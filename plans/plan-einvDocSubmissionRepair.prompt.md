# Plan: Repair `dbo.EInvDocSubmission` on the E-UUID click

## Goal (user request, verbatim intent)

Clicking the **E-UUID** cell in `SaInvoiceList` currently only opens the LHDN MyInvois portal tab.
It must ALSO re-read the document from MyInvois and **create-or-update** the `dbo.EInvDocSubmission`
row, because (a) LHDN sometimes changes the record at its side, and (b) sometimes the row was never
created during submission.

## Research findings that shape the plan

| Fact | Evidence |
|---|---|
| `RefreshAsync` is already the full repair: authorize(Submit) → `GetDocumentDetail(uuid)` → ERP `IRBM*` write-back → `ApplyStatusAsync` upsert → `SaEInvoiceLog` "Refresh" row | `ErpWeb.Core/EInvoice/SaEInvoiceService.cs:381-518` |
| `EInvoiceSubmissionWriter.UpsertAsync` already does find-or-insert on `(companyID, submissionUUID, documentType, documentNo)`, sets `LastSyncedOn`, and recovers duplicate-key races | `ErpWeb.Core/EInvoice/EInvoiceSubmissionWriter.cs:216-260, 247` |
| Both writer hooks **skip silently** when `submissionId` is blank | `EInvoiceSubmissionWriter.cs:107-112, 152-157` |
| The submission id is deliberately always taken from the ERP document, never the payload (plan rule 3.5) | `EInvoiceSubmissionWriter.cs:133-140` |
| `ApplyDetail` ignores `d.submissionUid`; `SubmissionUuid` is only set by `NewRow` | `EInvoiceSubmissionWriter.cs:340-400` |
| The UI can only reach `ISaEInvoiceService` (`EInvoiceSubmissionWriter` is `internal`) | `ISaEInvoiceService.cs:6-12`, `EInvoiceSubmissionWriter.cs:49` |
| `EInvoicePortalLinkOpener` is UUID-only and its contract says "Never calls MyInvois and writes no audit row" | `ErpWeb.UI/Components/Common/EInvoicePortalLinkOpener.cs`, `ISaEInvoiceService.cs:70-77` |
| `GetDocumentDetail` returns `DocumentValidatation`, which HAS `submissionUid` | `ErpWeb.EInvoiceLib/Model/Document/Submission.cs:143-165` |
| `AuthorizeAsync` REFUSES self-billed (SBI/SBC/SBD) with `NotConfigured`; `LoadStateAsync` has no case for them | `SaEInvoiceService.cs:2481-2537, 1832-1900` |
| `AuthorizeAsync` uses `TryBranchScope()`; `GetPortalLinkAsync` uses `TryCompanyScope()` | `SaEInvoiceService.cs:2496, 898` |
| Only ONE screen uses `onSelectColHandle` today (`SaInvoiceList`); CN/DN + self-billed lists do not exist yet | workspace grep |
| Tests: xUnit 2.9.3 + `Microsoft.NET.Test.Sdk` = **VSTest** (no MTP signal in `global.json`) | `ErpWeb.Tests/ErpWeb.Tests.csproj` |

## Decisions (from the Q&A round)

- D1 — New **UUID-addressed `ISaEInvoiceService`** method; the UI never touches the lib.
- D2 — When the ERP `IRBMSubmitID` is blank, **adopt `submissionUid` from the MyInvois response**
  and write it back to the invoice. Deliberate, documented exception to plan rule 3.5.
- D3 — Repair **both** the history row and the ERP document.
- D4 — A row existing under a **different** `submissionUUID` ⇒ **insert a new row** (keep the current rule).
- D5 — Requires `PermissionCodes.Submit`.
- D6 — Failure is **best-effort**; the portal tab must still open.
- D7 — The tab opens **first**; the sync runs after.
- D8 — `GetDocumentDetail` only (no `GetDocument`/raw).
- D9 — Shared helper, so every current and future UUID link gets it.
- D10 — **Hard re-entry invariant on the click:** at most **ONE** MyInvois repair and at most **ONE**
  portal-link retry per click. The retry exists only to re-resolve a link, never to repair again (see F1).
- D11 — The click timeout option is `RepairPersistenceTimeoutSeconds`, named for what it actually bounds.
  It cannot abort the MyInvois HTTP call; the lib fix is logged as tech debt, not done here.
- D12 — `internal enum EInvoiceHistoryWrite` stays **internal and unchanged**. The public service contract
  exposes a separate `EInvoiceHistoryWriteResult`, so the persistence implementation does not leak
  through `ISaEInvoiceService`.

### F-decisions I took (not explicitly answered — flagged for review)

- F1 — **Hybrid timing, with a hard invariant.** Fast path = resolve the link, open the tab, then sync.
  If the link CANNOT be resolved (the missing-row case, which is the user's main motivation), await the
  repair and retry the link **once** before giving up — otherwise the repair is invisible exactly when
  it is needed.
  - **The retry is a LINK-RESOLUTION fallback only, never a second repair.** The order is fixed:
    link → (if null) repair once → re-resolve link once → open or report. `RepairSubmissionAsync` is
    called `<= 1` time and `GetPortalLinkAsync` at most **twice**, whatever the outcome (D10,
    test 25). Written down because the natural "fix" for a future failure — loop until the link
    appears — would turn one click into an unbounded MyInvois loop.
- F2 — **Self-billed gets the existing refusal message**, no bypass. `AuthorizeAsync` refuses SBI/SBC/SBD
  with `NotConfigured` and `LoadStateAsync` cannot load them; inventing a bypass would ship an
  untestable path. Revisit when the Purchase self-bill payload is wired.
- F3 — **Company-wide fallback** when the branch-scoped `LoadStateAsync` returns null but the row's
  `BranchCode` differs from the caller's scope.

## Scope

**In scope:** INV, CN, DN UUID links (today: `SaInvoiceList`). History row create/update, ERP `IRBM*`
write-back, submission-id recovery, `Refresh` audit row.

**Out of scope:** `GetDocument`/raw payload capture; the `document` column (never written, plan D-14);
true mirroring/deletion sync (see Risks); self-billed repair; a UI "Repair" button (the UUID click IS
the affordance); adding a new permission constant; changing the e-Invoice lib signatures to accept a
`CancellationToken` (tech debt — Further consideration 4).

---

## Phase 1 — Core plumbing (no behaviour change on its own)

Files: `ErpWeb.Core/EInvoice/SaEInvoiceResults.cs`, `ErpWeb.Core/EInvoice/SaEInvoiceService.cs`.
`EInvoiceSubmissionWriter.cs` is **not** touched (D12).

1. Add a **public** `EInvoiceHistoryWriteResult` enum to `SaEInvoiceResults.cs`:
   `NotAttempted`, `Inserted`, `Updated`, `Skipped`, `Failed`. `internal enum EInvoiceHistoryWrite`
   (`EInvoiceSubmissionWriter.cs:16`) stays **internal and unchanged** (D12) — `SaEInvoiceService` maps
   the writer's four outcomes onto the public five. `Skipped` and `Failed` are kept distinct because the
   UI message differs: "there is no submission id to record" vs "the history write failed".
2. `SaEInvoiceResult` (`SaEInvoiceResults.cs:74`) gains two members:
   - `EInvoiceHistoryWriteResult HistoryWrite { get; init; }` — what the registry write actually did
     (default `NotAttempted`).
   - `bool SubmissionIdRecovered { get; init; }` — true when the id came from the API, not the invoice.
3. `RefreshAsync` (`SaEInvoiceService.cs:381-518`), in the persist block after the tracked
   `LoadStateAsync` and before `ApplyState`:
   - if `loaded.SubmitId` is blank AND `detail is { IsSuccess: true }` AND
     `detail.result.submissionUid` is non-blank ⇒ assign the trimmed value to `loaded.SubmitId` and set
     a local `submissionIdRecovered = true`.
   - capture the return of `EInvoiceSubmissionWriter.ApplyStatusAsync` (currently discarded at
     `SaEInvoiceService.cs:481-491`) into `historyWrite`.
   - set both new fields on the returned `SaEInvoiceResult`.
   Note: `ApplyState` already writes `SubmitId` back to `SaInvoices`/`SaCdns.IrbmSubmitId`, so the
   recovery persists without extra code.
4. `EInvoiceOptions` (`SaEInvoiceResults.cs:8`) gains
   `int RepairPersistenceTimeoutSeconds { get; set; } = 25`. **The name is the contract**: it bounds
   the click path's *persistence* work only. It does NOT bound the MyInvois HTTP call, because
   `GetDocumentDetail` takes no `CancellationToken` (see Risks and Further consideration 4).

No change to `EInvoiceSubmissionWriter`'s logic. This is deliberate: the writer is already correct and
tested, and the only missing input was the key.

## Phase 2 — The repair entry point (depends on 1)

Files: `ErpWeb.Core/EInvoice/ISaEInvoiceService.cs`, `ErpWeb.Core/EInvoice/SaEInvoiceResults.cs`,
`ErpWeb.Core/EInvoice/SaEInvoiceService.cs`

5. Add `SaEInvoiceSubmissionRepairResult` to `SaEInvoiceResults.cs`:
   `Attempted`, `Succeeded`, `Message` (always UI-ready), `DocumentType`, `DocumentNo`, `Status`,
   `SubmissionId`, `SubmissionIdRecovered`, `HistoryWrite` (`EInvoiceHistoryWriteResult`), `ErrorKind`,
   plus a `NotApplicable(message)` factory. `Attempted == false` is the "nothing to do" answer and is
   NOT an error, so the opener never turns it into a red toast.
6. Add `ISaEInvoiceService.RepairSubmissionAsync(string uuid, CancellationToken ct = default)`,
   documented as: UUID-addressed, resolves the document itself, best-effort, never throws.
7. Implement in `SaEInvoiceService` as **resolver + delegate**, next to `GetPortalLinkAsync`
   (`SaEInvoiceService.cs:888`):
   - blank uuid ⇒ `NotApplicable`.
   - `_tenant.TryCompanyScope()`; null ⇒ `NotApplicable` / authorization message.
   - **Resolve the key** (new private `ResolveKeyByUuidAsync`):
     1. `db.EInvDocSubmissions` where `CompanyId == scope.CompanyCode && Uuid == uuid`,
        `OrderByDescending(Id)`, project `DocumentType`/`DocumentNo`/`BranchCode` (the same "current row
        = highest Id" rule `GetPortalLinkAsync:905-916` already uses). Skip if `DocumentType` is
        self-billed (F2).
     2. else `db.SaInvoices` where `CompanyCode == scope.CompanyCode` and
        `IrbmUuid == uuid || IrbmOriUuid == uuid` ⇒ `INV` + `InvNo`;
        then `db.SaCdns` likewise ⇒ `CN`/`DN` from its `Type`.
     3. else ⇒ `NotApplicable("This MyInvois UUID is not linked to any ERP document.")`
   - `AuthorizeAsync(key, PermissionCodes.Submit)` ⇒ on error return
     `Attempted: false, ErrorKind: Authorization` with a message saying the history was NOT repaired
     (D5 + D6: the link still opens).
   - load state (non-tracking) for the existence/branch check; on null retry with the row's
     `BranchCode` (F3); still null ⇒ `NotApplicable`/NotFound message.
   - `await RefreshAsync(key, ct)` inside a linked `CancellationTokenSource` bounded by
     `_options.RepairPersistenceTimeoutSeconds`; catch `OperationCanceledException` and every other
     exception. **This is the only call to `RefreshAsync` on the click path** (D10).
   - map the `SaEInvoiceResult` into `SaEInvoiceSubmissionRepairResult` and compose `Message` from
     `HistoryWrite` (`Inserted` ⇒ "e-Invoice history row created", `Updated` ⇒ "…updated"),
     `SubmissionIdRecovered`, the resulting `Status`, and `ErrorMessage`.

## Phase 3 — Tests for Phases 1-2 (depends on 2)

Files: new `ErpWeb.Tests/SaEInvoiceSubmissionRepairTests.cs` (follow
`ErpWeb.Tests/EInvoiceSubmissionHistoryTests.cs` + `EInvoiceTestHost`); touch
`ErpWeb.Tests/FakeSubmitDocumentHelper.cs` only if a new handler is needed (`DocumentDetailHandler` is
already a settable `Func<string, GeneralResult<DocumentValidatation>>`, line 52).

8. `Repair_inserts_the_missing_row_from_the_api_submissionUid` — `SeedInvoiceAsync(irbmStatus: Valid,
   irbmUuid: <uuid>, irbmSubmitId: null)`, no registry row, `DocumentDetailHandler` returns success with
   a `submissionUid`; assert `HistoryWrite == Inserted`, `SubmissionIdRecovered`, exactly one row whose
   `SubmissionUuid` is the API value, and `SaInvoices.IrbmSubmitId` written back.
9. `Repair_updates_the_row_when_lhdn_changed_the_document` — seed a matching
   `irbmSubmitId` + row; handler returns a different `status`/`totalPayableAmount` ⇒ `Updated`,
   `LastSyncedOn` set, totals overwritten.
10. `Repair_does_not_erase_history_when_lhdn_returns_a_sparse_detail` — non-null-only rule still holds.
11. `Repair_is_not_applicable_when_the_uuid_is_not_linked` — assert `Attempted == false`,
    `HistoryWrite == NotAttempted`, and **`Helper.Calls` contains no `GetDocumentDetail`**.
12. `Repair_refuses_without_submit_permission` — `Attempted == false`, `ErrorKind == Authorization`,
    no MyInvois call.
13. `Repair_creates_no_row_when_neither_source_has_a_submission_id` — both blank ⇒ message explains,
    zero rows.
14. `Refresh_adopts_the_submission_uid_and_creates_the_missing_row` — pins the Phase-1 change so the
    E-STATUS button's behaviour change is intentional, not accidental.
15. `Repair_works_for_credit_and_debit_notes` — CN (`SeedCdnAsync`) version of test 8.
16. `Repair_is_not_configured_for_self_billed` — pins F2.
17. `Repair_uses_company_scope_when_the_branch_scoped_state_is_missing` — the positive half of F3: a
    registry row whose `BranchCode` differs from the caller's scope still repairs.
18. `Repair_does_not_write_across_branches` — the negative half of F3, and the automated cover for the
    risk bullet: the ERP `IRBM*` write-back must land on `(CompanyCode, BranchCode, InvNo)`. Seed the
    same `InvNo` in two branches, then assert only the caller's branch row changed and the other
    branch's row is byte-identical after the repair.
19. `Repair_calls_GetDocumentDetail_exactly_once` — pins D10/F1:
    `Helper.Calls.Count(x => x == nameof(GetDocumentDetail)) == 1` per repair, so the retry path can
    never silently become a second MyInvois call.
20. `Repair_does_not_create_duplicate_history_rows_when_called_twice` — pins the upsert contract the
    whole design rests on: repairing the same document twice leaves exactly ONE row.

## Phase 4 — UI (depends on 2; parallel with 3)

Files: `ErpWeb.UI/Components/Common/EInvoicePortalLinkOpener.cs`,
`ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor.cs`

21. `EInvoicePortalLinkOpener`: keep `OpenAsync` unchanged (backward compatible) and add
    `OpenAndRepairAsync(eInvoices, jsRuntime, uuid, ct)` implementing the F1 hybrid under the D10
    invariant: link → open tab → sync, EXCEPT when the link is unresolvable, in which case repair once
    (bounded by the persistence timeout) and retry the link once. **Write the invariant into the method
    doc comment as an explicit "at most one repair, at most one link retry" statement**, so the
    fallback cannot be mistaken for a retry-until-success loop.
22. Extend `EInvoicePortalOpenResult` with `bool StateChanged` (set when the history row or the ERP
    document was written) and fold the repair text into `Message`. `Opened` keeps its current meaning:
    "the portal tab opened", so `SaInvoiceList`'s existing Status/Error split is untouched.
23. `SaInvoiceList.onSelectColHandle` (lines 369-387): call `OpenAndRepairAsync`, and when
    `StateChanged` is true also `await ReloadGridAsync()` so the E-Inv/E-Status columns reflect the
    refreshed state (the repair runs in its own `AppDbContext`, so the grid must re-read).
24. Update the XML doc + the copy-paste handler sample at the top of `EInvoicePortalLinkOpener.cs`
    (lines 13-24) to the new pattern, so future CN/DN + self-billed screens copy the right one.

## Phase 5 — Tests for Phase 4 + docs (depends on 4)

Files: new `ErpWeb.Tests/EInvoicePortalLinkOpenerTests.cs`; `docs/einvoice-history.md`;
`plans/plan-einvDocSubmissionHistory.prompt.md`

25. `Opener_opens_the_tab_even_when_the_repair_throws`; `Opener_retries_the_link_once_after_a_successful_repair`;
    `Opener_does_not_repair_a_blank_uuid`;
    `Opener_reports_StateChanged_only_when_something_was_written`;
    `Opener_calls_the_repair_at_most_once_when_the_link_stays_unresolvable` — the D10 invariant at the
    UI layer (repair once, resolve the link twice, then report; never a loop).
26. Document the repair path and the **rule-3.5 exception** in `docs/einvoice-history.md` (which already
    has an "Implemented in ErpWeb" section) and add it as a new decision (D-19) in
    `plans/plan-einvDocSubmissionHistory.prompt.md` so the locked-rule list stays honest.
27. Document the **`Refresh` audit-row semantics** in `docs/einvoice-history.md`: a row means "a
    synchronization was requested, and this is what MyInvois answered" — **not** "LHDN data changed".
    Every click writes one, so an operator clicking the same UUID repeatedly produces a run of identical
    rows by design. Record the future option (suppress an identical, unchanged row) without building it.

---

## Files to modify (complete)

| File | Change |
|---|---|
| `ErpWeb.Core/EInvoice/EInvoiceSubmissionWriter.cs` | **No change** — `EInvoiceHistoryWrite` stays internal (D12). |
| `ErpWeb.Core/EInvoice/SaEInvoiceResults.cs` | `+ public EInvoiceHistoryWriteResult`; `+ HistoryWrite`, `+ SubmissionIdRecovered` on `SaEInvoiceResult`; `+ RepairPersistenceTimeoutSeconds` on `EInvoiceOptions`; `+ SaEInvoiceSubmissionRepairResult` |
| `ErpWeb.Core/EInvoice/ISaEInvoiceService.cs` | `+ RepairSubmissionAsync` |
| `ErpWeb.Core/EInvoice/SaEInvoiceService.cs` | `RefreshAsync` recovers the submission id + captures the write outcome; `+ RepairSubmissionAsync` + `ResolveKeyByUuidAsync`; maps the internal write outcome to `EInvoiceHistoryWriteResult` |
| `ErpWeb.UI/Components/Common/EInvoicePortalLinkOpener.cs` | `+ OpenAndRepairAsync` (D10 invariant in its doc comment), `+ StateChanged`, updated doc sample |
| `ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor.cs` | `onSelectColHandle` → `OpenAndRepairAsync` + conditional grid reload |
| `ErpWeb.Tests/SaEInvoiceSubmissionRepairTests.cs` | new (tests 8-20) |
| `ErpWeb.Tests/EInvoicePortalLinkOpenerTests.cs` | new (test 25) |
| `docs/einvoice-history.md`, `plans/plan-einvDocSubmissionHistory.prompt.md` | repair path, the 3.5 exception, the `Refresh` audit-row semantics |

## Verification

1. `dotnet build ErpWeb.slnx --nologo -v:q` — 0 errors. **Mandatory**: `ErpWeb.Tests` has no
   `ErpWeb.UI` reference, so `dotnet test` never compiles the changed `.razor.cs`. If a running
   `ErpWeb` app locks the host project, build `ErpWeb.Core` / `ErpWeb.UI` / `ErpWeb.Tests` per project.
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~EInvoice"` (VSTest
   syntax; no `--` separator — the project is xUnit 2 + `Microsoft.NET.Test.Sdk`, and `global.json`
   has no MTP runner key).
3. Known baseline to expect on the full suite: **13 pre-existing failures** in `SaCustServiceTests` (9)
   + `PoSupplierServiceTests` (4). Any e-Invoice failure is mine; those 13 are not.
4. If the SQL Server suites are enabled: set `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` and
   `ConnectionStrings__SqlServerTestConnection` in **separate** terminal commands (a semicolon on the
   `dotnet test` line truncates the value), then run `dotnet test` alone. `Skipped: 0` is the only
   proof they ran. Scratch DB `ERPWeb_EInvHistoryTest`. Two known unrelated failures will appear:
   `AdSmParamSqlServerConcurrencyTests.Two_simultaneous_inserts…` and
   `SaEInvoiceSqlServerConcurrencyTests.A_row_touched_during_the_submission…`.
5. Manual smoke (needs a browser): open `/sales/invoices`, delete one `dbo.EInvDocSubmission` row for a
   VALID invoice with `sqlcmd -E -d ERPWeb -I`, click its E-UUID cell ⇒ the tab opens AND a row
   reappears with `LastSyncedOn` set; a `SaEInvoiceLog` `Refresh` row exists; the grid's E-Inv column
   refreshed. Repeat on a CN/DN.
6. Click a UUID as a user WITHOUT SUBMIT rights ⇒ the tab still opens, no row is created, the message
   says the history was not repaired. Click one whose UUID belongs to no ERP document ⇒ "not linked"
   message, no MyInvois call. Click the same UUID twice ⇒ two `Refresh` rows but still **one**
   `EInvDocSubmission` row, and at most one MyInvois call per click (D10).
7. Read-only check: `scripts/discover-einvdocsubmission.sql` (already re-runnable) confirms the row
   shape after a repair.

## Risks / accepted consequences

- **The click becomes a write.** Every click now adds a `SaEInvoiceLog` `Refresh` row and possibly
  updates the invoice's `IRBMStatus`. Intentional (traceability), but a change from today's read-only
  click.
- **A `Refresh` audit row means "a sync was requested", not "LHDN data changed".** An operator clicking
  the same UUID five times writes five identical rows. Accepted for now (it records who asked and
  when); the alternative — suppress or dedupe an unchanged row — is recorded as a future option, not
  built, and the semantics are documented in `docs/einvoice-history.md` (Phase 5, item 27).
- **Not a true mirror.** `ApplySummary`/`ApplyDetail` copy non-null values only (locked plan 3.4), and
  `Status` only moves for values `SaEInvoiceStatusMap.IsRecognised` accepts. A field cleared at LHDN is
  NOT cleared locally. "Update" = fill/overwrite, never erase.
- **Duplicates are possible by design.** D4 means a legacy row stored under a different
  `submissionUUID` yields a second row for the same document. `GetPortalLinkAsync` then resolves "the
  highest Id" as current — intended, but two rows describe one document.
- **`EInvoiceOptions.RepairPersistenceTimeoutSeconds` will not abort the HTTP call**, because
  `ISubmitDocumentHelper.GetDocumentDetail(string uuid)` takes no `CancellationToken`
  (`ErpWeb.EInvoiceLib/Interface/ISubmitDocumentHelper.cs:14`). It bounds only the persistence work. A
  transport hang still waits for `HttpClient`'s default timeout. The option is deliberately NOT named a
  "repair timeout"; the lib fix is logged as tech debt (Further consideration 4) and not done here.
- **Self-billed (SBI/SBC/SBD) is not repairable** until the Purchase self-bill payload mapping exists
  (`SaEInvoiceService.cs:2532-2537`). F2.
- **Branch scope.** A company-wide fallback (F3) is added, but the ERP `IRBM*` write-back still targets
  the invoice found by `(CompanyCode, BranchCode, InvNo)`; if the row genuinely belongs to another
  branch the repair reports not-found rather than writing across branches. That is the safe direction.
- **`Succeeded` is not "the row was repaired".** `RefreshAsync` returns `Succeeded = applied`, i.e.
  only when a recognised status was applied. `HistoryWrite` is the authoritative "did the registry get
  written" signal, and the UI message must be composed from it.

## Further considerations

1. **F1 is my inference, not your stated choice** — you picked "open the tab first", but that alone
   leaves the first click on a never-recorded document showing "No LHDN link". The hybrid keeps the
   normal path instant. Option A = hybrid (recommended). Option B = strictly open-first, accept that
   the repair only helps on the second click. Option C = always sync-then-open (simplest to reason
   about, slowest click).
2. **Should the repair also run from the E-STATUS button?** It already will, because the change is
   inside `RefreshAsync`. If you want that isolated to the UUID click only, the submission-id recovery
   must move out of `RefreshAsync` into the new method — at the cost of a second `GetDocumentDetail`
   call and a second code path. I recommend keeping it shared.
3. **`DocumentId` stays NULL.** `EInvDocSubmission.DocumentId` is deliberately never written (plan
   D-15) because `SaInvoice` has no identity key. A repair does not change that; flagged only so it is
   not mistaken for an omission.
4. **TECH DEBT (not this task): give `GetDocumentDetail` a `CancellationToken`.** The only reason the
   click-path timeout cannot cover the MyInvois call is
   `ISubmitDocumentHelper.GetDocumentDetail(string uuid)` and its repository twin
   (`ErpWeb.EInvoiceLib/Interface/IE_InvoiceRepository.cs:11`). The fix is an optional-token overload
   `GetDocumentDetail(string uuid, CancellationToken cancellationToken = default)` (plus `GetDocument` /
   `GetSubmission`), threaded into `E_InvoiceRepository`'s `HttpClient` call. Out of scope here because
   it touches the e-Invoice lib and every fake and consumer — but it is the correct long-term fix and
   should not have to be re-discovered.
5. **Review-driven changes in this revision:** (a) **D10** — the one-repair/one-retry invariant, with
   the retry explicitly a link-resolution fallback and its own test (25); (b) `RepairTimeoutSeconds`
   renamed `RepairPersistenceTimeoutSeconds` because it cannot cancel the HTTP call, with the lib fix
   logged as tech debt; (c) **Phase 3 tests 17-18** (F3 branch fallback, positive and negative) and
   **19-20** (exactly one `GetDocumentDetail`; no duplicate rows on a repeat repair); (d)
   `EInvoiceHistoryWrite` stays internal with a separate public `EInvoiceHistoryWriteResult` on the
   service contract (D12).

---

# IMPLEMENTED 2026-09-21

All five phases shipped. Builds: `ErpWeb.Core`, `ErpWeb.UI`, `ErpWeb.Tests` — **0 errors**.
Tests: **26 new, all green** (`SaEInvoiceSubmissionRepairTests` 13, `EInvoicePortalLinkOpenerTests` 13).

## What shipped

| File | Change |
|---|---|
| `ErpWeb.Core/EInvoice/SaEInvoiceResults.cs` | `+ public enum EInvoiceHistoryWriteResult` (5 members); `SaEInvoiceResult` `+ HistoryWrite`, `+ SubmissionIdRecovered`; `EInvoiceOptions` `+ RepairPersistenceTimeoutSeconds (25)`; `+ SaEInvoiceSubmissionRepairResult` |
| `ErpWeb.Core/EInvoice/SaEInvoiceService.cs` | `RefreshAsync` recovers the submission id from the API and captures the registry outcome; `+ RepairSubmissionAsync` (resolver + single delegation to `RefreshAsync`); `+ ResolveKeyByUuidAsync`, `MapHistoryWrite`, `DescribeRepair`, `RepairFailed`, `ResolvedSubmissionKey` |
| `ErpWeb.Core/EInvoice/ISaEInvoiceService.cs` | `+ RepairSubmissionAsync` |
| `ErpWeb.Core/EInvoice/EInvoiceSubmissionWriter.cs` | **untouched** — `EInvoiceHistoryWrite` stayed internal (D12) |
| `ErpWeb.UI/Components/Common/EInvoicePortalLinkOpener.cs` | `+ OpenAndRepairAsync` (D10 invariant in the doc comment); `OpenAsync` extracted into `ResolveLinkAsync`/`OpenTabAsync` with its messages byte-identical; `EInvoicePortalOpenResult + StateChanged`; sample updated to the 5-line handler |
| `ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor.cs` | `onSelectColHandle` → `OpenAndRepairAsync` + conditional `ReloadGridAsync()` |
| `ErpWeb.Tests/ErpWeb.Tests.csproj` | `+ ProjectReference ErpWeb.UI` (see deviation 3) |
| `ErpWeb.Tests/SaEInvoiceSubmissionRepairTests.cs` | new (13 tests) |
| `ErpWeb.Tests/EInvoicePortalLinkOpenerTests.cs` | new (13 tests) |
| `docs/einvoice-history.md` | new §Repair on the E-UUID click: call chain, invariant, refusal table, `HistoryWrite` vs `Succeeded`, `Refresh` audit-row semantics, limitations |
| `plans/plan-einvDocSubmissionHistory.prompt.md` | `+ D-24` (the repair path and its narrow §3.5 exception) |

## Deviations from this plan's text

1. **F3 was changed, because the plan's own tests 17-18 contradicted each other.** Test 17 asked for "a
   row whose `BranchCode` differs from the caller's scope **still repairs**", while test 18 asked for
   "**does not write across branches**". Both cannot hold. The safe half was kept: the UUID is resolved
   **company-wide** (so the document is found rather than mis-reported as "not linked"), the tool then
   **refuses with a message naming the branch**, and nothing is written — matching this plan's own risk
   bullet ("if the row genuinely belongs to another branch the repair reports not-found rather than
   writing across branches"). As shipped:
   - `Repair_allows_a_legacy_row_with_no_branch_to_repair` — a blank `BranchCode` (every legacy row)
     carries no branch claim and repairs normally;
   - `Repair_does_not_write_across_branches` — asserts no MyInvois call, an untouched ERP document and a
     byte-identical registry row.
   No cross-branch write path was invented.
2. **`EInvoiceHistoryWriteResult` has five members, not three.** The review proposed
   `None/Inserted/Updated`. `Skipped` and `Failed` were kept distinct because the operator message
   differs ("the document has no submission id" vs "the history write failed"), and because
   `RefreshAsync` already returns `Skipped` for the no-key case — collapsing it into `None` would make a
   real, reportable state invisible.
3. **`ErpWeb.Tests` now references `ErpWeb.UI`.** It did not before, so plan item 25 (opener tests) was
   not achievable as written. `ErpWeb.UI` is a plain `net10.0` Razor class library, so the reference adds
   no runtime dependency and the suite builds unchanged. The csproj carries a comment: a green suite
   still does **not** prove a `.razor` page renders, so `dotnet build ErpWeb.slnx` after touching
   `ErpWeb.UI` remains mandatory.
4. **`StateChanged` is defined from the document, not only the history row.** It is true when the refresh
   applied a status **or** the row was inserted/updated — because the grid's E-Inv / E-Status columns
   come from `SaInvoice`/`SaCdn`, not from `EInvDocSubmission`. My first test asserted otherwise and was
   wrong: the code was right, so the test was split into
   `StateChanged_is_true_when_only_the_document_status_moved` and
   `StateChanged_is_false_when_nothing_was_applied_and_no_row_was_written`.
5. **Anchoring detail:** the new members were placed next to `GetPortalLinkAsync` (the other
   UUID-addressed method) rather than at the end of the service.

## Test-name index (as shipped)

`SaEInvoiceSubmissionRepairTests`: `Repair_inserts_the_missing_row_from_the_api_submissionUid`,
`Refresh_adopts_the_submission_uid_and_creates_the_missing_row`,
`Repair_updates_the_row_when_lhdn_changed_the_document`,
`Repair_does_not_erase_history_when_lhdn_returns_a_sparse_detail`,
`Repair_does_not_create_duplicate_history_rows_when_called_twice`,
`Repair_is_not_applicable_when_the_uuid_is_not_linked`, `Repair_refuses_without_submit_permission`,
`Repair_creates_no_row_when_neither_source_has_a_submission_id`,
`Repair_is_not_configured_for_self_billed`,
`Repair_allows_a_legacy_row_with_no_branch_to_repair`, `Repair_does_not_write_across_branches`,
`Repair_calls_GetDocumentDetail_exactly_once`, `Repair_works_for_credit_notes`.

`EInvoicePortalLinkOpenerTests`: `A_blank_uuid_opens_nothing_and_never_repairs`,
`A_uuid_column_click_is_recognised_by_field_name_only`,
`The_tab_opens_and_the_repair_runs_behind_it`,
`StateChanged_is_true_when_only_the_document_status_moved`,
`StateChanged_is_false_when_nothing_was_applied_and_no_row_was_written`,
`StateChanged_is_false_when_the_repair_was_not_attempted`,
`The_tab_still_opens_when_the_repair_throws`,
`A_blocked_popup_is_reported_but_the_repair_still_runs`,
`OpenAsync_keeps_its_original_behaviour_and_never_repairs`,
`The_link_is_retried_once_after_a_successful_repair`,
`The_repair_runs_at_most_once_when_the_link_stays_unresolvable`,
`A_resolve_failure_is_reported_without_spending_a_repair`,
`An_unresolvable_link_reports_both_halves_of_the_failure`.

## Still open (unchanged from the plan)

Manual browser smoke (plan Verification steps 5-6): delete a registry row for a VALID invoice, click the
E-UUID cell, confirm the row reappears with `LastSyncedOn` set, a `SaEInvoiceLog` `Refresh` row exists
and the grid refreshed. Also unverified by automation: the `SaInvoiceList` page handler itself (the
opener is tested, the page only calls it).

