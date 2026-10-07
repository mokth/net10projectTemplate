using ErpWeb.Core.Lookups;
using ErpWeb.Core.Sales;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Sales.Master;

[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.SalesMasters)]
public class SaCustomerLargeLookupTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaCustomerLargeLookupTests()
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
        for (var i = 1; i <= 250; i++)
        {
            db.SaCusts.Add(MakeCust($"C{i:D4}", $"Customer {i}", isActive: true));
        }

        db.SaCusts.Add(MakeCust("INACTIVE1", "Inactive", isActive: false));
        db.SaCusts.Add(MakeCust("OTHER1", "Other Co", isActive: true, company: "OTHER"));
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Paged_search_returns_20_with_total()
    {
        var lookups = CreateLookups();
        var page = await lookups.SearchCustomersPagedAsync(new LargeLookupSearchRequest { Take = 20 });
        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.Equal(20, page.Rows.Count);
        Assert.Equal(250, page.TotalCount);
        Assert.DoesNotContain(page.Rows, x => x.CustCode == "INACTIVE1");
        Assert.DoesNotContain(page.Rows, x => x.CustCode == "OTHER1");
    }

    [Fact]
    public async Task Exact_resolve_succeeds()
    {
        var lookups = CreateLookups();
        var result = await lookups.ResolveCustomerAsync("C0042");
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal("C0042", result.Item!.CustCode);
        Assert.Equal("Customer 42", result.Item.CustName);
    }

    [Fact]
    public async Task Inactive_resolve_fails()
    {
        var lookups = CreateLookups();
        var result = await lookups.ResolveCustomerAsync("INACTIVE1");
        Assert.False(result.Succeeded);
    }

    private ISaCustLookupService CreateLookups() =>
        new SaCustLookupService(_factory, InventoryTenantTestHelper.CreateTenantContext());

    private static SaCust MakeCust(string code, string name, bool isActive, string company = "DEMO") =>
        new()
        {
            CompanyCode = company,
            CustCode = code,
            CustName = name,
            IsActive = isActive,
            Currency = "MYR"
        };
}
