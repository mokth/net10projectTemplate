# productionv2 — Repository-Wide ERP Form Alignment & Semantic Field Width Standardization
## APPROVED FOR IMPLEMENTATION — 10/10 AI Coding Agent Plan

**Repository:** `mokth/net10projectTemplate`  
**Target branch:** `productionv2`  
**Plan type:** UI/UX standardization only  
**Approval status:** **APPROVED FOR IMPLEMENTATION**  
**Risk level:** Medium if done as a one-shot global CSS change; **Low/controlled with this staged opt-in plan**  
**Primary objective:** Make editable ERP forms look professionally aligned and proportioned without disturbing business logic, bindings, workflow, validation, or unrelated UI.

---

# 1. Executive decision

The current UI has a real cross-page consistency problem:

1. controls that appear in the same logical row can have different vertical positions;
2. short-value fields often consume the same visual width as long-value fields;
3. different page families use different form-layout techniques and therefore drift visually;
4. there is already good semantic sizing work in some production pages, but it is page-specific instead of shared.

This plan **must not** be implemented by changing `.iv-field`, all DevExpress editors, or all form grids globally in one step.

The safe solution is:

> **Introduce opt-in shared form-layout primitives, prove them on a small canary set covering every UI archetype, then migrate editable pages in controlled module batches.**

This keeps current pages visually unchanged until each page is intentionally migrated.

No database change is required.  
No EF change is required.  
No service/business-rule change is required.  
No transaction workflow change is required.

---

# 2. Repository evidence reviewed

The plan is based on the current `productionv2` branch and the following live UI architecture.

## Shared/global UI

- `ErpWeb/Components/App.razor`
- `ErpWeb/wwwroot/css/site.css`
- `ErpWeb/wwwroot/css/inventory-chrome.css`
- `ErpWeb/wwwroot/css/pr-popup-grid.css`

`App.razor` currently loads:

1. `site.css`
2. `login.css`
3. `inventory-chrome.css`
4. `pr-popup-grid.css`
5. generated app/scoped CSS

This means a new intentional shared stylesheet can be loaded before scoped `.razor.css`, allowing page-specific CSS to remain the final override authority.

## Existing UI agent standards

- `.agents/skills/erp-ui-core/skill.md`
- `.agents/skills/erp-transaction-ui-standard/skill.md`
- `.agents/skills/inventory-transaction-ui/skill.md`
- `.agents/skills/sales-transaction-ui/skill.md`
- `.agents/skills/purchase-transaction-ui/skill.md`
- `.agents/skills/production-transaction-ui/skill.md`

Important existing rule already present in the repo:

> If three or more pages need a genuinely new shared primitive, extract it intentionally instead of continuing copy/paste divergence.

This task clearly qualifies.

## Representative forms reviewed

### Admin
- `ErpWeb.UI/Admin/AdminSettings.razor`
- `ErpWeb.UI/Admin/AdminSettings.razor.css`

### Inventory
- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor`
- `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor.css`
- `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor`
- `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor.css`
- `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor`
- `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor.css`

### Sales
- `ErpWeb.UI/Sales/Masters/SaCustEntry.razor`
- `ErpWeb.UI/Sales/Masters/SaCustEntry.razor.css`
- `ErpWeb.UI/Sales/Transactions/SaDocPage.razor`
- `ErpWeb.UI/Sales/Transactions/SaDocPage.razor.css`
- `ErpWeb.UI/Sales/Transactions/SaSo.razor`
- `ErpWeb.UI/Sales/Transactions/SaSo.razor.css`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor.css`

### Purchase
- `ErpWeb.UI/Purchase/Masters/PoSuppEntry.razor`
- `ErpWeb.UI/Purchase/Masters/PoSuppEntry.razor.css`
- `ErpWeb.UI/Purchase/Transactions/PoOrder.razor`
- `ErpWeb.UI/Purchase/Transactions/PoOrder.razor.css`
- `ErpWeb.UI/Purchase/Transactions/PoPr.razor`
- `ErpWeb.UI/Purchase/Transactions/PoPr.razor.css`

### Production / Planning
- `ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor`
- `ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor.css`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.css`
- `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor.css`

## Existing tests reviewed

- `ErpWeb.Tests/UI/GridColumnSizingTests.cs`
- `ErpWeb.Tests/Other/DxPopupFooterContractTests.cs`
- `ErpWeb.Tests/ErpWeb.Tests.csproj`

The repository already uses source-level UI contract tests. This plan extends that pattern rather than introducing a new UI-test framework.

---

# 3. Confirmed root causes

## 3.1 Same-row vertical misalignment

The reported Admin Settings popup is a concrete example.

Current shared field primitive:

```css
.iv-field {
    display: grid;
    gap: 6px;
}
```

Current Admin popup grid:

```css
.as-editor__grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 14px;
}
```

The `Stored at` field contains:

- label
- editor
- help text

while `Value` contains:

- label
- editor

Both field containers participate in the same CSS grid row. Without an explicit top-content alignment contract, the shorter field can distribute its internal free space differently, causing the editor boxes to no longer share the same Y position.

This is a structural layout problem, not a DevExpress data-binding problem.

## 3.2 Oversized controls

The repository uses several patterns that naturally make an editor fill the available cell:

- `CssClass="w-100"`
- two equal columns
- four equal columns
- `DxFormLayoutItem` with generic `ColSpanMd`
- page-specific equal-fraction CSS grids

That is appropriate for long descriptions and addresses, but visually wasteful for:

- UOM
- currency
- status
- type
- prefix
- sequence
- quantity
- percentage
- date
- simple code
- yes/no settings

## 3.3 Multiple page archetypes

The repo intentionally has several entry-page languages:

- `iv-*` master/list chrome
- Inventory header/detail entries
- Sales/Purchase `SaDocPage` / `sdoc-*`
- Production `pwo-*`, `pr-dp-*`, etc.
- Product Definition specialized authoring UI

These must **not** be flattened into one universal form template.

The new standard therefore owns only the low-level behavior:

- vertical field alignment
- semantic editor width
- spacing
- responsive width fallback
- hint/error positioning

It does **not** replace page archetypes.

---

# 4. Non-negotiable safety strategy

## 4.1 No one-shot global override

Do **not** make a first-step change such as:

```css
.iv-field { ... }
.dxbl-text-edit { ... }
.dxbl-combobox { ... }
.dxbl-spin-edit { ... }
```

across the whole application.

Do **not** globally apply `max-width` to DevExpress editors.

Do **not** globally replace `.w-100`.

A global selector can silently break:

- search boxes;
- line-grid editors;
- popup lookup controls;
- responsive layouts;
- memo/remark fields;
- address fields;
- generated DevExpress layout internals.

## 4.2 New behavior must be opt-in

A page/form must explicitly opt in using a new scope class.

Recommended shared scope:

```text
erp-form-scope
```

Pages that do not have this class must remain visually unchanged.

## 4.3 Preserve business behavior exactly

This task must not change:

- routes;
- EF entities;
- database schema;
- service calls;
- posting logic;
- costing;
- stock authority;
- sales pricing;
- procurement matching;
- production execution;
- validation rules;
- permissions;
- status transitions;
- dirty-state logic;
- navigation;
- lookup query behavior;
- `Value`, `ValueChanged`, `ValueExpression`;
- `Text`, `TextChanged`, `TextExpression`;
- existing required-field behavior;
- event handlers.

A layout task is not permission to refactor transaction logic.

## 4.4 Preserve control types during this project

Do not convert a DevExpress editor into another component merely for styling.

Do not replace existing raw controls unless there is a separate verified functional reason.

The safest implementation changes only wrappers/classes/layout CSS.

---

# 5. Shared form-layout primitive

## 5.1 Add a dedicated stylesheet

Create:

```text
ErpWeb/wwwroot/css/erp-form-layout.css
```

Load it in:

```text
ErpWeb/Components/App.razor
```

Recommended position:

```text
site.css
inventory-chrome.css
erp-form-layout.css
pr-popup-grid.css
generated/scoped CSS
```

The exact order may be adjusted after verifying selector precedence, but scoped page CSS must remain able to override the shared primitives.

Why a dedicated stylesheet:

- this is intentionally cross-module;
- it avoids turning `site.css` into an unstructured form framework;
- it avoids incorrectly putting Sales/Purchase/Production rules into `inventory-chrome.css`;
- it is easy to revert;
- it is easy to audit.

## 5.2 Scope every rule

All new shared rules must be scoped.

Allowed examples:

```css
.erp-form-scope .iv-field { ... }
.erp-form-scope .erp-editor--sm { ... }
.erp-form-scope .erp-editor--full { ... }
```

Forbidden examples:

```css
.iv-field { ... }                    /* too broad for this migration */
.dxbl-text-edit { ... }              /* forbidden global DevExpress override */
.dxbl-combobox { max-width: ... }    /* forbidden */
.w-100 { max-width: ... }            /* forbidden */
```

## 5.3 Field stack alignment

Within an opted-in form:

```css
.erp-form-scope .iv-field {
    align-content: start;
    grid-auto-rows: max-content;
    min-width: 0;
}
```

For custom field classes used by a page, apply the same principle:

```text
display/grid/flex container
→ align content/items to start
→ do not vertically distribute unused height
→ hint/error remains below editor
```

Do not add arbitrary `margin-top` fixes to individual editors.

## 5.4 Row alignment

Shared row/grid containers that opt in must use:

```text
align-items: start
```

not stretch-based vertical distribution for field content.

For flex rows:

```text
align-items: flex-start
```

For CSS grid rows:

```text
align-items: start
```

This is the default professional form behavior.

---

# 6. Semantic editor-width standard

The editor width should reflect the expected value, not the database column size and not the entire grid cell.

Use a small, reusable taxonomy.

## 6.1 Recommended semantic widths

The exact CSS token names can use `rem`, but the visual targets should be close to:

| Semantic size | Typical target | Typical use |
|---|---:|---|
| `xs` | 96–120 px | sequence, tiny codes, UOM, flag context |
| `sm` | 128–160 px | currency, status, priority, percentage, prefix |
| `md` | 168–220 px | date, document no., short reference, warehouse |
| `lg` | 240–320 px | item/customer/supplier lookup, normal business code |
| `xl` | 320–440 px | names, descriptions, larger lookups |
| `full` | available width | address, long description, remark, memo |

Recommended classes:

```text
erp-editor--xs
erp-editor--sm
erp-editor--md
erp-editor--lg
erp-editor--xl
erp-editor--full
```

## 6.2 Width class behavior

The class should be applied to a safe wrapper around the editor where possible.

Example:

```razor
<div class="erp-editor--sm">
    <DxComboBox ... CssClass="w-100" />
</div>
```

This lets the existing editor remain `width:100%` within the correctly sized wrapper.

Do not fight DevExpress internals with fragile descendant selectors when a wrapper can solve the problem.

Recommended behavior:

```css
.erp-form-scope .erp-editor--xs   { width: min(100%, var(--erp-editor-xs)); }
.erp-form-scope .erp-editor--sm   { width: min(100%, var(--erp-editor-sm)); }
.erp-form-scope .erp-editor--md   { width: min(100%, var(--erp-editor-md)); }
.erp-form-scope .erp-editor--lg   { width: min(100%, var(--erp-editor-lg)); }
.erp-form-scope .erp-editor--xl   { width: min(100%, var(--erp-editor-xl)); }
.erp-form-scope .erp-editor--full { width: 100%; }
```

Use `max-width`/`width` carefully and test with DevExpress rendered roots.

## 6.3 Mobile behavior

At the mobile breakpoint used by the current page family, semantic editors should normally become:

```text
width: 100%
max-width: none
```

unless the existing page intentionally keeps a small editor compact.

The user must not get a tiny 110px field in a 390px-wide single-column mobile form when full width is more usable.

## 6.4 Do not classify solely by `maxlength`

Examples:

- a 20-character TIN may still need a medium/large editor;
- a two-character currency can use small;
- an item code may be 20 characters but should often be large because lookup usability matters;
- warehouse/location values may need medium/large due to descriptions.

Classify by user task and displayed content.

---

# 7. Label and hint standard

## 7.1 Labels

Keep labels short and business-oriented.

Do not turn a label into a sentence just to explain the field.

Example:

Bad:

```text
Select the company scope where this setting should be stored
```

Good:

```text
Stored at
```

with a hint below.

This keeps editor rows aligned.

## 7.2 Hints

Hints belong below the editor.

A field with a hint must not push the neighboring editor downward.

Expected desktop geometry:

```text
Label A              Label B
[ editor A ]         [ editor B ]
hint A
```

not:

```text
Label A              Label B
[ editor A ]
hint A                [ editor B ]
```

## 7.3 Validation errors

Validation stays below the editor just like hints.

Do not reserve large permanent error space across every field.

When an error appears, the row may grow downward; the editors at the top of the row must remain aligned.

---

# 8. Do not rearrange business forms in Phase 1

The first migration goal is:

1. align controls;
2. apply sensible editor widths;
3. preserve field order;
4. preserve current grouping;
5. preserve current breakpoints.

Do **not** aggressively move fields to different rows in the first pass.

Why:

- moving fields has higher regression risk;
- tab order can change;
- operators may rely on familiar field order;
- `DxFormLayout` responsive behavior can change;
- validation placement can move.

Only after semantic sizing is complete should a page be considered for a separate targeted compaction pass.

---

# 9. Phase 0 — build an auditable migration inventory

Before changing page markup, the AI agent must scan all current:

```text
ErpWeb.UI/**/*.razor
ErpWeb.UI/**/*.razor.css
```

and produce a migration manifest.

Suggested artifact:

```text
docs/ui-form-layout-migration-productionv2.md
```

If this repository does not keep UI audit docs under `docs`, place it under an agreed repository planning folder; do not invent a runtime dependency.

For every page containing editable UI, record:

| Field | Meaning |
|---|---|
| Path | Razor file |
| Module | Admin / Inventory / Sales / Purchase / Planning |
| Archetype | Master / transaction / production workspace / popup / filter |
| Current layout | `iv-form-grid`, `DxFormLayout`, custom CSS grid, flex |
| Has alignment risk | Yes/No |
| Has oversized short fields | Yes/No |
| Migration phase | Canary / module batch / no change |
| Exceptions | why it must retain full/auto width |

### In-scope

- master entry/edit pages;
- transaction headers;
- transaction line-editor popups;
- admin setting/edit popups;
- production input/workspace fields;
- editable filter popups where the issue is visible;
- other editable modal forms.

### Normally out-of-scope

- list-grid column widths;
- read-only inquiry grids;
- dashboards;
- cards containing only display values;
- pure lookup result tables;
- chart sizes;
- report layout;
- login/auth forms unless the audit proves the same defect;
- mobile compact list cards.

Do not claim completion until every `.razor` page has at least been classified.

---

# 10. Phase 1 — shared primitive only

Implement:

1. `erp-form-layout.css`;
2. load it from `App.razor`;
3. add source-level tests;
4. **do not add `erp-form-scope` to all pages yet**.

At the end of this phase there should be essentially **zero intentional visual change** because no broad selector is allowed.

This is an important safety checkpoint.

---

# 11. Phase 2 — canary migration

Do not migrate the entire repository immediately.

Migrate one representative page/form from each archetype.

## Canary A — exact reported defect

### `AdminSettings`

Files:

```text
ErpWeb.UI/Admin/AdminSettings.razor
ErpWeb.UI/Admin/AdminSettings.razor.css
```

Required result:

```text
Stored at              Value
[ Company ▼ ]          [ Not set — uses default ▼ ]
helper text...
```

Both editor top edges must align.

Suggested semantic widths:

- Stored at: `md`
- Value: `lg` or `xl` depending on the token content
- Remark: `full`

Do not change setting resolution logic.

## Canary B — master-data form

Use one of:

```text
ErpWeb.UI/Sales/Masters/SaCustEntry.razor
or
ErpWeb.UI/Purchase/Masters/PoSuppEntry.razor
```

Prefer one first; migrate the sibling only after the first is visually approved.

Targets:

- Postal code: `sm/md`
- Country/state: `md`
- TIN/BRN: `md`
- currency/payment terms: `md`
- customer/supplier name: `xl`
- address lines: `full` or large
- remark/memo: `full`

Do not rearrange tabs or contact/payment logic.

## Canary C — commercial document

Use:

```text
ErpWeb.UI/Sales/Transactions/SaSo.razor
```

Preserve:

```text
SaDocPage
sdoc-*
DxFormLayout
```

Do not replace the commercial-document shell.

Use semantic editor wrappers inside the existing `DxFormLayoutItem` spans.

Important:

- keep existing `ColSpanMd` in the first pass;
- constrain short editors within their existing cells;
- keep customer/item lookups sufficiently wide;
- keep address/memo fields full.

## Canary D — Inventory transaction

Use:

```text
ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor
```

Preserve the Inventory transaction anatomy and `DxFormLayout`.

Test:

- dates/types/status/reference;
- line popup;
- warehouse/location/lot;
- qty/UOM;
- remarks.

Do not touch posting/rollback behavior.

## Canary E — Production workflow

Use:

```text
ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor
```

or, if that page is actively being changed for another branch feature, use:

```text
PrWorkOrderEntry.razor
```

Preserve the production workflow and command bar.

Do not convert it to Sales `sdoc-*`.

---

# 12. Mandatory canary acceptance gate

The AI agent **must stop the broad rollout** if any canary fails.

Each canary must be checked at minimum:

### Desktop
- 1440 × 900
- 1280 × 720
- 1024 × 768

### Narrow/mobile
- ~768 px width
- ~390 px width

### Theme
- light
- dark

### State
- new/edit
- view/read-only where applicable
- popup open
- validation error visible
- hint visible
- required field visible

### Browser zoom
- 100%
- 125%

Acceptance:

- editor top edges align within the same logical row;
- no text overlaps;
- no editor is clipped;
- dropdown button remains visible;
- date icon/spin buttons remain visible;
- validation is not covered;
- popup remains inside viewport;
- keyboard tab order is unchanged;
- mobile editor width remains usable;
- dark theme remains readable;
- no grid or search field is accidentally narrowed.

If the coding agent has browser automation/computer-use capability, capture screenshots for the canary pages.

If it does not, it must explicitly output a visual-verification checklist and must not pretend browser verification occurred.

Do **not** add Playwright/NUnit/Selenium packages to the solution just for this refactor unless separately approved. The current test project does not contain a browser E2E framework.

---

# 13. Phase 3 — module rollout order

After canaries pass, migrate in small module batches.

Recommended order:

## Batch 1 — Admin + simple masters

Low workflow risk.

Examples:

- Admin Settings
- Admin Company/role/user forms where the manifest identifies an issue
- small reference-master popups

## Batch 2 — Sales/Purchase master profiles

Examples:

- `SaCustEntry`
- `PoSuppEntry`
- other editable master forms

These contain many fields and benefit significantly from semantic width.

## Batch 3 — Sales commercial documents

Examples:

- `SaQt`
- `SaSo`
- `SaDo`
- `SaInvoice`
- `SaCdn`
- `SaDeliveryRequestEntry`

Rules:

- preserve `SaDocPage`;
- do not change document anatomy;
- preserve current `DxFormLayoutItem` order/spans first;
- use semantic wrappers inside cells.

## Batch 4 — Purchase commercial documents

Examples:

- `PoPr`
- `PoOrder`
- `PoInvoice`
- `PoCdn`
- self-bill documents

Follow the Purchase UI skill and the live closest reference.

## Batch 5 — Inventory entries/masters

Examples:

- `IvStockMasterEntry`
- `IvGoodsReceipt`
- `IvMiscIssue`
- `IvMiscReceipt`
- `IvStockTransfer`
- `IvStockAdjustment`
- `IvStockCount`
- `IvStockReturn`
- `IvVendorReturn`
- `IvScrap`

Do not alter stock-source/destination/lot picker behavior.

## Batch 6 — Production / Planning

Examples:

- `PrProductDefEntry`
- `PrWorkOrderEntry`
- `PrDailyProductionEntry`
- `PrMaterialIssueEntry`
- `PrFinishedGoodReceiptEntry`
- machine/shift/calendar editable forms

Production is last because the pages have more specialized workflow layout.

---

# 14. Product Definition is a design reference, not a copy target

`PrProductDefEntry.razor.css` already contains useful field-size concepts such as:

```text
short code
sequence
quantity
time
count
cost
UOM
prefix
warehouse
description
remark
```

The agent should reuse the **concept**, not copy its `pdf-*` classes globally.

The shared standard should generalize the semantic sizes into neutral `erp-*` classes.

After the shared standard is proven, Product Definition may optionally be migrated from `pdf-field-*` classes to the neutral standard, but only if doing so reduces duplication without degrading its specialized authoring layout.

Do not rewrite Product Definition simply for naming purity.

---

# 15. `DxFormLayout` migration rule

For Sales/Purchase/Inventory pages that already use `DxFormLayout`:

### First pass

Keep existing:

```text
DxFormLayout
DxFormLayoutGroup
DxFormLayoutItem
ColSpanMd
BeginRow
```

unchanged unless there is a proven layout bug.

Add a safe wrapper around the editor:

```razor
<DxFormLayoutItem Caption="Currency" ColSpanMd="4">
    <div class="erp-editor--sm">
        <DxComboBox ... CssClass="w-100" />
    </div>
</DxFormLayoutItem>
```

This lowers regression risk dramatically.

### Do not do this initially

Do not immediately change:

```text
ColSpanMd=4 → ColSpanMd=2
```

everywhere just because the editor is shorter.

That would change row flow and responsive behavior.

After the first migration is approved, a **separate targeted compacting pass** may reduce spans where it clearly improves a page.

---

# 16. Custom CSS grid migration rule

For custom page grids such as:

```text
pwo-form-grid
pr-dp-*
iv-stock-fields
as-editor__grid
```

retain the page-specific grid structure unless it is the root cause.

Apply:

- `align-items:start`;
- field-stack top alignment;
- semantic editor-width wrappers;
- responsive full-width fallback.

Do not replace every custom grid with `.erp-form-grid`.

The shared primitive should complement page-specific layout, not erase it.

---

# 17. Popup standard

Editable popups are included because the reported defect occurs in a popup.

For migrated popups:

- keep `DxPopup`;
- keep `common-popup`;
- keep `CloseOnOutsideClick` behavior;
- keep existing footer pattern;
- keep `ShowFooter` contract where a footer template exists;
- size controls semantically;
- keep memo/remark long;
- top-align same-row fields;
- keep popup scrollable on narrow screens.

Do not globally shrink popup widths.

A popup width may be changed only when:

1. the migrated content is obviously over-wide;
2. all fields still fit;
3. narrow-screen behavior is verified;
4. there is no clipping of lookup/dropdown content.

---

# 18. Automated guard tests

Add source-level contract tests under:

```text
ErpWeb.Tests/UI/
```

Recommended file:

```text
ErpFormLayoutContractTests.cs
```

## Required test A — stylesheet is loaded

Assert `App.razor` references:

```text
css/erp-form-layout.css
```

exactly once.

## Required test B — no unscoped DevExpress overrides

Scan the new CSS and fail if it contains broad selectors that start with bare:

```text
.dxbl-
```

or broad changes to:

```text
.w-100
.iv-field
```

outside the approved `.erp-form-scope` scope.

The test does not need a full CSS parser; a clear source-level contract similar to `DxPopupFooterContractTests` is acceptable.

## Required test C — semantic tokens exist

Assert the shared stylesheet defines:

```text
erp-form-scope
erp-editor--xs
erp-editor--sm
erp-editor--md
erp-editor--lg
erp-editor--xl
erp-editor--full
```

This prevents half-completed shared infrastructure.

## Required test D — canary adoption

Assert the selected canary pages contain the opt-in marker after Phase 2.

Do not assert every UI page until its module batch is actually migrated.

## Required test E — no binding rewrite

A source test cannot reliably prove binding equivalence, so this must be enforced through diff review:

- any changed line containing `ValueChanged`;
- `ValueExpression`;
- `TextChanged`;
- `TextExpression`;
- `@bind-`;
- event handlers

must be explicitly reviewed.

For a pure layout migration, these lines should generally be unchanged apart from wrapper indentation.

---

# 19. Build/test gate after every batch

After every batch:

```powershell
dotnet build ErpWeb.slnx
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --no-build
```

If the local workflow requires building the test project separately, use the repository's normal build sequence.

Important note already present in the test project:

> A green xUnit suite does not prove a `.razor` page renders.

Therefore both build and visual canary checks are required.

Do not continue to the next module if the current module fails build or UI verification.

---

# 20. Diff-review gate

For every migrated page, review the Git diff and reject unexpected changes.

Expected diff types:

```text
+ erp-form-scope class
+ semantic editor wrapper/class
+ small page CSS alignment adjustment
+ responsive width rule
```

Unexpected / reject unless justified:

```text
service call changed
route changed
status logic changed
permission changed
event handler changed
model property changed
validation rule changed
posting action changed
lookup data source changed
database call changed
grid data source changed
price/cost logic changed
```

The agent must provide a per-batch summary:

```text
UI-only files changed:
Behavior/code-behind files changed: NONE
Database/model/service changes: NONE
Binding changes: NONE
```

If any item is not `NONE`, explain and stop for review.

---

# 21. Git/rollback strategy

Do not put the entire migration in one giant commit.

Recommended commit structure:

```text
1. ui: add opt-in ERP form layout primitives and contract tests
2. ui: migrate form-layout canaries
3. ui: standardize admin and master forms
4. ui: standardize sales commercial forms
5. ui: standardize purchase commercial forms
6. ui: standardize inventory forms
7. ui: standardize production forms
8. ui: finalize form-layout audit and standards
```

Benefits:

- a problematic module can be reverted independently;
- the shared primitive can remain even if one batch is reverted;
- source of visual regression is easy to identify;
- user acceptance can happen progressively.

Because the CSS is opt-in, removing `erp-form-scope` or reverting a page migration restores its previous behavior without a database rollback.

---

# 22. Admin Settings concrete target

The reported popup should be the first proof.

Current concept:

```text
Stored at                 Value
[ Company          ▼ ]         [ Not set — default ▼ ]
helper text...
```

Required result:

```text
Stored at                 Value
[ Company ▼ ]             [ Not set — uses default ▼ ]
helper text...
```

Editor top edges aligned.

Recommended markup concept:

```razor
<div class="as-editor__grid erp-form-scope">
    <div class="iv-field">
        <label class="iv-field__label">Stored at</label>
        <div class="erp-editor--md">
            ... existing DxComboBox unchanged ...
        </div>
        <small class="as-editor__hint">...</small>
    </div>

    <div class="iv-field">
        <label class="iv-field__label">Value</label>
        <div class="erp-editor--lg">
            ... existing editor unchanged ...
        </div>
    </div>

    <div class="iv-field as-editor__remark">
        <label class="iv-field__label">Remark</label>
        <div class="erp-editor--full">
            ... existing DxTextBox unchanged ...
        </div>
    </div>
</div>
```

The exact wrapper structure can vary if DevExpress markup requires it, but the bindings and event callbacks must remain unchanged.

---

# 23. Field classification guidance

Use this matrix when migrating pages.

| Field | Default semantic size |
|---|---|
| Yes/No checkbox | `xs` or natural |
| Sequence/order | `xs` |
| UOM | `xs` / `sm` |
| Currency | `sm` |
| Prefix | `sm` |
| Status | `sm` / `md` |
| Priority | `sm` |
| Percent | `sm` |
| Pack size | `sm` |
| Qty | `sm` / `md` |
| Date | `md` |
| Document no. | `md` |
| TIN/BRN | `md` |
| Warehouse | `md` / `lg` |
| Location | `md` / `lg` |
| Project/department | `md` / `lg` |
| Salesman/buyer | `md` / `lg` |
| Customer/Supplier lookup | `lg` |
| Item lookup | `lg` / `xl` |
| Name | `xl` |
| Description | `xl` / `full` |
| Address line | `full` or `xl` |
| Remark/Memo | `full` |
| Search box | retain current search sizing; do not force semantic form width |

This is guidance, not a blind regex transformation.

---

# 24. Do not auto-transform by search/replace

The coding agent must not perform a blind operation such as:

```text
all DxComboBox → erp-editor--sm
all DxTextBox → erp-editor--md
all DxSpinEdit → erp-editor--sm
```

Control type does not determine business width.

Examples:

- customer combo must be wide;
- currency combo can be small;
- description text box must be wide;
- prefix text box can be small;
- quantity spin edit can be small;
- amount spin edit may need medium.

Every field must be classified semantically.

---

# 25. Existing grid-column sizing is separate

The repo already has:

```text
GridColumnSizing
GridColumnSize
GridColumnSizingTests
```

This project must not merge form editor sizing with grid column sizing.

They solve related visual issues but are separate contracts:

```text
grid column width != form editor width
```

Do not modify `GridColumnSizing` as part of this task unless a separate grid defect is found and approved.

---

# 26. Accessibility and keyboard acceptance

For every changed form:

- label remains associated/visually clear;
- required indication remains;
- validation remains visible;
- dropdown button remains keyboard reachable;
- tab order follows original DOM order;
- no field is hidden because its width is constrained;
- 125% zoom remains usable;
- mobile input does not become unusably narrow;
- no essential meaning is communicated only by width/color.

Do not use `overflow:hidden` on form editors as a cosmetic shortcut.

---

# 27. Performance impact

This change should have negligible runtime performance impact.

Do not add:

- JavaScript measurement loops;
- ResizeObserver-based editor sizing;
- DOM scanning;
- runtime width calculations;
- per-field JS interop.

CSS/layout primitives are sufficient.

---

# 28. Definition of Done — repository-wide

The task is complete only when all are true:

- [ ] All `ErpWeb.UI/**/*.razor` pages were classified in the migration manifest.
- [ ] Shared form CSS is opt-in, not global.
- [ ] Shared CSS has no broad DevExpress editor override.
- [ ] Admin Settings reported alignment defect is fixed.
- [ ] Canary pages across all archetypes passed visual acceptance.
- [ ] Master-data forms use semantic editor widths where useful.
- [ ] Sales commercial-document architecture remains `SaDocPage`/`sdoc-*`.
- [ ] Purchase commercial-document architecture remains intact.
- [ ] Inventory transaction anatomy remains intact.
- [ ] Production workflow architecture remains intact.
- [ ] Existing field order remains unless a separately documented improvement was approved.
- [ ] `w-100` remains valid for controls inside semantically sized wrappers.
- [ ] Address/description/memo fields retain appropriate width.
- [ ] Mobile forms remain usable.
- [ ] Light/dark themes pass.
- [ ] Required-field styling still works.
- [ ] Validation messages still appear correctly.
- [ ] No bindings/event handlers/business logic changed unexpectedly.
- [ ] No EF/database/service change exists.
- [ ] `dotnet build ErpWeb.slnx` succeeds.
- [ ] `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --no-build` succeeds.
- [ ] UI source-contract tests succeed.
- [ ] Final diff review confirms UI-only scope.
- [ ] Migration batches are separately revertible.

---

# 29. Hard rejection / STOP conditions

The implementation is **NOT APPROVED** if any of the following occurs:

1. A bare global `.dxbl-*` editor width override is added.
2. `.w-100` is globally redefined.
3. `.iv-field` is globally changed before canary proof.
4. All 300+ UI/CSS files are mass-edited in one run.
5. A regex blindly assigns width by DevExpress control type.
6. Sales/Purchase `SaDocPage` is replaced.
7. Production workflow pages are converted to a generic commercial form.
8. Existing field order is broadly rearranged during the first pass.
9. A binding/event expression changes without a functional reason.
10. Business/service/database code changes under this UI task.
11. A canary shows clipping/overflow/mobile regression and the agent continues anyway.
12. The agent claims visual verification without actually using a browser.
13. A new browser automation framework is added without approval.
14. Build/tests fail and the agent continues to later batches.
15. The final diff cannot be cleanly described as UI layout-only.

---

# 30. AI coding agent execution sequence

Use this exact order:

```text
STEP 1
Read:
- erp-ui-core
- erp-transaction-ui-standard
- all module skills relevant to pages being changed

STEP 2
Scan every ErpWeb.UI Razor/CSS page.
Create the form-layout migration manifest.

STEP 3
Create opt-in erp-form-layout.css.
Load it from App.razor.
Add source-level contract tests.
Build/test.
No mass page migration yet.

STEP 4
Migrate Admin Settings canary.
Build/test.
Verify visually.

STEP 5
Migrate one master, one Sales document, one Inventory transaction,
and one Production workflow canary.
Build/test after each archetype.
Verify visually.

STEP 6
STOP if any canary fails.
Fix shared primitive or page-specific usage.
Do not patch symptoms with arbitrary margins.

STEP 7
Roll out module-by-module in separate commits.
Preserve every page's established archetype.

STEP 8
After each module:
- build
- tests
- visual representative check
- diff review
- update migration manifest

STEP 9
Final repository audit:
- no unclassified editable pages
- no accidental global selectors
- no behavior changes
- no horizontal/vertical clipping
- all checks green

STEP 10
Produce final implementation report:
- pages migrated
- pages intentionally excluded
- semantic-size exceptions
- build/test results
- visual verification actually performed
- any follow-up UI refinements
```

---

# 31. Expected user-facing result

The ERP should retain its current familiar workflows, but forms should feel intentionally designed.

Example:

Before:

```text
Currency
[                                      MYR ▼ ]

UOM
[                                      PCS ▼ ]

Description
[                                      ...   ]
```

After:

```text
Currency        UOM
[ MYR ▼ ]       [ PCS ▼ ]

Description
[                                      ...   ]
```

Same data.  
Same workflow.  
Same validation.  
Same binding.  
Better visual hierarchy and faster scanning.

---

# 32. Final approval assessment

| Area | Rating |
|---|---:|
| Repository relevance | 10/10 |
| Business-logic safety | 10/10 |
| Rollback safety | 10/10 |
| DevExpress compatibility strategy | 10/10 |
| Cross-module consistency | 10/10 |
| Responsive safety | 10/10 |
| Regression-control strategy | 10/10 |
| AI-agent implementability | 10/10 |
| Long-term maintainability | 10/10 |
| User-facing ERP professionalism | 10/10 |

## Final status

# **APPROVED FOR IMPLEMENTATION — 10/10**

The approval depends on following the staged, opt-in rollout.  
A broad global CSS rewrite or mass automated width replacement is explicitly **not approved**.

The central safety rule is:

> **Add the shared standard without changing existing pages; prove it on canaries; then migrate one page family at a time.**

That is what prevents a repository-wide UI cleanup from turning into a repository-wide UI regression.
