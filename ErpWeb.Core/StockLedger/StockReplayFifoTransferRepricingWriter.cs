using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Reprices both legs and FIFO consumption evidence of an unchanged physical
/// transfer. Its item quantity and value remain neutral under the replay root.
/// </summary>
public static class StockReplayFifoTransferRepricingWriter
{
    public static async Task<IReadOnlyList<StockValuationFact>> AppendAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        StockReplayFifoWritePermit permit, StockReplayEventKey transferKey,
        int reversalPostingLineNo, int replacementPostingLineNo,
        long? receiptLayerId = null,
        CancellationToken cancellationToken = default)
    {
        StockReplayFifoInsertionPlanner.ValidatePreparedPlan(plan);
        if (plan.PhysicalPlan.Candidate.SignedDelta > 0m)
            StockReplayFifoReceiptLayerBinding.Validate(plan, receiptLayerId);
        else if (receiptLayerId is not null)
            throw Invalid("An issue insertion has no projected receipt layer.");
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, plan, cancellationToken);
        return await AppendCoreAsync(context, plan.ItemCode, plan.Snapshot,
            plan.Projection, plan.PlanHash, "INSERT", permit, transferKey,
            reversalPostingLineNo, replacementPostingLineNo,
            receiptLayerId, cancellationToken);
    }

    public static async Task<IReadOnlyList<StockValuationFact>> AppendAsync(
        StockPostingContext context, StockReplayFifoRemovalPlan plan,
        StockReplayFifoWritePermit permit, StockReplayEventKey transferKey,
        int reversalPostingLineNo, int replacementPostingLineNo,
        CancellationToken cancellationToken = default)
    {
        StockReplayFifoRemovalPlanner.ValidatePreparedPlan(plan);
        var first = plan.PhysicalPlan.RemovedActiveEvent.Key.EffectiveAt.Date;
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, first, plan.PhysicalPlan.LaterEvents
                .Select(e => e.Key.EffectiveAt.Date).Append(first).Max(),
            cancellationToken);
        return await AppendCoreAsync(context, plan.ItemCode, plan.Snapshot,
            plan.Projection, plan.PlanHash, "REMOVE", permit, transferKey,
            reversalPostingLineNo, replacementPostingLineNo,
            null, cancellationToken);
    }

    private static async Task<IReadOnlyList<StockValuationFact>> AppendCoreAsync(
        StockPostingContext context, string itemCode,
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayFifoTailProjection projection, string planHash,
        string operation, StockReplayFifoWritePermit permit,
        StockReplayEventKey transferKey, int reversalPostingLineNo,
        int replacementPostingLineNo, long? receiptLayerId,
        CancellationToken cancellationToken)
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
            throw Invalid("FIFO transfer correction needs its prepared root lines.");
        var transfer = projection.Issues.SingleOrDefault(i =>
            i.Key == transferKey && !i.IsCandidate && !i.DepletesPool);
        var historyId = StockReplayFifoMiRootWriter.HistoryId(transferKey);
        if (transfer is null || transfer.PreviousConsumptions.Count == 0
            || transfer.Allocations.Count == 0
            || transfer.Quantity <= 0m
            || transfer.Quantity != StockLedgerPrecision.Quantity(
                transfer.PreviousConsumptions.Sum(c => c.Quantity))
            || transfer.Quantity != StockLedgerPrecision.Quantity(
                transfer.Allocations.Sum(a => a.Quantity))
            || transfer.Value != StockLedgerPrecision.Money(
                transfer.Allocations.Sum(a => a.Value)))
            throw Invalid("FIFO transfer has no complete projected allocation.");
        var oldShape = transfer.PreviousConsumptions.Select(c =>
                (c.LayerId, c.Quantity, c.Value))
            .OrderBy(x => x.LayerId).ThenBy(x => x.Quantity)
            .ThenBy(x => x.Value).ToArray();
        var newShape = transfer.Allocations.Select(a =>
                (a.LayerId, a.Quantity, a.Value))
            .OrderBy(x => x.LayerId).ThenBy(x => x.Quantity)
            .ThenBy(x => x.Value).ToArray();
        if (oldShape.SequenceEqual(newShape))
            return [];
        var db = context.Db;
        var history = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == historyId
                && h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode,
                cancellationToken);
        if (history is null || history.ICode != itemCode
            || history.TrxType != IvTrxTypes.StockTransfer
            || history.LedgerVersion != 2
            || history.LedgerEpochId != context.Epoch.Id
            || history.EntryRole != "ORIGINAL"
            || history.BatchStatus != "POSTED"
            || history.ValuationStatus != StockValuationStatuses.Valued
            || history.FromBalLocId is not > 0
            || history.ToBalLocId is not > 0
            || history.FrStdQty != transfer.Quantity
            || history.ToStdQty != transfer.Quantity
            || history.TrxDtTime != transferKey.EffectiveAt)
            throw Invalid($"FIFO history {historyId} is not an active transfer.");
        var active = (await StockReplayValuationGenerationResolver.ResolveAsync(
                db, context.CompanyCode, context.BranchCode,
                [history], cancellationToken))[historyId]
            .OrderBy(f => f.PostingLineNo).ThenBy(f => f.SplitOrdinal)
            .ToArray();
        var outbound = active.Where(f => f.MovementCode == "TRANSFER_OUT"
            && f.Direction == -1).ToArray();
        var inbound = active.Where(f => f.MovementCode == "TRANSFER_IN"
            && f.Direction == 1).ToArray();
        var previous = transfer.PreviousConsumptions;
        if (outbound.Length != previous.Count || inbound.Length != 1
            || active.Length != outbound.Length + 1
            || active.Select(f => (f.StockPostingId, f.ValuationVersion))
                .Distinct().Count() != 1)
            throw Invalid($"FIFO transfer {historyId} has an incomplete active generation.");
        if (active.Any(f => f.CostMethod != StockCostMethods.Fifo
                || f.ValuationSource != StockValuationSources.Fifo))
            throw Invalid($"FIFO transfer {historyId} has another cost method or source.");
        if (active.Any(f => f.EffectiveAt != transferKey.EffectiveAt
                || f.InventoryHistoryId != historyId))
            throw Invalid($"FIFO transfer {historyId} has a different business identity.");
        if (outbound.Any(f => f.TransactionCurrency is not null
                || f.TransactionCostAmount is not null
                || f.ExchangeRate is not null)
            || inbound[0].TransactionCurrency is not null
            || inbound[0].TransactionCostAmount is < 0m
            || inbound[0].TransactionCostAmount is > 0m
                && inbound[0].ExchangeRate != Rate(
                    inbound[0].CostAmount
                    / inbound[0].TransactionCostAmount.Value)
            || inbound[0].TransactionCostAmount is null or 0m
                && inbound[0].ExchangeRate is not null)
            throw Invalid($"FIFO transfer {historyId} has unsupported transaction cost evidence.");
        if (active.Any(f => f.BaseCostAmount != f.CostAmount))
            throw Invalid($"FIFO transfer {historyId} has mismatched base cost.");
        if (outbound.Sum(f => f.BaseQty) != transfer.Quantity
            || inbound[0].BaseQty != transfer.Quantity
            || outbound.Sum(f => f.CostAmount) != inbound[0].CostAmount
            || outbound.Any(f => !previous.Any(c =>
                c.IssueFactId == f.Id && c.SplitOrdinal == f.SplitOrdinal
                && c.Quantity == f.BaseQty && c.Value == f.CostAmount)))
            throw Invalid($"FIFO transfer {historyId} has mismatched active allocations.");
        var outboundIds = outbound.Select(f => f.Id).ToArray();
        var persisted = await db.StockFifoLayerConsumptions.AsNoTracking()
            .Where(c => outboundIds.Contains(c.IssueValuationFactId))
            .ToArrayAsync(cancellationToken);
        if (persisted.Length != previous.Count
            || persisted.Any(c => !previous.Any(p =>
                p.ConsumptionId == c.Id
                && p.IssueFactId == c.IssueValuationFactId
                && p.LayerId == c.FifoLayerId
                && p.Quantity == c.ConsumedQty
                && p.Value == c.ConsumedValue
                && p.SplitOrdinal == c.SplitOrdinal)))
            throw Invalid($"FIFO transfer {historyId} has changed consumption evidence.");
        var occupied = await db.StockValuationFacts.AsNoTracking()
            .AnyAsync(f => f.StockPostingId == context.Posting.Id
                && (f.PostingLineNo == reversalPostingLineNo
                    || f.PostingLineNo == replacementPostingLineNo),
                cancellationToken);
        if (occupied || db.ChangeTracker.Entries<StockValuationFact>()
            .Any(e => e.State == EntityState.Added
                && e.Entity.StockPostingId == context.Posting.Id
                && (e.Entity.PostingLineNo == reversalPostingLineNo
                    || e.Entity.PostingLineNo == replacementPostingLineNo)))
            throw Invalid("A FIFO transfer root line is already used.");
        var reversals = await StockReplayValuationReversalWriter.AppendAsync(
            context, historyId, active.Select((f, i) =>
                new StockReplayValuationReversalLine(f.Id,
                    reversalPostingLineNo, i)).ToArray(),
            cancellationToken);
        var reverseById = reversals.ToDictionary(f =>
            f.ReversesValuationFactId!.Value);
        var now = DateTime.UtcNow;
        foreach (var old in previous)
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
        var replacements = transfer.Allocations.Select((allocation, i) =>
            Replacement(outbound[0], context, replacementPostingLineNo,
                i, allocation.Quantity, allocation.Value,
                allocation.UnitCost, now)).ToList();
        replacements.Add(Replacement(inbound[0], context,
            replacementPostingLineNo, replacements.Count, transfer.Quantity,
            transfer.Value, StockLedgerPrecision.Money(
                transfer.Value / transfer.Quantity), now));
        var transactionAmount = inbound[0].TransactionCostAmount;
        replacements[^1].TransactionCostAmount = transactionAmount;
        replacements[^1].ExchangeRate = transactionAmount is > 0m
            ? Rate(transfer.Value / transactionAmount.Value) : null;
        db.StockValuationFacts.AddRange(replacements);
        for (var i = 0; i < transfer.Allocations.Count; i++)
        {
            var allocation = transfer.Allocations[i];
            db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                IssueValuationFact = replacements[i],
                FifoLayerId = StockReplayFifoReceiptLayerBinding.Map(
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

    private static StockValuationFact Replacement(StockValuationFact template,
        StockPostingContext context, int postingLineNo, int splitOrdinal,
        decimal quantity, decimal value, decimal unitCost, DateTime now) => new()
    {
        CompanyCode = template.CompanyCode,
        BranchCode = template.BranchCode,
        LedgerEpochId = template.LedgerEpochId,
        StockPostingId = context.Posting.Id,
        PostingLineNo = postingLineNo,
        SplitOrdinal = splitOrdinal,
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
        Direction = template.Direction,
        BaseQty = quantity,
        CostMethod = StockCostMethods.Fifo,
        UnitCost = unitCost,
        CostAmount = value,
        BaseCurrency = template.BaseCurrency,
        BaseCostAmount = value,
        ValuationSource = StockValuationSources.Fifo,
        ValuationStatus = StockValuationStatuses.Valued,
        ValuationVersion = checked(template.ValuationVersion + 1),
        InventoryHistoryId = template.InventoryHistoryId,
        FromBalLocId = template.FromBalLocId,
        ToBalLocId = template.ToBalLocId,
        OriginalValuationFactId = template.OriginalValuationFactId ?? template.Id,
        CreatedAtUtc = now,
        CreatedBy = context.UserId
    };

    private static decimal Rate(decimal value) =>
        decimal.Round(value, 8, MidpointRounding.AwayFromZero);

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
