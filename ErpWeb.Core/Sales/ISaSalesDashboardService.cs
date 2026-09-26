using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

/// <summary>
/// The Sales Dashboard (plan-salesReportsAndInquiries.prompt.md Phase 3). Read-only: one call returns
/// every KPI chip and chart payload for the current company, each computed as a bounded server-side
/// aggregate — never a per-row or per-customer/per-item round trip. The chart payloads reuse
/// <see cref="ISaSalesAnalysisService"/> so the dashboard and the analysis screens can never disagree.
/// </summary>
public interface ISaSalesDashboardService
{
    Task<IvMasterOperationResult<SaDashboardResult>> GetDashboardAsync(
        CancellationToken cancellationToken = default);
}
