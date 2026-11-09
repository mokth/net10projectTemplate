---
name: erp-entry-footer-standard
description: >-
  Productionv2 ERP entry-page Save/Cancel/Close/Edit action placement and color standard.
  Use whenever creating, modifying, reviewing, or standardizing an Inventory,
  Sales, Purchase/Procurement, or Production transaction entry, header-detail form,
  document screen, workflow workspace, sticky footer, popup action bar, button
  colors, Save/Cancel/Close/Edit actions, or unsaved-changes interaction. Use the
  current Miscellaneous Receipt entry as the canonical UX reference for action
  order, alignment, semantics, DevExpress button styles and theme-safe colors;
  preserve each module's existing workflow, permissions, validation, posting and
  rollback behavior.
---

# ERP Entry Footer & Button Color Standard — Misc Receipt Reference

## 1. The intended ERP experience

Users should not need to hunt for **Save**, **Cancel**, **Close** or **Edit** on different transaction entry pages. A regular entry page has a clear bottom action area, consistent button order, consistent color meanings, and predictable action behavior.

Reference screen: `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor`, route `/inventory/misc-receipt/...` (inspect actual routes on the current branch).

Expected, **editable New/Edit page**:

```text
┌──────────────────────────────────────────────────────────────────────────┐
│ ... entry form and transaction lines ...                                 │
├──────────────────────────────────────────────────────────────────────────┤
│ Useful line/total/status hint                    [ Cancel ]  [ Save ]   │
│                                                secondary      PRIMARY   │
└──────────────────────────────────────────────────────────────────────────┘
```

Expected, **read-only View page**:

```text
│ Useful line/total/status hint                     [ Close ]  [ Edit ]    │
│                                                secondary    PRIMARY*   │
```

`*` Show Edit **only** when the actual user permission and document status allow it. It is not a promise that every posted/closed document can be edited.

Principles:

- Keep footer visible/usefully reachable while the entry page scrolls, following the verified Misc Receipt sticky-footer pattern.
- Footer actions live on the **right** in desktop layout; any hint/summary sits to the **left**.
- The **secondary exit action first**, the **primary action last**.
- The primary Save is visually distinct. Cancel is neutral; do **not** render Cancel in the destructive/danger color merely because its English label sounds destructive.
- The rule is the *interaction language*, not a command to replace Sales/Purchase document shells or Production workspaces with Inventory markup.

## 2. Verify current source of truth — before changing any page

1. Inspect the **CURRENT** `mokth/net10projectTemplate` branch requested by the user (normally `productionv2`). Read the target entry `.razor`, `.razor.cs`, `.razor.css` and shared wrappers/styles. Never implement from this historical description alone.
2. Read `.agents/skills/erp-transaction-ui-standard/skill.md` and `.agents/skills/erp-ui-core/skill.md`.
3. Read the applicable module skill: `inventory-transaction-ui`, `sales-transaction-ui`, `purchase-transaction-ui`, or `production-transaction-ui`. These module skills own lifecycle and entry archetype.
4. Inspect **live** canonical Misc Receipt references:
   - `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor`
   - `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor.cs`
   - `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor.css`
5. Inspect shared color/CSS ownership:
   - `ErpWeb/wwwroot/css/site.css` (light/dark semantic variables)
   - `ErpWeb/wwwroot/css/inventory-chrome.css` (shared `iv-*` layout)
   - existing `sdoc-*` commercial-document CSS, where applicable
6. Compare the closest **same-module** working entry. Preserve its workflow-specific actions and its established footer/container structure.
7. If the task includes list-grid row actions, also read `.agents/skills/erp-grid-row-actions-left/skill.md`. List-grid row actions are **left-fixed columns**; page-level entry Save/Cancel belong in the **bottom-right footer**. Do not mix them up.

### Verified references on productionv2 (2026-10-30, for context only)

Misc Receipt entry currently renders:

- New/Edit: `Cancel` (`ButtonRenderStyle.Secondary`, x-mark icon), then `Save` (`ButtonRenderStyle.Primary`, floppy-disk icon).
- View: `Close` (`Secondary`), then permission/status-gated `Edit` (`Primary`).
- Save label becomes `Saving…` during `IsSubmitting`, and the button uses `CanSave`.
- A line-count/amount hint is left of the actions.
- Footer CSS: `.mr-footer` is sticky; `.mr-footer__actions` is a flex group aligned to the right; responsive break at `860px` stacks footer sections.
- `OnCancelAsync` checks edit/dirty conditions and opens a discard confirmation; confirmed discard uses a **Danger** button. `OnSaveAsync` checks state and submission guard.
- `IvStockTransfer.razor` and `IvMiscIssue.razor` already use the same footer behavior.
- `PoInvoice.razor` uses a commercial `sdoc-footer` with the same secondary Cancel / primary Save colors, **plus** separately gated Post and Rollback actions.
- `PrMaterialIssueEntry.razor` uses a Production workspace footer with `CANCEL` (`Secondary`) and `SAVE DRAFT` (`Primary`). This is the same action semantics with a domain-specific label.

The current repository may evolve. **If actual code disagrees, inspect it and document the difference.**

## 3. Mandatory action wording, order, colors and icons

| Meaning | Label examples | DevExpress style | Typical icon | Important distinction |
|---|---|---|---|---|
| Persist new/edited draft | Save / Save Draft | `ButtonRenderStyle.Primary` | `fa-solid fa-floppy-disk` | Never silently post or change inventory/financial balances. |
| Stop editing / return without saving | Cancel | `ButtonRenderStyle.Secondary` | `fa-solid fa-xmark` | Neutral, **not Danger**; protect unsaved changes. |
| Leave read-only display | Close | `ButtonRenderStyle.Secondary` | `fa-solid fa-xmark` | Close/view navigation only, not document lifecycle Close. |
| Enter permitted edit mode | Edit / Edit Draft | `ButtonRenderStyle.Primary` | `fa-solid fa-pen` | Only if permission + status + business eligibility allow. |
| Open/add/edit line inside popup | Add / Update / Apply | `ButtonRenderStyle.Primary` | matching existing icon | Cancel/Close remains Secondary. |
| Post/accounting or inventory commit | Post | `ButtonRenderStyle.Success` where the current module uses it | existing module icon | Separate server workflow from Save; do not auto-post. |
| Reverse an eligible posting | Rollback | `ButtonRenderStyle.Warning` where current module uses it | existing module icon | Must remain gated and confirmed. |
| Delete, discard or irreversible document cancellation | Delete / Discard / Cancel Document | `ButtonRenderStyle.Danger` | `fa-solid fa-trash` or established icon | **Document cancellation** is not the same as **Cancel editing**. |
| Non-destructive assistance/navigation | Back / Lookup / Refresh / Copy | `ButtonRenderStyle.Secondary` | existing icon | Do not make all secondary utilities primary. |

**Colors must be consistent by meaning across pages and modes.** Use DevExpress `DxButton.RenderStyle`, the application's configured DevExpress Fluent theme, and existing semantic CSS variables. Do **not** independently hardcode blue/gray/red per module, recolor every icon, or override all buttons with a global `.dxbl-btn` CSS rule. Note that DevExpress `Primary` is theme-defined; do not assume it equals a hardcoded hex value.

`site.css` defines light/dark tokens such as `--bg`, `--surface`, `--text`, `--muted`, `--line`, `--accent`, `--navy`, `--danger`. Use these for footer/background, hint text, borders, emphasis, and alerts. Prefer inherited `sdoc-*` styling in Sales/Purchase and module-scoped CSS only for genuine differences. Any new colors must be semantic/theme-aware, not a fifth module palette.

**Do not globally recolor existing status chips** as a byproduct of this footer standard: status badges carry independent domain semantics. Apply the same semantic style principles to any *newly touched* alerts/status indicators without erasing legitimate status distinctions.

## 4. Entry mode and safety contract

| Mode / condition | Footer must show | Required behavior |
|---|---|---|
| New, editable | `Cancel` → `Save` | `Cancel` exits safely; `Save` persists the draft when valid. |
| Edit, editable | `Cancel` → `Save` | Dirty/unsaved changes require the established discard confirmation. |
| View/read-only | `Close` → optional `Edit` | Never display Save as if editable; edit capability requires permissions/status. |
| Saving | Disabled Cancel and Save (or the page's equivalent safe busy state); Save reads `Saving…` | No duplicate submission, no route loss, no accidental discard while saving. |
| Posted / closed / historical view | `Close` (plus permitted dedicated workflow actions) | No ordinary Edit unless the original domain rules allow it. |
| Validation error | Remain on entry with visible actionable feedback | Never save, post or silently navigate away on failure. |

Existing service/API/domain rules are authoritative. This skill may standardize **visual appearance and presentation gating** but must not relax validation, permission, status, month-end, future-date, stock/WIP, costing or rollback checks. Keep the existing route model and transaction behavior.

### Cancel vs business cancellation: do not conflate

- **Cancel (editing)**: user leaves unsaved form; neutral Secondary button, dirty check and optional discard confirmation.
- **Cancel Document**: actual business transaction transition; destructive/danger semantics only where appropriate, with current permission/state eligibility and confirmation. Never wire the footer's Cancel to the document-cancellation service by accident.
- **Close (view)**: exit the read-only page. Separate from **Close Document**, a business lifecycle transition on certain PO/SO documents.

### Unsaved changes

- Follow the target family's established dirty guard, including edits to header **and line** values.
- If the page already uses a discard confirmation, keep it. Standard action order is `Keep editing` (`Secondary`) then `Discard` (`Danger`).
- Do not bypass the guard by replacing the Cancel handler with a raw navigation call.
- Do not invent a new global navigation interception architecture to achieve button styling. If the current page has missing unsaved protection, report it or implement the smallest **UI-only** correction within scope and test it explicitly.

## 5. Exact editable/view markup reference (adapt to the live page)

The following is a **visual wiring example**, NOT a replacement for the page's real variables, menu permission guards, validation logic or handlers:

```razor
<footer class="mr-footer">
    <p class="mr-footer__hint">@LineCountLabel · Total @DocumentTotal.ToString("n2")</p>
    <div class="mr-footer__actions">
        @if (IsViewMode)
        {
            <DxButton RenderStyle="ButtonRenderStyle.Secondary"
                      Text="Close"
                      IconCssClass="fa-solid fa-xmark"
                      Click="@OnCloseAsync" />
            @if (CanEditFromView)
            {
                <PermissionAuthorize MenuCode="@MenuCodes.InventoryMiscReceipt"
                                     Permission="@PermissionCodes.Edit">
                    <DxButton RenderStyle="ButtonRenderStyle.Primary"
                              Text="Edit"
                              IconCssClass="fa-solid fa-pen"
                              Click="@OnEditFromView" />
                </PermissionAuthorize>
            }
        }
        else
        {
            <DxButton RenderStyle="ButtonRenderStyle.Secondary"
                      Text="Cancel"
                      IconCssClass="fa-solid fa-xmark"
                      Click="@OnCancelAsync"
                      Enabled="@(!IsSubmitting)" />
            <PermissionAuthorize MenuCode="@MenuCodes.InventoryMiscReceipt"
                                 Permission="@(IsNewMode ? PermissionCodes.Add : PermissionCodes.Edit)">
                <DxButton RenderStyle="ButtonRenderStyle.Primary"
                          Text="@(IsSubmitting ? "Saving…" : "Save")"
                          IconCssClass="fa-solid fa-floppy-disk"
                          Click="@OnSaveAsync"
                          Enabled="@CanSave" />
            </PermissionAuthorize>
        }
    </div>
</footer>
```

**Do not copy the `mr-*` CSS class, `MenuCodes.InventoryMiscReceipt`, route, `LineCountLabel`, `CanSave` predicate or handlers to an unrelated document.** Reuse existing `tr-*`, `mi-*`, `sdoc-*`, or `pr-*` footer containers and the target's *own* permission and mode wiring. Do not rename classes to satisfy this skill.

## 6. Layout and responsiveness

Desktop:

- Footer should be the entry's predictable bottom action location and remain reachable (sticky where the existing family supports it).
- Put hint/summary on the left, action group to the right, and `Cancel` before `Save`.
- Consistent button height, padding, icon alignment, spacing (current Misc Receipt uses `gap: 8px`), and stable width that fits full labels.
- No extra Save button in a randomly placed toolbar/card unless there is an established workflow reason.
- Avoid competing sticky bars (top commandbar, bottom footer, popup footer). Keep overlay/z-index behavior and scroll ownership intact.
- Footer must not cover last data row, editor, validation summary, or popup; test with long forms and browser zoom.

Narrow/mobile:

- Match the live Misc Receipt responsive behavior concept: hint and action group may stack; **both actions remain visible/tappable** without horizontal scrolling.
- Keep logical order when wrapping: secondary exit then primary action; save remains obvious.
- Ensure scroll-to-error and focused editors are not hidden by sticky footer/soft keyboard; test at 320–375px widths and 200% zoom.
- Never force desktop dimensions that clip or hide Save/Cancel.

Accessibility:

- Text labels are required for primary entry actions; icons alone are not sufficient.
- Preserve accessible names and keyboard navigation. Disabled controls must communicate why through nearby validation/hints where appropriate.
- Do not rely on color alone for post/rollback/error meaning; retain readable labels.

## 7. Popups and workflow-specific actions

For an entry-line popup, prefer current Misc Receipt alignment and order:

```text
[Cancel (Secondary)] [Add/Update (Primary)]
```

For a discard confirmation:

```text
[Keep editing (Secondary)] [Discard (Danger)]
```

For Sales/Purchase documents keep commercial `SaDocPage` / `sdoc-*` structure and their current business actions. The live Purchase Invoice may show `Post`/`Rollback` in addition to Save/Cancel. Do not remove, merge or reassign these transitions to satisfy the two-button reference. Preserve validated ordering and state handling from the closest document.

For Production, keep work-order/source-selection phases, readiness, allocation, `SAVE DRAFT`, Release, Post and Rollback. The footer colors/visual hierarchy can be harmonized, but do not turn a multi-stage production workspace into a generic Misc Receipt form.

For modal primary actions, use `Primary` for Add/Update/Apply and `Secondary` for Cancel. Do not replace a domain-specific `Warning`/`Danger` confirmation with `Primary` just for visual uniformity.

## 8. Scope and hard exclusions

This skill is **UI STANDARDIZATION FIRST**. The smallest safe implementation should usually edit only `.razor` and page-scoped `.razor.css`, with `.razor.cs` edits only for already-needed display/dirty/busy guards.

MUST NOT change:

- inventory quantities, costing/FIFO/CostLayer, WIP, GL, tax or currency calculations;
- posting, rollback/reversal, delete/close/reopen service logic;
- database entities, schemas, migrations, SQL, APIs, background jobs or transaction boundaries;
- document statuses, month-end/closed-period, future-date/future-stock or concurrency rules;
- actual permissions, audit trails, or source-document import/matching behavior;
- canonical Sales/Purchase/Production workflows merely to imitate Inventory markup;
- already working list pagination, filtering, selection or saved grid layouts.

Never add a duplicate `Save`/`Cancel` component or change button handlers simply for cosmetic consistency. If shared footer styling already solves the problem, reuse it; do not copy a block of CSS into 40 pages.

## 9. Required tests / acceptance gates

- [ ] Misc Receipt New, Edit and View are used as the **visual reference**; behavior is verified, not assumed.
- [ ] Every changed entry's footer has right-aligned `Cancel` (Secondary) → `Save` (Primary) in editable mode.
- [ ] View has `Close` (Secondary) → `Edit` (Primary) only when Edit is genuinely eligible.
- [ ] Footer remains reachable without scrolling to an unrelated control and does not cover the last line/editor.
- [ ] Same semantic actions use the same style/color/icon meaning across target pages, including light/dark themes.
- [ ] The app's shared CSS tokens are used; no new hardcoded page palette or global DevExpress button overrides.
- [ ] Busy/disabled state prevents accidental double-save and cancel during active save.
- [ ] Dirty Cancel shows correct discard confirmation and does not unexpectedly discard edits.
- [ ] Save performs **Save only**, not Post, when those are separate operations.
- [ ] Existing Post/Rollback/Release/Close Document/Cancel Document remain present, correctly gated and distinct.
- [ ] Valid/invalid New, Edit, View, Posted/Closed, historical, permission-denied states tested.
- [ ] Desktop/tablet/phone, horizontal/vertical scroll, keyboard, 200% zoom, and dark mode reviewed.
- [ ] No changes to quantity, costing, WIP, accounting, database schema, transaction or security logic.

## 10. Mandatory evidence/reporting

```text
ERP ENTRY ACTION UX EVIDENCE
Branch + commit inspected:
Target entry page + module:
Actual footer CSS/container:
Closest same-module reference:
Misc Receipt reference checked:
Editable New/Edit buttons (label, order, style, icon, handler):
Read-only View buttons and edit eligibility:
Busy/disabled/validation guards:
Unsaved-change/discard flow:
Additional lifecycle actions preserved:
Theme tokens and CSS changes:
Desktop, narrow-screen, dark-mode checks:
Permission/status checks unchanged:
Files changed:
Out-of-scope issues and exceptions:
```

**Final acceptance:** A normal ERP user sees predictable, well-aligned, theme-consistent Save/Cancel or Close/Edit actions at the bottom of every applicable entry page. Nothing about the visual standard can bypass or subtly change financial, inventory or production business rules.
