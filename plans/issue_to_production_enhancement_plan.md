# Issue to Production Enhancement Plan

**Repository:** `mokth/net10projectTemplate`\
**Branch reviewed:** `production`\
**Baseline:** current Issue-to-Production implementation around
`ProductionMaterialIssueService*`,
`ProductionMaterialAllocationService`, inventory posting, production
material movements, and production balance lots.

## 1. Objective

Strengthen the existing Issue-to-Production (IP) implementation without
replacing its current architecture.

The current implementation already has a strong base: frozen Work Order
snapshot validation, BOM/tolerance limits, warehouse and lot validation,
as-of stock checks, transaction-time revalidation, SQL locking,
inventory posting integration, production material balance creation,
cost capture, rollback/reversal, and downstream-dependency protection.

This enhancement should close the remaining production-control and
costing gaps:

1.  Enforce Work Centre / Process execution sequence before material
    issue.
2.  Clarify and control Desired Output semantics across multiple IP
    documents.
3.  Make stock availability aware of allocations in other open IP
    drafts.
4.  Strengthen immutable production material lot identity and cost
    provenance.
5.  Separate issue allowance from actual consumption/production
    variance.
6.  Require an audit reason when issuing above standard BOM quantity.
7.  Add Work Order material-reconciliation rules before WO completion.
8.  Ensure IP, Daily Production, Production Stock Ledger, and production
    costing use one consistent movement/cost chain.

This is intentionally scoped for a practical Malaysian SME ERP and
should not become a SAP-style reservation/MRP project.

------------------------------------------------------------------------

## 2. Preserve Existing Controls

Do **not** weaken or replace these existing controls:

-   Only `Released` / `InProgress` Work Orders may issue.
-   Full non-legacy frozen WO snapshot is mandatory.
-   Snapshot revision/hash stale-check remains mandatory.
-   Material must belong to the selected WO and operation.
-   Only eligible manual warehouse-supplied materials may be manually
    issued.
-   WO BOM warehouse restriction remains authoritative.
-   UOM/base-UOM conversion must reconcile.
-   Lot-controlled stock must use valid, active, non-expired lots.
-   Future stock / unavailable as-of stock must remain blocked.
-   BOM tolerance and cumulative issued/returned quantities remain
    enforced.
-   Posting must revalidate inside the database transaction.
-   Inventory balance rows must remain locked before stock-out.
-   Period-close guard remains mandatory.
-   Inventory posting and production movement creation remain atomic.
-   Actual inventory posting cost must flow into production.
-   Rollback must use reversal facts rather than deleting posted
    history.
-   Rollback must remain blocked when downstream production movements
    depend on the issue.

------------------------------------------------------------------------

# Phase 1 - Shared Production Operation Eligibility

## 3. Add a Shared Operation Eligibility Service

Create a production-domain service rather than embedding sequence rules
separately in IP and Daily Production.

Suggested files:

-   `ErpWeb.Core/Production/IProductionOperationEligibilityService.cs`
-   `ErpWeb.Core/Production/ProductionOperationEligibilityService.cs`

Suggested result contract:

``` csharp
public sealed class ProductionOperationEligibilityResult
{
    public bool IsEligible { get; init; }
    public string? BlockingReason { get; init; }
    public IReadOnlyList<long> BlockingOperationIds { get; init; } = [];
}
```

The service should evaluate the frozen Work Order route hierarchy only.

### Required sequence rule

For the same Work Order:

1.  Work Centres / route steps execute by ascending `StageSequence`.
2.  A higher Work Centre sequence cannot start while a required lower
    sequence is incomplete.
3.  Work Centres with the same `StageSequence` are parallel and may
    start simultaneously.
4.  Within a Work Centre, processes execute by ascending
    `ProcessSequence`.
5.  Processes with the same `ProcessSequence` are parallel and may start
    simultaneously.
6.  A higher process sequence cannot start until all required lower
    process-sequence operations in that Work Centre are sufficiently
    completed.
7.  Never derive the sequence from Product Definition after WO release;
    use the frozen WO snapshot.

### Define "predecessor complete"

Do not guess from only WO status.

The shared service should use the authoritative production execution
facts already used by Daily Production. Prefer actual operation/output
completion state if present.

If the repository currently lacks one authoritative operation-completion
calculation, create one shared calculation from posted production
output/movements and use it in both Daily Production and IP.

### IP behavior

Before: - BOM preview, - draft save/update, and - final post,

check operation eligibility.

The **posting transaction must perform the final authoritative check**
after locking the WO.

Do not rely only on UI/search filtering.

### Search/UI

`SearchEligibleOperationsAsync` should either: - exclude
sequence-blocked operations, or - return them disabled with
`BlockingReason`.

Preferred: show them disabled so users understand why a process cannot
be issued yet.

Example:

> Blocked - Work Centre WC10 / Process CUT must be completed first.

### Important exception

Do not block parallel operations that share the same applicable sequence
number.

------------------------------------------------------------------------

# Phase 2 - Desired Output Semantics

## 4. Treat Desired Output as Material Requirement Basis

The current `ProductionQtyThisIssue` is primarily used to calculate the
BOM quantity for an IP document. It is **not actual production output**.

Keep the database property for compatibility, but change user-facing
wording to:

**Material Requirement Basis Qty**

or, if retaining the current label:

**Desired Output (material calculation basis)**

Add help text:

> Used to calculate this issue's BOM requirement. This is not recorded
> production output.

This prevents users from interpreting IP Desired Output as actual
production.

## 5. Add Cumulative Basis Visibility

For the selected operation, calculate and expose:

-   Planned Output Qty
-   Actual Produced Qty
-   Posted IP Basis Qty
-   Open Draft IP Basis Qty
-   Current Document Basis Qty
-   Remaining unallocated planning basis

This is primarily an operational warning/control.

### Recommended policy

Do not automatically treat posted IP basis quantity as actual
production.

However, prevent obviously inconsistent planning:

``` text
other open draft basis
+ posted active IP basis
+ current basis
> operation planned output
```

If the total exceeds planned output, block save/post unless the business
explicitly decides that overlapping material staging is allowed.

For the current ERP, use the stricter default: **do not allow cumulative
active IP basis to exceed operation planned output**.

Rollback/cancelled documents must no longer contribute.

### Concurrency

Recalculate this cumulative basis during posting while the Work Order is
locked.

------------------------------------------------------------------------

# Phase 3 - Open Draft Stock Awareness

## 6. Reuse Existing Reservation-Aware Allocation Support

`ProductionMaterialAllocationService.GetStockCandidatesAsync` and
`AutoAllocateAsync` already support:

``` csharp
reservedBaseQtyByBalance
```

Do not create a new reservation table in this enhancement.

Instead, create a helper that derives reservations from other open
Issue-to-Production drafts.

Suggested service/helper:

-   `ProductionMaterialIssueDraftReservationReader`

For the current tenant/branch and issue date, aggregate:

``` text
ProductionMaterialIssueLine
 -> IvTrxBatchDetail.FromBalLocId
 -> ProductionPostingLink.Status = Draft
 -> IvTrxBatch.BatchStatus = New
```

Return:

``` csharp
IReadOnlyDictionary<int, decimal> ReservedBaseQtyByBalance
```

### Exclusions

When editing an existing draft: - exclude that draft's own batch from
the reservation total.

Cancelled, deleted, posted, or rolled-back/reopened states must be
handled according to their actual current batch/link status.

## 7. Allocation Availability

For UI proposal purposes:

``` text
Available to this draft
=
As-of usable stock
- allocation in other open IP drafts
```

Feed this dictionary into `ProductionMaterialAllocationService`.

Display, where useful:

-   Physical/As-of Available
-   Reserved by Other IP Drafts
-   Available to Allocate

### Authority rule

Open-draft reservation is **advisory/planning protection**, not
inventory ownership.

Final posting must continue to lock and revalidate real as-of stock.

This means stale drafts cannot create negative stock.

------------------------------------------------------------------------

# Phase 4 - Over-Standard Issue Audit

## 8. Require Reason When Exceeding Standard BOM

Tolerance defines the maximum permitted quantity, but it should not
silently explain why excess material was issued.

For each material:

``` text
StandardAllowedForBasis =
RequestedForProductionQty(...)

if IssueQty > StandardAllowedForBasis
    excess reason required
```

Do not require the reason when quantity is at or below standard.

### Schema

Preferred: store reason at the material line level because different
materials can have different causes.

Enhance `ProductionMaterialIssueLine` with something equivalent to:

``` text
ExcessIssueReason nvarchar(250) null
```

If avoiding schema expansion is strongly preferred, a document-level
reason is acceptable but less precise.

### UI

When line quantity exceeds standard: - show an `Above BOM standard`
indicator; - show standard quantity; - show tolerance maximum; - require
`Excess Issue Reason`.

Examples:

-   Cutting wastage
-   Setup loss
-   Material quality loss
-   Trial run
-   Approved process variance
-   Other

Allow free-text details.

### Posting validation

Revalidate the reason in `PostDraftBatchAsync`; UI-only validation is
insufficient.

Tolerance maximum remains a hard ceiling regardless of reason.

------------------------------------------------------------------------

# Phase 5 - Production Material Lot Identity and Cost Provenance

## 9. Make ProductionBalLot the Authoritative Issued-Material Pile

The production material pile created by IP must preserve the historical
source facts required by Daily Production and costing.

For every `ProductionBalLot` created from an IP movement, ensure
immutable traceability to:

-   Company
-   Branch
-   Work Order
-   Work Order Material
-   Consuming Operation
-   IP Posting Link
-   Original `ProductionMaterialMovement`
-   Inventory batch/detail/history
-   Source `IvBalLoc`
-   Item
-   Base UOM/conversion
-   Source warehouse
-   Source location
-   Physical lot / lot ID
-   Issue transaction date
-   Actual issued quantity/base quantity
-   Actual inventory unit cost
-   Actual total cost
-   Stock posting / ledger identity where applicable

Some of these facts already exist across `ProductionMaterialMovement`,
`ProductionBalLot`, and ledger movements. Do not duplicate columns
unnecessarily.

The enhancement goal is to make sure downstream code **follows immutable
IDs to the original facts** rather than re-reading current master
values.

## 10. Cost Rule

The cost chain must be:

``` text
Inventory posting/history actual cost
    -> ProductionMaterialMovement
    -> ProductionBalLot
    -> Daily Production Consume movement
    -> WIP ProductionBalLot
    -> next process consumption
    -> FG production output
```

Daily Production must never recalculate historical RM cost from: -
current `IvBalLoc.UnitPrice`, - today's average cost, - current item
master cost, or - current FIFO layer price.

Consume the actual cost carried by the selected production pile.

### Partial consumption

When consuming part of a production material pile:

``` text
ConsumedCost =
ConsumedBaseQty * pile cost per base unit
```

Preserve deterministic rounding and ensure the final depletion consumes
any residual rounding amount so:

``` text
sum(child consumed cost) == original pile total cost
```

This is critical for accurate production costing.

------------------------------------------------------------------------

# Phase 6 - Issue vs Consumption vs Variance

## 11. Keep Movement Meanings Separate

Do not use issued quantity as production consumption.

Maintain the existing movement semantics:

-   `Issue`
-   `IssueReversal`
-   `Consume`
-   `ConsumeReversal`
-   `Return`

Use calculations such as:

``` text
EffectiveIssued = Issue - IssueReversal

NetIssued = EffectiveIssued - Return

EffectiveConsumed = Consume - ConsumeReversal

ProductionMaterialBalance =
NetIssued - EffectiveConsumed
```

Use this shared calculation everywhere instead of implementing variants
in individual pages/services.

Create/extend a shared calculation method if needed.

## 12. Production Variance

For reporting and later costing/KPI:

``` text
Standard Consumption for Actual Output
vs
Actual Consumed Qty
```

Do not calculate material usage variance from `IssuedQty`.

Example:

``` text
Required for actual output : 100
Issued                    : 105
Consumed                  : 98
Returned                   : 7
Usage variance             : -2
```

The 105 issue is staging; 98 is actual material consumption.

No advanced variance accounting is required in this phase. Ensure the
data model and calculations do not prevent it later.

------------------------------------------------------------------------

# Phase 7 - Work Order Completion Material Reconciliation

## 13. Add Production Material Reconciliation Guard

Before a Work Order can move to `Completed` / equivalent final status,
verify its production material balances.

Create a reusable service:

-   `IProductionMaterialReconciliationService`
-   `ProductionMaterialReconciliationService`

For each WO material / production material pile calculate:

``` text
Effective Issue
- Return
- Effective Consume
= Remaining Production Material
```

The WO must not silently complete with unresolved material.

### Blocking conditions

Block completion when any of these exist:

-   positive unused issued RM balance;
-   positive WIP/material production pile that should be
    disposed/returned;
-   open NEW IP drafts;
-   unresolved material issue posting state;
-   negative or inconsistent production balance;
-   material movement totals inconsistent with production balance lots.

### Allowed resolution

Remaining material must be explicitly handled by an existing or future
transaction:

-   Return to Inventory;
-   Consume in production;
-   approved scrap/wastage transaction;
-   approved adjustment/disposition.

Do not auto-zero the balance during WO completion.

### User message

Return actionable details, for example:

> WO000123 cannot be completed. ITEM-A has 7.0000 KG remaining in
> production from IP batch 10452.

------------------------------------------------------------------------

# Phase 8 - Rollback and Lifecycle Compatibility

## 14. Update Rollback Checks

Existing rollback dependency protection is good and must remain.

Enhance rollback to account for new facts:

-   sequence eligibility does not prevent rollback;
-   rolled-back IP basis quantity is removed from cumulative basis
    totals;
-   reopened draft contributes to open-draft basis/reservation again;
-   line excess reasons remain with the reopened draft;
-   production material pile cost/source identity remains auditable
    after reversal;
-   reversal must exactly negate original quantity and cost;
-   no downstream consume/WIP/output dependency may exist before
    rollback.

Do not delete historical issue/reversal movements.

## 15. Cancel/Delete

When a NEW draft is cancelled or deleted: - its stock reservation
projection disappears immediately; - its basis quantity no longer counts
against operation planning; - no production movement or production
balance fact is created.

------------------------------------------------------------------------

# Phase 9 - UI Enhancements

## 16. Entry Page

Update:

-   `ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueEntry.razor`
-   `PrMaterialIssueEntry.razor.cs`

Add/adjust:

### Operation header

Show:

-   Work Order
-   Work Centre
-   Process
-   Stage sequence
-   Process sequence
-   Operation eligibility
-   Planned output
-   Actual output
-   Material Requirement Basis Qty

If blocked, show the exact predecessor reason.

### BOM grid

Recommended columns:

``` text
Item
Description
Required for Basis
Previously Net Issued
Other Draft Qty
Standard Remaining
Tolerance %
Maximum Allowed
Current Issue
Excess Reason
Available Stock
Reserved by Other Drafts
Available to Allocate
UOM
Warehouse
```

Keep cost columns permission-controlled.

### Stock allocation dialog

Show:

``` text
Warehouse
Location
Lot
Expiry
Stock Date
As-of Qty
Reserved Other Drafts
Available Qty
Selected Qty
Unit Cost (permission controlled)
```

Preserve FEFO/FIFO suggestion behavior.

------------------------------------------------------------------------

# Phase 10 - Service/API Contract Changes

## 17. Extend Contracts Carefully

Likely additions to `IProductionMaterialIssueService.cs` models:

`ProductionMaterialIssueOperationRow` - `StageSequence` -
`ProcessSequence` - `IsSequenceEligible` - `SequenceBlockingReason` -
`ActualOutputQty` - `PostedBasisQty` - `OpenDraftBasisQty` -
`RemainingBasisQty`

`ProductionMaterialIssueMaterial` - `StandardForCurrentBasis` -
`OtherDraftReservedBaseQty` - `AvailableAfterDraftReservations` -
`RequiresExcessReason`

`ProductionMaterialIssueLineRequest` - `ExcessIssueReason`

`ProductionMaterialStockCandidate` - `ReservedOtherDraftBaseQty` -
preserve existing current/as-of/usable values.

Avoid breaking existing consumers where possible.

------------------------------------------------------------------------

# Phase 11 - Database / Migration

## 18. Minimal Schema Change

Prefer only the schema required for auditability.

Recommended:

``` text
PrProductionMaterialIssueLine
    ExcessIssueReason nvarchar(250) null
```

If existing naming differs, follow repository conventions.

Do **not** introduce: - a general MRP reservation engine; - a new stock
reservation ledger; - a duplicate production-cost table; - duplicate
source/cost fields where immutable FK traceability already provides the
fact.

Add indexes only where query plans require them.

Likely review indexes around:

``` text
ProductionMaterialIssueLine:
  PostingLinkId
  WorkOrderMaterialId
  InventoryBatchNo
  InventoryBatchDetailId

ProductionPostingLink:
  CompanyCode, BranchCode, CommandType, Status
  WorkOrderId
  InventoryBatchNo

ProductionMaterialMovement:
  WorkOrderId
  WorkOrderMaterialId
  WorkOrderOperationId
  PostingLinkId
  OriginalMovementId

ProductionBalLot:
  WorkOrderId
  WorkOrderMaterialId
  OriginalIssueMovementId
```

Reuse existing indexes if already sufficient.

------------------------------------------------------------------------

# Phase 12 - Posting Transaction Order

## 19. Required Final Posting Sequence

`PostDraftBatchAsync` should follow this logical order:

1.  Begin DB transaction.
2.  Lock IP batch/link.
3.  Lock Work Order.
4.  Validate WO status.
5.  Validate snapshot revision/hash.
6.  Load selected operation.
7.  **Validate operation sequence eligibility.**
8.  Validate Material Requirement Basis Qty.
9.  **Validate cumulative active basis quantity.**
10. Lock all WO materials in deterministic order.
11. Recalculate effective issue/return facts.
12. Recalculate other open draft BOM quantities.
13. Validate standard/tolerance ceilings.
14. **Validate excess reasons.**
15. Validate period is open.
16. Begin stock posting/ledger transaction.
17. Lock all selected `IvBalLoc` rows in deterministic stock-slice
    order.
18. Recalculate as-of stock.
19. Validate item/warehouse/UOM/status/lot/expiry.
20. Post inventory stock-out.
21. Capture actual inventory history/cost.
22. Create `ProductionMaterialMovement` issue facts.
23. Create production material balance lots with immutable provenance.
24. Stamp stock/production ledger facts.
25. Update material aggregates.
26. Move WO `Released -> InProgress` where applicable.
27. Write audit event.
28. Complete stock posting.
29. Commit.

No UI-derived availability value is authoritative at step 18.

------------------------------------------------------------------------

# Phase 13 - Integrity Invariants

## 20. Non-Negotiable Invariants

After implementation, these must always hold.

### BOM

``` text
Net issued per WO material
<=
RequiredQty * (1 + Tolerance%)
```

### Desired basis

``` text
Current IP issue
<=
BOM requirement for current basis + tolerance
```

### Allocation

``` text
sum(allocation BaseQty)
==
IssueQty * ConversionFactorToBase
```

within repository rounding tolerance.

### Stock

``` text
posted BaseQty <= usable as-of stock
```

at posting time under lock.

### Warehouse

``` text
allocation warehouse == frozen WO material warehouse
```

### Production material

``` text
NetIssuedBase
=
ConsumedBase
+ ReturnedBase
+ RemainingProductionBase
```

subject only to explicit reversal/scrap/adjustment movement types.

### Cost

``` text
Original IP material cost
=
Consumed cost
+ Returned cost
+ Remaining production material cost
```

subject to explicit cost-bearing scrap/adjustment transactions.

### Rollback

A posted IP can be rolled back only when its issued production material
has no blocking downstream dependency and the full reversible balance
remains.

------------------------------------------------------------------------

# Phase 14 - Validation Scenarios

## 21. Functional Verification

Unit tests are optional if the project is not requiring them, but these
scenarios must be manually/integration verified before approval.

### Sequence

1.  Lower WC incomplete -\> higher WC IP blocked.
2.  Lower WC complete -\> next WC allowed.
3.  Same WC sequence parallel branches -\> both allowed.
4.  Lower process incomplete -\> higher process blocked.
5.  Same process sequence -\> parallel operations allowed.
6.  UI bypass/direct service call -\> post still blocked.

### BOM/tolerance

7.  Standard issue accepted.
8.  Above standard but inside tolerance with reason -\> accepted.
9.  Above standard without reason -\> blocked.
10. Above tolerance -\> blocked regardless of reason.
11. Previous posted issue reduces remaining allowance.
12. Return increases remaining issue allowance correctly.
13. Other open draft reduces draftable BOM allowance.

### Basis quantity

14. Single basis \<= planned output -\> accepted.
15. Cumulative active basis \<= planned -\> accepted.
16. Cumulative active basis \> planned -\> blocked.
17. Cancel draft -\> basis released.
18. Rollback/reopened draft -\> correct basis state.

### Stock

19. Future stock unavailable.
20. Wrong warehouse blocked.
21. Wrong item blocked.
22. Expired lot blocked.
23. Inactive lot blocked.
24. Two drafts select same stock -\> UI availability deducts other
    draft.
25. Concurrent posting -\> second post cannot drive stock negative.

### Cost

26. IP captures actual posted inventory cost.
27. Production pile cost equals IP movement cost.
28. Partial Daily Production consumption preserves proportional cost.
29. Full depletion consumes residual rounding cost exactly.
30. WIP output cost derives from consumed production lots, not current
    inventory price.

### Rollback

31. Unconsumed IP can rollback.
32. Partially consumed IP rollback blocked.
33. Downstream WIP dependency blocks rollback.
34. Successful rollback restores inventory and reverses production
    balance/cost.
35. Reopened draft can be corrected and reposted without duplicate
    movement history.

### WO completion

36. Remaining issued material blocks WO completion.
37. Open IP draft blocks completion.
38. All material consumed/returned and no unresolved state -\>
    completion allowed.
39. Reconciliation error identifies item and remaining quantity.

------------------------------------------------------------------------

# Phase 15 - Implementation Order

## 22. Recommended Development Sequence

Implement in this order to minimize rework:

### Step 1

Create shared operation eligibility calculation/service.

### Step 2

Integrate eligibility into: - IP search, - BOM preview, - draft save, -
final posting.

Then make Daily Production use the same service/rules where applicable.

### Step 3

Clarify Desired Output UI semantics and add cumulative basis
calculation/control.

### Step 4

Implement open-draft stock reservation projection using the allocation
service's existing `reservedBaseQtyByBalance` capability.

### Step 5

Add line-level excess issue reason and server-side validation.

### Step 6

Audit/strengthen ProductionBalLot provenance and cost flow.

### Step 7

Verify Daily Production consumes actual production pile costs and
preserves cost through WIP.

### Step 8

Create production material reconciliation service.

### Step 9

Integrate reconciliation into WO completion.

### Step 10

Update rollback/cancel/delete for all new derived controls.

### Step 11

Update IP UI and user messages.

### Step 12

Run all verification scenarios and reconcile: - inventory history, -
production movements, - production balance lots, - production stock
ledger, - WO material aggregates, - costs.

------------------------------------------------------------------------

# Phase 16 - Definition of Done

The enhancement is production-ready when:

-   IP cannot bypass Work Centre/Process execution sequence.
-   Parallel same-sequence operations still work.
-   Material basis quantity cannot create inconsistent cumulative
    staging beyond operation plan.
-   Other open IP drafts reduce displayed allocatable stock.
-   Posting still relies on locked real stock, not reservations.
-   Over-standard issues require an auditable reason.
-   BOM tolerance remains a hard ceiling.
-   Production material lots retain immutable source and cost
    provenance.
-   Daily Production consumes production-pile cost rather than
    recalculating RM cost.
-   Issue, consume, return, and reversal remain distinct facts.
-   WO completion cannot hide unused issued production material.
-   Rollback remains safe and dependency-aware.
-   Inventory, production balance, stock ledger, and costing reconcile
    after post, partial consume, return, rollback, and repost.
-   Existing tenant/branch isolation, permissions, period close,
    future-stock protection, snapshot protection, and inventory locking
    continue to pass.

------------------------------------------------------------------------

## Final Architecture Principle

Treat Issue-to-Production as the controlled transfer of **quantity + lot
identity + historical cost** from Inventory into Production.

From that point onward, Daily Production and WIP processing must consume
and transform those production-side facts. They must not reconstruct
historical quantity, lot, or cost from current inventory/master data.

That principle is what will keep the later Production Costing Report
accurate.
