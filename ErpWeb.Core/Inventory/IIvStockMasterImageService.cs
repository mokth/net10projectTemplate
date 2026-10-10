namespace ErpWeb.Core.Inventory;

public interface IIvStockMasterImageService : IIvStockMasterImageFileCleanup
{
    Task<IvMasterOperationResult<IvPreparedStockImage>> PrepareAsync(
        string originalFileName,
        string? contentType,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvStoredStockImage>> StorePreparedAsync(
        string companyCode,
        string itemCode,
        IvPreparedStockImage image,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IReadOnlyList<IvStockMasterImageRow>>> ListAsync(
        string itemCode,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvStockMasterImageReadResult>> OpenReadAsync(
        string itemCode,
        long? imageId = null,
        CancellationToken cancellationToken = default);
}
