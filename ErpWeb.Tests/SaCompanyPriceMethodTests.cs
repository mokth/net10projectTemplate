using ErpWeb.Core.Sales;

namespace ErpWeb.Tests;

/// <summary>
/// Company pricing mode + the four-stage pipeline contract (plan sections "Company pricing mode",
/// "Line pricing pipeline", "Target resolution order").
///
/// Pure and DB-free: the mode only selects which sources are ELIGIBLE, so the whole contract can be
/// pinned without a database. The single most important assertion here is the PIPELINE ORDER — the
/// discount must be resolved against the tax-BASIS-CONVERTED price, because
/// <c>SaInvoiceCalc.CalculateLine</c> derives the per-unit discount from the line's raw UnitPrice and
/// only then divides by (1 + t).
/// </summary>
public class SaCompanyPriceMethodTests
{
    private static readonly DateTime DocDate = new(2026, 9, 15);

    // ═════════════════════════════ mode normalisation ═════════════════════════════

    [Theory]
    [InlineData(null, SaCompanyPriceMethod.CustomerItemAndList)]
    [InlineData("", SaCompanyPriceMethod.CustomerItemAndList)]
    [InlineData("   ", SaCompanyPriceMethod.CustomerItemAndList)]
    [InlineData("NOT_A_METHOD", SaCompanyPriceMethod.CustomerItemAndList)]
    [InlineData("customer_item_only", SaCompanyPriceMethod.CustomerItemOnly)]
    [InlineData("PRICE_LIST_ONLY", SaCompanyPriceMethod.PriceListOnly)]
    [InlineData("  Item_Default_Only  ", SaCompanyPriceMethod.ItemDefaultOnly)]
    [InlineData("CUSTOMER_ITEM_AND_LIST", SaCompanyPriceMethod.CustomerItemAndList)]
    public void Normalize_UnknownOrBlank_FallsBackToTheDefaultMethod(string? stored, string expected) =>
        Assert.Equal(expected, SaCompanyPriceMethod.Normalize(stored));

    [Fact]
    public void ResolveSources_DefaultMode_IsTheFullChain_WithItemDefaultLast()
    {
        var sources = SaCompanyPriceMethod.ResolveSources(null);

        SaPriceSource[] expected =
        [
            SaPriceSource.CustomerItem,
            SaPriceSource.CustomerPriceList,
            SaPriceSource.CustomerGroupPriceList,
            SaPriceSource.ItemDefault
        ];

        Assert.Equal(expected, sources);
    }

    /// <summary>No configuration may produce a chain that ends without the item default.</summary>
    [Fact]
    public void ResolveSources_EveryMode_EndsWithItemDefault()
    {
        foreach (var method in SaCompanyPriceMethod.All)
        {
            var sources = SaCompanyPriceMethod.ResolveSources(method);

            Assert.NotEmpty(sources);
            Assert.Equal(SaPriceSource.ItemDefault, sources[^1]);
            Assert.Equal(1, sources.Count(x => x == SaPriceSource.ItemDefault));
        }
    }

    /// <summary>The specificity ORDER is fixed and can never be reordered by configuration.</summary>
    [Fact]
    public void ResolveSources_NeverReordersSpecificity()
    {
        foreach (var method in SaCompanyPriceMethod.All)
        {
            var sources = SaCompanyPriceMethod.ResolveSources(method);
            var indices = sources
                .Select(x => Array.IndexOf(
                    new[]
                    {
                        SaPriceSource.CustomerItem,
                        SaPriceSource.CustomerPriceList,
                        SaPriceSource.CustomerGroupPriceList,
                        SaPriceSource.ItemDefault
                    },
                    x))
                .ToList();

            Assert.Equal(indices.OrderBy(x => x), indices);
        }
    }

    [Theory]
    [InlineData(SaCompanyPriceMethod.CustomerItemOnly, SaPriceSource.CustomerPriceList, false)]
    [InlineData(SaCompanyPriceMethod.CustomerItemOnly, SaPriceSource.CustomerGroupPriceList, false)]
    [InlineData(SaCompanyPriceMethod.PriceListOnly, SaPriceSource.CustomerItem, false)]
    [InlineData(SaCompanyPriceMethod.PriceListOnly, SaPriceSource.CustomerGroupPriceList, true)]
    [InlineData(SaCompanyPriceMethod.ItemDefaultOnly, SaPriceSource.CustomerItem, false)]
    [InlineData(SaCompanyPriceMethod.ItemDefaultOnly, SaPriceSource.ItemDefault, true)]
    public void IsEligible_MatchesTheModeChain(string method, SaPriceSource source, bool expected) =>
        Assert.Equal(expected, SaCompanyPriceMethod.IsEligible(method, source));

    // ═════════════════════════════ mode drives the walk ═════════════════════════════

    [Fact]
    public void ItemDefaultOnly_IgnoresACustomerItemPriceThatWouldOtherwiseWin()
    {
        var resolution = SaItemFamilyPriceResolver.Resolve(
            Request(companyMethod: SaCompanyPriceMethod.ItemDefaultOnly),
            FullCandidates());

        Assert.True(resolution.Found);
        Assert.Equal(SaPriceSource.ItemDefault, resolution.PricingSource);
        Assert.Equal(14m, resolution.UnitPrice);
    }

    [Fact]
    public void CustomerItemOnly_IgnoresThePriceList()
    {
        var candidates = new SaItemFamilyPriceCandidates
        {
            PriceListLines = [PriceLine("PL1", 13.75m)],
            Items = [Item(14m)]
        };

        var resolution = SaItemFamilyPriceResolver.Resolve(
            Request(custPriceCode: "PL1", companyMethod: SaCompanyPriceMethod.CustomerItemOnly),
            candidates);

        Assert.True(resolution.Found);
        Assert.Equal(SaPriceSource.ItemDefault, resolution.PricingSource);
        Assert.Equal(14m, resolution.UnitPrice);
    }

    [Fact]
    public void PriceListOnly_IgnoresTheCustomerItemPrice()
    {
        var resolution = SaItemFamilyPriceResolver.Resolve(
            Request(custPriceCode: "PL1", companyMethod: SaCompanyPriceMethod.PriceListOnly),
            FullCandidates());

        Assert.True(resolution.Found);
        Assert.Equal(SaPriceSource.CustomerPriceList, resolution.PricingSource);
        Assert.Equal(13.75m, resolution.UnitPrice);
    }

    [Fact]
    public void DefaultMode_StillPrefersTheCustomerItemPrice()
    {
        var resolution = SaItemFamilyPriceResolver.Resolve(
            Request(custPriceCode: "PL1", companyMethod: null),
            FullCandidates());

        Assert.Equal(SaPriceSource.CustomerItem, resolution.PricingSource);
        Assert.Equal(12.50m, resolution.UnitPrice);
    }

    /// <summary>A source the mode excludes must not be able to block the walk either.</summary>
    [Fact]
    public void ItemDefaultOnly_DoesNotBlockOnAnExcludedSourceCurrencyMismatch()
    {
        var candidates = new SaItemFamilyPriceCandidates
        {
            CustomerItems = [Customer(price: 12.50m, currency: "USD")],
            Items = [Item(14m)]
        };

        var resolution = SaItemFamilyPriceResolver.Resolve(
            Request(documentCurrency: "MYR", companyMethod: SaCompanyPriceMethod.ItemDefaultOnly),
            candidates);

        Assert.True(resolution.Found);
        Assert.Equal(14m, resolution.UnitPrice);
    }

    /// <summary>An ELIGIBLE source that holds an unusable price still fails closed (§7.1 D8).</summary>
    [Fact]
    public void EligibleSourceWithCurrencyMismatch_StillBlocks()
    {
        var resolution = SaItemFamilyPriceResolver.Resolve(
            Request(documentCurrency: "MYR", companyMethod: SaCompanyPriceMethod.CustomerItemOnly),
            new SaItemFamilyPriceCandidates
            {
                CustomerItems = [Customer(price: 12.50m, currency: "USD")],
                Items = [Item(14m)]
            });

        Assert.False(resolution.Found);
        Assert.Contains("conversion is not implicit", resolution.Message!);
    }

    // ═════════════════════════════ price-list band + validity ═════════════════════════════

    [Theory]
    [InlineData(1, 9, 1, true)]
    [InlineData(1, 9, 9, true)]
    [InlineData(1, 9, 10, false)]
    [InlineData(10, null, 10, true)]
    [InlineData(10, null, 9999, true)]
    [InlineData(10, null, 9, false)]
    [InlineData(null, null, 5, true)]
    public void WithinBand_IsInclusiveAndNullsMeanUnbounded(
        int? minQty, int? maxQty, int qty, bool expected) =>
        Assert.Equal(expected, SaItemFamilyPriceResolver.WithinBand(minQty, maxQty, qty));

    [Fact]
    public void WithinWindow_ComparesOnTheDatePartOnly()
    {
        var from = new DateTime(2026, 9, 1, 0, 0, 0);
        var to = new DateTime(2026, 9, 30, 23, 59, 59);

        // A doc date carrying a time component must still land inside the window.
        Assert.True(SaItemFamilyPriceResolver.WithinWindow(from, to, new DateTime(2026, 9, 1, 14, 30, 0)));
        Assert.True(SaItemFamilyPriceResolver.WithinWindow(from, to, new DateTime(2026, 9, 30, 23, 0, 0)));
        Assert.False(SaItemFamilyPriceResolver.WithinWindow(from, to, new DateTime(2026, 8, 31, 23, 0, 0)));
        Assert.False(SaItemFamilyPriceResolver.WithinWindow(from, to, new DateTime(2026, 10, 1, 0, 0, 0)));

        // NULL bounds are open on that side.
        Assert.True(SaItemFamilyPriceResolver.WithinWindow(null, null, DocDate));
        Assert.True(SaItemFamilyPriceResolver.WithinWindow(null, to, new DateTime(1999, 1, 1)));
    }

    [Fact]
    public void SelectPriceListLine_HigherMinQtyBandWins()
    {
        var lines = new List<IvCustPriceCandidate>
        {
            Line(minQty: 0m, price: 100m),
            Line(minQty: 10m, maxQty: 99m, price: 95m),
            Line(minQty: 100m, price: 90m)
        };

        var result = SaItemFamilyPriceResolver.SelectPriceListLine(
            Request(qty: 100m, custPriceCode: "PL1"), "PL1", lines, SaPriceSource.CustomerPriceList);

        Assert.True(result.Found);
        Assert.Equal(90m, result.UnitPrice);
        Assert.Equal("PL1 QTY 100+", result.Ref);
    }

    [Fact]
    public void SelectPriceListLine_QtyBetweenBands_UsesTheLowerBand()
    {
        var lines = new List<IvCustPriceCandidate>
        {
            Line(minQty: 0m, maxQty: 9m, price: 100m),
            Line(minQty: 10m, price: 95m)
        };

        var result = SaItemFamilyPriceResolver.SelectPriceListLine(
            Request(qty: 5m, custPriceCode: "PL1"), "PL1", lines, SaPriceSource.CustomerPriceList);

        Assert.True(result.Found);
        Assert.Equal(100m, result.UnitPrice);
    }

    [Fact]
    public void SelectPriceListLine_NewestValidFromWinsOnTheSameBand()
    {
        var lines = new List<IvCustPriceCandidate>
        {
            Line(minQty: 0m, price: 100m, validFrom: new DateTime(2020, 1, 1)),
            Line(minQty: 0m, price: 88m, validFrom: new DateTime(2026, 9, 1))
        };

        var result = SaItemFamilyPriceResolver.SelectPriceListLine(
            Request(custPriceCode: "PL1"), "PL1", lines, SaPriceSource.CustomerPriceList);

        Assert.Equal(88m, result.UnitPrice);
    }

    [Fact]
    public void SelectPriceListLine_ExpiredAndFutureWindows_AreNotCandidates()
    {
        var lines = new List<IvCustPriceCandidate>
        {
            Line(minQty: 0m, price: 50m, validFrom: new DateTime(2020, 1, 1), validTo: new DateTime(2020, 12, 31)),
            Line(minQty: 0m, price: 60m, validFrom: new DateTime(2030, 1, 1))
        };

        var result = SaItemFamilyPriceResolver.SelectPriceListLine(
            Request(custPriceCode: "PL1"), "PL1", lines, SaPriceSource.CustomerPriceList);

        Assert.False(result.Found);
        Assert.False(result.IsBlocking);
    }

    /// <summary>
    /// Two quantity bands legitimately start on the SAME ValidFrom — the reason the natural primary key
    /// cannot contain ValidFrom alone (plan 3.2).
    /// </summary>
    [Fact]
    public void SelectPriceListLine_TwoBandsSharingOneValidFrom_BothResolve()
    {
        var shared = new DateTime(2026, 9, 1);
        var lines = new List<IvCustPriceCandidate>
        {
            Line(minQty: 1m, maxQty: 9m, price: 100m, validFrom: shared),
            Line(minQty: 10m, price: 90m, validFrom: shared)
        };

        var low = SaItemFamilyPriceResolver.SelectPriceListLine(
            Request(qty: 5m, custPriceCode: "PL1"), "PL1", lines, SaPriceSource.CustomerPriceList);
        var high = SaItemFamilyPriceResolver.SelectPriceListLine(
            Request(qty: 50m, custPriceCode: "PL1"), "PL1", lines, SaPriceSource.CustomerPriceList);

        Assert.Equal(100m, low.UnitPrice);
        Assert.Equal(90m, high.UnitPrice);
    }

    [Fact]
    public void SelectPriceListLine_OnlyAnotherCurrencyPresent_Blocks()
    {
        var lines = new List<IvCustPriceCandidate>
        {
            Line(minQty: 0m, price: 25m, currency: "USD")
        };

        var result = SaItemFamilyPriceResolver.SelectPriceListLine(
            Request(custPriceCode: "PL1", documentCurrency: "MYR"), "PL1", lines, SaPriceSource.CustomerPriceList);

        Assert.False(result.Found);
        Assert.True(result.IsBlocking);
        Assert.Contains("Conversion is not implicit", result.BlockingReason!);
    }

    [Fact]
    public void SelectPriceListLine_MatchingCurrencyWinsOverABlankBaseCurrencyLine()
    {
        var lines = new List<IvCustPriceCandidate>
        {
            Line(minQty: 0m, price: 100m),
            Line(minQty: 0m, price: 25m, currency: "USD")
        };

        var result = SaItemFamilyPriceResolver.SelectPriceListLine(
            Request(custPriceCode: "PL1", documentCurrency: "USD"), "PL1", lines, SaPriceSource.CustomerPriceList);

        Assert.Equal(25m, result.UnitPrice);
    }

    [Fact]
    public void SelectPriceListLine_NoListAssigned_IsNotABlock()
    {
        var result = SaItemFamilyPriceResolver.SelectPriceListLine(
            Request(custPriceCode: "PL1"), null, [], SaPriceSource.CustomerPriceList);

        Assert.False(result.Found);
        Assert.False(result.IsBlocking);
    }

    // ═════════════════════════════ persisted source tokens ═════════════════════════════

    [Fact]
    public void Resolve_PopulatesThePersistedSourceTokenAndRef()
    {
        var resolution = SaItemFamilyPriceResolver.Resolve(Request(), FullCandidates());

        Assert.Equal(SaPriceSource.CustomerItem, resolution.PricingSource);
        Assert.Equal(SaPriceSourceTokens.CustomerItem, resolution.PricingSourceToken);
        Assert.Equal("MOQ=0", resolution.PricingRef);

        // The legacy human label is unchanged — it is the shipped, test-pinned contract.
        Assert.Equal(SaItemFamilyPriceSources.CustomerItem, resolution.Source);
    }

    [Fact]
    public void PersistedSourceTokens_FitTheColumnWidth()
    {
        foreach (var source in Enum.GetValues<SaPriceSource>())
        {
            var token = SaPriceSourceTokens.For(source);

            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.True(token.Length <= SaPriceSourceTokens.MaxLength);
            Assert.Equal(token.ToUpperInvariant(), token);
        }
    }

    // ═════════════════════════════ PIPELINE ORDER (the corrected defect) ═════════════════════════════

    /// <summary>
    /// Stage 3 must consume the STAGE 2 price. <c>SaInvoiceCalc.CalculateLine</c> derives the per-unit
    /// discount from the line's raw UnitPrice and only then divides by (1 + t) on the inclusive path, so
    /// resolving the discount against the tax-EXCLUSIVE price would double-scale it.
    ///
    /// Exclusive 100.00, 6% tax, inclusive line, 10% rule:
    ///   stage 1 -> 100.00 (exclusive) ; stage 2 -> 106.00 (stored on the line)
    ///   correct discount (against 106.00) = 10.60
    ///   wrong   discount (against 100.00) = 10.00  <-- the defect this test pins
    /// </summary>
    [Fact]
    public void PipelineOrder_DiscountIsResolvedAgainstTheBasisConvertedPrice()
    {
        const decimal exclusivePrice = 100m;
        const decimal taxPercent = 6m;

        // ---- stage 2: the basis conversion ----
        var storedUnitPrice = SaItemFamilyPriceBasis.ToInclusive(exclusivePrice, taxPercent / 100m);
        Assert.Equal(106m, storedUnitPrice);

        // ---- stage 3: the discount, against the STAGE 2 price ----
        var selection = SaItemFamilyDiscountResolver.Resolve(
            new SaItemFamilyDiscountRequest
            {
                ICode = "I1",
                Qty = 10m,
                DocDate = DocDate,
                DiscountMethod = "SPLIT",
                UnitPrice = storedUnitPrice
            },
            [
                new SaItemFamilyDiscountRule
                {
                    Id = 1,
                    ICode = "I1",
                    QtyFr = 1m,
                    QtyTo = 100m,
                    DateFr = new DateTime(2020, 1, 1),
                    Discount = 10m,
                    DiscountType = "PERCENTAGE"
                }
            ]);

        Assert.True(selection.Found);
        Assert.Equal(10.60m, selection.DiscountPerUnit);

        // ---- stage 4: the document engine must independently agree ----
        var line = new SaInvoiceLineCalcState
        {
            Qty = 1m,
            UnitPrice = storedUnitPrice,
            IsInclusive = true,
            ItemDiscount = 10m
        };
        SaInvoiceCalc.CalculateLine(line, taxPercent, decPoint: true, discMethod: "SPLIT");

        Assert.Equal(selection.DiscountPerUnit, line.DiscountPerUnit);

        // 106.00 inclusive, 10.60 discount -> ex-tax 100.00 gross, 90.00 net, 5.40 tax,
        // and the tax-inclusive line total is preserved exactly (90.00 + 5.40 = 95.40).
        Assert.Equal(100m, line.Amount);
        Assert.Equal(90m, line.NetAmount);
        Assert.Equal(5.40m, line.TaxAmt);

        // And the wrong order is provably different, so this test cannot pass by accident.
        var wrongOrder = SaItemFamilyDiscountResolver.Resolve(
            new SaItemFamilyDiscountRequest
            {
                ICode = "I1",
                Qty = 10m,
                DocDate = DocDate,
                DiscountMethod = "SPLIT",
                UnitPrice = exclusivePrice
            },
            [
                new SaItemFamilyDiscountRule
                {
                    Id = 1,
                    ICode = "I1",
                    QtyFr = 1m,
                    QtyTo = 100m,
                    DateFr = new DateTime(2020, 1, 1),
                    Discount = 10m,
                    DiscountType = "PERCENTAGE"
                }
            ]);

        Assert.Equal(10m, wrongOrder.DiscountPerUnit);
        Assert.NotEqual(wrongOrder.DiscountPerUnit, selection.DiscountPerUnit);
    }

    [Fact]
    public void ExclusiveLine_IsNotGrossedUp()
    {
        // Stage 2 is a no-op for an exclusive line: the master price is already the stored basis.
        const decimal exclusivePrice = 100m;
        var stored = exclusivePrice;

        Assert.Equal(100m, stored);
        Assert.Equal(100m, SaItemFamilyPriceBasis.ToInclusive(exclusivePrice, 0m));
    }

    // ═════════════════════════════ helpers ═════════════════════════════

    private static SaItemFamilyPriceRequest Request(
        decimal qty = 5m,
        string? custPriceCode = null,
        string? companyMethod = null,
        string? documentCurrency = null,
        DateTime? docDate = null) =>
        new()
        {
            CustCode = "CUST1",
            ICode = "I1",
            UOM = "PCS",
            Qty = qty,
            DocDate = docDate ?? DocDate,
            CustPriceCode = custPriceCode,
            CompanyPriceMethod = companyMethod,
            DocumentCurrency = documentCurrency
        };

    private static SaItemFamilyPriceCandidates FullCandidates() => new()
    {
        CustomerItems = [Customer(price: 12.50m)],
        PriceListLines = [PriceLine("PL1", 13.75m)],
        Items = [Item(14m)]
    };

    private static SaItemCustPriceCandidate Customer(
        int moq = 0, decimal? price = null, string? currency = null) =>
        new() { ICode = "I1", UOM = "PCS", MOQ = moq, UnitPrice = price, Currency = currency };

    private static IvCustPriceCandidate PriceLine(string code, decimal price) =>
        new() { CustPriceCode = code, ICode = "I1", UOM = "PCS", SellingPrice = price };

    private static IvCustPriceCandidate Line(
        decimal? minQty = null,
        decimal? maxQty = null,
        decimal? price = null,
        DateTime? validFrom = null,
        DateTime? validTo = null,
        string? currency = null) =>
        new()
        {
            CustPriceCode = "PL1",
            ICode = "I1",
            UOM = "PCS",
            SellingPrice = price,
            MinQty = minQty,
            MaxQty = maxQty,
            ValidFrom = validFrom,
            ValidTo = validTo,
            Currency = currency
        };

    private static IvStockMasterPriceCandidate Item(decimal price, bool active = true) =>
        new() { ICode = "I1", IsActive = active, SellingUom = "PCS", SellingPrice = price };
}
