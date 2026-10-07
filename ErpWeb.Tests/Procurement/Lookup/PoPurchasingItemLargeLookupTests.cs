using ErpWeb.Core.Lookups;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Procurement.Lookup;

[Trait(TestCategories.Name, TestCategories.Purchase)]
[Trait(TestCategories.Name, TestCategories.PurchaseMasters)]
public class PoPurchasingItemLargeLookupTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PoPurchasingItemLargeLookupTests()
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
        for (var i = 1; i <= 30; i++)
        {
            db.IvStockMasters.Add(MakeStock($"S{i:D3}", $"Stock {i}", purchasePrice: 10m + i));
        }

        db.IvStockMasters.Add(MakeStock("INACTIVE1", "Inactive stock", isActive: false));
        db.IvStockMasters.Add(MakeStock("OTHER1", "Other co", company: "OTHER"));
        db.IvStockMasters.Add(MakeStock("BOTH1", "Stock BOTH1", purchasePrice: 99m));

        for (var i = 1; i <= 20; i++)
        {
            db.PoPurItems.Add(MakeIndirect($"I{i:D3}", $"Indirect {i}", unitPrice: 5m + i));
        }

        db.PoPurItems.Add(MakeIndirect("BOTH1", "Indirect BOTH1", unitPrice: 11m));
        db.PoPurItems.Add(MakeIndirect("OTHERI", "Other co indirect", company: "OTHER"));

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Paged_search_returns_stock_and_indirect()
    {
        var lookups = CreateLookups(canViewCost: true);
        var page = await lookups.SearchPagedAsync(new LargeLookupSearchRequest { Take = 20 });
        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.Equal(20, page.Rows.Count);
        // 31 stock (30 + BOTH1) + 21 indirect (20 + BOTH1) in DEMO = 52
        Assert.Equal(52, page.TotalCount);
        Assert.Contains(page.Rows, x => !x.IsIndirect);
        Assert.DoesNotContain(page.Rows, x => x.ICode == "INACTIVE1");
        Assert.DoesNotContain(page.Rows, x => x.ICode == "OTHER1");
    }

    [Fact]
    public async Task Search_identifies_source_label()
    {
        var lookups = CreateLookups(canViewCost: true);
        var stockPage = await lookups.SearchPagedAsync(new LargeLookupSearchRequest
        {
            SearchText = "S001",
            Take = 10
        });
        Assert.True(stockPage.Succeeded, stockPage.ErrorMessage);
        var stock = Assert.Single(stockPage.Rows);
        Assert.False(stock.IsIndirect);
        Assert.Equal("Stock", stock.SourceLabel);

        var indirectPage = await lookups.SearchPagedAsync(new LargeLookupSearchRequest
        {
            SearchText = "I001",
            Take = 10
        });
        Assert.True(indirectPage.Succeeded, indirectPage.ErrorMessage);
        var indirect = Assert.Single(indirectPage.Rows);
        Assert.True(indirect.IsIndirect);
        Assert.Equal("Indirect", indirect.SourceLabel);
    }

    [Fact]
    public async Task Ambiguous_same_code_does_not_auto_select()
    {
        var lookups = CreateLookups(canViewCost: true);
        var result = await lookups.ResolveAsync("BOTH1");
        Assert.False(result.Succeeded);
        Assert.True(result.Ambiguous);
    }

    [Fact]
    public async Task Exact_stock_and_indirect_resolve()
    {
        var lookups = CreateLookups(canViewCost: true);
        var stock = await lookups.ResolveAsync("S005");
        Assert.True(stock.Succeeded, stock.ErrorMessage);
        Assert.Equal("S005", stock.Item!.ICode);
        Assert.False(stock.Item.IsIndirect);
        Assert.Equal(15m, stock.Item.UnitPrice);

        var indirect = await lookups.ResolveAsync("I002");
        Assert.True(indirect.Succeeded, indirect.ErrorMessage);
        Assert.Equal("I002", indirect.Item!.ICode);
        Assert.True(indirect.Item.IsIndirect);
        Assert.Equal(7m, indirect.Item.UnitPrice);
    }

    [Fact]
    public async Task Cost_hidden_without_view_cost()
    {
        var lookups = CreateLookups(canViewCost: false);
        var resolved = await lookups.ResolveAsync("S001", MenuCodes.PurchaseRequisition);
        Assert.True(resolved.Succeeded, resolved.ErrorMessage);
        Assert.Null(resolved.Item!.UnitPrice);

        var page = await lookups.SearchPagedAsync(
            new LargeLookupSearchRequest { SearchText = "S001", Take = 5 },
            MenuCodes.PurchaseRequisition);
        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.All(page.Rows, x => Assert.Null(x.UnitPrice));
    }

    [Fact]
    public async Task IncludeIndirect_false_excludes_pur_items()
    {
        var lookups = CreateLookups(canViewCost: true);
        var page = await lookups.SearchPagedAsync(
            new LargeLookupSearchRequest { Take = 100 },
            includeIndirect: false);
        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.Equal(31, page.TotalCount); // 30 numbered + BOTH1 stock
        Assert.All(page.Rows, x => Assert.False(x.IsIndirect));
    }

    private IPoPurchasingItemLookupService CreateLookups(bool canViewCost)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string permission, CancellationToken _) =>
                permission != PermissionCodes.ViewCost || canViewCost);

        return new PoPurchasingItemLookupService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            access.Object);
    }

    private static IvStockMaster MakeStock(
        string code,
        string desc,
        bool isActive = true,
        string company = "DEMO",
        decimal purchasePrice = 10m) =>
        new()
        {
            CompanyCode = company,
            ICode = code,
            IDesc = desc,
            StdUom = "EA",
            PurUom = "EA",
            PurStdPackSize = 1m,
            PurchasePrice = purchasePrice,
            IsActive = isActive,
            RowVersion = Guid.NewGuid().ToByteArray()
        };

    private static PoPurItem MakeIndirect(
        string code,
        string desc,
        string company = "DEMO",
        decimal unitPrice = 5m) =>
        new()
        {
            CompanyCode = company,
            BranchCode = "HQ",
            ICode = code,
            IDesc = desc,
            PurUom = "EA",
            Vendor = "SUP01",
            VendName = "Supplier",
            UnitPrice = unitPrice,
            Moq = 0m,
            Category = "GEN",
            RowVersion = Guid.NewGuid().ToByteArray()
        };
}
