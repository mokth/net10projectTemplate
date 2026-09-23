# Plan: Self-billed e-Invoice parity with the sales lists (E-STATUS + standard toolbar)

Make `PoSbInvoiceList` and `PoSbCdnList` behave — and read — exactly like `SaInvoiceList` / `SaCdnList`
for the e-Invoice surface. Two parts: (1) E-STATUS gains the missing no-selection **refresh-all** mode
with its progress strip and Stop button; (2) every toolbar / row-action / confirm caption is
standardised on the sales wording. No payload, party, credential or lifecycle changes.

The one accepted divergence afterwards: the self-billed lists have **no `POST` / `ROLLBACK`** — there is
no ERP finalisation step, because the e-Invoice state *is* the lifecycle. That is correct and must stay.

## Central acceptance criterion

For every e-Invoice control on `PoSbInvoiceList` / `PoSbCdnList`, the caption, icon, style, tooltip,
enabled rule and resulting behaviour must be identical to `SaInvoiceList` / `SaCdnList` — except that
POST / ROLLBACK do not exist on the self-billed lists.

**Sales is the standard; self-billed is the deviation.** Never "improve" the sales side to meet the
self-billed side.

## Gap inventory (measured against the code)

### A. Behaviour

| # | Sales | Self-billed | Ref |
|---|---|---|---|
| A1 | E-STATUS with nothing selected -> refresh **every** `SUBMITTED` row the grid shows, cap `SaEInvoiceLimits.MaxRefreshAllRun` (200) | `StatusMessage = "No Record Selected!"` | `SaInvoiceList.razor.cs` ~544-551 -> `BeginRefreshAllAsync` :679; `PoSbInvoiceList.razor.cs` ~328-332 |
| A2 | Progress strip + **Stop** button (`EInvoiceProgress`, `StopEInvoiceRefresh`) | absent | `SaInvoiceList.razor:23-30`; `SaCdnList.razor:24-30` |
| A3 | Selection kept when `FailedCount > 0 \|\| SkippedCount > 0` (retry just those) | always cleared | `ExecuteEInvoiceBatchAsync` both files |
| A4 | Permission checked **before** "No Record Selected!" | rows checked first | `BeginEInvoiceAsync` both files |
| A5 | Batch wrapped in `BeginBlockingWork` **and** `IsSubmitting = true` | blocking only; `IsSubmitting` never set on the e-Invoice path | `ConfirmActionAsync` both files |
| A6 | Token reaches only the refresh-all driver | token also passed into `RefreshManyAsync` / `SubmitManyAsync` / `CancelManyAsync` | `ExecuteEInvoiceBatchAsync` both files |
| A7 | E-UUID click -> shared `EInvoicePortalLinkOpener.HandleUuidClickAsync` | hand-rolled twice (`OnSelectedColumnHandle`, `OpenLhdnAsync`/`OpenPortalAsync`) | `SaInvoiceList.razor.cs:427` vs `PoSbInvoiceList.razor.cs:262, 295` |
| A8 | `LHDN` row action is **INVALID-only**, error otherwise | falls back to opening the portal for any status | same |

### B. Captions / icons / styles

| Control | Sales | Self-billed | Action |
|---|---|---|---|
| Toolbar submit | `SUBMIT` (`fas fa-paper-plane`, primary) | **`E-INV`** | rename -> `SUBMIT` |
| Toolbar submit tooltip | "Submit the selected invoices to MyInvois" | "…self-billed invoices…" | adopt sales string |
| Toolbar E-STATUS tooltip | "…selected invoices; with nothing selected, every submitted invoice in this branch" | "…selected invoices" | adopt sales string (after A1) |
| Toolbar `NEW` / `DELETE` / `E-STATUS` / `CANCEL` | — | — | already identical |
| Row `VIEW` / `EDIT` | — | — | already identical |
| Row `LHDN` icon | `fa-solid fa-triangle-exclamation` | `fas fa-up-right-from-square` | adopt sales |
| Row `LHDN` tooltip | "View the LHDN validation detail (INVALID documents only)" | "Open the LHDN document detail / portal" | adopt sales |
| Confirm submit style | `Primary` | **`Success`** | adopt sales |
| Confirm `Refresh` / `Cancel e-Invoice` / `Delete` | — | — | already identical |
| Grid captions `E-Inv` / `E-UUID` / `E-Status` | — | — | already identical |

`E-INV` lives in exactly two files, each in **four** places — missing one makes the button silently do
nothing, because the caption string *is* the `case` key.

### C. Internal action keys

Sales declares `private const EInvSubmitAction = "EINV_SUBMIT";` / `EInvStatusAction = "EINV_STATUS";`
/ `EInvCancelAction = "EINV_CANCEL";` and uses them throughout. Self-billed scatters the same literals
and mixes the grammar (`"E-INV"`, `"EINV_SUBMIT"`, `"SUBMIT"`, `"STATUS"`, `"CANCEL"` for the same three
actions). Standardise on the sales constants.

## Protected areas — DO NOT MODIFY

- Every e-Invoice payload concern: party direction, `EInvoiceSourceDocument`, `EInvoiceDocumentMapper`,
  `EInvoiceValidator`.
- `EInvoiceDocumentState.Supplier`, `ApplyCompanyCredentials`, submission grouping, `issuerTin`,
  `OriginUuid` / `RefDocumentNo`, `EInvoiceSubmissionWriter`.
- The lifecycle / state machine, `EInvoiceStatuses` semantics, authorization, credentials.
- `RunRefreshAllAsync`'s contracts: pre-flight cap, blank-`IRBMUUID` -> `Skipped` (never `Recover`),
  chunk boundary, partial aggregate on cancellation, per-chunk refusal -> remaining keys `Failed`.
  **The self-billed path consumes this driver unchanged — no new MyInvois code.**
- The sales pages. They are the reference; do not edit them to "help" the self-billed side.
- `SaEInvoiceLimits` values (`MaxBatchSelection = 10`, `MaxRefreshAllRun = 200`).
- Do **not** add POST / ROLLBACK to the self-billed lists, and `PoSbStatuses.Posted` must stay retired
  (the filter labels remain for legacy rows only).

## Phase 0 — Record the contract

Append a section to `docs/einvoice-history.md` (its "Not covered" paragraph lists exactly this work as
outstanding):

- the gap inventory above, with the acceptance criterion;
- the explicit statement that sales is the reference and `E-INV` was the deviation;
- that `RunRefreshAllAsync` is reused verbatim, so no new MyInvois call path is introduced;
- that the self-billed lists legitimately have no `POST` / `ROLLBACK`.

## Phase 1 — Core: refresh-all candidates for the self-billed families

**Decision required first: where does the candidate query live?**

The sales design's core invariant is *"what the grid shows" == "what gets refreshed"*, enforced by one
shared definition (`SaInvoiceQueryMapper` / `SaCdnQueryMapper` feeding the repository the grid uses). The
self-billed lists have **no repository** — `PoSbInvoiceService.SearchAsync` and
`PoSbCdnService.SearchAsync` query `AppDbContext` directly, and each already filters
`PoSbQuery.IrbmStatus` (`PoSbInvoiceService.cs:142-145`, `PoSbCdnService` likewise). `PoSbQuery` already
carries `SearchText`, `Status`, `IrbmStatus`, `Type`, `VendorCode`, `DateFrom/To`,
`SortField/SortDescending`, `Skip/Take` + `NormalizedPaging()`.

**Recommended — Option A (single definition, no new layers).** Extract the predicate set into a static
applier next to the existing mappers, e.g. `ErpWeb.Core/Purchase/PoSbQueryApplier.cs`:

```csharp
public static IQueryable<PoSbInvoice> Apply(IQueryable<PoSbInvoice> q, PoSbQuery query);
public static IQueryable<PoSbCdn>     Apply(IQueryable<PoSbCdn> q, PoSbQuery query);   // adds Type
```

`PoSbInvoiceService.SearchAsync` / `PoSbCdnService.SearchAsync` are rewired onto it (behaviour must not
change — pinned by the existing `PoSbServiceTests` list tests), and `SaEInvoiceService`'s new candidate
loader uses the same two methods. This mirrors `SaInvoiceQueryMapper` / `SaCdnQueryMapper` exactly and
keeps the invariant without inventing repositories.

*Alternatives, recorded:*
- **Option B** — inject `IPoSbInvoiceService` / `IPoSbCdnService` into `SaEInvoiceService` and call
  `SearchAsync`. Cycle-free (neither service depends on `ISaEInvoiceService`) but `SearchAsync` demands
  `PermissionCodes.Access`, so a Submit-only role would be refused the refresh-all — a real behavioural
  trap.
- **Option C** — new `IPoSbInvoiceRepository` / `IPoSbCdnRepository` in `ErpWeb.Model` mirroring
  `SaInvoiceRepository`. Architecturally closest to sales, but the largest blast radius (new files, DI,
  rewire both services) for a UI-parity feature.
- **Option A is chosen.**

**Steps**

1. Add `PoSbQueryApplier` (Option A) and rewire the two `SearchAsync` methods onto it. No behaviour
   change; the grid must keep filtering identically.
2. `ISaEInvoiceService` — add a third `RefreshSubmittedAsync` overload, documented as the self-billed
   twin:

   ```csharp
   Task<SaEInvoiceBatchResult> RefreshSubmittedAsync(
       ErpWeb.Core.Purchase.PoSbQuery? scope,
       IProgress<SaEInvoiceRefreshProgress>? progress = null,
       CancellationToken cancellationToken = default);
   ```

   Same XML-doc shape as the CN/DN overload: family and menu derive from the scope; the candidate set is
   the grid's own query; only `IrbmStatus` (pinned `SUBMITTED`) and paging are ours; the cap is
   pre-flight.
3. `SaEInvoiceService` — implement it:
   - `scope.Type` selects the family and the menu, exactly like the CN/DN overload: `CN` ->
     `SelfBilledCreditNote` + `MenuCodes.PurchaseSbCreditNote`; `DN` -> `SelfBilledDebitNote` +
     `MenuCodes.PurchaseSbDebitNote`; `SBI` -> `SelfBilledInvoice` + `MenuCodes.PurchaseSbInvoice`;
     blank/unknown -> refused as `Validation` (never thrown).
   - Branch scope, then a **single** `_accessRights.CanAsync(menuCode, PermissionCodes.Submit, ct)` gate
     before any query.
   - New `LoadSbRefreshCandidatesAsync(branchScope, scope, docType, ct)` beside
     `LoadCdnRefreshCandidatesAsync`: builds
     `PoSbQuery { Type, SearchText, Status, DateFrom, DateTo, IrbmStatus = EInvoiceStatuses.Submitted,
     SortField = DocNo, SortDescending = false, Skip,
     Take = Math.Min(MaxRefreshAllRun, PoSbLimits.MaxPageSize) }`, pages via `PoSbQueryApplier`, refuses
     over-cap with the same message shape ("Narrow the filter to 200 or fewer, then try again.") and
     **zero** MyInvois calls, projects `DocNo`/`IrbmStatus`/`IrbmUuid` into an anonymous type and maps in
     memory (EF cannot translate a named constructor).
   - Delegate to the existing `RunRefreshAllAsync(documentType, candidates, progress, ct)`.
4. Do **not** add a `Recover` button (the sales lists have none either).

## Phase 2 — UI: E-STATUS behaviour parity

Both `PoSbInvoiceList.razor.cs` and `PoSbCdnList.razor.cs`, mirroring the sales files:

5. In `BeginEInvoiceAsync`: check permission **first** (A4), then rows; when `rows.Count == 0` and the
   action is E-STATUS, call the new `BeginRefreshAllAsync()`; SUBMIT and CANCEL stay selection-only and
   still say "No Record Selected!" (A1).
6. Add `BeginRefreshAllAsync()` copied in shape from `SaInvoiceList.razor.cs:679`: blocking overlay,
   `IsSubmitting` / `IsEInvoiceBusy`, buttons disabled, fresh `_einvCts`,
   `Progress<SaEInvoiceRefreshProgress>` -> `EInvoiceProgress = "Refreshing e-Invoice status… {Done} of
   {Total} completed"`, call `EInvoices.RefreshSubmittedAsync(DataSource.CurrentQuery, progress,
   _einvCts.Token)`, handle `Refused`, the empty-candidate case ("No submitted document found..."),
   results popup + status line, then `ReloadGridAsync()`. **No confirmation popup** — a refresh is
   read-only (the sales comment says the same).
7. Add `EInvoiceProgress`, `StopEInvoiceRefresh()`, and cancel `_einvCts` in `Dispose()` (A2).
8. Add the progress strip markup to `PoSbInvoiceList.razor` / `PoSbCdnList.razor`, byte-identical to
   `SaInvoiceList.razor:23-30`.
9. `ExecuteEInvoiceBatchAsync`: keep the selection when `FailedCount > 0 || SkippedCount > 0` (A3); set
   `IsSubmitting` for the run so the popup's Cancel is genuinely disabled (A5); stop passing
   `_einvCts.Token` into `RefreshManyAsync` / `SubmitManyAsync` / `CancelManyAsync` (A6).
10. Replace the hand-rolled UUID click with the shared opener (A7) — the file's own doc-block shows the
    exact four-line handler:

    ```csharp
    protected async Task OnSelectedColumnHandle(SelectedColumnInfo info)
    {
        var row = info.Context as PoSbInvoiceListRow;
        var outcome = await EInvoicePortalLinkOpener.HandleUuidClickAsync(
            EInvoices, JsRuntime, info, row?.IrbmStatus, CanAccessEInvoiceTin);
        if (outcome.NavigateUrl is not null) { Navigation.NavigateTo(outcome.NavigateUrl); return; }
        if (outcome.Message is not null) { ErrorMessage = outcome.Message; return; }
        var result = outcome.Portal!;
        if (result.Opened) { ErrorMessage = null; StatusMessage = result.Message; }
        else { ErrorMessage = result.Message; }
        if (result.StateChanged) { await ReloadGridAsync(); }
    }
    ```

    Delete `OpenLhdnAsync` / `OpenPortalAsync` and make the `LHDN` row action use
    `EInvoiceDetailLink.Resolve` INVALID-only like sales (A8 — the same change A8 needs).

## Phase 3 — UI: standard captions

11. `PoSbInvoiceList.razor.cs` — change all four `"E-INV"` literals to `"SUBMIT"`: the `Buttons` entry
    (~:157), the `IsEInvoiceBusy` re-entry guard (~:187), the `OnButtonClick` switch (~:199), and
    `SetEInvoiceButtonsEnabled` (~:543).
12. `PoSbCdnList.razor.cs` — same four edits (~:167, :201, :213, :571).
13. Adopt the sales tooltips verbatim for `SUBMIT`, `E-STATUS` (now with the "with nothing selected…"
    clause) and `LHDN`; adopt the `LHDN` icon `fa-solid fa-triangle-exclamation`.
14. `ConfirmButtonStyle`: `"EINV_SUBMIT"` -> `ButtonRenderStyle.Primary` in both files (~:75 / :72).
15. Introduce the sales action constants in both files and replace the scattered literals:

    ```csharp
    private const string EInvSubmitAction = "EINV_SUBMIT";
    private const string EInvStatusAction = "EINV_STATUS";
    private const string EInvCancelAction = "EINV_CANCEL";
    ```

    and pass them (`BeginEInvoiceAsync(EInvStatusAction)`) instead of the ad-hoc `"STATUS"` / `"CANCEL"` /
    `"SUBMIT"` mix. `ConfirmAction` values then match sales exactly.
16. *(Optional, layout only — confirm before doing it.)* Move `E-Inv` to `VisibleIndex = 4` so the column
    order matches sales; captions are already identical. If done, `VisibleIndex` must stay unique across
    every column in the grid.

## Phase 4 — Tests

17. `ErpWeb.Tests/EInvoiceTestHost.cs`
    - `CreateService()` (:210) gains the new ctor args if Option C were taken — under Option A there is
      none; **but** extend the host either way:
    - add an `irbmStatus` parameter to `SeedSbCdnAsync` (it currently accepts only `originIrbmStatus`;
      `SeedSbInvoiceAsync` already has `irbmStatus`);
    - add bulk seed helpers mirroring `SeedSubmittedInvoicesAsync` / `SeedSubmittedCreditNotesAsync`,
      e.g. `SeedSubmittedSbInvoicesAsync(count, docNoFactory)` and a CN/DN twin, so the cap/chunk tests
      have volume.
18. New `ErpWeb.Tests/SaEInvoiceSbRefreshAllTests.cs`, mirroring `SaEInvoiceBatchTests`' refresh-all tests:
    - candidates come from the **grid's own query** — a search-text filter narrows the run;
    - the e-Invoice status is pinned to `SUBMITTED` (a `VALID` or blank-status row is never touched);
    - **cap is pre-flight**: `MaxRefreshAllRun + 1` matching rows -> refused, message names both numbers,
      **zero** MyInvois calls; exactly `MaxRefreshAllRun` rows -> runs;
    - progress reports `Total == candidate count` before the first chunk;
    - blank `IRBMUUID` -> `Skipped` with `Missing IRBMUUID`, never sent, never escalated to `Recover`;
    - chunking: detail calls ÷ `MaxBatchSelection` (deterministic boundary);
    - cancellation between chunks returns the partial aggregate;
    - **family isolation**: a `CN` run never touches a `DN` row and vice versa; an `SBI` run never touches
      a note;
    - **menu isolation**: the `SBI`/`SBC`/`SBD` runs require `MenuCodes.PurchaseSbInvoice` /
      `PurchaseSbCreditNote` / `PurchaseSbDebitNote` + `PermissionCodes.Submit`; a Submit-only role (no
      `Access`) **is authorized** — this is the test that would have caught Option B;
    - unknown/blank `Type` -> `Validation` refusal, not a throw;
    - branch scoping: another branch's `SUBMITTED` rows are not candidates.
19. `PoSbQueryApplier` regression: the existing self-billed list tests stay green **unedited**, proving the
    refactor changed no filter semantics.
20. Caption/behaviour guard (UI-level, if the suite has any page-level tests — if not, pin it in the
    findings doc instead): `SUBMIT` / `E-STATUS` / `CANCEL` are the only e-Invoice toolbar captions on all
    four lists.

## Verification

1. `dotnet build ErpWeb.slnx` -> 0 errors.
2. `dotnet test` (ErpWeb.Tests): the new self-billed refresh-all suite green; the CN/DN and invoice
   refresh-all suites **unedited** and green; the self-billed payload suite green. The 15 known
   pre-existing failures must not grow.
3. Manual, per list (SBI, SBC, SBD): with rows selected, E-STATUS pre-flights and reports per row exactly
   as sales; with **nothing** selected it refreshes every submitted row in the current filter, shows
   `x of y completed`, the **Stop** button halts at the next chunk boundary keeping completed work, and
   the results popup lists each row.
4. Side-by-side caption check: `PoSbInvoiceList` vs `SaInvoiceList`, `PoSbCdnList` vs `SaCdnList` — every
   toolbar caption, icon, style and tooltip identical, minus `POST`/`ROLLBACK`.
5. Negative: over-cap filter -> refusal, no MyInvois traffic (confirm in the log / `EInvDocSubmission`).
6. Confirm the e-Invoice payload for a self-billed document is **byte-identical** before/after — this
   change must not touch the payload (see the separate `plan-selfBilledPartyReversal.prompt.md` for the
   party work).

## Decisions recorded

- Sales is the standard; self-billed conforms. Never the reverse.
- The toolbar submit caption becomes `SUBMIT`; `E-INV` is retired (both files, all four occurrences each).
- The no-selection E-STATUS mode is implemented for **all three** self-billed families by reusing
  `RunRefreshAllAsync` — no new MyInvois code.
- Candidate resolution uses a shared `PoSbQueryApplier` (Option A), preserving "grid query == refreshed
  set" without introducing repositories or a service-to-service dependency.
- The refresh-all is gated on the family's `PurchaseSb*` menu + `PermissionCodes.Submit`, and must **not**
  require `PermissionCodes.Access`.
- Refresh is read-only -> no confirmation popup; SUBMIT / CANCEL stay selection-only.
- The UUID click goes through the one shared `EInvoicePortalLinkOpener.HandleUuidClickAsync`; the
  duplicated handlers are deleted.
- POST / ROLLBACK are not added, and POSTED stays retired.

## Further considerations

1. **Scope creep risk.** A1-A8 plus B is a real change set. If you want it staged: **Phase 2 + 3 first**
   (pure UI, instantly visible, no Core change), then Phase 1 + 4. Phase 3 alone is a ~30-minute,
   zero-risk change.
2. **`EInvoiceProgress` duplication.** Three lists now copy `BeginRefreshAllAsync` verbatim. Worth
   extracting a shared `EInvoiceRefreshAllState` helper — but deliberately **out of scope** here, because
   refactoring the sales pages is exactly what this plan forbids.
3. **Option A is a refactor of a working page.** The self-billed `SearchAsync` is currently correct;
   extracting `PoSbQueryApplier` is the one place a regression could hide. Its guard is the existing
   unedited list tests — if those aren't adequate, add list-filter tests **before** the extraction.
4. If a future `Recover` button is ever added, it must be added to sales first; the self-billed lists must
   not lead.
