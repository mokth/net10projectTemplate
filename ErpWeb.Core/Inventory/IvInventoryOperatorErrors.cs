using System.Globalization;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;

namespace ErpWeb.Core.Inventory;

/// <summary>
/// Builds the operator-facing text for an inventory posting rejection.
/// </summary>
/// <remarks>
/// <para>
/// This factory only reads a decision that has already been made: the persisted
/// <see cref="StockLedgerError"/> the posting path decided to throw, plus the caller's own transaction
/// type and action. It performs no lookup, re-runs no costing, changes no quantity and never rewrites
/// the ledger error — <c>StockLedgerError.Code</c>, <c>.Message</c> and the thrown exception stay
/// exactly as they were.
/// </para>
/// <para>
/// It deliberately leaves item, warehouse, lot, available/required/shortage and cost empty. The
/// posting boundary does not prove those values for the failing line, and an invented quantity is worse
/// than an omitted one; a later phase may enrich them from verified evidence.
/// </para>
/// </remarks>
public static class IvInventoryOperatorErrors
{
    /// <summary>
    /// Maps one persisted ledger error to operator text for one batch. <paramref name="trxType"/> is the
    /// caller's transaction type (not error text) and only chooses the document noun.
    /// </summary>
    /// <param name="error">The error the posting path already threw. Never modified.</param>
    /// <param name="trxType">Inventory transaction type, e.g. <see cref="IvTrxTypes.GoodsReceive"/>.</param>
    /// <param name="post">True for a post, false for a rollback.</param>
    /// <param name="batchNo">The batch/document number shown in the grid.</param>
    /// <param name="referenceId">Support reference issued by the caller, or null when none was issued.</param>
    /// <param name="evidence">
    /// Verified read-only evidence of the affected transaction, or null when the lookup was skipped,
    /// failed or could not prove ownership. Null simply produces the safe generic wording.
    /// </param>
    public static ErpOperatorError FromLedgerError(
        StockLedgerError error,
        string? trxType,
        bool post,
        int batchNo,
        string? referenceId,
        IvFailureEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        return FromLedgerError(
            error,
            ToDocumentKind(trxType),
            batchNo.ToString(CultureInfo.InvariantCulture),
            post ? ErpErrorAction.Post : ErpErrorAction.Rollback,
            referenceId,
            evidence);
    }

    /// <summary>
    /// Maps one persisted ledger error to operator text when the caller already knows the document
    /// family and number (for example a stock count, whose number is not a batch number).
    /// </summary>
    /// <param name="error">The error the posting path already threw. Never modified.</param>
    /// <param name="documentKind">The document family the caller itself knows.</param>
    /// <param name="documentNumber">The operator-visible document number, or null when none exists.</param>
    /// <param name="action">What the operator attempted. Chooses the headline verb only.</param>
    /// <param name="referenceId">Support reference issued by the caller, or null when none was issued.</param>
    /// <param name="evidence">Verified read-only evidence of the affected transaction, or null.</param>
    public static ErpOperatorError FromLedgerError(
        StockLedgerError error,
        ErpDocumentKind documentKind,
        string? documentNumber,
        ErpErrorAction action,
        string? referenceId,
        IvFailureEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        return ErpOperatorErrorPresenter.Present(new StockOperatorFailureContext
        {
            TechnicalCode = error.Code,
            Retryable = error.Retryable,
            Action = action,
            Origin = ErpErrorOrigin.Inventory,
            RejectionPhase = ToRejectionPhase(error.Phase),
            DocumentKind = documentKind,
            DocumentNumber = documentNumber,
            ReferenceId = referenceId,
            AffectedDocumentKind = evidence?.DocumentKind ?? ErpDocumentKind.Unknown,
            AffectedDocumentNumber = evidence?.DocumentNumber,
            AffectedTransactionDate = evidence?.TransactionDate,
            ItemCode = evidence?.ItemCode,
            ItemDescription = evidence?.ItemDescription,
            Warehouse = evidence?.Warehouse,
            Location = evidence?.Location,
            LotNo = evidence?.LotNo
        });
    }

    /// <summary>Transaction type → the document noun an operator recognises. Unknown stays unknown.</summary>
    public static ErpDocumentKind ToDocumentKind(string? trxType) =>
        (trxType ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            IvTrxTypes.GoodsReceive => ErpDocumentKind.GoodsReceipt,
            IvTrxTypes.NonStockGoodsReceive => ErpDocumentKind.NonStockGoodsReceipt,
            IvTrxTypes.MiscellaneousReceipt => ErpDocumentKind.MiscellaneousReceipt,
            IvTrxTypes.MiscellaneousIssue => ErpDocumentKind.MiscellaneousIssue,
            IvTrxTypes.CustomerReturn => ErpDocumentKind.StockReturn,
            IvTrxTypes.VendorReturn => ErpDocumentKind.VendorReturn,
            IvTrxTypes.Scrap => ErpDocumentKind.Scrap,
            IvTrxTypes.StockTransfer => ErpDocumentKind.StockTransfer,
            IvTrxTypes.StockAdjustment => ErpDocumentKind.StockAdjustment,
            _ => ErpDocumentKind.Unknown
        };

    /// <summary>Replay-phase metadata → presenter phase. Unspecified stays unspecified.</summary>
    public static ErpRejectionPhase ToRejectionPhase(StockLedgerErrorPhase phase) => phase switch
    {
        StockLedgerErrorPhase.Removing => ErpRejectionPhase.Removing,
        StockLedgerErrorPhase.Rewinding => ErpRejectionPhase.Rewinding,
        StockLedgerErrorPhase.Replaying => ErpRejectionPhase.Replaying,
        _ => ErpRejectionPhase.NotSpecified
    };

    /// <summary>
    /// Short support reference quoted to the operator and written to the same log line, so support can
    /// find the original technical code and message. Not a diagnostic id inside the message text.
    /// </summary>
    public static string NewReferenceId() =>
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..10].ToUpperInvariant();
}
