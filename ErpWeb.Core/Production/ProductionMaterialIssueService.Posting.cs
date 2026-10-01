using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionMaterialIssueService
{
    public async Task<IvMasterOperationResult<ProductionMaterialIssuePostResult>> PostAsync(
        ProductionMaterialIssuePostRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_runningNumbers is null || _stockPosting is null || _inventoryPosting is null)
            return PostFail(IvMasterErrorCode.Validation, "Material issue posting dependencies are unavailable.");
        foreach (var permission in new[] { PermissionCodes.Access, PermissionCodes.Add, PermissionCodes.Post })
            if (!await _access.CanAsync(MenuCodes.PlanningMaterialIssue, permission, cancellationToken))
                return PostFail(IvMasterErrorCode.AccessDenied, "Access denied.");

        var validation = ProductionMaterialIssuePostValidator.Validate(request, _clock.Today);
        if (validation.Count > 0)
            return IvMasterOperationResult<ProductionMaterialIssuePostResult>.Fail(
                IvMasterErrorCode.Validation, "The posting request is invalid.", validation);
        var scope = _tenant.TryWriteScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return PostFail(IvMasterErrorCode.InvalidScope, "A company, branch and user scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var existing = await LockPostingLinkAsync(db, scope.CompanyCode, scope.BranchCode!, request.PostingRequestId, cancellationToken);
            if (existing is not null)
            {
                if (existing.Status == ProductionPostingLinkStatuses.Succeeded && existing.InventoryBatchNo.HasValue)
                {
                    var replay = await BuildResultAsync(db, existing, request.WorkOrderNo, cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return IvMasterOperationResult<ProductionMaterialIssuePostResult>.Ok(replay);
                }
                return PostFail(IvMasterErrorCode.Concurrency, "This posting request is already being processed.");
            }

            var order = await LockWorkOrderAsync(db, scope.CompanyCode, scope.BranchCode!, request.WorkOrderNo.Trim(), cancellationToken);
            if (order is null) return PostFail(IvMasterErrorCode.NotFound, "Work Order was not found.");
            if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
                return PostFail(IvMasterErrorCode.Validation, "Work Order status does not allow material issue.");
            if (order.SnapshotRevision != request.SnapshotRevision || order.SnapshotHash != request.SnapshotHash
                || order.SnapshotFormatVersion != ProductionSnapshotFormatVersions.Current || order.IsLegacySnapshot)
                return PostFail(IvMasterErrorCode.Concurrency, "The Work Order snapshot is stale; reload before posting.");

            var now = _clock.Now;
            var user = scope.UserId.Length > 10 ? scope.UserId[..10] : scope.UserId;
            var link = new ProductionPostingLink
            {
                CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!,
                CommandType = ProductionPostingCommandTypes.MaterialIssuePost,
                PostingRequestId = request.PostingRequestId, WorkOrderId = order.Uid,
                ProductionDocumentType = ProductionDocumentTypes.MaterialIssue,
                Status = ProductionPostingLinkStatuses.Pending, CreatedDate = now, CreatedBy = user
            };
            db.ProductionPostingLinks.Add(link);
            await db.SaveChangesAsync(cancellationToken);

            var requestedIds = request.Lines.Select(x => x.WorkOrderMaterialId).OrderBy(x => x).ToList();
            var materials = new Dictionary<long, ProductionWorkOrderMaterial>();
            foreach (var id in requestedIds)
            {
                var material = await LockMaterialAsync(db, id, cancellationToken);
                if (material is null || material.WorkOrderId != order.Uid)
                    return PostFail(IvMasterErrorCode.NotFound, "A Work Order material was not found.");
                var policy = ValidateMaterialPolicy(material);
                if (policy is not null) return PostFail(IvMasterErrorCode.Validation, policy);
                materials[id] = material;
            }

            var existingFacts = await db.ProductionMaterialMovements.AsNoTracking()
                .Where(x => requestedIds.Contains(x.WorkOrderMaterialId))
                .Select(x => new { x.WorkOrderMaterialId, x.MovementType, x.Qty }).ToListAsync(cancellationToken);
            foreach (var line in request.Lines)
            {
                var m = materials[line.WorkOrderMaterialId];
                var issued = existingFacts.Where(x => x.WorkOrderMaterialId == m.Uid && x.MovementType == ProductionMaterialMovementTypes.Issue).Sum(x => x.Qty)
                    - existingFacts.Where(x => x.WorkOrderMaterialId == m.Uid && x.MovementType == ProductionMaterialMovementTypes.IssueReversal).Sum(x => x.Qty);
                var returned = existingFacts.Where(x => x.WorkOrderMaterialId == m.Uid && x.MovementType == ProductionMaterialMovementTypes.Return).Sum(x => x.Qty);
                var newNet = IvQty.Round(issued - returned + line.IssueQty);
                if (newNet > ProductionMaterialExecutionCalc.MaxAllowedNetIssue(m.RequiredQty, m.Tolerance))
                    return PostFail(IvMasterErrorCode.Validation, $"Material {m.ComponentCode} exceeds its issue tolerance.");
                var expectedBase = ProductionMaterialExecutionCalc.BaseQtyForIssueQty(line.IssueQty, m.ConversionFactorToBase);
                if (Math.Abs(IvQty.Round(line.Allocations.Sum(x => x.BaseQty)) - expectedBase) > 0.0001m)
                    return PostFail(IvMasterErrorCode.Validation, $"Material {m.ComponentCode} allocation does not match its issue quantity.");
            }

            var allocationIds = request.Lines.SelectMany(x => x.Allocations).Select(x => x.FromBalLocId).Distinct().ToList();
            var slices = await db.IvBalLocs.AsNoTracking().Where(x => allocationIds.Contains(x.Id))
                .Select(x => new { x.Id, Key = new IvStockSliceKey(x.CompanyCode, x.BranchCode, x.ICode, x.WhCode, x.LocCode, x.LotNo, x.IStatus) })
                .ToListAsync(cancellationToken);
            if (slices.Count != allocationIds.Count) return PostFail(IvMasterErrorCode.Validation, "A selected stock balance was not found.");
            var locked = new Dictionary<int, IvBalLocLockResult>();
            foreach (var slice in slices.OrderBy(x => x.Key))
            {
                var row = await _stockPosting.LockBalLocByIdForTenantAsync(db, slice.Id, scope.CompanyCode, scope.BranchCode!, cancellationToken);
                if (row is null) return PostFail(IvMasterErrorCode.Validation, "A selected stock balance is outside the current tenant.");
                locked[row.Id] = row;
            }

            var requiredByBalance = request.Lines.SelectMany(x => x.Allocations)
                .GroupBy(x => x.FromBalLocId).ToDictionary(x => x.Key, x => IvQty.Round(x.Sum(y => y.BaseQty)));
            foreach (var (id, qty) in requiredByBalance)
                if (locked[id].StdQty < qty) return PostFail(IvMasterErrorCode.Validation, $"Insufficient stock on balance {id}.");

            var masters = await db.IvStockMasters.AsNoTracking().Where(x => x.CompanyCode == scope.CompanyCode && materials.Values.Select(m => m.ComponentCode).Contains(x.ICode))
                .ToDictionaryAsync(x => x.ICode, StringComparer.OrdinalIgnoreCase, cancellationToken);
            var batchNo = await _runningNumbers.GetNextAsync(db, scope.CompanyCode, RunningNumberKeys.IvBatch, cancellationToken);
            var batch = new IvTrxBatch
            {
                CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, BatchNo = batchNo,
                TrxDtTime = request.IssueDate.Date, TrxType = IvTrxTypes.IssueToProduction,
                BatchStatus = IvBatchStatuses.New, RefNo = order.WorkOrderNo,
                Remarks = Truncate(request.Remark, 250), LocationCode = scope.LocationCode,
                CreatedDate = now, CreatedBy = user
            };
            var lineMap = new Dictionary<short, (ProductionWorkOrderMaterial Material, decimal Qty)>();
            short trxLine = 0;
            foreach (var line in request.Lines.OrderBy(x => x.WorkOrderMaterialId))
            {
                var material = materials[line.WorkOrderMaterialId];
                var qtyParts = ProductionMaterialExecutionCalc.AllocateIssueQty(line.IssueQty, line.Allocations.Select(x => x.BaseQty).ToList(), material.ConversionFactorToBase);
                for (var i = 0; i < line.Allocations.Count; i++)
                {
                    var allocation = line.Allocations[i]; var bal = locked[allocation.FromBalLocId]; trxLine++;
                    if (!masters.TryGetValue(material.ComponentCode, out var master) || !master.IsActive || !master.StockControl
                        || bal.ICode != material.ComponentCode || bal.WhCode != material.WarehouseCode
                        || (!string.IsNullOrWhiteSpace(material.LocationCode) && bal.LocCode != material.LocationCode)
                        || bal.IStatus != IvItemStatuses.Active)
                        return PostFail(IvMasterErrorCode.Validation, $"Stock balance {bal.Id} is not eligible for material {material.ComponentCode}.");
                    if (master.LotControl)
                    {
                        var lot = bal.LotId.HasValue ? await db.IvLots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == bal.LotId && x.CompanyCode == scope.CompanyCode, cancellationToken) : null;
                        if (lot is null || !lot.IsActive || string.IsNullOrWhiteSpace(bal.LotNo) || lot.ExpiryDate?.Date < request.IssueDate.Date)
                            return PostFail(IvMasterErrorCode.Validation, $"Stock balance {bal.Id} has an invalid or expired lot.");
                    }
                    var detail = new IvTrxBatchDetail
                    {
                        CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, BatchNo = batchNo,
                        TrxLineNo = trxLine, TrxType = IvTrxTypes.IssueToProduction,
                        ProdCode = order.ProductCode, ProdDesc = order.ProductDescription,
                        ICode = material.ComponentCode, IDesc = material.ComponentDescription,
                        FromBalLocId = bal.Id, FromLotId = bal.LotId, FrWarehouse = bal.WhCode,
                        FrLocation = bal.LocCode, FrLotNo = bal.LotNo, FrStdQty = IvQty.Round(allocation.BaseQty),
                        FrStdUom = material.BaseUom, IStatus = bal.IStatus, UnitPrice = bal.UnitPrice ?? 0m,
                        Remarks = Truncate(request.Remark, 250), LocationCode = scope.LocationCode
                    };
                    batch.Details.Add(detail); lineMap[trxLine] = (material, qtyParts[i]);
                }
            }
            db.IvTrxBatches.Add(batch);
            await db.SaveChangesAsync(cancellationToken);
            var posted = await _inventoryPosting.PostStockOutInTransactionAsync(db, scope.CompanyCode, scope.BranchCode!, user, batchNo, IvTrxTypes.IssueToProduction, cancellationToken);
            if (!posted.Succeeded) return PostFail(IvMasterErrorCode.Validation, posted.ErrorMessage ?? "Inventory posting failed.");
            await db.SaveChangesAsync(cancellationToken);
            var histories = await db.IvTrxHistories.Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.BatchNo == batchNo).ToListAsync(cancellationToken);
            foreach (var history in histories)
            {
                var detail = batch.Details.Single(x => x.TrxLineNo == history.TrxLineNo); var mapped = lineMap[history.TrxLineNo];
                db.ProductionMaterialMovements.Add(new ProductionMaterialMovement
                {
                    CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, WorkOrderId = order.Uid,
                    WorkOrderMaterialId = mapped.Material.Uid, WorkOrderOperationId = mapped.Material.WorkOrderOperationId!.Value,
                    MovementType = ProductionMaterialMovementTypes.Issue, MovementDate = request.IssueDate.Date,
                    ItemCode = mapped.Material.ComponentCode, Qty = mapped.Qty, Uom = mapped.Material.RequiredUom!,
                    BaseQty = history.FrStdQty!.Value, BaseUom = mapped.Material.BaseUom!, ConversionFactorToBase = mapped.Material.ConversionFactorToBase,
                    WarehouseCode = history.FrWarehouse ?? "", LocationCode = history.FrLocation ?? "", LotNo = history.FrLotNo ?? "",
                    LotId = history.FromLotId, FromBalLocId = history.FromBalLocId!.Value, ItemStatus = history.IStatus ?? "",
                    InventoryBatchId = batch.Id, InventoryBatchNo = batchNo, InventoryBatchDetailId = detail.Id,
                    InventoryTrxLineNo = detail.TrxLineNo, InventoryHistoryId = history.Id,
                    InventoryPostingOperationId = posted.OperationId?.ToString("N"), UnitCost = history.UnitPrice ?? 0m,
                    TotalCost = IvQty.Round(history.FrStdQty.Value * (history.UnitPrice ?? 0m)), PostingLinkId = link.Uid,
                    Remarks = Truncate(request.Remark, 250), CreatedDate = now, CreatedBy = user
                });
            }
            foreach (var material in materials.Values)
            {
                var prior = existingFacts.Where(x => x.WorkOrderMaterialId == material.Uid);
                var added = request.Lines.Single(x => x.WorkOrderMaterialId == material.Uid).IssueQty;
                material.IssuedQty = IvQty.Round(prior.Where(x => x.MovementType == ProductionMaterialMovementTypes.Issue).Sum(x => x.Qty)
                    - prior.Where(x => x.MovementType == ProductionMaterialMovementTypes.IssueReversal).Sum(x => x.Qty) + added);
                material.ReturnedQty = IvQty.Round(prior.Where(x => x.MovementType == ProductionMaterialMovementTypes.Return).Sum(x => x.Qty));
                material.ConsumedQty = IvQty.Round(prior.Where(x => x.MovementType == ProductionMaterialMovementTypes.Consume).Sum(x => x.Qty));
                material.ModifiedDate = now; material.ModifiedBy = user;
            }
            var fromStatus = order.Status;
            if (order.Status == ProductionWorkOrderStatuses.Released) order.Status = ProductionWorkOrderStatuses.InProgress;
            order.ModifiedDate = now; order.ModifiedBy = user;
            db.ProductionAuditEvents.Add(new ProductionAuditEvent { WorkOrderId = order.Uid, EventType = ProductionAuditEventTypes.MaterialIssued,
                FromStatus = fromStatus, ToStatus = order.Status, SnapshotRevision = order.SnapshotRevision,
                Reason = $"Issue to Production batch {batchNo}", OccurredDate = now, ActorUserId = user });
            link.InventoryBatchNo = batchNo; link.PostingOperationId = posted.OperationId?.ToString("N");
            link.ProductionDocumentNo = batchNo.ToString(); link.Status = ProductionPostingLinkStatuses.Succeeded;
            link.ResultCode = "OK"; link.ResultMessage = $"Posted IP batch {batchNo}."; link.CompletedDate = now;
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<ProductionMaterialIssuePostResult>.Ok(await BuildResultAsync(db, link, order.WorkOrderNo, cancellationToken));
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PostFail(IvMasterErrorCode.Concurrency, "The posting conflicted with another request; retry with the same PostingRequestId.");
        }
    }

    private static async Task<ProductionPostingLink?> LockPostingLinkAsync(ErpWeb.Model.Data.AppDbContext db, string company, string branch, string requestId, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionPostingLinks.FromSqlInterpolated($@"SELECT * FROM dbo.PrProductionPostingLink WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode={company} AND BranchCode={branch} AND CommandType={ProductionPostingCommandTypes.MaterialIssuePost} AND PostingRequestId={requestId}").FirstOrDefaultAsync(ct)
            : await db.ProductionPostingLinks.FirstOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.CommandType == ProductionPostingCommandTypes.MaterialIssuePost && x.PostingRequestId == requestId, ct);
    private static async Task<ProductionWorkOrder?> LockWorkOrderAsync(ErpWeb.Model.Data.AppDbContext db, string company, string branch, string no, CancellationToken ct) =>
        db.Database.IsSqlServer() ? await db.ProductionWorkOrders.FromSqlInterpolated($@"SELECT * FROM dbo.PrWorkOrder WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode={company} AND BranchCode={branch} AND WorkOrderNo={no}").SingleOrDefaultAsync(ct)
        : await db.ProductionWorkOrders.SingleOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.WorkOrderNo == no, ct);
    private static async Task<ProductionWorkOrderMaterial?> LockMaterialAsync(ErpWeb.Model.Data.AppDbContext db, long id, CancellationToken ct) =>
        db.Database.IsSqlServer() ? await db.ProductionWorkOrderMaterials.FromSqlInterpolated($@"SELECT * FROM dbo.PrWorkOrderMaterial WITH (UPDLOCK, HOLDLOCK) WHERE UID={id}").SingleOrDefaultAsync(ct)
        : await db.ProductionWorkOrderMaterials.SingleOrDefaultAsync(x => x.Uid == id, ct);
    private static string? ValidateMaterialPolicy(ProductionWorkOrderMaterial m) =>
        m.WorkOrderOperationId is null || m.RequiredQty <= 0m || m.ConversionFactorToBase <= 0m || string.IsNullOrWhiteSpace(m.RequiredUom) || string.IsNullOrWhiteSpace(m.BaseUom) ? $"Material {m.ComponentCode} has an invalid execution snapshot."
        : m.IssueMethod != PrMaterialIssueMethods.Manual ? $"Material {m.ComponentCode} is not manually issuable."
        : m.SupplySource is not (PrMaterialSupplySources.Purchased or PrMaterialSupplySources.ExternalSupply) ? $"Material {m.ComponentCode} is not warehouse supplied."
        : string.IsNullOrWhiteSpace(m.WarehouseCode) ? $"Material {m.ComponentCode} has no warehouse." : null;
    private static async Task<ProductionMaterialIssuePostResult> BuildResultAsync(ErpWeb.Model.Data.AppDbContext db, ProductionPostingLink link, string woNo, CancellationToken ct)
    {
        var order = await db.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Uid == link.WorkOrderId, ct);
        var mats = await db.ProductionWorkOrderMaterials.AsNoTracking().Where(x => x.WorkOrderId == order.Uid).ToListAsync(ct);
        return new ProductionMaterialIssuePostResult { PostingRequestId = link.PostingRequestId, BatchNo = link.InventoryBatchNo ?? 0,
            PostingOperationId = link.PostingOperationId, WorkOrderNo = woNo, WorkOrderStatus = order.Status, PostedDate = link.CompletedDate ?? link.CreatedDate,
            Materials = mats.Select(x => new ProductionMaterialIssuePostedMaterial { WorkOrderMaterialId = x.Uid, IssuedQty = x.IssuedQty,
                ReturnedQty = x.ReturnedQty, NetIssuedQty = ProductionWorkOrderCalc.NetIssuedQty(x.IssuedQty, x.ReturnedQty),
                OutstandingQty = ProductionWorkOrderCalc.OpenRequirementQty(x.RequiredQty, x.IssuedQty, x.ReturnedQty) }).ToList() };
    }
    private static string? Truncate(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
    private static IvMasterOperationResult<ProductionMaterialIssuePostResult> PostFail(IvMasterErrorCode code, string message) => IvMasterOperationResult<ProductionMaterialIssuePostResult>.Fail(code, message);
}
