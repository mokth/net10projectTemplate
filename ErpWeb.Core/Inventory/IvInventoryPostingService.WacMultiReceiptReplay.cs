using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    // Each item has its own WAC plan, while all lines share one document,
    // branch transaction, posting root, and rollback operation.
    private sealed record MultiWacMrInsertLine(
        StockReplayFifoMrReceiptAuthority Authority, StockReplayWacInsertionPlan Financial);

    private sealed record MultiWacMrRemovalLine(
        IvTrxHistory Original, StockReplayWacRemovalPlan Financial);

    private static bool IsMultiWacMrShape(IvTrxBatch batch, IvTrxBatchDetail[] details)
        => details.Length is >= 2 and <= 10
           && details.All(d => d.BatchId == batch.Id && d.CompanyCode == batch.CompanyCode
               && d.BranchCode == batch.BranchCode && d.BatchNo == batch.BatchNo
               && d.TrxType == IvTrxTypes.MiscellaneousReceipt && d.TrxLineNo > 0
               && !string.IsNullOrWhiteSpace(d.ICode))
           && details.Select(d => d.TrxLineNo).Distinct().Count() == details.Length
           && details.Select(d => d.DocumentRevision).Distinct().Count() == 1
           && details.Select(d => d.ICode!).Distinct(StringComparer.OrdinalIgnoreCase).Count() == details.Length;

    private static async Task<bool> HasOnlyWacPoolsAsync(AppDbContext db, string company, string branch,
        IvTrxBatchDetail[] details, CancellationToken ct)
    {
        var items = details.Select(d => d.ICode!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var states = await db.StockCostStates.AsNoTracking().Where(s => s.CompanyCode == company
            && s.BranchCode == branch && items.Contains(s.ItemCode)).ToArrayAsync(ct);
        return states.Length == items.Length && items.All(item => states.Count(s =>
            string.Equals(s.ItemCode, item, StringComparison.OrdinalIgnoreCase)
            && s.CostMethod == StockCostMethods.MovingAverage) == 1);
    }

    private static async Task<bool> HasReversedOutgoingAfterAsync(AppDbContext db, string company, string branch,
        string[] items, DateTime receiptAt, CancellationToken ct) =>
        await db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.CompanyCode == company && h.BranchCode == branch
            && h.ICode != null && items.Contains(h.ICode) && h.LedgerVersion == 2 && h.EntryRole == "ORIGINAL"
            && h.TrxDtTime >= receiptAt && h.FromBalLocId != null && h.FrStdQty > 0m
            && db.IvTrxHistories.Any(r => r.ReversesHistoryId == h.Id), ct);

    private async Task<IvInventoryPostingBatchResult?> TryPostBackdatedMultiWacMrAsync(
        string company, string branch, int batchNo, CancellationToken ct)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(ct);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b => b.CompanyCode == company
            && b.BranchCode == branch && b.BatchNo == batchNo && b.TrxType == IvTrxTypes.MiscellaneousReceipt
            && b.BatchStatus == IvBatchStatuses.New, ct);
        if (batch is null) return null;
        var details = await read.IvTrxBatchDetails.AsNoTracking().Where(d => d.BatchId == batch.Id)
            .OrderBy(d => d.TrxLineNo).ToArrayAsync(ct);
        if (!IsMultiWacMrShape(batch, details) || !await HasOnlyWacPoolsAsync(read, company, branch, details, ct))
            return null;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, company, branch, batch.TrxDtTime, ct) is string closed)
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        if (FuturePostDateError(batch) is string future)
            return IvInventoryPostingBatchResult.Fail(batchNo, future);
        var items = details.Select(d => d.ICode!).ToArray();
        if (await HasReversedOutgoingAfterAsync(read, company, branch, items, batch.TrxDtTime, ct))
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "This multi-line receipt has a later reversed issue that needs additional replay.");

        var lines = new List<MultiWacMrInsertLine>();
        foreach (var detail in details)
        {
            var pile = await StockReplayInventoryReceiptPileLocator.FindUniqueAsync(read, company, branch, detail, ct);
            if (pile is not > 0)
                return IvInventoryPostingBatchResult.Fail(batchNo, $"Line {detail.TrxLineNo} needs an existing destination pile for replay.");
            var authority = await StockReplayFifoMrReceiptAuthorityReader.ReadLineAsync(
                read, company, branch, batchNo, pile.Value, detail.Id, ct);
            var physical = await StockReplayInventoryCandidatePlanner.BuildSingleAsync(
                read, company, branch, pile.Value, authority.EffectiveAt, authority.Quantity, ct);
            var candidate = new StockReplayWacEvent(physical.Candidate.EventId, physical.Candidate.Key,
                0m, 0m, authority.Quantity, authority.Value);
            var financial = await StockReplayWacInsertionPlanner.BuildAsync(read, company, branch,
                authority.ItemCode, physical, candidate, ct);
            if (financial.Tail.LaterEvents.Count == 0 && physical.LaterEvents.Count != 0)
                return IvInventoryPostingBatchResult.Fail(batchNo, $"Line {detail.TrxLineNo} has a later movement without valuation.");
            if (financial.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                    read, financial.Tail.LaterEvents, batch.TrxDtTime, ct) is string unsupported)
                return IvInventoryPostingBatchResult.Fail(batchNo, unsupported);
            lines.Add(new(authority, financial));
        }

        await using var write = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await write.Database.BeginTransactionAsync(ct);
        await new BranchStockTransactionLock().AcquireAsync(write, company, branch, ct);
        var locked = await _posting.LockBatchForUpdateAsync(write, company, branch, batchNo, ct);
        if (locked is null || locked.Id != batch.Id || locked.TrxType != batch.TrxType
            || locked.BatchStatus != IvBatchStatuses.New || locked.TrxDtTime != batch.TrxDtTime)
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt changed. Retry posting.");
        var currentDetails = await write.IvTrxBatchDetails.AsNoTracking().Where(d => d.BatchId == batch.Id)
            .OrderBy(d => d.TrxLineNo).ToArrayAsync(ct);
        if (!IsMultiWacMrShape(locked, currentDetails)
            || !currentDetails.Select(d => d.Id).SequenceEqual(details.Select(d => d.Id))
            || !await HasOnlyWacPoolsAsync(write, company, branch, currentDetails, ct)
            || await HasReversedOutgoingAfterAsync(write, company, branch, items, batch.TrxDtTime, ct))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt or its dependencies changed. Retry posting.");
        if (await RejectReversedProductionReceiptAsync(write, company, branch, batchNo,
                IvTrxTypes.MiscellaneousReceipt, rollback: false, ct) is { } rejected)
            return rejected;
        foreach (var line in lines)
        {
            var current = await StockReplayFifoMrReceiptAuthorityReader.ReadLineAsync(write, company, branch,
                batchNo, line.Authority.BalLocId, line.Authority.DetailId, ct);
            if (current != line.Authority)
                return IvInventoryPostingBatchResult.Fail(batchNo, "An approved receipt line changed. Retry posting.");
            if (line.Financial.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                    write, line.Financial.Tail.LaterEvents, batch.TrxDtTime, ct) is string changed)
                return IvInventoryPostingBatchResult.Fail(batchNo, changed);
        }
        var ledger = await BeginStandaloneLedgerAsync(write, company, branch, batchNo, reversal: false, ct);
        await using var scope = new StockPostingAbortScope(_coordinator, ledger.Context);
        if (ledger.Fail is not null) return ledger.Fail;
        var context = ledger.Context ?? throw MultiWacError("Multi-line WAC replay requires an active stock ledger.");
        var required = await WriteMultiWacMrAsync(context, lines, ct);
        await _coordinator!.CompleteReplayInTransactionAsync(context, required, ct);
        scope.MarkCompleted();
        await tx.CommitAsync(ct);
        return IvInventoryPostingBatchResult.Ok(batchNo, locked.PostingOperationId ?? Guid.Empty);
    }

    private static async Task<IReadOnlyList<int>> WriteMultiWacMrAsync(StockPostingContext context,
        IReadOnlyList<MultiWacMrInsertLine> lines, CancellationToken ct)
    {
        context.EnsureUnsealed();
        var groups = lines.GroupBy(l => l.Authority.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.ToArray()).ToArray();
        if (context.CostMethod != StockCostMethods.MovingAverage || lines.Count < 2
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != IvTrxTypes.MiscellaneousReceipt
            || context.Posting.PostingRole != "PRIMARY"
            || groups.Select(g => g[0].Authority.BalLocId).Distinct().Count() != groups.Length
            || lines.Any(l => l.Authority.BatchId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    != context.Posting.SourceDocumentId
                || l.Authority.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    != context.Posting.SourceDocumentNo
                || l.Authority.DocumentRevision != context.Posting.DocumentRevision
                || l.Authority.EffectiveAt != context.Posting.EffectiveAt
                || l.Financial.PhysicalPlan.Candidate.Key.OriginalPostingSequence != context.Posting.PostingSequence
                || l.Financial.ItemCode != l.Authority.ItemCode)
            || groups.Any(g => g.Any(l => !ReferenceEquals(l.Financial, g[0].Financial)
                    || l.Authority.BalLocId != g[0].Authority.BalLocId)
                || g[0].Financial.PhysicalPlan.BalLocId != g[0].Authority.BalLocId
                || g[0].Financial.PhysicalPlan.Candidate.SignedDelta != IvQty.Round(g.Sum(l => l.Authority.Quantity))
                || g[0].Financial.Candidate.ReplaySignedQuantity != IvQty.Round(g.Sum(l => l.Authority.Quantity))
                || g[0].Financial.Candidate.FixedSignedValue != StockLedgerPrecision.Money(g.Sum(l => l.Authority.Value))
                || g[0].Financial.Projection.Candidate.SignedValue != StockLedgerPrecision.Money(g.Sum(l => l.Authority.Value))))
            throw MultiWacError("The multi-line receipt differs from its approved replay plan.");

        var db = context.Db;
        var permits = new List<StockReplayWacWritePermit>(groups.Length);
        foreach (var group in groups)
            permits.Add(await StockReplayWacInsertionPlanner.PrepareWithinRootAsync(context, group[0].Financial, ct));
        var batchId = lines[0].Authority.BatchId;
        var batch = await db.IvTrxBatches.SingleAsync(b => b.Id == batchId, ct);
        var lineIds = lines.Select(l => l.Authority.DetailId).ToArray();
        var details = await db.IvTrxBatchDetails.Where(d => lineIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        var pileIds = groups.Select(g => g[0].Authority.BalLocId).ToArray();
        var balances = await db.IvBalLocs.Where(b => pileIds.Contains(b.Id)
                && b.CompanyCode == context.CompanyCode && b.BranchCode == context.BranchCode)
            .ToDictionaryAsync(b => b.Id, ct);
        db.ChangeTracker.DetectChanges();
        if (batch.BatchStatus != IvBatchStatuses.New || db.Entry(batch).State != EntityState.Unchanged
            || details.Count != lines.Count || balances.Count != groups.Length
            || details.Values.Any(d => db.Entry(d).State != EntityState.Unchanged)
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode && ((h.BatchNo == lines[0].Authority.BatchNo
                    && h.DocumentRevision == context.Posting.DocumentRevision) || h.StockPostingId == context.Posting.Id), ct))
            throw MultiWacError("The multi-line receipt or posting root changed.");

        var now = DateTime.UtcNow;
        var user = context.UserId.Length > 10 ? context.UserId[..10] : context.UserId;
        foreach (var group in groups)
        {
            var authority = group[0].Authority;
            var balance = balances[authority.BalLocId];
            var physical = group[0].Financial.PhysicalPlan;
            if (balance.StdQty != physical.CurrentQuantity || !balance.RowVersion.SequenceEqual(physical.BalanceRowVersion)
                || group.Any(l => balance.LotId != l.Authority.LotId
                    || !string.Equals(balance.LotNo ?? "", l.Authority.LotNo ?? "", StringComparison.OrdinalIgnoreCase)
                    || details[l.Authority.DetailId].DocumentRevision != l.Authority.DocumentRevision
                    || !string.IsNullOrWhiteSpace(details[l.Authority.DetailId].PoNo)
                    || details[l.Authority.DetailId].PoRelNo is not null))
                throw MultiWacError("A multi-line receipt destination changed after planning.");
            if (balance.StdQty == 0m)
            {
                balance.UnitPrice = authority.UnitCost;
                balance.Cost = authority.UnitCost;
                balance.PriceEvidence = details[authority.DetailId].PriceEvidence;
                if (group.Skip(1).Any(l => l.Authority.UnitCost != authority.UnitCost))
                    balance.PriceEvidence = null;
            }
            else if (group.Any(l => balance.UnitPrice != l.Authority.UnitCost)) balance.PriceEvidence = null;
            balance.StdQty = physical.Projection.FinalQuantity;
            balance.TransDate = physical.LaterEvents.Count == 0 ? authority.EffectiveAt
                : physical.LaterEvents.Max(e => e.Key.EffectiveAt);
            balance.ModifiedDate = now;
        }
        var histories = new List<IvTrxHistory>(lines.Count);
        foreach (var line in lines)
        {
            var authority = line.Authority;
            var detail = details[authority.DetailId];
            var balance = balances[authority.BalLocId];
            detail.ToBalLocId = balance.Id;
            detail.ToLotId = authority.LotId;
            histories.Add(new IvTrxHistory
            {
                CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
                BatchNo = authority.BatchNo, TrxLineNo = detail.TrxLineNo,
                TrxType = IvTrxTypes.MiscellaneousReceipt, BatchStatus = IvBatchStatuses.Posted, RefNo = batch.RefNo,
                ProdCode = detail.ProdCode ?? detail.ICode, ProdDesc = detail.ProdDesc ?? detail.IDesc,
                ICode = authority.ItemCode, IDesc = detail.IDesc, ToWarehouse = detail.ToWarehouse,
                ToLocation = detail.ToLocation, ToLotNo = detail.ToLotNo, ToStdQty = detail.ToStdQty,
                ToStdUom = detail.ToStdUom, ToPurQty = detail.ToPurQty, ToPurUom = detail.ToPurUom,
                IStatus = detail.IStatus, DoNo = detail.DoNo, PoNo = detail.PoNo, PoRelNo = detail.PoRelNo,
                PoLineNo = detail.PoLineNo, Remarks = detail.Remarks, PriceEvidence = detail.PriceEvidence,
                CostEvidenceType = detail.CostEvidenceType, CostOverrideReason = detail.CostOverrideReason,
                CostApprovedBy = detail.CostApprovedBy, CostApprovedAtUtc = detail.CostApprovedAtUtc,
                UnitPrice = detail.UnitPrice, Cost = detail.Cost, CostPrice = detail.CostPrice,
                BaseUnitPrices = detail.BaseUnitPrices, LocationCode = detail.LocationCode,
                ToBalLocId = balance.Id, ToLotId = authority.LotId, CreatedDate = now, CreatedBy = user
            });
        }
        new IvInventoryHistoryWriter().StampGeneration(context, histories, context.Posting.DocumentRevision);
        db.IvTrxHistories.AddRange(histories);
        batch.BatchStatus = IvBatchStatuses.Posted;
        batch.PostedDate = now;
        batch.PostedBy = user;
        batch.PostedCount += 1;
        batch.PostingOperationId = Guid.NewGuid();
        batch.ModifiedDate = now;
        batch.ModifiedBy = user;
        await db.SaveChangesAsync(ct);

        var currency = await db.Companies.AsNoTracking().Where(c => c.CompanyCode == context.CompanyCode)
            .Select(c => c.CurrencyCode).SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(currency)) throw MultiWacError("The receipt company has no base currency.");
        for (var i = 0; i < lines.Count; i++)
        {
            var authority = lines[i].Authority;
            var history = histories.Single(h => h.TrxLineNo == details[authority.DetailId].TrxLineNo);
            if (history.ToStdQty != authority.Quantity || history.TrxDtTime != authority.EffectiveAt
                || history.StockPostingId != context.Posting.Id || history.LedgerEpochId != context.Epoch.Id
                || history.EntryRole != "ORIGINAL")
                throw MultiWacError("A receipt history differs from its replay candidate.");
            var sourceLine = history.TrxLineNo.ToString(System.Globalization.CultureInfo.InvariantCulture);
            db.StockValuationFacts.Add(new StockValuationFact
            {
                CompanyCode = context.CompanyCode, BranchCode = context.BranchCode, LedgerEpochId = context.Epoch.Id,
                StockPostingId = context.Posting.Id, PostingLineNo = i + 1, SplitOrdinal = 0,
                SourceLineId = $"{context.Posting.SourceDocumentType}:{context.Posting.SourceDocumentId}:{sourceLine}:{i + 1}:0",
                SourceDocumentType = context.Posting.SourceDocumentType, SourceDocumentId = context.Posting.SourceDocumentId,
                SourceDocumentNo = context.Posting.SourceDocumentNo, SourceDocumentLine = sourceLine,
                EffectiveAt = authority.EffectiveAt, BusinessDate = context.Posting.BusinessDate,
                PeriodKey = context.Posting.PeriodKey, ItemCode = authority.ItemCode,
                WarehouseCode = string.IsNullOrWhiteSpace(history.ToWarehouse) ? null : history.ToWarehouse.Trim(),
                LocationCode = string.IsNullOrWhiteSpace(history.ToLocation) ? null : history.ToLocation.Trim(),
                LotId = authority.LotId, LotNo = authority.LotNo,
                ItemStatus = string.IsNullOrWhiteSpace(history.IStatus) ? null : history.IStatus.Trim(),
                BaseUom = authority.BaseUom, MovementCode = "RECEIPT_IN", Direction = 1, BaseQty = authority.Quantity,
                CostMethod = StockCostMethods.MovingAverage, UnitCost = authority.UnitCost, CostAmount = authority.Value,
                BaseCurrency = currency, BaseCostAmount = authority.Value, ValuationSource = StockValuationSources.MovingAverage,
                ValuationStatus = StockValuationStatuses.Valued, ValuationVersion = 1, InventoryHistoryId = history.Id,
                ToBalLocId = authority.BalLocId, CreatedAtUtc = now, CreatedBy = context.UserId
            });
            history.ValuationStatus = StockValuationStatuses.Valued;
        }
        await db.SaveChangesAsync(ct);
        var required = histories.Select(h => h.Id).ToList();
        var nextLine = checked(lines.Count + 1);
        for (var i = 0; i < groups.Length; i++)
        {
            var tail = await StockReplayWacTailRepricingWriter.AppendPreparedInsertionTailAsync(
                context, permits[i], groups[i][0].Financial, nextLine, ct);
            required.AddRange(tail.RepricedHistoryIds);
            nextLine = checked(nextLine + 2 * groups[i][0].Financial.Tail.LaterEvents.Count);
            await StockReplayWacCostStateWriter.ApplyAsync(context, permits[i], ct);
        }
        return required.Distinct().ToArray();
    }

    private async Task<IvInventoryPostingBatchResult?> TryRollBackBackdatedMultiWacMrAsync(
        string company, string branch, string userId, int batchNo, CancellationToken ct)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(ct);
        var batch = await read.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b => b.CompanyCode == company
            && b.BranchCode == branch && b.BatchNo == batchNo && b.TrxType == IvTrxTypes.MiscellaneousReceipt
            && b.BatchStatus == IvBatchStatuses.Posted, ct);
        if (batch is null) return null;
        var details = await read.IvTrxBatchDetails.AsNoTracking().Where(d => d.BatchId == batch.Id)
            .OrderBy(d => d.TrxLineNo).ToArrayAsync(ct);
        if (!IsMultiWacMrShape(batch, details) || !await HasOnlyWacPoolsAsync(read, company, branch, details, ct))
            return null;
        var route = await StockReplayInventoryRouteSelector.ReadRollbackAsync(read, company, branch, batchNo,
            IvTrxTypes.MiscellaneousReceipt, ct);
        if (!route.RequiresReplay) return null;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, company, branch, batch.TrxDtTime, ct) is string closed)
            return IvInventoryPostingBatchResult.Fail(batchNo, closed);
        var items = details.Select(d => d.ICode!).ToArray();
        if (await HasReversedOutgoingAfterAsync(read, company, branch, items, batch.TrxDtTime, ct))
            return IvInventoryPostingBatchResult.Fail(batchNo,
                "This multi-line receipt has a later reversed issue that needs additional replay before rollback.");
        var histories = (await _posting.LoadHistoryForBatchAsync(read, company, branch, batchNo, ct)).ToArray();
        if (histories.Length != details.Length || histories.Select(h => h.StockPostingId).Distinct().Count() != 1
            || histories.Any(h => h.EntryRole != "ORIGINAL" || h.LedgerVersion != 2 || h.StockPostingId is not > 0
                || h.TrxType != IvTrxTypes.MiscellaneousReceipt || h.ValuationStatus != StockValuationStatuses.Valued))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The multi-line receipt has incomplete posted history.");
        var owner = await read.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == histories[0].StockPostingId, ct);
        if (owner is null || owner.CompanyCode != company || owner.BranchCode != branch || owner.SealedAtUtc is null
            || owner.SourceModule != "INVENTORY" || owner.SourceDocumentType != IvTrxTypes.MiscellaneousReceipt
            || owner.SourceDocumentId != batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || owner.SourceDocumentNo != batchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || owner.PostingRole != "PRIMARY" || owner.EffectiveAt != batch.TrxDtTime
            || histories.Any(h => h.LedgerEpochId != owner.LedgerEpochId || h.TrxDtTime != owner.EffectiveAt
                || h.DocumentRevision != owner.DocumentRevision))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The multi-line receipt has no exact sealed owner.");

        var lines = new List<MultiWacMrRemovalLine>();
        foreach (var detail in details)
        {
            var matching = histories.Where(h => h.TrxLineNo == detail.TrxLineNo).ToArray();
            if (matching.Length != 1 || !MultiWacMrHistoryMatches(detail, matching[0]))
                return IvInventoryPostingBatchResult.Fail(batchNo, $"Receipt line {detail.TrxLineNo} differs from its posted history.");
            var original = matching[0];
            var key = new StockReplayEventKey(owner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
                owner.PostingSequence, $"H{original.Id:D10}", 0);
            var removed = new StockReplayPhysicalEvent(original.Id, key, original.ToStdQty!.Value);
            var physical = await StockReplayPhysicalPlanner.BuildRemovalAsync(read, company, branch,
                original.ToBalLocId!.Value, removed, ct);
            var financial = await StockReplayWacRemovalPlanner.BuildAsync(read, company, branch,
                original.ICode!, physical, ct);
            if (financial.Tail.LaterEvents.Count == 0 && physical.LaterEvents.Count != 0)
                return IvInventoryPostingBatchResult.Fail(batchNo, $"Receipt line {detail.TrxLineNo} has a later movement without valuation.");
            if (financial.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                    read, financial.Tail.LaterEvents, batch.TrxDtTime, ct) is string unsupported)
                return IvInventoryPostingBatchResult.Fail(batchNo, unsupported);
            lines.Add(new(original, financial));
        }

        await using var write = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await write.Database.BeginTransactionAsync(ct);
        await new BranchStockTransactionLock().AcquireAsync(write, company, branch, ct);
        var locked = await _posting.LockBatchForUpdateAsync(write, company, branch, batchNo, ct);
        if (locked is null || locked.Id != batch.Id || locked.TrxType != batch.TrxType
            || locked.BatchStatus != IvBatchStatuses.Posted || locked.TrxDtTime != batch.TrxDtTime)
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt changed. Retry rollback.");
        var currentDetails = await write.IvTrxBatchDetails.AsNoTracking().Where(d => d.BatchId == batch.Id)
            .OrderBy(d => d.TrxLineNo).ToArrayAsync(ct);
        var currentHistories = (await _posting.LoadHistoryForBatchAsync(write, company, branch, batchNo, ct)).ToArray();
        if (!IsMultiWacMrShape(locked, currentDetails) || currentHistories.Length != histories.Length
            || !currentDetails.Select(d => d.Id).SequenceEqual(details.Select(d => d.Id))
            || !currentHistories.Select(h => h.Id).Order().SequenceEqual(histories.Select(h => h.Id).Order())
            || currentDetails.Any(d => currentHistories.Count(h => h.TrxLineNo == d.TrxLineNo
                && MultiWacMrHistoryMatches(d, h)) != 1)
            || !await HasOnlyWacPoolsAsync(write, company, branch, currentDetails, ct)
            || await HasReversedOutgoingAfterAsync(write, company, branch, items, batch.TrxDtTime, ct))
            return IvInventoryPostingBatchResult.Fail(batchNo, "The receipt or its dependencies changed. Retry rollback.");
        if (await RejectReversedProductionReceiptAsync(write, company, branch, batchNo,
                IvTrxTypes.MiscellaneousReceipt, rollback: true, ct) is { } rejected)
            return rejected;
        var qaRestore = await _qaPhysical.RestoreStockOutAsync(write, company, branch, batchNo, ct);
        if (qaRestore is not null) return IvInventoryPostingBatchResult.Fail(batchNo, qaRestore);
        foreach (var line in lines)
            if (line.Financial.Tail.LaterEvents.Count > 0 && await CheckSimpleWacTailAsync(
                    write, line.Financial.Tail.LaterEvents, batch.TrxDtTime, ct) is string changed)
                return IvInventoryPostingBatchResult.Fail(batchNo, changed);
        var ledger = await BeginStandaloneLedgerAsync(write, company, branch, batchNo, reversal: true, ct);
        await using var scope = new StockPostingAbortScope(_coordinator, ledger.Context);
        if (ledger.Fail is not null) return ledger.Fail;
        var context = ledger.Context ?? throw MultiWacError("Multi-line WAC rollback requires an active stock ledger.");
        if (context.CostMethod != StockCostMethods.MovingAverage || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != IvTrxTypes.MiscellaneousReceipt
            || context.Posting.SourceDocumentId != owner.SourceDocumentId
            || context.Posting.SourceDocumentNo != owner.SourceDocumentNo
            || context.Posting.PostingRole != "REVERSAL" || context.Posting.ReversesPostingId != owner.Id
            || context.Posting.EffectiveAt != owner.EffectiveAt)
            throw MultiWacError("The multi-line rollback root does not reverse its receipt owner.");
        var permits = new List<StockReplayWacWritePermit>(lines.Count);
        foreach (var line in lines)
            permits.Add(await StockReplayWacRemovalPlanner.PrepareWithinRootAsync(context, line.Financial, ct));
        var result = await RollBackInventoryMRCoreAsync(write, company, branch, userId, batchNo,
            IvTrxTypes.MiscellaneousReceipt, ct, context);
        if (!result.Succeeded) return result;
        await write.SaveChangesAsync(ct);
        var rootReversals = await write.IvTrxHistories.Where(h => h.StockPostingId == context.Posting.Id
            && h.EntryRole == "REVERSAL").ToArrayAsync(ct);
        if (rootReversals.Length != lines.Count)
            throw MultiWacError("The multi-line receipt has incomplete reversal histories.");
        var activeFacts = await StockReplayValuationGenerationResolver.ResolveAsync(write, company, branch,
            currentHistories, ct);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var original = line.Original;
            var reversal = rootReversals.SingleOrDefault(h => h.ReversesHistoryId == original.Id);
            var pile = await write.IvBalLocs.AsNoTracking().SingleAsync(b => b.Id == original.ToBalLocId
                && b.CompanyCode == company && b.BranchCode == branch, ct);
            var facts = activeFacts[original.Id];
            if (reversal is null || reversal.FromBalLocId != original.ToBalLocId
                || reversal.FrStdQty != original.ToStdQty || reversal.FromLotId != original.ToLotId
                || pile.StdQty != line.Financial.PhysicalPlan.Projection.FinalQuantity || facts.Count != 1
                || facts[0].CostMethod != StockCostMethods.MovingAverage
                || facts[0].SignedQty != line.Financial.RemovedActiveEvent.OldSignedQuantity
                || facts[0].SignedValue != line.Financial.RemovedActiveEvent.OldSignedValue
                || facts[0].LotId != original.ToLotId
                || !string.Equals(facts[0].LotNo ?? "", original.ToLotNo ?? "", StringComparison.OrdinalIgnoreCase))
                throw MultiWacError("A multi-line receipt reversal does not reconcile with its active value.");
            await StockReplayValuationReversalWriter.AppendAsync(context, original.Id,
                [new StockReplayValuationReversalLine(facts[0].Id, i + 1, 0, reversal.Id)], ct);
            reversal.ValuationStatus = StockValuationStatuses.Reversed;
        }
        var required = new List<int>();
        var nextLine = checked(lines.Count + 1);
        for (var i = 0; i < lines.Count; i++)
        {
            var tail = await StockReplayWacTailRepricingWriter.AppendPreparedRemovalTailAsync(
                context, permits[i], lines[i].Financial, nextLine, ct);
            required.AddRange(tail.RepricedHistoryIds);
            nextLine = checked(nextLine + 2 * lines[i].Financial.Tail.LaterEvents.Count);
            await StockReplayWacCostStateWriter.ApplyAsync(context, permits[i], ct);
        }
        await _coordinator!.CompleteReplayInTransactionAsync(context, required.Distinct().ToArray(), ct);
        scope.MarkCompleted();
        await tx.CommitAsync(ct);
        return result;
    }

    private static bool MultiWacMrHistoryMatches(IvTrxBatchDetail detail, IvTrxHistory history) =>
        history.ICode == detail.ICode && history.TrxLineNo == detail.TrxLineNo
        && history.DocumentRevision == detail.DocumentRevision && history.ToBalLocId is > 0
        && history.ToBalLocId == detail.ToBalLocId && history.ToStdQty is > 0m && history.ToStdQty == detail.ToStdQty
        && history.ToStdUom == detail.ToStdUom && history.ToWarehouse == detail.ToWarehouse
        && history.ToLocation == detail.ToLocation && history.ToLotId == detail.ToLotId
        && string.Equals(history.ToLotNo ?? "", detail.ToLotNo ?? "", StringComparison.OrdinalIgnoreCase)
        && history.IStatus == detail.IStatus && history.FromBalLocId is null && history.FrStdQty is null
        && string.IsNullOrWhiteSpace(detail.PoNo) && detail.PoRelNo is null;

    private static StockLedgerException MultiWacError(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
