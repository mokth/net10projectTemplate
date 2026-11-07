using ErpWeb.Core.Inventory;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.UI.Components.Common.OperatorError;

namespace ErpWeb.Tests.Production.Transaction;

/// <summary>
/// Phase 5 cross-module contract: a stock or costing refusal raised by Sales, Purchase or Production
/// reaches the operator as the same <see cref="ErpOperatorError"/> shape the Inventory screens use,
/// keeps the module and document in the headline, keeps POST and ROLLBACK distinct, and never leaks
/// the technical ledger text or invents a reason.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Production)]
[Trait(TestCategories.Name, TestCategories.Shared)]
public class PrCrossModuleOperatorErrorTests
{
    private const string TechnicalText = "INTERNAL_LEDGER_TEXT event=2 tail";

    public static TheoryData<ErpErrorOrigin, ErpDocumentKind, string, string, string> CrossModuleDocuments()
    {
        var data = new TheoryData<ErpErrorOrigin, ErpDocumentKind, string, string, string>();
        data.Add(ErpErrorOrigin.Sales, ErpDocumentKind.DeliveryOrder, "DO-0001", "SALES", "Delivery Order");
        data.Add(ErpErrorOrigin.Sales, ErpDocumentKind.SalesInvoice, "SI-0001", "SALES", "Sales Invoice");
        data.Add(ErpErrorOrigin.Purchase, ErpDocumentKind.PurchaseInvoice, "PI-0001", "PURCHASE", "Purchase Invoice");
        data.Add(ErpErrorOrigin.Purchase, ErpDocumentKind.PurchaseCreditDebitNote, "PCN-0001", "PURCHASE", "Purchase Credit/Debit Note");
        data.Add(ErpErrorOrigin.Production, ErpDocumentKind.MaterialIssue, "MI-0001", "PRODUCTION", "Material Issue");
        data.Add(ErpErrorOrigin.Production, ErpDocumentKind.DailyProduction, "DP-0001", "PRODUCTION", "Daily Production");
        data.Add(ErpErrorOrigin.Production, ErpDocumentKind.FinishedGoodReceipt, "FG-0001", "PRODUCTION", "Finished Good Receipt");
        data.Add(ErpErrorOrigin.Production, ErpDocumentKind.ProductionStockReturn, "PSR-0001", "PRODUCTION", "Production Stock Return");
        data.Add(ErpErrorOrigin.Production, ErpDocumentKind.ProductionStockAdjustment, "PADJ-0001", "PRODUCTION", "Production Stock Adjustment");
        data.Add(ErpErrorOrigin.Production, ErpDocumentKind.WorkOrderTransfer, "WOT-0001", "PRODUCTION", "Work Order Transfer");
        return data;
    }

    [Theory]
    [MemberData(nameof(CrossModuleDocuments))]
    public void Ledger_refusal_names_the_cross_module_document_and_module(
        ErpErrorOrigin origin,
        ErpDocumentKind kind,
        string number,
        string expectedOrigin,
        string expectedLabel)
    {
        var error = ErpOperatorErrors.FromLedgerError(
            new StockLedgerError(StockLedgerErrorCodes.InsufficientBaseQty, TechnicalText),
            origin,
            kind,
            number,
            ErpErrorAction.Post,
            "REF0000001");

        Assert.Equal($"Cannot post {expectedLabel} {number}.", error.Title);
        Assert.Equal($"{expectedOrigin}.STOCK_SHORTAGE_NOW", error.PresentationCode);
        Assert.Equal("REF0000001", error.ReferenceId);
        Assert.DoesNotContain("INTERNAL_LEDGER_TEXT", error.Title + error.Explanation + error.NextStep);
        Assert.DoesNotContain("event=2", error.Title + error.Explanation + error.NextStep);
    }

    [Fact]
    public void Posting_and_rollback_of_the_same_code_read_differently()
    {
        var ledgerError = new StockLedgerError(StockLedgerErrorCodes.InsufficientBaseQty, TechnicalText);

        var post = ErpOperatorErrors.FromLedgerError(
            ledgerError, ErpErrorOrigin.Production, ErpDocumentKind.MaterialIssue,
            "MI-0001", ErpErrorAction.Post, null);

        var rollback = ErpOperatorErrors.FromLedgerError(
            ledgerError, ErpErrorOrigin.Production, ErpDocumentKind.MaterialIssue,
            "MI-0001", ErpErrorAction.Rollback, null);

        Assert.Equal("PRODUCTION.STOCK_SHORTAGE_NOW", post.PresentationCode);
        Assert.Equal("PRODUCTION.HISTORICAL_NEGATIVE_STOCK", rollback.PresentationCode);
        Assert.Equal("Cannot post Material Issue MI-0001.", post.Title);
        Assert.Equal("Cannot roll back Material Issue MI-0001.", rollback.Title);
        Assert.Contains("not enough eligible stock", post.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Removing this record would cause insufficient historical stock", rollback.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(post.NextStep, rollback.NextStep);
    }

    [Fact]
    public void Reconciliation_code_is_never_presented_as_a_shortage()
    {
        var error = ErpOperatorErrors.FromLedgerError(
            new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, TechnicalText),
            ErpErrorOrigin.Production,
            ErpDocumentKind.FinishedGoodReceipt,
            "FG-0001",
            ErpErrorAction.Post,
            null);

        Assert.Equal("PRODUCTION.RECONCILIATION_REQUIRED", error.PresentationCode);
        Assert.Equal("The related stock or costing records need reconciliation.", error.Explanation);
        Assert.DoesNotContain("not enough", error.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insufficient", error.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unexpected_failure_stays_generic_and_keeps_the_reference()
    {
        var error = ErpOperatorErrors.Unexpected(
            ErpErrorOrigin.Sales, ErpDocumentKind.DeliveryOrder, "DO-0001", ErpErrorAction.Post, "REF0000002");

        Assert.Equal("SALES.UNKNOWN_FAILURE", error.PresentationCode);
        Assert.Equal("Cannot post Delivery Order DO-0001.", error.Title);
        Assert.Equal("The action could not be completed.", error.Explanation);
        Assert.Equal("REF0000002", error.ReferenceId);
        Assert.False(error.MayRetryAfterRefresh);
    }

    [Fact]
    public void Stale_and_already_processed_offer_different_next_actions()
    {
        var stale = ErpOperatorErrors.Stale(
            ErpErrorOrigin.Purchase, ErpDocumentKind.PurchaseInvoice, "PI-0001", ErpErrorAction.Post, null);
        var processed = ErpOperatorErrors.FromLedgerError(
            new StockLedgerError(StockLedgerErrorCodes.DocumentAlreadyPosted, TechnicalText),
            ErpErrorOrigin.Purchase, ErpDocumentKind.PurchaseInvoice, "PI-0001", ErpErrorAction.Post, null);

        Assert.True(stale.MayRetryAfterRefresh);
        Assert.Contains("Refresh", stale.NextStep!, StringComparison.OrdinalIgnoreCase);
        Assert.False(processed.MayRetryAfterRefresh);
        Assert.Contains("Do not submit again", processed.NextStep!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Batch_view_keeps_coded_and_plain_failures_together()
    {
        var coded = ErpOperatorErrors.FromLedgerError(
            new StockLedgerError(StockLedgerErrorCodes.InsufficientBaseQty, TechnicalText),
            ErpErrorOrigin.Production, ErpDocumentKind.MaterialIssue, "MI-0001", ErpErrorAction.Post, "REF0000003");

        var view = ErpOperatorFailureView.FromRecords(
        [
            new ErpOperatorFailureRecord("MI-0001", "technical", coded),
            new ErpOperatorFailureRecord("MI-0002", "Only NEW documents can be posted.", null)
        ], totalSelected: 3);

        Assert.NotNull(view);
        Assert.Equal("2 of the 3 selected records could not be completed.", view!.Headline);
        Assert.Equal("There is not enough eligible stock to issue these materials to Production.", view.Reason);
        Assert.Equal(2, view.Notes.Count);
        // The producer already names the document, so the page label is not repeated.
        Assert.Equal("Cannot post Material Issue MI-0001.", view.Notes[0].Title);
        Assert.Equal("REF0000003", view.Notes[0].Reference);
        Assert.Equal("Record MI-0002", view.Notes[1].Title);
        Assert.Equal("Only NEW documents can be posted.", view.Notes[1].Explanation);
    }

    [Fact]
    public void Single_cross_module_failure_reuses_the_same_banner_shape()
    {
        var coded = ErpOperatorErrors.ReconciliationRequired(
            ErpErrorOrigin.Production, ErpDocumentKind.WorkOrderTransfer, "WOT-0001", ErpErrorAction.Rollback, "REF0000004");

        var view = ErpOperatorFailureView.FromError(coded);

        Assert.NotNull(view);
        Assert.Equal("Cannot roll back Work Order Transfer WOT-0001.", view!.Headline);
        Assert.Equal(coded.Explanation, view.Reason);
        Assert.Single(view.Notes);
    }

    [Fact]
    public void Nothing_structured_keeps_the_pages_existing_message()
    {
        var view = ErpOperatorFailureView.FromRecords(
        [
            new ErpOperatorFailureRecord("MI-0001", "Only NEW documents can be posted.", null)
        ]);

        Assert.Null(view);
        Assert.Null(ErpOperatorFailureView.FromError(null));
    }

    [Fact]
    public void Operation_result_carries_the_structured_error_without_changing_the_message()
    {
        var coded = ErpOperatorErrors.FromLedgerError(
            new StockLedgerError(StockLedgerErrorCodes.InsufficientBaseQty, TechnicalText),
            ErpErrorOrigin.Production, ErpDocumentKind.FinishedGoodReceipt, "FG-0001", ErpErrorAction.Post, null);

        var result = IvMasterOperationResult<bool>.Fail(
            IvMasterErrorCode.Validation, "technical refusal", operatorError: coded);

        Assert.False(result.Succeeded);
        Assert.Equal("technical refusal", result.Message);
        Assert.Same(coded, result.OperatorError);
    }

    [Fact]
    public void Production_failure_wrapper_keeps_existing_string_returns_unchanged()
    {
        ProductionPostingFailure plain = "Only NEW documents can be posted.";

        Assert.Equal("Only NEW documents can be posted.", plain.Message);
        Assert.Null(plain.OperatorError);

        var coded = ErpOperatorErrors.Unexpected(
            ErpErrorOrigin.Production, ErpDocumentKind.MaterialIssue, "MI-0001", ErpErrorAction.Post, null);
        var enriched = new ProductionPostingFailure("technical", coded);

        Assert.Equal("technical", enriched.Message);
        Assert.Same(coded, enriched.OperatorError);
    }
}
