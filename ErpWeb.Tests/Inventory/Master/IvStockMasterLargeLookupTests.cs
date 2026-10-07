using ErpWeb.Core.Inventory;
using ErpWeb.Core.Lookups;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Inventory.Master;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryMasters)]
public class IvStockMasterLargeLookupTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvStockMasterLargeLookupTests()
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
            db.IvStockMasters.Add(MakeItem($"ITEM{i:D3}", $"Item {i}", isActive: true));
        }

        for (var i = 1; i <= 10; i++)
        {
            db.IvStockMasters.Add(MakeItem($"INACTIVE{i:D2}", $"Inactive {i}", isActive: false));
        }

        db.IvStockMasters.Add(MakeItem("OTHERCO1", "Other company", isActive: true, company: "OTHER"));
        db.IvStockMasters.Add(MakeItem("BAR-ONLY", "Barcode item", isActive: true, barcode: "BC-UNIQUE-1"));
        db.IvStockMasters.Add(MakeItem("BAR-A", "Dup barcode A", isActive: true, barcode: "BC-DUP"));
        db.IvStockMasters.Add(MakeItem("BAR-B", "Dup barcode B", isActive: true, barcode: "BC-DUP"));

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Paged_search_returns_take_20_with_correct_total()
    {
        var lookups = CreateLookups();
        var page = await lookups.SearchStockMastersPagedAsync(new LargeLookupSearchRequest
        {
            SearchText = null,
            Skip = 0,
            Take = 20
        });

        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.Equal(20, page.Rows.Count);
        // 250 active DEMO + BAR-ONLY + BAR-A + BAR-B = 253
        Assert.Equal(253, page.TotalCount);
        Assert.DoesNotContain(page.Rows, x => x.ICode.StartsWith("INACTIVE", StringComparison.Ordinal));
        Assert.DoesNotContain(page.Rows, x => x.ICode == "OTHERCO1");
    }

    [Fact]
    public async Task Paged_search_clamps_take_to_100()
    {
        var lookups = CreateLookups();
        var page = await lookups.SearchStockMastersPagedAsync(new LargeLookupSearchRequest
        {
            Take = 5000
        });

        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.Equal(100, page.Rows.Count);
    }

    [Fact]
    public async Task Paged_search_filters_by_text()
    {
        var lookups = CreateLookups();
        var page = await lookups.SearchStockMastersPagedAsync(new LargeLookupSearchRequest
        {
            SearchText = "ITEM001",
            Take = 20
        });

        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.Contains(page.Rows, x => x.ICode == "ITEM001");
        Assert.All(page.Rows, x =>
            Assert.True(
                x.ICode.Contains("ITEM001", StringComparison.OrdinalIgnoreCase)
                || (x.IDesc?.Contains("ITEM001", StringComparison.OrdinalIgnoreCase) ?? false)));
    }

    [Fact]
    public async Task Exact_resolve_by_code()
    {
        var lookups = CreateLookups();
        var result = await lookups.ResolveItemAsync("ITEM042");
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal("ITEM042", result.Item!.ICode);
    }

    [Fact]
    public async Task Exact_resolve_by_barcode()
    {
        var lookups = CreateLookups();
        var result = await lookups.ResolveItemAsync("BC-UNIQUE-1");
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal("BAR-ONLY", result.Item!.ICode);
    }

    [Fact]
    public async Task Ambiguous_barcode_does_not_auto_select()
    {
        var lookups = CreateLookups();
        var result = await lookups.ResolveItemAsync("BC-DUP");
        Assert.False(result.Succeeded);
        Assert.Contains("multiple", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Item);
    }

    [Fact]
    public async Task Inactive_excluded_from_exact_resolve()
    {
        var lookups = CreateLookups();
        var result = await lookups.ResolveItemAsync("INACTIVE01");
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Large_lookup_request_normalizes_take()
    {
        var request = new LargeLookupSearchRequest { Take = 0, Skip = -5 };
        Assert.Equal(LargeLookupSearchRequest.DefaultPageSize, request.NormalizedTake);
        Assert.Equal(0, request.NormalizedSkip);

        request.Take = 999;
        Assert.Equal(LargeLookupSearchRequest.MaxPageSize, request.NormalizedTake);
    }

    private IIvInventoryLookupService CreateLookups(string company = "DEMO", string branch = "HQ")
    {
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.CompanyCode).Returns(company);
        current.SetupGet(x => x.BranchCode).Returns(branch);
        return new IvInventoryLookupService(
            current.Object,
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            _factory);
    }

    private static IvStockMaster MakeItem(
        string code,
        string desc,
        bool isActive,
        string company = "DEMO",
        string? barcode = null) =>
        new()
        {
            CompanyCode = company,
            ICode = code,
            IDesc = desc,
            StdUom = "EA",
            IsActive = isActive,
            Barcode = barcode,
            StockControl = false,
            LotControl = false
        };
}
