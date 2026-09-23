using System.Diagnostics;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.EInvoiceLib.GenerateDoc;
using ErpWeb.EInvoiceLib.Interface;
using ErpWeb.EInvoiceLib.Model;
using ErpWeb.EInvoiceLib.Model.Document;
using ErpWeb.EInvoiceLib.Model.InputData;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CdnStatuses = ErpWeb.Core.Sales.SaCdnStatuses;

namespace ErpWeb.Core.EInvoice;

/// <summary>
/// Production façade over <c>ErpWeb.EInvoiceLib</c>.
///
/// Locked rules implemented here:
/// <list type="bullet">
/// <item><c>SUBMITTING</c> is persisted BEFORE the MyInvois HTTP call, and no SQL transaction is held
/// across that call.</item>
/// <item>Concurrent submits are refused via the status gate plus <c>RowVersion</c>; a
/// <c>DbUpdateConcurrencyException</c> aborts without a second MyInvois call.</item>
/// <item>A <c>FAILED</c> + <c>Unknown</c> outcome, or a stuck <c>SUBMITTING</c>, must be reconciled by
/// <see cref="RecoverAsync"/> before anything may be submitted again. <c>SUBMITTING</c> is never
/// silently reset to <c>NEW</c>.</item>
/// <item>MyInvois statuses are mapped through <see cref="SaEInvoiceStatusMap"/> and nothing else.</item>
/// <item>Every action is appended to <c>SaEInvoiceLog</c>; the document keeps only a short error.</item>
/// </list>
/// </summary>
public sealed class SaEInvoiceService : ISaEInvoiceService
{
    /// <summary>
    /// MyInvois limits the Cancel Document <c>reason</c> to 300 characters. Enforced in the service so
    /// every caller (panel, list, background job) is covered rather than trusting the UI.
    /// </summary>
    private const int MaxCancelReasonLength = 300;

    /// <summary>Why a candidate that cannot be addressed at MyInvois is reported as skipped.</summary>
    private const string MissingUuidReason = "Missing IRBMUUID";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ITenantScopeContext _tenant;
    private readonly ISaInvoiceRepository _invoices;
    private readonly ISaCdnRepository _cdns;
    private readonly IAccessRightService _accessRights;
    private readonly IClientSecretStore _secrets;
    private readonly ISubmitDocumentHelper _helper;
    private readonly IConfiguration _configuration;
    private readonly EInvoiceOptions _options;
    private readonly EInvoiceValidator _validator;
    private readonly EInvoiceDocumentMapper _mapper;
    private readonly ILogger<SaEInvoiceService> _logger;

    public SaEInvoiceService(
        IDbContextFactory<AppDbContext> dbFactory,
        ITenantScopeContext tenant,
        ISaInvoiceRepository invoices,
        ISaCdnRepository cdns,
        IAccessRightService accessRights,
        IClientSecretStore secrets,
        ISubmitDocumentHelper helper,
        IConfiguration configuration,
        IOptions<EInvoiceOptions> options,
        EInvoiceValidator validator,
        EInvoiceDocumentMapper mapper,
        ILogger<SaEInvoiceService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _invoices = invoices;
        _cdns = cdns;
        _accessRights = accessRights;
        _secrets = secrets;
        _helper = helper;
        _configuration = configuration;
        _options = options.Value;
        _validator = validator;
        _mapper = mapper;
        _logger = logger;
    }

    // ─────────────────────────────── Public operations ───────────────────────────────

    public async Task<SaEInvoiceResult> ValidateAsync(
        SaEInvoiceDocumentKey key,
        CancellationToken cancellationToken = default)
    {
        var gate = await AuthorizeAsync(key, PermissionCodes.Submit, cancellationToken);
        if (gate.Error is not null)
        {
            return gate.Error;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var state = await LoadStateAsync(db, gate.Scope!, key, tracking: false, cancellationToken);
        if (state is null)
        {
            return NotFound(key);
        }

        ApplyCompanyCredentials(gate.Scope!, state.Supplier);
        var report = await ValidateSourceAsync(db, state, gate.Scope!, cancellationToken);
        await AppendLogAsync(db, state, EInvoiceActions.Validate,
            status: EInvoiceStatuses.Normalize(state.Status),
            attemptNo: await NextAttemptAsync(db, state, cancellationToken),
            correlationId: Guid.NewGuid(),
            requestTime: DateTime.UtcNow,
            responseTime: DateTime.UtcNow,
            durationMs: 0,
            errorCode: report.IsValid ? null : "ERP_VALIDATION",
            errorMessage: report.IsValid ? null : report.Summary(4000),
            userId: gate.Scope!.UserId,
            cancellationToken: cancellationToken);

        return report.IsValid
            ? Ok(key, state)
            : SaEInvoiceResult.FailValidation(key, report.Summary(), report.Errors);
    }

    public Task<SaEInvoiceResult> SubmitAsync(
        SaEInvoiceDocumentKey key,
        CancellationToken cancellationToken = default) =>
        SubmitCoreAsync(key, EInvoiceActions.Submit, cancellationToken);

    public Task<SaEInvoiceResult> RetryAsync(
        SaEInvoiceDocumentKey key,
        CancellationToken cancellationToken = default) =>
        SubmitCoreAsync(key, EInvoiceActions.Retry, cancellationToken);

    public async Task<SaEInvoiceResult> RecoverAsync(
        SaEInvoiceDocumentKey key,
        bool operatorInitiated,
        CancellationToken cancellationToken = default)
    {
        var gate = await AuthorizeAsync(key, PermissionCodes.Submit, cancellationToken);
        if (gate.Error is not null)
        {
            return gate.Error;
        }

        var scope = gate.Scope!;
        var correlationId = Guid.NewGuid();

        // ── Phase 1: claim the recovery attempt (no HTTP inside an open context) ──
        EInvoiceDocumentState state;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var loaded = await LoadStateAsync(db, scope, key, tracking: true, cancellationToken);
            if (loaded is null)
            {
                return NotFound(key);
            }

            state = loaded;
            var status = EInvoiceStatuses.Normalize(state.Status);

            var eligible = status switch
            {
                EInvoiceStatuses.Submitting => operatorInitiated || IsStuck(state),
                EInvoiceStatuses.Failed => state.Outcome == EInvoiceOutcomes.Unknown,
                _ => false
            };

            if (!eligible)
            {
                return SaEInvoiceResult.Fail(key,
                    status == EInvoiceStatuses.Submitting
                        ? "The submission is still in flight. Recover becomes available after the stuck timeout."
                        : "Recover only applies to a stuck SUBMITTING document or an unknown failure outcome.");
            }

            ApplyCompanyCredentials(scope, state.Supplier);
            var sourceBuild = await BuildSourceAsync(db, state, scope, cancellationToken);
            state.Source = sourceBuild.Source;
            state.Report = sourceBuild.Report;
        }

        var supplier = state.Supplier!;
        var requestTime = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var outcome = ReconcileOutcome.Uncertain;
        string? myInvoisStatus = null;
        string? uuid = null;
        string? submissionId = state.SubmitId;
        DateTime? validatedOn = null;
        string? errorCode = null;
        string? errorMessage = null;
        string? reconcileUserMessage = null;

        // Captured inside the HTTP block below so the submission registry can be written after the
        // business commit without re-asking MyInvois (plan 3.7 - Recover passes the summary payload).
        DocumentSummary? matchedDocument = null;
        string? submissionOverallStatus = null;
        int? submissionDocumentCount = null;

        try
        {
            // Step 1 (locked order): if we have a submission id, ask MyInvois about that submission.
            if (!string.IsNullOrWhiteSpace(state.SubmitId))
            {
                var submission = await _helper.GetSubmission(state.SubmitId!);
                if (submission.IsSuccess && submission.result is not null)
                {
                    submissionOverallStatus = submission.result.overallStatus;
                    submissionDocumentCount = submission.result.documentCount;
                    var match = FindDocumentInSubmission(submission.result, state.DocumentNo);
                    if (match is not null)
                    {
                        matchedDocument = match;
                        myInvoisStatus = match.status;
                        uuid = match.uuid;
                        submissionId = match.submissionUid ?? submissionId;
                        validatedOn = match.dateTimeValidated;
                        outcome = ReconcileOutcome.Reconciled;
                    }
                    else if (SubmissionIsComplete(submission.result))
                    {
                        // The submission finished and this document is not part of it: it was never accepted.
                        outcome = ReconcileOutcome.ConfirmedAbsent;
                    }
                }
                else
                {
                    errorCode = submission.errorCode;
                    errorMessage = submission.error;
                }
            }

            // Step 2 (locked order): search by document number when the submission lookup did not settle it.
            if (outcome == ReconcileOutcome.Uncertain)
            {
                var found = await SearchByDocumentNumberAsync(state, supplier, cancellationToken);
                if (found.Found)
                {
                    myInvoisStatus = found.Status;
                    uuid = found.Uuid;
                    submissionId = found.SubmissionId ?? submissionId;
                    validatedOn = found.ValidatedOn;
                    outcome = ReconcileOutcome.Reconciled;
                }
                else if (found.SearchWasConclusive)
                {
                    outcome = ReconcileOutcome.ConfirmedAbsent;
                }
                else
                {
                    errorCode ??= found.ErrorCode;
                    errorMessage ??= found.Error;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "e-Invoice recovery failed for {Document}", key);
            errorCode ??= "RECOVER_EXCEPTION";
            errorMessage ??= ex.Message;
            outcome = ReconcileOutcome.Uncertain;
        }

        stopwatch.Stop();

        // ── Phase 2: persist the reconciled outcome ──
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var loaded = await LoadStateAsync(db, scope, key, tracking: true, cancellationToken);
            if (loaded is null)
            {
                return NotFound(key);
            }

            var current = EInvoiceStatuses.Normalize(loaded.Status);
            var targetStatus = current;

            switch (outcome)
            {
                case ReconcileOutcome.Reconciled when SaEInvoiceStatusMap.IsRecognised(myInvoisStatus):
                    targetStatus = SaEInvoiceStatusMap.FromMyInvoisDocumentStatus(myInvoisStatus);
                    loaded.Outcome = null;
                    loaded.Uuid = uuid ?? loaded.Uuid;
                    loaded.SubmitId = submissionId ?? loaded.SubmitId;
                    if (targetStatus == EInvoiceStatuses.Valid)
                    {
                        loaded.ValidOn = validatedOn ?? DateTime.UtcNow;
                    }
                    else if (targetStatus == EInvoiceStatuses.Cancelled)
                    {
                        loaded.CancelOn ??= DateTime.UtcNow;
                    }
                    loaded.Error = null;
                    reconcileUserMessage = "Reconciled with MyInvois as " + targetStatus + ".";
                    break;

                case ReconcileOutcome.Reconciled:
                    // The lookup matched the document but returned no usable status. Stay put and try later.
                    targetStatus = EInvoiceStatuses.Failed;
                    loaded.Outcome = EInvoiceOutcomes.Unknown;
                    loaded.Uuid = uuid ?? loaded.Uuid;
                    loaded.SubmitId = submissionId ?? loaded.SubmitId;
                    loaded.Error = Truncate("MyInvois returned the document without a usable status.", 500);
                    reconcileUserMessage = loaded.Error;
                    break;

                case ReconcileOutcome.ConfirmedAbsent:
                    targetStatus = EInvoiceStatuses.Failed;
                    loaded.Outcome = EInvoiceOutcomes.ConfirmedFailure;
                    loaded.Error = Truncate("MyInvois confirmed the document was not accepted. You may retry.", 500);
                    reconcileUserMessage = loaded.Error;
                    break;

                default:
                    targetStatus = EInvoiceStatuses.Failed;
                    loaded.Outcome = EInvoiceOutcomes.Unknown;
                    loaded.Error = Truncate("MyInvois could not confirm whether the document exists. Retry is blocked until Recover succeeds.", 500);
                    reconcileUserMessage = loaded.Error;
                    break;
            }

            loaded.Status = targetStatus;
            ApplyState(loaded);
            var attemptNo = await NextAttemptAsync(db, loaded, cancellationToken);
            await AppendLogAsync(db, loaded, EInvoiceActions.Recover,
                status: targetStatus,
                attemptNo: attemptNo,
                correlationId: correlationId,
                requestTime: requestTime,
                responseTime: DateTime.UtcNow,
                durationMs: stopwatch.ElapsedMilliseconds,
                errorCode: errorCode,
                errorMessage: JoinError(errorCode, errorMessage) ?? (outcome == ReconcileOutcome.Reconciled ? null : loaded.Error),
                userId: scope.UserId,
                cancellationToken: cancellationToken);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return ConcurrencyFailure(key);
            }

            // Submission registry: upsert after the business commit (plan 3.7). `loaded.SubmitId` is the
            // ERP document's own submission id, so a mismatched API payload cannot land on another row.
            await EInvoiceSubmissionWriter.ApplyStatusAsync(
                _dbFactory,
                scope,
                key,
                submissionId: loaded.SubmitId,
                summary: matchedDocument,
                detail: null,
                overallStatus: submissionOverallStatus,
                documentCount: submissionDocumentCount,
                logger: _logger,
                cancellationToken: cancellationToken);

            return new SaEInvoiceResult
            {
                Succeeded = outcome != ReconcileOutcome.Uncertain,
                ErrorKind = outcome == ReconcileOutcome.Uncertain ? SaEInvoiceErrorKind.MyInvois : SaEInvoiceErrorKind.None,
                ErrorMessage = reconcileUserMessage,
                ErrorCode = errorCode,
                DocumentType = key.DocumentType,
                DocumentNo = key.DocumentNo,
                Status = targetStatus,
                Outcome = loaded.Outcome,
                Uuid = loaded.Uuid,
                SubmissionId = loaded.SubmitId,
                AttemptNo = attemptNo,
                CorrelationId = correlationId,
                RecoveryRequired = outcome == ReconcileOutcome.Uncertain
            };
        }
    }

    public async Task<SaEInvoiceResult> RefreshAsync(
        SaEInvoiceDocumentKey key,
        CancellationToken cancellationToken = default)
    {
        var gate = await AuthorizeAsync(key, PermissionCodes.Submit, cancellationToken);
        if (gate.Error is not null)
        {
            return gate.Error;
        }

        var scope = gate.Scope!;
        var correlationId = Guid.NewGuid();

        EInvoiceDocumentState state;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var loaded = await LoadStateAsync(db, scope, key, tracking: false, cancellationToken);
            if (loaded is null)
            {
                return NotFound(key);
            }

            if (string.IsNullOrWhiteSpace(loaded.Uuid))
            {
                return SaEInvoiceResult.Fail(key,
                    "This document has no MyInvois UUID to refresh.",
                    status: EInvoiceStatuses.Normalize(loaded.Status));
            }

            state = loaded;
            ApplyCompanyCredentials(scope, state.Supplier);
        }

        var requestTime = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        GeneralResult<DocumentValidatation>? detail;
        try
        {
            detail = await _helper.GetDocumentDetail(state.Uuid!);
        }
        catch (Exception ex)
        {
            // Refresh is read-only. A transport failure must not abort a batch refresh or be mistaken
            // for a status change: keep the current status and report the error.
            _logger.LogError(ex, "e-Invoice refresh threw for {Document}", key);
            detail = null;
        }
        stopwatch.Stop();

        var myInvoisStatus = detail?.result?.status;
        var errorCode = detail is { IsSuccess: false } ? detail.errorCode : null;
        var errorMessage = detail is null
            ? "MyInvois could not be reached to refresh this document."
            : detail.IsSuccess ? null : detail.error;

        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var loaded = await LoadStateAsync(db, scope, key, tracking: true, cancellationToken);
            if (loaded is null)
            {
                return NotFound(key);
            }

            var applied = false;
            if (detail is { IsSuccess: true } && SaEInvoiceStatusMap.IsRecognised(myInvoisStatus))
            {
                var mapped = SaEInvoiceStatusMap.FromMyInvoisDocumentStatus(myInvoisStatus);
                loaded.Status = mapped;
                loaded.Outcome = null;
                loaded.Error = null;
                if (mapped == EInvoiceStatuses.Valid)
                {
                    loaded.ValidOn = detail.result!.dateTimeValidated ?? DateTime.UtcNow;
                }
                else if (mapped == EInvoiceStatuses.Cancelled)
                {
                    loaded.CancelOn ??= DateTime.UtcNow;
                }

                applied = true;
            }
            else if (detail is null || !detail.IsSuccess)
            {
                loaded.Error = Truncate(errorMessage, 500);
            }

            // Submission-id recovery - the ONE exception to "the registry key always comes from the ERP
            // document". A document MyInvois accepted can still have no IRBMSubmitID locally (the history
            // write failed, or the submission came from outside ErpWeb). Without a key the registry write
            // below skips and the row stays missing forever. The API's own submissionUid is adopted only
            // when there is NO ERP value to disagree with, and documentType/documentNo/companyID stay
            // ERP-derived - so a mismatched payload can at worst create a wrongly-keyed row, never
            // overwrite a correctly-keyed one.
            var submissionIdRecovered = false;
            if (string.IsNullOrWhiteSpace(loaded.SubmitId)
                && detail is { IsSuccess: true }
                && !string.IsNullOrWhiteSpace(detail.result?.submissionUid))
            {
                loaded.SubmitId = detail.result!.submissionUid!.Trim();
                submissionIdRecovered = true;
            }

            // The tracked entity is what SaveChangesAsync persists; the state wrapper is only a view.
            ApplyState(loaded);

            var attemptNo = await NextAttemptAsync(db, loaded, cancellationToken);
            await AppendLogAsync(db, loaded, EInvoiceActions.Refresh,                status: EInvoiceStatuses.Normalize(loaded.Status),
                attemptNo: attemptNo,
                correlationId: correlationId,
                requestTime: requestTime,
                responseTime: DateTime.UtcNow,
                durationMs: stopwatch.ElapsedMilliseconds,
                errorCode: errorCode,
                errorMessage: JoinError(errorCode, errorMessage),
                userId: scope.UserId,
                cancellationToken: cancellationToken);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return ConcurrencyFailure(key);
            }

            // Submission registry: refresh in place (plan 3.7). A non-successful detail response carries
            // no payload, so only a successful one is passed - and a null field never erases history.
            // The returned outcome is the authoritative "was the row written" signal: this Refresh can
            // report success while the write was skipped (no submission id) or failed.
            var historyWrite = await EInvoiceSubmissionWriter.ApplyStatusAsync(
                _dbFactory,
                scope,
                key,
                submissionId: loaded.SubmitId,
                summary: null,
                detail: detail is { IsSuccess: true } ? detail.result : null,
                overallStatus: null,
                documentCount: null,
                logger: _logger,
                cancellationToken: cancellationToken);

            return new SaEInvoiceResult
            {
                Succeeded = applied,
                ErrorKind = applied ? SaEInvoiceErrorKind.None : SaEInvoiceErrorKind.MyInvois,
                ErrorMessage = applied ? "Status refreshed from MyInvois." : (errorMessage ?? "MyInvois returned an unrecognised status."),
                ErrorCode = errorCode,
                DocumentType = key.DocumentType,
                DocumentNo = key.DocumentNo,
                Status = EInvoiceStatuses.Normalize(loaded.Status),
                Outcome = loaded.Outcome,
                Uuid = loaded.Uuid,
                SubmissionId = loaded.SubmitId,
                AttemptNo = attemptNo,
                CorrelationId = correlationId,
                HistoryWrite = MapHistoryWrite(historyWrite),
                SubmissionIdRecovered = submissionIdRecovered
            };
        }
    }

    public async Task<SaEInvoiceResult> CancelAsync(
        SaEInvoiceDocumentKey key,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var gate = await AuthorizeAsync(key, PermissionCodes.Cancel, cancellationToken);
        if (gate.Error is not null)
        {
            return gate.Error;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return SaEInvoiceResult.Fail(key, "A cancellation reason is mandatory.", SaEInvoiceErrorKind.Validation);
        }

        // MyInvois limits the reason to 300 characters; a longer value is refused before any HTTP call.
        if (reason.Trim().Length > MaxCancelReasonLength)
        {
            return SaEInvoiceResult.Fail(key,
                $"The cancellation reason cannot exceed {MaxCancelReasonLength} characters.",
                SaEInvoiceErrorKind.Validation);
        }

        var scope = gate.Scope!;
        var correlationId = Guid.NewGuid();

        EInvoiceDocumentState state;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var loaded = await LoadStateAsync(db, scope, key, tracking: false, cancellationToken);
            if (loaded is null)
            {
                return NotFound(key);
            }

            var status = EInvoiceStatuses.Normalize(loaded.Status);
            if (status is not (EInvoiceStatuses.Submitted or EInvoiceStatuses.Valid))
            {
                return SaEInvoiceResult.Fail(key,
                    "Only a SUBMITTED or VALID e-Invoice can be cancelled.",
                    status: status);
            }

            if (string.IsNullOrWhiteSpace(loaded.Uuid))
            {
                return SaEInvoiceResult.Fail(key,
                    "This document has no MyInvois UUID to cancel.",
                    status: status);
            }

            // Cancel window is measured from validation (the LHDN rule: 72 hours from the document's
            // validation timestamp), else from submission when the document is not valid yet.
            var clockStart = loaded.ValidOn ?? loaded.SentOn;
            if (clockStart.HasValue && _options.CancelWindowHours > 0)
            {
                var age = DateTime.UtcNow - clockStart.Value;
                if (age.TotalHours > _options.CancelWindowHours)
                {
                    return SaEInvoiceResult.Fail(key,
                        $"The {_options.CancelWindowHours}-hour cancellation window has passed. Issue a credit/debit note instead.",
                        status: status);
                }
            }

            state = loaded;
            ApplyCompanyCredentials(scope, state.Supplier);
        }

        var requestTime = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var cancelDoc = new CancelDocument
        {
            status = "cancelled",
            reason = reason,
            docType = state.DocumentType
        };

        GeneralResult<CancelRespone> cancelResult;
        try
        {
            cancelResult = await _helper.CancelDocument(cancelDoc, [state.Uuid!]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "e-Invoice cancellation threw for {Document}", key);
            cancelResult = new GeneralResult<CancelRespone> { IsSuccess = false, error = ex.Message };
        }

        stopwatch.Stop();
        var cancelled = cancelResult.IsSuccess
                        && (!string.IsNullOrWhiteSpace(cancelResult.result?.status)
                            ? string.Equals(cancelResult.result!.status, "cancelled", StringComparison.OrdinalIgnoreCase)
                            : true);

        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var loaded = await LoadStateAsync(db, scope, key, tracking: true, cancellationToken);
            if (loaded is null)
            {
                return NotFound(key);
            }

            if (cancelled)
            {
                loaded.Status = EInvoiceStatuses.Cancelled;
                loaded.Outcome = null;
                loaded.CancelOn = DateTime.UtcNow;
                loaded.Error = null;
            }
            else
            {
                // The financial document is untouched; only the short error and the audit row change.
                loaded.Error = Truncate(DescribeCancelError(cancelResult), 500);
            }

            ApplyState(loaded);

            var attemptNo = await NextAttemptAsync(db, loaded, cancellationToken);
            await AppendLogAsync(db, loaded, EInvoiceActions.Cancel,
                status: EInvoiceStatuses.Normalize(loaded.Status),
                attemptNo: attemptNo,
                correlationId: correlationId,
                requestTime: requestTime,
                responseTime: DateTime.UtcNow,
                durationMs: stopwatch.ElapsedMilliseconds,
                errorCode: cancelResult.IsSuccess ? null : cancelResult.errorCode,
                errorMessage: cancelled ? null : JoinError(cancelResult.errorCode, cancelResult.error),
                userId: scope.UserId,
                cancellationToken: cancellationToken);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return ConcurrencyFailure(key);
            }

            // Submission registry: a SUCCESSFUL cancel marks the row cancelled (plan 3.8). A failed
            // cancel must not touch history - the financial document is unchanged and only the short
            // error plus the audit row changed.
            if (cancelled)
            {
                await EInvoiceSubmissionWriter.ApplyStatusAsync(
                    _dbFactory,
                    scope,
                    key,
                    submissionId: loaded.SubmitId,
                    summary: null,
                    detail: null,
                    overallStatus: null,
                    documentCount: null,
                    logger: _logger,
                    cancelOnUtc: loaded.CancelOn ?? DateTime.UtcNow,
                    cancellationToken: cancellationToken);
            }

            return new SaEInvoiceResult
            {
                Succeeded = cancelled,
                ErrorKind = cancelled ? SaEInvoiceErrorKind.None : SaEInvoiceErrorKind.MyInvois,
                ErrorMessage = cancelled ? "e-Invoice cancelled." : loaded.Error,
                ErrorCode = cancelResult.errorCode,
                DocumentType = key.DocumentType,
                DocumentNo = key.DocumentNo,
                Status = EInvoiceStatuses.Normalize(loaded.Status),
                Uuid = loaded.Uuid,
                SubmissionId = loaded.SubmitId,
                AttemptNo = attemptNo,
                CorrelationId = correlationId
            };
        }
    }

    public async Task<SaEInvoiceStatusView?> GetStatusAsync(
        SaEInvoiceDocumentKey key,
        CancellationToken cancellationToken = default)
    {
        var gate = await AuthorizeAsync(key, PermissionCodes.Access, cancellationToken);
        if (gate.Error is not null)
        {
            return null;
        }

        var scope = gate.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var state = await LoadStateAsync(db, scope, key, tracking: false, cancellationToken);
        if (state is null)
        {
            return null;
        }

        var history = await db.SaEInvoiceLogs.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                        && x.DocumentType == key.DocumentType
                        && x.DocumentNo == key.DocumentNo)
            .OrderByDescending(x => x.Id)
            .Take(50)
            .Select(x => new SaEInvoiceLogRow
            {
                Id = x.Id,
                Action = x.Action,
                Status = x.Status,
                SubmissionId = x.SubmissionId,
                DocumentUuid = x.DocumentUuid,
                AttemptNo = x.AttemptNo,
                CorrelationId = x.CorrelationId,
                RequestTime = x.RequestTime,
                ResponseTime = x.ResponseTime,
                DurationMs = x.DurationMs,
                ErrorCode = x.ErrorCode,
                ErrorMessage = x.ErrorMessage,
                CreatedBy = x.CreatedBy,
                CreatedOn = x.CreatedOn
            })
            .ToListAsync(cancellationToken);

        return ToStatusView(key, state, history);
    }

    // ─────────────────────────────── Batch operations ───────────────────────────────

    public async Task<SaEInvoiceBatchResult> SubmitManyAsync(
        IReadOnlyList<SaEInvoiceDocumentKey> keys,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeKeys(keys);
        var refused = ValidateBatchSelection(normalized);
        if (refused is not null)
        {
            return refused;
        }

        var run = await SubmitBatchAsync(normalized, EInvoiceActions.Submit, cancellationToken);
        if (run.Refusal is not null)
        {
            return SaEInvoiceBatchResult.Failed(
                run.Refusal.ErrorMessage ?? "The action was refused.",
                run.Refusal.ErrorKind);
        }

        return SaEInvoiceBatchResult.From(run.Rows.Select(ToBatchItem).ToList());
    }

    public async Task<SaEInvoiceBatchResult> RefreshManyAsync(
        IReadOnlyList<SaEInvoiceDocumentKey> keys,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeKeys(keys);
        var refused = ValidateBatchSelection(normalized);
        if (refused is not null)
        {
            return refused;
        }

        var unauthorized = await RefuseIfUnauthorizedAsync(normalized, PermissionCodes.Submit, cancellationToken);
        if (unauthorized is not null)
        {
            return SaEInvoiceBatchResult.Failed(unauthorized.ErrorMessage ?? "Not authorized.", unauthorized.ErrorKind);
        }

        var rows = new List<BatchRowOutcome>(normalized.Count);
        foreach (var key in normalized)
        {
            // One call per document and commit, matching the panel's per-document refresh. Refresh is
            // read-only, so a failure here never destroys the current status.
            SaEInvoiceResult result;
            try
            {
                result = await RefreshAsync(key, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "e-Invoice batch refresh threw for {Document}", key);
                result = SaEInvoiceResult.Fail(key, ex.Message, SaEInvoiceErrorKind.MyInvois);
            }

            rows.Add(new BatchRowOutcome
            {
                Key = key,
                Result = result,
                Skipped = IsIneligibleResult(result)
            });
        }

        return SaEInvoiceBatchResult.From(rows.Select(ToBatchItem).ToList());
    }

    /// <summary>
    /// Refresh every <c>SUBMITTED</c> invoice the grid is showing — the no-selection counterpart of the
    /// list page's E-STATUS button. Read-only: nothing is submitted, cancelled, recovered or retried.
    /// See the interface for the full contract (single filter definition, pre-flight cap, chunked
    /// delegation, cancellation between chunks).
    /// </summary>
    public async Task<SaEInvoiceBatchResult> RefreshSubmittedAsync(
        SaInvoiceListQuery? scope,
        IProgress<SaEInvoiceRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var branchScope = _tenant.TryBranchScope();
        if (branchScope is null)
        {
            return SaEInvoiceBatchResult.Failed(
                "Invalid company or branch context.", SaEInvoiceErrorKind.Authorization);
        }

        // This entry point is invoice-only, so the menu code is fixed rather than derived per key the way
        // AuthorizeAsync does it. Gated once, before any query or MyInvois call.
        if (!await _accessRights.CanAsync(MenuCodes.SalesInvoice, PermissionCodes.Submit, cancellationToken))
        {
            return SaEInvoiceBatchResult.Failed("Not authorized.", SaEInvoiceErrorKind.Authorization);
        }

        var load = await LoadRefreshCandidatesAsync(branchScope, scope, cancellationToken);
        if (load.Refused is not null)
        {
            return load.Refused;
        }

        return await RunRefreshAllAsync(
            EInvoiceDocumentTypes.Invoice, load.Candidates, progress, cancellationToken);
    }

    /// <summary>
    /// Refresh every <c>SUBMITTED</c> credit or debit note the grid is showing — the CN/DN counterpart of
    /// the invoice overload. Read-only: nothing is submitted, cancelled, recovered or retried.
    ///
    /// <para>
    /// The family comes from the scope's <c>Type</c>: it selects the document family AND the menu the
    /// caller must hold, so a credit-note run can never touch a debit note. An unknown or blank type is
    /// refused as validation, never thrown.
    /// </para>
    /// </summary>
    public async Task<SaEInvoiceBatchResult> RefreshSubmittedAsync(
        SaCdnListQuery? scope,
        IProgress<SaEInvoiceRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The ERP family token (SaCdn.Type) is the same CN / DN token this façade already uses everywhere
        // else, so the check reuses it rather than the twice-declared SaCdnTypes helper.
        var docType = (scope?.Type ?? string.Empty).Trim().ToUpperInvariant();
        if (docType != EInvoiceDocumentTypes.CreditNote && docType != EInvoiceDocumentTypes.DebitNote)
        {
            return SaEInvoiceBatchResult.Failed("Type must be CN or DN.", SaEInvoiceErrorKind.Validation);
        }

        var branchScope = _tenant.TryBranchScope();
        if (branchScope is null)
        {
            return SaEInvoiceBatchResult.Failed(
                "Invalid company or branch context.", SaEInvoiceErrorKind.Authorization);
        }

        var isCreditNote = docType == EInvoiceDocumentTypes.CreditNote;
        var documentType = isCreditNote ? EInvoiceDocumentTypes.CreditNote : EInvoiceDocumentTypes.DebitNote;
        var menuCode = isCreditNote ? MenuCodes.SalesCreditNote : MenuCodes.SalesDebitNote;

        // Gated once, on the family's OWN menu, before any query or MyInvois call.
        if (!await _accessRights.CanAsync(menuCode, PermissionCodes.Submit, cancellationToken))
        {
            return SaEInvoiceBatchResult.Failed("Not authorized.", SaEInvoiceErrorKind.Authorization);
        }

        var load = await LoadCdnRefreshCandidatesAsync(branchScope, scope!, docType, cancellationToken);
        if (load.Refused is not null)
        {
            return load.Refused;
        }

        return await RunRefreshAllAsync(documentType, load.Candidates, progress, cancellationToken);
    }

    /// <summary>
    /// Refresh every <c>SUBMITTED</c> self-billed document (LHDN 11 / 12 / 13) the grid is showing — the
    /// SBI / SBC / SBD counterpart of the invoice and CN/DN overloads. Read-only: nothing is submitted,
    /// cancelled, recovered or retried.
    ///
    /// <para>
    /// The family is passed in (see the interface for why it is not derived from the scope) and selects
    /// the source table, the authorizing menu and the document-type token handed to the shared driver, so
    /// a credit-note run can never touch a debit note.
    /// </para>
    /// </summary>
    public async Task<SaEInvoiceBatchResult> RefreshSubmittedAsync(
        string documentType,
        PoSbQuery? scope,
        IProgress<SaEInvoiceRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var family = ResolveSbRefreshFamily(documentType);
        if (family is null)
        {
            return SaEInvoiceBatchResult.Failed(
                "Type must be SBI, SBC or SBD.", SaEInvoiceErrorKind.Validation);
        }

        var branchScope = _tenant.TryBranchScope();
        if (branchScope is null || string.IsNullOrWhiteSpace(branchScope.BranchCode))
        {
            // The self-billed list services refuse a blank branch too, so the candidate query would
            // silently see nothing. Refuse with the same message rather than returning an empty run.
            return SaEInvoiceBatchResult.Failed(
                "Invalid company or branch context.", SaEInvoiceErrorKind.Authorization);
        }

        // Gated once, on the family's OWN menu, before any query or MyInvois call. Submit — not Access:
        // reading the list already requires Access, and an operator trusted to submit from it must be
        // able to refresh it. Requiring Access here would lock out a Submit-only role.
        if (!await _accessRights.CanAsync(family.Value.MenuCode, PermissionCodes.Submit, cancellationToken))
        {
            return SaEInvoiceBatchResult.Failed("Not authorized.", SaEInvoiceErrorKind.Authorization);
        }

        var load = await LoadSbRefreshCandidatesAsync(
            branchScope, scope, family.Value.DocumentType, cancellationToken);
        if (load.Refused is not null)
        {
            return load.Refused;
        }

        return await RunRefreshAllAsync(
            family.Value.DocumentType, load.Candidates, progress, cancellationToken);
    }

    /// <summary>
    /// The ONE place a self-billed e-Invoice family is mapped to its source table, its authorizing menu
    /// and its document-type token. Returns null for anything that is not a self-billed family.
    /// </summary>
    private static (string DocumentType, string MenuCode)? ResolveSbRefreshFamily(string? documentType) =>
        (documentType ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            EInvoiceDocumentTypes.SelfBilledInvoice =>
                (EInvoiceDocumentTypes.SelfBilledInvoice, MenuCodes.PurchaseSbInvoice),
            EInvoiceDocumentTypes.SelfBilledCreditNote =>
                (EInvoiceDocumentTypes.SelfBilledCreditNote, MenuCodes.PurchaseSbCreditNote),
            EInvoiceDocumentTypes.SelfBilledDebitNote =>
                (EInvoiceDocumentTypes.SelfBilledDebitNote, MenuCodes.PurchaseSbDebitNote),
            _ => null
        };

    /// <summary>Plural family name for the over-cap refusal message.</summary>
    private static string DescribeSbFamily(string documentType) => documentType switch
    {
        EInvoiceDocumentTypes.SelfBilledCreditNote => "self-billed credit notes",
        EInvoiceDocumentTypes.SelfBilledDebitNote => "self-billed debit notes",
        _ => "self-billed invoices"
    };

    /// <summary>
    /// The self-billed twin of <see cref="LoadRefreshCandidatesAsync"/> /
    /// <see cref="LoadCdnRefreshCandidatesAsync"/>: enumerates the refresh-all candidates through the SAME
    /// filter definition the self-billed lists use (<see cref="PoSbQueryApplier"/>), with only the
    /// e-Invoice status pinned to <c>SUBMITTED</c>. Everything else — search text, ERP status, vendor and
    /// date range — is the caller's, passed through verbatim.
    ///
    /// <para>
    /// Returns a refusal instead of throwing when more than <see cref="SaEInvoiceLimits.MaxRefreshAllRun"/>
    /// documents match. The decision comes from the first page's total, so an over-cap run costs one query
    /// and zero MyInvois calls.
    /// </para>
    /// </summary>
    private async Task<RefreshCandidateLoad> LoadSbRefreshCandidatesAsync(
        TenantScope branchScope,
        PoSbQuery? scope,
        string documentType,
        CancellationToken cancellationToken)
    {
        var query = new PoSbQuery
        {
            // The operator's filters, passed through verbatim: this is the grid's definition, not ours.
            SearchText = scope?.SearchText,
            Status = scope?.Status,
            VendorCode = scope?.VendorCode,
            DateFrom = scope?.DateFrom,
            DateTo = scope?.DateTo,

            // Only what DEFINES the action rather than filters it.
            IrbmStatus = EInvoiceStatuses.Submitted,
            SortField = nameof(PoSbInvoice.DocNo),
            SortDescending = false,
            Skip = 0,
            Take = Math.Min(SaEInvoiceLimits.MaxRefreshAllRun, PoSbLimits.MaxPageSize)
        };

        var candidates = new List<RefreshCandidate>();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        while (true)
        {
            query.Skip = candidates.Count;
            var (page, total) = await SearchSbRefreshPageAsync(
                db, branchScope, documentType, query, cancellationToken);

            if (total > SaEInvoiceLimits.MaxRefreshAllRun)
            {
                return new RefreshCandidateLoad(
                    [],
                    SaEInvoiceBatchResult.Failed(
                        $"This would refresh {total} {DescribeSbFamily(documentType)}. Narrow the filter "
                        + $"to {SaEInvoiceLimits.MaxRefreshAllRun} or fewer, then try again.",
                        SaEInvoiceErrorKind.Validation));
            }

            candidates.AddRange(page);

            // Stop on a short page, on the total, or on the cap — whichever comes first.
            if (page.Count == 0
                || candidates.Count >= total
                || candidates.Count >= SaEInvoiceLimits.MaxRefreshAllRun)
            {
                break;
            }
        }

        return new RefreshCandidateLoad(candidates, null);
    }

    /// <summary>
    /// One page of self-billed refresh-all candidates, read through
    /// <see cref="PoSbQueryApplier"/> so the candidate set IS the grid's own query.
    ///
    /// <para>
    /// The projection is an anonymous type mapped in memory: EF cannot translate a named constructor
    /// inside a LINQ projection (the same reason the invoice and CN/DN loaders do it).
    /// </para>
    /// </summary>
    private static async Task<(IReadOnlyList<RefreshCandidate> Page, int Total)> SearchSbRefreshPageAsync(
        AppDbContext db,
        TenantScope branchScope,
        string documentType,
        PoSbQuery query,
        CancellationToken cancellationToken)
    {
        if (documentType == EInvoiceDocumentTypes.SelfBilledInvoice)
        {
            var invoices = PoSbQueryApplier.Apply(
                db.PoSbInvoices.AsNoTracking(), query, branchScope.CompanyCode, branchScope.BranchCode!);

            var invoiceTotal = await invoices.CountAsync(cancellationToken);
            var invoicePage = await invoices
                .Skip(query.Skip).Take(query.Take)
                .Select(x => new { x.DocNo, x.IrbmStatus, x.IrbmUuid })
                .ToListAsync(cancellationToken);

            return (
                invoicePage.Select(x => new RefreshCandidate(x.DocNo, x.IrbmStatus, x.IrbmUuid)).ToList(),
                invoiceTotal);
        }

        // The note grid's ERP token: the same CN / DN value PoSbCdn.Type stores.
        var type = documentType == EInvoiceDocumentTypes.SelfBilledDebitNote
            ? PoSbTypes.DebitNote
            : PoSbTypes.CreditNote;

        var notes = PoSbQueryApplier.Apply(
            db.PoSbCdns.AsNoTracking(), query, branchScope.CompanyCode, branchScope.BranchCode!, type);

        var noteTotal = await notes.CountAsync(cancellationToken);
        var notePage = await notes
            .Skip(query.Skip).Take(query.Take)
            .Select(x => new { x.DocNo, x.IrbmStatus, x.IrbmUuid })
            .ToListAsync(cancellationToken);

        return (
            notePage.Select(x => new RefreshCandidate(x.DocNo, x.IrbmStatus, x.IrbmUuid)).ToList(),
            noteTotal);
    }

    /// <summary>
    /// The ONE chunked refresh-all driver, shared by the invoice and the credit/debit-note paths.
    ///
    /// <para>
    /// Blank-<c>IRBMUUID</c> candidates become <c>Skipped</c> items and never reach MyInvois. Eligible
    /// keys are handed to the public <see cref="RefreshManyAsync"/> in chunks, so the interactive cap,
    /// the per-chunk authorization check and the per-document semantics stay exactly where they are.
    /// Cancellation is honoured between chunks only, and an exception that escapes a chunk converts
    /// everything still outstanding into <c>Failed</c> items instead of discarding completed work.
    /// </para>
    /// </summary>
    private async Task<SaEInvoiceBatchResult> RunRefreshAllAsync(
        string documentType,
        IReadOnlyList<RefreshCandidate> candidates,
        IProgress<SaEInvoiceRefreshProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return SaEInvoiceBatchResult.From([]);
        }

        // Reported before the first chunk so the UI has a denominator while MyInvois is still untouched.
        progress?.Report(new SaEInvoiceRefreshProgress { Done = 0, Total = candidates.Count });

        var items = new List<SaEInvoiceBatchItemResult>(candidates.Count);
        var keys = new List<SaEInvoiceDocumentKey>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.IrbmUuid))
            {
                // Nothing to address at MyInvois. Reported as skipped rather than escalated: the operator
                // asked for a status refresh, so Recover stays a deliberate, separate action.
                items.Add(new SaEInvoiceBatchItemResult
                {
                    DocumentType = documentType,
                    DocumentNo = candidate.DocumentNo,
                    Skipped = true,
                    Status = EInvoiceStatuses.Normalize(candidate.IrbmStatus),
                    ErrorMessage = MissingUuidReason
                });
                continue;
            }

            keys.Add(new SaEInvoiceDocumentKey
            {
                DocumentType = documentType,
                DocumentNo = candidate.DocumentNo
            });
        }

        // Skipped rows already carry a result, so they count as done from the start.
        var done = items.Count;
        for (var start = 0; start < keys.Count; start += SaEInvoiceLimits.MaxBatchSelection)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Stop starting new chunks, but keep everything already produced: a Stop must never hide
                // work that has actually been done.
                break;
            }

            var take = Math.Min(SaEInvoiceLimits.MaxBatchSelection, keys.Count - start);
            var chunk = keys.GetRange(start, take);

            // The shipped batch primitive, verbatim, so the interactive cap, the per-chunk authorization
            // check and the per-document Refresh semantics stay exactly where they are.
            SaEInvoiceBatchResult chunkResult;
            try
            {
                chunkResult = await RefreshManyAsync(chunk, cancellationToken);
            }
            catch (Exception ex)
            {
                // A guard, not a policy change: an exception that escapes the batch primitive must not
                // reach the page (it would discard the work already done), so everything still
                // outstanding is reported as Failed instead.
                _logger.LogError(ex, "e-Invoice refresh-all chunk threw for {DocumentType}", documentType);
                var thrown = string.IsNullOrWhiteSpace(ex.Message)
                    ? "The refresh failed before it finished."
                    : ex.Message;
                for (var i = start; i < keys.Count; i++)
                {
                    items.Add(FailedBatchItem(keys[i], thrown));
                }

                break;
            }

            if (chunkResult.Refused)
            {
                // A whole-chunk refusal (authorization revoked mid-run) must not silently drop the rest.
                var reason = chunkResult.ErrorMessage ?? "The action was refused.";
                for (var i = start; i < keys.Count; i++)
                {
                    items.Add(FailedBatchItem(keys[i], reason));
                }

                break;
            }

            items.AddRange(chunkResult.Items);
            done += chunk.Count;
            progress?.Report(new SaEInvoiceRefreshProgress { Done = done, Total = candidates.Count });
        }

        return SaEInvoiceBatchResult.From(items);
    }

    /// <summary>
    /// Enumerates the refresh-all candidates through the SAME repository search the grid uses, with only
    /// two things set here: the e-Invoice status is pinned to <c>SUBMITTED</c>, and the paging is ours.
    /// Everything else — search text, document status, date range, sort — is the caller's, untouched.
    ///
    /// <para>
    /// Returns a refusal instead of throwing when more than
    /// <see cref="SaInvoiceLimits.MaxEInvoiceRefreshAllRun"/> invoices match. The decision comes from the
    /// first page's total, so an over-cap run costs one query and zero MyInvois calls.
    /// </para>
    /// </summary>
    private async Task<RefreshCandidateLoad> LoadRefreshCandidatesAsync(
        TenantScope branchScope,
        SaInvoiceListQuery? scope,
        CancellationToken cancellationToken)
    {
        var query = new SaInvoiceListQuery
        {
            // The operator's filters, passed through verbatim: this is the grid's definition, not ours.
            SearchText = scope?.SearchText,
            Status = scope?.Status,
            DateFrom = scope?.DateFrom,
            DateTo = scope?.DateTo,

            // Only what DEFINES the action rather than filters it.
            IrbmStatus = EInvoiceStatuses.Submitted,
            SortField = nameof(SaInvoice.InvNo),
            SortDescending = false,
            Skip = 0,
            Take = Math.Min(SaEInvoiceLimits.MaxRefreshAllRun, SaInvoiceRepository.MaxPageSize)
        };

        var candidates = new List<RefreshCandidate>();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        while (true)
        {
            query.Skip = candidates.Count;
            var (page, total) = await _invoices.SearchPagedAsync(
                db,
                branchScope.CompanyCode,
                branchScope.BranchCode ?? string.Empty,
                SaInvoiceQueryMapper.ToSearchArgs(query, query.Skip, query.Take),
                cancellationToken);

            if (total > SaEInvoiceLimits.MaxRefreshAllRun)
            {
                return new RefreshCandidateLoad(
                    [],
                    SaEInvoiceBatchResult.Failed(
                        $"This would refresh {total} invoices. Narrow the filter to "
                        + $"{SaEInvoiceLimits.MaxRefreshAllRun} or fewer, then try again.",
                        SaEInvoiceErrorKind.Validation));
            }

            candidates.AddRange(page.Select(x => new RefreshCandidate(x.InvNo, x.IrbmStatus, x.IrbmUuid)));

            // Stop on a short page, on the total, or on the cap — whichever comes first.
            if (page.Count == 0
                || candidates.Count >= total
                || candidates.Count >= SaEInvoiceLimits.MaxRefreshAllRun)
            {
                break;
            }
        }

        return new RefreshCandidateLoad(candidates, null);
    }

    /// <summary>
    /// The CN/DN twin of <see cref="LoadRefreshCandidatesAsync"/>: enumerates the refresh-all candidates
    /// through the SAME repository search the credit/debit-note grid uses, with only two things set here —
    /// the family (<c>CN</c> or <c>DN</c>) and the e-Invoice status pinned to <c>SUBMITTED</c>. Everything
    /// else, including the search text, document status and date range, is the caller's, untouched.
    ///
    /// <para>
    /// Returns a refusal instead of throwing when more than <see cref="SaEInvoiceLimits.MaxRefreshAllRun"/>
    /// notes match. The decision comes from the first page's total, so an over-cap run costs one query and
    /// zero MyInvois calls.
    /// </para>
    /// </summary>
    private async Task<RefreshCandidateLoad> LoadCdnRefreshCandidatesAsync(
        TenantScope branchScope,
        SaCdnListQuery scope,
        string docType,
        CancellationToken cancellationToken)
    {
        var query = new SaCdnListQuery
        {
            // The family IS part of the scope here: the grid only ever shows one of the two.
            Type = docType,

            // The operator's filters, passed through verbatim: this is the grid's definition, not ours.
            SearchText = scope.SearchText,
            Status = scope.Status,
            DateFrom = scope.DateFrom,
            DateTo = scope.DateTo,

            // Only what DEFINES the action rather than filters it.
            IrbmStatus = EInvoiceStatuses.Submitted,
            SortField = nameof(SaCdn.DocNo),
            SortDescending = false,
            Skip = 0,
            Take = Math.Min(SaEInvoiceLimits.MaxRefreshAllRun, SaCdnRepository.MaxPageSize)
        };

        var candidates = new List<RefreshCandidate>();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        while (true)
        {
            query.Skip = candidates.Count;
            var (page, total) = await _cdns.SearchPagedAsync(
                db,
                branchScope.CompanyCode,
                branchScope.BranchCode ?? string.Empty,
                SaCdnQueryMapper.ToSearchArgs(query, query.Skip, query.Take),
                cancellationToken);

            if (total > SaEInvoiceLimits.MaxRefreshAllRun)
            {
                var family = docType == EInvoiceDocumentTypes.CreditNote ? "credit notes" : "debit notes";
                return new RefreshCandidateLoad(
                    [],
                    SaEInvoiceBatchResult.Failed(
                        $"This would refresh {total} {family}. Narrow the filter to "
                        + $"{SaEInvoiceLimits.MaxRefreshAllRun} or fewer, then try again.",
                        SaEInvoiceErrorKind.Validation));
            }

            candidates.AddRange(page.Select(x => new RefreshCandidate(x.DocNo, x.IrbmStatus, x.IrbmUuid)));

            // Stop on a short page, on the total, or on the cap — whichever comes first.
            if (page.Count == 0
                || candidates.Count >= total
                || candidates.Count >= SaEInvoiceLimits.MaxRefreshAllRun)
            {
                break;
            }
        }

        return new RefreshCandidateLoad(candidates, null);
    }

    public async Task<SaEInvoiceBatchResult> CancelManyAsync(
        IReadOnlyList<SaEInvoiceDocumentKey> keys,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeKeys(keys);
        var refused = ValidateBatchSelection(normalized);
        if (refused is not null)
        {
            return refused;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return SaEInvoiceBatchResult.Failed("A cancellation reason is mandatory.", SaEInvoiceErrorKind.Validation);
        }

        if (reason.Trim().Length > MaxCancelReasonLength)
        {
            return SaEInvoiceBatchResult.Failed(
                $"The cancellation reason cannot exceed {MaxCancelReasonLength} characters.",
                SaEInvoiceErrorKind.Validation);
        }

        var unauthorized = await RefuseIfUnauthorizedAsync(normalized, PermissionCodes.Cancel, cancellationToken);
        if (unauthorized is not null)
        {
            return SaEInvoiceBatchResult.Failed(unauthorized.ErrorMessage ?? "Not authorized.", unauthorized.ErrorKind);
        }

        var rows = new List<BatchRowOutcome>(normalized.Count);
        foreach (var key in normalized)
        {
            // Each cancellation is its own MyInvois call with its own commit, so a later failure never
            // rolls back an earlier success. Sequential on purpose: LHDN allows 12 cancel requests/min.
            SaEInvoiceResult result;
            try
            {
                result = await CancelAsync(key, reason, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "e-Invoice batch cancellation threw for {Document}", key);
                result = SaEInvoiceResult.Fail(key, ex.Message, SaEInvoiceErrorKind.MyInvois);
            }

            rows.Add(new BatchRowOutcome
            {
                Key = key,
                Result = result,
                Skipped = IsIneligibleResult(result)
            });
        }

        return SaEInvoiceBatchResult.From(rows.Select(ToBatchItem).ToList());
    }

    public async Task<IReadOnlyList<SaEInvoiceStatusView?>> GetStatusManyAsync(
        IReadOnlyList<SaEInvoiceDocumentKey> keys,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeKeys(keys);
        var views = new List<SaEInvoiceStatusView?>(normalized.Count);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        foreach (var key in normalized)
        {
            // Access-only pre-flight. This is a convenience snapshot for the confirmation prompt and is
            // deliberately NOT the eligibility verdict: the Many operations re-authorize and re-validate.
            var gate = await AuthorizeAsync(key, PermissionCodes.Access, cancellationToken);
            if (gate.Error is not null)
            {
                views.Add(null);
                continue;
            }

            var state = await LoadStateAsync(db, gate.Scope!, key, tracking: false, cancellationToken);
            views.Add(state is null ? null : ToStatusView(key, state));
        }

        return views;
    }

    public async Task<SaEInvoicePortalLink?> GetPortalLinkAsync(
        string uuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return null;
        }

        // Company-scoped: a UUID belonging to another company must never resolve to a link here.
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return null;
        }

        var wanted = uuid.Trim();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // A re-submission adds a row for the same document (plan R5), so the current row is the
        // highest ID - the same rule the history writer reads it back with.
        var row = await db.EInvDocSubmissions
            .AsNoTracking()
            .Where(x => x.CompanyId == scope.CompanyCode && x.Uuid == wanted)
            .OrderByDescending(x => x.Id)
            .Select(x => new { x.Uuid, x.LongId, x.Status })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : SaEInvoicePortalLink.Create(row.Uuid, row.LongId, row.Status, _secrets.getPortalUrl());
    }

    public async Task<SaEInvoiceSubmissionRepairResult> RepairSubmissionAsync(
        string uuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return SaEInvoiceSubmissionRepairResult.NotApplicable(
                "This document has no MyInvois UUID to check the e-Invoice history against.");
        }

        var companyScope = _tenant.TryCompanyScope();
        if (companyScope is null)
        {
            return SaEInvoiceSubmissionRepairResult.NotApplicable(
                "Invalid company context, so the e-Invoice history was not checked.");
        }

        var wanted = uuid.Trim();

        ResolvedSubmissionKey resolved;
        try
        {
            resolved = await ResolveKeyByUuidAsync(companyScope, wanted, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "e-Invoice history repair could not resolve UUID {Uuid}", wanted);
            return SaEInvoiceSubmissionRepairResult.NotApplicable(
                "The e-Invoice history could not be checked (see the log).");
        }

        if (resolved.Key is null)
        {
            // Not an error: the uuid may belong to another company, or to a document this deployment
            // does not own. The portal link keeps its own behaviour and message.
            return SaEInvoiceSubmissionRepairResult.NotApplicable(
                "This MyInvois UUID is not linked to any ERP document, so there is no e-Invoice history to repair.");
        }

        var key = resolved.Key;

        // The registry is company-scoped, but the document is branch-owned and the ERP write-back below
        // resolves it by (CompanyCode, BranchCode, InvNo). A row naming a DIFFERENT branch is therefore
        // reported, never written: a silent cross-branch write would be worse than an explicit refusal.
        // A blank BranchCode carries no claim - every legacy row is blank - so it passes through.
        if (!string.IsNullOrWhiteSpace(resolved.BranchCode)
            && !string.IsNullOrWhiteSpace(companyScope.BranchCode)
            && !string.Equals(resolved.BranchCode, companyScope.BranchCode, StringComparison.OrdinalIgnoreCase))
        {
            return SaEInvoiceSubmissionRepairResult.NotApplicable(
                $"This e-Invoice belongs to branch {resolved.BranchCode}. Sign in to that branch to repair its history.");
        }

        // Checked here (as well as inside RefreshAsync) so a missing right is reported as "not repaired"
        // instead of surfacing as a failed refresh on a click whose real job is opening the portal.
        var gate = await AuthorizeAsync(key, PermissionCodes.Submit, cancellationToken);
        if (gate.Error is not null)
        {
            return SaEInvoiceSubmissionRepairResult.NotApplicable(
                "You do not have e-Invoice SUBMIT rights for this document, so its history was not repaired.");
        }

        SaEInvoiceResult result;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            // Bounds the persistence work only: the MyInvois HTTP call inside RefreshAsync does not
            // observe a token (see EInvoiceOptions.RepairPersistenceTimeoutSeconds).
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RepairPersistenceTimeoutSeconds)));
            try
            {
                // The single delegation. One repair, one MyInvois read.
                result = await RefreshAsync(key, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                return RepairFailed(key, resolved, "The e-Invoice history repair timed out before it finished.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "e-Invoice history repair threw for {Document}", key);
                return RepairFailed(key, resolved, "The e-Invoice history could not be repaired (see the log).");
            }
        }

        return new SaEInvoiceSubmissionRepairResult
        {
            Attempted = true,
            Succeeded = result.Succeeded,
            Message = DescribeRepair(result),
            DocumentType = key.DocumentType,
            DocumentNo = key.DocumentNo,
            Status = result.Status,
            SubmissionId = result.SubmissionId,
            SubmissionIdRecovered = result.SubmissionIdRecovered,
            HistoryWrite = result.HistoryWrite,
            ErrorKind = result.ErrorKind
        };
    }

    private static SaEInvoiceSubmissionRepairResult RepairFailed(
        SaEInvoiceDocumentKey key,
        ResolvedSubmissionKey resolved,
        string message) =>
        new()
        {
            Attempted = true,
            Succeeded = false,
            Message = message,
            DocumentType = key.DocumentType,
            DocumentNo = key.DocumentNo,
            HistoryWrite = EInvoiceHistoryWriteResult.NotAttempted,
            ErrorKind = SaEInvoiceErrorKind.Unexpected
        };

    /// <summary>
    /// Maps the writer's internal outcome onto the public contract enum, so the persistence
    /// implementation stays internal to this assembly.
    /// </summary>
    private static EInvoiceHistoryWriteResult MapHistoryWrite(EInvoiceHistoryWrite write) => write switch
    {
        EInvoiceHistoryWrite.Inserted => EInvoiceHistoryWriteResult.Inserted,
        EInvoiceHistoryWrite.Updated => EInvoiceHistoryWriteResult.Updated,
        EInvoiceHistoryWrite.Skipped => EInvoiceHistoryWriteResult.Skipped,
        EInvoiceHistoryWrite.Failed => EInvoiceHistoryWriteResult.Failed,
        _ => EInvoiceHistoryWriteResult.NotAttempted
    };

    /// <summary>One sentence for the operator, built from both halves of the repair.</summary>
    private static string DescribeRepair(SaEInvoiceResult result)
    {
        var parts = new List<string>
        {
            result.Succeeded
                ? "e-Invoice status refreshed from MyInvois."
                : result.ErrorMessage ?? "MyInvois returned no usable status."
        };

        parts.Add(result.HistoryWrite switch
        {
            EInvoiceHistoryWriteResult.Inserted => "The missing history row was created.",
            EInvoiceHistoryWriteResult.Updated => "The history row was refreshed.",
            EInvoiceHistoryWriteResult.Skipped => "No history row was written (the document has no submission id).",
            EInvoiceHistoryWriteResult.Failed => "The history row could not be written (see the log).",
            _ => "No history row was written."
        });

        if (result.SubmissionIdRecovered)
        {
            parts.Add("The submission id was recovered from MyInvois.");
        }

        if (!string.IsNullOrWhiteSpace(result.Status))
        {
            parts.Add("Status: " + result.Status + ".");
        }

        return string.Join(" ", parts);
    }

    /// <summary>The ERP document a MyInvois UUID belongs to.</summary>
    private sealed record ResolvedSubmissionKey(string? DocumentType, string? DocumentNo, string? BranchCode)
    {
        public SaEInvoiceDocumentKey? Key =>
            string.IsNullOrWhiteSpace(DocumentType) || string.IsNullOrWhiteSpace(DocumentNo)
                ? null
                : new SaEInvoiceDocumentKey
                {
                    DocumentType = DocumentType!.Trim(),
                    DocumentNo = DocumentNo!.Trim()
                };
    }

    /// <summary>
    /// Finds the ERP document a MyInvois UUID belongs to, company-wide.
    ///
    /// <para>
    /// The registry is consulted first: a row that exists is the direct answer, and the current row is
    /// the <b>highest ID</b> — the same rule <see cref="GetPortalLinkAsync"/> reads it with. The document
    /// tables are the fallback, so a UUID whose registry row was never written still resolves. Invoices
    /// and credit/debit notes are matched on both <c>IRBMUUID</c> and <c>IRBMORIUUID</c>, because a
    /// re-submission moves the superseded uuid onto the "ori" column.
    /// </para>
    ///
    /// <para>
    /// Projections are anonymous and mapped in memory on purpose: EF Core cannot translate a named type's
    /// constructor inside a LINQ-to-Entities projection.
    /// </para>
    /// </summary>
    private async Task<ResolvedSubmissionKey> ResolveKeyByUuidAsync(
        TenantScope companyScope,
        string uuid,
        CancellationToken cancellationToken)
    {
        var company = companyScope.CompanyCode;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.EInvDocSubmissions
            .AsNoTracking()
            .Where(x => x.CompanyId == company && x.Uuid == uuid)
            .OrderByDescending(x => x.Id)
            .Select(x => new { x.DocumentType, x.DocumentNo, x.BranchCode })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is not null
            && !string.IsNullOrWhiteSpace(row.DocumentType)
            && !string.IsNullOrWhiteSpace(row.DocumentNo))
        {
            return new ResolvedSubmissionKey(row.DocumentType, row.DocumentNo, row.BranchCode);
        }

        var invoice = await db.SaInvoices
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && (x.IrbmUuid == uuid || x.IrbmOriUuid == uuid))
            .OrderByDescending(x => x.ModifiedDate)
            .Select(x => new { x.InvNo, x.BranchCode })
            .FirstOrDefaultAsync(cancellationToken);
        if (invoice is not null && !string.IsNullOrWhiteSpace(invoice.InvNo))
        {
            return new ResolvedSubmissionKey(
                EInvoiceDocumentTypes.Invoice, invoice.InvNo, invoice.BranchCode);
        }

        var cdn = await db.SaCdns
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && (x.IrbmUuid == uuid || x.IrbmOriUuid == uuid))
            .OrderByDescending(x => x.ModifiedDate)
            .Select(x => new { x.Type, x.DocNo, x.BranchCode })
            .FirstOrDefaultAsync(cancellationToken);
        if (cdn is not null && !string.IsNullOrWhiteSpace(cdn.DocNo))
        {
            // SaCdn.Type already holds the ERP family token (CN / DN).
            return new ResolvedSubmissionKey(cdn.Type, cdn.DocNo, cdn.BranchCode);
        }

        var sbInvoice = await db.PoSbInvoices
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && (x.IrbmUuid == uuid || x.IrbmOriUuid == uuid))
            .OrderByDescending(x => x.ModifiedDate)
            .Select(x => new { x.DocNo, x.BranchCode })
            .FirstOrDefaultAsync(cancellationToken);
        if (sbInvoice is not null && !string.IsNullOrWhiteSpace(sbInvoice.DocNo))
        {
            return new ResolvedSubmissionKey(
                EInvoiceDocumentTypes.SelfBilledInvoice, sbInvoice.DocNo, sbInvoice.BranchCode);
        }

        var sbCdn = await db.PoSbCdns
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && (x.IrbmUuid == uuid || x.IrbmOriUuid == uuid))
            .OrderByDescending(x => x.ModifiedDate)
            .Select(x => new { x.Type, x.DocNo, x.BranchCode })
            .FirstOrDefaultAsync(cancellationToken);
        if (sbCdn is not null && !string.IsNullOrWhiteSpace(sbCdn.DocNo))
        {
            // PoSbCdn.Type already holds the ERP family token (CN / DN); map it to the self-billed token
            // so the resolved key stays comparable with the one the self-billed list pages build.
            var family = string.Equals(sbCdn.Type, PoSbTypes.DebitNote, StringComparison.OrdinalIgnoreCase)
                ? EInvoiceDocumentTypes.SelfBilledDebitNote
                : EInvoiceDocumentTypes.SelfBilledCreditNote;
            return new ResolvedSubmissionKey(family, sbCdn.DocNo, sbCdn.BranchCode);
        }

        return new ResolvedSubmissionKey(null, null, null);
    }

    /// <summary>
    /// Trims, drops blanks and deduplicates case-insensitively while preserving the caller's order.
    /// The batch cap is applied AFTER this, so selecting the same invoice twice counts once.
    /// </summary>
    private static List<SaEInvoiceDocumentKey> NormalizeKeys(IReadOnlyList<SaEInvoiceDocumentKey>? keys) =>
        (keys ?? [])
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.DocumentNo))
            .Select(x => new SaEInvoiceDocumentKey
            {
                DocumentType = (x.DocumentType ?? string.Empty).Trim().ToUpperInvariant(),
                DocumentNo = x.DocumentNo.Trim()
            })
            .GroupBy(x => (Type: x.DocumentType.ToUpperInvariant(), No: x.DocumentNo.ToUpperInvariant()))
            .Select(g => g.First())
            .ToList();

    private static SaEInvoiceBatchResult? ValidateBatchSelection(IReadOnlyList<SaEInvoiceDocumentKey> keys)
    {
        if (keys.Count == 0)
        {
            return SaEInvoiceBatchResult.Failed("Select at least one document.", SaEInvoiceErrorKind.Validation);
        }

        if (keys.Count > SaEInvoiceLimits.MaxBatchSelection)
        {
            return SaEInvoiceBatchResult.Failed(
                $"Select at most {SaEInvoiceLimits.MaxBatchSelection} documents per e-Invoice action.",
                SaEInvoiceErrorKind.Validation);
        }

        return null;
    }

    /// <summary>
    /// Refuses the whole action when the caller lacks the permission for any selected document, so a
    /// missing right is reported as one clear answer instead of a grid full of skipped rows.
    /// </summary>
    private async Task<SaEInvoiceResult?> RefuseIfUnauthorizedAsync(
        IReadOnlyList<SaEInvoiceDocumentKey> keys,
        string permission,
        CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            var gate = await AuthorizeAsync(key, permission, cancellationToken);
            if (gate.Error is not null && gate.Error.ErrorKind == SaEInvoiceErrorKind.Authorization)
            {
                return gate.Error;
            }
        }

        return null;
    }

    /// <summary>
    /// True when a refusal means "this document was never a candidate" rather than "the operation was
    /// tried and failed". Only these rows are reported as skipped in a batch result.
    /// </summary>
    private static bool IsIneligibleResult(SaEInvoiceResult result) =>
        !result.Succeeded
        && result.ErrorKind is SaEInvoiceErrorKind.StateRule
            or SaEInvoiceErrorKind.NotFound
            or SaEInvoiceErrorKind.Validation
            or SaEInvoiceErrorKind.NotConfigured;

    private static SaEInvoiceBatchItemResult ToBatchItem(BatchRowOutcome row) =>        new()
        {
            DocumentType = row.Key.DocumentType,
            DocumentNo = row.Key.DocumentNo,
            Succeeded = row.Result.Succeeded,
            Skipped = row.Skipped,
            Status = row.Result.Status,
            Outcome = row.Result.Outcome,
            Uuid = row.Result.Uuid,
            SubmissionId = row.Result.SubmissionId,
            ErrorMessage = row.Result.Succeeded ? null : row.Result.ErrorMessage,
            RecoveryRequired = row.Result.RecoveryRequired
        };

    /// <summary>
    /// A candidate that was never attempted because the whole chunk was refused. Reported as failed, not
    /// skipped, so a mid-run refusal cannot be mistaken for "this document was not a candidate".
    /// </summary>
    private static SaEInvoiceBatchItemResult FailedBatchItem(SaEInvoiceDocumentKey key, string reason) => new()
    {
        DocumentType = key.DocumentType,
        DocumentNo = key.DocumentNo,
        ErrorMessage = reason
    };

    private static SaEInvoiceStatusView ToStatusView(
        SaEInvoiceDocumentKey key,
        EInvoiceDocumentState state,
        IReadOnlyList<SaEInvoiceLogRow>? history = null) =>
        new()
        {
            DocumentType = key.DocumentType,
            DocumentNo = key.DocumentNo,
            Status = EInvoiceStatuses.Normalize(state.Status),
            Outcome = state.Outcome,
            Uuid = state.Uuid,
            OriginUuid = state.OriUuid,
            SubmissionId = state.SubmitId,
            SentOn = state.SentOn,
            ValidOn = state.ValidOn,
            CancelledOn = state.CancelOn,
            Error = state.Error,
            History = history ?? []
        };

    // ─────────────────────────────── TIN tools (read-only) ───────────────────────────────

    public async Task<SaEInvoiceTinCheckResult> ValidateTinAsync(
        string idType,
        string idValue,
        string tin,
        CancellationToken cancellationToken = default)
    {
        var cleanTin = (tin ?? string.Empty).Trim();
        var cleanIdValue = (idValue ?? string.Empty).Trim();
        var lhdnIdType = LhdnCodeLookup.TryIdType(idType);
        if (cleanTin.Length == 0 || cleanIdValue.Length == 0 || lhdnIdType is null)
        {
            return SaEInvoiceTinCheckResult.Fail(
                "TIN, identity type (NRIC, BRN, PASSPORT or ARMY) and identity value are all required.",
                SaEInvoiceErrorKind.Validation);
        }

        var scope = await AuthorizeTinToolsAsync(cancellationToken);
        if (scope.Error is not null)
        {
            return SaEInvoiceTinCheckResult.Fail(scope.Error, SaEInvoiceErrorKind.Authorization);
        }

        try
        {
            var result = await _helper.ValidateTin(cleanTin, lhdnIdType.Value, cleanIdValue);
            return result.IsSuccess
                ? SaEInvoiceTinCheckResult.Ok(cleanTin, lhdnIdType.Value.ToString(), cleanIdValue)
                : SaEInvoiceTinCheckResult.Invalid(cleanTin, lhdnIdType.Value.ToString(), cleanIdValue, result.error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TIN validation failed for {IdType}:{IdValue}", idType, cleanIdValue);
            return SaEInvoiceTinCheckResult.Fail(ex.Message, SaEInvoiceErrorKind.MyInvois);
        }
    }

    public async Task<SaEInvoiceTinSearchResult> SearchTinAsync(
        SaEInvoiceTinSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var taxpayerName = string.IsNullOrWhiteSpace(query?.TaxpayerName) ? null : query!.TaxpayerName!.Trim();
        var idValue = string.IsNullOrWhiteSpace(query?.IdValue) ? null : query!.IdValue!.Trim();
        var idType = string.IsNullOrWhiteSpace(query?.IdType) ? null : LhdnCodeLookup.TryIdType(query!.IdType)?.ToString();

        if (taxpayerName is null && idValue is null && idType is null)
        {
            return SaEInvoiceTinSearchResult.Fail(
                "Enter a taxpayer name, or an identity type with its value.",
                SaEInvoiceErrorKind.Validation);
        }

        var scope = await AuthorizeTinToolsAsync(cancellationToken);
        if (scope.Error is not null)
        {
            return SaEInvoiceTinSearchResult.Fail(scope.Error, SaEInvoiceErrorKind.Authorization);
        }

        try
        {
            var result = await _helper.SearchTin(new SearchTINInput
            {
                taxpayerName = taxpayerName,
                idType = idType,
                idValue = idValue
            });

            if (!result.IsSuccess)
            {
                return SaEInvoiceTinSearchResult.Fail(result.error ?? "MyInvois could not complete the TIN search.");
            }

            var rows = (result.result ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x?.tin))
                .Select(x => new SaEInvoiceTinSearchRow { Tin = x.tin.Trim() })
                .ToList();

            return SaEInvoiceTinSearchResult.Ok(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TIN search failed for {Query}", query);
            return SaEInvoiceTinSearchResult.Fail(ex.Message, SaEInvoiceErrorKind.MyInvois);
        }
    }

    /// <summary>
    /// TIN tools are company-scoped but not document-scoped, so they use the company scope and apply
    /// that company's credentials before the MyInvois call.
    /// </summary>
    private async Task<(TenantScope? Scope, string? Error)> AuthorizeTinToolsAsync(CancellationToken cancellationToken)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return (null, "Invalid company context.");
        }

        if (!await _accessRights.CanAsync(MenuCodes.SalesEInvoiceTin, PermissionCodes.Access, cancellationToken))
        {
            return (null, "Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        ApplyCompanyCredentials(scope, await LoadSupplierAsync(db, scope.CompanyCode, cancellationToken));
        return (scope, null);
    }

    public async Task<SaEInvoiceDetailResult> GetDocumentDetailAsync(
        string uuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return SaEInvoiceDetailResult.Fail(
                "A MyInvois document UUID is required.",
                SaEInvoiceErrorKind.Validation);
        }

        // Same company scope + credentials the TIN tools use: this is a company-scoped MyInvois read,
        // not a branch-owned ERP document operation.
        var scope = await AuthorizeTinToolsAsync(cancellationToken);
        if (scope.Error is not null)
        {
            return SaEInvoiceDetailResult.Fail(scope.Error, SaEInvoiceErrorKind.Authorization);
        }

        GeneralResult<DocumentValidatation> result;
        try
        {
            // The helper takes no CancellationToken (see EInvoiceOptions.RepairPersistenceTimeoutSeconds),
            // so the token cannot abort the HTTP call itself - only the work around it.
            result = await _helper.GetDocumentDetail(uuid.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get Document Details failed for {Uuid}", uuid);
            return SaEInvoiceDetailResult.Fail(
                "MyInvois could not be reached to read this document's detail.",
                SaEInvoiceErrorKind.MyInvois);
        }

        if (result is not { IsSuccess: true } || result.result is null)
        {
            return ClassifyDetailFailure(result);
        }

        // Read-only by construction: no ApplyStatusAsync, no AppendLogAsync, no SaveChangesAsync.
        // Looking at why a document failed must never change its e-Invoice state.
        return SaEInvoiceDetailResult.Ok(SaEInvoiceDetailMapper.Map(result.result));
    }

    /// <summary>
    /// Turns a failed Get Document Details response into an operator-facing message.
    ///
    /// <para>
    /// The substring matching is unavoidable and is a direct consequence of leaving
    /// <c>ErpWeb.EInvoiceLib</c> untouched: <c>E_InvoiceRepository.getDocumentDetail</c> flattens the
    /// response to <c>code + " " + details[0].message</c> (or just <c>code</c> when there are no
    /// details) and never populates <c>GeneralResult.errorCode</c>. This method is the single place to
    /// change if that is ever fixed.
    /// </para>
    /// </summary>
    private static SaEInvoiceDetailResult ClassifyDetailFailure(GeneralResult<DocumentValidatation> result)
    {
        var message = result.error;
        var errorCode = result.errorCode;

        if (Mentions(message, errorCode, "notfound") || Mentions(message, errorCode, "not found"))
        {
            // MyInvois returns not-found when the caller is the receiver of a document that is still
            // Submitted/Invalid, and for a UUID this deployment does not own.
            return SaEInvoiceDetailResult.Fail(
                "MyInvois has no detail for this document UUID. It may belong to another taxpayer, "
                + "or the submission may not have been accepted.",
                SaEInvoiceErrorKind.NotFound,
                errorCode);
        }

        if (Mentions(message, errorCode, "toomanyrequests")
            || Mentions(message, errorCode, "too many requests")
            || IsBareCode(message, "429")
            || IsBareCode(errorCode, "429"))
        {
            return SaEInvoiceDetailResult.Fail(
                "MyInvois is throttling requests. Wait a moment, then open LHDN detail again.",
                SaEInvoiceErrorKind.MyInvois,
                errorCode);
        }

        return SaEInvoiceDetailResult.Fail(
            string.IsNullOrWhiteSpace(message)
                ? "MyInvois could not return this document's detail."
                : message,
            SaEInvoiceErrorKind.MyInvois,
            errorCode);
    }

    private static bool Mentions(string? message, string? errorCode, string needle) =>
        (message?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
        || (errorCode?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// True when the value is exactly this code. Deliberately not a substring test: the collapsed
    /// message can contain unrelated digits (amounts, document numbers), so "contains 429" would
    /// misclassify a genuine failure as throttling.
    /// </summary>
    private static bool IsBareCode(string? value, string code) =>
        string.Equals(value?.Trim(), code, StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────────── Submit core ───────────────────────────────

    /// <summary>
    /// Single-document Submit/Retry. It is deliberately a batch of one so the detail panel and the list
    /// page share the exact same engine: lifecycle matrix, POSTED gate, SUBMITTING claim and audit.
    /// </summary>
    private async Task<SaEInvoiceResult> SubmitCoreAsync(
        SaEInvoiceDocumentKey key,
        string action,
        CancellationToken cancellationToken)
    {
        var run = await SubmitBatchAsync([key], action, cancellationToken);
        if (run.Refusal is not null)
        {
            return run.Refusal;
        }

        return run.Rows.Count > 0 && run.Rows[0].Result is not null
            ? run.Rows[0].Result!
            : NotFound(key);
    }

    /// <summary>
    /// The submit engine for one or many documents.
    /// <para>
    /// Phase 1 validates every document and claims <c>SUBMITTING</c> in one commit; phase 2 calls
    /// MyInvois with <b>no</b> database transaction open; phase 3 persists each outcome. Ineligible
    /// documents are skipped and reported, and the eligible ones still run.
    /// </para>
    /// </summary>
    private async Task<SubmitBatchRun> SubmitBatchAsync(
        IReadOnlyList<SaEInvoiceDocumentKey> keys,
        string action,
        CancellationToken cancellationToken)
    {
        var rows = keys.Select(x => new BatchRowOutcome { Key = x }).ToList();
        var correlationId = Guid.NewGuid();

        // ── Phase 0: authorize. A missing SUBMIT permission refuses the whole action; any other per-key
        // problem (unknown type, not enabled yet) is that row's own result. ──
        TenantScope? scope = null;
        foreach (var row in rows)
        {
            var gate = await AuthorizeAsync(row.Key, PermissionCodes.Submit, cancellationToken);
            if (gate.Error is not null)
            {
                if (gate.Error.ErrorKind == SaEInvoiceErrorKind.Authorization)
                {
                    return new SubmitBatchRun { Refusal = gate.Error, Rows = rows };
                }

                row.Skipped = true;
                row.Result = gate.Error;
                continue;
            }

            scope ??= gate.Scope;
        }

        var pending = rows.Where(x => x.Result is null).ToList();
        if (pending.Count == 0)
        {
            return new SubmitBatchRun { Rows = rows };
        }

        var claims = new List<BatchClaim>(pending.Count);
        var requestTime = DateTime.UtcNow;

        // ── Phase 1: validate, claim SUBMITTING, persist. NO HTTP here. ──
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            foreach (var row in pending)
            {
                var key = row.Key;
                var loaded = await LoadStateAsync(db, scope!, key, tracking: true, cancellationToken);
                if (loaded is null)
                {
                    row.Skipped = true;
                    row.Result = NotFound(key);
                    continue;
                }

                var status = EInvoiceStatuses.Normalize(loaded.Status);

                // A SALES invoice and a sales credit/debit note may only be sent once POSTED: the LHDN
                // payload must describe a finalised document. Self-billed documents have no ERP status
                // requirement. The UI already gates this, but the service re-checks so another UI, a job
                // or an API caller cannot bypass it. Placed BEFORE the SUBMITTING claim, so a refusal
                // never reaches MyInvois and never locks the document.
                if (!IsSourceDocumentSubmittable(loaded))
                {
                    row.Skipped = true;
                    row.Result = SaEInvoiceResult.Fail(key,
                        $"{DocumentTypeLabel(key.DocumentType)} {loaded.DocumentNo} must be POSTED before it can be sent to MyInvois.",
                        SaEInvoiceErrorKind.StateRule,
                        status: status);
                    continue;
                }

                // Pre-submit gate (locked).
                switch (status)
                {
                    case EInvoiceStatuses.Submitting when !IsStuck(loaded):
                        row.Skipped = true;
                        row.Result = SaEInvoiceResult.Fail(key,
                            "A submission is already in progress for this document.",
                            status: status);
                        continue;
                    case EInvoiceStatuses.Submitting:
                        row.Skipped = true;
                        row.Result = SaEInvoiceResult.Fail(key,
                            "The previous submission is stuck. Run Recover to reconcile it with MyInvois before submitting again.",
                            recoveryRequired: true,
                            status: status);
                        continue;
                    case EInvoiceStatuses.Submitted:
                    case EInvoiceStatuses.Valid:
                        row.Skipped = true;
                        row.Result = SaEInvoiceResult.Fail(key,
                            "This document has already been submitted to MyInvois.",
                            status: status);
                        continue;
                    case EInvoiceStatuses.Failed when loaded.Outcome == EInvoiceOutcomes.Unknown:
                        row.Skipped = true;
                        row.Result = SaEInvoiceResult.Fail(key,
                            "The previous submission outcome is unknown. Run Recover before submitting again.",
                            recoveryRequired: true,
                            status: status);
                        continue;
                }

                ApplyCompanyCredentials(scope!, loaded.Supplier);

                var build = await BuildSourceAsync(db, loaded, scope!, cancellationToken);
                if (!build.Report.IsValid || build.Source is null)
                {
                    await AppendLogAsync(db, loaded, EInvoiceActions.Validate,
                        status: status,
                        attemptNo: await NextAttemptAsync(db, loaded, cancellationToken),
                        correlationId: correlationId,
                        requestTime: DateTime.UtcNow,
                        responseTime: DateTime.UtcNow,
                        durationMs: 0,
                        errorCode: "ERP_VALIDATION",
                        errorMessage: build.Report.Summary(4000),
                        userId: scope!.UserId,
                        cancellationToken: cancellationToken);

                    // Persist the short error now, as the single-document path always did: this row is
                    // skipped, not claimed, so nothing later in phase 1 would flush it.
                    loaded.Error = Truncate(build.Report.Summary(), 500);
                    ApplyState(loaded);
                    row.Skipped = true;
                    row.Result = SaEInvoiceResult.FailValidation(key, build.Report.Summary(), build.Report.Errors);
                    try
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        row.Skipped = false;
                        row.Result = ConcurrencyFailure(key);
                    }

                    continue;
                }

                var mapped = _mapper.Map(build.Source, loaded.Supplier!, loaded.Supplier!.DocumentVersion ?? _secrets.getDocumentVersion());
                if (!mapped.IsValid)
                {
                    await AppendLogAsync(db, loaded, EInvoiceActions.Validate,
                        status: status,
                        attemptNo: await NextAttemptAsync(db, loaded, cancellationToken),
                        correlationId: correlationId,
                        requestTime: DateTime.UtcNow,
                        responseTime: DateTime.UtcNow,
                        durationMs: 0,
                        errorCode: "ERP_VALIDATION",
                        errorMessage: mapped.Report.Summary(4000),
                        userId: scope!.UserId,
                        cancellationToken: cancellationToken);

                    loaded.Error = Truncate(mapped.Report.Summary(), 500);
                    ApplyState(loaded);
                    row.Skipped = true;
                    row.Result = SaEInvoiceResult.FailValidation(key, mapped.Report.Summary(), mapped.Report.Errors);
                    try
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        row.Skipped = false;
                        row.Result = ConcurrencyFailure(key);
                    }

                    continue;
                }

                loaded.Mapped = mapped;
                loaded.Source = build.Source;

                // A CN/DN always records the origin invoice's MyInvois UUID, so the audit trail can answer
                // "which invoice does this note belong to?" long after the UUID is no longer on the source row.
                loaded.OriUuid = build.Source.OriginUuid ?? loaded.OriUuid;

                // Freeze the exact buyer identity being submitted BEFORE the status transition, so the one
                // SaveChangesAsync below commits both together: SUBMITTING never exists without the values
                // that were signed, and a rollback leaves the document live for a clean rebuild.
                ApplyBuyerFreeze(loaded, build.ResolvedBuyer);

                var attemptNo = await NextAttemptAsync(db, loaded, cancellationToken);
                loaded.Status = EInvoiceStatuses.Submitting;
                loaded.Outcome = null;
                loaded.Error = null;
                loaded.SentOn = requestTime;
                ApplyState(loaded);

                // No audit row here: one row per attempt is written in phase 3 with the outcome. A crash in
                // between leaves the persisted SUBMITTING status (and its ModifiedDate) as the evidence, which
                // is what Recover keys off.
                claims.Add(new BatchClaim
                {
                    Row = row,
                    State = loaded,
                    AttemptNo = attemptNo,
                    RequestTime = requestTime
                });
            }

            if (claims.Count > 0)
            {
                try
                {
                    // Commits the SUBMITTING claims (and their RowVersion checks) before MyInvois is called.
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Do NOT retry the transition blindly; no MyInvois call has happened yet.
                    foreach (var claim in claims)
                    {
                        claim.Row.Skipped = false;
                        claim.Row.Result = ConcurrencyFailure(claim.Row.Key);
                    }

                    return new SubmitBatchRun { Rows = rows };
                }
            }
        }

        // ── Phase 2: call MyInvois with NO database transaction open. ──
        // The signing step writes its scratch file into EInv_JsonPath before the library creates the
        // folder, so make sure it exists first (fresh installs have no App_Data folder yet).
        EnsureJsonPathDirectory();

        // The library reads the supplier identity (OnBehalfTin / DocumentVersion) from a shared store, and
        // the document family picks the generator, so a group is only ever built from rows that share both.
        // In one company/branch scope this is a single call; the grouping is what stops a mixed selection
        // from being signed or mapped with the wrong profile.
        var groups = claims
            .GroupBy(x => new
            {
                Note = EInvoiceDocumentTypeMap.IsCreditOrDebitNote(
                    EInvoiceDocumentTypeMap.GetDocumentTypeCode(x.State.Mapped!.Header.docType)),
                Tin = x.State.Supplier?.TinNo ?? string.Empty,
                OnBehalf = x.State.Supplier?.OnBehalfTin ?? string.Empty,
                Version = x.State.Supplier?.DocumentVersion ?? string.Empty
            })
            .ToList();

        foreach (var group in groups)
        {
            var groupClaims = group.ToList();
            ApplyCompanyCredentials(scope!, groupClaims[0].State.Supplier);

            var stopwatch = Stopwatch.StartNew();
            GeneralResult<SuccessSubmit>? submitResult = null;
            Exception? transportFailure = null;
            try
            {
                var headers = groupClaims.Select(x => x.State.Mapped!.Header).ToList();
                submitResult = group.Key.Note
                    ? await _helper.SubmitCreditDebitNotes(headers)
                    : await _helper.SubmitInvoices(headers);
            }
            catch (Exception ex)
            {
                // The call produced no readable answer: every document in this group may or may not have
                // reached MyInvois, so each one has to be reconciled with Recover.
                _logger.LogError(ex, "e-Invoice submission threw for {Count} document(s)", groupClaims.Count);
                transportFailure = ex;
            }
            stopwatch.Stop();

            var durationMs = stopwatch.ElapsedMilliseconds;
            var classified = transportFailure is null
                ? ClassifySubmitResults(submitResult!, groupClaims.Select(x => x.Row.Key.DocumentNo).ToList())
                : null;

            foreach (var claim in groupClaims)
            {
                claim.DurationMs = durationMs;
                claim.Classification = classified is null
                    ? new SubmitClassification
                    {
                        Status = EInvoiceStatuses.Failed,
                        Outcome = EInvoiceOutcomes.Unknown,
                        Error = transportFailure!.Message
                    }
                    : classified[claim.Row.Key.DocumentNo];
            }
        }

        // ── Phase 3: persist the outcome, one commit per document, so a conflict on one row cannot lose
        // the outcome of the others. ──
        foreach (var claim in claims)
        {
            var key = claim.Row.Key;
            var classification = claim.Classification;

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var loaded = await LoadStateAsync(db, scope!, key, tracking: true, cancellationToken);
            if (loaded is null)
            {
                claim.Row.Skipped = true;
                claim.Row.Result = NotFound(key);
                continue;
            }

            loaded.Status = classification.Status;
            loaded.Outcome = classification.Outcome;
            // A clean accept clears the previous short error; REJECTED and FAILED keep the reason so the
            // document shows why the last attempt failed (the full text lives in the audit row below).
            loaded.Error = classification.Status == EInvoiceStatuses.Submitted
                ? null
                : Truncate(classification.Error, 500);
            loaded.SubmitId = classification.SubmissionId ?? loaded.SubmitId;
            loaded.Uuid = classification.Uuid ?? loaded.Uuid;

            if (classification.Status == EInvoiceStatuses.Submitted)
            {
                loaded.SentOn ??= claim.RequestTime;
            }

            ApplyState(loaded);

            await AppendLogAsync(db, loaded, action,
                status: classification.Status,
                attemptNo: claim.AttemptNo,
                correlationId: correlationId,
                requestTime: claim.RequestTime,
                responseTime: DateTime.UtcNow,
                durationMs: claim.DurationMs,
                errorCode: classification.ErrorCode,
                errorMessage: JoinError(classification.ErrorCode, classification.Error),
                userId: scope!.UserId,
                cancellationToken: cancellationToken);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                claim.Row.Skipped = false;
                claim.Row.Result = ConcurrencyFailure(key);
                continue;
            }

            // ── Submission registry (dbo.EInvDocSubmission) ──
            // Written AFTER the business commit, on its OWN DbContext, and it never throws (plan D-8),
            // so a history failure can never fail the e-Invoice action and a rolled-back action can never
            // leave a phantom history row. Only ACCEPTED documents are recorded (plan R3 / Model A).
            if (classification.Status == EInvoiceStatuses.Submitted && classification.SubmissionId is not null)
            {
                await EInvoiceSubmissionWriter.RecordSubmitAsync(
                    _dbFactory,
                    scope!,
                    key,
                    submissionId: classification.SubmissionId,
                    uuid: classification.Uuid,
                    internalId: classification.InternalId,
                    documentCount: classification.DocumentCount,
                    overallStatus: classification.OverallStatus,
                    submittedOnUtc: claim.RequestTime,
                    logger: _logger,
                    cancellationToken: cancellationToken);
            }

            claim.Row.Skipped = false;
            claim.Row.Result = new SaEInvoiceResult
            {
                Succeeded = classification.Status == EInvoiceStatuses.Submitted,
                ErrorKind = classification.Status == EInvoiceStatuses.Submitted
                    ? SaEInvoiceErrorKind.None
                    : SaEInvoiceErrorKind.MyInvois,
                ErrorMessage = classification.Status == EInvoiceStatuses.Submitted
                    ? "Submitted to MyInvois."
                    : classification.Error,
                ErrorCode = classification.ErrorCode,
                DocumentType = key.DocumentType,
                DocumentNo = key.DocumentNo,
                Status = classification.Status,
                Outcome = classification.Outcome,
                Uuid = loaded.Uuid,
                SubmissionId = loaded.SubmitId,
                AttemptNo = claim.AttemptNo,
                CorrelationId = correlationId,
                RecoveryRequired = classification.Outcome == EInvoiceOutcomes.Unknown
            };
        }

        return new SubmitBatchRun { Rows = rows };
    }

    // ─────────────────────────────── Reconciliation helpers ───────────────────────────────

    private async Task<SearchOutcome> SearchByDocumentNumberAsync(
        EInvoiceDocumentState state,
        EInvoiceSupplierProfile supplier,
        CancellationToken cancellationToken)
    {
        var query = new SearchDocumentInput
        {
            documentType = EInvoiceDocumentTypeMap.GetDocumentTypeCode(
                EInvoiceDocumentMapper.MapDocumentType(state.DocumentType)),
            direction = "Sent",
            issuerTin = supplier.TinNo,
            searchQuery = state.DocumentNo,
            pageNo = 1,
            pageSize = 20,
            submissionDateFrom = DateTime.UtcNow.AddDays(-Math.Max(1, _options.RecoverySearchDays))
        };

        GeneralResult<RecentDocument> search;
        try
        {
            search = await _helper.SearchDocument(query);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "e-Invoice recovery search threw for {DocumentNo}", state.DocumentNo);
            return SearchOutcome.Inconclusive(ex.Message);
        }

        if (!search.IsSuccess)
        {
            return SearchOutcome.Inconclusive(search.error, search.errorCode);
        }

        var match = search.result?.result?.FirstOrDefault(x =>
            string.Equals(x.internalId, state.DocumentNo, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            // A successful search that returns nothing for this document number is treated as
            // "confirmed absent" only when MyInvois gave us a usable result set.
            return new SearchOutcome { SearchWasConclusive = true };
        }

        return new SearchOutcome
        {
            Found = true,
            SearchWasConclusive = true,
            Uuid = match.uuid,
            Status = match.status,
            SubmissionId = match.submissionUid,
            ValidatedOn = match.dateTimeValidated
        };
    }

    private static DocumentSummary? FindDocumentInSubmission(Submission submission, string documentNo) =>
        submission.documentSummary?.FirstOrDefault(x =>
            string.Equals(x.internalId, documentNo, StringComparison.OrdinalIgnoreCase));

    private static bool SubmissionIsComplete(Submission submission) =>
        !string.IsNullOrWhiteSpace(submission.overallStatus)
        && !string.Equals(submission.overallStatus, "inprogress", StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────────── Submit result classification (locked) ───────────────────────────────

    /// <summary>
    /// Maps one MyInvois submission response onto each document it carried.
    /// <para>
    /// A document that appears in neither <c>acceptedDocuments</c> nor <c>rejectedDocuments</c> while the
    /// response DID carry per-document lists is unresolved: it becomes <c>FAILED</c> + <c>Unknown</c> so the
    /// operator must Recover. It is never treated as a success.
    /// </para>
    /// <para>
    /// When the response carries no per-document lists at all, the whole call failed the same way for every
    /// document, so the locked single-document classification decides for all of them. That keeps generation
    /// failures (<c>errorCode 100</c>), duplicate submissions and transport failures on their existing rules.
    /// </para>
    /// </summary>
    private static Dictionary<string, SubmitClassification> ClassifySubmitResults(
        GeneralResult<SuccessSubmit> result,
        IReadOnlyList<string> documentNumbers)
    {
        var map = new Dictionary<string, SubmitClassification>(StringComparer.OrdinalIgnoreCase);

        // Values that describe the SUBMISSION rather than one document. The submission registry stores
        // them once per submission, so they are applied to every row this submission produced.
        Dictionary<string, SubmitClassification> Finish(Dictionary<string, SubmitClassification> classified)
        {
            // The submission-level status is only known to be SUBMITTED when EVERY document in the
            // submission was accepted. A mixed batch has no single value, so it stays null for a later
            // Refresh/Recover to fill from the API.
            var allAccepted = classified.Values.All(v => v.Status == EInvoiceStatuses.Submitted);
            foreach (var entry in classified.Values)
            {
                entry.DocumentCount = documentNumbers.Count;
                entry.OverallStatus = allAccepted ? EInvoiceStatuses.Submitted : null;
            }

            return classified;
        }

        var accepted = result.result?.acceptedDocuments?
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.invoiceCodeNumber))
            .GroupBy(x => x.invoiceCodeNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var rejected = result.result?.rejectedDocuments?
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.invoiceCodeNumber))
            .GroupBy(x => x.invoiceCodeNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // The locked single-document classifier only treats accepted documents as a success when the call
        // itself succeeded, so mirror that here.
        if (!result.IsSuccess)
        {
            accepted = null;
        }

        if (accepted is not { Count: > 0 } && rejected is not { Count: > 0 })
        {
            var global = ClassifySubmitResult(result);
            foreach (var documentNo in documentNumbers)
            {
                map[documentNo] = global;
            }

            return Finish(map);
        }

        foreach (var documentNo in documentNumbers)
        {
            if (accepted is not null && accepted.TryGetValue(documentNo, out var acceptedDoc))
            {
                map[documentNo] = new SubmitClassification
                {
                    Status = EInvoiceStatuses.Submitted,
                    Uuid = acceptedDoc.uuid,
                    InternalId = acceptedDoc.invoiceCodeNumber,
                    SubmissionId = result.result!.submissionUID
                };
                continue;
            }

            if (rejected is not null && rejected.TryGetValue(documentNo, out var rejectedDoc))
            {
                var message = rejectedDoc.error?.details is { Count: > 0 }
                    ? rejectedDoc.error.details[0].message
                    : rejectedDoc.error?.message ?? rejectedDoc.error?.code ?? "The document was rejected by MyInvois.";
                map[documentNo] = new SubmitClassification
                {
                    Status = EInvoiceStatuses.Rejected,
                    ErrorCode = rejectedDoc.error?.code,
                    Error = message,
                    SubmissionId = result.result?.submissionUID
                };
                continue;
            }

            // The response carried per-document lists but never mentioned this document. Its outcome is
            // unresolved, so block another submit until Recover has reconciled it.
            map[documentNo] = new SubmitClassification
            {
                Status = EInvoiceStatuses.Failed,
                Outcome = EInvoiceOutcomes.Unknown,
                Error = "MyInvois did not report an outcome for this document. Run Recover before submitting again."
            };
        }

        return Finish(map);
    }

    private static SubmitClassification ClassifySubmitResult(GeneralResult<SuccessSubmit> result)
    {
        var accepted = result.result?.acceptedDocuments;
        if (result.IsSuccess && accepted is { Count: > 0 })
        {
            return new SubmitClassification
            {
                Status = EInvoiceStatuses.Submitted,
                Uuid = accepted[0].uuid,
                InternalId = accepted[0].invoiceCodeNumber,
                SubmissionId = result.result!.submissionUID
            };
        }

        var rejected = result.result?.rejectedDocuments;
        if (rejected is { Count: > 0 })
        {
            var first = rejected[0];
            var message = first.error?.details is { Count: > 0 }
                ? first.error.details[0].message
                : first.error?.message ?? first.error?.code ?? "The document was rejected by MyInvois.";
            return new SubmitClassification
            {
                Status = EInvoiceStatuses.Rejected,
                ErrorCode = first.error?.code,
                Error = message,
                SubmissionId = result.result?.submissionUID
            };
        }

        var errorCode = result.errorCode;
        var error = result.error;

        // Generation failed inside the library - nothing ever reached MyInvois.
        if (string.Equals(errorCode, "100", StringComparison.Ordinal))
        {
            return new SubmitClassification
            {
                Status = EInvoiceStatuses.Failed,
                Outcome = EInvoiceOutcomes.ConfirmedFailure,
                ErrorCode = errorCode,
                Error = error
            };
        }

        // A blank token means the request was never sent.
        if (!string.IsNullOrWhiteSpace(error)
            && error.Contains("Token is blank", StringComparison.OrdinalIgnoreCase))
        {
            return new SubmitClassification
            {
                Status = EInvoiceStatuses.Failed,
                Outcome = EInvoiceOutcomes.ConfirmedFailure,
                ErrorCode = errorCode,
                Error = error
            };
        }

        // DuplicateSubmission (HTTP 422): a previous identical submission exists, so the document may
        // already be at MyInvois. That must be reconciled, never blindly retried.
        if ((!string.IsNullOrWhiteSpace(error)
             && error.Contains("DuplicateSubmission", StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(errorCode)
                && errorCode.Contains("Unprocessable", StringComparison.OrdinalIgnoreCase)))
        {
            return new SubmitClassification
            {
                Status = EInvoiceStatuses.Failed,
                Outcome = EInvoiceOutcomes.Unknown,
                ErrorCode = errorCode,
                Error = error
            };
        }

        // No status code at all means a transport failure or an unreadable response: the request may
        // have been received, so the outcome is unknown and recovery is required.
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return new SubmitClassification
            {
                Status = EInvoiceStatuses.Failed,
                Outcome = EInvoiceOutcomes.Unknown,
                ErrorCode = errorCode,
                Error = error ?? "The MyInvois response could not be read."
            };
        }

        // A definitive HTTP status came back: MyInvois did not accept the submission.
        return new SubmitClassification
        {
            Status = EInvoiceStatuses.Failed,
            Outcome = EInvoiceOutcomes.ConfirmedFailure,
            ErrorCode = errorCode,
            Error = error
        };
    }

    // ─────────────────────────────── Data loading / persistence ───────────────────────────────

    private sealed class EInvoiceDocumentState
    {
        public object Entity { get; init; } = null!;
        public string DocumentType { get; init; } = string.Empty;
        public string DocumentNo { get; init; } = string.Empty;
        public string CompanyCode { get; init; } = string.Empty;
        public string BranchCode { get; init; } = string.Empty;

        public string? Status { get; set; }
        public string? Outcome { get; set; }
        public string? SubmitId { get; set; }
        public string? Uuid { get; set; }
        public string? OriUuid { get; set; }
        public DateTime? SentOn { get; set; }
        public DateTime? ValidOn { get; set; }
        public string? Error { get; set; }
        public DateTime? CancelOn { get; set; }

        public DateTime? ModifiedDate { get; init; }
        public string? RefDocumentNo { get; init; }

        /// <summary>Supplier profile resolved for this company; set by <see cref="ApplyCompanyCredentials"/>.</summary>
        public EInvoiceSupplierProfile? Supplier { get; set; }

        public EInvoiceSourceDocument? Source { get; set; }
        public MappedEInvoiceDocument? Mapped { get; set; }
        public EInvoiceValidationReport? Report { get; set; }
    }

    private async Task<EInvoiceDocumentState?> LoadStateAsync(
        AppDbContext db,
        TenantScope scope,
        SaEInvoiceDocumentKey key,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var company = scope.CompanyCode;
        var branch = scope.BranchCode ?? string.Empty;
        var supplier = await LoadSupplierAsync(db, company, cancellationToken);

        switch (key.DocumentType)
        {
            case EInvoiceDocumentTypes.Invoice:
            {
                var query = db.SaInvoices
                    .Include(x => x.Details)
                    .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.InvNo == key.DocumentNo);
                var invoice = tracking
                    ? await query.FirstOrDefaultAsync(cancellationToken)
                    : await query.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
                if (invoice is null)
                {
                    return null;
                }

                return new EInvoiceDocumentState
                {
                    Entity = invoice,
                    DocumentType = EInvoiceDocumentTypes.Invoice,
                    DocumentNo = invoice.InvNo,
                    CompanyCode = invoice.CompanyCode,
                    BranchCode = invoice.BranchCode,
                    Status = invoice.IrbmStatus,
                    Outcome = invoice.IrbmOutcome,
                    SubmitId = invoice.IrbmSubmitId,
                    Uuid = invoice.IrbmUuid,
                    OriUuid = invoice.IrbmOriUuid,
                    SentOn = invoice.IrbmSentOn,
                    ValidOn = invoice.IrbmValidOn,
                    Error = invoice.IrbmError,
                    CancelOn = invoice.IrnmCancelOn,
                    ModifiedDate = invoice.ModifiedDate,
                    Supplier = supplier
                };
            }

            case EInvoiceDocumentTypes.CreditNote:
            case EInvoiceDocumentTypes.DebitNote:
            {
                var query = db.SaCdns
                    .Include(x => x.Details)
                    .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.DocNo == key.DocumentNo);
                var cdn = tracking
                    ? await query.FirstOrDefaultAsync(cancellationToken)
                    : await query.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
                if (cdn is null)
                {
                    return null;
                }

                // The ERP document type must match the CN/DN the caller asked for.
                var expectedType = key.DocumentType == EInvoiceDocumentTypes.CreditNote ? "CN" : "DN";
                if (!string.Equals(cdn.Type, expectedType, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return new EInvoiceDocumentState
                {
                    Entity = cdn,
                    DocumentType = key.DocumentType,
                    DocumentNo = cdn.DocNo,
                    CompanyCode = cdn.CompanyCode,
                    BranchCode = cdn.BranchCode,
                    Status = cdn.IrbmStatus,
                    Outcome = cdn.IrbmOutcome,
                    SubmitId = cdn.IrbmSubmitId,
                    Uuid = cdn.IrbmUuid,
                    OriUuid = cdn.IrbmOriUuid,
                    SentOn = cdn.IrbmSentOn,
                    ValidOn = cdn.IrbmValidOn,
                    Error = cdn.IrbmError,
                    CancelOn = cdn.IrnmCancelOn,
                    ModifiedDate = cdn.ModifiedDate,
                    RefDocumentNo = cdn.InvNo,
                    Supplier = supplier
                };
            }

            case EInvoiceDocumentTypes.SelfBilledInvoice:
            {
                var query = db.PoSbInvoices
                    .Include(x => x.Details)
                    .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.DocNo == key.DocumentNo);
                var sbInvoice = tracking
                    ? await query.FirstOrDefaultAsync(cancellationToken)
                    : await query.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
                if (sbInvoice is null)
                {
                    return null;
                }

                return new EInvoiceDocumentState
                {
                    Entity = sbInvoice,
                    DocumentType = EInvoiceDocumentTypes.SelfBilledInvoice,
                    DocumentNo = sbInvoice.DocNo,
                    CompanyCode = sbInvoice.CompanyCode,
                    BranchCode = sbInvoice.BranchCode,
                    Status = sbInvoice.IrbmStatus,
                    Outcome = sbInvoice.IrbmOutcome,
                    SubmitId = sbInvoice.IrbmSubmitId,
                    Uuid = sbInvoice.IrbmUuid,
                    OriUuid = sbInvoice.IrbmOriUuid,
                    SentOn = sbInvoice.IrbmSentOn,
                    ValidOn = sbInvoice.IrbmValidOn,
                    Error = sbInvoice.IrbmError,
                    CancelOn = sbInvoice.IrnmCancelOn,
                    ModifiedDate = sbInvoice.ModifiedDate,
                    Supplier = supplier
                };
            }

            case EInvoiceDocumentTypes.SelfBilledCreditNote:
            case EInvoiceDocumentTypes.SelfBilledDebitNote:
            {
                var query = db.PoSbCdns
                    .Include(x => x.Details)
                    .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.DocNo == key.DocumentNo);
                var sbCdn = tracking
                    ? await query.FirstOrDefaultAsync(cancellationToken)
                    : await query.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
                if (sbCdn is null)
                {
                    return null;
                }

                // The ERP note type must match the family the caller asked for (CN vs DN).
                var expectedType = key.DocumentType == EInvoiceDocumentTypes.SelfBilledCreditNote ? "CN" : "DN";
                if (!string.Equals(sbCdn.Type, expectedType, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return new EInvoiceDocumentState
                {
                    Entity = sbCdn,
                    DocumentType = key.DocumentType,
                    DocumentNo = sbCdn.DocNo,
                    CompanyCode = sbCdn.CompanyCode,
                    BranchCode = sbCdn.BranchCode,
                    Status = sbCdn.IrbmStatus,
                    Outcome = sbCdn.IrbmOutcome,
                    SubmitId = sbCdn.IrbmSubmitId,
                    Uuid = sbCdn.IrbmUuid,
                    OriUuid = sbCdn.IrbmOriUuid,
                    SentOn = sbCdn.IrbmSentOn,
                    ValidOn = sbCdn.IrbmValidOn,
                    Error = sbCdn.IrbmError,
                    CancelOn = sbCdn.IrnmCancelOn,
                    ModifiedDate = sbCdn.ModifiedDate,
                    RefDocumentNo = sbCdn.OriginSbInvNo,
                    Supplier = supplier
                };
            }

            default:
                return null;
        }
    }
    /// <summary>Writes the e-Invoice state back onto the tracked entity.</summary>
    private static void ApplyState(EInvoiceDocumentState state)
    {
        switch (state.Entity)
        {
            case SaInvoice invoice:
                invoice.IrbmStatus = state.Status;
                invoice.IrbmOutcome = state.Outcome;
                invoice.IrbmSubmitId = state.SubmitId;
                invoice.IrbmUuid = state.Uuid;
                invoice.IrbmOriUuid = state.OriUuid;
                invoice.IrbmSentOn = state.SentOn;
                invoice.IrbmValidOn = state.ValidOn;
                invoice.IrbmError = state.Error;
                invoice.IrnmCancelOn = state.CancelOn;
                invoice.ModifiedDate = DateTime.UtcNow;
                break;

            case SaCdn cdn:
                cdn.IrbmStatus = state.Status;
                cdn.IrbmOutcome = state.Outcome;
                cdn.IrbmSubmitId = state.SubmitId;
                cdn.IrbmUuid = state.Uuid;
                cdn.IrbmOriUuid = state.OriUuid;
                cdn.IrbmSentOn = state.SentOn;
                cdn.IrbmValidOn = state.ValidOn;
                cdn.IrbmError = state.Error;
                cdn.IrnmCancelOn = state.CancelOn;
                cdn.ModifiedDate = DateTime.UtcNow;
                break;

            case PoSbInvoice sbInvoice:
                sbInvoice.IrbmStatus = state.Status;
                sbInvoice.IrbmOutcome = state.Outcome;
                sbInvoice.IrbmSubmitId = state.SubmitId;
                sbInvoice.IrbmUuid = state.Uuid;
                sbInvoice.IrbmOriUuid = state.OriUuid;
                sbInvoice.IrbmSentOn = state.SentOn;
                sbInvoice.IrbmValidOn = state.ValidOn;
                sbInvoice.IrbmError = state.Error;
                sbInvoice.IrnmCancelOn = state.CancelOn;
                sbInvoice.ModifiedDate = DateTime.UtcNow;
                break;

            case PoSbCdn sbCdn:
                sbCdn.IrbmStatus = state.Status;
                sbCdn.IrbmOutcome = state.Outcome;
                sbCdn.IrbmSubmitId = state.SubmitId;
                sbCdn.IrbmUuid = state.Uuid;
                sbCdn.IrbmOriUuid = state.OriUuid;
                sbCdn.IrbmSentOn = state.SentOn;
                sbCdn.IrbmValidOn = state.ValidOn;
                sbCdn.IrbmError = state.Error;
                sbCdn.IrnmCancelOn = state.CancelOn;
                sbCdn.ModifiedDate = DateTime.UtcNow;
                break;
        }
    }

    private async Task<EInvoiceSupplierProfile> LoadSupplierAsync(
        AppDbContext db,
        string companyCode,
        CancellationToken cancellationToken)
    {
        var company = await db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == companyCode, cancellationToken);

        return new EInvoiceSupplierProfile
        {
            CompanyCode = companyCode,
            Enabled = company?.EInvEnabled ?? false,
            CompanyName = company?.LegalName is { Length: > 0 } legal ? legal : company?.CompanyName,
            TinNo = company?.EInvOnBehalfTin,
            RegistrationNo = company?.RegistrationNo,
            RegType = company?.EInvRegType,
            SstNo = company?.EInvSstNo,
            MsicCode = company?.EInvMsicCode,
            BusinessDescription = company?.EInvBizDescription,
            Addr1 = company?.Address1,
            Addr2 = company?.Address2,
            Addr3 = company?.Address3,
            City = company?.City,
            // The dedicated LHDN codes win when set; otherwise the free-text company state/country is
            // translated by LhdnCodeLookup at map time (which fails closed on an unknown value).
            State = string.IsNullOrWhiteSpace(company?.EInvStateCode) ? company?.State : company.EInvStateCode,
            PostalCode = company?.PostCode,
            Country = string.IsNullOrWhiteSpace(company?.EInvCountryCode) ? company?.Country : company.EInvCountryCode,
            Phone = company?.Phone,
            Email = company?.Email,
            DocumentVersion = company?.EInvDocumentVersion,
            OnBehalfTin = company?.EInvOnBehalfTin
        };
    }

    /// <summary>
    /// Applies the per-company credentials for this request. Credential VALUES come from
    /// configuration (never from the database), and the store is scoped so nothing leaks between
    /// concurrent companies.
    /// </summary>
    private void ApplyCompanyCredentials(TenantScope scope, EInvoiceSupplierProfile? supplier)
    {
        var section = _configuration.GetSection($"Einvoice:Companies:{scope.CompanyCode}");
        _secrets.setCompanyCredentials(
            section["EInv_SecretID"],
            section["EInv_SecretKey"],
            section["EInv_CertPath"],
            section["EInv_CertPass"],
            supplier?.OnBehalfTin ?? section["EInv_OnBehalfTin"],
            supplier?.DocumentVersion ?? section["EInv_docversion"]);
    }

    private sealed class SourceBuildResult
    {
        public EInvoiceSourceDocument? Source { get; init; }
        public EInvoiceValidationReport Report { get; init; } = EInvoiceValidationReport.Valid;

        /// <summary>
        /// Set for an invoice whose buyer identity was taken live from the customer master (that is, not
        /// yet frozen). The submit claim freezes these values onto the document in the same commit as the
        /// <c>SUBMITTING</c> transition.
        /// </summary>
        public BuyerIdentity? ResolvedBuyer { get; init; }
    }

    private async Task<EInvoiceValidationReport> ValidateSourceAsync(
        AppDbContext db,
        EInvoiceDocumentState state,
        TenantScope scope,
        CancellationToken cancellationToken)
    {
        var build = await BuildSourceAsync(db, state, scope, cancellationToken);
        return build.Report;
    }

    /// <summary>
    /// Builds the payload source for one document. A thin dispatcher over per-family helpers.
    /// <para>
    /// <b>Rebuild window (do not break this).</b> The payload is built from the ERP document at exactly
    /// two entry points: <c>ValidateAsync</c> and <c>SubmitBatchAsync</c> (Submit / Retry).
    /// <c>RefreshAsync</c> only reads MyInvois and applies the mapped status, and <c>RecoverAsync</c>
    /// builds a source for reconciliation and never resubmits — so nothing here may be called from
    /// those paths. Together with the submit gate that refuses SUBMITTED/VALID, that is what guarantees a
    /// document already accepted by MyInvois can never be rebuilt from changed master data (the vendor
    /// block is read live from the vendor master, and there is deliberately no snapshot).
    /// </para>
    /// </summary>
    private async Task<SourceBuildResult> BuildSourceAsync(
        AppDbContext db,
        EInvoiceDocumentState state,
        TenantScope scope,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var taxGroups = await db.SaTaxGroups.AsNoTracking()
            .Where(x => x.CompanyCode == state.CompanyCode)
            .ToListAsync(cancellationToken);
        var taxLookup = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var taxTypeLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in taxGroups)
        {
            taxLookup[group.TaxGrCode] = group.Percentage;
            if (!string.IsNullOrWhiteSpace(group.TaxType))
            {
                taxTypeLookup[group.TaxGrCode] = group.TaxType.Trim();
            }
        }

        // ERP UOM code -> LHDN UNECE code. Line documents keep the ERP UOM (StdUom) for reporting and
        // inventory; only the e-Invoice payload is translated.
        var uomMasters = await db.MsUoms.AsNoTracking()
            .Where(x => x.CompanyCode == state.CompanyCode)
            .ToListAsync(cancellationToken);
        var uneceLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uom in uomMasters)
        {
            if (!string.IsNullOrWhiteSpace(uom.UneceUom))
            {
                uneceLookup[uom.UomCode] = uom.UneceUom.Trim();
            }
        }

        EInvoiceSourceDocument source;
        // Set only for an invoice whose buyer identity was read live from the customer master: the
        // SUBMITTING claim uses it to freeze exactly what is being signed. Null once frozen, and for CN/DN.
        BuyerIdentity? buyerIdentity = null;

        switch (state.Entity)
        {
            case SaInvoice invoice:
            {
                var lines = invoice.Details
                    .OrderBy(x => x.Line)
                    .Select(x => new EInvoiceSourceLine
                    {
                        Line = x.Line,
                        ItemCode = x.ICode,
                        ItemDesc = x.IDesc,
                        Uom = ResolveUneceUom(uneceLookup, x.StdUom, state.DocumentNo),
                        Qty = x.Qty,
                        UnitPrice = x.UnitPrice,
                        GrossAmount = x.Amount,
                        AmountExclTax = x.NetAmount,
                        TaxAmount = x.TaxAmt,
                        TaxType = ResolveTaxType(taxTypeLookup, x.TaxGrCode, state.DocumentNo),
                        TaxPercent = ResolveTaxPercent(taxLookup, x.TaxGrCode),
                        ClassificationCode = x.Classification
                    })
                    .ToList();

                // Buyer block. The ADDRESS and CONTACT always come from the customer master (D-1), so
                // correcting the profile is enough to repair an INVALID submission. The IDENTITY keeps its
                // live-vs-frozen rule: a frozen status is never actually submitted (it is blocked outright
                // or is Recover-only), so the frozen columns stay a pure audit record of what was sent.
                // ONE read of the master feeds both the identity and the address block.
                BuyerIdentity? resolvedBuyer = null;
                var frozenBuyer = EInvoiceStatuses.IsBuyerIdentityFrozen(state.Status, state.Outcome);
                var profile = await LoadBuyerProfileAsync(db, invoice.CompanyCode, invoice.CustCode, cancellationToken);
                BuyerIdentity buyer;
                if (frozenBuyer)
                {
                    buyer = new BuyerIdentity(
                        invoice.BuyerTin,
                        invoice.BuyerBrn,
                        invoice.BuyerRegType,
                        invoice.InvEmail,
                        invoice.GstregNo);
                }
                else if (profile is null)
                {
                    errors["Buyer.Customer"] =
                        $"Customer '{invoice.CustCode}' was not found, so its e-Invoice identity cannot be read.";
                    buyer = new BuyerIdentity(null, null, null, null, null);
                }
                else
                {
                    resolvedBuyer = ToBuyerIdentity(profile);
                    buyer = resolvedBuyer;
                }

                // The readiness gate runs on the live master too: a legacy/free-text registration type has
                // to be corrected on the customer, because the four canonical types are what LHDN accepts.
                if (!frozenBuyer
                    && !string.IsNullOrWhiteSpace(buyer.RegType)
                    && !EInvoiceRegistrationTypes.IsValid(buyer.RegType))
                {
                    errors["Buyer.RegType"] =
                        $"Customer '{invoice.CustCode}' registration type '{buyer.RegType}' is not one of " +
                        "BRN, NRIC, PASSPORT or ARMY. Correct it on the customer profile.";
                }

                source = new EInvoiceSourceDocument
                {
                    DocumentType = EInvoiceDocumentTypes.Invoice,
                    DocumentNo = invoice.InvNo,
                    DocumentDate = invoice.InvDate,
                    Currency = invoice.Currency,
                    CurrRate = invoice.CurrRate,
                    AmountExclTax = invoice.GrossAmnt,
                    TaxAmount = invoice.Taxes,
                    AmountIncTax = invoice.TotAmnt,
                    CustomerName = profile?.Name,
                    CustomerTin = buyer.Tin,
                    CustomerRegNo = buyer.RegNo,
                    CustomerRegType = buyer.RegType,
                    CustomerSstNo = buyer.SstNo,
                    CustomerAddr1 = profile?.Address1,
                    CustomerAddr2 = profile?.Address2,
                    CustomerAddr3 = profile?.Address3,
                    CustomerAddr4 = profile?.Address4,
                    CustomerCity = profile?.City,
                    CustomerState = profile?.State,
                    CustomerPostalCode = profile?.PostalCode,
                    CustomerCountry = profile?.Country,
                    CustomerPhone = profile?.Phone,
                    CustomerEmail = profile?.Email,
                    Lines = lines
                };
                buyerIdentity = resolvedBuyer;
                break;
            }

            case SaCdn cdn:
            {
                var documentType = string.Equals(cdn.Type, "CN", StringComparison.OrdinalIgnoreCase)
                    ? EInvoiceDocumentTypes.CreditNote
                    : EInvoiceDocumentTypes.DebitNote;

                // CN/DN must reference a VALID source invoice with a MyInvois UUID.
                SaInvoice? origin = null;
                if (!string.IsNullOrWhiteSpace(cdn.InvNo))
                {
                    origin = await db.SaInvoices.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.CompanyCode == cdn.CompanyCode
                                                  && x.BranchCode == cdn.BranchCode
                                                  && x.InvNo == cdn.InvNo, cancellationToken);
                }

                if (origin is null)
                {
                    errors["OriginInvoice.No"] =
                        "The original invoice referenced by this credit/debit note was not found.";
                }
                else if (!string.Equals(EInvoiceStatuses.Normalize(origin.IrbmStatus), EInvoiceStatuses.Valid, StringComparison.Ordinal)
                         || string.IsNullOrWhiteSpace(origin.IrbmUuid))
                {
                    errors["OriginInvoice.Uuid"] =
                        "The original invoice does not have a VALID MyInvois e-Invoice, so a credit/debit note cannot be submitted against it.";
                }

                var lines = cdn.Details
                    .OrderBy(x => x.Line)
                    .Select(x => new EInvoiceSourceLine
                    {
                        Line = x.Line,
                        ItemCode = x.ICode,
                        ItemDesc = x.IDesc,
                        Uom = ResolveUneceUom(uneceLookup, x.StdUom, cdn.DocNo),
                        Qty = x.Qty,
                        UnitPrice = x.UnitPrice,
                        GrossAmount = x.Amount,
                        AmountExclTax = x.NetAmount,
                        TaxAmount = x.TaxAmt,
                        TaxType = ResolveTaxType(taxTypeLookup, x.TaxGroup, cdn.DocNo),
                        TaxPercent = ResolveTaxPercent(taxLookup, x.TaxGroup),
                        ClassificationCode = x.Classification
                    })
                    .ToList();

                // The note's buyer ADDRESS and CONTACT come from the customer master (D-1); its IDENTITY is
                // the origin invoice's submitted values (D-2) because a note has to match the invoice it
                // references at MyInvois. D-2a: a legacy origin that reached VALID with blank Buyer*
                // columns would otherwise send a null buyer, so the master is used instead.
                var profile = await LoadBuyerProfileAsync(db, cdn.CompanyCode, cdn.CustCode, cancellationToken);
                var originIdentity = origin is null
                    ? null
                    : new BuyerIdentity(
                        origin.BuyerTin,
                        origin.BuyerBrn,
                        origin.BuyerRegType,
                        origin.InvEmail,
                        origin.GstregNo);
                var noteIdentity = ResolveNoteIdentity(originIdentity, profile);

                source = new EInvoiceSourceDocument
                {
                    DocumentType = documentType,
                    DocumentNo = cdn.DocNo,
                    DocumentDate = cdn.DocDate,
                    RefDocumentNo = cdn.InvNo,
                    OriginUuid = origin?.IrbmUuid,
                    Currency = cdn.Currency,
                    CurrRate = cdn.CurrRate == 0m ? 1m : cdn.CurrRate,
                    AmountExclTax = cdn.GrossAmnt,
                    TaxAmount = cdn.Taxes,
                    AmountIncTax = cdn.TotAmnt,
                    CustomerName = profile?.Name ?? cdn.CustName,
                    CustomerTin = noteIdentity?.Tin,
                    CustomerRegNo = noteIdentity?.RegNo,
                    CustomerRegType = noteIdentity?.RegType,
                    CustomerSstNo = noteIdentity?.SstNo,
                    CustomerAddr1 = profile?.Address1,
                    CustomerAddr2 = profile?.Address2,
                    CustomerAddr3 = profile?.Address3,
                    CustomerAddr4 = profile?.Address4,
                    CustomerCity = profile?.City,
                    CustomerState = profile?.State,
                    CustomerPostalCode = profile?.PostalCode,
                    CustomerCountry = profile?.Country,
                    CustomerPhone = profile?.Phone,
                    CustomerEmail = profile?.Email,
                    Lines = lines
                };
                break;
            }

            case PoSbInvoice sbInvoice:
            {
                var sbContext = new SbSourceContext(
                    db, errors, taxLookup, taxTypeLookup, uneceLookup, state.Supplier!);
                source = await BuildSbInvoiceSourceAsync(sbContext, sbInvoice, cancellationToken);
                break;
            }

            case PoSbCdn sbCdn:
            {
                var sbContext = new SbSourceContext(
                    db, errors, taxLookup, taxTypeLookup, uneceLookup, state.Supplier!);
                source = await BuildSbCdnSourceAsync(sbContext, sbCdn, cancellationToken);
                break;
            }

            default:
                return new SourceBuildResult
                {
                    Report = EInvoiceValidationReport.From(
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Document"] = "Unsupported e-Invoice document type."
                        })
                };
        }

        var report = _validator.Validate(source, state.Supplier!);
        if (!report.IsValid)
        {
            foreach (var error in report.Errors)
            {
                errors[error.Key] = error.Value;
            }
        }

        return new SourceBuildResult
        {
            Source = source,
            ResolvedBuyer = buyerIdentity,
            Report = errors.Count == 0 ? EInvoiceValidationReport.Valid : EInvoiceValidationReport.From(errors)
        };
    }

    // ─────────────────────────── Self-billed (11/12/13) source builders ───────────────────────────

    /// <summary>
    /// State bag for the self-billed source builders: the open context, the error dictionary the caller
    /// turns into the report, the three code lookups resolved once per build, and the COMPANY profile
    /// that supplies the buyer block (a self-billed document is issued by the buyer).
    /// </summary>
    private sealed record SbSourceContext(
        AppDbContext Db,
        Dictionary<string, string> Errors,
        IReadOnlyDictionary<string, decimal> TaxPercents,
        IReadOnlyDictionary<string, string> TaxTypes,
        IReadOnlyDictionary<string, string> UneceUoms,
        EInvoiceSupplierProfile Company);

    /// <summary>The raw line fields a self-billed document contributes to the payload.</summary>
    private sealed record SbLineFields(
        short Line,
        string? ICode,
        string? IDesc,
        string? StdUom,
        decimal Qty,
        decimal UnitPrice,
        decimal Amount,
        decimal NetAmount,
        decimal TaxAmt,
        string? TaxGroup,
        string? Classification);

    /// <summary>
    /// Builds the payload source for a self-billed invoice (LHDN 11).
    /// <para>
    /// The parties are REVERSED: a self-billed document is issued by the buyer, so the VENDOR supplies
    /// the payload's <c>Supplier</c> block (read live from the vendor master) while OUR COMPANY supplies
    /// the <c>Customer</c> block. An invoice never references an origin, so neither <c>RefDocumentNo</c>
    /// nor <c>OriginUuid</c> is set.
    /// </para>
    /// </summary>
    private async Task<EInvoiceSourceDocument> BuildSbInvoiceSourceAsync(
        SbSourceContext context, PoSbInvoice invoice, CancellationToken cancellationToken)
    {
        var vendor = await LoadVendorPartyAsync(
            context.Db, invoice.CompanyCode, invoice.VendorCode, context.Errors, cancellationToken);
        var buyer = CompanyAsBuyer(context.Company);

        return new EInvoiceSourceDocument
        {
            DocumentType = EInvoiceDocumentTypes.SelfBilledInvoice,
            DocumentNo = invoice.DocNo,
            DocumentDate = invoice.DocDate,
            Currency = invoice.Currency,
            CurrRate = invoice.CurrRate == 0m ? 1m : invoice.CurrRate,
            AmountExclTax = invoice.GrossAmnt,
            TaxAmount = invoice.Taxes,
            AmountIncTax = invoice.TotAmnt,
            SupplierParty = vendor,
            CustomerName = buyer.Name,
            CustomerTin = buyer.Tin,
            CustomerRegNo = buyer.RegNo,
            CustomerRegType = buyer.RegType,
            CustomerSstNo = buyer.SstNo,
            CustomerAddr1 = buyer.Address1,
            CustomerAddr2 = buyer.Address2,
            CustomerAddr3 = buyer.Address3,
            CustomerAddr4 = buyer.Address4,
            CustomerCity = buyer.City,
            CustomerState = buyer.State,
            CustomerPostalCode = buyer.PostalCode,
            CustomerCountry = buyer.Country,
            CustomerPhone = buyer.Phone,
            CustomerEmail = buyer.Email,
            Lines = invoice.Details
                .OrderBy(x => x.Line)
                .Select(x => MapSbLine(
                    context,
                    new SbLineFields(x.Line, x.ICode, x.IDesc, x.StdUom, x.Qty, x.UnitPrice,
                        x.Amount, x.NetAmount, x.TaxAmt, x.TaxGroup, x.Classification),
                    invoice.DocNo))
                .ToList()
        };
    }

    /// <summary>
    /// Builds the payload source for a self-billed credit / debit note (LHDN 12 / 13).
    /// <para>
    /// The parties are reversed exactly as for the self-billed invoice: the VENDOR is the payload's
    /// <c>Supplier</c> and OUR COMPANY (which issues the note) is the <c>Customer</c>.
    /// </para>
    /// <para>
    /// The origin is resolved through <see cref="PoSbOriginResolver"/>, which owns every rule (same
    /// company and branch, self-billed family, live document, VALID at MyInvois, UUID present) so the
    /// save-time warning and this hard gate cannot drift apart.
    /// </para>
    /// </summary>
    private async Task<EInvoiceSourceDocument> BuildSbCdnSourceAsync(
        SbSourceContext context, PoSbCdn cdn, CancellationToken cancellationToken)
    {
        var documentType = string.Equals(cdn.Type, PoSbTypes.DebitNote, StringComparison.OrdinalIgnoreCase)
            ? EInvoiceDocumentTypes.SelfBilledDebitNote
            : EInvoiceDocumentTypes.SelfBilledCreditNote;

        var origin = await PoSbOriginResolver.ResolveValidAsync(
            context.Db, cdn.CompanyCode, cdn.BranchCode, cdn.OriginSbInvNo, cancellationToken);
        if (!origin.Ok)
        {
            context.Errors[origin.ErrorKey] = origin.Message;
        }

        var vendor = await LoadVendorPartyAsync(
            context.Db, cdn.CompanyCode, cdn.VendorCode, context.Errors, cancellationToken);
        var buyer = CompanyAsBuyer(context.Company);

        return new EInvoiceSourceDocument
        {
            DocumentType = documentType,
            DocumentNo = cdn.DocNo,
            DocumentDate = cdn.DocDate,
            RefDocumentNo = cdn.OriginSbInvNo,
            OriginUuid = origin.Origin?.IrbmUuid,
            Currency = cdn.Currency,
            CurrRate = cdn.CurrRate == 0m ? 1m : cdn.CurrRate,
            AmountExclTax = cdn.GrossAmnt,
            TaxAmount = cdn.Taxes,
            AmountIncTax = cdn.TotAmnt,
            SupplierParty = vendor,
            CustomerName = buyer.Name,
            CustomerTin = buyer.Tin,
            CustomerRegNo = buyer.RegNo,
            CustomerRegType = buyer.RegType,
            CustomerSstNo = buyer.SstNo,
            CustomerAddr1 = buyer.Address1,
            CustomerAddr2 = buyer.Address2,
            CustomerAddr3 = buyer.Address3,
            CustomerAddr4 = buyer.Address4,
            CustomerCity = buyer.City,
            CustomerState = buyer.State,
            CustomerPostalCode = buyer.PostalCode,
            CustomerCountry = buyer.Country,
            CustomerPhone = buyer.Phone,
            CustomerEmail = buyer.Email,
            Lines = cdn.Details
                .OrderBy(x => x.Line)
                .Select(x => MapSbLine(
                    context,
                    new SbLineFields(x.Line, x.ICode, x.IDesc, x.StdUom, x.Qty, x.UnitPrice,
                        x.Amount, x.NetAmount, x.TaxAmt, x.TaxGroup, x.Classification),
                    cdn.DocNo))
                .ToList()
        };
    }

    /// <summary>
    /// The COMPANY's contribution to a self-billed payload: it is the payload's <c>Customer</c>, because
    /// a self-billed document is issued by the buyer. (The mapping of the company into a SUPPLIER block
    /// lives in <see cref="EInvoiceDocumentMapper"/> and is used by the sales families.)
    /// </summary>
    /// <remarks>
    /// The state and country already carry the <c>EInvStateCode ?? State</c> precedence resolved by
    /// <see cref="LoadSupplierAsync"/>; the mapper is what translates them to LHDN codes.
    /// </remarks>
    private static CompanyBuyerFields CompanyAsBuyer(EInvoiceSupplierProfile company) => new(
        company.CompanyName,
        company.TinNo,
        company.RegistrationNo,
        NormalizeRegistrationType(company.RegType),
        company.SstNo,
        company.Addr1,
        company.Addr2,
        company.Addr3,
        company.Addr4,
        company.City,
        company.State,
        company.PostalCode,
        company.Country,
        company.Phone,
        company.Email);

    /// <summary>The company's fields for a self-billed payload's Customer block.</summary>
    private sealed record CompanyBuyerFields(
        string? Name,
        string? Tin,
        string? RegNo,
        string? RegType,
        string? SstNo,
        string? Address1,
        string? Address2,
        string? Address3,
        string? Address4,
        string? City,
        string? State,
        string? PostalCode,
        string? Country,
        string? Phone,
        string? Email);

    private EInvoiceSourceLine MapSbLine(SbSourceContext context, SbLineFields line, string? documentNo) => new()
    {
        Line = line.Line,
        ItemCode = line.ICode,
        ItemDesc = line.IDesc,
        Uom = ResolveUneceUom(context.UneceUoms, line.StdUom, documentNo),
        Qty = line.Qty,
        UnitPrice = line.UnitPrice,
        GrossAmount = line.Amount,
        AmountExclTax = line.NetAmount,
        TaxAmount = line.TaxAmt,
        TaxType = ResolveTaxType(context.TaxTypes, line.TaxGroup, documentNo),
        TaxPercent = ResolveTaxPercent(context.TaxPercents, line.TaxGroup),
        ClassificationCode = line.Classification
    };

    /// <summary>
    /// Reads the VENDOR master for the payload's supplier block on a self-billed document. A missing or
    /// inactive vendor is reported with a field-keyed error rather than being guessed — an e-Invoice
    /// cannot be issued against a supplier the ERP does not know.
    /// </summary>
    private static async Task<PoSupplierPartyProfile?> LoadVendorPartyAsync(
        AppDbContext db,
        string companyCode,
        string? vendorCode,
        Dictionary<string, string> errors,
        CancellationToken cancellationToken)
    {
        var code = string.IsNullOrWhiteSpace(vendorCode) ? null : vendorCode.Trim();
        if (code is null)
        {
            errors["Supplier.Vendor"] =
                "The document has no vendor, so the payload's supplier block cannot be read.";
            return null;
        }

        var vendor = await db.PoSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == companyCode && x.SuppCode == code, cancellationToken);

        if (vendor is null)
        {
            errors["Supplier.Vendor"] =
                $"Vendor '{code}' was not found, so the payload's supplier block cannot be read.";
            return null;
        }

        if (!vendor.IsActive)
        {
            errors["Supplier.Vendor"] =
                $"Vendor '{code}' is inactive, so it cannot be used on a self-billed e-Invoice.";
        }

        return PoSupplierPartyProfileResolver.Resolve(vendor);
    }

    /// <summary>
    /// The vendor registration type normalised to the four canonical LHDN types. The stored value is
    /// returned unchanged when it is not one of them, so the validator refuses it with an actionable
    /// message instead of the payload carrying a silent substitution.
    /// </summary>
    private static string? NormalizeRegistrationType(string? regType) =>
        EInvoiceRegistrationTypes.Normalize(regType) ?? regType;

    /// <summary>
    /// One read of the customer master for the e-Invoice buyer identity. Both the payload and (at the
    /// SUBMITTING claim) the frozen snapshot come from this single result.
    /// </summary>
    /// <summary>
    /// The resolved e-Invoice buyer block for a document's customer, or null when the master row is gone.
    /// Both the identity and the billing address of a submitted document come from this one read.
    /// </summary>
    private static async Task<SaCustBuyerProfile?> LoadBuyerProfileAsync(
        AppDbContext db,
        string companyCode,
        string custCode,
        CancellationToken cancellationToken)
    {
        var customer = await db.SaCusts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == companyCode && x.CustCode == custCode, cancellationToken);

        return customer is null ? null : SaCustBuyerProfileResolver.Resolve(customer);
    }

    /// <summary>
    /// The identity half of a resolved buyer block, normalised to the four canonical LHDN registration
    /// types — the same shape the frozen columns hold, so a live read and a frozen read cannot diverge.
    /// </summary>
    private static BuyerIdentity ToBuyerIdentity(SaCustBuyerProfile profile) => new(
        profile.Tin,
        profile.RegNo,
        EInvoiceRegistrationTypes.Normalize(profile.RegType) ?? profile.RegType,
        profile.Email,
        profile.SstNo);

    /// <summary>
    /// The buyer identity a credit/debit note should carry. D-2: the origin invoice's submitted values,
    /// because the note must match the invoice it references. D-2a: fall back to the customer master when
    /// the origin carries no frozen identity — a legacy invoice that reached VALID before the frozen
    /// columns were populated — so the note is not refused with an unexplained missing buyer.
    /// </summary>
    private static BuyerIdentity? ResolveNoteIdentity(
        BuyerIdentity? fromOrigin,
        SaCustBuyerProfile? fromMaster) =>
        fromOrigin is not null && !IsBlankIdentity(fromOrigin)
            ? fromOrigin
            : fromMaster is null ? null : ToBuyerIdentity(fromMaster);

    /// <summary>True when a frozen identity holds nothing usable.</summary>
    private static bool IsBlankIdentity(BuyerIdentity identity) =>
        string.IsNullOrWhiteSpace(identity.Tin)
        && string.IsNullOrWhiteSpace(identity.RegNo)
        && string.IsNullOrWhiteSpace(identity.RegType)
        && string.IsNullOrWhiteSpace(identity.Email)
        && string.IsNullOrWhiteSpace(identity.SstNo);

    /// <summary>
    /// Writes the live customer identity onto the tracked document so the commit that claims
    /// <c>SUBMITTING</c> also records what is about to be sent. No-op for CN/DN and for a document whose
    /// identity is already frozen.
    /// </summary>
    private static void ApplyBuyerFreeze(EInvoiceDocumentState state, BuyerIdentity? buyer)
    {
        if (buyer is null || state.Entity is not SaInvoice invoice)
        {
            return;
        }

        invoice.BuyerTin = Truncate(buyer.Tin, 20);
        invoice.BuyerBrn = Truncate(buyer.RegNo, 50);
        invoice.BuyerRegType = Truncate(buyer.RegType, 20);
        invoice.InvEmail = Truncate(buyer.Email, 100);
        invoice.GstregNo = Truncate(buyer.SstNo, 50);
    }

    /// <summary>
    /// The buyer identity as one immutable snapshot. Frozen columns and the customer master both
    /// resolve to this shape, so the payload is built from exactly one source per attempt.
    /// </summary>
    private sealed record BuyerIdentity(
        string? Tin,
        string? RegNo,
        string? RegType,
        string? Email,
        string? SstNo);

    private static double? ResolveTaxPercent(IReadOnlyDictionary<string, decimal> lookup, string? taxGroupCode)
    {
        // A blank tax group means "this line carries no tax", so the percentage is 0 - the counterpart of
        // ResolveTaxType substituting the LHDN default (06, "Not Applicable") for the same input. Returning
        // null here made EInvoiceValidator refuse the line with "Tax percentage cannot be negative", so an
        // empty item tax still blocked the whole submission. An unknown (non-blank) code already resolved
        // to 0, so this only removes the blank-code special case.
        if (string.IsNullOrWhiteSpace(taxGroupCode))
        {
            return 0d;
        }

        return lookup.TryGetValue(taxGroupCode, out var percent) ? (double)percent : 0d;
    }

    /// <summary>
    /// ERP UOM code (<c>MsUOM.UOMCode</c>) mapped to the LHDN UNECE code the payload must carry.
    /// Blank input falls back silently; a non-blank code that cannot be resolved (no UOM master row, or
    /// the master's UNECE_UOM is blank) falls back to <see cref="LhdnDefaults.UneceUom"/> and logs a
    /// warning — the default keeps the payload valid, the warning keeps the master-data gap visible.
    /// Never fails the submission for this alone.
    /// </summary>
    private string ResolveUneceUom(
        IReadOnlyDictionary<string, string> lookup,
        string? uomCode,
        string? documentNo)
    {
        if (string.IsNullOrWhiteSpace(uomCode))
        {
            return LhdnDefaults.UneceUom;
        }

        var erpUom = uomCode.Trim();
        if (lookup.TryGetValue(erpUom, out var unece) && !string.IsNullOrWhiteSpace(unece))
        {
            return unece;
        }

        _logger.LogWarning(
            "e-Invoice UOM fallback for document {DocumentNo}: ERP UOM '{Uom}' has no UNECE code on the UOM master. Using {Default}.",
            documentNo,
            erpUom,
            LhdnDefaults.UneceUom);
        return LhdnDefaults.UneceUom;
    }

    /// <summary>
    /// ERP tax group code (<c>SaTaxGroup.TaxGrCode</c>) mapped to the LHDN tax type carried by the
    /// payload. Blank input falls back silently; a non-blank code that cannot be resolved (no tax group
    /// row, or the row's TaxType is blank) falls back to <see cref="LhdnDefaults.TaxType"/> and logs a
    /// warning — the default keeps the payload valid, the warning keeps the master-data gap visible.
    /// Never fails the submission for this alone.
    /// </summary>
    private string ResolveTaxType(
        IReadOnlyDictionary<string, string> lookup,
        string? taxGroupCode,
        string? documentNo)
    {
        if (string.IsNullOrWhiteSpace(taxGroupCode))
        {
            return LhdnDefaults.TaxType;
        }

        var erpTaxGroup = taxGroupCode.Trim();
        if (lookup.TryGetValue(erpTaxGroup, out var taxType) && !string.IsNullOrWhiteSpace(taxType))
        {
            return taxType;
        }

        _logger.LogWarning(
            "e-Invoice tax type fallback for document {DocumentNo}: tax group '{TaxGroup}' has no LHDN tax type. Using {Default}.",
            documentNo,
            erpTaxGroup,
            LhdnDefaults.TaxType);
        return LhdnDefaults.TaxType;
    }

    // ─────────────────────────────── Audit / attempts / authorization ───────────────────────────────

    private static async Task AppendLogAsync(
        AppDbContext db,
        EInvoiceDocumentState state,
        string action,
        string? status,
        int attemptNo,
        Guid correlationId,
        DateTime? requestTime,
        DateTime? responseTime,
        long? durationMs,
        string? errorCode,
        string? errorMessage,
        string? userId,
        CancellationToken cancellationToken)
    {
        db.SaEInvoiceLogs.Add(new SaEInvoiceLog
        {
            CompanyCode = state.CompanyCode,
            DocumentType = state.DocumentType,
            DocumentNo = state.DocumentNo,
            SubmissionId = state.SubmitId,
            DocumentUuid = state.Uuid,
            Action = action,
            Status = status,
            AttemptNo = attemptNo,
            CorrelationId = correlationId,
            RequestTime = requestTime,
            ResponseTime = responseTime,
            DurationMs = durationMs,
            ErrorCode = Truncate(errorCode, 100),
            ErrorMessage = Truncate(errorMessage, 4000),
            CreatedBy = Truncate(userId, 20),
            CreatedOn = DateTime.UtcNow
        });

        await Task.CompletedTask;
    }

    /// <summary>
    /// Next position in this document's e-Invoice attempt chain. Every audit row (validate, submit,
    /// recover, refresh, cancel, retry) takes the next number, so the chain reads 1, 2, 3… and a
    /// Submit → timeout → Recover → Valid story stays traceable in order.
    /// </summary>
    private static async Task<int> NextAttemptAsync(
        AppDbContext db,
        EInvoiceDocumentState state,
        CancellationToken cancellationToken)
    {
        var max = await db.SaEInvoiceLogs.AsNoTracking()
            .Where(x => x.CompanyCode == state.CompanyCode
                        && x.DocumentType == state.DocumentType
                        && x.DocumentNo == state.DocumentNo)
            .Select(x => (int?)x.AttemptNo)
            .MaxAsync(cancellationToken);

        return (max ?? 0) + 1;
    }

    private sealed class AuthorizationGate
    {
        public TenantScope? Scope { get; init; }
        public SaEInvoiceResult? Error { get; init; }
    }

    private async Task<AuthorizationGate> AuthorizeAsync(
        SaEInvoiceDocumentKey key,
        string permission,
        CancellationToken cancellationToken)
    {
        if ((!EInvoiceDocumentTypes.IsKnown(key.DocumentType) && !EInvoiceDocumentTypes.IsSelfBilled(key.DocumentType))
            || string.IsNullOrWhiteSpace(key.DocumentNo))
        {
            return new AuthorizationGate
            {
                Error = SaEInvoiceResult.Fail(key, "Unknown e-Invoice document type or number.", SaEInvoiceErrorKind.Validation)
            };
        }

        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return new AuthorizationGate
            {
                Error = SaEInvoiceResult.Fail(key, "Invalid company or branch context.", SaEInvoiceErrorKind.Authorization)
            };
        }

        var menuCode = key.DocumentType switch
        {
            EInvoiceDocumentTypes.CreditNote => MenuCodes.SalesCreditNote,
            EInvoiceDocumentTypes.DebitNote => MenuCodes.SalesDebitNote,
            // A self-billed document is issued by the buyer, so it is authorized through its own
            // Purchase self-billed menu — never through the ordinary purchase invoice/CN/DN menus.
            EInvoiceDocumentTypes.SelfBilledCreditNote => MenuCodes.PurchaseSbCreditNote,
            EInvoiceDocumentTypes.SelfBilledDebitNote => MenuCodes.PurchaseSbDebitNote,
            EInvoiceDocumentTypes.SelfBilledInvoice => MenuCodes.PurchaseSbInvoice,
            _ => MenuCodes.SalesInvoice
        };

        if (!await _accessRights.CanAsync(menuCode, permission, cancellationToken))
        {
            return new AuthorizationGate
            {
                Error = SaEInvoiceResult.Fail(key, "Not authorized.", SaEInvoiceErrorKind.Authorization)
            };
        }

        return new AuthorizationGate { Scope = scope };
    }

    // ─────────────────────────────── Small helpers ───────────────────────────────

    /// <summary>
    /// True when the loaded source document may be submitted. A <b>sales</b> invoice and a sales
    /// credit/debit note must be POSTED — the LHDN payload has to describe a finalised document. The
    /// <b>self-billed</b> families (SBI / SBC / SBD) have no such rule: their ERP NEW/POSTED dimension is
    /// retired, so the e-Invoice state alone decides (see <see cref="EInvoiceStatuses.IsLocked"/>).
    /// The UI gates this too, but the service re-checks so a job or an API caller cannot bypass it.
    /// </summary>
    private static bool IsSourceDocumentSubmittable(EInvoiceDocumentState state) => state.Entity switch
    {
        SaInvoice invoice =>
            string.Equals(invoice.Status, SaInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase),
        SaCdn cdn =>
            string.Equals(cdn.Status, CdnStatuses.Posted, StringComparison.OrdinalIgnoreCase),
        // Self-billed documents fall through: there is nothing else to require of them.
        _ => true
    };

    /// <summary>The operator-facing noun for an e-Invoice document type, for refusal messages.</summary>
    private static string DocumentTypeLabel(string? documentType) => documentType switch
    {
        EInvoiceDocumentTypes.CreditNote => "Credit note",
        EInvoiceDocumentTypes.DebitNote => "Debit note",
        EInvoiceDocumentTypes.SelfBilledInvoice => "Self-billed invoice",
        EInvoiceDocumentTypes.SelfBilledCreditNote => "Self-billed credit note",
        EInvoiceDocumentTypes.SelfBilledDebitNote => "Self-billed debit note",
        _ => "Invoice"
    };

    private bool IsStuck(EInvoiceDocumentState state)
    {
        if (_options.SubmittingStuckMinutes <= 0)
        {
            return true;
        }

        var lastTouch = new[] { state.ModifiedDate, state.SentOn }
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        if (lastTouch == DateTime.MinValue)
        {
            // Nothing to measure from: leave recovery to an explicit operator action.
            return false;
        }

        return DateTime.UtcNow - lastTouch > TimeSpan.FromMinutes(_options.SubmittingStuckMinutes);
    }

    /// <summary>
    /// Creates the configured <c>Einvoice:EInv_JsonPath</c> folder when it is missing.
    /// <para>
    /// The library writes its signing scratch file into that folder <i>before</i> it creates the
    /// folder itself, so a fresh deployment would otherwise fail the first submission with
    /// <see cref="DirectoryNotFoundException"/>. The path is resolved the same way the library
    /// resolves it (relative to the process working directory), so both point at the same folder.
    /// Never throws: a bad path surfaces as a normal MyInvois/transport failure from the library.
    /// </para>
    /// </summary>
    private void EnsureJsonPathDirectory()
    {
        var jsonPath = _configuration[$"{EInvoiceOptions.SectionName}:EInv_JsonPath"];
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(jsonPath);
        }
        catch (Exception ex)
        {
            // The submission itself will fail with a clearer error if the folder really is unusable.
            _logger.LogWarning(ex, "Could not create the e-Invoice JSON folder {JsonPath}", jsonPath);
        }
    }

    /// <summary>
    /// Friendly text for the documented MyInvois cancellation failures. The raw error code and full
    /// message still go to <c>SaEInvoiceLog</c>; this only makes the short field useful.
    /// </summary>
    private static string DescribeCancelError(GeneralResult<CancelRespone> result)
    {
        var message = result.error;
        switch (result.errorCode)
        {
            case "OperationPeriodOver":
                return "The MyInvois cancellation window has passed. Issue a credit/debit note instead of cancelling.";
            case "IncorrectState":
                return "MyInvois will not cancel this e-Invoice in its current state; refresh the status first.";
            case "ActiveReferencingDocuments":
                return "Another document (for example a credit note) references this e-Invoice. Cancel the referencing document first.";
            default:
                return string.IsNullOrWhiteSpace(message) ? "MyInvois rejected the cancellation." : message!;
        }
    }

    private static SaEInvoiceResult Ok(SaEInvoiceDocumentKey key, EInvoiceDocumentState state) =>
        new()
        {
            Succeeded = true,
            ErrorKind = SaEInvoiceErrorKind.None,
            DocumentType = key.DocumentType,
            DocumentNo = key.DocumentNo,
            Status = EInvoiceStatuses.Normalize(state.Status),
            Outcome = state.Outcome,
            Uuid = state.Uuid,
            SubmissionId = state.SubmitId
        };

    private static SaEInvoiceResult NotFound(SaEInvoiceDocumentKey key) =>
        SaEInvoiceResult.Fail(key, "The document was not found.", SaEInvoiceErrorKind.NotFound);

    private static SaEInvoiceResult ConcurrencyFailure(SaEInvoiceDocumentKey key) =>
        SaEInvoiceResult.Fail(key,
            "This document was changed by another user. Reload before trying again.",
            SaEInvoiceErrorKind.Concurrency);

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string? JoinError(string? errorCode, string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage;
        }

        return string.IsNullOrWhiteSpace(errorMessage) ? errorCode : errorCode + " " + errorMessage;
    }

    private enum ReconcileOutcome
    {
        Uncertain,
        Reconciled,
        ConfirmedAbsent
    }

    private sealed class SubmitClassification
    {
        public string Status { get; init; } = EInvoiceStatuses.Failed;
        public string? Outcome { get; init; }
        public string? ErrorCode { get; init; }
        public string? Error { get; init; }
        public string? Uuid { get; init; }
        public string? SubmissionId { get; init; }

        /// <summary>
        /// MyInvois' own code number for an ACCEPTED document - the submission registry's
        /// <c>internalId</c>. Null for anything that was not accepted, because nothing else is recorded.
        /// </summary>
        public string? InternalId { get; init; }

        /// <summary>Documents this submission carried - the registry's <c>DocumentCount</c>.</summary>
        public int? DocumentCount { get; set; }

        /// <summary>
        /// Submission-level <c>overallStatus</c> for the registry. Only set when <b>every</b> document in
        /// the submission was accepted: a mixed batch has no single submission-level value, so this stays
        /// null and a later Refresh/Recover fills it in from the API.
        /// </summary>
        public string? OverallStatus { get; set; }
    }

    /// <summary>One selected document inside a batch run, carrying its full result.</summary>
    private sealed class BatchRowOutcome
    {
        public SaEInvoiceDocumentKey Key { get; init; } = new();

        /// <summary>True when the row was never attempted because it was ineligible.</summary>
        public bool Skipped { get; set; }

        /// <summary>Null until the row has been judged.</summary>
        public SaEInvoiceResult? Result { get; set; }
    }

    /// <summary>One refresh-all candidate: enough to build a key and to explain a skip.</summary>
    private sealed record RefreshCandidate(string DocumentNo, string? IrbmStatus, string? IrbmUuid);

    /// <summary>Candidate-load outcome: either the candidates, or the refusal that replaced them.</summary>
    private sealed record RefreshCandidateLoad(
        IReadOnlyList<RefreshCandidate> Candidates,
        SaEInvoiceBatchResult? Refused);

    /// <summary>A document that passed validation and holds a committed <c>SUBMITTING</c> claim.</summary>
    private sealed class BatchClaim
    {
        public BatchRowOutcome Row { get; init; } = null!;
        public EInvoiceDocumentState State { get; init; } = null!;
        public int AttemptNo { get; init; }
        public DateTime RequestTime { get; init; }
        public long DurationMs { get; set; }
        public SubmitClassification Classification { get; set; } = new();
    }

    private sealed class SubmitBatchRun
    {
        /// <summary>Set when the whole action was refused before any work, so no row result exists.</summary>
        public SaEInvoiceResult? Refusal { get; init; }

        public List<BatchRowOutcome> Rows { get; init; } = [];
    }

    private sealed class SearchOutcome
    {
        public bool Found { get; init; }
        public bool SearchWasConclusive { get; init; }
        public string? Uuid { get; init; }
        public string? Status { get; init; }
        public string? SubmissionId { get; init; }
        public DateTime? ValidatedOn { get; init; }
        public string? ErrorCode { get; init; }
        public string? Error { get; init; }

        public static SearchOutcome Inconclusive(string? error, string? errorCode = null) =>
            new() { SearchWasConclusive = false, Error = error, ErrorCode = errorCode };
    }
}
