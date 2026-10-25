using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvStockCountService
{
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
        var effectiveAt = StockReplayEventKey.CountEndOfDay(header.CountDate);
        if (header.CountDate.Date >= _currentDate.Today
            && !await HasLaterSealedCountValuationAsync(read, company, branch,
                id, effectiveAt, cancellationToken))
            return null;
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
        if (header.CountDate.Date > _currentDate.Today)
            return IvStockCountOperationResult.Fail(
                $"Count date {header.CountDate:yyyy-MM-dd} is in the future.");
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, company,
                branch, header.CountDate, cancellationToken) is string closed)
            return IvStockCountOperationResult.Fail(closed);
        var plan = await StockReplayCountSheetRootWriter.BuildAsync(read,
            _accessRights, company, branch, id, costApprovals, user.UserId ?? "PREVIEW",
            DateTime.UtcNow, cancellationToken);
        if (fifoCount)
        {
            if (plan.Fifo is not { Items.Count: 1 }
                || plan.PhysicalSheet.Lines.Count != 1)
                return IvStockCountOperationResult.Fail(
                    "The FIFO count needs one supported pile.");
            if (await CheckBoundedFifoCountTailAsync(read,
                    plan.PhysicalSheet.Lines[0].LaterEvents,
                    plan.Fifo.Items[0].Projection.LaterIssues,
                    plan.Fifo.Items[0].Projection.BeforeFirstCount.LaterReceipts,
                    plan.PhysicalSheet.Lines[0].Candidate.Key,
                    company, branch, header.CountDate, lines[0].ICode!,
                    effectiveAt, cancellationToken) is string fifoTailError)
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
        var effectiveAt = StockReplayEventKey.CountEndOfDay(header.CountDate);
        if (header.CountDate.Date >= _currentDate.Today
            && !await HasLaterSealedCountValuationAsync(read, company, branch,
                id, effectiveAt, cancellationToken))
            return null;
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
        if (header.CountDate.Date > _currentDate.Today)
            return IvStockCountOperationResult.Fail(
                $"Count date {header.CountDate:yyyy-MM-dd} is in the future.");
        if (await IvPeriodCloseGuard.EnsureOpenAsync(read, company,
                branch, header.CountDate, cancellationToken) is string closed)
            return IvStockCountOperationResult.Fail(closed);
        var plan = await StockReplayCountSheetRootWriter.BuildAsync(read,
            _accessRights, company, branch, id, costApprovals, user.UserId!,
            DateTime.UtcNow, cancellationToken);
        var wac = plan.Wac;
        if (fifoCount)
        {
            if (plan.Fifo is not { Items.Count: 1 }
                || plan.PhysicalSheet.Lines.Count != 1)
                return IvStockCountOperationResult.Fail(
                    "The FIFO count needs one supported pile.");
            if (await CheckBoundedFifoCountTailAsync(read,
                    plan.PhysicalSheet.Lines[0].LaterEvents,
                    plan.Fifo.Items[0].Projection.LaterIssues,
                    plan.Fifo.Items[0].Projection.BeforeFirstCount.LaterReceipts,
                    plan.PhysicalSheet.Lines[0].Candidate.Key,
                    company, branch, header.CountDate, lines[0].ICode!,
                    effectiveAt, cancellationToken) is string fifoTailError)
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
        decimal? zeroStandardQuantity = null;
        decimal? zeroStandardValue = null;
        StockReplayFifoActiveLayerSnapshot? zeroFifoSnapshot = null;
        IReadOnlyList<StockReplayWacEvent> later;
        if (fifoCount)
        {
            if (plan.Fifo is { Items.Count: 1 } fifo
                && fifo.Sheet.Lines.Count == 1
                && fifo.Items[0].Projection is not null)
            {
                if (await CheckBoundedFifoCountTailAsync(read,
                        fifo.Sheet.Lines[0].Physical.LaterEvents,
                        fifo.Items[0].Projection.LaterIssues,
                        fifo.Items[0].Projection.BeforeFirstCount.LaterReceipts,
                        fifo.Sheet.Lines[0].Physical.RemovedActiveEvent.Key,
                        company, branch, header.CountDate, lines[0].ICode!,
                        effectiveAt, cancellationToken) is string fifoTailError)
                    return IvStockCountOperationResult.Fail(fifoTailError);
            }
            else if (plan.Zero is { Removal.Lines.Count: 1 } zero
                && zero.Removal.Lines[0].Physical.RemovedActiveEvent.SignedDelta == 0m)
            {
                zeroFifoSnapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
                    read, company, branch, lines[0].ICode!, cancellationToken);
                var pile = await read.IvBalLocs.AsNoTracking().SingleAsync(b =>
                    b.Id == lines[0].BalLocId && b.CompanyCode == company
                    && b.BranchCode == branch, cancellationToken);
                var physical = zero.Removal.Lines[0].Physical;
                var projection = StockReplayFifoTailProjector.InsertCountAdjustment(
                    zeroFifoSnapshot, physical.RemovedActiveEvent.Key,
                    0m, null, pile.StdUom);
                if (projection.FinalQuantity != zeroFifoSnapshot.CurrentQuantity
                    || projection.FinalValue != zeroFifoSnapshot.CurrentValue)
                    return IvStockCountOperationResult.Fail(
                        "The zero FIFO count changes its active cost pool.");
                if (await CheckBoundedFifoCountTailAsync(read,
                        physical.LaterEvents, projection.Issues,
                        projection.BeforeCandidate.LaterReceipts,
                        physical.RemovedActiveEvent.Key, company, branch,
                        header.CountDate, lines[0].ICode!, effectiveAt,
                        cancellationToken) is string fifoTailError)
                    return IvStockCountOperationResult.Fail(fifoTailError);
            }
            else
                return IvStockCountOperationResult.Fail(
                    "The FIFO count rollback needs one supported pile.");
            later = [];
        }
        else if (standardCount)
        {
            if (plan.Standard is { Items.Count: 1 } standard
                && standard.Sheet.Lines.Count == 1
                && standard.Items[0].RemovedEvents.Count == 1)
            {
                var removedId = standard.Items[0].RemovedEvents[0].EventId;
                var standardLater = standard.Items[0].Tail.LaterEvents
                    .Where(e => e.EventId != removedId).ToArray();
                if (await CheckBoundedStandardCountTailAsync(read, standardLater,
                        standard.Sheet.Lines[0].Physical.LaterEvents,
                        header.CountDate, cancellationToken) is string standardError)
                    return IvStockCountOperationResult.Fail(standardError);
            }
            else if (plan.Zero is { Removal.Lines.Count: 1 } zero
                && zero.Removal.Lines[0].Physical.RemovedActiveEvent.SignedDelta == 0m)
            {
                var after = new StockReplayEventKey(effectiveAt,
                    StockReplayEventKey.CountClass, long.MaxValue,
                    "\uffff", int.MaxValue);
                var tail = await StockReplayStandardFinancialTailReader.ReadLaterAsync(
                    read, company, branch, lines[0].ICode!, after,
                    cancellationToken);
                if (await CheckBoundedStandardCountTailAsync(read,
                        tail.LaterEvents,
                        zero.Removal.Lines[0].Physical.LaterEvents,
                        header.CountDate, cancellationToken) is string standardError)
                    return IvStockCountOperationResult.Fail(standardError);
                zeroStandardQuantity = tail.CurrentQuantity;
                zeroStandardValue = tail.CurrentValue;
            }
            else
                return IvStockCountOperationResult.Fail(
                    "The Standard count rollback needs one supported pile.");
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
        var written = await StockReplayCountSheetRemovalRootWriter
            .AppendWithinRootAsync(root, plan, _runningNumbers,
                reason.Trim(), DateTime.UtcNow, cancellationToken);
        await _coordinator.CompleteReplayInTransactionAsync(root,
            written.RequiredActiveHistoryIds, cancellationToken);
        var removal = wac?.Sheet ?? plan.Standard?.Sheet
            ?? plan.Fifo?.Sheet ?? plan.Zero!.Removal;
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
            var item = standardPlan.Items[0];
            var state = await write.StockCostStates.AsNoTracking()
                .SingleAsync(s => s.CompanyCode == company
                    && s.BranchCode == branch && s.ItemCode == item.ItemCode,
                    cancellationToken);
            if (state.OnHandBaseQty != item.FinalQuantity
                || state.InventoryValue != item.FinalValue)
                throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                    "The historical Standard count rollback does not reconcile its cost state."));
        }
        else if (standardCount && zeroStandardQuantity.HasValue
            && zeroStandardValue.HasValue)
        {
            var state = await write.StockCostStates.AsNoTracking()
                .SingleAsync(s => s.CompanyCode == company
                    && s.BranchCode == branch && s.ItemCode == lines[0].ICode,
                    cancellationToken);
            if (state.OnHandBaseQty != zeroStandardQuantity.Value
                || state.InventoryValue != zeroStandardValue.Value)
                throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                    "The zero Standard count rollback changed its cost state."));
        }
        else if (plan.Fifo is { } fifoPlan)
        {
            var item = fifoPlan.Items[0];
            var state = await write.StockCostStates.AsNoTracking()
                .SingleAsync(s => s.CompanyCode == company
                    && s.BranchCode == branch && s.ItemCode == item.ItemCode,
                    cancellationToken);
            if (item.Projection is null
                || state.OnHandBaseQty != item.Projection.FinalQuantity
                || state.InventoryValue != item.Projection.FinalValue)
                throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                    "The historical FIFO count rollback does not reconcile its cost state."));
        }
        else if (zeroFifoSnapshot is not null)
        {
            var state = await write.StockCostStates.AsNoTracking()
                .SingleAsync(s => s.CompanyCode == company
                    && s.BranchCode == branch && s.ItemCode == lines[0].ICode,
                    cancellationToken);
            var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
                write, company, branch, lines[0].ICode!, cancellationToken);
            if (state.OnHandBaseQty != zeroFifoSnapshot.CurrentQuantity
                || state.InventoryValue != zeroFifoSnapshot.CurrentValue
                || !StockReplayFifoActiveLayerReader.SameSnapshot(
                    zeroFifoSnapshot, snapshot))
                throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                    "The zero FIFO count rollback changed its cost layers."));
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
        method == StockCostMethods.Fifo && lines.Count == 1
        && lines[0].PhysicalQty >= 0m;

    private static bool IsBoundedStandardCount(
        string method, IReadOnlyList<IvStockCountLine> lines) =>
        method == StockCostMethods.Standard && lines.Count == 1
        && lines[0].PhysicalQty >= 0m;

    private static async Task<string?> CheckBoundedStandardCountPlanAsync(
        AppDbContext db, StockReplayCountSheetRootPlan plan,
        DateTime countDate, CancellationToken cancellationToken)
    {
        if (plan.Standard is not { Items.Count: 1 } standard
            || plan.PhysicalSheet.Lines.Count != 1)
            return "The Standard count needs one supported pile.";
        return await CheckBoundedStandardCountTailAsync(db,
            standard.Items[0].Tail.LaterEvents,
            plan.PhysicalSheet.Lines[0].LaterEvents,
            countDate, cancellationToken);
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

    private static async Task<bool> HasLaterSealedCountValuationAsync(
        AppDbContext db, string company, string branch, int countId,
        DateTime effectiveAt, CancellationToken cancellationToken)
    {
        var itemCodes = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.StockCountId == countId && l.PhysicalQty != null)
            .Select(l => l.ICode).Distinct().ToArrayAsync(cancellationToken);
        if (itemCodes.Length == 0)
            return false;
        return await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
            f.CompanyCode == company && f.BranchCode == branch
            && itemCodes.Contains(f.ItemCode) && f.EffectiveAt > effectiveAt
            && f.StockPosting!.SealedAtUtc != null, cancellationToken);
    }

    private static async Task<string?> CheckBoundedFifoCountTailAsync(
        AppDbContext db,
        IReadOnlyList<StockReplayPhysicalEvent> events,
        IReadOnlyList<StockReplayFifoProjectedIssue> issues,
        IReadOnlyList<StockReplayFifoLayerPosition> receipts,
        StockReplayEventKey countKey,
        string company, string branch, DateTime countDate,
        string itemCode, DateTime effectiveAt,
        CancellationToken cancellationToken)
    {
        const string unsupported = "Historical FIFO count supports at most 100 later Inventory MR/MI movements from the counted pile in the same open month.";
        var countReceipts = receipts.Where(r => r.ReceiptKey == countKey).ToArray();
        var laterReceipts = receipts.Where(r => r.ReceiptKey != countKey).ToArray();
        if (countReceipts.Length > 1
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
        }
        return null;
    }
}
