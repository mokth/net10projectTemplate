using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed class ProductionMaterialConsumeVarianceInquiryService : IProductionMaterialConsumeVarianceInquiryService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;

    public ProductionMaterialConsumeVarianceInquiryService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
    }

    public async Task<IvMasterOperationResult<ProductionMaterialConsumeVariancePage>> SearchDetailAsync(
        ProductionMaterialConsumeVarianceQuery query, CancellationToken cancellationToken = default)
    {
        var gate = await AuthorizeAsync(cancellationToken);
        if (gate.Error is not null)
            return IvMasterOperationResult<ProductionMaterialConsumeVariancePage>.Fail(gate.Error.Value.Code, gate.Error.Value.Message);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var q = FilteredFacts(db, gate.Scope!, query);
        var total = await q.CountAsync(cancellationToken);
        var take = Math.Clamp(query.Take <= 0 ? 50 : query.Take, 1, 200);
        var rows = await q
            .OrderByDescending(x => x.Output.ProductionDate)
            .ThenByDescending(x => x.Output.Uid)
            .ThenBy(x => x.Fact.ComponentCode)
            .Skip(Math.Max(0, query.Skip))
            .Take(take)
            .Select(x => new ProductionMaterialConsumeVarianceRow
            {
                ProductionOutputId = x.Output.Uid,
                DocumentNo = x.Output.DocumentNo,
                ProductionDate = x.Output.ProductionDate,
                DocumentStatus = x.Output.Status,
                WorkOrderId = x.Output.WorkOrderId,
                WorkOrderNo = x.WorkOrder.WorkOrderNo,
                ProductCode = x.WorkOrder.ProductCode,
                WorkCentreCode = x.Route.WorkCentreCode,
                OperationCode = x.Operation.OperationCode,
                WorkOrderMaterialId = x.Fact.WorkOrderMaterialId,
                ComponentCode = x.Fact.ComponentCode,
                SupplySource = x.Fact.SupplySource,
                IssueMethod = x.Fact.IssueMethod,
                RequiredUom = x.Fact.RequiredUom,
                WoBomRequiredQty = x.Fact.WoBomRequiredQty,
                StandardQty = x.Fact.StandardQty,
                ConsumeQty = x.Fact.ConsumeQty,
                VarianceQty = x.Fact.VarianceQty,
                VariancePct = x.Fact.StandardQty > 0m
                    ? x.Fact.VarianceQty / x.Fact.StandardQty * 100m
                    : null,
                TolerancePercent = x.Fact.TolerancePercent,
                VarianceReasonCode = x.Fact.VarianceReasonCode,
                VarianceReasonText = x.Fact.VarianceReasonText,
                PostedBy = x.Output.PostedBy,
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<ProductionMaterialConsumeVariancePage>.Ok(
            new ProductionMaterialConsumeVariancePage { Rows = rows, TotalCount = total });
    }

    public async Task<IvMasterOperationResult<ProductionMaterialConsumeVarianceSummaries>> SearchSummariesAsync(
        ProductionMaterialConsumeVarianceQuery query, CancellationToken cancellationToken = default)
    {
        var gate = await AuthorizeAsync(cancellationToken);
        if (gate.Error is not null)
            return IvMasterOperationResult<ProductionMaterialConsumeVarianceSummaries>.Fail(gate.Error.Value.Code, gate.Error.Value.Message);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var q = FilteredFacts(db, gate.Scope!, query);

        var byComponent = await q
            .GroupBy(x => new { x.Fact.ComponentCode, x.Fact.RequiredUom })
            .Select(g => new ProductionMaterialConsumeVarianceByComponent
            {
                ComponentCode = g.Key.ComponentCode,
                RequiredUom = g.Key.RequiredUom,
                TotalStandardQty = g.Sum(x => x.Fact.StandardQty),
                TotalConsumeQty = g.Sum(x => x.Fact.ConsumeQty),
                TotalVarianceQty = g.Sum(x => x.Fact.VarianceQty),
                OverDocumentCount = g.Count(x => x.Fact.VarianceQty > 0m),
                UnderDocumentCount = g.Count(x => x.Fact.VarianceQty < 0m),
                ExactDocumentCount = g.Count(x => x.Fact.VarianceQty == 0m),
            })
            .OrderBy(x => x.ComponentCode)
            .ThenBy(x => x.RequiredUom)
            .Take(200)
            .ToListAsync(cancellationToken);

        var byReason = await q
            .GroupBy(x => x.Fact.VarianceReasonCode)
            .Select(g => new ProductionMaterialConsumeVarianceByReason
            {
                VarianceReasonCode = g.Key,
                DocumentLineCount = g.Count(),
                OverLineCount = g.Count(x => x.Fact.VarianceQty > 0m),
                UnderLineCount = g.Count(x => x.Fact.VarianceQty < 0m),
                ExactLineCount = g.Count(x => x.Fact.VarianceQty == 0m),
                AffectedWorkOrderCount = g.Select(x => x.WorkOrder.WorkOrderNo).Distinct().Count(),
                AffectedComponentCount = g.Select(x => x.Fact.ComponentCode).Distinct().Count(),
            })
            .OrderBy(x => x.VarianceReasonCode)
            .Take(200)
            .ToListAsync(cancellationToken);

        var byReasonComponent = await q
            .GroupBy(x => new { x.Fact.VarianceReasonCode, x.Fact.ComponentCode, x.Fact.RequiredUom })
            .Select(g => new ProductionMaterialConsumeVarianceByReasonComponent
            {
                VarianceReasonCode = g.Key.VarianceReasonCode,
                ComponentCode = g.Key.ComponentCode,
                RequiredUom = g.Key.RequiredUom,
                TotalVarianceQty = g.Sum(x => x.Fact.VarianceQty),
                TotalAbsoluteVarianceQty = g.Sum(x => x.Fact.VarianceQty < 0m ? -x.Fact.VarianceQty : x.Fact.VarianceQty),
            })
            .OrderBy(x => x.VarianceReasonCode)
            .ThenBy(x => x.ComponentCode)
            .Take(200)
            .ToListAsync(cancellationToken);

        var byWorkOrder = await q
            .GroupBy(x => new { x.WorkOrder.WorkOrderNo, x.WorkOrder.ProductCode })
            .Select(g => new ProductionMaterialConsumeVarianceByWorkOrder
            {
                WorkOrderNo = g.Key.WorkOrderNo,
                ProductCode = g.Key.ProductCode,
                VarianceLineCount = g.Count(),
                OverLineCount = g.Count(x => x.Fact.VarianceQty > 0m),
                UnderLineCount = g.Count(x => x.Fact.VarianceQty < 0m),
                ExactLineCount = g.Count(x => x.Fact.VarianceQty == 0m),
                AffectedComponentCount = g.Select(x => x.Fact.ComponentCode).Distinct().Count(),
            })
            .OrderBy(x => x.WorkOrderNo)
            .Take(200)
            .ToListAsync(cancellationToken);

        var byWoComponent = await q
            .GroupBy(x => new { x.WorkOrder.WorkOrderNo, x.Fact.ComponentCode, x.Fact.RequiredUom })
            .Select(g => new ProductionMaterialConsumeVarianceByWorkOrderComponent
            {
                WorkOrderNo = g.Key.WorkOrderNo,
                ComponentCode = g.Key.ComponentCode,
                RequiredUom = g.Key.RequiredUom,
                TotalStandardQty = g.Sum(x => x.Fact.StandardQty),
                TotalConsumeQty = g.Sum(x => x.Fact.ConsumeQty),
                TotalVarianceQty = g.Sum(x => x.Fact.VarianceQty),
            })
            .OrderBy(x => x.WorkOrderNo)
            .ThenBy(x => x.ComponentCode)
            .Take(200)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<ProductionMaterialConsumeVarianceSummaries>.Ok(
            new ProductionMaterialConsumeVarianceSummaries
            {
                ByComponent = byComponent,
                ByReason = byReason,
                ByReasonComponent = byReasonComponent,
                ByWorkOrder = byWorkOrder,
                ByWorkOrderComponent = byWoComponent,
            });
    }

    private IQueryable<FactJoin> FilteredFacts(
        AppDbContext db, InventoryTenantScope scope, ProductionMaterialConsumeVarianceQuery query)
    {
        var status = string.IsNullOrWhiteSpace(query.DocumentStatus)
            ? ProductionOutputStatuses.Posted
            : query.DocumentStatus.Trim();

        var dateFrom = query.DateFrom;
        var dateToExclusive = query.DateTo?.Date.AddDays(1);
        var workOrderNo = query.WorkOrderNo?.Trim();
        var productCode = query.ProductCode?.Trim();
        var componentCode = query.ComponentCode?.Trim();
        var workCentreCode = query.WorkCentreCode?.Trim();
        var operationCode = query.OperationCode?.Trim();
        var reasonCode = query.VarianceReasonCode?.Trim();
        var postedBy = query.PostedBy?.Trim();
        var direction = query.VarianceDirection;

        return
            from fact in db.ProductionOutputMaterials.AsNoTracking()
            join output in db.ProductionOutputs.AsNoTracking()
                on new { fact.ProductionOutputId, fact.CompanyCode, fact.BranchCode }
                equals new { ProductionOutputId = output.Uid, output.CompanyCode, output.BranchCode }
            join wo in db.ProductionWorkOrders.AsNoTracking() on output.WorkOrderId equals wo.Uid
            join op in db.ProductionWorkOrderOperations.AsNoTracking() on output.WorkOrderOperationId equals op.Uid
            join route in db.ProductionWorkOrderRouteSteps.AsNoTracking() on output.RouteStepId equals route.Uid
            where fact.CompanyCode == scope.CompanyCode
                && fact.BranchCode == scope.BranchCode
                && !fact.IsHandoff
                && output.Status == status
                && (dateFrom == null || output.ProductionDate >= dateFrom)
                && (dateToExclusive == null || output.ProductionDate < dateToExclusive)
                && (workOrderNo == null || wo.WorkOrderNo.Contains(workOrderNo))
                && (productCode == null || wo.ProductCode.Contains(productCode))
                && (componentCode == null || fact.ComponentCode.Contains(componentCode))
                && (workCentreCode == null || route.WorkCentreCode.Contains(workCentreCode))
                && (operationCode == null || op.OperationCode.Contains(operationCode))
                && (reasonCode == null || fact.VarianceReasonCode == reasonCode)
                && (postedBy == null || output.PostedBy == postedBy)
                && (direction != ProductionMaterialVarianceDirections.Over || fact.VarianceQty > 0m)
                && (direction != ProductionMaterialVarianceDirections.Under || fact.VarianceQty < 0m)
                && (direction != ProductionMaterialVarianceDirections.Exact || fact.VarianceQty == 0m)
            select new FactJoin
            {
                Fact = fact,
                Output = output,
                WorkOrder = wo,
                Operation = op,
                Route = route,
            };
    }

    private async Task<(InventoryTenantScope? Scope, (IvMasterErrorCode Code, string Message)? Error)> AuthorizeAsync(
        CancellationToken cancellationToken)
    {
        if (!await _access.CanAsync(MenuCodes.PlanningMaterialConsumeVariance, PermissionCodes.Access, cancellationToken))
            return (null, (IvMasterErrorCode.AccessDenied, "Access denied."));
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return (null, (IvMasterErrorCode.InvalidScope, "A company and branch scope is required."));
        return (scope, null);
    }

    private sealed class FactJoin
    {
        public ProductionOutputMaterial Fact { get; init; } = null!;
        public ProductionOutput Output { get; init; } = null!;
        public ProductionWorkOrder WorkOrder { get; init; } = null!;
        public ProductionWorkOrderOperation Operation { get; init; } = null!;
        public ProductionWorkOrderRouteStep Route { get; init; } = null!;
    }
}
