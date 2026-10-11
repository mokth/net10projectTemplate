using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Purchase;

public interface IPoProcurementKpiService
{
    Task<IvMasterOperationResult<PoProcurementKpiSummary>> GetSummaryAsync(
        PoInquiryQuery query,
        CancellationToken cancellationToken = default);
}
