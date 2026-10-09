namespace ErpWeb.Core.Inventory;

public interface IIvStockMasterImageFileCleanup
{
    Task TryDeleteManagedFileAsync(
        string? relativePath,
        string companyCode,
        string itemCode,
        string operation,
        CancellationToken cancellationToken = default);
}
