# Production Work Order Implementation Plan (Phase-1 founding plan — historical)

> **Status: historical / Phase-1 founding plan.**
> This document records the original Phase-0/1 Work Order implementation (`PrWorkOrder*`, flattened snapshot, generic resources).
> It is **superseded in direction** by [`plans/work-order-plan.md`](work-order-plan.md) (the Production Work Order Enhancement Plan),
> which adds Milestone 0 (Product Definition readiness), the full hierarchy snapshot, Option A1 labour ownership, and the hardened
> Release/lifecycle contracts. This file remains as historical reference; it is marked formally archived once the enhancement plan
> is promoted to authoritative and its acceptance passes. Do not use it as the current target.

## 1. Purpose and interpretation

This plan converts the legacy Web Forms work-order study into an implementable plan for the current ERP solution.

The user request is:

- study the legacy ProductionPlan/work-order behavior;
- produce a workable implementation plan;
- make the new UI follow the existing project UI style.

The source study is not treated as an instruction to copy the old architecture. Its content is classified as follows:

- **Confirmed**: legacy behavior to preserve or test for compatibility.
- **Recommended/proposed**: target design proposal to adapt to this repository.
- **Unconfirmed**: a discovery or business decision gate; it must not be silently implemented as fact.

No direct port of Web Forms pages, page session state, `DataTable` state, inline SQL, or legacy status strings is planned.

Source study: `docs/production-workorder-analysis-and-implementation-plan.md`.

Review disposition:

- **Approved to proceed:** Phase 0 and Phase 1.
- **Mandatory hardening gate before Phase 2:** material reservation, operation quantity semantics, idempotent posting, transaction boundaries, Change Order controls, completion/closure predicates, WIP valuation, production costing, variance, and future GL integration must be explicit and testable.

## 2. Outcome

Deliver the standard production path as one coherent vertical slice:

```text
Create Draft
  -> Process current BOM/routing into a Work Order snapshot
  -> Save Draft
  -> Release
  -> Issue material / return material
  -> Record operation output and WIP
  -> Receive finished goods
  -> Complete and close
  -> Reverse through controlled documents when permitted
```

The first release should support manual Work Order creation from an effective Product Definition/BOM revision. Delivery Request, Sales Order, MRP, QA/MRB, subcontracting, mobile, and customer-specific variants should be integrated after the standard path is proven, but must be included in discovery and migration mapping.

## 3. Current repository baseline

The current solution is a .NET 10 Blazor Interactive Server application with:

- `ErpWeb.Model`: EF Core entities, configurations, repositories, and `AppDbContext`.
- `ErpWeb.Core`: business services, authorization, tenant scope, numbering, inventory, and period controls.
- `ErpWeb.UI`: DevExpress Blazor pages, shared data grids, layouts, theme, and responsive UI.
- `ErpWeb.Tests`: SQLite service tests and SQL Server concurrency/integration tests.

Existing foundations to reuse:

| Existing foundation | How Work Order uses it |
|---|---|
| `PrBomHdr` / `PrDefBOM` | Select the effective product/BOM revision and copy its values into an order snapshot. |
| `IPrProductDefService` | Load and validate Product Definition data. |
| `IBomExplosionService` | Reuse BOM explosion rules where the Work Order needs multi-level planning. |
| `AppDbContext` and EF configurations | Add production entities using the existing entity/configuration pattern. |
| `ITenantScopeContext` | Enforce company, branch, location, and user scope on every query and command. |
| `IAccessRightService` | Gate menu access and state-changing commands. |
| `IDocumentNumberingService` / running numbers | Allocate Work Order and inventory batch numbers inside the caller transaction. |
| `IIvInventoryPostingService` | Use the existing inventory ledger and balance/cost/history mechanisms. |
| `IvPeriodCloseGuard` | Block stock-driving posts and reversals in closed periods. |
| `RowVersion` conventions | Detect concurrent edits and concurrent posting. |

There are currently no new-side Work Order, `PrSch*`, daily-production, WIP, or finished-goods production entities/pages. This is a new production domain built on existing BOM and inventory foundations.

## 4. Architecture decisions

### 4.1 Separate master data from execution

Product Definition remains reusable master data. A Work Order copies the selected revision at creation/recalculation time and stores a complete snapshot:

- product and output UOM;
- BOM revision and base quantity;
- material lines;
- operations and process sequence;
- work centres, machines, and labour/resources;
- planned quantities and dates;
- scheduling direction and calendar inputs;
- source demand references.

After release, the snapshot is read-only. A change must create an explicit change-order/revision record with before/after values and an audit event.

### 4.2 Do not duplicate the inventory ledger

Material issue, material return, and finished-goods receipt must link to the existing inventory transaction model rather than creating a second stock system.

Use `IvTrxBatch`, `IvTrxBatchDetail`, `IvTrxHistory`, `IvBalLoc`, lot records, and the existing posting repository/service wherever the existing contract is suitable. Add a production source/link record if the existing `RefNo`/remarks fields are insufficient for durable Work Order traceability.

The current public inventory posting dispatcher does not expose the full production `IP`/`FG` workflow. The production posting service must either:

1. extend the inventory posting contract with explicit production stock-out/stock-in operations; or
2. add a production adapter that calls the existing in-transaction posting cores while preserving one database transaction.

This decision must be made before Phase 2 posting work.

### 4.3 Use domain commands, not UI callbacks

The UI may provide `PROCESS`, `RECALCULATE`, `SAVE`, `RELEASE`, and posting buttons, but all values must be reloaded and recalculated on the server. Browser payloads, hidden fields, grid state, and component memory are never authoritative.

Recommended service boundaries:

```text
IProductionWorkOrderService
  Query/search/detail
  CreateDraft
  ProcessPreview
  SaveDraft
  Release
  CreateChangeOrder
  CancelDraft

IProductionExecutionService
  Create/save/post issue
  Create/save/post return
  Record operation completion
  Transfer WIP
  Receive finished goods
  Reverse posted execution

IProductionInquiryService
  Progress
  Shortage
  WIP
  Traceability
  Cost/variance projections
```

### 4.4 Material reservation and allocation contract

Shortage checking alone is insufficient because two Work Orders can see the same physical stock as available. The production domain therefore needs an explicit material commitment lifecycle:

```text
Required
  -> Available
  -> Reserved / Allocated
  -> Picked
  -> Issued
  -> Returned
  -> Consumed
```

`ProductionMaterialReservation` is a commitment record, not a second inventory balance. Inventory remains authoritative for physical on-hand quantity.

Phase 0 must decide and document:

- whether reservation is optional or mandatory before release/issue;
- whether it is soft or hard;
- whether it is made at item, warehouse, location, lot, or serial level;
- reservation priority when multiple Work Orders compete for stock;
- whether expiry/reallocation is allowed;
- whether picking is a distinct operational state;
- when reservations are released on return, Change Order, completion, close, cancellation, or reversal;
- how approved negative stock or over-issue affects reservation availability.

At minimum, availability for a Work Order must distinguish physical on-hand from stock already hard-reserved by other documents. Reservation creation and issue posting must revalidate stock under database locks; a UI availability value is informational only.

### 4.5 Idempotent posting and transaction boundaries

Every stock- or WIP-affecting command must carry a stable `PostingRequestId`/idempotency key. The key is unique within company/branch and command type. Retrying the same request must return the original outcome and must not create another inventory batch, WIP movement, cost transaction, or audit event.

`ProductionPostingLink` should retain:

- request/idempotency key;
- production document and line identity;
- Work Order and operation identity;
- inventory batch number and posting operation ID;
- original/reversal relationship;
- status, result, actor, and timestamps.

The transaction boundary for a production post is:

```text
Begin database transaction
  -> lock/check idempotency record
  -> lock Work Order and production document
  -> validate tenant, permission, row version, state, and period
  -> validate reservation, stock/WIP availability, and quantity equations
  -> create or reuse the inventory batch
  -> call inventory posting on the same DbContext/transaction
  -> append WIP, execution, cost, and audit facts
  -> update rebuildable projections and Work Order status
  -> persist idempotency result
Commit
```

Any failure rolls back the whole unit. Reversal uses a separate idempotency key, references the original posting, and follows the same atomicity rule. Locks must be acquired in a documented deterministic order to reduce deadlocks.

### 4.6 Derived projections and traceability

Posted documents and ledger movements are authoritative. Header totals, progress percentages, WIP balances, and inquiry rows are rebuildable projections.

Required traceability must work in both directions:

```text
Raw-material lot
  -> material issue
  -> Work Order / operation
  -> WIP movement
  -> FG receipt
  -> finished-goods lot

Finished-goods lot
  -> FG receipt
  -> Work Order / operations
  -> material issues
  -> raw-material lots
```

Cached progress values are allowed for performance only when they can be reconciled and rebuilt from posted, non-reversed source facts.

## 5. Target Work Order lifecycle

Use one Work Order state machine:

```text
Draft -> Released -> InProgress -> Completed -> Closed
  |         |            |
  v         v            v
Cancelled  Change       Reversal/change approval
            Order
```

Rules:

| State/action | Rule |
|---|---|
| Draft | Editable, recalculable, and cancellable. No stock or WIP posting is allowed. |
| Released | Product, BOM revision, output UOM, snapshot lines, and released schedule are frozen. |
| InProgress | Entered by the first posted issue, WIP, or production movement. |
| Completed | Planned output is accounted for by good, scrap, reject, hold, or an approved variance disposition. |
| Closed | Required inventory, WIP, costing, variance, and demand conditions are settled. |
| Cancelled | Only allowed when no posted descendant documents exist. |
| Reversal | Uses an explicit reversal document and retains the original document link. Direct deletion is not allowed after posting. |

Inventory document statuses remain separate: `NEW`, `POSTED`, and controlled rollback/reversal state.

### 5.1 Change Order control matrix

The fundamental rule is: **a Change Order changes future requirements; it never rewrites posted historical facts.**

| Change after release | Allowed | Required control |
|---|---|---|
| Planned completion date | Yes | Change Order, reason, reschedule, and audit. |
| Planned quantity | Controlled | Change Order, approval where configured, and recalculation of only future/open requirements. |
| BOM component quantity | Controlled | Approved Change Order with material delta. |
| Add component | Controlled | Approved Change Order and new future requirement line. |
| Remove component | Controlled | Approved Change Order; issued/consumed history remains untouched. |
| Substitute component | Controlled | Substitution approval, reason, effective quantity, and traceability to original component. |
| Routing sequence | Restricted | Change Order; reject when completed/transferred operations make the new sequence invalid. |
| Work centre/machine | Controlled | Change Order and reschedule; completed resource history remains unchanged. |
| Already-issued material | Never rewrite | Use material return, additional issue, consumption adjustment, or approved variance document. |
| Posted operation output/WIP | Never rewrite | Use an explicit reversal/correction document. |
| Posted finished goods | Never rewrite | Use FG reversal/correction linked to the original posting. |

A Change Order records the source snapshot revision, the proposed replacement revision, line-level deltas, reason, requester, approver, effective point, and status. Only an approved Change Order may publish a new active Work Order snapshot revision.

### 5.2 Predicate-based completion and closure

Completion and closure must be evaluated by named server-side predicates that return both a result and machine-readable refusal reasons. A status string alone is never proof that the conditions are satisfied.

Initial predicate shape:

```text
CanComplete =
    status is Released or InProgress
    AND planned output is fully accounted for
    AND all mandatory operation execution is resolved
    AND no blocking hold/rework disposition remains
    AND no unposted completion document remains

CanClose =
    status is Completed
    AND open material requirement is zero or has approved variance
    AND open WIP quantity is zero
    AND hold quantity is zero
    AND reject/scrap disposition is resolved
    AND costing state is complete
    AND mandatory production variances are approved
    AND no NEW/pending execution document exists
    AND no pending Change Order or material reservation remains
    AND demand allocation is reconciled
```

Phase 0 must confirm the exact predicates. `Complete` and `Close` update status only after re-evaluating them inside the same transaction that records the transition and audit event.

## 6. Proposed domain model

Create a production namespace/folder rather than putting all execution entities into the existing BOM master namespace.

Suggested entities:

```text
ProductionWorkOrder
  ├─ ProductionWorkOrderOperation
  │    └─ ProductionWorkOrderResource
  ├─ ProductionWorkOrderMaterial
  │    └─ ProductionMaterialReservation
  ├─ ProductionWorkOrderDemandAllocation
  ├─ ProductionExecutionDocument
  │    ├─ ProductionExecutionLine
  │    └─ ProductionOperationCompletion
  ├─ ProductionWipMovement
  ├─ ProductionFinishedGoodsReceipt
  ├─ ProductionPostingLink
  ├─ ProductionCostTransaction
  ├─ ProductionVariance
  ├─ ProductionChangeOrder
  │    └─ ProductionChangeOrderLine
  └─ ProductionAuditEvent
```

These are required domain concepts; implementation may combine closely related concepts into one physical header/line table where that keeps invariants clearer.

Every production-owned row should include the repository's applicable company/branch/location and audit fields, plus a concurrency token where the row can be edited.

### 6.1 Work Order header

Minimum fields:

- internal identity and Work Order number;
- company, branch, and location;
- product code and product description snapshot;
- BOM header/revision reference and snapshot hash;
- planned quantity, output UOM, and BOM base quantity/UOM;
- planned start and completion dates;
- forward/backward scheduling direction;
- status and row version;
- delivery/request/Sales Order/MRP source type and source reference;
- planned, good, scrap, reject, hold, and remaining quantities;
- created/updated audit fields.

### 6.2 Operation snapshot

Store the selected work centre, process sequence, final-process flag, planned dates, planned quantity, machine/resource assignments, setup/operation loss inputs, and scheduling values used to produce the dates. The current master data may change later; the Work Order snapshot must remain historically accurate.

### 6.3 Material snapshot

Store:

- component code and description;
- source BOM revision/line;
- required quantity and required UOM;
- base quantity/UOM;
- source warehouse/location defaults;
- scrap/tolerance inputs and calculated quantities;
- issued, returned, consumed, and variance projections;
- substitute/approval fields when that policy is enabled.

Do not overwrite required quantity after release. Actual movements are ledger/document facts.

### 6.4 Material commitment quantities

For each Work Order material line, expose distinct quantities rather than one overloaded balance:

- `RequiredQty`;
- `AvailableQty` at the requested stock scope;
- `ReservedQty`;
- `PickedQty` where picking is enabled;
- `IssuedQty`;
- `ReturnedQty`;
- `ConsumedQty` when consumption is distinct from issue;
- `OpenRequirementQty`;
- `VarianceQty` and approval reference.

Reservation, issue, return, and consumption totals are projections from their source documents. They must not be freely editable counters.

### 6.5 Operation execution quantity contract

Each operation must distinguish:

- `InputQty`;
- `ProcessedQty`;
- `GoodQty`;
- `ScrapQty`;
- `RejectQty`;
- `HoldQty`;
- `ReworkQty`;
- `TransferredQty`;
- `RemainingQty`.

Each posted execution line represents one mutually exclusive disposition so totals cannot double-count the same quantity. The baseline reconciliation is:

```text
ProcessedQty = GoodQty + ScrapQty + RejectQty + HoldQty + ReworkQty
RemainingQty = max(InputQty - ProcessedQty, 0)

AvailableForNextOperation =
    GoodQty
    + ReleasedHoldQty
    + AcceptedReworkOutputQty
    - TransferredQty
```

Phase 0 must decide whether reject, released hold, and rework re-enter the same operation, a dedicated rework operation, or a disposition document. Only eligible output may transfer to the next operation. The posting service must validate the equation and WIP availability while holding the relevant rows.

### 6.6 Production costing, WIP valuation, and future accounting

The domain must capture enough facts to derive accounting even if GL posting is delivered later. Required cost concepts are:

- material;
- labour;
- machine;
- overhead;
- subcontract;
- WIP;
- finished goods;
- scrap;
- production variance.

Conceptual value flow:

```text
Material issue           Raw Material Inventory -> WIP
Material return          WIP -> Raw Material Inventory
Labour/machine/overhead  Absorption/Clearing -> WIP
Subcontract receipt      Subcontract/Payable -> WIP
FG receipt               WIP -> Finished Goods Inventory
Scrap                    WIP -> Scrap/Production Variance
Close variance           WIP -> Production Variance
```

The debit/credit accounts, costing method, and period behavior remain configurable decisions, but production events must carry the dimensions, quantities, rates, amounts, source references, and reversal links needed to create those entries later.

Rules:

- inventory issue/receipt cost comes from the existing inventory costing authority;
- production stores references to inventory cost/history instead of inventing a competing item cost;
- `ProductionCostTransaction` is append-only and links to the originating material/resource/WIP/FG event;
- reversal creates compensating cost facts; it does not edit posted cost history;
- WIP value must reconcile as opening WIP + inputs - FG transfer - scrap/variance = closing WIP;
- close is blocked while required costing or variance approval is pending.

Before Phase 2, define the event contract. Before Phase 3 exit, define WIP valuation and cost conservation tests. Before Phase 4 close, approve the production variance and future GL mapping contract.

### 6.7 Derived progress measures

Progress comes from posted, non-reversed facts:

```text
MaterialProgress =
    sum(min(NetIssuedQty, RequiredQty)) / sum(RequiredQty)

OperationProgress =
    AccountedOperationQty / PlannedOperationQty

ProductionProgress =
    AccountedOutputQty / PlannedOutputQty
```

The service must define zero-denominator behavior and cap display percentages where excess issue/production would otherwise inflate progress. Approved over-production and approved variances remain visible separately.

## 7. Calculation contract

Use `decimal`, never `float`/`double`.

The confirmed legacy baseline includes:

```text
CentreQty = WorkOrderQty × CentreStdPackSize / HeaderStdBatchSize

BomRequirement = round(
    BomStdQty / CentreStdPackSize × CentreQty,
    4)

RemainingIssue = max(RequiredQty - PostedIssueQty + PostedReturnQty, 0)
```

The new calculation contract must make the following explicit:

```text
RequiredBaseQty = RoundForItem(
    PlannedOutputBaseQty
    × ComponentQtyPerBomOutputQty
    × (1 + ScrapPct)
    / BomOutputBaseQty)

NetIssuedQty = PostedIssueQty - PostedReturnQty
OpenRequirementQty = max(RequiredBaseQty - NetIssuedQty, 0)

GoodQty = ProducedQty - RejectedQty
OpenProductionQty = PlannedQty - GoodQty - ApprovedVarianceQty
```

The exact treatment of setup loss, operation loss, tolerance, scrap, reject, yield, catch weight, UOM conversion, and rounding scale must be decided in Phase 0 and encoded in calculation tests.

## 8. Implementation phases

### Phase 0 — Discovery and decision closure

Deliverables:

- live legacy schema inspection;
- trace of all Work Order, daily-production, WIP, FG, QA/MRB, transfer, subcontract, and alternate/mobile paths;
- legacy view/report dependency list;
- status mapping table;
- calculation examples approved by the business owner;
- inventory/costing integration decision;
- signed decision log for every currently `UNCONFIRMED` item.

Required decisions:

- UOM conversion and rounding;
- scrap/reject/yield/tolerance semantics;
- material commitment lifecycle, reservation priority, and shortage/negative-stock policy;
- material-return and actual-consumption rules;
- operation input/processed/good/scrap/reject/hold/rework/transfer quantity semantics;
- posting idempotency key, lock order, and atomic transaction boundary;
- detailed Change Order matrix and approval policy;
- explicit `CanComplete` and `CanClose` predicates;
- costing method, production cost events, WIP valuation, variance, and future GL effect;
- scheduling direction, shift calendar, downtime, and stock-process behavior;
- scope of DR/SO/MRP generation;
- QA/MRB, subcontracting, mobile, JONG, and customer-specific variants.

Exit gate: no unresolved decision may affect the first vertical slice's quantity, inventory, status, or close behavior.

### Phase 1 — Foundation and snapshot

Deliverables:

- production entities and EF configurations;
- SQL creation/alter scripts;
- Work Order numbering;
- menu and permission seeds;
- calculation service;
- Work Order query/detail DTOs;
- manual Draft creation;
- server-side Process preview;
- snapshot save/load;
- Draft cancellation;
- Release command;
- Change Order header/line contract and transition rules;
- completion/closure predicate result contracts, even if execution facts are not implemented yet;
- idempotency and posting-link schema reserved for later phases;
- audit and concurrency handling.

Exit gate: an approved BOM can produce a Draft, persist the complete snapshot, reload it, and release it without any inventory side effect.

### Mandatory execution-readiness gate — before Phase 2

Phase 2 must not start until the following are documented with examples, represented in the domain model, and covered by contract tests:

1. Material reservation/allocation lifecycle and competition policy.
2. Operation quantity names, equations, and next-operation eligibility.
3. Stable idempotency key and duplicate-request behavior.
4. One-transaction boundary and deterministic lock order for every posting/reversal.
5. Detailed Change Order permissions and future-only effect.
6. Predicate-based Complete and Close rules with refusal reasons.
7. WIP quantity and valuation model.
8. Material/labour/machine/overhead/subcontract costing event model.
9. Scrap and production-variance disposition/approval model.
10. Future GL derivation contract, even if GL posting remains out of scope.

Gate evidence must include at least one reviewed end-to-end example from material reservation through FG receipt and reversal, with expected quantities, costs, inventory movements, WIP balance, and statuses.

### Phase 2 — Material execution

Deliverables:

- material availability/shortage view;
- material reservation/allocation document or service according to the approved policy;
- reservation priority, release, expiry, and reallocation behavior;
- partial issue document;
- lot/location selection;
- material return document;
- lot/serial traceability;
- over-issue/substitution approval rules;
- idempotent stock posting and rollback/reversal integration;
- atomic posting link between production document, inventory batch, WIP/cost facts, and Work Order;
- period-close validation;
- rebuildable Work Order material-progress projection from posted documents.

Exit gate: reservation, issue, return, and consumed/open quantities reconcile to the material snapshot and inventory ledger; concurrent competition, duplicate retry, failure rollback, and reversal produce no duplicate or partial facts.

### Phase 3 — WIP, production, and FG

Deliverables:

- operation completion document;
- explicit input/processed/good/scrap/reject/hold/rework/transferred/remaining quantities;
- enforced operation quantity reconciliation equation;
- predecessor-operation validation;
- WIP lot movement and balance projection;
- partial production;
- finished-goods receipt;
- WIP quantity protection;
- FG reversal;
- idempotent posting/reversal across WIP and FG;
- production cost transactions for material, labour, machine, overhead, subcontract, WIP, FG, and scrap as applicable;
- final-process and derived Work Order progress rules.

Exit gate: a two-operation Work Order can reconcile every input disposition, produce partial WIP, transfer only eligible output, receive FG in multiple lots, reject over-consumption, reverse atomically, and satisfy quantity and WIP cost-conservation tests.

### Phase 4 — Closure, inquiry, and reporting

Deliverables:

- executable `CanComplete`/`CanClose` predicates with refusal reason codes;
- open WIP and variance checks;
- cost/WIP/variance read model and approval state;
- future GL mapping/export contract where direct GL posting is not yet in scope;
- Work Order backlog inquiry;
- shortage/reservation inquiry;
- planned-versus-actual output;
- WIP ageing;
- bidirectional raw-material-lot-to-FG-lot traceability;
- audit and document relationship inquiry;
- report/print parity requirements.

### Phase 5 — Migration and legacy variants

Deliverables:

- staged legacy import;
- Work Order snapshot migration;
- execution/inventory/WIP reconciliation;
- exception queue;
- DR-generated Work Orders;
- MRP conversion;
- QA/MRB and WIP transfer integration;
- subcontracting and alternate/mobile paths where approved;
- parallel-run comparison and sign-off.

## 9. UI implementation plan

The UI must use the existing project style, not a recreated Web Forms layout.

### 9.1 Work Order list

Route:

```text
/planning/work-orders
```

Use:

- `PageBase` and `MenuAuthorize`;
- `iv-page`, `iv-hero`, `iv-card`, `iv-toast`, and `iv-chip` styles;
- `CommonDataGridEx` with a server-side custom data source;
- the existing search/filter popup pattern;
- persistent grid layout;
- responsive compact rows for small screens;
- `AppNavigation` for navigation;
- permission-aware toolbar/action buttons.

List columns:

- Work Order number;
- product and description;
- status;
- BOM revision;
- planned/good/remaining quantity;
- start and completion dates;
- demand/source reference;
- issue/WIP/FG progress;
- created/updated audit information.

### 9.2 Work Order detail

Routes:

```text
/planning/work-orders/new
/planning/work-orders/edit/{WorkOrderNo}
/planning/work-orders/view/{WorkOrderNo}
```

Header:

- `iv-hero` with production icon;
- Work Order number, product, revision, and status chips;
- immediately visible operational KPIs;
- action buttons enabled by status and permission.

The Overview must show the production state without requiring the user to open every tab:

- Work Order quantity;
- good quantity;
- reject and scrap quantity;
- remaining quantity;
- material shortage indicator;
- material issue percentage;
- production percentage;
- current WIP quantity;
- finished-goods received quantity;
- status and blocking reasons for the next lifecycle action.

Sections/tabs:

1. Overview and demand references.
2. Operations and schedule.
3. Materials and shortage status.
4. Issues and returns.
5. WIP and completions.
6. Finished goods.
7. Costs and variances.
8. Audit and traceability.

Use `DxFormLayout`, `DxGrid`, `DxTabs`, `DxPopup`, existing stock/warehouse/location lookups, and page-specific CSS variables derived from the existing theme variables. Keep the interface dense and operational, with no new color system.

Editing rules:

- Draft: editable and recalculable.
- Released: read-only except through Change Order.
- Posted sections: always read-only.
- Destructive actions: confirmation popup with reason where required.
- Concurrency conflict: show the existing reload/latest-version style flow.

## 10. Database and migration rules

Use explicit EF configurations and SQL deployment scripts. Do not bulk-copy legacy `PrSch*` tables as if they were a clean target schema.

Migration order:

```text
Master data and BOM revisions
  -> Work Order headers and snapshots
  -> Issue/return documents
  -> Production and WIP history
  -> FG documents and inventory links
  -> Reconciled projections and inquiry views
```

Preserve:

- original Work Order number and revision;
- legacy status;
- dates and users;
- source DR/SO/MRP references;
- lot/location identifiers;
- batch/document references;
- posted/reversed relationships;
- migration exception reason.

Historical required quantities must come from the migrated snapshot, not from the current BOM.

## 11. Testing strategy

### Unit tests

- centre quantity and BOM ratio calculations;
- `1:5`, `0.2`, and fractional quantities;
- UOM conversion and item rounding;
- scrap/reject/tolerance decisions;
- operation processed-quantity reconciliation and mutually exclusive dispositions;
- next-operation eligible quantity and transfer limits;
- derived material/operation/production progress formulas;
- Complete/Close predicates and refusal reason codes;
- forward/backward scheduling;
- state-transition rules;
- open requirement and open production calculations.

### Service/integration tests

- company/branch isolation;
- Draft save and release;
- row-version conflict;
- competing Work Orders reserving the same stock;
- reservation release on Change Order/cancel/close/reversal;
- partial issue and material return;
- lot/serial movement;
- insufficient stock;
- sequential and concurrent duplicate post retry with the same idempotency key;
- transaction failure after stock mutation leaves no inventory, WIP, cost, status, or posting-link partial state;
- WIP predecessor validation;
- partial completion and FG receipt;
- WIP over-consumption;
- FG reversal;
- closed-period refusal;
- Change Order changes only future requirements and never mutates posted facts;
- Complete/Close refusal and successful transition paths;
- inventory/WIP quantity reconciliation;
- WIP cost conservation and reversal;
- raw-material-to-FG and FG-to-raw-material traceability.

### UI verification

- desktop and compact mobile layouts;
- light and dark themes;
- grid layout persistence;
- responsive list rows;
- permission-disabled commands;
- Draft/Released/Posted read-only behavior;
- validation displayed at both headline and line level;
- navigation guard for unsaved changes.

## 12. First implementation backlog

The recommended first implementation sequence is:

1. Complete Phase 0 decision log.
2. Freeze production terminology, lifecycle states, and refusal reason codes.
3. Finalize BOM/UOM/rounding/scrap/yield calculation examples and tests.
4. Finalize the Work Order snapshot schema.
5. Add production entities/configurations, numbering, menus, and permissions.
6. Implement Draft -> Process Preview -> Save -> Release, UI, concurrency, and audit.
7. Finalize reservation/allocation policy and stock-competition tests.
8. Finalize the idempotent inventory production-posting adapter and atomic transaction boundaries.
9. Implement reservation, material issue, and material return.
10. Finalize and implement the operation execution quantity contract.
11. Implement WIP movement and rebuildable WIP balance projection.
12. Implement partial production and finished-goods receipt with idempotent reversal.
13. Implement production cost events and WIP valuation/conservation projections.
14. Implement `CanComplete` and `CanClose` predicates and production variance approval.
15. Implement inquiry, bidirectional traceability, cost/variance, and audit reporting.
16. Reconcile and sign off the standard production path end to end.
17. Only then migrate legacy Work Orders and approved special variants.

## 13. Definition of done for the first release

The first release is ready when:

- a user can create a manual Work Order from an active BOM revision;
- the saved order contains a complete, auditable snapshot;
- released orders cannot be silently overwritten by current master data;
- material issue and return reconcile to the inventory ledger;
- reservation prevents two Work Orders from consuming the same committed stock according to the approved policy;
- every operation execution satisfies the approved quantity equation;
- production and FG receipt reconcile to WIP and inventory;
- duplicate posting is safe through a persisted idempotency key;
- posting is atomic across production, inventory, WIP, cost, status, and audit facts;
- reversals preserve the audit chain;
- closed periods block prohibited movement;
- Work Order progress is calculated from posted facts;
- Change Orders affect only future requirements and preserve posted history;
- completion and closure use explicit predicates and show refusal reasons;
- WIP value and production cost events reconcile under the approved costing contract;
- lot traceability works from raw material to FG and from FG back to raw material;
- permissions and concurrency are enforced server-side;
- the list/detail screens match the existing project UI on desktop and mobile;
- automated tests cover the quantity, status, posting, reversal, and reconciliation rules.

## 14. Main risks

- Treating unconfirmed legacy behavior as a business rule.
- Introducing a duplicate inventory or costing engine.
- Reporting stock as available without accounting for reservations held by other Work Orders.
- Ambiguous operation quantities allowing the same output to be double-counted or transferred twice.
- Retried posting creating duplicate inventory, WIP, cost, or audit facts.
- A transaction failure leaving production status inconsistent with inventory or WIP.
- Allowing released snapshot lines to change without a change order.
- Designing execution events without the dimensions required for WIP valuation and future accounting.
- Implementing only the main Work Order page and missing daily/WIP/FG/QA variants.
- Assuming `PrSchDailyProcess.Completed` is authoritative instead of deriving progress from posted facts.
- Copying legacy floating-point and inline-SQL behavior into the new system.
- Migrating projections before inventory/WIP ledgers are reconciled.
- Extending the UI before the posting and reversal transaction boundaries are finalized.

## 15. Implementation checkpoint — 2026-09-28

The first Phase 1 vertical slice is implemented. It establishes the Work Order aggregate and UI without crossing the mandatory Phase 2 posting gate.

Implemented:

- production entities and EF configurations for Work Orders, immutable material/operation snapshots, audit events, Change Order contracts, and posting idempotency links;
- tenant-scoped Work Order numbering, menu registration, and permission seeds;
- server-authoritative Process Preview and Draft save/load using the active Product Definition/BOM revision;
- deterministic snapshot hashing, optimistic concurrency, release, Draft cancellation with reason, and audit history;
- line-level source BOM revision/base-quantity lineage, including nested Phantom expansion, with deletion protection for every retained source revision;
- explicit quantity calculations and lifecycle/predicate contracts;
- responsive Work Order list and entry/detail pages using the project's DevExpress and `iv-*` UI patterns;
- automated tests for calculations, permissions, tenant boundaries, snapshot immutability, concurrency, release, cancellation, and the no-inventory-side-effect release rule.

Intentionally deferred behind the hardening gate:

- reservations and allocation policy;
- material issue/return posting;
- operation execution and WIP movement;
- finished-goods receipt, costing, reversal, and closure;
- routing row generation until an authoritative routing source is approved.

Deployment order for this slice:

1. Apply `scripts/create-prdefbom.sql` if the Product Definition/BOM schema is not already installed.
2. Apply `scripts/create-production-workorder.sql`.
3. Apply `scripts/init-planning-workorder-menu.sql` and run the normal menu synchronization/restart process.

Verification commands:

```powershell
dotnet build ErpWeb.Model\ErpWeb.Model.csproj --no-restore
dotnet build ErpWeb.Core\ErpWeb.Core.csproj --no-restore
dotnet build ErpWeb.UI\ErpWeb.UI.csproj --no-restore
dotnet test ErpWeb.Tests\ErpWeb.Tests.csproj --no-restore --filter "Category=Planning"
```

Current verification result: all 63 Planning-category tests pass; Model, Core, and UI compile successfully.

