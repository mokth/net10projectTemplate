# Production Stock Return + Production Stock Adjustment
## FINAL DEEP-REVIEWED UI/UX FIX — APPROVED 10/10 AI CODING AGENT PLAN

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Reviewed HEAD:** `74f09c0ca8c3c70290b16f785b10a8c5abd5263f`  
**Review type:** Fresh repo-backed red-team review of the first UI fix plan  
**Scope:** PRTN/PADJ UI, read-only lookup/query contracts, UI-facing DTO correctness.  
**Explicitly protected:** Production quantity authority, costing, valuation, posting, reconciliation and rollback logic.

---

# 0. Final review verdict

The first UI plan had the correct overall direction, but it was **not safe to implement unchanged**.

This second review found concrete repo-backed corrections that materially improve implementability:

1. `NEW` toolbar handling is genuinely broken and has an unsafe action fall-through.
2. The Work Order maintenance service is the wrong lookup authority for PRTN/PADJ UI because it has a different menu-permission boundary.
3. Creation-eligible Work Orders must **not** be reused as the list-history filter or historical documents can become unfilterable after WO completion.
4. Saved PRTN edit state currently confuses the saved requested return quantity with current returnable quantity.
5. Saved PADJ edit state currently reuses adjustment-line `BaseQty` as if it were the Production pool's current `BaseQty`.
6. PADJ `RequiresCostApproval` does **not** mean manual approval is always required. Empty WIP may use Standard Cost automatically at POST.
7. PRTN/PADJ list quantity summaries contain display-authority errors:
   - PRTN list uses Inventory base qty/UOM instead of saved Production requested qty/UOM;
   - PADJ list formats quantity with Item Code instead of UOM.
8. Leaving the source grid permanently stacked above the document is unnecessarily cluttered; the existing FG Receipt source-picker pattern is better.
9. The repo has no bUnit/Playwright component-test infrastructure; adding one solely for this fix is unnecessary scope expansion.
10. The first plan needed explicit cancellation/version guards for debounced source searches and exact 100-line UI limits.

After the amendments in this document, the plan is:

> **APPROVED FOR IMPLEMENTATION — 10/10**

Approval means the plan is concrete and implementable against the reviewed branch. It does **not** authorize changing the existing Production posting/costing algorithms.

---

# 1. Non-negotiable safety boundary

This task fixes user interaction and UI-facing read models.

## 1.1 Do not modify business authority

Do **not** change:

- PRTN original ISSUE contribution calculation;
- PRTN exact Inventory destination rules;
- PRTN proportional Production carrying-value rules;
- PADJ positive/negative costing hierarchy;
- ProductionMaterialMovement equations;
- effective Production supply/reconciliation calculations;
- `ProductionStockWriter`;
- `ProductionPoolValuationService`;
- `InventoryValuationService`;
- StockPosting creation/sealing;
- rollback dependency/LIFO rules;
- SQL ledger constraints/triggers;
- document lifecycle state semantics.

## 1.2 Allowed Core changes

Allowed Core changes are read/query/DTO changes required by the UI:

- server-paged eligible WO lookup;
- exact WO resolve for the smart lookup;
- additional current context fields in document DTOs;
- correct list display projections;
- source paging already supported by `Skip/Take`.

No new DB table and no schema migration are required.

---

# 2. Authoritative live UI references

Before coding, re-read:

```text
.agents/skills/erp-ui-core/skill.md
.agents/skills/erp-transaction-ui-standard/skill.md
.agents/skills/production-transaction-ui/skill.md
```

Canonical live references:

```text
ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptList.razor/.cs
ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor/.cs/.css

ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueEntry.razor/.cs/.css

ErpWeb.UI/Components/Common/Lookups/SmartLookupInput.razor/.cs/.css
ErpWeb.UI/Sales/Lookups/SaCustomerPicker.razor/.cs
ErpWeb.UI/Planning/Lookups/PrDeliveryRequestSearchPopup.razor/.cs

ErpWeb/wwwroot/css/pr-popup-grid.css
```

Do not copy Sales/Purchase document shells.

---

# 3. Target operator flow

## 3.1 PRTN

```text
Production Stock Return list
        |
       NEW
        |
Search/select Work Order
        |
Source picker opens with candidate MATERIAL_IN stock
        |
ADD one or more sources
        |
Return workspace
        |
Enter deliberate Return Qty
        |
Review projected Production qty + exact Inventory destination
        |
SAVE DRAFT
        |
POST confirmation
        |
POSTED
```

## 3.2 PADJ

```text
Production Stock Adjustment list
        |
       NEW
        |
Search/select Work Order
        |
Source picker opens with MATERIAL_IN / WIP pools
        |
ADD one or more pools
        |
Adjustment workspace
        |
Choose ADD or REDUCE
        |
Enter positive Qty
        |
Review projected New Qty
        |
Optional cost override only when legally applicable
        |
SAVE DRAFT
        |
POST confirmation
        |
POSTED
```

The user must **not** need to memorize Work Order numbers.

---

# 4. Fix list-toolbar functional defects first

Affected:

```text
PrProductionStockReturnList.razor.cs
PrProductionStockAdjustmentList.razor.cs
```

Current defect:

```text
RebuildButtons() creates NEW / POST / ROLLBACK
OnButton() has no NEW branch
```

The handler currently performs selection validation before establishing that the action is POST/ROLLBACK. `"NEW"` can therefore fall into logic that was written for state-changing selected-row actions.

Replace with explicit dispatch:

```csharp
switch (action)
{
    case "NEW":
        if (!CanAdd)
        {
            StatusMessage = "Access denied.";
            return;
        }

        Navigation.NavigateTo(NewRoute);
        return;

    case "POST":
        await BeginPostAsync();
        return;

    case "ROLLBACK":
        await BeginRollbackAsync();
        return;

    default:
        ErrorMessage = $"Unsupported action '{action}'.";
        return;
}
```

No unknown action may fall through.

---

# 5. Exactly one NEW action

Current list markup also renders a standalone top-row `NEW` button.

Remove it.

Target list anatomy:

```text
Hero

[ Search............................... ] [ FILTER ]

CommonDataGridEx
    NEW
    POST
    ROLLBACK
    VIEW / EDIT row actions
```

This follows FG Receipt and prevents duplicate commands with different behavior.

---

# 6. List search/filter cleanup

## 6.1 Search layout

Use:

```razor
<div class="iv-toolbar-row__search">
    <DxTextBox ... CssClass="w-100" />
</div>
```

Do not place a raw `w-100` textbox directly as a flex child.

## 6.2 FILTER

Use the established FILTER popup.

For this fix, keep structured filter scope intentionally small:

```text
Status
```

Do **not** reuse the creation-eligible Work Order lookup as a historical list filter.

Reason:

```text
A historical PRTN/PADJ may belong to a Work Order that is now
COMPLETED/CLOSED/CANCELLED.

Such a WO is not creation-eligible, but its transaction history must
remain searchable/filterable.
```

Free-text list search already supports Work Order number.

If a separate WO dropdown is later desired on the list, it must be built from WOs that occur in transaction history, not from current creation eligibility.

---

# 7. Correct list display authority

This is a UI/data-projection correction only.

## 7.1 PRTN quantity summary

Current list query summarizes:

```text
IvTrxBatchDetail.ToStdQty
IvTrxBatchDetail.ToStdUom
```

Those are Inventory/base destination facts.

For the Production Return list, operator-facing quantity must come from:

```text
ProductionStockReturnLine.RequestedQty
ProductionStockReturnLine.SourceUom
```

So list example becomes:

```text
5.0000 KG
```

in the Production source UOM the user actually entered.

Do not convert the list back to Inventory base quantity just for display.

## 7.2 PADJ quantity summary

Current code formats:

```text
+3.0000 ITEM001
```

because it appends `ICode`.

Correct it to use:

```text
ProductionStockAdjustmentLine.AdjustmentQty
ProductionStockAdjustmentLine.SourceUom
```

Example:

```text
-3.0000 PCS
```

## 7.3 List search consistency

For both list services:

```text
BatchNo
WorkOrderNo
RefNo
```

must remain searchable even when the search term is numeric.

Do not use a branch where numeric text prevents numeric `RefNo` from matching.

---

# 8. Do not use IProductionWorkOrderService as the PRTN/PADJ picker authority

Current entry pages inject:

```text
IProductionWorkOrderService WorkOrders
```

and call:

```text
WorkOrders.GetAsync(...)
```

That service belongs to the Work Order maintenance/menu permission boundary.

PRTN/PADJ users should be authorized by their own transaction menu.

The new picker must therefore be backed by:

```text
IProductionStockReturnService
IProductionStockAdjustmentService
```

respectively.

This also removes the exact-WO-number `GetAsync()` dependency.

---

# 9. Add a shared lookup row contract

Add in a suitable Production contracts file:

```csharp
public sealed class ProductionStockWorkOrderLookupRow
{
    public long WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = "";
    public string ProductCode { get; init; } = "";
    public string? ProductDescription { get; init; }
    public string Status { get; init; } = "";
    public decimal PlannedQty { get; init; }
    public decimal RemainingQty { get; init; }
    public string? OutputUom { get; init; }

    public int SnapshotRevision { get; init; }
    public string SnapshotHash { get; init; } = "";

    public int CandidateSourceCount { get; init; }
}
```

`SnapshotRevision` and `SnapshotHash` are essential because selecting the row establishes the draft snapshot identity.

Do not call the Work Order service again after selection merely to obtain these values.

---

# 10. Add domain-owned eligible WO lookup methods

## 10.1 PRTN interface

Add:

```csharp
Task<LargeLookupPage<ProductionStockWorkOrderLookupRow>>
    SearchEligibleWorkOrdersAsync(
        LargeLookupSearchRequest request,
        CancellationToken ct = default);

Task<LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>>
    ResolveEligibleWorkOrderAsync(
        string workOrderNo,
        CancellationToken ct = default);
```

## 10.2 PADJ interface

Add the same method shapes to `IProductionStockAdjustmentService`.

These are read-only methods.

---

# 11. Exact creation eligibility query

Use one DB-translated base query per transaction type.

## 11.1 Common Work Order requirements

```text
same CompanyCode
same BranchCode
Status == RELEASED or IN_PROGRESS
SnapshotFormatVersion >= ProductionSnapshotFormatVersions.FullHierarchyV2
IsLegacySnapshot == false
SnapshotRevision > 0
SnapshotHash is non-empty / expected 64-char current stored hash
```

Do not call `IProductionWorkOrderService`.

## 11.2 PRTN candidate-source existence

Require at least one structural source candidate matching the same basic shape already used by `SearchSourcesAsync`:

```text
ProductionBalLot.WorkOrderId == WO
Kind == MATERIAL_IN
BaseQty > 0
OriginalIssueMovementId != null
WorkOrderMaterialId != null
SourceIvBalLocId != null
```

Do **not** perform every expensive per-pool readiness check merely to render the WO lookup.

`CandidateSourceCount` means candidate pools, not guaranteed READY lines.

## 11.3 PADJ candidate-source existence

Require at least one:

```text
ProductionBalLot.WorkOrderId == WO
Kind == MATERIAL_IN or WIP
```

Zero-balance existing WIP pools remain valid PADJ candidates under the current service design.

## 11.4 Search fields

Server search:

```text
WorkOrderNo
ProductCode
ProductDescription
```

Blank search returns the first page only.

Sort:

```text
WorkOrderNo descending or current repo-consistent deterministic ordering
```

Use `request.NormalizedSkip` / `request.NormalizedTake`.

## 11.5 Exact resolver

Exact typed code must use the **same eligibility predicate**.

If the WO exists but is not currently eligible, return a clear message:

```text
Work Order 'WO123' is not currently eligible for Production Stock Return.
```

or PADJ equivalent.

Do not silently resolve a WO and then fail later with a blank source page.

---

# 12. Shared Work Order picker UI

Recommended new files:

```text
ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderPicker.razor
ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderPicker.razor.cs

ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderSearchPopup.razor
ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderSearchPopup.razor.cs
```

Reuse:

```text
SmartLookupInput<TItem>
LargeLookupSearchRequest
LargeLookupPage<T>
LargeLookupResolveResult<T>
```

Do not create another custom debounce/resolve textbox implementation.

---

# 13. Shared picker must not inject the wrong business service

The picker must be reusable for PRTN and PADJ.

Use callback/delegate parameters such as:

```csharp
[Parameter] public Func<string, CancellationToken,
    Task<LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>>> ResolveAsync { get; set; }

[Parameter] public Func<LargeLookupSearchRequest, CancellationToken,
    Task<LargeLookupPage<ProductionStockWorkOrderLookupRow>>> SearchAsync { get; set; }
```

and:

```text
Selected
Cleared
```

The PRTN page passes its PRTN service methods.

The PADJ page passes its PADJ service methods.

This avoids:

- injecting both transaction services into a generic component;
- using Work Order maintenance permissions;
- duplicated picker implementations.

Follow `SaCustomerPicker` / `PoSupplierPicker` structure.

---

# 14. Work Order popup UX

Popup should open even with blank search.

Columns:

```text
WORK ORDER
PRODUCT
DESCRIPTION
STATUS
PLANNED
REMAINING
UOM
SOURCE POOLS
```

Suggested width:

```text
90vw
MaxWidth around 1100–1200px
```

Behavior:

```text
Search box
server page size 20
PREV / NEXT
focused row
double-click select
SELECT button
CANCEL button
Enter selects focused row
Escape closes
```

Search placeholder:

```text
WO / product / description
```

Primary picker text:

```text
WO000123
```

Secondary text:

```text
FG001 — Widget Assembly · IN PROGRESS
```

---

# 15. Remove Work Order maintenance service dependency from entry pages

After this change, PRTN/PADJ entry pages should not require `IProductionWorkOrderService` merely to drive their selection/context UI.

When a new WO is selected:

```text
Document.WorkOrderId = row.WorkOrderId
Document.WorkOrderNo = row.WorkOrderNo
Document.SnapshotRevision = row.SnapshotRevision
Document.SnapshotHash = row.SnapshotHash
```

Keep UI-only context:

```text
SelectedProductCode
SelectedProductDescription
SelectedWorkOrderStatus
```

from the selected row.

---

# 16. Existing-document WO context

For existing NEW/POSTED/REVERSED documents, no eligible-WO resolve should be required because a historical WO may no longer be creation-eligible.

Extend both document DTOs with current descriptive WO context:

```text
ProductCode
ProductDescription
WorkOrderStatus
```

Populate these inside the existing transaction service `MapAsync()` where the service already loads the `ProductionWorkOrder`.

This is read-only UI context.

In historical View mode label status as:

```text
Current WO status
```

because WO status can change after the transaction.

The document's saved:

```text
SnapshotRevision
SnapshotHash
```

remain the transaction snapshot identity.

---

# 17. Work Order selection phase

Replace:

```text
DxTextBox Work Order number
LOAD WORK ORDER
```

with:

```text
Work Order
[ smart lookup.......................... ][search]
```

Copy:

```text
Select Work Order
Choose a RELEASED or IN_PROGRESS Work Order.
The lookup shows Work Orders with candidate Production stock.
```

There is no `LOAD WORK ORDER` button.

---

# 18. After WO selection

For a brand-new document:

1. commit selected WO context;
2. clear stale source-search state;
3. clear incompatible selected lines;
4. reset source page to 0;
5. open the source picker automatically;
6. load the first 20 candidate sources.

This removes:

```text
LOAD WORK ORDER
FIND POOLS
```

from the normal path.

---

# 19. Source selection must use a popup, not a permanent stacked grid

The first plan left the source finder permanently above the document workspace.

Do not do that.

Use:

```text
Locked WO context
Document details
Selected lines
```

with:

```text
ADD MATERIAL     (PRTN)
ADD POOL         (PADJ)
```

button in the Lines section.

The button opens a `DxPopup` source picker using `pr-popup-grid.css`.

For the first new document after WO selection, open this picker automatically.

For later additions, the user explicitly reopens it.

This is the existing FG Receipt interaction pattern and produces a much cleaner screen.

---

# 20. Source popup paging

Both existing contracts already expose:

```text
Skip
Take
TotalCount
```

Use them.

```text
SourcePageSize = 20
SourcePage = 0
SourceTotalCount
```

Request:

```text
Skip = SourcePage * SourcePageSize
Take = SourcePageSize
```

Display:

```text
37 source pools · page 1 / 2
PREV    NEXT
```

Delete the current `Take = 100` pseudo-limit.

---

# 21. Debounced source search

Search fields:

## PRTN

```text
item
description
lot
```

## PADJ

```text
item
description
lot
```

Use:

```text
BindValueMode.OnInput
~400ms debounce
CancellationTokenSource
request/version guard
```

When a newer search starts:

```text
cancel previous request
ignore stale completion
```

Entry component should dispose/cancel the active search CTS.

Do not allow an old slower query to overwrite results for newer text.

---

# 22. Source popup limits

Both Save services cap documents at:

```text
1..100 lines
```

UI must show:

```text
Selected 12 / 100
```

When 100 is reached:

- disable all remaining ADD buttons;
- show `Maximum 100 lines reached.`;
- do not wait for Save to reject line 101.

Duplicate pool rules:

```text
same ProductionBalLotId may appear only once
```

Already-added source rows show:

```text
ADDED
```

disabled.

---

# 23. PRTN source popup columns

Use:

```text
ITEM / DESCRIPTION
PRODUCTION LOT
CURRENT PRODUCTION QTY
CURRENT RETURNABLE
UOM
INVENTORY DESTINATION
READINESS
ACTION
```

Destination stacked cell:

```text
WH01 / RM-A
Lot LOT001
```

Do not expose destination as editable.

Readiness text is important because `SearchSourcesAsync()` can return a structural candidate that is not currently safe to return.

---

# 24. PADJ source popup columns

Use:

```text
ITEM / DESCRIPTION
POOL
LOT
CURRENT QTY
UOM
READINESS
ACTION
```

Do not expose costing controls in the source picker.

---

# 25. Source popup empty state

Initial/no candidates:

```text
No candidate Production stock was found for this Work Order.
Choose another Work Order.
```

Filtered zero:

```text
No Production stock matches the current search.
Clear the search to show all source pools.
```

A blank region is not acceptable.

---

# 26. Critical PRTN edit-mode DTO correction

Current `ProductionStockReturnService.MapAsync()` fills:

```text
ReturnableQty = line.BaseQty / line.SourceFactor
```

That represents the line's saved quantity/base quantity relationship, not necessarily the **current remaining original ISSUE contribution**.

Do not use that field as live edit authority.

Add to `ProductionStockReturnLineDto`:

```csharp
public decimal CurrentReturnableQty { get; set; }
public string CurrentReadiness { get; set; } = "";
```

For a NEW draft, populate current source state from the current pool/original ISSUE contribution.

At minimum use the same current contribution authority already used by the PRTN source search.

If current source state can no longer be established:

```text
CurrentReturnableQty = 0
CurrentReadiness = concrete blocker
Document.ReadinessErrors += blocker
```

Do not silently fall back to the saved requested qty.

Historical:

```text
RequestedQty
BaseQty
TotalValue
```

remain unchanged.

---

# 27. PRTN editor reconstruction

When loading a saved NEW draft, construct `ReturnEditor.Source` using:

```text
ProductionQty = current pool qty
ReturnableQty = CurrentReturnableQty
Readiness = CurrentReadiness
```

not the saved line qty.

`RequestedQty` initializes the editable Return Qty.

Therefore:

```text
saved request = what user entered previously
current returnable = what can be returned now
```

remain separate concepts.

---

# 28. Critical PADJ edit-mode DTO correction

Current PADJ entry reconstruction passes:

```text
ProductionStockAdjustmentLineDto.BaseQty
```

into `ProductionStockAdjustmentSource.BaseQty`.

But `ProductionStockAdjustmentLineDto.BaseQty` is the saved **adjustment line base quantity**, not the pool's current base balance.

Add to `ProductionStockAdjustmentLineDto`:

```csharp
public decimal CurrentBaseQty { get; set; }
```

Populate:

```text
CurrentQty = pool.Qty
CurrentBaseQty = pool.BaseQty
```

in `ProductionStockAdjustmentService.MapAsync()`.

When reconstructing the UI source:

```text
Source.Qty = CurrentQty
Source.BaseQty = CurrentBaseQty
```

Never use the adjustment line's `BaseQty` as current pool balance.

---

# 29. Entry page shell / CSS ownership

Current pages reference classes like:

```text
pr-dp-page
pr-dp-section-head
pr-dp-grid
```

whose definitions live in another component's isolated `.razor.css`.

That is not reliable styling ownership.

Replace with component-owned classes:

```text
pr-stock-page
pr-stock-commandbar
pr-stock-commandbar__left
pr-stock-commandbar__right
pr-stock-section-head
pr-stock-meta
pr-stock-meta__item
pr-stock-form
pr-stock-lines
pr-stock-grid
pr-stock-empty
pr-stock-readiness
pr-stock-footer
```

Each PRTN/PADJ isolated CSS file must define the classes it uses.

No dependency on FG Receipt's isolated CSS.

---

# 30. Entry max width

Both entry pages:

```css
.pr-stock-page {
    max-width: 1480px;
    margin-inline: auto;
}
```

Match the existing Production transaction family.

---

# 31. Command bar

Use an explicit two-sided structure.

```text
LEFT
    BACK
    CHANGE WORK ORDER  (new unsaved only)

RIGHT
    EDIT DRAFT
    SAVE DRAFT
    POST
    ROLLBACK
    DELETE
    CREATE CORRECTION
```

Rules:

```text
CHANGE WORK ORDER:
    only Id == 0
    only editable
    only selected WO

saved NEW draft:
    WO identity remains locked

POSTED/REVERSED:
    no editable context actions
```

Do not re-enable WO changes on a saved document.

---

# 32. Change Work Order confirmation

Do not prompt merely because a WO was selected.

Prompt when changing WO would discard meaningful user work:

```text
selected lines > 0
or Reference entered
or Remarks entered
or Effective Date changed from initial default
```

Copy:

```text
Change Work Order?

Selected lines and unsaved entry details will be cleared.
```

Then reset:

```text
WorkOrderId
WorkOrderNo
SnapshotRevision
SnapshotHash
product/status context
Editors
source filters/page/results
```

and reopen WO selection.

---

# 33. Compact document header

Do not repeat the selected WO as another giant read-only textbox.

Locked context card:

```text
Work Order        WO000123
Product           FG001 — Widget Assembly
Current WO Status IN PROGRESS
Snapshot          4
```

Document input form:

```text
Effective Date
Reference
Remarks
```

Use `SizeMode.Small`.

---

# 34. Semantic field widths

Recommended:

```text
Effective Date       ~180px
Reference            max ~320px
Source search        max ~520px where not in popup full row
Qty input            ~112px
PADJ Direction       ~120px
```

Do not apply `w-100` to every short field.

`w-100` is fine inside a wrapper whose width is intentionally bounded.

---

# 35. Mirror server text limits in the UI

Both transactions:

```text
Reference       maxlength 30
Header Remarks  maxlength 200
Line Remarks    maxlength 250
```

PADJ:

```text
Cost approval reason maxlength 250
```

Do not wait for Save to report a preventable length error.

---

# 36. Mirror OTHER-reason validation

PRTN and PADJ services require line Remarks when:

```text
Reason == OTHER
```

UI should show:

```text
Remark required for OTHER.
```

and make Save unavailable until corrected.

Server validation remains unchanged.

---

# 37. PRTN line entry — deliberate quantity

Current AddSource defaults to the maximum return quantity.

Remove this.

New line:

```text
ReturnQty = 0
```

The operator must make an explicit quantity decision.

This is important for expensive material.

---

# 38. PRTN editable line grid

Columns:

```text
ITEM / LOT
CURRENT PRODUCTION
CURRENT RETURNABLE
RETURN QTY
PROJECTED AFTER RETURN
DESTINATION
REASON
REMARKS
REMOVE
```

## Quantity

```text
n4
increment 0.0001
width ~112px
```

UI validation:

```text
ReturnQty > 0
ReturnQty <= CurrentReturnableQty
```

The server still revalidates current pool/contribution at Save and POST.

## Projected after return

```text
CurrentProductionQty - ReturnQty
```

Label/tooltip:

```text
Projected only — based on current loaded Production balance.
Save/POST revalidates stock.
```

Never present this as a persisted fact before posting.

---

# 39. PRTN destination

Display the fixed destination from original ISSUE authority:

```text
Warehouse / Location
Inventory Lot
Item Status if useful
```

Read-only.

This helps the operator verify where the material is going without implying they may redirect it.

---

# 40. PADJ entry — remove signed-number mental burden

Backend contract remains:

```text
AdjustmentQty > 0 = IN
AdjustmentQty < 0 = OUT
```

The normal UI should not require users to type signs.

Editor state:

```csharp
enum AdjustmentDirection
{
    Add,
    Reduce
}

decimal InputQty; // always >= 0
```

Request conversion:

```csharp
AdjustmentQty =
    Direction == AdjustmentDirection.Add
        ? Math.Abs(InputQty)
        : -Math.Abs(InputQty);
```

No backend contract change.

---

# 41. PADJ saved-edit reconstruction

When loading a saved NEW line:

```text
if AdjustmentQty > 0:
    Direction = ADD
    InputQty = AdjustmentQty

if AdjustmentQty < 0:
    Direction = REDUCE
    InputQty = abs(AdjustmentQty)
```

Correction documents use the same mapping.

Do not lose the sign during load/edit/save round-trip.

---

# 42. PADJ editable line grid

Columns:

```text
ITEM / POOL / LOT
CURRENT QTY
DIRECTION
QTY
PROJECTED NEW QTY
REASON
COST STATUS
REMARKS
REMOVE
```

Projected:

```text
ADD:
    CurrentQty + InputQty

REDUCE:
    CurrentQty - InputQty
```

Validation:

```text
InputQty > 0
four-decimal UOM precision
REDUCE => projected qty >= 0
OTHER => remarks required
```

Display:

```text
Projected only — Save/POST revalidates the Production pool.
```

---

# 43. Correct PADJ cost UX semantics

Do not interpret:

```text
ProductionStockAdjustmentSource.RequiresCostApproval
```

as:

```text
manual approval is always required
```

The current service's posting hierarchy is:

```text
negative adjustment
    -> current pooled cost relief

positive, non-empty pool
    -> current Production pool cost

positive, empty MATERIAL_IN
    -> original ISSUE cost

positive, empty WIP with saved manual/zero override
    -> override

positive, empty WIP without override and Standard cost method
    -> effective Standard Cost

otherwise
    -> POST fails and manual/zero override is needed
```

Therefore an empty WIP can still post automatically under Standard Cost.

---

# 44. PADJ main-grid Cost Status

Do not permanently display:

```text
Cost Evidence
Proposed Unit Cost
Approval Reason
```

for every row.

Show a compact status.

Examples:

## REDUCE

```text
System — pooled cost
```

## ADD to non-empty pool

```text
System — current pool cost
```

## ADD to empty MATERIAL_IN

```text
System — original ISSUE cost
```

## ADD to empty WIP, no override

```text
Automatic Standard Cost if configured
```

with secondary warning:

```text
If no effective Standard Cost exists, POST will require an approved cost override.
```

## Saved manual override

```text
Manual override
```

## Saved zero override

```text
Approved zero cost
```

Do not falsely show `Approval required` solely because `RequiresCostApproval == true`.

---

# 45. PADJ cost override popup

Only offer an override when all are true:

```text
Direction == ADD
current pool BaseQty == 0
PoolKind == WIP
CanPriceOverride
```

This is why `CurrentBaseQty` must be correct.

Button text:

```text
SET COST OVERRIDE
```

or when one exists:

```text
EDIT COST OVERRIDE
```

Popup:

```text
Cost evidence
    MANUAL_APPROVED
    ZERO_COST_APPROVED

Proposed unit cost
Approval reason

CANCEL
APPLY
```

Validation mirrors the service:

```text
MANUAL_APPROVED => unit cost > 0
ZERO_COST_APPROVED => unit cost == 0
approval reason required
reason <= 250 chars
```

If direction is changed to REDUCE:

```text
clear pending CostEvidenceType
clear ProposedUnitCost
clear CostOverrideReason
```

because the server rejects manual/zero evidence on a negative adjustment.

Do not invent a second cost calculation in UI.

---

# 46. PriceOverride vs ViewCost permissions

Preserve existing permission semantics:

```text
PriceOverride
    controls ability to enter an override

ViewCost
    controls visibility of monetary cost/value facts
```

Do not require ViewCost merely to use PriceOverride if the existing service does not.

Do not expose historical monetary values to a user without ViewCost.

---

# 47. Save gate — PRTN

UI `CanSave` requires:

```text
editable NEW
WO selected
1..100 lines
all ReturnQty > 0
all ReturnQty <= loaded CurrentReturnableQty
all reasons valid
OTHER lines have Remarks
text lengths valid
EffectiveDate not future
```

The service remains authoritative and can still reject stale current stock/snapshot.

---

# 48. Save gate — PADJ

UI `CanSave` requires:

```text
editable NEW
WO selected
1..100 lines
all InputQty > 0
all projected REDUCE qty >= 0
all reasons valid
OTHER lines have Remarks
all entered cost overrides locally valid
text lengths valid
EffectiveDate not future
```

Do not require a manual cost override merely because the pool is empty WIP.

---

# 49. Readiness presentation

Use a structured panel.

Example ready:

```text
Ready to save
3 lines selected
```

Example blocked:

```text
Action required
• Line RM001: Return Qty must be greater than zero.
• Line RM003: Remark is required for OTHER.
```

Existing `Document.ReadinessErrors` remain visible separately for server/domain readiness.

Do not merge a UI typo with a domain posting blocker into one vague message.

---

# 50. POST confirmation on entry pages

Current entry POST runs directly.

Add confirmation.

## PRTN

```text
Post PRTN 12345?

3 material lines will move from Production back to their original Inventory balances.
The destination cannot be changed after posting.
```

## PADJ

```text
Post PADJ 12346?

3 Production stock corrections will be posted.
No Inventory quantity movement will be created by PADJ.
```

Confirm button calls the existing idempotent `PostAsync()` path.

Preserve `_pending ??=` request behavior.

---

# 51. DELETE confirmation

Current NEW draft Delete should not execute from one accidental click.

Add:

```text
Delete NEW Production Stock Return?
```

or PADJ equivalent.

Only NEW uses delete.

No new lifecycle rule.

---

# 52. ROLLBACK

Keep existing mandatory reason.

Do not weaken service eligibility.

Confirmation copy must continue to explain the physical/accounting consequence.

---

# 53. View mode

Do not render a wall of disabled editors.

Metadata:

```text
Work Order
Product
Current WO status
Document Date
Reference
Status
Revision
Posting ID
Reversal ID
```

Then historical line grid.

## PRTN history

```text
Item
Production Lot
Returned Qty / Source UOM
Inventory Destination
Reason
Historical Value (ViewCost only)
```

## PADJ history

```text
Item / Pool / Lot
Signed Adjustment Qty / UOM
Reason
Cost Basis (when available/authorized)
Historical Value (ViewCost only)
```

Do not show current live pool qty as if it were a posting fact.

---

# 54. Source popup ADD behavior

PRTN:

```text
ADD
    creates ReturnEditor
    ReturnQty = 0
    Reason = existing default EXCESS
```

PADJ:

```text
ADD
    creates AdjustmentEditor
    Direction = ADD
    InputQty = 0
    Reason = existing default COUNT
```

After ADD:

```text
row action becomes ADDED
popup stays open so multiple sources can be added
```

Footer:

```text
DONE
```

The operator can add multiple sources without reopening the popup repeatedly.

---

# 55. Initial source popup behavior

Auto-open source picker only when:

```text
Id == 0
WO was just selected
Editors.Count == 0
```

Do not auto-open when:

- reopening a saved draft;
- opening View;
- returning from POST;
- creating component render after every state change.

---

# 56. Search cancellation lifecycle

Add to each entry code-behind:

```text
CancellationTokenSource? _sourceSearchCts
long _sourceSearchVersion
```

Before new search/page:

```text
cancel/dispose previous CTS
new CTS
increment version
```

Before applying returned rows:

```text
if cancelled or stale version => ignore
```

On component dispose:

```text
cancel/dispose CTS
```

No background orphan query should update a disposed Blazor circuit.

---

# 57. Responsive CSS

Desktop:

```text
max-width 1480px
metadata 4 columns
command bar two sides
```

<= 900px:

```text
metadata 2 columns
command buttons wrap
```

<= 700px:

```text
metadata 1 column
command bar stacks
popup within viewport
grids scroll horizontally
```

Do not shrink text below readable size.

---

# 58. List row actions

Both list pages need:

```text
CanEdit
```

loaded from access rights.

Row actions:

```text
VIEW
EDIT
```

EDIT:

```text
permission required
row status must be NEW
```

If status changed after list load:

```text
service/edit page fails safe
or route redirects to View with message
```

Do not rely on button state as security.

---

# 59. No current-eligibility filter on historical list

This is an explicit rejection rule.

Do not build:

```text
List FILTER -> "eligible WOs"
```

because it would exclude completed historical WO identities.

If the list needs a WO filter later:

```text
query distinct WorkOrderId/No from existing PRTN/PADJ documents
```

not from current WO eligibility.

For this fix, Status FILTER + free-text WO search is sufficient.

---

# 60. Core lookup tests — use existing test stack

Do not introduce bUnit/Playwright packages for this task.

`ErpWeb.Tests` currently has:

```text
xUnit
Moq
EF Core SQLite
```

and explicitly notes that a green test suite does not prove Razor rendering.

Add normal Core tests for the new lookup methods.

---

# 61. PRTN eligible-WO lookup tests

Test:

```text
branch/company isolation
Released included
InProgress included
Draft excluded
Completed excluded
Closed/Cancelled excluded
legacy snapshot excluded
invalid/empty snapshot excluded
WO without MATERIAL_IN candidate excluded
WO with structural candidate included
search by WO
search by Product
search by Description
blank search bounded
paging stable
exact resolve works
ineligible exact resolve fails
```

No posting expectations change.

---

# 62. PADJ eligible-WO lookup tests

Test:

```text
same scope/status/snapshot rules
MATERIAL_IN candidate qualifies
WIP candidate qualifies
zero-balance existing WIP may qualify
no candidate excludes WO
search/paging/resolve
```

---

# 63. DTO/read-model regression tests

Add targeted tests where practical:

## PRTN NEW mapping

Verify:

```text
RequestedQty remains saved requested qty
CurrentReturnableQty is separate current value
```

Never set current returnable from saved line base qty.

## PADJ NEW mapping

Verify:

```text
CurrentQty = current pool Qty
CurrentBaseQty = current pool BaseQty
line BaseQty remains saved adjustment base qty
```

---

# 64. List summary tests

PRTN:

```text
QtySummary uses RequestedQty + SourceUom
```

PADJ:

```text
QtySummary uses signed AdjustmentQty + SourceUom
not ItemCode
```

Numeric RefNo remains searchable.

---

# 65. UI build gate

After Razor/CSS changes:

```bash
dotnet build ErpWeb.slnx
```

is mandatory.

Do not accept:

```text
dotnet test passes
```

as proof that Razor compiles.

---

# 66. Existing backend regression gate

Run the existing relevant tests.

At minimum:

```text
ProductionStockReturnService tests
ProductionStockAdjustmentService tests
ProductionStockAdjustmentMath tests
Production material reconciliation tests
Production pool valuation tests
StockPosting / valuation tests touched by current solution
```

No existing expected posted qty/value should need to change.

If a UI change requires changing an expected posting value:

```text
STOP — scope violation.
```

---

# 67. Mandatory browser acceptance — PRTN

1. Open PRTN list.
2. Verify one NEW action only.
3. Click NEW with no row selected.
4. New page opens.
5. WO field is a smart searchable lookup.
6. Search icon opens blank first page.
7. Search by product description works.
8. Select WO.
9. Source picker opens automatically.
10. Candidate sources page at 20 rows.
11. Search item/lot with debounce.
12. Add a source.
13. Return Qty starts at 0.
14. Source row changes to ADDED.
15. Add second source if desired.
16. DONE closes picker.
17. Workspace shows compact WO context.
18. Enter Return Qty.
19. Projected after-return qty updates.
20. Exact Inventory destination is visible/read-only.
21. OTHER requires remark.
22. Save Draft.
23. Reopen Edit.
24. Current Returnable is current contribution, not merely prior Requested Qty.
25. POST opens confirmation.
26. POST succeeds through unchanged service.
27. View is factual/read-only.

---

# 68. Mandatory browser acceptance — PADJ REDUCE

1. NEW works with no selection.
2. Select WO by popup.
3. Source picker auto-opens.
4. Add pool.
5. DONE.
6. Choose REDUCE.
7. Enter positive 3.
8. UI displays projected:
   `10.0000 -> 7.0000`.
9. User never types `-3`.
10. Attempt quantity 11.
11. UI blocks Save and states projected qty is below zero.
12. Set valid 3.
13. Save.
14. Reopen Edit.
15. Direction still REDUCE; Qty still 3.
16. POST confirmation explains Production-only correction.
17. POST service receives `AdjustmentQty = -3`.

---

# 69. Mandatory browser acceptance — PADJ ADD non-empty

1. Add non-empty Production pool.
2. Direction ADD.
3. Enter Qty.
4. Projected New Qty correct.
5. Cost status:
   `System — current pool cost`.
6. No manual-cost fields shown.
7. Save/Post uses unchanged backend cost logic.

---

# 70. Mandatory browser acceptance — PADJ empty WIP

1. Add empty WIP pool.
2. Direction ADD.
3. Cost text says:
   `Automatic Standard Cost if configured`.
4. It does **not** falsely say manual approval is always mandatory.
5. User with PriceOverride sees SET COST OVERRIDE.
6. User without PriceOverride does not.
7. If override chosen:
   - Manual > 0 works;
   - Zero requires exact zero;
   - approval reason required.
8. Switch direction to REDUCE.
9. Override fields are cleared/not submitted.
10. Backend remains final costing authority.

---

# 71. Mandatory browser acceptance — permissions

Test representative user rights:

```text
PRTN access + no Work Order maintenance access
PADJ access + no Work Order maintenance access
```

Both must still be able to use their transaction WO picker.

This is a required acceptance condition.

---

# 72. Mandatory browser acceptance — historical list

Use a transaction whose WO is now Completed/Closed.

Verify:

```text
document remains visible
free-text WO search finds it
status filter does not exclude it incorrectly
view opens
```

Creation picker should correctly exclude that WO.

Creation eligibility and history visibility must remain separate.

---

# 73. Mandatory browser acceptance — responsive/theme

Check:

```text
desktop
~900px
~700px/mobile
light theme
dark theme
```

Reject if:

- short inputs stretch absurdly;
- command bar overlaps;
- popup exceeds viewport;
- grids become impossible to scroll;
- contrast depends on hardcoded white backgrounds.

---

# 74. Implementation file map

## PRTN list

```text
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor.cs
```

## PRTN entry

```text
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor.css
```

## PADJ list

```text
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentList.razor
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentList.razor.cs
```

## PADJ entry

```text
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor.css
```

## Shared Production stock WO lookup

```text
ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderPicker.razor
ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderPicker.razor.cs

ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderSearchPopup.razor
ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderSearchPopup.razor.cs
```

## Contracts/Core read paths

```text
ErpWeb.Core/Production/IProductionStockReturnService.cs
ErpWeb.Core/Production/ProductionStockReturnService.cs
ErpWeb.Core/Production/ProductionStockReturnService.Lifecycle.cs
    only if DTO mapping/lifecycle file location requires it; do not touch rollback business logic

ErpWeb.Core/Production/IProductionStockAdjustmentService.cs
ErpWeb.Core/Production/ProductionStockAdjustmentService.cs
```

No schema file should be required.

---

# 75. Recommended implementation order

## Phase 1 — stop current broken behavior

- fix NEW dispatch;
- remove duplicate NEW;
- add `CanEdit`;
- add explicit default action rejection;
- correct PRTN/PADJ list qty summaries;
- fix numeric search consistency.

Build.

## Phase 2 — Core eligible WO lookup

- add shared row DTO;
- add PRTN search/resolve;
- add PADJ search/resolve;
- add Core tests.

Test.

## Phase 3 — shared smart picker

- SmartLookupInput wrapper;
- server-paged popup;
- delegate-backed PRTN/PADJ search;
- no WorkOrder-service injection.

Build.

## Phase 4 — correct UI read models

- document Product/Description/WO status;
- PRTN `CurrentReturnableQty` + readiness;
- PADJ `CurrentBaseQty`;
- update saved-draft editor reconstruction.

Test/build.

## Phase 5 — source picker workflow

- remove permanent source grid;
- popup;
- 20-row paging;
- debounced search cancellation;
- 100-line gate;
- auto-open only first new selection.

Build/browser test.

## Phase 6 — compact layout/CSS

- owned `pr-stock-*`;
- semantic widths;
- command bar;
- metadata;
- responsive.

Browser test.

## Phase 7 — PRTN line UX

- qty defaults zero;
- current returnable;
- projected after;
- destination;
- UI validations.

Browser test.

## Phase 8 — PADJ line UX

- ADD/REDUCE;
- absolute input qty;
- projected new qty;
- cost status;
- optional override popup;
- correct saved-edit mapping.

Browser test.

## Phase 9 — lifecycle confirmations/view

- POST confirm;
- DELETE confirm;
- readiness panel;
- factual View mode.

## Phase 10 — final gates

- `dotnet build ErpWeb.slnx`;
- Core/relevant tests;
- mandatory browser scenarios;
- diff review confirms no costing/posting authority changed.

---

# 76. Explicit rejection rules

Reject the implementation if any is true:

1. Two NEW buttons remain.
2. NEW still requires row selection.
3. Unknown list toolbar actions can reach POST/ROLLBACK.
4. Entry still asks user to remember/type exact WO as the only selection mechanism.
5. A `LOAD WORK ORDER` button remains in the normal flow.
6. A `FIND POOLS` button remains immediately after WO selection.
7. PRTN/PADJ picker relies on Work Order maintenance permission.
8. Entry picker preloads all WOs.
9. Source picker preloads all pools.
10. Source paging is silently capped at 100.
11. List history filter uses creation-eligible WOs.
12. Historical Completed/Closed WO transactions become hard to find.
13. PRTN list displays Inventory base qty as operator Return Qty.
14. PADJ list displays Item Code where UOM should be.
15. PRTN saved edit treats prior requested qty as current returnable qty.
16. PADJ saved edit treats line BaseQty as current pool BaseQty.
17. PADJ empty WIP automatically displays `Approval required` merely from `RequiresCostApproval`.
18. Manual cost controls remain permanently visible for all PADJ rows.
19. PADJ normal user still has to type signed quantities.
20. PADJ REDUCE can visually project below zero without warning.
21. PRTN ADD defaults to maximum return quantity.
22. Permanent source-search grid remains stacked above the normal active workspace.
23. Component relies on another page's isolated `pr-dp-*` CSS.
24. Effective date/reference/qty controls stretch across unreasonable widths.
25. POST entry action has no confirmation.
26. DELETE NEW draft has no confirmation.
27. A new UI framework/test framework is added merely for this fix.
28. SQL schema migration is added without a separate justified requirement.
29. Production posting/costing/valuation algorithms are changed.
30. Existing expected posted quantities or values are changed to satisfy the UI.

---

# 77. Final Definition of Done

## PRTN

An ordinary user can:

```text
click NEW
search WO without memorizing code
select it
see candidate raw material immediately
add source
enter deliberate Return Qty
see current returnable
see projected Production balance
see exact fixed Inventory destination
save
post with confirmation
review historical facts
```

## PADJ

An ordinary user can:

```text
click NEW
search WO
select pool
choose ADD or REDUCE
enter a positive quantity
see projected new balance
understand cost status without seeing technical fields
enter override only when appropriate
save
post with confirmation
review historical facts
```

## Engineering

```text
No WorkOrder-maintenance permission dependency for transaction lookup.
No unbounded lookup.
No CSS-isolation dependency.
No misleading current-vs-saved quantity.
No misleading cost approval state.
No new schema.
No posting/costing authority change.
Solution builds.
Existing business tests remain green.
Manual browser gates pass.
```

---

# 78. Final approval

**APPROVED FOR IMPLEMENTATION — 10/10 AFTER DEEP RED-TEAM REVIEW**

This v2 plan **supersedes** the previous:

`productionv2_PRTN_PADJ_UI_FIX_APPROVED_10of10_AI_AGENT_PLAN.md`

The highest-risk corrections introduced by this final review are:

```text
permission-safe WO lookup
creation lookup != historical list filter
current PRTN returnable != saved requested qty
current PADJ pool BaseQty != adjustment line BaseQty
empty WIP != automatically manual-cost-required
Production list qty displayed in the correct transaction UOM
source picker popup instead of permanent clutter
repo-realistic test/build gates
```

The AI coding agent should implement this document as the authoritative UI fix plan.
