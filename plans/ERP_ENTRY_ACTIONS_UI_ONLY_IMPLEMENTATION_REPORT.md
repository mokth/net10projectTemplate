# ERP Entry Actions UI-only — Phase 6 implementation report

Working branch/HEAD: `fix/dataprotection-key-ring` @ `cebb854` (`cebb85487cd6cbc1f1e928df2bc58658aa1e567a`, message: `checked in Pur inv cn dn fix`)  
DevExpress.Blazor: `26.1.4`  
Report date: 2026-10-30  
App under UAT: `http://127.0.0.1:5088` (`dotnet run --project ErpWeb/ErpWeb.csproj -c Release --no-build`, DEMO admin)

## Pre-existing dirty files

Not overwritten. Unrelated to this change:

- Modified: `.agents/skills/erp-transaction-ui-standard/skill.md`, `.agents/skills/erp-ui-core/skill.md`, `.gitignore`, `ANTIFORGERY_FIX.md`, `DEPLOYMENT_GUIDE.md`, `FINAL_DEPLOYMENT_STEPS.md`, `deploy.ps1`
- Untracked (pre-existing): `.agents/skills/erp-entry-footer-standard/`, `.agents/skills/erp-grid-row-actions-left/`, `.agents/skills/erp-inline-search-standard/`, `.replay-wac-fg-origin-20261030/`, `DATA_PROTECTION_KEY_RING_RUNBOOK.md`

Allowlisted UI files were clean at the start of this change.

## Baseline build/test results

- `dotnet build ErpWeb.slnx -c Release`: **exit 0**, 0 errors.
- Full `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj -c Release` was not captured as a numbered baseline before markup edits. Historical local-environment failures (customer validation, SQL concurrency, UBL self-bill roots, product-definition snapshots, inquiry workbench) were already present on this machine.

## Files modified (exact diff)

Allowlisted UI / tests / reports only. `git diff --name-only -- ErpWeb.Core ErpWeb.Model ErpWeb.Library ErpWeb.Report ErpWeb.UI/Purchase/Transactions/PoCdn.razor.cs` is empty.

**Shared grid**

- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor`
- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor.cs` (layout-load hook only)
- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGrid.razor`
- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGrid.razor.cs` (layout-load hook only)
- `ErpWeb.UI/Components/Common/DataGrid/GridLayoutPersistence.cs`
- `ErpWeb.Tests/UI/GridLayoutPersistenceTests.cs` (new)

**Direct entry/detail command columns (Phase 2)**

- Inventory: `IvMiscReceipt.razor`, `IvMiscIssue.razor`, `IvGoodsReceipt.razor`, `IvStockReturn.razor`, `IvVendorReturn.razor`, `IvStockAdjustment.razor`, `IvScrap.razor`, `IvStockTransfer.razor`
- Purchase: `PoCdn.razor` (popups only; main already pinned), `PoCdn.razor.css` (new, page-scoped right-group alignment), `PoSbCdn.razor`, `PoSbInvoice.razor`, `PoPr.razor`, `PoOrder.razor`, `PoInvoice.razor` (Cancel icon only)
- Sales: `SaCdn.razor`, `SaDeliveryRequestEntry.razor`, `SaDo.razor`, `SaInvoice.razor`, `SaQt.razor`, `SaSo.razor`, `SaCustEntry.razor`
- Production: `PrWorkOrderEntry.razor`, `PrProductionStockAdjustmentEntry.razor`, `PrProductionStockReturnEntry.razor`, `PrFinishedGoodReceiptEntry.razor` (+ `.razor.css` sticky), `PrDailyProductionEntry.razor` (+ `.razor.css` sticky), `PrMaterialIssueEntry.razor`, `PrWorkOrderTransferEntry.razor`, `PrShiftEntry.razor`

**Reports**

- `plans/ERP_ENTRY_ACTIONS_UI_ONLY_SCOPE_MATRIX.md` (Phase 0)
- `plans/ERP_ENTRY_ACTIONS_UI_ONLY_IMPLEMENTATION_REPORT.md` (this file)

`git diff --stat -- ErpWeb.UI ErpWeb.Tests/UI`: 36 tracked files, +566 / −119, plus the three untracked allowlisted files above.

## Page/route coverage matrix (PASS / EXCEPTION / NOT RUN)

| Route / page | Outcome | Evidence |
|---|---|---|
| `/inventory/misc-receipt` list (`GridKey=inv-misc-receipt-list`) | **PASS** (partial Gate 1) | Browser: Command visually left of Selection; View stayed at x=46 after ~796px horizontal scroll; COLUMNS chooser lists BATCH NO…MODIFIED BY, no Actions/Command |
| `/inventory/misc-receipt/new` | **PASS** | Cancel+Save bottom-right (`actionsX` 1163 / 980 on 1366px) |
| `/inventory/misc-receipt/view/11` (POSTED) | **PASS** | Close only; no Save/Edit |
| `/purchase/debit-notes/new` and `/purchase/credit-notes/new` | **PASS** | Cancel+Save right; hint `0 lines`; Save disabled (`Enabled="@CanSave"`) |
| `/purchase/debit-notes/new` @ 320px | **PASS** (overflow) | Cancel x=20, Save x=114, `overflow=false`. Sidebar overlay in screenshot is the mobile nav, not footer overflow |
| `/planning/work-orders/new` | **PASS** | Command bar BACK / HOW TO USE / MORE / RELEASE only; footer CANCEL + SAVE DRAFT right (`actionsRight` 1332 vs `footerRight` 1336) |
| `/planning/shifts/new` | **PASS** | Header has no Save; footer Cancel then Save, right-aligned |
| Shared-grid Reset Layout | **NOT RUN** | Not clicked in this session |
| Header context-menu Hide Column | **NOT RUN** | Column chooser verified; separate header Hide Column not exercised |
| Existing `:v2` layout captured *before* this change | **NOT RUN** | No pre-change localStorage snapshot was taken; unit tests cover schema reconciliation only |
| Selection-only / neither lists; 1/2/3/5 action counts | **NOT RUN** | Misc Receipt is Actions+Selection (2 icons) only |
| Light/dark, 375 / 768 / 200% zoom | **NOT RUN** | 1366 and 320px only |
| PoCdn posted lifecycle (Post/Delete/Rollback left) | **NOT RUN** | New CN/DN only; no posted document opened |
| PoSbCdn / PoSbInvoice View/Edit/locked | **NOT RUN** | Markup only |
| Adjustment / Return / WO Transfer New/Edit/View | **NOT RUN** | Markup only |
| DP / FG / Issue View Close | **NOT RUN** | Markup only (`BackToList` at end of view branch) |
| SaDo / SaInvoice confirm Post Success | **NOT RUN** | Markup `ButtonRenderStyle.Success`; confirm dialog not opened |
| Direct-grid horizontal scroll (entry line grids) | **NOT RUN** | Pin markup present; browser scroll not repeated per grid |
| `IvPeriodClose` REOPEN, 56px attachments, 72px DR picker, `SaCustPriceGroupList` / `IvClassList` / `SaDisGroupList`, `PoSuppEntry` | **EXCEPTION** | Audit only; 72px DR picker left unpinned |
| F14 Product Def / calendars / stock count | **EXCEPTION** | Inspect only |

## Shared grid verification (persisted v2, legacy, no actions, selection)

**Markup (declaration order unchanged):** `CommonDataGridEx` / `CommonDataGrid` still declare **Selection then Command**. Visual order uses `VisibleIndex=0` (Command) and `VisibleIndex=1` (Selection when actions exist, else 0), both `FixedPosition.Left`, `AllowReorder=false`, `ShowInColumnChooser=false`.

**Style mapping (required):** row `DxToolbarItem` maps `ButtonInfo.Style` case-insensitively: primary / secondary / success / warning / danger / info; unknown/missing → `ButtonRenderStyle.Info`. Captions, icons, `Enabled`, and `OnEditClick` unchanged.

**Persistence helper:** `GridLayoutPersistence.NormalizeLeftActionColumns` / `EnforceLeftActionColumns`:

- Identical schema: Command `VisibleIndex=0` Visible; Selection `VisibleIndex=1` (or 0 if no actions).
- Optional Command/Selection mismatch: insert/remove those entries only; rebuild source order **Selection then Command** then data; preserve data-column FieldName / VisibleIndex / Visible / Width / sort / group.
- Ambiguous unnamed columns or duplicate Command/Selection: return `null`; load hook **does not assign** `e.Layout` and **does not overwrite** stored `:v2`.
- Key remains `erp-grid-layout:{GridKey}:v2`. No `StripColumnWidths` on active v2 loads. Legacy: existing strip (Ex also strips filter) then normalize then save v2.
- `ColumnType` accepted as `"Command"` / `"Selection"` string **or** numeric enum.

**Unit tests:** `dotnet test --filter FullyQualifiedName~GridLayoutPersistenceTests -c Release` → **Passed 14 / Failed 0**. Covers four variants, insert/drop optional columns, hidden command forced visible, duplicates blocked, unnamed blocked, numeric enum, strip widths/filter, legacy strip+normalize, cross-variant actions-only → actions+selection.

**Browser:** Misc Receipt list Command-then-Selection visually; View remained at left after horizontal scroll; column chooser omitted Actions. Reset Layout, pre-change `:v2` fidelity, Hide Column, dark, 375/768/200% zoom, selection-only/neither: **NOT RUN**.

## Direct grid and popup verification (per grid)

Pinned `VisibleIndex=0` `FixedPosition.Left` (declaration order unchanged):

| Grid | Width | Status |
|---|---|---|
| IvMiscReceipt / Issue / GoodsReceipt / StockReturn / VendorReturn / Adjustment / Scrap | 88px | Markup PASS; scroll **NOT RUN** |
| IvStockTransfer | 120px | Markup PASS; scroll **NOT RUN** |
| PoCdn main | already Left | SKIP re-pin |
| PoCdn invoice picker / line picker | 220px / 90px | Markup PASS; popup **NOT RUN** |
| PoSbCdn main + origin picker | 88px + 88px | Markup PASS |
| PoSbInvoice main | 88px | Markup PASS |
| PoPr / PoOrder main | 88px | Markup PASS |
| SaCdn / SaDo / SaInvoice / SaQt / SaSo main | 88px | Markup PASS |
| SaDo / SaInvoice source pickers | 115px | Markup PASS (wide) |
| SaDeliveryRequestEntry main / shortage | 88px / 110px | Markup PASS |
| SaDeliveryRequestEntry 72px eligible-source picker | 72px | **EXCEPTION** — unpinned |
| SaCustEntry address / contact | 96px | Markup PASS |
| PrWorkOrderEntry Change / Select | 90px | Markup PASS |
| Adjustment REMOVE / Return REMOVE / FG REMOVE | 90 / 92 / 110px | Markup PASS |
| PoOrder 56/70px attachments & PR picker; PoPr 56px attachments | — | **EXCEPTION** — unpinned |
| IvPeriodClose REOPEN | — | **EXCEPTION** |
| PoSuppEntry | — | AUDIT ONLY |

## Mode-aware footer verification (per F01–F14)

| ID | Page | Result | Notes |
|---|---|---|---|
| F01 | PoCdn | **PASS** (New CN/DN) | Lifecycle left (Post/Delete/Rollback byte-for-byte); Cancel/Save or Close/Edit right via `.pocdn-footer__entry { margin-inline-start:auto }`; Save `Enabled="@CanSave"`; caption `Saving…` when `IsSubmitting`; hint `LineCountLabel` when no lifecycle. Posted lifecycle **NOT RUN** |
| F02 | PoSbCdn | Markup PASS / UAT **NOT RUN** | Close only in `IsView`; Cancel in `!IsView`; both `Click="@GoBack"` |
| F03 | PoSbInvoice | Markup PASS / UAT **NOT RUN** | Same Close/Cancel split |
| F04 | PrShiftEntry | **PASS** | Cancel then Save in bottom `iv-footer`, `ms-auto`; clicks `@(() => Nav.NavigateTo("/planning/shifts"))` and `@SaveAsync` |
| F05 | PrWorkOrderEntry | **PASS** (New) | SAVE DRAFT + EDIT DRAFT moved from command bar; footer Cancel+Save or Close+Edit Primary; MORE/BACK/HOW TO USE/lifecycle stay top; `Enabled="@CanSaveDraft"`, `Click="@SaveDraftAsync"` / `@BackToList` / `@OpenEdit` |
| F06 | Adjustment | Markup PASS / UAT **NOT RUN** | SAVE DRAFT still `@if (CanSave)` `Enabled="@(!Busy)"`; Cancel/Close `BackAsync`; Edit Primary |
| F07 | Return | Markup PASS / UAT **NOT RUN** | Same wrappers; Cancel/Close `BackAsync` |
| F08 | WO Transfer | Markup PASS / UAT **NOT RUN** | Cancel/Close `BackToList`; confirm ROLLBACK `Danger` → `Warning` |
| F09 | Daily Production | Markup PASS / UAT **NOT RUN** | `.pr-dp-footer` sticky; View Close `BackToList` at end of view branch |
| F10 | FG Receipt | Markup PASS / UAT **NOT RUN** | Sticky; View Close after `LoadFailed` branch, not on LoadFailed |
| F11 | Material Issue | Markup PASS / UAT **NOT RUN** | View Close `BackToList`; footer already sticky |
| F12 | PoInvoice | Markup PASS / UAT **NOT RUN** | `fa-solid fa-xmark` on Cancel only |
| F13 | SaDo / SaInvoice | Markup PASS / UAT **NOT RUN** | Confirm POST DELIVERY ORDER / POST INVOICE `Primary` → `Success`; `ConfirmPostAsync` and Enabled unchanged |
| F14 | Product Def / calendars | **EXCEPTION** | Inspect only |

## Preserved handlers/eligibility/permissions evidence

- PoCdn Save remains `Enabled="@CanSave"` / `Click="@OnSaveAsync"`. Cancel/Close `Click="@OnClose"`. Edit `Click="@OnEditFromView"` under `IsViewMode && CanEditFromView`. Post `Enabled="@CanPostCommand"` `Click="@OnPostAsync"`; Delete `Enabled="@(CanEditPermission && !IsSubmitting)"`; Rollback `Enabled="@(CanPostPermission && !IsSubmitting)"`. `PoCdn.razor.cs` not in the diff.
- Shared-grid `.razor.cs` diffs are only `hasActions` / `hasSelection` plus `NormalizeLeftActionColumns` around existing load/migrate. No `ActionButtons`, `OnEditClick`, `GridKey`, paging, or permission changes.
- Production Save Drafts keep original wrappers and Enabled expressions (`CanSaveDraft`, `CanSave` + `!Busy`, `CanEditFields` + `CanSave && !Busy`). Lifecycle buttons were not moved.
- No `ErpWeb.Core` / `Model` / `Library` / `Report` edits. No new services.

## Light/dark/mobile/zoom/browser UAT evidence

- Browser: Cursor IDE browser, tab unlocked after UAT, DEMO admin, Fluent Light.
- **PASS:** Misc Receipt list left-fixed View/Edit after horizontal scroll; Misc Receipt New footer right; posted View Close; PoCdn/PoCdn DN New footer right + 320px no overflow; WO New footer right; Shift footer right.
- **NOT RUN:** dark theme, 375px, 768px, 200% zoom, Reset Layout, Hide Column, DP/FG/Issue View Close, posted PoCdn, Adjustment/Return/Transfer, SaDo/SaInvoice confirm.

## Post-change build and test results

- `dotnet build ErpWeb.slnx -c Release`: **exit 0**, 0 errors (same as baseline).
- `GridLayoutPersistenceTests`: **14 passed**, 0 failed, 122 ms.
- Full `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj -c Release`: **Failed 38, Passed 4903, Skipped 8, Total 4949**, duration 16 m 34 s, exit 1.

None of the 38 failures are `GridLayoutPersistenceTests`. Failures are the same families already known on this local SQL environment (SaCust validation, SQL concurrency, UBL self-bill roots, product-definition / BOM tree / explosion, PO inquiry, inventory posting concurrency). This change did not add Core/Model tests or data access. **No claim that the 38 equal a recorded pre-edit count** — a numbered baseline suite was not captured before markup edits.

## Explicit non-UI changes (must be NONE)

**NONE.** Diff fence holds. `PoCdn.razor.cs` untouched.

## OPEN preexisting unsaved-navigation gaps (unchanged)

Relabeling Close→Cancel does not add discard protection:

- `PoCdn`
- `PoSbCdn`
- `PoSbInvoice`
- `PoInvoice`
- `PrShiftEntry`
- `PrDailyProductionEntry`
- `PrMaterialIssueEntry`

Pages that already had guards (WO, FG Receipt, Adjustment/Return/Transfer, Misc Receipt family) were not changed.

## Blocking failures / unsupported scenarios

- Full test suite is not green (38 environment/pre-existing failures). New layout tests pass.
- Gate 1 extra (existing `:v2` layout from *before* this change, Reset Layout, header Hide Column, dark/375/768/200% zoom) **NOT RUN**.
- Several required entry UAT paths (posted PoCdn lifecycle, self-billed View/locked, production F06–F11, F13 confirm Post) **NOT RUN**.
- Extra Phase 0 command-column hits (`PoSuppEntry`, list/modal masters) remain audit-only.

## Final gate: FAIL

Implementation of the allowlisted UI is complete and fenced. Overall acceptance **cannot PASS**: required browser UAT items remain **NOT RUN** (never marked PASS by inspection), and the full test suite is not green. Layout unit tests and the exercised browser paths above are real evidence, not inspection.
