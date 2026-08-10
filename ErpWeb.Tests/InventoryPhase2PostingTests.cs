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

public class InventoryPhase2PostingTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private ICompanyContext _company = null!;
    private IPostingEngine _engine = null!;
    private IStockTakeService _stockTake = null!;
    private long _uomId;
    private long _variantId;
    private long _whA;
    private long _whB;
    private long _locA;
    private long _locB;
    private long _reasonId;
    private int _companyId;
    private long _branchId;

    public InventoryPhase2PostingTests()
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
        _companyId = db.Companies.Single().CompanyId;
        var branch = new Branch
        {
            CompanyId = _companyId,
            BranchCode = "HQ",
            BranchName = "Head Office",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        _branchId = branch.Id;

        var today = DateTime.UtcNow.Date;
        db.InventoryPeriods.Add(new InventoryPeriod
        {
            CompanyId = _companyId,
            FiscalYear = today.Year,
            FiscalMonth = today.Month,
            StartDate = new DateTime(today.Year, today.Month, 1),
            EndDate = new DateTime(today.Year, today.Month, 1).AddMonths(1).AddDays(-1),
            IsClosed = false,
            CreatedAtUtc = DateTime.UtcNow
        });
        // also cover previous month for backdate tests
        var prev = today.AddMonths(-1);
        db.InventoryPeriods.Add(new InventoryPeriod
        {
            CompanyId = _companyId,
            FiscalYear = prev.Year,
            FiscalMonth = prev.Month,
            StartDate = new DateTime(prev.Year, prev.Month, 1),
            EndDate = new DateTime(prev.Year, prev.Month, 1).AddMonths(1).AddDays(-1),
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
        var reasonService = new ReasonCodeService(_factory, _company, current, access.Object);

        var uom = await uomService.AddAsync(new UOM { UOMCode = "PCS", UOMName = "Piece", DecimalPlaces = 0, IsActive = true });
        _uomId = uom.Item!.Id;
        var item = await itemService.AddAsync(new Item
        {
            ItemCode = "ITEM01",
            ItemDescription = "Test Item",
            BaseUOMId = _uomId,
            IsStockItem = true,
            IsActive = true
        });
        var variants = await itemService.GetVariantsAsync(item.Item!.Id);
        _variantId = variants.Items.Single(v => v.IsDefault).Id;

        var whA = await whService.AddAsync(new Warehouse { WarehouseCode = "WHA", WarehouseName = "WH A", IsActive = true });
        var whB = await whService.AddAsync(new Warehouse { WarehouseCode = "WHB", WarehouseName = "WH B", IsActive = true });
        _whA = whA.Item!.Id;
        _whB = whB.Item!.Id;

        await using var db2 = await _factory.CreateDbContextAsync();
        _locA = (await db2.WarehouseLocations.SingleAsync(l => l.WarehouseId == _whA)).Id;
        _locB = (await db2.WarehouseLocations.SingleAsync(l => l.WarehouseId == _whB)).Id;

        var reason = await reasonService.AddAsync(new ReasonCode
        {
            ReasonCodeValue = "ADJ",
            ReasonName = "Adjustment",
            AppliesTo = "SA",
            IsActive = true
        });
        _reasonId = reason.Item!.Id;

        _engine = new PostingEngine(_factory, _company, current, access.Object, NullLogger<PostingEngine>.Instance);
        _stockTake = new StockTakeService(_factory, _company, current, access.Object, _engine, NullLogger<StockTakeService>.Instance);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private DateTime Today => DateTime.UtcNow.Date;

    private CreateDocumentLineDto Line(decimal qty, decimal unitCost, long? loc = null) => new()
    {
        ItemVariantId = _variantId,
        UOMId = _uomId,
        Qty = qty,
        UnitCost = unitCost,
        LocationId = loc ?? _locA
    };

    [Fact]
    public async Task MAV_receipt_issue_preserves_historical_ledger_cost()
    {
        var g1 = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(100, 10)]
        });
        Assert.True(g1.Succeeded, g1.ErrorMessage);
        Assert.True((await _engine.PostAsync(g1.Document!.Id, "admin")).Succeeded);

        var g2 = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(100, 14)]
        });
        Assert.True((await _engine.PostAsync(g2.Document!.Id, "admin")).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var cost = await db.ItemCosts.SingleAsync(c => c.WarehouseId == _whA && c.ItemVariantId == _variantId);
            Assert.Equal(12m, cost.AverageCost);
            var bal = await db.StockBalances.SingleAsync(b => b.WarehouseId == _whA && b.ItemVariantId == _variantId);
            Assert.Equal(200m, bal.QtyOnHand);
        }

        var gi = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GI,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(50, 0)]
        });
        var giPost = await _engine.PostAsync(gi.Document!.Id, "admin");
        Assert.True(giPost.Succeeded, giPost.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var giLedger = await db.StockLedgers.SingleAsync(l => l.DocumentId == gi.Document!.Id);
            Assert.Equal(50m, giLedger.QtyOutBase);
            Assert.Equal(12m, giLedger.UnitCost);
            Assert.Equal(600m, giLedger.Amount);

            var cost = await db.ItemCosts.SingleAsync(c => c.WarehouseId == _whA && c.ItemVariantId == _variantId);
            Assert.Equal(12m, cost.AverageCost);
            Assert.Equal(150m, (await db.StockBalances.SingleAsync(b => b.WarehouseId == _whA && b.ItemVariantId == _variantId)).QtyOnHand);
        }

        var g3 = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(50, 20)]
        });
        Assert.True((await _engine.PostAsync(g3.Document!.Id, "admin")).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var giLedger = await db.StockLedgers.SingleAsync(l => l.DocumentId == gi.Document!.Id);
            Assert.Equal(12m, giLedger.UnitCost); // historical unchanged
            Assert.Equal(600m, giLedger.Amount);
        }
    }

    [Fact]
    public async Task Transfer_preserves_source_cost_and_blends_destination_MAV()
    {
        var seed = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.OB,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(100, 10)]
        });
        Assert.True((await _engine.PostAsync(seed.Document!.Id, "admin")).Succeeded);

        var st = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.ST,
            DocDate = Today,
            SourceWarehouseId = _whA,
            DestinationWarehouseId = _whB,
            SourceLocationId = _locA,
            DestinationLocationId = _locB,
            Lines = [Line(40, 0, _locA)]
        });
        var post = await _engine.PostAsync(st.Document!.Id, "admin");
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var ledgers = await db.StockLedgers.Where(l => l.DocumentId == st.Document!.Id).OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(2, ledgers.Count);
        Assert.Equal(40m, ledgers[0].QtyOutBase);
        Assert.Equal(10m, ledgers[0].UnitCost);
        Assert.Equal(40m, ledgers[1].QtyInBase);
        Assert.Equal(10m, ledgers[1].UnitCost);
        Assert.Equal(2, await db.StockMovementAllocations.CountAsync(a => ledgers.Select(x => x.Id).Contains(a.StockLedgerId)));

        Assert.Equal(60m, (await db.StockBalances.SingleAsync(b => b.WarehouseId == _whA)).QtyOnHand);
        Assert.Equal(40m, (await db.StockBalances.SingleAsync(b => b.WarehouseId == _whB)).QtyOnHand);
        Assert.Equal(10m, (await db.ItemCosts.SingleAsync(c => c.WarehouseId == _whB)).AverageCost);
    }

    [Fact]
    public async Task Concurrent_issue_rejects_insufficient_stock()
    {
        var seed = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.OB,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(100, 10)]
        });
        Assert.True((await _engine.PostAsync(seed.Document!.Id, "admin")).Succeeded);

        var a = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GI,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(80, 0)]
        });
        var b = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GI,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(50, 0)]
        });

        var r1 = await _engine.PostAsync(a.Document!.Id, "admin");
        var r2 = await _engine.PostAsync(b.Document!.Id, "admin");
        Assert.True(r1.Succeeded ^ r2.Succeeded || (r1.Succeeded && !r2.Succeeded));
        Assert.Contains(new[] { r1, r2 }, r => !r.Succeeded && r.ErrorCode == InventoryErrorCodes.InsufficientStock);
    }

    [Fact]
    public async Task Duplicate_post_is_idempotent()
    {
        var g = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(10, 5)]
        });
        var p1 = await _engine.PostAsync(g.Document!.Id, "admin");
        var p2 = await _engine.PostAsync(g.Document!.Id, "admin");
        Assert.True(p1.Succeeded);
        Assert.True(p2.Succeeded);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.StockLedgers.CountAsync(l => l.DocumentId == g.Document!.Id));
    }

    [Fact]
    public async Task Reversal_nets_qty_to_zero_and_keeps_original_ledger()
    {
        var g = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(100, 8)]
        });
        Assert.True((await _engine.PostAsync(g.Document!.Id, "admin")).Succeeded);

        var rev = await _engine.ReverseAsync(g.Document!.Id, "admin");
        Assert.True(rev.Succeeded, rev.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var bal = await db.StockBalances.SingleAsync(b => b.WarehouseId == _whA && b.ItemVariantId == _variantId);
        Assert.Equal(0m, bal.QtyOnHand);
        Assert.Equal(2, await db.StockLedgers.CountAsync(l => l.ItemVariantId == _variantId));
        var original = await db.InventoryDocuments.SingleAsync(d => d.Id == g.Document!.Id);
        Assert.Equal(DocumentStatus.REVERSED, original.Status);
        Assert.True(await db.StockLedgers.AnyAsync(l => l.DocumentId == g.Document!.Id && l.QtyInBase == 100));
    }

    [Fact]
    public async Task Zero_and_negative_qty_rejected()
    {
        var z = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(0, 10)]
        });
        Assert.False(z.Succeeded);
        Assert.Equal(InventoryErrorCodes.ZeroQtyNotAllowed, z.ErrorCode);

        var n = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(-5, 10)]
        });
        Assert.False(n.Succeeded);
        Assert.Equal(InventoryErrorCodes.ZeroQtyNotAllowed, n.ErrorCode);
    }

    [Fact]
    public async Task Zero_cost_GRN_requires_AllowZeroCost()
    {
        var denied = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = Today,
            WarehouseId = _whA,
            AllowZeroCost = false,
            Lines = [Line(10, 0)]
        });
        Assert.True(denied.Succeeded);
        var postDenied = await _engine.PostAsync(denied.Document!.Id, "admin");
        Assert.False(postDenied.Succeeded);
        Assert.Equal(InventoryErrorCodes.ZeroCostNotAllowed, postDenied.ErrorCode);

        var allowed = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = Today,
            WarehouseId = _whA,
            AllowZeroCost = true,
            Lines = [Line(10, 0)]
        });
        var postOk = await _engine.PostAsync(allowed.Document!.Id, "admin");
        Assert.True(postOk.Succeeded, postOk.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0m, (await db.ItemCosts.SingleAsync(c => c.WarehouseId == _whA)).AverageCost);
    }

    [Fact]
    public async Task Negative_stock_GI_rejected()
    {
        var seed = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.OB,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(10, 5)]
        });
        Assert.True((await _engine.PostAsync(seed.Document!.Id, "admin")).Succeeded);

        var gi = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GI,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(15, 0)]
        });
        var post = await _engine.PostAsync(gi.Document!.Id, "admin");
        Assert.False(post.Succeeded);
        Assert.Equal(InventoryErrorCodes.InsufficientStock, post.ErrorCode);
    }

    [Fact]
    public async Task Backdated_post_blocked_when_later_txn_exists()
    {
        var later = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(10, 5)]
        });
        Assert.True((await _engine.PostAsync(later.Document!.Id, "admin")).Succeeded);

        var earlier = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = Today.AddDays(-5),
            WarehouseId = _whA,
            Lines = [Line(10, 5)]
        });
        var post = await _engine.PostAsync(earlier.Document!.Id, "admin");
        Assert.False(post.Succeeded);
        Assert.Equal(InventoryErrorCodes.BackdatedPostingNotAllowed, post.ErrorCode);
    }

    [Fact]
    public async Task Cross_branch_ST_rejected()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var other = new Branch
        {
            CompanyId = _companyId,
            BranchCode = "BR2",
            BranchName = "Branch 2",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.Branches.Add(other);
        await db.SaveChangesAsync();
        db.Warehouses.Add(new Warehouse
        {
            CompanyId = _companyId,
            BranchId = other.Id,
            WarehouseCode = "WHX",
            WarehouseName = "Other",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var otherWh = await db.Warehouses.SingleAsync(w => w.WarehouseCode == "WHX");
        db.WarehouseLocations.Add(new WarehouseLocation
        {
            CompanyId = _companyId,
            BranchId = other.Id,
            WarehouseId = otherWh.Id,
            LocationCode = "MAIN",
            LocationName = "Main",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var otherLoc = await db.WarehouseLocations.SingleAsync(l => l.WarehouseId == otherWh.Id);

        var st = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.ST,
            DocDate = Today,
            SourceWarehouseId = _whA,
            DestinationWarehouseId = otherWh.Id,
            SourceLocationId = _locA,
            DestinationLocationId = otherLoc.Id,
            Lines = [Line(1, 0)]
        });
        Assert.False(st.Succeeded);
        Assert.Equal(InventoryErrorCodes.CrossBranchTransferNotAllowed, st.ErrorCode);
    }

    [Fact]
    public async Task Stock_take_generates_SA_and_posts_variance()
    {
        var seed = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.OB,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(100, 10)]
        });
        Assert.True((await _engine.PostAsync(seed.Document!.Id, "admin")).Succeeded);

        var create = await _stockTake.CreateAsync(Today, _whA, [
            new StockTakeLineInput
            {
                ItemVariantId = _variantId,
                LocationId = _locA,
                SystemQty = 100,
                CountedQty = 92,
                ReasonCodeId = _reasonId
            }
        ]);
        Assert.True(create.Succeeded, create.ErrorMessage);

        Assert.True((await _stockTake.StartCountingAsync(create.Document!.Id)).Succeeded);
        Assert.True((await _stockTake.CompleteCountingAsync(create.Document!.Id, [
            new StockTakeLineInput { ItemVariantId = _variantId, LocationId = _locA, CountedQty = 92, ReasonCodeId = _reasonId }
        ])).Succeeded);
        Assert.True((await _stockTake.SubmitForApprovalAsync(create.Document!.Id)).Succeeded);
        Assert.True((await _stockTake.ApproveAsync(create.Document!.Id, "admin")).Succeeded);

        var gen = await _stockTake.GenerateAdjustmentAsync(create.Document!.Id);
        Assert.True(gen.Succeeded, gen.ErrorMessage);
        Assert.Equal(DocumentType.SA, gen.Document!.DocType);

        var take = await _stockTake.GetAsync(create.Document!.Id);
        Assert.Equal(StockTakeStatus.ADJUSTMENT_GENERATED, take!.Status);
        Assert.All(take.Lines, l => Assert.Equal(92m, l.CountedQty)); // frozen values

        var post = await _stockTake.PostGeneratedAdjustmentAsync(create.Document!.Id, "admin");
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(92m, (await db.StockBalances.SingleAsync(b => b.WarehouseId == _whA)).QtyOnHand);
        var saLedgers = await db.StockLedgers.Where(l => l.DocumentId == gen.Document!.Id).ToListAsync();
        Assert.Single(saLedgers);
        Assert.Equal(8m, saLedgers[0].QtyOutBase);

        // lines immutable after approve — approve again should fail status
        var again = await _stockTake.CompleteCountingAsync(create.Document!.Id, [
            new StockTakeLineInput { ItemVariantId = _variantId, LocationId = _locA, CountedQty = 50 }
        ]);
        Assert.False(again.Succeeded);
        Assert.Equal(InventoryErrorCodes.StockTakeNotEditable, again.ErrorCode);
    }

    [Fact]
    public async Task Non_batch_rejects_lot_fields_batch_requires_lot()
    {
        var lotOnNonBatch = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [new CreateDocumentLineDto
            {
                ItemVariantId = _variantId,
                UOMId = _uomId,
                Qty = 1,
                UnitCost = 1,
                LocationId = _locA,
                LotNo = "L1"
            }]
        });
        Assert.False(lotOnNonBatch.Succeeded);
        Assert.Equal(InventoryErrorCodes.LotNotAllowedInPhase, lotOnNonBatch.ErrorCode);

        await using var db = await _factory.CreateDbContextAsync();
        var item = await db.Items.SingleAsync(i => i.ItemCode == "ITEM01");
        item.IsBatchItem = true;
        await db.SaveChangesAsync();

        var missingLot = await _engine.CreateDocumentAsync(new CreateDocumentDto
        {
            DocType = DocumentType.GRN,
            DocDate = Today,
            WarehouseId = _whA,
            Lines = [Line(1, 1)]
        });
        Assert.False(missingLot.Succeeded);

        item.IsBatchItem = false;
        await db.SaveChangesAsync();
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
