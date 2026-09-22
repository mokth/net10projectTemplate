# Plan: e-Invoice re-submission for INVALID documents (INV / CN / DN)

## IMPLEMENTATION STATUS (2026-09-22)

**Landed: Phase 0, A, B, C and the non-gated part of F.** Phases D + E are HELD, deliberately, as one
unit — see "Why D and E must land together" below.

Verified: `dotnet build ErpWeb.slnx` → **0 errors**; e-Invoice suite **289 passed / 0 failed**; full suite
**1954 total / 1941 passed / 13 failed / 0 skipped**, the 13 being the pre-existing
`SaCustServiceTests` (9) + `PoSupplierServiceTests` (4) GL-code/phone WIP. Those two service files were
already modified in the working tree before this work started and were never touched here.

| Item | Where |
| --- | --- |
| Phase A — CN/DN POSTED parity | `SaEInvoiceService.IsSourceDocumentPosted` + `DocumentTypeLabel` |
| Phase B — shared resolver | **NEW** `ErpWeb.Core/Sales/SaCustBuyerProfileResolver.cs` |
| Phase C — payload sources the master | `SaEInvoiceService.BuildSourceAsync` (INV and CN/DN branches) |
| Phase D-2a — blank frozen identity | `SaEInvoiceService.ResolveNoteIdentity` / `IsBlankIdentity` |
| Phase F — regression tests | **NEW** `ErpWeb.Tests/SaEInvoiceBuyerSourcingTests.cs` (8 tests) + the rewritten phone test |

**Refinements made while implementing (deltas from the reviewed plan):**

1. **The resolver's blank-field rule needed two shapes, not one.** The first cut swapped the whole address
   block only when address line 1 AND city were blank; a billing override that filled line 1 but omitted
   state/postal/country was then used as-is and refused the submission (caught by the new
   `AppInvoice_false_uses_the_billing_overrides` test). Final rule, documented in the resolver:
   **address lines 1-3 are a block** (line 1 decides for all three, so lines can never be spliced between
   two addresses), while **name/city/state/postal/country/telephone fall back per field**. `Address4`
   stays master-only per D-7.
2. **`BuyerIdentity` was NOT folded into `SaCustBuyerProfile`.** They are genuinely different concerns —
   `SaCustBuyerProfile` is what the *master holds*, `BuyerIdentity` is the five-field *frozen audit* shape
   the SUBMITTING claim writes. Folding them would have meant filling ten address fields with nulls at the
   freeze site. Instead `ToBuyerIdentity(profile)` is the single conversion, so the two cannot drift.
   (Deviation from review item 2, which asked for one shape.)
3. **A test-host seed that hid a defect is now production-accurate.** `BuildCreditNote` seeded EVERY note
   with `InvNo = "INV-1001"`, including debit notes — a state `SaCdnService` cannot produce. The seed is now
   type-aware, which is what makes gap 4 visible in the suite.

**Why D and E must land together (new finding, refines the reviewed phasing).** The reviewed plan treated
D as ungated and E as gated. That ordering is unsafe: with D alone the validator stops refusing a note that
has no origin reference, so the submission proceeds to the HTTP phase, where
`GenerateCreditNote` throws `OriginInvoiceUUID is required`, the throw is swallowed at
`SubmitDocumentHelper.cs` ~L190, and the document is left in a FAILED state with a confusing message.
Today it is refused cleanly *before* the SUBMITTING claim, consuming nothing. D must therefore not ship
until E can, and E waits on Verification step 4.


## Problem

The TIN scenario already works: `EInvoiceStatuses.IsBuyerIdentityFrozen` keeps `INVALID` live, so
`BuildSourceAsync` re-reads the buyer identity from `SaCust`, the service pre-submit gate lets INVALID
fall through, and `SaEInvoiceStatusView.CanSubmit` includes INVALID so the Submit button is enabled.
Pinned by `ErpWeb.Tests/SaEInvoiceBuyerIdentityTests.cs` (`Resubmit_uses_the_corrected_customer_identity`,
`[InlineData(EInvoiceStatuses.Invalid, null)]`).

Four real gaps remain:

1. **CN/DN POSTED is a UI-only gate.** `SaEInvoiceService.IsSourceDocumentPosted` (~L3277) only inspects
   `SaInvoice`; anything else returns true. The UI gate is `EInvoiceIneligibleReason` in
   `ErpWeb.UI/Sales/Transactions/SaCdnList.razor.cs` (~L720), which rejects "not POSTED". A non-UI caller
   (job / API) can therefore submit a draft CN/DN.
2. **The payload's buyer name/address/city/state/postal/country/phone come from the document snapshot**
   (`SaEInvoiceService.cs` ~L2886-2899 for INV, ~L2961-2986 for CN/DN), not the customer master. Only the
   identity block (TIN / BRN / RegType / email / SST) is live — and only while not frozen.
3. **A CN/DN with no origin invoice cannot be submitted at all.** Hard-blocked twice:
   - `EInvoiceValidator.ValidateNoteOrigin` (`EInvoiceValidator.cs` ~L190-201) requires `RefDocumentNo`
     and `OriginUuid`.
   - `ErpWeb.EInvoiceLib/GenerateDoc/GenerateCreditNote.cs` ~L43 **throws**
     "OriginInvoiceUUID is required for credit/debit note submission." (caught at
     `SubmitDocumentHelper.cs` ~L190, surfacing as errorCode "100").
4. **A Debit Note can never carry an origin invoice number, so a DN can never be submitted today.**
   `SaCdnService` writes `InvNo = docType == SaCdnTypes.CreditNote ? Norm(request.InvNo) : null` at three
   sites (~L563, ~L680, ~L772) — for a **DN** `InvNo` is ALWAYS null, and `SaCdn` has no other
   invoice-link column (`DoNo` is explicitly documented as unreliable, `SaCdn.cs` ~L10-14).
   `BuildSourceAsync` only looks the origin up when `cdn.InvNo` is non-blank, so a DN always trips
   `errors["OriginInvoice.No"]` and is refused. A blank `InvNo` IS legal for a CN — the CN validation at
   `SaCdnService.cs` ~L717 is skipped when blank.
   **The test host masks this**: `EInvoiceTestHost.BuildCreditNote` (~L550-585) seeds EVERY note with
   `InvNo = "INV-1001"`, DNs included — a state the production service cannot produce.
   **Consequence**: D-5 is not an edge case, it is the ONLY path by which a DN can ever reach MyInvois.
   If the sandbox rejects a note with no `BillingReference`, DNs are unsubmittable and the correct fix
   becomes a DN→invoice link (schema + UI), not a generator change.

## Invariants

```
Document snapshot  (SaInvoice.Inv* / SaCdn.Inv*)  = print / business historical data
Customer master    (SaCust)                       = current e-Invoice buyer source
Frozen Buyer*      (SaInvoice.BuyerTin / ...)     = audit record of what was submitted
```

- The e-Invoice payload reads the master, or the origin invoice's frozen copy per D-2. It NEVER reads
  `SaInvoice.Inv*` / `SaCdn.Inv*`.
- The frozen `Buyer*` columns are WRITTEN but never READ by the payload (except D-2). State this in a
  comment at both the write site (`ApplyBuyerFreeze`) and the read site (`BuildSourceAsync`).

## Decisions (confirmed with user)

- **D-1** For e-Invoice submission the buyer info ALWAYS comes from the customer master (`SaCust`). The
  document columns (`SaInvoice.Inv*`, `SaCdn.Inv*`) stay the print/business snapshot and are NOT changed.
- **D-2** A CN/DN whose origin invoice IS a valid e-invoice keeps inheriting that invoice's submitted
  identity (`origin.BuyerTin` / `BuyerBrn` / `BuyerRegType` / `GstregNo` / `InvEmail`) — unchanged
  behaviour, so `SaEInvoiceBuyerIdentityTests` tests 2 and 9 keep passing. Only when there is NO usable
  origin invoice does the note use the customer master.
- **D-2a** If a *valid* origin invoice's frozen identity is entirely blank (a legacy invoice that reached
  VALID with NULL `Buyer*` columns), fall back to the customer master instead of emitting a null buyer.
  Without this the note fails `Buyer.Tin` / `Buyer.RegNo` validation with no actionable message.
- **D-3** The frozen `SaInvoice.Buyer*` columns stay, and keep being written at the SUBMITTING claim
  (`ApplyBuyerFreeze`) as the audit record of what was sent. The payload stops READING them, except the
  D-2 CN/DN inheritance path. No schema change; `SaInvoiceService.ApplyMasterSnapshotsAsync`
  (~L2980-3017) keeps writing them on save.
- **D-4** CN/DN POSTED enforced server-side, in parity with the invoice.
- **D-5** A CN/DN without an origin e-invoice is submitted with NO `BillingReference` at all (not a blank
  UUID). User confirmed: "A CN/DN can have empty origin Invoice UUID"; the case hit in practice is
  `SaCdn.InvNo` blank — which per gap 4 is *every* DN.
  **RISK, user-accepted, and a HARD IMPLEMENTATION GATE**: `GenerateCreditNote.cs` ~L41-42 carries a
  comment asserting MyInvois rejects a note with no real invoice reference. Unit tests cannot prove
  acceptance. Phase E does not start until the sandbox explicitly accepts a no-reference note
  (Verification step 4).
- **D-6** The CN/DN origin decision is an explicit ENUMERATED STATE on the source document, not a
  boolean: `SaEInvoiceOriginState { None, Unsubmitted, Valid, InFlight, Invalid, Rejected, Failed }`,
  computed in exactly ONE place in `BuildSourceAsync`. Behaviour:
  `Valid` → inherit the origin's identity + emit the reference; `None` / `Unsubmitted` → whole buyer block
  from the master + NO reference; everything else → refuse. `EInvoiceValidator.ValidateNoteOrigin`
  switches on the state and never infers intent from nulls. (Replaces the earlier
  `RequiresOriginReference` boolean.)
- **D-7** `Addr4` ALWAYS resolves from `SaCust.Address4` — the only master field, because `SaCust` has no
  `InvAddress4`. `SaInvoice.InvAddress4` therefore does not appear in the e-Invoice payload. FINAL, not an
  assumption — this changes the payload, so it is a decision, not a note.
- **D-8** The stale `OriginInvoiceUUID is required` comment in the vendored generator is replaced with the
  decision record on the day Phase E lands, citing the sandbox evidence and its date, so the next reader
  does not "restore" the throw.

## RefDocumentNo / OriginUuid contract

| `OriginState` | `OriginUuid` | `RefDocumentNo` | `BillingReference` | Meaning |
| --- | --- | --- | --- | --- |
| `Valid` | populated | populated (`cdn.InvNo`) | emitted | A real origin e-Invoice is required |
| `None` / `Unsubmitted` | null | blank or populated | OMITTED | Document reference only, NOT a MyInvois origin |
| `InFlight` / `Invalid` / `Rejected` / `Failed` | — | — | — | Refused before generation |

`RefDocumentNo` is therefore NOT itself proof of an e-Invoice origin. Any downstream consumer that
assumes otherwise is a defect to fix under Phase E's call-chain audit.

## Review disposition (score 9.1, approve with conditions)

Accepted as-is: invariant block (1), resolver owning the whole buyer block (2), origin states (4), hard
sandbox gate (5, 14), generator call-chain audit as a required task (6), the `RefDocumentNo` / `OriginUuid`
contract table (7), DN POSTED tests (8), mixed-source regression test (9), Address4 final (10),
serialized-payload verification (11.D), Phase 0→F ordering (12).

Refined:

- **(2) Naming** — `SaCustBuyerProfileResolver` + a `SaCustBuyerProfile` record. The existing
  `BuyerIdentity` record is FOLDED INTO it so there is exactly one buyer shape; `LoadBuyerIdentityAsync`
  and `ApplyBuyerFreeze` are updated for the rename.
- **(3) Live/frozen table** — the proposed table omits two cases: `FAILED + Unknown` is FROZEN (a request
  may be in flight) and `CANCELLED` is LIVE. The three requested tests for INVALID / REJECTED / FAILED +
  customer-changed ALREADY EXIST — `Resubmit_uses_the_corrected_customer_identity` is a 5-case theory
  covering null, Invalid, Rejected, Cancelled and Failed + ConfirmedFailure. The genuinely missing
  dimension is address/phone, added as F3. Also noted: under D-1 the frozen *read* becomes unreachable for
  the payload, because every frozen status is either unsubmittable or recover-only. It is therefore
  retained deliberately as Recover-time defence-in-depth and DOCUMENTED, not deleted.
- **(11.D) Payload-level test** — accepted with a scope limit. The SIGNED payload cannot be asserted in
  tests: `EInvSignatureHelper.StartProcess` needs the configured secrets and document version "1.1". The
  achievable and sufficient assertion is the unsigned generated model — `GenerateCreditNote` is public
  (:L13), `InvoiceRoot` is a public field (:L19), and `HashUtility.SerializeJson(object)` is public static
  (`Utility/HashUtility.cs:51`).

Added by this review pass (not in the reviewer's list): gap 4 (the Debit Note defect and the test-host
masking), D-2a (blank frozen origin identity), and the Phase 0 characterization step.

## Steps

### Phase 0 — lock current behaviour (characterization first, no production change)

0.1 **Un-mask the DN.** `EInvoiceTestHost.BuildCreditNote` (~L550-585) seeds `InvNo = "INV-1001"` for both
    note types. Make the seed type-aware so a DN can be created the way production writes it (`InvNo`
    null). This is what makes gap 4 visible in tests instead of hidden behind an impossible seed.
0.2 Add a table-driven test over the CURRENT origin behaviour (see F4's matrix) so the Phase D change
    is a reviewable diff rather than a silent behaviour swap.
0.3 Record why these two tests must stay green through Phase D:
    `Credit_note_without_a_valid_origin_invoice_is_refused` and
    `Credit_note_against_an_origin_that_is_not_valid_is_refused_even_with_a_uuid` — both seed an origin
    that is SUBMITTED, i.e. `InFlight` under D-6, not `Unsubmitted`.

### Phase A — server POSTED gate (no dependency on B-F)

A1. `ErpWeb.Core/EInvoice/SaEInvoiceService.cs` — `IsSourceDocumentPosted` (~L3277). Replace the
    `state.Entity is not SaInvoice` short-circuit with a switch:
    - `SaInvoice invoice` → `Status == SaInvoiceStatuses.Posted`
    - `SaCdn cdn` → `Status == SaCdnStatuses.Posted` (both resolve; `ErpWeb.Core.Sales` is already imported)
    - default → true (leaves the not-yet-wired self-billed family alone)
    The refusal message (~L1944-1948) hardcodes "Invoice {no}". Make it name the document type via
    `EInvoiceDocumentTypes` (`EInvoiceStatuses.cs` ~L117): invoice / credit note / debit note.
    Without this the two `EInvoiceIneligibleReason` UI gates (`SaInvoiceList.razor.cs` ~L786,
    `SaCdnList.razor.cs` ~L720) remain the only POSTED protection.

### Phase B — one shared buyer resolver

B1. Extract the `AppInvoice` billing rule out of `SaInvoiceService.BackfillCommercialHeader`
    (~L2935-2975) into `ErpWeb.Core/Sales/SaCustBuyerProfileResolver.cs`. Rule:
    `customer.AppInvoice == true` → main `CustName` / `Address1..4` / `City` / `State` / `PostalCode` /
    `Country` / `Tel`; else the billing overrides `InvName` / `InvAddress1..3` / `InvCity` / `InvState` /
    `InvPostalCode` / `InvCountry` / `InvTel`.
    `ErpWeb.Core` is ONE assembly, so `ErpWeb.Core.Sales` and `ErpWeb.Core.EInvoice` share it — there is no
    layering obstacle to putting the resolver in `Sales` and consuming it from `EInvoice`.
    Deviation, FINAL per D-7: `Addr4` always resolves from `customer.Address4`.
B2. `SaCustBuyerProfile` record owns the COMPLETE buyer block — name, addr1-4, city, state, postal,
    country, phone, email, tin, regNo, regType, sstNo. Fold the existing `BuyerIdentity` record into it so
    there is exactly one buyer shape, and update `LoadBuyerIdentityAsync` (~L3018), `ApplyBuyerFreeze`
    (~L3044) and the freeze tests for the rename. Email reuses the existing
    `SaInvoiceService.ResolveInvoiceEmail` rule (`InvEmail ?? Email`, ~L3029) — extract it so both share it.

### Phase C — invoice payload sources the resolver

C1. `SaEInvoiceService.BuildSourceAsync` INV branch (~L2886-2899): take `CustomerName`,
    `CustomerAddr1..4`, `CustomerCity`, `CustomerState`, `CustomerPostalCode`, `CustomerCountry`,
    `CustomerPhone`, `CustomerEmail` from the B2 resolver instead of `invoice.Inv*`.
    Load `SaCust` in the SAME read that `LoadBuyerIdentityAsync` already performs — widen that query to
    return the full profile rather than adding a second round trip.
C2. Identity: keep the existing live-vs-frozen guard, and add a comment stating the resolved policy —
    `NEW` / `INVALID` / `REJECTED` / `CANCELLED` / `FAILED + ConfirmedFailure` read the master;
    `SUBMITTING` / `SUBMITTED` / `VALID` / `FAILED + Unknown` read the frozen columns. Note in the comment
    that the frozen branch is now Recover-only (every frozen status is unsubmittable or recover-only), so
    it is deliberate defence-in-depth, not the payload gate.

### Phase D — CN/DN origin state machine (depends on B)

D1. New `ErpWeb.Core/EInvoice/EInvoiceOriginState.cs`:
    `enum SaEInvoiceOriginState { None, Unsubmitted, Valid, InFlight, Invalid, Rejected, Failed }` plus a
    PURE `Classify(origin)` mapper — unit-testable with no database, in the idiom of `LhdnCodeLookup`.
D2. In the `SaCdn` branch (~L2930-2987) compute the state from the origin ALREADY loaded — do NOT add a
    second query. `None` = no `InvNo`; `Unsubmitted` = the referenced invoice exists with
    `IrbmStatus` null/NEW; `Valid` = status VALID with a non-blank `IrbmUuid`; `InFlight` = SUBMITTING /
    SUBMITTED; `Invalid` / `Rejected` / `Failed` map from the origin's `IrbmStatus`.
D3. Behaviour per D-6:
    - `Valid` → identity from `origin.Buyer*` (D-2), boosted to the master when those are blank (D-2a);
      address / phone / email from the resolver (D-1); `OriginUuid = origin.IrbmUuid`;
      `RefDocumentNo = cdn.InvNo`.
    - `None` / `Unsubmitted` → the ENTIRE buyer block from `SaCust` via `cdn.CustCode`;
      `OriginUuid = null`; `RefDocumentNo = cdn.InvNo` (may be blank); no `BillingReference`.
    - `InFlight` / `Invalid` / `Rejected` / `Failed` → refuse with today's `OriginInvoice.Uuid` message.
      This is what keeps `Credit_note_against_an_origin_that_is_not_valid_is_refused_even_with_a_uuid` and
      `Credit_note_without_a_valid_origin_invoice_is_refused` green unchanged.
D4. `EInvoiceSourceDocument` (`EInvoiceSourceDocument.cs` ~L9-58) gains `OriginState`; drop the planned
    `RequiresOriginReference` boolean (D-6 — one source of truth). `EInvoiceValidator.ValidateNoteOrigin`
    (~L190-201) switches on the state and still early-returns for `DocumentType == Invoice`.
D5. Put the `RefDocumentNo` / `OriginUuid` contract table from this plan into the code comment at both the
    builder and the validator, so `RefDocumentNo` is never mistaken for proof of an e-Invoice origin.

### Phase E — generator change (HARD GATED — do not start before Verification step 4 passes)

E1. **Required task, not an optional check — complete the generator call-chain audit FIRST.** Enumerate
    and report every consumer of `RefDocumentNo`, `OriginUuid`, `_oriInvUUID`, `BillingReference`,
    `InvoiceDocumentReference` across `GenerateCreditNote` → `GenerateDocHelper` → every other helper →
    the serialized MyInvois payload. Specifically confirm:
    - a blank `RefDocumentNo` cannot accidentally produce a `BillingReference` entry;
    - a null `OriginUuid` cannot produce an invalid UUID structure (`UUID: [null]`);
    - no downstream validation still assumes every CN/DN has an origin invoice.
    Known starting points: `GenerateCreditNote.cs` ~L43 / ~L115-133, `GenerateDocHelper.cs` ~L532
    (`doc.RefDocumentNo ?? ""`), `EInvoiceDocumentMapper.cs` ~L94-95.
E2. Only then edit `GenerateCreditNote.cs` ~L41-47: delete the throw, and build `invoice.BillingReference`
    ONLY when `_oriInvUUID` is non-blank — otherwise leave the list null so `NullValueHandling.Ignore`
    omits it (omitting beats emitting `UUID: [null]`).
E3. Apply E1's findings to any other guard the audit turned up.
E4. Replace the stale comment (~L41-42) with the decision record per D-8: user-accepted, sandbox evidence,
    date, and what to do if MyInvois later starts rejecting it. This is a deliberate, documented exception
    to the "leave the vendored lib alone" rule noted in `ErpWeb.Core/EInvoice/ISaEInvoiceService.cs` ~L6.

### Phase F — regression, DN coverage, payload verification

F1. **Test-host seed fix (blocks F2-F9).** `ErpWeb.Tests/EInvoiceTestHost.cs` ~L302-317 seeds only
    `CustName` / `TinNo` / `CustBrn` / `RegType` / `Email` / `GstregNo` — NO address and NO `Tel`. Once the
    payload reads the master, every payload test fails `Buyer.Address` / `Buyer.Phone`. Seed `Address1` /
    `City` / `State` / `PostalCode` / `Country` / `Tel`, the `Inv*` overrides and the `AppInvoice` flag.
F2. POSTED gate, BOTH types: `NEW` CN → refused, `POSTED` CN → submitted; `NEW` DN → refused,
    `POSTED` DN → submitted. Assert refusal at the SERVICE level via `FakeSubmitDocumentHelper.Calls` being
    empty, not only on the result object. `EInvoiceTestHost.BuildCreditNote` already seeds
    `Status = "POSTED"` (~L565), so no existing note test breaks.
F3. Master-sourced buyer block, BOTH types: correct `SaCust.Address1` / `City` / `Tel` after an INVALID
    submission, resubmit, assert `host.Helper.Submitted[0].Customer` carries the corrected values.
F4. Origin-state matrix, table-driven over all seven states (`None`, `Unsubmitted`, `Valid`, `InFlight`,
    `Invalid`, `Rejected`, `Failed`) asserting: refused / inherited identity / master identity / whether a
    `BillingReference` is emitted.
F5. **Snapshot-unchanged test (proves D-1 and D-3 together).** Set `SaInvoice.InvName = OLD` and
    `SaCust.CustName = NEW`; resubmit; assert the payload buyer name is `NEW` AND
    `SaInvoice.InvName` is still `OLD`.
F6. **Mixed-source test for a `Valid`-origin note (review item 9).** `origin.Buyer*` = OLD identity while
    `SaCust` address/contact = CURRENT; assert the identity fields stay OLD and address/phone/email come
    from the CURRENT master. Add a code comment where the two sources are deliberately joined.
F7. No-origin CN and DN (per gap 4, the DN case is the important one): blank `InvNo` → submitted, whole
    buyer block from the master, `OriginInvoiceUUID` null/absent. This test FAILS before Phase E, which is
    the point.
F8. `AppInvoice` both ways: `true` uses `Address1` / `Tel`; `false` uses `InvAddress1` / `InvTel`.
F9. **Serialized-payload verification (review item 11.D), with the signing scope limit.** Drive
    `EInvoiceDocumentMapper.Map` → `new GenerateCreditNote(header)` → `Generate()` and assert the
    GENERATED model, not just the source object: the buyer name/address in `InvoiceRoot` are the NEW
    master values, and `HashUtility.SerializeJson(InvoiceRoot)` contains neither the superseded snapshot
    value nor the origin UUID on a no-origin note. `GenerateCreditNote` is public (:L13), `InvoiceRoot` is
    a public field (:L19), `HashUtility.SerializeJson(object)` is public static (`HashUtility.cs:51`).
    `ErpWeb.Tests` reaches the library transitively through `ErpWeb.Core`; add an explicit
    `ErpWeb.EInvoiceLib` ProjectReference if clarity is preferred. The SIGNED payload is out of reach —
    `EInvSignatureHelper.StartProcess` needs the configured secrets and document version "1.1".
F10. REWRITE — `SaEInvoiceLifecycleTests.cs` ~L277
    (`A_non_E164_buyer_telephone_is_refused_and_points_at_the_document_snapshot`) and ~L297
    (`A_local_format_telephone_is_accepted_and_submitted_as_E164`): both assert the DOCUMENT snapshot is
    the phone source. They must now assert the MASTER is, and the "re-save the document" guidance in
    `EInvoiceValidator.cs` ~L88-93 must be reworded because fixing the master is now sufficient.
F11. Keep `SaEInvoiceBuyerIdentityTests` tests 1-12 green except where F3 / F8 / F10 require a change;
    tests 2 and 9 (the note inherits the origin's identity) are the D-2 guard and must stay.

## Relevant files

- `ErpWeb.Core/EInvoice/SaEInvoiceService.cs` — `BuildSourceAsync` (~L2760-3005),
  `IsSourceDocumentPosted` (~L3277), `LoadBuyerIdentityAsync` (~L3018), `ApplyBuyerFreeze` (~L3044),
  refusal message (~L1944).
- `ErpWeb.Core/EInvoice/EInvoiceValidator.cs` — `ValidateNoteOrigin` (~L190), `ValidateBuyer` (~L78-107),
  the phone "re-save" guidance (~L88-93).
- `ErpWeb.Core/EInvoice/EInvoiceSourceDocument.cs` — `Customer*` props (~L42-56) + the new `OriginState`.
- `ErpWeb.Core/EInvoice/EInvoiceStatuses.cs` — `IsBuyerIdentityFrozen` (~L70),
  `EInvoiceDocumentTypes` (~L117). **NEW** `ErpWeb.Core/EInvoice/EInvoiceOriginState.cs`.
- `ErpWeb.Core/Sales/SaInvoiceService.cs` — `BackfillCommercialHeader` (~L2935, extract),
  `ApplyMasterSnapshotsAsync` (~L2980-3017, keep as-is), `ResolveInvoiceEmail` (~L3029, extract).
- `ErpWeb.Core/Sales/SaCdnCalc.cs` — `SaCdnStatuses.Posted` (~L12).
- `ErpWeb.Core/Sales/SaCdnService.cs` — the three `InvNo = ... : null` writes (~L563, ~L680, ~L772) and
  the CN-only `InvNo` validation (~L717). Evidence for gap 4; no change planned here.
- **NEW** `ErpWeb.Core/Sales/SaCustBuyerProfileResolver.cs` (+ the `SaCustBuyerProfile` record).
- `ErpWeb.EInvoiceLib/GenerateDoc/GenerateCreditNote.cs` — the throw (~L41-47) and
  `BillingReference` (~L115-133). `GenerateDocHelper.cs` ~L532. `Utility/HashUtility.cs` ~L51.
- `ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor.cs` (~L786) and `SaCdnList.razor.cs` (~L720) —
  `EInvoiceIneligibleReason`; both already require POSTED, so no change (verify only).
- `ErpWeb.Tests/EInvoiceTestHost.cs` (seed ~L302-317, `BuildCreditNote` ~L550-585,
  `SeedCreditNoteAsync` ~L465), `ErpWeb.Tests/SaEInvoiceLifecycleTests.cs`,
  `ErpWeb.Tests/SaEInvoiceBuyerIdentityTests.cs`, and the new origin-state / payload tests.
- `ErpWeb.Tests/ErpWeb.Tests.csproj` — reaches `ErpWeb.EInvoiceLib` transitively; add an explicit
  `ProjectReference` if the generator tests want one.
- `plans/plan-einvoiceInvalidResubmit.prompt.md` — this plan doc.

## Verification

1. `dotnet build ErpWeb.slnx` → 0 `error CS` / `RZ`. Mandatory: `ErpWeb.EInvoiceLib` and `ErpWeb.UI` change,
   and `dotnet test` alone does not prove a page renders. A running `ErpWeb` app causes the known
   8 MSB3027 / MSB3021 host-file locks — build per project if so.
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` unpiped. Baseline **1892 total / 1879 passed / 13 failed
   / 0 skipped**; the 13 are the pre-existing `SaCustServiceTests` + `PoSupplierServiceTests`
   GL-code/phone WIP failures. State explicitly that they are the SAME 13 — any DIFFERENT failure is a
   regression and blocks.
3. SQL concurrency suites: set `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` and
   `ConnectionStrings__SqlServerTestConnection` in their OWN terminal commands, then run `dotnet test`
   alone (semicolons on the same line truncate the value). Expect `Skipped: 0`.
4. **HARD GATE — MyInvois sandbox, before Phase E starts.** Submit BOTH a CN with a blank `InvNo` and a
   DN (which always has a blank `InvNo`, gap 4) against the sandbox. Record the response verbatim,
   including the LHDN error code if it is rejected.
   - Accepted → proceed with Phase E.
   - Rejected → do NOT force Phase E. Keep mandatory-valid-origin behaviour, document the actual response
     here, and re-scope: DNs need a DN→invoice link (schema + UI) to ever be submittable.
   Unit tests cannot prove MyInvois acceptance — this step is not satisfiable by the suite.
5. Serialized-payload verification (F9) passes, i.e. the generated model and its serialized JSON carry the
   master values and not the superseded snapshot.
6. Manual smoke: an INVALID + POSTED invoice whose customer TIN AND address were both corrected →
   `SaEInvoicePanel` "Submit to LHDN" is enabled → resubmit → the payload carries the corrected values.
   Repeat for a CN and for a DN.

## Further Considerations

1. **Sandbox fallback for DNs is the biggest open risk.** If step 4 rejects a no-reference note, DNs are
   unsubmittable by design and the fix becomes linking a DN to its invoice (a schema + UI change), not a
   generator change. Decide before Phase E starts whether that fallback is in scope for this plan or a
   follow-up.
2. **CN/DN address vs identity split.** For a `Valid`-origin note the identity stays the origin invoice's
   while the address/contact come from the master (D-1 + D-2), so a note can carry a buyer address that
   differs from the invoice it references. This is intentional; F6 is the regression guard. If a reviewer
   reads it as inconsistent with LHDN, the alternative is to make D-1 invoice-only.
3. **The frozen-branch question raised during review.** Under D-1 the frozen identity read is unreachable
   for the payload. Options: keep it as Recover-time defence-in-depth (chosen — no tested path deleted),
   or delete it as dead code. Do NOT delete it before confirming what `RecoverAsync` does with
   `state.Source` (~L180).
