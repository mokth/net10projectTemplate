using System.IO;
using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

public interface ISaPriceMaintenanceService
{
    Task<IvMasterOperationResult<SaPriceReviewPage>> SearchAsync(
        SaPriceReviewQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaPriceListImpactSummary>> GetPriceListImpactAsync(
        string custPriceCode,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaPricePreviewResult>> PreviewAsync(
        SaPricePreviewRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaPriceApplyResult>> ApplyAsync(
        SaPriceApplyRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<byte[]>> BuildReviewWorkbookAsync(
        SaPriceReviewExportRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaPriceImportPreview>> ParseImportAsync(
        Stream workbook,
        SaPriceImportContext context,
        CancellationToken cancellationToken = default);
}
