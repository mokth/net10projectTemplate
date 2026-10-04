using ErpWeb.Core.Inventory;
using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Data;
using System.Text.RegularExpressions;

namespace ErpWeb.Tests.Production.Transaction;
[Trait(TestCategories.Name, TestCategories.Production)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public sealed class ProductionStockLedgerSqlServerTests
{
    private static bool RequireSqlServer =>
        string.Equals(
            Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"),
            "1",
            StringComparison.Ordinal);

    [Fact]
    public async Task SqlServer_coordinator_replays_and_rejects_concurrent_duplicate_revision()
    {
        var cs = TryResolveScratch();
        if (cs is null)
            return;

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.EnsureCreatedAsync();

        await using (var db = await factory.CreateDbContextAsync())
        {
            if (!await db.StockLedgerEpochs.AnyAsync(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ"))
            {
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
        }

        var coordinator = new StockPostingCoordinator(
            factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            new BranchStockTransactionLock(),
            new StockPeriodGuard(),
            new NoActiveStockFreezeGuard());
        var requestId = Guid.NewGuid();
        var command = new StockPostingCommand
        {
            RequestId = requestId,
            CommandType = "TEST_SQL",
            SourceModule = "TEST",
            SourceDocumentType = "FIXTURE",
            SourceDocumentId = requestId.ToString("N"),
            SourceDocumentNo = "SQL-1",
            DocumentRevision = 1,
            EffectiveAt = new DateTime(2026, 10, 2, 8, 0, 0),
            Evidence = StockPostingFingerprint.Create(new { command = "TEST_SQL", qty = 1 }, new { line = 1 })
        };

        var first = await coordinator.ExecuteAsync(command, (_, _) => Task.FromResult(1));
        var replay = await coordinator.ExecuteAsync(command, (_, _) => Task.FromResult(2));
        Assert.True(first.Succeeded, first.Error?.Message);
        Assert.True(replay.WasReplay);

        var secondToken = command with
        {
            RequestId = Guid.NewGuid(),
            Evidence = StockPostingFingerprint.Create(new { command = "TEST_SQL", qty = 2 }, new { line = 1 })
        };
        var rejected = await coordinator.ExecuteAsync(secondToken, (_, _) => Task.FromResult(3));
        Assert.Equal(StockLedgerErrorCodes.DocumentAlreadyPosted, rejected.Error!.Code);
    }

    [Fact]
    public async Task SqlServer_ledger_scripts_are_rerunnable_without_replacing_current_movement_constraint()
    {
        var cs = TryResolveScratch();
        if (cs is null)
            return;

        await EnsureLedgerSchemaAsync(cs);

        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        var firstConstraint = await ReadMovementConstraintAsync(connection);
        var firstColumn = await ReadMovementColumnAsync(connection);

        Assert.True(firstConstraint.ObjectId > 0);
        Assert.False(firstConstraint.IsDisabled);
        Assert.False(firstConstraint.IsNotTrusted);
        Assert.Equal("nvarchar", firstColumn.TypeName, ignoreCase: true);
        Assert.True(firstColumn.MaxLength == -1 || firstColumn.MaxLength >= 64);
        Assert.False(firstColumn.IsNullable);
        foreach (var movementType in ExpectedMovementTypes)
            Assert.Contains($"'{movementType}'", firstConstraint.Definition, StringComparison.OrdinalIgnoreCase);

        await ExecuteLedgerSchemaSequenceAsync(connection);

        var secondConstraint = await ReadMovementConstraintAsync(connection);
        var secondColumn = await ReadMovementColumnAsync(connection);
        Assert.Equal(firstConstraint, secondConstraint);
        Assert.Equal(firstColumn, secondColumn);
    }

    [Fact]
    public async Task SqlServer_seal_triggers_reject_existing_and_new_facts_for_a_prepared_branch()
    {
        var cs = TryResolveScratch();
        if (cs is null)
            return;

        await EnsureLedgerSchemaAsync(cs);
        var fixture = await SeedSealFixtureAsync(cs);

        await AssertSqlErrorNumberAsync(() => ExecuteNonQueryAsync(
            cs, "UPDATE dbo.StockPosting SET PostedBy = N'blocked' WHERE Id = @id", ("@id", fixture.PostingId)), 51000);

        await AssertSqlErrorNumberAsync(() => ExecuteNonQueryAsync(
            cs, "UPDATE dbo.PrProductionBalLotMovement SET CreatedBy = N'blocked' WHERE UID = @id", ("@id", fixture.ReceiptMovementId)), 51001);
        await AssertSqlErrorNumberAsync(() => InsertProductionMovementAsync(cs, fixture, "NEW-PROD"), 51001);

        await AssertSqlErrorNumberAsync(() => ExecuteNonQueryAsync(
            cs, "UPDATE dbo.PrMaterialMovement SET CreatedBy = N'blocked' WHERE UID = @id", ("@id", fixture.MaterialMovementId)), 51002);
        await AssertSqlErrorNumberAsync(() => InsertMaterialMovementAsync(cs, fixture, "NEW-MAT"), 51002);

        await AssertSqlErrorNumberAsync(() => ExecuteNonQueryAsync(
            cs, "UPDATE dbo.PrProductionMovementAllocation SET BaseQty = BaseQty WHERE Id = @id", ("@id", fixture.AllocationId)), 51003);
        await AssertSqlErrorNumberAsync(() => InsertAllocationAsync(cs, fixture), 51003);

        await AssertSqlErrorNumberAsync(() => ExecuteNonQueryAsync(
            cs, "UPDATE dbo.IvTrxHistory SET BatchStatus = N'blocked' WHERE ID = @id", ("@id", fixture.HistoryId)), 51004);
        await AssertSqlErrorNumberAsync(() => InsertInventoryHistoryAsync(cs, fixture), 51004);
    }

    [Fact]
    public async Task SqlServer_active_write_guard_returns_51011_without_context_and_allows_same_session_ef_updates()
    {
        var cs = TryResolveScratch();
        if (cs is null)
            return;

        await EnsureLedgerSchemaAsync(cs);
        var fixture = await SeedActiveBalanceFixtureAsync(cs);

        await AssertSqlErrorNumberAsync(() => UpdateInventoryBalanceWithoutContextAsync(cs, fixture), 51011);
        await AssertSqlErrorNumberAsync(() => UpdateProductionBalanceWithoutContextAsync(cs, fixture), 51011);

        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await SetPostingSessionContextAsync(connection, fixture.PostingId);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options;
            await using var db = new AppDbContext(options);
            var inventoryBalance = await db.IvBalLocs.SingleAsync(x => x.Id == fixture.InventoryBalanceId);
            var productionBalance = await db.ProductionBalLots.SingleAsync(x => x.Uid == fixture.ProductionBalanceId);
            inventoryBalance.StdQty += 1m;
            productionBalance.Qty += 1m;
            productionBalance.BaseQty += 1m;
            await db.SaveChangesAsync();
        }
        finally
        {
            await SetPostingSessionContextAsync(connection, null);
        }
    }

    private static readonly string[] ExpectedMovementTypes =
    [
        "OPENING_IN", "ISSUE", "ISSUE_REVERSAL", "PRODUCE", "PRODUCE_REVERSAL", "CONSUME",
        "CONSUME_REVERSAL", "RETURN", "RETURN_REVERSAL", "TRANSFER_OUT", "TRANSFER_IN",
        "STATUS_OUT", "STATUS_IN", "ADJUST_IN", "ADJUST_OUT", "SCRAP_OUT", "FG_RECEIPT_OUT"
    ];

    private static async Task EnsureLedgerSchemaAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
        await using (var db = new AppDbContext(options))
            await db.Database.EnsureCreatedAsync();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteLedgerSchemaSequenceAsync(connection);
    }

    private static async Task ExecuteLedgerSchemaSequenceAsync(SqlConnection connection)
    {
        foreach (var path in new[]
                 {
                     "scripts/create-stock-posting-ledger.sql",
                     "scripts/alter-inventory-history-ledger.sql",
                     "scripts/alter-production-stock-ledger.sql",
                     "scripts/create-stock-ledger-write-guard.sql"
                 })
        {
            await ExecuteScriptAsync(connection, path);
        }
    }

    private static async Task ExecuteScriptAsync(SqlConnection connection, string relativePath)
    {
        foreach (var batch in SplitSqlBatchesOnGo(ReadScript(relativePath)))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static IEnumerable<string> SplitSqlBatchesOnGo(string script) =>
        Regex.Split(script, @"(?im)^[\t ]*GO[\t ]*(?:\r?\n|$)")
            .Where(batch => !string.IsNullOrWhiteSpace(batch));

    private static string ReadScript(string relativePath) =>
        File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), relativePath));

    private static string ResolveRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }.Distinct())
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ErpWeb.slnx")))
                    return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root containing ErpWeb.slnx.");
    }

    private static async Task<MovementConstraintMetadata> ReadMovementConstraintAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cc.object_id, cc.definition, cc.is_disabled, cc.is_not_trusted
            FROM sys.check_constraints AS cc
            WHERE cc.parent_object_id = OBJECT_ID(N'dbo.PrProductionBalLotMovement')
              AND cc.name = N'CK_PrProductionBalLotMovement_Type';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "MovementType check constraint was not created.");
        return new MovementConstraintMetadata(reader.GetInt32(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3));
    }

    private static async Task<MovementColumnMetadata> ReadMovementColumnAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TYPE_NAME(c.system_type_id), c.max_length, c.is_nullable
            FROM sys.columns AS c
            WHERE c.object_id = OBJECT_ID(N'dbo.PrProductionBalLotMovement')
              AND c.name = N'MovementType';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "MovementType column was not found.");
        return new MovementColumnMetadata(reader.GetString(0), reader.GetInt16(1), reader.GetBoolean(2));
    }

    private static async Task<SealFixture> SeedSealFixtureAsync(string connectionString)
    {
        var company = UniqueCompany("S");
        const string branch = "HQ";
        var token = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new AppDbContext(options);

        var graph = await SeedWorkOrderGraphAsync(db, company, branch, token, includeExecutionHierarchy: true);
        var epoch = new StockLedgerEpoch
        {
            CompanyCode = company, BranchCode = branch, EffectiveFrom = DateTime.UtcNow,
            Version = 2, Status = StockLedgerEpochStatuses.Prepared, MigrationBatchId = Guid.NewGuid(),
            ReconciliationManifestHash = new string('A', 64)
        };
        db.StockLedgerEpochs.Add(epoch);
        await db.SaveChangesAsync();

        var posting = NewPosting(company, branch, epoch.Id, 1, token);
        db.StockPostings.Add(posting);
        var link = new ProductionPostingLink
        {
            CompanyCode = company, BranchCode = branch, CommandType = "SEAL_TEST", PostingRequestId = token,
            WorkOrderId = graph.WorkOrderId, Status = ProductionPostingLinkStatuses.Pending,
            CreatedDate = DateTime.UtcNow, CreatedBy = "tester"
        };
        var balance = new ProductionBalLot
        {
            CompanyCode = company, BranchCode = branch, Kind = "WIP", ItemCode = $"FG-{token}",
            Qty = 10m, Uom = "EA", BaseQty = 10m, BaseUom = "EA", ConversionFactorToBase = 1m,
            WorkOrderId = graph.WorkOrderId, WorkOrderNo = graph.WorkOrderNo,
            WarehouseCode = "WH", LocationCode = "LOC", LotNo = $"LOT-{token}"
        };
        db.ProductionPostingLinks.Add(link);
        db.ProductionBalLots.Add(balance);
        await db.SaveChangesAsync();

        var receipt = NewProductionMovement(company, branch, graph, balance.Uid, link.Uid, posting.Id, "PRODUCE", token, 1);
        var outbound = NewProductionMovement(company, branch, graph, balance.Uid, link.Uid, posting.Id, "CONSUME", token, 2);
        var additionalOutbound = NewProductionMovement(company, branch, graph, balance.Uid, link.Uid, posting.Id, "RETURN", token, 3);
        db.ProductionBalLotMovements.AddRange(receipt, outbound, additionalOutbound);
        await db.SaveChangesAsync();

        var material = NewMaterialMovement(company, branch, graph, balance.Uid, receipt.Uid, link.Uid, posting.Id, token, "BASE");
        var history = new IvTrxHistory
        {
            CompanyCode = company, BranchCode = branch, BatchNo = UniquePositiveInt(), TrxLineNo = 1,
            TrxDtTime = DateTime.UtcNow, TrxType = "TEST", BatchStatus = "POSTED", ICode = $"FG-{token}",
            StockPostingId = posting.Id
        };
        db.ProductionMaterialMovements.Add(material);
        db.IvTrxHistories.Add(history);
        await db.SaveChangesAsync();

        var allocation = new ProductionMovementAllocation
        {
            CompanyCode = company, BranchCode = branch, StockPostingId = posting.Id,
            ReceiptMovementId = receipt.Uid, OutboundMovementId = outbound.Uid,
            BaseQty = 1m, CreatedAtUtc = DateTime.UtcNow
        };
        db.ProductionMovementAllocations.Add(allocation);
        await db.SaveChangesAsync();

        posting.SealedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return new SealFixture(company, branch, posting.Id, graph, balance.Uid, link.Uid, receipt.Uid,
            additionalOutbound.Uid, material.Uid, allocation.Id, history.Id, token);
    }

    private static async Task<ActiveBalanceFixture> SeedActiveBalanceFixtureAsync(string connectionString)
    {
        var company = UniqueCompany("G");
        const string branch = "HQ";
        var token = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new AppDbContext(options);
        var graph = await SeedWorkOrderGraphAsync(db, company, branch, token, includeExecutionHierarchy: false);

        var itemCode = $"RM-{token}";
        var warehouseCode = $"W{token[..6]}";
        var inventoryBalance = new IvBalLoc
        {
            CompanyCode = company, BranchCode = branch, ICode = itemCode, WhCode = warehouseCode,
            LocCode = "LOC", LotNo = "", IStatus = "", StdQty = 10m, StdUom = "EA"
        };
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = company, ICode = itemCode, IDesc = itemCode, StdUom = "EA", MfgType = "BUY", IsActive = true
        });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = company, BranchCode = branch, WarehouseCode = warehouseCode, WarehouseDesc = warehouseCode, IsActive = true
        });
        db.IvBalLocs.Add(inventoryBalance);
        var productionBalance = new ProductionBalLot
        {
            CompanyCode = company, BranchCode = branch, Kind = "WIP", ItemCode = $"FG-{token}",
            Qty = 10m, Uom = "EA", BaseQty = 10m, BaseUom = "EA", ConversionFactorToBase = 1m,
            WorkOrderId = graph.WorkOrderId, WorkOrderNo = graph.WorkOrderNo,
            WarehouseCode = warehouseCode, LocationCode = "LOC", LotNo = $"LOT-{token}"
        };
        db.ProductionBalLots.Add(productionBalance);
        await db.SaveChangesAsync();

        var epoch = new StockLedgerEpoch
        {
            CompanyCode = company, BranchCode = branch, EffectiveFrom = DateTime.UtcNow,
            Version = 2, Status = StockLedgerEpochStatuses.Active, MigrationBatchId = Guid.NewGuid(),
            ReconciliationManifestHash = new string('B', 64)
        };
        db.StockLedgerEpochs.Add(epoch);
        await db.SaveChangesAsync();
        var posting = NewPosting(company, branch, epoch.Id, 1, token);
        db.StockPostings.Add(posting);
        await db.SaveChangesAsync();
        return new ActiveBalanceFixture(inventoryBalance.Id, productionBalance.Uid, posting.Id);
    }

    private static async Task<WorkOrderFixture> SeedWorkOrderGraphAsync(
        AppDbContext db, string company, string branch, string token, bool includeExecutionHierarchy)
    {
        var header = new PrBomHdr
        {
            CompanyCode = company, ProdCode = $"FG-{token}", DefinitionCode = "STD", Version = 1,
            Status = PrBomStatuses.Draft, BaseQty = 1m, BaseUom = "EA", ValidationStatus = PrBomValidationStatuses.Unverified
        };
        db.PrBomHdrs.Add(header);
        await db.SaveChangesAsync();

        var order = new ProductionWorkOrder
        {
            CompanyCode = company, BranchCode = branch, WorkOrderNo = $"WO-{token}", SnapshotHash = new string('C', 64),
            DefinitionEffectiveDate = DateTime.UtcNow.Date, ProductCode = header.ProdCode, OutputUom = "EA",
            SourceDefinitionCode = header.DefinitionCode, SourceBomHdrId = header.Uid, SourceBomVersion = header.Version,
            BomBaseQty = 1m, BomBaseUom = "EA", PlannedQty = 10m, RemainingQty = 10m,
            PlannedStartDateTime = DateTime.UtcNow, PlannedCompletionDateTime = DateTime.UtcNow.AddHours(1),
            SchedulingDirection = ProductionSchedulingDirections.Forward, Status = ProductionWorkOrderStatuses.Draft,
            SourceType = ProductionSourceTypes.Manual
        };
        db.ProductionWorkOrders.Add(order);
        await db.SaveChangesAsync();

        long materialId = 0;
        long operationId = 0;
        if (includeExecutionHierarchy)
        {
            var step = new ProductionWorkOrderRouteStep
            {
                WorkOrderId = order.Uid, StageSequence = 1, WorkCentreCode = "WC", OutputItemCode = header.ProdCode,
                OutputBaseQty = 1m, OutputUom = "EA", PlannedQty = 10m
            };
            db.ProductionWorkOrderRouteSteps.Add(step);
            await db.SaveChangesAsync();
            var operation = new ProductionWorkOrderOperation
            {
                WorkOrderId = order.Uid, RouteStepId = step.Uid, SequenceNo = 1, ProcessSequence = 1,
                ProcessType = "MANUAL", StandardDurationMinutes = 1m, PlannedInputQty = 1m, PlannedInputUom = "EA",
                PlannedOutputQty = 1m, PlannedOutputUom = "EA", PlannedQty = 10m, OperationCode = "OP"
            };
            db.ProductionWorkOrderOperations.Add(operation);
            await db.SaveChangesAsync();
            var material = new ProductionWorkOrderMaterial
            {
                WorkOrderId = order.Uid, WorkOrderOperationId = operation.Uid, LineNo = 1, MaterialSequence = 1,
                BomPath = "/", ComponentCode = $"RM-{token}", MfgType = "BUY", ComponentQtyPerParent = 1m,
                BomOutputQty = 1m, IssueMethod = "MANUAL", SupplySource = "PURCHASED",
                RequiredQty = 1m, RequiredBaseQty = 1m, ConversionFactorToBase = 1m
            };
            db.ProductionWorkOrderMaterials.Add(material);
            await db.SaveChangesAsync();
            materialId = material.Uid;
            operationId = operation.Uid;
        }

        return new WorkOrderFixture(order.Uid, order.WorkOrderNo, materialId, operationId);
    }

    private static StockPosting NewPosting(string company, string branch, long epochId, long sequence, string token) => new()
    {
        CompanyCode = company, BranchCode = branch, LedgerEpochId = epochId, PostingSequence = sequence,
        RequestId = Guid.NewGuid(), CommandType = "SQL_SEAL_TEST", RequestFingerprint = new string('D', 64),
        SourceModule = "TEST", SourceDocumentType = "FIXTURE", SourceDocumentId = token,
        SourceDocumentNo = $"DOC-{token}", DocumentRevision = 1, PostingRole = "PRIMARY",
        SourceSnapshotJson = "{}", SourceSnapshotHash = new string('E', 64), SourceSnapshotSchemaVersion = 1,
        EffectiveAt = DateTime.UtcNow, BusinessDate = DateTime.UtcNow.Date, PeriodKey = "2026-10",
        PostedAtUtc = DateTime.UtcNow, PostedBy = "tester"
    };

    private static ProductionBalLotMovement NewProductionMovement(
        string company, string branch, WorkOrderFixture graph, long balanceId, long linkId, long postingId,
        string type, string token, int ordinal) => new()
    {
        ProductionBalLotId = balanceId, MovementType = type, Qty = 1m, Uom = "EA", BaseQty = 1m, BaseUom = "EA",
        UnitCost = 0m, TotalCost = 0m, WorkOrderId = graph.WorkOrderId, PostingLinkId = linkId,
        CompanyCode = company, BranchCode = branch, StockPostingId = postingId, ItemCode = $"FG-{token}",
        DocumentType = "TEST", DocumentNo = $"PM-{token}-{ordinal}", MovementDate = DateTime.UtcNow,
        CreatedDate = DateTime.UtcNow, CreatedBy = "tester"
    };

    private static ProductionMaterialMovement NewMaterialMovement(
        string company, string branch, WorkOrderFixture graph, long balanceId, long balanceMovementId,
        long linkId, long postingId, string token, string sourceLine) => new()
    {
        CompanyCode = company, BranchCode = branch, WorkOrderId = graph.WorkOrderId,
        WorkOrderMaterialId = graph.MaterialId, WorkOrderOperationId = graph.OperationId,
        MovementType = "CONSUME", MovementDate = DateTime.UtcNow, ItemCode = $"RM-{token}",
        Qty = 1m, Uom = "EA", BaseQty = 1m, BaseUom = "EA", ConversionFactorToBase = 1m,
        WarehouseCode = "WH", LocationCode = "LOC", LotNo = "", ItemStatus = "",
        ProductionBalLotId = balanceId, ProductionBalLotMovementId = balanceMovementId,
        UnitCost = 0m, TotalCost = 0m, PostingLinkId = linkId, StockPostingId = postingId,
        SourceLineId = sourceLine, SplitOrdinal = 0, CreatedDate = DateTime.UtcNow, CreatedBy = "tester"
    };

    private static async Task InsertProductionMovementAsync(string connectionString, SealFixture fixture, string documentNo)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new AppDbContext(options);
        var movement = NewProductionMovement(fixture.Company, fixture.Branch, fixture.Graph,
            fixture.BalanceId, fixture.PostingLinkId, fixture.PostingId, "PRODUCE", fixture.Token, 99);
        movement.DocumentNo = documentNo;
        db.ProductionBalLotMovements.Add(movement);
        await db.SaveChangesAsync();
    }

    private static async Task InsertMaterialMovementAsync(string connectionString, SealFixture fixture, string sourceLine)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new AppDbContext(options);
        db.ProductionMaterialMovements.Add(NewMaterialMovement(fixture.Company, fixture.Branch, fixture.Graph,
            fixture.BalanceId, fixture.ReceiptMovementId, fixture.PostingLinkId, fixture.PostingId, fixture.Token, sourceLine));
        await db.SaveChangesAsync();
    }

    private static async Task InsertAllocationAsync(string connectionString, SealFixture fixture)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new AppDbContext(options);
        db.ProductionMovementAllocations.Add(new ProductionMovementAllocation
        {
            CompanyCode = fixture.Company, BranchCode = fixture.Branch, StockPostingId = fixture.PostingId,
            ReceiptMovementId = fixture.ReceiptMovementId, OutboundMovementId = fixture.AdditionalOutboundMovementId,
            BaseQty = 1m, CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task InsertInventoryHistoryAsync(string connectionString, SealFixture fixture)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new AppDbContext(options);
        db.IvTrxHistories.Add(new IvTrxHistory
        {
            CompanyCode = fixture.Company, BranchCode = fixture.Branch, BatchNo = UniquePositiveInt(), TrxLineNo = 1,
            TrxDtTime = DateTime.UtcNow, TrxType = "TEST", BatchStatus = "POSTED", ICode = $"FG-{fixture.Token}",
            StockPostingId = fixture.PostingId
        });
        await db.SaveChangesAsync();
    }

    private static async Task UpdateInventoryBalanceWithoutContextAsync(string connectionString, ActiveBalanceFixture fixture)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new AppDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("EXEC sys.sp_set_session_context @key=N'STOCK_LEDGER_V2_POSTING_ID', @value=NULL;");
        var balance = await db.IvBalLocs.SingleAsync(x => x.Id == fixture.InventoryBalanceId);
        balance.StdQty += 1m;
        await db.SaveChangesAsync();
    }

    private static async Task UpdateProductionBalanceWithoutContextAsync(string connectionString, ActiveBalanceFixture fixture)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new AppDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("EXEC sys.sp_set_session_context @key=N'STOCK_LEDGER_V2_POSTING_ID', @value=NULL;");
        var balance = await db.ProductionBalLots.SingleAsync(x => x.Uid == fixture.ProductionBalanceId);
        balance.Qty += 1m;
        balance.BaseQty += 1m;
        await db.SaveChangesAsync();
    }

    private static async Task SetPostingSessionContextAsync(SqlConnection connection, long? postingId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC sys.sp_set_session_context @key=N'STOCK_LEDGER_V2_POSTING_ID', @value=@postingId;";
        command.Parameters.Add("@postingId", SqlDbType.BigInt).Value = postingId is null ? DBNull.Value : postingId.Value;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteNonQueryAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertSqlErrorNumberAsync(Func<Task> action, int expectedNumber)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(action);
        var sqlException = FindSqlException(exception);
        Assert.NotNull(sqlException);
        Assert.Equal(expectedNumber, sqlException!.Number);
    }

    private static SqlException? FindSqlException(Exception exception) =>
        exception switch
        {
            SqlException sqlException => sqlException,
            AggregateException aggregate when aggregate.InnerExceptions.Select(FindSqlException).FirstOrDefault(x => x is not null) is { } sqlException => sqlException,
            _ when exception.InnerException is not null => FindSqlException(exception.InnerException),
            _ => null
        };

    private static string UniqueCompany(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..5].ToUpperInvariant();
    private static int UniquePositiveInt() => Random.Shared.Next(1, int.MaxValue);

    private sealed record MovementConstraintMetadata(int ObjectId, string Definition, bool IsDisabled, bool IsNotTrusted);
    private sealed record MovementColumnMetadata(string TypeName, short MaxLength, bool IsNullable);
    private sealed record WorkOrderFixture(long WorkOrderId, string WorkOrderNo, long MaterialId, long OperationId);
    private sealed record SealFixture(
        string Company, string Branch, long PostingId, WorkOrderFixture Graph, long BalanceId, long PostingLinkId,
        long ReceiptMovementId, long AdditionalOutboundMovementId, long MaterialMovementId, long AllocationId,
        int HistoryId, string Token);
    private sealed record ActiveBalanceFixture(int InventoryBalanceId, long ProductionBalanceId, long PostingId);

    private static string? TryResolveScratch()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString("SqlServerTestConnection");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (RequireSqlServer)
                Assert.Fail("Stock ledger SQL Server tests require ConnectionStrings__SqlServerTestConnection.");
            return null;
        }

        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
        if (!builder.InitialCatalog.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            if (RequireSqlServer)
                Assert.Fail("Stock ledger SQL Server tests require a database name containing 'test'.");
            return null;
        }

        return cs;
    }
}
