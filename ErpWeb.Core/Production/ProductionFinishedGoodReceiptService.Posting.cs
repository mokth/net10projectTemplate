using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.Transactions;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionFinishedGoodReceiptService
{
    public Task<IvMasterOperationResult<FinishedGoodReceiptDocument>> PostAsync(FinishedGoodReceiptCommand request, CancellationToken ct = default)
        => ExecuteAsync(request, false, ct);
    public Task<IvMasterOperationResult<FinishedGoodReceiptDocument>> RollbackAsync(FinishedGoodReceiptCommand request, CancellationToken ct = default)
        => ExecuteAsync(request, true, ct);

    private async Task<IvMasterOperationResult<FinishedGoodReceiptDocument>> ExecuteAsync(FinishedGoodReceiptCommand request, bool reversal, CancellationToken ct)
    {
        try
        {
            var scope = await ScopeAsync(reversal ? PermissionCodes.Rollback : PermissionCodes.Post, ct);
            if (request.RequestId == Guid.Empty) throw new FgException("A stable request ID is required.");
            var commandType = reversal ? ProductionPostingCommandTypes.FinishedGoodReceiptRollback : ProductionPostingCommandTypes.FinishedGoodReceiptPost;
            var fingerprint = StockPostingFingerprint.Create(new { request.Id, Version = Convert.ToBase64String(request.ExpectedVersion), Command = commandType, request.Reason }, new { });
            await using var db = await factory.CreateDbContextAsync(ct);
            db.FinishedGoodReceiptWrite = true;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, ct);
            // Replay precedes date, version, enablement and lifecycle validation.
            var replay = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                && x.CommandType == commandType && x.RequestId == request.RequestId, ct);
            if (replay is not null)
            {
                if (replay.RequestFingerprint != fingerprint.RequestFingerprint) throw new FgException("The request ID was reused with different input.");
                if (replay.SealedAtUtc is null) throw new FgException("The existing posting is not sealed; reconciliation is required.");
                var recorded = await LoadAsync(db, scope, request.Id, false, ct);
                var result = await MapAsync(db, recorded, ct);
                // Preserve the original operation identity even if the receipt was subsequently reversed.
                if (reversal) result.ReversalPostingId = replay.Id; else result.PostingId = replay.Id;
                return IvMasterOperationResult<FinishedGoodReceiptDocument>.Ok(result);
            }
            var r = await LoadAsync(db, scope, request.Id, true, ct); Version(r, request.ExpectedVersion);
            if (r.DeletedAtUtc is not null || r.Batch.DeletedAtUtc is not null)
                throw new FgException(TransactionLifecycleGuard.ArchivedError(r.DeletedAtUtc ?? r.Batch.DeletedAtUtc, "This receipt")!);
            if (r.Batch.TrxType != "FG" || r.Batch.BatchStatus != (reversal ? "POSTED" : "NEW"))
                throw new FgException(reversal ? "Only POSTED FG receipts can be reversed." : "Only NEW FG receipts can be posted.");
            if (!reversal && !options.Value.PostingEnabled) throw new FgException("FG posting is disabled pending release acceptance.");
            if (reversal && (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500)) throw new FgException("Enter a rollback reason of at most 500 characters.");
            var date = reversal ? clock.Now : r.Batch.TrxDtTime;
            if (date > clock.Now) throw new FgException("Future receipt dates are not allowed.");
            if (reversal && (r.PostingId is null || !await db.StockPostings.AnyAsync(x => x.Id == r.PostingId && x.SealedAtUtc != null, ct)))
                throw new FgException("The original sealed posting is missing.");
            if (db.Database.IsSqlServer())
                await db.ProductionWorkOrders.FromSqlInterpolated($"SELECT * FROM dbo.PrWorkOrder WITH (UPDLOCK,HOLDLOCK) WHERE UID={r.WorkOrderId}").SingleAsync(ct);
            var pools = new Dictionary<long, ProductionBalLot>();
            foreach (var sourceId in r.Sources.Select(x => x.ProductionBalLotId).Distinct().Order())
            {
                var source = db.Database.IsSqlServer()
                    ? await db.ProductionBalLots.FromSqlInterpolated($"SELECT * FROM dbo.PrProductionBalLot WITH (UPDLOCK,HOLDLOCK) WHERE UID={sourceId}").SingleAsync(ct)
                    : await db.ProductionBalLots.SingleAsync(x => x.Uid == sourceId, ct);
                if (source.CompanyCode != scope.CompanyCode || source.BranchCode != scope.BranchCode || source.WorkOrderId != r.WorkOrderId) throw new FgException("Source tenant or Work Order mismatch.");
                pools.Add(sourceId, source);
            }
            if (!reversal)
            {
                foreach (var pool in pools.Values)
                {
                    if (!await EligibleSources(db, scope).AnyAsync(x => x.Uid == pool.Uid, ct)) throw new FgException($"Source {pool.Uid} is no longer eligible.");
                    if (await SourceReadinessAsync(db, pool, ct) is { } error) throw new FgException(error);
                    if (pool.LastStockEventEffectiveAt > date || pool.LastMovementDate > date) throw new FgException("Receipt date precedes a source stock event.");
                }
            }
            var snapshot = StockPostingFingerprint.Create(new { request.Id, Version = Convert.ToBase64String(request.ExpectedVersion), Command = commandType, request.Reason },
                new { r.BatchId, r.WorkOrderId, r.DocumentRevision, EffectiveAt = date, Lines = r.Sources.OrderBy(x => x.Id).Select(s => new {
                    s.Id, s.ProductionBalLotId, s.RequestedQty, s.BaseQty, s.SourceUom, s.DestinationUom, s.BaseUom, s.SourceFactor, s.DestinationFactor,
                    s.Detail.ToWarehouse, s.Detail.ToLocation, s.Detail.ToLotNo, s.Detail.ExpiryDate }) });
            var command = new StockPostingCommand { RequestId = request.RequestId, CommandType = commandType, SourceModule = "PRODUCTION",
                SourceDocumentType = ProductionDocumentTypes.FinishedGoodReceipt, SourceDocumentId = r.BatchId.ToString(), SourceDocumentNo = r.Batch.BatchNo.ToString(),
                DocumentRevision = r.DocumentRevision, PostingRole = reversal ? "REVERSAL" : "PRIMARY", EffectiveAt = date, Evidence = snapshot,
                ReversesPostingId = reversal ? r.PostingId : null, ReasonText = reversal ? request.Reason : null };
            var begin = await coordinator.BeginInTransactionAsync(db, command, ct);
            if (begin.Error is not null) throw new FgException(begin.Error.Message);
            if (!begin.LedgerEnabled || begin.Context is null) throw new FgException("A compatible active V2 stock ledger is required. No legacy FG posting is permitted.");
            var context = begin.Context;
            var link = new ProductionPostingLink { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, WorkOrderId = r.WorkOrderId,
                CommandType = commandType, PostingRequestId = request.RequestId.ToString("N"), ProductionDocumentType = "FG_RECEIPT",
                ProductionDocumentNo = r.Batch.BatchNo.ToString(), InventoryBatchNo = r.Batch.BatchNo, CreatedDate = date, CreatedBy = User(scope),
                Status = ProductionPostingLinkStatuses.Pending };
            db.ProductionPostingLinks.Add(link); await db.SaveChangesAsync(ct);
            context.Posting.ProductionPostingLinkId = link.Uid;
            await stock.LockStockMastersAsync(db, scope.CompanyCode, pools.Values.Select(x => x.ItemCode).Distinct().Order(StringComparer.Ordinal), ct);
            if (reversal) await ReverseLegsAsync(context, r, pools, link, scope, ct);
            else await PostLegsAsync(context, r, pools, link, scope, ct);
            link.Status = ProductionPostingLinkStatuses.Succeeded; link.CompletedDate = date;
            r.DocumentRevision++;
            if (reversal)
            {
                r.ReversalPostingId = context.Posting.Id; r.ReversalReason = request.Reason!.Trim(); r.Batch.BatchStatus = "REVERSED";
                r.Batch.RollbackDate = date; r.Batch.RollbackBy = User(scope); r.Batch.RollbackCount++; r.Batch.RollbackOperationId = request.RequestId;
            }
            else
            {
                r.PostingId = context.Posting.Id; r.Batch.BatchStatus = "POSTED"; r.Batch.PostedDate = date;
                r.Batch.PostedBy = User(scope); r.Batch.PostedCount++; r.Batch.PostingOperationId = request.RequestId;
            }
            await coordinator.CompleteInTransactionAsync(context, ct); await tx.CommitAsync(ct);
            return IvMasterOperationResult<FinishedGoodReceiptDocument>.Ok(await MapAsync(db, r, ct));
        }
        catch (FgException e) { return Fail<FinishedGoodReceiptDocument>(e.Message, e.Code); }
        catch (StockLedgerException e) { return Fail<FinishedGoodReceiptDocument>(e.Error.Message); }
        catch (DbUpdateConcurrencyException) { return Fail<FinishedGoodReceiptDocument>("Stock or document changed. Reload and retry.", IvMasterErrorCode.Concurrency); }
        catch (InvalidOperationException e) { return Fail<FinishedGoodReceiptDocument>(e.Message); }
    }

    private async Task PostLegsAsync(StockPostingContext context, ProductionFinishedGoodReceipt r, Dictionary<long, ProductionBalLot> pools,
        ProductionPostingLink link, InventoryTenantScope scope, CancellationToken ct)
    {
        var db = context.Db; var date = context.Posting.EffectiveAt;
        var amounts = new Dictionary<long, decimal>();
        foreach (var group in r.Sources.GroupBy(x => x.ProductionBalLotId))
        {
            var pool = pools[group.Key];
            foreach (var amount in FinishedGoodReceiptMath.AllocateValue(pool.BaseQty, pool.TotalCost, group.Select(x => (x.Id, x.BaseQty)).ToArray())) amounts.Add(amount.Key, amount.Value);
        }
        var lotIds = new Dictionary<long, int?>();
        // Actual company/item/lot identity order is shared across branches; range locks cover missing lots.
        foreach (var group in r.Sources.GroupBy(x => (pools[x.ProductionBalLotId].ItemCode, Lot: x.Detail.ToLotNo ?? ""))
            .OrderBy(x => x.Key.ItemCode, StringComparer.Ordinal).ThenBy(x => x.Key.Lot, StringComparer.Ordinal))
        {
            var item = await db.IvStockMasters.SingleAsync(x => x.CompanyCode == scope.CompanyCode && x.ICode == group.Key.ItemCode, ct);
            if (!item.IsActive || !item.StockControl) throw new FgException("Destination item is inactive or not stock controlled.");
            if (!item.LotControl)
            {
                if (group.Key.Lot.Length != 0) throw new FgException("Non-lot items must use an empty lot.");
                foreach (var s in group) lotIds[s.Id] = null;
                continue;
            }
            if (group.Key.Lot.Length == 0) throw new FgException("A destination lot is required.");
            var source = pools[group.First().ProductionBalLotId];
            if (group.Any(s => !SameOrigin(source, pools[s.ProductionBalLotId]))) throw new FgException("Lines targeting one lot must share the same production origin.");
            var lot = await stock.TryLockLotAsync(db, scope.CompanyCode, item.ICode, group.Key.Lot, ct);
            var dates = group.Select(x => x.Detail.ExpiryDate?.Date).Distinct().ToArray();
            if (lot is null)
            {
                if (dates.Length != 1) throw new FgException("All lines creating a lot must agree on expiry.");
                if (dates[0] < date.Date) throw new FgException("Expiry cannot precede the receipt date.");
                lot = await stock.FindOrCreateLotAsync(db, scope.CompanyCode, item.ICode, group.Key.Lot, "FG", r.Batch.BatchNo.ToString(), date, dates[0], User(scope), cancellationToken: ct);
                db.ProductionFinishedGoodLotOriginRows.Add(new() { LotId = lot.Id, CompanyCode = scope.CompanyCode, OriginatingBranch = scope.BranchCode!,
                    WorkOrderId = source.WorkOrderId, RouteStepId = source.ProducingRouteStepId!.Value, OperationId = source.WorkOrderOperationId!.Value, PhysicalLotNo = source.PhysicalLotNo ?? source.LotNo });
                await db.SaveChangesAsync(ct);
            }
            else
            {
                var origin = await db.ProductionFinishedGoodLotOriginRows.SingleOrDefaultAsync(x => x.LotId == lot.Id, ct);
                if (!lot.IsActive || origin is null || origin.CompanyCode != scope.CompanyCode || origin.OriginatingBranch != scope.BranchCode
                    || origin.WorkOrderId != source.WorkOrderId || origin.RouteStepId != source.ProducingRouteStepId || origin.OperationId != source.WorkOrderOperationId
                    || origin.PhysicalLotNo != (source.PhysicalLotNo ?? source.LotNo)) throw new FgException($"Lot {lot.LotNo} has absent or conflicting FG origin evidence.");
                if (dates.Any(x => x.HasValue && x != lot.ExpiryDate?.Date)) throw new FgException($"Lot {lot.LotNo} has a different expiry date.");
                if (lot.ExpiryDate?.Date < date.Date) throw new FgException($"Lot {lot.LotNo} is expired on the receipt date.");
            }
            foreach (var s in group) { lotIds[s.Id] = lot.Id; s.Detail.ExpiryDate = lot.ExpiryDate?.Date; }
        }
        var destinations = new Dictionary<long, IvBalLoc>();
        foreach (var group in r.Sources.GroupBy(x => Slice(scope, x, pools[x.ProductionBalLotId])).OrderBy(x => x.Key))
        {
            var first = group.First(); await ValidateDestinationAsync(db, scope, group.Key.WhCode, group.Key.LocCode, ct);
            var balance = await stock.FindOrCreateBalLocAsync(db, group.Key, lotIds[first.Id], first.DestinationUom, User(scope), null, scope.LocationCode, ct);
            if (balance.StdUom != first.DestinationUom || group.Any(x => x.DestinationUom != first.DestinationUom)) throw new FgException("Destination standard UOM changed. Re-save the draft.");
            if (balance.TransDate > date || await db.IvTrxHistories.AnyAsync(x => (x.ToBalLocId == balance.Id || x.FromBalLocId == balance.Id) && x.TrxDtTime > date, ct)) throw new FgException("Receipt date precedes a destination stock event.");
            var quantity = group.Sum(x => x.Detail.ToStdQty ?? 0);
            var price = IvQty.Round(group.Sum(x => amounts[x.Id]) / quantity);
            db.ProductionFinishedGoodPriceSnapshotRows.Add(new() { StockPostingId = context.Posting.Id, DestinationBalanceId = balance.Id,
                PreviousUnitPrice = balance.UnitPrice, PreviousCost = balance.Cost, PreviousPriceEvidence = balance.PriceEvidence, PostedUnitPrice = price });
            balance.UnitPrice = price; balance.Cost = price; balance.PriceEvidence = $"FG_EXACT_BASE_CURRENCY:{context.Posting.Id}";
            balance.StdQty = IvQty.Round(balance.StdQty + quantity); balance.TransDate = date;
            foreach (var s in group) destinations[s.Id] = balance;
        }
        var histories = new List<IvTrxHistory>();
        foreach (var s in r.Sources.OrderBy(x => x.Id))
        {
            var pool = pools[s.ProductionBalLotId];
            var converted = FinishedGoodReceiptMath.Convert(s.RequestedQty, s.SourceFactor, s.DestinationFactor);
            if (pool.Uom != s.SourceUom || pool.BaseUom != s.BaseUom || pool.ConversionFactorToBase != s.SourceFactor
                || converted.BaseQty != s.BaseQty || converted.DestinationQty != s.Detail.ToStdQty) throw new FgException("The frozen quantity contract is inconsistent.");
            var h = new IvTrxHistory { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, BatchNo = r.Batch.BatchNo, TrxLineNo = s.Detail.TrxLineNo,
                TrxType = "FG", BatchStatus = "POSTED", TrxDtTime = date, RefNo = r.Batch.RefNo, ICode = pool.ItemCode, IDesc = pool.Description,
                ToBalLocId = destinations[s.Id].Id, ToLotId = lotIds[s.Id], ToWarehouse = s.Detail.ToWarehouse, ToLocation = s.Detail.ToLocation, ToLotNo = s.Detail.ToLotNo,
                ToStdQty = s.Detail.ToStdQty, ToStdUom = s.DestinationUom, IStatus = "ACTIVE", UnitPrice = destinations[s.Id].UnitPrice, Cost = destinations[s.Id].Cost,
                ExactTransferredValue = amounts[s.Id], ValuationStatus = "VERIFIED", EvidenceBaseQty = s.BaseQty, EvidenceBaseUom = s.BaseUom,
                PriceEvidence = "FG_EXACT_BASE_CURRENCY", CreatedDate = context.Posting.PostedAtUtc, CreatedBy = User(scope) };
            histories.Add(h); db.IvTrxHistories.Add(h); s.Detail.ToBalLocId = h.ToBalLocId; s.Detail.ToLotId = h.ToLotId;
        }
        historyWriter.StampGeneration(context, histories, r.DocumentRevision); await db.SaveChangesAsync(ct);
        var legs = r.Sources.OrderBy(x => x.Id).Select(s => new ProductionStockLeg(pools[s.ProductionBalLotId], ProductionBalLotMovementTypes.FgReceiptOut,
            s.RequestedQty, s.BaseQty, r.WorkOrderId, null, pools[s.ProductionBalLotId].WorkOrderOperationId, pools[s.ProductionBalLotId].ProducingRouteStepId,
            link.Uid, "FG_RECEIPT", r.Batch.BatchNo.ToString(), s.Id.ToString(), 0, InventoryHistoryId: histories.Single(x => x.TrxLineNo == s.Detail.TrxLineNo).Id,
            ExactTotalValue: amounts[s.Id], ValuationStatus: "VERIFIED")).ToArray();
        var movements = await production.ApplyAsync(context, legs, ct); await db.SaveChangesAsync(ct);
        await ProductionPoolValuationService.RecordAsync(context, movements, ct);
        foreach (var s in r.Sources)
        {
            var movement = movements.Single(x => x.SourceLineId == s.Id.ToString());
            var h = histories.Single(x => x.TrxLineNo == s.Detail.TrxLineNo);
            if (h.EvidenceBaseQty != movement.BaseQty || h.ExactTransferredValue != movement.TotalCost) throw new FgException("FG quantity/value conservation failed.");
            db.ProductionFinishedGoodFactRows.Add(new() { BatchId = r.BatchId, SourceId = s.Id, StockPostingId = context.Posting.Id,
                ProductionMovementId = movement.Uid, InventoryHistoryId = h.Id, DestinationBalanceId = destinations[s.Id].Id, BaseQty = s.BaseQty, TotalValue = amounts[s.Id] });
        }
    }

    private static bool SameOrigin(ProductionBalLot a, ProductionBalLot b) => a.CompanyCode == b.CompanyCode && a.BranchCode == b.BranchCode
        && a.WorkOrderId == b.WorkOrderId && a.ProducingRouteStepId == b.ProducingRouteStepId && a.WorkOrderOperationId == b.WorkOrderOperationId
        && (a.PhysicalLotNo ?? a.LotNo) == (b.PhysicalLotNo ?? b.LotNo);
    private static IvStockSliceKey Slice(InventoryTenantScope scope, ProductionFinishedGoodSource s, ProductionBalLot pool) =>
        IvStockSliceKey.Create(scope.CompanyCode, scope.BranchCode!, pool.ItemCode, s.Detail.ToWarehouse!, s.Detail.ToLocation, s.Detail.ToLotNo, "ACTIVE");

    private async Task ReverseLegsAsync(StockPostingContext context, ProductionFinishedGoodReceipt r, Dictionary<long, ProductionBalLot> pools,
        ProductionPostingLink link, InventoryTenantScope scope, CancellationToken ct)
    {
        var db = context.Db;
        var facts = await db.ProductionFinishedGoodFactRows.Where(x => x.BatchId == r.BatchId && x.StockPostingId == r.PostingId && x.ReversesFactId == null).OrderBy(x => x.SourceId).ToListAsync(ct);
        if (facts.Count != r.Sources.Count) throw new FgException("The original FG posting facts are incomplete.");
        var originalPosting = await db.StockPostings.SingleAsync(x => x.Id == r.PostingId, ct);
        var poolIds = pools.Keys.ToArray();
        var later = await (from m in db.ProductionBalLotMovements join p in db.StockPostings on m.StockPostingId equals p.Id
            where poolIds.Contains(m.ProductionBalLotId) && p.PostingSequence > originalPosting.PostingSequence && p.SealedAtUtc != null
                && m.OriginalMovementId == null && !db.StockPostings.Any(x => x.ReversesPostingId == p.Id && x.SealedAtUtc != null)
            select m).ToListAsync(ct);
        if (later.Any(m => new StockMovementRegistry().GetRequired(m.MovementType).Direction < 0)) throw new FgException("Reverse later active source-pool outflows first.");
        var histories = await db.IvTrxHistories.Where(x => facts.Select(f => f.InventoryHistoryId).Contains(x.Id)).ToListAsync(ct);
        var maxOriginalHistoryId = histories.Max(h => h.Id);
        var snapshots = await db.ProductionFinishedGoodPriceSnapshotRows.Where(x => x.StockPostingId == r.PostingId).ToListAsync(ct);
        foreach (var group in r.Sources.GroupBy(x => Slice(scope, x, pools[x.ProductionBalLotId])).OrderBy(x => x.Key))
        {
            var locked = await stock.LockBalanceSlicesAsync(db, [group.Key], ct);
            if (!locked.TryGetValue(group.Key, out var balance)) throw new FgException("The destination balance is missing.");
            var groupFacts = facts.Where(f => f.DestinationBalanceId == balance.Id).ToArray();
            if (groupFacts.Length != group.Count()) throw new FgException("Destination identity no longer matches posting evidence.");
            var laterHistory = await db.IvTrxHistories.Where(x => (x.ToBalLocId == balance.Id || x.FromBalLocId == balance.Id)
                && x.StockPostingId != r.PostingId && x.ReversesHistoryId == null
                && !db.IvTrxHistories.Any(reverse => reverse.ReversesHistoryId == x.Id)
                && (x.StockPostingId == null ? x.Id > maxOriginalHistoryId : db.StockPostings.Any(p => p.Id == x.StockPostingId && p.PostingSequence > originalPosting.PostingSequence && p.SealedAtUtc != null))).AnyAsync(ct);
            if (laterHistory) throw new FgException("Reverse later active destination stock movements first.");
            var qty = histories.Where(x => x.ToBalLocId == balance.Id).Sum(x => x.ToStdQty ?? 0);
            if (balance.StdQty < qty) throw new FgException("Insufficient destination stock for rollback.");
            var price = snapshots.Single(x => x.DestinationBalanceId == balance.Id);
            if (balance.UnitPrice != price.PostedUnitPrice || balance.Cost != price.PostedUnitPrice || balance.PriceEvidence != $"FG_EXACT_BASE_CURRENCY:{r.PostingId}")
                throw new FgException("Destination pricing changed after this receipt; rollback cannot overwrite it.");
            balance.StdQty = IvQty.Round(balance.StdQty - qty); balance.TransDate = context.Posting.EffectiveAt;
            balance.UnitPrice = price.PreviousUnitPrice; balance.Cost = price.PreviousCost; balance.PriceEvidence = price.PreviousPriceEvidence;
        }
        var reversedHistories = historyWriter.AppendReversal(context, histories, r.DocumentRevision);
        await db.SaveChangesAsync(ct);
        var originals = await db.ProductionBalLotMovements.Where(x => facts.Select(f => f.ProductionMovementId).Contains(x.Uid)).ToListAsync(ct);
        var legs = facts.Select(f => {
            var original = originals.Single(x => x.Uid == f.ProductionMovementId);
            return new ProductionStockLeg(pools[original.ProductionBalLotId], ProductionBalLotMovementTypes.FgReceiptReversal,
                original.Qty, f.BaseQty, r.WorkOrderId, null, original.WorkOrderOperationId, original.RouteStepId, link.Uid, "FG_RECEIPT", r.Batch.BatchNo.ToString(),
                f.SourceId.ToString(), 0, original.Uid, reversedHistories.Single(x => x.ReversesHistoryId == f.InventoryHistoryId).Id, f.TotalValue, original.ValuationStatus ?? "UNVALUED");
        }).ToArray();
        var movements = await production.ApplyAsync(context, legs, ct); await db.SaveChangesAsync(ct);
        await ProductionPoolValuationService.RecordAsync(context, movements, ct);
        foreach (var f in facts)
            db.ProductionFinishedGoodFactRows.Add(new() { BatchId = r.BatchId, SourceId = f.SourceId, StockPostingId = context.Posting.Id,
                ProductionMovementId = movements.Single(x => x.SourceLineId == f.SourceId.ToString()).Uid,
                InventoryHistoryId = reversedHistories.Single(x => x.ReversesHistoryId == f.InventoryHistoryId).Id,
                DestinationBalanceId = f.DestinationBalanceId, BaseQty = f.BaseQty, TotalValue = f.TotalValue, ReversesFactId = f.Id });
    }
}
