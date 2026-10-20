using ErpWeb.Core.Planning;
using ErpWeb.Core.Production;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Planning.Transaction;
[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionMachineCapacityServiceTests : IAsyncLifetime
{
    private static readonly DateTime ShiftStart = new(2026, 10, 5, 8, 0, 0);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ProductionMachineCapacityService _service;
    private readonly long _bomHdrId;

    public ProductionMachineCapacityServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        _service = new ProductionMachineCapacityService(_factory);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        var bom = new PrBomHdr
        {
            CompanyCode = "DEMO",
            ProdCode = "FG001",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            DefinitionName = PrProductDefinitionCodes.StandardName,
            IsDefaultDefinition = true,
            Version = 1,
            Status = PrBomStatuses.Active,
            EffectiveFrom = new DateTime(2026, 1, 1),
            BaseQty = 1m,
            BaseUom = "PCS",
            BranchCode = "HQ",
            LocationCode = "SITE",
            RowVersion = [1]
        };
        db.PrBomHdrs.Add(bom);
        db.SaveChanges();
        _bomHdrId = bom.Uid;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Legacy_empty_zero_duration_row_is_ignored_without_hiding_corruption()
    {
        var row = await SeedAsync(
            ProductionWorkOrderStatuses.InProgress,
            setup: 0m, conversion: 0m, queue: 0m, run: 0m,
            plannedStart: ShiftStart, plannedEnd: ShiftStart,
            operationStart: ShiftStart, operationEnd: ShiftStart);

        await using var db = await _factory.CreateDbContextAsync();
        var evidence = await _service.LoadAsync(db, "DEMO", ["MC01"]);

        Assert.False(evidence.HasBlockers);
        Assert.Equal(1, evidence.LegacyEmptyRowCount);
        Assert.Empty(evidence.GetOccupied("MC01"));
        Assert.Single(evidence.SourceStamps);
        Assert.Contains(row.MachineId.ToString(), evidence.SourceStamps[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Zero_duration_with_a_positive_saved_span_blocks()
    {
        await SeedAsync(
            ProductionWorkOrderStatuses.InProgress,
            setup: 0m, conversion: 0m, queue: 0m, run: 0m,
            plannedStart: ShiftStart, plannedEnd: ShiftStart.AddHours(1),
            operationStart: ShiftStart, operationEnd: ShiftStart.AddHours(1));

        await using var db = await _factory.CreateDbContextAsync();
        var evidence = await _service.LoadAsync(db, "DEMO", ["MC01"]);

        Assert.True(evidence.HasBlockers);
        Assert.Contains(evidence.Blockers, x => x.Contains(ProductionReadinessErrorCodes.MachineCapacityInvalid, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Zero_duration_with_a_positive_allocation_fragment_blocks()
    {
        await SeedAsync(
            ProductionWorkOrderStatuses.InProgress,
            setup: 0m, conversion: 0m, queue: 0m, run: 0m,
            plannedStart: ShiftStart, plannedEnd: ShiftStart,
            operationStart: ShiftStart, operationEnd: ShiftStart,
            allocationStart: ShiftStart, allocationEnd: ShiftStart.AddHours(1));

        await using var db = await _factory.CreateDbContextAsync();
        var evidence = await _service.LoadAsync(db, "DEMO", ["MC01"]);

        Assert.True(evidence.HasBlockers);
        var finding = Assert.Single(evidence.Findings);
        Assert.Equal("MC01", finding.MachineCode);
        Assert.Contains("allocation", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Negative_timing_component_never_classifies_as_legacy_empty()
    {
        var row = await SeedAsync(
            ProductionWorkOrderStatuses.InProgress,
            setup: 0m, conversion: 0m, queue: 0m, run: 0m,
            plannedStart: ShiftStart, plannedEnd: ShiftStart,
            operationStart: ShiftStart, operationEnd: ShiftStart);

        // The relational check constraint rejects negative timing on normal writes; corrupt
        // legacy data is created here through a controlled raw update so the runtime classifier
        // is exercised against the same shape it may encounter in production.
        await using (var raw = await _factory.CreateDbContextAsync())
        {
            await raw.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
            await raw.Database.ExecuteSqlRawAsync(
                "UPDATE PrWorkOrderMachine SET SetupSeconds = -60 WHERE UID = {0}", row.MachineId);
        }

        await using var db = await _factory.CreateDbContextAsync();
        var evidence = await _service.LoadAsync(db, "DEMO", ["MC01"]);

        Assert.True(evidence.HasBlockers);
        Assert.Equal(0, evidence.LegacyEmptyRowCount);
    }

    [Fact]
    public async Task Positive_active_row_without_current_allocations_blocks()
    {
        await SeedAsync(
            ProductionWorkOrderStatuses.Released,
            setup: 0m, conversion: 0m, queue: 0m, run: 60m,
            plannedStart: ShiftStart, plannedEnd: ShiftStart.AddHours(1),
            operationStart: ShiftStart, operationEnd: ShiftStart.AddHours(1));

        await using var db = await _factory.CreateDbContextAsync();
        var evidence = await _service.LoadAsync(db, "DEMO", ["MC01"]);

        Assert.True(evidence.HasBlockers);
        Assert.Contains(evidence.Blockers, x => x.Contains("allocation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Positive_active_row_occupies_exactly_its_verified_allocations()
    {
        var row = await SeedAsync(
            ProductionWorkOrderStatuses.Released,
            setup: 0m, conversion: 0m, queue: 0m, run: 60m,
            plannedStart: ShiftStart, plannedEnd: ShiftStart.AddHours(1),
            operationStart: ShiftStart, operationEnd: ShiftStart.AddHours(1),
            allocationStart: ShiftStart, allocationEnd: ShiftStart.AddHours(1));

        await using var db = await _factory.CreateDbContextAsync();
        var evidence = await _service.LoadAsync(db, "DEMO", ["MC01"]);

        Assert.False(evidence.HasBlockers);
        var interval = Assert.Single(evidence.GetOccupied("MC01"));
        Assert.Equal(ShiftStart, interval.Start);
        Assert.Equal(ShiftStart.AddHours(1), interval.End);
        Assert.Equal(1, evidence.PositiveRowCount);
        Assert.Single(evidence.SourceStamps);
        Assert.Contains(row.MachineId.ToString(), evidence.SourceStamps[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overlapping_verified_allocations_block()
    {
        await SeedAsync(
            ProductionWorkOrderStatuses.Released,
            setup: 0m, conversion: 0m, queue: 0m, run: 60m,
            plannedStart: ShiftStart, plannedEnd: ShiftStart.AddHours(1),
            operationStart: ShiftStart, operationEnd: ShiftStart.AddHours(1),
            allocationStart: ShiftStart, allocationEnd: ShiftStart.AddHours(2),
            workOrderNo: "WO-A");
        await SeedAsync(
            ProductionWorkOrderStatuses.Released,
            setup: 0m, conversion: 0m, queue: 0m, run: 60m,
            plannedStart: ShiftStart.AddHours(1), plannedEnd: ShiftStart.AddHours(2),
            operationStart: ShiftStart.AddHours(1), operationEnd: ShiftStart.AddHours(2),
            allocationStart: ShiftStart.AddHours(1), allocationEnd: ShiftStart.AddHours(2),
            workOrderNo: "WO-B");

        await using var db = await _factory.CreateDbContextAsync();
        var evidence = await _service.LoadAsync(db, "DEMO", ["MC01"]);

        Assert.True(evidence.HasBlockers);
        Assert.Contains(evidence.Blockers, x => x.Contains("overlap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Draft_and_inactive_rows_never_consume_runtime_capacity()
    {
        await SeedAsync(
            ProductionWorkOrderStatuses.Draft,
            setup: 0m, conversion: 0m, queue: 0m, run: 60m,
            plannedStart: ShiftStart, plannedEnd: ShiftStart.AddHours(1),
            operationStart: ShiftStart, operationEnd: ShiftStart.AddHours(1),
            allocationStart: ShiftStart, allocationEnd: ShiftStart.AddHours(1));

        await using var db = await _factory.CreateDbContextAsync();
        var evidence = await _service.LoadAsync(db, "DEMO", ["MC01"]);

        Assert.False(evidence.HasBlockers);
        Assert.Empty(evidence.GetOccupied("MC01"));
        Assert.Equal(0, evidence.PositiveRowCount);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("machine")]
    [InlineData("operation")]
    [InlineData("workorder")]
    [InlineData("hash")]
    public async Task Allocation_provenance_mismatch_blocks_runtime_capacity(string corruption)
    {
        // Plan §27: an active positive row is authoritative only when each current fragment's
        // provenance matches the selected machine operation exactly.
        await SeedAsync(
            ProductionWorkOrderStatuses.Released,
            setup: 0m, conversion: 0m, queue: 0m, run: 60m,
            plannedStart: ShiftStart, plannedEnd: ShiftStart.AddHours(1),
            operationStart: ShiftStart, operationEnd: ShiftStart.AddHours(1),
            allocationStart: ShiftStart, allocationEnd: ShiftStart.AddHours(1));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var allocation = await db.ProductionMachineScheduleAllocations.SingleAsync();
            switch (corruption)
            {
                case "revision":
                    allocation.ScheduleRevision += 1;
                    break;
                case "machine":
                    allocation.MachineCode = "MC02";
                    break;
                case "operation":
                    allocation.WorkOrderOperationId += 1;
                    break;
                case "workorder":
                    allocation.WorkOrderId += 1;
                    break;
                case "hash":
                    allocation.ScheduleSourceHash = "OTHER-HASH";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(corruption), corruption, "Unknown corruption.");
            }

            await db.SaveChangesAsync();
        }

        await using var verify = await _factory.CreateDbContextAsync();
        var evidence = await _service.LoadAsync(verify, "DEMO", ["MC01"]);

        Assert.True(evidence.HasBlockers);
        Assert.Contains(
            evidence.Blockers,
            x => x.Contains(ProductionReadinessErrorCodes.MachineCapacityInvalid, StringComparison.Ordinal));
        Assert.Empty(evidence.GetOccupied("MC01"));
    }

    [Fact]
    public async Task Excluded_work_order_does_not_block_or_occupy_its_own_release()
    {
        var row = await SeedAsync(
            ProductionWorkOrderStatuses.Released,
            setup: 0m, conversion: 0m, queue: 0m, run: 60m,
            plannedStart: ShiftStart, plannedEnd: ShiftStart.AddHours(1),
            operationStart: ShiftStart, operationEnd: ShiftStart.AddHours(1),
            allocationStart: ShiftStart, allocationEnd: ShiftStart.AddHours(1));

        await using var db = await _factory.CreateDbContextAsync();
        var evidence = await _service.LoadAsync(
            db, "DEMO", ["MC01"], excludedWorkOrderId: row.WorkOrderId);

        Assert.False(evidence.HasBlockers);
        Assert.Empty(evidence.GetOccupied("MC01"));
    }

    private async Task<SeededRow> SeedAsync(
        string status,
        decimal setup,
        decimal conversion,
        decimal queue,
        decimal run,
        DateTime? plannedStart,
        DateTime? plannedEnd,
        DateTime? operationStart = null,
        DateTime? operationEnd = null,
        DateTime? allocationStart = null,
        DateTime? allocationEnd = null,
        string machineCode = "MC01",
        string workOrderNo = "WO-1")
    {
        await using var db = await _factory.CreateDbContextAsync();
        var order = new ProductionWorkOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WorkOrderNo = workOrderNo,
            ProductCode = "FG001",
            Status = status,
            SourceDefinitionCode = PrProductDefinitionCodes.Standard,
            SourceBomHdrId = _bomHdrId,
            SourceBomVersion = 1,
            SourceEffectiveFrom = new DateTime(2026, 1, 1),
            SchedulingDirection = ProductionSchedulingDirections.Forward,
            PlannedStartDateTime = plannedStart ?? ShiftStart,
            PlannedCompletionDateTime = plannedEnd ?? ShiftStart,
            CreatedDate = ShiftStart,
            CreatedBy = "TEST"
        };
        db.ProductionWorkOrders.Add(order);
        await db.SaveChangesAsync();

        var step = new ProductionWorkOrderRouteStep
        {
            WorkOrderId = order.Uid,
            StageSequence = 10,
            WorkCentreCode = "WC01",
            OutputItemCode = "FG001",
            OutputBaseQty = 1m,
            OutputUom = "PCS",
            PlannedQty = 10m
        };
        db.ProductionWorkOrderRouteSteps.Add(step);
        await db.SaveChangesAsync();

        var operation = new ProductionWorkOrderOperation
        {
            WorkOrderId = order.Uid,
            RouteStepId = step.Uid,
            OperationCode = "OP10",
            ProcessSequence = 10,
            ProcessType = PrProcessTypes.Machine,
            PlannedStartDateTime = operationStart,
            PlannedCompletionDateTime = operationEnd
        };
        db.ProductionWorkOrderOperations.Add(operation);
        await db.SaveChangesAsync();

        var machine = new ProductionWorkOrderMachine
        {
            OperationId = operation.Uid,
            MachineCode = machineCode,
            IsSelected = true,
            IsDefault = true,
            SetupSeconds = setup,
            ConversionSeconds = conversion,
            QueueSeconds = queue,
            PlannedRunMinutes = run,
            PlannedStartDateTime = plannedStart,
            PlannedCompletionDateTime = plannedEnd,
            ScheduleRevision = 1,
            ScheduleSourceHash = "HASH"
        };
        db.ProductionWorkOrderMachines.Add(machine);
        await db.SaveChangesAsync();

        if (allocationStart is DateTime start && allocationEnd is DateTime end)
        {
            db.ProductionMachineScheduleAllocations.Add(new ProductionMachineScheduleAllocation
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                MachineCode = machineCode,
                WorkOrderId = order.Uid,
                WorkOrderOperationId = operation.Uid,
                WorkOrderMachineId = machine.Uid,
                ScheduleRevision = 1,
                FragmentNo = 1,
                StartDateTime = start,
                EndDateTime = end,
                ScheduleSourceHash = "HASH",
                CreatedDate = ShiftStart,
                CreatedBy = "TEST"
            });
            await db.SaveChangesAsync();
        }

        return new SeededRow(order.Uid, operation.Uid, machine.Uid);
    }

    private sealed record SeededRow(long WorkOrderId, long OperationId, long MachineId);
}
