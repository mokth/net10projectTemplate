# ERP Costing Enhancement Plan — Inventory, Procurement & Sales
## Repository-grounded implementation plan for AI Coding Agent

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `production`  
**Baseline commit reviewed:** `a58c508d8efc3de0bd8b34d7c7c9a3f0d3deecdd`  
**Scope:** Inventory + Procurement + Sales costing only, while preserving existing Production costing integrations and stock-ledger contracts.  
**Plan status target:** **10/10 — implementation-ready after the acceptance gates in this document are satisfied.**

---

# 1. Purpose

Enhance the current ERP costing implementation so that the same authoritative financial inventory ledger can reliably support:

1. **Moving Weighted Average Cost** — keep and harden the current perpetual moving-average implementation.
2. **FIFO financial costing** — add real financial FIFO layers; do not confuse the existing physical FIFO shipment allocation with costing.
3. **Standard Costing** — add effective-dated standard cost revisions, standard-cost inventory valuation, purchase price variance, and controlled revaluation.
4. **Reliable Procurement actual-cost flow** — GR provisional valuation must be reconciled when Purchase Invoice actual cost arrives.
5. **Reliable Sales COGS and returns** — no duplicate COGS, no broad document/item fallback, and sales return must use exact original cost lineage.
6. **Authoritative costing reports** — Inventory Valuation, COGS/Gross Profit, Purchase Cost/PPV must read the valuation ledger rather than mutable `IvBalLoc.UnitPrice` / `IvStockMaster.PurchasePrice`.
7. **Costing-safe posting validations** — prevent missing FX, unapproved zero/manual costs, invalid count/adjustment costs, and closed/frozen-period writes from corrupting valuation.
8. **The two SHOULD enhancements identified in review**:
   - persistent stock freeze for stock count / protected stock scopes;
   - landed cost allocation after PI variance is stable.

This is a **delta/enhancement plan against the current production branch**, not a request to rebuild the stock ledger from scratch.

---

# 2. Existing foundation that MUST be preserved

The current branch already contains a strong V2 stock/valuation foundation. The coding agent must reuse it.

## 2.1 Existing financial truth

Current files:

- `ErpWeb.Core/StockLedger/StockPostingCoordinator.cs`
- `ErpWeb.Core/StockLedger/StockPostingContext.cs`
- `ErpWeb.Core/StockLedger/InventoryValuationService.cs`
- `ErpWeb.Core/StockLedger/StockValuationQueryService.cs`
- `ErpWeb.Core/StockLedger/StockValuationSnapshotBuilder.cs`
- `ErpWeb.Core/StockLedger/InventoryStockPostingCommandFactory.cs`
- `ErpWeb.Model/Entities/StockLedger/StockPosting.cs`
- `ErpWeb.Model/Entities/StockLedger/StockValuationFact.cs`
- `ErpWeb.Model/Entities/StockLedger/StockValuationPeriodSnapshot.cs`
- `ErpWeb.Model/Configurations/StockLedger/*`
- `scripts/create-stock-valuation-ledger.sql`

The architecture already provides:

```text
Source document
    -> IvTrxBatch / IvTrxBatchDetail
    -> StockPostingCoordinator
    -> physical inventory mutation
    -> IvTrxHistory
    -> InventoryValuationService
    -> StockValuationFact
    -> StockCostState
    -> financial period snapshot
```

This stays the authoritative path.

## 2.2 Existing financial invariants to preserve

Do not weaken these current behaviours:

- `StockValuationFact` is append-only / immutable.
- reversals use `ReversesValuationFactId`; do not recalculate current cost for rollback.
- one posting-line/split identity is unique.
- negative stock cost state is blocked.
- zero quantity with residual value is blocked at close.
- `StockCostState` reconciles to valuation facts.
- period close blocks unresolved `UNVALUED` facts.
- physical quantity snapshot is reconciled to valued quantity.
- posting is tenant/branch scoped.
- source-document/revision/posting-role uniqueness remains.
- request fingerprint / idempotency remains.
- backdated valued movement protection remains.
- no-future-stock validation remains.
- DO / Invoice `LinkDo` physical ownership remains:
  - DO owns stock-out when the DO shipped;
  - `LinkDo == true` invoice does **not** stock-out again;
  - direct `LinkDo == false` invoice owns its own stock-out.

## 2.3 Do NOT use these as historical costing authority

These remain operational/display data only:

- `IvBalLoc.UnitPrice`
- `IvBalLoc.Cost`
- `IvStockMaster.PurchasePrice`

They may be maintained for UI compatibility, but historical costing reports must not be reconstructed from them.

---

# 3. Findings this plan MUST resolve

## MUST enhancements

1. Add a real costing-method policy and strategy seam.
2. Complete Purchase Invoice actual-cost / purchase-price-variance flow.
3. Remove foreign-currency PI fallback to exchange rate `1`.
4. Replace the current estimated stock-value report as costing authority.
5. Fix Sales Return exact original-cost lineage, especially `Invoice -> LinkDo -> DO`.
6. Remove unsafe broad Invoice COGS fallback when exact line identity is missing.
7. Prevent arbitrary positive Stock Adjustment cost from changing valuation.
8. Correct Stock Count increase valuation policy.
9. Enforce Misc Receipt cost evidence / approved zero cost.
10. Implement true financial FIFO.
11. Implement Standard Costing.

## SHOULD enhancements

12. Replace `NoActiveStockFreezeGuard` with persistent stock freeze.
13. Add landed cost through the same immutable cost-adjustment pipeline.

Everything else is out of scope unless required to satisfy one of the above.

---

# 4. Scope boundaries and non-goals

Do **not** turn this into SAP product costing.

Not in this plan:

- LIFO.
- simultaneous mixed financial methods by item within one branch.
- activity-based costing.
- advanced overhead absorption.
- intercompany transfer pricing.
- full manufacturing standard-cost variance accounting.
- retrospective rewrite of sealed historical valuation facts.
- replacing `IvTrxBatch`, `IvTrxHistory`, or `IvBalLoc`.
- a second stock-posting coordinator.
- a second COGS engine.
- using shipment FIFO as financial FIFO.
- rewriting production WIP/FG architecture unless a regression fix is required because of the shared valuation strategy seam.

Production must continue to pass its existing stock-ledger tests.

---

# 5. Costing policy decision

## 5.1 One active financial method per company + branch

For this implementation phase, use exactly one active financial costing method per:

```text
CompanyCode + BranchCode
```

Allowed methods:

```text
MOVING_AVERAGE
FIFO
STANDARD
```

Do **not** introduce per-item mixed cost methods in this phase.

Why:

- current `StockCostState` pool is company + branch + item;
- period close groups facts by item/method;
- mixed item methods complicate stock close, cutover, audit, and report reconciliation;
- most SME companies select one inventory costing policy per accounting entity/branch.

## 5.2 Effective-dated policy

Create a new entity/table:

### `StockCostPolicyRevision`

Suggested columns:

```text
Id                       bigint PK
CompanyCode              nvarchar(5)
BranchCode               nvarchar(5)
CostMethod               nvarchar(30)
EffectiveFrom            date
EffectiveTo              date NULL
Status                    nvarchar(20)   -- ACTIVE / SUPERSEDED
Reason                    nvarchar(250) NULL
ApprovedBy                nvarchar(100)
ApprovedAtUtc             datetime2(7)
CreatedAtUtc              datetime2(7)
CreatedBy                 nvarchar(100)
RowVersion                rowversion
```

Constraints:

- `CostMethod IN ('MOVING_AVERAGE','FIFO','STANDARD')`
- only one policy may cover a business date for a company/branch;
- no overlapping effective ranges;
- no retroactive policy edit after sealed valuation facts exist in the affected period.

Create:

- `StockCostPolicyRevision.cs`
- `StockCostPolicyRevisionConfiguration.cs`
- `IStockCostMethodResolver`
- `StockCostMethodResolver`

`IStockCostMethodResolver.ResolveAsync(db, company, branch, effectiveAt)` must fail closed if no policy exists.

## 5.3 Migration default

For every existing branch that already has V2 costing:

- seed `MOVING_AVERAGE`;
- `EffectiveFrom` = its V2 ledger epoch/cutover start;
- do not alter existing `StockValuationFact.CostMethod`.

This makes the current system behaviour explicit rather than silently hardcoded.

---

# 6. Refactor costing into method strategies without replacing the ledger

Current:

`ErpWeb.Core/StockLedger/InventoryValuationService.cs`

currently contains moving-average logic directly.

Refactor it into an orchestration service plus method strategies.

Create:

```text
ErpWeb.Core/StockLedger/Costing/
    IInventoryCostingStrategy.cs
    MovingAverageCostingStrategy.cs
    FifoCostingStrategy.cs
    StandardCostingStrategy.cs
    InventoryCostingStrategyResolver.cs
```

Suggested contract:

```csharp
public interface IInventoryCostingStrategy
{
    string CostMethod { get; }

    Task<IReadOnlyList<StockValuationFact>> ValuePendingAsync(
        StockPostingContext context,
        IReadOnlyList<IvTrxHistory> pendingHistory,
        CancellationToken cancellationToken);
}
```

`InventoryValuationService` remains the registered `IInventoryValuationService` and performs:

1. load policy for `context.Posting.EffectiveAt`;
2. load pending history/evidence exactly as today;
3. select one costing strategy;
4. execute it inside the existing coordinator transaction;
5. save valuation facts;
6. update cost state / FIFO layers / standard state;
7. never seal itself; sealing remains `StockPostingCoordinator` responsibility.

### Moving Average strategy

Move the current proven WAC logic into `MovingAverageCostingStrategy` with behavioural parity.

Do not change the mathematical results while refactoring.

### `StockCostMethods`

Extend:

```csharp
public const string MovingAverage = "MOVING_AVERAGE";
public const string Fifo = "FIFO";
public const string Standard = "STANDARD";
```

---

# 7. Harden `StockCostState` for multiple methods

Current entity:

`ErpWeb.Model/Entities/StockLedger/StockValuationFact.cs`

contains `StockCostState` with:

```text
OnHandBaseQty
InventoryValue
AverageUnitCost
```

`AverageUnitCost` is WAC-specific.

Add:

```text
CurrentUnitCost decimal(19,6)
```

Migration:

```text
CurrentUnitCost = AverageUnitCost
```

Rules:

- WAC:
  - `CurrentUnitCost == AverageUnitCost`.
- FIFO:
  - `CurrentUnitCost` = `InventoryValue / OnHandBaseQty` informational current blended value;
  - FIFO issue cost comes from FIFO layers, never this field.
- STANDARD:
  - `CurrentUnitCost` = effective standard cost.
- keep `AverageUnitCost` for compatibility in this plan; deprecate it outside WAC but do not remove it.

Every report/service created by this plan must use `CurrentUnitCost` when it needs a current informational unit cost.

---

# 8. Phase A — Procurement actual cost / Purchase Invoice variance

## 8.1 Existing correct GR behaviour to preserve

File:

`ErpWeb.Core/Inventory/IvGoodsReceiptService.cs`

already freezes receipt cost evidence from PO commercial values and approved FX.

Preserve:

```text
PO net purchase amount
    -> purchase UOM unit cost
    -> base currency
    -> base stock UOM
    -> GR valuation fact
```

GR remains the initial/provisional receipt valuation for WAC/FIFO.

For STANDARD, Phase F changes receipt valuation to standard cost while retaining the actual/provisional commercial evidence for variance analysis.

## 8.2 Fix PI FX immediately

File:

`ErpWeb.Core/Purchase/PoInvoiceService.cs`

Current `ResolveCurrRateAsync()` can return `1m` when foreign currency has no valid rate.

Change rule:

```text
Currency is company base currency
    -> rate = 1

Foreign currency
    -> valid active rate covering DocDate is mandatory
    -> rate > 0
    -> otherwise SAVE/POST fails
```

Do not silently use `1`.

Prefer a shared currency-rate resolver used by both GR and PI so the same rule cannot drift.

Create/reuse a service such as:

```text
ICompanyCurrencyRateResolver
```

Do not duplicate ad-hoc rate lookup in each transaction service.

## 8.3 Add immutable purchase cost adjustment audit

Create:

### `PurchaseCostAdjustment`

Suggested columns:

```text
Id                        bigint PK
CompanyCode               nvarchar(5)
BranchCode                nvarchar(5)
AdjustmentType            nvarchar(30)
SourceDocumentType        nvarchar(30)
SourceDocumentNo          nvarchar(50)
SourceDocumentLine        int
PoNo                      nvarchar(50)
PoRelNo                   smallint
PoLineNo                  smallint
ItemCode                  nvarchar(30)
CostMethod                nvarchar(30)

PiBaseQty                 decimal(19,6)
ActualBaseUnitCost        decimal(19,6)
ReferenceBaseUnitCost     decimal(19,6)
TotalAdjustmentAmount     decimal(19,6)

InventoryAdjustmentAmount decimal(19,6)
ConsumedVarianceAmount    decimal(19,6)

StockPostingId            bigint
InventoryAdjustmentFactId bigint NULL
ReversesAdjustmentId      bigint NULL

CreatedAtUtc              datetime2(7)
CreatedBy                 nvarchar(100)
```

Types:

```text
PI_VARIANCE
PURCHASE_CN_VARIANCE
LANDED_COST        -- Phase I
```

Never update/delete posted adjustment rows. Reversal creates a linked compensating adjustment.

## 8.4 PI posting must join the valuation transaction boundary

Current `PoInvoiceService.PostOneAsync()` only updates commercial `InvoicedQty`.

Enhance it so POST:

```text
lock PI
lock related PO
validate quantities/prices/FX
    ->
begin StockPosting context for financial cost adjustment
    ->
calculate PI base actual cost
    ->
append PurchaseCostAdjustment
    ->
append zero-quantity StockValuationFact value adjustment where applicable
    ->
update StockCostState / FIFO layer adjustments
    ->
update PO InvoicedQty
    ->
seal StockPosting
    ->
mark PI POSTED
    ->
COMMIT
```

All must be one SQL transaction.

Rollback must reverse the exact cost-adjustment posting before restoring PI to NEW.

## 8.5 PI actual base unit cost

Use existing commercial fields:

```text
PoInvoiceDetail.NetAmount
PoInvoiceDetail.StdQty
PoInvoice.CurrRate
```

For stock-controlled line:

```text
ActualBaseLineAmount
    = NetAmount * CurrRate

ActualBaseUnitCost
    = ActualBaseLineAmount / StdQty
```

Use dedicated money/rate precision. Do not use `IvQty.Round` as the universal monetary rounding function.

Tax treatment must match the existing `NetAmount` contract. Do not capitalize `TaxAmt` unless the ERP explicitly identifies that tax as non-recoverable; that is outside this plan.

## 8.6 WAC PI variance policy

Do not rewrite the original GR fact and do not backdate the PI adjustment to GR date.

Effective date = PI posting/business date.

Calculate:

```text
TotalVariance
    = PI actual base amount
      - provisional value represented by the PI quantity
```

For WAC, late PI variance is a **current-date value adjustment**.

Because moving average intentionally loses receipt-layer identity, use the following explicit deterministic policy:

```text
CapitalizableQty
    = min(PI base qty, current positive on-hand base qty)

InventoryShare
    = CapitalizableQty / PI base qty

InventoryAdjustmentAmount
    = TotalVariance * InventoryShare

ConsumedVarianceAmount
    = TotalVariance - InventoryAdjustmentAmount
```

Rules:

- positive/negative variance supported;
- inventory value may never fall below zero;
- if on-hand = zero: all variance is consumed variance;
- do not restate already sealed historical sales COGS;
- consumed variance is reportable as purchase-price/consumed-cost variance for the PI posting period;
- append a zero-quantity valuation fact only for `InventoryAdjustmentAmount`;
- update `StockCostState.InventoryValue` and WAC after the inventory portion.

This is intentionally a current-period perpetual-WAC policy and avoids hidden retroactive revaluation.

## 8.7 FIFO PI variance policy

FIFO can identify remaining value precisely using layers.

For each PI/PO line:

1. resolve GR-origin FIFO layers for the PO line;
2. determine quantity represented by the PI allocation;
3. for layer quantity still open:
   - append an inventory value adjustment fact;
   - increase/decrease that layer's accumulated adjustment / remaining value;
4. for layer quantity already consumed:
   - record consumed variance in `PurchaseCostAdjustment`;
   - do not rewrite old issue facts.

The total must reconcile:

```text
TotalVariance
=
InventoryAdjustmentAmount
+
ConsumedVarianceAmount
```

## 8.8 Standard Cost PI variance policy

Under STANDARD:

- inventory remains at standard cost;
- PI actual cost does **not** revalue inventory;
- full actual-vs-standard difference is PPV:

```text
InventoryAdjustmentAmount = 0
ConsumedVarianceAmount     = Total PPV
```

Report it as Purchase Price Variance.

---

# 9. Phase B — Manual receipt / adjustment / stock-count cost gates

## 9.1 Misc Receipt

File:

`ErpWeb.Core/Inventory/IvMiscReceiptService.cs`

Current risk:

- unit price may be zero;
- `PriceConfirmed` exists but financial valuation can still resolve from `UnitPrice`.

Change the posting contract.

For every stock-in MR line, persist one explicit evidence type:

```text
MANUAL_APPROVED
ZERO_COST_APPROVED
OPENING_APPROVED
```

Add fields to `IvTrxBatchDetail` if necessary:

```text
CostEvidenceType
CostOverrideReason
CostApprovedBy
CostApprovedAtUtc
```

Do not encode all semantics into a free-text `PriceEvidence` string.

Rules:

- cost > 0:
  - requires `MANUAL_APPROVED`;
- cost == 0:
  - requires explicit `ZERO_COST_APPROVED`;
  - reason mandatory;
- opening migration:
  - requires `OPENING_APPROVED`;
- no confirmed evidence:
  - block POST.

WAC/FIFO uses the approved cost.
STANDARD ignores entered valuation for inventory and values at effective standard; the entered amount remains commercial/reference evidence.

## 9.2 Positive Stock Adjustment

File:

`ErpWeb.Core/Inventory/IvStockAdjustmentService.cs`

Current risk:

```text
UnitPrice >= 0
```

is insufficient.

New rules:

### WAC

For positive quantity:

- default cost must be current authoritative WAC from `StockCostState`;
- user-entered different cost requires approved manual override;
- if no cost state / zero existing valued quantity:
  - explicit opening/manual approved cost mandatory.

For negative quantity:

- ignore entered financial price;
- issue at authoritative current WAC.

### FIFO

Positive adjustment:

- create a new adjustment FIFO layer;
- approved cost evidence mandatory unless a documented system-derived source exists.

Negative adjustment:

- consume FIFO layers exactly like a normal financial issue.

### STANDARD

Positive/negative adjustment:

- value at effective standard cost;
- entered price cannot change inventory cost;
- differences may be retained as audit/reference only.

## 9.3 Stock Count

File:

`ErpWeb.Core/Inventory/IvStockCountService.cs`

Remove costing authority from:

```text
balance.UnitPrice ?? master.PurchasePrice ?? 0
```

for the final posted financial adjustment.

`SnapshotUnitPrice` may remain informational evidence for the count sheet, but must not control authoritative financial value.

At post time:

### WAC

Count gain:
- use current WAC;
- if no valued cost exists, require approved manual/opening cost before POST.

Count loss:
- use current WAC.

### FIFO

Count gain:
- create approved count-adjustment FIFO layer at approved/system-derived cost.

Count loss:
- consume FIFO layers.

### STANDARD

Gain/loss:
- use effective standard cost.

The stock count must not silently alter method policy.

---

# 10. Phase C — Sales COGS and Sales Return correctness

## 10.1 Preserve COGS ownership contract

Files:

- `ErpWeb.Core/Sales/SaDoService.cs`
- `ErpWeb.Core/Sales/SaInvoiceService.cs`

Hard invariant:

```text
DO physical stock-out
    -> DO valuation owner

Invoice LinkDo == true
    -> no second physical stock-out
    -> no second COGS
    -> report resolves source DO valuation

Direct Invoice LinkDo == false
    -> Invoice valuation owner
```

Do not change this.

## 10.2 Remove unsafe invoice COGS fallback

File:

`ErpWeb.Core/StockLedger/StockValuationQueryService.cs`

Current behaviour:

- attempts exact source line;
- if no exact line, falls back to all matching document + item facts.

Change:

```text
exact owner type + document + source line + item found
    -> resolved

exact source line missing or ambiguous
    -> IsResolved = false
    -> COGS must not be silently guessed
```

Never aggregate all same-item facts onto each invoice line.

For authoritative gross-profit reports:

- unresolved COGS marks the invoice/report row as unresolved;
- totals must separately expose unresolved count/value;
- period close/reconciliation may block if sales physical stock-out lacks deterministic line ownership.

## 10.3 Fix Customer Return original-cost lineage

Files:

- `ErpWeb.Core/Sales/SaCdnService.cs`
- `ErpWeb.Core/StockLedger/InventoryValuationService.cs`
- `ErpWeb.Core/StockLedger/StockValuationQueryService.cs`
- relevant sales entity/config files.

Current risk:

`AddCrBatchDetails()` primarily carries `InvNo`, but a linked invoice's physical COGS owner can be a DO.

Before creating the Customer Return batch, resolve the exact outbound valuation owner for each returned line.

Persist on the return stock detail/history:

```text
OriginalValuationFactId
OriginalOwnerType
OriginalOwnerDocumentNo
OriginalOwnerDocumentLine
```

Prefer `OriginalValuationFactId` as the authoritative FK.

For partial returns:

- returned quantity may not exceed remaining returnable quantity against the original sale valuation;
- track cumulative returned quantity by original valuation fact / sales source line.

Do not resolve return cost by document-number guess.

### WAC sales return

Inventory receipt value:

```text
ReturnedQty * original outbound unit cost
```

This becomes a new WAC receipt at return date.

### FIFO sales return

Create a new FIFO return layer dated at return posting date:

```text
UnitCost = exact original outbound consumed cost
OriginalValuationFactId = original sales-out fact
```

Do not re-open an old already-consumed layer.

### STANDARD sales return

COGS reversal must trace to original sale.

Inventory comes back at effective current standard cost.

If original sale standard differs from current standard:

```text
ReturnStandardVariance
=
current standard inventory receipt
-
original COGS reversal basis
```

record the difference as an explicit standard-cost variance fact; do not hide it.

## 10.4 Sales CN without physical return

If `ReturnStock == false`:

- no stock valuation fact;
- no inventory receipt;
- financial sales credit affects sales revenue only;
- COGS is unchanged unless a separate physical return is posted.

Preserve this distinction in Gross Profit reporting.

---

# 11. Phase D — Authoritative costing reports

## 11.1 Retire `Est. Inventory Value` as costing authority

Current files:

- `ErpWeb.UI/Inventory/Inquiry/IvStockValue.razor`
- `ErpWeb.UI/Inventory/Inquiry/IvStockValue.razor.cs`
- `ErpWeb.Core/Inventory/IvStockSummaryService.cs`
- `ErpWeb.Model/Repositories/Inventory/IvStockInquiryRepository.cs`
- `ErpWeb/Inventory/IvStockValueExportEndpoints.cs`

Current estimated formula:

```text
IvBalLoc.StdQty *
(IvBalLoc.UnitPrice ?? IvStockMaster.PurchasePrice ?? 0)
```

This may stay only as an operational estimate if desired, but it cannot be the official costing report.

## 11.2 Create valuation report service

Create:

```text
ErpWeb.Core/StockLedger/StockValuationReportService.cs
ErpWeb.Core/StockLedger/IStockValuationReportService.cs
```

Queries must use:

- sealed `StockValuationFact`;
- latest applicable `StockValuationPeriodSnapshot` for closed periods;
- facts after snapshot watermark for open/current period;
- never mutable current purchase price as historical fallback.

Support:

```text
AsOfDate
ItemCode
WarehouseCode
ItemClass
CostMethod
GroupBy:
    Item
    Warehouse
    Class
    ItemWarehouse
```

Rows:

```text
Item
Description
Class
Warehouse
BaseUom
Qty
InventoryValue
UnitCost
CostMethod
ValuationStatus
```

For a group with mixed UOM, do not show a meaningless total quantity.

## 11.3 Rewire Inventory Value UI

Reuse the existing route/menu/permission if possible so access control remains stable.

Change page title only after the authoritative service is wired:

```text
Inventory Valuation
```

The export endpoint `/inventory/valuation/export` must use the same report service and the same source-of-truth query as the screen.

The screen, summary cards and export must reconcile exactly.

## 11.4 Minimum Procurement costing inquiry

Create a read model/service for:

```text
PO provisional cost
GR receipt cost
PI actual cost
PI variance
inventory-capitalized variance
consumed variance
landed cost (Phase I)
```

Suggested service:

```text
IPurchaseCostInquiryService
PurchaseCostInquiryService
```

Minimum report grain:

```text
Vendor + PO + PO line + item + PI + PI line
```

## 11.5 Minimum Sales gross profit inquiry

Create a read model/service that combines:

- posted invoice net sales;
- exact COGS from `StockValuationFact`;
- `LinkDo` source DO cost;
- direct invoice cost;
- stock-return COGS reversals when appropriate.

Fields:

```text
Invoice
Date
Customer
Item
Qty
NetSales
Cogs
GrossProfit
GrossMarginPercent
CogsOwnerType
CogsOwnerNo
CogsResolved
```

Formula:

```text
GrossProfit = NetSales - COGS

GrossMargin% =
GrossProfit / NetSales * 100
```

Do not include unresolved lines in a falsely precise "final" total.

---

# 12. Phase E — True financial FIFO

The current `IvSpFifoEligibility` / shipment allocation code is physical stock selection only.

Do not reuse `IvBalLoc` as the financial FIFO layer table.

## 12.1 Add FIFO layer tables

### `StockFifoLayer`

Suggested columns:

```text
Id                        bigint PK
CompanyCode               nvarchar(5)
BranchCode                nvarchar(5)
ItemCode                  nvarchar(30)
BaseUom                   nvarchar(10)

OriginValuationFactId     bigint
OriginStockPostingId      bigint
ReceiptEffectiveAt        datetime2(7)

OriginalQty               decimal(19,6)
RemainingQty              decimal(19,6)

OriginalValue             decimal(19,6)
AccumulatedAdjustment     decimal(19,6)
RemainingValue            decimal(19,6)
CurrentUnitCost           decimal(19,6)

SourceDocumentType        nvarchar(40)
SourceDocumentNo          nvarchar(50)
SourceDocumentLine        nvarchar(50) NULL

WarehouseCode             nvarchar(20) NULL  -- provenance only
LotId                     int NULL            -- provenance only
LotNo                     nvarchar(50) NULL

Status                     nvarchar(20)       -- OPEN / CLOSED
RowVersion                 rowversion
```

Financial FIFO ordering:

```text
ReceiptEffectiveAt
then OriginValuationFactId
```

The financial pool remains company + branch + item.

Do not force the financial layer order to equal physical shipment-lot selection.

### `StockFifoLayerConsumption`

```text
Id                     bigint PK
CompanyCode
BranchCode
IssueValuationFactId   bigint
FifoLayerId            bigint
ConsumedQty            decimal(19,6)
ConsumedValue          decimal(19,6)
SplitOrdinal           int
ReversesConsumptionId  bigint NULL
CreatedAtUtc
CreatedBy
```

Unique:

```text
IssueValuationFactId + SplitOrdinal
```

## 12.2 FIFO receipt

For every valued inventory receipt:

1. create normal receipt `StockValuationFact`;
2. create one new FIFO layer;
3. `OriginalQty = RemainingQty = receipt qty`;
4. `OriginalValue = RemainingValue = receipt value`.

PI variance does not rewrite the receipt fact.

It updates layer adjustment state by adding an immutable adjustment fact.

## 12.3 FIFO issue

For required issue quantity:

```text
lock OPEN FIFO layers for item
order by ReceiptEffectiveAt, Id
consume until requested qty fulfilled
```

For each layer take:

```text
ConsumedQty
ConsumedValue
```

Create valuation fact split(s) or one parent fact with deterministic split identity according to the existing `PostingLineNo + SplitOrdinal` contract.

Recommendation:

- one `StockValuationFact` per layer consumption split;
- `SplitOrdinal` corresponds to FIFO consumption order.

Final layer depletion must consume exact remaining layer value.

No residual value may remain on zero-qty layer.

## 12.4 FIFO reversal

Never rerun FIFO.

Load original `StockFifoLayerConsumption` rows and restore exactly:

```text
same layer
same quantity
same value
```

Create reversal consumption records and valuation facts linked to originals.

## 12.5 FIFO transfer

Internal stock transfer does not create a new financial FIFO receipt layer.

The same financial inventory remains owned by the branch/item pool.

Transfer valuation facts must remain value-neutral.

## 12.6 FIFO sales return

Create a new return layer at return date with original sale cost lineage.

## 12.7 FIFO close reconciliation

Add checks:

```text
sum OPEN RemainingQty
    == StockCostState.OnHandBaseQty

sum OPEN RemainingValue
    == StockCostState.InventoryValue

every FIFO issue fact
    == sum linked layer consumption values
```

Block close on mismatch.

---

# 13. Phase F — Standard Costing

## 13.1 Add effective-dated standard cost

Create:

### `ItemStandardCostRevision`

```text
Id                  bigint PK
CompanyCode         nvarchar(5)
BranchCode          nvarchar(5)
ItemCode            nvarchar(30)

EffectiveFrom       date
EffectiveTo         date NULL

MaterialCost        decimal(19,6)
LabourCost          decimal(19,6) default 0
MachineCost         decimal(19,6) default 0
OverheadCost        decimal(19,6) default 0
SubcontractCost     decimal(19,6) default 0

TotalStandardCost   decimal(19,6)

Status              nvarchar(20) -- APPROVED / SUPERSEDED
Revision            int
ApprovedBy          nvarchar(100)
ApprovedAtUtc       datetime2(7)
Reason              nvarchar(250)
CreatedAtUtc
CreatedBy
RowVersion
```

For the Inventory/Procurement/Sales scope of this plan:

- `MaterialCost` is required.
- other components are retained as zero-capable fields because Production already exists and future integration must not require a breaking schema redesign.
- this plan does not calculate manufacturing activity rates.

Constraints:

```text
TotalStandardCost >= 0
no overlapping effective ranges for same branch/item
```

STANDARD posting fails when a stock-controlled item has no approved standard cost effective on the transaction date.

## 13.2 Standard receipt

Inventory receipt value:

```text
ReceiptQty * effective TotalStandardCost
```

Commercial purchase cost remains separately recorded.

For GR:

- stock valuation = standard;
- PO provisional / supplier amount remains purchase evidence.

## 13.3 Standard issue / COGS

Any issue:

```text
IssueQty * effective standard cost
```

Sales COGS uses the standard cost fact produced by the stock-out owner.

## 13.4 PI Purchase Price Variance

For PI:

```text
Actual purchase base amount
-
PI Qty * effective standard cost
=
PPV
```

No inventory revaluation from PI.

Post PPV as financial variance data/fact, not stock quantity.

## 13.5 Standard cost revision / revaluation

When a new standard becomes effective and stock exists:

```text
RevaluationAmount
=
OnHandQty * (NewStandard - OldStandard)
```

Create a dedicated `StockPosting` with role/type such as:

```text
STANDARD_COST_REVALUATION
```

Append zero-quantity valuation fact(s):

```text
MovementCode = STANDARD_REVALUE_IN
or
MovementCode = STANDARD_REVALUE_OUT
```

Update `StockCostState.InventoryValue` and `CurrentUnitCost`.

Do not modify old valuation facts.

A standard revision may not be backdated into a closed period.

## 13.6 Standard return

As defined in Sales phase:

- reverse original COGS using original lineage;
- inventory receipt at current effective standard;
- difference recorded as explicit return-standard variance.

---

# 14. Phase G — Reconciliation and close changes for three methods

File:

`ErpWeb.Core/StockLedger/StockValuationSnapshotBuilder.cs`

## 14.1 Centralize movement classification

Current code treats only movement codes beginning with `ADJUST_` as adjustment.

Create a centralized classifier, e.g.:

```text
StockValuationMovementClassifier
```

Classify value-only adjustments including:

```text
ADJUST_IN
ADJUST_OUT
PURCHASE_PRICE_VARIANCE_IN
PURCHASE_PRICE_VARIANCE_OUT
LANDED_COST_IN
LANDED_COST_OUT
STANDARD_REVALUE_IN
STANDARD_REVALUE_OUT
RETURN_STANDARD_VARIANCE_IN
RETURN_STANDARD_VARIANCE_OUT
```

Financial snapshot must present these as adjustments, not normal receipt/issue quantity.

## 14.2 Generic close invariants

For every method:

```text
OpeningQty
+ InQty
+ AdjustmentQty
- OutQty
= ClosingQty
```

```text
OpeningValue
+ InValue
+ AdjustmentValue
- OutValue
= ClosingValue
```

Block when:

- closing qty < 0;
- closing value < 0;
- qty == 0 and value != 0;
- physical quantity != valued quantity;
- unresolved sales COGS ownership exists;
- required cost evidence unresolved;
- PI adjustment posting is partially applied;
- cost policy missing.

## 14.3 Method-specific close invariants

### WAC

```text
StockCostState
== cumulative valued facts
```

and:

```text
CurrentUnitCost
== InventoryValue / OnHandQty
```

except zero qty => zero cost.

### FIFO

Also require:

```text
open FIFO qty/value
== StockCostState qty/value
```

### STANDARD

Require:

```text
InventoryValue
== OnHandQty * effective standard cost
```

after all standard revaluation postings up to close watermark.

---

# 15. Phase H — SHOULD: persistent stock freeze

Current file:

`ErpWeb.Core/StockLedger/StockPostingGuards.cs`

Current DI:

`IStockFreezeGuard -> NoActiveStockFreezeGuard`

The current guard is a no-op.

## 15.1 Create stock freeze entity

### `StockFreeze`

```text
Id                    bigint PK
CompanyCode
BranchCode
FreezeType            nvarchar(30)
SourceDocumentType    nvarchar(30)
SourceDocumentId      nvarchar(64)

WarehouseCode         nvarchar(20) NULL
LocationCode          nvarchar(10) NULL
ProductionLocationId  bigint NULL

Status                nvarchar(20) -- ACTIVE / RELEASED
StartedAtUtc
StartedBy
ReleasedAtUtc NULL
ReleasedBy NULL
Reason
RowVersion
```

## 15.2 Replace guard

Create:

```text
PersistentStockFreezeGuard : IStockFreezeGuard
```

Register it instead of `NoActiveStockFreezeGuard`.

The guard must:

- use trusted company/branch from posting context;
- reject a posting whose `FreezeScopes` overlaps an ACTIVE freeze.

## 15.3 Populate `FreezeScopes`

Enhance `InventoryStockPostingCommandFactory.CreateAsync()` to derive warehouse/location scopes from the batch's current revision lines.

Sales posting command paths must pass the shipment warehouse/location scopes.

## 15.4 Stock count lifecycle

Use `IvStockCountService`.

Suggested freeze lifecycle:

```text
DRAFT
    -> no freeze

COUNTED
    -> create ACTIVE freeze for the counted warehouse/location scope

POSTED
    -> release after successful stock adjustment + header commit

CANCELLED
    -> release

ROLLED_BACK / Recover
    -> release or recreate according to current count state
```

If the current stock count can span multiple warehouses/locations, create one freeze row per unique scope.

This enhancement is SHOULD, but implement it before enabling high-volume costing reports in production because it materially improves count integrity.

---

# 16. Phase I — SHOULD: Landed Cost

Implement only after PI variance passes all acceptance tests.

Reuse the `PurchaseCostAdjustment` architecture.

## 16.1 Supported cost components

Minimum:

```text
FREIGHT
INSURANCE
IMPORT_DUTY
HANDLING
FORWARDER
OTHER
```

## 16.2 Allocation bases

Support only practical SME bases:

```text
QUANTITY
VALUE
WEIGHT
MANUAL
```

Do not add advanced allocation rules in this phase.

## 16.3 Posting rule

Landed-cost document:

```text
save allocation
    ->
validate total allocated = source charge
    ->
create StockPosting
    ->
append immutable PurchaseCostAdjustment rows
    ->
append value-only StockValuationFact(s)
    ->
update WAC/FIFO state
    ->
seal
```

STANDARD:

- do not change inventory standard value;
- record landed-cost variance separately.

## 16.4 Reversal

Never edit original allocation.

Reverse exact original allocation rows/facts.

---

# 17. Cost method cutover

Changing an active branch method is financially dangerous.

Create:

```text
IStockCostMethodCutoverService
StockCostMethodCutoverService
```

## 17.1 Allowed cutover

Only when:

- prior accounting period is closed;
- new effective date is the first day of an open period;
- no sealed stock posting exists after the requested cutover date unless explicitly rolled back;
- no active stock freeze;
- current quantity/value reconciliation passes;
- user has explicit costing setup permission.

## 17.2 WAC -> FIFO

At cutover:

Preferred:
- rebuild opening FIFO layers from defensible open receipt history if exact receipt history is complete.

Fallback:
- one `OPENING_APPROVED` FIFO layer per item at approved opening value.

Never fabricate detailed historical FIFO layers from current `PurchasePrice`.

## 17.3 WAC/FIFO -> STANDARD

Require approved standard cost for every positive-stock item.

Append standard-cost revaluation facts so:

```text
InventoryValue
=
OnHandQty * StandardCost
```

## 17.4 STANDARD -> WAC

Opening WAC:

```text
AverageUnitCost
=
current inventory value / on-hand qty
```

create explicit cutover/opening fact/state.

## 17.5 STANDARD/WAC -> FIFO

Same opening-layer rule as above.

Cutover records must be auditable.

---

# 18. Posting validation matrix

The AI agent must implement server-side checks. UI checks are convenience only.

| Transaction | WAC | FIFO | STANDARD |
|---|---|---|---|
| GR | approved provisional PO/base cost | create receipt layer | value at effective std cost |
| PI | post inventory/consumed PPV | adjust open layers + consumed variance | full PPV, no inventory revalue |
| Purchase CN | exact reverse/variance logic | exact layer/variance reverse | standard/PPV reverse |
| Misc Receipt | approved cost evidence | approved cost creates layer | standard cost |
| Stock Adj + | WAC unless approved override | new approved layer | standard cost |
| Stock Adj - | current WAC | consume FIFO | standard cost |
| Stock Count + | WAC / approved opening | new count layer | standard cost |
| Stock Count - | current WAC | consume FIFO | standard cost |
| Transfer | value-neutral | no new financial layer | value-neutral |
| DO | WAC COGS | FIFO layer COGS | standard COGS |
| Direct Invoice | WAC COGS | FIFO layer COGS | standard COGS |
| LinkDo Invoice | reuse DO COGS | reuse DO COGS | reuse DO COGS |
| Sales Return | original outbound cost | new return layer at original cost | current std inventory + explicit variance |
| Rollback | exact fact reversal | exact layer consumption reversal | exact fact/variance reversal |

---

# 19. Required source-file change map

This is not exhaustive for configuration/migration files, but the agent must anchor work here.

## Existing files to modify

### Stock ledger

- `ErpWeb.Core/StockLedger/InventoryValuationService.cs`
- `ErpWeb.Core/StockLedger/StockValuationQueryService.cs`
- `ErpWeb.Core/StockLedger/StockValuationSnapshotBuilder.cs`
- `ErpWeb.Core/StockLedger/StockPostingGuards.cs`
- `ErpWeb.Core/StockLedger/InventoryStockPostingCommandFactory.cs`
- `ErpWeb.Core/CoreServiceCollectionExtensions.cs`

### Model

- `ErpWeb.Model/Entities/StockLedger/StockValuationFact.cs`
- `ErpWeb.Model/Configurations/StockLedger/StockValuationFactConfiguration.cs`
- `ErpWeb.Model/Data/AppDbContext.cs`
- relevant model configuration registration
- `scripts/create-stock-valuation-ledger.sql` or a new idempotent follow-up costing enhancement SQL script

### Inventory

- `ErpWeb.Core/Inventory/IvGoodsReceiptService.cs`
- `ErpWeb.Core/Inventory/IvMiscReceiptService.cs`
- `ErpWeb.Core/Inventory/IvStockAdjustmentService.cs`
- `ErpWeb.Core/Inventory/IvStockCountService.cs`
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`
- `ErpWeb.Model/Entities/Inventory/IvTrxBatchDetail.cs`
- `ErpWeb.Model/Entities/Inventory/IvTrxHistory.cs`

### Procurement

- `ErpWeb.Core/Purchase/PoInvoiceService.cs`
- `ErpWeb.Core/Purchase/PoCdnService.cs`
- `ErpWeb.Model/Entities/Purchase/PoInvoiceDetail.cs` only if additional persisted financial reference fields are necessary; do not duplicate data already available in adjustment tables.

### Sales

- `ErpWeb.Core/Sales/SaDoService.cs`
- `ErpWeb.Core/Sales/SaInvoiceService.cs`
- `ErpWeb.Core/Sales/SaCdnService.cs`
- corresponding sales entities only where exact source-line/return lineage cannot be persisted through current inventory batch/history identities.

### Reporting UI

- `ErpWeb.UI/Inventory/Inquiry/IvStockValue.razor`
- `ErpWeb.UI/Inventory/Inquiry/IvStockValue.razor.cs`
- `ErpWeb/Inventory/IvStockValueExportEndpoints.cs`

Do not rewrite `IvStockSummaryService` solely to become costing authority. Keep stock summary as a quantity/operational inquiry.

## New files/tables expected

```text
StockCostPolicyRevision
PurchaseCostAdjustment
StockFifoLayer
StockFifoLayerConsumption
ItemStandardCostRevision
StockFreeze
```

Services:

```text
IStockCostMethodResolver
StockCostMethodResolver

IInventoryCostingStrategy
MovingAverageCostingStrategy
FifoCostingStrategy
StandardCostingStrategy
InventoryCostingStrategyResolver

IStockValuationReportService
StockValuationReportService

IPurchaseCostInquiryService
PurchaseCostInquiryService

ISalesGrossProfitInquiryService
SalesGrossProfitInquiryService

IStockCostMethodCutoverService
StockCostMethodCutoverService

PersistentStockFreezeGuard
```

Landed-cost service/page may be added in the SHOULD phase after PPV is stable.

---

# 20. Monetary precision

Current code sometimes uses `IvQty.Round` for values.

Create/centralize financial precision helpers:

```text
RoundQuantity
RoundUnitCost
RoundMoney
RoundRate
```

Recommended current schema-compatible precisions:

```text
Qty       decimal(19,6)
UnitCost  decimal(19,6)
Money     decimal(19,6)
FX Rate   decimal(19,8)
```

Rules:

- carry full configured precision through calculation;
- round only at documented boundaries;
- final depletion takes exact remaining value;
- sum of split cost amounts must equal parent/source cost amount exactly after final-line residual adjustment.

---

# 21. Backdated transaction policy

Preserve current WAC protection.

General rule:

```text
Closed period
    -> reject

Open period + no later valued movement affecting the required costing state
    -> allowed

Open period + later valued movements
    -> reject and require rollback/repost
```

For FIFO:
- a backdated receipt/issue that would change layer order after later FIFO consumption must be rejected unless later affected postings are rolled back first.

For STANDARD:
- historical transaction uses the standard effective on that transaction date;
- a backdated standard revision that would alter sealed postings is rejected.

No silent historical recomputation.

---

# 22. Reversal rules

Every financial feature in this plan must implement rollback before it is considered complete.

## Generic rule

Rollback:

```text
load original posting/facts
    ->
append exact compensating facts
    ->
reverse exact state mutation
    ->
preserve original record
```

Never:

```text
query current cost
recalculate what the old cost "should have been"
```

## PI rollback

Reverse:

- `PurchaseCostAdjustment`;
- inventory value adjustment fact;
- WAC state change / FIFO layer adjustment;
- consumed variance record;
- PO invoiced quantity.

## Standard revaluation rollback

Reverse exact revaluation amount and restore prior current standard state only when no later dependent posting makes rollback invalid.

## FIFO issue rollback

Restore exact original FIFO layers and exact consumed values.

---

# 23. Reconciliation service

Create or extend reconciliation so management reports are not enabled while costing is inconsistent.

Minimum findings:

```text
PHYSICAL_QTY_VS_VALUED_QTY
VALUATION_VS_COST_STATE
ZERO_QTY_RESIDUAL_VALUE
NEGATIVE_VALUE
MISSING_COST_POLICY
MISSING_COST_EVIDENCE
UNVALUED_FACT
PI_ADJUSTMENT_MISMATCH
PI_VARIANCE_NOT_BALANCED
COGS_OWNER_UNRESOLVED
SALES_RETURN_ORIGINAL_COST_UNRESOLVED
FIFO_LAYER_QTY_MISMATCH
FIFO_LAYER_VALUE_MISMATCH
FIFO_CONSUMPTION_MISMATCH
STANDARD_COST_MISSING
STANDARD_VALUE_MISMATCH
ACTIVE_FREEZE_CONFLICT
```

Severity:

- all above are **blocking** for financial period close where relevant.

---

# 24. Database migration / deployment strategy

Do not modify production data destructively.

## 24.1 Additive migrations first

Deploy:

- new tables;
- new nullable evidence/lineage fields;
- `CurrentUnitCost`;
- indexes/constraints;
- new services disabled behind existing WAC policy default.

## 24.2 Seed WAC policy

Create policy revisions for existing active branches.

## 24.3 Backfill only defensible data

Safe backfill:

- `StockCostState.CurrentUnitCost = AverageUnitCost`;
- existing valuation facts already contain `MOVING_AVERAGE`;
- create no fake FIFO layer history unless running an explicit cutover.

## 24.4 Enable new WAC hardening first

Before FIFO/Standard:

- PI FX gate;
- PI variance;
- MR/ADJ/COUNT cost gates;
- sales return lineage;
- strict COGS resolution;
- authoritative valuation report.

Only after WAC path reconciles should FIFO/Standard be enabled.

---

# 25. Implementation sequence for AI Coding Agent

The agent must follow this order.

## Stage 1 — Freeze baseline and preserve WAC

1. Add cost policy entity/config/resolver.
2. Seed existing branches to `MOVING_AVERAGE`.
3. Add `CurrentUnitCost`.
4. Refactor current WAC into `MovingAverageCostingStrategy`.
5. Verify existing WAC valuation outputs remain unchanged.
6. Keep `StockPostingCoordinator` unchanged except dependency wiring needed for resolver/strategy.

**Exit gate:** existing inventory/sales/production valuation tests still pass with identical WAC outcomes.

## Stage 2 — Procurement correctness

7. Introduce shared fail-closed FX resolver.
8. Fix `PoInvoiceService` foreign FX.
9. Add `PurchaseCostAdjustment`.
10. Add PI cost-adjustment stock posting.
11. Implement WAC PI variance policy.
12. Implement PI rollback.
13. Extend purchase CN reversal/adjustment behaviour consistently.

**Exit gate:** GR -> PI -> rollback reconciles quantity/value exactly.

## Stage 3 — Manual inventory cost gates

14. Add structured cost-evidence fields.
15. Enforce MR approved/zero-cost evidence.
16. Enforce Stock Adjustment costing by method.
17. Change Stock Count financial valuation to method policy.
18. Add permissions/audit for manual cost override.

**Exit gate:** user-entered arbitrary price cannot silently change authoritative inventory value.

## Stage 4 — Sales correctness

19. Remove document/item COGS fallback.
20. Add exact sales return original valuation lineage.
21. Add cumulative partial-return guard.
22. Implement WAC return.
23. Verify DO/LinkDo/direct invoice ownership remains unchanged.
24. Implement exact rollback.

**Exit gate:** mixed invoice (DO-linked + direct) and partial returns reconcile COGS exactly.

## Stage 5 — Authoritative reports

25. Add `StockValuationReportService`.
26. Rewire `IvStockValue` screen/export.
27. Add Purchase Cost / PPV inquiry.
28. Add Sales Gross Profit inquiry.
29. Add unresolved-cost indicators.

**Exit gate:** all three reports reconcile to the same sealed facts and period snapshots.

## Stage 6 — FIFO

30. Add FIFO layer/consumption schema.
31. Implement FIFO strategy receipt.
32. Implement FIFO issue.
33. Implement FIFO exact rollback.
34. Implement FIFO PI variance layer adjustment.
35. Implement FIFO sales return.
36. Add FIFO close reconciliation.

**Exit gate:** known FIFO scenarios produce exact expected COGS and remaining layers.

## Stage 7 — Standard Cost

37. Add standard cost revision schema/service.
38. Implement Standard strategy.
39. Implement standard GR/issue/sales COGS.
40. Implement PI PPV.
41. Implement standard-cost revaluation.
42. Implement sales-return standard variance.
43. Add Standard close reconciliation.

**Exit gate:** inventory value always equals quantity × effective standard after required revaluation postings.

## Stage 8 — Controlled method cutover

44. Implement cutover service.
45. WAC/FIFO/STANDARD transition rules.
46. Prevent casual cost-method edits.
47. Add audit.

**Exit gate:** no method can be changed while leaving un-reconciled value.

## Stage 9 — SHOULD: stock freeze

48. Add `StockFreeze`.
49. Implement `PersistentStockFreezeGuard`.
50. Populate command freeze scopes.
51. Wire stock count lifecycle.

## Stage 10 — SHOULD: landed cost

52. Reuse purchase adjustment framework.
53. Add basic charge types/allocation bases.
54. Add exact rollback.
55. Include in Purchase Cost inquiry.

## Stage 11 — final close/reconciliation hardening

56. Centralize movement classification.
57. Add method-specific close gates.
58. Add reconciliation findings.
59. Validate report/snapshot totals.
60. Remove any remaining official report dependency on mutable purchase price.

---

# 26. Required test coverage

Costing is a financial subsystem; targeted automated tests are mandatory.

Do not add huge UI test suites. Focus on ledger invariants.

Use existing folders/namespaces:

```text
ErpWeb.Tests/Inventory/Transaction
ErpWeb.Tests/Inventory/Inquiry
ErpWeb.Tests/Procurement/Transaction
ErpWeb.Tests/Sales/Transaction
ErpWeb.Tests/Other
```

## 26.1 WAC

Test:

```text
opening 0
GR 100 @ 10
GR 100 @ 12
issue 150
```

Expected:

```text
avg before issue = 11
COGS = 1650
remaining qty = 50
remaining value = 550
```

Final depletion must remove exact remaining value.

## 26.2 PI variance

Case:

```text
GR 100 @ provisional 10
PI 100 @ actual 11
current on-hand 40
```

Expected by policy:

```text
total variance = 100
inventory portion = 40
consumed variance = 60
```

Then rollback returns exactly to pre-PI values.

Also test negative price variance.

## 26.3 Missing foreign FX

Foreign PI with no valid rate:

```text
SAVE/POST fails
no InvoicedQty changed
no valuation fact
no PurchaseCostAdjustment
```

## 26.4 MR zero cost

Zero-cost MR without `ZERO_COST_APPROVED` fails.

With approval succeeds and leaves audit evidence.

## 26.5 Stock adjustment

Positive adjustment with arbitrary cost but no override permission/evidence fails.

## 26.6 Stock count

Count gain never uses mutable `PurchasePrice` as final cost authority.

## 26.7 DO / Invoice ownership

Test:

- DO shipped -> one COGS owner.
- LinkDo invoice -> zero additional stock-out facts.
- direct invoice -> invoice-owned COGS.
- mixed invoice -> report totals both once.

## 26.8 Duplicate item lines

Invoice with two lines for the same item.

If exact line source identity is missing:

```text
both must not receive whole-document same-item COGS
IsResolved=false
```

## 26.9 Sales return

Test:

```text
DO -> Invoice LinkDo -> CN ReturnStock
```

Return must resolve DO-owned original valuation.

Partial return twice must not exceed original outbound quantity.

## 26.10 FIFO

Receipt:

```text
100 @ 10
100 @ 12
issue 150
```

Expected:

```text
COGS 1600
remaining 50 @ 12 = 600
```

Rollback restores:

```text
100 @ 10
100 @ 12
```

exactly.

## 26.11 Standard

Standard 10:

```text
GR actual 11 x 100
```

Expected:

```text
inventory = 1000
PPV = 100
```

Sale 20:

```text
COGS = 200
inventory = 800
```

Change standard to 12 with 80 on hand:

```text
revaluation +160
inventory = 960
```

## 26.12 Close

Close fails for:

- unresolved valuation;
- cost-state mismatch;
- FIFO layer mismatch;
- standard inventory mismatch;
- unresolved COGS;
- zero qty residual value.

---

# 27. SQL concurrency and locking requirements

Costing changes must follow existing repository locking style.

For any posting affecting financial cost:

1. trusted tenant/branch scope;
2. branch transaction lock where existing stock posting path uses it;
3. lock source document;
4. lock cost state / FIFO layers in deterministic key order;
5. mutate physical and financial state in one transaction;
6. append history/facts;
7. seal posting;
8. commit.

FIFO layer lock order:

```text
Company
Branch
Item
ReceiptEffectiveAt
LayerId
```

PI adjustment lock order must be deterministic by:

```text
PO No
PO Revision
PO Line
Item
```

Avoid deadlock-prone inconsistent ordering.

---

# 28. Security and audit

Add/extend permissions for:

```text
COSTING_SETUP
COSTING_METHOD_CHANGE
STANDARD_COST_APPROVE
MANUAL_COST_OVERRIDE
VIEW_COST
EXPORT_COST
```

Reuse the repository's existing menu/permission infrastructure.

Audit at minimum:

- costing-method policy revision;
- method cutover;
- standard cost revision;
- standard revaluation;
- manual/zero/opening cost approval;
- PI variance;
- landed cost;
- stock freeze release/override.

Do not expose cost values to users without the equivalent `VIEW_PRICE` / costing permission.

---

# 29. Agent guardrails

The coding agent MUST NOT:

1. create another financial ledger table that competes with `StockValuationFact`;
2. create another stock-posting coordinator;
3. use `IvStockMaster.PurchasePrice` as historical cost fallback;
4. use `IvBalLoc.UnitPrice` as historical report authority;
5. use physical shipment FIFO as financial FIFO;
6. duplicate COGS for `LinkDo` invoice lines;
7. recompute rollback using current cost;
8. silently default foreign FX to `1`;
9. silently accept zero-cost stock without explicit evidence;
10. modify sealed valuation facts;
11. retroactively recalculate sealed WAC/FIFO/Standard issues;
12. permit cost-method change by a simple dropdown update while stock exists;
13. build official management profit totals from unresolved COGS;
14. remove existing no-future-stock or period-close checks;
15. break current Production stock-ledger integration.

If the agent finds an existing implementation already satisfying a step, it should verify and reuse it rather than duplicate it.

---

# 30. Definition of done — 10/10 approval gate

The costing enhancement is **not approved** until all MUST gates below pass.

## Architecture

- one explicit active cost policy per company/branch/business date;
- WAC/FIFO/STANDARD all create authoritative `StockValuationFact`;
- reports consume the same ledger;
- `StockPostingCoordinator` remains the sole posting envelope.

## WAC

- current behaviour remains mathematically correct;
- PI variance changes current financial state under documented policy;
- arbitrary adjustment/count/manual costs cannot corrupt WAC.

## Procurement

- GR provisional cost is traceable.
- missing foreign PI FX blocks.
- PI creates auditable variance.
- inventory vs consumed variance balances to total PI variance.
- rollback exactly reverses it.
- purchase CN follows exact reverse policy.

## Sales

- every physical stock-out has exactly one COGS owner.
- LinkDo invoice never duplicates DO COGS.
- direct invoice owns its COGS.
- duplicate same-item invoice lines cannot duplicate COGS through fallback.
- sales return resolves exact original cost lineage.
- partial returns cannot over-return original quantity.

## FIFO

- layers are financial, not `IvBalLoc`.
- issue consumes oldest financial layer.
- final layer depletion uses exact residual value.
- rollback restores exact original layers.
- PI variance adjusts open layer value and classifies consumed variance.

## Standard

- every stock-controlled STANDARD item has an effective approved standard cost.
- receipt/issue use standard cost.
- PI actual-vs-standard produces PPV.
- standard revision creates explicit revaluation.
- old facts remain immutable.

## Reports

- Inventory Valuation no longer uses the old estimate formula as authority.
- Inventory Valuation ties to valuation snapshots/facts.
- Purchase Cost/PPV ties to GR/PI adjustments.
- Gross Profit ties to exact sales COGS facts.
- unresolved rows are visible and cannot masquerade as final totals.

## Close

- physical qty = valued qty.
- valuation value = method state.
- FIFO open layers reconcile where FIFO active.
- standard quantity × standard cost reconciles where STANDARD active.
- zero qty residual value is blocked.
- unresolved COGS/cost evidence blocks close when financially relevant.

## Reversal

Every new financial posting path has an exact reversal path.

---

# 31. Final implementation target

```text
                           COST POLICY
                 MOVING_AVERAGE / FIFO / STANDARD
                              |
                              v
SOURCE DOCUMENTS ------> StockPostingCoordinator
Inventory                    |
Procurement                   v
Sales                  IvTrxHistory
                              |
                              v
                   InventoryValuationService
                              |
                 +------------+-------------+
                 |            |             |
                 v            v             v
              WAC         FIFO layers    STANDARD
                 |            |             |
                 +------------+-------------+
                              |
                              v
                    StockValuationFact
                      (financial truth)
                              |
                 +------------+-------------+
                 |                          |
                 v                          v
             Current state           Period snapshots
                 |                          |
                 +-------------+------------+
                               |
                               v
                    AUTHORITATIVE REPORTS
                 Inventory Valuation
                 Purchase Cost / PPV
                 Sales COGS / Gross Profit
```

---

# 32. Recommended implementation checkpoints

The AI coding agent should produce a checkpoint after each stage containing:

```text
Changed files
New files
DB changes
Posting paths affected
Rollback paths affected
Reconciliation changes
Tests added
Known unresolved items
```

Do not implement FIFO and Standard before the hardened WAC/PI/Sales/reporting stages reconcile.

The safest delivery order is:

```text
WAC hardening
    ->
Procurement actual cost
    ->
Manual inventory cost controls
    ->
Sales COGS/return lineage
    ->
Authoritative reports
    ->
FIFO
    ->
Standard Cost
    ->
Method cutover
    ->
Stock freeze
    ->
Landed cost
```

This order protects the currently working repository while adding the requested costing capabilities on top of the existing V2 stock-ledger foundation.

---

# 33. Final status

**Plan intent: APPROVED IMPLEMENTATION BLUEPRINT**

This plan is grounded in the reviewed `production` branch at:

`a58c508d8efc3de0bd8b34d7c7c9a3f0d3deecdd`

It deliberately preserves the repository's already-correct V2 stock/valuation architecture and only adds the MUST and SHOULD costing enhancements identified in the costing review.

The coding agent should not need to invent any major costing-policy decision during implementation. Where implementation details differ because the branch moves forward, the agent must preserve the financial invariants and map the same contracts to the then-current source files rather than introducing a parallel costing architecture.
