using ErpWeb.Core.Inventory;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed class ProductionMaterialStockAvailabilityReader : IProductionMaterialStockAvailabilityReader
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly ICurrentDateService _clock;
    private readonly IInventoryAsOfStockService _asOfStock;
    private readonly IProductionMaterialIssueDraftReservationReader _draftReservations;

    public ProductionMaterialStockAvailabilityReader(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        ICurrentDateService clock,
        IInventoryAsOfStockService asOfStock,
        IProductionMaterialIssueDraftReservationReader draftReservations)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _clock = clock;
        _asOfStock = asOfStock;
        _draftReservations = draftReservations;
    }

    public async Task<IvMasterOperationResult<ProductionMaterialStockAvailability>> GetAsync(
        long workOrderMaterialId,
        DateTime issueDate,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<int, decimal>? reservedBaseQtyByBalance = null,
        int? excludeInventoryBatchNo = null)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
        {
            return Fail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");
        }

        if (issueDate > _clock.Now)
        {
            return Fail(IvMasterErrorCode.Validation, "Future issue dates are not allowed.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var material = await db.ProductionWorkOrderMaterials
            .AsNoTracking()
            .Where(x => x.Uid == workOrderMaterialId
                && x.WorkOrder!.CompanyCode == scope.CompanyCode
                && x.WorkOrder.BranchCode == scope.BranchCode)
            .Select(x => new MaterialRow
            {
                WorkOrderId = x.WorkOrderId,
                ComponentCode = x.ComponentCode,
                ComponentDescription = x.ComponentDescription,
                RequiredBaseQty = x.RequiredBaseQty,
                IssueMethod = x.IssueMethod,
                SupplySource = x.SupplySource,
                WarehouseCode = x.WarehouseCode,
                BaseUom = x.BaseUom,
                ConversionFactorToBase = x.ConversionFactorToBase,
                WorkOrderStatus = x.WorkOrder!.Status
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (material is null)
        {
            return Fail(IvMasterErrorCode.NotFound, "Work Order material was not found.");
        }

        if (material.WorkOrderStatus is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
        {
            return Fail(IvMasterErrorCode.Validation, "Materials can be issued only for released or in-progress Work Orders.");
        }

        if (material.SupplySource is not (PrMaterialSupplySources.Purchased or PrMaterialSupplySources.ExternalSupply))
        {
            return Fail(IvMasterErrorCode.Validation, "This material is not supplied from warehouse stock.");
        }

        if (string.IsNullOrWhiteSpace(material.WarehouseCode))
        {
            return Fail(IvMasterErrorCode.Validation, "The Work Order material has no source warehouse.");
        }

        if (string.IsNullOrWhiteSpace(material.BaseUom) || material.ConversionFactorToBase <= 0m)
        {
            return Fail(IvMasterErrorCode.Validation, "The Work Order material has an invalid base-UOM conversion.");
        }

        var raw = await db.IvBalLocs
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.ICode == material.ComponentCode
                && x.WhCode == material.WarehouseCode
                && x.IStatus == IvItemStatuses.Active
                && x.StdQty > 0m
                && x.StockMaster.IsActive
                && x.StockMaster.StockControl
                && (x.StdUom == null || x.StdUom == "" || x.StdUom == material.BaseUom)
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
                PhysicalBaseQty = x.StdQty,
                BaseUom = material.BaseUom!,
                UnitPrice = x.UnitPrice,
                LotControl = x.StockMaster.LotControl
            })
            .ToListAsync(cancellationToken);

        var availability = await _asOfStock.GetAsync(
            db,
            scope.CompanyCode,
            scope.BranchCode!,
            raw.Select(x => x.FromBalLocId).ToArray(),
            issueDate,
            cancellationToken);
        var otherDraftReserved = await _draftReservations.GetReservedBaseQtyByBalanceAsync(
            db,
            scope.CompanyCode,
            scope.BranchCode!,
            workOrderMaterialId,
            issueDate,
            excludeInventoryBatchNo,
            cancellationToken);

        foreach (var row in raw)
        {
            if (!availability.TryGetValue(row.FromBalLocId, out var stock))
            {
                continue;
            }

            row.CurrentBaseQty = stock.CurrentBaseQty;
            row.AsOfBaseQty = stock.AsOfBaseQty;
            row.UsableBaseQty = stock.UsableBaseQty;
            row.ReservedOtherDraftBaseQty = otherDraftReserved.GetValueOrDefault(row.FromBalLocId);
            row.ReservedCurrentDocumentBaseQty = reservedBaseQtyByBalance is not null
                && reservedBaseQtyByBalance.TryGetValue(row.FromBalLocId, out var reservedQty)
                ? IvQty.Round(Math.Max(reservedQty, 0m))
                : 0m;
            row.AvailableBaseQty = IvQty.Round(Math.Max(
                stock.UsableBaseQty
                    - row.ReservedOtherDraftBaseQty
                    - row.ReservedCurrentDocumentBaseQty,
                0m));
        }

        var candidates = raw
            .OrderBy(x => x.LotControl && x.ExpiryDate is null ? 1 : 0)
            .ThenBy(x => x.LotControl ? x.ExpiryDate : null)
            .ThenBy(x => x.StockDate)
            .ThenBy(x => x.LotControl ? x.LotNo : string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.FromBalLocId)
            .Select(Map)
            .ToList();

        return IvMasterOperationResult<ProductionMaterialStockAvailability>.Ok(new()
        {
            WorkOrderMaterialId = workOrderMaterialId,
            WorkOrderId = material.WorkOrderId,
            ComponentCode = material.ComponentCode,
            ComponentDescription = material.ComponentDescription,
            RequiredBaseQty = material.RequiredBaseQty,
            WarehouseCode = material.WarehouseCode,
            BaseUom = material.BaseUom,
            ConversionFactorToBase = material.ConversionFactorToBase,
            IssueMethod = material.IssueMethod,
            SupplySource = material.SupplySource,
            WorkOrderStatus = material.WorkOrderStatus,
            Candidates = candidates
        });
    }

    private static ProductionMaterialStockCandidate Map(CandidateRow row) => new()
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
        UsableBaseQty = IvQty.Round(row.UsableBaseQty),
        ReservedOtherDraftBaseQty = IvQty.Round(row.ReservedOtherDraftBaseQty),
        ReservedCurrentDocumentBaseQty = IvQty.Round(row.ReservedCurrentDocumentBaseQty),
        AvailableToAllocateBaseQty = IvQty.Round(row.AvailableBaseQty),
        LotControl = row.LotControl,
        BaseUom = row.BaseUom,
        UnitPrice = row.UnitPrice
    };

    private static IvMasterOperationResult<ProductionMaterialStockAvailability> Fail(
        IvMasterErrorCode code,
        string message) => IvMasterOperationResult<ProductionMaterialStockAvailability>.Fail(code, message);

    private sealed class MaterialRow
    {
        public long WorkOrderId { get; init; }
        public string ComponentCode { get; init; } = string.Empty;
        public string? ComponentDescription { get; init; }
        public decimal RequiredBaseQty { get; init; }
        public string IssueMethod { get; init; } = string.Empty;
        public string SupplySource { get; init; } = string.Empty;
        public string? WarehouseCode { get; init; }
        public string? BaseUom { get; init; }
        public decimal ConversionFactorToBase { get; init; }
        public string WorkOrderStatus { get; init; } = string.Empty;
    }

    private sealed class CandidateRow
    {
        public int FromBalLocId { get; init; }
        public string Warehouse { get; init; } = string.Empty;
        public string Location { get; init; } = string.Empty;
        public int? LotId { get; init; }
        public string LotNo { get; init; } = string.Empty;
        public DateTime? ExpiryDate { get; init; }
        public DateTime? StockDate { get; init; }
        public decimal PhysicalBaseQty { get; init; }
        public decimal AvailableBaseQty { get; set; }
        public decimal CurrentBaseQty { get; set; }
        public decimal AsOfBaseQty { get; set; }
        public decimal UsableBaseQty { get; set; }
        public decimal ReservedOtherDraftBaseQty { get; set; }
        public decimal ReservedCurrentDocumentBaseQty { get; set; }
        public string BaseUom { get; init; } = string.Empty;
        public decimal? UnitPrice { get; init; }
        public bool LotControl { get; init; }
    }
}
