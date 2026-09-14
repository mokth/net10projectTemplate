---
name: Revise Procurement Plan v2
overview: Revise `plans/procurement-planv2.md` in place to v2.3, locking the third-review clarifications (validator split, GR rollback vs invoice, CN-to-INV remaining qty, FinClosed under lock, source-of-truth table). Document only — no application code.
todos:
  - id: qty-model
    content: Add canonical quantity model, source-of-truth table, persisted-equals-helper invariant, and AllowedInvoicedQty preamble to procurement-planv2.md
    status: completed
  - id: phase1-2
    content: Split validators; reject GR rollback that would over-invoice; VR-vs-GR-rollback rationale; zero-qty policy; DBA integrity report
    status: completed
  - id: phase3-lock
    content: CN references one posted INV remaining qty; CN post/rollback math; FinClosed from all lines under lock; price-override either-direction + document auth
    status: completed
  - id: truth-table
    content: Add GR-rollback-after-invoice reject row, CN-per-invoice remaining row, changelog + status v2.3
    status: completed
isProject: false
---

# Revise Procurement Plan v2.3

Update [plans/procurement-planv2.md](plans/procurement-planv2.md) in place. Do not implement services, schema, or tests until this spec is accepted.

**New status line:** `Implementation-ready (v2.3 — final clarifications locked)`

Retain the five-phase structure, lock order, draft-vs-post split, transaction atomicity, and deletion of `ApplyReturnQtyAsync`. Replace every formula that treats `RecvQty + ReturnQty` as additive.

v2.1 and v2.2 locks stay. This pass only adds the third-review clarifications — no architectural redesign.

---

## Locked decisions from the third review

- **AllowedInvoicedQty is always actual NetReceivedQty.** If NetReceived exceeds Ordered, that is receive tolerance only — not an invoice tolerance. State this immediately above the truth table.
- **Validator split:** `ValidateQtyInvariants` = persisted PO-line state is internally valid. `ValidateReceiptAgainstTolerance` = proposed GR result under receive tolerance. PO edit/revise floors live in a third helper (`ValidateOrderQtyChange`), not in the GR validator.
- **GR rollback after invoice is rejected** when it would make `InvoicedQty > NewNetReceivedQty`. Over-invoice from GR rollback is not an allowed exception.
- **VR after invoice stays allowed** (physical return after supplier invoice; CN is the correction). GR rollback after invoice is rejected (undoing the receipt; reverse INV/CN first). Document this distinction in Phase 2 and Phase 1.
- **CN references one specific posted INV** (`PoInvoice.InvNo`). CN qty cannot exceed that invoice's remaining qty on the same PO line (INV line qty minus posted CNs for that InvNo + PO line). Not a free allocation against accumulated PO `InvoicedQty` alone. Cannot rollback an INV that still has posted CNs referencing it.
- **CN rollback:** `InvoicedQty += CNQty`, then `0 <= InvoicedQty`, then recompute Invoiceable / OverInvoiced / FinClosed.
- **FinClosed** is computed from the **complete** set of PO lines after the affected line is updated, while header + all details remain locked.
- **Quantities must be > 0:** active PO detail `OrderedQty > 0`; GR/NG/VR/INV/CN transaction qty `> 0`.
- **Invoice price-tolerance override may increase or decrease** the vendor-item value (`0..100`). No extra permission beyond existing `PO_INVOICE` EDIT/POST. Document that a 100% override is a control bypass accepted under document authorization (no new permission in this plan).
- **Persisted equals helper:** after every write-back, `BalanceQty == ComputeBalance(...)` and `OverRecvQty == ComputeOverRecv(...)`.
- **DBA pre-enable report** for existing PO lines (see §8).

---

## Locked decisions from the second review

- **Invoice qty tolerance is receive-only.** `AllowedInvoicedQty = NetReceivedQty`. Qty tolerance may raise `AllowedRecvQty` above `OrderedQty`; it must not raise invoice qty above net received.
- **CN is quantity-only.** `Type=CN` qty reduces `InvoicedQty`. Value-only credit notes are out of scope.
- **No negative `InvoiceableQty`.** Split over-invoice into `OverInvoicedQty`.
- **PO revise cannot go below net received or invoiced.**
- **VR is prohibited on service / non-stock (NG) lines.**
- **Invoice UOM must equal the PO line `PurchaseUom`.** No conversion in this plan.
- **Prices and tolerances are non-negative.** Master and invoice-override tolerances are `0..100`.
- **`RecvQty` is the current effective posted GR/NG quantity** after posts and rollbacks, not an immutable historical total. VR never decreases it.

---

## 1. Canonical quantity model (new §3.5)

Persist the starred fields on `PoOrderDetail`; derive the rest in `PoOrderCalc`.

- `OrderedQty`* = `PoPurQty`
- `ReceivedQty`* = `RecvQty` — current effective posted GR/NG qty after all posts and rollbacks. Not a lifetime cumulative. VR never decreases this.
- `ReturnedQty`* = `ReturnQty` — current effective posted VR qty after posts and rollbacks
- `InvoicedQty`* — current effective posted INV qty minus posted CN qty
- `OverRecvQty`* — recomputed, never incremented
- `BalanceQty`* — recomputed, never incremented
- `NetReceivedQty` = `ReceivedQty - ReturnedQty`
- `InvoiceableQty` = `Max(0, NetReceivedQty - InvoicedQty)`
- `OverInvoicedQty` = `Max(0, InvoicedQty - NetReceivedQty)`

Locked formulas (`PoOrderCalc`, `AwayFromZero`, 4 dp):

```
NetReceived(recv, ret)     = RoundQty(recv - ret)
BalanceQty                 = Max(0, OrderedQty - NetReceivedQty)
OverRecvQty                = Max(0, NetReceivedQty - OrderedQty)
InvoiceableQty             = Max(0, RoundQty(NetReceivedQty - InvoicedQty))
OverInvoicedQty            = Max(0, RoundQty(InvoicedQty - NetReceivedQty))
AllowedRecvQty             = RoundQty(OrderedQty * (1 + qtyTol/100))
AllowedInvoicedQty         = NetReceivedQty
```

`InvoiceableQty` is derived only (never persisted). `OverInvoicedQty` is derived only. UI shows remaining-to-invoice as `InvoiceableQty`, and an over-invoiced chip/text when `OverInvoicedQty > 0` (never display `-10`). Over-invoice is a data-quality exception that requires a quantity CN.

**Supersedes v2:** `OverRecvQty = Max(0, RecvQty + ReturnQty - PoPurQty)`.
**Supersedes v2.1:** `AllowedInvoicedQty = NetReceived * (1 + qtyTol/100)` and signed `InvoiceableQty`.

Worked example: PO 100, GR 110, VR 10 → `NetReceived=100`, `Balance=0`, `OverRecv=0`, `AllowedInvoiced=100`.

Recompute `BalanceQty` / `OverRecvQty` from persisted inputs after every GR/VR post and rollback. Do not `+=` those two fields.

**Persisted-equals-helper invariant** (data-repair / migration check):

```
Persisted BalanceQty  == PoOrderCalc.ComputeBalance(...)
Persisted OverRecvQty == PoOrderCalc.ComputeOverRecv(...)
```

Source of truth (implementation reference):

- `OrderedQty` — `PoPurQty` — persisted — written by PO
- `ReceivedQty` — `RecvQty` — persisted — written by GR/NG post and rollback
- `ReturnedQty` — `ReturnQty` — persisted — written by VR post and rollback
- `InvoicedQty` — INV minus CN — persisted — written by INV/CN post and rollback
- `NetReceivedQty` — Recv minus Return — derived
- `BalanceQty` — Ordered minus NetReceived (clamped) — persisted, always recomputed
- `OverRecvQty` — NetReceived minus Ordered (clamped) — persisted, always recomputed
- `InvoiceableQty` — NetReceived minus Invoiced (clamped) — derived
- `OverInvoicedQty` — Invoiced minus NetReceived (clamped) — derived

`ReturnQtyCn` stays unused. Stock returns are VR; quantity correction on the invoice is CN.

**Zero-quantity policy:** reject `OrderedQty <= 0` on active PO detail lines. Reject GR/NG/VR/INV/CN lines with qty `<= 0`. This also keeps percentage price-tolerance formulas defined.

---

## 2. Phase 1 — quantity integrity

In [ErpWeb.Core/Purchase/PoOrderCalc.cs](ErpWeb.Core/Purchase/PoOrderCalc.cs):

- Replace `ComputeBalance` with the clamped net-received formula.
- Add `ComputeNetReceived`, `ComputeOverRecv`, `ComputeInvoiceable`, `ComputeOverInvoiced`, `AllowedInvoicedQty` (= net received).
Authoritative validator split — do not mix these:

- **`ValidateQtyInvariants`** — structural check of the **current persisted** PO line. Recv/Return/Invoiced `>= 0`, `ReturnQty <= RecvQty`, persisted Balance/OverRecv equal the helpers. It must **accept** `OrderedQty < NetReceivedQty` when a tolerated over-receipt is already posted. It is not a GR transaction validator and must not encode receive tolerance or a proposed qty.
- **`ValidateReceiptAgainstTolerance`** — transaction-specific **proposed GR/NG** result after lock: incoming qty `> 0`, `NewNetReceived <= AllowedRecvQty`, `NewReceivedQty >= ExistingReturnedQty`. Allows `NewNet > Ordered` only inside the receive-tolerance ceiling.
- **`ValidateOrderQtyChange`** — PO edit/revise only: `NewOrdered > 0`, `NewOrdered >= NetReceivedQty`, `NewOrdered >= InvoicedQty`. This is the floor that v2.2 incorrectly stuffed into `ValidateQtyInvariants`.

GR posting in [IvInventoryPostingService.cs](ErpWeb.Core/Inventory/IvInventoryPostingService.cs):

- Receive ceiling uses **net** received: after lock, `NewRecvQty - ReturnQty <= AllowedRecvQty`.
- GR rollback, after lock, must satisfy the **resulting** state:
  - `NewReceivedQty >= ExistingReturnedQty`
  - `InvoicedQty <= NewNetReceivedQty` — **reject** otherwise (reverse INV/CN first)
- Then recompute `NetReceived`, `BalanceQty`, `OverRecvQty`, `FinClosed` from **all** PO lines under the same header/detail lock, and operational status.
- After apply, persisted Balance/OverRecv must equal the helpers for both signs.

**Why GR rollback after invoice is rejected but VR after invoice is allowed**

- VR is a new physical return that can happen after the supplier has invoiced. The financial correction is a quantity CN; `OverInvoicedQty > 0` is the expected temporary exception.
- GR rollback undoes the original receipt. That is not a physical return. Allowing it after invoice would manufacture the same over-invoice without a goods movement. Reject it so the chain is INV/CN first, then GR rollback.

UI display at `PoOrder.razor.cs` uses the same clamped helpers.

---

## 3. Phase 2 — Vendor Return

Keep the MI clone, same-transaction PO write-back, lock order, and delete `ApplyReturnQtyAsync`.

Cumulative return, under PO-line lock:

```
NewReturnQty <= RecvQty - ExistingReturnQty
```

Reject VR on **service / non-stock** lines (`PoOrderCalc.IsServiceIType` or `OneTime` / NG family). `FromBalLocId` is irrelevant for those lines; they receive via NG, not VR.

VR post, after lock:

```
ReturnQty += qty
recompute NetReceived, BalanceQty, OverRecvQty
recompute InvoiceableQty / OverInvoicedQty (derived)
recompute FinClosed          // Phase 3 column; wire the call in Phase 2 once it exists
recompute operational status via PoStatusPolicy
```

VR rollback:

```
ReturnQty -= qty   // not below 0
same recompute list as VR post, including FinClosed
```

State this cross-phase dependency **inside Phase 2**, not only in Phase 3, so VR implementers do not skip financial close.

A full return can reopen operational `CLOSED` via `PoStatusPolicy` (never set status directly).

---

## 4. Phase 3 — invoice + quantity CN, no AP/GL

### Documents and lifecycle

`PoInvoice.Type`: `INV` increases `InvoicedQty`; `CN` decreases `InvoicedQty` (not below 0). Debit note out of scope. Value-only CN out of scope (`Qty` must be `> 0`).

**CN-to-invoice reference:** `PoInvoice.InvNo` is required on CN and must be one **specific posted INV** for the same company/branch/vendor. CN qty on a PO line cannot exceed that INV's **remaining qty** on that line:

```
RemainingOnInvLine = PostedInvQty(InvNo, PoLine) - Sum(PostedCnQty where InvNo + PoLine)
newCnQty <= RemainingOnInvLine
```

A CN is not a free draw against the PO's accumulated `InvoicedQty`. Example: INV-001=40, INV-002=60; a CN of 20 must name INV-001 or INV-002 and cannot exceed that document's remaining 40 or 60.

Cannot rollback a posted INV that still has posted CNs referencing it (rollback those CNs first).

Follow the existing ERP convention (GR / SaCdn):

- **NEW (draft):** edit, delete, post. VR drafts may also cancel (MI clone).
- **POSTED:** rollback only. No delete, no qty edit.
- **Rolled back:** status returns to `NEW`; quantities already reversed; re-post is a new apply.
- **Cancelled:** draft-only (VR). Posted documents are not cancelled; they are rolled back.

Repeating Post/Rollback for the same document must not double-apply PO quantities. Gate on status inside the transaction (already-posted post → status conflict / no-op, no second write-back). Applies to GR, VR, INV, and CN.

### 3-way match (qty + UOM)

Authoritative net received, after PO-line lock: `RecvQty - ReturnQty`. History aggregation is a consistency check, not a second formula.

Invoice post:

- Reject invoice-before-receipt (`NetReceivedQty <= 0`)
- Invoice line UOM must equal PO line `PurchaseUom` (map `PoInvoiceDetail.SellingUOM`; no conversion)
- `newInvoiceQty <= AllowedInvoicedQty - InvoicedQty` where `AllowedInvoicedQty = NetReceivedQty`
- After commit: `0 <= InvoicedQty <= NetReceivedQty`
- Client cannot supply `InvoicedQty`

CN post / rollback (state under PO lock, then recompute derived + FinClosed from **all** lines):

```
CN post:     InvoicedQty -= CNQty     // also newCnQty <= RemainingOnInvLine and <= InvoicedQty
CN rollback: InvoicedQty += CNQty
then:        0 <= InvoicedQty
             recompute InvoiceableQty, OverInvoicedQty
             recompute FinClosed from the complete PO line set
```

VR after invoice is **allowed** (see Phase 1 distinction). That creates `OverInvoicedQty > 0` and `InvoiceableQty = 0`. Correction path is a quantity CN against the specific INV. Invoice post still refuses to increase `InvoicedQty` above `NetReceivedQty`.

### Price tolerance

- `PoVendorByItem.PriceTolerance` (`decimal(18,4)`, default 0) + nullable `PoInvoice.PriceTolerance` override
- Shared `PoToleranceLookup` for qty and price
- Basis: unit price, after `RoundPrice` (`POPriceDecimal`, default 6, `AwayFromZero`)
- Master and override validation: `0 <= QtyTolerance <= 100`, `0 <= PriceTolerance <= 100`. Reject negatives. Do not allow malformed master data to create unlimited receive/invoice.
- Invoice override **may increase or decrease** the vendor-item tolerance (`Invoice.PriceTolerance ?? vendorItem.PriceTolerance ?? 0`). No new permission: `PO_INVOICE` EDIT/POST is the authorization. A 100% override can neutralize price match; that is accepted as a document-level control, not a silent master bypass.
- Commercial prices: `PoUnitPrice >= 0`, invoice `UnitPrice >= 0`. Negative prices are not supported (same as [PoMasterRefService](ErpWeb.Core/Purchase/PoMasterRefService.cs) today).
- Formula:

```
EffectivePriceTolerance = Invoice.PriceTolerance ?? vendorItem.PriceTolerance ?? 0
if RoundPrice(poPrice) == 0: accept only RoundPrice(invPrice) == 0
else: |inv - po| / po * 100 <= EffectivePriceTolerance
```

A zero invoice price against a positive PO price is a 100% variance and fails unless the effective tolerance is 100.

### Financial close

`FinClosed` / `FinClosedOn` / `FinClosedBy` on `PoOrder`. Recomputed, not one-way.

```
FinClosed =
    !Cancelled
    AND every non-service line:
        BalanceQty == 0
        AND InvoicedQty == NetReceivedQty
```

Because invoice qty tolerance is off, `RequiredInvoiceQty = AllowedInvoiceQty = NetReceivedQty`. Exact match, not `InvoiceableQty <= 0`.

Service lines use the same receipt-then-invoice rule (NG). A service-only PO can become `FinClosed` after NG + invoice; it still does not auto-operational-close.

Stamp `FinClosedOn`/`By` on false→true; clear on true→false.

Compute `FinClosed` from the **complete** set of PO lines after the affected line is updated, while the PO header and **all** detail rows remain locked. Do not decide close from the changed line alone.

Recompute after: GR post/rollback, VR post/rollback, INV post/rollback, CN post/rollback, PO revise, PO reopen. Force-close does not set `FinClosed`.

### PO revision vs invoiced qty

`ReviseAsync` / PO edit use **`ValidateOrderQtyChange`** (not `ValidateQtyInvariants`):

```
New OrderedQty > 0
New OrderedQty >= NetReceivedQty
New OrderedQty >= InvoicedQty
```

Replace today’s `purchaseQty < RecvQty + ReturnQty` check in [PoOrderService.cs](ErpWeb.Core/Purchase/PoOrderService.cs) (~1289). Quantity reduction after invoicing is not supported.

### Service / NG / VR

Existing split: stock lines → GR; `OneTime` / indirect → NG. [IvGoodsReceiptService.SearchPoLinesAsync](ErpWeb.Core/Inventory/IvGoodsReceiptService.cs) already filters by `OneTime == indirect`. NG posting already writes `RecvQty` the same way as GR.

Lock in the plan:

- Service / non-stock receipt is **NG**; `RecvQty` is populated; `BalanceQty` uses the same net-received formulas; partial NG is allowed.
- Invoice still requires `NetReceivedQty > 0` (no invoice-without-NG).
- **VR is rejected** for service IType and OneTime/NG lines (no stock-out, `FromBalLocId` unused).
- Financial correction after NG+invoice is a quantity CN, not a VR.

### POInvoice commercial / tax scope

**Reuse unchanged** from `PoOrderCalc` / `PoPrCalc`: `ComputeTax`, `PurchaseTaxDec`, `ApplyTwoLevelDiscount` (`ItemDiscount` / `ItemDiscount1` only), `SumTotals`.

**Clone pattern** from `SaCdnService`: numbering inside the save transaction, `NEW`↔`POSTED`, currency rate into `CurrRate`, commercial-readiness GL-code checks (validation only).

**Wire:** `InvNo` required on `INV`; `DocDate` is the invoice/CN date; `ExternalDocNo` extra ref. Add `PostedDate` / `PostedBy` / `RollbackDate` / `RollbackBy` on `PoInvoice`.

**Out of scope:** AP/GL journals, freight, dedicated supplier-invoice-date column, debit note, e-invoice/IRBM, value-only CN, invoice UOM conversion.

---

## 5. Phase 5 / UX / audit links

- Over-receipt chip: `OverRecvQty > 0`
- Partial invoice: `InvoicedQty > 0 && InvoiceableQty > 0`
- Over-invoiced chip: `OverInvoicedQty > 0` (text “N over-invoiced”, not a negative number)
- `AnyReceived` stays `RecvQty > 0`

Cross-document navigation (list/detail links; PO keys already exist):

- PO → GR, PO → VR, PO → Invoice
- Invoice → PO, CN → Invoice, VR → originating PO

§11: remove price-tolerance and FinClosed decision items. Keep the legacy `OPEN` audit for Phase 4.

---

## 6. Quantity truth table (appendix)

**Preamble (print immediately above the table):** `AllowedInvoicedQty` equals actual `NetReceivedQty`. If `NetReceivedQty` exceeds `OrderedQty`, that is solely receive tolerance. It is not an invoice quantity tolerance and must not be reintroduced as `NetReceived * (1 + qtyTol/100)`.

All rows: Ordered = 100, receive qty tol = 10% unless noted. Invoice qty tol does not apply.

- Normal receipt — GR 100 → Recv 100, Return 0, Net 100, Bal 0, OverRecv 0, Inv 0, Invoiceable 100, OverInv 0
- Tolerance receipt — GR 110 → Recv 110, Net 110, Bal 0, OverRecv 10, Invoiceable 110; AllowedRecv 110; AllowedInvoiced 110
- Over-receipt beyond tol — GR 111 → reject
- Partial receipt — GR 40 → Recv 40, Net 40, Bal 60, Invoiceable 40
- Full return — GR 100, VR 100 → Recv 100, Return 100, Net 0, Bal 100, OverRecv 0; status RECEIVED
- Partial return — GR 100, VR 30 → Recv 100, Return 30, Net 70, Bal 30
- Over-return — GR 100, VR 70, VR 40 → second VR rejected
- Partial invoice — GR 100, INV 40 → Inv 40, Invoiceable 60, FinClosed false
- Invoice after return — GR 110, VR 10, INV 100 → Net 100, Inv 100, Invoiceable 0, OverInv 0, FinClosed true
- Invoice above net after return — GR 110, VR 10, INV 110 → **reject** (AllowedInvoiced = 100, not 110)
- Invoice before GR — reject
- Invoice rollback — after INV 40, rollback → Inv 0, Invoiceable = Net, FinClosed false
- GR rollback after over-receipt — GR 110, no invoice, rollback 110 → Recv 0, OverRecv 0, Bal 100
- GR rollback blocked by return — GR 100, VR 40, GR rollback 70 → reject (`NewRecv 30 < Return 40`)
- GR rollback after invoice — GR 100, INV 100, GR rollback → **reject** (`Invoiced 100 > NewNet 0`); rollback INV (or CN then INV) first
- Partial GR rollback still covering invoice — GR1 60 + GR2 40, INV 60, rollback GR2 → NewNet 60, Invoiced 60 → allow
- VR after full invoice — GR 100, INV 100 (FinClosed true), VR 10 → Net 90, Inv 100, Invoiceable 0, OverInv 10, Bal 10, FinClosed false; CN 10 against that INV → Inv 90, OverInv 0, FinClosed if all lines match
- CN exceeding that INV remaining — INV-001 40 + INV-002 60, CN 50 against INV-001 → reject; CN 20 against INV-001 → allow (INV-001 remaining 20)
- CN rollback — after CN 20, rollback → Invoiced += 20, then `0 <= Invoiced`, FinClosed recomputed from all lines
- PO revise below invoiced — Ordered 100, Inv 100, revise to 80 → reject
- PO revise below net received — Recv 100, Return 0, revise to 80 → reject
- Service VR — NG 10 then VR → reject
- Invoice UOM mismatch — PO `PurchaseUom=KG`, invoice UOM `PC` → reject
- Double-post — second Post on already-POSTED INV/VR/GR → no qty change
- Concurrent invoices — two INV 60 against Net 100 → one commits, one fails; Inv never exceeds Net

Required tests: `PoOrderCalcTests`, `IvInventoryPostingServiceTests`, `IvVendorReturnPostingServiceTests`, `PoInvoiceServiceTests`, plus PO revise / service-VR cases on `PoOrderServiceTests`.

---

## 7. DBA pre-enable integrity report

Before enabling v2.3 logic, run a one-time report (manual script, not startup) on existing `PoOrderDetail` rows and fail the cutover on violations (or list them for repair):

- `RecvQty >= 0`, `ReturnQty >= 0`, `ReturnQty <= RecvQty`
- `PoPurQty > 0` on active lines
- `InvoicedQty >= 0` (column will be 0 after add)
- `PoPurQty >= (RecvQty - ReturnQty)` unless a documented historical over-receipt is being accepted into `OverRecvQty`
- `BalanceQty >= 0`
- After backfill: persisted `BalanceQty` / `OverRecvQty` equal the helpers

Existing inconsistent rows are reported, not silently rewritten.

---

## 8. Changelog and status

Append to §12:

- `2026-09-13 | v2.1 — net-received model, cumulative VR, INV+CN, price-tolerance source, FinClosed recompute, truth table, POInvoice tax reuse / AP-GL deferral.`
- `2026-09-13 | v2.2 — invoice qty = net received only; quantity CN only; OverInvoicedQty; RecvQty=effective posted; price/tolerance validation; FinClosed exact match; PO revise vs InvoicedQty; service VR ban; invoice UOM identity; document lifecycle + idempotent post; audit links.`
- `2026-09-13 | v2.3 — validator split; GR rollback rejected when it would over-invoice; VR-vs-GR-rollback rationale; CN bound to one INV remaining qty; CN rollback math; FinClosed from all lines under lock; zero-qty policy; persisted-equals-helper; DBA integrity report; AllowedInvoicedQty preamble; price override either-direction under PO_INVOICE auth.`

No application code, scripts, or tests in this pass. After acceptance, the only deliverable is the revised markdown.
