using System.Diagnostics;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.EInvoiceLib.GenerateDoc;
using ErpWeb.EInvoiceLib.Interface;
using ErpWeb.EInvoiceLib.Model;
using ErpWeb.EInvoiceLib.Model.Document;
using ErpWeb.EInvoiceLib.Model.InputData;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ITenantScopeContext _tenant;
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

        try
        {
            // Step 1 (locked order): if we have a submission id, ask MyInvois about that submission.
            if (!string.IsNullOrWhiteSpace(state.SubmitId))
            {
                var submission = await _helper.GetSubmission(state.SubmitId!);
                if (submission.IsSuccess && submission.result is not null)
                {
                    var match = FindDocumentInSubmission(submission.result, state.DocumentNo);
                    if (match is not null)
                    {
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
                return SaEInvoiceResult.Fail(key, "This document has no MyInvois UUID to refresh.");
            }

            state = loaded;
            ApplyCompanyCredentials(scope, state.Supplier);
        }

        var requestTime = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var detail = await _helper.GetDocumentDetail(state.Uuid!);
        stopwatch.Stop();

        var myInvoisStatus = detail.result?.status;
        var errorCode = detail.IsSuccess ? null : detail.errorCode;
        var errorMessage = detail.IsSuccess ? null : detail.error;

        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var loaded = await LoadStateAsync(db, scope, key, tracking: true, cancellationToken);
            if (loaded is null)
            {
                return NotFound(key);
            }

            var applied = false;
            if (detail.IsSuccess && SaEInvoiceStatusMap.IsRecognised(myInvoisStatus))
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
            else if (!detail.IsSuccess)
            {
                loaded.Error = Truncate(errorMessage, 500);
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
                CorrelationId = correlationId
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
                    "Only a SUBMITTED or VALID e-Invoice can be cancelled.");
            }

            if (string.IsNullOrWhiteSpace(loaded.Uuid))
            {
                return SaEInvoiceResult.Fail(key, "This document has no MyInvois UUID to cancel.");
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
                        $"The {_options.CancelWindowHours}-hour cancellation window has passed. Issue a credit/debit note instead.");
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

        return new SaEInvoiceStatusView
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
            History = history
        };
    }

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

    // ─────────────────────────────── Submit core ───────────────────────────────

    private async Task<SaEInvoiceResult> SubmitCoreAsync(
        SaEInvoiceDocumentKey key,
        string action,
        CancellationToken cancellationToken)
    {
        var gate = await AuthorizeAsync(key, PermissionCodes.Submit, cancellationToken);
        if (gate.Error is not null)
        {
            return gate.Error;
        }

        var scope = gate.Scope!;
        var correlationId = Guid.NewGuid();

        // ── Phase 1: validate, claim SUBMITTING, persist. NO HTTP here. ──
        EInvoiceDocumentState state;
        int attemptNo;
        DateTime requestTime;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var loaded = await LoadStateAsync(db, scope, key, tracking: true, cancellationToken);
            if (loaded is null)
            {
                return NotFound(key);
            }

            state = loaded;
            var status = EInvoiceStatuses.Normalize(state.Status);

            // Pre-submit gate (locked).
            switch (status)
            {
                case EInvoiceStatuses.Submitting when !IsStuck(state):
                    return SaEInvoiceResult.Fail(key, "A submission is already in progress for this document.");
                case EInvoiceStatuses.Submitting:
                    return SaEInvoiceResult.Fail(key,
                        "The previous submission is stuck. Run Recover to reconcile it with MyInvois before submitting again.",
                        recoveryRequired: true);
                case EInvoiceStatuses.Submitted:
                case EInvoiceStatuses.Valid:
                    return SaEInvoiceResult.Fail(key,
                        "This document has already been submitted to MyInvois.");
                case EInvoiceStatuses.Failed when state.Outcome == EInvoiceOutcomes.Unknown:
                    return SaEInvoiceResult.Fail(key,
                        "The previous submission outcome is unknown. Run Recover before submitting again.",
                        recoveryRequired: true);
            }

            ApplyCompanyCredentials(scope, state.Supplier);

            var build = await BuildSourceAsync(db, state, scope, cancellationToken);
            if (!build.Report.IsValid || build.Source is null)
            {
                await AppendLogAsync(db, state, EInvoiceActions.Validate,
                    status: status,
                    attemptNo: await NextAttemptAsync(db, state, cancellationToken),
                    correlationId: correlationId,
                    requestTime: DateTime.UtcNow,
                    responseTime: DateTime.UtcNow,
                    durationMs: 0,
                    errorCode: "ERP_VALIDATION",
                    errorMessage: build.Report.Summary(4000),
                    userId: scope.UserId,
                    cancellationToken: cancellationToken);

                // Persist the short error so the UI shows why the last attempt failed.
                state.Error = Truncate(build.Report.Summary(), 500);
                ApplyState(state);
                await db.SaveChangesAsync(cancellationToken);

                return SaEInvoiceResult.FailValidation(key, build.Report.Summary(), build.Report.Errors);
            }

            var mapped = _mapper.Map(build.Source, state.Supplier!, state.Supplier!.DocumentVersion ?? _secrets.getDocumentVersion());
            if (!mapped.IsValid)
            {
                await AppendLogAsync(db, state, EInvoiceActions.Validate,
                    status: status,
                    attemptNo: await NextAttemptAsync(db, state, cancellationToken),
                    correlationId: correlationId,
                    requestTime: DateTime.UtcNow,
                    responseTime: DateTime.UtcNow,
                    durationMs: 0,
                    errorCode: "ERP_VALIDATION",
                    errorMessage: mapped.Report.Summary(4000),
                    userId: scope.UserId,
                    cancellationToken: cancellationToken);

                state.Error = Truncate(mapped.Report.Summary(), 500);
                ApplyState(state);
                await db.SaveChangesAsync(cancellationToken);

                return SaEInvoiceResult.FailValidation(key, mapped.Report.Summary(), mapped.Report.Errors);
            }

            state.Mapped = mapped;
            state.Source = build.Source;

            // A CN/DN always records the origin invoice's MyInvois UUID, so the audit trail can answer
            // "which invoice does this note belong to?" long after the UUID is no longer on the source row.
            state.OriUuid = build.Source.OriginUuid ?? state.OriUuid;

            attemptNo = await NextAttemptAsync(db, state, cancellationToken);
            requestTime = DateTime.UtcNow;

            state.Status = EInvoiceStatuses.Submitting;
            state.Outcome = null;
            state.Error = null;
            state.SentOn = requestTime;
            ApplyState(state);

            // No audit row here: one row per attempt is written in phase 3 with the outcome. A crash in
            // between leaves the persisted SUBMITTING status (and its ModifiedDate) as the evidence, which
            // is what Recover keys off.
            try
            {
                // Commits the SUBMITTING claim (and its RowVersion check) before MyInvois is called.
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Do NOT retry the transition blindly; no MyInvois call has happened yet.
                return ConcurrencyFailure(key);
            }
        }

        // ── Phase 2: call MyInvois with NO database transaction open. ──
        // The signing step writes its scratch file into EInv_JsonPath before the library creates the
        // folder, so make sure it exists first (fresh installs have no App_Data folder yet).
        EnsureJsonPathDirectory();
        var stopwatch = Stopwatch.StartNew();
        GeneralResult<SuccessSubmit> submitResult;
        // The document family decides which generator the library runs: invoices (01/11) go through
        // the frozen GenerateInvoice path, credit/debit notes (02/03/12/13) through GenerateCreditNote.
        var mappedDocumentTypeCode = EInvoiceDocumentTypeMap.GetDocumentTypeCode(state.Mapped!.Header.docType);
        var isNote = EInvoiceDocumentTypeMap.IsCreditOrDebitNote(mappedDocumentTypeCode);
        try
        {
            submitResult = isNote
                ? await _helper.SubmitCreditDebitNotes([state.Mapped!.Header])
                : await _helper.SubmitInvoices([state.Mapped!.Header]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "e-Invoice submission threw for {Document}", key);
            submitResult = new GeneralResult<SuccessSubmit>
            {
                IsSuccess = false,
                error = ex.Message
            };
        }

        stopwatch.Stop();
        var classification = ClassifySubmitResult(submitResult);

        // ── Phase 3: persist the outcome. ──
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var loaded = await LoadStateAsync(db, scope, key, tracking: true, cancellationToken);
            if (loaded is null)
            {
                return NotFound(key);
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
                loaded.SentOn ??= requestTime;
            }

            ApplyState(loaded);

            await AppendLogAsync(db, loaded, action,
                status: classification.Status,
                attemptNo: attemptNo,
                correlationId: correlationId,
                requestTime: requestTime,
                responseTime: DateTime.UtcNow,
                durationMs: stopwatch.ElapsedMilliseconds,
                errorCode: classification.ErrorCode,
                errorMessage: JoinError(classification.ErrorCode, classification.Error),
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

            return new SaEInvoiceResult
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
                AttemptNo = attemptNo,
                CorrelationId = correlationId,
                RecoveryRequired = classification.Outcome == EInvoiceOutcomes.Unknown
            };
        }
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

    private static SubmitClassification ClassifySubmitResult(GeneralResult<SuccessSubmit> result)
    {
        var accepted = result.result?.acceptedDocuments;
        if (result.IsSuccess && accepted is { Count: > 0 })
        {
            return new SubmitClassification
            {
                Status = EInvoiceStatuses.Submitted,
                Uuid = accepted[0].uuid,
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
            TinNo = company?.TaxNo,
            RegistrationNo = company?.RegistrationNo,
            RegType = company?.EInvRegType,
            SstNo = company?.EInvSstNo,
            MsicCode = company?.EInvMsicCode,
            BusinessDescription = company?.EInvBizDescription,
            Addr1 = company?.Address1,
            Addr2 = company?.Address2,
            Addr3 = company?.Address3,
            City = company?.City,
            State = company?.State,
            PostalCode = company?.PostCode,
            Country = company?.Country,
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

    private async Task<SourceBuildResult> BuildSourceAsync(
        AppDbContext db,
        EInvoiceDocumentState state,
        TenantScope scope,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var taxPercents = await db.SaTaxGroups.AsNoTracking()
            .Where(x => x.CompanyCode == state.CompanyCode)
            .ToListAsync(cancellationToken);
        var taxLookup = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in taxPercents)
        {
            taxLookup[group.TaxGrCode] = group.Percentage;
        }

        EInvoiceSourceDocument source;

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
                        Uom = x.StdUom,
                        Qty = x.Qty,
                        UnitPrice = x.UnitPrice,
                        GrossAmount = x.Amount,
                        AmountExclTax = x.NetAmount,
                        TaxAmount = x.TaxAmt,
                        TaxType = x.TaxGrCode,
                        TaxPercent = ResolveTaxPercent(taxLookup, x.TaxGrCode),
                        ClassificationCode = x.Classification
                    })
                    .ToList();

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
                    CustomerName = invoice.InvName ?? invoice.CustName,
                    CustomerTin = invoice.BuyerTin,
                    CustomerRegNo = invoice.BuyerBrn,
                    CustomerRegType = invoice.BuyerRegType,
                    CustomerSstNo = invoice.GstregNo,
                    CustomerAddr1 = invoice.InvAddress1,
                    CustomerAddr2 = invoice.InvAddress2,
                    CustomerAddr3 = invoice.InvAddress3,
                    CustomerAddr4 = invoice.InvAddress4,
                    CustomerCity = invoice.InvCity,
                    CustomerState = invoice.InvState,
                    CustomerPostalCode = invoice.InvPostalCode,
                    CustomerCountry = invoice.InvCountry,
                    CustomerPhone = invoice.InvTel,
                    CustomerEmail = invoice.InvEmail,
                    Lines = lines
                };
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
                        Uom = x.StdUom,
                        Qty = x.Qty,
                        UnitPrice = x.UnitPrice,
                        GrossAmount = x.Amount,
                        AmountExclTax = x.NetAmount,
                        TaxAmount = x.TaxAmt,
                        TaxType = x.TaxGroup,
                        TaxPercent = ResolveTaxPercent(taxLookup, x.TaxGroup),
                        ClassificationCode = x.Classification
                    })
                    .ToList();

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
                    CustomerName = cdn.CustName,
                    CustomerTin = origin?.BuyerTin,
                    CustomerRegNo = origin?.BuyerBrn,
                    CustomerRegType = origin?.BuyerRegType,
                    CustomerSstNo = origin?.GstregNo,
                    CustomerAddr1 = cdn.InvAddress1,
                    CustomerAddr2 = cdn.InvAddress2,
                    CustomerAddr3 = cdn.InvAddress3,
                    CustomerAddr4 = cdn.InvAddress4,
                    CustomerCity = cdn.City,
                    CustomerState = cdn.State,
                    CustomerPostalCode = cdn.PostalCode,
                    CustomerCountry = cdn.Country,
                    CustomerPhone = cdn.Tel,
                    CustomerEmail = origin?.InvEmail,
                    Lines = lines
                };
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
            Report = errors.Count == 0 ? EInvoiceValidationReport.Valid : EInvoiceValidationReport.From(errors)
        };
    }

    private static double? ResolveTaxPercent(IReadOnlyDictionary<string, decimal> lookup, string? taxGroupCode)
    {
        if (string.IsNullOrWhiteSpace(taxGroupCode))
        {
            return null;
        }

        return lookup.TryGetValue(taxGroupCode, out var percent) ? (double)percent : 0d;
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
            // A self-billed document belongs to the buyer, so it is authorized through Purchase.
            EInvoiceDocumentTypes.SelfBilledCreditNote => MenuCodes.PurchaseCreditNote,
            EInvoiceDocumentTypes.SelfBilledDebitNote => MenuCodes.PurchaseDebitNote,
            EInvoiceDocumentTypes.SelfBilledInvoice => MenuCodes.PurchaseInvoice,
            _ => MenuCodes.SalesInvoice
        };

        if (!await _accessRights.CanAsync(menuCode, permission, cancellationToken))
        {
            return new AuthorizationGate
            {
                Error = SaEInvoiceResult.Fail(key, "Not authorized.", SaEInvoiceErrorKind.Authorization)
            };
        }

        // The LHDN document-type mapping and the generators for 11/12/13 are in place, but the payload
        // still has to come from the Purchase self-bill documents, which are not wired yet. Say so
        // explicitly instead of reporting a misleading "document not found".
        if (EInvoiceDocumentTypes.IsSelfBilled(key.DocumentType))
        {
            return new AuthorizationGate
            {
                Error = SaEInvoiceResult.Fail(key,
                    "Self-billed e-Invoice (LHDN 11/12/13) is not enabled yet: the Purchase self-bill payload mapping has not been wired.",
                    SaEInvoiceErrorKind.NotConfigured)
            };
        }

        return new AuthorizationGate { Scope = scope };
    }

    // ─────────────────────────────── Small helpers ───────────────────────────────

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
