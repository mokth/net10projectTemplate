using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

public class InventoryMastersPhase1Tests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public InventoryMastersPhase1Tests()
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
        db.Companies.Add(new Company
        {
            CompanyCode = "DEMO",
            CompanyName = "Demo",
            IsActive = true,
            CurrencyCode = "MYR",
            TimeZoneId = "Asia/Kuala_Lumpur"
        });
        await db.SaveChangesAsync();
        var companyId = db.Companies.Single().CompanyId;
        db.Branches.Add(new Branch
        {
            CompanyId = companyId,
            BranchCode = "HQ",
            BranchName = "Head Office",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Create_UOM_Item_Warehouse_creates_default_variant_and_MAIN_location()
    {
        var companyContext = await CreateResolvedContextAsync();
        var access = new Mock<IAccessRightService>();
        access.Setup(a => a.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var current = new StubUser();

        var uomService = new UomService(_factory, companyContext, current, access.Object, NullLogger<UomService>.Instance);
        var itemService = new ItemService(_factory, companyContext, current, access.Object, NullLogger<ItemService>.Instance);
        var whService = new WarehouseService(_factory, companyContext, current, access.Object, NullLogger<WarehouseService>.Instance);

        var uomResult = await uomService.AddAsync(new UOM { UOMCode = "PCS", UOMName = "Piece", DecimalPlaces = 0, IsActive = true });
        Assert.True(uomResult.Succeeded, uomResult.ErrorMessage);

        var itemResult = await itemService.AddAsync(new Item
        {
            ItemCode = "ITEM01",
            ItemDescription = "Test Item",
            BaseUOMId = uomResult.Item!.Id,
            IsStockItem = true,
            IsActive = true
        });
        Assert.True(itemResult.Succeeded, itemResult.ErrorMessage);

        var variants = await itemService.GetVariantsAsync(itemResult.Item!.Id);
        Assert.True(variants.Succeeded);
        Assert.Contains(variants.Items, v => v.IsDefault && v.SKU == "ITEM01");

        var whResult = await whService.AddAsync(new Warehouse
        {
            WarehouseCode = "WH1",
            WarehouseName = "Main WH",
            IsActive = true
        });
        Assert.True(whResult.Succeeded, whResult.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var main = await db.WarehouseLocations.SingleAsync(l => l.WarehouseId == whResult.Item!.Id);
        Assert.Equal("MAIN", main.LocationCode);
        Assert.Equal("Main", main.LocationName);
        Assert.True(main.IsActive);
    }

    private async Task<ICompanyContext> CreateResolvedContextAsync()
    {
        var ctx = new CompanyContext(new StubUser(), _factory);
        await ctx.ResolveAsync();
        return ctx;
    }

    private sealed class StubUser : ICurrentUserService
    {
        public bool IsAuthenticated => true;
        public string? UserId => "admin";
        public string? LoginId => "admin";
        public string? FullName => "Admin";
        public string? CompanyCode => "DEMO";
        public string? BranchCode => "HQ";
        public string? LocationCode => "MAIN";
        public string? UserLevel => "SYSTEM_ADMIN";
        public bool MustChangePassword => false;
        public string? SubjectUid => "1";
        public bool IsInRole(string role) => true;
    }
}
