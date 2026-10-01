using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>
/// Read-only FEFO/FIFO proposal service. Posting must independently lock and revalidate every
/// returned balance row; a proposal is never stock authority.
/// </summary>
public sealed class ProductionMaterialAllocationService : IProductionMaterialAllocationService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;
    private readonly ICurrentDateService _clock;
    private readonly IInventoryAsOfStockService _asOfStock;

    public ProductionMaterialAllocationService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        ICurrentDateService clock,
        IInventoryAsOfStockService? asOfStock = null)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _clock = clock;
        _asOfStock = asOfStock ?? new InventoryAsOfStockService();
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<ProductionMaterialStockCandidate>>> GetStockCandidatesAsync(
        long workOrderMaterialId,
        DateTime issueDate,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(workOrderMaterialId, issueDate, cancellationToken);
        if (prepared.Error is not null)
            return IvMasterOperationResult<IReadOnlyList<ProductionMaterialStockCandidate>>.Fail(
                prepared.Error.Value.Code, prepared.Error.Value.Message);

        var data = prepared.Data!;
        return IvMasterOperationResult<IReadOnlyList<ProductionMaterialStockCandidate>>.Ok(
            data.Candidates.Select(x => Map(x, 0m, data.CanViewCost)).ToList());
    }

    public async Task<IvMasterOperationResult<ProductionMaterialAllocationResult>> AutoAllocateAsync(
        ProductionMaterialAllocationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.RequestedQty <= 0m)
            return Fail(IvMasterErrorCode.Validation, "Requested quantity must be greater than zero.");

        var prepared = await PrepareAsync(request.WorkOrderMaterialId, request.IssueDate, cancellationToken);
        if (prepared.Error is not null)
            return Fail(prepared.Error.Value.Code, prepared.Error.Value.Message);

        var data = prepared.Data!;
        var requestedQty = IvQty.Round(request.RequestedQty);
        var requestedBaseQty = ProductionMaterialExecutionCalc.BaseQtyForIssueQty(
            requestedQty, data.ConversionFactorToBase);
        var remaining = requestedBaseQty;
        var allocations = new List<ProductionMaterialStockCandidate>();

        foreach (var candidate in data.Candidates)
        {
            if (remaining <= 0m) break;
            var suggested = IvQty.Round(Math.Min(candidate.AvailableBaseQty, remaining));
            if (suggested <= 0m) continue;
            allocations.Add(Map(candidate, suggested, data.CanViewCost));
            remaining = IvQty.Round(remaining - suggested);
        }

        var allocated = IvQty.Round(allocations.Sum(x => x.SuggestedBaseQty));
        return IvMasterOperationResult<ProductionMaterialAllocationResult>.Ok(new ProductionMaterialAllocationResult
        {
            RequestedQty = requestedQty,
            RequestedBaseQty = requestedBaseQty,
            AllocatedBaseQty = allocated,
            ShortBaseQty = IvQty.Round(Math.Max(requestedBaseQty - allocated, 0m)),
            Allocations = allocations
        });
    }

    private async Task<PreparedResult> PrepareAsync(
        long materialId,
        DateTime issueDate,
        CancellationToken cancellationToken)
    {
        if (!await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access, cancellationToken))
            return PreparedResult.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");

        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return PreparedResult.Fail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");
        if (issueDate > _clock.Now)
            return PreparedResult.Fail(IvMasterErrorCode.Validation, "Future issue dates are not allowed.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var material = await db.ProductionWorkOrderMaterials
            .AsNoTracking()
            .Where(x => x.Uid == materialId
                && x.WorkOrder!.CompanyCode == scope.CompanyCode
                && x.WorkOrder.BranchCode == scope.BranchCode)
            .Select(x => new
            {
                x.ComponentCode,
                x.IssueMethod,
                x.SupplySource,
                x.WarehouseCode,
                x.LocationCode,
                x.BaseUom,
                x.ConversionFactorToBase,
                WorkOrderStatus = x.WorkOrder!.Status
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (material is null)
            return PreparedResult.Fail(IvMasterErrorCode.NotFound, "Work Order material was not found.");
        if (material.WorkOrderStatus is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
            return PreparedResult.Fail(IvMasterErrorCode.Validation, "Materials can be issued only for released or in-progress Work Orders.");
        if (!string.Equals(material.IssueMethod, PrMaterialIssueMethods.Manual, StringComparison.OrdinalIgnoreCase))
            return PreparedResult.Fail(IvMasterErrorCode.Validation, "This material is not configured for manual issue.");
        if (material.SupplySource is not (PrMaterialSupplySources.Purchased or PrMaterialSupplySources.ExternalSupply))
            return PreparedResult.Fail(IvMasterErrorCode.Validation, "This material is not supplied from warehouse stock.");
        if (string.IsNullOrWhiteSpace(material.WarehouseCode))
            return PreparedResult.Fail(IvMasterErrorCode.Validation, "The Work Order material has no source warehouse.");
        if (string.IsNullOrWhiteSpace(material.BaseUom) || material.ConversionFactorToBase <= 0m)
            return PreparedResult.Fail(IvMasterErrorCode.Validation, "The Work Order material has an invalid base-UOM conversion.");

        var raw = await db.IvBalLocs
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.ICode == material.ComponentCode
                && x.WhCode == material.WarehouseCode
                && (string.IsNullOrEmpty(material.LocationCode) || x.LocCode == material.LocationCode)
                && x.IStatus == IvItemStatuses.Active
                && x.StdQty > 0m
                && x.StockMaster.IsActive
                && x.StockMaster.StockControl
                && (!x.StockMaster.LotControl
                    || (x.LotId != null && x.Lot != null && x.Lot.IsActive && x.LotNo != ""
                        && (x.Lot.ExpiryDate == null || x.Lot.ExpiryDate.Value.Date >= issueDate.Date))))
            .Select(x => new CandidateRow
            {
                FromBalLocId = x.Id,
                Warehouse = x.WhCode,
                Location = x.LocCode,
                LotId = x.LotId,
                LotNo = x.LotNo,
                ExpiryDate = x.Lot == null ? null : x.Lot.ExpiryDate,
                StockDate = x.TransDate,
                AvailableBaseQty = x.StdQty,
                BaseUom = material.BaseUom!,
                UnitPrice = x.UnitPrice,
                LotControl = x.StockMaster.LotControl
            })
            .ToListAsync(cancellationToken);

        var availability = await _asOfStock.GetAsync(db, scope.CompanyCode, scope.BranchCode!,
            raw.Select(x => x.FromBalLocId).ToArray(), issueDate, cancellationToken);
        foreach (var row in raw)
        {
            if (!availability.TryGetValue(row.FromBalLocId, out var stock))
                continue;
            row.CurrentBaseQty = stock.CurrentBaseQty;
            row.AsOfBaseQty = stock.AsOfBaseQty;
            row.AvailableBaseQty = stock.UsableBaseQty;
        }
        raw.RemoveAll(x => x.AvailableBaseQty <= 0m);

        var ordered = raw
            .OrderBy(x => x.LotControl && x.ExpiryDate is null ? 1 : 0)
            .ThenBy(x => x.LotControl ? x.ExpiryDate : null)
            .ThenBy(x => x.StockDate)
            .ThenBy(x => x.LotControl ? x.LotNo : string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.FromBalLocId)
            .ToList();

        var canViewCost = await _access.CanAsync(
            MenuCodes.PlanningMaterialIssue, PermissionCodes.ViewCost, cancellationToken);
        return PreparedResult.Ok(material.ConversionFactorToBase, canViewCost, ordered);
    }

    private static ProductionMaterialStockCandidate Map(CandidateRow row, decimal suggested, bool canViewCost) => new()
    {
        FromBalLocId = row.FromBalLocId,
        Warehouse = row.Warehouse,
        Location = row.Location,
        LotId = row.LotId,
        LotNo = row.LotNo,
        ExpiryDate = row.ExpiryDate,
        StockDate = row.StockDate,
        AvailableBaseQty = IvQty.Round(row.AvailableBaseQty),
        CurrentBaseQty = IvQty.Round(row.CurrentBaseQty),
        AsOfBaseQty = IvQty.Round(row.AsOfBaseQty),
        UsableBaseQty = IvQty.Round(row.AvailableBaseQty),
        BaseUom = row.BaseUom,
        SuggestedBaseQty = suggested,
        UnitPrice = canViewCost ? row.UnitPrice : null
    };

    private static IvMasterOperationResult<ProductionMaterialAllocationResult> Fail(
        IvMasterErrorCode code, string message) =>
        IvMasterOperationResult<ProductionMaterialAllocationResult>.Fail(code, message);

    private sealed class CandidateRow
    {
        public int FromBalLocId { get; init; }
        public string Warehouse { get; init; } = string.Empty;
        public string Location { get; init; } = string.Empty;
        public int? LotId { get; init; }
        public string LotNo { get; init; } = string.Empty;
        public DateTime? ExpiryDate { get; init; }
        public DateTime? StockDate { get; init; }
        public decimal AvailableBaseQty { get; set; }
        public decimal CurrentBaseQty { get; set; }
        public decimal AsOfBaseQty { get; set; }
        public string BaseUom { get; init; } = string.Empty;
        public decimal? UnitPrice { get; init; }
        public bool LotControl { get; init; }
    }

    private sealed record PreparedData(
        decimal ConversionFactorToBase,
        bool CanViewCost,
        IReadOnlyList<CandidateRow> Candidates);

    private sealed record PreparedResult(
        PreparedData? Data,
        (IvMasterErrorCode Code, string Message)? Error)
    {
        public static PreparedResult Ok(decimal conversion, bool canViewCost, IReadOnlyList<CandidateRow> candidates) =>
            new(new PreparedData(conversion, canViewCost, candidates), null);
        public static PreparedResult Fail(IvMasterErrorCode code, string message) => new(null, (code, message));
    }
}
