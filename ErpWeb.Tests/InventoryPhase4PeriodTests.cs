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

/// <summary>
/// Phase 4 exit: July close viewed Aug 10 historically correct
/// (snapshot/as-of from ledger, not live StockBalance).
/// </summary>
public class InventoryPhase4PeriodTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private ICompanyContext _company = null!;
    private IPostingEngine _engine = null!;
    private IInventoryPeriodService _periods = null!;
    private IInventoryAsOfService _asOf = null!;
    private long _uomId;
    private long _variantId;
    private long _whId;
    private long _locId;

    public InventoryPhase4PeriodTests()
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
            BranchName = "HQ",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        _company = new CompanyContext(new StubUser(), _factory);
        await _company.ResolveAsync();
        var access = new Mock<IAccessRightService>();
        access.Setup(a => a.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var current = new StubUser();

        var uomService = new UomService(_factory, _company, current, access.Object, NullLogger<UomService>.Instance);
        var itemService = new ItemService(_factory, _company, current, access.Object, NullLogger<ItemService>.Instance);
        var whService = new WarehouseService(_factory, _company, current, access.Object, NullLogger<WarehouseService>.Instance);

        _uomId = (await uomService.AddAsync(new UOM { UOMCode = "PCS", UOMName = "Piece", IsActive = true })).Item!.Id;
        var item = await itemService.AddAsync(new Item
        {
            ItemCode = "P4ITEM",
            ItemDescription = "Period Item",
            BaseUOMId = _uomId,
            IsStockItem = true,
            IsActive = true
        });
        _variantId = (await itemService.GetVariantsAsync(item.Item!.Id)).Items.Single(v => v.IsDefault).Id;
        _whId = (await whService.AddAsync(new Warehouse { WarehouseCode = "WH1", WarehouseName = "WH1", IsActive = true })).Item!.Id;
        await using var db2 = await _factory.CreateDbContextAsync();
        _locId = (await db2.WarehouseLocations.SingleAsync(l => l.WarehouseId == _whId)).Id;

        _engine = new PostingEngine(_factory, _company, current, access.Object, NullLogger<PostingEngine>.Instance);
        _periods = new InventoryPeriodService(_factory, _company, current, access.Object, NullLogger<InventoryPeriodService>.Instance);
        _asOf = new InventoryAsOfService(_factory, _company, access.Object, current);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task July_close_viewed_Aug10_is_historically_correct()
    {
        // Use a fixed year so the test is deterministic
        var year = 2025;
        var july15 = new DateTime(year, 7, 15);
        var july31 = new DateTime(year, 7, 31);
        var aug5 = new DateTime(year, 8, 5);
        var aug10 = new DateTime(year, 8, 10);

        var july = await _periods.EnsurePeriodAsync(year, 7);
        var august = await _periods.EnsurePeriodAsync(year, 8);
        Assert.True(july.Succeeded && august.Succeeded);

        // July activity
        await PostGrn(july15, 100, 10m);
        await PostGrn(july15, 50, 14m);
        // July end: Qty 150, Value 1000+700=1700, Cost 1700/150 ≈ 11.333333

        var julyAsOfBeforeClose = await _asOf.GetAsOfValuationAsync(july31);
        Assert.True(julyAsOfBeforeClose.Succeeded);
        var julyLine = julyAsOfBeforeClose.Valuation!.Lines.Single(l => l.ItemVariantId == _variantId);
        Assert.Equal(150m, julyLine.Qty);
        Assert.Equal(1700m, julyLine.Value);

        // August activity (after July end) — live balances diverge
        await PostGi(aug5, 30);
        await PostGrn(aug5, 20, 20m);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var live = await db.StockBalances.SingleAsync(b => b.WarehouseId == _whId && b.ItemVariantId == _variantId);
            Assert.Equal(140m, live.QtyOnHand); // 150 - 30 + 20
        }

        // Close July (idempotent snapshot from ledger ≤ July 31)
        var close = await _periods.ClosePeriodAsync(july.Period!.Id, "admin");
        Assert.True(close.Succeeded, close.ErrorMessage);
        Assert.True(close.Period!.IsClosed);
        Assert.Single(close.Snapshots);
        Assert.Equal(150m, close.Snapshots[0].ClosingQty);
        Assert.Equal(1700m, close.Snapshots[0].ClosingValue);
        Assert.Equal(july31, close.Snapshots[0].SnapshotDate);

        // Aug 10: viewing July history must match July close — not live Aug balances
        var viewedAug10 = await _periods.GetSnapshotsAsync(july.Period.Id);
        Assert.True(viewedAug10.Succeeded);
        Assert.Equal(150m, viewedAug10.Snapshots.Single().ClosingQty);
        Assert.Equal(1700m, viewedAug10.Snapshots.Single().ClosingValue);

        var asOfJulyFromAug10 = await _asOf.GetAsOfValuationAsync(july31);
        Assert.Equal(150m, asOfJulyFromAug10.Valuation!.Lines.Single().Qty);
        Assert.Equal(1700m, asOfJulyFromAug10.Valuation.Lines.Single().Value);

        var asOfAug10 = await _asOf.GetAsOfValuationAsync(aug10);
        Assert.Equal(140m, asOfAug10.Valuation!.Lines.Single().Qty);
        Assert.NotEqual(asOfJulyFromAug10.Valuation.TotalValue, asOfAug10.Valuation.TotalValue);

        // July DocDate posting blocked after close
        var blocked = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = july15,
            WarehouseId = _whId,
            Lines =
            [
                new CreateDocumentLineDto
                {
                    ItemVariantId = _variantId, UOMId = _uomId, Qty = 1, UnitCost = 1, LocationId = _locId
                }
            ]
        });
        Assert.True(blocked.Succeeded);
        var postBlocked = await _engine.PostAsync(blocked.Document!.Id, "admin");
        Assert.False(postBlocked.Succeeded);
        Assert.Equal(InventoryErrorCodes.PeriodClosed, postBlocked.ErrorCode);

        // Idempotent re-close
        var reclose = await _periods.ClosePeriodAsync(july.Period.Id, "admin");
        Assert.True(reclose.Succeeded);
        Assert.Single(reclose.Snapshots);
    }

    private async Task PostGrn(DateTime date, decimal qty, decimal cost)
    {
        var doc = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = date,
            WarehouseId = _whId,
            Lines =
            [
                new CreateDocumentLineDto
                {
                    ItemVariantId = _variantId, UOMId = _uomId, Qty = qty, UnitCost = cost, LocationId = _locId
                }
            ]
        });
        Assert.True(doc.Succeeded, doc.ErrorMessage);
        var post = await _engine.PostAsync(doc.Document!.Id, "admin");
        Assert.True(post.Succeeded, post.ErrorMessage);
    }

    private async Task PostGi(DateTime date, decimal qty)
    {
        var doc = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GI,
            DocDate = date,
            WarehouseId = _whId,
            Lines =
            [
                new CreateDocumentLineDto
                {
                    ItemVariantId = _variantId, UOMId = _uomId, Qty = qty, UnitCost = 0, LocationId = _locId
                }
            ]
        });
        Assert.True(doc.Succeeded, doc.ErrorMessage);
        var post = await _engine.PostAsync(doc.Document!.Id, "admin");
        Assert.True(post.Succeeded, post.ErrorMessage);
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
