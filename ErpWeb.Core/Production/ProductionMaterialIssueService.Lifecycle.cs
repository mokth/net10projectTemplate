using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionMaterialIssueService
{
    public Task<IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>> DeleteAsync(
        IReadOnlyList<int> batchNos, CancellationToken cancellationToken = default) =>
        ChangeDraftsAsync(batchNos, delete: true, PermissionCodes.Delete, cancellationToken);

    public Task<IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>> CancelAsync(
        IReadOnlyList<int> batchNos, CancellationToken cancellationToken = default) =>
        ChangeDraftsAsync(batchNos, delete: false, PermissionCodes.Cancel, cancellationToken);

    private async Task<IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>> ChangeDraftsAsync(
        IReadOnlyList<int> batchNos, bool delete, string permission, CancellationToken ct)
    {
        if (!await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access, ct)
            || !await _access.CanAsync(MenuCodes.PlanningMaterialIssue, permission, ct))
            return IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");
        var scope = _tenant.TryWriteScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>.Fail(IvMasterErrorCode.InvalidScope, "A company, branch and user scope is required.");
        var items = new List<ProductionMaterialIssueBatchActionItem>();
        foreach (var batchNo in batchNos.Where(x => x > 0).Distinct())
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var batch = await LockIssueBatchAsync(db, scope.CompanyCode, scope.BranchCode!, batchNo, ct);
            var link = await LockIssueLinkByBatchAsync(db, scope.CompanyCode, scope.BranchCode!, batchNo, ct);
            if (batch is null || link is null || batch.BatchStatus != IvBatchStatuses.New || link.Status != ProductionPostingLinkStatuses.Draft)
            {
                items.Add(new() { BatchNo = batchNo, Succeeded = false, Message = "Only a NEW draft can be changed." });
                continue;
            }
            var maps = await db.ProductionMaterialIssueLines.Where(x => x.PostingLinkId == link.Uid).ToListAsync(ct);
            foreach (var materialId in maps.Select(x => x.WorkOrderMaterialId).Distinct().OrderBy(x => x))
                await LockMaterialAsync(db, materialId, ct);
            if (delete)
            {
                var details = await db.IvTrxBatchDetails.Where(x => x.BatchId == batch.Id).ToListAsync(ct);
                db.ProductionMaterialIssueLines.RemoveRange(maps);
                await db.SaveChangesAsync(ct);
                db.IvTrxBatchDetails.RemoveRange(details);
                db.ProductionPostingLinks.Remove(link);
                db.IvTrxBatches.Remove(batch);
            }
            else
            {
                batch.BatchStatus = IvBatchStatuses.Cancelled;
                batch.ModifiedDate = _clock.Now;
                batch.ModifiedBy = scope.UserId.Length > 10 ? scope.UserId[..10] : scope.UserId;
                link.Status = ProductionPostingLinkStatuses.Cancelled;
                link.CompletedDate = _clock.Now;
                link.ResultCode = "CANCELLED";
                link.ResultMessage = "Draft cancelled.";
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            items.Add(new() { BatchNo = batchNo, Succeeded = true });
        }
        var result = new ProductionMaterialIssueBatchActionResult { Batches = items,
            SucceededCount = items.Count(x => x.Succeeded), FailedCount = items.Count(x => !x.Succeeded) };
        return IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>.Ok(result);
    }

    public async Task<IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>> PostAsync(
        IReadOnlyList<int> batchNos, CancellationToken cancellationToken = default)
    {
        if (_stockPosting is null || _inventoryPosting is null)
            return IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>.Fail(IvMasterErrorCode.Validation, "Posting dependencies are unavailable.");
        if (!await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access, cancellationToken)
            || !await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Post, cancellationToken))
            return IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");
        var selected = batchNos.Where(x => x > 0).Distinct().ToArray();
        if (selected.Length == 0 || selected.Length > IvPostingLimits.MaxPostSelection)
            return IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>.Fail(IvMasterErrorCode.Validation, $"Select between 1 and {IvPostingLimits.MaxPostSelection} batches.");
        var scope = _tenant.TryWriteScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>.Fail(IvMasterErrorCode.InvalidScope, "A company, branch and user scope is required.");
        var results = new List<ProductionMaterialIssueBatchActionItem>();
        foreach (var batchNo in selected)
        {
            var error = await PostDraftBatchAsync(scope.CompanyCode, scope.BranchCode!, scope.UserId, batchNo, cancellationToken);
            results.Add(new() { BatchNo = batchNo, Succeeded = error is null, Message = error });
        }
        return IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>.Ok(new ProductionMaterialIssueBatchActionResult
        {
            Batches = results, SucceededCount = results.Count(x => x.Succeeded), FailedCount = results.Count(x => !x.Succeeded)
        });
    }

    private async Task<string?> PostDraftBatchAsync(string company, string branch, string userId, int batchNo, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var batch = await LockIssueBatchAsync(db, company, branch, batchNo, ct);
            var link = await LockIssueLinkByBatchAsync(db, company, branch, batchNo, ct);
            if (batch is null || link is null || batch.BatchStatus != IvBatchStatuses.New || link.Status != ProductionPostingLinkStatuses.Draft)
                return "Only a NEW material issue draft can be posted.";
            var order = db.Database.IsSqlServer()
                ? await db.ProductionWorkOrders.FromSqlInterpolated($@"SELECT * FROM dbo.PrWorkOrder WITH (UPDLOCK, HOLDLOCK) WHERE UID={link.WorkOrderId}").SingleOrDefaultAsync(ct)
                : await db.ProductionWorkOrders.SingleOrDefaultAsync(x => x.Uid == link.WorkOrderId, ct);
            if (order is null || order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
                return "Work Order status does not allow material issue.";
            if (link.SnapshotRevision != order.SnapshotRevision || link.SnapshotHash != order.SnapshotHash)
                return "The Work Order snapshot changed after this draft was saved.";
            var maps = await db.ProductionMaterialIssueLines.Where(x => x.PostingLinkId == link.Uid).OrderBy(x => x.InventoryTrxLineNo).ToListAsync(ct);
            var details = await db.IvTrxBatchDetails.Where(x => x.BatchId == batch.Id).OrderBy(x => x.TrxLineNo).ToListAsync(ct);
            if (maps.Count == 0 || maps.Count != details.Count || maps.Select(x => x.WorkOrderOperationId).Distinct().Count() != 1)
                return "The draft allocation map is incomplete or mixes operations.";
            var materialIds = maps.Select(x => x.WorkOrderMaterialId).Distinct().OrderBy(x => x).ToArray();
            var materials = new Dictionary<long, ProductionWorkOrderMaterial>();
            foreach (var id in materialIds)
            {
                var material = await LockMaterialAsync(db, id, ct);
                if (material is null || material.WorkOrderId != order.Uid || ValidateMaterialPolicy(material) is not null)
                    return "A frozen Process BOM material is no longer eligible.";
                materials[id] = material;
            }
            var facts = await db.ProductionMaterialMovements.AsNoTracking().Where(x => materialIds.Contains(x.WorkOrderMaterialId))
                .Select(x => new { x.WorkOrderMaterialId, x.MovementType, x.Qty }).ToListAsync(ct);
            var otherDrafts = await (from map in db.ProductionMaterialIssueLines.AsNoTracking()
                join otherLink in db.ProductionPostingLinks.AsNoTracking() on map.PostingLinkId equals otherLink.Uid
                join otherBatch in db.IvTrxBatches.AsNoTracking() on map.InventoryBatchId equals otherBatch.Id
                where materialIds.Contains(map.WorkOrderMaterialId) && map.InventoryBatchNo != batchNo
                    && otherLink.Status == ProductionPostingLinkStatuses.Draft && otherBatch.BatchStatus == IvBatchStatuses.New
                select new { map.WorkOrderMaterialId, map.IssueQty }).ToListAsync(ct);
            foreach (var group in maps.GroupBy(x => x.WorkOrderMaterialId))
            {
                var material = materials[group.Key];
                var issued = facts.Where(x => x.WorkOrderMaterialId == group.Key && x.MovementType == ProductionMaterialMovementTypes.Issue).Sum(x => x.Qty)
                    - facts.Where(x => x.WorkOrderMaterialId == group.Key && x.MovementType == ProductionMaterialMovementTypes.IssueReversal).Sum(x => x.Qty);
                var returned = facts.Where(x => x.WorkOrderMaterialId == group.Key && x.MovementType == ProductionMaterialMovementTypes.Return).Sum(x => x.Qty);
                var other = otherDrafts.Where(x => x.WorkOrderMaterialId == group.Key).Sum(x => x.IssueQty);
                if (issued - returned + other + group.Sum(x => x.IssueQty) > ProductionMaterialExecutionCalc.MaxAllowedNetIssue(material.RequiredQty, material.Tolerance))
                    return $"Material {material.ComponentCode} exceeds its issue tolerance.";
            }
            if (await IvPeriodCloseGuard.EnsureOpenAsync(db, company, branch, batch.TrxDtTime, ct) is string periodError)
                return periodError;
            var balanceIds = details.Select(x => x.FromBalLocId!.Value).Distinct().ToArray();
            var sliceKeys = await db.IvBalLocs.AsNoTracking().Where(x => balanceIds.Contains(x.Id))
                .Select(x => new { x.Id, Key = new IvStockSliceKey(x.CompanyCode, x.BranchCode, x.ICode, x.WhCode, x.LocCode, x.LotNo, x.IStatus) }).ToListAsync(ct);
            var locked = new Dictionary<int, IvBalLocLockResult>();
            foreach (var slice in sliceKeys.OrderBy(x => x.Key))
            {
                var balance = await _stockPosting!.LockBalLocByIdForTenantAsync(db, slice.Id, company, branch, ct);
                if (balance is null) return "A selected stock balance is unavailable.";
                locked[balance.Id] = balance;
            }
            var asOf = await new InventoryAsOfStockService().GetAsync(db, company, branch, balanceIds, batch.TrxDtTime, ct);
            foreach (var used in details.GroupBy(x => x.FromBalLocId!.Value))
                if (!asOf.TryGetValue(used.Key, out var stock) || IvQty.Round(used.Sum(x => x.FrStdQty ?? 0m)) > stock.UsableBaseQty)
                    return $"Insufficient current/as-of stock on balance {used.Key}.";
            var itemCodes = materials.Values.Select(x => x.ComponentCode).Distinct().ToArray();
            var masters = await db.IvStockMasters.AsNoTracking().Where(x => x.CompanyCode == company && itemCodes.Contains(x.ICode))
                .ToDictionaryAsync(x => x.ICode, StringComparer.OrdinalIgnoreCase, ct);
            foreach (var map in maps)
            {
                var detail = details.Single(x => x.Id == map.InventoryBatchDetailId);
                var material = materials[map.WorkOrderMaterialId];
                var balance = locked[detail.FromBalLocId!.Value];
                if (!masters.TryGetValue(material.ComponentCode, out var master) || !master.IsActive || !master.StockControl
                    || balance.ICode != material.ComponentCode || balance.WhCode != material.WarehouseCode
                    || (!string.IsNullOrWhiteSpace(material.LocationCode) && balance.LocCode != material.LocationCode)
                    || balance.IStatus != IvItemStatuses.Active)
                    return $"Stock balance {balance.Id} is no longer eligible for {material.ComponentCode}.";
                if (master.LotControl)
                {
                    var lot = balance.LotId.HasValue
                        ? await db.IvLots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == balance.LotId && x.CompanyCode == company, ct)
                        : null;
                    if (lot is null || !lot.IsActive || string.IsNullOrWhiteSpace(balance.LotNo) || lot.ExpiryDate?.Date < batch.TrxDtTime.Date)
                        return $"Stock balance {balance.Id} has an invalid or expired lot.";
                }
            }
            foreach (var detail in details)
                detail.UnitPrice = locked[detail.FromBalLocId!.Value].UnitPrice ?? 0m;
            link.Status = ProductionPostingLinkStatuses.Pending;
            var posted = await _inventoryPosting!.PostStockOutInTransactionAsync(db, company, branch,
                userId.Length > 10 ? userId[..10] : userId, batchNo, IvTrxTypes.IssueToProduction, ct);
            if (!posted.Succeeded) return posted.ErrorMessage ?? "Inventory posting failed.";
            await db.SaveChangesAsync(ct);
            var histories = await db.IvTrxHistories.Where(x => x.CompanyCode == company && x.BranchCode == branch && x.BatchNo == batchNo).ToListAsync(ct);
            if (histories.Count != details.Count) return "Inventory history did not match every draft allocation.";
            var now = _clock.Now; var user = userId.Length > 10 ? userId[..10] : userId;
            foreach (var map in maps)
            {
                var detail = details.Single(x => x.Id == map.InventoryBatchDetailId);
                var history = histories.Single(x => x.TrxLineNo == detail.TrxLineNo);
                var material = materials[map.WorkOrderMaterialId];
                db.ProductionMaterialMovements.Add(new ProductionMaterialMovement { CompanyCode = company, BranchCode = branch,
                    WorkOrderId = order.Uid, WorkOrderMaterialId = material.Uid, WorkOrderOperationId = map.WorkOrderOperationId,
                    MovementType = ProductionMaterialMovementTypes.Issue, MovementDate = batch.TrxDtTime,
                    ItemCode = material.ComponentCode, Qty = map.IssueQty, Uom = material.RequiredUom!, BaseQty = map.BaseQty,
                    BaseUom = material.BaseUom!, ConversionFactorToBase = material.ConversionFactorToBase,
                    WarehouseCode = history.FrWarehouse ?? "", LocationCode = history.FrLocation ?? "", LotNo = history.FrLotNo ?? "",
                    LotId = history.FromLotId, FromBalLocId = history.FromBalLocId!.Value, ItemStatus = history.IStatus ?? "",
                    InventoryBatchId = batch.Id, InventoryBatchNo = batchNo, InventoryBatchDetailId = detail.Id,
                    InventoryTrxLineNo = detail.TrxLineNo, InventoryHistoryId = history.Id,
                    InventoryPostingOperationId = posted.OperationId?.ToString("N"), UnitCost = history.UnitPrice ?? 0m,
                    TotalCost = IvQty.Round(map.BaseQty * (history.UnitPrice ?? 0m)), PostingLinkId = link.Uid,
                    Remarks = batch.Remarks, CreatedDate = now, CreatedBy = user });
            }
            await db.SaveChangesAsync(ct);
            var allFacts = await db.ProductionMaterialMovements.AsNoTracking().Where(x => materialIds.Contains(x.WorkOrderMaterialId))
                .Select(x => new { x.WorkOrderMaterialId, x.MovementType, x.Qty }).ToListAsync(ct);
            foreach (var material in materials.Values)
            {
                var rows = allFacts.Where(x => x.WorkOrderMaterialId == material.Uid);
                material.IssuedQty = IvQty.Round(rows.Where(x => x.MovementType == ProductionMaterialMovementTypes.Issue).Sum(x => x.Qty)
                    - rows.Where(x => x.MovementType == ProductionMaterialMovementTypes.IssueReversal).Sum(x => x.Qty));
                material.ReturnedQty = IvQty.Round(rows.Where(x => x.MovementType == ProductionMaterialMovementTypes.Return).Sum(x => x.Qty));
                material.ConsumedQty = IvQty.Round(rows.Where(x => x.MovementType == ProductionMaterialMovementTypes.Consume).Sum(x => x.Qty));
                material.ModifiedDate = now; material.ModifiedBy = user;
            }
            var fromStatus = order.Status;
            if (order.Status == ProductionWorkOrderStatuses.Released) order.Status = ProductionWorkOrderStatuses.InProgress;
            order.ModifiedDate = now; order.ModifiedBy = user;
            db.ProductionAuditEvents.Add(new ProductionAuditEvent { WorkOrderId = order.Uid, EventType = ProductionAuditEventTypes.MaterialIssued,
                FromStatus = fromStatus, ToStatus = order.Status, SnapshotRevision = order.SnapshotRevision,
                Reason = $"Issue to Production batch {batchNo}", OccurredDate = now, ActorUserId = user });
            link.PostingOperationId = posted.OperationId?.ToString("N"); link.Status = ProductionPostingLinkStatuses.Succeeded;
            link.ResultCode = "OK"; link.ResultMessage = $"Posted IP batch {batchNo}."; link.CompletedDate = now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return null;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            await tx.RollbackAsync(ct);
            return "Posting conflicted with another change; reload and retry.";
        }
    }
}
