# ERP Costing Foundation --- Production-Branch-Aligned 10/10 Approved Implementation Plan

**Repository:** `mokth/net10projectTemplate` **Authoritative target
branch:** `production` **Status:** **APPROVED FOR IMPLEMENTATION ---
10/10** **Scope:** Inventory valuation, Procurement cost, Production
RM/WIP/FG cost, Sales COGS, period close, reconciliation, cutover, and
management-report readiness.

## Repository Re-Review Corrections Incorporated

This v3 plan incorporates three mandatory corrections verified against
the current `production` branch:

1.  **Sales DO/Invoice COGS follows the existing `LinkDo` behavior.** A
    `LinkDo` invoice line must not create another stock-out or another
    COGS fact because the physical movement was already performed by the
    Delivery Order. Only non-`LinkDo` direct Invoice lines own Invoice
    stock-out/COGS.
2.  **Inventory lineage IDs use the repository's actual key types.**
    `IvTrxHistory.Id`, `IvBalLoc.Id`, and `IvLot.Id` are `int`;
    `StockPosting.Id` and production movement/work-order IDs are `long`.
    New valuation entities must preserve these types instead of widening
    inventory IDs merely for architectural symmetry.
3.  **Production valuation reuses existing lineage fields.**
    `ProductionBalLotMovement` and `ProductionMaterialMovement` already
    contain the V2 posting/history/source/reversal identities needed for
    valuation linkage. Do not add a parallel set of valuation identity
    columns to those production tables unless a genuinely missing
    relationship is proven.

------------------------------------------------------------------------

# 1. Goal

Complete the costing architecture already emerging in the `production`
branch so the ERP can reliably support historical stock valuation,
inventory as-of valuation, COGS, gross profit and margin, purchase cost
and purchase-price variance, WIP valuation, work-order actual cost,
finished-goods manufacturing cost, and management cost/profit summaries.

The authoritative value chain is:

``` text
PO / GRN / PI
      |
      v
Receipt Cost + Purchase Cost Adjustments
      |
      v
Inventory Financial Valuation
      |
      v
Issue to Production
      |
      v
RM Cost -> WIP / Process / Work Centre
      |
      v
Finished Goods Actual Cost
      |
      v
FG Inventory
      |
      v
Sales Stock-Out
      |
      v
Historical COGS
      |
      v
Net Sales - COGS = Gross Profit
```

This is deliberately an SME-level ERP costing architecture. It must be
financially reliable and auditable without introducing SAP-level
complexity.

------------------------------------------------------------------------

# 2. Current `production` Branch --- Verified Starting Point

## 2.1 V2 stock posting infrastructure already exists

Verified current V2 components include:

-   `ErpWeb.Core/StockLedger/StockPostingContext.cs`
-   `ErpWeb.Core/StockLedger/StockPostingCoordinator.cs`
-   `ErpWeb.Core/StockLedger/StockMovementRegistry.cs`
-   `ErpWeb.Core/StockLedger/StockLedgerCompatibility.cs`
-   `ErpWeb.Model/Entities/StockLedger/StockPosting.cs`
-   `ErpWeb.Model/Entities/StockLedger/StockLedgerEpoch.cs`
-   `ErpWeb.Model/Entities/StockLedger/StockPostingBranchSequence.cs`
-   `ErpWeb.Model/Entities/StockLedger/StockPeriodSnapshot.cs`

`StockPostingCoordinator` already provides trusted company/branch scope,
branch serialization, ledger epoch enforcement, request idempotency,
source-document uniqueness, reversal target validation, period/freeze
guards, branch posting sequence, immutable source snapshot/hash,
transaction-scoped `StockPostingContext`, and sealing of completed
postings.

**This infrastructure is the posting/audit envelope. It is not yet the
monetary valuation ledger.** Do not create another posting coordinator.

## 2.2 Current V2 period snapshot is intentionally quantity-only

Keep the existing V2 quantity snapshot contract. Do not overload
`StockPeriodSnapshotLine` with financial valuation fields. Introduce a
separate valuation fact model and a separate financial valuation
snapshot.

## 2.3 Existing operational inventory remains important

Current transaction code still uses `IvTrxBatch`, `IvTrxBatchDetail`,
`IvTrxHistory`, `IvBalLoc`, `UnitPrice`, `Cost`, `CostPrice`, and
lot/location/status stock slices. These remain operational stock
structures; they are not the long-term historical financial source of
truth.

## 2.4 Repository key-type contract

The current entities establish this key-type split:

``` text
IvTrxHistory.Id       int
IvBalLoc.Id           int
IvLot.Id              int
StockPosting.Id       long
Production movement IDs / Work Order IDs   long
```

All new foreign-key fields must match the referenced entity's actual
CLR/EF key type. Do not use `long` for inventory history/balance/lot IDs
merely because StockPosting and Production use `long`.

## 2.5 Production already participates in V2 posting identity

Current `ProductionBalLotMovement` already includes:

``` text
LedgerVersion
LedgerEpochId
StockPostingId
PostingLineNo
SourceLineId
SplitOrdinal
InventoryHistoryId
ValuationStatus
CostBasisVersion
OriginalMovementId
```

Current `ProductionMaterialMovement` already includes:

``` text
InventoryHistoryId
InventoryPostingOperationId
StockPostingId
SourceLineId
SplitOrdinal
OriginalMovementId
SourceIssueMovementId
ReversesMaterialMovementId
ProductionBalLotMovementId
ProductionOutputId
```

These are the canonical production-side lineage seams. Reuse them.

Do **not** introduce duplicate `ValuationFactId`, posting-line,
source-line, reversal, or history-link columns into production entities
merely to mirror `StockValuationFact`. Resolve valuation through the
existing `StockPostingId + PostingLineNo/SplitOrdinal`,
`InventoryHistoryId`, source identity, and existing movement/reversal
relationships. Add a new FK only if implementation proves an existing
identity cannot deterministically resolve the relationship.

## 2.6 Production already has useful material/WIP cost propagation

Preserve inventory issue cost flow into `ProductionMaterialMovement`,
production balance `TotalCost/AverageUnitCost`, proportional WIP
depletion, exact final depletion, and downstream accumulated output
cost.

## 2.7 Historical period valuation remains unsafe

Historical value must not be reconstructed from current
`IvBalLoc.UnitPrice`, `IvBalLoc.Cost`, or `IvStockMaster.PurchasePrice`.

## 2.8 Purchase Invoice variance needs a cost consequence

Persist and audit the difference between provisional GRN cost and final
PI cost.

## 2.9 Current Sales physical-stock ownership is already explicit

Current `SaInvoiceService` deliberately excludes `LinkDo` lines from
Invoice shipment creation and from Invoice stock-out posting. Those
lines were physically shipped by the Delivery Order.

Therefore the costing implementation must preserve this existing
invariant:

``` text
DO line physically shipped
    -> DO stock-out owns valuation/COGS

Invoice LinkDo == true
    -> references/aggregates the DO valuation
    -> MUST NOT stock-out again
    -> MUST NOT create COGS again

Invoice LinkDo == false and stock-controlled
    -> Invoice performs stock-out
    -> Invoice owns valuation/COGS
```

This behavior is mandatory, not an optional reporting convention.

------------------------------------------------------------------------

# 3. Locked Architecture --- Four Separate Responsibilities

``` text
A. STOCK POSTING ENVELOPE
   StockPosting / StockLedgerEpoch / StockPostingContext
   Identity, sequence, source evidence, idempotency, audit

B. PHYSICAL STOCK
   IvTrxHistory / IvBalLoc / operational stock movements
   Warehouse, location, lot, status, physical quantity

C. FINANCIAL INVENTORY VALUATION
   NEW: StockValuationFact
   Immutable historical UnitCost / CostAmount

D. MANUFACTURING COST
   ProductionMaterialMovement / ProductionBalLot / output
   RM + WIP + labour + machine + OH + subcontract
```

Rules:

1.  `StockPosting` remains the posting envelope.
2.  `IvTrxHistory` remains operational movement/history.
3.  `StockValuationFact` becomes authoritative historical financial
    inventory-cost truth.
4.  Production movements/lots remain the manufacturing cost subledger.
5.  Physical FIFO/lot allocation does not automatically mean financial
    FIFO.
6.  Mutable current balance/master prices are never historical financial
    truth.
7.  Production uses its existing lineage fields to connect to the
    valuation domain.

------------------------------------------------------------------------

# 4. Phase 0 --- Costing Transaction Matrix and Method Decision

Create `docs/costing-transaction-matrix.md` and trace every
stock-affecting route: Goods/Misc Receipt, Goods/Misc Issue,
Customer/Vendor Return, Transfer, Adjustment, Stock Take, DO, Direct
Invoice, Sales Return, GRN, Purchase Return/CN physical return,
Issue/Return to Production, Daily Production/output, and Finished Goods
Receipt.

For each route document service/entry point, source identity, physical
direction, stock dimensions, current cost behavior, V2 context usage,
`IvTrxHistory`, rollback/reversal, production movement, period-close
effect, valuation rule, and COGS ownership.

For Sales, the matrix must explicitly distinguish:

``` text
DO stock-out
Invoice LinkDo stock line (no second stock-out)
Direct Invoice non-LinkDo stock-out
```

### Recommended V1 financial method: Moving Weighted Average

Recommended pool:

``` text
CompanyCode + BranchCode + ItemCode
```

Physical stock remains warehouse/location/lot specific. Reserve FIFO and
STANDARD for future methods.

**Gate:** no valuation implementation until matrix and method are
approved.

------------------------------------------------------------------------

# 5. Phase 1 --- Canonical `StockValuationFact`

Create `ErpWeb.Model/Entities/StockLedger/StockValuationFact.cs`.

Minimum contract:

``` text
Id                          long
CompanyCode                 string
BranchCode                  string

LedgerEpochId               long
StockPostingId              long
PostingLineNo               int
SplitOrdinal                int

SourceLineId                string
SourceDocumentType          string
SourceDocumentId            string
SourceDocumentNo            string
SourceDocumentLine          string/null

EffectiveAt                 DateTime
BusinessDate                DateTime
PeriodKey                   string

ItemCode                    string
WarehouseCode               string/null
LocationCode                string/null
LotId                       int/null       // matches IvLot.Id
LotNo                       string/null
ItemStatus                  string/null
BaseUom                     string

MovementCode                string
Direction                   int
BaseQty                     decimal

CostMethod                  string
UnitCost                    decimal
CostAmount                  decimal

TransactionCurrency         string/null
TransactionCostAmount       decimal/null
ExchangeRate                decimal/null
BaseCurrency                string/null
BaseCostAmount              decimal

ValuationSource             string
ValuationStatus             string
ValuationVersion            int

InventoryHistoryId          int/null       // matches IvTrxHistory.Id
FromBalLocId                int/null       // when useful; matches IvBalLoc.Id
ToBalLocId                  int/null       // when useful; matches IvBalLoc.Id
ProductionMovementId        long/null
WorkOrderId                 long/null
WorkOrderOperationId        long/null
ProductionPostingLinkId     long/null

OriginalValuationFactId     long/null
ReversesValuationFactId     long/null

CreatedAtUtc                DateTime
CreatedBy                   string
```

Use the exact current entity key types. Any additional FK must follow
the same rule.

### Identity and uniqueness

Recommended unique key:

``` text
StockPostingId + PostingLineNo + SplitOrdinal
```

If multiple financial allocations arise from one physical line,
`SplitOrdinal` must be deterministic.

Index company/branch/item/effective date, period, inventory history,
source document, original/reversal fact, and work-order/operation.

### Sign contract

``` text
BaseQty      = positive magnitude
Direction    = +1 value in / -1 value out
CostAmount   = positive magnitude
SignedQty    = BaseQty * Direction
SignedValue  = CostAmount * Direction
```

Transfers create equal-value OUT/IN facts.

### Valuation sources

At minimum: `RECEIPT_ACTUAL`, `MOVING_AVERAGE`, `ORIGINAL_REVERSAL`,
`ORIGINAL_SALE_RETURN`, `PRODUCTION_ACTUAL`, `PURCHASE_PRICE_VARIANCE`,
`LANDED_COST`, `MANUAL_APPROVED`, `OPENING_APPROVED`,
`BACKFILL_VERIFIED`.

### Status

`UNVALUED`, `VALUED`, `REVERSED`. Required `UNVALUED` facts block close.

------------------------------------------------------------------------

# 6. Phase 2 --- `StockCostState`

Identity:

``` text
CompanyCode + BranchCode + ItemCode + CostMethod
```

State:

``` text
OnHandBaseQty
InventoryValue
AverageUnitCost
LastValuationFactId
LastPostingSequence
RowVersion
```

It is current state/cache/control, while `StockValuationFact` is
historical truth.

Moving-average receipt:

``` text
NewQty   = OldQty + ReceiptQty
NewValue = OldValue + ReceiptValue
NewAvg   = NewValue / NewQty
```

Issue:

``` text
IssueUnitCost = OldAverageUnitCost
IssueValue    = IssueQty * IssueUnitCost
NewQty        = OldQty - IssueQty
NewValue      = OldValue - IssueValue
```

Final depletion consumes exact remaining value.

------------------------------------------------------------------------

# 7. Phase 3 --- Central `IInventoryValuationService`

Use the same `AppDbContext`, DB transaction, and `StockPostingContext`
as physical posting.

Responsibilities: value receipts/issues/transfers/adjustments, exact
reversals, update `StockCostState`, create valuation facts, return
resolved cost to inventory/production callers, and reject unresolved
valuation rather than silently using master purchase price.

Transfer destination receives exact source-out value. Reversal reads
original valuation facts and creates compensating facts; it never
recalculates at current cost.

------------------------------------------------------------------------

# 8. Phase 4 --- Atomic V2 Integration

Every financial stock posting:

``` text
Begin/obtain StockPostingContext
 -> lock/validate physical stock and StockCostState
 -> mutate physical stock
 -> append IvTrxHistory
 -> create StockValuationFact
 -> update StockCostState
 -> update production/source facts
 -> seal StockPosting
 -> COMMIT
```

Failure anywhere rolls back everything. Preserve branch locking, tenant
scope, period/freeze guard, idempotency, source uniqueness, and source
snapshot/hash.

------------------------------------------------------------------------

# 9. Phase 5 --- Inventory Route Migration

Apply valuation to every stock-affecting inventory route. Keep
`IvTrxBatch`, `IvTrxBatchDetail`, `IvTrxHistory`, and `IvBalLoc`.

Every V2-capable history movement must trace to `LedgerEpochId`,
`StockPostingId`, posting/source identity, and a corresponding valuation
fact where financially relevant.

`IvBalLoc.UnitPrice/Cost` may remain operational/display/cache data but
never historical report authority.

------------------------------------------------------------------------

# 10. Phase 6 --- Quantity Close vs Financial Close

Keep `StockPeriodSnapshotHdr/Line` quantity-only.

Add:

``` text
StockValuationPeriodSnapshotHdr
StockValuationPeriodSnapshotLine
```

Financial snapshot is generated from sealed valuation facts to a
recorded posting watermark.

Invariants:

``` text
OpeningQty + InQty + AdjustmentQty - OutQty = ClosingQty
OpeningValue + InValue + AdjustmentValue - OutValue = ClosingValue
```

Quantity and value snapshots must reconcile.

------------------------------------------------------------------------

# 11. Phase 7 --- Period Close

Refactor `IvPeriodCloseService.Snapshot.cs` so current/master prices are
not historical value authority.

Financial close uses valuation facts/snapshots. `StockCostState` is only
a current-state reconciliation target.

Close fails for unresolved valuation, quantity/value mismatch,
cost-state mismatch, production FG transfer mismatch, sales stock-out
without COGS valuation, or unresolved PI/landed-cost adjustments
required by policy.

------------------------------------------------------------------------

# 12. Phase 8 --- Historical / As-Of Valuation

Read sealed valuation facts plus prior financial snapshots. Never
reconstruct old value from today's `IvBalLoc`, item master purchase
price, or current `StockCostState`.

------------------------------------------------------------------------

# 13. Phase 9 --- Backdated Policy

-   Closed period: reject.
-   Open period with no later valued movement in the cost pool: allow.
-   Open period with later valued movements: reject and require
    rollback/repost.

Do not silently forward-revalue historical moving-average issues in V1.

------------------------------------------------------------------------

# 14. Phase 10 --- Procurement Cost

GRN establishes receipt valuation and freezes required
commercial/currency inputs.

Add `PurchaseCostAdjustment` for PI variance. Use actual repository key
types.

Do not rewrite original GRN facts. Allocate variance between remaining
inventory and consumed quantity. Persist consumed variance separately.
Vendor return follows one documented policy. Landed cost is optional
after PI variance is stable.

------------------------------------------------------------------------

# 15. Phase 11 --- Sales COGS Ownership and `LinkDo` Invariant

Historical COGS is the value of the authoritative physical sales
stock-out valuation fact. Do not create a competing COGS engine.

## 15.1 Mandatory ownership contract matching current code

### Delivery Order

When a DO posts its shipment and performs physical stock-out:

``` text
DO physical OUT
 -> IvTrxHistory
 -> StockValuationFact
 -> historical COGS source
```

The DO owns the physical stock-out valuation.

### Invoice with `LinkDo == true`

The current Invoice service intentionally excludes these lines from
Invoice shipment/stock-out.

Therefore:

``` text
Invoice LinkDo line
 -> references source DO line/application
 -> resolves COGS from source DO StockValuationFact
 -> creates NO Invoice stock-out valuation
 -> creates NO duplicate COGS
```

This is a hard acceptance invariant.

### Direct Invoice / `LinkDo == false`

For stock-controlled non-`LinkDo` lines, the Invoice performs its own
physical stock-out:

``` text
Direct Invoice physical OUT
 -> IvTrxHistory
 -> StockValuationFact
 -> Invoice owns historical COGS
```

### Reporting

Invoice profitability must be able to combine:

-   COGS inherited from linked DO valuation facts for `LinkDo` lines;
    and
-   COGS owned by direct Invoice valuation facts for non-`LinkDo` lines.

Never infer ownership merely from document numbers. Use the existing
source/application identities and persisted valuation source line
identity.

## 15.2 Sales Return

When linked to an original sale, restore original outbound valuation
cost and set original valuation lineage. Never silently use current
purchase price.

## 15.3 Profit

``` text
Gross Profit = Net Sales - Historical COGS
Gross Margin % = Gross Profit / Net Sales * 100
```

CN/DN affects COGS only when tied to a physical valued stock movement.

------------------------------------------------------------------------

# 16. Phase 12 --- Production Material Issue Integration

Preserve the current production movement/lot model.

Inventory -\> Production must use the authoritative resolved inventory
valuation.

Invariant:

``` text
Inventory Material OUT Cost = Production Material IN Cost
```

### Reuse existing lineage; do not duplicate it

For `ProductionMaterialMovement`, use existing:

``` text
InventoryHistoryId
StockPostingId
SourceLineId
SplitOrdinal
OriginalMovementId
SourceIssueMovementId
ReversesMaterialMovementId
ProductionBalLotMovementId
ProductionOutputId
```

For `ProductionBalLotMovement`, use existing:

``` text
LedgerEpochId
StockPostingId
PostingLineNo
SourceLineId
SplitOrdinal
InventoryHistoryId
OriginalMovementId
ValuationStatus
CostBasisVersion
```

`StockValuationFact` should be resolvable through these identities. Do
not add a second family of posting/history/reversal identifiers to
production entities.

Only add a direct `StockValuationFactId` FK later if
profiling/implementation proves deterministic lookup is insufficient and
the new FK has a clear single source of truth. If added, it must
supplement---not replace---the existing posting/history lineage.

Required production movements transition to `VALUED` only after
authoritative cost resolution.

------------------------------------------------------------------------

# 17. Phase 13 --- WIP and Production Output

Preserve proportional depletion and exact final depletion.

``` text
Opening WIP Value
+ RM Additions
+ Prior-WIP Additions
+ Conversion Cost
- WIP Consumed
= Closing WIP Value
```

Parallel route steps keep separate contribution identity until combined
downstream. Do not derive historical WIP from current master rates.

------------------------------------------------------------------------

# 18. Phase 14 --- Finished Goods Receipt

Production final WIP/FG OUT must equal Inventory FG IN in total value.
Same actual transferred value, same V2 transaction/posting boundary.

Rollback reverses exact inventory FG valuation and restores exact
production value; never recalculate current cost.

------------------------------------------------------------------------

# 19. Phase 15 --- Manufacturing Conversion Cost

Support optional SME components:

``` text
Material + Prior WIP + Labour + Machine + Overhead + Subcontract
= Actual Manufacturing Cost
```

Persist resolved effective-dated machine/labour/overhead/subcontract
values at posting time. Later rate changes cannot alter historical cost.

------------------------------------------------------------------------

# 20. Phase 16 --- Scrap / Reject

Default V1: normal scrap/reject cost is absorbed by accepted good
output.

``` text
FG Unit Cost = Total Actual Production Cost / Accepted Good Base Qty
```

Persist GoodQty, ScrapQty, RejectQty, and absorbed value.

------------------------------------------------------------------------

# 21. Phase 17 --- Cost Precision

Use dedicated monetary precision; do not use `IvQty.Round` universally
for money.

Final depletion consumes exact remaining value to avoid zero quantity
with residual value.

------------------------------------------------------------------------

# 22. Phase 18 --- Reconciliation

Inventory:

``` text
IvTrxHistory Qty <-> IvBalLoc Qty <-> StockValuationFact Qty
Cumulative StockValuationFact Value <-> StockCostState InventoryValue
```

Procurement: GRN value equals receipt valuation; PI variance/landed cost
fully classified.

Production: Inventory issue OUT = Production Material IN; WIP movement =
WIP balance; FG OUT = inventory FG IN; rollback exactly offsets
original.

Sales:

``` text
Every physical sales stock-out has one valuation owner.
DO stock-out valuation = DO COGS source.
Invoice LinkDo line references DO valuation and creates no second COGS.
Invoice non-LinkDo stock-out valuation = Invoice COGS source.
Sales Return restores original cost where linked.
```

Blocking findings prevent close.

------------------------------------------------------------------------

# 23. Phase 19 --- Cutover / Backfill

Choose cutover date, verify opening quantity, establish defensible
opening value, create `OPENING_APPROVED` valuation facts, initialize
`StockCostState`, backfill only defensible history, reconcile, then
activate authoritative valuation.

Never silently fabricate opening value from current PurchasePrice.

------------------------------------------------------------------------

# 24. Phase 20 --- Reporting Read Models

Only after valuation/reconciliation gates pass.

Inventory: Stock Valuation Summary/Detail, As-Of, Movement Cost, Aging
Value, Cost Audit Trail.

Procurement: Purchase Cost History, Supplier Cost Trend, PO/GRN/PI Cost,
PPV, Landed Cost.

Sales: Gross Profit and Profit by
Invoice/Item/Customer/Category/Salesperson/Branch/Warehouse. Invoice
reporting must combine linked-DO COGS and direct-Invoice COGS without
duplication.

Production: WIP Valuation, WO Cost Summary/Detail,
Material/Machine/Labour/Overhead/Scrap Cost, FG Actual Manufacturing
Cost, Cost by Work Centre/Process.

------------------------------------------------------------------------

# 25. Phase 21 --- Management Dashboard

Build only after detailed reports reconcile. Dashboard amounts must
drill to the same immutable facts.

------------------------------------------------------------------------

# 26. Multi-Tenant, Audit and Security

Enforce trusted company/branch scope. Audit manual/opening valuation,
adjustment overrides, PI variance, landed cost, close/reopen,
costing-method configuration, backfill/rebuild, and manufacturing-rate
changes.

------------------------------------------------------------------------

# 27. Implementation Order --- Agent Execution Sequence

1.  Create `docs/costing-transaction-matrix.md`, explicitly including DO
    vs Invoice `LinkDo` ownership.
2.  Lock V1 method and cost-pool grain.
3.  Add `StockValuationFact` using exact current key types (`int`
    inventory IDs, `long` posting/production IDs).
4.  Add `StockCostState`.
5.  Add valuation contracts.
6.  Implement `IInventoryValuationService`.
7.  Integrate atomically with `StockPostingContext`.
8.  Migrate inventory routes.
9.  Implement exact valuation reversal.
10. Implement historical/as-of service.
11. Add financial period snapshots.
12. Refactor period close.
13. Implement backdated policy.
14. Complete GRN valuation.
15. Add `PurchaseCostAdjustment`.
16. Implement PI variance.
17. Complete return policy.
18. Add optional landed cost.
19. Implement Sales COGS ownership exactly around DO / `LinkDo` / direct
    Invoice.
20. Implement Sales Return original-cost restoration.
21. Integrate Production Material Issue using existing production
    lineage fields.
22. Complete production `UNVALUED -> VALUED` without duplicate identity
    columns.
23. Harden WIP reconciliation.
24. Complete FG Receipt value transfer.
25. Add optional conversion cost.
26. Apply scrap/reject policy.
27. Implement cross-module reconciliation.
28. Perform cutover/backfill.
29. Build report projections.
30. Build detailed reports.
31. Build management dashboard.

Do not start steps 29-31 until close/reconciliation gates pass.

------------------------------------------------------------------------

# 28. Implementation Guardrails

1.  Do not create another stock-posting coordinator.
2.  Keep `StockPosting` as V2 posting/audit envelope.
3.  Use `StockValuationFact` as historical financial truth.
4.  Keep current `StockPeriodSnapshot` quantity-only.
5.  Use separate financial period snapshot.
6.  Keep `IvTrxBatch`, `IvTrxHistory`, `IvBalLoc` operational.
7.  Never use current PurchasePrice as silent historical fallback.
8.  Never recalculate old COGS from current average cost.
9.  Never recalculate rollback cost.
10. Never duplicate COGS between DO and Invoice.
11. **For `LinkDo == true`, Invoice must not perform another physical
    stock-out or create another COGS valuation.**
12. **For non-`LinkDo` direct Invoice stock lines, Invoice owns its
    physical stock-out valuation/COGS.**
13. **Use `int` for FKs to `IvTrxHistory`, `IvBalLoc`, and `IvLot`; use
    `long` where the referenced StockPosting/Production entity uses
    `long`.**
14. **Reuse existing production posting/history/source/reversal fields;
    do not create parallel lineage columns without proof they are
    necessary.**
15. Never treat physical FIFO as financial FIFO.
16. Required `UNVALUED` facts block close.
17. Preserve no-future-stock validation.
18. Preserve period/freeze guards.
19. Preserve tenant isolation, branch locking, idempotency, and source
    uniqueness.
20. Preserve production lot cost lineage and exact final depletion.
21. Do not rewrite original GRN facts for PI variance.
22. Do not modify sealed valuation facts.
23. Do not build management profit reports until authoritative COGS and
    reconciliation are complete.

------------------------------------------------------------------------

# 29. 10/10 Acceptance Gate

## V2 financial contract

-   `StockPosting` remains posting envelope/audit identity.
-   Every financially relevant movement has deterministic
    `StockValuationFact`.
-   Key types match actual referenced entities.
-   Valuation uniqueness prevents duplicate financial legs.
-   Reversals exactly offset originals.
-   Sealed valuation facts cannot be silently edited.

## Inventory

-   Every receipt/issue has persisted historical value.
-   Moving-average state reconciles.
-   Transfer is value-neutral.
-   Adjustment rules explicit.
-   Zero qty has no unexplained residual value.
-   No-future-stock remains enforced.

## Historical valuation

Changing current `IvBalLoc.UnitPrice`, `IvStockMaster.PurchasePrice`, or
`StockCostState` cannot alter historical valuation/COGS.

## Period close

Quantity snapshot remains quantity-only; financial snapshot derives from
sealed valuation facts; quantity/value reconcile; unresolved valuation
blocks close.

## Procurement

GRN value traceable; PI variance explicit; inventory/consumed variance
classified; original GRN facts immutable; returns follow one policy.

## Production

-   Inventory RM OUT = Production Material IN.
-   Existing production lineage fields resolve the authoritative
    valuation relationship.
-   No duplicate posting/history/reversal identity family is introduced
    without documented necessity.
-   WIP preserves value.
-   FG Production OUT = Inventory FG IN.
-   Rollback reverses exact original.
-   Required movements transition `UNVALUED -> VALUED`.

## Sales --- mandatory repo-specific gate

-   Every physical sales stock-out has exactly one valuation/COGS owner.
-   DO physical stock-out creates the DO valuation/COGS source.
-   `SaInvoiceDetail.LinkDo == true` lines create **no second Invoice
    stock-out** and **no second COGS**.
-   Linked Invoice profitability resolves/aggregates the originating DO
    valuation.
-   Non-`LinkDo` direct Invoice stock lines create Invoice stock-out
    valuation/COGS.
-   A mixed Invoice containing linked-DO and direct lines reports both
    correctly without duplication.
-   Sales Return restores original cost where linked.
-   Later purchase-price changes cannot alter old COGS.
-   `Net Sales - COGS = Gross Profit`.

## Reconciliation

Physical quantity, valued quantity, current cost state, period
snapshots, production transfers, and sales COGS all reconcile; no
blocking costing findings remain.

## Reporting

The same authoritative facts support Stock Valuation, As-Of, COGS, Gross
Profit, Profit by Item/Customer/Invoice, Supplier Cost, PPV, WIP, WO
Cost, and FG Actual Cost without reconstructing historical cost from
mutable prices.

------------------------------------------------------------------------

# 30. Explicit Non-Goals

No SAP-level product costing, LIFO, simultaneous multiple financial
methods, full standard-cost variance accounting, activity-based costing,
sophisticated overhead absorption, intercompany transfer pricing,
predictive costing, or full GL manufacturing accounting unless
separately scoped.

------------------------------------------------------------------------

# 31. Final Target Architecture

``` text
SOURCE DOCUMENTS
Purchase / Inventory / Sales / Production
        |
        v
StockPostingContext / StockPosting / Epoch
        |
        +---------------------------+
        |                           |
        v                           v
PHYSICAL STOCK               FINANCIAL VALUATION
IvTrxHistory / IvBalLoc      StockValuationFact / StockCostState
        |                           |
        +-------------+-------------+
                      |
                      v
             Issue to Production
                      |
                      v
       Existing Production Lineage + RM/WIP Cost
                      |
                      v
          Finished Goods Receipt
                      |
                      v
             FG Inventory Value
                      |
                      v
         DO or Direct Invoice OUT
                      |
                      v
              Historical COGS

Invoice LinkDo line:
Invoice -> source DO valuation -> COGS
(no second stock-out, no second COGS)

Period Close:
StockPeriodSnapshot          = immutable quantity close
StockValuationPeriodSnapshot = immutable financial value close
```

------------------------------------------------------------------------

# 32. Final Approval

**Plan score: 10/10 --- APPROVED FOR IMPLEMENTATION against the current
`production` branch.**

This v3 revision incorporates the final repository-grounded corrections:

-   Sales valuation/COGS now explicitly follows the current `LinkDo`
    implementation: DO-shipped lines are not stocked out or costed again
    by Invoice, while direct non-`LinkDo` Invoice lines own their
    stock-out/COGS.
-   `StockValuationFact` now locks inventory lineage to the actual
    current `int` key types (`IvTrxHistory`, `IvBalLoc`, `IvLot`) while
    retaining `long` for StockPosting/Production identities.
-   Production integration explicitly reuses the existing V2 lineage
    already present on `ProductionBalLotMovement` and
    `ProductionMaterialMovement`, avoiding a second competing identity
    model.

The coding agent should not need to invent any major costing
architecture decision during implementation. Remaining decisions should
be limited to normal naming/configuration details and
transaction-specific source mapping discovered while implementing the
approved transaction matrix.
