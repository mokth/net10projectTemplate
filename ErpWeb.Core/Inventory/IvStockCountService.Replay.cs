using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvStockCountService
{
    private async Task<string?> CaptureCountWatermarkGuardAsync(
        string companyCode, string branchCode, int countId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var header = await db.IvStockCountHdrs.AsNoTracking()
            .Where(h => h.Id == countId && h.CompanyCode == companyCode && h.BranchCode == branchCode)
            .Select(h => new { h.Id, h.Status, h.CountDate, h.RowVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null)
            return null;
        var lines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.StockCountId == countId)
            .OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.BalLocId, l.ICode, l.PhysicalQty, l.RowVersion })
            .ToArrayAsync(cancellationToken);
        var items = lines.Select(l => l.ICode).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var pileIds = lines.Select(l => l.BalLocId).Distinct().OrderBy(x => x).ToArray();
        var states = items.Length == 0 ? [] : await db.StockCostStates.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode && s.BranchCode == branchCode && items.Contains(s.ItemCode))
            .OrderBy(s => s.ItemCode).ThenBy(s => s.CostMethod)
            .Select(s => new { s.ItemCode, s.CostMethod, s.OnHandBaseQty, s.InventoryValue, s.LastValuationFactId, s.LastPostingSequence })
            .ToArrayAsync(cancellationToken);
        var piles = pileIds.Length == 0 ? [] : await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode && b.BranchCode == branchCode && pileIds.Contains(b.Id))
            .OrderBy(b => b.Id)
            .Select(b => new { b.Id, b.StdQty, b.RowVersion })
            .ToArrayAsync(cancellationToken);
        return StockPostingFingerprint.Create(new { Kind = "COUNT_WATERMARK_GUARD", header, lines, states, piles }, new { }).RequestFingerprint;
    }

    private sealed record QaCountProjection(int BalanceId, decimal CurrentQuantity,
        byte[] RowVersion, decimal FinalQuantity);

    private async Task<string?> CheckQaCountHoldsWithinRootAsync(
        StockPostingContext root, IReadOnlyList<QaCountProjection> projections,
        CancellationToken cancellationToken)
    {
        root.EnsureUnsealed();
        if (root.Db.Database.CurrentTransaction is null || projections.Count == 0
            || projections.Select(p => p.BalanceId).Distinct().Count() != projections.Count
            || projections.Any(p => p.BalanceId <= 0 || p.FinalQuantity < 0m))
            return "The count stock balances changed before QA validation.";
        var ids = projections.Select(p => p.BalanceId).ToArray();
        var snapshots = await root.Db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == root.CompanyCode && b.BranchCode == root.BranchCode
                && ids.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);
        if (snapshots.Count != projections.Count)
            return "The count stock balances changed before QA validation.";
        var bySlice = new Dictionary<IvStockSliceKey, QaCountProjection>();
        foreach (var projection in projections)
        {
            var pile = snapshots[projection.BalanceId];
            var slice = IvStockSliceKey.Create(pile.CompanyCode, pile.BranchCode,
                pile.ICode, pile.WhCode, pile.LocCode, pile.LotNo, pile.IStatus);
            if (!bySlice.TryAdd(slice, projection))
                return "The count has ambiguous stock balances for QA validation.";
        }
        var ordered = _postingRepo.GetOrderedStockSlices(bySlice.Keys);
        var locked = await _postingRepo.LockBalanceSlicesAsync(root.Db, ordered, cancellationToken);
        foreach (var slice in ordered)
        {
            var projection = bySlice[slice];
            if (!locked.TryGetValue(slice, out var pile) || pile.Id != projection.BalanceId
                || pile.StdQty != projection.CurrentQuantity
                || !pile.RowVersion.SequenceEqual(projection.RowVersion))
                return "The count stock balances changed before QA validation.";
            var error = await _qaPhysical.AssertRemainingCoversHoldsAsync(root.Db,
                root.CompanyCode, root.BranchCode, pile.Id, projection.FinalQuantity, cancellationToken);
            if (error is not null) return error;
        }
        return null;
    }

    private async Task<IvStockCountOperationResult?> TryPreviewBackdatedCountAsync(
        UserContext user, int id,
        IReadOnlyCollection<StockCountCostApprovalRequest> costApprovals,
        CancellationToken cancellationToken)
    {
        var company = user.CompanyCode!;
        var branch = user.BranchCode!;
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var header = await read.IvStockCountHdrs.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == id && h.CompanyCode == company
                && h.BranchCode == branch, cancellationToken);
        if (header is null || header.Status is not
                (IvStockCountStatuses.Counted or IvStockCountStatuses.RolledBack))
            return null;
        if (header.CountDate.Date > _currentDate.Today)
            return IvStockCountOperationResult.Fail(
                $"Count date {header.CountDate:yyyy-MM-dd} is in the future.");
        var effectiveAt = StockReplayEventKey.CountEndOfDay(header.CountDate);
        var lines = await ReadBoundedReplayCountLinesAsync(read, id,
            cancellationToken);
        if (lines is null)
            return IvStockCountOperationResult.Fail(
                "This historical count needs a supported replay sheet with at most ten fully counted piles across two items.");
        var method = await new StockCostMethodResolver().ResolveAsync(read,
            company, branch, effectiveAt, cancellationToken);
        var fifoCount = IsBoundedFifoCount(method, lines);
        var standardCount = IsBoundedStandardCount(method, lines);
        if (method != StockCostMethods.MovingAverage && !fifoCount
            && !standardCount)
            return IvStockCountOperationResult.Fail(
                "This historical count needs a supported event-date cost replay method.");
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, company,
                branch, header.CountDate, cancellationToken) is string closed)
            return IvStockCountOperationResult.Fail(closed);
        var plan = await StockReplayCountSheetRootWriter.BuildAsync(read,
            _accessRights, company, branch, id, costApprovals, user.UserId ?? "PREVIEW",
            DateTime.UtcNow, cancellationToken);
        if (fifoCount)
        {
            if (plan.Fifo is not { Items.Count: >= 1 and <= 2 } fifo
                || plan.PhysicalSheet.Lines.Count is < 1 or > 10)
                return IvStockCountOperationResult.Fail(
                    "The FIFO count needs a supported replay sheet.");
            if (await CheckBoundedFifoCountPlanAsync(read, fifo,
                    company, branch, header.CountDate, effectiveAt,
                    cancellationToken) is string fifoTailError)
                return IvStockCountOperationResult.Fail(fifoTailError);
        }
        else if (standardCount)
        {
            if (await CheckBoundedStandardCountPlanAsync(read, plan,
                    header.CountDate, cancellationToken) is string standardError)
                return IvStockCountOperationResult.Fail(
                    standardError);
        }
        else
        {
            if (plan.Wac is not { Items.Count: >= 1 and <= 2 } wac)
                return IvStockCountOperationResult.Fail(
                    "The count has no bounded WAC replay plan.");
            if (await CheckBoundedWacCountTailAsync(read,
                    wac.Items.SelectMany(i => i.Tail.LaterEvents).ToArray(),
                    header.CountDate, cancellationToken) is string unsupported)
                return IvStockCountOperationResult.Fail(unsupported);
        }
        var preview = await StockReplayCountSheetPreviewReader.ReadAsync(
            read, company, branch, id, cancellationToken);
        var byId = preview.Lines.ToDictionary(p => p.LineId);
        var mapped = lines.Select(line =>
        {
            var projected = byId[line.Id];
            var signed = projected.SignedAdjustmentQty!.Value;
            return new IvStockCountPreviewLine
            {
                LineNumber = line.LineNumber,
                BalLocId = line.BalLocId,
                ICode = line.ICode!,
                IDesc = line.IDesc,
                WHCode = line.WHCode,
                LocCode = line.LocCode,
                LotNo = line.LotNo,
                Uom = line.StdUom,
                SystemQty = line.SystemQty,
                LiveQty = projected.LiveQty,
                HistoricalPreCountQty = projected.HistoricalPreCountQty,
                PhysicalQty = line.PhysicalQty,
                Variance = -signed,
                ProjectedFinalQty = projected.ProjectedFinalQty,
                Direction = signed > 0m ? "INCREASE"
                    : signed < 0m ? "DECREASE" : "NONE",
                IsStale = projected.IsGeneratedSnapshotStale,
                SnapshotUnitPrice = line.SnapshotUnitPrice
            };
        }).ToArray();
        var signedDeltas = preview.Lines
            .Select(p => p.SignedAdjustmentQty!.Value).ToArray();
        return IvStockCountOperationResult.OkPreview(new IvStockCountPostPreview
        {
            Id = id,
            CountNo = header.CountNo,
            CountDate = header.CountDate,
            Status = header.Status,
            IncreaseLines = signedDeltas.Count(d => d > 0m),
            DecreaseLines = signedDeltas.Count(d => d < 0m),
            ZeroVarianceLines = signedDeltas.Count(d => d == 0m),
            StaleLines = mapped.Count(l => l.IsStale),
            TotalIncrease = signedDeltas.Sum(d => Math.Max(0m, d)),
            TotalDecrease = signedDeltas.Sum(d => Math.Max(0m, -d)),
            Lines = mapped
        });
    }

    private async Task<IvStockCountOperationResult?> TryPostBackdatedCountAsync(
        UserContext user, int id,
        IReadOnlyCollection<StockCountCostApprovalRequest> costApprovals,
        CancellationToken cancellationToken)
    {
        var company = user.CompanyCode!;
        var branch = user.BranchCode!;
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var header = await read.IvStockCountHdrs.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == id && h.CompanyCode == company
                && h.BranchCode == branch, cancellationToken);
        if (header is null || header.Status is not
                (IvStockCountStatuses.Counted or IvStockCountStatuses.RolledBack))
            return null;
        if (header.CountDate.Date > _currentDate.Today)
            return IvStockCountOperationResult.Fail(
                $"Count date {header.CountDate:yyyy-MM-dd} is in the future.");
        var effectiveAt = StockReplayEventKey.CountEndOfDay(header.CountDate);
        var lines = await ReadBoundedReplayCountLinesAsync(read, id,
            cancellationToken);
        if (lines is null)
            return IvStockCountOperationResult.Fail(
                "This historical count needs a supported replay sheet with at most ten fully counted piles across two items.");
        var method = await new StockCostMethodResolver().ResolveAsync(read,
            company, branch, effectiveAt, cancellationToken);
        var fifoCount = IsBoundedFifoCount(method, lines);
        var standardCount = IsBoundedStandardCount(method, lines);
        if (method != StockCostMethods.MovingAverage && !fifoCount
            && !standardCount)
            return IvStockCountOperationResult.Fail(
                "This historical count needs a supported event-date cost replay method.");
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, company,
                branch, header.CountDate, cancellationToken) is string closed)
            return IvStockCountOperationResult.Fail(closed);
        var plan = await StockReplayCountSheetRootWriter.BuildAsync(read,
            _accessRights, company, branch, id, costApprovals, user.UserId!,
            DateTime.UtcNow, cancellationToken);
        var wac = plan.Wac;
        if (fifoCount)
        {
            if (plan.Fifo is not { Items.Count: >= 1 and <= 2 } fifo
                || plan.PhysicalSheet.Lines.Count is < 1 or > 10)
                return IvStockCountOperationResult.Fail(
                    "The FIFO count needs a supported replay sheet.");
            if (await CheckBoundedFifoCountPlanAsync(read, fifo,
                    company, branch, header.CountDate, effectiveAt,
                    cancellationToken) is string fifoTailError)
                return IvStockCountOperationResult.Fail(fifoTailError);
        }
        else if (standardCount)
        {
            if (await CheckBoundedStandardCountPlanAsync(read, plan,
                    header.CountDate, cancellationToken) is string standardError)
                return IvStockCountOperationResult.Fail(
                    standardError);
        }
        else
        {
            if (wac is not { Items.Count: >= 1 and <= 2 })
                return IvStockCountOperationResult.Fail(
                    "The count has no bounded WAC replay plan.");
            if (await CheckBoundedWacCountTailAsync(read,
                    wac.Items.SelectMany(i => i.Tail.LaterEvents).ToArray(),
                    header.CountDate, cancellationToken) is string unsupported)
                return IvStockCountOperationResult.Fail(unsupported);
        }

        await using var write = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await write.Database.BeginTransactionAsync(cancellationToken);
        var begin = await _coordinator!.BeginInTransactionAsync(write,
            plan.Command, cancellationToken);
        await using var postingScope = new StockPostingAbortScope(
            _coordinator, begin.Context);
        if (begin.Error is not null || begin.Context is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvStockCountOperationResult.Fail(begin.Error?.Message
                ?? "Historical count requires an active V2 posting root.");
        }
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, company,
                branch, header.CountDate, cancellationToken) is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvStockCountOperationResult.Fail(lockedClosed);
        }
        var root = begin.Context;
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(root, plan.PhysicalSheet, cancellationToken);
        var qaError = await CheckQaCountHoldsWithinRootAsync(root,
            plan.PhysicalSheet.Lines.Select(l => new QaCountProjection(
                l.BalLocId, l.CurrentQuantity, l.BalanceRowVersion, l.Projection.FinalQuantity)).ToArray(),
            cancellationToken);
        if (qaError is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvStockCountOperationResult.Fail(qaError);
        }
        var written = await StockReplayCountSheetRootWriter.AppendWithinRootAsync(
            root, plan, _runningNumbers, cancellationToken);
        await _coordinator.CompleteReplayInTransactionAsync(root,
            written.RequiredActiveHistoryIds, cancellationToken);
        var projections = wac is not null
            ? wac.Items.Select(i => (i.ItemCode,
                i.Projection.FinalQuantity, i.Projection.FinalValue)).ToArray()
            : plan.Standard is { } standard
                ? standard.Items.Select(i => (i.ItemCode,
                    i.Projection.FinalQuantity, i.Projection.FinalValue)).ToArray()
                : plan.Fifo!.Items.Select(i => (i.ItemCode,
                    i.Projection.FinalQuantity, i.Projection.FinalValue)).ToArray();
        var itemCodes = projections.Select(i => i.ItemCode).ToArray();
        var states = await write.StockCostStates.AsNoTracking()
            .Where(s => s.CompanyCode == company && s.BranchCode == branch
                && itemCodes.Contains(s.ItemCode))
            .ToDictionaryAsync(s => s.ItemCode, cancellationToken);
        var pileIds = lines.Select(l => l.BalLocId).ToArray();
        var piles = await write.IvBalLocs.AsNoTracking()
            .Where(b => pileIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        var projectedByPile = plan.PhysicalSheet.Lines
            .ToDictionary(p => p.BalLocId,
                p => p.Projection.FinalQuantity);
        if (states.Count != projections.Length
            || projections.Any(i => !states.TryGetValue(i.ItemCode, out var state)
                || state.OnHandBaseQty != i.FinalQuantity
                || state.InventoryValue != i.FinalValue)
            || piles.Count != lines.Length
            || projectedByPile.Count != lines.Length
            || lines.Any(l => !piles.TryGetValue(l.BalLocId, out var pile)
                || pile.StdQty != projectedByPile[l.BalLocId]))
            throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                "The historical count does not reconcile to its projected stock and cost state."));
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        var posted = await write.IvStockCountHdrs.AsNoTracking()
            .SingleAsync(h => h.Id == id, cancellationToken);
        return IvStockCountOperationResult.OkPosted(id, posted.CountNo,
            written.Wac?.History.AdjustmentBatchNo
                ?? written.Standard?.History.AdjustmentBatchNo
                ?? written.Fifo?.History.AdjustmentBatchNo,
            posted.PostedStaleLines ?? 0);
    }

    private async Task<IvStockCountOperationResult?> TryRollBackBackdatedCountAsync(
        UserContext user, int id, string reason,
        CancellationToken cancellationToken)
    {
        var company = user.CompanyCode!;
        var branch = user.BranchCode!;
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var header = await read.IvStockCountHdrs.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == id && h.CompanyCode == company
                && h.BranchCode == branch, cancellationToken);
        if (header is null || header.Status != IvStockCountStatuses.Posted
            || !await HasV2CountGenerationsAsync(read, header,
                cancellationToken))
            return null;
        var lines = await ReadBoundedReplayCountLinesAsync(read, id,
            cancellationToken);
        if (lines is null)
            return null;
        var effectiveAt = StockReplayEventKey.CountEndOfDay(header.CountDate);
        var method = await new StockCostMethodResolver().ResolveAsync(read,
            company, branch, effectiveAt, cancellationToken);
        var fifoCount = IsBoundedFifoCount(method, lines);
        var standardCount = IsBoundedStandardCount(method, lines);
        if (method != StockCostMethods.MovingAverage && !fifoCount
            && !standardCount)
            return null;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, company,
                branch, header.CountDate, cancellationToken) is string closed)
            return IvStockCountOperationResult.Fail(closed);
        var plan = await StockReplayCountSheetRemovalRootWriter.BuildAsync(
            read, company, branch, id, cancellationToken);
        var wac = plan.Wac;
        Dictionary<string, (decimal Quantity, decimal Value)>? zeroStandardStates = null;
        Dictionary<string, StockReplayFifoActiveLayerSnapshot>? zeroFifoSnapshots = null;
        IReadOnlyList<StockReplayWacEvent> later;
        if (fifoCount)
        {
            if (plan.Fifo is { Items.Count: >= 1 and <= 2 } fifo
                && fifo.Sheet.Lines.Count is >= 1 and <= 10)
            {
                var lineById = fifo.Sheet.Lines.ToDictionary(l => l.CountLineId);
                foreach (var item in fifo.Items)
                {
                    var itemLines = item.CountLineIds.Select(lineId => lineById[lineId]).ToArray();
                    if (item.Projection is not null)
                    {
                        if (await CheckBoundedFifoCountTailAsync(read,
                                itemLines.SelectMany(l => l.Physical.LaterEvents).ToArray(),
                                item.Projection.LaterIssues,
                                item.Projection.BeforeFirstCount.LaterReceipts,
                                itemLines.Select(l => l.Physical.RemovedActiveEvent.Key).ToArray(),
                                company, branch, header.CountDate, item.ItemCode,
                                effectiveAt, cancellationToken) is string fifoTailError)
                            return IvStockCountOperationResult.Fail(fifoTailError);
                    }
                    else
                    {
                        zeroFifoSnapshots ??= new(StringComparer.Ordinal);
                        zeroFifoSnapshots[item.ItemCode] = item.Snapshot;
                        if (await CheckZeroFifoItemCountTailAsync(read,
                                item.Snapshot, itemLines, company, branch,
                                header.CountDate, item.ItemCode, effectiveAt,
                                cancellationToken) is string zeroFifoError)
                            return IvStockCountOperationResult.Fail(zeroFifoError);
                    }
                }
            }
            else if (plan.Zero is { Removal.Lines.Count: >= 1 and <= 10 } zero
                && zero.Removal.Lines.All(l => l.Physical.RemovedActiveEvent.SignedDelta == 0m))
            {
                zeroFifoSnapshots = new(StringComparer.Ordinal);
                var lineById = lines.ToDictionary(l => l.Id);
                foreach (var group in zero.Removal.Lines.GroupBy(l => lineById[l.CountLineId].ICode!))
                {
                    var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
                        read, company, branch, group.Key, cancellationToken);
                    zeroFifoSnapshots[group.Key] = snapshot;
                    if (await CheckZeroFifoItemCountTailAsync(read,
                            snapshot, group.ToArray(), company, branch,
                            header.CountDate, group.Key, effectiveAt,
                            cancellationToken) is string zeroFifoError)
                        return IvStockCountOperationResult.Fail(zeroFifoError);
                }
            }
            else
                return IvStockCountOperationResult.Fail(
                    "The FIFO count rollback needs a supported replay sheet.");
            later = [];
        }
        else if (standardCount)
        {
            if (plan.Standard is { Items.Count: >= 1 and <= 2 } standard
                && standard.Sheet.Lines.Count is >= 1 and <= 10)
            {
                var lineById = standard.Sheet.Lines.ToDictionary(l => l.CountLineId);
                foreach (var item in standard.Items)
                {
                    var removedIds = item.RemovedEvents.Select(e => e.EventId).ToHashSet();
                    var standardLater = item.Tail.LaterEvents
                        .Where(e => !removedIds.Contains(e.EventId)).ToArray();
                    var physicalLater = item.CountLineIds
                        .SelectMany(lineId => lineById[lineId].Physical.LaterEvents).ToArray();
                    if (await CheckBoundedStandardCountTailAsync(read, standardLater,
                            physicalLater, header.CountDate, cancellationToken) is string standardError)
                        return IvStockCountOperationResult.Fail(standardError);
                }
            }
            else if (plan.Zero is { Removal.Lines.Count: >= 1 and <= 10 } zero
                && zero.Removal.Lines.All(l => l.Physical.RemovedActiveEvent.SignedDelta == 0m))
            {
                var after = new StockReplayEventKey(effectiveAt,
                    StockReplayEventKey.CountClass, long.MaxValue,
                    "\uffff", int.MaxValue);
                zeroStandardStates = new(StringComparer.Ordinal);
                var lineById = lines.ToDictionary(l => l.Id);
                foreach (var group in zero.Removal.Lines.GroupBy(l => lineById[l.CountLineId].ICode!))
                {
                    var tail = await StockReplayStandardFinancialTailReader.ReadLaterAsync(
                        read, company, branch, group.Key, after,
                        cancellationToken);
                    var physicalLater = group.SelectMany(l => l.Physical.LaterEvents).ToArray();
                    if (await CheckBoundedStandardCountTailAsync(read,
                            tail.LaterEvents, physicalLater,
                            header.CountDate, cancellationToken) is string standardError)
                        return IvStockCountOperationResult.Fail(standardError);
                    zeroStandardStates[group.Key] = (tail.CurrentQuantity, tail.CurrentValue);
                }
            }
            else
                return IvStockCountOperationResult.Fail(
                    "The Standard count rollback needs a supported replay sheet.");
            later = [];
        }
        else if (wac is { Items.Count: >= 1 and <= 2 })
        {
            later = wac.Items.SelectMany(i =>
            {
                var removedIds = i.RemovedEvents.Select(e => e.EventId).ToHashSet();
                return i.Tail.LaterEvents.Where(e => !removedIds.Contains(e.EventId));
            }).ToArray();
        }
        else if (plan.Zero is not null)
        {
            var after = new StockReplayEventKey(effectiveAt,
                StockReplayEventKey.CountClass, long.MaxValue,
                "\uffff", int.MaxValue);
            var tails = new List<StockReplayWacEvent>();
            foreach (var itemCode in lines.Select(l => l.ICode!).Distinct())
                tails.AddRange((await StockReplayWacFinancialTailReader.ReadLaterAsync(
                    read, company, branch, itemCode, after,
                    cancellationToken)).LaterEvents);
            later = tails;
        }
        else
            return IvStockCountOperationResult.Fail(
                "The count has no bounded WAC rollback plan.");
        if (!standardCount && await CheckBoundedWacCountTailAsync(read,
                later, header.CountDate,
                cancellationToken) is string unsupported)
            return IvStockCountOperationResult.Fail(unsupported);

        await using var write = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await write.Database.BeginTransactionAsync(cancellationToken);
        var begin = await _coordinator!.BeginInTransactionAsync(write,
            plan.Command, cancellationToken);
        await using var postingScope = new StockPostingAbortScope(
            _coordinator, begin.Context);
        if (begin.Error is not null || begin.Context is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvStockCountOperationResult.Fail(begin.Error?.Message
                ?? "Historical count rollback requires an active V2 posting root.");
        }
        if (await IvPeriodCloseGuard.EnsureOpenAsync(write, company,
                branch, header.CountDate, cancellationToken) is string lockedClosed)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvStockCountOperationResult.Fail(lockedClosed);
        }
        var root = begin.Context;
        var removal = wac?.Sheet ?? plan.Standard?.Sheet
            ?? plan.Fifo?.Sheet ?? plan.Zero!.Removal;
        await StockReplayCountSheetRemovalPlanner.ValidateWithinRootAsync(root, removal, cancellationToken);
        var qaError = await CheckQaCountHoldsWithinRootAsync(root,
            removal.Lines.Select(l => new QaCountProjection(l.BalLocId,
                l.Physical.CurrentQuantity, l.Physical.BalanceRowVersion,
                l.Physical.Projection.FinalQuantity)).ToArray(), cancellationToken);
        if (qaError is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvStockCountOperationResult.Fail(qaError);
        }
        var written = await StockReplayCountSheetRemovalRootWriter
            .AppendWithinRootAsync(root, plan, _runningNumbers,
                reason.Trim(), DateTime.UtcNow, cancellationToken);
        await _coordinator.CompleteReplayInTransactionAsync(root,
            written.RequiredActiveHistoryIds, cancellationToken);
        var projectedByPile = removal.Lines.ToDictionary(l => l.BalLocId,
            l => l.Physical.Projection.FinalQuantity);
        var pileIds = projectedByPile.Keys.ToArray();
        var piles = await write.IvBalLocs.AsNoTracking()
            .Where(b => pileIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        if (piles.Count != lines.Length || projectedByPile.Count != lines.Length
            || projectedByPile.Any(p => !piles.TryGetValue(p.Key, out var pile)
                || pile.StdQty != p.Value))
            throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                "The historical count rollback does not reconcile its projected stock piles."));
        if (wac is not null)
        {
            var itemCodes = wac.Items.Select(i => i.ItemCode).ToArray();
            var states = await write.StockCostStates.AsNoTracking()
                .Where(s => s.CompanyCode == company && s.BranchCode == branch
                    && itemCodes.Contains(s.ItemCode))
                .ToDictionaryAsync(s => s.ItemCode, cancellationToken);
            if (states.Count != wac.Items.Count
                || wac.Items.Any(i => !states.TryGetValue(i.ItemCode, out var state)
                    || state.OnHandBaseQty != i.FinalQuantity
                    || state.InventoryValue != i.FinalValue))
                throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                    "The historical count rollback does not reconcile its WAC state."));
        }
        else if (plan.Standard is { } standardPlan)
        {
            var itemCodes = standardPlan.Items.Select(i => i.ItemCode).ToArray();
            var states = await write.StockCostStates.AsNoTracking()
                .Where(s => s.CompanyCode == company
                    && s.BranchCode == branch && itemCodes.Contains(s.ItemCode))
                .ToDictionaryAsync(s => s.ItemCode, cancellationToken);
            if (states.Count != standardPlan.Items.Count
                || standardPlan.Items.Any(i => !states.TryGetValue(i.ItemCode, out var state)
                    || state.OnHandBaseQty != i.FinalQuantity
                    || state.InventoryValue != i.FinalValue))
                throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                    "The historical Standard count rollback does not reconcile its cost state."));
        }
        else if (standardCount && zeroStandardStates is { Count: > 0 })
        {
            var itemCodes = zeroStandardStates.Keys.ToArray();
            var states = await write.StockCostStates.AsNoTracking()
                .Where(s => s.CompanyCode == company
                    && s.BranchCode == branch && itemCodes.Contains(s.ItemCode))
                .ToDictionaryAsync(s => s.ItemCode, cancellationToken);
            if (states.Count != zeroStandardStates.Count
                || zeroStandardStates.Any(p => !states.TryGetValue(p.Key, out var state)
                    || state.OnHandBaseQty != p.Value.Quantity
                    || state.InventoryValue != p.Value.Value))
                throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                    "The zero Standard count rollback changed its cost state."));
        }
        else if (plan.Fifo is { } fifoPlan)
        {
            var itemCodes = fifoPlan.Items.Select(i => i.ItemCode).ToArray();
            var states = await write.StockCostStates.AsNoTracking()
                .Where(s => s.CompanyCode == company
                    && s.BranchCode == branch && itemCodes.Contains(s.ItemCode))
                .ToDictionaryAsync(s => s.ItemCode, cancellationToken);
            if (states.Count != fifoPlan.Items.Count)
                throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                    "The historical FIFO count rollback does not reconcile its cost state."));
            foreach (var item in fifoPlan.Items)
            {
                var state = states[item.ItemCode];
                if (item.Projection is not null)
                {
                    if (state.OnHandBaseQty != item.Projection.FinalQuantity
                        || state.InventoryValue != item.Projection.FinalValue)
                        throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                            "The historical FIFO count rollback does not reconcile its cost state."));
                }
                else if (zeroFifoSnapshots is not null
                    && zeroFifoSnapshots.TryGetValue(item.ItemCode, out var expectedZero))
                {
                    var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
                        write, company, branch, item.ItemCode, cancellationToken);
                    if (state.OnHandBaseQty != expectedZero.CurrentQuantity
                        || state.InventoryValue != expectedZero.CurrentValue
                        || !StockReplayFifoActiveLayerReader.SameSnapshot(
                            expectedZero, snapshot))
                        throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                            "The zero FIFO count rollback changed its cost layers."));
                }
            }
        }
        else if (zeroFifoSnapshots is { Count: > 0 })
        {
            var itemCodes = zeroFifoSnapshots.Keys.ToArray();
            var states = await write.StockCostStates.AsNoTracking()
                .Where(s => s.CompanyCode == company
                    && s.BranchCode == branch && itemCodes.Contains(s.ItemCode))
                .ToDictionaryAsync(s => s.ItemCode, cancellationToken);
            if (states.Count != zeroFifoSnapshots.Count)
                throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                    "The zero FIFO count rollback changed its cost layers."));
            foreach (var (itemCode, expectedZero) in zeroFifoSnapshots)
            {
                var state = states[itemCode];
                var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
                    write, company, branch, itemCode, cancellationToken);
                if (state.OnHandBaseQty != expectedZero.CurrentQuantity
                    || state.InventoryValue != expectedZero.CurrentValue
                    || !StockReplayFifoActiveLayerReader.SameSnapshot(
                        expectedZero, snapshot))
                    throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                        "The zero FIFO count rollback changed its cost layers."));
            }
        }
        postingScope.MarkCompleted();
        await tx.CommitAsync(cancellationToken);
        return IvStockCountOperationResult.OkSaved(id, header.CountNo);
    }

    private static async Task<IvStockCountLine[]?> ReadBoundedReplayCountLinesAsync(
        AppDbContext db, int countId, CancellationToken cancellationToken)
    {
        var lines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.StockCountId == countId)
            .OrderBy(l => l.LineNumber).ThenBy(l => l.Id)
            .ToArrayAsync(cancellationToken);
        if (lines.Length is < 1 or > 10
            || string.IsNullOrWhiteSpace(lines[0].ICode)
            || lines.Any(l => l.PhysicalQty is null
                || string.IsNullOrWhiteSpace(l.ICode))
            || lines.Select(l => l.ICode).Distinct().Count() > 2
            || lines.Select(l => l.BalLocId).Distinct().Count() != lines.Length)
            return null;
        return lines;
    }

    private static bool IsBoundedFifoCount(
        string method, IReadOnlyList<IvStockCountLine> lines) =>
        method == StockCostMethods.Fifo && lines.Count is >= 1 and <= 10
        && lines.All(l => l.PhysicalQty >= 0m);

    private static bool IsBoundedStandardCount(
        string method, IReadOnlyList<IvStockCountLine> lines) =>
        method == StockCostMethods.Standard && lines.Count is >= 1 and <= 10
        && lines.All(l => l.PhysicalQty >= 0m);

    private static async Task<string?> CheckBoundedStandardCountPlanAsync(
        AppDbContext db, StockReplayCountSheetRootPlan plan,
        DateTime countDate, CancellationToken cancellationToken)
    {
        if (plan.Standard is not { Items.Count: >= 1 and <= 2 } standard
            || plan.PhysicalSheet.Lines.Count is < 1 or > 10)
            return "The Standard count needs a supported replay sheet.";
        foreach (var item in standard.Items)
        {
            var physical = item.CountedPiles.SelectMany(p => p.LaterEvents).ToArray();
            if (await CheckBoundedStandardCountTailAsync(db,
                    item.Tail.LaterEvents, physical,
                    countDate, cancellationToken) is string error)
                return error;
        }
        return null;
    }

    private static async Task<string?> CheckBoundedFifoCountPlanAsync(
        AppDbContext db, StockReplayFifoCountSheetPostingPlan fifo,
        string company, string branch, DateTime countDate,
        DateTime effectiveAt, CancellationToken cancellationToken)
    {
        foreach (var item in fifo.Items)
        {
            var events = item.CountedPiles.SelectMany(p => p.LaterEvents).ToArray();
            var countKeys = item.CountedPiles.Select(p => p.Candidate.Key).ToArray();
            if (await CheckBoundedFifoCountTailAsync(db, events,
                    item.Projection.LaterIssues,
                    item.Projection.BeforeFirstCount.LaterReceipts,
                    countKeys, company, branch, countDate, item.ItemCode,
                    effectiveAt, cancellationToken) is string error)
                return error;
        }
        return null;
    }

    private static async Task<string?> CheckZeroFifoItemCountTailAsync(
        AppDbContext db, StockReplayFifoActiveLayerSnapshot snapshot,
        IReadOnlyList<StockReplayCountSheetRemovalLine> itemLines,
        string company, string branch, DateTime countDate,
        string itemCode, DateTime effectiveAt,
        CancellationToken cancellationToken)
    {
        var first = itemLines[0];
        var pile = await db.IvBalLocs.AsNoTracking().SingleAsync(b =>
            b.Id == first.BalLocId && b.CompanyCode == company
            && b.BranchCode == branch, cancellationToken);
        var projection = StockReplayFifoTailProjector.InsertCountAdjustment(
            snapshot, first.Physical.RemovedActiveEvent.Key,
            0m, null, pile.StdUom);
        if (projection.FinalQuantity != snapshot.CurrentQuantity
            || projection.FinalValue != snapshot.CurrentValue)
            return "The zero FIFO count changes its active cost pool.";
        return await CheckBoundedFifoCountTailAsync(db,
            itemLines.SelectMany(l => l.Physical.LaterEvents).ToArray(),
            projection.Issues, projection.BeforeCandidate.LaterReceipts,
            itemLines.Select(l => l.Physical.RemovedActiveEvent.Key).ToArray(),
            company, branch, countDate, itemCode, effectiveAt,
            cancellationToken);
    }

    private static async Task<string?> CheckBoundedStandardCountTailAsync(
        AppDbContext db,
        IReadOnlyList<StockReplayStandardEvent> financial,
        IReadOnlyList<StockReplayPhysicalEvent> physical,
        DateTime countDate, CancellationToken cancellationToken)
    {
        const string unsupported = "Historical Standard count supports at most 100 later Inventory MR/MI movements from the counted pile in the same open month.";
        if (financial.Count != physical.Count
            || financial.Any(e => e.Kind != StockReplayStandardEventKinds.Movement
                || e.AbsoluteCountQty is not null)
            || physical.Any(p => p.AbsoluteCountQty is not null
                || !financial.Any(e => e.EventId == p.EventId
                    && e.Key == p.Key
                    && e.OldSignedQuantity == p.SignedDelta)))
            return unsupported;
        var ordinary = financial.Select(e => new StockReplayWacEvent(
            e.EventId, e.Key, e.OldSignedQuantity, e.OldSignedValue,
            e.ReplaySignedQuantity)).ToArray();
        return await CheckBoundedWacCountTailAsync(db, ordinary,
            countDate, cancellationToken) is null ? null : unsupported;
    }

    private static async Task<string?> CheckBoundedFifoCountTailAsync(
        AppDbContext db,
        IReadOnlyList<StockReplayPhysicalEvent> events,
        IReadOnlyList<StockReplayFifoProjectedIssue> issues,
        IReadOnlyList<StockReplayFifoLayerPosition> receipts,
        IReadOnlyCollection<StockReplayEventKey> countKeys,
        string company, string branch, DateTime countDate,
        string itemCode, DateTime effectiveAt,
        CancellationToken cancellationToken)
    {
        const string unsupported = "Historical FIFO count supports at most 100 later Inventory MR/MI movements from the counted pile in the same open month.";
        var keySet = countKeys.ToHashSet();
        var countReceipts = receipts.Where(r => r.ReceiptKey is { } key && keySet.Contains(key)).ToArray();
        var laterReceipts = receipts.Where(r => r.ReceiptKey is null || !keySet.Contains(r.ReceiptKey.Value)).ToArray();
        if (countReceipts.Length > keySet.Count
            || receipts.Any(r => r.ReceiptKey is null)
            || events.Count > 100
            || events.Count != issues.Count + laterReceipts.Length
            || events.Select(e => e.EventId).Distinct().Count() != events.Count
            || events.Any(e => e.EventId <= 0 || e.EventId > int.MaxValue
                || e.AbsoluteCountQty is not null || e.SignedDelta == 0m
                || e.Key.EffectiveAt.Year != countDate.Year
                || e.Key.EffectiveAt.Month != countDate.Month)
            || events.Any(e => e.SignedDelta < 0m
                ? !issues.Any(i => i.Key == e.Key
                    && i.Quantity == -e.SignedDelta)
                : !laterReceipts.Any(r => r.ReceiptKey == e.Key
                    && r.OriginalQuantity == e.SignedDelta)))
            return $"{unsupported} FIFO tail shape did not match the counted pile.";
        if (events.Count == 0)
            return await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.CompanyCode == company && f.BranchCode == branch
                && f.ItemCode == itemCode && f.EffectiveAt > effectiveAt
                && f.StockPosting!.SealedAtUtc != null, cancellationToken)
                ? unsupported : null;
        var ids = events.Select(e => checked((int)e.EventId)).ToArray();
        var byId = events.ToDictionary(e => checked((int)e.EventId));
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => ids.Contains(h.Id)).ToArrayAsync(cancellationToken);
        if (histories.Length != ids.Length || histories.Any(h =>
                h.ICode != itemCode
                || h.LedgerVersion != 2 || h.EntryRole != "ORIGINAL"
                || h.ValuationStatus != StockValuationStatuses.Valued
                || h.BatchStatus != IvBatchStatuses.Posted
                || h.StockPostingId is null
                || h.TrxDtTime != byId[h.Id].Key.EffectiveAt
                || !string.IsNullOrWhiteSpace(h.PoNo)
                || h.PoRelNo is not null
                || (byId[h.Id].SignedDelta < 0m
                    ? h.TrxType != IvTrxTypes.MiscellaneousIssue
                        || h.FromBalLocId is null || h.ToBalLocId is not null
                        || h.FrStdQty != -byId[h.Id].SignedDelta
                    : h.TrxType != IvTrxTypes.MiscellaneousReceipt
                        || h.ToBalLocId is null || h.FromBalLocId is not null
                        || h.ToStdQty != byId[h.Id].SignedDelta)))
            return $"{unsupported} A later movement history is not eligible.";
        await StockReplayValuationGenerationResolver.ResolveAsync(db,
            histories[0].CompanyCode, histories[0].BranchCode,
            histories, cancellationToken);
        var ownerIds = histories.Select(h => h.StockPostingId!.Value)
            .Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking()
            .Where(p => ownerIds.Contains(p.Id)).ToArrayAsync(cancellationToken);
        if (owners.Length != ownerIds.Length || owners.Any(p =>
                p.SealedAtUtc is null || p.SourceModule != "INVENTORY"
                || histories.Where(h => h.StockPostingId == p.Id)
                    .Select(h => h.TrxType).Distinct().Count() != 1
                || p.SourceDocumentType != histories.First(h =>
                    h.StockPostingId == p.Id).TrxType
                || p.CommandType != $"IV_POST_{histories.First(h =>
                    h.StockPostingId == p.Id).TrxType}"))
            return $"{unsupported} A later movement posting owner is not eligible.";
        var laterFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.CompanyCode == company
                && f.BranchCode == branch
                && f.ItemCode == itemCode && f.EffectiveAt > effectiveAt
                && f.StockPosting!.SealedAtUtc != null)
            .Select(f => f.InventoryHistoryId).ToArrayAsync(cancellationToken);
        if (laterFacts.Any(historyId => historyId is null
            || !ids.Contains(historyId.Value)))
            return $"{unsupported} A later valuation is outside those movements.";
        return null;
    }

    private static async Task<string?> CheckBoundedWacCountTailAsync(
        AppDbContext db, IReadOnlyList<StockReplayWacEvent> events,
        DateTime countDate, CancellationToken cancellationToken)
    {
        if (events.Count > 100
            || events.Select(e => e.EventId).Distinct().Count() != events.Count
            || events.Any(e => e.EventId <= 0 || e.EventId > int.MaxValue
                || e.Key.EffectiveAt.Year != countDate.Year
                || e.Key.EffectiveAt.Month != countDate.Month))
            return "Historical WAC count supports at most 100 later Inventory MR/MI/transfer movements in the same open month.";
        if (events.Count == 0)
            return null;
        var ids = events.Select(e => checked((int)e.EventId)).ToArray();
        var byId = events.ToDictionary(e => checked((int)e.EventId));
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => ids.Contains(h.Id)).ToArrayAsync(cancellationToken);
        if (histories.Length != ids.Length || histories.Any(h =>
                h.LedgerVersion != 2 || h.EntryRole != "ORIGINAL"
                || h.ValuationStatus != StockValuationStatuses.Valued
                || h.BatchStatus != IvBatchStatuses.Posted
                || h.StockPostingId is null
                || !string.IsNullOrWhiteSpace(h.PoNo)
                || h.PoRelNo is not null
                || (h.TrxType == IvTrxTypes.MiscellaneousReceipt
                    ? byId[h.Id].OldSignedQuantity <= 0m
                    : h.TrxType == IvTrxTypes.MiscellaneousIssue
                        ? byId[h.Id].OldSignedQuantity >= 0m
                        : h.TrxType != IvTrxTypes.StockTransfer
                            || byId[h.Id].OldSignedQuantity != 0m)))
            return "Historical WAC count has a later movement outside Inventory MR/MI/transfer.";
        var active = await StockReplayValuationGenerationResolver.ResolveAsync(db,
            histories[0].CompanyCode, histories[0].BranchCode,
            histories, cancellationToken);
        if (histories.Any(h => h.TrxType == IvTrxTypes.StockTransfer
            && active[h.Id].Any(f => f.StockPostingId != h.StockPostingId)))
            return "Historical WAC count needs an unchanged later Inventory transfer generation.";
        var ownerIds = histories.Select(h => h.StockPostingId!.Value)
            .Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking()
            .Where(p => ownerIds.Contains(p.Id)).ToArrayAsync(cancellationToken);
        if (owners.Length != ownerIds.Length || owners.Any(p =>
                p.SealedAtUtc is null
                || histories.Where(h => h.StockPostingId == p.Id)
                    .Select(h => h.TrxType).Distinct().Count() != 1
                || !(StockReplayInventoryMiscPostingOwner.Matches(p,
                        histories.First(h => h.StockPostingId == p.Id).TrxType)
                    || p.SourceModule == "INVENTORY"
                        && p.SourceDocumentType == IvTrxTypes.StockTransfer
                        && p.CommandType == $"IV_POST_{IvTrxTypes.StockTransfer}"
                        && p.PostingRole == "PRIMARY")))
            return "Historical WAC count has a foreign or unsealed later posting.";
        var transferHistories = histories.Where(h =>
            h.TrxType == IvTrxTypes.StockTransfer).ToArray();
        if (transferHistories.Length > 0)
        {
            var numbers = transferHistories.Select(h => h.BatchNo)
                .Distinct().ToArray();
            var batches = await db.IvTrxBatches.AsNoTracking()
                .Where(b => b.CompanyCode == histories[0].CompanyCode
                    && b.BranchCode == histories[0].BranchCode
                    && numbers.Contains(b.BatchNo))
                .ToArrayAsync(cancellationToken);
            if (batches.Length != numbers.Length
                || transferHistories.Any(h => !batches.Any(b =>
                    b.BatchNo == h.BatchNo
                    && b.TrxType == IvTrxTypes.StockTransfer
                    && b.BatchStatus == IvBatchStatuses.Posted
                    && owners.Any(p => p.Id == h.StockPostingId
                        && p.SourceDocumentId == b.Id.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                        && p.SourceDocumentNo == h.BatchNo.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)))))
                return "Historical WAC count has a transfer without its exact posted Inventory owner.";
            if (await ErpWeb.Core.Subcon.SubconCustodyGuardService.Instance.HasSubconOwnedTransferAsync(
                    db, histories[0].CompanyCode, histories[0].BranchCode, numbers, cancellationToken))
                return "Historical WAC count cannot yet replay across a subcontractor custody transfer.";
        }
        return null;
    }
}
