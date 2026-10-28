using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoMrRemovalWriteResult(
    int ReversalHistoryId, IReadOnlyList<int> RequiredActiveHistoryIds);

/// <summary>
/// Removes one FIFO MR receipt and corrects supported later MI allocations
/// inside a caller-owned reversal root.
/// </summary>
public static class StockReplayFifoMrRemovalRootWriter
{
    public static async Task<StockReplayFifoMrRemovalWriteResult> RemoveAsync(
        StockPostingContext context, StockReplayFifoRemovalPlan plan,
        StockReplayFifoWritePermit permit, int batchNo,
        CancellationToken cancellationToken = default,
        bool goodsReceipt = false, bool allowSales = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        StockReplayFifoRemovalPlanner.ValidatePreparedPlan(plan);
        if (plan.PhysicalPlan.RemovedActiveEvent.SignedDelta <= 0m
            || plan.Projection.CandidateReceipt is null
            || permit.Operation != "REMOVE"
            || permit.PlanHash != plan.PlanHash
            || permit.RootPostingId != context.Posting.Id
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != (goodsReceipt
                ? IvTrxTypes.GoodsReceive : IvTrxTypes.MiscellaneousReceipt)
            || context.Posting.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.PostingRole != "REVERSAL")
            throw Invalid("FIFO MR rollback needs its prepared receipt permit.");
        var changed = StockReplayFifoMiRootWriter.ChangedIssues(
            plan.Projection);
        var required = new List<int>(changed.Count);
        var line = 2;
        foreach (var issue in changed)
        {
            var replacements = issue.DepletesPool
                ? await StockReplayFifoIssueRepricingWriter.AppendAsync(
                    context, plan, permit, issue.Key,
                    line, line + 1, cancellationToken, allowSales)
                : await StockReplayFifoTransferRepricingWriter.AppendAsync(
                    context, plan, permit, issue.Key,
                    line, line + 1, cancellationToken);
            if (replacements.Count == 0)
                throw Invalid("A changed FIFO issue produced no replacement generation.");
            required.Add(StockReplayFifoMiRootWriter.HistoryId(issue.Key));
            line += 2;
        }
        var reversalHistoryId = await StockReplayFifoMrReceiptRemovalPhysicalWriter
            .AppendAsync(context, plan, permit, batchNo, cancellationToken,
                goodsReceipt);
        await StockReplayFifoMrReceiptRemovalValuationWriter.AppendAsync(
            context, plan, permit, reversalHistoryId, 1, cancellationToken,
            goodsReceipt);
        await StockReplayFifoStateWriter.ApplyMiscReceiptRemovalAsync(
            context, plan, permit, cancellationToken);
        return new(reversalHistoryId, required);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
