# Plan: E.164 phone validation + normalization for e-Invoice

## TL;DR

Add one shared normalizer (`PhoneNumberFormat`), gate the master-data saves that own a phone field, and
canonicalize at the e-Invoice boundary so legacy and transaction-snapshot values still submit clean.
One private classifier behind four public members, one rule set, no schema change. The e-Invoice boundary
lands first because it is the only layer that actually changes what MyInvois receives.

## Current state (verified)

- Entry services only trim (`NullIfWhiteSpace`) or length-check; **no phone format rule exists anywhere**.
- `CompanyService.ValidateCompany` — no phone rule; `MapNewEntity` L615 / `ApplyEditableFields` L656 trim only.
- `SaCustService.ValidateModel` — no phone rule. `ReplaceChildrenAsync` L449 writes `SaCustAdd.Tel`
  **raw — not even trimmed**. Contacts (L462+) written raw too.
- `PoSupplierService.ValidateModel` — no phone rule; address rows do trim (L513).
- `SaSalesRefService` — `ValidateOptionalLength(..., 50)` only (Tel ~L1619, Mobile ~L1620/1655).
- `EInvoiceValidator.ValidateSupplier` / `ValidateBuyer` — **presence only** (L50 / L85).
- `EInvoiceDocumentMapper.Map` — copies `supplier.Phone` (L121) and `document.CustomerPhone` (L139) verbatim.
- `GenerateDocHelper` supplier/buyer party builders — only an `IsNullOrEmpty` check (L37/213, L301/459).
- Exactly **one** production `_mapper.Map` call: `SaEInvoiceService.cs:1206`, always after
  `BuildSourceAsync` (L1918) ran `_validator.Validate` (L2144). The validator is the single gate.
- `SaEInvoiceService.ValidateAsync` (L81) is the UI pre-flight action → reuses the same validator.
- Buyer phone comes from the document snapshot (`invoice.InvTel`, `cdn.Tel`) and is frozen
  (`ApplyBuyerFreeze`) — master-data fixes do NOT retro-fix a posted invoice.
- Columns: `SaCust.Tel`, `SaCustAdd.Tel`, `PoSupplier.Tel` etc. `HasMaxLength(50)`; `Company.Phone`
  UI `maxlength=30`. E.164 max is 16 chars → no schema change.

## Design

### Layer 1 — `ErpWeb.Core/Services/PhoneNumberFormat.cs` (new)

`ErpWeb.Core.Services` namespace, alongside `ValidationMessageFormat.cs` (same cross-module utility role).
Style: static class, `Try*` / `Normalize` idiom of `EInvoiceRegistrationTypes` / `LhdnCodeLookup`.

Public surface (4 members + 1 const). `Classify` is private and is the single source of truth; every
public member reads from it and none of them re-implements a rule.

- `MalaysiaCountryCode` — `public const string` = `"60"`. This is the **only** place the default country
  code is written. It is the default value of `Normalize`'s parameter, so no `"60"` literal ever appears
  in a service.
- `Normalize(string? value, string defaultCountryCode = MalaysiaCountryCode) -> string?` — canonical
  `+<digits>`, or null when blank/unusable. READ + SUBMIT use.
- `ToStored(string? value) -> string?` — non-lossy write helper. Exact semantics in
  "Preservation semantics" below. ALL master-data write paths use this.
- `Validate(string? value) -> string?` — **FORMAT check only.** null when ok/blank, else a field-ready
  message. Requiredness deliberately stays with the existing `Require(...)` calls so a phone field never
  produces two competing messages.
- `IsE164(string? value) -> bool` — true iff `value` is already canonical E.164, i.e. iff
  `Normalize(value) == value`. Deliberately **false for null and blank**: it answers "is this a valid
  E.164 value?", not "does this field need filling in?" (that is `Validate(null) == null`).
  `IsE164(null)` = false, `IsE164("")` = false, `IsE164("+60398765432")` = true,
  `IsE164("03-9876 5432")` = false, `IsE164("60398765432")` = false.

#### Recognition rules (order matters)

These decide (a) whether the value is a phone number at all and (b) what its canonical E.164 form is.
They do **not** decide what is stored for a rejected value — see "Preservation semantics" next.

1. Blank / null → blank.
2. Placeholders (case-insensitive) → blank: `NA`, `N/A`, `NIL`, `NULL`, `TBA`, `TBD`, `-`, `--`, `.`,
   `0`, `00`, `X`, `XX`. **This is an ERP business-data convention, not an E.164 rule** — E.164 says
   nothing about placeholders. Say so in the XML doc so nobody later reads it as spec-derived.
3. Reject if it contains a letter (catches `A-phone`, `ext 12`), `,` `;` `/` `\` (multiple numbers), or
   any `+` outside index 0.
4. Strip spaces / tabs / NBSP / `-` / `.` / `(` / `)` — **recognition only**, so that `03-9876 5432`
   can be recognised. This stripping is never applied to a rejected value on the way to storage.
5. Prefix resolution (default country code = `MalaysiaCountryCode`):
   - leading `+` → keep digits
   - leading `00` → `+` + rest
   - leading `60` **and ≥ 10 digits** → `+` + value (already country-coded). The `≥ 10` guard is what
     keeps a malformed 9-digit `601234567` out — it fails this branch and falls through to the
     ambiguous branch below.
   - leading `0` → `+60` + value[1..] (drop the MY trunk `0`)
   - leading `1`-`9` → **AMBIGUOUS** → reject ("missing country code"). This is what rejects
     `601234567`, `111` and `9990000`.
6. Digit count must be 8..15 (`MaxDigits = 15` per E.164; `MinDigits = 8` is a practical floor, not an
   E.164 rule).
7. Canonical output is `"+" + digits`.

Worked examples:

| input | output |
|---|---|
| `" 03-9876 5432 "` | `+60398765432` |
| `"+60 3-9876 5432"` | `+60398765432` |
| `"60398765432"` | `+60398765432` |
| `"0060398765432"` | `+60398765432` |
| `"011-1234 5678"` | `+601112345678` |
| `"NA"` / `"0"` / `""` | blank (null) |
| `"111"`, `"999-0000"` | reject (ambiguous) |
| `"A-phone"` | reject (letters) |
| `"03-1234 5678 / 03-8765 4321"` | reject (multiple) |
| `"1234567890123456"` | reject (>15 digits) |
| `"++60398765432"` | reject (`+` not at index 0) |
| `"+60+398765432"` | reject (`+` not at index 0) |
| `"03+98765432"` | reject (`+` not at index 0) |
| `"+"` | reject (no digits) |
| `"601234567"` | reject (ambiguous — 9 digits fails the `60…` guard) |

Every "reject" row stores via `ToStored` as the outer-trimmed original, per the preservation table below.

#### Preservation semantics — `ToStored`

`ToStored` is **trim-only preserving**: outer whitespace trim, then either the canonical E.164 form (when
recognised) or the user's original value. "Cleaned" in this plan means **outer whitespace trim and
nothing else** — it does NOT mean the step-4 separator stripping. It never removes punctuation, never
reorders, never blanks a field the user typed.

| input | `Classify` | `Normalize` | `ToStored` | `Validate` |
|---|---|---|---|---|
| `" 03-9876 5432 "` | Recognized | `+60398765432` | `+60398765432` | null |
| `"+60398765432"` | Recognized | `+60398765432` | `+60398765432` | null |
| `"NA"` | Placeholder | null | null | null |
| `"A-phone"` | Invalid | null | `"A-phone"` | message |
| `"03-1234 / 04-5678"` | Invalid | null | `"03-1234 / 04-5678"` | message |
| `"  999-0000  "` | Invalid | null | `"999-0000"` (outer trim only) | message |
| `null`, `""`, `"   "` | Blank | null | null | null |

The one deliberate exception to non-lossiness is the placeholder row: a placeholder means "no value", so
it becomes null rather than being preserved. That is intentional — persisting `"NA"` only to emit it to
MyInvois as a telephone number is worse — and must be documented on `ToStored` itself.

Worked examples:

| input | output |
|---|---|
| `" 03-9876 5432 "` | `+60398765432` |
| `"+60 3-9876 5432"` | `+60398765432` |
| `"60398765432"` | `+60398765432` |
| `"0060398765432"` | `+60398765432` |
| `"011-1234 5678"` | `+601112345678` |
| `"NA"` / `"0"` / `""` | blank (null) |
| `"111"`, `"999-0000"` | reject (ambiguous) |
| `"A-phone"` | reject (letters) |
| `"03-1234 5678 / 03-8765 4321"` | reject (multiple) |
| `"1234567890123456"` | reject (>15 digits) |
| `"++60398765432"` | reject (`+` not at index 0) |
| `"+60+398765432"` | reject (`+` not at index 0) |
| `"03+98765432"` | reject (`+` not at index 0) |
| `"+"` | reject (no digits) |
| `"601234567"` | reject (ambiguous — 9 digits fails the `60…` guard) |

Every "reject" row above stores via `ToStored` as the outer-trimmed original, per the preservation table.

#### What this does NOT validate

`PhoneNumberFormat` validates **E.164 structure only**: a `+`, then an 8-15 digit numeric string. It does
NOT validate Malaysian numbering allocation or prefix validity, and it knows nothing about operator
mobile / fixed-line ranges. `+60123456789` passing `Validate` means "structurally E.164", not "a number
actually allocated in Malaysia". Keeping allocation checks out of here is deliberate: MyInvois stays the
authority on reachability, and a numbering-plan change never becomes an ERP code change.

### Layer 2 — e-Invoice boundary (highest value; do this FIRST)

- `EInvoiceDocumentMapper.cs` (L121, L139): wrap both `PhoneNo` assignments in
  `PhoneNumberFormat.Normalize(x) ?? x`. Blank stays blank so the existing `?? ""` + `IsNullOrEmpty`
  guard in `GenerateDocHelper` is unchanged; invalid stays verbatim (the validator is the gate).
  Add a comment: normalization is formatting, not inference — consistent with the class contract.
- `EInvoiceValidator.cs`:
  - After the `Supplier.Phone` `Require` (L50): if non-blank and `Normalize` is null →
    `errors["Supplier.Phone"] = "...must follow the E.164 format (for example +60398765432)."`
  - After the `Buyer.Phone` `Require` (L85): same, key `Buyer.Phone`, with a message that tells the user
    to correct the customer profile **and re-open/re-save the invoice** (the phone is a frozen document
    snapshot, unlike the live-read e-Invoice identity).
  - Reuse the existing field keys so `ValidationMessageFormat` / the validation panel pick them up.

### Layer 3 — master-data save gates (the user's "validate before save")

Mirror the `RegType` precedent in `SaCustService.AddRegistrationTypeError`: **block** a new/changed
invalid value, **tolerate** an unchanged legacy value on edit (do not strand historical rows).

- `CompanyService.cs` (`ValidateCompany` ~L566): add phone rule. Write: `MapNewEntity` L615 and
  `ApplyEditableFields` L656 → `ToStored(source.Phone)`. `Fax` is deliberately left on the existing
  `NullIfWhiteSpace` trim — `ToStored` is NOT applied to fax anywhere (it would re-format a printed fax
  number).
- `SaCustService.cs`:
  - `ValidateModel` (L624): add `AddPhoneError` for `Tel` (+ `ShipTel` when `!UsesMainShip(model)`), and
    `errors[$"Addresses[{i}].Tel"]` inside the existing address loop (L814).
  - `ApplyHeaderFields` L574 → `ToStored(model.Tel)`; normalize once into a local and reuse it in
    `ApplyInvoiceShipment` (L511 `InvTel`, L527 `ShipTel`) instead of re-reading `model.Tel`.
  - `ApplyContactLine1` L495 + `ReplaceChildrenAsync` L449/L462+ → `ToStored(...)` (fixes the
    un-trimmed raw write). Contacts are NOT submitted to LHDN → clean, never block.
- `PoSupplierService.cs` (`ValidateModel` L629): add `Tel` rule; write L559 → `ToStored`; address rows
  L513 → `ToStored`; add an address-loop phone error mirroring SaCust. `Fax` → existing trim only (see
  the `CompanyService` note: fax never goes through `ToStored`).
- `SaSalesRefService.cs`: keep the length checks, add a `Tel` format rule (~L1619) and a `Mobile` format
  rule (~L1620) + `ToStored` at ~L1655.

### Explicitly OUT of scope

- Blocking validation on TRANSACTION tel fields (`SaInvoice.InvTel`, `SaSo`, `SaDo`, `SaQt`, `SaCdn.Tel`,
  `PoOrder.VendTel`). They are snapshots/overrides; Layer 2 canonicalizes them at submission, which is
  what actually matters. Adding save-time blocks there risks historical documents failing re-save.
- `Fax` / `ShipFax` / `InvFax` / `ContactFax` beyond whitespace cleaning (LHDN submits only Telephone).
- `UserLogin.mobileno` (login/2FA, not documents) and `PoMasterRef.MobileNo`.

## Steps

1. **Phase 1:** add `ErpWeb.Core/Services/PhoneNumberFormat.cs` + new
   `ErpWeb.Tests/PhoneNumberFormatTests.cs`. Table-driven xUnit `[Theory]` cases for:
   - **accept** — every row of the worked-examples table, asserting the exact canonical output.
   - **placeholder** — `NA` / `NIL` / `-` / `0` / `00` / `X` → `Normalize` null and `ToStored` null.
   - **reject** — letters, extensions, multiple numbers, >15 digits, ambiguous 1-9 leading, and the
     malformed-`+` set (`++60…`, `+60+…`, `03+…`, bare `+`).
   - **`ToStored` preservation** — an invalid value round-trips character-for-character after an outer
     whitespace trim; a placeholder becomes null (the documented exception).
   - **`IsE164` semantics** — `null` and `""` are false; a canonical value is true; a raw local format
     and a bare `60398765432` are false.
   - **idempotency** — `Normalize(Normalize(x)) == Normalize(x)` for every accepted input, plus the
     explicit fixed point `Normalize("+60398765432") == "+60398765432"`. This guards repeated
     processing (master save → mapper → any future re-run) against format drift.
   *Blocks phases 2-4.*
2. **Phase 2:** `EInvoiceDocumentMapper.cs` normalization + `EInvoiceValidator.cs` rules. *Depends on 1.*
3. **Phase 2b:** extend `SaEInvoiceLhdnCodeResolutionTests.cs` "Mapper safety net" section with a
   canonicalization test; extend its `SourceWithCodes` / `Supplier()` helpers with optional phone params
   (defaults keep existing tests green — `Supplier()` already has `Phone = "0312345678"` → `+60312345678`).
4. **Phase 2c:** add `EInvoiceTestHost.SeedCompanyAsync(..., string? phone = null)`; add a
   `SaEInvoiceLifecycleTests` case: submit with `phone = "A-phone"` → `SaEInvoiceErrorKind.Validation` +
   `Assert.Contains("Supplier.Phone", result.ValidationErrors.Keys)` (mirror the `Supplier.Msic` test).
5. **Phase 3:** `CompanyService` → `SaCustService` → `PoSupplierService` → `SaSalesRefService`.
   *Each is independent of the others, but all depend on 1 and should land after 2.*
6. **Phase 4:** run the full suite; fix only the fixtures that genuinely save through a service with an
   invalid literal (known candidates: `SaCustServiceTests.cs` L104/123/130/517,
   `PoSupplierServiceTests.cs` L81, `PoCdnServiceTests.cs` L120, `SaDoServiceTests.cs` L229).
   Direct-EF seeds bypass validation and are unaffected. Also revisit the intent of
   `SaCustServiceTests.cs:517` (`loaded.Tel = "999-0000"`).

## Relevant files

- `ErpWeb.Core/Services/PhoneNumberFormat.cs` (new)
- `ErpWeb.Core/EInvoice/EInvoiceDocumentMapper.cs`
- `ErpWeb.Core/EInvoice/EInvoiceValidator.cs`
- `ErpWeb.Core/Security/CompanyService.cs`
- `ErpWeb.Core/Sales/SaCustService.cs`
- `ErpWeb.Core/Purchase/PoSupplierService.cs`
- `ErpWeb.Core/Sales/SaSalesRefService.cs`
- `ErpWeb.Tests/PhoneNumberFormatTests.cs` (new)
- `ErpWeb.Tests/EInvoiceTestHost.cs`
- `ErpWeb.Tests/SaEInvoiceLhdnCodeResolutionTests.cs`
- `ErpWeb.Tests/SaEInvoiceLifecycleTests.cs`

## Verification

1. `dotnet build ErpWeb.slnx` — clean, no new warnings.
2. `dotnet test ErpWeb.Tests --filter "FullyQualifiedName~PhoneNumberFormat"` — new table tests.
3. `dotnet test ErpWeb.Tests --filter "FullyQualifiedName~SaEInvoiceLhdnCodeResolution|FullyQualifiedName~SaEInvoiceLifecycle|FullyQualifiedName~SaEInvoiceBatch|FullyQualifiedName~SaEInvoiceBuyerIdentity"`.
4. `dotnet test ErpWeb.Tests` — full suite (SQLite **and** the SQL Server concurrency tests).
5. **Manual, master data:** customer entry `Tel = 03-9876 5432` → saved and re-read as `+60398765432`;
   `Tel = A-phone` → blocked with a field-keyed message on `Tel`; `Tel = NA` → saved as blank.
   Repeat on `AdminCompany` Phone and supplier entry Tel.
6. **Manual, e-Invoice:** set `Company.Phone = "111"`, run the panel's **Validate** action →
   `Supplier.Phone` error, no MyInvois call, `ERP_VALIDATION` audit row. Then set `Company.Phone` back to
   `03-1234 5678`, submit, and confirm the payload's `AccountingSupplierParty` Telephone is
   `+60312345678` with no whitespace.
7. **Manual, legacy path:** seed `SaInvoice.InvTel = " 03-9876 5432 "` directly in the DB, submit, and
   confirm the submitted buyer telephone is `+60398765432`.

## Decisions

- Validation is **fail-closed** at the e-Invoice gate (error, not a silent passthrough) but **fail-soft**
  at the mapper (normalize; never guess a value, never wipe one).
- `ToStored` is deliberately non-lossy: invalid input keeps its outer-trimmed raw text (punctuation
  intact), so a master-data write can never blank or re-format a field the user typed. Only `Validate`
  blocks. The single documented exception is the placeholder → null rule.
- `MalaysiaCountryCode` (`"60"`) is a public const and the default parameter value, so the country-code
  literal exists in exactly one place and a future multi-country supplier passes its own code explicitly.
- The helper validates E.164 **structure**, not Malaysian numbering allocation. MyInvois stays the
  authority on whether a number is real and reachable.
- `"0"` / `"00"` (and the other tokens in step 2) are **ERP business-data placeholder conventions, not
  E.164 rules** — documented as such in the XML doc so the two are never conflated later.
- `IsE164(null)` is false while `Validate(null)` is null. The pair deliberately separates "is a valid
  E.164 value" from "this field is optional".
- Unchanged legacy values are tolerated on edit (mirrors `AddRegistrationTypeError`), so an unrelated
  edit to a customer cannot be blocked by a pre-existing dirty phone.
- Trim/normalize happens at the e-Invoice boundary rather than sweeping 8 transaction services.

## Confirmed decisions (user answered)

1. **Mapper stays FAIL-SOFT** (normalize only). One gate, one message; the only production call site is
   already validator-gated. No duplicate rule in the mapper, no throw added to `GenerateDocHelper`.
2. **Transaction tel fields are DEFERRED** — no save-time sweep of SaInvoice/SaSo/SaDo/SaQt/SaCdn/
   PoOrder/PoInvoice/PoCdn. The mapper canonicalizes them at submission, and historical posted documents
   keep re-saving cleanly.
3. **`MinDigits = 8` confirmed** (practical floor, not an E.164 rule; `MaxDigits = 15` per E.164).

## Further considerations

All seven review items are now folded in: exact `ToStored` preservation semantics, `"0"`/`"00"` labelled
as ERP convention, E.164-structure-not-allocation stated explicitly, `IsE164(null/blank)` defined,
idempotency tests, malformed-`+` tests, and the centralized `MalaysiaCountryCode` const.

One residual risk to watch during Phase 4: a fixture that saves through a service with a short numeric
literal (e.g. `"111"`) will now be rejected — fix the fixture, do not loosen the rule.