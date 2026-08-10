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

public class InventoryPhase3LotTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private ICompanyContext _company = null!;
    private IPostingEngine _engine = null!;
    private ILotReconciliationService _reconcile = null!;
    private long _uomId;
    private long _variantId;
    private long _whA;
    private long _whB;
    private long _locA;
    private long _locB;

    public InventoryPhase3LotTests()
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

        var today = DateTime.UtcNow.Date;
        db.InventoryPeriods.Add(new InventoryPeriod
        {
            CompanyId = companyId,
            FiscalYear = today.Year,
            FiscalMonth = today.Month,
            StartDate = new DateTime(today.Year, today.Month, 1),
            EndDate = new DateTime(today.Year, today.Month, 1).AddMonths(1).AddDays(-1),
            IsClosed = false,
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
            ItemCode = "BATCH01",
            ItemDescription = "Batch Item",
            BaseUOMId = _uomId,
            IsStockItem = true,
            IsBatchItem = true,
            IsActive = true
        });
        _variantId = (await itemService.GetVariantsAsync(item.Item!.Id)).Items.Single(v => v.IsDefault).Id;

        _whA = (await whService.AddAsync(new Warehouse { WarehouseCode = "WHA", WarehouseName = "A", IsActive = true })).Item!.Id;
        _whB = (await whService.AddAsync(new Warehouse { WarehouseCode = "WHB", WarehouseName = "B", IsActive = true })).Item!.Id;
        await using var db2 = await _factory.CreateDbContextAsync();
        _locA = (await db2.WarehouseLocations.SingleAsync(l => l.WarehouseId == _whA)).Id;
        _locB = (await db2.WarehouseLocations.SingleAsync(l => l.WarehouseId == _whB)).Id;

        _engine = new PostingEngine(_factory, _company, current, access.Object, NullLogger<PostingEngine>.Instance);
        _reconcile = new LotReconciliationService(_factory, _company);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private DateTime Today => DateTime.UtcNow.Date;

    [Fact]
    public async Task Multi_lot_receipt_issue_transfer_and_reconcile()
    {
        // Multi-lot RCV via two lines
        var grn = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = Today,
            WarehouseId = _whA,
            Lines =
            [
                new CreateDocumentLineDto
                {
                    ItemVariantId = _variantId, UOMId = _uomId, Qty = 60, UnitCost = 10,
                    LocationId = _locA, LotNo = "LOT-A"
                },
                new CreateDocumentLineDto
                {
                    ItemVariantId = _variantId, UOMId = _uomId, Qty = 40, UnitCost = 12,
                    LocationId = _locA, LotNo = "LOT-B"
                }
            ]
        });
        Assert.True(grn.Succeeded, grn.ErrorMessage);
        Assert.True((await _engine.PostAsync(grn.Document!.Id, "admin")).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(2, await db.Lots.CountAsync());
            Assert.Equal(100m, (await db.StockBalances.SingleAsync(b => b.WarehouseId == _whA)).QtyOnHand);
            Assert.Equal(100m, await db.LotBalances.SumAsync(b => b.QtyOnHand));
            Assert.Equal(2, await db.StockLedgers.CountAsync(l => l.DocumentId == grn.Document!.Id));
            Assert.All(await db.StockLedgers.Where(l => l.DocumentId == grn.Document!.Id).ToListAsync(),
                l => Assert.NotNull(l.LotId));
        }

        var lotA = await FindLotAsync("LOT-A");
        var lotB = await FindLotAsync("LOT-B");

        // Multi-lot ISSUE via LotAllocations on one line → many ledger rows
        var gi = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GI,
            DocDate = Today,
            WarehouseId = _whA,
            Lines =
            [
                new CreateDocumentLineDto
                {
                    ItemVariantId = _variantId,
                    UOMId = _uomId,
                    Qty = 50,
                    LocationId = _locA,
                    LotAllocations =
                    [
                        new LotAllocationInput { LotId = lotA, Qty = 30 },
                        new LotAllocationInput { LotId = lotB, Qty = 20 }
                    ]
                }
            ]
        });
        Assert.True(gi.Succeeded, gi.ErrorMessage);
        var giPost = await _engine.PostAsync(gi.Document!.Id, "admin");
        Assert.True(giPost.Succeeded, giPost.ErrorMessage);
        Assert.Equal(2, giPost.LedgerIds.Count);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(50m, (await db.StockBalances.SingleAsync(b => b.WarehouseId == _whA)).QtyOnHand);
            Assert.Equal(30m, (await db.LotBalances.SingleAsync(b => b.LotId == lotA)).QtyOnHand);
            Assert.Equal(20m, (await db.LotBalances.SingleAsync(b => b.LotId == lotB)).QtyOnHand);
            Assert.Equal(2, await db.StockMovementAllocations.CountAsync(a =>
                a.DocumentLineId == gi.Document!.Lines.Single().Id));
        }

        // Transfer preserves LotId
        var st = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.ST,
            DocDate = Today,
            SourceWarehouseId = _whA,
            DestinationWarehouseId = _whB,
            SourceLocationId = _locA,
            DestinationLocationId = _locB,
            Lines =
            [
                new CreateDocumentLineDto
                {
                    ItemVariantId = _variantId,
                    UOMId = _uomId,
                    Qty = 10,
                    LocationId = _locA,
                    LotId = lotA
                }
            ]
        });
        Assert.True(st.Succeeded, st.ErrorMessage);
        var stPost = await _engine.PostAsync(st.Document!.Id, "admin");
        Assert.True(stPost.Succeeded, stPost.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var stLedgers = await db.StockLedgers.Where(l => l.DocumentId == st.Document!.Id).OrderBy(l => l.Id).ToListAsync();
            Assert.Equal(2, stLedgers.Count);
            Assert.Equal(lotA, stLedgers[0].LotId);
            Assert.Equal(lotA, stLedgers[1].LotId); // same LotId preserved
            Assert.Equal(1, await db.Lots.CountAsync(l => l.LotNo == "LOT-A")); // no new Lot

            Assert.Equal(40m, (await db.StockBalances.SingleAsync(b => b.WarehouseId == _whA)).QtyOnHand);
            Assert.Equal(10m, (await db.StockBalances.SingleAsync(b => b.WarehouseId == _whB)).QtyOnHand);
            Assert.Equal(20m, (await db.LotBalances.SingleAsync(b => b.LotId == lotA && b.WarehouseId == _whA)).QtyOnHand);
            Assert.Equal(10m, (await db.LotBalances.SingleAsync(b => b.LotId == lotA && b.WarehouseId == _whB)).QtyOnHand);
        }

        var issues = await _reconcile.FindLotBalanceMismatchesAsync();
        Assert.Empty(issues);
    }

    [Fact]
    public async Task Non_batch_still_rejects_lot_fields()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var item = await db.Items.SingleAsync(i => i.ItemCode == "BATCH01");
        item.IsBatchItem = false;
        await db.SaveChangesAsync();

        var result = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = Today,
            WarehouseId = _whA,
            Lines =
            [
                new CreateDocumentLineDto
                {
                    ItemVariantId = _variantId, UOMId = _uomId, Qty = 1, UnitCost = 1,
                    LocationId = _locA, LotNo = "X"
                }
            ]
        });
        Assert.False(result.Succeeded);
        Assert.Equal(InventoryErrorCodes.LotNotAllowedInPhase, result.ErrorCode);
    }

    private async Task<long> FindLotAsync(string lotNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return (await db.Lots.SingleAsync(l => l.LotNo == lotNo)).Id;
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
