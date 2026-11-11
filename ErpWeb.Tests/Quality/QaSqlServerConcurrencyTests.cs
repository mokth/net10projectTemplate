using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Quality;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Quality;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ErpWeb.Tests.Quality;

/// <summary>
/// Phase 5 SQL Server two-session proof for record-only QA.
/// Runs only against <c>ConnectionStrings:SqlServerTestConnection</c> when the catalog name
/// contains "test". Set <c>ERPWEB_REQUIRE_SQLSERVER_TESTS=1</c> so a missing scratch database
/// fails instead of silently returning.
/// Never uses DefaultConnection / live ERPWeb.
/// </summary>
[Collection("QaSqlServer")]
[Trait(TestCategories.Name, TestCategories.Quality)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public class QaSqlServerConcurrencyTests : IAsyncLifetime
{
    internal const string TestConnectionKey = "SqlServerTestConnection";
    private const string Company = "QA5T";
    private const string Branch = "HQ";

    private string? _connectionString;
    private IDbContextFactory<AppDbContext>? _factory;

    private static bool RequireSqlServer =>
        string.Equals(
            Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"),
            "1",
            StringComparison.Ordinal);

    public async Task InitializeAsync()
    {
        _connectionString = GetSqlServerConnectionString();
        if (_connectionString is null
            || !IsScratchDatabase(_connectionString)
            || !CanConnect(_connectionString))
        {
            return;
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connectionString)
            .Options;
        _factory = new TestDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Two_connections_allocate_distinct_case_numbers()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var first = CreateCaseService("tester");
        var second = CreateCaseService("tester");
        var results = await Task.WhenAll(
            first.SaveDraftAsync(Incoming("DN-A")),
            second.SaveDraftAsync(Incoming("DN-B")));

        Assert.All(results, r => Assert.True(r.Succeeded, r.ErrorMessage));
        Assert.Equal(2, results.Select(r => r.Case!.CaseNo).Distinct().Count());
    }

    [Fact]
    public async Task Two_connections_cannot_both_approve_the_same_rowversion()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var inspector = CreateCaseService("tester");
        var pending = await ReadyForReviewAsync(inspector, "DN-RACE");
        var command = Review(pending.Case!);
        var left = CreateCaseService("approver");
        var right = CreateCaseService("approver");

        var results = await Task.WhenAll(
            left.ApproveAsync(command),
            right.ApproveAsync(command));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        var loser = Assert.Single(results.Where(r => !r.Succeeded));
        Assert.Equal(QaCaseMessages.ChangedReload, loser.ErrorMessage);

        await using var db = await _factory!.CreateDbContextAsync();
        var stored = await db.QaCases.SingleAsync(x => x.CaseNo == pending.Case!.CaseNo);
        Assert.Equal(QaCaseStatuses.Approved, stored.Status);
    }

    [Fact]
    public async Task Unique_case_no_index_rejects_a_second_writer()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var caseNo = $"QC{Guid.NewGuid():N}"[..20];
        await using (var first = await _factory!.CreateDbContextAsync())
        {
            first.QaCases.Add(MinimalCase(caseNo));
            await first.SaveChangesAsync();
        }

        await using var second = await _factory.CreateDbContextAsync();
        second.QaCases.Add(MinimalCase(caseNo));
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Unique_action_no_index_rejects_a_second_writer()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var approved = await ApproveWithRejectAsync("DN-ACT");
        await using var db = await _factory!.CreateDbContextAsync();
        var entity = await db.QaCases
            .Include(x => x.Lines)
            .Include(x => x.CorrectiveActions)
            .SingleAsync(x => x.CaseNo == approved.Case!.CaseNo);
        var line = entity.Lines.Single();
        var duplicateNo = entity.CorrectiveActions.Max(x => x.ActionNo);
        db.QaCorrectiveActions.Add(new QaCorrectiveAction
        {
            CaseId = entity.Uid,
            CaseLineId = line.Uid,
            ActionNo = duplicateNo,
            ActionType = QaActionTypes.FollowUp,
            Qty = 0m,
            Status = QaActionStatuses.Open,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "tester"
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Follow_up_script_is_idempotent_and_rebuilds_dropped_tables()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        try
        {
            await using (var db = await _factory!.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS dbo.QaInspectionAttempt;");
                await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS dbo.QaCorrectiveAction;");
            }

            await using (var missing = await _factory.CreateDbContextAsync())
            {
                var before = await QaSchemaReadiness.EvaluateAsync(missing);
                Assert.False(before.FollowUp.IsReady);
                Assert.True(before.CanQueryCaseGraph);
            }

            await ExecuteFollowUpScriptAsync();
            await ExecuteFollowUpScriptAsync();

            await using var readyDb = await _factory.CreateDbContextAsync();
            var ready = await QaSchemaReadiness.EvaluateAsync(readyDb);
            Assert.False(ready.ProbeFailed);
            Assert.True(ready.FollowUp.IsReady);
            Assert.True(ready.CanQueryFollowUp);
            Assert.True(ready.CanQueryCaseGraph);
        }
        finally
        {
            try
            {
                await ExecuteFollowUpScriptAsync();
            }
            catch
            {
                // Best-effort restore so later tests in this collection still see follow-up tables.
            }
        }
    }

    [Fact]
    public async Task Schema_probe_enlists_in_the_current_transaction()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        await using var db = await _factory!.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var ready = await QaSchemaReadiness.EvaluateAsync(db);
        Assert.False(ready.ProbeFailed);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Record_only_workflow_does_not_mutate_stock_cost_or_wip()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var before = await SnapshotAsync();
        var approved = await ApproveWithRejectAsync("DN-ND");
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        Assert.Contains(approved.Case!.Actions, x => x.ActionType == QaActionTypes.Reject);

        var after = await SnapshotAsync();
        Assert.Equal(before.BalLocCount, after.BalLocCount);
        Assert.Equal(before.HistoryCount, after.HistoryCount);
        Assert.Equal(before.BatchCount, after.BatchCount);
        Assert.Equal(before.CostStateCount, after.CostStateCount);
        Assert.Equal(before.WipLotCount, after.WipLotCount);
        Assert.Equal(before.WipMoveCount, after.WipMoveCount);
        Assert.Equal(before.CaseCount + 1, after.CaseCount);
        Assert.True(after.ActionCount > before.ActionCount);
        Assert.True(after.AttemptCount > before.AttemptCount);
    }

    private bool SkipWithoutSqlServer()
    {
        if (_factory is not null)
        {
            return false;
        }

        if (RequireSqlServer)
        {
            Assert.Fail(
                "QA SQL Server suite requires ConnectionStrings__SqlServerTestConnection pointing at a "
                + "catalog whose name contains 'test'.");
        }

        return true;
    }

    private IQaCaseService CreateCaseService(string user)
    {
        var tenant = new Mock<IInventoryTenantContext>();
        var access = new Mock<IAccessRightService>();
        var scope = new InventoryTenantScope
        {
            CompanyCode = Company,
            BranchCode = Branch,
            UserId = user
        };
        tenant.Setup(x => x.TryCompanyScope()).Returns(scope);
        tenant.Setup(x => x.TryBranchScope()).Returns(scope);
        tenant.Setup(x => x.TryWriteScope()).Returns(scope);
        access
            .Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new QaCaseService(_factory!, tenant.Object, access.Object, new RunningNumberService());
    }

    private async Task<QaResult> ReadyForReviewAsync(IQaCaseService cases, string deliveryNote)
    {
        var saved = await cases.SaveDraftAsync(Incoming(deliveryNote));
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var submitted = await cases.SubmitAsync(Review(saved.Case!));
        Assert.True(submitted.Succeeded, submitted.ErrorMessage);
        var started = await cases.StartInspectionAsync(Review(submitted.Case!));
        Assert.True(started.Succeeded, started.ErrorMessage);
        var inspected = await cases.SaveInspectionAsync(Inspect(started.Case!, accept: 6m, reject: 4m));
        Assert.True(inspected.Succeeded, inspected.ErrorMessage);
        var review = await cases.SubmitForReviewAsync(Review(inspected.Case!));
        Assert.True(review.Succeeded, review.ErrorMessage);
        return review;
    }

    private async Task<QaResult> ApproveWithRejectAsync(string deliveryNote)
    {
        var inspector = CreateCaseService("tester");
        var pending = await ReadyForReviewAsync(inspector, deliveryNote);
        var approver = CreateCaseService("approver");
        var approved = await approver.ApproveAsync(Review(pending.Case!));
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        return approved;
    }

    private async Task<StockSnapshot> SnapshotAsync()
    {
        await using var db = await _factory!.CreateDbContextAsync();
        return new StockSnapshot(
            await db.IvBalLocs.CountAsync(),
            await db.IvTrxHistories.CountAsync(),
            await db.IvTrxBatches.CountAsync(),
            await db.StockCostStates.CountAsync(),
            await db.ProductionBalLots.CountAsync(),
            await db.ProductionBalLotMovements.CountAsync(),
            await db.QaCases.CountAsync(),
            await db.QaCorrectiveActions.CountAsync(),
            await db.QaInspectionAttempts.CountAsync());
    }

    private async Task ExecuteFollowUpScriptAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scripts", "create-qa-followup.sql");
        Assert.True(File.Exists(path), path);
        var sql = await File.ReadAllTextAsync(path);
        await using var db = await _factory!.CreateDbContextAsync();
        foreach (var batch in SplitGoBatches(sql))
        {
            await db.Database.ExecuteSqlRawAsync(batch);
        }
    }

    private static IEnumerable<string> SplitGoBatches(string sql)
    {
        var current = new System.Text.StringBuilder();
        foreach (var line in sql.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                var batch = current.ToString().Trim();
                if (batch.Length > 0)
                {
                    yield return batch;
                }

                current.Clear();
                continue;
            }

            current.AppendLine(line);
        }

        var last = current.ToString().Trim();
        if (last.Length > 0)
        {
            yield return last;
        }
    }

    private static QaCaseSaveCommand Incoming(string deliveryNote) => new()
    {
        CaseDate = DateTime.Today,
        SourceType = QaSourceTypes.IncomingPreGrn,
        DeliveryNote = deliveryNote,
        Lines = [new QaCaseLineSaveCommand { LineNo = 1, ItemCode = "RM1", Uom = "PCS", Qty = 10m }]
    };

    private static QaReviewCommand Review(QaCaseDto value) => new()
    {
        CaseNo = value.CaseNo,
        RowVersion = value.RowVersion
    };

    private static QaInspectionSaveCommand Inspect(QaCaseDto value, decimal accept, decimal reject) => new()
    {
        CaseNo = value.CaseNo,
        RowVersion = value.RowVersion,
        Lines =
        [
            new QaInspectionLineCommand
            {
                LineNo = 1,
                Result = reject > 0m ? QaInspectionResults.Fail : QaInspectionResults.Pass,
                InspectedQty = accept + reject,
                Dispositions =
                [
                    new QaDispositionDto { Kind = QaDispositionKinds.Accept, Qty = accept },
                    new QaDispositionDto { Kind = QaDispositionKinds.Reject, Qty = reject }
                ]
            }
        ]
    };

    private static QaCase MinimalCase(string caseNo) => new()
    {
        CompanyCode = Company,
        BranchCode = Branch,
        CaseNo = caseNo,
        CaseDate = DateTime.Today,
        Status = QaCaseStatuses.Draft,
        SourceType = QaSourceTypes.IncomingPreGrn,
        EligibilityNote = QaEligibilityNotes.RecordOnly,
        CreatedDate = DateTime.UtcNow,
        CreatedBy = "tester"
    };

    private static string? GetSqlServerConnectionString()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString(TestConnectionKey);
        return string.IsNullOrWhiteSpace(cs) ? null : cs;
    }

    private static bool IsScratchDatabase(string connectionString)
    {
        try
        {
            var name = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString).InitialCatalog;
            return name.Contains("test", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool CanConnect(string connectionString)
    {
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
            using var db = new AppDbContext(options);
            return db.Database.IsSqlServer() && db.Database.CanConnect();
        }
        catch
        {
            return false;
        }
    }

    private sealed record StockSnapshot(
        int BalLocCount,
        int HistoryCount,
        int BatchCount,
        int CostStateCount,
        int WipLotCount,
        int WipMoveCount,
        int CaseCount,
        int ActionCount,
        int AttemptCount);
}

[CollectionDefinition("QaSqlServer", DisableParallelization = true)]
public sealed class QaSqlServerCollectionDefinition;
