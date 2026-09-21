using ErpWeb.Core.Services;
using ErpWeb.EInvoiceLib.Model.Document;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.EInvoice;

/// <summary>
/// What a history write actually did. Returned (rather than <c>void</c>) so the four lifecycle hooks and
/// their tests can assert the outcome without inspecting a logger.
/// </summary>
internal enum EInvoiceHistoryWrite
{
    /// <summary>Nothing to record — the document has no MyInvois submission id.</summary>
    Skipped,

    /// <summary>A new history row was inserted.</summary>
    Inserted,

    /// <summary>An existing history row was refreshed in place.</summary>
    Updated,

    /// <summary>The write failed. Logged as a Warning; the business action is unaffected.</summary>
    Failed
}

/// <summary>
/// The <b>single</b> writer for <c>dbo.EInvDocSubmission</c> — the e-Invoice submission registry
/// (one row per submitted document; see the plan's rules R1..R9).
///
/// <para>
/// <b>Failure isolation (plan D-8).</b> This writer owns its own <see cref="AppDbContext"/> and is called
/// <i>after</i> the caller has committed its business transaction. It never throws. That combination is
/// deliberate: a "never throws" writer that mutated the caller's tracked context would leave that
/// context poisoned when its own <c>SaveChangesAsync</c> failed, and a swallowed exception could then
/// present a half-applied business action as a success. Here the two commit separately, so a history
/// failure cannot affect the e-Invoice action, and a rolled-back action never leaves a phantom row.
/// </para>
///
/// <para>
/// The trade-off is that the history write is not atomic with the business commit. That is acceptable
/// because the row is <b>derivable</b>: Refresh/Recover rebuild it from the document's own
/// <c>IRBMSubmitID</c>/<c>IRBMUUID</c> plus the MyInvois response.
/// </para>
///
/// <para>
/// <b>Concurrency (plan D-10).</b> The unique index
/// <c>UX_EInvDocSubmission_Submission</c> is the authority, not this code. A duplicate-key race is
/// recovered deterministically (fresh context, re-read the winner's row, apply the same field updates)
/// so the operator never sees it.
/// </para>
/// </summary>
internal static class EInvoiceSubmissionWriter
{
    // The widths below mirror the [MaxLength] attributes on EInvDocSubmission. They exist so that an
    // unexpectedly long string from MyInvois can never turn a history write into a failure - a silent
    // missing row is exactly the failure mode this feature is meant to end.
    private const int MaxCompanyId = 20;
    private const int MaxBranchCode = 5;
    private const int MaxDocumentType = 3;
    private const int MaxDocumentNo = 30;
    private const int MaxSubmissionUuid = 50;
    private const int MaxUuid = 50;
    private const int MaxLongId = 100;
    private const int MaxInternalId = 30;
    private const int MaxTypeName = 20;
    private const int MaxIssuerName = 250;
    private const int MaxReceiverId = 30;
    private const int MaxTin = 20;
    private const int MaxReceiverIdType = 50;
    private const int MaxSubmissionChannel = 20;
    private const int MaxIntermediaryRob = 30;
    private const int MaxIssuerId = 50;
    private const int MaxCurrency = 10;
    private const int MaxStatus = 20;
    private const int MaxReason = 250;
    private const int MaxUserId = 70;

    /// <summary>
    /// CREATE — one call per <b>accepted</b> document (plan R3 / Model A). Rejected documents are not
    /// recorded: they carry no <c>uuid</c>/<c>internalId</c>, and the rejection is already captured by
    /// <c>SaEInvoiceLog</c> and the document's own <c>IRBMStatus</c>/<c>IRBMOutcome</c>.
    /// </summary>
    public static Task<EInvoiceHistoryWrite> RecordSubmitAsync(
        IDbContextFactory<AppDbContext> factory,
        TenantScope scope,
        SaEInvoiceDocumentKey key,
        string? submissionId,
        string? uuid,
        string? internalId,
        int? documentCount,
        string? overallStatus,
        DateTime submittedOnUtc,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(submissionId))
        {
            // Without a submission id there is no key, so there is nothing to record.
            return Task.FromResult(EInvoiceHistoryWrite.Skipped);
        }

        return UpsertAsync(factory, scope, key, submissionId!, row =>
        {
            row.Uuid = Clean(uuid, MaxUuid) ?? row.Uuid;
            row.InternalId = Clean(internalId, MaxInternalId) ?? row.InternalId;

            // A submitted document is SUBMITTED until a later Refresh/Recover says otherwise.
            row.Status = EInvoiceStatuses.Submitted;

            // Provisional issue timestamp: the submission instant. A later MyInvois response supersedes
            // it with the document's real issue time (ApplyStatusAsync copies the API value when present).
            row.DateTimeIssued ??= submittedOnUtc;

            if (!string.IsNullOrWhiteSpace(overallStatus))
            {
                row.OverallStatus = Clean(overallStatus!.ToUpperInvariant(), MaxStatus) ?? row.OverallStatus;
            }

            if (documentCount.HasValue)
            {
                row.DocumentCount = documentCount;
            }

            row.CreatedOn ??= DateTime.UtcNow;
        }, logger, cancellationToken);
    }

    /// <summary>
    /// UPDATE — Refresh / Recover / Cancel. Upserts by the four-part key, copying only the values the
    /// response actually carried (plan 3.4) so a sparse response can never erase known history.
    /// </summary>
    /// <param name="submissionId">
    /// Taken from the <b>ERP document</b> (its <c>IRBMSubmitID</c>), never from the API payload, so a
    /// stale or mismatched response cannot write into another submission's row (plan 3.5).
    /// </param>
    /// <param name="cancelOnUtc">
    /// Non-null when the caller has just cancelled the document successfully. A <b>failed</b> cancel must
    /// pass null: the financial document is unchanged, so history must not claim otherwise.
    /// </param>
    public static Task<EInvoiceHistoryWrite> ApplyStatusAsync(
        IDbContextFactory<AppDbContext> factory,
        TenantScope scope,
        SaEInvoiceDocumentKey key,
        string? submissionId,
        DocumentSummary? summary,
        DocumentValidatation? detail,
        string? overallStatus,
        int? documentCount,
        ILogger logger,
        DateTime? cancelOnUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(submissionId))
        {
            // Nothing was ever submitted for this document, so there is no row to update.
            return Task.FromResult(EInvoiceHistoryWrite.Skipped);
        }

        return UpsertAsync(factory, scope, key, submissionId!, row =>
        {
            if (summary is not null)
            {
                ApplySummary(row, summary);
            }

            if (detail is not null)
            {
                ApplyDetail(row, detail);
            }

            if (cancelOnUtc.HasValue)
            {
                row.Status = EInvoiceStatuses.Cancelled;
                row.CancelDateTime ??= cancelOnUtc.Value;

                // OverallStatus describes the whole SUBMISSION, so a per-document cancellation only
                // proves it when nothing else was known.
                row.OverallStatus ??= EInvoiceStatuses.Cancelled;
            }
            else
            {
                // Document-level status is only ever set from a status the LOCKED map recognises
                // (SaEInvoiceStatusMap); an unrecognised value must not clobber a known state.
                var apiStatus = detail?.status ?? summary?.status;
                if (SaEInvoiceStatusMap.IsRecognised(apiStatus))
                {
                    row.Status = SaEInvoiceStatusMap.FromMyInvoisDocumentStatus(apiStatus);
                }
            }

            if (!string.IsNullOrWhiteSpace(overallStatus))
            {
                // Diagnostic, submission-level, stored verbatim: deliberately NOT constrained to
                // EInvoiceStatuses.All, because it is the API's own vocabulary (plan 2.2).
                row.OverallStatus = Clean(overallStatus!.ToUpperInvariant(), MaxStatus) ?? row.OverallStatus;
            }

            if (documentCount.HasValue)
            {
                row.DocumentCount = documentCount;
            }

            row.LastSyncedOn = DateTime.UtcNow;
        }, logger, cancellationToken);
    }

    // ────────────────────────────────── internals ──────────────────────────────────

    /// <summary>
    /// Find-or-insert by the four-part business key, then apply the caller's field updates, with
    /// deterministic duplicate-key recovery.
    /// </summary>
    private static async Task<EInvoiceHistoryWrite> UpsertAsync(
        IDbContextFactory<AppDbContext> factory,
        TenantScope scope,
        SaEInvoiceDocumentKey key,
        string submissionId,
        Action<EInvDocSubmission> apply,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var row = await FindAsync(db, scope, key, submissionId, cancellationToken);
            var isNew = row is null;
            if (isNew)
            {
                row = NewRow(scope, key, submissionId);
                db.EInvDocSubmissions.Add(row);
            }

            apply(row!);

            // `status` is NOT NULL with no database default, so a row created from an ApplyStatus call
            // (a submission that predates this feature) needs a floor value.
            if (isNew && string.IsNullOrWhiteSpace(row!.Status))
            {
                row.Status = EInvoiceStatuses.Submitted;
            }

            await db.SaveChangesAsync(cancellationToken);
            return isNew ? EInvoiceHistoryWrite.Inserted : EInvoiceHistoryWrite.Updated;
        }
        catch (DbUpdateException ex) when (SqlErrorClassifier.IsUniqueViolation(ex))
        {
            // Another writer won the race between our read and our insert. The unique index did its job;
            // our job is to make that a non-event. Re-read the winner's row on a FRESH context (the
            // failed one still tracks the rejected insert) and apply the same updates to it.
            return await RecoverFromRaceAsync(factory, scope, key, submissionId, apply, logger, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "e-Invoice submission history was NOT written for {DocumentType}:{DocumentNo} (submission {SubmissionId}). "
                    + "The e-Invoice action itself is unaffected. Check dbo.EInvDocSubmission and the migration.",
                key.DocumentType, key.DocumentNo, submissionId);
            return EInvoiceHistoryWrite.Failed;
        }
    }

    private static async Task<EInvoiceHistoryWrite> RecoverFromRaceAsync(
        IDbContextFactory<AppDbContext> factory,
        TenantScope scope,
        SaEInvoiceDocumentKey key,
        string submissionId,
        Action<EInvDocSubmission> apply,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var row = await FindAsync(db, scope, key, submissionId, cancellationToken);
            if (row is null)
            {
                // The index said "duplicate" but the row is not visible: give up rather than loop.
                logger.LogWarning(
                    "e-Invoice submission history lost a duplicate-key race for {DocumentType}:{DocumentNo} "
                        + "(submission {SubmissionId}) but the winning row could not be re-read.",
                    key.DocumentType, key.DocumentNo, submissionId);
                return EInvoiceHistoryWrite.Failed;
            }

            apply(row);
            await db.SaveChangesAsync(cancellationToken);
            return EInvoiceHistoryWrite.Updated;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "e-Invoice submission history duplicate-key recovery failed for {DocumentType}:{DocumentNo} "
                    + "(submission {SubmissionId}).",
                key.DocumentType, key.DocumentNo, submissionId);
            return EInvoiceHistoryWrite.Failed;
        }
    }

    private static Task<EInvDocSubmission?> FindAsync(
        AppDbContext db,
        TenantScope scope,
        SaEInvoiceDocumentKey key,
        string submissionId,
        CancellationToken cancellationToken) =>
        db.EInvDocSubmissions.FirstOrDefaultAsync(
            x => x.CompanyId == scope.CompanyCode
                 && x.SubmissionUuid == submissionId
                 && x.DocumentType == key.DocumentType
                 && x.DocumentNo == key.DocumentNo,
            cancellationToken);

    private static EInvDocSubmission NewRow(TenantScope scope, SaEInvoiceDocumentKey key, string submissionId) =>
        new()
        {
            CompanyId = Clean(scope.CompanyCode, MaxCompanyId),
            BranchCode = Clean(scope.BranchCode, MaxBranchCode),
            DocumentType = Clean(key.DocumentType, MaxDocumentType),
            DocumentNo = Clean(key.DocumentNo, MaxDocumentNo),
            SubmissionUuid = Clean(submissionId, MaxSubmissionUuid),
            CreatedByUserId = Clean(scope.UserId, MaxUserId),
            CreatedOn = DateTime.UtcNow
            // `document` (raw signed payload) is deliberately never written - plan D-14.
        };

    /// <summary>
    /// Copies everything the document-summary response carried. Non-null (and non-blank) incoming values
    /// only: different MyInvois responses legitimately carry different subsets of fields, so a blank must
    /// never erase a value an earlier response supplied (plan 3.4).
    /// </summary>
    private static void ApplySummary(EInvDocSubmission row, DocumentSummary s)
    {
        row.Uuid = Clean(s.uuid, MaxUuid) ?? row.Uuid;
        row.LongId = Clean(s.longId, MaxLongId) ?? row.LongId;
        row.InternalId = Clean(s.internalId, MaxInternalId) ?? row.InternalId;
        row.TypeName = Clean(s.typeName, MaxTypeName) ?? row.TypeName;
        row.TypeVersionName = Clean(s.typeVersionName, MaxTypeName) ?? row.TypeVersionName;
        row.IssuerTin = Clean(s.issuerTin, MaxTin) ?? row.IssuerTin;
        row.IssuerName = Clean(s.issuerName, MaxIssuerName) ?? row.IssuerName;
        row.ReceiverId = Clean(s.receiverId, MaxReceiverId) ?? row.ReceiverId;
        row.ReceiverName = Clean(s.receiverName, MaxIssuerName) ?? row.ReceiverName;

        row.DateTimeIssued = s.dateTimeIssued ?? row.DateTimeIssued;
        row.DateTimeReceived = s.dateTimeReceived ?? row.DateTimeReceived;
        row.DateTimeValidated = s.dateTimeValidated ?? row.DateTimeValidated;

        row.TotalSales = s.totalExcludingTax ?? row.TotalSales;
        row.TotalDiscount = s.totalDiscount ?? row.TotalDiscount;
        row.NetAmount = s.totalNetAmount ?? row.NetAmount;
        row.Total = s.totalPayableAmount ?? row.Total;

        row.CancelDateTime = s.cancelDateTime ?? row.CancelDateTime;
        row.RejectRequestDateTime = s.rejectRequestDateTime ?? row.RejectRequestDateTime;
        row.DocumentStatusReason = Clean(s.documentStatusReason, MaxReason) ?? row.DocumentStatusReason;
        row.CreatedByUserId = Clean(s.createdByUserId, MaxUserId) ?? row.CreatedByUserId;

        // The live table already carried these payload-capture columns (Phase 0 discovery: the legacy
        // entity never mapped them). The submission response is exactly where they come from.
        row.SupplierTin = Clean(s.supplierTIN, MaxTin) ?? row.SupplierTin;
        row.SupplierName = Clean(s.supplierName, MaxIssuerName) ?? row.SupplierName;
        row.BuyerName = Clean(s.buyerName, MaxIssuerName) ?? row.BuyerName;
        row.BuyerTin = Clean(s.buyerTIN, MaxTin) ?? row.BuyerTin;
        row.ReceiverTin = Clean(s.receiverTIN, MaxTin) ?? row.ReceiverTin;
        row.ReceiverIdType = Clean(s.receiverIdType, MaxReceiverIdType) ?? row.ReceiverIdType;
        row.SubmissionChannel = Clean(s.submissionChannel, MaxSubmissionChannel) ?? row.SubmissionChannel;
        row.IntermediaryName = Clean(s.intermediaryName, MaxIssuerName) ?? row.IntermediaryName;
        row.IntermediaryTin = Clean(s.intermediaryTIN, MaxTin) ?? row.IntermediaryTin;
        row.IntermediaryRob = Clean(s.intermediaryROB, MaxIntermediaryRob) ?? row.IntermediaryRob;
        row.IssuerId = Clean(s.issuerID, MaxIssuerId) ?? row.IssuerId;
        row.IssuerIdType = Clean(s.issuerIDType, MaxTin) ?? row.IssuerIdType;
        row.DocumentCurrency = Clean(s.documentCurrency, MaxCurrency) ?? row.DocumentCurrency;
    }

    /// <summary>
    /// Copies everything the single-document detail response carried, under the same non-null rule.
    /// The detail payload is a narrower shape than the summary: it has no supplier/buyer/intermediary
    /// or currency fields, so those are left as they are.
    /// </summary>
    private static void ApplyDetail(EInvDocSubmission row, DocumentValidatation d)
    {
        row.Uuid = Clean(d.uuid, MaxUuid) ?? row.Uuid;
        row.LongId = Clean(d.longId, MaxLongId) ?? row.LongId;
        row.InternalId = Clean(d.internalId, MaxInternalId) ?? row.InternalId;
        row.TypeName = Clean(d.typeName, MaxTypeName) ?? row.TypeName;
        row.TypeVersionName = Clean(d.typeVersionName, MaxTypeName) ?? row.TypeVersionName;
        row.IssuerTin = Clean(d.issuerTin, MaxTin) ?? row.IssuerTin;
        row.IssuerName = Clean(d.issuerName, MaxIssuerName) ?? row.IssuerName;
        row.ReceiverId = Clean(d.receiverId, MaxReceiverId) ?? row.ReceiverId;
        row.ReceiverName = Clean(d.receiverName, MaxIssuerName) ?? row.ReceiverName;

        row.DateTimeIssued = d.dateTimeIssued ?? row.DateTimeIssued;
        row.DateTimeReceived = d.dateTimeReceived ?? row.DateTimeReceived;
        row.DateTimeValidated = d.dateTimeValidated ?? row.DateTimeValidated;

        row.TotalSales = d.totalExcludingTax ?? row.TotalSales;
        row.TotalDiscount = d.totalDiscount ?? row.TotalDiscount;
        row.NetAmount = d.totalNetAmount ?? row.NetAmount;
        row.Total = d.totalPayableAmount ?? row.Total;

        row.CancelDateTime = d.cancelDateTime ?? row.CancelDateTime;
        row.RejectRequestDateTime = d.rejectRequestDateTime ?? row.RejectRequestDateTime;
        row.DocumentStatusReason = Clean(d.documentStatusReason, MaxReason) ?? row.DocumentStatusReason;
        row.CreatedByUserId = Clean(d.createdByUserId, MaxUserId) ?? row.CreatedByUserId;
    }

    /// <summary>
    /// Blank becomes null (so it can never erase a stored value) and an over-long value is truncated to
    /// its column width (so a verbose MyInvois response cannot fail the write).
    /// </summary>
    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
