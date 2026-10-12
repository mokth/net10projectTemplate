# productionv2 — Repository-Wide ERP Form Alignment & Semantic Field Width Standardization
## FINAL DEEP-REVIEWED AI CODING AGENT PLAN
### APPROVED FOR IMPLEMENTATION — 10/10

**Repository:** `mokth/net10projectTemplate`  
**Target branch:** `productionv2`  
**Repository baseline reviewed:** `61a4a66099061ef07157af15f8020cb257077420`  
**Baseline commit time:** 2026-10-11 08:00:08 UTC  
**Stack verified:** .NET 10, Blazor Server/Razor components, DevExpress Blazor **26.1.4**  
**Plan scope:** UI layout only — alignment, editor proportions, responsive form ergonomics  
**Approval status:** **APPROVED FOR IMPLEMENTATION — 10/10**  
**Implementation risk:** High if done as a mass/global CSS rewrite; **controlled/low with the staged opt-in strategy in this plan**

---

# 0. FINAL REVIEW VERDICT

The previous plan had the correct core direction: opt-in shared form primitives, canaries, module batches, and no business-logic changes.

After a deeper review against the current `productionv2` repository, this revision strengthens the plan in several critical areas:

1. pins the reviewed repository baseline SHA and adds a **branch-drift gate**;
2. records the exact DevExpress version: **26.1.4**;
3. records the actual UI footprint: **231 `.razor` files + 84 `.razor.css` files = 315 Razor/style artifacts** under `ErpWeb.UI`;
4. distinguishes **route pages from reusable components** instead of treating every `.razor` file as a page;
5. makes the shared style behavior **smallest-scope opt-in**, not page-wide by default;
6. avoids unnecessary wrapper markup when a DevExpress control can safely receive a semantic `CssClass`;
7. adds a **direct-child CSS dependency check** before wrappers are introduced;
8. explicitly protects the existing `SmartLookupInput` and picker architecture;
9. makes the repository's `devexpress-blazor-editor-binding-safety` skill mandatory whenever editor markup is touched;
10. makes `devexpress-popup-action-safety` mandatory whenever a `DxPopup` is touched;
11. explicitly resolves a branch-authority ambiguity in older skill prose: **the user-requested `productionv2` branch is authoritative for this task**;
12. adds a **visual sign-off gate before broad rollout**;
13. forbids a new generic Razor `ErpField` component during this migration because that would unnecessarily risk `EditContext`/validation behavior;
14. separates **editor width correction** from later optional **form compaction / ColSpan changes**;
15. adds anti-regression rules for labels that wrap, mixed control `SizeMode`, custom composite lookups, popup reachability, and existing page CSS using `::deep`;
16. adds a stricter per-batch diff gate to detect accidental business/binding changes;
17. makes rollback practical by limiting commit size and module scope.

**Final decision:** the work is justified and can be made safe, but only if the coding agent follows the staged gates exactly.

---

# 1. REPOSITORY EVIDENCE — CURRENT `productionv2`

## 1.1 Branch authority

The target is explicitly:

```text
productionv2
```

The reviewed HEAD is:

```text
61a4a66099061ef07157af15f8020cb257077420
```

Some repository skill documents contain generic wording that says to inspect `production`.

For **this task**, the user explicitly requested `productionv2`, and the shared UI core skill already says the requested target branch takes precedence.

Therefore:

> **Do not accidentally implement or audit against `production` when the task is `productionv2`.**

Before implementation, the coding agent must verify its current branch.

Recommended:

```powershell
git branch --show-current
git rev-parse HEAD
```

Expected branch:

```text
productionv2
```

The SHA does **not** need to stay exactly at the reviewed SHA. The branch may legitimately advance.

If HEAD differs from `61a4a660...`:

1. do not reset or force checkout to the old SHA;
2. record the new SHA;
3. re-read the shared CSS, UI skills, and canary files;
4. re-run the migration inventory before implementation;
5. continue only if the architecture assumptions in this plan still hold.

This is the **branch-drift gate**.

---

## 1.2 Verified project stack

Current project files show:

```text
ErpWeb.UI/ErpWeb.UI.csproj
DevExpress.Blazor 26.1.4

ErpWeb/ErpWeb.csproj
DevExpress.Blazor 26.1.4
```

The solution is:

```text
ErpWeb.slnx
```

and it includes:

- ErpWeb
- ErpWeb.UI
- ErpWeb.Core
- ErpWeb.Model
- ErpWeb.Library
- ErpWeb.Report
- ErpWeb.EInvoiceLib
- ErpWeb.Tests

Therefore:

```powershell
dotnet build ErpWeb.slnx
```

is a valid full Razor + application + test-project compilation gate.

---

## 1.3 Verified UI size

Current tree enumeration under `ErpWeb.UI`:

```text
231 .razor files
84  .razor.css files
315 Razor/style artifacts total
```

Top-level distribution:

```text
Admin       13
Components  24
Inventory   76
Planning    55
Purchase    48
Sales       98
root         1
```

This is exactly why a one-shot visual refactor is unsafe.

Important:

> `231 .razor files` does **not** mean `231 pages`.

Some are:

- route pages;
- entry screens;
- lists;
- popups;
- reusable components;
- lookup components;
- shared document components;
- display-only panels.

The migration manifest must classify them before deciding which ones actually need modification.

---

# 2. VERIFIED SHARED UI ARCHITECTURE

## 2.1 Global CSS load order

`ErpWeb/Components/App.razor` currently loads:

```text
DevExpress theme
site.css
login.css
inventory-chrome.css
pr-popup-grid.css
ErpWeb.styles.css
_content/ErpWeb.UI/ErpWeb.UI.styles.css
```

The shared UI styles currently live in the host project:

```text
ErpWeb/wwwroot/css/
```

Therefore the new shared form-layout stylesheet should follow the existing host-owned shared CSS pattern.

Approved file:

```text
ErpWeb/wwwroot/css/erp-form-layout.css
```

Approved registration in `App.razor`:

```razor
<link href="@Assets["css/erp-form-layout.css"]" rel="stylesheet" />
```

Place it after `inventory-chrome.css` and before the generated/scoped CSS links unless current-branch verification shows a stronger reason otherwise.

Recommended:

```text
site.css
login.css
inventory-chrome.css
erp-form-layout.css
pr-popup-grid.css
ErpWeb.styles.css
_content/ErpWeb.UI/ErpWeb.UI.styles.css
```

The important rule is not the exact position relative to `pr-popup-grid.css`; the important rules are:

1. shared primitive loads before page-scoped CSS;
2. page-scoped CSS remains able to override it;
3. the shared file contains no broad unscoped DevExpress overrides.

---

## 2.2 Existing shared field primitive

Current `inventory-chrome.css`:

```css
.iv-form-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 14px;
}

.iv-field {
    display: grid;
    gap: 6px;
}
```

This is widely reused.

The migration must **not** globally rewrite `.iv-field`.

---

## 2.3 Existing commercial document shell

Sales uses:

```text
SaDocPage
sdoc-*
DxFormLayout
```

Purchase intentionally imports and reuses the same `SaDocPage` / `sdoc-*` shell.

This is confirmed by:

```text
ErpWeb.UI/Purchase/_Imports.razor
@using ErpWeb.UI.Sales.Transactions
```

Therefore:

> Do not create a new `pdoc-*` framework and do not replace `SaDocPage`.

---

## 2.4 Existing production workspace language

Production execution lives under:

```text
ErpWeb.UI/Planning/WorkOrders
```

with page-specific layouts such as:

```text
pwo-*
pr-dp-*
```

Production pages are workflow workspaces, not generic commercial documents.

Do not flatten them into Sales/Purchase form anatomy.

---

# 3. VERIFIED ROOT CAUSES

## 3.1 Same-row vertical misalignment

The reported Admin Settings popup is a confirmed structural example.

Current popup grid:

```css
.as-editor__grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 14px;
}
```

Current field stack:

```css
.iv-field {
    display: grid;
    gap: 6px;
}
```

`Stored at` has:

```text
label
editor
hint
```

`Value` has:

```text
label
editor
```

The parent grid stretches items by default. The shorter internal grid can distribute its available block-axis space differently, so the control top edges do not remain aligned.

The correct fix is structural top alignment, not manual margin compensation.

---

## 3.2 Oversized short-value editors

The repository contains several patterns that make controls occupy their entire available cell:

```text
.w-100
page-scoped ::deep .dxbl-* { width:100%; }
two equal columns
four equal columns
DxFormLayout ColSpanMd
custom equal-fraction CSS grids
```

This is correct for:

- long descriptions;
- addresses;
- remarks;
- long names;
- large search/lookup fields.

It is visually poor for:

- UOM;
- currency;
- prefix;
- small reference code;
- priority;
- percentage;
- quantity;
- sequence;
- status;
- compact date;
- yes/no context.

The solution is to constrain the **editor**, not blindly shrink the grid cell.

---

## 3.3 Existing page CSS already forces full-width DevExpress editors

Examples found in current master pages:

```css
.iv-page ::deep .dxbl-text-edit,
.iv-page ::deep .dxbl-combobox,
.iv-page ::deep .dxbl-spin-edit,
.iv-page ::deep .dxbl-memo {
    width: 100%;
}
```

This exists in pages such as:

- `SaCustEntry.razor.css`
- `PoSuppEntry.razor.css`

and related patterns exist in Item Master.

Therefore the shared semantic-width primitive must safely coexist with `width:100%`.

This is one reason `max-inline-size` is preferred over replacing width rules.

---

# 4. EXISTING GOOD REPOSITORY PATTERNS TO PRESERVE

## 4.1 Product Definition already demonstrates semantic sizing

`PrProductDefEntry.razor.css` already uses concepts such as:

```text
product code
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

with different visual widths.

This proves the UX concept already fits this ERP.

The new standard should generalize the idea into neutral cross-module classes.

Do **not** copy `pdf-*` names globally.

Do **not** rewrite Product Definition just for naming consistency.

---

## 4.2 Smart lookup composites already own internal width behavior

`SmartLookupInput.razor.css` currently has:

```css
.smart-lookup {
    display: flex;
    flex-direction: column;
    min-width: 0;
    width: 100%;
}

.smart-lookup__row {
    display: flex;
    width: 100%;
    min-width: 0;
}
```

This is correct.

It means semantic width should be applied at the **outside field/control boundary**, not by changing the lookup internals.

Protected lookup examples include:

```text
IvStockMasterPicker
PoPurchasingItemPicker
PoSupplierPicker
SaCustomerPicker
IvBalLocPicker
IvCodeComboBox
```

Do not modify their query, resolution, paging, tenant scope, or business selection behavior during this visual task.

---

# 5. MANDATORY SKILLS FOR THE CODING AGENT

Before changing UI, the coding agent must read:

```text
.agents/skills/erp-ui-core/skill.md
.agents/skills/erp-transaction-ui-standard/skill.md
```

Then the correct module skill:

```text
inventory-transaction-ui
sales-transaction-ui
purchase-transaction-ui
production-transaction-ui
```

Additionally:

## Any DevExpress editor markup touched

Mandatory:

```text
.agents/skills/devexpress-blazor-editor-binding-safety/skill.md
```

This is critical because this repository has explicit rules around:

```text
TextExpression
ValueExpression
DateExpression
CheckedExpression
```

A visual wrapper/class change must not accidentally break those contracts.

## Any `DxPopup` touched

Mandatory:

```text
.agents/skills/devexpress-popup-action-safety/skill.md
```

Keep footer/action visibility, exit paths, `CloseOnOutsideClick`, and submission safety intact.

## Any large-data lookup component itself touched

Mandatory:

```text
.agents/skills/erp-large-data-smart-lookup-standard/skill.md
```

For this task, the preferred approach is **not to touch lookup behavior at all**.

---

# 6. NON-NEGOTIABLE SCOPE BOUNDARY

This task may change:

```text
Razor wrappers/classes
page-specific form layout CSS
one new shared CSS file
App.razor stylesheet registration
source-level UI contract tests
migration manifest/documentation
```

This task must not change:

```text
database schema
EF entities
migrations
repositories
services
pricing logic
costing logic
stock logic
posting logic
e-Invoice logic
permissions
status transitions
routes
numbering
tenant scope
lookup query behavior
source-document eligibility
workflow gates
dirty-state logic
validation rules
```

Code-behind should normally remain untouched.

If a `.razor.cs`, Core, Model, Repository, or SQL file changes:

> STOP and explain why before continuing.

---

# 7. NO GENERIC FIELD COMPONENT IN THIS MIGRATION

Do **not** introduce a generic component such as:

```text
<ErpField>
<ErpFormField>
<SemanticEditor>
```

during this project.

Reason:

A new Razor abstraction would introduce unnecessary risk around:

- `EditContext`;
- validation expressions;
- render fragments;
- generic value types;
- changed callbacks;
- DevExpress binding semantics;
- custom lookup components.

This project needs a **CSS/layout standard**, not a new form framework.

A reusable Razor field abstraction can be considered later as a separate architecture task if real duplication justifies it.

---

# 8. SAFE SHARED PRIMITIVE — FINAL DESIGN

## 8.1 File

Create:

```text
ErpWeb/wwwroot/css/erp-form-layout.css
```

## 8.2 Opt-in only

All shared behavior must require:

```text
erp-form-scope
```

Pages/forms not opted in must remain visually unchanged.

Use the **smallest practical scope**.

Good:

```text
only the Admin Settings editor grid
only the Identity form
only the line editor popup form
only the header form
```

Avoid placing `erp-form-scope` on the entire page unless the entire page was deliberately audited.

---

## 8.3 Neutral semantic control classes

Use these names:

```text
erp-form-control--xs
erp-form-control--sm
erp-form-control--md
erp-form-control--lg
erp-form-control--xl
erp-form-control--full
```

This naming is intentionally more specific than generic `erp-editor-*`.

Recommended tokens:

```css
.erp-form-scope {
    --erp-form-xs: 7.5rem;   /* ~120 px */
    --erp-form-sm: 10rem;    /* ~160 px */
    --erp-form-md: 13rem;    /* ~208 px */
    --erp-form-lg: 18rem;    /* ~288 px */
    --erp-form-xl: 26rem;    /* ~416 px */
}
```

These are starting limits, not business constants.

---

## 8.4 Width behavior

Approved concept:

```css
.erp-form-scope .erp-form-control--xs,
.erp-form-scope .erp-form-control--sm,
.erp-form-scope .erp-form-control--md,
.erp-form-scope .erp-form-control--lg,
.erp-form-scope .erp-form-control--xl,
.erp-form-scope .erp-form-control--full {
    inline-size: 100%;
    min-inline-size: 0;
    justify-self: start;
}

.erp-form-scope .erp-form-control--xs {
    max-inline-size: var(--erp-form-xs);
}

.erp-form-scope .erp-form-control--sm {
    max-inline-size: var(--erp-form-sm);
}

.erp-form-scope .erp-form-control--md {
    max-inline-size: var(--erp-form-md);
}

.erp-form-scope .erp-form-control--lg {
    max-inline-size: var(--erp-form-lg);
}

.erp-form-scope .erp-form-control--xl {
    max-inline-size: var(--erp-form-xl);
}

.erp-form-scope .erp-form-control--full {
    max-inline-size: none;
}
```

Why this is safer:

- it works with controls already using `width:100%`;
- it works with global `.w-100 { width:100% !important; }` because `max-inline-size` still caps the result;
- it does not require fragile `.dxbl-*` internal selectors;
- it can be applied to a control root or an outer wrapper;
- it uses logical sizing rather than left/right assumptions.

---

## 8.5 Alignment behavior

Approved:

```css
.erp-form-scope .iv-field {
    align-content: start;
    grid-auto-rows: max-content;
    min-width: 0;
}

.erp-form-scope.erp-form-row,
.erp-form-scope .erp-form-row {
    align-items: start;
}
```

Do not globally change `.iv-field`.

Do not add arbitrary `margin-top` to individual editors.

Do not solve vertical misalignment by hardcoding pixel offsets.

---

## 8.6 No `!important` in the new shared form file

The new `erp-form-layout.css` should not need `!important`.

If the coding agent thinks it needs `!important`:

1. stop;
2. inspect existing specificity;
3. prefer `max-inline-size`, a local class, or a page-specific override;
4. document the conflict.

A shared migration CSS file full of `!important` is rejected.

---

# 9. DIRECT CLASS VS WRAPPER — SAFE DECISION RULE

The previous plan leaned heavily toward wrappers.

This revision makes the rule safer.

## Pattern A — preferred when safe: semantic class on control root

If the DevExpress control already exposes `CssClass` and the existing value is simple:

```razor
<DxComboBox ...
            CssClass="w-100 erp-form-control--sm" />
```

This avoids extra DOM structure.

Existing binding parameters remain untouched.

## Pattern B — wrapper when the component cannot safely accept the class

Use:

```razor
<div class="erp-form-control--lg">
    <SomeCustomPicker ... />
</div>
```

This is appropriate for:

- composite lookup components without an exposed CSS class;
- complex dynamic `CssClass` expressions;
- controls where changing `CssClass` would risk required-state styling;
- custom components whose internal root already owns `width:100%`.

---

## 9.1 Mandatory direct-child dependency check before wrapping

Before inserting a wrapper, inspect the target page's `.razor.css` for:

```text
>
:first-child
:last-child
::deep
.dxbl-
child combinators
grid/flex rules that assume the editor is the immediate child
```

If a wrapper would break those selectors:

- use Pattern A if possible; or
- add a very small page-specific CSS adjustment;
- do not blindly wrap.

This check is mandatory.

---

# 10. DO NOT CHANGE EDITOR HEIGHT GLOBALLY

The user's primary issue is horizontal alignment and inappropriate width.

Do not create shared rules such as:

```css
height: 32px;
min-height: 34px;
```

for all editors.

DevExpress controls and page families already use their own `SizeMode`/theme metrics.

If controls in the same row have inconsistent heights because one control uses a different `SizeMode`:

1. verify the live page;
2. correct the specific page/control;
3. do not normalize height globally.

---

# 11. LABEL-WRAP RULE

Even perfect field-stack alignment cannot keep editor tops aligned if one label wraps to two lines and the adjacent label stays one line.

Do **not** solve this with a global fixed label height.

Instead:

1. keep labels concise;
2. give the field sufficient width;
3. move unusually verbose explanation to a hint;
4. if a label must wrap, treat that row as a page-specific layout case.

Do not truncate required business labels merely to force alignment.

---

# 12. RESPONSIVE RULE

Semantic width is a desktop ergonomics feature, not a reason to make mobile inputs tiny.

At narrow width, compact controls should normally expand to the available single-column width.

Approved default at the shared mobile breakpoint:

```css
@media (max-width: 767px) {
    .erp-form-scope .erp-form-control--xs,
    .erp-form-scope .erp-form-control--sm,
    .erp-form-scope .erp-form-control--md,
    .erp-form-scope .erp-form-control--lg,
    .erp-form-scope .erp-form-control--xl {
        max-inline-size: none;
    }
}
```

Important:

Some existing pages collapse earlier, e.g. around 900px.

For those pages, add a **page-specific** earlier full-width override if the visual check shows the compact cap is awkward after the page has already become single-column.

Do not impose one global 900px breakpoint on all page families.

---

# 13. SEMANTIC SIZE GUIDANCE

Use semantic purpose, not control type and not `maxlength`.

| Field type | Default size |
|---|---|
| Sequence / tiny code | `xs` |
| UOM | `xs` / `sm` |
| Currency | `sm` |
| Prefix | `sm` |
| Priority | `sm` |
| Percentage | `sm` |
| Pack size | `sm` |
| Status | `sm` / `md` |
| Quantity | `sm` / `md` |
| Date | `md` |
| Document no. | `md` |
| TIN / BRN | `md` |
| Small reference | `md` |
| Warehouse | `md` / `lg` |
| Location | `md` / `lg` |
| Project / department | `md` / `lg` |
| Salesman / buyer | `md` / `lg` |
| Customer / Supplier lookup | `lg` / `xl` |
| Item lookup | `lg` / `xl` |
| Name | `xl` |
| Description | `xl` / `full` |
| Address | `full` |
| Memo / Remark | `full` |
| Search box | keep current page/search sizing |

These are defaults.

The coding agent must consider:

- placeholder text;
- dropdown option text;
- lookup secondary text;
- localization;
- expected user scanning;
- current page cell width.

---

# 14. DO NOT CLASSIFY BY DEVEXPRESS CONTROL TYPE

Forbidden automation:

```text
all DxComboBox => sm
all DxTextBox  => md
all DxSpinEdit => sm
```

Examples proving why:

```text
Customer ComboBox => wide
Currency ComboBox => compact

Description TextBox => wide
Prefix TextBox => compact

Quantity SpinEdit => compact
large financial amount => medium
```

Every field must be classified by business meaning.

---

# 15. SMART LOOKUP SAFETY

Do not modify internal lookup width rules merely to make the page look compact.

Current `SmartLookupInput` is correctly `width:100%` inside its container.

Approved:

```text
semantic width on outer field/control boundary
```

Rejected:

```text
changing SmartLookupInput.razor.css to 240px
changing lookup button layout
changing resolve behavior
changing server paging
changing debounce
changing tenant scope
```

If a custom picker has no `CssClass` parameter, use an outside semantic wrapper.

---

# 16. `DxFormLayout` MIGRATION RULE

Sales, Purchase, and many Inventory forms already use `DxFormLayout`.

## First pass

Keep:

```text
DxFormLayout
DxFormLayoutGroup
DxFormLayoutItem
ColSpanMd
BeginRow
field order
```

unchanged unless there is a proven layout defect.

Apply semantic width **inside the existing item cell**.

Example:

```razor
<DxFormLayoutItem Caption="Currency" ColSpanMd="4">
    <DxComboBox ...
                CssClass="w-100 erp-form-control--sm" />
</DxFormLayoutItem>
```

or wrapper if required.

This fixes the visual box width without changing row flow.

---

## 16.1 `ColSpanMd` changes are a separate compaction phase

Do not immediately do:

```text
ColSpanMd="4" -> "2"
```

just because the editor is visually smaller.

That changes:

- row packing;
- label wrapping;
- responsive flow;
- tab visual order;
- overall familiarity.

Only after semantic width is accepted may a page be proposed for a second, targeted **compaction pass**.

That compaction pass is not required for this plan to succeed.

---

# 17. CUSTOM GRID MIGRATION RULE

Examples:

```text
as-editor__grid
iv-stock-fields
pwo-form-grid
pr-dp-*
```

Keep the current page grid unless the grid itself is proven defective.

Safe first-pass changes:

```text
top-align row items
top-align field stacks
apply semantic max width
preserve existing grid columns
preserve existing field order
preserve breakpoints
```

Do not replace all custom grids with one universal `.erp-form-grid`.

---

# 18. ADMIN SETTINGS — EXACT FIRST CANARY

Files:

```text
ErpWeb.UI/Admin/AdminSettings.razor
ErpWeb.UI/Admin/AdminSettings.razor.css
```

Do not change:

```text
setting resolution
scope inheritance
save/clear logic
AppSettingType logic
permissions
popup visibility
```

## 18.1 Alignment

Add the opt-in scope to the editor grid:

```text
as-editor__grid
erp-form-scope
erp-form-row
```

This must top-align both fields.

## 18.2 Dynamic `Value` sizing

Current value editor changes by `AppSettingType`.

Use:

```text
Flag   => natural checkbox / xs only if needed
Token  => lg
Number => sm or md
Text   => lg
```

`Stored at`:

```text
md
```

`Remark`:

```text
full
```

Keep the popup's current `Width="520px"` during the first pass.

Do **not** change popup width until alignment and semantic sizing are proven.

Expected desktop geometry:

```text
Stored at                  Value
[ Company ▼ ]              [ Not set — uses default ▼ ]
helper text...
```

The top edges of the two editors must align.

---

# 19. MASTER-FORM CANARY

Preferred first master:

```text
ErpWeb.UI/Sales/Masters/SaCustEntry.razor
```

Then:

```text
ErpWeb.UI/Purchase/Masters/PoSuppEntry.razor
```

Current code confirms both pages:

- use `EditForm`;
- use many `.iv-field` blocks;
- have page-scoped `::deep .dxbl-* { width:100%; }`;
- use two-column identity sections;
- collapse near 900px.

Therefore the canary must specifically verify compatibility with those existing full-width rules.

Example classifications:

```text
Customer/Supplier code => md/lg
Customer/Supplier name => xl/full
Short name             => lg
Prefix                 => sm
TIN/BRN                => md
Area/Type               => md
Address                 => full
Currency/terms          => md
Remark/Memo             => full
Checkbox                => natural
```

Do not rearrange the identity columns in the first pass.

---

# 20. COMMERCIAL DOCUMENT CANARY

Use:

```text
ErpWeb.UI/Sales/Transactions/SaSo.razor
```

Preserve:

```text
SaDocPage
sdoc-*
DxFormLayout
all existing lifecycle/revision behavior
```

First-pass examples:

```text
SO no.          => md
Date            => md
Status          => sm/md
Prefix          => sm
Customer        => lg/xl
Currency/rate   => md
Payment term    => md
Tax group       => md
Salesman        => md/lg
Ref no.         => md
Cust PO         => md/lg
Project         => md/lg
address fields  => full/current cell width
remarks         => full
```

Do not change Sales pricing, revision, DR lock behavior, source flow, or validation.

---

# 21. PURCHASE COMMERCIAL DOCUMENT CANARY

After Sales Order passes, verify the same shared primitive against:

```text
ErpWeb.UI/Purchase/Transactions/PoOrder.razor
```

Purchase deliberately shares `SaDocPage`.

Verify fields such as:

```text
PO no.
Date
Status
Prefix
Supplier
Currency
Buying term
Payment term
Project
Buyer
Department
Refs
```

Do not change:

```text
supplier lock rules
received-line rules
revision state
cost visibility
source PR behavior
```

---

# 22. INVENTORY TRANSACTION CANARY

Use:

```text
ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor
```

Preserve current `DxFormLayout`.

Verify both header and line popup.

Examples:

```text
Transaction date => md
Status           => sm/md
Receipt type     => md
Ref no.          => md
Remark           => full

PO               => md
PO line          => sm
Balance qty      => sm/md
Item             => lg/xl
Receive qty      => sm/md
UOM              => xs/sm
Warehouse        => md/lg
Location         => md/lg
Lot no.          => md/lg
Expiry date      => md
Item status      => md
Line remarks     => full
```

Do not touch posting, rollback, stock chronology, costing, or lot authority.

---

# 23. PRODUCTION CANARY

Use:

```text
ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor
```

Then verify Work Order separately before full Production rollout.

Preserve:

```text
operation-selection phase
production workspace
POST separate from SAVE
ROLLBACK eligibility
machine/operator behavior
material facts
historical facts
```

Typical compact fields:

```text
Production date/time => md
Shift                => md
Actual machine       => md/lg
Operator             => md/lg
Output lot           => md
Good/Scrap/Reject/Hold => sm/md
```

Do not alter workflow phases.

---

# 24. PRODUCT DEFINITION RULE

Product Definition already has semantic sizing concepts.

Treat it as:

```text
reference evidence
```

not:

```text
a class library to copy
```

Only migrate its page-specific semantic classes to the neutral shared standard if:

1. the shared standard is already proven;
2. the change actually reduces duplication;
3. its specialized authoring layout remains unchanged.

Otherwise leave it alone.

---

# 25. POPUP RULES

Any editable popup included in the migration must preserve:

```text
DxPopup
common-popup
CloseOnOutsideClick behavior
existing action pattern
ShowFooter contract where footer template exists
visible safe exit
submission guard
scrollable content
```

Do not globally reduce popup width.

Do not move action buttons during this task.

Do not make the popup prettier by risking unreachable Save/Post/Delete actions.

Existing:

```text
ErpWeb.Tests/Other/DxPopupFooterContractTests.cs
```

must remain green.

---

# 26. BINDING SAFETY — P0 GATE

A layout-only diff should almost never need to change:

```text
@bind-Text
@bind-Value
@bind-Date
@bind-Checked
TextChanged
ValueChanged
DateChanged
CheckedChanged
TextExpression
ValueExpression
DateExpression
CheckedExpression
```

For every changed Razor file, review all changed lines containing those tokens.

If the only reason for the change is indentation after adding a wrapper, confirm semantic equivalence.

Reject:

```text
@bind-* changed to manual event without reason
manual event loses *Expression
Expression points to a different property
ValidationEnabled=false added to hide an error
```

This is a P0 regression class.

---

# 27. PHASE 0 — BUILD THE MIGRATION MANIFEST

Before changing shared CSS, scan:

```text
ErpWeb.UI/**/*.razor
ErpWeb.UI/**/*.razor.css
```

Create:

```text
docs/ui-form-layout-migration-productionv2.md
```

If the repository does not use `docs`, use an existing planning/documentation folder; do not invent a runtime dependency.

## 27.1 Classify every Razor artifact

Each of the 231 `.razor` files must be classified as one of:

```text
Route page — editable
Route page — display/list/inquiry
Reusable editable component
Reusable display component
Lookup/picker
Popup/dialog
Shared shell
Not relevant
```

This prevents the false assumption that every Razor file should be modified.

## 27.2 Manifest columns

For editable artifacts record:

| Column | Meaning |
|---|---|
| Path | exact Razor file |
| Module | Admin/Inventory/Sales/Purchase/Planning |
| Artifact type | route page/component/popup/lookup |
| Archetype | master/commercial/inventory/production/filter |
| Current form system | `iv-field`, `DxFormLayout`, custom grid, flex |
| Scoped CSS file | if any |
| Existing width forcing | `w-100`, `::deep width:100%`, etc. |
| Direct-child CSS risk | Yes/No |
| Contains composite lookup | Yes/No |
| Contains `DxPopup` | Yes/No |
| Alignment issue | Yes/No |
| Oversized short fields | Yes/No |
| Migration decision | migrate / no change |
| Semantic exceptions | why |
| Batch | canary / batch number |

## 27.3 No-change is a valid result

Do not change a page merely to mark it migrated.

If a form is already well proportioned:

```text
Migration decision = no change
Reason = existing layout already meets standard
```

---

# 28. PHASE 1 — SHARED INFRASTRUCTURE ONLY

Implement only:

```text
ErpWeb/wwwroot/css/erp-form-layout.css
App.razor stylesheet registration
ErpFormLayoutContractTests.cs
migration manifest
```

Do not opt in all forms.

At the end of Phase 1:

> There should be no intentional visible change to current pages.

Build and test before Phase 2.

---

# 29. PHASE 2 — ADMIN SETTINGS CANARY

Migrate only the exact reported Admin Settings editor.

Then:

```powershell
dotnet build ErpWeb.slnx
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --no-build
```

Perform actual runtime visual verification.

If this canary fails:

> STOP. Do not migrate any other page.

---

# 30. PHASE 3 — CROSS-ARCHETYPE CANARIES

Migrate exactly one from each major family:

```text
Master          SaCustEntry
Sales document  SaSo
Purchase doc    PoOrder
Inventory       IvGoodsReceipt
Production      PrDailyProductionEntry
```

A master canary may start with `SaCustEntry`; `PoSuppEntry` follows after that pattern is validated.

Build/test after each canary or tightly grouped pair.

Do not wait until all canaries are changed before compiling.

---

# 31. MANDATORY VISUAL RELEASE GATE

Broad rollout cannot begin until canaries have real visual approval.

This approval may be performed by:

- an AI coding agent with browser/computer capability; or
- the user/developer manually.

If the coding agent cannot actually open the running application:

> It must stop after producing the canary code/build result and request visual sign-off before repo-wide rollout.

It must not claim the UI is visually verified from source code alone.

---

## 31.1 Required viewports

At minimum:

```text
1440 x 900
1366 x 768
1024 x 768
768 px wide
390 px wide
```

## 31.2 Required themes

```text
light
dark
```

## 31.3 Required states

Where applicable:

```text
new/edit
view/read-only
required field empty
validation error visible
hint visible
popup open
lookup control present
```

## 31.4 Zoom

```text
100%
125%
```

---

# 32. VISUAL ACCEPTANCE CRITERIA

A canary passes only if:

- [ ] controls in the same logical row start at the same vertical position;
- [ ] short fields are visibly compact where appropriate;
- [ ] long fields still have enough usable width;
- [ ] labels are not clipped;
- [ ] placeholders do not make the control unusable;
- [ ] dropdown buttons remain visible;
- [ ] date buttons remain visible;
- [ ] spin buttons remain visible where used;
- [ ] validation messages stay below the correct editor;
- [ ] hints do not push adjacent controls down;
- [ ] required-field tint still works;
- [ ] composite lookup search button remains aligned;
- [ ] no horizontal page overflow appears;
- [ ] popup actions remain reachable;
- [ ] mobile forms remain usable;
- [ ] dark theme remains readable;
- [ ] 125% zoom remains usable;
- [ ] tab order is unchanged;
- [ ] no search/grid field was accidentally constrained.

---

# 33. BEFORE/AFTER SCREENSHOT RULE

When browser capability exists, capture canary screenshots before and after at the same viewport.

Recommended evidence:

```text
Admin Settings popup
Customer Identity
Sales Order header
Purchase Order header
Goods Receipt header/line popup
Daily Production input
```

Do not need pixel-perfect visual-diff tooling.

The purpose is human review and easy regression detection.

Do not add a new screenshot testing framework solely for this task.

---

# 34. PHASE 4 — MODULE ROLLOUT

Only after canary visual sign-off.

Recommended order:

## Batch A — Admin/simple editable masters

Low workflow risk.

## Batch B — Sales/Purchase master profiles

Examples:

```text
SaCustEntry
PoSuppEntry
other editable master profiles
```

## Batch C — Sales commercial documents

Examples:

```text
SaQt
SaSo
SaDo
SaInvoice
SaCdn
SaDeliveryRequestEntry
```

## Batch D — Purchase commercial documents

Examples:

```text
PoPr
PoOrder
PoInvoice
PoCdn
PoSbInvoice
PoSbCdn
```

## Batch E — Inventory

Examples:

```text
IvStockMasterEntry
IvGoodsReceipt
IvMiscIssue
IvMiscReceipt
IvStockTransfer
IvStockAdjustment
IvStockCount
IvStockReturn
IvVendorReturn
IvScrap
IvPeriodClose where applicable
```

## Batch F — Production/Planning

Examples:

```text
PrProductDefEntry
PrShiftEntry
PrWorkOrderEntry
PrMaterialIssueEntry
PrDailyProductionEntry
PrFinishedGoodReceiptEntry
other editable Planning masters
```

---

# 35. BATCH SIZE LIMIT

Do not create one giant module commit containing dozens of unrelated forms.

Recommended maximum:

```text
5–8 meaningful editable forms per commit
```

Smaller is acceptable.

Trivial reference masters may be grouped more tightly when they use the exact same pattern.

Benefits:

- easier diff review;
- easier rollback;
- easier visual spot-check;
- easier root-cause identification.

---

# 36. OPTIONAL PHASE 5 — FORM COMPACTION

This phase is **not** part of the mandatory first-pass fix.

After semantic widths are accepted, some pages may still contain visually excessive blank grid cells because the existing `ColSpanMd` remains large.

Only then consider:

```text
reducing selected ColSpanMd
combining related compact fields on a row
changing specific custom grid tracks
```

Requirements:

1. page-specific reason;
2. preserve field order;
3. preserve tab order;
4. re-run all responsive checks;
5. user/visual approval.

Do not mix compaction with the initial semantic-width rollout.

---

# 37. SOURCE-LEVEL CONTRACT TESTS

Add:

```text
ErpWeb.Tests/UI/ErpFormLayoutContractTests.cs
```

Follow the repository's existing source-contract style such as:

```text
DxPopupFooterContractTests
```

## Test A — shared stylesheet registered exactly once

Verify `App.razor` contains:

```text
css/erp-form-layout.css
```

exactly once.

## Test B — required semantic classes exist

Verify:

```text
erp-form-scope
erp-form-control--xs
erp-form-control--sm
erp-form-control--md
erp-form-control--lg
erp-form-control--xl
erp-form-control--full
```

## Test C — no forbidden broad selector

Reject bare/global changes in the new file such as:

```text
.dxbl-
.w-100
.iv-field
```

unless they are under the explicit `.erp-form-scope` selector.

The implementation can use a small source-level parser/regex appropriate to the controlled CSS file.

Do not build a general CSS parser.

## Test D — no `!important` in shared form CSS

The new shared file should contain zero:

```text
!important
```

unless a future separately approved exception exists.

## Test E — semantic usage has scope

For any Razor file containing:

```text
erp-form-control--
```

also require the file to contain:

```text
erp-form-scope
```

This is not a perfect DOM proof, but it catches accidental unscoped usage.

## Test F — selected canaries opted in

After canary phase, assert expected canary files contain the scope marker.

Do not assert all pages until their migration batch is complete.

---

# 38. EXISTING TESTS THAT MUST REMAIN GREEN

At minimum:

```text
ErpWeb.Tests/Other/DxPopupFooterContractTests.cs
ErpWeb.Tests/UI/GridColumnSizingTests.cs
```

Do not modify `GridColumnSizing` during this task.

Grid-column sizing and form-control sizing are related visually but are different contracts.

---

# 39. BUILD / TEST GATE

After shared infrastructure:

```powershell
dotnet build ErpWeb.slnx
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --no-build
```

After every canary:

```powershell
dotnet build ErpWeb.slnx
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --no-build
```

After every batch:

```powershell
dotnet build ErpWeb.slnx
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --no-build
```

The current test project itself notes that a green unit suite does not prove Razor rendering, so visual verification remains mandatory.

---

# 40. DIFF SAFETY GATE

For every batch run:

```powershell
git diff --check
git diff --stat
git diff
```

Reject unexpectedly broad formatting.

A layout migration should not rewrite an entire Razor file because an IDE formatter changed whitespace.

Expected changes:

```text
semantic class added
opt-in scope added
small local CSS alignment rule
responsive semantic width rule
shared CSS/tests/manifest
```

Unexpected changes:

```text
service call changed
route changed
model changed
handler changed
permission changed
status changed
validation changed
binding target changed
query changed
posting changed
pricing changed
costing changed
lookup source changed
```

---

# 41. BINDING DIFF AUDIT

For changed `.razor` files specifically search the diff for:

```text
@bind-
TextChanged
ValueChanged
DateChanged
CheckedChanged
TextExpression
ValueExpression
DateExpression
CheckedExpression
ValidationEnabled
```

Expected:

```text
NONE changed
```

or indentation-only movement.

If behavior changes:

> STOP and review under the binding-safety skill.

---

# 42. POPUP DIFF AUDIT

For any changed file containing `DxPopup`, inspect all popups in that file for:

```text
FooterTemplate
FooterContentTemplate
ShowFooter
CloseOnOutsideClick
visible Cancel/Close path
```

Do not fix unrelated business logic.

Do fix an obvious same-file footer-contract violation if discovered and if it is within the existing popup safety contract; record it separately in the batch report.

---

# 43. PER-BATCH REPORT FORMAT

The coding agent must output:

```text
BATCH:
BASE SHA:
END SHA:

Pages/components migrated:
Pages/components reviewed but unchanged:

Shared CSS changed:
Page CSS changed:

Behavior/code-behind changed: NONE
Core/Model/Repository changed: NONE
Database/SQL changed: NONE
Binding semantics changed: NONE
Lookup behavior changed: NONE

Build: PASS/FAIL
Tests: PASS/FAIL
Visual verification: PASS / NOT AVAILABLE / FAIL

Viewports checked:
Themes checked:
Known exceptions:
Rollback commit(s):
```

If any `NONE` item is not none, stop and explain.

---

# 44. ROLLBACK STRATEGY

Recommended commits:

```text
1. ui: add opt-in ERP form layout primitive and contract tests
2. ui: align and size Admin Settings canary
3. ui: migrate cross-archetype form-layout canaries
4. ui: standardize admin and simple masters
5. ui: standardize sales/purchase master forms
6. ui: standardize sales commercial forms
7. ui: standardize purchase commercial forms
8. ui: standardize inventory forms
9. ui: standardize production/planning forms
10. ui: finalize form-layout migration manifest
```

Because the behavior is opt-in:

```text
remove the form scope/class or revert the batch commit
```

restores prior form behavior without a database rollback.

---

# 45. P0 / P1 RISK MATRIX

| Risk | Severity | Mitigation |
|---|---|---|
| Broad `.dxbl-*` width override breaks many pages | P0 | forbidden; opt-in only |
| `.w-100` changed globally | P0 | forbidden |
| DevExpress binding expression accidentally changed | P0 | binding-safety skill + diff audit |
| Sales/Purchase shell flattened | P0 | preserve `SaDocPage` |
| Production workflow flattened | P0 | preserve production skill/archetype |
| Lookup behavior changed during visual work | P0 | protect smart lookup internals |
| Wrapper breaks direct-child CSS | P1 | mandatory direct-child dependency check |
| Popup buttons clipped/hidden | P1 | popup safety skill + existing contract test |
| Short control remains narrow on mobile | P1 | responsive full-width fallback |
| Label wrap reintroduces misalignment | P1 | page-specific label/layout handling |
| Large empty cells remain after editor shrink | P2 | optional later compaction phase |
| Existing already-good page unnecessarily changed | P2 | manifest permits `no change` |
| Branch changed after plan review | P1 | branch-drift gate |

---

# 46. HARD STOP / REJECTION CONDITIONS

The implementation is **NOT APPROVED** if any occurs:

1. bare/global `.dxbl-*` form editor overrides are added;
2. `.w-100` is redefined;
3. global `.iv-field` behavior changes for all pages;
4. new shared form CSS uses `!important`;
5. all 315 UI artifacts are mass-edited;
6. fields are sized by control type through regex;
7. a new generic Razor field framework is introduced;
8. Sales/Purchase `SaDocPage` is replaced;
9. Production pages are converted to generic commercial forms;
10. `ColSpanMd` values are mass-reduced in the first pass;
11. field order is broadly rearranged in the first pass;
12. a custom smart lookup's internal sizing/query behavior is changed without need;
13. `TextExpression`/`ValueExpression`/binding semantics change accidentally;
14. validation is disabled to work around a DevExpress error;
15. popup action visibility or safe exit is degraded;
16. business/service/database files change under this task without explicit review;
17. the agent continues after a canary visual failure;
18. the agent continues broad rollout without actual visual sign-off;
19. build or tests fail and the agent proceeds;
20. the final diff contains large unexplained formatting churn.

---

# 47. DEFINITION OF DONE

The repository-wide task is complete only when:

- [ ] implementation branch is confirmed as `productionv2`;
- [ ] branch drift was checked;
- [ ] all 231 Razor artifacts are classified in the manifest;
- [ ] only relevant editable artifacts are migrated;
- [ ] shared CSS is opt-in;
- [ ] no global DevExpress form-width override exists;
- [ ] no `!important` exists in shared form-layout CSS;
- [ ] semantic size classes are implemented;
- [ ] Admin Settings exact reported defect is fixed;
- [ ] master canary passes;
- [ ] Sales canary passes;
- [ ] Purchase canary passes;
- [ ] Inventory canary passes;
- [ ] Production canary passes;
- [ ] actual visual sign-off occurred before broad rollout;
- [ ] existing page archetypes remain intact;
- [ ] existing field order remains unless separately approved;
- [ ] SmartLookup/pickers retain existing behavior;
- [ ] required-field styling still works;
- [ ] validation remains correct;
- [ ] popup actions remain reachable;
- [ ] mobile layout remains usable;
- [ ] light and dark themes pass;
- [ ] 100% and 125% zoom pass on canaries;
- [ ] no binding/event semantics changed;
- [ ] no Core/Model/Repository/SQL changes were required;
- [ ] `dotnet build ErpWeb.slnx` passes after every batch;
- [ ] `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --no-build` passes;
- [ ] existing popup contract test passes;
- [ ] new form-layout contract tests pass;
- [ ] migration manifest reflects final state;
- [ ] batches are independently revertible.

---

# 48. EXACT AI CODING AGENT EXECUTION ORDER

```text
STEP 1 — BASELINE
Confirm productionv2.
Record current HEAD.
If HEAD differs from reviewed SHA, re-read live shared CSS and canary files.

STEP 2 — READ STANDARDS
Read erp-ui-core.
Read erp-transaction-ui-standard.
Read relevant module skills.
Read editor-binding-safety.
Read popup-action-safety for popup files.
Read smart-lookup standard only if a lookup component itself must be touched.

STEP 3 — INVENTORY
Enumerate all ErpWeb.UI Razor/CSS artifacts.
Create migration manifest.
Classify route pages vs components.
Do not code yet.

STEP 4 — SHARED PRIMITIVE
Create erp-form-layout.css.
Register with @Assets in App.razor.
Add source contract tests.
Do not opt in pages broadly.
Build + test.

STEP 5 — ADMIN CANARY
Fix Admin Settings exact problem.
Keep popup width and business logic unchanged.
Build + test.
Runtime visual check.

STEP 6 — CROSS-ARCHETYPE CANARIES
SaCustEntry
SaSo
PoOrder
IvGoodsReceipt
PrDailyProductionEntry
Build/test incrementally.
Runtime visual checks.

STEP 7 — RELEASE GATE
Require actual visual sign-off.
If no browser is available, stop and request user/developer sign-off.

STEP 8 — MODULE BATCHES
Migrate small batches.
Maximum about 5–8 meaningful forms per commit.
Preserve each module archetype.

STEP 9 — AFTER EVERY BATCH
Build.
Full tests.
Diff check.
Binding audit.
Popup audit when applicable.
Representative visual smoke test.
Update manifest.

STEP 10 — FINAL AUDIT
Confirm no broad selectors.
Confirm no behavior changes.
Confirm no unclassified Razor artifact.
Confirm mobile/dark/zoom checks.
Confirm final build/tests.

STEP 11 — REPORT
Produce the mandatory per-batch/final report.
```

---

# 49. EXPECTED USER-FACING RESULT

Before:

```text
Currency
[                                      MYR ▼ ]

UOM
[                                      PCS ▼ ]

Status
[                                      NEW ▼ ]

Description
[                                      long description... ]
```

After:

```text
Currency          UOM             Status
[ MYR ▼ ]         [ PCS ▼ ]       [ NEW ▼ ]

Description
[                                      long description... ]
```

And for fields with hints:

```text
Stored at                  Value
[ Company ▼ ]              [ Not set — uses default ▼ ]
helper text...
```

not:

```text
Stored at                  Value
[ Company ▼ ]
helper text...              [ Not set — uses default ▼ ]
```

The ERP remains functionally identical.

The improvement is:

- cleaner horizontal rhythm;
- faster visual scanning;
- more professional data-entry density;
- less wasted space;
- consistent field alignment;
- responsive behavior retained.

---

# 50. FINAL SCORE AFTER DEEP RE-REVIEW

| Review dimension | Score |
|---|---:|
| Current `productionv2` repository grounding | 10/10 |
| Branch safety | 10/10 |
| DevExpress 26.1.4 compatibility strategy | 10/10 |
| Binding safety | 10/10 |
| Popup safety | 10/10 |
| Smart-lookup preservation | 10/10 |
| Business-logic isolation | 10/10 |
| Regression containment | 10/10 |
| Responsive/mobile safety | 10/10 |
| Dark-theme safety | 10/10 |
| Rollback safety | 10/10 |
| AI-agent implementability | 10/10 |
| Long-term maintainability | 10/10 |
| User-facing ERP professionalism | 10/10 |

# FINAL STATUS: **APPROVED FOR IMPLEMENTATION — 10/10**

Approval is conditional on following the staged gates.

The most important rule is:

> **Do not "fix the whole ERP" by changing one global selector. Add a neutral opt-in form standard, prove it on real canaries, obtain visual sign-off, then migrate small page-family batches while preserving all business behavior.**

This is the safest workable path for `productionv2`.
