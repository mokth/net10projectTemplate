# ProductionV2 Subcontracting G1–G3 — Repository Verification & Targeted Fix Plan

**Review date:** 2026-11-23 (Malaysia)  
**Source of truth:** `mokth/net10projectTemplate`, branch `productionv2`, commit `5c4f87f181f4f4595468bb7ed33ca966d0881d1e` (2026-11-23 13:31:37 UTC).  
**Basis:** Read current remote GitHub source for Subcon, Inventory TR, stock coordinator/lock, warehouse master, scripts, UI, and tests. No local worktree, SQL Server test database, actual build or live UAT was available. **Static-review findings, not runtime pass certificates.**  
**Decision:** **PARTIAL IMPLEMENTATION / NOT APPROVED FOR PRODUCTION ROLLOUT**. Non-lot same-SKU custody workflow is wired, but G3 certification and several safeguards are incomplete. BOM conversion, lot handling, Production external WIP, fee capitalization and replay-required Subcon posting remain intentionally unsupported.

## 1. What is implemented and should be preserved

- `ErpWeb.Core/Subcon/SubconInventoryTransferAdapter.cs`: Post/Rollback Send Out and Receive Back; single caller-owned transaction, branch transaction lock, one `TR` batch, existing Inventory TR in-transaction service and V2 posting coordinator, supplier custody allocation and posting-link records. Preserve this approach; **do not build Subcon costing, FIFO/WAC/Standard, stock or WIP engines**.
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs` and `IIvInventoryPostingService.cs`: new in-transaction TR Post/Rollback seam. The core checks open period, future posting date, source balance, historical as-of availability, status and FIFO/WAC/Standard valuation through existing V2 coordinator. If `RequiresReplay` is true, the in-transaction seam safely refuses. Do **not** bypass this refusal to claim backdated support.
- `ErpWeb.Core/StockLedger/BranchStockTransactionLock.cs`: SQL Server `sp_getapplock`, transaction ownership, exclusive per-branch key, 15-second default timeout; see `StockPostingCoordinator.BeginInTransactionAsync` for same lock and idempotency checks.
- `ErpWeb.Core/Subcon/SubconApplicationService.cs`: server permission checks (Subcon + Inventory Transfer), status and rowversion; posted receipt and dispatch rollback endpoints, outstanding allocation tracking.
- Custody exclusion added to several Inventory, Production and Sales lookups, plus inventory posting guard. Feature flags default OFF and forbid unsupported modes.
- `ErpWeb.Tests/Subcon/SubconLiveCustodyPostingTests.cs`: SQLite/in-memory unit/integration coverage for WAC/FIFO/Standard normal custody, rollback/repost, multi-line failure, injected crash, lookup isolation, RequestId and a blocked historical case. `ErpWeb.Tests/Subcon/SubconSqlServerConcurrencyTests.cs` covers **concurrent receipt DRAFT saves**, not live Subcon Post/Rollback.

## 2. Findings by severity

### H-01 — HIGH — Custody warehouse classification can be changed while supplier stock is outstanding
**Evidence:** `ErpWeb.Core/Inventory/IvInventoryRefService.cs` (`SaveWarehouseAsync` update around lines 196–238) permits changing `WarehouseType` and `IsActive` after only lookup validation / rowversion. `SetWarehouseActiveAsync` also toggles active state. There is no check for outstanding `SubconCustodyAllocation` or existing supplier-held `IvBalLoc`. The new visibility filters in `IvInventoryLookupService`, `ProductionMaterialStockAvailabilityReader`, `SaDeliveryRequestFulfilmentService`, `IvStockCommonRepository` and `StockReplayCountCandidateReader` depend on `WarehouseType == SUBCON`. The lower-level `SubconCustodyGuardService` also checks active allocations for source movement, which mitigates some **posting** routes, but it does not stop visibility leaks nor prove every count/reservation/reader is safe.
**Risk:** A normal warehouse edit can make offsite stock appear available for normal business workflows and can race with posting because the warehouse edit does not acquire the shared branch stock lock. Do not claim that all physical posting routes become unprotected: some are guarded by active allocations.
**Fix:** While any active custody allocation, custody balance or unresolved related Subcon posting exists, prohibit changing `SUBCON` type to another type, deactivating or repurposing the warehouse/location, including bulk active toggles. Serialize these mutations with the existing branch stock lock and validate inside the same transaction. Keep the existing lookup master design otherwise unchanged; no independent balance or costing system. Use a human-readable message indicating the active supplier holdings. Consider also prohibiting arbitrary promotion of an occupied normal warehouse to `SUBCON` until balances/commitments are reconciled. Cover concurrent master edit vs posting.

### H-02 — HIGH — No explicit active V2 ledger/valuation gate in live Subcon adapter
**Evidence:** `SubconInventoryTransferAdapter.PostAsync` (~325–345) and `PostReceiptAsync` (~835–855) accept `BeginPostingInTransactionAsync` if `begin.Error` is null and then call physical TR and `CompletePostingInTransactionAsync(postingContext)`. The begin result can legally be `LedgerEnabled=false, Context=null, Error=null` when there is no active `StockLedgerEpoch` (`StockPostingCoordinator.BeginInTransactionAsync`, ~250–259; `IvInventoryPostingService.BeginPostingInTransactionAsync` also returns disabled when `_coordinator` absent). Completing with null is a no-op. This may be intentional backward compatibility for ordinary Inventory, but **Subcon cannot claim V2 cost-layer traceability in such a tenant**.
**Risk:** Live Subcon custody movement could be allowed without the intended V2 valuation/audit authority if the branch is not V2-ready. This is a conditional safety gap; actual impact depends on tenant costing setup and existing legacy cost mode.
**Fix:** Before live Subcon Post/Receive/Rollback, require the approved active V2 ledger/valuation authority **or** a separately documented and tested non-V2 costing policy; default to fail-closed for Subcon G3. Assert `begin.LedgerEnabled && begin.Context != null && !begin.WasReplay` before calling the physical seam when V2 is the required source. The effect must be zero on refusal. Add tests with missing epoch, disabled coordinator, and malformed/replayed begin. Do not change ordinary Inventory's existing legacy compatibility.

### H-03 — HIGH — No executed SQL Server live-post concurrency/rollback proof
**Evidence:** `SubconLiveCustodyPostingTests` use an in-memory SQLite test fixture and do not run concurrent Send Out/Receive Back Post/Rollback on distinct SQL Server connections. `SubconSqlServerConcurrencyTests` only tests concurrent `SaveReceiptDraftAsync` requests. GitHub commit status and associated PR workflow runs for pinned HEAD returned no checks/runs. No live SQL Server test execution was available in this review.
**Risk:** `sp_getapplock`, rowversion, SQL Server isolation, valuation seals, same-bin contention, deadlock handling and pooled session cleanup cannot be certified from code inspection or SQLite results alone.
**Fix/tests:** On a disposable SQL Server tenant, test two competing Send Outs for last stock, two Receive Backs for one dispatch allocation, Post vs Rollback same document, Receive Back vs Rollback Send Out, mixed normal Inventory MI/TR vs Subcon Send Out/Receipt, and Stock Count/warehouse edit collision. Force overlap using barrier synchronization and separate `AppDbContext`/connections. Assert all per-slice balances, supplier allocations, FIFO layers, WAC/Standard values, V2 postings, history, active links and statuses; zero partial data after failures. Test duplicate RequestId, stale RowVersion, lock timeout, network timeout after commit, retry, deadlock/cancellation and SQL session-context cleanup. Record exact commands, DB provider, exit codes, outcomes.

### M-01 — MEDIUM — Supplier code length exceeds custody location code length
**Evidence:** `SubconInventoryTransferAdapter.PostAsync` (~161–170) derives `custodyLoc = entity.SupplierCode.Trim()` and requires matching `IvLocation.LocCode`. `SupplierCode` supports 20 chars (`SubconPolicy`/supplier and corresponding DTO), while `IvLocation.LocCode` is limited to 10 chars. A supplier code longer than 10 characters cannot be represented as the required location.
**Risk:** Valid supplier master records cannot send stock out; trimming supplier code would risk collisions.
**Fix:** Define an explicit validated supplier-to-custody-location mapping with stable 10-char location key, or use a safe configured code mapping in existing data model if available. Preserve exact supplier identity separately. Add long-code, collision and multiple suppliers/branches tests. Do not silently truncate supplier identity.

### M-02 — MEDIUM — Rollback RequestId cannot recover its result after timeout
**Evidence:** Both Post operations check an explicit primary posting-link RequestId and return prior result if still posted. `RollbackAsync` and `RollbackReceiptAsync` do not check the previously created reversal link by RequestId before rejecting a document that has returned to `NEW`. UI handlers generate `Guid.NewGuid()` on every click (`SubconSendOut.razor.cs` ~413,442 and `SubconReceiveBack.razor.cs` ~265,294).
**Risk:** A committed rollback followed by a lost response appears to fail on retry, and a retry with a new ID cannot retrieve the original success. Duplicate stock movement is blocked by status; this is a recovery/UX gap, not proof of duplicate reversal.
**Fix:** Add exact reversal-link idempotent response recovery before status checks; verify doc identity, role, original active generation, sealed V2 reversal and request fingerprint/semantic equivalence. Persist the same operation RequestId across retry until definitive acknowledgement. Keep new ID for a *new* user-initiated lifecycle action. Add timeout-after-commit tests.

### M-03 — MEDIUM — Cross-dispatch FIFO origin trace not yet proven for pooled supplier bin
**Evidence:** `SubconInventoryTransferAdapter.PostReceiptAsync` (~680–810) references exact `SubconCustodyAllocation` by dispatch line for quantity, but the stock movement points to a shared `CustodyBalLocId` per supplier/item/warehouse/location; FIFO transfer valuation remains delegated to existing TR by that pooled balance. The `Two_dispatches_sharing_supplier_custody_bin_cannot_cross_consume_allocations` test proves quantity allocation, **not** that returned FIFO cost layers correspond to the specific dispatch's original cost source.
**Risk:** Two send-outs of the same SKU at different FIFO source costs may be physically fungible but lose business-document-specific value provenance for future conversion/fee/return tracing. Do not add manual layer selection to Subcon.
**Fix:** First define the business contract: FIFO chronological pooled-custody ownership versus exact per-dispatch layer lineage. If exact origin is required, use only the existing FIFO layer-transfer/lineage APIs and add proof or reject mixed-origin cases; any missing capability requires separate protected ledger approval. Verify two dispatches with distinct source layers, interleaved partial receipt, rollback/repost, and reconciliation of total FIFO layers and source attribution.

## 3. Historical/replay and feature scope — deliberate blockers, not defects

- Existing in-transaction `PostStockTransferInTransactionAsync` and `RollBackStockTransferInTransactionAsync` explicitly return `UnsupportedHistoricalInventoryPost/Rollback` when `RequiresReplay=true`. The standalone Inventory TR route can support some bounded WAC/FIFO/Standard backdated cases, but Subcon does **not** inherit those replay handlers through the new caller-owned seam. This is **safe refusal**, not an implementation of all backdates. **Do not call the new G3 Subcon historical replay complete.**
- Lot-controlled custody, BOM components to different FG SKU, scrap/byproduct conversion, service-fee capitalization, external Production WIP, and complex downstream Sales/PI/FG valuation are frozen by `SubconApplicationService.SavePolicyAsync` and the blocked Production adapter. G4–G9 from repo plan v11 remain unimplemented; do not enable their flags prematurely.
- Ordinary backdate without later dependencies may pass the current fast-path guards only if Inventory's chronology/as-of/period/business-date policy permits it; backdate with later dependent posting remains blocked.

## 4. Priority implementation order (minimal change)

1. **Patch H-01 and H-02** without changing any Inventory FIFO/WAC/Standard/CostLayer calculations or global replay policy. Keep tenant flag OFF until verified. Confirm customer warehouses and supplier code/location mappings.
2. **Build and execute H-03 SQL Server stress/integration tests**; fix any confirmed race/atomicity issues using the *existing* branch lock and in-transaction Inventory TR seam, not ad-hoc mutex or extra costing logic.
3. **Patch M-01 and M-02** with narrowly scoped supplier-location mapping and retry result semantics; validate existing doc status/permissions/audit.
4. **Investigate M-03** by business invariant and tests; block unsupported mixed-origin value attribution where necessary. Do not rewrite FIFO or CostLayer.
5. **Recheck full G3 end-to-end:** New/Edit/Save -> Post Send Out -> partial Receipts -> rollback Receipt -> rollback Send Out -> repost -> Inventory stock/valuation -> stock count and Sales/Production exclusion -> closed/future/backdated policies -> 2-user concurrency -> UI and support audit.
6. Complete focused tests + cross-module (Inventory MR/MI/TR/GR/Count, Sales DO/Invoice, Procurement PI, Production IP/FG) + full suite; record exact outputs and SQL Server provider. Human sign-off per tenant/mode **before** enabling live flags.

## 5. Acceptance criteria

- All normal non-lot same-item custody actions preserve net stock and company valuation; FIFO layers, WAC state and Standard valuation reconcile independently.
- Changes to warehouse master/lookup cannot expose or strand active offsite stock, including under simultaneous postings.
- No Subcon physical posting succeeds with missing/unverified required valuation authority.
- Every Post/Rollback operation has one atomic Inventory TR + Subcon business-state transaction and exactly one sealed active generation; failed/timeout actions leave no partial/misleading status.
- Two concurrent callers for shared inventory or custody allocations cannot over-issue/over-return; SQL Server tests show final quantities and value.
- Backdated replay-required Subcon actions remain completely blocked until separately approved exact replay integration; closed-period/future posting guards remain unchanged.
- Permissions/tenant/branch checks remain server-side, and posting links/history/audit survive rollback/repost.
- No new `SubconFIFO`, `SubconWAC`, `SubconCostLayer`, `SubconWipBalance`, direct `IvBalLoc` or `ProductionBalLot` adjustment, or duplicate independent V2 posting root.

## 6. Mandatory non-changes

**DO NOT** modify existing cost formulas, FIFO splitting/consumption, WAC cost-pool calculations, Standard variance, material issue/FG/Sales/Procurement posting ownership, stock-count absolute count rules, month-end/closed period guard, future-dated posting, or activate `OpenPeriodBackdatedReplayEnabled` to bypass a Subcon block. Protected core changes only via small, reviewed changes after source proof. Do not activate live capability flags as part of code deploy.

## Status / honest limitations

**Verified in source:** live G3 services exist; SQL transaction + branch lock + in-transaction transfer + V2 integration pattern; guarded supported scope and code-level safe refusal.  
**Needs execution:** actual SQL Server schema, real concurrency, rollback/repost failure injection, full-system regression, UI UAT and cost/quantity reconciliations.  
**Approval:** **NOT APPROVED FOR PRODUCTION** until HIGH findings and live SQL test gate are closed; no 10/10 claim.
