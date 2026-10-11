using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ErpWeb.Tests.Procurement.Inquiry;

[Trait(TestCategories.Name, TestCategories.Purchase)]
[Trait(TestCategories.Name, TestCategories.PurchaseOrder)]
public sealed class PoProcurementKpiServiceTests
{
    [Fact]
    public async Task Summary_UsesCompletedDeliveryDenominator_AndReportsMissingEtaAndPartialSeparately()
    {
        var inquiry = CreateInquiryMock();
        inquiry.Setup(x => x.GetDeliveryPerformanceLinesAsync(
                MenuCodes.PurchaseKpi, It.IsAny<PoInquiryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IvMasterOperationResult<PoInquiryPage<PoDeliveryPerformanceLineRow>>.Ok(new()
            {
                TotalCount = 4,
                Rows =
                [
                    new() { DeliveryStatus = PoDeliveryPerformanceStatuses.Early, EtaDate = new DateTime(2026, 10, 1) },
                    new() { DeliveryStatus = PoDeliveryPerformanceStatuses.Late, EtaDate = new DateTime(2026, 10, 1) },
                    new() { DeliveryStatus = PoDeliveryPerformanceStatuses.PartiallyReceived, EtaDate = new DateTime(2026, 10, 1) },
                    new() { DeliveryStatus = PoDeliveryPerformanceStatuses.NotDue, EtaDate = null }
                ]
            }));

        var result = await CreateSut(inquiry.Object).GetSummaryAsync(new PoInquiryQuery
        {
            DateFrom = new DateTime(2026, 10, 1),
            DateTo = new DateTime(2026, 10, 11),
            AsOfDate = new DateTime(2026, 10, 11)
        });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Data!.OnTimeCompletedLines);
        Assert.Equal(2, result.Data.CompletedDeliveryLines);
        Assert.Equal(50m, result.Data.OnTimePercentage);
        Assert.Equal(1, result.Data.MissingEtaLines);
        Assert.Equal(1, result.Data.PartialDeliveryLines);
        Assert.Contains("Select one item", result.Data.PriceUnavailableReason);
    }

    [Fact]
    public async Task PriceMovement_IsUnavailable_WhenLatestSamplesUseDifferentPurchaseUom()
    {
        var inquiry = CreateInquiryMock();
        inquiry.Setup(x => x.GetDeliveryPerformanceLinesAsync(
                MenuCodes.PurchaseKpi, It.IsAny<PoInquiryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IvMasterOperationResult<PoInquiryPage<PoDeliveryPerformanceLineRow>>.Ok(new()));
        inquiry.Setup(x => x.GetPurchasePriceHistoryAsync(
                MenuCodes.PurchaseKpi, It.IsAny<PoInquiryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IvMasterOperationResult<PoInquiryPage<PoPurchasePriceHistoryRow>>.Ok(new()
            {
                TotalCount = 2,
                Rows =
                [
                    new() { Currency = "MYR", Uom = "BOX", NetUnitPrice = 20m },
                    new() { Currency = "MYR", Uom = "EA", NetUnitPrice = 2m }
                ]
            }));

        var result = await CreateSut(inquiry.Object).GetSummaryAsync(new PoInquiryQuery
        {
            ItemCode = "ITEM1",
            Currency = "MYR",
            AsOfDate = new DateTime(2026, 10, 11)
        });

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(result.Data!.LatestNetUnitPrice);
        Assert.Contains("same verified purchase UOM", result.Data.PriceUnavailableReason);
    }

    private static Mock<IPoPurchaseInquiryService> CreateInquiryMock()
    {
        var inquiry = new Mock<IPoPurchaseInquiryService>();
        inquiry.Setup(x => x.GetPoOutstandingSummaryAsync(
                MenuCodes.PurchaseKpi, It.IsAny<PoInquiryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IvMasterOperationResult<PoOrderOutstandingSummary>.Ok(new()
            {
                OpenLineCount = 8,
                OverdueLineCount = 2
            }));
        inquiry.Setup(x => x.GetPoMatchingSummaryAsync(
                MenuCodes.PurchaseKpi, It.IsAny<PoInquiryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IvMasterOperationResult<PoMatchingSummary>.Ok(new() { ReceivedNotInvoicedCount = 3 }));
        return inquiry;
    }

    private static PoProcurementKpiService CreateSut(IPoPurchaseInquiryService inquiry)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Features:ProcurementKpi"] = "true" })
            .Build();
        return new PoProcurementKpiService(
            inquiry, configuration, new FixedCurrentDateService(new DateTime(2026, 10, 11)));
    }
}
