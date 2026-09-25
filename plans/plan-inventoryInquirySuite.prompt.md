# Plan: Inventory Inquiry Suite (MUST + SHOULD)

> **STATUS — Phases 1, 2 and 3 SHIPPED (2026-09-24/25). Phase 4 NOT started (deferred by this plan).**
>
> Phase 3 delivered items 14-17: `IvStockCountVariance` (`/inventory/stock-count-variance`,
> `INV_STOCK_COUNT_VAR`), `IvStockValue` — Est. Inventory Value (`/inventory/valuation`,
> `INV_STOCK_VALUE`) and `IvReconciliation` (`/inventory/reconciliation`, `INV_RECONCILIATION`), plus
> two exports, three `init-inv-*-menu.sql` scripts (applied twice to dev `ERPWeb`, second run a clean
> no-op) and the D18 slice-aggregation rework of `IIvInventoryReconciliationService`.
> **0 schema change**; `IvStockCommonRepository` still untouched (D8 held).
>
> Deviations worth recording: (a) the variance report lives on the EXISTING `IIvStockCountService`
> (`IvStockCountService.Variance.cs`; the class is now `partial`) exactly as this plan required, and each
> of its members takes the MENU CODE so the screen has its own grant; (b) **Est. Inventory Value reuses
> `IIvStockSummaryService` under its own menu code** rather than repeating the grouped query — its ACCESS
> and `VIEW_PRICE` are its own grant, which is the literal D11 Option B isolation and is pinned by a test;
> (c) `Math.Abs` has no SQL translation on this provider, so the absolute-variance total is built from two
> one-sided sums; (d) `IIvInventoryReconciliationService` gained the ACCESS gate it never had and now
> resolves `ORPHAN_HISTORY` existence against the WHOLE branch — the old filtered-set resolution reported
> every cross-warehouse transfer source as orphaned history, a false positive removed here.
>
> Still owed (needs a human): browser smoke of the six inquiry pages; a role grant before anyone sees a
> value column. Item 9's performance acceptance test from Phase 1 is still outstanding.
>
> Phase 4 (items 18-19) remains deferred and is not started.
>
> Phase 2 delivered items 10-13: `IvStockAlerts` (`/inventory/stock-alerts`, `INV_STOCK_ALERTS`),
> `IvLotInquiry` (`/inventory/lots`, `INV_LOT_INQUIRY`), `IvStockSummary`
> (`/inventory/stock-summary`, `INV_STOCK_SUMMARY`), the two exports, three `init-inv-*-menu.sql`
> scripts and the `IvStockMasterList` Min/Max-stock columns. **0 schema change.**
>
> Deviations worth recording: (a) the alert and summary queries could not use the joined-aggregate or
> entity-reference shapes this plan implied — see "EF Core translation notes" in
> `ErpWeb/docs/inventory-inquiry-suite.md`; the compositions use correlated scalar subqueries and a flat
> summary slice; (b) the summary's sort whitelist is derived **per grouping mode** (a grouped query may
> only be ordered by its own keys/aggregates), so no shared `IvStockSummarySortFields` exists;
> (c) `EXPIRING`/`EXPIRED` return one row per lot **per warehouse**, and a lot with no pile in scope is
> not an alert; (d) `IncludeInactive`/`IncludeNonStockControl` default OFF for alerts, so the
> `StockControl = 1 AND IsActive = 1` guards are the default and a user can opt out of them.

Build the missing read-only inventory inquiries as ONE reusable "inquiry kit" (query DTO → single
repository composition → service → page → menu → optional export), reusing the shipped
`IvBalanceLot` feature as the proven template.

**Item count, stated exactly.** The previous revision said "15 includable items" while its own
consolidation table named 17, so the number could not be used as a completeness check. Corrected
accounting:

| Bucket | Count |
|---|---|
| Assessment examples considered (`§6` P1 9 + `§6` P2 10) | **19** |
| less excluded with evidence — Stock Availability, Negative Stock, Stock Aging | −3 |
| **In scope** | **16** |
| less already delivered, needing no new page (Stock Take — the document already exists) | −1 |
| **Deliverable assessment items** | **15** |
| plus alert rules added for completeness — Overstock, Dead Stock (`§5` category candidates that are **not** in the `§6` P1/P2 example lists) | +2 |
| **Items named in the consolidation table below** | **17** |

**17 items → 10 consolidation rows → 8 new pages, 1 modified existing page, 1 additive index, 2
OPTIONAL columns.** `IvBalanceLot` itself is **not** modified — it stays byte-stable, which is the
shipped plan's own acceptance criterion. The single modified page is `IvStockMasterList`.

Supersedes nothing. Companion doc: `ErpWeb/docs/inventory-balance-by-lot.md` (the reference
implementation of the kit).

---

## Scope decisions (locked)

**Excluded — not built** (with evidence, so they are not re-litigated):
- **Stock Availability** — no reservation/allocation model exists. `IvBalLoc` has no reserved
  column; `ErpWeb/docs/inventory-stock-lot.md` §11 states `ReservedQty`/`AvailableQty` are "not
  modelled yet". "Reserved" in this ERP is sales *document-capacity arithmetic*
  (`SaSoLineReserve`, CN reservations) and never touches stock. `Available == OnHand` ⇒ duplicate.
- **Negative Stock** — impossible: `CK_IvBalLoc_StdQty_NonNegative` + whole-batch abort in the
  posting engine. A report can never return a row. `ALLOW_NEGATIVE_STOCK` is declared in
  `AppSettingCatalogue` but consulted by no consumer.
- **Stock Aging for non-lot items** — needs receipt layers. `IvBalLoc.TransDate` is the *last*
  movement date and is mutated on every posting (can move backwards), and there are no cost layers.
  Phase 4 only. **Lot-level ageing IS delivered** (via `IvLot.ReceiptDate` on the Lot / Batch page).

**Downgraded — built, but not under the name the assessment used:**
- **Inventory Valuation** → **"Est. Inventory Value"** (informational). There is no costing method,
  no cost layer, no revaluation and no GL posting of stock value; `IvBalLoc.Cost` is written only by
  `ApplyDestCostFromSource` (transfer destination at qty 0) and is NULL almost everywhere. A true
  valuation is Phase 4.

**Consolidation (the core design decision).** One page per *user question*, never one page per
filter. Provenance: `P1`/`P2` = in the assessment's MUST/SHOULD examples; `ADD` = added for
completeness because it is a natural sibling rule.

| Assessment item | Source | Delivered by | New? |
|---|---|---|---|
| Stock Balance, Warehouse Stock | P1 | `/inventory/balance-by-lot` (unchanged) + Stock Summary | 1 new |
| Stock Card, Stock Movement | P1 | Stock Card page (2 modes) | new |
| Stock Transaction Inquiry, Adjustment Inquiry, Adjustment Analysis | P1 + P2 | Transaction Inquiry page (TrxType multi-select) | new |
| Low Stock, Slow Moving, Expiry | P2 | Stock Alerts page (Rule selector) | new |
| Overstock, Dead Stock | ADD | Stock Alerts page (Rule selector) | new |
| Lot/Batch Inquiry (+ lot ageing, expiry detail) | P2 | Lot / Batch Inquiry page | new |
| Warehouse comparison | P2 | Stock Summary page (group-by) | new |
| Stock Take Variance | P1 | Stock Count Variance page | new |
| Inventory Valuation (as estimate) | P1 | Est. Inventory Value page | new |
| Stock Reconciliation | P2 | Reconciliation page (diagnostic) | new |

---

## The inquiry kit — the shape every new page copies

Reference: `ErpWeb.UI/Inventory/Inquiry/IvBalanceLot.razor(.cs)(.css)`,
`ErpWeb.Core/Inventory/IIvBalanceLotService.cs`, `ErpWeb.Core/Inventory/IvBalanceLotService.cs`,
`ErpWeb.Model/Repositories/Inventory/IvBalanceLotResults.cs`,
`ErpWeb.Model/Repositories/Inventory/IvStockCommonRepository.cs` (`BuildBalanceLotQuery` + 4 thin
callers), `ErpWeb/Inventory/IvBalanceLotExportEndpoints.cs`, `scripts/init-inv-balance-lot-menu.sql`.

1. **Query/Row/Summary/sort-whitelist** in `ErpWeb.Model/Repositories/Inventory/<Feature>Results.cs`
   (the repository needs them, so they live in the Model layer — do NOT put them in `Core`).
   Every Row declares the four audit properties (`CreatedDate`, `CreatedBy`, `ModifiedDate`,
   `ModifiedBy`) because `DxGridDataColumn.FieldName` resolves by property name at render time.
2. **ONE private `Build<Feature>Query(AppDbContext, company, branch, query) → IQueryable<Slice>`**
   plus thin callers: `Search<Paged>Async`, `Count<Feature>Async`, `Summarise<Feature>Async`,
   `List<Feature>ForExportAsync`. No raw SQL, no second data-access stack.
3. **Service** in `ErpWeb.Core/Inventory/`: resolve `IInventoryTenantContext.TryBranchScope()`
   **first** (fail closed with `InvalidScope`), then `AccessRights.CanAsync(menu,
   PermissionCodes.Access)`. Return `IvMasterOperationResult<T>`. Inject `ICurrentDateService`.
   Register beside `IIvStockCountService` in `ErpWeb.Core/CoreServiceCollectionExtensions.cs` (~:149).
4. **Page** in `ErpWeb.UI/Inventory/Inquiry/`: `@inherits PageBase`, `<MenuAuthorize MenuCode>`,
   `iv-page`/`iv-hero`/`iv-card`/`iv-toolbar-row` chrome, `List<GridColumnData> Columns` ending in
   `..AuditColumns.For(n)`, a `GridCustomDataSource` subclass, a Draft/Applied filter popup,
   400 ms `Timer` debounce, `AccessRights.CanAsync(menu, PermissionCodes.Export)` for the toolbar.
5. **Export** (only where listed): `ErpWeb/Inventory/<Feature>ExportEndpoints.cs`, xlsx via
   `DocumentFormat.OpenXml`, `MaxExportRows = 50_000`, `PermissionCodes.Export` checked **inside**
   the handler, registered in `ErpWeb/Program.cs` (~:110).
6. **Menu**: a `MenuCodes` constant + a `menus.xml` row under `INV_INQUIRY` (guarded by
   `MenuDeploymentParityTests`) + `scripts/init-inv-<feature>-menu.sql` for the ACCESS/EXPORT grants.
   `INV_INQUIRY` parent already exists (SortOrder 3); Balance by Lot is SortOrder 1, so new children
   take SortOrder 2..N.
7. **Tests**: one class per feature, `[Trait(TestCategories.Name, TestCategories.Inventory)]` plus a
   NEW screen trait constant in `ErpWeb.Tests/TestCategories.cs`.

**The common export contract — every xlsx endpoint, no exceptions (D19):** the export uses the SAME
applied query object as the grid (never the grid's current page); it never pages; it resolves
company/branch server-side through the service and accepts **no** company/branch query parameter; it
re-checks `PermissionCodes.Export` inside the handler; it applies the same price-visibility masking
(the money column is omitted, not blanked, when the caller may not see price); it refuses with
`Results.BadRequest` naming both numbers when the match count exceeds `MaxExportRows = 50_000`
(never truncates); and its row count for a given query equals the grid's `TotalCount`. One endpoint
behaving differently from the others is a defect.

Non-negotiables (from the shipped plan): no new data-access stack; the summary aggregates in SQL
(`CountAsync`/`SumAsync` over the composed `IQueryable`) and never by materialising rows; date ranges
are half-open (`>= from && < to.AddDays(1)`) with no `.Date` on a database column; an unknown
`SortField` falls back to the default order and can never reach the expression tree; an empty status
selection means ALL statuses.

---

## Phase 1 — the real data gap (1 index, 0 **schema** columns) — blocks nothing

The genuinely missing capability: nothing today can show *posted movements* outside a per-document
header list. This phase fixes that and gives the Stock Card.

1. **New `ErpWeb.Model/Repositories/Inventory/IvStockHistoryRepository.cs`** (+
   `IIvStockHistoryRepository`), modelled on `IvStockTransactionRepository`: one private
   `BuildTrxHistoryQuery` over `db.IvTrxHistories.AsNoTracking()` (LEFT joins to `IvStockMasters`
   on `(CompanyCode, ICode)`), then `SearchTrxHistoryPagedAsync`, `CountTrxHistoryAsync`,
   `SummariseTrxHistoryAsync` (grouped opening/in/out/closing), `ListTrxHistoryForExportAsync`.
   Net per line = `ToStdQty - FrStdQty`; `FrWarehouse`/`ToWarehouse`, `FrLotNo`/`ToLotNo`, `IStatus`,
   `TrxType`, `RefNo`, `DoNo`/`InvNo`/`SoNo`/`PoNo`, `Cost`/`CostPrice`/`UnitPrice` are already
   denormalised on the row (verified in `IvTrxHistoryConfiguration`).
2. **`IvTrxHistoryResults.cs`** — `IvTrxHistoryQuery` (ICode, WhCode, LocCode, LotNo, `TrxTypes`,
   `BatchNo`, `RefNo`, `DocumentNo`, `IStatuses`, `TrxDateFrom/To`, sort/skip/take),
   `IvTrxHistoryRow` (line + item + direction + qty in/out + net + doc refs + value + the 4 audit
   props), `IvTrxHistorySummary`, `IvTrxHistorySortFields.Allowed`.
   - **No cancelled/status filter.** History rows are written with `BatchStatus = POSTED` only, and
     rollback DELETES that batch's history — so there is nothing cancelled to filter. `IStatuses`
     filters the **item/stock** status (`IvTrxHistory.IStatus`), not the batch status. Do not add a
     batch-status control that can never change the result.
3. **`IIvTrxHistoryService` / `IvTrxHistoryService`** in `ErpWeb.Core/Inventory/`; register in
   `CoreServiceCollectionExtensions.cs`.
4. **New page `ErpWeb.UI/Inventory/Inquiry/IvTrxInquiry.razor(.cs)(.css)`** at
   `/inventory/trx-inquiry`, menu `INV_TRX_INQUIRY`. The TrxType multi-select is the single control
   that turns this one page into *Transaction Inquiry* (all types) and *Adjustment Inquiry /
   Analysis* (`TrxType = 'ADJ'`). Filter controls: item picker (`IvStockMasterPicker`), warehouse
   (`IvCodeComboBox` over `ListActiveWarehousesAsync`), bin, lot, TrxType list box, status list box,
   date range, doc-no text, search text.
5. **Reason column decision (see D2).** V1 = parse `Remarks` through the existing single definition
   `IvStockAdjustmentLineInvariant.ParseStoredRemarks` and expose a computed `Reason` on the row —
   **no schema change**. Add a `Reason` column only if the report must filter/group by it server-side.
6. **New page `ErpWeb.UI/Inventory/Inquiry/IvStockCard.razor(.cs)(.css)`** at `/inventory/stock-card`,
   menu `INV_STOCK_CARD`, with two modes over the SAME query:
   - **Card** — chronological rows with a computed running balance. **The balance scope is
     load-bearing (D12): opening and running are computed over exactly the key the row filter uses,
     never over a wider scope.**
     - Scope key = the stock slice `(ICode, WhCode, LocCode, LotNo, IStatus)` within company+branch;
       reuse `IvStockSliceKey.Create(...)` as the single definition of that key.
     - `OpeningQty` = `SUM(ToStdQty - FrStdQty)` over history with `TrxDtTime < fromDate` **within the
       scope key**; `RunningQty = OpeningQty + cumulative net within the period`.
     - Filtering to a single pile (the default and the recommended workflow) gives exactly that
       pile's ledger. A multi-pile scope (e.g. item-only) makes the running column a **sum across
       the matching piles**, so its caption must read "Running (all bins/lots in scope)" — it is NOT
       any single physical pile's balance.
     - Columns: Date, Doc type, Batch no, Ref, Warehouse, Bin, Lot, Status, In, Out, Running,
       Unit price, Est. value.
   - **Movement** — period summary (Opening / In / Out / Adjust / Closing) via
     `SummariseTrxHistoryAsync`, same scope key.
   - **Value formula — locked, do not invent (D13):** `unitPrice = history.UnitPrice ??
     sm.PurchasePrice ?? 0m`; `rowValue = IvQty.Round(netQty × unitPrice)`. Show In/Out quantities
     and the **signed** net value so a card's values sum to the closing value. Do **not** use
     `IvTrxHistory.Cost` / `CostPrice` / `AsNowCost` — they are legacy and sparse, and there is no
     costing engine, so using them would invent a valuation. Money columns follow the price-
     visibility decision in D11 and carry the same "Est. — not a GL valuation" caption as the
     shipped page.
   - **MANDATORY caption:** the ledger closing and the live `IvBalLoc` qty are shown side by side and
     the difference is flagged. Opening is only sound when every stock entry was posted here; the
     reconciliation service already treats `OpeningQty = 0` as test-only. Legacy/imported on-hand
     with no matching history is the expected cause of any difference.
   - **Cancelled / rolled-back documents never appear**, because posting writes history only on
     POSTED batches and rollback deletes it. State this on the page so the absence is not read as a
     bug.
   - Drill-down target of the existing Balance by Lot page (out of V1 scope to wire; recorded so it
     is not bolted on later).
7. **Index (the only DB work in this phase).** New `scripts/alter-ivtrxhistory-inquiry-index.sql`
   adding `IX_IvTrxHistory_Inquiry (CompanyCode, BranchCode, TrxType, TrxDtTime)`. Today the only
   indexes are `IX_IvTrxHistory_ICode_TrxDtTime`, the batch/line unique index and the four FK
   indexes — so a **type-and-date-range** inquiry (the default view, unfiltered by item) scans. Add
   the matching `HasIndex` to
   `ErpWeb.Model/Configurations/Inventory/IvTrxHistoryConfiguration.cs` (index only, no column).
   Additive, idempotent, DBA-run.
   - **Do NOT add a further index speculatively (D20).** The Stock Card's dominant predicate is
     `ICode + TrxDtTime`, which `IX_IvTrxHistory_ICode_TrxDtTime` **already covers** — the remaining
     slice columns are a residual filter on a narrow seek, which is the expected shape. Prove it with
     the performance acceptance test in Verification instead: if the real execution plan on a
     representative `IvTrxHistory` volume shows a scan or a sort spill for the opening query, add an
     index then, designed from that plan — not before.
8. **Menus/scripts/tests/docs** for both pages. New traits
   `TestCategories.InventoryTrxInquiry` / `InventoryStockCard`.
   `scripts/init-inv-trx-inquiry-menu.sql` and `scripts/init-inv-stock-card-menu.sql` follow
   `init-inv-balance-lot-menu.sql` exactly (ensure parent by MenuCode, insert ACCESS + EXPORT with
   `NOT EXISTS`, re-activate, verify, `PRINT` the expected row count).
9. **xlsx export** for both pages (this is where "Stock Movement as a report" is fulfilled).

## Phase 2 — stock control & position (0 schema change)

10. **New page `IvStockAlerts.razor(.cs)(.css)`** at `/inventory/stock-alerts`, menu
    `INV_STOCK_ALERTS`, with a **Rule selector** producing one grid. **`AsOfDate` = the date part of
    `ICurrentDateService.Now`** (company-local), resolved once per query in the service and used by
    every rule — never a mix of server clock, SQL clock and application time (D14).
    - `LOW` — `sm.StockControl = true AND sm.IsActive = true AND sm.MinStock IS NOT NULL AND
      OnHand < sm.MinStock`
    - `OVER` — same guards, `sm.MaxStock IS NOT NULL AND OnHand > sm.MaxStock`
    - `SLOW` — `onHand > 0 AND lastMovement < AsOfDate - SlowDays` (SlowDays is a **filter
      parameter**, default 90 — do not hard-code)
    - `DEAD` — `onHand > 0 AND lastMovement < AsOfDate - DeadDays` (DeadDays is a filter parameter,
      default 180, and **must be > SlowDays**).
    - `NEVER_MOVED` — `onHand > 0 AND lastMovement IS NULL` (opening/legacy stock with no history at
      all) — that is NOT dead stock and must not be folded into it.
    - **Locked (D21): every movement/age rule requires `onHand > 0`.** An item with no stock anywhere
      is not an operational stock alert and must not appear on SLOW / DEAD / NEVER_MOVED. LOW/OVER
      are the deliberate opposite — a never-stocked item IS a low-stock alert.
    - `EXPIRING` — `lot.ExpiryDate >= AsOfDate AND lot.ExpiryDate <= AsOfDate + ExpiryDays`
    - `EXPIRED` — `lot.ExpiryDate < AsOfDate`. **Already-expired stock is the control exception and
      must not be dropped.** The boundary is identical to the shipped page: a lot expiring **today is
      not expired**, computed from a C# date parameter with no `.Date` on the column.
    - **`LOW`/`OVER` are driven by `IvStockMasters`, not by `IvBalLoc`** — LEFT JOIN the aggregated
      on-hand. An item that has never had a pile must still alert; a balance-driven query cannot see
      it. `SLOW`/`DEAD`/`NEVER_MOVED` are item master + `IvTrxHistory` aggregate; `EXPIRING`/
      `EXPIRED` are driven by `IvLot` (non-lot items have no `IvLot` row and are explicitly out).
    - `lastMovement` = `MAX(IvTrxHistory.TrxDtTime)` per item — **never `IvBalLoc.TransDate`**, which
      is mutable and can move backwards on a back-dated post.
    - `OnHand` = `SUM(IvBalLoc.StdQty)` for the item across the branch's warehouses (the D3 basis).
      `IncludeZeroQty` must be ON for these rules — a zero-on-hand item is the primary low-stock case.
    - **Result columns so the alert explains itself (D15):** Item, Description, Std UOM, Min Stock,
      Max Stock, On Hand, Variance (`OnHand − MinStock` for LOW; `OnHand − MaxStock` for OVER), Last
      Movement, Days Since Movement, Expiry Date, Days To Expiry, Threshold Basis ("all warehouses"
      or the selected warehouse).
    - `MinStock` / `MaxStock` = NULL **or 0** ⇒ not configured, **never alerts**. The `IS NOT NULL`
      guard is explicit so NULL cannot silently become 0; a 0 threshold can never fire because
      negative stock is impossible.
    - These are the FIRST consumers of `MinStock`/`MaxStock`, which are stored but used nowhere today.
11. **New page `IvLotInquiry.razor(.cs)(.css)`** at `/inventory/lots`, menu `INV_LOT_INQUIRY` —
    the lot passport. Header: `IvLot` origin (`SourceType`, `SourceDocNo`, `SupplierCode`,
    `ReceiptDate`, `MfgDate`, `ExpiryDate`, `QcStatus`) + age in days. Child: on-hand per pile
    (`IvBalLoc` by `LotId`, `IX_IvBalLoc_LotId` exists) and movement history by
    `FromLotId`/`ToLotId` (both indexed on `IvTrxHistory`). **Terminology:** "Batch" in this ERP is
    `IvTrxBatch`, a document — not a manufacturing batch. The **display name is "Lot / Batch
    Inquiry"** (so a user who searches for "batch" finds it) while the code artefact stays
    `IvLotInquiry`. The page carries a **subtitle "Lot / Batch Inquiry — Inventory Lot
    Traceability"** plus a one-line legend: *Lot = inventory traceability lot (`IvLot`); Batch =
    inventory document/batch number (`IvTrxBatch`)*. The subtitle is required because this page does
    **not** read `IvTrxBatch` as its primary entity — without it a user may expect a transaction-batch
    inquiry. `IvTrxBatch` itself is reachable from the Trx Inquiry page, not here.
    Delivers Lot/Batch Inquiry + Expiry detail + lot-level ageing (Method B, `IvLot.ReceiptDate`)
    in one page. Non-lot items are explicitly out (they have no `LotId`).
12. **New page `IvStockSummary.razor(.cs)(.css)`** at `/inventory/stock-summary`, menu
    `INV_STOCK_SUMMARY` — server-side `GROUP BY` on the balance slice with a Group-by selector:
    Item / Warehouse / Item×Warehouse / **Class**. Columns: group keys, total qty, **UOM**, pile
    count, zero-qty piles, est. value (gated by the price-visibility decision in D11). Delivers Stock
    Balance totals and Warehouse comparison without destabilising the shipped Balance by Lot page.
    - **`Class` = `IvStockMaster.IClassCode`** (column `IClass`), displayed with `IvClass.IDesc` from
      `IvClass` keyed `(CompanyCode, IClassCode)`. This is the codebase's own meaning of the word:
      `IvStockMasterList` already renders a "Class" column from
      `IvStockMasterListRow.IClassCode`, and both `ListStockCountCandidatesAsync` and
      `IvStockCountScope` filter on `sm.IClassCode`. It is **not** `IvClassification`,
      `IvStockMaster.Classification`, `IType` or `ISubClassCode` — do not substitute another field.
    - **UOM presentation (D16).** `StdQty` is each row's quantity in that item's **standard** UOM
      (`bal.StdUom ?? sm.StdUom`), so a quantity total is only meaningful when the group holds ONE
      UOM. The rule is therefore **per grouping mode**, not one caption for all:
      - **Item** and **Item×Warehouse** → render `UOM` + **"Total qty"**. Meaningful; no caveat.
      - **Warehouse** and **Class** → a grand total there adds PCS + KG + BOX, which is not a
        quantity. The default view shows **item count, pile count, zero-qty pile count and est.
        value**, and **hides the qty column entirely**. A deliberate opt-in ("show mixed-unit
        total") may reveal it, capped with the caption **"Total qty (mixed Std UOM)"** and the
        tooltip *"each row is in the item's standard UOM; the total adds different units"*.
        Default hidden, because a management figure like "Warehouse A = 15,382" misleads even with a
        tooltip.
      - The shipped Balance by Lot caption is left untouched.
13. Exports where they earn their keep: Alerts + Summary. Menus, `init-*-menu.sql`, traits, docs.

## Phase 3 — evidence & estimate (0 schema change; 2 optional columns)

14. **New page `IvStockCountVariance.razor(.cs)(.css)`** at `/inventory/stock-count-variance`, menu
    `INV_STOCK_COUNT_VAR` — variance over POSTED sheets. New service members on the EXISTING
    `IIvStockCountService` (`SearchVarianceAsync` + an export) rather than a parallel service; data
    comes from `IvStockCountHdr` + `IvStockCountLine`.
    - Variance = `SystemQty - PhysicalQty` (both stored). Value = `Variance × SnapshotUnitPrice`.
    - **Caveat that MUST be on the page:** `SystemQty` is Generate-time evidence, not the live qty at
      post (the post engine re-reads the live balance). So this is a **sheet variance** report, and
      the header's `PostedStaleLines` must be shown prominently as the staleness disclosure.
    - Filters: date range, warehouse, class, status. Export = xlsx (see the export contract in the
      inquiry kit).
    - **Accuracy formula — locked, do not invent (D17):** `ExactMatchLines` = lines where
      `PhysicalQty == SystemQty`; `CountedLines` = lines where `PhysicalQty IS NOT NULL`;
      **`LineAccuracy % = ExactMatchLines / CountedLines × 100`**, and when `CountedLines = 0` the
      value is **null (rendered "—"), never 0 and never 100**. Also report `NetVarianceQty`,
      `AbsVarianceQty` and `VarianceValue = Σ(Variance × SnapshotUnitPrice)`. Do **not** use a
      `1 − ABS(Variance)/ABS(SystemQty)` qty-ratio formula — `SystemQty = 0` is common in a count
      and it divides by zero.
    - No new column required — see D4 for the as-posted alternative.
15. **New page `IvStockValue.razor(.cs)(.css)`** at `/inventory/valuation`, menu `INV_STOCK_VALUE` —
    **Est. Inventory Value** by Item / Warehouse / Class. Reuse the shipped formula verbatim:
    `unitPrice = bal.UnitPrice ?? sm.PurchasePrice ?? 0m`,
    `rowValue = IvQty.Round(StdQty * unitPrice)`, `totalValue = IvQty.Round(SUM(StdQty * unitPrice))`
    aggregated in SQL. Gated by the price-visibility decision in **D11** — note that reusing
    `ICurrentUserService.CanViewPrice` on a page other than Balance by Lot needs explicit owner
    sign-off, because the shipped plan recorded it as a scoped V1 (its SC6/D4: "later adoption by any
    other screen is a separate, separately-approved change"); Option B in D11 avoids that entirely.
    When the caller may not see price, omit the money columns entirely (not blanked). **The caption
    must read "Estimate — not a GL valuation"** and the page must carry the price-vintage caveat
    (each pile keeps the price it was received at; no revaluation). Must NOT be titled "Inventory
    Valuation".
16. **New page `IvReconciliation.razor(.cs)(.css)`** at `/inventory/reconciliation`, menu
    `INV_RECONCILIATION` — thin UI over the EXISTING `IIvInventoryReconciliationService`
    (already registered at `CoreServiceCollectionExtensions.cs:157`; today it has no page, no menu,
    no route). Optional item/warehouse filters, findings grid (`Code`, `Message`, `Slice`,
    `BalLocQty`, `HistoryNetQty`), ACCESS only, **no export** (findings are diagnostics, not a
    business deliverable — exporting them invites treating them as an audit report), and the
    service's own "diagnostic only — needs an opening-balance baseline" caption preserved verbatim.
    - **Both sides must be aggregated at the same slice before comparison (D18):** reuse
      `IvStockSliceKey.Create(CompanyCode, BranchCode, ICode, WhCode, LocCode, LotNo, IStatus)` —
      already the key the service's `DUPLICATE_SLICE` check uses — for the `IvBalLoc` side **and** for
      the `IvTrxHistory` side (net `ToStdQty − FrStdQty` grouped by the slice the history row points
      at via `FromBalLocId`/`ToBalLocId`). Comparing item-level history against pile-level balances
      manufactures false discrepancies; that is a test requirement, not a preference.
17. Menus/scripts/traits/docs for all three.

## Phase 4 — deferred (each needs a written decision first, do NOT start)

18. Non-lot **Stock Aging** — requires a frozen `ReceivedDate` on `IvBalLoc` or a receipt/cost-layer
    table, plus the FIFO-ordering rework that depends on it.
19. True **Inventory Valuation** — requires a costing method (item/company), cost layers, and a
    valuation service consistent with postings. Until then Phase 3 step 15 is the ceiling.

---

## Relevant files

**Create** (per new page, following the Balance by Lot precedent):
- `ErpWeb.Model/Repositories/Inventory/IvStockHistoryRepository.cs` (+ `IIvStockHistoryRepository`)
  — owns every `IvTrxHistory` read (Trx Inquiry, Stock Card, the SLOW/DEAD movement aggregate)
- `ErpWeb.Model/Repositories/Inventory/IvStockInquiryRepository.cs` (+ interface) — owns the NEW
  on-hand-derived compositions (alerts, summary, lot inquiry, est. value)
- `ErpWeb.Model/Repositories/Inventory/IvTrxHistoryResults.cs`
- `ErpWeb.Core/Inventory/IIvTrxHistoryService.cs`, `IvTrxHistoryService.cs`
- `ErpWeb.Core/Inventory/IIvStockAlertService.cs`(+impl), `IIvLotInquiryService.cs`(+impl),
  `IIvStockSummaryService.cs`(+impl), `IIvStockValueService.cs`(+impl)
- `ErpWeb.UI/Inventory/Inquiry/IvTrxInquiry*`, `IvStockCard*`, `IvStockAlerts*`, `IvLotInquiry*`,
  `IvStockSummary*`, `IvStockCountVariance*`, `IvStockValue*`, `IvReconciliation*`
- `ErpWeb/Inventory/Iv{TrxInquiry,StockAlerts,StockSummary,StockCountVariance,StockValue}ExportEndpoints.cs`
- `scripts/init-inv-{trx-inquiry,stock-card,stock-alerts,lot-inquiry,stock-summary,stock-count-var,stock-value,reconciliation}-menu.sql`
- `scripts/alter-ivtrxhistory-inquiry-index.sql`
- `ErpWeb.Tests/Iv{TrxHistory,StockCard,StockAlert,LotInquiry,StockSummary,StockCountVariance,StockValue,Reconciliation}*Tests.cs`
- `ErpWeb/docs/inventory-inquiry-suite.md`

**Modify**:
- `ErpWeb.Core/Menus/MenuCodes.cs` — 8 constants in the inventory block (beside
  `InventoryBalanceLot`)
- `ErpWeb/Menus/menus.xml` — 8 children under the existing `INV_INQUIRY` (SortOrder 2..9)
- `ErpWeb.Core/CoreServiceCollectionExtensions.cs` — service registrations
- `ErpWeb/Program.cs` — export endpoint registrations (~:110)
- `ErpWeb.Model/Configurations/Inventory/IvTrxHistoryConfiguration.cs` — the new index
- `ErpWeb.Model/Repositories/Inventory/IvStockCommonRepository.cs` — **no change.** Listed as
  "read first" below, not here, and called out so it is not edited by reflex: its
  `BuildBalanceLotQuery` must stay byte-stable (D8)
- `ErpWeb.Core/Inventory/IIvStockCountService.cs` + `IvStockCountService.cs` — variance query
- `ErpWeb.Tests/TestCategories.cs` — 8 new screen trait constants
- `ErpWeb.UI/Inventory/Masters/IvStockMasterList.razor(.cs)` — expose `MinStock`/`MaxStock` columns
  (they are entered on the entry page but invisible on the list, which makes a Low Stock result
  impossible to explain). This needs the two properties **and** the repository projection added to
  `IvStockMasterListRow`; the grid resolves `FieldName` by property name, so adding only the
  `GridColumnData` throws at render

**Read first, do not modify**: `ErpWeb/docs/inventory-stock-lot.md`,
`ErpWeb/docs/inventory-balance-by-lot.md`, `ErpWeb.UI/Inventory/Inquiry/IvBalanceLot.razor(.cs)`,
`ErpWeb.Model/Repositories/Inventory/IvBalanceLotResults.cs`, `IvStockAdjustmentLineInvariant.cs`,
`IvInventoryReconciliationService.cs`, `IvTrxConstants.cs`, `IvSpShipmentAllocator.cs`.

---

## Verification

1. `dotnet build ErpWeb.slnx --nologo -v:q` → 0 `error CS`/`RZ`. (MSB3027/MSB3021 are the running
   `ErpWeb` app locking host-project DLLs; build `ErpWeb.Core`/`ErpWeb.UI`/`ErpWeb.Tests`
   individually. `ErpWeb.Tests` never compiles `.razor`, so the solution build is mandatory after
   every page.)
2. Build each layer as it lands: `dotnet build ErpWeb.Core`, then `ErpWeb.UI`, then `ErpWeb.Tests`.
3. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=Inventory&Category!=SqlServer"`
   → must match the documented baseline (currently 246/246 for Inventory-no-SQL). The pre-existing
   full-suite failures are 9 `SaCustServiceTests` + 4 `PoSupplierServiceTests` + 2 library
   `InvoiceTypeCode` — anything else is a regression.
4. `dotnet test … --filter "Category=Menus"` → proves every new `MenuCodes` constant has a
   `menus.xml` row (`MenuDeploymentParityTests`).
5. **Ledger reconciliation proof (Phase 1):** for one seeded item, sum
   `ToStdQty - FrStdQty` over `IvTrxHistory` and assert it equals `IvBalLoc.StdQty`. Any difference
   is the opening-balance caveat made visible — record it in the doc, do not hide it.
6. **Summary == predicate == export:** for one query, `summary.TotalRows == grid.TotalCount ==
   exported row count`, asserted at two page sizes so the aggregate provably does not depend on
   paging.
7. **Mandatory business fixtures** — each an asserted test, not a manual smoke:
   - **Stock Card** — no opening history; positive opening; mixed movement types (MR in, MI out, TR
     both legs, SC, ADJ); transfer-in and transfer-out on the same pile; multiple warehouses;
     multiple lots; a date boundary row dated exactly at `from` (in) and at `to + 1 day` (out); the
     opening computed over a single-pile scope vs an item-only multi-pile scope; and a rolled-back
     batch leaving nothing behind.
   - **Alerts** — `MinStock` NULL; `MaxStock` NULL; zero threshold; exactly equal to the threshold;
     one unit below and one above; **an item with NO `IvBalLoc` row at all must still alert on LOW**
     (the item-master-driven requirement); zero on-hand; no movement ever (`NEVER_MOVED`); old
     movement at both the SLOW and DEAD boundaries, with `DeadDays > SlowDays` enforced; an expired
     lot; expiry exactly today (**not** expired); expiry exactly `ExpiryDays` away (in).
   - **Stock Summary** — mixed UOM inside one group (the caption and UOM column); zero-quantity
     piles; the same item in two warehouses; the same item in two lots; Class grouping resolves
     `IClassCode`.
   - **Stock Count Variance** — `SystemQty = 0` with `PhysicalQty > 0`; `PhysicalQty = 0` with
     `SystemQty > 0`; both zero; an uncounted (NULL physical) line excluded from the denominator;
     a stale line; `CountedLines = 0` ⇒ accuracy renders "—" and is not 0 or 100.
   - **Reconciliation** — a duplicate slice; an orphan history row; and **no false positive when
     history and balance agree**, which is the aggregation-key test from D18.
   - **Security** — wrong company; wrong branch; ACCESS denied; EXPORT denied; price permission
     denied; and company/branch absent from the query string entirely.
8. Apply every new `scripts/*.sql` twice on a scratch DB and twice on dev `ERPWeb`; the second run
   must be a clean no-op.
9. Manual smoke: Stock Card opening + running balance against a hand-computed fixture; Alerts LOW
   row matches the item's `MinStock`; Reconciliation findings render and stay diagnostic-captioned.
10. **Performance acceptance (D20).** Seed a representative `IvTrxHistory` volume, then capture the
    execution plan and elapsed time for the two worst paths: (a) the Stock Card opening query
    (`ICode` + `TrxDtTime < fromDate` + slice residual) and (b) a Trx Inquiry over a date range with
    no item filter. Agree an operational threshold **before** measuring, then assert against it. If
    either plan shows a scan or a sort spill, add an index designed from **that** plan and record it
    here — never pre-emptively.

---

## Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | One page per user question, not per filter — 17 items → 8 new pages, 1 modified existing page (`IvStockMasterList`), 0 changes to `IvBalanceLot` | Avoids five near-identical on-hand grids; matches how `IvBalanceLot` already collapsed Balance + Warehouse + Lot + Expiry filters into one page |
| D2 | Adjustment **Reason** = parse `Remarks` via `IvStockAdjustmentLineInvariant.ParseStoredRemarks` in V1; a dedicated `ReasonCode` column only if server-side filter/group is required | Zero schema change; the parser is already the single definition. If chosen, the column needs `IvTrxBatchDetail` + `IvTrxHistory` + an add-only script + a write at the ADJ save path |
| D3 | `MinStock`/`MaxStock` compare against **total on-hand across the branch's warehouses** in V1 | They are item-level columns; per-warehouse minimums would need a new table. Basis stated on the caption |
| D4 | Stock Count Variance is **sheet variance** (`SystemQty - PhysicalQty`), no new column; `PostedStaleLines` disclosed on the page | Both values are already stored. Storing the as-posted variance is an additive `IvStockCountLine` column — offered, not required |
| D5 | "Inventory Valuation" ships as **"Est. Inventory Value"**, reusing the shipped formula and `CanViewPrice` gate | There is no costing method or cost layer; calling it a valuation would be false |
| D6 | Stock Reconciliation is a **diagnostic** page, ACCESS only, no export, caption preserved | The service self-describes as diagnostic-only until an opening-balance baseline exists |
| D7 | Stock Availability and Negative Stock are **not built** | No reservation model; the non-negative CHECK makes a negative-stock report always empty |
| D8 | Two new feature-scoped repositories — `IvStockHistoryRepository` (all `IvTrxHistory` reads) and `IvStockInquiryRepository` (the new on-hand-derived compositions). `IvStockCommonRepository` is **not modified at all** and `BuildBalanceLotQuery` stays byte-stable | It is already ~1,900 lines. Growing it further is the failure mode; a new file per data domain is the guardrail, and it also removes any merge risk against the shipped feature |
| D9 | Every new page is ACCESS-gated in the service and `<MenuAuthorize>`-guarded on the page; EXPORT only in the endpoint | Server-side enforcement is the boundary; hiding a button is not |
| D10 | Half-open date ranges, SQL-side aggregation, whitelist sort with fallback, empty status = all | Carried over verbatim from the shipped plan so the suite behaves as one product |
| D11 | **Price visibility on the new money pages — NEEDS OWNER SIGN-OFF, blocking.** Option A (recommended): extend the existing `userlogin.CanViewPrice` claim to Stock Card, Stock Summary, Est. Inventory Value and their exports. Option B (zero new mechanism): gate those pages with the menu-based `VIEW_COST` / `VIEW_PRICE` permissions and leave `CanViewPrice` wired to Balance by Lot only | The shipped plan recorded `CanViewPrice` as a **scoped V1 for one page** — "later adoption by any other screen is a separate, separately-approved change" (its SC6/D4). This plan must not silently extend it. Option B keeps that decision intact and needs no cross-cutting change; Option A is more consistent for the user |
| D12 | Stock Card opening/running balance is computed over the **same scope key as the row filter** — the stock slice `(ICode, WhCode, LocCode, LotNo, IStatus)` via `IvStockSliceKey.Create`, with `TrxDtTime < fromDate` for the opening | A global opening filtered by warehouse produces a nonsense running balance. Multi-pile scopes are allowed but must be captioned "all bins/lots in scope" |
| D13 | Stock Card value = `IvQty.Round(netQty × (history.UnitPrice ?? sm.PurchasePrice ?? 0m))`, signed, carrying the "Est. — not a GL valuation" caption and the D11 gate. `Cost` / `CostPrice` / `AsNowCost` are **not** a valuation basis | Reuses the shipped price rule so the card and the balance page agree; `Cost` is sparse and there is no costing engine, so using it would invent a valuation |
| D14 | Stock Alerts `AsOfDate` = the date part of `ICurrentDateService.Now`, resolved once in the service and used by every rule | Makes SLOW / DEAD / NEVER_MOVED / EXPIRING / EXPIRED deterministic and testable, and stops server, SQL and application clocks being mixed |
| D15 | Every alert rule returns its threshold and its basis: Min Stock, Max Stock, On Hand, Variance, Last Movement, Days Since Movement, Expiry, Days To Expiry, Std UOM, Threshold Basis | An alert a user cannot explain is not actionable |
| D16 | UOM presentation is **per grouping mode**: Item and Item×Warehouse show `UOM` + "Total qty"; Warehouse and Class **hide qty by default** (item count, pile count, zero-qty piles, est. value) with a deliberate opt-in showing "Total qty (mixed Std UOM)"; the shipped Balance by Lot caption is untouched. **Accepts the review's §17 concern in substance, not its literal wording** | Each row genuinely IS in the item's standard UOM, so "Standard UOM" alone would imply a single unit — but the real driver for hiding the Warehouse/Class total is that a mixed-unit grand total is not a quantity a manager can act on |
| D17 | Stock Count accuracy = `ExactMatchLines / CountedLines × 100`, with `CountedLines = 0` ⇒ null ("—"). A `1 − ABS(Variance)/ABS(SystemQty)` ratio is forbidden | `SystemQty = 0` is common in a count, so the ratio formula divides by zero; line accuracy has no such failure mode |
| D18 | Reconciliation compares both sides aggregated on `IvStockSliceKey.Create(...)` — the same 7-part key the service's `DUPLICATE_SLICE` check already uses | Comparing item-level history against pile-level balances manufactures false discrepancies |
| D19 | One common export contract for every xlsx endpoint: same query as the grid, no paging, server-side tenant, EXPORT re-checked inside the handler, price masking, 50 000 refusal, row count == `TotalCount` | Stops one agent-written export behaving differently from another |
| D20 | **No speculative indexing.** Ship the one planned `IX_IvTrxHistory_Inquiry`; prove the Stock Card opening path with a performance acceptance test and add a further index **only** from a real execution plan | The Stock Card's `ICode + TrxDtTime` seek is already covered by `IX_IvTrxHistory_ICode_TrxDtTime`; a second index now would be a guess |
| D21 | SLOW / DEAD / NEVER_MOVED all require `onHand > 0`; an item with no stock anywhere is not an operational stock alert | Settles the previously open question. LOW/OVER are deliberately the opposite — a never-stocked item IS a low-stock alert |
| D22 | Phase headings say "1 index, 0 **schema** columns" | "0 columns" alone reads as "no data-model change", which is false: Phase 1 adds a repository, DTOs, a service, two pages and an export |

---

## Further considerations

1. **Phase 1 export scope.** Option A: export on both Trx Inquiry and Stock Card (recommended — the
   "Stock Movement report" requirement is only met with an export). Option B: Trx Inquiry only.
   Option C: no export in Phase 1, add later.
2. **Balance by Lot drill-down.** Option A: leave it (recommended for V1 — a separate, approved
   change). Option B: wire Balance by Lot → Stock Card as part of Phase 1, at the cost of touching a
   shipped page.
3. **`MinStock` per warehouse.** Option A: item-level basis with a warehouse filter (recommended,
   D3). Option B: a new per-warehouse min/max table (real schema work, needs its own plan).
4. **Phase order.** Option A: 1 → 2 → 3 as written (recommended: Phase 1 closes the actual gap and
   is the dependency for Stock Movement, Adjustment Analysis and Slow Moving). Option B: lead with
   Phase 2 Alerts if the immediate business pain is reordering. Option C: lead with Phase 3
   Reconciliation if a data-quality audit is the driver.
5. **Price visibility mechanism (D11) — blocking, must be decided before Phase 1 ships money.**
   Option A: extend `CanViewPrice` to the new money pages (needs explicit owner approval, because
   the shipped plan scoped it to one page). Option B: use `VIEW_COST` / `VIEW_PRICE` on the new pages
   and leave `CanViewPrice` alone (no cross-cutting change, no approval needed).
6. **Alert threshold defaults.** Option A: `SlowDays = 90`, `DeadDays = 180`, `ExpiryDays = 30`, all
   user-editable filter parameters (recommended). Option B: no defaults — require the user to enter
   them each time. Option C: read them from the settings registry (`AppSettingCatalogue` already has
   an Inventory module, though every Inventory key there ships inert).
7. **`NEVER_MOVED` as its own rule.** Option A: a separate rule (recommended — it is truthful and
   keeps DEAD honest). Option B: fold `lastMovement IS NULL` into DEAD behind a checkbox.
8. ~~Should an item with no pile at all appear on SLOW / DEAD?~~ **CLOSED (D21)** — no. Every
   movement/age rule requires `onHand > 0`, and `NEVER_MOVED` additionally requires
   `lastMovement IS NULL`. No implementation choice remains.
9. ~~Stock Summary Warehouse/Class quantity.~~ **CLOSED (D16)** — hidden by default, opt-in with the
   mixed-unit caption. Revisit only if a real user asks for the number.

---

## Implementation-ready gate

Do not start coding until every line below is true. This gates the PLAN; the code's own checklist is
the Verification section.

- [ ] Count table above is authoritative: 17 named / 16 in scope / 15 deliverable / +2 added
- [ ] 8 new pages + 1 modified existing page confirmed, and `IvBalanceLot` confirmed unmodified
- [ ] Dead Stock redefined (`lastMovement < AsOfDate − DeadDays`) with `NEVER_MOVED` split out
- [ ] `EXPIRED` and `EXPIRING` both present, with "expires today is not expired"
- [ ] Alert `AsOfDate` = the date part of `ICurrentDateService.Now`, used by every rule
- [ ] Low/Over operators and NULL/0 threshold behaviour locked (`<` and `>`, explicit `IS NOT NULL`)
- [ ] LOW/OVER confirmed item-master-driven, so a never-stocked item still alerts
- [ ] Stock Card opening/running scope key locked to `IvStockSliceKey.Create` + the D12 caption rule
- [ ] Stock Card value formula locked (D13), with `Cost`/`CostPrice`/`AsNowCost` excluded as a basis
- [ ] Stock Count accuracy formula and zero-denominator rule locked (D17)
- [ ] Reconciliation aggregation key locked to the 7-part slice (D18), incl. the no-false-positive test
- [ ] "Class" locked to `IvStockMaster.IClassCode` / `IvClass.IDesc`
- [ ] Export contract locked (D19) and applied to every endpoint
- [ ] UOM presentation locked (D16) — Item/Item×Warehouse show qty; Warehouse/Class hide it by default
- [ ] SLOW / DEAD / NEVER_MOVED all require `onHand > 0` (D21), with `NEVER_MOVED` split out
- [ ] No speculative index; the performance acceptance test is defined (D20)
- [ ] **D11 price-visibility mechanism decided by the owner — record `D11 = OPTION A` or `D11 = OPTION B` in this file, then remove the other option from the implementation path**
- [ ] Security rules retained (server-side tenant, service ACCESS, endpoint EXPORT, price masking)
- [ ] Repository guardrail retained: `IvStockCommonRepository` unchanged
- [ ] Phase 4 remains deferred and is not started
