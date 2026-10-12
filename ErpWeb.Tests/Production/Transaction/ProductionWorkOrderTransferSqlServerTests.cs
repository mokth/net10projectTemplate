using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Core.Settings;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Production.Transaction;

public sealed partial class ProductionWorkOrderTransferSqlServerTests
{
    private static readonly DateTime BusinessTime = new(2026, 10, 12, 9, 0, 0);

    [ProductionStockSqlServerFact]
    public async Task Concurrent_new_drafts_for_one_source_have_one_winner()
    {
        var f = await SqlFixture.CreateAsync();
        var request = new ProductionWorkOrderTransferCreateRequest
        {
            SourceProductionBalLotId = f.SourcePoolId,
            TargetWorkOrderMaterialId = f.TargetMaterialId,
            TransferDateTime = BusinessTime,
        };

        var results = await Task.WhenAll(
            f.CreateTransferService().CreateAsync(request),
            f.CreateTransferService().CreateAsync(request));

        Assert.Single(results, x => x.Succeeded);
        await using var db = await f.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.ProductionWorkOrderTransfers.CountAsync(x => x.CompanyCode == f.Company
            && x.SourceProductionBalLotId == f.SourcePoolId && x.Status == ProductionWorkOrderTransferStatuses.New
            && x.DeletedAtUtc == null));
        Assert.Equal(1, await db.ProductionPostingLinks.CountAsync(x => x.CompanyCode == f.Company
            && x.CommandType == ProductionPostingCommandTypes.WorkOrderTransferPost));
    }

    [ProductionStockSqlServerFact]
    public async Task Concurrent_double_post_creates_one_sealed_posting_and_one_movement_pair()
    {
        var f = await SqlFixture.CreateAsync();
        var draft = await f.CreateDraftAsync(f.SourcePoolId);
        var results = await Task.WhenAll(
            f.CreateTransferService().PostAsync(draft.Id, draft.RowVersion),
            f.CreateTransferService().PostAsync(draft.Id, draft.RowVersion));

        Assert.Single(results, x => x.Succeeded);
        var current = await f.CreateTransferService().GetAsync(draft.Id);
        Assert.True(current.Succeeded, current.Message);
        var replay = await f.CreateTransferService().PostAsync(draft.Id, current.Data!.RowVersion);
        Assert.True(replay.Succeeded, replay.Message);

        await using var db = await f.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.StockPostings.CountAsync(x => x.CompanyCode == f.Company
            && x.SourceDocumentType == ProductionDocumentTypes.WorkOrderTransfer
            && x.SourceDocumentId == draft.Id.ToString()));
        Assert.Equal(2, await db.ProductionBalLotMovements.CountAsync(x => x.CompanyCode == f.Company
            && x.DocumentType == ProductionDocumentTypes.WorkOrderTransfer
            && x.DocumentNo == current.Data!.TransferNo));
        Assert.Equal(ProductionWorkOrderTransferStatuses.Posted, replay.Data!.Status);
        Assert.Equal(0m, (await db.ProductionBalLots.SingleAsync(x => x.Uid == f.SourcePoolId)).Qty);
    }

    [ProductionStockSqlServerFact]
    public async Task Concurrent_transfers_merge_into_one_target_wip_identity()
    {
        var f = await SqlFixture.CreateAsync();
        var secondSourceId = await f.AddSecondSourceAsync();
        var first = await f.CreateDraftAsync(f.SourcePoolId);
        var second = await f.CreateDraftAsync(secondSourceId);

        var posted = await Task.WhenAll(
            f.CreateTransferService().PostAsync(first.Id, first.RowVersion),
            f.CreateTransferService().PostAsync(second.Id, second.RowVersion));

        Assert.All(posted, x => Assert.True(x.Succeeded, x.Message));
        await using var db = await f.Factory.CreateDbContextAsync();
        var targetRows = await db.ProductionBalLots.Where(x => x.CompanyCode == f.Company
            && x.BranchCode == "HQ" && x.WorkOrderId == f.TargetWorkOrderId
            && x.ProducingRouteStepId == f.TargetProducerRouteId && x.ItemCode == "WIP-1"
            && x.LotNo == "LOT-TRANSFER-1").ToListAsync();
        var target = Assert.Single(targetRows);
        Assert.Equal(8.5m, target.Qty);
        Assert.Equal(21.25m, target.BaseQty);
        Assert.Equal(70.246912m, target.TotalCost);
        Assert.True(target.Qty >= 0m && target.BaseQty >= 0m && target.TotalCost >= 0m);
        Assert.Equal(4, await db.ProductionBalLotMovements.CountAsync(x => x.CompanyCode == f.Company
            && x.DocumentType == ProductionDocumentTypes.WorkOrderTransfer));
        Assert.Equal(2, await db.StockPostings.CountAsync(x => x.CompanyCode == f.Company
            && x.SourceDocumentType == ProductionDocumentTypes.WorkOrderTransfer));
        Assert.All(await db.ProductionBalLots.Where(x => x.CompanyCode == f.Company).ToListAsync(), x =>
        {
            Assert.True(x.Qty >= 0m);
            Assert.True(x.BaseQty >= 0m);
            Assert.True(x.TotalCost >= 0m);
        });
    }

    [ProductionStockSqlServerFact]
    public async Task Transfer_racing_daily_production_consumption_keeps_all_pools_nonnegative()
    {
        var f = await SqlFixture.CreateAsync();
        await f.CompleteTargetProducerAsync();
        var draft = await f.CreateDraftAsync(f.SourcePoolId);
        var daily = await f.CreateOutputService().CreateAsync(f.DailyRequest(f.TargetConsumerOperationId));
        Assert.True(daily.Succeeded, daily.Message);

        var transferPost = f.CreateTransferService().PostAsync(draft.Id, draft.RowVersion);
        var outputPost = f.CreateOutputService().PostAsync(daily.Data!.Uid);
        await Task.WhenAll(transferPost, outputPost);

        Assert.True((await transferPost).Succeeded);
        _ = await outputPost;
        await using var db = await f.Factory.CreateDbContextAsync();
        Assert.All(await db.ProductionBalLots.Where(x => x.CompanyCode == f.Company).ToListAsync(), x =>
        {
            Assert.True(x.Qty >= 0m);
            Assert.True(x.BaseQty >= 0m);
            Assert.True(x.TotalCost >= 0m);
        });
        Assert.True(await db.ProductionBalLots.CountAsync(x => x.CompanyCode == f.Company
            && x.WorkOrderId == f.TargetWorkOrderId && x.ProducingRouteStepId == f.TargetProducerRouteId
            && x.ItemCode == "WIP-1" && x.LotNo == "LOT-TRANSFER-1") <= 1);
        Assert.True(await db.StockPostings.CountAsync(x => x.CompanyCode == f.Company
            && (x.SourceDocumentType == ProductionDocumentTypes.WorkOrderTransfer
                || x.SourceDocumentType == ProductionDocumentTypes.ProductionOutput)) <= 2);
    }

    [ProductionStockSqlServerFact]
    public async Task Transfer_racing_natural_production_keeps_a_single_target_wip_pool()
    {
        var f = await SqlFixture.CreateAsync();
        var draft = await f.CreateDraftAsync(f.SourcePoolId);
        var production = await f.CreateOutputService().CreateAsync(f.DailyRequest(f.TargetProducerOperationId,
            outputLotNo: "LOT-TRANSFER-1", goodQty: 1m));
        Assert.True(production.Succeeded, production.Message);

        var transferPost = f.CreateTransferService().PostAsync(draft.Id, draft.RowVersion);
        var outputPost = f.CreateOutputService().PostAsync(production.Data!.Uid);
        await Task.WhenAll(transferPost, outputPost);

        Assert.True((await transferPost).Succeeded);
        _ = await outputPost;
        await using var db = await f.Factory.CreateDbContextAsync();
        var target = await db.ProductionBalLots.Where(x => x.CompanyCode == f.Company
            && x.WorkOrderId == f.TargetWorkOrderId && x.ProducingRouteStepId == f.TargetProducerRouteId
            && x.ItemCode == "WIP-1" && x.LotNo == "LOT-TRANSFER-1").ToListAsync();
        Assert.True(target.Count <= 1);
        Assert.All(await db.ProductionBalLots.Where(x => x.CompanyCode == f.Company).ToListAsync(), x =>
        {
            Assert.True(x.Qty >= 0m);
            Assert.True(x.BaseQty >= 0m);
            Assert.True(x.TotalCost >= 0m);
        });
    }

    private sealed class SqlFixture
    {
        private static readonly SemaphoreSlim SchemaGate = new(1, 1);
        private static bool _schemaReady;
        private readonly IStockPostingCoordinator _coordinator;
        private readonly IRunningNumberService _numbers = new SqlRunningNumberService();
        private readonly IAccessRightService _transferAccess = ProductionLedgerTestFixture.Allow(MenuCodes.PlanningWorkOrderTransfer);
        private readonly IAccessRightService _outputAccess = ProductionLedgerTestFixture.Allow(MenuCodes.PlanningDailyProduction);
        private readonly IInventoryTenantContext _tenant;

        public IDbContextFactory<AppDbContext> Factory { get; }
        public string Company { get; }
        public long SourcePoolId { get; private set; }
        public long SourceWorkOrderId { get; private set; }
        public long TargetWorkOrderId { get; private set; }
        public long TargetMaterialId { get; private set; }
        public long TargetProducerRouteId { get; private set; }
        public long TargetProducerOperationId { get; private set; }
        public long TargetConsumerOperationId { get; private set; }

        private SqlFixture(string connectionString, string company)
        {
            Company = company;
            Factory = new SqlServerDbContextFactory(connectionString);
            _tenant = InventoryTenantTestHelper.CreateTenantContext(company: company, branch: "HQ");
            _coordinator = ProductionLedgerTestFixture.CreateCoordinator(Factory, company, "HQ");
        }

        public static async Task<SqlFixture> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable(ProductionStockTransactionSqlServerTests.ConnectionVariable);
            Assert.False(string.IsNullOrWhiteSpace(connectionString));
            var builder = new SqlConnectionStringBuilder(connectionString);
            Assert.Contains("test", builder.InitialCatalog, StringComparison.OrdinalIgnoreCase);
            await EnsureSchemaAsync(connectionString);
            var fixture = new SqlFixture(connectionString, "T" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
            await fixture.SeedAsync();
            return fixture;
        }

        private static async Task EnsureSchemaAsync(string connectionString)
        {
            await SchemaGate.WaitAsync();
            try
            {
                if (_schemaReady) return;
                await using var db = new SqlServerDbContextFactory(connectionString).CreateDbContext();
                await db.Database.EnsureCreatedAsync();
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 60;
                command.CommandText = File.ReadAllText(FindScript("create-production-work-order-transfer.sql"));
                await command.ExecuteNonQueryAsync();
                _schemaReady = true;
            }
            finally { SchemaGate.Release(); }
        }

        private async Task SeedAsync()
        {
            await using var db = await Factory.CreateDbContextAsync();
            await ProductionLedgerTestFixture.SeedActiveEpochAsync(db, Company, "HQ", BusinessTime.AddDays(-1));

            var source = CreateOrder("WO-SOURCE", ProductionWorkOrderStatuses.InProgress, Company);
            var sourceRoute = CreateRoute(source, "WC-SOURCE", "WIP-1", "BOX");
            var sourceOperation = CreateOperation(source, sourceRoute, "OP-SOURCE", true, "BOX");
            var target = CreateOrder("WO-TARGET", ProductionWorkOrderStatuses.Released, Company);
            var producerRoute = CreateRoute(target, "WC-TARGET-PRODUCER", "WIP-1", "BOX");
            var producerOperation = CreateOperation(target, producerRoute, "OP-PRODUCE", true, "BOX");
            var consumerRoute = CreateRoute(target, "WC-TARGET-PRODUCER", "FG-1", "EA");
            var consumerOperation = CreateOperation(target, consumerRoute, "OP-CONSUME", false, "EA");
            var material = new ProductionWorkOrderMaterial
            {
                WorkOrder = target,
                WorkOrderOperation = consumerOperation,
                ProducingRouteStep = producerRoute,
                ComponentCode = "WIP-1",
                ComponentDescription = "Intermediate WIP",
                MfgType = "INHOUSE",
                IssueMethod = "MANUAL",
                SupplySource = PrMaterialSupplySources.InternalRouteWip,
                RequiredQty = 10m,
                RequiredUom = "BOX",
                RequiredBaseQty = 25m,
                BaseUom = "EA",
                ConversionFactorToBase = 2.5m,
                RowVersion = [1],
            };
            target.Materials.Add(material);
            db.ProductionWorkOrders.AddRange(source, target);
            await db.SaveChangesAsync();

            var sourceLocation = await AddLocationAsync(db, "WC-SOURCE");
            var producerLocation = await AddLocationAsync(db, "WC-TARGET-PRODUCER");
            var sourcePool = CreatePool(source, sourceRoute, sourceOperation, sourceLocation,
                "LOT-TRANSFER-1", 4.25m, 10.625m, 35.123456m);
            db.ProductionBalLots.Add(sourcePool);
            await db.SaveChangesAsync();
            await ProductionLedgerTestFixture.SeedVerifiedPoolAsync(db, sourcePool);

            SourcePoolId = sourcePool.Uid;
            SourceWorkOrderId = source.Uid;
            TargetWorkOrderId = target.Uid;
            TargetMaterialId = material.Uid;
            TargetProducerRouteId = producerRoute.Uid;
            TargetProducerOperationId = producerOperation.Uid;
            TargetConsumerOperationId = consumerOperation.Uid;
            _ = producerLocation;
        }

        public async Task<long> AddSecondSourceAsync()
        {
            await using var db = await Factory.CreateDbContextAsync();
            var order = CreateOrder("WO-SOURCE-2", ProductionWorkOrderStatuses.InProgress, Company);
            var route = CreateRoute(order, "WC-SOURCE-2", "WIP-1", "BOX");
            var operation = CreateOperation(order, route, "OP-SOURCE-2", true, "BOX");
            db.ProductionWorkOrders.Add(order);
            await db.SaveChangesAsync();
            var location = await AddLocationAsync(db, "WC-SOURCE-2");
            var pool = CreatePool(order, route, operation, location, "LOT-TRANSFER-1", 4.25m, 10.625m, 35.123456m);
            db.ProductionBalLots.Add(pool);
            await db.SaveChangesAsync();
            await ProductionLedgerTestFixture.SeedVerifiedPoolAsync(db, pool);
            return pool.Uid;
        }

        public async Task CompleteTargetProducerAsync()
        {
            await using var db = await Factory.CreateDbContextAsync();
            var operation = await db.ProductionWorkOrderOperations.SingleAsync(x => x.Uid == TargetProducerOperationId);
            operation.GoodQty = operation.PlannedOutputQty;
            operation.RemainingQty = 0m;
            await db.SaveChangesAsync();
        }

        public async Task<ProductionWorkOrderTransferDocument> CreateDraftAsync(long sourcePoolId)
        {
            var result = await CreateTransferService().CreateAsync(new ProductionWorkOrderTransferCreateRequest
            {
                SourceProductionBalLotId = sourcePoolId,
                TargetWorkOrderMaterialId = TargetMaterialId,
                TransferDateTime = BusinessTime,
            });
            Assert.True(result.Succeeded, result.Message);
            return result.Data!;
        }

        public ProductionWorkOrderTransferService CreateTransferService() => new(
            Factory, _tenant, _transferAccess, new FixedCurrentDateService(BusinessTime), _numbers,
            new BranchStockTransactionLock(), _coordinator, new ProductionStockWriter(new StockMovementRegistry()));

        public ProductionOutputService CreateOutputService()
        {
            var settings = new Mock<IAppSettingService>();
            settings.Setup(x => x.GetValueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<AppSettingScope>(),
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(IvMasterOperationResult<string?>.Ok(ProductionWipConsumptionPolicies.SharedBomWip));
            return new ProductionOutputService(Factory, _tenant, _outputAccess,
                new FixedCurrentDateService(BusinessTime), _numbers, _coordinator, settings: settings.Object);
        }

        public ProductionOutputCreateRequest DailyRequest(long operationId, string? outputLotNo = null, decimal goodQty = 1m) => new()
        {
            WorkOrderOperationId = operationId,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            ProductionDate = BusinessTime,
            GoodQty = goodQty,
            OutputLotNo = outputLotNo ?? "LOT-DAILY-TRANSFER",
        };

        private async Task<ProductionLocation> AddLocationAsync(AppDbContext db, string code)
        {
            var location = new ProductionLocation
            {
                CompanyCode = Company, BranchCode = "HQ", Code = code,
                Description = code + " production location", WorkCentreCode = code, IsActive = true,
            };
            db.ProductionLocations.Add(location);
            await db.SaveChangesAsync();
            return location;
        }

        private ProductionBalLot CreatePool(ProductionWorkOrder order, ProductionWorkOrderRouteStep route,
            ProductionWorkOrderOperation operation, ProductionLocation location, string lotNo,
            decimal qty, decimal baseQty, decimal value) => new()
        {
            CompanyCode = Company, BranchCode = "HQ", Kind = ProductionBalLotKinds.Wip,
            ItemCode = "WIP-1", Description = "Intermediate WIP", Qty = qty, Uom = "BOX",
            BaseQty = baseQty, BaseUom = "EA", ConversionFactorToBase = 2.5m,
            TotalCost = value, AverageUnitCost = StockLedgerPrecision.Money(value / baseQty),
            WorkOrderId = order.Uid, WorkOrderNo = order.WorkOrderNo,
            WarehouseCode = "", LocationCode = "", LotNo = lotNo,
            BalanceStage = "PROCESS_WIP", ProductionLocationId = location.Id,
            StockStatusCode = "AVAILABLE", PhysicalLotNo = "PHY-TRANSFER-1", PoolCode = "POOL-TRANSFER-1",
            LotIdentityKind = "PRODUCTION", OriginType = "PRODUCED",
            ProducingRouteStepId = route.Uid, WorkOrderOperationId = operation.Uid,
            OutputType = PrRouteOutputTypes.WipStocked, WorkCentreCode = route.WorkCentreCode,
            ProcessCode = operation.OperationCode,
            LastMovementDate = BusinessTime.AddDays(-1), LastStockEventEffectiveAt = BusinessTime.AddDays(-1),
        };

        private static ProductionWorkOrder CreateOrder(string number, string status, string company) => new()
        {
            CompanyCode = company, BranchCode = "HQ", WorkOrderNo = number,
            ProductCode = number.Contains("TARGET", StringComparison.Ordinal) ? "FG-TARGET" : "FG-SOURCE",
            ProductDescription = "Transfer SQL test product", OutputUom = "EA", Status = status,
            PlannedQty = 10m, RemainingQty = 10m, SnapshotRevision = 1,
            SnapshotHash = new string('A', 64), SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
            SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current, IsLegacySnapshot = false,
            DefinitionEffectiveDate = BusinessTime.Date,
            PlannedStartDateTime = BusinessTime.Date, PlannedCompletionDateTime = BusinessTime.Date.AddDays(1),
        };

        private static ProductionWorkOrderRouteStep CreateRoute(
            ProductionWorkOrder order, string workCentre, string outputItem, string outputUom)
        {
            var route = new ProductionWorkOrderRouteStep
            {
                WorkOrder = order, StageSequence = order.RouteSteps.Count + 1,
                WorkCentreCode = workCentre, OutputItemCode = outputItem,
                OutputType = PrRouteOutputTypes.WipStocked, YieldPercent = 100m,
                OutputBaseQty = 1m, OutputUom = outputUom, OutputBaseUom = "EA",
                OutputConversionFactorToBase = outputUom == "BOX" ? 2.5m : 1m,
                PlannedQty = 10m,
            };
            order.RouteSteps.Add(route);
            return route;
        }

        private static ProductionWorkOrderOperation CreateOperation(
            ProductionWorkOrder order, ProductionWorkOrderRouteStep route, string code, bool isFinal, string outputUom)
        {
            var operation = new ProductionWorkOrderOperation
            {
                WorkOrder = order, RouteStep = route, OperationCode = code, ProcessType = "MANUAL",
                ProcessSequence = 1, PlannedOutputQty = 10m, PlannedOutputUom = outputUom,
                IsFinalOperation = isFinal, RemainingQty = 10m, GoodQty = 0m,
            };
            route.Operations.Add(operation);
            order.Operations.Add(operation);
            return operation;
        }

        private static string FindScript(string name)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var path = Path.Combine(directory.FullName, "scripts", name);
                if (File.Exists(path)) return path;
                directory = directory.Parent;
            }
            throw new FileNotFoundException($"Cannot locate {name}.");
        }

        private sealed class SqlServerDbContextFactory(string connectionString) : IDbContextFactory<AppDbContext>
        {
            private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(connectionString).Options;

            public AppDbContext CreateDbContext() => new(_options);
            public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(CreateDbContext());
        }

        private sealed class SqlRunningNumberService : IRunningNumberService
        {
            private int _next = 100_000;
            public Task<int> PeekNextAsync(AppDbContext db, string companyCode, string docKey,
                CancellationToken cancellationToken = default) => Task.FromResult(Volatile.Read(ref _next));
            public Task<int> GetNextAsync(AppDbContext db, string companyCode, string docKey,
                CancellationToken cancellationToken = default) => Task.FromResult(Interlocked.Increment(ref _next));
        }
    }
}
