using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Tests.Infrastructure.Helpers;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Sales.Master;

[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.SalesMasters)]
public sealed class SaPriceMaintenanceServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaPriceMaintenanceServiceTests()
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
        db.IvStockMasters.AddRange(
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "ACTIVE",
                IDesc = "Active item",
                IsActive = true,
                SellingUom = "PCS",
                SellingPrice = 10m,
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 1]
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "INACTIVE",
                IDesc = "Inactive item",
                IsActive = false,
                SellingUom = "PCS",
                SellingPrice = 20m,
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 2]
            },
            new IvStockMaster
            {
                CompanyCode = "OTHER",
                ICode = "OTHER-ITEM",
                IDesc = "Other company item",
                IsActive = true,
                SellingUom = "PCS",
                SellingPrice = 30m,
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 3]
            });
        db.IvCustPriceGroups.Add(new IvCustPriceGroup
        {
            CompanyCode = "DEMO",
            CustPriceCode = "PL-1",
            CustPriceDesc = "Demo price list",
            IsActive = true,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 4]
        });
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "CUST-1",
            CustName = "Direct price-list customer",
            CustPriceCode = "PL-1",
            IsActive = true
        });
        db.SaCustGroups.Add(new SaCustGroup
        {
            CompanyCode = "DEMO",
            CustGroupCode = "GROUP-1",
            CustGroupDesc = "Default price-list group",
            CustPriceCode = "PL-1"
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Load_all_active_items_never_includes_inactive_rows()
    {
        var result = await CreateSut().SearchAsync(new SaPriceReviewQuery
        {
            TargetType = SaPriceMaintenanceTargets.ItemDefault,
            LoadAllActiveItems = true,
            ActiveItemsOnly = false
        });

        Assert.True(result.Succeeded, result.Message);
        Assert.Single(result.Data!.Rows);
        Assert.Equal("ACTIVE", result.Data.Rows[0].ItemCode);
    }

    [Fact]
    public async Task Irrelevant_customer_filter_cannot_open_unbounded_item_default_review()
    {
        var result = await CreateSut().SearchAsync(new SaPriceReviewQuery
        {
            TargetType = SaPriceMaintenanceTargets.ItemDefault,
            CustCode = "CUST-ONLY"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("scope filter", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Irrelevant_price_list_filter_cannot_open_unbounded_customer_special_review()
    {
        var result = await CreateSut().SearchAsync(new SaPriceReviewQuery
        {
            TargetType = SaPriceMaintenanceTargets.CustomerItem,
            CustPriceCode = "PL-ONLY"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("scope filter", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Price_list_impact_is_company_scoped_and_counts_assignments()
    {
        var result = await CreateSut().GetPriceListImpactAsync("PL-1");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("PL-1", result.Data!.CustPriceCode);
        Assert.Equal(1, result.Data.CustomersAssignedDirectly);
        Assert.Equal(1, result.Data.CustomerGroupsUsingAsDefault);
    }

    private SaPriceMaintenanceService CreateSut()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tenant = new Mock<ITenantScopeContext>();
        tenant.Setup(x => x.TryCompanyScope()).Returns(Scope());
        tenant.Setup(x => x.TryWriteScope()).Returns(Scope());

        return new SaPriceMaintenanceService(
            _factory,
            tenant.Object,
            access.Object,
            new FixedCurrentDateService(new DateTime(2026, 10, 10)));
    }

    private static TenantScope Scope() => new()
    {
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        LocationCode = "SITE",
        UserId = "admin"
    };
}
