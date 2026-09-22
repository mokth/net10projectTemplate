# Plan: LHDN e-Invoice Document Detail page (INVALID diagnosis)

## Problem

An INVALID invoice / CN / DN shows only a status chip in the UI. LHDN's actual reason
(`validationResults.validationSteps[].error`) is fetched by `GetDocumentDetail(uuid)` and then thrown
away.

Evidence chain:

| Layer | File:Line | Keeps | Drops |
| --- | --- | --- | --- |
| HTTP + parse | `ErpWeb.EInvoiceLib/Repository/E_InvoiceRepository.cs:1677` | full `DocumentValidatation`, incl. `validationResults` | — |
| Helper passthrough | `ErpWeb.EInvoiceLib/SubmitDoc/SubmitDocumentHelper.cs:312` | same object | — |
| Service | `ErpWeb.Core/EInvoice/SaEInvoiceService.cs:421` (`RefreshAsync`) | `status`, `dateTimeValidated`, `submissionUid` | **`validationResults`**, totals, dates, parties |
| Registry write | `ErpWeb.Core/EInvoice/EInvoiceSubmissionWriter.cs:381` (`ApplyDetail`) | uuid, longId, typeName, parties, 3 dates, totals | **`validationResults`** (no column exists) |
| View model | `ErpWeb.Core/EInvoice/SaEInvoiceResults.cs` (`SaEInvoiceStatusView.Error`) | the audit-log string only | LHDN detail |
| UI | `ErpWeb.UI/Sales/Transactions/SaEInvoicePanel.razor:35-40` | `_status.Error` + history rows | LHDN detail |

`GetDocumentDetail(uuid)` has exactly **one** production caller today: `RefreshAsync`. `EInvDocSubmission`
has no column for validation results, so the data is fetched live and discarded every time.

## Recommended approach

- New read-only, UUID-addressed `ISaEInvoiceService.GetDocumentDetailAsync(uuid, ct)` returning a
  Core-owned view model.
- New Blazor page `/sales/einvoice/detail/{Uuid}` that renders the full payload, above all LHDN's
  validation steps.
- A per-row "LHDN" action on the two sales list grids, plus an INVALID-only link in each grid's mobile
  compact row.
- A **pure** `DocumentValidatation` → view-model mapper, unit-testable without a database or host.
- No ERP writes, no schema change, no change to `ErpWeb.EInvoiceLib`.

## Decisions (confirmed with user)

- **Menu code: reuse `MenuCodes.SalesEInvoiceTin`** (`"SA_EINVOICE_TIN"`). Zero new artefacts (no
  `menus.xml` row, no seed script, no `MenuDeploymentParityTests` churn). Consequence: the page and the
  grid buttons require `SA_EINVOICE_TIN` ACCESS.
- **Entry points: grid row actions** in `SaInvoiceList` **and** `SaCdnList` (CN + DN) only. The
  `SaEInvoicePanel` button is deferred (Resolved decision 1).
- **`ErpWeb.EInvoiceLib` left alone.** `E_InvoiceRepository.getDocumentDetail` keeps collapsing
  `error.details[]` into `code + details[0].message`, so a non-200 response shows one line only.
- **No persistence.** `EInvDocSubmission` gets no new column; the page is always a live call.
- **The service and page are general-purpose; only the grid entry point is INVALID-only.**
  `GetDocumentDetailAsync(uuid, ct)` and the page render any MyInvois status (SUBMITTED / VALID /
  INVALID / CANCELLED), and handle `validationResults == null`. The *button* is gated to INVALID rows
  because LHDN's own documentation mandates it (see below) — this is a deliberate policy gate, not an
  architectural limitation of the page.
- **Never call this API for status polling.** LHDN's Get Document Details page states verbatim:
  *"Use the Get Document Details API only to retrieve error details or exceptions in invalid
  documents. For checking submission status, utilise the 'Get Submission API' with a polling
  frequency of 3-5 seconds. Excessive requests for the same document may result in throttling."*
  The existing status refresh path (`RefreshAsync` / E-STATUS) is unaffected and stays as-is; the new
  page must be reachable only by explicit operator action — never prefetched, never looped, never
  refreshed on a timer.

### LHDN Get Document Details — API facts that constrain the design

Source: <https://sdk.myinvois.hasil.gov.my/einvoicingapi/08-get-document-details> and
<https://sdk.myinvois.hasil.gov.my/standard-error-response/>.

- **Signature** `GET /api/v1.0/documents/{uuid}/details` — matches `E_InvoiceRepository.getDocumentDetail`.
- **Rate limit** 125 requests/minute per client ID. `429 TooManyRequests` is returned past the limit
  with a `Retry-After` header holding the seconds to wait.
- **Issuer vs receiver visibility.** *"Receiver of the documents can retrieve documents that are in
  'Valid' or 'Cancelled' status. If document exists, and is issued to given receiver, but status is
  'submitted' or 'invalid', not found code will be returned. Issuer of the documents can retrieve
  documents in any status."* ErpWeb issues the documents, so any status resolves — but a `NotFound`
  response is a legitimate outcome that must render as a clear message, not as a raw error code.
- **`longId` is only returned for valid documents** — it is the value `SaEInvoicePortalLink` needs, so
  an INVALID document legitimately has no portal share link.

## Steps

### Phase 1 — Core contract + view model

1. New result types alongside `ErpWeb.Core/EInvoice/SaEInvoiceResults.cs` (or a new
   `SaEInvoiceDetailResults.cs`), mirroring the `SaEInvoiceTinSearchResult` conventions: `sealed`,
   `{ get; init; }` properties, static `Ok` / `Fail` factories, `IReadOnlyList<T> { get; init; } = [];`

   - `SaEInvoiceDetailResult` — `Succeeded`, `ErrorKind` (`SaEInvoiceErrorKind`), `ErrorMessage`,
     `ErrorCode`, `Detail` (`SaEInvoiceDetailView?`)

   `ErrorCode` is copied from `GeneralResult<DocumentValidatation>.errorCode`. **Note the trap:**
   `E_InvoiceRepository.getDocumentDetail` never assigns `errorCode` on any failure path — it only sets
   `error` (see step 3). So `ErrorCode` will be `null` in practice today, exactly as it already is for
   `SaEInvoiceResult.ErrorCode` read at `SaEInvoiceService.cs:425`. Carry the property anyway so the
   contract is honest and becomes useful if the lib is ever fixed; do **not** build logic that requires
   it.
   - `SaEInvoiceDetailView` — uuid, submissionUid, longId, internalId, typeName, typeVersionName,
     issuerTin/Name, receiverId/Name, the four dates plus `cancelDateTime` / `rejectRequestDateTime`,
     the four totals, `Status`, `DocumentStatusReason`, `CreatedByUserId`, `ValidationStatus`, `Steps`
   - `SaEInvoiceValidationStep` — `Name`, `Status`, `Issues`
   - `SaEInvoiceValidationIssue` — `PropertyPath`, `PropertyName`, `ErrorCode`, `Error`, `ErrorMs`,
     `MetaData`, `InnerErrors`

   `DocumentValidatation` (an `ErpWeb.EInvoiceLib` type) must **not** leak into the UI.

   **`Error` vs `ErrorMs` — the single most important naming clarification.** `Ms` is **not**
   "milliseconds": it is **Bahasa Melayu (Malay)**. `Error` is the English message, `ErrorMs` is the
   same message in Malay. LHDN occasionally writes the property as `errorMS`, and the repo model names
   it `ErrorMs`; Newtonsoft's property matching is case-insensitive so either binds.

   **Exact source mapping — copy this table verbatim into the mapper:**

   | View model property | Source (`DocErrorDetail` / `InnerErrorMsg`) | Type | Nullable | Display rule |
   | --- | --- | --- | --- | --- |
   | `PropertyName` | `PropertyName` | `string` | yes | Show as a chip/label when non-blank |
   | `PropertyPath` | `PropertyPath` | `string` | yes | Primary locator; show verbatim, monospace |
   | `ErrorCode` | `ErrorCode` | `string` | yes | Machine-handled code, e.g. `Error04`, `CV317` |
   | `Error` | `Error` | `string` | yes | **Primary human-readable message (English)** |
   | `ErrorMs` | `ErrorMs` | `string` | yes | **Secondary message (Malay)** — collapsed by default |
   | `MetaData` | `MetaData` | `string` | yes | Undocumented passthrough; render only when non-blank |
   | `InnerErrors` | `InnerError` | `List<InnerErrorMsg>` | yes | Child issues — usually the actionable ones |

   Notes the implementer must not guess at:

   - Neither `Error` nor `ErrorMs` is truncatable — both can contain a full sentence plus an SDK URL
     (`https://sdk.myinvois.hasil.gov.my/documentvalidationrules/code-validator-error/#CV317`).
   - Both may be null; `PropertyPath` and `PropertyName` are documented as *"might have the value null
     if the property path cannot be resolved"*. Every field is nullable — never assume a non-null string.
   - `MetaData` does **not** appear in LHDN's documented error structure. It exists in the repo model as
     `string`. Treat it as an optional opaque passthrough and never parse it.
   - `target` exists in the documented structure but is **absent from `DocErrorDetail`** — do not invent it.
   - `typeVersionNumber` is returned by the API but is **absent from `DocumentValidatation`**. Do not add
     it; the view model carries `typeVersionName` only.

   **`innerError` recursion — state the real limit.** LHDN documents `innerError` as *"list of multiple
   errors ... Each Error object is of the same structure as defined in this table that allows
   composition of multiple errors received"*, i.e. it is **recursive in the API**. The repo model is
   **not** recursive: `DocErrorDetail.InnerError` is typed `List<InnerErrorMsg>`, and
   `InnerErrorMsg.InnerError` is typed `object`. So the mapper expands **exactly one level**. Do not
   write a recursive mapper — it cannot bind beyond depth 1, and a deeper tree would silently produce
   `object` nodes. If a third level is ever needed, that is a model change in `ErpWeb.EInvoiceLib`
   (typed as `JToken`/`JsonElement`), which is out of scope here.

   **The actionable error is usually the child.** Per LHDN's own example, a step whose status is
   `Invalid` can carry a step-level summary (`errorCode: "Error04"`,
   `error: "Step04-Invalid Code Field Validator"`) with the real per-field issue nested in
   `innerError[]` (`errorCode: "CV317"`, `propertyPath: /ubl:Invoice/...`, full English + Malay
   messages). The UI must therefore render the step-level issue **and** its children, with children
   visually subordinated — showing only the step level reproduces today's "I can't tell what's wrong"
   problem.

2. Add `Task<SaEInvoiceDetailResult> GetDocumentDetailAsync(string uuid, CancellationToken cancellationToken = default)`
   to `ErpWeb.Core/EInvoice/ISaEInvoiceService.cs`, slotted after `SearchTinAsync` (L230-234), using the
   `GetPortalLinkAsync` (L64-78) doc-comment style — state that it is UUID-addressed, company-scoped,
   read-only, authorized with `SA_EINVOICE_TIN` ACCESS, and writes no audit row.

### Phase 2 — Service implementation  (depends on 1-2)

3. Implement in `ErpWeb.Core/EInvoice/SaEInvoiceService.cs`, in the read-only region near the TIN
   methods (L1645-1752).

   **Authorization** — copy `AuthorizeTinToolsAsync` (L1734-1752):

   - `_tenant.TryCompanyScope()`; null → `Fail("Invalid company context.", Authorization)`
   - `_accessRights.CanAsync(MenuCodes.SalesEInvoiceTin, PermissionCodes.Access, ct)`; false → `Fail("Not authorized.", Authorization)`
   - `ApplyCompanyCredentials(scope, await LoadSupplierAsync(db, scope.CompanyCode, ct))`

   **Call** — `_helper.GetDocumentDetail(uuid.Trim())` inside `try/catch (Exception ex)` →
   `_logger.LogError(...)` + `Fail(ex.Message, SaEInvoiceErrorKind.MyInvois)`, mirroring
   `ValidateTinAsync` (L1645-1679). Blank uuid → `Fail(..., Validation)` before any MyInvois call.

   **Failure classification — best-effort, because the lib discards the code.** The service maps a
   failed `GeneralResult` to a `SaEInvoiceDetailResult` as follows:

   - A thrown exception (transport) → `ErrorKind.MyInvois` with `"MyInvois could not be reached."`
   - A non-success result whose message contains `notfound` / `not found` (case-insensitive) →
     `ErrorKind.NotFound`, message `"MyInvois has no detail for this document UUID. It may belong to
     another taxpayer, or the submission may not have been accepted."`
   - A non-success result whose message contains `toomanyrequests` / `429` → `ErrorKind.MyInvois`, message
     advising the operator to wait and retry manually.
   - Anything else → `ErrorKind.MyInvois`, message surfaced from the API.

   This substring matching is unavoidable and is a direct consequence of the "leave
   `ErpWeb.EInvoiceLib` alone" decision: `E_InvoiceRepository.getDocumentDetail` flattens the response
   to `code + " " + details[0].message` (or just `code` when there are no details) and never populates
   `GeneralResult.errorCode`. **Do not invent a stronger contract than the data supports**, and put the
   classification in one small `private static` helper on the service so it is obvious where to change it
   if the lib is ever fixed to expose `errorCode`.

   **Read-only invariant** — **no** `AppendLogAsync`, **no** `ApplyStatusAsync`, **no**
   `SaveChangesAsync`. That is the whole point of the `GetPortalLinkAsync` / TIN-method precedent.

   **Mapping — a pure function in its own file, unit-testable in isolation.** The transformation lives
   in a new `ErpWeb.Core/EInvoice/SaEInvoiceDetailMapper.cs`: an `internal static class
   SaEInvoiceDetailMapper` with `internal static SaEInvoiceDetailView Map(DocumentValidatation detail)`
   plus small private helpers (`MapStep`, `MapIssue`). This mirrors the existing pure-mapper precedent,
   `SaEInvoiceStatusMap` (`ErpWeb.Core/EInvoice/EInvoiceStatuses.cs:143`).

   `internal` (not `private`) is deliberate: `ErpWeb.Core.csproj:15` already declares
   `<InternalsVisibleTo Include="ErpWeb.Tests" />`, so the mapper is directly testable without making
   it part of the public Core surface. **Do not** put the mapper as a `private` method inside
   `SaEInvoiceService` — that would force the mapper tests to go through `EInvoiceTestHost`, which is
   exactly what this split is meant to avoid.

   Requirements:

   - **No** database access, **no** authorization check, **no** logging, **no** state mutation, **no**
     `ILogger`, **no** `DbContext`, **no** `_helper`, **no** `_tenant`. It takes a `DocumentValidatation`
     and returns a `SaEInvoiceDetailView`; nothing else.
   - Fully synchronous — no `async`, no `Task`.
   - Null-safety throughout: `validationResults == null` → `Steps = []`; `validationSteps == null` →
     `Steps = []`; `step.error == null` → `Issues = []` (a step can be `Valid` with no error object);
     `InnerError == null` → child issues `[]`.
   - Expand exactly one level of `InnerError`, per the limit documented in step 1.
   - A step whose status is `Invalid` but whose `error` is null must still render as a step with no
     issues — never dropped, never reported as a failure.
   - Preserve `validationSteps` order; map every step, never `.First()`.
   - The authorization check, the `_helper` call and its `try/catch` stay in the **service method**,
     which simply calls `SaEInvoiceDetailMapper.Map(detail.result)` on a successful response.

4. No DI change — `ISaEInvoiceService` is already registered scoped
   (`EInvoiceServiceCollectionExtensions.cs:51`).

### Phase 3 — The page  (parallel with 5-7 once step 1 is frozen)

5. New `ErpWeb.UI/Sales/Transactions/SaEInvoiceDetail.razor`, modelled on `SaEInvoiceTin.razor`:

   - `@page "/sales/einvoice/detail/{Uuid}"`, `@inherits PageBase`, `<PageTitle>`,
     `<MenuAuthorize MenuCode="@MenuCode">` wrapping the body
   - `MenuCode => MenuCodes.SalesEInvoiceTin`
   - Root `<div class="iv-page">` → `<header class="iv-hero">` → `<section class="iv-card">`
   - **Card 1 (the point):** `validationResults.status` plus **one block per step — iterate all of
     `Steps`, never `.First()`**. Each step renders its name/status and a table of
     `PropertyName | PropertyPath | ErrorCode | Error (English) | MetaData`, with the Malay `ErrorMs`
     shown as a collapsed secondary line and child `InnerErrors` indented beneath the parent row.
     Render each column only when at least one issue in the block has a non-blank value for it.
   - **Card 2:** document header / parties / dates / totals via `iv-detail-grid` + `iv-detail-row` +
     `iv-detail-row__label` / `iv-detail-row__value` (all exist, `inventory-chrome.css:344`). Card 2
     renders for **every** status, including when Card 1 has nothing to show.
   - Errors surfaced with the `iv-toast` / `iv-toast--err` pattern (page-local `StatusMessage`).

   **Explicit UI state machine — the implementer must not invent alternative states.** The page is in
   exactly one of these at any time:

   | State | Trigger | What renders |
   | --- | --- | --- |
   | Loading | `IsBusy == true` during load | `iv-skeleton` block, no tables, no toasts |
   | Success, issues found | `Succeeded`, `Steps` has ≥1 issue, `ValidationStatus == "Invalid"` | Card 1 with the issue tables, Card 2 below |
   | Success, no issues | `Succeeded`, `Steps` is empty or has no issues (`validationResults == null` is normal for SUBMITTED / VALID) | Card 1 shows an explicit **"No validation errors were returned by MyInvois."** empty state, Card 2 still renders. **Never** render a fake/blank error row. |
   | MyInvois failure | `!Succeeded`, `ErrorKind == MyInvois` | `iv-toast--err`: `"MyInvois could not return this document's details: {ErrorMessage}"`. No Card 1. |
   | Authorization failure | `!Succeeded`, `ErrorKind == Authorization` | `iv-toast--err` with the service's message (`"Not authorized."` / `"Invalid company context."`). No Card 1. |
   | Validation failure | `!Succeeded`, `ErrorKind == Validation` (blank uuid) | `iv-toast--err` with the service message. No MyInvois call was made. |
   | Not found at MyInvois | `!Succeeded`, `ErrorKind == NotFound` (best-effort classification) | `iv-toast--err` reading `"MyInvois has no detail for this document UUID. It may belong to another taxpayer, or the submission may not have been accepted."` |
   | Throttled | `!Succeeded`, message indicates `TooManyRequests` / `429` | `iv-toast--err` advising the operator to wait and retry manually. No auto-retry. |

   The failure message text lives in the service (one place), not the page; the page only decides which
   toast/empty state to show. `NotFound` and throttling are classified from the API message (step 3) —
   the page must still render *some* error toast for any other `!Succeeded` result, never a blank screen.

6. `SaEInvoiceDetail.razor.cs`:

   - `[Inject] private ISaEInvoiceService EInvoices`
   - `[Parameter] public string? Uuid`
   - Load in `OnPageInitializedAsync` (the base `PageBase.OnInitializedAsync` calls it), with the house
     `if (IsBusy) return;` guard and `try/catch/finally`
   - Page-local `StatusMessage` / `StatusIsError` + `DismissStatus` — `PageBase` has **no**
     `DismissError`
   - Needs its own `using ErpWeb.Core.EInvoice;` and `using ErpWeb.Core.Menus;` — the Sales
     `_Imports.razor` does not import them

7. `SaEInvoiceDetail.razor.css` with a page-local `eid-*` prefix, per the convention comment in
   `SaEInvoiceTin.razor.css`. Note `.iv-table` and `.iv-mono` **do not exist** — define `eid-table`
   (copy the `.tin-table` shape) and `eid-mono` (copy `.einvoice__mono`).

8. No `menus.xml` or seed-script change — the route is not a menu row.

### Phase 4 — Entry points  (depend on step 5 for the route; 9 and 10 parallel)

9. `ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor.cs`:

   - Add a third `ActionButtons` entry to the array at L165-169
   - Handle `"LHDN"` in `OnActionClick` (L209-240) beside the EDIT branch:
     - blank `IrbmUuid` → `ErrorMessage = "This document has no MyInvois UUID."`
     - `EInvoiceStatuses.Normalize(row.IrbmStatus) != EInvoiceStatuses.Invalid` → error message,
       mirroring the EDIT branch's `"Only NEW invoices can be edited."` (L229-233). **This gate is
       intentional, not a limitation** — LHDN directs that this API be used only for invalid-document
       error details (see Decisions). Wording: `"LHDN detail is available only while the e-Invoice
       status is INVALID."`
     - `Navigation.NavigateTo($"/sales/einvoice/detail/{Uri.EscapeDataString(uuid)}")`
   - `Enabled = CanAccessEInvoiceTin`, a new `bool` set from
     `AccessRights.CanAccessAsync(MenuCodes.SalesEInvoiceTin)` in `OnPageInitializedAsync` (near
     L152-159). `ButtonInfo.Enabled` is static (`DataGridModel.cs:29`) so it cannot vary per row.
   - **Mobile — the compact list must not be silently left out.** The `iv-list-compact` branch
     (`SaInvoiceList.razor:104-125`) renders no row actions, so without a change a phone user sees
     `INVALID` with no way to find out why. Add a tappable link inside the existing row button: the
     `row.Status` chip becomes an `<a>`-styled span for INVALID rows only, routing to the same
     `/sales/einvoice/detail/{Uri.EscapeDataString(row.IrbmUuid)}` URL, and rendered only when
     `CanAccessEInvoiceTin` and `IrbmUuid` is non-blank. Keep the whole compact row's primary tap
     behaviour (navigate to the invoice) unchanged; the link is a distinct tap target.

10. `ErpWeb.UI/Sales/Transactions/SaCdnList.razor.cs` — the same change; `ActionButtons` at L224,
    `OnActionClick` at L273, plus the same compact-list link in `SaCdnList.razor`. Serves both CN and
    DN because the route is UUID-addressed and type-agnostic — the same reason
    `ISaEInvoiceService.GetPortalLinkAsync` is document-type agnostic.

### Phase 5 — Tests

11. New `ErpWeb.Tests/SaEInvoiceDetailTests.cs` on `EInvoiceTestHost` (SQLite in-memory, real validator
    and mapper, `FakeSubmitDocumentHelper` standing in for MyInvois). Seed the response with
    `host.Helper.DocumentDetailHandler = _ => FakeSubmitDocumentHelper.Success(new DocumentValidatation { ... })`.

    Plus a small **pure-mapper** test class (`SaEInvoiceDetailMapperTests`) calling
    `SaEInvoiceDetailMapper.Map(...)` directly with no host and no database — the mapper is pure and
    `internal` with `InternalsVisibleTo ErpWeb.Tests`, so this costs almost nothing and covers the shape
    edge cases:

    - `Mapper_maps_a_step_with_no_error` — a `Valid` step with `error == null` still appears in `Steps`
      with `Issues` empty (never dropped, never reported as a failure).
    - `Mapper_handles_null_validation_results` — `validationResults == null` → `Steps` empty, no throw.
    - `Mapper_handles_null_validation_steps` — `validationResults.validationSteps == null` → `Steps` empty.
    - `Mapper_expands_only_one_level_of_inner_error` — a two-level `innerError` tree yields the first
      level only, pinning the documented `DocErrorDetail` / `InnerErrorMsg` limit.
    - `Mapper_keeps_error_and_error_ms_distinct` — English `Error` and Malay `ErrorMs` are not
      conflated, and both survive intact including an embedded SDK URL.
    - `Mapper_preserves_step_order` — steps come out in `validationSteps` order.

    Service-level tests:

    - `Detail_maps_validation_steps_and_messages` — pin that `PropertyName` / `PropertyPath` / `ErrorCode`
      / `Error` reach the view model, including one inner error carrying the actionable `CV317` code
    - `Detail_maps_multiple_validation_steps` — send **three** steps with different outcomes
      (`Valid`, `Invalid` with an error, `Invalid` with `error == null`) and assert all three appear in
      order. Catches an accidental `validationSteps.First()` (reviewer item 5).
    - `Detail_handles_valid_document_without_validation_results` — `status = "Valid"` and
      `validationResults = null`: assert the call **succeeds**, no exception is thrown, `Steps` is
      empty, no synthetic issue is invented, and the document fields (uuid, totals, dates) are still
      populated. Guards the normal VALID/SUBMITTED path against future null-handling regressions
      (reviewer item 4).
    - `Detail_reports_not_found_distinctly` —
      `Failure<DocumentValidatation>("NotFound", errorCode: null)` surfaces
      `ErrorKind.NotFound` and the actionable "no detail for this document UUID" message rather than a
      raw code (LHDN returns not-found for receiver-side lookups on submitted/invalid documents).
    - `Detail_classifies_a_throttled_response` — `Failure<DocumentValidatation>("TooManyRequests")`
      produces the wait-and-retry message, not a generic failure.
    - `Detail_calls_GetDocumentDetail_exactly_once` — mirror `SaEInvoiceSubmissionRepairTests.cs:398-415`.
      Also asserts **no self-retry on failure**: one click, one MyInvois call, because of the throttling
      guidance in Decisions.
    - `Detail_writes_no_audit_row_and_does_not_change_the_status` — assert `LogsAsync()` is empty and
      the seeded `IRBMStatus` is unchanged. **This is the key read-only pin.**
    - `Detail_fails_without_the_tin_tools_access_right` —
      `host.DeniedPermissions.Add(PermissionCodes.Access)`, then
      `Assert.DoesNotContain(nameof(FakeSubmitDocumentHelper.GetDocumentDetail), host.Helper.Calls)`
    - `Detail_reports_a_myinvois_failure` — `Failure<DocumentValidatation>("...")`
    - `Detail_rejects_a_blank_uuid` — nothing sent to MyInvois

## Relevant files

- `ErpWeb.Core/EInvoice/SaEInvoiceResults.cs` (or new `SaEInvoiceDetailResults.cs`) — new result types
- `ErpWeb.Core/EInvoice/ISaEInvoiceService.cs` — new member (slot after L230-234)
- `ErpWeb.Core/EInvoice/SaEInvoiceDetailMapper.cs` — **new**, pure mapper (`internal static`)
- `ErpWeb.Core/EInvoice/EInvoiceStatuses.cs:143` (`SaEInvoiceStatusMap`) — the pure-mapper precedent
- `ErpWeb.Core/ErpWeb.Core.csproj:15` — `InternalsVisibleTo ErpWeb.Tests` (why the mapper can be `internal`)
- `ErpWeb.Core/EInvoice/SaEInvoiceService.cs` — new method; templates: `ValidateTinAsync` L1645,
  `AuthorizeTinToolsAsync` L1734, `RefreshAsync` L383-421, `GetPortalLinkAsync` L1255
- `ErpWeb.UI/Sales/Transactions/SaEInvoiceDetail.razor` (+ `.razor.cs`, `.razor.css`) — **new**
- `ErpWeb.UI/Sales/Transactions/SaEInvoiceTin.razor` (+ `.razor.cs`, `.razor.css`) — the page template
- `ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor.cs` — L165-169, L209-240, ~L152-159
- `ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor` — `iv-list-compact` branch, L104-125 (mobile link)
- `ErpWeb.UI/Sales/Transactions/SaCdnList.razor.cs` — L224, L273
- `ErpWeb.UI/Sales/Transactions/SaCdnList.razor` — its `iv-list-compact` branch (mobile link)
- `ErpWeb.Tests/SaEInvoiceDetailTests.cs` — **new** (service-level)
- `ErpWeb.Tests/SaEInvoiceDetailMapperTests.cs` — **new** (pure-mapper tests, no host)
- `ErpWeb/wwwroot/css/inventory-chrome.css` — reference only (`iv-detail-grid` L344, etc.)
- `ErpWeb.Core/Services/ValidationMessageFormat.cs` — reference only; not used for LHDN issues

**Not modified:** `ErpWeb.EInvoiceLib/**`, `ErpWeb/Menus/menus.xml`, `scripts/**`,
`EInvDocSubmission` entity/config, `EInvoiceSubmissionWriter`, `EInvoicePortalLinkOpener`,
`SaEInvoicePanel`, the E-UUID cell click, `RefreshAsync` / the E-STATUS path, `ISubmitDocumentHelper`,
`IE_InvoiceRepository`

## Verification

1. `dotnet build ErpWeb.slnx` clean.
2. `dotnet test --filter "FullyQualifiedName~SaEInvoiceDetailTests"`.
3. `dotnet test --filter "FullyQualifiedName~SaEInvoice"` — proves `SaEInvoiceSubmissionRepairTests` and
   `SaEInvoiceBatchTests` (which assert `GetDocumentDetail` call counts) see no new caller on their
   paths.
4. `dotnet test --filter "FullyQualifiedName~MenuDeploymentParityTests"` still green (no new menu code).
5. Manual, sandbox (`Einvoice:EInv_Url`): submit a document LHDN rejects on a field → the grid shows
   INVALID → row action LHDN → the page shows the step name, and for the failing step the child
   `PropertyPath`, `ErrorCode` (e.g. `CV317`) and the **English** `Error` text; confirm the Malay
   `ErrorMs` is available but collapsed.
6. Manual negative: on a VALID row the LHDN action reports the "only INVALID" message and calls
   nothing (`host.Helper.Calls` / network tab shows no request).
7. Manual negative: open a VALID document's detail URL directly — Card 1 shows the "No validation
   errors were returned by MyInvois." empty state, Card 2 still renders, no fake error row.
8. Manual throttling check: confirm a single click produces exactly **one** `details` request in the
   browser network tab, and that no timer/refresh/`OnParametersSet` path re-issues it. This is the
   guard for the 125 RPM limit and LHDN's excessive-request throttling warning.

## Resolved decisions and scope boundary

The four previously-open items are now closed:

1. **Panel entry point — DEFERRED (out of scope).** `SaEInvoicePanel` is where an operator sees INVALID
   on the entry screen and it already holds `_status.Uuid`, so adding a link there is ~6 lines — but it
   needs a new `[Inject] NavigationManager` (the panel derives from `ComponentBase`, not `PageBase`).
   The grid entry points already cover the workflow, so the panel is a follow-up, not part of this
   change.
2. **Mobile — IN SCOPE, no longer deferred.** Added to step 9 / 10: an INVALID-only link in the
   `iv-list-compact` row so a phone user who sees `INVALID` can reach the reason. Leaving this out
   would mean shipping a diagnosis feature that a whole form factor cannot reach.
3. **E-UUID cell — DEFERRED, untouched.** Its click is pinned to open-the-portal-and-repair ("at most
   ONE repair and at most ONE link retry per click", guarded by `EInvoicePortalLinkOpenerTests`). Any
   modifier-click route to the detail page is a deliberate contract change and belongs in its own task.
4. **`CancellationToken` on the HTTP call — DEFERRED (out of scope).** `GetDocumentDetail(string)` takes
   no `CancellationToken` (`ISubmitDocumentHelper.cs:14`), so the page cannot abort mid-HTTP. Pre-existing
   logged tech debt (`SaEInvoiceResults.cs:38`, `plans/plan-einvDocSubmissionRepair.prompt.md` §Risks 4).
   Fixing it would drag `ISubmitDocumentHelper` and `IE_InvoiceRepository` into this task for no
   functional gain here. The page's own `CancellationTokenSource` still bounds the post-call work.

## Scope boundary

**In scope:** the read-only service method, its view model and pure mapper, the detail page, the two
list-grid entry points (desktop row action + mobile compact link), and the tests listed in Phase 5.

**Explicitly out of scope:** `ErpWeb.EInvoiceLib` (including the lossy `error.details[]` collapse in
`E_InvoiceRepository.getDocumentDetail`), `EInvDocSubmission` schema, `EInvoiceSubmissionWriter`,
`EInvoicePortalLinkOpener`, `SaEInvoicePanel`, the E-UUID cell click, `RefreshAsync` and the E-STATUS
path, `menus.xml` and the seed scripts, and the `GetDocumentDetail` cancellation-token signature.

## Open question for the reviewer

1. **Cache the detail for the session?** LHDN's throttling warning is about *excessive requests for the
   same document*. A single click makes one call, so we are well inside the limit — but an operator
   fixing a rejected invoice may navigate back and forth several times. Options: (a) no caching, one
   call per page load (simplest, matches the plan as written); (b) hold the last result in a
   scoped/session cache keyed by uuid; (c) rely on the operator reloading deliberately. Recommend (a)
   for v1 — the call is cheap and the warning targets polling, not clicks.
2. **Should the detail page be able to Trigger a corrective action?** Today it is strictly read-only,
   which the read-only tests pin. If an operator's next step after seeing `CV317` is "edit the invoice",
   a link back to the invoice entry page would be useful — but it must be an obvious separate action, and
   it does not belong in this read-only slice.
