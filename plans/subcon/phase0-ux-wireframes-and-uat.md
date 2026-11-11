# Subcon Phase 0 — UX wireframes and UAT

**HEAD:** `1b2d70618ce44d5db0f13ea14d78697dbc27ea5f`  
**Nature:** paper + live-component mapping. No Razor pages added this phase.  
**Posting:** screens must never imply a successful Post while adapters are BLOCKED.

---

## UI STANDARD EVIDENCE

```text
UI STANDARD EVIDENCE
Target module: Subcontracting (single module, two source modes)
Target page(s): Workbench, Send Out, Receive Back, Inquiry/Statement, Settings
Page archetype: Inventory-style list + compact entry (NOT WO command-bar workspace)
Closest live reference: IvMiscReceipt (entry/footer) + IvMiscReceiptList (Post on list)
  + IvStockTransfer (from/to qty) + QaWorkbench / PoProcurementWorkbench (KPI home)
Shared component(s): CommonDataGridEx, PoSupplierPicker, IvBalLocPicker,
  IvStockMasterPicker, MenuAuthorize, PermissionAuthorize, ErpOperatorFailureBanner
Shared/global CSS: ErpWeb/wwwroot/css/inventory-chrome.css (iv-page, iv-hero, iv-card,
  iv-toolbar-row, erp-inline-search, iv-list-desktop / iv-list-compact)
Route/mode model: /subcon (workbench), /subcon/send-out, /subcon/receive-back,
  /subcon/inquiry, /subcon/settings — proposed only
List actions: View (left-fixed), Receive (if outstanding), Print; toolbar NEW / filter
Entry/workflow actions: Cancel + Save on entry; Post/Rollback on list IF a posting
  owner exists — currently Post must be disabled with reason
Editable statuses: NEW drafts only
Permission codes: proposed SUBCON_* + Access/Add/Edit/Delete/Post/Rollback/Print/Export/ViewCost
Intentional deviations: (1) Two dominant workbench CTAs (Send Out / Receive Back)
  instead of a single NEW document. (2) Inventory-only tenants hide WO without
  requiring Planning permission. (3) Post hidden/disabled while Phase 2I/2P BLOCKED.
```

```text
INVENTORY UI STANDARD EVIDENCE (Send Out / Receive Back)
Closest live reference: IvStockTransfer.razor + IvMiscReceipt.razor
SizeMode: Small on DxFormLayout
Command column: VisibleIndex=0, FixedPosition=Left (erp-grid-row-actions-left)
Footer: erp-entry-footer-standard — Cancel Secondary, Save Primary; no Post on entry
Source picker: IvBalLocPicker for inventory mode
Vendor: PoSupplierPicker (not the Misc Receipt DxComboBox vendor)
```

```text
PRODUCTION UI STANDARD EVIDENCE (Production mode fields only)
Target workflow: WIP source pick (disabled until 2P)
List/entry reference: PrMaterialIssueEntry (operation context) + PrWorkOrderEntry
  operations grid (read-only identity)
Workflow phase: do not copy PWO command bar (Release / Issue Materials) onto Subcon
Source selector: WO + operation + eligible ProductionBalLot
Locked context after selection: mode immutable after first posted reference
Statuses: drafts NEW; job status DERIVED
Deviation: compact misc-receipt hero, not full WO workspace
```

Skills: `.agents/skills/erp-ui-core/skill.md`, `inventory-transaction-ui/skill.md`, `production-transaction-ui/skill.md`, `erp-entry-footer-standard`, `erp-grid-row-actions-left`, `erp-large-data-smart-lookup-standard`.

---

## Navigation (proposed; not implemented)

Top-level `SUBCONTRACTING` in `ErpWeb/Menus/menus.xml` between Purchase (4) and Planning (5), bumping Planning→6, Quality→7, Security→8 — **or** SortOrder 5 with later items +1. Reason: Inventory-only companies must see Subcon **without** a Planning grant. Quality’s flat children are the pattern:

| Code (proposed) | Name | Route |
|---|---|---|
| `SUBCONTRACTING` | Subcontracting | parent |
| `SUBCON_WORKBENCH` | Workbench | `/subcon` |
| `SUBCON_SEND_OUT` | Send Out | `/subcon/send-out` |
| `SUBCON_RECEIVE_BACK` | Receive Back | `/subcon/receive-back` |
| `SUBCON_INQUIRY` | Inquiry / Statement | `/subcon/inquiry` |
| `SUBCON_SETTINGS` | Settings (admin) | `/subcon/settings` |

No compulsory “New Subcon Job” menu. Job identity is created behind Send Out if needed.

---

## Wireframe 1 — Workbench (1366 desktop)

```text
SUBCONTRACTING                                              [How to use ?]
[Open lines: 18]  [Outstanding lines: 12]  [Overdue: 6]
  Do NOT show "2,450 PCS" as a mixed-UOM sum.
  Optional per-UOM chips: 200 PCS | 15 KG   (never add them)

[ Send Out ]     [ Receive Back ]

Search supplier / item / WO / document ...     [Filter]
-------------------------------------------------------------------
Actions | Date       | Supplier      | Item / WO        | Sent | Returned | Balance | Due        | Status
[View]  | 11/11/2026 | ABC Painting  | METAL-A          | 500  | 300      | 200     | 18/11/2026 | Open
[View]  | ...        | ...           | WO-100 / Plate   | ...  | ...      | ...     | ...        | Awaiting inspection
-------------------------------------------------------------------
Footer: [Supplier statement] [Print] [Export if permitted]
Empty: "No outstanding items for ABC Painting" + button Send Out
```

KPI cards copy `PoProcurementWorkbench` / `QaWorkbench` (`iv-hero` chips). Grid: `CommonDataGridEx`, `GridColumnSizing` Date 120 / Name 240 / Quantity 120 / Status 115. Command column 88px left-fixed.

**768px tablet:** hide `iv-list-desktop`, show `iv-list-compact` tap rows (`inventory-chrome.css` max-width 767px). Send Out / Receive Back remain visible (not overflow-hidden). Hit targets ~44px.

**Blocked-mode banner (required while 2I/2P BLOCKED):**

```text
Posting is not enabled. You can review layouts and save drafts after Phase 1
approval. Stock and WIP will not move. Inventory lot-controlled items and
Work Order send-out stay disabled.
```

---

## Wireframe 2 — Send Out (one page, three decisions)

```text
SEND OUT                                          chips: Draft | lines 1 | status NEW
Guidance: Who → What → How much. Then Save. Post is a separate confirmation
          on the list — currently disabled.

Header (DxFormLayout SizeMode.Small)
  Date (4)   Status RO (4)   Doc No RO (4)
  Supplier PoSupplierPicker (6)   Supplier name RO (6)
  Source: ( ) Inventory stock   ( ) Work Order WIP
          Inventory-only company: source radio HIDDEN, Inventory implied.
          Production adapter BLOCKED: WO radio visible only with rights, disabled
          with "Work Order send-out is not available yet".
  Promised return date (4)   Linked PO RO (8)  [Link PO] [Create PO Draft →]
  Remark (12) optional

Lines (compact, no per-line modal required for qty edit)
  Actions | Item            | Source                         | Avail | Qty | UOM | Due
  [Edit]  | METAL-A Paint   | Main / LOC-A  (non-lot)        | 500   | 300 | PCS | ...
  Toolbar: [Add selected…]  (popup multi-select IvBalLocPicker)

Footer: 1 line · 300 PCS     [Cancel Secondary] [Save Primary]
        NO Post button on entry (inventory canonical).
```

**Add-selected popup (760px, copy IvStockTransfer line popup):** Item + `IvBalLocPicker`; show warehouse/location/lot/available; qty SpinEdit; dest supplier warehouse RO from mapping (Phase 2I). Do not auto-pick an ambiguous lot. Do not allow `LotControl=true` while BLOCKED — disable with *This item is lot-controlled. Send-out cannot use Stock Transfer until return-to-lot is designed.*

**20-line task:** one multi-select, then edit qty in grid. No 20 New screens.

**Keyboard:** Tab supplier → source → qty → next line. Enter in picker searches; Enter must **not** Post.

---

## Wireframe 3 — Receive Back

```text
RECEIVE BACK
  Supplier or dispatch no.   → shows ONLY open eligible lines

  Originally sent / Already returned / Still out / Receiving now

  Actions | Dispatch | Item/WO | Sent | Already | Still | Qty now | UOM
  [ ]     | SD-1001  | METAL-A | 500  | 300     | 200   | 200     | PCS

  More details (collapsed): Accept / Hold / Rejected returned / Unprocessed /
                            Supplier-side loss (loss is NOT a normal receipt qty)

Footer: [Cancel] [Save]
Post on list: disabled while adapter BLOCKED.
If QA required and frozen: "Cannot accept: inspection cannot hold stock yet."
Over-return: inline "You can receive only 200 PCS on this dispatch."
```

Two dispatches of non-lot METAL-A to the same supplier warehouse must remain **separate rows**. Warehouse total is not an allocation.

---

## Wireframe 4 — Inquiry / statement

Filter: supplier, mode, item, WO, status, date, due. Outstanding **by source line and base UOM**. Monetary columns hidden without `ViewCost`. As-of date uses business date, not “today’s UI total”. Label *Sent (awaiting acknowledgment)* vs *Supplier confirmed* — do not claim vendor acceptance on Post alone.

Deep links: existing PO page, QA case (record-only banner), stock/WO inquiry. Do not clone those pages.

---

## Wireframe 5 — Error / blocked states (must be designed before Post exists)

| Situation | Message (plain English) | Control |
|---|---|---|
| Lot-controlled item | This item is lot-controlled. Send-out is not available until return lots are designed. | Line rejected; draft other lines kept |
| Adapter BLOCKED | Cannot post: stock transfer cannot be saved together with this subcontract document yet. | Post disabled |
| Offsite isolation missing | Cannot post: stock at the supplier warehouse could still be sold or issued. | Post disabled |
| Production mode | Work Order send-out is not available. | WO source hidden/disabled |
| Required QA frozen | This item is awaiting inspection. Quality cannot hold warehouse stock yet. | Receive Post blocked |
| Over-return | You can receive only 40 PCS on this dispatch. | Qty invalid |
| Rollback after receipt | Cannot undo: 15 PCS were already received. | Rollback disabled + reason |
| Closed period | This date is in a closed period. Use an open date or ask an administrator. | Post/Rollback refused |
| Conversion RAW→FG | Conversion required. This version cannot change the item code. | Option disabled |
| Partial multi-batch | Never show “Posted” if only some source batches moved. | N/A until 2I |

Retain entered rows after validation failure. Confirm only Post/Rollback, not ordinary navigation (prompt if dirty).

---

## Component mapping

| Subcon area | Copy from | Notes |
|---|---|---|
| Workbench chrome / empty | `QaWorkbench.razor`, `PoProcurementWorkbench.razor` | `iv-page`, KPI, `qa-empty` |
| Workbench grid / filter / confirm | `IvMiscReceiptList.razor[.cs]` | server `CustomDataSource`, debounced search |
| Send Out header/footer | `IvMiscReceipt.razor[.css]` | `SizeMode.Small`, sticky footer @860px |
| Send Out from/qty | `IvStockTransfer.razor` | `tr-cell-stack`, avail advisory |
| Supplier | `PoSupplierPicker` + `PoSupplierSearchPopup` | 70vw, PREV/NEXT, Enter search |
| Inventory source pick | `IvBalLocPicker` | large-data paging |
| WO/operation context | `PrMaterialIssueEntry` / `PrWorkOrderEntry` ops grid | identity only; no PWO command bar |
| PO link | `PoOrder.razor` deep link | `sdoc-field-hint`, line flags; no service-line label in current PO UI |
| QA | `QaWorkbench` + Misc Receipt Request QA | `QaRecordOnlyBanner` |
| Row actions | `CommonDataGridEx` + left-fixed command column | VisibleIndex 0 |
| Buttons | Save Primary, Cancel Secondary, Post Success, Rollback Warning | list toolbar |

PoOrder has **no** “service line” caption. Link commercial PO as a reference; do not invent a Subcon service-line editor.

---

## 5-task UAT script (measure later; do not claim now)

Population: **5 non-developer operators × these 5 tasks = 25 trials**. Target ≥23/25 completed without coaching; **5/5** on tasks 2 and 5 (correct qty / blocked action). Record time, errors, scroll, training.

Prepared data: one supplier ABC Painting, item METAL-A non-lot 500 PCS available, promised date, tablet 768 and desktop 1366, keyboard-only pass.

| # | Task | Target | Pass if |
|---|---|---|---|
| 1 | Send one eligible line (supplier, item, qty 300, Save) | median ≤60s excluding login | 3 decisions + Save; no accidental Post |
| 2 | Attempt Post while adapter BLOCKED | — | Operator **recognizes** block; no false success; draft kept |
| 3 | Receive partial 200 against remaining 300 (after posting exists) | median ≤45s | Cannot type 400; remaining 100 shown |
| 4 | Dispatch 20 prepared source lines | ≤3 min | One multi-select; no 20 New screens; invalid 21st line keeps other 20 |
| 5 | Find how much is still with ABC | 1 workbench query | No mixed-UOM KPI; filter supplier |

**Now (Phase 0):** paper walkthrough of tasks 1, 2, 4, 5 against Misc Receipt / Stock Transfer / Workbench screens for friction (field width, vertical scroll, command column). Task 3 waits for Phase 3.

**Phase 1 (if approved):** live non-posting prototype of 1, 2, 4, 5. Fix navigation friction **before** any posting integration.

**Phase 4:** 5×5 with Inventory-only staff first; Production users only if 2P unblocked.

---

## Form factor

| Width | Must show | Must not |
|---|---|---|
| 768 tablet | Send Out / Receive Back, Save, supplier, qty | Hidden primary in overflow; tiny 20px hits |
| 1024 tablet | Grid + header without horizontal scroll for Actions | 12 unused columns |
| 1366 desktop | Full workbench KPIs + grid | Oversized WO-style command bar |

Date format in controls: `dd/MM/yyyy`. Persist under current date policy.

ViewCost: hide money, never hide physical qty from authorized operators.

---

## Phase 0 UX conclusion

The live inventory compact entry + list Post pattern is the right shell. Workbench should feel like Quality/Procurement home, not a second Inventory module and not a WO workspace.

**UX is not a posting license.** A beautiful Send Out that calls `IvStockTransferService.PostAsync` then writes Subcon rows would violate atomicity. Prototype Save-draft and disabled Post until Phase 2I is unblocked.

**STOPPED for Phase 1 UI-shell approval (optional) and for any posting phase.**
