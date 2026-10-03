using ErpWeb.Model.Data;

namespace ErpWeb.Core.Production;

/// <summary>
/// Projects allocations in other live IP drafts. This is advisory allocation protection only;
/// it never reserves inventory or replaces stock locks at posting time.
/// </summary>
public interface IProductionMaterialIssueDraftReservationReader
{
    Task<IReadOnlyDictionary<int, decimal>> GetReservedBaseQtyByBalanceAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long workOrderMaterialId,
        DateTime issueDate,
        int? excludeInventoryBatchNo = null,
        CancellationToken cancellationToken = default);
}
