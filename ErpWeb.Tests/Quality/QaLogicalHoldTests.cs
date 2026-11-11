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
    public async Task Approve_places_logical_hold_without_mutating_wip()
    {
        await EnableHoldAsync();
        var lotId = await SeedLotAsync(100m, totalCost: 250m);
        var approved = await ApproveWipHoldAsync(lotId, holdQty: 20m, inspected: 100m);
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        Assert.Equal(20m, approved.Case!.HeldQty);

        await using var db = await _factory.CreateDbContextAsync();
        var hold = Assert.Single(await db.QaLogicalHolds.ToListAsync());
        Assert.Equal(lotId, hold.ProductionBalLotId);
        Assert.Equal(20m, hold.ReservedBaseQty);
        var lot = await db.ProductionBalLots.SingleAsync(x => x.Uid == lotId);
        Assert.Equal(100m, lot.BaseQty);
        Assert.Equal(250m, lot.TotalCost);
    }

    [Fact]
    public async Task Approve_converts_logical_hold_to_pool_base_uom()
    {
        await EnableHoldAsync();
        var lotId = await SeedLotAsync(
            qty: 10m, uom: "BOX", baseUom: "EA", conversionFactorToBase: 10m);
        var approved = await ApproveWipHoldAsync(
            lotId, holdQty: 2m, inspected: 10m, uom: "BOX");
        Assert.True(approved.Succeeded, approved.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var hold = Assert.Single(await db.QaLogicalHolds.ToListAsync());
        Assert.Equal(20m, hold.ReservedBaseQty);
        Assert.Equal(100m, await db.ProductionBalLots
            .Where(x => x.Uid == lotId).Select(x => x.BaseQty).SingleAsync());
    }

    [Fact]
    public async Task Existing_hold_blocks_over_reservation_even_when_the_gate_is_off()
    {
        var lotId = await SeedLotAsync(100m);
        var first = await ApproveWipHoldAsync(lotId, holdQty: 60m, inspected: 60m);
        Assert.True(first.Succeeded, first.ErrorMessage);
        await SeedLogicalHoldAsync(first.Case!.CaseNo, lotId, 60m);

        await using var db = await _factory.CreateDbContextAsync();
        var covered = await new QaLogicalHoldAuthorizationService()
            .AssertRemainingCoversHoldsAsync(db, "DEMO", "HQ", lotId, 60m);
        Assert.Null(covered);
        var over = await new QaLogicalHoldAuthorizationService()
            .AssertRemainingCoversHoldsAsync(db, "DEMO", "HQ", lotId, 59m);
        Assert.NotNull(over);
        Assert.Contains("Only 59", over, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await db.QaLogicalHolds.CountAsync());
        Assert.Equal(100m, await db.ProductionBalLots.Where(x => x.Uid == lotId).Select(x => x.BaseQty).SingleAsync());
    }

    [Fact]
    public async Task Outflow_of_80_is_allowed_and_81_is_blocked_while_20_is_reserved()
    {
        var lotId = await SeedLotAsync(100m);
        var approved = await ApproveWipHoldAsync(lotId, 20m, 100m);
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        await SeedLogicalHoldAsync(approved.Case!.CaseNo, lotId, 20m);

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
        var lotId = await SeedLotAsync(100m);
        var approved = await ApproveWipHoldAsync(lotId, 20m, 100m);
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        await SeedLogicalHoldAsync(approved.Case!.CaseNo, lotId, 20m);
        var current = await CreateCaseService().GetAsync(approved.Case.CaseNo);
        Assert.True(current.Succeeded, current.ErrorMessage);

        var released = await CreateCaseService().ReleaseHoldAsync(Review(current.Case!));
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
        var lotId = await SeedLotAsync(100m);
        var approved = await ApproveWipHoldAsync(lotId, 20m, 100m);
        await SeedLogicalHoldAsync(approved.Case!.CaseNo, lotId, 20m);
        var cases = CreateCaseService();
        var current = await cases.GetAsync(approved.Case.CaseNo);
        var closed = await cases.CloseAsync(Review(current.Case!));
        Assert.False(closed.Succeeded);
        Assert.Contains("Release the quality hold", closed.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var released = await cases.ReleaseHoldAsync(Review(current.Case!));
        Assert.True(released.Succeeded, released.ErrorMessage);
        var completed = await CompleteOpenFollowUpsAsync(cases, released.Case!);
        var afterRelease = await cases.CloseAsync(Review(completed));
        Assert.True(afterRelease.Succeeded, afterRelease.ErrorMessage);
        Assert.Equal(QaCaseStatuses.Closed, afterRelease.Case!.Status);
    }

    private async Task EnableHoldAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.QaPolicies.Add(new QaPolicy
        {
            CompanyCode = "DEMO",
            Version = 1,
            Enabled = true,
            LogicalHoldEnabled = true,
            IsPublished = true,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "tester"
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedLogicalHoldAsync(string caseNo, long lotId, decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var entity = await db.QaCases.Include(x => x.Lines).SingleAsync(x => x.CaseNo == caseNo);
        var line = entity.Lines.OrderBy(x => x.LineNo).First();
        db.QaLogicalHolds.Add(new QaLogicalHold
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            CaseId = entity.Uid,
            CaseLineId = line.Uid,
            ProductionBalLotId = lotId,
            ReservedBaseQty = qty,
            Status = QaLogicalHoldStatuses.Active,
            Cause = QaLogicalHoldCauses.KeepHold,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<long> SeedLotAsync(
        decimal qty,
        decimal totalCost = 0m,
        string uom = "EA",
        string baseUom = "EA",
        decimal conversionFactorToBase = 1m)
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
            Uom = uom,
            BaseQty = IvQty.Round(qty * conversionFactorToBase),
            BaseUom = baseUom,
            ConversionFactorToBase = conversionFactorToBase,
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

    private async Task<QaResult> ApproveWipHoldAsync(
        long lotId, decimal holdQty, decimal inspected, string uom = "EA")
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
                    Uom = uom,
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

    private static async Task<QaCaseDto> CompleteOpenFollowUpsAsync(IQaCaseService cases, QaCaseDto value)
    {
        var current = value;
        foreach (var action in value.Actions.Where(x => x.IsOpen).ToList())
        {
            var done = await cases.CompleteActionAsync(new QaActionUpdateCommand
            {
                CaseNo = current.CaseNo,
                RowVersion = current.RowVersion,
                ActionNo = action.ActionNo,
                CompletionEvidence = "Hold released. Follow-up closed. No stock posting."
            });
            Assert.True(done.Succeeded, done.ErrorMessage);
            current = done.Case!;
        }

        return current;
    }
}
