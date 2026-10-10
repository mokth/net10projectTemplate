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

public sealed class IvStockMasterImageRow
{
    public long Uid { get; init; }
    public int SortOrder { get; init; }
    public bool IsPrimary { get; init; }
}

public sealed class IvStockMasterImageGalleryChangeSet
{
    public IReadOnlyList<IvPendingStockImage> Additions { get; init; } = Array.Empty<IvPendingStockImage>();
    public IReadOnlyList<long> RemoveImageIds { get; init; } = Array.Empty<long>();
    public IvStockMasterPrimarySelection? PrimarySelection { get; init; }

    // Compatibility adapter for the former replace/remove command. The normal write path is the gallery change set.
    internal bool ReplaceAllExisting { get; init; }
    internal bool RemoveAllExisting { get; init; }

    public bool HasChange => Additions is { Count: > 0 }
        || RemoveImageIds is { Count: > 0 }
        || PrimarySelection is not null
        || ReplaceAllExisting
        || RemoveAllExisting;
}

public sealed class IvPendingStockImage
{
    public Guid Token { get; init; }
    public required IvPreparedStockImage Image { get; init; }
}

public sealed class IvStockMasterPrimarySelection
{
    public long? ExistingImageId { get; init; }
    public Guid? PendingImageToken { get; init; }
}
