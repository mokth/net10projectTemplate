using System.Globalization;
using ErpWeb.Core.StockLedger;

namespace ErpWeb.Core.Services;

/// <summary>
/// Cross-module entry point for the operator-error contract: Sales, Purchase and Production stock or
/// costing failures that are already decided elsewhere.
/// </summary>
/// <remarks>
/// <para>
/// It only turns a decision that was already made into text. It never re-runs a calculation, never
/// changes a status, never replaces the persisted <c>StockLedgerErrorCodes</c> value and never parses
/// an error message. The original exception text stays available to logs and to the caller's own
/// technical message.
/// </para>
/// <para>
/// The inventory-specific factory (<c>IvInventoryOperatorErrors</c>) predates this type and keeps its
/// transaction-type mapping; both produce the same <see cref="ErpOperatorError"/> shape, so a page can
/// render either with the same banner.
/// </para>
/// </remarks>
public static class ErpOperatorErrors
{
    /// <summary>
    /// Short support reference quoted to the operator and written to the same log line, so support can
    /// find the original technical code and message. Not a diagnostic id inside the message text.
    /// </summary>
    public static string NewReferenceId() =>
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..10].ToUpperInvariant();

    /// <summary>
    /// A stock-ledger refusal raised outside the inventory transaction screens. Classification uses
    /// the persisted code, the attempted action and (where supplied) the replay phase — never the text.
    /// </summary>
    public static ErpOperatorError FromLedgerError(
        StockLedgerError error,
        ErpErrorOrigin origin,
        ErpDocumentKind documentKind,
        string? documentNumber,
        ErpErrorAction action,
        string? referenceId,
        string? itemCode = null,
        string? warehouse = null,
        string? location = null,
        string? lotNo = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        return ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            TechnicalCode = error.Code,
            Retryable = error.Retryable,
            Action = action,
            Origin = origin,
            RejectionPhase = error.Phase switch
            {
                StockLedgerErrorPhase.Removing => ErpRejectionPhase.Removing,
                StockLedgerErrorPhase.Rewinding => ErpRejectionPhase.Rewinding,
                StockLedgerErrorPhase.Replaying => ErpRejectionPhase.Replaying,
                _ => ErpRejectionPhase.NotSpecified
            },
            DocumentKind = documentKind,
            DocumentNumber = documentNumber,
            ItemCode = itemCode,
            Warehouse = warehouse,
            Location = location,
            LotNo = lotNo,
            ReferenceId = referenceId
        });
    }

    /// <summary>
    /// A failure that is not a stock-ledger refusal (an unexpected exception, a write conflict, a
    /// concurrency loss or an unsupported workflow state). The caller logs the original exception; the
    /// operator only ever sees the safe generic text and the reference.
    /// </summary>
    public static ErpOperatorError Unexpected(
        ErpErrorOrigin origin,
        ErpDocumentKind documentKind,
        string? documentNumber,
        ErpErrorAction action,
        string? referenceId) =>
        ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            Family = ErpErrorFamily.UnknownFailure,
            Action = action,
            Origin = origin,
            DocumentKind = documentKind,
            DocumentNumber = documentNumber,
            ReferenceId = referenceId
        });

    /// <summary>
    /// The stock and costing records disagree in a way only an authorised review can resolve. Distinct
    /// from <see cref="Unexpected"/>: the failure is understood, it is the correction that needs a person.
    /// </summary>
    public static ErpOperatorError ReconciliationRequired(
        ErpErrorOrigin origin,
        ErpDocumentKind documentKind,
        string? documentNumber,
        ErpErrorAction action,
        string? referenceId) =>
        ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            Family = ErpErrorFamily.ReconciliationRequired,
            Action = action,
            Origin = origin,
            DocumentKind = documentKind,
            DocumentNumber = documentNumber,
            ReferenceId = referenceId
        });

    /// <summary>
    /// A stale-document / concurrency refusal raised by a cross-module boundary. Distinct from
    /// <see cref="Unexpected"/> because the operator genuinely can refresh and retry, and the outcome
    /// of the failed attempt is known: nothing was written.
    /// </summary>
    public static ErpOperatorError Stale(
        ErpErrorOrigin origin,
        ErpDocumentKind documentKind,
        string? documentNumber,
        ErpErrorAction action,
        string? referenceId) =>
        ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            Family = ErpErrorFamily.StaleDocument,
            Action = action,
            Origin = origin,
            DocumentKind = documentKind,
            DocumentNumber = documentNumber,
            ReferenceId = referenceId
        });
}
