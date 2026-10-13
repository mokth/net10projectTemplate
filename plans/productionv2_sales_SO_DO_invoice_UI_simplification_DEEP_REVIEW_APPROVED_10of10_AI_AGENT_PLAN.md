# productionv2 Sales SO / DO / Invoice UI Simplification
## Deep Repo-Reviewed AI Coding Agent Plan

**Repository:** `mokth/net10projectTemplate`  
**Branch reviewed:** `productionv2`  
**Scope:** Sales UI + UI orchestration only  
**Backend/domain/schema changes:** **NONE**  
**Status:** **APPROVED FOR IMPLEMENTATION — 10/10 PLAN REVIEW**

> This approval is for the plan. The implementation is approved only after the build, tests, and runtime acceptance gates in this document pass.

## 1. Review conclusion

The existing Sales backend is already strong. The main usability problem is repeated navigation and re-selection between SO, DO, stock allocation, posting, and Invoice.

The approved target is:

```text
SO
Customer → Quick Add lines → Save
                  ↓
             Create DO
                  ↓
DO
SO/customer/remaining lines already prepared
↓
Review → Save → Allocate Stock → Post
                  ↓
             Create Invoice
                  ↓
Invoice
DO/customer/billable lines already prepared
↓
Review → Save → Post
```

The system must reduce clicks by automating **existing UI steps**, not by skipping business checkpoints.


## 2. Deep-review corrections to the earlier plan

The earlier direction was correct, but these changes are mandatory:

1. **No separate start wizard.** Keep the normal `SaDocPage` and add a compact Source launcher. A wizard adds another click for manual users.
2. **Remove NEW-document `Save & Allocate`.** Use `Save → saved edit route → Allocate Stock → Post`. This avoids fragile Blazor route/mode/error handling.
3. **Source-first must reuse current customer-default logic.** Do not copy SO/DO header snapshots directly into downstream documents.
4. **Never force downstream currency from the source.** Existing service compatibility checks remain authoritative.
5. **Respect actual search capabilities.** SO search currently supports SO no/customer code/customer name, not Customer PO. DO search supports DO no/customer code/customer name.
6. **DO entry Post must send current RowVersion.** Use the exact existing `SaDoKeyedRequest`.
7. **Successful Post must navigate to View mode.** Do not leave a POSTED document on an Edit route.
8. **Source query parameters require one-shot bootstrap protection.** Include source identity in route/load state so re-rendering cannot import twice.
9. **Quick Add must reuse existing pricing/validation code.** No second simplified price engine.
10. **Generic source lookup must remain bounded/server-driven.** Never preload all SOs or DOs into the Blazor circuit.

This reviewed version supersedes the previous plan.


## 3. Non-negotiable change boundary

Do not change behavior in:

```text
ErpWeb.Core/Sales
ErpWeb.Model
EF mappings
database tables
migrations
```

Do not alter:

- SO revision / `CustRel`;
- SO remaining quantity;
- reservations / over-allocation;
- Delivery Request controls;
- production-demand rules;
- pricing / price override;
- tax / discount;
- warehouse logic;
- shipment / FIFO / lot rules;
- future-stock protection;
- stock movement;
- costing;
- document numbering;
- Post / Rollback / Force Close;
- optimistic concurrency;
- e-Invoice readiness;
- MyInvois lifecycle.

If an enhancement truly requires a new backend capability, mark that enhancement **out of scope** rather than quietly changing the backend.


## 4. Mandatory repository UI standards

The coding agent must read:

```text
.agents/skills/erp-ui-core/skill.md
.agents/skills/sales-transaction-ui/skill.md
.agents/skills/devexpress-blazor-editor-binding-safety/skill.md
.agents/skills/erp-large-data-smart-lookup-standard/skill.md
.agents/skills/devexpress-popup-action-safety/skill.md
.agents/skills/devexpress-dxgrid-column-readability/skill.md
```

Continue using:

```text
SaDocPage
sdoc-*
DxFormLayout
DxGrid
DxPopup
DxButton
SaCustomerPicker
IvStockMasterPicker
SaDocFlowPanel
SdValidationSummary
```

`SaDocFlowPanel` stays read-only. Do not replace Sales entry with an Inventory-style hero/card shell.


## 5. Repo evidence used for this review

Primary UI:

```text
ErpWeb.UI/Sales/Transactions/SaSo.razor/.cs
ErpWeb.UI/Sales/Transactions/SaDo.razor/.cs
ErpWeb.UI/Sales/Transactions/SaInvoice.razor/.cs
ErpWeb.UI/Sales/Transactions/SaSoList.razor/.cs
ErpWeb.UI/Sales/Transactions/SaDoList.razor/.cs
ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor/.cs
ErpWeb.UI/Sales/Transactions/SaDocFlowPanel.razor
```

Existing contracts:

```text
ISaSoService
ISaDoService
ISaInvoiceService
```

Important live methods already available:

```text
SO:
GetAsync
SearchAsync
GetRemainingLinesAsync
GetBillableLinesAsync
GetCustomerDefaultsAsync
SaveNewAsync
UpdateAsync
ReviseAsync

DO:
GetAsync
SearchAsync
GetCustomerDefaultsAsync
SaveNewAsync
UpdateAsync
AddShipmentAsync
SearchBillableLinesAsync
PostAsync

Invoice:
GetAsync
GetCustomerDefaultsAsync
SaveNewAsync
UpdateAsync
AddShipmentAsync
PostAsync
```

The core plan therefore does **not** require a new backend service.


## 6. Phase 1 — Save keeps the user in the document

### SO

New SO:

```text
Sos.SaveNewAsync()
→ use returned SoNo
→ _isDirty = false
→ /sales/sales-orders/edit/{SoNo}
```

Existing SO update:

```text
Sos.UpdateAsync()
→ ApplyDocument(result.Document)
→ RecalcDocument()
→ _isDirty = false
→ StatusMessage = "Sales order saved."
```

SO Revise: after successful revision, leave `revise` mode and navigate to the current SO edit/view route. Do not leave the newly-created revision in the revise UI state.

### DO

New DO:

```text
Dos.SaveNewAsync()
→ /sales/delivery-orders/edit/{result.DoNo}
```

Existing DO:

```text
Dos.UpdateAsync()
→ ApplyDocument(result.Document)
→ refresh RowVersion
→ stay on page
```

### Invoice

New Invoice:

```text
Invoices.SaveNewAsync()
→ /sales/invoices/edit/{result.InvNo}
```

Do this even when `PostWarnings` exist; `GetAsync()` reloads the current warning state.

Existing Invoice:

```text
Invoices.UpdateAsync()
→ ApplyDocument(result.Document)
→ PostWarnings = result.PostWarnings
→ stay on page
```

**Acceptance:** no normal successful Save returns the clerk to a list.


## 7. Phase 2 — compact Source launcher, not a wizard

### Blank new DO

Inside the normal Sales entry layout:

```text
SOURCE

Fastest way to create a Delivery Order
[ 🔍 FIND SALES ORDER ]

or select a customer below for a manual Delivery Order.
```

### Blank new Invoice

```text
SOURCE

[ FROM DELIVERY ORDER ]   Recommended
[ FROM SALES ORDER ]

or select a customer below for a manual invoice.
```

Do not make this a separate full-page step. Once a source/manual path is chosen, keep the helper compact.


## 8. Phase 3 — SO → Create DO

On a saved current SO add a contextual action outside the titlebar:

```text
Next action
[ CREATE DELIVERY ORDER ]
```

UI gate:

```text
SoNo saved
!_isDirty
IsCurrentDocument
!IsHistoricalRevision
Status = NEW or SHIPPED
user has Sales Delivery Order ADD permission
!IsSubmitting
```

Permission:

```csharp
AccessRights.CanAsync(
    MenuCodes.SalesDeliveryOrder,
    PermissionCodes.Add)
```

If dirty:

```text
Save changes before creating the Delivery Order.
```

Navigate only:

```text
/sales/delivery-orders/new?sourceSo={escaped SoNo}
```

Do not create/save the DO from the SO page.


## 9. Phase 4 — one-shot source query bootstrap

Add UI-only query parameters.

DO:

```csharp
[SupplyParameterFromQuery(Name = "sourceSo")]
public string? SourceSoNo { get; set; }
```

Invoice:

```csharp
[SupplyParameterFromQuery(Name = "sourceDo")]
public string? SourceDoNo { get; set; }

[SupplyParameterFromQuery(Name = "sourceSo")]
public string? SourceSoNo { get; set; }
```

Rules:

- process only in `new` mode;
- ignore in edit/view;
- include source identity in `_loadedKey` or an equivalent bootstrap key;
- use `_sourceBootstrapAppliedFor` or equivalent one-shot state;
- never import the same source twice because of rerender/parameter refresh;
- no timer-based workaround.


## 10. Phase 5 — DO bootstrap from a known SO

For:

```text
/sales/delivery-orders/new?sourceSo=SO000123
```

run after normal DO lookups and `ResetNewDocument()`.

### A. Load source

```csharp
Sos.GetAsync(SourceSoNo)
```

Require a current source and normal usable status (`NEW` or `SHIPPED`). Closed/superseded/historical sources must not be silently used.

### B. Apply customer using current DO behavior

Use the SO `CustCode`, but route it through the existing DO customer path:

```text
OnCustCodeChanged
→ ApplyCustomerAsync
→ Dos.GetCustomerDefaultsAsync
→ ApplyDefaults
```

Do **not** directly assign SO address/payment/currency/header snapshots to the DO.

### C. Load actual remaining lines

Reuse:

```csharp
Sos.GetRemainingLinesAsync(sourceSoNo, excludeDoNo: null)
```

This already accounts for real reservations, delivered qty, draft documents, DR/production capacity, and current revision.

### D. Reuse exact mapping

```csharp
SaDoLineVm.FromSalesOrder(...)
```

No second mapper.

### E. Populate the unsaved draft

For direct SO → DO launch, place all currently eligible remaining lines into the draft and show:

```text
Source: SO000123
5 eligible lines added
```

Nothing is persisted until Save, so the user can adjust/remove quantities.

### F. No remaining qty

Show a useful empty state and do not create an empty DO:

```text
SO000123 has no remaining quantity available for delivery.

Quantity may already be delivered, reserved by another draft,
or controlled by the current Delivery Request/production workflow.

[ CHOOSE ANOTHER SO ] [ MANUAL DELIVERY ORDER ]
```


## 11. Generic “Find Sales Order” on new DO

Use existing:

```csharp
Sos.SearchAsync(SaSoListQuery)
```

Actual search capability today:

```text
SO No
Customer Code
Customer Name
```

Do **not** advertise Customer PO search in this UI-only change.

Open statuses are normally:

```text
NEW
SHIPPED
```

Because `SaSoListQuery.Status` is one status per call, the UI may issue two bounded server calls, merge/deduplicate the small result sets, and require search refinement rather than loading all open SOs.

Lookup requirements:

- server filtering;
- bounded results;
- ~400–550 ms debounce;
- stale-response protection;
- loading state;
- keyboard selection;
- readable document/customer/date/status columns;
- no full-table preload.

Selecting an SO must call the **same bootstrap path** used by `sourceSo`; do not create a second behavior.


## 12. Existing Add from SO remains for an already-started DO

Do not remove:

```text
OpenSoPickerAsync
LoadSoPickerOptionsAsync
OnSelectedSourceSoChangedAsync
AddFromSo
```

Once a DO has a customer/lines, the current customer-scoped source picker is appropriate.

UI-only polish is allowed, e.g.:

```text
ADD 3 LINES
```

instead of:

```text
Add selected
```

but source rules and mappings remain unchanged.


## 13. Phase 6 — rename “Add shipment” to operator language

Change user-facing wording:

```text
Add shipment
```

to:

```text
ALLOCATE STOCK
```

When allocation already exists, a suitable label may be:

```text
REVIEW / REBUILD STOCK ALLOCATION
```

The handler and service remain exactly:

```text
OnAddShipmentAsync()
Dos.AddShipmentAsync(...)
Invoices.AddShipmentAsync(...)
```

No shipment/FIFO logic changes.


## 14. DO workflow after Save — corrected design

Do **not** add a combined `SAVE & ALLOCATE` action for a brand-new DO.

Approved flow:

```text
New DO
[ SAVE ]
↓
/sales/delivery-orders/edit/DO000123
↓
Next action:
[ ALLOCATE STOCK ]
↓
[ POST DELIVERY ORDER ]
```

Use existing mapped state:

```text
NeedsShipment
ShipmentComplete
_hasShipment
```

If incomplete:

```text
⚠ Stock allocation incomplete
[ REVIEW STOCK ALLOCATION ]
```

For an existing dirty NEW DO, current `OnAddShipmentAsync()` already saves the latest changes before allocation. Keep that logic.


## 15. Phase 7 — single-document DO Post

Keep batch Post on `SaDoList`.

Add entry-page Post permission:

```csharp
CanPostPermission = await AccessRights.CanAsync(
    MenuCodes.SalesDeliveryOrder,
    PermissionCodes.Post);
```

UI eligibility should require:

```text
saved DO
Status = NEW
!_isDirty
!IsSubmitting
CanPostPermission
and if NeedsShipment => ShipmentComplete
```

This is UX guidance only; the service is authoritative.

Confirmation popup:

```text
Post DO000123?

Posting will issue stock from the allocated shipment
and apply the Delivery Order to its source Sales Order(s).

[ CANCEL ] [ POST DELIVERY ORDER ]
```

Exact service call:

```csharp
await Dos.PostAsync(
[
    new SaDoKeyedRequest
    {
        DoNo = DoNo!,
        RowVersion = _rowVersion
    }
],
_cts.Token);
```

Current RowVersion is mandatory.

Success:

```text
/sales/delivery-orders/view/{DoNo}
```

Failure: remain on the document and show existing posting outcome/error.


## 16. Phase 8 — POSTED DO → Create Invoice

On a saved POSTED DO show:

```text
Next action
[ CREATE INVOICE ]
```

Require Invoice Add permission:

```csharp
AccessRights.CanAsync(
    MenuCodes.SalesInvoice,
    PermissionCodes.Add)
```

Navigate only:

```text
/sales/invoices/new?sourceDo={escaped DoNo}
```

Do not calculate “fully billed” in the DO UI just to enable the button. The Invoice billable-line service is the authority.


## 17. Phase 9 — Invoice bootstrap from known DO

For:

```text
/sales/invoices/new?sourceDo=DO000123
```

### A. Load DO

```csharp
Dos.GetAsync(SourceDoNo)
```

Require source status `POSTED`.

### B. Apply customer using current Invoice behavior

Use source DO `CustCode`, then reuse:

```text
OnCustCodeChanged
→ ApplyCustomerAsync
→ Invoices.GetCustomerDefaultsAsync
→ ApplyDefaults
```

Do not copy DO billing/shipping snapshots into the Invoice.

Do not force Invoice currency to DO currency.

### C. Use existing billable-line search

Call:

```csharp
Dos.SearchBillableLinesAsync(
    new SaDoBillableLinesQuery
    {
        CustCode = CustCode!,
        Currency = Currency,
        SearchText = SourceDoNo,
        Skip = 0,
        Take = ...
    })
```

This existing service already ensures:

- POSTED DO;
- customer compatibility;
- currency compatibility;
- remaining billable qty only.

### D. Reuse mapping

Use:

```text
SaInvoiceLineVm.FromDeliveryOrder(...)
ApplyItemClassificationAsync(...)
```

No second mapper.

### E. Populate unsaved draft

Add all remaining billable lines from that selected DO into the new unsaved Invoice draft.

### F. Fully billed/incompatible

Do not save an empty Invoice. Show a clear message and allow another source/manual path.


## 18. Generic “From Delivery Order” on a blank Invoice

`SearchBillableLinesAsync()` requires a customer, so do not call it with a blank customer.

Use two existing-service stages.

### Stage A — choose a POSTED DO header

```csharp
Dos.SearchAsync(
    new SaDoListQuery
    {
        Status = SaDoStatuses.Posted,
        SearchText = ...,
        Skip = ...,
        Take = ...
    })
```

Existing search fields:

```text
DO No
Customer Code
Customer Name
```

### Stage B — after selection

```text
Dos.GetAsync(doNo)
→ apply its customer through existing Invoice customer logic
→ SearchBillableLinesAsync for selected DO/customer/currency
→ existing AddFromDo mapping
```

Known UI-only limitation: the initial POSTED DO header search may show an already fully-billed DO. That is acceptable; selecting it returns no billable lines and the UI explains why. Do not add backend logic merely to hide it.


## 19. Direct SO → Invoice remains

Keep current direct-SO Invoice capability.

For source-first direct SO Invoice:

```text
Sos.GetAsync(SoNo)
→ apply source customer through existing Invoice customer path
→ Sos.GetBillableLinesAsync(SoNo)
→ SaInvoiceLineVm.FromSalesOrder(...)
```

Preserve current rule:

```text
DO-linked lines and direct-SO lines cannot be mixed.
```


## 20. Invoice stock allocation semantics

The repo already handles DO-linked stock correctly.

For:

```text
LinkDo = true
```

stock was fulfilled through the DO. The Invoice shipment service excludes those lines.

Therefore:

### Pure DO-linked Invoice

```text
Stock
✓ Fulfilled through Delivery Order
```

No primary `Allocate Stock` action.

### Direct/manual stock Invoice

When current page `HasShipmentLines` is true, retain the existing stock allocation action before Post.

Never allocate DO-linked stock twice.


## 21. Phase 10 — single-document Invoice Post

Keep batch Post on `SaInvoiceList`.

Load:

```csharp
CanPostPermission = await AccessRights.CanAsync(
    MenuCodes.SalesInvoice,
    PermissionCodes.Post);
```

UI gate:

```text
saved Invoice
Status = NEW
!_isDirty
!IsSubmitting
CanPostPermission
```

Confirmation:

```text
Post INV000123?

Posting will update the Sales/Accounting transaction
and issue stock for direct stock lines where applicable.

[ CANCEL ] [ POST INVOICE ]
```

Exact service:

```csharp
await Invoices.PostAsync([InvNo!], _cts.Token);
```

Success:

```text
/sales/invoices/view/{InvNo}
```

MyInvois submission remains separate.


## 22. One obvious next action

A small pure-presentation component may be added if it avoids duplication:

```text
SaDocNextActionBar.razor
```

It must contain no business rules.

Examples:

```text
SO saved       → CREATE DELIVERY ORDER
DO saved       → ALLOCATE STOCK
DO allocated   → POST DELIVERY ORDER
DO posted      → CREATE INVOICE
Invoice saved  → POST INVOICE
```

Do not overload the titlebar or sticky Save footer.

Reject button overload such as:

```text
SAVE & CLOSE
SAVE & NEW
SAVE & ALLOCATE
SAVE & POST
SAVE & ALLOCATE & POST
POST & CREATE INVOICE
```

The design should expose one clear next action, not more compound buttons.


## 23. Phase 11 — Quick Add for faster manual SO entry

The full line popup remains.

Add a compact SO Quick Add area:

```text
Item                  Qty       Delivery Date
[ search item       ] [ 1.0000 ] [ 12/10/2026 ]

Price RM 12.50 · MAIN · SST

[ ADD ]
```

Keyboard target:

```text
select item
→ focus Qty
→ Enter/Add
→ line added
→ focus returns to Item
```

This is the biggest manual-entry speed improvement for SO.


## 24. Quick Add must reuse current line logic

Current `SaSo.razor.cs` already has critical logic in:

```text
OnPopupItemSelectedAsync
OnPopupQuantityChangedAsync
ResolvePopupPriceAsync
OnPopupSave
```

It covers:

- item/UOM/pack;
- stock control;
- classification;
- production setup state;
- default warehouse;
- tax;
- pricing engine;
- discounts;
- override permission/reason;
- quantity validation;
- date validation;
- inclusive/exclusive rules;
- allocation floors.

Do not implement a second simplified price/validation engine.

Refactor page-local helper methods only as needed so both:

```text
Full popup
Quick Add
```

call the same initialization/pricing/validation/commit path.

If Quick Add hits a complex condition (missing system price, override required, unusual tax/warehouse/date case), open the existing full editor pre-populated rather than bypassing the rule.

After SO is stable, the same UX concept may be applied to manual DO/Invoice using each page's own existing handlers/models.


## 25. DevExpress binding safety — mandatory

For every added/modified DevExpress editor, obey:

```text
devexpress-blazor-editor-binding-safety
```

Explicit change handler requires matching Expression:

```razor
<DxTextBox Text="@Model.Value"
           TextChanged="@OnValueChanged"
           TextExpression="@(() => Model.Value)" />
```

```razor
<DxSpinEdit Value="@Model.Qty"
            ValueChanged="@OnQtyChanged"
            ValueExpression="@(() => Model.Qty)" />
```

Never use:

```text
@bind-Value + ValueChanged
@bind-Text + TextChanged
```

Never use `ValidationEnabled="false"` just to hide a binding error.


## 26. Progressive disclosure without hiding important data

Keep all existing fields.

Line editor:

```text
BASIC
Item
Qty
Delivery Date
Price

MORE DETAILS
Warehouse
Customer Item
ETD/ETA
Tax
Discount
Remarks
Production info
```

Auto-expand advanced fields when they contain an error or require intervention.

Header primary fields:

```text
SO:      Date, Customer, Customer PO, Payment Term, Salesman, Project
DO:      Date, Customer, Payment Term, Salesman, Project, Ship Via
Invoice: Date, Customer, Payment Term, Salesman, Department, Project, Reference
```

System/status/currency fields stay visible but compact.

Billing/shipping may show a compact summary with an Edit action, but summary values must come from current transaction state. Do not re-read master data just to render the summary.


## 27. Large transaction lookup safety

All new SO/DO source lookup UIs must follow the large-data lookup standard:

- no full history preload;
- server filtering;
- `Skip/Take` or explicitly bounded retrieval;
- debounce around 400–550 ms;
- stale-request protection;
- loading state;
- keyboard-friendly selection;
- popup search row is light;
- active/current transaction selection rules preserved.

Do not use a searchable ComboBox containing every SO/DO.


## 28. Popup safety

For every modified/new `DxPopup`:

```text
CloseOnOutsideClick="false"
```

for data-entry/confirmation flows.

Confirmation order:

```text
Cancel | Primary action
```

Async state-changing actions must:

- disable while processing;
- have handler-level duplicate guards;
- keep errors visible;
- keep action buttons reachable at 1366x768;
- not hide actions below an overflowing popup body.

Audit sibling popups in every touched Razor file.


## 29. Source currency/customer semantics — strict rejection rule

Source-first must behave exactly like the existing manual sequence.

### DO from SO

```text
select SO's customer through DO customer-selection logic
→ current DO defaults are applied
→ select same SO
→ GetRemainingLinesAsync
→ AddFromSo
```

### Invoice from DO

```text
select DO's customer through Invoice customer-selection logic
→ current Invoice defaults are applied
→ search same DO
→ SearchBillableLinesAsync
→ AddFromDo
```

Reject any implementation that directly does:

```text
DO.Currency = SO.Currency
DO.Address = SO.Address
Invoice.Currency = DO.Currency
Invoice.Address = DO.Address
```

The services already validate customer/currency/source compatibility.

If Invoice defaults and source DO currency conflict, show the incompatibility; do not silently overwrite currency.


## 30. Revision and concurrency

Preserve source `CustRel` through existing mapping.

Never replace it with a hardcoded revision.

If the SO is revised/superseded between source selection and save:

- keep the user on page;
- show existing concurrency/source message;
- require reload/reselection;
- never silently remap to the newest revision.

DO entry Post must always use the refreshed `_rowVersion` obtained after Save/Update/Allocate.


## 31. Optional list shortcuts — implement last

After entry workflow is stable:

### SO list

For one suitable selected SO and DO Add permission:

```text
CREATE DO
```

Navigation only.

### DO list

For one selected POSTED DO and Invoice Add permission:

```text
CREATE INVOICE
```

Navigation only.

Do not automatically persist downstream documents from list toolbars.

Do not overcrowd existing action toolbars; follow current `CommonDataGridEx` conventions.


## 32. Implementation order

Implement and test incrementally:

```text
Stage 0  Baseline evidence/build/tests
Stage 1  Stay on document after Save
Stage 2  Cross-document permissions + next-action presentation
Stage 3  SO → DO sourceSo bootstrap
Stage 4  Generic bounded Find SO on blank DO
Stage 5  Rename Add shipment → Allocate Stock
Stage 6  DO entry Post with RowVersion
Stage 7  DO → Invoice sourceDo bootstrap
Stage 8  Generic Invoice From DO / From SO launchers
Stage 9  Invoice entry Post
Stage 10 SO Quick Add
Stage 11 Manual DO/Invoice Quick Add if needed
Stage 12 Header/address/advanced-field progressive disclosure
Stage 13 Optional list shortcuts
```

Compile/test after each stage. Do not deliver this as one giant UI refactor.


## 33. Interaction-count acceptance target

### DO from SO

Current roughly:

```text
New DO
select customer
Add from SO
select SO
select lines
Add selected
Save
list
reopen
Add shipment
list/select
Post
confirm
```

Target:

```text
Create DO
review
Save
Allocate Stock
Post
confirm
```

### Invoice from DO

Current roughly:

```text
New Invoice
select customer
Add from DO
search DO
select lines
Add selected
Save
list/select
Post
confirm
```

Target:

```text
Create Invoice
review
Save
Post
confirm
```

No irreversible business step is hidden or skipped.


## 34. Runtime acceptance matrix

| Scenario | Required result |
|---|---|
| New SO manual | Works as today |
| SO Quick Add simple item | Existing pricing/defaults used |
| SO Quick Add complex price | Full editor/override rule preserved |
| Save new SO | Opens saved SO, not list |
| Update SO | Stays on SO |
| Revise SO | Leaves revise mode correctly |
| Historical/closed SO | No Create DO |
| Dirty SO | Create DO requires Save first |
| NEW SO → Create DO | Customer + eligible remaining lines prepared |
| Partial SHIPPED SO → DO | Only real remaining qty |
| DR-controlled SO | Current DR capacity rules preserved |
| Source revised concurrently | Existing concurrency rule |
| Generic Find SO | Bounded server search |
| Manual DO | Still supported |
| Save new DO | Opens saved DO |
| Dirty saved DO → Allocate | Existing update-then-allocation behavior |
| Insufficient stock | Existing allocation result |
| Incomplete allocation | Post still blocked by service |
| DO Post no permission | UI unavailable + service protected |
| DO Post stale RowVersion | Existing concurrency error |
| DO Post success | View route |
| POSTED DO → Invoice | Customer + billable lines prepared |
| Fully billed DO | Clear no-remaining message |
| DO currency mismatch | Rejected; no silent currency overwrite |
| Pure LinkDo Invoice | No duplicate allocation |
| Direct-stock Invoice | Existing allocation flow |
| Save new Invoice | Opens saved Invoice |
| Invoice Post success | View route |
| MyInvois | Still separate |
| e-Invoice locked | Existing edit lock |
| View mode | No mutation controls |
| Light/dark | Correct |
| 1366x768/mobile | Popups/actions reachable |


## 35. Build, test, and static gates

Run:

```bash
dotnet build ErpWeb.slnx
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj
```

Focus on existing Sales tests around:

```text
SaSoService
SaDoService
SaInvoiceService
SaSoLineReserve
SaSoRevision
SaLinePricing
shipment
posting
SaEInvoice
```

If popup contract tests exist:

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj   --filter "FullyQualifiedName~DxPopupFooterContractTests"
```

Do not weaken backend tests to accommodate a UI refactor.

Static DevExpress audit for changed Razor files:

```text
TextChanged → TextExpression
ValueChanged → ValueExpression
DateChanged → DateExpression
CheckedChanged → CheckedExpression
```

Also verify no `@bind-*` is combined with its matching explicit changed handler.


## 36. Regression rejection gate

Reject implementation if it changes any of:

```text
SO revision lineage / CustRel
Delivery Request behavior
production-demand qty
reservation / over-allocation
pricing / price override
discount / tax
stock allocation
FIFO / lot
future-stock protection
costing
posting / rollback / force close
document numbering
e-Invoice / MyInvois
permissions
tenant scope
optimistic concurrency
```

Reject if it:

- preloads all SOs/DOs;
- filters transaction history only in browser/circuit memory;
- duplicates source mapping;
- duplicates pricing logic;
- force-copies source currency/address snapshots;
- hides validation errors in collapsed UI;
- allows repeated async action execution.


## 37. Definition of Done

The work is complete only when:

- [ ] `SaDocPage` remains the entry shell.
- [ ] No Core/Model/schema behavior changed.
- [ ] SO/DO/Invoice no longer return to list after normal Save.
- [ ] SO revision Save exits revise mode.
- [ ] Saved current SO can Create DO.
- [ ] `sourceSo` bootstrap is one-shot and uses existing DO customer defaults.
- [ ] `GetRemainingLinesAsync` remains the SO→DO quantity authority.
- [ ] `SaDoLineVm.FromSalesOrder` remains the mapper.
- [ ] Generic SO source search is bounded/server-driven.
- [ ] No false Customer PO search promise.
- [ ] New DO uses Save, then Allocate Stock, then Post.
- [ ] Existing `AddShipmentAsync` remains stock allocation authority.
- [ ] DO entry Post sends current RowVersion.
- [ ] Successful DO Post goes to View.
- [ ] POSTED DO can Create Invoice.
- [ ] `sourceDo` bootstrap uses existing Invoice customer-default path.
- [ ] Existing `SearchBillableLinesAsync` remains DO→Invoice eligibility authority.
- [ ] `SaInvoiceLineVm.FromDeliveryOrder` remains mapper.
- [ ] Source currency mismatch is not silently changed.
- [ ] LinkDo lines are not stock-allocated again.
- [ ] Invoice entry Post uses existing service.
- [ ] Successful Invoice Post goes to View.
- [ ] MyInvois remains a separate action.
- [ ] Manual SO/DO/Invoice remain usable.
- [ ] Full line editors remain.
- [ ] Quick Add reuses existing pricing/validation path.
- [ ] DevExpress expressions are correct.
- [ ] Popups are visible/reachable/double-submit safe.
- [ ] Dirty-state protections remain.
- [ ] Build passes.
- [ ] Tests pass.
- [ ] Runtime matrix passes.

## 38. Final approval

After deep review against the current `productionv2` Sales pages, service contracts, repositories, and repository UI skills:

**APPROVED FOR IMPLEMENTATION — 10/10 PLAN REVIEW**

Why this version is implementation-ready:

1. It cuts the largest redundant user interactions.
2. It reuses existing service methods instead of inventing backend behavior.
3. It reuses current source line mapping.
4. It preserves customer-default semantics.
5. It preserves stock/costing/reservation/e-Invoice safeguards.
6. It handles DO RowVersion correctly for entry Post.
7. It avoids the fragile NEW-document Save+Allocate chain.
8. It keeps transaction source lookup server-bounded.
9. It follows the existing Sales `SaDocPage` standard.
10. It defines explicit permission, concurrency, build, runtime and regression gates.

**This file supersedes the earlier Sales SO/DO/Invoice UI simplification plan and is the version the AI coding agent should implement.**
