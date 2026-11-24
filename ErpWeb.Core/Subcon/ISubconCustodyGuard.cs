using ErpWeb.Model.Data;

namespace ErpWeb.Core.Subcon;

public static class SubconCustodyConstants
{
    public const string CustodyWarehouseType = "SUBCON";

    public const string SourceCustodyBlocked =
        "Stock held at a subcontractor custody warehouse cannot be issued, adjusted, counted, or transferred directly. Use Subcontracting Receive Back.";

    public const string DestinationCustodyBlocked =
        "Stock cannot be received, adjusted, or transferred directly into a subcontractor custody warehouse. Use Subcontracting Send Out.";

    public const string SubconOwnedBatchBlocked =
        "This stock transfer is owned by a Subcontracting document and can only be edited, posted, or rolled back from Subcontracting.";

    public const string SameSideCustodyTransferBlocked =
        "A subcontracting transfer must move between a company warehouse and a subcontractor custody warehouse.";
}

public interface ISubconCustodyGuard
{
    Task<bool> IsCustodyWarehouseAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        CancellationToken cancellationToken = default);

    Task<string?> AssertStockMovementAllowedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string trxType,
        IReadOnlyCollection<int> sourceBalLocIds,
        IReadOnlyCollection<string> destinationWarehouseCodes,
        bool isAuthorizedSubconCaller,
        CancellationToken cancellationToken = default);

    Task<string?> AssertBatchNotSubconOwnedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchId,
        CancellationToken cancellationToken = default);

    Task<string?> AssertWarehouseMutationAllowedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        string? priorWarehouseType,
        string? nextWarehouseType,
        bool nextIsActive,
        CancellationToken cancellationToken = default);

    Task<string?> AssertLocationMutationAllowedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        string locCode,
        bool nextIsActive,
        CancellationToken cancellationToken = default);

    Task<bool> HasSubconOwnedTransferAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<int> transferBatchNos,
        CancellationToken cancellationToken = default);
}
