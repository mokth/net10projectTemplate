# Production Stock Return + Production Stock Adjustment
## FINAL APPROVED 10/10 — AI Coding Agent Implementation Plan v5 — RED-TEAM SAFETY REVIEW
### Repository: `mokth/net10projectTemplate`
### Branch verified: `productionv2`
### Verified branch HEAD: `61a4a66099061ef07157af15f8020cb257077420`
### Deep-review date: 2026-10-11

> **FINAL STATUS: APPROVED FOR IMPLEMENTATION — 10/10**
>
> **Risk level: CRITICAL**
>
> This plan changes physical Production quantity, Inventory quantity, Production carrying value,
> Inventory valuation, reversal lineage, and Work Order material projections. The coding agent must
> implement the controls in this plan exactly and must not replace them with a simpler CRUD/posting flow.

---

# 0. Final review verdict


This plan has now been subjected to a **fresh red-team production-safety audit** against the live
`productionv2` branch at:

```text
61a4a66099061ef07157af15f8020cb257077420
```

The branch HEAD was re-read before this review and is unchanged from v4.

This review did not accept the previous plan merely because it had already been marked “10/10”.
The audit re-traced the live quantity and value paths from:

```text
Inventory Issue
    -> Production MATERIAL_IN
    -> Production consume / adjustment / return
    -> Inventory return receipt
    -> Inventory valuation
    -> rollback
    -> Work Order material reconciliation / completion
```

and re-checked the actual code contracts in:

- `ProductionStockWriter.cs`
- `ProductionPoolValuationService.cs`
- `ProductionMaterialMovementTotals.cs`
- `ProductionMaterialReconciliationService.cs`
- `ProductionMaterialIssueService.cs`
- `ProductionMaterialIssueService.Draft.cs`
- `ProductionMaterialIssueService.Posting.cs`
- `ProductionMaterialIssueService.Lifecycle.cs`
- `ProductionMaterialIssueService.Rollback.cs`
- `ProductionOutputService.Posting.cs`
- `ProductionWorkOrderService.Completion.cs`
- `ProductionBalLotConfiguration.cs`
- `ProductionMaterialMovementConfiguration.cs`
- `ProductionBalLotMovementConfiguration.cs`
- `ProductionPostingLink.cs` / configuration
- `IvInventoryPostingService.cs`
- `IIvInventoryPostingService.cs`
- `IvStockPostingRepository.cs`
- `IvTrxHistory` / configuration
- `IvTrxBatchDetail` / configuration
- `InventoryValuationService.cs`
- `StockPostingCoordinator.cs`
- `ProductionFinishedGoodReceiptService.Posting.cs`
- `ProductionFinishedGoodReceiptService.CostTrace.cs`
- `scripts/create-production-daily-output.sql`
- `scripts/alter-production-stock-ledger.sql`
- the current Production/UI agent skills.

### New critical findings from this audit

The v4 plan contained additional risks that are corrected in this v5:

1. **PRTN Inventory posting must reuse the existing in-transaction Inventory stock-in engine.**
   Hand-building `IvBalLoc` and `IvTrxHistory` would duplicate locking, lot, location, chronology and
   stock-slice rules already implemented by `PostStockInInTransactionAsync`.
2. **A missing/mismatched original Inventory balance is corruption, not a reason to create a new
   destination.** PRTN must return to the exact original `FromBalLocId` identity from the ISSUE chain
   or fail closed.
3. **Raw-material PADJ changes how much additional material may be issued.** A shortage adjustment
   must allow replacement issue; a positive adjustment must reduce further-issue allowance. Updating
   only Work Order completion reconciliation is not enough.
4. **Production financial authority is 6-decimal, while `IvTrxBatchDetail.UnitPrice` is only
   4-decimal.** PADJ manual cost authority must therefore live in its Production sidecar line, not in
   the generic Inventory detail price field.
5. **PADJ positive value must be calculated from exact total-value formulas first**, then derive unit
   cost. Multiplying by a rounded average unit cost can create silent value drift.
6. **PRTN/PADJ rollback is made strict per-pool LIFO.** If a later active Production movement exists
   on a touched pool, reverse that later document first. This avoids mixing valuation generations and
   stale contribution evidence.
7. **Work Order snapshot revision/hash is frozen on PRTN/PADJ drafts and revalidated at POST.** A
   change order between Save and POST cannot silently change the manufacturing contract.
8. **Open PRTN/PADJ drafts block Work Order completion.**
9. **Adjustment reversal movements must be collapsed by the existing effective-movement replay.**
10. **Deployment is fail-closed with posting feature flags and SQL preflight.**

After incorporating these corrections, this v5 plan is:

> **APPROVED FOR IMPLEMENTATION — 10/10 PLAN QUALITY**

This is approval of the implementation plan, **not approval of untested code**. Production activation
is permitted only after the mandatory SQL Server tests, reconciliation gates, and controlled rollout
defined below pass.

If `productionv2` moves away from the reviewed SHA before implementation, the agent must perform a
drift review of every touched production/inventory/costing contract before coding or deployment.

# 1. Critical corrections made during this final review

These are mandatory corrections to the earlier draft.

## 1.1 Do NOT add many production-only columns to `IvTrxBatchDetail`

The repo already shows the safer pattern:

- FG Receipt uses `IvTrxBatch` / `IvTrxBatchDetail` as the inventory document shell;
- production-specific source identity is kept in `PrFinishedGoodReceipt` / `PrFinishedGoodSource`;
- immutable cross-ledger evidence is kept in `PrFinishedGoodFact`.

Material Issue also uses a production sidecar mapping (`ProductionMaterialIssueLine`) around inventory detail rows.

Therefore the final design is:

```text
IvTrxBatch
IvTrxBatchDetail
        +
Production-specific sidecar header/line tables
        +
immutable posting fact tables
```

Do **not** put Work Order, ProductionBalLot, contribution identity, production UOM contract, etc. directly into the generic `IvTrxBatchDetail`.

The existing `IvTrxBatchDetail` fields are already sufficient for:
- item;
- Inventory return destination;
- quantity/UOM;
- reason/remarks;
- approved cost evidence fields where needed.

---

## 1.2 Every new Production Return / Production Adjustment document is single-Work-Order

`ProductionPostingLink` contains one non-null `WorkOrderId`.

Current FG Receipt is also one Work Order per document.

Therefore:

```text
ONE Production Stock Return document
    = ONE Work Order
    = one or more eligible raw-material production lots from that Work Order

ONE Production Stock Adjustment document
    = ONE Work Order
    = one or more production balances belonging to that Work Order
```

Do not allow one batch to mix Production stock from different Work Orders.

This avoids:
- ambiguous `ProductionPostingLink`;
- broken Work Order audit;
- mixed lineage;
- difficult rollback/invariant logic.

---

## 1.3 Production Return must use a PRODUCTION `StockPostingCommand`

Do **not** start the Production Return posting through:

`InventoryStockPostingCommandFactory`.

That factory sets:

```text
SourceModule = INVENTORY
```

Production Return is owned by Production.

Follow the proven FG Receipt approach:

```text
StockPostingCommand
SourceModule      = "PRODUCTION"
SourceDocumentType = PRODUCTION_STOCK_RETURN
```

The same applies to Production Stock Adjustment.

---

## 1.4 Backend reference for Production Return is FG Receipt, not Inventory Customer Return

UI may follow the Inventory Stock Return style.

Backend posting must follow the architecture proven in:

`ProductionFinishedGoodReceiptService.Posting.cs`

because FG Receipt is already the repo's exact:

```text
Production stock -> Inventory stock
quantity + exact value
one StockPosting
append-only reversal
```

Production Return is the same cross-ledger class of problem, but:
- source is `MATERIAL_IN`;
- destination must be the original Inventory slice;
- quantity provenance must remain tied to the original Issue-to-Production contribution.

Do not copy Sales Customer Return costing.

---

## 1.5 Rollback must NOT reopen and mutate the historical posted document

Generic Inventory MR/CR rollback returns the batch to NEW and advances detail generations.

That is unsuitable for this feature because Production Return will have immutable:
- Production material movement;
- Production stock movement;
- cross-ledger fact;
- source-line lineage.

Follow FG Receipt's safer lifecycle:

```text
NEW -> POSTED -> REVERSED
```

After REVERSED:
- original document remains read-only;
- optional `CREATE CORRECTION` creates a new NEW document.

Same lifecycle for Production Stock Adjustment.

This preserves historical detail identity.

---

## 1.6 Return contribution must be the original ISSUE contribution, not merely pool stock

`ProductionBalLot` is a pooled production balance.

It can later contain:
- original ISSUE quantity;
- `ADJUST_IN`;
- other valid inbound contributions.

Therefore:

```text
Returnable Qty != ProductionBalLot.BaseQty
```

Returnable quantity must come from the still-unallocated quantity of the specific original
`ProductionBalLotMovementTypes.Issue` receipt contribution.

This is enforced at:
- source search;
- Save;
- POST under locks;
- `ProductionPoolValuationService.RecordAsync`.

---


## 1.7 Follow the existing material dependency chain

Current Daily Production already links a raw-material `CONSUME` to its source ISSUE through:

```text
ProductionMaterialMovement.OriginalMovementId
```

Current Issue rollback discovers downstream material dependencies by recursively following that field.

Therefore a forward PRTN material RETURN must set:

```text
OriginalMovementId    = source ISSUE ProductionMaterialMovement.Uid
SourceIssueMovementId = source ISSUE ProductionMaterialMovement.Uid
```

The two fields serve different purposes:

- `OriginalMovementId` = executable dependency/reversal chain already used by the repo;
- `SourceIssueMovementId` = explicit source-issue audit identity.

A RETURN reversal must point `OriginalMovementId` at the original RETURN.

Do not create a parallel dependency mechanism that Issue rollback cannot see.

---

## 1.8 Raw-material PADJ must reconcile to Work Order material execution

`ProductionMaterialReconciliationService` currently proves:

```text
material execution movement balance
==
MATERIAL_IN ProductionBalLot balance
```

A PADJ against `MATERIAL_IN` therefore cannot be only a `ProductionBalLotMovement`.

It must also append:

```text
ProductionMaterialMovement
MovementType = ADJUST
```

linked to the exact `ADJUST_IN` / `ADJUST_OUT` production balance movement.

Reconciliation must include the signed **net active** adjustment amount.

Do not add a second Work Order material quantity field just for this. The immutable adjustment fact +
linked production movement are the authority.

---

## 1.9 Active Work Order status is mandatory for new PRTN/PADJ

The repo's Work Order completion gate already requires:
- zero unreconciled material balance;
- zero open WIP.

Therefore legitimate leftover production stock must be corrected/returned **before** completion.

For v1:

```text
Allowed Work Order status:
RELEASED
IN_PROGRESS

Rejected:
DRAFT
COMPLETED
CLOSED
CANCELLED
```

Enforce at:
- source search;
- Save;
- POST under lock.

Do not let these new stock-changing documents silently mutate a completed/closed Work Order.

Post-completion repair belongs to Costing/repair tooling or a future explicitly approved correction workflow.

---


## 1.10 Reuse the existing Inventory stock-in core for PRTN

PRTN is Production-owned, but its Inventory leg is still a normal Inventory stock-in operation.

Use:

```text
IIvInventoryPostingService.PostStockInInTransactionAsync(
    StockPostingContext,
    AppDbContext,
    company,
    branch,
    user,
    batchNo,
    IvTrxTypes.ProductionStockReturn)
```

inside the **same Production-owned StockPosting and SQL transaction**.

This existing core already owns:
- stock-master locking;
- warehouse/location/status validation;
- lot handling;
- deterministic stock-slice locking;
- stock-date chronology;
- `IvBalLoc` mutation;
- one `IvTrxHistory` row per detail;
- V2 history generation stamping.

Do not copy those rules into `ProductionStockReturnService`.

The Production service adds the parts the generic stock-in core does not know:
- exact source ISSUE eligibility;
- exact original Inventory destination verification;
- exact Production transferred value;
- Production RETURN movement;
- contribution restriction;
- Work Order material RETURN;
- cross-ledger ReturnFact/invariant.

---

## 1.11 The original Inventory destination must already exist and exactly match ISSUE authority

The original ISSUE history has a restricted FK to `IvBalLoc`.

For a valid PRTN source require all of these to identify the same row:

```text
ProductionBalLot.SourceIvBalLocId
ProductionMaterialMovement ISSUE.FromBalLocId
ISSUE IvTrxHistory.FromBalLocId
```

Lock that exact `IvBalLoc` by ID.

Then verify its current immutable stock-slice identity still matches the ISSUE history:

```text
company
branch
item
warehouse
location
lot
item status
base UOM
```

If the row is missing or the identity does not match:

```text
FAIL CLOSED
```

Do not use `FindOrCreateBalLocAsync` to manufacture a “replacement original location”.
That is a reconciliation/corruption case.

For lot-controlled stock, also require the exact original lot identity to remain valid.

---

## 1.12 Raw-material PADJ must affect Issue-to-Production allowance

A raw-material PADJ changes actual material physically available in Production.

Keep these audit facts distinct:

```text
IssuedQty   = actual Inventory -> Production issue
ReturnedQty = actual Production -> Inventory return
ConsumedQty = actual Daily Production consumption
AdjustmentQty = Production-only physical correction
```

Do **not** rewrite `IssuedQty` to hide an adjustment.

But Issue-to-Production eligibility must use:

```text
EffectiveSupplyQty (Work Order RequiredUom)
= EffectiveIssuedQty
- EffectiveReturnedQty
+ NetActiveMaterialAdjustmentQty
```

Examples:

```text
Required 100
Issued   100
PADJ OUT 20 shortage
=> EffectiveSupply = 80
=> replacement issue up to 20 is allowed
```

and:

```text
Required 100
Issued    80
PADJ IN   20 found stock
=> EffectiveSupply = 100
=> further issue is no longer allowed merely because IssuedQty is still 80
```

Consumption is deliberately not subtracted from the issue-cap formula: consuming already-issued
material does not mean the business should re-issue the same BOM requirement.

Work Order physical remaining stock uses the separate equation:

```text
RemainingProductionMaterialBaseQty
= IssuedBaseQty
- ReturnedBaseQty
- ConsumedBaseQty
+ NetAdjustmentBaseQty
```

---

## 1.13 Production 6-decimal money is authoritative

Current repo precision:

```text
IvTrxBatchDetail.UnitPrice / Cost / CostPrice  decimal(18,4)
IvTrxHistory.UnitPrice                         decimal(18,4)

Production movement UnitCost / TotalCost       decimal(19,6)
IvTrxHistory.ExactTransferredValue             decimal(19,6)
Production fact values                         decimal(19,6)
```

Therefore:
- PRTN financial authority = `ExactTransferredValue` + immutable ReturnFact, never detail UnitPrice;
- PADJ approved/manual Production cost must be stored at 19,6 in the Production adjustment sidecar,
  not only in `IvTrxBatchDetail.UnitPrice`;
- display prices may be rounded, but exact total value cannot be reconstructed from them.

---

## 1.14 Rollback is strict per-pool LIFO

For PRTN and PADJ rollback, if any later **active forward** `ProductionBalLotMovement` exists on a
touched Production pool after the original posting sequence:

```text
ROLLBACK BLOCKED
```

The operator must roll back the later document first.

This deliberately includes later inbound movements, not only outflows.

Reason: a later inbound may start a new valuation generation after the earlier movement emptied a
pool. Reversing an older movement underneath it could combine generations or invalidate provenance.

`HasActiveDependentsAsync` remains an additional dependency gate.

---

## 1.15 Freeze Work Order manufacturing snapshot on the draft

Both PRTN and PADJ headers store:

```text
SnapshotRevision
SnapshotHash
```

captured at Save.

POST locks the Work Order and requires both values to still match.

If a change order or snapshot change occurred:

```text
reject POST
reload/re-save/review the transaction
```

A correction document captures the **current** snapshot, not the old reversed snapshot.

---

## 1.16 Fail-closed rollout

Add posting feature switches:

```text
ProductionStockReturnPostingEnabled
ProductionStockAdjustmentPostingEnabled
```

Default them to disabled for first deployment.

They block new POST only. They must not block:
- viewing;
- inquiry/reconciliation;
- rollback of already-posted documents.

Enable only after SQL preflight, reconciliation and integration tests pass.

---


# 2. Business scope

Implement two Production transactions.

## 2.1 Production Stock Return

Purpose:

Return unused raw material from Production back to Inventory.

Allowed source:

```text
Inventory stock
 -> Issue to Production
 -> Production MATERIAL_IN
 -> still-unused ISSUE contribution
```

Not allowed:
- WIP;
- route output;
- FG staging;
- opening-only Production stock;
- quantity created by Production Stock Adjustment;
- unrelated contribution from the same item/pool.

Posting effect:

```text
Production quantity     DOWN
Production value        DOWN
Inventory quantity      UP
Inventory valuation     UP
Work Order ReturnedQty  UP
```

All effects are one atomic transaction.

---

## 2.2 Production Stock Adjustment

Purpose:

Correct physical Production stock quantity.

It affects Production stock only.

Posting effect:

```text
Production quantity/value only
```

It must never:
- update `IvBalLoc`;
- insert `IvTrxHistory`;
- create Inventory valuation facts.

Allowed:
- material production balances;
- WIP / route production balances;
- FG staging production balances;

provided they belong to the document Work Order and pass valuation/readiness rules.

---

# 3. Existing repo authorities — do not bypass

## 3.1 Inventory quantity authority

```text
IvBalLoc
```

## 3.2 Inventory immutable movement authority

```text
IvTrxHistory
```

V2 history is append-only via:

`IIvInventoryHistoryWriter`

## 3.3 Inventory financial authority

```text
StockValuationFact
StockCostState
StockFifoLayer
...
```

through:

`InventoryValuationService`

Supported financial methods:
- Moving Average;
- FIFO;
- Standard Cost.

---

## 3.4 Production quantity/value projection

```text
ProductionBalLot
```

It already carries:
- Qty/BaseQty;
- UOM/BaseUOM/conversion;
- TotalCost/AverageUnitCost;
- Work Order;
- Work Order Material;
- OriginalIssueMovementId;
- SourceIvBalLocId;
- production location/status/pool/lot dimensions.

Do not introduce a second production balance table.

---

## 3.5 Production immutable stock movement

```text
ProductionBalLotMovement
```

Existing movement codes already include:

```text
RETURN
RETURN_REVERSAL
ADJUST_IN
ADJUST_OUT
```

Do not invent alternative movement names.

---

## 3.6 Only Production quantity/value writer

Use:

`IProductionStockWriter`

All PRTN/PADJ production quantity changes must go through:

`ProductionStockWriter.ApplyAsync`.

Never directly change ProductionBalLot quantity/value in these new services.

---

## 3.7 Production valuation/provenance authority

Use:

`ProductionPoolValuationService`

It owns:
- production pool tracked qty/value;
- VERIFIED / UNVALUED;
- receipt-to-outbound contribution allocation;
- dependency graph;
- reversal allocations;
- downstream dependency detection.

Extend this service minimally; do not create a second Production costing engine.

---

# 4. New transaction/constants

## 4.1 Inventory batch transaction types

Add to:

`ErpWeb.Core/Inventory/IvTrxConstants.cs`

```csharp
public const string ProductionStockReturn = "PRTN";
public const string ProductionStockAdjustment = "PADJ";
```

Both fit the current `nvarchar(20)` TrxType schema.

Do not reuse:
- CR;
- MR;
- ADJ.

---

## 4.2 Production document types

Add to `ProductionDocumentTypes`:

```csharp
public const string ProductionStockReturn = "PRODUCTION_STOCK_RETURN";
public const string ProductionStockAdjustment = "PRODUCTION_STOCK_ADJUSTMENT";
```

Both fit the current ProductionPostingLink document-type length.

---

## 4.3 Posting command types

Add to `ProductionPostingCommandTypes`:

```text
PRODUCTION_STOCK_RETURN_POST
PRODUCTION_STOCK_RETURN_ROLLBACK
PRODUCTION_STOCK_ADJUSTMENT_POST
PRODUCTION_STOCK_ADJUSTMENT_ROLLBACK
```

Check max length against `ProductionPostingLink.CommandType` (40).

---

## 4.4 Production material movement type

Current `ProductionMaterialMovementTypes` contains:

```text
RETURN
```

Add:

```csharp
public const string ReturnReversal = "RETURN_REVERSAL";
```

Update:
- entity constraint;
- SQL migration constraint;
- tests.

Do not use `ISSUE_REVERSAL` to represent a Production Return rollback.

---

## 4.5 Audit event types

Add:

```text
MATERIAL_RETURNED
MATERIAL_RETURN_ROLLED_BACK
PRODUCTION_STOCK_ADJUSTED
PRODUCTION_STOCK_ADJUSTMENT_ROLLED_BACK
```

Use existing `ProductionAuditEvent`.

---


## 4.6 Controlled rollout options

Add a focused options class, e.g.:

```text
ProductionStockTransactionOptions
```

with:

```text
ReturnPostingEnabled
AdjustmentPostingEnabled
```

Initial deployment defaults both to false.

POST checks the corresponding flag and exposes a readiness reason in the UI.

ROLLBACK must ignore the forward-post enable flag.

---


# 5. Final data model — Production Stock Return

Follow FG Receipt's sidecar/fact pattern.

## 5.1 `ProductionStockReturn`

Recommended table:

```text
PrProductionStockReturn
```

Fields:

```text
BatchId                 int PK/FK -> IvTrxBatch.ID
CompanyCode             nvarchar(5)
BranchCode              nvarchar(5)
WorkOrderId             bigint
SnapshotRevision        int
SnapshotHash            varchar(64)
DocumentRevision        int
RowVersion              rowversion
PostingId               bigint null -> StockPosting
ReversalPostingId       bigint null -> StockPosting
CorrectedBatchId        int null -> PrProductionStockReturn
ReversalReason          nvarchar(500) null
DeletedAtUtc            datetime2 null
DeletedBy               nvarchar(10) null
DeleteReason            nvarchar(250) null
```

Rules:
- one batch = one Work Order;
- `PostingId` unique when not null;
- `ReversalPostingId` unique when not null.

---

## 5.2 `ProductionStockReturnLine`

Recommended table:

```text
PrProductionStockReturnLine
```

Fields:

```text
Id                      bigint identity PK
BatchId                 int FK -> PrProductionStockReturn
DetailId                int FK -> IvTrxBatchDetail, unique
ProductionBalLotId      bigint FK -> PrProductionBalLot
SourceIssueMovementId   bigint FK -> PrMaterialMovement
RequestedQty            decimal(18,4)
SourceUom               nvarchar(10)
BaseUom                 nvarchar(10)
SourceFactor            decimal(18,8)
BaseQty                 decimal(18,4)
```

Important:
- one source ProductionBalLot may appear only once in a document;
- `SourceIssueMovementId` is the authoritative original `ProductionMaterialMovement ISSUE`;
- do not freeze cost here;
- cost is calculated at POST from the locked production pool.

`IvTrxBatchDetail` stores:
- item/description;
- exact original Inventory destination WH/location/lot;
- `ToStdQty = BaseQty`;
- `ToStdUom = BaseUom`;
- item status;
- reason;
- remarks.

The destination fields are read-only to the user.

---

## 5.3 `ProductionStockReturnFact`

Recommended table:

```text
PrProductionStockReturnFact
```

Immutable fields:

```text
Id                          bigint identity PK
BatchId                     int
ReturnLineId                bigint
StockPostingId              bigint
ProductionMovementId        bigint -> PrProductionBalLotMovement
ProductionMaterialMovementId bigint -> PrMaterialMovement
InventoryHistoryId          int -> IvTrxHistory
DestinationBalanceId        int -> IvBalLoc
SourceIssueMovementId       bigint -> PrMaterialMovement
BaseQty                     decimal(18,4)
TotalValue                  decimal(19,6)
ReversesFactId              bigint null
```

Unique/index rules:

```text
(StockPostingId, ReturnLineId) unique
ProductionMovementId unique
ProductionMaterialMovementId unique
InventoryHistoryId unique
ReversesFactId unique filtered when not null
```

Purpose:

Prove exactly:

```text
one return line
 -> one Production RETURN movement
 -> one Production material RETURN fact
 -> one Inventory receipt history
 -> one exact transferred value
```

Rollback appends another fact with `ReversesFactId`.

---

## 5.4 `ProductionStockReturnPriceSnapshot`

Recommended table:

```text
PrProductionStockReturnPriceSnapshot
```

Use only when PRTN changes operational `IvBalLoc` price evidence.

Fields mirror FG pattern:

```text
Id
StockPostingId
DestinationBalanceId
PreviousUnitPrice
PreviousCost
PreviousPriceEvidence
PostedUnitPrice
PostedCost
PostedPriceEvidence
```

Unique:

```text
(StockPostingId, DestinationBalanceId)
```

See §§13 and 25 for capture/rollback rules.

---

# 6. Final data model — Production Stock Adjustment


## 6.1 `ProductionStockAdjustment`

Recommended table:

```text
PrProductionStockAdjustment
```

Lifecycle shell:

```text
BatchId                 int PK/FK -> IvTrxBatch
CompanyCode             nvarchar(5)
BranchCode              nvarchar(5)
WorkOrderId             bigint
SnapshotRevision        int
SnapshotHash            varchar(64)
DocumentRevision        int
RowVersion              rowversion
PostingId               bigint null
ReversalPostingId       bigint null
CorrectedBatchId        int null
ReversalReason          nvarchar(500) null
DeletedAtUtc
DeletedBy
DeleteReason
```

One PADJ document belongs to one Work Order.

---

## 6.2 `ProductionStockAdjustmentLine`

Recommended table:

```text
PrProductionStockAdjustmentLine
```

Fields:

```text
Id                  bigint identity PK
BatchId             int FK -> PrProductionStockAdjustment
DetailId            int FK -> IvTrxBatchDetail, unique
ProductionBalLotId  bigint FK -> PrProductionBalLot

AdjustmentQty       decimal(18,4)   // signed Production-UOM quantity
SourceUom           nvarchar(10)
BaseUom             nvarchar(10)
SourceFactor        decimal(18,8)
BaseQty             decimal(18,4)   // absolute base quantity

ProposedUnitCost    decimal(19,6) null
CostEvidenceType    nvarchar(30) null
CostOverrideReason  nvarchar(250) null
CostApprovedBy      nvarchar(10) null
CostApprovedAtUtc   datetime2(7) null
```

Direction:

```text
AdjustmentQty > 0 -> ADJUST_IN
AdjustmentQty < 0 -> ADJUST_OUT
0                 -> invalid
```

### Why cost approval is on this sidecar

`IvTrxBatchDetail.UnitPrice` is only `decimal(18,4)`.
Production financial values are `decimal(19,6)`.

Therefore `IvTrxBatchDetail.UnitPrice` must **not** be PADJ financial authority.

Use the sidecar 19,6 `ProposedUnitCost` only for explicit:
- `MANUAL_APPROVED`;
- `ZERO_COST_APPROVED`.

Automatic cost bases are re-resolved at POST and do not trust a saved UI unit price.

On any draft edit affecting:
- source;
- quantity;
- transaction date;
- proposed unit cost;
- evidence type;
- reason;

clear/revalidate approval metadata and require the server to authorize it again.

### Generic `IvTrxBatchDetail` usage for PADJ

Keep only generic document-line identity/display fields:

```text
TrxType = PADJ
ICode / IDesc
Reason
Remarks
TrxLineNo
DocumentRevision
```

Do not set:
- FromBalLocId;
- ToBalLocId;
- inventory From/To quantity/location/lot fields as stock authority.

All Production stock identity and quantity live in the Production sidecar line.

This prevents generic Inventory tooling from mistaking PADJ for an Inventory movement.

---

## 6.3 `ProductionStockAdjustmentFact`

Recommended immutable table:

```text
PrProductionStockAdjustmentFact
```

Fields:

```text
Id
BatchId
AdjustmentLineId
StockPostingId
ProductionMovementId

BaseQty                     decimal(18,4)
TotalValue                  decimal(19,6)
UnitCost                    decimal(19,6)
CostBasisCode               nvarchar(40)

OriginalIssueMovementId     bigint null FK -> PrMaterialMovement
StandardCostRevisionId      bigint null FK -> ItemStandardCostRevision

CostEvidenceType            nvarchar(30) null
CostOverrideReason          nvarchar(250) null
CostApprovedBy              nvarchar(10) null
CostApprovedAtUtc           datetime2(7) null

ReversesFactId              bigint null
```

Do not use one polymorphic `cost-authority reference` for unrelated table types.

Authority contract:

### `CURRENT_PRODUCTION_POOL_COST`

```text
OriginalIssueMovementId = null
StandardCostRevisionId  = null
manual approval fields  = null
```

### `ORIGINAL_ISSUE_COST`

```text
OriginalIssueMovementId = exact source ISSUE material movement UID
StandardCostRevisionId  = null
```

### `STANDARD_COST`

```text
StandardCostRevisionId  = exact approved revision Id
OriginalIssueMovementId = null
```

### `MANUAL_APPROVED` / `ZERO_COST_APPROVED`

Freeze the server-approved evidence into the immutable fact:

```text
CostEvidenceType
CostOverrideReason
CostApprovedBy
CostApprovedAtUtc
```

This prevents historical costing audit from depending only on a mutable draft projection.

Allowed basis:

```text
CURRENT_PRODUCTION_POOL_COST
ORIGINAL_ISSUE_COST
STANDARD_COST
MANUAL_APPROVED
ZERO_COST_APPROVED
REVERSAL
```

For `REVERSAL`, exact original authority remains reachable through `ReversesFactId`; do not invent a
new cost authority.

# 7. AppDbContext ownership/write guards


Follow current FG protection but minimize regression scope.

Add scoped capability flags:

```text
ProductionStockReturnWrite
ProductionStockAdjustmentWrite
```

Only the owning services set them.

Do **not** broadly rename/rewrite the existing `ValidateFinishedGoodWrites()` logic just to fit these
features.

Instead add a focused:

```text
ValidateProductionStockTransactionWrites()
```

and call it from the existing SaveChanges/SaveChangesAsync pipeline alongside the FG guard.

Reject generic writes to:
- ProductionStockReturn header/line;
- PRTN `IvTrxBatch` / current detail generation;
- ProductionStockAdjustment header/line;
- PADJ `IvTrxBatch` / current detail generation.

New facts are immutable:
- no UPDATE;
- no DELETE;
- correction/reversal = append new linked fact.

Add the new immutable fact types to the existing immutable-evidence guard or an equally focused new
guard.

Do not weaken:
- existing FG write ownership;
- SQL seal triggers;
- StockPosting sealing rules.

# 8. Production Return source eligibility

Create an authoritative source query/service, e.g.:

```text
IProductionStockReturnSourceService
ProductionStockReturnSourceService
```

A source is eligible only if all are true.

## 8.1 Production balance identity


The source Work Order must be in:

```text
RELEASED
IN_PROGRESS
```

and the Production balance must satisfy:

```text
ProductionBalLot.CompanyCode == scope
ProductionBalLot.BranchCode  == scope
ProductionBalLot.WorkOrderId == document WorkOrderId
ProductionBalLot.Kind        == MATERIAL_IN
ProductionBalLot.BaseQty     > 0
ProductionBalLot.OriginalIssueMovementId != null
ProductionBalLot.WorkOrderMaterialId != null
```

Reject Draft, Completed, Closed and Cancelled Work Orders.

This aligns with current Work Order completion logic: unused material prevents completion, so a valid
material return must occur while the Work Order is still active.

## 8.2 Original issue movement


Resolve:

```text
ProductionBalLot.OriginalIssueMovementId
    -> ProductionMaterialMovement ISSUE
    -> ProductionBalLotMovement ISSUE
```

Require all of the following:

```text
material ISSUE.MovementType == ISSUE
material ISSUE.WorkOrderId == pool.WorkOrderId
material ISSUE.WorkOrderMaterialId == pool.WorkOrderMaterialId
material ISSUE.ProductionBalLotId == pool.Uid
material ISSUE.ProductionBalLotMovementId != null

balance ISSUE.MovementType == ISSUE
balance ISSUE.ProductionBalLotId == pool.Uid
balance ISSUE.WorkOrderId == pool.WorkOrderId
balance ISSUE.WorkOrderMaterialId == pool.WorkOrderMaterialId

material ISSUE.InventoryHistoryId == balance ISSUE.InventoryHistoryId

pool.SourceIvBalLocId
== material ISSUE.FromBalLocId
== ISSUE history.FromBalLocId
```

Also require:
- item identity matches;
- BaseUOM matches;
- conversion factor matches;
- ISSUE is V2/sealed/VERIFIED;
- no active ISSUE reversal exists.

A broken chain is not “best effort”. Fail the PRTN source and route it to reconciliation/repair.

## 8.3 Source Inventory history


The original ISSUE's `InventoryHistoryId` must resolve to an active V2 Issue-to-Production history:

```text
TrxType == IP
EntryRole == ORIGINAL
not actively reversed
FromBalLocId present
FrStdQty > 0
FrStdUom present
```

Lock the exact original `FromBalLocId` under the posting transaction and require its current key to
still equal the ISSUE history:

```text
company / branch
item
warehouse
location
lot
item status
StdUom/BaseUom
```

Do not substitute a different balance row with the same text labels.

For lot-controlled material require:
- ISSUE `FromLotId` present;
- material movement `LotId` matches;
- the exact `IvLot` exists;
- item + lot number + expiry identity are consistent;
- lot is usable according to current Inventory receipt rules on the return date.

If the exact original `IvBalLoc`/lot authority is missing or inconsistent:

```text
PRTN POST = REJECT
```

Do not create replacement authority.

## 8.4 Production cost readiness


Require:

`ProductionCostReadiness.PoolValuationError(...) == null`

and additionally verify the live scalar contract:

```text
ConversionFactorToBase > 0

Qty
== IvQty.Round(BaseQty / ConversionFactorToBase)

if BaseQty > 0:
    AverageUnitCost
    == Money(TotalCost / BaseQty)

if BaseQty == 0:
    Qty == 0
    TotalCost == 0
    AverageUnitCost == 0
```

Reject:
- UNVALUED pool;
- qty/value projection mismatch;
- stranded value;
- quantity/UOM projection mismatch;
- average-cost scalar mismatch.

Do not silently repair these values during Return.

# 9. Authoritative returnable-quantity calculation

This is one of the most important controls.

## 9.1 Required receipt contribution

For a MATERIAL_IN source:

```text
ProductionMaterialMovement ISSUE
 -> ProductionBalLotMovementId
 -> ProductionBalLotMovement ISSUE
```

That `ProductionBalLotMovement ISSUE` is the only contribution PRTN may return.

---

## 9.2 Do not duplicate contribution math

Current `ProductionPoolValuationService.RecordAsync` already calculates available inbound contributions using:

- original receipt quantity;
- reversal movement quantity;
- `ProductionMovementAllocation`;
- reversal allocations;
- current generation.

Extract/refactor that calculation into one reusable internal authority, e.g.:

```text
ProductionContributionAvailabilityService
```

or an equivalent pure helper used by both:

- `ProductionPoolValuationService`;
- Production Return search/save/post.

There must be ONE contribution-availability formula.

Do not implement a second approximation in the UI service.

---

## 9.3 Returnable quantity

Conceptually:

```text
original ISSUE receipt contribution
- net active allocations from that ISSUE receipt
- active receipt reversal effect
= remaining ISSUE contribution
```

For RETURN:

```text
ReturnableBaseQty
= remaining contribution of the original ISSUE receipt
```

Also require:

```text
ReturnableBaseQty <= ProductionBalLot.BaseQty
```

---

## 9.4 Current generation

The original ISSUE receipt must belong to the current pool valuation generation.

If the pool previously reached zero and a later ADJUST_IN started another generation:

```text
old ISSUE contribution is NOT returnable
```

even if the same ProductionBalLot row later has quantity again.

---

## 9.5 Adjustment does not increase ReturnableQty

Example:

```text
original ISSUE receipt contribution  = 100
ADJUST_IN                             = +20
current pool BaseQty                  = 120

Returnable from Inventory ISSUE       = max 100
```

If 80 of original ISSUE contribution is consumed/adjusted out:

```text
Returnable = 20
```

A request for 30 fails even though pool BaseQty may be 40.

---

# 10. Production Return draft/save workflow


## 10.1 Work Order first

User selects one Work Order.

The document is single-Work-Order and that identity becomes locked after source selection.

Only show eligible Production stock from that Work Order.

---

## 10.2 Save-time Work Order lock / completion race protection

SAVE is inside a transaction.

Lock the Work Order using the same SQL Server pattern already used by Material Issue draft:

```text
UPDLOCK, HOLDLOCK
```

before accepting/refreshing the draft.

Require:
- status RELEASED or IN_PROGRESS;
- current valid nonblank snapshot identity;
- request/header SnapshotRevision + SnapshotHash match the locked Work Order.

This serializes PRTN draft Save against Work Order completion.

Without this lock, a completion/save race could leave a new stock-changing draft attached to a Work
Order that just became Completed.

---

## 10.3 Source picker

Show:
- Work Order;
- material item / description;
- Production lot;
- Production location;
- Production stock qty;
- Returnable original-ISSUE qty;
- UOM;
- original Issue-to-Production batch;
- original Inventory warehouse/location/lot;
- readiness.

Cost/value requires `ViewCost`.

Server paging/bounded lookup is mandatory.

---

## 10.4 Destination is system-controlled

Populate destination snapshot from original ISSUE history:

```text
ToWarehouse = original FrWarehouse
ToLocation  = original FrLocation
ToLotNo     = original FrLotNo
ToStdUom    = original FrStdUom / source BaseUom
IStatus     = original IStatus
```

User cannot edit destination.

No Inventory destination picker in v1.

Different bin requirement:

```text
PRTN to original Inventory slice
then normal Inventory Stock Transfer
```

---

## 10.5 Lot rule

For lot-controlled material:
- exact original IvLot only;
- item/lot/expiry identity must match original ISSUE authority;
- missing/invalid authority fails closed;
- current Inventory receipt validity applies.

Expired/invalid material is not silently returned to usable stock; use the approved Production
adjustment/write-off path as appropriate.

---

## 10.6 Quantity conversion

`RequestedQty` is Production material UOM.

```text
BaseQty = IvQty.Round(RequestedQty * SourceFactor)
```

Require exact four-decimal round trip:

```text
IvQty.Round(BaseQty / SourceFactor) == RequestedQty
```

The material source UOM must be the Work Order material RequiredUom for MATERIAL_IN.

---

## 10.7 Save validation

Under the Work Order lock:
- same Work Order;
- valid snapshot;
- no duplicate ProductionBalLotId;
- source scalar contract valid;
- exact source ISSUE chain valid;
- qty > 0;
- BaseQty > 0;
- BaseQty <= current Production pool BaseQty;
- BaseQty <= current original-ISSUE contribution;
- reason required;
- OTHER needs remark;
- UOM/factor frozen.

Save/update:
- PRTN batch/detail;
- PRTN sidecar header/line.

No physical stock/value changes on Save.

Save-time available/returnable quantities remain advisory; POST recalculates under stock locks.

# 11. Production Return exact value

Production-side valuation is pooled-average based in the current repo.

For each locked source pool:

```text
return value = proportional share of current verified pool TotalCost
```

Use the same proven allocation algorithm as:

`FinishedGoodReceiptMath.AllocateValue`.

Do not invent a different rounding algorithm.

For one line per pool:

```text
if BaseQty == pool.BaseQty:
    exact value = pool.TotalCost
else:
    exact value = Money(pool.TotalCost * BaseQty / pool.BaseQty)
```

If implementation permits multiple lines from one pool, use `AllocateValue` so the final line absorbs exact money rounding.

Do not calculate from UI-rounded `UnitPrice`.

---

# 12. Production Return POST — exact transaction design


Primary architectural references:

```text
ProductionFinishedGoodReceiptService.Posting.cs
IIvInventoryPostingService.PostStockInInTransactionAsync(...)
```

FG Receipt remains the reference for Production-owned cross-ledger orchestration.
The generic Inventory stock-in core remains the authority for the Inventory physical receipt leg.

Do not refactor FG Receipt during this feature unless a shared helper is separately proven by
regression tests.

## 12.1 Request/idempotency

POST command DTO:

```text
DocumentId
ExpectedRowVersion
RequestId : Guid
```

RequestId is stable across retry.

Replay handling follows StockPostingCoordinator / FG pattern:
- same RequestId + same fingerprint + sealed = replay success;
- same RequestId + different semantic input = fail;
- existing unsealed posting = reconciliation required.

---

## 12.2 Transaction start

1. authorize Access + Post;
2. verify `ProductionStockReturnPostingEnabled`;
3. create DbContext;
4. set `ProductionStockReturnWrite = true`;
5. begin SQL transaction;
6. acquire branch stock transaction lock;
7. replay check;
8. lock PRTN header/batch;
9. verify RowVersion;
10. require `NEW`;
11. reject future effective date.

---

## 12.3 Work Order / snapshot gate

Lock Work Order.

Require:

```text
Status in RELEASED | IN_PROGRESS
header.SnapshotRevision == WorkOrder.SnapshotRevision
header.SnapshotHash     == WorkOrder.SnapshotHash
```

If snapshot changed since Save:
- reject;
- reload/re-save/review source and quantities.

The snapshot revision/hash are included in the StockPosting fingerprint and ProductionPostingLink.

---

## 12.4 Locking / exact source revalidation

Use deterministic order:

1. Work Order;
2. source ProductionBalLot rows ascending UID;
3. source ISSUE/material lineage;
4. Inventory stock masters by item code;
5. exact original `FromBalLocId` rows / lot identities;
6. normal Inventory stock-in core performs canonical `IvStockSliceKey` locking.

Under lock recalculate:
- Production scalar contract;
- cost readiness;
- current valuation generation;
- exact remaining source ISSUE contribution;
- quantity/UOM round-trip;
- original Inventory slice identity;
- exact return value.

Do not rely on Save-time “available” values.

---

## 12.5 Chronology

Production source guard:

```text
pool.LastStockEventEffectiveAt <= return effective date
pool.LastMovementDate          <= return effective date
```

Inventory destination chronology must follow the **existing Inventory stock-in core**, not a new
stricter custom rule.

Current core permits same-day history and rejects a later stock day. Keep that repository contract.

Do not add an independent “any later timestamp on same day” forward-post blocker in PRTN.

Rollback uses stronger active-posting sequence rules; see §24.

---

## 12.6 StockPosting command

Build a Production-owned command:

```text
SourceModule       = PRODUCTION
SourceDocumentType = PRODUCTION_STOCK_RETURN
SourceDocumentId   = BatchId
SourceDocumentNo   = BatchNo
PostingRole        = PRIMARY
EffectiveAt        = batch transaction date
```

Fingerprint includes at minimum:

```text
document id
row version
command type
document revision
snapshot revision/hash
effective date
WorkOrderId

each line:
  ReturnLineId
  ProductionBalLotId
  SourceIssueMovementId
  RequestedQty
  BaseQty
  SourceUom
  BaseUom
  SourceFactor
  exact original Inventory BalLocId
  warehouse/location/lot/status
```

Begin through:

`IStockPostingCoordinator.BeginInTransactionAsync`.

V2 ledger is mandatory.
No legacy fallback.

Do not use `InventoryStockPostingCommandFactory` for this command because that would incorrectly make
the posting source module `INVENTORY`.

---

## 12.7 ProductionPostingLink

Create PENDING link:

```text
WorkOrderId
CommandType           = PRODUCTION_STOCK_RETURN_POST
PostingRequestId      = RequestId N-format
ProductionDocumentType = PRODUCTION_STOCK_RETURN
ProductionDocumentNo = BatchNo
InventoryBatchNo      = BatchNo
SnapshotRevision      = header SnapshotRevision
SnapshotHash          = header SnapshotHash
```

Set:

`context.Posting.ProductionPostingLinkId = link.Uid`.

Add a DB uniqueness rule for the primary PRTN post command/batch identity so an application bug cannot
create two primary production links for one PRTN batch.

# 13. Inventory leg of PRTN


Do not call Customer Return costing.
Do not manually reproduce Inventory stock-in mechanics.

Use the existing in-transaction stock-in core in the same Production StockPosting.

## 13.1 Exact original destination — fail closed

Before invoking the Inventory core, prove the destination is the original ISSUE balance.

Require:

```text
pool.SourceIvBalLocId
== material ISSUE.FromBalLocId
== ISSUE history.FromBalLocId
== locked IvBalLoc.Id
```

and exact key equality for:

```text
company
branch
item
warehouse
location
lot
item status
base/standard UOM
```

The PRTN detail destination is frozen to those exact values.

For lot-controlled material:
- use the exact original `FromLotId`;
- preserve original expiry identity;
- require current lot validity.

A missing/mismatched balance/lot is a reconciliation error.

**Do not create a replacement “original” Inventory balance.**

---

## 13.2 Prepare PRTN detail for generic stock-in

Before calling the stock-in core:

```text
detail.TrxType     = PRTN
detail.ICode       = source item
detail.ToWarehouse = exact original warehouse
detail.ToLocation  = exact original location
detail.ToLotNo     = exact original lot
detail.ToStdQty    = BaseQty
detail.ToStdUom    = BaseUom
detail.IStatus     = exact original item status
detail.ExpiryDate  = exact original lot expiry where applicable
```

For operational display only, detail UnitPrice may store the 4-decimal representation of:

```text
exact return TotalValue / BaseQty
```

and `PriceEvidence` may identify the PRTN receipt.

But:

```text
IvTrxBatchDetail.UnitPrice is NOT financial authority
```

because it is only decimal(18,4).

Exact Production value is held separately in memory/fact and is stamped to history at 19,6 after the
Inventory stock-in core creates the history row.

---

## 13.3 Capture operational price snapshot before stock-in

For every destination `IvBalLoc`, capture:

```text
PreviousUnitPrice
PreviousCost
PreviousPriceEvidence
```

before the stock-in core mutates it.

A single destination may aggregate several PRTN lines; snapshot one row per StockPosting +
DestinationBalance.

---

## 13.4 Execute existing Inventory stock-in core

Call:

```text
_inventoryPosting.PostStockInInTransactionAsync(
    context,
    db,
    company,
    branch,
    user,
    batchNo,
    IvTrxTypes.ProductionStockReturn,
    ct)
```

This core owns:
- item/warehouse/location/status validation;
- lot handling;
- stock-slice locking;
- stock-date chronology;
- `IvBalLoc` quantity;
- operational balance price-evidence behavior;
- creation of one `IvTrxHistory` per detail;
- V2 history stamping.

If it fails, abort the entire Production transaction.

Immediately after success assert, per line:

```text
detail.ToBalLocId == original ISSUE FromBalLocId

if lot controlled:
    detail.ToLotId == original ISSUE FromLotId
```

If the generic core resolves anything else, fail the outer transaction. Never accept a “similar”
destination.

Do not call the public standalone Inventory dispatcher for PRTN.
PRTN remains Production-owned and must use the caller-owned transaction overload only.

Update the interface XML comments so they no longer misleadingly say this generic in-transaction
stock-in overload is limited to MR/CR.

---

## 13.5 Stamp exact Production transfer authority onto PRTN history

After the stock-in core succeeds and its new V2 histories are available, map each history by stable
document line.

Set:

```text
history.ExactTransferredValue = exact Production return TotalValue  // decimal(19,6)
history.EvidenceBaseQty        = line BaseQty
history.EvidenceBaseUom        = line BaseUom
history.PriceEvidence          = PRTN_EXACT_BASE_CURRENCY
```

Do **not** set history `ValuationStatus = VERIFIED`.

Inventory valuation owns history valuation status.
After `ValuePostingInTransactionAsync`, the forward receipt must be:

```text
ValuationStatus = VALUED
```

The PRTN exact transfer amount is independent of its 4-decimal display UnitPrice.

---

## 13.6 Capture post stock-in operational price state

After the generic stock-in core has applied its normal operational `IvBalLoc` logic, store:

```text
PostedUnitPrice
PostedCost
PostedPriceEvidence
```

beside the pre-post snapshot.

Rollback may restore previous operational fields only if current fields still exactly equal these
captured posted values.

Financial authority remains:
- StockValuationFact;
- StockCostState/FIFO layer;
- ExactTransferredValue.

---

## 13.7 Pre-seal Inventory valuation

Call:

`IIvInventoryPostingService.ValuePostingInTransactionAsync(context)`.

This delegates to the same `StockPostingCoordinator.ValuePendingInTransactionAsync`.

It is safe to call before the material RETURN row is inserted because the existing
`SynchronizeProductionMaterialCostAsync` only synchronizes outbound:

```text
PRODUCTION_MATERIAL_OUT
```

ISSUE facts.

PRTN is an inbound receipt.

Calling `CompleteInTransactionAsync` later is safe: after valuation facts have been saved there are no
new unvalued histories for the coordinator to value a second time.

# 14. Production leg of PRTN


For each PRTN line create a `ProductionStockLeg`:

```text
MovementType        = RETURN
ProductionBalLot    = locked source
Qty                 = RequestedQty
BaseQty             = BaseQty
WorkOrderId
WorkOrderMaterialId
WorkOrderOperationId
PostingLinkId
DocumentType        = PRODUCTION_STOCK_RETURN
DocumentNo          = BatchNo
SourceLineId        = ReturnLineId
SplitOrdinal        = 0
OriginalMovementId  = null
InventoryHistoryId  = created PRTN history Id
ExactTotalValue     = exact current Production relief value
ValuationStatus     = VERIFIED
```

**Important:** the Production balance RETURN movement must have `OriginalMovementId = null`.

`ProductionPoolValuationService` interprets a non-null balance-movement `OriginalMovementId` as a
reversal. Source ISSUE lineage for a forward Return belongs in:
- the required contribution directive;
- the material RETURN fact's `OriginalMovementId` / `SourceIssueMovementId`.

Call:

`IProductionStockWriter.ApplyAsync`.

This is the only code that mutates `ProductionBalLot` quantity/value.

### Quantity origin vs financial value

These are intentionally different authorities:

```text
Returnable quantity:
    remaining contribution from exact original ISSUE receipt

Return financial value:
    current verified pooled Production carrying value
```

Example: if an ISSUE contribution and a later valued ADJUST_IN are pooled together, a Return may be
restricted to ISSUE-origin quantity while its RM value is relieved at the current pooled carrying
value.

Do not force the financial amount back to original purchase/ISSUE cost; that would violate the
current Production pooled-value model.

# 15. Return-specific contribution allocation

Current `ProductionPoolValuationService` generically allocates outbound movements among available inbound contributions.

PRTN needs stricter behavior.

## 15.1 Extend `RecordAsync` with optional directives

Add a backward-compatible overload/options object, e.g.:

```text
ProductionPoolValuationDirectives
```

containing:

```text
RequiredReceiptMovementByOutboundMovementId
InboundValuationAuthorityByMovementId
```

Existing callers pass null and keep exact existing behavior.

Do not change Material Issue / Daily Production / FG behavior.

---

## 15.2 RETURN directive

For each PRTN RETURN movement:

```text
RequiredReceiptMovementId
= original ISSUE ProductionBalLotMovementId
```

`ProductionPoolValuationService` must prove:
- same ProductionBalLot;
- receipt type ISSUE;
- VERIFIED;
- same current generation;
- remaining contribution >= return BaseQty.

Then create the `ProductionMovementAllocation` only against that receipt.

Do not let generic FIFO allocation consume ADJUST_IN contribution.

---

# 16. Production material RETURN fact


After the Production stock movement exists, insert a `ProductionMaterialMovement`:

```text
MovementType               = RETURN
WorkOrderId
WorkOrderMaterialId
WorkOrderOperationId
ItemCode
Qty/BaseQty/UOM
Warehouse/Location/Lot     = original Inventory slice
LotId
FromBalLocId               = original Inventory source balance identity
InventoryBatchId/No        = PRTN batch
InventoryBatchDetailId
InventoryTrxLineNo
InventoryHistoryId         = PRTN receipt history
ProductionBalLotId
ProductionBalLotMovementId = RETURN movement UID
UnitCost
TotalCost
PostingLinkId
StockPostingId
SourceLineId               = ReturnLineId
SplitOrdinal               = 0

OriginalMovementId         = original ISSUE material movement UID
SourceIssueMovementId      = original ISSUE material movement UID

Reason
Remarks
```

This deliberately matches the current Daily Production dependency convention, where raw-material
`CONSUME` uses `OriginalMovementId` to point to the source ISSUE.

`SourceIssueMovementId` is also populated because it is the explicit audit/source field already
present in the entity.

This is mandatory so the existing Issue rollback dependency graph can discover an active RETURN.

For rollback:

```text
MovementType               = RETURN_REVERSAL
OriginalMovementId         = original RETURN material movement UID
ReversesMaterialMovementId = original RETURN material movement UID
SourceIssueMovementId      = original RETURN.SourceIssueMovementId
```

Do not delete/edit the forward RETURN.

# 17. Inventory valuation for PRTN

After `IvTrxHistory` is stamped and before final invariant/seal:

call:

`IStockPostingCoordinator.ValuePendingInTransactionAsync(context)`.

This allows the invariant to inspect authoritative `StockValuationFact`.

Calling `CompleteInTransactionAsync` later is safe because no unvalued history remains.

---

## 17.1 Moving Average

`InventoryValuationService` must use:

`IvTrxHistory.ExactTransferredValue`.

Expected:

```text
Production RETURN TotalCost
== Inventory receipt StockValuationFact.CostAmount
```

Inventory moving-average state updates normally.

---

## 17.2 FIFO


The Inventory receipt uses the exact Production transferred value.

The returned stock becomes a **new FIFO receipt layer/fact** at that exact transferred value.

Do **not** attempt to reopen or reconstruct the original Inventory FIFO layer(s) that were consumed by
Issue to Production.

Reason:

- Inventory FIFO provenance determined the cost of the original ISSUE;
- after ISSUE, Production carries that material in a pooled Production value;
- subsequent Production consume/adjust/return uses the Production pool carrying value;
- the authoritative return value is therefore the exact value relieved from Production.

This preserves:

```text
Production value out == Inventory FIFO receipt value in
```

and avoids inventing a FIFO genealogy that the Production ledger does not maintain.

Do not use current Inventory average cost.

## 17.3 Standard Cost


Inventory receipt remains valued at effective Standard Cost.

Actual Production relieved value remains in:

`IvTrxHistory.ExactTransferredValue`.

Generalize current:

`AppendProductionStandardVarianceIfNeeded`

to recognize both:
- FG;
- PRTN.

Prefer helper:

```text
IsProductionToInventoryTransfer(...)
```

For PRTN:

```text
ActualProductionValue   = ExactTransferredValue
StandardInventoryValue  = StockValuationFact.CostAmount
Variance                = ActualProductionValue - StandardInventoryValue
```

Reuse:

`ProductionStandardCostVariance`.

Do not create another variance engine/table.

The current entity contains optional `FinishedGoodReceiptId`; PRTN does not need to fake an FG
identity. Leave that field null for PRTN and use the existing authoritative links:

```text
StockPostingId
ProductionPostingLinkId
InventoryValuationFactId
```

Existing fact-based reversal logic must create the exact negative reversal variance.

Example:

```text
actual Production return value = 100
Inventory Standard receipt      = 110
variance                         = -10

rollback variance                = +10
net variance                     = 0
```

## 17.4 Inventory movement code

Extend `InventoryValuationService.MovementCode(...)`:

```text
PRTN -> PRODUCTION_RETURN_IN
```

This improves:
- costing trace;
- diagnostics;
- reporting.

---

# 18. PRTN immutable cross-ledger fact

After both legs exist, add:

`ProductionStockReturnFact`.

Require exact conservation:

```text
Fact.BaseQty
== Production RETURN BaseQty
== Inventory History EvidenceBaseQty
== Inventory valuation BaseQty
```

For MA/FIFO:

```text
Fact.TotalValue
== Production RETURN TotalCost
== IvTrxHistory.ExactTransferredValue
== Inventory StockValuationFact.CostAmount
```

For Standard:

```text
Fact.TotalValue
== Production actual RETURN value

Production actual value
- Inventory Standard valuation value
== ProductionStandardCostVariance
```

---

# 19. PRTN Work Order projection

Update:

`ProductionWorkOrderMaterial.ReturnedQty`

from immutable movements.

Do not increment it manually from request qty.

Add:

```csharp
EffectiveReturned(returnQty, returnReversalQty)
```

to `ProductionMaterialMovementTotals`.

Update `FromRows` to include `RETURN_REVERSAL`.

Every repo location currently doing:

```text
sum(RETURN)
```

must change to net effective return.

Known live locations include:
- Material Issue posting projection;
- Material Issue rollback projection;
- result/read models using ReturnedQty.

Search the complete branch before coding completion.

---

# 20. Fix material dependency semantics


Current Issue rollback recursively follows:

```text
ProductionMaterialMovement.OriginalMovementId
```

and calls:

`ProductionMaterialMovementTotals.HasBlockingDownstreamDependency`.

The important distinction is:

```text
ProductionBalLotMovement RETURN (forward)
    OriginalMovementId = null

ProductionMaterialMovement RETURN (forward)
    OriginalMovementId = source material ISSUE
    SourceIssueMovementId = source material ISSUE
```

The forward balance RETURN is **not a reversal**.
The forward material RETURN is a **dependency of the source ISSUE**.

Update the material dependency helper to collapse active/reversed pairs.

### Active CONSUME

Existing behavior remains:

```text
CONSUME without its matching CONSUME_REVERSAL
```

### Active RETURN

```text
forward material RETURN pointing to ISSUE
without a RETURN_REVERSAL whose ReversesMaterialMovementId points to that RETURN
```

### Active raw-material ADJUST

```text
forward material ADJUST pointing to ISSUE
without an ADJUST reversal row whose ReversesMaterialMovementId points to that ADJUST
```

A fully reversed Return/Adjustment must not permanently block the original Issue rollback.

Do not weaken any active downstream dependency blocker.

# 21. PRTN invariant


Add:

```text
AssertProductionReturnVerifiedAsync
AssertProductionReturnRollbackVerifiedAsync
```

Forward invariant must prove, per line:

1. same company/branch/current StockPosting throughout;
2. exactly one immutable ReturnFact;
3. PRTN Inventory history is V2 and `VALUED`;
4. PRTN history `ToBalLocId` equals the exact original ISSUE `FromBalLocId`;
5. for lot stock, PRTN history `ToLotId` equals exact original ISSUE `FromLotId`;
6. PRTN history item/warehouse/location/lot/status/UOM equal source ISSUE authority;
7. history `EvidenceBaseQty == ReturnFact.BaseQty`;
8. history `ExactTransferredValue == ReturnFact.TotalValue`;
9. Production RETURN balance movement is V2 + VERIFIED;
10. forward balance RETURN has `OriginalMovementId == null`;
11. material RETURN:
    - points `OriginalMovementId` to source material ISSUE;
    - points `SourceIssueMovementId` to same ISSUE;
    - links exact RETURN balance movement and PRTN history;
12. required quantity contribution allocation points **only** to original ISSUE balance receipt movement;
13. Production financial dependency edges remain valid for all current pool contributors;
14. pool tracked qty/value equals live ProductionBalLot qty/value;
15. Inventory valuation fact exists and quantity reconciles;
16. MA/FIFO exact Production value == Inventory valuation receipt value;
17. Standard difference equals explicit `ProductionStandardCostVariance`;
18. operational IvBalLoc price state equals the captured Posted* snapshot;
19. no orphan/missing source or reversal authority.

POST must fail before StockPosting seal if any assertion fails.

# 22. Complete PRTN POST


After all cross-ledger invariants pass:

- recompute Work Order material ReturnedQty/effective supply from immutable facts;
- append ProductionAuditEvent `MATERIAL_RETURNED`;
- mark ProductionPostingLink SUCCEEDED;
- set sidecar `PostingId`;
- **assert** the generic stock-in core already set `IvTrxBatch.BatchStatus = POSTED`;
- save;
- call `CompleteInTransactionAsync(context)`;
- assert sealed;
- commit outer SQL transaction.

Do not independently perform a second Inventory batch post/status transition.

Any failure before commit rolls back the entire transaction:
- Inventory balance qty;
- Inventory history;
- Inventory valuation/state/FIFO;
- Production qty/value;
- Production movement;
- contribution/dependency rows;
- Work Order projections;
- variance/facts/audit/link.

There is no allowed partial success.

# 23. PRTN rollback — lifecycle

Rollback effective time:

```text
clock.Now
```

Follow FG's Production reversal pattern.

Require:
- status POSTED;
- original PostingId exists and is sealed;
- rollback reason required;
- stable RequestId;
- V2 ledger active.

Create reversal StockPosting:

```text
SourceModule        = PRODUCTION
SourceDocumentType  = PRODUCTION_STOCK_RETURN
PostingRole         = REVERSAL
ReversesPostingId   = original PostingId
```

Create rollback `ProductionPostingLink` with:
- same Work Order;
- `OriginalPostingLinkId`.

---

# 24. PRTN rollback dependency checks


## 24.1 Production side — strict per-pool LIFO

Load original PRTN posting sequence.

For every touched ProductionBalLot, block rollback if any **later active forward**
`ProductionBalLotMovement` exists after the original posting sequence.

Active forward means:
- `OriginalMovementId == null`;
- its StockPosting is sealed;
- that posting has not itself been reversed by a sealed reversal StockPosting.

This blocks later:
- CONSUME;
- PRODUCE interactions;
- RETURN;
- PADJ IN/OUT;
- transfer/status changes;
- other relevant production stock movements.

Do not limit this gate to later outflows.

Reason: a later inbound can start a new valuation generation after the original PRTN emptied a pool.
Restoring older stock beneath that generation is too risky.

Also require:

`ProductionPoolValuationService.HasActiveDependentsAsync(...) == false`

for the original PRTN outbound movement where applicable.

User message:

```text
Reverse later Production stock transactions on this lot first.
```

---

## 24.2 Inventory side

For each returned destination balance:
- lock exact destination;
- require sufficient current Inventory quantity;
- block if any later **active** Inventory history exists after the original PRTN posting authority.

Use the same active/reversed evidence philosophy as FG rollback; do not merely compare BatchNo.

---

## 24.3 Operational price snapshot

Require current:

```text
UnitPrice
Cost
PriceEvidence
```

to equal the PRTN `Posted*` snapshot before restoring the `Previous*` values.

If another valid process changed those operational fields:
- block rollback;
- do not overwrite them.

---

## 24.4 Work Order state on rollback

Rollback is corrective history and remains allowed even if the Work Order later changed state,
provided all stock/dependency/period rules pass.

The posting-enabled feature switch must **not** disable rollback.

Do not require the Work Order to still be RELEASED/IN_PROGRESS for rollback of an already-posted
document.

# 25. PRTN rollback facts


Do **not** call generic:

`RollBackStockInInTransactionAsync`

for PRTN.

That generic Inventory rollback intentionally reopens ordinary MR/CR-style batches to NEW and advances
detail generations. PRTN requires immutable:

```text
POSTED -> REVERSED
```

Use the append-only reversal architecture already proven by FG Receipt.

## 25.1 Inventory physical reversal

Under the same reversal StockPosting/SQL transaction:

1. lock destination `IvBalLoc` rows;
2. apply §24 Inventory dependency/chronology checks;
3. require enough current qty;
4. subtract exactly the original PRTN receipt qty;
5. restore operational price snapshot only when its posted-state guard matches;
6. append V2 history reversals with:
   `IIvInventoryHistoryWriter.AppendReversal`.

Do not delete original PRTN history.
Do not clear original detail ToBalLoc/ToLot identity.
Do not reopen the original PRTN batch.

---

## 25.2 Production RETURN reversal

Append `ProductionStockLeg`:

```text
MovementType       = RETURN_REVERSAL
OriginalMovementId = original RETURN ProductionBalLotMovement UID
Qty/BaseQty        = exact original
ExactTotalValue    = original ReturnFact.TotalValue
```

Call:
- `ProductionStockWriter.ApplyAsync`;
- `ProductionPoolValuationService.RecordAsync`.

The existing reversal path must:
- append allocation reversals;
- append dependency reversals;
- restore correct valuation generation evidence;
- never delete original valuation rows.

---

## 25.3 Material RETURN_REVERSAL

Append `ProductionMaterialMovement`:

```text
MovementType               = RETURN_REVERSAL
OriginalMovementId         = original material RETURN UID
ReversesMaterialMovementId = original material RETURN UID
SourceIssueMovementId      = original RETURN.SourceIssueMovementId
ProductionBalLotMovementId = RETURN_REVERSAL movement
InventoryHistoryId         = reversal Inventory history
same exact qty/value/UOM/WorkOrder/material lineage
```

The unique filtered reversal key prevents two material reversals for one forward RETURN.

After this row exists, the source ISSUE material dependency chain must treat that Return as inactive.

---

## 25.4 Inventory valuation reversal

Call:

`ValuePostingInTransactionAsync(context)`.

The Inventory valuation engine must append exact reversal facts.

For Standard Cost:
- reverse the original `ProductionStandardCostVariance` exactly;
- `original variance + reversal variance = 0`.

---

## 25.5 Reversal cross-ledger fact

Append `ProductionStockReturnFact` with:
- exact original BaseQty;
- exact original TotalValue;
- reversal Production movement;
- reversal material movement;
- reversal Inventory history;
- `ReversesFactId = original ReturnFact.Id`.

Run the rollback invariant before sealing.

---

## 25.6 Final state

Set:

```text
BatchStatus       = REVERSED
ReversalPostingId = reversal StockPosting
ReversalReason
```

Recompute net Work Order ReturnedQty from immutable effective facts.

Append audit event:

`MATERIAL_RETURN_ROLLED_BACK`.

Seal and commit atomically.

# 26. PRTN correction UX

Implement `CreateCorrectionAsync` following FG pattern.

Allowed only when original status = REVERSED.

Create a new NEW document:
- same Work Order;
- copy line intent;
- use current date;
- re-run current source eligibility/returnable validation;
- user may edit before Save/Post.

Do not mutate the reversed document.

---

# 27. Production Stock Adjustment — source/readiness


Work Order is selected first.

For new Save/POST, Work Order must be:

```text
RELEASED
IN_PROGRESS
```

Reject Draft, Completed, Closed and Cancelled.

Source picker displays existing `ProductionBalLot` identities for that Work Order.

Do not create arbitrary new ProductionBalLot identities in v1.

Every selected pool must pass the scalar contract:

```text
factor > 0
Qty == IvQty.Round(BaseQty / factor)

if BaseQty > 0:
    AverageUnitCost == Money(TotalCost / BaseQty)

if BaseQty == 0:
    Qty == 0
    TotalCost == 0
    AverageUnitCost == 0
```

Fail closed on mismatch.

### MATERIAL_IN additional rules

Require:
- `WorkOrderMaterialId != null`;
- `OriginalIssueMovementId != null`;
- exact source ISSUE lineage resolves as in PRTN;
- source ISSUE belongs to same Work Order material.

Legacy/orphan material pools are repaired through reconciliation tooling, not PADJ.

### Negative adjustment

For ADJUST_OUT:
- `ProductionCostReadiness.PoolValuationError == null`;
- absolute BaseQty <= locked pool BaseQty.

### Positive adjustment into nonempty pool

Require current pool VERIFIED and scalar-valid.

Do not allow a positive adjustment to “clean” an UNVALUED nonempty pool.

### Positive adjustment into empty existing pool

Require:

```text
BaseQty == 0
Qty == 0
TotalCost == 0
AverageUnitCost == 0
```

Cost authority is resolved at POST from §29.

The new inbound starts a new production valuation generation.

# 28. PADJ Save


SAVE runs in a transaction and locks the Work Order with the same `UPDLOCK,HOLDLOCK` pattern
used by current Material Issue drafts.

This serializes PADJ draft Save against Work Order completion.

Save rules:
- one Work Order only;
- Work Order status RELEASED/IN_PROGRESS;
- freeze current `SnapshotRevision` / `SnapshotHash`;
- source pool belongs to Work Order;
- signed quantity non-zero;
- UOM/factor frozen;
- BaseQty round-trip safe;
- reason required;
- OTHER requires remarks;
- negative does not exceed current qty at Save.

Save is advisory; POST re-locks and revalidates everything.

Persist:
- PADJ `IvTrxBatch`;
- generic `IvTrxBatchDetail`;
- `ProductionStockAdjustment`;
- `ProductionStockAdjustmentLine`.

No stock/value change on Save.

### Manual/zero approval

If explicit cost approval is needed, store it only in the Production sidecar at 19,6.

Allowed persisted evidence codes:

```text
MANUAL_APPROVED
ZERO_COST_APPROVED
```

Use the repository constants in `InventoryCostEvidenceTypes` for those code strings where practical.

Do **not** allow `OPENING_APPROVED` for PADJ.

Server enforcement:
- `PriceOverride` permission;
- positive manual cost for MANUAL_APPROVED;
- exactly zero for ZERO_COST_APPROVED;
- mandatory reason;
- approver + timestamp stamped server-side.

If source/qty/date/cost/evidence/reason changes, clear and re-approve the evidence.

# 29. PADJ positive cost authority


Positive Production Adjustment introduces value without an Inventory receipt.

It must never remain UNVALUED.

Resolve financial authority under POST locks.

**Rule: calculate exact `TotalValue` first at Money(19,6), then derive UnitCost.**
Do not multiply a rounded saved/display unit price back into the quantity.

## 29.1 Non-empty VERIFIED Production pool — all cost methods

If:

```text
pool.BaseQty > 0
pool valuation VERIFIED
```

calculate:

```text
TotalValue
= Money(pool.TotalCost * AddBaseQty / pool.BaseQty)

UnitCost
= Money(TotalValue / AddBaseQty)

CostBasisCode = CURRENT_PRODUCTION_POOL_COST
```

This applies under Inventory:
- Moving Average;
- FIFO;
- Standard.

Do not inject Inventory Standard Cost into a nonempty Production actual-value pool.

---

## 29.2 Empty MATERIAL_IN pool with exact verified source ISSUE

If:
- pool qty/value are zero;
- original material ISSUE and balance ISSUE are exact and VERIFIED;

calculate:

```text
TotalValue
= Money(originalIssue.TotalCost * AddBaseQty / originalIssue.BaseQty)

UnitCost
= Money(TotalValue / AddBaseQty)

CostBasisCode = ORIGINAL_ISSUE_COST
OriginalIssueMovementId = original material ISSUE Uid
```

This is a new Production valuation generation.

The added quantity remains an ADJUST_IN contribution; it does **not** become returnable original ISSUE
contribution.

---

## 29.3 Other empty Production pool — Standard fallback

Resolve active cost method.

Only when active method is Standard and an approved effective Standard Cost revision exists:

```text
TotalValue = Money(AddBaseQty * EffectiveStandardCost)
UnitCost   = Money(TotalValue / AddBaseQty)
CostBasisCode = STANDARD_COST
StandardCostRevisionId = ItemStandardCostRevision.Id
```

No Item Master price fallback.

---

## 29.4 Empty pool without trustworthy automatic authority

Require server-approved sidecar evidence.

### MANUAL_APPROVED

```text
ProposedUnitCost > 0
TotalValue = Money(AddBaseQty * ProposedUnitCost)
UnitCost   = Money(TotalValue / AddBaseQty)
```

### ZERO_COST_APPROVED

```text
ProposedUnitCost == 0
TotalValue == 0
UnitCost == 0
```

Both require:
- PriceOverride;
- approval reason;
- approved-by/timestamp.

Do not use:
- PurchasePrice;
- SellingPrice;
- latest PO price;
- 4-decimal `IvTrxBatchDetail.UnitPrice`;
- stale previous-generation WIP/FG average.

# 30. PADJ negative cost


ADJUST_OUT value is always system-owned and uses the locked current Production pool.

Partial removal:

```text
TotalValue
= Money(pool.TotalCost * RemoveBaseQty / pool.BaseQty)

UnitCost
= Money(TotalValue / RemoveBaseQty)
```

Full depletion:

```text
if RemoveBaseQty == pool.BaseQty:
    TotalValue = pool.TotalCost
```

This exact full-depletion rule prevents stranded sub-sen value.

Do not use:
- user-entered cost;
- `AverageUnitCost * qty` as final authority;
- Inventory current cost.

`ProductionStockWriter` receives `ExactTotalValue` in every PADJ OUT.

# 31. Extend ProductionPoolValuationService for ADJUST_IN


Current positive non-PRODUCE movements begin as UNVALUED.

PADJ requires explicit authority.

Use the backward-compatible directive object from §15.

For each ADJUST_IN movement pass:

```text
Basis
UnitCost
TotalValue
optional OriginalIssueMovementId / StandardCostRevisionId / manual approval evidence
```

Allowed inbound bases:

```text
CURRENT_PRODUCTION_POOL_COST
ORIGINAL_ISSUE_COST
STANDARD_COST
MANUAL_APPROVED
ZERO_COST_APPROVED
```

`RecordAsync` validates:
- movement is ADJUST_IN;
- exact movement value equals directive;
- source pool state allows inbound;
- basis is approved;
- non-empty pool uses CURRENT_PRODUCTION_POOL_COST;
- ORIGINAL_ISSUE_COST points to the pool's exact verified source ISSUE;
- STANDARD_COST identifies the approved effective revision when used;
- manual/zero bases have already passed permission/evidence validation.

Then write `ProductionValuationEvidence`:

```text
Status = VERIFIED
Basis = exact CostBasisCode
Currency = COMPANY_BASE
PriceUom = BaseUom
Price = UnitCost
ConversionFactor
```

A successful ADJUST_IN must remain VERIFIED.

If the pool is nonempty UNVALUED:
- reject POST;
- do not append another receipt and leave pool contaminated.

For FG cost tracing, an ADJUST_IN is a valid VERIFIED contributor. Update the cost-trace replay detail
so this inbound is identified as `PRODUCTION_STOCK_ADJUSTMENT` with its exact valuation basis instead
of being reported only as an anonymous legacy/external movement. Keep the amount unchanged; this is
trace classification, not a new costing calculation.

# 32. PADJ POST


Posting shell:

1. authorize Access + Post;
2. verify `ProductionStockAdjustmentPostingEnabled`;
3. require stable RequestId;
4. create DbContext and enable Adjustment write capability;
5. begin SQL transaction;
6. branch stock lock;
7. replay check;
8. lock PADJ document / rowversion / require NEW;
9. reject future date;
10. lock Work Order;
11. require `RELEASED|IN_PROGRESS`;
12. require draft SnapshotRevision/Hash still equal Work Order;
13. lock ProductionBalLot rows ascending UID;
14. validate scalar/pool readiness and chronology;
15. resolve exact 19,6 qty/value authority under lock;
16. build Production StockPosting fingerprint including snapshot and exact source identity;
17. begin PRODUCTION StockPosting;
18. create PENDING ProductionPostingLink with snapshot revision/hash;
19. apply `ProductionStockLeg` rows with ExactTotalValue;
20. save movement IDs;
21. record Production valuation using directives;
22. for MATERIAL_IN append linked `ProductionMaterialMovement` ADJUST;
23. append immutable AdjustmentFact;
24. recompute material execution totals/effective supply projections from immutable facts;
25. run PADJ invariant;
26. append audit;
27. set POSTED;
28. complete/seal StockPosting;
29. commit.

No Inventory posting API is called.

### MATERIAL_IN material ADJUST fact

Forward row:

```text
MovementType               = ADJUST
OriginalMovementId         = source ISSUE material movement UID
SourceIssueMovementId      = source ISSUE material movement UID
ProductionBalLotId         = source pool UID
ProductionBalLotMovementId = created ADJUST_IN/ADJUST_OUT movement
WorkOrderId
WorkOrderMaterialId
WorkOrderOperationId
Qty                      = signed-direction magnitude in Work Order material RequiredUom
Uom                      = Work Order material RequiredUom
BaseQty                  = exact absolute base quantity
BaseUom                  = Work Order material BaseUom
ConversionFactorToBase   = frozen material factor

WarehouseCode            = source ISSUE.WarehouseCode
LocationCode             = source ISSUE.LocationCode
LotNo                    = source ISSUE.LotNo
LotId                    = source ISSUE.LotId
FromBalLocId              = source ISSUE.FromBalLocId
ItemStatus                = source ISSUE.ItemStatus

UnitCost/TotalCost
PostingLinkId
StockPostingId
SourceLineId               = AdjustmentLineId
SplitOrdinal               = 0
Reason/Remarks
```

Inventory **transaction** identity remains null because PADJ is Production-only:

```text
InventoryBatchId/No
InventoryBatchDetailId
InventoryTrxLineNo
InventoryHistoryId
```

`FromBalLocId` on a MATERIAL_IN ADJUST is retained only as frozen source-ISSUE lineage, exactly like
current Production consume facts; it does not mutate Inventory.

Do not create a material ADJUST row for WIP/FG pools.

### Projection rule

Do not alter the meaning of `ProductionWorkOrderMaterial.IssuedQty`.

Refresh:
- IssuedQty from ISSUE - ISSUE_REVERSAL;
- ReturnedQty from effective RETURN;
- ConsumedQty from effective CONSUME.

Adjustment-aware **effective supply** is calculated from immutable material facts when needed for Issue
eligibility; it is not hidden inside IssuedQty.

# 33. PADJ ProductionStockLeg

For positive:

```text
MovementType = ADJUST_IN
ExactTotalValue = approved/system total value
```

For negative:

```text
MovementType = ADJUST_OUT
ExactTotalValue = exact pool value removed
```

Populate:
- WorkOrderId;
- WorkOrderMaterialId when source pool has one;
- WorkOrderOperationId;
- RouteStepId;
- PostingLinkId;
- DocumentType = PRODUCTION_STOCK_ADJUSTMENT;
- SourceLineId = AdjustmentLineId;
- SplitOrdinal = 0.

---

# 34. PADJ invariant


Add:

```text
AssertProductionStockAdjustmentVerifiedAsync
AssertProductionStockAdjustmentRollbackVerifiedAsync
```

POST must prove for every line:

```text
movement VERIFIED
movement BaseQty == fact BaseQty
movement TotalCost == fact.TotalValue
pool tracked qty == ProductionBalLot.BaseQty
pool tracked value == ProductionBalLot.TotalCost
pool qty/value nonnegative
zero qty cannot have nonzero value
```

For ADJUST_IN:
- evidence basis equals fact basis;
- unit/total value match;
- authority is valid under §29.

For every `MATERIAL_IN` line additionally prove:
- exactly one material `ADJUST` fact exists;
- `OriginalMovementId` and `SourceIssueMovementId` equal the pool's source ISSUE;
- its linked `ProductionBalLotMovementId` is the exact ADJUST_IN/ADJUST_OUT movement;
- item/WorkOrder/material/qty/UOM/value all match.

Also assert:

```text
NO IvTrxHistory belongs to this StockPosting
NO StockValuationFact belongs to this StockPosting
NO IvBalLoc quantity changed by PADJ
```

The final SQL/integration test suite must explicitly prove this.

# 35. PADJ rollback


Status POSTED only.

Use current rollback time and mandatory reason.

Feature posting-disable flag must not block rollback.

Create reversal StockPosting linked to original.

## 35.1 Strict per-pool rollback gate

For every touched ProductionBalLot:

block if any later active forward Production movement exists after the original PADJ posting sequence.

Require later Production documents to be reversed first.

For an original ADJUST_IN also require:

`ProductionPoolValuationService.HasActiveDependentsAsync(...) == false`

for the added contribution.

This is intentionally stricter than simply checking current on-hand quantity.

---

## 35.2 Inverse Production balance movement

Use registry inverse:

```text
ADJUST_IN  -> ADJUST_OUT
ADJUST_OUT -> ADJUST_IN
```

Create `ProductionStockLeg` with:
- exact original Qty/BaseQty;
- exact original TotalValue;
- `OriginalMovementId = original balance movement Uid`;
- original Work Order/operation/material lineage.

`ProductionPoolValuationService.RecordAsync` must create:
- REVERSAL evidence;
- allocation reversals;
- dependency reversals.

No new cost approval is required.

### Effective-movement replay update

`ProductionBalLotSignedQty.EffectiveMovements` currently recognizes named reversal movement types but
does not collapse inverse ADJUST movements.

Add both:

```text
ADJUST_IN
ADJUST_OUT
```

to the reversal-type test **only when `OriginalMovementId` is non-null**, which is already how that
helper identifies reversals.

Required tests:

```text
forward ADJUST_IN + rollback ADJUST_OUT -> no effective movement
forward ADJUST_OUT + rollback ADJUST_IN -> no effective movement
```

This protects prefix replay, latest movement date and Production rollback logic.

---

## 35.3 Raw-material material-fact reversal

For original MATERIAL_IN PADJ append:

```text
MovementType               = ADJUST
OriginalMovementId         = original material ADJUST UID
ReversesMaterialMovementId = original material ADJUST UID
SourceIssueMovementId      = original.SourceIssueMovementId
ProductionBalLotMovementId = inverse balance movement UID
same exact qty/value/UOM/WorkOrder/material identity
Reason = ROLLBACK
```

The reversal collapses the forward material adjustment for:
- Work Order reconciliation;
- Issue-to-Production effective supply;
- Issue rollback dependency.

---

## 35.4 PADJ reversal fact/final state

Append AdjustmentFact:
- exact original qty/value/unit cost;
- basis `REVERSAL`;
- `ReversesFactId`.

Run rollback invariant.

Set:
```text
REVERSED
ReversalPostingId
ReversalReason
```

Append audit, seal, commit.

Original document remains immutable/read-only.

# 36. PADJ correction

Offer `CREATE CORRECTION` for REVERSED document.

Clone intent to new NEW document and re-resolve:
- current stock;
- current cost method;
- current Standard Cost;
- current pooled cost.

Do not copy old automatic cost as new financial authority.

For manual override, user must confirm/re-enter approval according to permission policy.

---

# 37. Period + chronology rules


## 37.1 Forward PRTN/PADJ

Both:
- cannot be future;
- must pass StockPosting period/freeze guards;
- must not precede source `LastStockEventEffectiveAt`;
- must not precede source `LastMovementDate`.

PRTN Inventory leg then uses the existing Inventory stock-in chronology:
- normal balance stock-date check;
- later-day movement check;
- same-day behavior remains consistent with the current Inventory engine.

Do not invent a second conflicting chronology system.

---

## 37.2 Rollback

Rollback effective timestamp = current time.

Do not backdate rollback to original document date.

For Production pool chronology use strict active-posting sequence LIFO from §§24/35.

For PRTN Inventory destination, block later active Inventory history before subtracting the returned
stock.

These rollback rules are intentionally stricter than forward-entry chronology.

# 38. Concurrency/locking

Critical for expensive material such as coil.

Required:
- one branch stock transaction lock;
- deterministic ProductionBalLot lock order;
- deterministic Inventory slice lock order;
- Work Order lock;
- re-read all source values under lock;
- re-run contribution availability under lock;
- re-run valuation readiness under lock.

Race examples that must be safe:

```text
Return vs Daily Production consume
Return vs PADJ OUT
Return vs another Return
PADJ OUT vs Daily Production consume
PADJ OUT vs FG Receipt
PADJ IN/OUT vs another PADJ
```

Only one valid conflicting operation may succeed.

No negative qty/value.

No double allocation.

---

# 39. Source/draft reservations

Do not make PRTN NEW drafts physically reserve Production stock in v1.

Production execution remains free to use stock until PRTN POST.

However Return source UI may subtract other open PRTN drafts from its advisory:

```text
Available now
Open Return drafts
Advisory available
```

Label clearly as advisory.

POST always revalidates authoritative current contribution.

Do not change Daily Production core availability logic for a draft-only return.

---

# 40. Reason codes

Add Production-specific reason closed lists.

## Return examples

```text
EXCESS
WO_CANCELLED
WRONG_ISSUE
NOT_REQUIRED
QC_RETURN
OTHER
```

## Adjustment examples

```text
COUNT
FOUND
SHORTAGE
DAMAGE
DATA_CORRECTION
OTHER
```

Use actual SME-friendly wording in UI.

`OTHER` requires remarks.

Do not reuse Sales Customer Return reasons blindly.

---

# 41. Permissions

Menu codes:

```text
PLN_PRODUCTION_STOCK_RETURN
PLN_PRODUCTION_STOCK_ADJUSTMENT
```

Standard permissions:

```text
Access
Add
Edit
Delete
Post
Rollback
ViewCost
```

Positive PADJ manual/zero cost:

```text
PriceOverride
```

Cost visibility is independent from stock-post permission.

---

# 42. Menu

Add under:

`PLN_TRANSACTIONS`

Recommended order:

```text
1 Work Orders
2 Issue to Production
3 Daily Production
4 Finished Good Receipt
5 Production Stock Return
6 Production Stock Adjustment
7 Machine Maintenance
```

Update:
- `MenuCodes.cs`;
- `menus.xml`;
- deployment/init SQL if repo deployment requires explicit menu scripts;
- menu parity tests.

---

# 43. Transaction delete/archive policy

Add owner types:

```text
PRODUCTION_STOCK_RETURN
PRODUCTION_STOCK_ADJUSTMENT
```

to `TransactionDeleteOwnerTypes`.

Add evaluators in:

`TransactionDeletePolicyService`.

Lifecycle:

### NEW with no historical posting
hard delete allowed.

### POSTED
delete blocked:
```text
rollback first
```

### REVERSED / historical
archive owning document, never remove ledger/history/facts.

Use `InventoryBatchRetention` patterns.

---

# 44. Cost trace

Add `ViewCost` trace similar to:

`ProductionFinishedGoodReceiptService.CostTrace.cs`.

## Return trace should show

```text
original Inventory ISSUE
Production MATERIAL_IN pool
pool qty/value immediately before return
return contribution availability
exact Production value relieved
Production RETURN movement
Inventory PRTN history
Inventory StockValuationFact
Standard variance when applicable
reversal chain
```

Status:
- VERIFIED;
- VERIFIED_WITH_UNCLASSIFIED if existing trace convention requires;
- INCONSISTENT when immutable facts do not reconcile.

---

## 44.1 PADJ cost trace

Show:
- pool before adjustment;
- movement;
- exact qty/value;
- cost basis;
- Standard Cost revision if used;
- manual approval if used;
- pool after;
- reversal chain.

Do not infer historical cost from today's master.

---

# 45. Production reconciliation enhancement


Three related projections must be correct.

## 45.1 Production stock qty/value reconciliation

Extend `ProductionStockHistoryService.ReconcileAsync` sealed movement projection with:

```text
TotalCost
SignedValue = registry direction * TotalCost
```

Per ProductionBalLot:

```text
LedgerBaseQty = sum SignedBaseQty
LedgerValue   = Money(sum SignedValue)
```

Compare to:

```text
ProductionBalLot.BaseQty
ProductionBalLot.TotalCost
```

Findings:

```text
QTY_MISMATCH
VALUE_MISMATCH
STRANDED_VALUE
UNVERIFIED_POOL
```

Use exact repository quantity/money rounding, not an arbitrary RM tolerance.

---

## 45.2 One authoritative material execution calculation

The current `FromRows((MovementType, Qty))` shape is not sufficient for PADJ because an `ADJUST`
material row does not encode its sign by material MovementType.

Add a richer fact input/helper rather than guessing, e.g.:

```text
ProductionMaterialExecutionFact
- MaterialMovementId
- MovementType
- Qty
- BaseQty
- OriginalMovementId
- ReversesMaterialMovementId
- ProductionBalLotMovementId
- ProductionBalLotMovementType
```

Build the fact by joining material ADJUST rows to their linked `ProductionBalLotMovement`.

Keep existing helpers/overloads where old callers/tests need them, but migrate all execution/supply
authority callers to the richer calculator.

Rules:

```text
EffectiveIssue =
    ISSUE - ISSUE_REVERSAL

EffectiveConsume =
    CONSUME - CONSUME_REVERSAL

EffectiveReturn =
    sum forward RETURN not referenced by active RETURN_REVERSAL

NetActiveAdjustment =
    sum active forward ADJUST:
        + BaseQty when linked balance movement = ADJUST_IN
        - BaseQty when linked balance movement = ADJUST_OUT
```

A reversal ADJUST row (`ReversesMaterialMovementId != null`) is never counted as a new independent
adjustment; it cancels its referenced forward ADJUST.

Return **both Work Order quantity-UOM totals and BaseQty totals**.

Required quantity totals:

```text
EffectiveIssuedQty
EffectiveReturnedQty
EffectiveConsumedQty
NetActiveAdjustmentQty
EffectiveSupplyQty
RemainingProductionMaterialQty
```

Required base totals:

```text
EffectiveIssuedBaseQty
EffectiveReturnedBaseQty
EffectiveConsumedBaseQty
NetActiveAdjustmentBaseQty
EffectiveSupplyBaseQty
RemainingProductionMaterialBaseQty
```

Do not mix RequiredUom quantity with BaseQty in one formula.

Use Qty totals for:
- Material Issue allowance;
- Work Order material UI;
- `RequiredQty` comparisons.

Use BaseQty totals for:
- ProductionBalLot reconciliation;
- contribution/stock checks.

Every caller uses the same effective facts:

For ADJUST rows, sign comes only from their linked `ProductionBalLotMovement`:

```text
ADJUST_IN  => +BaseQty
ADJUST_OUT => -BaseQty
```

A material ADJUST with a matching `ReversesMaterialMovementId` reversal is inactive.

Equations:

```text
EffectiveSupplyQty
= EffectiveIssuedQty
- EffectiveReturnedQty
+ NetActiveAdjustmentQty
```

used for Issue-to-Production cap/outstanding supply.

```text
RemainingProductionMaterialBaseQty
= EffectiveIssuedBaseQty
- EffectiveReturnedBaseQty
- EffectiveConsumedBaseQty
+ NetActiveAdjustmentBaseQty
```

used for Work Order physical material reconciliation.

Do not infer direction from:
- adjustment reason;
- signed UI quantity;
- document text.

The linked Production balance movement is authoritative.

---

## 45.3 Update Issue-to-Production calculations

Current Material Issue code uses:

```text
issued - returned
```

for:
- `AvailableToDraft`;
- remaining Work Order issue allowance;
- StandardRemaining;
- OutstandingQty;
- post/rollback result summaries.

Update all of those supply/allowance calculations to use `EffectiveSupplyQty`.

Keep audit fields:
- IssuedQty;
- ReturnedQty;
- ConsumedQty;

with their original meanings.

Add additive DTO/display fields where useful:

```text
ProductionAdjustmentQty
EffectiveSupplyQty
```

Do not silently redefine `NetIssuedQty` unless every caller and label is migrated explicitly.

Required example:

```text
required 100
issued   100
PADJ OUT 20
=> allow replacement issue 20
```

and:

```text
required 100
issued    80
PADJ IN   20
=> no further issue available
```

Search the entire branch for:
- direct RETURN sums;
- `NetIssuedQty`;
- `OpenRequirementQty`;
- `Outstanding`;
- `remainingWo`;
- `AvailableToDraft`;

before coding completion.

---

## 45.4 Work Order completion material reconciliation

Update:

`ProductionMaterialReconciliationService`

from:

```text
issued - returned - consumed
```

to:

```text
RemainingProductionMaterialBaseQty
```

and compare it against actual summed `MATERIAL_IN` ProductionBalLot BaseQty.

This must remain exact.

---

## 45.5 Open PRTN/PADJ drafts block Work Order completion

Current reconciliation already blocks open Material Issue drafts and PENDING Production links.

Also find undeleted NEW:
- Production Stock Return;
- Production Stock Adjustment;

for the Work Order.

Add finding:

```text
OPEN_PRODUCTION_STOCK_DRAFT
```

A Work Order cannot complete while a saved stock-changing Production draft still exists.

# 46. PRTN cross-ledger reconciliation

Add targeted reconciliation for Production Return facts.

Moving Average/FIFO:

```text
ReturnFact.TotalValue
== Production RETURN TotalCost
== IvTrxHistory.ExactTransferredValue
== StockValuationFact.CostAmount
```

Standard:

```text
Production actual value
- Inventory Standard value
== ProductionStandardCostVariance
```

Also validate:
- quantity;
- company/branch;
- item;
- posting;
- reversal linkage.

---

# 47. Inventory inquiry/display

Register friendly TrxType label for:

```text
PRTN = Production Stock Return
```

Inventory transaction inquiry should be able to display PRTN receipt history.

PADJ must not appear as an Inventory movement because it has no IvTrxHistory.

---

# 48. Production stock card/movement inquiry

Existing production inquiry reads `ProductionBalLotMovement`.

Ensure labels/display support:

```text
RETURN
RETURN_REVERSAL
ADJUST_IN
ADJUST_OUT
```

Show when user has cost permission:
- UnitCost;
- TotalCost;
- valuation status.

Show:
- document type/no;
- posting sequence;
- Work Order;
- lot;
- source/reversal identity.

---

# 49. Costing Center / diagnostics

Add ownership/diagnostic recognition for:
- Production Stock Return;
- Production Stock Adjustment.

Detect at minimum:

### PRTN
- missing ReturnFact;
- missing Inventory history;
- missing Inventory valuation;
- missing Production movement;
- missing material RETURN;
- wrong contribution allocation;
- qty mismatch;
- value mismatch;
- standard variance mismatch.

### PADJ
- missing AdjustmentFact;
- UNVALUED ADJUST_IN;
- pool qty/value mismatch;
- bad reversal.

Do not auto-repair by inventing cost.

Any repair adapter added later must reconstruct only from immutable authority.

---

# 50. UI design — shared principle


The new pages are classified as:

```text
PRODUCTION workflow/workspace
```

not Inventory transactions, even though their visual interaction may borrow from Inventory
Stock Return / Stock Adjustment.

Mandatory reading order before UI coding:

1. `.agents/skills/erp-transaction-ui-standard/skill.md`
2. `.agents/skills/erp-ui-core/skill.md`
3. `.agents/skills/production-transaction-ui/skill.md`
4. inspect live Production references:
   - `PrMaterialIssue*`
   - `PrFinishedGoodReceipt*`
5. inspect Inventory Return/Adjustment only for line-editor interaction ideas.

The coding agent must produce the repository-required evidence block before markup:

```text
PRODUCTION UI STANDARD EVIDENCE
Target workflow:
List reference:
Entry reference:
Workflow phase(s):
Source selector:
Locked context after selection:
Draft identity:
Statuses:
Save gate:
Post/readiness gate:
Rollback gate:
Delete/cancel gate:
Correction/reopen behavior:
Historical facts shown:
Live values explicitly shown:
Permissions:
Route model:
Intentional deviations:
```

`Intentional deviations` should be `None` unless documented by this plan.

UI workflow:

```text
Select Work Order / eligible Production source
    ->
locked Production workspace
    ->
SAVE NEW draft
    ->
POST
    ->
POSTED read-only
    ->
ROLLBACK
    ->
REVERSED read-only
    ->
CREATE CORRECTION
```

Rules:
- POST is not Save;
- Inventory source/destination/current stock shown as live values must be labelled as live;
- posted qty/value/facts shown in View mode come from immutable saved facts, never today's recomputation;
- source search must be server-paged/bounded;
- no `SaDocPage`;
- all orchestration in `.razor.cs`;
- use DevExpress controls and existing production/inventory picker components;
- cost editability follows backend authority and permission, never visual consistency.

# 51. Production Stock Return UI

Recommended files:

```text
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor.css

ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor.css
```

Recommended route family:

```text
/planning/production-stock-returns
/planning/production-stock-return/new
/planning/production-stock-return/{id}/edit
/planning/production-stock-return/{id}/view
```

Use actual BatchId key convention consistently.

---

## 51.1 Return header

- Work Order;
- Transaction date;
- Batch/Ref No;
- Status;
- Remark.

After Work Order selection it becomes locked document identity.

---

## 51.2 Return line grid

Show:

```text
Item
Description
Production Lot
Production Location
Production Qty
Returnable ISSUE Qty
Return Qty
UOM
Original IP Batch
Return Warehouse
Return Location
Return Lot
Reason
```

Cost permission adds:

```text
Production Unit Carrying Cost
Estimated Return Value
```

Estimated values are explicitly preview only until POST.

---

## 51.3 Line popup

User selects eligible Production MATERIAL_IN source.

Editable:
- Return Qty;
- Reason;
- remarks.

Read-only:
- item;
- Work Order;
- production location;
- production lot;
- original issue;
- return destination;
- cost.

Do not provide Inventory destination picker.

---

# 52. Production Stock Adjustment UI

Recommended files:

```text
PrProductionStockAdjustmentList.razor/.cs/.css
PrProductionStockAdjustmentEntry.razor/.cs/.css
```

Header:
- Work Order;
- date;
- batch/ref;
- status;
- remark.

Line source picker:
- existing Production balance.

Show:
- stage;
- production location;
- lot;
- current qty;
- UOM;
- signed adjustment;
- new qty preview;
- reason.

With ViewCost:
- current pool unit cost;
- current pool value;
- resolved cost basis;
- estimated value change.

---

## 52.1 Positive adjustment cost UI

Default:
- system-calculated cost displayed read-only.

Only when service says automatic authority unavailable AND user has `PriceOverride`:
- enable explicit approval editor;
- evidence type;
- approved unit cost;
- mandatory reason.

Do not always show an editable Unit Cost field.

---

# 53. List lifecycle/actions

For both:

```text
NEW
POST
ROLLBACK
DELETE
```

For REVERSED:
- `CREATE CORRECTION`.

State rules:

```text
NEW      -> editable
POSTED   -> read-only
REVERSED -> read-only
```

No direct edit of POSTED/REVERSED.

---

# 54. Posting confirmation

Because material can be expensive, confirmation should show quantity and value.

Example Return:

```text
Return 1,250.0000 KG from Production to
WH01 / RM / LOT-C001.

Estimated Production carrying value:
RM 48,725.340000

The final value is recalculated under posting locks.
```

PADJ:

```text
Adjust Production stock by -120.0000 KG.
Estimated value reduction: RM ...

POST creates an auditable Production stock movement.
```

Final server calculation is authoritative.

---

# 55. Rounding contract

Quantity:

```text
IvQty.Scale = 4
MidpointRounding.AwayFromZero
```

Financial value:

```text
StockLedgerPrecision.Money
decimal scale 6
```

Use exact total value as financial authority.

Do not derive final money from UI-rounded 4-decimal `UnitPrice`.

---

# 56. SQL/schema constraints


Create idempotent schema for:

```text
PrProductionStockReturn
PrProductionStockReturnLine
PrProductionStockReturnFact
PrProductionStockReturnPriceSnapshot

PrProductionStockAdjustment
PrProductionStockAdjustmentLine
PrProductionStockAdjustmentFact
```

Also:
- add `RETURN_REVERSAL` to PrMaterialMovement constraint;
- all financial values 19,6;
- quantities 18,4;
- factors 18,8;
- all FKs Restrict;
- filtered unique reversal indexes;
- fact/posting unique indexes;
- primary PRTN/PADJ ProductionPostingLink/batch uniqueness where appropriate.

Do not drop/rebuild stock-ledger tables.

## Mandatory SQL preflight before enabling POST

Verify the deployed DB already has the prerequisites introduced by the current Production scripts.

### `PrMaterialMovement` nullable production-only lineage columns

These must permit null for Production-only material facts:

```text
FromBalLocID
InventoryBatchID
InventoryBatchNo
InventoryBatchDetailID
InventoryTrxLineNo
```

The current repo's `create-production-daily-output.sql` relaxes them.

If production DB still has legacy NOT NULL definitions:

```text
ABORT DEPLOYMENT
```

Apply the prerequisite migration first.

### V2 seal triggers

Require:

```text
TR_PrProductionBalLotMovement_Seal
TR_PrMaterialMovement_Seal
TR_PrProductionMovementAllocation_Seal
```

### Current movement constraints/indexes

Verify:
- V2 stock movement constraint includes all current codes;
- V2 posting-line uniqueness;
- reversal uniqueness;
- source-line uniqueness;
- material source/reversal FKs.

### No destructive feature rollback

Once PRTN/PADJ has posted live facts, emergency rollback is:

```text
disable posting feature flags
```

not dropping schema or historical evidence.

# 57. DbSets/configuration


Add strongly typed DbSets and separate configuration classes.

Precision:

```text
qty/frozen BaseQty        18,4
conversion factor         18,8
Production unit/value     19,6
ProposedUnitCost          19,6
```

Never downgrade Production cost authority to the generic detail's 18,4 price columns.

Document headers use rowversion.

Add immutable fact/reversal uniqueness.

Keep `UseSqlOutputClause(false)` where required by table triggers/current repo patterns.

Add a schema/preflight integration test that executes against SQL Server metadata, not only EF model
metadata.

# 58. Service registration

Update:

`CoreServiceCollectionExtensions.cs`.

Register:
- Return service;
- Return source/contribution service if separate;
- Adjustment service;
- any focused internal helper.

Do not use service locator.

---

# 59. Required unit tests — return eligibility


Release blockers:

- only Inventory-issued MATERIAL_IN is eligible;
- exact material ISSUE -> balance ISSUE chain required;
- exact source `FromBalLocId` triple identity required;
- source BalLoc missing => reject;
- source BalLoc key mismatch => reject;
- lot identity missing/mismatched/invalid => reject;
- WIP / PROCESS_WIP / ROUTE_WIP / FG staging rejected;
- pool without original ISSUE rejected;
- reversed ISSUE rejected;
- UNVALUED pool rejected;
- Qty/BaseQty/factor scalar mismatch rejected;
- AverageUnitCost/TotalCost scalar mismatch rejected;
- wrong Work Order/branch/company rejected;
- old valuation-generation issue contribution rejected;
- missing original Inventory history rejected;
- DRAFT/COMPLETED/CLOSED/CANCELLED Work Order rejected for new POST;
- snapshot revision/hash changed after Save => POST rejected;
- posting feature disabled => new POST rejected;
- rollback remains available while posting feature disabled.

# 60. Required tests — return contribution

- full unused issue returnable.
- partial consume reduces returnable.
- multiple consumes reduce returnable.
- consume reversal restores returnable.
- active prior Return reduces returnable.
- Return reversal restores returnable.
- ADJUST_IN does not increase returnable.
- ADJUST_OUT allocation can reduce original ISSUE contribution.
- ADJUST_OUT reversal restores contribution.
- return cannot exceed issue contribution even when pool qty is higher.
- return cannot exceed current Production pool qty.

---

# 61. Required tests — PRTN costing


## Moving Average

- exact Production value relieved;
- exact value copied to `IvTrxHistory.ExactTransferredValue` at 19,6;
- Inventory valuation receipt value equals it;
- 4-decimal detail UnitPrice cannot change financial result;
- Inventory cost state updates correctly;
- full Production depletion leaves exact zero qty/value.

## FIFO

- PRTN creates a new FIFO receipt fact/layer at exact Production transferred value;
- does not reopen old ISSUE-consumed FIFO layers;
- exact Production value == new FIFO receipt value.

## Standard

- Inventory receipt uses current effective Standard Cost;
- Production relief remains actual current Production value;
- `ProductionStandardCostVariance = ActualProductionValue - StandardInventoryValue`;
- exact reversal makes net variance zero.

## Inventory stock-in integration

- PRTN calls caller-owned `PostStockInInTransactionAsync` with expected type PRTN;
- resulting ToBalLocId/ToLotId exactly equal original ISSUE FromBalLocId/FromLotId;
- same Production StockPosting/DbContext/transaction;
- generic stock-in locking/lot/chronology is used;
- no standalone Inventory StockPosting is created;
- PRTN history is `VALUED` after valuation;
- PRTN history is never manually marked VERIFIED before valuation.

## Precision

Test awkward rates/quantities where a 4-decimal display unit cost would produce a different result
than exact 19,6 total-value calculation. The exact transfer amount must win.

# 62. Required PRTN atomicity tests

Failure injection at:

1. after Inventory balance increase;
2. after Inventory history;
3. after ProductionStockWriter;
4. after ProductionPoolValuationService;
5. after material RETURN;
6. after Inventory valuation;
7. before seal.

After each forced failure:

```text
no persistent quantity change
no persistent value change
no orphan history
no orphan production movement
no orphan fact/allocation
```

---

# 63. Required PRTN concurrency tests

SQL Server integration:

- two PRTN posts same source;
- PRTN vs Daily Production consume;
- PRTN vs PADJ OUT;
- PRTN vs Material Issue rollback;
- PRTN vs PRTN rollback.
- PRTN Save draft vs Work Order completion race.

Assert:
- no double-return;
- no negative stock;
- no duplicate allocations;
- deterministic failure message/retry.

---

# 64. Required PRTN rollback tests


- exact append-only qty/value reversal;
- original PRTN batch remains immutable and becomes REVERSED;
- original details keep their history identities;
- no generic stock-in rollback/reopen-to-NEW path is used;
- Inventory history reversal links exact original;
- StockValuationFact reversal exact;
- RETURN_REVERSAL links original Production movement;
- material RETURN_REVERSAL links original material RETURN;
- contribution allocation reversal exists;
- ReturnedQty/effective supply returns to correct value;
- Standard variance reversal exact;
- later active Inventory movement blocks rollback;
- **any later active forward Production movement on same pool blocks rollback**, including inbound;
- active pooled dependency blocks rollback;
- operational price snapshot restores only when posted-state guard matches;
- repeated RequestId replays;
- duplicate rollback rejected;
- posting-disabled feature flag does not block rollback.

# 65. Required PADJ tests


## Quantity / isolation

- positive works;
- negative works;
- zero rejected;
- negative beyond stock rejected;
- PADJ creates no IvTrxHistory;
- PADJ creates no Inventory StockValuationFact;
- PADJ changes no IvBalLoc;
- generic PADJ detail has no Inventory balance IDs;
- scalar-invalid Production pool rejected.

## Positive value

- nonempty VERIFIED pool uses exact proportional current Production pool value under MA/FIFO/Standard;
- total value is calculated first, then unit cost;
- nonempty Standard branch does not inject Inventory Standard Cost;
- empty verified MATERIAL_IN uses exact proportional ORIGINAL_ISSUE_COST;
- empty other pool may use approved effective Standard Cost;
- missing Standard authority falls to explicit approved path, never Item Master price;
- MANUAL_APPROVED uses 19,6 sidecar ProposedUnitCost and freezes approval evidence into AdjustmentFact;
- ZERO_COST_APPROVED requires exact zero;
- PriceOverride enforced server-side;
- approval metadata cleared/revalidated when draft financial inputs change;
- generic `IvTrxBatchDetail.UnitPrice` cannot become financial authority;
- positive into nonempty UNVALUED pool rejected.

## Negative value

- partial = Money(pool.TotalCost * qty / pool.BaseQty);
- full depletion uses exact remaining pool.TotalCost;
- no stranded value.

## Raw-material execution

- MATERIAL_IN PADJ writes one linked material ADJUST;
- sign comes from linked ADJUST_IN/ADJUST_OUT;
- raw-material PADJ OUT reduces EffectiveSupply and permits replacement Material Issue;
- raw-material PADJ IN increases EffectiveSupply and prevents excess further issue;
- IssuedQty retains true Inventory issue meaning;
- Work Order remaining-material reconciliation equals live MATERIAL_IN;
- open PADJ draft blocks Work Order completion;
- PADJ on invalid Work Order status rejected;
- Work Order snapshot drift rejects POST.

## Contribution lineage

- ADJUST_IN is a separate production contribution;
- does not increase PRTN returnable original ISSUE qty;
- outbound allocation can consume adjusted contribution according to existing allocator;
- rollback restores allocation/dependency state.

# 66. Required PADJ rollback/concurrency tests


- rollback ADJUST_IN before dependent/later movement succeeds;
- rollback ADJUST_IN with active dependent consumption blocked;
- rollback ADJUST_OUT exact;
- any later active forward movement on same pool blocks rollback;
- forward ADJUST_IN + inverse rollback ADJUST_OUT collapses from EffectiveMovements;
- forward ADJUST_OUT + inverse rollback ADJUST_IN collapses;
- material ADJUST reversal cancels forward material adjustment;
- EffectiveSupply and Work Order reconciliation return exactly to pre-PADJ state;
- PADJ vs Daily Production race;
- PADJ vs PRTN race;
- two negative PADJ race;
- PADJ vs Material Issue race;
- PADJ Save draft vs Work Order completion race;
- no negative qty/value;
- no double contribution allocation.

# 67. SQL Server tests are mandatory


Do not approve code with only EF InMemory/SQLite.

Mandatory SQL Server coverage:
- branch lock behavior;
- `UPDLOCK/HOLDLOCK`;
- rowversion;
- actual database decimal precision;
- filtered indexes;
- exact `PrMaterialMovement` nullable-column preflight;
- seal triggers;
- PRTN generic stock-in invocation inside Production StockPosting;
- source BalLoc exact-lock identity;
- concurrent PRTN/PADJ/Daily Production/Material Issue races;
- append-only reversal;
- full transaction rollback on injected failures;
- schema constraints for new tables;
- feature flags default disabled after initial deploy.

Run with representative 18,4 quantities and 19,6 money values, including expensive material.

# 68. Regression tests — existing Production


All existing tests stay green, especially:
- Material Issue Save/Post/Rollback;
- Material Issue max issue / outstanding calculations;
- Daily Production;
- Production contribution allocation;
- ProductionCostReadiness;
- Work Order completion reconciliation;
- Finished Good Receipt;
- FG cost trace;
- Inventory MR/CR/GR/ADJ posting;
- Inventory MA/FIFO/Standard valuation;
- stock ledger cutover/reconciliation.

Add regression proofs:

```text
ProductionPoolValuationService existing overload with no directives
=> existing behavior unchanged.

PRTN forward Return material dependency
=> source Issue rollback blocked.

PRTN rollback
=> Return dependency no longer active.

raw PADJ OUT
=> replacement issue allowance increases by exact adjustment qty.

raw PADJ IN
=> further issue allowance decreases by exact adjustment qty.

PADJ rollback
=> issue allowance and Work Order material reconciliation return to exact prior state.

existing non-PRTN stock-in transactions
=> unchanged by PRTN expectedTrxType support/comment changes.

existing FG Receipt
=> unchanged.
```

# 69. Implementation phases


## Phase 0 — baseline / preflight

Before code:
- capture reviewed branch SHA;
- run existing Production/Inventory/StockLedger tests;
- run Production qty/value reconciliation on representative DB;
- run SQL metadata preflight;
- verify seal triggers and material nullable-column prerequisites.

## Phase 1 — domain/schema/options

- constants;
- sidecar/fact entities;
- 19,6 cost fields;
- snapshot revision/hash;
- configs;
- write guards;
- SQL;
- rollout options default disabled.

No posting UI activation.

## Phase 2 — one authoritative Production material execution calculator

Refactor/extend material totals for:
- effective issue;
- effective return;
- effective consume;
- signed active adjustment;
- effective supply;
- remaining production material.

Migrate Material Issue workspace/Save/Post/rollback summaries and Work Order reconciliation to it.

Do this before PADJ posting exists.

## Phase 3 — Production valuation directives/contribution authority

Add backward-compatible overload/options for:
- required Return receipt contribution;
- verified ADJUST_IN valuation authority.

Regression-test old callers with null directives.

## Phase 4 — PRTN backend

- source/readiness;
- exact original BalLoc/lot validation;
- Save;
- Production-owned StockPosting;
- existing Inventory stock-in core;
- exact history transfer stamping;
- Inventory valuation;
- Production Return;
- material Return;
- facts/invariants;
- strict rollback;
- correction;
- tests.

## Phase 5 — PRTN UI

UI can save drafts while POST remains feature-disabled during staging.

## Phase 6 — PADJ backend

- sidecar 19,6 approval;
- exact value formulas;
- material ADJUST/effective supply;
- POST;
- invariant;
- strict rollback;
- correction;
- tests.

## Phase 7 — PADJ UI

## Phase 8 — inquiry/reconciliation/cost trace

## Phase 9 — controlled activation

1. DB backup/restore point;
2. deploy schema/code with POST flags false;
3. run SQL preflight;
4. run staging/golden tests against SQL Server;
5. run Production stock qty/value reconciliation;
6. verify no unexplained critical findings;
7. enable Return posting for controlled users/site;
8. monitor reconciliation;
9. enable Adjustment posting only after Return path is stable;
10. keep emergency disable switch available.

Never “roll back” live historical facts by dropping schema.

# 70. AI coding agent checkpoint gates


The agent must stop at each gate.

## Gate A — schema/preflight

PASS only if:
- current DB prerequisite nullability/triggers confirmed;
- 18,4 / 19,6 precision correct;
- new constraints/indexes applied;
- POST flags default disabled.

## Gate B — material execution math

PASS only if:
- EffectiveSupply and RemainingProductionMaterial are centralized;
- Material Issue Save/UI/Post results use adjustment-aware supply;
- raw PADJ shortage permits replacement issue;
- raw PADJ surplus prevents excess issue;
- Work Order completion reconciliation remains exact.

## Gate C — contribution authority

PASS only if:
- PRTN quantity uses exact source ISSUE contribution;
- PADJ contribution cannot leak into returnable ISSUE quantity;
- old Production valuation callers unchanged when directives absent.

## Gate D — PRTN physical Inventory leg

PASS only if:
- exact original source BalLoc/lot is validated;
- generic caller-owned stock-in engine is reused;
- no standalone Inventory StockPosting created;
- exact 19,6 transfer is stamped to V2 history.

## Gate E — PRTN costing

PASS only if:
- MA/FIFO exact value equality;
- Standard variance equation exact;
- full-depletion zero-value invariant.

## Gate F — PRTN rollback

PASS only if:
- custom append-only POSTED->REVERSED flow;
- strict per-pool LIFO;
- exact Inventory/Production/value reversal.

## Gate G — PADJ

PASS only if:
- no Inventory side effect;
- exact 19,6 value authority;
- Production pool stays VERIFIED;
- material ADJUST/effective-supply behavior exact;
- inverse adjustment collapse works.

## Gate H — concurrency / SQL Server

PASS only after real SQL Server race/failure-injection tests.

## Gate I — UI

PASS production UI standards and readiness/error visibility.

## Gate J — production activation

PASS only after reconciliation is clean and explicit posting flags are enabled intentionally.

# 71. Explicit rejection rules


Implementation is NOT APPROVED if any applies:

1. PRTN reuses Customer Return `CR`.
2. PADJ uses Inventory `ADJ` posting.
3. PRTN StockPosting is sourced as INVENTORY.
4. PRTN hand-codes Inventory stock-in instead of using caller-owned `PostStockInInTransactionAsync`.
5. PRTN calls the standalone Inventory dispatcher and creates a second StockPosting.
6. PRTN creates a replacement destination when original ISSUE BalLoc identity is missing/mismatched.
7. one PRTN/PADJ batch mixes Work Orders.
8. Work Order snapshot revision/hash is not revalidated at POST.
9. PRTN returnable qty is derived only from ProductionBalLot.BaseQty.
10. ADJUST_IN quantity becomes returnable as original ISSUE quantity.
11. PRTN user can freely choose a different Inventory destination.
12. Return cost is user-entered.
13. PRTN exact value is reconstructed from 4-decimal UnitPrice.
14. PADJ financial authority relies only on IvTrxBatchDetail.UnitPrice.
15. Item Master PurchasePrice/latest PO/selling price is used silently as Production cost.
16. ProductionBalLot is mutated outside ProductionStockWriter.
17. financially-valued PRTN/PADJ movement omits ExactTotalValue.
18. positive ADJUST_IN is allowed to remain UNVALUED.
19. nonempty verified pool ADJUST_IN uses Inventory Standard Cost instead of current Production carrying value.
20. PADJ total value is derived by multiplying qty by a rounded display average when exact proportional total is available.
21. forward RETURN balance movement incorrectly sets OriginalMovementId to source ISSUE (that field would make it look like a reversal).
22. forward **material RETURN** fails to set OriginalMovementId + SourceIssueMovementId to source material ISSUE.
23. strict Return quantity directive can allocate against ADJUST_IN contribution.
24. raw-material PADJ changes the Production lot without a linked material ADJUST fact.
25. raw-material PADJ is ignored by Material Issue available-to-issue/outstanding calculations.
26. raw-material PADJ is ignored by Work Order material reconciliation.
27. PADJ creates IvTrxHistory / StockValuationFact / IvBalLoc change.
28. posted movement/fact/history is deleted or overwritten on rollback.
29. PRTN rollback calls generic MR/CR rollback and reopens the batch to NEW.
30. PRTN/PADJ rollback is allowed beneath a later active forward Production pool movement.
31. reversal material facts are still treated as permanently blocking the source ISSUE.
32. adjustment inverse movements are not collapsed from EffectiveMovements replay.
33. StockPosting seals before all qty/value/lineage invariants pass.
34. Inventory and Production legs commit in separate SQL transactions.
35. PRTN lacks immutable cross-ledger ReturnFact.
36. DRAFT/COMPLETED/CLOSED/CANCELLED Work Order accepts a new PRTN/PADJ POST.
37. open PRTN/PADJ draft is ignored by Work Order completion.
38. SQL preflight does not verify legacy PrMaterialMovement nullability/seal triggers.
39. only SQLite/InMemory tests are used for release approval.
40. existing FG/Material Issue behavior changes when new valuation directives are absent.
41. feature is activated in production before reconciliation/golden/race tests pass.
42. PRTN/PADJ Save does not lock Work Order against completion race.
43. material totals mix Work Order Qty with BaseQty.
44. AdjustmentFact uses an ambiguous polymorphic cost authority instead of explicit immutable authority fields.
45. coding agent performs broad unrelated Production refactors.

# 72. Exact existing files expected to change


High-confidence existing files:

```text
ErpWeb.Core/Inventory/IvTrxConstants.cs
ErpWeb.Core/Inventory/IIvInventoryPostingService.cs
# IvInventoryPostingService.cs should only need minimal/comment or narrowly-scoped support;
# do not fork/rewrite its generic stock-in core.

ErpWeb.Model/Data/AppDbContext.cs
ErpWeb.Model/Entities/Inventory/IvTrxHistory.cs  # update ExactTransferredValue comment from FG-only to Production-transfer authority

ErpWeb.Model/Entities/Production/ProductionDomainConstants.cs
ErpWeb.Model/Configurations/Production/ProductionMaterialMovementConfiguration.cs

ErpWeb.Core/Production/ProductionMaterialMovementTotals.cs
ErpWeb.Core/Production/ProductionWorkOrderCalc.cs
ErpWeb.Core/Production/ProductionMaterialReconciliationService.cs

ErpWeb.Core/Production/ProductionMaterialIssueService.cs
ErpWeb.Core/Production/ProductionMaterialIssueService.Draft.cs
ErpWeb.Core/Production/ProductionMaterialIssueService.Posting.cs
ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs
ErpWeb.Core/Production/ProductionMaterialIssueService.Rollback.cs
ErpWeb.Core/Production/IProductionMaterialIssueService.cs

ErpWeb.Core/Production/ProductionPoolValuationService.cs
ErpWeb.Core/Production/ProductionPostingInvariant.cs
ErpWeb.Core/Production/ProductionStockHistoryService.cs
ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.CostTrace.cs

ErpWeb.Core/StockLedger/InventoryValuationService.cs

ErpWeb.Core/Menus/MenuCodes.cs
ErpWeb/Menus/menus.xml
ErpWeb.Core/CoreServiceCollectionExtensions.cs

ErpWeb.Core/Transactions/TransactionDeleteContracts.cs
ErpWeb.Core/Transactions/TransactionDeletePolicyService.cs

scripts/<new idempotent production return-adjustment migration/preflight>
```

The agent must branch-wide search every direct material-total calculation and Return/Adjustment usage
before declaring completion.

Do not edit an existing file merely because it appears here if implementation can preserve it
unchanged; this is an impact map, not permission for broad refactoring.

# 73. New backend files — recommended

```text
ErpWeb.Model/Entities/Production/ProductionStockReturn.cs
ErpWeb.Model/Configurations/Production/ProductionStockReturnConfiguration.cs

ErpWeb.Model/Entities/Production/ProductionStockAdjustment.cs
ErpWeb.Model/Configurations/Production/ProductionStockAdjustmentConfiguration.cs

ErpWeb.Core/Production/IProductionStockReturnService.cs
ErpWeb.Core/Production/ProductionStockReturnService.cs
ErpWeb.Core/Production/ProductionStockReturnService.Posting.cs
ErpWeb.Core/Production/ProductionStockReturnService.CostTrace.cs

ErpWeb.Core/Production/IProductionStockAdjustmentService.cs
ErpWeb.Core/Production/ProductionStockAdjustmentService.cs
ErpWeb.Core/Production/ProductionStockAdjustmentService.Posting.cs
ErpWeb.Core/Production/ProductionStockAdjustmentService.CostTrace.cs
```

Optional focused helper:

```text
ProductionContributionAvailabilityService.cs
```

Do not build one giant generic ProductionStockTransactionService.

Return and Adjustment have different accounting semantics.

---

# 74. New UI files — recommended

```text
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor.css
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor.css

ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentList.razor
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentList.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentList.razor.css
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor.css
```

---

# 75. Final 10/10 acceptance equations

These are the final financial gates.

## PRTN — Moving Average/FIFO

For each line:

```text
ProductionQtyOut == InventoryBaseQtyIn

ProductionValueOut
== ReturnFact.TotalValue
== IvTrxHistory.ExactTransferredValue
== InventoryValuationFact.CostAmount
```

For document:

```text
sum ProductionValueOut
== sum Inventory Financial Receipt Value
```

---

## PRTN — Standard

```text
ProductionValueOut
== ReturnFact.TotalValue
== ExactTransferredValue

InventoryValueIn
== current Standard Cost * BaseQty

ProductionValueOut
- InventoryValueIn
== ProductionStandardCostVariance
```

Reversal:

```text
original + reversal = 0
```

for qty/value/variance.

---

## PADJ

For each movement:

```text
NewProductionQty
= OldProductionQty
+ movementDirection * BaseQty

NewProductionValue
= OldProductionValue
+ movementDirection * TotalValue
```

After every POST/rollback:

```text
ProductionPoolValuation.TrackedBaseQty
== ProductionBalLot.BaseQty

ProductionPoolValuation.TrackedValue
== ProductionBalLot.TotalCost
```

and:

```text
ProductionBalLot.BaseQty >= 0
ProductionBalLot.TotalCost >= 0

if BaseQty == 0:
    TotalCost == 0
```

For MATERIAL_IN:

```text
LiveMaterialBaseQty
==
EffectiveIssueBaseQty
- EffectiveReturnBaseQty
- EffectiveConsumeBaseQty
+ NetActiveMaterialAdjustmentBaseQty
```

where material adjustment direction comes from the linked production balance movement.

A PADJ rollback must restore this equation to the exact pre-PADJ result.

---



## Production source scalar invariant

Before every PRTN/PADJ POST:

```text
Qty == IvQty.Round(BaseQty / ConversionFactorToBase)

if BaseQty > 0:
    AverageUnitCost == Money(TotalCost / BaseQty)

if BaseQty == 0:
    Qty == 0
    TotalCost == 0
    AverageUnitCost == 0
```

## Raw-material execution invariant

```text
EffectiveSupplyQty
= EffectiveIssuedQty
- EffectiveReturnedQty
+ NetActiveAdjustmentQty

RemainingProductionMaterialBaseQty
= EffectiveIssuedBaseQty
- EffectiveReturnedBaseQty
- EffectiveConsumedBaseQty
+ NetActiveAdjustmentBaseQty

RemainingProductionMaterialBaseQty
== summed live MATERIAL_IN BaseQty
```


# 75A. Mandatory golden numerical scenarios

The coding agent must encode these as deterministic tests. Equivalent values are acceptable only if
the same repo rounding rules produce them.

## 75A.1 Raw-material Issue -> Consume -> PADJ IN -> Return

Starting ISSUE:

```text
Issued raw material       = 100.0000 KG
Production ISSUE value    = 1,000.000000
Production unit cost      = 10.000000
```

After Daily Production consumes 40:

```text
MATERIAL_IN pool qty      = 60.0000
MATERIAL_IN pool value    = 600.000000
remaining ISSUE contribution = 60.0000
```

Positive PADJ +20 on this nonempty verified pool:

```text
PADJ cost basis           = CURRENT_PRODUCTION_POOL_COST
PADJ unit cost            = 10.000000
PADJ value                = 200.000000

pool qty                  = 80.0000
pool value                = 800.000000
returnable ISSUE qty      = still 60.0000
```

Work Order material reconciliation must be:

```text
100 issued
- 0 returned
- 40 consumed
+ 20 net adjustment
= 80 live MATERIAL_IN
```

Now PRTN returns 50:

```text
PRTN BaseQty              = 50.0000
PRTN exact value          = 500.000000

Production pool qty       = 30.0000
Production pool value     = 300.000000

Inventory qty in          = 50.0000
Inventory exact transfer  = 500.000000

Work Order:
100 issued
- 50 returned
- 40 consumed
+ 20 adjustment
= 30 live MATERIAL_IN
```

Remaining original ISSUE contribution:

```text
10.0000 KG
```

A PRTN request for 11 must fail even though Production pool qty is 30.

---

## 75A.2 PADJ OUT reduces returnable ISSUE contribution

Continue from the prior state:

```text
pool qty                  = 30
remaining ISSUE contribution = 10
adjustment contribution   = 20
```

PADJ OUT 5 uses current Production pool cost:

```text
value out                 = 50
pool qty                  = 25
pool value                = 250
```

With current FIFO contribution allocation, the outbound quantity consumes the earliest available
ISSUE contribution first:

```text
remaining ISSUE contribution = 5
```

A PRTN request for 6 must fail.

After PADJ rollback:
- pool returns to 30 / 300;
- adjustment allocation is reversed;
- returnable ISSUE contribution returns to 10;
- Work Order material reconciliation returns to the exact pre-PADJ state.

---

## 75A.3 FIFO Inventory ISSUE then Production Return

Inventory ISSUE to Production consumes FIFO layers:

```text
40 @ 8.000000  = 320.000000
20 @ 10.000000 = 200.000000

Issue qty       = 60.0000
Production pool = 60.0000 / 520.000000
Production avg  = 8.666667 (display/rate precision)
```

Production consumes 20:

```text
consume value   = Money(520 * 20 / 60)
                = 173.333333

pool remaining  = 40.0000 / 346.666667
```

PRTN returns 10:

```text
return value    = Money(346.666667 * 10 / 40)
                = 86.666667
```

Expected Inventory FIFO behavior:

```text
new FIFO receipt qty   = 10.0000
new FIFO receipt value = 86.666667
```

It must **not** reopen the old 8.00/10.00 FIFO layers.

Production and Inventory value conservation:

```text
Production value decrease == 86.666667
Inventory FIFO value increase == 86.666667
```

---

## 75A.4 Standard Cost return variance

```text
Production actual return value = 100.000000
Inventory Standard receipt     = 110.000000
```

Expected:

```text
ProductionStandardCostVariance = -10.000000
```

Rollback:

```text
reversal variance = +10.000000
net variance      = 0
```

---

## 75A.5 Empty MATERIAL_IN positive adjustment

A MATERIAL_IN pool has reached:

```text
qty   = 0
value = 0
```

Its verified original ISSUE was:

```text
100 KG
value = 1,250
unit  = 12.500000
```

PADJ IN +4 because physical count finds leftover raw material:

```text
CostBasisCode = ORIGINAL_ISSUE_COST
UnitCost      = 12.500000
TotalValue    = 50.000000
new pool      = 4 / 50
```

This new generation is VERIFIED.

The +4 adjustment contribution is **not PRTN-returnable** as original ISSUE contribution.

---

## 75A.6 Full depletion money rule

For any PRTN or PADJ OUT:

if:

```text
requested BaseQty == current ProductionBalLot.BaseQty
```

then:

```text
ExactTotalValue = current ProductionBalLot.TotalCost
```

not `qty × rounded unit cost`.

After the movement:

```text
BaseQty   = 0
TotalCost = 0
```

No stranded RM 0.000001 value is permitted.

---



# 75B. Additional red-team numerical gates

## 75B.1 PADJ OUT shortage must permit replacement Issue

```text
WO required                      100.0000
actual Inventory issue           100.0000
returned                           0.0000
consumed                           0.0000
PADJ OUT shortage                 20.0000
```

Expected:

```text
IssuedQty                        100.0000   // audit truth unchanged
NetActiveAdjustment             -20.0000
EffectiveSupply                  80.0000
Further issue allowance          20.0000
RemainingProductionMaterial      80.0000
```

After replacement Issue 20:

```text
IssuedQty                        120.0000
EffectiveSupply                 100.0000
Further issue allowance           0.0000
RemainingProductionMaterial     100.0000
```

The system must not block the replacement issue merely because historical IssuedQty already reached
100.

---

## 75B.2 PADJ IN found stock must prevent excess Issue

```text
WO required                      100.0000
IssuedQty                         80.0000
PADJ IN                           20.0000
```

Expected:

```text
EffectiveSupply                 100.0000
Further issue allowance           0.0000
```

Do not silently change IssuedQty to 100.

---

## 75B.3 Exact proportional positive adjustment value

Pool:

```text
BaseQty   = 3.0000
TotalCost = 10.000000
```

PADJ IN:

```text
AddBaseQty = 2.0000
```

Correct:

```text
TotalValue = Money(10 * 2 / 3)
           = 6.666667

UnitCost   = Money(6.666667 / 2)
           = 3.333334
```

Do not calculate:

```text
rounded average 3.333333 * 2
```

as financial authority.

After post:

```text
pool BaseQty   = 5.0000
pool TotalCost = 16.666667
```

---

## 75B.4 Quantity source can differ from pooled value source

Initial verified ISSUE contribution:

```text
100 KG @ total value 1,000
```

then valued PADJ IN:

```text
20 KG @ total value 300
```

pool:

```text
120 KG / 1,300
```

PRTN requests 50 KG and the exact ISSUE contribution has at least 50 remaining.

Quantity authority:

```text
50 KG must allocate only to original ISSUE contribution
```

Financial authority:

```text
ReturnValue = Money(1,300 * 50 / 120)
            = 541.666667
```

Do **not** force value to 500 merely because returned quantity is ISSUE-origin.

This is the deliberate current Production pooled-value model.

---

## 75B.5 Rollback generation protection

If PRTN/PADJ posting sequence 100 touches pool P, and a later active forward movement at sequence 101
touches P:

```text
rollback sequence 100 => BLOCKED
```

even if current qty is numerically sufficient.

Reverse sequence 101 first.

---


# 76. Final architecture

## Production Stock Return

```text
IvTrxBatch PRTN
  + IvTrxBatchDetail
  + PrProductionStockReturn / Line
             |
             v
      PRODUCTION StockPosting
             |
     ----------------------
     |                    |
     v                    v
Production RETURN      Inventory PRTN receipt
via StockWriter        IvBalLoc + IvTrxHistory
     |                    |
     v                    v
ProductionPool         InventoryValuationService
Valuation              MA / FIFO / STANDARD
     |                    |
     -----------+----------
                v
       PrProductionStockReturnFact
                |
                v
     exact qty/value reconciliation
```

Quantity provenance:

```text
PRTN RETURN
must allocate only against
original ISSUE ProductionBalLotMovement
```

---

## Production Stock Adjustment

```text
IvTrxBatch PADJ
  + IvTrxBatchDetail
  + PrProductionStockAdjustment / Line
             |
             v
      PRODUCTION StockPosting
             |
             v
ADJUST_IN / ADJUST_OUT
      via ProductionStockWriter
             |
             v
ProductionPoolValuationService
             |
             v
PrProductionStockAdjustmentFact

NO IvBalLoc mutation
NO IvTrxHistory
NO Inventory StockValuationFact
```

---

# 77. Final approval statement


**APPROVED FOR IMPLEMENTATION — 10/10 PLAN QUALITY**

Reviewed against current `productionv2` HEAD:

```text
61a4a66099061ef07157af15f8020cb257077420
```

This v5 is materially safer than v4 and supersedes it.

The final red-team review now requires:

- exact original ISSUE contribution for PRTN quantity;
- current verified pooled Production carrying value for PRTN financial relief;
- exact original Inventory BalLoc/lot identity with fail-closed corruption handling;
- the existing caller-owned Inventory stock-in engine for PRTN physical Inventory posting;
- 19,6 exact transfer value independent of 4-decimal display price;
- MA/FIFO exact cross-ledger value conservation;
- explicit reversible Standard Cost variance;
- Production-only PADJ with no Inventory movement;
- 19,6 PADJ manual cost authority in the Production sidecar;
- exact-total-first PADJ valuation formulas;
- raw-material PADJ integrated into Material Issue supply allowance and Work Order completion
  reconciliation without falsifying IssuedQty;
- Work Order snapshot drift protection;
- strict per-pool LIFO rollback;
- append-only material/balance/valuation reversal;
- SQL schema/triggers/nullability preflight;
- posting feature flags and controlled activation;
- real SQL Server race/failure-injection/golden tests.

### Production release decision

The **plan is approved**.

The implementation is **not production-approved merely because it compiles**.

The coding agent must not enable live posting until every mandatory gate in §§59–70 passes,
especially:
- exact qty/value invariants;
- Material Issue replacement/excess scenarios after PADJ;
- MA/FIFO/Standard costing;
- strict rollback;
- SQL Server concurrency;
- clean Production reconciliation before and after posting.

Any failure in those gates is a release blocker.
