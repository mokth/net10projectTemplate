using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Quality;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Quality;

[Trait(TestCategories.Name, TestCategories.Quality)]
public class QaPhase1SafetyTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly Mock<IInventoryTenantContext> _tenant = new();
    private readonly Mock<IAccessRightService> _access = new();
    private InventoryTenantScope _scope = Scope("tester");

    public QaPhase1SafetyTests()
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
    public async Task Posted_grn_search_returns_database_id_not_batch_no()
    {
        var seeded = await SeedPostedGrnAsync(batchNo: 1205, qty: 10m);
        var result = await CreateCaseService().SearchSourcesAsync(new QaSourceQuery
        {
            SourceType = QaSourceTypes.PostedGrn,
            ExactSourceHeaderId = seeded.Batch.Id,
            Take = 20
        });

        Assert.True(result.Succeeded, result.ErrorMessage);
        var row = Assert.Single(result.SourcePage!.Rows);
        Assert.Equal(seeded.Batch.Id, row.SourceHeaderId);
        Assert.Equal("1205", row.SourceDocNo);
        Assert.NotEqual(1205, seeded.Batch.Id);
        Assert.Equal(seeded.Detail.Id, row.SourceLineId);
    }

    [Fact]
    public async Task Posted_grn_without_header_id_is_rejected()
    {
        var result = await CreateCaseService().SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.PostedGrn,
            Lines =
            [
                new QaCaseLineSaveCommand { LineNo = 1, ItemCode = "A100", Uom = "EA", Qty = 1m, SourceLineId = 1 }
            ]
        });
        Assert.False(result.Succeeded);
        Assert.Equal(QaSourceMessages.NeedSourceHeader, result.ErrorMessage);
    }

    [Fact]
    public async Task Daily_production_without_header_id_is_rejected()
    {
        var result = await CreateCaseService().SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.ProductionOutputGood,
            Lines =
            [
                new QaCaseLineSaveCommand { LineNo = 1, ItemCode = "FG1", Uom = "PCS", Qty = 1m }
            ]
        });
        Assert.False(result.Succeeded);
        Assert.Equal(QaSourceMessages.NeedSourceHeader, result.ErrorMessage);
    }

    [Fact]
    public async Task Incoming_delivery_note_without_po_is_allowed()
    {
        var result = await CreateCaseService().SaveDraftAsync(UnlinkedIncoming(5m));
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(QaSourceTypes.IncomingPreGrn, result.Case!.SourceType);
    }

    [Fact]
    public async Task Two_case_lines_cannot_exceed_the_same_posted_source_qty()
    {
        var seeded = await SeedPostedGrnAsync(batchNo: 88, qty: 10m);
        var result = await CreateCaseService().SaveDraftAsync(new QaCaseSaveCommand
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
                    Qty = 6m,
                    SourceLineId = seeded.Detail.Id
                },
                new QaCaseLineSaveCommand
                {
                    LineNo = 2,
                    ItemCode = "A100",
                    Uom = "EA",
                    Qty = 6m,
                    SourceLineId = seeded.Detail.Id
                }
            ]
        });
        Assert.False(result.Succeeded);
        Assert.Equal(QaSourceMessages.StaleQty(1), result.ErrorMessage);
    }

    [Fact]
    public async Task Empty_rowversion_is_stale_when_the_case_has_a_token()
    {
        var saved = await CreateCaseService().SaveDraftAsync(UnlinkedIncoming(5m));
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        if (saved.Case!.RowVersion is not { Length: > 0 })
        {
            return;
        }

        var command = new QaCaseSaveCommand
        {
            CaseNo = saved.Case.CaseNo,
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.IncomingPreGrn,
            DeliveryNote = "DN-MANUAL",
            RowVersion = [],
            Lines =
            [
                new QaCaseLineSaveCommand { LineNo = 1, ItemCode = "RM1", Uom = "PCS", Qty = 5m }
            ]
        };
        var again = await CreateCaseService().SaveDraftAsync(command);
        Assert.False(again.Succeeded);
        Assert.Equal(QaCaseMessages.ChangedReload, again.ErrorMessage);
    }

    [Fact]
    public async Task Partial_inspection_cannot_be_submitted_for_review()
    {
        var cases = CreateCaseService();
        var saved = await cases.SaveDraftAsync(UnlinkedIncoming(5m));
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
                    InspectedQty = 2m,
                    Dispositions = [new QaDispositionDto { Kind = QaDispositionKinds.Accept, Qty = 2m }]
                }
            ]
        });
        Assert.True(inspected.Succeeded, inspected.ErrorMessage);
        var review = await cases.SubmitForReviewAsync(Review(inspected.Case!));
        Assert.False(review.Succeeded);
        Assert.Equal(QaCaseMessages.InspectFullQuantity, review.ErrorMessage);
    }

    [Fact]
    public async Task Any_contributing_inspector_cannot_approve()
    {
        var cases = CreateCaseService();
        var saved = await cases.SaveDraftAsync(TwoLineIncoming());
        var submitted = await cases.SubmitAsync(Review(saved.Case!));
        var started = await cases.StartInspectionAsync(Review(submitted.Case!));

        var first = await cases.SaveInspectionAsync(InspectLine(started.Case!, 1, "tester"));
        Assert.True(first.Succeeded, first.ErrorMessage);

        _scope = Scope("second");
        BindTenant();
        var second = await cases.SaveInspectionAsync(InspectLine(first.Case!, 2, "second"));
        Assert.True(second.Succeeded, second.ErrorMessage);

        var review = await cases.SubmitForReviewAsync(Review(second.Case!));
        Assert.True(review.Succeeded, review.ErrorMessage);

        _scope = Scope("tester");
        BindTenant();
        var approved = await cases.ApproveAsync(Review(review.Case!));
        Assert.False(approved.Succeeded);
        Assert.Contains("inspector cannot approve", approved.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_batch_id_does_not_validate_as_batch_no()
    {
        var seeded = await SeedPostedGrnAsync(batchNo: 1205, qty: 10m);
        var result = await CreateCaseService().SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.PostedGrn,
            SourceHeaderId = 1205,
            Lines =
            [
                new QaCaseLineSaveCommand
                {
                    LineNo = 1,
                    ItemCode = "A100",
                    Uom = "EA",
                    Qty = 1m,
                    SourceLineId = seeded.Detail.Id
                }
            ]
        });
        Assert.False(result.Succeeded);
        Assert.Equal(QaSourceMessages.BatchNotFound, result.ErrorMessage);
        Assert.NotEqual(1205, seeded.Batch.Id);
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

    private static QaCaseSaveCommand UnlinkedIncoming(decimal qty) => new()
    {
        CaseDate = DateTime.Today,
        SourceType = QaSourceTypes.IncomingPreGrn,
        DeliveryNote = "DN-MANUAL",
        Lines =
        [
            new QaCaseLineSaveCommand { LineNo = 1, ItemCode = "RM1", Uom = "PCS", Qty = qty }
        ]
    };

    private static QaCaseSaveCommand TwoLineIncoming() => new()
    {
        CaseDate = DateTime.Today,
        SourceType = QaSourceTypes.IncomingPreGrn,
        DeliveryNote = "DN-TWO",
        Lines =
        [
            new QaCaseLineSaveCommand { LineNo = 1, ItemCode = "RM1", Uom = "PCS", Qty = 5m },
            new QaCaseLineSaveCommand { LineNo = 2, ItemCode = "RM2", Uom = "PCS", Qty = 5m }
        ]
    };

    private static QaReviewCommand Review(QaCaseDto value) => new()
    {
        CaseNo = value.CaseNo,
        RowVersion = value.RowVersion
    };

    private static QaInspectionSaveCommand InspectLine(QaCaseDto value, int lineNo, string _) => new()
    {
        CaseNo = value.CaseNo,
        RowVersion = value.RowVersion,
        Lines =
        [
            new QaInspectionLineCommand
            {
                LineNo = lineNo,
                Result = QaInspectionResults.Pass,
                InspectedQty = 5m,
                Dispositions = [new QaDispositionDto { Kind = QaDispositionKinds.Accept, Qty = 5m }]
            }
        ]
    };

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
}
