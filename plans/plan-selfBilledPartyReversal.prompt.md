# Plan: Self-billed e-Invoice party reversal (SBI / SBC / SBD)

Goal: for LHDN document types 11/12/13 only, emit `AccountingSupplierParty` = the VENDOR
(`POSupplier` purchase master, incl. its MSIC + business description) and
`AccountingCustomerParty` = OUR COMPANY profile (the buyer, which is also the issuer).
Sales INV/CN/DN, the vendored `ErpWeb.EInvoiceLib`, the lifecycle/state machine, authorization,
credentials and the submission identity stay exactly as they are.

Reason: LHDN SDK, Self-Billed Invoice v1.1 data structure — Supplier = "supplier providing the
goods / services"; Buyer = "recipient of goods / services **who is the issuer of the self-billed
e-Invoice**". Type page: "Self-Billed Invoice … will be issued by the Buyer". FAQ duplicate rule:
"Supplier TIN (for normal e-Invoices), **Buyer TIN** (for self-billed e-Invoices)". The repo does the
opposite today (`plans/plan-poSelfBilledEInvoice.prompt.md` D2 + unimplemented "Prerequisite 0").

User decisions (2024 session):
- Supplier block sourced from the Purchase master table `POSupplier` (live read, no company fallback).
- Vendor `MiscCode` / `BizDesc` required for self-billing — validated on the self-billed path.
- Existing self-billed documents: leave as-is (development state; LHDN sandbox only).
- Vendor state/country: keep current source + add a small remark below the input that it is used for e-Invoice.
- Rename `PoSupplierBuyerProfile(Resolver)` -> `PoSupplierPartyProfile(Resolver)`.

Decisions confirmed with the user after the second review (2026-09-23):
- **Vendor validation = Option B**: do **not** hard-require `MiscCode`/`BizDesc` on the vendor master;
  enforce on the self-billed submit path only (avoids blocking edits of the existing vendor base).
- **`BizDesc` stays 200** (the DB column is `nvarchar(200)`); the UI `maxlength="500"` is corrected down
  to 200. No schema change.
- **No persisted vendor snapshot (S1)**: `POSupplier` is read live inside each source build, matching the
  existing code design; the guarantee is the submit gate, not a stored snapshot.

---

## Central acceptance criterion (the invariant to implement)

```
NORMAL SALES                             SELF-BILLED (types 11 / 12 / 13)
------------                             ---------------------------------
Company profile   -> Supplier            Vendor master (POSupplier) -> Supplier
Customer master   -> Buyer               Company profile            -> Buyer
```

- "Self-billed" = LHDN document type `11` (SBI), `12` (SBC) **and `13` (SBD)** — all three families,
  not just SBI/SBC.
- No other payload field may change (see Verification 4 and 5).

## Three layers that MUST stay separate

Any implementation that collapses two of these layers is wrong:

1. **Payload parties** — `AccountingSupplierParty` / `AccountingCustomerParty` (this is what changes).
2. **Source-document party data** — what `EInvoiceSourceDocument` holds for one build (in memory only;
   see Party source semantics — it is **not** persisted).
3. **Submission / credential identity** — `EInvoiceDocumentState.Supplier`, `ApplyCompanyCredentials`,
   the submission grouping, `issuerTin`, on-behalf-of; **always the company**, never the vendor.

### CRITICAL INVARIANT — do not reinterpret `EInvoiceDocumentState.Supplier`

```
Payload Supplier Party  !=  submission/credential Supplier identity

Payload             : Supplier = Vendor, Buyer = Company
Submission identity : Supplier/Issuer = Company
```

An agent that sees `Supplier = Vendor` and "fixes" `EInvoiceDocumentState.Supplier` to match would
break authentication and on-behalf-of submission. That is a hard regression; treat it as forbidden.

### Second invariant — the self-billed `Customer*` / Buyer is ALWAYS the company

For a self-billed document:

```
SupplierParty = vendor
Customer*     = company profile

Do not load Customer* from POSupplier.
Do not load Customer* from the purchase document's vendor.
Do not use the vendor as the Buyer.
```

This is the second most likely agent mistake after the `EInvoiceDocumentState.Supplier` one, so it is
stated explicitly: the vendor appears **only** in the Supplier block.

### Party source semantics — source-build-time authoritative source (verified against the code)

**Verified**: `EInvoiceSourceDocument` is **not persisted**. It is rebuilt from the ERP document at
exactly two entry points — `ValidateAsync` and `SubmitBatchAsync` (`SaEInvoiceService.BuildSourceAsync`;
see the class doc at `SaEInvoiceService.cs:2905-2913`, which states that the vendor block is read live
from the vendor master "and there is deliberately **no snapshot**"). `RefreshAsync` only reads MyInvois
and applies status; `RecoverAsync` builds a source for reconciliation and never resubmits.

So: **`POSupplier` is the authoritative source, read live at each source build** (validate, submit). The
resolved data is carried in an in-memory supplier-party record on the source document for the duration
of that single build:

```
POSupplier (read live at source-build time)
        |
        v
PoSupplierPartyProfile
        |
        v
EInvoiceSourceDocument.SupplierParty   <- in-memory, per build; NOT persisted, NOT a freeze
        |
        v
EInvoiceDocumentMapper
        |
        v
AccountingSupplierParty
```

Three consequences:

- The **mapper** must never read `POSupplier` — only the source builder does; the mapper consumes the
  record carried on the source document.
- This is **read-live, no snapshot**, matching the existing sales-buyer address behaviour (D-1). The
  lifecycle guarantee is the existing submit gate (`SUBMITTED`/`VALID` cannot be rebuilt or resubmitted),
  not a stored snapshot. Known trade-off, identical to sales today: a **FAILED/INVALID** document
  re-submitted after a vendor-master edit uses the new vendor data. Record this in the findings doc.
- If true freezing is ever required, mirror the existing buyer freeze
  (`SourceBuildResult.ResolvedBuyer` + `EInvoiceStatuses.IsBuyerIdentityFrozen`) — **out of scope** here,
  because it would need new columns/a schema change.

---

## Protected areas — DO NOT MODIFY

- Sales e-Invoice party mapping (company -> `Supplier`, customer master -> `Customer`).
- Sales `EInvoiceSourceDocument.Customer*` semantics.
- The buyer freeze mechanism (`SourceBuildResult.ResolvedBuyer`, `EInvoiceStatuses.IsBuyerIdentityFrozen`,
  the buyer columns written at the `SUBMITTING` claim).
- The rebuild window documented on `BuildSourceAsync` (`SaEInvoiceService.cs:2905-2913`): a source may
  only be built from `ValidateAsync` / `SubmitBatchAsync`.
- The state/country/reg-type lookup algorithm (only which party receives the resolved value changes).
- `EInvoiceDocumentState.Supplier`.
- `ApplyCompanyCredentials`.
- Submission grouping.
- `issuerTin` in `SearchByDocumentNumberAsync`.
- `OriginUuid` / `RefDocumentNo`.
- `EInvoiceSubmissionWriter` response capture.
- `MapDocumentType`.
- Lifecycle / state machine.
- Authorization.
- Credentials.

---

## Phase 0 — Record the contract (Prerequisite 0, never done)

- Create `docs/einvoice-selfbilled-phase0-findings.md`: LHDN citations above, the local evidence
  (`App_Data/einvoice/SBI2609-0001_inv_json.json` has "Demo Company Sdn. Bhd." under
  `AccountingSupplierParty` = the company profile seen in `INV2609-0005_inv_json.json`), the conclusion
  that D2 is reversed, and the note that all self-billed submissions so far are sandbox-only.
  Also record the verified constraints this plan is built on:
  - `EInvoiceSourceDocument` is **not persisted**; the source is rebuilt at `ValidateAsync` /
    `SubmitBatchAsync` and there is deliberately no snapshot (`SaEInvoiceService.cs:2905-2913`), so the
    vendor is read live at each build and the submit gate is the lifecycle guarantee;
  - vendor `MiscCode`/`BizDesc` are enforced on the **self-billed submit path only** (Option B);
  - `dbo.POSupplier.BizDesc` is `nvarchar(200)`, so the UI `maxlength="500"` is a pre-existing bug that is
    corrected down to 200 — the LHDN 300 maximum is *not* being adopted (no schema change).
- `plans/plan-poSelfBilledEInvoice.prompt.md`: mark D2 (:81), Prerequisite 0 (:31-41), step 16 (:176)
  and step 35 (:204) as SUPERSEDED, pointing to the findings doc + this plan. Keep the rest as history.

## Phase 1 — Party data channel (Core)

1. `ErpWeb.Core/Purchase/PoSupplierBuyerProfileResolver.cs` -> rename file/type to
   `PoSupplierPartyProfileResolver` / `PoSupplierPartyProfile`; add `MsicCode` + `BusinessDescription`;
   `Resolve(PoSupplier)` reads `MiscCode` / `BizDesc` (already on `ErpWeb.Model/Entities/Purchase/PoSupplier.cs:79-80`);
   keep `StateCode ?? State`, `CountryCode ?? Country` precedence and the rest of the mapping.
   Rewrite the class doc: this is the payload's **Supplier** (LHDN) block for a self-billed document.
   This resolver is the **only** place that reads `POSupplier` for the payload, and it runs inside the
   source build (validate / submit). Describe it as the *source-build-time authoritative source* —
   avoid the bare phrase "live source" so the lifecycle cannot be misread (see Party source semantics).
   Do the rename repo-wide: search the whole solution for `PoSupplierBuyerProfile` and
   `PoSupplierBuyerProfileResolver` and update **usages, DI registration, tests, comments, XML docs,
   namespaces and any file references** — not just the two files. A missed reference in another
   project will break the build.
2. `ErpWeb.Core/EInvoice/EInvoiceSourceDocument.cs`: add a nullable supplier-party record property
   (name, TIN, regNo, regType, SST, MSIC, business description, addr1-4, city, state, postal, country,
   phone, email) — populated only for the self-billed families from the `POSupplier` read performed
   during that build. It is an **in-memory, per-build carrier from the builder to the mapper**: it is
   not persisted, not a freeze, and it must not be treated as a snapshot (see Party source semantics).
   Leave the existing `Customer*` flat group untouched (the sales path's frozen buyer snapshot and
   its tests depend on it). For self-billed these fields hold the COMPANY profile — never the vendor.
   Add a comment on that group: **"Do not rename `Customer*`. This is intentionally a
   compatibility-preserving change — for self-billed it holds the company (buyer). Do not refactor
   the common source model or migrate the sales tests as part of the self-billed party reversal."**
3. `ErpWeb.Core/EInvoice/EInvoiceDocumentMapper.cs` `Map(...)`: implement this branch explicitly —

   ```
   if source document carries the self-billed supplier party (sb_*)
       Supplier = source.SupplierParty   // vendor: name, TIN, regNo, regType, SST, addr1-4, city,
                                         //         state, postal, country, phone, email,
                                         //         IndustryClassificationCode (MSIC), BizDesiption
       Customer = source.Customer*       // company profile
   else
       Supplier = company argument       // today's behaviour, unchanged
       Customer = existing Customer*     // customer master
   ```

   Same branch for the reg-type/state/country lookups + their `Supplier.*` / `Buyer.*` error keys —
   **do not change the lookup algorithm** (dedicated code column wins, else the maintained list value,
   translated by `LhdnCodeLookup`); only change *which party receives the resolved values*.
   `MapDocumentType` unchanged.
4. `ErpWeb.Core/EInvoice/SaEInvoiceService.cs`:
   - `LoadVendorProfileAsync` (:3343): returns the extended profile; error keys `Buyer.Vendor` ->
     `Supplier.Vendor`; messages reworded ("vendor … supplies the payload's Supplier block").
   - `BuildSbInvoiceSourceAsync` (:3217) and `BuildSbCdnSourceAsync` (:3267): vendor -> new supplier-party
     property; the `Customer*` fields now come from the COMPANY profile (pass it through `SbSourceContext`
     from `BuildSourceAsync`/`LoadStateAsync` instead of loading it again), applying the same
     `EInvStateCode ?? State` / `EInvCountryCode ?? Country` precedence.
   - Replace both builder doc comments (the "COMPANY … supplies the payload's Supplier" sentences).
   - DO NOT touch: `OriginUuid`/`RefDocumentNo`, `EInvoiceDocumentState.Supplier` (still the company:
     it feeds `ApplyCompanyCredentials` :2008 and the submission grouping :2143),
     `SearchByDocumentNumberAsync` `issuerTin` (:2305) — must stay the company TIN because LHDN keys a
     self-billed document on the Buyer (= us), and the `EInvoiceSubmissionWriter` response capture.

## Phase 2 — Validation & vendor master data

5. `ErpWeb.Core/EInvoice/EInvoiceValidator.cs`: keep `Enabled` as a company-level gate that always runs;
   make the party rule sets reusable; for self-billed apply the *supplier* rule set to the VENDOR
   (name, TIN, regNo, MSIC, business description, addr1, city, postal, E.164 phone, reg type, state,
   country) and the *buyer* rule set to the COMPANY. Sales wiring unchanged.
6. Error keys become `Supplier.*` for the vendor and `Buyer.*` for the company (they currently invert).
   Check `ValidationMessageFormat.KnownLabels` (`ErpWeb.Core/Services/ValidationMessageFormat.cs`, `VendorCode`
   -> "Supplier") and add labels so the PoSb `SdValidationSummary` reads correctly; messages must say where
   to fix ("Fix it on the vendor master." / "Fix it on the company e-Invoice profile.").
   Renaming the validator keys is **not sufficient**: confirm the PoSb summary actually renders the vendor
   failures under the Supplier section and the company failures under the Buyer section (see
   Verification 7).
7. Vendor master (`PoSupplierService.ValidateModel` ~:631) — **decision recorded: Option B.**
   `ValidateModel` runs on every vendor create/edit, so making `MiscCode`/`BizDesc` hard-required there
   would block editing of the existing vendor base (a wide regression well beyond self-billing).
   Therefore:
   - **Do NOT make `MiscCode`/`BizDesc` mandatory in `ValidateModel`.**
   - Enforce them on the **self-billed path only**: a vendor must be complete before a self-billed
     document is submitted (submit-time validation, `Supplier.Msic` / `Supplier.BizDescription`), plus a
     non-blocking UI hint on the vendor screen and the self-billed screens.
   - Keep `MiscCode` a **string**. Do not parse/convert it to a number — MSIC codes include a leading
     zero (`01234` must not become `1234`). Validate format/length only (5 digits).
   - **`BizDesc` length = 200, not 300.** Verified: the DB column is `nvarchar(200)`
     (`scripts/create-po-masters.sql:334`; `PoSupplierConfiguration.cs:85` = 200), so 300 would need a
     schema change — ruled out. Fix the existing UI mismatch instead: `PoSuppEntry.razor:737`
     `maxlength="500"` must become **200**, and validation must be `<= 200`.
   - Do not declare a 300 limit anywhere in code or docs until/unless the column is widened separately.
8. `ErpWeb.UI/Purchase/Masters/PoSuppEntry.razor`: add the small remark under State (:415) / Country (:420)
   — used for the e-Invoice, so the value must be a recognised Malaysian state / country — and a hint on
   MSIC (:365) / TIN (:315) / BizDesc (:737).
9. Update the vendor-facing comments that assert the old direction:
   `PoSupplierResults.cs:80-84`, `PoSupplierService.cs:540`, `:663`.

## Phase 3 — Tests

10. `ErpWeb.Tests/EInvoiceTestHost.cs`: `SeedVendorAsync` (:793) seeds `MiscCode` + `BizDesc`; fix the
    XML doc (:789-792). `SeedCompanyAsync` already supplies every buyer-side field.
11. `ErpWeb.Tests/SaEInvoiceSelfBilledPayloadTests.cs`:
    - direction test (:22-59): rename; `Supplier` = "Vendor Sdn Bhd" + vendor TIN/BRN/addr/city/postal/phone
      + `IndustryClassificationCode` = vendor MSIC + `BizDesciption`; `Customer` = "Demo Sdn Bhd" + company TIN;
    - CN assertions (:82-83) flipped;
    - **cover all three self-billed families**: SBI (11), SBC (12) and **SBD (13)**. Verified: there is
      **no separate SBD builder** — SBD is `PoSbCdn` with `Type = DN`, mapped to document type 13 and
      built by `BuildSbCdnSourceAsync`. Assert SBD's direction explicitly anyway;
    - vendor-incomplete refusal (:149-165) re-keyed `Buyer.Tin`/`Buyer.Phone` -> `Supplier.Tin`/`Supplier.Phone`;
      add a blank-MSIC/BizDesc refusal case;
    - keep type code 11, no-origin, origin-UUID, menu, persistence assertions.
12. Source-level and lifecycle tests:
    - around `BuildSbInvoiceSourceAsync` (11) and `BuildSbCdnSourceAsync` (12 **and** 13 — no separate SBD
      builder exists), assert `source.SupplierParty` = vendor and `source.Customer*` = company; this
      isolates source construction from payload mapping;
    - **no-rebuild test**: once a document is `SUBMITTED`/`VALID`, editing the vendor master
      (name/TIN/MSIC/address) must not change what can be submitted — the submit gate refuses
      ("already been submitted") and no new payload may be built; also assert `RefreshAsync` /
      `RecoverAsync` never rebuild a source;
    - document (do not assert as a pass) that a `FAILED`/`INVALID` re-submit picks up the live vendor
      values — the same trade-off as the existing sales-buyer address behaviour, because there is
      deliberately no persisted snapshot.
13. Validation error routing: test **both** directions, not just the message text —
    vendor failures under `Supplier.Tin` / `Supplier.Msic` / `Supplier.BizDescription` / `Supplier.Phone`,
    company failures under `Buyer.Tin` / `Buyer.Name` / `Buyer.Address`, asserting the summary groups them
    correctly.
14. Guard the sales path: assert a normal invoice still emits company -> `Supplier` and customer master ->
    `Customer` (extend an existing sales payload test); `SaEInvoiceBuyerSourcingTests` /
    `SaEInvoiceBuyerIdentityTests` stay green as the regression net.
15. Guard credentials: self-billed submit still uses the company profile for on-behalf/version
    (`FakeSubmitDocumentHelper` capture).

## Phase 4 — Docs & naming cleanup

16. Fix the misleading docs/comments: `PoSbInvoice.cs:3-9`, `PoSbCdn.cs:3-9`,
    `SaEInvoiceService.cs:3209-3215`/`:3260-3266`, `EInvoiceSourceDocument.cs:40` ("Buyer / customer
    snapshot (frozen on the document)" — false for self-billed),
    `ErpWeb.UI/Purchase/Transactions/PoSbInvoice.razor.cs:14-16`, `PoSbCdn.razor.cs:11-19`,
    `ErpWeb.Tests/EInvoiceTestHost.cs:789-792`, `ErpWeb.Tests/PoSupplierServiceTests.cs:235`.
17. `ErpWeb.UI/Admin/AdminCompany.razor:401-403` hint ("Supplier profile for MyInvois…") — re-scope: the
    company identity is the supplier block on sales documents and the buyer block on self-billed documents.
18. Optional stale-doc cleanup `docs/einvoice-history.md:479,641,766` ("self-billed … not wired").

## Phase 5 — Data

19. No migration, no schema change. **DO NOT write a migration or update script for historical
    self-billed e-Invoice documents.** Record in the findings doc that the only self-billed submissions
    to date are LHDN **sandbox** documents issued during development with the parties swapped; the
    rebuild window is closed for SUBMITTED/VALID rows, so any re-issue needed for smoke testing must be
    a new document.

---

## Verification

1. `dotnet build ErpWeb.slnx` -> 0 errors.
2. `dotnet test` (ErpWeb.Tests): self-billed suite green; the 15 known pre-existing failures
   (13 SaCust/PoSupplier GL-code+phone WIP, 2 `EInvoiceDocumentTypeMap` namespace) must not grow.
   New vendor-master MSIC/BizDesc rules will break `PoSupplierServiceTests` fixtures that don't set them
   — update those fixtures in the same change.
3. Regenerate a self-billed payload for **each** family (writer drops JSON into
   `Einvoice:EInv_JsonPath` = `App_Data/einvoice`) and confirm vendor Supplier / company Buyer for:
   - SBI (type 11)
   - SBC (type 12)
   - SBD (type 13) — `PoSbCdn` with `Type = DN`; it shares `BuildSbCdnSourceAsync`, but still verify it.
   `AccountingSupplierParty` = vendor name/TIN/BRN/MSIC/business description;
   `AccountingCustomerParty` = the company; `BillingReference` unchanged.
4. Self-billed regression: compare against a pre-change self-billed JSON — everything must be identical
   **except the party blocks intentionally reversed**. Check `InvoiceTypeCode`, `BillingReference`,
   `InvoiceLine`, tax, totals, currency, dates, document references and UUID/origin are unchanged.
   If present in the current JSON, also inspect `SupplierTax`, `BuyerTax`, `PaymentMeans`,
   `AllowanceCharge`, `TaxSubtotal`, `TaxTotal`, `LegalMonetaryTotal` — but do not add checks for fields
   that do not exist.
5. Sales regression: compare a normal sales invoice payload with a pre-change `INV2609-*.json` — must be
   identical.
6. Source-level test (before the mapper): around `BuildSbInvoiceSourceAsync` / `BuildSbCdnSourceAsync`
   (covering 11, 12 and 13), assert `source.SupplierParty` = vendor and `source.Customer*` = company.
   Plus the **no-rebuild check**: after a document reaches `SUBMITTED`/`VALID`, a vendor-master edit must
   not be able to alter a future submitted payload — submit is refused and `Refresh`/`Recover` never
   rebuild a source.
7. Validation error routing: test both directions end-to-end —
   vendor failures keyed `Supplier.Tin` / `Supplier.Msic` / `Supplier.BizDescription` / `Supplier.Phone`,
   company failures keyed `Buyer.Tin` / `Buyer.Name` / `Buyer.Address`, and confirm the PoSb
   `SdValidationSummary` shows each under the correct section (changing the validator keys alone does
   not prove the UI renders them correctly).
8. Sandbox smoke test (the authority): submit one SBI, one SBC **and one SBD** to the preprod MyInvois
   (`Einvoice:EInv_Url` = preprod-api) and verify all of:
   1. API submission accepted
   2. document status = `VALID`
   3. authenticated issuer remains the company
   4. payload `AccountingSupplierParty` = vendor
   5. payload `AccountingCustomerParty` = company
   6. Supplier TIN belongs to the vendor
   7. Buyer TIN belongs to the company
   8. no unintended change to credentials / `onbehalfof`
9. Negative: vendor with blank MSIC/BizDesc is refused at the vendor master (if Option A applies) and at
   submit, keyed `Supplier.Msic` / `Supplier.BizDescription`, with no MyInvois call.

## Decisions recorded

- Party direction for self-billed reversed; sales families unchanged. Applies to types 11, 12 and 13.
- Supplier block data source = Purchase master `POSupplier`, read live **inside each source build**
  (validate / submit) and carried in memory on the source document. Verified: `EInvoiceSourceDocument`
  is not persisted and there is deliberately no snapshot (`SaEInvoiceService.cs:2905-2913`); the guard
  is the submit gate that refuses `SUBMITTED`/`VALID`. There is no company fallback.
- Only the source builder reads `POSupplier`; the mapper consumes the source document.
- Self-billed `Customer*` / Buyer is **always the company profile** — never the vendor.
- Payload party ≠ submission identity: `EInvoiceDocumentState.Supplier`, credentials, issuer TIN and
  submission grouping stay the company's for self-billed documents.
- Vendor validation scope: **Option B** — not hard-required on the vendor master; enforced on the
  self-billed submit path only.
- `EInvoiceSourceDocument.Customer*` names are kept as-is; for self-billed they hold the company.
- `MiscCode` remains a string (leading zero preserved); `BizDesc` max = **200** (DB column is
  `nvarchar(200)`), so the UI `maxlength="500"` is corrected down to 200 — no schema change.
- The state/country/reg-type lookup algorithm is unchanged; only the receiving party changes.
- Vendor state/country keep today's source (dedicated code column wins, else the maintained list value)
  translated by `LhdnCodeLookup`; UI remark added under those inputs.
- Credentials, submission identity and document search stay the company's.
- No schema change, no data migration; existing sandbox self-billed documents left as-is.

## Further considerations

1. Existing vendors will mostly have blank `MiscCode`/`BizDesc`, so self-billing them will be refused at
   submit until the master is fixed. Under Option B this does **not** block normal vendor edits; provide a
   one-off operator query listing vendors used by self-billed documents with blank MSIC, and a UI hint on
   the vendor screen + the self-billed screens.
2. `EInvoiceSourceDocument.Customer*` now means the company for self-billed and the customer master for
   sales. Decision: keep the names, document the meaning and add the explicit "do not rename" comment
   (Phase 1 item 2). Renaming to a neutral party block would force a sales-test migration — out of scope
   for this change.
3. `EInvDocSubmission.SupplierTin/SupplierName` (payload capture from the MyInvois response,
   `EInvoiceSubmissionWriter.cs:361-362`) will hold the vendor for self-billed rows. Nothing reads them
   today; note it for any future submission-history report.
4. Do not add a migration/rework script for historical self-billed documents — they are LHDN sandbox rows
   issued with the parties swapped and are intentionally left as-is (Phase 5).
5. Because there is no persisted vendor snapshot, a `FAILED`/`INVALID` self-billed document re-submitted
   after a vendor-master edit will use the new vendor values. This matches the existing sales-buyer
   address behaviour; if strict freezing is ever needed, mirror the `BuyerIdentity` freeze
   (`SourceBuildResult.ResolvedBuyer`) — that requires new columns and is a separate change.
