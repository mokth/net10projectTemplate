# Subcon Phase 1 — Exact-file implementation proposal

**Status:** **HOLD — NO IMPLEMENTATION.** This file is the v6 Phase 0 deliverable that names files, schema, and seams. It is **not** approval to add them.

**HEAD this proposal is based on:** `1b2d70618ce44d5db0f13ea14d78697dbc27ea5f`  
**Readiness:** [phase0-source-readiness-matrix.md](phase0-source-readiness-matrix.md) — Inventory 2I **BLOCKED**, Production 2P **BLOCKED**.

**If Phase 1 is later approved, it remains class A/B:** menus, draft documents, PO deep-link, feature flags **default OFF**, **zero** stock/WIP/ledger posting. Post buttons stay disabled with the Blocked reasons from the matrix.

---

## 1. What Phase 1 may do (only after explicit approval)

- One Subcon menu family and permissions.
- Workbench / Send Out / Receive Back / Inquiry **shells** with empty/disabled posting.
- Inventory-only hide of WO; Production source radio disabled with reason.
- Supplier picker via existing `PoSupplierPicker`.
- Optional read-only link to an existing service PO line; `Create PO Draft` navigates to `PoOrder` (no approval bypass).
- Minimum **draft** tables (company/branch, rowversion, unique keys). Enable flag default **OFF**.
- Tests: permissions, tenancy, dirty-draft retain, Inventory-only UX, **assert no `IvBalLoc` / `ProductionBalLot` / history mutation**.

## 2. What Phase 1 must not do

- Call `IIvStockTransferService.PostAsync` / `RollbackAsync` or any inventory/production poster.
- Add `PostStockTransferInTransactionAsync` (that is a **separate protected-engine request**, §6).
- Filter-change Sales/MI/Production/count consumers “just to make a demo warehouse work”.
- Enable lot-controlled send-out, Production mode, 2M, conversion, fee capitalization.
- Turn on `OpenPeriodBackdatedReplayEnabled`.
- Show a clickable Post that silently no-ops.

---

## 3. Proposed new files (Phase 1 shell)

Namespaces follow live patterns (`ErpWeb.UI/Quality`, `ErpWeb.Core/Quality`). **One** Subcon tree, not Inventory-Subcon + Production-Subcon.

### 3.1 UI

| File | Role |
|---|---|
| `ErpWeb.UI/Subcon/SubconWorkbench.razor[.cs/.css]` | Home, KPIs without mixed UOM, two CTAs |
| `ErpWeb.UI/Subcon/SubconSendOut.razor[.cs/.css]` | Compact entry; Save draft only |
| `ErpWeb.UI/Subcon/SubconSendOutList.razor[.cs]` | List; Post/Rollback **disabled** + reason |
| `ErpWeb.UI/Subcon/SubconReceiveBack.razor[.cs/.css]` | Outstanding grid shell; Save draft only |
| `ErpWeb.UI/Subcon/SubconReceiveBackList.razor[.cs]` | Same Post rule |
| `ErpWeb.UI/Subcon/SubconInquiry.razor[.cs]` | Filters; empty/sample labelled read-only |
| `ErpWeb.UI/Subcon/SubconSettings.razor[.cs]` | Admin; flags default off; no raw replay switch |

Copy chrome from `IvMiscReceipt` / `IvMiscReceiptList` / `QaWorkbench`. Use `PoSupplierPicker`, `CommonDataGridEx`, left-fixed command column, `erp-entry-footer-standard`.

### 3.2 Core / Model (draft only)

| File | Role |
|---|---|
| `ErpWeb.Core/Subcon/ISubconApplicationService.cs` | Draft CRUD, queries, rights; **no Post that moves stock** |
| `ErpWeb.Core/Subcon/SubconApplicationService.cs` | Implementation |
| `ErpWeb.Core/Subcon/SubconMode.cs` | `Inventory` \| `Production` |
| `ErpWeb.Core/Subcon/ISubconInventorySourceAdapter.cs` | Interface only: `FindEligibleSource`, `ValidateBeforePost`, `Post`, `Rollback`, `GetBalance` — **Post throws NotSupported / returns BLOCKED** until 2I |
| `ErpWeb.Core/Subcon/ISubconProductionSourceAdapter.cs` | Same; Production Post always BLOCKED in Phase 1–2P |
| `ErpWeb.Core/Subcon/SubconBlockedReasons.cs` | Operator strings from the matrix |
| `ErpWeb.Model/Entities/Subcon/*.cs` | See §4 |
| `ErpWeb.Model/Configurations/Subcon/*.cs` | EF config, indexes, rowversion |

### 3.3 Registration (touch existing files — still Phase 1 only)

| File | Change |
|---|---|
| `ErpWeb.Core/Menus/MenuCodes.cs` | `Subcontracting`, `SubconWorkbench`, `SubconSendOut`, `SubconReceiveBack`, `SubconInquiry`, `SubconSettings` |
| `ErpWeb/Menus/menus.xml` | Parent + children; SortOrder after Purchase |
| Menu SQL seed (follow `init-qa-menu.sql` pattern if that is how QA is deployed) | Tenant menu rows |
| `ErpWeb.Core/CoreServiceCollectionExtensions.cs` | `AddScoped<ISubconApplicationService, …>` |
| `ErpWeb.Core/Numbering/RunningNumberKeys.cs` | `SubconDispatch`, `SubconReceipt` (server-side only; never client-generated) |
| `ErpWeb.Model/Data/AppDbContext.cs` | DbSets for §4 |
| `ErpWeb.Model` EF migration | Additive Subcon tables only; down script required |

**Do not edit** `IvInventoryPostingService*.cs`, `StockLedger/Costing/**`, Production posters, `IvSpFifoEligibility`, material availability readers, or QA gate frozen policy in Phase 1.

### 3.4 Tests (Phase 1)

| File | Assert |
|---|---|
| `ErpWeb.Tests/Subcon/SubconApplicationServiceTests.cs` | Save/update/delete NEW draft; no BalLoc/WIP change; company/branch from tenant not client |
| `ErpWeb.Tests/Subcon/SubconPermissionTests.cs` | Missing Access/Add/Edit; Inventory-only user does not need Planning; ViewCost hides money |
| `ErpWeb.Tests/Subcon/SubconModeTests.cs` | Production radio disabled; cannot mix modes on one draft |
| `ErpWeb.Tests/Subcon/SubconPostBlockedTests.cs` | Post returns blocked reason; **zero** TR/WIP SQL |

---

## 4. Proposed minimum schema (draft evidence — not posting authority)

Names illustrative; prefer fewer tables. **No tables for quantities already derivable from inventory/WIP events** (there are none yet).

| Table | Keys / columns | Notes |
|---|---|---|
| `SubconJob` | Uid, CompanyCode, BranchCode, SupplierId, Mode, optional PoUid, PromisedReturnDate, audit, RowVersion | Optional; Send Out may create lazily |
| `SubconDispatch` | Uid, JobUid?, DocNo, Mode, Status=`NEW`, TrxDt, SupplierId, audit, RowVersion | Header |
| `SubconDispatchLine` | DispatchUid, LineNo, source identity (IvBalLocId **or** ProductionBalLotUid — **exactly one**, no WO FK on inventory lines), Item, Uom, BaseUom, Qty, PromisedDate | Unique (Dispatch, LineNo); unique source-line constraint as needed |
| `SubconReceipt` / `SubconReceiptLine` | Same pattern; allocation **must** point at exact dispatch line | Draft only in Phase 1 |
| `SubconPostingLink` | Company, Branch, Action, RequestId, SourcePostingId, Generation, Qty, Uom, identity, reversal-of | **Unused until 2I/2P.** Unique `(Company, Branch, Action, RequestId)` |
| `SubconSupplierLocationMapping` | Company, Branch, SupplierId, WarehouseCode, LocationCode, ActiveFrom/To | **Do not use for posting** until consumer matrix is green |
| `SubconPOLineLink` | Dispatch/Job, PoUid, PoRevision, Line, ServiceItem, agreed qty/UOM | Reference only |
| Feature / policy row | Company, `SubconEnabled` default 0, `InventoryModeEnabled` 0, `ProductionModeEnabled` 0, `PoRequired` TBD, `QaRequired` | Never expose Enable Costing Replay |

SQL decimal precision = existing inventory/production models. No cascade delete of posted evidence (when posting exists). Tenant from authenticated scope.

**Migration rollback:** drop Subcon tables only; never rewrite `IvBalLoc` / `ProductionBalLot`.

---

## 5. Transaction orchestration (Phase 1 = none)

```text
UI  → ISubconApplicationService.SaveDraft
        → AppDbContext transaction on Subcon tables only
        → NO inventory/production posting
        → NO coordinator begin

UI Post → return SubconBlockedReasons.NoAtomicInventorySeam
          (or NoProductionCustody, LotControlledBlocked, …)
```

A 20-line dispatch, when posting is someday legal, must be **one** `IvTrxBatch` (one TR document), not `PostAsync([batch1, batch2])`.

---

## 6. Protected-engine request (STOP — not Phase 1)

Required before Phase 2I. **Do not implement without a separate written user approval.**

### 6.1 In-transaction stock transfer hook

**Sketch only:**

```text
IIvInventoryPostingService:
  Task<IvInventoryPostingBatchResult> PostStockTransferInTransactionAsync(
      AppDbContext db, string companyCode, string branchCode, string userId,
      int batchNo, CancellationToken cancellationToken = default);

  Task<IvInventoryPostingBatchResult> RollBackStockTransferInTransactionAsync(
      AppDbContext db, string companyCode, string branchCode, string userId,
      int batchNo, CancellationToken cancellationToken = default);
```

Implementation: factor the body of private `PostInventoryTRAsync` / `RollBackInventoryTRAsync` **after** `BeginTransaction` so the caller’s tx + `BranchStockTransactionLock` + `BeginStandaloneLedgerAsync` run once. Method must **not** `CreateDbContext` or `Commit`. Failed result → caller rolls back.

**Impact map (class C):** physical `IvBalLoc` both legs, lots, history, V2 posting, FIFO/WAC/Standard valuation, replay helpers, batch POSTED/NEW, permissions (`INV_STOCK_TRANSFER` Post/Rollback still enforced inside).

**Tests required before that hook is accepted:** existing `IvStockTransferPostingServiceTests` + `IvStockTransferLedgerAuthorityTests` still green; new SQL Server tests: Subcon draft + TR in **one** commit / **one** rollback; kill connection after TR mutate before Subcon save → **nothing** posted; duplicate RequestId idempotent; concurrent last qty.

**Rollback strategy:** unused hook is dead code until Subcon calls it; can be left unused if Subcon is abandoned. Do not change lot uniqueness rules in the same change.

### 6.2 Offsite consumer exclusion

Separate class-C request: every path in readiness matrix §3 must refuse supplier-mapped warehouses/locations **server-side** (Sales FIFO, DR, MI/scrap/VR, TR source, Production material, stock count). Options (choose in that review, not now): dedicated warehouse list used as a denylist; or a new stock dimension enforced in `SearchOnHandPagedAsync` / `MatchesCandidate` / count candidates / production availability. **UI filter alone is NO-GO.**

This **will** touch protected inventory selection rules. Stop and ask.

### 6.3 Production partial custody

No sketch until a design can reduce dispatchable WIP **and** stop Daily Production / FG / WOT / PADJ / inquiry. Likely new movement + second `ProductionBalLot` or first-class reservation subtracted in **every** reader listed in matrix §4. `ProcessType=SUBCONTRACT` is **not** that mechanism. **Phase 2P stays BLOCKED.**

---

## 7. Permission composition (Phase 1 decision to record, not bypass)

Recommendation unless security architecture says otherwise:

- Viewing Subcon: `SUBCON_*` Access.
- Saving drafts: Subcon Add/Edit.
- **Future Post:** Subcon Post **and** `INV_STOCK_TRANSFER` Post (inventory mode) or the relevant Production Post (when 2P exists).
- PO create: real PO Add. ViewCost on Subcon does not reveal PO prices without PO/ViewCost rules.

Server `CanAsync` on every write. Never trust client company/branch.

---

## 8. Test / build commands (for a future Phase 1 PR — not run now)

```text
dotnet build
dotnet test --filter FullyQualifiedName~ErpWeb.Tests.Subcon
dotnet test --filter FullyQualifiedName~ErpWeb.Tests.Inventory.Transaction.IvStockTransfer
dotnet test --filter FullyQualifiedName~ErpWeb.Tests.Production.Transaction.ProductionWorkOrderTransfer
```

Phase 1 exit also requires a **git diff** showing **no** changes under `ErpWeb.Core/StockLedger/Costing`, production cost posters, or `IvInventoryPostingService` except if the **separate** §6 approval landed in another PR.

SQL Server concurrency: **not** a Phase 1 shell gate; **is** a Phase 2I gate. SQLite draft tests do not certify posting.

---

## 9. Phase 1 exit criteria (if approved later)

1. Feature flags default OFF; disabled-mode explanation shown.
2. Inventory-only user: full Workbench/Send/Receive chrome, **no** WO lookup, no Production permission required.
3. Schema migrate up/down on a copy database.
4. Save draft / validation refuse does not mutate stock/WIP (test query counts).
5. Post control disabled with exact blocked text.
6. UX: 768/1024/1366; left actions; compact header; 20-line grid shell; 5-task dry run without coaching on navigation.
7. **STOP.** Do not self-start 2I.

---

## 10. Recommended sequence after this Phase 0

```text
NOW     Phase 0 COMPLETE — this folder
NEXT    Human: approve or reject Phase 1 shell (no posting)
LATER   Separate approval: §6.1 TR in-transaction hook
LATER   Separate approval: §6.2 offsite denylist on all consumers
THEN    Phase 2I non-lot Inventory dispatch + rollback only
THEN    Phase 3 non-lot receive + allocation + rollback
NEVER   Lot-controlled / 2P / 2M / Phase 5 until their own proofs
```

**COSTING FILES CHANGED THIS PHASE:** NONE (markdown under `plans/subcon/` only).

**DECISION:** Phase 0 **COMPLETE**. Inventory source **BLOCKED**. Production source **BLOCKED**. Phase 1 **HOLD**.

**STOPPED — AWAITING EXPLICIT NEXT PHASE APPROVAL.**
