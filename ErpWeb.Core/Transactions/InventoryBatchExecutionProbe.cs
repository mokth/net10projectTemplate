using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Transactions;

/// <summary>
/// Fail-safe answer to "has this physical IvTrxBatch ever executed?".
/// Current-generation helpers such as HistoryExistsForBatchAsync are intentionally not used.
/// </summary>
public static class InventoryBatchExecutionProbe
{
    public static async Task<bool> HasEverExecutedAsync(
        AppDbContext db,
        IvTrxBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.PostedCount > 0
            || batch.RollbackCount > 0
            || batch.PostedDate is not null
            || batch.RollbackDate is not null
            || batch.ForceCloseDate is not null
            || batch.DeletedAtUtc is not null)
        {
            return true;
        }

        var company = batch.CompanyCode;
        var branch = batch.BranchCode;
        var sourceId = batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (await db.IvTrxHistories.AsNoTracking().AnyAsync(x =>
                x.CompanyCode == company && x.BranchCode == branch && x.BatchNo == batch.BatchNo,
                cancellationToken))
        {
            return true;
        }

        if (await db.StockPostings.AsNoTracking().AnyAsync(x =>
                x.CompanyCode == company
                && x.BranchCode == branch
                && x.SourceModule == "INVENTORY"
                && x.SourceDocumentType == batch.TrxType
                && x.SourceDocumentId == sourceId,
                cancellationToken))
        {
            return true;
        }

        if (await db.ProductionMaterialMovements.AsNoTracking().AnyAsync(x =>
                x.CompanyCode == company
                && x.BranchCode == branch
                && (x.InventoryBatchId == batch.Id || x.InventoryBatchNo == batch.BatchNo),
                cancellationToken))
        {
            return true;
        }

        if (await db.ProductionPostingLinks.AsNoTracking().AnyAsync(x =>
                x.CompanyCode == company
                && x.BranchCode == branch
                && x.InventoryBatchNo == batch.BatchNo
                && x.Status != ProductionPostingLinkStatuses.Draft
                && x.Status != ProductionPostingLinkStatuses.Cancelled,
                cancellationToken))
        {
            return true;
        }

        if (await db.ProductionFinishedGoodFactRows.AsNoTracking().AnyAsync(x =>
                x.BatchId == batch.Id, cancellationToken))
        {
            return true;
        }

        return false;
    }
}
