# Stock Transfer Implementation Plan v8 — ErpWeb (this repository)

**Status:** implementation-ready spec for this codebase (review comments folded in).  
**Supersedes:** `stock_transfer_implementation_plan_v7.md` (wrong architecture — do not implement).

Related:

- [inventory-trx-pattern.md](inventory-trx-pattern.md) — clone + delta for inventory screens
- [inventory-stock-lot.md](inventory-stock-lot.md) — `IvLot` / `IvBalLoc` / `IvTrxHistory`
- [inventory-posting-explain.md](inventory-posting-explain.md) — how MI posting locks and mutates stock

---

## 1. Verdict from repository inspection

v7 assumed `InventoryDocument`, `StockMovementAllocation`, `StockLedger`, `StockBalance`, `ItemCost`, and `inventory_new_design_v3.2.md`. **None of those exist here.**

This system already has:

| Job | Actual type | Table |
| --- | --- | --- |
| Document header | `IvTrxBatch` | `IvTrxBatch` |
| Document line | `IvTrxBatchDetail` | `IvTrxBatchDetail` |
| On-hand pile | `IvBalLoc` | `IvBalLoc` |
| Posted ledger | `IvTrxHistory` | `IvTrxHistory` |
| Lot identity | `IvLot` | `IvLot` |
| Tenant | `IInventoryTenantContext` | claims: `CompanyCode` / `BranchCode` |

Transfer is already named:

```csharp
IvTrxTypes.StockTransfer = "TR"
```

Posting `DispatchAsync` currently handles only `MR` and `MI`. `TR` is not implemented.

**Do not create a second inventory engine.** Stock Transfer is a document type inside the existing engine.

---

## 2. Locked decisions (do not reopen while coding)

These replace v7’s unresolved gates.

| Topic | Decision |
| --- | --- |
| Document type | `TR` (existing constant). Never invent `ST`. |
| Status | `NEW` → `POSTED`. Draft cancel = `CANCELLED`. Undo posted = **Rollback** to `NEW` (same as MI/MR). No compensating reversal document. |
| Numbering | Shared `RunningNumberKeys.IvBatch` (`IV_BATCH`). Integer `BatchNo`. UI must not allocate. |
| Tenant | `IInventoryTenantContext.TryWriteScope()`. Never trust client company/branch. |
| User `LocationCode` | Tenant site claim. Stamp on batch like MI/MR. **Not** `IvLocation.LocCode`. |
| Line model | Flat `IvTrxBatchDetail`: source `FromBalLocId` **and** destination `ToWarehouse` / `ToLocation` on the **same** row. |
| 1 source → N dest | Multiple detail lines sharing the same `FromBalLocId`. Posting **aggregates** source decrease. No allocation table. |
| Multiple source piles | A document **may** contain many different `FromBalLocId` values (e.g. A→X, A→Y, B→Z). Each pile is aggregated independently. Do not require one source pile per document. |
| Same-slice compare | Reject source == dest using the **complete** `IvStockSliceKey` (all seven fields). Not warehouse + location only. |
| Post-lock source check | After locking `FromBalLocId`, re-read the authoritative `IvBalLoc` row and verify identity + `StdUom` match staged source. Mismatch → fail whole transaction. |
| Dest pile creation | Use existing `FindOrCreateBalLocAsync` only. Never insert `IvBalLoc` from TR code. `UQ_IvBalLoc_StockSlice` is authoritative. |
| Rollback integrity | Validate history ↔ detail line pairing **before** any stock mutation. Failure → no qty change, no history delete, batch stays `POSTED`. |
| UOM V1 | Same std UOM only. `FrStdQty == ToStdQty`. No conversion engine exists (`MsUom` is a code master). BOX→PCS is deferred. |
| Cost V1 | See Section 5.5. Copy source `Cost` / `UnitPrice` to history always. Dest overwrite only when pile missing or `StdQty = 0`. Cost mismatch on non-empty dest does **not** block transfer. No `ItemCost` / MAV. |
| Lots V1 | Reject `LotControl` items and any source pile with a non-empty `LotNo`. Lot-preserving transfer is Phase 2. |
| Lot split / repack | Out of scope. Separate document later. |
| Periods | No inventory-period table. Do not invent closed-period checks. |
| Idempotency | Same as MI/MR: lock batch; reject if not `NEW` or if history exists. Do not return a fake success on double-post. |
| Deadlock | Existing `UPDLOCK, HOLDLOCK` + `IvStockSliceKey` sort. Catch `DbUpdateConcurrencyException` like today. Do not add a 3-attempt retry loop just for TR. |
| Permissions | `ACCESS`, `ADD`, `EDIT`, `DELETE`, `POST`, `ROLLBACK`, `CANCEL`. Cost columns gated by `VIEW_COST` if shown. |
| Schema | **No new tables.** Use existing From/To columns on detail + history. |
| Shared MI/MR behaviour | Do not change unless a test proves a shared bug. Any shared change needs regression tests + human approval. |

---

## 3. Actual mapping (Gate A — done)

### 3.1 Stock grain

`UQ_IvBalLoc_StockSlice`:

```text
CompanyCode + BranchCode + ICode + WhCode + LocCode + LotNo + IStatus
```

Empty loc/lot/status is `""`, never null. `IvStockSliceKey` is the lock-order type.

### 3.2 Line ownership

`IvTrxBatchDetail` already has both directions:

- Source: `FromBalLocId`, `FrWarehouse`, `FrLocation`, `FrLotNo`, `FrStdQty`, `FrStdUom`, `FromLotId`
- Dest: `ToBalLocId`, `ToWarehouse`, `ToLocation`, `ToLotNo`, `ToStdQty`, `ToStdUom`, `ToLotId`

MI uses only From\*. MR uses only To\*. **TR uses both.**

### 3.3 Ledger

One `IvTrxHistory` row per detail line. Copy both Fr\* and To\*. Stamp `FromBalLocId` / `ToBalLocId` (and lot IDs) **at post**, not at save.

`IvInventoryReconciliationService` already nets:

- `ToBalLocId` += `ToStdQty`
- `FromBalLocId` -= `FrStdQty`

TR history with both FKs filled will reconcile without a new reconciliation model.

### 3.4 Save vs post (follow live MI/MR, not the older staging note)

| When | FromBalLocId | ToWarehouse / ToLocation | ToBalLocId / lot IDs | IvBalLoc qty | History |
| --- | --- | --- | --- | --- | --- |
| Save `NEW` | set (like MI) | set (like MR) | null | unchanged | none |
| Post | kept | kept | set | decrease source, increase dest | insert |
| Rollback | kept for re-post | kept | clear dest IDs | reverse qty | **delete** (same as MI/MR) |

Draft does not reserve stock.

### 3.5 Locking / isolation

- `BeginTransactionAsync()` — default isolation (do not change globally).
- Batch: `UPDLOCK, HOLDLOCK` via `LockBatchForUpdateAsync`.
- Piles: `LockBalLocByIdForTenantAsync` (source) and `LockBalanceSlicesAsync` / `FindOrCreateBalLocAsync` (dest).
- Decrease: `DecreaseBalLocQtyAsync` (SQL only if qty still sufficient).
- Increase: `IncreaseBalLocQtyAsync` or in-memory add after dest lock (same as MR).
- Lock order: sort **all** affected slices (sources **and** destinations) with `GetOrderedStockSlices` / `IvStockSliceKey`.
- **Concurrent destination creation:** concurrent transfers targeting the same new destination slice must not create duplicate `IvBalLoc` rows. Use existing `FindOrCreateBalLocAsync` (lock-then-insert, unique-violation re-read on `UQ_IvBalLoc_StockSlice`). TR posting code must not bypass this helper with direct inserts.

### 3.6 Numbering / audit

- Allocate `BatchNo` in the save transaction with `IRunningNumberService.GetNextAsync(..., RunningNumberKeys.IvBatch)`.
- Header already has `PostedBy` / `PostedDate` / `PostingOperationId` / `RollbackBy` / `RollbackDate` / `RollbackOperationId` / `CreatedBy` / `CreatedDate`.
- Do not add ST-specific audit tables.

---

## 4. Scope

### V1 (this spec)

- Same company + same branch
- Warehouse → warehouse
- Location → location (same or different warehouse)
- Same std UOM
- One source pile → many destinations (many lines)
- Multiple source piles per document (each aggregated independently)
- Atomic OUT + IN
- Non-lot items only
- Copy source cost/price
- List + document UI, post, rollback, cancel, delete

### Deferred (do not implement)

- UOM conversion (BOX → PCS)
- Warehouse-level moving average / `ItemCost`
- Closed inventory periods
- Cross-branch transfer
- Lot-controlled items / `LotBalance`
- Lot split / repack
- Compensating reversal document
- Serials, reservation, GL, FIFO, landed cost
- Auto-create `MAIN` location on warehouse insert
- Document numbers like `ST-000001`

---

## 5. Business rules (V1)

### 5.1 Quantity

- UI quantities are positive. Posting owns the sign (source −, dest +).
- Reject qty ≤ 0.
- Round with `IvQty.Round` (4 dp, AwayFromZero).
- On each line: `FrStdQty == ToStdQty` (same UOM).
- UOM = source pile `StdUom` (fallback item `StdUom`). Dest UOM must match.

### 5.2 Source and destination

- Source is an existing `IvBalLoc` in current company/branch (`FromBalLocId`).
- Item must be active, `StockControl = true`, `LotControl = false`.
- Source `LotNo` must be `""`.
- Dest warehouse: exists, active, current company + branch.
- Dest location: if the warehouse has active locations, location is required and must belong to that warehouse; otherwise location = `""`.
- Preserve source `IStatus` on the destination slice. Transfer does not change item status.
- Reject when source slice equals dest slice. Compare the **complete** `IvStockSliceKey`:

```text
CompanyCode + BranchCode + ICode + WhCode + LocCode + LotNo + IStatus
```

Do not compare warehouse + location only. Same warehouse/location with a different `IStatus` is a **different** stock slice and is allowed.

- Reject dest warehouse/location on another branch (tenant query already scopes; still validate warehouse row).

### 5.3 Multi-destination (one source pile)

Example: 24 EA from WH-A/A01 to three bins.

Three `IvTrxBatchDetail` rows, **same** `FromBalLocId`:

| Line | From | FrStdQty | To | ToStdQty |
| --- | --- | ---: | --- | ---: |
| 1 | WH-A/A01 | 10 | WH-A/A02 | 10 |
| 2 | WH-A/A01 | 8 | WH-B/B01 | 8 |
| 3 | WH-A/A01 | 6 | WH-B/B02 | 6 |

Posting decreases source **once** by 24, not three times by 24.

### 5.3a Multiple source piles

A single document may contain **different** `FromBalLocId` values. Example:

```text
Source A → Destination X
Source A → Destination Y
Source B → Destination Z
```

Each `FromBalLocId` is aggregated independently for source decrease. Do not require one source pile per document.

### 5.3b UI available quantity (advisory only)

UI available qty is **informational only** (source on-hand minus other lines on the same `FromBalLocId`, same pattern as MI `PopupMaxIssueQty`).

It must **never** be used as the authoritative quantity during posting. Posting must always lock and re-read `IvBalLoc.StdQty` inside the database transaction. Never trust client-supplied available qty.

### 5.4 Same-source merge

Do **not** merge lines on save. Keep one row per destination (audit). Only posting aggregates by `FromBalLocId` / dest slice.

If two lines hit the same dest slice, aggregate dest increase as well (same as MR).

### 5.5 Destination costing (V1)

- If destination pile does **not** exist: create it using source `Cost` / `UnitPrice`.
- If destination pile exists and `StdQty = 0`: set `Cost` / `UnitPrice` from source.
- If destination pile exists and `StdQty > 0`: **do not overwrite** existing `Cost` / `UnitPrice`.
- If destination `StdQty > 0` and source `Cost` / `UnitPrice` **differs** from destination `Cost` / `UnitPrice`, the transfer is **still allowed**. Do not treat the cost difference as a validation failure. Existing destination cost remains unchanged.
- `IvTrxHistory` always records source `Cost` / `UnitPrice` for the transferred quantity.
- V1 does not calculate MAV or any warehouse-level blended cost.

---

## 6. Posting proof (actual entities)

Scenario: 24 EA, source WH-A / A01, three destinations. Item not lot-controlled. Source cost RM10.

### Document

```text
IvTrxBatch
  TrxType      = TR
  BatchStatus  = NEW → POSTED
  BatchNo      = <IV_BATCH>
  CompanyCode / BranchCode from IInventoryTenantContext
```

### Lines (staging)

```text
IvTrxBatchDetail × 3
  FromBalLocId = <source pile id>
  FrWarehouse / FrLocation / FrStdQty / FrStdUom
  ToWarehouse / ToLocation / ToStdQty / ToStdUom
  ToBalLocId = null until post
```

### After post — on-hand

```text
IvBalLoc source WH-A/A01  StdQty −24
IvBalLoc dest  WH-A/A02  StdQty +10   (find or create)
IvBalLoc dest  WH-B/B01  StdQty +8
IvBalLoc dest  WH-B/B02  StdQty +6
```

### After post — history (3 rows, both directions)

| TrxType | FrWarehouse | FrLocation | FrStdQty | ToWarehouse | ToLocation | ToStdQty | FromBalLocId | ToBalLocId |
| --- | --- | --- | ---: | --- | --- | ---: | --- | --- |
| TR | WH-A | A01 | 10 | WH-A | A02 | 10 | source | dest A02 |
| TR | WH-A | A01 | 8 | WH-B | B01 | 8 | source | dest B01 |
| TR | WH-A | A01 | 6 | WH-B | B02 | 6 | source | dest B02 |

Conservation:

```text
SUM(FrStdQty) = SUM(ToStdQty) = 24
source decrease = SUM(FrStdQty) aggregated by FromBalLocId = 24
```

There is **no** `StockMovementAllocation`. Traceability is `BatchNo` + `TrxLineNo` on history, plus both BalLoc FKs.

Cost V1 (see Section 5.5): history always gets source `Cost` / `UnitPrice`. Destination pile gets source cost only when new or `StdQty = 0`. Non-empty destination cost is never overwritten, even when source cost differs.

---

## 7. Posting algorithm

Add `PostInventoryTRAsync` / `RollBackInventoryTRAsync`. Movement rules differ from both MI and MR, so **do not** dispatch TR into those methods.

Wire `DispatchAsync`:

```text
TR + post     → PostInventoryTRAsync
TR + rollback → RollBackInventoryTRAsync
menu          → MenuCodes.InventoryStockTransfer
```

Keep `MaxPostSelection = 10`.

### PostInventoryTRAsync

```text
BEGIN TRANSACTION
1. Lock batch (company + branch + batchNo)
2. Must be TrxType TR and status NEW
3. Load details; reject empty
4. Reject if history already exists
5. Lock item masters
6. Build a plan per line:
     FromBalLocId, FromSlice, ToSlice, qty, UOM, IStatus
     validate item / lot / warehouse / location / qty / same-slice
7. Aggregate decrease by FromBalLocId
8. Aggregate increase by ToSlice
9. Collect all slices (from + to), sort with GetOrderedStockSlices
10. Lock source piles by id; re-read the authoritative IvBalLoc row inside the
    transaction and verify identity + StdUom match staged source:
    CompanyCode, BranchCode, ICode, WhCode, LocCode, LotNo, IStatus, StdUom
    Do not rely on the previously loaded entity as authoritative state.
11. Find-or-create dest piles via FindOrCreateBalLocAsync only (non-lot: LotId null, LotNo "")
12. Re-read source StdQty after lock; reject insufficient
13. Decrease each source (DecreaseBalLocQtyAsync)
14. Increase each dest; apply dest cost rules (Section 5.5)
15. Stamp detail FromLotId/ToLotId/ToBalLocId
16. Insert one IvTrxHistory per line (Fr* and To*; history cost from source)
17. Mark batch POSTED; set PostedBy/PostedDate/PostingOperationId
18. COMMIT

On any line failure: ROLLBACK entire batch (no partial source decrease,
no partial dest increase, no committed dest pile, no history, batch stays NEW)
```

UI / document service must not write `IvBalLoc.StdQty`. Only `IvStockPostingRepository` / posting engine.

### RollBackInventoryTRAsync

```text
BEGIN TRANSACTION
1. Lock batch; must be TR + POSTED
2. Load details + history
3. Integrity-check BEFORE any stock mutation (clone ValidateMiHistoryIntegrity pattern):
     History.BatchNo == BatchNo
     History.TrxType == TR
     History.TrxLineNo == Detail.TrxLineNo
     History.FromBalLocId == Detail.FromBalLocId
     History.ToBalLocId == Detail.ToBalLocId
     History.FrStdQty == Detail.FrStdQty
     History.ToStdQty == Detail.ToStdQty
     1:1 line pairing, no duplicate TrxLineNo, tenant match
   On integrity failure: ROLLBACK — no stock change, no history delete, batch stays POSTED
4. For each history row: dest decrease ToStdQty, source increase FrStdQty
5. Aggregate per BalLocId; lock all slices in deterministic order
6. Reject if dest would go negative (stock already moved on) — fail atomically; no source restore
7. Apply qty changes
8. Remove history rows
9. Clear ToBalLocId / ToLotId / FromLotId on details (keep FromBalLocId and Fr*/To* codes)
10. Status NEW; set Rollback* fields
11. COMMIT
```

Do not UPDATE/DELETE posted history qty; rollback **deletes** history rows like MI/MR. That is this engine’s undo, not v7’s compensating document.

---

## 8. Document service

Clone MI service shape (`IIvMiscIssueService`), not MR.

Methods: Peek, lookups, Search, Get, SaveNew, Update, Delete, Cancel, Post, Rollback.

Deltas vs MI:

- `TrxType = IvTrxTypes.StockTransfer`
- Menu `MenuCodes.InventoryStockTransfer`
- Line request includes `ToWarehouse`, `ToLocation` (and dest UOM = source UOM)
- Save writes both Fr\* and To\* qty/UOM (`FrStdQty = ToStdQty = rounded qty`)
- Validate dest warehouse/location like MR
- Reject lot-controlled items and non-empty source `LotNo`
- Reject source slice == dest slice
- `PostAsync` / `RollbackAsync` call `_posting.PostAsync("TR", …)` / `RollbackAsync("TR", …)`
- Lookups: reuse `IIvInventoryLookupService` + `IvBalLocPicker` for source; warehouse/location lists for dest (MR pattern)

Error text: “stock transfer”, not “miscellaneous issue”.

---

## 9. UI

Third family: **from and to**. Clone **MI chrome** (list, modes, post/rollback/cancel, dirty check). Add destination fields from **MR**.

Do not build a two-grid parent/allocation UI in V1. One line grid; each row is one source→dest movement. Multiple rows may share a source pile.

### Identity

| Item | Value |
| --- | --- |
| TrxType | `IvTrxTypes.StockTransfer` (`TR`) |
| Menu | `INV_STOCK_TRANSFER` |
| Routes | `/inventory/stock-transfer`, `/inventory/stock-transfer/{new\|edit\|view}`, `/inventory/stock-transfer/{new\|edit\|view}/{BatchNo}` |
| CSS prefix | `tr-` |
| GridKey | `inv-stock-transfer-list` |
| Titles | Stock transfer |

### Clone file list

From MI:

| Role | Copy from | New path |
| --- | --- | --- |
| List UI | `ErpWeb.UI/Inventory/Transactions/IvMiscIssueList.razor` | `IvStockTransferList.razor` (+ `.cs` / `.css`) |
| Document UI | `ErpWeb.UI/Inventory/Transactions/IvMiscIssue.razor` | `IvStockTransfer.razor` (+ `.cs` / `.css`) |
| DTOs + interface | `ErpWeb.Core/Inventory/IIvMiscIssueService.cs` | `IIvStockTransferService.cs` |
| Document service | `ErpWeb.Core/Inventory/IvMiscIssueService.cs` | `IvStockTransferService.cs` |
| Tests | `ErpWeb.Tests/IvMiscIssuePostingServiceTests.cs` | `IvStockTransferServiceTests.cs` (CRUD + validation + post/rollback) |

Always touch:

| Role | Path |
| --- | --- |
| Menu constant | `ErpWeb.Core/Menus/MenuCodes.cs` → `InventoryStockTransfer = "INV_STOCK_TRANSFER"` |
| Menu XML | `ErpWeb/Menus/menus.xml` under `INVENTORY`, SortOrder 10 |
| DI | `ErpWeb.Core/CoreServiceCollectionExtensions.cs` |
| Posting | `IvInventoryPostingService.DispatchAsync` + `PostInventoryTRAsync` / `RollBackInventoryTRAsync` |
| Trx type | reuse `IvTrxTypes.StockTransfer` — do not add a second code |

Reuse `IvBalLocPicker` / `IvBalLocSearchPopup` for source. Do not put posting SQL in the Blazor page.

### Document page extras vs MI

- Columns: From WH/Loc, To WH/Loc, Qty, UOM, Status
- Popup: source picker (MI) + dest warehouse + dest location (MR)
- Show remaining available on the selected source pile (advisory only — posting re-reads locked `IvBalLoc.StdQty`)
- No reason dropdown required unless you copy MI reason for remarks only — prefer omit `IvTrxReasons` unless product wants it
- Hide cost unless `VIEW_COST`

---

## 10. Files that must not appear

Do not add:

- `InventoryDocument`, `StockMovementAllocation`, `StockLedger`, `StockBalance`, `ItemCost`, `LotBalance`
- `ICompanyContext` (use `IInventoryTenantContext`)
- A second `DbContext`
- Transfer-specific ledger or balance tables
- New running-number key

---

## 11. Acceptance tests

### CRUD / UI contract (service tests)

- [ ] Save allocates `IV_BATCH` once; peek does not consume
- [ ] Tenant mismatch cannot load another company/branch batch
- [ ] Only `NEW` can edit / delete / cancel / post
- [ ] Posted cannot edit
- [ ] Cancel: `NEW` → `CANCELLED`, no stock change
- [ ] Delete only `NEW`

### Validation

- [ ] No lines → cannot save
- [ ] Qty ≤ 0 rejected
- [ ] Missing `FromBalLocId` rejected
- [ ] Missing dest warehouse rejected
- [ ] Location required when warehouse has locations
- [ ] Location belonging to another warehouse rejected
- [ ] Inactive warehouse rejected
- [ ] Same source and dest slice rejected
- [ ] Lot-controlled item rejected
- [ ] Source pile with `LotNo` rejected
- [ ] Non-stock-controlled item rejected
- [ ] Dest warehouse on another branch rejected

### Posting

- [ ] Draft does not change `IvBalLoc`
- [ ] WH-A → WH-B same location name still moves piles (slice includes warehouse)
- [ ] Same warehouse, loc A → loc B
- [ ] 1 source → 3 dest: source −24, dests +10/+8/+6
- [ ] Source OUT is aggregated (not −24 three times)
- [ ] Two lines to the same dest slice: dest +sum
- [ ] History: 1 row per line, both Fr\* and To\* and both BalLoc FKs
- [ ] `FrStdQty == ToStdQty` on every history row
- [ ] Insufficient source qty fails whole batch; no partial dest create that commits
- [ ] Stale UI available qty cannot bypass post-time lock
- [ ] Double-post: second fails (not `NEW` or history exists); no duplicate history
- [ ] Concurrent two posts of overlapping source: one succeeds, one insufficient / concurrency
- [ ] Cost/UnitPrice copied from source pile
- [ ] Dest `IStatus` equals source `IStatus`
- [ ] Non-lot dest: `LotId` null, `LotNo` `""`
- [ ] Does not call `FindOrCreateLotAsync`

### Rollback

- [ ] Restores source qty and dest qty
- [ ] History removed
- [ ] Batch `NEW` and can be posted again
- [ ] Fails if dest qty no longer covers the transfer (stock moved on)

### 8.1 Multiple source piles

- [ ] Two source piles can be transferred in one document
- [ ] Source quantities are aggregated independently by `FromBalLocId`

### 8.2 Same complete stock slice

- [ ] Same complete `IvStockSliceKey` source/destination is rejected
- [ ] Same warehouse/location but different `IStatus` remains a different stock slice (allowed)

### 8.3 Destination cost

- [ ] New destination receives source `Cost` / `UnitPrice`
- [ ] Empty existing destination (`StdQty = 0`) receives source `Cost` / `UnitPrice`
- [ ] Non-empty destination `Cost` / `UnitPrice` is not overwritten
- [ ] Destination with `StdQty > 0` and different source cost still posts; dest cost unchanged
- [ ] History records source `Cost` / `UnitPrice`

### 8.4 Concurrent destination creation

- [ ] Concurrent transfers targeting a new identical destination slice do not create duplicate `IvBalLoc` rows
- [ ] Final destination quantity equals the sum of successful transfers

### 8.5 Source identity after lock

- [ ] Source identity mismatch after lock fails the whole transaction
- [ ] Source `StdUom` mismatch after lock fails the whole transaction

### 8.6 Rollback integrity

- [ ] Missing/mismatched history causes rollback to fail atomically
- [ ] Failed rollback leaves batch `POSTED` and leaves stock/history unchanged

### 8.7 Partial destination movement

- [ ] Rollback fails atomically when only part of the transferred destination quantity remains
- [ ] No source restoration occurs if rollback cannot fully reverse the transfer

### 8.8 Full-batch atomicity

- [ ] Given a 3-line transfer where line 3 is invalid: no source qty change, no dest qty change, no committed dest pile, no history rows, batch stays `NEW`, entire transaction rolls back (proves later-line failure cannot partially commit earlier lines)

### 8.9 Concurrent overlapping transfer

- [ ] Transfer A and Transfer B both move Source X → Destination Z concurrently
- [ ] Source quantity cannot become negative; no lost update
- [ ] No duplicate destination `IvBalLoc` slice
- [ ] Successful transaction reflects correct quantity; competing transaction fails/waits per existing locking
- [ ] No partial history

### Regression

- [ ] Existing MI post/rollback tests still pass
- [ ] Existing MR post/rollback tests still pass
- [ ] Reconciliation still nets From− and To+ (add a TR case)

---

## 12. Implementation order

Stop after each phase for human review. Do not implement the whole module in one pass.

### Phase TR-1 — Posting (no UI)

`DispatchAsync` + `PostInventoryTRAsync` + `RollBackInventoryTRAsync` + tests with existing test factory pattern (`IvMiscIssuePostingServiceTests`).

Prove the 24 EA split and rollback.

### Phase TR-2 — Document service

`IIvStockTransferService` / `IvStockTransferService` + DI + validation tests.

### Phase TR-3 — Menu + UI

Menu constant, `menus.xml`, list + document razor (clone MI, dest fields from MR).

### Phase TR-4 — Regression

Run `ErpWeb.Tests` inventory tests. Confirm MI/MR unchanged.

---

## 13. AI coding agent instructions

1. Read this v8 spec and `inventory-trx-pattern.md`. Ignore v7 entity names.
2. Inspect live MI/MR/posting before adding classes.
3. Reuse `IvTrxTypes.StockTransfer` (`TR`).
4. Use `IInventoryTenantContext`, never client company/branch.
5. Do not mutate `IvBalLoc` from UI or the document service.
6. Do not modify posted history qty; rollback deletes history like MI/MR.
7. Do not add tables or MAV/UOM-conversion engines.
8. Do not treat `ICurrentUserService.LocationCode` as a warehouse bin.
9. Do not implement lot transfer in V1.
10. Do not dispatch TR into `PostInventoryMIAsync` / `PostInventoryMRAsync`.
11. Preserve MI/MR behaviour; add TR tests plus existing inventory tests.
12. Clone, don’t extract a shared document base class.
13. Implement only the current phase.
14. UI available quantity is advisory only; posting must re-read locked `IvBalLoc.StdQty`.
15. Do not overwrite `Cost` / `UnitPrice` of a non-empty destination pile; cost mismatch does not block transfer.
16. Verify complete `IvStockSliceKey` identity after locking source piles (authoritative re-read inside transaction).
17. Follow existing `FindOrCreateBalLocAsync` concurrency behavior; never create duplicate stock slices.
18. Rollback must validate document/history integrity before changing stock.

---

## 14. Phase 2 (later — not this work)

Lot-aware transfer, matching `inventory-stock-lot.md`:

```text
FromLotId == ToLotId
FromBalLocId != ToBalLocId
```

Same `LotNo`. Do not create a new `IvLot` because warehouse/location changed. Do not add a `LotBalance` table; qty stays on `IvBalLoc`.

UOM conversion and MAV costing remain separate projects.

---

## 15. Ready-for-implementation checklist

- [x] Actual schema inspected (`IvTrxBatch` / Detail / `IvBalLoc` / `IvTrxHistory` / `IvLot`)
- [x] `StockMovementAllocation` does not exist — use multiple detail lines
- [x] 1 source → many dest proven with aggregated `FromBalLocId`
- [x] Ledger = one history row per line with both Fr and To
- [x] Stock grain = existing unique slice (includes `IStatus`)
- [x] Numbering = `IV_BATCH`
- [x] Undo = rollback, not reversing document
- [x] Tenant = `IInventoryTenantContext`
- [x] No second inventory architecture
- [x] V1 excludes lots, UOM conversion, MAV, periods, cross-branch
- [x] Destination cost overwrite rule defined
- [x] Cost mismatch on non-empty dest does not block transfer
- [x] Multiple source piles explicitly supported
- [x] Full `IvStockSliceKey` comparison defined
- [x] Source identity + `StdUom` revalidated after lock (authoritative re-read)
- [x] Destination creation uses existing `FindOrCreateBalLocAsync`
- [x] Rollback validates history integrity before mutation
- [x] Rollback failure is atomic
- [x] Full-batch posting atomicity tested (8.8)
- [x] Concurrent overlapping transfer tested (8.9)

---

## 16. TR V2 — Lot split / new destination lots

**Scope:** Lot-controlled source lines; each destination line carries a **new** `ToLotNo` (manual entry or Split dialog). Post creates `IvLot` (create-only), increases dest `IvBalLoc` with `LotId`, decreases source. Not same-lot WH move (§14). Not RS/repack as separate trx.

**Locked rules (BR-1 … BR-8):** See implementation plan `tr_lot_split_ui` — summary:

| BR | Rule |
|----|------|
| BR-1 | Lot lines: `FrLotNo` + `ToLotNo` required; `ToLotNo` ≠ `FrLotNo`; batch uniqueness case-insensitive; non-lot: both lot fields empty |
| BR-2 | Dest lots create-only; never `FindOrCreateLotAsync` for TR dest; reject existing at UI/save/post/DB unique index |
| BR-3 | `IvLotNumberGenerator.AllocateAsync`; auto `yyMMdd` + seq 001–999; skip collisions |
| BR-4 | All lot/bal/history/post changes in one DB transaction; any failure rolls back entirely |
| BR-5 | Equal split uses `IvQty.Scale`; last line gets remainder |
| BR-6 | Save: new line → lot must not exist; unchanged `ToLotNo` on edit OK; changed → new must not exist |
| BR-7 | After Split.Process, lines are independent (no parent/child) |
| BR-8 | `PostInventoryTRAsync` catches unique `IvLot` violation on `SaveChanges`, rolls back, returns business message (not `DispatchAsync`) |

**Post:** `SourceType = TR`, `SourceDocNo = batchNo`; `FromLotId` from source `IvBalLoc`; `ToLotId` from newly staged dest lot.

**UI:** Split button on lot-controlled grid rows; dialog → N lines with generated `ToLotNo`; add/edit popup has dest lot + Generate.

**Out of scope:** UOM conversion, expiry on split, v8 §14 same-lot move, reusing existing dest lots.
