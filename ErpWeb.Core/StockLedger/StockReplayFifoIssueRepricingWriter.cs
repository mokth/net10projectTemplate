using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Appends a complete corrected split and consumption generation for a later
/// ordinary Inventory issue. The root caller must apply the prepared final
/// layers, item cost state, physical candidate and all other affected issues
/// before replay completion seals the transaction.
/// </summary>
public static class StockReplayFifoIssueRepricingWriter
{
    public static async Task<IReadOnlyList<StockValuationFact>> AppendAsync(
        StockPostingContext context,
        StockReplayFifoInsertionPlan plan,
        StockReplayFifoWritePermit permit,
        StockReplayEventKey issueKey,
        int reversalPostingLineNo,
        int replacementPostingLineNo,
        long? receiptLayerId,
        CancellationToken cancellationToken = default,
        bool allowSales = false)
    {
        StockReplayFifoReceiptLayerBinding.Validate(plan, receiptLayerId);
        await ValidateFifoPolicyAsync(context, plan, cancellationToken);
        return await AppendCoreAsync(context, plan.ItemCode, plan.Snapshot,
            plan.Projection.Issues, plan.PlanHash, "INSERT", permit, issueKey,
            reversalPostingLineNo, replacementPostingLineNo,
            receiptLayerId, cancellationToken, allowSales: allowSales);
    }

    public static async Task<IReadOnlyList<StockValuationFact>> AppendAsync(
        StockPostingContext context,
        StockReplayFifoInsertionPlan plan,
        StockReplayFifoWritePermit permit,
        StockReplayEventKey issueKey,
        int reversalPostingLineNo,
        int replacementPostingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        StockReplayFifoInsertionPlanner.ValidatePreparedPlan(plan);
        if (plan.PhysicalPlan.Candidate.SignedDelta >= 0m)
            throw Invalid("FIFO receipt issue correction needs its saved layer identity.");
        await ValidateFifoPolicyAsync(context, plan, cancellationToken);
        return await AppendCoreAsync(context, plan.ItemCode, plan.Snapshot,
            plan.Projection.Issues, plan.PlanHash, "INSERT", permit, issueKey,
            reversalPostingLineNo, replacementPostingLineNo, null, cancellationToken);
    }

    public static async Task<IReadOnlyList<StockValuationFact>> AppendAsync(
        StockPostingContext context,
        StockReplayFifoRemovalPlan plan,
        StockReplayFifoWritePermit permit,
        StockReplayEventKey issueKey,
        int reversalPostingLineNo,
        int replacementPostingLineNo,
        CancellationToken cancellationToken = default,
        bool allowSales = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        StockReplayFifoRemovalPlanner.ValidatePreparedPlan(plan);
        if (plan.PhysicalPlan.RemovedActiveEvent.SignedDelta == 0m
            || plan.PhysicalPlan.RemovedActiveEvent.SignedDelta > 0m
                && plan.Projection.CandidateReceipt is null)
            throw Invalid("FIFO issue correction needs a removed issue or receipt.");
        await ValidateFifoPolicyAsync(context,
            plan.PhysicalPlan.RemovedActiveEvent.Key.EffectiveAt.Date,
            plan.PhysicalPlan.LaterEvents.Select(e => e.Key.EffectiveAt.Date)
                .Append(plan.PhysicalPlan.RemovedActiveEvent.Key.EffectiveAt.Date).Max(),
            cancellationToken);
        return await AppendCoreAsync(context, plan.ItemCode, plan.Snapshot,
            plan.Projection.Issues, plan.PlanHash, "REMOVE", permit, issueKey,
            reversalPostingLineNo, replacementPostingLineNo, null, cancellationToken,
            allowSales: allowSales);
    }

    internal static async Task<IReadOnlyList<StockValuationFact>> AppendCountAsync(
        StockPostingContext context,
        StockReplayFifoCountSheetInsertionPlan plan,
        StockReplayFifoCountSheetWritePermit countPermit,
        StockReplayEventKey issueKey,
        int reversalPostingLineNo,
        int replacementPostingLineNo,
        IReadOnlyDictionary<long, StockFifoLayer> countLayers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(countPermit);
        ArgumentNullException.ThrowIfNull(countLayers);
        // The caller validates the prepared plan before writing. Staging a new
        // frozen basis assigns its Id, which changes the original plan hash.
        if (context.Posting.SourceDocumentType != "IV_STOCK_COUNT"
            || countPermit.RootPostingId != context.Posting.Id
            || countPermit.ItemCode != plan.ItemCode
            || countPermit.PlanHash != plan.PlanHash
            || countPermit.SnapshotHash != plan.Snapshot.SnapshotHash
            || countPermit.FinalQuantity != plan.Projection.FinalQuantity
            || countPermit.FinalValue != plan.Projection.FinalValue
            || plan.Projection.LaterIssues.Any(i => !i.DepletesPool))
            throw Invalid("FIFO count correction needs its root permit and supported issue tail.");
        var projectedReceipts = plan.Projection.Counts
            .Where(c => c.Receipt is not null)
            .Select(c => c.Receipt!).ToArray();
        if (projectedReceipts.Length != countLayers.Count
            || projectedReceipts.Any(receipt =>
                !countLayers.TryGetValue(receipt.LayerId, out var layer)
                || layer.Id <= 0
                || layer.OriginStockPostingId != context.Posting.Id
                || layer.OriginValuationFactId <= 0
                || layer.ItemCode != plan.ItemCode
                || layer.OriginalQty != receipt.OriginalQuantity
                || layer.OriginalValue != receipt.OriginalValue))
            throw Invalid("FIFO count correction lost a count-created layer.");
        await ValidateFifoPolicyAsync(context,
            plan.PhysicalSheet.EffectiveAt.Date,
            plan.Projection.LaterIssues.Select(i => i.Key.EffectiveAt.Date)
                .Append(plan.PhysicalSheet.EffectiveAt.Date).Max(),
            cancellationToken);
        return await AppendCoreAsync(context, plan.ItemCode, plan.Snapshot,
            plan.Projection.LaterIssues, plan.PlanHash, "COUNT",
            new StockReplayFifoWritePermit(context, plan), issueKey,
            reversalPostingLineNo, replacementPostingLineNo, null,
            cancellationToken, countLayers.ToDictionary(p => p.Key,
                p => p.Value.Id));
    }

    internal static async Task<IReadOnlyList<StockValuationFact>>
        AppendCountRemovalAsync(StockPostingContext context,
            StockReplayFifoCountSheetRemovalPlan plan,
            StockReplayFifoCountSheetRemovalItem item,
            StockReplayEventKey issueKey,
            int reversalPostingLineNo,
            int replacementPostingLineNo,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(item);
        StockReplayFifoCountSheetRemovalPlanner.ValidatePreparedPlan(plan);
        if (context.Posting.SourceDocumentType != "IV_STOCK_COUNT"
            || context.Posting.PostingRole != "REVERSAL"
            || !plan.Items.Contains(item)
            || item.Projection is null)
            throw Invalid("FIFO count issue correction needs its removal root.");
        await ValidateFifoPolicyAsync(context,
            plan.Sheet.EffectiveAt.Date,
            item.Projection.LaterIssues.Select(i => i.Key.EffectiveAt.Date)
                .Append(plan.Sheet.EffectiveAt.Date).Max(),
            cancellationToken);
        return await AppendCoreAsync(context, item.ItemCode, item.Snapshot,
            item.Projection.LaterIssues, plan.PlanHash, "COUNT_REMOVE",
            new StockReplayFifoWritePermit(context, plan, item), issueKey,
            reversalPostingLineNo, replacementPostingLineNo, null,
            cancellationToken);
    }

    private static async Task<IReadOnlyList<StockValuationFact>> AppendCoreAsync(
        StockPostingContext context, string itemCode,
        StockReplayFifoActiveLayerSnapshot snapshot,
        IReadOnlyList<StockReplayFifoProjectedIssue> issues, string planHash,
        string operation, StockReplayFifoWritePermit permit,
        StockReplayEventKey issueKey, int reversalPostingLineNo,
        int replacementPostingLineNo, long? receiptLayerId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<long, long>? countLayerIds = null,
        bool allowSales = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        if (permit.Operation != operation
            || permit.RootPostingId != context.Posting.Id
            || permit.RootPostingSequence != context.Posting.PostingSequence
            || permit.LedgerEpochId != context.Epoch.Id
            || permit.CompanyCode != context.CompanyCode
            || permit.BranchCode != context.BranchCode
            || permit.ItemCode != itemCode
            || permit.PlanHash != planHash
            || permit.SnapshotHash != snapshot.SnapshotHash
            || reversalPostingLineNo <= 0 || replacementPostingLineNo <= 0
            || reversalPostingLineNo == replacementPostingLineNo)
            throw Invalid("FIFO issue correction needs its validated root permit and unique lines.");

        var issue = issues.SingleOrDefault(i =>
            i.Key == issueKey && !i.IsCandidate && i.DepletesPool);
        if (issue is null || issueKey.LineIdentity.Length != 11
            || issueKey.LineIdentity[0] != 'H'
            || !int.TryParse(issueKey.LineIdentity.AsSpan(1), out var historyId)
            || historyId <= 0)
            throw Invalid("FIFO correction has no ordinary Inventory issue in the prepared tail.");
        var previous = snapshot.ActiveConsumptions
            .Where(c => c.IssueKey == issueKey).ToArray();
        if (previous.Length == 0
            || previous.Length != issue.PreviousConsumptions.Count
            || !previous.OrderBy(c => c.ConsumptionId)
                .SequenceEqual(issue.PreviousConsumptions
                    .OrderBy(c => c.ConsumptionId))
            || issue.Quantity != StockLedgerPrecision.Quantity(
                previous.Sum(c => c.Quantity))
            || issue.Allocations.Count == 0
            || issue.Allocations.Any(a => a.Quantity <= 0m
                || a.Value < 0m || a.LayerId <= 0)
            || issue.Quantity != StockLedgerPrecision.Quantity(
                issue.Allocations.Sum(a => a.Quantity))
            || issue.Value != StockLedgerPrecision.Money(
                issue.Allocations.Sum(a => a.Value)))
            throw Invalid("FIFO correction split differs from the prepared allocation.");
        var oldShape = previous.Select(c =>
                (c.LayerId, c.Quantity, c.Value))
            .OrderBy(x => x.LayerId).ToArray();
        var newShape = issue.Allocations.Select(a =>
                (a.LayerId, a.Quantity, a.Value))
            .OrderBy(x => x.LayerId).ToArray();
        if (oldShape.SequenceEqual(newShape))
            return [];

        var db = context.Db;
        var history = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == historyId
                && h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode,
                cancellationToken);
        if (history is null || history.ICode != itemCode
            || !(StockReplayFifoInventoryIssueKinds.IsSupported(history.TrxType)
                || allowSales && history.TrxType == IvTrxTypes.SalesOut)
            || history.LedgerVersion != 2
            || history.LedgerEpochId != context.Epoch.Id
            || history.EntryRole != "ORIGINAL"
            || history.BatchStatus != "POSTED"
            || history.ValuationStatus != StockValuationStatuses.Valued
            || history.FromBalLocId is not > 0
            || history.ToBalLocId is > 0
            || history.FrStdQty != issue.Quantity
            || history.ToStdQty is > 0m
            || history.TrxDtTime != issueKey.EffectiveAt)
            throw Invalid($"FIFO history {historyId} is not a supported active MI issue.");
        var activeFacts = (await StockReplayValuationGenerationResolver
                .ResolveAsync(db, context.CompanyCode, context.BranchCode,
                    [history], cancellationToken))[historyId]
            .OrderBy(f => f.SplitOrdinal).ToArray();
        if (activeFacts.Length != previous.Length
            || activeFacts.Any(f => f.CostMethod != StockCostMethods.Fifo
                || f.ValuationSource != StockValuationSources.Fifo
                || f.MovementCode !=
                    StockReplayFifoInventoryIssueKinds.MovementCode(
                        history.TrxType)
                || f.Direction != -1 || f.EffectiveAt != issueKey.EffectiveAt
                || f.TransactionCurrency is not null
                || f.TransactionCostAmount is not null
                || f.ExchangeRate is not null
                || f.BaseCostAmount != f.CostAmount)
            || activeFacts.Select(f => (f.StockPostingId, f.ValuationVersion))
                .Distinct().Count() != 1)
            throw Invalid($"FIFO history {historyId} has an unsupported active issue generation.");
        var byFact = previous.ToDictionary(c => c.IssueFactId);
        if (byFact.Count != activeFacts.Length
            || activeFacts.Any(f => !byFact.TryGetValue(f.Id, out var c)
                || f.SplitOrdinal != c.SplitOrdinal
                || f.BaseQty != c.Quantity || f.CostAmount != c.Value))
            throw Invalid($"FIFO history {historyId} consumption differs from active facts.");
        var persisted = await db.StockFifoLayerConsumptions.AsNoTracking()
            .Where(c => activeFacts.Select(f => f.Id)
                .Contains(c.IssueValuationFactId))
            .ToArrayAsync(cancellationToken);
        if (persisted.Length != previous.Length
            || persisted.Any(c => !previous.Any(p => p.ConsumptionId == c.Id
                && p.IssueFactId == c.IssueValuationFactId
                && p.LayerId == c.FifoLayerId
                && p.Quantity == c.ConsumedQty
                && p.Value == c.ConsumedValue
                && p.SplitOrdinal == c.SplitOrdinal)))
            throw Invalid($"FIFO history {historyId} has changed consumption evidence.");
        var replacementOccupied = await db.StockValuationFacts.AsNoTracking()
            .AnyAsync(f => f.StockPostingId == context.Posting.Id
                && f.PostingLineNo == replacementPostingLineNo,
                cancellationToken);
        if (replacementOccupied || db.ChangeTracker.Entries<StockValuationFact>()
            .Any(e => e.State == EntityState.Added
                && e.Entity.StockPostingId == context.Posting.Id
                && e.Entity.PostingLineNo == replacementPostingLineNo))
            throw Invalid("The FIFO replacement root line is already used.");

        var reversals = await StockReplayValuationReversalWriter.AppendAsync(
            context, historyId, activeFacts.Select((f, i) =>
                new StockReplayValuationReversalLine(
                    f.Id, reversalPostingLineNo, i)).ToArray(),
            cancellationToken);
        var reverseById = reversals.ToDictionary(
            f => f.ReversesValuationFactId!.Value);
        var now = DateTime.UtcNow;
        foreach (var old in previous)
        {
            db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                IssueValuationFact = reverseById[old.IssueFactId],
                FifoLayerId = old.LayerId,
                ConsumedQty = old.Quantity,
                ConsumedValue = old.Value,
                SplitOrdinal = old.SplitOrdinal,
                ReversesConsumptionId = old.ConsumptionId,
                CreatedAtUtc = now,
                CreatedBy = context.UserId
            });
        }
        var template = activeFacts[0];
        var originalFactId = template.OriginalValuationFactId ?? template.Id;
        var version = checked(template.ValuationVersion + 1);
        var replacements = issue.Allocations.Select((allocation, ordinal) =>
            new StockValuationFact
            {
                CompanyCode = template.CompanyCode,
                BranchCode = template.BranchCode,
                LedgerEpochId = template.LedgerEpochId,
                StockPostingId = context.Posting.Id,
                PostingLineNo = replacementPostingLineNo,
                SplitOrdinal = ordinal,
                SourceLineId = template.SourceLineId,
                SourceDocumentType = template.SourceDocumentType,
                SourceDocumentId = template.SourceDocumentId,
                SourceDocumentNo = template.SourceDocumentNo,
                SourceDocumentLine = template.SourceDocumentLine,
                EffectiveAt = template.EffectiveAt,
                BusinessDate = template.BusinessDate,
                PeriodKey = template.PeriodKey,
                ItemCode = template.ItemCode,
                WarehouseCode = template.WarehouseCode,
                LocationCode = template.LocationCode,
                LotId = template.LotId,
                LotNo = template.LotNo,
                ItemStatus = template.ItemStatus,
                BaseUom = template.BaseUom,
                MovementCode = template.MovementCode,
                Direction = -1,
                BaseQty = allocation.Quantity,
                CostMethod = StockCostMethods.Fifo,
                UnitCost = allocation.UnitCost,
                CostAmount = allocation.Value,
                BaseCurrency = template.BaseCurrency,
                BaseCostAmount = allocation.Value,
                ValuationSource = StockValuationSources.Fifo,
                ValuationStatus = StockValuationStatuses.Valued,
                ValuationVersion = version,
                InventoryHistoryId = historyId,
                FromBalLocId = template.FromBalLocId,
                ToBalLocId = template.ToBalLocId,
                OriginalValuationFactId = originalFactId,
                CreatedAtUtc = now,
                CreatedBy = context.UserId
            }).ToArray();
        db.StockValuationFacts.AddRange(replacements);
        for (var i = 0; i < issue.Allocations.Count; i++)
        {
            var allocation = issue.Allocations[i];
            db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                IssueValuationFact = replacements[i],
                FifoLayerId = countLayerIds is not null
                    && countLayerIds.TryGetValue(allocation.LayerId,
                        out var savedCountLayerId)
                        ? savedCountLayerId
                        : StockReplayFifoReceiptLayerBinding.Map(
                            allocation.LayerId, receiptLayerId),
                ConsumedQty = allocation.Quantity,
                ConsumedValue = allocation.Value,
                SplitOrdinal = i,
                CreatedAtUtc = now,
                CreatedBy = context.UserId
            });
        }
        return replacements;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    internal static async Task ValidateFifoPolicyAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        CancellationToken cancellationToken)
    {
        var first = plan.PhysicalPlan.Candidate.Key.EffectiveAt.Date;
        var last = plan.PhysicalPlan.LaterEvents
            .Select(e => e.Key.EffectiveAt.Date)
            .Append(first).Max();
        await ValidateFifoPolicyAsync(context, first, last, cancellationToken);
    }

    internal static async Task ValidateFifoPolicyAsync(
        StockPostingContext context, DateTime first, DateTime last,
        CancellationToken cancellationToken)
    {
        var policies = await context.Db.StockCostPolicyRevisions
            .AsNoTracking()
            .Where(p => p.CompanyCode == context.CompanyCode
                && p.BranchCode == context.BranchCode
                && p.Status == StockCostPolicyStatuses.Active
                && p.EffectiveFrom <= last
                && (p.EffectiveTo == null || p.EffectiveTo > first))
            .OrderBy(p => p.EffectiveFrom).ThenBy(p => p.Id)
            .ToArrayAsync(cancellationToken);
        var cursor = first;
        for (var i = 0; i < policies.Length; i++)
        {
            var policy = policies[i];
            if ((i == 0 ? policy.EffectiveFrom > cursor
                    : policy.EffectiveFrom != cursor)
                || policy.CostMethod != StockCostMethods.Fifo)
                throw Invalid("FIFO issue replay crosses a missing or changed costing policy.");
            cursor = policy.EffectiveTo ?? DateTime.MaxValue;
            if (cursor <= policy.EffectiveFrom)
                throw Invalid("FIFO issue replay has an invalid costing policy range.");
        }
        if (cursor <= last)
            throw Invalid("FIFO issue replay has no costing policy covering its tail.");
    }
}
