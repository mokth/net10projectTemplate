# Inventory Stock Count (Cycle Count) — Blazor port

> **STATUS: IMPLEMENTED 2026-09-24.** Phases 0 → 4 are in the codebase: model + configs, DDL and menu
> scripts (both applied twice to dev `ERPWeb`, second runs clean no-ops), the service, the ADJ core
> extraction, the four-mode UI, the SQLite suite (37 tests) and the SQL Server concurrency suite
> (5 tests, `Skipped: 0` with `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`). Full fast suite is
> **2016 total / 2001 passed / 15 failed** — the same pre-existing baseline (9 `SaCustServiceTests` +
> 4 `PoSupplierServiceTests` + 2 library `InvoiceTypeCode`). Solution build: 0 errors.
> Deviations from this plan are recorded in `docs/erp_cyclecount-study.md` → "Implemented in ErpWeb":
> the line column is `LineNumber` (SQL Server 2022 rejects a bare `LineNo`), `RollbackAsync` does not
> nest a transaction around the posting service (SQLite), and `RecoverAsync` clears `PostedBatchNo` to
> keep invariant I1 exact. Still owed: the Phase 0a grid-prototype measurement and a browser smoke.
>
> **Rev 3 (2026-09-24): D11 / I2 relaxed — a `COUNTED` sheet stays editable until it is `POSTED`.**
> The original rule froze the sheet the moment **Save counts** was pressed, which made "save my work"
> and "sign the sheet" the same act and made a long count keyed in over several days impossible. See
> the revised **D11**.

## Overview

Add a physical stock-count document to the Inventory module. It snapshots live `IvBalLoc` slices into its own staging tables, records physical quantities, then at POST computes each variance against **live** stock and generates an **ADJ batch** posted through the **existing** `IIvInventoryPostingService`. No new posting engine, no new posting table, no change to how stock moves.

The legacy `MRP.Inventory.CycleCount` module (studied in `docs/erp_cyclecount-study.md`) was a thin front-end that generated an IA (inventory adjustment) batch. In this solution the equivalent of IA is `IvTrxTypes.StockAdjustment = "ADJ"`, and it is already implemented end-to-end: `PostInventoryADJAsync` / `RollBackInventoryADJAsync` in `ErpWeb.Core/Inventory/IvInventoryPostingService.cs:812,1026`, dispatched from `DispatchAsync` (`:54`, `isAdj` at `:69`, menu mapping at `:105`).

The one additive engine change — extracting the ADJ body into an in-transaction core method — makes batch-insert + stock-move + header stamp a single transaction, which structurally removes the legacy §8.4 family of defects ("the document is never un-posted", "double-post risk", "two posting entry points, only one updates the header").

The single deliberate semantic departure from legacy: **the adjustment delta is computed at POST against live stock**, not frozen when the sheet was generated. The sheet's `SystemQty` is recorded as evidence and displayed in the post preview; a stale line is flagged, not blocked.

## Revision 2 — review record (2026-09-24)

Reviewed against the code (not just re-read). Verdict: **approve with changes** — 8.8/10. The architecture is **kept unchanged**: separate count document → live `IvBalLoc` validation → the existing `ADJ` engine → one transaction → rollback / `Recover` / reconciliation. Revision 2 closes the business-semantics and operational-safety gaps; it adds no posting infrastructure and **no new transaction type** (D17).

| Area | Score | Rev 2 outcome |
| --- | --- | --- |
| Architecture / reuse of `ADJ` | 9.5 | unchanged — do not redesign |
| Data model | 9.0 | one rename (`SnapshotUnitPrice`, D15); immutability stated (I2/I3) |
| Posting correctness | 9.0 | **D9** locks the CountDate-vs-live-stock date semantics |
| Concurrency | 8.5 | **I1** states the one-POSTED-batch invariant; header `RowVersion` unchanged |
| UI / UX | 8.0 | **D12** fixes multi-slice "Enter by item"; grid-volume acceptance criteria added |
| Testing | 9.0 | +business-safety, +date-semantics, +rounding, +grid-volume, +ADJ-extraction regression |
| Audit / recovery | 8.5 | **D11/D14/D16** — immutability, server stamps, reconciliation as an acceptance criterion |
| Deployment | 9.0 | unchanged |
| Scope control | 9.5 | unchanged |

### Review items → where each one is closed

| Review item | Closed by |
| --- | --- |
| **P0-1** CountDate vs live-posting date | **D9** (locked) + step 14c guard + Phase 0a item 1 |
| **P0-2** Regeneration underspecified | **D10** + the state table in step 7 |
| **P0-3** Multi-slice "Enter by item" | **D12** + `SetItemCountAsync` (step 6) + step 20 |
| **P1-1** Post/re-post idempotency | **I1** + step 14b + step 16 |
| **P1-2** `COUNTED` editability | **D11 / I2** + step 9 — *reopened and answered differently in rev 3* |
| **P1-3** Server-generated timestamps | **D14 / I4** |
| **P1-4** `CountDate` timezone | **D14** + the `CountDate` schema row |
| **P1-5** Maximum sheet size | **D13** + the cap bullet in step 3 |
| **P1-6** `IncludeZeroQty` meaning | the scope rules in step 3 |
| **4.1** Evidence immutable after post | **I3** + the line-table immutability note |
| **4.2** Two rollback routes | step 16 + step 24 (reconciliation is a formal acceptance criterion) |
| **5.2** One rounding rule | step 14f + the rounding test in step 18 |
| **5.4** Insufficient stock stays the engine's job | the note in step 18 (over-decrease is unreachable from the count service) |
| **6.1** Tenant validation at every entry point | **I6** + the line-table tenant note |
| **6.2** `UnitPrice` semantics | **D15** + the `SnapshotUnitPrice` rename |
| **7.2** Grid volume | step 20 volume criteria + Phase 0a item 4 |
| **7.3** Enter-by-item ≠ arbitrary slice | **D12** (same as P0-3) |
| **8** Test additions | step 11 (business) + step 18 (posting/date/rounding) + step 22 (concurrency) |
| **9** Spike before implementation | **Phase 0a** (a hard gate) |
| **11** Do not add a `StockCount` trx type | **D17** + the out-of-scope list |
| **12** Implementation gate | **Implementation gate** (end of file) |

### Newly verified for rev 2 (do not re-research)

| Fact | Evidence |
| --- | --- |
| `PostInventoryMIAsync` is the exact wrapper/core split to copy: the wrapper opens db + transaction, calls a core that contains **no** `RollbackAsync` / `SaveChangesAsync` / `CommitAsync`, rolls back itself when the result failed, then saves and commits | `IvInventoryPostingService.cs:1362` (wrapper) vs `:1406` (core); the MI core ends at `return …Ok(batchNo, opId)` (~`:1570`) |
| `PostInventoryADJAsync` (`:812`) currently contains **8** `await tx.RollbackAsync(…)` calls plus `SaveChangesAsync` + `CommitAsync` (`:820`–`:1017`). All of them must move to the wrapper, or the caller's transaction is already finished when it tries to save | read of `:812-1024` |
| The house way to test "failure between the stock move and history still rolls everything back" is an `internal Action?` hook + `InternalsVisibleTo`; there is **no ADJ hook** today, so the rev-2 transaction test needs one | `TestHookAfterMiStockUpdate` / `TestHookAfterMiHistory` at `:1268` / `:1271` (used by `IvMiscIssuePostingServiceTests`, `IvVendorReturnPostingServiceTests`, `SaInvoiceServiceTests`); `ErpWeb.Core.csproj:15` |
| `IvTrxTypes.Scrap == "SC"` — an `SC` `CountNo` prefix collides with a **transaction-type token** | `IvTrxConstants.cs:14` |
| `IvTrxBatch.RefNo` is `nvarchar(50)`, `Remarks` `nvarchar(250)`, `LocationCode` `nvarchar(10)`, and `(CompanyCode, BranchCode, TrxType, RefNo)` is indexed | `IvTrxBatchConfiguration.cs:19,20,23,43` |
| `IvBalLoc.UnitPrice` exists, but **`IvOnHandBalanceRow` does not project it** — only `PurchasePrice`. The Generate snapshot therefore cannot read the balance price without touching the DTO the on-hand/lot pickers already share (`IvInventoryLookupService.MapOnHand`) | `IvBalLoc.cs:22` vs `IvOnHandBalanceRow.cs`; `IvStockCommonRepository.cs:475,521` |
| Both `IncreaseBalLocQtyAsync` and `DecreaseBalLocQtyAsync` **overwrite `IvBalLoc.TransDate` with `batch.TrxDtTime`** | `IvStockPostingRepository.cs:564-640` |
| `IvBalLoc.TransDate` is **load-bearing for FIFO**: piles are ordered `TransDate, LotNo, Id` and filtered `TransDate <= document date` | `IvSpShipmentAllocator.cs:32`; `IvSpFifoEligibility.cs`; `docs/sales-invoice-recon.md:16-18` |
| The legacy Generate filtered **`TransDate <= StartCount`** ("as at" snapshot) and always excluded `IStatus = 'SCRAPS'` | `docs/erp_cyclecount-study.md` §6.2 |
| `ICurrentDateService` already gives **company-local** time (`Company.TimeZoneId`, fallback `Asia/Kuala_Lumpur`) — use it, never `DateTime.Now`/`UtcNow`, for user-visible stamps | `ErpWeb.Core/Services/CurrentDateService.cs:6-40` |
| `IvQty.Round` = 4 dp, `MidpointRounding.AwayFromZero` | `IvTrxConstants.cs:35` |
| `PostStockOutInTransactionAsync` carries an extra `expectedTrxType`; the ADJ core hard-checks `IvTrxTypes.StockAdjustment` itself, so the new member deliberately omits that argument | `:1276-1289`, `:1428` |

## Verified baseline (do not re-research)

| Fact | Evidence |
| --- | --- |
| The legacy IA path already exists as `ADJ` | `ErpWeb.Core/Inventory/IvTrxConstants.cs:15`; `PostInventoryADJAsync` / `RollBackInventoryADJAsync` at `IvInventoryPostingService.cs:812,1026` |
| The ADJ engine applies a **delta**, not an absolute quantity | `IvStockAdjustmentLineInvariant` — To-side = increase, From-side = decrease, positive magnitude only, `IvQty.Round` 4 dp |
| Rollback reverses and re-opens the batch | `RollBackInventoryADJAsync` → `BatchStatus` POSTED→NEW + inverse net + history delete |
| A decrease cannot go negative | `DecreaseBalLocQtyAsync` (`IvStockPostingRepository.cs:564`) `WHERE StdQty >= qty` |
| Balances are locked by surrogate Id, not by the 5-part slice | `LockBalLocByIdForTenantAsync` (`IvStockPostingRepository.cs:507`), `UPDLOCK, HOLDLOCK` on SQL Server |
| Posting re-validates slice **identity** only, never quantity | `ValidateAdjSliceMatch` in `IvInventoryPostingService.cs` |
| The `COUNT` reason code already exists | `IvAdjustmentReasons.Count = "COUNT"` (`IvTrxConstants.cs`) |
| Only ADJ's own service can create an ADJ batch | its `ValidateLineAsync` / `ValidateLinesAsync` / `AddDetails` / `ValidatedLine` are all `private`; a solution-wide grep found no other ADJ batch builder |
| The on-hand search **cannot** be the Generate source | `SearchOnHandPagedAsync` hard-filters `bal.StdQty > 0m && sm.IsActive && sm.StockControl` (`IvStockCommonRepository.cs:437-439`) |
| `GetOnHandByIdAsync` is the closer template for Generate | same file, `:497` — no qty filter, no active/stock-control filter |
| No catch-weight on balances | `IvBalLoc` has no weight columns; `UseWeight` is a PO/PR option + `appsettings.json` flag only |
| No costing engine | no `IvMas`, `IvMasPack`, `IvBalance`, `IvBalLocCost` entity or `DbSet` anywhere |
| `RunningNumberService.GetNextAsync` auto-creates a missing counter row | `ErpWeb.Core/Numbering/RunningNumberService.cs` (3-attempt loop); `SaveChangesAsync` enlists in the caller's transaction; `MsRunningNo.DocKey` is `nvarchar(20)` |
| No inline grid editing exists in the repository | only an unused `CommonDataGridEx.OnEditModelSaving` stub |
| `ErpWeb.Tests` references `ErpWeb.UI` but a green suite still proves nothing about a page | run `dotnet build ErpWeb.slnx` after touching `ErpWeb.UI` |

## Locked decisions

| # | Decision | Rationale |
| --- | --- | --- |
| D1 | Delta is computed at POST against live `IvBalLoc.StdQty`; `SystemQty` on the sheet is evidence only | Removes the stale-count bug class (legacy §8.4 #5/#7). Post preview flags staleness instead of blocking. |
| D2 | Dedicated tables `IvStockCountHdr` + `IvStockCountLine`, plus a generated `IvTrxBatch` (TrxType = `ADJ`) | Keeps snapshot/physical columns out of the shared `IvTrxBatchDetail`; the batch stays the movement record so history, the Adjustment list and rollback keep working |
| D3 | The count service owns the ADJ batch build; it does **not** call `IIvStockAdjustmentService.SaveNewAsync` | That method stamps `MenuCodes.InventoryStockAdjustment` + `PermissionCodes.Add` and cannot carry count provenance |
| D4 | Drop the weight axis, the zero-cost `CheckUP` two-pass dance, the `ICodeTo` range, the `Session` working set, and the Adjustment-list-as-rollback-home model | Structurally impossible or unnecessary in this codebase (see the baseline table) |
| D5 | New running-number key `IV_STOCK_COUNT`; `CountNo = "CC" + seq:D6`; the ADJ batch keeps `IV_BATCH` and carries `RefNo = CountNo` | The sheet needs a human-readable number while counting; the batch keeps one global inventory sequence. **Prefix `CC`, not `SC`** — `IvTrxTypes.Scrap == "SC"` (`IvTrxConstants.cs:14`), and an `SC…` `RefNo` on an `ADJ` batch would read as a scrap reference on the Adjustment list and in `IvTrxHistory` reporting |
| D6 | No month-end lock | The Blazor system has no `AdPara` period state (legacy §7.1 / §8.4 #6 knowingly not ported) |
| D7 | Single-pass count with a `RecountCount` counter; no multi-pass workflow | Legacy is single-pass; a full workflow is not justified yet |
| D8 | Count batches remain visible on the Stock Adjustment list; the authoritative rollback is the count screen, backed by `Recover` self-heal + a reconciliation finding | Divergence is *detected*, not assumed impossible |
| **D9** | **Posting date semantics.** `TrxDtTime = CountDate` (the physical-count business date). Posting is **refused** when `CountDate` is in the future, or older than `IvStockCountLimits.MaxBackdateDays` (**default 7**). Accepted consequence, made explicit and test-pinned: the ADJ engine writes `IvBalLoc.TransDate = TrxDtTime`, so a back-dated count **re-dates the pile** and can move it in FIFO order. | The operator signs a sheet for a business date; dating the correction "now" would make the adjustment land on a different day than the evidence. Bounded back-dating keeps the FIFO side effect small, and `MaxBackdateDays = 1` is the single switch if the business rejects any back-dating. |
| **D10** | **Regeneration safety.** A sheet containing **any** `PhysicalQty` can never be silently regenerated — not even in DRAFT. Regeneration is allowed only when every line's `PhysicalQty` is null; otherwise the caller must pass `discardCounts: true` after an explicit "Regenerate and discard N counted lines?" confirmation, or Cancel + create a new count. | Closes the ambiguity between "always a full replace" and "refused unless DRAFT": a DRAFT sheet **with** counts is exactly the dangerous case. Enforced in the service, not only in the UI. |
| **D11** | **Count immutability — rev 3 (2026-09-24).** `PhysicalQty` is editable while the sheet is `DRAFT`, `COUNTED` or `ROLLED_BACK`; the freeze happens at **`POSTED`**. `COUNTED` means only "at least one line has been counted so far" — a working state, because the evidence a variance audit rests on is the *posted* adjustment, not the working sheet. Amendments are visible as `RecountCount` + `CountedBy`/`CountedOn`. A `CANCELLED` sheet is retained as evidence and never re-counted. | The original rule ("once `COUNTED`, `PhysicalQty` is immutable") made **Save counts** and **sign the sheet** the same act. That is unusable for the normal case — a long sheet keyed in over several days — and it was inconsistent with every other document in this ERP, which freezes at `POSTED`. Corrections *after* posting remain `Rollback` + re-count; a mistyped quantity *before* posting is now simply an edit. |
| **D12** | **Multi-slice item entry.** "Enter by item" may set a quantity directly **only** when the item has exactly one countable line in this sheet. With more than one line the user must pick the slice(s), and the **service** refuses a direct item-level write with a message naming the item and its slice count. | An item can legitimately live in several warehouse/location/lot piles; one total quantity applied to an arbitrary slice would post a wrong adjustment. |
| **D13** | **Maximum sheet size.** Generate refuses to create a sheet whose candidate count exceeds `IvStockCountLimits.MaxCountLines` (**default 20 000**), naming the count and suggesting a narrower scope. The limit lives in a constants class like `IvPostingLimits`, not in the settings registry. | An `IncludeZeroQty` warehouse-wide generate must not push an unbounded result into a Blazor circuit. |
| **D14** | **Timestamps.** Every audit stamp (`Created`/`Updated`, `CountedOn`, `PostedOn`, `RolledBackOn`) is generated server-side from `ICurrentDateService.Now` (company-local). The browser never supplies a timestamp, and `CountDate` is interpreted in the same company-local zone. | Client clocks differ, and the ERP already has exactly one company-local clock — do not introduce a second. |
| **D15** | **`SnapshotUnitPrice` is evidence, never a posting input.** Posting uses the *locked* `IvBalLoc.UnitPrice ?? item.PurchasePrice ?? 0m`. The snapshot column is filled from `IvOnHandBalanceRow.PurchasePrice` (the shared DTO is left untouched) and is documented and named so nobody reads it as the posted cost. | Today the sheet value and the posted value can differ; a column named `UnitPrice` invites that mistake. |
| **D16** | **Staleness is warn-only, with one confirm.** A line whose live `StdQty` differs from `SystemQty` is flagged in the preview and posting is **not** blocked, but posting requires a single explicit confirmation when at least one line is stale, and the number of stale lines is stored in `IvStockCountHdr.PostedStaleLines`. | D1's whole point is that the delta self-corrects; blocking would make any movement between Generate and Post un-postable. Storing the count keeps the fact auditable after the session ends. |
| **D17** | **No `StockCount` transaction type.** Counts post as `ADJ` with `RefNo = CountNo`. | One posting path, one rollback path, the Adjustment list unchanged. Revisit only if the business needs count adjustments segregated in reporting. |

**Out of scope:** the Production variant (`IvProdCyCntHdr`), printed variance / accuracy reports, ABC count scheduling, a dedicated `StockCount` trx type, and making `IIvInventoryReconciliationService` a production stock-audit (it keeps its own "diagnostic only" caveat).

## Schema

Script `scripts/create-iv-stock-count.sql` — additive, idempotent (`IF OBJECT_ID IS NULL` / `IF COL_LENGTH IS NULL`), `SET QUOTED_IDENTIFIER ON`, with its own verification SELECT block. Manual DBA run; never at application startup.

**`IvStockCountHdr`**

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | int IDENTITY | PK |
| `CompanyCode` | nvarchar(5) | tenant; matches `TenantScopeContext.MaxCompanyLength` |
| `BranchCode` | nvarchar(5) | |
| `CountNo` | nvarchar(30) | `CC000001` — **not** `SC…` (collides with `IvTrxTypes.Scrap`); see D5 |
| `CountDate` | datetime2 | the physical-count business date (legacy `StartCount`). Becomes the batch `TrxDtTime` under **D9**; refused when in the future or older than `MaxBackdateDays`. Always company-local (**D14**) |
| `Status` | nvarchar(20) | DRAFT / COUNTED / POSTED / ROLLED_BACK / CANCELLED — lifecycle in the status machine below |
| `WHCode`, `IClassCode`, `ISubClassCode`, `IType`, `IStatus` | nvarchar(20/10) | scope stamps, so a sheet can be re-edited and re-printed. **SCRAPS is excluded by default** (legacy parity) and only appears when the operator adds it to the status scope |
| `ICodeList` | nvarchar(1000) | optional item IN-list |
| `IncludeZeroQty` | bit | snapshot of the Generate option |
| `CountedBy` | nvarchar(10) | person in charge |
| `Remark` | nvarchar(250) | |
| `PostedBatchNo` | int NULL | the generated `IvTrxBatch.BatchNo`; NULL only for the all-zero-variance post (step 15) |
| `PostedBy`, `PostedOn` | nvarchar(10), datetime2 NULL | |
| `PostedStaleLines` | int NULL | **D16** — how many lines were stale when the sheet was posted (0 = none); survives the session so "we posted against a moved count" is answerable later |
| `RolledBackBy`, `RolledBackOn`, `RollbackReason` | nvarchar(10), datetime2 NULL, nvarchar(250) | history kept across a re-post (I1) |
| `RowVersion` | rowversion | Level A concurrency |
| audit | | `Created` / `UserID` / `Updated` / `UpdatedUID` (legacy alias names, mapped via `HasColumnName`) |

Unique index `UQ_IvStockCountHdr_No (CompanyCode, BranchCode, CountNo)`.

**`IvStockCountLine`**

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | int IDENTITY | PK |
| `StockCountId` | int | FK → `IvStockCountHdr.Id`, `Restrict` (no cascade), mirroring `IvTrxBatchDetail.BatchId` |
| `LineNo` | smallint | |
| `BalLocId` | int | the pile; the only posting identity the engine needs |
| `ICode`, `IDesc`, `WHCode`, `LocCode`, `LotNo`, `IStatus`, `IClassCode` | | denormalised display/slice-match columns |
| `StdUom` | nvarchar(10) | |
| `SystemQty` | decimal(18,4) | live qty at Generate — evidence, never a posting input |
| `PhysicalQty` | decimal(18,4) NULL | NULL = not counted ⇒ excluded from posting (legacy semantics, kept deliberately) |
| `SnapshotUnitPrice` | decimal(18,4) NULL | **evidence only, never a posting input** (**D15**). Filled from `IvOnHandBalanceRow.PurchasePrice`; posting resolves its own price from the locked balance |
| `ExpiryDate` | datetime2 NULL | from `IvLot` |
| `RecountCount` | smallint | D7 |
| `CountedBy`, `CountedOn` | nvarchar(10) NULL, datetime2 NULL | |
| `RowVersion` | rowversion | |

Unique indexes `UQ_IvStockCountLine_BalLoc (StockCountId, BalLocId)` — one line per pile, which is what makes the post-time net-per-BalLoc unambiguous — and `UQ_IvStockCountLine_No (StockCountId, LineNo)`.

**Immutability (I2/I3, rev 3):** `PhysicalQty` is written by the count-entry path while the sheet is `DRAFT`, `COUNTED` or `ROLLED_BACK`. After `POSTED`, none of `BalLocId`, the slice columns, `SystemQty`, `PhysicalQty` or `SnapshotUnitPrice` is ever mutated — not by rollback, not by `Recover`. Only the header's status/stamps and the linked batch move.

**Tenant shape:** the line carries **no** `CompanyCode`/`BranchCode` column — it inherits the header's, and the service re-validates that the `BalLocId` belongs to that company **and** branch at Generate, `SaveCountsAsync` and `PostAsync` (**I6**). The unique index is therefore scoped by `StockCountId`, which is itself tenant-scoped.

## Status machine

New `IvStockCountStatuses` constants class in `ErpWeb.Core/Inventory`. `IvBatchStatuses` (NEW/POSTED/CANCELLED) is **not** reused — the count document has its own lifecycle.

| From | To | Trigger |
| --- | --- | --- |
| — | DRAFT | Generate + Save |
| DRAFT | COUNTED | Count-entry Save (at least one physical qty) |
| COUNTED | POSTED | Post |
| POSTED | ROLLED_BACK | Rollback |
| ROLLED_BACK | COUNTED | Re-count |
| ROLLED_BACK | POSTED | Re-post (creates a **new** batch) |
| DRAFT / COUNTED / ROLLED_BACK | CANCELLED | Cancel |
| DRAFT | (row deleted) | Delete |

Enforced server-side. Post requires `COUNTED` or `ROLLED_BACK` **and** that the previously linked batch (if any) is not POSTED. Edit of the header/scope requires `DRAFT`. Count entry requires `DRAFT`, `COUNTED` or `ROLLED_BACK` — i.e. anything except `POSTED`/`CANCELLED` (**D11**, rev 3).

**Invariants — each one gets a test, and each one is stated in the service's XML docs:**

- **I1 — at most one live batch.** A count document has **at most one POSTED batch at a time.** `COUNTED` ⇒ `PostedBatchNo` must be null. `ROLLED_BACK` ⇒ a previous `PostedBatchNo` may exist, but its `IvTrxBatch.BatchStatus` must not be `POSTED`. `POSTED` ⇒ either a `POSTED` batch or (the all-zero case) `PostedBatchNo = null`. A re-post always allocates a **new** batch number and leaves the old, rolled-back batch visible as history.
- **I2 — counts are editable until POSTED (rev 3).** `PhysicalQty` may be written while the status is `DRAFT`, `COUNTED` or `ROLLED_BACK` (**D11**); only `POSTED` (frozen evidence) and `CANCELLED` (terminal) refuse a write. A posted sheet is corrected with `Rollback` + re-count.
- **I3 — `POSTED` evidence is frozen.** `CountDate`, the scope, `SystemQty`, `PhysicalQty`, `SnapshotUnitPrice`, `BalLocId` and the slice columns are never mutated after posting — not by rollback, not by `Recover`. Rollback changes only the status/stamps and the batch.
- **I4 — every timestamp is server-generated** (**D14**); no request DTO carries a date that is written verbatim.
- **I5 — `CANCELLED` is terminal.** A cancelled sheet is evidence: it cannot be re-counted, posted or re-opened. (If operations push back on the round trip, an audited `ReopenAsync` is the alternative — decide it deliberately, never add it by accident.)
- **I6 — tenant shape at every entry point.** A line's `BalLocId` must belong to the header's company **and** branch in Generate, `SaveCountsAsync` **and** `PostAsync` — not only at save.

## Implementation steps

### Phase 0a — Architecture spike (hard gate, decisions only — no production code)

Time-boxed. Nothing in Phase 0 starts until all six answers are written into this file. This ordering exists so a large UI is never built on unresolved posting semantics.

1. **D9 confirmed.** Is `TrxDtTime = CountDate` acceptable given that it *overwrites* `IvBalLoc.TransDate` (`IvStockPostingRepository.cs:564/611`) and therefore the pile's FIFO position (`IvSpShipmentAllocator.cs:32`)? Pick `MaxBackdateDays` (default 7; set 1 for "effectively today only").
2. **D10 confirmed** — the regeneration rule, and the exact wording of the "Regenerate and discard N counted lines?" confirmation.
3. **D12 confirmed** — the multi-slice "Enter by item" interaction (pick a slice vs. refuse).
4. **Count-entry grid prototype at 2 000 and 5 000 lines.** Measure whether per-cell editors in a `DxGridDataCellTemplate` keep the circuit responsive, and decide paging / virtual scrolling / plain-HTML fallback. `MaxCountLines` (D13) is the backstop, not the plan.
5. **SQL Server `rowversion` + lock behaviour** on the scratch DB: `LockBalLocByIdForTenantAsync` + header `RowVersion` under two concurrent posts.
6. **The ADJ extraction proven inert**: do step 12 (extract the core, move the rollback/save/commit out, add the two test hooks), then run the existing `IvStockAdjustmentPostingServiceTests` **untouched** and confirm green before writing any count code.

### Phase 0 — Foundation (no business logic)

1. **Constants.** Add `InventoryStockCount = "IV_STOCK_COUNT"` to `ErpWeb.Core/Numbering/RunningNumberKeys.cs` (after `IvBatch` at line 6) and `InventoryStockCount = "INV_STOCK_COUNT"` to `ErpWeb.Core/Menus/MenuCodes.cs` (after `InventoryStockAdjustment` at line 18). Add the `menus.xml` row: one `<Menu Code="INV_STOCK_COUNT" Name="Stock Count" Route="/inventory/stock-count" SortOrder="9" />` inside the existing `INV_TRANSACTIONS` parent in `ErpWeb/Menus/menus.xml` (after `INV_STOCK_ADJUSTMENT` at line 23). The `MenuCodes` constant and the XML row must ship together — `ErpWeb.Tests/MenuDeploymentParityTests.cs` guards the pair, and `MenuSyncService` soft-disables any `dbo.Menu` row absent from the XML.

2. **Entities and configuration.** `ErpWeb.Model/Entities/Inventory/IvStockCountHdr.cs` and `IvStockCountLine.cs` per the schema above, plus `ErpWeb.Model/Configurations/Inventory/IvStockCountHdrConfiguration.cs` and `IvStockCountLineConfiguration.cs`. Configuration must declare the table name, `HasKey`, every `HasMaxLength`/`HasPrecision(18,4)`, the audit `HasColumnName` aliases (`CreatedDate`→`Created`, `CreatedBy`→`UserID`, `ModifiedDate`→`Updated`, `ModifiedBy`→`UpdatedUID`), `RowVersion` via `IsRowVersion()`, the two unique indexes, and the `HasOne(...).WithMany(...).HasForeignKey(l => l.StockCountId).OnDelete(DeleteBehavior.Restrict)` navigation on the line. Registration is automatic: `AppDbContext.OnModelCreating` calls `ApplyConfigurationsFromAssembly` (line 121). Add the two `DbSet`s to `ErpWeb.Model/Data/AppDbContext.cs` after the `IvTrxHistories` line (line 46).

3. **Generate query.** Add `ListStockCountCandidatesAsync` to `IIvStockCommonRepository` and `IvStockCommonRepository` (`ErpWeb.Model/Repositories/Inventory/IvStockCommonRepository.cs`), placed after `SearchOnHandPagedAsync` (line 420) and modelled on `GetOnHandByIdAsync` (line 497) — which already has **no** qty / active / stock-control filter. Parameters: company, branch, optional warehouse list, optional location list, optional item class, sub-class, item type, item status list, optional `ICode` IN-list, `IncludeZeroQty` (default true), `IncludeInactive` (default false), plus skip/take or a hard cap. Project `IvOnHandBalanceRow` (it already carries `Id`, `ICode`, `IDesc`, `WhCode`, `LocCode`, `LotNo`, `StdQty`, `StdUom`, `IStatus`, `ExpiryDate`, `IClassCode`, `LotControl`, `PurchasePrice`, `LotId`). Deterministic `ORDER BY ICode, WhCode, LocCode, LotNo, Id` — the legacy module had no `ORDER BY` at all.

   **Scope rules — decided, not inherited from the legacy query:**

   - `IncludeZeroQty = false` → only `StdQty > 0`; `IncludeZeroQty = true` → positive **and** zero rows. Never unfilter: the query always keeps `CompanyCode`/`BranchCode` (plus the tenant `LocationCode` when the scope carries one), so an `IncludeZeroQty` generate cannot reach another tenant, and a zero row for an inactive or non-stock-controlled item is still excluded by the two rules below.
   - `IncludeInactive = false` → drop items whose `IvStockMaster` is not active. `StockControl = false` items are **always** excluded — an uncontrolled item has no balance worth adjusting.
   - `IStatus = 'SCRAPS'` is excluded **by default** (legacy parity); the status multi-select can add it back explicitly.
   - **`TransDate <= CountDate` is deliberately NOT ported.** The legacy "as at" filter assumed `TransDate` is immutable, and it is not — every increase/decrease overwrites it with the batch date. Under D1 the scope is "the piles that exist now": a pile created after `CountDate` that nobody counted simply has a null `PhysicalQty` and is skipped at post.
   - **Cap:** the query returns the candidate count; `GenerateAsync` refuses above `IvStockCountLimits.MaxCountLines` (**D13**) with the number and a suggestion to narrow the scope.
   - **Price:** do **not** add a column to the shared `IvOnHandBalanceRow` — it is projected by `SearchOnHandPagedAsync` (`:475`) and `GetOnHandByIdAsync` (`:521`) and mapped into the on-hand/lot picker rows (`IvInventoryLookupService.MapOnHand`), so every inventory screen inherits the change. Snapshot `PurchasePrice` into `SnapshotUnitPrice` and rely on **D15** — posting resolves the real price from the locked balance.

4. **DDL script.** `scripts/create-iv-stock-count.sql`, additive and idempotent, following the house script rules: guarded `IF OBJECT_ID(N'dbo.IvStockCountHdr','U') IS NULL CREATE ...`; separate `GO` batches for the create and any later constraint step (under `SET XACT_ABORT ON` a later failure in the same batch rolls the earlier statements back); `SET QUOTED_IDENTIFIER ON` at the top; a verification block at the end printing the table and index names found. Header comment: manual DBA run, do not run at app startup, safe to re-run. Target `ERPWeb` explicitly — `scripts/init-msrunningno.sql` still carries a stale `USE ERPLiteEx`.

5. **DI.** Register `services.AddScoped<IIvStockCountService, IvStockCountService>();` in `ErpWeb.Core/CoreServiceCollectionExtensions.cs` immediately after the `IIvStockAdjustmentService` line (line 147). The two entities need no repository registration — `IIvStockCommonRepository` / `IIvStockTransactionRepository` / `IIvStockPostingRepository` are already registered in `ErpWeb.Model/ModelServiceCollectionExtensions.cs`.

**Verify before continuing:** `dotnet build ErpWeb.slnx` is clean, and the DDL script is a clean no-op on its second run against a scratch database.

### Phase 1 — Document lifecycle

6. **Service surface.** `ErpWeb.Core/Inventory/IIvStockCountService.cs` holding the DTOs and the interface, in the shape of `IIvStockAdjustmentService.cs` (result object with `Ok` / `OkSaved` / `OkPeek` / `OkDocument` / `OkList` / `OkPosting` / `Fail` factories). Members: `PeekNextCountNoAsync`, `SearchAsync`, `GetAsync`, `GenerateAsync` (with the explicit `discardCounts` flag of **D10**), `SaveAsync`, `UpdateAsync`, `DeleteAsync`, `CancelAsync`, `SaveCountsAsync`, `SetItemCountAsync` (the **D12**-guarded "Enter by item" write), `PreviewPostAsync`, `PostAsync`, `RollbackAsync`, `RecoverAsync`. Plus two constants classes next to `IvStockCountStatuses`, in the shape of `IvPostingLimits`: `IvStockCountLimits` (`MaxCountLines = 20_000`, `MaxBackdateDays = 7`).

7. **`GenerateAsync`.** Loads the candidate set from step 3, applies the scope, and builds the sheet lines: `BalLocId`, `LineNo` sequential from 1, denormalised display columns, `SystemQty = row.StdQty` (rounded through `IvQty.Round`), `SnapshotUnitPrice = row.PurchasePrice` (**D15** — evidence only), `ExpiryDate`, `PhysicalQty = null`. Pre-population is **always a full replace** (legacy behaviour), but **D10** governs when a replace may happen:

   | Sheet state | Any `PhysicalQty`? | Generate |
   | --- | --- | --- |
   | DRAFT | none | allowed |
   | DRAFT | any | **refused** unless `discardCounts: true`, which the UI only sends after "Regenerate and discard N counted lines?" |
   | COUNTED / POSTED | any | refused — no regeneration from these states at all |
   | ROLLED_BACK | any | refused (Cancel + new count, or re-count in place) |

   The refusal is enforced in the service, not only in the UI, and names the number of counted lines that would be lost.

8. **`SaveAsync` / `UpdateAsync`.** Allocate `CountNo` from `RunningNumberService.GetNextAsync(db, company, RunningNumberKeys.InventoryStockCount)` inside the same transaction as the header insert, formatted `"CC" + seq.ToString("D6")` (D5). `UpdateAsync` requires DRAFT and `RowVersion` (stale token ⇒ a concurrency failure message, per the house pattern; `AppDbContext.OnModelCreating` forces `RowVersion` to `ValueGenerated.Never` for SQLite, so SQLite tests must stamp a token explicitly on any service-created row before editing it).

9. **`SaveCountsAsync`.** Persists only `PhysicalQty`, `CountedBy`, `CountedOn` per line (legacy `SetIvCyCntUpdOnly` semantics — no insert, no delete), then moves DRAFT/ROLLED_BACK → COUNTED. Validate: `PhysicalQty >= 0` when present; every `BalLocId` still belongs to this company/branch (**I6**). Do not re-read and reconcile `SystemQty` here. `CountedBy`/`CountedOn` come from the authenticated user and `ICurrentDateService.Now` (**D14**), never from the request. A save containing **no** physical quantity anywhere does **not** move the document to `COUNTED` — it stays DRAFT: `COUNTED` means "at least one line counted". A save against a `COUNTED` sheet is **allowed** and leaves it `COUNTED` (**I2**, rev 3); only `POSTED` and `CANCELLED` are refused.

10. **`DeleteAsync`** requires DRAFT (removes lines then header, in a transaction). **`CancelAsync`** requires DRAFT/COUNTED/ROLLED_BACK. **`SearchAsync`** reuses `IIvStockTransactionRepository.SearchPagedAsync`-style paging but queries the count tables directly — `IvTrxBatchSearchArgs` is trx-type-shaped and does not fit a document whose statuses are DRAFT/COUNTED.

**Verify:** `dotnet test ErpWeb.Tests --filter "Category=InventoryStockCount&Category!=SqlServer"` green for the lifecycle tests added in step 11.

11. **Tests, `ErpWeb.Tests/IvStockCountServiceTests.cs`.** Clone the fixture from `IvStockAdjustmentPostingServiceTests.cs`: `SqliteConnection("DataSource=:memory:")` + `TestDbContextFactory` + `InventoryTenantTestHelper.CreateTenantContext()` (default location `"SITE"` while the seeded slice is `"BIN1"`, so line requests must carry `Location = "BIN1"`) + the ten-argument constructor list at lines 425-447. Trait header: `[Trait(TestCategories.Name, TestCategories.Inventory)]` plus a new `[Trait(TestCategories.Name, TestCategories.InventoryStockCount)]` — add that constant to `ErpWeb.Tests/TestCategories.cs` (after `InventoryAdjustment` at line 124) and to the `.vscode/tasks.json` picker.
The control total is `DEMO` / `HQ` / `MAIN` / `BIN1` / `ACTIVE` / `EA` / `RAW`, item `A100`, `StockControl = true`, `LotControl = false` — the same seed, so tests can be copied rather than invented.

**Lifecycle tests to add here** (each maps to a locked decision, so a decision without a test is a missing test): generate with `IncludeZeroQty` false/true; every scope filter is actually applied — warehouse, location, item class, sub-class, item type, status list and the `ICode` IN-list, one case each or one combined assertion set; inactive item excluded; `StockControl = false` item excluded; a `SCRAPS`-status pile excluded by default and present when the status scope asks for it; the `MaxCountLines` refusal; regenerate refused while a `PhysicalQty` exists and allowed with `discardCounts: true` (**D10**); a `COUNTED` sheet refuses both `SaveCountsAsync` and `UpdateAsync` (**I2**); `SaveCountsAsync` with no quantity leaves the sheet DRAFT; `PhysicalQty < 0` refused; `SetItemCountAsync` succeeds for a single-line item and is **refused** for a two-slice item (**D12**); `DeleteAsync` requires DRAFT; `CancelAsync` is terminal and a cancelled sheet refuses count entry (**I5**); a stale `RowVersion` on `UpdateAsync`/`SaveCountsAsync` returns the house concurrency message; `CountNo` is `CC`-prefixed and unique per company/branch.

### Phase 2 — Post and Rollback

12. **Extract the ADJ core method — the only edit to existing engine code.** In `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`, move the body of `PostInventoryADJAsync` (`:812`) into a private `PostInventoryADJCoreAsync(AppDbContext db, string companyCode, string branchCode, string userId, int batchNo, CancellationToken cancellationToken)`, mirroring `PostInventoryMICoreAsync` (`:1406`) exactly. **These must move to the wrapper and must not remain in the core:**

   - the eight `await tx.RollbackAsync(cancellationToken)` calls (the invariant-error path, the missing-`BalLoc` path, the slice-mismatch path, the insufficient-quantity path, and the two stock-move failure paths at `:911`/`:925`) — a core that rolls back has already finished the caller's transaction before the caller tries to save;
   - `await db.SaveChangesAsync(cancellationToken)` and `await tx.CommitAsync(cancellationToken)`.

   The public `PostInventoryADJAsync` keeps its signature and becomes the wrapper: open the context + transaction, call the core, `await tx.RollbackAsync(…)` when `!result.Succeeded`, else `SaveChangesAsync` + `CommitAsync`, then log — the same shape as `PostInventoryMIAsync` (`:1362-1397`). **The `IvTrxTypes.StockAdjustment` check stays hard-coded inside the core**; it must not become a caller-supplied `expectedTrxType`.

   Add two failure-injection hooks next to `TestHookAfterMiStockUpdate` (`:1268`), as `internal Action?`: `TestHookAfterAdjStockUpdate` (after the `IvBalLoc` move, before the history insert) and `TestHookAfterAdjHistory` (after history, before the batch is flagged POSTED). `InternalsVisibleTo("ErpWeb.Tests")` already exists (`ErpWeb.Core.csproj:15`), and this is the house way to prove "both, or neither" — `IvMiscIssuePostingServiceTests:210` is the template. No observable behaviour may change; the untouched `IvStockAdjustmentPostingServiceTests` suite plus the existing hook-based rollback tests are the regression guard.

13. **In-transaction entry point.** Add to `IIvInventoryPostingService` and `IvInventoryPostingService`:

   `Task<IvInventoryPostingBatchResult> PostStockAdjustmentInTransactionAsync(AppDbContext db, string companyCode, string branchCode, string userId, int batchNo, CancellationToken cancellationToken = default)`

   `ArgumentNullException.ThrowIfNull(db)` then delegate straight to `PostInventoryADJCoreAsync`. It mirrors `PostStockOutInTransactionAsync` (`:1276`) and `PostStockInInTransactionAsync` (`:1304`) with one deliberate difference: those two take an `expectedTrxType` because one core serves several transaction types, whereas the ADJ core owns a single type and keeps its own hard check. Document the caller contract in XML docs exactly as those two do — **the caller must already have begun a transaction, the method neither saves nor commits, and on a failed result the caller must roll the transaction back.**

14. **`IvStockCountService.PostAsync` — one transaction.**

   a. Permission `PermissionCodes.Post` on `MenuCodes.InventoryStockCount`; `_tenant.TryWriteScope()` (requires company + branch + location).
   b. Load the header with `RowVersion`, require `COUNTED` or `ROLLED_BACK`, and check **I1** before allocating anything: `COUNTED` ⇒ `PostedBatchNo` is null; `ROLLED_BACK` ⇒ the linked batch is not `POSTED` (otherwise say so and offer `Recover`).
   c. Apply **D9** to `CountDate`: refuse when it is in the future or older than `IvStockCountLimits.MaxBackdateDays`, naming the date and the limit.
   d. Collect lines where `PhysicalQty` is not null (`> 0m` or `== 0m` — a counted zero is meaningful, it writes the pile down to zero). A `PhysicalQty < 0` is a validation failure, not a skip. If none remain, fail with "No counted lines."
   e. Allocate the batch number from `RunningNumberKeys.IvBatch` **inside this transaction** (`RunningNumberService.SaveChangesAsync` enlists in the caller's transaction, so a failed post does not consume a number).
   f. Per line, in `BalLocId` ascending order — the same order the ADJ core locks in, so a concurrent ADJ cannot deadlock against us: `LockBalLocByIdForTenantAsync` (missing row is a **hard failure** — never recreate the slice, legacy semantics); validate the slice identity against the sheet columns (`ICode`, `WHCode`, `LocCode`, `LotNo`, `IStatus`) with `OrdinalIgnoreCase`, failing with the line number; compute `variance = IvQty.Round(locked.StdQty) - IvQty.Round(physicalQty)` (**both operands through `IvQty.Round`** — 4 dp `AwayFromZero`, the same precision the ADJ engine uses); set `isStale = locked.StdQty != line.SystemQty` and count it for **D16**. The core re-locks the same rows on the same connection later, which is a no-op.
   g. Translate the sign into the ADJ dialect: `variance > 0` ⇒ Decrease (`FromBalLocId`, `FrWarehouse`/`FrLocation`/`FrLotNo`/`FrStdUom` from the balance, `FrStdQty = variance`); `variance < 0` ⇒ Increase (`ToBalLocId`, To-side columns, `ToStdQty = |variance|`); `variance == 0` ⇒ skip the line.
   h. Build `IvTrxBatch`: `TrxType = IvTrxTypes.StockAdjustment`, `BatchStatus = IvBatchStatuses.New`, `TrxDtTime = hdr.CountDate` (**D9**), `RefNo = hdr.CountNo` (fits `nvarchar(50)`), `Remarks = "STOCK COUNT " + CountNo` truncated to 250, `LocationCode` from the scope, audit stamps from `ICurrentDateService.Now` and the authenticated user.
   i. Add the details: `TrxLineNo` sequential, `TrxType = ADJ`, `ICode`/`IDesc`/`ProdCode`/`ProdDesc`, `IStatus`, `IClassCode`, `ExpiryDate`, `UnitPrice = locked.UnitPrice ?? item.PurchasePrice ?? 0m` rounded (**D15** — the sheet's `SnapshotUnitPrice` is not used here), `Remarks = IvStockAdjustmentLineInvariant.CombineRemarks(IvAdjustmentReasons.Count, CountNo)`. Insert via `IIvStockTransactionRepository.InsertAsync` (it only `Add`s — the caller saves).
   j. Stamp the header: `Status = POSTED`, `PostedBatchNo`, `PostedBy`/`PostedOn`, `PostedStaleLines` (**D16**), `ModifiedDate`/`ModifiedBy`. Keep any previous `RolledBack*` values — they are the audit trail.
   k. Call `PostStockAdjustmentInTransactionAsync(db, company, branch, userId, batchNo)` and, on a failed result, roll the whole transaction back and return the batch result's message unchanged (it is the operator's diagnosis).
   l. `SaveChangesAsync` then `CommitAsync`, log, return.

15. **All-zero variance.** If every counted line nets to zero, do **not** create a batch: set `Status = POSTED` with `PostedBatchNo = null`, record `PostedStaleLines`, and return a success result stating "No variance found." This is a valid, auditable outcome and it must be distinguishable from "not posted" — which is exactly what **I1** encodes.

16. **`RollbackAsync`.** Requires `POSTED` and a non-null `PostedBatchNo`; call `_posting.RollbackAsync(IvTrxTypes.StockAdjustment, [PostedBatchNo])`; on success set `Status = ROLLED_BACK`, `RolledBackBy`/`RolledBackOn`, `RollbackReason` (required), and leave `PostedBatchNo` in place for the audit trail. For a zero-variance POSTED document (no batch), rollback is a pure status reset to COUNTED. Re-post after rollback allocates a **new** batch number, and the old rolled-back batch stays visible as history (**I1**).

    D8 keeps the **second** rollback route open — the Stock Adjustment list (`/inventory/stock-adjustment`) rolls the same batch back under its own menu permission. That is deliberate, and it is why there are two hard requirements: (1) `Recover` must be able to repair any resulting divergence, and (2) the reconciliation finding in step 24 must *detect* it. Neither may be dropped as "cannot happen".

17. **`PreviewPostAsync`.** Read-only: per line it returns `SystemQty`, live `StdQty`, `PhysicalQty`, variance, direction, `SnapshotUnitPrice`, and an `IsStale` flag; plus totals (increase / decrease / zero / not-counted / stale lines) and the **D9** date check result. This replaces the legacy client-side `CheckUP` confirm and the "0 cost item" `confirm()` dance with a server-computed, structured preview — see the legacy §7.6 defect list for why the original must not be copied. Staleness **warns**; the UI must obtain one explicit confirmation when `StaleLines > 0` before calling `PostAsync` (**D16**).

18. **Posting tests.** Extend the SQLite class (and add the matching cases to the SQL Server class in step 22).

   *Variance and deltas:* decrease variance; increase variance; counted-zero write-down; all-zero ⇒ `POSTED` with `PostedBatchNo = null`; `PhysicalQty` null ⇒ skipped; missing `BalLoc` ⇒ hard failure; slice mismatch ⇒ failure naming the line; permission denied. **Note:** a count line can never decrease a pile below zero — `variance = live − physical` with `physical ≥ 0` always gives `variance ≤ live` — so the "decrease larger than on-hand" case is **not** reachable through the count service. Assert the invariant (counted zero ⇒ exact write-down to 0) here and leave the over-decrease guard to the existing ADJ engine suite, which is where that protection actually lives.
   *Transaction integrity:* with `TestHookAfterAdjStockUpdate`/`TestHookAfterAdjHistory` throwing, assert the header is **not** POSTED and the batch is **not** POSTED and neither stock nor history moved — "both or neither".
   *Audit/immutability:* `PostedStaleLines` is set when a line moved between Generate and Post; a `POSTED` sheet refuses `SaveCountsAsync`, `UpdateAsync`, `DeleteAsync` and `CancelAsync` (**I3**); a `ROLLED_BACK` sheet can be re-counted and re-posted for a **new** batch number while the old batch remains visible (**I1**).
   *Dates:* `CountDate` in the future is refused; `CountDate` older than `MaxBackdateDays` is refused; `CountDate` inside the window posts and the batch `TrxDtTime` equals `CountDate`; a movement **after `COUNTED` but before `Post`** is picked up by the live delta and flagged stale; and — the deliberate D9 consequence — the affected `IvBalLoc.TransDate` is re-dated to `CountDate`, pinned by an assertion so it can never change silently.
   *Rounding:* a `live`/`physical` pair that rounds differently at 4 dp (e.g. `10.12346` vs `10.12344`) produces the variance `IvQty.Round` implies, proving the count engine and the ADJ engine share one precision.
   *Preview:* `IsStale` is true for a line whose live quantity moved; the date check is reported.
   *Regression:* the extraction in step 12 did not change behaviour — the existing `IvStockAdjustmentPostingServiceTests` must stay green **untouched**.

**Verify:** `dotnet build ErpWeb.slnx` clean; `dotnet test --filter "Category=Inventory&Category!=SqlServer"` green with the pre-existing 15-failure baseline unchanged and no new failures.

### Phase 3 — UI (parallel with Phase 2 once the service interface is frozen)

19. **List page.** `ErpWeb.UI/Inventory/Transactions/IvStockCountList.razor`, `.razor.cs`, `.razor.css` at `/inventory/stock-count`, cloned from `IvStockAdjustmentList`. `@inherits PageBase`, `<MenuAuthorize MenuCode="@MenuCodes.InventoryStockCount">`, no `@attribute`. `GridKey="inv-stock-count-list"`, `CommonDataGridEx` with `CustomDataSource`, a `IvStockCountGridDataSource : GridCustomDataSource` declared in the same `.razor.cs` (see `IvStockAdjustmentList.razor.cs:541`), `..AuditColumns.For(startVisibleIndex: n)`, toolbar NEW / POST / ROLLBACK / CANCEL / DELETE gated by `AccessRights.CanAsync(MenuCodes.InventoryStockCount, ...)`, row actions VIEW / EDIT / COUNT. Copy the `ConfirmAction` string + `ConfirmButtonText`/`ConfirmButtonStyle` + `DxPopup` confirm pattern verbatim, including `IvPostingLimits.MaxPostSelection` on POST/ROLLBACK and the status preconditions in `BeginPostAsync` / `BeginRollbackAsync`. Wrap the service call in `BeginBlockingWork` and do not navigate inside the scope.

20. **Document page, four modes in one component.** `ErpWeb.UI/Inventory/Transactions/IvStockCount.razor`, `.razor.cs`, `.razor.css` at `/inventory/stock-count/{Mode:regex(^(new|edit|count|view)$)}` and the same with `/{CountNo}` — cloned from `IvStockAdjustment`, which already uses one regex route with `new|edit|view` and a `_loadedKey = $"{Mode}|{BatchNo}"` guard in `OnParametersSetAsync`. Inventory entry pages do **not** use `<SaDocPage>` (that is Sales/Purchase only); the family convention is local `sa-`/`iv-` chrome.
Mode semantics: `new`/`edit` = header + scope criteria + GENERATE and a read-only grid; `count` = header fields disabled, `PhysicalQty` editable, "Save counts"; `view` = everything read-only, with Edit-from-View. Save navigates back to the list (house convention — do not navigate to the saved document's own edit route).
Scope controls come from `IIvStockCommonRepository.ListActiveWarehousesAsync` / `ListActiveClassesAsync` / `ListActiveSubClassesAsync` / `ListActiveTypesAsync` / `ListActiveStatusesAsync`. Count-entry grid: `PhysicalQty` rendered with an editor inside a `DxGridDataCellTemplate`; the `ValueChanged` handler must be an explicit lambda `(decimal v) => Method(v)` — a bare method group does not convert to `EventCallback<T>`.

   **"Enter by item" — D12, not the legacy waterfall.** The popup searches items and then:

   - the item has exactly **one** count line in this sheet → the entered quantity goes straight to that line and `SetItemCountAsync` performs it;
   - the item has **more than one** count line → the popup lists the slices (warehouse / location / lot / status, with `SystemQty`) and the user must pick one or more; the total is never applied to a slice the user did not name.

   The service refuses a bare item-level write in the multi-slice case — the UI rule alone is not enough. This replaces the legacy "UPDATE ITEM QTY" FIFO waterfall, which the study explicitly says not to port. The item's **total** across slices stays a display value in the popup header; it is never a write target.

   **Volume acceptance criteria** (this is the largest UI risk in the plan): the count grid must stay usable at **500 / 2 000 / 5 000 / 10 000 lines**, with an explicit decision in Phase 0a on paging, virtual scrolling or a plain HTML table — the 10 000 case decides the fallback, it is not a target to optimise for. `MaxCountLines` (D13) is the backstop, not the answer. If the grid pages, the *document* keeps every line in SQL — only the rendered window is bounded.

   **Read-only modes** must not render an editor: `view` shows `PhysicalQty` as text, `new`/`edit` show it blank. The count screen shows `SystemQty`, `PhysicalQty`, variance and the stale marker for every line — the stale marker is what step 17's confirmation is about.

21. **Menu and permission seed.** `scripts/init-inv-stock-count-menu.sql`, copied from `scripts/init-pobsb-menu.sql`: header comment explaining the `menus.xml` coupling; a guarded parent lookup `DECLARE @invTransactionsId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'INV_TRANSACTIONS');`; a guarded menu insert; the `MenuPermission` insert selecting `p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'POST', N'ROLLBACK', N'CANCEL')` with the `NOT EXISTS` guard; the re-activate block (`MenuPermission` has `IsActive`; the role grant table `dbo.RoleMenuPermission` does **not** — it uses `IsAllowed`, and role grants stay with the deployment owner); and a verification SELECT expecting 7 active rows. `MenuSyncService` auto-creates only the ACCESS grant for a newly inserted menu; ADD/EDIT/DELETE/POST/ROLLBACK/CANCEL must come from this script or no grant can ever be effective.

22. **SQL Server concurrency tests.** Put them in a new `ErpWeb.Tests/IvStockCountSqlServerConcurrencyTests.cs` (traits `Inventory` + `InventoryStockCount` + `SqlServer`) rather than bolting onto the shared posting class; follow `IvInventoryPostingSqlServerConcurrencyTests`'s connection resolution and silent-skip helpers. Cases: two concurrent `PostAsync` of the same count ⇒ exactly one succeeds and the second reports the batch already posted; two users saving the same sheet with the same `RowVersion` ⇒ one wins, one gets the concurrency message; a post racing a count edit; a count post racing an MI/DO movement on the same `BalLocId` ⇒ no deadlock escape, one deterministic outcome; a count post racing another ADJ on the same `BalLocId`. Also add the one SQL Server-only model guarantee the SQLite suite cannot prove: `UQ_IvStockCountLine_BalLoc` rejects a duplicate `BalLocId` inside one sheet.

   **Reminder: a green suite is not evidence these ran.** Most SQL concurrency classes return early and are reported as PASSED; only a `Skipped: 0` run with `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` proves it.

**Verify:** `dotnet build ErpWeb.slnx` 0 `error CS`/`RZ`; both scripts applied twice to a scratch database then twice to dev `ERPWeb`, second runs clean; `dotnet test --filter "Category=InventoryStockCount"`.

### Phase 4 — Hardening and documentation

23. **`RecoverAsync` — the one repair path.** Mirrors `SaEInvoiceService.RepairSubmissionAsync`: when the header says POSTED but its `PostedBatchNo` batch is `NEW` (someone rolled it back from the Stock Adjustment list), reset the header to COUNTED and stamp the reason. There must be exactly one repair path; do not add a second synchronisation route. It repairs **status only** — it must not touch the frozen evidence (**I3**).

24. **Reconciliation finding — an acceptance criterion, not a nice-to-have.** Extend `IIvInventoryReconciliationService` with a count-vs-batch consistency check reported as an `IvInventoryReconcileFinding`: (a) header `POSTED` whose linked `IvTrxBatch.BatchStatus` is not `POSTED`; (b) header `POSTED` with `PostedBatchNo` null **and** lines that had a non-zero variance (the all-zero case is legitimate and must not be flagged); (c) header `ROLLED_BACK` whose batch is still `POSTED`. Keep the existing "diagnostic only" doc caveat intact. This is the control that makes **D8** safe: two rollback routes are allowed *because* divergence is detected.

25. **Documentation.** Append an "Implemented in ErpWeb" section to `docs/erp_cyclecount-study.md` (house pattern — a prominent banner or a closing section beats burying it; leave the legacy sections intact as the record), listing the files, the D1/D9 semantic changes, the deliberately-not-ported items (`TransDate <= StartCount` filter, the weight axis, the `CheckUP` dance, the `ICodeTo` range, the "UPDATE ITEM QTY" waterfall, the Adjustment-list-as-rollback-home model), and the verification queries from the study's Appendix 13.2 that apply.

## Relevant files

**Create — model**
- `ErpWeb.Model/Entities/Inventory/IvStockCountHdr.cs`
- `ErpWeb.Model/Entities/Inventory/IvStockCountLine.cs`
- `ErpWeb.Model/Configurations/Inventory/IvStockCountHdrConfiguration.cs`
- `ErpWeb.Model/Configurations/Inventory/IvStockCountLineConfiguration.cs`

**Create — core**
- `ErpWeb.Core/Inventory/IIvStockCountService.cs`
- `ErpWeb.Core/Inventory/IvStockCountService.cs`
- `ErpWeb.Core/Inventory/IvStockCountStatuses.cs`
- `ErpWeb.Core/Inventory/IvStockCountLimits.cs`

**Create — UI**
- `ErpWeb.UI/Inventory/Transactions/IvStockCountList.razor` / `.razor.cs` / `.razor.css`
- `ErpWeb.UI/Inventory/Transactions/IvStockCount.razor` / `.razor.cs` / `.razor.css`

**Create — scripts and tests**
- `scripts/create-iv-stock-count.sql`
- `scripts/init-inv-stock-count-menu.sql`
- `ErpWeb.Tests/IvStockCountServiceTests.cs`
- `ErpWeb.Tests/IvStockCountSqlServerConcurrencyTests.cs`

**Modify**
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs` — extract `PostInventoryADJCoreAsync` from `PostInventoryADJAsync` (`:812`), moving the eight `RollbackAsync` calls and the `SaveChangesAsync`/`CommitAsync` out into the wrapper, and add `TestHookAfterAdjStockUpdate` + `TestHookAfterAdjHistory`; add `PostStockAdjustmentInTransactionAsync` (mirror `:1276`). **The only edit to existing engine code.**
- `ErpWeb.Core/Inventory/IIvInventoryPostingService.cs` — declare the new in-transaction member next to `PostStockOutInTransactionAsync`.
- `ErpWeb.Model/Repositories/Inventory/IvStockCommonRepository.cs` — add `ListStockCountCandidatesAsync` to the interface and the class, after `SearchOnHandPagedAsync` (line 420).
- `ErpWeb.Model/Data/AppDbContext.cs` — two `DbSet`s after line 46.
- `ErpWeb.Core/Numbering/RunningNumberKeys.cs` — new key.
- `ErpWeb.Core/Menus/MenuCodes.cs` — new constant.
- `ErpWeb/Menus/menus.xml` — one row under `INV_TRANSACTIONS`.
- `ErpWeb.Core/CoreServiceCollectionExtensions.cs` — one registration after line 147.
- `ErpWeb.Tests/TestCategories.cs` — `InventoryStockCount` constant.
- `.vscode/tasks.json` — add the new category to the picker.
- `docs/erp_cyclecount-study.md` — implemented section.

**Reuse unchanged (do not duplicate)**
- `ErpWeb.Core/Inventory/IvStockAdjustmentLineInvariant.cs` — `ValidateDetail`, `TryGetDirection`, `GetBalLocId`, `GetSignedDelta`, `GetAbsoluteQty`, `ValidateReasonCode`, `NormalizeReasonCode`, `CombineRemarks`. The only public, reusable ADJ piece.
- `ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs` — `LockBatchForUpdateAsync`, `LockBalLocByIdForTenantAsync` (507), `DecreaseBalLocQtyAsync` (564), `IncreaseBalLocQtyAsync` (611), `LoadDetailsForBatchAsync`, `AddHistory`, `LoadHistoryForBatchAsync`, `HistoryExistsForBatchAsync`. Do not add new lock or history helpers.
- `ErpWeb.Core/Inventory/IvTrxConstants.cs` — `IvTrxTypes.StockAdjustment`, `IvBatchStatuses`, `IvQty.Round`, `IvPostingLimits.MaxPostSelection`, `IvAdjustmentReasons`.
- `ErpWeb.UI/Inventory/Lookups/IvBalLocPicker.razor.cs` — `BalLocId` / `BalLocIdChanged` / `Selected` / `ICodeFilter` / `DisplayLotNo` / `Enabled`.
- `ErpWeb.Core/Services/SqlErrorClassifier.cs` — `IsUniqueViolation` / `IsSerializationConflict`. Do not re-implement `SqlException.Number is 2601 or 2627`.
- Reference only, do not call: `IvStockAdjustmentService` (its validation and `AddDetails` are `private`).

## Verification

1. `dotnet build ErpWeb.slnx --nologo -v:q` → 0 `error CS` / `error RZ`. MSB3027/MSB3021 file-lock errors on `ErpWeb.csproj` are expected while the app is running; build `ErpWeb.Core`, `ErpWeb.UI` and `ErpWeb.Tests` individually in that case. Do not kill the user's running app.
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=Inventory&Category!=SqlServer"` — baseline 246 passing; expect those plus the new domain tests, with the pre-existing 15-failure baseline (9 `SaCustServiceTests` + 4 `PoSupplierServiceTests` + 2 library `InvoiceTypeCode`) unchanged.
3. SQL Server suite: set `ConnectionStrings__SqlServerTestConnection` and `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` in **separate** terminal commands (a `;` on the same line as `dotnet test` silently truncates the connection string), then run `dotnet test` alone. **Require `Skipped: 0`** — most SQL Server classes self-skip and are reported as PASSED, so a green suite can conceal that they never ran.
4. `sqlcmd -E -d ERPWeb -i scripts/create-iv-stock-count.sql` twice → the second run is a clean no-op; confirm both tables, the 4 unique indexes and the FK exist. Repeat for `scripts/init-inv-stock-count-menu.sql` → 7 ACTIVE `MenuPermission` rows.
5. Manual smoke. New count with scope = one warehouse, `IncludeZeroQty` on, and a deliberately seeded zero-qty slice to prove it is generated. Count one slice short and one over, leave one untouched. Post. Then check: `IvBalLoc.StdQty` moved by the variance for exactly the counted lines; `IvTrxHistory` rows in the correct direction with `TrxType = ADJ`, `RefNo = CountNo`; the header POSTED with `PostedBatchNo` set and `PostedStaleLines` populated if anything moved; the batch POSTED. Then Rollback → stock restored, batch back to `NEW`, header `ROLLED_BACK`. Then re-post → a **new** batch number and correct stock, with the old batch still visible.
6. Regression guard for D8: roll the count batch back from the **Stock Adjustment** list (`/inventory/stock-adjustment`), reopen the count, and confirm `Recover` resets it to COUNTED and that re-post works — and that the reconciliation finding reports the mismatch *before* `Recover` is used.
7. Consistency: `PreviewPostAsync` reports a stale line when stock moved between Generate and Post, posting asks for the one confirmation and then proceeds.
8. **D9 / FIFO:** post a count whose `CountDate` is a few days in the past and confirm by query that the affected `IvBalLoc.TransDate` is now `CountDate` (the documented consequence), then confirm a future `CountDate` and one beyond `MaxBackdateDays` are both refused with a readable message.
9. **D10 regeneration:** enter a quantity on a DRAFT sheet, try to regenerate (refused, message names the counted line count), then regenerate with `discardCounts: true` (succeeds, counts gone).
10. **D11 (rev 3) editability:** on a `COUNTED` sheet confirm the counted quantities are still editable and `SaveCountsAsync` succeeds (and `RecountCount` increments), while `UpdateAsync` is still refused; on a `POSTED` sheet confirm `SaveCountsAsync` is refused with the "frozen" message and that the count-entry page explains why instead of offering an editor that always fails.
11. **D12 multi-slice:** count an item that has two piles; confirm "Enter by item" forces a slice choice and that a crafted direct item-level save is refused by the service.
12. **D13 cap:** generate with a scope built to exceed `MaxCountLines` and confirm the refusal message names the number and suggests narrowing the scope.
13. **D14 timestamps:** confirm every stamp is company-local (`ICurrentDateService`) — a browser with a skewed clock must not be able to move one.

## Risks

- **The ADJ extraction is the highest-risk change in the plan, because it touches a shipped, tested engine.** The body being moved contains its own transaction calls; a partial move leaves the caller committing a connection whose transaction was already rolled back. Mitigation is ordering and evidence, not care: do the extraction in Phase 0a (spike item 6), with the eight `RollbackAsync` calls and the `SaveChangesAsync`/`CommitAsync` explicitly accounted for, and require `IvStockAdjustmentPostingServiceTests` + the existing hook-based rollback tests to pass **untouched** before any count code is written.
- **The transaction-integrity test needs a hook that does not exist yet.** `TestHookAfterAdjStockUpdate` / `TestHookAfterAdjHistory` must be added in step 12; without them the claim "header and batch move together, or neither" is not testable, and that claim is the whole reason for the in-transaction entry point.
- **D9 has a real, non-obvious side effect.** `TrxDtTime` is written into `IvBalLoc.TransDate` by both stock-move helpers, and `TransDate` drives FIFO pile ordering and eligibility. A back-dated count therefore re-orders piles for later sales allocation. This is why D9 is bounded by `MaxBackdateDays` and why the effect is a pinned assertion rather than a comment. If the business wants *no* FIFO movement, the single change is `TrxDtTime = ICurrentDateService.Now` — record that as a decision, do not discover it in production.
- **Count-entry inline editing has no precedent in this repository.** No page uses `DxGrid` inline/`EditCell` mode and the only `EditModelSaving` reference is an unused stub. Recommended is a `PhysicalQty` editor inside a `DxGridDataCellTemplate` (component-level binding only, no grid edit mode). Fallbacks, in order: a popup per line exactly like `IvStockAdjustment` (proven, but slow on a large sheet), then a plain HTML table on the count screen only. Decide with the Phase 0a spike before building the whole page, at 2 000 and 5 000 lines.
- **Duplicating validation.** The count service cannot reuse ADJ's private validators. Keep the duplicate minimal and BalLoc-driven: item exists + active + stock-controlled, `BalLocId` belongs to this company/branch (**I6**), slice identity matches, `PhysicalQty >= 0`. Do not copy the warehouse/location/status/class master lookups — the sheet already holds the values read from the balance, and the post-time slice check is the real guard.
- **Two-phase temptation.** Do not "insert the batch, then call the public `PostAsync`". That reintroduces the orphan-`NEW`-batch window and the header/batch desync the design exists to avoid. Step 12/13 is what makes step 14 one transaction.
- **SQLite vs SQL Server.** `AppDbContext.OnModelCreating` sets `RowVersion` to `ValueGenerated.Never` on SQLite and strips filtered indexes, so SQLite tests must stamp `RowVersion` explicitly on service-created rows before an update, and cannot prove uniqueness on the filtered indexes. The `UQ_IvStockCountLine_BalLoc` guarantee therefore needs a SQL Server test, not only a SQLite one.
- **Script hygiene.** `scripts/init-msrunningno.sql` still says `USE ERPLiteEx` (stale — target `ERPWeb`). Keep the new DDL's create step and any later constraint step in separate `GO` batches under `SET XACT_ABORT ON`, and set `QUOTED_IDENTIFIER ON` in the script itself.

## Open items (deliberate, not accidental)

These are the only things left undecided. Each is a Phase 0a question, and each has a stated default so a silent omission is impossible.

1. **`MaxBackdateDays` value.** Default 7. Set `1` if the business wants a count to be postable only on the day it was taken. (`TrxDtTime` semantics themselves are locked in D9.)
2. **Should `IncludeZeroQty` default to `false` on the screen?** Default `true` (legacy parity) with the D13 cap as the guard; flip it if operators routinely generate warehouse-wide sheets they do not need.
3. **The `CANCELLED`-is-terminal round trip (I5).** If operations push back on "miscounted a `COUNTED` sheet ⇒ cancel and start again", the alternative is an audited `ReopenAsync` (`COUNTED → DRAFT`, `EDIT` permission, reason required). That is a one-method addition — but it weakens D11, so it must be a decision, not a convenience.
4. **Paging vs virtual scrolling on the count grid.** Decided by the Phase 0a prototype measurement, not by preference. Note that `IvStockCountLine` is intentionally *not* reserved for a paged solution: the document keeps all its lines in SQL either way.
5. **Do not revisit D17 without a business driver.** A dedicated `StockCount` trx type costs a second `DispatchAsync` branch plus a near-duplicate of the ADJ engine, validation, rollback and test matrix, and buys only reporting segregation.
6. **Out of scope, restated so it is not reopened mid-implementation:** the weight axis, catch-weight, the two `CheckUP` passes, `ICodeTo`, the legacy `Session` working set, ABC scheduling, variance/accuracy reports, and any change to `IIvInventoryReconciliationService`'s "diagnostic only" caveat.

## Implementation gate

Do not write Phase 0 code until every line below is closed in this file. This is the gate the review asked for, and it is what keeps a large UI from being built on unresolved posting semantics.

```
PLAN APPROVED (rev 2)
   |
   +-- D9  CountDate semantics locked (+ MaxBackdateDays value chosen)
   +-- D10 Regeneration rules locked
   +-- D12 Multi-slice "Enter by item" locked
   +-- D13 MaxCountLines locked
   +-- D11 / I1-I6 immutability + idempotency invariants locked
   +-- D15 SnapshotUnitPrice semantics documented
   +-- D14 timestamp + timezone semantics documented
   +-- Phase 0a grid prototype measured (2 000 / 5 000 lines)
   +-- Phase 0a ADJ extraction proven inert (existing suite untouched, green)
   |
   v
IMPLEMENT  (Phase 0 -> 1 -> 2 -> 3 -> 4)
```
