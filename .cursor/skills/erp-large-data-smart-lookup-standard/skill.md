---
name: erp-large-data-smart-lookup-standard
description: >-
  Mandatory lookup UX and scalability standard for mokth/net10projectTemplate.
  Use whenever creating, modifying, reviewing, or debugging any lookup/select/search field
  whose data source can become large or naturally grows over time, including Inventory Items,
  Customers, Suppliers/Vendors, direct/indirect Purchase Items, Lots/Balance Lots, Work Orders,
  Sales Orders, Delivery Orders, Invoices, Purchase Requisitions, Purchase Orders, GRNs,
  Production Balance Lots, Members, Employees, Projects, and similar business entities.
  Enforces editable exact-code resolution with debounce, icon-only popup search, server-side
  paging/search, race-safety, historical-value preservation, tenant isolation, and prohibition
  of full-table preload into Blazor Server circuits.
---

# ERP Large-Data Smart Lookup Standard

## Purpose

This skill defines the mandatory lookup architecture and user experience for **large or naturally unbounded data sources** in:

- Repository: `mokth/net10projectTemplate`
- Branch: `production`
- Stack: .NET 10, Blazor Server, DevExpress Blazor 26.1.x

Use this skill whenever an agent touches a field that selects or resolves data such as:

- Item;
- Customer;
- Supplier / Vendor;
- direct or indirect Purchase Item;
- Lot / Balance Lot;
- Work Order;
- Sales Order;
- Delivery Order;
- Sales Invoice;
- Purchase Requisition;
- Purchase Order;
- GRN / receipt source;
- Purchase Invoice;
- Production Balance Lot;
- Member;
- Employee;
- Project;
- Machine when the master can become large;
- any business master or transaction history that can naturally grow with customer usage.

The objective is:

> **Fast keyboard-first ERP entry for users who know the code, easy popup discovery for users who do not, and bounded server-side data access that remains safe when the database contains tens or hundreds of thousands of rows.**

This skill is not a cosmetic rule. It is both a **UX standard** and a **Blazor Server scalability standard**.

---

# 1. Repository Is the Source of Truth

Before implementing, always inspect the current `production` branch.

The repository may have evolved after this skill was written.

Do not blindly apply old line numbers or assume a component still has the same contract.

At minimum, inspect the closest live implementation before changing it.

Important current reference files include:

## Item lookup

- `ErpWeb.UI/Inventory/Lookups/IvStockMasterPicker.razor`
- `ErpWeb.UI/Inventory/Lookups/IvStockMasterPicker.razor.cs`
- `ErpWeb.UI/Inventory/Lookups/IvStockMasterSearchPopup.razor`
- `ErpWeb.UI/Inventory/Lookups/IvStockMasterSearchPopup.razor.cs`
- `ErpWeb.Core/Inventory/IvInventoryLookupService.cs`
- `ErpWeb.Model/Repositories/Inventory/IvStockMasterRepository.cs`

## Customer lookup

- `ErpWeb.Core/Sales/ISaCustLookupService.cs`
- `ErpWeb.Core/Sales/SaCustLookupService.cs`
- `ErpWeb.Model/Repositories/Sales/SaCustRepository.cs`
- `ErpWeb.Model/Repositories/Sales/SaCustSearchArgs.cs`

## Supplier lookup

- `ErpWeb.Core/Purchase/IPoSupplierLookupService.cs`
- `ErpWeb.Core/Purchase/PoSupplierLookupService.cs`
- `ErpWeb.Model/Repositories/Purchase/PoSupplierRepository.cs`
- `ErpWeb.Model/Repositories/Purchase/PoSupplierSearchArgs.cs`

## Good server-paged references

- `ErpWeb.UI/Inventory/Lookups/IvBalLocSearchPopup.razor`
- `ErpWeb.UI/Inventory/Lookups/IvBalLocSearchPopup.razor.cs`
- `ErpWeb.Core/Production/IProductionWorkOrderService.cs`

`IvBalLocSearchPopup` and the Work Order list are good references because they already use server-side `Skip`, `Take`, and `TotalCount` patterns.

---

# 2. Core Rule

For a naturally unbounded lookup:

> **Never preload the entire dataset into a Blazor Server page or circuit merely to provide a searchable dropdown.**

A searchable `DxComboBox` is **not scalable** when its `Data` collection already contains all Items, all Customers, or all Suppliers.

This is forbidden:

```csharp
var customers = await db.SaCusts
    .Where(...)
    .OrderBy(...)
    .ToListAsync();

var items = await db.IvStockMasters
    .Where(...)
    .OrderBy(...)
    .ToListAsync();
```

followed by:

```razor
<DxComboBox Data="@Customers"
            SearchMode="ListSearchMode.AutoSearch" />
```

The search appears modern, but the entire table has already crossed into the Blazor Server circuit.

For large data, search must happen **in SQL/server-side query composition** before materialization.

---

# 3. Standard User Experience

A large lookup should look and behave like:

```text
Customer
┌────────────────────────────────────────────┬─────┐
│ C000123                                    │ 🔍  │
└────────────────────────────────────────────┴─────┘
  ABC Trading Sdn Bhd
```

The user has two workflows.

## Workflow A — known code

```text
type code
   ↓
550 ms debounce
   ↓
exact server resolve
   ↓
found
   ↓
same Selected/commit pipeline as popup selection
   ↓
page applies existing defaults/business behavior
```

## Workflow B — do not know the code

```text
type optional search text
   ↓
click 🔍
   ↓
server-side popup search
   ↓
select row
   ↓
same Selected/commit pipeline
```

The lookup icon is for **discovery**, not a mandatory extra step.

---

# 4. Mandatory Visual Standard

Use:

```text
[ editable input ................................ ][ 🔍 ]
```

Do not use:

```text
[ read-only input ] [ Search ]
```

Rules:

- input and icon button must sit on the same row;
- same height;
- compact spacing;
- input takes remaining width;
- search button uses `fa-solid fa-magnifying-glass`;
- do not show `Search` text next to the icon for this lookup pattern;
- icon-only button must still have `title`, tooltip, or accessible name;
- layout must not wrap awkwardly on normal desktop widths.

Example accessible meaning:

```text
Search items
Search customers
Search suppliers
```

---

# 5. Exact Resolve Behavior

Typed entry is an **exact resolve**, not a fuzzy popup search.

Examples:

```text
ITEM001
C000123
SUP001
WO000045
```

The resolver should query the smallest possible bounded result.

For Item, preserve the repository's existing behavior where applicable:

```text
exact Item Code
then exact Barcode
```

Do not use an unbounded `%term%` search just to validate a known code.

---

# 6. Debounce Standard

Default delay:

```text
550 ms
```

Use DevExpress delayed input when practical:

```razor
BindValueMode="BindValueMode.OnDelayedInput"
InputDelay="550"
```

or equivalent controlled debounce logic.

Do not resolve on every keypress.

The debounce is an interaction optimization, not a correctness boundary.

---

# 7. Enter and Escape

## Enter

Pressing `Enter` must:

1. cancel pending delayed resolve;
2. immediately resolve current input;
3. ignore stale older responses;
4. commit the value only if still current.

## Escape

Pressing `Escape` should:

1. cancel pending resolve;
2. clear transient errors;
3. restore the last committed value;
4. not mutate transaction business state.

---

# 8. Critical State Separation

A smart lookup must distinguish:

```text
temporary typed text
from
committed business value
```

Do not bind every keystroke directly to a transaction's actual Customer/Supplier/Item property when changing that property has side effects.

Example:

```text
Committed customer: C00001

User types C99999
C99999 is invalid

Correct:
input returns to C00001
existing sales lines remain
customer defaults remain

Wrong:
CustCode becomes null
existing lines get cleared
```

Maintain conceptual state such as:

```csharp
_inputText
_committedValue
_committedDisplayText
```

The lookup must not emit destructive null values because a replacement attempt failed.

Explicit user Clear is different and may commit null when the consuming page allows it.

---

# 9. Not-Found Behavior

Do not use a modal, blocking alert, or repetitive toast for an invalid code.

Use inline validation.

Example:

```text
SUP9999
Supplier not found
```

Recommended timing:

```text
550 ms resolve delay
↓
not found
↓
show inline message
↓
about 900 ms grace period
↓
if input is unchanged:
    restore previous committed value
    or clear when there was no previous committed value
```

Never clear newer user input because an older lookup failed.

---

# 10. Race-Condition Safety Is Mandatory

Blazor Server requests may return out of order.

Example:

```text
request #30 → ABC
request #31 → ABC001

#31 succeeds first
#30 returns later
```

Request #30 must be ignored.

Use:

- `CancellationTokenSource`;
- request/generation sequence;
- preferably both.

Required conceptual rule:

```csharp
var seq = ++_requestSequence;

var result = await ResolveAsync(...);

if (seq != _requestSequence)
    return;
```

Apply stale-request protection to:

- delayed exact resolve;
- Enter resolve;
- popup searches;
- delayed not-found restore/clear.

---

# 11. Same Commit Pipeline

Typed resolution and popup selection must execute the **same commit path**.

Do not create two separate business flows.

Required shape:

```text
typed resolve ───────┐
barcode resolve ─────┤
popup selection ─────┤
                     ▼
              CommitSelectionAsync(row)
                     │
                     ├─ committed value
                     ├─ display text
                     ├─ ValueChanged
                     └─ Selected(row)
```

This prevents:

```text
typed Item populates only code
popup Item populates code + UOM + description
```

which is a serious ERP consistency bug.

---

# 12. Shared UX Mechanics vs Domain Logic

Do not create one giant generic component that knows all business rules.

Use this architecture:

```text
SmartLookupInput<T>
       │
       ├─ debounce
       ├─ cancellation
       ├─ stale-response control
       ├─ loading state
       ├─ invalid state
       ├─ Enter
       ├─ Escape
       └─ icon/search action

Domain wrapper
       │
       ├─ Item resolver/search
       ├─ Customer resolver/search
       ├─ Supplier resolver/search
       └─ domain row/display contract

Page
       │
       └─ transaction-specific business/default logic
```

The generic component owns **interaction mechanics**.

The domain component owns **lookup semantics**.

The page owns **business effects**.

---

# 13. Recommended Shared UI Location

When implementing the shared smart-lookup infrastructure, use:

```text
ErpWeb.UI/Components/Common/Lookups/
```

Recommended files:

```text
SmartLookupInput.razor
SmartLookupInput.razor.cs
SmartLookupInput.razor.css
SmartLookupState.cs
```

Follow the project's code-behind convention.

Do not place giant `@code` blocks into `.razor` files when the surrounding module uses `.razor.cs`.

---

# 14. Domain Wrapper Locations

Recommended:

## Customer

```text
ErpWeb.UI/Sales/Lookups/
    SaCustomerPicker.razor
    SaCustomerPicker.razor.cs
    SaCustomerSearchPopup.razor
    SaCustomerSearchPopup.razor.cs
```

## Supplier

```text
ErpWeb.UI/Purchase/Lookups/
    PoSupplierPicker.razor
    PoSupplierPicker.razor.cs
    PoSupplierSearchPopup.razor
    PoSupplierSearchPopup.razor.cs
```

## Purchasing Item

```text
ErpWeb.UI/Purchase/Lookups/
    PoPurchasingItemPicker.razor
    PoPurchasingItemPicker.razor.cs
    PoPurchasingItemSearchPopup.razor
    PoPurchasingItemSearchPopup.razor.cs
```

Reuse `IvStockMasterPicker` for stock Items instead of creating redundant Item components unless Sales/Procurement genuinely needs a thin domain wrapper.

---

# 15. Server-Side Paging Contract

For large lookup search, use the conceptual contract:

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
```

Default:

```text
Take = 20
```

Hard maximum:

```text
100
```

Never accept unlimited `Take`.

Blank search may return the first page by code.

Blank search must never mean:

```text
load entire table
```

---

# 16. Large Lookup Service Pattern

Each large domain should normally provide two operations:

```csharp
ResolveAsync(code)
SearchPagedAsync(request)
```

The roles are different.

## Resolve

Optimized for known exact identity:

```text
ITEM001
C00001
SUP100
```

Normally returns:

```text
0 rows
1 row
ambiguous
```

## Search

Optimized for discovery:

```text
steel
ABC trading
sony
WO-26
```

Returns:

```text
Rows
TotalCount
```

with `Skip/Take`.

Do not conflate these two paths.

---

# 17. Tenant / Scope Rules

Never weaken existing data isolation.

Current repository rules include:

## Item

```text
CompanyCode
```

Do not add BranchCode to Item master lookup merely for consistency when the live repository intentionally scopes Item by Company.

## Customer

```text
CompanyCode
```

## Supplier

For Procurement transaction selection, preserve:

```text
CompanyCode + BranchCode
```

where current service/repository behavior uses branch scope.

## Transaction-history lookups

Preserve current:

```text
CompanyCode + BranchCode
```

when the underlying transaction is branch-scoped.

Never trust tenant/company/branch values supplied by browser input when current user context already supplies them.

---

# 18. Existing Repositories Should Be Reused

Do not create duplicate repositories just to support lookup UI.

Current useful repository contracts already include:

## Item

`IIvStockMasterRepository`

- `GetByCodeAsync`
- `SearchPagedAsync`
- `GetByBarcodeAsync`

## Customer

`ISaCustRepository`

- `GetByCodeAsync`
- `SearchPagedAsync`

## Supplier

`IPoSupplierRepository`

- `GetByCodeAsync`
- `SearchPagedAsync`

Prefer extending existing lookup services over inventing parallel data-access stacks.

---

# 19. Search Ranking

Preferred ranking:

```text
1. exact code
2. code starts with term
3. code contains term
4. name starts with term
5. name contains term
6. stable code sort
```

However:

> Do not introduce fragile EF translation or expensive client-side sorting merely to achieve perfect ranking.

If necessary, use:

```text
filtered server query + Code ascending
```

for Phase 1.

Bounded server retrieval and correctness are more important than sophisticated ranking.

---

# 20. Search Popup Standard

A large-data popup must:

- search server-side;
- use a small page;
- show total count where useful;
- allow next/previous page or supported virtual server loading;
- support double-click selection;
- support Enter to select focused row;
- support Escape to close;
- accept initial search text from the main input;
- not reload the entire master.

Example:

```text
Search: [ ABC Trading                    ]

Code       Name                         Currency
-------------------------------------------------
C0012      ABC Trading Sdn Bhd          MYR
C0177      ABC Trading JB               MYR

Showing 1–20 of 57
```

Keep popup rows light.

Do not load full address/contact children just to show a search row.

---

# 21. Historical / Inactive Values

This is mandatory in ERP.

A persisted historical document must continue showing a master value even after the master is inactive.

Example:

```text
2024 PO → SUP001
SUP001 becomes inactive in 2026
```

Opening the old PO must still show:

```text
SUP001 — ABC Supplier
```

Rules:

```text
new lookup search choices      => active only
new exact typed selection      => active only
persisted document display     => preserve stored value
```

Do not automatically clear a persisted inactive Customer/Supplier/Item while rendering a page.

Do not require active lookup membership merely to display an existing transaction.

---

# 22. Existing Value vs New Selection

If a lookup must resolve current metadata for an already-persisted value, distinguish:

```text
ResolveForDisplay / includeInactive
```

from:

```text
ResolveForNewSelection / activeOnly
```

Do not accidentally make inactive masters selectable for new transactions merely to support historical display.

---

# 23. Customer-Specific Rule

Customer selection often has transaction side effects.

Existing Sales pages contain logic such as:

```text
_customerApplySeq
OnCustCodeChanged
ApplyCustomerAsync
ApplyCustomerDefaultsAsync
confirm customer change when lines exist
clear dependent fields
```

The shared Customer picker must **not** perform these business actions.

Correct:

```text
SaCustomerPicker resolves row
        ↓
page handler receives row
        ↓
existing OnCustCodeChanged / confirmation logic
        ↓
existing ApplyCustomerAsync
        ↓
existing defaults
```

Wrong:

```text
picker directly changes sales document fields
picker clears lines
picker bypasses confirmation
```

---

# 24. Supplier-Specific Rule

Procurement supplier selection has similar side effects.

Existing PO behavior includes:

```text
CanEditSupplier
ConfirmSupplierChangeVisible
ApplySupplierAsync
GetSupplierDefaultsAsync
ClearSupplierFields
```

The Supplier picker must resolve/select only.

The transaction page remains responsible for:

- confirmation;
- clearing dependent fields;
- supplier address;
- terms;
- currency;
- one-time behavior;
- document lines.

---

# 25. Item-Specific Rule

Existing page logic often applies:

```text
Description
Std UOM
Selling/Purchase UOM
Pack size
Stock control
Tax group
Default warehouse
Classification
Purchase GL
Default vendor
```

Do not lose these defaults when removing the full in-memory Item list.

The selected lookup row must contain the metadata required by the existing page.

Then the page's existing selected-item handler applies that metadata.

---

# 26. Sales Pricing Rule — Non-Negotiable

Sales Item lookup is not pricing authority.

Several current Sales pages intentionally state that Item master selling price is **not** directly copied to UnitPrice.

The Sales pricing engine remains authoritative.

When migrating:

```text
selected item
↓
apply item metadata
↓
existing ResolvePopupPriceAsync(...)
```

Do not replace it with:

```csharp
Popup.UnitPrice = item.SellingPrice;
```

unless the live page explicitly does that today.

Do not silently change pricing behavior while fixing lookup architecture.

---

# 27. Existing Sales Lines Must Not Reprice on Open

Existing draft line editing deliberately avoids silent repricing.

Preserve this.

Do not call pricing resolution merely because:

```text
user opened Edit Line
```

Pricing should continue to run only under the same current business triggers.

If existing helper methods depend on a full `Items` list:

```text
RefreshPackFromItem
ClassificationFromItem
```

replace that dependency with:

- persisted line metadata; or
- exact single-item resolve; or
- bounded batch resolve for only the document's used item codes.

Never reload the entire Item master to support existing-line editing.

---

# 28. Purchase Cost Permission

Purchase Item lookup must not expose cost to users who lack `VIEW_COST`.

If a lookup row contains:

```text
PurchasePrice
UnitCost
```

enforce permission at the service boundary where possible.

Do not show hidden cost in:

- popup row;
- browser state;
- returned DTO;
- tooltip.

Permission must not depend only on CSS hiding.

---

# 29. Direct + Indirect Purchase Items

PR/PO may select from both:

```text
IvStockMaster
PoPurItem
```

Do not preload both full tables.

Use a server-driven combined purchasing-item lookup.

If the same code exists in both sources:

```text
do not silently choose one
```

Return ambiguous state or require popup selection.

Popup should identify source:

```text
Stock
Indirect
```

This prevents hidden behavior changes.

---

# 30. `GetLookupsAsync()` Rule

After smart lookup migration:

> `GetLookupsAsync()` should contain only small/bounded reference lists.

Forbidden unbounded startup payloads:

```text
all Items
all Customers
all Suppliers
all Lots
all Work Orders
all Sales Orders
all POs
all Invoices
```

Allowed examples:

```text
Tax Groups
Currencies
Payment Terms
UOM
Status
small Warehouse list
small reference codes
```

Projects/Departments may stay as normal lists only while they are known to remain small.

If they can grow substantially for real customers, move them to smart server lookup too.

---

# 31. Current Known Full-Preload Hotspots

When modifying these areas, explicitly check for removal of large master preloads.

## Sales

- `ErpWeb.Core/Sales/SaQtService.cs`
- `ErpWeb.Core/Sales/SaSoService.cs`
- `ErpWeb.Core/Sales/SaDoService.cs`
- `ErpWeb.Core/Sales/SaInvoiceService.cs`
- `ErpWeb.Core/Sales/SaCdnService.cs`

Their interfaces/results have historically carried Item/Customer collections.

## Procurement

- `ErpWeb.Core/Purchase/PoPrService.cs`
- `ErpWeb.Core/Purchase/PoOrderService.cs`
- `ErpWeb.Core/Purchase/PoInvoiceService.cs`
- `ErpWeb.Core/Purchase/PoCdnService.cs`
- `ErpWeb.Core/Purchase/PoSbLookupsLoader.cs`

Do not assume these remain unchanged—inspect current production first.

---

# 32. Transaction-History Lookups Also Count as Large

The rule is not limited to master tables.

Naturally unbounded transaction lookups include:

- open Sales Orders;
- posted Delivery Orders;
- Sales Invoices;
- PRs;
- POs;
- GRNs;
- Purchase Invoices;
- Work Orders;
- production outputs;
- production balance lots.

These must be server searched/paged or otherwise explicitly bounded.

A source-document picker that loads years of documents is the same scalability defect as loading all Customers.

---

# 33. Bounded Is Better Than Unbounded, But Paging Is Better

Existing methods using:

```text
Take(50)
Take(100)
```

are safer than unlimited `ToListAsync()`.

Do not unnecessarily rewrite them during an unrelated feature.

However, if users need to browse beyond the cap, evolve them to:

```text
SearchText
Skip
Take
TotalCount
```

rather than increasing the cap to a huge number.

---

# 34. Do Not Over-Standardize Small Reference Combos

Do not convert everything into a smart lookup.

Normal combo boxes remain appropriate for genuinely small reference lists such as:

- Currency;
- UOM;
- Tax Group;
- Payment Term;
- Status;
- Country;
- normal Warehouse list;
- small type/category reference codes.

The purpose of this skill is not to add complexity.

The decision rule is:

> **Can this dataset naturally grow with business activity or years of usage?**

If yes: smart server lookup.

If no and it is a small reference family: normal combo is usually correct.

---

# 35. Explicit Clear Behavior

Clear-button behavior must be intentional.

For optional lookups:

```text
explicit Clear
↓
commit null/empty
↓
page handles business consequences
```

For required lookups:

- Clear may temporarily blank the input;
- page validation still prevents save;
- do not silently restore unless business UX requires it.

Failed resolution is **not** the same as explicit Clear.

---

# 36. Loading Feedback

During exact resolve, use subtle feedback.

Acceptable:

- spinner inside/near field;
- temporary resolving icon/state;
- disabled search button during critical short operation if necessary.

Avoid:

- blocking whole page;
- full-screen loader;
- toast per keystroke.

Lookup should feel lightweight.

---

# 37. Error Types

Differentiate:

```text
NotFound
Ambiguous
Authorization
Server/Database failure
```

Suggested UX:

## Not found

Inline:

```text
Customer not found
```

## Ambiguous

Inline:

```text
Multiple matches found. Use search.
```

and keep search icon available.

## Authorization / server failure

Display a meaningful page/field error.

Do not convert infrastructure failure into misleading `Not found`.

---

# 38. Save/Post Validation Still Required

Lookup validation is not an integrity boundary.

Even if the UI resolves a Customer/Supplier/Item successfully, the service must continue validating at:

```text
Save
Update
Post
Release
Submit
```

because:

- record can become inactive;
- stock state can change;
- permissions can change;
- another user can modify data.

Never remove service-layer validation because the smart picker "already checked it."

---

# 39. Search Query Safety

Every large lookup query must:

- apply tenant/company/branch scope first;
- remain `AsNoTracking()` for read-only lookup;
- apply filters in SQL;
- project only required lookup columns;
- apply deterministic order;
- apply `Skip`;
- apply bounded `Take`;
- materialize last.

Preferred pipeline:

```text
Scope
→ Filter
→ Rank/Order
→ Project
→ Skip
→ Take
→ ToListAsync
```

Do not materialize before filtering/paging.

---

# 40. Do Not Add Speculative Indexes

Do not automatically add SQL indexes merely because a smart lookup uses search.

First:

1. implement bounded query;
2. test with representative data;
3. inspect SQL Server execution plan;
4. add index only when evidence supports it.

Schema/index work should be separately justified.

---

# 41. Existing Good Patterns Must Stay Good

Do not replace working paged implementations merely to use a new abstraction.

Examples:

## Balance Lot / On-Hand

Current `IvBalLocSearchPopup` already uses:

```text
SearchText
Skip
Take
TotalCount
```

Keep that pattern.

## Work Order list

Current Work Order list query already contains:

```text
Skip
Take
```

Keep it.

Standardize only where needed.

---

# 42. Test Standard

Every new large lookup implementation needs tests for:

```text
exact resolve
not found
paging
TotalCount
tenant isolation
active-only new selection
historical/inactive display rule
max Take clamp
stale-request protection
```

Use representative counts above one page.

Example:

```text
250 active records
10 inactive records
second company/branch data
```

---

# 43. UI State Tests

The shared lookup state/controller should be testable without adding bUnit solely for this feature.

Test:

- valid delayed resolve commits;
- Enter resolves immediately;
- Escape restores committed value;
- stale old response is ignored;
- delayed not-found clear cannot clear newer text;
- invalid replacement does not emit null;
- explicit Clear can emit null;
- popup selection and typed selection use the same commit semantics.

Use xUnit in `ErpWeb.Tests`.

---

# 44. Regression Tests

When migrating transaction pages, existing service tests must remain green.

Examples include:

## Sales

- `SaQtServiceTests`
- `SaSoServiceTests`
- `SaDoServiceTests`
- `SaInvoiceServiceTests`
- `SaCdnServiceTests`

## Procurement

- `PoPrServiceTests`
- `PoOrderServiceTests`
- `PoInvoiceServiceTests`
- `PoCdnServiceTests`

Also run relevant SQL Server concurrency tests when touched services participate in them.

---

# 45. Build Gate

After any `.razor` or `.razor.cs` lookup change:

```text
dotnet build ErpWeb.slnx
```

Final implementation gate:

```text
dotnet test
dotnet build ErpWeb.slnx
```

A green unit-test suite alone does not prove Razor compilation.

---

# 46. Large-Data Acceptance Gate

For a migrated page, verify behavior with representative scale such as:

```text
20,000 Items
10,000 Customers
10,000 Suppliers
```

Opening the page must **not** retrieve all those rows.

Expected:

```text
Page startup:
small reference lists only

Exact lookup:
1 bounded query

Popup:
20 rows per page by default
<= 100 maximum
```

Do not set a fixed millisecond SLA without deployment evidence.

The mandatory architectural SLA is:

> **bounded database result + bounded circuit state.**

---

# 47. Review Checklist

Reject or revise a lookup implementation when any of these are true:

- [ ] Input is read-only and forces popup for a known code.
- [ ] Search button contains unnecessary `Search` text beside magnifying glass.
- [ ] Input and search button do not sit in one clean row.
- [ ] Every keystroke causes a database query.
- [ ] Full Item master is loaded into page state.
- [ ] Full Customer master is loaded into page state.
- [ ] Full Supplier master is loaded into page state.
- [ ] Full transaction-history table is loaded into a picker.
- [ ] A searchable ComboBox is used over a giant preloaded collection.
- [ ] Search is performed client-side over thousands of rows.
- [ ] Typed resolve and popup selection populate different fields.
- [ ] Failed lookup clears an existing committed Customer/Supplier and destroys dependent state.
- [ ] Stale requests can overwrite newer input.
- [ ] Inactive historical values disappear from old documents.
- [ ] Customer/Supplier confirmation logic is bypassed.
- [ ] Sales UnitPrice is incorrectly taken directly from Item master.
- [ ] Existing Sales line gets repriced merely by opening edit.
- [ ] Purchase cost is exposed without `VIEW_COST`.
- [ ] Tenant/branch scoping was weakened.
- [ ] `Take` is unbounded.
- [ ] UI validation replaced server save/post validation.
- [ ] Huge speculative index/schema changes were added without evidence.

If any checked condition exists, the implementation is not approved.

---

# 48. Implementation Checklist

Before marking complete:

## UX

- [ ] Editable known-code input.
- [ ] 550 ms debounce.
- [ ] Enter immediate resolve.
- [ ] Escape restore/cancel.
- [ ] Icon-only magnifying-glass button.
- [ ] Accessible search button title/name.
- [ ] Input + search icon same row.
- [ ] Inline not-found message.
- [ ] Popup inherits typed search text.
- [ ] Popup supports keyboard selection.

## Correctness

- [ ] Typed and popup selection share one commit path.
- [ ] Invalid replacement does not emit destructive null.
- [ ] Stale request protection exists.
- [ ] Existing persisted/inactive value remains displayable.
- [ ] Service validation remains authoritative.

## Performance

- [ ] No full-table preload.
- [ ] Server filtering.
- [ ] Server `Skip/Take`.
- [ ] Default page size around 20.
- [ ] Hard maximum no more than 100.
- [ ] `TotalCount` available when popup uses paging.

## Business preservation

- [ ] Sales pricing remains unchanged.
- [ ] Tax/default warehouse/UOM behavior remains unchanged.
- [ ] Customer change confirmation remains unchanged.
- [ ] Supplier change confirmation remains unchanged.
- [ ] Purchase cost permission remains unchanged.
- [ ] Posting/costing algorithms untouched.

## Build

- [ ] Relevant tests pass.
- [ ] `dotnet test` passes.
- [ ] `dotnet build ErpWeb.slnx` passes.

---

# 49. Agent Workflow

When asked to add or modify a lookup:

## Step 1 — classify the source

Ask from repository evidence:

```text
Is this a small bounded reference list?
or
Is it naturally unbounded?
```

Do not ask the user when the code/data model already makes the answer obvious.

## Step 2 — inspect current flow

Find:

- UI control;
- page handler;
- service;
- repository;
- result DTO;
- defaults/side effects;
- tenant scope;
- permissions;
- existing tests.

## Step 3 — identify business side effects

For example:

```text
Customer → currency/address/tax/line confirmation
Supplier → currency/terms/address/line confirmation
Item → UOM/tax/warehouse/pricing/cost
```

Document these before replacing the lookup.

## Step 4 — remove unbounded preload

Move the large dataset out of:

```text
GetLookupsAsync
page initialization
component state
```

without removing small reference lists.

## Step 5 — implement exact resolve + server popup

Reuse existing repositories where available.

## Step 6 — wire to existing business handler

Do not recreate transaction logic in the picker.

## Step 7 — test one page/module at a time

Do not create one giant cross-module patch with no intermediate build.

## Step 8 — run build and tests

Required.

---

# 50. Final Instruction to Coding Agents

When working on a large ERP lookup in this repository:

> **Optimize for two users at once: the experienced operator who already knows the code and wants zero extra clicks, and the occasional operator who needs a searchable popup. Both paths must resolve through the same business selection logic.**

And:

> **Never solve lookup convenience by loading an unbounded master or history table into a Blazor Server circuit. Search and paging belong on the server.**

Preserve the existing ERP business rules. Standardize the lookup interaction and data-loading architecture, not the domain behavior.
