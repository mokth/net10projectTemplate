---
name: production-transaction-ui
description: >-
  UI/UX standard for production execution and work-order transaction pages in
  mokth/net10projectTemplate. Use for Work Order, Material Issue / Issue to Production,
  Daily Production and Finished Good Receipt screens. The live UI is under
  ErpWeb.UI/Planning/WorkOrders. Requires erp-ui-core and preserves workflow/workspace
  interaction rather than forcing a generic header/detail document.
---

# Production Transaction UI Skill

## Required companion

Read first:

`.agents/skills/erp-ui-core/skill.md`

---

# 1. Actual repository location

Production execution UI currently lives under:

`ErpWeb.UI/Planning/WorkOrders`

Do not create a parallel `ErpWeb.UI/Production/Transactions` hierarchy unless an explicit architecture task requires it.

Core services/models may live under both Planning and Production namespaces. Follow the existing target feature.

---

# 2. Mandatory live references

Always inspect the exact workflow being changed.

## Work Order

- `PrWorkOrderList.razor/.cs`
- `PrWorkOrderEntry.razor/.cs/.css`

## Material Issue / Issue to Production

- `PrMaterialIssueList.razor/.cs/.css`
- `PrMaterialIssueEntry.razor/.cs/.css`
- `ErpWeb.UI/Planning/Components/PrMaterialIssueBomDialog.razor`
- `ErpWeb.UI/Planning/Components/PrMaterialAllocationDialog.razor`

## Daily Production

- `PrDailyProductionList.razor/.cs/.css`
- `PrDailyProductionEntry.razor/.cs/.css`

## Finished Good Receipt

- `PrFinishedGoodReceiptList.razor/.cs/.css`
- `PrFinishedGoodReceiptEntry.razor/.cs/.css`

## Shared production picker chrome

- `ErpWeb/wwwroot/css/pr-popup-grid.css`

Also inspect the shared list core from `erp-ui-core`.

---

# 3. Production UI Standard Evidence Block

Before coding:

```text
PRODUCTION UI STANDARD EVIDENCE
Target workflow:
List reference:
Entry reference:
Workflow phase(s):
Source selector:
Locked context after selection:
Draft identity:
Statuses:
Save gate:
Post/readiness gate:
Rollback gate:
Delete/cancel gate:
Correction/reopen behavior:
Historical facts shown:
Live values explicitly shown:
Permissions:
Route model:
Intentional deviations:
```

Do not write markup until the phases and gates are understood.

---

# 4. Core Production principle

A Production entry is often an **execution workspace**, not just a document form.

The UI must help the operator answer:

1. What Work Order / operation / production lot am I working on?
2. What is allowed at this stage?
3. What quantity am I producing/issuing/receiving?
4. What materials/lots/warehouse facts are involved?
5. Is the draft ready to POST?
6. What will POST change?
7. Can it be rolled back or corrected?

Do not hide these workflow facts behind a generic Save form.

---

# 5. Production list page

Production transaction lists still use the shared list standard:

- `iv-page`
- `iv-hero`
- search + FILTER
- `CommonDataGridEx`
- compact mobile list
- confirmations

Hero eyebrow can use:

`Planning · Production`

or the live neighboring convention.

Use production-relevant compact-row facts:

- Work Order;
- product;
- operation/work centre;
- status;
- qty;
- date;
- batch/output identity.

Do not make list layout a mini execution workspace.

---

# 6. Production entry shell

Current production execution pages use:

```text
iv-page
├─ feedback
├─ iv-hero
├─ command bar
├─ workflow phase content
├─ grids/readiness/context
├─ footer/action area as established
└─ confirmations/pickers
```

Use scoped prefixes such as:

- `pwo-*`
- `pr-issue-*`
- `pr-dp-*`

Do not use `SaDocPage` for Work Order, Material Issue, Daily Production or Finished Good Receipt.

---

# 7. Command bar standard

Production command bar sits directly below the hero.

Left side generally contains navigation/context-changing actions:

```text
BACK
CHANGE OPERATION
CHANGE WORK ORDER
HOW TO USE
```

Right side contains current-state workflow actions:

```text
EDIT DRAFT
SAVE DRAFT
RELEASE
POST
ROLLBACK
DELETE
CREATE CORRECTION
...
```

Only show actions that apply to the current workflow/state.

Use semantic DevExpress styles:

- primary for normal forward action;
- success for POST/RELEASE when established;
- warning for ROLLBACK;
- danger for DELETE/CANCEL;
- secondary for navigation/tools.

Do not scatter lifecycle buttons through unrelated cards.

---

# 8. Workflow phase model

Production UI should make phases explicit in code-behind.

Examples:

```text
Selecting operation
        ->
Operation workspace
        ->
Saved NEW draft
        ->
POSTED
        ->
ROLLBACK / correction when allowed
```

or:

```text
Selecting production source
        ->
Receipt workspace
        ->
Saved NEW receipt
        ->
POSTED
        ->
REVERSED
        ->
Correction when allowed
```

Render the phase intentionally.

Do not stack a source finder permanently above an active workspace unless the current workflow explicitly requires it.

After a source/operation becomes document identity, lock it and provide a deliberate `CHANGE ...` action if changing is allowed.

---

# 9. Work Order UI

Work Order is a planning/configuration workflow.

Current concepts include:

- product;
- definition/snapshot;
- planned quantity;
- schedule;
- route/work centres/processes;
- materials;
- machines;
- labour;
- execution readiness;
- audit;
- Draft -> Release;
- Reopen for Edit when no downstream activity permits it.

Preserve the current command/actions such as:

- Save Draft;
- Calculate Preview;
- Recalculate Schedule;
- Change Definition;
- Refresh Definition;
- Release;
- Issue Materials;
- Reopen for Edit.

Do not collapse definition/snapshot complexity into one generic header/detail grid.

Release eligibility must be visible through state/readiness, not a permanently enabled button.

---

# 10. Material Issue / Issue to Production UI

Material Issue is allocation-centric.

Expected operator flow:

```text
Find/select operation
    ->
calculate/apply production basis
    ->
review BOM requirement
    ->
allocate inventory lots
    ->
save/post according to current workflow
```

Use existing BOM/allocation components.

Show:

- required/outstanding;
- issue qty;
- allocated qty;
- warehouse/location/lot;
- available stock as-of relevant date;
- allocation completeness.

The current save gate requires more than “one line exists”; allocation/readiness state matters.

Do not let a visually valid line imply it is fully allocated when it is not.

---

# 11. Daily Production UI

Daily Production is operation execution.

Current flow includes:

```text
Select eligible operation
    ->
Production workspace
    ->
enter output/scrap/reject/hold + machine/operator/material facts
    ->
save NEW
    ->
POST
    ->
ROLLBACK when eligible
```

Important:

- operation selection is a phase;
- saved historical facts remain distinguishable from live values;
- POST is a separate lifecycle gate;
- ROLLBACK is only for eligible posted documents;
- DELETE is only for eligible NEW drafts.

Do not force the operator to re-select the operation after it has become the document identity.

---

# 12. Finished Good Receipt UI

FG Receipt is source-production-lot receipt flow.

Current phases:

```text
Find eligible source lot(s)
    ->
Select
    ->
Receipt workspace locked to Work Order
    ->
save NEW
    ->
POST if readiness permits
    ->
ROLLBACK
    ->
CREATE CORRECTION when reversed and permitted
```

Use a deliberate `CHANGE WORK ORDER` path to abandon/reset incompatible source context.

Show readiness blockers before POST.

Do not hide production-source facts after selection.

---

# 13. Historical facts vs live values

Production auditability is critical.

View mode should display saved document snapshots/facts for:

- output;
- consumption;
- material standard/consume/variance;
- machine/operator;
- lot;
- posting/reversal identity/time;
- other persisted execution facts.

If a value is live/current rather than historical, label it clearly.

Example:

`Current Available`

must not look like a saved posting fact.

Do not recompute history from today's master/BOM/stock and present it as if it were the original transaction.

---

# 14. Readiness gates

Forward lifecycle buttons must be driven by explicit code-behind/service readiness.

Examples:

- Work Order Release;
- Material Issue save/post;
- Daily Production POST;
- FG Receipt POST.

When blocked, the user should understand why.

Prefer:

- concise readiness panel;
- field/line issue;
- warning list;
- disabled action with visible explanatory state;

rather than a silent disabled button.

The service remains authoritative.

---

# 15. POST and ROLLBACK

POST is not Save.

Typical semantics:

```text
Save = persist draft facts
POST = create authoritative inventory/production/costing effect
ROLLBACK = reverse eligible posted effect
```

Keep these actions visually and behaviorally distinct.

Confirmation should describe material consequences.

Do not allow ROLLBACK simply because status text says POSTED; downstream/period/costing rules may also apply.

---

# 16. Route model

Do not force one route pattern across Production.

Current families differ.

Examples:

- Work Order uses mode route.
- Daily Production supports operation-specific new route and ID edit/view.
- Material Issue supports WorkOrder-specific new route.
- Finished Good Receipt uses `/new` and `/{Id}/edit|view`.

The target page's current route model is authoritative.

A UI standardization task is not permission to normalize all routes.

---

# 17. Production grids and pickers

Use `DxGrid` for workspace/document grids.

For larger source/allocation picker tables, reuse current production components and:

`pr-popup-grid.css`

Show dense operational facts without turning every row into a card.

Use:

- stacked code/description cells;
- right-aligned quantities;
- explicit UOM;
- status/readiness context;
- bounded scrolling/paging.

Do not load an unbounded eligible-operation/source-lot set.

---

# 18. Production CSS

Entry pages commonly center the workspace around a maximum width near the current 1480px pattern.

Use existing `iv-*` for global shell/hero/cards and scoped `pr-*` for workflow-specific layout.

Do not duplicate `inventory-chrome.css`.

Daily Production and FG Receipt currently contain similar isolated `pr-dp-*` styles. Follow the existing page family now; if adding enough new pages to justify extraction, make that a separate shared-CSS refactor.

Do not silently create another near-copy with different radii/gaps.

---

# 19. View mode

Production View mode should be optimized for factual review.

Prefer:

- compact metadata grid;
- saved lines;
- posting/reversal facts;
- readiness/history context;
- Edit only if the document is an editable draft and permission allows.

Do not render every editable input disabled just to simulate View mode when the current family uses cleaner factual presentation.

---

# 20. Production rejection rules

Reject if:

- Production entry is rebuilt with `SaDocPage`;
- operation/source search remains permanently stacked over a locked workspace without workflow reason;
- a selected operation/work order can silently change and invalidate lines;
- POST is treated as Save;
- ROLLBACK ignores eligibility;
- historical values are silently recalculated from current master data;
- a live stock value is presented as saved historical fact;
- readiness blockers are hidden until the server throws one generic error;
- Material Issue ignores allocation completeness;
- FG Receipt hides source-lot/work-order context;
- route patterns are normalized merely for aesthetics;
- a new production grid fetches the whole eligible population.

---

# 21. Production Definition of Done

- [ ] Core skill passed.
- [ ] Production evidence block completed.
- [ ] Correct production workflow reference inspected.
- [ ] List follows shared list standard.
- [ ] Entry uses production hero + command bar + workflow phase pattern.
- [ ] Phase model is explicit in code-behind.
- [ ] Selected operation/source becomes clear locked context.
- [ ] Readiness/save/post/rollback gates are explicit.
- [ ] Historical vs live facts are distinguishable.
- [ ] POST is separate from Save.
- [ ] Reversal/correction/reopen follows actual service rules.
- [ ] Existing BOM/allocation/picker components are reused.
- [ ] Scoped CSS follows the current production visual metrics.
- [ ] Responsive workspace remains usable.
- [ ] No Sales/Purchase commercial-document shell was introduced.
