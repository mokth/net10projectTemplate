using ErpWeb.Core.Sales;

namespace ErpWeb.Tests;

/// <summary>
/// Price and discount resolution contract — the pure rules (plan §7, §7.1, §7.2, §8, §8.1, §8.1.1,
/// §8.3, §8.3.1, §9.1 "artefact + test").
///
/// These tests need no database: the contract is what the future SO/DO/INV/CN consumer must obey, so it
/// is pinned here before that consumer exists. Two rules carry the most risk and are asserted most
/// heavily: "no price" must BLOCK (never 0 — the legacy defect), and JOIN vs SPLIT must not silently
/// swap behaviour.
/// </summary>
public class SaItemFamilyPricingContractTests
{
    private static readonly DateTime DocDate = new(2026, 9, 15);

    // ═════════════════════════════ §7 step 0 — dealer mode ═════════════════════════════

    [Theory]
    [InlineData("FOLLOW DEFAULT DEALER PRICE", true)]
    [InlineData("follow default dealer price", true)]
    [InlineData("FOLLOW SELLING PRICE X DISCOUNT", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DealerPriceMethod_IsDetectedCaseInsensitively(string? priceMethod, bool expected) =>
        Assert.Equal(expected, SaItemFamilyPriceResolver.IsDealerPriceMethod(priceMethod));

    [Fact]
    public void E6_DealerCustomer_FailsClosed_AndDoesNotFallBackToSellingPrice()
    {
        var resolution = Resolve(
            Request(priceMethod: "FOLLOW DEFAULT DEALER PRICE"),
            new SaItemFamilyPriceCandidates
            {
                CustomerItems = [Customer("I1", "PCS", moq: 0, price: 12.50m)],
                Items = [Item("I1", price: 14m)]
            });

        Assert.False(resolution.Found);
        Assert.Null(resolution.UnitPrice);
        Assert.Contains("Dealer pricing is not supported", resolution.Message!);
    }

    // ═════════════════════════════ §7.2 worked examples ═════════════════════════════

    [Fact]
    public void E1_CustomerItemWins_AndLaterStepsAreNotConsulted()
    {
        var resolution = Resolve(
            Request(custPriceCode: "PL1"),
            new SaItemFamilyPriceCandidates
            {
                CustomerItems = [Customer("I1", "PCS", moq: 0, price: 12.50m)],
                PriceListLines = [PriceLine("PL1", "I1", "PCS", 13.75m)],
                Items = [Item("I1", price: 14m)]
            });

        Assert.True(resolution.Found);
        Assert.Equal(SaItemFamilyPriceSources.CustomerItem, resolution.Source);
        Assert.Equal(12.50m, resolution.UnitPrice);
        Assert.Equal(0, resolution.MatchedMoq);
    }

    [Fact]
    public void E2_HighestMoqAtOrBelowQuantityWins()
    {
        var resolution = Resolve(
            Request(qty: 10m),
            new SaItemFamilyPriceCandidates
            {
                CustomerItems =
                [
                    Customer("I1", "PCS", moq: 5, price: 12.00m),
                    Customer("I1", "PCS", moq: 10, price: 11.00m),
                    Customer("I1", "PCS", moq: 50, price: 9.00m)   // above the ordered qty -> not eligible
                ]
            });

        Assert.True(resolution.Found);
        Assert.Equal(11.00m, resolution.UnitPrice);
        Assert.Equal(10, resolution.MatchedMoq);
    }

    [Fact]
    public void E3_PriceListWins_WhenNoCustomerItemRowExists()
    {
        var resolution = Resolve(
            Request(custPriceCode: "PL1"),
            new SaItemFamilyPriceCandidates
            {
                PriceListLines = [PriceLine("PL1", "I1", "PCS", 13.75m)],
                Items = [Item("I1", price: 14m)]
            });

        Assert.True(resolution.Found);
        Assert.Equal(SaItemFamilyPriceSources.PriceList, resolution.Source);
        Assert.Equal(13.75m, resolution.UnitPrice);
        Assert.Null(resolution.MatchedMoq);
    }

    [Fact]
    public void E4_ItemSellingPriceWins_WhenUomMatches()
    {
        var resolution = Resolve(
            Request(uom: "PCS"),
            new SaItemFamilyPriceCandidates { Items = [Item("I1", price: 14m, sellingUom: "PCS")] });

        Assert.True(resolution.Found);
        Assert.Equal(SaItemFamilyPriceSources.ItemSellingPrice, resolution.Source);
        Assert.Equal(14m, resolution.UnitPrice);
    }

    [Fact]
    public void E4b_ItemSellingPrice_FallsBackToStdUomWhenSellingUomIsBlank()
    {
        var resolution = Resolve(
            Request(uom: "PCS"),
            new SaItemFamilyPriceCandidates { Items = [Item("I1", price: 14m, sellingUom: null, stdUom: "PCS")] });

        Assert.True(resolution.Found);
        Assert.Equal(14m, resolution.UnitPrice);
    }

    [Fact]
    public void E5_UomMismatch_IsNotConverted_AndBlocks()
    {
        var resolution = Resolve(
            Request(uom: "BOX"),
            new SaItemFamilyPriceCandidates { Items = [Item("I1", price: 14m, sellingUom: "PCS")] });

        Assert.False(resolution.Found);
        Assert.Null(resolution.UnitPrice);
        Assert.Contains("No price found", resolution.Message!);
    }

    [Fact]
    public void E7_NoCandidate_Blocks_AndNeverReturnsZero()
    {
        var resolution = Resolve(Request(), new SaItemFamilyPriceCandidates());

        Assert.False(resolution.Found);
        Assert.Null(resolution.UnitPrice);       // the legacy "default to 0" defect must not return
        Assert.Contains("No price found for item I1 / UOM PCS", resolution.Message!);
    }

    [Fact]
    public void E8_InactiveItemIsNotA_PerItemCandidate_ButCustomerItemStillResolves()
    {
        var inactive = Resolve(
            Request(),
            new SaItemFamilyPriceCandidates { Items = [Item("I1", price: 14m, isActive: false)] });
        Assert.False(inactive.Found);

        var withCustomerRow = Resolve(
            Request(),
            new SaItemFamilyPriceCandidates
            {
                CustomerItems = [Customer("I1", "PCS", moq: 0, price: 12.50m)],
                Items = [Item("I1", price: 14m, isActive: false)]
            });
        Assert.True(withCustomerRow.Found);
        Assert.Equal(12.50m, withCustomerRow.UnitPrice);
    }

    [Fact]
    public void E9_CustomerItemCurrencyMismatch_FailsClosed()
    {
        var resolution = Resolve(
            Request(documentCurrency: "MYR"),
            new SaItemFamilyPriceCandidates
            {
                CustomerItems = [Customer("I1", "PCS", moq: 0, price: 12.50m, currency: "USD")],
                PriceListLines = [PriceLine("PL1", "I1", "PCS", 13.75m)],
                Items = [Item("I1", price: 14m)]
            });

        // The winning candidate is unusable, so the chain stops rather than silently pricing from another
        // source the customer never agreed to (D8).
        Assert.False(resolution.Found);
        Assert.Contains("different currency", resolution.Message!);
    }

    [Fact]
    public void E9b_MatchingOrBlankCurrency_Resolves()
    {
        var matching = Resolve(
            Request(documentCurrency: "MYR"),
            new SaItemFamilyPriceCandidates
            {
                CustomerItems = [Customer("I1", "PCS", moq: 0, price: 12.50m, currency: "myr")]
            });
        Assert.True(matching.Found);

        var blank = Resolve(
            Request(documentCurrency: "MYR"),
            new SaItemFamilyPriceCandidates
            {
                CustomerItems = [Customer("I1", "PCS", moq: 0, price: 12.50m, currency: null)]
            });
        Assert.True(blank.Found);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void E10_ZeroOrNegativeStoredPrice_IsNotACandidate_AndFallsThrough(int stored)
    {
        var resolution = Resolve(
            Request(custPriceCode: "PL1"),
            new SaItemFamilyPriceCandidates
            {
                CustomerItems = [Customer("I1", "PCS", moq: 0, price: stored)],
                PriceListLines = [PriceLine("PL1", "I1", "PCS", 13.75m)]
            });

        Assert.True(resolution.Found);
        Assert.Equal(SaItemFamilyPriceSources.PriceList, resolution.Source);
        Assert.Equal(13.75m, resolution.UnitPrice);
    }

    [Fact]
    public void E10b_NullStoredPrice_IsNotACandidate_AndTheNextBandIsConsidered()
    {
        var resolution = Resolve(
            Request(qty: 10m),
            new SaItemFamilyPriceCandidates
            {
                CustomerItems =
                [
                    Customer("I1", "PCS", moq: 10, price: null),
                    Customer("I1", "PCS", moq: 5, price: 12.00m)
                ]
            });

        Assert.True(resolution.Found);
        Assert.Equal(12.00m, resolution.UnitPrice);
        Assert.Equal(5, resolution.MatchedMoq);
    }

    [Fact]
    public void E11_UnknownCustPriceCode_FallsThrough_AndIsNotAnError()
    {
        var resolution = Resolve(
            Request(custPriceCode: "RETIRED"),
            new SaItemFamilyPriceCandidates
            {
                PriceListLines = [PriceLine("PL1", "I1", "PCS", 13.75m)],   // a different list
                Items = [Item("I1", price: 14m)]
            });

        Assert.True(resolution.Found);
        Assert.Equal(SaItemFamilyPriceSources.ItemSellingPrice, resolution.Source);
        Assert.Equal(14m, resolution.UnitPrice);
    }

    [Fact]
    public void BlankCustPriceCode_SkipsThePriceListStepEntirely()
    {
        var resolution = Resolve(
            Request(custPriceCode: null),
            new SaItemFamilyPriceCandidates
            {
                PriceListLines = [PriceLine("PL1", "I1", "PCS", 13.75m)],
                Items = [Item("I1", price: 14m)]
            });

        Assert.True(resolution.Found);
        Assert.Equal(SaItemFamilyPriceSources.ItemSellingPrice, resolution.Source);
    }

    [Fact]
    public void PriceCandidates_AreMatchedOnItemAndUom_CaseInsensitively()
    {
        var resolution = Resolve(
            Request(iCode: "i1", uom: "pcs"),
            new SaItemFamilyPriceCandidates { Items = [Item("I1", price: 14m, sellingUom: "PCS")] });

        Assert.True(resolution.Found);
        Assert.Equal(14m, resolution.UnitPrice);
    }

    // ═════════════════════════════ §7.1 D7 — price basis ═════════════════════════════

    [Fact]
    public void PriceBasis_ExclusiveMasterPrice_GrossesUp_AtTheBoundary()
    {
        // The shipped line engine un-taxes an inclusive UnitPrice; a master price is exclusive, so the
        // consumer must gross it up first (D7).
        Assert.Equal(106m, SaItemFamilyPriceBasis.ToInclusive(100m, 0.06m));
        Assert.Equal(100m, SaItemFamilyPriceBasis.ToExclusive(106m, 0.06m));
        Assert.Equal(100m, SaItemFamilyPriceBasis.ToInclusive(100m, 0m));
        Assert.Equal(100m, SaItemFamilyPriceBasis.ToInclusive(100m, -1m));
    }

    // ═════════════════════════════ §8.3 — rule matching ═════════════════════════════

    [Theory]
    [InlineData(1, true)]        // inclusive lower bound
    [InlineData(10, true)]       // inclusive upper bound
    [InlineData(5, true)]
    [InlineData(0.5, false)]
    [InlineData(10.0001, false)]
    public void BandMatch_IsInclusiveOnBothBounds(double qty, bool expected) =>
        Assert.Equal(expected, SaItemFamilyRuleMatch.BandContains(1m, 10m, (decimal)qty));

    [Fact]
    public void BandMatch_HasNoGapBetweenAdjacentBands()
    {
        Assert.False(SaItemFamilyRuleMatch.BandsOverlap(1m, 10m, 11m, 20m));
        Assert.True(SaItemFamilyRuleMatch.BandsOverlap(1m, 10m, 10m, 20m));
    }

    [Fact]
    public void Rule_WithNullOrPastEndDate_MatchesOnlyAsTheWindowAllows()
    {
        var openEnded = Rule(id: 1, qtyFr: 1m, qtyTo: 10m, dateFr: DocDate, dateTo: null);
        Assert.False(SaItemFamilyRuleMatch.Applies(openEnded, DiscountRequest(11m, DocDate)));   // outside the band
        Assert.True(SaItemFamilyRuleMatch.Applies(openEnded, DiscountRequest(5m, new DateTime(2030, 1, 1))));
        Assert.True(SaItemFamilyRuleMatch.Applies(openEnded, DiscountRequest(5m, DocDate)));

        var closed = Rule(id: 2, qtyFr: 1m, qtyTo: 10m, dateFr: DocDate, dateTo: DocDate);
        Assert.True(SaItemFamilyRuleMatch.Applies(closed, DiscountRequest(5m, DocDate)));         // inclusive
        Assert.False(SaItemFamilyRuleMatch.Applies(closed, DiscountRequest(5m, DocDate.AddDays(1))));
    }

    [Fact]
    public void WindowMatch_ComparesTheDatePart_SoAStoredTimeCannotExcludeTheBoundaryDay()
    {
        // The live legacy columns are `datetime`; a stored 15:00 must not stop the boundary day matching.
        var rule = Rule(id: 1, qtyFr: 1m, qtyTo: 10m, dateFr: DocDate.AddHours(15), dateTo: DocDate.AddHours(15));

        Assert.True(SaItemFamilyRuleMatch.Applies(rule, DiscountRequest(5m, DocDate.AddHours(9))));
        Assert.True(SaItemFamilyRuleMatch.Applies(rule, DiscountRequest(5m, DocDate.AddHours(23))));
        Assert.False(SaItemFamilyRuleMatch.Applies(rule, DiscountRequest(5m, DocDate.AddDays(1))));
    }

    [Fact]
    public void WindowsOverlap_TreatsNullOrEndDateAsOpenEnded()
    {
        var from = new DateTime(2026, 9, 1);
        var to = new DateTime(2026, 9, 30);

        Assert.True(SaItemFamilyRuleMatch.WindowsOverlap(from, to, new DateTime(2026, 9, 30), null));
        Assert.True(SaItemFamilyRuleMatch.WindowsOverlap(from, null, new DateTime(2020, 1, 1), to));
        Assert.False(SaItemFamilyRuleMatch.WindowsOverlap(from, to, new DateTime(2026, 10, 1), null));
    }

    [Fact]
    public void ClassSpecificRule_DoesNotApplyToAnotherClass()
    {
        var classRule = Rule(id: 1, qtyFr: 1m, qtyTo: 10m, dateFr: DocDate, iClass: "C1");
        Assert.True(SaItemFamilyRuleMatch.Applies(classRule, DiscountRequest(5m, DocDate, "C1")));
        Assert.False(SaItemFamilyRuleMatch.Applies(classRule, DiscountRequest(5m, DocDate, "C2")));

        var blankRule = Rule(id: 2, qtyFr: 1m, qtyTo: 10m, dateFr: DocDate, iClass: null);
        Assert.True(SaItemFamilyRuleMatch.Applies(blankRule, DiscountRequest(5m, DocDate, "C2")));
    }

    [Fact]
    public void RuleForAnotherItem_DoesNotApply() =>
        Assert.False(SaItemFamilyRuleMatch.Applies(
            new SaItemFamilyDiscountRule { Id = 1, ICode = "I2", QtyFr = 1m, QtyTo = 10m, DateFr = DocDate },
            DiscountRequest(5m, DocDate)));

    // ═════════════════════════════ §8.1.1 — JOIN vs SPLIT, pinned ═════════════════════════════

    [Theory]
    // slot 1            slot 2            method    expected per-unit discount (unit price 100.00)
    [InlineData(10, "PERCENTAGE", 5, "PERCENTAGE", "JOIN", 15.00)]
    [InlineData(10, "PERCENTAGE", 5, "PERCENTAGE", "SPLIT", 14.50)]
    [InlineData(10, "PERCENTAGE", 5, "PERCENTAGE", null, 14.50)]      // no method = sequential
    [InlineData(10, "PERCENTAGE", 2, "AMOUNT", "JOIN", 12.00)]
    [InlineData(10, "PERCENTAGE", 2, "AMOUNT", "SPLIT", 12.00)]
    [InlineData(0, null, 5, "AMOUNT", "JOIN", 5.00)]                  // amount slots 2.00 + 3.00
    [InlineData(0, null, 0, null, "JOIN", 0.00)]
    public void DiscountSlots_FollowTheDocumented_JOINandSPLIT_Semantics(
        double slot1,
        string? type1,
        double slot2,
        string? type2,
        string? method,
        double expected)
    {
        // Only the two legacy slots are used; the engine's percent slots 3-6 stay 0 by design (§8.2).
        var rule = new SaItemFamilyDiscountRule
        {
            Id = 1,
            ICode = "I1",
            QtyFr = 1m,
            QtyTo = 10m,
            DateFr = DocDate,
            Discount = (decimal)slot1,
            DiscountType = type1,
            Discount1 = (decimal)slot2,
            DiscountType1 = type2
        };

        var selection = SaItemFamilyDiscountResolver.Resolve(
            new SaItemFamilyDiscountRequest
            {
                ICode = "I1",
                Qty = 5m,
                DocDate = DocDate,
                UnitPrice = 100m,
                DiscountMethod = method
            },
            [rule]);

        Assert.True(selection.Found);
        Assert.Equal((decimal)expected, selection.DiscountPerUnit);
    }

    [Fact]
    public void DiscountSlots_MapOntoPercentSlotsOneAndTwo_AndAmountSlotsOneAndTwo()
    {
        var selection = SaItemFamilyDiscountResolver.Resolve(
            new SaItemFamilyDiscountRequest { ICode = "I1", Qty = 5m, DocDate = DocDate, UnitPrice = 100m, DiscountMethod = "SPLIT" },
            [Rule(id: 1, qtyFr: 1m, qtyTo: 10m, dateFr: DocDate, discount: 10m, discount1: 2.5m, discount1Type: "AMOUNT")]);

        Assert.Equal(10m, selection.PercentSlot1);
        Assert.Equal(0m, selection.PercentSlot2);
        Assert.Equal(0m, selection.AmountSlot1);
        Assert.Equal(2.5m, selection.AmountSlot2);
        Assert.Equal(12.50m, selection.DiscountPerUnit);
    }

    [Fact]
    public void BlankSlotType_WithAValue_IsTreatedAsPercentage()
    {
        // The save-time validator requires a type when a value is present; this only guards legacy rows.
        var rule = new SaItemFamilyDiscountRule
        {
            Id = 1,
            ICode = "I1",
            QtyFr = 1m,
            QtyTo = 10m,
            DateFr = DocDate,
            Discount = 10m,
            DiscountType = null
        };

        var selection = SaItemFamilyDiscountResolver.Resolve(
            new SaItemFamilyDiscountRequest { ICode = "I1", Qty = 5m, DocDate = DocDate, UnitPrice = 100m, DiscountMethod = "JOIN" },
            [rule]);

        Assert.Equal(10m, selection.PercentSlot1);
        Assert.Equal(10m, selection.DiscountPerUnit);
    }

    [Fact]
    public void NoMatchingRule_IsNotADiscount_AndIsNotAnError()
    {
        var selection = SaItemFamilyDiscountResolver.Resolve(
            DiscountRequest(50m, DocDate),
            [Rule(id: 1, qtyFr: 1m, qtyTo: 10m, dateFr: DocDate, discount: 10m)]);

        Assert.False(selection.Found);
        Assert.Null(selection.RuleId);
        Assert.Equal(0m, selection.DiscountPerUnit);
        Assert.Empty(selection.CompetingRuleIds);
    }

    // ═════════════════════════════ §8.3.1 — deterministic tie-break ═════════════════════════════

    [Fact]
    public void TieBreak_ClassSpecificBeatsBlankClass()
    {
        var selection = ResolveDiscount([
            Rule(id: 1, qtyFr: 1m, qtyTo: 10m, dateFr: DocDate, iClass: null, discount: 5m),
            Rule(id: 2, qtyFr: 1m, qtyTo: 10m, dateFr: DocDate, iClass: "C1", discount: 7m)
        ]);

        Assert.Equal(2, selection.RuleId);
        Assert.Equal([1], selection.CompetingRuleIds);
    }

    [Fact]
    public void TieBreak_ThenHigherQtyFrWins()
    {
        var selection = ResolveDiscount([
            Rule(id: 1, qtyFr: 1m, qtyTo: 10m, dateFr: DocDate, discount: 5m),
            Rule(id: 2, qtyFr: 4m, qtyTo: 10m, dateFr: DocDate, discount: 7m)
        ]);

        Assert.Equal(2, selection.RuleId);
        Assert.Equal([1], selection.CompetingRuleIds);
    }

    [Fact]
    public void TieBreak_ThenEarlierDateFrWins()
    {
        var selection = ResolveDiscount([
            Rule(id: 1, qtyFr: 1m, qtyTo: 10m, dateFr: DocDate.AddDays(-5), discount: 5m),
            Rule(id: 2, qtyFr: 1m, qtyTo: 10m, dateFr: DocDate, discount: 7m)
        ]);

        Assert.Equal(1, selection.RuleId);
        Assert.Equal([2], selection.CompetingRuleIds);
    }

    [Fact]
    public void TieBreak_ThenLowerIdWins_AndIsStable()
    {
        var rules = new[]
        {
            Rule(id: 9, qtyFr: 1m, qtyTo: 10m, dateFr: DocDate, discount: 7m),
            Rule(id: 3, qtyFr: 1m, qtyTo: 10m, dateFr: DocDate, discount: 5m)
        };

        var first = ResolveDiscount(rules);
        var second = ResolveDiscount(rules.Reverse().ToArray());

        Assert.Equal(3, first.RuleId);
        Assert.Equal(3, second.RuleId);            // order of the input collection cannot change the pick
        Assert.Equal([9], first.CompetingRuleIds);
    }

    [Fact]
    public void ThreeWayOverlap_ReportsEveryLosingRuleId()
    {
        var selection = ResolveDiscount([
            Rule(id: 1, qtyFr: 1m, qtyTo: 20m, dateFr: DocDate, discount: 1m),
            Rule(id: 2, qtyFr: 5m, qtyTo: 20m, dateFr: DocDate, discount: 2m),
            Rule(id: 3, qtyFr: 5m, qtyTo: 20m, dateFr: DocDate.AddDays(-1), discount: 3m)
        ]);

        Assert.Equal(3, selection.RuleId);
        Assert.Equal([2, 1], selection.CompetingRuleIds);
    }

    // ═════════════════════════════ helpers ═════════════════════════════

    private static SaItemFamilyPriceRequest Request(
        string custCode = "CUST1",
        string iCode = "I1",
        string uom = "PCS",
        decimal qty = 10m,
        string? priceMethod = null,
        string? custPriceCode = null,
        string? documentCurrency = null) =>
        new()
        {
            CustCode = custCode,
            ICode = iCode,
            UOM = uom,
            Qty = qty,
            DocDate = DocDate,
            PayCode = "CASH",
            PriceMethod = priceMethod,
            CustPriceCode = custPriceCode,
            DocumentCurrency = documentCurrency
        };

    private static SaItemFamilyPriceResolution Resolve(
        SaItemFamilyPriceRequest request,
        SaItemFamilyPriceCandidates candidates) =>
        SaItemFamilyPriceResolver.Resolve(request, candidates);

    private static SaItemCustPriceCandidate Customer(
        string iCode,
        string uom,
        int moq,
        decimal? price,
        string? currency = null) =>
        new() { ICode = iCode, UOM = uom, MOQ = moq, UnitPrice = price, Currency = currency };

    private static IvCustPriceCandidate PriceLine(string custPriceCode, string iCode, string uom, decimal? price) =>
        new() { CustPriceCode = custPriceCode, ICode = iCode, UOM = uom, SellingPrice = price };

    private static IvStockMasterPriceCandidate Item(
        string iCode,
        decimal? price,
        string? sellingUom = "PCS",
        string? stdUom = null,
        bool isActive = true) =>
        new() { ICode = iCode, SellingPrice = price, SellingUom = sellingUom, StdUom = stdUom, IsActive = isActive };

    private static SaItemFamilyDiscountRequest DiscountRequest(decimal qty, DateTime docDate, string? iClass = null) =>
        new() { ICode = "I1", IClass = iClass, Qty = qty, DocDate = docDate, UnitPrice = 100m, DiscountMethod = "JOIN" };

    private static SaItemFamilyDiscountSelection ResolveDiscount(IEnumerable<SaItemFamilyDiscountRule> rules) =>
        SaItemFamilyDiscountResolver.Resolve(DiscountRequest(5m, DocDate), rules);

    private static SaItemFamilyDiscountRule Rule(
        int id,
        decimal qtyFr,
        decimal qtyTo,
        DateTime dateFr,
        DateTime? dateTo = null,
        string? iClass = null,
        decimal? discount = null,
        string? discountType = null,
        decimal? discount1 = null,
        string? discount1Type = null) =>
        new()
        {
            Id = id,
            ICode = "I1",
            IClass = iClass,
            QtyFr = qtyFr,
            QtyTo = qtyTo,
            DateFr = dateFr,
            DateTo = dateTo,
            Discount = discount,
            DiscountType = discountType ?? (discount is null ? null : SaDiscountSlotTypes.Percentage),
            Discount1 = discount1,
            DiscountType1 = discount1Type ?? (discount1 is null ? null : SaDiscountSlotTypes.Percentage)
        };
}
