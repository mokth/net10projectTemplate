# Plan: Build the whole picker surface end to end

Add a "Pick from invoice" button inside the existing line popup that seeds the line from the selected originating self-billed invoice, keeping manual key-in and the free-text item box exactly as they are today.

> **Review status (2026-09-24).** External review scored the plan **9.3/10** — direction accepted, *no architecture change*. The P1/P2 findings below are folded into the steps; the finding-by-finding mapping is in *Review disposition* at the end of this file.

**Steps**

1. **Page state** — in `PoSbCdn.razor.cs` add `OriginLines` (`List<PoSbLineDto>`), `OriginPickerVisible`, `OriginPickerLoading`, `OriginPickerError`, and `HasOrigin` (a computed `!string.IsNullOrWhiteSpace(Model.OriginSbInvNo)`). `OriginLines` is a **short-lived UI cache only**: an origin change invalidates it, and every picker open re-reads the origin invoice (load contract in step 2, invalidation in step 4).
2. **Load the invoice lines** — `OpenOriginLinePickerAsync()`, in this order:
   1. Guard on `HasOrigin` (otherwise show the "select the origin first" hint and do not open).
   2. Capture `var originNo = Model.OriginSbInvNo;` **before** awaiting, set `OriginPickerVisible = true`, `OriginPickerLoading = true`, and clear `OriginPickerError` (a previous error must never survive into a new load).
   3. Call the already-injected `SbInvoices.GetAsync(originNo)`.
   4. **Stale-response guard:** after the await, if `!string.Equals(Model.OriginSbInvNo, originNo, StringComparison.OrdinalIgnoreCase)` discard the result and return — a late response for the *previous* origin must never repopulate the cache.
   5. On success: `OriginLines = result.Document.Lines.OrderBy(x => x.Line).ToList()`.
   6. On failure: **clear `OriginLines` first**, then set `OriginPickerError` from `result.Message` — stale rows must never be visible next to an error.
   7. Always clear `OriginPickerLoading` in a `finally`.
3. **Seed the line** — `UseOriginLine(PoSbLineDto row)`:
   1. **Derive an explicit source → popup mapping before writing the method**, by reading `PoSbLineDto` (`PoSbSharedResults.cs`), `LineEdit.FromDto`, `PoSbLineRequest` and `OnPopupSave` side by side. Matching property names are not proof of matching semantics; the mapping must cover `UnitPrice`, `StdUom`, `TaxGroup`, `IsInclusive`, both discount slots **and their discount-type codes**, `Classification` and `Remarks`.
   2. Write the fields onto the **existing** `Popup` instance — never replace the object and never create a second line. The parent line popup stays open so the operator reviews/edits before `OnPopupSave`.
   3. Do **not** copy `Amount`/`NetAmount`/`TaxAmt`; `OnPopupSave` recomputes them through `PoSbCalc.ComputeLineAmounts`, the single money authority.
   4. Nullable/default handling follows `LineEdit.FromDto` / `PoSbLineRequest` exactly. The **only** new defensive default is `Qty <= 0 → 1` (because `PoSbCalc.ValidateLines` refuses a zero quantity).
   5. Close the picker as the last action (`OriginPickerVisible = false`), which also makes a repeated "Use" click impossible after the selection.
4. **Origin change behaviour** (*depends on 1*) — in `OnOriginChangedAsync`, keep the existing `Model.Currency ??= origin.Currency` inherit, then:
   - always invalidate the cache (`OriginLines = []`) — it is the async guard in step 2.4 that makes this safe against an in-flight load;
   - if `EditLines.Count > 0`, set a hint such as "Origin changed to X — N existing line(s) kept; review them against the new invoice";
   - do **not** touch `Model.CurrRate` (see *Decisions*).
5. **Line popup markup** (*depends on 1,4*) — in `PoSbCdn.razor`, wrap the "Item code" `DxTextBox` in a `div.sdoc-field-row` (the shared flex style from `SaDocPage.razor.css`) with a secondary `DxButton` "Pick from invoice", `Enabled="@HasOrigin"`, plus a `sdoc-field-hint` explaining the disabled state.
6. **Picker popup markup** (*depends on 2,3*) — add a `DxPopup` "Select invoice line" (Width 900px, `CloseOnOutsideClick="false"`, `common-popup`), mirroring `PoCdn.razor`'s source-line picker: a `DxGrid` with a "Use" command column (`CellDisplayTemplate` → `UseOriginLine(row)`) plus `Line`, `ICode`, `IDesc`, `Qty`, `UnitPrice`, `StdUom`, `TaxGroup`, `Classification`, `NetAmount`, `TaxAmt` columns, and "Close" in the footer. **State precedence is fixed and exclusive — Loading → Error → Empty → Grid** — so an error and the empty message can never render together. This is a second popup on top of the line popup, the same nesting `PoCdn` already uses.
7. **Docs** (*parallel with 1-6*) — append a short entry to `docs/einvoice-history.md` recording that the SB note entry screen can now seed a line from the origin invoice, and that provenance is still document-level only.

**Relevant files**

- `ErpWeb.UI/Purchase/Transactions/PoSbCdn.razor.cs` — `OnOriginChangedAsync` (origin hook), `OnNewLineClick`/`OnPopupSave` (popup lifecycle), `LineEdit.FromDto` (the field mapping the seeder must match), `SbInvoices` (already injected).
- `ErpWeb.UI/Purchase/Transactions/PoSbCdn.razor` — the line `DxPopup`, its "Item code" item, and the new picker popup.
- `ErpWeb.UI/Purchase/Transactions/PoCdn.razor` (lines 405-552, 620-686) and `PoCdn.razor.cs` (`OpenLinePickerAsync`, `UseInvoiceLine`) — the markup/method pattern to copy, minus the remaining-value columns.
- `ErpWeb.Core/Purchase/IPoSbInvoiceService.cs` (`GetAsync`) and `PoSbSharedResults.cs` (`PoSbLineDto`) — reused as-is.
- `ErpWeb.UI/Sales/Transactions/SaDocPage.razor.css` — `sdoc-field-row`, `sdoc-field-hint`, `sdoc-popup-hint`.
- `docs/einvoice-history.md` — release-note entry.

**Verification**

Build / automated:

1. `dotnet build ErpWeb.slnx` → zero errors/warnings.
2. `dotnet test ErpWeb.Tests` → the existing `PoSbServiceTests` coverage (CN/DN save + origin refusals) stays green; no Core change is expected, and the repo has no bUnit harness, so the component itself is smoke-verified.

Smoke matrix (route `/purchase/self-billed-credit-notes/new` unless stated otherwise):

| # | Scenario | Expected |
|---|---|---|
| 1 | Origin not yet chosen | "Pick from invoice" disabled + hint |
| 2 | Origin chosen, invoice has lines | Grid lists the invoice's lines in `Line` order |
| 3 | Origin chosen, invoice has no lines | Empty message only — no error |
| 4 | Origin load fails | Error shown **and** `OriginLines` empty — no stale rows |
| 5 | Origin changed after a successful load | Picker serves only the new invoice's lines |
| 6 | Origin changed while the load is in flight | Late response discarded (step 2.4) |
| 7 | Pick one line | Parent line popup receives the fields; picker closes; no extra line created |
| 8 | Change qty/price after picking | Operator override retained |
| 9 | Save the line | Amounts recomputed by `PoSbCalc.ComputeLineAmounts` |
| 10 | Existing lines + origin change | Lines preserved + hint shown |
| 11 | DN route `/purchase/self-billed-debit-notes/new` | Same behaviour as CN |
| 12 | `view → edit` an existing note | Existing lines load; picker works |
| 13 | e-Invoice-locked note | Line popup unreachable, so the picker cannot be reached |
| 14 | Foreign-currency origin | The note keeps its own `CurrRate` (decision below); amounts follow the note's rate |
| 15 | Persistence | `POSbCdnDetail` holds the seeded values; header `GrossAmnt`/`Taxes`/`TotAmnt` = sums of lines |

**Decisions**

- **Currency and rate.** The picker never writes `Currency` or `CurrRate`. `OnOriginChangedAsync` keeps today's `Currency ??=` inherit only; `CurrRate` stays whatever the note already has (default `1`). Detecting "the operator has not manually changed the rate" would add state for no behaviour change in this scope, so the existing behaviour is kept deliberately and covered by smoke case 14. Revisit only if FX notes become common.
- **Selection contract.** One pick → seed the open popup → close the picker → hand control back to the line popup. The picker never auto-creates, auto-appends or auto-saves a line.
- **`OriginLines` lifecycle.** Short-lived UI cache: invalidated on origin change (step 4), re-read on every picker open (step 2.2), replaced only by a response whose origin still matches (step 2.4), cleared on failure (step 2.6).
- Convenience seeder only: the picked line is **not** traceable back to the invoice line — nothing on `PoSbLineDto`/`PoSbLineRequest`/`POSbCdnDetail` records provenance, and no ceiling is enforced. This matches the recorded MyInvois requirement, which is document-level (`RefDocumentNo` + origin UUID via `PoSbOriginResolver`), not line-for-line.
- Manual key-in and the free-text item box are untouched; the picker is an accelerator only.
- Single-line pick; no bulk "copy all lines" (declined).
- Zero backend changes: no `ErpWeb.Core`, no SQL script, no migration.

**Further Considerations**

1. **Ceilings later?** Mirroring `PoCdnCalc.EvaluateSourceLineQuantity` would need an `InvLineNo` column on `POSbCdnDetail` plus a consumption query (the same shape as `PoCdnService.ListSourceLineUsageAsync`) — a separate decision and a DBA-run script.
2. **Show what is already on the note?** The picker could flag item codes already added (`EditLines`), even though duplicates are legitimate. A small nicety, not required by the chosen scope.
3. **Also inherit the origin's rate?** Resolved for this scope: **not inherited** (see *Decisions*). Revisit only if foreign-currency self-billed notes become common, in which case "has the operator touched the rate?" needs an explicit tracking flag rather than an inferred one.

---

## Review disposition (2026-09-24)

External review: **9.3/10 — ready after minor plan hardening**, no architecture change. Verdict per finding:

| Finding | Sev | Disposition | Where |
|---|---|---|---|
| Verify the exact source-line field mapping before coding | P1 | **Accepted** | Step 3.1 (read the four members side by side; explicit mapping incl. discount-type codes) |
| Currency / `CurrRate` needs a firm decision | P1 | **Accepted** — Option A (keep current behaviour, document it, test it) | *Decisions* + smoke case 14 |
| `OriginLines` caching semantics ambiguous | P1 | **Accepted** | Steps 1, 2.2-2.6, 4 (+ *Decisions* lifecycle bullet) |
| Failed load must not leave old rows visible | P1 | **Accepted** | Step 2.6 (clear rows, then set the error; clear the previous error before loading) |
| Double-click / repeated "Use" | P2 | **Accepted** | Step 3.5 (picker closes on selection, so a second "Use" is not reachable) |
| Nullable/default copying must be safe | P2 | **Accepted** | Step 3.4 (`LineEdit.FromDto` / `PoSbLineRequest` conventions; `Qty` is the only new default) |
| Stale-origin race guard | P2 | **Accepted** | Step 2.2 + 2.4 (capture the origin, discard a mismatched late response) |
| Distinguish "no lines" from failure in the UI | P2 | **Accepted** | Step 6 (exclusive precedence Loading → Error → Empty → Grid) |
| Add positive and negative origin scenarios | P2 | **Accepted** | 15-row smoke matrix (empty / error / race / FX / locked / CN / DN / edit) |

Deliberately **not** adopted in this plan (would expand scope beyond the agreed change): line-level provenance/ceilings (needs an `InvLineNo` column plus a consumption query — see *Further Considerations* 1) and bulk "copy all lines" (declined by the requester).
