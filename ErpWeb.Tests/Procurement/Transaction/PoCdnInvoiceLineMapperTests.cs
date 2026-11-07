using ErpWeb.Core.Purchase;
using ErpWeb.UI.Purchase.Transactions;

namespace ErpWeb.Tests.Procurement.Transaction;

[Trait(TestCategories.Name, TestCategories.Purchase)]
[Trait(TestCategories.Name, TestCategories.PurchaseCdn)]
public class PoCdnInvoiceLineMapperTests
{
    [Fact]
    public void TryMap_copies_identities_and_historic_inputs_without_inventing_stock_flags()
    {
        var row = SupportedRow(line: 7);

        var result = PoCdnInvoiceLineMapper.TryMap(row);

        Assert.True(result.IsSupported);
        var line = result.Line!;
        Assert.Equal((short)7, line.InvLineNo);
        Assert.Equal("ITEM1", line.ICode);
        Assert.Equal("Stock item", line.IDesc);
        Assert.Equal(10m, line.Qty);
        Assert.Equal(100m, line.UnitPrice);
        Assert.Equal(2m, line.ItemDiscount);
        Assert.Equal(1m, line.ItemDiscount1);
        Assert.Equal("%", line.IDiscountType);
        Assert.Equal("A", line.IDiscountType1);
        Assert.True(line.IsInclusive);
        Assert.Equal("SR", line.TaxGroup);
        Assert.Equal("GLITEM1", line.ItemGlCode);
        Assert.Equal("PO1", line.PoNo);
        Assert.Equal((short)1, line.PoRelNo);
        Assert.Equal((short)2, line.PoLineNo);
        Assert.Equal(1m, line.StdCustPSize);
        Assert.Equal("EA", line.StdUom);
        Assert.Equal(0m, line.CostPrice);
        Assert.False(line.IsStockReturn);
        Assert.Null(line.FromBalLocId);
        Assert.Null(line.LotNo);
        Assert.False(line.LotControl);
        Assert.False(line.StockControl);
        Assert.False(line.IsTaxOnly);
        Assert.Null(line.Remarks);
        Assert.False(string.IsNullOrWhiteSpace(line.ClientKey));

        var request = line.ToRequest();
        Assert.Equal((short)7, request.InvLineNo);
        Assert.Equal(2m, request.ItemDiscount);
        Assert.Equal("PO1", request.PoNo);
        Assert.False(request.IsStockReturn);
    }

    [Fact]
    public void TryMap_does_not_block_a_leftover_discount_amount()
    {
        var row = SupportedRow(discount: 12m);

        var result = PoCdnInvoiceLineMapper.TryMap(row);

        Assert.True(result.IsSupported, result.BlockReason);
        Assert.Equal(12m, row.Discount);
    }

    [Fact]
    public void TryMap_blocks_independent_unrepresentable_discount_levels()
    {
        var row = SupportedRow(line: 3, itemDiscount2: 5m);

        var result = PoCdnInvoiceLineMapper.TryMap(row);

        Assert.False(result.IsSupported);
        Assert.Null(result.Line);
        Assert.Contains("3", result.BlockReason);
        Assert.Contains("ItemDiscount2", result.BlockReason);
    }

    [Fact]
    public void MapSelection_rejects_the_entire_set_when_any_row_is_unsupported()
    {
        var rows = new[]
        {
            SupportedRow(line: 1),
            SupportedRow(line: 2, itemDiscount3: 1m),
            SupportedRow(line: 3)
        };

        var result = PoCdnInvoiceLineMapper.MapSelection(rows);

        Assert.False(result.IsSupported);
        Assert.Empty(result.Lines);
        Assert.Contains("2", result.ErrorMessage);
        Assert.DoesNotContain("1", result.ErrorMessage ?? string.Empty);
    }

    [Fact]
    public void MapSelection_accepts_one_three_and_all_supported_rows()
    {
        var one = PoCdnInvoiceLineMapper.MapSelection([SupportedRow(line: 1)]);
        Assert.True(one.IsSupported);
        Assert.Equal((short)1, Assert.Single(one.Lines).InvLineNo);

        var three = PoCdnInvoiceLineMapper.MapSelection(
        [
            SupportedRow(line: 1),
            SupportedRow(line: 4),
            SupportedRow(line: 9)
        ]);
        Assert.True(three.IsSupported);
        Assert.Equal(new short[] { 1, 4, 9 }, three.Lines.Select(x => x.InvLineNo!.Value).ToArray());
    }

    [Fact]
    public void TryMap_blocks_missing_item_or_pack_size()
    {
        Assert.False(PoCdnInvoiceLineMapper.TryMap(SupportedRow(iCode: " ")).IsSupported);
        Assert.False(PoCdnInvoiceLineMapper.TryMap(SupportedRow(stdCustPSize: 0m)).IsSupported);
    }

    private static PoCdnInvoiceLinePickerRow SupportedRow(
        short line = 1,
        string? iCode = "ITEM1",
        decimal stdCustPSize = 1m,
        decimal discount = 0m,
        decimal itemDiscount2 = 0m,
        decimal itemDiscount3 = 0m) => new()
    {
        Line = line,
        ICode = iCode,
        IDesc = "Stock item",
        Qty = 10m,
        StdQty = 10m,
        StdCustPSize = stdCustPSize,
        UnitPrice = 100m,
        Amount = 1000m,
        NetAmount = 1000m,
        TaxGroup = "SR",
        IsInclusive = true,
        ItemGlCode = "GLITEM1",
        PoNo = "PO1",
        PoRelNo = 1,
        PoLineNo = 2,
        ItemDiscount = 2m,
        ItemDiscount1 = 1m,
        IDiscountType = "%",
        IDiscountType1 = "A",
        StdUom = "EA",
        SellingUom = "EA",
        Discount = discount,
        ItemDiscount2 = itemDiscount2,
        ItemDiscount3 = itemDiscount3,
        RemainingStdQty = 6m,
        RemainingAmount = 600m
    };
}
