using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.Tests.UI;

public sealed class GridColumnSizingTests
{
    public static IEnumerable<object[]> Profiles =>
    [
        [GridColumnSize.Auto, null, 50],
        [GridColumnSize.Tiny, "72px", 65],
        [GridColumnSize.Small, "95px", 80],
        [GridColumnSize.Status, "115px", 95],
        [GridColumnSize.Date, "120px", 105],
        [GridColumnSize.DateTime, "155px", 135],
        [GridColumnSize.Quantity, "120px", 100],
        [GridColumnSize.Percent, "105px", 90],
        [GridColumnSize.Amount, "135px", 110],
        [GridColumnSize.Code, "160px", 135],
        [GridColumnSize.DocumentNo, "165px", 135],
        [GridColumnSize.Reference, "190px", 145],
        [GridColumnSize.Name, "240px", 190],
        [GridColumnSize.Description, "280px", 210],
        [GridColumnSize.LongText, "330px", 220]
    ];

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Semantic_profile_returns_approved_width_and_minimum(
        GridColumnSize size,
        string? expectedWidth,
        int expectedMinWidth)
    {
        var column = new GridColumnData { Size = size };

        Assert.Equal(expectedWidth, GridColumnSizing.GetEffectiveWidth(column));
        Assert.Equal(expectedMinWidth, GridColumnSizing.GetEffectiveMinWidth(column));
    }

    [Fact]
    public void Explicit_width_and_minimum_override_semantic_profile()
    {
        var column = new GridColumnData
        {
            Size = GridColumnSize.Code,
            Width = "175px",
            MinWidth = 150
        };

        Assert.Equal("175px", GridColumnSizing.GetEffectiveWidth(column));
        Assert.Equal(150, GridColumnSizing.GetEffectiveMinWidth(column));
    }

    [Fact]
    public void Grid_column_definition_uses_the_same_resolver_contract()
    {
        var column = new GridColumnDefinition { Size = GridColumnSize.Description };

        Assert.Equal("280px", GridColumnSizing.GetEffectiveWidth(column));
        Assert.Equal(210, GridColumnSizing.GetEffectiveMinWidth(column));
        Assert.IsType<int>(GridColumnSizing.GetEffectiveMinWidth(column));
    }
}
