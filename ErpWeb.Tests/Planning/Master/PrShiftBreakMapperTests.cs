using ErpWeb.Core.Planning;
using ErpWeb.Model.Entities.Planning;

namespace ErpWeb.Tests.Planning.Master;
[Trait(TestCategories.Name, TestCategories.Planning)]
public class PrShiftBreakMapperTests
{
    [Fact]
    public void ResolveBreaks_child_version_empty_is_authoritative()
    {
        var shift = Shift(version: 1);
        shift.BreakTm1From = TodayAt(12, 0);
        shift.BreakTm1To = TodayAt(12, 30);

        var breaks = PrShiftBreakMapper.ResolveBreaks(shift, []);

        Assert.Empty(breaks);
    }

    [Fact]
    public void ResolveBreaks_legacy_version_reads_wide()
    {
        var shift = Shift(version: 0);
        shift.BreakTm1From = TodayAt(12, 0);
        shift.BreakTm1To = TodayAt(12, 30);

        var breaks = PrShiftBreakMapper.ResolveBreaks(shift, []);

        var br = Assert.Single(breaks);
        Assert.Equal(new TimeOnly(12, 0), br.Start);
        Assert.Equal(new TimeOnly(12, 30), br.End);
    }

    [Fact]
    public void FromWideColumns_treats_midnight_sentinel_and_null_as_unused_but_keeps_midnight_break()
    {
        var shift = Shift(version: 0);
        shift.BreakTm1From = TodayAt(0, 0);
        shift.BreakTm1To = TodayAt(0, 0);
        shift.BreakTm2From = TodayAt(0, 0);
        shift.BreakTm2To = TodayAt(0, 30);
        // slots 3–5 left null

        var breaks = PrShiftBreakMapper.FromWideColumns(shift);

        var br = Assert.Single(breaks);
        Assert.Equal(new TimeOnly(0, 0), br.Start);
        Assert.Equal(new TimeOnly(0, 30), br.End);
    }

    [Fact]
    public void PackToWideColumns_uses_midnight_sentinel_for_unused_slots()
    {
        var shift = Shift(version: 1);
        var breaks = new[]
        {
            new ShiftTimeCalculator.BreakPair(new TimeOnly(10, 0), new TimeOnly(10, 15)),
            new ShiftTimeCalculator.BreakPair(new TimeOnly(12, 0), new TimeOnly(12, 30))
        };

        PrShiftBreakMapper.PackToWideColumns(shift, breaks);

        Assert.Equal(new TimeOnly(10, 0), TimeOnly.FromDateTime(shift.BreakTm1From!.Value));
        Assert.Equal(new TimeOnly(12, 30), TimeOnly.FromDateTime(shift.BreakTm2To!.Value));
        Assert.Equal(TimeSpan.Zero, shift.BreakTm3From!.Value.TimeOfDay);
        Assert.Equal(TimeSpan.Zero, shift.BreakTm5To!.Value.TimeOfDay);
    }

    [Fact]
    public void PackToWideColumns_zero_breaks_writes_five_midnight_sentinels()
    {
        var shift = Shift(version: 1);
        PrShiftBreakMapper.PackToWideColumns(shift, []);

        Assert.Equal(TimeSpan.Zero, shift.BreakTm1From!.Value.TimeOfDay);
        Assert.Equal(TimeSpan.Zero, shift.BreakTm1To!.Value.TimeOfDay);
        Assert.Equal(TimeSpan.Zero, shift.BreakTm5From!.Value.TimeOfDay);
        Assert.Equal(TimeSpan.Zero, shift.BreakTm5To!.Value.TimeOfDay);
    }

    [Fact]
    public void Remigrate_from_version0_replaces_stale_children_with_current_wide()
    {
        // Rollback simulation: version demoted to 0, wide edited to B, stale children still A.
        var shift = Shift(version: 0);
        shift.BreakTm1From = TodayAt(14, 0);
        shift.BreakTm1To = TodayAt(14, 30);
        PrShiftBreakMapper.PackToWideColumns(shift, [
            new ShiftTimeCalculator.BreakPair(new TimeOnly(14, 0), new TimeOnly(14, 30))
        ]);
        shift.BreakStorageVersion = 0;

        var staleChildren = PrShiftBreakMapper.ToChildRows(
            shift,
            [new ShiftTimeCalculator.BreakPair(new TimeOnly(10, 0), new TimeOnly(10, 15))],
            "u1");

        // Remigrate algorithm (same as Phase C / post-rollback promotion).
        var fromWide = PrShiftBreakMapper.FromWideColumns(shift);
        var canonical = PrShiftBreakMapper.CanonicalizeForShift(
            TimeOnly.FromDateTime(shift.StartTm!.Value),
            TimeOnly.FromDateTime(shift.EndTm!.Value),
            fromWide);
        PrShiftBreakMapper.PackToWideColumns(shift, canonical);
        shift.BreakStorageVersion = 1;
        var rebuilt = PrShiftBreakMapper.ToChildRows(shift, canonical, "u1");

        Assert.NotEqual(staleChildren[0].BreakFrom.TimeOfDay, rebuilt[0].BreakFrom.TimeOfDay);
        var resolved = PrShiftBreakMapper.ResolveBreaks(shift, rebuilt);
        var br = Assert.Single(resolved);
        Assert.Equal(new TimeOnly(14, 0), br.Start);
        Assert.Equal(new TimeOnly(14, 30), br.End);
    }

    [Fact]
    public void CanonicalizeForShift_sorts_overnight_breaks_on_shift_timeline()
    {
        var breaks = new[]
        {
            new ShiftTimeCalculator.BreakPair(new TimeOnly(2, 0), new TimeOnly(2, 15)),
            new ShiftTimeCalculator.BreakPair(new TimeOnly(23, 0), new TimeOnly(23, 20))
        };

        var canonical = PrShiftBreakMapper.CanonicalizeForShift(new TimeOnly(22, 0), new TimeOnly(6, 0), breaks);

        Assert.Equal(new TimeOnly(23, 0), canonical[0].Start);
        Assert.Equal(new TimeOnly(2, 0), canonical[1].Start);
    }

    [Fact]
    public void CanonicalizeForShift_rejects_more_than_five()
    {
        var breaks = Enumerable.Range(0, 6)
            .Select(i => new ShiftTimeCalculator.BreakPair(new TimeOnly(8 + i, 0), new TimeOnly(8 + i, 5)));

        Assert.Throws<ArgumentException>(() =>
            PrShiftBreakMapper.CanonicalizeForShift(new TimeOnly(8, 0), new TimeOnly(18, 0), breaks));
    }

    [Fact]
    public void CanonicalizeForShift_daytime_and_parity_fixtures_match_ShiftTimeCalculator()
    {
        var start = new TimeOnly(8, 0);
        var end = new TimeOnly(17, 0);
        var breaks = new[]
        {
            new ShiftTimeCalculator.BreakPair(new TimeOnly(15, 0), new TimeOnly(15, 10)),
            new ShiftTimeCalculator.BreakPair(new TimeOnly(10, 0), new TimeOnly(10, 15)),
            new ShiftTimeCalculator.BreakPair(new TimeOnly(12, 0), new TimeOnly(12, 30))
        };

        var canonical = PrShiftBreakMapper.CanonicalizeForShift(start, end, breaks);
        Assert.Equal(new TimeOnly(10, 0), canonical[0].Start);
        Assert.Equal(new TimeOnly(12, 0), canonical[1].Start);
        Assert.Equal(new TimeOnly(15, 0), canonical[2].Start);
        Assert.Equal(485, ShiftTimeCalculator.ComputeNetMinutes(start, end, canonical));
    }

    private static PrShift Shift(byte version) => new()
    {
        ShiftCd = "DAY",
        CompCode = "DEMO",
        StartTm = TodayAt(8, 0),
        EndTm = TodayAt(17, 0),
        BreakStorageVersion = version
    };

    private static DateTime TodayAt(int hour, int minute) => DateTime.Today.Add(new TimeSpan(hour, minute, 0));
}
