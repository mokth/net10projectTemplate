using ErpWeb.Core.Inventory;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;

namespace ErpWeb.Tests.Inventory.Transaction;

/// <summary>
/// The bridge between a persisted ledger error and the operator contract. These are the only mapping
/// rules the posting boundary is allowed to apply: transaction type chooses the document noun, replay
/// phase metadata chooses the wording, and nothing else may be inferred.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryPosting)]
public class IvInventoryOperatorErrorsTests
{
    [Theory]
    [InlineData(IvTrxTypes.GoodsReceive, ErpDocumentKind.GoodsReceipt)]
    [InlineData(IvTrxTypes.NonStockGoodsReceive, ErpDocumentKind.NonStockGoodsReceipt)]
    [InlineData(IvTrxTypes.MiscellaneousReceipt, ErpDocumentKind.MiscellaneousReceipt)]
    [InlineData(IvTrxTypes.MiscellaneousIssue, ErpDocumentKind.MiscellaneousIssue)]
    [InlineData(IvTrxTypes.CustomerReturn, ErpDocumentKind.StockReturn)]
    [InlineData(IvTrxTypes.VendorReturn, ErpDocumentKind.VendorReturn)]
    [InlineData(IvTrxTypes.Scrap, ErpDocumentKind.Scrap)]
    [InlineData(IvTrxTypes.StockTransfer, ErpDocumentKind.StockTransfer)]
    [InlineData(IvTrxTypes.StockAdjustment, ErpDocumentKind.StockAdjustment)]
    [InlineData(" gr ", ErpDocumentKind.GoodsReceipt)]
    public void Transaction_type_selects_the_document_noun(string trxType, ErpDocumentKind expected) =>
        Assert.Equal(expected, IvInventoryOperatorErrors.ToDocumentKind(trxType));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ZZ")]
    public void Unknown_transaction_type_stays_unknown(string? trxType) =>
        Assert.Equal(ErpDocumentKind.Unknown, IvInventoryOperatorErrors.ToDocumentKind(trxType));

    [Theory]
    [InlineData(StockLedgerErrorPhase.Removing, ErpRejectionPhase.Removing)]
    [InlineData(StockLedgerErrorPhase.Rewinding, ErpRejectionPhase.Rewinding)]
    [InlineData(StockLedgerErrorPhase.Replaying, ErpRejectionPhase.Replaying)]
    [InlineData(StockLedgerErrorPhase.Unspecified, ErpRejectionPhase.NotSpecified)]
    public void Replay_phase_metadata_is_carried_through(
        StockLedgerErrorPhase phase,
        ErpRejectionPhase expected) =>
        Assert.Equal(expected, IvInventoryOperatorErrors.ToRejectionPhase(phase));

    [Fact]
    public void Goods_receipt_rollback_names_the_receipt_and_hides_ledger_terms()
    {
        var error = IvInventoryOperatorErrors.FromLedgerError(
            new StockLedgerError(
                StockLedgerErrorCodes.InsufficientBaseQty,
                "Physical replay would make the stock slice negative at event 42 while replaying.")
            {
                Phase = StockLedgerErrorPhase.Replaying,
                EventId = 42
            },
            IvTrxTypes.GoodsReceive,
            post: false,
            batchNo: 1,
            referenceId: "REF0000001");

        Assert.Equal("INVENTORY.HISTORICAL_NEGATIVE_STOCK_REPLAYING", error.PresentationCode);
        Assert.Equal("Cannot roll back Goods Receipt 1.", error.Title);
        Assert.Equal("REF0000001", error.ReferenceId);

        var text = error.ToOperatorText();
        Assert.DoesNotContain("slice", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("event", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("replaying", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("42", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Posting_a_shortage_is_not_described_as_a_historical_problem()
    {
        var error = IvInventoryOperatorErrors.FromLedgerError(
            new StockLedgerError(StockLedgerErrorCodes.InsufficientBaseQty, "Internal."),
            IvTrxTypes.MiscellaneousIssue,
            post: true,
            batchNo: 7,
            referenceId: null);

        Assert.Equal("INVENTORY.STOCK_SHORTAGE_NOW", error.PresentationCode);
        Assert.Equal("Cannot post Miscellaneous Issue 7.", error.Title);
        Assert.False(error.HasReference);
    }

    [Fact]
    public void Retryable_metadata_never_advertises_a_retry_on_its_own()
    {
        var error = IvInventoryOperatorErrors.FromLedgerError(
            new StockLedgerError(StockLedgerErrorCodes.InsufficientBaseQty, "Internal.", Retryable: true),
            IvTrxTypes.GoodsReceive,
            post: false,
            batchNo: 1,
            referenceId: null);

        Assert.False(error.MayRetryAfterRefresh);
    }

    [Fact]
    public void Busy_stock_is_retryable_and_says_so()
    {
        var error = IvInventoryOperatorErrors.FromLedgerError(
            new StockLedgerError(StockLedgerErrorCodes.StockBusy, "Internal.", Retryable: true),
            IvTrxTypes.GoodsReceive,
            post: true,
            batchNo: 2,
            referenceId: null);

        Assert.True(error.MayRetryAfterRefresh);
        Assert.Equal("Cannot post Goods Receipt 2.", error.Title);
    }

    [Fact]
    public void Reference_id_is_short_upper_case_and_unique()
    {
        var first = IvInventoryOperatorErrors.NewReferenceId();
        var second = IvInventoryOperatorErrors.NewReferenceId();

        Assert.Equal(10, first.Length);
        Assert.Equal(first.ToUpperInvariant(), first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Null_error_is_rejected_rather_than_silently_presented()
    {
        Assert.Throws<ArgumentNullException>(() => IvInventoryOperatorErrors.FromLedgerError(
            null!, IvTrxTypes.GoodsReceive, post: false, batchNo: 1, referenceId: null));
    }

    [Fact]
    public void Verified_evidence_names_the_affected_document_date_and_item()
    {
        var error = IvInventoryOperatorErrors.FromLedgerError(
            new StockLedgerError(
                StockLedgerErrorCodes.InsufficientBaseQty,
                "Internal technical text.")
            {
                Phase = StockLedgerErrorPhase.Replaying,
                EventId = 42
            },
            IvTrxTypes.GoodsReceive,
            post: false,
            batchNo: 1,
            referenceId: "REF0000001",
            new IvFailureEvidence
            {
                DocumentKind = ErpDocumentKind.MiscellaneousIssue,
                DocumentNumber = "7",
                TransactionDate = new DateTime(2026, 10, 2),
                ItemCode = "A100",
                ItemDescription = "Stock A",
                Warehouse = "MAIN",
                Location = "BIN1"
            });

        Assert.Equal("INVENTORY.HISTORICAL_NEGATIVE_STOCK_REPLAYING", error.PresentationCode);
        Assert.Equal("Miscellaneous Issue 7, dated 02/10/2026",
            Assert.Single(error.Details, d => d.Label == "Affected transaction").Value);
        Assert.Equal("A100 — Stock A",
            Assert.Single(error.Details, d => d.Label == "Item").Value);
        Assert.Equal("MAIN", Assert.Single(error.Details, d => d.Label == "Warehouse").Value);
        Assert.Contains("on 02/10/2026", error.Explanation, StringComparison.Ordinal);

        var text = error.ToOperatorText();
        Assert.DoesNotContain("event", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("42", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_evidence_leaves_the_explanation_generic()
    {
        var error = IvInventoryOperatorErrors.FromLedgerError(
            new StockLedgerError(StockLedgerErrorCodes.InsufficientBaseQty, "Internal.")
            {
                Phase = StockLedgerErrorPhase.Replaying,
                EventId = 42
            },
            IvTrxTypes.GoodsReceive,
            post: false,
            batchNo: 1,
            referenceId: null);

        Assert.DoesNotContain(error.Details, d => d.Label == "Affected transaction");
        Assert.DoesNotContain(error.Details, d => d.Label == "Item");
        Assert.DoesNotContain("dated", error.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_stock_count_generation_has_no_document_number_invented()
    {
        var error = IvInventoryOperatorErrors.FromLedgerError(
            new StockLedgerError(StockLedgerErrorCodes.InsufficientBaseQty, "Internal.")
            {
                Phase = StockLedgerErrorPhase.Replaying,
                EventId = -1
            },
            IvTrxTypes.GoodsReceive,
            post: false,
            batchNo: 1,
            referenceId: null,
            new IvFailureEvidence { DocumentKind = ErpDocumentKind.StockCount, IsStockCountGeneration = true });

        Assert.DoesNotContain(error.Details, d => d.Label == "Affected transaction");
        Assert.DoesNotContain("-1", error.ToOperatorText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Stock_count_overload_names_the_family_without_inventing_a_number()
    {
        var error = IvInventoryOperatorErrors.FromLedgerError(
            new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, "Internal."),
            ErpDocumentKind.StockCount,
            documentNumber: null,
            ErpErrorAction.Post,
            "REF0000001");

        Assert.Equal("INVENTORY.RECONCILIATION_REQUIRED", error.PresentationCode);
        Assert.Equal("Cannot post this stock count.", error.Title);
        Assert.Equal("REF0000001", error.ReferenceId);
        Assert.False(string.IsNullOrWhiteSpace(error.NextStep));
    }

    [Fact]
    public void Aggregate_exposes_only_failed_batch_operator_errors_and_keeps_technical_totals()
    {
        ErpOperatorError Note(string reference) => new()
        {
            PresentationCode = "INVENTORY.TEST",
            Title = "Title",
            Explanation = "Explanation",
            NextStep = "Next",
            ReferenceId = reference
        };

        var result = IvInventoryPostingResult.FromBatches(
        [
            IvInventoryPostingBatchResult.Ok(1, Guid.NewGuid()),
            IvInventoryPostingBatchResult.Fail(2, "technical one", Note("A")),
            IvInventoryPostingBatchResult.Fail(3, "technical two"),
            IvInventoryPostingBatchResult.Fail(4, "technical three", Note("B"))
        ]);

        Assert.True(result.HasOperatorErrors);
        Assert.Equal(new[] { "A", "B" }, result.OperatorErrors.Select(x => x.ReferenceId));
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(3, result.FailedCount);
        Assert.Equal("technical one", result.Batches[1].ErrorMessage);
        Assert.Contains("technical one", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Result_without_structured_errors_advertises_none()
    {
        var result = IvInventoryPostingResult.Fail("Technical only.");

        Assert.False(result.HasOperatorErrors);
        Assert.Empty(result.OperatorErrors);
        Assert.Equal("Technical only.", result.ErrorMessage);
    }
}
