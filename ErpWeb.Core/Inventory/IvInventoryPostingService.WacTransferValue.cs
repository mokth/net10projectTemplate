using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private static async Task<decimal> AppendWacTransferOriginalValueAsync(
        StockPostingContext root,
        StockReplayFifoTransferDraftAuthority authority,
        StockReplayWacResult projection, int historyId,
        CancellationToken cancellationToken)
    {
        root.EnsureUnsealed();
        var db = root.Db;
        var history = await db.IvTrxHistories.SingleAsync(h =>
            h.Id == historyId && h.CompanyCode == root.CompanyCode
            && h.BranchCode == root.BranchCode, cancellationToken);
        var detail = await db.IvTrxBatchDetails.SingleAsync(d =>
            d.Id == authority.DetailId, cancellationToken);
        if (root.CostMethod != StockCostMethods.MovingAverage
            || history.StockPostingId != root.Posting.Id
            || history.LedgerEpochId != root.Epoch.Id
            || history.LedgerVersion != 2
            || history.EntryRole != "ORIGINAL"
            || history.TrxType != IvTrxTypes.StockTransfer
            || history.BatchStatus != IvBatchStatuses.Posted
            || history.ValuationStatus is not null
            || history.ICode != authority.ItemCode
            || history.BatchNo != authority.BatchNo
            || history.FromBalLocId != authority.SourceBalLocId
            || history.ToBalLocId != authority.DestinationBalLocId
            || history.FrStdQty != authority.Quantity
            || history.ToStdQty != authority.Quantity
            || detail.BatchId != authority.BatchId
            || detail.DocumentRevision != authority.DocumentRevision
            || detail.ToBalLocId != authority.DestinationBalLocId
            || projection.HistoricalQuantityBeforeCandidate < authority.Quantity
            || projection.Candidate.SignedQuantity != 0m
            || projection.Candidate.SignedValue != 0m)
            throw InvalidWacTransfer("The posted WAC transfer differs from its historical projection.");
        if (await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.InventoryHistoryId == historyId
                || f.StockPostingId == root.Posting.Id
                    && f.PostingLineNo == 1, cancellationToken))
            throw InvalidWacTransfer("The WAC transfer already has valuation facts.");
        var baseCurrency = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == root.CompanyCode)
            .Select(c => c.CurrencyCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseCurrency))
            throw InvalidWacTransfer("The WAC transfer company has no base currency.");
        var amount = WacTransferAmount(authority.Quantity,
            projection.HistoricalQuantityBeforeCandidate,
            projection.HistoricalValueBeforeCandidate);
        var unitCost = StockLedgerPrecision.Money(amount / authority.Quantity);
        var now = DateTime.UtcNow;
        StockValuationFact Fact(int ordinal, int direction,
            string movement, string? warehouse, string? location) => new()
        {
            CompanyCode = root.CompanyCode,
            BranchCode = root.BranchCode,
            LedgerEpochId = root.Epoch.Id,
            StockPostingId = root.Posting.Id,
            PostingLineNo = 1,
            SplitOrdinal = ordinal,
            SourceLineId = $"{root.Posting.SourceDocumentType}:{root.Posting.SourceDocumentId}:{history.TrxLineNo}:1:{ordinal}",
            SourceDocumentType = root.Posting.SourceDocumentType,
            SourceDocumentId = root.Posting.SourceDocumentId,
            SourceDocumentNo = root.Posting.SourceDocumentNo,
            SourceDocumentLine = history.TrxLineNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            EffectiveAt = root.Posting.EffectiveAt,
            BusinessDate = root.Posting.BusinessDate,
            PeriodKey = root.Posting.PeriodKey,
            ItemCode = authority.ItemCode,
            WarehouseCode = BlankWacTransferField(warehouse),
            LocationCode = BlankWacTransferField(location),
            ItemStatus = BlankWacTransferField(history.IStatus),
            BaseUom = authority.BaseUom,
            MovementCode = movement,
            Direction = direction,
            BaseQty = authority.Quantity,
            CostMethod = StockCostMethods.MovingAverage,
            UnitCost = unitCost,
            CostAmount = amount,
            BaseCurrency = baseCurrency,
            BaseCostAmount = amount,
            ValuationSource = StockValuationSources.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = 1,
            InventoryHistoryId = historyId,
            FromBalLocId = authority.SourceBalLocId,
            ToBalLocId = authority.DestinationBalLocId,
            CreatedAtUtc = now,
            CreatedBy = root.UserId
        };
        var outbound = Fact(0, -1, "TRANSFER_OUT",
            history.FrWarehouse, history.FrLocation);
        var inbound = Fact(1, 1, "TRANSFER_IN",
            history.ToWarehouse, history.ToLocation);
        inbound.TransactionCostAmount = 0m;
        db.StockValuationFacts.AddRange(outbound, inbound);
        history.ValuationStatus = StockValuationStatuses.Valued;
        return amount;
    }

    private static async Task VerifyPostedWacTransferAsync(
        StockPostingContext root, StockReplayPhysicalPlan source,
        StockReplayPhysicalPlan destination,
        StockReplayFifoTransferDraftAuthority authority,
        StockReplayWacFinancialTail before,
        int historyId, decimal amount,
        CancellationToken cancellationToken)
    {
        if (root.Posting.SealedAtUtc is null)
            throw InvalidWacTransfer("The WAC transfer root was not sealed.");
        var db = root.Db;
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == root.CompanyCode
                && b.BranchCode == root.BranchCode
                && (b.Id == source.BalLocId
                    || b.Id == destination.BalLocId))
            .ToArrayAsync(cancellationToken);
        var state = await db.StockCostStates.AsNoTracking().SingleAsync(s =>
            s.CompanyCode == root.CompanyCode
            && s.BranchCode == root.BranchCode
            && s.ItemCode == authority.ItemCode, cancellationToken);
        var history = await db.IvTrxHistories.AsNoTracking().SingleAsync(h =>
            h.Id == historyId, cancellationToken);
        var facts = (await StockReplayValuationGenerationResolver.ResolveAsync(
            db, root.CompanyCode, root.BranchCode,
            [history], cancellationToken))[historyId];
        if (piles.Length != 2
            || piles.Single(b => b.Id == source.BalLocId).StdQty
                != source.Projection.FinalQuantity
            || piles.Single(b => b.Id == destination.BalLocId).StdQty
                != destination.Projection.FinalQuantity
            || state.OnHandBaseQty != before.CurrentQuantity
            || state.InventoryValue != before.CurrentValue
            || facts.Count != 2
            || facts.Any(f => f.StockPostingId != root.Posting.Id
                || f.CostMethod != StockCostMethods.MovingAverage)
            || facts.Where(f => f.Direction < 0).Sum(f => f.CostAmount)
                != amount
            || facts.Where(f => f.Direction > 0).Sum(f => f.CostAmount)
                != amount)
            throw InvalidWacTransfer("The sealed WAC transfer differs from its paired projection.");
    }

    private static string? BlankWacTransferField(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
