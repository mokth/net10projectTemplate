using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Production;

/// <summary>
/// Builds the cross-module operator error for a Production posting/rollback refusal and writes the
/// matching server log line, so the support reference shown to the operator can be found later.
/// </summary>
/// <remarks>
/// Presentation and diagnostics only: it runs after the business code has already refused the action,
/// never changes a decision, a status, a quantity or a cost, and never replaces the original technical
/// message that the caller already returns.
/// </remarks>
internal static class ProductionOperatorErrorLog
{
    /// <summary>A stock-ledger refusal (code, phase and retryability stay exactly as produced).</summary>
    public static ErpOperatorError Ledger(
        ILogger? logger,
        Exception? exception,
        StockLedgerError error,
        ErpDocumentKind documentKind,
        string? documentNumber,
        ErpErrorAction action,
        string operation)
    {
        var reference = ErpOperatorErrors.NewReferenceId();
        logger?.LogError(exception,
            "Production {Operation} for {Document} {DocumentNumber} was refused by the stock ledger "
            + "(ref {Reference}, code {Code}, retryable {Retryable}).",
            operation, documentKind, documentNumber, reference, error.Code, error.Retryable);
        return ErpOperatorErrors.FromLedgerError(
            error, ErpErrorOrigin.Production, documentKind, documentNumber, action, reference);
    }

    /// <summary>The stock and costing records disagree; a person must reconcile them.</summary>
    public static ErpOperatorError Reconciliation(
        ILogger? logger,
        Exception? exception,
        ErpDocumentKind documentKind,
        string? documentNumber,
        ErpErrorAction action,
        string operation)
    {
        var reference = ErpOperatorErrors.NewReferenceId();
        logger?.LogError(exception,
            "Production {Operation} for {Document} {DocumentNumber} failed a posting invariant "
            + "and needs reconciliation (ref {Reference}).",
            operation, documentKind, documentNumber, reference);
        return ErpOperatorErrors.ReconciliationRequired(
            ErpErrorOrigin.Production, documentKind, documentNumber, action, reference);
    }

    /// <summary>Anything else. The exception is logged; the operator only sees generic text.</summary>
    public static ErpOperatorError Unexpected(
        ILogger? logger,
        Exception? exception,
        ErpDocumentKind documentKind,
        string? documentNumber,
        ErpErrorAction action,
        string operation)
    {
        var reference = ErpOperatorErrors.NewReferenceId();
        logger?.LogError(exception,
            "Production {Operation} for {Document} {DocumentNumber} failed unexpectedly (ref {Reference}).",
            operation, documentKind, documentNumber, reference);
        return ErpOperatorErrors.Unexpected(
            ErpErrorOrigin.Production, documentKind, documentNumber, action, reference);
    }
}
