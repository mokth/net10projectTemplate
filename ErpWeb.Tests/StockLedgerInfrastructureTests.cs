using ErpWeb.Core.Inventory;
using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Shared)]
public sealed class StockLedgerInfrastructureTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly IDbContextFactory<AppDbContext> _factory;

    public StockLedgerInfrastructureTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new LocalFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public void Model_contains_tenant_keys_v2_indexes_and_conditional_fields()
    {
        var sqlOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(local);Database=StockLedgerModelOnly;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var db = new AppDbContext(sqlOptions);
        var posting = db.Model.FindEntityType(typeof(StockPosting))!;
        Assert.Contains(posting.GetKeys(), key =>
            key.Properties.Select(x => x.Name).SequenceEqual(["CompanyCode", "BranchCode", "Id"]));
        Assert.Contains(posting.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(x => x.Name)
                .SequenceEqual(["CompanyCode", "BranchCode", "CommandType", "RequestId"]));

        var productionMovement = db.Model.FindEntityType(typeof(ProductionBalLotMovement))!;
        Assert.Contains(productionMovement.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(x => x.Name)
                .SequenceEqual(["StockPostingId", "PostingLineNo"]));
        Assert.NotNull(db.Model.FindEntityType(typeof(ProductionMovementAllocation)));

        foreach (var type in new[]
                 {
                     typeof(StockPosting), typeof(IvTrxHistory),
                     typeof(ProductionBalLotMovement), typeof(ProductionMaterialMovement),
                     typeof(ProductionMovementAllocation), typeof(IvBalLoc), typeof(ProductionBalLot)
                 })
        {
            var entityType = db.Model.FindEntityType(type)
                ?? throw new InvalidOperationException($"{type.Name} is not mapped.");
            Assert.False(entityType.IsSqlOutputClauseUsed(), $"{type.Name} must not use SQL Server OUTPUT.");
        }
    }

    [Fact]
    public void Fingerprint_is_stable_across_json_property_order()
    {
        var left = StockPostingFingerprint.Canonicalize("""{"qty":2,"item":"RM1"}""");
        var right = StockPostingFingerprint.Canonicalize("""{"item":"RM1","qty":2}""");
        Assert.Equal(left, right);
        Assert.Equal(StockPostingFingerprint.Hash(left), StockPostingFingerprint.Hash(right));
    }

    [Fact]
    public void Movement_registry_has_consistent_explicit_inverses()
    {
        var registry = new StockMovementRegistry();
        foreach (var movement in registry.All.Where(x => x.InverseCode is not null))
        {
            var inverse = registry.GetRequired(movement.InverseCode!);
            Assert.Equal(-movement.Direction, inverse.Direction);
            Assert.Equal(movement.Code, inverse.InverseCode);
        }
    }

    [Fact]
    public void Contribution_allocator_uses_one_shared_fifo_budget()
    {
        var allocator = new ProductionContributionAllocator();
        var contributions = new[]
        {
            new ProductionContribution(1, 10, new DateTime(2026, 10, 1), 1, 1, 10m)
        };

        var exact = allocator.BuildPlan(contributions,
        [
            new("A", 6m),
            new("B", 4m)
        ]);
        Assert.Equal(10m, exact.Sum(x => x.BaseQty));

        var ex = Assert.Throws<StockLedgerException>(() => allocator.BuildPlan(contributions,
        [
            new("A", 6m),
            new("B", 6m)
        ]));
        Assert.Equal(StockLedgerErrorCodes.InsufficientBaseQty, ex.Error.Code);
    }

    [Fact]
    public async Task Production_writer_rolls_fact_and_envelope_back_atomically()
    {
        await ActivateAsync();
        var writer = new ProductionStockWriter(new StockMovementRegistry());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateCoordinator().ExecuteAsync<int>(Command(Guid.NewGuid()), async (context, ct) =>
            {
                var lot = new ProductionBalLot
                {
                    Uid = 999, CompanyCode = "DEMO", BranchCode = "HQ", Kind = "WIP",
                    ItemCode = "WIP-1", Qty = 10m, Uom = "EA", BaseQty = 10m,
                    BaseUom = "EA", ConversionFactorToBase = 1m,
                    WorkOrderId = 1, WorkOrderNo = "WO-1", LotNo = "POOL-1"
                };
                await writer.ApplyAsync(context,
                [
                    new(lot, "CONSUME", 4m, 4m, 1, null, null, null, 1,
                        "OUTPUT", "OUT-1", "LINE-1", 0)
                ], ct);
                throw new InvalidOperationException("fault injection");
#pragma warning disable CS0162
                return 0;
#pragma warning restore CS0162
            }));

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Empty(await verify.ProductionBalLotMovements.ToListAsync());
        Assert.Empty(await verify.StockPostings.ToListAsync());
    }

    [Fact]
    public async Task Coordinator_stays_disabled_without_active_epoch()
    {
        var coordinator = CreateCoordinator();
        var called = false;
        var result = await coordinator.ExecuteAsync(Command(Guid.NewGuid()), (_, _) =>
        {
            called = true;
            return Task.FromResult(1);
        });

        Assert.False(result.Succeeded);
        Assert.False(result.LedgerEnabled);
        Assert.Equal(StockLedgerErrorCodes.LedgerDisabled, result.Error!.Code);
        Assert.False(called);
    }

    [Fact]
    public async Task Coordinator_seals_once_replays_same_fingerprint_and_rejects_reuse()
    {
        await ActivateAsync();
        var coordinator = CreateCoordinator();
        var requestId = Guid.NewGuid();
        var command = Command(requestId);
        var calls = 0;

        var first = await coordinator.ExecuteAsync(command, (_, _) =>
            Task.FromResult(++calls));
        var replay = await coordinator.ExecuteAsync(command, (_, _) =>
            Task.FromResult(++calls));
        var changed = command with
        {
            Evidence = StockPostingFingerprint.Create(new { command = "TEST", qty = 2 }, new { line = 1 })
        };
        var rejected = await coordinator.ExecuteAsync(changed, (_, _) =>
            Task.FromResult(++calls));

        Assert.True(first.Succeeded);
        Assert.False(first.WasReplay);
        Assert.True(replay.Succeeded);
        Assert.True(replay.WasReplay);
        Assert.Equal(first.StockPostingId, replay.StockPostingId);
        Assert.Equal(1, calls);
        Assert.Equal(StockLedgerErrorCodes.RequestIdReused, rejected.Error!.Code);

        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.StockPostings.SingleAsync();
        Assert.NotNull(stored.SealedAtUtc);
        Assert.Equal(1, stored.PostingSequence);
    }

    [Fact]
    public async Task Coordinator_rejects_cross_tenant_reversal_id()
    {
        await ActivateAsync();
        long foreignPostingId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var foreignEpoch = new StockLedgerEpoch
            {
                CompanyCode = "OTHER",
                BranchCode = "B1",
                EffectiveFrom = DateTime.UtcNow,
                Version = 2,
                Status = StockLedgerEpochStatuses.Active,
                MigrationBatchId = Guid.NewGuid(),
                ReconciliationManifestHash = new string('B', 64)
            };
            db.StockLedgerEpochs.Add(foreignEpoch);
            await db.SaveChangesAsync();
            var foreign = Posting("OTHER", "B1", foreignEpoch.Id, 1, Guid.NewGuid());
            db.StockPostings.Add(foreign);
            await db.SaveChangesAsync();
            foreignPostingId = foreign.Id;
        }

        var result = await CreateCoordinator().ExecuteAsync(
            Command(Guid.NewGuid()) with { ReversesPostingId = foreignPostingId },
            (_, _) => Task.FromResult(1));

        Assert.False(result.Succeeded);
        Assert.Equal(StockLedgerErrorCodes.InvalidStockIdentity, result.Error!.Code);
    }

    private StockPostingCoordinator CreateCoordinator() => new(
        _factory,
        new Tenant(),
        new BranchStockTransactionLock(),
        new StockPeriodGuard(),
        new NoActiveStockFreezeGuard());

    private async Task ActivateAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.StockLedgerEpochs.Add(new StockLedgerEpoch
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            EffectiveFrom = new DateTime(2026, 10, 1),
            Version = 2,
            Status = StockLedgerEpochStatuses.Active,
            MigrationBatchId = Guid.NewGuid(),
            ReconciliationManifestHash = new string('A', 64)
        });
        await db.SaveChangesAsync();
    }

    private static StockPostingCommand Command(Guid requestId) => new()
    {
        RequestId = requestId,
        CommandType = "TEST",
        SourceModule = "TEST",
        SourceDocumentType = "FIXTURE",
        SourceDocumentId = "1",
        SourceDocumentNo = "T-1",
        DocumentRevision = 1,
        EffectiveAt = new DateTime(2026, 10, 2, 8, 0, 0),
        Evidence = StockPostingFingerprint.Create(new { command = "TEST", qty = 1 }, new { line = 1 })
    };

    private static StockPosting Posting(
        string company, string branch, long epochId, long sequence, Guid requestId) => new()
    {
        CompanyCode = company,
        BranchCode = branch,
        LedgerEpochId = epochId,
        PostingSequence = sequence,
        RequestId = requestId,
        CommandType = "FOREIGN",
        RequestFingerprint = new string('C', 64),
        SourceModule = "TEST",
        SourceDocumentType = "FOREIGN",
        SourceDocumentId = requestId.ToString("N"),
        SourceDocumentNo = "FOREIGN",
        DocumentRevision = 1,
        PostingRole = "PRIMARY",
        SourceSnapshotJson = "{}",
        SourceSnapshotHash = new string('D', 64),
        SourceSnapshotSchemaVersion = 1,
        EffectiveAt = DateTime.UtcNow,
        BusinessDate = DateTime.UtcNow.Date,
        PeriodKey = "2026-10",
        PostedAtUtc = DateTime.UtcNow,
        PostedBy = "test",
        SealedAtUtc = DateTime.UtcNow
    };

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private sealed class Tenant : IInventoryTenantContext
    {
        private static readonly InventoryTenantScope Scope = new()
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            LocationCode = "SITE",
            UserId = "tester"
        };
        public InventoryTenantScope? TryCompanyScope() => Scope;
        public InventoryTenantScope? TryBranchScope() => Scope;
        public InventoryTenantScope? TryWriteScope() => Scope;
    }

    private sealed class LocalFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
