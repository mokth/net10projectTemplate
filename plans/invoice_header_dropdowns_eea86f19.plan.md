---
name: Invoice header dropdowns
overview: Restructure the sales invoice header to DATE / STATUS / PREFIX / TYPE / INV NO / DO NO, keep customer in a following card, and replace billing/shipping country and state free text with the same IvCodeComboBox lookups used on customer entry.
todos:
  - id: header-layout
    content: Split invoice header into identity card (Date/Status/Prefix/Type/InvNo/DoNo) and customer card; add DoNoDisplay
    status: completed
  - id: address-dropdowns
    content: Load country/state lookups and replace Billing/Shipping free-text fields with IvCodeComboBox
    status: completed
isProject: false
---

# Invoice header and address dropdowns

UI-only change in [`ErpWeb.UI/Sales/Transactions/SaInvoice.razor`](ErpWeb.UI/Sales/Transactions/SaInvoice.razor) and [`SaInvoice.razor.cs`](ErpWeb.UI/Sales/Transactions/SaInvoice.razor.cs). No schema, save, or numbering changes.

## Header layout

Split the current mixed header form into two cards:

**Card 1 — document identity** (2×3 grid, `ColSpanMd="3"`):

- Date: existing `DxDateEdit` / `OnInvDateChanged`
- Status: read-only `StatusDisplay` (`NEW` / `POSTED`, not `OPEN`)
- Prefix: read-only `InvPrefix` (filled from customer defaults; empty until customer selected)
- Type: read-only `"INV"` (no new column)
- Inv no.: existing `InvNoDisplay` (`AUTO` until save)
- DO no.: new `DoNoDisplay` (`AUTO` on new; after load use `doc.DoNo` or `doc.InvNo`)

**Card 2 — customer:** move Customer combo, Customer name, and Currency/rate here unchanged.

Bind `DoNoDisplay` in `ResetNewDocument()` (`"AUTO"`) and `ApplyDocument()`.

## Country / state dropdowns

On Billing and Shipping tabs, replace `DxTextBox` for State and Country with `IvCodeComboBox` (already imported via [`ErpWeb.UI/Sales/_Imports.razor`](ErpWeb.UI/Sales/_Imports.razor)), matching [`SaCustEntry.razor`](ErpWeb.UI/Sales/Masters/SaCustEntry.razor):

- Inject `ISaCustLookupService`
- Load `Countries` via `ListCountriesForAssignmentAsync` and `States` via `ListStatesForAssignmentAsync` in `LoadAsync`
- Bind `InvState` / `InvCountry` and `ShipState` / `ShipCountry`; call `MarkDirtyOnly` on change

Stored values remain codes. Customer defaults already copy those codes, so the combos will select them when they exist in the master lists.

## Out of scope

- PREFIX as an editable dropdown / prefix master
- `OPEN` edit-lock status
- DEPARTMENT / PROJECT ID
- Backend `GetLookupsAsync` / save / numbering
