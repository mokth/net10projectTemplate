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

namespace ErpWeb.Tests.Other;
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
        var valuation = db.Model.FindEntityType(typeof(StockValuationFact))!;
        Assert.Contains(valuation.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(x => x.Name)
                .SequenceEqual(["StockPostingId", "PostingLineNo", "SplitOrdinal"]));
        Assert.Equal(typeof(int?), valuation.FindProperty(nameof(StockValuationFact.InventoryHistoryId))!.ClrType);
        Assert.Equal(typeof(int?), valuation.FindProperty(nameof(StockValuationFact.FromBalLocId))!.ClrType);
        Assert.Equal(typeof(int?), valuation.FindProperty(nameof(StockValuationFact.LotId))!.ClrType);
        Assert.Equal(typeof(long), valuation.FindProperty(nameof(StockValuationFact.StockPostingId))!.ClrType);
        Assert.NotNull(db.Model.FindEntityType(typeof(StockCostState)));
        Assert.NotNull(db.Model.FindEntityType(typeof(StockValuationPeriodSnapshotHdr)));
        Assert.NotNull(db.Model.FindEntityType(typeof(StockCountCostBasis)));
        Assert.NotNull(db.Model.FindEntityType(typeof(StockCountReplayGeneration)));
        Assert.NotNull(db.Model.FindEntityType(typeof(StockReplayRun)));
        Assert.NotNull(db.Model.FindEntityType(typeof(StockReplayEffect)));
        Assert.NotNull(db.Model.FindEntityType(typeof(ProductionCostRestatementFact)));

        var generation = db.Model.FindEntityType(typeof(StockCountReplayGeneration))!;
        Assert.Contains(generation.GetIndexes(), index =>
            index.Properties.Select(x => x.Name).SequenceEqual(
                ["CompanyCode", "BranchCode", "EffectiveAt", "AnchorPostingSequence", "StockCountLineId"]));

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
    public async Task Count_generation_reader_validates_anchor_and_adjustment_history()
    {
        // SQL Server stores decimal quantities numerically; the SQLite test model stores
        // them as text, so its copied SQL Server decimal CHECK expression is not portable.
        await using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA ignore_check_constraints = ON";
            await pragma.ExecuteNonQueryAsync();
        }
        await ActivateAsync();
        await using var db = await _factory.CreateDbContextAsync();
        var epochId = await db.StockLedgerEpochs.Select(x => x.Id).SingleAsync();
        var countDate = new DateTime(2026, 10, 10);
        var header = new IvStockCountHdr
        {
            CompanyCode = "DEMO", BranchCode = "HQ", CountNo = "CC000001",
            CountDate = countDate, Status = "POSTED"
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = 7,
            ICode = "RM1", IStatus = "ACTIVE", PhysicalQty = 4m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();

        var posting = Posting("DEMO", "HQ", epochId, 1, Guid.NewGuid());
        posting.SourceModule = "INVENTORY";
        posting.SourceDocumentType = "IV_STOCK_COUNT";
        posting.SourceDocumentId = header.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        posting.SourceDocumentNo = header.CountNo;
        posting.EffectiveAt = StockReplayEventKey.CountEndOfDay(countDate);
        posting.BusinessDate = countDate;
        db.StockPostings.Add(posting);
        await db.SaveChangesAsync();

        db.StockCountReplayGenerations.Add(new StockCountReplayGeneration
        {
            CompanyCode = "DEMO", BranchCode = "HQ", StockCountLineId = line.Id,
            AnchorPostingId = posting.Id, WritePostingId = posting.Id,
            AnchorPostingSequence = posting.PostingSequence,
            EffectiveAt = posting.EffectiveAt,
            SignedAdjustmentQty = 0m, CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var active = await StockCountGenerationResolver.ResolveActiveAsync(
            db, "DEMO", "HQ", [line.Id]);
        Assert.Single(active);
        Assert.Equal(0m, active[line.Id].SignedAdjustmentQty);

        db.IvStockMasters.Add(new IvStockMaster { CompanyCode = "DEMO", ICode = "RM2" });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN"
        });
        var balance = new IvBalLoc
        {
            CompanyCode = "DEMO", BranchCode = "HQ", ICode = "RM2", WhCode = "MAIN",
            LocCode = "", LotNo = "", IStatus = "ACTIVE", StdQty = 4m
        };
        db.IvBalLocs.Add(balance);
        var adjustmentLine = new IvStockCountLine
        {
            StockCountId = header.Id, LineNumber = 2, BalLocId = 8,
            ICode = "RM2", IStatus = "ACTIVE", PhysicalQty = 4m
        };
        db.IvStockCountLines.Add(adjustmentLine);
        await db.SaveChangesAsync();
        adjustmentLine.BalLocId = balance.Id;
        var history = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 41, TrxLineNo = 1,
            TrxDtTime = countDate, TrxType = "ADJ", BatchStatus = "POSTED",
            ICode = "RM2", ToBalLocId = balance.Id, ToStdQty = 4m,
            LedgerVersion = 2, LedgerEpochId = epochId, StockPostingId = posting.Id,
            PostingLineNo = 1, DocumentRevision = 1, EntryRole = "ORIGINAL"
        };
        db.IvTrxHistories.Add(history);
        await db.SaveChangesAsync();
        db.StockCountReplayGenerations.Add(new StockCountReplayGeneration
        {
            CompanyCode = "DEMO", BranchCode = "HQ", StockCountLineId = adjustmentLine.Id,
            AnchorPostingId = posting.Id, WritePostingId = posting.Id,
            AnchorPostingSequence = posting.PostingSequence, EffectiveAt = posting.EffectiveAt,
            SignedAdjustmentQty = 4m, AdjustmentBatchNo = 41,
            AdjustmentHistoryId = history.Id, CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        active = await StockCountGenerationResolver.ResolveActiveAsync(
            db, "DEMO", "HQ", [line.Id, adjustmentLine.Id]);
        Assert.Equal(2, active.Count);
        Assert.Equal(4m, active[adjustmentLine.Id].SignedAdjustmentQty);

        var ordinaryPosting = Posting("DEMO", "HQ", epochId, 2, Guid.NewGuid());
        ordinaryPosting.SourceModule = "INVENTORY";
        ordinaryPosting.SourceDocumentType = "MR";
        ordinaryPosting.SourceDocumentId = "101";
        ordinaryPosting.SourceDocumentNo = "MR101";
        ordinaryPosting.EffectiveAt = posting.EffectiveAt;
        ordinaryPosting.BusinessDate = countDate;
        db.StockPostings.Add(ordinaryPosting);
        await db.SaveChangesAsync();
        var ordinaryHistory = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 42, TrxLineNo = 1,
            TrxDtTime = ordinaryPosting.EffectiveAt, TrxType = "MR", BatchStatus = "POSTED",
            ICode = "RM2", ToBalLocId = balance.Id, ToStdQty = 1m,
            LedgerVersion = 2, LedgerEpochId = epochId, StockPostingId = ordinaryPosting.Id,
            PostingLineNo = 1, DocumentRevision = 1, EntryRole = "ORIGINAL"
        };
        db.IvTrxHistories.Add(ordinaryHistory);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var earlier = new StockReplayEventKey(countDate.AddDays(-1),
            StockReplayEventKey.OrdinaryClass, 1, "candidate", 0);
        var tail = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, "DEMO", "HQ", balance.Id, earlier);
        Assert.Equal(2, tail.Count);
        Assert.Equal(StockReplayEventKey.OrdinaryClass, tail[0].Key.ClassPriority);
        Assert.Equal(StockReplayEventKey.CountClass, tail[1].Key.ClassPriority);
        Assert.Equal(4m, tail[1].AbsoluteCountQty);
        Assert.Equal(-active[adjustmentLine.Id].Id, tail[1].EventId);

        var rollbackPosting = Posting("DEMO", "HQ", epochId, 3, Guid.NewGuid());
        rollbackPosting.SourceModule = "INVENTORY";
        rollbackPosting.SourceDocumentType = "MR_ROLLBACK";
        rollbackPosting.SourceDocumentId = "101";
        rollbackPosting.SourceDocumentNo = "MR101";
        rollbackPosting.EffectiveAt = countDate.AddDays(1);
        db.StockPostings.Add(rollbackPosting);
        await db.SaveChangesAsync();
        db.IvTrxHistories.Add(new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 42, TrxLineNo = 1,
            TrxDtTime = rollbackPosting.EffectiveAt, TrxType = "MR", BatchStatus = "POSTED",
            ICode = "RM2", FromBalLocId = balance.Id, FrStdQty = 1m,
            LedgerVersion = 2, LedgerEpochId = epochId, StockPostingId = rollbackPosting.Id,
            PostingLineNo = 1, DocumentRevision = 2, EntryRole = "REVERSAL",
            ReversesHistoryId = ordinaryHistory.Id
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        tail = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, "DEMO", "HQ", balance.Id, earlier);
        Assert.Single(tail);
        Assert.Equal(StockReplayEventKey.CountClass, tail[0].Key.ClassPriority);

        db.StockPostingBranchSequences.Add(new StockPostingBranchSequence
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LastSequence = 3,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var candidate = new StockReplayPhysicalEvent(10_000,
            new StockReplayEventKey(countDate.AddDays(-1).AddHours(12),
                StockReplayEventKey.OrdinaryClass, 4, "candidate", 0), 2m);
        var plan = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            db, "DEMO", "HQ", balance.Id, candidate);
        Assert.Equal(3, plan.BranchWatermark);
        Assert.Equal(0m, plan.Projection.HistoricalQuantityBeforeCandidate);
        Assert.Equal(4m, plan.Projection.FinalQuantity);
        Assert.Equal(64, plan.PlanHash.Length);

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var coordinator = CreateCoordinator();
            var begin = await coordinator.BeginInTransactionAsync(db,
                Command(Guid.NewGuid()) with { EffectiveAt = candidate.Key.EffectiveAt });
            Assert.NotNull(begin.Context);
            await StockReplayPhysicalPlanner.ValidateWithinRootAsync(begin.Context!, plan);
            balance = await db.IvBalLocs.SingleAsync(x => x.Id == balance.Id);
            balance.StdQty = 5m;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var stale = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayPhysicalPlanner.ValidateWithinRootAsync(begin.Context!, plan));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument, stale.Error.Code);
            Assert.True(stale.Error.Retryable);
            await coordinator.AbortInTransactionAsync(begin.Context!);
            await transaction.RollbackAsync();
        }

        history = await db.IvTrxHistories.SingleAsync(x => x.Id == history.Id);
        history.ToStdQty = 3m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var historyError = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockCountGenerationResolver.ResolveActiveAsync(db, "DEMO", "HQ", [adjustmentLine.Id]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, historyError.Error.Code);

        posting = await db.StockPostings.SingleAsync(x => x.Id == posting.Id);
        posting.SourceDocumentNo = "DIFFERENT";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var error = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockCountGenerationResolver.ResolveActiveAsync(db, "DEMO", "HQ", [line.Id]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, error.Error.Code);
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
    public async Task Caller_owned_abort_clears_context_and_rollback_removes_unsealed_root()
    {
        await ActivateAsync();
        var coordinator = CreateCoordinator();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var begin = await coordinator.BeginInTransactionAsync(db, Command(Guid.NewGuid()));
            Assert.NotNull(begin.Context);
            await coordinator.AbortInTransactionAsync(begin.Context!);
            await coordinator.AbortInTransactionAsync(begin.Context!);
            await transaction.RollbackAsync();
        }

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Empty(await verify.StockPostings.ToListAsync());
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
