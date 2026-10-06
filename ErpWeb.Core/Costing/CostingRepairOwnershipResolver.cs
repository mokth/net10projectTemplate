using ErpWeb.Core.Inventory;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Sales;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
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
        var batchNo = int.TryParse(posting.SourceDocumentNo, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedNo)
            ? parsedNo
            : (int?)null;
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
        var link = await ResolveProductionLinkAsync(db, posting, scope.CompanyCode, scope.BranchCode!, cancellationToken);

        return CostingRepairOwnershipRules.Resolve(new CostingRepairEvidence(
            scope.CompanyCode,
            scope.BranchCode,
            posting.Id,
            physical,
            posting.SourceDocumentId,
            posting.SourceDocumentNo,
            doNo,
            invNo,
            batch?.ForceCloseDate is not null,
            salesCn is not null,
            salesCn,
            purchaseCn is not null,
            purchaseCn,
            link is not null,
            link?.ProductionDocumentType,
            link?.ProductionDocumentNo));
    }

    internal static async Task<ProductionPostingLink?> ResolveProductionLinkAsync(
        AppDbContext db,
        StockPosting posting,
        string company,
        string branch,
        CancellationToken cancellationToken)
    {
        if (posting.ProductionPostingLinkId is long linkId)
        {
            var linked = await db.ProductionPostingLinks.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Uid == linkId
                    && x.CompanyCode == company
                    && x.BranchCode == branch, cancellationToken);
            if (linked is not null)
                return linked;
        }

        return await ResolveLegacyProductionLinkAsync(db, posting, company, branch, cancellationToken);
    }

    private static async Task<ProductionPostingLink?> ResolveLegacyProductionLinkAsync(
        AppDbContext db,
        StockPosting posting,
        string company,
        string branch,
        CancellationToken cancellationToken)
    {
        var physical = posting.SourceDocumentType.Trim();
        if (string.Equals(physical, ProductionDocumentTypes.FinishedGoodReceipt, StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(posting.SourceDocumentId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var batchId))
                return null;
            var receiptBatchNo = await db.ProductionFinishedGoodReceiptRows.AsNoTracking()
                .Where(x => x.BatchId == batchId && x.CompanyCode == company && x.BranchCode == branch)
                .Select(x => (int?)x.Batch.BatchNo)
                .FirstOrDefaultAsync(cancellationToken);
            var documentNo = posting.SourceDocumentNo.Trim();
            return await db.ProductionPostingLinks.AsNoTracking()
                .Where(x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.ProductionDocumentType == ProductionDocumentTypes.FinishedGoodReceipt
                    && (x.ProductionDocumentNo == documentNo
                        || (receiptBatchNo != null && x.InventoryBatchNo == receiptBatchNo)))
                .OrderByDescending(x => x.Uid)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (string.Equals(physical, ProductionDocumentTypes.ProductionOutput, StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(posting.SourceDocumentId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var outputId))
                return null;
            var requestId = await db.ProductionOutputs.AsNoTracking()
                .Where(x => x.Uid == outputId && x.CompanyCode == company && x.BranchCode == branch)
                .Select(x => x.PostingRequestId)
                .FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(requestId))
                return null;
            return await db.ProductionPostingLinks.AsNoTracking()
                .Where(x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.PostingRequestId == requestId)
                .OrderByDescending(x => x.Uid)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (string.Equals(physical, IvTrxTypes.IssueToProduction, StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(posting.SourceDocumentNo, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var inventoryBatchNo))
                return null;
            return await db.ProductionPostingLinks.AsNoTracking()
                .Where(x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.InventoryBatchNo == inventoryBatchNo)
                .OrderByDescending(x => x.Uid)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return null;
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
