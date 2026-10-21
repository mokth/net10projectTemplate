using System.Data.Common;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace ErpWeb.Tests.Production.Transaction;
[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionStockLedgerP0Tests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProductionStockLedgerP0Tests()
    {
        _connection.Open();
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new SqliteUnicodeLiteralInterceptor())
            .Options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        ProductionLedgerTestFixture.SeedActiveEpochAsync(db).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Replay_restatement_writer_reverses_and_replaces_a_verified_movement()
    {
        var fixture = await SeedOutputAsync(
            new MaterialSpec("WIP-A", 10m, "EA", 1m));
        await SeedLotAsync(fixture, "WIP-A", 10m, "EA", 10m, 1m);
        var posted = await CreateService().PostAsync(fixture.OutputId);
        Assert.True(posted.Succeeded, posted.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var epoch = await db.StockLedgerEpochs.SingleAsync();
        var movement = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.MovementType == ProductionBalLotMovementTypes.Consume);
        Assert.Equal(10m, movement.BaseQty);
        Assert.Equal(0m, movement.TotalCost);
        var owner = await db.StockPostings.SingleAsync(p =>
            p.Id == movement.StockPostingId);
        Assert.NotNull(owner.SealedAtUtc);
        var ownerAt = owner.EffectiveAt;
        var rootAt = ownerAt.AddDays(-1);
        var root = new StockPosting
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerEpochId = epoch.Id,
            PostingSequence = owner.PostingSequence + 1,
            RequestId = Guid.NewGuid(), CommandType = "PRODUCTION_RESTATEMENT_TEST",
            RequestFingerprint = new string('A', 64),
            SourceModule = "PRODUCTION", SourceDocumentType = "TEST",
            SourceDocumentId = "2", SourceDocumentNo = "2", DocumentRevision = 1,
            PostingRole = "PRIMARY", SourceSnapshotJson = "{}",
            SourceSnapshotHash = new string('B', 64),
            EffectiveAt = rootAt, BusinessDate = rootAt.Date,
            PeriodKey = "2026-10", PostedAtUtc = DateTime.UtcNow,
            PostedBy = "test"
        };
        db.StockPostings.Add(root);
        await db.SaveChangesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var context = new StockPostingContext(db, epoch, root, "test");
        var original = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            "DEMO", "HQ", [movement.Uid]))[movement.Uid];
        var noRun = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionCostRestatementWriter.AppendWithinRootAsync(context,
                original, 12m, 1.2m));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            noRun.Error.Code);
        db.StockReplayRuns.Add(new StockReplayRun
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            RootStockPostingId = root.Id, RootRequestId = root.RequestId,
            RootDocumentType = "TEST", RootDocumentId = "2",
            EarliestEffectiveAt = rootAt, LatestEffectiveAt = ownerAt,
            PlanHash = new string('C', 64),
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = "test"
        });
        await db.SaveChangesAsync();
        owner.SealedAtUtc = null;
        await db.SaveChangesAsync();
        var unsealedOwner = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionCostRestatementWriter.AppendWithinRootAsync(context,
                original, 12m, 1.2m));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            unsealedOwner.Error.Code);
        owner.SealedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var first = await ProductionCostRestatementWriter
            .AppendWithinRootAsync(context, original, 12m, 1.2m);
        Assert.Null(first.Reversal);
        Assert.NotNull(first.Replacement);
        var firstEffect = await db.StockReplayEffects.SingleAsync();
        Assert.Equal(owner.Id, firstEffect.BusinessOwnerPostingId);
        Assert.Equal(first.Replacement!.Id, firstEffect.AffectedEntityId);
        Assert.Equal(movement.Uid, firstEffect.OriginalEntityId);
        var current = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            "DEMO", "HQ", [movement.Uid]))[movement.Uid];
        Assert.Equal(12m, current.TotalCost);
        var stale = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionCostRestatementWriter.AppendWithinRootAsync(context,
                original, 15m, 1.5m));
        Assert.Equal(StockLedgerErrorCodes.StaleDocument,
            stale.Error.Code);
        Assert.Single(await db.ProductionCostRestatementFacts.ToArrayAsync());

        var second = await ProductionCostRestatementWriter
            .AppendWithinRootAsync(context, current, 15m, 1.5m);
        Assert.NotNull(second.Reversal);
        Assert.Equal(first.Replacement!.Id,
            second.Reversal.ReversesRestatementFactId);
        Assert.NotNull(second.Replacement);
        Assert.Equal(3, await db.StockReplayEffects.CountAsync());
        current = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            "DEMO", "HQ", [movement.Uid]))[movement.Uid];
        Assert.Equal(15m, current.TotalCost);
        var restored = await ProductionCostRestatementWriter
            .AppendWithinRootAsync(context, current, 0m, 0m);
        Assert.NotNull(restored.Reversal);
        Assert.Null(restored.Replacement);
        Assert.Equal(4, await db.StockReplayEffects.CountAsync());
        current = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            "DEMO", "HQ", [movement.Uid]))[movement.Uid];
        Assert.Equal(original, current);
        db.StockPostings.Add(new StockPosting
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerEpochId = epoch.Id,
            PostingSequence = root.PostingSequence + 1,
            RequestId = Guid.NewGuid(), CommandType = "PRODUCTION_RESTATEMENT_TEST_REVERSAL",
            RequestFingerprint = new string('D', 64),
            SourceModule = "PRODUCTION", SourceDocumentType = "TEST",
            SourceDocumentId = "2", SourceDocumentNo = "2", DocumentRevision = 1,
            PostingRole = "REVERSAL", SourceSnapshotJson = "{}",
            SourceSnapshotHash = new string('E', 64),
            EffectiveAt = ownerAt, BusinessDate = ownerAt.Date,
            PeriodKey = "2026-10", PostedAtUtc = DateTime.UtcNow,
            PostedBy = "test", ReversesPostingId = owner.Id,
            SealedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var reversedOwner = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionCostRestatementWriter.AppendWithinRootAsync(context,
                original, 12m, 1.2m));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            reversedOwner.Error.Code);
        await transaction.RollbackAsync();
        db.ChangeTracker.Clear();
        Assert.Empty(await db.ProductionCostRestatementFacts.ToArrayAsync());
        Assert.Empty(await db.StockReplayEffects.ToArrayAsync());
    }

    [Fact]
    public async Task Shared_lot_budget_rejects_two_demands_that_exceed_one_pool()
    {
        var fixture = await SeedOutputAsync(
            new MaterialSpec("WIP-A", 6m, "EA", 1m),
            new MaterialSpec("WIP-A", 6m, "EA", 1m));
        await SeedLotAsync(fixture, "WIP-A", qty: 10m, uom: "EA", baseQty: 10m, factor: 1m);

        var result = await CreateService().PostAsync(fixture.OutputId);

        Assert.False(result.Succeeded);
        Assert.Contains("Insufficient production balance", result.Message, StringComparison.OrdinalIgnoreCase);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(10m, (await db.ProductionBalLots.SingleAsync()).BaseQty);
        Assert.Empty(await db.ProductionMaterialMovements.ToListAsync());
        Assert.Equal(ProductionOutputStatuses.New, (await db.ProductionOutputs.SingleAsync()).Status);
    }

    [Fact]
    public async Task Shared_lot_budget_allows_exact_depletion_across_two_demands()
    {
        var fixture = await SeedOutputAsync(
            new MaterialSpec("WIP-A", 6m, "EA", 1m),
            new MaterialSpec("WIP-A", 4m, "EA", 1m));
        await SeedLotAsync(fixture, "WIP-A", qty: 10m, uom: "EA", baseQty: 10m, factor: 1m);

        var result = await CreateService().PostAsync(fixture.OutputId);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var lot = await db.ProductionBalLots.SingleAsync();
        Assert.Equal(0m, lot.Qty);
        Assert.Equal(0m, lot.BaseQty);
        Assert.Equal(10m, await db.ProductionMaterialMovements.SumAsync(x => x.BaseQty));
    }

    [Fact]
    public async Task Exhausted_first_lot_does_not_create_a_zero_quantity_leg()
    {
        var fixture = await SeedOutputAsync(
            new MaterialSpec("WIP-A", 6m, "EA", 1m),
            new MaterialSpec("WIP-A", 4m, "EA", 1m));
        await SeedLotAsync(fixture, "WIP-A", 5m, "EA", 5m, 1m, "LOT-A");
        await SeedLotAsync(fixture, "WIP-A", 5m, "EA", 5m, 1m, "LOT-B");

        var result = await CreateService().PostAsync(fixture.OutputId);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var legs = await db.ProductionBalLotMovements
            .Where(x => x.MovementType == ProductionBalLotMovementTypes.Consume)
            .OrderBy(x => x.Uid).ToListAsync();
        Assert.Equal(new[] { 5m, 1m, 4m }, legs.Select(x => x.BaseQty));
        Assert.All(legs, x => Assert.True(x.Qty > 0m && x.BaseQty > 0m));
        Assert.Equal(3, await db.ProductionMaterialMovements.CountAsync());
    }

    [Fact]
    public async Task Consumption_uses_balance_owned_uom_for_lot_projection()
    {
        var fixture = await SeedOutputAsync(new MaterialSpec("WIP-A", 1m, "BOX", 10m));
        await SeedLotAsync(fixture, "WIP-A", qty: 10m, uom: "EA", baseQty: 10m, factor: 1m);

        var result = await CreateService().PostAsync(fixture.OutputId);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var lot = await db.ProductionBalLots.SingleAsync();
        Assert.Equal(0m, lot.Qty);
        Assert.Equal(0m, lot.BaseQty);
        var materialMovement = await db.ProductionMaterialMovements.SingleAsync();
        Assert.Equal(1m, materialMovement.Qty);
        Assert.Equal("BOX", materialMovement.Uom);
        var balanceMovement = await db.ProductionBalLotMovements.SingleAsync();
        Assert.Equal(10m, balanceMovement.Qty);
        Assert.Equal("EA", balanceMovement.Uom);
    }

    [Fact]
    public async Task Posting_flushes_all_facts_before_recomputing_consumed_aggregate()
    {
        var fixture = await SeedOutputAsync(new MaterialSpec("WIP-A", 4m, "EA", 1m));
        await SeedLotAsync(fixture, "WIP-A", qty: 4m, uom: "EA", baseQty: 4m, factor: 1m);

        var result = await CreateService().PostAsync(fixture.OutputId);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(4m, (await db.ProductionWorkOrderMaterials.SingleAsync()).ConsumedQty);
        Assert.Single(await db.ProductionMaterialMovements
            .Where(x => x.MovementType == ProductionMaterialMovementTypes.Consume)
            .ToListAsync());
    }

    [Fact]
    public async Task Rollback_counts_each_persisted_reversal_once_for_multiple_materials()
    {
        var fixture = await SeedOutputAsync(
            new MaterialSpec("WIP-A", 2m, "EA", 1m),
            new MaterialSpec("WIP-B", 3m, "EA", 1m));
        await SeedLotAsync(fixture, "WIP-A", qty: 2m, uom: "EA", baseQty: 2m, factor: 1m);
        await SeedLotAsync(fixture, "WIP-B", qty: 3m, uom: "EA", baseQty: 3m, factor: 1m);
        var sut = CreateService();
        var posted = await sut.PostAsync(fixture.OutputId);
        Assert.True(posted.Succeeded, posted.Message);

        var rolledBack = await sut.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = fixture.OutputId,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "P0 aggregate regression",
        });

        Assert.True(rolledBack.Succeeded, rolledBack.Message);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.All(await db.ProductionWorkOrderMaterials.ToListAsync(), x => Assert.Equal(0m, x.ConsumedQty));
        Assert.Equal(5m, await db.ProductionBalLots.SumAsync(x => x.BaseQty));
        Assert.Equal(2, await db.ProductionMaterialMovements.CountAsync(
            x => x.MovementType == ProductionMaterialMovementTypes.ConsumeReversal));
    }

    private async Task<Fixture> SeedOutputAsync(params MaterialSpec[] materialSpecs)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        var order = new ProductionWorkOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WorkOrderNo = $"WO-P0-{Guid.NewGuid():N}",
            ProductCode = "FG-P0",
            OutputUom = "EA",
            Status = ProductionWorkOrderStatuses.Released,
            PlannedQty = 100m,
            RemainingQty = 100m,
            SnapshotRevision = 1,
            SnapshotHash = new string('A', 64),
            SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
            SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
            DefinitionEffectiveDate = new DateTime(2026, 10, 1),
            PlannedStartDateTime = new DateTime(2026, 10, 1),
            PlannedCompletionDateTime = new DateTime(2026, 10, 2),
            RowVersion = [1],
        };
        var route = new ProductionWorkOrderRouteStep
        {
            WorkOrder = order,
            StageSequence = 10,
            WorkCentreCode = "WC-P0",
            OutputItemCode = "FG-P0",
            OutputType = PrRouteOutputTypes.WipNonstock,
            YieldPercent = 100m,
            OutputUom = "EA",
            OutputBaseUom = "EA",
            OutputConversionFactorToBase = 1m,
            RowVersion = [1],
        };
        var operation = new ProductionWorkOrderOperation
        {
            WorkOrder = order,
            RouteStep = route,
            OperationCode = "OP-P0",
            ProcessSequence = 10,
            ProcessType = "MANUAL",
            PlannedOutputQty = 10m,
            PlannedInputUom = "EA",
            PlannedOutputUom = "EA",
            IsFinalOperation = true,
            RemainingQty = 10m,
            RowVersion = [1],
        };
        route.Operations.Add(operation);
        order.RouteSteps.Add(route);
        order.Operations.Add(operation);
        db.ProductionWorkOrders.Add(order);
        await db.SaveChangesAsync();

        for (var index = 0; index < materialSpecs.Length; index++)
        {
            var spec = materialSpecs[index];
            db.ProductionWorkOrderMaterials.Add(new ProductionWorkOrderMaterial
            {
                WorkOrder = order,
                WorkOrderOperation = operation,
                LineNo = index + 1,
                ComponentCode = spec.ItemCode,
                MfgType = "MAKE",
                BomPath = spec.ItemCode,
                IssueMethod = PrMaterialIssueMethods.Manual,
                SupplySource = PrMaterialSupplySources.InternalRouteWip,
                ProducingRouteStepId = route.Uid,
                RequiredQty = spec.RequiredQty,
                RequiredBaseQty = IvQty.Round(spec.RequiredQty * spec.Factor),
                RequiredUom = spec.Uom,
                BaseUom = "EA",
                ConversionFactorToBase = spec.Factor,
                RowVersion = [1],
            });
        }

        var link = new ProductionPostingLink
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            CommandType = ProductionPostingCommandTypes.OutputPost,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            WorkOrderId = order.Uid,
            ProductionDocumentType = ProductionDocumentTypes.ProductionOutput,
            ProductionDocumentNo = "OUT-P0",
            SnapshotRevision = order.SnapshotRevision,
            SnapshotHash = order.SnapshotHash,
            Status = ProductionPostingLinkStatuses.Draft,
            CreatedDate = new DateTime(2026, 10, 1),
            CreatedBy = "admin",
        };
        var output = new ProductionOutput
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            DocumentNo = "OUT-P0",
            Status = ProductionOutputStatuses.New,
            WorkOrderId = order.Uid,
            RouteStepId = route.Uid,
            WorkOrderOperationId = operation.Uid,
            ProductionDate = new DateTime(2026, 10, 1, 8, 0, 0),
            GoodQty = 10m,
            OutputUom = "EA",
            OutputItemCode = "FG-P0",
            OutputType = PrRouteOutputTypes.WipNonstock,
            OutputLotNo = "LOT-P0",
            SnapshotRevision = order.SnapshotRevision,
            SnapshotHash = order.SnapshotHash,
            CreateRequestId = Guid.NewGuid().ToString("N"),
            CreateRequestFingerprint = new string('A', 64),
            CreatedDate = new DateTime(2026, 10, 1),
            CreatedBy = "admin",
            RowVersion = [1],
        };
        db.ProductionPostingLinks.Add(link);
        db.ProductionOutputs.Add(output);
        await db.SaveChangesAsync();
        link.ProductionDocumentLineId = output.Uid;
        var woMaterials = await db.ProductionWorkOrderMaterials
            .Where(x => x.WorkOrderId == order.Uid)
            .OrderBy(x => x.LineNo)
            .ToListAsync();
        foreach (var material in woMaterials)
        {
            db.ProductionOutputMaterials.Add(new ProductionOutputMaterial
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                ProductionOutputId = output.Uid,
                WorkOrderMaterialId = material.Uid,
                ComponentCode = material.ComponentCode,
                RequiredUom = material.RequiredUom ?? "EA",
                SupplySource = material.SupplySource,
                IssueMethod = material.IssueMethod,
                WoBomRequiredQty = material.RequiredQty,
                ConversionFactorToBase = material.ConversionFactorToBase,
                BaseUom = material.BaseUom,
                StandardQty = material.RequiredQty,
                ConsumeQty = material.RequiredQty,
                CreatedDate = new DateTime(2026, 10, 1),
                CreatedBy = "admin",
                RowVersion = [1],
            });
        }
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        return new Fixture(order.Uid, route.Uid, output.Uid);
    }

    private async Task SeedLotAsync(
        Fixture fixture,
        string itemCode,
        decimal qty,
        string uom,
        decimal baseQty,
        decimal factor,
        string? lotNo = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.ProductionBalLots.Add(new ProductionBalLot
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Kind = ProductionBalLotKinds.Wip,
            ItemCode = itemCode,
            Qty = qty,
            Uom = uom,
            BaseQty = baseQty,
            BaseUom = "EA",
            ConversionFactorToBase = factor,
            WorkOrderId = fixture.OrderId,
            WorkOrderNo = "WO-P0",
            ProducingRouteStepId = fixture.RouteStepId,
            OutputType = PrRouteOutputTypes.WipStocked,
            LotNo = lotNo ?? $"LOT-{itemCode}",
            LastMovementDate = new DateTime(2026, 9, 30),
        });
        await db.SaveChangesAsync();
        var lot = await db.ProductionBalLots.OrderByDescending(x => x.Uid).FirstAsync();
        await ProductionLedgerTestFixture.SeedVerifiedPoolAsync(db, lot);
    }

    private ProductionOutputService CreateService() =>
        ProductionLedgerTestFixture.CreateProductionOutputService(_factory);

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private readonly record struct MaterialSpec(string ItemCode, decimal RequiredQty, string Uom, decimal Factor);
    private readonly record struct Fixture(long OrderId, long RouteStepId, long OutputId);

    private sealed class TestRunningNumberService : IRunningNumberService
    {
        public Task<int> PeekNextAsync(
            AppDbContext db,
            string companyCode,
            string docKey,
            CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<int> GetNextAsync(
            AppDbContext db,
            string companyCode,
            string docKey,
            CancellationToken cancellationToken = default) => Task.FromResult(1);
    }

    private sealed class SqliteUnicodeLiteralInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return ValueTask.FromResult(result);
        }
    }
}
