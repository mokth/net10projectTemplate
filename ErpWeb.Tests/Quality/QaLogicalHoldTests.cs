using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Production;
using ErpWeb.Core.Quality;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Quality;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Quality;

[Trait(TestCategories.Name, TestCategories.Quality)]
public class QaLogicalHoldTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly Mock<IInventoryTenantContext> _tenant = new();
    private readonly Mock<IAccessRightService> _access = new();
    private InventoryTenantScope _scope = Scope("tester");

    public QaLogicalHoldTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        BindTenant();
        _access
            .Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Approve_is_record_only_when_logical_hold_is_off()
    {
        var lotId = await SeedLotAsync(100m);
        var approved = await ApproveWipHoldAsync(lotId, holdQty: 20m, inspected: 100m);
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        Assert.Equal(0m, approved.Case!.HeldQty);
        Assert.DoesNotContain(approved.Case.Audit, x => x.EventType == QaAuditEvents.HoldPlaced);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.QaLogicalHolds.CountAsync());
        Assert.Equal(100m, await db.ProductionBalLots.Where(x => x.Uid == lotId).Select(x => x.BaseQty).SingleAsync());
    }

    [Fact]
    public async Task Approve_reserves_20_of_100_without_changing_lot_qty_or_cost()
    {
        await EnableHoldAsync();
        var lotId = await SeedLotAsync(100m, totalCost: 250m);
        var approved = await ApproveWipHoldAsync(lotId, holdQty: 20m, inspected: 100m);
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        Assert.Equal(20m, approved.Case!.HeldQty);
        Assert.Contains(approved.Case.Audit, x => x.EventType == QaAuditEvents.HoldPlaced);
        Assert.Contains("reserved", approved.Case.EligibilityNote, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("moved", approved.Case.EligibilityNote, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        var hold = Assert.Single(await db.QaLogicalHolds.ToListAsync());
        Assert.Equal(20m, hold.ReservedBaseQty);
        Assert.Equal(QaLogicalHoldStatuses.Active, hold.Status);
        var lot = await db.ProductionBalLots.SingleAsync(x => x.Uid == lotId);
        Assert.Equal(100m, lot.BaseQty);
        Assert.Equal(250m, lot.TotalCost);
        Assert.Equal("PROCESS_WIP", lot.BalanceStage);
        Assert.Equal("AVAILABLE", lot.StockStatusCode);
    }

    [Fact]
    public async Task Second_hold_cannot_over_reserve_the_same_lot()
    {
        await EnableHoldAsync();
        var lotId = await SeedLotAsync(100m);
        var first = await ApproveWipHoldAsync(lotId, holdQty: 60m, inspected: 60m);
        Assert.True(first.Succeeded, first.ErrorMessage);
        var second = await ApproveWipHoldAsync(lotId, holdQty: 60m, inspected: 60m);
        Assert.False(second.Succeeded);
        Assert.Contains("Only 40", second.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.QaLogicalHolds.CountAsync());
        Assert.Equal(100m, await db.ProductionBalLots.Where(x => x.Uid == lotId).Select(x => x.BaseQty).SingleAsync());
    }

    [Fact]
    public async Task Outflow_of_80_is_allowed_and_81_is_blocked_while_20_is_reserved()
    {
        await EnableHoldAsync();
        var lotId = await SeedLotAsync(100m);
        Assert.True((await ApproveWipHoldAsync(lotId, 20m, 100m)).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var holds = new QaLogicalHoldAuthorizationService();
        Assert.Null(await holds.AssertRemainingCoversHoldsAsync(db, "DEMO", "HQ", lotId, 20m));
        var blocked = await holds.AssertRemainingCoversHoldsAsync(db, "DEMO", "HQ", lotId, 19m);
        Assert.NotNull(blocked);
        Assert.Contains("Only 19", blocked, StringComparison.OrdinalIgnoreCase);

        var lot = await db.ProductionBalLots.SingleAsync(x => x.Uid == lotId);
        await using var tx = await db.Database.BeginTransactionAsync();
        var context = await CreatePostingContextAsync(db);
        var writer = new ProductionStockWriter(new StockMovementRegistry(), holds);
        await writer.ApplyAsync(context, [Consume(lot, 80m)], CancellationToken.None);
        Assert.Equal(20m, lot.BaseQty);

        var over = await Assert.ThrowsAsync<StockLedgerException>(
            () => writer.ApplyAsync(context, [Consume(lot, 1m)], CancellationToken.None));
        Assert.Equal(StockLedgerErrorCodes.InsufficientBaseQty, over.Error.Code);
        Assert.Contains("reservation", over.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(20m, lot.BaseQty);
    }

    [Fact]
    public async Task Writer_outflow_is_unchanged_when_the_hold_flag_is_off()
    {
        var lotId = await SeedLotAsync(100m);
        await using var db = await _factory.CreateDbContextAsync();
        var lot = await db.ProductionBalLots.SingleAsync(x => x.Uid == lotId);
        await using var tx = await db.Database.BeginTransactionAsync();
        var context = await CreatePostingContextAsync(db);
        var writer = new ProductionStockWriter(
            new StockMovementRegistry(), new QaLogicalHoldAuthorizationService());
        await writer.ApplyAsync(context, [Consume(lot, 100m)], CancellationToken.None);
        Assert.Equal(0m, lot.BaseQty);
    }

    [Fact]
    public async Task Release_clears_the_reservation_without_stock_movement()
    {
        await EnableHoldAsync();
        var lotId = await SeedLotAsync(100m);
        var approved = await ApproveWipHoldAsync(lotId, 20m, 100m);
        Assert.True(approved.Succeeded, approved.ErrorMessage);

        var released = await CreateCaseService().ReleaseHoldAsync(Review(approved.Case!));
        Assert.True(released.Succeeded, released.ErrorMessage);
        Assert.Equal(0m, released.Case!.HeldQty);
        Assert.Contains(released.Case.Audit, x => x.EventType == QaAuditEvents.HoldReleased);

        await using var db = await _factory.CreateDbContextAsync();
        var hold = Assert.Single(await db.QaLogicalHolds.ToListAsync());
        Assert.Equal(QaLogicalHoldStatuses.Released, hold.Status);
        Assert.Equal(100m, await db.ProductionBalLots.Where(x => x.Uid == lotId).Select(x => x.BaseQty).SingleAsync());
        Assert.Null(await new QaLogicalHoldAuthorizationService()
            .AssertRemainingCoversHoldsAsync(db, "DEMO", "HQ", lotId, 0m));
    }

    [Fact]
    public async Task Close_is_blocked_while_a_hold_is_active()
    {
        await EnableHoldAsync();
        var lotId = await SeedLotAsync(100m);
        var approved = await ApproveWipHoldAsync(lotId, 20m, 100m);
        var cases = CreateCaseService();
        var closed = await cases.CloseAsync(Review(approved.Case!));
        Assert.False(closed.Succeeded);
        Assert.Contains("Release the quality hold", closed.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var released = await cases.ReleaseHoldAsync(Review(approved.Case!));
        Assert.True(released.Succeeded, released.ErrorMessage);
        var afterRelease = await cases.CloseAsync(Review(released.Case!));
        Assert.True(afterRelease.Succeeded, afterRelease.ErrorMessage);
        Assert.Equal(QaCaseStatuses.Closed, afterRelease.Case!.Status);
    }

    private async Task EnableHoldAsync()
    {
        var result = await CreatePolicyService().SaveAsync(new QaPolicySaveCommand
        {
            InspectorApproverSeparation = true,
            LogicalHoldEnabled = true
        });
        Assert.True(result.Succeeded, result.ErrorMessage);
    }

    private async Task<long> SeedLotAsync(decimal qty, decimal totalCost = 0m)
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
            TotalCost = totalCost,
            AverageUnitCost = qty == 0m ? 0m : totalCost / qty,
            WorkOrderId = 1,
            WorkOrderNo = "WO-WIP",
            WarehouseCode = "MAIN",
            LocationCode = "SITE",
            LotNo = "WIP-1",
            BalanceStage = "PROCESS_WIP",
            ProductionLocationId = location.Id,
            StockStatusCode = "AVAILABLE",
            OriginType = "PRODUCED"
        };
        db.ProductionBalLots.Add(lot);
        await db.SaveChangesAsync();
        return lot.Uid;
    }

    private async Task<QaResult> ApproveWipHoldAsync(long lotId, decimal holdQty, decimal inspected)
    {
        _scope = Scope("tester");
        BindTenant();
        var cases = CreateCaseService();
        var saved = await cases.SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.ProductionWipLot,
            WorkOrderNo = "WO-WIP",
            ProductionBalLotId = lotId,
            Lines =
            [
                new QaCaseLineSaveCommand
                {
                    LineNo = 1,
                    ItemCode = "WIP1",
                    Uom = "EA",
                    Qty = inspected,
                    LotNo = "WIP-1"
                }
            ]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var submitted = await cases.SubmitAsync(Review(saved.Case!));
        var started = await cases.StartInspectionAsync(Review(submitted.Case!));
        var acceptQty = IvQty.Round(inspected - holdQty);
        var inspectedResult = await cases.SaveInspectionAsync(new QaInspectionSaveCommand
        {
            CaseNo = started.Case!.CaseNo,
            RowVersion = started.Case.RowVersion,
            Lines =
            [
                new QaInspectionLineCommand
                {
                    LineNo = 1,
                    Result = holdQty == inspected ? QaInspectionResults.Fail : QaInspectionResults.Conditional,
                    InspectedQty = inspected,
                    Dispositions = acceptQty > 0m
                        ?
                        [
                            new QaDispositionDto { Kind = QaDispositionKinds.Accept, Qty = acceptQty },
                            new QaDispositionDto { Kind = QaDispositionKinds.Hold, Qty = holdQty }
                        ]
                        : [new QaDispositionDto { Kind = QaDispositionKinds.Hold, Qty = holdQty }]
                }
            ]
        });
        Assert.True(inspectedResult.Succeeded, inspectedResult.ErrorMessage);
        var review = await cases.SubmitForReviewAsync(Review(inspectedResult.Case!));
        _scope = Scope("approver");
        BindTenant();
        return await cases.ApproveAsync(Review(review.Case!));
    }

    private static async Task<StockPostingContext> CreatePostingContextAsync(AppDbContext db)
    {
        var epoch = new StockLedgerEpoch
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            EffectiveFrom = new DateTime(2026, 10, 1),
            Version = 2,
            Status = StockLedgerEpochStatuses.Active,
            MigrationBatchId = Guid.NewGuid(),
            ReconciliationManifestHash = new string('A', 64)
        };
        db.StockLedgerEpochs.Add(epoch);
        await db.SaveChangesAsync();
        var posting = new StockPosting
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            LedgerEpochId = epoch.Id,
            PostingSequence = 1,
            RequestId = Guid.NewGuid(),
            CommandType = "TEST",
            RequestFingerprint = new string('C', 64),
            SourceModule = "PRODUCTION",
            SourceDocumentType = "TEST",
            SourceDocumentId = "HOLD-1",
            SourceDocumentNo = "HOLD-1",
            DocumentRevision = 1,
            PostingRole = "PRIMARY",
            SourceSnapshotJson = "{}",
            SourceSnapshotHash = new string('D', 64),
            EffectiveAt = new DateTime(2026, 10, 2),
            BusinessDate = new DateTime(2026, 10, 2),
            PeriodKey = "2026-10",
            PostedAtUtc = DateTime.UtcNow,
            PostedBy = "test"
        };
        db.StockPostings.Add(posting);
        await db.SaveChangesAsync();
        return new StockPostingContext(db, epoch, posting, "test");
    }

    private static ProductionStockLeg Consume(ProductionBalLot lot, decimal qty) =>
        new(lot, "CONSUME", qty, qty, 1, null, null, null, 1, "OUTPUT", "OUT-1", "LINE-1", 0);

    private IQaPolicyService CreatePolicyService() =>
        new QaPolicyService(_factory, _tenant.Object, _access.Object);

    private IQaCaseService CreateCaseService() =>
        new QaCaseService(
            _factory,
            _tenant.Object,
            _access.Object,
            new RunningNumberService(),
            new BranchStockTransactionLock(),
            new QaLogicalHoldAuthorizationService());

    private void BindTenant()
    {
        _tenant.Setup(x => x.TryCompanyScope()).Returns(_scope);
        _tenant.Setup(x => x.TryBranchScope()).Returns(_scope);
        _tenant.Setup(x => x.TryWriteScope()).Returns(_scope);
    }

    private static InventoryTenantScope Scope(string user) => new()
    {
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        UserId = user
    };

    private static QaReviewCommand Review(QaCaseDto value) => new()
    {
        CaseNo = value.CaseNo,
        RowVersion = value.RowVersion
    };
}
