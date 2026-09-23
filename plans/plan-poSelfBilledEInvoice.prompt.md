# Plan: Self-billed e-Invoice (SBI / SBC / SBD)

> ## ⚠️ SUPERSEDED — party direction (2026-09-23)
>
> **D2, Prerequisite 0, Phase 3 step 16 and Phase 3 step 35 are SUPERSEDED.** The LHDN contract was
> finally recorded in `docs/einvoice-selfbilled-phase0-findings.md` and it is the OPPOSITE of what this
> plan assumed: a self-billed e-Invoice is issued by the BUYER, so the payload's
> `AccountingSupplierParty` is the VENDOR and `AccountingCustomerParty` is OUR COMPANY (which also stays
> the submission/credential identity).
>
> The correction is implemented by **`plans/plan-selfBilledPartyReversal.prompt.md`**. Everything else in
> this document (data model, menus, lifecycle, UI, tests) remains the historical record of what was built.

Add two new slim purchase document families — `PoSbInvoice` (LHDN type **11**) and `PoSbCdn` (types **12/13**) — plus their list/entry screens, and wire them into the existing e-invoice façade. The vendor is the LHDN "Customer" and our company is the "Supplier"; the vendor block is read live from `PoSupplier`. **`ErpWeb.EInvoiceLib` needs zero changes** — it already generates, signs and submits 11/12/13; only the Core façade's dispatch (which today deliberately refuses self-billed) plus the ERP data/UI are missing.

## Review response (2026-09-23)

An external review scored the plan 9.2/10 and raised 4×P0 + 6×P1 items. Disposition, after checking each claim against the shipped code:

| Reviewer point | Verdict | Where it landed |
|---|---|---|
| P0 — verify LHDN 11/12/13 semantics against the current spec | **Accepted** | New **Prerequisite 0** (blocks Phase 3) |
| P0 — live vendor data breaks document reproducibility; repair/resubmit may rebuild | **Corrected + accepted** | The premise is wrong: `RepairSubmissionAsync` delegates once to `RefreshAsync`, which **never** calls `BuildSourceAsync` (it only reads `GetDocumentDetail` and maps the status). The real rule is the **rebuild window** — see *Lifecycle & rebuild rules* and **D7** |
| P0 — posted/submitted editability undefined | **Accepted** | *Lifecycle & rebuild rules* matrix; the server-side precedent (`SaInvoiceService.cs:645/825/1670`, `SaCdnService.cs:697/896`) is now cited as the pattern to copy |
| P0 — origin integrity | **Accepted** | Step 11 `PoSbOriginResolver` + step 34 tests. Company/branch scoping is **not** a new decision — it is already the shipped convention for `SaCdn.InvNo` in `BuildSourceAsync` |
| P1 — `OriginSbInvNo` alone may be insufficient | **Accepted** | Step 1 + step 11: the lookup scope is explicitly `(CompanyCode, BranchCode, DocNo)`, never a bare `DocNo` |
| P1 — cancellation behaviour unspecified | **Accepted** | *Lifecycle & rebuild rules* + step 11: a cancelled origin may not back a new note; cancellation never rewrites `IRBMORIUUID` |
| P1 — numbering concurrency test | **Accepted** | Step 33 (precedent: `PoInvoiceSqlServerConcurrencyTests`) |
| P1 — header total rounding unspecified | **Accepted, but reuse not invent** | Step 7: reuse `PoOrderCalc.ComputeAmount` / `ComputeTax` / `SumTotals` + `PoInvoiceCalc.ApplyHeaderTotals`, so `GrossAmnt = Σ NetAmount`, `Taxes = Σ TaxAmt`, `TotAmnt = GrossAmnt + Taxes` hold **by construction** |
| P1 — vendor validation must happen before submission | **Partially accepted** | `EInvoiceValidator.ValidateBuyer` + `RequireE164` **already** enforce buyer name/TIN/RegNo/RegType/Addr1/City/Postal/Phone — and the self-billed `Customer*` *is* the vendor block, so the rules already apply. What was missing is an explicit ordering statement and tests: step 12 + step 36 |
| P1 — payload-level negative tests | **Accepted** | Step 36 (10 cases, incl. wrong party direction and cross-company origin) |
| Phase 1 — table naming ambiguity, FK/vendor-mandatory question | **Accepted** | Step 1 pins physical names (`POSbInvoice`/`POSbInvoiceDetail`/`POSbCdn`/`POSbCdnDetail`); no FK (house convention — `PoInvoice.VendorCode` has none); `VendorCode` mandatory; an inactive vendor is refused **at submit**, not retroactively |
| Phase 2 — split the validation stages; block rollback of a VALID document | **Accepted** | Step 8 (stages), step 33 (rollback lock test) |
| Phase 3 — extract small helpers out of `BuildSourceAsync` | **Accepted** | Steps 16-17 |
| Phase 4 — verify service-level authorization, not just UI hiding | **Accepted** | Step 26 |
| Phase 5 — state-driven UI enablement must be explicit | **Accepted** | Step 28 |
| Phase 6 — end-to-end and failure workflows | **Accepted** | Step 37 |
| §5 — do not introduce a generic self-billed abstraction | **Accepted** | Phase 3 step 16 keeps the dispatcher thin and extracts named helpers; a type→handler map refactor stays explicitly out of scope |

Two review premises are **not** accepted as written (they are corrected in the table above): that repair rebuilds a payload, and that vendor pre-submit validation has to be added. Both corrections reduce scope rather than add it.

## Prerequisite 0 — verify the LHDN contract (blocks Phase 3)

> **SUPERSEDED 2026-09-23.** Executed, and it produced the OPPOSITE conclusion to D2 — see
> `docs/einvoice-selfbilled-phase0-findings.md`. The party direction is corrected by
> `plans/plan-selfBilledPartyReversal.prompt.md`. The rest of this section is kept as history.

Types 11/12/13 are an external compliance contract. The vendored library already encodes several decisions (type codes, UBL namespace, signature id, note-origin requirement), but it is **evidence, not authority**. Before Phase 3, record in `docs/einvoice-selfbilled-phase0-findings.md`:

1. Types 11/12/13 are current, and what each is for (SBI vs SBC vs SBD).
2. **Party semantics** for self-billed: confirm `AccountingSupplierParty` is the *issuer* (our company) and the buyer block is the vendor — i.e. confirm **D2**. This is the highest-risk assumption in the plan.
3. Whether 12/13 must reference the **self-billed** invoice specifically (the library throws unless `OriginInvoiceUUID` is supplied, and `GenerateCreditNote` refuses a non-note type, but it cannot express "which document family the origin must be").
4. Required fields per type (classification code, MSIC, business description, address, E.164 phone) and anything the validator does **not** already check.
5. Cancellation rules and window, and whether a note may exist while its origin invoice is cancelled.

**STOP rule:** if the spec contradicts D2 or the origin rule, stop and re-plan before Phase 3 — do not absorb the divergence inside `BuildSourceAsync`.

## Lifecycle & rebuild rules

The e-invoice dimension is fixed by the shipped façade and must be mirrored, not reinvented:

- `EInvoiceStatuses.Locked` = `{SUBMITTING, SUBMITTED, VALID}` (`EInvoiceStatuses.cs:48-55`). The new services must refuse structural edits and deletes in those states, exactly as `SaInvoiceService.cs:645` (edit), `:825` (delete), `:1670` (rollback) and `SaCdnService.cs:697` (edit), `:896` (delete) already do. **This is a server-side rule, not a UI rule.**
- The payload is rebuilt from the ERP document at **exactly two entry points**: `ValidateAsync` and `SubmitBatchAsync` (Submit/Retry). `RefreshAsync` reads MyInvois and applies the mapped status without touching the source; `RecoverAsync` builds a source for reconciliation only and **never resubmits**. `RepairSubmissionAsync` is a single delegation to `RefreshAsync`.
- Consequence — **the rebuild window** is exactly the set the pre-submit gate lets through: `NEW`, `REJECTED`, `INVALID`, `CANCELLED`, and `FAILED + ConfirmedFailure`. `FAILED + Unknown` must run `Recover` first. `SUBMITTED`/`VALID` are refused as *already submitted* by the gate (`SubmitBatchAsync` status switch), so a document that reached the portal can never be silently rebuilt with changed vendor data. This is the same partition as `EInvoiceStatuses.IsBuyerIdentityFrozen` and it is the reason D7 is safe.

| `IRBMStatus` | Structural edit | Delete | Rollback | Submit / Retry | E-STATUS | CANCEL |
|---|---|---|---|---|---|---|
| NULL / `NEW` | allowed | allowed | n/a — retired | allowed | – | refused |
| `SUBMITTING` | refused | refused | refused | refused — run Recover | allowed when a UUID exists | refused |
| `SUBMITTED` | refused | refused | refused | refused — already submitted | allowed | allowed in the window |
| `VALID` | refused | refused | refused | refused — already submitted | allowed | allowed in the window |
| `INVALID` | allowed (fix + resubmit) | allowed | allowed | allowed | allowed | refused |
| `REJECTED` | allowed | allowed | allowed | allowed | – | refused |
| `FAILED` + `ConfirmedFailure` | allowed | allowed | allowed | allowed | – | refused |
| `FAILED` + `Unknown` | allowed, but a rebuild+submit is refused until Recover succeeds | allowed | allowed | refused — Recover first | – | refused |
| `CANCELLED` | allowed | allowed | allowed | allowed (a new submission) | allowed | refused |

The ERP dimension (`NEW` vs `POSTED`) is **RETIRED for the self-billed families** — superseded by
`plans/plan-poSelfBilledLifecycle.prompt.md` (implemented 2026-09-23). The original design mirrored
`PoInvoiceService`/`PoCdnService` (including POST/ROLLBACK and a `POSTED`-before-submit rule); that was
rejected in review because these documents have no stock/AP/GL effect, so the ERP transition bought
nothing. As shipped now:

- **Edit and Delete** are gated by the e-Invoice state alone (`PoSbCalc.IsEInvoiceLocked`): `SUBMITTING` /
  `SUBMITTED` / `VALID` refuse; `NEW`, `INVALID`, `REJECTED`, `CANCELLED` and `FAILED` allow. A legacy
  `POSTED` row behaves exactly like a `NEW` one.
- **`PostAsync` / `RollbackAsync` do not exist** on `IPoSbInvoiceService` / `IPoSbCdnService`, and no UI
  offers them. Nothing writes `POSTED` any more; `PoSbStatuses.Posted` survives only as the legacy value
  the list status filter still offers.
- **Submission no longer requires POSTED**: `SaEInvoiceService.IsSourceDocumentSubmittable` keeps the
  POSTED rule for SALES invoices and sales notes only, and returns true for the self-billed families.

## Confirmed decisions

- **D1 Data model:** new slim tables `PoSbInvoice`/`PoSbInvoiceDetail` + `PoSbCdn`/`PoSbCdnDetail` — *not* a `SelfBilled` flag on `PoInvoice`/`PoCdn`.
- **D2 Payload parties:** Supplier = our company (issuer), Customer = the vendor (`PoSupplier`). The mapper is unchanged; only `BuildSourceAsync`'s two party assignments depend on this. **SUPERSEDED 2026-09-23 — reversed by `plans/plan-selfBilledPartyReversal.prompt.md`:** the VENDOR is the payload `Supplier` and our COMPANY is the `Customer`.
- **D3 Menus:** dedicated `PO_SB_INVOICE` / `PO_SB_CN` / `PO_SB_DN`, so `AuthorizeAsync`'s menu switch **must** be repointed (today it maps SBI/SBC/SBD to `PO_INVOICE`/`PO_CN`/`PO_DN`, `SaEInvoiceService.cs:3279-3287`).
- **D4 Vendor block:** read live from `PoSupplier` at build time; no snapshot columns on the document.
- **D5 Origin for SBC/SBD:** our own `PoSbInvoice` with `IRBMStatus = VALID` and a non-blank `IRBMUUID`.
- **D6 Scope:** no stock / AP / GL effect — tax document only. "Copy from Purchase Invoice" seeding is excluded.
- **D7 Vendor block stays live, with a locked rebuild window.** No snapshot columns in v1. A payload can only be rebuilt while the document is in the *rebuild window* (see *Lifecycle & rebuild rules*), so vendor-master edits can never change an already-submitted document. If audit reproducibility is later required, mirror `SaInvoice.Buyer*`: **write-only** columns stamped at the `SUBMITTING` claim, never read back into the payload (that is exactly what the sales side does — plan-einvoiceInvalidResubmit phase D-1).

## Implementation status (2026-09-23) — ALL PHASES IMPLEMENTED

> **LIFECYCLE SUPERSEDED 2026-09-23** by `plans/plan-poSelfBilledLifecycle.prompt.md`: POST/ROLLBACK were
> removed and submission no longer requires the ERP `POSTED` status. See the note under *Lifecycle &
> rebuild rules* below. Everything else in this document still describes the shipped code.

Phases 1-6 are implemented and verified. Nothing is outstanding except the manual smoke test and the
Prerequisite 0 spec check (which the sandbox submission will settle).

- Build: `dotnet build ErpWeb.slnx` (whole solution incl. the web host) — **0 errors**.
- Full suite: **1991 total / 1976 passed / 15 failed / 0 skipped** (+36 tests, all green).
  The 15 failures are unchanged and pre-existing: 13 `SaCustServiceTests`(9) + `PoSupplierServiceTests`(4)
  GL-code/phone WIP, plus 2 from a **library defect unrelated to this feature** —
  `EInvoiceDocumentTypeMap` declares all four namespace constants as `…:xsd:Invoice-2` while its own test
  expects `CreditNote-2` / `DebitNote-2` for codes 12/13. Proven pre-existing: the *committed* library and
  the *committed* test disagree, and neither file is in this change set. The vendored library is
  deliberately not modified — the one-line fix is a separate decision.
- New tests: `PoSbServiceTests` (25 — derived totals, NEW/POSTED, the e-Invoice lock on edit/delete/rollback,
  line validation, inactive vendor, batch cap, authorization, tenancy, stale `RowVersion`, and every origin
  rule incl. vendor/currency matching and the CN-vs-DN menu split) and `SaEInvoiceSelfBilledPayloadTests`
  (11 — LHDN 11/12/13, **party direction**, no origin for an invoice, origin UUID carried by a note, the
  generator's `InvoiceTypeCode`, plus the refusals: non-VALID origin, incomplete vendor, unposted document,
  incomplete line, unknown document). New `EInvoiceTestHost` seams: `SeedVendorAsync`,
  `SeedSbInvoiceAsync`, `SeedSbCdnAsync`, `SbInvoiceKey`/`SbCdnKey`, `GetSbInvoiceAsync`/`GetSbCdnAsync`,
  and `ExpectedMenu` extended.
- Dev `ERPWeb` applied and verified: 4 tables (34/22/36/22 columns, checked **by name** incl. `IRBMOutcome`,
  `IRNMCancelOn`, `OriginSbInvNo`, `Type`), 3 `AdSmNumDate` rows, 3 active menus, 24 `MenuPermission` rows.
  All four scripts exit 0 on a repeat run (idempotent). The tables already existed at first run (create_date
  2026-09-23 12:19; only 4 of 97 tables date from today) — the shape was **verified, not assumed**.
- Delivered: 4 entities + 4 configurations (tables `POSbInvoice`, `POSbInvoiceDetail`, `POSbCdn`,
  `POSbCdnDetail`), `PoSbCalc`, `PoSbSharedResults`, `IPoSbInvoiceService`/`PoSbInvoiceService`,
  `IPoSbCdnService`/`PoSbCdnService`, `PoSbLookupsLoader`, `PoSbOriginResolver`,
  `PoSupplierBuyerProfileResolver`, 4 SQL scripts, 3 `MenuCodes` + 3 `menus.xml` rows, and the full
  façade wiring (steps 13-22 incl. the `ValidateNoteOrigin` fix and the rebuild-window comment).
- **Deviations (deliberate):** (1) no separate repository classes — the services use `AppDbContext`
  directly and `PoSbLookupsLoader` holds the shared queries; (2) shared DTOs in `PoSbSharedResults.cs`
  instead of duplicating `PoSbQuery`/line/request types per family; (3) the origin rule is split — save
  time requires the origin to **exist** in the same company+branch and to match vendor+currency, while
  "must be VALID with a UUID" stays at payload build so a note can be drafted while its invoice is still
  being submitted; (4) `PoSbLimits.TaxDecimals = 2` rather than a company setting.
- **Test-driven decision (new):** the e-Invoice lock is evaluated **before** the ERP status rule in
  edit/delete, so a POSTED+SUBMITTED document reports "cancel the e-Invoice first" rather than "Only NEW
  documents can be edited" — the actionable message wins.
- **Phase 5 UI delivered:** `PoSbInvoiceList` / `PoSbCdnList` (iv- chrome + `CommonDataGridEx`, `E-Inv` /
  `E-UUID` / `E-Status` columns, E-INV / E-STATUS / CANCEL toolbar with a `GetStatusManyAsync` pre-flight
  and the `iv-einv-*` per-row results popup, `AuditColumns.For(n)`, a data source exposing `CurrentQuery`,
  compact/mobile list and the "Why is this invalid?" LHDN detail link) and `PoSbInvoice` / `PoSbCdn` entry
  pages (`<SaDocPage>`, header form, line grid + popup, totals, Save/Post/Rollback and
  `<SaEInvoicePanel>` with the `OnStateChanged` edit lock).
- **UI-phase decisions worth knowing:** (1) `PoSbQuery` is **mutable** (`{ get; set; }`) like
  `PoCdnListQuery`/`SaCdnListQuery`, because a grid data source clones it and overwrites
  `Skip`/`Take`/`SortField` per request; (2) `PoSbTaxGroupLookupRow` gained `Percentage` so the line popup
  can **preview** tax (the service still resolves and stores the authoritative value); (3) the note's
  origin is chosen from the **posted self-billed invoices of the same vendor**, which enforces the
  vendor-match rule in the UI as well as the service; (4) the line popup exposes qty / price / UOM / tax
  group / classification / inclusive flag but **not** the discount slots — they default to 0 and the server
  supports them, so adding the inputs later is a UI-only change; (5) E-STATUS requires a selection: the
  purchase-typed `RefreshSubmittedAsync` overload (plan step 20, marked optional) was **not** taken, so
  there is no "refresh every SUBMITTED in this branch" mode yet.
- **Verification:** the four new pages are compile-verified only (`dotnet build ErpWeb.slnx`, 0 errors). A
  browser smoke test of the two list screens and one full SBI → VALID → SBC flow is still owed, because the
  test suite cannot exercise Razor rendering.

## Steps

### Phase 1 — Model + DDL (no behaviour change)

1. New entities in `ErpWeb.Model/Entities/Purchase/`: `PoSbInvoice`, `PoSbInvoiceDetail`, `PoSbCdn`, `PoSbCdnDetail`. **Physical table names are pinned here** (identical in the configurations, the create scripts and every verification query): `POSbInvoice`, `POSbInvoiceDetail`, `POSbCdn`, `POSbCdnDetail`. Header keys `(CompanyCode, BranchCode, DocNo)`; IRBM block named/spelled exactly like `SaCdnConfiguration` (`IRBMStatus`, `IRBMOutcome`, `IRBMSubmitID`, `IRBMUUID`, `IRBMORIUUID`, `IRBMSentOn`, `IRBMValidOn`, `IRBMError`, `IRNMCancelOn`) — `IRBMOutcome` and `IRNMCancelOn` are the two that `PoCdn` is missing. `PoSbCdn` also carries `Type` (CN/DN) and `OriginSbInvNo` (a soft reference resolved within the document's company+branch — see step 11).
2. Four configurations in `ErpWeb.Model/Configurations/Purchase/` (auto-scanned by `ApplyConfigurationsFromAssembly`), with `RowVersion`, `datetime2` columns and indexes `(CompanyCode, BranchCode, Status, DocDate)`, `(CompanyCode, BranchCode, VendorCode)`, plus an IRBM-status index for refresh-all. **No filtered index** (forces `QUOTED_IDENTIFIER` on all DML).
3. DbSets in `AppDbContext` next to `PoInvoices`/`PoCdns` (`:96-99`), and an `InventoryLeftoverSite.Apply` overload per new entity (tenant-stamp convention).
4. `scripts/create-po-sb-invoice.sql` + `scripts/create-po-sb-cdn.sql` — additive, idempotent, DBA-run; the same columns go into both the create script and any alter script (the create-vs-alter drift trap). No FK to `PoSupplier` (house convention: `PoInvoice.VendorCode` has none) and no FK on `OriginSbInvNo` (soft reference, authority = step 11). `VendorCode` is mandatory on every header.
5. `scripts/seed-po-sb-numbering.sql` — `AdSmNumDate` rows for `PO_SBI` / `PO_SBC` / `PO_SBD`, pattern copied from `seed-po-invoice-numbering.sql`.
6. Prove the schema before any service work: `sqlcmd -E -d ERPWeb -Q "SELECT ... FROM sys.columns WHERE object_id = OBJECT_ID('dbo.POSbInvoice')"` for all four tables (`-d` always supplied; the object must resolve). Only then start Phase 2.

### Phase 2 — Core service (no e-invoice yet)

7. `ErpWeb.Core/Purchase/PoSbCalc.cs`: `PoSbStatuses` (NEW/POSTED), `PoSbTypes` (CN/DN), `PoSbLimits` (`NumberingModule` per family — `PO_SBI`/`PO_SBC`/`PO_SBD` — and `MaxPostSelection`), plus the money rules **reusing** the shipped helpers instead of new maths: `PoOrderCalc.ComputeAmount` / `ApplyTwoLevelDiscount` / `ComputeTax(..., taxDecimals)` / `SumTotals` and `PoInvoiceCalc.ApplyHeaderTotals`. Contract to pin with unit tests: `GrossAmnt = Σ NetAmount`, `Taxes = Σ TaxAmt` and `TotAmnt = GrossAmnt + Taxes` hold **by construction** through `ApplyHeaderTotals` (never hand-assigned); line money is rounded at the same point as `PoInvoice` (`PoOrderCalc`); the tax-decimals source is the same one `PoInvoiceService` passes into `PoInvoiceCalc.ComputeLineAmounts`; inclusive-priced lines are un-taxed by `ComputeTax`; discounts are the two-level `ItemDiscount` + `ItemDiscount1` shape.
8. `IPoSbInvoiceService`/`PoSbInvoiceService` and `IPoSbCdnService`/`PoSbCdnService`, mirroring the shipped DTO vocabulary (`…ListQuery`, `…ListRow`, `…Document`, `…LineDto`, `…LineRequest`, `…SaveRequest`, `…OperationResult`, `…Lookups`, `…VendorDefaults`) — *parallel with step 9*. Save/Post/Rollback/Delete; authorization through the new menu codes (`CanAsync` mirroring `PoCdnService.MenuCodeFor(type)` so CN and DN check different menus); numbering via `IDocumentNumberingService.NextAsync`; and the **e-invoice lock guard at every write entry point** (`EInvoiceStatuses.IsLocked` → validation refusal, message shape copied from `SaCdnService.cs:697-701`). Validation is split into ordered **stages** inside `PrepareAsync` so a failure names its stage: (a) ERP status rules, (b) e-invoice lock, (c) vendor profile completeness, (d) origin resolution for CN/DN, (e) per-line validation, (f) calculation. Collect per-line errors and return them **before** any document-level check — the validation-message-masking trap this repo has hit twice. Vendor defaults (currency, tax group) from `PoSupplier`.
9. Repositories `PoSbInvoiceRepository` / `PoSbCdnRepository` in `ErpWeb.Model/Repositories/Purchase/` following the single-file `I<Entity>Repository` + `<Entity>Repository` pattern (`SearchPagedAsync` with `MaxPageSize = 100`, `LockForUpdateAsync`, `GetWithDetailsAsync`).
10. DI registrations in `CoreServiceCollectionExtensions.cs` next to `:184/:186`.
11. New `ErpWeb.Core/Purchase/PoSbOriginResolver.cs` — static, takes the caller's `AppDbContext` so the façade needs **no new DI dependency**. `ResolveValidSbInvoiceOriginAsync(db, companyCode, branchCode, originSbInvNo, ct)` returns the origin row or a typed reason, and is the ONE place these are enforced: resolved by `(CompanyCode, BranchCode, DocNo)` — never a bare `DocNo` (same scope rule `BuildSourceAsync` already uses for `SaCdn.InvNo`); the row is the self-billed invoice family; the document is not deleted; `IRBMStatus = VALID`; `IRBMUUID` non-blank. Callers: `PoSbCdnService` (save-time warning) and `BuildSourceAsync` (hard gate → `OriginInvoice.No` / `OriginInvoice.Uuid`). A `CANCELLED` origin must be refused, and cancellation never rewrites `IRBMORIUUID`.
12. Vendor pre-submit completeness: **no new validator is required.** `EInvoiceValidator.ValidateBuyer` + `RequireE164` already enforce name, TIN, RegNo, `RegType ∈ {BRN, NRIC, PASSPORT, ARMY}`, Addr1, City, PostalCode and an E.164 phone — and for self-billed the `Customer*` block *is* the vendor, so those field-keyed errors already fire before the `SUBMITTING` claim. Add an ERP-side guard that names the vendor (`PoSupplier.SuppCode`) so the message is actionable, and confirm the vendor is active: an inactive vendor is refused **at submit**, never retroactively for a document already accepted.

### Phase 3 — e-Invoice façade wiring (*depends on Prerequisite 0 + Phases 1-2*)

13. `EInvoiceStatuses.cs`: widen `EInvoiceDocumentTypes.IsKnown` to all six types (only 3 call sites: `AuthorizeAsync:3261`, `SaEInvoicePanel.razor.cs:94`, one test); keep `IsSelfBilled`.
14. `AuthorizeAsync` (~3256-3305): point `SBI/SBC/SBD` at the **new** `PO_SB_*` menu codes (replacing the current purchase-menu mapping) and delete the `NotConfigured` refusal.
15. `LoadStateAsync` (~2573), `ApplyState` (~2670) and `IsSourceDocumentPosted` (~3331): add both entity cases — branch-scoped load, CN/DN `Type` match, `POSTED` required.
16. `BuildSourceAsync` (~2809): keep it as a thin dispatcher and extract `BuildSbInvoiceSourceAsync` / `BuildSbCdnSourceAsync` as private helpers. SBI sets **no** `RefDocumentNo`/`OriginUuid`; SBC/SBD resolve the origin through `PoSbOriginResolver` (step 11) and populate `RefDocumentNo` + `OriginUuid` from the resolved row. `Customer*` comes from the resolver in step 17; `Supplier` stays the company profile.
17. New pure `PoSupplierBuyerProfileResolver` + record (mirror `SaCustBuyerProfileResolver` minus the AppInvoice branch; `RegType` returned RAW; `SupplierBrn` → `RegNo`; `GstregNo` → `SstNo`; `Tel` → `Phone`).
    **UPDATED 2026-09-23:** renamed to `PoSupplierPartyProfileResolver` / `PoSupplierPartyProfile` and it now supplies the payload's **Supplier** block (a self-billed document reverses the parties) with `MiscCode` + `BizDesc` added. See `plans/plan-selfBilledPartyReversal.prompt.md`.
18. **Fix** `EInvoiceValidator.ValidateNoteOrigin` (`:188-200`): currently requires an origin for *everything except* `INV`, so an SBI would be wrongly refused. Require it only for the four note families.
19. `ResolveKeyByUuidAsync` (~1487) + `RepairSubmissionAsync` (~1331): add both tables (`IrbmUuid` **or** `IrbmOriUuid`) and delete the self-billed refusal so the E-UUID click/repair works.
20. `DocumentTypeLabel` (~3347): add SBI/SBC/SBD labels. Optionally add a purchase-typed `RefreshSubmittedAsync` overload for the no-selection E-STATUS button.
21. Verify, do not change: the group key `EInvoiceDocumentTypeMap.IsCreditOrDebitNote(GetDocumentTypeCode(...))` already routes 12/13 to `SubmitCreditDebitNotes` and 11 to `SubmitInvoices` (`SaEInvoiceService.cs:2118-2139`). Pin it with a test rather than touching the routing.
22. Record the rebuild-window rule as a code comment at the `BuildSourceAsync` dispatcher: the payload is rebuilt only by `ValidateAsync` and `SubmitBatchAsync`; `RefreshAsync`/`RecoverAsync` must never rebuild one, or D7's guarantee silently breaks.

### Phase 4 — Menus / permissions (*depends on 14 for the menu codes*)

23. `MenuCodes` constants + `menus.xml` rows under `PO_TRANSACTIONS` (SortOrder 7/8/9) — XML **and** SQL must ship together or `MenuSyncService` soft-disables the menus.
24. `scripts/init-pobsb-menu.sql`: menu rows + `MenuPermission` ACCESS/ADD/EDIT/DELETE/POST/ROLLBACK + SUBMIT/CANCEL for the three menus, idempotent, mirroring `init-sales-cdn-einvoice-permissions.sql` (note `RoleMenuPermission` uses `IsAllowed`, never `IsActive`).
25. Apply 4+5+24 to dev `ERPWeb` with `sqlcmd -E -d ERPWeb`, twice, to prove idempotency; assert all three menus exist and each carries the seven permission rows.
26. Prove authorization is **service-level, not UI hiding**: call `SaveNewAsync` / `PostAsync` / `ISaEInvoiceService.SubmitAsync` directly as a user without the menu right and assert a refusal. Menu authorization and service authorization must agree (`PoCdnService.CanAsync(type, ...)` is the pattern).

### Phase 5 — UI (*parallel with Phase 4*)

27. `PoSbInvoiceList.razor(.cs)` + `PoSbCdnList.razor(.cs)` copying `PoInvoiceList`/`PoCdnList` chrome, with a `GridCustomDataSource` that exposes **`CurrentQuery`** (needed for refresh-all), `E-Inv`/`E-UUID`/`E-Status` columns, E-INV / E-STATUS / CANCEL toolbar, the `iv-einv-*` result popup and `..AuditColumns.For(n)`.
28. `PoSbInvoice.razor(.cs)` + `PoSbCdn.razor(.cs)` entry pages using `<SaDocPage>`, line grid, totals, Save/Post/Rollback and `<SaEInvoicePanel DocType="SBI|SBC|SBD" MenuCode="…">` with the `OnStateChanged` edit lock. Enablement is **derived from the lifecycle matrix**, never from the ERP status alone: Save/Delete/Rollback disabled while `EInvoiceStatuses.IsLocked`; Save/Post disabled until the document has been saved (and Post until it is POSTED-eligible); E-INV disabled unless POSTED **and** `SaEInvoiceStatusView.CanSubmit`; E-STATUS disabled unless `CanRefresh`; CANCEL disabled unless `CanCancel`; all disabled while a batch action is in flight (`IsEInvoiceBusy`) and while the document is dirty/unsaved.
29. Reuse `EInvoiceDetailLink`, `EInvoicePortalLinkOpener` and `/sales/einvoice/detail/{uuid}` verbatim (family-agnostic; gated by `SA_EINVOICE_TIN`).
30. Wire the no-selection E-STATUS path through `DataSource.CurrentQuery` (the `SaInvoiceList.razor.cs:698` pattern) and cover it with the refresh-all test in step 33 — a `CurrentQuery` that does not clone the grid's own filters makes "what the operator sees" and "what gets refreshed" drift apart.

### Phase 6 — Tests

31. `EInvoiceTestHost`: add `SeedSbInvoiceAsync` / `SeedSbCdnAsync` + private `BuildSbInvoice`/`BuildSbCdn` mirrors; extend `ExpectedMenu`.
32. Update the three assertions that pin the old limitation: `SaEInvoiceSelfBillAndTinTests:76` and `:87`, `SaEInvoiceSubmissionRepairTests:320`.
33. Service tests (SQLite): save/post/rollback, POSTED gate, stale `RowVersion`, tenant isolation, the `IsLocked` refusal on edit/delete/rollback, the **refresh-all candidate set** (a `CurrentQuery` that clones the grid's own filters → the refreshed set, mirroring the sales refresh-all tests), and **document-number allocation under concurrent creation** (no duplicates, correct prefix, correct company/branch, no reuse after delete — precedent `PoInvoiceSqlServerConcurrencyTests`, which seeds `PO_INV`) on the SQL Server scratch DB.
34. Origin integrity tests: cross-company origin refused; cross-branch origin refused; missing origin; origin not `VALID`; origin `CANCELLED`; origin with a blank UUID; and "a POSTED note's origin cannot be changed".
35. Payload positive tests, asserting the **generated** payload via the public `GenerateDocHelper.getInvoiceLine` (no HTTP): `InvoiceTypeCode._` is `11`/`12`/`13`; `Supplier` = our company; `Customer` = the vendor; SBI carries no origin and SBC/SBD do; 12/13 route to `SubmitCreditDebitNotes` and 11 to `SubmitInvoices`.
    **SUPERSEDED 2026-09-23:** the direction assertion is the reverse — `Supplier` = the vendor, `Customer` = our company. The type codes, origin and routing assertions still hold.
36. Payload **negative** tests (each asserts the field-keyed error **and** `FakeSubmitDocumentHelper.Calls` stays empty): missing vendor TIN; invalid vendor phone; missing vendor address; missing vendor registration identity; SBI with an origin set; SBC/SBD without an origin; SBC/SBD whose origin is not `VALID`; swapped party assignment; wrong type code; cross-company origin.
37. End-to-end workflows through the façade: (a) create SBI → POST → submit → `VALID` → create SBC against it → POST → submit → `VALID`; (b) create SBI → POST → submit → `INVALID` → correct the vendor master → resubmit succeeds; (c) SBI `VALID` → create SBC → attempt to change the origin after submit → refused.
38. Security tests: tenant isolation, branch isolation, menu permission and direct-service authorization (step 26), plus "a user with only `PO_SB_INVOICE` rights cannot submit a `PO_SB_CN` document".
39. Run `--filter "FullyQualifiedName~EInvoice"` first (fast signal), then the full suite; `Skipped: 0` together with `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` is the only evidence the SQL Server classes actually ran.

## Relevant files

- `ErpWeb.Core/EInvoice/SaEInvoiceService.cs` — `AuthorizeAsync` (~3256), `LoadStateAsync` (~2573), `ApplyState` (~2670), `BuildSourceAsync` (~2809), `ResolveKeyByUuidAsync` (~1487), `RepairSubmissionAsync` (~1331), `IsSourceDocumentPosted` (~3331), `DocumentTypeLabel` (~3347): the whole façade change lands here.
- `ErpWeb.Core/EInvoice/EInvoiceStatuses.cs` — `EInvoiceDocumentTypes.IsKnown` (`:129`) / `IsSelfBilled` (`:136`).
- `ErpWeb.Core/EInvoice/EInvoiceValidator.cs` — `ValidateNoteOrigin` (`:188`) SBI fix; `ValidateLines` (`:136`) sets the `Qty > 0` rule that constrains the line shape.
- `ErpWeb.Core/Sales/SaCustBuyerProfileResolver.cs` — the template for the new `PoSupplierBuyerProfileResolver`.
- `ErpWeb.Core/Sales/SaInvoiceService.cs` (`:645` edit guard, `:825` delete guard, `:1670` rollback guard — the shipped wording for "cannot be edited while its e-Invoice status is …") and `ErpWeb.Core/Sales/SaCdnService.cs` (`:697` edit, `:896` delete) — the shipped **server-side** `EInvoiceStatuses.IsLocked` pattern the new services must mirror (step 8).
- `ErpWeb.Core/Purchase/PoInvoiceCalc.cs` (`ComputeLineAmounts`, `ApplyHeaderTotals`) + `PoOrderCalc.cs` (`ComputeAmount`, `ApplyTwoLevelDiscount`, `ComputeTax`, `SumTotals`, `RoundQty`) — the money rules to reuse, not reinvent (step 7).
- `ErpWeb.Model/Entities/Purchase/PoCdn.cs` + `Configurations/Purchase/PoCdnConfiguration.cs` (`:50`, `:58`) — the IRBM column spellings to copy.
- `ErpWeb.Core/Purchase/PoInvoiceService.cs` / `PoCdnService.cs` / `PoInvoiceCalc.cs` / `PoCdnCalc.cs` — numbering, status, authorization and `PrepareAsync` patterns.
- `ErpWeb.Model/Data/AppDbContext.cs` (`:92-113`, `:117`) and `ErpWeb.Core/CoreServiceCollectionExtensions.cs` (`:184`, `:186`) — registration points.
- `ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor.cs` (`BeginEInvoiceAsync:527`, `EInvoiceIneligibleReason:786`, `DataSource.CurrentQuery` at `:698`) and `SaInvoice.razor:526` — the list/panel wiring to mirror.
- `ErpWeb/Menus/menus.xml` (lines 75-83), `scripts/init-menu-access.sql` (`:415-423`), `scripts/init-sales-cdn-einvoice-permissions.sql`, `scripts/seed-po-invoice-numbering.sql`.
- `ErpWeb.Tests/EInvoiceTestHost.cs` (`:277`, `:476`, `:728`, `:741`) and `FakeSubmitDocumentHelper.Submitted`.

## Verification

**Build**
1. `dotnet build ErpWeb.slnx --nologo -v:q` → **0 `error CS`/`RZ`** (the test suite never compiles `.razor`, so this is mandatory after Phase 5).
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` → no new failures; then re-run with `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` and `ConnectionStrings__SqlServerTestConnection` set in **separate** commands, and confirm `Skipped: 0`.

**Database**
3. `sqlcmd -E -d ERPWeb` runs of the create scripts twice → the second run is a clean no-op; `sys.columns` matches the entity configurations for all four tables; the three new indexes exist; exactly one `AdSmNumDate` row per `NumCd` (`PO_SBI`/`PO_SBC`/`PO_SBD`).
4. `-d` supplied on every probe and the object must resolve before any conclusion is drawn (a missing database produces a confident wrong answer — it has happened in this repo).

**Functional**
5. Generated-payload assertions pass for 11/12/13: type code, party direction, no origin for SBI, origin for SBC/SBD, and `GrossAmnt + Taxes == TotAmnt` equals the sum of the lines.
6. All ten negative payload cases (step 36) fail with the expected field key and make **no** HTTP call.
7. Workflows (step 37): happy path SBI → `VALID` → SBC → `VALID`; failure path `INVALID` → vendor corrected → resubmit; origin immutability after submit.
8. `IRBMUUID` / `IRBMORIUUID` persist correctly (SBC/SBD record the origin UUID; SBI has neither).

**Security**
9. Tenant isolation, branch isolation, menu permission, and direct-service authorization (steps 26/38) — the UI must not be the only gate.

**Submission**
10. Success, INVALID response, duplicate-submission prevention, recover, refresh status, and cancellation, each asserted through `FakeSubmitDocumentHelper` (plus the SQL Server concurrency suite for numbering).
11. Manual smoke: create → post → E-INV submit one SBI, then one SBC referencing it; confirm the `EInvDocSubmission` rows and the JSON dump under `App_Data/einvoice` (`_inv_json.json` / `_cn_json.json`).

## Scope boundaries

- The decisions of record are **D1-D7** above; nothing in this section overrides them.
- **Out of scope:** `PoCdn`'s dormant IRBM columns and `SelfBilled` bit are left untouched; no change to `ErpWeb.EInvoiceLib`; no PO/GR lineage; no stock, AP or GL effect; no "copy from Purchase Invoice" seeding; no approval/workflow; no type→handler-map refactor of `AuthorizeAsync`/`LoadStateAsync`/`ApplyState`.
- **Vendor data required at submit:** TIN, RegType ∈ {BRN, NRIC, PASSPORT, ARMY}, registration number (`SupplierBrn`), address line 1, city, postal code and an E.164 phone; `GstregNo` supplies the SST number. The vendor must be active — enforced at submit, never retroactively.
- **UUID bookkeeping:** `ApplyState` writes `IRBMORIUUID` for SBC/SBD at submit; SBI has none. Cancellation never rewrites it.

## Further Considerations

1. **Delivery order** — not yet answered: "SBI first" vs "all three together". Recommendation: **all three in one pass**, because the façade change (steps 13-22) is shared and shipping the notes later means touching `BuildSourceAsync`/`IsKnown` twice. The phases are ordered so SBI alone is a valid stop after Phase 5's SBI half. Option A: all three (recommended) / Option B: SBI only first, SBC/SBD as a follow-up plan.
2. **Tax-only lines** — `EInvoiceValidator.ValidateLines` refuses `Qty <= 0`, so `PoCdn`'s tax-only adjustment line cannot be submitted as a self-billed note. Self-billed note lines will be quantity-bearing only. If tax-only adjustments are required, that is a validator change and a separate decision.
3. **Vendor snapshot for audit reproducibility** — deliberately **not** in v1 (D7). The rebuild window makes payload drift impossible, so a snapshot would only add an audit record. If LHDN or the auditor requires proof of exactly which vendor data was sent, mirror `SaInvoice.Buyer*`: write-only columns stamped at the `SUBMITTING` claim and never read back into the payload. Revisit after Prerequisite 0.
