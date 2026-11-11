using ErpWeb.Core.Sales;
using ErpWeb.UI.Sales.Transactions;

namespace ErpWeb.Tests.Sales.Transaction;

[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.SalesInvoice)]
public sealed class SaInvoiceFromDeliveryOrderTests
{
    [Fact]
    public void FromDeliveryOrder_copies_commercial_slots_and_drops_override_and_amounts()
    {
        var dto = CommercialDoLine(
            doNo: "DO-1",
            line: 1,
            remaining: 25m,
            unitPrice: 10m,
            discount2: 5m,
            amount2: 1.25m,
            taxGroup: "SR",
            orderType: "STD",
            original: 12m,
            reason: "promo");

        var vm = SaInvoiceLineVm.FromDeliveryOrder(dto);

        Assert.True(vm.LinkDo);
        Assert.Equal("DO-1", vm.DoNo);
        Assert.Equal((short)1, vm.DoLine);
        Assert.Equal(25m, vm.Qty);
        Assert.Equal(0m, vm.SoConsumedQty);
        Assert.Equal(10m, vm.UnitPrice);
        Assert.Equal("CUSTOMER_ITEM", vm.PricingSource);
        Assert.Equal("MOQ=10", vm.PricingRef);
        Assert.Null(vm.OriginalUnitPrice);
        Assert.Null(vm.OverrideReason);
        Assert.Equal(10m, vm.ItemDiscount);
        Assert.Equal(5m, vm.ItemDiscount2);
        Assert.Equal(3m, vm.ItemDiscount3);
        Assert.Equal(2m, vm.ItemDiscount4);
        Assert.Equal(1m, vm.ItemDiscount5);
        Assert.Equal(0.5m, vm.ItemDiscount6);
        Assert.Equal(0.5m, vm.ItemDiscAmount);
        Assert.Equal(1.25m, vm.ItemDiscAmount1);
        Assert.True(vm.IsInclusive);
        Assert.Equal("SR", vm.TaxGrCode);
        Assert.Equal("STD", vm.OrderType);
        Assert.Equal("BOX", vm.StdUom);
        Assert.Equal("BOX", vm.SellingUom);
        Assert.Equal("BOX", SaLinePricingTrigger.CommercialUom(vm.SellingUom, vm.StdUom));
        Assert.Equal(0m, vm.Amount);
        Assert.Equal(0m, vm.TaxAmt);
        Assert.Equal(0m, vm.NetAmount);
        Assert.Null(vm.StdPackSize);

        var request = vm.ToRequest();
        Assert.Equal(10m, request.ItemDiscount);
        Assert.Equal(1.25m, request.ItemDiscAmount1);
        Assert.True(request.IsInclusive);
        Assert.Equal("SR", request.TaxGrCode);
        Assert.Equal("STD", request.OrderType);
        Assert.Null(request.OriginalUnitPrice);
        Assert.Null(request.OverrideReason);
    }

    [Fact]
    public void FromDeliveryOrder_keeps_per_source_terms_on_mixed_dos()
    {
        var first = SaInvoiceLineVm.FromDeliveryOrder(
            CommercialDoLine("DO-A", 1, remaining: 40m, unitPrice: 10m, discount2: 0m, amount2: 0m, taxGroup: "SR", orderType: "A"));
        var second = SaInvoiceLineVm.FromDeliveryOrder(
            CommercialDoLine("DO-B", 1, remaining: 15m, unitPrice: 12m, discount2: 5m, amount2: 2m, taxGroup: "ZR", orderType: "B"));

        Assert.Equal(10m, first.UnitPrice);
        Assert.Equal(0m, first.ItemDiscount2);
        Assert.Equal("SR", first.TaxGrCode);
        Assert.Equal("A", first.OrderType);

        Assert.Equal(12m, second.UnitPrice);
        Assert.Equal(5m, second.ItemDiscount2);
        Assert.Equal(2m, second.ItemDiscAmount1);
        Assert.Equal("ZR", second.TaxGrCode);
        Assert.Equal("B", second.OrderType);
        Assert.NotEqual(first.UnitPrice, second.UnitPrice);
    }

    [Fact]
    public void C08_inherited_qty_change_keeps_unit_price_and_recalcs_amount()
    {
        var vm = SaInvoiceLineVm.FromDeliveryOrder(
            CommercialDoLine("DO-1", 1, remaining: 40m, unitPrice: 10m, discount2: 0m, amount2: 0m, taxGroup: "SR", orderType: "STD"));
        vm.ItemDiscount = 0m;
        vm.ItemDiscount3 = 0m;
        vm.ItemDiscount4 = 0m;
        vm.ItemDiscount5 = 0m;
        vm.ItemDiscount6 = 0m;
        vm.ItemDiscAmount = 0m;
        vm.IsInclusive = false;

        Assert.True(vm.HasSource);
        Assert.False(SaLinePricingTrigger.AllowLiveResolve(
            isEditingExistingLine: true,
            hasInheritedSource: vm.HasSource,
            isManualOverride: false));

        var before = vm.ToCalcState();
        SaInvoiceCalc.CalculateLine(before, taxPercent: 0m, decPoint: false, discMethod: null);
        Assert.Equal(10m, before.UnitPrice);
        Assert.Equal(400m, before.Amount);

        vm.Qty = 25m;
        var after = vm.ToCalcState();
        SaInvoiceCalc.CalculateLine(after, taxPercent: 0m, decPoint: false, discMethod: null);
        Assert.Equal(10m, after.UnitPrice);
        Assert.Equal(25m, after.Qty);
        Assert.Equal(250m, after.Amount);
        Assert.Equal("CUSTOMER_ITEM", vm.PricingSource);
    }

    [Fact]
    public void C19_saved_direct_invoice_line_does_not_live_resolve_on_qty()
    {
        var vm = SaInvoiceLineVm.FromDto(new SaInvoiceLineDto
        {
            Line = 1,
            ICode = "A100",
            Qty = 2m,
            UnitPrice = 10m,
            TaxGrCode = "SR"
        });

        Assert.False(vm.HasSource);
        Assert.False(SaLinePricingTrigger.AllowLiveResolve(
            isEditingExistingLine: true,
            hasInheritedSource: vm.HasSource,
            isManualOverride: false));
        Assert.True(SaLinePricingTrigger.AllowLiveResolve(
            isEditingExistingLine: false,
            hasInheritedSource: vm.HasSource,
            isManualOverride: false));
    }

    [Fact]
    public void FromDto_round_trips_order_type_on_later_edit()
    {
        var dto = new SaInvoiceLineDto
        {
            Line = 1,
            ICode = "A100",
            Qty = 2m,
            UnitPrice = 10m,
            OrderType = "STD",
            TaxGrCode = "SR",
            ItemDiscount = 10m
        };

        var vm = SaInvoiceLineVm.FromDto(dto);
        Assert.Equal("STD", vm.OrderType);
        Assert.Equal("STD", vm.ToRequest().OrderType);
        Assert.Equal("STD", vm.Clone().OrderType);
    }

    private static SaDoBillableLineDto CommercialDoLine(
        string doNo,
        short line,
        decimal remaining,
        decimal unitPrice,
        decimal discount2,
        decimal amount2,
        string taxGroup,
        string orderType,
        decimal? original = null,
        string? reason = null) =>
        new()
        {
            DoNo = doNo,
            Line = line,
            ICode = "PACK1",
            RemainingBillableQty = remaining,
            Qty = remaining,
            UnitPrice = unitPrice,
            PricingSource = "CUSTOMER_ITEM",
            PricingRef = "MOQ=10",
            OriginalUnitPrice = original,
            OverrideReason = reason,
            SellingUom = "BOX",
            StdUom = "BOX",
            FrWarehouse = "MAIN",
            StockControl = true,
            ItemDiscount = 10m,
            ItemDiscount2 = discount2,
            ItemDiscount3 = 3m,
            ItemDiscount4 = 2m,
            ItemDiscount5 = 1m,
            ItemDiscount6 = 0.5m,
            ItemDiscAmount = 0.5m,
            ItemDiscAmount1 = amount2,
            IsInclusive = true,
            TaxGroup = taxGroup,
            OrderType = orderType
        };
}
