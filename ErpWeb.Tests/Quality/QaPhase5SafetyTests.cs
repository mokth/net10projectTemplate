using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Quality;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Quality;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Quality;

[Trait(TestCategories.Name, TestCategories.Quality)]
public class QaPhase5SafetyTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly Mock<IInventoryTenantContext> _tenant = new();
    private readonly Mock<IAccessRightService> _access = new();
    private InventoryTenantScope _scope = Scope("tester");

    public QaPhase5SafetyTests()
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
    public async Task Record_only_approve_and_follow_up_do_not_mutate_stock_cost_or_wip()
    {
        await SeedPileAsync(100m);
        var before = await SnapshotAsync();
        Assert.Equal(1, before.BalLocCount);
        Assert.Equal(100m, before.BalLocQty);

        var cases = CreateCaseService();
        var started = await StartIncomingAsync(cases, 10m);
        var inspected = await cases.SaveInspectionAsync(Inspect(started.Case!, 10m, accept: 6m, reject: 4m));
        var review = await cases.SubmitForReviewAsync(Review(inspected.Case!));
        _scope = Scope("approver");
        BindTenant();
        var approved = await cases.ApproveAsync(Review(review.Case!));
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        var action = Assert.Single(approved.Case!.Actions, x => x.ActionType == QaActionTypes.Reject);
        var startedAction = await cases.StartActionAsync(new QaActionUpdateCommand
        {
            CaseNo = approved.Case.CaseNo,
            RowVersion = approved.Case.RowVersion,
            ActionNo = action.ActionNo
        });
        Assert.True(startedAction.Succeeded, startedAction.ErrorMessage);
        var completed = await cases.CompleteActionAsync(new QaActionUpdateCommand
        {
            CaseNo = startedAction.Case!.CaseNo,
            RowVersion = startedAction.Case.RowVersion,
            ActionNo = action.ActionNo,
            CompletionEvidence = "Supplier credit requested"
        });
        Assert.True(completed.Succeeded, completed.ErrorMessage);
        var closed = await cases.CloseAsync(Review(completed.Case!));
        Assert.True(closed.Succeeded, closed.ErrorMessage);

        var after = await SnapshotAsync();
        Assert.Equal(before.BalLocCount, after.BalLocCount);
        Assert.Equal(before.BalLocQty, after.BalLocQty);
        Assert.Equal(before.HistoryCount, after.HistoryCount);
        Assert.Equal(before.BatchCount, after.BatchCount);
        Assert.Equal(before.CostStateCount, after.CostStateCount);
        Assert.Equal(before.WipLotCount, after.WipLotCount);
        Assert.Equal(before.WipMoveCount, after.WipMoveCount);
        Assert.Equal(before.CaseCount + 1, after.CaseCount);
        Assert.True(after.ActionCount >= 1);
        Assert.True(after.AttemptCount >= 1);
    }

    [Fact]
    public async Task Company_branch_case_no_unique_index_rejects_a_duplicate_row()
    {
        await using var first = await _factory.CreateDbContextAsync();
        first.QaCases.Add(MinimalCase("QC00000001"));
        await first.SaveChangesAsync();

        await using var second = await _factory.CreateDbContextAsync();
        second.QaCases.Add(MinimalCase("QC00000001"));
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    private IQaCaseService CreateCaseService() =>
        new QaCaseService(_factory, _tenant.Object, _access.Object, new RunningNumberService());

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

    private static QaInspectionSaveCommand Inspect(QaCaseDto value, decimal qty, decimal accept, decimal reject)
    {
        return new QaInspectionSaveCommand
        {
            CaseNo = value.CaseNo,
            RowVersion = value.RowVersion,
            Lines =
            [
                new QaInspectionLineCommand
                {
                    LineNo = 1,
                    Result = reject > 0m ? QaInspectionResults.Fail : QaInspectionResults.Pass,
                    InspectedQty = qty,
                    Dispositions =
                    [
                        new QaDispositionDto { Kind = QaDispositionKinds.Accept, Qty = accept },
                        new QaDispositionDto { Kind = QaDispositionKinds.Reject, Qty = reject }
                    ]
                }
            ]
        };
    }

    private async Task<QaResult> StartIncomingAsync(IQaCaseService cases, decimal qty)
    {
        var saved = await cases.SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.IncomingPreGrn,
            DeliveryNote = "DN-P5",
            Lines = [new QaCaseLineSaveCommand { LineNo = 1, ItemCode = "RM1", Uom = "PCS", Qty = qty }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var submitted = await cases.SubmitAsync(Review(saved.Case!));
        return await cases.StartInspectionAsync(Review(submitted.Case!));
    }

    private async Task SeedPileAsync(decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        db.IvBalLocs.Add(new IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = "A100",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = "LOT-P5",
            IStatus = IvItemStatuses.Active,
            StdQty = qty,
            StdUom = "EA",
            TransDate = DateTime.Today
        });
        await db.SaveChangesAsync();
    }

    private async Task<StockSnapshot> SnapshotAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return new StockSnapshot(
            await db.IvBalLocs.CountAsync(),
            await db.IvBalLocs.SumAsync(x => (decimal?)x.StdQty) ?? 0m,
            await db.IvTrxHistories.CountAsync(),
            await db.IvTrxBatches.CountAsync(),
            await db.StockCostStates.CountAsync(),
            await db.ProductionBalLots.CountAsync(),
            await db.ProductionBalLotMovements.CountAsync(),
            await db.QaCases.CountAsync(),
            await db.QaCorrectiveActions.CountAsync(),
            await db.QaInspectionAttempts.CountAsync());
    }

    private static QaCase MinimalCase(string caseNo) => new()
    {
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        CaseNo = caseNo,
        CaseDate = DateTime.Today,
        Status = QaCaseStatuses.Draft,
        SourceType = QaSourceTypes.IncomingPreGrn,
        EligibilityNote = QaEligibilityNotes.RecordOnly,
        CreatedDate = DateTime.UtcNow,
        CreatedBy = "tester"
    };

    private sealed record StockSnapshot(
        int BalLocCount,
        decimal BalLocQty,
        int HistoryCount,
        int BatchCount,
        int CostStateCount,
        int WipLotCount,
        int WipMoveCount,
        int CaseCount,
        int ActionCount,
        int AttemptCount);
}
