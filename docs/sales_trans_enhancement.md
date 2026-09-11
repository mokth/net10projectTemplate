# Sales Transaction Module — Enhancement & Defect Register

> **Purpose:** the single backlog for the SO / DO / INV / CN / DN module. Every item below was **verified in the current code** before being recorded; each one carries the evidence needed to open a work item without re-investigation.
> **Companion document:** [sales_trans_logic.md](sales_trans_logic.md) — current behaviour reference (no proposals).
> **Reviewed:** 2026-09-10 · **Scope of review:** `ErpWeb.Core/Sales`, `ErpWeb.Core/Inventory`, `ErpWeb.UI/Sales/Transactions`, `ErpWeb.Model/Entities/Sales`
>
> **Intended use:** this file is the input for a future **enhancement plan**. See [§4 Suggested phasing](#4-suggested-phasing) for a sequencing proposal and [§5 Open product questions](#5-open-product-questions) for the decisions that must be made before the plan can be finalised.

---

## Table of contents

1. [Severity and type legend](#1-severity-and-type-legend)
2. [Register summary](#2-register-summary)
3. [Item detail](#3-item-detail)
4. [Suggested phasing](#4-suggested-phasing)
5. [Open product questions](#5-open-product-questions)
6. [Cross-cutting engineering recommendations](#6-cross-cutting-engineering-recommendations)
7. [Not-yet-implemented capability (gap analysis vs WebForms)](#7-not-yet-implemented-capability-gap-analysis-vs-webforms)

---

## 1. Severity and type legend

**Severity**

| Level | Meaning |
|---|---|
| 🔴 **Critical** | Causes incorrect stock or financial data, or destroys audit evidence. Fix before production scale-up. |
| 🟠 **High** | Causes permanently stuck documents, unrecoverable business states, or referential inconsistency. |
| 🟡 **Medium** | Inconsistent behaviour, asymmetric validation, edge-case money differences, or operator-confusing state. |
| 🔵 **Low** | Code hygiene, defence-in-depth, cosmetic. |

**Type**

| Type | Meaning |
|---|---|
| **Bug** | Verified incorrect behaviour — code does not do what it was intended to do. |
| **Risk** | Verified structural weakness whose impact depends on a product decision. |
| **Enhancement** | New capability, not a defect. |

**Effort**

| Tag | Rough size |
|---|---|
| `S` | ≤ 1 day — localised guard clause / filter + unit test |
| `M` | 2–4 days — method-level rework, migrations, regression tests across documents |
| `L` | 1–2 weeks — new subsystem, service, or data model change |

---

## 2. Register summary

| ID | Title | Type | Severity | Effort | Area |
|---|---|---|---|---|---|
| **R1** | Mixed LinkDo + plain-line invoice can double-deduct stock | Bug | 🔴 Critical | M | Inventory / Invoice |
| **R2** | `Taxes` / `GrossAmnt` wrong for tax-inclusive lines | Bug | 🔴 Critical | M | Calculation (all docs) |
| **R3** | Force-closed DO strands SO billable quantity permanently | Risk | 🟠 High | M | SO / DO |
| **R4** | DO force-close deletes SP batch, orphaning `IvTrx` audit rows | Bug | 🟠 High | S–M | Audit / Inventory |
| **R5** | Invoice rollback/delete do not check for existing CN/DN | Bug | 🟠 High | S | INV / CN |
| **R6** | `decPoint` hard-coded `false` in CN post-time cap check | Bug | 🟡 Medium | S | CN |
| **R7** | `SaDo.TotAmnt` ex-tax vs `SaInvoice/SaCdn.TotAmnt` inc-tax | Risk | 🟡 Medium | M | Cross-module |
| **R8** | Invoice delete ignores `RowVersion` (asymmetric with SO/DO/CN) | Bug | 🟡 Medium | S | INV |
| **R9** | Abandoned draft CN permanently consumes invoice credit capacity | Risk | 🟡 Medium | M | CN |
| **R10** | Assorted hygiene items (see detail) | Low | 🔵 Low | S | Various |
| **E1** | UOM conversion on allocation (currently strict equality) | Enhancement | 🔵 Low | L | Allocation |
| **E2** | SO as an allocation *target* (requires `TargetCustRel` in the unique key) | Enhancement | 🔵 Low | L | Allocation |
| **E3** | Document Flow UI (SO → DO → INV navigation and drill-through) | Enhancement | 🔵 Low | M | UI |
| **E4** | Prevent / hide "Add shipment" on a pure LinkDo invoice | Enhancement | 🟡 Medium | S | UX / Invoice |
| **E5** | AR + GL posting for INV / CN / DN (WebForms parity) | Enhancement | 🟠 High | L | Finance |
| **E6** | E-invoice submission lifecycle (`EInvoice` status, submit/cancel/re-submit) | Enhancement | 🟡 Medium | L | Compliance |
| **E7** | Draft document expiry / housekeeping job | Enhancement | 🔵 Low | M | Operations |
| **E8** | Sales-side allocation reconciliation report | Enhancement | 🔵 Low | M | Operations |

> R1 and R2 are the only items that can produce silently wrong inventory or tax figures. Everything else degrades usability, recoverability, or auditability.

---

## 3. Item detail

### 🔴 R1 — Mixed LinkDo + plain-line invoice can double-deduct stock

| | |
|---|---|
| **Type / Severity / Effort** | Bug · 🔴 Critical · `M` |
| **Area** | `SaInvoiceService` → inventory posting |

**Symptom.** An invoice that contains **both** a LinkDo line (goods already physically shipped and stock-deducted at DO post) **and** a plain stock line (item code with no source document) will, after "Add shipment" + post, **deduct stock a second time for the LinkDo line**.

**Evidence.**

- `SaInvoiceService.AddShipmentAsync` builds `RequiredLines` from **all** `invoice.Details` with no `!LinkDo` filter:
  ```csharp
  RequiredLines = invoice.Details
      .OrderBy(x => x.Line)
      .Select(x => new IvSpRequiredLine { Line = x.Line, ICode = ..., StdQty = x.StdQty, ... })
  ```
- `SaInvoiceService.PostOneAsync` computes the stock subset correctly (`!x.LinkDo && StockControl && StdQty > 0`) but then passes **all** details into the validator:
  ```csharp
  var stockLines = invoice.Details.Where(x => !x.LinkDo && IvSpFifoEligibility.IsShipmentRequired(...)).ToList();
  ...
  RequiredLines = invoice.Details.Select(d => new IvSpRequiredLine { ... }).ToList(),
  ```
- `IvSpShipmentService` allocates SP for **every** required line (`required.First(x => x.Line == take.SoLineNo)`) and has no `LinkDo` concept on `IvSpRequiredLine` — it cannot filter even if asked.
- `IvInventoryPostingService.PostStockOutInTransactionAsync` posts **every** detail of the batch.

**Impact.**

| Scenario | Outcome |
|---|---|
| Pure LinkDo invoice + "Add shipment" | `stockLines.Count == 0` → batch never posted, but a stale `NEW` SP batch is left behind (orphan reservation). Confusing but not financially wrong. |
| Mixed invoice + "Add shipment" + post | **LinkDo lines are posted as a second `SalesOut`** → inventory understated, goods deducted twice. |

**Proposed fix.**

1. Filter `RequiredLines` by `!LinkDo` in `SaInvoiceService.AddShipmentAsync`.
2. Filter `RequiredLines` by `!LinkDo` in `SaInvoiceService.PostOneAsync` (validator input only — keep `stockLines` for the batch-required decision).
3. Defensively hide/disable "Add shipment" in `SaInvoice.razor` when every stock line is LinkDo, and surface a clear message if invoked.
4. Optionally add a `LinkDo`/`IsAlreadyShipped` flag to `IvSpRequiredLine` so the shipment layer can self-defend rather than relying on the caller.

**Acceptance criteria.**

- Given an invoice with 1 LinkDo stock line + 1 plain stock line, after post the LinkDo line's item shows **no** additional `SalesOut` transaction (assert `IvTrx` count and `IvBalLoc` movement for that item/warehouse).
- Given a pure LinkDo invoice, "Add shipment" is unavailable and no `IvTrxBatch` is created for `RefNo = invNo`.
- Existing LinkDo-only posting behaviour is unchanged.

**Tests to add.** Extend `ErpWeb.Tests` with `Mixed_invoice_LinkDo_plus_plain_does_not_double_deduct` and `Pure_LinkDo_invoice_adds_no_shipment_batch`.

---

### 🔴 R2 — `Taxes` / `GrossAmnt` are wrong for tax-inclusive lines

| | |
|---|---|
| **Type / Severity / Effort** | Bug · 🔴 Critical · `M` |
| **Area** | `SaInvoiceCalc.CalculateLine` — consumed by SO, DO, INV, CN, DN |

**Symptom.** For lines flagged `IsInclusive`, the unit price is never un-taxed. Header `Taxes` and `GrossAmnt` therefore do not represent output tax / net revenue, and `HasTax()` (which uses `Taxes`) returns `false` for a taxed inclusive document.

**Evidence.** `SaInvoiceCalc.CalculateLine`:

```csharp
line.Amount = Money(qty * unitPrice);            // note: no /(1 + tax%) for inclusive
...
if (line.IsInclusive)
    line.TaxAmt = Money((unitPrice - discountPerUnit) * qty, TaxDecimalPlaces) - line.NetAmount;
```

With `d = discountPerUnit` and `t = taxPercent`:

$$
\text{NetAmount}_{\text{incl}} = qty\cdot UP - qty\cdot\frac{d}{1+t},
\qquad
\text{TaxAmt}_{\text{incl}} = qty(UP-d) - \text{NetAmount} = -\frac{qty\cdot d\cdot t}{1+t}
$$

| Case (qty 1, UP 110, t 10%) | Discount | `NetAmount` | `TaxAmt` | `TotAmnt` | Correct? |
|---|---|---|---|---|---|
| Inclusive, no discount | 0 | 110 | **0** | 110 | total ✅ / tax ❌ |
| Inclusive, discount 11 | 11 | 100 | **−1** | 99 | total ✅ / gross & tax ❌ |
| Exclusive, no discount | 0 | 100 | 10 | 110 | ✅ |

The WebForms reference (`docs/cdn-logic.md` §6) un-taxes first:
`exclusiveUnitPrice = UnitPrice / (1 + taxPercent / 100)`.

Additional divergence: `Amount` is stored tax-inclusive for inclusive lines; the legacy stores it ex-tax.

**Impact.**

1. **Period tax reporting**: inclusive documents report zero tax.
2. **`HasTax` gating**: `ValidateCommercialReadinessAsync` requires a tax GL only when `HasTax(Taxes)` — so inclusive invoices post **without** a tax GL branch.
3. Any future AR/GL post would mis-state output tax and revenue.
4. Negative `Taxes` is a mathematically impossible value to surface to a user or a tax officer.

**Reachable in production:** yes — `ErpWeb.UI/Sales/Transactions/SaInvoice.razor` line ~577 exposes a **"Tax inclusive"** checkbox (`Popup.IsInclusive`).

**Proposed fix.**

1. **Product decision first** (see [open question Q1](#5-open-product-questions)): is `IsInclusive` meant to mean "the entered unit price includes tax" (legacy semantics)?
2. If yes: in `CalculateLine`, when `IsInclusive`, derive `exclusiveUnitPrice = UnitPrice / (1 + t)` and compute `Amount`, `NetAmount`, `TaxAmt` from it; keep `TotAmnt` unchanged (it is already correct).
3. Reconcile `ApplyTaxAdaptiveRounding`'s inclusive branch (`Amount == NetAmount`) with the new semantics — after the fix, exclusive-basis `Amount` and `NetAmount` are still equal when there is no discount, so the branch likely still holds; assert it explicitly.
4. Add a document-level regression matrix asserting `GrossAmnt + Taxes == TotAmnt` **and** `GrossAmnt` is ex-tax, for both inclusive and exclusive, both with and without discounts, across SO / DO / INV / CN.

**Acceptance criteria.**

- For a 1-line inclusive document (qty 1, price 110, tax 10%): `GrossAmnt = 100`, `Taxes = 10`, `TotAmnt = 110`.
- With an 11 discount: `GrossAmnt = 90`, `Taxes = 9`, `TotAmnt = 99`.
- `HasTax()` returns `true` for the above, so the tax-GL gate engages.
- Invariant `GrossAmnt + Taxes == TotAmnt` holds for every document type in a property-based test.
- Existing exclusive-mode amounts are byte-for-byte unchanged.

---

### 🟠 R3 — Force-closed DO strands SO billable quantity permanently

| | |
|---|---|
| **Type / Severity / Effort** | Risk · 🟠 High · `M` |
| **Area** | `SaSoLineReserve`, `SaDoService`, `SaSoService` |

**Symptom.** Force-closing a `POSTED` DO consumes SO billable capacity forever, while simultaneously removing the only billing route for that capacity. The SO line can never reach `InvoicedQty == OrderQty`, so `FULLY_CONSUMED` is unreachable and the line is stuck until someone force-closes the SO itself.

**Evidence.**

- `SaSoLineReserve.SumDoQtyAsync(newOnly: false)` includes `Status ∈ { NEW, POSTED, CLOSED }`:
  ```csharp
  : (h.Status == SaDoStatuses.New || h.Status == SaDoStatuses.Posted || h.Status == SaDoStatuses.Closed)
  ```
- `SaDoService.GetBillableLinesAsync` returns only `Status == SaDoStatuses.Posted`.
- `SaDocApplicationService.AllocateAsync` rejects a `CLOSED` DO (`ALLOC_CLOSED`).
- `SaDoService.ForceCloseOneAsync` sets `CLOSED` only — it does **not** reverse the `SO_DO` allocation, so `DeliveredQty` also remains.

**Net effect after force-closing a DO for the full SO quantity:**

| Projection | Value | Consequence |
|---|---|---|
| `DeliveredQty` | unchanged (full) | SO never returns to `NEW`; `FulfillmentStatus = FULL` |
| `InvoicedQty` | unchanged (0) | `BillingStatus = NONE` |
| `RemainingBillable` | 0 | "Add from SO" picker shows nothing |
| DO picker | excludes `CLOSED` | DO can never be billed |

The only exit is `SO ForceClose`, which itself reverses nothing.

**Proposed fix — requires a product decision** ([Q2](#5-open-product-questions)):

- **Option A — "force-close means write off".** Keep status-only semantics but stop `LiveDoQty` from counting `CLOSED` DOs that have no `DO→INV` allocation. The SO line then becomes directly invoiceable again, and the write-off is explicit in the DO status.
- **Option B — "force-close means abandon".** On force-close, reverse the `SO_DO` allocation (so `DeliveredQty` drops back) and document that stock is *not* reversed, making the DO a pure status tombstone. This is a bigger semantic change and should print a strong confirmation.
- **Option C — "force-close is forbidden after allocation".** Only allow force-close while no SO allocation exists; route allocated documents through rollback instead.

**Acceptance criteria** (for whichever option is chosen).

- After force-closing a fully-allocated SO-linked DO, the operator can still see and act on the SO line's remaining quantity through exactly one documented route.
- No SO line can become permanently stranded; a test asserts `RemainingDeliverable + RemainingBillable` reaches 0 for a terminal SO.
- The chosen semantics are documented in `sales_trans_logic.md` §8.

---

### 🟠 R4 — DO force-close deletes the SP batch, orphaning `IvTrx` audit rows

| | |
|---|---|
| **Type / Severity / Effort** | Bug · 🟠 High · `S–M` |
| **Area** | `SaDoService.ForceCloseOneAsync` / `IvInventoryPostingService` |

**Symptom.** Force-closing a DO deletes the `IvTrxBatch` and its `IvTrxBatchDetails` without reversing stock and without touching the `IvTrx` rows that the batch produced. Posted stock transactions are left pointing at a batch that no longer exists.

**Evidence.**

- `SaDoService.ForceCloseOneAsync`:
  ```csharp
  var spDetails = await _postingRepo.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
  db.IvTrxBatchDetails.RemoveRange(spDetails);
  db.IvTrxBatches.Remove(batch);
  // No stock reversal — the physical shipment has already occurred.
  ```
- `IvInventoryPostingService` writes `BatchNo` and `RefNo` onto every stock row it creates (`BatchNo = batchNo`, `RefNo = batch.RefNo`) at four separate posting sites.

**Impact.**

- The shipment audit trail is destroyed for a document that still shows as having moved stock.
- Any batch-based reconciliation, reprint, or drill-through will fail or silently omit the movement.
- `IvInventoryReconciliationService` may report unexplained transactions.

**Proposed fix.**

1. Decide whether force-close should retain the batch as a read-only audit artifact (**recommended**) rather than deleting it.
2. If the batch must be removed, first null/anonymise `IvTrx.BatchNo` references or record a `ForceClosedBatch` tombstone row so lineage survives.
3. Add a guard that refuses force-close when the batch contains posted transactions that have no substitute lineage — with a clear message.

**Acceptance criteria.**

- After force-close, every `IvTrx` row that existed before the action is still resolvable to either a live batch or an explicit tombstone.
- A reconciliation query over the DO's date range returns zero unexplained movements.

---

### 🟠 R5 — Invoice rollback/delete do not check for existing CN/DN

| | |
|---|---|
| **Type / Severity / Effort** | Bug · 🟠 High · `S` |
| **Area** | `SaInvoiceService.RollbackOneAsync`, `DeleteAsync` |

**Symptom.** An invoice that has a **posted** credit note pointing at it can be rolled back to `NEW` with no warning. The CN's `InvNo` then references a non-posted invoice, and the CN's cap basis (`invoice.TotAmnt` minus other CNs) silently changes meaning.

**Evidence.** `SaInvoiceService.cs` contains **zero** references to `SaCdn` / `SaCdns` / credit notes:
```
grep "SaCdn|CreditNote|CDN" ErpWeb.Core/Sales/SaInvoiceService.cs  →  (empty)
```
By contrast the legacy WebForms flow (`docs/cdn-logic.md` §4.7) blocked rollback when another `GLTrans` row had `MatchNo = CNNo` and `MatchType = 'CN'`.

**Impact.** The operator can unwind an invoice while a customer credit exists against it, producing a state that no downstream reconciliation explains. Rolling the invoice forward again does not restore the original amounts, so the CN cap may now be satisfied or violated arbitrarily.

**Proposed fix.**

1. In `RollbackOneAsync`, after locking the invoice, query `SaCdn` for rows with `InvNo == invNo` and `Status ∈ {NEW, POSTED}`.
   - `POSTED` → hard block: "Invoice X cannot be rolled back because credit/debit note Y is posted against it."
   - `NEW` (draft) → either block, or offer a confirmation listing the drafts to delete first.
2. Apply the same guard in `DeleteAsync` (an invoice can already only be deleted while `NEW`, but a CN can reference a `NEW` invoice only if the CN was created while it was posted and then the invoice was rolled back — so both paths need the check).
3. Add the symmetric guard on the CN side if the invoice rollback is ever bypassed.

**Acceptance criteria.**

- Rolling back / deleting an invoice with a posted CN fails with an actionable message naming the CN.
- Deleting the CN first, then rolling back the invoice, succeeds.
- Regression test `Invoice_rollback_blocked_by_posted_credit_note`.

---

### 🟡 R6 — `decPoint` hard-coded `false` in the CN post-time cap check

| | |
|---|---|
| **Type / Severity / Effort** | Bug · 🟡 Medium · `S` |
| **Area** | `SaCdnService.PostOneAsync` |

**Symptom.** The credit-note remaining check rounds to 2 decimals at post time even for a customer configured with `DecPoint = true` (whole-currency). Save and update use the correct customer flag, so the two gates can disagree.

**Evidence.** `SaCdnService.PostOneAsync`:

```csharp
var decPoint = false; // Will check properly below
var otherAmounts = await _cdns.ListOtherCnTotAmntsAsync(db, ..., cdn.InvNo, docNo, cancellationToken);
var eval = SaCdnCalc.EvaluateRemaining(locked.Invoice.TotAmnt, otherAmounts, cdn.TotAmnt, decPoint);
```

The variable is never corrected, even though the invoice/customer are already loaded here.

**Impact.** Up to a 0.01 discrepancy between the save-time and post-time gates for 0-decimal customers — a CN can save successfully and then fail to post (or vice versa), with no user-visible explanation.

**Proposed fix.** Load the customer's `DecPoint` (or reuse the invoice's customer) and pass it. Prefer deriving it once in `PrepareLinesAsync` and carrying it on the CN/result rather than re-reading.

**Acceptance criteria.**

- For a `DecPoint = true` customer, save and post accept and reject exactly the same set of amounts.
- Test with a CN total that differs from the remaining amount by 0.005 in both directions.

---

### 🟡 R7 — `SaDo.TotAmnt` is ex-tax while `SaInvoice`/`SaCdn.TotAmnt` are inc-tax

| | |
|---|---|
| **Type / Severity / Effort** | Risk · 🟡 Medium · `M` |
| **Area** | `SaDoService.ApplyCalculatedTotals` vs `SaInvoiceCalc.CalculateHeader` |

**Symptom.** The same column name carries two different meanings depending on the document type.

**Evidence.**

| Document | `GrossAmnt` | `Taxes` | `TotAmnt` |
|---|---|---|---|
| DO (`SaDoService`) | `Σ Calc.Amount` (list, pre-discount) | `Σ Calc.TaxAmt` | `Σ Calc.NetAmount` → **ex-tax** |
| INV / CN / DN (`CalculateHeader`) | `Σ Calc.NetAmount` (post-discount) | `Σ Calc.TaxAmt` | `GrossAmnt + Taxes` → **inc-tax** |

The divergence exists on **both** fields, not just `TotAmnt`: DO's `GrossAmnt` is a pre-discount list amount while INV's is a post-discount net amount.

**Impact.** Any list column, report, dashboard total, or AR posting that treats `TotAmnt` uniformly across DO and INV will be wrong. A DO list showing "Amount" next to an invoice list showing "Amount" looks consistent to a user but is not. Comparison of "shipped value" vs "billed value" is not currently possible without a correction factor.

**Proposed fix.**

- **Option A (recommended, lower risk):** keep the physical columns as-is but document the semantics explicitly in the DO/INV list captions and introduce a shared read-model projection (`SalesDocTotals`) that exposes `GrossExTax`, `Tax`, and `TotalIncTax` regardless of source. Migrate reports to the projection.
- **Option B:** normalise `SaDo` to the invoice convention with a data migration and backfill.

**Acceptance criteria.**

- Every list/report that aggregates `TotAmnt` across document types uses the normalised projection.
- A test asserts `DO.TotAmnt + DO.Taxes == INV-equivalent inc-tax total` for a fully-billed DO.
- A migration script exists if Option B is chosen, with before/after row-count and sum reconciliation.

---

### 🟡 R8 — Invoice delete ignores `RowVersion`

| | |
|---|---|
| **Type / Severity / Effort** | Bug · 🟡 Medium · `S` |
| **Area** | `SaInvoiceService.DeleteAsync` |

**Symptom.** Delete semantics are inconsistent across the four documents.

| Document | Delete requires `RowVersion` | Concurrency mechanism |
|---|---|---|
| SO | ✅ | explicit compare + `LoadForUpdate` |
| DO | ✅ | explicit compare + `LoadForUpdate` |
| CN/DN | ✅ | explicit compare + `LoadForUpdate` |
| **INV** | ❌ | `LoadForUpdate` only |

**Evidence.** `SaInvoiceService.DeleteAsync` obtains `LockForUpdateAsync` — so it is not racy — but never compares the caller's `RowVersion`, unlike the other three.

**Impact.** A user holding a stale invoice list can delete an invoice that another user edited moments earlier. `LockForUpdate` prevents a lost update, but it does not give the operator the "someone changed this, reload" safety rail the other screens provide.

**Proposed fix.** Change the delete contract to accept `(InvNo, RowVersion)` pairs (mirroring `SaDoKeyedRequest` / `SaCdnKeyedRequest`), compare, and return `SaInvoiceErrorKind.Concurrency`. Update `SaInvoiceList.razor` to pass the row version it already holds.

**Acceptance criteria.**

- Deleting with a stale `RowVersion` fails with the standard "changed by another user" concurrency message.
- Deleting with the current `RowVersion` succeeds.
- UI compiles and passes the version; no other caller regresses.

---

### 🟡 R9 — Abandoned draft CN permanently consumes invoice credit capacity

| | |
|---|---|
| **Type / Severity / Effort** | Risk · 🟡 Medium · `M` |
| **Area** | `SaCdnRepository.ListOtherCnTotAmntsAsync` |

**Symptom.** `ListOtherCnTotAmntsAsync` includes both `NEW` and `POSTED` credit notes. A draft CN created and then abandoned (browser closed, user forgot) reduces the invoice's creditable amount until someone explicitly deletes it.

**Evidence.**

```csharp
.Where(x => x.CompanyCode == company && x.BranchCode == branch
        && x.Type == SaCdnTypes.CreditNote && x.InvNo == invoice
        && (x.Status == SaCdnStatuses.New || x.Status == SaCdnStatuses.Posted))
```

**Impact.** Operators see "Credit note total exceeds invoice remaining" with no visible culprit, because drafts are not shown in the posted-CN picker. Recovery requires manually finding and deleting the draft.

**Proposed fix (choose one or combine).**

1. **Visibility:** surface a "Reserved by draft CN(s)" indicator on the CN entry screen and on the invoice, listing the draft document numbers.
2. **Housekeeping:** background job that flags or auto-cancels drafts older than N days (pairs with E7).
3. **Explain in the error:** include the draft document numbers in the `CDN_REMAINING` message.

**Acceptance criteria.**

- A user creating a second CN against a fully-drafted invoice sees the draft document number responsible.
- After the draft is deleted, the cap is restored immediately.

---

### 🔵 R10 — Assorted hygiene items

| | | |
|---|---|---|
| **Type / Severity / Effort** | Bug/Low · 🔵 Low · `S` each |

| # | Item | Evidence | Proposed fix |
|---|---|---|---|
| R10.1 | `ApplyTaxAdaptiveRounding(lines, taxPercent)` — the `taxPercent` parameter is **never used** (the body reads `line.TaxPercent`); `SaCdnService` passes the first line's percent while the others pass `0m` | `SaInvoiceCalc.ApplyTaxAdaptiveRounding` | Remove the parameter, or honour it consistently. Today the divergence is harmless; it is a latent trap. |
| R10.2 | `SaInvoiceDetail` has **no `SellingUom` column**, so `AllocateInvoiceLinesAsync` passes `SellingUom = null` and the allocation UOM check becomes a no-op for invoices | `SaInvoiceDetail.cs`; `AllocateInvoiceLinesAsync` sets `SellingUom = null` | Either persist `SellingUom` on the invoice line and populate it from `PreparedLine`, or document explicitly that UOM integrity for invoices is guaranteed only at `PrepareLinesAsync`. |
| R10.3 | Invoice `MapDocument` computes `ShipmentComplete` from SP qty vs `StdQty` for **all** stock lines including LinkDo → a postable pure-LinkDo invoice displays "Open" forever | `SaInvoiceService.MapDocument`: `var complete = !x.StockControl \|\| shipped == IvQty.Round(x.StdQty);` | Treat LinkDo lines as complete (`LinkDo \|\| !StockControl \|\| shipped == StdQty`). Fixes a persistent UI lie that will break any future gate that trusts the flag. |
| R10.4 | `SaCdn.DoNo` is accepted and persisted but **never validated** to exist, or to belong to the same customer | `SaCdnService` write path | Validate like `InvNo`, or remove the field from the UI until it has meaning. |
| R10.5 | `SaCdn.Status` supports only `NEW`/`POSTED`; the legacy `OPEN` edit-lock concept is absent, so two users can open the same `NEW` CN concurrently and the second save wins on `RowVersion` | `SaCdnStatuses` | Accepted departure from legacy. Verify that the last-writer-wins UX messaging is adequate; consider an advisory "being edited by" indicator if it becomes a support issue. |
| R10.6 | `SaDoService.UpdateAsync` deletes and re-inserts all details, renumbering from 1 | `db.SaDoDetails.RemoveRange(...)` + `AddDetails(...)` | Safe today because edit is restricted to `NEW` DOs and the ledger only keys `TargetLineId` for posted documents. **Would become unsafe if NEW-DO allocation is ever introduced** — record this as a pre-condition on any such enhancement. |
| R10.7 | `SaInvoiceService.SaveNewAsync` sets `invoice.DoNo = invNo` (legacy column reuse) | `SaveNewAsync` | Document it in the schema/annotation so nobody treats `SaInvoice.DoNo` as a delivery-order reference. |

---

### 🔵 E1 — UOM conversion on allocation

**Type** Enhancement · 🔵 Low · `L`

Today the allocation layer requires strict string equality on the selling UOM (`UomsEqual`, `ALLOC_UOM_MISMATCH`). Converting between an SO's selling UOM and a DO's selling UOM (e.g. carton → each) is impossible without a manual line split. Introduce a UOM conversion factor (`IvMaster` pack size is already persisted as `StdPsize`) so `AppliedQty` can be normalised to a common base UOM.

**Prerequisite:** decide on the canonical base UOM per item and define rounding ownership (conversion before or after the invariant check).

---

### 🔵 E2 — SO as an allocation target

**Type** Enhancement · 🔵 Low · `L`

`SaDocApplication.TargetCustRel` is always `0`, so the unique key cannot express "many source lines onto one target *revision*". Required for SO-to-SO relationships (e.g. blanket order → call-off order, or quotation → SO conversion with lineage). Also required to support many source lines onto one target line, which is currently forbidden by `ALLOC_MERGE_FORBIDDEN`.

**Impact:** schema change + unique-index change + migration of existing rows (all `TargetCustRel = 0`, so the migration is mechanical).

---

### 🔵 E3 — Document Flow UI

**Type** Enhancement · 🔵 Low · `M`

There is no way to navigate SO → DO → INV → CN from a document. The ledger already holds everything needed. Add a "Related documents" panel / drill-through on the SO, DO, INV, and CN screens driven by a single query over `SaDocApplication` plus `SaCdn.InvNo`.

**Suggestion:** one `ISaDocFlowQuery` service returning a directed graph for a document key, reused by all four screens.

---

### 🟡 E4 — Prevent / hide "Add shipment" on a pure LinkDo invoice

**Type** Enhancement · 🟡 Medium · `S`

`SaInvoice.razor` enables the footer "Add shipment" button whenever `Lines.Count > 0 && HasCustomer`, including on a pure LinkDo invoice where no shipment is needed (and where clicking it currently creates an orphan `NEW` batch). Fold into the R1 fix: disable the button when every stock line is `LinkDo`, and explain why in the footer hint.

---

### 🟠 E5 — AR + GL posting for INV / CN / DN

**Type** Enhancement · 🟠 High · `L`

**This is the largest functional gap between the current Blazor module and the WebForms source.** The document layer is complete (save, post, rollback, stock movement, allocation), but there is no accounting effect:

| WebForms capability | Blazor status |
|---|---|
| CN/DN → `ArCreditNoteHeader/Detail`, `ArDebitNoteHeader/Detail`, `GLTrans` | **not implemented** — `SaCdnService` only flips status and moves stock |
| INV → debtor control / sales / tax GL | **not implemented** |
| Two-phase cross-database post (`ERP` + `Account`) | not modelled |
| "Rollback Account Only" recovery path | not modelled |
| Knock-off / match (`MatchType`, `MatchNo`) | not modelled |
| `AdPara.SalesPostTotal` grouping (per-item vs grouped by `ItemGLCode + TaxGroup + LMW`) | not modelled |

The reference specification, including exact GL line construction, header/detail field mapping, and the two-phase commit ordering, is fully documented in `docs/cdn-logic.md` §7.

**Prerequisite:** R2 (inclusive tax) must be fixed first, otherwise the GL amounts will be wrong for inclusive documents.

**Deliverable shape:** an `IArPostingService` + `IGlPostingService` with an explicit posting journal (so a partial cross-database post can be detected and repaired, replacing the legacy "Rollback Account Only" hack), plus a posting-status column distinct from the document status.

---

### 🟡 E6 — E-invoice submission lifecycle

**Type** Enhancement · 🟡 Medium · `L`

The invoice already stores the compliance fields (`BuyerTin`, `BuyerBrn`, `BuyerRegType`, `GstregNo`, `ExternalDocNo`, `InvEmail`) and `ValidateCommercialReadinessAsync` already enforces them at save time — a strong foundation. What is missing is the submission state machine that the WebForms module had (`EInvoiceStatus`: `Valid` / `Submitted` / cancelled / re-submitted) and its interaction with rollback (legacy blocked rollback when the e-invoice was `Valid` or `Submitted`).

**Suggested scope:** submission record per document, status transition guards on rollback/delete, submission/cancel/re-submit rights, and audit of payloads.

---

### 🔵 E7 — Draft document expiry / housekeeping

**Type** Enhancement · 🔵 Low · `M`

`NEW` DOs and `NEW` invoices reserve SO quantity indefinitely (via `NewDoQty` / `NewSoInvQty`), and `NEW` CNs reserve invoice credit (R9). There is no expiry, no notification, and no admin view of what is holding capacity.

**Suggested scope:** a scheduled job that reports (and optionally auto-cancels after N days) `NEW` documents with no modification for a configurable period, plus a "reservations" admin screen showing SO lines with non-zero `NewDoQty` / `NewSoInvQty` and their owning document.

---

### 🔵 E8 — Sales-side allocation reconciliation report

**Type** Enhancement · 🔵 Low · `M`

`IvInventoryReconciliationService` exists for inventory, but there is no equivalent for allocations. A reconciliation report should prove, per SO revision line:

$$
\text{OrderQty} \ge \text{DeliveredQty} + \text{NewDoQty}
\quad\text{and}\quad
\text{OrderQty} \ge \text{InvoicedQty} + \text{NewSoInvQty} + \text{LiveDoQty}
$$

and that `DeliveredQty` / `InvoicedQty` equal the ledger sums. This would have caught R3 and R7 immediately, and gives operations a self-serve diagnostic instead of a development ticket.

---

## 4. Suggested phasing

A sequencing proposal — adjust once the [open questions](#5-open-product-questions) are answered.

### Phase 0 — Stop the bleeding (target: 1 sprint)

| Item | Why first |
|---|---|
| **R1** LinkDo double-deduct | Only defect that silently corrupts inventory. |
| **R4** audit-row orphaning | Small change, protects evidence. |
| **R5** INV rollback vs CN guard | Small, blocks an unrecoverable state. |
| **R6** `decPoint` | One-line correctness fix. |
| **R8** INV delete `RowVersion` | Small, closes a UX inconsistency. |
| **R10.1, R10.3** | Trivial, remove a lie and a trap. |

### Phase 1 — Tax correctness (target: 1 sprint, may overlap Phase 0 tail)

| Item | Notes |
|---|---|
| **R2** inclusive tax | Needs Q1 answered before coding. Touches all four documents, so it needs a shared calculation test matrix first. |
| **R7** total semantics | Pairs naturally with R2 — both are about making money fields mean one thing. |
| **E8** reconciliation report | Provides the safety net that proves Phase 1 did not regress anything. |

### Phase 2 — Lifecycle semantics (target: 1–2 sprints)

| Item | Notes |
|---|---|
| **R3** force-close semantics | Needs Q2. Decide, then implement Option A/B/C plus documentation. |
| **R9 + E7** draft reservations | Reservations become visible and expire. |
| **E4** Add-shipment UX | Pairs with R1. |
| **E3** document flow UI | Makes the whole lifecycle observable — very high operator value for the effort. |

### Phase 3 — Finance and compliance (target: multi-sprint)

| Item | Notes |
|---|---|
| **E5** AR + GL posting | Depends on R2. The largest single workstream; needs the WebForms spec in `docs/cdn-logic.md` §7 as the functional baseline. |
| **E6** e-invoice lifecycle | Builds on E5's posting-status model. |

### Phase 4 — Structural (opportunistic)

**E1** UOM conversion · **E2** SO as target / many-to-one allocation · any items deferred from earlier phases.

---

## 5. Open product questions

These must be answered before the plan is finalised. Each one gates specific items.

| # | Question | Gates | Why it matters |
|---|---|---|---|
| **Q1** | Is the "Tax inclusive" checkbox meant to mean *the entered unit price includes tax* (legacy semantics), or *store the net amount as inclusive and carry tax separately*? | R2 | Determines whether R2 is a bug fix or a redesign. The legacy spec un-taxes; the current code does not. |
| **Q2** | What should **force-close a DO** mean commercially — write off, abandon, or never allowed once allocated? | R3 | Determines whether `LiveDoQty` should exclude closed DOs, whether allocations are reversed, or whether the action is restricted. |
| **Q3** | When an SO line is only partially shipped and then the DO is closed, should the unshipped remainder return to the SO as directly-invoiceable/deliverable, or be written off with the DO? | R3, E7 | Drives the same implementation as Q2 and defines the operator's recovery path. |
| **Q4** | Should a credit note reduce SO `InvoicedQty` (i.e. re-open billing capacity), or remain a purely financial document as it is today? | E5, and possibly R5 | Today it does neither. The legacy behaved the same way, so this may be intentional — but it should be a conscious choice, documented. |
| **Q5** | Is the DO/INV `TotAmnt` divergence (R7) acceptable long-term, or should `SaDo` be migrated to the invoice convention? | R7 | Option A is cheap and non-breaking; Option B is a migration. |
| **Q6** | Should a `NEW` (draft) CN reserve invoice credit at all, or only `POSTED` ones? | R9 | If only posted, R9 becomes a fix; if drafts reserve, it becomes a visibility/housekeeping feature. |
| **Q7** | Is `SaCdn.DoNo` a real business reference (CN against a delivery order)? | R10.4 | Determines whether to validate it against `SaDo` or remove it from the UI. |

---

## 6. Cross-cutting engineering recommendations

Applies across the whole module rather than to one register item.

1. **Build a shared line-calculation test matrix before touching R2.** One fixture table of `(qty, unitPrice, taxPercent, discounts, isInclusive, decPoint)` with expected `Amount / NetAmount / TaxAmt / GrossAmnt / Taxes / TotAmnt`, asserted for SO, DO, INV, CN. This is the only safe way to change `CalculateLine`, which is shared by all four documents.

2. **Make the invariants executable and property-based.** `SaSoLineReserve.Evaluate` is pure and already the single authority. Wrap it in a property test that generates random sequences of `(draft DO, posted DO, direct INV, LinkDo INV, rollback)` and asserts the two inequalities never go negative and that projections always equal ledger sums. This catches R3-class problems automatically.

3. **Introduce a posting-status model separate from document status.** Today `NEW`/`POSTED`/`CLOSED` conflates "saved", "stock moved", and (future) "accounted". E5 will need this anyway; introducing it early prevents a second migration later.

4. **Make every destructive action auditable.** Post, rollback, force-close, and force-close-with-batch-deletion should all write an audit row recording actor, timestamp, before/after status, and — for force-close — the batch that was removed (R4). This is cheap insurance and enables support to reconstruct history.

5. **Keep the ledger as the only writer, and keep drafts out of it.** The current design (drafts reserve by live query, posted documents reserve by ledger) is sound and self-healing. Resist any enhancement that writes draft rows into `SaDocApplication`; it would reintroduce the compensating-delete problem the design avoids.

6. **Document the semantics of every shared column name once.** `TotAmnt`, `GrossAmnt`, `Amount`, `NetAmount`, `AppliedQty`, `ShippedQty`. R2 and R7 both exist because a shared name came to mean two things. A short schema-contract table in `sales_trans_logic.md` §10.8 is already the start of this.

7. **Add regression tests for every Phase 0/1 fix in the same commit as the fix.** `ErpWeb.Tests/SaDocApplicationTests.cs` already has the harness (SQL Server + in-memory) and the naming convention; reuse it so the invariants stay pinned.

---

## 7. Not-yet-implemented capability (gap analysis vs WebForms)

Recorded here so the enhancement plan accounts for the full scope. Full behavioural reference: `docs/cdn-logic.md`.

| Capability | WebForms source | Blazor status |
|---|---|---|
| CN/DN month-end and account-close checks on post / rollback | `CDNHelper.StartPost` | ❌ not implemented |
| AR + GL writing for CN/DN (`Ar*Header/Detail`, `GLTrans`) | `CDNHelper`, `AccCNDNBL` | ❌ not implemented (E5) |
| `AdPara.SalesPostTotal` grouping (per-item vs grouped) | `CDNHelper.StartPost` | ❌ not implemented (E5) |
| Knock-off / `MatchType`/`MatchNo` matching | `AccCNDNBL` | ❌ not implemented (E5) |
| Cost price lookup for CN lines from `IvBalLoc` | `CNEntry` line calc | ❌ not implemented — `SaCdnDetail.CostPrice` is written from the request |
| Customer return via `AddCR` from `IvTrxHistory` by batch (skip `ReturnType = 'Re-Supply'`) | `CNEntry.AddCR` | ⚠️ partially — returns exist as CR batches, no batch-picker flow |
| Price/discount inheritance from the latest `SaSODetail` when `SO_No` exists | `CNEntry.AddCR` | ❌ not implemented |
| Barcode entry (exclusive-only, qty += 1 for same item) | `CNEntry` | ❌ not implemented |
| `SaSalesAttach` remap from `AUTO` to the real document number | `CNEntry.Save` | ❌ not implemented |
| Audit logging (`HDR` / `DTL`, `DELETE`) | `CDNHelper.DeleteDoc` | ⚠️ `_logger` only, no durable audit table |
| `OPEN` edit-lock status | `BatchStatusHelper` | ⚠️ deliberately replaced by `RowVersion` (R10.5) |
| E-invoice submit / cancel / re-submit | `EInvoiceScreenID` | ❌ not implemented (E6) |
| Customer-return reason codes | `IvReturnReasons` exists in inventory | ⚠️ not wired into the CN line |

**Recommendation:** treat E5 as its own programme of work with its own plan document. It is larger than everything else in this register combined, and its correctness depends on Phase 1 (R2) landing first.
