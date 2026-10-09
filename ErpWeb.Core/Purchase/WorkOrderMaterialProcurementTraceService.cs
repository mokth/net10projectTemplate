using ErpWeb.Core.Inventory;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed class WorkOrderMaterialProcurementTraceService : IWorkOrderMaterialProcurementTraceService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly ICurrentDateService _dates;
    private readonly IProductionMaterialStockAvailabilityReader _stockAvailability;

    public WorkOrderMaterialProcurementTraceService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        ICurrentDateService dates,
        IProductionMaterialStockAvailabilityReader stockAvailability)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _dates = dates;
        _stockAvailability = stockAvailability;
    }

    public async Task<WorkOrderMaterialProcurementTraceResult> GetAsync(
        long workOrderMaterialId,
        DateTime? asOfDate = null,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
        {
            return WorkOrderMaterialProcurementTraceResult.Fail("A company and branch scope is required.");
        }

        var issueDate = (asOfDate ?? _dates.Today).Date;
        if (issueDate > _dates.Now.Date)
        {
            return WorkOrderMaterialProcurementTraceResult.Fail("The procurement trace date cannot be in the future.");
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
                BaseUom = x.BaseUom,
                RequiredBaseQty = x.RequiredBaseQty,
                SupplySource = x.SupplySource
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (material is null)
        {
            return WorkOrderMaterialProcurementTraceResult.Fail("Work Order material was not found.");
        }

        var availability = await _stockAvailability.GetAsync(
            workOrderMaterialId,
            issueDate,
            cancellationToken);
        if (!availability.Succeeded || availability.Data is null)
        {
            return WorkOrderMaterialProcurementTraceResult.Fail(
                availability.Message ?? "Material stock availability could not be read.");
        }

        var prRows = await db.PoPrDetails
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.WorkOrderMaterialId == workOrderMaterialId
                && x.Pr.Status != PoPrStatuses.Cancelled
                && x.Status != PoPrStatuses.Cancelled)
            .Select(x => new PrRow
            {
                PrNo = x.PrNo,
                PrLineNo = x.Line,
                StdQty = x.StdQty,
                StdUom = x.StdUom
            })
            .OrderBy(x => x.PrNo)
            .ThenBy(x => x.PrLineNo)
            .ToListAsync(cancellationToken);

        var prNos = prRows
            .Select(x => x.PrNo)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var poRows = prNos.Length == 0
            ? []
            : await db.PoOrderDetails
                .AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.PrNo != null
                    && prNos.Contains(x.PrNo)
                    && x.PrLineNo != null)
                .Select(x => new PoRow
                {
                    PrNo = x.PrNo!,
                    PrLineNo = x.PrLineNo!.Value,
                    PoNo = x.PoNo,
                    PoRelNo = x.PoRelNo,
                    PoOrderedStdQty = x.PoQty,
                    PoOpenStdQty = x.BalanceQty * (x.PackSz == 0m ? 1m : x.PackSz),
                    StdUom = x.StdUom,
                    EtaDate = x.EtaDate,
                    Status = x.Order.Status
                })
                .ToListAsync(cancellationToken);

        var latestRevisionByPo = poRows
            .GroupBy(x => x.PoNo, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Max(x => x.PoRelNo),
                StringComparer.OrdinalIgnoreCase);

        var traceLines = new List<WorkOrderMaterialProcurementTraceLine>();
        var openProcurement = 0m;
        var consistent = true;
        string? inconsistency = null;

        foreach (var pr in prRows)
        {
            var prUomMatches = string.Equals(
                pr.StdUom?.Trim(),
                material.BaseUom?.Trim(),
                StringComparison.OrdinalIgnoreCase);
            if (!prUomMatches)
            {
                consistent = false;
                inconsistency ??= $"PR {pr.PrNo} line {pr.PrLineNo} standard UOM does not match the Work Order material base UOM.";
            }

            var currentPoLines = poRows
                .Where(x => string.Equals(x.PrNo, pr.PrNo, StringComparison.OrdinalIgnoreCase)
                    && x.PrLineNo == pr.PrLineNo
                    && latestRevisionByPo.TryGetValue(x.PoNo, out var latest)
                    && x.PoRelNo == latest
                    && !string.Equals(x.Status, PoOrderStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var orderedByCurrentPo = currentPoLines.Sum(x => x.PoOrderedStdQty);
            var prUnordered = PoPrCalc.RoundQty(Math.Max(pr.StdQty - orderedByCurrentPo, 0m));

            if (currentPoLines.Count == 0)
            {
                openProcurement += prUnordered;
                traceLines.Add(new WorkOrderMaterialProcurementTraceLine
                {
                    WorkOrderMaterialId = workOrderMaterialId,
                    PrNo = pr.PrNo,
                    PrLineNo = pr.PrLineNo,
                    PrStdQty = pr.StdQty,
                    PrStdUom = pr.StdUom,
                    PrUnorderedStdQty = prUnordered,
                    IsConsistent = prUomMatches,
                    InconsistencyMessage = prUomMatches ? null : "PR standard UOM does not match the material base UOM."
                });
                continue;
            }

            foreach (var po in currentPoLines)
            {
                var poUomMatches = string.Equals(
                    po.StdUom?.Trim(),
                    material.BaseUom?.Trim(),
                    StringComparison.OrdinalIgnoreCase);
                if (!poUomMatches)
                {
                    consistent = false;
                    inconsistency ??= $"PO {po.PoNo} revision {po.PoRelNo} standard UOM does not match the Work Order material base UOM.";
                }

                var lineOpen = PoPrCalc.RoundQty(Math.Max(po.PoOpenStdQty, 0m));
                openProcurement += lineOpen;
                if (!poUomMatches)
                {
                    openProcurement = Math.Max(0m, openProcurement);
                }

                traceLines.Add(new WorkOrderMaterialProcurementTraceLine
                {
                    WorkOrderMaterialId = workOrderMaterialId,
                    PrNo = pr.PrNo,
                    PrLineNo = pr.PrLineNo,
                    PrStdQty = pr.StdQty,
                    PrStdUom = pr.StdUom,
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoOrderedStdQty = po.PoOrderedStdQty,
                    PoOpenStdQty = lineOpen,
                    PrUnorderedStdQty = prUnordered,
                    EtaDate = po.EtaDate,
                    IsCurrentPoRevision = true,
                    IsConsistent = prUomMatches && poUomMatches,
                    InconsistencyMessage = !prUomMatches
                        ? "PR standard UOM does not match the material base UOM."
                        : poUomMatches ? null : "PO standard UOM does not match the material base UOM."
                });
            }

            // The PR remainder is already represented by the PO's standard ordered quantity.
            // Add only the unconsumed PR quantity once, even when the current PO has multiple lines.
            openProcurement += prUnordered;
        }

        var available = IvQty.Round(availability.Data.Candidates.Sum(x => x.AvailableToAllocateBaseQty));
        var physicalShort = IvQty.Round(Math.Max(material.RequiredBaseQty - available, 0m));
        var open = IvQty.Round(Math.Max(openProcurement, 0m));
        var net = consistent
            ? IvQty.Round(Math.Max(physicalShort - open, 0m))
            : 0m;

        return WorkOrderMaterialProcurementTraceResult.Ok(new WorkOrderMaterialProcurementTrace
        {
            WorkOrderMaterialId = workOrderMaterialId,
            WorkOrderId = material.WorkOrderId,
            ComponentCode = material.ComponentCode,
            ComponentDescription = material.ComponentDescription,
            BaseUom = material.BaseUom,
            SupplySource = material.SupplySource,
            RequiredBaseQty = material.RequiredBaseQty,
            AvailableBaseQty = available,
            PhysicalShortBaseQty = physicalShort,
            OpenProcurementBaseQty = open,
            NetProcurementRequiredBaseQty = net,
            IsConsistent = consistent,
            InconsistencyMessage = inconsistency,
            Lines = traceLines
        });
    }

    private sealed class MaterialRow
    {
        public long WorkOrderId { get; init; }
        public string ComponentCode { get; init; } = string.Empty;
        public string? ComponentDescription { get; init; }
        public string? BaseUom { get; init; }
        public decimal RequiredBaseQty { get; init; }
        public string SupplySource { get; init; } = string.Empty;
    }

    private sealed class PrRow
    {
        public string PrNo { get; init; } = string.Empty;
        public short PrLineNo { get; init; }
        public decimal StdQty { get; init; }
        public string? StdUom { get; init; }
    }

    private sealed class PoRow
    {
        public string PrNo { get; init; } = string.Empty;
        public short PrLineNo { get; init; }
        public string PoNo { get; init; } = string.Empty;
        public short PoRelNo { get; init; }
        public decimal PoOrderedStdQty { get; init; }
        public decimal PoOpenStdQty { get; init; }
        public string? StdUom { get; init; }
        public DateTime? EtaDate { get; init; }
        public string? Status { get; init; }
    }
}
