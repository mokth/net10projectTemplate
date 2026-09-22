# Plan: e-Invoice parity for Sales Credit/Debit Note list pages

## TL;DR

Bring the e-Invoice feature that already ships on `SaInvoiceList` (`/sales/invoices`) to the shared Sales Credit/Debit Note list (`SaCdnList`, routes `/sales/credit-notes` and `/sales/debit-notes`).

The **backend is already CN/DN-aware** — `SaEInvoiceService` derives `MenuCodes.SalesCreditNote` / `MenuCodes.SalesDebitNote` from the document type, loads a `SaCdn` source document, enforces `cdn.Type == CN|DN`, and the E-UUID repair path already falls back to `db.SaCdns`. The CN/DN **entry** page already hosts `<SaEInvoicePanel>`.

So this is *not* a new integration. What is missing is:

1. **List-row visibility** — `E-Inv` / `E-UUID` / `E-Status` columns (the row DTO has no e-Invoice fields at all).
2. **Batch actions on the list** — `SUBMIT` / `E-STATUS` / `CANCEL`, the confirm reason, the per-row results popup, and the E-UUID → LHDN portal click.
3. **No-selection `E-STATUS` (refresh-all)** — currently an invoice-only service path.
4. **Permissions** — `SA_CN` / `SA_DN` have no `SUBMIT` / `CANCEL` `MenuPermission` rows, so even the shipped entry-page panel is effectively admin-only. Granting the permission is **not sufficient on its own**: a role grant and a non-admin verification are part of the deliverable (see *Deployment runbook* in Phase D).
5. **One pre-existing list defect** to fix in the same pass: the `Lines` column is always `0`.

> **Review pass applied 2026-09-22.** Five findings were folded into this plan and are marked inline as **[R1]–[R5]**: `IrbmStatus` duplication clarification (D-2), limits-extraction regression proof, the permissions deployment runbook, explicit failure semantics plus failure-path tests, and the CN/DN adaptation checklist.

## Behavioural contracts

These are the parts an implementation usually gets subtly wrong. They are normative: the steps must satisfy them, and the Phase E tests exist to pin them.

| # | Contract |
|---|---|
| C1 | **The service layer is reused, never re-implemented.** No page may call `IE_InvoiceRepository` / `ISubmitDocumentHelper`; every action goes through `ISaEInvoiceService`. |
| C2 | **One filter definition.** The grid and the refresh-all must resolve `SaCdnListQuery` through `SaCdnQueryMapper`; the refresh-all applies the grid's query **verbatim** and only adds `IrbmStatus = SUBMITTED` plus its own paging. |
| C3 | **Candidate set == what the user sees.** Company + branch + the scope's `Type` (CN or DN) + `IrbmStatus = SUBMITTED` + the supplied filter. A CN refresh must never touch a DN row, and vice versa. |
| C4 | **The status predicate is plain equality.** `SaCDN.IRBMStatus` and the live database are `SQL_Latin1_General_CP1_CI_AS`; wrapping the column in `UPPER()`/`ToUpper()` would kill the index for no gain. A title-case legacy value is documented, never "fixed" with a function. |
| C5 | **The cap is decided before the first MyInvois call.** Candidates are counted first; over-cap refuses with **zero** calls. |
| C6 | **Progress is completed-of-total candidates**, reported once before the first chunk. |
| C7 | **Cancellation is honoured at chunk boundaries only** — the in-flight chunk finishes, no further chunk starts, and every item already produced is returned. |
| C8 | **Blank-`IRBMUUID` candidates never enter `RefreshManyAsync`** — they become `Skipped` items with reason `Missing IRBMUUID` (an asserted string; do not reword). |
| C9 | **Authorization is per-menu.** CN work is gated on `SA_CN`, DN on `SA_DN` — the page checks its own `MenuCode`, and the service re-checks server-side before any query. |
| C10 | **A refresh-all run is read-only.** It never submits, cancels, recovers or retries. |
| C11 | **`DxGridDataColumn.FieldName` is resolved by property name at render time.** Every e-Invoice column needs its DTO property added *first* (Phase A), or the grid throws when it renders. Every `VisibleIndex` must be unique. |
| C12 | **[R4] A failure never discards completed work and never leaks an exception to the page.** One document failing inside a chunk does not stop the rest of that chunk or any later chunk; an exception that escapes the batch primitive is converted into `Failed` items for the remaining keys and the partial aggregate is returned. |
| C13 | **[R5] No invoice-family token may survive in the CN/DN path.** The port is a copy *plus* an adaptation, proved by the Phase C0 checklist and the verification grep — not by "it compiles". |

### Failure semantics (normative, [R4])

These are the intended behaviours for a long-running batch, stated **before** implementation so the Phase E tests can pin them. They apply to the shared driver, so both the invoice and the CN/DN refresh-all inherit them.

| Situation | Intended behaviour | Where enforced |
|---|---|---|
| One candidate fails inside a chunk (MyInvois error, validation refusal, transport failure, or a document that no longer qualifies) | That item is reported `Failed`/`Skipped`; the **remaining documents in the same chunk still run**, and every later chunk still runs. One bad document never aborts a run. | `RefreshManyAsync`'s per-document `try`/`catch` — unchanged. |
| A whole chunk is refused (authorization revoked mid-run, or the interactive cap is exceeded) | Every remaining key — including the current chunk's — becomes a `Failed` item carrying the refusal reason; the loop ends; the partial aggregate is returned. Refused chunks are never silently dropped. | The existing `chunkResult.Refused` branch — unchanged. |
| An unexpected exception escapes the batch primitive (a DB failure, or the EF `OperationCanceledException` that a pre-cancelled token raises from the candidate query) | **NEW**: the shared driver catches it once per chunk, converts the current chunk's remaining keys **and every remaining chunk's keys** into `Failed` items, breaks the loop, and returns the partial aggregate. It must never propagate to the page, because propagating discards work already done. | New guard in `RunRefreshAllAsync` (Phase B step 7). |
| Cancellation combined with a failing chunk | Deterministic: the token is observed only **between** chunks, so a chunk's own failures are reported normally and the partial aggregate contains chunk 1's results, then chunk 2's, in chunk order. The same input always produces the same output. | C7 + the new guard. |
| Cancellation **before the first chunk** (the candidate query aborts) | The `OperationCanceledException` may escape the candidate load — there is no partial work to lose at that point. The page's existing `catch (Exception ex) { ErrorMessage = ex.Message; }` reports it. This is the documented behaviour from the invoice refresh-all; **do not** try to "fix" it by swallowing the token. | Documented, not coded. |

## Architecture

```
SaCdnList (/sales/credit-notes, /sales/debit-notes)
   |
   +-- E-UUID cell click
   |      -> EInvoicePortalLinkOpener.OpenAndRepairAsync(ISaEInvoiceService, IJSRuntime, uuid)
   |           -> GetPortalLinkAsync  (portal link)
   |           -> RepairSubmissionAsync(uuid)  (<=1 repair, <=1 link retry per click)
   |
   +-- SUBMIT / CANCEL  (selection required)
   |      -> GetStatusManyAsync(keys)      pre-flight preview only, never the verdict
   |      -> confirm popup (+ mandatory reason for CANCEL)
   |      -> SubmitManyAsync / CancelManyAsync(keys, reason)
   |
   +-- E-STATUS with a selection
   |      -> same confirm + RefreshManyAsync(keys)
   |
   +-- E-STATUS with NO selection  ->  refresh-all
          -> RefreshSubmittedAsync(SaCdnListQuery scope)      <- NEW overload
                -> authorize (SA_CN or SA_DN, derived from scope.Type) + Submit
                -> SaCdnQueryMapper.ToSearchArgs(scope) verbatim   (C2)
                -> pin IrbmStatus = SUBMITTED, order by DocNo asc
                -> count candidates (cap + 1)                      (C5)
                -> over cap? refuse, ZERO MyInvois calls
                -> blank UUID -> Skipped "Missing IRBMUUID"        (C8)
                -> chunk by MaxBatchSelection, per chunk: RefreshManyAsync
                -> report (done, total)                            (C6)
                -> cancelled between chunks? return partial        (C7)
```

## Implementation

### Phase A — Query capability (foundation; blocks B and C)

1. `ErpWeb.Core/Sales/ISaCdnService.cs`
   - Add `string? IrbmStatus` to `SaCdnListQuery`, copying the invoice doc comment (it is the single definition shared by the grid filter and the refresh-all candidate query).
   - Add `IrbmStatus` / `IrbmOutcome` / `IrbmUuid` to `SaCdnListRow`, copying `SaInvoiceListRow` (`ISaInvoiceService.cs:210-235`).
   - **[R1] Three properties, not four.** `E-Inv` and `E-Status` are *both* bound to `IrbmStatus` on the invoice list (`SaInvoiceList.razor.cs:129` and `:136`, the second with `DataType = "string"`); `E-UUID` is `IrbmUuid` (`:133`). There is **one** MyInvois status value on the row, rendered twice. No "submission exists" property is bound anywhere, so do not invent one here. The duplication is deliberate and mirrored — see **D-2** in *Decisions* and the confirmation step in Phase C.
2. **New** `ErpWeb.Core/Sales/SaCdnQueryMapper.cs` — public static `ToSearchArgs(SaCdnListQuery? query, int skip, int take)`, mirroring `SaInvoiceQueryMapper`: blank → `null` for `SearchText`, `Status`, `IrbmStatus`; pass `Type` through. This absorbs the mapping currently inlined at `SaCdnService.SearchAsync:308` (exactly one production call site).
3. `ErpWeb.Model/Repositories/Sales/SaCdnRepository.cs`
   - Add `string? IrbmStatus` to the `SaCdnSearchArgs` positional record.
   - Add the predicate inside `SearchPagedAsync` next to the `Status` filter: `query = query.Where(x => x.IrbmStatus == irbmStatus);` — plain equality (C4).
4. `ErpWeb.Core/Sales/SaCdnService.cs` → `SearchAsync`
   - Build the args through `SaCdnQueryMapper`.
   - Project `IrbmStatus`, `IrbmOutcome`, `IrbmUuid` onto each `SaCdnListRow`.
   - **Fix the `Lines` defect**: replace `LineCount = x.Details?.Count ?? 0` (always `0`, because the repository query never `.Include`s `Details`) with an explicit grouped count over `SaCdnDetails` scoped to company + branch + the page's doc numbers, mirroring `SaInvoiceService.SearchAsync:339-352`.

### Phase B — Service: CN/DN refresh-all (depends on A)

5. **[R2] New** `ErpWeb.Core/EInvoice/SaEInvoiceLimits.cs`
   - `MaxBatchSelection = 10`, `MaxRefreshAllRun = 200`.
   - Change `SaInvoiceLimits.MaxEInvoiceBatchSelection` / `MaxEInvoiceRefreshAllRun` (`ErpWeb.Core/Sales/SaInvoiceCalc.cs`) to forward to the new constants, so no existing call site or test changes and CN/DN code does not depend on an invoice-named type.
   - **Forwarding is a behaviour contract, so it is asserted, not assumed.** Add a test (Phase E step **15c**) proving `SaInvoiceLimits.MaxEInvoiceBatchSelection == SaEInvoiceLimits.MaxBatchSelection` and `SaInvoiceLimits.MaxEInvoiceRefreshAllRun == SaEInvoiceLimits.MaxRefreshAllRun`, so a future edit to one constant cannot silently split the two.
   - **Do not edit any existing invoice refresh-all test to make this work.** `ErpWeb.Tests/SaEInvoiceBatchTests.cs`'s refresh region (cap boundary, grid-scope preservation, chunking, blank UUID, cancellation) must pass **unchanged** — that is the evidence for the "byte-identical invoice behaviour" claim in step 7. If one of them needs editing, the refactor changed behaviour and the refactor is wrong.
6. `ErpWeb.Core/EInvoice/ISaEInvoiceService.cs` — add the overload in the Batch operations region:
   `Task<SaEInvoiceBatchResult> RefreshSubmittedAsync(SaCdnListQuery scope, IProgress<SaEInvoiceRefreshProgress>? progress = null, CancellationToken cancellationToken = default);`
   Document it as the CN/DN counterpart of the invoice refresh-all: read-only (C10), the scope's `Type` selects the document family and the authorizing menu (C9), the cap is pre-flight (C5), progress is completed-of-total (C6), cancellation is chunk-boundary only (C7).
7. `ErpWeb.Core/EInvoice/SaEInvoiceService.cs`
   - Rename the private `RefreshCandidate` property `InvNo` → `DocumentNo` (the record is private to this file, so the rename is safe and stops the CN/DN path reading an "invoice").
   - Extract the existing chunk driver into `RunRefreshAllAsync(string documentType, IReadOnlyList<RefreshCandidate> candidates, IProgress<SaEInvoiceRefreshProgress>?, CancellationToken)`. The existing invoice entry point delegates to it — **behaviour must be byte-identical for every path an existing test covers**.
   - **[R4] The one deliberate change to that shared driver**: wrap each `RefreshManyAsync` chunk call in a `try`/`catch`. On an unexpected exception, convert the current chunk's remaining keys **and every remaining chunk's keys** into `Failed` items, break the loop, and return the partial aggregate — never let it escape to the page (see *Failure semantics*). This is additive: no existing test exercises an escaping exception, so the invoice refresh-all tests must still pass untouched. Update the invoice overload's XML comment to describe the guard, since it now covers both families.
   - Add `LoadCdnRefreshCandidatesAsync(TenantScope branchScope, SaCdnListQuery scope, string docType, CancellationToken)`: pins `IrbmStatus = SUBMITTED`, `SortField = nameof(SaCdn.DocNo)`, ascending, pages with `SaCdnRepository.MaxPageSize` (100 < the 200 cap, so paging is required), and refuses over-cap after one query.
   - Implement the new overload in this order: validate `scope.Type` ∈ {`SaCdnTypes.CreditNote`, `SaCdnTypes.DebitNote`} and **refuse** (never throw) → derive the menu code (`SA_CN`/`SA_DN`) and the `EInvoiceDocumentTypes` token → `_tenant.TryBranchScope()` → gate once on that menu + `PermissionCodes.Submit` **before any query or MyInvois call** → load candidates → `RunRefreshAllAsync`.
   - Update the existing "This entry point is invoice-only" XML comment on the invoice overload, which is no longer true.
8. Make the shared batch messages family-neutral: `"Select at least one document."` and `"Select at most {n} documents per e-Invoice action."` (`SaEInvoiceService.cs:1385` and `ValidateBatchSelection`). No test asserts either string — verified. `Missing IRBMUUID` **must stay verbatim** (`SaEInvoiceBatchTests.cs:757`).

### Phase C0 — CN/DN adaptation checklist ([R5]; do this BEFORE step 9, it is a gate)

Phase C is a copy **plus an adaptation**. A "verbatim" copy that keeps an invoice assumption compiles perfectly and then sends the wrong document type, so the audit is explicit and its result is recorded in the PR description.

**C0.1** — Confirm **D-2** (the `E-Inv` / `E-Status` duplication) with the owner. If they want `E-Inv` to mean something else (e.g. "submitted or beyond", `EInvoiceStatuses.IsSubmittedOrBeyond`), that is a *different* value needing a new `SaCdnListRow` property and a change to Phase A step 1. **Decide now, not during Phase C.** Default: mirror the invoice list.

**C0.2** — Audit every copied reference against this table before writing code:

| Invoice construct in `SaInvoiceList` | CN/DN replacement |
|---|---|
| `SaInvoiceListRow` | `SaCdnListRow` |
| `row.InvNo` (document key) | `row.DocNo` |
| `row.InvDate` | `row.DocDate` |
| `SaInvoiceStatuses.New` / `.Posted` | `SaCdnStatuses.New` / `.Posted` |
| `SaInvoiceLimits.MaxEInvoiceBatchSelection` | `SaEInvoiceLimits.MaxBatchSelection` |
| `SaInvoiceListQuery` | `SaCdnListQuery` (which also carries `Type`) |
| `SaInvoiceGridDataSource` | `SaCdnGridDataSource` |
| `SaInvoiceService` / `Invoices` | `ISaCdnService` / `Cdns` |
| `SaInvoiceOperationResult`, `PostAsync(nos)` | `SaCdnOperationResult`, `PostAsync(keyed)` |
| `EInvoiceDocumentTypes.Invoice` | `CreditNote` / `DebitNote`, from the page's `_type` |
| `MenuCodes.SalesInvoice` | the page's `MenuCode` property (`SA_CN` / `SA_DN`) |
| routes `/sales/invoices/…` | `NewRoute` / `ViewRoute(docNo)` / `EditRoute(docNo)` |
| `NavigateView(row.InvNo)` | `NavigateView(row.DocNo)` |
| `GridKey` = `sa-invoice-list`, `KeyName` = `InvNo` | `GridKey` property, `KeyName` = `DocNo` |
| "Invoice(s) deleted." and other invoice wording | "Document(s) deleted.", `DocumentTypeName`, `TotalCountLabel` |
| search placeholder / filter title | `SearchPlaceholder` / `FilterTitle` |

**C0.3** — Grep gate. After step 9, this must return **zero** hits for the forbidden tokens:

```
SaInvoiceListRow|SaInvoiceStatuses|SaInvoiceLimits|SaInvoiceListQuery|SaInvoiceGridDataSource|SaInvoiceOperationResult|SaInvoiceService|MenuCodes.SalesInvoice|EInvoiceDocumentTypes.Invoice
```

**Allowed exception:** `SaCdnListRow.InvNo` / `row.InvNo` **must stay** — the CN/DN row legitimately carries the referenced *source invoice* number, and the grid's "Invoice" column binds it. Do not "clean" it away; that would break the source-reference column.

### Phase C — UI parity: `SaCdnList` (depends on A, B and C0)

Port `SaInvoiceList.razor(.cs)` — the shipped, verified implementation — rather than designing a new shape, then apply the C0 checklist.

9. `ErpWeb.UI/Sales/Transactions/SaCdnList.razor.cs`
   - Inject `ISaEInvoiceService EInvoices` and `IJSRuntime JsRuntime`.
   - Fields/state: `_einvCts`, `IsEInvoiceBusy`, `EInvoiceCancelReason`, `EInvoiceProgress`, `EInvoiceResultsVisible`, `EInvoiceResultsTitle`, `EInvoiceResults`, `CanSubmitEInv`, `CanCancelEInv`; the `EInvSubmitAction` / `EInvStatusAction` / `EInvCancelAction` constants and `IsEInvoiceAction`.
   - Permissions from the page's own `MenuCode` property (already CN/DN-aware): `AccessRights.CanAsync(MenuCode, PermissionCodes.Submit)` and `(…, PermissionCodes.Cancel)` (C9).
   - Columns: insert `E-Inv` (`IrbmStatus`, index 4), `E-UUID` (`IrbmUuid`, `DataType = "link"`, index 9), `E-Status` (`IrbmStatus`, `DataType = "string"`, index 10), renumber `Lines` to 11 and change the audit block to `AuditColumns.For(startVisibleIndex: 12)`. Every index must be unique and every `FieldName` must exist on the DTO (C11).
     **[R1]** `E-Inv` and `E-Status` intentionally render the **same** `IrbmStatus` value twice, exactly as the invoice list does — the only difference is the explicit `DataType = "string"` on the latter. This is a mirrored legacy display artefact, **not** two data sources. Add a short comment above the two columns saying so, or the next reader will "fix" one of them into a wrong binding. Confirm D-2 (Phase C0.1) before writing this block.
   - Toolbar: add `SUBMIT` / `E-STATUS` / `CANCEL` with `Enabled = CanSubmitEInv` / `CanCancelEInv` and the invoice tooltips; `OnButtonClick` gains the re-entry guard (`IsEInvoiceBusy && mode is "SUBMIT" or "E-STATUS" or "CANCEL"` → return) and the three new cases.
   - Extend `ConfirmButtonText`, `ConfirmButtonStyle`, `CanConfirmAction` (reason required for cancel) for the three e-Invoice actions.
   - `ConfirmActionAsync`: route e-Invoice actions to `ExecuteEInvoiceBatchAsync()` before the POST/ROLLBACK/DELETE path.
   - Add `onSelectColHandle` verbatim from the invoice page (`EInvoicePortalLinkOpener.IsUuidColumn` → `OpenAndRepairAsync` → success/error toast → reload only when `result.StateChanged`).
   - Add `BeginEInvoiceAsync(action)`: distinct, trimmed, de-duplicated selection; no selection + `E-STATUS` ⇒ `BeginRefreshAllAsync()`; no selection + `SUBMIT`/`CANCEL` ⇒ "No Record Selected!"; cap at `SaEInvoiceLimits.MaxBatchSelection`; `GetStatusManyAsync` pre-flight preview; `EInvoiceIneligibleReason` using `SaCdnStatuses.Posted` and the CN/DN key.
   - Add `ExecuteEInvoiceBatchAsync`, `BeginRefreshAllAsync` (passing `DataSource.CurrentQuery`, which carries `Type`), `StopEInvoiceRefresh`, `SetEInvoiceButtonsEnabled`, `DismissEInvoiceResults`, `EInvoiceResultOutcome` / `EInvoiceResultClass`.
   - `ToEInvoiceKey(row)` maps the page's `_type` to `EInvoiceDocumentTypes.CreditNote` / `DebitNote` with `row.DocNo.Trim()`.
   - `Dispose()` also cancels and disposes `_einvCts`.
10. `ErpWeb.UI/Sales/Transactions/SaCdnList.razor`
    - Grid gains `OnSelectedColumnHandle="@onSelectColHandle"`.
    - Progress strip above the hero, reusing `iv-toast iv-toast--wait` plus the `iv-einv-progress__stop` Stop button.
    - Confirm popup gains the conditional cancellation-reason block (required, max 300 characters) and binds `Enabled="@CanConfirmAction"`.
    - New results popup (table of `DocumentNo` / outcome chip / `Status` / `ErrorMessage`).
    - No new CSS: `iv-einv-chip`, `iv-einv-results__table` and `iv-einv-progress__stop` already exist in `ErpWeb/wwwroot/css/inventory-chrome.css`.

### Phase D — Permissions (parallel with C)

11. **New** `scripts/init-sales-cdn-einvoice-permissions.sql` — idempotent `INSERT … WHERE NOT EXISTS` of `MenuPermission` rows (and `RoleMenuPermission` grants kept out of scope) for `SUBMIT` + `CANCEL` × `SA_CN`, `SA_DN`, following the house MERGE-style pattern. The `Permission` rows themselves already exist (`scripts/init-menu-access.sql:108,114`).
12. Add `N'SUBMIT', N'CANCEL'` to the `SA_CN`/`SA_DN` permission list at `scripts/init-menu-access.sql:333`, so a **fresh** database matches a migrated one (the create-vs-alter drift rule).
13. No `menus.xml` change is needed — both menus already exist — so `MenuDeploymentParityTests` is untouched.

### Deployment runbook — permissions ([R3]; the feature is NOT shippable without this)

A `MenuPermission` row makes a permission *available*; it grants it to nobody. Without the steps below the buttons render **disabled** for every non-admin, the shipped entry-page panel stays locked, and the feature looks broken while being technically complete. This is deliberately kept out of the SQL script (hard-coding role names into a migration is worse than a documented manual step), but it is **not optional**.

| # | Step | Evidence it is done |
|---|---|---|
| 1 | Identify the roles that may submit/cancel e-Invoices for credit and debit notes (ask the deployment owner — do **not** guess from `RoleCode` naming). | A named list of roles, recorded in the PR/handover. |
| 2 | Insert one `dbo.RoleMenuPermission` row per (role × menu × permission): `RoleId`, `MenuId` for `SA_CN`/`SA_DN`, `PermissionId` for `SUBMIT`/`CANCEL`, `IsAllowed = 1`. **The column is `IsAllowed`, not `IsActive`** — writing `IsActive` here is a hard `Msg 207` (a defect that already happened once in this repo). | The row counts below. |
| 3 | Verify as a **non-admin** user in each role: the three toolbar buttons are enabled on the CN and DN lists, and the entry-page `SaEInvoicePanel` buttons are enabled. | Screenshot or a manual smoke note. |
| 4 | Verify a user **without** the grant sees the actions disabled (not a server error), and that the service still refuses server-side if a crafted request is sent anyway (C9). | Manual smoke note. |

Verification queries (read-only, `-d ERPWeb`):

```sql
-- 2. the grants themselves
SELECT m.MenuCode, p.PermissionCode, r.RoleCode, rmp.IsAllowed
FROM dbo.RoleMenuPermission rmp
JOIN dbo.Menu m ON m.MenuId = rmp.MenuId
JOIN dbo.Permission p ON p.PermissionId = rmp.PermissionId
JOIN dbo.Role r ON r.RoleId = rmp.RoleId
WHERE m.MenuCode IN (N'SA_CN', N'SA_DN') AND p.PermissionCode IN (N'SUBMIT', N'CANCEL');

-- 4. roles that can read the menu but still cannot submit — this is the "looks broken" set
SELECT m.MenuCode, r.RoleCode
FROM dbo.RoleMenuPermission rmp
JOIN dbo.Menu m ON m.MenuId = rmp.MenuId
JOIN dbo.Role r ON r.RoleId = rmp.RoleId
WHERE m.MenuCode IN (N'SA_CN', N'SA_DN')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.RoleMenuPermission x
      JOIN dbo.Permission p ON p.PermissionId = x.PermissionId
      WHERE x.RoleId = rmp.RoleId AND x.MenuId = rmp.MenuId
        AND p.PermissionCode = N'SUBMIT' AND x.IsAllowed = 1);
```

### Phase E — Tests (depends on B and C wording)

14. `ErpWeb.Tests/EInvoiceTestHost.cs`
    - Give `SeedCreditNoteAsync` optional `irbmStatus` / `irbmUuid` parameters.
    - Add a batched `SeedSubmittedCreditNotesAsync(count, docNo, type, uuid, companyCode, branchCode)` mirroring `SeedSubmittedInvoicesAsync` — the per-row helper does a context, a lookup and a save each and is far too slow for 100/200-row candidate sets.
15. **New** `ErpWeb.Tests/SaEInvoiceCdnBatchTests.cs` (or a new region in `SaEInvoiceBatchTests.cs`), one test per contract:
    - **C3 type isolation** — SUBMITTED CNs refresh; SUBMITTED DNs in the same company/branch are untouched, and the reverse.
    - **C2 grid-scope preservation** — 100 SUBMITTED notes plus a filter matching 12 ⇒ exactly 12 refreshed. This is the test that catches a re-derived filter.
    - **C5 cap boundary** — exactly 200 succeeds; 201 is refused with **zero** MyInvois calls (assert on `FakeSubmitDocumentHelper.Calls`, not only on the result object).
    - **Chunking** — 25 candidates ⇒ batches of 10 / 10 / 5, never one over-cap call.
    - **C8 blank UUID** — `Skipped` with reason `Missing IRBMUUID`, and the UUID never reaches `DocumentDetailHandler`.
    - **C7 chunk-boundary cancellation** — cancel during chunk 2 ⇒ every chunk-1 item is present, chunk 3 never runs, the partial aggregate is returned.
    - **C9 authorization** — no `SA_CN` submit right ⇒ refused before any call; a DN run is gated by `SA_DN`, not `SA_CN`.
    - **Validation** — a blank or unknown `scope.Type` is refused as validation, with no MyInvois call.
15b. **[R4] Failure-path tests** (the cases the contract table defines; assert the *result composition*, not just "no exception"):
    - **one item fails, the rest continue** — chunk of 10 where document 3 fails ⇒ 9 outcomes plus one `Failed`, and the run does not stop.
    - **a failing chunk does not stop the next chunk** — chunk 1 contains a failure, chunk 2 still executes and its items are present.
    - **an escaping exception is converted, not propagated** — make the batch primitive throw (the existing `FakeSubmitDocumentHelper` seams support a throwing handler) ⇒ the call returns a `SaEInvoiceBatchResult` whose remaining keys are `Failed`, **no exception escapes**, and the items produced before the throw are retained (C12).
    - **cancellation + failure is deterministic** — cancel during chunk 2 while chunk 1 contained a failure ⇒ the same aggregate every run: chunk 1's results in order, then chunk 2's, then nothing.
    - **a refused chunk converts the remainder** — a mid-run authorization loss turns the current chunk *and* every remaining key into `Failed` items carrying the refusal reason; none are dropped.
15c. **[R2] Limits forwarding assertion** — `SaInvoiceLimits.MaxEInvoiceBatchSelection == SaEInvoiceLimits.MaxBatchSelection` and `SaInvoiceLimits.MaxEInvoiceRefreshAllRun == SaEInvoiceLimits.MaxRefreshAllRun`, so the two definitions cannot drift apart silently.
16. `ErpWeb.Tests/SaCdnServiceTests.cs` — the `IrbmStatus` filter returns only matching rows; `IrbmStatus`/`IrbmUuid` project onto `SaCdnListRow`; `LineCount` is now correct (regression test for the fix).

### Phase F — Documentation

17. `docs/einvoice-history.md` — extend the "Implemented in ErpWeb" section with the CN/DN list parity (columns, batch actions, refresh-all, permission seed) and the fact that `RefreshSubmittedAsync` now serves both families. Record the **permission runbook completion** (which roles were granted, verified by whom) in the handover, since that step is what makes the feature usable rather than merely deployed.

## Relevant files

- `ErpWeb.Core/Sales/ISaCdnService.cs` — `SaCdnListQuery` (L183), `SaCdnListRow` (L197).
- `ErpWeb.Core/Sales/SaCdnService.cs` — `SearchAsync` (L280), `CanAsync(docType, permission)` (L2468).
- `ErpWeb.Core/Sales/SaCdnCalc.cs` — `SaCdnStatuses` / `SaCdnTypes` / `SaCdnLimits` (L8-26).
- `ErpWeb.Core/Sales/SaInvoiceQueryMapper.cs` — the mapper template to mirror.
- `ErpWeb.Core/Sales/SaInvoiceCalc.cs` — `SaInvoiceLimits` (L9-26).
- `ErpWeb.Model/Repositories/Sales/SaCdnRepository.cs` — `SaCdnSearchArgs` (L7), `SearchPagedAsync` (L137), `MaxPageSize` (L59).
- `ErpWeb.Core/EInvoice/ISaEInvoiceService.cs` — the Batch operations region.
- `ErpWeb.Core/EInvoice/SaEInvoiceService.cs` — `RefreshSubmittedAsync` (L838), `LoadRefreshCandidatesAsync` (L946), `AuthorizeAsync` (L2946), `LoadStateAsync` CN/DN case (L2345), `ResolveKeyByUuidAsync` (L1310), `ValidateBatchSelection` (L1380).
- `ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor(.cs)` — the feature to copy **then adapt** (Phase C0 is the adaptation gate; "verbatim" alone is explicitly not sufficient).
- `ErpWeb.UI/Sales/Transactions/SaCdnList.razor(.cs)` — the target.
- `ErpWeb.UI/Components/Common/EInvoicePortalLinkOpener.cs` — the shared E-UUID click helper (already type-agnostic).
- `ErpWeb.UI/Sales/Transactions/SaCdn.razor(.cs)` — the entry page; already correct, no change.
- `scripts/init-menu-access.sql` (L333), new `scripts/init-sales-cdn-einvoice-permissions.sql`.
- `ErpWeb.Tests/{EInvoiceTestHost,SaEInvoiceBatchTests,SaCdnServiceTests}.cs`.

## Verification

1. `dotnet build ErpWeb.slnx --nologo -v:q` → 0 `error CS` / `RZ`. **MANDATORY**: `dotnet test` never compiles `.razor`, so a green suite says nothing about a page. (A running `ErpWeb` app causes MSB3021/MSB3027 on the host project only.)
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` → the new tests green. Baseline before this work: **1892 total / 1879 passed / 13 failed / 0 skipped**, where the 13 are the pre-existing `SaCustServiceTests` (9) + `PoSupplierServiceTests` (4) GL-code/phone WIP. Any other failure is a regression.
2b. **[R2] Prove the invoice refresh-all did not change.** Run `--filter "FullyQualifiedName~SaEInvoiceBatchTests"` on the refactor commit **before and after** Phase B step 7 and confirm the same tests pass with **no edit to the test file**. `git diff --stat ErpWeb.Tests/SaEInvoiceBatchTests.cs` must be empty for the refactor commit; a non-empty diff means behaviour moved and must be justified line by line.
3. `--filter "FullyQualifiedName~EInvoice"` for the focused subset.
4. Read-only probe (needs `-d ERPWeb`, or a missing object resolves to a plausible, confident, wrong answer):
   `SELECT Type, IRBMStatus, COUNT(*) FROM dbo.SaCDN GROUP BY Type, IRBMStatus`
   — sizes the run cap and reveals any title-case legacy spelling. The SQLite test database has a BINARY collation, so such a row is **documented, not tested**; do not "fix" it with `ToUpper()` (C4).
5. Apply `scripts/init-sales-cdn-einvoice-permissions.sql` to dev `ERPWeb` **twice** (the second run must be a clean no-op) and confirm the four `MenuPermission` rows exist.
5b. **[R3] Complete the *Deployment runbook* in Phase D end to end** — the role grants, the non-admin smoke, and the unauthorized-user smoke. Also run its second verification query: if it returns any row, those roles can open the list but cannot submit, which is exactly the "feature looks broken" state this runbook exists to prevent.
5c. **[R5] Run the Phase C0.3 grep gate** and confirm the forbidden-token list returns zero hits, while `SaCdnListRow.InvNo` (the legitimate source-invoice reference) is still present.
6. Manual smoke on both routes:
   - `E-Inv` / `E-UUID` / `E-Status` render (a `FieldName` missing from the DTO throws at render — C11).
   - Select POSTED notes → `SUBMIT`, `E-STATUS` (confirm popup) and `CANCEL` (reason required, 1-300 characters) fill the results popup.
   - Nothing selected → `E-STATUS` runs the refresh-all with the progress strip and Stop; `SUBMIT`/`CANCEL` still say "No Record Selected!".
   - Stop mid-run → partial results popup listing everything that ran, buttons re-enable.
   - E-UUID click → LHDN portal opens and `EInvDocSubmission` is created/refreshed.
   - Navigate away mid-run → the "Please wait…" blocking-work guard still holds the page.
   - On `/sales/credit-notes` the refresh-all never reports a debit note, and vice versa.

## Decisions

- **Scope**: full parity, including the no-selection `E-STATUS` refresh-all (user choice).
- **Permissions**: a new idempotent script **and** the `init-menu-access.sql` update, so fresh installs match migrated ones (user choice).
- **`Lines` column defect**: fixed in this work (user choice).
- **Caps**: introduce `SaEInvoiceLimits` in the `ErpWeb.Core.EInvoice` namespace; `SaInvoiceLimits.MaxEInvoiceBatchSelection` / `MaxEInvoiceRefreshAllRun` forward to it. This keeps CN/DN code out of an invoice-named type while changing no call site and no test.
- **Service layer**: `AuthorizeAsync`, `LoadStateAsync`, `RepairSubmissionAsync` and the `SaCdn` e-Invoice columns are reused as-is; no new MyInvois code and no page access to the vendored library.
- **Naming**: `SaCdnQueryMapper` mirrors `SaInvoiceQueryMapper`; the new interface member is an **overload** (`SaCdnListQuery` vs `SaInvoiceListQuery`), which resolves unambiguously at both call sites because neither passes `null`.
- **D-2 [R1] — the `E-Inv` / `E-Status` duplication is mirrored, not redesigned.** On the invoice list both columns bind `IrbmStatus`; there is no second "submission exists" value on the row. Mirroring it keeps the two list screens visually and semantically identical. The alternative (making `E-Inv` mean "submitted or beyond") needs a **new** DTO property and a Phase A change, and must be decided at Phase C0.1 — not during Phase C. A later, separate change may collapse the pair into one column on **both** screens; that is out of scope here because it would alter the shipped invoice list.
- **D-3 [R2] — the limit constants are forwarding aliases.** `SaInvoiceLimits.MaxEInvoice*` keeps its name for the invoice call sites and tests, but its value now comes from `SaEInvoiceLimits`, and the equality is pinned by a test rather than by comment.
- **D-4 [R3] — role grants stay manual.** The permission script ships the capability; the runbook ships the *grant*. Hard-coding role names into a migration was rejected, but the manual step is documented with verification queries so it cannot be quietly skipped.
- **D-5 [R4] — one intentional behaviour change to shared code.** The exception guard in `RunRefreshAllAsync` is the **only** semantic change to the invoice refresh-all path. It is additive (no existing test exercises an escaping exception), it is documented on both overloads, and it is pinned by a new test rather than left implicit.

## Further Considerations

1. **Role grants are deliberately excluded from the script — see the *Deployment runbook* in Phase D.** A `RoleMenuPermission` row per role is still required before a non-admin can use `SUBMIT`/`CANCEL`, and **the feature is not considered delivered until the runbook's four steps are complete**. Seeding it would hard-code role names into a migration and was judged riskier than a documented manual step — confirm that trade-off is acceptable, and if it is not, the fallback is a script that grants to **no** role but prints the exact `RoleMenuPermission` `INSERT` for a human to fill in.
2. **Per-type refresh scope needs a smoke assertion, not code.** CN and DN share one list component, so the `Type` isolation is structural; it is pinned by a test but also worth one manual check on each route.
3. **`SaCdnListQuery` has no `IrbmStatus` UI filter.** The invoice page does not expose one either, so the grid filter stays as-is; if an e-Invoice status filter popup is wanted later, it must be added to `SaCdnQueryMapper` and the `SaCdnListQuery`, never to the page alone.
4. **Naming review**: `SaEInvoiceLimits.MaxBatchSelection` is deliberately shorter than `SaInvoiceLimits.MaxEInvoiceBatchSelection`; if the team prefers identical suffixes across both classes, say so before Phase B.
5. **Out of scope, unchanged**: the CN/DN entry-page panel (already correct), self-billed `SBI`/`SBC`/`SBD` payload mapping, a `Recover` button on the list (the invoice list has none either), and the same feature on the Purchase CN/DN lists.

## Implementation status — IMPLEMENTED (2026-09-22)

All phases A–F are done. `plans/plan-saCdnEInvoiceParity.prompt.md` is the plan of record; the handover
narrative lives in `docs/einvoice-history.md` (new "Sales Credit / Debit Note list parity" section).

### Evidence

| Check | Result |
|---|---|
| `dotnet build ErpWeb.slnx --nologo -v:q` | **0** `error CS` / `RZ` / `MSB` (exit 0) |
| `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` | **1912 total / 1899 passed / 13 failed / 0 skipped** |
| Baseline before this work | 1892 / 1879 / **13** / 0 → **+20 tests, +20 passing, failure count unchanged** |
| The 13 failures | Confirmed identical: `--filter "…SaCustServiceTests\|…PoSupplierServiceTests"` → 13 failed / 57 passed (the pre-existing GL-code/phone WIP) |
| `--filter "…EInvoice\|…SaCdnServiceTests"` | **288 passed / 0 failed / 0 skipped** |
| Phase C0.3 grep gate | zero forbidden invoice tokens in `SaCdnList.razor*`; `SaCdnListRow.InvNo` (the source-invoice reference) correctly survives |
| `scripts/init-sales-cdn-einvoice-permissions.sql` on dev `ERPWeb` | applied **twice**, exit 0 both runs, `0 inserted` (the four rows already existed) |
| Live collation probe | `SaCDN.IRBMStatus` and the DB are both `SQL_Latin1_General_CP1_CI_AS` → plain equality confirmed (C4) |

### Deviations and findings worth carrying forward

1. **`SaCdnTypes` is declared TWICE** — `ErpWeb.Core.Sales` and `ErpWeb.Model.Repositories.Sales` — so
   `ErpWeb.Core.EInvoice` cannot name it without a `CS0104`. `SaEInvoiceService` therefore compares against
   `EInvoiceDocumentTypes.CreditNote` / `.DebitNote`, which are the same `CN` / `DN` tokens `SaCdn.Type`
   already stores (the façade already relied on that mapping in `ResolveKeyByUuidAsync`). Exact twin of the
   `PoCdnTypes` duplication already noted in the repo memory — **collapsing the pair is still worth doing**.
2. **The dev database already had the four `MenuPermission` rows** for `SA_CN`/`SA_DN` × `SUBMIT`/`CANCEL`,
   all `IsActive = 1`. The plan's premise that the entry-page panel was locked was true of the *scripts*,
   not of this database. The `init-menu-access.sql` change is what a fresh database genuinely needed; the
   new script exists for databases in between and is a verified no-op here.
3. **Dev `dbo.SaCDN` holds 0 rows.** The refresh-all cannot be smoke-tested manually on dev without first
   creating a SUBMITTED note. Recorded rather than worked around.
4. **The two failure-semantics tests initially failed** (`SucceededCount` was 0, not 10). Cause: the
   permission check runs **per document** (`RefreshAsync` re-authorizes), not once per chunk, so counting
   checks landed inside chunk 1. Trigger re-keyed on *documents read*
   (`ChunksCompleted(host)`, i.e. detail calls ÷ `MaxBatchSelection`) — a deterministic chunk boundary that
   does not depend on how many times authorization happens to be called. Worth remembering for any future
   mid-run fault injection.
5. **A `sqlcmd` script guard bug was found by the verification itself**: the first version used
   `NOT EXISTS (SELECT 1 … GROUP BY 1 HAVING COUNT(*) = 2)`, which is a hard `Msg 164` (a constant cannot
   be the only `GROUP BY` expression). Replaced with a scalar `COUNT(*) < 2`.
6. **`Select-Object -First N` on a `sqlcmd` pipeline reports a bogus exit code** (`-1`): truncating the
   pipeline kills `sqlcmd` before it finishes. Redirect to a file and read that instead when asserting on
   `$LASTEXITCODE`.
7. **Design decisions honoured unchanged**: the overload (`SaCdnListQuery` vs `SaInvoiceListQuery`) was kept
   exactly as planned; the C0.1 gate resolved to "mirror the invoice list" (D-2), so `E-Inv` and `E-Status`
   both bind `IrbmStatus` with an explanatory comment; the exception guard was implemented in the shared
   driver per D-5 with the invoice refresh-all tests left **unedited**, which is the evidence for the
   byte-identical claim.

### Still owed (not code)

- The **role grants** in the deployment runbook (Phase D): a `RoleMenuPermission` row with
  `IsAllowed = 1` per role × `SA_CN`/`SA_DN` × `SUBMIT`/`CANCEL`, then the non-admin and unauthorized-user
  smoke. The feature is not delivered until that is done.
- A manual smoke on both routes with at least one SUBMITTED note (blocked by finding 3 above).
