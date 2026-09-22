using ErpWeb.EInvoiceLib.Model;
using ErpWeb.EInvoiceLib.Model.Document;

namespace ErpWeb.Core.EInvoice;

/// <summary>
/// Pure mapping from the MyInvois Get Document Details payload to the UI-owned
/// <see cref="SaEInvoiceDetailView"/>.
///
/// <para>
/// <b>Deliberately a pure function.</b> There is no database access, no authorization, no logging, no
/// <c>ILogger</c>, no <c>DbContext</c>, no <c>ISubmitDocumentHelper</c> and no state mutation here —
/// it takes a <see cref="DocumentValidatation"/> and returns a <see cref="SaEInvoiceDetailView"/>, and
/// nothing else. That is what lets <c>SaEInvoiceDetailMapperTests</c> pin the fiddly validation-result
/// transformation without spinning up a test host.
/// </para>
///
/// <para>
/// <c>internal</c> rather than <c>public</c> so it stays out of the public Core surface;
/// <c>ErpWeb.Core.csproj</c> already grants <c>InternalsVisibleTo("ErpWeb.Tests")</c>, so the tests can
/// still call it directly. Follows the same shape as <see cref="SaEInvoiceStatusMap"/>.
/// </para>
/// </summary>
internal static class SaEInvoiceDetailMapper
{
    /// <summary>
    /// Maps one MyInvois document detail response. Never throws and never returns null; every
    /// collection is empty rather than null when the API omitted it.
    /// </summary>
    internal static SaEInvoiceDetailView Map(DocumentValidatation detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        return new SaEInvoiceDetailView
        {
            Uuid = detail.uuid ?? string.Empty,
            SubmissionUid = detail.submissionUid,
            LongId = detail.longId,
            InternalId = detail.internalId,
            TypeName = detail.typeName,
            TypeVersionName = detail.typeVersionName,

            IssuerTin = detail.issuerTin,
            IssuerName = detail.issuerName,
            ReceiverId = detail.receiverId,
            ReceiverName = detail.receiverName,

            DateTimeIssued = detail.dateTimeIssued,
            DateTimeReceived = detail.dateTimeReceived,
            DateTimeValidated = detail.dateTimeValidated,
            CancelDateTime = detail.cancelDateTime,
            RejectRequestDateTime = detail.rejectRequestDateTime,

            TotalExcludingTax = detail.totalExcludingTax,
            TotalDiscount = detail.totalDiscount,
            TotalNetAmount = detail.totalNetAmount,
            TotalPayableAmount = detail.totalPayableAmount,

            MyInvoisStatus = detail.status,
            // Normalize only for the chip vocabulary; the raw value is kept above so an unrecognised
            // status cannot be reported to the operator as "NEW".
            Status = EInvoiceStatuses.Normalize(detail.status),
            DocumentStatusReason = detail.documentStatusReason,
            CreatedByUserId = detail.createdByUserId,

            ValidationStatus = detail.validationResults?.status,
            Steps = MapSteps(detail.validationResults?.validationSteps)
        };
    }

    /// <summary>
    /// Maps every step in order. A step is never dropped — including a <c>Valid</c> step with no error
    /// object, and an <c>Invalid</c> step whose error is null — because which validations ran is part
    /// of the diagnosis.
    /// </summary>
    private static List<SaEInvoiceValidationStep> MapSteps(List<ValidationStep>? steps)
    {
        if (steps is null || steps.Count == 0)
        {
            return [];
        }

        var mapped = new List<SaEInvoiceValidationStep>(steps.Count);
        foreach (var step in steps)
        {
            mapped.Add(new SaEInvoiceValidationStep
            {
                Name = step.name,
                Status = step.status,
                Issues = MapIssues(step.error)
            });
        }

        return mapped;
    }

    private static List<SaEInvoiceValidationIssue> MapIssues(DocErrorDetail? error)
    {
        if (error is null)
        {
            return [];
        }

        return
        [
            new SaEInvoiceValidationIssue
            {
                PropertyName = error.PropertyName,
                PropertyPath = error.PropertyPath,
                ErrorCode = error.ErrorCode,
                Error = error.Error,
                ErrorMs = error.ErrorMs,
                MetaData = error.MetaData,
                InnerErrors = MapInnerErrors(error.InnerError)
            }
        ];
    }

    /// <summary>
    /// Flattens <c>InnerError</c>. Exactly one level is possible: the child type's own
    /// <c>InnerError</c> is <c>object</c>, so a deeper tree cannot be bound without changing
    /// <c>ErpWeb.EInvoiceLib</c>. A blank child is skipped — MyInvois' own sample returns
    /// <c>"innerError": null</c> inside a populated child.
    /// </summary>
    private static List<SaEInvoiceValidationIssue> MapInnerErrors(List<InnerErrorMsg>? innerErrors)
    {
        if (innerErrors is null || innerErrors.Count == 0)
        {
            return [];
        }

        var mapped = new List<SaEInvoiceValidationIssue>(innerErrors.Count);
        foreach (var inner in innerErrors)
        {
            if (inner is null || IsBlank(inner))
            {
                continue;
            }

            mapped.Add(new SaEInvoiceValidationIssue
            {
                PropertyName = inner.PropertyName,
                PropertyPath = inner.PropertyPath,
                ErrorCode = inner.ErrorCode,
                Error = inner.Error,
                ErrorMs = inner.ErrorMs,
                MetaData = inner.MetaData,
                // Intentionally empty: see the summary on this method.
                InnerErrors = []
            });
        }

        return mapped;
    }

    private static bool IsBlank(InnerErrorMsg inner) =>
        string.IsNullOrWhiteSpace(inner.PropertyName)
        && string.IsNullOrWhiteSpace(inner.PropertyPath)
        && string.IsNullOrWhiteSpace(inner.ErrorCode)
        && string.IsNullOrWhiteSpace(inner.Error)
        && string.IsNullOrWhiteSpace(inner.ErrorMs)
        && string.IsNullOrWhiteSpace(inner.MetaData);
}
