using ErpWeb.Core.Quality;

namespace ErpWeb.Tests.Quality;

[Trait(TestCategories.Name, TestCategories.Quality)]
public class QaSourcePrefillTests
{
    [Fact]
    public void Incoming_po_matches_all_rel_lines()
    {
        var rows = new[]
        {
            Row(poNo: "PO-1", rel: 1, item: "A", line: 1),
            Row(poNo: "PO-1", rel: 1, item: "B", line: 2),
            Row(poNo: "PO-1", rel: 2, item: "C", line: 1),
            Row(poNo: "PO-2", rel: 1, item: "D", line: 1)
        };

        var matches = QaSourcePrefill.Matching(rows, new QaSourcePrefillQuery { PoNo = "PO-1", PoRelNo = 1 });

        Assert.Equal(2, matches.Count);
        Assert.Equal(["A", "B"], matches.Select(x => x.ItemCode).ToArray());
    }

    [Fact]
    public void Posted_batch_matches_document_number_when_header_id_is_database_id()
    {
        var rows = new[]
        {
            Row(sourceHeaderId: 9001, sourceDocNo: "42", item: "A", sourceLineId: 1),
            Row(sourceHeaderId: 9001, sourceDocNo: "42", item: "B", sourceLineId: 2),
            Row(sourceHeaderId: 9002, sourceDocNo: "43", item: "C", sourceLineId: 3)
        };

        var matches = QaSourcePrefill.Matching(rows, new QaSourcePrefillQuery { SourceHeaderId = 42 });

        Assert.Equal(2, matches.Count);
        Assert.Equal(["A", "B"], matches.Select(x => x.ItemCode).ToArray());
    }

    [Fact]
    public void Fg_lot_matches_production_balance_id()
    {
        var rows = new[]
        {
            new QaSourceRow { ItemCode = "FG", ProductionBalLotId = 77, SourceHeaderId = 77, LotNo = "L1" },
            new QaSourceRow { ItemCode = "FG", ProductionBalLotId = 78, SourceHeaderId = 78, LotNo = "L2" }
        };

        var matches = QaSourcePrefill.Matching(rows, new QaSourcePrefillQuery { LotId = 77 });

        var match = Assert.Single(matches);
        Assert.Equal("L1", match.LotNo);
    }

    [Fact]
    public void Daily_production_matches_output_id()
    {
        var rows = new[]
        {
            new QaSourceRow { ItemCode = "FG", SourceHeaderId = 501, LotNo = "OUT-1" },
            new QaSourceRow { ItemCode = "FG", SourceHeaderId = 502, LotNo = "OUT-2" }
        };

        var matches = QaSourcePrefill.Matching(rows, new QaSourcePrefillQuery { OutputId = 501 });

        var match = Assert.Single(matches);
        Assert.Equal("OUT-1", match.LotNo);
    }

    [Fact]
    public void Search_text_prefers_lot_then_output_then_batch_then_po()
    {
        Assert.Equal("77", QaSourcePrefill.SearchText(new QaSourcePrefillQuery { LotId = 77, OutputId = 9, SourceHeaderId = 42, PoNo = "PO-1" }));
        Assert.Equal("9", QaSourcePrefill.SearchText(new QaSourcePrefillQuery { OutputId = 9, SourceHeaderId = 42, PoNo = "PO-1" }));
        Assert.Equal("42", QaSourcePrefill.SearchText(new QaSourcePrefillQuery { SourceHeaderId = 42, PoNo = "PO-1" }));
        Assert.Equal("PO-1", QaSourcePrefill.SearchText(new QaSourcePrefillQuery { PoNo = "PO-1" }));
        Assert.Null(QaSourcePrefill.SearchText(new QaSourcePrefillQuery()));
        Assert.False(QaSourcePrefill.HasIdentity(new QaSourcePrefillQuery()));
        Assert.True(QaSourcePrefill.HasIdentity(new QaSourcePrefillQuery { PoNo = "PO-1" }));
    }

    private static QaSourceRow Row(
        string? poNo = null,
        short? rel = null,
        string item = "X",
        short? line = null,
        long? sourceHeaderId = null,
        string? sourceDocNo = null,
        long? sourceLineId = null) =>
        new()
        {
            ItemCode = item,
            PoNo = poNo,
            PoRelNo = rel,
            PoLineNo = line,
            SourceHeaderId = sourceHeaderId,
            SourceDocNo = sourceDocNo,
            SourceLineId = sourceLineId
        };
}
