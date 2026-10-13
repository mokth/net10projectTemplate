# ProductionV2 Work Order Transfer Entry & Lookup Repair
## FINAL RE-REVIEWED AI CODING AGENT PLAN — APPROVED FOR IMPLEMENTATION — 10/10

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Reviewed HEAD:** `b08deebb7cdd31fe2d0f295cb52f7d96330abf0b`  
**Reviewed tree:** `57110be4fc1168460aa29694b82b921f1bd22a9a`  
**Scope:** Work Order Transfer entry, source/target Work Order lookup, lookup query efficiency, UI state/permissions, and regression hardening.  
**Release priority:** Production quantity balance, exact WIP value, posting atomicity, and safe rollback remain non-negotiable.

---

# 1. Final review verdict

The existing Work Order Transfer business engine is fundamentally sound.

Preserve:

```text
SOURCE PROCESS_WIP
    TRANSFER_OUT
    exact full Qty
    exact full BaseQty
    exact TotalCost
            |
            v
TARGET PROCESS_WIP
    TRANSFER_IN
    same Qty
    same BaseQty
    same TotalCost
```

The current problems are primarily:

1. non-standard Work Order lookup;
2. source Work Order list derived indirectly from deeply validated WIP;
3. poor diagnostic distinction between "WO does not exist" and "WO exists but no transferable WIP";
4. unnecessary full source/target scans during page interaction;
5. confusing side-by-side entry layout;
6. missing stale-request protection on target/source requirement loads;
7. DELETE UI incorrectly depends on Edit permission;
8. no standard dirty-navigation protection;
9. target requirement query applies WorkOrderNo too late, after candidate validation/scanning.

After the corrections in this plan, the change is:

**APPROVED FOR IMPLEMENTATION — 10/10**

Approval is conditional on all quantity/cost/posting/rollback/concurrency/period/future-date regression gates below passing.

---

# 2. What Work Order Transfer is

WOT moves ownership of the **entire remaining stockable WIP pool** from one Work Order to another compatible Work Order.

Example:

```text
Source WO A
WIP001 / LOT001
Qty       100 PCS
Value     RM500

POST

Source WO A
Qty       0
Value     0

Target WO B
WIP001 / LOT001
Qty       +100 PCS
Value     +RM500
```

It is NOT:

- partial transfer;
- Inventory transfer;
- warehouse transfer;
- new production;
- BOM modification;
- item substitution;
- Work Order quantity change;
- FG receipt;
- cost revaluation.

No quantity editor.

No automatic split.

No silent cap.

---

# 3. Current repository authorities

Re-open before implementation:

```text
ErpWeb.Core/Production/IProductionWorkOrderTransferService.cs
ErpWeb.Core/Production/ProductionWorkOrderTransferService.cs
ErpWeb.Core/Production/ProductionWorkOrderTransferService.Rollback.cs
ErpWeb.Model/Configurations/Production/ProductionWorkOrderTransferConfiguration.cs

ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor.css

ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor.css

ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderPicker.razor
ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderPicker.razor.cs
ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderSearchPopup.razor
ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderSearchPopup.razor.cs

ErpWeb.UI/Components/Common/Lookups/SmartLookupInput.razor
ErpWeb.UI/Components/Common/Lookups/SmartLookupInput.razor.cs

ErpWeb.Core/Production/ProductionStockWorkOrderLookupRow.cs
ErpWeb.Core/Lookups/LargeLookupContracts.cs

ErpWeb.Tests/Production/Transaction/ProductionWorkOrderTransferServiceTests.cs
ErpWeb.Tests/Production/Transaction/ProductionWorkOrderTransferSqlServerTests.cs
```

If these contracts materially change before coding, re-review.

---

# 4. Core posting logic MUST remain

Do not redesign `PostAsync()` or rollback mathematics.

Current WOT already uses:

```text
IBranchStockTransactionLock
ProductionWorkOrderExecutionLock
RowVersion
IStockPostingCoordinator
ProductionStockWriter
ProductionPoolValuationService
TRANSFER_OUT
TRANSFER_IN
immutable Work Order Transfer fact
sealed StockPosting
```

Current POST verifies:

```text
source Qty/BaseQty/TotalCost cleared exactly
source + target total quantity conserved
source + target total value conserved
pool valuation matches live balance
source contribution availability cleared
target contribution evidence complete
no unrelated Inventory history/value facts
no Work Order execution projection change
```

Current rollback verifies exact immutable forward evidence and refuses reversal after incompatible downstream/later activity.

Preserve all of this.

---

# 5. Closed period / freeze / month-end authority

WOT POST and ROLLBACK currently pass through:

```text
IStockPostingCoordinator.BeginInTransactionAsync()
```

which invokes:

```text
IStockPeriodGuard
IStockFreezeGuard
```

Do not bypass this.

Required behavior:

```text
closed stock period       -> POST blocked
closed rollback period    -> ROLLBACK blocked
stock freeze violation    -> POST/ROLLBACK blocked
```

Failure must occur before WIP mutation is committed.

No UI change may weaken these controls.

---

# 6. Future-date and chronology authority

Keep current rules:

```text
future TransferDateTime
    SAVE DRAFT allowed

POST before effective date/time
    blocked
```

Also preserve chronology:

```text
transfer time >= source latest movement / stock event
transfer time >= existing target WIP latest movement / stock event
```

No "future WIP" may be consumed implicitly by this UI change.

---

# 7. Source and target Work Order status rules

## Source

Keep current WOT-specific rule:

```text
RELEASED       allowed
IN_PROGRESS    allowed
COMPLETED      allowed
```

Block:

```text
DRAFT
CLOSED
CANCELLED
```

COMPLETED support is intentional for valid surplus residual WIP.

## Target

Allow only:

```text
RELEASED
IN_PROGRESS
```

Block:

```text
DRAFT
COMPLETED
CLOSED
CANCELLED
```

Source and target cannot be the same Work Order.

---

# 8. Sequence readiness

Do NOT make target sequence readiness a WOT hard blocker.

Correct concept:

```text
WOT may pre-position WIP
        |
        v
Daily Production remains responsible for sequence execution
```

A target may show:

```text
WAITING FOR EARLIER STEP
```

and remain selectable.

WOT must never update:

```text
WorkOrder.GoodQty
WorkOrder.RemainingQty
Operation.GoodQty
Operation.ProcessedQty
Operation.TransferredQty
Material.ConsumedQty
```

just to make the target "ready".

---

# 9. Current source lookup problem

Current WOT uses page-local `DxComboBox`.

`SearchEligibleSourcesAsync()` scans WIP and only afterwards derives `SourceWorkOrders`.

Deep candidate validation includes:

```text
PROCESS_WIP shape
route/final operation identity
UOM reconciliation
snapshot validity
verified valuation
average cost reconciliation
complete contribution evidence
physical lot rules
```

Candidate validation exceptions are intentionally swallowed during lookup batching.

This creates poor UX:

```text
WO exists
candidate WIP exists
deep WIP validation fails
        ↓
WO silently absent
```

The entry must distinguish:

```text
WO not found
```

from:

```text
WO exists but has no WIP currently ready for WOT
```

without weakening the deep validation.

---

# 10. Standard Work Order lookup

Reuse the current Production standard:

```text
PrProductionStockWorkOrderPicker
    -> SmartLookupInput
    -> PrProductionStockWorkOrderSearchPopup
```

Do not create another general lookup framework.

Add only backward-compatible optional text/caption parameters if required:

```text
NullText
NotFoundMessage
SearchButtonAriaLabel
PopupHeaderText
SearchPlaceholder
CandidateCountCaption
EmptyListMessage
NoSearchMatchMessage
```

Existing Stock Return / Stock Adjustment behavior must remain unchanged when these parameters are omitted.

Do not rename `CandidateSourceCount` in this patch.

Update only its XML comment if reused by WOT.

---

# 11. Add WOT-specific Work Order lookup methods

Update:

```text
IProductionWorkOrderTransferService
```

Add:

```csharp
Task<LargeLookupPage<ProductionStockWorkOrderLookupRow>>
    SearchSourceWorkOrdersAsync(
        LargeLookupSearchRequest request,
        CancellationToken ct = default);

Task<LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>>
    ResolveSourceWorkOrderAsync(
        string workOrderNo,
        CancellationToken ct = default);

Task<LargeLookupPage<ProductionStockWorkOrderLookupRow>>
    SearchTargetWorkOrdersAsync(
        long sourceProductionBalLotId,
        LargeLookupSearchRequest request,
        CancellationToken ct = default);

Task<LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>>
    ResolveTargetWorkOrderAsync(
        long sourceProductionBalLotId,
        string workOrderNo,
        CancellationToken ct = default);
```

Use WOT's own:

```text
MenuCodes.PlanningWorkOrderTransfer
PermissionCodes.Access
tenant/branch scope
```

Do not borrow Stock Return permission authority.

---

# 12. Source Work Order structural lookup

Search Work Orders directly.

Require:

```text
same CompanyCode
same BranchCode

status:
    RELEASED
    IN_PROGRESS
    COMPLETED

valid current WOT-compatible snapshot:
    SnapshotRevision > 0
    SnapshotHash length 64
    IsFullHierarchy(...)
    IsLegacySnapshot == false
    supported SnapshotHashVersion
```

Do NOT use a non-translatable helper inside the EF query.

Spell the SQL-translatable equivalent of the same WOT snapshot contract.

Require at least one **unreserved structural WIP candidate**.

Structural pool conditions should include:

```text
Kind == WIP
BalanceStage == PROCESS_WIP
OutputType == WIP_STOCKED
Qty > 0
BaseQty > 0
Uom not blank
BaseUom not blank
ConversionFactorToBase > 0
StockStatusCode == AVAILABLE
OriginType == PRODUCED
WorkOrderMaterialId == null
OriginalIssueMovementId == null
ProductionLocationId != null
LotNo not blank
PhysicalLotNo blank OR PhysicalLotNo == LotNo
ProducingRouteStepId != null
WorkOrderOperationId != null
```

Also require SQL-translatable producer shape consistency:

```text
route belongs to same WO
route OutputType == WIP_STOCKED
route OutputItemCode == pool ItemCode
route OutputUom == pool Uom
route OutputBaseUom == pool BaseUom
route OutputConversionFactorToBase == pool factor

operation belongs to that route
operation IsFinalOperation == true
```

Do not run valuation/contribution traversal for the WO popup.

That remains the exact WIP lookup's responsibility.

---

# 13. Active NEW draft exclusion

Current DB already has:

```text
UQ_PrWorkOrderTransfer_NewSource
```

and CreateAsync also checks duplicate NEW drafts.

Make lookup consistent with this.

Exclude a source pool from NEW-selection lookup when:

```text
same company/branch
same SourceProductionBalLotId
Status == NEW
DeletedAtUtc == null
```

Apply this to:

1. source Work Order candidate existence/count;
2. exact `SearchEligibleSourcesAsync()` result rows.

The DB filtered unique index and `CreateAsync()` remain authoritative for races.

Do not remove those server protections.

If an exact typed source Work Order exists but all candidate WIP is already tied to active NEW WOT drafts, return a useful message such as:

```text
Work Order 'WO123' has no unreserved WIP available for a new transfer.
One or more candidate WIP balances already belong to NEW Work Order Transfer drafts.
```

Do not silently invite a duplicate selection.

---

# 14. Source candidate count wording

Because the Work Order popup intentionally does **not** perform full valuation/contribution validation, the popup count is not guaranteed "transferable".

Therefore use:

```text
CANDIDATE WIP
```

not:

```text
TRANSFERABLE WIP
```

This avoids overstating readiness.

Exact source WIP rows remain the final selectable authority.

---

# 15. Source Work Order resolve diagnostics

`ResolveSourceWorkOrderAsync()` must distinguish:

```text
Work Order not found
invalid source status
invalid snapshot
no structural PROCESS_WIP
all candidate WIP already reserved by NEW WOT drafts
valid source WO
```

Examples:

```text
Work Order 'WO001' was not found.

Work Order 'WO001' is CLOSED and cannot be used as a WOT source.

Work Order 'WO001' does not have a current Production snapshot.

Work Order 'WO001' has no remaining stockable PROCESS_WIP.

Work Order 'WO001' has no unreserved WIP available for a new transfer.
```

Do not return a generic "not found" for all cases.

---

# 16. Exact source WIP lookup

Keep:

```text
SearchEligibleSourcesAsync()
```

as the deep authority.

When a source WO has been selected, always call it with:

```text
WorkOrderNo = selectedSourceWorkOrder.WorkOrderNo
```

Keep all existing deep checks:

```text
valuation VERIFIED
complete current-generation contribution evidence
average cost reconciliation
UOM scalar contract
route/final operation identity
physical lot rule
snapshot validation
```

Add the active NEW-draft exclusion only to the lookup result set.

`CreateAsync()` still revalidates under lock.

Do not expose raw internal validation exceptions row-by-row.

If structural candidates exist but none survive deep readiness:

```text
This Work Order has WIP, but no balance currently passes WOT quantity,
valuation and lineage readiness.
```

---

# 17. Preserve existing source/target page contracts

Current DTOs expose:

```text
ProductionWorkOrderTransferSourcePage.WorkOrders
ProductionWorkOrderTransferTargetPage.WorkOrders
ProductionWorkOrderTransferWorkOrderChoice
```

The redesigned WOT entry should stop depending on these lists.

However:

**do not remove these DTO properties/types in this patch.**

Keep them for backward compatibility and minimal regression risk.

A later cleanup may remove them after repository-wide usage is proven absent.

---

# 18. Target Work Order lookup

Only enable target WO lookup after an exact source WIP has been selected.

The target lookup should be keyed by:

```text
sourceProductionBalLotId
```

Load/validate the current source WIP sufficiently to obtain:

```text
source WO
ItemCode
Uom
BaseUom
ConversionFactorToBase
```

Target Work Order:

```text
same company/branch
not source WO
RELEASED or IN_PROGRESS
valid current snapshot
```

Require at least one **structural compatible target requirement**.

Material conditions:

```text
ComponentCode == source ItemCode
SupplySource == INTERNAL_ROUTE_WIP
ProducingRouteStepId != null
WorkOrderOperationId != null
BaseUom == source BaseUom
RequiredUom not blank
ConversionFactorToBase > 0
RequiredQty >= 0
RequiredBaseQty >= 0
RequiredQty/BaseQty/factor internally reconcile at IvQty precision
```

Important:

```text
consumer material ConversionFactorToBase
does NOT have to equal source WIP factor
```

Only common BaseUom is required for the consumer material contract.

---

# 19. Target producer structural contract

To avoid showing obvious dead-end target WOs, the Work Order lookup must also verify the producer route structurally:

```text
producer route belongs target WO
OutputType == WIP_STOCKED
OutputItemCode == source ItemCode
OutputUom == source Uom
OutputBaseUom == source BaseUom
OutputConversionFactorToBase == source factor
```

Require exactly one final producer operation for that route.

Also require the consuming operation/material route pointers to belong to the same target Work Order.

This is still structural query logic.

Do not perform destination valuation/collision/dependency mutation in the popup query.

`ValidateTargetAsync()` remains authoritative.

---

# 20. Target candidate count wording

For the target Work Order popup use:

```text
CANDIDATE REQUIREMENTS
```

not "MATCHING REQUIREMENTS" if full target validation has not yet run.

This prevents the popup count from overstating readiness.

---

# 21. Target resolve diagnostics

Distinguish:

```text
WO not found
invalid target status
invalid snapshot
source WO selected as target
no INTERNAL_ROUTE_WIP requirement for source item
producer route incompatible with source WIP
```

Examples:

```text
Work Order 'WO200' is COMPLETED and cannot receive a WOT.

Work Order 'WO200' does not have a compatible INTERNAL_ROUTE_WIP
requirement and producer route for WIP001.
```

Do not weaken `ValidateTargetAsync()`.

---

# 22. Critical performance correction in SearchEligibleTargetsAsync

Current code accepts:

```text
query.WorkOrderNo
```

but applies it only **after** candidate material batches have already been scanned and validated.

Fix this.

Before candidate paging/validation:

```csharp
if (!string.IsNullOrWhiteSpace(query.WorkOrderNo))
{
    var workOrderNo = query.WorkOrderNo.Trim();
    targets = targets.Where(x => x.WorkOrder!.WorkOrderNo == workOrderNo);
}
```

Then perform candidate paging.

Keep the later defensive identity comparison if desired, but SQL must already restrict the candidate set.

This is a performance correction only.

It must not change target eligibility rules.

---

# 23. Do NOT load all targets immediately after source selection

Current:

```text
SelectSourceAsync()
    -> LoadTargetsAsync()
```

This scans target requirements before the operator has chosen a target Work Order.

Remove that behavior.

After source WIP selection:

```text
SelectedSource = source
clear target state
show target Work Order SmartLookup
DO NOT call SearchEligibleTargetsAsync yet
```

Target requirement lookup happens only after a receiving WO is selected.

This is mandatory.

---

# 24. New entry flow

Final NEW flow:

```text
STEP 1
Choose SOURCE Work Order

STEP 2
Choose exact transferable WIP

STEP 3
Choose RECEIVING Work Order

STEP 4
Choose/confirm compatible requirement

STEP 5
Review + Save Draft
```

Only reveal the next step after the prior selection exists.

Do not show empty FROM and TO grids side-by-side on initial load.

---

# 25. Source Work Order UI

Initial page:

```text
New Work Order Transfer

Step 1
Choose Source Work Order

[ WO000123                         🔍 ]

Search by Work Order, product or description.
```

Use the standard SmartLookup.

Wrapper width:

```text
max-width about 780px
```

After selection, follow Stock Return behavior:

- hide/lock the editable picker;
- show compact WO context;
- provide explicit `CHANGE WORK ORDER`.

Do not leave a freely editable WO input visible while downstream selections exist.

This avoids accidental state replacement.

---

# 26. Source WIP UI

After source WO selection:

```text
Step 2
Choose WIP Balance
```

Grid:

```text
ITEM / DESCRIPTION
LOT
QTY / UOM
BASE QTY / BASE UOM
WC / PROCESS
LAST MOVEMENT
VALUE STATUS
ACTION
```

Cost value appears only with `VIEW_COST`.

No quantity editor.

After selection:

- show compact selected WIP card;
- provide `CHANGE WIP BALANCE` while unsaved;
- changing source WIP clears every target-dependent state.

---

# 27. Target Work Order UI

Only after exact source WIP selection:

```text
Step 3
Choose Receiving Work Order

Transferring:
WIP001 · LOT001 · 100 PCS

[ WO000456                         🔍 ]
```

Use same standard lookup component.

Target picker must be recreated/cancelled when source WIP changes.

Recommended:

```razor
@key="SelectedSource?.ProductionBalLotId"
```

or an equivalent explicit cancellation/reset mechanism.

This prevents an async lookup started for source A from committing a target for source B.

---

# 28. Target requirement UI

After target WO selection:

call:

```text
SearchEligibleTargetsAsync(
    sourceProductionBalLotId,
    WorkOrderNo = selectedTargetWO)
```

Do not query every target WO.

If and only if:

```text
TargetTotal == 1
AND
Targets.Count == 1
```

auto-select that requirement.

Do not auto-select merely because the current page contains one row.

If more than one:

```text
MATERIAL
BOM REQUIREMENT
NEEDED AT WC / PROCESS
WIP HELD AT WC / PROCESS
SEQUENCE STATUS
ACTION
```

WAITING remains selectable.

---

# 29. Requirement wording

Current WOT data exposes:

```text
RequiredQty
RequiredBaseQty
```

Do not invent a new "remaining requirement" calculation in this UI repair unless a separate repository-backed business rule is implemented and reviewed.

For this patch label it clearly as:

```text
BOM requirement
```

or:

```text
WO material requirement
```

The full-balance warning may compare:

```text
Source.BaseQty > Target.RequiredBaseQty
```

but must describe that number as the target BOM requirement, not guaranteed remaining demand.

Example:

```text
This WIP balance is 100 PCS while the Work Order material requirement is 80 PCS.
WOT always moves the full 100 PCS. It will not split or cap the balance.
```

Do not change WOT quantity because of the warning.

---

# 30. Review section

Show only when source WIP and target requirement are established.

Example:

```text
FROM
WO000123
WIP001 / LOT001
100 PCS
WC01 / PROCESS-A

        ↓

TO
WO000456
WIP001
held at WC02 / PROCESS-B
needed by WC03 / ASSEMBLY

Transfer Date/Time   [ ... ]
Remark               [ ... ]
```

If `VIEW_COST`:

```text
source current value
exact transfer value
target current value
projected target value
```

Otherwise no cost/value leakage.

---

# 31. Sequence warning

If:

```text
SequenceReady == false
```

show:

```text
Earlier production steps are not complete.
This WIP may be transferred now, but Daily Production cannot consume it
until predecessor operations are complete.

<BlockingReason>
```

Do not disable Select.

Do not bypass Daily Production sequence logic.

---

# 32. Standard hero / command bar

Use Production Stock Return / Adjustment structure.

Hero:

```text
Planning · Production
New/Edit/View Work Order Transfer

chips:
status
Transfer No
source -> target where available
```

Command bar immediately under hero.

Left:

```text
BACK
CHANGE WORK ORDER        when applicable before draft creation
```

Right actions by lifecycle.

## Unsaved NEW

```text
SAVE DRAFT
```

## Saved NEW — view mode

```text
EDIT DRAFT      if Edit permission
POST            if Post permission
DELETE          if Delete permission
```

## Saved NEW — edit mode

```text
SAVE DRAFT
POST
DELETE
```

subject to permissions.

## POSTED

```text
ROLLBACK
```

subject to Rollback permission.

Do not require Edit permission to display/execute DELETE.

---

# 33. Permission correction

Current UI:

```text
RequestDelete()
    requires CanEditFields
```

This unnecessarily requires Edit permission even though:

```text
ProductionWorkOrderTransferService.DeleteAsync()
```

requires Delete permission.

Correct UI rule:

```text
CanDelete =
    Id > 0
    && Status == NEW
    && CanDeleteAccess
```

independent of Edit permission.

Do not weaken service permission checks.

Keep:

```text
unsaved edit fields -> Add
saved draft edit fields -> Edit
POST -> Post
ROLLBACK -> Rollback
VIEW_COST -> ViewCost
```

---

# 34. Saved NEW edit behavior

For an existing NEW WOT:

Source identity is fixed.

Show source summary from the saved document.

Do not allow replacing:

```text
SourceProductionBalLotId
Source Work Order
Item
Lot
source producer route/operation
```

Keep:

```text
REFRESH SOURCE
```

for refreshing quantity/value snapshot through existing service behavior.

Target may be changed while NEW.

Use an explicit:

```text
CHANGE RECEIVING WORK ORDER
```

action.

When opened:

- preserve current saved target until a new target requirement is confirmed;
- cancellation restores current target unchanged;
- Save is not allowed while a target-change picker is open without a confirmed new target;
- selecting a new target requirement updates the draft through existing `UpdateAsync()`.

Do not silently clear the saved target merely by opening the picker.

---

# 35. Unsaved source change behavior

Changing source WO or source WIP invalidates target context.

Clear:

```text
SelectedTargetWorkOrder
SelectedTarget
Target rows
Target total/page/search
target value preview
sequence warning
```

If meaningful data would be lost, use a confirmation dialog consistent with Stock Return.

Meaningful data includes:

```text
selected source WIP
selected target
modified TransferDateTime
Remark
```

After confirmation return to the appropriate earlier step.

---

# 36. Dirty navigation

Implement the standard:

```text
NavigationLock
```

and copy the established Production Stock Return pattern.

Do not add a new navigation framework.

Track serialized/snapshotted UI state.

Dirty state should cover meaningful editable data.

After successful Save:

```text
reset saved snapshot
```

Do not prompt on simple initial page load.

---

# 37. Async cancellation / stale result protection

Current WOT source debounce only cancels the delay; once `LoadSourcesAsync()` begins, it does not pass the cancellation token into the service.

Current target load has no cancellation/version guard.

Fix this.

Mirror Stock Return/Adjustment pattern:

```text
CancellationTokenSource
monotonic load version / sequence
pass ct to service
discard stale completion
dispose CTS
```

Use separate state for:

```text
source WIP load
target requirement load
```

Implement:

```text
IAsyncDisposable
```

on the entry component and cancel both outstanding lookups during dispose/navigation/reset.

This prevents stale rows from an old WO/source being displayed under a new selection.

---

# 38. Initial page load

For NEW:

Do NOT call:

```text
LoadSourcesAsync()
```

on initialization.

Initialize only:

```text
permissions
TransferDateTime = Clock.Now
status NEW
cost visibility
```

Work Order data loads only when the SmartLookup searches/resolves.

This removes the current expensive WIP-universe scan.

---

# 39. Page initialization guard

Follow Stock Return/Adjustment's `_loadedKey` approach or an equivalent stable route-load guard.

Do not reinitialize editable NEW state unexpectedly on repeated parameter lifecycle calls.

Key should include:

```text
Id
Mode / relative path
```

Reset lookup cancellation state only when the logical page changes.

---

# 40. Error / empty states

Distinguish:

## WO missing

```text
Work Order 'WO001' was not found.
```

## Invalid status

```text
Work Order 'WO001' is CLOSED and cannot be used as a WOT source.
```

## Invalid snapshot

```text
Work Order 'WO001' does not have a current Production snapshot.
```

## No structural WIP

```text
WO001 has no remaining stockable PROCESS_WIP.
```

## WIP exists but none deeply ready

```text
WO001 has WIP, but no balance currently passes WOT quantity,
valuation and lineage readiness.
```

## All candidate WIP reserved by NEW WOT

```text
WO001 has no unreserved WIP available for a new transfer.
```

## No target WO

```text
No active receiving Work Order currently has a compatible
INTERNAL_ROUTE_WIP requirement for WIP001.
```

## Selected target WO produces incompatible WIP

```text
WO002 does not have a compatible producer route for WIP001.
```

Keep server messages for concurrency/posting failures.

---

# 41. UI widths / professional layout

Reuse Stock Return CSS principles.

Preferred:

```text
page max width         ~1480px
WO lookup              max ~780px
Date/time              <= 220px
Qty                    110–140px
UOM                    80–110px
WO/item/lot            semantic bounded widths
Remark                  wider but not arbitrary full-page width
```

Desktop:

```text
progressive vertical sections
compact metadata grids
horizontal alignment
```

Mobile:

```text
one-column flow
buttons wrap
lookup full available width
grids scroll only when necessary
```

No random Bootstrap col spans that produce uneven editor alignment.

No global CSS changes.

---

# 42. No DB schema change

This patch must not add migrations.

Do not alter:

```text
PrWorkOrderTransfer
PrProductionBalLot
PrProductionBalLotMovement
PrProductionPoolValuation
PrValuationEvidence
PrProductionPostingLink
StockPosting
IvBalLoc
IvTrxHistory
CostLayer
```

`ProductionStockWorkOrderLookupRow` may have its comment generalized for WOT usage.

Do not rename `CandidateSourceCount`.

---

# 43. Files expected to change

Primary:

```text
ErpWeb.Core/Production/IProductionWorkOrderTransferService.cs
ErpWeb.Core/Production/ProductionWorkOrderTransferService.cs
ErpWeb.Core/Production/ProductionStockWorkOrderLookupRow.cs

ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor.css
```

Additive lookup customization if needed:

```text
ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderPicker.razor
ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderPicker.razor.cs
ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderSearchPopup.razor
ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderSearchPopup.razor.cs
```

Tests:

```text
ErpWeb.Tests/Production/Transaction/ProductionWorkOrderTransferServiceTests.cs
ErpWeb.Tests/Production/Transaction/ProductionWorkOrderTransferSqlServerTests.cs
```

Do not introduce a new UI test framework solely for this patch.

If the repo already has a suitable component/UI test pattern, use it; otherwise cover service/state logic with existing test infrastructure plus manual UI acceptance.

---

# 44. Implementation order

## Phase 1 — service lookup APIs

1. Add source WO search/resolve.
2. Add target WO search/resolve.
3. Use server paging.
4. Add precise diagnostics.
5. Add active NEW-draft exclusion.
6. Strengthen structural source/target candidate predicates.

## Phase 2 — exact lookup efficiency

1. Add active NEW-draft exclusion to source exact list.
2. Apply target `WorkOrderNo` filter in SQL **before** target candidate scanning.
3. Preserve all existing deep validators.
4. Preserve page DTO compatibility.

## Phase 3 — shared lookup UI

1. Add optional captions/messages only.
2. Preserve existing defaults.
3. Confirm Stock Return/Adjustment output remains unchanged.

## Phase 4 — NEW entry flow

1. Remove initial source WIP universe load.
2. Add standard source WO picker.
3. Load selected WO's WIP only.
4. Select exact WIP.
5. Reveal target WO lookup.
6. Do not load target requirement universe.
7. After target WO selection load only that WO's requirements.
8. Auto-select only when total exactly one.
9. Show review.

## Phase 5 — saved draft UX

1. Add standard command bar.
2. Add EDIT DRAFT view behavior.
3. Fix DELETE permission condition.
4. Preserve source identity.
5. Add explicit target-change flow.
6. Keep Refresh Source.

## Phase 6 — state safety

1. NavigationLock.
2. loaded-key initialization guard.
3. source/target CTS + version guards.
4. target picker keyed/reset by source WIP.
5. clear downstream state deterministically.

## Phase 7 — regression

Run all current WOT and related Production transaction tests.

---

# 45. Mandatory lookup tests

## Source Work Order search

Verify:

```text
RELEASED + candidate WIP      found
IN_PROGRESS + candidate WIP   found
COMPLETED + residual WIP      found

DRAFT                          excluded
CLOSED                         excluded
CANCELLED                      excluded
```

Search by:

```text
WO No
Product Code
Product Description
```

Verify server paging.

## Source resolve

Test distinct results for:

```text
not found
invalid status
invalid snapshot
no structural WIP
all candidate WIP in NEW WOT draft
valid source
```

## Source exact WIP

Verify exclusion for:

```text
unverified valuation
incomplete contribution evidence
bad UOM reconciliation
wrong producer route
non-final producer operation
invalid physical lot identity
active NEW WOT draft
```

CreateAsync must still reject a duplicate under race.

---

# 46. Mandatory target lookup tests

Verify:

```text
same source item + valid INTERNAL_ROUTE_WIP + valid producer contract -> found
different component -> excluded
source WO itself -> excluded
target RELEASED -> found
target IN_PROGRESS -> found
target COMPLETED -> excluded
invalid target snapshot -> excluded
producer UOM mismatch -> excluded
producer base UOM mismatch -> excluded
producer conversion mismatch -> excluded
missing / duplicate final producer op -> excluded
```

Consumer material may use a different RequiredUom/factor when BaseUom remains compatible.

Sequence blocked target:

```text
still returned
SequenceReady == false
BlockingReason populated
```

---

# 47. Mandatory target filter performance test

Add a regression proving:

```text
SearchEligibleTargetsAsync(
    sourceId,
    WorkOrderNo = WO123)
```

does not validate candidate materials belonging to unrelated Work Orders.

At minimum assert returned results/count are restricted correctly.

Where practical, instrument a test dataset with many unrelated targets so the code path proves the SQL filter is applied before batching.

---

# 48. Quantity / costing regression gates

All current WOT tests must pass.

Mandatory:

```text
source Qty -> 0 after POST
source BaseQty -> 0
source TotalCost -> 0
source AverageUnitCost -> 0

target receives exact source Qty
target receives exact source BaseQty
target receives exact source TotalCost
target AverageUnitCost reconciles
```

Conservation:

```text
sourceBaseBefore + targetBaseBefore
    ==
sourceBaseAfter + targetBaseAfter

sourceValueBefore + targetValueBefore
    ==
sourceValueAfter + targetValueAfter
```

No recalculated transfer cost.

Use exact source TotalCost.

---

# 49. Posting / atomicity regression gates

Verify:

```text
one transaction
branch lock
both WO execution locks
source/destination pool locks
one sealed StockPosting
one TRANSFER_OUT
one TRANSFER_IN
one immutable fact
```

Injected/forced validation failure before commit must leave:

```text
no partial source reduction
no partial target increase
no orphan movement
no orphan sealed posting
```

Do not split WOT posting into separate transactions as part of this UI work.

---

# 50. Concurrency gates

Keep existing SQL Server tests:

```text
concurrent NEW drafts -> one winner
concurrent double POST -> one sealed posting / one pair
concurrent transfers -> one target WIP identity
WOT vs Daily Production race -> nonnegative balances
WOT vs natural production race -> one target WIP pool
```

Add/retain stale UI request protection separately.

DB lock/unique constraints remain authoritative.

---

# 51. Closed period / freeze tests

Add or retain tests proving:

## POST

```text
closed effective stock period
    -> fail
    -> source unchanged
    -> target unchanged
    -> no WOT movements
```

## ROLLBACK

Rollback uses current effective time.

If current stock period/freeze blocks reversal:

```text
rollback fails
original WOT remains POSTED
source/target remain unchanged
```

Never bypass `IStockPostingCoordinator`.

---

# 52. Future-date / chronology tests

Verify:

```text
future draft save succeeds
future POST fails
POST when effective time reached succeeds
```

Chronology:

```text
transfer before source latest movement -> fail
transfer before target latest movement -> fail
```

No quantity/value mutation on failure.

---

# 53. Upstream / downstream tests

Keep:

```text
source Daily Production -> WOT dependency
target Daily Production consumption
FG / later production dependency where applicable
```

After target transferred WIP has been consumed:

```text
WOT rollback blocked
```

After later active source/target WIP movement:

```text
WOT rollback blocked
```

After valid downstream reversal removes blockers:

```text
rollback may proceed only if exact WOT invariants again hold
```

---

# 54. Rollback exactness

Immediate safe rollback must restore:

```text
source exact pre-WOT Qty
source exact pre-WOT BaseQty
source exact pre-WOT TotalCost

target exact pre-WOT Qty
target exact pre-WOT BaseQty
target exact pre-WOT TotalCost
```

Reversal movement pair must reference original transfer movements.

Immutable forward fact remains unchanged.

Append linked reversal evidence.

---

# 55. Inventory isolation

WOT must continue to create no unrelated:

```text
IvTrxHistory
StockValuationFact
Inventory CostLayer
IvBalLoc quantity movement
ProductionMaterialMovement ISSUE/RETURN/CONSUME
```

WOT is Production WIP ownership transfer only.

---

# 56. Work Order execution isolation

Verify POST and ROLLBACK do not alter:

```text
ProductionWorkOrder.GoodQty
ScrapQty
RejectQty
HoldQty
RemainingQty

ProductionWorkOrderOperation.InputQty
ProcessedQty
GoodQty
ScrapQty
RejectQty
HoldQty
ReworkQty
TransferredQty
RemainingQty
```

except where unrelated existing services independently do so.

WOT itself must not.

---

# 57. Permission acceptance

Verify combinations independently:

```text
Add only
Edit only
Delete only
Post only
Rollback only
ViewCost absent/present
```

Critical regression:

A user with:

```text
Delete = true
Edit = false
```

can delete a saved NEW WOT if normal menu/access permission is present.

A user without ViewCost must never receive cost via rendered UI.

Server still enforces each permission independently.

---

# 58. Standard lookup regression

After optional shared picker parameters are added:

Production Stock Return and Production Stock Adjustment must still preserve:

```text
typed exact resolve
search popup
paging
double-click
Enter/Escape
clear behavior
secondary product/status text
existing default captions/messages
```

No visual or behavioral regression unless explicitly configured.

---

# 59. UI acceptance

## Initial NEW

User immediately understands:

```text
Step 1 — Choose Source Work Order
```

No target grid yet.

No whole-WIP-universe scan.

## Source selected

User sees:

```text
WO / Product / Status
candidate WIP list
```

## WIP selected

User sees:

```text
selected item/lot/qty
target WO lookup
```

## Target selected

Only selected WO's requirements are queried.

## One exact requirement

Auto-select only when:

```text
TargetTotal == 1 && Targets.Count == 1
```

## Multiple requirements

User chooses explicitly.

## Saved NEW view

Shows:

```text
EDIT DRAFT
POST
DELETE
```

according to individual permissions.

## POSTED

Shows:

```text
ROLLBACK
```

according to permission.

---

# 60. AI agent MUST NOT change

Do NOT:

- rewrite WOT posting math;
- rewrite rollback math;
- alter `ProductionStockWriter`;
- alter `ProductionPoolValuationService`;
- alter Inventory FIFO or CostLayer;
- mutate `IvBalLoc` for WOT;
- create Inventory history/value facts for WOT;
- change Work Order execution quantities;
- change Work Order status;
- change BOM;
- introduce partial transfer;
- introduce quantity editing;
- silently cap source WIP to target requirement;
- make sequence readiness a WOT hard blocker;
- remove COMPLETED source support;
- change physical lot;
- change source item;
- convert WIP UOM during transfer;
- calculate transfer value from a new average instead of exact source TotalCost;
- remove duplicate-draft DB unique protection;
- bypass branch / WO execution locks;
- bypass stock period/freeze guard;
- bypass chronology;
- bypass RowVersion;
- bypass immutable posting fact;
- remove rollback downstream dependency blockers;
- remove later-active-movement blocker;
- remove existing source/target page DTO contracts in this patch;
- rename `CandidateSourceCount`;
- add DB schema/migration for this UI repair;
- refactor unrelated Production/Sales/Procurement/Inventory code;
- add a new UI testing framework solely for this task.

---

# 61. Acceptance criteria

Implementation passes only if:

1. NEW WOT opens without loading all source WIP.
2. Source WO uses standard SmartLookup/search popup.
3. Source WO search supports WO/product/description.
4. Existing WO is not falsely treated as nonexistent because deep WIP validation fails.
5. Candidate count is labelled honestly.
6. Exact source list retains all quantity/cost/lineage validation.
7. Active NEW WOT source pools are excluded from new selection.
8. Duplicate draft race remains protected by DB + CreateAsync.
9. Selecting source WIP does not scan all target requirements.
10. Target WO uses standard SmartLookup/search popup.
11. Target structural lookup matches current producer/consumer contracts.
12. Target WorkOrderNo filter is applied in SQL before deep candidate scanning.
13. Target sequence WAITING remains selectable.
14. Target exact requirement remains authoritative.
15. Auto-selection occurs only when total result count is exactly one.
16. Saved source identity cannot be replaced.
17. Saved NEW target can be changed explicitly and safely.
18. DELETE no longer incorrectly requires Edit permission.
19. UI has standard hero/command bar and progressive workflow.
20. Source/target async lookup results cannot overwrite newer selections.
21. NavigationLock protects unsaved meaningful edits.
22. No DB schema change.
23. No Inventory FIFO/CostLayer change.
24. No WOT quantity/value behavior change.
25. Source + target quantity/value conservation remains exact.
26. CLOSED period/freeze still blocks POST/ROLLBACK.
27. Future-date and chronology rules still hold.
28. Concurrency SQL Server tests pass.
29. Rollback exactness/downstream blockers pass.
30. Stock Return/Adjustment lookup behavior has no regression.
31. Full Production test suite relevant to IP / Daily Production / FG / WIP transfer passes.

---

# 62. Final implementation target

The operator experience should become:

```text
NEW WORK ORDER TRANSFER

Step 1
Source Work Order
[ WO000123                       🔍 ]

        ↓

Step 2
Available WIP
WIP001 · LOT001 · 100 PCS
WC01 / PROCESS-A
[ SELECT ]

        ↓

Step 3
Receiving Work Order
[ WO000456                       🔍 ]

        ↓

Step 4
Compatible Requirement
WIP001
BOM requirement: 80 PCS
Held at: WC02 / PROCESS-B
Needed at: WC03 / ASSEMBLY
Sequence: WAITING FOR EARLIER STEP
[ SELECT ]

        ↓

Step 5
Review

WO000123
WIP001 / LOT001 / 100 PCS
        ↓
WO000456

Transfer date/time: [...]
Remark: [...]

[ SAVE DRAFT ]
```

After save:

```text
[ BACK ]      [ SAVE DRAFT ] [ POST ] [ DELETE ]
```

View-mode NEW:

```text
[ BACK ]      [ EDIT DRAFT ] [ POST ] [ DELETE ]
```

POSTED:

```text
[ BACK ]      [ ROLLBACK ]
```

The user must be able to understand immediately:

```text
what is moving
which WO owns it now
which WO will receive it
which WIP lot is involved
how much moves
that the entire balance moves
which production step will own/consume it
whether it is sequence-ready
that SAVE does not move stock
that POST moves the WIP
```

without needing knowledge of internal valuation tables.

---

# 63. Final decision

This is a targeted WOT entry/lookup repair.

Preserve:

```text
posting
rollback
exact quantity
exact value
valuation lineage
genealogy
locks
period controls
future-date/chronology controls
permissions
audit evidence
```

Fix:

```text
Work Order lookup
lookup diagnostics
source/target query efficiency
progressive UI
command bar
DELETE permission gating
async stale-result safety
dirty navigation
operator guidance
```

**FINAL STATUS: APPROVED FOR IMPLEMENTATION — 10/10**, provided every mandatory regression and safety gate in this plan passes.
