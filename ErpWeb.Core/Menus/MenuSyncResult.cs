namespace ErpWeb.Core.Menus;

public sealed class MenuSyncResult
{
    public bool Success { get; init; }
    public int InsertedCount { get; init; }
    public int UpdatedCount { get; init; }
    public int UnchangedCount { get; init; }
    public int DisabledCount { get; init; }
    public int ErrorCount => Errors.Count;
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> InsertedMenuCodes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UpdatedMenuCodes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DisabledMenuCodes { get; init; } = Array.Empty<string>();

    public static MenuSyncResult Failed(IEnumerable<string> errors) =>
        new()
        {
            Success = false,
            Errors = errors.ToList()
        };
}
