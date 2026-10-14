using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Tests.Inventory.Transaction;

public sealed class StockCountGenerationResolverTests
{
    [Fact]
    public void Resolves_zero_replacement_after_a_nonzero_adjustment()
    {
        var initial = Generation(1, 10, null, 3m, 101);
        var replacement = WithReversal(Generation(2, 10, 1, 0m, null), 201);
        var leaves = StockCountGenerationResolver.ResolveActive([initial, replacement]);

        Assert.Same(replacement, leaves[10]);
        Assert.Equal(0m, leaves[10].SignedAdjustmentQty);
        Assert.Null(leaves[10].AdjustmentHistoryId);
    }

    [Theory]
    [InlineData("fork")]
    [InlineData("missing-reversal")]
    [InlineData("missing-parent")]
    public void Rejects_ambiguous_or_incomplete_generation_chains(string failure)
    {
        var initial = Generation(1, 10, null, 3m, 101);
        var child = Generation(2, 10, 1, 2m, 102);
        child.ReversalHistoryId = failure == "missing-reversal" ? null : 201;
        child.ReversalBatchNo = failure == "missing-reversal" ? null : 42;
        var rows = failure switch
        {
            "fork" => new[] { initial, child, WithReversal(Generation(3, 10, 1, 1m, 103), 202) },
            "missing-parent" => new[] { initial, WithReversal(Generation(2, 10, 99, 2m, 102), 201) },
            _ => new[] { initial, child }
        };

        var error = Assert.Throws<StockLedgerException>(() =>
            StockCountGenerationResolver.ResolveActive(rows));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, error.Error.Code);
    }

    [Theory]
    [InlineData("different-anchor")]
    [InlineData("different-sequence")]
    [InlineData("different-effective-time")]
    [InlineData("missing-adjustment-batch")]
    [InlineData("reused-history")]
    public void Rejects_broken_count_lineage(string failure)
    {
        var initial = Generation(1, 10, null, 3m, 101);
        var child = WithReversal(Generation(2, 10, 1, 2m, 102), 201);
        switch (failure)
        {
            case "different-anchor": child.AnchorPostingId = 2; break;
            case "different-sequence": child.AnchorPostingSequence = 2; break;
            case "different-effective-time": child.EffectiveAt = child.EffectiveAt.AddDays(1); break;
            case "missing-adjustment-batch": child.AdjustmentBatchNo = null; break;
            case "reused-history": child.AdjustmentHistoryId = 101; break;
        }

        var error = Assert.Throws<StockLedgerException>(() =>
            StockCountGenerationResolver.ResolveActive([initial, child]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, error.Error.Code);
    }

    [Fact]
    public void Repost_after_a_removal_starts_a_new_anchor_on_the_same_immutable_line()
    {
        var initial = Generation(1, 10, null, 3m, 101);
        var removal = WithReversal(Generation(2, 10, 1, 0m, null), 201);
        removal.IsRemoval = true;
        var repost = Generation(3, 10, 2, 2m, 301);
        repost.AnchorPostingId = 2;
        repost.AnchorPostingSequence = 2;
        repost.WritePostingId = 2;

        var active = StockCountGenerationResolver.ResolveActive([initial, removal, repost]);

        Assert.Same(repost, active[10]);
    }

    private static StockCountReplayGeneration Generation(
        long id, int lineId, long? parent, decimal qty, int? historyId) => new()
    {
        Id = id,
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        StockCountLineId = lineId,
        AnchorPostingId = 1,
        WritePostingId = 1,
        AnchorPostingSequence = 1,
        EffectiveAt = StockReplayEventKey.CountEndOfDay(new DateTime(2026, 10, 10)),
        PreviousGenerationId = parent,
        SignedAdjustmentQty = qty,
        AdjustmentBatchNo = historyId.HasValue ? 41 : null,
        AdjustmentHistoryId = historyId
    };

    private static StockCountReplayGeneration WithReversal(
        StockCountReplayGeneration generation, int historyId)
    {
        generation.ReversalHistoryId = historyId;
        generation.ReversalBatchNo = 42;
        return generation;
    }
}
