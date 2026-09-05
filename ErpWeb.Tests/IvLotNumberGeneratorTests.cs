using ErpWeb.Core.Inventory;

namespace ErpWeb.Tests;

public class IvLotNumberGeneratorTests
{
    [Fact]
    public async Task AllocateAsync_skips_database_and_document_collisions_for_auto_generate()
    {
        var db = new HashSet<string>(StringComparer.Ordinal) { "260901001", "260901003" };
        var used = new[] { "260901004" };

        var lots = await IvLotNumberGenerator.AllocateAsync(
            3,
            "260901",
            startSeq: 1,
            autoGenerate: true,
            used,
            lot => Task.FromResult(db.Contains(lot)));

        Assert.Equal(["260901002", "260901005", "260901006"], lots);
    }

    [Fact]
    public async Task AllocateAsync_skips_document_collision_case_insensitive()
    {
        var lots = await IvLotNumberGenerator.AllocateAsync(
            1,
            "260901",
            startSeq: 1,
            autoGenerate: true,
            ["260901001"],
            _ => Task.FromResult(false));

        Assert.Equal("260901002", lots[0]);
    }

    [Fact]
    public async Task AllocateAsync_manual_suffix_increments_trailing_digits()
    {
        var lots = await IvLotNumberGenerator.AllocateAsync(
            2,
            "LOT009",
            startSeq: 1,
            autoGenerate: false,
            [],
            _ => Task.FromResult(false));

        Assert.Equal(["LOT009", "LOT010"], lots);
    }

    [Fact]
    public async Task AllocateAsync_fails_when_auto_sequence_exhausted()
    {
        var db = new HashSet<string> { "260901998", "260901999" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IvLotNumberGenerator.AllocateAsync(
                2,
                "260901",
                startSeq: 998,
                autoGenerate: true,
                [],
                lot => Task.FromResult(db.Contains(lot))));
    }

    [Theory]
    [InlineData("LOT009", "LOT010")]
    [InlineData("LOT099", "LOT100")]
    [InlineData("BASE", "BASE-2")]
    [InlineData("BASE-2", "BASE-3")]
    public void NextManualLot_increments_expected_suffix(string current, string expected)
    {
        Assert.Equal(expected, IvLotNumberGenerator.NextManualLot(current));
    }
}
