using ErpWeb.Core.Sales;

namespace ErpWeb.Tests;

public sealed class SaDocPickerLinesTests
{
    [Fact]
    public void FilterRemainingSoLines_requires_selectedSoNo_and_does_not_read_dto()
    {
        var remaining = new[]
        {
            new SaSoLineDto { Line = 1, ICode = "A" },
            new SaSoLineDto { Line = 2, ICode = "B" }
        };

        var emptySo = SaDocPickerLines.FilterRemainingSoLines(remaining, "  ", []);
        Assert.Empty(emptySo);

        var filtered = SaDocPickerLines.FilterRemainingSoLines(
            remaining,
            "SO-1",
            [("SO-1", 1), ("OTHER", 2)]);

        var only = Assert.Single(filtered);
        Assert.Equal(2, only.Line);
    }

    [Fact]
    public void FilterRemainingSoLines_ignores_other_so_same_line()
    {
        var remaining = new[]
        {
            new SaSoLineDto { Line = 5, ICode = "X" }
        };

        var filtered = SaDocPickerLines.FilterRemainingSoLines(
            remaining,
            "SO-A",
            [("SO-B", 5)]);

        Assert.Single(filtered);
    }

    [Fact]
    public void FilterRemainingSoLines_none_some_all_already_added()
    {
        var remaining = new[]
        {
            new SaSoLineDto { Line = 1 },
            new SaSoLineDto { Line = 2 },
            new SaSoLineDto { Line = 3 }
        };

        Assert.Equal(3, SaDocPickerLines.FilterRemainingSoLines(remaining, "SO-1", []).Count);
        Assert.Equal(2, SaDocPickerLines.FilterRemainingSoLines(remaining, "SO-1", [("SO-1", 2)]).Count);
        Assert.Empty(SaDocPickerLines.FilterRemainingSoLines(
            remaining,
            "SO-1",
            [("SO-1", 1), ("SO-1", 2), ("SO-1", 3)]));
    }

    [Fact]
    public void FilterRemainingDoLines_distinguishes_same_line_on_two_dos()
    {
        var remaining = new[]
        {
            new SaDoBillableLineDto { DoNo = "DO-1", Line = 1, ICode = "A" },
            new SaDoBillableLineDto { DoNo = "DO-2", Line = 1, ICode = "B" }
        };

        var filtered = SaDocPickerLines.FilterRemainingDoLines(
            remaining,
            [("DO-1", 1)]);

        var only = Assert.Single(filtered);
        Assert.Equal("DO-2", only.DoNo);
        Assert.Equal(1, only.Line);
    }

    [Fact]
    public void SelectAllCurrent_is_new_list_with_same_instances()
    {
        var rows = new List<SaSoLineDto>
        {
            new() { Line = 1 },
            new() { Line = 2 }
        };

        var selected = SaDocPickerLines.SelectAllCurrent(rows);
        Assert.Equal(2, selected.Count);
        Assert.Same(rows[0], selected[0]);
        Assert.Same(rows[1], selected[1]);
        Assert.NotSame(rows, selected);

        Assert.Empty(SaDocPickerLines.SelectAllCurrent(Array.Empty<SaSoLineDto>()));
    }

    [Fact]
    public void SaDoBillablePickerRow_picker_key_is_stable_business_identity()
    {
        var row = new SaDoBillablePickerRow
        {
            Source = new SaDoBillableLineDto { DoNo = "DO/1", Line = 3 }
        };
        Assert.Equal(SaDocPickerLines.FormatDoPickerKey("DO/1", 3), row.PickerKey);
    }

    [Fact]
    public void CustPo_resolve_prefers_persisted_then_lookup()
    {
        var map = new Dictionary<(string SoNo, short CustRel), string?>
        {
            [("SO-1", 1)] = "PO-1"
        };

        Assert.Equal("SAVED", SaDocCustPoLookup.Resolve(map, "SO-1", 1, "SAVED"));
        Assert.Equal("PO-1", SaDocCustPoLookup.Resolve(map, "SO-1", 1));
        Assert.Null(SaDocCustPoLookup.Resolve(map, null, 1));
        Assert.Null(SaDocCustPoLookup.Resolve(null, "SO-1", 1));
    }
}
