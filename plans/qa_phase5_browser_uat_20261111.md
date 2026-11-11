# Phase 5 — Browser UAT evidence (logged-in DEMO)

**Date:** 2026-11-11  
**Tenant:** local `ERPWeb` on `.\SQLEXPRESS`, company **DEMO**, branch **HQ**  
**User:** Demo Admin (`ADM01`), `SYSTEM_ADMIN` — same inspector and reviewer (SoD turned Off for this path)  
**App:** `http://localhost:5000` (`dotnet run --project ErpWeb --launch-profile http`)  
**Policy after UAT:** DEMO published `QaPolicy` Version 1. `Enabled=0`. All posting gates **0**. `InspectorApproverSeparation=0`. Record-only.

This is **not** `QA_INVENTORY_POSTING_COMPLETE`.  
Owner signoff is still required for `QA_RECORD_AND_FOLLOW_UP_COMPLETE`.

---

## Same-user path (requested)

Inspector–approver separation was **On** at the start of the session and blocked Approve/Reject as ADM01. Per “use back the same user”, SoD was turned **Off** in Quality Settings and saved. Toast: **Quality settings saved. Inspector-approval rules were updated. Posting gates stay frozen.**

SQL after Save: `InspectorApproverSeparation=0`, `IncomingInspectionEnabled=0`, `FinalFgInspectionEnabled=0`, `LogicalHoldEnabled=0`, `PhysicalHoldEnabled=0`, `NonGoodEnabled=0`, `Enabled=0`, `IsPublished=1`.

---

## Executed in a real browser (Demo Admin)

| Step | Result |
|---|---|
| Settings | Record-only copy, support/rollback notes, gates **Off / Not available yet**. SoD checkbox saved Off |
| New case | Source types listed (incoming, posted GRN, DP good/non-good, WIP lot, FG staging, posted MR) |
| Incoming + Lookup | Selected `PO2609-0001` / item `O6959EE5F` qty 6 EA, supplier Alpha, DN `DN-UAT-P5` |
| Save | **QC00000003** Draft → view |
| Submit | Status **Submitted**. Toast: inspection record only, stock not changed |
| Pass full amount / Pass and save | Toast: **Inspection saved. Stock was not changed.** |
| Submit for review | Status **Pending review** |
| Approve as inspector (SoD On) | Server: **The inspector cannot approve this case. Another user must review it.** |
| Reject as inspector (SoD On) | Server: **The inspector cannot reject this case. Another user must review it.** |
| Settings Save SoD Off | Policy updated; posting gates stayed frozen |
| Approve as same user | Toast: **Case is now Approved. Inspection record only — stock was not changed.** ReviewedBy=`ADM01` |
| Follow-up Add / Start / Done | Action 1 `FOLLOW_UP` qty 0 **DONE** by ADM01 |
| Reinspect | Popup required reason. Reason bound: `UAT reinspect same source. No second delivery.` Toast: **Reinspection started on the same source. Stock was not changed.** |
| Reinspect Pass / Submit / Approve | Same user. Toast: **Case is now Approved. Inspection record only — stock was not changed.** |
| Close case | Toast: **Case is now Closed. Inspection record only — stock was not changed.** Status **CLOSED** |
| Inquiry | **QC00000003 Closed** · Accept 6 · Reject 0 · **Posted 0**. Destination **Open goods receipt (Suggested)** — not executed |
| Workbench | New QA, cases listed; completed-tasks chip **1** after follow-up Done |
| How to use | Popup opens with record-only steps |
| Viewport 375×812 | How-to-use text **clips** horizontally |

Existing tenant cases also visible (not created this session): **QC00000001** Daily Production (good) Draft; **QC00000002** FG staging Pending review.

Follow-up tables exist on `ERPWeb` (`QaCorrectiveAction`, `QaInspectionAttempt` + unique indexes). After save, follow-up copy is the task disclaimer (not “not installed”). New unsaved case still shows the not-installed line until first schema evaluate.

---

## SQL proof (live `ERPWeb`, after Close)

**QaCase:** `QC00000003` `CLOSED`, Source `INCOMING_PRE_GRN` / `PO2609-0001`, DN `DN-UAT-P5`, InspectedBy=`ADM01`, ReviewedBy=`ADM01`, ReviewDecision=`APPROVED`.

**Line:** item `O6959EE5F` qty 6 / inspected 6, identity `PO2609-0001 / 1 · O6959EE5F`.

**Disposition:** ACCEPT 6 only.

**Follow-up:** ActionNo 1 `FOLLOW_UP` qty 0 Status `DONE` CompletedBy=`ADM01`.

**Attempts:** 3 rows, all `PASS` ADM01 qty 6 (inspect + reinspect saves).

**Grants / holds for this case:** ReceiptGrant 0, ReceiptAllocation 0, FgGrant 0, LogicalHold 0, PhysicalHold 0, NonGoodGrant 0.

**Audit:** CREATED → SUBMITTED → INSPECTED → REVIEW_SUBMITTED → APPROVED → ACTION_CREATED → ACTION_COMPLETED → REINSPECTION_STARTED → INSPECTED (×2) → REVIEW_SUBMITTED → APPROVED → CLOSED. All `ADM01`.

**Inventory (UAT window from 2026-11-11 02:00 UTC):** `IvTrxHistory` rows 0 (any), 0 for `O6959EE5F`, 0 with Ref/Remarks `QC00000003` or `DN-UAT-P5`. `IvTrxBatch` rows 0. No history rows exist for `O6959EE5F` at all.

---

## Still open (not blockers for same-user record-only path)

1. **Unlinked pre-GRN without PO** — Add line opens Lookup, not a typed item/qty form. Server still allows unlinked drafts in tests; the browser has no typed-line path.
2. **Export file contents** — Export clicked earlier; CSV body not captured here.
3. **Keyboard-first / 1920 vs 1366** — DevExpress dropdowns were opened via DOM click because the automation overlay intercepted Playwright clicks. Not a keyboard UAT.
4. **Second source-family live create** — GRN / DP / FG / WIP / MR were listed and two historical cases exist; this session created only incoming.
5. **Reinspect with reject mix** — not run. Reinspect used Pass full amount. Follow-up was a separate qty-0 task completed before reinspect.
6. **SoD remains Off on DEMO** until an owner turns it back on. Posting gates were not enabled.

---

## Recommendation

Browser UAT same-user path is **PASS** for record-only create → inspect → review → follow-up → reinspect same source → approve → close, with **Posted 0** and no inventory history.

Not `QA_INVENTORY_POSTING_COMPLETE`. Owner should confirm `QA_RECORD_AND_FOLLOW_UP_COMPLETE` and whether DEMO SoD should be restored to On.

Do not turn posting gates on. Do not pull origin costing. Do not mix Sales dirty files into a QA commit.
