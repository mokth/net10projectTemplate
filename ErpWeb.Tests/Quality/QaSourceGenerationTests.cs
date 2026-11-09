using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Quality;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Quality;

[Trait(TestCategories.Name, TestCategories.Quality)]
public class QaSourceGenerationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly Mock<IInventoryTenantContext> _tenant = new();
    private readonly Mock<IAccessRightService> _access = new();

    public QaSourceGenerationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        var scope = new InventoryTenantScope { CompanyCode = "DEMO", BranchCode = "HQ", UserId = "tester" };
        _tenant.Setup(x => x.TryCompanyScope()).Returns(scope);
        _tenant.Setup(x => x.TryBranchScope()).Returns(scope);
        _tenant.Setup(x => x.TryWriteScope()).Returns(scope);
        _access
            .Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Incoming_unknown_po_is_rejected()
    {
        var result = await CreateCaseService().SaveDraftAsync(Incoming("MISSING", rel: 0, poLine: 1, qty: 1m));
        Assert.False(result.Succeeded);
        Assert.Equal(QaSourceMessages.PoNotFound, result.ErrorMessage);
    }

    [Fact]
    public async Task Incoming_injected_po_line_is_rejected()
    {
        await SeedPoAsync("PO-1", rel: 1, line: 1, item: "A100", qty: 10m);
        var result = await CreateCaseService().SaveDraftAsync(Incoming("PO-1", rel: 1, poLine: 9, qty: 1m));
        Assert.False(result.Succeeded);
        Assert.Equal(QaSourceMessages.Injected(1), result.ErrorMessage);
    }

    [Fact]
    public async Task Incoming_line_from_another_release_is_rejected()
    {
        await SeedPoAsync("PO-1", rel: 1, line: 1, item: "A100", qty: 10m);
        await SeedPoAsync("PO-1", rel: 2, line: 2, item: "A100", qty: 10m);
        var result = await CreateCaseService().SaveDraftAsync(Incoming("PO-1", rel: 1, poLine: 1, qty: 10m, extraLine: 2));
        Assert.False(result.Succeeded);
        Assert.Equal(QaSourceMessages.Injected(2), result.ErrorMessage);
    }

    [Fact]
    public async Task Incoming_qty_above_po_is_stale()
    {
        await SeedPoAsync("PO-1", rel: 1, line: 1, item: "A100", qty: 10m);
        var result = await CreateCaseService().SaveDraftAsync(Incoming("PO-1", rel: 1, poLine: 1, qty: 11m));
        Assert.False(result.Succeeded);
        Assert.Equal(QaSourceMessages.StaleQty(1), result.ErrorMessage);
    }

    [Fact]
    public async Task Incoming_duplicate_po_lines_are_rejected()
    {
        var result = await CreateCaseService().SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.IncomingPreGrn,
            PoNo = "PO-1",
            PoRelNo = 1,
            Lines =
            [
                new QaCaseLineSaveCommand { LineNo = 1, ItemCode = "A100", Uom = "EA", Qty = 1m, PoLineNo = 1 },
                new QaCaseLineSaveCommand { LineNo = 2, ItemCode = "A100", Uom = "EA", Qty = 1m, PoLineNo = 1 }
            ]
        });
        Assert.False(result.Succeeded);
        Assert.Equal(QaSourceMessages.DuplicatePoLine, result.ErrorMessage);
    }

    [Fact]
    public async Task Forged_fg_lot_is_rejected()
    {
        var result = await CreateCaseService().SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.FgStagingLot,
            WorkOrderNo = "WO-FG",
            ProductionBalLotId = 44,
            Lines =
            [
                new QaCaseLineSaveCommand { LineNo = 1, ItemCode = "FG1", Uom = "EA", Qty = 10m, LotNo = "PROD-1" }
            ]
        });
        Assert.False(result.Succeeded);
        Assert.Equal(QaSourceMessages.LotNotFound, result.ErrorMessage);
    }

    [Fact]
    public async Task Mixed_lots_on_a_wip_case_are_rejected()
    {
        var lotId = await SeedLotAsync("PROCESS_WIP", "WIP-1", 100m);
        var result = await CreateCaseService().SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.ProductionWipLot,
            WorkOrderNo = "WO-WIP",
            ProductionBalLotId = lotId,
            Lines =
            [
                new QaCaseLineSaveCommand { LineNo = 1, ItemCode = "WIP1", Uom = "EA", Qty = 10m, LotNo = "WIP-1" },
                new QaCaseLineSaveCommand { LineNo = 2, ItemCode = "WIP1", Uom = "EA", Qty = 10m, LotNo = "WIP-2" }
            ]
        });
        Assert.False(result.Succeeded);
        Assert.Equal(QaSourceMessages.MixedLot, result.ErrorMessage);
    }

    [Fact]
    public async Task Stale_inspection_qty_is_rejected_when_the_lot_is_smaller()
    {
        var lotId = await SeedLotAsync("PROCESS_WIP", "WIP-1", 10m);
        var cases = CreateCaseService();
        var saved = await cases.SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.ProductionWipLot,
            WorkOrderNo = "WO-WIP",
            ProductionBalLotId = lotId,
            Lines =
            [
                new QaCaseLineSaveCommand { LineNo = 1, ItemCode = "WIP1", Uom = "EA", Qty = 10m, LotNo = "WIP-1" }
            ]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        await ShrinkLotAsync(lotId, 4m);
        var submitted = await cases.SubmitAsync(Review(saved.Case!));
        var started = await cases.StartInspectionAsync(Review(submitted.Case!));
        var inspected = await cases.SaveInspectionAsync(new QaInspectionSaveCommand
        {
            CaseNo = started.Case!.CaseNo,
            RowVersion = started.Case.RowVersion,
            Lines =
            [
                new QaInspectionLineCommand
                {
                    LineNo = 1,
                    Result = QaInspectionResults.Pass,
                    InspectedQty = 10m,
                    Dispositions = [new QaDispositionDto { Kind = QaDispositionKinds.Accept, Qty = 10m }]
                }
            ]
        });
        Assert.False(inspected.Succeeded);
        Assert.Equal(QaSourceMessages.StaleQty(1), inspected.ErrorMessage);
    }

    [Fact]
    public async Task Forged_daily_production_id_is_rejected()
    {
        var result = await CreateCaseService().SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.ProductionOutputNonGood,
            SourceHeaderId = 99,
            Lines =
            [
                new QaCaseLineSaveCommand { LineNo = 1, ItemCode = "FG1", Uom = "PCS", Qty = 4m }
            ]
        });
        Assert.False(result.Succeeded);
        Assert.Equal(QaSourceMessages.OutputNotFound, result.ErrorMessage);
    }

    [Fact]
    public async Task Incoming_matching_po_stays_record_only()
    {
        await SeedPoAsync("PO-1", rel: 1, line: 1, item: "A100", qty: 10m);
        var saved = await CreateCaseService().SaveDraftAsync(Incoming("PO-1", rel: 1, poLine: 1, qty: 10m));
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        Assert.NotNull(saved.Case!.DeliveryInstanceId);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.QaReceiptGrants.CountAsync());
    }

    private IQaCaseService CreateCaseService() =>
        new QaCaseService(_factory, _tenant.Object, _access.Object, new RunningNumberService());

    private static QaCaseSaveCommand Incoming(string poNo, short rel, short poLine, decimal qty, short? extraLine = null)
    {
        var lines = new List<QaCaseLineSaveCommand>
        {
            new() { LineNo = 1, ItemCode = "A100", Uom = "EA", Qty = qty, PoLineNo = poLine }
        };
        if (extraLine is short second)
        {
            lines.Add(new QaCaseLineSaveCommand
            {
                LineNo = 2,
                ItemCode = "A100",
                Uom = "EA",
                Qty = 1m,
                PoLineNo = second
            });
        }

        return new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.IncomingPreGrn,
            PoNo = poNo,
            PoRelNo = rel,
            Lines = lines
        };
    }

    private async Task SeedPoAsync(string poNo, short rel, short line, string item, decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.PoOrders.Add(new PoOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PoNo = poNo,
            PoRelNo = rel,
            Status = "OPEN",
            Details =
            [
                new PoOrderDetail
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    PoNo = poNo,
                    PoRelNo = rel,
                    Line = line,
                    ICode = item,
                    PoQty = qty,
                    PurchaseUom = "EA",
                    StdUom = "EA"
                }
            ]
        });
        await db.SaveChangesAsync();
    }

    private async Task<long> SeedLotAsync(string stage, string lotNo, decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        var location = new ProductionLocation
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Code = "WIP",
            Description = "WIP floor"
        };
        db.ProductionLocations.Add(location);
        await db.SaveChangesAsync();
        var lot = new ProductionBalLot
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Kind = ProductionBalLotKinds.Wip,
            ItemCode = "WIP1",
            Qty = qty,
            Uom = "EA",
            BaseQty = qty,
            BaseUom = "EA",
            ConversionFactorToBase = 1m,
            WorkOrderId = 1,
            WorkOrderNo = "WO-WIP",
            WarehouseCode = "MAIN",
            LocationCode = "SITE",
            LotNo = lotNo,
            BalanceStage = stage,
            ProductionLocationId = location.Id,
            StockStatusCode = "AVAILABLE",
            OriginType = "PRODUCED"
        };
        db.ProductionBalLots.Add(lot);
        await db.SaveChangesAsync();
        return lot.Uid;
    }

    private async Task ShrinkLotAsync(long lotId, decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var lot = await db.ProductionBalLots.SingleAsync(x => x.Uid == lotId);
        lot.Qty = qty;
        lot.BaseQty = qty;
        await db.SaveChangesAsync();
    }

    private static QaReviewCommand Review(QaCaseDto value) => new()
    {
        CaseNo = value.CaseNo,
        RowVersion = value.RowVersion
    };
}
