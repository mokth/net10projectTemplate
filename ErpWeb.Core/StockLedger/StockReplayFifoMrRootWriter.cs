using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoMrWriteResult(
    StockReplayFifoMrReceiptValuation Receipt,
    IReadOnlyList<int> RequiredActiveHistoryIds);

/// <summary>
/// Writes one approved FIFO MR and every supported later MI correction inside
/// its prepared replay root. The caller completes and reconciles before commit.
/// </summary>
public static class StockReplayFifoMrRootWriter
{
    public static async Task<StockReplayFifoMrWriteResult> InsertAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        StockReplayFifoWritePermit permit, int batchNo,
        CancellationToken cancellationToken = default,
        bool goodsReceipt = false, bool allowInvoiced = false,
        bool allowSales = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        if (permit.Operation != "INSERT"
            || permit.RootPostingId != context.Posting.Id
            || permit.PlanHash != plan.PlanHash
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != (goodsReceipt
                ? IvTrxTypes.GoodsReceive : IvTrxTypes.MiscellaneousReceipt)
            || context.Posting.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.PostingRole != "PRIMARY")
            throw Invalid("FIFO MR root needs its prepared insertion permit.");
        StockReplayFifoReceiptLayerBinding.ValidatePlan(plan);
        var changed = StockReplayFifoMiRootWriter.ChangedIssues(
            plan.Projection);
        var historyId = await StockReplayFifoMrReceiptPhysicalWriter
            .AppendAsync(context, plan, permit, batchNo, cancellationToken,
                goodsReceipt, allowInvoiced);
        var receipt = await StockReplayFifoMrReceiptValuationWriter
            .AppendAsync(context, plan, permit, historyId, 1,
                cancellationToken, goodsReceipt);
        var required = new List<int>(changed.Count + 1) { historyId };
        var line = 2;
        foreach (var issue in changed)
        {
            var replacements = issue.DepletesPool
                ? await StockReplayFifoIssueRepricingWriter.AppendAsync(
                    context, plan, permit, issue.Key,
                    line, line + 1, receipt.LayerId, cancellationToken, allowSales)
                : await StockReplayFifoTransferRepricingWriter.AppendAsync(
                    context, plan, permit, issue.Key,
                    line, line + 1, receipt.LayerId, cancellationToken);
            if (replacements.Count == 0)
                throw Invalid("A changed FIFO issue produced no replacement generation.");
            required.Add(StockReplayFifoMiRootWriter.HistoryId(issue.Key));
            line += 2;
        }
        await StockReplayFifoStateWriter.ApplyMiscReceiptInsertionAsync(
            context, plan, permit, receipt, cancellationToken);
        return new(receipt, required);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
