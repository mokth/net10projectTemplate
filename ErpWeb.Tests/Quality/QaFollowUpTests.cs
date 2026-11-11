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
public class QaFollowUpTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly Mock<IInventoryTenantContext> _tenant = new();
    private readonly Mock<IAccessRightService> _access = new();
    private InventoryTenantScope _scope = Scope("tester");

    public QaFollowUpTests()
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
    public async Task Inspection_save_appends_attempts_without_dropping_history()
    {
        var cases = CreateCaseService();
        var started = await StartIncomingAsync(cases, 10m);
        var first = await cases.SaveInspectionAsync(Inspect(started.Case!, 10m, accept: 10m));
        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.Single(first.Case!.Attempts);

        var second = await cases.SaveInspectionAsync(Inspect(first.Case, 10m, accept: 6m, reject: 4m, reason: "Retest"));
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.Equal(2, second.Case!.Attempts.Count);
        Assert.Equal(1, second.Case.Attempts[0].AttemptNo);
        Assert.Equal(2, second.Case.Attempts[1].AttemptNo);
        Assert.Equal("Retest", second.Case.Attempts[1].Reason);
        Assert.Equal(4m, second.Case.RejectedQty);
        Assert.Single(second.Case.Lines[0].Dispositions, x => x.Kind == QaDispositionKinds.Reject);
    }

    [Fact]
    public async Task Approve_creates_follow_up_for_reject_qty_without_stock()
    {
        var cases = CreateCaseService();
        var started = await StartIncomingAsync(cases, 10m);
        var inspected = await cases.SaveInspectionAsync(Inspect(started.Case!, 10m, accept: 7m, reject: 3m));
        var review = await cases.SubmitForReviewAsync(Review(inspected.Case!));
        _scope = Scope("approver");
        BindTenant();
        var approved = await cases.ApproveAsync(Review(review.Case!));
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        var action = Assert.Single(approved.Case!.Actions, x => x.ActionType == QaActionTypes.Reject);
        Assert.Equal(3m, action.Qty);
        Assert.True(action.IsOpen);
        Assert.Equal(1, approved.Case.OpenActionCount);
        Assert.True(approved.Case.FollowUpReady);
        Assert.Equal(QaCaseMessages.AttachmentsDeferred, approved.Case.AttachmentsNote);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.IvBalLocs.CountAsync());
        Assert.Equal(0, await db.IvTrxBatches.CountAsync());
        Assert.Equal(1, await db.QaCorrectiveActions.CountAsync());
    }

    [Fact]
    public async Task Follow_up_qty_cannot_exceed_remaining_decision_qty()
    {
        var approved = await ApproveWithRejectAsync(10m, 4m);
        var cases = CreateCaseService();
        var extra = await cases.SaveActionAsync(new QaActionSaveCommand
        {
            CaseNo = approved.Case!.CaseNo,
            RowVersion = approved.Case.RowVersion,
            LineNo = 1,
            ActionType = QaActionTypes.Reject,
            Qty = 1m,
            Reason = "Second task"
        });
        Assert.False(extra.Succeeded);
        Assert.Contains("cannot exceed", extra.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Duplicate_request_id_is_idempotent()
    {
        var approved = await ApproveWithRejectAsync(10m, 4m);
        var cases = CreateCaseService();
        var requestId = Guid.NewGuid();
        var command = new QaActionSaveCommand
        {
            CaseNo = approved.Case!.CaseNo,
            RowVersion = approved.Case.RowVersion,
            LineNo = 1,
            ActionType = QaActionTypes.FollowUp,
            Qty = 0m,
            Reason = "Call supplier",
            RequestId = requestId
        };
        var first = await cases.SaveActionAsync(command);
        Assert.True(first.Succeeded, first.ErrorMessage);
        var second = await cases.SaveActionAsync(new QaActionSaveCommand
        {
            CaseNo = command.CaseNo,
            RowVersion = first.Case!.RowVersion,
            LineNo = command.LineNo,
            ActionType = command.ActionType,
            Qty = command.Qty,
            Reason = command.Reason,
            RequestId = requestId
        });
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.Equal(2, second.Case!.Actions.Count);
        Assert.Equal(1, second.Case.Actions.Count(x => x.RequestId == requestId));
    }

    [Fact]
    public async Task Close_is_blocked_while_follow_up_is_open()
    {
        var approved = await ApproveWithRejectAsync(10m, 4m);
        var cases = CreateCaseService();
        var closed = await cases.CloseAsync(Review(approved.Case!));
        Assert.False(closed.Succeeded);
        Assert.Equal(QaCaseMessages.OpenActionsRemain, closed.ErrorMessage);

        var done = await cases.CompleteActionAsync(new QaActionUpdateCommand
        {
            CaseNo = approved.Case!.CaseNo,
            RowVersion = approved.Case.RowVersion,
            ActionNo = approved.Case.Actions.Single(x => x.IsOpen).ActionNo,
            CompletionEvidence = "Supplier notified. No stock posting."
        });
        Assert.True(done.Succeeded, done.ErrorMessage);
        var after = await cases.CloseAsync(Review(done.Case!));
        Assert.True(after.Succeeded, after.ErrorMessage);
        Assert.Equal(QaCaseStatuses.Closed, after.Case!.Status);
    }

    [Fact]
    public async Task Reinspect_from_rejected_keeps_the_same_source_identity()
    {
        var cases = CreateCaseService();
        var started = await StartIncomingAsync(cases, 5m);
        var inspected = await cases.SaveInspectionAsync(Inspect(started.Case!, 5m, accept: 0m, reject: 5m));
        var review = await cases.SubmitForReviewAsync(Review(inspected.Case!));
        _scope = Scope("approver");
        BindTenant();
        var rejected = await cases.RejectAsync(Review(review.Case!));
        Assert.True(rejected.Succeeded, rejected.ErrorMessage);

        var reinspect = await cases.ReinspectAsync(new QaReviewCommand
        {
            CaseNo = rejected.Case!.CaseNo,
            RowVersion = rejected.Case.RowVersion,
            Remarks = "Supplier sent replacement photos"
        });
        Assert.True(reinspect.Succeeded, reinspect.ErrorMessage);
        Assert.Equal(QaCaseStatuses.InInspection, reinspect.Case!.Status);
        Assert.Equal(rejected.Case.SourceHeaderId, reinspect.Case.SourceHeaderId);
        Assert.Equal(rejected.Case.SourceDocNo, reinspect.Case.SourceDocNo);
        Assert.Equal(rejected.Case.DeliveryNote, reinspect.Case.DeliveryNote);
        Assert.Contains(reinspect.Case.Audit, x => x.EventType == QaAuditEvents.ReinspectionStarted);
    }

    [Fact]
    public void Reinspect_is_blocked_while_a_hold_is_active()
    {
        var entity = new QaCase { Status = QaCaseStatuses.Approved };
        entity.LogicalHolds.Add(new QaLogicalHold
        {
            Status = QaLogicalHoldStatuses.Active,
            ReservedBaseQty = 1m
        });
        Assert.False(QaFollowUp.CanReinspect(entity));

        var sealedCase = new QaCase { Status = QaCaseStatuses.Approved };
        var grant = new QaReceiptGrant();
        grant.Allocations.Add(new QaReceiptAllocation { Status = QaReceiptAllocationStatuses.Posted });
        sealedCase.Grants.Add(grant);
        Assert.False(QaFollowUp.CanReinspect(sealedCase));
    }

    [Fact]
    public async Task Earlier_attempt_inspector_cannot_approve_after_reinspection()
    {
        var cases = CreateCaseService();
        var started = await StartIncomingAsync(cases, 5m);
        var first = await cases.SaveInspectionAsync(Inspect(started.Case!, 5m, accept: 5m));
        var review = await cases.SubmitForReviewAsync(Review(first.Case!));
        _scope = Scope("approver");
        BindTenant();
        var approved = await cases.ApproveAsync(Review(review.Case!));
        Assert.True(approved.Succeeded, approved.ErrorMessage);

        var reinspect = await cases.ReinspectAsync(new QaReviewCommand
        {
            CaseNo = approved.Case!.CaseNo,
            RowVersion = approved.Case.RowVersion,
            Remarks = "Retest the same delivery"
        });
        Assert.True(reinspect.Succeeded, reinspect.ErrorMessage);

        _scope = Scope("second");
        BindTenant();
        var second = await cases.SaveInspectionAsync(Inspect(reinspect.Case!, 5m, accept: 5m));
        Assert.True(second.Succeeded, second.ErrorMessage);
        var review2 = await cases.SubmitForReviewAsync(Review(second.Case!));
        Assert.True(review2.Succeeded, review2.ErrorMessage);

        _scope = Scope("tester");
        BindTenant();
        var blocked = await cases.ApproveAsync(Review(review2.Case!));
        Assert.False(blocked.Succeeded);
        Assert.Contains("inspector cannot approve", blocked.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_follow_up_tables_do_not_block_the_case_graph()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS QaCorrectiveAction;");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS QaInspectionAttempt;");
        }

        await using var probe = await _factory.CreateDbContextAsync();
        var ready = await QaSchemaReadiness.EvaluateAsync(probe);
        Assert.True(ready.CanQueryCaseGraph);
        Assert.False(ready.CanQueryFollowUp);

        var cases = CreateCaseService();
        var started = await StartIncomingAsync(cases, 5m);
        var inspected = await cases.SaveInspectionAsync(Inspect(started.Case!, 5m, accept: 5m));
        Assert.True(inspected.Succeeded, inspected.ErrorMessage);
        Assert.Empty(inspected.Case!.Attempts);
        Assert.False(inspected.Case.FollowUpReady);

        var action = await cases.SaveActionAsync(new QaActionSaveCommand
        {
            CaseNo = inspected.Case.CaseNo,
            RowVersion = inspected.Case.RowVersion,
            LineNo = 1,
            ActionType = QaActionTypes.FollowUp,
            Qty = 0m,
            Reason = "Call supplier"
        });
        Assert.False(action.Succeeded);
        Assert.Equal(QaCaseMessages.FollowUpNotInstalled, action.ErrorMessage);
    }

    [Fact]
    public async Task Source_check_note_is_recorded_without_changing_the_source()
    {
        var seeded = await SeedPostedGrnAsync(batchNo: 44, qty: 8m);
        var cases = CreateCaseService();
        var saved = await cases.SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.PostedGrn,
            SourceHeaderId = seeded.Batch.Id,
            Lines =
            [
                new QaCaseLineSaveCommand
                {
                    LineNo = 1,
                    ItemCode = "A100",
                    Uom = "EA",
                    Qty = 8m,
                    SourceLineId = seeded.Detail.Id
                }
            ]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;");
            db.IvTrxBatches.RemoveRange(db.IvTrxBatches);
            await db.SaveChangesAsync();
        }

        var current = await cases.GetAsync(saved.Case!.CaseNo);
        Assert.True(current.Succeeded, current.ErrorMessage);
        Assert.Equal(QaSourceMessages.BatchNotFound, current.Case!.SourceCheckNote);
        Assert.Equal(saved.Case.CaseNo, current.Case.CaseNo);

        await using var after = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await after.IvTrxBatches.CountAsync());
        Assert.Equal(1, await after.QaCases.CountAsync());
    }

    private async Task<QaResult> ApproveWithRejectAsync(decimal qty, decimal reject)
    {
        var cases = CreateCaseService();
        var started = await StartIncomingAsync(cases, qty);
        var inspected = await cases.SaveInspectionAsync(Inspect(started.Case!, qty, accept: qty - reject, reject: reject));
        var review = await cases.SubmitForReviewAsync(Review(inspected.Case!));
        _scope = Scope("approver");
        BindTenant();
        var approved = await cases.ApproveAsync(Review(review.Case!));
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        return approved;
    }

    private async Task<QaResult> StartIncomingAsync(IQaCaseService cases, decimal qty)
    {
        var saved = await cases.SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.IncomingPreGrn,
            DeliveryNote = "DN-FU",
            Lines = [new QaCaseLineSaveCommand { LineNo = 1, ItemCode = "RM1", Uom = "PCS", Qty = qty }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var submitted = await cases.SubmitAsync(Review(saved.Case!));
        return await cases.StartInspectionAsync(Review(submitted.Case!));
    }

    private async Task<(IvTrxBatch Batch, IvTrxBatchDetail Detail)> SeedPostedGrnAsync(int batchNo, decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var batch = new IvTrxBatch
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            BatchNo = batchNo,
            TrxDtTime = DateTime.Today,
            TrxType = IvTrxTypes.GoodsReceive,
            BatchStatus = IvBatchStatuses.Posted,
            RefNo = "DN-GRN"
        };
        batch.Details.Add(new IvTrxBatchDetail
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            BatchNo = batchNo,
            TrxLineNo = 1,
            TrxType = IvTrxTypes.GoodsReceive,
            ICode = "A100",
            ToPurQty = qty,
            ToStdQty = qty,
            ToPurUom = "EA",
            ToStdUom = "EA"
        });
        db.IvTrxBatches.Add(batch);
        await db.SaveChangesAsync();
        return (batch, batch.Details.Single());
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

    private static QaInspectionSaveCommand Inspect(
        QaCaseDto value,
        decimal qty,
        decimal accept,
        decimal reject = 0m,
        string? reason = null)
    {
        var dispositions = new List<QaDispositionDto>
        {
            new() { Kind = QaDispositionKinds.Accept, Qty = accept }
        };
        if (reject > 0m)
        {
            dispositions.Add(new QaDispositionDto { Kind = QaDispositionKinds.Reject, Qty = reject });
        }

        return new QaInspectionSaveCommand
        {
            CaseNo = value.CaseNo,
            RowVersion = value.RowVersion,
            ReinspectionReason = reason,
            Lines =
            [
                new QaInspectionLineCommand
                {
                    LineNo = 1,
                    Result = reject > 0m ? QaInspectionResults.Fail : QaInspectionResults.Pass,
                    InspectedQty = qty,
                    Dispositions = dispositions
                }
            ]
        };
    }
}
