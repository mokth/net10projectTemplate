using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Checks the sealed FIFO state before its enclosing replay transaction commits.
/// A failure still rolls the entire root back.
/// </summary>
public static class StockReplayFifoReconciliationGate
{
    public static async Task VerifySealedWithinRootAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        StockReplayFifoWritePermit permit,
        StockReplayFifoMrReceiptValuation receipt,
        CancellationToken cancellationToken = default)
    {
        StockReplayFifoReceiptLayerBinding.Validate(plan, receipt.LayerId);
        await VerifyCoreAsync(context, plan.ItemCode, plan.Snapshot,
            plan.Projection, plan.PlanHash, "INSERT", null, permit,
            cancellationToken, receipt);
    }

    public static async Task VerifySealedWithinRootAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        StockReplayFifoWritePermit permit,
        CancellationToken cancellationToken = default)
    {
        StockReplayFifoInsertionPlanner.ValidatePreparedPlan(plan);
        await VerifyCoreAsync(context, plan.ItemCode, plan.Snapshot,
            plan.Projection, plan.PlanHash, "INSERT", null, permit,
            cancellationToken);
    }

    public static async Task VerifySealedWithinRootAsync(
        StockPostingContext context, StockReplayFifoRemovalPlan plan,
        StockReplayFifoWritePermit permit,
        CancellationToken cancellationToken = default)
    {
        StockReplayFifoRemovalPlanner.ValidatePreparedPlan(plan);
        await VerifyCoreAsync(context, plan.ItemCode, plan.Snapshot,
            plan.Projection, plan.PlanHash, "REMOVE",
            plan.PhysicalPlan.RemovedActiveEvent.SignedDelta < 0m
                ? plan.PhysicalPlan.RemovedActiveEvent.Key : null,
            permit, cancellationToken, null,
            plan.PhysicalPlan.RemovedActiveEvent.SignedDelta > 0m
                ? plan.Projection.CandidateReceipt : null);
    }

    private static async Task VerifyCoreAsync(
        StockPostingContext context, string itemCode,
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayFifoTailProjection projection, string planHash,
        string operation, StockReplayEventKey? removedIssueKey,
        StockReplayFifoWritePermit permit,
        CancellationToken cancellationToken,
        StockReplayFifoMrReceiptValuation? receipt = null,
        StockReplayFifoLayerPosition? removedReceipt = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(permit);
        if (context.Db.Database.CurrentTransaction is null
            || context.Posting.SealedAtUtc is null
            || permit.Operation != operation
            || permit.RootPostingId != context.Posting.Id
            || permit.RootPostingSequence != context.Posting.PostingSequence
            || permit.LedgerEpochId != context.Epoch.Id
            || permit.CompanyCode != context.CompanyCode
            || permit.BranchCode != context.BranchCode
            || permit.ItemCode != itemCode
            || permit.PlanHash != planHash
            || permit.SnapshotHash != snapshot.SnapshotHash)
            throw Invalid("FIFO reconciliation needs its sealed root and prepared permit.");
        var current = await StockReplayFifoActiveLayerReader.ReadAsync(
            context.Db, context.CompanyCode, context.BranchCode,
            itemCode, cancellationToken);
        if (current.CurrentQuantity != permit.FinalQuantity
            || current.CurrentValue != permit.FinalValue
            || current.Layers.Count != projection.FinalLayers.Count)
            throw Invalid("Sealed FIFO item totals differ from the replay projection.");
        var projected = projection.FinalLayers.ToDictionary(l =>
            StockReplayFifoReceiptLayerBinding.Map(l.LayerId, receipt?.LayerId));
        if (current.Layers.Any(layer =>
                !projected.TryGetValue(layer.LayerId, out var final)
                || layer.RemainingQuantity != final.RemainingQuantity
                || layer.RemainingValue != final.RemainingValue
                || layer.CurrentUnitCost != final.CurrentUnitCost
                || layer.Status != final.Status))
            throw Invalid("Sealed FIFO layers differ from the replay projection.");
        if (receipt is not null)
        {
            var candidate = projection.CandidateReceipt;
            var actual = current.Layers.SingleOrDefault(l =>
                l.LayerId == receipt.LayerId);
            if (candidate is null || actual is null
                || snapshot.Layers.Any(l => l.LayerId == receipt.LayerId)
                || actual.OriginFactId != receipt.FactId
                || actual.OriginPostingId != context.Posting.Id
                || actual.OriginalQuantity != candidate.OriginalQuantity
                || actual.OriginalValue != candidate.OriginalValue
                || candidate.ReceiptKey is not { } candidateKey
                || actual.ReceiptKey != candidateKey with
                    { LineIdentity = $"H{receipt.HistoryId:D10}" })
                throw Invalid("Sealed FIFO receipt origin differs from the prepared candidate.");
        }
        if (removedReceipt is not null)
        {
            var layer = await context.Db.StockFifoLayers.AsNoTracking()
                .SingleOrDefaultAsync(l => l.Id == removedReceipt.LayerId,
                    cancellationToken);
            var reversalCount = await context.Db.StockValuationFacts.AsNoTracking()
                .CountAsync(f => f.StockPostingId == context.Posting.Id
                    && f.ReversesValuationFactId
                        == removedReceipt.OriginFactId
                    && f.Direction == -1
                    && f.ValuationStatus == StockValuationStatuses.Reversed,
                    cancellationToken);
            if (layer is null || layer.Status != StockFifoLayerStatuses.Closed
                || layer.RemainingQty != 0m || layer.RemainingValue != 0m
                || layer.CurrentUnitCost != 0m || reversalCount != 1
                || current.Layers.Any(l => l.LayerId == removedReceipt.LayerId))
                throw Invalid("The removed FIFO receipt remains active after sealing.");
        }
        if (removedIssueKey is { } removed
            && current.ActiveConsumptions.Any(c => c.IssueKey == removed))
            throw Invalid("The removed FIFO issue still has an active consumption.");
        foreach (var issue in projection.Issues.Where(i => !i.IsCandidate))
        {
            var active = current.ActiveConsumptions
                .Where(c => c.IssueKey == issue.Key).ToArray();
            var expected = issue.Allocations
                .Select(a => (LayerId: StockReplayFifoReceiptLayerBinding.Map(
                    a.LayerId, receipt?.LayerId), a.Quantity, a.Value))
                .OrderBy(a => a.LayerId).ThenBy(a => a.Quantity)
                .ThenBy(a => a.Value).ToArray();
            var actual = active
                .Select(c => (c.LayerId, c.Quantity, c.Value))
                .OrderBy(a => a.LayerId).ThenBy(a => a.Quantity)
                .ThenBy(a => a.Value).ToArray();
            if (!expected.SequenceEqual(actual)
                || active.Sum(c => c.Quantity) != issue.Quantity
                || active.Sum(c => c.Value) != issue.Value
                || active.Any(c => c.DepletesPool != issue.DepletesPool))
                throw Invalid($"FIFO issue {issue.Key.LineIdentity} has an incomplete corrected allocation.");
        }
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
