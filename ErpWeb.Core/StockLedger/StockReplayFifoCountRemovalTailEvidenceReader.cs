using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Proves that the active ordinary FIFO tail of a count is owned by supported
/// Inventory MR or MI/Scrap histories, and that every counted-pile movement
/// matches exactly one such financial event. Transfers and other descendants
/// need separate rollback adapters.
/// </summary>
internal static class StockReplayFifoCountRemovalTailEvidenceReader
{
    private sealed record Movement(int PileId, StockReplayPhysicalEvent Event);

    public static async Task<string> ReadAsync(AppDbContext db,
        StockReplayCountSheetRemovalPlan sheet, string itemCode,
        IReadOnlyList<StockReplayCountSheetRemovalLine> lines,
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayFifoCountSheetRemovalProjection? projection,
        CancellationToken cancellationToken)
    {
        var physical = lines.SelectMany(l => l.Physical.LaterEvents
                .Select(e => new Movement(l.BalLocId, e)))
            .OrderBy(e => e.PileId).ThenBy(e => e.Event.Key).ToArray();
        if (projection is null)
        {
            if (physical.Length != 0)
                throw Invalid("A zero FIFO count item has unsupported later pile movement.");
            return Fingerprint(new { sheet.PlanHash, itemCode,
                snapshot.SnapshotHash, EmptyTail = true });
        }
        var receipts = projection.BeforeFirstCount.LaterReceipts
            .Where(l => l.ReceiptKey?.ClassPriority
                == StockReplayEventKey.OrdinaryClass).ToArray();
        var issueGroups = projection.BeforeFirstCount.LaterConsumptions
            .Where(c => c.IssueKey.ClassPriority
                == StockReplayEventKey.OrdinaryClass)
            .GroupBy(c => c.IssueKey)
            .ToDictionary(g => g.Key, g => g.ToArray());
        if (projection.LaterIssues.Count != issueGroups.Count
            || projection.LaterIssues.Any(i => i.IsCandidate
                || !i.DepletesPool || !issueGroups.ContainsKey(i.Key))
            || receipts.Any(r => r.ReceiptKey is null))
            throw Invalid("The FIFO count has an unsupported later event.");
        var factIds = receipts.Select(r => r.OriginFactId)
            .Concat(issueGroups.Values.SelectMany(g => g)
                .Select(c => c.IssueFactId)).Distinct().ToArray();
        var facts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => factIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, cancellationToken);
        if (facts.Count != factIds.Length
            || facts.Values.Any(f => f.InventoryHistoryId is null))
            throw Invalid("A later FIFO event has no Inventory owner.");
        var historyIds = facts.Values.Select(f =>
            f.InventoryHistoryId!.Value).Distinct().ToArray();
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => historyIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, cancellationToken);
        var postingIds = receipts.Select(r => r.OriginPostingId)
            .Concat(facts.Values.Select(f => f.StockPostingId))
            .Concat(histories.Values.Where(h => h.StockPostingId.HasValue)
                .Select(h => h.StockPostingId!.Value))
            .Distinct().ToArray();
        var postings = await db.StockPostings.AsNoTracking()
            .Where(p => postingIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var pileIds = histories.Values.SelectMany(h =>
                new int?[] { h.FromBalLocId, h.ToBalLocId })
            .Where(id => id.HasValue).Select(id => id!.Value)
            .Distinct().ToArray();
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(p => pileIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (histories.Count != historyIds.Length
            || postings.Count != postingIds.Length
            || piles.Count != pileIds.Length)
            throw Invalid("A later FIFO owner or pile is missing.");
        var countedIds = lines.Select(l => l.BalLocId).ToHashSet();
        var expected = new List<Movement>();
        foreach (var receipt in receipts)
        {
            if (!facts.TryGetValue(receipt.OriginFactId, out var fact)
                || fact.InventoryHistoryId is not int historyId
                || !histories.TryGetValue(historyId, out var history)
                || history.ToBalLocId is not int pileId
                || !piles.TryGetValue(pileId, out var pile)
                || !postings.TryGetValue(receipt.OriginPostingId,
                    out var owner)
                || receipt.ReceiptKey is not { } key
                || key.LineIdentity != $"H{historyId:D10}"
                || key.OriginalPostingSequence != owner.PostingSequence
                || !ValidOwner(owner, sheet, receipt.ReceiptEffectiveAt,
                    IvTrxTypes.MiscellaneousReceipt)
                || !ValidHistory(history, sheet, itemCode, owner,
                    IvTrxTypes.MiscellaneousReceipt)
                || !ValidPile(pile, sheet, itemCode, receipt.BaseUom)
                || history.ToStdQty != receipt.OriginalQuantity
                || history.ToStdUom != receipt.BaseUom
                || history.TrxDtTime != key.EffectiveAt
                || fact.StockPostingId != receipt.OriginPostingId
                || fact.InventoryHistoryId != historyId
                || fact.LedgerEpochId != sheet.LedgerEpochId
                || fact.ItemCode != itemCode
                || fact.CostMethod != StockCostMethods.Fifo
                || fact.SourceDocumentType != owner.SourceDocumentType
                || fact.SourceDocumentId != owner.SourceDocumentId
                || fact.SourceDocumentLine != history.TrxLineNo.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                || fact.PostingLineNo != history.PostingLineNo
                || fact.EffectiveAt != key.EffectiveAt
                || fact.BaseUom != receipt.BaseUom
                || fact.MovementCode != "RECEIPT_IN"
                || fact.Direction != 1
                || fact.BaseQty != receipt.OriginalQuantity
                || fact.CostAmount != receipt.OriginalValue
                || fact.ValuationStatus != StockValuationStatuses.Valued)
                throw Invalid("A later FIFO receipt has no exact MR owner.");
            if (countedIds.Contains(pileId))
                expected.Add(new(pileId, new(historyId, key,
                    receipt.OriginalQuantity)));
        }
        foreach (var (key, consumptions) in issueGroups)
        {
            var firstFactId = consumptions[0].IssueFactId;
            if (!facts.TryGetValue(firstFactId, out var firstFact)
                || firstFact.InventoryHistoryId is not int historyId
                || key.LineIdentity != $"H{historyId:D10}"
                || !histories.TryGetValue(historyId, out var history)
                || history.FromBalLocId is not int pileId
                || !piles.TryGetValue(pileId, out var pile)
                || history.StockPostingId is not long ownerId
                || !postings.TryGetValue(ownerId, out var owner)
                || !StockReplayFifoInventoryIssueKinds.IsSupported(
                    history.TrxType)
                || !ValidOwner(owner, sheet, key.EffectiveAt,
                    history.TrxType)
                || !ValidHistory(history, sheet, itemCode, owner,
                    history.TrxType)
                || !ValidPile(pile, sheet, itemCode, history.FrStdUom)
                || key.OriginalPostingSequence != owner.PostingSequence
                || history.TrxDtTime != key.EffectiveAt
                || history.FrStdQty != consumptions.Sum(c => c.Quantity)
                || history.ToBalLocId is > 0
                || consumptions.Any(c => !c.DepletesPool
                    || !facts.TryGetValue(c.IssueFactId, out var fact)
                    || fact.InventoryHistoryId != historyId
                    || fact.LedgerEpochId != sheet.LedgerEpochId
                    || fact.ItemCode != itemCode
                    || fact.BaseUom != history.FrStdUom
                    || fact.CostMethod != StockCostMethods.Fifo
                    || fact.SourceDocumentType != owner.SourceDocumentType
                    || fact.SourceDocumentId != owner.SourceDocumentId
                    || fact.SourceDocumentLine != history.TrxLineNo.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    || fact.EffectiveAt != key.EffectiveAt
                    || fact.ValuationSource != StockValuationSources.Fifo
                    || fact.MovementCode !=
                        StockReplayFifoInventoryIssueKinds.MovementCode(
                            history.TrxType)
                    || fact.Direction != -1
                    || fact.BaseQty != c.Quantity
                    || fact.CostAmount != c.Value
                    || fact.SplitOrdinal != c.SplitOrdinal
                    || fact.ValuationStatus
                        != StockValuationStatuses.Valued))
                throw Invalid("A later FIFO issue has no exact MI/Scrap owner.");
            if (countedIds.Contains(pileId))
                expected.Add(new(pileId, new(historyId, key,
                    -history.FrStdQty!.Value)));
        }
        if (physical.Any(e => e.Event.EventId <= 0
                || e.Event.EventId > int.MaxValue
                || e.Event.Key.ClassPriority
                    != StockReplayEventKey.OrdinaryClass
                || e.Event.AbsoluteCountQty is not null
                || e.Event.SignedDelta == 0m)
            || !physical.Select(e => (e.PileId, e.Event))
                .OrderBy(e => e.PileId).ThenBy(e => e.Event.Key)
                .SequenceEqual(expected
                    .Select(e => (e.PileId, e.Event))
                    .OrderBy(e => e.PileId).ThenBy(e => e.Event.Key)))
            throw Invalid("A later counted-pile movement lacks FIFO ownership.");
        return Fingerprint(new
        {
            sheet.PlanHash, itemCode,
            Receipts = receipts,
            Issues = issueGroups.OrderBy(g => g.Key).Select(g => new
            {
                g.Key, Consumptions = g.Value
            }).ToArray(),
            Histories = histories.Values.OrderBy(h => h.Id).Select(h => new
            {
                h.Id, h.CompanyCode, h.BranchCode, h.ICode, h.TrxType,
                h.BatchNo, h.TrxLineNo, h.TrxDtTime, h.BatchStatus,
                h.ValuationStatus, h.EntryRole, h.LedgerVersion,
                h.LedgerEpochId, h.StockPostingId, h.PostingLineNo,
                h.DocumentRevision, h.FromBalLocId, h.ToBalLocId,
                h.FrStdQty, h.ToStdQty, h.FrStdUom, h.ToStdUom
            }).ToArray(),
            Facts = facts.Values.OrderBy(f => f.Id).Select(f => new
            {
                f.Id, f.InventoryHistoryId, f.StockPostingId,
                f.OriginalValuationFactId, f.ReversesValuationFactId,
                f.PostingLineNo, f.SplitOrdinal, f.MovementCode,
                f.Direction, f.BaseQty, f.CostAmount,
                f.ValuationVersion, f.ValuationStatus,
                f.SourceDocumentType, f.SourceDocumentId,
                f.SourceDocumentLine, f.EffectiveAt, f.BaseUom,
                f.ValuationSource
            }).ToArray(),
            Owners = postings.Values.OrderBy(p => p.Id).Select(p => new
            {
                p.Id, p.CompanyCode, p.BranchCode, p.LedgerEpochId,
                p.PostingSequence, p.EffectiveAt, p.PostingRole,
                p.SourceModule, p.SourceDocumentType,
                p.SourceDocumentId, p.DocumentRevision, p.SealedAtUtc
            }).ToArray(),
            Piles = piles.Values.OrderBy(p => p.Id).Select(p => new
            {
                p.Id, p.CompanyCode, p.BranchCode, p.ICode, p.StdUom
            }).ToArray(),
            Physical = physical
        });
    }

    private static bool ValidOwner(StockPosting owner,
        StockReplayCountSheetRemovalPlan sheet, DateTime effectiveAt,
        string documentType) =>
        owner.CompanyCode == sheet.CompanyCode
        && owner.BranchCode == sheet.BranchCode
        && owner.LedgerEpochId == sheet.LedgerEpochId
        && owner.SourceModule == "INVENTORY"
        && owner.SourceDocumentType == documentType
        && owner.PostingRole == "PRIMARY"
        && owner.SealedAtUtc is not null
        && owner.EffectiveAt == effectiveAt;

    private static bool ValidHistory(ErpWeb.Model.Entities.Inventory.IvTrxHistory
        history, StockReplayCountSheetRemovalPlan sheet, string itemCode,
        StockPosting owner, string documentType) =>
        history.CompanyCode == sheet.CompanyCode
        && history.BranchCode == sheet.BranchCode
        && history.ICode == itemCode
        && history.TrxType == documentType
        && history.EntryRole == "ORIGINAL"
        && history.LedgerVersion == 2
        && history.LedgerEpochId == sheet.LedgerEpochId
        && history.StockPostingId == owner.Id
        && history.DocumentRevision == owner.DocumentRevision
        && history.BatchStatus == "POSTED"
        && history.ValuationStatus == StockValuationStatuses.Valued
        && owner.SourceDocumentId == history.BatchNo.ToString(
            System.Globalization.CultureInfo.InvariantCulture);

    private static bool ValidPile(ErpWeb.Model.Entities.Inventory.IvBalLoc
        pile, StockReplayCountSheetRemovalPlan sheet, string itemCode,
        string? baseUom) =>
        pile.CompanyCode == sheet.CompanyCode
        && pile.BranchCode == sheet.BranchCode
        && pile.ICode == itemCode
        && pile.StdUom == baseUom;

    private static string Fingerprint(object value) =>
        StockPostingFingerprint.Create(value, new { }).RequestFingerprint;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch,
            message));
}
