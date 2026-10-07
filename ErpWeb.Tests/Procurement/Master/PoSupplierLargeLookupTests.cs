using ErpWeb.Core.Lookups;
using ErpWeb.Core.Purchase;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Procurement.Master;

[Trait(TestCategories.Name, TestCategories.Purchase)]
[Trait(TestCategories.Name, TestCategories.PurchaseMasters)]
public class PoSupplierLargeLookupTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PoSupplierLargeLookupTests()
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
            db.PoSuppliers.Add(MakeSupp($"S{i:D4}", $"Supplier {i}", isActive: true));
        }

        db.PoSuppliers.Add(MakeSupp("INACTIVE1", "Inactive", isActive: false));
        db.PoSuppliers.Add(MakeSupp("BR2-001", "Other branch", isActive: true, branch: "BR2"));
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Paged_search_is_branch_scoped()
    {
        var lookups = CreateLookups();
        var page = await lookups.SearchSuppliersPagedAsync(new LargeLookupSearchRequest { Take = 20 });
        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.Equal(20, page.Rows.Count);
        Assert.Equal(250, page.TotalCount);
        Assert.DoesNotContain(page.Rows, x => x.SuppCode == "INACTIVE1");
        Assert.DoesNotContain(page.Rows, x => x.SuppCode == "BR2-001");
    }

    [Fact]
    public async Task Exact_resolve_succeeds()
    {
        var lookups = CreateLookups();
        var result = await lookups.ResolveSupplierAsync("S0010");
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal("S0010", result.Item!.SuppCode);
    }

    [Fact]
    public async Task Other_branch_resolve_fails()
    {
        var lookups = CreateLookups();
        var result = await lookups.ResolveSupplierAsync("BR2-001");
        Assert.False(result.Succeeded);
    }

    private IPoSupplierLookupService CreateLookups() =>
        new PoSupplierLookupService(_factory, InventoryTenantTestHelper.CreateTenantContext());

    private static PoSupplier MakeSupp(
        string code,
        string name,
        bool isActive,
        string company = "DEMO",
        string branch = "HQ") =>
        new()
        {
            CompanyCode = company,
            BranchCode = branch,
            SuppCode = code,
            SuppName = name,
            IsActive = isActive,
            Currency = "MYR"
        };
}
