namespace ErpWeb.Core.Inventory;

public sealed class IvPreparedStockImage
{
    public required byte[] Content { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public string ContentType { get; init; } = "image/webp";
}

public sealed class IvStoredStockImage
{
    public required string RelativePath { get; init; }
}

public sealed class IvStockMasterImageReadResult
{
    public required Stream Stream { get; init; }
    public string ContentType { get; init; } = "image/webp";
}

public sealed class IvStockMasterImageChange
{
    public IvPreparedStockImage? Replacement { get; init; }
    public bool RemoveExisting { get; init; }

    public bool HasChange => Replacement is not null || RemoveExisting;
}
