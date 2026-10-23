using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Production;
using ErpWeb.Core.Settings;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Tests.Infrastructure.Fakes;
using ErpWeb.Tests.Infrastructure.Helpers;
using ErpWeb.Tests.Production.Transaction;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ErpWeb.Tests.Planning.Transaction;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class MachineScheduleRescheduleServiceTests
{
    private static bool RequireSqlServer =>
        string.Equals(Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"), "1", StringComparison.Ordinal);

    private static string? ResolveScratchConnectionString()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString("SqlServerTestConnection");
        if (string.IsNullOrWhiteSpace(cs))
            return null;

        var database = new SqlConnectionStringBuilder(cs).InitialCatalog;
        return database.Contains("test", StringComparison.OrdinalIgnoreCase) ? cs : null;
    }

    [Fact]
    public async Task Commit_changes_schedule_and_allocations_but_freezes_snapshot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync();
        var preview = await fixture.Service.PreviewRescheduleAsync(seeded.Request);

        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0), preview.Data.ProposedStart);
        Assert.Equal(new DateTime(2026, 10, 1, 11, 0, 0), preview.Data.ProposedCompletion);

        var committed = await fixture.Service.CommitRescheduleAsync(
            WithToken(seeded.Request, preview.Data.PreviewToken));

        Assert.True(committed.Succeeded, committed.Message);
        await using var verify = fixture.Factory.CreateDbContext();
        var order = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == seeded.Request.WorkOrderId);
        var operation = await verify.ProductionWorkOrderOperations.SingleAsync(x => x.Uid == seeded.Request.OperationId);
        var machine = await verify.ProductionWorkOrderMachines.SingleAsync(x => x.Uid == seeded.Request.WorkOrderMachineId);
        var material = await verify.ProductionWorkOrderMaterials
            .SingleAsync(x => x.WorkOrderOperationId == operation.Uid);
        var allocations = await verify.ProductionMachineScheduleAllocations
            .Where(x => x.WorkOrderMachineId == machine.Uid).ToListAsync();
        var audit = await verify.ProductionAuditEvents
            .SingleAsync(x => x.WorkOrderId == order.Uid && x.EventType == ProductionAuditEventTypes.ScheduleChanged);

        Assert.Equal(seeded.SnapshotRevision, order.SnapshotRevision);
        Assert.Equal(seeded.SnapshotHash, order.SnapshotHash);
        Assert.Equal(1, order.ScheduleRevision);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0), order.PlannedStartDateTime);
        Assert.Equal(new DateTime(2026, 10, 1, 11, 0, 0), order.PlannedCompletionDateTime);
        Assert.Equal(order.PlannedStartDateTime, operation.PlannedStartDateTime);
        Assert.Equal(order.PlannedCompletionDateTime, operation.PlannedCompletionDateTime);
        Assert.Equal(10m, order.PlannedQty);
        Assert.Equal(0m, order.GoodQty);
        Assert.Equal(10m, order.RemainingQty);
        Assert.Equal(10m, operation.PlannedQty);
        Assert.Equal(10m, operation.PlannedOutputQty);
        Assert.Equal(0m, operation.ProcessedQty);
        Assert.Equal(3.25m, operation.OtherCostPerOutputUnit);
        Assert.Equal(1, machine.ScheduleRevision);
        Assert.Equal("MC1", machine.MachineCode);
        Assert.True(machine.IsSelected);
        Assert.Equal(47.50m, machine.PlannedCostAmount);
        Assert.Equal(4.75m, machine.CostPerOutputUnit);
        Assert.Equal("RM-FROZEN", material.ComponentCode);
        Assert.Equal(PrMaterialSupplySources.Purchased, material.SupplySource);
        Assert.Equal(25m, material.RequiredQty);
        Assert.Equal(25m, material.RequiredBaseQty);
        var allocation = Assert.Single(allocations);
        Assert.Equal(1, allocation.ScheduleRevision);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0), allocation.StartDateTime);
        Assert.Equal(new DateTime(2026, 10, 1, 11, 0, 0), allocation.EndDateTime);
        Assert.Equal(order.SnapshotRevision, audit.SnapshotRevision);
    }

    [Fact]
    public async Task Preview_has_no_persisted_schedule_side_effects()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync();

        var preview = await fixture.Service.PreviewRescheduleAsync(seeded.Request);

        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));
        await using var verify = fixture.Factory.CreateDbContext();
        var order = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == seeded.Request.WorkOrderId);
        var machine = await verify.ProductionWorkOrderMachines.SingleAsync(x => x.Uid == seeded.Request.WorkOrderMachineId);
        var allocation = Assert.Single(await verify.ProductionMachineScheduleAllocations
            .Where(x => x.WorkOrderMachineId == machine.Uid).ToListAsync());

        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), order.PlannedStartDateTime);
        Assert.Equal(0, order.ScheduleRevision);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), machine.PlannedStartDateTime);
        Assert.Equal(0, machine.ScheduleRevision);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), allocation.StartDateTime);
        Assert.Empty(await verify.ProductionAuditEvents
            .Where(x => x.WorkOrderId == order.Uid
                && x.EventType == ProductionAuditEventTypes.ScheduleChanged)
            .ToListAsync());
    }

    [Fact]
    public async Task Rescheduling_can_move_an_operation_earlier_than_the_current_work_order_start()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync(
            start: new DateTime(2026, 10, 1, 10, 30, 0));
        var request = WithRequestedStart(seeded.Request, new DateTime(2026, 10, 1, 8, 0, 0));

        var preview = await fixture.Service.PreviewRescheduleAsync(request);

        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), preview.Data.ProposedStart);
        Assert.Equal(new DateTime(2026, 10, 1, 9, 0, 0), preview.Data.ProposedCompletion);
        Assert.Contains(preview.Data.Changes, change =>
            change.BeforeStart == new DateTime(2026, 10, 1, 10, 30, 0)
            && change.AfterStart == new DateTime(2026, 10, 1, 8, 0, 0));

        var committed = await fixture.Service.CommitRescheduleAsync(
            WithToken(request, preview.Data.PreviewToken));

        Assert.True(committed.Succeeded, committed.Message);
        await using var verify = fixture.Factory.CreateDbContext();
        var order = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == request.WorkOrderId);
        var machine = await verify.ProductionWorkOrderMachines.SingleAsync(x => x.Uid == request.WorkOrderMachineId);
        var allocation = Assert.Single(await verify.ProductionMachineScheduleAllocations
            .Where(x => x.WorkOrderMachineId == machine.Uid).ToListAsync());
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), order.PlannedStartDateTime);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), machine.PlannedStartDateTime);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), allocation.StartDateTime);
        Assert.Equal(1, order.ScheduleRevision);
        Assert.Equal(1, machine.ScheduleRevision);
    }

    [Fact]
    public async Task Preview_blocks_when_the_requested_start_produces_no_schedule_change()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync();
        var request = WithRequestedStart(seeded.Request, new DateTime(2026, 10, 1, 8, 0, 0));

        var preview = await fixture.Service.PreviewRescheduleAsync(request);

        Assert.True(preview.Succeeded, preview.Message);
        Assert.False(preview.Data!.CanCommit);
        Assert.Contains(preview.Data.Blockers,
            blocker => blocker.Contains("no schedule change", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Backward_scheduled_work_order_remains_locked_for_reschedule()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync(
            schedulingDirection: ProductionSchedulingDirections.Backward);

        var preview = await fixture.Service.PreviewRescheduleAsync(seeded.Request);

        Assert.True(preview.Succeeded, preview.Message);
        Assert.False(preview.Data!.CanCommit);
        Assert.Contains(preview.Data.Blockers,
            blocker => blocker.Contains("Backward-scheduled Work Orders remain locked", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ProductionOutputStatuses.New, "NEW Daily Production drafts")]
    [InlineData(ProductionOutputStatuses.Posted, "POSTED Daily Production history")]
    public async Task Schedule_change_is_blocked_by_any_existing_daily_production_state(
        string outputStatus,
        string expectedBlocker)
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync();
        await fixture.SeedProductionOutputAsync(seeded, outputStatus);

        var preview = await fixture.Service.PreviewRescheduleAsync(seeded.Request);

        Assert.True(preview.Succeeded, preview.Message);
        Assert.False(preview.Data!.CanCommit);
        Assert.Contains(preview.Data.Blockers,
            blocker => blocker.Contains(expectedBlocker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Calendar_change_guard_identifies_active_machine_load_in_affected_window()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedActiveMachineScheduleAsync();
        await using var db = fixture.Factory.CreateDbContext();

        var impact = await MachineScheduleCalendarImpactGuard.FindBlockingImpactAsync(
            db,
            "DEMO",
            new DateTime(2026, 10, 1, 8, 30, 0),
            new DateTime(2026, 10, 1, 8, 45, 0),
            ["MC1"]);
        var clearWindow = await MachineScheduleCalendarImpactGuard.FindBlockingImpactAsync(
            db,
            "DEMO",
            new DateTime(2026, 10, 1, 9, 0, 0),
            new DateTime(2026, 10, 1, 10, 0, 0),
            ["MC1"]);
        var otherMachine = await MachineScheduleCalendarImpactGuard.FindBlockingImpactAsync(
            db,
            "DEMO",
            new DateTime(2026, 10, 1, 8, 30, 0),
            new DateTime(2026, 10, 1, 8, 45, 0),
            ["MC2"]);

        Assert.NotNull(impact);
        Assert.Contains("1 active machine schedule", impact, StringComparison.Ordinal);
        Assert.Null(clearWindow);
        Assert.Null(otherMachine);
    }

    [Fact]
    public async Task Stale_preview_is_rejected_without_changing_dates_or_allocations()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync();
        var preview = await fixture.Service.PreviewRescheduleAsync(seeded.Request);
        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));

        await using (var db = fixture.Factory.CreateDbContext())
        {
            var order = await db.ProductionWorkOrders.SingleAsync(x => x.Uid == seeded.Request.WorkOrderId);
            order.ScheduleRevision++;
            order.RowVersion = [9];
            await db.SaveChangesAsync();
        }

        var stale = await fixture.Service.CommitRescheduleAsync(
            WithToken(seeded.Request, preview.Data.PreviewToken));

        Assert.False(stale.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, stale.ErrorCode);
        await using var verify = fixture.Factory.CreateDbContext();
        var orderAfter = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == seeded.Request.WorkOrderId);
        var machineAfter = await verify.ProductionWorkOrderMachines.SingleAsync(x => x.Uid == seeded.Request.WorkOrderMachineId);
        var allocation = Assert.Single(await verify.ProductionMachineScheduleAllocations
            .Where(x => x.WorkOrderMachineId == machineAfter.Uid).ToListAsync());
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), orderAfter.PlannedStartDateTime);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), machineAfter.PlannedStartDateTime);
        Assert.Equal(0, machineAfter.ScheduleRevision);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), allocation.StartDateTime);
        Assert.Empty(await verify.ProductionAuditEvents
            .Where(x => x.WorkOrderId == orderAfter.Uid && x.EventType == ProductionAuditEventTypes.ScheduleChanged)
            .ToListAsync());
    }

    [Fact]
    public async Task Rescheduling_propagates_to_serial_operations_that_consume_internal_wip()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync(includeDownstreamOperations: true);
        var preview = await fixture.Service.PreviewRescheduleAsync(seeded.Request);

        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));
        Assert.Contains(preview.Data.Changes, change =>
            change.OperationCode == "OP2"
            && change.AfterStart == new DateTime(2026, 10, 1, 11, 0, 0)
            && change.AfterCompletion == new DateTime(2026, 10, 1, 11, 30, 0));
        Assert.Equal(new DateTime(2026, 10, 1, 11, 30, 0), preview.Data.WorkOrderCompletionAfter);

        var committed = await fixture.Service.CommitRescheduleAsync(
            WithToken(seeded.Request, preview.Data.PreviewToken));

        Assert.True(committed.Succeeded, committed.Message);
        await using var verify = fixture.Factory.CreateDbContext();
        var downstream = await verify.ProductionWorkOrderOperations
            .SingleAsync(x => x.WorkOrderId == seeded.Request.WorkOrderId && x.OperationCode == "OP2");
        var material = await verify.ProductionWorkOrderMaterials
            .SingleAsync(x => x.WorkOrderOperationId == downstream.Uid);
        Assert.Equal(new DateTime(2026, 10, 1, 11, 0, 0), downstream.PlannedStartDateTime);
        Assert.Equal(PrMaterialSupplySources.InternalRouteWip, material.SupplySource);
        Assert.Equal(seeded.TargetRouteStepId, material.ProducingRouteStepId);
    }

    [Fact]
    public async Task Injected_allocation_insert_failure_rolls_back_dates_revisions_allocations_and_audit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync();
        var preview = await fixture.Service.PreviewRescheduleAsync(seeded.Request);
        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));

        await using (var db = fixture.Factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER FailPlannerAllocationInsert
                BEFORE INSERT ON PrMachineScheduleAllocation
                WHEN NEW.ScheduleRevision = 1
                BEGIN
                    SELECT RAISE(ABORT, 'injected schedule allocation failure');
                END;
                """);
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Service.CommitRescheduleAsync(
            WithToken(seeded.Request, preview.Data.PreviewToken)));

        await using var verify = fixture.Factory.CreateDbContext();
        var order = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == seeded.Request.WorkOrderId);
        var operation = await verify.ProductionWorkOrderOperations.SingleAsync(x => x.Uid == seeded.Request.OperationId);
        var machine = await verify.ProductionWorkOrderMachines.SingleAsync(x => x.Uid == seeded.Request.WorkOrderMachineId);
        var allocation = Assert.Single(await verify.ProductionMachineScheduleAllocations
            .Where(x => x.WorkOrderMachineId == machine.Uid).ToListAsync());

        Assert.Equal(seeded.SnapshotRevision, order.SnapshotRevision);
        Assert.Equal(seeded.SnapshotHash, order.SnapshotHash);
        Assert.Equal(0, order.ScheduleRevision);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), order.PlannedStartDateTime);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), operation.PlannedStartDateTime);
        Assert.Equal(0, machine.ScheduleRevision);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), machine.PlannedStartDateTime);
        Assert.Equal(0, allocation.ScheduleRevision);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), allocation.StartDateTime);
        Assert.Empty(await verify.ProductionAuditEvents
            .Where(x => x.WorkOrderId == order.Uid && x.EventType == ProductionAuditEventTypes.ScheduleChanged)
            .ToListAsync());
    }

    [Fact]
    [Trait(TestCategories.Name, TestCategories.SqlServer)]
    public async Task SqlServer_concurrent_same_machine_commits_cannot_overbook_capacity()
    {
        var connectionString = ResolveScratchConnectionString();
        if (connectionString is null)
        {
            if (RequireSqlServer)
            {
                Assert.Fail("Machine planner SQL Server tests require ConnectionStrings__SqlServerTestConnection "
                    + "pointing at a scratch database whose name contains 'test'.");
            }
            return;
        }

        await using var fixture = await Fixture.CreateSqlServerAsync(connectionString);
        var first = await fixture.SeedActiveMachineScheduleAsync(
            start: new DateTime(2026, 10, 1, 8, 0, 0));
        var second = await fixture.SeedActiveMachineScheduleAsync(
            start: new DateTime(2026, 10, 1, 9, 0, 0));
        var firstPreview = await fixture.Service.PreviewRescheduleAsync(first.Request);
        var secondPreview = await fixture.Service.PreviewRescheduleAsync(second.Request);
        Assert.True(firstPreview.Succeeded, firstPreview.Message);
        Assert.True(firstPreview.Data!.CanCommit, string.Join(" ", firstPreview.Data.Blockers));
        Assert.True(secondPreview.Succeeded, secondPreview.Message);
        Assert.True(secondPreview.Data!.CanCommit, string.Join(" ", secondPreview.Data.Blockers));

        var releaseCommits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        async Task<IvMasterOperationResult<MachineScheduleRescheduleCommitResult>> CommitAsync(
            SeededSchedule schedule,
            MachineScheduleReschedulePreview preview)
        {
            if (Interlocked.Increment(ref ready) == 2)
                releaseCommits.TrySetResult();
            await releaseCommits.Task;
            return await fixture.Service.CommitRescheduleAsync(
                WithToken(schedule.Request, preview.PreviewToken));
        }

        var results = await Task.WhenAll(
            CommitAsync(first, firstPreview.Data),
            CommitAsync(second, secondPreview.Data));
        Assert.Single(results.Where(x => x.Succeeded));
        Assert.Single(results.Where(x => !x.Succeeded && x.ErrorCode == IvMasterErrorCode.Concurrency));

        await using var verify = fixture.Factory.CreateDbContext();
        var allocations = await verify.ProductionMachineScheduleAllocations
            .Where(x => x.MachineCode == fixture.MachineCode)
            .OrderBy(x => x.StartDateTime)
            .ToListAsync();
        Assert.Equal(2, allocations.Count);
        Assert.All(allocations.Zip(allocations.Skip(1)), pair =>
            Assert.True(pair.First.EndDateTime <= pair.Second.StartDateTime,
                "Concurrent reschedules must leave half-open allocation intervals non-overlapping."));
    }

    [Fact]
    [Trait(TestCategories.Name, TestCategories.SqlServer)]
    public async Task SqlServer_planner_commit_racing_daily_production_create_has_one_safe_serial_order()
    {
        var connectionString = ResolveScratchConnectionString();
        if (connectionString is null)
        {
            if (RequireSqlServer)
            {
                Assert.Fail("Planner/Daily Production SQL races require a reachable scratch database whose name contains 'test'.");
            }
            return;
        }

        await using var fixture = await Fixture.CreateSqlServerAsync(connectionString);
        var seeded = await fixture.SeedActiveMachineScheduleAsync(includeFrozenMaterial: false);
        var preview = await fixture.Service.PreviewRescheduleAsync(seeded.Request);
        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));

        var dailyProduction = ProductionLedgerTestFixture.CreateProductionOutputService(
            fixture.Factory, startingSequence: NextUniqueSqlTestDocumentSequence());
        var createRequest = CreateDailyProductionRequest(seeded.Request.OperationId);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        async Task<IvMasterOperationResult<T>> AtStart<T>(Func<Task<IvMasterOperationResult<T>>> action)
        {
            if (Interlocked.Increment(ref ready) == 2)
                start.TrySetResult();
            await start.Task;
            return await action();
        }

        var commitTask = AtStart(() => fixture.Service.CommitRescheduleAsync(
            WithToken(seeded.Request, preview.Data.PreviewToken)));
        var createTask = AtStart(() => dailyProduction.CreateAsync(createRequest));
        await Task.WhenAll(commitTask, createTask);
        var committed = await commitTask;
        var created = await createTask;

        Assert.True(created.Succeeded, created.Message);
        await using var verify = fixture.Factory.CreateDbContext();
        var order = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == seeded.Request.WorkOrderId);
        var persistedOutput = await verify.ProductionOutputs.SingleAsync(x => x.CreateRequestId == createRequest.CreateRequestId);
        Assert.Equal(ProductionOutputStatuses.New, persistedOutput.Status);
        Assert.Equal(order.SnapshotHash, persistedOutput.SnapshotHash);
        Assert.Equal(order.SnapshotRevision, persistedOutput.SnapshotRevision);
        if (committed.Succeeded)
        {
            Assert.Equal(1, order.ScheduleRevision);
            Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0), order.PlannedStartDateTime);
        }
        else
        {
            Assert.Equal(IvMasterErrorCode.Validation, committed.ErrorCode);
            Assert.Contains("NEW Daily Production drafts", committed.Message, StringComparison.Ordinal);
            Assert.Equal(0, order.ScheduleRevision);
            Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), order.PlannedStartDateTime);
        }
    }

    [Fact]
    [Trait(TestCategories.Name, TestCategories.SqlServer)]
    public async Task SqlServer_planner_commit_racing_daily_production_post_keeps_posted_history_and_schedule_safe()
    {
        var connectionString = ResolveScratchConnectionString();
        if (connectionString is null)
        {
            if (RequireSqlServer)
                Assert.Fail("Planner/Daily Production SQL races require a reachable scratch database whose name contains 'test'.");
            return;
        }

        await using var fixture = await Fixture.CreateSqlServerAsync(connectionString);
        await SeedDailyProductionLedgerAsync(fixture.Factory);
        var seeded = await fixture.SeedActiveMachineScheduleAsync(includeFrozenMaterial: false);
        var preview = await fixture.Service.PreviewRescheduleAsync(seeded.Request);
        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));

        var dailyProduction = ProductionLedgerTestFixture.CreateProductionOutputService(
            fixture.Factory, startingSequence: NextUniqueSqlTestDocumentSequence());
        var create = await dailyProduction.CreateAsync(CreateDailyProductionRequest(seeded.Request.OperationId));
        Assert.True(create.Succeeded, create.Message);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        async Task<IvMasterOperationResult<T>> AtStart<T>(Func<Task<IvMasterOperationResult<T>>> action)
        {
            if (Interlocked.Increment(ref ready) == 2)
                start.TrySetResult();
            await start.Task;
            return await action();
        }

        var commitTask = AtStart(() => fixture.Service.CommitRescheduleAsync(
            WithToken(seeded.Request, preview.Data.PreviewToken)));
        var postTask = AtStart(() => dailyProduction.PostAsync(create.Data!.Uid));
        await Task.WhenAll(commitTask, postTask);
        var committed = await commitTask;
        var posted = await postTask;

        Assert.True(posted.Succeeded, posted.Message);
        Assert.False(committed.Succeeded);
        Assert.Contains(committed.ErrorCode, new[] { IvMasterErrorCode.Validation, IvMasterErrorCode.Concurrency });
        if (committed.ErrorCode == IvMasterErrorCode.Validation)
        {
            Assert.True(
                committed.Message.Contains("NEW Daily Production drafts", StringComparison.Ordinal)
                || committed.Message.Contains("POSTED Daily Production history", StringComparison.Ordinal),
                committed.Message);
        }
        else
        {
            Assert.Contains("changed since the board was loaded", committed.Message, StringComparison.OrdinalIgnoreCase);
        }
        await using var verify = fixture.Factory.CreateDbContext();
        var order = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == seeded.Request.WorkOrderId);
        var output = await verify.ProductionOutputs.SingleAsync(x => x.Uid == create.Data!.Uid);
        Assert.Equal(ProductionOutputStatuses.Posted, output.Status);
        Assert.Equal(0, order.ScheduleRevision);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), order.PlannedStartDateTime);
    }

    [Fact]
    [Trait(TestCategories.Name, TestCategories.SqlServer)]
    public async Task SqlServer_planner_commit_racing_daily_production_rollback_preserves_reversed_history_and_schedule()
    {
        var connectionString = ResolveScratchConnectionString();
        if (connectionString is null)
        {
            if (RequireSqlServer)
                Assert.Fail("Planner/Daily Production SQL races require a reachable scratch database whose name contains 'test'.");
            return;
        }

        await using var fixture = await Fixture.CreateSqlServerAsync(connectionString);
        await SeedDailyProductionLedgerAsync(fixture.Factory);
        var seeded = await fixture.SeedActiveMachineScheduleAsync(includeFrozenMaterial: false);
        var preview = await fixture.Service.PreviewRescheduleAsync(seeded.Request);
        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));

        var dailyProduction = ProductionLedgerTestFixture.CreateProductionOutputService(
            fixture.Factory, startingSequence: NextUniqueSqlTestDocumentSequence());
        var create = await dailyProduction.CreateAsync(CreateDailyProductionRequest(seeded.Request.OperationId));
        Assert.True(create.Succeeded, create.Message);
        var posted = await dailyProduction.PostAsync(create.Data!.Uid);
        Assert.True(posted.Succeeded, posted.Message);

        var rollbackRequest = new ProductionOutputRollbackRequest
        {
            OutputId = create.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "SQL planner rollback race"
        };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        async Task<IvMasterOperationResult<T>> AtStart<T>(Func<Task<IvMasterOperationResult<T>>> action)
        {
            if (Interlocked.Increment(ref ready) == 2)
                start.TrySetResult();
            await start.Task;
            return await action();
        }

        var commitTask = AtStart(() => fixture.Service.CommitRescheduleAsync(
            WithToken(seeded.Request, preview.Data.PreviewToken)));
        var rollbackTask = AtStart(() => dailyProduction.RollbackAsync(rollbackRequest));
        await Task.WhenAll(commitTask, rollbackTask);
        var committed = await commitTask;
        var rolledBack = await rollbackTask;

        Assert.True(rolledBack.Succeeded, rolledBack.Message);
        Assert.False(committed.Succeeded);
        Assert.Contains(committed.ErrorCode, new[] { IvMasterErrorCode.Validation, IvMasterErrorCode.Concurrency });
        if (committed.ErrorCode == IvMasterErrorCode.Validation)
        {
            Assert.Contains("POSTED Daily Production history", committed.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("changed since the board was loaded", committed.Message, StringComparison.OrdinalIgnoreCase);
        }
        await using var verify = fixture.Factory.CreateDbContext();
        var order = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == seeded.Request.WorkOrderId);
        var output = await verify.ProductionOutputs.SingleAsync(x => x.Uid == create.Data.Uid);
        Assert.Equal(ProductionOutputStatuses.New, output.Status);
        Assert.NotNull(output.PostingId);
        Assert.NotNull(output.ReversalPostingId);
        Assert.Equal(0, order.ScheduleRevision);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), order.PlannedStartDateTime);
    }

    [Fact]
    [Trait(TestCategories.Name, TestCategories.SqlServer)]
    public async Task SqlServer_planner_commit_racing_machine_calendar_writer_never_leaves_stale_calendar_schedule()
    {
        var connectionString = ResolveScratchConnectionString();
        if (connectionString is null)
        {
            if (RequireSqlServer)
                Assert.Fail("Planner/calendar SQL races require a reachable scratch database whose name contains 'test'.");
            return;
        }

        await using var fixture = await Fixture.CreateSqlServerAsync(connectionString);
        var seeded = await fixture.SeedActiveMachineScheduleAsync(includeFrozenMaterial: false);
        var preview = await fixture.Service.PreviewRescheduleAsync(seeded.Request);
        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));

        var access = new Mock<IPlanningMasterAccess>();
        access.Setup(x => x.CanAccessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        access.Setup(x => x.CanEditAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var calendarWriter = new PrShiftCalendarService(
            fixture.Factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            access.Object);
        var loadedCalendar = await calendarWriter.LoadYearAsync(fixture.MachineCode, 2026);
        Assert.True(loadedCalendar.Succeeded, loadedCalendar.Message);
        var proposedDays = loadedCalendar.Value!.Days
            .Append(new MachineCalendarDayVm { Date = new DateTime(2026, 10, 1), DateCd = "O" })
            .ToList();

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        async Task<T> AtStart<T>(Func<Task<T>> action)
        {
            if (Interlocked.Increment(ref ready) == 2)
                start.TrySetResult();
            await start.Task;
            return await action();
        }

        var commitTask = AtStart(() => fixture.Service.CommitRescheduleAsync(
            WithToken(seeded.Request, preview.Data.PreviewToken)));
        var calendarTask = AtStart(() => calendarWriter.SaveYearAsync(
            fixture.MachineCode, 2026, proposedDays, loadedCalendar.Value.Fingerprint));
        await Task.WhenAll(commitTask, calendarTask);
        var committed = await commitTask;
        var calendarSaved = await calendarTask;

        Assert.True(committed.Succeeded, committed.Message);
        Assert.False(calendarSaved.Succeeded);
        Assert.Equal(PlanningErrorCode.DependencyExists, calendarSaved.ErrorCode);
        Assert.Contains("active machine schedule", calendarSaved.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = fixture.Factory.CreateDbContext();
        var order = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == seeded.Request.WorkOrderId);
        Assert.Equal(1, order.ScheduleRevision);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0), order.PlannedStartDateTime);
        Assert.Empty(await verify.PrShiftCalendars.Where(x => x.MachineCode == fixture.MachineCode).ToListAsync());
    }

    [Fact]
    [Trait(TestCategories.Name, TestCategories.SqlServer)]
    public async Task SqlServer_allocation_backfill_dry_run_is_read_only_and_apply_verifies_fragments()
    {
        var connectionString = ResolveScratchConnectionString();
        if (connectionString is null)
        {
            if (RequireSqlServer)
                Assert.Fail("Machine planner backfill tests require a reachable scratch database whose name contains 'test'.");
            return;
        }

        await using var fixture = await Fixture.CreateSqlServerAsync(connectionString);
        var backfill = fixture.CreateBackfillService();
        var beforeSeed = await backfill.PreviewBackfillAsync();
        Assert.True(beforeSeed.Succeeded, beforeSeed.Message);
        Assert.True(beforeSeed.Data!.CanApply, string.Join(" ", beforeSeed.Data.Findings.Select(x => x.Message)));

        var seeded = await fixture.SeedActiveMachineScheduleAsync(
            includeFrozenMaterial: false,
            includeAllocation: false);

        var preview = await backfill.PreviewBackfillAsync();
        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanApply, string.Join(" ", preview.Data.Findings.Select(x => x.Message)));
        Assert.Equal(beforeSeed.Data.MissingAllocationCount + 1, preview.Data.MissingAllocationCount);
        Assert.Equal(0, preview.Data.OverlappingMachineCount);
        await using (var beforeApply = fixture.Factory.CreateDbContext())
        {
            Assert.Empty(await beforeApply.ProductionMachineScheduleAllocations
                .Where(x => x.WorkOrderId == seeded.Request.WorkOrderId)
                .ToListAsync());
        }

        var applied = await backfill.ApplyVerifiedBackfillAsync(preview.Data.PreviewToken);
        Assert.True(applied.Succeeded, applied.Message);
        Assert.True(applied.Data!.CanApply, string.Join(" ", applied.Data.Findings.Select(x => x.Message)));
        Assert.Equal(0, applied.Data.MissingAllocationCount);
        Assert.Equal(0, applied.Data.OverlappingMachineCount);

        await using var verify = fixture.Factory.CreateDbContext();
        var order = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == seeded.Request.WorkOrderId);
        Assert.Equal(0, order.ScheduleRevision);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), order.PlannedStartDateTime);
        Assert.NotEmpty(await verify.ProductionMachineScheduleAllocations
            .Where(x => x.WorkOrderId == seeded.Request.WorkOrderId)
            .ToListAsync());
        Assert.Single(await verify.ProductionAuditEvents
            .Where(x => x.WorkOrderId == seeded.Request.WorkOrderId
                && x.EventType == ProductionAuditEventTypes.ScheduleAllocationBackfilled)
            .ToListAsync());
    }

    [Fact]
    public async Task Insert_split_rewrites_segments_and_allocations_without_changing_production_quantities()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync();
        await fixture.SetMachineCycleSecondsAsync(seeded.Request.WorkOrderMachineId, 360m);
        var backfill = fixture.CreateSegmentBackfillService();
        var backfillPreview = await backfill.PreviewAsync();
        Assert.True(backfillPreview.Succeeded, backfillPreview.Message);
        var backfilled = await backfill.ApplyAsync(backfillPreview.Data!.PreviewToken);
        Assert.True(backfilled.Succeeded, backfilled.Message);
        var service = fixture.CreateAdvancedService();
        var request = AdvancedRequest(
            seeded.Request,
            MachineScheduleAdvancedAction.InsertSplit,
            requestedStart: new DateTime(2026, 10, 1, 7, 0, 0),
            partialQuantity: 4m);

        var preview = await service.PreviewInsertAsync(request);

        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));
        Assert.Contains(preview.Data.Segments,
            segment => segment.Kind == ProductionMachineScheduleSegmentKinds.UrgentInsert);
        Assert.Equal(4m, preview.Data.RequestedQty);

        var committed = await service.CommitInsertAsync(
            request with { PreviewToken = preview.Data.PreviewToken });

        Assert.True(committed.Succeeded, committed.Message);
        await using var verify = fixture.Factory.CreateDbContext();
        var order = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == request.WorkOrderId);
        var operation = await verify.ProductionWorkOrderOperations.SingleAsync(x => x.Uid == request.OperationId);
        var machine = await verify.ProductionWorkOrderMachines.SingleAsync(x => x.Uid == request.WorkOrderMachineId);
        var segments = await verify.ProductionMachineScheduleSegments
            .Where(x => x.WorkOrderMachineId == machine.Uid
                && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
            .OrderBy(x => x.SegmentSequence)
            .ToListAsync();
        var allocations = await verify.ProductionMachineScheduleAllocations
            .Where(x => x.WorkOrderMachineId == machine.Uid && x.ScheduleRevision == machine.ScheduleRevision)
            .ToListAsync();
        var audit = await verify.ProductionAuditEvents.SingleAsync(x =>
            x.WorkOrderId == order.Uid && x.EventType == ProductionAuditEventTypes.ScheduleJobInserted);

        Assert.Equal(2, segments.Count);
        Assert.Contains(segments, x => x.SegmentKind == ProductionMachineScheduleSegmentKinds.UrgentInsert);
        Assert.Equal(10m, segments.Sum(x => x.PlannedOutputQty));
        Assert.Equal(4m, segments.Single(x => x.SegmentKind == ProductionMachineScheduleSegmentKinds.UrgentInsert).PlannedOutputQty);
        Assert.Equal(2, allocations.Count);
        Assert.All(allocations, x => Assert.NotNull(x.MachineScheduleSegmentId));
        Assert.All(segments, segment => Assert.Contains(allocations,
            allocation => allocation.MachineScheduleSegmentId == segment.Uid));
        Assert.Equal(seeded.SnapshotRevision, order.SnapshotRevision);
        Assert.Equal(seeded.SnapshotHash, order.SnapshotHash);
        Assert.Equal(1, order.ScheduleRevision);
        Assert.Equal(1, machine.ScheduleRevision);
        Assert.Equal(10m, order.PlannedQty);
        Assert.Equal(10m, order.RemainingQty);
        Assert.Equal(10m, operation.PlannedOutputQty);
        Assert.Equal(0m, operation.GoodQty);
        Assert.Equal(order.SnapshotRevision, audit.SnapshotRevision);

        var capacity = await new ProductionMachineCapacityService(fixture.Factory)
            .LoadAsync(verify, order.CompanyCode, [machine.MachineCode]);
        Assert.Empty(capacity.Blockers);
        var occupied = Assert.Single(capacity.OccupiedByMachine).Value;
        Assert.Equal(2, occupied.Count);
        // The machine calendar opens at 08:00, so the 07:00 request snaps forward to the first
        // usable minute; the uninserted remainder keeps its later plan at 09:00.
        Assert.Contains(occupied, interval =>
            interval.Start == new DateTime(2026, 10, 1, 8, 0, 0)
            && interval.End == new DateTime(2026, 10, 1, 8, 24, 0));
        Assert.Contains(occupied, interval =>
            interval.Start == new DateTime(2026, 10, 1, 9, 0, 0)
            && interval.End == new DateTime(2026, 10, 1, 9, 36, 0));
    }

    [Fact]
    public async Task Advanced_split_uses_approved_box_to_piece_conversion_for_cycles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync();
        var backfill = fixture.CreateSegmentBackfillService();
        var backfillPreview = await backfill.PreviewAsync();
        Assert.True(backfillPreview.Succeeded, backfillPreview.Message);
        Assert.True((await backfill.ApplyAsync(backfillPreview.Data!.PreviewToken)).Succeeded);

        await using (var db = fixture.Factory.CreateDbContext())
        {
            var operation = await db.ProductionWorkOrderOperations
                .SingleAsync(x => x.Uid == seeded.Request.OperationId);
            operation.PlannedOutputUom = "BOX";
            var machine = await db.ProductionWorkOrderMachines
                .SingleAsync(x => x.Uid == seeded.Request.WorkOrderMachineId);
            machine.OutputPerCycleUom = "PCS";
            machine.OutputPerCycle = 2m;
            machine.CycleSeconds = 60m;
            await db.SaveChangesAsync();
        }

        var uom = new Mock<IUomConversionService>();
        uom.Setup(x => x.ConvertAsync("DEMO", "OP1", It.IsAny<decimal>(), "BOX", "PCS",
                It.IsAny<CancellationToken>()))
            .Returns((string company, string item, decimal quantity, string fromUom,
                string toUom, CancellationToken cancellationToken) =>
                Task.FromResult(UomConversionResult.Ok(quantity * 12m, 4)));
        var service = fixture.CreateAdvancedService(uom.Object);
        var request = AdvancedRequest(seeded.Request, MachineScheduleAdvancedAction.InsertSplit,
            requestedStart: new DateTime(2026, 10, 1, 7, 0, 0), partialQuantity: 4m);

        var preview = await service.PreviewInsertAsync(request);

        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));
        var inserted = Assert.Single(preview.Data.Segments,
            x => x.Kind == ProductionMachineScheduleSegmentKinds.UrgentInsert);
        Assert.Equal(4m, inserted.PlannedOutputQty);
        Assert.Equal(24m, inserted.CycleCount);
        Assert.Equal(24m, inserted.RunMinutes);

        uom.Setup(x => x.ConvertAsync("DEMO", "OP1", It.IsAny<decimal>(), "BOX", "PCS",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(UomConversionResult.Fail(
                UomConversionFailureCodes.MissingConversion, "Approved BOX to PCS conversion is missing."));
        var blocked = await service.PreviewInsertAsync(request);
        Assert.True(blocked.Succeeded, blocked.Message);
        Assert.False(blocked.Data!.CanCommit);
        Assert.Contains(blocked.Data.Blockers, x => x.Contains("BOX to PCS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hold_split_ignores_historical_unlinked_allocation_fragments()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync();
        await fixture.SetMachineCycleSecondsAsync(seeded.Request.WorkOrderMachineId, 360m);
        var backfill = fixture.CreateSegmentBackfillService();
        var backfillPreview = await backfill.PreviewAsync();
        Assert.True(backfillPreview.Succeeded, backfillPreview.Message);
        Assert.True((await backfill.ApplyAsync(backfillPreview.Data!.PreviewToken)).Succeeded);

        var service = fixture.CreateAdvancedService();
        var request = AdvancedRequest(seeded.Request, MachineScheduleAdvancedAction.Hold,
            requestedStart: new DateTime(2026, 10, 1, 8, 30, 0),
            holdType: ProductionScheduleHoldTypes.QualityHold);
        var before = await service.PreviewHoldAsync(request);
        Assert.True(before.Succeeded, before.Message);
        Assert.True(before.Data!.CanCommit, string.Join(" ", before.Data.Blockers));

        await using (var db = fixture.Factory.CreateDbContext())
        {
            var current = await db.ProductionMachineScheduleAllocations.AsNoTracking()
                .SingleAsync(x => x.WorkOrderMachineId == seeded.Request.WorkOrderMachineId);
            db.ProductionMachineScheduleAllocations.Add(new ProductionMachineScheduleAllocation
            {
                CompanyCode = current.CompanyCode,
                BranchCode = current.BranchCode,
                MachineCode = current.MachineCode,
                WorkOrderId = current.WorkOrderId,
                WorkOrderOperationId = current.WorkOrderOperationId,
                WorkOrderMachineId = current.WorkOrderMachineId,
                ScheduleRevision = current.ScheduleRevision - 1,
                FragmentNo = 1,
                StartDateTime = current.StartDateTime,
                EndDateTime = current.EndDateTime,
                ScheduleSourceHash = current.ScheduleSourceHash,
                CreatedDate = current.CreatedDate,
                CreatedBy = "test"
            });
            await db.SaveChangesAsync();
        }

        var after = await service.PreviewHoldAsync(request);
        Assert.True(after.Succeeded, after.Message);
        Assert.True(after.Data!.CanCommit, string.Join(" ", after.Data.Blockers));
        Assert.Equal(before.Data.Segments.Select(x => (x.PlannedOutputQty, x.Start, x.Completion)),
            after.Data.Segments.Select(x => (x.PlannedOutputQty, x.Start, x.Completion)));
    }

    [Fact]
    public async Task Open_ended_hold_reports_unknown_completion_and_resume_reanchors_the_remaining_quantity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync();
        await fixture.SetMachineCycleSecondsAsync(seeded.Request.WorkOrderMachineId, 360m);
        var backfill = fixture.CreateSegmentBackfillService();
        var backfillPreview = await backfill.PreviewAsync();
        Assert.True(backfillPreview.Succeeded, backfillPreview.Message);
        var backfilled = await backfill.ApplyAsync(backfillPreview.Data!.PreviewToken);
        Assert.True(backfilled.Succeeded, backfilled.Message);
        var service = fixture.CreateAdvancedService();
        // Hold mid-job so a real prefix segment remains before the unknown interruption.
        var holdRequest = AdvancedRequest(
            seeded.Request,
            MachineScheduleAdvancedAction.Hold,
            requestedStart: new DateTime(2026, 10, 1, 8, 30, 0),
            holdType: ProductionScheduleHoldTypes.MaterialShortage);

        var preview = await service.PreviewHoldAsync(holdRequest);

        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));
        Assert.True(preview.Data.HasUnscheduledRemaining);
        Assert.Null(preview.Data.ProposedCompletion);

        var committed = await service.CommitHoldAsync(
            holdRequest with { PreviewToken = preview.Data.PreviewToken });
        Assert.True(committed.Succeeded, committed.Message);
        Assert.True(committed.Data!.HasUnscheduledRemaining);

        await using var verify = fixture.Factory.CreateDbContext();
        var order = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == holdRequest.WorkOrderId);
        var machine = await verify.ProductionWorkOrderMachines.SingleAsync(x => x.Uid == holdRequest.WorkOrderMachineId);
        var activeHold = await verify.ProductionWorkOrderScheduleHolds.SingleAsync(x =>
            x.WorkOrderId == order.Uid && x.Status == ProductionScheduleHoldStatuses.Active);
        Assert.Equal(ProductionScheduleHoldTypes.MaterialShortage, activeHold.HoldType);
        Assert.Null(activeHold.ResumeNotBefore);
        Assert.Equal(seeded.SnapshotRevision, order.SnapshotRevision);
        Assert.Equal(seeded.SnapshotHash, order.SnapshotHash);

        // A known prefix may exist, but the outer dates stay on the last known plan.
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), machine.PlannedStartDateTime);
        Assert.Equal(new DateTime(2026, 10, 1, 9, 0, 0), machine.PlannedCompletionDateTime);
        var heldSegments = await verify.ProductionMachineScheduleSegments
            .Where(x => x.WorkOrderMachineId == machine.Uid
                && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
            .ToListAsync();
        var heldPrefix = Assert.Single(heldSegments);
        Assert.Equal(5m, heldPrefix.PlannedOutputQty);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 30, 0), heldPrefix.PlannedCompletionDateTime);

        var releaseRequest = new MachineScheduleAdvancedRequest
        {
            Action = MachineScheduleAdvancedAction.ReleaseHold,
            WorkOrderId = order.Uid,
            OperationId = holdRequest.OperationId,
            WorkOrderMachineId = machine.Uid,
            MachineCode = machine.MachineCode,
            ExpectedWorkOrderScheduleRevision = order.ScheduleRevision,
            ExpectedWorkOrderRowVersion = order.RowVersion.ToArray(),
            ExpectedMachineScheduleRevision = machine.ScheduleRevision,
            ExpectedMachineRowVersion = machine.RowVersion.ToArray(),
            RequestedStart = new DateTime(2026, 10, 1, 13, 0, 0),
            HoldId = activeHold.Uid,
            Reason = "Material received"
        };

        var releasePreview = await service.PreviewReleaseHoldAsync(releaseRequest);
        Assert.True(releasePreview.Succeeded, releasePreview.Message);
        Assert.True(releasePreview.Data!.CanCommit, string.Join(" ", releasePreview.Data.Blockers));
        Assert.Equal(new DateTime(2026, 10, 1, 13, 0, 0), releasePreview.Data.ProposedStart);

        var released = await service.CommitReleaseHoldAsync(
            releaseRequest with { PreviewToken = releasePreview.Data.PreviewToken });
        Assert.True(released.Succeeded, released.Message);

        await using var verifyReleased = fixture.Factory.CreateDbContext();
        var storedHold = await verifyReleased.ProductionWorkOrderScheduleHolds.SingleAsync(x => x.Uid == activeHold.Uid);
        var currentSegments = await verifyReleased.ProductionMachineScheduleSegments
            .Where(x => x.WorkOrderMachineId == machine.Uid
                && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
            .ToListAsync();
        var releasedOrder = await verifyReleased.ProductionWorkOrders.SingleAsync(x => x.Uid == order.Uid);
        Assert.Equal(ProductionScheduleHoldStatuses.Released, storedHold.Status);
        var resumeSegment = Assert.Single(currentSegments);
        Assert.Equal(ProductionMachineScheduleSegmentKinds.HoldResume, resumeSegment.SegmentKind);
        Assert.Equal(new DateTime(2026, 10, 1, 13, 0, 0), resumeSegment.PlannedStartDateTime);
        Assert.Equal(10m, resumeSegment.PlannedOutputQty);
        Assert.Equal(seeded.SnapshotRevision, releasedOrder.SnapshotRevision);
        Assert.Equal(seeded.SnapshotHash, releasedOrder.SnapshotHash);
        Assert.Equal(10m, releasedOrder.PlannedQty);
    }

    [Fact]
    public async Task Replan_remaining_uses_only_unexecuted_quantity_and_keeps_execution_and_costing_facts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync();
        await fixture.SetMachineCycleSecondsAsync(seeded.Request.WorkOrderMachineId, 360m);
        await using (var prepare = fixture.Factory.CreateDbContext())
        {
            var operation = await prepare.ProductionWorkOrderOperations.SingleAsync(x => x.Uid == seeded.Request.OperationId);
            var order = await prepare.ProductionWorkOrders.SingleAsync(x => x.Uid == seeded.Request.WorkOrderId);
            operation.ProcessedQty = 6m;
            operation.GoodQty = 6m;
            operation.RemainingQty = 4m;
            order.GoodQty = 6m;
            order.RemainingQty = 4m;
            await prepare.SaveChangesAsync();
        }

        var backfill = fixture.CreateSegmentBackfillService();
        var backfillPreview = await backfill.PreviewAsync();
        Assert.True(backfillPreview.Succeeded, backfillPreview.Message);
        var backfilled = await backfill.ApplyAsync(backfillPreview.Data!.PreviewToken);
        Assert.True(backfilled.Succeeded, backfilled.Message);

        var service = fixture.CreateAdvancedService();
        var request = AdvancedRequest(
            seeded.Request,
            MachineScheduleAdvancedAction.ReplanRemaining,
            requestedStart: new DateTime(2026, 10, 1, 10, 0, 0));

        var preview = await service.PreviewReplanRemainingAsync(request);

        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));
        Assert.Equal(4m, preview.Data.RequestedQty);

        var committed = await service.CommitReplanRemainingAsync(
            request with { PreviewToken = preview.Data.PreviewToken });
        Assert.True(committed.Succeeded, committed.Message);

        await using var verify = fixture.Factory.CreateDbContext();
        var orderAfter = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == request.WorkOrderId);
        var operationAfter = await verify.ProductionWorkOrderOperations.SingleAsync(x => x.Uid == request.OperationId);
        var machineAfter = await verify.ProductionWorkOrderMachines.SingleAsync(x => x.Uid == request.WorkOrderMachineId);
        var segment = await verify.ProductionMachineScheduleSegments.SingleAsync(x =>
            x.WorkOrderMachineId == machineAfter.Uid
            && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded);
        var allocations = await verify.ProductionMachineScheduleAllocations
            .Where(x => x.WorkOrderMachineId == machineAfter.Uid
                && x.ScheduleRevision == machineAfter.ScheduleRevision)
            .ToListAsync();
        Assert.Equal(4m, segment.PlannedOutputQty);
        Assert.Equal(ProductionMachineScheduleSegmentKinds.RemainingReplan, segment.SegmentKind);
        Assert.NotEmpty(allocations);
        Assert.All(allocations, x => Assert.NotNull(x.MachineScheduleSegmentId));
        Assert.Equal(seeded.SnapshotRevision, orderAfter.SnapshotRevision);
        Assert.Equal(seeded.SnapshotHash, orderAfter.SnapshotHash);
        Assert.Equal(10m, orderAfter.PlannedQty);
        Assert.Equal(6m, orderAfter.GoodQty);
        Assert.Equal(4m, orderAfter.RemainingQty);
        Assert.Equal(10m, operationAfter.PlannedOutputQty);
        Assert.Equal(6m, operationAfter.GoodQty);
        Assert.Equal(4m, operationAfter.RemainingQty);
        Assert.Equal(47.50m, machineAfter.PlannedCostAmount);
    }

    [Fact]
    public async Task Maintenance_replan_reanchors_the_unfinished_quantity_after_downtime_and_freezes_snapshot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync();
        await fixture.SetMachineCycleSecondsAsync(seeded.Request.WorkOrderMachineId, 360m);
        var backfill = fixture.CreateSegmentBackfillService();
        var backfillPreview = await backfill.PreviewAsync();
        Assert.True(backfillPreview.Succeeded, backfillPreview.Message);
        var backfilled = await backfill.ApplyAsync(backfillPreview.Data!.PreviewToken);
        Assert.True(backfilled.Succeeded, backfilled.Message);

        var service = fixture.CreateAdvancedService();
        var request = new MachineScheduleAdvancedRequest
        {
            Action = MachineScheduleAdvancedAction.MaintenanceReplan,
            WorkOrderId = seeded.Request.WorkOrderId,
            OperationId = seeded.Request.OperationId,
            WorkOrderMachineId = seeded.Request.WorkOrderMachineId,
            MachineCode = seeded.Request.MachineCode,
            ExpectedWorkOrderScheduleRevision = seeded.Request.ExpectedWorkOrderScheduleRevision,
            ExpectedWorkOrderRowVersion = seeded.Request.ExpectedWorkOrderRowVersion,
            ExpectedMachineScheduleRevision = seeded.Request.ExpectedMachineScheduleRevision,
            ExpectedMachineRowVersion = seeded.Request.ExpectedMachineRowVersion,
            DowntimeStart = new DateTime(2026, 10, 1, 8, 30, 0),
            DowntimeEnd = new DateTime(2026, 10, 1, 10, 30, 0),
            MaintenanceType = "BREAKDOWN",
            Reason = "Bearing failure"
        };

        var preview = await service.PreviewMaintenanceReplanAsync(request);

        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));
        Assert.Equal(2, preview.Data.Segments.Count);
        var resume = Assert.Single(preview.Data.Segments,
            x => x.Kind == ProductionMachineScheduleSegmentKinds.MaintenanceResume);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 30, 0), resume.Start);
        Assert.Equal(5m, resume.PlannedOutputQty);

        var committed = await service.CommitMaintenanceReplanAsync(
            request with { PreviewToken = preview.Data.PreviewToken });
        Assert.True(committed.Succeeded, committed.Message);

        await using var verify = fixture.Factory.CreateDbContext();
        var order = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == request.WorkOrderId);
        var machine = await verify.ProductionWorkOrderMachines.SingleAsync(x => x.Uid == request.WorkOrderMachineId);
        var segments = await verify.ProductionMachineScheduleSegments
            .Where(x => x.WorkOrderMachineId == machine.Uid
                && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
            .OrderBy(x => x.PlannedStartDateTime)
            .ToListAsync();
        Assert.Equal(2, segments.Count);
        Assert.Equal(10m, segments.Sum(x => x.PlannedOutputQty));
        Assert.Equal(new DateTime(2026, 10, 1, 10, 30, 0),
            segments.Single(x => x.SegmentKind == ProductionMachineScheduleSegmentKinds.MaintenanceResume)
                .PlannedStartDateTime);
        Assert.Equal(seeded.SnapshotRevision, order.SnapshotRevision);
        Assert.Equal(seeded.SnapshotHash, order.SnapshotHash);
        Assert.Equal(10m, order.PlannedQty);
    }

    [Fact]
    public async Task Open_ended_maintenance_never_invents_a_resume_date_or_moves_the_last_known_plan()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync();
        await fixture.SetMachineCycleSecondsAsync(seeded.Request.WorkOrderMachineId, 360m);
        var backfill = fixture.CreateSegmentBackfillService();
        var backfillPreview = await backfill.PreviewAsync();
        Assert.True(backfillPreview.Succeeded, backfillPreview.Message);
        var backfilled = await backfill.ApplyAsync(backfillPreview.Data!.PreviewToken);
        Assert.True(backfilled.Succeeded, backfilled.Message);

        var service = fixture.CreateAdvancedService();
        var request = new MachineScheduleAdvancedRequest
        {
            Action = MachineScheduleAdvancedAction.MaintenanceReplan,
            WorkOrderId = seeded.Request.WorkOrderId,
            OperationId = seeded.Request.OperationId,
            WorkOrderMachineId = seeded.Request.WorkOrderMachineId,
            MachineCode = seeded.Request.MachineCode,
            ExpectedWorkOrderScheduleRevision = seeded.Request.ExpectedWorkOrderScheduleRevision,
            ExpectedWorkOrderRowVersion = seeded.Request.ExpectedWorkOrderRowVersion,
            ExpectedMachineScheduleRevision = seeded.Request.ExpectedMachineScheduleRevision,
            ExpectedMachineRowVersion = seeded.Request.ExpectedMachineRowVersion,
            DowntimeStart = new DateTime(2026, 10, 1, 8, 30, 0),
            DowntimeEnd = null,
            MaintenanceType = "BREAKDOWN",
            Reason = "Repair end not yet known"
        };

        var preview = await service.PreviewMaintenanceReplanAsync(request);

        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));
        Assert.True(preview.Data.HasUnscheduledRemaining);
        Assert.Null(preview.Data.ProposedCompletion);
        Assert.Single(preview.Data.Segments);
        Assert.DoesNotContain(preview.Data.Segments,
            x => x.Kind == ProductionMachineScheduleSegmentKinds.MaintenanceResume);

        var committed = await service.CommitMaintenanceReplanAsync(
            request with { PreviewToken = preview.Data.PreviewToken });
        Assert.True(committed.Succeeded, committed.Message);
        Assert.True(committed.Data!.HasUnscheduledRemaining);

        await using var verify = fixture.Factory.CreateDbContext();
        var order = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == request.WorkOrderId);
        var machine = await verify.ProductionWorkOrderMachines.SingleAsync(x => x.Uid == request.WorkOrderMachineId);
        var operation = await verify.ProductionWorkOrderOperations.SingleAsync(x => x.Uid == request.OperationId);
        var segments = await verify.ProductionMachineScheduleSegments
            .Where(x => x.WorkOrderMachineId == machine.Uid
                && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
            .ToListAsync();
        var prefix = Assert.Single(segments);
        Assert.Equal(5m, prefix.PlannedOutputQty);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 30, 0), prefix.PlannedCompletionDateTime);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), order.PlannedStartDateTime);
        Assert.Equal(new DateTime(2026, 10, 1, 9, 0, 0), order.PlannedCompletionDateTime);
        Assert.Equal(new DateTime(2026, 10, 1, 9, 0, 0), machine.PlannedCompletionDateTime);
        Assert.Equal(new DateTime(2026, 10, 1, 9, 0, 0), operation.PlannedCompletionDateTime);
        Assert.Equal(seeded.SnapshotRevision, order.SnapshotRevision);
        Assert.Equal(seeded.SnapshotHash, order.SnapshotHash);
        Assert.Equal(10m, order.PlannedQty);
    }

    [Fact]
    public async Task Advanced_commit_rejects_a_stale_preview_token()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync();
        await fixture.SetMachineCycleSecondsAsync(seeded.Request.WorkOrderMachineId, 360m);
        var backfill = fixture.CreateSegmentBackfillService();
        var backfillPreview = await backfill.PreviewAsync();
        Assert.True(backfillPreview.Succeeded, backfillPreview.Message);
        var backfilled = await backfill.ApplyAsync(backfillPreview.Data!.PreviewToken);
        Assert.True(backfilled.Succeeded, backfilled.Message);
        var service = fixture.CreateAdvancedService();
        var request = AdvancedRequest(
            seeded.Request,
            MachineScheduleAdvancedAction.Hold,
            requestedStart: new DateTime(2026, 10, 1, 8, 0, 0),
            holdType: ProductionScheduleHoldTypes.QualityHold);
        var preview = await service.PreviewHoldAsync(request);
        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers));

        await using (var mutate = fixture.Factory.CreateDbContext())
        {
            var machine = await mutate.ProductionWorkOrderMachines.SingleAsync(x => x.Uid == request.WorkOrderMachineId);
            machine.ScheduleRevision++;
            await mutate.SaveChangesAsync();
        }

        var committed = await service.CommitHoldAsync(
            request with { PreviewToken = preview.Data.PreviewToken });

        Assert.False(committed.Succeeded);
        Assert.Contains("changed", committed.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        await using var verify = fixture.Factory.CreateDbContext();
        Assert.Empty(await verify.ProductionWorkOrderScheduleHolds
            .Where(x => x.WorkOrderId == request.WorkOrderId)
            .ToListAsync());
    }

    [Fact]
    public async Task Segment_backfill_creates_original_segments_and_links_existing_allocations_without_moving_dates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedActiveMachineScheduleAsync();
        var service = fixture.CreateSegmentBackfillService();

        var preview = await service.PreviewAsync();

        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(preview.Data!.CanApply, string.Join(" ", preview.Data.Findings));
        Assert.Equal(1, preview.Data.MissingSegmentCount);

        var applied = await service.ApplyAsync(preview.Data.PreviewToken);

        Assert.True(applied.Succeeded, applied.Message);
        await using var verify = fixture.Factory.CreateDbContext();
        var order = await verify.ProductionWorkOrders.SingleAsync(x => x.Uid == seeded.Request.WorkOrderId);
        var machine = await verify.ProductionWorkOrderMachines.SingleAsync(x => x.Uid == seeded.Request.WorkOrderMachineId);
        var segment = await verify.ProductionMachineScheduleSegments.SingleAsync(x =>
            x.WorkOrderMachineId == machine.Uid);
        var allocation = await verify.ProductionMachineScheduleAllocations.SingleAsync(x =>
            x.WorkOrderMachineId == machine.Uid);
        Assert.Equal(ProductionMachineScheduleSegmentKinds.Original, segment.SegmentKind);
        Assert.Equal(1, segment.SegmentSequence);
        Assert.Equal(segment.Uid, allocation.MachineScheduleSegmentId);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), segment.PlannedStartDateTime);
        Assert.Equal(new DateTime(2026, 10, 1, 9, 0, 0), segment.PlannedCompletionDateTime);
        Assert.Equal(10m, segment.PlannedOutputQty);
        Assert.Equal(seeded.SnapshotRevision, order.SnapshotRevision);
        Assert.Equal(seeded.SnapshotHash, order.SnapshotHash);
        Assert.Equal(0, order.ScheduleRevision);
        Assert.Equal(0, machine.ScheduleRevision);
    }

    private static MachineScheduleAdvancedRequest AdvancedRequest(
        MachineScheduleRescheduleRequest request,
        MachineScheduleAdvancedAction action,
        DateTime? requestedStart = null,
        decimal? partialQuantity = null,
        string? holdType = null) => new()
    {
        Action = action,
        WorkOrderId = request.WorkOrderId,
        OperationId = request.OperationId,
        WorkOrderMachineId = request.WorkOrderMachineId,
        MachineCode = request.MachineCode,
        ExpectedWorkOrderScheduleRevision = request.ExpectedWorkOrderScheduleRevision,
        ExpectedWorkOrderRowVersion = request.ExpectedWorkOrderRowVersion,
        ExpectedMachineScheduleRevision = request.ExpectedMachineScheduleRevision,
        ExpectedMachineRowVersion = request.ExpectedMachineRowVersion,
        RequestedStart = requestedStart,
        PartialQuantity = partialQuantity,
        HoldType = holdType,
        Reason = "Advanced schedule test"
    };

    [Fact]
    [Trait(TestCategories.Name, TestCategories.SqlServer)]
    public async Task SqlServer_readiness_evidence_is_current_while_write_setting_still_controls_enablement()
    {
        var connectionString = ResolveScratchConnectionString();
        if (connectionString is null)
        {
            if (RequireSqlServer)
                Assert.Fail("Machine planner readiness tests require a reachable scratch database whose name contains 'test'.");
            return;
        }

        await using var fixture = await Fixture.CreateSqlServerAsync(connectionString);
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var settingOff = new Mock<IAppSettingService>();
        settingOff.Setup(x => x.GetFlagAsync(
                AppSettingModules.Planning,
                AppSettingCatalogue.PlanningKeys.MachineSchedulePlannerWriteEnabled,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var readinessOff = new MachineSchedulePlannerReadinessService(
            fixture.Factory, tenant, access.Object, settingOff.Object);

        var refreshed = await readinessOff.RefreshEvidenceAsync();
        Assert.True(refreshed.Succeeded, refreshed.Message);
        Assert.True(refreshed.Data!.EvidenceRecorded, refreshed.Data.Message);
        Assert.False(refreshed.Data.WriteSettingEnabled);

        var disabledStatus = await readinessOff.GetReadinessAsync();
        Assert.True(disabledStatus.AllocationSchemaReady);
        Assert.True(disabledStatus.ActiveAllocationBackfillComplete);
        Assert.Equal(0, disabledStatus.UnverifiedActiveAllocations);
        Assert.Equal(0, disabledStatus.StaleAllocations);
        Assert.Equal(0, disabledStatus.UnexplainedActiveMachineOverlaps);
        Assert.True(disabledStatus.CalendarAndReleaseEvidenceVerified);
        Assert.False(disabledStatus.WriteSettingEnabled);
        Assert.False(disabledStatus.IsWriteReady);

        var settingOn = new Mock<IAppSettingService>();
        settingOn.Setup(x => x.GetFlagAsync(
                AppSettingModules.Planning,
                AppSettingCatalogue.PlanningKeys.MachineSchedulePlannerWriteEnabled,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var enabledStatus = await new MachineSchedulePlannerReadinessService(
            fixture.Factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            access.Object,
            settingOn.Object).GetReadinessAsync();
        Assert.True(enabledStatus.IsWriteReady, string.Join(" ", enabledStatus.BlockingReasons));
    }

    private static ProductionOutputCreateRequest CreateDailyProductionRequest(long operationId) => new()
    {
        WorkOrderOperationId = operationId,
        CreateRequestId = Guid.NewGuid().ToString("N"),
        ProductionDate = new DateTime(2026, 10, 1),
        GoodQty = 1m,
        OutputLotNo = "RACE-LOT"
    };

    private static int NextUniqueSqlTestDocumentSequence() => Random.Shared.Next(10_000_000, 99_999_999);

    private static async Task SeedDailyProductionLedgerAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        if (!await db.IvWarehouses.AnyAsync(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.WarehouseCode == "WH01"))
        {
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "WH01",
                WarehouseDesc = "Planner race test",
                IsActive = true
            });
        }
        if (!await db.IvStockMasters.AnyAsync(x => x.CompanyCode == "DEMO" && x.ICode == "FG-1"))
        {
            db.IvStockMasters.Add(new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "FG-1",
                IDesc = "Planner race finished good",
                StdUom = "EA",
                MfgType = PrMfgTypes.Make,
                DefWarehouse = "WH01",
                IsActive = true
            });
        }
        await db.SaveChangesAsync();
        await ProductionLedgerTestFixture.SeedActiveEpochAsync(db, company: "DEMO", branch: "HQ");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly DateOnly HorizonStart = new(2026, 10, 1);
        private static readonly DateOnly HorizonEnd = new(2026, 10, 15);
        private readonly SqliteConnection? _connection;
        private readonly FakeProductionCalendarScheduleDataLoader _calendars = new();
        private readonly string _machineCode;

        private Fixture(SqliteConnection? connection, TestDbContextFactory factory, string machineCode)
        {
            _connection = connection;
            _machineCode = machineCode;
            Factory = factory;
            Service = CreateService();
        }

        public TestDbContextFactory Factory { get; }
        public MachineScheduleRescheduleService Service { get; }
        public string MachineCode => _machineCode;

        public async Task SetMachineCycleSecondsAsync(long machineId, decimal cycleSeconds)
        {
            await using var db = Factory.CreateDbContext();
            var machine = await db.ProductionWorkOrderMachines.SingleAsync(x => x.Uid == machineId);
            machine.CycleSeconds = cycleSeconds;
            await db.SaveChangesAsync();
        }

        public MachineScheduleSegmentBackfillService CreateSegmentBackfillService()
        {
            var access = new Mock<IAccessRightService>();
            access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            return new MachineScheduleSegmentBackfillService(
                Factory,
                InventoryTenantTestHelper.CreateTenantContext(),
                access.Object);
        }

        public MachineScheduleAdvancedPlanningService CreateAdvancedService(IUomConversionService? uom = null)
        {
            var access = new Mock<IAccessRightService>();
            access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var settings = new Mock<IAppSettingService>();
            settings.Setup(x => x.GetFlagAsync(
                    AppSettingModules.Planning,
                    AppSettingCatalogue.PlanningKeys.MachineScheduleSegmentWriteEnabled,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var readiness = new Mock<IMachineSchedulePlannerReadinessService>();
            readiness.Setup(x => x.GetReadinessAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MachineSchedulePlannerReadiness
                {
                    IsWriteReady = true,
                    WriteSettingEnabled = true,
                    AllocationSchemaReady = true,
                    ActiveAllocationBackfillComplete = true
                });
            return new MachineScheduleAdvancedPlanningService(
                Factory,
                InventoryTenantTestHelper.CreateTenantContext(),
                access.Object,
                settings.Object,
                readiness.Object,
                _calendars,
                new AlwaysOpenWorkOrderCalendarProvider(),
                new ProductionMachineCapacityService(Factory),
                uom ?? new IvUomConversionService(Factory));
        }

        public MachineScheduleAllocationBackfillService CreateBackfillService()
        {
            var access = new Mock<IAccessRightService>();
            access.Setup(x => x.CanAsync(
                    MenuCodes.PlanningMachineSchedulePlanner,
                    PermissionCodes.Access,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            access.Setup(x => x.CanAsync(
                    MenuCodes.PlanningMachineSchedulePlanner,
                    PermissionCodes.Edit,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            access.Setup(x => x.CanAsync(
                    MenuCodes.AdminSettings,
                    PermissionCodes.Edit,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            return new MachineScheduleAllocationBackfillService(
                Factory,
                InventoryTenantTestHelper.CreateTenantContext(),
                access.Object,
                _calendars);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var factory = new TestDbContextFactory(options);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
            }
            return new Fixture(connection, factory, "MC1");
        }

        public static async Task<Fixture> CreateSqlServerAsync(string connectionString)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(connectionString)
                .Options;
            var factory = new TestDbContextFactory(options);
            await using (var db = factory.CreateDbContext())
                await db.Database.EnsureCreatedAsync();
            return new Fixture(null, factory, "M" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant());
        }

        public async Task<SeededSchedule> SeedActiveMachineScheduleAsync(
            bool includeDownstreamOperations = false,
            DateTime? start = null,
            string schedulingDirection = ProductionSchedulingDirections.Forward,
            bool includeFrozenMaterial = true,
            bool includeAllocation = true)
        {
            var data = await _calendars.LoadAsync("DEMO", _machineCode, HorizonStart, HorizonEnd);
            var sourceHash = WorkOrderScheduleSourceHasher.Compute(data, HorizonStart, HorizonEnd);
            start ??= new DateTime(2026, 10, 1, 8, 0, 0);
            var completion = start.Value.AddHours(1);
            long sourceBomHdrId;
            await using (var seedDb = Factory.CreateDbContext())
            {
                var source = await seedDb.PrBomHdrs.FirstOrDefaultAsync(x =>
                    x.CompanyCode == "DEMO"
                    && x.ProdCode == "FG-1"
                    && x.DefinitionCode == PrProductDefinitionCodes.Standard);
                if (source is null)
                {
                    source = new PrBomHdr
                    {
                        CompanyCode = "DEMO",
                        ProdCode = "FG-1",
                        DefinitionCode = PrProductDefinitionCodes.Standard,
                        Version = 1,
                        Status = PrBomStatuses.Active,
                        EffectiveFrom = new DateTime(2026, 1, 1),
                        BaseQty = 1m,
                        BaseUom = "EA",
                        BranchCode = "HQ",
                        LocationCode = "SITE"
                    };
                    seedDb.PrBomHdrs.Add(source);
                    await seedDb.SaveChangesAsync();
                }
                sourceBomHdrId = source.Uid;
            }
            var order = new ProductionWorkOrder
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "SITE",
                WorkOrderNo = $"WO-MSP-{Guid.NewGuid():N}"[..19],
                SnapshotRevision = 4,
                SnapshotHash = new string('A', 64),
                SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
                SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
                IsLegacySnapshot = false,
                SourceDefinitionCode = PrProductDefinitionCodes.Standard,
                DefinitionEffectiveDate = new DateTime(2026, 9, 1),
                ProductCode = "FG-1",
                OutputUom = "EA",
                SourceBomHdrId = sourceBomHdrId,
                SourceBomVersion = 1,
                BomBaseQty = 1m,
                BomBaseUom = "EA",
                PlannedQty = 10m,
                RemainingQty = 10m,
                PlannedStartDateTime = start.Value,
                PlannedCompletionDateTime = completion,
                SchedulingDirection = schedulingDirection,
                ScheduleRevision = 0,
                Status = ProductionWorkOrderStatuses.Released,
                SourceType = ProductionSourceTypes.Manual,
                RowVersion = [1]
            };
            var step = new ProductionWorkOrderRouteStep
            {
                WorkOrder = order,
                StageSequence = 10,
                WorkCentreCode = "WC1",
                OutputItemCode = "FG-1",
                OutputType = PrRouteOutputTypes.FinishedGoods,
                OutputBaseQty = 1m,
                OutputUom = "EA",
                OutputBaseUom = "EA",
                OutputConversionFactorToBase = 1m,
                PlannedQty = 10m,
                PlannedStartDateTime = start.Value,
                PlannedCompletionDateTime = completion,
                RowVersion = [2]
            };
            var operation = new ProductionWorkOrderOperation
            {
                WorkOrder = order,
                RouteStep = step,
                SequenceNo = 10,
                ProcessSequence = 10,
                ProcessType = PrProcessTypes.Machine,
                OperationCode = "OP1",
                IsFinalOperation = true,
                PlannedQty = 10m,
                PlannedOutputQty = 10m,
                RemainingQty = 10m,
                PlannedOutputUom = "EA",
                OtherCostPerOutputUnit = 3.25m,
                PlannedStartDateTime = start.Value,
                PlannedCompletionDateTime = completion,
                ScheduleSourceHash = sourceHash,
                CalendarSourceType = ProductionCalendarSourceTypes.Machine,
                CalendarHorizonStart = HorizonStart.ToDateTime(TimeOnly.MinValue),
                CalendarHorizonEnd = HorizonEnd.ToDateTime(TimeOnly.MinValue),
                RowVersion = [3]
            };
            var machine = new ProductionWorkOrderMachine
            {
                Operation = operation,
                MachineCode = _machineCode,
                IsDefault = true,
                IsSelected = true,
                Priority = 1,
                ParallelMachineCount = 1,
                CycleQuantityMode = ProductionMachineCycleQuantityModes.Discrete,
                OutputPerCycle = 1m,
                PlannedRunMinutes = 60m,
                PlannedCostAmount = 47.50m,
                CostPerOutputUnit = 4.75m,
                PlannedStartDateTime = start.Value,
                PlannedCompletionDateTime = completion,
                ScheduleRevision = 0,
                ScheduleSourceHash = sourceHash,
                CalendarHorizonStart = HorizonStart.ToDateTime(TimeOnly.MinValue),
                CalendarHorizonEnd = HorizonEnd.ToDateTime(TimeOnly.MinValue),
                RowVersion = [4]
            };
            operation.Machines.Add(machine);
            step.Operations.Add(operation);
            order.RouteSteps.Add(step);
            order.Operations.Add(operation);
            if (includeFrozenMaterial)
            {
                var frozenMaterial = new ProductionWorkOrderMaterial
                {
                    WorkOrder = order,
                    WorkOrderOperation = operation,
                    MaterialSequence = 8,
                    LineNo = 8,
                    ComponentCode = "RM-FROZEN",
                    ComponentQtyPerParent = 2.5m,
                    StandardUom = "KG",
                    BomOutputQty = 10m,
                    BomOutputUom = "EA",
                    RequiredQty = 25m,
                    RequiredUom = "KG",
                    RequiredBaseQty = 25m,
                    BaseUom = "KG",
                    ConversionFactorToBase = 1m,
                    IssueMethod = PrMaterialIssueMethods.Manual,
                    SupplySource = PrMaterialSupplySources.Purchased,
                    MfgType = PrMfgTypes.Buy,
                    BomPath = "FG-1/RM-FROZEN",
                    RowVersion = [8]
                };
                operation.Materials.Add(frozenMaterial);
                order.Materials.Add(frozenMaterial);
            }

            if (includeDownstreamOperations)
            {
                var downstreamStep = new ProductionWorkOrderRouteStep
                {
                    WorkOrder = order,
                    StageSequence = 20,
                    WorkCentreCode = "WC2",
                    OutputItemCode = "FG-1",
                    OutputType = PrRouteOutputTypes.FinishedGoods,
                    OutputBaseQty = 1m,
                    OutputUom = "EA",
                    OutputBaseUom = "EA",
                    PlannedQty = 10m,
                    PlannedStartDateTime = new DateTime(2026, 10, 1, 9, 0, 0),
                    PlannedCompletionDateTime = new DateTime(2026, 10, 1, 9, 30, 0),
                    RowVersion = [5]
                };
                var downstreamOperation = new ProductionWorkOrderOperation
                {
                    WorkOrder = order,
                    RouteStep = downstreamStep,
                    SequenceNo = 20,
                    ProcessSequence = 10,
                    ProcessType = PrProcessTypes.Manual,
                    OperationCode = "OP2",
                    StandardDurationMinutes = 30m,
                    IsFinalOperation = true,
                    PlannedQty = 10m,
                    PlannedOutputQty = 10m,
                    PlannedOutputUom = "EA",
                    PlannedStartDateTime = downstreamStep.PlannedStartDateTime,
                    PlannedCompletionDateTime = downstreamStep.PlannedCompletionDateTime,
                    RowVersion = [6]
                };
                var internalWip = new ProductionWorkOrderMaterial
                {
                    WorkOrder = order,
                    WorkOrderOperation = downstreamOperation,
                    MaterialSequence = 1,
                    LineNo = 1,
                    ComponentCode = "WIP-1",
                    SupplySource = PrMaterialSupplySources.InternalRouteWip,
                    ProducingRouteStep = step,
                    ComponentQtyPerParent = 1m,
                    BomOutputQty = 1m,
                    RequiredQty = 10m,
                    RequiredBaseQty = 10m,
                    RequiredUom = "EA",
                    BaseUom = "EA",
                    ConversionFactorToBase = 1m,
                    IssueMethod = "MANUAL",
                    MfgType = PrMfgTypes.Make,
                    RowVersion = [7]
                };
                downstreamOperation.Materials.Add(internalWip);
                downstreamStep.Operations.Add(downstreamOperation);
                order.RouteSteps.Add(downstreamStep);
                order.Operations.Add(downstreamOperation);
                order.Materials.Add(internalWip);
            }

            await using var db = Factory.CreateDbContext();
            var isSqlite = string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal);
            if (isSqlite)
                await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            db.ProductionWorkOrders.Add(order);
            await db.SaveChangesAsync();
            if (includeAllocation)
            {
                db.ProductionMachineScheduleAllocations.Add(new ProductionMachineScheduleAllocation
                {
                    CompanyCode = order.CompanyCode,
                    BranchCode = order.BranchCode,
                    MachineCode = machine.MachineCode,
                    WorkOrderId = order.Uid,
                    WorkOrderOperationId = operation.Uid,
                    WorkOrderMachineId = machine.Uid,
                    ScheduleRevision = machine.ScheduleRevision,
                    FragmentNo = 1,
                    StartDateTime = start.Value,
                    EndDateTime = completion,
                    ScheduleSourceHash = sourceHash,
                    CreatedDate = new DateTime(2026, 9, 30),
                    CreatedBy = "test"
                });
                await db.SaveChangesAsync();
            }
            if (isSqlite)
                await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

            var storedOrder = await db.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Uid == order.Uid);
            var storedMachine = await db.ProductionWorkOrderMachines.AsNoTracking().SingleAsync(x => x.Uid == machine.Uid);
            return new SeededSchedule(
                new MachineScheduleRescheduleRequest
                {
                    WorkOrderId = storedOrder.Uid,
                    OperationId = operation.Uid,
                    WorkOrderMachineId = storedMachine.Uid,
                    MachineCode = machine.MachineCode,
                    RequestedStart = new DateTime(2026, 10, 1, 10, 0, 0),
                    ExpectedWorkOrderScheduleRevision = storedOrder.ScheduleRevision,
                    ExpectedWorkOrderRowVersion = storedOrder.RowVersion.ToArray(),
                    ExpectedMachineScheduleRevision = storedMachine.ScheduleRevision,
                    ExpectedMachineRowVersion = storedMachine.RowVersion.ToArray(),
                    Reason = "Approved schedule move"
                },
                storedOrder.SnapshotRevision,
                storedOrder.SnapshotHash,
                step.Uid);
        }

        public async Task SeedProductionOutputAsync(SeededSchedule schedule, string status)
        {
            await using var db = Factory.CreateDbContext();
            db.ProductionOutputs.Add(new ProductionOutput
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                DocumentNo = $"DP-{Guid.NewGuid():N}"[..30],
                Status = status,
                WorkOrderId = schedule.Request.WorkOrderId,
                RouteStepId = schedule.TargetRouteStepId,
                WorkOrderOperationId = schedule.Request.OperationId,
                ProductionDate = new DateTime(2026, 10, 1),
                GoodQty = 0m,
                ScrapQty = 0m,
                RejectQty = 0m,
                HoldQty = 0m,
                OutputUom = "EA",
                OutputItemCode = "FG-1",
                OutputType = PrRouteOutputTypes.FinishedGoods,
                OutputLotNo = "TEST-LOT",
                SnapshotRevision = schedule.SnapshotRevision,
                SnapshotHash = schedule.SnapshotHash,
                CreateRequestId = Guid.NewGuid().ToString("N"),
                CreateRequestFingerprint = new string('A', 64),
                CreatedDate = new DateTime(2026, 10, 1),
                CreatedBy = "test",
                RowVersion = [9]
            });
            await db.SaveChangesAsync();
        }

        private MachineScheduleRescheduleService CreateService()
        {
            var access = new Mock<IAccessRightService>();
            access.Setup(x => x.CanAsync(
                    MenuCodes.PlanningMachineSchedulePlanner,
                    PermissionCodes.Access,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            access.Setup(x => x.CanAsync(
                    MenuCodes.PlanningMachineSchedulePlanner,
                    PermissionCodes.Edit,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var readiness = new Mock<IMachineSchedulePlannerReadinessService>();
            readiness.Setup(x => x.GetReadinessAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MachineSchedulePlannerReadiness { IsWriteReady = true });

            return new MachineScheduleRescheduleService(
                Factory,
                InventoryTenantTestHelper.CreateTenantContext(),
                access.Object,
                readiness.Object,
                _calendars,
                new AlwaysOpenWorkOrderCalendarProvider());
        }

        public ValueTask DisposeAsync() => _connection?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed record SeededSchedule(
        MachineScheduleRescheduleRequest Request,
        int SnapshotRevision,
        string SnapshotHash,
        long TargetRouteStepId);

    private static MachineScheduleRescheduleRequest WithToken(
        MachineScheduleRescheduleRequest request,
        string? previewToken) => new()
    {
        WorkOrderId = request.WorkOrderId,
        OperationId = request.OperationId,
        WorkOrderMachineId = request.WorkOrderMachineId,
        MachineCode = request.MachineCode,
        RequestedStart = request.RequestedStart,
        ExpectedWorkOrderScheduleRevision = request.ExpectedWorkOrderScheduleRevision,
        ExpectedWorkOrderRowVersion = request.ExpectedWorkOrderRowVersion,
        ExpectedMachineScheduleRevision = request.ExpectedMachineScheduleRevision,
        ExpectedMachineRowVersion = request.ExpectedMachineRowVersion,
        Reason = request.Reason,
        PreviewToken = previewToken
    };

    private static MachineScheduleRescheduleRequest WithRequestedStart(
        MachineScheduleRescheduleRequest request,
        DateTime requestedStart) => new()
    {
        WorkOrderId = request.WorkOrderId,
        OperationId = request.OperationId,
        WorkOrderMachineId = request.WorkOrderMachineId,
        MachineCode = request.MachineCode,
        RequestedStart = requestedStart,
        ExpectedWorkOrderScheduleRevision = request.ExpectedWorkOrderScheduleRevision,
        ExpectedWorkOrderRowVersion = request.ExpectedWorkOrderRowVersion,
        ExpectedMachineScheduleRevision = request.ExpectedMachineScheduleRevision,
        ExpectedMachineRowVersion = request.ExpectedMachineRowVersion,
        Reason = request.Reason
    };
}
