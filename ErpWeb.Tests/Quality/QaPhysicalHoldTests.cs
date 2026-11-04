using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Quality;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Quality;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Quality;

[Trait(TestCategories.Name, TestCategories.Quality)]
public class QaPhysicalHoldTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly Mock<IInventoryTenantContext> _tenant = new();
    private readonly Mock<IAccessRightService> _access = new();
    private InventoryTenantScope _scope = Scope("tester");

    public QaPhysicalHoldTests()
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
    public async Task Approve_is_record_only_when_physical_hold_is_off()
    {
        var pileId = await SeedPileAsync(100m);
        var approved = await ApproveStockHoldAsync(pileId, holdQty: 20m, inspected: 100m);
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        Assert.Equal(0m, approved.Case!.PhysicalHeldQty);
        Assert.DoesNotContain(approved.Case.Audit, x => x.EventType == QaAuditEvents.HoldPlaced);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.QaPhysicalHolds.CountAsync());
        var pile = await db.IvBalLocs.SingleAsync(x => x.Id == pileId);
        Assert.Equal(100m, pile.StdQty);
        Assert.Equal(IvItemStatuses.Active, pile.IStatus);
    }

    [Fact]
    public async Task Approve_places_physical_hold_without_mutating_stock()
    {
        await EnableHoldAsync();
        var pileId = await SeedPileAsync(100m, cost: 250m);
        var approved = await ApproveStockHoldAsync(pileId, holdQty: 20m, inspected: 100m);
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        Assert.Equal(20m, approved.Case!.PhysicalHeldQty);

        await using var db = await _factory.CreateDbContextAsync();
        var hold = Assert.Single(await db.QaPhysicalHolds.ToListAsync());
        Assert.Equal(pileId, hold.BalLocId);
        Assert.Equal(20m, hold.ReservedBaseQty);
        var pile = await db.IvBalLocs.SingleAsync(x => x.Id == pileId);
        Assert.Equal(100m, pile.StdQty);
        Assert.Equal(250m, pile.Cost);
        Assert.Equal(IvItemStatuses.Active, pile.IStatus);
    }

    [Fact]
    public async Task Same_pile_lines_are_aggregated_before_hold_placement()
    {
        await EnableHoldAsync();
        var pileId = await SeedPileAsync(100m);
        var approved = await ApproveStockHoldLinesAsync(pileId, 60m, 50m);
        Assert.False(approved.Succeeded);
        Assert.Contains("Only 100", approved.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.QaPhysicalHolds.CountAsync());
        Assert.Equal(100m, await db.IvBalLocs
            .Where(x => x.Id == pileId).Select(x => x.StdQty).SingleAsync());
    }

    [Fact]
    public async Task Outflow_of_80_is_allowed_and_81_is_blocked_while_20_is_reserved()
    {
        var pileId = await SeedPileAsync(100m);
        var approved = await ApproveStockHoldAsync(pileId, 20m, 100m);
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        await SeedPhysicalHoldAsync(approved.Case!.CaseNo, pileId, 20m);

        await using var db = await _factory.CreateDbContextAsync();
        var holds = new QaPhysicalHoldAuthorizationService();
        Assert.Null(await holds.AssertAndClaimStockOutAsync(
            db, "DEMO", "HQ", IvTrxTypes.MiscellaneousIssue, 1,
            [Need(pileId, IvItemStatuses.Active, 100m, 80m, 11)]));
        var blocked = await holds.AssertAndClaimStockOutAsync(
            db, "DEMO", "HQ", IvTrxTypes.MiscellaneousIssue, 2,
            [Need(pileId, IvItemStatuses.Active, 100m, 81m, 12)]);
        Assert.NotNull(blocked);
        Assert.Contains("Only 19", blocked, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reservation", blocked, StringComparison.OrdinalIgnoreCase);

        var pile = await db.IvBalLocs.SingleAsync(x => x.Id == pileId);
        Assert.Equal(100m, pile.StdQty);
        Assert.Equal(IvItemStatuses.Active, pile.IStatus);
    }

    [Fact]
    public async Task Only_posted_allocations_reduce_outstanding_hold()
    {
        var pileId = await SeedPileAsync(100m);
        var approved = await ApproveStockHoldAsync(pileId, 20m, 100m);
        await SeedPhysicalHoldAsync(approved.Case!.CaseNo, pileId, 20m);

        await using var db = await _factory.CreateDbContextAsync();
        var hold = await db.QaPhysicalHolds.SingleAsync();
        var allocation = new QaPhysicalAllocation
        {
            HoldId = hold.Uid,
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Qty = 5m,
            Status = QaReceiptAllocationStatuses.Posted,
            TrxType = IvTrxTypes.Scrap,
            BatchNo = 15,
            BatchDetailId = 151,
            PostedAtUtc = DateTime.UtcNow
        };
        db.QaPhysicalAllocations.Add(allocation);
        await db.SaveChangesAsync();

        var holds = new QaPhysicalHoldAuthorizationService();
        Assert.Null(await holds.AssertAndClaimStockOutAsync(
            db, "DEMO", "HQ", IvTrxTypes.Scrap, 16,
            [Need(pileId, IvItemStatuses.Active, 100m, 85m, 152)]));
        Assert.NotNull(await holds.AssertAndClaimStockOutAsync(
            db, "DEMO", "HQ", IvTrxTypes.Scrap, 17,
            [Need(pileId, IvItemStatuses.Active, 100m, 86m, 153)]));

        allocation.Status = QaReceiptAllocationStatuses.Reversed;
        await db.SaveChangesAsync();
        Assert.NotNull(await holds.AssertAndClaimStockOutAsync(
            db, "DEMO", "HQ", IvTrxTypes.Scrap, 18,
            [Need(pileId, IvItemStatuses.Active, 100m, 81m, 154)]));
    }

    [Fact]
    public async Task Flag_off_is_a_noop_even_for_qchold()
    {
        var pileId = await SeedPileAsync(100m, status: IvItemStatuses.QcHold);
        await using var db = await _factory.CreateDbContextAsync();
        var error = await new QaPhysicalHoldAuthorizationService().AssertAndClaimStockOutAsync(
            db, "DEMO", "HQ", IvTrxTypes.MiscellaneousIssue, 3,
            [Need(pileId, IvItemStatuses.QcHold, 100m, 100m, 13)]);
        Assert.Null(error);
        Assert.Equal(0, await db.QaPhysicalAllocations.CountAsync());
    }

    [Fact]
    public async Task Qchold_miscellaneous_issue_is_blocked_when_the_flag_is_on()
    {
        await EnableHoldAsync();
        var pileId = await SeedPileAsync(100m, status: IvItemStatuses.QcHold);
        await using var db = await _factory.CreateDbContextAsync();
        var error = await new QaPhysicalHoldAuthorizationService().AssertAndClaimStockOutAsync(
            db, "DEMO", "HQ", IvTrxTypes.MiscellaneousIssue, 4,
            [Need(pileId, IvItemStatuses.QcHold, 100m, 10m, 14)]);
        Assert.NotNull(error);
        Assert.Contains("QCHOLD", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == pileId).Select(x => x.StdQty).SingleAsync());
    }

    [Fact]
    public async Task Scrap_without_lineage_uses_only_unreserved_stock_and_restore_reverses_seeded_allocation()
    {
        await EnableHoldAsync();
        var pileId = await SeedPileAsync(100m);
        await using var db = await _factory.CreateDbContextAsync();
        var holds = new QaPhysicalHoldAuthorizationService();
        var allowed = await holds.AssertAndClaimStockOutAsync(
            db, "DEMO", "HQ", IvTrxTypes.Scrap, 9,
            [Need(pileId, IvItemStatuses.Active, 100m, 20m, 21)]);
        Assert.Null(allowed);
        Assert.Equal(0, await db.QaPhysicalAllocations.CountAsync());

        var caseRow = new QaCase
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            CaseNo = "QCSEED01",
            CaseDate = DateTime.Today,
            Status = QaCaseStatuses.Approved,
            SourceType = QaSourceTypes.InventoryStock,
            EligibilityNote = QaEligibilityNotes.PostedStock,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "tester"
        };
        var line = new QaCaseLine { LineNo = 1, ItemCode = "A100", Uom = "EA", Qty = 20m };
        caseRow.Lines.Add(line);
        db.QaCases.Add(caseRow);
        await db.SaveChangesAsync();
        var hold = new QaPhysicalHold
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            CaseId = caseRow.Uid,
            CaseLineId = line.Uid,
            BalLocId = pileId,
            ItemCode = "A100",
            WarehouseCode = "MAIN",
            LocationCode = "BIN1",
            LotNo = "LOT-1",
            ItemStatus = IvItemStatuses.Active,
            ReservedBaseQty = 20m,
            Status = QaLogicalHoldStatuses.Active,
            Cause = QaLogicalHoldCauses.KeepHold,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.QaPhysicalHolds.Add(hold);
        await db.SaveChangesAsync();
        db.QaPhysicalAllocations.Add(new QaPhysicalAllocation
        {
            HoldId = hold.Uid,
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Qty = 20m,
            Status = QaReceiptAllocationStatuses.Posted,
            TrxType = IvTrxTypes.Scrap,
            BatchNo = 9,
            BatchDetailId = 21,
            PostedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        Assert.Null(await holds.RestoreStockOutAsync(db, "DEMO", "HQ", 9));
        await db.SaveChangesAsync();
        var allocation = Assert.Single(await db.QaPhysicalAllocations.ToListAsync());
        Assert.Equal(QaReceiptAllocationStatuses.Reversed, allocation.Status);
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == pileId).Select(x => x.StdQty).SingleAsync());
    }

    [Fact]
    public async Task Transfer_of_a_reserved_pile_is_blocked()
    {
        var pileId = await SeedPileAsync(100m);
        var approved = await ApproveStockHoldAsync(pileId, 20m, 100m);
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        await SeedPhysicalHoldAsync(approved.Case!.CaseNo, pileId, 20m);

        await using var db = await _factory.CreateDbContextAsync();
        var blocked = await new QaPhysicalHoldAuthorizationService().AssertAndClaimStockOutAsync(
            db, "DEMO", "HQ", IvTrxTypes.StockTransfer, 8,
            [Need(pileId, IvItemStatuses.Active, 100m, 20m, 31)]);
        Assert.NotNull(blocked);
        Assert.Contains("not a quality release", blocked, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Close_is_blocked_while_a_physical_hold_is_active()
    {
        var pileId = await SeedPileAsync(100m);
        var approved = await ApproveStockHoldAsync(pileId, 20m, 100m);
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        await SeedPhysicalHoldAsync(approved.Case!.CaseNo, pileId, 20m);
        var cases = CreateCaseService();
        var current = await cases.GetAsync(approved.Case.CaseNo);
        var closed = await cases.CloseAsync(Review(current.Case!));
        Assert.False(closed.Succeeded);
        Assert.Contains("Release the quality hold", closed.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var released = await cases.ReleaseHoldAsync(Review(current.Case!));
        Assert.True(released.Succeeded, released.ErrorMessage);
        Assert.Equal(0m, released.Case!.PhysicalHeldQty);
        var afterRelease = await cases.CloseAsync(Review(released.Case!));
        Assert.True(afterRelease.Succeeded, afterRelease.ErrorMessage);
        Assert.Equal(QaCaseStatuses.Closed, afterRelease.Case!.Status);

        await using var db = await _factory.CreateDbContextAsync();
        var pile = await db.IvBalLocs.SingleAsync(x => x.Id == pileId);
        Assert.Equal(100m, pile.StdQty);
        Assert.Equal(IvItemStatuses.Active, pile.IStatus);
    }

    private async Task EnableHoldAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.QaPolicies.Add(new QaPolicy
        {
            CompanyCode = "DEMO",
            Version = 1,
            Enabled = true,
            PhysicalHoldEnabled = true,
            IsPublished = true,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "tester"
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedPhysicalHoldAsync(string caseNo, int pileId, decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var entity = await db.QaCases.Include(x => x.Lines).SingleAsync(x => x.CaseNo == caseNo);
        var line = entity.Lines.OrderBy(x => x.LineNo).First();
        db.QaPhysicalHolds.Add(new QaPhysicalHold
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            CaseId = entity.Uid,
            CaseLineId = line.Uid,
            BalLocId = pileId,
            ItemCode = "A100",
            WarehouseCode = "MAIN",
            LocationCode = "BIN1",
            LotNo = "LOT-1",
            ItemStatus = IvItemStatuses.Active,
            ReservedBaseQty = qty,
            Status = QaLogicalHoldStatuses.Active,
            Cause = QaLogicalHoldCauses.KeepHold,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> SeedPileAsync(decimal qty, decimal cost = 0m, string status = IvItemStatuses.Active)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        var pile = new IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = "A100",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = "LOT-1",
            IStatus = status,
            StdQty = qty,
            StdUom = "EA",
            Cost = cost,
            UnitPrice = qty == 0m ? 0m : cost / qty,
            TransDate = DateTime.Today
        };
        db.IvBalLocs.Add(pile);
        await db.SaveChangesAsync();
        return pile.Id;
    }

    private async Task<QaResult> ApproveStockHoldAsync(int pileId, decimal holdQty, decimal inspected)
    {
        _scope = Scope("tester");
        BindTenant();
        var cases = CreateCaseService();
        var saved = await cases.SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.InventoryStock,
            SourceDocNo = pileId.ToString(),
            Lines =
            [
                new QaCaseLineSaveCommand
                {
                    LineNo = 1,
                    ItemCode = "A100",
                    Uom = "EA",
                    Qty = inspected,
                    LotNo = "LOT-1",
                    WarehouseCode = "MAIN",
                    LocationCode = "BIN1",
                    ItemStatus = IvItemStatuses.Active
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

    private async Task<QaResult> ApproveStockHoldLinesAsync(int pileId, params decimal[] holdQtys)
    {
        _scope = Scope("tester");
        BindTenant();
        var cases = CreateCaseService();
        var saved = await cases.SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.InventoryStock,
            SourceDocNo = pileId.ToString(),
            Lines = holdQtys.Select((qty, index) => new QaCaseLineSaveCommand
            {
                LineNo = index + 1,
                ItemCode = "A100",
                Uom = "EA",
                Qty = qty,
                LotNo = "LOT-1",
                WarehouseCode = "MAIN",
                LocationCode = "BIN1",
                ItemStatus = IvItemStatuses.Active
            }).ToArray()
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var submitted = await cases.SubmitAsync(Review(saved.Case!));
        var started = await cases.StartInspectionAsync(Review(submitted.Case!));
        var inspected = await cases.SaveInspectionAsync(new QaInspectionSaveCommand
        {
            CaseNo = started.Case!.CaseNo,
            RowVersion = started.Case.RowVersion,
            Lines = holdQtys.Select((qty, index) => new QaInspectionLineCommand
            {
                LineNo = index + 1,
                Result = QaInspectionResults.Fail,
                InspectedQty = qty,
                Dispositions = [new QaDispositionDto
                {
                    Kind = QaDispositionKinds.Hold,
                    Qty = qty
                }]
            }).ToArray()
        });
        Assert.True(inspected.Succeeded, inspected.ErrorMessage);
        var review = await cases.SubmitForReviewAsync(Review(inspected.Case!));
        _scope = Scope("approver");
        BindTenant();
        return await cases.ApproveAsync(Review(review.Case!));
    }

    private static QaStockOutNeed Need(int pileId, string status, decimal onHand, decimal qty, int detailId) =>
        new()
        {
            BalLocId = pileId,
            ItemStatus = status,
            OnHand = onHand,
            Qty = qty,
            BatchDetailId = detailId
        };

    private IQaPolicyService CreatePolicyService() =>
        new QaPolicyService(_factory, _tenant.Object, _access.Object);

    private IQaCaseService CreateCaseService() =>
        new QaCaseService(
            _factory,
            _tenant.Object,
            _access.Object,
            new RunningNumberService(),
            new BranchStockTransactionLock(),
            physicalHolds: new QaPhysicalHoldAuthorizationService());

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
