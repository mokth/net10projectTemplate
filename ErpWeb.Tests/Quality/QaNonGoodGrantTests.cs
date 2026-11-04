using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Quality;
using ErpWeb.Core.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Quality;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Quality;

[Trait(TestCategories.Name, TestCategories.Quality)]
public class QaNonGoodGrantTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly Mock<IInventoryTenantContext> _tenant = new();
    private readonly Mock<IAccessRightService> _access = new();
    private InventoryTenantScope _scope = Scope("tester");

    public QaNonGoodGrantTests()
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
    public async Task Claim_is_noop_when_nongood_gate_is_off()
    {
        var output = Output(qty: 4m);
        await using var db = await _factory.CreateDbContextAsync();
        var error = await new QaNonGoodAuthorizationService().ClaimDailyProductionAsync(
            db, "DEMO", "HQ", output);
        Assert.Null(error);
        Assert.Equal(0, await db.QaNonGoodAllocations.CountAsync());
        Assert.Equal(0, await db.ProductionBalLots.CountAsync());
    }

    [Fact]
    public async Task Claim_fails_closed_when_gate_is_on()
    {
        await EnableNonGoodAsync();
        var output = Output(qty: 4m);
        await using var db = await _factory.CreateDbContextAsync();
        var error = await new QaNonGoodAuthorizationService().ClaimDailyProductionAsync(
            db, "DEMO", "HQ", output);
        Assert.Equal(QaReadinessMessages.NonGoodFrozen, error);
        Assert.Equal(0, await db.QaNonGoodAllocations.CountAsync());
    }

    [Fact]
    public async Task Claim_does_not_consume_grant_while_nongood_is_frozen()
    {
        await EnableNonGoodAsync();
        await SeedGrantAsync(accepted: 4m);
        await using var db = await _factory.CreateDbContextAsync();
        var error = await new QaNonGoodAuthorizationService().ClaimDailyProductionAsync(
            db, "DEMO", "HQ", Output(qty: 4m));
        Assert.Equal(QaReadinessMessages.NonGoodFrozen, error);
        Assert.Equal(0, await db.QaNonGoodAllocations.CountAsync());
    }

    [Fact]
    public async Task Restore_reverses_posted_allocation_even_when_gate_is_off()
    {
        await SeedGrantAsync(accepted: 4m);
        await using var db = await _factory.CreateDbContextAsync();
        var grant = await db.QaNonGoodGrants.SingleAsync();
        db.QaNonGoodAllocations.Add(new QaNonGoodAllocation
        {
            GrantId = grant.Uid,
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Qty = 4m,
            Status = QaReceiptAllocationStatuses.Posted,
            ProductionOutputId = 99,
            PostedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var auth = new QaNonGoodAuthorizationService();
        Assert.Null(await auth.RestoreDailyProductionAsync(db, "DEMO", "HQ", 99));
        await db.SaveChangesAsync();

        var allocation = Assert.Single(await db.QaNonGoodAllocations.ToListAsync());
        Assert.Equal(QaReceiptAllocationStatuses.Reversed, allocation.Status);
        Assert.NotNull(allocation.ReversedAtUtc);
        Assert.Equal(0, await db.ProductionBalLots.CountAsync());
    }

    [Fact]
    public async Task Approve_is_record_only_without_grant_valued_lots_or_cost()
    {
        var cases = new QaCaseService(_factory, _tenant.Object, _access.Object, new RunningNumberService());
        var saved = await cases.SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.ProductionOutputNonGood,
            SourceDocType = "WI",
            SourceDocNo = "DP00000099",
            WorkOrderNo = "WO1",
            Lines =
            [
                new QaCaseLineSaveCommand
                {
                    LineNo = 1,
                    ItemCode = "FG1",
                    Uom = "PCS",
                    Qty = 4m
                }
            ]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
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
                    InspectedQty = 4m,
                    Dispositions =
                    [
                        new QaDispositionDto { Kind = QaDispositionKinds.Accept, Qty = 4m }
                    ]
                }
            ]
        });
        Assert.True(inspected.Succeeded, inspected.ErrorMessage);
        var review = await cases.SubmitForReviewAsync(Review(inspected.Case!));
        _scope = Scope("approver");
        BindTenant();
        var approved = await cases.ApproveAsync(Review(review.Case!));
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        Assert.DoesNotContain(approved.Case!.Audit, x => x.EventType == QaAuditEvents.GrantIssued);
        Assert.Equal(QaEligibilityNotes.NonGoodOutput, approved.Case.EligibilityNote);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.QaNonGoodGrants.CountAsync());
        Assert.Equal(0, await db.QaNonGoodAllocations.CountAsync());
        Assert.Equal(0, await db.ProductionBalLots.CountAsync());
        Assert.Equal(0, await db.IvBalLocs.CountAsync());
        Assert.Equal(0, await db.IvTrxBatches.CountAsync());
        Assert.Equal(0, await db.StockPostings.CountAsync());
    }

    private async Task EnableNonGoodAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.QaPolicies.Add(new QaPolicy
        {
            CompanyCode = "DEMO",
            Version = 1,
            Enabled = true,
            NonGoodEnabled = true,
            IsPublished = true,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "tester"
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedGrantAsync(decimal accepted)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var qaCase = new QaCase
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            CaseNo = "QC00000001",
            CaseDate = DateTime.Today,
            Status = QaCaseStatuses.Approved,
            SourceType = QaSourceTypes.ProductionOutputNonGood,
            SourceHeaderId = 99,
            SourceDocNo = "DP00000099",
            EligibilityNote = QaEligibilityNotes.NonGoodGrant,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "tester"
        };
        var line = new QaCaseLine
        {
            LineNo = 1,
            ItemCode = "FG1",
            Uom = "PCS",
            Qty = accepted
        };
        qaCase.Lines.Add(line);
        qaCase.NonGoodGrants.Add(new QaNonGoodGrant
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Case = qaCase,
            CaseLine = line,
            ProductionOutputId = 99,
            LineKey = 1,
            DocumentNo = "DP00000099",
            ItemCode = "FG1",
            Uom = "PCS",
            AcceptedQty = accepted,
            Status = QaReceiptGrantStatuses.Open,
            ApprovedAtUtc = DateTime.UtcNow
        });
        db.QaCases.Add(qaCase);
        await db.SaveChangesAsync();
    }

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

    private static ProductionOutput Output(decimal qty) => new()
    {
        Uid = 99,
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        DocumentNo = "DP00000099",
        DocumentRevision = 1,
        ScrapQty = qty >= 2m ? 2m : qty,
        RejectQty = qty >= 3m ? 1m : 0m,
        HoldQty = Math.Max(0m, qty - 3m)
    };

    private static QaReviewCommand Review(QaCaseDto value) => new()
    {
        CaseNo = value.CaseNo,
        RowVersion = value.RowVersion
    };
}
