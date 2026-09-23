# Plan: Self-billed documents — retire POST/ROLLBACK, gate Edit on e-Invoice state only, standardise grid row actions

**Scope:** the three self-billed e-Invoice families only — `PoSbInvoice` (SBI), `PoSbCdn` type CN (SBC) and type DN (SBD).

**Goal**
1. Drop the POST / ROLLBACK actions entirely (the ERP `NEW → POSTED` transition buys nothing: the document has no stock/AP/GL effect, and the e-Invoice lifecycle is the real workflow).
2. Make the e-Invoice state the ONLY structural edit/delete gate: a document is editable while it has not been submitted, and while it is in a fix-and-resubmit state (`INVALID`, `REJECTED`, `CANCELLED` or `FAILED`) — i.e. *not* `SUBMITTING` / `SUBMITTED` / `VALID`. The full matrix is in Phase 0.
3. Give both item grids the standard purchase-entry (PO entry) row-action column.

**Area:** `ErpWeb.Core/Purchase` (PoSb*), `ErpWeb.Core/EInvoice`, `ErpWeb.UI/Purchase/Transactions` (PoSbInvoice, PoSbCdn, PoSbInvoiceList, PoSbCdnList), `scripts/init-pobsb-menu.sql`, `ErpWeb.Tests`.

---

## Implementation status (2026-09-23) — PHASES A-E IMPLEMENTED

All phases are in the working tree. Evidence:

- `dotnet build ErpWeb.slnx` → **0 errors** (0 `error CS` / `RZ`, no host file locks this run).
- `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` → **2002 total / 1987 passed / 15 failed / 0 skipped**. The
  15 are exactly the pre-existing set: 2 × `SaEInvoiceSelfBillAndTinTests.Self_billed_types_use_the_note_and_invoice_ubl_roots`
  (the library's `EInvoiceDocumentTypeMap` emits `Invoice-2` for 12/13) + 9 `SaCustServiceTests` + 4
  `PoSupplierServiceTests` (the GL-code/phone WIP). Zero failures touch this change.
- `PoSbServiceTests` alone: **36 passed / 0 failed** (was 25 — the ERP-status tests were replaced by the
  lifecycle theory, the race test, the delete tests and the origin-picker filter test).
- `--filter "…PoSbServiceTests|…EInvoice"` → 337 total, 335 passed, the same 2 library failures.
- Static audit (verification step 4): the only self-billed hits left for `PoSbStatuses.Posted` are the two
  list status-filter options (intentional legacy reads) and the new tests; every other `PostAsync` /
  `CanPost` / `CanRollback` hit belongs to inventory, `PoInvoice`/`PoCdn` or sales and is untouched.
- Not done (cannot be done from here): the manual browser smoke test, and applying the amended
  `scripts/init-pobsb-menu.sql` to the databases — it is a manual DBA script, never run at startup.
- Decisions taken as planned: delete = lock-only (D8), `CANCELLED`/`REJECTED` stay editable (see
  *Further considerations*), the `POSTED` filter option stays, and the grids are keyed by `UiKey`.

This plan is now a completed record; the live backlog items are the manual smoke test, the SQL apply, and
the open business question about `CANCELLED`/`REJECTED` terminality.

---

## Confirmed decisions

- **D1 — the ERP `NEW/POSTED` dimension is RETIRED for self-billed.** Nothing writes `POSTED` any more. The `Status` column stays (it is `NOT NULL` and a legacy row may hold `POSTED`), and `PoSbStatuses.Posted` stays as the single definition of that legacy value, still referenced by the list status-filter option.
- **D2 — submission to MyInvois must no longer require POSTED for SBI/SBC/SBD.** `SaEInvoiceService.IsSourceDocumentPosted` is renamed `IsSourceDocumentSubmittable` and the two self-billed branches are deleted so the `_ => true` default applies. Sales `SaInvoice` / `SaCdn` keep the POSTED-before-submit rule **untouched**.
- **D3 — the only structural gate is `PoSbCalc.IsEInvoiceLocked(IrbmStatus)`** (= `EInvoiceStatuses.IsLocked` = `{SUBMITTING, SUBMITTED, VALID}`). Editable/deleteable = *not locked*, which yields exactly the requested rule: `NEW`, `INVALID`, `REJECTED`, `CANCELLED`, `FAILED` are editable; `SUBMITTING`, `SUBMITTED`, `VALID` are not. No new helper is introduced — this is one expression used at every gate.
- **D4 — `PostAsync` / `RollbackAsync` are DELETED** from both service interfaces, both service classes, both entry pages and both list pages (not merely hidden — hiding leaves dead code).
- **D5 — out of scope:** the `sdoc-items__head` "Add line" button wrapper (PoOrder's `poorder-item-actions` is *scoped* CSS and cannot be reused from another page) and the line popup's missing discount slots.
- **D6 — structural mutation and submission are SEPARATE gates.** `IsEInvoiceLocked` governs Edit/Delete only. Submit / Retry / Recover / Refresh / Cancel keep their own shipped predicates (`SaEInvoiceStatusView`, `SaEInvoiceResults.cs:265-274`). Submit eligibility must never be inferred from "not locked".
- **D7 — the service layer is authoritative.** UI `CanEdit` / `CanDelete` / button enablement is presentation only. `UpdateAsync` and `DeleteAsync` re-read the header inside their own transaction and refuse from the *current* database state, so a submission that lands between "page loaded" and "Save pressed" is rejected. `RowVersion` additionally covers concurrent *saves*; it does NOT cover the submit-vs-save race, because the e-Invoice claim does not bump the row version — the lock check is that guard.
- **D8 — delete uses the SAME predicate as edit** (lock-only), so a legacy `POSTED` row is deletable. This is a deliberate data-retention decision, not an oversight: the ERP status carries no accounting meaning for these documents.
- **D9 — legacy `POSTED` rows stay readable** and are editable/deletable; no new code writes `POSTED`; nothing transitions `Status` back to `NEW` any more. Editing never writes `Status` either (`ApplyHeader` touches vendor/currency/rate/tax group/remarks only), so a legacy row keeps its value across an edit.
- **D10 — authorization is unchanged by the grant removal.** The permissions that now control each operation: **new** = `ADD`, **edit** = `EDIT`, **delete** = `DELETE`, **submit/retry** = `SUBMIT`, **cancel** = `CANCEL`, all on the `PO_SB_INVOICE` / `PO_SB_CN` / `PO_SB_DN` menu, each re-checked inside the service. `POST`/`ROLLBACK` were never the gate for any of them, so removing the two grants creates no authorization gap.
- **D11 — one change covers every submission path.** Verified: `IsSourceDocumentPosted` has exactly ONE call site (`SubmitBatchAsync`, `SaEInvoiceService.cs:1965`), reached by the single-document path (`SubmitAsync`/`RetryAsync`, L1888) and the batch path (`SubmitManyAsync`, L784); `RecoverAsync` and `RefreshAsync` never rebuild or submit.
- **D12 — `PoSbQuery` gains a server-side `IrbmStatus` filter** so the note's origin picker is filtered in SQL to `VALID` instead of post-filtering a page of 100 rows.
- **D13 — the batch cap now bounds Delete only.** `PoSbLimits.MaxPostSelection` keeps its name for house-wide parity (`PoInvoiceLimits`, `PoCdnLimits`, `SaCdnLimits`, `SaDoLimits`, `SaInvoiceLimits`, `IvPostingLimits` all use the same name); only its XML comment changes.

---

## Phase 0 — Lifecycle & regression audit (EXECUTED — evidence recorded, no work left)

Each line names the artefact that proves it; nothing here is an assumption.

| # | Question | Answer (verified) |
|---|---|---|
| 1 | Where can a self-billed document be submitted? | One gate. `IsSourceDocumentPosted` has a single call site (`SubmitBatchAsync`, L1965); `SubmitAsync`/`RetryAsync` reach it via L1888 and `SubmitManyAsync` via L784. `RecoverAsync` (L141) and `RefreshAsync` (L387) never rebuild or submit. |
| 2 | Where is a self-billed document edited/deleted? | `PoSbInvoiceService.UpdateAsync`/`DeleteAsync` and `PoSbCdnService.UpdateAsync`/`DeleteAsync`, each re-reading the header inside its own transaction. |
| 3 | Does anything else interpret `Status = POSTED`? | Only the two services' own gates, the two list pages and `SaEInvoiceService`. No report, export or API. |
| 4 | GL / stock / audit side effects on the ERP transition? | None. `TransitionAsync` writes only `Status` + `ModifiedDate`/`ModifiedBy` (plus inert `PostedBy`/`RollbackBy` repair stamps). Tax-only by design (`plan-poSelfBilledEInvoice.prompt.md` D6). |
| 5 | Background jobs / host endpoints? | None. Zero `PoSb` references under `ErpWeb/` (the host project) — no job, endpoint, API or hosted service touches these families. |
| 6 | Audit/history tables? | None for purchase documents — the solution has no field-level audit trail (only `IvTrxHistory` for inventory plus Serilog), so removing POST/ROLLBACK orphans no history rows. |
| 7 | Does removing POST/ROLLBACK weaken authorization? | No — see D10. Only the two now-unused menu grants are deactivated. |
| 8 | Is the UUID cleared on re-submission? | No code clears it. `IrbmStatus` is claimed as `SUBMITTING` (L2088) while `IrbmUuid`/`IrbmError` for self-billed are written only by `ApplyStatusAsync` (L2797-2814), i.e. overwritten by the new submission's outcome. |
| 9 | Does the e-Invoice claim bump `RowVersion`? | No — which is exactly why the lock check, not `RowVersion`, is the race guard for submit-vs-save (D7). |

### The canonical lifecycle matrix (shipped behaviour; exactly ONE cell changes)

| `IRBMStatus` | Edit | Delete | Submit | Retry | Recover | Refresh | Cancel |
|---|---|---|---|---|---|---|---|
| `NULL` / `NEW` | yes | yes | **yes** (was: only after POSTED) | – | – | – | – |
| `SUBMITTING` | no | no | no | – | yes (when stuck) | yes (with a UUID) | – |
| `SUBMITTED` | no | no | no | – | – | yes (with a UUID) | yes |
| `VALID` | no | no | no | – | – | yes (with a UUID) | yes |
| `INVALID` | yes | yes | yes | – | – | – | – |
| `REJECTED` | yes | yes | yes | – | – | – | – |
| `FAILED` + `ConfirmedFailure` | yes | yes | yes | yes | – | – | – |
| `FAILED` + `Unknown` | yes | yes | no — Recover first | – | yes | – | – |
| `CANCELLED` | yes | yes | yes (a new submission) | – | – | – | – |

The Edit/Delete half comes from `EInvoiceStatuses.Locked`; the right half from `SaEInvoiceStatusView` (`SaEInvoiceResults.cs:265-274`) and the `SubmitBatchAsync` pre-submit gate (L1978-2005). This matrix is the contract Phase E pins with a theory.

### Handling beyond the action matrix (closed questions)

| `IRBMStatus` | Re-submission | Existing MyInvois UUID | ERP `Status` effect |
|---|---|---|---|
| `NULL` / `NEW` | n/a — the first submission | none yet | created `NEW`; never changes |
| `SUBMITTING` | no — Recover first | untouched until the outcome lands | unchanged |
| `SUBMITTED` | no — already submitted | written from the API outcome | unchanged |
| `VALID` | no — already submitted | written from the API outcome | unchanged |
| `INVALID` | yes — fix then resubmit | **overwritten** by the new outcome | unchanged |
| `REJECTED` | yes | **overwritten** by the new outcome | unchanged |
| `FAILED` + `ConfirmedFailure` | yes (Retry) | **overwritten** by the new outcome | unchanged |
| `FAILED` + `Unknown` | no — Recover first | reconciled by Recover | unchanged |
| `CANCELLED` | yes — a **new** submission | **overwritten** by the new outcome | unchanged |

No state ever moves the ERP `Status`; nothing sets it back to `NEW`, and editing does not write it (D9). No code clears a UUID — `ApplyStatusAsync` overwrites it (audit row 8).

### Submit eligibility is its own predicate (D6)

Shipped, unchanged by this plan, and deliberately *not* derived from "not locked":

- `SaEInvoiceStatusView.CanSubmit` = `NEW | REJECTED | INVALID | CANCELLED | (FAILED + ConfirmedFailure)` — `SaEInvoiceResults.cs:267-268`.
- `CanRetry` = `FAILED + ConfirmedFailure`; `CanRecover` = `SUBMITTING` or `FAILED + Unknown`; `CanRefresh`/`CanCancel` = `SUBMITTED | VALID` with a UUID (`SaEInvoiceResults.cs:269-274`).
- The service re-checks the same partition before the `SUBMITTING` claim (`SaEInvoiceService.cs:1976-2005`), so the UI predicates are a mirror, never the gate (D7).

This plan changes none of it. It removes only the ERP-POSTED precondition that sat in front of it (step 4).

### Business decisions, not audits

1. `CANCELLED` and `REJECTED` are editable and re-submittable today, exactly like `INVALID`. If either should be terminal, that must be said before Phase A — see *Further considerations*.
2. `POSTED` becomes a read-only legacy value; confirm the list status filter keeps offering it.

---

## Phase A — Core gates (blocks B, C and E)

1. **`ErpWeb.Core/Purchase/PoSbInvoiceService.cs`**
   - `UpdateAsync` (~L413-421): keep the `IsEInvoiceLocked` refusal (it must stay *before* the ERP rule — that ordering is test-pinned), **delete** the `Status != PoSbStatuses.New` → `"Only NEW documents can be edited."` block.
   - `DeleteAsync` (~L498-503): keep the lock refusal, remove the `Status == NEW` ternary so the predicate returns null after the lock check.
   - `SearchAsync` row projection (~L196-220): drop the `isNew` / `isPosted` locals and the `CanPost` / `CanRollback` assignments; `CanEdit = CanDelete = !locked`.
   - `ToDocument` (~L740-747): same four lines.
   - Delete `PostAsync` (~L520) and `RollbackAsync` (~L540). Keep `TransitionAsync` (~L566) — `DeleteAsync` still relies on it — but delete the two now-inert `PostedBy`/`RollbackBy` stamp blocks inside it (~L641-648): once nothing writes `PostedDate`/`RollbackDate` they are dead code. The columns themselves stay.
2. **`ErpWeb.Core/Purchase/PoSbCdnService.cs`** — the identical set at L228-231 (list projection), L473 (`UpdateAsync`), L567 (`DeleteAsync`), L582-618 (transitions), L866-871 (`ToDocument`), plus the same `TransitionAsync` (~L628) stamp cleanup. *Parallel with step 1.*
3. **`ErpWeb.Core/Purchase/IPoSbInvoiceService.cs` + `IPoSbCdnService.cs`** — remove `PostAsync` / `RollbackAsync` from the interfaces; remove `CanPost` / `CanRollback` from `PoSbInvoiceListRow`, `PoSbInvoiceDocument`, `PoSbCdnListRow`, `PoSbCdnDocument`. *Depends on 1-2.*
4. **`ErpWeb.Core/EInvoice/SaEInvoiceService.cs`** — `IsSourceDocumentPosted` (~L3655-3673) → `IsSourceDocumentSubmittable`; delete the `PoSbInvoice` and `PoSbCdn` switch arms; keep the sales arms and `_ => true`; update the XML doc comment (self-billed is gated by the e-Invoice state alone; the POSTED rule is a sales-document rule). One call site (~L1965) needs the rename; the refusal text `"must be POSTED before it can be sent to MyInvois."` stays, because only sales can now reach it. **Mandatory: without this, no self-billed document can ever be submitted once POST is gone.**
5. **`ErpWeb.Core/Purchase/PoSbCalc.cs`** — re-document `PoSbStatuses.Posted` as a legacy value that no transition writes any more (reads only). *Depends on 1-3.*

## Phase B — Entry pages (parallel with Phase C)

6. **`ErpWeb.UI/Purchase/Transactions/PoSbInvoice.razor`**
   - Footer: delete the Post block (L232-235) and the Rollback block (L236-239).
   - View-mode Edit button (L219-223): drop the `string.Equals(Model.Status, PoSbStatuses.New, …)` condition; keep `IsView && CanEdit && IsEditable`.
   - Grid: replace the action `DxGridDataColumn Caption="" Width="140px"` (L180-196) with `DxGridCommandColumn Width="88px" Caption=" " NewButtonVisible="false" EditButtonVisible="false" DeleteButtonVisible="false"` wrapping a `CellDisplayTemplate` that renders `sdoc-row-actions` plus two buttons: `RenderStyle=Secondary`, `RenderStyleMode=ButtonRenderStyleMode.Text`, `IconCssClass="fa-solid fa-pen"` → `OnEditLine`, and `RenderStyle=Danger`, `RenderStyleMode=Text`, `IconCssClass="fa-solid fa-trash"` → `OnDeleteLine`, both `Enabled="@CanMutateLines"`.
   - Grid `KeyFieldName` → the new `LineEdit.UiKey` (see step 7).
   - UI review checklist for both pages (the review's §4.1): `CanMutateLines` must resolve to the same authoritative predicate the service enforces (`IsEditable && !IsSubmitting`, with `IsEditable = !IsView && !Model.IsEInvoiceLocked`); the row handlers stay thin (they mutate the in-memory list only — `UpdateAsync`/`DeleteAsync` still re-check on save, D7); the buttons are icon-only with a `title`, and `DxGridCommandColumn` is the first column so it cannot disturb the data columns; the grids have no selection/detail-row feature, so there is nothing else for the command column to interfere with.
7. **`PoSbInvoice.razor.cs`** — delete the `CanPost` / `CanRollback` fields (L40-41) and their `AccessRights.CanAsync` calls (L98-99); delete `PostAsync` (L314) and `RollbackAsync` (L344); add `public Guid UiKey { get; } = Guid.NewGuid();` to `LineEdit`, assign `UiKey = Guid.NewGuid()` in `FromDto` (the `Clone()` `MemberwiseClone` deliberately keeps it so an edit does not change the grid key). UI identity contract, to be stated in the type's XML doc: `FromDto()` = a new identity for a line loaded from the server; `Clone()` = the SAME identity (it only ever backs `OnEditLine`); `OnNewLineClick` = a new identity. `Clone()` must never be used to duplicate a line as a new row.
8. **`PoSbCdn.razor` / `PoSbCdn.razor.cs`** — the same as steps 6-7 (Post L250, Rollback L254, Edit button gate L238; fields L45-46, rights L117-118, `PostAsync` L382, `RollbackAsync` L412). *Parallel with 6-7.*
9. **`PoSbCdn.razor.cs` `RefreshOriginOptionsAsync` (~L205-215)** — stop filtering the picker by `Status = PoSbStatuses.Posted`; keep the `VendorCode` filter and add `IrbmStatus = EInvoiceStatuses.Valid` (D12), which is the same hard rule the payload applies in `PoSbOriginResolver.ResolveValidAsync`. Update the method's XML comment. **If this is missed, every new note has an empty origin combo.**
   - **The full origin invariant** (one rule, two enforcement points + one view): the origin is a `PoSbInvoice` — never a note — in the **same company and branch**, of the **same vendor**, with `IrbmStatus = VALID` **and** a non-blank `IrbmUuid`, and not `CANCELLED`. Save time additionally requires the note's currency to match the origin's (a blank inherits; a conflict is refused). The ERP `Status` is irrelevant. Enforced at submit by `PoSbOriginResolver.ResolveValidAsync` and at save by `PoSbCdnService.LoadOriginAsync`; the picker is a convenience view of the same rule, never the gate.
   - Supporting change: `PoSbSharedResults.cs` `PoSbQuery` gains `public string? IrbmStatus { get; set; }`, and both `SearchAsync` implementations add an upper-cased equality `Where` for it. The list data sources are unaffected (the property defaults to null), and the list's status filter keeps using the ERP `Status` property — the two must not be confused in the query object.

## Phase C — List pages

10. **`ErpWeb.UI/Purchase/Transactions/PoSbInvoiceList.razor.cs`**
    - Remove the `CanPost` / `CanRollback` fields (L51-52) and their rights calls (L152-153).
    - Remove the POST and ROLLBACK entries from the `Buttons` toolbar (L161-162).
    - Remove the `"POST"` / `"ROLLBACK"` arms from `ConfirmButtonText` (L69-70) and `ConfirmButtonStyle` (L79-80), the `OnButtonClick` dispatch cases (L206-207) and the confirm-executor switch cases (L655-656); delete `BeginPostAsync` (L722) and `BeginRollbackAsync` (L744).
    - `EInvoiceConfirmMessage` (~L411-413): drop the `"Only a POSTED document can be submitted; the document is finalised at MyInvois."` sentence.
    - `EInvoiceIneligibleReason` (~L429-431): drop the `not POSTED` branch (the `view.CanSubmit` checks stay).
    - `BeginDeleteAsync` (~L727-734): replace the `notNew` guard with the existing lock guard keyed on `x.CanDelete == false`.
    - `StatusFilterOptions` (L121-124): keep the POSTED option but source it from `PoSbStatuses.Posted`; `StatusChipClass` (L692) unchanged.
    - Update the class XML doc (L18-23), which states "SUBMIT requires the document to be POSTED".
11. **`PoSbCdnList.razor.cs`** — mirror of step 10 (fields L48-49, rights L164-165, buttons L173-174, text/style L66-67 / L76-77, dispatch L222-223 / L685-686, `BeginPostAsync` L752, `BeginRollbackAsync` L784, messages L437 / L454, filter options L129-133, chip L721). Its confirm sentence also mentions the origin invoice being VALID — keep that half. *Parallel with step 10.*

## Phase D — Deployment + docs

12. **`scripts/init-pobsb-menu.sql`** — remove `N'POST'` and `N'ROLLBACK'` from both `MenuPermission` `IN (…)` lists; replace the re-activate `UPDATE` with a targeted `IsActive = 0` `UPDATE` for those two permissions (deactivate, do **not** delete — `dbo.RoleMenuPermission` may reference them and the house style is a soft disable); change the verification expectation from 24 (3 menus × 8) to 18 (3 × 6) and update the header comment. The remaining grants (`ACCESS`, `ADD`, `EDIT`, `DELETE`, `SUBMIT`, `CANCEL`) are what authorize everything left (D10).
    - **Role grants are the deployment owner's step, not this script's**: `dbo.RoleMenuPermission` uses `IsAllowed` (no `IsActive`) and is deliberately never touched by seed scripts. Document in the script header that a role still holding a `RoleMenuPermission` row for `POST`/`ROLLBACK` on these three menus keeps an inert grant that no code consults; optionally offer a commented-out cleanup statement for the DBA.
13. **`plans/plan-poSelfBilledEInvoice.prompt.md`** — update the "Lifecycle & rebuild rules" row for `NULL / NEW` ("refused until POSTED, then allowed" → "allowed") and the paragraph asserting the ERP dimension is orthogonal and required for submission; add a short implementation-status entry.
14. **No change to `ErpWeb/Menus/menus.xml`** — it carries only `Menu` rows (no permission elements); `POST`/`ROLLBACK` come from the SQL seed script.

## Phase E — Tests

15. **`ErpWeb.Tests/SaEInvoiceSelfBilledPayloadTests.cs`** — flip `A_self_billed_invoice_must_be_posted_before_it_can_be_submitted` (L167-180) to assert that a self-billed invoice with ERP status `NEW` submits successfully (result succeeded, exactly one helper call). The other payload tests seed `status: "POSTED"` by default and stay valid.
16. **`ErpWeb.Tests/PoSbServiceTests.cs`**
    - Replace the `PostedInvoiceAsync` helper with a direct-status seeder (save, then write `Status` / `IrbmStatus` / `IrbmUuid` through the `DbContext`) because `PostAsync` no longer exists.
    - Rewrite `A_new_document_is_NEW_and_posted_documents_cannot_be_edited` (L266) into the opposite assertion: a row holding legacy `POSTED` **is** editable.
    - Rename `A_locked_e_invoice_refuses_edit_delete_and_rollback` (L281) → `…_refuses_edit_and_delete`, dropping the rollback asserts.
    - Rewrite `A_failed_e_invoice_does_not_lock_the_document` (L303) to assert `UpdateAsync` succeeds.
    - Delete `A_cancelled_e_invoice_can_be_rolled_back` (L316) and `Posting_requires_the_post_permission` (L412).
    - Add: delete is permitted for an `INVALID` row (the lock-only rule), and refused for a `SUBMITTED` one.
17. **Lifecycle-matrix theory (new)** in `PoSbServiceTests` — one `[Theory]` over the Phase 0 matrix asserting `CanEdit`/`CanDelete` on the loaded document for every `IRBMStatus` value (the three locked states → false, everything else → true), plus a batch delete refused for a `SUBMITTED` row and allowed for `INVALID`/`CANCELLED`. This is the regression net for D3/D8.
18. **Service-authority / race test (new)** in `PoSbServiceTests` — save a document, call `GetAsync` (simulating a page that loaded it), then write `IRBMStatus = SUBMITTED` directly through a second `DbContext`, then call `UpdateAsync`/`DeleteAsync` with the previously loaded object: both must be refused with the e-Invoice lock message. Pins D7 (the service re-reads; the UI guard is never the gate).
19. **Origin-picker filter test (new)** in `PoSbServiceTests` — seed two self-billed invoices for the same vendor (one `VALID`, one `INVALID`) plus a `VALID` one for a different vendor, then assert `SearchAsync(new PoSbQuery { VendorCode = …, IrbmStatus = EInvoiceStatuses.Valid })` returns exactly the one usable origin. Add the fourth case the review asked for: a **legacy `POSTED` + `VALID`** origin must appear (ERP status is irrelevant). This is the service-level half of the picker rule; the UI half stays a manual smoke item.
20. **Submission-boundary tests (new)** in `SaEInvoiceSelfBilledPayloadTests` — (a) an `INVALID` self-billed document can be resubmitted; (b) a **legacy `POSTED`** self-billed document still submits; (c) the D2 non-leak guard: a **sales** `SaInvoice` with ERP status `NEW` is still refused with `must be POSTED`, proving the gate change is scoped to SBI/SBC/SBD.

---

## Verification

1. `dotnet build ErpWeb.slnx` → 0 `error CS` / `RZ` (MSB3021 / MSB3027 on the host project are the known file locks when the running app is up).
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~PoSbServiceTests|FullyQualifiedName~EInvoice"` → fully green.
3. Full `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` → the same 15 pre-existing failures only (13 `SaCustServiceTests` + `PoSupplierServiceTests` GL-code/phone WIP, 2 library `InvoiceTypeCode`) and `0 skipped`. Any new failure belongs to this change.
4. Repository-wide static audit (the review's item 8) — search for every one of `PoSbInvoice`, `PoSbCdn`, `PoSbStatuses.Posted`, `CanPost`, `CanRollback`, `PostAsync`, `RollbackAsync`, `"POST"`, `"ROLLBACK"`, `"must be POSTED"`, `IsSourceDocumentPosted`, `IsSourceDocumentSubmittable`, then classify EVERY remaining occurrence as (a) intentional legacy/read-only, (b) an unrelated family that must stay untouched (`PoInvoice`, `PoCdn`, `SaDo`, `SaInvoice`, `SaCdn`, inventory posting), (c) a test fixture, or (d) obsolete and removed. Expected (a) for self-billed: the list status-filter option only. Expected (b): the intact sales/inventory gates, including the retained `"must be POSTED"` message.
5. Manual smoke (browser, still owed for the feature): a freshly saved SBI submits from the list without POST; the entry page is read-only while `SUBMITTED` / `VALID`; an `INVALID` document re-opens for editing; SBC's origin combo lists only `VALID` self-billed invoices of the same vendor; both grids render icon-only pen / trash row buttons.

## Risks

- **`SaEInvoiceService` gate** is the functional breakage: miss step 4 and every self-billed submission is refused with "must be POSTED".
- **`PoSbCdn.RefreshOriginOptionsAsync`** is the silent breakage: miss step 9 and every new note has an empty origin combo.
- **Removing `PostAsync`** breaks ~6 tests; they must be rewritten in the same change or the suite is red.
- `ErpWeb.Tests` DOES reference `ErpWeb.UI` now, but a green suite still does not prove a page renders — the `dotnet build ErpWeb.slnx` in step 1 is mandatory after touching `.razor`.
- **`PoSbQuery` is the shared list filter** — adding `IrbmStatus` next to the existing ERP `Status` is a copy-paste trap; a swap would make a list silently filter on the wrong column.
- **The permission change is a deployment step**, not just code: until the amended `init-pobsb-menu.sql` is applied, the POST/ROLLBACK grants stay active (harmless, since nothing calls them, but the menu's permission matrix still advertises them).

## Further considerations

1. **`CANCELLED` / `REJECTED` editability** — this plan keeps both editable, deletable and re-submittable (they are not in `EInvoiceStatuses.Locked`), i.e. the same path as `INVALID`. If cancellation is meant to be terminal in the business sense, `CANCELLED` needs its own predicate (or must join the lock): that is a business decision, not a code detail.
2. **Submit straight after Save** — today the entry page navigates to the list on save, so submission is a list action. Alternatives: re-open the saved document in view mode, or surface the `SaEInvoicePanel` on the edit route.
3. **Grid key** — the grids currently key on `ICode`, which is not unique; step 6-7 fixes it with `UiKey`. Alternative: key on a line number.

## Review traceability (so nothing is silently dropped)

| Review item | Where it landed |
|---|---|
| §2.1 complete state contract | Phase 0 — the action matrix **plus** the "handling beyond the action matrix" table, and the submit-eligibility predicate section |
| §2.2 separate edit/delete from submit | D6 + the submit-eligibility predicate section |
| §2.3 concurrency / service authority | D7 + audit row 9 + test step 18 |
| §2.4 legacy POSTED behaviour | D8, D9 + audit rows 3, 4 + verification step 4 |
| §2.5 all submit paths | D11 + audit row 1 + test step 20(c) |
| §3.1 delete decision | promoted into D8 (no longer a "consideration") |
| §3.2 `TransitionAsync` audit | audit row 4 + Phase A stamp cleanup |
| §4.1 UI verification checklist | Phase B step 6 bullet + manual smoke item |
| §4.2 `UiKey` semantics | Phase B step 7 identity contract |
| §5 origin invariant + matrix | Phase B step 9 (full invariant) + test step 19 (4 cases) |
| §6 permission references | D10 + Phase D step 12 (deactivate, not delete) + role-grant note |
| §7 lifecycle/submission tests | Phase E steps 17, 18, 20 |
| §8 broader static verification | Verification step 4 |
| §9 authorization regression | D10 + audit row 7 |
| §10 audit/history regression | audit row 6 |
| §11 API / background jobs | audit row 5 |
| §12 Phase 0 restructuring | Phase 0 exists (executed, with evidence) |
| §13 D6-D10 | D6, D7, D8/D9, D10, D11 — kept the review's wording, extended to D12/D13 |
| Implementation order | Phase 0 (done) → A core gates incl. the submission gate → B/C UI → D permissions + docs → E tests → verification. Permission cleanup deliberately follows the code, so a partially applied change can never leave a live UI calling a revoked grant |
