using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Production.Inquiry;
[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionStockCardTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProductionStockCardTests()
    {
        _connection.Open();
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Card_computes_opening_period_in_out_and_closing()
    {
        await SeedLedgerAsync();
        var sut = CreateService();

        var result = await sut.GetCardAsync(new ProductionStockHistoryQuery
        {
            ItemCode = "RM-1",
            BaseUom = "EA",
            From = new DateTime(2026, 10, 5),
            To = new DateTime(2026, 10, 16),
        });

        Assert.True(result.Succeeded, result.Message);
        var card = result.Data!;
        Assert.Equal(ProductionStockCoverageCodes.V2, card.CoverageCode);
        Assert.Null(card.CoverageWarning);
        Assert.Equal(50m, card.OpeningQty);
        Assert.Equal(20m, card.PeriodInQty);
        Assert.Equal(30m, card.PeriodOutQty);
        Assert.Equal(40m, card.ClosingQty);
        Assert.Equal(5, card.Watermark);
        Assert.Equal(2, card.Rows.Count);
        Assert.Equal(70m, card.Rows[0].RunningQty);
        Assert.Equal(40m, card.Rows[1].RunningQty);
        Assert.Equal(new DateTime(2026, 10, 5), card.Rows[0].EffectiveAt);
        Assert.Equal(new DateTime(2026, 10, 10), card.Rows[1].EffectiveAt);
    }

    [Fact]
    public async Task As_of_before_reversal_keeps_the_original_receipt()
    {
        await SeedLedgerAsync();
        var sut = CreateService();

        var october = await sut.GetAsOfAsync(new ProductionStockHistoryQuery
        {
            ItemCode = "RM-1",
            BaseUom = "EA",
            AsOf = new DateTime(2026, 11, 1),
        });
        Assert.True(october.Succeeded, october.Message);
        Assert.Equal(50m, Assert.Single(october.Data!.Rows).BaseQty);

        var afterReversal = await sut.GetAsOfAsync(new ProductionStockHistoryQuery
        {
            ItemCode = "RM-1",
            BaseUom = "EA",
            AsOf = new DateTime(2026, 11, 6),
        });
        Assert.True(afterReversal.Succeeded, afterReversal.Message);
        Assert.Equal(40m, Assert.Single(afterReversal.Data!.Rows).BaseQty);
    }

    [Fact]
    public async Task No_active_epoch_returns_coverage_warning_and_empty_card()
    {
        var sut = CreateService();

        var result = await sut.GetCardAsync(new ProductionStockHistoryQuery
        {
            ItemCode = "RM-1",
            BaseUom = "EA",
            From = new DateTime(2026, 10, 1),
            To = new DateTime(2026, 11, 1),
        });

        Assert.True(result.Succeeded, result.Message);
        var card = result.Data!;
        Assert.Equal(ProductionStockCoverageCodes.NoActiveEpoch, card.CoverageCode);
        Assert.False(string.IsNullOrWhiteSpace(card.CoverageWarning));
        Assert.Equal(0m, card.OpeningQty);
        Assert.Equal(0m, card.ClosingQty);
        Assert.Empty(card.Rows);
        Assert.Equal(0, card.Watermark);
    }

    [Fact]
    public async Task Document_type_display_filter_does_not_change_opening()
    {
        await SeedLedgerAsync();
        var sut = CreateService();
        var query = new ProductionStockHistoryQuery
        {
            ItemCode = "RM-1",
            BaseUom = "EA",
            From = new DateTime(2026, 10, 5),
            To = new DateTime(2026, 10, 16),
        };

        var full = await sut.GetCardAsync(query);
        Assert.True(full.Succeeded, full.Message);
        query.SourceDocumentType = "OUTPUT";
        var filtered = await sut.GetCardAsync(query);

        Assert.True(filtered.Succeeded, filtered.Message);
        Assert.Equal(full.Data!.OpeningQty, filtered.Data!.OpeningQty);
        Assert.Equal(full.Data.ClosingQty, filtered.Data.ClosingQty);
        Assert.Equal(50m, filtered.Data.OpeningQty);
        Assert.Equal(40m, filtered.Data.ClosingQty);
        Assert.Single(filtered.Data.Rows);
        Assert.Equal("OUTPUT", filtered.Data.Rows[0].SourceDocumentType);
        Assert.Equal(40m, filtered.Data.Rows[0].RunningQty);
    }

    [Fact]
    public async Task History_before_cutover_returns_coverage_warning()
    {
        await SeedLedgerAsync();
        var sut = CreateService();

        var result = await sut.GetCardAsync(new ProductionStockHistoryQuery
        {
            ItemCode = "RM-1",
            BaseUom = "EA",
            From = new DateTime(2026, 9, 1),
            To = new DateTime(2026, 10, 16),
        });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(ProductionStockCoverageCodes.HistoryBeforeCutover, result.Data!.CoverageCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Data.CoverageWarning));
        Assert.Equal(0m, result.Data.OpeningQty);
        Assert.Equal(40m, result.Data.ClosingQty);
    }

    private ProductionStockHistoryService CreateService()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                MenuCodes.PlanningProductionBalance,
                PermissionCodes.Access,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var tenant = new Mock<IInventoryTenantContext>();
        tenant.Setup(x => x.TryBranchScope()).Returns(new InventoryTenantScope
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            LocationCode = "SITE",
            UserId = "tester",
        });
        return new ProductionStockHistoryService(_factory, tenant.Object, access.Object);
    }

    private async Task SeedLedgerAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");

        var epoch = new StockLedgerEpoch
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            EffectiveFrom = new DateTime(2026, 10, 1),
            Version = 2,
            Status = StockLedgerEpochStatuses.Active,
            MigrationBatchId = Guid.NewGuid(),
            ReconciliationManifestHash = new string('A', 64),
            ActivatedAtUtc = new DateTime(2026, 10, 1),
            ActivatedBy = "tester",
        };
        db.StockLedgerEpochs.Add(epoch);
        await db.SaveChangesAsync();

        var lot = new ProductionBalLot
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Kind = ProductionBalLotKinds.MaterialIn,
            ItemCode = "RM-1",
            Qty = 40m,
            Uom = "EA",
            BaseQty = 40m,
            BaseUom = "EA",
            ConversionFactorToBase = 1m,
            WorkOrderId = 1,
            WorkOrderNo = "WO-A",
            WarehouseCode = "WH1",
            LocationCode = "BIN1",
            LotNo = "LOT-1",
            BalanceStage = "MATERIAL",
            ProductionLocationId = 1,
            StockStatusCode = "AVAILABLE",
            PoolCode = "LOT-1",
            OriginType = "OPENING",
        };
        db.ProductionBalLots.Add(lot);
        await db.SaveChangesAsync();

        await AddMovementAsync(db, epoch.Id, lot.Uid, 1, new DateTime(2026, 10, 1),
            ProductionBalLotMovementTypes.OpeningIn, 50m, "OPENING", "OPEN-1");
        await AddMovementAsync(db, epoch.Id, lot.Uid, 2, new DateTime(2026, 10, 5),
            ProductionBalLotMovementTypes.Issue, 20m, "ISSUE", "IP-1");
        await AddMovementAsync(db, epoch.Id, lot.Uid, 3, new DateTime(2026, 10, 10),
            ProductionBalLotMovementTypes.Consume, 30m, "OUTPUT", "OUT-1");
        var laterIssue = await AddMovementAsync(db, epoch.Id, lot.Uid, 4, new DateTime(2026, 10, 20),
            ProductionBalLotMovementTypes.Issue, 10m, "ISSUE", "IP-2");
        await AddMovementAsync(db, epoch.Id, lot.Uid, 5, new DateTime(2026, 11, 5),
            ProductionBalLotMovementTypes.IssueReversal, 10m, "ISSUE", "IP-2R", laterIssue);
    }

    private static async Task<long> AddMovementAsync(
        AppDbContext db,
        long epochId,
        long lotId,
        long sequence,
        DateTime effectiveAt,
        string movementType,
        decimal qty,
        string documentType,
        string documentNo,
        long? originalMovementId = null)
    {
        var posting = new StockPosting
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            LedgerEpochId = epochId,
            PostingSequence = sequence,
            RequestId = Guid.NewGuid(),
            CommandType = documentType,
            RequestFingerprint = new string('B', 64),
            SourceModule = "PR",
            SourceDocumentType = documentType,
            SourceDocumentId = documentNo,
            SourceDocumentNo = documentNo,
            DocumentRevision = 1,
            PostingRole = "PRIMARY",
            SourceSnapshotJson = "{}",
            SourceSnapshotHash = new string('C', 64),
            SourceSnapshotSchemaVersion = 1,
            EffectiveAt = effectiveAt,
            BusinessDate = effectiveAt.Date,
            PeriodKey = effectiveAt.ToString("yyyy-MM"),
            PostedAtUtc = effectiveAt,
            PostedBy = "tester",
            SealedAtUtc = effectiveAt,
        };
        db.StockPostings.Add(posting);
        await db.SaveChangesAsync();

        var movement = new ProductionBalLotMovement
        {
            ProductionBalLotId = lotId,
            MovementType = movementType,
            Qty = qty,
            Uom = "EA",
            BaseQty = qty,
            BaseUom = "EA",
            WorkOrderId = 1,
            PostingLinkId = 1,
            OriginalMovementId = originalMovementId,
            LedgerVersion = 2,
            LedgerEpochId = epochId,
            StockPostingId = posting.Id,
            PostingLineNo = 1,
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ItemCode = "RM-1",
            BalanceStage = "MATERIAL",
            ProductionLocationCode = "LINE-1",
            LotIdentity = "LOT-1",
            StockStatusCode = "AVAILABLE",
            WorkOrderNo = "WO-A",
            ConversionFactorToBase = 1m,
            ValuationStatus = "UNVALUED",
            DocumentType = documentType,
            DocumentNo = documentNo,
            MovementDate = effectiveAt,
            CreatedDate = effectiveAt,
            CreatedBy = "tester",
        };
        db.ProductionBalLotMovements.Add(movement);
        await db.SaveChangesAsync();
        return movement.Uid;
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
