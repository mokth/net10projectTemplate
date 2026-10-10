using ErpWeb.Core.Pricing;
using ErpWeb.Model.Entities.Sales;

namespace ErpWeb.Tests.Sales.Master;

[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.SalesMasters)]
public sealed class SalesPriceChangeAuditFactoryTests
{
    [Fact]
    public void Build_batch_snapshots_lines_and_normalizes_protocol_tokens()
    {
        var changedAt = new DateTime(2026, 10, 10, 4, 30, 0, DateTimeKind.Utc);
        var batch = SalesPriceChangeAuditFactory.BuildBatch(
            new SalesPriceChangeAuditContext(
                "demo",
                SalesPriceChangeAuditOrigins.PriceReviewWorkbench,
                SalesPriceChangeAuditTargets.ItemDefault,
                new DateTime(2026, 10, 10, 18, 0, 0),
                "operator",
                AdjustmentMethod: "set_price",
                AdjustmentValue: 12.5m,
                RoundingMode: "normal",
                DecimalPlaces: 2,
                Reason: "  Annual review  ",
                ChangedAtUtc: changedAt),
            [new SalesPriceChangeAuditLine(
                SalesPriceChangeKinds.Update,
                " ITEM001 ",
                ItemDescriptionSnapshot: "Widget",
                OldUom: "PCS",
                NewUom: "PCS",
                OldPrice: 10m,
                NewPrice: 12.5m)]);

        Assert.Equal("DEMO", batch.CompanyCode);
        Assert.Equal(SalesPriceChangeAuditOrigins.PriceReviewWorkbench, batch.Origin);
        Assert.Equal(SalesPriceChangeAuditTargets.ItemDefault, batch.TargetType);
        Assert.Equal("SET_PRICE", batch.AdjustmentMethod);
        Assert.Equal("NORMAL", batch.RoundingMode);
        Assert.Equal("Annual review", batch.Reason);
        Assert.Equal(changedAt, batch.ChangedAtUtc);
        Assert.Equal(1, batch.ChangedRowCount);

        var line = Assert.Single(batch.Lines);
        Assert.Equal("ITEM001", line.ItemCode);
        Assert.Equal(SalesPriceChangeKinds.Update, line.ChangeKind);
        Assert.Equal(10m, line.OldPrice);
        Assert.Equal(12.5m, line.NewPrice);
    }

    [Fact]
    public void Legacy_money_comparison_uses_four_decimal_scale()
    {
        Assert.True(SalesPriceChangeAuditFactory.MoneyEquals(12.34567d, 12.3457m));
        Assert.False(SalesPriceChangeAuditFactory.MoneyEquals(12.34564d, 12.3457m));
        Assert.Equal(12.3457d, SalesPriceChangeAuditFactory.UnscaleLegacyMoney(12.34567m));
        Assert.Null(SalesPriceChangeAuditFactory.NormalizeLegacyMoney(null));
    }
}
