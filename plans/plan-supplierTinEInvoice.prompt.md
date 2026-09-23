# Plan: Supplier TIN field for self-billed e-Invoice

A self-billed invoice (LHDN 11) reverses the parties: your company is the payload `Supplier`, and the **vendor is the `Customer` (Buyer)**. `PoSupplierBuyerProfileResolver` reads the vendor row and `EInvoiceValidator.ValidateBuyer` validates it — which is why a supplier-profile gap appears as `Buyer.*`. The blocking gap is exactly one field: `PoSupplier.TinNo` exists in the entity, EF config and DDL, and *is* read by the resolver, but is absent from the VM, the service read/write path and the screen, so it can never be entered. Add it — capture only, no save gate — plus a short note on the input.

**Steps**

*Phase 1 — Core (VM + service)*
1. `ErpWeb.Core/Purchase/PoSupplierResults.cs` — add `public string? TinNo { get; set; }` to `PoSupplierEditVm`, next to `SupplierBrn`/`RegType`.
2. `ErpWeb.Core/Purchase/PoSupplierService.cs` → `ApplyHeaderFields` (L534) — add a `SetIfChanged(...)` for `entity.TinNo` right after the `SupplierBrn` line (L539).
3. `PoSupplierService.cs` → `MapEditVm` (L864) — add `TinNo = x.TinNo,` near `RegType = x.RegType` (L874).
4. `PoSupplierService.cs` → `ValidateModel` (L629) — length-only guard (≤ 20) so an overlong TIN is a field message rather than a SQL truncation. **This mirrors the physical `nvarchar(20)` column capacity and is NOT LHDN TIN-format validation** — nothing in this step claims the value is a format LHDN will accept. No required-field rule.

*Phase 2 — UI (depends on 1–4)*
5. `ErpWeb.UI/Purchase/Masters/PoSuppEntry.razor` view mode — add `@Detail("TIN", Model.TinNo)` after COM REG NO in the Identity card.
6. Same file, edit mode — add a TIN `DxTextBox` (`maxlength="20"`) after COM REG NO, with a small `po-supp-hint` line naming the e-Invoice requirement, plus `@FieldError("TinNo")` for the length guard. No required asterisk, since save is not blocked.
   - **Defence in depth, three independent layers:** `maxlength` is UX only → `ValidateModel` is the application guard → `nvarchar(20)` is the database guard. The UI cap does not replace the server check, and none of the three asserts LHDN validity.
7. `PoSuppEntry.razor.cs` — `Clone` copies `TinNo`; `CloneForCopy` clears it alongside `BankName`/`AccountNo`/`GlCode`.

*Phase 3 — Tests (depends on 1–4)*
8. `ErpWeb.Tests/PoSupplierServiceTests.cs` — add three tests:
   - `TinNo_RoundTrips_OnCreateAndEdit`
   - `TinNo_Blank_IsAllowed` — `TinNo = null` **and** `TinNo = ""` both save successfully. This is the regression test for the central "optional, never blocks save" decision.
   - `TinNo_Over20Chars_Rejected`
8a. Review additions (2026-09-23) — five more cases in the same class; every expectation below is checked against the shipped code:
   - `TinNo_IsTrimmedOnSave` — `NullIfWhiteSpace` (`PoSupplierService.cs:993`) trims, so `"  C1234567890  "` stores as `"C1234567890"`.
   - `TinNo_Exactly20Chars_IsAccepted` — `ValidateModel` trims and then tests `> 20`, so exactly 20 characters saves (21 is rejected). Pins the boundary the implementation actually establishes.
   - `TinNo_ClearedToBlank_Saves` — clearing an existing value must store `NULL`. This guards `SetIfChanged`'s `!Equals(current, newValue)` branch: a future "only assign when non-blank" refactor would silently make a TIN impossible to remove.
   - `TinNo_UnchangedOnEdit_KeepsValue` — a save that leaves the TIN identical succeeds and the value stays byte-identical (`SetIfChanged` short-circuits, so the column is not rewritten).
   - No new resolver or payload test — the e-Invoice leg is the acceptance run in *Verification* step 5, not a unit test.
9. Do **not** add a new resolver test — the `DB → Buyer.TIN` leg is already covered (see *Existing coverage*).
10. Capture the pre-existing `PoSupplierServiceTests` failure baseline *before* the change, then compare — that class has known baseline failures.

*Test matrix*

| Test | Expected |
|---|---|
| Create supplier with TIN | Saves |
| Edit existing supplier TIN | Updates |
| Re-open supplier | TIN persists |
| Blank TIN (`null` and `""`) | Saves |
| TIN > 20 characters | Validation error |
| Copy supplier | TIN blank |
| Resolver reads TIN (existing test, L38-43) | `Buyer.Tin` populated |
| Existing `PoSupplierServiceTests` | Baseline unchanged |
| Self-billed resubmit | `Buyer.Tin` error resolved |

*Review additions (2026-09-23)*

| Test | Expected |
|---|---|
| TIN with leading/trailing spaces | Stored trimmed (`"  C1234567890  "` → `"C1234567890"`) |
| TIN exactly 20 characters | Saves |
| TIN 21 characters | Validation error (existing test) |
| Existing TIN cleared to blank | Saves, column becomes `NULL` |
| Edit that leaves the TIN identical | Save succeeds, value byte-identical (column not rewritten) |
| Self-billed submit with a valid TIN | The vendor-identity error key clears (today `Buyer.Tin`) — acceptance step 5 |

**Existing coverage (do not duplicate)**
Both legs of `PoSupplier.TinNo → Buyer.TIN` are already proven; the new service tests only add the missing `UI → VM → DB` leg.
- `ErpWeb.Tests/SaEInvoiceSelfBilledPayloadTests.cs` L38-43 (positive) — `EInvoiceTestHost.SeedVendorAsync` sets `PoSupplier.TinNo`, and the test asserts `header.Customer.TinNo == "C9876543210"` on the generated payload.
- `ErpWeb.Tests/SaEInvoiceSelfBilledPayloadTests.cs` L146-162 (negative) — `SeedVendorAsync(tin: null)` asserts the `Buyer.Tin` validation-error key.
- Both must stay green and unchanged by this work.

**Relevant files**
- `ErpWeb.Core/Purchase/PoSupplierResults.cs` — `PoSupplierEditVm` (L70)
- `ErpWeb.Core/Purchase/PoSupplierService.cs` — `ApplyHeaderFields` L534, `ValidateModel` L629, `MapEditVm` L864
- `ErpWeb.UI/Purchase/Masters/PoSuppEntry.razor` and `.razor.cs`
- `ErpWeb.Tests/PoSupplierServiceTests.cs` — host + seed at L80-135, `ValidNewModel` L417
- Reference only, unchanged: `EInvoiceValidator.cs` L80-113, `PoSupplierBuyerProfileResolver.cs`, `PoSupplier.cs:75`

**Verification**
1. Pre-flight data check on the failing vendor: select `TINNo, SupplierBRN, RegType, Address1, City, PostalCode, State, StateCode, Country, CountryCode, Tel, Email, Active` from `dbo.POSupplier` for that code.
2. Build `ErpWeb.Core`, `ErpWeb.UI`, `ErpWeb.Tests` — 0 errors.
3. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~PoSupplierServiceTests"` — new tests green, baseline unchanged.
4. Manual on `/purchase/suppliers`: blank TIN saves; TIN persists across edit/view; "Copy supplier" leaves TIN blank.
5. **End-to-end acceptance run — MANDATORY, the only proof of the business requirement.** The unit tests prove `DB → resolver → Buyer.TIN` and `VM → service → DB` separately; the requirement is the whole chain `Supplier UI → DB → self-billed e-Invoice → Buyer.TIN`. Do not close the work without it:
   - *Before:* the self-billed submit reports `Buyer.Tin: Customer TIN is required for e-Invoice.`
   - *Steps:* open the supplier for edit → enter the TIN → save → re-open the supplier → verify the TIN persisted → resubmit the self-billed e-Invoice.
   - *Expected:* **the vendor-identity error for the TIN is gone.** (The key is `Buyer.Tin` today — see *Direction dependency* below.)
   - **Do NOT assert that every `Buyer.*` error clears.** `Buyer.RegNo`, `Buyer.RegType`, `Buyer.Addr1`, `Buyer.City`, `Buyer.PostalCode`, `Buyer.Phone` and `Buyer.State` are independent supplier-master data gaps and must be evaluated separately.

**Decisions**
- TIN is captured, **not** enforced at save — `EInvoiceValidator` stays the gate.
- The `≤ 20` guard is a **database-capacity limit, not LHDN TIN-format validation**. TIN *format* is validated by MyInvois, and can be checked ad hoc on the `SA_EINVOICE_TIN` tools page.
- Small e-Invoice remark, no required asterisk.
- Copy clears the TIN.
- No `StateCode`/`CountryCode` UI (`State`/`Country` stay authoritative per `supplier_master_plan.md` decision 9).
- No `ErpWeb.EInvoiceLib` change and **no migration** — `POSupplier.TINNo nvarchar(20) NULL` already exists.
- **One validation authority.** No client-side or save-time TIN *format* validation is added. The chain stays capture → save → submit → `EInvoiceValidator` / MyInvois decides, with ad-hoc format checks on the `SA_EINVOICE_TIN` tools page. Adding a second authority (ERP format rules) would eventually disagree with MyInvois and create two sources of truth.
- Out of scope: save-time readiness gate, supplier-list TIN column, TIN search/validate button, self-billed screen changes, and any `LhdnCodeLookup` state-code alias work.

**Follow-up investigation (NOT part of this change)**
1. **Vendor `State` resolvability.** `LhdnCodeLookup.TryStateCode` accepts state names, `01`–`17`, and only its own abbreviations (`sgr`, `jhr`, `kdh`, `kltn`, `mlk`, `nsn`, `phg`, `png`, `prk`, `pls`, `trg`, `sbh`, `swk`). The repo's own state seed row is Code `SEL` / Name `Selangor` (`scripts/init-sales-masters.sql:338-339`).
   - **Verified 2026-09-23:** the `StateCodes` map contains `selangor` and `sgr` but **no `sel`** key
     (`LhdnCodeLookup.cs:21-46`). So the free-text path resolves the *name* and does not resolve the *code* —
     which value `PoSupplier.State` actually holds (the `IvCodeComboBox` binds `Code` or `Name`) decides whether
     a production submit passes. Confirm against live `IvMSCode` rows, do not assume.
   - The e-Invoice tests never exercise this path — `EInvoiceTestHost.SeedVendorAsync` seeds **both** `StateCode = "10"` and `State = "Selangor"`, and `PoSupplierBuyerProfileResolver` prefers `StateCode`, so the fixture passes regardless of whether the free-text value is resolvable. In production `StateCode` is always `NULL` (decision 9), so the free-text `State` is what actually gets read.
   - Consequence: the TIN work can be perfect and the submit can still fail on `Buyer.State`.
   - Raise as its own task: verify the live `IvMSCode` STATE rows, then *Option A* fix the master data, *Option B* add the ERP codes as aliases in `LhdnCodeLookup`, or *Option C* leave as-is. **Do not fold this into the TIN change** unless the actual failing vendor proves it is required.
2. The other `Buyer.*` errors (RegNo, RegType, Addr1, City, PostalCode, Phone) map to existing supplier fields — data entry, not code. `Tel` is canonicalised to E.164 on save, so a local format like `03-1234 5678` is accepted and stored as `+60312345678`.
3. `PoSupplierListRow` has no `TinNo`; a list column is optional.

---

## IMPLEMENTATION STATUS — CODE COMPLETE / ACCEPTANCE PENDING (2026-09-23)

All phases landed; no deviation from the decisions above. The code is complete and unit-verified, but
**production acceptance has not happened yet** — do not report this as "done" or "production-ready" on the
ticket until the three items in *Acceptance gate* below are closed.

**Review changes applied (2026-09-23):** status renamed DONE → CODE COMPLETE / ACCEPTANCE PENDING; the
supplier-UI → DB → self-billed submit run is now the mandatory acceptance gate (*Verification* step 5); the
test matrix gained the trim, 20-character boundary, clear-to-blank, no-op-edit and end-to-end rows; the
"one validation authority" decision is now stated explicitly; the `Buyer.*` keys are flagged as
direction-dependent.

**Files changed**
- `ErpWeb.Core/Purchase/PoSupplierResults.cs` — `PoSupplierEditVm.TinNo` (+ a doc comment stating it is optional and why).
- `ErpWeb.Core/Purchase/PoSupplierService.cs` — `ApplyHeaderFields` persists it via `SetIfChanged`; `MapEditVm` reads it; `ValidateModel` carries the `≤ 20` capacity guard.
- `ErpWeb.UI/Purchase/Masters/PoSuppEntry.razor` — view-mode `@Detail("TIN", Model.TinNo)` and an edit-mode `DxTextBox maxlength="20"` with the e-Invoice hint and `@FieldError("TinNo")`.
- `ErpWeb.UI/Purchase/Masters/PoSuppEntry.razor.cs` — `Clone` copies it; `CloneForCopy` clears it.
- `ErpWeb.Tests/PoSupplierServiceTests.cs` — the three tests from the matrix.

**Verification results**
- `dotnet build ErpWeb.UI` → **0 errors** (Razor + code-behind). `dotnet build ErpWeb.slnx` reports 8 `MSB3027/MSB3021` file-lock errors on `ErpWeb.csproj` only, because the `ErpWeb` app is running; **0 `error CS` / `error RZ`**.
- Full suite → **2005 total / 1990 passed / 15 failed / 0 skipped**. Baseline was 2002/1987/15, so this is **+3 tests and an unchanged failure set**: 13 pre-existing `SaCustServiceTests` (9) + `PoSupplierServiceTests` (4) WIP, plus the 2 library `InvoiceTypeCode` failures.
- The 4 `PoSupplierServiceTests` failures are unchanged and unrelated to TIN: `AddressReplace_AssignsLine_AndDiscardsBlank` + 3 `GlCode_*`, all "Validation failed." from stale phone/GL-code expectations.
- `SaEInvoiceSelfBilledPayloadTests` 11/11 green, including the positive `Customer.TinNo` assertion and the negative `Buyer.Tin` one — so the existing `DB → Buyer.TIN` coverage still holds.

**Deviation found while testing (harness, not product)**
The `TinNo_RoundTrips_OnCreateAndEdit` UPDATE leg initially failed with `Concurrency token is missing. Reload and try again.` — not a TIN defect. `AppDbContext.OnModelCreating` forces `RowVersion` to `ValueGenerated.Never` for SQLite, so a **service-inserted** row reads back with an empty concurrency token and the edit path refuses it (the same reason the fixture's seed rows carry an explicit `RowVersion`). Resolved with a local `StampRowVersionAsync` helper that stamps a token after the create, which is the documented house workaround. **Any future create→edit round-trip test must do this.**

**Acceptance gate (pending — nothing is production-ready until all three close)**
1. Browser smoke of the new field (new / edit / view / copy).
2. The end-to-end acceptance run in *Verification* step 5 against real vendor data: TIN entered on the supplier
   screen → persisted → self-billed e-Invoice resubmitted → the vendor-identity error key clears. **Mandatory,
   not a nice-to-have** — it is the only evidence that the business requirement is met.
3. Live DB verification of `IvMSCode(CodeType='STATE')` values against `LhdnCodeLookup.StateCodes`, run in the
   same acceptance session — otherwise a `Buyer.State` refusal gets misread as a TIN problem.

**Direction dependency (read before quoting the `Buyer.*` keys)**
`Buyer.Tin` is the key **today**, while the vendor occupies the payload's `Customer` block. The companion plan
`plans/plan-selfBilledPartyReversal.prompt.md` (vendor → `AccountingSupplierParty`) re-keys every
vendor-identity error from `Buyer.*` to `Supplier.*` and additionally makes the vendor's MSIC code and business
description mandatory. When that lands:
- the acceptance run must read "the vendor-identity error key clears", not `Buyer.Tin` specifically;
- the negative test at `SaEInvoiceSelfBilledPayloadTests.cs` L146-162 is re-keyed by *that* change, not this one.
  "Both must stay green and unchanged by this work" still holds for this TIN change.
