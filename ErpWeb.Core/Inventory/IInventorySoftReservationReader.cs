using ErpWeb.Model.Data;

namespace ErpWeb.Core.Inventory;

/// <summary>Soft reservations that reduce available inventory without changing physical stock.</summary>
public sealed record InventorySoftReservationAmounts(
    decimal NewSpReservedQty,
    decimal DrReservedQty)
{
    public decimal TotalReservedQty => IvQty.Round(NewSpReservedQty + DrReservedQty);
}

public interface IInventorySoftReservationReader
{
    Task<IReadOnlyDictionary<int, InventorySoftReservationAmounts>> GetReservedByBalanceAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string locationCode,
        IReadOnlyCollection<int> balanceIds,
        IReadOnlyCollection<int>? excludeSpDetailIds = null,
        long? excludeDeliveryRequestSourceId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<(long DeliveryRequestSourceId, int BalLocId), decimal>>
        GetActiveDeliveryRequestReservationsAsync(
            AppDbContext db,
            string companyCode,
            string branchCode,
            string locationCode,
            IReadOnlyCollection<long> deliveryRequestSourceIds,
            CancellationToken cancellationToken = default);
}
