using System.Collections.ObjectModel;

namespace ErpWeb.UI.Components.Common.DataGrid;

public enum GridColumnSize
{
    Auto,
    Tiny,
    Small,
    Status,
    Date,
    DateTime,
    Quantity,
    Percent,
    Amount,
    Code,
    DocumentNo,
    Reference,
    Name,
    Description,
    LongText
}

public readonly record struct GridColumnSizingProfile(string? Width, int MinWidth);

public static class GridColumnSizing
{
    private static readonly IReadOnlyDictionary<GridColumnSize, GridColumnSizingProfile> Profiles =
        new ReadOnlyDictionary<GridColumnSize, GridColumnSizingProfile>(new Dictionary<GridColumnSize, GridColumnSizingProfile>
        {
            [GridColumnSize.Auto] = new(null, 50),
            [GridColumnSize.Tiny] = new("72px", 65),
            [GridColumnSize.Small] = new("95px", 80),
            [GridColumnSize.Status] = new("115px", 95),
            [GridColumnSize.Date] = new("120px", 105),
            [GridColumnSize.DateTime] = new("155px", 135),
            [GridColumnSize.Quantity] = new("120px", 100),
            [GridColumnSize.Percent] = new("105px", 90),
            [GridColumnSize.Amount] = new("135px", 110),
            [GridColumnSize.Code] = new("160px", 135),
            [GridColumnSize.DocumentNo] = new("165px", 135),
            [GridColumnSize.Reference] = new("190px", 145),
            [GridColumnSize.Name] = new("240px", 190),
            [GridColumnSize.Description] = new("280px", 210),
            [GridColumnSize.LongText] = new("330px", 220)
        });

    public static string? GetEffectiveWidth(GridColumnData column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return !string.IsNullOrWhiteSpace(column.Width)
            ? column.Width
            : GetProfile(column.Size).Width;
    }

    public static int GetEffectiveMinWidth(GridColumnData column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column.MinWidth ?? GetProfile(column.Size).MinWidth;
    }

    public static string? GetEffectiveWidth(GridColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return !string.IsNullOrWhiteSpace(column.Width)
            ? column.Width
            : GetProfile(column.Size).Width;
    }

    public static int GetEffectiveMinWidth(GridColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column.MinWidth ?? GetProfile(column.Size).MinWidth;
    }

    public static GridColumnSizingProfile GetProfile(GridColumnSize size) =>
        Profiles.TryGetValue(size, out var profile)
            ? profile
            : Profiles[GridColumnSize.Auto];
}
