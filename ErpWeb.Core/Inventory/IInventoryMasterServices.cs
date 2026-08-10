using ErpWeb.Model.Entities;

namespace ErpWeb.Core.Inventory;

public sealed class InventoryOpResult<T>
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public T? Item { get; init; }
    public IReadOnlyList<T> Items { get; init; } = [];

    public static InventoryOpResult<T> Ok() => new() { Succeeded = true };
    public static InventoryOpResult<T> Ok(T item) => new() { Succeeded = true, Item = item };
    public static InventoryOpResult<T> Ok(IReadOnlyList<T> items) => new() { Succeeded = true, Items = items };
    public static InventoryOpResult<T> Fail(string message) => new() { Succeeded = false, ErrorMessage = message };
}

public interface IUomService
{
    Task<InventoryOpResult<UOM>> GetAsync(CancellationToken cancellationToken = default);
    Task<InventoryOpResult<UOM>> AddAsync(UOM uom, CancellationToken cancellationToken = default);
    Task<InventoryOpResult<UOM>> UpdateAsync(UOM uom, CancellationToken cancellationToken = default);
    Task<InventoryOpResult<UOM>> DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default);
}

public interface IItemService
{
    Task<InventoryOpResult<Item>> GetAsync(CancellationToken cancellationToken = default);
    Task<InventoryOpResult<Item>> AddAsync(Item item, CancellationToken cancellationToken = default);
    Task<InventoryOpResult<Item>> UpdateAsync(Item item, CancellationToken cancellationToken = default);
    Task<InventoryOpResult<Item>> DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default);
    Task<InventoryOpResult<ItemVariant>> GetVariantsAsync(long itemId, CancellationToken cancellationToken = default);
    Task<InventoryOpResult<UOMConversion>> GetConversionsAsync(long itemId, CancellationToken cancellationToken = default);
    Task<InventoryOpResult<UOMConversion>> AddConversionAsync(UOMConversion conversion, CancellationToken cancellationToken = default);
    Task<InventoryOpResult<UOMConversion>> DeleteConversionsAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default);
}

public interface IWarehouseService
{
    Task<InventoryOpResult<Warehouse>> GetAsync(CancellationToken cancellationToken = default);
    Task<InventoryOpResult<Warehouse>> AddAsync(Warehouse warehouse, CancellationToken cancellationToken = default);
    Task<InventoryOpResult<Warehouse>> UpdateAsync(Warehouse warehouse, CancellationToken cancellationToken = default);
    Task<InventoryOpResult<Warehouse>> DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default);
}

public interface IWarehouseLocationService
{
    Task<InventoryOpResult<WarehouseLocation>> GetAsync(long? warehouseId = null, CancellationToken cancellationToken = default);
    Task<InventoryOpResult<WarehouseLocation>> AddAsync(WarehouseLocation location, CancellationToken cancellationToken = default);
    Task<InventoryOpResult<WarehouseLocation>> UpdateAsync(WarehouseLocation location, CancellationToken cancellationToken = default);
    Task<InventoryOpResult<WarehouseLocation>> DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default);
}

public interface IReasonCodeService
{
    Task<InventoryOpResult<ReasonCode>> GetAsync(CancellationToken cancellationToken = default);
    Task<InventoryOpResult<ReasonCode>> AddAsync(ReasonCode reason, CancellationToken cancellationToken = default);
    Task<InventoryOpResult<ReasonCode>> UpdateAsync(ReasonCode reason, CancellationToken cancellationToken = default);
    Task<InventoryOpResult<ReasonCode>> DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default);
}
