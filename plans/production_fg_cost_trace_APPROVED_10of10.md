# Finished Good Cost Trace — AI Code Agent Execution Plan

## Verification / Approval

- Repository: `mokth/net10projectTemplate`
- Branch: `production`
- Verified branch head: `6b729eb24be8f00e3b02ba200cb2e02e920d1e05`
- Verification date: 2026-10-08
- Target screen: `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor`
- Plan status: **APPROVED FOR IMPLEMENTATION**
- Code-Agent readiness: **~10/10**
- Database migration required: **NO**

### Verified authority model

- Exact posted FG line value: `ProductionFinishedGoodFact.TotalValue`
- Corroborating FG production value: `ProductionBalLotMovement.TotalCost`
- Corroborating inventory history value: `IvTrxHistory.ExactTransferredValue`
- Production money precision: `StockLedgerPrecision.Money(...)` = 6 decimals
- FG/production balance quantity precision: `IvQty.Scale` = 4 decimals
- Raw inventory valuation authority: `StockValuationFact`
- Absorbed conversion-cost authority: `ProductionConversionCostFact`
- Production pooled-value evidence: `ProductionValuationEvidence`
- Production value dependency graph: `ProductionPoolDependency`
- `ProductionMovementAllocation` is FIFO **quantity genealogy only**; it is not monetary authority.

# Objective

Add a compact trace button beside each Finished Good `Value` cell in FG Receipt **View** mode.

For POSTED and REVERSED receipts, clicking the button MUST open a read-only popup showing:

- exact frozen FG line value;
- effective line unit cost;
- source production pool quantity/value immediately before the original FG posting;
- Material;
- Labour;
- Machine;
- Utilities / Overhead;
- Other;
- Verified Unclassified value when old/external verified evidence cannot be safely decomposed;
- source production/output/material valuation evidence;
- posting/reversal audit IDs;
- reconciliation status.

The posted FG total MUST remain authoritative.

The component split is an **explanatory derived trace** reconstructed from immutable posting evidence under the repository's pooled-average production value flow. It MUST reconcile exactly to the frozen FG total or fail closed.

# Confirmed Current Behavior

## FG Value UI

File:

`ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor`

Current View-mode `Value` column displays:

`FinishedGoodReceiptLine.TotalValue`

Current service mapping obtains POSTED value from:

`PrFinishedGoodFact.TotalValue`

No trace/drill-down exists.

## FG posting value

Files:

- `ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.Posting.cs`
- `ErpWeb.Core/Production/FinishedGoodReceiptMath.cs`

Posting groups source rows by `ProductionBalLotId` and executes:

```csharp
FinishedGoodReceiptMath.AllocateValue(
    pool.BaseQty,
    pool.TotalCost,
    sourceLines)
```

The allocated value is frozen into:

- `PrFinishedGoodFact.TotalValue`;
- `PrProductionBalLotMovement.TotalCost` with movement type `FG_RECEIPT_OUT`;
- `IvTrxHistory.ExactTransferredValue`.

`FinishedGoodReceiptMath.AllocateValue` sorts by numeric line ID and gives the final numeric-ID line the money residual.

## Production pooled valuation

File:

`ErpWeb.Core/Production/ProductionPoolValuationService.cs`

Confirmed:

- normal outbound production value basis is `POOLED_AVERAGE`;
- pool status/evidence is stored in `PrValuationEvidence`;
- `PrPoolDependency` deliberately includes pooled contributors even when FIFO quantity allocation for a contributor is zero;
- `PrProductionMovementAllocation` performs FIFO quantity genealogy and MUST NOT be used as a money-allocation table.

## Absorbed production cost

Files:

- `ErpWeb.Core/Production/ProductionAbsorbedCostCalculator.cs`
- `ErpWeb.Core/Production/ProductionOutputService.Posting.cs`
- `ErpWeb.Model/Entities/Production/ProductionConversionCostFact.cs`

Frozen conversion types:

- `LABOUR`
- `MACHINE`
- `UTILITIES_OVERHEAD`
- `OTHER`

A normal `PRODUCE` value is verified against:

```text
consumed input value + absorbed conversion facts = PRODUCE TotalCost
```

## Inventory issue valuation

File:

`ErpWeb.Core/StockLedger/InventoryValuationService.cs`

`StockValuationFact` is the immutable material-cost authority.

For FIFO, one production issue can contain multiple valuation facts and each FIFO fact is linked through:

`StockFifoLayerConsumption -> StockFifoLayer`

This can expose the actual FIFO receipt-layer source document.

For Moving Average and Standard Cost, there is no requirement to invent a specific receipt document as the cost origin.

# Corrections Applied to the Previous Plan

The implementation MUST use these corrections.

1. **Do not hard-code `RM`.**
   The UI currently stores company-base value; the historical FG facts do not freeze a guaranteed MYR code. Keep the Value cell numeric. Popup labels monetary amounts as `Company base value`.

2. **Quantity precision is explicitly 4 decimals for FG/production balance replay.**
   Use `IvQty.Round`. Do not substitute `StockLedgerPrecision.Quantity`, which is 6 decimals.

3. **Component amounts are derived trace values, not a second accounting authority.**
   The exact posted FG total remains the only FG monetary authority.

4. **Historical pre-FG pool state MUST exclude the original FG posting.**
   Reconstruct movements with posting sequence `< originalFgPosting.PostingSequence`.

5. **Multi-line FG trace MUST follow numeric `SourceId` ordering.**
   `FinishedGoodReceiptMath` uses numeric IDs. Do not use production movement `PostingLineNo` or string `SourceLineId` ordering for the target FG line group.

6. **Known production evidence must fail closed if incomplete.**
   A verified normal `PRODUCE` with basis `CONSUMED_INPUTS`, `ABSORBED_CONVERSION`, or `CONSUMED_INPUTS+ABSORBED_CONVERSION` MUST reconcile from its recorded dependencies/facts. Missing required evidence is corruption, not `UNCLASSIFIED`.

7. **Verified external/legacy basis may be `UNCLASSIFIED_VERIFIED`.**
   This is allowed only when a verified inbound movement does not claim one of the repository-owned decomposable production bases.

8. **FIFO origin document must come from FIFO layer evidence when shown.**
   Do not label the production Issue document as the FIFO receipt source.

9. **Destination posted unit price is a 4-decimal projection, not exact line-value authority.**
   Use `PrFinishedGoodPriceSnapshot.PostedUnitPrice` for that field and show it separately from the exact line effective unit cost.

10. **Fast Production test command MUST exclude SQL Server tests.**
    Run SQL Server traits separately when the scratch DB is configured.

# Scope

Included:

- FG Receipt View-mode Value-cell trace button.
- POSTED and REVERSED documents.
- Server-side `ViewCost` authorization.
- Exact original FG authority verification.
- Historical production-pool replay.
- Derived pooled component trace.
- Recursive WIP/component provenance.
- Material valuation facts.
- FIFO layer source-document detail.
- Frozen absorbed conversion facts.
- Reversal validation.
- Responsive DevExpress popup.
- Focused unit/integration tests.

# Non-Goals

MUST NOT change:

- `FinishedGoodReceiptMath.AllocateValue`;
- FG posting;
- FG rollback;
- Daily Production posting;
- Daily Production rollback;
- `ProductionPoolValuationService`;
- inventory valuation;
- Moving Average calculation;
- FIFO costing/layer consumption;
- Standard Cost calculation;
- absorbed conversion-cost calculation;
- Work Order costing snapshots;
- month-end / period close;
- posted historical facts.

MUST NOT introduce:

- a new financial costing authority;
- a new cost snapshot table;
- a migration just for this inquiry;
- historical fact backfill;
- master/current price recalculation.

# Exact Files

## Existing files to modify

### Core contract

`ErpWeb.Core/Production/IProductionFinishedGoodReceiptService.cs`

Add:

- `GetCostTraceAsync(...)`;
- read-only trace DTOs.

### UI

`ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor`

Add:

- Value-cell trace icon;
- read-only `DxPopup`.

`ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor.cs`

Add:

- popup state;
- async trace loading;
- stale-request/cancellation protection.

`ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor.css`

Add:

- page-scoped Value-cell layout;
- popup summary/detail responsive styles.

### Existing tests

`ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptTests.cs`

Extend existing FG pooled-value/permission/reversal regression coverage where practical.

## Proposed new files

`ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.CostTrace.cs`

Purpose:

- read-only evidence loading;
- authority checks;
- historical replay;
- recursive provenance;
- DTO construction.

`ErpWeb.Core/Production/ProductionPooledCostTraceMath.cs`

Purpose:

- pure deterministic component-atom allocation;
- exact residual handling;
- no DB access;
- MUST NOT be referenced by posting/costing write paths.

`ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptCostTraceTests.cs`

Purpose:

- end-to-end trace service tests.

`ErpWeb.Tests/Production/Transaction/ProductionPooledCostTraceMathTests.cs`

Purpose:

- pure component allocation/reversal/residual tests.

# Database Changes

**NONE.**

Do not add a migration.

Existing required evidence:

- `PrFinishedGoodReceipt`
- `PrFinishedGoodSource`
- `PrFinishedGoodFact`
- `PrFinishedGoodPriceSnapshot`
- `PrProductionBalLot`
- `PrProductionBalLotMovement`
- `PrValuationEvidence`
- `PrPoolValuation`
- `PrPoolDependency`
- `PrProductionMovementAllocation`
- `PrProductionConversionCostFact`
- `StockPosting`
- `StockValuationFact`
- `StockFifoLayer`
- `StockFifoLayerConsumption`
- `IvTrxHistory`

# Authority Rules

## Exact FG monetary authority

Primary:

```text
ProductionFinishedGoodFact.TotalValue
```

Required equality:

```text
FG Fact.TotalValue
==
FG ProductionBalLotMovement.TotalCost
==
FG IvTrxHistory.ExactTransferredValue
```

Do not replace this value with:

- current pool value;
- current stock cost;
- destination unit price;
- master price;
- recomputed material cost.

## Production movement authority

For historical production flow:

```text
ProductionBalLotMovement.TotalCost
```

is the exact movement value.

`ProductionValuationEvidence` determines:

- generation;
- VERIFIED/UNVALUED state;
- valuation basis.

Do not recompute a movement's historical TotalCost from quantity × a current/derived rate.

## Material authority

Use:

`StockValuationFact.CostAmount`

Mirror the validation already used by `ProductionPoolValuationService` for production `ISSUE`:

- same StockPosting;
- same InventoryHistory;
- direction `< 0`;
- movement code `PRODUCTION_MATERIAL_OUT`;
- valuation status `VALUED`;
- `IvQty.Round(sum(BaseQty)) == IvQty.Round(issueMovement.BaseQty)`;
- `StockLedgerPrecision.Money(sum(CostAmount)) == StockLedgerPrecision.Money(issueMovement.TotalCost)`.

## Conversion authority

Use:

`ProductionConversionCostFact.CostAmount`

Do not recalculate from current labour, machine, operation, or Product Definition values.

## Quantity genealogy

`ProductionMovementAllocation` is audit/quantity genealogy only.

It MUST NOT drive monetary component allocation.

## Destination unit price

Use original:

`ProductionFinishedGoodPriceSnapshot.PostedUnitPrice`

as the destination-balance price snapshot.

It is not the exact line value.

# API Contract

Add to:

`IProductionFinishedGoodReceiptService`

```csharp
Task<IvMasterOperationResult<FinishedGoodCostTrace>> GetCostTraceAsync(
    int receiptId,
    long sourceId,
    CancellationToken ct = default);
```

Recommended read-only DTO shape:

```csharp
public sealed class FinishedGoodCostTrace
{
    public int ReceiptId { get; init; }
    public int BatchNo { get; init; }
    public long SourceId { get; init; }
    public long ProductionBalLotId { get; init; }

    public string DocumentStatus { get; init; } = "";
    public string TraceStatus { get; init; } = "";
    public string BreakdownBasis { get; init; } = "DERIVED_POOLED_COMPONENT_TRACE";

    public string WorkOrderNo { get; init; } = "";
    public string ItemCode { get; init; } = "";
    public string SourceLot { get; init; } = "";

    public decimal SourceQty { get; init; }
    public string SourceUom { get; init; } = "";
    public decimal BaseQty { get; init; }

    public decimal DestinationQty { get; init; }
    public string DestinationUom { get; init; } = "";

    public decimal ExactPostedValue { get; init; }
    public decimal EffectiveLineUnitCost { get; init; }
    public decimal? DestinationPostedUnitPrice { get; init; }

    public int ValuationGeneration { get; init; }
    public decimal PoolBaseQtyBeforePosting { get; init; }
    public decimal PoolValueBeforePosting { get; init; }

    public long OriginalPostingId { get; init; }
    public long ProductionMovementId { get; init; }
    public long? ReversalPostingId { get; init; }

    public IReadOnlyList<FinishedGoodCostTraceComponent> Components { get; init; } = [];
    public IReadOnlyList<FinishedGoodCostTraceDetail> Details { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
```

Use trace statuses:

- `VERIFIED`
- `VERIFIED_WITH_UNCLASSIFIED`
- `INCONSISTENT`

Document status remains separate (`POSTED` / `REVERSED`).

Component types:

- `MATERIAL`
- `LABOUR`
- `MACHINE`
- `UTILITIES_OVERHEAD`
- `OTHER`
- `UNCLASSIFIED_VERIFIED`

Detail DTO MUST support:

- component type;
- source kind;
- source fact/movement ID;
- source document type/no;
- optional origin document type/no;
- production output/document no;
- item;
- lot;
- qty/UOM;
- rate or cost method;
- valuation source;
- allocated amount.

All DTOs are read-only inquiry output.

# Exact Core Implementation

## 1. Authorization / scope

Implement in:

`ProductionFinishedGoodReceiptService.CostTrace.cs`

Start with the existing service authorization pattern.

MUST require:

- Planning Finished Good Receipt Access;
- `PermissionCodes.ViewCost`;
- active company + branch scope.

Recommended:

reuse `ScopeAsync(PermissionCodes.ViewCost, ct)`.

Unauthorized result:

- `IvMasterErrorCode.AccessDenied`;
- no cost DTO.

## 2. Load target receipt/source

Load the FG receipt using the same company/branch boundary as existing FG service reads.

Require:

- receipt exists;
- source belongs to receipt;
- source belongs to receipt's production pool;
- receipt has original `PostingId`;
- document status is `POSTED` or `REVERSED`.

For `NEW`:

return validation failure:

`Authoritative posted cost trace is available after posting.`

Do not return a draft value from this API.

## 3. Load original FG authority envelope

Load exactly one original forward `ProductionFinishedGoodFact`:

```text
BatchId == receiptId
SourceId == sourceId
StockPostingId == receipt.PostingId
ReversesFactId == null
```

Load:

- fact;
- source;
- source pool;
- FG `ProductionBalLotMovement`;
- `IvTrxHistory`;
- `ProductionValuationEvidence`;
- original `StockPosting`;
- `ProductionFinishedGoodPriceSnapshot`.

Require:

```text
fact.StockPostingId == receipt.PostingId
movement.Uid == fact.ProductionMovementId
history.Id == fact.InventoryHistoryId
movement.InventoryHistoryId == fact.InventoryHistoryId
movement.ProductionBalLotId == source.ProductionBalLotId
movement.StockPostingId == receipt.PostingId
history.StockPostingId == receipt.PostingId
```

Require V2/sealed authority:

```text
movement.MovementType == FG_RECEIPT_OUT
movement.LedgerVersion == 2
history.LedgerVersion == 2
posting.SealedAtUtc != null
evidence.MovementId == movement.Uid
evidence.ProductionBalLotId == source.ProductionBalLotId
evidence.Status == VERIFIED
evidence.Basis == POOLED_AVERAGE
```

Require quantity equality at FG precision:

```text
fact.BaseQty
==
source.BaseQty
==
movement.BaseQty
==
history.EvidenceBaseQty
```

after `IvQty.Round` where necessary.

Require exact stored money equality:

```text
fact.TotalValue
==
movement.TotalCost
==
history.ExactTransferredValue
```

If the fact exists but corroborating evidence is inconsistent:

- return `TraceStatus = INCONSISTENT`;
- `ExactPostedValue = fact.TotalValue`;
- no trusted component breakdown;
- include diagnostic warning.

Never silently pick a different value.

## 4. Validate destination price snapshot separately

Load snapshot where:

```text
StockPostingId == receipt.PostingId
DestinationBalanceId == fact.DestinationBalanceId
```

For the target history require:

```text
history.UnitPrice == snapshot.PostedUnitPrice
history.Cost == snapshot.PostedUnitPrice
```

For all original FG facts in that posting/destination balance:

```text
IvQty.Round(sum(exact line values) / sum(destination quantities))
==
snapshot.PostedUnitPrice
```

This is a projection validation only.

Do not use it to replace exact line value.

## 5. Resolve target valuation generation

Use target FG `ProductionValuationEvidence.Generation`.

Every production movement included in historical pool replay MUST have:

- matching `ProductionValuationEvidence`;
- same source pool;
- same target generation;
- sealed posting;
- posting sequence earlier than target FG posting.

Target pre-state query boundary:

```text
posting.PostingSequence < originalFgPosting.PostingSequence
```

Do not include target FG movement(s) when building pre-state.

## 6. Reconstruct scalar pre-FG pool state

Order source-pool movement evidence by:

1. `StockPosting.PostingSequence`;
2. `ProductionBalLotMovement.PostingLineNo`;
3. `ProductionBalLotMovement.Uid`.

Use `StockMovementRegistry` direction.

Replay:

```csharp
poolQty = IvQty.Round(poolQty + direction * movement.BaseQty);
poolValue = StockLedgerPrecision.Money(
    poolValue + direction * movement.TotalCost);
```

At every step:

- quantity MUST NOT be negative;
- value MUST NOT be negative;
- quantity `0` with non-zero value is invalid;
- unsealed or missing V2 posting evidence is invalid.

A positive quantity with zero value is valid.

Do not reconstruct posted history from current `ProductionBalLot.TotalCost`.

## 7. Component atom model

`ProductionPooledCostTraceMath` MUST operate on immutable value atoms.

Recommended atom identity:

```text
ComponentType
SourceKind
SourceKey
```

Atom fields:

- stable key;
- component type;
- current value balance;
- immutable detail metadata.

Zero-value verified inputs may have detail metadata but do not need a positive atom balance.

## 8. Resolve original inbound component vector

### A. `ISSUE`

For original forward `ISSUE`:

- require VERIFIED evidence;
- require basis beginning with `STOCK_VALUATION:`;
- load the authoritative `StockValuationFact` rows using the same predicates as `ProductionPoolValuationService`;
- exact fact money sum MUST equal movement TotalCost.

Create `MATERIAL` atoms per valuation fact/split.

#### FIFO detail

If `StockValuationFact.CostMethod == FIFO`:

load:

`StockFifoLayerConsumption`

by `IssueValuationFactId`.

For each FIFO issue fact:

- authoritative cost remains `StockValuationFact.CostAmount`;
- `StockFifoLayerConsumption.ConsumedValue` MUST reconcile to that fact;
- use `StockFifoLayer.SourceDocumentType/No/Line` only as the FIFO origin receipt detail.

If FIFO layer provenance is missing but the authoritative valuation fact is valid:

- material cost remains trusted;
- omit origin receipt fields;
- add a warning.

Do not replace fact money with layer current balances.

#### Moving Average

Show:

- production Issue document;
- cost method;
- valuation source;
- frozen unit/value.

Do NOT invent a purchase receipt source.

#### Standard

Show:

- production Issue document;
- cost method;
- valuation source;
- frozen unit/value.

Do NOT infer a standard-cost revision ID unless the implementation explicitly verifies a unique historical match. Revision drill-down is not required for this feature.

### B. normal `PRODUCE`

Inspect `ProductionValuationEvidence.Basis`.

Repository-owned decomposable bases:

- `CONSUMED_INPUTS`
- `ABSORBED_CONVERSION`
- `CONSUMED_INPUTS+ABSORBED_CONVERSION`

For these bases, decomposition is mandatory.

Load forward dependency rows:

```text
ConsumerMovementId == produceMovement.Uid
ReversesDependencyId == null
StockPostingId == produceMovement.StockPostingId
```

Contributor movements MUST be:

- `CONSUME`;
- same production output;
- same posting link;
- same stock posting as the `PRODUCE`.

Resolve each consume movement's exact component vector from its own source pool replay.

Load forward conversion facts:

```text
ProductionMovementId == produceMovement.Uid
ReversesFactId == null
```

Map:

- `LABOUR`
- `MACHINE`
- `UTILITIES_OVERHEAD`
- `OTHER`

Require exact:

```text
sum(consume component vectors)
+
sum(conversion facts)
==
produceMovement.TotalCost
```

If a repository-owned decomposable basis is missing required dependency/fact evidence:

`INCONSISTENT`

Do NOT downgrade it to Unclassified.

### C. verified `PRODUCE` with non-standard/external basis

If:

- movement/evidence is VERIFIED;
- basis is NOT one of the repository-owned decomposable production bases;
- exact movement TotalCost is valid;

represent the complete movement value as:

`UNCLASSIFIED_VERIFIED`

Examples include explicit old/test/imported verified basis where component lineage was not recorded.

This rule preserves exact money without inventing a component.

### D. other original verified inbound movement

For verified original inbound movement types that do not have a repository-owned component decomposition:

create:

`UNCLASSIFIED_VERIFIED`

for exact `movement.TotalCost`.

Preserve movement/document identity.

### E. reversal movement

When `OriginalMovementId != null`:

the reversal vector MUST be the exact original movement vector.

Require:

```text
reversal.TotalCost == original.TotalCost
reversal.BaseQty == original.BaseQty
```

Do not proportionally reallocate a reversal.

## 9. Pooled outbound component allocation

For a normal outbound movement where `OriginalMovementId == null`:

the exact amount to remove is always:

`movement.TotalCost`

Do NOT recompute outbound movement money from qty × pool average.

Pure helper input:

- current positive component atom balances;
- exact outbound amount.

### Zero-value rule

If exact outbound amount is `0`:

- allocate `0`;
- leave value atoms unchanged;
- scalar quantity replay still proceeds.

### Final depletion

If exact outbound amount equals current pool value:

- transfer every remaining atom balance exactly;
- resulting component pool value = `0`.

### Partial outbound

For a positive partial outbound:

1. Require `outboundValue <= poolValue`.
2. Select a deterministic residual receiver:
   - largest current atom value;
   - tie-break by stable atom key.
3. For every other positive atom in stable-key order:

```text
share =
Money(outboundValue * atomValue / poolValue)
```

4. Clamp share only to:
   - atom remaining balance;
   - outbound remaining amount.
5. Residual receiver gets the exact remaining outbound amount.
6. If residual exceeds its available balance, fail closed.
7. Subtract all allocations from atom balances.

Required after every outbound:

```text
sum(outbound atom allocations) == exact movement.TotalCost
sum(remaining atoms) == scalar pool value after movement
no atom balance < 0
```

The helper MUST use `StockLedgerPrecision.Money`.

It MUST NOT use `IvQty.Round` for money.

## 10. Cycle/corruption protection

Recursive resolution MUST maintain:

- active recursion movement-ID set;
- resolved movement-vector cache;
- resolved pool-snapshot cache where practical.

If a movement is re-entered while active:

`INCONSISTENT: cyclic production value lineage`

Reject dependency edges where contributor posting is after the consumer posting.

Do not recurse indefinitely.

## 11. Validate the original FG allocation as a group

This is mandatory for multi-line accuracy.

For the target original FG posting:

load **all forward original FG facts from the same source production pool**:

```text
StockPostingId == receipt.PostingId
movement.ProductionBalLotId == targetPoolId
ReversesFactId == null
```

Resolve their source rows.

Sort by numeric:

`ProductionFinishedGoodFact.SourceId`

Use the reconstructed pre-FG scalar pool:

```csharp
FinishedGoodReceiptMath.AllocateValue(
    poolQtyBeforePosting,
    poolValueBeforePosting,
    lines.Select(x => (x.SourceId, x.BaseQty)))
```

Every calculated line value MUST equal its stored fact `TotalValue`.

This validation MUST happen before component display.

### Critical ordering rule

Do NOT use:

- `ProductionBalLotMovement.PostingLineNo`;
- string `SourceLineId` lexical ordering;

to reproduce `FinishedGoodReceiptMath` residual behavior.

The runtime allocation authority uses numeric `SourceId`.

## 12. Derive target FG line components

Clone the exact pre-FG component atom state.

Process all same-pool FG facts from the original posting in numeric `SourceId` order.

For each fact:

- use stored `fact.TotalValue` as exact outbound value;
- allocate component atoms with `ProductionPooledCostTraceMath`;
- capture the vector for the requested target `SourceId`;
- subtract it from the cloned pool.

This special target-group process is separate from the production movement `PostingLineNo` ordering because the original FG monetary allocation uses numeric source IDs.

Require for requested line:

```text
sum(target component vector)
==
target ProductionFinishedGoodFact.TotalValue
```

Aggregate to popup components:

- MATERIAL
- LABOUR
- MACHINE
- UTILITIES_OVERHEAD
- OTHER
- UNCLASSIFIED_VERIFIED

If Unclassified amount > 0:

`TraceStatus = VERIFIED_WITH_UNCLASSIFIED`

Else:

`TraceStatus = VERIFIED`

## 13. Effective unit cost

Exact line unit cost:

```csharp
StockLedgerPrecision.Money(
    ExactPostedValue / DestinationQty)
```

only when `DestinationQty > 0`.

This is a derived line-effective rate.

Do not force equality with `PostedUnitPrice`.

The destination price snapshot is rounded with the existing FG destination-price behavior and can differ slightly from the line-effective rate when several lines share one destination slice or residual money exists.

# Reversal Validation

For a `REVERSED` FG receipt:

continue tracing the ORIGINAL posting/facts.

Additionally load the reversal evidence.

Require:

```text
receipt.ReversalPostingId != null
reversal StockPosting is sealed
reversalPosting.ReversesPostingId == receipt.PostingId
```

For the target line require exactly one reversal FG fact:

```text
reversalFact.ReversesFactId == originalFact.Id
```

Require reversal movement:

```text
reversalMovement.OriginalMovementId == originalMovement.Uid
reversalMovement.MovementType == FG_RECEIPT_REVERSAL
reversalMovement.TotalCost == originalMovement.TotalCost
reversalMovement.BaseQty == originalMovement.BaseQty
```

Require reversal history:

```text
reversalHistory.ReversesHistoryId == originalHistory.Id
reversalHistory.ExactTransferredValue == originalHistory.ExactTransferredValue
reversalHistory.EvidenceBaseQty == originalHistory.EvidenceBaseQty
```

Reversal mismatch:

- `TraceStatus = INCONSISTENT`;
- still show original fact total;
- no trusted component breakdown.

UI badge:

`REVERSED — showing original posted cost`

Do not display restored current pool value as historical cost.

# UI Changes

## Value cell

File:

`PrFinishedGoodReceiptEntry.razor`

Keep existing Value column permission gate:

`Document.CanViewCost`

Keep the current numeric Value display formatting in the grid unless an existing project-wide money formatter is already used on this screen.

**Do not prepend `RM`.**

For POSTED or REVERSED line with `TotalValue`:

render the numeric value and a compact icon button in one row.

Icon:

`fa-solid fa-calculator`

Accessible label/title:

`Trace posted cost`

Click:

```csharp
OpenCostTraceAsync(line)
```

For NEW:

- no active trace icon;
- preserve current estimated Value behavior;
- do not call `GetCostTraceAsync`.

## Popup

Use `DxPopup` and the page's existing popup visual conventions.

Header/business summary:

- FG receipt no;
- POSTED/REVERSED badge;
- Work Order;
- item;
- source lot;
- source qty/UOM;
- destination qty/UOM;
- exact posted company-base value;
- effective line cost;
- destination posted unit price;
- reconciliation status.

Component summary:

| Component | Amount |
|---|---:|
| Material | ... |
| Labour | ... |
| Machine | ... |
| Utilities / Overhead | ... |
| Other | ... |
| Verified Unclassified | only when present |
| **Total** | exact FG value |

Popup monetary audit display:

- exact totals/components: `N6`;
- quantities: `N4`;
- destination posted price: `N4`;
- frozen conversion rate: `N6`.

Show a short note:

`Posted value is authoritative. Component amounts are a reconciled trace derived from frozen posting evidence.`

Detail area:

- Component;
- Source;
- Source Document;
- FIFO Origin Document when applicable;
- Item/Lot;
- Qty/UOM;
- Rate / Cost Method;
- Amount.

Audit/evidence area may be collapsible:

- original StockPosting ID;
- production movement ID;
- pool ID;
- valuation generation;
- pool qty before posting;
- pool value before posting;
- reversal posting ID;
- reconciliation result.

## Inconsistent state

When the service returns `INCONSISTENT`:

show:

`The posted FG value is available, but the detailed cost lineage cannot be proven from the stored evidence.`

Show:

- exact `ProductionFinishedGoodFact.TotalValue`;
- diagnostics/warnings.

Do NOT show a fabricated component table.

## Loading / stale request protection

File:

`PrFinishedGoodReceiptEntry.razor.cs`

Add state:

```csharp
protected bool CostTraceVisible;
protected bool IsCostTraceLoading;
protected string? CostTraceError;
protected FinishedGoodCostTrace? CostTrace;
```

Add:

```csharp
protected async Task OpenCostTraceAsync(
    FinishedGoodReceiptLine line)
```

MUST protect against rapid multi-cell clicks.

Use either:

- per-load `CancellationTokenSource`; or
- monotonically increasing request version.

Preferred:

- cancel previous trace load;
- new CTS per click;
- dispose CTS in existing page `Dispose()`.

A late response MUST NOT replace a newer selected line's popup.

No monetary calculation belongs in Razor/code-behind.

# CSS

File:

`PrFinishedGoodReceiptEntry.razor.css`

Page-scoped only.

Add classes for:

- Value number + icon horizontal alignment;
- right-aligned money;
- compact icon;
- component summary;
- evidence grid;
- warning state;
- responsive popup/detail overflow.

Requirements:

- preserve current `pr-dp-*` visual language;
- value button must not dominate the grid;
- detail section horizontally scrolls on narrow screens;
- popup remains usable on mobile;
- no global CSS changes.

# Read Execution Order

`GetCostTraceAsync`:

1. authorize Access + ViewCost;
2. resolve company/branch;
3. load receipt + source;
4. require original posting and POSTED/REVERSED status;
5. load FG authority envelope;
6. validate exact fact/movement/history value;
7. validate destination price snapshot separately;
8. resolve target valuation generation;
9. load/replay source-pool state strictly before original FG posting;
10. build/replay component atom state;
11. validate grouped original `FinishedGoodReceiptMath.AllocateValue`;
12. derive all same-pool target-posting line component vectors in numeric SourceId order;
13. capture target line vector;
14. require exact component sum == exact fact value;
15. validate reversal evidence when REVERSED;
16. return DTO.

No write transaction.

No `SaveChangesAsync`.

No posting lock.

No branch lock.

No mutation.

# Permanent Invariants

## FG authority

```text
FG Fact.TotalValue
==
FG Movement.TotalCost
==
FG History.ExactTransferredValue
```

## FG quantity

```text
IvQty.Round(Fact.BaseQty)
==
IvQty.Round(Source.BaseQty)
==
IvQty.Round(Movement.BaseQty)
==
IvQty.Round(History.EvidenceBaseQty)
```

## Historical pool

For every replay step:

```text
pool qty >= 0
pool value >= 0
pool qty == 0 => pool value == 0
```

Positive quantity with zero value is valid.

## Component pool

```text
Money(sum(component atom balances))
==
replayed scalar pool value
```

## Normal outbound

```text
Money(sum(component allocation))
==
movement.TotalCost
```

## Normal decomposable PRODUCE

```text
Money(sum(consumed component vectors)
      + sum(conversion facts))
==
produce movement.TotalCost
```

## Original FG allocation

For every same-pool source in original FG posting:

```text
FinishedGoodReceiptMath.AllocateValue(...)
==
stored ProductionFinishedGoodFact.TotalValue
```

## Target trace

```text
Money(sum(target component amounts))
==
target FG Fact.TotalValue
```

## Reversal

```text
reversal quantity == original quantity
reversal value == original value
```

No tolerance is allowed to hide a stored 19,6 money mismatch.

# Concurrency / Performance

This feature is historical read-only inquiry.

Use `AsNoTracking()` for evidence queries where practical.

MUST NOT introduce:

- `UPDLOCK`;
- `HOLDLOCK`;
- branch posting lock;
- stock mutation transaction.

Bound historical reads by:

- company;
- branch;
- production pool;
- valuation generation;
- sealed StockPosting;
- StockPosting sequence.

Recommended memoization inside one trace request:

```text
movementId -> resolved component vector
(poolId, generation, beforePostingSequence) -> replayed component snapshot
```

This prevents repeated recursive WIP replay.

Later postings MUST NOT change the original trace because the read is bounded to the original posting sequence and immutable evidence.

# Tests

## 1. Pure pooled component math

New:

`ProductionPooledCostTraceMathTests.cs`

Test:

### proportional component allocation

Pool:

```text
Material 110
Labour    20
Total    130
```

Outbound exact value:

`65`

Expected:

```text
Material 55
Labour   10
Total    65
```

### 19,6 residual

Use repeating decimal proportions.

Assert:

- exact requested outbound sum;
- deterministic result;
- no negative atom;
- exact remaining pool value.

### largest-atom residual receiver

Assert deterministic tie-breaking.

### final depletion

All component balances transfer exactly.

### zero-cost outbound

Positive quantity movement with exact value `0` does not fail.

### reversal

Original vector restores exactly; no re-proportion.

## 2. Existing pooled FG fixture

Reuse/extend:

`FinishedGoodReceiptTests.cs`

Existing verified scenario:

```text
3 qty / 30
3 qty / 42
4 qty / 48
pool = 10 qty / 120
FG receipt = 6
exact FG = 72
```

Assert:

- exact trace total = 72;
- component total = 72;
- all pooled contributors affect historical pool replay;
- FIFO production quantity allocations are NOT used as money;
- synthetic verified contributor basis becomes `UNCLASSIFIED_VERIFIED`;
- current post/rollback behavior remains unchanged.

## 3. Numeric SourceId ordering regression

Mandatory.

Create multiple FG source lines from the same production pool with source IDs whose numeric and lexical ordering differ, e.g.:

```text
2
10
```

Use a value that creates a money residual.

Assert:

- `FinishedGoodReceiptMath` stored line values are reproduced exactly;
- trace processes target FG component lines by numeric SourceId;
- neither PostingLineNo nor lexical `"10" < "2"` changes the trace.

## 4. Full absorbed-cost trace

Create posted production with:

- material;
- labour;
- machine;
- utilities/overhead;
- other.

Then post FG.

Assert:

```text
Material
+ Labour
+ Machine
+ Utilities/Overhead
+ Other
==
exact FG fact value
```

Assert conversion values originate from `ProductionConversionCostFact`.

Change current Work Order/master rate values after posting where the test model permits.

Reload trace.

Historical amounts MUST NOT change.

## 5. Recursive WIP

Stage/operation 1:

- material + conversion -> WIP.

Later stage:

- consumes WIP;
- adds conversion;
- final FG.

Assert final trace retains upstream component identity instead of collapsing all prior WIP into Material.

## 6. Multi-output pooled mix

Create at least two production outputs into the same source pool with different component mixes.

Partially receive FG.

Assert:

- pooled component trace;
- exact stored line value;
- exact component total;
- no FIFO quantity-as-money behavior.

## 7. Inventory cost methods

Test production material issue under:

- Moving Average;
- FIFO;
- Standard.

Assert trace uses `StockValuationFact`.

### FIFO

Assert FIFO detail can resolve:

`StockFifoLayerConsumption -> StockFifoLayer.SourceDocument*`

without replacing the issue valuation fact as monetary authority.

### Moving Average / Standard

Assert no fake originating receipt is shown.

## 8. Zero-value verified cost

Create a verified zero-value production pool/line supported by existing valuation rules.

Assert:

- trace total 0;
- status remains VERIFIED;
- no divide-by-zero;
- no false inconsistency.

## 9. Permission

With `ViewCost = false`:

- Value remains protected as current UI;
- `GetCostTraceAsync` returns AccessDenied;
- no monetary DTO leaks.

## 10. NEW document

Assert API refuses authoritative trace for NEW.

## 11. REVERSED document

Post then rollback.

Assert:

- original trace remains identical;
- reversal StockPosting links original;
- reversal FG fact links original fact;
- reversal movement links original movement;
- reversal history links original history;
- exact qty/value conserved.

## 12. Fail-closed corruption

Test:

- missing FG movement;
- missing history;
- fact/movement value mismatch;
- fact/history value mismatch;
- missing target valuation evidence;
- target evidence not VERIFIED;
- known PRODUCE basis with missing dependency;
- known PRODUCE basis with missing conversion fact;
- material ISSUE with missing valuation fact;
- recursive dependency cycle;
- future-posting dependency;
- destination snapshot mismatch.

Expected:

- exact fact total may be returned when fact itself exists;
- status = INCONSISTENT;
- trusted component list empty;
- no fabricated values.

# Test / Build Commands

## Fast focused Production tests

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj \
  --filter "Category=Production&Category!=SqlServer"
```

## Solution build

```bash
dotnet build ErpWeb.slnx
```

## Fast full regression

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj \
  --filter "Category!=SqlServer"
```

## SQL Server Production tests

Only when the repository's SQL Server scratch-test environment is configured:

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj \
  --filter "Category=Production&Category=SqlServer"
```

Do not treat skipped/unconfigured SQL Server tests as proof of SQL Server behavior.

# Implementation Order

## STEP 1 — add failing focused tests

Add:

- pure pooled-component tests;
- numeric SourceId residual regression;
- existing 72-value pooled scenario trace test;
- permission/fail-closed tests.

## STEP 2 — implement pure trace math

Create:

`ProductionPooledCostTraceMath.cs`

No DB access.

No posting references to this helper.

Pass all pure tests.

## STEP 3 — add trace API/DTOs

Update:

`IProductionFinishedGoodReceiptService.cs`

Compile.

## STEP 4 — implement authority envelope

Create:

`ProductionFinishedGoodReceiptService.CostTrace.cs`

Implement:

- permission/scope;
- FG fact/movement/history/evidence validation;
- price snapshot validation.

Pass focused authority tests.

## STEP 5 — implement historical pooled replay

Add:

- scalar replay;
- component atoms;
- reversal-vector reuse;
- cycle guard;
- memoization.

Pass pooled fixture tests.

## STEP 6 — add recursive production decomposition

Add:

- ISSUE -> StockValuationFact;
- FIFO layer detail;
- PRODUCE dependencies;
- conversion facts;
- Unclassified verified rule.

Pass full absorbed/WIP tests.

## STEP 7 — implement grouped target FG allocation

Add:

- same-pool original fact group;
- numeric SourceId `AllocateValue` verification;
- numeric SourceId component derivation.

Pass residual regression.

## STEP 8 — implement reversed-document validation

Pass forward/reversal tests.

## STEP 9 — add UI popup

Update:

- `.razor`;
- `.razor.cs`;
- `.razor.css`.

Add cancellation/stale-load protection.

## STEP 10 — regression

Run:

- fast Production tests;
- solution build;
- fast full suite;
- SQL Server Production tests when configured.

# Regression Areas

MUST remain unchanged:

- inventory valuation;
- inventory posting;
- FIFO layers;
- Standard Cost revisions;
- Moving Average state;
- Production Material Issue posting;
- Daily Production;
- Production Output rollback;
- production pool mutation;
- Finished Good posting;
- Finished Good rollback;
- destination balance pricing;
- Work Order snapshot costing;
- month-end / period close.

Primary regression areas:

- Production;
- Inventory costing;
- historical cost inquiry;
- FG View UI.

# Do-Not Rules

DO NOT:

- change financial posting logic to support the popup;
- change `FinishedGoodReceiptMath`;
- read current `ProductionBalLot.TotalCost` as historical posted authority;
- read current `IvBalLoc.Cost` as historical material authority;
- use current master/item/work-order rate to replace frozen facts;
- use `ProductionMovementAllocation` as monetary allocation;
- hard-code `RM`;
- claim Moving Average has one originating receipt;
- claim an Issue document is the FIFO receipt source;
- convert a known decomposable PRODUCE with missing evidence into Unclassified;
- invent a cost component;
- silently absorb a reconciliation difference;
- add a tolerance for 19,6 money mismatch;
- recompute stored production movement TotalCost;
- use lexical SourceLineId ordering for FG residuals;
- mutate facts;
- call `SaveChangesAsync`;
- add database schema for this inquiry;
- weaken `ViewCost`;
- add unrelated refactoring.

# Acceptance Criteria

- [ ] Current `production` branch builds.
- [ ] Value cell has a compact trace icon for POSTED/REVERSED lines with `ViewCost`.
- [ ] No hard-coded MYR/RM prefix is introduced.
- [ ] NEW receipts are not presented as authoritative posted traces.
- [ ] Server re-validates `ViewCost`.
- [ ] FG Fact, production movement, and inventory history exact values reconcile.
- [ ] Target valuation evidence is VERIFIED/POOLED_AVERAGE.
- [ ] Historical pre-FG pool is rebuilt strictly before original FG posting.
- [ ] Pool replay uses `IvQty.Round` for FG/production balance quantity and 19,6 money authority for value.
- [ ] Existing `FinishedGoodReceiptMath.AllocateValue` reproduces every same-pool original FG fact.
- [ ] Same-pool target lines use numeric SourceId order.
- [ ] `ProductionMovementAllocation` never determines money.
- [ ] Material money comes from `StockValuationFact`.
- [ ] FIFO receipt origin, when shown, comes from FIFO layer evidence.
- [ ] Labour/Machine/Utilities/Other money comes from `ProductionConversionCostFact`.
- [ ] Recursive WIP preserves upstream components.
- [ ] Known production basis with missing evidence fails closed.
- [ ] Verified unsupported/legacy basis is represented only as `UNCLASSIFIED_VERIFIED`.
- [ ] Derived component total equals exact FG Fact.TotalValue.
- [ ] Zero-value verified cost is handled without error.
- [ ] Destination price snapshot is shown separately from exact line cost.
- [ ] Reversed FG shows original posted trace and validates all reversal links.
- [ ] Inconsistent evidence shows exact fact total only and no fabricated breakdown.
- [ ] Trace path performs no writes.
- [ ] No database migration is added.
- [ ] Fast Production tests pass.
- [ ] `dotnet build ErpWeb.slnx` passes.
- [ ] Fast full regression passes.
- [ ] SQL Server Production tests pass when the scratch environment is configured.

# Approval Status

**APPROVED FOR IMPLEMENTATION — ~10/10 CODE-AGENT READY**

This approval is valid for:

`mokth/net10projectTemplate` / `production` / `6b729eb24be8f00e3b02ba200cb2e02e920d1e05`

If the branch head changes before implementation, the Coding Agent MUST re-check the touched costing/FG files before applying this plan.
