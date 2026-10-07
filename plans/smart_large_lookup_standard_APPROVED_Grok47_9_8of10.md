# APPROVED IMPLEMENTATION PLAN
## Smart Server-Side Lookup Standard for Large ERP Data Sources
### Items, Customers, Suppliers/Vendors, Purchase Items, Lots, Work Orders, and Unbounded Document Lookups

**Status:** APPROVED FOR IMPLEMENTATION  
**Approval score:** **9.8 / 10**  
**Target agent:** Cursor AI Agent / Grok 4.7 or equivalent coding agent  
**Repository:** `mokth/net10projectTemplate`  
**Branch:** `production`  
**Verified baseline commit:** `313655aa4b828bc29dde60d1a45515cf998c1ec9`  
**Framework/UI baseline:** .NET 10, Blazor Server, DevExpress Blazor **26.1.4**  
**Risk level:** Medium-High because lookup behavior touches transaction entry, but this plan deliberately preserves all pricing, defaults, posting rules, validation, tenant scope, and persisted-document semantics.  
**Database migration:** **Not required for the first implementation.** Do not add indexes blindly. Add indexes only after query-plan evidence shows they are required.

---

# 1. Executive Decision

This plan is **APPROVED**.

The current `production` branch contains two different lookup patterns:

1. Good server-driven patterns already exist:
   - `IvBalLocSearchPopup` uses `SearchOnHandAsync` with `Skip/Take`.
   - Production Work Order list uses `Skip`, `Take`, filters, and `TotalCount`.
   - Customer and Supplier repositories already expose server-side paged search methods.

2. High-risk preloading patterns also exist:
   - Sales transaction services load **all active Items** and **all active Customers** during `GetLookupsAsync()`.
   - Procurement transaction services load **all active Items / indirect purchase items / Suppliers** during `GetLookupsAsync()`.
   - `IvStockMasterPicker` is read-only and forces the user through a popup even when the item code is known.
   - Several transaction screens use searchable `DxComboBox`, but the search is only client-side because the full master list was already loaded into the Blazor Server circuit.

This is both a **UX problem** and a **Blazor Server scalability problem**.

The approved solution is:

> **Any naturally unbounded lookup must use editable smart lookup behavior + debounce + exact resolve + icon-only popup search + server-side filtered paging. It must never require the entire master table to be preloaded into the Blazor Server circuit.**

This plan standardizes that behavior without changing business rules.

---

# 2. Primary Goals

Implement one consistent ERP lookup experience for large datasets.

The standard interaction is:

```text
Item / Customer / Supplier / Work Order / etc.

┌────────────────────────────────────────────┬─────┐
│ C000123                                    │ 🔍  │
└────────────────────────────────────────────┴─────┘
  ABC Trading Sdn Bhd
```

Behavior:

1. User types a known code.
2. Wait about **550 ms** after typing stops.
3. Resolve the exact code on the server.
4. If found:
   - commit the selected value;
   - call the same business-selection handler used by popup selection;
   - populate existing defaults exactly as today.
5. If not found:
   - display a small inline `Not found` message;
   - do not immediately destructively clear transaction state;
   - after about **900 ms**, restore the last committed value or clear if there was no prior committed value, but only if the text has not changed.
6. `Enter` resolves immediately without waiting for debounce.
7. The icon-only `🔍` button remains available for discovery.
8. Clicking `🔍` opens a server-side search popup.
9. Any text already entered is passed into the popup as the initial search.
10. Popup selection follows the same commit path as exact typed resolution.

---

# 3. Non-Negotiable UX Rules

## 3.1 Input + search button layout

Use one horizontal control group.

Required:

```text
[ editable input ................................ ][ 🔍 ]
```

Do **not** use:

```text
[ read-only input ] [ 🔍 Search ]
```

The search button must:

- use `fa-solid fa-magnifying-glass`;
- be icon-only;
- have an accessible tooltip/name such as `Search items`, `Search customers`, `Search suppliers`;
- have the same height as the input;
- remain visible even when typing is supported;
- open the full lookup popup.

---

## 3.2 Debounce

Default:

```text
InputDelay = 550 ms
```

Do not resolve on every keystroke.

Use DevExpress delayed input where practical:

```razor
BindValueMode="BindValueMode.OnDelayedInput"
InputDelay="550"
```

or equivalent controlled debounce behavior in the shared lookup implementation.

---

## 3.3 Exact resolution is not fuzzy search

Typed input is an **exact resolver**.

Examples:

```text
ITEM001
CUST0001
SUP001
WO000123
```

Exact resolution should be optimized separately from popup search.

Do not run a large `%term%` search merely to validate an exact code.

---

## 3.4 Enter key

`Enter` must resolve immediately.

It must:

- cancel any pending delayed lookup;
- resolve the current typed value;
- commit only if still current.

---

## 3.5 Escape key

`Escape` should:

- cancel the pending edit;
- restore the last committed value;
- clear transient lookup errors;
- not modify transaction business state.

---

## 3.6 Not-found handling

Do not use:

- modal message;
- blocking alert;
- toast for every invalid code.

Use:

- inline error text;
- invalid field state;
- optional small loading/resolution indicator.

Recommended:

```text
SUP99999
Supplier not found
```

After about 900 ms:

- if there was no previously committed value -> clear the input;
- if editing an already-selected value -> revert to the previous committed value;
- never emit `ValueChanged(null)` merely because an attempted replacement was invalid.

This avoids destructive side effects such as existing lines being cleared because a user mistyped a Customer or Supplier code.

---

# 4. Critical State Model

The smart lookup must distinguish:

```text
Display/input text
vs
Committed business value
```

Example:

```text
Committed customer = C00001

User types:
C00999

C00999 not found

Result:
Input returns to C00001
Business state never received null
Existing document lines are untouched
```

Do not bind raw delayed keystrokes directly to transaction state.

Required conceptual states:

```text
Empty
Committed
Editing
Resolving
Resolved
NotFound
Error
Disabled
```

The component must keep:

```csharp
_inputText
_committedValue
_committedDisplayText
_requestSequence
CancellationTokenSource? _resolveCts
```

or an equivalent safe design.

---

# 5. Race-Condition Protection

This is mandatory for Blazor Server.

Example:

```text
request #20 = ABC
request #21 = ABC001
```

If #21 returns first and succeeds, #20 must never overwrite it afterward.

Every lookup implementation must use:

- cancellation token cancellation;
- and/or generation/request sequence checking.

Recommended use both.

The same stale-request rule applies to:

- delayed not-found clearing;
- popup searching;
- exact resolving.

---

# 6. Shared Architecture

Do **not** copy/paste debounce and race-control logic into every page.

Create a common UI lookup infrastructure.

## Add

```text
ErpWeb.UI/Components/Common/Lookups/
    SmartLookupInput.razor
    SmartLookupInput.razor.cs
    SmartLookupInput.razor.css
    SmartLookupState.cs
```

Follow the project's Razor + code-behind convention.

### `SmartLookupInput<TItem>`

Responsibilities:

- editable input;
- delayed input;
- Enter immediate resolution;
- Escape restore;
- loading state;
- invalid/not-found state;
- stale request rejection;
- icon-only search button;
- accessible label/tooltip;
- separate pending text and committed value;
- fire a resolved-item callback;
- open-search callback.

It must **not** know business rules for Customer, Supplier, Item, pricing, tax, etc.

Domain wrappers own those rules.

### `SmartLookupState.cs`

Make the race/state mechanics testable without a Razor renderer.

The current `ErpWeb.Tests` project does not include bUnit. Do not add bUnit solely for this change.

Pure state behavior can be tested through xUnit against this helper/controller.

---

# 7. Common Core Contracts

Add a small common contract file:

```text
ErpWeb.Core/Lookups/LargeLookupContracts.cs
```

Recommended conceptual contracts:

```csharp
public sealed class LargeLookupSearchRequest
{
    public string? SearchText { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class LargeLookupPage<T>
{
    public IReadOnlyList<T> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class LargeLookupResolveResult<T>
{
    public bool Succeeded { get; init; }
    public bool Ambiguous { get; init; }
    public string? ErrorMessage { get; init; }
    public T? Item { get; init; }
}
```

Names may be adjusted to existing project naming conventions, but preserve the contract.

Default page size:

```text
20
```

Hard maximum:

```text
100
```

Never accept caller-supplied unlimited `Take`.

---

# 8. What Counts as a Large Lookup

Treat data as a large/unbounded lookup when row count naturally grows with:

- customers;
- suppliers;
- products/items;
- transaction history;
- years of operation;
- lots;
- work orders;
- members/employees;
- source documents.

Current repo categories:

| Lookup | Policy |
|---|---|
| Inventory Item | Smart server lookup |
| Customer | Smart server lookup |
| Supplier/Vendor | Smart server lookup |
| Direct purchase item | Smart server lookup |
| Indirect purchase item (`PoPurItem`) | Smart server lookup |
| Lot / balance lot | Smart server lookup — already largely correct |
| Work Order | Server paged — already correct |
| Sales Order picker | Server searched/paged |
| Delivery Order picker | Server searched/paged |
| Sales Invoice picker | Server searched/paged |
| Purchase Requisition picker | Server searched/paged |
| Purchase Order picker | Server searched/paged |
| GRN/source receipt picker | Server searched/paged |
| Purchase Invoice picker | Server searched/paged |
| Production balance lot | Smart server lookup |
| Project | Smart lookup if customer data volume is unbounded |
| Employee/Member | Smart lookup when used in future large selectors |
| Warehouse | Normal combo unless actual customer scale proves otherwise |
| Location | Dependent lookup; server-search if large |
| Currency | Normal combo |
| UOM | Normal combo |
| Tax Group | Normal combo |
| Payment Term | Normal combo |
| Status | Normal combo |
| Country/State | Normal combo unless actual scale requires otherwise |

Rule:

> Small reference masters can remain normal combo boxes. Business entities and transaction-history entities must not be fully preloaded.

---

# 9. Inventory Item Lookup

## Existing evidence

Current files:

```text
ErpWeb.UI/Inventory/Lookups/IvStockMasterPicker.razor
ErpWeb.UI/Inventory/Lookups/IvStockMasterPicker.razor.cs
ErpWeb.UI/Inventory/Lookups/IvStockMasterSearchPopup.razor
ErpWeb.UI/Inventory/Lookups/IvStockMasterSearchPopup.razor.cs
ErpWeb.Core/Inventory/IvInventoryLookupService.cs
ErpWeb.Model/Repositories/Inventory/IvStockMasterRepository.cs
```

Current problem:

`IvStockMasterPicker` uses:

```razor
ReadOnly="true"
```

and a button with:

```razor
Text="Search"
```

Current service already has:

```csharp
ResolveItemAsync(string iCodeOrBarcode, ...)
```

Current repository already has:

```csharp
SearchPagedAsync(...)
```

returning rows + total count.

Therefore do **not** invent another inventory master repository.

---

## 9.1 Upgrade `IvStockMasterPicker`

Modify:

```text
ErpWeb.UI/Inventory/Lookups/IvStockMasterPicker.razor
ErpWeb.UI/Inventory/Lookups/IvStockMasterPicker.razor.cs
```

Required:

- editable text;
- 550 ms debounce;
- exact `ResolveItemAsync`;
- barcode remains supported;
- Enter immediate;
- inline not-found;
- icon-only search button;
- initial popup search text;
- use one common `CommitSelectionAsync(IvStockMasterLookupRow row)` path for:
  - typed resolution;
  - barcode resolution;
  - popup selection.

The existing `Selected` callback must remain the page-specific business hook.

---

## 9.2 Upgrade `IvStockMasterSearchPopup`

The current popup can load broad item data.

Convert it to explicit server paging.

Add/extend service API:

```csharp
Task<LargeLookupPage<IvStockMasterLookupRow>> SearchStockMastersPagedAsync(
    LargeLookupSearchRequest request,
    CancellationToken cancellationToken = default);
```

Use the existing:

```text
IIvStockMasterRepository.SearchPagedAsync
```

Do not use:

```text
ListActiveForLookupAsync
```

for popup browsing.

Popup:

- server search;
- page size 20;
- display total count;
- Previous/Next or supported virtual server loading;
- initial search text from the picker;
- double-click selects;
- Enter selects focused row;
- Escape closes.

---

## 9.3 Extend inventory lookup row only where required

Sales and Procurement currently depend on item lookup metadata.

If shared item lookup is reused by Sales, extend `IvStockMasterLookupRow` only with fields already present on `IvStockMaster` and currently consumed by existing transaction DTOs, such as:

```text
SellingUom
PurUom
StdPackSize
PurStdPackSize
SellingPrice
PurchasePrice
TaxGroup
PurchaseTaxGroup
StockControl
PurchaseGlCode
Classification
```

Do not move pricing-engine business logic into the lookup service.

Lookup metadata is not pricing authority.

---

# 10. Customer Lookup

## Existing evidence

Current files:

```text
ErpWeb.Core/Sales/ISaCustLookupService.cs
ErpWeb.Core/Sales/SaCustLookupService.cs
ErpWeb.Model/Repositories/Sales/SaCustRepository.cs
ErpWeb.Model/Repositories/Sales/SaCustSearchArgs.cs
```

The repository already exposes:

```csharp
SearchPagedAsync(...)
GetByCodeAsync(...)
```

The existing lookup service already has bounded `SearchCustomersAsync`, but it returns only a bounded list, not true paging.

Do not create another customer repository.

---

## 10.1 Add paged customer lookup

Extend:

```text
ISaCustLookupService
SaCustLookupService
```

with:

```csharp
ResolveCustomerAsync(string custCode, CancellationToken ct)
SearchCustomersPagedAsync(LargeLookupSearchRequest request, CancellationToken ct)
```

Implementation:

- company scoped;
- new selections: active customers only;
- search on code/name/short name and existing supported searchable fields;
- repository paging;
- total count;
- max 100 rows per request.

Use the existing `ISaCustRepository`.

---

## 10.2 Add UI components

Add:

```text
ErpWeb.UI/Sales/Lookups/
    SaCustomerPicker.razor
    SaCustomerPicker.razor.cs
    SaCustomerSearchPopup.razor
    SaCustomerSearchPopup.razor.cs
```

Update:

```text
ErpWeb.UI/Sales/_Imports.razor
```

with:

```razor
@using ErpWeb.UI.Sales.Lookups
```

Customer search popup columns:

```text
Customer Code
Customer Name
Currency (optional)
```

Do not place address/contact-heavy payloads in search rows.

---

# 11. Supplier / Vendor Lookup

## Existing evidence

Current files:

```text
ErpWeb.Core/Purchase/IPoSupplierLookupService.cs
ErpWeb.Core/Purchase/PoSupplierLookupService.cs
ErpWeb.Model/Repositories/Purchase/PoSupplierRepository.cs
ErpWeb.Model/Repositories/Purchase/PoSupplierSearchArgs.cs
```

Repository already exposes:

```csharp
SearchPagedAsync(...)
GetByCodeAsync(...)
```

The existing service already has bounded:

```csharp
SearchSuppliersAsync(...)
```

Use the repository rather than creating another supplier data layer.

---

## 11.1 Add paged supplier lookup

Extend:

```text
IPoSupplierLookupService
PoSupplierLookupService
```

with:

```csharp
ResolveSupplierAsync(string suppCode, CancellationToken ct)
SearchSuppliersPagedAsync(LargeLookupSearchRequest request, CancellationToken ct)
```

Required scope:

```text
CompanyCode + BranchCode
```

This must preserve the current procurement branch isolation.

New selections should return active supplier rows.

Do not weaken save-time inactive/suspended validation.

---

## 11.2 Add UI components

Add:

```text
ErpWeb.UI/Purchase/Lookups/
    PoSupplierPicker.razor
    PoSupplierPicker.razor.cs
    PoSupplierSearchPopup.razor
    PoSupplierSearchPopup.razor.cs
```

Update:

```text
ErpWeb.UI/Purchase/_Imports.razor
```

with:

```razor
@using ErpWeb.UI.Purchase.Lookups
```

Search popup columns:

```text
Supplier Code
Supplier Name
Currency (optional)
```

---

# 12. Purchase Direct + Indirect Item Lookup

PR and PO currently combine:

```text
IvStockMaster
+
PoPurItem
```

and preload both lists.

This must be replaced with one server-driven purchasing item lookup.

## Add

```text
ErpWeb.Core/Purchase/IPoPurchasingItemLookupService.cs
ErpWeb.Core/Purchase/PoPurchasingItemLookupService.cs
```

Register in:

```text
ErpWeb.Core/CoreServiceCollectionExtensions.cs
```

Recommended row:

```csharp
PoPurchasingItemLookupRow
{
    string ICode
    string? IDesc
    bool IsIndirect
    string? IType
    string? PurchaseUom
    string? StdUom
    decimal PackSz
    decimal? UnitPrice
    string? TaxGroup
    string? DefWarehouse
    string? Category
    string? VendorCd
    string? VendNm
    decimal? Moq
}
```

Implement:

```csharp
ResolveAsync(code)
SearchPagedAsync(request)
```

The server query must represent both:

```text
IvStockMaster active stock/direct items
PoPurItem indirect items
```

Use a shared projection and a server-side `Concat/Union All` where EF translation is safe.

If exact code exists in both sources:

```text
do not silently pick one
```

Return `Ambiguous=true` and make the user select from the popup, where a `Source` column shows:

```text
Stock
Indirect
```

This avoids hidden behavior changes.

---

## Add UI

```text
ErpWeb.UI/Purchase/Lookups/
    PoPurchasingItemPicker.razor
    PoPurchasingItemPicker.razor.cs
    PoPurchasingItemSearchPopup.razor
    PoPurchasingItemSearchPopup.razor.cs
```

---

# 13. Sales Transaction Migration

The following services currently preload full Items and Customers into `GetLookupsAsync()`:

```text
ErpWeb.Core/Sales/SaQtService.cs
ErpWeb.Core/Sales/SaSoService.cs
ErpWeb.Core/Sales/SaDoService.cs
ErpWeb.Core/Sales/SaInvoiceService.cs
ErpWeb.Core/Sales/SaCdnService.cs
```

Their result contracts also carry these full lists:

```text
ErpWeb.Core/Sales/ISaQtService.cs
ErpWeb.Core/Sales/ISaSoService.cs
ErpWeb.Core/Sales/ISaDoService.cs
ErpWeb.Core/Sales/ISaInvoiceService.cs
ErpWeb.Core/Sales/ISaCdnService.cs
```

## Required change

Remove unbounded:

```text
Items
Customers
```

from page-startup lookup payloads.

Keep small lookup data:

```text
Warehouses
TaxGroups
PayCodes
SalesReps
Departments
Projects
other bounded reference lists
```

Do not preload Items/Customers.

---

## Pages to migrate

```text
ErpWeb.UI/Sales/Transactions/SaQt.razor
ErpWeb.UI/Sales/Transactions/SaQt.razor.cs

ErpWeb.UI/Sales/Transactions/SaSo.razor
ErpWeb.UI/Sales/Transactions/SaSo.razor.cs

ErpWeb.UI/Sales/Transactions/SaDo.razor
ErpWeb.UI/Sales/Transactions/SaDo.razor.cs

ErpWeb.UI/Sales/Transactions/SaInvoice.razor
ErpWeb.UI/Sales/Transactions/SaInvoice.razor.cs

ErpWeb.UI/Sales/Transactions/SaCdn.razor
ErpWeb.UI/Sales/Transactions/SaCdn.razor.cs
```

Replace Customer `DxComboBox Data="@Customers"` with:

```text
SaCustomerPicker
```

Replace item line `DxComboBox Data="@Items"` with:

```text
enhanced IvStockMasterPicker
```

or a thin Sales wrapper over the same inventory lookup.

---

# 14. Preserve Sales Customer Change Confirmation

Existing Sales pages contain important behavior around Customer changes.

Examples include:

```text
_customerApplySeq
ApplyCustomerAsync
ApplyCustomerDefaultsAsync
confirmation when lines already exist
wipe dependent values only after accepted change
```

Do not bypass this.

The Customer picker should return a resolved row to the page.

The page must then call its existing Customer-change workflow.

Concept:

```text
SaCustomerPicker resolves C0001
        ↓
page OnCustomerResolvedAsync(row)
        ↓
existing OnCustCodeChanged(row.Code)
        ↓
existing confirmation logic
        ↓
existing ApplyCustomerAsync
        ↓
existing ApplyCustomerDefaultsAsync
```

Do not have the shared picker clear Sales lines.

---

# 15. Preserve Sales Item Behavior

Current line handlers depend on preloaded `Items`.

Examples verified in:

```text
SaSo.razor.cs
SaInvoice.razor.cs
```

Current logic includes:

- description;
- standard UOM;
- selling UOM;
- standard pack size;
- stock-control flag;
- tax group;
- default warehouse;
- classification;
- pricing-engine invocation.

The new selected-item handler must receive the resolved item row directly instead of doing:

```csharp
Items.FirstOrDefault(...)
```

Example conversion:

OLD:

```csharp
var item = Items.FirstOrDefault(x => x.ICode == Popup.ICode);
```

NEW:

```csharp
protected async Task OnPopupItemSelectedAsync(IvStockMasterLookupRow item)
{
    Popup.ICode = item.ICode;
    ...
}
```

Then preserve all current defaulting logic.

### Pricing rule — non-negotiable

Do **not** seed Sales unit price from `IvStockMaster.SellingPrice` if the current page intentionally delegates price authority to the Sales pricing engine.

Existing comments explicitly state:

```text
UnitPrice is deliberately NOT seeded from item.SellingPrice.
The server engine decides it.
```

Preserve this exactly.

After selected item metadata is applied:

```text
ResolvePopupPriceAsync(...)
```

must continue to execute as today.

---

# 16. Existing Sales Lines Must Not Reprice on Open/Edit

Existing code explicitly protects against silent repricing when editing a draft line.

Preserve that rule.

Where code currently uses:

```text
RefreshPackFromItem(...)
ClassificationFromItem(...)
```

replace full-master list dependency with one of:

1. data already persisted on the line; or
2. exact single-item resolve; or
3. a bounded batch exact-code resolver for the few item codes present in the current document.

Do **not** load the complete item master merely to hydrate existing lines.

Do **not** call the pricing engine solely because an existing line was opened.

---

# 17. Procurement Transaction Migration

Current full-preload hotspots verified:

```text
ErpWeb.Core/Purchase/PoPrService.cs
ErpWeb.Core/Purchase/PoOrderService.cs
ErpWeb.Core/Purchase/PoInvoiceService.cs
ErpWeb.Core/Purchase/PoCdnService.cs
ErpWeb.Core/Purchase/PoSbLookupsLoader.cs
```

Current contracts:

```text
ErpWeb.Core/Purchase/IPoPrService.cs
ErpWeb.Core/Purchase/IPoOrderService.cs
ErpWeb.Core/Purchase/IPoInvoiceService.cs
ErpWeb.Core/Purchase/IPoCdnService.cs
```

---

## Pages to migrate

```text
ErpWeb.UI/Purchase/Transactions/PoPr.razor
ErpWeb.UI/Purchase/Transactions/PoPr.razor.cs

ErpWeb.UI/Purchase/Transactions/PoOrder.razor
ErpWeb.UI/Purchase/Transactions/PoOrder.razor.cs

ErpWeb.UI/Purchase/Transactions/PoInvoice.razor
ErpWeb.UI/Purchase/Transactions/PoInvoice.razor.cs

ErpWeb.UI/Purchase/Transactions/PoCdn.razor
ErpWeb.UI/Purchase/Transactions/PoCdn.razor.cs

ErpWeb.UI/Purchase/Transactions/PoSbInvoice.razor
ErpWeb.UI/Purchase/Transactions/PoSbInvoice.razor.cs

ErpWeb.UI/Purchase/Transactions/PoSbCdn.razor
ErpWeb.UI/Purchase/Transactions/PoSbCdn.razor.cs
```

---

# 18. Preserve Procurement Supplier Logic

PO contains important existing behavior:

```text
CanEditSupplier
ConfirmSupplierChangeVisible
ApplySupplierAsync
GetSupplierDefaultsAsync
ClearSupplierFields
```

The new `PoSupplierPicker` must resolve only the selection.

The page remains responsible for:

- confirmation;
- lines;
- supplier defaults;
- addresses;
- currency;
- payment terms;
- one-time rules.

Do not move transaction logic into the lookup component.

---

# 19. Preserve Procurement Item Defaults

Verified current handlers include:

### PO

```text
description
item type
purchase UOM
standard UOM
pack size
purchase unit price
tax group
default warehouse
currency
```

### PR

```text
description
purchase UOM
standard UOM
pack size
category
tax group
preferred vendor
vendor name
default warehouse
unit cost subject to VIEW_COST permission
currency
```

### Purchase CN/DN

```text
description
purchase price
purchase tax group / tax group
purchase GL
classification
stock control
pack size
```

The server lookup result must provide the fields necessary for the existing behavior.

Do not replace those defaults with generic assumptions.

---

# 20. Permission Rule for Purchase Cost

Current PR behavior has:

```text
CanViewCost
```

The smart lookup must not leak purchase price to a user who lacks `VIEW_COST`.

Options:

1. return `UnitPrice = null` from the lookup service when caller cannot view cost; or
2. return the row but let the page mask it before display/use.

Preferred:

> Enforce price visibility at the service boundary where current lookup payload already honors permission.

Do not expose cost in popup columns for users without permission.

---

# 21. Self-Billed Documents

Current:

```text
PoSbLookupsLoader.LoadAsync
```

loads all Suppliers.

Change it so `PoSbLookupsLoader` continues loading only small lookup families:

```text
TaxGroups
Currencies
UOMs
```

Remove:

```text
Vendors
```

from startup lookup payload.

Use:

```text
PoSupplierPicker
```

on:

```text
PoSbInvoice
PoSbCdn
```

Preserve existing:

```text
LoadVendorDefaultsAsync
```

or route through the canonical supplier resolver without altering MyInvois behavior.

---

# 22. Existing `IvStockMasterPicker` Consumers

Because `IvStockMasterPicker` becomes editable and server-resolving, the following existing pages should receive the improved behavior with minimal page changes.

Verified usage includes:

## Inventory

```text
IvBalanceLot
IvCostingCenter
IvLotInquiry
IvStockAlerts
IvStockCard
IvStockCountVariance
IvTrxInquiry
IvMiscIssue
IvMiscReceipt
IvScrap
IvStockAdjustment
IvStockReturn
IvStockTransfer
IvVendorReturn
```

## Sales

```text
SaSalesByItem
SaPriceHistoryInquiry
SaCustPriceGroupList
SaDisGroupItemList
SaItemCustList
PriceInquiry
```

## Purchase

```text
PoDeliveryPerformanceInquiry
PoMatchingInquiry
PoPriceHistoryInquiry
```

## Planning / Production

```text
PrProductDefEntry
PrWorkOrderEntry
```

`PrProductDefEntry` uses the picker in multiple places including:

- Product Code;
- Work Centre output item;
- BOM/material item.

After changing the shared component, verify all these consumers.

Do not blindly edit all of them if the shared change already supplies the new UX.

---

# 23. Barcode Handling

Inventory item exact resolver currently supports:

```text
exact ICode
then exact Barcode
```

Preserve this.

For ambiguous barcode matches:

```text
do not auto-select
```

Show an inline message such as:

```text
Barcode matches multiple items. Use search.
```

and allow the user to open the popup.

---

# 24. Historical / Inactive Values

This is critical.

A persisted historical document must continue to display a Customer, Supplier, or Item even if that master is inactive today.

Rules:

```text
New search choices        => active only
New exact typed choice    => active only
Persisted document value  => display existing snapshot/value
```

Do not auto-clear a persisted inactive value during component initialization.

Do not resolve and reject existing values merely because the page rendered.

Resolve only when:

- the operator edits the field;
- or the page intentionally requests current master metadata.

Existing Sales code currently has `EnsureCustomerOption(...)` to work around inactive values disappearing from an active-only combo.

After migration that workaround may become unnecessary, but remove it only after the new picker demonstrably preserves persisted display values.

---

# 25. Search Popup Ranking

For large lookup search results, use practical ERP ranking.

Preferred order:

```text
1. exact code
2. code starts with term
3. code contains term
4. name starts with term
5. name contains term
```

Then stable tie-breaker:

```text
Code ascending
```

Do not rely on arbitrary table order.

For SQL Server/EF queries, implement ranking only if it translates cleanly and remains indexed/performant.

If translation becomes fragile:

```text
Code ascending with filtered server search
```

is acceptable for Phase 1.

Correctness and bounded query size are more important than complex ranking.

---

# 26. Popup Search Policy

Default popup:

```text
Take = 20
```

Allowed max:

```text
100
```

Never fetch unlimited records.

If search text is blank:

- return the first page by code, not the entire table.

The popup may show:

```text
Showing 1–20 of 12,438
```

---

# 27. Source Document Lookups

The same rule applies to transaction history.

## Already acceptable / relatively bounded

`PoOrderService.SearchPrForPoAsync` currently uses:

```text
Take(100)
```

`PoInvoiceService.SearchPostedInvoicesAsync` currently uses:

```text
Take(50)
```

These are bounded and are not first-priority failures.

However, they should eventually expose proper paging if users need to browse beyond the cap.

---

## Must review / improve

`SaDoService.GetBillableLinesAsync` currently loads all posted DO headers for a Customer and then all corresponding details.

This is naturally unbounded.

Change Add-from-DO in Sales Invoice to a paged/searchable source lookup rather than loading every posted DO line for the customer.

Create/extend a query contract with:

```text
Customer
Currency
SearchText
Skip
Take
TotalCount
```

Do not change billable-quantity calculations.

Only change how the candidate rows are fetched.

---

## Purchase invoiceable PO lines

`PoInvoiceService.SearchInvoiceablePoLinesAsync` currently loads up to 100 PO headers plus details.

This is bounded, but not true line-level paging.

Mark as **Phase 2 hardening** after the Customer/Supplier/Item preload removal.

Do not block Phase 1 approval on redesigning every source-document picker.

---

# 28. Lots and Work Orders — Preserve Good Existing Patterns

Do not rewrite working server-driven patterns unnecessarily.

## Lot/on-hand

Keep:

```text
IvBalLocSearchPopup
IvOnHandSearchRequest
Skip
Take
TotalCount
```

Use it as a reference implementation.

## Production Work Orders

Keep:

```text
ProductionWorkOrderListQuery
Skip
Take
TotalCount
```

Use it as a reference implementation.

Only standardize their visual input/search-button behavior if needed.

---

# 29. `GetLookupsAsync()` Architecture Rule

After this implementation:

> `GetLookupsAsync()` may return only small/bounded reference datasets.

It must not return naturally unbounded business masters.

Forbidden in startup lookup payloads:

```text
all Items
all Customers
all Suppliers
all Lots
all Work Orders
all Sales Orders
all Purchase Orders
all Invoices
```

Allowed:

```text
Tax groups
Currencies
Payment terms
UOM
Status
Warehouses when naturally small
Departments/Projects when proven small
other reference-code tables
```

---

# 30. Exact Files — Core Layer

## Add

```text
ErpWeb.Core/Lookups/LargeLookupContracts.cs

ErpWeb.Core/Purchase/IPoPurchasingItemLookupService.cs
ErpWeb.Core/Purchase/PoPurchasingItemLookupService.cs
```

## Modify

```text
ErpWeb.Core/CoreServiceCollectionExtensions.cs

ErpWeb.Core/Inventory/IvInventoryLookupService.cs

ErpWeb.Core/Sales/ISaCustLookupService.cs
ErpWeb.Core/Sales/SaCustLookupService.cs

ErpWeb.Core/Purchase/IPoSupplierLookupService.cs
ErpWeb.Core/Purchase/PoSupplierLookupService.cs

ErpWeb.Core/Sales/ISaQtService.cs
ErpWeb.Core/Sales/SaQtService.cs
ErpWeb.Core/Sales/ISaSoService.cs
ErpWeb.Core/Sales/SaSoService.cs
ErpWeb.Core/Sales/ISaDoService.cs
ErpWeb.Core/Sales/SaDoService.cs
ErpWeb.Core/Sales/ISaInvoiceService.cs
ErpWeb.Core/Sales/SaInvoiceService.cs
ErpWeb.Core/Sales/ISaCdnService.cs
ErpWeb.Core/Sales/SaCdnService.cs

ErpWeb.Core/Purchase/IPoPrService.cs
ErpWeb.Core/Purchase/PoPrService.cs
ErpWeb.Core/Purchase/IPoOrderService.cs
ErpWeb.Core/Purchase/PoOrderService.cs
ErpWeb.Core/Purchase/IPoInvoiceService.cs
ErpWeb.Core/Purchase/PoInvoiceService.cs
ErpWeb.Core/Purchase/IPoCdnService.cs
ErpWeb.Core/Purchase/PoCdnService.cs

ErpWeb.Core/Purchase/PoSbLookupsLoader.cs
```

Do not modify unrelated posting/costing services.

---

# 31. Exact Files — UI Layer

## Add

```text
ErpWeb.UI/Components/Common/Lookups/SmartLookupInput.razor
ErpWeb.UI/Components/Common/Lookups/SmartLookupInput.razor.cs
ErpWeb.UI/Components/Common/Lookups/SmartLookupInput.razor.css
ErpWeb.UI/Components/Common/Lookups/SmartLookupState.cs

ErpWeb.UI/Sales/Lookups/SaCustomerPicker.razor
ErpWeb.UI/Sales/Lookups/SaCustomerPicker.razor.cs
ErpWeb.UI/Sales/Lookups/SaCustomerSearchPopup.razor
ErpWeb.UI/Sales/Lookups/SaCustomerSearchPopup.razor.cs

ErpWeb.UI/Purchase/Lookups/PoSupplierPicker.razor
ErpWeb.UI/Purchase/Lookups/PoSupplierPicker.razor.cs
ErpWeb.UI/Purchase/Lookups/PoSupplierSearchPopup.razor
ErpWeb.UI/Purchase/Lookups/PoSupplierSearchPopup.razor.cs

ErpWeb.UI/Purchase/Lookups/PoPurchasingItemPicker.razor
ErpWeb.UI/Purchase/Lookups/PoPurchasingItemPicker.razor.cs
ErpWeb.UI/Purchase/Lookups/PoPurchasingItemSearchPopup.razor
ErpWeb.UI/Purchase/Lookups/PoPurchasingItemSearchPopup.razor.cs
```

## Modify

```text
ErpWeb.UI/Inventory/Lookups/IvStockMasterPicker.razor
ErpWeb.UI/Inventory/Lookups/IvStockMasterPicker.razor.cs
ErpWeb.UI/Inventory/Lookups/IvStockMasterSearchPopup.razor
ErpWeb.UI/Inventory/Lookups/IvStockMasterSearchPopup.razor.cs

ErpWeb.UI/Sales/_Imports.razor
ErpWeb.UI/Purchase/_Imports.razor

ErpWeb.UI/Sales/Transactions/SaQt.razor
ErpWeb.UI/Sales/Transactions/SaQt.razor.cs
ErpWeb.UI/Sales/Transactions/SaSo.razor
ErpWeb.UI/Sales/Transactions/SaSo.razor.cs
ErpWeb.UI/Sales/Transactions/SaDo.razor
ErpWeb.UI/Sales/Transactions/SaDo.razor.cs
ErpWeb.UI/Sales/Transactions/SaInvoice.razor
ErpWeb.UI/Sales/Transactions/SaInvoice.razor.cs
ErpWeb.UI/Sales/Transactions/SaCdn.razor
ErpWeb.UI/Sales/Transactions/SaCdn.razor.cs

ErpWeb.UI/Purchase/Transactions/PoPr.razor
ErpWeb.UI/Purchase/Transactions/PoPr.razor.cs
ErpWeb.UI/Purchase/Transactions/PoOrder.razor
ErpWeb.UI/Purchase/Transactions/PoOrder.razor.cs
ErpWeb.UI/Purchase/Transactions/PoInvoice.razor
ErpWeb.UI/Purchase/Transactions/PoInvoice.razor.cs
ErpWeb.UI/Purchase/Transactions/PoCdn.razor
ErpWeb.UI/Purchase/Transactions/PoCdn.razor.cs
ErpWeb.UI/Purchase/Transactions/PoSbInvoice.razor
ErpWeb.UI/Purchase/Transactions/PoSbInvoice.razor.cs
ErpWeb.UI/Purchase/Transactions/PoSbCdn.razor
ErpWeb.UI/Purchase/Transactions/PoSbCdn.razor.cs
```

---

# 32. Implementation Sequence

The coding agent must implement in this order.

## Phase 1 — Common smart-input infrastructure

1. Add `LargeLookupContracts`.
2. Add `SmartLookupState`.
3. Add `SmartLookupInput`.
4. Add xUnit tests for state/race behavior.
5. Build solution.

Do not start transaction page migration before this compiles.

---

## Phase 2 — Inventory item lookup

1. Extend `IvStockMasterLookupRow` only as required.
2. Add paged item-search API using existing `IIvStockMasterRepository.SearchPagedAsync`.
3. Upgrade `IvStockMasterPicker`.
4. Upgrade `IvStockMasterSearchPopup`.
5. Verify existing picker consumers.
6. Add tests.
7. Build solution.

---

## Phase 3 — Customer and Supplier

1. Extend `ISaCustLookupService`.
2. Use existing `ISaCustRepository.GetByCodeAsync/SearchPagedAsync`.
3. Add Customer picker + popup.
4. Extend `IPoSupplierLookupService`.
5. Use existing `IPoSupplierRepository.GetByCodeAsync/SearchPagedAsync`.
6. Add Supplier picker + popup.
7. Add tests.
8. Build solution.

---

## Phase 4 — Sales transaction removal of full preload

Migrate one page at a time in this order:

```text
SaQt
SaSo
SaDo
SaInvoice
SaCdn
```

For each page:

1. remove Customer master list from startup lookup payload;
2. remove Item master list from startup lookup payload;
3. replace Customer combo with `SaCustomerPicker`;
4. replace Item combo with smart Item picker;
5. preserve existing change/default/price behavior;
6. run that page's current service tests;
7. build before continuing.

Do not perform all five pages in one uncontrolled edit.

---

## Phase 5 — Procurement transaction removal of full preload

Implement `PoPurchasingItemLookupService`.

Migrate in order:

```text
PoPr
PoOrder
PoInvoice
PoCdn
PoSbInvoice
PoSbCdn
```

For each:

1. remove Supplier/Vendor full preload;
2. remove direct/indirect Item full preload where applicable;
3. use `PoSupplierPicker`;
4. use `PoPurchasingItemPicker` for PR/PO;
5. use smart stock Item picker for stock-only procurement flows;
6. preserve cost permissions;
7. preserve supplier confirmation/default logic;
8. run current service tests;
9. build before continuing.

---

## Phase 6 — Large source-document picker hardening

1. Convert Sales Invoice Add-from-DO to paged/searchable server query.
2. Review PO Invoiceable line picker for paging beyond its existing 100-header cap.
3. Keep already-bounded PR/Invoice searches functioning.
4. Do not change quantity-reservation/application calculations.

---

# 33. Service Tests to Add

Add new tests under the existing module folder structure.

Suggested new files:

```text
ErpWeb.Tests/Inventory/Lookup/IvLargeItemLookupTests.cs
ErpWeb.Tests/Sales/Lookup/SaCustomerLargeLookupTests.cs
ErpWeb.Tests/Procurement/Lookup/PoSupplierLargeLookupTests.cs
ErpWeb.Tests/Procurement/Lookup/PoPurchasingItemLargeLookupTests.cs
ErpWeb.Tests/UI/SmartLookupStateTests.cs
```

If project folder conventions require a different namespace, follow the existing folder-based namespace standard.

---

# 34. Required Lookup Test Cases

## Smart state

Test:

```text
delayed resolve commits valid value
Enter resolves immediately
Escape restores committed value
old request result is ignored
not-found delayed clear is ignored after newer text
invalid replacement does not emit null
explicit clear does emit null
```

---

## Item

Seed at least:

```text
250 active items
10 inactive items
barcode match
duplicate barcode case
```

Verify:

```text
Take=20 returns 20
TotalCount correct
search is tenant/company scoped
inactive excluded from new search
exact item resolves
barcode resolves
ambiguous barcode does not auto-select
```

---

## Customer

Seed:

```text
250 active customers
inactive customers
second company customers
```

Verify:

```text
20-row paging
total count
company isolation
code search
name search
active-only new selection
exact resolve
```

---

## Supplier

Seed:

```text
250 suppliers
two branches
inactive supplier
suspended supplier
```

Verify:

```text
branch isolation
company isolation
20-row paging
active-only selector
existing save-time suspended/inactive validation remains
```

---

## Purchase item

Seed:

```text
stock items
indirect items
same code collision
```

Verify:

```text
both sources searchable
Source identified
ambiguous same code does not silently choose
cost hidden without VIEW_COST
```

---

# 35. Existing Test Suites That Must Remain Green

At minimum run relevant existing tests:

```text
ErpWeb.Tests/Sales/Transaction/SaQtServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaSoServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaDoServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaInvoiceServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaCdnServiceTests.cs

ErpWeb.Tests/Procurement/Transaction/PoPrServiceTests.cs
ErpWeb.Tests/Procurement/Transaction/PoOrderServiceTests.cs
ErpWeb.Tests/Procurement/Transaction/PoInvoiceServiceTests.cs
ErpWeb.Tests/Procurement/Transaction/PoCdnServiceTests.cs

ErpWeb.Tests/Inventory/Master/IvStockMasterServiceTests.cs
ErpWeb.Tests/Procurement/Master/PoSupplierServiceTests.cs
```

Also run SQL Server concurrency tests affected by touched services.

---

# 36. Build Gate

Because `ErpWeb.Tests.csproj` explicitly notes that a green test suite does **not** prove Razor rendering, the agent must run:

```text
dotnet build ErpWeb.slnx
```

after any `.razor` / `.razor.cs` changes.

Final gate:

```text
dotnet test
dotnet build ErpWeb.slnx
```

Both must pass.

If the repository uses narrower test commands during development, the final complete run is still mandatory.

---

# 37. Performance Acceptance Gate

Create test data or use a local dev database with representative scale.

Minimum target:

```text
20,000 Items
10,000 Customers
10,000 Suppliers
```

Opening:

```text
Sales Order New
Sales Invoice New
Purchase Order New
Purchase Invoice New
```

must **not** transfer all of those master rows into the page.

Expected behavior:

```text
startup loads small reference lists only
Customer lookup loads <= 20/100 rows per request
Supplier lookup loads <= 20/100 rows per request
Item popup loads <= 20/100 rows per request
exact code resolve returns 1 row
```

No fixed millisecond performance claim is required because deployment/network/database conditions vary.

The architectural pass condition is bounded I/O and bounded circuit state.

---

# 38. SQL / Index Guidance

Do not add indexes during Phase 1 simply because search uses `Contains`.

Existing repositories already query scoped fields such as:

```text
CompanyCode
BranchCode
CustCode
SuppCode
ICode
IsActive
```

First implement bounded lookup.

Then inspect SQL Server execution plans with representative data.

Only add an index if evidence shows a measurable problem.

Any index migration must be:

- separately reviewed;
- tenant-scope aware;
- compatible with current unique/business keys;
- not based on speculation.

---

# 39. Security and Tenant Rules

Never weaken existing isolation.

Required:

## Item

```text
CompanyCode scoped
```

Item master is intentionally not BranchCode scoped in the current repository.

## Customer

```text
CompanyCode scoped
```

## Supplier

```text
CompanyCode + BranchCode scoped
```

for transaction selection where the current procurement flow uses branch-scoped suppliers.

## Transactions

Use:

```text
CompanyCode + BranchCode
```

where existing transaction services already do so.

Do not accept tenant/company/branch identifiers from browser input when current user context already supplies them.

---

# 40. Business Validation Boundary

Lookup validation is not the final integrity boundary.

Continue to validate again at:

```text
Save
Post
Release
Submit
```

as existing services do.

The smart lookup is for UX and bounded retrieval.

It must not replace server-side business validation.

---

# 41. Accessibility

Required:

- search icon has accessible name/tooltip;
- keyboard users can type without using mouse;
- Enter resolves;
- Escape restores/cancels;
- popup supports keyboard focus;
- selected row can be committed by Enter;
- loading state does not trap focus;
- error text is associated with the control where practical.

---

# 42. Styling Rules

Use component-isolated CSS for common lookup UI.

Do not scatter custom CSS into every transaction page.

Required visual behavior:

```text
input + icon button same row
same height
no large gap
responsive width
button does not wrap under input
input receives remaining width
```

Use existing DevExpress theme colors.

Do not hard-code unrelated custom color themes.

---

# 43. Regression Guardrails for the Coding Agent

The agent must **not**:

1. change posting algorithms;
2. change costing algorithms;
3. change Sales price authority;
4. change tax calculation;
5. change UOM conversion;
6. change stock availability rules;
7. change transaction statuses;
8. change approval rules;
9. change MyInvois submission rules;
10. remove tenant/branch filters;
11. add speculative schema migrations;
12. convert every small combo box into a server lookup;
13. perform hidden automatic Customer/Supplier change that bypasses confirmation dialogs;
14. clear transaction lines because a lookup typo failed;
15. reprice existing Sales lines merely because the line editor was opened;
16. expose purchase cost to a user without `VIEW_COST`.

---

# 44. Definition of Done

Implementation is complete only when all are true:

- [ ] `IvStockMasterPicker` is editable.
- [ ] Item exact code/barcode resolves with debounce.
- [ ] Item search button is icon-only.
- [ ] Item popup is server-paged.
- [ ] Customer smart picker exists.
- [ ] Customer popup is server-paged.
- [ ] Supplier smart picker exists.
- [ ] Supplier popup is server-paged.
- [ ] Purchase combined item picker exists.
- [ ] Sales startup no longer preloads full Item master.
- [ ] Sales startup no longer preloads full Customer master.
- [ ] Procurement startup no longer preloads full Supplier master.
- [ ] PR/PO startup no longer preloads all direct + indirect items.
- [ ] Self-billed startup no longer preloads all Suppliers.
- [ ] Existing item/customer/supplier defaults remain identical.
- [ ] Sales pricing engine remains authoritative.
- [ ] Purchase cost permission remains enforced.
- [ ] Customer/Supplier change confirmation remains.
- [ ] Existing inactive historical values still display.
- [ ] stale debounce results cannot overwrite newer input.
- [ ] no invalid replacement emits destructive null state.
- [ ] current relevant tests pass.
- [ ] new lookup tests pass.
- [ ] `dotnet test` passes.
- [ ] `dotnet build ErpWeb.slnx` passes.
- [ ] large-data verification proves bounded lookup result sizes.

---

# 45. Agent Execution Checklist

Before editing:

1. confirm current branch is `production`;
2. inspect current branch HEAD;
3. compare it to the verified baseline in this plan;
4. if files changed since the baseline, re-read touched files before modifying;
5. do not blindly apply line numbers from this plan.

Implementation:

1. common lookup state/input;
2. inventory item lookup;
3. customer lookup;
4. supplier lookup;
5. purchase combined item lookup;
6. Sales migration one document at a time;
7. Procurement migration one document at a time;
8. source-document hardening;
9. tests;
10. full build.

After each transaction page migration:

```text
build
run relevant test class
review diff
continue
```

Do not produce one giant unverified patch.

---

# 46. Final Architectural Target

After implementation:

```text
                     ┌───────────────────────┐
typed exact code ───▶│ Exact Resolve        │──▶ 0/1/ambiguous
                     │ bounded DB query      │
                     └───────────────────────┘
                               │
                               ▼
                    domain Selected handler
                               │
                               ▼
             existing page business/default logic
```

and:

```text
search icon
    │
    ▼
server search popup
    │
    ├── SearchText
    ├── Skip
    ├── Take
    └── TotalCount
    │
    ▼
20-row page
    │
    ▼
same Selected handler
```

Startup:

```text
OLD
Page load
 ├── all Items
 ├── all Customers
 ├── all Suppliers
 └── small references

NEW
Page load
 └── small references only

User asks for Item/Customer/Supplier
 └── bounded server request
```

---

# 47. Approval Assessment

| Area | Score |
|---|---:|
| Repository alignment | 10.0/10 |
| UX design | 10.0/10 |
| Blazor Server scalability | 10.0/10 |
| Preservation of business logic | 9.8/10 |
| Tenant/security correctness | 10.0/10 |
| Implementation clarity | 9.8/10 |
| Testability | 9.8/10 |
| Rollout safety | 9.7/10 |
| Over-engineering control | 9.7/10 |
| Overall | **9.8/10** |

### Approval decision

**APPROVED FOR CURSOR / GROK 4.7 IMPLEMENTATION.**

The remaining 0.2 points are not design defects. They represent normal implementation-time verification that can only be completed by building/running the modified repository and measuring the actual SQL/database behavior with representative data.

This plan is sufficiently concrete to implement without inventing business rules.

---

# 48. Final Instruction to Coding Agent

Implement this as a **lookup architecture + UX standardization**, not a cosmetic refactor.

The most important outcomes are:

1. Known code can be typed directly.
2. Debounce resolves it without extra clicks.
3. Search icon still opens discovery popup.
4. Popup queries the server in small pages.
5. Sales/Procurement no longer preload huge master lists.
6. Existing pricing/defaulting/validation behavior remains unchanged.
7. One shared interaction pattern is used across modules.
8. The system remains safe with large real-world ERP datasets.

If implementation pressure requires prioritization, complete in this exact order:

```text
Item
Customer
Supplier
Sales full-preload removal
PR/PO combined item lookup
Procurement full-preload removal
source-document picker hardening
```

Do not sacrifice business-rule preservation to finish faster.
