# Plan: Inventory Balance by Lot Inquiry

Build a read-only, ACCESS-gated, pile-level on-hand inquiry over `dbo.IvBalLoc` as an **EF Core composition** inside the existing inventory repository — not as a raw-SQL query builder — with the money column gated by a **new per-user `userlogin.CanViewPrice` flag**, and an xlsx export following the shipped inventory precedent.

This plan supersedes `plans/ballance-lot-view.md`. That document's discipline is kept (hard Phase 0 gate, one shared filter definition, explicit "do not invent ERP behaviour"), but several of its foundations do not match this codebase and have been replaced: see **§1 Corrections to the source plan**.

**Revision 2 (2026-09-24)** — review pass applied. Blocking/high changes: the inclusion-toggle polarity in the Phase C predicate is written unambiguously (the previous shorthand read as inverted); export is explicitly bound to the **same** query composition as the grid with tenant scope resolved server-side; the `ModifiedBy` mapping is pinned to `null` because no such column exists; the orphan-pile join is **decided** (LEFT, D15); the exact value formula, its rounding unit and its caveat are specified; date-range boundaries are half-open with no `.Date` on a database column; and §3.3 + §9 add the security requirements and acceptance criteria the plan was missing. The five original owner decisions (D4, D5, D6, D7 and the §2 scope) are unchanged.

**Revision 2.1 (2026-09-24)** — the same review was re-submitted. Every finding was already applied in Revision 2 (see the mapping in the conversation record); no finding was re-opened. Three residual wordings were closed while re-checking: the company/branch parameters are now named as coming from `IInventoryTenantContext.TryBranchScope()` (Phase C step 18), the test list gained the ACCESS-without-EXPORT and EXPORT-without-`CanViewPrice` denial cases (step 33), and the review's question about `TotalValue` mixing UOMs is answered explicitly — money *is* summable across UOMs, only quantity carries the caveat, and the total still mixes price vintages so it must not be called "inventory value" (§4.4, D16).

**Revision 2.2 (2026-09-24)** — the same review was submitted a third time, byte-identical. Re-verified against the file: nothing was re-opened and no behaviour changed. One consistency gap closed: the review's "LotNo source must be explicitly stated" was present as guidance in step 16 but had no entry in the decisions register, unlike every other item that required a choice, so it is now **D21**. The document is stable — further identical submissions need no edits; see the finding-to-location mapping in the conversation record.

---

## TL;DR

1. `IvBalLoc` queries already exist in `IvStockCommonRepository` (`SearchOnHandPagedAsync`, `GetOnHandByIdAsync`, `ListStockCountCandidatesAsync`). Extend that pattern with **one private `IQueryable` composition** shared by grid, count and summary. No raw SQL, no CTE discussion, no second data-access stack.
2. The source plan's `Cost × StdQty` valuation is invented. `IvBalLoc.Cost` is written in exactly one code path (transfer destinations with qty 0) and is NULL almost everywhere. Value the pile as `IvQty.Round(StdQty × (bal.UnitPrice ?? sm.PurchasePrice ?? 0m))` — the shipped Stock Count / Stock Adjustment price rule, with the rounding moved from the unit price onto the product (§4.4).
3. Money visibility is a **new `userlogin.CanViewPrice` bit**, surfaced as a claim and as `ICurrentUserService.CanViewPrice`. This is a *second* price-visibility mechanism alongside `VIEW_COST`/`VIEW_PRICE` and must be documented as a deliberate, scoped deviation.
4. Menu wiring needs three artefacts, not one: a `MenuCodes` constant, a `menus.xml` row (guarded by `MenuDeploymentParityTests`), and `scripts/init-inv-balance-lot-menu.sql`. **`INV_INQUIRY` does not exist** and must be added as a new parent.
5. Export is **xlsx via `DocumentFormat.OpenXml`** in a minimal-API endpoint with a 50 000-row cap — not the source plan's streaming CSV with a bespoke exception type.
6. Grid, count, summary **and export** are four callers of ONE `IvBalanceLotQuery` + ONE repository composition. Only paging differs. The export endpoint resolves company/branch from `IInventoryTenantContext` server-side and never accepts them as query parameters.

---

## 1. Corrections to the source plan

| # | Source plan says | Verified reality | Resolution |
|---|---|---|---|
| 1 | Build `IvBalanceLotQueryBuilder` emitting raw SQL, executed with `IDbConnection.QueryAsync` | The solution is 100% EF Core via `IDbContextFactory<AppDbContext>`. There is no Dapper, no `IDbConnection` in `ErpWeb.Core`. Raw SQL appears only as `FromSqlInterpolated` for lock hints, guarded by `db.Database.IsSqlServer()` | One private `BuildQuery(...) → IQueryable` in `IvStockCommonRepository`, composed by grid/count/summary |
| 2 | "A CTE exists only for the single statement immediately following it, so grid and summary cannot share one" | True of SQL, irrelevant to EF: an `IQueryable` composition is naturally shared and translated per statement | Drop the whole `BuildGridSql`/`BuildCountSql`/`BuildSummarySql` trio, and the "ORDER BY must use output column names, not table aliases" rule with it |
| 3 | `Existing inquiry patterns: PriceInquiry.razor` | `PriceInquiry` is a *single-record explanation* page (`ComponentBase`, no grid, no paging) — not a list-page template | Use `ErpWeb.UI/Inventory/Masters/IvStockMasterList.razor(.cs)` as the template |
| 4 | `IvBalanceLotViewRow.cs` in `ErpWeb.Model/Entities/Inventory/` | That folder holds EF entities only. Query/row/result DTOs live in `ErpWeb.Core/Inventory/*Results.cs` | `ErpWeb.Core/Inventory/IvBalanceLotResults.cs` |
| 5 | `CostValue = COALESCE(bl.Cost,0) * bl.StdQty` | `IvBalLoc.Cost` is set in exactly one place — `ApplyDestCostFromSource` (`IvInventoryPostingService.cs:2514`, called at `:2023`) — and only when the destination pile's `StdQty == 0`. `FindOrCreateBalLocAsync` never sets it. **No valuation exists anywhere in the ERP** (`grep StockValue\|InventoryValue\|Valuation\|TotalValue` over `ErpWeb.Core` = 0 relevant hits) | `Value = IvQty.Round(StdQty × (bal.UnitPrice ?? sm.PurchasePrice ?? 0m))`, gated by `CanViewPrice`, labelled "Est. value — not a GL valuation". Exact definition in §4.4 |
| 6 | `IBusinessDateProvider` (new) | `ICurrentDateService.Now` already returns company-local time with an `Asia/Kuala_Lumpur` fallback | Reuse `ICurrentDateService`; no new interface |
| 7 | Menu: add `MenuCodes` constant + `menus.xml` row, `ParentMenuCode="INV_INQUIRY"` | `INV_INQUIRY` does not exist. Inventory parents are `INV_MASTER` and `INV_TRANSACTIONS` only. `MenuSyncService` auto-creates **only the ACCESS grant** for a menu it inserts; ADD/EDIT/DELETE/EXPORT need a seed script | New `INV_INQUIRY` parent + page child in `menus.xml`, **plus** `scripts/init-inv-balance-lot-menu.sql` |
| 8 | Streaming CSV, `ExportLimitExceededException`, hand-rolled `EscapeCsv` | Inventory precedent is xlsx via `DocumentFormat.OpenXml` with `MaxExportRows = 50_000` and `Results.File`. A CSV precedent exists (`SaAnalysisExportEndpoints`, `text/csv`, RFC 4180 `Escape`, UTF-8 BOM) but is used by sales analysis only. Nothing streams | xlsx endpoint + 50 000 cap returning `BadRequest`; drop the exception type and the streaming code |
| 9 | `Available Qty` may be `StdQty` minus reservations | No reservation/allocation model exists. `ErpWeb/docs/inventory-stock-lot.md:330` states `ReservedQty`/`AvailableQty` are explicitly "not modelled yet" | `Available Qty` = `StdQty`; the concept is out of V1 |
| 10 | Test the "Qty < 0" boundary case | Impossible: `CK_IvBalLoc_StdQty_NonNegative CHECK (StdQty >= 0)` (`scripts/alter-iv-stock-integrity.sql:142`) plus a whole-batch abort in the posting engine | Becomes a schema assertion, not a UI behaviour |
| 11 | Trait/export/docs/`_Imports` not mentioned | Every test class needs `[Trait(TestCategories.Name, X)]`; no inventory-inquiry screen trait exists. Export endpoints must be registered in `Program.cs`. `ErpWeb.UI/Inventory/` already has a `_Imports.razor` | Covered in Phases A/F; add an `InventoryBalanceLot` trait constant |

---

## 2. Scope / user story

**User story.** As a warehouse manager, stock controller or inventory planner, I want to query current on-hand stock at the pile level (item × warehouse × bin × lot × status) with flexible filters and a downloadable spreadsheet, so that I stop writing ad-hoc SQL and emailing spreadsheets to answer "how much of lot X is in bin Y?".

**In scope (V1)**
- Read-only paged grid over `IvBalLoc` for the caller's company + branch.
- Filters: item code, warehouse, bin (`LocCode`), lot number, status, free-text search, quantity range, expiry-before, last-movement date range, and three explicit inclusion toggles (zero-qty / inactive item / non-stock-control item).
- A summary block (row count, total qty, estimated value, zero-qty row count, expired row count) computed from the **same** predicate as the grid.
- Server-side sort from a whitelist; server-side paging.
- xlsx export of the filtered set, capped at 50 000 rows.
- Estimated value column, visible only to a user whose `userlogin.CanViewPrice` is set.

**Out of scope (V1)** — state these explicitly so they are not re-litigated
- Historical movement analysis. `TransDate` is the *last posted movement's* date and is mutated on every posting, so it cannot support history.
- Stock ageing analysis (same reason).
- Available quantity / reservations (no model exists).
- Drill-down to `IvTrxHistory` (a separate inquiry; see §10).
- High-value highlighting (no business threshold is defined anywhere).
- SignalR live updates, saved filter presets, batch print, dashboard widgets.
- Mobile compact-card view; `IvStockMasterList`'s `CompactRows` preview remains available as prior art if it is ever wanted.

---

## 3. Requirements

### 3.1 Functional
- R1 — Only piles for the caller's company **and branch** are ever returned.
- R2 — Paging and sorting are server-side; page size default 50, clamped 1..100 by the data source.
- R3 — Sorting is restricted to a whitelist; an unknown `SortField` falls back to the default order (`ICode, WhCode, LocCode, LotNo, Id`) rather than being interpolated.
- R4 — Grid, row count and summary use one shared predicate. It must be impossible for the summary to describe a different set than the grid.
- R5 — `IncludeZeroQty`, `IncludeInactive`, `IncludeNonStockControl` each default **true** and are individually toggleable.
- R6 — Expiry "before" comparison uses the company-local business date; `ExpiryDate == today` is **not** expired.
- R7 — The estimated value column is present only when `ICurrentUserService.CanViewPrice` is true — on the grid **and** in the export. See §3.3; this is a security rule, not a display preference.
- R8 — Export refuses (with a clear message naming the matched count and the cap) rather than truncating.
- R9 — The page requires the menu's `ACCESS` permission; the export requires the menu's `EXPORT` permission. Both are enforced **server-side**.
- R10 — Grid, count, summary and export share one query object and one repository composition. Only paging differs. For a given query the exported row count must equal the grid's `TotalCount`.
- R11 — An empty or absent status selection means **all** statuses, never "none".
- R12 — Each inclusion toggle is polarity-safe: `true` (the default) applies no predicate for that category; `false` applies the corresponding predicate. See the exact table in Phase C step 18.
- R13 — Date-range boundaries are half-open and built from C# parameters: `TransDate >= from && TransDate < to.AddDays(1)`. No `.Date` is applied to a database column.

### 3.2 Non-functional
- N1 — No new data-access stack, no hand-written SQL, no new DDL for `IvBalLoc`.
- N2 — No change to `IvOnHandBalanceRow` or `IIvInventoryLookupService`; every existing inventory picker must be untouched.
- N3 — Reuse `ICurrentDateService`, `IInventoryTenantContext`, `IvMasterOperationResult<T>`, `IvQty.Round`, `AuditColumns`, `GridCustomDataSource`, `MenuAuthorize`.
- N4 — New SQL scripts are additive, idempotent and DBA-run; never executed at application startup.
- N5 — The summary aggregates in SQL (`CountAsync`/`SumAsync` over the composed `IQueryable`). Never `ToListAsync()` followed by in-memory LINQ — with `IncludeZeroQty = true` the filtered set can be the whole table.
- N6 — The list-row DTO declares the four audit properties because `AuditColumns.For()` resolves `FieldName` by property name at render time. `IvBalLoc` has no modified-by column, so the mapping is `ModifiedBy = null` — **never add a database column to satisfy the DTO** (§4.1).

### 3.3 Security requirements
- SC1 — `ACCESS` is checked in the service before any data is read; a denied caller receives `IvMasterErrorCode.AccessDenied`, not an empty grid.
- SC2 — `EXPORT` is checked **inside the export endpoint**. Hiding the toolbar button is not an authorization boundary.
- SC3 — Company and branch come only from `IInventoryTenantContext.TryBranchScope()` inside the service. The export endpoint calls that same service and must not accept `companyCode`/`branchCode` from the query string.
- SC4 — `CanViewPrice` is evaluated **server-side** from `ICurrentUserService`, and the value is `null` when it is false. The UI merely omits the column; it is never the enforcement point.
- SC5 — When `CanViewPrice` is false the export omits the value **column entirely** rather than emitting a blank column — a blank column still discloses that the field exists and where it sits.
- SC6 — `CanViewPrice` is a new **cross-cutting** capability. V1 wires it to this page only. Later adoption by any other screen is a separate, separately-approved change (see §5 D4).

---

## 4. Verified facts (Phase 0 is already answered — do not re-research)

Authoritative inventory rules: `ErpWeb/docs/inventory-stock-lot.md`. Read it in full before Phase A.

### 4.1 `IvBalLoc` schema
`ErpWeb.Model/Entities/Inventory/IvBalLoc.cs`, `ErpWeb.Model/Configurations/Inventory/IvBalLocConfiguration.cs`

- PK `Id` → column `ID`, identity. `RowVersion` is a SQL `rowversion`.
- Audit aliases: `CreatedDate`→`Created`, `CreatedBy`→`UserID`, `ModifiedDate`→`Updated`. **There is no `ModifiedBy` column — do not invent one.** The rules doc states it verbatim (`ErpWeb/docs/inventory-stock-lot.md:368`): "`IvBalLoc` has `ModifiedDate` but no `ModifiedBy` (legacy table had `Updated`, not `UpdatedUID`)."
- **Consequence for the list DTO (N6):** `AuditColumns.For()` needs all four audit properties to exist on the row type, because `DxGridDataColumn.FieldName` resolves by property name at render time. The DTO therefore declares `ModifiedBy`, and the mapping sets it to **`null`**. The grid renders a blank "Modified By" cell for every row — that is the correct, documented outcome. Do not add the column to the database and do not borrow `UpdatedUID` from another table.
- Unique business key `UQ_IvBalLoc_StockSlice` = `(CompanyCode, BranchCode, ICode, WhCode, LocCode, LotNo, IStatus)`.
- Additional indexes: `IX_IvBalLoc_ICode_WhCode` = `(CompanyCode, BranchCode, ICode, WhCode)`; `IX_IvBalLoc_LotId`.
- `RevNo` exists but is **not** part of the unique key.
- Widths: CompanyCode/BranchCode 5, ICode 30, `WhCode`→`WHCode` 20, LocCode 10, LotNo 50, IStatus 10, RefNo 50, `StdUom`→`StdUOM` 10, `PoNo`→`PO_No` 30, Remarks 250, all qty/money `decimal(18,4)`.

**`LocCode` vs `LocationCode` — both exist, different meanings.** Do not conflate:
- `LocCode` nvarchar(10) NOT NULL default `''` = the **stock bin**, part of the unique slice. Unused is `''`, never NULL.
- `LocationCode` nvarchar(10) NULL = "site/plant leftover" stamp. The rules doc says verbatim: "**Not** the stock bin. Optional. Do not use in posting slice lookups."
- Written once by `StampLocationIfMissing` (`IvStockPostingRepository.cs:808`) and never overwritten.

→ Display and filter on `LocCode`. Omit `LocationCode` from V1.

### 4.2 Join cardinality (all 1:0..1 — no COUNT/SUM double-count risk)
| Lookup | Key | Join |
|---|---|---|
| `IvStockMaster` | `(CompanyCode, ICode)` — **company-level**; its `BranchCode` is a leftover, not in the key | **LEFT** on `(CompanyCode, ICode)` — decided in D15. An orphan pile is still a pile, and the INNER alternative silently drops rows |
| `IvWarehouse` | `(CompanyCode, BranchCode, WarehouseCode)` | LEFT on `(CompanyCode, BranchCode, WhCode)` |
| `IvLot` | `Id` + unique `(CompanyCode, ICode, LotNo)` | LEFT on `LotId` (nullable surrogate) — **do not** join on ICode+LotNo, and **do not** filter `IvLot.IsActive` |
| `IvStatus` | `(CompanyCode, IStatus)` | LEFT on `(CompanyCode, IStatus)` |
| `IvLocation` | `(CompanyCode, BranchCode, WarehouseCode, LocCode)` | LEFT on all four parts; `LocCode = ''` has no row |

### 4.3 Lifecycle semantics
- **Zero-qty rows are never deleted and never deactivated.** `IncreaseBalLocQtyAsync`/`DecreaseBalLocQtyAsync` (`IvStockPostingRepository.cs:612`/`:565`) only UPDATE `StdQty`, `TransDate`, `Updated`. `grep IvBalLocs.Remove*` over `ErpWeb.Core` = zero hits. `IvLot` is never deleted either (traceability outlives on-hand). ⇒ `IncludeZeroQty` is meaningful, and `IvBalLoc` accumulates one row per slice ever touched.
- **Negative `StdQty` is impossible.** `CK_IvBalLoc_StdQty_NonNegative` + the engine fails the whole batch if any slice would go below zero.
- **`TransDate` = the date of the last posted movement, and it is mutable.** Overwritten on every increase/decrease with the posted batch's `TrxDtTime`, which may be a back-date — so it can move **backwards**. It is load-bearing for FIFO (`IvSpShipmentAllocator` orders `TransDate, LotNo, Id`; `IvSpFifoEligibility.MatchesCandidate` filters `rowTransDate.Value.Date <= docDate.Date`). Label the column **"Last movement"**. The date-range filter means "last movement date within range", not "had a movement in range".
- **`IStatus` values are not just `IvItemStatuses`.** `IvItemStatuses` = `ACTIVE`/`DAMAGED`/`QCHOLD`, and `IvStockCountScope.ScrapStatus = "SCRAPS"` also exists. `IvStatus.StatusDesc` supplies the display text; the `IvStatus` master's `BranchCode`/`LocationCode` are nullable leftovers, not key parts.
- **Orphan piles are undetectable today.** `IvInventoryReconciliationService` reports `ORPHAN_HISTORY` (history pointing at a missing `IvBalLoc`), never the reverse, and both `SearchOnHandPagedAsync` and `ListStockCountCandidatesAsync` INNER join `IvStockMasters`. A pile whose item master is missing is therefore invisible to every existing screen. That is why this inquiry uses a LEFT join (D15) — it makes the anomaly visible instead of hiding it a third time.

### 4.4 Valuation — the exact definition

**There is no inventory valuation anywhere in this ERP** — no report, no service, no GL posting of stock value. Whatever this page shows is an *informational estimate* and is labelled as such on screen and in `docs/`.

**Inputs and their evidence**
- `IvBalLoc.Cost` is unusable (§1 row 5): written only by `ApplyDestCostFromSource`, and only into a zero-quantity transfer destination.
- `IvBalLoc.UnitPrice` is the price carried onto the pile from `IvTrxBatchDetail.UnitPrice`. Sparse, but the best pile-level figure available.
- `IvStockMaster.PurchasePrice` is the fallback.
- **Both prices are per `StdUom`.** Evidence: the two shipped call sites pair the fallback price with `StdQty`-scale quantities and never apply a pack factor — `IvStockCountService.cs:1153` pairs it with the count snapshot quantity stored beside `balance.StdUom`/`line.StdUom`, and `IvStockAdjustmentService.cs:826` pairs it with `FrStdQty`/`ToStdQty`.
- The displayed UOM is `bal.StdUom ?? sm.StdUom`, matching `SearchOnHandPagedAsync`.

**The formula — single source of truth**

```
unitPrice  = bal.UnitPrice ?? sm.PurchasePrice ?? 0m   // both columns are decimal(18,4) already
rowValue   = IvQty.Round(StdQty * unitPrice)           // IvQty.Round = 4 dp, MidpointRounding.AwayFromZero
totalValue = IvQty.Round(SUM(StdQty * unitPrice))      // aggregated in SQL, rounded once on the scalar
```

- **The rounding sits on the product, not the unit price.** The shipped precedent (`IvStockCountService.cs:1153`) rounds the unit price because it *stores a unit price on a document line*; here the requirement is a value, and since `UnitPrice`/`PurchasePrice` are already `decimal(18,4)`, rounding the unit price would be a no-op while rounding the product is the correct unit of precision.
- The per-row figure reconciles with the grid: `StdQty × (displayed unit price) == Value` to 4 dp.
- **Known and accepted caveat:** `totalValue` is the rounded sum of unrounded products, so it can differ from adding the displayed row values by a few `0.0001` when quantities are fractional. The expression above is authoritative; the test asserts the DB-side expression, not the sum of rendered grid rows.
- **Unlike quantity, value does *not* suffer a mixed-UOM problem.** `Value` is money, so summing it across `PCS`, `BOX` and `KG` produces a legitimate amount — only the quantity KPI needs the "(all UOM)" caveat (D16). What `TotalValue` legitimately *is not* is an accounting figure: each pile carries whatever price it was received at, with no revaluation, so the total mixes price vintages. Say that on screen, not "inventory value".
- Rounding helper: `IvQty.Round` = 4 dp `MidpointRounding.AwayFromZero` (`ErpWeb.Core/Inventory/IvTrxConstants.cs:31-36`). Qty and money are `decimal(18,4)`.
- Grid money display precedent: `DisplayFormat = "n4"` on `IvStockMasterList`.

**Where the value is computed**
The query retrieves `StdQty`, `UnitPrice` and `PurchasePrice`; the **service's mapping** computes `Value` after materialisation — one place, easy to review, and it keeps the `CanViewPrice` rule in the same block. No arithmetic is pushed into SQL for the row projection. The **summary is the exception**: it must aggregate in SQL (N5), so `TotalValue` uses the `SUM(StdQty * unitPrice)` expression and `IvQty.Round` is applied to the scalar result.

### 4.5 Existing query infrastructure to reuse
`ErpWeb.Model/Repositories/Inventory/IvStockCommonRepository.cs`
- `SearchOnHandPagedAsync` (`:443`) — INNER joins `IvStockMasters` on `(CompanyCode, ICode)`, LEFT joins `IvLots` on `LotId`, filters company + branch + `StdQty > 0m` + `sm.IsActive` + `sm.StockControl`, orders `ICode/WhCode/LocCode/LotNo/Id`, clamps `take` to 1..100, projects into `IvOnHandBalanceRow`.
- `GetOnHandByIdAsync` (`:520`) — the same shape by `Id`.
- `ListStockCountCandidatesAsync` (`:551`) — the closest existing filterable+paged balance grid; adds `IncludeZeroQty`, `IncludeInactive`, and a status list that excludes `SCRAPS` by default.
- Search semantics there are `Contains` on ICode/IDesc/WhCode/LocCode/LotNo.
- **`IvOnHandBalanceRow` is the shared projection of every inventory picker** (`IvBalLocSearchPopup`, `IvBalLocPicker`, `IIvInventoryLookupService.SearchOnHandAsync`/`MapOnHand`). Widening it changes every picker ⇒ create a new inquiry row DTO.

### 4.6 Business date, tenancy, permissions
- `ICurrentDateService.Now` (`ErpWeb.Core/Services/CurrentDateService.cs`) → company-local, `Asia/Kuala_Lumpur` fallback.
- `IInventoryTenantContext.TryBranchScope()` / `ITenantScopeContext`; `InventoryTenantContext` delegates to `TenantScopeContext`. `MaxCompanyLength = 5`; an over-long company code yields **no scope** (fail closed).
- `PermissionCodes.Access` = `"ACCESS"`, `PermissionCodes.Export` = `"EXPORT"`.

### 4.7 `userlogin` (the `CanViewPrice` host)
- `ErpWeb.Model/Entities/UserLogin.cs` — the **only** entity that uses data annotations (`[Table("userlogin")]`, `[Key]`, `[DatabaseGenerated]`, `[Required]`, `[MaxLength]`) **and** duplicates the mapping in `ErpWeb.Model/Configurations/UserLoginConfiguration.cs`. Any new column must be added in **both**.
- Existing flag precedent: `changepass bit NOT NULL DEFAULT (0)` → `[Required] public bool changepass`, claim `AppClaimTypes.ChangePassword`, exposed as `ICurrentUserService.MustChangePassword`.
- Claims are built **once at sign-in** by `AuthService.CreatePrincipal` (`ErpWeb.Core/Services/AuthService.cs:130-148`), called from `CookieSignInService.SignInAsync` (`ErpWeb/Authentication/CookieSignInService.cs:32`). Session lifetime 8h sliding; persistent 30 days.
- Admin CRUD: `ErpWeb.UI/Admin/AdminUser.razor(.cs)` binds `selectedRow` **as the `UserLogin` entity** (`DxCheckBox @bind-Checked="@selectedRow.active"` at `:69` is the pattern). `IUserAdminService.AddUserAsync(UserLogin, plainPassword)` / `UpdateUserAsync(UserLogin)` take the entity whole.
- `UserLoginRepository.UpdateProfileAsync` (`ErpWeb.Model/Repositories/UserLoginRepository.cs:227+`) copies a **fixed whitelist** of fields — a new column must be added there explicitly.
- DDL lives in `scripts/init-userlogin.sql` (the `CREATE TABLE`, note: it targets `ERPLiteEx`, not `ERPWeb`).

### 4.8 House UI/export/menu conventions
- **List page template** = `ErpWeb.UI/Inventory/Masters/IvStockMasterList.razor(.cs)`: `PageBase` + `OnPageInitializedAsync`, `List<GridColumnData> Columns` ending in `..AuditColumns.For(startVisibleIndex: n)`, a `GridCustomDataSource` subclass, `List<ButtonInfo> Buttons` toolbar, a Draft/Applied filter-popup pair, a 400 ms `Timer` debounce, and `AccessRights.CanAsync(menu, permission)`.
- **Every list-row DTO must declare** `CreatedDate`, `CreatedBy`, `ModifiedDate`, `ModifiedBy` — `DxGridDataColumn.FieldName` resolves by property name at render time, so a missing property throws.
- `AuditColumns.For(n)` is mandatory on every list page (currently 57 of them).
- Sort whitelist shape = `HashSet<string>` of `nameof(Row.Property)` named `Allowed` (`IvStockMasterSortFields`).
- Result envelope = `IvMasterOperationResult<T>` + `IvMasterErrorCode` (`ErpWeb.Core/Inventory/IvMasterResults.cs`).
- Page authorization = `<MenuAuthorize MenuCode="@MenuCodes.X">` (`ErpWeb.UI/Components/Security/MenuAuthorize.razor.cs`) **plus** a service-side `CanAsync` check.
- **Export** = `ErpWeb/Inventory/IvStockMasterExportEndpoints.cs`: xlsx via `DocumentFormat.OpenXml`, `MaxExportRows = 50_000`, the endpoint checks `PermissionCodes.Export` itself, `Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name)`, `.RequireAuthorization()`, registered in `Program.cs:109`. The page triggers it with `QueryHelpers.AddQueryString` + `Navigation.NavigateTo(url, forceLoad: true)`.
- **Menus** = `MenuCodes` constant + `ErpWeb/Menus/menus.xml` row (guarded by `ErpWeb.Tests/MenuDeploymentParityTests.cs`, which asserts every `MenuCodes` string constant exists in the shipped XML) + `scripts/init-<feature>-menu.sql` for non-ACCESS grants (precedent `scripts/init-inv-stock-count-menu.sql`).
- **Tests** = `ErpWeb.Tests/Iv*Tests.cs`, SQLite via `AppDbContext` + `EnsureCreatedAsync`, `[Trait(TestCategories.Name, TestCategories.X)]`. `ErpWeb.Tests` references `ErpWeb.UI` but still never compiles `.razor`, so `dotnet build ErpWeb.slnx` is mandatory after UI work.
- **Docs** = `ErpWeb/docs/*.md`.

---

## 5. Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | One private `BuildQuery(company, branch, query) → IQueryable` in `IvStockCommonRepository`, shared by grid / count / summary | EF composition; kills the CTE problem and the raw-SQL stack |
| D2 | A **new** `IvBalanceLotRow` DTO; `IvOnHandBalanceRow` is untouched | It is the projection of every inventory picker |
| D3 | Estimated value = `IvQty.Round(StdQty × (bal.UnitPrice ?? sm.PurchasePrice ?? 0m))`, computed in the service's mapping after materialisation | Follows the shipped Stock Count / Stock Adjustment price rule but rounds the **product** rather than the unit price (§4.4); `IvBalLoc.Cost` is NULL for most piles and no ERP valuation exists |
| D4 | Value gated by a **new `userlogin.CanViewPrice` bit**, published as a claim and as `ICurrentUserService.CanViewPrice` | **Owner decision, explicitly approved as a cross-cutting capability (SC6).** It is a *second* price-visibility mechanism: `VIEW_COST`/`VIEW_PRICE` stay menu-permission based. V1 wires it to this page only. The alternative (`VIEW_COST`, zero schema change) was considered and rejected by the owner. Record the decision in `docs/` |
| D5 | `IncludeZeroQty`, `IncludeInactive`, `IncludeNonStockControl` all default **true**, each an explicit toggle | Owner decision: show everything, let the user narrow |
| D6 | New `INV_INQUIRY` parent menu under `INVENTORY`, page as its first child | Owner decision; leaves room for future inquiry screens |
| D7 | xlsx export via `DocumentFormat.OpenXml`, 50 000-row cap | Owner decision; inventory house precedent |
| D8 | `TransDate` labelled "Last movement"; no ageing/history features | It is mutated on every posting and can move backwards |
| D9 | `Available Qty` = `StdQty`; no reservation concept in V1 | No reservation model exists in the ERP |
| D10 | Deliberate divergence from every existing on-hand picker: this page includes zero-qty rows and inactive / non-stock-control items by default | It is an inquiry, not a picker. Document it, or users will ask why this screen shows more than `IvBalLocPicker` |
| D11 | `SearchText` uses `Contains` on ICode/IDesc/WhCode/LocCode/LotNo | One search meaning across inventory screens |
| D12 | No new DDL for `IvBalLoc`; the feature ships only the menu seed and the `userlogin` column script | The table is legacy-migrated by `scripts/alter-iv-stock-integrity.sql` |
| D13 | `CanViewPrice` changes take effect only after the user signs out and back in | Claims are baked at sign-in. Document it and hint it on the admin screen |
| D14 | If `CanViewPrice` is false the service returns `null` for the value and the export omits the column entirely | Visibility only; the page is read-only so nothing can be erased (SC4, SC5) |
| D15 | **LEFT** join to `IvStockMaster`; an orphan pile is returned with blank item fields | The owner chose "show everything"; orphan piles are invisible to every existing screen (§4.3), so hiding them again would defeat the purpose of an inquiry. Consequence: when `IncludeInactive == false` or `IncludeNonStockControl == false`, orphans are excluded too, because `sm == null` fails both predicates — to see them, `IncludeInactive` must be `true` (the default) |
| D16 | The quantity KPI is captioned **"Total qty (all UOM)"**; the value KPI needs no such caveat | `PCS`, `BOX` and `KG` cannot be meaningfully added. Money can be: the unit of measure of `Value` is the currency, so `TotalValue` stays meaningful across mixed UOMs. The caveat is about quantity only (§4.4) |
| D17 | `Value` is computed in the service mapping, not in SQL | Keeps the arithmetic and the `CanViewPrice` masking in one reviewable place; the only SQL aggregation is the summary (§4.4) |
| D18 | Date ranges are half-open: `TransDate >= from && TransDate < to.AddDays(1)` | Inclusive-by-day semantics with no `.Date` applied to a column; `ExpiryBefore` uses the same idea via a C#-computed `businessDate.Date` parameter |
| D19 | An empty or absent status selection means **all** statuses | Matches every other filter's "unset means no restriction" semantics, and the UI convention that clearing a filter widens the result |
| D20 | `IvBalanceLotRow.ModifiedBy` is always `null` | `IvBalLoc` has no such column and the grid needs the property to render (§4.1, N6) |
| D21 | The displayed `LotNo` comes from **`IvBalLoc.LotNo`**, never `IvLot.LotNo` | `IvBalLoc.LotNo` is the stock-slice dimension the unique key is built on, so it is the pile's own truth; `IvLot` is joined for `ExpiryDate` only. The two must never be swapped (P6 step 16) |

---

## 6. Steps

### Phase A — `userlogin.CanViewPrice` workstream
Independent of the rest; blocks Phase D. *Files: `scripts/alter-userlogin-canviewprice.sql` (new), `scripts/init-userlogin.sql`, `ErpWeb.Model/Entities/UserLogin.cs`, `ErpWeb.Model/Configurations/UserLoginConfiguration.cs`, `ErpWeb.Core/Authentication/AppClaimTypes.cs`, `ErpWeb.Core/Services/AuthService.cs`, `ErpWeb.Core/Services/CurrentUserService.cs`, `ErpWeb.Model/Repositories/UserLoginRepository.cs`, `ErpWeb.UI/Admin/AdminUser.razor(.cs)`*

1. Write `scripts/alter-userlogin-canviewprice.sql`: guarded, idempotent `ALTER TABLE dbo.userlogin ADD CanViewPrice bit NOT NULL CONSTRAINT DF_userlogin_CanViewPrice DEFAULT (0)` for `OBJECT_ID(N'dbo.userlogin')`, with the house `SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON;` header and a `PRINT` verification. No dynamic SQL needed — the script never references the added column in the same batch.
2. Add the same column to the `CREATE TABLE dbo.userlogin` block in `scripts/init-userlogin.sql` (create-vs-alter drift rule).
3. `UserLogin` entity: `public bool CanViewPrice { get; set; }` beside `changepass`, plus `[Required]` if following that precedent.
4. `UserLoginConfiguration`: `builder.Property(e => e.CanViewPrice).IsRequired();` (mirrors `changepass`).
5. `AppClaimTypes`: `public const string CanViewPrice = "can_view_price";`.
6. `AuthService.CreatePrincipal`: add `new(AppClaimTypes.CanViewPrice, user.CanViewPrice ? "true" : "false")` next to `ChangePassword`.
7. `ICurrentUserService` + `CurrentUserService`: `bool CanViewPrice => string.Equals(Find(AppClaimTypes.CanViewPrice), "true", StringComparison.OrdinalIgnoreCase);` — copy `MustChangePassword` exactly.
8. `UserLoginRepository.UpdateProfileAsync`: add `existing.CanViewPrice = user.CanViewPrice;`.
9. `AdminUser.razor`: `DxCheckBox @bind-Checked="@selectedRow.CanViewPrice"` beside "User Active", plus a "Can View Price" grid column in `Columns()`. `AdminUser.razor.cs` needs no change beyond that.
10. Add a hint that the flag applies only after the user's next sign-in.

### Phase B — Phase 0 verification (gate for C and D)
11. Read `ErpWeb/docs/inventory-stock-lot.md` end to end.
12. **Confirm `scripts/alter-iv-stock-integrity.sql` is applied to dev `ERPWeb`**: `UQ_IvBalLoc_StockSlice`, `CK_IvBalLoc_StdQty_NonNegative`, `LotId`, `RowVersion` present on `dbo.IvBalLoc` (read-only probe over `sys.indexes` / `sys.check_constraints` / `sys.columns`, and always pass `-d ERPWeb`). Every assumption in §4 depends on it; if it is missing, STOP.
13. Measure `IvBalLoc` on dev and record the numbers: total rows; rows with `StdQty = 0`; rows with non-NULL `UnitPrice`; rows with non-NULL `Cost`; and the count of piles with no matching `IvStockMaster` (the D15 orphans). **These measurements are validation and observability only — they do not change an approved decision.** If a measurement looks unacceptable (for example `IncludeZeroQty = true` returning an unusably large default set), STOP and obtain an explicit owner decision before changing D5, D10 or D16. Do not silently re-tune the defaults.
14. Confirm no `create-ivballoc.sql` exists (the table is legacy) ⇒ no `IvBalLoc` DDL in this feature.
15. Record the D10 divergences and the D15 orphan rule in writing before coding.

### Phase C — Core
*Files: `ErpWeb.Core/Inventory/IvBalanceLotResults.cs` (new), `ErpWeb.Model/Repositories/Inventory/IvStockCommonRepository.cs`, `ErpWeb.Core/Inventory/IIvBalanceLotService.cs` + `IvBalanceLotService.cs` (new), `ErpWeb.Core/CoreServiceCollectionExtensions.cs`*

16. `IvBalanceLotResults.cs`:
    - `IvBalanceLotQuery` — `ICode`, `WhCode`, `LocCode`, `LotNo`, `IStatuses` (`IReadOnlyList<string>`; **empty means all**, R11), `SearchText`, `MinQty`, `MaxQty`, `ExpiryBefore`, `TransDateFrom`, `TransDateTo`, `IncludeZeroQty = true`, `IncludeInactive = true`, `IncludeNonStockControl = true`, `SortField`, `SortDescending`, `Skip`, `Take`.
    - `IvBalanceLotRow` — `Id`, `ICode`, `IDesc`, `WhCode`, `WhDesc`, `LocCode`, `LocDesc`, `LotNo`, `IStatus`, `IStatusDesc`, `StdQty`, `StdUom`, `LotId`, `ExpiryDate`, `TransDate`, `PoNo`, `RefNo`, `Remarks`, `UnitPrice`, `Value`, **plus the four audit properties** `CreatedDate`, `CreatedBy`, `ModifiedDate`, `ModifiedBy` (N6). The mapping sets `ModifiedBy = null` (D20).
    - **`LotNo` is projected from `IvBalLoc.LotNo`** — the stock-slice dimension — never from `IvLot.LotNo` (D21). `IvLot` supplies `ExpiryDate` only. Do not let the implementation swap these.
    - `IvBalanceLotSummary` — `TotalRows`, `TotalQty`, `TotalValue`, `ZeroQtyRowCount`, `ExpiredRowCount`.
    - `IvBalanceLotSortFields.Allowed` — `HashSet<string>` of `nameof(IvBalanceLotRow.*)`, restricted to the sortable set.
17. `IvStockCommonRepository`: add **one** `private static IQueryable<BalanceLotSlice> BuildBalanceLotQuery(AppDbContext db, string company, string branch, IvBalanceLotQuery query)` — the single composition of joins + predicate. Then add thin callers that all start from it: `SearchBalanceLotPagedAsync` (sort + `Skip`/`Take`), `CountBalanceLotAsync` (`CountAsync`), `SummariseBalanceLotAsync` (`CountAsync` + `SumAsync`), and `ListBalanceLotForExportAsync` (sort, no paging, capped). Joins: **LEFT** `IvStockMasters` on `(CompanyCode, ICode)` (D15); LEFT `IvLots` on `LotId` (never on ICode+LotNo); LEFT `IvStatuses` on `(CompanyCode, IStatus)`; LEFT `IvWarehouses` on `(CompanyCode, BranchCode, WhCode)`; LEFT `IvLocations` on `(CompanyCode, BranchCode, WhCode, LocCode)`. Because the item master is LEFT joined, every predicate that reads it must null-guard (`sm != null && …`).
18. Predicate — **implement exactly as specified; the toggle polarity is load-bearing.** Each `Include*` flag is an *include* switch: `true` means "do not restrict on this category".

    | Flag (default `true`) | When `true` | When `false` |
    |---|---|---|
    | `IncludeZeroQty` | no quantity predicate | `StdQty > 0` |
    | `IncludeInactive` | no activity predicate | `sm != null && sm.IsActive` |
    | `IncludeNonStockControl` | no control predicate | `sm != null && sm.StockControl` |

    Always applied:
    - Company and branch, taken from the parameters the **service derived from `IInventoryTenantContext.TryBranchScope()`** and never from a caller-supplied value: `bal.CompanyCode == company && bal.BranchCode == branch`.
    - `ICode`, `WhCode` and `LocCode` equality, each only when supplied and non-blank.
    - `LotNo` via `Contains` when supplied and non-blank.
    - **Status: when `IStatuses` is null or empty there is NO predicate (all statuses, including `SCRAPS`); otherwise `statuses.Contains(bal.IStatus)`** (R11, D19).
    - `SearchText` via `Contains` over `bal.ICode`, `sm.IDesc`, `bal.WhCode`, `bal.LocCode`, `bal.LotNo` (D11).
    - `MinQty`/`MaxQty`, inclusive, each only when supplied.
    - `ExpiryBefore`: `lot != null && lot.ExpiryDate != null && lot.ExpiryDate < expiryBound`, where `expiryBound` is a **C#-computed parameter** — `query.ExpiryBefore?.Date ?? businessDate.Date`. Do **not** call `.Date` on the column. This is what makes R6 true: a lot expiring today is not expired, because `today 00:00` is not `< today 00:00`.
    - `TransDateFrom`/`TransDateTo` (D18): `bal.TransDate >= from` and `bal.TransDate < to.AddDays(1)`, with `from`/`to` C#-computed date-only parameters — inclusive by day, no `.Date` on the column. The UI must say this is the *last movement* date (§4.3), not "had a movement in range".

    Sort: a `switch` over `IvBalanceLotSortFields.Allowed` producing a typed ordering, then `ThenBy(bal.Id)` as the tie-breaker. Apply `SortDescending` **only** after a valid whitelisted field has been selected (R3, U3 — an unknown field can never reach the expression tree). Default order: `ICode, WhCode, LocCode, LotNo, Id`.
19. `IIvBalanceLotService`/`IvBalanceLotService`: resolve `IInventoryTenantContext.TryBranchScope()` **first** (fail-closed `InvalidScope` message when null — SC3), then check `AccessRights.CanAsync(MenuCodes.InventoryBalanceLot, PermissionCodes.Access)` (SC1). Inject `ICurrentDateService` for the business date and `ICurrentUserService` for `CanViewPrice`. Return `IvMasterOperationResult<…>`. Compute `Value` in the mapping (`IvQty.Round(StdQty * unitPrice)`), then set it to `null` when `CanViewPrice` is false (SC4, D14, D17). The summary comes from `SummariseBalanceLotAsync` — **aggregated in SQL, never by materialising rows** (N5) — with `TotalValue` = `IvQty.Round` of the SQL `SUM` (§4.4). Expose the method the export endpoint needs so it reuses this service rather than re-querying (R10). Keep this service ACCESS-gated; do **not** extend `IIvInventoryLookupService`.
20. Register in `CoreServiceCollectionExtensions.cs` next to `IIvStockCountService` (`:149`).

### Phase D — UI (depends on A and C)
*Files: `ErpWeb.UI/Inventory/Inquiry/IvBalanceLot.razor(.cs)(.css)` (new)*

21. Clone `IvStockMasterList`'s structure: `@page "/inventory/balance-by-lot"`, `@inherits PageBase`, `<MenuAuthorize MenuCode="@MenuCodes.InventoryBalanceLot">`, `iv-page`/`iv-hero`/`iv-card`/`iv-toolbar-row` chrome, a hero with row-count and qty chips, and a filter popup with Draft/Applied pairs. Build the popup from these fixed controls so nothing is guessed at:

    | Filter | Control |
    |---|---|
    | Item | `IvStockMasterPicker` (existing; `ICode` / `ICodeChanged` / `Selected`) |
    | Warehouse | `IvCodeComboBox` over `ListActiveWarehousesAsync` |
    | Bin (`LocCode`) | `DxTextBox` |
    | Lot no. | `DxTextBox` |
    | Status | multi-select `DxListBox` over `IvStatus` descriptions; empty selection = all (R11) |
    | Search | `DxTextBox`, 400 ms debounce (D11) |
    | Min qty / Max qty | `DxSpinEdit` (`decimal`) |
    | Expiry before | `DxDateEdit` |
    | Last movement from / to | `DxDateEdit` ×2 |
    | Include zero qty / inactive / non-stock-control | `DxCheckBox` ×3, all default `true` (D5) |
22. `Columns` with the default visible set (Item, Description, Warehouse, Bin, Lot, Status, Qty, UOM, Expiry, Last movement, Est. value) plus hidden optional columns (PO no., Ref, Remarks, Lot id), ending with `..AuditColumns.For(...)`. Gate the Est. value column on `CanViewPrice`. The "Modified By" audit cell renders **blank for every row** — that is expected (D20), not a bug to chase.
23. `IvBalanceLotGridDataSource : GridCustomDataSource` holding the `IvBalanceLotQuery`, mapping `options.StartIndex`/`options.Count`/`options.SortInfo` into it, exactly like `IvStockMasterGridDataSource`.
24. Toolbar: `REFRESH`, `FILTER`, `EXPORT` (enabled from `AccessRights.CanAsync(menu, PermissionCodes.Export)`). 400 ms debounce on search. Expiry highlighting computed client-side from the business date returned by the service — do not re-derive it in the grid.
25. Captions carry the caveats: the value column reads **"Est. value"** (tooltip: pile price × qty, not a GL valuation), and the quantity KPI reads **"Total qty (all UOM)"** (D16).

### Phase E — Export (parallel with D)
*Files: `ErpWeb/Inventory/IvBalanceLotExportEndpoints.cs` (new), `ErpWeb/Program.cs`*

26. `MapGet("/inventory/balance-by-lot/export")` with `.RequireAuthorization()`. The `[FromQuery]` parameters carry **only the filters** — never `companyCode`/`branchCode` (SC3). `PermissionCodes.Export` is checked inside the handler and a denial returns `Results.Forbid()` (SC2).
27. The handler calls the **service** (`IIvBalanceLotService`), not the repository directly, so the export inherits the service's tenant resolution, ACCESS check, filter composition and `CanViewPrice` masking (R10, U1). Ask for the match count first, then fetch the rows. Build the workbook with `DocumentFormat.OpenXml` (headers + `CellText`/`CellNumber` helpers, copy from `IvStockMasterExportEndpoints`). **When `CanViewPrice` is false the value column is omitted from the header row and from every data row — not blanked** (SC5). `MaxExportRows = 50_000`; when the match count exceeds it return `Results.BadRequest` naming both numbers (R8).
28. Register in `Program.cs` beside `MapIvStockMasterExportEndpoints()` (`:109`).

### Phase F — Menu, tests, docs
*Files: `ErpWeb.Core/Menus/MenuCodes.cs`, `ErpWeb/Menus/menus.xml`, `scripts/init-inv-balance-lot-menu.sql` (new), `ErpWeb.Tests/TestCategories.cs`, `ErpWeb.Tests/IvBalanceLotServiceTests.cs` (new), `ErpWeb/docs/inventory-balance-by-lot.md` (new) or `ErpWeb/docs/inventory-stock-lot.md`*

29. `MenuCodes.InventoryBalanceLot = "INV_BALANCE_LOT"`.
30. `menus.xml`: a new `<Menu Code="INV_INQUIRY" Name="Inquiry" SortOrder="3" Icon="fa-solid fa-magnifying-glass-chart">` under `INVENTORY`, with the page as a child (`Name="Balance by Lot"`, `Route="/inventory/balance-by-lot"`, `SortOrder="1"`). Confirm `MenuDeploymentParityTests` passes.
31. `scripts/init-inv-balance-lot-menu.sql` following `init-inv-stock-count-menu.sql`: ensure the menu row (resolving its parent by `MenuCode`), then insert the `ACCESS` and `EXPORT` `MenuPermission` rows with `NOT EXISTS` guards, re-activate any disabled grants, and end with a verification `SELECT`. Remember `dbo.RoleMenuPermission` uses `IsAllowed`, not `IsActive`.
32. `TestCategories.cs`: add `public const string InventoryBalanceLot = "InventoryBalanceLot";` with a doc comment.
33. `IvBalanceLotServiceTests.cs`, tagged `[Trait(TestCategories.Name, TestCategories.Inventory)]` + `[Trait(TestCategories.Name, TestCategories.InventoryBalanceLot)]`. Groups:
    - **Tenant isolation** — an `ICode` that exists in another company is excluded; the same company/item in another branch is excluded.
    - **Toggle polarity, pinned in BOTH directions** (the review found this ambiguous): with `IncludeZeroQty = true` a `StdQty == 0` pile is returned, with `false` it is not; likewise `IncludeInactive` for an inactive item and `IncludeNonStockControl` for a `StockControl == false` item. Assert the presence/absence of the *specific seeded row*, not a total count.
    - **Orphans (D15)** — a pile with no `IvStockMaster` row is returned with a null `IDesc` when `IncludeInactive = true`, and excluded when `IncludeInactive = false`.
    - **Expiry** — yesterday is expired, today is **not** (R6), tomorrow is not, null is not.
    - **Last movement** — a `TransDate` of exactly `from` is included, exactly `to` is included, `to + 1 day` is excluded (R13).
    - **Sorting** — an unknown `SortField` falls back to the default order and cannot influence the query; a whitelisted field sorts; `SortDescending` reverses only a valid field.
    - **Status** — an empty selection returns all statuses including `SCRAPS`; a single-select list restricts to it (R11).
    - **SearchText** — matches on item code, description, warehouse, bin and lot (D11).
    - **Summary equals the predicate** — `TotalRows` equals the grid's `TotalCount`, `TotalQty` equals `SUM(StdQty)`, `TotalValue` equals `IvQty.Round(SUM(StdQty * price))`, and the zero/expired counts match — asserted at page size 1 **and** page size 50 so the aggregate provably does not depend on paging (N5).
    - **Security** — ACCESS denied returns `AccessDenied` with no data (SC1); a caller holding ACCESS but **not** EXPORT is refused by the export path (SC2); a caller holding EXPORT but **not** `CanViewPrice` receives no value on either path (SC4/SC5); `CanViewPrice = false` returns rows with `Value == null`; `CanViewPrice = true` returns the documented value; `ModifiedBy` is `null` and no database column was invented for it (D20).
    - **Export** — the export path uses the same composition: the exported row count equals the grid's `TotalCount` for the same query (R10); the value column is **absent** (not blank) when `CanViewPrice` is false (SC5); the 50 000 / 50 001 boundary behaves as specified (R8).
34. Write `ErpWeb/docs/inventory-balance-by-lot.md` recording: the TBD Phase 0 measurements, the D10 divergences from the pickers, the D3 valuation rule and its caveat, the D4 `CanViewPrice` gate and its sign-in caveat, and the "Last movement" semantics of `TransDate`.

---

## 7. Relevant files

**Create**
- `scripts/alter-userlogin-canviewprice.sql`
- `scripts/init-inv-balance-lot-menu.sql`
- `ErpWeb.Core/Inventory/IvBalanceLotResults.cs`
- `ErpWeb.Core/Inventory/IIvBalanceLotService.cs`, `ErpWeb.Core/Inventory/IvBalanceLotService.cs`
- `ErpWeb.UI/Inventory/Inquiry/IvBalanceLot.razor`, `.razor.cs`, `.razor.css`
- `ErpWeb/Inventory/IvBalanceLotExportEndpoints.cs`
- `ErpWeb.Tests/IvBalanceLotServiceTests.cs`
- `ErpWeb/docs/inventory-balance-by-lot.md`

**Modify**
- `ErpWeb.Model/Entities/IvBalLoc.cs` — no change
- `ErpWeb.Model/Entities/UserLogin.cs`, `ErpWeb.Model/Configurations/UserLoginConfiguration.cs`
- `ErpWeb.Model/Repositories/Inventory/IvStockCommonRepository.cs`
- `ErpWeb.Model/Repositories/UserLoginRepository.cs`
- `ErpWeb.Core/Authentication/AppClaimTypes.cs`, `ErpWeb.Core/Services/AuthService.cs`, `ErpWeb.Core/Services/CurrentUserService.cs`
- `ErpWeb.Core/CoreServiceCollectionExtensions.cs`
- `ErpWeb.Core/Menus/MenuCodes.cs`
- `ErpWeb.UI/Admin/AdminUser.razor`, `.razor.cs`
- `ErpWeb/Menus/menus.xml`, `ErpWeb/Program.cs`, `scripts/init-userlogin.sql`
- `ErpWeb.Tests/TestCategories.cs`

**Read first (do not modify)**
- `ErpWeb/docs/inventory-stock-lot.md` — authoritative inventory rules
- `ErpWeb.Model/Configurations/Inventory/IvBalLocConfiguration.cs`, `IvLotConfiguration.cs`, `IvStatusConfiguration.cs`, `IvLocationConfiguration.cs`
- `ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs` (`:404` find-or-create, `:565`/`:612` qty mutation, `:808` location stamp)
- `ErpWeb.Core/Inventory/IvInventoryLookupService.cs` (`IvOnHandBalanceRow`)
- `ErpWeb.Core/Inventory/IvStockCountService.cs:1153` — the valuation + rounding precedent
- `ErpWeb.Core/Inventory/IvMasterResults.cs` — envelope + sort-whitelist shape
- `ErpWeb.UI/Inventory/Masters/IvStockMasterList.razor(.cs)` — page / data source / toolbar template
- `ErpWeb/Inventory/IvStockMasterExportEndpoints.cs` — export template
- `scripts/alter-iv-stock-integrity.sql`, `scripts/init-inv-stock-count-menu.sql`

---

## 8. Verification

1. `dotnet build ErpWeb.slnx --nologo -v:q` → 0 `error CS`/`RZ`. (The 8 `MSB3027`/`MSB3021` are the running `ErpWeb` app locking the host project's copied DLLs; build `ErpWeb.Core`/`ErpWeb.UI`/`ErpWeb.Tests` individually if needed. `ErpWeb.Tests` never compiles `.razor`, so this step is mandatory after Phase D.)
2. **Build and test each layer as it lands, not only at the end** — `dotnet build ErpWeb.Core`, then `ErpWeb.UI`, then `ErpWeb.Tests`, then the solution. Phase A must be green on its own before Phase C starts, because it is a cross-cutting security change (SC6).
3. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=Inventory&Category!=SqlServer"` → matches the documented baseline. The pre-existing failures are 9 `SaCustServiceTests` + 4 `PoSupplierServiceTests` (GL-code/phone WIP) + 2 library `InvoiceTypeCode`; anything else is a regression. **Use this exact filter syntax** — it is the syntax already shipped in `.vscode/tasks.json` (`test: Inventory (no SqlServer)`), so copy a working command out of the repo rather than inventing one; `Category` is the trait name from `ErpWeb.Tests/TestCategories.cs`.
4. `dotnet test … --filter "Category=Menus"` → proves the new `MenuCodes` constant and the `INV_INQUIRY` parent both have `menus.xml` rows.
5. Apply `scripts/alter-userlogin-canviewprice.sql` and `scripts/init-inv-balance-lot-menu.sql` twice each on a scratch DB and twice on dev `ERPWeb`; the second run must be a clean no-op. Verify the menu ends with exactly the expected active `MenuPermission` rows.
6. Manual smoke: sign in as a user with `CanViewPrice = 0` — the Est. value column is absent from the grid **and** the export (absent, not blank); sign out, set the flag, sign in again — it appears. Export > 50 000 matched rows returns the refusal message, not a truncated file. Confirm the KPI and the grid agree under the same filter.
7. Manual smoke (deferred items): grid responsiveness at high row counts with `IncludeZeroQty = true`.

The full acceptance list is §9.

---

## 9. Acceptance criteria

Implementation is complete only when every line below is demonstrated — by a test, a script run, or a recorded manual smoke.

**Phase 0 / data integrity**
- [ ] `UQ_IvBalLoc_StockSlice`, `CK_IvBalLoc_StdQty_NonNegative`, `LotId` and `RowVersion` are all present on `dbo.IvBalLoc`.
- [ ] Dev `IvBalLoc` measurements recorded: rows, zero-qty rows, non-NULL `UnitPrice`, non-NULL `Cost`, orphan piles.
- [ ] No new `IvBalLoc` DDL was shipped.

**Scope and isolation**
- [ ] Company isolation: another company's `ICode` never appears.
- [ ] Branch isolation: another branch's pile never appears.
- [ ] Scope is resolved server-side; no company/branch parameter is accepted from a client (SC3).

**Filter semantics**
- [ ] Zero-qty piles are included by default; disabling the toggle removes them.
- [ ] Inactive items are included by default; disabling the toggle removes them.
- [ ] Non-stock-control items are included by default; disabling the toggle removes them.
- [ ] Each of the three toggles is asserted **in both directions** against a specific seeded row.
- [ ] Orphan piles follow D15 and are visible with `IncludeInactive = true`.
- [ ] Expiry: yesterday is expired, today is not, null is not (R6).
- [ ] Last movement: `from` and `to` inclusive, `to + 1 day` excluded (R13).
- [ ] An empty status selection means all statuses, including `SCRAPS` (R11).
- [ ] `MinQty`/`MaxQty` are inclusive.

**Query integrity**
- [ ] An unknown `SortField` cannot affect the generated query and falls back to the default order (R3, U3).
- [ ] Grid, count, summary and export share one filter definition (R10, U1).
- [ ] For one query: `summary.TotalRows == grid.TotalCount == exported row count`.
- [ ] The summary is aggregated in SQL; no `ToListAsync` precedes it (N5).
- [ ] `TransDate` and `ExpiryBefore` apply no `.Date` to a database column (R13).

**Security**
- [ ] ACCESS is enforced server-side; a denied caller gets `AccessDenied` and no data (SC1).
- [ ] EXPORT is enforced inside the endpoint; a denied caller gets `Forbidden` (SC2).
- [ ] An unauthorized caller receives no value, on the grid or the export (SC4).
- [ ] An unauthorized export omits the value **column**, not merely its values (SC5).
- [ ] The `CanViewPrice` sign-in caveat is documented on the admin screen and in `docs/` (D13).

**Data / DTO**
- [ ] `ModifiedBy` is `null` and no nonexistent database column was invented (D20, N6).
- [ ] `LotNo` comes from `IvBalLoc.LotNo`; `IvLot` supplies only `ExpiryDate` (D21).
- [ ] `Value = IvQty.Round(StdQty * (UnitPrice ?? PurchasePrice ?? 0m))` and `totalValue` matches the documented expression (§4.4).

**Export**
- [ ] Exactly 50 000 matched rows exports successfully.
- [ ] 50 001 matched rows is refused, with a message naming both numbers (R8).
- [ ] The exported filter result matches the grid's for the same query.

**Delivery**
- [ ] `dotnet build ErpWeb.slnx` passes with 0 `error CS`/`RZ`.
- [ ] The relevant test categories pass with no failures beyond the documented baseline.
- [ ] Both SQL scripts are idempotent on a second run.
- [ ] The new page and menu are reachable for a user holding ACCESS.

---

## 10. Further considerations

1. **Claim staleness vs a database read (D13).** *A:* accept the claim and document "sign out and back in" (recommended — identical to `changepass`). *B:* read `CanViewPrice` from `userlogin` by `uid` in the service so a change applies on the next query, at the cost of one extra read and a divergence from every other user flag. *C:* B, plus re-issue the cookie when an admin edits their own row.
2. **Row volume with `IncludeZeroQty = true` — decided (D5), with a gate.** Zero-qty slices are never deleted, so they can dominate the table. The default stays `true` per the owner decision, and Phase B step 13 now carries an explicit **STOP-and-ask** clause if the measured volume is unusable. The only sub-option left open is cosmetic: instead of a bare `TotalRows`, show "N of M rows carry stock" so the header cannot be misread. (Wait for the measurement before choosing.)
3. **`SearchText` semantics (D11).** *A:* `Contains` on ICode/IDesc/WhCode/LocCode/LotNo, matching `SearchOnHandPagedAsync` (recommended). *B:* prefix for codes, contains for descriptions, as the source plan proposed. *C:* exact-match item code only. Note `Contains` on `nvarchar` compiles to `LIKE '%x%'`, which cannot seek — acceptable at the current data volume, worth revisiting if `IvBalLoc` grows.
4. **A stale-`UQ_IvBalLoc_StockSlice` database.** If Phase B step 12 finds the integrity script unapplied, duplicate slices are possible and the summary would double-count. The guard to add regardless: reuse `IvInventoryReconciliationService`'s `DUPLICATE_SLICE` detection as a one-off Phase B probe, and consider a "duplicate slice" warning row on the page later.
5. **Index coverage.** `IX_IvBalLoc_ICode_WhCode` is `(CompanyCode, BranchCode, ICode, WhCode)`, so a filter on `WhCode` alone cannot seek. Accept a scan for V1 and measure; a `(CompanyCode, BranchCode, WhCode)` index is the follow-up if it matters.
6. **Summary across UOMs and statuses — decided.** The quantity KPI is captioned "Total qty (all UOM)" (D16) and an empty status selection means all statuses (D19). The only thing left is presentation polish: consider pairing the KPI with a status chip so "includes SCRAPS" is visible rather than implied.
7. **Orphan piles — decided (D15).** LEFT join to `IvStockMaster`, so an orphan pile is returned with blank item fields; it is visible whenever `IncludeInactive = true` (the default) and excluded when that toggle is off. This is possible on any database where the integrity script's guarded FK was skipped because no matching unique key existed. What remains is the test that pins both directions (§6 step 33) and, later, a "pile has no item master" warning affordance.
8. **A future drill-down** would join `IvTrxHistory` on `FromBalLocId`/`ToBalLocId` (both indexed via FKs, no FK to batch). That is a separate inquiry page; note it here so it is not bolted onto this one.
