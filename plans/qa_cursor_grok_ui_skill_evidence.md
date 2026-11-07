# ProductionV2 QA Phase 0 — Cursor/Grok UI Skill Evidence

**Status:** design evidence only (no QA Razor pages exist)  
**Date:** 2026-11-02  
**HEAD at audit START:** `9fb40eed4f4246fbb7ca0cf038b2237134339d4e` on `fix/dataprotection-key-ring`  
**HEAD at audit END:** `717f3484846233ca05e45dd51c202f1272c47cfb` (user commit `prodcut defination enhance`; not an agent commit)  
**Browser QA UI tests:** **NOT RUN** (pages do not exist). Do not treat this file as a visual PASS.

`.agents/skills` vs `.cursor/skills`: SHA256 **identical** for every skill listed below. Duplicates were not double-read as different documents. No skill conflicts found. Precedence if later conflict: current target code → same-module live reference → shared component/CSS → module skill → core skill → this plan.

No new skills were created in Phase 0.

ERPOrigin QA study: **EXTERNAL_REFERENCE_UNAVAILABLE**.

---

## Reading log (actual local files and headings used)

Read from `.agents/skills/` (authoritative path named by the execution instruction).

| Order | Path | Headings / rules actually used |
|-------|------|--------------------------------|
| 1 | `.agents/skills/erp-ui-core/skill.md` | §1 UI STANDARD EVIDENCE block; §2 `.razor`/`.cs`/`.css`; §4 tokens `var(--surface)` etc.; §6 list `iv-page`/`iv-hero`/`iv-card`; §7 search (pairs with inline-search); §8 CommonDataGridEx GridKey `:v2`; §9–12 actions/permissions; §14 popups; §16 responsive |
| 2 | `.agents/skills/erp-transaction-ui-standard/skill.md` | §1 routing sequence; §2 UI folders (Inventory/Purchase/Planning/WorkOrders — do not invent Production/Transactions); §3 archetypes A list, B inventory, C commercial sdoc, **D production workspace**; §4 live code beats stale prose |
| 3 | `.agents/skills/production-transaction-ui/skill.md` | §1 `ErpWeb.UI/Planning/WorkOrders`; §2 DP/FG live refs; §6 hero + command bar + phases + footer; §8 select source → lock → NEW → POST → ROLLBACK; §15 POST≠Save; §16 route families |
| 4 | `.agents/skills/inventory-transaction-ui/skill.md` | §7 entry anatomy + sticky footer; §8 `/inventory/.../{Mode}/{BatchNo}`; GR family list actions NEW/POST/ROLLBACK/DELETE; cost/unit price gated |
| 5 | `.agents/skills/purchase-transaction-ui/skill.md` | Lists `iv-*`; entries **SaDocPage/sdoc-***; do not convert PO to inventory style. Used for **not** using purchase shell on QA inspection; settings analogue `PoApprovalSettings` is a master/policy page, not sdoc |
| 6 | `.agents/skills/erp-entry-footer-standard/skill.md` | **EXTRA vs GitHub v4 list.** §1 Cancel then Save (secondary then primary); View Close then Edit; §3 Save≠Post; §4 discard Keep editing + Discard; §5 adapt `mr-footer` / `pr-dp-footer` / `iv-footer` |
| 7 | `.agents/skills/erp-grid-row-actions-left/skill.md` | **EXTRA.** VisibleIndex 0 **and** FixedPosition=Left; selection beside actions; persisted layout `NormalizeLeftActionColumns`; VisibleIndex alone insufficient |
| 8 | `.agents/skills/erp-inline-search-standard/skill.md` | **EXTRA.** One row caption + input + **icon-only** magnifier; `inventory-chrome.css`; `BindValueMode.OnInput` + TextExpression; `ShowSearchBox=false` on grid; `ErpInlineSearchContractTests` |
| 9 | `.agents/skills/erp-large-data-smart-lookup-standard/skill.md` | §2 no full-table preload; §3–4 exact code + popup; §10 race version; §15 Skip/Take/TotalCount; WO/item/lot/PO/supplier |
| 10 | `.agents/skills/devexpress-dxgrid-column-readability/skill.md` | Semantic `GridColumnSize`; Width CSS units + integer MinWidth; no global wrap; stable GridKey; production qty right-aligned |
| 11 | `.agents/skills/devexpress-blazor-editor-binding-safety/skill.md` | `@bind-*` **or** Changed+matching Expression; never mix; never `ValidationEnabled=false` to hide broken binds |
| 12 | `.agents/skills/devexpress-popup-action-safety/skill.md` | `ShowFooter=true` when using FooterTemplate/FooterContentTemplate; visible Cancel/primary; CloseOnOutsideClick false for data entry; double-submit guard |
| 13 | `.agents/skills/productionv2-costing/SKILL.md` | Class A/B this phase: **zero** costing/qty/ledger/schema mutation. Mandatory stock-balance + replay-rollback stance |
| 13a | `.agents/skills/productionv2-costing/references/stock-balance.md` | Physical identity includes **status**; reservations ≠ issues; draft must not reduce on-hand |
| 13b | `references/inventory.md` | (loaded as applicable) WAC/FIFO/Standard owned by inventory ledger |
| 13c | `references/production.md` | RM/WIP/FG conservation; do not equate unlike UOMs |
| 13d | `references/replay-rollback.md` | `OpenPeriodBackdatedReplayEnabled` OFF until approved; unsupported graph → refuse root |
| 13e | `references/testing.md` | SQLite ≠ SQL Server lock proof |

**Not blanket-read for QA MVP:** AI chat, gauges, pivot, ribbon, scheduler, treelist, sales-transaction-ui (sales not in Grok QA pages). DevExpress grid/combobox/toolbar skills exist and will be read at Phase 1 **if** those components are actually instantiated; Phase 0 notes CommonDataGridEx already uses `DxToolbar` internally — **do not invent a second toolbar system**. Prefer existing CommonDataGridEx / DxButton patterns.

---

## Live reference inventory (inspected)

| Reference | Route | GridKey / footer | Notes |
|-----------|-------|------------------|-------|
| `ErpWeb.UI/Inventory/Transactions/IvMiscReceiptList.razor` | `/inventory/misc-receipt` | `inv-misc-receipt-list` | Canonical `iv-page` list + inline search + FILTER + CommonDataGridEx |
| `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor` | `/inventory/misc-receipt/{Mode}/{BatchNo}` | `mr-footer` Cancel/Close + Save/Edit | Footer skill canonical |
| `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor` | `/inventory/goods-receipts/{Mode}/{BatchNo}` | inventory family | Preserve; additive QA later |
| `ErpWeb.UI/Inventory/Transactions/IvGoodsReceiptList.razor` | `/inventory/goods-receipts` | `inv-goods-receipt-list` | POST/ROLLBACK on list |
| `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionList.razor` | `/planning/daily-production` | `pln-daily-production-list` | Production list shell |
| `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor` | `/planning/daily-production/{Mode}` + `/new/{WorkOrderOperationId}` | `pr-dp-footer` CANCEL + SAVE DRAFT | Command bar POST separate |
| `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptList.razor` | `/planning/finished-good-receipts` | `pln-fg-receipts` | |
| `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor` | `/new`, `/{Id}/edit`, `/{Id}/view` | `pr-dp-footer` | Source lock + readiness |
| `ErpWeb.UI/Purchase/Masters/PoApprovalSettings.razor` | `/purchase/approval/settings` | `iv-footer` | Policy editor ACCESS/EDIT |
| `ErpWeb.UI/Admin/AdminSettings.razor` | `/admin/settings` | popup row save | QA **tab already exists** via `AppSettingModules.Qa` |
| `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor` | shared | Command column `VisibleIndex=0` `FixedPosition=Left` (lines 129–134); selection index 1 when actions exist (121–122) | Layout `{GridKey}:v2` |
| `GridColumnSizing.cs` | shared | `GridColumnSize.DocumentNo/Reference/Code/Name/Quantity/Status/LongText` | |

Shared CSS: `ErpWeb/wwwroot/css/site.css`, `inventory-chrome.css`, `pr-popup-grid.css`. Page-scoped `Qa*.razor.css` later; tokens `var(--surface)`, `var(--text)`, `var(--muted)`, `var(--accent)`, `var(--line)`.

Menus today: no Quality nodes. Pattern `scripts/init-po-approval-menu.sql` + `init-planning-daily-production-menu.sql` (menu + permissions; running numbers for production docs).

---

## Per-page UI STANDARD EVIDENCE

### Current git (for this evidence file)

START HEAD `9fb40eed`; END HEAD `717f348` (user landed previously dirty Product Definition / PO-approval files). Dirty-file inventory: `plans/qa_phase0_source_posting_contract.md` §0. This agent modified only the two `plans/qa_*.md` evidence files.

---

### Page: QA Workbench (proposed `/quality`)

| Field | Value |
|-------|--------|
| **Page/feature** | QaWorkbench list — **not implemented** |
| **Skills read** | erp-ui-core §6–9; erp-transaction-ui-standard archetype **A**; erp-inline-search-standard; erp-grid-row-actions-left; dxgrid-column-readability; production-transaction-ui list §5 |
| **Module classification** | QA Workbench **cross-module list** (not a commercial document) |
| **Closest live references** | **Primary:** `PrDailyProductionList.razor[.cs/.css]`. **List mechanics:** `IvMiscReceiptList.razor[.cs/.css]` |
| **Shared component / grid / key** | `CommonDataGridEx`; proposed GridKey `qa-workbench-list` (`:v2` persistence) |
| **Status / permission source** | Future `MenuCodes` e.g. QA_WORKBENCH; ACCESS for list; row Inspect/View gated by case status + ACCESS/EDIT; **no POST** |
| **Route + New/Edit/View** | List only `/quality`. NEW INSPECTION → Inspection entry |
| **Action toolbar / leftmost row action** | Toolbar: FILTER, NEW INSPECTION, How to use. Row: Inspect/View **left-fixed** via CommonDataGridEx (do not rewrite grid internals to reorder checkbox) |
| **Save/Cancel vs Submit/Approve vs posting** | None on list |
| **Popups / footer** | FILTER popup: `ShowFooter=true` if FooterTemplate; or in-content `iv-popup-footer`. Discard N/A |
| **Editor bindings** | N/A (search TextExpression per inline-search) |
| **Large lookup** | List search is inline-search, **not** SmartLookup. Optional FILTER fields that pick WO/item/supplier **must** use server-paged lookup, not unbounded ComboBox Data |
| **Widths / responsive / dark** | Semantic GridColumnSize; compact mobile rows: QA No + Source + Item + Qty + Status + first-tap Inspect. Tokens only. Visual **NOT RUN** |
| **A11y / keyboard / help** | How to use popup; empty/loading/error states; 350–550 ms debounce like live family |
| **Intentional differences** | KPI chips Needs inspection / Awaiting review / Approved from **server-scoped queries**. Label “On Hold” only as **QA review pending (record only)** in Phases 0–2B — **not** warehouse hold. No prices without VIEW_COST. No charts unless later justified (charts skill not required) |
| **Proof / NOT RUN** | Code refs VERIFIED. Browser 1920/1440/1280/1024/390, light/dark, 125% zoom: **NOT RUN** |

---

### Page: QA Inspection entry (proposed `/quality/cases/new`, `/quality/cases/{id}`)

| Field | Value |
|-------|--------|
| **Page/feature** | QaInspectionEntry — **not implemented** |
| **Skills read** | production-transaction-ui §6–8, 15–16; erp-entry-footer-standard; large-data-smart-lookup; editor-binding-safety; popup-action-safety; dxgrid-column-readability for split grid |
| **Module classification** | **Production-style workflow** (cross-module case). **Not** Purchase `sdoc-*`. **Not** inventory batch clone |
| **Closest live references** | **Primary:** `PrDailyProductionEntry.razor[.cs/.css]`. **Secondary:** `PrFinishedGoodReceiptEntry.razor[.cs/.css]` (source lock, readiness). Footer semantics also `IvMiscReceipt` |
| **Shared component / grid / key** | Direct `DxGrid` for split decisions (not CommonDataGridEx). Leftmost `DxGridCommandColumn` VisibleIndex=0 FixedPosition=Left. Width CSS + MinWidth int |
| **Status / permission** | Same QA menu: ADD/EDIT draft; SUBMIT; APPROVE; no POST. VIEW_COST if any cost evidence shown (default hide). Source POST remains on GRN/FG |
| **Route + New/Edit/View** | Recommend FG-style `/quality/cases/new?source=...` and `/quality/cases/{id}` with view/edit by status — document in Phase 1 evidence. Source prefilled; CHANGE SOURCE only before saved identity |
| **Actions** | Command bar: BACK, HOW TO USE, CHANGE SOURCE (draft), SUBMIT, APPROVE, REJECT FOR CORRECTION. **No generic POST.** Open GRN/FG/VR later phases as navigation only |
| **Save/Cancel vs Submit/Approve** | Sticky footer: Cancel (secondary) then Save Draft (primary) using `pr-dp-footer` (adapt, do not copy DP handlers). Submit/Approve in command bar; must save/validate via **one service contract**, not two unguarded commits. IsSubmitting / double-click suppress |
| **Popups** | Confirmations with business impact (“records QA only; stock has not moved”). ShowFooter=true if DX footer templates. Visible Cancel |
| **Editor bindings** | `@bind-*` or Text/Value/DateExpression on every DxTextBox/SpinEdit/DateEdit/ComboBox |
| **Large lookup** | Source picker: type exact code + server resolve + paged popup; version token vs stale search. Never preload all PO/GRN/WO/item/lot |
| **Widths / responsive** | Short qty/date; wide remarks. Qty bar Inspected/Accepted/Rejected/Unassigned. Record-only banner mandatory |
| **A11y / help** | Info popup why controls disabled; Tab order Qty then Result |
| **Intentional differences** | Contextual fields by source (WO vs PO vs lot). User labels never show SourceFingerprint/DocumentRevision/PostedGeneration. Phase 1 copy: “Recording QA only. Stock and WIP have not moved.” Incoming reject is not VR |
| **Proof / NOT RUN** | Visual matrix **NOT RUN**. Bindings/popups untested until Phase 1 |

---

### Page: QA Settings (proposed `/quality/settings`)

| Field | Value |
|-------|--------|
| **Page/feature** | QaPolicyEntry — **not implemented** |
| **Skills read** | erp-ui-core; erp-entry-footer-standard; popup-action-safety; purchase skill only as **do not use sdoc** |
| **Module classification** | Admin-style policy form |
| **Closest live references** | **Primary:** `PoApprovalSettings.razor[.cs/.css]` (`iv-footer` Cancel→Save, ACCESS view / EDIT mutate). **Secondary:** `AdminSettings.razor` QA tab (placeholder flag only) |
| **Shared component** | DxFormLayout / toggles / comboboxes; no transaction grid required |
| **Permission** | New `QA_SETTINGS` ACCESS + EDIT. Do not grant APPROVE by default to all ACCESS |
| **Route** | `/quality/settings`. AdminSettings QA tab should remain placeholder or later redirect — **do not enable** `QaRequireInspectionOnReceipt` |
| **Actions** | Save policy; Cancel dirty check. No POST/ROLLBACK |
| **Save/Cancel** | `iv-footer` secondary Cancel, primary Save |
| **Popups** | HOW TO SET UP QA in plain English: Receive delivery → Inspect → Save → Submit → Approve → Create GRN from accepted qty → Post GRN. Rejected before GRN is **not** inventory. Do not mention “replay hook” to operators |
| **Lookups** | Item override lookup must be smart-paged if present |
| **Intentional differences** | Nontechnical labels (“Check supplier deliveries before stock receipt?”). Unsupported sources: **Not available yet** + tooltip — checkbox not enabled without posting guard. Default **OFF**. Feature flags admin-only |
| **Proof / NOT RUN** | **NOT RUN** |

---

### Existing GRN / Daily Production / FG (later additive links only)

| Field | Value |
|-------|--------|
| **Page/feature** | Additive Request QA / status indicator — **not in Phase 0** |
| **Skills** | inventory-transaction-ui (GRN); production-transaction-ui (DP/FG); purchase-transaction-ui **if** PO screen touched |
| **Classification** | Existing GRN Inventory / existing FG Production / existing PO Purchase |
| **References** | `IvGoodsReceipt.razor[.cs/.css]`; `PrDailyProductionEntry`; `PrFinishedGoodReceiptEntry` |
| **Rule** | Preserve shells, command bars, Save≠Post, item/value fields. One unobtrusive QA action. WI non-good: banner record-only |
| **Proof** | No UI change this phase |

---

## Proposed skill routing (Phase 1, not started)

```
erp-ui-core → erp-transaction-ui-standard
  Workbench: inline-search + grid-row-actions-left + production list refs
  Inspection: production-transaction-ui + entry-footer + smart-lookup + editor-binding + popup-safety
  Settings: admin/policy (PoApprovalSettings), not purchase sdoc
  GRN links: inventory-transaction-ui
  PO links: purchase-transaction-ui (only if PO markup changes)
```

Folder: `ErpWeb.UI/Quality/` as specified in the v4 product plan (cross-module). This does **not** create `ErpWeb.UI/Production/Transactions` (forbidden by production-transaction-ui §1).

---

## UI gate (Gate 0 vs later)

| Gate | Required | This phase |
|------|----------|------------|
| Gate 0 | Skill log + archetype/reference map | **This file** |
| Gate 1 | Workbench CommonDataGridEx, inspection production shell, settings access, left actions, footer, lookups, bindings, popups, no stock movement | **CODED** — see Phase 1 block below. Browser visual matrix still **NOT RUN**. |
| Gate 2A | From GRN Request QA prefill + return; incoming totals; settings incoming toggle default OFF; friendly POST-block copy | **CODED**. Browser visual matrix still **NOT RUN**. |
| Gate 2B | Settings FinalFg toggle default OFF; DP/FG Request QA; FG totals; FG POST-block copy; PADJ FG_STAGING hidden/blocked when FinalFg ON | **CODED**. Browser visual matrix still **NOT RUN**. |
| Gate 2C | Settings Logical WIP hold toggle default OFF; case QA reserved chip; Release hold; never “moved to QA warehouse” | **CODED**. Browser visual matrix still **NOT RUN**. |
| Gate 3 | Case Open buttons (permission-checked); inquiry source→disposition→destination, aging, FPY with explicit denominator; destination NEW banners; never auto-post | **CODED**. Browser visual matrix still **NOT RUN**. |
| Gate 4 | Settings Physical warehouse hold toggle default OFF; case Physical reserved chip; Release hold; never “converted to QCHOLD” / never transfer-as-release | **CODED**. Browser visual matrix still **NOT RUN**. |
| Gate 5 | Settings Non-good Daily Production toggle default OFF; Daily Production Request QA (non-good); destinations NOT_STOCK / EXECUTED from sealed allocation; never “valued lot” | **CODED**. Browser visual matrix still **NOT RUN**. |
| Closeout | Posted GR Request QA uses POSTED_GRN; posted MR Request QA uses INVENTORY_STOCK | **CODED**. Browser visual matrix still **NOT RUN**. |
| Visual matrix | Desktop/tablet/phone, light/dark, 125%, empty/long names | **NOT RUN** |

A later clean backend test run cannot substitute for this UI gate.

---

## Costing skill attestation (UI-adjacent)

Phase 0 produced **only** Markdown under `plans/`. No inventory/WIP/ledger/schema/UI code. Task class **A** relative to costing skill. Stock-balance.md: QA record-only must not be described as available-to-promise reduction.

---

## Phase 1 UI STANDARD EVIDENCE (implemented)

```text
UI STANDARD EVIDENCE
Target module: Quality (new top-level, not Inventory/Production/Purchase sdoc)
Target page(s):
  /quality/cases            QaWorkbench     list
  /quality/cases/{mode}     QaCaseEntry     entry/workflow
  /quality/settings         QaSettings      policy
Page archetype:
  Workbench = inventory/production list (iv-page + CommonDataGridEx + erp-inline-search)
  Case entry = Misc Receipt sticky footer (Cancel/Save; Close/Edit; workflow extras)
  Settings = PoApprovalSettings-style policy, posting gates disabled
Closest live reference:
  Workbench: ErpWeb.UI/Inventory/Transactions/IvMiscReceiptList.razor
             ErpWeb.UI/Planning/WorkOrders/PrDailyProductionList.razor
  Entry:     ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor
  Settings:  ErpWeb.UI/Purchase/Masters/PoApprovalSettings.razor
Shared component(s): CommonDataGridEx, MenuAuthorize, PermissionAuthorize, AuditColumns
Shared/global CSS: inventory-chrome.css (iv-*), page-scoped mr-* copies on entry/settings
Route/mode model: list + new/edit/view (same regex as Misc Receipt)
List actions: NEW, DELETE, row VIEW/EDIT (left command column via CommonDataGridEx)
Entry/workflow actions: Save/Cancel; Submit; Start inspection; Submit for review; Approve/Reject; Close case
Editable statuses: DRAFT only
Permission codes: QA_WORKBENCH ACCESS/ADD/EDIT/DELETE/SUBMIT/APPROVE/REJECT/EXPORT; QA_SETTINGS ACCESS/EDIT; no POST
Intentional deviations:
  - Attachments disabled with on-page banner (no QA storage adapter).
  - Incoming delivery note is non-authoritative text (Phase 0 contract).
  - Posting-gate checkboxes are visible but disabled/off.
```

Costing class **B** (workflow, no posting mutation). Public GR/FG/WIP POST routes were not edited. `QaRequireInspectionOnReceipt` remains default false and unused for posting.
