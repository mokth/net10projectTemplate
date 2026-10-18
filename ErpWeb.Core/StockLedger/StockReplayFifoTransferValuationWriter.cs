using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Values the original, physically posted transfer. Its OUT splits have
/// nondepleting FIFO consumption evidence; its IN fact restores the same
/// branch/item value without creating a new layer.
/// </summary>
public static class StockReplayFifoTransferValuationWriter
{
    public static async Task<IReadOnlyList<StockValuationFact>> AppendAsync(
        StockPostingContext context,
        StockReplayFifoTransferInsertionPlan plan,
        int historyId, int postingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        StockReplayFifoTransferInsertionPlanner.ValidatePreparedPlan(plan);
        var authority = plan.DraftAuthority;
        if (authority is null || historyId <= 0 || postingLineNo <= 0
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != IvTrxTypes.StockTransfer
            || context.Posting.SourceDocumentId
                != authority.BatchId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.SourceDocumentNo
                != authority.BatchNo.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.DocumentRevision != authority.DocumentRevision
            || context.Posting.PostingRole != "PRIMARY")
            throw Invalid("FIFO transfer valuation needs its prepared posted draft.");
        var db = context.Db;
        if (await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.CompanyCode == context.CompanyCode
                && f.BranchCode == context.BranchCode
                && (f.InventoryHistoryId == historyId
                    || f.StockPostingId == context.Posting.Id
                    && f.PostingLineNo == postingLineNo),
                cancellationToken)
            || db.ChangeTracker.Entries<StockValuationFact>().Any(e =>
                e.State == EntityState.Added
                && (e.Entity.InventoryHistoryId == historyId
                    || e.Entity.StockPostingId == context.Posting.Id
                    && e.Entity.PostingLineNo == postingLineNo)))
            throw Invalid("The FIFO transfer already has valuation or an occupied root line.");
        var history = await db.IvTrxHistories.SingleOrDefaultAsync(h =>
            h.Id == historyId
            && h.CompanyCode == context.CompanyCode
            && h.BranchCode == context.BranchCode, cancellationToken);
        var detail = await db.IvTrxBatchDetails.SingleOrDefaultAsync(d =>
            d.Id == authority.DetailId, cancellationToken);
        if (history is null || detail is null
            || db.Entry(history).State != EntityState.Unchanged
            || db.Entry(detail).State != EntityState.Unchanged
            || history.StockPostingId != context.Posting.Id
            || history.LedgerEpochId != context.Epoch.Id
            || history.LedgerVersion != 2
            || history.EntryRole != "ORIGINAL"
            || history.TrxType != IvTrxTypes.StockTransfer
            || history.BatchStatus != IvBatchStatuses.Posted
            || history.ValuationStatus is not null
            || history.ICode != plan.ItemCode
            || history.BatchNo != authority.BatchNo
            || history.FromBalLocId != authority.SourceBalLocId
            || history.ToBalLocId != authority.DestinationBalLocId
            || history.FrStdQty != authority.Quantity
            || history.ToStdQty != authority.Quantity
            || history.FrStdUom != authority.BaseUom
            || history.ToStdUom != authority.BaseUom
            || history.TrxDtTime != authority.EffectiveAt
            || detail.BatchId != authority.BatchId
            || detail.DocumentRevision != authority.DocumentRevision
            || detail.FromBalLocId != authority.SourceBalLocId
            || detail.ToBalLocId != authority.DestinationBalLocId
            || detail.FrStdQty != authority.Quantity
            || detail.ToStdQty != authority.Quantity
            || !string.IsNullOrWhiteSpace(detail.Currency))
            throw Invalid("The FIFO transfer history or detail changed before valuation.");
        var boundKey = plan.Source.Candidate.Key with
        {
            LineIdentity = $"H{historyId:D10}"
        };
        var bound = StockReplayFifoTailProjector.InsertTransfer(
            plan.Snapshot, boundKey, authority.Quantity);
        var planned = plan.Projection.Issues.Single(i => i.IsCandidate);
        var candidate = bound.Issues.Single(i => i.IsCandidate);
        if (candidate.DepletesPool || candidate.Quantity != authority.Quantity
            || candidate.Value != planned.Value
            || !candidate.Allocations.Select(a =>
                    (a.LayerId, a.Quantity, a.Value))
                .SequenceEqual(planned.Allocations.Select(a =>
                    (a.LayerId, a.Quantity, a.Value)))
            || bound.FinalQuantity != plan.Projection.FinalQuantity
            || bound.FinalValue != plan.Projection.FinalValue)
            throw Invalid("The bound transfer differs from the prepared FIFO allocation.");
        var layerIds = candidate.Allocations.Select(a => a.LayerId)
            .Distinct().ToArray();
        var knownLayers = await db.StockFifoLayers.AsNoTracking()
            .Where(l => l.CompanyCode == context.CompanyCode
                && l.BranchCode == context.BranchCode
                && l.ItemCode == plan.ItemCode
                && layerIds.Contains(l.Id))
            .Select(l => l.Id).ToArrayAsync(cancellationToken);
        if (candidate.Allocations.Count == 0
            || knownLayers.Length != layerIds.Length)
            throw Invalid("The FIFO transfer references a missing receipt layer.");
        var baseCurrency = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == context.CompanyCode)
            .Select(c => c.CurrencyCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseCurrency))
            throw Invalid("The FIFO transfer company has no base currency.");
        var now = DateTime.UtcNow;
        var sourceLine = history.TrxLineNo.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var facts = candidate.Allocations.Select((allocation, ordinal) =>
            Fact(context, history, postingLineNo, ordinal, sourceLine,
                "TRANSFER_OUT", -1, allocation.Quantity,
                allocation.Value, allocation.UnitCost,
                history.FrWarehouse, history.FrLocation,
                history.FrStdUom!, baseCurrency, now)).ToList();
        var inbound = Fact(context, history, postingLineNo, facts.Count,
            sourceLine, "TRANSFER_IN", 1, authority.Quantity,
            candidate.Value, StockLedgerPrecision.Money(
                candidate.Value / authority.Quantity),
            history.ToWarehouse, history.ToLocation,
            history.ToStdUom!, baseCurrency, now);
        var transactionUnit = detail.CostPrice ?? history.CostPrice
            ?? detail.UnitPrice ?? history.UnitPrice;
        inbound.TransactionCostAmount = transactionUnit is decimal unit
            ? StockLedgerPrecision.Money(authority.Quantity * unit) : null;
        inbound.ExchangeRate = inbound.TransactionCostAmount is > 0m
            ? decimal.Round(candidate.Value /
                inbound.TransactionCostAmount.Value, 8,
                MidpointRounding.AwayFromZero) : null;
        facts.Add(inbound);
        db.StockValuationFacts.AddRange(facts);
        for (var i = 0; i < candidate.Allocations.Count; i++)
        {
            var allocation = candidate.Allocations[i];
            db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                IssueValuationFact = facts[i],
                FifoLayerId = allocation.LayerId,
                ConsumedQty = allocation.Quantity,
                ConsumedValue = allocation.Value,
                SplitOrdinal = i,
                CreatedAtUtc = now,
                CreatedBy = context.UserId
            });
        }
        history.ValuationStatus = StockValuationStatuses.Valued;
        return facts;
    }

    private static StockValuationFact Fact(StockPostingContext context,
        ErpWeb.Model.Entities.Inventory.IvTrxHistory history,
        int postingLineNo, int ordinal, string sourceLine,
        string movement, int direction, decimal quantity, decimal value,
        decimal unitCost, string? warehouse, string? location,
        string baseUom, string baseCurrency, DateTime now) => new()
    {
        CompanyCode = context.CompanyCode,
        BranchCode = context.BranchCode,
        LedgerEpochId = context.Epoch.Id,
        StockPostingId = context.Posting.Id,
        PostingLineNo = postingLineNo,
        SplitOrdinal = ordinal,
        SourceLineId = $"{context.Posting.SourceDocumentType}:{context.Posting.SourceDocumentId}:{sourceLine}:{postingLineNo}:{ordinal}",
        SourceDocumentType = context.Posting.SourceDocumentType,
        SourceDocumentId = context.Posting.SourceDocumentId,
        SourceDocumentNo = context.Posting.SourceDocumentNo,
        SourceDocumentLine = sourceLine,
        EffectiveAt = context.Posting.EffectiveAt,
        BusinessDate = context.Posting.BusinessDate,
        PeriodKey = context.Posting.PeriodKey,
        ItemCode = history.ICode!,
        WarehouseCode = BlankToNull(warehouse),
        LocationCode = BlankToNull(location),
        ItemStatus = BlankToNull(history.IStatus),
        BaseUom = baseUom,
        MovementCode = movement,
        Direction = direction,
        BaseQty = quantity,
        CostMethod = StockCostMethods.Fifo,
        UnitCost = unitCost,
        CostAmount = value,
        BaseCurrency = baseCurrency,
        BaseCostAmount = value,
        ValuationSource = StockValuationSources.Fifo,
        ValuationStatus = StockValuationStatuses.Valued,
        ValuationVersion = 1,
        InventoryHistoryId = history.Id,
        FromBalLocId = history.FromBalLocId,
        ToBalLocId = history.ToBalLocId,
        CreatedAtUtc = now,
        CreatedBy = context.UserId
    };

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
