# ProductionV2 QA Phase 3 — Existing-transaction navigation

**Status:** PHASE_3_CODED. Gate 3 STOP.  
**Date:** 2026-11-02  
**Agent:** Cursor Grok 4.6  
**HEAD:** `717f3484846233ca05e45dd51c202f1272c47cfb` (`prodcut defination enhance`)  
**Branch:** `fix/dataprotection-key-ring`  
**Task class:** **B costing-adjacent** (navigation, inquiry, banners). Posting, valuation, FIFO/WAC/standard replay, production absorbed-cost formulas, layers, and quantity mutators were not changed in this phase.

Live DBA scripts were **not applied**. Flags stay **OFF**. No git commit.

---

## 1. What Phase 3 is

Quality opens **existing** documents. It does not post them.

| Destination | When it appears | Status | Stock effect |
|-------------|-----------------|--------|--------------|
| Open goods receipt | Incoming remaining accepted qty after approve | `SUGGESTED` | None until the operator posts GR |
| Goods receipt view | Sealed `QaReceiptAllocation` `POSTED` grouped by `BatchNo` | `EXECUTED` | Already posted by GR, not by QA |
| Goods receipt view | Sealed allocation `REVERSED` | `REVERSED` | Already reversed by GR rollback |
| Vendor return / purchase CN | Posted-GRN reject after approve | `LINKED_UNVERIFIED` | Navigation only. No in-tx QA hook |
| Inventory scrap | Inventory-stock reject after approve | `LINKED_UNVERIFIED` | Navigation only |
| Vendor return | Incoming reject-before-GRN | `NOT_STOCK`, `CanNavigate=false` | Qty never entered stock |
| Physical QCHOLD | Inventory-stock reject | `REQUIRES_LATER_PHASE` | Phase 4 |
| Non-good Daily Production | `PRODUCTION_OUTPUT_NON_GOOD` | `REQUIRES_LATER_PHASE` | Phase 5 |

`EXECUTED` is only derived from sealed `QaReceiptAllocation` / `QaFgAllocation` rows created in Phase 2A/2B posting transactions. Document-number matching is never used. VR/SC/CN are never `EXECUTED`.

Purchase credit-note-owned vendor returns (`RefNo` starts with `PCN/`) must still be posted from the credit note. The QA caption says so. QA does not call vendor-return POST.

---

## 2. Costing / stock-balance attestation (class B)

Inspected and **unchanged** in this phase:

- `IvInventoryPostingService` (+ FIFO/WAC/Standard replay adapters)
- `ProductionStockWriter`, Daily Production consume/handoff, FG receipt posting
- FIFO layers, WAC, standard, production absorbed-cost formulas
- No new QA movement type, no `IvBalLoc` / `ProductionBalLot` mutation, no grant/allocation schema

Destination NEW pages (`IvGoodsReceipt`, `IvVendorReturn`, `IvScrap`, `PoCdn`, `PrFinishedGoodReceiptEntry`) only read `?qaCase=` and set a status banner. They do **not** auto-create lines and do **not** post.

Logical hold from Phase 2C remains a reservation (`HeldReservedBaseQty`), not a warehouse transfer and not ATP unless a later approved policy says so.

**Stock-balance.md:** Phase 3 does not change `EndingOnHand`, reservations, or UOM. Open GR/FG/VR/SC/CN is the same as the operator navigating there from the menu.

---

## 3. Code delivered

| Area | Path |
|------|------|
| Composer | `ErpWeb.Core/Quality/QaDestinationComposer.cs` |
| DTOs / statuses | `QaDtos.cs`, `QaCodes.cs` (`QaDestinationKinds` / `QaDestinationStatuses`) |
| Case map + inquiry | `IQaCaseService.GetInquiryAsync`, `QaCaseService` |
| Case Open buttons | `ErpWeb.UI/Quality/QaCaseEntry.razor(+.cs+.css)` — ACCESS on `INV_GOODS_RECEIPT`, `PLN_FINISHED_GOOD_RECEIPT`, `INV_VENDOR_RETURN`, `INV_SCRAP`, `PO_CN` |
| Inquiry | `/quality/inquiry` `QaInquiry.razor(+.cs+.css)` |
| Menu | `MenuCodes.QualityInquiry = QA_INQUIRY`; `ErpWeb/Menus/menus.xml`; `scripts/init-qa-menu.sql` (**write only, not executed live**) |
| Banners | `QaOpenedFromCase.Banner` on GR/VR/SC/FG/PoCdn NEW |

No new physical schema. Destinations are computed.

First-pass yield caption (explicit denominator):  
**“First-pass yield is approved cases with zero rejected qty, divided by cases that reached inspection.”**

Aging = `today − CaseDate` for cases that are not CLOSED/CANCELLED; closed/cancelled age is 0.

---

## 4. Tests

Command:

```
dotnet build ErpWeb.Tests --no-restore
dotnet test ErpWeb.Tests --no-build --filter "Category=Quality"
dotnet test ErpWeb.Tests --no-build --filter "FullyQualifiedName~MenuDeploymentParityTests"
```

| Suite | Result |
|-------|--------|
| Quality (foundation + incoming + FG + logical hold + destinations) | **46 Passed** |
| MenuDeploymentParityTests | **2 Passed** (`QA_INQUIRY` present in `menus.xml`) |

Covered:

- Reject-before-GRN is `NOT_STOCK`, not a vendor return
- GR/FG `EXECUTED` from allocation `BatchNo` / `BatchId`, not `SourceDocNo`
- Posted-GRN reject is `LINKED_UNVERIFIED` never `EXECUTED`
- VR/SC/CN never receive `EXECUTED`
- Inquiry does not insert `IvBalLoc` / `IvTrxBatch` / allocations
- FPY helper uses inspection denominator

**Not run:** SQL Server concurrency, live tenant script apply, browser visual matrix (desktop/tablet/phone, light/dark, 125%).

---

## 5. Phase 4 feasibility / design (implemented as reservation in Gate 4)

See `plans/qa_phase4_implementation_evidence.md`. The conversion command (`ACTIVE`→`QCHOLD`) remains unimplemented. The coded architecture is a fail-closed `IvBalLoc` reservation. Original feasibility notes follow.

QCHOLD today is an `IStatus` constant (`IvItemStatuses.QcHold`). It is **not** quarantine:

- MI, TR, VR, SC, ADJ, stock count, and generic replay of those types slice-match the pile. QCHOLD is consumed if the line matches.
- SP/IP pick ACTIVE-only, so QCHOLD is only accidentally protected on those routes.
- There is no universal outflow gate, no QA-owned status change, and no in-tx claim on VR/SC.

Physical hold therefore **cannot** be a UI label or a transfer-to-QA-warehouse shortcut. A later Phase 4 must, at minimum:

1. **Own a posting.** Either a dedicated inventory document or an existing type (status change / hold) that goes through `IvInventoryPostingService` on **every** POST and rollback path, including FIFO/WAC/Standard replay adapters — the same lesson as Phase 2A GR grants.
2. **Universal outflow gate.** Under the same SQL transaction and `IBranchStockTransactionLock` order already used for GR/FG, refuse MI/TR/VR/SC/ADJ/count/PADJ/production writer/IP consume of a held slice unless the document is the authorized QA release. Flags default OFF; non-QA tenants stay unrestricted.
3. **Identity.** Hold the exact `IvBalLoc` (item/warehouse/location/lot/status/UOM) plus posting generation. Do not merge unlike slices. `CK_PrProductionBalLot_V2` still requires ProductionLocation + StockStatusCode + OriginType when BalanceStage is set — do not invent a WIP QCHOLD pile that violates it.
4. **Release.** Reverse must restore the original slice and generation; do not use a second transfer as “release.”
5. **PoCdn vs standalone VR.** If the VR batch is owned by a purchase credit note (`RefNo` `PCN/`), Phase 4 must not independently POST/ROLLBACK that VR.
6. **Reject `PhysicalHoldEnabled` until (1)–(5) exist.** The policy setter already fail-closes that flag.

Out of Phase 4: non-good Daily Production valued lots (Phase 5), costing formula changes, enabling flags in live tenants without DBA scripts and a separate readiness gate.

---

## 6. Gate 3 verdict

| Item | Verdict |
|------|---------|
| PHASE_3_CODED | **YES** |
| Existing transactions remain the only stock posting owners | **YES** |
| Unverified VR/SC/CN never marked EXECUTED | **YES** |
| Reject-before-GRN is not VR/CN/SC | **YES** |
| No Phase 4/5 code | **YES** |
| Flags default OFF / live SQL not applied | **YES** |
| HEAD still `717f348` / no agent commit | **YES** |
| Browser visual matrix | **NOT RUN** |
| SQL Server concurrency | **NOT RUN** |
| IMPLEMENTATION_10_OF_10 | **NOT CLAIMED** |

**STOP.** Do not start Phase 4 (physical QCHOLD custody) or Phase 5 (non-good WI costing) until separately commissioned. Do not apply `create-qa-*.sql` / `init-qa-menu.sql` against a live tenant in this change set.
