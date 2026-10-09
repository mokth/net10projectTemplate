using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;

namespace ErpWeb.Core.Sales;

public static class SaDeliveryRequestFulfilmentStatuses
{
    public const string Open = "OPEN";
    public const string PartialReady = "PARTIAL_READY";
    public const string Ready = "READY";
    public const string PartialDelivered = "PARTIAL_DELIVERED";
    public const string Completed = "COMPLETED";
}

public static class SaDeliveryRequestBlockerCodes
{
    public const string None = "NONE";
    public const string WoNotPlanned = "WO_NOT_PLANNED";
    public const string MaterialShortage = "MATERIAL_SHORTAGE";
    public const string ProcurementLate = "PROCUREMENT_LATE";
    public const string ProductionLate = "PRODUCTION_LATE";
    public const string StockNotReady = "STOCK_NOT_READY";
}

public sealed class SaDeliveryRequestFulfilmentFacts
{
    public long DeliveryRequestId { get; init; }
    public decimal RequestedQty { get; init; }
    public decimal DeliveredQty { get; init; }
    public decimal OpenDemandQty { get; init; }
    public decimal DrStockReservedQty { get; init; }
    public decimal NewShipmentReservedQty { get; init; }
    public decimal ReadyQty { get; init; }
    public decimal ActiveWoAllocatedQty { get; init; }
    public decimal LinkedFgReceivedQty { get; init; }
    public decimal OutstandingWoSupplyQty { get; init; }
    public decimal ProductionRequiredQty { get; init; }
    public decimal ProductionUnplannedQty { get; init; }
    public decimal ProducedQty { get; init; }
    public string FulfilmentStatus { get; init; } = SaDeliveryRequestFulfilmentStatuses.Open;
    public string BlockerCode { get; init; } = SaDeliveryRequestBlockerCodes.None;
    public DateTime? FulfilledDate { get; init; }
    public DateTime? ForecastReadyDate { get; init; }
    public bool IsAtRisk { get; init; }
}

public interface ISaDeliveryRequestFulfilmentService
{
    Task<IvMasterOperationResult<SaDeliveryRequestFulfilmentFacts>> GetFactsAsync(
        long deliveryRequestId,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IReadOnlyDictionary<long, SaDeliveryRequestFulfilmentFacts>>> GetFactsBatchAsync(
        IReadOnlyCollection<long> deliveryRequestIds,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaDeliveryRequestFulfilmentFacts>> ReconcileAsync(
        long deliveryRequestId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles within the caller's transaction. The caller owns SaveChanges and commit.
    /// No nested transaction is started.
    /// </summary>
    Task<SaDeliveryRequestFulfilmentFacts> ReconcileInTransactionAsync(
        AppDbContext db,
        long deliveryRequestId,
        InventoryTenantScope scope,
        CancellationToken cancellationToken = default);

    Task ReleaseAllInTransactionAsync(
        AppDbContext db,
        long deliveryRequestId,
        InventoryTenantScope scope,
        string reason,
        CancellationToken cancellationToken = default);

    Task TransferToShipmentInTransactionAsync(
        AppDbContext db,
        long deliveryRequestSourceId,
        decimal quantity,
        InventoryTenantScope scope,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>Reconciles every active DR allocation owned by a Work Order in the caller's transaction.</summary>
    Task ReconcileForWorkOrderInTransactionAsync(
        AppDbContext db,
        long workOrderId,
        InventoryTenantScope scope,
        CancellationToken cancellationToken = default);
}
