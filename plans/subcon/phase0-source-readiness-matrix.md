# Subcon Phase 0 — Source readiness matrix

**HEAD:** `1b2d70618ce44d5db0f13ea14d78697dbc27ea5f` (`productionv2`)  
**Companion:** [phase0-current-repo-evidence.md](phase0-current-repo-evidence.md)  
**Rule:** Inventory readiness and Production readiness are **independent**. A blocked adapter stays invisible. Shared UI may be designed for both; it must not enable a blocked Post.

Legend: **PROVEN** = live code + tests demonstrate the behavior. **NOT PROVEN** = code exists but Subcon end-to-end / SQL Server / all-consumers not shown. **BLOCKED** = current rules make the Subcon scenario unsafe or illegal without a separately approved protected-engine change.

---

## Gate table (this audit)

| Workstream | Decision | Cause |
|---|---|---|
| Phase 0 evidence | **COMPLETE (code audit)** | Files below. No SQL Server execution. |
| Phase 1 UI shell + draft schema, no posting | **HOLD** | Exact files in [phase1-exact-file-implementation.md](phase1-exact-file-implementation.md). Needs explicit approval. Does **not** unblock 2I/2P. |
| Phase 2I Inventory non-lot dispatch + rollback | **BLOCKED** | No in-transaction TR hook; composing public `PostAsync` then writing Subcon rows can orphan stock. Offsite warehouse not excluded from consumers. |
| Phase 2I Inventory lot-controlled | **BLOCKED** | Dest lot must differ and must not already exist. Return to original/source lot via TR is illegal. |
| Phase 2P Production WIP dispatch + rollback | **BLOCKED** | No partial offsite WIP API. WO Transfer is full-balance inter-WO. `ProcessType=SUBCONTRACT` is duration-only. Unread OffsiteQty would not stop consumers. |
| Phase 2M owned materials | **BLOCKED** | Out of baseline; ownership classification unproven. |
| Phase 3 Receive Back + QA | **HOLD / blocked per mode** | No dispatch owner yet. QA physical gates frozen. |
| Phase 4 inquiry / live enablement | **HOLD** | No posted facts. |
| Phase 5 costing / conversion / write-off | **OUT OF SCOPE** | |

**Deployability (proof table 9):** first closed-loop **non-lot inventory** pilot = **NONE** until (a) an approved same-transaction TR+Subcon seam exists and (b) every ordinary consumer excludes the supplier warehouse **server-side**. Do not go live on a UI shell.

---

## 1. Inventory transfer API feasibility — BLOCKED (no legal seam)

| Question | Finding |
|---|---|
| Public in-tx stock-out/in/ADJ? | **Yes** — `PostStockOutInTransactionAsync`, `PostStockInInTransactionAsync`, `PostStockAdjustmentInTransactionAsync` |
| Public in-tx stock-transfer? | **No** — `PostStockTransferInTransactionAsync` does not exist |
| `PostInventoryTRAsync` tx boundary | **Private**; `CreateDbContext` + `BeginTransaction` + `Commit` (`IvInventoryPostingService.cs` ~3032–3502) |
| `DispatchAsync` multi-batch | Independent per-batch txs; `Posted X of Y` on mixed success (~417–428) |
| Coordinator wrap without TR hook? | **No.** `BeginInTransactionAsync` requires an existing SQL tx and only opens V2 `StockPosting`; it does not move `IvBalLoc` for TR |
| Legal Subcon compose today? | **No.** Calling `IvStockTransferService.PostAsync` then updating Subcon rows is two commits. Source success + Subcon fail = orphan movement. Reverse order = Subcon posted with no stock. Try/catch compensation is forbidden. |

**Proposed legal seam (STOP — protected-engine; not authorized):** add `PostStockTransferInTransactionAsync` / `RollBackStockTransferInTransactionAsync` mirroring stock-out/in: caller owns `AppDbContext` + transaction; method does **not** `BeginTransaction`/`Commit`; body factored from `PostInventoryTRAsync` after the lock. Impact: branch lock order, slice locks, lot create, history, V2 begin/complete, batch POSTED/NEW, FIFO/WAC/Standard valuation, replay helpers. Requires: exact diff sketch, qty/value/rollback matrix, SQL Server concurrency tests, explicit user approval. **Do not implement in Phase 1.**

Until that hook exists (or another **already public** same-tx owner is found — none found), **Phase 2I is BLOCKED**.

---

## 2. Lot round-trip — LotControl=true BLOCKED

| Step | Current TR | Subcon return? |
|---|---|---|
| Source lot A → new dest lot B | Required (A ≠ B, B must not exist) | Dispatch *could* create supplier lot B |
| Return B → original A | **Rejected** (A exists; dest must differ from source if returning “from B to A”, and A already exists) | **Illegal** on current service |
| Return B → new lot C | Allowed as a *new* lot | Breaks “same lot identity”; genealogy is a new lot, not restore |
| Undo via Rollback | Restores A qty; batch `NEW` | Operational undo, not a later receipt |

FIFO/WAC/Standard TR tests cover split-to-new-lot and rollback, **not** a later return into the original lot. **Do not change TR lot rules to pass Subcon.**

Non-lot items: empty lots enforced. This is the **only** inventory identity still eligible *after* atomicity + isolation are solved.

---

## 3. External-stock consumer matrix — NOT EXCLUDED

`WarehouseType` is master-data only. **No path below filters `IvWarehouse.WarehouseType` or a SUBCON warehouse code.** Creating a supplier warehouse and transferring into it would leave that stock **pickable**.

| Path | File / method | Filters used | Offsite excluded? |
|---|---|---|---|
| Sales DO FIFO pick | `IvSpShipmentService.DiscoverFifoPilesAsync`; `IvSpFifoEligibility.MatchesCandidate` (~21–45) | company/branch/item/warehouse **on the line**, location, `StdQty>0`, **`IStatus==ACTIVE`**, `TransDate` ≤ doc date | **NOT EXCLUDED** if DO warehouse = supplier WH |
| Sales direct Invoice stock issue | `SaInvoiceService` → same SP/FIFO | same | **NOT EXCLUDED** |
| Delivery Request reservation | `SaDeliveryRequestFulfilmentService` (~195–205) | header warehouse, `ACTIVE`, `StdQty>0` | **NOT EXCLUDED** |
| Soft reservations | `InventorySoftReservationReader` | NEW SalesOut + DR rows | **NOT EXCLUDED** |
| Stock Transfer source picker | `IvStockCommonRepository.SearchOnHandPagedAsync` (~472–552) | `StdQty>0`, `StockControl`, `IsActive`; **no IStatus**; **no WarehouseType** | **NOT EXCLUDED** |
| MI / Scrap / Vendor Return | same picker; post matches chosen `IStatus` | user-selected slice | **NOT EXCLUDED** |
| Production material issue | `ProductionMaterialStockAvailabilityReader` (~96–124) | WO material warehouse, `ACTIVE`, `StdQty>0`, `StockControl` | **NOT EXCLUDED** |
| Stock Count generate | `ListStockCountCandidatesAsync` (~601–658) | `StockControl`, optional qty, default **excludes SCRAPS only** | **NOT EXCLUDED** — offsite stock would be counted as on-hand |
| Balance / inquiry | `BuildBalanceLotQuery` | user filters; WH join for **description** | **NOT EXCLUDED** |

**IStatus is not a substitute warehouse guard.** Putting supplier stock in `QCHOLD`/`DAMAGED` would (a) still appear on `IvBalLocPicker`, (b) still be countable, (c) only block FIFO auto-pick for `ACTIVE`-only paths, (d) require QA physical gate which is **frozen**. Using status as fake custody is **forbidden**.

**Acceptance:** Phase 2I cannot enable a supplier warehouse until **every row above is proven to refuse that warehouse/location under the same SQL lock as posting**, or those paths are explicitly amended under a **separate approved** protected-path review. UI hiding is not enough.

---

## 4. WIP all-consumer matrix — Phase 2P BLOCKED

Hypothesis rejected: a Subcon-only `OffsiteQty` / flag that consumers do not read.

| Route | File | Reads `Qty`/`BaseQty`? | Would still consume dispatched qty if flag-only? | Lock owner |
|---|---|---|---|---|
| Daily Production material WIP | `ProductionOutputService.Posting.cs` PostAsync | **Yes** lock ~257–265, mutate ~364–366 | **Yes** | `BranchStockTransactionLock` + coordinator |
| Daily Production handoff | same | **Yes** | **Yes** | same |
| Daily Production availability UI | `ProductionOutputService.Materials.cs` `AvailableQtyAsync` | sums `BaseQty` | **Yes** (overstates) | read |
| Shared BOM WIP eligibility | `ProductionOutputService.SharedWip.cs` | **Yes** | **Yes** | post locks |
| FG receipt | `ProductionFinishedGoodReceiptService.Posting.cs` | **Yes** | **Yes** | coordinator + UPDLOCK |
| WO Transfer | `ProductionWorkOrderTransferService` | **Yes** full lot | **Yes** (moves entire remainder including “offsite”) | coordinator + UPDLOCK |
| PADJ | `ProductionStockAdjustmentService` | **Yes** | **Yes** | coordinator |
| Production stock return | `ProductionStockReturnService.Posting.cs` | **Yes** | **Yes** | coordinator |
| Rework qty on operation | execution fields | no direct lot consume | N/A | |
| Sequence / eligibility | `ProductionOperationSequenceGate` | `GoodQty` vs planned | does not reserve WIP | snapshot |
| QA logical hold | `QaLogicalHoldAuthorizationService` | remaining vs holds | **Partial** — some depletions blocked; **eligibility sums not reduced**; gate **frozen** | UPDLOCK |
| Balance inquiry | `ProductionBalanceInquiryService` | live lots | **Yes** | read |

**No existing API** atomically “send 200, keep 300 eligible internally” without decreasing `Qty`/`BaseQty` (which would look like consumption/output) or creating a **second** lot via a **new** movement type. `STATUS_OUT`/`STATUS_IN` are registered but unused by production posters.

**Phase 2P = BLOCKED.** Do not implement a fake custody balance.

---

## 5. PO service semantics — CLASSIFICATION PROVEN; SETTLEMENT NOT PROVEN

| Topic | Status | Evidence |
|---|---|---|
| `IType==SERVICE` classification | **PROVEN** | `PoOrderCalc.IsServiceIType`; `PoOrderService.IsServiceLine` |
| Create SERVICE PO line | **PROVEN** (any SERVICE item) | no extra save block |
| Approval / PENDING / CHECKED | **PROVEN as PO engine** | `IProcurementApprovalGuard`; Subcon must deep-link, not bypass |
| Invoice without GR / NG received qty | **BLOCKED by current invoice rule** | `PoInvoiceService` ~1633–1636 *cannot invoice before goods receipt (net received is zero)* |
| Service UOM vs physical PCS | **NOT PROVEN** | no HR/JOB converter; PO uses purchase UOM |
| Tax / SST / e-invoice / individual vendor | **NOT PROVEN for Subcon** | follow existing supplier/PO rules; do not invent |
| Equating PO qty to dispatched stock qty | **FORBIDDEN** unless separately approved conversion | v6 invariant |

Phase 1 may **link** an existing approved service PO line as a read-only reference. `Create PO Draft` = deep link to `PoOrder` with Subcon supplier prefilled. **Do not** post service PO as Inventory GRN.

---

## 6. QA frozen vs operational — FAIL CLOSED

| Gate | New enablement | Operational physical effect today | Subcon disposition |
|---|---|---|---|
| Incoming inspection | Frozen | Claim returns `IncomingFrozen` if flag on; pass-through if off | If policy **requires** QA on Subcon receipt → **BLOCK** receipt/release. Do not mark Accepted. |
| Physical warehouse hold | Frozen | Qty reservation implemented but gate off; does not set `QCHOLD` | Cannot use as Subcon quarantine |
| Logical WIP hold | Frozen | Reserves `ProductionBalLot` remaining vs holds | Cannot use as offsite WIP lock |
| FG inspection | Frozen | N/A for Subcon baseline | |
| Non-good DP | Frozen | Counters only | Supplier scrap/loss needs **authorized** stock/WIP write-off (Phase 5) |
| QA not installed | — | Posting continues | Allowed only if company policy is **no QA required** |

**Fail-closed:** required-QA + frozen gate = **block Post**, plain-language message, allow draft/view/print. Never silent Accepted.

---

## 7. Authority, idempotence, source lifecycle

| Object | Post | Rollback | Repost risk |
|---|---|---|---|
| Inventory TR batch | `NEW` → `POSTED` | `POSTED` → **`NEW`** | Same batch number reused; CostLayer/history generation must not double-count. Subcon links must be **new** for the new posting generation. |
| Inventory TR multi-batch Post | Partial `Posted X of Y` | Per batch | Never mark a Subcon header POSTED if any mapped batch failed |
| Production WO Transfer | Full lot | Reversal legs via writer; blocked if target consumed | Not a Subcon vehicle |
| Coordinator request id | Idempotent replay of same `RequestId` | Abort on fail | Subcon must mint request ids; retry after timeout must not create a second movement |
| Closed period / future date | Refused inside TR tx | Same | Inherit; do not wrap |

Subcon historical events are **append-only**. Do not store Subcon status `ROLLED_BACK` as if it were `IvTrxBatch.BatchStatus`.

---

## 8. Operator prototype / UAT

See [phase0-ux-wireframes-and-uat.md](phase0-ux-wireframes-and-uat.md). Paper + component mapping only this phase. 5-person live UAT is a Phase 4 gate, with a Phase 1 read-only dry run if Phase 1 is approved.

Blocked Post states that **must** appear in any later prototype: lot-controlled item, missing atomic seam, offsite warehouse not isolated, Production mode, required-QA frozen, closed period, over-return.

---

## 9. Deployability recommendation

| Candidate | Recommendation |
|---|---|
| Inventory non-lot same-item closed loop | **NONE** — atomic TR seam missing **and** offsite stock still pickable/countable |
| Inventory lot-controlled | **NONE** — lot round-trip illegal |
| Production WIP | **NONE** — no partial custody enforcement |
| UI-only Workbench (flag OFF, no Post) | **HOLD for Phase 1 approval** — useful for UX, must label “not posting / sample” and never enable tenant flags |
| Whole module GO | **NO** |

---

## Other scenarios

| Scenario | Status |
|---|---|
| Same-item Inventory conversion (RAW-A → PAINTED-B) | **BLOCKED** (Phase 5) |
| Yield / scrap capitalization | **BLOCKED** (Phase 5) |
| Supplier-side loss reducing valued stock | **BLOCKED** until authorized write-off owner |
| Company-owned extra materials (2M) | **BLOCKED** |
| Mixing Inventory and Production lines on one dispatch | **BLOCKED** by design (mode immutable per document) |
| Inventory-only tenant without Production permission | **REQUIRED** for any later UI — hide WO; do not query WIP |

---

## Costing / quantity invariants (must hold if 2I is ever unblocked)

From `productionv2-costing` + `stock-balance.md`:

1. Transfer: source −qty and dest +qty **one atomic operation**; net item qty unchanged (same base UOM).
2. No unexplained negative slice; no future-dated stock use beyond current policy.
3. FIFO layers move with TR; rollback restores the same layers; do not average a return.
4. WAC / Standard: snapshot item cost state and pool/value before/after/rollback; no unexplained monetary delta.
5. Offsite stock is still **company-owned**; it must not be sold, issued to production, or counted as shop-floor on-hand unless policy says so **and** code enforces it.
6. Draft Save: **zero** stock/WIP effect.

**Phase 0 did not run those tests against a Subcon document** because no Subcon document exists and posting is blocked.

---

## GO / NO-GO

| Adapter | Decision |
|---|---|
| Inventory source | **BLOCKED** (atomicity + isolation). Non-lot remains the *candidate* after those two are separately approved. Lot-controlled remains **BLOCKED**. |
| Production source | **BLOCKED** |
| Shared UX design | **GO to design only** (wireframes). **NO-GO** to Post buttons that call inventory/production. |
| Costing engine edits | **NO-GO** |
| Overall module implementation | **NO-GO** until per-mode closed loop is proven |

**STOPPED — AWAITING EXPLICIT NEXT PHASE APPROVAL**
