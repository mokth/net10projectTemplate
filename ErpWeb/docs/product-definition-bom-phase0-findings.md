# Product Definition / BOM — Phase 0 Findings

**Database:** `ERPWeb` on `.\SQLEXPRESS` (live, Trusted_Connection)  
**Git branch:** `transaction`  
**Audit date:** 2026-09-28  
**Method:** read-only `sys.*` / `sqlcmd` + whole-solution code search  
**Phase 0 result:** **PASS** — live DB reachable; `dbo.PrDefBOM` **ABSENT** → create modern table (no WC*/ProcessCode). Blocking questions resolved below.

**Note:** Branch `transaction` also contains scaffolded Planning entity stubs (`PrDefMa`, `PrDefMachine`, `PrDefProcess`, `PrDefWcenter`, etc. with legacy `CompCode`/`LocCode`/`Wccode` property names). Those are **not** registered on `AppDbContext` and are **not** used by Product Definition Phase 1. Live SQL has no `PrDefBOM` table; our implementation creates/maps the modern `PrDefBOM` only.

---

## Live DB access

| Check | Result |
|-------|--------|
| Connection | OK (`Server=.\SQLEXPRESS;Database=ERPWeb`) |
| `OBJECT_ID(N'dbo.PrDefBOM')` | **ABSENT** |
| Tables matching `%BOM%` / `PrDef%` | none |

**Rule applied:** table absent (live confirmed) → create new schema. Do not invent CompCode/LocCode/WC* columns; use project `CompanyCode` / `BranchCode` / `LocationCode` naming (same as `IvStockMaster`, `IvCustPriceGroup`).

---

## Supplied DDL vs live

| Supplied claim | Live |
|----------------|------|
| Table `PrDefBOM` with CompCode/LocCode/… | **Table does not exist** |
| PK including `WCCode`/`WCICode`/`ProcessCode` | N/A — columns and table absent |
| Discrepancy | Supplied script is outdated / not deployed; **do not repair** or invent WC* |

Repo search: **zero** hits for `PrDefBOM`, `BomDefault`, `WIPBomDefault`, `WCCode`, `WCICode`. `ProcessCode` only in unrelated `docs/erp_cyclecount-study.md`.

---

## Repository patterns (confirmed)

| # | Topic | Finding |
|---|--------|---------|
| 1–2 | Item Master | List `/inventory/items`, Entry `/inventory/items/{new\|edit\|view}` — `IvStockMasterList` / `IvStockMasterEntry` |
| 3 | Stock entity | `IvStockMaster` PK `(CompanyCode, ICode)`; live column `Active` → property `IsActive` |
| 4 | Lookups | `IIvInventoryLookupService` — stock company-scoped active; warehouses company+branch active |
| 5 | Tenant | `IInventoryTenantContext` — company / branch / write scopes; `InventoryLeftoverSite.Apply` stamps Branch/Location |
| 6 | Menu/auth | `menus.xml` + `MenuCodes` + `ACCESS`/`ADD`/`EDIT`/`DELETE`; **no PLANNING menu yet** |
| 7 | CRUD pattern | `IvMasterOperationResult<T>` + services; Price Groups aggregate save in `SaSalesRefService.ItemFamily` |
| 8 | Qty | `IvQty.Scale = 4`, `AwayFromZero` |
| 9 | Concurrency | Header `RowVersion` on Item Master / Price Groups; line-only aggregates use header token |
| 10 | Item types | Live `IvType`: `FG`, `RM` (both `KeepStock=1`). App has **no** FG/RM eligibility API — only `SERVICE` special-case in PO. **Do not invent** FG/RM filters for Phase 1. |

Unsaved changes: DxPopup “You have unsaved changes…” on Cancel (`IvStockMasterEntry`), not NavigationLock for dirty state.

Empty aggregate precedent: Price Groups **rejects** zero lines (“At least one item price is required.”).

---

## Locked decisions (Phase 0)

| Decision | Lock |
|----------|------|
| Git branch | `transaction` |
| Schema | **Create** `dbo.PrDefBOM` (modern). Columns use `CompanyCode`/`BranchCode`/`LocationCode` (not CompCode/LocCode). No WC*/ProcessCode. `StdQty`/`Tolerance` = `decimal(18,4)`. |
| DB → C# map | 1:1 modern names (no renames). |
| Business key / duplicate | `(CompanyCode, ProdCode, ICode)` unique |
| Scope | **CompanyCode** isolation (Item Master style). Branch/Location leftover stamps only — not part of uniqueness. |
| Warehouse | Meaning: **default source WH**. Required on Save. Seed from Stock Master `DefWarehouse` on component select; user may change; do not re-force on Save. |
| Header display | Product Description/UOM = **live** Stock Master |
| Component IName/StdUOM | **Snapshot at save**; display stored values on Get |
| Item eligibility | Active Stock Master only; **no** invented FG/RM restriction |
| BomDefault | `bit`, default true; **no** single-default enforcement / no auto-uncheck |
| Tolerance | `decimal(18,4) >= 0`; **excluded from PrBomCalc** |
| WIPBomDefault | Persist, default false; **no UI**; preserve on ICode match during replace |
| Empty BOM on Save | **Reject** — at least one component (Price Groups precedent) |
| List population | **Option A** — only products that already have BOM lines |
| Active column | From Stock Master `IsActive` |
| Save strategy | Transactional **replace-all** (UID unreferenced — new table). Preserve `WIPBomDefault` when ICode matches prior row. |
| Concurrency | Client sends `LoadedUids` from Get; Save verifies DB UID set matches before replace; else Concurrency error. No new header table. |
| Delete | Deletes all BOM lines for product only — never Stock Master |
| Qty calc | `IvQty.Round(ProductionQty * StdQty)`; never round StdQty first; never use Tolerance |
| Permissions | ACCESS / ADD / EDIT / DELETE |
| Menu | Create `PLANNING` → `PLN_MASTER` → `PLN_PRODUCT_DEF` |

---

## Production snapshot contract (document only)

Copy at Production Order create: `ICode`, `IName`, `StdQty`, `StdUOM`, `Warehouse`, `BomDefault`, `Tolerance`. Do not live-link historical orders to current `PrDefBOM`.

---

## Unresolved / non-blocking

- Live FG/RM types exist but unused by app eligibility — future Production may add rules; Phase 1 does not.
- Float persistence tests N/A (new table uses decimal).

---

## Phase 1 gate

**Cleared** — live schema verified absent; create path approved; locks above apply.
