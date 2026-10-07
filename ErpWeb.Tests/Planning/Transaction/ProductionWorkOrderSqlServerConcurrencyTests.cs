using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Production;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;

namespace ErpWeb.Tests.Planning.Transaction;
/// <summary>
/// Plan §12.3 — Work Order SQL Server fixture. Proves CHECK constraints, filtered indexes,
/// Option A1 labour delete paths, aggregate row-version concurrency, Release races, and the
/// company scheduling applock. Skipped unless <c>ConnectionStrings:SqlServerTestConnection</c>
/// names a scratch database containing "test". Set <c>ERPWEB_REQUIRE_SQLSERVER_TESTS=1</c> to
/// fail instead of silently skipping.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Planning)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public sealed class ProductionWorkOrderSqlServerConcurrencyTests
{
    private const string Company = "DEMO";
    private const string Branch = "HQ";

    private static string? ResolveScratchConnectionString()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var cs = config.GetConnectionString("SqlServerTestConnection");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return null;
        }

        var database = ExtractDatabaseName(cs);
        if (database is null || !database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return cs;
    }

    private static string? ExtractDatabaseName(string cs)
    {
        foreach (var part in cs.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            foreach (var key in (string[])["Database", "Initial Catalog"])
            {
                if (trimmed.StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[(key.Length + 1)..].Trim();
                }
            }
        }

        return null;
    }

    private static bool RequireSqlServer =>
        string.Equals(
            Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"), "1", StringComparison.Ordinal);

    private static string? TryResolveScratch()
    {
        var cs = ResolveScratchConnectionString();
        if (cs is null && RequireSqlServer)
        {
            Assert.Fail(
                "Work Order SQL Server suite requires ConnectionStrings__SqlServerTestConnection "
                + "pointing at a database whose name contains 'test'.");
        }

        return cs;
    }

    // ── Schema / CHECK / indexes ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureCreated_installs_checks_filtered_indexes_and_cascade_graph()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        await using var db = await host.Factory.CreateDbContextAsync();

        var checks = await db.Database.SqlQueryRaw<NameRow>("""
            SELECT name AS Value
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'dbo.PrWorkOrderLabour')
               OR parent_object_id = OBJECT_ID(N'dbo.PrWorkOrderMaterial')
               OR parent_object_id = OBJECT_ID(N'dbo.PrWorkOrderMachine')
            """).ToListAsync();
        Assert.Contains(checks, x => x.Value == "CK_PrWorkOrderLabour_ExclusiveOwner");
        Assert.Contains(checks, x => x.Value == "CK_PrWorkOrderMaterial_InternalWipProducer");

        var formatCheck = await db.Database.SqlQueryRaw<NameRow>("""
            SELECT definition AS Value
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'dbo.PrWorkOrder')
              AND name = N'CK_PrWorkOrder_SnapshotFormat'
            """).SingleAsync();
        Assert.Contains("3", formatCheck.Value, StringComparison.Ordinal);

        var indexes = await db.Database.SqlQueryRaw<NameRow>("""
            SELECT name AS Value
            FROM sys.indexes
            WHERE object_id IN (
                OBJECT_ID(N'dbo.PrWorkOrderRouteStep'),
                OBJECT_ID(N'dbo.PrWorkOrderOperation'),
                OBJECT_ID(N'dbo.PrWorkOrderMaterial'),
                OBJECT_ID(N'dbo.PrWorkOrderMachine'),
                OBJECT_ID(N'dbo.PrWorkOrderLabour'))
              AND name IS NOT NULL
            """).ToListAsync();
        Assert.Contains(indexes, x => x.Value == "UQ_PrWorkOrderRouteStep_Order_SourceKey");
        Assert.Contains(indexes, x => x.Value == "UQ_PrWorkOrderOperation_RouteStep_SourceKey");
        Assert.Contains(indexes, x => x.Value == "UQ_PrWorkOrderMaterial_Operation_SourceKey");
        Assert.Contains(indexes, x => x.Value == "UQ_PrWorkOrderMachine_Operation_SourceKey");
        Assert.Contains(indexes, x => x.Value == "UX_PrWorkOrderMachine_OneSelected");

        var labourFk = await db.Database.SqlQueryRaw<CascadeRow>("""
            SELECT fk.name AS Name, fk.delete_referential_action_desc AS DeleteAction
            FROM sys.foreign_keys fk
            WHERE fk.parent_object_id = OBJECT_ID(N'dbo.PrWorkOrderLabour')
            """).ToListAsync();
        Assert.Contains(labourFk, x =>
            x.Name.Contains("Machine", StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.DeleteAction, "CASCADE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(labourFk, x =>
            x.Name.Contains("Operation", StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.DeleteAction, "NO_ACTION", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Exclusive_owner_check_rejects_labour_with_both_owners()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var graph = await host.SeedHierarchyAsync(includeMachine: true, includeOperationLabour: false);

        await using var db = await host.Factory.CreateDbContextAsync();
        db.ProductionWorkOrderLabours.Add(new ProductionWorkOrderLabour
        {
            MachineId = graph.MachineId,
            OperationId = graph.OperationId,
            LabourCode = "BAD",
            RateBasis = ProductionLabourRateBases.PerOutputUnit,
            ContributesToPlan = true
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("CK_PrWorkOrderLabour_ExclusiveOwner", RootMessage(ex), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Internal_wip_producer_check_accepts_and_rejects_correct_rows()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var graph = await host.SeedHierarchyAsync(includeMachine: false, includeOperationLabour: false);

        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.ProductionWorkOrderMaterials.Add(ValidMaterial(graph, PrMaterialSupplySources.Purchased, producingRouteStepId: null));
            await db.SaveChangesAsync();
        }

        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.ProductionWorkOrderMaterials.Add(ValidMaterial(
                graph, PrMaterialSupplySources.InternalRouteWip, producingRouteStepId: null));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("CK_PrWorkOrderMaterial_InternalWipProducer", RootMessage(ex), StringComparison.OrdinalIgnoreCase);
        }

        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.ProductionWorkOrderMaterials.Add(ValidMaterial(
                graph, PrMaterialSupplySources.Purchased, producingRouteStepId: graph.RouteStepId));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("CK_PrWorkOrderMaterial_InternalWipProducer", RootMessage(ex), StringComparison.OrdinalIgnoreCase);
        }

        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.ProductionWorkOrderMaterials.Add(ValidMaterial(
                graph, PrMaterialSupplySources.InternalRouteWip, producingRouteStepId: graph.RouteStepId));
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Filtered_one_selected_machine_index_rejects_a_second_selected_row()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var graph = await host.SeedHierarchyAsync(includeMachine: true, includeOperationLabour: false);

        await using var db = await host.Factory.CreateDbContextAsync();
        db.ProductionWorkOrderMachines.Add(new ProductionWorkOrderMachine
        {
            OperationId = graph.OperationId,
            MachineCode = "MC02",
            Priority = 2,
            IsSelected = true,
            IsDefault = false,
            ParallelMachineCount = 1,
            CycleQuantityMode = ProductionMachineCycleQuantityModes.Discrete,
            OutputPerCycle = 1m
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(
            RootMessage(ex).Contains("UX_PrWorkOrderMachine_OneSelected", StringComparison.OrdinalIgnoreCase)
            || RootMessage(ex).Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Parallel_stage_sequences_and_duplicate_source_keys_behave_as_contracted()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var graph = await host.SeedHierarchyAsync(includeMachine: false, includeOperationLabour: false);
        var sharedKey = Guid.NewGuid();

        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.ProductionWorkOrderRouteSteps.Add(new ProductionWorkOrderRouteStep
            {
                WorkOrderId = graph.WorkOrderId,
                StageSequence = graph.StageSequence,
                WorkCentreCode = "WC02",
                OutputItemCode = "WIP02",
                OutputBaseQty = 1m,
                PlannedQty = 10m,
                OutputUom = "PCS",
                SourceRouteStepKey = Guid.NewGuid()
            });
            await db.SaveChangesAsync();
        }

        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            var original = await db.ProductionWorkOrderRouteSteps.SingleAsync(x => x.Uid == graph.RouteStepId);
            original.SourceRouteStepKey = sharedKey;
            await db.SaveChangesAsync();
        }

        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.ProductionWorkOrderRouteSteps.Add(new ProductionWorkOrderRouteStep
            {
                WorkOrderId = graph.WorkOrderId,
                StageSequence = 99,
                WorkCentreCode = "WC99",
                OutputItemCode = "WIP99",
                OutputBaseQty = 1m,
                PlannedQty = 10m,
                OutputUom = "PCS",
                SourceRouteStepKey = sharedKey
            });
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.True(
                RootMessage(ex).Contains("UQ_PrWorkOrderRouteStep_Order_SourceKey", StringComparison.OrdinalIgnoreCase)
                || RootMessage(ex).Contains("unique", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ── Labour delete paths (Option A1) ────────────────────────────────────────────────────────

    [Fact]
    public async Task Operation_level_labour_blocks_operation_delete_until_removed_explicitly()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var graph = await host.SeedHierarchyAsync(includeMachine: false, includeOperationLabour: true);

        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            var operation = await db.ProductionWorkOrderOperations.SingleAsync(x => x.Uid == graph.OperationId);
            db.ProductionWorkOrderOperations.Remove(operation);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("REFERENCE", RootMessage(ex), StringComparison.OrdinalIgnoreCase);
        }

        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            var labour = await db.ProductionWorkOrderLabours.SingleAsync(x => x.OperationId == graph.OperationId);
            db.ProductionWorkOrderLabours.Remove(labour);
            await db.SaveChangesAsync();

            var operation = await db.ProductionWorkOrderOperations.SingleAsync(x => x.Uid == graph.OperationId);
            db.ProductionWorkOrderOperations.Remove(operation);
            await db.SaveChangesAsync();
        }

        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            Assert.False(await db.ProductionWorkOrderOperations.AnyAsync(x => x.Uid == graph.OperationId));
            Assert.False(await db.ProductionWorkOrderLabours.AnyAsync(x => x.OperationId == graph.OperationId));
        }
    }

    [Fact]
    public async Task Machine_owned_labour_cascades_when_operation_is_deleted()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var graph = await host.SeedHierarchyAsync(includeMachine: true, includeOperationLabour: false);

        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.ProductionWorkOrderLabours.Add(new ProductionWorkOrderLabour
            {
                MachineId = graph.MachineId,
                LabourCode = "MACHLAB",
                RateBasis = ProductionLabourRateBases.PerOutputUnit,
                Rate = 1m,
                ContributesToPlan = true,
                PlannedAmount = 1m
            });
            await db.SaveChangesAsync();

            var operation = await db.ProductionWorkOrderOperations
                .Include(x => x.Machines)
                .SingleAsync(x => x.Uid == graph.OperationId);
            db.ProductionWorkOrderOperations.Remove(operation);
            await db.SaveChangesAsync();
        }

        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            Assert.False(await db.ProductionWorkOrderMachines.AnyAsync(x => x.Uid == graph.MachineId));
            Assert.False(await db.ProductionWorkOrderLabours.AnyAsync(x => x.MachineId == graph.MachineId));
        }
    }

    [Fact]
    public async Task Mixed_labour_ownership_cleans_up_when_operation_labour_is_deleted_first()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var graph = await host.SeedHierarchyAsync(includeMachine: true, includeOperationLabour: true);

        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.ProductionWorkOrderLabours.Add(new ProductionWorkOrderLabour
            {
                MachineId = graph.MachineId,
                LabourCode = "MACHLAB",
                RateBasis = ProductionLabourRateBases.PerOutputUnit,
                Rate = 1m,
                ContributesToPlan = true,
                PlannedAmount = 1m
            });
            await db.SaveChangesAsync();

            var opLabour = await db.ProductionWorkOrderLabours.SingleAsync(x => x.OperationId == graph.OperationId);
            db.ProductionWorkOrderLabours.Remove(opLabour);
            await db.SaveChangesAsync();

            var operation = await db.ProductionWorkOrderOperations.SingleAsync(x => x.Uid == graph.OperationId);
            db.ProductionWorkOrderOperations.Remove(operation);
            await db.SaveChangesAsync();
        }

        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            Assert.False(await db.ProductionWorkOrderOperations.AnyAsync(x => x.Uid == graph.OperationId));
            Assert.False(await db.ProductionWorkOrderMachines.AnyAsync(x => x.Uid == graph.MachineId));
            Assert.Equal(0, await db.ProductionWorkOrderLabours.CountAsync(x =>
                x.OperationId == graph.OperationId || x.MachineId == graph.MachineId));
        }
    }

    // ── Service concurrency / locks ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDraft_snapshot_hash_survives_sql_server_reload()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var created = await host.CreateDraftAsync();
        Assert.True(created.Succeeded, created.Message);
        var detail = created.Data!;

        await using var db = await host.Factory.CreateDbContextAsync();
        var entity = await db.ProductionWorkOrders
            .Include(x => x.Materials).ThenInclude(x => x.ProducingRouteStep)
            .Include(x => x.Materials).ThenInclude(x => x.WorkOrderOperation)
            .Include(x => x.Operations).ThenInclude(x => x.Machines).ThenInclude(x => x.Labours)
            .Include(x => x.Operations).ThenInclude(x => x.Labours)
            .Include(x => x.RouteSteps).ThenInclude(x => x.Operations).ThenInclude(x => x.Machines).ThenInclude(x => x.Labours)
            .Include(x => x.RouteSteps).ThenInclude(x => x.Operations).ThenInclude(x => x.Labours)
            .AsSplitQuery()
            .SingleAsync(x => x.WorkOrderNo == detail.WorkOrderNo);

        Assert.Equal(detail.SnapshotHash, entity.SnapshotHash);
        Assert.Equal(entity.SnapshotHash, WorkOrderSnapshotHasher.ComputeSnapshotHash(entity));
        Assert.All(entity.Materials, m => Assert.NotNull(m.WorkOrderOperation));
    }

    [Fact]
    public async Task New_current_snapshot_can_create_and_release_without_definition_refresh()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var released = await host.CreateService().CreateAndReleaseAsync(host.DraftRequest());
        Assert.True(released.Succeeded, released.Message);
        var detail = released.Data!;

        Assert.Equal(ProductionWorkOrderStatuses.Released, detail.Status);
        Assert.Equal(ProductionSnapshotFormatVersions.Current, detail.SnapshotFormatVersion);
        Assert.False(detail.IsLegacySnapshot);
        Assert.NotNull(detail.SourceProductDefinitionRevisionId);
        Assert.False(string.IsNullOrWhiteSpace(detail.DefinitionSourceHash));
        Assert.NotEmpty(detail.RouteSteps);
        Assert.NotEmpty(detail.Operations);
        Assert.NotEmpty(detail.Materials);
        Assert.NotNull(detail.ReleasedDate);
        Assert.Equal(1, detail.AuditEvents.Count(x => x.EventType == ProductionAuditEventTypes.Created));
        Assert.Equal(1, detail.AuditEvents.Count(x => x.EventType == ProductionAuditEventTypes.Released));
        Assert.DoesNotContain(detail.AuditEvents, x => x.EventType == ProductionAuditEventTypes.Refreshed);

        await using var db = await host.Factory.CreateDbContextAsync();
        var stored = await db.ProductionWorkOrders.SingleAsync(x => x.WorkOrderNo == detail.WorkOrderNo);
        Assert.Equal(ProductionSnapshotHashVersions.Current, stored.SnapshotHashVersion);
    }

    [Fact]
    public async Task Create_and_release_failure_does_not_leave_a_sql_server_work_order()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var released = await host.CreateService(releaseEnabled: false).CreateAndReleaseAsync(host.DraftRequest());
        Assert.False(released.Succeeded);
        Assert.Contains(ProductionReadinessErrorCodes.ReleaseDisabled, released.Message, StringComparison.Ordinal);

        await using var db = await host.Factory.CreateDbContextAsync();
        Assert.False(await db.ProductionWorkOrders.AnyAsync(x => x.ProductCode == host.ProductCode));
    }

    [Fact]
    public async Task Update_and_release_with_stale_tokens_never_partially_updates_or_releases()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var created = await host.CreateDraftAsync();
        Assert.True(created.Succeeded, created.Message);
        var detail = created.Data!;
        var staleVersion = detail.RowVersion.ToArray();
        staleVersion[0] ^= 0xFF;

        var raced = await host.CreateService().UpdateAndReleaseAsync(new ProductionWorkOrderUpdateAndReleaseRequest
        {
            WorkOrderNo = detail.WorkOrderNo,
            PlannedQty = detail.PlannedQty + 5m,
            SchedulingDirection = detail.SchedulingDirection,
            ScheduleAnchorDateTime = detail.ScheduleAnchorDateTime,
            Remark = "stale update and release",
            RowVersion = staleVersion,
            SnapshotRevision = detail.SnapshotRevision,
            SnapshotHash = detail.SnapshotHash,
            SourceProductDefinitionRevisionId = detail.SourceProductDefinitionRevisionId
        });

        Assert.False(raced.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, raced.ErrorCode);
        var reload = await host.CreateService().GetAsync(detail.WorkOrderNo);
        Assert.Equal(ProductionWorkOrderStatuses.Draft, reload.Data!.Status);
        Assert.Equal(detail.PlannedQty, reload.Data.PlannedQty);
        Assert.Equal(detail.Remark, reload.Data.Remark);
        Assert.Equal(detail.SnapshotRevision, reload.Data.SnapshotRevision);
        Assert.Equal(detail.SnapshotHash, reload.Data.SnapshotHash);
    }

    [Fact]
    public async Task Two_releases_with_the_same_tokens_leave_exactly_one_released_order()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var created = await host.CreateDraftAsync();
        Assert.True(created.Succeeded, created.Message);
        var detail = created.Data!;
        var gate = new ManualResetEventSlim(false);

        async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> ReleaseAsync()
        {
            var sut = host.CreateService();
            gate.Wait();
            return await sut.ReleaseCurrentAsync(new ProductionWorkOrderReleaseRequest
            {
                WorkOrderNo = detail.WorkOrderNo,
                RowVersion = detail.RowVersion,
                SnapshotRevision = detail.SnapshotRevision,
                SnapshotHash = detail.SnapshotHash,
                SourceProductDefinitionRevisionId = detail.SourceProductDefinitionRevisionId
            });
        }

        var taskA = Task.Run(ReleaseAsync);
        var taskB = Task.Run(ReleaseAsync);
        gate.Set();
        var results = await Task.WhenAll(taskA, taskB);

        var winners = results.Where(x => x.Succeeded).ToList();
        var losers = results.Where(x => !x.Succeeded).ToList();
        Assert.True(
            winners.Count == 1,
            "Expected one Release to succeed. Failures: "
            + string.Join(" | ", losers.Select(x => $"{x.ErrorCode}:{x.Message}")));
        Assert.Single(losers);
        var reload = await host.CreateService().GetAsync(detail.WorkOrderNo);
        Assert.Equal(ProductionWorkOrderStatuses.Released, reload.Data!.Status);
    }

    [Fact]
    public async Task Reopen_racing_new_material_issue_draft_lock_order_has_exclusive_outcome()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var created = await host.CreateDraftAsync();
        Assert.True(created.Succeeded, created.Message);
        var released = await host.CreateService().ReleaseCurrentAsync(new ProductionWorkOrderReleaseRequest
        {
            WorkOrderNo = created.Data!.WorkOrderNo,
            RowVersion = created.Data.RowVersion,
            SnapshotRevision = created.Data.SnapshotRevision,
            SnapshotHash = created.Data.SnapshotHash,
            SourceProductDefinitionRevisionId = created.Data.SourceProductDefinitionRevisionId
        });
        Assert.True(released.Succeeded, released.Message);
        var detail = released.Data!;
        var gate = new ManualResetEventSlim(false);

        async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> ReopenAsync()
        {
            var sut = host.CreateService();
            gate.Wait();
            return await sut.ReopenForEditAsync(new ProductionWorkOrderReopenRequest
            {
                WorkOrderNo = detail.WorkOrderNo,
                RowVersion = detail.RowVersion,
                Reason = "SQL race reopen vs new MI draft"
            });
        }

        // Mirrors ProductionMaterialIssueService.Draft: lock Work Order first, then insert PostingLink.
        async Task<(bool Succeeded, string? Message)> SimulateNewMaterialIssueDraftAsync()
        {
            gate.Wait();
            await using var db = await host.Factory.CreateDbContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                var order = await db.ProductionWorkOrders
                    .FromSqlInterpolated($@"
                        SELECT *
                        FROM dbo.PrWorkOrder WITH (UPDLOCK, HOLDLOCK)
                        WHERE CompanyCode = {Company}
                          AND BranchCode = {Branch}
                          AND WorkOrderNo = {detail.WorkOrderNo}")
                    .SingleOrDefaultAsync();
                if (order is null)
                {
                    return (false, "Work Order was not found.");
                }

                if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
                {
                    return (false, "Work Order status does not allow material issue.");
                }

                db.ProductionPostingLinks.Add(new ProductionPostingLink
                {
                    CompanyCode = order.CompanyCode,
                    BranchCode = order.BranchCode,
                    CommandType = ProductionPostingCommandTypes.MaterialIssuePost,
                    PostingRequestId = Guid.NewGuid().ToString("N"),
                    WorkOrderId = order.Uid,
                    InventoryBatchNo = Random.Shared.Next(200000, 299999),
                    SnapshotRevision = order.SnapshotRevision,
                    SnapshotHash = order.SnapshotHash,
                    Status = ProductionPostingLinkStatuses.Draft,
                    CreatedDate = DateTime.UtcNow,
                    CreatedBy = "race-mi"
                });
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return (true, null);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, ex.Message);
            }
        }

        var reopenTask = Task.Run(ReopenAsync);
        var miTask = Task.Run(SimulateNewMaterialIssueDraftAsync);
        gate.Set();
        await Task.WhenAll(reopenTask, miTask);

        var reopen = await reopenTask;
        var mi = await miTask;
        await using var verify = await host.Factory.CreateDbContextAsync();
        var order = await verify.ProductionWorkOrders.SingleAsync(x => x.WorkOrderNo == detail.WorkOrderNo);
        var draftLinks = await verify.ProductionPostingLinks
            .Where(x => x.WorkOrderId == order.Uid && x.Status == ProductionPostingLinkStatuses.Draft)
            .ToListAsync();

        Assert.False(
            order.Status == ProductionWorkOrderStatuses.Draft && draftLinks.Count > 0,
            "Forbidden: Draft Work Order with an active Material Issue draft.");
        Assert.True(
            (reopen.Succeeded && !mi.Succeeded && order.Status == ProductionWorkOrderStatuses.Draft && draftLinks.Count == 0)
            || (!reopen.Succeeded
                && reopen.ErrorCode == IvMasterErrorCode.InUse
                && mi.Succeeded
                && order.Status == ProductionWorkOrderStatuses.Released
                && draftLinks.Count == 1),
            $"Unexpected race outcome. Reopen={reopen.ErrorCode}:{reopen.Message}; MI={(mi.Succeeded ? "ok" : mi.Message)}; Status={order.Status}; DraftLinks={draftLinks.Count}");
    }

    [Fact]
    public async Task Reopen_racing_existing_material_issue_draft_post_cannot_leave_draft_with_execution()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var created = await host.CreateDraftAsync();
        Assert.True(created.Succeeded, created.Message);
        var released = await host.CreateService().ReleaseCurrentAsync(new ProductionWorkOrderReleaseRequest
        {
            WorkOrderNo = created.Data!.WorkOrderNo,
            RowVersion = created.Data.RowVersion,
            SnapshotRevision = created.Data.SnapshotRevision,
            SnapshotHash = created.Data.SnapshotHash,
            SourceProductDefinitionRevisionId = created.Data.SourceProductDefinitionRevisionId
        });
        Assert.True(released.Succeeded, released.Message);
        var detail = released.Data!;

        long linkId;
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            var order = await db.ProductionWorkOrders.SingleAsync(x => x.WorkOrderNo == detail.WorkOrderNo);
            var link = new ProductionPostingLink
            {
                CompanyCode = order.CompanyCode,
                BranchCode = order.BranchCode,
                CommandType = ProductionPostingCommandTypes.MaterialIssuePost,
                PostingRequestId = Guid.NewGuid().ToString("N"),
                WorkOrderId = order.Uid,
                InventoryBatchNo = Random.Shared.Next(300000, 399999),
                SnapshotRevision = order.SnapshotRevision,
                SnapshotHash = order.SnapshotHash,
                Status = ProductionPostingLinkStatuses.Draft,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = "seed-mi"
            };
            db.ProductionPostingLinks.Add(link);
            await db.SaveChangesAsync();
            linkId = link.Uid;
        }

        var gate = new ManualResetEventSlim(false);

        async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> ReopenAsync()
        {
            var sut = host.CreateService();
            gate.Wait();
            return await sut.ReopenForEditAsync(new ProductionWorkOrderReopenRequest
            {
                WorkOrderNo = detail.WorkOrderNo,
                RowVersion = detail.RowVersion,
                Reason = "SQL race reopen vs existing MI post"
            });
        }

        // Mirrors posting path dependency: lock existing PostingLink, then Work Order, then mutate.
        async Task<(bool Succeeded, string? Message)> SimulatePostExistingDraftAsync()
        {
            gate.Wait();
            await using var db = await host.Factory.CreateDbContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                var link = await db.ProductionPostingLinks
                    .FromSqlInterpolated($@"
                        SELECT *
                        FROM dbo.PrProductionPostingLink WITH (UPDLOCK, HOLDLOCK)
                        WHERE UID = {linkId}")
                    .SingleOrDefaultAsync();
                if (link is null || link.Status != ProductionPostingLinkStatuses.Draft)
                {
                    return (false, "Draft posting link was not available.");
                }

                var order = await db.ProductionWorkOrders
                    .FromSqlInterpolated($@"
                        SELECT *
                        FROM dbo.PrWorkOrder WITH (UPDLOCK, HOLDLOCK)
                        WHERE UID = {link.WorkOrderId}")
                    .SingleOrDefaultAsync();
                if (order is null)
                {
                    return (false, "Work Order was not found.");
                }

                if (order.Status == ProductionWorkOrderStatuses.Draft)
                {
                    return (false, "Work Order became Draft before post.");
                }

                link.Status = ProductionPostingLinkStatuses.Succeeded;
                link.CompletedDate = DateTime.UtcNow;
                order.Status = ProductionWorkOrderStatuses.InProgress;
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return (true, null);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, ex.Message);
            }
        }

        var reopenTask = Task.Run(ReopenAsync);
        var postTask = Task.Run(SimulatePostExistingDraftAsync);
        gate.Set();
        await Task.WhenAll(reopenTask, postTask);

        var reopen = await reopenTask;
        _ = await postTask;
        await using var verify = await host.Factory.CreateDbContextAsync();
        var latest = await verify.ProductionWorkOrders.SingleAsync(x => x.WorkOrderNo == detail.WorkOrderNo);
        var linkStatus = await verify.ProductionPostingLinks
            .Where(x => x.Uid == linkId)
            .Select(x => x.Status)
            .SingleAsync();

        Assert.False(reopen.Succeeded);
        Assert.Equal(IvMasterErrorCode.InUse, reopen.ErrorCode);
        Assert.NotEqual(ProductionWorkOrderStatuses.Draft, latest.Status);
        Assert.False(
            latest.Status == ProductionWorkOrderStatuses.Draft
            && linkStatus == ProductionPostingLinkStatuses.Succeeded,
            "Forbidden: Draft Work Order while material post succeeded.");
    }

    [Fact]
    public async Task Draft_header_update_racing_release_leaves_a_consistent_aggregate()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var created = await host.CreateDraftAsync();
        Assert.True(created.Succeeded, created.Message);
        var detail = created.Data!;
        var gate = new ManualResetEventSlim(false);

        async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> ReleaseAsync()
        {
            var sut = host.CreateService();
            gate.Wait();
            return await sut.ReleaseCurrentAsync(new ProductionWorkOrderReleaseRequest
            {
                WorkOrderNo = detail.WorkOrderNo,
                RowVersion = detail.RowVersion,
                SnapshotRevision = detail.SnapshotRevision,
                SnapshotHash = detail.SnapshotHash,
                SourceProductDefinitionRevisionId = detail.SourceProductDefinitionRevisionId
            });
        }

        async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> UpdateAsync()
        {
            var sut = host.CreateService();
            gate.Wait();
            return await sut.UpdateDraftHeaderAsync(new ProductionWorkOrderHeaderUpdate
            {
                WorkOrderNo = detail.WorkOrderNo,
                PlannedQty = detail.PlannedQty,
                SchedulingDirection = detail.SchedulingDirection,
                ScheduleAnchorDateTime = detail.ScheduleAnchorDateTime,
                Remark = "race update",
                RowVersion = detail.RowVersion
            });
        }

        var releaseTask = Task.Run(ReleaseAsync);
        var updateTask = Task.Run(UpdateAsync);
        gate.Set();
        var results = await Task.WhenAll(releaseTask, updateTask);

        Assert.Equal(1, results.Count(x => x.Succeeded));
        Assert.Equal(1, results.Count(x => !x.Succeeded));

        var reload = await host.CreateService().GetAsync(detail.WorkOrderNo);
        Assert.True(reload.Succeeded, reload.Message);
        Assert.Contains(reload.Data!.Status, new[]
        {
            ProductionWorkOrderStatuses.Draft,
            ProductionWorkOrderStatuses.Released
        });
        if (reload.Data.Status == ProductionWorkOrderStatuses.Released)
        {
            Assert.Equal(detail.Remark, reload.Data.Remark);
        }
        else
        {
            Assert.Equal("race update", reload.Data.Remark);
        }
    }

    [Fact]
    public async Task Recalculate_advances_header_rowversion_exactly_once()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var created = await host.CreateDraftAsync();
        Assert.True(created.Succeeded, created.Message);
        var before = created.Data!;

        var recalculated = await host.CreateService().RecalculateDraftScheduleAsync(
            new ProductionWorkOrderRecalculateRequest
            {
                WorkOrderNo = before.WorkOrderNo,
                RowVersion = before.RowVersion,
                SnapshotRevision = before.SnapshotRevision,
                SnapshotHash = before.SnapshotHash
            });
        Assert.True(recalculated.Succeeded, recalculated.Message);
        Assert.False(before.RowVersion.SequenceEqual(recalculated.Data!.RowVersion));
        Assert.Equal(before.SnapshotRevision + 1, recalculated.Data.SnapshotRevision);

        var stale = await host.CreateService().RecalculateDraftScheduleAsync(
            new ProductionWorkOrderRecalculateRequest
            {
                WorkOrderNo = before.WorkOrderNo,
                RowVersion = before.RowVersion,
                SnapshotRevision = before.SnapshotRevision,
                SnapshotHash = before.SnapshotHash
            });
        Assert.False(stale.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, stale.ErrorCode);
    }

    [Fact]
    public async Task Exclusive_scheduling_lock_blocks_recalculate_with_source_busy()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var created = await host.CreateDraftAsync();
        Assert.True(created.Succeeded, created.Message);
        var detail = created.Data!;

        var previousTimeout = WorkOrderSchedulingLock.TimeoutMs;
        WorkOrderSchedulingLock.TimeoutMs = 1_000;
        try
        {
            await using var locker = await host.Factory.CreateDbContextAsync();
            await using var tx = await locker.Database.BeginTransactionAsync();
            await WorkOrderSchedulingLock.AcquireAsync(locker, Company, exclusive: true);

            var blocked = await host.CreateService().RecalculateDraftScheduleAsync(
                new ProductionWorkOrderRecalculateRequest
                {
                    WorkOrderNo = detail.WorkOrderNo,
                    RowVersion = detail.RowVersion,
                    SnapshotRevision = detail.SnapshotRevision,
                    SnapshotHash = detail.SnapshotHash
                });

            Assert.False(blocked.Succeeded);
            Assert.Contains(
                ProductionReadinessErrorCodes.SchedulingSourceBusy,
                blocked.Message,
                StringComparison.Ordinal);
            await tx.RollbackAsync();
        }
        finally
        {
            WorkOrderSchedulingLock.TimeoutMs = previousTimeout;
        }
    }

    [Fact]
    public async Task Failed_child_insert_rolls_back_the_aggregate()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var graph = await host.SeedHierarchyAsync(includeMachine: true, includeOperationLabour: false);

        await using var db = await host.Factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        db.ProductionWorkOrderMachines.Add(new ProductionWorkOrderMachine
        {
            OperationId = graph.OperationId,
            MachineCode = "MC-DUP",
            Priority = 2,
            IsSelected = true,
            ParallelMachineCount = 1,
            CycleQuantityMode = ProductionMachineCycleQuantityModes.Discrete,
            OutputPerCycle = 1m
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        await tx.RollbackAsync();

        await using var verify = await host.Factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.ProductionWorkOrderMachines.CountAsync(x => x.OperationId == graph.OperationId));
        Assert.True(await verify.ProductionWorkOrders.AnyAsync(x => x.Uid == graph.WorkOrderId));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static ProductionWorkOrderMaterial ValidMaterial(
        HierarchyIds graph,
        string supplySource,
        long? producingRouteStepId) => new()
    {
        WorkOrderId = graph.WorkOrderId,
        WorkOrderOperationId = graph.OperationId,
        LineNo = Random.Shared.Next(1, 1_000_000),
        BomPath = "/RM001",
        ComponentCode = "RM001",
        MfgType = PrMfgTypes.Buy,
        ComponentQtyPerParent = 1m,
        BomOutputQty = 1m,
        ScrapPercent = 0m,
        Tolerance = 0m,
        IssueMethod = PrMaterialIssueMethods.Manual,
        SupplySource = supplySource,
        RequiredQty = 1m,
        RequiredBaseQty = 1m,
        ConversionFactorToBase = 1m,
        ProducingRouteStepId = producingRouteStepId
    };

    private static string RootMessage(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message;
    }

    private sealed class NameRow
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class CascadeRow
    {
        public string Name { get; set; } = string.Empty;
        public string DeleteAction { get; set; } = string.Empty;
    }

    private sealed record HierarchyIds(
        long WorkOrderId,
        long RouteStepId,
        long OperationId,
        long? MachineId,
        int StageSequence);

    private sealed class Host : IAsyncDisposable
    {
        private Host(
            IDbContextFactory<AppDbContext> factory,
            string productCode,
            long bomHdrId)
        {
            Factory = factory;
            ProductCode = productCode;
            BomHdrId = bomHdrId;
        }

        public IDbContextFactory<AppDbContext> Factory { get; }
        public string ProductCode { get; }
        public long BomHdrId { get; }

        public static async Task<Host> CreateAsync(string connectionString)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
            IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);

            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var productCode = "WO" + suffix;
            var componentCode = "RM" + suffix;

            long bomHdrId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                if (!await db.IvWarehouses.AnyAsync(x =>
                        x.CompanyCode == Company && x.BranchCode == Branch && x.WarehouseCode == "WH01"))
                {
                    db.IvWarehouses.Add(new IvWarehouse
                    {
                        CompanyCode = Company,
                        BranchCode = Branch,
                        WarehouseCode = "WH01",
                        WarehouseDesc = "Main",
                        IsActive = true
                    });
                }

                db.IvStockMasters.AddRange(
                    new IvStockMaster
                    {
                        CompanyCode = Company,
                        ICode = productCode,
                        IDesc = "WO SQL finished",
                        StdUom = "PCS",
                        MfgType = PrMfgTypes.Make,
                        DefWarehouse = "WH01",
                        IsActive = true
                    },
                    new IvStockMaster
                    {
                        CompanyCode = Company,
                        ICode = componentCode,
                        IDesc = "WO SQL component",
                        StdUom = "PCS",
                        MfgType = PrMfgTypes.Buy,
                        DefWarehouse = "WH01",
                        IsActive = true
                    });

                if (!await db.PrWorkCentres.AnyAsync(x => x.CompCode == Company && x.WrkCtrCd == "WC01"))
                {
                    db.PrWorkCentres.Add(new PrWorkCentre
                    {
                        CompCode = Company,
                        WrkCtrCd = "WC01",
                        WrkCtrDes = "Centre"
                    });
                }

                if (!await db.PrProcesses.AnyAsync(x => x.CompCode == Company && x.ProcessCd == "FIN"))
                {
                    db.PrProcesses.Add(new PrProcess
                    {
                        CompCode = Company,
                        ProcessCd = "FIN",
                        ProcessDes = "Finish",
                        WorkCentre = "WC01"
                    });
                }

                var operationKey = Guid.NewGuid();
                var header = new PrBomHdr
                {
                    CompanyCode = Company,
                    ProdCode = productCode,
                    Version = 1,
                    Status = PrBomStatuses.Active,
                    EffectiveFrom = new DateTime(2026, 1, 1),
                    BaseQty = 1m,
                    BaseUom = "PCS",
                    BranchCode = Branch,
                    LocationCode = "SITE"
                };
                var operation = new PrBomOperation
                {
                    CompanyCode = Company,
                    WorkCentreCode = "WC01",
                    OutputItemCode = productCode,
                    CentralSequence = 10,
                    OperationCode = "FIN",
                    ProcessSequence = 10,
                    ProcessType = PrProcessTypes.Manual,
                    StandardDurationMinutes = 30m,
                    IsFinalOperation = true,
                    OutputBaseQty = 1m,
                    OutputUom = "PCS",
                    OperationKey = operationKey
                };
                var step = new PrBomRouteStep
                {
                    CompanyCode = Company,
                    WorkCentreCode = "WC01",
                    StageSequence = 10,
                    OutputItemCode = productCode,
                    OutputType = PrRouteOutputTypes.FinishedGoods,
                    StandardOutputQty = 1m,
                    OutputUom = "PCS",
                    Operations = { operation }
                };
                header.RouteSteps.Add(step);
                header.Operations.Add(operation);
                header.Lines.Add(new PrDefBOM
                {
                    CompanyCode = Company,
                    ProdCode = productCode,
                    ICode = componentCode,
                    IName = "Component",
                    StdQty = 1m,
                    StdUom = "PCS",
                    SeqNo = 1,
                    Warehouse = "WH01",
                    BranchCode = Branch,
                    LocationCode = "SITE",
                    OperationKey = operationKey,
                    Operation = operation
                });
                db.PrBomHdrs.Add(header);
                await db.SaveChangesAsync();
                bomHdrId = header.Uid;
            }

            return new Host(factory, productCode, bomHdrId);
        }

        public ProductionWorkOrderDraftRequest DraftRequest() => new()
        {
            ProductCode = ProductCode,
            PlannedQty = 10m,
            DefinitionCode = PrProductDefinitionCodes.Standard,
            PlannedStartDate = new DateTime(2026, 10, 1, 8, 0, 0),
            PlannedCompletionDate = new DateTime(2026, 10, 3, 17, 0, 0),
            SchedulingDirection = ProductionSchedulingDirections.Forward,
            SourceType = ProductionSourceTypes.Manual,
            Remark = "SQL Server concurrency"
        };

        public ProductionWorkOrderService CreateService(bool releaseEnabled = true)
        {
            var access = new Mock<IAccessRightService>();
            access.Setup(x => x.CanAsync(
                    MenuCodes.PlanningWorkOrder,
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var productAccess = new Mock<IAccessRightService>();
            productAccess.Setup(x => x.CanAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var tenant = InventoryTenantTestHelper.CreateTenantContext(branch: Branch, location: "SITE");
            var explosion = new BomExplosionService(Factory, tenant, productAccess.Object);
            var uom = new IvUomConversionService(Factory);
            var quantities = new WorkOrderQuantityCalculator(uom);
            return new ProductionWorkOrderService(
                Factory,
                tenant,
                access.Object,
                new RunningNumberService(),
                explosion,
                new FakeProductionCalendarScheduleDataLoader(),
                new WorkOrderSnapshotBuilder(Factory, new ProductDefinitionSnapshotLoader(Factory), quantities),
                quantities,
                new WorkOrderScheduleCalculator(new AlwaysOpenWorkOrderCalendarProvider()),
                new WorkOrderReadinessValidator(),
                Options.Create(new ProductionWorkOrderOptions { ReleaseEnabled = releaseEnabled }));
        }

        public Task<IvMasterOperationResult<ProductionWorkOrderDetail>> CreateDraftAsync() =>
            CreateService().CreateDraftAsync(DraftRequest());

        public async Task<HierarchyIds> SeedHierarchyAsync(bool includeMachine, bool includeOperationLabour)
        {
            await using var db = await Factory.CreateDbContextAsync();
            var order = new ProductionWorkOrder
            {
                CompanyCode = Company,
                BranchCode = Branch,
                LocationCode = "SITE",
                WorkOrderNo = "WO-SQL-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
                SnapshotRevision = 1,
                SnapshotHash = new string('a', 64),
                DefinitionEffectiveDate = new DateTime(2026, 9, 28),
                ProductCode = ProductCode,
                OutputUom = "PCS",
                SourceBomHdrId = BomHdrId,
                SourceBomVersion = 1,
                BomBaseQty = 1m,
                BomBaseUom = "PCS",
                PlannedQty = 10m,
                RemainingQty = 10m,
                PlannedStartDateTime = new DateTime(2026, 10, 1, 8, 0, 0),
                PlannedCompletionDateTime = new DateTime(2026, 10, 1, 17, 0, 0),
                SchedulingDirection = ProductionSchedulingDirections.Forward,
                Status = ProductionWorkOrderStatuses.Draft,
                SourceType = ProductionSourceTypes.Manual,
                SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
                SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
                IsLegacySnapshot = false,
                ScheduleAnchorDateTime = new DateTime(2026, 10, 1, 8, 0, 0)
            };

            var step = new ProductionWorkOrderRouteStep
            {
                StageSequence = 10,
                WorkCentreCode = "WC01",
                OutputItemCode = ProductCode,
                OutputBaseQty = 1m,
                PlannedQty = 10m,
                OutputUom = "PCS",
                SourceRouteStepKey = Guid.NewGuid()
            };
            var operation = new ProductionWorkOrderOperation
            {
                SequenceNo = 10,
                ProcessSequence = 10,
                ProcessType = includeMachine ? PrProcessTypes.Machine : PrProcessTypes.Manual,
                OperationCode = "FIN",
                StandardDurationMinutes = includeMachine ? 0m : 30m,
                IsFinalOperation = true,
                PlannedQty = 10m,
                PlannedOutputQty = 10m,
                PlannedOutputUom = "PCS",
                SourceOperationKey = Guid.NewGuid()
            };
            step.Operations.Add(operation);
            order.RouteSteps.Add(step);
            order.Operations.Add(operation);

            ProductionWorkOrderMachine? machine = null;
            if (includeMachine)
            {
                machine = new ProductionWorkOrderMachine
                {
                    MachineCode = "MC01",
                    Priority = 1,
                    IsDefault = true,
                    IsSelected = true,
                    ParallelMachineCount = 1,
                    CycleQuantityMode = ProductionMachineCycleQuantityModes.Discrete,
                    OutputPerCycle = 1m,
                    SourceMachineKey = Guid.NewGuid()
                };
                operation.Machines.Add(machine);
            }

            if (includeOperationLabour)
            {
                operation.Labours.Add(new ProductionWorkOrderLabour
                {
                    LabourCode = "OPLAB",
                    RateBasis = ProductionLabourRateBases.PerOutputUnit,
                    Rate = 1m,
                    ContributesToPlan = true,
                    PlannedAmount = 1m,
                    SourceLabourKey = Guid.NewGuid()
                });
            }

            db.ProductionWorkOrders.Add(order);
            await db.SaveChangesAsync();

            return new HierarchyIds(
                order.Uid,
                step.Uid,
                operation.Uid,
                machine?.Uid,
                step.StageSequence);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
