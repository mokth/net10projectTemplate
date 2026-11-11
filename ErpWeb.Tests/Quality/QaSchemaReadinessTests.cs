using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
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
public class QaSchemaReadinessTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public QaSchemaReadinessTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task EnsureCreated_sqlite_catalog_is_ready()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var ready = await QaSchemaReadiness.EvaluateAsync(db);
        Assert.False(ready.ProbeFailed);
        Assert.False(ready.ShouldSkipAllQaQueries);
        Assert.Equal(QaSchemaProvenance.PreviouslyDeployed, ready.Provenance);
        Assert.True(ready.CanQueryCaseGraph);
        Assert.True(ready.Incoming.IsReady);
        Assert.True(ready.FollowUp.IsReady);
        Assert.True(ready.CanQueryFollowUp);
        Assert.Null(ready.RequireCaseGraph());
    }

    [Fact]
    public async Task Absent_allowlist_skips_goods_receipt_claim()
    {
        await DropQaTablesAsync();
        var (batch, details) = await SeedReceiptAsync();
        await using var db = await _factory.CreateDbContextAsync();
        var ready = await QaSchemaReadiness.EvaluateAsync(db);
        Assert.True(ready.ShouldSkipAllQaQueries);
        Assert.Equal(QaSchemaProvenance.Unknown, ready.Provenance);
        Assert.NotEqual("LEGACY_CONFIRMED", ready.Provenance);
        Assert.Null(await new QaReceiptAuthorizationService().ClaimGoodsReceiptAsync(
            db, "DEMO", "HQ", IvTrxTypes.GoodsReceive, batch, details));
    }

    [Fact]
    public async Task Partial_incoming_pair_fails_closed_on_claim_and_restore()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE QaReceiptAllocation;");
        }

        var (batch, details) = await SeedReceiptAsync();
        await using var claimDb = await _factory.CreateDbContextAsync();
        var error = await new QaReceiptAuthorizationService().ClaimGoodsReceiptAsync(
            claimDb, "DEMO", "HQ", IvTrxTypes.GoodsReceive, batch, details);
        Assert.NotNull(error);
        Assert.Contains("incomplete", error, StringComparison.OrdinalIgnoreCase);

        var restore = await new QaReceiptAuthorizationService().RestoreGoodsReceiptAsync(
            claimDb, "DEMO", "HQ", batch.BatchNo, details);
        Assert.NotNull(restore);
        Assert.Contains("incomplete", restore, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_incoming_pair_on_a_deployed_catalog_fails_closed()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS QaReceiptAllocation;");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS QaReceiptGrant;");
        }

        var (batch, details) = await SeedReceiptAsync();
        await using var claimDb = await _factory.CreateDbContextAsync();
        var ready = await QaSchemaReadiness.EvaluateAsync(claimDb);
        Assert.False(ready.ShouldSkipAllQaQueries);
        Assert.Equal(QaSchemaProvenance.PreviouslyDeployed, ready.Provenance);
        Assert.True(ready.Incoming.IsAbsent);

        var error = await new QaReceiptAuthorizationService().ClaimGoodsReceiptAsync(
            claimDb, "DEMO", "HQ", IvTrxTypes.GoodsReceive, batch, details);
        Assert.NotNull(error);
        Assert.Contains("incomplete", error, StringComparison.OrdinalIgnoreCase);

        var restore = await new QaReceiptAuthorizationService().RestoreGoodsReceiptAsync(
            claimDb, "DEMO", "HQ", batch.BatchNo, details);
        Assert.NotNull(restore);
        Assert.Contains("incomplete", restore, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Policy_get_skips_when_catalog_is_absent()
    {
        await DropQaTablesAsync();
        var policies = CreatePolicyService();
        var result = await policies.GetAsync();
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(QaReadinessMessages.NotInstalled, result.InfoMessage);
        Assert.False(result.Policy!.IncomingInspectionEnabled);
        Assert.False(result.Policy.FinalFgInspectionEnabled);
        Assert.False(result.Policy.LogicalHoldEnabled);
        Assert.False(result.Policy.PhysicalHoldEnabled);
        Assert.False(result.Policy.NonGoodEnabled);
    }

    [Fact]
    public async Task Policy_save_fails_when_catalog_is_absent()
    {
        await DropQaTablesAsync();
        var result = await CreatePolicyService().SaveAsync(new QaPolicySaveCommand());
        Assert.False(result.Succeeded);
        Assert.Equal(QaReadinessMessages.NotInstalled, result.ErrorMessage);
    }

    [Fact]
    public async Task Policy_save_refuses_to_turn_gates_on()
    {
        var result = await CreatePolicyService().SaveAsync(new QaPolicySaveCommand { IncomingInspectionEnabled = true });
        Assert.False(result.Succeeded);
        Assert.Contains("frozen", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private IQaPolicyService CreatePolicyService()
    {
        var tenant = new Mock<IInventoryTenantContext>();
        var access = new Mock<IAccessRightService>();
        var scope = new InventoryTenantScope { CompanyCode = "DEMO", BranchCode = "HQ", UserId = "tester" };
        tenant.Setup(x => x.TryCompanyScope()).Returns(scope);
        tenant.Setup(x => x.TryWriteScope()).Returns(scope);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new QaPolicyService(_factory, tenant.Object, access.Object);
    }

    private async Task DropQaTablesAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;");
        foreach (var table in QaSchemaReadiness.AllowlistedTables.Reverse())
        {
            await db.Database.ExecuteSqlRawAsync($"DROP TABLE IF EXISTS {table};");
        }
    }

    private async Task<(IvTrxBatch Batch, IReadOnlyList<IvTrxBatchDetail> Details)> SeedReceiptAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var batch = new IvTrxBatch
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            BatchNo = 1,
            TrxDtTime = DateTime.Today,
            TrxType = IvTrxTypes.GoodsReceive,
            BatchStatus = IvBatchStatuses.New,
            RefNo = "DN-1"
        };
        batch.Details.Add(new IvTrxBatchDetail
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            BatchNo = 1,
            TrxLineNo = 1,
            TrxType = IvTrxTypes.GoodsReceive,
            ICode = "A100",
            ToPurQty = 4m,
            ToStdQty = 4m,
            ToPurUom = "EA",
            ToStdUom = "EA"
        });
        db.IvTrxBatches.Add(batch);
        await db.SaveChangesAsync();
        return (batch, batch.Details.ToList());
    }
}
