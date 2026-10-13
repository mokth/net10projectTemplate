using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Core.Transactions;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionStockReturnService
{
    public Task<IvMasterOperationResult<ProductionStockReturnDocument>> PostAsync(ProductionStockReturnCommand request, CancellationToken ct = default)
        => ExecuteAsync(request, reversal: false, ct);

    public Task<IvMasterOperationResult<ProductionStockReturnDocument>> RollbackAsync(ProductionStockReturnCommand request, CancellationToken ct = default)
        => ExecuteAsync(request, reversal: true, ct);

    private async Task<IvMasterOperationResult<ProductionStockReturnDocument>> ExecuteAsync(
        ProductionStockReturnCommand request, bool reversal, CancellationToken ct)
    {
        try
        {
            var scope = await ScopeAsync(reversal ? PermissionCodes.Rollback : PermissionCodes.Post, ct);
            if (request.RequestId == Guid.Empty) throw new ReturnException("A stable request ID is required.");
            if (reversal && (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500))
                throw new ReturnException("Enter a rollback reason of at most 500 characters.");
            var commandType = reversal ? ProductionPostingCommandTypes.ProductionStockReturnRollback
                : ProductionPostingCommandTypes.ProductionStockReturnPost;
            var fingerprint = StockPostingFingerprint.Create(
                new { request.Id, Version = Convert.ToBase64String(request.ExpectedVersion), Command = commandType, Reason = request.Reason?.Trim() }, new { });
            await using var db = await factory.CreateDbContextAsync(ct);
            db.ProductionStockReturnWrite = true;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, ct);
            var replay = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode && x.CommandType == commandType
                && x.RequestId == request.RequestId, ct);
            if (replay is not null)
            {
                if (replay.RequestFingerprint != fingerprint.RequestFingerprint) throw new ReturnException("The request ID was reused with different input.");
                if (replay.SealedAtUtc is null) throw new ReturnException("The existing posting is not sealed; reconciliation is required.");
                var recorded = await LoadAsync(db, scope, request.Id, false, ct);
                var replayResult = await MapAsync(db, recorded, scope, ct);
                if (reversal) replayResult.ReversalPostingId = replay.Id; else replayResult.PostingId = replay.Id;
                return IvMasterOperationResult<ProductionStockReturnDocument>.Ok(replayResult);
            }

            var r = await LoadAsync(db, scope, request.Id, true, ct);
            Version(r, request.ExpectedVersion);
            if (r.DeletedAtUtc is not null || r.Batch.DeletedAtUtc is not null)
                throw new ReturnException(TransactionLifecycleGuard.ArchivedError(r.DeletedAtUtc ?? r.Batch.DeletedAtUtc, "This return")!);
            if (r.Batch.TrxType != IvTrxTypes.ProductionStockReturn
                || r.Batch.BatchStatus != (reversal ? IvBatchStatuses.Posted : IvBatchStatuses.New))
                throw new ReturnException(reversal ? "Only a POSTED Production Stock Return can be reversed."
                    : "Only a NEW Production Stock Return can be posted.");
            if (reversal && r.PostingId is not long originalPostingId)
                throw new ReturnException("The original PRTN posting is missing.");
            var date = reversal ? clock.Now : r.Batch.TrxDtTime;
            if (date > clock.Now) throw new ReturnException("Future Production Stock Return dates are not allowed.");
            if (r.Lines.Count == 0) throw new ReturnException("The return has no lines.");

            var order = await LockWorkOrderAsync(db, scope.CompanyCode, scope.BranchCode!, r.WorkOrderId, ct);
            if (!reversal && WorkOrderError(order, r.SnapshotRevision, r.SnapshotHash) is { } snapshotError)
                throw new ReturnException(snapshotError, IvMasterErrorCode.Concurrency);
            var pools = new Dictionary<long, ProductionBalLot>();
            var authorities = new Dictionary<long, SourceAuthority>();
            foreach (var sourceId in r.Lines.Select(x => x.ProductionBalLotId).Distinct().Order())
            {
                var pool = db.Database.IsSqlServer()
                    ? await db.ProductionBalLots.FromSqlInterpolated($"SELECT * FROM dbo.PrProductionBalLot WITH (UPDLOCK,HOLDLOCK) WHERE UID={sourceId}").SingleAsync(ct)
                    : await db.ProductionBalLots.SingleAsync(x => x.Uid == sourceId, ct);
                if (pool.WorkOrderId != r.WorkOrderId || pool.CompanyCode != scope.CompanyCode || pool.BranchCode != scope.BranchCode)
                    throw new ReturnException("Return source tenant or Work Order identity changed.");
                pools.Add(sourceId, pool);
                var authority = await RequireSourceAsync(db, scope, pool, ct, date, forRollback: reversal);
                authorities.Add(sourceId, authority);
                if (!reversal)
                {
                    if (pool.LastStockEventEffectiveAt > date || pool.LastMovementDate > date)
                        throw new ReturnException($"Return date precedes a Production stock event for pool {pool.Uid}.");
                }
            }

            var snapshot = StockPostingFingerprint.Create(
                new { request.Id, Version = Convert.ToBase64String(request.ExpectedVersion), Command = commandType, Reason = request.Reason?.Trim() },
                new
                {
                    r.BatchId, r.WorkOrderId, r.DocumentRevision, r.SnapshotRevision, r.SnapshotHash,
                    EffectiveAt = date,
                    Lines = r.Lines.OrderBy(x => x.Id).Select(x => new
                    {
                        x.Id, x.ProductionBalLotId, x.SourceIssueMovementId, x.RequestedQty, x.BaseQty,
                        x.SourceUom, x.BaseUom, x.SourceFactor,
                        SourceBalanceId = authorities[x.ProductionBalLotId].InventoryBalance.Id,
                        SourceWarehouse = authorities[x.ProductionBalLotId].InventoryBalance.WhCode,
                        SourceLocation = authorities[x.ProductionBalLotId].InventoryBalance.LocCode,
                        SourceLotNo = authorities[x.ProductionBalLotId].InventoryBalance.LotNo,
                        SourceStatus = authorities[x.ProductionBalLotId].InventoryBalance.IStatus,
                        SourceStdUom = authorities[x.ProductionBalLotId].InventoryBalance.StdUom
                    })
                });
            var postingCommand = new StockPostingCommand
            {
                RequestId = request.RequestId, CommandType = commandType, SourceModule = "PRODUCTION",
                SourceDocumentType = ProductionDocumentTypes.ProductionStockReturn,
                SourceDocumentId = r.BatchId.ToString(), SourceDocumentNo = r.Batch.BatchNo.ToString(),
                DocumentRevision = r.DocumentRevision, PostingRole = reversal ? "REVERSAL" : "PRIMARY",
                EffectiveAt = date, Evidence = snapshot, ReversesPostingId = reversal ? r.PostingId : null,
                ReasonText = reversal ? request.Reason!.Trim() : null
            };
            var begin = await coordinator.BeginInTransactionAsync(db, postingCommand, ct);
            if (begin.Error is not null) throw new ReturnException(begin.Error.Message);
            if (!begin.LedgerEnabled || begin.Context is null)
                throw new ReturnException("An active V2 stock ledger is required. No legacy Production Stock Return is permitted.");
            var context = begin.Context;
            var originalLink = reversal
                ? await db.ProductionPostingLinks.SingleAsync(x => x.InventoryBatchNo == r.Batch.BatchNo
                    && x.CommandType == ProductionPostingCommandTypes.ProductionStockReturnPost, ct)
                : null;
            var link = new ProductionPostingLink
            {
                CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, WorkOrderId = r.WorkOrderId,
                CommandType = commandType, PostingRequestId = request.RequestId.ToString("N"),
                ProductionDocumentType = ProductionDocumentTypes.ProductionStockReturn,
                ProductionDocumentNo = r.Batch.BatchNo.ToString(), InventoryBatchNo = r.Batch.BatchNo,
                SnapshotRevision = r.SnapshotRevision, SnapshotHash = r.SnapshotHash,
                OriginalPostingLinkId = originalLink?.Uid, CreatedDate = date, CreatedBy = User(scope),
                Status = ProductionPostingLinkStatuses.Pending
            };
            db.ProductionPostingLinks.Add(link); await db.SaveChangesAsync(ct);
            context.Posting.ProductionPostingLinkId = link.Uid;
            await stock.LockStockMastersAsync(db, scope.CompanyCode, pools.Values.Select(x => x.ItemCode).Distinct().Order(StringComparer.Ordinal), ct);
            if (reversal) await ReverseLegsAsync(context, r, pools, authorities, link, scope, request.Reason!.Trim(), ct);
            else await PostLegsAsync(context, r, pools, authorities, link, scope, ct);

            link.Status = ProductionPostingLinkStatuses.Succeeded; link.CompletedDate = date;
            if (reversal)
            {
                r.ReversalPostingId = context.Posting.Id; r.ReversalReason = request.Reason!.Trim();
                r.Batch.BatchStatus = "REVERSED"; r.Batch.RollbackDate = date; r.Batch.RollbackBy = User(scope);
                r.Batch.RollbackCount++; r.Batch.RollbackOperationId = request.RequestId;
            }
            else
            {
                r.PostingId = context.Posting.Id; r.Batch.BatchStatus = IvBatchStatuses.Posted;
                r.Batch.PostedDate = date; r.Batch.PostedBy = User(scope); r.Batch.PostedCount++;
                r.Batch.PostingOperationId = request.RequestId;
            }
            await db.SaveChangesAsync(ct);
            await coordinator.CompleteInTransactionAsync(context, ct);
            if (context.Posting.SealedAtUtc is null) throw new ProductionPostingInvariantException("The PRTN posting was not sealed.");
            await tx.CommitAsync(ct);
            return IvMasterOperationResult<ProductionStockReturnDocument>.Ok(await MapAsync(db, r, scope, ct));
        }
        catch (ReturnException e) { return Fail<ProductionStockReturnDocument>(e.Message, e.Code); }
        catch (StockLedgerException e) { return Fail<ProductionStockReturnDocument>(e.Error.Message); }
        catch (DbUpdateConcurrencyException) { return Fail<ProductionStockReturnDocument>("Stock or return document changed. Reload and retry.", IvMasterErrorCode.Concurrency); }
        catch (InvalidOperationException e) { return Fail<ProductionStockReturnDocument>(e.Message); }
    }

    private async Task PostLegsAsync(StockPostingContext context, ProductionStockReturn r,
        IReadOnlyDictionary<long, ProductionBalLot> pools, IReadOnlyDictionary<long, SourceAuthority> authorities,
        ProductionPostingLink link, InventoryTenantScope scope, CancellationToken ct)
    {
        var db = context.Db;
        var values = new Dictionary<long, decimal>();
        foreach (var group in r.Lines.GroupBy(x => x.ProductionBalLotId))
        {
            var pool = pools[group.Key];
            foreach (var pair in FinishedGoodReceiptMath.AllocateValue(pool.BaseQty, pool.TotalCost,
                         group.Select(x => (x.Id, x.BaseQty)).ToArray()))
                values.Add(pair.Key, pair.Value);
        }
        foreach (var line in r.Lines.OrderBy(x => x.Id))
        {
            var pool = pools[line.ProductionBalLotId];
            var source = authorities[pool.Uid];
            if (pool.LastStockEventEffectiveAt > context.Posting.EffectiveAt || pool.LastMovementDate > context.Posting.EffectiveAt)
                throw new ReturnException($"Return date precedes a Production stock event for pool {pool.Uid}.");
            if (line.SourceIssueMovementId != source.Issue.Uid || line.SourceFactor != pool.ConversionFactorToBase
                || line.SourceUom != pool.Uom || line.BaseUom != pool.BaseUom
                || IvQty.Round(line.RequestedQty * line.SourceFactor) != line.BaseQty
                || IvQty.Round(line.BaseQty / line.SourceFactor) != line.RequestedQty
                || line.BaseQty > pool.BaseQty || line.BaseQty > source.Contribution.Contribution.AvailableBaseQty)
                throw new ReturnException($"Return source or quantity changed for Production pool {pool.Uid}; save the draft again.");
        }

        var snapshots = new Dictionary<int, (decimal? UnitPrice, decimal? Cost, string? PriceEvidence)>();
        foreach (var id in authorities.Values.Select(x => x.InventoryBalance.Id).Distinct().Order())
        {
            var tracked = await db.IvBalLocs.SingleAsync(x => x.Id == id && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode, ct);
            if (tracked.Id != id) throw new ReturnException("The exact original Inventory source identity changed.");
            snapshots.Add(id, (tracked.UnitPrice, tracked.Cost, tracked.PriceEvidence));
        }
        foreach (var line in r.Lines)
        {
            var pool = pools[line.ProductionBalLotId];
            line.Detail.UnitPrice = IvQty.Round(values[line.Id] / line.BaseQty);
            line.Detail.Cost = line.Detail.UnitPrice; line.Detail.CostPrice = line.Detail.UnitPrice;
            line.Detail.BaseUnitPrices = line.Detail.UnitPrice; line.Detail.PriceEvidence = "PRTN_EXACT_BASE_CURRENCY";
            line.Detail.ToWarehouse = authorities[pool.Uid].IssueHistory.FrWarehouse;
            line.Detail.ToLocation = authorities[pool.Uid].IssueHistory.FrLocation;
            line.Detail.ToLotNo = authorities[pool.Uid].IssueHistory.FrLotNo ?? "";
            line.Detail.ToStdQty = line.BaseQty; line.Detail.ToStdUom = line.BaseUom;
            line.Detail.IStatus = authorities[pool.Uid].IssueHistory.IStatus;
            line.Detail.ExpiryDate = authorities[pool.Uid].IssueHistory.FromLotId is int lotId
                ? await db.IvLots.Where(x => x.Id == lotId).Select(x => x.ExpiryDate).SingleAsync(ct) : null;
        }

        var inventory = await inventoryPosting.PostStockInInTransactionAsync(context, db, scope.CompanyCode,
            scope.BranchCode!, User(scope), r.Batch.BatchNo, IvTrxTypes.ProductionStockReturn, ct);
        if (!inventory.Succeeded) throw new ReturnException(inventory.ErrorMessage ?? "Inventory stock-in core rejected the Production Stock Return.");
        var histories = db.ChangeTracker.Entries<IvTrxHistory>().Where(x => x.State == EntityState.Added
                && x.Entity.StockPostingId == context.Posting.Id && x.Entity.TrxType == IvTrxTypes.ProductionStockReturn)
            .Select(x => x.Entity).OrderBy(x => x.TrxLineNo).ToArray();
        if (histories.Length != r.Lines.Count) throw new ReturnException("Inventory stock-in core did not create one history row per return line.");
        await db.SaveChangesAsync(ct);
        var historyByLine = histories.ToDictionary(x => x.TrxLineNo);
        foreach (var line in r.Lines)
        {
            var history = historyByLine[line.Detail.TrxLineNo];
            var source = authorities[line.ProductionBalLotId];
            if (line.Detail.ToBalLocId != source.InventoryBalance.Id
                || (source.Item.LotControl && line.Detail.ToLotId != source.InventoryBalance.LotId))
                throw new ReturnException("The Inventory stock-in core resolved a different destination than the exact original ISSUE balance.");
            history.ExactTransferredValue = values[line.Id]; history.EvidenceBaseQty = line.BaseQty;
            history.EvidenceBaseUom = line.BaseUom; history.PriceEvidence = "PRTN_EXACT_BASE_CURRENCY";
            line.Detail.ToBalLocId = history.ToBalLocId; line.Detail.ToLotId = history.ToLotId;
        }
        await db.SaveChangesAsync(ct);
        foreach (var group in r.Lines.GroupBy(x => authorities[x.ProductionBalLotId].InventoryBalance.Id))
        {
            var balance = await db.IvBalLocs.SingleAsync(x => x.Id == group.Key, ct);
            var before = snapshots[group.Key];
            db.ProductionStockReturnPriceSnapshots.Add(new ProductionStockReturnPriceSnapshot
            {
                StockPostingId = context.Posting.Id, DestinationBalanceId = group.Key,
                PreviousUnitPrice = before.UnitPrice, PreviousCost = before.Cost, PreviousPriceEvidence = before.PriceEvidence,
                PostedUnitPrice = balance.UnitPrice, PostedCost = balance.Cost, PostedPriceEvidence = balance.PriceEvidence
            });
        }
        await inventoryPosting.ValuePostingInTransactionAsync(context, ct);
        var valuationFacts = await db.StockValuationFacts.Where(x => x.StockPostingId == context.Posting.Id
            && x.Direction > 0 && x.MovementCode == "PRODUCTION_STOCK_RETURN_IN").ToListAsync(ct);
        var legs = new List<ProductionStockLeg>();
        foreach (var line in r.Lines.OrderBy(x => x.Id))
        {
            var pool = pools[line.ProductionBalLotId]; var source = authorities[pool.Uid];
            var history = historyByLine[line.Detail.TrxLineNo];
            var stockValue = StockLedgerPrecision.Money(valuationFacts.Where(x => x.InventoryHistoryId == history.Id).Sum(x => x.CostAmount));
            if (!valuationFacts.Any(x => x.InventoryHistoryId == history.Id)
                || (context.CostMethod != StockCostMethods.Standard && stockValue != values[line.Id]))
                throw new ReturnException("Inventory valuation does not reconcile to the exact Production return value.");
            var leg = new ProductionStockLeg(pool, ProductionBalLotMovementTypes.Return, line.RequestedQty, line.BaseQty,
                r.WorkOrderId, pool.WorkOrderMaterialId, source.Material.WorkOrderOperationId, null, link.Uid,
                ProductionDocumentTypes.ProductionStockReturn, r.Batch.BatchNo.ToString(), line.Id.ToString(), 0,
                InventoryHistoryId: history.Id, ExactTotalValue: values[line.Id], ValuationStatus: ProductionPoolValuationService.Verified);
            legs.Add(leg);
        }
        var movements = await production.ApplyAsync(context, legs, ct);
        await db.SaveChangesAsync(ct);
        var directives = new ProductionPoolValuationDirectives
        {
            RequiredReceiptMovementByOutboundMovementId = movements.ToDictionary(x => x.Uid,
                x => authorities.Values.Single(a => a.Pool.Uid == x.ProductionBalLotId).IssueBalanceMovement.Uid)
        };
        await ProductionPoolValuationService.RecordAsync(context, movements, directives, ct);
        var balanceMovements = movements.ToDictionary(x => x.SourceLineId!, x => x);
        var materialRows = new List<(ProductionStockReturnLine Line, ProductionBalLotMovement Movement,
            IvTrxHistory History, ProductionMaterialMovement Material)>();
        foreach (var line in r.Lines)
        {
            var source = authorities[line.ProductionBalLotId];
            var movement = balanceMovements[line.Id.ToString()];
            var history = historyByLine[line.Detail.TrxLineNo];
            var materialMovement = new ProductionMaterialMovement
            {
                CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, WorkOrderId = r.WorkOrderId,
                WorkOrderMaterialId = source.Material.Uid, WorkOrderOperationId = source.Material.WorkOrderOperationId!.Value,
                MovementType = ProductionMaterialMovementTypes.Return, MovementDate = context.Posting.EffectiveAt,
                ItemCode = movement.ItemCode ?? source.Material.ComponentCode, Qty = line.RequestedQty, Uom = line.SourceUom, BaseQty = line.BaseQty,
                BaseUom = line.BaseUom, ConversionFactorToBase = line.SourceFactor,
                WarehouseCode = source.IssueHistory.FrWarehouse ?? "", LocationCode = source.IssueHistory.FrLocation ?? "",
                LotNo = source.IssueHistory.FrLotNo ?? "", LotId = source.IssueHistory.FromLotId,
                FromBalLocId = source.InventoryBalance.Id, ItemStatus = source.IssueHistory.IStatus ?? "",
                InventoryBatchId = r.BatchId, InventoryBatchNo = r.Batch.BatchNo, InventoryBatchDetailId = line.DetailId,
                InventoryTrxLineNo = line.Detail.TrxLineNo, InventoryHistoryId = history.Id,
                InventoryPostingOperationId = context.Posting.RequestId.ToString("N"),
                ProductionBalLotId = poolUid(line), ProductionBalLotMovementId = movement.Uid,
                UnitCost = movement.UnitCost, TotalCost = movement.TotalCost, PostingLinkId = link.Uid,
                StockPostingId = context.Posting.Id, SourceLineId = line.Id.ToString(), SplitOrdinal = 0,
                OriginalMovementId = source.Issue.Uid, SourceIssueMovementId = source.Issue.Uid,
                Reason = line.Detail.Reason, Remarks = line.Detail.Remarks, CreatedDate = context.Posting.PostedAtUtc, CreatedBy = User(scope)
            };
            db.ProductionMaterialMovements.Add(materialMovement);
            materialRows.Add((line, movement, history, materialMovement));
        }
        await db.SaveChangesAsync(ct);
        foreach (var row in materialRows)
        {
            var line = row.Line; var movement = row.Movement; var history = row.History;
            var source = authorities[line.ProductionBalLotId];
            db.ProductionStockReturnFacts.Add(new ProductionStockReturnFact
            {
                BatchId = r.BatchId, ReturnLineId = line.Id, StockPostingId = context.Posting.Id,
                ProductionMovementId = movement.Uid, ProductionMaterialMovementId = row.Material.Uid,
                InventoryHistoryId = history.Id, DestinationBalanceId = source.InventoryBalance.Id,
                SourceIssueMovementId = source.Issue.Uid, BaseQty = line.BaseQty, TotalValue = values[line.Id]
            });
        }
        await db.SaveChangesAsync(ct);
        await AssertForwardInvariantsAsync(context, r, values, ct);

        long poolUid(ProductionStockReturnLine line) => line.ProductionBalLotId;
    }

    private async Task AssertForwardInvariantsAsync(StockPostingContext context, ProductionStockReturn r,
        IReadOnlyDictionary<long, decimal> values, CancellationToken ct)
    {
        var db = context.Db;
        foreach (var line in r.Lines)
        {
            var fact = await db.ProductionStockReturnFacts.SingleAsync(x => x.BatchId == r.BatchId
                && x.ReturnLineId == line.Id && x.StockPostingId == context.Posting.Id && x.ReversesFactId == null, ct);
            var movement = await db.ProductionBalLotMovements.SingleAsync(x => x.Uid == fact.ProductionMovementId, ct);
            var material = await db.ProductionMaterialMovements.SingleAsync(x => x.Uid == fact.ProductionMaterialMovementId, ct);
            var history = await db.IvTrxHistories.SingleAsync(x => x.Id == fact.InventoryHistoryId, ct);
            var pool = await db.ProductionBalLots.SingleAsync(x => x.Uid == line.ProductionBalLotId, ct);
            var valuation = await db.ProductionPoolValuationRows.SingleAsync(x => x.ProductionBalLotId == pool.Uid, ct);
            var inventoryValue = await db.StockValuationFacts.Where(x => x.InventoryHistoryId == history.Id
                && x.StockPostingId == context.Posting.Id && x.Direction > 0 && x.MovementCode == "PRODUCTION_STOCK_RETURN_IN")
                .SumAsync(x => (decimal?)x.CostAmount, ct) ?? 0m;
            if (movement.MovementType != ProductionBalLotMovementTypes.Return || movement.OriginalMovementId is not null
                || movement.BaseQty != line.BaseQty || material.MovementType != ProductionMaterialMovementTypes.Return
                || material.OriginalMovementId != line.SourceIssueMovementId || material.SourceIssueMovementId != line.SourceIssueMovementId
                || material.ProductionBalLotMovementId != movement.Uid || history.ExactTransferredValue != values[line.Id]
                || history.EvidenceBaseQty != line.BaseQty || history.ValuationStatus != StockValuationStatuses.Valued
                || fact.TotalValue != values[line.Id] || fact.TotalValue != movement.TotalCost || fact.TotalValue != material.TotalCost
                || valuation.TrackedBaseQty != pool.BaseQty || valuation.TrackedValue != pool.TotalCost
                || (context.CostMethod != StockCostMethods.Standard && StockLedgerPrecision.Money(inventoryValue) != fact.TotalValue))
                throw new ProductionPostingInvariantException($"Return line {line.Id} failed cross-ledger quantity/value lineage.");
        }
    }

    private async Task ReverseLegsAsync(StockPostingContext context, ProductionStockReturn r,
        IReadOnlyDictionary<long, ProductionBalLot> pools, IReadOnlyDictionary<long, SourceAuthority> authorities,
        ProductionPostingLink link, InventoryTenantScope scope, string reason, CancellationToken ct)
    {
        var db = context.Db;
        var facts = await db.ProductionStockReturnFacts.Where(x => x.BatchId == r.BatchId
            && x.StockPostingId == r.PostingId && x.ReversesFactId == null).OrderBy(x => x.ReturnLineId).ToListAsync(ct);
        if (facts.Count != r.Lines.Count) throw new ReturnException("The original PRTN cross-ledger facts are incomplete.");
        var originalPosting = await db.StockPostings.SingleAsync(x => x.Id == r.PostingId, ct);
        var poolIds = pools.Keys.ToArray();
        var laterMovement = await (from m in db.ProductionBalLotMovements
            join p in db.StockPostings on m.StockPostingId equals p.Id
            where poolIds.Contains(m.ProductionBalLotId) && p.PostingSequence > originalPosting.PostingSequence
                && p.SealedAtUtc != null && m.OriginalMovementId == null
                && !db.StockPostings.Any(x => x.ReversesPostingId == p.Id && x.SealedAtUtc != null)
            select m.Uid).AnyAsync(ct);
        if (laterMovement) throw new ReturnException("Reverse later active Production pool movements first.", IvMasterErrorCode.InUse);
        if (await ProductionPoolValuationService.HasActiveDependentsAsync(db,
                facts.Select(x => x.ProductionMovementId).ToArray(), ct))
            throw new ReturnException("Reverse later active Production movements that depend on this return first.", IvMasterErrorCode.InUse);
        var histories = await db.IvTrxHistories.Where(x => facts.Select(f => f.InventoryHistoryId).Contains(x.Id)).ToListAsync(ct);
        if (histories.Count != facts.Count || histories.Any(x => x.ReversesHistoryId is not null
            || x.LedgerVersion != 2 || x.StockPostingId != r.PostingId
            || db.IvTrxHistories.Any(y => y.ReversesHistoryId == x.Id)))
            throw new ReturnException("The original PRTN Inventory history is missing or already reversed.");
        var snapshots = await db.ProductionStockReturnPriceSnapshots.Where(x => x.StockPostingId == r.PostingId).ToListAsync(ct);
        if (snapshots.Count == 0) throw new ReturnException("The original PRTN operational price snapshots are missing.");
        foreach (var group in facts.GroupBy(x => x.DestinationBalanceId).OrderBy(x => x.Key))
        {
            var line = r.Lines.Single(x => group.Any(f => f.ReturnLineId == x.Id));
            var source = authorities[line.ProductionBalLotId];
            var slice = IvStockSliceKey.Create(scope.CompanyCode, scope.BranchCode!, source.InventoryBalance.ICode,
                source.InventoryBalance.WhCode, source.InventoryBalance.LocCode, source.InventoryBalance.LotNo, source.InventoryBalance.IStatus);
            var locked = await stock.LockBalanceSlicesAsync(db, [slice], ct);
            if (!locked.TryGetValue(slice, out var balance) || balance.Id != group.Key)
                throw new ReturnException("The exact PRTN destination Inventory balance is missing.");
            var snapshot = snapshots.SingleOrDefault(x => x.DestinationBalanceId == balance.Id)
                ?? throw new ReturnException("The original PRTN destination price snapshot is missing.");
            if (balance.UnitPrice != snapshot.PostedUnitPrice || balance.Cost != snapshot.PostedCost
                || balance.PriceEvidence != snapshot.PostedPriceEvidence)
                throw new ReturnException("Destination operational price evidence changed after PRTN; rollback cannot overwrite it.", IvMasterErrorCode.InUse);
            var quantity = histories.Where(x => x.ToBalLocId == balance.Id).Sum(x => x.ToStdQty ?? 0m);
            if (quantity <= 0m || balance.StdQty < quantity)
                throw new ReturnException("Insufficient Inventory quantity remains at the exact original destination.", IvMasterErrorCode.InUse);
            var maxOriginalHistoryId = await db.IvTrxHistories.Where(x => x.StockPostingId == r.PostingId)
                .MaxAsync(x => (int?)x.Id, ct) ?? 0;
            var laterHistory = await db.IvTrxHistories.Where(x => (x.ToBalLocId == balance.Id || x.FromBalLocId == balance.Id)
                && x.StockPostingId != r.PostingId && x.ReversesHistoryId == null
                && !db.IvTrxHistories.Any(y => y.ReversesHistoryId == x.Id)
                && (x.StockPostingId == null ? x.Id > maxOriginalHistoryId
                    : db.StockPostings.Any(p => p.Id == x.StockPostingId && p.PostingSequence > originalPosting.PostingSequence && p.SealedAtUtc != null)))
                .AnyAsync(ct);
            if (laterHistory) throw new ReturnException("Reverse later active Inventory movements on the PRTN destination first.", IvMasterErrorCode.InUse);
            balance.StdQty = IvQty.Round(balance.StdQty - quantity); balance.TransDate = context.Posting.EffectiveAt;
            balance.UnitPrice = snapshot.PreviousUnitPrice; balance.Cost = snapshot.PreviousCost; balance.PriceEvidence = snapshot.PreviousPriceEvidence;
        }

        var reversedHistories = historyWriter.AppendReversal(context, histories, r.DocumentRevision);
        await db.SaveChangesAsync(ct);
        await inventoryPosting.ValuePostingInTransactionAsync(context, ct);
        var originalMovements = await db.ProductionBalLotMovements.Where(x => facts.Select(f => f.ProductionMovementId).Contains(x.Uid)).ToListAsync(ct);
        var legs = facts.Select(f =>
        {
            var original = originalMovements.Single(x => x.Uid == f.ProductionMovementId);
            return new ProductionStockLeg(pools[original.ProductionBalLotId], ProductionBalLotMovementTypes.ReturnReversal,
                original.Qty, f.BaseQty, r.WorkOrderId, original.WorkOrderMaterialId, original.WorkOrderOperationId,
                original.RouteStepId, link.Uid, ProductionDocumentTypes.ProductionStockReturn, r.Batch.BatchNo.ToString(),
                f.ReturnLineId.ToString(), 0, original.Uid,
                reversedHistories.Single(x => x.ReversesHistoryId == f.InventoryHistoryId).Id,
                f.TotalValue, original.ValuationStatus ?? ProductionPoolValuationService.Unvalued);
        }).ToArray();
        var movements = await production.ApplyAsync(context, legs, ct); await db.SaveChangesAsync(ct);
        await ProductionPoolValuationService.RecordAsync(context, movements, ct);
        var reversedMaterials = new List<(ProductionStockReturnFact OriginalFact, ProductionMaterialMovement Reversal, long ProductionMovementId)>();
        foreach (var fact in facts)
        {
            var originalMaterial = await db.ProductionMaterialMovements.SingleAsync(x => x.Uid == fact.ProductionMaterialMovementId, ct);
            var reversal = new ProductionMaterialMovement
            {
                CompanyCode = originalMaterial.CompanyCode, BranchCode = originalMaterial.BranchCode,
                WorkOrderId = originalMaterial.WorkOrderId, WorkOrderMaterialId = originalMaterial.WorkOrderMaterialId,
                WorkOrderOperationId = originalMaterial.WorkOrderOperationId, MovementType = ProductionMaterialMovementTypes.ReturnReversal,
                MovementDate = context.Posting.EffectiveAt, ItemCode = originalMaterial.ItemCode, Qty = originalMaterial.Qty,
                Uom = originalMaterial.Uom, BaseQty = originalMaterial.BaseQty, BaseUom = originalMaterial.BaseUom,
                ConversionFactorToBase = originalMaterial.ConversionFactorToBase, WarehouseCode = originalMaterial.WarehouseCode,
                LocationCode = originalMaterial.LocationCode, LotNo = originalMaterial.LotNo, LotId = originalMaterial.LotId,
                FromBalLocId = originalMaterial.FromBalLocId, ItemStatus = originalMaterial.ItemStatus,
                InventoryBatchId = originalMaterial.InventoryBatchId, InventoryBatchNo = originalMaterial.InventoryBatchNo,
                InventoryBatchDetailId = originalMaterial.InventoryBatchDetailId, InventoryTrxLineNo = originalMaterial.InventoryTrxLineNo,
                InventoryHistoryId = reversedHistories.Single(x => x.ReversesHistoryId == fact.InventoryHistoryId).Id,
                ProductionBalLotId = originalMaterial.ProductionBalLotId,
                ProductionBalLotMovementId = movements.Single(x => x.OriginalMovementId == fact.ProductionMovementId).Uid,
                UnitCost = originalMaterial.UnitCost, TotalCost = originalMaterial.TotalCost, PostingLinkId = link.Uid,
                StockPostingId = context.Posting.Id, SourceLineId = fact.ReturnLineId.ToString(), SplitOrdinal = 0,
                OriginalMovementId = originalMaterial.Uid, ReversesMaterialMovementId = originalMaterial.Uid,
                SourceIssueMovementId = originalMaterial.SourceIssueMovementId, Reason = "ROLLBACK",
                Remarks = reason, CreatedDate = context.Posting.PostedAtUtc, CreatedBy = User(scope)
            };
            db.ProductionMaterialMovements.Add(reversal);
            reversedMaterials.Add((fact, reversal,
                movements.Single(x => x.OriginalMovementId == fact.ProductionMovementId).Uid));
        }

        // ProductionMaterialMovement.Uid is database-generated. Persist reversal movements first so
        // each append-only return fact can reference the assigned identity in the same transaction.
        await db.SaveChangesAsync(ct);
        foreach (var reversed in reversedMaterials)
        {
            var fact = reversed.OriginalFact;
            db.ProductionStockReturnFacts.Add(new ProductionStockReturnFact
            {
                BatchId = r.BatchId, ReturnLineId = fact.ReturnLineId, StockPostingId = context.Posting.Id,
                ProductionMovementId = reversed.ProductionMovementId,
                ProductionMaterialMovementId = reversed.Reversal.Uid,
                InventoryHistoryId = reversedHistories.Single(x => x.ReversesHistoryId == fact.InventoryHistoryId).Id,
                DestinationBalanceId = fact.DestinationBalanceId, SourceIssueMovementId = fact.SourceIssueMovementId,
                BaseQty = fact.BaseQty, TotalValue = fact.TotalValue, ReversesFactId = fact.Id
            });
        }
        await db.SaveChangesAsync(ct);
        foreach (var pool in pools.Values)
        {
            var valuation = await db.ProductionPoolValuationRows.SingleAsync(x => x.ProductionBalLotId == pool.Uid, ct);
            if (valuation.TrackedBaseQty != pool.BaseQty || valuation.TrackedValue != pool.TotalCost)
                throw new ProductionPostingInvariantException($"Rollback did not restore Production pool {pool.Uid} quantity/value.");
        }
    }
}
