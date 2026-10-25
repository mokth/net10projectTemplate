using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayWacCountSheetRemovalItem(
    string ItemCode,
    IReadOnlyList<int> CountLineIds,
    IReadOnlyList<StockReplayWacEvent> RemovedEvents,
    StockReplayWacFinancialTail Tail,
    decimal FinalQuantity,
    decimal FinalValue,
    string PlanHash,
    StockReplayWacRemovalResult? Repricing = null);

public sealed record StockReplayWacCountSheetRemovalPlan(
    StockPostingCommand Command,
    StockReplayCountSheetRemovalPlan Sheet,
    IReadOnlyList<StockReplayWacCountSheetRemovalItem> Items,
    string PlanHash);

/// <summary>
/// Projects removal of every count line from its item's shared WAC pool.
/// Later Inventory receipts retain their fixed approved value. Counted piles
/// of one item share a projection that reprices later Inventory issues and
/// verifies paired, unchanged Inventory transfers.
/// </summary>
public static class StockReplayWacCountSheetRemovalPlanner
{
    public static async Task<StockReplayWacCountSheetRemovalPlan> BuildAsync(
        AppDbContext db, string companyCode, string branchCode, int countId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var sheet = await StockReplayCountSheetRemovalPlanner.BuildAsync(
            db, companyCode, branchCode, countId, cancellationToken);
        var method = await new StockCostMethodResolver().ResolveAsync(db,
            companyCode, branchCode, sheet.EffectiveAt, cancellationToken);
        if (method != StockCostMethods.MovingAverage)
            throw Invalid("The count sheet has no WAC cost authority.");
        var items = await ReadItemsAsync(db, sheet, cancellationToken);
        var command = await StockCountRollbackCommandFactory.CreateAsync(
            db, sheet, cancellationToken);
        var plan = new StockReplayWacCountSheetRemovalPlan(command, sheet,
            items, string.Empty);
        return plan with { PlanHash = Hash(plan) };
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context,
        StockReplayWacCountSheetRemovalPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        if (context.Posting.RequestId != plan.Command.RequestId
            || context.Posting.RequestFingerprint
                != plan.Command.Evidence.RequestFingerprint
            || !StringComparer.Ordinal.Equals(plan.PlanHash, Hash(plan)))
            throw Stale("The WAC count removal plan differs from its root.");
        await StockReplayCountSheetRemovalPlanner.ValidateWithinRootAsync(
            context, plan.Sheet, cancellationToken);
        var currentCommand = await StockCountRollbackCommandFactory.CreateAsync(
            context.Db, plan.Sheet, cancellationToken);
        if (currentCommand.RequestId != plan.Command.RequestId
            || currentCommand.Evidence.RequestFingerprint
                != plan.Command.Evidence.RequestFingerprint)
            throw Stale("The WAC count source snapshot changed.");
        var method = await new StockCostMethodResolver().ResolveAsync(
            context.Db, context.CompanyCode, context.BranchCode,
            plan.Sheet.EffectiveAt, cancellationToken);
        if (method != StockCostMethods.MovingAverage)
            throw Stale("The count's event-date costing policy changed.");
        IReadOnlyList<StockReplayWacCountSheetRemovalItem> current;
        try
        {
            current = await ReadItemsAsync(context.Db, plan.Sheet,
                cancellationToken);
        }
        catch (StockLedgerException)
        {
            throw Stale("The WAC count removal dependencies changed.");
        }
        var refreshed = plan with { Items = current };
        if (!StringComparer.Ordinal.Equals(plan.PlanHash, Hash(refreshed)))
            throw Stale("The WAC count removal dependencies changed.");
    }

    private static async Task<IReadOnlyList<StockReplayWacCountSheetRemovalItem>>
        ReadItemsAsync(AppDbContext db, StockReplayCountSheetRemovalPlan sheet,
            CancellationToken cancellationToken)
    {
        StockReplayCountSheetRemovalPlanner.ValidatePreparedPlan(sheet);
        var pileIds = sheet.Lines.Select(l => l.BalLocId).ToArray();
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == sheet.CompanyCode
                && b.BranchCode == sheet.BranchCode
                && pileIds.Contains(b.Id))
            .Select(b => new { b.Id, b.ICode })
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        if (piles.Count != sheet.Lines.Count
            || piles.Values.Any(p => string.IsNullOrWhiteSpace(p.ICode)))
            throw Invalid("The counted WAC piles changed items.");
        var items = new List<StockReplayWacCountSheetRemovalItem>();
        foreach (var group in sheet.Lines.GroupBy(l => piles[l.BalLocId].ICode)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var lines = group.OrderBy(l => l.Physical.RemovedActiveEvent.Key)
                .ToArray();
            var firstKey = lines[0].Physical.RemovedActiveEvent.Key;
            if (firstKey.EffectiveAt == DateTime.MinValue
                || lines.Any(l => l.Physical.RemovedActiveEvent.Key.EffectiveAt
                        != firstKey.EffectiveAt
                    || l.Physical.RemovedActiveEvent.Key.OriginalPostingSequence
                        != firstKey.OriginalPostingSequence))
                throw Invalid("The count item needs descendant physical replay.");
            var before = new StockReplayEventKey(firstKey.EffectiveAt,
                StockReplayEventKey.OrdinaryClass, long.MaxValue,
                "\uffff", int.MaxValue);
            var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(
                db, sheet.CompanyCode, sheet.BranchCode, group.Key,
                before, cancellationToken);
            var targetIds = lines.Select(l =>
                l.Physical.RemovedActiveEvent.EventId).ToHashSet();
            var removed = tail.LaterEvents.Where(e =>
                    targetIds.Contains(e.EventId))
                .OrderBy(e => e.Key).ToArray();
            var later = tail.LaterEvents.Where(e =>
                    !targetIds.Contains(e.EventId))
                .OrderBy(e => e.Key).ToArray();
            var physicalEvents = tail.PhysicalByPile.Values
                .SelectMany(e => e).ToArray();
            var laterIds = later.Where(e => e.EventId is > 0 and <= int.MaxValue)
                .Select(e => (int)e.EventId).ToArray();
            var laterHistories = await db.IvTrxHistories.AsNoTracking()
                .Where(h => laterIds.Contains(h.Id))
                .ToArrayAsync(cancellationToken);
            var activeFacts = await StockReplayValuationGenerationResolver
                .ResolveAsync(db, sheet.CompanyCode, sheet.BranchCode,
                    laterHistories, cancellationToken);
            var ownerIds = laterHistories.Where(h => h.StockPostingId.HasValue)
                .Select(h => h.StockPostingId!.Value)
                .Concat(activeFacts.Values.SelectMany(f => f)
                    .Select(f => f.StockPostingId))
                .Distinct().ToArray();
            var owners = await db.StockPostings.AsNoTracking()
                .Where(p => ownerIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, cancellationToken);
            var correctedIds = laterHistories.Where(h =>
                    h.TrxType == IvTrxTypes.MiscellaneousIssue
                    && activeFacts.TryGetValue(h.Id, out var facts)
                    && facts.Any(f => f.StockPostingId != h.StockPostingId))
                .Select(h => h.Id).ToArray();
            var originRows = await db.StockValuationFacts.AsNoTracking()
                .Where(f => f.InventoryHistoryId.HasValue
                    && correctedIds.Contains(f.InventoryHistoryId.Value)
                    && f.OriginalValuationFactId == null
                    && f.ReversesValuationFactId == null)
                .ToArrayAsync(cancellationToken);
            var originsByHistory = originRows.GroupBy(f =>
                    f.InventoryHistoryId!.Value)
                .ToDictionary(g => g.Key,
                    g => (IReadOnlyList<StockValuationFact>)g.ToArray());
            var hasRepriceable = later.Any(e => e.OldSignedQuantity <= 0m);
            var transferNumbers = laterHistories.Where(h =>
                    h.TrxType == IvTrxTypes.StockTransfer)
                .Select(h => h.BatchNo).Distinct().ToArray();
            var transferBatches = await db.IvTrxBatches.AsNoTracking()
                .Where(b => b.CompanyCode == sheet.CompanyCode
                    && b.BranchCode == sheet.BranchCode
                    && transferNumbers.Contains(b.BatchNo))
                .ToArrayAsync(cancellationToken);
            if (targetIds.Count != lines.Length
                || removed.Length != lines.Length
                || removed.Any(e => e.AbsoluteCountQty is null)
                || later.Any(e => e.EventId is <= 0 or > int.MaxValue
                    || e.Key.CompareTo(
                        lines[^1].Physical.RemovedActiveEvent.Key) <= 0
                    || e.ReplaySignedQuantity != e.OldSignedQuantity
                    || (e.OldSignedQuantity >= 0m
                        ? e.FixedSignedValue != e.OldSignedValue
                        : e.FixedSignedValue is not null)
                    || e.AbsoluteCountQty is not null)
                || physicalEvents.Select(e => e.EventId).Distinct().Count()
                    != tail.LaterEvents.Count
                || !physicalEvents.Select(e => e.EventId).Order()
                    .Distinct().SequenceEqual(tail.LaterEvents
                        .Select(e => e.EventId).Order())
                || laterHistories.Length != later.Length
                || transferBatches.Length != transferNumbers.Length
                || laterHistories.Any(h =>
                    h.TrxType is not (IvTrxTypes.MiscellaneousReceipt
                        or IvTrxTypes.MiscellaneousIssue
                        or IvTrxTypes.StockTransfer)
                    || h.StockPostingId is not long id
                    || !owners.TryGetValue(id, out var owner)
                    || owner.SealedAtUtc is null
                    || owner.CompanyCode != sheet.CompanyCode
                    || owner.BranchCode != sheet.BranchCode
                    || !(StockReplayInventoryMiscPostingOwner.Matches(owner,
                            h.TrxType)
                        || h.TrxType == IvTrxTypes.StockTransfer
                            && transferBatches.Any(b => b.BatchNo == h.BatchNo
                                && b.TrxType == IvTrxTypes.StockTransfer
                                && b.BatchStatus == IvBatchStatuses.Posted
                                && owner.SourceModule == "INVENTORY"
                                && owner.SourceDocumentType == h.TrxType
                                && owner.CommandType == $"IV_POST_{h.TrxType}"
                                && owner.PostingRole == "PRIMARY"
                                && owner.SourceDocumentId == b.Id.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture)
                                && owner.SourceDocumentNo == h.BatchNo.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture)))
                    || !activeFacts.TryGetValue(h.Id, out var facts)
                    || facts.Count == 0 || facts.Count > 1000
                    || h.TrxType == IvTrxTypes.StockTransfer
                        && facts.Any(f => f.StockPostingId != h.StockPostingId)
                    || h.TrxType == IvTrxTypes.MiscellaneousReceipt
                        && facts.Count != 1
                    || !HasValidLaterFactLineage(h, facts,
                        originsByHistory.GetValueOrDefault(h.Id), owners,
                        sheet.CompanyCode, sheet.BranchCode))
                || hasRepriceable && later.Any(e => e.OldSignedQuantity < 0m
                    && !laterHistories.Any(h => h.Id == e.EventId
                        && h.TrxType == IvTrxTypes.MiscellaneousIssue)
                    || e.OldSignedQuantity == 0m
                    && !laterHistories.Any(h => h.Id == e.EventId
                        && h.TrxType == IvTrxTypes.StockTransfer)))
                throw Invalid("The count item needs later financial replay.");
            var eventById = removed.ToDictionary(e => e.EventId);
            if (lines.Any(l => !eventById.TryGetValue(
                        l.Physical.RemovedActiveEvent.EventId, out var e)
                    || e.Key != l.Physical.RemovedActiveEvent.Key
                    || e.OldSignedQuantity
                        != l.Physical.RemovedActiveEvent.SignedDelta
                    || e.AbsoluteCountQty != l.PhysicalQty
                    || !tail.CurrentQuantityByPile.TryGetValue(
                        l.BalLocId, out var current)
                    || current != l.Physical.CurrentQuantity
                    || l.Physical.Projection.FinalQuantity
                        != IvQty.Round(current - e.OldSignedQuantity)))
                throw Invalid("The count's WAC and physical removals disagree.");
            StockReplayWacRemovalResult? repricing = null;
            if (hasRepriceable)
            {
                repricing = StockReplayWacTailProjector
                    .SimulateCountSheetRemoval(tail,
                        lines.Select(l => l.Physical).ToArray(), removed);
                StockReplayWacTailRepricingWriter
                    .ValidateProjectedCountSheetRemovalTail(later,
                        repricing.LaterSteps);
            }
            var finalQuantity = repricing?.FinalQuantity
                ?? IvQty.Round(tail.CurrentQuantity
                    - removed.Sum(e => e.OldSignedQuantity));
            var finalValue = repricing?.FinalValue
                ?? StockLedgerPrecision.Money(tail.CurrentValue
                    - removed.Sum(e => e.OldSignedValue));
            if (finalQuantity < 0m || finalValue < 0m
                || finalQuantity == 0m && finalValue != 0m
                || finalQuantity != IvQty.Round(
                    tail.CurrentQuantityByPile.Sum(p => p.Value)
                    - lines.Sum(l =>
                        l.Physical.RemovedActiveEvent.SignedDelta)))
                throw Invalid("The count removal does not reconcile its WAC pool.");
            var lineIds = lines.Select(l => l.CountLineId).ToArray();
            var hash = StockPostingFingerprint.Create(new
            {
                group.Key, sheet.PlanHash, lineIds,
                physical = lines.Select(l => l.Physical.PlanHash).ToArray(),
                removed, later,
                ownership = laterHistories.OrderBy(h => h.Id).Select(h => new
                {
                    h.Id, h.TrxType, h.StockPostingId,
                    Owner = PostingHash(owners[h.StockPostingId!.Value]),
                    ActiveOwner = PostingHash(owners[
                        activeFacts[h.Id][0].StockPostingId]),
                    Origins = originsByHistory.GetValueOrDefault(h.Id)?
                        .OrderBy(f => f.PostingLineNo)
                        .ThenBy(f => f.SplitOrdinal)
                        .Select(f => new
                        {
                            f.Id, f.StockPostingId, f.PostingLineNo,
                            f.SplitOrdinal, f.InventoryHistoryId,
                            f.SourceLineId, f.Direction, f.BaseQty,
                            f.UnitCost, f.CostAmount, f.BaseCostAmount,
                            f.ValuationVersion, f.OriginalValuationFactId,
                            f.ReversesValuationFactId
                        }).ToArray(),
                    Facts = activeFacts[h.Id].OrderBy(f => f.PostingLineNo)
                        .ThenBy(f => f.SplitOrdinal)
                        .Select(f => new
                        {
                            f.Id, f.CompanyCode, f.BranchCode,
                            f.LedgerEpochId, f.StockPostingId,
                            f.PostingLineNo, f.SplitOrdinal,
                            f.SourceLineId, f.SourceDocumentType,
                            f.SourceDocumentId, f.SourceDocumentNo,
                            f.SourceDocumentLine, f.EffectiveAt,
                            f.BusinessDate, f.PeriodKey, f.ItemCode,
                            f.WarehouseCode, f.LocationCode, f.LotId,
                            f.LotNo, f.ItemStatus, f.BaseUom,
                            f.MovementCode, f.Direction, f.BaseQty,
                            f.CostMethod, f.UnitCost, f.CostAmount,
                            f.TransactionCurrency, f.TransactionCostAmount,
                            f.ExchangeRate, f.BaseCurrency,
                            f.BaseCostAmount, f.ValuationSource,
                            f.ValuationStatus, f.ValuationVersion,
                            f.InventoryHistoryId, f.FromBalLocId,
                            f.ToBalLocId, f.ProductionMovementId,
                            f.WorkOrderId, f.WorkOrderOperationId,
                            f.ProductionPostingLinkId,
                            f.OriginalValuationFactId,
                            f.ReversesValuationFactId,
                            f.CreatedAtUtc, f.CreatedBy
                        }).ToArray()
                }).ToArray(),
                tail.CurrentQuantity, tail.CurrentValue,
                piles = tail.CurrentQuantityByPile.OrderBy(p => p.Key)
                    .Select(p => new
                    {
                        p.Key, p.Value,
                        Events = tail.PhysicalByPile[p.Key]
                    }).ToArray(),
                finalQuantity, finalValue,
                repricing
            }, new { }).RequestFingerprint;
            items.Add(new(group.Key, lineIds, removed, tail,
                finalQuantity, finalValue, hash, repricing));
        }
        return items;
    }

    private static string Hash(StockReplayWacCountSheetRemovalPlan plan) =>
        StockPostingFingerprint.Create(new
        {
            plan.Sheet.PlanHash,
            plan.Command.RequestId,
            plan.Command.Evidence.RequestFingerprint,
            items = plan.Items.Select(i => new
            {
                i.ItemCode, i.CountLineIds, i.PlanHash,
                i.RemovedEvents, i.Tail.CurrentQuantity,
                i.Tail.CurrentValue, i.Tail.LaterEvents,
                piles = i.Tail.CurrentQuantityByPile.OrderBy(p => p.Key)
                    .ToArray(),
                physical = i.Tail.PhysicalByPile.OrderBy(p => p.Key)
                    .Select(p => new { p.Key, p.Value }).ToArray(),
                i.FinalQuantity, i.FinalValue, i.Repricing
            }).ToArray()
        }, new { }).RequestFingerprint;

    private static string PostingHash(StockPosting p) =>
        StockPostingFingerprint.Create(new
        {
            p.Id, p.CompanyCode, p.BranchCode, p.LedgerEpochId,
            p.PostingSequence, p.RequestId, p.CommandType,
            p.RequestFingerprint, p.SourceModule, p.SourceDocumentType,
            p.SourceDocumentId, p.SourceDocumentNo, p.DocumentRevision,
            p.PostingRole, p.SourceSnapshotJson, p.SourceSnapshotHash,
            p.SourceSnapshotSchemaVersion, p.EffectiveAt,
            p.BusinessDate, p.PeriodKey, p.ProductionPostingLinkId,
            p.ReversesPostingId, p.ReasonCode, p.ReasonText,
            p.PostedAtUtc, p.PostedBy, p.SealedAtUtc
        }, new { }).RequestFingerprint;

    private static bool HasValidLaterFactLineage(
        IvTrxHistory history,
        IReadOnlyList<StockValuationFact> active,
        IReadOnlyList<StockValuationFact>? origins,
        IReadOnlyDictionary<long, StockPosting> postings,
        string companyCode, string branchCode)
    {
        if (history.StockPostingId is not long originalPostingId
            || active.Select(f => f.StockPostingId).Distinct().Count() != 1)
            return false;
        var activePostingId = active[0].StockPostingId;
        if (!postings.TryGetValue(activePostingId, out var activePosting)
            || activePosting.CompanyCode != companyCode
            || activePosting.BranchCode != branchCode
            || activePosting.LedgerEpochId != history.LedgerEpochId
            || activePosting.SealedAtUtc is null)
            return false;
        if (activePostingId == originalPostingId)
            return active.All(f => f.OriginalValuationFactId is null)
                && origins is null;
        if (history.TrxType != IvTrxTypes.MiscellaneousIssue
            || origins is null || origins.Count != active.Count
            || !postings.TryGetValue(originalPostingId,
                out var originalPosting)
            || activePosting.PostingSequence
                <= originalPosting.PostingSequence
            || origins.Select(f => f.Id).Distinct().Count() != origins.Count
            || active.Select(f => f.OriginalValuationFactId)
                .Distinct().Count() != active.Count)
            return false;
        var byId = origins.ToDictionary(f => f.Id);
        return origins.All(f => f.StockPostingId == originalPostingId
                && f.OriginalValuationFactId is null
                && f.ReversesValuationFactId is null
                && f.InventoryHistoryId == history.Id)
            && active.All(f => f.OriginalValuationFactId is long originId
                && byId.TryGetValue(originId, out var origin)
                && SameIssueIdentity(f, origin)
                && f.ValuationVersion > origin.ValuationVersion);
    }

    private static bool SameIssueIdentity(
        StockValuationFact active, StockValuationFact origin) =>
        active.CompanyCode == origin.CompanyCode
        && active.BranchCode == origin.BranchCode
        && active.LedgerEpochId == origin.LedgerEpochId
        && active.SourceLineId == origin.SourceLineId
        && active.SourceDocumentType == origin.SourceDocumentType
        && active.SourceDocumentId == origin.SourceDocumentId
        && active.SourceDocumentNo == origin.SourceDocumentNo
        && active.SourceDocumentLine == origin.SourceDocumentLine
        && active.EffectiveAt == origin.EffectiveAt
        && active.BusinessDate == origin.BusinessDate
        && active.PeriodKey == origin.PeriodKey
        && active.ItemCode == origin.ItemCode
        && active.WarehouseCode == origin.WarehouseCode
        && active.LocationCode == origin.LocationCode
        && active.LotId == origin.LotId
        && active.LotNo == origin.LotNo
        && active.ItemStatus == origin.ItemStatus
        && active.BaseUom == origin.BaseUom
        && active.MovementCode == origin.MovementCode
        && active.Direction == origin.Direction
        && active.BaseQty == origin.BaseQty
        && active.CostMethod == origin.CostMethod
        && active.BaseCurrency == origin.BaseCurrency
        && active.ValuationSource == origin.ValuationSource
        && active.InventoryHistoryId == origin.InventoryHistoryId
        && active.FromBalLocId == origin.FromBalLocId
        && active.ToBalLocId == origin.ToBalLocId
        && active.ProductionMovementId == origin.ProductionMovementId
        && active.WorkOrderId == origin.WorkOrderId
        && active.WorkOrderOperationId == origin.WorkOrderOperationId
        && active.ProductionPostingLinkId == origin.ProductionPostingLinkId;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
