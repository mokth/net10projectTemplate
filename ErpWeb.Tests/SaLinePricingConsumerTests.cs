using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// The line-pricing ORCHESTRATOR over a real database (plan Phase 1 step 1.10): the four-stage
/// pipeline, the per-company mode read server-side, the tax-basis conversion and the end-to-end
/// discount hand-off.
///
/// The pure contract is pinned in <see cref="SaCompanyPriceMethodTests"/>; this file is what proves
/// the WIRING — that the mode comes from <c>Company</c>, that only eligible sources are queried, and
/// that stage 3 really consumes the stage 2 price when the database is involved.
/// </summary>
public class SaLinePricingConsumerTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 15);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaLinePricingConsumerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();

        // No row for DEMO on purpose in most tests: a MISSING company row must behave like NULL
        // (the full chain), never like "deny everything".
        db.Companies.Add(new Company
        {
            CompanyCode = "DEMO",
            CompanyName = "Demo company",
            CurrencyCode = "MYR"
        });
        db.Companies.Add(new Company
        {
            CompanyCode = "OTHER",
            CompanyName = "Other company",
            CurrencyCode = "MYR"
        });

        // I1 — the everyday item.
        db.IvStockMasters.Add(Item("DEMO", "I1", 14m, "PCS", "C1", active: true, rv: 1));
        // I2 — a clean item for the pipeline-order test (no customer/list price will shadow it).
        db.IvStockMasters.Add(Item("DEMO", "I2", 100m, "PCS", "C1", active: true, rv: 2));
        // I3 — no price at all.
        db.IvStockMasters.Add(Item("DEMO", "I3", 0m, "PCS", "C1", active: true, rv: 3));
        // I4 — sells in BOX, so a PCS line must not silently borrow its price.
        db.IvStockMasters.Add(Item("DEMO", "I4", 900m, "BOX", "C1", active: true, rv: 4));
        // I5 — inactive.
        db.IvStockMasters.Add(Item("DEMO", "I5", 77m, "PCS", "C1", active: false, rv: 5));

        db.SaCusts.Add(Customer("DEMO", "CUST1", "Plain customer", rv: 10));
        db.SaCusts.Add(Customer("DEMO", "CUST2", "List customer", rv: 11, custPriceCode: "PL1"));
        db.SaCusts.Add(Customer("DEMO", "CUST4", "Negotiated customer", rv: 12));
        // Phase 5 — CUST5 has a GROUP but no list of its own (must fall back to the group);
        // CUST6 has BOTH, so its own list must win.
        db.SaCusts.Add(Customer("DEMO", "CUST5", "Group-only customer", rv: 15, custGroupCode: "G1"));
        db.SaCusts.Add(Customer("DEMO", "CUST6", "Own list beats group", rv: 16, custPriceCode: "PL1", custGroupCode: "G1"));
        // Phase 3 - one customer per price-list shape the resolver now has to discriminate on.
        db.SaCusts.Add(Customer("DEMO", "CUST7", "Banded list customer", rv: 17, custPriceCode: "PLB"));
        db.SaCusts.Add(Customer("DEMO", "CUST8", "Future window customer", rv: 18, custPriceCode: "PLX"));
        db.SaCusts.Add(Customer("DEMO", "CUST9", "Usd only customer", rv: 19, custPriceCode: "PLC"));
        db.SaCusts.Add(Customer("DEMO", "CUST10", "Base plus explicit currency", rv: 20, custPriceCode: "PLU"));
        db.SaCusts.Add(Customer("DEMO", "CUST11", "Single day window", rv: 21, custPriceCode: "PLD"));
        db.SaCusts.Add(Customer("DEMO", "CUST12", "Past window customer", rv: 22, custPriceCode: "PLW"));
        db.SaCusts.Add(Customer("DEMO", "DEALER1", "Dealer customer", rv: 13, priceMethod: SaCustPaymentOptions.PriceDealer));
        db.SaCusts.Add(Customer("OTHER", "CUST1", "Other company customer", rv: 14));

        // Customer special price: wins over EVERYTHING for CUST4.
        db.SaItemCusts.Add(new SaItemCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUST4",
            ICode = "I1",
            SellingUOM = "PCS",
            MOQ = 0,
            CustICode = "CUST4-I1",
            UnitPrice = 12.50d,
            Currency = "MYR",
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 20]
        });

        // Price list PL1, assigned to CUST2. I2 is present so the pipeline test can be shadowed if the
        // list were consulted when it should not be.
        db.IvCustPriceGroups.Add(new IvCustPriceGroup
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PL1",
            CustPriceDesc = "Dealer list",
            IsActive = true,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 30]
        });
        db.IvCustPrices.Add(new IvCustPrice
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PL1",
            ICode = "I1",
            UOM = "PCS",
            SellingPrice = 13.75m
        });
        db.IvCustPrices.Add(new IvCustPrice
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PL1",
            ICode = "I2",
            UOM = "PCS",
            SellingPrice = 90m
        });

        // Phase 5 — the GROUP's default price list. G1 points at PLG, which prices I1 at 11.00 (so a
        // group-priced line is distinguishable from both PL1's 13.75 and I1's item default of 14).
        // I2 is deliberately absent from PLG so a fall-through to the item default can be proven.
        db.SaCustGroups.Add(new SaCustGroup
        {
            CompanyCode = "DEMO",
            CustGroupCode = "G1",
            CustGroupDesc = "Group with a default list",
            CustPriceCode = "PLG",
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 40]
        });
        db.IvCustPriceGroups.Add(new IvCustPriceGroup
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLG",
            CustPriceDesc = "Group default list",
            IsActive = true,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 41]
        });
        db.IvCustPrices.Add(new IvCustPrice
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLG",
            ICode = "I1",
            UOM = "PCS",
            SellingPrice = 11m
        });

        // ══════════ Phase 3 seeds: bands, windows and currency ══════════
        // The document date is 2026-09-15 for every request in this file.

        // PLB - TIERS: the SAME item and UOM twice, one row per quantity band. Both rows start on the
        // SAME ValidFrom, which is exactly what a composite key containing ValidFrom could not hold.
        db.IvCustPriceGroups.Add(new IvCustPriceGroup
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLB",
            CustPriceDesc = "Banded list",
            IsActive = true,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 50]
        });
        db.IvCustPrices.Add(new IvCustPrice
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLB",
            ICode = "I1",
            UOM = "PCS",
            SellingPrice = 12m,
            MinQty = 1m,
            MaxQty = 9m,
            ValidFrom = new DateTime(2026, 9, 1)
        });
        db.IvCustPrices.Add(new IvCustPrice
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLB",
            ICode = "I1",
            UOM = "PCS",
            SellingPrice = 10m,
            MinQty = 10m,
            MaxQty = null,
            ValidFrom = new DateTime(2026, 9, 1)
        });

        // PLX - the whole window is in the FUTURE, so nothing may be used from it.
        db.IvCustPriceGroups.Add(new IvCustPriceGroup
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLX",
            CustPriceDesc = "Future list",
            IsActive = true,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 51]
        });
        db.IvCustPrices.Add(new IvCustPrice
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLX",
            ICode = "I1",
            UOM = "PCS",
            SellingPrice = 2m,
            ValidFrom = new DateTime(2030, 1, 1)
        });

        // PLW - the whole window is in the PAST.
        db.IvCustPriceGroups.Add(new IvCustPriceGroup
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLW",
            CustPriceDesc = "Expired list",
            IsActive = true,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 52]
        });
        db.IvCustPrices.Add(new IvCustPrice
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLW",
            ICode = "I1",
            UOM = "PCS",
            SellingPrice = 1m,
            ValidFrom = new DateTime(2020, 1, 1),
            ValidTo = new DateTime(2020, 12, 31)
        });

        // PLC - the only line is in USD, so a MYR document must BLOCK rather than borrow it.
        db.IvCustPriceGroups.Add(new IvCustPriceGroup
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLC",
            CustPriceDesc = "Usd list",
            IsActive = true,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 53]
        });
        db.IvCustPrices.Add(new IvCustPrice
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLC",
            ICode = "I1",
            UOM = "PCS",
            SellingPrice = 3m,
            CurrencyCode = "USD"
        });

        // PLU - a blank (base) line AND an explicit MYR line for the same band; MYR must win.
        db.IvCustPriceGroups.Add(new IvCustPriceGroup
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLU",
            CustPriceDesc = "Base plus explicit",
            IsActive = true,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 54]
        });
        db.IvCustPrices.Add(new IvCustPrice
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLU",
            ICode = "I1",
            UOM = "PCS",
            SellingPrice = 4m,
            CurrencyCode = null
        });
        db.IvCustPrices.Add(new IvCustPrice
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLU",
            ICode = "I1",
            UOM = "PCS",
            SellingPrice = 5m,
            CurrencyCode = "MYR"
        });

        // PLD - a SINGLE-DAY window covering the document date, so both bounds are inclusive.
        db.IvCustPriceGroups.Add(new IvCustPriceGroup
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLD",
            CustPriceDesc = "Single day list",
            IsActive = true,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 55]
        });
        db.IvCustPrices.Add(new IvCustPrice
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PLD",
            ICode = "I1",
            UOM = "PCS",
            SellingPrice = 6m,
            ValidFrom = FixedToday,
            ValidTo = FixedToday
        });

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    // ═════════════════════════════ stage 1 — the DB-loaded source chain ═════════════════════════════

    [Fact]
    public async Task FallsThroughToTheItemDefault_WhenNoCustomerPriceExists()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST1", "I1"));

        Assert.True(result.Succeeded);
        Assert.Equal(14m, result.Data!.UnitPrice);
        Assert.Equal(SaPriceSource.ItemDefault, result.Data.PricingSource);
        Assert.Equal("Item default selling price", result.Data.PricingSourceLabel);
    }

    [Fact]
    public async Task CustomerItemPrice_BeatsThePriceListAndTheItemDefault()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST4", "I1"));

        Assert.True(result.Succeeded);
        Assert.Equal(12.50m, result.Data!.UnitPrice);
        Assert.Equal(SaPriceSource.CustomerItem, result.Data.PricingSource);
        Assert.Equal("MOQ=0", result.Data.PricingRef);
    }

    [Fact]
    public async Task PriceListPrice_IsUsedWhenTheCustomerHasNoSpecialPrice()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST2", "I1"));

        Assert.True(result.Succeeded);
        Assert.Equal(13.75m, result.Data!.UnitPrice);
        Assert.Equal(SaPriceSource.CustomerPriceList, result.Data.PricingSource);
        Assert.Equal("PL1", result.Data.PricingRef);
    }

    /// <summary>The legacy defect: a missing price must BLOCK, never resolve to RM 0.00.</summary>
    [Fact]
    public async Task NoPriceAtAll_BlocksWithAMessage_AndNeverReturnsZero()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST1", "I3"));

        Assert.False(result.Succeeded);
        Assert.Contains("No price found", result.Message!);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task UomMismatch_Blocks_AndDoesNotBorrowAnotherUomsPrice()
    {
        // I4 sells in BOX; a PCS line must not silently use the BOX price.
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST1", "I4", uom: "PCS"));

        Assert.False(result.Succeeded);
        Assert.Contains("No price found", result.Message!);
    }

    [Fact]
    public async Task InactiveItem_IsNotAPriceCandidate()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST1", "I5"));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task DealerCustomer_FailsClosed_EvenThoughAnItemPriceExists()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("DEALER1", "I1"));

        Assert.False(result.Succeeded);
        Assert.Contains("Dealer pricing is not supported", result.Message!);
    }

    [Fact]
    public async Task UnknownCustomer_IsNotFound()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("NOPE", "I1"));

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task CustomerInAnotherCompany_IsNotVisible()
    {
        // CUST1 exists in BOTH companies, but item I1 exists only in DEMO. The OTHER company's
        // customer is found (correct), and DEMO's item price must then be invisible to it.
        var result = await CreateSut("OTHER").ResolveLinePricingAsync(Request("CUST1", "I1"));

        Assert.False(result.Succeeded);
        Assert.Contains("No price found", result.Message!);
        Assert.Null(result.Data);
    }

    // ═════════════════════════════ stage 2 — tax-basis conversion ═════════════════════════════

    [Fact]
    public async Task ExclusiveLine_StoresTheMasterPriceUnchanged()
    {
        var result = await CreateSut().ResolveLinePricingAsync(
            Request("CUST1", "I1", isInclusive: false, taxPercent: 6m));

        Assert.Equal(14m, result.Data!.UnitPrice);
        Assert.Equal(14m, result.Data.BaseUnitPrice);
    }

    [Fact]
    public async Task InclusiveLine_GrossesUpTheExclusiveMasterPrice()
    {
        var result = await CreateSut().ResolveLinePricingAsync(
            Request("CUST1", "I1", isInclusive: true, taxPercent: 6m));

        Assert.Equal(14.84m, result.Data!.UnitPrice);
        // The engine itself still reports the exclusive value it resolved (audit).
        Assert.Equal(14m, result.Data.BaseUnitPrice);
    }

    // ═════════════════════════════ PIPELINE ORDER end-to-end ═════════════════════════════

    /// <summary>
    /// The corrected defect, proven with the database in the loop. Exclusive 100.00, 6% tax, inclusive
    /// line, 10% rule: the discount must be 10.60 (against the grossed-up 106.00), NOT 10.00.
    /// </summary>
    [Fact]
    public async Task InclusiveLineWithADiscount_ResolvesTheDiscountAgainstTheGrossedUpPrice()
    {
        await SeedDiscountRuleAsync(ruleId: 1, discountPercent: 10m);

        var result = await CreateSut().ResolveLinePricingAsync(
            Request("CUST1", "I2", qty: 10m, isInclusive: true, taxPercent: 6m, discountMethod: "SPLIT"));

        Assert.True(result.Succeeded);
        var priced = result.Data!;

        Assert.Equal(106m, priced.UnitPrice);
        Assert.Equal(10m, priced.ItemDiscount);
        Assert.Equal(10.60m, priced.DiscountPerUnit);
        Assert.Equal(1, priced.DiscountRuleId);

        // Stage 4 must independently agree with stage 3.
        var line = new SaInvoiceLineCalcState
        {
            Qty = 1m,
            UnitPrice = priced.UnitPrice,
            IsInclusive = true,
            ItemDiscount = priced.ItemDiscount
        };
        SaInvoiceCalc.CalculateLine(line, 6m, decPoint: true, discMethod: "SPLIT");

        Assert.Equal(priced.DiscountPerUnit, line.DiscountPerUnit);
        Assert.Equal(90m, line.NetAmount);
        Assert.Equal(5.40m, line.TaxAmt);
    }

    [Fact]
    public async Task DiscountRule_IsAssignedOntoTheResultSlots()
    {
        await SeedDiscountRuleAsync(ruleId: 1, discountPercent: 5m);

        var result = await CreateSut().ResolveLinePricingAsync(
            Request("CUST1", "I2", qty: 5m, isInclusive: false, discountMethod: "JOIN"));

        Assert.Equal(5m, result.Data!.ItemDiscount);
        Assert.Equal(5m, result.Data.DiscountPerUnit);
    }

    [Fact]
    public async Task NoMatchingDiscountRule_IsNotAPricingFailure()
    {
        var result = await CreateSut().ResolveLinePricingAsync(
            Request("CUST1", "I2", qty: 5m, isInclusive: false));

        Assert.True(result.Succeeded);
        Assert.Equal(0m, result.Data!.DiscountPerUnit);
        Assert.Null(result.Data.DiscountRuleId);
    }

    // ═════════════════════════════ stage 0 — the per-company mode ═════════════════════════════

    [Fact]
    public async Task NullCompanyMethod_BehavesLikeTheFullChain()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST4", "I1"));

        Assert.Equal(SaPriceSource.CustomerItem, result.Data!.PricingSource);
    }

    [Fact]
    public async Task ItemDefaultOnlyMode_IgnoresTheCustomerSpecialPrice()
    {
        await SetCompanyMethodAsync(SaCompanyPriceMethod.ItemDefaultOnly);

        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST4", "I1"));

        Assert.True(result.Succeeded);
        Assert.Equal(14m, result.Data!.UnitPrice);
        Assert.Equal(SaPriceSource.ItemDefault, result.Data.PricingSource);
    }

    [Fact]
    public async Task CustomerItemOnlyMode_IgnoresThePriceList()
    {
        await SetCompanyMethodAsync(SaCompanyPriceMethod.CustomerItemOnly);

        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST2", "I1"));

        // CUST2 has a price list but no special price, so the chain falls to the item default.
        Assert.True(result.Succeeded);
        Assert.Equal(14m, result.Data!.UnitPrice);
        Assert.Equal(SaPriceSource.ItemDefault, result.Data.PricingSource);
    }

    [Fact]
    public async Task PriceListOnlyMode_IgnoresTheCustomerSpecialPrice()
    {
        await SetCompanyMethodAsync(SaCompanyPriceMethod.PriceListOnly);

        // CUST4 has a special price AND no list, so under this mode only the item default remains.
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST4", "I1"));

        Assert.True(result.Succeeded);
        Assert.Equal(14m, result.Data!.UnitPrice);
        Assert.Equal(SaPriceSource.ItemDefault, result.Data.PricingSource);
    }

    /// <summary>An unrecognised token must fall back to the default chain, not break the company.</summary>
    [Fact]
    public async Task UnknownCompanyMethod_FallsBackToTheFullChain()
    {
        await SetCompanyMethodRawAsync("SOMETHING_ELSE");

        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST4", "I1"));

        Assert.True(result.Succeeded);
        Assert.Equal(SaPriceSource.CustomerItem, result.Data!.PricingSource);
    }

    [Fact]
    public async Task BlankCompanyCode_IsAnInvalidScope()
    {
        var result = await CreateSut(company: null).ResolveLinePricingAsync(Request("CUST1", "I1"));

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.InvalidScope, result.ErrorCode);
    }

    // ═════════════════════════════ traceability surface ═════════════════════════════

    [Fact]
    public async Task Describe_NamesTheSource_ForThePopupHint()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST4", "I1"));

        Assert.Equal("Customer item price (MOQ=0)", result.Data!.Describe());
    }

    [Fact]
    public async Task PricingSourceToken_IsPersistable_AndFitsTheColumn()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST2", "I1"));

        Assert.Equal("CUSTOMER_PRICE_LIST", result.Data!.PricingSourceToken);
        Assert.True(result.Data.PricingSourceToken.Length <= SaPriceSourceTokens.MaxLength);
    }

    // ═════════════════════════════ Phase 5 — the group default price list ═════════════════════════════

    /// <summary>
    /// The customer has no list of its own, so the GROUP's default list prices the line. This level was
    /// dead before Phase 5: the orchestrator passed an empty group code, so this test would have
    /// resolved I1 from the item default (14) instead of the group's 11.
    /// </summary>
    [Fact]
    public async Task GroupDefaultPriceList_IsUsedWhenTheCustomerHasNoListOfItsOwn()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST5", "I1"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(11m, result.Data!.UnitPrice);
        Assert.Equal(SaPriceSource.CustomerGroupPriceList, result.Data.PricingSource);
    }

    /// <summary>
    /// The customer's own list always beats its group's default, because the walk tries the customer
    /// level first. PL1 has I1 at 13.75; the group's PLG has it at 11.00.
    /// </summary>
    [Fact]
    public async Task CustomerOwnPriceList_BeatsTheGroupDefault()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST6", "I1"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(13.75m, result.Data!.UnitPrice);
        Assert.Equal(SaPriceSource.CustomerPriceList, result.Data.PricingSource);
    }

    /// <summary>
    /// A group default with no line for the item is NOT an error — the walk continues to the item's
    /// selling price. PLG has no I2 line, so I2's own 100 applies.
    /// </summary>
    [Fact]
    public async Task GroupDefaultWithoutALineForTheItem_FallsThroughToTheItemDefault()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST5", "I2"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(100m, result.Data!.UnitPrice);
        Assert.Equal(SaPriceSource.ItemDefault, result.Data.PricingSource);
    }

    /// <summary>
    /// The company method governs the group level like any other source: under ITEM_DEFAULT_ONLY the
    /// group's list must NOT be consulted even though the data exists.
    /// </summary>
    [Fact]
    public async Task CompanyMethodCanExcludeTheGroupLevel()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var company = await db.Companies.SingleAsync(x => x.CompanyCode == "DEMO");
            company.SalesPriceMethod = SaCompanyPriceMethod.ItemDefaultOnly;
            await db.SaveChangesAsync();
        }

        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST5", "I1"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(14m, result.Data!.UnitPrice);
        Assert.Equal(SaPriceSource.ItemDefault, result.Data.PricingSource);
    }

    /// <summary>A customer with no group behaves exactly as it did before Phase 5.</summary>
    [Fact]
    public async Task CustomerWithNoGroup_FallsThroughToTheItemDefault()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST1", "I1"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(14m, result.Data!.UnitPrice);
        Assert.Equal(SaPriceSource.ItemDefault, result.Data.PricingSource);
    }

    // ═════════════ Phase 3 — validity, quantity bands and currency ═════════════

    /// <summary>
    /// The tier below the requested quantity wins: 5 units fall in the 1-9 band, not the 10+ band.
    /// </summary>
    [Fact]
    public async Task QuantityBands_TheBandContainingTheQuantityWins()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST7", "I1", qty: 5m));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(12m, result.Data!.UnitPrice);
        Assert.Equal(1m, result.Data.MatchedMinQty);
        Assert.Equal(9m, result.Data.MatchedMaxQty);
    }

    /// <summary>
    /// Exactly ON the band floor is inside the band: 10 units take the 10+ tier. This is the boundary
    /// that a strict inequality would get wrong.
    /// </summary>
    [Fact]
    public async Task QuantityBands_TheBandFloorIsInclusive()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST7", "I1", qty: 10m));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(10m, result.Data!.UnitPrice);
        Assert.Equal(10m, result.Data.MatchedMinQty);
        Assert.Null(result.Data.MatchedMaxQty);
    }

    /// <summary>
    /// Exactly ON the band ceiling is inside the band, and the NEXT tier does not apply yet.
    /// </summary>
    [Fact]
    public async Task QuantityBands_TheBandCeilingIsInclusive()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST7", "I1", qty: 9m));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(12m, result.Data!.UnitPrice);
    }

    /// <summary>A quantity below every band floor finds nothing and falls through to the item default.</summary>
    [Fact]
    public async Task QuantityBands_BelowEveryBandFallsThroughToTheItemDefault()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST7", "I1", qty: 0m));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(14m, result.Data!.UnitPrice);
        Assert.Equal(SaPriceSource.ItemDefault, result.Data.PricingSource);
    }

    /// <summary>
    /// A line whose window has not opened yet is not a candidate: the walk falls through to the item
    /// default instead of applying a future price early.
    /// </summary>
    [Fact]
    public async Task ValidityWindow_AFutureLineIsNotACandidate()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST8", "I1"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(14m, result.Data!.UnitPrice);
        Assert.Equal(SaPriceSource.ItemDefault, result.Data.PricingSource);
    }

    /// <summary>An EXPIRED line is not a candidate either.</summary>
    [Fact]
    public async Task ValidityWindow_AnExpiredLineIsNotACandidate()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST12", "I1"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(14m, result.Data!.UnitPrice);
        Assert.Equal(SaPriceSource.ItemDefault, result.Data.PricingSource);
    }

    /// <summary>
    /// Both bounds are INCLUSIVE: a line valid from and to the document date applies on that date.
    /// </summary>
    [Fact]
    public async Task ValidityWindow_BothBoundsAreInclusive()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST11", "I1"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(6m, result.Data!.UnitPrice);
        Assert.Equal(SaPriceSource.CustomerPriceList, result.Data.PricingSource);
    }

    /// <summary>
    /// A list that holds a price for the item only in ANOTHER currency BLOCKS. It must not fall through
    /// to the item default: that would silently price the line from a different source than the data
    /// intends, and currency conversion is explicitly not implicit anywhere in this repository.
    /// </summary>
    [Fact]
    public async Task Currency_OnlyAnotherCurrencyPresent_Blocks()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST9", "I1"));

        Assert.False(result.Succeeded);
        Assert.Contains("USD", result.Message);
    }

    /// <summary>
    /// An EXPLICIT currency match outranks a blank (base-currency) line. Blank stays usable because
    /// legacy data is sparse, but a price deliberately labelled in the document's currency is the
    /// better answer when both are present.
    /// </summary>
    [Fact]
    public async Task Currency_AnExplicitMatchBeatsABlankLine()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST10", "I1"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(5m, result.Data!.UnitPrice);
    }

    /// <summary>
    /// The band that matched is REPORTED, so the provenance can explain a tier price rather than just
    /// naming the list.
    /// </summary>
    [Fact]
    public async Task QuantityBands_TheMatchedBandIsReportedForTraceability()
    {
        var result = await CreateSut().ResolveLinePricingAsync(Request("CUST7", "I1", qty: 25m));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(10m, result.Data!.UnitPrice);
        Assert.Equal(10m, result.Data.MatchedMinQty);
        Assert.Contains("PLB", result.Data.PricingRef);
        Assert.Contains("10", result.Data.PricingRef);
    }

    // ═════════════════════════════ Phase 6 — the explanation ladder ═════════════════════════════

    /// <summary>
    /// Every level is reported in the fixed specificity order, whether it applied, was skipped because a
    /// more specific level already won, or was never reached.
    /// </summary>
    [Fact]
    public async Task Explain_ReportsEveryLevelAndNamesTheWinner()
    {
        var result = await CreateSut().ExplainLinePriceAsync(Request("CUST2", "I1"));

        Assert.True(result.Succeeded, result.Message);
        var explanation = result.Data!;

        Assert.True(explanation.Found);
        Assert.Equal(13.75m, explanation.UnitPrice);
        Assert.Equal(SaPriceSource.CustomerPriceList, explanation.WinningSource);

        Assert.Equal(4, explanation.Levels.Count);
        Assert.Equal(
            new[]
            {
                SaPriceSource.CustomerItem,
                SaPriceSource.CustomerPriceList,
                SaPriceSource.CustomerGroupPriceList,
                SaPriceSource.ItemDefault
            },
            explanation.Levels.Select(x => x.Source).ToArray());

        Assert.False(explanation.Levels[0].Applied);
        Assert.True(explanation.Levels[1].Applied);
        Assert.Equal(13.75m, explanation.Levels[1].UnitPrice);
        Assert.False(explanation.Levels[2].Applied);
        Assert.False(explanation.Levels[3].Applied);
    }

    /// <summary>
    /// The answer to the top support question: a level the COMPANY METHOD excludes is still reported, and
    /// says so, rather than silently disappearing from the ladder.
    /// </summary>
    [Fact]
    public async Task Explain_ReportsExcludedLevelsWithTheCompanyMethodAsTheReason()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var company = await db.Companies.SingleAsync(x => x.CompanyCode == "DEMO");
            company.SalesPriceMethod = SaCompanyPriceMethod.ItemDefaultOnly;
            await db.SaveChangesAsync();
        }

        var result = await CreateSut().ExplainLinePriceAsync(Request("CUST2", "I1"));
        Assert.True(result.Succeeded, result.Message);
        var explanation = result.Data!;

        Assert.True(explanation.Found);
        Assert.Equal(14m, explanation.UnitPrice);
        Assert.Equal(SaPriceSource.ItemDefault, explanation.WinningSource);

        var excluded = explanation.Levels.Where(x => !x.Eligible).ToList();
        Assert.Equal(3, excluded.Count);
        Assert.All(excluded, x =>
            Assert.Contains(SaCompanyPriceMethod.ItemDefaultOnly, x.Reason!, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The inquiry can never disagree with the price a document receives: the ladder's price is the
    /// resolver's tax-EXCLUSIVE price, level for level.
    /// </summary>
    [Theory]
    [InlineData("CUST1", "I1")]   // item default
    [InlineData("CUST2", "I1")]   // customer price list
    [InlineData("CUST4", "I1")]   // negotiated customer-item price
    [InlineData("CUST5", "I1")]   // group default price list (Phase 5)
    [InlineData("CUST6", "I1")]   // own list beats the group
    public async Task Explain_AgressWithTheResolvedPrice(string custCode, string iCode)
    {
        var sut = CreateSut();

        var resolved = await sut.ResolveLinePricingAsync(Request(custCode, iCode));
        Assert.True(resolved.Succeeded, resolved.Message);

        var explained = await sut.ExplainLinePriceAsync(Request(custCode, iCode));
        Assert.True(explained.Succeeded, explained.Message);

        Assert.True(explained.Data!.Found);
        Assert.Equal(resolved.Data!.BaseUnitPrice, explained.Data.UnitPrice);
        Assert.Equal(resolved.Data.PricingSource, explained.Data.WinningSource);
    }

    /// <summary>
    /// When nothing produces a price the ladder must still explain EVERY level, so the operator can see
    /// that each one was genuinely tried.
    /// </summary>
    [Fact]
    public async Task Explain_WhenNothingPricesTheItem_ExplainsEveryLevel()
    {
        var result = await CreateSut().ExplainLinePriceAsync(Request("CUST1", "I3"));

        Assert.True(result.Succeeded, result.Message);
        var explanation = result.Data!;

        Assert.False(explanation.Found);
        Assert.Null(explanation.UnitPrice);
        Assert.Equal(4, explanation.Levels.Count);
        Assert.All(explanation.Levels, x => Assert.False(x.Applied));
        Assert.All(explanation.Levels, x => Assert.False(string.IsNullOrWhiteSpace(x.Reason)));
    }

    /// <summary>The ladder is never blocked by pricing; a block is REPORTED as the outcome.</summary>
    [Fact]
    public async Task Explain_ReportsABlockAsTheOutcome()
    {
        var result = await CreateSut().ExplainLinePriceAsync(Request("DEALER1", "I1"));

        Assert.True(result.Succeeded, result.Message);
        var explanation = result.Data!;

        Assert.False(explanation.Found);
        Assert.False(string.IsNullOrWhiteSpace(explanation.Describe()));
        Assert.All(explanation.Levels, x => Assert.False(x.Applied));
    }

    // ═════════════════════════════ helpers ═════════════════════════════

    private static IvStockMaster Item(
        string company, string iCode, decimal price, string sellingUom, string iClass, bool active, byte rv) =>
        new()
        {
            CompanyCode = company,
            ICode = iCode,
            IDesc = $"Item {iCode}",
            IClassCode = iClass,
            StdUom = sellingUom,
            SellingUom = sellingUom,
            SellingPrice = price,
            IsActive = active,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, rv]
        };

    private static SaCust Customer(
        string company,
        string custCode,
        string name,
        byte rv,
        string? custPriceCode = null,
        string? priceMethod = null,
        string? custGroupCode = null) =>
        new()
        {
            CompanyCode = company,
            CustCode = custCode,
            CustName = name,
            Currency = "MYR",
            CustPriceCode = custPriceCode,
            CustGroupCode = custGroupCode,
            PriceMethod = priceMethod,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, rv]
        };

    private static SaLinePricingRequest Request(
        string custCode,
        string iCode,
        string uom = "PCS",
        decimal qty = 1m,
        bool isInclusive = false,
        decimal taxPercent = 0m,
        string? discountMethod = null) =>
        new()
        {
            CustCode = custCode,
            ICode = iCode,
            UOM = uom,
            Qty = qty,
            DocDate = FixedToday,
            DocumentCurrency = "MYR",
            IClass = "C1",
            TaxPercent = taxPercent,
            IsInclusive = isInclusive,
            DiscountMethod = discountMethod
        };

    private async Task SeedDiscountRuleAsync(int ruleId, decimal discountPercent)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.SaDisGroupItems.Add(new SaDisGroupItem
        {
            Id = ruleId,
            CompanyCode = "DEMO",
            ICode = "I2",
            QtyFr = 1m,
            QtyTo = 1000m,
            DateFr = new DateTime(2020, 1, 1),
            Discount = discountPercent,
            DiscountType = SaDiscountSlotTypes.Percentage,
            EffectPrice = SaEffectPriceOptions.Selling,
            GroupStatus = "NEW",
            RowVersion = [0, 0, 0, 0, 0, 0, 0, (byte)ruleId]
        });
        await db.SaveChangesAsync();
    }

    private async Task SetCompanyMethodAsync(string method)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var company = await db.Companies.SingleAsync(x => x.CompanyCode == "DEMO");
        // Write the NORMALISED token, exactly as CompanyService does on save.
        company.SalesPriceMethod = SaCompanyPriceMethod.Normalize(method);
        await db.SaveChangesAsync();
    }

    private async Task SetCompanyMethodRawAsync(string rawValue)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var company = await db.Companies.SingleAsync(x => x.CompanyCode == "DEMO");
        company.SalesPriceMethod = rawValue;
        await db.SaveChangesAsync();
    }

    private SaSalesRefService CreateSut(string? company = "DEMO")
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tenant = InventoryTenantTestHelper.CreateTenantContext(company ?? string.Empty, "HQ", "SITE");
        return new SaSalesRefService(
            _factory,
            tenant,
            access.Object,
            new FixedCurrentDateService(FixedToday),
            new SaCustLookupService(_factory, tenant));
    }
}
