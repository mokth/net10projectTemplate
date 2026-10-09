# APPROVED FOR IMPLEMENTATION — DevExpress DxGrid Column Readability & Resizing Standardization

**Repository:** `mokth/net10projectTemplate`  
**Target branch:** `productionv2`  
**Verified branch baseline:** `a552ed1f39bfaa786e1904c5037390521b052586`  
**UI framework:** DevExpress Blazor `26.1.4`  
**Target framework:** `.NET 10`  
**Status:** **FINAL REVIEWED — APPROVED FOR IMPLEMENTATION**  
**Implementation readiness:** **10/10**  
**Professional review:** **PASSED**  
**Review date:** `2026-10-09`  
**Current `productionv2` head re-verified:** `a552ed1f39bfaa786e1904c5037390521b052586`  
**Change type:** UI / shared grid infrastructure / Razor presentation only  
**Business-logic risk:** Low, provided the guardrails in this plan are followed

---

# 0. Professional Review Amendments

The original plan was structurally strong and based on the correct `productionv2` branch. This final review keeps its two-layer architecture but closes several implementation gaps that could otherwise reduce reliability.

## 0.1 Issues found and resolved by this review

1. **`MinWidth` typing is now explicit.**  
   DevExpress `DxGridColumn.MinWidth` is an `int` in pixels, while `Width` is a CSS-unit string. Shared render helpers must therefore always supply a concrete `int` for `MinWidth`; they must not pass nullable `int?` directly to the Razor component parameter.

2. **Semantic sizing now has deterministic presets.**  
   The agent no longer has to interpret broad ranges when implementing `GridColumnSize`. Exact default Width/MinWidth pairs are defined in this plan.

3. **Existing explicit narrow Width values can no longer silently defeat semantic sizing.**  
   If a column is migrated to `Size = GridColumnSize.Code`, `Name`, etc., the old narrow `Width` must be removed unless it is an intentional page-specific override.

4. **Unitless width values are explicitly audited.**  
   The repository contains direct-grid patterns such as `Width="110"` as well as normal CSS-unit values such as `Width="110px"`. Since `Width` is a CSS-unit value, touched grid widths must use an explicit unit (`px`, `%`, etc.). `MinWidth` remains an integer pixel value.

5. **Layout v2 migration is now deterministic and one-time.**  
   The first legacy load strips widths, strips non-persisted filter criteria, immediately saves the migrated layout under `:v2`, and thereafter loads v2 widths intact.

6. **Stable `GridKey` is now a precondition for persisted widths.**  
   Shared grids involved in this change must use a non-empty, stable and unique `GridKey`. Do not rely on a title-derived fallback for long-term width persistence.

7. **Direct `DxGrid` persistence scope is constrained.**  
   Direct entry/popup/workflow grids become readable and resizable, but this task does not introduce a new persistent-layout subsystem for every direct grid unless that grid already has one. This prevents unnecessary state-management risk.

8. **Template-cell tooltip limitations are handled explicitly.**  
   DevExpress ellipsis hover tooltips work for ordinary data cells, but template cells containing block-level markup may not get the same built-in tooltip behaviour. Long-text template cells must be checked individually.

9. **Build/test gates are repository-specific.**  
   The verified solution is `ErpWeb.slnx`, and `ErpWeb.Tests` already references `ErpWeb.UI`; focused pure helper tests can therefore be added without creating a new test project.

## 0.2 Final review decision

With the amendments below, the plan is concrete enough for a capable AI coding agent to implement without inventing grid behaviour, changing business logic, or relying on ambiguous sizing rules.

**Professional review score: 10/10 — APPROVED.**

---

# 1. Objective

Fix the system-wide poor grid readability in `productionv2`.

Current user experience:

- many grid columns are too narrow;
- ordinary values are shown as `...`;
- users cannot comfortably read item codes, document numbers, customer/vendor names, references, descriptions, machine/process/work-centre values, etc.;
- some direct `DxGrid` instances are not resizable at all;
- some shared grid layouts discard user-resized widths when the page is reloaded.

Required end state:

1. Normal ERP values should be readable without unnecessary truncation.
2. Only genuinely long text fields such as Description, Remarks, Notes, Address and similar free text should normally truncate.
3. Long-text columns must start at a useful width and remain user-resizable.
4. All appropriate desktop `DxGrid` grids must support column resizing.
5. User-resized widths in shared list grids must persist.
6. Existing saved layouts must not reintroduce today's bad narrow widths.
7. Mobile list/card behaviour must remain unchanged.
8. Posting, rollback, costing, stock, sales, purchasing, production and other business logic must not be modified.

---

# 2. Verified Repository Findings

The following findings were verified directly against `productionv2`.

## 2.1 Shared grid infrastructure

Primary files:

- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGrid.razor`
- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGrid.razor.cs`
- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor`
- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor.cs`
- `ErpWeb.UI/Components/Common/DataGrid/DataGridModel.cs`
- `ErpWeb.UI/Components/Common/DataGrid/GridModels.cs`
- `ErpWeb.UI/Components/Common/DataGrid/AuditColumns.cs`
- `ErpWeb.UI/Components/Common/DataGrid/IGridLayoutStorage.cs`

`CommonDataGridEx` currently uses:

```razor
ColumnResizeMode="GridColumnResizeMode.ColumnsContainer"
TextWrapEnabled="false"
```

Its generated columns use:

```razor
Width="@col.Width"
```

`GridColumnData` currently contains:

```csharp
public string? Width { get; set; }
```

but has no shared semantic sizing policy and no shared `MinWidth`.

`GridColumnDefinition` has the same basic limitation.

## 2.2 Persisted layout problem

`CommonDataGridEx.razor.cs` currently loads a persisted layout through:

```csharp
e.Layout = StripColumnWidths(layout);
```

This deliberately removes saved widths.

Effect:

- the user manually resizes a column;
- the layout is saved;
- on a later load, the width is removed;
- the grid falls back to the page's narrow defaults.

This must be corrected.

## 2.3 Examples of narrow direct grids already verified

Examples include, but are not limited to:

### Inventory

- `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor`
- `IvMiscIssue.razor`
- `IvMiscReceipt.razor`
- `IvScrap.razor`
- `IvStockAdjustment.razor`
- `IvStockCount.razor`
- `IvStockReturn.razor`
- `IvStockTransfer.razor`
- `IvVendorReturn.razor`
- inventory lookup grids
- inventory inquiry grids

Many columns are approximately `56px` to `130px` while text wrapping is disabled.

### Sales

- `ErpWeb.UI/Sales/Transactions/SaSo.razor`
- `SaQt.razor`
- `SaDo.razor`
- `SaInvoice.razor`
- `SaCdn.razor`
- `SaDeliveryRequestEntry.razor`
- customer/master grids
- customer lookup grids
- sales analysis grids

### Purchase

- `ErpWeb.UI/Purchase/Transactions/PoOrder.razor`
- `PoPr.razor`
- `PoInvoice.razor`
- `PoCdn.razor`
- `PoSbInvoice.razor`
- `PoSbCdn.razor`
- supplier/master grids
- purchasing lookup grids
- procurement inquiry grids

### Planning / Production

- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor`
- `PrMaterialIssueEntry.razor`
- `PrDailyProductionEntry.razor`
- `PrFinishedGoodReceiptEntry.razor`
- `PrMaterialConsumeVarianceInquiry.razor`
- `PrBomExplorer.razor`
- `PrDeliveryRequestSearchPopup.razor`
- maintenance grids

`PrWorkOrderEntry.razor` is particularly important because it contains many direct grids and many columns around `55px` to `110px`.

## 2.4 Shared list examples verified

The list pages use `CommonDataGridEx`, for example:

- `Inventory/Transactions/IvGoodsReceiptList.razor`
- `Sales/Transactions/SaSoList.razor`
- `Purchase/Transactions/PoOrderList.razor`
- `Planning/WorkOrders/PrWorkOrderList.razor`

Their column definitions are normally in the corresponding `.razor.cs`.

Example from Sales Order List:

```csharp
new() { Caption = "SO No.", Width = "140px" }
new() { Caption = "Customer", Width = "120px" }
new() { Caption = "Name" } // no explicit useful width
new() { Caption = "Customer PO", Width = "160px" }
```

The concept is usable, but the system lacks a common sizing contract.

---

# 3. Non-Negotiable UX Rules

The implementation MUST follow these rules.

## 3.1 Do not enable wrapping everywhere

Do **not** solve the problem by globally setting:

```razor
TextWrapEnabled="true"
```

ERP grids become excessively tall and harder to scan.

Default behaviour remains:

```razor
TextWrapEnabled="false"
```

for normal dense desktop ERP grids.

## 3.2 Normal business data must be readable

Examples that must normally fit without ellipsis:

- document number;
- item code;
- customer/vendor code;
- warehouse;
- location;
- status;
- UOM;
- work centre;
- process;
- machine;
- operator;
- normal reference number;
- normal customer PO;
- quantities;
- prices;
- costs;
- amounts;
- dates.

## 3.3 Long text may truncate

These fields may use a controlled width and ellipsis:

- Description;
- Remarks;
- Notes;
- Address;
- free-text Reason;
- Comments;
- other genuinely variable-length text.

They must still receive a useful default width.

## 3.4 Horizontal scrolling is acceptable

When a business grid genuinely contains many columns, it is better to provide readable columns plus horizontal scrolling than to compress every column until the values are unreadable.

Do not shrink important columns merely to force the entire grid into one viewport.

## 3.5 User resizing must remain available

Desktop users must be able to resize appropriate grid columns.

---

# 4. Approved Column Sizing Standard

Use the following as the ERP baseline.

| Column meaning | Default width | Minimum guidance |
|---|---:|---:|
| Selection / checkbox | 48–60px | 48px |
| Row action / icon | based on action count | do not squeeze icons |
| Sequence / revision / small integer | 70–90px | 65px |
| Boolean | 80–95px | 70px |
| UOM | 80–100px | 80px |
| Status | 100–120px | 95px |
| Percentage | 95–115px | 90px |
| Date | 110–125px | 105px |
| Date + time | 140–165px | 135px |
| Quantity | 105–125px | 100px |
| Price / cost / amount | 120–145px | 110px |
| Warehouse / location | 120–150px | 110px |
| Item / product code | 145–170px | 135px |
| Customer / vendor code | 145–170px | 135px |
| Document number | 145–180px | 135px |
| Work centre / process / machine | 145–180px | 135px |
| Normal reference | 160–210px | 145px |
| Customer PO / vendor ref | 170–220px | 150px |
| Person / buyer / sales rep name | 160–220px | 145px |
| Customer / vendor name | 220–280px | 190px |
| Item / product description | 240–320px | 210px |
| Remarks / notes / long text | 280–360px | 220px |
| Address / very long text | 320–420px | 240px |

These are defaults, not rigid laws.

The target page's domain meaning remains authoritative.

---

# 5. Implementation Architecture

The fix must use **two layers**.

## Layer A — Shared list-grid infrastructure

Fix:

- `CommonDataGridEx`
- `CommonDataGrid`
- shared column models
- shared persisted-layout behaviour
- shared audit-column widths

This resolves a large portion of list/master/inquiry screens centrally.

## Layer B — Direct `DxGrid` audit

Audit every direct `<DxGrid>` under:

- `ErpWeb.UI/Admin`
- `ErpWeb.UI/Inventory`
- `ErpWeb.UI/Sales`
- `ErpWeb.UI/Purchase`
- `ErpWeb.UI/Planning`
- shared components that contain business grids

Apply the same sizing rules explicitly.

Do not attempt to replace every direct `DxGrid` with `CommonDataGridEx`.

Entry-line grids, popup grids and workflow grids are intentionally allowed to remain direct `DxGrid`.

---

# 6. Phase 1 — Create Shared Grid Sizing Contract

## 6.1 Add a shared sizing file

Create:

`ErpWeb.UI/Components/Common/DataGrid/GridColumnSizing.cs`

Recommended structure:

```csharp
namespace ErpWeb.UI.Components.Common.DataGrid;

public enum GridColumnSize
{
    Auto,
    Tiny,
    Small,
    Status,
    Date,
    DateTime,
    Quantity,
    Percent,
    Amount,
    Code,
    DocumentNo,
    Reference,
    Name,
    Description,
    LongText
}

public static class GridColumnSizing
{
    // Central width/min-width resolution.
}
```

The enum names above are the approved baseline. Do not rename them without a compile-time reason.

### 6.1.1 Exact shared preset map

Implement these deterministic defaults:

| `GridColumnSize` | Effective `Width` | Effective `MinWidth` |
|---|---:|---:|
| `Auto` | `null` | `50` |
| `Tiny` | `72px` | `65` |
| `Small` | `95px` | `80` |
| `Status` | `115px` | `95` |
| `Date` | `120px` | `105` |
| `DateTime` | `155px` | `135` |
| `Quantity` | `120px` | `100` |
| `Percent` | `105px` | `90` |
| `Amount` | `135px` | `110` |
| `Code` | `160px` | `135` |
| `DocumentNo` | `165px` | `135` |
| `Reference` | `190px` | `145` |
| `Name` | `240px` | `190` |
| `Description` | `280px` | `210` |
| `LongText` | `330px` | `220` |

Use `Small` for compact values such as UOM or short revision fields. Use `Code` for item/product/customer/vendor/warehouse/location/work-centre/process/machine codes unless a more specific profile applies.

`Auto` intentionally preserves DevExpress automatic width calculation while retaining the DevExpress minimum-width baseline.

## 6.2 Preserve explicit overrides

Existing page definitions must continue to work.

Resolution precedence:

1. explicit page `Width`;
2. semantic `GridColumnSize`;
3. existing DevExpress/default auto behaviour.

Do not break existing `Width = "..."` definitions.

## 6.3 Extend `GridColumnData`

Modify:

`ErpWeb.UI/Components/Common/DataGrid/DataGridModel.cs`

Add:

```csharp
public GridColumnSize Size { get; set; } = GridColumnSize.Auto;
public int? MinWidth { get; set; }
```

`MinWidth` is nullable in the model only so a page can distinguish "no explicit override" from an explicit minimum. The rendered DevExpress parameter must still receive a concrete `int`.

## 6.4 Extend `GridColumnDefinition`

Modify:

`ErpWeb.UI/Components/Common/DataGrid/GridModels.cs`

Add equivalent properties:

```csharp
public GridColumnSize Size { get; set; } = GridColumnSize.Auto;
public int? MinWidth { get; set; }
```

Use the same resolver contract as `GridColumnData`; do not implement a second set of width constants.

## 6.5 Add helper resolution

Provide typed helpers similar to:

```csharp
string? GetEffectiveWidth(GridColumnData column);
int GetEffectiveMinWidth(GridColumnData column);

string? GetEffectiveWidth(GridColumnDefinition column);
int GetEffectiveMinWidth(GridColumnDefinition column);
```

Rules:

- explicit `Width` wins;
- explicit `MinWidth` wins;
- otherwise use the exact preset in section 6.1.1;
- `Auto` returns `null` Width and `50` MinWidth;
- do not return nullable `int?` from the renderer-facing MinWidth helper;
- do not convert `MinWidth` to a CSS string.

Do not hard-code field-name guessing inside the shared component.

The page/domain definition should state the semantic column type.

### 6.5.1 Critical migration rule

When converting an existing column from an explicit narrow width to a semantic size, remove the obsolete Width unless it is intentionally overriding the profile.

Bad:

```csharp
new() {
    FieldName = nameof(Row.CustomerCode),
    Size = GridColumnSize.Code,
    Width = "120px" // defeats the Code preset
}
```

Correct:

```csharp
new() {
    FieldName = nameof(Row.CustomerCode),
    Size = GridColumnSize.Code
}
```

or, for a proven page-specific exception:

```csharp
new() {
    FieldName = nameof(Row.CustomerCode),
    Size = GridColumnSize.Code,
    Width = "175px"
}
```

---

# 7. Phase 2 — Update Shared Grid Rendering

## 7.1 `CommonDataGridEx.razor`

Change generated `DxGridDataColumn` instances to use:

```razor
Width="@GetEffectiveWidth(col)"
MinWidth="@GetEffectiveMinWidth(col)"
```

`GetEffectiveMinWidth` must return `int`, not `int?`. This is a compile-safety requirement because `DxGridColumn.MinWidth` is an integer pixel parameter.

Apply this consistently to:

- date;
- time;
- string;
- stringicon;
- stringicon2;
- stringiconlink;
- link;
- click;
- decimal;
- double;
- int;
- bool;
- default.

Do not leave one data-type branch using the old behaviour.

Keep:

```razor
TextWrapEnabled="false"
ColumnResizeMode="GridColumnResizeMode.ColumnsContainer"
```

unless runtime testing proves a specific shared-grid scenario needs a documented exception.

## 7.2 `CommonDataGrid.razor`

Apply equivalent effective width/min-width handling.

Keep its existing selection/action behaviour.

Do not change export, sorting, grouping or selection logic.

---

# 8. Phase 3 — Correct Persisted Grid Width Behaviour

This phase is mandatory.

## 8.1 Current problem

`CommonDataGridEx` currently calls:

```csharp
StripColumnWidths(layout)
```

when loading.

That makes user width customization non-persistent.

## 8.2 Introduce layout schema version 2

Do not simply start loading all old widths.

Old local-storage layouts may already contain undesirable widths.

Implement a one-time safe migration.

Recommended concept:

```text
legacy key:
erp-grid-layout:{grid-key}

new key:
erp-grid-layout:{grid-key}:v2
```

The actual versioning can be implemented inside the component before it calls `IGridLayoutStorage`.

### Load algorithm

1. Derive two keys from the same stable base `GridKey`:
   - legacy key = current `LayoutKey`;
   - active key = `${LayoutKey}:v2`.
2. Try the `v2` key.
3. If found:
   - load it **with widths intact**;
   - do not call `StripColumnWidths`.
4. If `v2` does not exist:
   - load the legacy layout;
   - if legacy exists, call `StripColumnWidths` **once**;
   - also apply the existing filter-criteria stripping rule before writing the migrated layout;
   - preserve sort / visibility / order / grouping state;
   - set `e.Layout` to the migrated layout;
   - **immediately save the migrated layout to the `v2` key** so every subsequent load is deterministic.
5. If no layout exists:
   - use the corrected page defaults and let future layout saves use only the `v2` key.

Do not delete the legacy key during normal first migration; it is harmless once v2 exists and can be useful during rollback. `RESET LAYOUT` is the operation that clears both.

### Save algorithm

For `v2`:

- preserve width;
- preserve column order;
- preserve visibility;
- preserve sort/group state;
- continue removing filter criteria if current design requires it.

Do **not** call `StripColumnWidths()` for a normal `v2` load.

## 8.3 Reset Layout behaviour

`RESET LAYOUT` must clear:

- the active `v2` key; and
- the legacy key when needed to prevent the old layout from being immediately re-imported.

After reset, the grid must return to the new ERP width defaults.

### 8.3.1 Stable `GridKey` requirement

Before enabling v2 persistence on a `CommonDataGridEx` page:

- `GridKey` must be non-empty;
- it must be unique within the ERP;
- it must remain stable across caption/title changes;
- do not use the title-derived fallback as the long-term persisted key for pages touched by this task.

If a touched page currently omits `GridKey`, add an explicit deterministic key as part of this task.

## 8.4 Apply equivalent protection to `CommonDataGrid`

`CommonDataGrid` also persists layouts.

Make its **load/save versioning semantics** consistent so old narrow layouts do not override the new standard.

Do not add a new visible Reset Layout toolbar to `CommonDataGrid` solely for this task if the component does not already expose one. The mandatory visible Reset Layout behaviour applies to `CommonDataGridEx`, where the repository already has that action.

Prefer extracting small shared helper/key logic if doing so avoids duplicated migration code without creating a broad refactor.

---

# 9. Phase 4 — Standardize Shared Audit Columns

Modify:

`ErpWeb.UI/Components/Common/DataGrid/AuditColumns.cs`

Current widths include:

- Created: `140px`
- User ID: `110px`
- Modified Date: `140px`
- Modified By: `110px`

Recommended update:

- Created: ~`150px`
- User ID: ~`130–140px`
- Modified Date: ~`150px`
- Modified By: ~`130–140px`

Also apply appropriate semantic size/min-width values.

Reason:

audit users often have values longer than 110px, and these columns currently participate in the same readability problem.

---

# 10. Phase 5 — Migrate `CommonDataGridEx` Column Definitions

Audit every `GridColumnData` definition in `.razor.cs`.

Start with transaction lists and high-use pages.

## Verified priority pages

### Sales

- `SaSoList.razor.cs`
- `SaQtList.razor.cs`
- `SaDoList.razor.cs`
- `SaInvoiceList.razor.cs`
- `SaCdnList.razor.cs`
- `SaDeliveryRequestList.razor.cs`

### Purchase

- `PoOrderList.razor.cs`
- `PoPrList.razor.cs`
- `PoInvoiceList.razor.cs`
- `PoCdnList.razor.cs`
- self-bill list pages

### Inventory

- `IvGoodsReceiptList.razor.cs`
- `IvMiscIssueList.razor.cs`
- `IvMiscReceiptList.razor.cs`
- `IvScrapList.razor.cs`
- `IvStockAdjustmentList.razor.cs`
- `IvStockCountList.razor.cs`
- `IvStockReturnList.razor.cs`
- `IvStockTransferList.razor.cs`
- `IvVendorReturnList.razor.cs`

### Production

- `PrWorkOrderList.razor.cs`
- `PrMaterialIssueList.razor.cs`
- `PrDailyProductionList.razor.cs`
- `PrFinishedGoodReceiptList.razor.cs`

Then continue through:

- master lists;
- inquiry grids using `CommonDataGridEx`;
- monitor grids;
- maintenance grids;
- admin grids.

## 10.1 Example target adjustment — Sales Order List

Current style:

```csharp
new() { Caption = "SO No.", Width = "140px" }
new() { Caption = "Customer", Width = "120px" }
new() { Caption = "Name" }
new() { Caption = "Customer PO", Width = "160px" }
```

Approved target concept:

```csharp
new() { Caption = "SO No.", Size = GridColumnSize.DocumentNo, ... }
new() { Caption = "Customer", Size = GridColumnSize.Code, ... }
new() { Caption = "Name", Size = GridColumnSize.Name, ... }
new() { Caption = "Customer PO", Size = GridColumnSize.Reference, ... }
```

Explicit width is still allowed where the page needs an exception.

Do not mechanically convert every column to the same preset.

---

# 11. Phase 6 — Audit Every Direct `DxGrid`

This is the largest UI pass.

The coding agent must perform a repository scan on the current `productionv2` head before editing.

Search for:

```text
<DxGrid
<DxGridDataColumn
<DxGridCommandColumn
<DxGridSelectionColumn
TextWrapEnabled=
ColumnResizeMode=
Width=
MinWidth=
```

Create a temporary working inventory grouped by:

- module;
- file;
- grid purpose;
- direct/shared;
- resizable yes/no;
- text wrapping yes/no;
- risky narrow text columns.

Do not rely only on the examples in this plan.

---

# 12. Direct `DxGrid` Rules

## 12.1 Enable resizing

For normal desktop direct grids, add:

```razor
ColumnResizeMode="GridColumnResizeMode.ColumnsContainer"
```

where it is missing.

Exceptions are allowed only when:

- the grid is intentionally tiny;
- the grid is a very small fixed structural matrix;
- resizing would damage a special interactive layout.

Any exception must be documented in the implementation summary.

## 12.2 Keep dense rows

Normally keep:

```razor
TextWrapEnabled="false"
```

for transaction, list, lookup and inquiry grids.

## 12.3 Fix narrow text columns

Do not leave meaningful text columns at sizes such as:

```text
55px
56px
70px
80px
90px
100px
110px
```

unless their content is genuinely tiny, for example:

- selector;
- revision;
- sequence;
- UOM;
- small numeric flag;
- icon/action.

## 12.4 Normalize `Width` units

`DxGridColumn.Width` is a CSS-unit string. During the direct-grid audit, flag unitless values such as:

```razor
Width="110"
Width="90"
```

Touched widths must use an explicit CSS unit, normally:

```razor
Width="110px"
Width="90px"
```

Percentage widths may remain percentages where they are intentional.

Do **not** append `px` to `MinWidth`; `MinWidth` is an integer pixel value.

## 12.5 Direct-grid persistence scope

Do not introduce persistent layout storage to every direct transaction/popup/workflow `DxGrid` in this task.

For direct grids, the required behaviour is:

- sensible readable defaults;
- appropriate `MinWidth`;
- runtime column resizing;
- horizontal scrolling when necessary.

Persist widths only where a direct grid already has an established layout persistence mechanism. Shared `CommonDataGridEx` width persistence is mandatory.

## 12.6 Use `MinWidth`

For important direct columns, use appropriate `MinWidth` so a column cannot be collapsed to an unusable size.

Example concept:

```razor
<DxGridDataColumn FieldName="..."
                  Caption="Item"
                  Width="155px"
                  MinWidth="135" />
```

## 12.7 Long fields

Example:

```razor
<DxGridDataColumn FieldName="..."
                  Caption="Description"
                  Width="280px"
                  MinWidth="210" />
```

Long text can still show ellipsis when its content exceeds the column.

Users can resize wider if needed.

---

# 13. High-Priority Direct Grids

These should be handled before low-use grids.

## 13.1 Sales transaction entries

- `SaSo.razor`
- `SaQt.razor`
- `SaDo.razor`
- `SaInvoice.razor`
- `SaCdn.razor`
- `SaDeliveryRequestEntry.razor`

Prioritize:

- Item;
- Description;
- UOM;
- warehouse;
- tax;
- reference;
- customer PO;
- source documents;
- shipment references.

## 13.2 Purchase transaction entries

- `PoOrder.razor`
- `PoPr.razor`
- `PoInvoice.razor`
- `PoCdn.razor`
- `PoSbInvoice.razor`
- `PoSbCdn.razor`

Prioritize:

- Item;
- Description;
- vendor/reference;
- UOM;
- delivery date;
- tax;
- price/amount;
- source document fields.

## 13.3 Inventory transaction entries

- `IvGoodsReceipt.razor`
- `IvMiscIssue.razor`
- `IvMiscReceipt.razor`
- `IvScrap.razor`
- `IvStockAdjustment.razor`
- `IvStockCount.razor`
- `IvStockReturn.razor`
- `IvStockTransfer.razor`
- `IvVendorReturn.razor`

Prioritize:

- Item;
- Description;
- lot;
- warehouse/location;
- status;
- reference;
- UOM;
- quantity.

## 13.4 Production / Work Order

- `PrWorkOrderEntry.razor`
- `PrMaterialIssueEntry.razor`
- `PrDailyProductionEntry.razor`
- `PrFinishedGoodReceiptEntry.razor`

`PrWorkOrderEntry.razor` requires careful manual review because it contains multiple grids for different production levels.

Do not apply one blanket width to all of them.

Classify separately:

- Work Centre;
- Process;
- BOM;
- Machine;
- Labour;
- delivery-request/source grids;
- scheduling-related grids.

Production traceability columns must be readable:

- SO;
- DR;
- Work Order;
- Product;
- Definition;
- Work Centre;
- Process;
- Machine.

---

# 14. Lookup Popup Rules

Verified examples include:

- `IvStockMasterSearchPopup.razor`
- `IvBalLocSearchPopup.razor`
- `SaCustomerSearchPopup.razor`
- `PoSupplierSearchPopup.razor`
- `PoPurchasingItemSearchPopup.razor`
- `PrDeliveryRequestSearchPopup.razor`

Requirements:

1. popup width must remain viewport-safe;
2. grid can horizontally scroll;
3. Code must be readable;
4. Name/Description must receive the largest useful space;
5. small supporting fields can stay compact;
6. add column resizing if appropriate;
7. do not expand the popup beyond mobile/desktop viewport constraints merely to avoid scrolling.

---

# 15. Inquiry / Analysis Grid Rules

Do not force analytical grids into narrow equal columns.

Examples:

- sales analysis;
- procurement delivery performance;
- stock inquiries;
- production variance;
- maintenance summaries.

Numeric KPI columns can be compact.

Dimension text must be wider:

- Item;
- Category;
- Customer;
- Vendor;
- Warehouse;
- Process;
- Machine.

---

# 16. Responsive Behaviour

This work must not damage the existing mobile patterns.

## Shared transaction lists

Pages such as `SaSoList.razor` already use:

```text
iv-list-desktop
iv-list-compact
```

Do not remove or redesign that behaviour.

The grid-width improvement applies primarily to desktop.

## Entry grids

At narrow viewport widths:

- allow grid horizontal scrolling;
- do not shrink fonts;
- do not wrap every cell;
- keep action controls usable;
- do not cause the whole page body to overflow because of an incorrectly fixed container width.

DevExpress `ColumnsContainer` mode can show a horizontal scrollbar when the sum of column widths/minimums exceeds the grid container. Prefer the grid's own scrolling behaviour. Do not add a global `overflow-x` rule to every `.dxbl-grid`.

If a specific page clips the grid or scrollbar because of its surrounding flex/grid container, fix only that page/container (for example `min-width: 0; width: 100%`) and verify both themes.

---

# 17. Long-Text Discoverability

For cells intentionally allowed to truncate:

- preserve a clear way to access the full value;
- use existing DevExpress behaviour if it already exposes the full text appropriately;
- otherwise use an unobtrusive `title`/tooltip pattern on the specific long-text cell.

Do not add heavyweight popups for every Description cell.

Do not add tooltip logic to numeric/date cells.

Important: do not assume DevExpress's built-in ellipsis tooltip covers every templated cell. For `CellDisplayTemplate` content that uses block-level markup (for example `<div>`) or button/link templates, verify truncation manually. If a long-text templated cell does not expose its full value, add a lightweight `title`/tooltip only to that specific template.

---

# 18. Do Not Add Automatic Best-Fit on Every Render

Do **not** call an auto-fit operation every time a grid renders or refreshes.

Reasons:

- long Remarks/Description values can create absurdly wide columns;
- server/custom grids may not have all records materialized;
- repeated auto-fit can cause layout jumping;
- it can fight user-resized widths.

The approved solution is:

- good defaults;
- sensible minimums;
- user resize;
- persisted widths.

A separate optional "Best Fit" feature can be considered later if users request it.

It is **not required for this implementation**.

---

# 19. Update the AI UI Skill to Prevent Regression

Modify:

`.agents/skills/erp-ui-core/skill.md`

Add a section similar to:

## Grid readability contract

Mandatory rules:

- normal text must be readable without unnecessary ellipsis;
- direct desktop DxGrid should normally be resizable;
- keep dense grids no-wrap unless a documented exception exists;
- use semantic shared sizing for `CommonDataGridEx`;
- Code/Document/Name/Description/Reference must use the ERP sizing standard;
- only genuinely long text should normally truncate;
- use horizontal scrolling rather than compressing all business columns;
- respect persisted user widths;
- do not introduce text-bearing 70–100px columns without domain justification;
- inspect mobile behaviour separately.

Also update the Definition of Done:

```text
[ ] Grid columns were checked for readable default widths.
[ ] Direct DxGrid columns are resizable where appropriate.
[ ] Long-text truncation is intentional, not accidental.
[ ] User-resized shared-grid widths survive reload.
```

Do not duplicate the whole policy into every module skill unless a module needs a real exception.

The shared `erp-ui-core` skill should remain authoritative.

---

# 20. Business Logic Guardrails

The coding agent MUST NOT modify business behaviour as part of this task.

Forbidden unrelated changes include:

- posting;
- rollback;
- costing;
- FIFO / stock costing;
- inventory movement;
- available stock;
- future-stock rules;
- sales fulfilment;
- purchasing matching;
- e-Invoice logic;
- production BOM calculation;
- labour/machine costing;
- work-order scheduling;
- delivery-request allocation;
- permission logic;
- document status transitions;
- database schema unrelated to UI layout;
- service queries merely to make a column wider.

No entity/database migration is required.

No business DTO field should be added only for grid sizing.

---

# 21. Expected Changed File Groups

## Mandatory shared files

Expected:

```text
ErpWeb.UI/Components/Common/DataGrid/GridColumnSizing.cs        NEW
ErpWeb.UI/Components/Common/DataGrid/DataGridModel.cs
ErpWeb.UI/Components/Common/DataGrid/GridModels.cs
ErpWeb.UI/Components/Common/DataGrid/CommonDataGrid.razor
ErpWeb.UI/Components/Common/DataGrid/CommonDataGrid.razor.cs
ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor
ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor.cs
ErpWeb.UI/Components/Common/DataGrid/AuditColumns.cs
.agents/skills/erp-ui-core/skill.md
ErpWeb.Tests/UI/GridColumnSizingTests.cs                         NEW (recommended)
```

`IGridLayoutStorage.cs` should only change if a small storage helper is genuinely required.

Avoid expanding its contract unless necessary.

## Page-level files

Expect `.razor.cs` list-column definition changes and direct `.razor` grid changes throughout:

```text
ErpWeb.UI/Admin
ErpWeb.UI/Inventory
ErpWeb.UI/Sales
ErpWeb.UI/Purchase
ErpWeb.UI/Planning
```

Do not modify unrelated `.razor.cs` business handlers.

---

# 22. Focused Automated Tests

Because `ErpWeb.Tests` already references `ErpWeb.UI`, add focused tests for the pure sizing resolver.

Recommended file:

`ErpWeb.Tests/UI/GridColumnSizingTests.cs`

Minimum cases:

1. `Auto` => Width `null`, MinWidth `50`.
2. every semantic profile returns the exact Width/MinWidth pair in section 6.1.1.
3. explicit `Width` overrides semantic Width.
4. explicit `MinWidth` overrides semantic MinWidth.
5. Width output uses valid CSS-unit strings for presets.
6. the renderer-facing MinWidth resolver returns `int`.

If layout-key/version logic is extracted into a pure helper, also test:

- legacy key generation;
- `:v2` key generation;
- empty-key guard behaviour.

Do not attempt to unit-test Razor rendering through a new UI test framework as part of this task. Build + targeted runtime smoke testing is the correct gate for the `.razor` files.

---

# 23. Implementation Sequence

The AI coding agent should follow this order.

## Step 1 — Reconfirm branch

Before editing:

```bash
git status
git branch --show-current
git rev-parse HEAD
```

Required branch:

```text
productionv2
```

If head has advanced from the baseline in this plan, re-scan changed grid files before implementation.

## Step 2 — Full grid inventory

Scan all target UI folders.

Recommended commands:

```bash
rg -l '<DxGrid\b' ErpWeb.UI -g '*.razor'
rg -n 'Width="\d+"' ErpWeb.UI -g '*.razor'
rg -n '<CommonDataGridEx\b' ErpWeb.UI -g '*.razor'
```

Produce a working checklist that includes:

- every direct `DxGrid` file;
- every shared `CommonDataGridEx` use;
- any unitless Width value;
- any missing/unstable `GridKey` on a shared persisted grid.

## Step 3 — Shared sizing model

Implement `GridColumnSizing`.

Compile.

## Step 4 — Shared component rendering

Update `CommonDataGrid` and `CommonDataGridEx`.

Compile.

## Step 5 — Persisted layout v2 migration

Implement versioned layout behaviour.

Compile.

## Step 6 — Shared audit columns

Update `AuditColumns`.

Compile.

## Step 7 — Shared list definitions

Update high-use transaction lists first.

Build and inspect.

## Step 8 — Direct transaction grids

Sales → Purchase → Inventory → Production.

Production `PrWorkOrderEntry` receives dedicated review.

## Step 9 — Lookups / inquiries / masters / admin / analysis

Complete the repository scan.

## Step 10 — Skill update

Update `erp-ui-core`.

## Step 11 — Build + static audit + runtime smoke test

Do not declare complete before all gates pass.

---

# 24. Build and Verification Gates

## 24.1 Compile

Required repository build:

```bash
dotnet build ErpWeb.slnx
```

Then run the existing test project:

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj
```

The full solution build must succeed with zero new compile errors and the existing test suite must not regress.

Pay special attention to:

- `MinWidth` parameter type;
- Razor enum references;
- generic shared component rendering;
- nullable width values.

## 24.2 Static grid audit

After changes, re-scan all direct `<DxGrid>` instances and all shared-grid usages.

Also verify:

```bash
rg -n 'Width="\d+"' ErpWeb.UI -g '*.razor'
```

Any remaining unitless grid Width must be fixed or documented as not belonging to a DevExpress grid column.

Flag any remaining direct text-bearing column narrower than approximately `110px`.

Each flagged column must be manually classified as:

```text
VALID COMPACT COLUMN
```

or fixed.

Examples of valid compact columns:

- selection;
- action;
- sequence;
- revision;
- UOM;
- boolean;
- short status where tested;
- small quantity/percentage where appropriate.

Do not accept a remaining narrow:

- Item;
- Description;
- Customer;
- Vendor;
- Document No;
- Reference;
- Warehouse;
- Machine;
- Process;
- Work Centre;

without explicit justification.

For shared persisted grids, also verify every touched `CommonDataGridEx` has a stable explicit `GridKey`.

---

# 25. Runtime Smoke-Test Matrix

At minimum test these representative pages.

## Sales

- Sales Order List
- Sales Order Entry
- Sales Invoice Entry
- Delivery Request Entry
- Customer lookup

## Purchase

- PO List
- PO Entry
- Purchase Invoice Entry
- Supplier lookup

## Inventory

- Goods Receipt List
- Goods Receipt Entry
- Stock Transfer
- Stock Count
- Item lookup

## Production

- Work Order List
- Work Order Entry
- Material Issue
- Daily Production
- DR lookup

## Admin / master

- one `CommonDataGridEx` master list;
- one direct `DxGrid` admin/master page.

---

# 26. Runtime Acceptance Scenarios

For each representative grid, verify:

### Default opening

- normal codes are readable;
- normal names are readable;
- document numbers are readable;
- important references are readable;
- descriptions have useful width;
- only truly long values truncate.

### Resize

1. manually widen a column;
2. manually narrow another column within its allowed minimum;
3. reload/navigate away and back;
4. shared list grid must restore the user's `v2` widths.

### Reset Layout

1. resize several shared list columns;
2. press RESET LAYOUT;
3. new ERP defaults return;
4. old narrow legacy layout does not reappear.

### Long text

Use long:

- Description;
- Remarks;
- reference.

Expected:

- grid remains stable;
- long value can truncate;
- surrounding columns are not crushed.

### Many columns

Expected:

- horizontal scrolling works;
- grid stays inside its page/card/container;
- no whole-page accidental horizontal overflow where the grid itself should scroll.

### Export

On at least one `CommonDataGridEx` page that uses built-in export:

- export to XLSX still completes;
- exported data is unchanged;
- column sizing changes do not produce an unusable export layout.

### Mobile

Expected:

- existing compact list/card mode remains;
- grid changes do not break mobile page shell.

---

# 27. Specific Regression Checks

The implementation is rejected if any of the following occurs.

- Grid rows become multi-line everywhere.
- Every Description auto-expands to the longest database value.
- User width changes disappear after reload.
- RESET LAYOUT restores the old broken widths.
- Direct transaction grids remain non-resizable without documented reason.
- A Customer/Vendor Name column defaults around 100px.
- An Item Description column defaults around 100px.
- Work Order trace fields become unreadable.
- Popup grids extend outside the viewport.
- Mobile compact lists disappear.
- Shared list search/filter behaviour changes.
- Sorting/grouping/selection/export breaks.
- Posting/rollback/costing logic changes.
- Server paging is replaced with client-side full loading.
- New global CSS hacks target every `.dxbl-grid` indiscriminately.
- A semantic `Size` is added but an obsolete narrow explicit `Width` still overrides it.
- A touched DevExpress grid column still uses an accidental unitless `Width="110"` style value.
- Shared persisted grids rely on a mutable title instead of a stable explicit `GridKey`.
- v2 migration repeats on every load instead of being saved once.

---

# 28. Performance Requirements

The fix must not:

- inspect all row values on every render to calculate widths;
- load extra database data solely to calculate a column width;
- materialize server-paged datasets;
- run repeated JavaScript text measurement;
- auto-fit every refresh.

Sizing is primarily metadata-driven.

This keeps the grid predictable and fast.

---

# 29. Code Quality Requirements

- Keep `.razor` focused on markup.
- Keep shared sizing calculation in reusable C# code.
- Do not introduce large inline `@code` blocks.
- Follow existing code-behind conventions.
- Use DevExpress controls already present in the repository.
- Do not introduce another grid library.
- Do not add broad global CSS overrides for DevExpress internals unless absolutely required and verified against light/dark theme.
- Prefer existing theme variables.
- Keep changes reviewable by module/phase.

---

# 30. Suggested Commit Breakdown

If the coding agent is allowed to make multiple commits, use:

### Commit 1

```text
UI: add shared ERP grid column sizing and layout v2
```

Contains:

- sizing model;
- shared grid rendering;
- persisted layout migration;
- audit columns.

### Commit 2

```text
UI: improve Sales and Purchase grid readability
```

### Commit 3

```text
UI: improve Inventory grid readability
```

### Commit 4

```text
UI: improve Production and remaining grid readability
```

### Commit 5

```text
Docs: enforce grid readability in ERP UI skill
```

Do not mix business feature changes into these commits.

---

# 31. Agent Completion Report

The coding agent's final report must include:

```text
GRID READABILITY IMPLEMENTATION REPORT

Branch:
Final commit:

Shared grid files changed:
Direct DxGrid files changed:
CommonDataGridEx pages reviewed:
Direct DxGrid pages reviewed:

Layout migration:
- v2 key implemented: YES/NO
- old widths reset once: YES/NO
- new user widths persist: YES/NO
- Reset Layout verified: YES/NO

Modules completed:
- Admin:
- Inventory:
- Sales:
- Purchase:
- Planning/Production:

Representative runtime pages tested:
1.
2.
3.
...

Build:
- command:
- result:

Remaining intentionally narrow columns:
- file / column / width / reason

Business logic changed:
NONE

Known limitations:
NONE / list
```

If `Business logic changed` is not `NONE`, the implementation must be reviewed before merge.

---

# 32. Definition of Done

The task is complete only when all applicable items are checked.

## Shared infrastructure

- [ ] `GridColumnSizing` exists.
- [ ] `GridColumnData` supports semantic size and min width.
- [ ] `GridColumnDefinition` supports semantic size and min width.
- [ ] exact semantic preset map is implemented once.
- [ ] renderer-facing MinWidth resolution returns `int`.
- [ ] `CommonDataGridEx` uses effective width/min width.
- [ ] `CommonDataGrid` uses effective width/min width.
- [ ] normal shared grids remain no-wrap.
- [ ] shared grids remain resizable.

## Layout persistence

- [ ] new layout schema/version is used.
- [ ] legacy narrow widths are not blindly reused.
- [ ] old sort/order/visibility is preserved where practical.
- [ ] user-resized v2 widths persist.
- [ ] RESET LAYOUT returns to new defaults.
- [ ] legacy layout cannot immediately reappear after reset.
- [ ] migrated legacy layout is immediately saved as v2.
- [ ] touched persisted shared grids use explicit stable `GridKey`.

## Pages

- [ ] Sales grids audited.
- [ ] Purchase grids audited.
- [ ] Inventory grids audited.
- [ ] Planning/Production grids audited.
- [ ] Admin grids audited.
- [ ] lookup grids audited.
- [ ] inquiry/analysis grids audited.
- [ ] master grids audited.
- [ ] direct desktop grids are resizable unless documented exception.
- [ ] touched DxGrid Width values use valid CSS units.
- [ ] semantic Size columns do not retain obsolete narrow Width overrides.
- [ ] normal business text is readable.
- [ ] only genuinely long fields normally truncate.

## Safety

- [ ] no posting change.
- [ ] no rollback change.
- [ ] no costing change.
- [ ] no stock logic change.
- [ ] no sales/procurement/production workflow change.
- [ ] no database migration required.
- [ ] no server-paging regression.

## Verification

- [ ] `dotnet build ErpWeb.slnx` passes.
- [ ] `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` passes.
- [ ] GridColumnSizing focused tests pass.
- [ ] shared layout persistence tested.
- [ ] RESET LAYOUT tested.
- [ ] long-text truncation tested.
- [ ] horizontal scrolling tested.
- [ ] light/dark theme checked.
- [ ] desktop checked.
- [ ] mobile/compact list checked.
- [ ] UI skill updated.

---

# 33. Approval Decision

## APPROVED FOR IMPLEMENTATION

This plan is approved because it addresses the actual `productionv2` causes rather than applying page-specific cosmetic hacks:

1. narrow hard-coded/default column widths;
2. disabled wrapping combined with insufficient width;
3. missing resizing on many direct grids;
4. lack of a shared semantic width standard;
5. persisted layouts that currently discard user-resized widths;
6. repeated UI patterns across Inventory, Sales, Purchase and Production.

The solution deliberately keeps dense ERP grids, allows long text to truncate, gives normal business fields readable defaults, preserves user resizing, protects existing business logic, and adds an AI-agent rule to prevent the problem from returning.

**Final status: FINAL REVIEWED — APPROVED FOR IMPLEMENTATION — 10/10 implementation readiness.**

**Professional review result:** No unresolved design blocker remains. The agent may implement this plan on the verified `productionv2` baseline, subject to the re-scan-at-start rule if the branch head changes.
