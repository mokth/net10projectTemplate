using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionMaterialIssueService
{
    public async Task<IvMasterOperationResult<int>> PeekNextBatchNoAsync(CancellationToken cancellationToken = default)
    {
        if (_runningNumbers is null)
            return IvMasterOperationResult<int>.Fail(IvMasterErrorCode.Validation, "Batch numbering is unavailable.");
        if (!await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<int>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");
        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<int>.Fail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return IvMasterOperationResult<int>.Ok(await _runningNumbers.PeekNextAsync(db, scope.CompanyCode, RunningNumberKeys.IvBatch, cancellationToken));
    }

    public Task<IvMasterOperationResult<ProductionMaterialIssueSaveResult>> CreateAsync(
        ProductionMaterialIssueSaveRequest request, CancellationToken cancellationToken = default) =>
        SaveDraftAsync(null, request, PermissionCodes.Add, cancellationToken);

    public Task<IvMasterOperationResult<ProductionMaterialIssueSaveResult>> UpdateAsync(
        int batchNo, ProductionMaterialIssueSaveRequest request, CancellationToken cancellationToken = default) =>
        SaveDraftAsync(batchNo, request, PermissionCodes.Edit, cancellationToken);

    private async Task<IvMasterOperationResult<ProductionMaterialIssueSaveResult>> SaveDraftAsync(
        int? existingBatchNo, ProductionMaterialIssueSaveRequest request, string permission, CancellationToken ct)
    {
        if (_runningNumbers is null)
            return DraftFail(IvMasterErrorCode.Validation, "Batch numbering is unavailable.");
        if (!await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access, ct)
            || !await _access.CanAsync(MenuCodes.PlanningMaterialIssue, permission, ct))
            return DraftFail(IvMasterErrorCode.AccessDenied, "Access denied.");
        var errors = ValidateSaveRequest(request, _clock.Now);
        if (errors.Count > 0)
            return IvMasterOperationResult<ProductionMaterialIssueSaveResult>.Fail(IvMasterErrorCode.Validation, "The draft is invalid.", errors);
        var scope = _tenant.TryWriteScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return DraftFail(IvMasterErrorCode.InvalidScope, "A company, branch and user scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        IvTrxBatch? batch = null;
        ProductionPostingLink? link = null;
        List<ProductionMaterialIssueLine> oldMaps = [];
        if (existingBatchNo.HasValue)
        {
            batch = await LockIssueBatchAsync(db, scope.CompanyCode, scope.BranchCode!, existingBatchNo.Value, ct);
            link = await LockIssueLinkByBatchAsync(db, scope.CompanyCode, scope.BranchCode!, existingBatchNo.Value, ct);
            if (batch is null || link is null)
                return DraftFail(IvMasterErrorCode.NotFound, "Material issue draft was not found.");
            if (batch.BatchStatus != IvBatchStatuses.New || link.Status != ProductionPostingLinkStatuses.Draft)
                return DraftFail(IvMasterErrorCode.Validation, "Only a NEW draft can be edited.");
            oldMaps = await db.ProductionMaterialIssueLines.Where(x => x.PostingLinkId == link.Uid).ToListAsync(ct);
        }

        var order = await LockWorkOrderAsync(db, scope.CompanyCode, scope.BranchCode!, request.WorkOrderNo.Trim(), ct);
        if (order is null)
            return DraftFail(IvMasterErrorCode.NotFound, "Work Order was not found.");
        if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
            return DraftFail(IvMasterErrorCode.Validation, "Work Order status does not allow material issue.");
        if (order.SnapshotRevision != request.SnapshotRevision || order.SnapshotHash != request.SnapshotHash
            || !ProductionSnapshotFormatVersions.IsFullHierarchy(order.SnapshotFormatVersion) || order.IsLegacySnapshot)
            return DraftFail(IvMasterErrorCode.Concurrency, "The Work Order snapshot is stale; recreate the draft.");
        if (existingBatchNo.HasValue && (link!.WorkOrderId != order.Uid
            || link.SnapshotRevision != order.SnapshotRevision || link.SnapshotHash != order.SnapshotHash))
            return DraftFail(IvMasterErrorCode.Concurrency, "The stored draft snapshot no longer matches the Work Order.");
        if (!await db.ProductionWorkOrderOperations.AsNoTracking().AnyAsync(x => x.Uid == request.WorkOrderOperationId && x.WorkOrderId == order.Uid, ct))
            return DraftFail(IvMasterErrorCode.Validation, "The selected operation does not belong to the Work Order.");

        var requestedIds = request.Lines.Select(x => x.WorkOrderMaterialId).OrderBy(x => x).ToArray();
        var allLockIds = requestedIds.Concat(oldMaps.Select(x => x.WorkOrderMaterialId)).Distinct().OrderBy(x => x);
        var materials = new Dictionary<long, ProductionWorkOrderMaterial>();
        foreach (var id in allLockIds)
        {
            var material = await LockMaterialAsync(db, id, ct);
            if (material is null)
                return DraftFail(IvMasterErrorCode.NotFound, "A Work Order material was not found.");
            if (requestedIds.Contains(id)) materials[id] = material;
        }
        foreach (var material in materials.Values)
        {
            if (material.WorkOrderId != order.Uid || material.WorkOrderOperationId != request.WorkOrderOperationId)
                return DraftFail(IvMasterErrorCode.Validation, "Every material must belong to the selected Work Order operation.");
            if (ValidateMaterialPolicy(material) is string policy)
                return DraftFail(IvMasterErrorCode.Validation, policy);
        }

        var facts = await db.ProductionMaterialMovements.AsNoTracking()
            .Where(x => requestedIds.Contains(x.WorkOrderMaterialId))
            .Select(x => new { x.WorkOrderMaterialId, x.MovementType, x.Qty }).ToListAsync(ct);
        var otherDrafts = await (from map in db.ProductionMaterialIssueLines.AsNoTracking()
            join draftLink in db.ProductionPostingLinks.AsNoTracking() on map.PostingLinkId equals draftLink.Uid
            join draftBatch in db.IvTrxBatches.AsNoTracking() on map.InventoryBatchId equals draftBatch.Id
            where requestedIds.Contains(map.WorkOrderMaterialId)
                && draftLink.Status == ProductionPostingLinkStatuses.Draft && draftBatch.BatchStatus == IvBatchStatuses.New
                && (!existingBatchNo.HasValue || map.InventoryBatchNo != existingBatchNo.Value)
            select new { map.WorkOrderMaterialId, map.IssueQty }).ToListAsync(ct);
        foreach (var line in request.Lines)
        {
            var material = materials[line.WorkOrderMaterialId];
            var issued = facts.Where(x => x.WorkOrderMaterialId == material.Uid && x.MovementType == ProductionMaterialMovementTypes.Issue).Sum(x => x.Qty)
                - facts.Where(x => x.WorkOrderMaterialId == material.Uid && x.MovementType == ProductionMaterialMovementTypes.IssueReversal).Sum(x => x.Qty);
            var returned = facts.Where(x => x.WorkOrderMaterialId == material.Uid && x.MovementType == ProductionMaterialMovementTypes.Return).Sum(x => x.Qty);
            var other = otherDrafts.Where(x => x.WorkOrderMaterialId == material.Uid).Sum(x => x.IssueQty);
            var remaining = IvQty.Round(Math.Max(ProductionMaterialExecutionCalc.MaxAllowedNetIssue(material.RequiredQty, material.Tolerance) - (issued - returned) - other, 0m));
            if (line.IssueQty > remaining)
                return DraftFail(IvMasterErrorCode.Validation, $"Material {material.ComponentCode} exceeds its remaining draft allowance of {remaining:n4}.");
            var expected = ProductionMaterialExecutionCalc.BaseQtyForIssueQty(line.IssueQty, material.ConversionFactorToBase);
            if (Math.Abs(IvQty.Round(line.Allocations.Sum(x => x.BaseQty)) - expected) > 0.0001m)
                return DraftFail(IvMasterErrorCode.Validation, $"Material {material.ComponentCode} allocation does not match its issue quantity.");
        }

        var allocationIds = request.Lines.SelectMany(x => x.Allocations).Select(x => x.FromBalLocId).Distinct().ToArray();
        var balances = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && allocationIds.Contains(x.Id))
            .Include(x => x.StockMaster).Include(x => x.Lot).ToDictionaryAsync(x => x.Id, ct);
        if (balances.Count != allocationIds.Length)
            return DraftFail(IvMasterErrorCode.Validation, "A selected stock balance was not found.");
        var asOf = await new InventoryAsOfStockService().GetAsync(db, scope.CompanyCode, scope.BranchCode!, allocationIds, request.TrxDateTime, ct);
        foreach (var line in request.Lines)
        foreach (var allocation in line.Allocations)
        {
            var material = materials[line.WorkOrderMaterialId];
            var balance = balances[allocation.FromBalLocId];
            if (!balance.StockMaster.IsActive || !balance.StockMaster.StockControl || balance.IStatus != IvItemStatuses.Active
                || balance.ICode != material.ComponentCode || balance.WhCode != material.WarehouseCode
                || (!string.IsNullOrWhiteSpace(material.LocationCode) && balance.LocCode != material.LocationCode))
                return DraftFail(IvMasterErrorCode.Validation, $"Stock balance {balance.Id} is not eligible for {material.ComponentCode}.");
            if (balance.StockMaster.LotControl && (balance.LotId is null || balance.Lot is null || !balance.Lot.IsActive
                || string.IsNullOrWhiteSpace(balance.LotNo) || balance.Lot.ExpiryDate?.Date < request.TrxDateTime.Date))
                return DraftFail(IvMasterErrorCode.Validation, $"Stock balance {balance.Id} has an invalid or expired lot.");
        }
        foreach (var used in request.Lines.SelectMany(x => x.Allocations).GroupBy(x => x.FromBalLocId))
            if (!asOf.TryGetValue(used.Key, out var stock) || IvQty.Round(used.Sum(x => x.BaseQty)) > stock.UsableBaseQty)
                return DraftFail(IvMasterErrorCode.Validation, $"Insufficient current/as-of stock on balance {used.Key}.");
        if (await IvPeriodCloseGuard.EnsureOpenAsync(db, scope.CompanyCode, scope.BranchCode!, request.TrxDateTime, ct) is string periodError)
            return DraftFail(IvMasterErrorCode.Validation, periodError);

        var now = _clock.Now;
        var user = scope.UserId.Length > 10 ? scope.UserId[..10] : scope.UserId;
        if (batch is null)
        {
            var batchNo = await _runningNumbers.GetNextAsync(db, scope.CompanyCode, RunningNumberKeys.IvBatch, ct);
            batch = new IvTrxBatch { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, BatchNo = batchNo,
                TrxDtTime = request.TrxDateTime, TrxType = IvTrxTypes.IssueToProduction, BatchStatus = IvBatchStatuses.New,
                RefNo = NormalizeIssueRef(request.RefNo, batchNo), Remarks = Truncate(request.Remark, 250), LocationCode = scope.LocationCode,
                CreatedDate = now, CreatedBy = user };
            link = new ProductionPostingLink { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!,
                CommandType = ProductionPostingCommandTypes.MaterialIssuePost, PostingRequestId = Guid.NewGuid().ToString("N"),
                WorkOrderId = order.Uid, ProductionDocumentType = ProductionDocumentTypes.MaterialIssue, InventoryBatchNo = batchNo,
                SnapshotRevision = order.SnapshotRevision, SnapshotHash = order.SnapshotHash, Status = ProductionPostingLinkStatuses.Draft,
                CreatedDate = now, CreatedBy = user };
            db.IvTrxBatches.Add(batch); db.ProductionPostingLinks.Add(link);
        }
        else
        {
            db.ProductionMaterialIssueLines.RemoveRange(oldMaps);
            await db.SaveChangesAsync(ct);
            var oldDetails = await db.IvTrxBatchDetails.Where(x => x.BatchId == batch.Id).ToListAsync(ct);
            db.IvTrxBatchDetails.RemoveRange(oldDetails);
            await db.SaveChangesAsync(ct);
            batch.TrxDtTime = request.TrxDateTime; batch.RefNo = NormalizeIssueRef(request.RefNo, batch.BatchNo);
            batch.Remarks = Truncate(request.Remark, 250); batch.ModifiedDate = now; batch.ModifiedBy = user;
        }

        short trxLine = 0;
        var pendingMaps = new List<(IvTrxBatchDetail Detail, ProductionWorkOrderMaterial Material, decimal IssueQty)>();
        foreach (var line in request.Lines.OrderBy(x => x.WorkOrderMaterialId))
        {
            var material = materials[line.WorkOrderMaterialId];
            var parts = ProductionMaterialExecutionCalc.AllocateIssueQty(line.IssueQty, line.Allocations.Select(x => x.BaseQty).ToList(), material.ConversionFactorToBase);
            for (var i = 0; i < line.Allocations.Count; i++)
            {
                var allocation = line.Allocations[i]; var balance = balances[allocation.FromBalLocId]; trxLine++;
                var detail = new IvTrxBatchDetail { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, BatchNo = batch.BatchNo,
                    TrxLineNo = trxLine, TrxType = IvTrxTypes.IssueToProduction, ProdCode = order.ProductCode, ProdDesc = order.ProductDescription,
                    ICode = material.ComponentCode, IDesc = material.ComponentDescription, FromBalLocId = balance.Id, FromLotId = balance.LotId,
                    FrWarehouse = balance.WhCode, FrLocation = balance.LocCode, FrLotNo = balance.LotNo, FrStdQty = IvQty.Round(allocation.BaseQty),
                    FrStdUom = material.BaseUom, IStatus = balance.IStatus, UnitPrice = balance.UnitPrice ?? 0m,
                    Remarks = Truncate(request.Remark, 250), LocationCode = scope.LocationCode };
                batch.Details.Add(detail); pendingMaps.Add((detail, material, parts[i]));
            }
        }
        await db.SaveChangesAsync(ct);
        foreach (var row in pendingMaps)
            db.ProductionMaterialIssueLines.Add(new ProductionMaterialIssueLine { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!,
                PostingLinkId = link!.Uid, InventoryBatchId = batch.Id, InventoryBatchDetailId = row.Detail.Id,
                InventoryBatchNo = batch.BatchNo, InventoryTrxLineNo = row.Detail.TrxLineNo, WorkOrderId = order.Uid,
                WorkOrderOperationId = request.WorkOrderOperationId, WorkOrderMaterialId = row.Material.Uid,
                IssueQty = row.IssueQty, BaseQty = row.Detail.FrStdQty!.Value, CreatedDate = now, CreatedBy = user });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return IvMasterOperationResult<ProductionMaterialIssueSaveResult>.Ok(new ProductionMaterialIssueSaveResult
            { BatchNo = batch.BatchNo, PostingRequestId = link!.PostingRequestId, Status = batch.BatchStatus });
    }

    private static IReadOnlyDictionary<string, string> ValidateSaveRequest(ProductionMaterialIssueSaveRequest? request, DateTime now)
    {
        var errors = new Dictionary<string, string>();
        if (request is null) { errors["Request"] = "Save request is required."; return errors; }
        if (string.IsNullOrWhiteSpace(request.WorkOrderNo)) errors[nameof(request.WorkOrderNo)] = "Work Order is required.";
        if (request.WorkOrderOperationId <= 0) errors[nameof(request.WorkOrderOperationId)] = "Operation is required.";
        if (request.SnapshotRevision <= 0 || string.IsNullOrWhiteSpace(request.SnapshotHash)) errors["Snapshot"] = "Snapshot fingerprint is required.";
        if (request.TrxDateTime == default || request.TrxDateTime > now) errors[nameof(request.TrxDateTime)] = "Transaction date/time is required and cannot be in the future.";
        if (request.Lines.Count == 0 || request.Lines.Count > ProductionMaterialIssuePostValidator.MaxMaterialLines) errors[nameof(request.Lines)] = "Provide at least one valid material line.";
        if (request.Lines.GroupBy(x => x.WorkOrderMaterialId).Any(x => x.Count() > 1)) errors[nameof(request.Lines)] = "A Work Order material may appear only once.";
        if (request.Lines.SelectMany(x => x.Allocations).Count() > short.MaxValue) errors["Allocations"] = "Too many allocation rows.";
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            if (line.WorkOrderMaterialId <= 0 || line.IssueQty <= 0m) errors[$"Lines[{i}]"] = "Material and issue quantity must be positive.";
            if (line.Allocations.Count == 0 || line.Allocations.Any(x => x.FromBalLocId <= 0 || x.BaseQty <= 0m)) errors[$"Lines[{i}].Allocations"] = "Positive allocations are required.";
            if (line.Allocations.GroupBy(x => x.FromBalLocId).Any(x => x.Count() > 1)) errors[$"Lines[{i}].Allocations"] = "A balance may appear only once per material.";
        }
        return errors;
    }

    private static string? NormalizeIssueRef(string? value, int batchNo) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Equals("AUTO", StringComparison.OrdinalIgnoreCase)
            ? batchNo.ToString() : Truncate(value.Trim(), 50);
    private static IvMasterOperationResult<ProductionMaterialIssueSaveResult> DraftFail(IvMasterErrorCode code, string message) =>
        IvMasterOperationResult<ProductionMaterialIssueSaveResult>.Fail(code, message);
    private static async Task<IvTrxBatch?> LockIssueBatchAsync(AppDbContext db, string company, string branch, int batchNo, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.IvTrxBatches.FromSqlInterpolated($@"SELECT * FROM dbo.IvTrxBatch WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode={company} AND BranchCode={branch} AND BatchNo={batchNo} AND TrxType={IvTrxTypes.IssueToProduction}").SingleOrDefaultAsync(ct)
            : await db.IvTrxBatches.SingleOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.BatchNo == batchNo && x.TrxType == IvTrxTypes.IssueToProduction, ct);
    private static async Task<ProductionPostingLink?> LockIssueLinkByBatchAsync(AppDbContext db, string company, string branch, int batchNo, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionPostingLinks.FromSqlInterpolated($@"SELECT * FROM dbo.PrProductionPostingLink WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode={company} AND BranchCode={branch} AND CommandType={ProductionPostingCommandTypes.MaterialIssuePost} AND InventoryBatchNo={batchNo}").SingleOrDefaultAsync(ct)
            : await db.ProductionPostingLinks.SingleOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.CommandType == ProductionPostingCommandTypes.MaterialIssuePost && x.InventoryBatchNo == batchNo, ct);
}
