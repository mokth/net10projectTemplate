# Inventory Inquiry Suite

Plan of record: `plans/plan-inventoryInquirySuite.prompt.md`.
Reference implementation of the "inquiry kit": [`inventory-balance-by-lot.md`](inventory-balance-by-lot.md).

The suite answers inventory questions as **one page per user question**, never one page per filter.
This document covers what has shipped (Phases 1-3) and records the decisions the code is pinned to.
Phase 4 (non-lot ageing, a true valuation) needs schema work and is not started.

---

## Phase 1 — posted movements and the stock card (SHIPPED)

Two new read-only screens, both under **Inventory → Inquiry**, plus one additive index.

| Page | Route | Menu | What it answers |
|---|---|---|---|
| Transaction Inquiry | `/inventory/trx-inquiry` | `INV_TRX_INQUIRY` | What moved, when, in which direction, on which document? |
| Stock Card | `/inventory/stock-card` | `INV_STOCK_CARD` | What is this item's/pile's ledger, with an opening and a running balance? |

The TrxType multi-select is the **single control** that turns Transaction Inquiry into
*Adjustment Inquiry / Adjustment Analysis* (select `ADJ` alone). There is deliberately no second
page for adjustments — same query, one control.

### Why this was the real data gap

Nothing in the ERP could show *posted movements* outside a per-document header list. The movements
themselves were already written to `dbo.IvTrxHistory` by the posting engine; there was simply no
read path. Phase 1 adds one.

### The one database change

`scripts/alter-ivtrxhistory-inquiry-index.sql` adds
`IX_IvTrxHistory_Inquiry (CompanyCode, BranchCode, TrxType, TrxDtTime)`.

The pre-existing indexes are `UQ_IvTrxHistory_Company_Branch_Batch_Line`,
`IX_IvTrxHistory_ICode_TrxDtTime`, `IX_IvTrxHistory_BatchNo` and the four FK indexes. The inquiry's
**default** view is a type-and-date window with no item filter, which none of those can serve.
Additive and idempotent; run it twice.

**No further index was added (D20).** The stock card's dominant predicate is `ICode + TrxDtTime`,
which `IX_IvTrxHistory_ICode_TrxDtTime` already covers; the remaining slice columns are a residual
filter on a narrow seek. If a real execution plan on a representative table shows a scan or a sort
spill, design the next index **from that plan**.

---

## Architecture

```
ErpWeb.Model/Repositories/Inventory/
  IvTrxHistoryResults.cs        query / row / summary / scope key / sort whitelist
  IvStockHistoryRepository.cs   owns EVERY IvTrxHistory read
  IvStockInquiryRepository.cs   on-hand-derived compositions (Phase 2 grows here)
ErpWeb.Core/Inventory/
  IIvTrxHistoryService.cs + IvTrxHistoryService.cs
ErpWeb.UI/Inventory/Inquiry/
  IvTrxInquiry.razor(.cs)(.css), IvStockCard.razor(.cs)(.css)
ErpWeb/Inventory/
  Iv{TrxInquiry,StockCard}ExportEndpoints.cs, IvInquiryExportWorkbook.cs
```

Two guardrails worth keeping:

* **`IvStockCommonRepository` is not modified at all** (D8). It is ~1,900 lines and backs the shipped
  Balance-by-Lot page; `BuildBalanceLotQuery` stays byte-stable. New data domains get new files.
* **`IvInquiryExportWorkbook` is the one xlsx writer** for the new endpoints, so the "money column is
  omitted, never blanked" decision stays one decision instead of one per endpoint.

---

## Locked decisions (the code is pinned to these)

| # | Decision | Where it lives |
|---|---|---|
| D2 | Adjustment **Reason** is parsed from `Remarks` with the existing `IvStockAdjustmentLineInvariant.ParseStoredRemarks`, and only on `ADJ` rows | `IvTrxHistoryService.DecorateRowsAsync` |
| D11 (Option B) | Money visibility is the menu-based **`VIEW_PRICE`** permission on the page's own menu. `ICurrentUserService.CanViewPrice` is untouched and stays wired to Balance by Lot only | service + endpoint + seed scripts |
| D13 | Value = `IvQty.Round(NetQty × (history.UnitPrice ?? item.PurchasePrice ?? 0))`, signed. `Cost`/`CostPrice`/`AsNowCost` are **not** a basis | `IvStockHistoryRepository` |
| D12 | The stock card's opening and running balance are computed over **exactly the slice the row filter uses** | `IvTrxHistoryScope` |
| D19 | One export contract for every endpoint | both endpoints + the workbook helper |

### D12 — the running balance's scope key, in detail

The scope key is `(ICode, WhCode, LocCode, LotNo, IStatus)` within company+branch — the same 7-part
key as `IvStockSliceKey` / `UQ_IvBalLoc_StockSlice`, via `IvTrxHistoryScope.ToSliceKey`.

* `OpeningQty` = Σ net of every in-scope movement with `TrxDtTime < fromDate`.
* `RunningQty` = `OpeningQty` + the cumulative net of the period's rows, **in movement order**.
* A movement has a from-leg and a to-leg, and each leg is judged independently. That is what makes a
  transfer read correctly: filtering to the issuing warehouse shows **Out** only; filtering to the
  receiving one shows **In** only; unfiltered it shows both and nets to zero.
* **A multi-pile scope makes the running column a sum across piles.** The column heading says so —
  `Running (all bins/lots in scope)` — and it only reads plain `Running` when warehouse, bin, lot
  *and* a single status are all pinned. A management figure that looks like one pile's balance but is
  actually a total is exactly the kind of number that gets acted on wrongly.

The service requires an **item** and a **start date**: without an item a "card" degenerates into the
transaction inquiry, and without a period start the opening balance has no meaning. The screen
pre-fills a one-month company-local period rather than demanding input.

### The mandatory reconciliation caveat

The card shows the **ledger closing** and the live `IvBalLoc.StdQty` quantity side by side and flags
a non-zero difference in a warning banner. The opening balance is only sound when every stock entry
was posted here; **legacy or imported on-hand with no matching history is the expected cause**. That
is a disclosure, not a bug — do not "fix" it by hiding the live figure.

### What never appears, and why that is not a filter

History rows are written on `POSTED` batches only, and a rollback **deletes** that batch's history.
So cancelled and rolled-back documents have nothing to show. There is deliberately **no batch-status
filter**, because one could never change the result. `IStatuses` filters the *item/stock* status
(`IvTrxHistory.IStatus`), which is a different concept from the batch status — do not conflate them.
Both pages state this so the absence is not read as a defect.

---

## The common export contract (D19)

Every inquiry xlsx endpoint, no exceptions:

1. uses the **same applied query object as the grid**, never the grid's current page;
2. never pages (the row cap is the only ceiling);
3. resolves company/branch **server-side** and accepts **no** company/branch query parameter;
4. re-checks `PermissionCodes.Export` **inside** the handler;
5. omits the money column entirely (not blanked) when `VIEW_PRICE` is denied;
6. refuses with a message naming **both** numbers when the match count exceeds `MaxExportRows = 50 000`
   — it never truncates;
7. returns a row count equal to the grid's `TotalCount`.

One endpoint behaving differently from the others is a defect, and the summary/grid/export equality is
asserted at **two page sizes** so it provably cannot depend on paging.

The stock card's export re-runs the same service call the page uses, so the workbook's row count and
its running-balance column can never describe a different scope from the one on screen.

---

## Performance note — the one materialised list

The stock card's ledger is materialised (capped at `MaxStockCardRows = 20 000`, plus one row to
detect the cap) and paged in memory. A running balance cannot be aggregated in SQL without a window
function, which EF Core does not translate. The **summaries are still SQL-side** (`SumAsync` over the
composed query), so the plan's "never materialise rows for aggregates" rule holds; only the ledger
itself is materialised, and exceeding the cap is reported rather than silently trimmed.

Because the running column depends on the order, the stock card's grid **ignores sort requests**. The
page says so; re-ordering the rows would make the column meaningless.

The grid's `FieldName` is resolved by property name at render time, so every derived column
(`InQty`, `OutQty`, `NetQty`, `StdUom`, `Reason`, `EstValue`, `RunningQty`) is a **settable**
property populated by the service, not a get-only computed expression.

---

## Security

| Layer | Rule |
|---|---|
| Service | `IInventoryTenantContext.TryBranchScope()` first (fail closed → `InvalidScope`), then `ACCESS` on the **caller's own menu** |
| Service | An unknown menu code is refused, so a page cannot borrow another screen's rights |
| Endpoint | `PermissionCodes.Export` re-checked inside the handler; company/branch never read from the query string |
| Money | `VIEW_PRICE` on the page's own menu; masked in the service, omitted in the export |

The service serves two screens, so **every member takes the menu code**. That is not decoration: both
`ACCESS` and `VIEW_PRICE` are checked against the screen actually being served, and only the page
knows which that is.

---

## Menus and deployment

Adding a menu requires **both** halves, guarded by `MenuDeploymentParityTests`:

* `ErpWeb.Core/Menus/MenuCodes.cs` — `InventoryTrxInquiry`, `InventoryStockCard`
* `ErpWeb/Menus/menus.xml` — two children under `INV_INQUIRY` (SortOrder 2 and 3; Balance by Lot is 1)
* `scripts/init-inv-trx-inquiry-menu.sql` and `scripts/init-inv-stock-card-menu.sql`
* `scripts/alter-ivtrxhistory-inquiry-index.sql` (the index)

`MenuSyncService` **soft-disables** every `dbo.Menu` row whose code is absent from the XML, and
`AccessRightService` filters the cache on `IsActive` — so a missing XML row locks the page out even
though the URL still resolves for an admin.

### Why `VIEW_PRICE` is in the seed scripts

A permission that is not attached to the menu via `dbo.MenuPermission` **can never be granted**, so
`CanAsync(menu, VIEW_PRICE)` would be false for everyone and the money column would never appear.
Both scripts therefore attach `ACCESS`, `EXPORT` and `VIEW_PRICE` (and ensure the `VIEW_PRICE`
permission row exists).

A **role** still needs a `dbo.RoleMenuPermission` row with **`IsAllowed`** (that table has no
`IsActive`) before any user sees the value columns. That grant is deliberately the deployment owner's
step and is not made by the scripts.

---

## Verification

```powershell
dotnet build ErpWeb.slnx --nologo -v:q                       # 0 error CS / RZ
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=Menus"
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=InventoryTrxInquiry|Category=InventoryStockCard"
```

Apply each new `scripts/*.sql` twice on a scratch DB and twice on dev `ERPWeb`; the second run must
be a clean no-op.

Test classes: `IvTrxHistoryServiceTests` (`Category=InventoryTrxInquiry`) and
`IvStockCardServiceTests` (`Category=InventoryStockCard`), sharing one SQLite fixture.

Pinned behaviours include: company/branch isolation; ACCESS denied; an unknown menu refused; the
menu actually checked; `VIEW_PRICE` masking on grid, summary **and** export; the locked value formula
including the item fallback; scope-aware in/out for a transfer's two legs; **row quantities summing
to the summary** (the invariant that stops the SQL and in-memory definitions drifting); summary ==
grid == export at two page sizes; the half-open date range with the boundary rows; empty
type/status selection meaning *all*; an unknown sort field falling back; the opening boundary; the
running balance across in/out/ADJ rows; chronological order surviving a sort request; and the
live-vs-ledger difference.

---

## Not in Phase 1

* **Balance by Lot → Stock Card drill-down.** Recorded so it is not bolted on later; a separate change.
* **Phase 4** — non-lot stock ageing and a true inventory valuation. Both need schema work (a frozen
  receipt date or receipt/cost layers, and a costing method) and a written decision before starting.

---

# Phase 2 — stock control and position (SHIPPED)

Three read-only screens under **Inventory → Inquiry**, and **no schema change at all**.

| Page | Route | Menu | What it answers |
|---|---|---|---|
| Stock Alerts | `/inventory/stock-alerts` | `INV_STOCK_ALERTS` | What needs reordering, what is not moving, what is expiring? |
| Lot / Batch Inquiry | `/inventory/lots` | `INV_LOT_INQUIRY` | Where did this lot come from, where is it, and how did it get here? |
| Stock Summary | `/inventory/stock-summary` | `INV_STOCK_SUMMARY` | What are the balance totals by item / warehouse / item × warehouse / class? |

Plus one change to a shipped page: `IvStockMasterList` now shows **Min stock** and **Max stock**
columns (`IvStockMasterListRow` gained the two properties **and** the repository projection — the grid
resolves `FieldName` by property name, so adding only the column definition throws at render).

## Stock Alerts — seven rules on one page

The rule selector is the only control that changes the question; everything else is a filter. It also
decides the column set, because "Min stock" is meaningless on an expiry rule and "Expiry date" is
meaningless on a threshold rule.

| Rule | Predicate | Driver |
|---|---|---|
| `LOW` | `StockControl = 1 AND IsActive = 1 AND MinStock IS NOT NULL AND MinStock <> 0 AND OnHand < MinStock` | `IvStockMaster` |
| `OVER` | same guards, `MaxStock`, `OnHand > MaxStock` | `IvStockMaster` |
| `SLOW` | `OnHand > 0 AND LastMovement < AsOfDate − SlowDays` | item master + history |
| `DEAD` | `OnHand > 0 AND LastMovement < AsOfDate − DeadDays` | item master + history |
| `NEVER_MOVED` | `OnHand > 0 AND LastMovement IS NULL` | item master + history |
| `EXPIRING` | `IvLot.ExpiryDate >= AsOfDate AND <= AsOfDate + ExpiryDays` | `IvLot` |
| `EXPIRED` | `IvLot.ExpiryDate < AsOfDate` | `IvLot` |

Decisions the code is pinned to:

* **`AsOfDate`** is the date part of `ICurrentDateService`'s company-local clock (D14), resolved **once**
  per request and handed to every rule. The page displays the value the service actually used (the page
  DTO carries it back), so the header cannot disagree with the rules.
* **A NULL *or zero* threshold means "not configured" and never alerts.** The `IS NOT NULL` guard is
  explicit so NULL cannot silently become 0; a 0 ceiling can never fire because negative stock is
  impossible.
* **`LOW`/`OVER` are item-master-driven** (LEFT-JOIN-free correlated subqueries in the end — see the
  translation note below). An item that has never had a pile still alerts on `LOW`, which a
  balance-driven query cannot see.
* **`SLOW`/`DEAD`/`NEVER_MOVED` all require `OnHand > 0` (D21).** An item with no stock anywhere is not an
  operational stock alert — even if it moved long ago. `LOW`/`OVER` are the deliberate opposite.
* **`NEVER_MOVED` is its own rule, not "dead with a null date".** Opening/imported on-hand with no history
  at all is not stock that has gone stale; folding it into `DEAD` would report a never-used item as a
  stock-holding that stopped selling.
* **`lastMovement` = `MAX(IvTrxHistory.TrxDtTime)`** — never `IvBalLoc.TransDate`, which is a mutable
  per-pile date that a back-dated post can move backwards.
* **`EXPIRING`/`EXPIRED` return one row per lot *per warehouse holding it*** — "which lot expires, where is
  it and how much is there" is the actionable set. A lot with no pile left in scope is not an alert. A lot
  with **no** expiry date is never reported by either rule.
* **A lot expiring today is not expired** (and it is not "expiring" beyond the horizon's inclusive edge).
* `DeadDays` must be greater than `SlowDays`; the service refuses with a message naming **both** numbers.
* Threshold basis is reported on every row: `All warehouses` (the D3 item-level basis) or the pinned
  warehouse code.
* `IncludeInactive` / `IncludeNonStockControl` default **off**, so a deactivated item's stock is a disposal
  question rather than a movement alert, and a non-stock-controlled item is not a stock item at all. Both
  can be switched on.

## Lot / Batch Inquiry

The lot passport: origin (`SourceType`/`SourceDocNo`/`SupplierCode`), dates (`ReceiptDate`/`MfgDate`/
`ExpiryDate`), `QcStatus`, computed **age** and **days to expiry**, plus the piles holding it and its
posted movements.

* **Terminology is load-bearing.** *Lot* = the inventory traceability lot (`IvLot`). *Batch* = an
  inventory document/batch number (`IvTrxBatch`), reachable from the Transaction Inquiry page. The screen
  is titled "Lot / Batch Inquiry" so the word is findable, and it carries a legend saying which is which.
  The code artefact stays `IvLot*`.
* A lot with **no stock left still appears** — its movements are the audit trail.
* Non-lot items have no `IvLot` row and are explicitly out.
* The **movements panel goes through `IIvTrxHistoryService`** (with the lot menu added to that service's
  allow-list), so the lot ledger shows the same scope-aware In/Out and the same adjustment-reason parsing
  as every other movement view. Pinning item **and** lot means a transfer between lots appears on both
  ledgers.
* ACCESS only: no money column, and (per the plan) no xlsx export.

## Stock Summary

Server-side `GROUP BY` over the balance slice, with an **Item / Warehouse / Item × Warehouse / Class**
selector.

* **UOM presentation is per grouping mode (D16).** A row's quantity is in that item's *standard* UOM, so
  `ITEM` and `ITEM_WAREHOUSE` carry a real UOM and a plain "Total qty". `WAREHOUSE` and `CLASS` **claim no
  UOM at all** and hide the quantity column by default; the opt-in shows it captioned **"Total qty (mixed
  Std UOM)"**. A management figure like "Warehouse A = 15,382" misleads even with a tooltip. The opt-in
  travels to the export, so a file can never carry a number the screen withheld.
* **"Class" = `IvStockMaster.IClassCode`** resolved through `IvClass.IDesc` — the codebase's own meaning
  of the word, as the item-master list and the stock-count scope already use it. It is not
  `Classification`, `IType` or `ISubClassCode`.
* **`Est. value`** reuses the shipped formula `SUM(StdQty × (bal.UnitPrice ?? item.PurchasePrice ?? 0))`,
  rounded with `IvQty.Round`. The page states **"Estimate — not a GL valuation"**: each pile keeps the
  price it was received at, there is no costing method and no revaluation.
* The summary's own measures (`TotalQty`, `PileCount`, `ZeroQtyPileCount`, `ItemCount`, `TotalValue`) are
  computed over the **flat** slice, so they are identical for every grouping mode; only the group count
  depends on the mode.

## Extensions to the inquiry kit

* `IIvStockInquiryRepository` grew the Phase 2 compositions (alerts, lot slice, piles, summary) — D8's
  guardrail held: **`IvStockCommonRepository` was not touched**.
* `IIvStockHistoryRepository` gained `MovementsForBranch(...)`, the composable source for movement reads.
  Every `IvTrxHistory` read is still **defined** in that one file.
* A shared `IvInquiryScopeResolver` now holds the resolve order (tenant first, fail closed, then ACCESS on
  the caller's own menu) for the three new services, so the order and the two messages cannot drift
  between the pages of one suite.
* The export contract (D19) was extended to the two new workbooks. `MaxExportRows = 50 000` now lives once
  on `IvInquiryExportWorkbook`; `IvTrxInquiryExportEndpoints.MaxExportRows` references it.

## EF Core translation notes (these cost real debugging time)

SQLite/EF will happily *compile* a query and then refuse to translate it at run time. Three shapes were
tried and two had to be abandoned:

1. **A conditional (`x == null ? 0 : x.Qty`) built over a joined aggregate, then compared in a `WHERE`,
   is NOT translatable.** EF inlines the conditional into the consumer and emits
   `a && b ? 0 : c < d`. Fixed by removing the joins entirely: `Sum` over a `decimal` sequence already
   yields 0 for no rows and `Max` over a nullable sequence yields `null`, so **no conditional is needed**.
2. **A helper method called *inside* a projection is an unexpandable method call.** The movement source
   must be **captured in a local** (`var movements = ...MovementsForBranch(...)`) before the projection so
   the expression tree can compose over it.
3. **`GROUP BY` over a projection holding entity references (a `DefaultIfEmpty` `TransparentIdentifier`
   chain) is NOT translatable.** The summary slice therefore projects **flat scalar columns only** — which
   also removes every conditional from the grouping keys, since a LEFT JOIN already yields `NULL`.

And one logical trap: **a grouped query may only be ordered by its own grouping keys and aggregates.**
Ordering an Item group by warehouse is invalid SQL, so the summary's legal sort fields are derived **per
grouping mode**; anything else falls back to that mode's default order (`IvStockSummarySortFields` was
deleted rather than left as a misleading all-modes whitelist).

## Verification (Phase 2 additions)

```powershell
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=InventoryStockAlerts|Category=InventoryLotInquiry|Category=InventoryStockSummary"
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=Inventory&Category!=SqlServer"
```

Apply `scripts/init-inv-{stock-alerts,lot-inquiry,stock-summary}-menu.sql` twice each (the second run a
clean no-op). Note that when the app is already running, `MenuSyncService` inserts the XML's menu rows
with **all** built-in permissions first, so those scripts are then no-ops — that is the XML-first
behaviour, and the scripts still matter for a script-first or fresh database.

Pinned behaviours: every case in the plan's mandatory fixture list — NULL/zero thresholds; exactly on the
threshold and one unit either side; **an item with no pile at all still alerting on LOW**; zero on-hand; no
movement ever; both ageing boundaries with `DeadDays > SlowDays` enforced; an expired lot; **expiry exactly
today not being expired**; expiry exactly `ExpiryDays` away; the lot passport's age and days-to-expiry
following the injected clock; the expiry window being inclusive; the movements panel being scope-aware and
limited to one lot; the summary's per-mode UOM rule; mixed-unit groups claiming no UOM; zero-quantity
piles; the same item in two warehouses and in two lots; `Class` resolving through `IvClass`; the estimate
formula with the pile price winning; price masking across grid/summary/export; and summary == grid ==
export at two page sizes.

# Phase 3 — evidence and estimate (SHIPPED)

Three read-only screens under **Inventory → Inquiry**, and again **no schema change at all**.

| Page | Route | Menu | What it answers |
|---|---|---|---|
| Stock Count Variance | `/inventory/stock-count-variance` | `INV_STOCK_COUNT_VAR` | How accurate were our physical counts, and what did they find? |
| Est. Inventory Value | `/inventory/valuation` | `INV_STOCK_VALUE` | What is the on-hand worth, grouped by item / warehouse / class? |
| Reconciliation | `/inventory/reconciliation` | `INV_RECONCILIATION` | Does the live on-hand agree with the posted movement ledger? |

## Stock Count Variance — sheet variance, and why the distinction matters (D4)

* The report lives on the **existing `IIvStockCountService`** (`IvStockCountService.Variance.cs`, the class
  is now `partial`) rather than in a parallel service: it is a read of the very same sheet/line evidence
  that service owns. Each variance member takes the **menu code** it is serving, so the variance screen
  has its **own grant** — reading count evidence is not the same right as creating, counting, posting or
  rolling back a sheet.
* **POSTED sheets only.** A DRAFT or COUNTED sheet has no evidence, and a ROLLED_BACK sheet's adjustment
  is no longer in the ledger — including either would print a variance beside stock that no longer exists.
  The page states this so the absence is not read as a defect.
* `Variance = SystemQty − PhysicalQty`, rounded through `IvQty.Round`, with the direction tokens in the
  **same sense as the count screen's own post preview** (`> 0` = write-down → `DECREASE`).
* **The staleness disclosure is the point of the page.** `SystemQty` is Generate-time evidence and posting
  re-reads the live balance, so this is a *sheet* variance; the header's `PostedStaleLines` travels onto
  every row and is totalled in the summary (per **sheet**, never multiplied by the sheet's line count).
* **Accuracy (D17, locked):** `ExactMatchLines / CountedLines × 100`, with an uncounted line excluded from
  the denominator rather than counted as agreement, and **`CountedLines = 0` rendering “—” (null), never 0
  and never 100**. A `1 − ABS(variance)/ABS(systemQty)` ratio is forbidden — `SystemQty = 0` is common in a
  count and divides by zero. `SystemQty = 0` with a positive count, and the reverse, are both asserted.
* The summary's SQL definitions are pinned to the rows by a test that recomputes `ExactMatchLines`,
  `NetVarianceQty` and `AbsVarianceQty` from the returned rows — the same anti-drift invariant Phase 1 used.

## Est. Inventory Value — the name is the disclaimer (D5)

* It reuses **`IIvStockSummaryService`** under its own menu code. "How much stock is there, by group" and
  "what is it worth, by group" are the same composition with a different emphasis, and a second grouped
  query would only give the two screens a way to disagree.
* Its `ACCESS` and `VIEW_PRICE` are checked against **`INV_STOCK_VALUE`**, not the summary screen's grant.
  A test grants `VIEW_PRICE` on the summary menu only and asserts the value screen still masks — the
  literal D11 Option B isolation.
* Item / Warehouse / Class (no Item × Warehouse — that split belongs to the Stock Summary screen), and the
  D16 unit rule applies unchanged: Warehouse/Class groups claim no single UOM and hide the quantity column
  unless the caller opts in.
* The caption **"Estimate — not a GL valuation"** and the price-vintage caveat are on the page, and the
  page is never titled "Inventory Valuation".

## Reconciliation — the slice is the unit of comparison (D18)

* `IvInventoryReconciliationService` now aggregates **both sides on the same 7-part slice key**
  (`IvStockSliceKey.Create`, resolved through the `BalLoc` a history leg points at) instead of comparing
  balance rows one by one. The slice a leg belongs to is never inferred from strings.
* Findings are emitted in **slice order**, so two runs diff cleanly, and a mismatch names the full slice.
* **One false positive was removed while implementing this.** `ORPHAN_HISTORY` used to resolve "does this
  pile exist" against the **filtered** balance set, so asking about one warehouse reported every transfer
  that came from another warehouse as orphaned history. Existence is now resolved against the **whole
  branch**; only the comparison is filtered.
* ACCESS is now checked on `INV_RECONCILIATION` (it was unchecked) — tenant first, then the permission,
  the same order as every other inquiry service. **ACCESS only, no export**, and the service's own
  "diagnostic only — needs an opening-balance baseline" caption is repeated on the page verbatim.
* Note the schema guarantees the happy path: `UQ_IvBalLoc_StockSlice` means two balance rows for one slice
  cannot exist in a correct database, and the `IvBalLoc` FK means a truly dangling history row cannot
  either. Both checks exist for **legacy** data, which is why the tests exercise them through a
  cross-branch pile rather than by seeding an impossible row.

## EF Core translation notes (Phase 3 addition)

* **`Math.Abs` has no SQL translation on this provider.** The absolute-variance total is therefore built
  from two one-sided sums (over `SystemQty > PhysicalQty` and `< PhysicalQty`), which also keeps both
  sides positive without a conditional.
* The variance slice carries two entity references and is still translatable, because its projection is a
  final `Select` over real columns — no `GROUP BY` and no conditional aggregating them. That is the
  distinction from the Phase 2 summary lesson, not a contradiction of it.

## Verification (Phase 3 additions)

```powershell
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=InventoryStockCountVar|Category=InventoryStockValue|Category=InventoryReconciliation"
dotnet build ErpWeb/ErpWeb.csproj -t:Compile        # checks the host without fighting the running app's file locks
```

Apply `scripts/init-inv-{stock-count-variance,stock-value,reconciliation}-menu.sql` twice each (the second
run a clean no-op), and check that each script's verification `SELECT` lists exactly the permissions its
`PRINT` claims — 2, 3 and 1 respectively.

Pinned behaviours: the four sign/direction cases including zero system quantity against a positive count;
both-zero; an uncounted line excluded from the denominator; nothing counted ⇒ accuracy null; every
non-POSTED status excluded; the stale count summed per sheet not per line; the value formula with the
pile's own price winning; price masking across grid, summary and export; the value screen's own price
grant; no false finding when the ledger and the balances agree; a transfer netting against both of its own
slices; an orphan history row still being reported; and no orphan manufactured by a filter.

## Still owed (needs a human)

* Browser smoke of all six inquiry pages (`dotnet build ErpWeb.slnx` is mandatory after touching
  `ErpWeb.UI`; the test project compiling against `ErpWeb.UI` does not compile `.razor` markup).
* A role needs `RoleMenuPermission.IsAllowed` before anyone sees a value column on either money screen.
* Phase 1's performance acceptance test (plan item 9 / D20) is still outstanding: seed a representative
  `IvTrxHistory` volume and capture the execution plans for the stock card's opening query and an
  unfiltered date-range inquiry, then add an index **only** from a real plan.
* **Phase 4** — non-lot stock ageing and a true inventory valuation. Both need schema work (a frozen
  receipt date or receipt/cost layers, and a costing method) and a written decision before starting.

