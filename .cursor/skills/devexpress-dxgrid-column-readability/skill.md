---
name: devexpress-dxgrid-column-readability
description: >-
  Enforce readable, resizable and consistent DevExpress Blazor DxGrid columns in
  mokth/net10projectTemplate. Apply whenever creating, modifying, reviewing, standardizing,
  or fixing DxGrid, DxGridDataColumn, CommonDataGrid, or CommonDataGridEx column widths,
  MinWidth, text truncation, horizontal scrolling, persisted layout widths, lookup grids,
  transaction line grids, inquiry grids, master grids, or Production/Work Order grids.
---

# DevExpress DxGrid Column Readability Standard

## Purpose

Prevent this recurring ERP UI problem:

```text
narrow columns
→ normal business values show "..."
→ users repeatedly resize columns
→ saved widths are inconsistent or lost
→ poor ERP usability
```

Required behaviour:

> **Normal business values must be readable by default. Only genuinely long text should normally truncate.**

This skill applies to:

- `DxGrid`
- `DxGridDataColumn`
- `DxGridCommandColumn`
- `DxGridSelectionColumn`
- `CommonDataGrid`
- `CommonDataGridEx`
- transaction line grids
- transaction lists
- lookup/search popup grids
- inquiry/analysis grids
- master/Admin grids
- Planning/Production/Work Order grids

Repository authority:

```text
mokth/net10projectTemplate
```

Always inspect the requested/current branch before coding.

---

# 1. Mandatory Parent Standards

Before changing an ERP grid, read:

```text
.agents/skills/erp-ui-core/skill.md
```

For transaction pages also read:

```text
.agents/skills/erp-transaction-ui-standard/skill.md
```

and the applicable module skill:

```text
Inventory  → .agents/skills/inventory-transaction-ui/skill.md
Sales      → .agents/skills/sales-transaction-ui/skill.md
Purchase   → .agents/skills/purchase-transaction-ui/skill.md
Production → .agents/skills/production-transaction-ui/skill.md
```

This skill controls **grid readability, sizing and resize behaviour** only.

It does not replace workflow, permission, posting, costing, save, rollback or module UI rules.

---

# 2. Inspect the Live Grid Before Coding

Do not size columns from memory.

Record:

```text
GRID READABILITY EVIDENCE
Page/file:
Grid purpose:
Shared CommonDataGridEx or direct DxGrid:
Current ColumnResizeMode:
Current TextWrapEnabled:
Current Width values:
Current MinWidth values:
Current GridKey:
Persisted layout:
Horizontal scrolling:
Mobile behaviour:
Template columns:
Long-text columns:
Numeric columns:
Intentional compact columns:
```

For repository-wide work use:

```bash
rg -l '<DxGrid\b' ErpWeb.UI -g '*.razor'
rg -n '<DxGridDataColumn\b' ErpWeb.UI -g '*.razor'
rg -n 'Width="\d+"' ErpWeb.UI -g '*.razor'
rg -n '<CommonDataGridEx\b' ErpWeb.UI -g '*.razor'
```

Do not rely only on an old plan or remembered file list.

---

# 3. Core UX Contract

## Normal business values should normally fit

Examples:

- document number
- item/product code
- customer/vendor code
- warehouse/location
- work centre/process/machine
- operator
- status
- UOM
- date
- quantity
- price/cost/amount
- normal reference
- normal customer PO/vendor reference

## Long text may truncate

Examples:

- Description
- Remarks
- Notes
- Address
- Comment
- free-text Reason

Long-text fields still require a useful default width.

---

# 4. Do Not Turn Wrapping On Globally

Do not solve narrow columns with:

```razor
TextWrapEnabled="true"
```

for all ERP grids.

Normal dense-grid default:

```razor
TextWrapEnabled="false"
```

Correct solution:

```text
useful default width
+ sensible MinWidth
+ column resizing
+ horizontal scrolling
+ ellipsis only when content is genuinely long
```

---

# 5. Horizontal Scrolling Is Acceptable

Do not compress 15–30 business columns into one viewport.

Priority:

```text
readability
→ sensible widths
→ MinWidth
→ resize
→ horizontal scroll
```

Not:

```text
fit everything
→ shrink every column
→ unreadable ERP data
```

---

# 6. Approved Shared Semantic Sizes

Use this shared vocabulary:

```csharp
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
```

Approved defaults:

| Size | Width | MinWidth |
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

Typical mapping:

```text
Tiny        → sequence, revision, tiny flag
Small       → UOM, compact enum
Status      → workflow/document status
Date        → date only
DateTime    → audit timestamp
Quantity    → qty/balance/count
Percent     → progress/fulfilment %
Amount      → price/cost/amount
Code        → item/customer/vendor/warehouse/location/WC/process/machine code
DocumentNo  → SO/PO/DO/Invoice/WO/DR
Reference   → customer PO/vendor ref/source ref
Name        → customer/vendor/person
Description → item/product description
LongText    → remarks/notes/comments/address-style text
```

---

# 7. Width and MinWidth Types

This is mandatory.

## Width

`Width` is a CSS-unit value:

```razor
Width="160px"
Width="25%"
```

Do not introduce:

```razor
Width="160"
```

Normalize unitless Width values when touching that grid.

## MinWidth

`MinWidth` is an integer pixel value:

```razor
MinWidth="135"
```

Do not write:

```razor
MinWidth="135px"
```

A shared model may store:

```csharp
public int? MinWidth { get; set; }
```

but the renderer-facing helper must return:

```csharp
int
```

not `int?`.

---

# 8. Shared Grid Model

Shared column models should support:

```csharp
public GridColumnSize Size { get; set; } = GridColumnSize.Auto;
public int? MinWidth { get; set; }
public string? Width { get; set; }
```

Use one shared sizing resolver.

Preferred API:

```csharp
string? GetEffectiveWidth(GridColumnData column);
int GetEffectiveMinWidth(GridColumnData column);

string? GetEffectiveWidth(GridColumnDefinition column);
int GetEffectiveMinWidth(GridColumnDefinition column);
```

Do not duplicate preset constants across components/pages.

---

# 9. Resolution Precedence

Width:

```text
1. explicit Width override
2. semantic Size Width
3. DevExpress Auto behaviour
```

MinWidth:

```text
1. explicit MinWidth override
2. semantic Size MinWidth
3. Auto baseline = 50
```

Do not guess semantic meaning from `FieldName` inside the shared renderer.

The page/domain definition decides the size.

---

# 10. Remove Obsolete Narrow Width Overrides

When migrating a shared column to semantic sizing, remove an old narrow Width that defeats the profile.

Wrong:

```csharp
new()
{
    Caption = "Customer",
    FieldName = nameof(Row.CustomerCode),
    Size = GridColumnSize.Code,
    Width = "120px"
}
```

Correct:

```csharp
new()
{
    Caption = "Customer",
    FieldName = nameof(Row.CustomerCode),
    Size = GridColumnSize.Code
}
```

Explicit override is allowed only when intentional:

```csharp
new()
{
    Caption = "Customer",
    FieldName = nameof(Row.CustomerCode),
    Size = GridColumnSize.Code,
    Width = "175px"
}
```

---

# 11. CommonDataGridEx Standard

For shared list grids keep:

```razor
ColumnResizeMode="GridColumnResizeMode.ColumnsContainer"
TextWrapEnabled="false"
```

Generated columns must use:

```razor
Width="@GetEffectiveWidth(col)"
MinWidth="@GetEffectiveMinWidth(col)"
```

Apply this to every RenderColumn branch:

- date
- time
- string
- stringicon
- stringicon2
- stringiconlink
- link
- click
- int
- decimal
- double
- bool
- default

Do not leave one branch on raw `col.Width`.

---

# 12. CommonDataGrid Standard

`CommonDataGrid` must share the same:

```text
GridColumnSize
Width resolver
MinWidth resolver
layout-version semantics
```

Do not create a second sizing system.

Do not alter selection/export/grouping/actions merely to fix column readability.

---

# 13. Stable GridKey Is Mandatory

Persisted shared grids need:

```text
non-empty
unique
stable
GridKey
```

Example:

```razor
GridKey="sa-so-list"
```

Do not rely on mutable Title text for long-term persisted layout identity.

If a touched `CommonDataGridEx` page lacks an explicit stable `GridKey`, add one.

---

# 14. Persist User-Resized Shared-Grid Widths

Approved layout model:

```text
legacy:
erp-grid-layout:{grid-key}

v2:
erp-grid-layout:{grid-key}:v2
```

## Normal v2 load

If v2 exists:

```text
load widths intact
```

Do not call `StripColumnWidths()` on a normal v2 load.

## One-time legacy migration

If v2 does not exist:

```text
load legacy
→ strip legacy widths once
→ apply existing filter-criteria stripping rule
→ preserve order/visibility/sort/group
→ immediately save migrated layout as v2
→ use migrated layout
```

## Reset Layout

Clear both:

```text
v2
legacy
```

Then return to the new ERP defaults.

Do not migrate legacy layout repeatedly.

---

# 15. Do Not Add Persistence to Every Direct DxGrid

For direct:

- entry line grids
- popup grids
- workflow grids
- production hierarchy grids

required behaviour is:

```text
readable defaults
+ MinWidth
+ resize
+ horizontal scrolling
```

Do not invent a new persistence subsystem for every direct grid.

Persist direct-grid layouts only when an established mechanism already exists.

Shared `CommonDataGridEx` persistence is mandatory.

---

# 16. Direct DxGrid Default

For normal desktop business grids:

```razor
<DxGrid ...
        ColumnResizeMode="GridColumnResizeMode.ColumnsContainer"
        TextWrapEnabled="false">
```

Use unless live repository behaviour proves a valid exception.

Document every exception.

---

# 17. Direct Column Examples

## Document Number

```razor
<DxGridDataColumn FieldName="@nameof(Row.DocumentNo)"
                  Caption="Document No"
                  Width="165px"
                  MinWidth="135" />
```

## Item Code

```razor
<DxGridDataColumn FieldName="@nameof(Row.ItemCode)"
                  Caption="Item"
                  Width="160px"
                  MinWidth="135" />
```

## Name

```razor
<DxGridDataColumn FieldName="@nameof(Row.Name)"
                  Caption="Name"
                  Width="240px"
                  MinWidth="190" />
```

## Description

```razor
<DxGridDataColumn FieldName="@nameof(Row.Description)"
                  Caption="Description"
                  Width="280px"
                  MinWidth="210" />
```

## Quantity

```razor
<DxGridDataColumn FieldName="@nameof(Row.Quantity)"
                  Caption="Qty"
                  Width="120px"
                  MinWidth="100"
                  DisplayFormat="n4"
                  TextAlignment="GridTextAlignment.Right" />
```

## Amount

```razor
<DxGridDataColumn FieldName="@nameof(Row.Amount)"
                  Caption="Amount"
                  Width="135px"
                  MinWidth="110"
                  DisplayFormat="n2"
                  TextAlignment="GridTextAlignment.Right" />
```

---

# 18. Narrow Columns Can Be Valid

Below ~110px can be valid for:

- selection
- action icons
- sequence
- revision
- UOM
- boolean
- compact percentage
- tiny enum/status

Do not leave these narrow without strong evidence:

- Item/Product
- Customer/Vendor
- Description
- Document No
- Reference
- Warehouse/Location
- Work Centre
- Process
- Machine
- Source document

---

# 19. Lookup Grid Standard

For lookup/search popups:

- keep popup viewport-safe
- keep Code readable
- give Name/Description most useful width
- allow compact supporting columns
- enable resize where appropriate
- allow horizontal scrolling

Do not make the whole popup enormous just to avoid horizontal scrolling.

---

# 20. Production / Work Order Grids

Do not apply one width blindly to all production grids.

Classify separately:

```text
Work Centre
Process
BOM
Machine
Labour
DR/SO source
Scheduling
Production Output
Material Issue
```

Traceability must be readable:

```text
SO
DR
Work Order
Product
Definition
Work Centre
Process
Machine
```

`PrWorkOrderEntry.razor` contains multiple different grids. Review each separately.

---

# 21. Inquiry / Analysis Grids

Use wider dimension columns:

- Item
- Category
- Customer
- Vendor
- Warehouse
- Process
- Machine

Numeric KPI columns can be compact:

- Qty
- Amount
- Variance
- %
- Count

Do not give all analytical columns equal width.

---

# 22. Long-Text Tooltip Rule

Do not assume every templated cell gets the same ellipsis/full-text behaviour as a plain data cell.

For:

```razor
<CellDisplayTemplate>
    <div>...</div>
</CellDisplayTemplate>
```

or link/button templates:

- test truncation
- ensure full long text is discoverable
- if necessary use a lightweight `title` or existing tooltip mechanism

Do not add tooltips to every numeric/date cell.

---

# 23. Do Not Auto-Fit Every Render

Do not run auto-fit/best-fit on every:

```text
render
refresh
load
filter
page change
```

It can:

- explode Description/Remarks width
- fight user width choices
- cause layout jumps
- behave inconsistently with server-paged data

Approved approach:

```text
semantic defaults
+ MinWidth
+ resize
+ persisted widths for shared list grids
```

---

# 24. Numeric Alignment

Normally right-align:

```text
quantity
price
cost
amount
percentage
```

Preserve existing domain formatting.

Typical:

```text
Qty   → n4
Money → n2
Cost  → current domain precision
Date  → existing explicit page format
```

Do not change costing/accounting precision during grid work.

---

# 25. Responsive Behaviour

Do not break existing mobile patterns.

If a list page already uses:

```text
iv-list-desktop
iv-list-compact
```

preserve both.

For entry grids:

- horizontal scrolling is allowed
- do not shrink font to solve overflow
- keep actions usable
- keep surrounding flex/grid containers at `min-width: 0` where necessary
- fix overflow locally

Do not add broad CSS against every:

```css
.dxbl-grid
```

---

# 26. Shared Audit Columns

Recommended baseline:

```text
Created        ~150px
User ID        ~130–140px
Modified Date  ~150px
Modified By    ~130–140px
```

Do not leave audit identities unnecessarily narrow.

---

# 27. Business Logic Guardrail

Grid work must not change:

- posting
- rollback
- delete eligibility
- costing/FIFO
- stock movement
- stock availability
- future-stock checks
- sales fulfilment
- purchase matching
- e-Invoice
- BOM calculation
- labour/machine costing
- WO scheduling
- DR allocation
- permissions
- document state transitions
- database schema
- service queries merely to determine UI width

No database migration should be required.

---

# 28. Performance Guardrail

Do not:

- query text lengths
- load extra rows to calculate width
- materialize server-paged datasets
- measure every cell with JavaScript
- inspect every row every render
- auto-fit on every refresh

Sizing is metadata-driven.

---

# 29. Implementation Workflow

## Step 1

Read:

```text
this skill
erp-ui-core
correct module skill
```

## Step 2

Inspect the live target grid.

## Step 3

Classify each column:

```text
Tiny
Small
Status
Date
DateTime
Quantity
Percent
Amount
Code
DocumentNo
Reference
Name
Description
LongText
custom override
```

## Step 4

Shared grid:

```text
semantic Size
MinWidth
stable GridKey
v2 layout
```

Direct grid:

```text
Width
MinWidth
ColumnResizeMode
horizontal scroll
```

## Step 5

Remove obsolete explicit narrow Width values that defeat semantic Size.

## Step 6

Normalize accidental unitless Width values:

```razor
Width="110"
```

to:

```razor
Width="110px"
```

where it is a DxGrid column Width.

## Step 7

Check long template cells.

## Step 8

Build:

```bash
dotnet build ErpWeb.slnx
```

## Step 9

Test:

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj
```

## Step 10

Runtime-test:

```text
default width
resize
shared-grid reload persistence
Reset Layout
long text
horizontal scrolling
mobile
light/dark theme
```

---

# 30. Shared-Sizing Tests

When shared sizing infrastructure changes, add/update:

```text
ErpWeb.Tests/UI/GridColumnSizingTests.cs
```

Minimum tests:

```text
Auto → Width null + MinWidth 50
all semantic profiles → exact approved pair
explicit Width overrides semantic Width
explicit MinWidth overrides semantic MinWidth
preset Width has valid CSS unit
renderer MinWidth resolver returns int
```

If layout key/version logic becomes a pure helper, test:

```text
legacy key
v2 key
empty key
stable key behaviour
```

Unit tests do not replace Razor compilation.

---

# 31. Static Review Gate

After changes:

```bash
rg -n 'Width="\d+"' ErpWeb.UI -g '*.razor'
rg -l '<DxGrid\b' ErpWeb.UI -g '*.razor'
rg -n '<CommonDataGridEx\b' ErpWeb.UI -g '*.razor'
```

For every remaining narrow text-bearing column, record:

```text
file
column
width
reason it is intentionally compact
```

---

# 32. Rejection Checklist

Reject if any applicable condition is true:

- [ ] normal Item code truncates by default
- [ ] normal Customer/Vendor code truncates by default
- [ ] normal document number truncates by default
- [ ] Customer/Vendor Name defaults near 100px
- [ ] Item Description defaults near 100px
- [ ] WO trace fields are unreadable
- [ ] wrapping is enabled globally
- [ ] normal desktop grid is non-resizable without reason
- [ ] semantic Size exists but old narrow Width still overrides it
- [ ] new DxGrid Width is unitless
- [ ] MinWidth uses `"px"`
- [ ] renderer passes nullable `int?` to MinWidth
- [ ] shared persisted grid lacks stable explicit GridKey
- [ ] normal v2 load strips widths
- [ ] legacy migration repeats every load
- [ ] Reset Layout lets legacy widths return
- [ ] direct grids receive unnecessary new persistence
- [ ] long templated text is inaccessible
- [ ] whole page overflows instead of grid/container handling scrolling
- [ ] global CSS hack targets all DevExpress grids
- [ ] mobile compact list is broken
- [ ] server paging/filtering changes
- [ ] posting/costing/workflow logic changes
- [ ] build fails
- [ ] tests regress

If any item is true, the grid implementation is not approved.

---

# 33. Definition of Done

## Shared

- [ ] semantic size profile exists once
- [ ] GridColumnData supports Size + MinWidth
- [ ] GridColumnDefinition supports Size + MinWidth
- [ ] Width resolver returns `string?`
- [ ] MinWidth resolver returns `int`
- [ ] CommonDataGridEx uses effective values in every column branch
- [ ] CommonDataGrid uses the same contract
- [ ] no-wrap remains default
- [ ] resizing remains enabled

## Persistence

- [ ] touched shared grids have stable GridKey
- [ ] v2 preserves user widths
- [ ] legacy widths stripped once
- [ ] migrated layout immediately saved as v2
- [ ] Reset Layout returns new defaults
- [ ] old narrow layout does not immediately return

## Direct grids

- [ ] meaningful text gets readable width
- [ ] important columns get MinWidth
- [ ] resize works where appropriate
- [ ] Width uses CSS units
- [ ] MinWidth uses integer pixels
- [ ] horizontal scrolling works
- [ ] narrow columns are justified

## Safety

- [ ] no business logic changed
- [ ] no costing changed
- [ ] no posting/rollback changed
- [ ] no database migration
- [ ] no paging/performance regression

## Verification

- [ ] `dotnet build ErpWeb.slnx` passes
- [ ] `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` passes
- [ ] static audit completed
- [ ] shared resize persistence tested
- [ ] Reset Layout tested
- [ ] long text tested
- [ ] horizontal scrolling tested
- [ ] desktop/mobile checked
- [ ] light/dark theme checked

---

# 34. Final Agent Rule

> **Do not optimize an ERP grid for the maximum number of columns visible at once. Optimize it so normal business data is readable on first use.**

Use:

```text
semantic defaults for shared grids
explicit readable widths for direct grids
sensible MinWidth
no-wrap dense rows
column resize
horizontal scrolling
persisted user widths for shared grids
ellipsis only for genuinely long text
```

Never change ERP business behaviour merely to standardize grid columns.
