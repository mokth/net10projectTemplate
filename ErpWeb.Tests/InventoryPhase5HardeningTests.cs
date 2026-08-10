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

public class InventoryPhase5HardeningTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private ICompanyContext _company = null!;
    private Mock<IAccessRightService> _access = null!;
    private StubUser _user = null!;
    private IPostingEngine _engine = null!;
    private IInventoryReconciliationService _recon = null!;
    private IStockInquiryService _inquiry = null!;
    private IItemService _items = null!;
    private IReasonCodeService _reasons = null!;
    private long _uomId;
    private long _variantId;
    private long _itemId;
    private long _whA;
    private long _whB;
    private long _locA;
    private long _locB;
    private long _reasonId;
    private DateTime _today;

    public InventoryPhase5HardeningTests()
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
        _today = DateTime.UtcNow.Date;
        await using var db = await _factory.CreateDbContextAsync();
        db.Companies.Add(new Company
        {
            CompanyCode = "DEMO", CompanyName = "Demo", IsActive = true,
            CurrencyCode = "MYR", TimeZoneId = "Asia/Kuala_Lumpur"
        });
        await db.SaveChangesAsync();
        var companyId = db.Companies.Single().CompanyId;
        db.Branches.Add(new Branch
        {
            CompanyId = companyId, BranchCode = "HQ", BranchName = "HQ",
            IsActive = true, CreatedAtUtc = DateTime.UtcNow
        });
        db.InventoryPeriods.Add(new InventoryPeriod
        {
            CompanyId = companyId,
            FiscalYear = _today.Year,
            FiscalMonth = _today.Month,
            StartDate = new DateTime(_today.Year, _today.Month, 1),
            EndDate = new DateTime(_today.Year, _today.Month, 1).AddMonths(1).AddDays(-1),
            IsClosed = false,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        _user = new StubUser();
        _company = new CompanyContext(_user, _factory);
        await _company.ResolveAsync();
        _access = new Mock<IAccessRightService>();
        _access.Setup(a => a.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var uom = new UomService(_factory, _company, _user, _access.Object, NullLogger<UomService>.Instance);
        _items = new ItemService(_factory, _company, _user, _access.Object, NullLogger<ItemService>.Instance);
        var wh = new WarehouseService(_factory, _company, _user, _access.Object, NullLogger<WarehouseService>.Instance);
        _reasons = new ReasonCodeService(_factory, _company, _user, _access.Object);

        _uomId = (await uom.AddAsync(new UOM { UOMCode = "PCS", UOMName = "Piece", IsActive = true })).Item!.Id;
        await uom.AddAsync(new UOM { UOMCode = "BOX", UOMName = "Box", IsActive = true });
        var item = await _items.AddAsync(new Item
        {
            ItemCode = "P5", ItemDescription = "Hardening", BaseUOMId = _uomId,
            IsStockItem = true, IsActive = true
        });
        _itemId = item.Item!.Id;
        _variantId = (await _items.GetVariantsAsync(_itemId)).Items.Single(v => v.IsDefault).Id;
        _whA = (await wh.AddAsync(new Warehouse { WarehouseCode = "WHA", WarehouseName = "A", IsActive = true })).Item!.Id;
        _whB = (await wh.AddAsync(new Warehouse { WarehouseCode = "WHB", WarehouseName = "B", IsActive = true })).Item!.Id;
        await using var db2 = await _factory.CreateDbContextAsync();
        _locA = (await db2.WarehouseLocations.SingleAsync(l => l.WarehouseId == _whA)).Id;
        _locB = (await db2.WarehouseLocations.SingleAsync(l => l.WarehouseId == _whB)).Id;
        _reasonId = (await _reasons.AddAsync(new ReasonCode
        {
            ReasonCodeValue = "ADJ", ReasonName = "Adj", AppliesTo = "SA", IsActive = true
        })).Item!.Id;

        RebuildEngine();
    }

    private void RebuildEngine()
    {
        _engine = new PostingEngine(_factory, _company, _user, _access.Object, NullLogger<PostingEngine>.Instance);
        _recon = new InventoryReconciliationService(
            _factory, _company, _user, _access.Object, NullLogger<InventoryReconciliationService>.Instance);
        _inquiry = new StockInquiryService(_factory, _company, _access.Object, _user);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Full_suite_reconciliation_clean_after_mav_flow()
    {
        await Post(DocumentType.GRN, _whA, _locA, 100, 10);
        await Post(DocumentType.GRN, _whA, _locA, 100, 14);
        await Post(DocumentType.GI, _whA, _locA, 50, 0);
        var issues = await _recon.FindIssuesAsync();
        Assert.DoesNotContain(issues, i => i.Kind is StockIntegrityKind.BalanceVsLedger or StockIntegrityKind.MissingItemCost);
        Assert.DoesNotContain(issues, i =>
            i.Kind == StockIntegrityKind.ValueMismatch &&
            Math.Abs((i.Expected ?? 0) - (i.Actual ?? 0)) > 0.01m);
    }

    [Fact]
    public async Task Reversal_matrix_restores_qty_for_OB_GRN_GI_ST_SA()
    {
        // OB
        var ob = await CreatePost(DocumentType.OB, _whA, _locA, 20, 5);
        Assert.True((await _engine.ReverseAsync(ob, "admin")).Succeeded);
        Assert.Equal(0m, await QtyAsync(_whA));

        // GRN
        var grn = await CreatePost(DocumentType.GRN, _whA, _locA, 30, 8);
        Assert.True((await _engine.ReverseAsync(grn, "admin")).Succeeded);
        Assert.Equal(0m, await QtyAsync(_whA));

        // GI
        await Post(DocumentType.GRN, _whA, _locA, 40, 10);
        var gi = await CreatePost(DocumentType.GI, _whA, _locA, 15, 0);
        Assert.True((await _engine.ReverseAsync(gi, "admin")).Succeeded);
        Assert.Equal(40m, await QtyAsync(_whA));

        // ST
        var st = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.ST,
            DocDate = _today,
            SourceWarehouseId = _whA,
            DestinationWarehouseId = _whB,
            SourceLocationId = _locA,
            DestinationLocationId = _locB,
            Lines = [Line(10, 0, _locA)]
        });
        Assert.True((await _engine.PostAsync(st.Document!.Id, "admin")).Succeeded);
        Assert.True((await _engine.ReverseAsync(st.Document.Id, "admin")).Succeeded);
        Assert.Equal(40m, await QtyAsync(_whA));
        Assert.Equal(0m, await QtyAsync(_whB));

        // SA decrease then reverse
        var sa = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.SA,
            DocDate = _today,
            WarehouseId = _whA,
            Lines =
            [
                new CreateDocumentLineDto
                {
                    ItemVariantId = _variantId, UOMId = _uomId, Qty = 5, UnitCost = 0,
                    LocationId = _locA, Direction = AdjustmentDirection.Decrease, ReasonCodeId = _reasonId
                }
            ]
        });
        Assert.True((await _engine.PostAsync(sa.Document!.Id, "admin")).Succeeded);
        Assert.True((await _engine.ReverseAsync(sa.Document.Id, "admin")).Succeeded);
        Assert.Equal(40m, await QtyAsync(_whA));
    }

    [Fact]
    public async Task UOM_conversion_history_frozen_on_ledger()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var box = await db.UOMs.SingleAsync(u => u.UOMCode == "BOX");
        var conv = await _items.AddConversionAsync(new UOMConversion
        {
            ItemId = _itemId,
            FromUOMId = box.Id,
            ToUOMId = _uomId,
            ConversionRate = 10m
        });
        Assert.True(conv.Succeeded, conv.ErrorMessage);

        var grn = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = _today,
            WarehouseId = _whA,
            Lines =
            [
                new CreateDocumentLineDto
                {
                    ItemVariantId = _variantId, UOMId = box.Id, Qty = 2, UnitCost = 5, LocationId = _locA
                }
            ]
        });
        Assert.True((await _engine.PostAsync(grn.Document!.Id, "admin")).Succeeded);

        await using (var db2 = await _factory.CreateDbContextAsync())
        {
            var ledger = await db2.StockLedgers.SingleAsync(l => l.DocumentId == grn.Document!.Id);
            Assert.Equal(10m, ledger.ConversionRateUsed);
            Assert.Equal(20m, ledger.QtyInBase);
            var row = await db2.UOMConversions.SingleAsync(c => c.Id == conv.Item!.Id);
            row.ConversionRate = 12m;
            await db2.SaveChangesAsync();
        }

        await using (var db3 = await _factory.CreateDbContextAsync())
        {
            var ledger = await db3.StockLedgers.SingleAsync(l => l.DocumentId == grn.Document!.Id);
            Assert.Equal(10m, ledger.ConversionRateUsed);
            Assert.Equal(20m, ledger.QtyInBase);
            Assert.Equal(20m, (await db3.StockBalances.SingleAsync(b => b.WarehouseId == _whA)).QtyOnHand);
        }
    }

    [Fact]
    public async Task VIEW_COST_masks_document_and_inquiry_costs()
    {
        await Post(DocumentType.GRN, _whA, _locA, 10, 7.5m);
        var docId = await CreatePost(DocumentType.GRN, _whA, _locA, 5, 9m);

        _user.UserLevel = "USER";
        _access.Setup(a => a.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string menu, string perm, CancellationToken _) =>
                !string.Equals(perm, PermissionCodes.ViewCost, StringComparison.OrdinalIgnoreCase));
        RebuildEngine();

        var dto = await _engine.GetDocumentAsync(docId);
        Assert.NotNull(dto);
        Assert.All(dto!.Lines, l => Assert.Null(l.UnitCost));
        Assert.All(dto.Lines, l => Assert.Null(l.TotalCost));

        var balances = await _inquiry.GetBalancesAsync();
        Assert.All(balances, b => Assert.Null(b.AverageCost));
        Assert.All(balances, b => Assert.Null(b.Value));

        var card = await _inquiry.GetStockCardAsync(_variantId, _whA);
        Assert.All(card, c => Assert.Null(c.UnitCost));
    }

    [Fact]
    public async Task Concurrency_stress_then_integrity_clean()
    {
        await Post(DocumentType.OB, _whA, _locA, 500, 1);
        for (var i = 0; i < 25; i++)
        {
            await Post(DocumentType.GRN, _whA, _locA, 2, 1 + (i % 3));
            await Post(DocumentType.GI, _whA, _locA, 1, 0);
        }

        var issues = await _recon.FindIssuesAsync();
        Assert.DoesNotContain(issues, i => i.Kind == StockIntegrityKind.BalanceVsLedger);
        Assert.DoesNotContain(issues, i => i.Kind == StockIntegrityKind.MissingItemCost);
        Assert.True(await QtyAsync(_whA) > 0);
    }

    [Fact]
    public async Task Rebuild_from_ledger_repairs_corrupted_balance()
    {
        await Post(DocumentType.GRN, _whA, _locA, 25, 4);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var bal = await db.StockBalances.SingleAsync(b => b.WarehouseId == _whA);
            bal.QtyOnHand = 999;
            await db.SaveChangesAsync();
        }

        var before = await _recon.FindIssuesAsync();
        Assert.Contains(before, i => i.Kind == StockIntegrityKind.BalanceVsLedger);

        var rebuild = await _recon.RebuildOperationalBalancesAsync();
        Assert.True(rebuild.Succeeded, rebuild.ErrorMessage);
        Assert.Equal(25m, await QtyAsync(_whA));

        var after = await _recon.FindIssuesAsync();
        Assert.DoesNotContain(after, i => i.Kind == StockIntegrityKind.BalanceVsLedger);
    }

    private CreateDocumentLineDto Line(decimal qty, decimal cost, long loc) => new()
    {
        ItemVariantId = _variantId, UOMId = _uomId, Qty = qty, UnitCost = cost, LocationId = loc
    };

    private async Task Post(DocumentType type, long wh, long loc, decimal qty, decimal cost)
    {
        var id = await CreatePost(type, wh, loc, qty, cost);
        Assert.True(id > 0);
    }

    private async Task<long> CreatePost(DocumentType type, long wh, long loc, decimal qty, decimal cost)
    {
        var created = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = type,
            DocDate = _today,
            WarehouseId = wh,
            Lines = [Line(qty, cost, loc)]
        });
        Assert.True(created.Succeeded, created.ErrorMessage);
        var post = await _engine.PostAsync(created.Document!.Id, "admin");
        Assert.True(post.Succeeded, post.ErrorMessage);
        return created.Document.Id;
    }

    private async Task<decimal> QtyAsync(long wh)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var bal = await db.StockBalances.FirstOrDefaultAsync(b => b.WarehouseId == wh && b.ItemVariantId == _variantId);
        return bal?.QtyOnHand ?? 0m;
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
        public string? UserLevel { get; set; } = "SYSTEM_ADMIN";
        public bool MustChangePassword => false;
        public string? SubjectUid => "1";
        public bool IsInRole(string role) => true;
    }
}
