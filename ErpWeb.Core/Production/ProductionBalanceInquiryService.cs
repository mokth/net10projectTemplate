using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed class ProductionBalanceInquiryService : IProductionBalanceInquiryService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;

    public ProductionBalanceInquiryService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
    }

    public async Task<IvMasterOperationResult<ProductionBalanceLotPage>> SearchAsync(
        ProductionBalanceLotQuery query, CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.PlanningProductionBalance, PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<ProductionBalanceLotPage>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");

        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<ProductionBalanceLotPage>.Fail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.ProductionBalLots.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);

        if (!query.IncludeZeroQty)
            q = q.Where(x => x.Qty > 0m);
        if (!string.IsNullOrWhiteSpace(query.Kind))
            q = q.Where(x => x.Kind == query.Kind.Trim());
        if (!string.IsNullOrWhiteSpace(query.WorkOrderNo))
            q = q.Where(x => x.WorkOrderNo.Contains(query.WorkOrderNo.Trim()));
        if (!string.IsNullOrWhiteSpace(query.ItemCode))
            q = q.Where(x => x.ItemCode.Contains(query.ItemCode.Trim()));
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            q = q.Where(x => x.ItemCode.Contains(term)
                || (x.Description != null && x.Description.Contains(term))
                || x.LotNo.Contains(term)
                || x.WorkOrderNo.Contains(term)
                || x.WarehouseCode.Contains(term)
                || x.LocationCode.Contains(term));
        }

        var total = await q.CountAsync(cancellationToken);
        var take = Math.Clamp(query.Take <= 0 ? 50 : query.Take, 1, 500);
        var rows = await q
            .OrderBy(x => x.Kind).ThenBy(x => x.ItemCode).ThenBy(x => x.LotNo).ThenBy(x => x.Uid)
            .Skip(Math.Max(0, query.Skip)).Take(take)
            .Select(x => new ProductionBalanceLotRow
            {
                Uid = x.Uid,
                Kind = x.Kind,
                ItemCode = x.ItemCode,
                Description = x.Description,
                LotNo = x.LotNo,
                WorkOrderNo = x.WorkOrderNo,
                WorkCentreCode = x.WorkCentreCode,
                ProcessCode = x.ProcessCode,
                Qty = x.Qty,
                Uom = x.Uom,
                NetReceivedBaseQty = (db.ProductionBalLotMovements.Where(m => m.ProductionBalLotId == x.Uid && m.MovementType == "FG_RECEIPT_OUT").Sum(m => (decimal?)m.BaseQty) ?? 0m) - (db.ProductionBalLotMovements.Where(m => m.ProductionBalLotId == x.Uid && m.MovementType == "FG_RECEIPT_REVERSAL").Sum(m => (decimal?)m.BaseQty) ?? 0m),
                PendingReceiptBaseQty = x.BalanceStage == "FG_STAGING" && x.StockStatusCode == "AVAILABLE" ? x.BaseQty : 0m,
                BaseQty = x.BaseQty,
                BaseUom = x.BaseUom,
                WarehouseCode = x.WarehouseCode,
                LocationCode = x.LocationCode,
                LastMovementDate = x.LastMovementDate,
            }).ToListAsync(cancellationToken);

        return IvMasterOperationResult<ProductionBalanceLotPage>.Ok(new ProductionBalanceLotPage
        {
            Rows = rows,
            TotalCount = total,
        });
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<ProductionBalanceLotMovementRow>>> GetMovementsAsync(
        long productionBalLotId, CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.PlanningProductionBalance, PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<IReadOnlyList<ProductionBalanceLotMovementRow>>.Fail(
                IvMasterErrorCode.AccessDenied, "Access denied.");

        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<IReadOnlyList<ProductionBalanceLotMovementRow>>.Fail(
                IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var lotExists = await db.ProductionBalLots.AsNoTracking().AnyAsync(x =>
            x.Uid == productionBalLotId
            && x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode, cancellationToken);
        if (!lotExists)
            return IvMasterOperationResult<IReadOnlyList<ProductionBalanceLotMovementRow>>.Fail(
                IvMasterErrorCode.NotFound, "Production balance lot was not found.");

        var rows = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(x => x.ProductionBalLotId == productionBalLotId)
            .OrderBy(x => x.MovementDate).ThenBy(x => x.CreatedDate).ThenBy(x => x.Uid)
            .Select(x => new ProductionBalanceLotMovementRow
            {
                Uid = x.Uid,
                FinishedGoodReceiptId = db.ProductionFinishedGoodFactRows.Where(f => f.ProductionMovementId == x.Uid).Select(f => (int?)f.BatchId).FirstOrDefault(),
                MovementType = x.MovementType,
                Qty = x.Qty,
                Uom = x.Uom,
                BaseQty = x.BaseQty,
                BaseUom = x.BaseUom,
                SignedBaseQty = 0m,
                DocumentType = x.DocumentType,
                DocumentNo = x.DocumentNo,
                MovementDate = x.MovementDate,
                CreatedDate = x.CreatedDate,
            }).ToListAsync(cancellationToken);

        var withSigned = rows.Select(x => new ProductionBalanceLotMovementRow
        {
            Uid = x.Uid,
            FinishedGoodReceiptId = x.FinishedGoodReceiptId,
            MovementType = x.MovementType,
            Qty = x.Qty,
            Uom = x.Uom,
            BaseQty = x.BaseQty,
            BaseUom = x.BaseUom,
            SignedBaseQty = ProductionBalLotSignedQty.SignedBaseQty(x.MovementType, x.BaseQty),
            DocumentType = x.DocumentType,
            DocumentNo = x.DocumentNo,
            MovementDate = x.MovementDate,
            CreatedDate = x.CreatedDate,
        }).ToList();

        return IvMasterOperationResult<IReadOnlyList<ProductionBalanceLotMovementRow>>.Ok(withSigned);
    }
}
