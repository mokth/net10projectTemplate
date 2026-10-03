using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed class ProductionMaterialIssueDraftReservationReader : IProductionMaterialIssueDraftReservationReader
{
    public async Task<IReadOnlyDictionary<int, decimal>> GetReservedBaseQtyByBalanceAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long workOrderMaterialId,
        DateTime issueDate,
        int? excludeInventoryBatchNo = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await (from line in db.ProductionMaterialIssueLines.AsNoTracking()
                          join link in db.ProductionPostingLinks.AsNoTracking() on line.PostingLinkId equals link.Uid
                          join batch in db.IvTrxBatches.AsNoTracking() on line.InventoryBatchId equals batch.Id
                          join detail in db.IvTrxBatchDetails.AsNoTracking() on line.InventoryBatchDetailId equals detail.Id
                          where line.CompanyCode == companyCode
                              && line.BranchCode == branchCode
                              && link.CommandType == ProductionPostingCommandTypes.MaterialIssuePost
                              && link.Status == ProductionPostingLinkStatuses.Draft
                              && batch.BatchStatus == IvBatchStatuses.New
                              && batch.TrxDtTime <= issueDate
                              && (!excludeInventoryBatchNo.HasValue || line.InventoryBatchNo != excludeInventoryBatchNo.Value)
                              && detail.FromBalLocId.HasValue
                          select new { BalanceId = detail.FromBalLocId!.Value, line.BaseQty })
            .ToListAsync(cancellationToken);

        return rows.GroupBy(x => x.BalanceId)
            .ToDictionary(x => x.Key, x => IvQty.Round(Math.Max(x.Sum(y => y.BaseQty), 0m)));
    }
}
