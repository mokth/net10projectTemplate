using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionMaterialIssueService
{
    /// <summary>Compatibility adapter; all work goes through the save-first lifecycle.</summary>
    [Obsolete("Use CreateAsync, then PostAsync(batchNos).")]
    public async Task<IvMasterOperationResult<ProductionMaterialIssuePostResult>> PostAsync(
        ProductionMaterialIssuePostRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ProductionMaterialIssuePostValidator.Validate(request, _clock.Now);
        if (validation.Count > 0)
            return IvMasterOperationResult<ProductionMaterialIssuePostResult>.Fail(IvMasterErrorCode.Validation, "The posting request is invalid.", validation);
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return PostFail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");
        await using var lookup = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var replayLink = await lookup.ProductionPostingLinks.AsNoTracking().FirstOrDefaultAsync(x =>
            x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
            && x.CommandType == ProductionPostingCommandTypes.MaterialIssuePost
            && x.PostingRequestId == request.PostingRequestId, cancellationToken);
        if (replayLink is not null)
        {
            if (replayLink.Status == ProductionPostingLinkStatuses.Succeeded)
                return IvMasterOperationResult<ProductionMaterialIssuePostResult>.Ok(
                    await BuildResultAsync(lookup, replayLink, request.WorkOrderNo, cancellationToken));
            return PostFail(IvMasterErrorCode.Concurrency, "This posting request already exists as a draft.");
        }
        var ids = request.Lines.Select(x => x.WorkOrderMaterialId).ToArray();
        var operationIds = await lookup.ProductionWorkOrderMaterials.AsNoTracking().Where(x => ids.Contains(x.Uid))
            .Select(x => x.WorkOrderOperationId).Distinct().ToListAsync(cancellationToken);
        if (operationIds.Count != 1 || operationIds[0] is null)
            return PostFail(IvMasterErrorCode.Validation, "One material issue may contain materials from exactly one operation.");
        var saved = await CreateAsync(new ProductionMaterialIssueSaveRequest
        {
            WorkOrderNo = request.WorkOrderNo, WorkOrderOperationId = operationIds[0]!.Value,
            SnapshotRevision = request.SnapshotRevision, SnapshotHash = request.SnapshotHash,
            ProductionQtyThisIssue = request.ProductionQtyThisIssue,
            TrxDateTime = request.IssueDate, RefNo = "AUTO", Remark = request.Remark, Lines = request.Lines
        }, cancellationToken);
        if (!saved.Succeeded || saved.Data is null)
            return IvMasterOperationResult<ProductionMaterialIssuePostResult>.Fail(saved.ErrorCode, saved.Message ?? "Unable to save draft.", saved.ValidationErrors);
        await using (var identityDb = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var savedLink = await identityDb.ProductionPostingLinks.SingleAsync(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode && x.CommandType == ProductionPostingCommandTypes.MaterialIssuePost
                && x.InventoryBatchNo == saved.Data.BatchNo, cancellationToken);
            savedLink.PostingRequestId = request.PostingRequestId;
            await identityDb.SaveChangesAsync(cancellationToken);
        }
        var posted = await PostAsync([saved.Data.BatchNo], cancellationToken);
        var item = posted.Data?.Batches.SingleOrDefault();
        if (!posted.Succeeded || item?.Succeeded != true)
            return PostFail(IvMasterErrorCode.Validation, item?.Message ?? posted.Message ?? "Draft was saved but could not be posted.");
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var link = await db.ProductionPostingLinks.AsNoTracking().SingleAsync(x => x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode && x.CommandType == ProductionPostingCommandTypes.MaterialIssuePost
            && x.InventoryBatchNo == saved.Data.BatchNo, cancellationToken);
        return IvMasterOperationResult<ProductionMaterialIssuePostResult>.Ok(await BuildResultAsync(db, link, request.WorkOrderNo, cancellationToken));
    }

    private static async Task<ProductionWorkOrder?> LockWorkOrderAsync(ErpWeb.Model.Data.AppDbContext db, string company, string branch, string no, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionWorkOrders.FromSqlInterpolated($@"SELECT * FROM dbo.PrWorkOrder WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode={company} AND BranchCode={branch} AND WorkOrderNo={no}").SingleOrDefaultAsync(ct)
            : await db.ProductionWorkOrders.SingleOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.WorkOrderNo == no, ct);

    private static async Task<ProductionWorkOrderMaterial?> LockMaterialAsync(ErpWeb.Model.Data.AppDbContext db, long id, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionWorkOrderMaterials.FromSqlInterpolated($@"SELECT * FROM dbo.PrWorkOrderMaterial WITH (UPDLOCK, HOLDLOCK) WHERE UID={id}").SingleOrDefaultAsync(ct)
            : await db.ProductionWorkOrderMaterials.SingleOrDefaultAsync(x => x.Uid == id, ct);

    private static string? ValidateMaterialPolicy(ProductionWorkOrderMaterial m) =>
        m.WorkOrderOperationId is null || m.RequiredQty <= 0m || m.ConversionFactorToBase <= 0m || string.IsNullOrWhiteSpace(m.RequiredUom) || string.IsNullOrWhiteSpace(m.BaseUom) ? $"Material {m.ComponentCode} has an invalid execution snapshot."
        : m.IssueMethod != PrMaterialIssueMethods.Manual ? $"Material {m.ComponentCode} is not manually issuable."
        : m.SupplySource is not (PrMaterialSupplySources.Purchased or PrMaterialSupplySources.ExternalSupply) ? $"Material {m.ComponentCode} is not warehouse supplied."
        : string.IsNullOrWhiteSpace(m.WarehouseCode) ? $"Material {m.ComponentCode} has no warehouse." : null;

    private static async Task<ProductionMaterialIssuePostResult> BuildResultAsync(ErpWeb.Model.Data.AppDbContext db, ProductionPostingLink link, string woNo, CancellationToken ct)
    {
        var order = await db.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Uid == link.WorkOrderId, ct);
        var materialIds = await db.ProductionMaterialMovements.AsNoTracking().Where(x => x.PostingLinkId == link.Uid)
            .Select(x => x.WorkOrderMaterialId).Distinct().ToListAsync(ct);
        var mats = await db.ProductionWorkOrderMaterials.AsNoTracking().Where(x => materialIds.Contains(x.Uid)).OrderBy(x => x.Uid).ToListAsync(ct);
        return new ProductionMaterialIssuePostResult { PostingRequestId = link.PostingRequestId, BatchNo = link.InventoryBatchNo ?? 0,
            PostingOperationId = link.PostingOperationId, WorkOrderNo = woNo, WorkOrderStatus = order.Status,
            PostedDate = link.CompletedDate ?? link.CreatedDate, Materials = mats.Select(x => new ProductionMaterialIssuePostedMaterial
            { WorkOrderMaterialId = x.Uid, IssuedQty = x.IssuedQty, ReturnedQty = x.ReturnedQty,
                NetIssuedQty = ProductionWorkOrderCalc.NetIssuedQty(x.IssuedQty, x.ReturnedQty),
                OutstandingQty = ProductionMaterialExecutionCalc.Outstanding(x.RequiredQty, x.IssuedQty, x.ReturnedQty) }).ToList() };
    }

    private static IvMasterOperationResult<ProductionMaterialIssuePostResult> PostFail(IvMasterErrorCode code, string message) =>
        IvMasterOperationResult<ProductionMaterialIssuePostResult>.Fail(code, message);

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];
}
