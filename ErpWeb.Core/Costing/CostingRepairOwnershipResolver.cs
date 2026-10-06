using ErpWeb.Core.Inventory;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Sales;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Costing;

public sealed class CostingRepairOwnershipResolver : ICostingRepairOwnershipResolver
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;

    public CostingRepairOwnershipResolver(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
    }

    public async Task<CostingRepairOwnershipResult> ResolveAsync(
        CostingRepairNode node,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
            return new CostingRepairOwnershipResult(false, "A trusted company and branch are required.", null);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var posting = await db.StockPostings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == node.StockPostingId
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode, cancellationToken);
        if (posting is null)
            return new CostingRepairOwnershipResult(false, "The stock posting was not found in this branch.", null);

        var physical = posting.SourceDocumentType.Trim();
        var batchNo = int.TryParse(posting.SourceDocumentId, out var parsed) ? parsed : (int?)null;
        var batch = batchNo is null
            ? null
            : await db.IvTrxBatches.AsNoTracking().FirstOrDefaultAsync(x =>
                x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.BatchNo == batchNo
                && x.TrxType == physical, cancellationToken);

        string? doNo = null;
        string? invNo = null;
        if (string.Equals(physical, IvTrxTypes.SalesOut, StringComparison.OrdinalIgnoreCase) && batchNo is not null)
        {
            var history = await db.IvTrxHistories.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.BatchNo == batchNo
                    && x.TrxType == IvTrxTypes.SalesOut)
                .Select(x => new { x.DoNo, x.InvNo })
                .FirstOrDefaultAsync(cancellationToken);
            doNo = history?.DoNo;
            invNo = history?.InvNo;
        }

        var salesCn = await FindSalesCreditNoteAsync(db, scope.CompanyCode, scope.BranchCode!, physical, batch, cancellationToken);
        var purchaseCn = await FindPurchaseCreditNoteAsync(db, scope.CompanyCode, scope.BranchCode!, physical, batchNo, cancellationToken);

        return CostingRepairOwnershipRules.Resolve(new CostingRepairEvidence(
            scope.CompanyCode,
            scope.BranchCode,
            posting.Id,
            physical,
            posting.SourceDocumentId,
            doNo,
            invNo,
            batch?.ForceCloseDate is not null,
            salesCn is not null,
            salesCn,
            purchaseCn is not null,
            purchaseCn,
            posting.ProductionPostingLinkId is not null));
    }

    private static async Task<string?> FindSalesCreditNoteAsync(
        AppDbContext db,
        string company,
        string branch,
        string physical,
        ErpWeb.Model.Entities.Inventory.IvTrxBatch? batch,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(physical, IvTrxTypes.CustomerReturn, StringComparison.OrdinalIgnoreCase))
            return null;
        var reference = batch?.RefNo?.Trim();
        if (string.IsNullOrWhiteSpace(reference)
            || !reference.StartsWith(SaCdnSpRefs.Prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var docNo = reference[SaCdnSpRefs.Prefix.Length..].Trim();
        if (docNo.Length == 0)
            return null;
        var owned = await db.SaCdns.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company
            && x.BranchCode == branch
            && x.DocNo == docNo
            && x.ReturnStock, cancellationToken);
        return owned ? docNo : null;
    }

    private static async Task<string?> FindPurchaseCreditNoteAsync(
        AppDbContext db,
        string company,
        string branch,
        string physical,
        int? batchNo,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(physical, IvTrxTypes.VendorReturn, StringComparison.OrdinalIgnoreCase) || batchNo is null)
            return null;
        return await db.PoCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company
                && x.BranchCode == branch
                && x.VrBatchNo == batchNo
                && x.ReturnStock)
            .Select(x => x.DocNo)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
