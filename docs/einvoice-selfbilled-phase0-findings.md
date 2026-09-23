# Phase 0 findings — LHDN self-billed e-Invoice party direction (types 11 / 12 / 13)

**Date:** 2026-09-23
**Status:** COMPLETE — the contract is recorded, and it contradicts the assumption the original
self-billed plan was built on.
**Applies to:** `plans/plan-poSelfBilledEInvoice.prompt.md` (D2, Prerequisite 0, Phase 3 steps 16 and 35)
**Correction plan:** `plans/plan-selfBilledPartyReversal.prompt.md`

---

## 1. The contract

A self-billed e-Invoice is issued by the **BUYER**, not by the supplier:

- **LHDN SDK — Self-Billed Invoice v1.1 data structure**
  - Supplier = "supplier providing the goods / services".
  - Buyer = "recipient of goods / services **who is the issuer of the self-billed e-Invoice**".
- **LHDN type page:** "Self-Billed Invoice … will be issued by the Buyer".
- **LHDN FAQ, duplicate-document rule:** "Supplier TIN (for normal e-Invoices), **Buyer TIN** (for
  self-billed e-Invoices)".

So for a self-billed document the UBL payload must carry:

| Payload block | Party |
|---|---|
| `AccountingSupplierParty` | the **VENDOR** (who supplied the goods / services) |
| `AccountingCustomerParty` | **OUR COMPANY** (the buyer, and the issuer of the document) |

The ERP keeps its own identity for everything that is not the payload's party blocks: credentials,
`onbehalfof`, `issuerTin` lookup, submission grouping and the lifecycle/state machine stay the company's.

## 2. Local evidence (what the repo actually did)

- `ErpWeb/App_Data/einvoice/SBI2609-0001_inv_json.json` — a self-billed invoice generated before this
  change — carries **"Demo Company Sdn. Bhd."** under `AccountingSupplierParty`, which is the company
  profile also seen in `INV2609-0005_inv_json.json`.
- The code matched that: `SaEInvoiceService.BuildSbInvoiceSourceAsync` / `BuildSbCdnSourceAsync` filled
  the `Customer*` block from the vendor master and let the company profile become the `Supplier`, and
  `EInvoiceValidator` therefore reported vendor gaps as `Buyer.*`, and `LoadVendorProfileAsync` keyed a
  missing vendor as `Buyer.Vendor`.
- **Conclusion: D2 was reversed.** The repo emitted the company as Supplier and the vendor as Customer;
  the contract requires the opposite.

## 3. Scope of the error (why it is safe to fix)

- Every self-billed submission to date is an **LHDN sandbox** document issued during development. There
  is no production self-billed history to reconcile.
- **No migration and no schema change** is part of the fix, and **no script is to be written** for the
  historical rows: the rebuild window is closed for `SUBMITTED`/`VALID` rows, so a re-issue needed for
  smoke testing must be a new document.
- The sandbox acceptance test after the fix is the authority: submit SBI (11), SBC (12) and SBD (13) and
  confirm the portal shows the vendor as Supplier and the company as Buyer.

## 4. Verified constraints the fix is built on

These were checked against the code, not assumed, and they shape the correction plan:

1. **`EInvoiceSourceDocument` is not persisted.** It is rebuilt from the ERP document at exactly two
   entry points — `ValidateAsync` and `SubmitBatchAsync` (`SaEInvoiceService.BuildSourceAsync`). The XML
   doc at `SaEInvoiceService.cs:2905-2913` states that the vendor block is read live from the vendor
   master "and there is deliberately **no snapshot**"; `RefreshAsync` only reads MyInvois and applies the
   status, and `RecoverAsync` never resubmits.
   **Consequence:** the guarantee that an accepted payload can never change is the **submit gate**
   (`SUBMITTED`/`VALID` are refused as already submitted), not a stored snapshot. A `FAILED`/`INVALID`
   document re-submitted after a vendor-master edit does pick up the new vendor values — exactly the
   trade-off the sales buyer address already makes (D-1). True freezing would mean mirroring the
   `BuyerIdentity` freeze (`SourceBuildResult.ResolvedBuyer`, `EInvoiceStatuses.IsBuyerIdentityFrozen`),
   which needs new columns; that is deliberately out of scope.
2. **Vendor validation is self-billed-scope only (Option B).** `PoSupplierService.ValidateModel` runs on
   every vendor create/edit, so making MSIC / business description mandatory there would block edits of
   the whole existing vendor base. They are enforced on the self-billed submit path instead, keyed
   `Supplier.Msic` / `Supplier.BizDescription`.
3. **`dbo.POSupplier.BizDesc` is `nvarchar(200)`** (`scripts/create-po-masters.sql:334`,
   `PoSupplierConfiguration.cs:85`). The LHDN maximum of 300 is therefore **not** adopted — that would
   need a schema change. The pre-existing UI `maxlength="500"` (`PoSuppEntry.razor`) exceeded the column
   and is corrected **down** to 200.
4. **`MiscCode` stays a string.** MSIC codes may start with a zero (`01234` must never become `1234`);
   validation checks the 5-digit format only.
5. **SBD has no separate source builder.** SBD is `PoSbCdn` with `Type = DN`, mapped to document type 13
   and built by `BuildSbCdnSourceAsync`.

## 5. Invariants that must survive the fix

```
NORMAL SALES                             SELF-BILLED (types 11 / 12 / 13)
------------                             ---------------------------------
Company profile   -> Supplier            Vendor master (POSupplier) -> Supplier
Customer master   -> Buyer               Company profile            -> Buyer
```

Three layers stay separate, and collapsing any two of them is a defect:

1. **Payload parties** — what changes here.
2. **Source-document party data** — in-memory, per build, never persisted.
3. **Submission / credential identity** — `EInvoiceDocumentState.Supplier`, `ApplyCompanyCredentials`,
   the submission grouping and `issuerTin` stay the **company's** for self-billed documents.
