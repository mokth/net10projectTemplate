# Plan: E-Invoice — a blank item tax resolves to LHDN tax type `06` without blocking the e-Invoice validation gate

Most of this is already built, and the plan now also answers *"can `06` be an appsetting?"* — see Phase 4.

`SaEInvoiceService.ResolveTaxType` (`:2847-2868`) already returns LHDN tax type **`06`** whenever the line's tax group is null, empty or whitespace, unknown, or its `SaTaxGroup.TaxType` is blank — on both the invoice (`:2559`) and CN/DN (`:2678`) paths — with `EInvoiceDocumentMapper` (`:173`) as a second net. The only thing still refusing an empty item tax is one rule in `EInvoiceValidator.ValidateLines`, which fails the whole submit before the payload is generated. Removing that rule is the change; no new defaulting code is needed.

**New finding (2026-09-22) — `06` is not an arbitrary ERP choice.** `ErpWeb.EInvoiceLib/GenerateDoc/GenerateDocHelper.getHeaderTaxTotal` writes `TaxCategory/ID = "06"` **hard-coded** in its zero-total-tax branch (`:~711`), while the line branch writes our resolved `itemTax.TaxType` (`:~890`). A *configurable* default other than `06` would therefore leave the document header saying `06` and the lines saying something else on a zero-tax document. Phase 4 carries the consequence.

**Scope wording.** "Without blocking" means the **e-Invoice validation gate** only. The invoice SAVE/POST commercial tax gates are deliberately untouched (Further Consideration 1).

**Steps**

1. *(Phase 0 — diagnostic, 5 min)* Reproduce with a blank line tax group and click **Validate** on the e-Invoice panel; record the exact `Line{n}.<key>` message. If it reads `Line1.TaxType` → this plan is complete. If it reads `Line1.Classification` or "Classification is missing on line N" → stop, the fix is a classification default instead (see Further Consideration 3).
2. *(Phase 1, the only production change)* In `ErpWeb.Core/EInvoice/EInvoiceValidator.cs`, delete the blank-`TaxType` block inside `ValidateLines` (`:165-168`). Keep every other rule — `Qty`, `ClassificationCode`, `Uom`, `TaxPercent`, `AmountExclTax`, `DiscountAmount` — untouched. Steps 3-4 already guarantee a code, so this cannot produce an empty `TaxCategory/ID`.
3. *(parallel with 2)* Fix the stale doc comment on `EInvoiceSourceLine.TaxType` in `ErpWeb.Core/EInvoice/EInvoiceSourceDocument.cs:82` — it says "= the ERP tax group code", but the value is the **translated LHDN tax type**.
4. *(no change, for the record)* `SaEInvoiceService.ResolveTaxType`, `EInvoiceDocumentMapper.Map`, `LhdnDefaults.TaxType`, `SaSalesRefService:2058` and `SaTaxGroupList.razor.cs:163` stay as they are.
5. *(Phase 2 — helper, depends on nothing)* In `ErpWeb.Tests/SaEInvoiceLhdnCodeResolutionTests.cs`, give the existing `SourceWithCodes(uom, taxType, customerPhone)` helper one more optional parameter, `string? classification = "022"` — it hard-codes `ClassificationCode = "022"` today, so a blank-classification case is otherwise inexpressible. Nothing else about the helper changes.
6. *(Phase 2 — tests, depends on 2)* Add to the same file:
   - `Validator_accepts_every_blank_form_of_line_tax` — `[Theory]` over `null`, `""`, `"   "`: `new EInvoiceValidator().Validate(SourceWithCodes(uom: "C62", taxType: value), Supplier())` → `IsValid`, and no `Line1.TaxType` key. This is the pin: it fails before step 2 and passes after.
   - `Validator_still_requires_classification_when_the_tax_is_blank` — blank tax **and** `classification: null` → invalid, `Line1.Classification` present, `Line1.TaxType` **absent**. Proves only the tax rule was removed.
   - `Invoice_with_a_blank_line_tax_group_submits_with_tax_type_06` — seed with `EInvoiceTestHost.SeedInvoiceAsync()`, then clear **only** the line's `TaxGrCode` and zero the tax so the document stays self-consistent (line `TaxAmt = 0`, header `Taxes = 0`, `TotAmnt = 100`); `SubmitAsync` succeeds; then assert BOTH layers: (i) the submitted `DocumentHeader` line carries `TaxType == "06"`, and (ii) the **generated** UBL structure produced by the library's own public generator — `GenerateDocHelper.getInvoiceLine(header)` → single `InvoiceLine` → `TaxTotal[0].TaxSubtotal[0].TaxCategory[0].ID[0]._ == "06"`, i.e. the value that actually reaches `TaxCategory/ID`. The classes live in `ErpWeb.EInvoiceLib/Document/InvoiceModel.cs` (`TaxCategory:795`, `TaxSubtotal:850`, `TaxTotal:858`); add `using ErpWeb.EInvoiceLib.GenerateDoc;` and confirm the exact member path by compiling.
   - CN mirror via `SeedCreditNoteAsync` + `cdn.Details[].TaxGroup`.
   - Do **not** use `SeedInvoiceAsync(validLines: false)` for these — it nulls `Classification` as well and would trip the other rule.
   - The "tax group code present but unknown" case needs no new test: `Invoice_lines_fall_back_to_H87_and_06_when_the_masters_have_no_LHDN_mapping` already pins it.
7. *(Phase 3)* Build `ErpWeb.Core`, run the test project unpiped, confirm the new tests pass and the count moves while the same 13 pre-existing `SaCust`/`PoSupplier` failures remain; revert step 2 locally to confirm the new validator tests fail.
8. *(Phase 3, optional)* Manual smoke against the MyInvois sandbox: a blank item tax now reaches submission with `06`, instead of the panel reporting "Tax type is required (item …)".
9. *(Phase 4 — separate, opt-in)* Make the default configurable. Do not start before Phase 3 is green.

**Phase 4 — can `06` be an appsetting?** Yes, the registry can carry it; the constraint is the new finding above. **(4B, recommended) Do not add it now:** `06` is LHDN's "Not Applicable" value *and* the code the generator itself writes for a zero-tax header (`GenerateDocHelper.cs:~711`), so a different value has no established business meaning and needs the library change below to stay internally consistent. **(4A, only when a business owner names a code):** add `AppSettingModules.EInvoice = "EINVOICE"` (plus an entry in `All` and a `Describe` label) and a definition `(EInvoice, "DEFAULT_TAX_TYPE", AppSettingType.Text, AppSettingScope.Global | AppSettingScope.Company, DefaultValue: LhdnDefaults.TaxType, Description: "LHDN tax type used when an item line has no tax group.")`. `Text`, not `Token`, because the LHDN tax-type family is mastered in `IvMSCode(CodeType='TAX')` (`SaSalesRefService.ListTaxTypesForAssignmentAsync:1906`; seed `scripts/init-sales-masters.sql:341`) — a static `AllowedTokens` list would be a second authority. Read it in `SaEInvoiceService.ResolveTaxType` through a new `IAppSettingService` ctor parameter (`AppSettingScope.Company` + company code, mirroring `SaQtService.ResolveQuoteValidityDaysAsync`), falling back to `LhdnDefaults.TaxType` when the read fails, is blank, or names a code absent from `IvMSCode(TAX)`. Pass the resolved value into `EInvoiceDocumentMapper.Map(..., string? fallbackTaxType = null)`. **Mandatory companion change:** parameterise the library's hard-coded zero-tax subtotal (an additive `DocumentHeader` property consumed at `GenerateDocHelper.cs:~711`) — without it the header and the lines can disagree. Tests: add a `FakeAppSettingService` property to `EInvoiceTestHost` and pass it in `CreateService()` — the only non-DI construction site; DI resolves the new ctor parameter automatically (`CoreServiceCollectionExtensions.cs:99`, `EInvoiceServiceCollectionExtensions.cs:49`). `FakeAppSettingService.With(...)` sets a value and its fail-every-read default pins "unconfigured == `06`". Update `docs/app-settings.md`; the admin screen picks the new module tab up from `AppSettingModules.All` with no UI change.

**Relevant files**
- `ErpWeb.Core/EInvoice/EInvoiceValidator.cs` — remove the blank-`TaxType` rule at `:165-168` in `ValidateLines`.
- `ErpWeb.Core/EInvoice/EInvoiceSourceDocument.cs` — correct the `TaxType` doc comment at `:82`.
- `ErpWeb.Tests/SaEInvoiceLhdnCodeResolutionTests.cs` — the helper parameter plus four tests; reuse `SourceWithCodes`, `Supplier()`, `EInvoiceTestHost`.
- `ErpWeb.EInvoiceLib/GenerateDoc/GenerateDocHelper.cs` — evidence for the payload assertion: `getHeaderTaxTotal` hard-codes `TaxCategory/ID = "06"` for a zero total tax (`:~711`), `getInvoiceLine` writes `itemTax.TaxType` (`:~890`). Its public statics are what the generated-document test drives. Only Phase 4A changes this file.
- `ErpWeb.Core/EInvoice/SaEInvoiceService.cs` — reference only: `ResolveTaxType` (`:2847-2868`), invoice call (`:2559`), CN/DN call (`:2678`), submit gates (`:1717`, `:1751`).
- `ErpWeb.Core/EInvoice/LhdnDefaults.cs:14` and `EInvoiceDocumentMapper.cs:173` — reference only: the two defaults that make the removal safe.
- `ErpWeb.Tests/EInvoiceTestHost.cs` — `BuildInvoice`/`SeedInvoiceAsync`/`CreateService`; also the only non-DI `SaEInvoiceService` construction site, which is what Phase 4A has to extend.
- Phase 4A only: `ErpWeb.Core/Settings/AppSettingCatalogue.cs`, `ErpWeb.Core/Settings/AppSettingModules.cs`, `ErpWeb.Core/Settings/IAppSettingService.cs`, `ErpWeb.Tests/FakeAppSettingService.cs`, `docs/app-settings.md`.

**Verification**
1. `dotnet build ErpWeb.Core/ErpWeb.Core.csproj` → 0 errors.
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` unpiped → new tests pass; failures stay at the known 13 (`SaCustServiceTests` 9 + `PoSupplierServiceTests` 4).
3. Revert step 2 in a scratch copy and confirm `Validator_accepts_every_blank_form_of_line_tax` fails — otherwise the pin is not pinning.
4. The **generated-document** assertion (step 6c) is the proof that removing the rule cannot produce an empty `TaxCategory/ID`; a pass on the internal `TaxType` alone is not accepted as evidence.
5. Confirm no `ErpWeb.UI` file was touched, so the razor build is not required for this change.
6. Phase 4A only: with the fake unconfigured the payload is still `06`; with a value configured, that code appears in **both** the line and the header subtotal.
7. Manual smoke (optional, needs browser + MyInvois sandbox) — blank line tax submits as `06`.

**Decisions**
- Scope is the **e-Invoice validation gate only**; the invoice SAVE and POST commercial tax gates are deliberately untouched.
- "Does not block" is deliberately narrower than "a blank tax bypasses validation": `ClassificationCode`, `Uom`, `Qty`, `TaxPercent` and the amount checks all still refuse a bad document.
- No new defaulting layer is added — `ResolveTaxType` plus the mapper already make a blank payload code impossible, so the payload never carries blank while the ERP boundary tolerates blank.
- `ClassificationCode` stays a hard requirement: `ErpWeb.EInvoiceLib/GenerateDoc/GenerateDocHelper.cs` throws on a blank classification code, and it does **not** throw on a blank tax code — which is exactly why the `06` default has to be the ERP's job.
- `06` remains the code default (`LhdnDefaults.TaxType`) and is treated as LHDN's "Not Applicable" value, which is also what the library hard-codes for a zero-tax header — a standards-aligned value, not a preference.
- Making it configurable is a **separate, opt-in change** (Phase 4) with a mandatory library-side companion; it is not required for this fix.
- `TaxPercent is null` handling is left alone: a blank tax group resolves to `0d`, not null, so that rule is unaffected.
- No test anywhere asserts the `Line{n}.TaxType` message (verified by grep), so removing the rule cannot break the suite.

**Further Considerations**
1. **Do you also want the invoice-level gates relaxed?** Today a *taxable* customer with a blank line **and** header tax group cannot be saved (`SaInvoiceService.cs:2101`), a document with tax and no GL-mapped group cannot be posted (`:3163-3179`), and the invoice screen shows "Tax group is required" (`SaInvoice.razor.cs:218/235/273`). Option A: leave them (recommended — they guard the GL posting). Option B: relax only the POST-time "Tax GL code is required" when the company has e-Invoice enabled. Option C: relax all three. **Recommendation: A** — keep ERP tax validation (accounting/GL consistency) separate from e-Invoice validation (which can tolerate a blank ERP tax group precisely because it resolves one).
2. **Should the `06` substitution be visible?** Today it is a Serilog warning only. Option A: keep log-only (recommended). Option B: show a non-blocking note in the e-invoice panel naming the lines that were defaulted. Either way it pairs with Phase 4A, where the panel could name both the line and the code used.
3. **If Phase 0 shows a classification message instead**, step 2 is still correct but insufficient — the real fix becomes a classification default, which is a different decision (LHDN `022` vs the item master's classification), so capture the exact message first.
4. **The appsetting question** — answered as Phase 4: the registry *can* carry it, but `06` is also the generator's own zero-tax code, so 4B (do not add it) is recommended and 4A (add it) must include the library companion change rather than deferring it.

**Test matrix**

| Scenario | Expected |
| --- | --- |
| Normal tax group | unchanged — existing tests |
| Blank `TaxGrCode` (`null`, `""`, `"   "`) | e-Invoice validation passes; payload tax type `06` |
| Blank `TaxGrCode` in the **generated** document | `TaxCategory/ID == "06"` |
| Unknown tax group | existing resolver fallback preserved (already pinned, no new test) |
| Blank tax + valid classification | passes |
| Blank tax + blank classification | **still fails** on `Classification` |
| CN blank tax group | payload tax type `06` |
| Unrelated validation errors (address, buyer identity, phone) | still fail |
| The 13 pre-existing `SaCust`/`PoSupplier` failures | remain the baseline |

## Status — IMPLEMENTED 2026-09-22 (Phases 0–3 complete; Phase 4 still open)

**The plan was incomplete: there were TWO blockers, not one.** The Phase 2 run found the second.

1. `EInvoiceValidator.ValidateLines` refused a blank `line.TaxType` → removed, as planned.
2. **Missed by the plan:** `SaEInvoiceService.ResolveTaxPercent` returned `null` for a blank tax group
   code, and the *same* validator method refuses `line.TaxPercent is null` with
   **"Tax percentage cannot be negative"** — so the invoice and credit-note end-to-end tests still failed
   after (1). Fixed by returning `0d`: the exact counterpart of the tax type defaulting to `06`
   ("Not Applicable"), and what an unknown (non-blank) code already did. The validator's percent rule was
   left intact, so a genuinely negative percentage is still refused.

Changed:
- `ErpWeb.Core/EInvoice/EInvoiceValidator.cs` — blank-`TaxType` rule deleted, with a comment naming the two
  defaulting layers that make it safe.
- `ErpWeb.Core/EInvoice/SaEInvoiceService.cs` — `ResolveTaxPercent` returns `0d` for a blank tax group code.
- `ErpWeb.Core/EInvoice/EInvoiceSourceDocument.cs` — `TaxType` doc comment corrected.
- `ErpWeb.Tests/SaEInvoiceLhdnCodeResolutionTests.cs` — `SourceWithCodes` gained the `classification`
  parameter; six new tests; helpers `BlankInvoiceLineTaxAsync`, `BlankCdnLineTaxAsync`,
  `GeneratedLineTaxCode`.

Evidence:
- **The pin is real:** restoring the `TaxType` rule turns `Validator_accepts_every_blank_form_of_line_tax`
  red **3/3**; removing it is green.
- `SaEInvoiceLhdnCodeResolutionTests`: **24 passed / 0 failed**, including both end-to-end tests that assert
  `TaxCategory/ID == "06"` on the library-GENERATED line (not just the ERP-side `TaxType`).
- Full suite: **1892 total / 1879 passed / 13 failed / 0 skipped** — +6 new tests and exactly the same 13
  pre-existing `SaCust`(9) + `PoSupplier`(4) GL-code/phone failures. No regressions.
- Note for reuse: `SourceWithCodes` needs a valid `customerPhone`, or the unrelated `Buyer.Phone` rule fails
  the source the test builds.

**Still open: Phase 4 only** (4B recommended — do not add the setting; `06` is also the generator's own
zero-tax code). Nothing else from the plan is outstanding.
