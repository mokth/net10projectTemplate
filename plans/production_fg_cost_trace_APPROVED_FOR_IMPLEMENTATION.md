# Finished Good Cost Trace — APPROVED FOR IMPLEMENTATION

## Repository Verification

- Repository: `mokth/net10projectTemplate`
- Branch: `production`
- Verified branch head: `6b729eb24be8f00e3b02ba200cb2e02e920d1e05`
- Verified date: 2026-10-08
- Target UI: `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor`
- Existing authoritative FG line value: `ProductionFinishedGoodFact.TotalValue`
- Existing production money precision: `StockLedgerPrecision.Money(...)` = 6 decimal places
- Existing production quantity precision used by FG source quantities: `IvQty.Round(...)`
- No schema change is required for this enhancement.

# Objective

Add a cost-trace button beside each visible Finished Good `Value` cell in the FG receipt View grid.

Clicking the button MUST open a read-only popup that explains exactly where the posted FG value came from, including:

- exact posted FG line value;
- effective unit cost;
- source production pool calculation;
- raw material / WIP value;
- labour;
- machine;
- utilities / overhead;
- other cost;
- any verified-but-unclassified historical value;
- upstream source documents / production outputs where evidence exists;
- posting/reversal audit IDs and reconciliation status.

The trace MUST NOT recalculate posted history from current master data. It MUST derive the explanation only from immutable/sealed posting evidence and MUST reconcile exactly to the frozen FG fact.

# Confirmed Current Problems

## 1. Value is visible but not traceable

File:

`ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor`

Current View-mode grid renders:

`FinishedGoodReceiptLine.TotalValue`

inside the `Value` column.

There is no UI action to explain:

- how the value was allocated;
- which production pool supplied it;
- which material/WIP costs contributed;
- which absorbed conversion costs contributed.

Required correction:

Add a cost-trace action in the same cell without changing the existing displayed value authority.

## 2. Posted FG value is already frozen correctly

Files:

- `ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.Posting.cs`
- `ErpWeb.Core/Production/FinishedGoodReceiptMath.cs`
- `ErpWeb.Model/Entities/Production/ProductionFinishedGoodReceipt.cs`

Confirmed posting rule:

`FinishedGoodReceiptMath.AllocateValue(pool.BaseQty, pool.TotalCost, ...)`

allocates exact value from the source production pool.

The exact posted amount is frozen in:

- `PrFinishedGoodFact.TotalValue`;
- `IvTrxHistory.ExactTransferredValue`;
- `PrProductionBalLotMovement.TotalCost` for `FG_RECEIPT_OUT`.

The trace MUST use these facts as authority.

## 3. FIFO production allocation is quantity genealogy, not monetary authority

Files:

- `ErpWeb.Core/Production/ProductionContributionAllocator.cs`
- `ErpWeb.Model/Entities/Production/ProductionMovementAllocation.cs`
- `ErpWeb.Core/Production/ProductionPoolValuationService.cs`
- `ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptTests.cs`

Confirmed behavior:

`PrProductionMovementAllocation` is FIFO quantity allocation.

`ProductionPoolValuationService` records `POOLED_AVERAGE` for outbound production value and deliberately records pooled dependencies for contributors that may have zero FIFO quantity allocation.

Existing test proves this case:

- pool quantity = 10;
- pool value = 120;
- contributors = `3/30`, `3/42`, `4/48`;
- FG receipt quantity = 6;
- exact FG value = 72;
- all 3 contributors remain pooled-value dependencies even though FIFO quantity can be satisfied by the first 2.

Required correction:

The trace MUST NOT sum FIFO contribution rows to calculate money.

## 4. Absorbed conversion-cost evidence already exists

Files:

- `ErpWeb.Core/Production/ProductionAbsorbedCostCalculator.cs`
- `ErpWeb.Core/Production/ProductionOutputService.Posting.cs`
- `ErpWeb.Model/Entities/Production/ProductionConversionCostFact.cs`
- `ErpWeb.Model/Configurations/Production/ProductionConversionCostFactConfiguration.cs`

Confirmed immutable conversion cost types:

- `LABOUR`
- `MACHINE`
- `UTILITIES_OVERHEAD`
- `OTHER`

`PrProductionConversionCostFact` stores:

- frozen work-order source identity;
- basis quantity/UOM;
- rate per output unit;
- exact cost amount;
- production movement;
- posting;
- reversal linkage.

Required correction:

Use these frozen facts. Do not read current labour/machine/product-definition rates to explain historical cost.

# Scope

Included:

- FG receipt View-mode `Value` cell trace button.
- Posted and reversed FG receipts.
- Exact frozen FG value reconciliation.
- Production pooled-cost replay up to the original FG posting.
- Recursive material/WIP lineage using existing production movements and immutable valuation evidence.
- Raw-material valuation details from `StockValuationFact`.
- Absorbed conversion-cost details from `ProductionConversionCostFact`.
- Reversal evidence validation.
- Permission enforcement using existing `ViewCost`.
- Loading, error, verified, partial-classification, reversed, and fail-closed UI states.
- Focused production tests.

# Non-Goals

MUST NOT:

- change FG posting calculation;
- change `FinishedGoodReceiptMath.AllocateValue`;
- change production pool valuation rules;
- change Moving Average, FIFO, or Standard Cost calculation;
- change Daily Production posting;
- change absorbed-cost calculation;
- change rollback rules;
- change inventory posting;
- change month-end/period close;
- modify historical posted facts;
- introduce a second financial valuation authority;
- add a new database table merely for UI tracing;
- backfill or rewrite old valuation facts;
- use current item cost, current balance cost, current labour rate, current machine rate, or current Product Definition as historical authority.

# Exact Files

## Existing files to change

### Core

`ErpWeb.Core/Production/IProductionFinishedGoodReceiptService.cs`

Add the cost-trace read API and trace DTOs.

`ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.cs`

No posting logic change. Existing partial class remains the service owner.

### UI

`ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor`

Add the Value-cell trace button and trace popup.

`ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor.cs`

Add popup state and `OpenCostTraceAsync`.

`ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor.css`

Add responsive Value-cell and trace-popup styling.

### Tests

`ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptTests.cs`

Extend existing FG authority/permission/reversal coverage where reuse is practical.

## Proposed new files

`ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.CostTrace.cs`

Purpose:

- repository reads;
- authority validation;
- historical pool replay orchestration;
- recursive movement provenance;
- trace DTO construction.

`ErpWeb.Core/Production/ProductionPooledCostTraceMath.cs`

Purpose:

Pure deterministic money-bucket allocation for pooled-average component tracing.

`ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptCostTraceTests.cs`

Purpose:

Focused end-to-end trace tests.

`ErpWeb.Tests/Production/Transaction/ProductionPooledCostTraceMathTests.cs`

Purpose:

Precision, residual, pooled-component and reversal math tests.

# Database Changes

None.

MUST NOT add a migration or SQL patch for this feature.

The required evidence already exists in:

- `PrFinishedGoodFact`;
- `PrProductionBalLotMovement`;
- `PrValuationEvidence`;
- `PrPoolValuation`;
- `PrPoolDependency`;
- `PrProductionMovementAllocation`;
- `PrProductionConversionCostFact`;
- `StockPosting`;
- `StockValuationFact`;
- `IvTrxHistory`.

The implementation MUST remain read-only with respect to these tables.

# Exact Code Changes

## 1. Add cost trace API

File:

`ErpWeb.Core/Production/IProductionFinishedGoodReceiptService.cs`

Add:

```csharp
Task<IvMasterOperationResult<FinishedGoodCostTrace>> GetCostTraceAsync(
    int receiptId,
    long sourceId,
    CancellationToken ct = default);
```

Add DTOs in this file or an adjacent Core production contract file:

```csharp
public sealed class FinishedGoodCostTrace
{
    public int ReceiptId { get; init; }
    public int BatchNo { get; init; }
    public long SourceId { get; init; }
    public long ProductionBalLotId { get; init; }
    public long OriginalPostingId { get; init; }
    public long ProductionMovementId { get; init; }
    public long? ReversalPostingId { get; init; }

    public string WorkOrderNo { get; init; } = "";
    public string ItemCode { get; init; } = "";
    public string SourceLot { get; init; } = "";
    public string Status { get; init; } = "";
    public string TraceStatus { get; init; } = "";

    public decimal ReceiptQty { get; init; }
    public string ReceiptUom { get; init; } = "";
    public decimal BaseQty { get; init; }

    public decimal ExactPostedValue { get; init; }
    public decimal EffectiveLineUnitCost { get; init; }
    public decimal? InventoryPostedUnitPrice { get; init; }

    public decimal PoolQtyBeforeReceipt { get; init; }
    public decimal PoolValueBeforeReceipt { get; init; }

    public IReadOnlyList<FinishedGoodCostTraceComponent> Components { get; init; } = [];
    public IReadOnlyList<FinishedGoodCostTraceDetail> Details { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
```

Component DTO MUST contain at minimum:

- component type;
- display name;
- exact allocated amount.

Detail DTO MUST support:

- component type;
- source kind;
- source document type;
- source document number;
- production output/document number where applicable;
- item;
- lot;
- cost method where applicable;
- valuation source where applicable;
- quantity/UOM where applicable;
- rate where applicable;
- exact allocated amount;
- source movement/fact identifier for audit/support.

Do not expose editable properties.

## 2. Implement repository-grounded trace service

Proposed file:

`ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.CostTrace.cs`

Implement `GetCostTraceAsync`.

### Permission

MUST require:

- menu access for `MenuCodes.PlanningFinishedGoodReceipt`;
- `PermissionCodes.ViewCost`.

Server-side permission MUST be enforced even if UI hides the button.

Unauthorized calls MUST return `AccessDenied` and MUST NOT return any monetary detail.

### Tenant scope

MUST validate the receipt belongs to the active company/branch.

All evidence queries MUST remain in that receipt/company/branch lineage.

### Eligible receipt states

Detailed historical trace is allowed only when the FG has an original `PostingId`.

Expected states:

- `POSTED`: trace original posting.
- `REVERSED`: trace original posting and validate reversal evidence.
- `NEW`: return a validation result stating that authoritative cost trace is available after posting.

Do not present a draft estimate as a historical posted trace.

## 3. Establish FG monetary authority first

For the requested `receiptId + sourceId`:

Load exactly one original forward `ProductionFinishedGoodFact` where:

- `BatchId == receiptId`;
- `SourceId == sourceId`;
- `StockPostingId == receipt.PostingId`;
- `ReversesFactId == null`.

Load its:

- `ProductionBalLotMovement`;
- `IvTrxHistory`;
- `ProductionValuationEvidence`;
- `StockPosting`.

Require all of the following:

```text
Fact.TotalValue
==
FG movement.TotalCost
==
IvTrxHistory.ExactTransferredValue
```

using exact 19,6 stored money values.

Require:

- movement type = `FG_RECEIPT_OUT`;
- valuation status = verified;
- posting is sealed;
- movement belongs to the same company/branch;
- fact base quantity equals the authoritative FG movement/evidence quantity.

If any authority check fails:

- return `TraceStatus = INCONSISTENT`;
- return the frozen FG fact total if permission permits;
- DO NOT produce a trusted component breakdown;
- include a concise diagnostic message.

MUST NOT silently substitute another value.

## 4. Reconstruct the source pool at the original posting point

Use:

- `ProductionValuationEvidence.Generation`;
- `ProductionBalLotMovement.ProductionBalLotId`;
- `StockPosting.PostingSequence`;
- `ProductionBalLotMovement.PostingLineNo`;
- `StockMovementRegistry` direction.

Query sealed V2 movements for the same source pool and same valuation generation up to the original FG posting.

Order strictly by:

1. `StockPosting.PostingSequence`;
2. `ProductionBalLotMovement.PostingLineNo`;
3. `ProductionBalLotMovement.Uid`.

Rebuild historical scalar pool state from zero:

```text
pool qty   += direction * movement.BaseQty
pool value += direction * movement.TotalCost
```

Money MUST use `StockLedgerPrecision.Money`.

Quantity MUST use the repository's production quantity precision for the stored movement values.

Before the FG posting sequence starts, the reconstructed pool state MUST be the pool state used by the original `FinishedGoodReceiptMath.AllocateValue`.

Do not use current `ProductionBalLot.TotalCost` for posted historical calculation because later movements may have changed it.

## 5. Re-run the EXISTING FG allocation only as a validation

For every forward FG fact in the same original stock posting and same source production pool:

- obtain `SourceId`;
- obtain exact `BaseQty`.

Use reconstructed pre-posting pool:

```csharp
FinishedGoodReceiptMath.AllocateValue(
    poolQtyBeforePosting,
    poolValueBeforePosting,
    sourceLines);
```

The returned amount for every source line MUST equal its stored `ProductionFinishedGoodFact.TotalValue`.

This is a validation of the frozen posting, not a replacement authority.

If one line differs:

- `TraceStatus = INCONSISTENT`;
- do not publish trusted component allocation.

This step is REQUIRED because `AllocateValue` contains final-line residual behavior that a simple `pool average * line qty` calculation can miss.

## 6. Build exact component provenance

Proposed helper:

`ProductionPooledCostTraceMath`

Use cost atoms, not only five totals.

Each atom MUST have a stable identity such as:

```text
ComponentType
SourceKind
SourceFactOrMovementKey
```

Component types:

- `MATERIAL`
- `LABOUR`
- `MACHINE`
- `UTILITIES_OVERHEAD`
- `OTHER`
- `UNCLASSIFIED_VERIFIED`

`UNCLASSIFIED_VERIFIED` is allowed only when the stored total is verified but the repository does not contain enough evidence to classify it safely.

Never guess a category.

## 7. Build inbound movement components from immutable evidence

### A. `ISSUE` inbound to production

Use the same evidence authority already enforced by `ProductionPoolValuationService`:

`StockValuationFact`

Query facts linked to the production movement's `InventoryHistoryId` and require:

- direction < 0;
- movement code = `PRODUCTION_MATERIAL_OUT`;
- `ValuationStatus == VALUED`;
- fact quantity sum reconciles to production issue base quantity;
- fact cost sum reconciles exactly to production movement `TotalCost`.

Create `MATERIAL` atoms from the frozen valuation facts.

Detail rows SHOULD expose:

- source document type/no;
- item;
- lot;
- cost method;
- valuation source;
- unit cost;
- exact cost amount.

MUST NOT calculate raw-material cost from current `IvBalLoc.Cost`, current stock master price, or current cost state.

### B. `PRODUCE` inbound to production

Use `PrPoolDependency` where:

- `ConsumerMovementId == produceMovement.Uid`;
- dependency represents original forward lineage.

Resolve the input `CONSUME` movements.

For each consume movement:

- recursively obtain the exact component vector removed from its source pool.

Add conversion atoms from `PrProductionConversionCostFact` where:

- `ProductionMovementId == produceMovement.Uid`;
- `ReversesFactId == null`.

Map exact types:

- `LABOUR`;
- `MACHINE`;
- `UTILITIES_OVERHEAD`;
- `OTHER`.

Require:

```text
sum(input consume component values)
+
sum(conversion fact CostAmount)
==
produce movement.TotalCost
```

This MUST reconcile at 19,6 precision.

If not, fail closed for trusted component display.

This recursive rule is what preserves labour/machine/overhead from upstream WIP stages instead of incorrectly hiding prior-stage conversion cost inside a generic WIP bucket.

### C. Reversal movement with `OriginalMovementId`

A reversal component vector MUST be the exact component vector of the original movement.

Require:

```text
reversal movement.TotalCost == original movement.TotalCost
```

Do not proportionally redistribute a reversal.

### D. Other verified inbound types

For verified movements such as opening/adjustment/transfer/status inbound where the current repository contains value but not enough component lineage:

- create one `UNCLASSIFIED_VERIFIED` atom for the exact movement total;
- preserve document/movement identity in detail.

If valuation is unverified:

- do not provide trusted component allocation.

## 8. Apply pooled-average component removal correctly

For a normal outbound production movement with no `OriginalMovementId`:

- monetary authority is the movement's exact `TotalCost`;
- current pool atoms represent the exact current pooled value composition.

Allocate `movement.TotalCost` proportionally across the current atom values.

MUST use:

`StockLedgerPrecision.Money`

Deterministic ordering MUST be used.

For all atoms except the final residual atom:

```text
allocated =
Money(exactOutboundValue * atomPoolValue / totalPoolValue)
```

Clamp only to prevent a rounding allocation from exceeding the atom balance.

The final atom receives:

```text
remaining exact outbound value
```

Required invariants:

```text
sum(outbound atom allocation) == movement.TotalCost
sum(pool atoms before)         == scalar pool value before
sum(pool atoms after)          == scalar pool value after
no atom balance < 0
```

For final depletion:

- transfer the remaining atom balances exactly;
- do not recompute percentages.

This is explanatory pooled-average decomposition using the same exact movement value authority. It is NOT a new financial costing engine.

## 9. Determine target FG line components

Replay the source pool in sealed posting order.

When the target `FG_RECEIPT_OUT` movement is reached:

- its exact outbound atom allocation is the requested FG line cost breakdown;
- cache it before subtracting from the pool;
- aggregate atoms by top-level component type for the popup.

Require:

```text
sum(target trace atoms)
==
ProductionFinishedGoodFact.TotalValue
```

If not exact:

- fail closed.

`PrProductionMovementAllocation` MAY be shown as quantity genealogy/audit detail, but MUST NOT be used to determine monetary component amounts.

## 10. Reversed FG validation

For `REVERSED` FG:

Continue showing the ORIGINAL posted cost trace.

Additionally require the expected reversal evidence:

- reversal FG fact links via `ReversesFactId`;
- reversal production movement links via `OriginalMovementId`;
- reversal value equals original value;
- reversal base quantity equals original quantity;
- `ReversalPostingId` is present and sealed.

Popup status:

`REVERSED — showing original posted cost`

Do not show the restored current pool as if it were the original historical calculation.

## 11. Unit-cost display rules

The grid `Value` is a total line value, not necessarily the destination balance's displayed unit price.

Popup MUST show separately:

- `Exact posted line value` = `ProductionFinishedGoodFact.TotalValue`;
- `Effective line cost / destination UOM` = `Money(ExactPostedValue / DestinationQty)` when quantity > 0;
- `Inventory posted unit price` = original `IvTrxHistory.UnitPrice`, when available.

Do not force these two unit-cost numbers to be equal.

Multiple FG source lines can share one destination balance and the repository may apply group-level destination pricing plus exact per-line residual value.

## 12. UI — Value cell

File:

`ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor`

Existing `Value` column remains permission-gated by:

`Document.CanViewCost`

For POSTED/REVERSED line with `TotalValue`:

Render:

```text
RM / formatted value     [calculator/search icon]
```

Use a compact icon-only DevExpress button.

Recommended icon:

`fa-solid fa-calculator`

Tooltip:

`Trace posted cost`

Button MUST call:

`OpenCostTraceAsync(line)`

For `NEW`:

- keep the current estimated value behavior if required by existing UX;
- clearly label it `Estimate` or `Current pool estimate`;
- do not enable historical trace;
- tooltip/copy: `Authoritative cost trace is available after posting.`

## 13. UI — Cost trace popup

Use `DxPopup`.

Popup MUST contain:

### Header summary

- FG receipt number;
- status;
- Work Order;
- item;
- source lot;
- receipt quantity/UOM;
- exact posted value;
- effective line unit cost;
- inventory posted unit price if different/available;
- valuation badge;
- reversed badge when applicable.

### Cost component summary

Display only trusted values.

Rows:

- Material;
- Labour;
- Machine;
- Utilities / Overhead;
- Other;
- Verified unclassified value, only if present;
- Total.

Total MUST equal `ExactPostedValue`.

### Calculation evidence

Show:

- source production pool ID;
- pool base quantity immediately before original FG posting;
- pool value immediately before original FG posting;
- original posting ID;
- FG production movement ID;
- valuation generation;
- reconciliation `PASS` / `INCONSISTENT`.

Keep audit IDs in a secondary/expandable area so normal users see the business explanation first.

### Detail table

Columns should include where applicable:

- Component;
- Source;
- Document;
- Item/Lot;
- Qty/UOM;
- Rate / Cost method;
- Amount.

Material detail should show frozen stock valuation source document information.

Conversion detail should show frozen work-order source identities and rate/basis from `ProductionConversionCostFact`.

### Error behavior

If trace cannot prove lineage:

Show:

`The posted FG value is available, but its detailed cost lineage cannot be proven from the stored evidence.`

Show the frozen posted total.

Do NOT show a made-up component split.

## 14. UI state

File:

`PrFinishedGoodReceiptEntry.razor.cs`

Add state similar to:

```csharp
protected bool CostTraceVisible;
protected bool IsCostTraceLoading;
protected string? CostTraceError;
protected FinishedGoodCostTrace? CostTrace;
```

Add:

```csharp
protected async Task OpenCostTraceAsync(FinishedGoodReceiptLine line)
```

Behavior:

1. Require `Document.CanViewCost`.
2. Require line source ID.
3. Open popup.
4. Show loading state.
5. Call `Receipts.GetCostTraceAsync(Document.Id, line.SourceId)`.
6. Render result or explicit error.
7. Never compute monetary components in Razor/code-behind.

Add close/reset handling.

## 15. CSS

File:

`PrFinishedGoodReceiptEntry.razor.css`

Add only page-scoped styles.

Required:

- Value + icon stay on one row;
- number remains right aligned;
- icon does not dominate the cell;
- popup summary supports desktop and mobile;
- detail table/area can horizontally scroll on narrow screens;
- component total is visually stronger;
- warnings are visible without using dangerous action styling;
- existing `pr-dp-*` visual language remains intact.

Do not add global CSS for this feature.

# Transaction / Execution Order

This feature is read-only.

`GetCostTraceAsync` execution:

1. verify Access + ViewCost;
2. resolve tenant/company/branch;
3. load FG receipt/source;
4. require original posting;
5. load original immutable FG fact/movement/history/evidence/posting;
6. verify exact FG authority equality;
7. resolve target valuation generation;
8. load sealed historical movement graph up to target posting;
9. validate original `FinishedGoodReceiptMath.AllocateValue` result;
10. recursively build immutable inbound cost atoms;
11. replay pooled-average component movement;
12. capture target FG component allocation;
13. require exact component-to-FG reconciliation;
14. validate reversal evidence if receipt is reversed;
15. return read-only DTO.

No write transaction.

No posting lock.

No balance mutation.

No `SaveChangesAsync`.

# Authority Rules

## FG monetary authority

Primary:

`ProductionFinishedGoodFact.TotalValue`

Required corroboration:

- `ProductionBalLotMovement.TotalCost`;
- `IvTrxHistory.ExactTransferredValue`.

## Production pool movement authority

`ProductionBalLotMovement.TotalCost`

with:

`ProductionValuationEvidence`

and sealed:

`StockPosting`

## Raw material cost authority

`StockValuationFact.CostAmount`

Do not recalculate Moving Average/FIFO/Standard cost in the trace.

The costing engine already froze the result.

## Conversion cost authority

`ProductionConversionCostFact.CostAmount`

Do not read current work-order/master rates to replace historical facts.

## Quantity genealogy

`ProductionMovementAllocation`

is quantity genealogy only.

It is NOT monetary authority.

## Current balances

`ProductionBalLot.TotalCost`, `AverageUnitCost`, `IvBalLoc.Cost`, and `IvBalLoc.UnitPrice` are projections/current state.

They MUST NOT replace historical frozen facts.

# Invariants

The implementation MUST test and enforce:

```text
FG fact TotalValue
==
FG production movement TotalCost
==
FG inventory history ExactTransferredValue
```

```text
Replayed original FG allocation
==
stored FG fact TotalValue
```

```text
For every normal PRODUCE:
sum(recursive consumed component values)
+
sum(conversion cost facts)
==
PRODUCE movement TotalCost
```

```text
For every traced outbound movement:
sum(allocated cost atoms)
==
movement TotalCost
```

```text
For target FG line:
sum(component amounts)
==
FG fact TotalValue
```

```text
During pool replay:
sum(active atom balances)
==
replayed scalar pool value
```

```text
No replayed quantity < 0
No replayed value < 0
No component atom value < 0
```

```text
Final pool depletion:
remaining quantity == 0
=> remaining value == 0
=> remaining component atom sum == 0
```

Money equality uses stored `decimal(19,6)` / `StockLedgerPrecision.Money`.

Do not introduce a looser tolerance to hide a reconciliation defect.

# Rollback / Reversal

No rollback logic is changed.

The trace MUST understand existing reversal evidence.

Production Output rollback already creates linked reversal `ProductionConversionCostFact` rows.

FG rollback already creates linked reversal `ProductionFinishedGoodFact` rows and production reversal movements.

Trace rules:

- historical forward facts remain immutable;
- original cost trace remains available after reversal;
- reversal rows are validation evidence, not replacement forward authority;
- missing or mismatched reversal linkage marks trace inconsistent;
- do not alter existing rollback guards;
- do not permit trace code to write correction/backfill rows.

# Concurrency / Locking

This is a historical read-only inquiry.

MUST NOT add `UPDLOCK`, `HOLDLOCK`, branch transaction locks, or posting locks merely to open the popup.

Historical calculation is bounded by:

- original sealed `StockPosting.PostingSequence`;
- immutable forward/reversal facts;
- exact target posting/movement IDs.

Later postings MUST NOT change the reconstructed original result.

Use `AsNoTracking()` for read-only evidence where practical.

If data changes concurrently because a reversal is posted while the popup loads:

- original posted value/lineage remains immutable;
- popup may show the original status from the read;
- refreshing may add the reversal badge;
- monetary result MUST remain the same.

# Tests

## A. Pure pooled trace math

Proposed:

`ErpWeb.Tests/Production/Transaction/ProductionPooledCostTraceMathTests.cs`

### Exact proportional allocation

Given component pool:

- Material = 110
- Labour = 20
- Total = 130

Outbound exact value = 65.

Expect:

- Material = 55
- Labour = 10
- Total = 65.

### 19,6 residual

Use values that produce repeating decimals.

Assert:

- each allocation is money-rounded;
- final residual closes exactly;
- allocation sum equals requested exact movement value;
- remaining atom sum equals remaining pool value.

### Final depletion

Outbound value equals full pool value.

Assert every remaining atom is transferred exactly and pool ends at zero.

### Reversal

Original movement component vector is restored exactly.

Do not proportionally reallocate reversal value.

## B. Existing pooled FG case — mandatory regression

Use/extend:

`ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptTests.cs`

Existing fixture:

- 3 qty / value 30;
- 3 qty / value 42;
- 4 qty / value 48;
- pool 10 / value 120;
- FG receipt 6;
- frozen FG value 72.

Assertions:

- trace exact posted value = 72;
- trace component total = 72;
- trace does NOT calculate money from only the FIFO quantity allocations;
- all pooled-value contributors influence the replay;
- existing FG posting value remains 72;
- current post/rollback behavior remains unchanged.

For synthetic verified contributors without component lineage:

- amount may be `UNCLASSIFIED_VERIFIED`;
- it MUST still total exactly 72;
- it MUST NOT be mislabeled as material/labour.

## C. Full absorbed-cost FG trace

Proposed:

`FinishedGoodReceiptCostTraceTests.cs`

Create a posted Daily Production / FG scenario containing:

- valued material;
- labour;
- machine;
- utilities/overhead;
- other.

Post Daily Production, then post FG receipt.

Assert:

```text
Material
+ Labour
+ Machine
+ Utilities/Overhead
+ Other
==
exact FG fact TotalValue
```

Assert each conversion amount comes from `PrProductionConversionCostFact`.

Change current labour/machine/master values after posting.

Reload trace.

Assert historical trace does NOT change.

## D. Multi-output pooled mix

Create at least two production outputs contributing to the same final source pool with different cost mixes.

Then partially receive FG.

Assert:

- exact FG line total equals frozen fact;
- component allocation follows pooled monetary composition;
- it does not follow FIFO quantity contribution as money;
- sum of component detail equals top-level components;
- sum of components equals FG exact value.

## E. Recursive WIP

Create:

- operation/stage 1 producing WIP with material + conversion;
- later operation consumes that WIP and adds more conversion;
- final FG receipt.

Assert final trace carries upstream:

- original raw material;
- stage-1 labour/machine/overhead;
- later-stage conversion;

instead of classifying all prior WIP value as raw material.

## F. Raw material cost methods

For production issues valued under supported cost methods:

- Moving Average;
- FIFO;
- Standard.

Assert trace uses `StockValuationFact.CostAmount`.

Changing current `StockCostState`, item master values, or balance display cost after posting MUST NOT change trace.

## G. Permission

With `ViewCost = false`:

- Value remains hidden as today;
- cost-trace API returns AccessDenied;
- no cost components are returned.

## H. NEW receipt

Assert:

- `GetCostTraceAsync` does not present draft estimate as authoritative posted trace;
- result explains trace is available after posting.

## I. REVERSED receipt

Post FG, then rollback.

Assert:

- original posted trace remains identical;
- reversal posting/fact is detected;
- original and reversal quantities/values reconcile;
- popup status indicates reversed.

## J. Corruption / incomplete evidence

Test at least:

- missing FG history;
- mismatched FG fact vs movement value;
- missing valuation evidence;
- produce total not equal consumed + conversion evidence;
- missing material valuation fact;
- recursive cycle / invalid lineage guard.

Expected:

- trace fails closed;
- exact frozen FG total may still be shown if available;
- no trusted component breakdown is emitted.

## K. Build/regression commands

Run:

```bash
dotnet build ErpWeb.slnx
```

Focused:

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=Production"
```

Fast full regression where appropriate:

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category!=SqlServer"
```

If the environment has the configured SQL Server scratch database, also run the SQL Server suites already used by the repository.

# Implementation Order

## STEP 1 — Tests first

Add failing tests for:

- pooled value != FIFO money;
- exact component residual;
- immutable historical authority;
- permission;
- reversed trace;
- incomplete evidence fail-closed.

## STEP 2 — Add pure pooled component math

Create:

`ProductionPooledCostTraceMath.cs`

Implement deterministic exact money allocation and reversal-safe atom operations.

Get all pure tests passing.

## STEP 3 — Add trace contracts/API

Update:

`IProductionFinishedGoodReceiptService.cs`

Add `GetCostTraceAsync` and read-only DTOs.

## STEP 4 — Implement historical trace orchestration

Create:

`ProductionFinishedGoodReceiptService.CostTrace.cs`

Implement:

- authority checks;
- generation/posting-order replay;
- existing `AllocateValue` validation;
- recursive material/WIP lineage;
- conversion fact aggregation;
- raw material valuation fact resolution;
- target component capture;
- reversal validation.

## STEP 5 — Add UI

Update:

- `PrFinishedGoodReceiptEntry.razor`;
- `PrFinishedGoodReceiptEntry.razor.cs`;
- `PrFinishedGoodReceiptEntry.razor.css`.

Add icon, popup, loading/error/result states.

## STEP 6 — Run focused tests

Run Production category tests.

Fix any regression without weakening invariants.

## STEP 7 — Run solution build and fast full suite

Run:

- `dotnet build ErpWeb.slnx`;
- non-SQL full test suite;
- SQL Server suites when available.

# Regression Areas

Must remain unchanged:

- Daily Production posting;
- Production Output rollback;
- production pool quantity/value mutation;
- FG posting;
- FG rollback;
- inventory history writing;
- stock valuation;
- FIFO layer logic;
- Moving Average;
- Standard Cost;
- month-end;
- period close;
- work-order costing snapshots;
- labour/machine/overhead calculation;
- `FinishedGoodReceiptMath.AllocateValue`;
- access-right behavior outside this new trace API.

Primary regression modules:

- Production;
- Inventory costing/valuation;
- historical inquiry;
- FG View UI.

# Do-Not Rules

DO NOT:

- calculate posted FG value from current `ProductionBalLot`;
- calculate material cost from current `IvBalLoc.Cost`;
- calculate historical conversion cost from current WO/master rates;
- treat `PrProductionMovementAllocation` FIFO quantity as money allocation;
- use current Product Definition as historical evidence;
- invent missing cost components;
- silently ignore money residuals;
- introduce a tolerance to hide 19,6 mismatches;
- update posted facts from the trace feature;
- add posting/rollback writes to the trace method;
- add a new costing engine;
- add a new cost snapshot table for this UI unless future repository evidence proves current immutable evidence is insufficient;
- weaken `ViewCost`;
- expose cost data through client-side calculations when permission is denied;
- add unrelated production refactoring.

# Acceptance Criteria

- [ ] FG View `Value` cell has a compact trace-cost icon for POSTED/REVERSED lines when `ViewCost` is allowed.
- [ ] NEW documents are not presented as having authoritative historical cost trace.
- [ ] Popup shows exact posted line value and effective line unit cost.
- [ ] Frozen FG fact, production movement, and inventory history values reconcile exactly.
- [ ] Existing `FinishedGoodReceiptMath.AllocateValue` reproduces every stored source-line FG value for the posting.
- [ ] Pooled component allocation uses all monetary pool contributors, not only FIFO quantity allocations.
- [ ] Material amounts come from frozen `StockValuationFact`.
- [ ] Labour/machine/utilities/other amounts come from frozen `ProductionConversionCostFact`.
- [ ] Recursive WIP carries upstream cost components into the final FG explanation.
- [ ] Component sum equals exact FG `TotalValue` at 19,6 precision.
- [ ] Any final residual is allocated deterministically and exactly.
- [ ] Reversed FG continues to show the original posted trace and validates reversal linkage.
- [ ] Missing/inconsistent evidence fails closed and does not display fabricated components.
- [ ] `ViewCost` is revalidated server-side.
- [ ] No database schema change is introduced.
- [ ] No posting/costing/rollback method is changed to calculate a different financial value.
- [ ] Production category tests pass.
- [ ] Solution build passes.
- [ ] Fast full regression suite passes.
- [ ] SQL Server suites pass when the test environment is available.

# Approval Status

APPROVED FOR IMPLEMENTATION
