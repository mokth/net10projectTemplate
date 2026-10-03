using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionMaterialIssueService
{
    public async Task<IvMasterOperationResult<ProductionMaterialIssueListPage>> SearchAsync(
        ProductionMaterialIssueListQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<ProductionMaterialIssueListPage>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return IvMasterOperationResult<ProductionMaterialIssueListPage>.Fail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");

        query ??= new ProductionMaterialIssueListQuery();
        var skip = Math.Max(0, query.Skip);
        var take = query.Take <= 0 ? 20 : Math.Min(query.Take, 500);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var rows =
            from link in db.ProductionPostingLinks.AsNoTracking()
            join order in db.ProductionWorkOrders.AsNoTracking() on link.WorkOrderId equals order.Uid
            join batch in db.IvTrxBatches.AsNoTracking()
                on new { link.CompanyCode, link.BranchCode, BatchNo = link.InventoryBatchNo ?? -1 }
                equals new { batch.CompanyCode, batch.BranchCode, batch.BatchNo } into batches
            from batch in batches.DefaultIfEmpty()
            where link.CompanyCode == scope.CompanyCode
                && link.BranchCode == scope.BranchCode
                && link.CommandType == ProductionPostingCommandTypes.MaterialIssuePost
                && link.InventoryBatchNo != null
            select new ProductionMaterialIssueListRow
            {
                BatchNo = link.InventoryBatchNo!.Value,
                IssueDate = batch != null ? batch.TrxDtTime : link.CreatedDate,
                WorkOrderNo = order.WorkOrderNo,
                ProductCode = order.ProductCode,
                ProductDescription = order.ProductDescription,
                Status = batch != null ? batch.BatchStatus : link.Status,
                PostedBy = batch != null ? batch.PostedBy : link.CreatedBy,
                PostedDate = batch != null ? batch.PostedDate : link.CompletedDate,
                RollbackDate = batch != null ? batch.RollbackDate : null
            };

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            rows = rows.Where(x => x.WorkOrderNo.Contains(term)
                || x.ProductCode.Contains(term)
                || (x.ProductDescription != null && x.ProductDescription.Contains(term)));
        }
        if (!string.IsNullOrWhiteSpace(query.WorkOrderNo))
        {
            var value = query.WorkOrderNo.Trim();
            rows = rows.Where(x => x.WorkOrderNo.Contains(value));
        }
        if (!string.IsNullOrWhiteSpace(query.ProductCode))
        {
            var value = query.ProductCode.Trim();
            rows = rows.Where(x => x.ProductCode.Contains(value));
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var value = query.Status.Trim().ToUpperInvariant();
            rows = rows.Where(x => x.Status == value);
        }
        if (query.BatchNo is > 0)
            rows = rows.Where(x => x.BatchNo == query.BatchNo.Value);
        if (query.DateFrom is DateTime from)
            rows = rows.Where(x => x.IssueDate >= from.Date);
        if (query.DateTo is DateTime to)
        {
            var exclusive = to.Date.AddDays(1);
            rows = rows.Where(x => x.IssueDate < exclusive);
        }

        rows = ApplyIssueSort(rows, query.SortField, query.SortDescending);
        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.Skip(skip).Take(take).ToListAsync(cancellationToken);
        var batchNos = page.Select(x => x.BatchNo).ToList();
        if (batchNos.Count > 0)
        {
            var mapRows = await db.ProductionMaterialIssueLines.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                    && batchNos.Contains(x.InventoryBatchNo))
                .Select(x => new { x.InventoryBatchNo, x.WorkOrderMaterialId })
                .ToListAsync(cancellationToken);
            var movements = await db.ProductionMaterialMovements.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                    && x.InventoryBatchNo.HasValue
                    && batchNos.Contains(x.InventoryBatchNo.Value)
                    && x.MovementType == ProductionMaterialMovementTypes.Issue)
                .Select(x => new { InventoryBatchNo = x.InventoryBatchNo!.Value, x.WorkOrderMaterialId })
                .ToListAsync(cancellationToken);
            var counts = movements.GroupBy(x => x.InventoryBatchNo)
                .ToDictionary(x => x.Key, x => x.Select(y => y.WorkOrderMaterialId).Distinct().Count());
            foreach (var group in mapRows.GroupBy(x => x.InventoryBatchNo))
                counts[group.Key] = group.Select(x => x.WorkOrderMaterialId).Distinct().Count();
            foreach (var row in page)
                row.LineCount = counts.GetValueOrDefault(row.BatchNo);
        }

        return IvMasterOperationResult<ProductionMaterialIssueListPage>.Ok(new ProductionMaterialIssueListPage
        {
            Rows = page,
            TotalCount = total
        });
    }

    public async Task<IvMasterOperationResult<ProductionMaterialIssueDocument>> GetAsync(
        int batchNo,
        CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<ProductionMaterialIssueDocument>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return IvMasterOperationResult<ProductionMaterialIssueDocument>.Fail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");
        if (batchNo <= 0)
            return IvMasterOperationResult<ProductionMaterialIssueDocument>.Fail(IvMasterErrorCode.Validation, "Batch number is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var link = await db.ProductionPostingLinks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                && x.CommandType == ProductionPostingCommandTypes.MaterialIssuePost
                && x.InventoryBatchNo == batchNo, cancellationToken);
        if (link is null)
            return IvMasterOperationResult<ProductionMaterialIssueDocument>.Fail(IvMasterErrorCode.NotFound, "Material issue was not found.");
        var order = await db.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Uid == link.WorkOrderId, cancellationToken);
        var batch = await db.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode && x.BatchNo == batchNo && x.TrxType == IvTrxTypes.IssueToProduction, cancellationToken);
        if (batch is null)
            return IvMasterOperationResult<ProductionMaterialIssueDocument>.Fail(IvMasterErrorCode.NotFound, "Inventory issue batch was not found.");
        var canViewCost = await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.ViewCost, cancellationToken);
        var movements = await db.ProductionMaterialMovements.AsNoTracking()
            .Where(x => x.PostingLinkId == link.Uid && x.MovementType == ProductionMaterialMovementTypes.Issue)
            .OrderBy(x => x.InventoryTrxLineNo)
            .ToListAsync(cancellationToken);
        var mapRows = await db.ProductionMaterialIssueLines.AsNoTracking()
            .Where(x => x.PostingLinkId == link.Uid)
            .OrderBy(x => x.InventoryTrxLineNo)
            .ToListAsync(cancellationToken);
        IReadOnlyList<ProductionMaterialIssueDocumentLine> documentLines;
        if (mapRows.Count > 0)
        {
            var detailIds = mapRows.Select(x => x.InventoryBatchDetailId).ToArray();
            var materialIds = mapRows.Select(x => x.WorkOrderMaterialId).Distinct().ToArray();
            var details = await db.IvTrxBatchDetails.AsNoTracking().Where(x => detailIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
            var materials = await db.ProductionWorkOrderMaterials.AsNoTracking().Where(x => materialIds.Contains(x.Uid)).ToDictionaryAsync(x => x.Uid, cancellationToken);
            var postedByDetail = movements.ToDictionary(x => x.InventoryBatchDetailId);
            documentLines = mapRows.Select(x =>
            {
                var detail = details[x.InventoryBatchDetailId];
                var material = materials[x.WorkOrderMaterialId];
                postedByDetail.TryGetValue(detail.Id, out var movement);
                var unitCost = movement?.UnitCost ?? detail.UnitPrice ?? 0m;
                return new ProductionMaterialIssueDocumentLine
                {
                    WorkOrderMaterialId = x.WorkOrderMaterialId, InventoryLineNo = x.InventoryTrxLineNo,
                    FromBalLocId = detail.FromBalLocId ?? 0,
                    ItemCode = material.ComponentCode, IssueQty = x.IssueQty, Uom = material.RequiredUom ?? string.Empty,
                    BaseQty = x.BaseQty, BaseUom = material.BaseUom ?? string.Empty,
                    Warehouse = detail.FrWarehouse ?? string.Empty, Location = detail.FrLocation ?? string.Empty,
                    LotNo = detail.FrLotNo ?? string.Empty, ItemStatus = detail.IStatus ?? string.Empty,
                    UnitCost = canViewCost ? unitCost : null, TotalCost = canViewCost ? IvQty.Round(x.BaseQty * unitCost) : null,
                    ExcessIssueReason = x.ExcessIssueReason
                };
            }).ToList();
        }
        else
        {
            documentLines = movements.Select(x => new ProductionMaterialIssueDocumentLine
            {
                WorkOrderMaterialId = x.WorkOrderMaterialId,
                InventoryLineNo = x.InventoryTrxLineNo ?? 0,
                FromBalLocId = x.FromBalLocId ?? 0,
                ItemCode = x.ItemCode, IssueQty = x.Qty, Uom = x.Uom, BaseQty = x.BaseQty,
                BaseUom = x.BaseUom, Warehouse = x.WarehouseCode, Location = x.LocationCode,
                LotNo = x.LotNo, ItemStatus = x.ItemStatus, UnitCost = canViewCost ? x.UnitCost : null,
                TotalCost = canViewCost ? x.TotalCost : null
            }).ToList();
        }

        return IvMasterOperationResult<ProductionMaterialIssueDocument>.Ok(new ProductionMaterialIssueDocument
        {
            BatchNo = batch.BatchNo,
            IssueDate = batch.TrxDtTime,
            RefNo = batch.RefNo,
            Status = batch.BatchStatus,
            WorkOrderNo = order.WorkOrderNo,
            ProductCode = order.ProductCode,
            ProductDescription = order.ProductDescription,
            PlannedQty = order.PlannedQty,
            OutputUom = order.OutputUom,
            WorkOrderId = order.Uid,
            WorkOrderOperationId = mapRows.Select(x => x.WorkOrderOperationId).FirstOrDefault(),
            SnapshotRevision = link.SnapshotRevision,
            SnapshotHash = link.SnapshotHash,
            ProductionQtyThisIssue = link.ProductionQtyThisIssue,
            Remark = batch.Remarks,
            PostedBy = batch.PostedBy,
            PostedDate = batch.PostedDate,
            RollbackDate = batch.RollbackDate,
            CanViewCost = canViewCost,
            Lines = documentLines
        });
    }

    private static IQueryable<ProductionMaterialIssueListRow> ApplyIssueSort(
        IQueryable<ProductionMaterialIssueListRow> rows,
        string? field,
        bool descending) => (field ?? string.Empty) switch
    {
        nameof(ProductionMaterialIssueListRow.BatchNo) => descending ? rows.OrderByDescending(x => x.BatchNo) : rows.OrderBy(x => x.BatchNo),
        nameof(ProductionMaterialIssueListRow.WorkOrderNo) => descending ? rows.OrderByDescending(x => x.WorkOrderNo) : rows.OrderBy(x => x.WorkOrderNo),
        nameof(ProductionMaterialIssueListRow.ProductCode) => descending ? rows.OrderByDescending(x => x.ProductCode) : rows.OrderBy(x => x.ProductCode),
        nameof(ProductionMaterialIssueListRow.Status) => descending ? rows.OrderByDescending(x => x.Status) : rows.OrderBy(x => x.Status),
        _ => descending ? rows.OrderBy(x => x.IssueDate) : rows.OrderByDescending(x => x.IssueDate).ThenByDescending(x => x.BatchNo)
    };
}
