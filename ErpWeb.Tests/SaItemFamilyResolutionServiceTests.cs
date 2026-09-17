using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// Resolution over the real database (plan §7/§8/§8.5/§24): the service entry points load the customer's
/// own <c>PriceMethod</c>/<c>CustPriceCode</c>, the candidate rows, and the legacy <c>float</c> columns,
/// then delegate to the pure rules. This file is what proves the wiring, the tenant scope and the
/// legacy-type scaling that the pure contract tests cannot reach.
/// </summary>
public class SaItemFamilyResolutionServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 15);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaItemFamilyResolutionServiceTests()
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

        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "I1",
            IDesc = "Item one",
            IClassCode = "C1",
            StdUom = "PCS",
            SellingUom = "PCS",
            SellingPrice = 14m,
            IsActive = true,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 1]
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "OTHER",
            ICode = "I1",
            IDesc = "Other company item",
            StdUom = "PCS",
            SellingUom = "PCS",
            SellingPrice = 99m,
            IsActive = true,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 2]
        });

        db.MsUoms.Add(new MsUom
        {
            CompanyCode = "DEMO", UomCode = "PCS", UomDesc = "Pieces", IsActive = true,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 3]
        });
        db.IvClasses.Add(new IvClass
        {
            CompanyCode = "DEMO", IClassCode = "C1", IDesc = "Class one",
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 4]
        });

        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUST1",
            CustName = "Customer one",
            Currency = "MYR",
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 5]
        });
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "DEALER1",
            CustName = "Dealer customer",
            PriceMethod = SaCustPaymentOptions.PriceDealer,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 6]
        });
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "OTHER",
            CustCode = "CUST1",
            CustName = "Other company customer",
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 7]
        });

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    // ═════════════════════════════ §7 — the DB-loaded price chain ═════════════════════════════

    [Fact]
    public async Task PriceResolution_FallsThroughToTheItemSellingPrice_WhenNothingElseExists()
    {
        var sut = CreateSut();

        var result = await sut.ResolveItemPriceAsync(PriceRequest("CUST1", "I1", "PCS"));

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Data!.Found);
        Assert.Equal(SaItemFamilyPriceSources.ItemSellingPrice, result.Data.Source);
        Assert.Equal(14m, result.Data.UnitPrice);
    }

    [Fact]
    public async Task PriceResolution_UsesTheCustomerItemRow_AndTheCustomerPricedListIsNotConsulted()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SaItemCusts.Add(new SaItemCust
            {
                CompanyCode = "DEMO",
                CustCode = "CUST1",
                ICode = "I1",
                SellingUOM = "PCS",
                MOQ = 0,
                CustICode = "CUST-PART-1",
                UnitPrice = 12.34,
                Status = "NEW",
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 11]
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var result = await sut.ResolveItemPriceAsync(PriceRequest("CUST1", "I1", "PCS"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(SaItemFamilyPriceSources.CustomerItem, result.Data!.Source);
        Assert.Equal(12.34m, result.Data.UnitPrice);
    }

    [Fact]
    public async Task PriceResolution_ReadsCustPriceCodeFromTheCustomer_AndResolvesFromThePriceList()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvCustPriceGroups.Add(new IvCustPriceGroup
            {
                CompanyCode = "DEMO",
                CustPriceCode = "PL1",
                CustPriceDesc = "PRICE LIST 1",
                IsActive = true,
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 12]
            });
            db.IvCustPrices.Add(new IvCustPrice
            {
                CompanyCode = "DEMO",
                CustPriceCode = "PL1",
                ICode = "I1",
                UOM = "PCS",
                SellingPrice = 13.75m
            });
            var customer = await db.SaCusts.SingleAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST1");
            customer.CustPriceCode = "PL1";
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var result = await sut.ResolveItemPriceAsync(PriceRequest("CUST1", "I1", "PCS"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(SaItemFamilyPriceSources.PriceList, result.Data!.Source);
        Assert.Equal(13.75m, result.Data.UnitPrice);
    }

    [Fact]
    public async Task PriceResolution_RetiredPriceList_IsTolerated_AndFallsThrough()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvCustPriceGroups.Add(new IvCustPriceGroup
            {
                CompanyCode = "DEMO",
                CustPriceCode = "PL2",
                CustPriceDesc = "RETIRED",
                IsActive = false,
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 13]
            });
            db.IvCustPrices.Add(new IvCustPrice
            {
                CompanyCode = "DEMO",
                CustPriceCode = "PL2",
                ICode = "I1",
                UOM = "PCS",
                SellingPrice = 9m
            });
            var customer = await db.SaCusts.SingleAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST1");
            customer.CustPriceCode = "PL2";
            await db.SaveChangesAsync();
        }

        // A retired list is no longer offered for assignment, but a customer still pointing at it must not
        // be blocked from selling (D-6 clause 3).
        var sut = CreateSut();
        var result = await sut.ResolveItemPriceAsync(PriceRequest("CUST1", "I1", "PCS"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(9m, result.Data!.UnitPrice);
        Assert.Equal(SaItemFamilyPriceSources.PriceList, result.Data.Source);
    }

    [Fact]
    public async Task PriceResolution_DealerCustomer_FailsClosed_FromTheStoredPriceMethod()
    {
        var sut = CreateSut();

        var result = await sut.ResolveItemPriceAsync(PriceRequest("DEALER1", "I1", "PCS"));

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("Dealer pricing is not supported", result.Message!);
    }

    [Fact]
    public async Task PriceResolution_UnknownCustomer_IsNotFound()
    {
        var sut = CreateSut();

        var result = await sut.ResolveItemPriceAsync(PriceRequest("NOPE", "I1", "PCS"));

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task PriceResolution_NoCandidate_Blocks_AndNeverReturnsZero()
    {
        var sut = CreateSut();

        // No UOM match: the item's selling UOM is PCS and BOX has no price anywhere.
        var result = await sut.ResolveItemPriceAsync(PriceRequest("CUST1", "I1", "BOX"));

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("No price found", result.Message!);
        Assert.Null(result.Data);
    }

    [Theory]
    [InlineData("", "I1", "PCS")]
    [InlineData("CUST1", "", "PCS")]
    [InlineData("CUST1", "I1", "")]
    public async Task PriceResolution_MissingKeyParts_AreAValidationError(
        string custCode,
        string iCode,
        string uom)
    {
        var sut = CreateSut();

        var result = await sut.ResolveItemPriceAsync(PriceRequest(custCode, iCode, uom));

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("required", result.Message!);
    }

    [Fact]
    public async Task PriceResolution_IsTenantScoped_AcrossCustomersPricesAndItems()
    {
        // The OTHER company has a customer, an item price and an item price of its own; none of it may
        // leak into a DEMO resolution (the DEMO customer/item do not exist there).
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvCustPriceGroups.Add(new IvCustPriceGroup
            {
                CompanyCode = "OTHER",
                CustPriceCode = "PL1",
                CustPriceDesc = "OTHER LIST",
                IsActive = true,
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 14]
            });
            db.IvCustPrices.Add(new IvCustPrice
            {
                CompanyCode = "OTHER",
                CustPriceCode = "PL1",
                ICode = "I1",
                UOM = "PCS",
                SellingPrice = 1m
            });
            await db.SaveChangesAsync();
        }

        var other = CreateSut(company: "OTHER");
        var result = await other.ResolveItemPriceAsync(PriceRequest("CUST1", "I1", "PCS"));

        // OTHER/CUST1 exists but has no CustPriceCode, so it resolves from OTHER/I1 — never from DEMO.
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(99m, result.Data!.UnitPrice);
        Assert.Equal(SaItemFamilyPriceSources.ItemSellingPrice, result.Data.Source);
    }

    [Fact]
    public async Task PriceResolution_WithoutACompanyContext_IsRejected() =>
        Assert.Equal(
            IvMasterErrorCode.InvalidScope,
            (await CreateSut(company: null).ResolveItemPriceAsync(PriceRequest("CUST1", "I1", "PCS"))).ErrorCode);

    // ═════════════════════════════ §8.5 — legacy float scaling ═════════════════════════════

    [Fact]
    public async Task PriceResolution_ScalesTheLegacyFloatUnitPrice_ToFourDecimals()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SaItemCusts.Add(new SaItemCust
            {
                CompanyCode = "DEMO",
                CustCode = "CUST1",
                ICode = "I1",
                SellingUOM = "PCS",
                MOQ = 0,
                CustICode = "CUST-PART-1",
                // The live column is `float`; a value with more precision than the contract allows must be
                // scaled once, explicitly, at the service boundary — never carried as a float.
                UnitPrice = 12.3456789,
                Status = "NEW",
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 15]
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var result = await sut.ResolveItemPriceAsync(PriceRequest("CUST1", "I1", "PCS"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(12.3457m, result.Data!.UnitPrice);
    }

    // ═════════════════════════════ §8 — the DB-loaded discount chain ═════════════════════════════

    [Fact]
    public async Task DiscountResolution_MatchesTheBandAndWindow_AndMapsTheSlots()
    {
        await SeedRuleAsync(id: 1, qtyFr: 1m, qtyTo: 10m, dateFr: FixedToday, dateTo: null, discount: 10m, discount1: 2.5m, discount1Type: SaDiscountSlotTypes.Amount);
        await SeedRuleAsync(id: 2, qtyFr: 11m, qtyTo: 20m, dateFr: FixedToday, dateTo: null, discount: 20m);

        var sut = CreateSut();
        var result = await sut.ResolveItemDiscountAsync(DiscountRequest(qty: 5m, method: "SPLIT"));

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Data!.Found);
        Assert.Equal(1, result.Data.RuleId);
        Assert.Equal(10m, result.Data.PercentSlot1);
        Assert.Equal(2.5m, result.Data.AmountSlot2);
        Assert.Equal(12.5m, result.Data.DiscountPerUnit);
    }

    [Fact]
    public async Task DiscountResolution_NoMatchingRule_IsNotAnError()
    {
        await SeedRuleAsync(id: 1, qtyFr: 1m, qtyTo: 10m, dateFr: FixedToday, dateTo: null, discount: 10m);

        var sut = CreateSut();
        var result = await sut.ResolveItemDiscountAsync(DiscountRequest(qty: 500m, method: "JOIN"));

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.Data!.Found);
        Assert.Null(result.Data.RuleId);
    }

    [Fact]
    public async Task DiscountResolution_OpenEndedWindow_KeepsMatchingOnLaterDates()
    {
        await SeedRuleAsync(id: 1, qtyFr: 1m, qtyTo: 10m, dateFr: FixedToday, dateTo: null, discount: 10m);

        var sut = CreateSut();
        var result = await sut.ResolveItemDiscountAsync(DiscountRequest(qty: 5m, method: "JOIN", docDate: FixedToday.AddYears(3)));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Data!.RuleId);
    }

    [Fact]
    public async Task DiscountResolution_ClosedWindow_StopsMatchingAfterTheEndDate()
    {
        await SeedRuleAsync(id: 1, qtyFr: 1m, qtyTo: 10m, dateFr: FixedToday, dateTo: FixedToday, discount: 10m);

        var sut = CreateSut();
        var onBoundary = await sut.ResolveItemDiscountAsync(DiscountRequest(qty: 5m, method: "JOIN"));
        var afterWindow = await sut.ResolveItemDiscountAsync(DiscountRequest(qty: 5m, method: "JOIN", docDate: FixedToday.AddDays(1)));

        Assert.True(onBoundary.Data!.Found);          // inclusive end date
        Assert.False(afterWindow.Data!.Found);
    }

    [Fact]
    public async Task DiscountResolution_ComparesTheWindowOnTheDatePart_SoAStoredTimeCannotExcludeTheDay()
    {
        // The legacy column is `datetime`; a 15:00 end time must still cover the whole day (§8.5).
        await SeedRuleAsync(id: 1, qtyFr: 1m, qtyTo: 10m, dateFr: FixedToday.AddHours(15), dateTo: FixedToday.AddHours(15), discount: 10m);

        var sut = CreateSut();
        var result = await sut.ResolveItemDiscountAsync(DiscountRequest(qty: 5m, method: "JOIN"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Data!.RuleId);
    }

    [Fact]
    public async Task DiscountResolution_LegacyOverlap_IsDeterministic_AndReportsTheLosers()
    {
        // Save-time validation prevents new overlaps, but live legacy rows may already overlap (§8.3.1).
        await SeedRuleAsync(id: 1, qtyFr: 1m, qtyTo: 10m, dateFr: FixedToday, dateTo: null, discount: 1m);
        await SeedRuleAsync(id: 2, qtyFr: 5m, qtyTo: 10m, dateFr: FixedToday, dateTo: null, discount: 2m);

        var sut = CreateSut();
        var first = await sut.ResolveItemDiscountAsync(DiscountRequest(qty: 7m, method: "JOIN"));
        var second = await sut.ResolveItemDiscountAsync(DiscountRequest(qty: 7m, method: "JOIN"));

        Assert.Equal(2, first.Data!.RuleId);            // higher QtyFr wins
        Assert.Equal(new[] { 1 }, first.Data.CompetingRuleIds);
        Assert.Equal(first.Data.RuleId, second.Data!.RuleId);   // stable across calls
    }

    [Fact]
    public async Task DiscountResolution_IsTenantScoped_SoAnotherCompanysRuleDoesNotApply()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SaDisGroupItems.Add(new SaDisGroupItem
            {
                CompanyCode = "OTHER",
                ICode = "I1",
                QtyFr = 1m,
                QtyTo = 10m,
                DateFr = FixedToday,
                Discount = 50m,
                DiscountType = SaDiscountSlotTypes.Percentage,
                GroupStatus = "NEW",
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 21]
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var result = await sut.ResolveItemDiscountAsync(DiscountRequest(qty: 5m, method: "JOIN"));

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.Data!.Found);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task DiscountResolution_BlankItem_IsAValidationError(string iCode)
    {
        var sut = CreateSut();

        var result = await sut.ResolveItemDiscountAsync(new SaItemFamilyDiscountRequest
        {
            ICode = iCode,
            Qty = 5m,
            DocDate = FixedToday,
            UnitPrice = 100m
        });

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task DiscountResolution_ZeroQuantity_IsAValidationError()
    {
        var sut = CreateSut();

        var result = await sut.ResolveItemDiscountAsync(DiscountRequest(qty: 0m, method: "JOIN"));

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("Quantity must be greater than zero", result.Message!);
    }

    [Fact]
    public async Task DiscountResolution_WithoutACompanyContext_IsRejected() =>
        Assert.Equal(
            IvMasterErrorCode.InvalidScope,
            (await CreateSut(company: null).ResolveItemDiscountAsync(DiscountRequest(5m, "JOIN"))).ErrorCode);

    // ═════════════════════════════ helpers ═════════════════════════════

    private static SaItemFamilyPriceRequest PriceRequest(string custCode, string iCode, string uom) =>
        new()
        {
            CustCode = custCode,
            ICode = iCode,
            UOM = uom,
            Qty = 1m,
            DocDate = FixedToday,
            PayCode = "CASH",
            DocumentCurrency = "MYR"
        };

    private static SaItemFamilyDiscountRequest DiscountRequest(
        decimal qty,
        string? method,
        DateTime? docDate = null) =>
        new()
        {
            ICode = "I1",
            IClass = "C1",
            Qty = qty,
            DocDate = docDate ?? FixedToday,
            UnitPrice = 100m,
            DiscountMethod = method
        };

    private async Task SeedRuleAsync(
        int id,
        decimal qtyFr,
        decimal qtyTo,
        DateTime dateFr,
        DateTime? dateTo,
        decimal? discount,
        decimal? discount1 = null,
        string? discount1Type = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.SaDisGroupItems.Add(new SaDisGroupItem
        {
            CompanyCode = "DEMO",
            ICode = "I1",
            QtyFr = qtyFr,
            QtyTo = qtyTo,
            DateFr = dateFr,
            DateTo = dateTo,
            Discount = discount,
            DiscountType = discount is null ? null : SaDiscountSlotTypes.Percentage,
            Discount1 = discount1,
            DiscountType1 = discount1Type,
            EffectPrice = SaEffectPriceOptions.Selling,
            GroupStatus = "NEW",
            RowVersion = [0, 0, 0, 0, 0, 0, 0, (byte)id]
        });
        await db.SaveChangesAsync();
    }

    private SaSalesRefService CreateSut(string? company = "DEMO")
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tenant = company is null
            ? InventoryTenantTestHelper.CreateTenantContext(string.Empty, "HQ", "SITE")
            : InventoryTenantTestHelper.CreateTenantContext(company, "HQ", "SITE");

        return new SaSalesRefService(
            _factory,
            tenant,
            access.Object,
            new FixedCurrentDateService(FixedToday),
            new SaCustLookupService(_factory, tenant));
    }
}
