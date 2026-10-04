using ErpWeb.Core.Purchase;

namespace ErpWeb.Tests.Procurement.Inquiry;
[Trait(TestCategories.Name, TestCategories.Purchase)]
[Trait(TestCategories.Name, TestCategories.PurchaseOrder)]
public class PoDeliveryPerformanceCalcTests
{
    private static readonly DateTime Eta = DateTime.Parse("2026-09-10");
    private static readonly DateTime AsOf = DateTime.Parse("2026-09-20");

    [Fact]
    public void Walk_PartialThenLate_ComputesFullyReceivedAndCappedQty()
    {
        var movements = new List<PoDeliveryPerformanceCalc.ReceiptMovement>
        {
            new(1, DateTime.Parse("2026-09-10"), 1, 1, 40m),
            new(2, DateTime.Parse("2026-09-15"), 1, 2, 30m),
            new(3, DateTime.Parse("2026-09-20"), 1, 3, 30m)
        };

        var walk = PoDeliveryPerformanceCalc.WalkReceipts(100m, Eta, movements);
        Assert.Equal(DateTime.Parse("2026-09-10"), walk.FirstGrnDate);
        Assert.Equal(DateTime.Parse("2026-09-20"), walk.LastGrnDate);
        Assert.Equal(DateTime.Parse("2026-09-20"), walk.FullyReceivedDate);
        Assert.Equal(40m, walk.OnTimeReceivedQty);
        Assert.Equal(60m, walk.LateReceivedQty);
        Assert.Equal(0m, walk.Receipts[^1].RemainingQty);
        Assert.Equal(3, walk.GrBatchCount);
    }

    [Fact]
    public void Walk_OverReceipt_CapsOtifQty_RemainingNeverNegative()
    {
        var movements = new List<PoDeliveryPerformanceCalc.ReceiptMovement>
        {
            new(1, DateTime.Parse("2026-09-08"), 1, 1, 120m)
        };

        var walk = PoDeliveryPerformanceCalc.WalkReceipts(100m, Eta, movements);
        Assert.Equal(100m, walk.OnTimeReceivedQty);
        Assert.Equal(0m, walk.LateReceivedQty);
        Assert.Equal(DateTime.Parse("2026-09-08"), walk.FullyReceivedDate);
        Assert.Equal(120m, walk.Receipts[0].CumulativeQty);
        Assert.Equal(0m, walk.Receipts[0].RemainingQty);
    }

    [Fact]
    public void Status_PartialOverdue_WinsOverOverdue()
    {
        var status = PoDeliveryPerformanceCalc.ResolveStatus(
            poPurQty: 100m,
            balanceQty: 60m,
            netReceivedQty: 40m,
            etaDate: Eta,
            fullyReceivedDate: null,
            asOf: AsOf);
        Assert.Equal(PoDeliveryPerformanceStatuses.PartiallyReceived, status);
        Assert.Equal(10, PoDeliveryPerformanceCalc.ComputeOpenDaysLate(60m, Eta, AsOf));
    }

    [Fact]
    public void Status_CompletedWithoutFullyReceived_IsException()
    {
        var status = PoDeliveryPerformanceCalc.ResolveStatus(
            100m, 0m, 100m, Eta, fullyReceivedDate: null, AsOf);
        Assert.Equal(PoDeliveryPerformanceStatuses.Exception, status);
    }

    [Fact]
    public void Status_EarlyOnTimeLate()
    {
        Assert.Equal(PoDeliveryPerformanceStatuses.Early,
            PoDeliveryPerformanceCalc.ResolveStatus(10m, 0m, 10m, Eta, DateTime.Parse("2026-09-08"), AsOf));
        Assert.Equal(PoDeliveryPerformanceStatuses.OnTime,
            PoDeliveryPerformanceCalc.ResolveStatus(10m, 0m, 10m, Eta, Eta, AsOf));
        Assert.Equal(PoDeliveryPerformanceStatuses.Late,
            PoDeliveryPerformanceCalc.ResolveStatus(10m, 0m, 10m, Eta, DateTime.Parse("2026-09-12"), AsOf));
    }

    [Fact]
    public void Preset_OverdueIncludesPartial_IndependentOfStatus()
    {
        Assert.True(PoDeliveryPerformanceCalc.MatchesPreset(
            PoInquiryWorkbenchPresets.Overdue, 100m, 60m, 40m, Eta, null, AsOf));
        Assert.True(PoDeliveryPerformanceCalc.MatchesPreset(
            PoInquiryWorkbenchPresets.Partial, 100m, 60m, 40m, Eta, null, AsOf));
        Assert.False(PoDeliveryPerformanceCalc.MatchesPreset(
            PoInquiryWorkbenchPresets.AllOpen, 0m, 0m, 0m, Eta, null, AsOf));
    }

    [Fact]
    public void Preset_OnTimeCompletedIncludesEarly()
    {
        Assert.True(PoDeliveryPerformanceCalc.MatchesPreset(
            PoInquiryWorkbenchPresets.OnTimeCompleted,
            10m, 0m, 10m, Eta, DateTime.Parse("2026-09-08"), AsOf));
    }

    [Fact]
    public void DueToday_IsNotOverdue()
    {
        var status = PoDeliveryPerformanceCalc.ResolveStatus(
            10m, 10m, 0m, AsOf, null, AsOf);
        Assert.Equal(PoDeliveryPerformanceStatuses.NotDue, status);
    }
}
