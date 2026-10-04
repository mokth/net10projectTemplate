using System.Reflection;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ErpWeb.Tests.Planning.Transaction;
/// <summary>
/// Milestone 1 schema contract (plan §6). These tests pin the parts of the model that the plan
/// calls out explicitly, so a later refactor cannot silently:
/// <list type="bullet">
///   <item><description>reintroduce a second structural cascade path from <c>PrWorkOrder</c> into labour (which SQL Server rejects);</description></item>
///   <item><description>make a parallel-capable sequence unique;</description></item>
///   <item><description>lose the time-of-day on schedule timestamps;</description></item>
///   <item><description>drop the NULL-safe <c>INTERNAL_ROUTE_WIP</c> producer check;</description></item>
///   <item><description>allow two selected machine options on one operation.</description></item>
/// </list>
/// </summary>
[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionWorkOrderSchemaTests
{
    private static readonly IModel Model = BuildModel();

    private static IModel BuildModel()
    {
        // The SQL Server provider is used for the schema contract because it is the deployment
        // target: the SQLite provider silently drops filtered indexes from the model, which would
        // hide the route-step / source-key uniqueness and the single-selected-machine rule.
        // Building the model does not open a connection.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\ModelOnly;Database=ModelOnly;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        using var db = new AppDbContext(options);
        // Check constraints and index filters are relational-only annotations, so read the
        // design-time model rather than the read-optimized runtime model.
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static AppDbContext SqlServerContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\ModelOnly;Database=ModelOnly;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options);

    [Fact]
    public void SqlServer_create_script_installs_the_delete_graph_without_duplicate_cascade_paths()
    {
        using var db = SqlServerContext();
        var script = db.Database.GenerateCreateScript();

        // Exactly one cascade from PrWorkOrder into the hierarchy root.
        Assert.Contains("FK_PrWorkOrderRouteStep_PrWorkOrder", script, StringComparison.Ordinal);
        // Descendant direct Work Order foreign keys must be NO ACTION.
        Assert.Contains("ON DELETE NO ACTION", script, StringComparison.Ordinal);
        // The two cascade edges that carry labour.
        Assert.Contains("FK_PrWorkOrderLabour_PrWorkOrderMachine", script, StringComparison.Ordinal);
        // Route step identity uniqueness is a partial index.
        Assert.Contains("WHERE", script, StringComparison.Ordinal);
        // The exclusive-owner check and the NULL-safe producer check are installed.
        Assert.Contains("CK_PrWorkOrderLabour_ExclusiveOwner", script, StringComparison.Ordinal);
        Assert.Contains("CK_PrWorkOrderMaterial_InternalWipProducer", script, StringComparison.Ordinal);
        // Parallel-capable sequences are never unique.
        Assert.Contains("CREATE INDEX [IX_PrWorkOrderRouteStep_Order_Stage]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE UNIQUE INDEX [IX_PrWorkOrderRouteStep_Order_Stage]", script, StringComparison.Ordinal);
    }

    private static IEntityType Entity<T>() =>
        Model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped.");

    private static IForeignKey ForeignKey<T>(string propertyName) =>
        Entity<T>().GetForeignKeys()
            .Single(fk => fk.Properties.Count == 1 && fk.Properties[0].Name == propertyName);

    // ── Delete graph: exactly one structural cascade path into labour ──────────────────────────

    [Fact]
    public void WorkOrder_to_route_step_cascades()
    {
        var fk = ForeignKey<ProductionWorkOrderRouteStep>(nameof(ProductionWorkOrderRouteStep.WorkOrderId));
        Assert.Equal(typeof(ProductionWorkOrder), fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }

    [Fact]
    public void Route_step_to_operation_cascades()
    {
        var fk = ForeignKey<ProductionWorkOrderOperation>(nameof(ProductionWorkOrderOperation.RouteStepId));
        Assert.Equal(typeof(ProductionWorkOrderRouteStep), fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }

    [Fact]
    public void Operation_to_material_cascades()
    {
        var fk = ForeignKey<ProductionWorkOrderMaterial>(nameof(ProductionWorkOrderMaterial.WorkOrderOperationId));
        Assert.Equal(typeof(ProductionWorkOrderOperation), fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }

    [Fact]
    public void Operation_to_machine_cascades()
    {
        var fk = ForeignKey<ProductionWorkOrderMachine>(nameof(ProductionWorkOrderMachine.OperationId));
        Assert.Equal(typeof(ProductionWorkOrderOperation), fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }

    [Fact]
    public void Machine_to_machine_owned_labour_cascades()
    {
        var fk = ForeignKey<ProductionWorkOrderLabour>(nameof(ProductionWorkOrderLabour.MachineId));
        Assert.Equal(typeof(ProductionWorkOrderMachine), fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }

    [Fact]
    public void Operation_to_direct_labour_is_no_action()
    {
        var fk = ForeignKey<ProductionWorkOrderLabour>(nameof(ProductionWorkOrderLabour.OperationId));
        Assert.Equal(typeof(ProductionWorkOrderOperation), fk.PrincipalEntityType.ClrType);
        // Option A1: Operation -> Labour must not cascade, otherwise two structural paths lead into
        // labour and SQL Server rejects the schema with a multiple-cascade-path error.
        Assert.Equal(DeleteBehavior.NoAction, fk.DeleteBehavior);
    }

    [Fact]
    public void Material_producer_reference_is_no_action()
    {
        var fk = ForeignKey<ProductionWorkOrderMaterial>(nameof(ProductionWorkOrderMaterial.ProducingRouteStepId));
        Assert.Equal(typeof(ProductionWorkOrderRouteStep), fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.NoAction, fk.DeleteBehavior);
    }

    [Fact]
    public void Descendant_direct_work_order_foreign_keys_do_not_cascade()
    {
        // The owning path already reaches these tables through RouteStep -> Operation. A second
        // cascading path from PrWorkOrder would be rejected by SQL Server (plan §6.6).
        Assert.Equal(
            DeleteBehavior.NoAction,
            ForeignKey<ProductionWorkOrderMaterial>(nameof(ProductionWorkOrderMaterial.WorkOrderId)).DeleteBehavior);
        Assert.Equal(
            DeleteBehavior.NoAction,
            ForeignKey<ProductionWorkOrderOperation>(nameof(ProductionWorkOrderOperation.WorkOrderId)).DeleteBehavior);
    }

    [Fact]
    public void Labour_has_exactly_one_cascading_foreign_key()
    {
        var cascades = Entity<ProductionWorkOrderLabour>()
            .GetForeignKeys()
            .Where(fk => fk.DeleteBehavior == DeleteBehavior.Cascade)
            .ToList();

        var only = Assert.Single(cascades);
        Assert.Equal(typeof(ProductionWorkOrderMachine), only.PrincipalEntityType.ClrType);
    }

    [Fact]
    public void No_table_is_reachable_from_the_work_order_by_two_cascade_paths()
    {
        // Walk every cascade edge from PrWorkOrder and assert no table is entered twice. This is the
        // structural condition SQL Server enforces at CREATE TABLE time.
        var cascadeEdges = Model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys().Where(fk => fk.DeleteBehavior == DeleteBehavior.Cascade))
            .ToList();

        var reached = new HashSet<Type> { typeof(ProductionWorkOrder) };
        var frontier = new Queue<Type>();
        frontier.Enqueue(typeof(ProductionWorkOrder));

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var edge in cascadeEdges.Where(fk => fk.PrincipalEntityType.ClrType == current))
            {
                var child = edge.DeclaringEntityType.ClrType;
                Assert.True(reached.Add(child), $"{child.Name} is reachable by more than one cascade path.");
                frontier.Enqueue(child);
            }
        }
    }

    // ── Checks ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Internal_wip_producer_check_is_null_safe_on_material()
    {
        var check = Entity<ProductionWorkOrderMaterial>()
            .GetCheckConstraints()
            .SingleOrDefault(c => c.Name == "CK_PrWorkOrderMaterial_InternalWipProducer");

        Assert.NotNull(check);
        // SupplySource must be NOT NULL so neither comparison can be UNKNOWN, and the non-WIP branch
        // must demand a NULL producer.
        var supplySource = Entity<ProductionWorkOrderMaterial>()
            .FindProperty(nameof(ProductionWorkOrderMaterial.SupplySource));
        Assert.NotNull(supplySource);
        Assert.False(supplySource!.IsNullable);

        Assert.Contains("INTERNAL_ROUTE_WIP", check!.Sql, StringComparison.Ordinal);
        Assert.Contains("IS NOT NULL", check.Sql, StringComparison.Ordinal);
        Assert.Contains("IS NULL", check.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Labour_exclusive_owner_check_exists()
    {
        var names = Entity<ProductionWorkOrderLabour>().GetCheckConstraints().Select(c => c.Name).ToList();
        Assert.Contains("CK_PrWorkOrderLabour_ExclusiveOwner", names);
    }

    [Fact]
    public void Machine_checks_guard_parallel_count_and_output_per_cycle()
    {
        var names = Entity<ProductionWorkOrderMachine>().GetCheckConstraints().Select(c => c.Name).ToList();
        Assert.Contains("CK_PrWorkOrderMachine_Parallel", names);
        Assert.Contains("CK_PrWorkOrderMachine_OutputPerCycle", names);
    }

    // ── Indexes ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stage_sequence_is_not_unique()
    {
        var index = Entity<ProductionWorkOrderRouteStep>()
            .GetIndexes()
            .Single(i => i.GetDatabaseName() == "IX_PrWorkOrderRouteStep_Order_Stage");
        Assert.False(index.IsUnique);
    }

    [Fact]
    public void Process_sequence_is_not_unique()
    {
        var index = Entity<ProductionWorkOrderOperation>()
            .GetIndexes()
            .Single(i => i.GetDatabaseName() == "IX_PrWorkOrderOperation_RouteStep_Process");
        Assert.False(index.IsUnique);
    }

    [Fact]
    public void Source_identity_uniqueness_is_filtered_on_null_keys()
    {
        AssertFilteredUnique<ProductionWorkOrderRouteStep>("UQ_PrWorkOrderRouteStep_Order_SourceKey");
        AssertFilteredUnique<ProductionWorkOrderOperation>("UQ_PrWorkOrderOperation_RouteStep_SourceKey");
        AssertFilteredUnique<ProductionWorkOrderMaterial>("UQ_PrWorkOrderMaterial_Operation_SourceKey");
        AssertFilteredUnique<ProductionWorkOrderMachine>("UQ_PrWorkOrderMachine_Operation_SourceKey");
    }

    [Fact]
    public void Only_one_machine_option_may_be_selected_per_operation()
    {
        var index = Entity<ProductionWorkOrderMachine>()
            .GetIndexes()
            .Single(i => i.GetDatabaseName() == "UX_PrWorkOrderMachine_OneSelected");
        Assert.True(index.IsUnique);
        Assert.NotNull(index.GetFilter());
        Assert.Contains("IsSelected", index.GetFilter()!, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_operation_sequence_uniqueness_only_applies_without_a_route_step()
    {
        var index = Entity<ProductionWorkOrderOperation>()
            .GetIndexes()
            .Single(i => i.GetDatabaseName() == "UQ_PrWorkOrderOperation_Order_Sequence_Legacy");
        Assert.True(index.IsUnique);
        Assert.Contains("RouteStepID", index.GetFilter() ?? string.Empty, StringComparison.Ordinal);
    }

    // ── Schedule timestamps keep time-of-day ──────────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(ProductionWorkOrder), nameof(ProductionWorkOrder.PlannedStartDateTime))]
    [InlineData(typeof(ProductionWorkOrder), nameof(ProductionWorkOrder.PlannedCompletionDateTime))]
    [InlineData(typeof(ProductionWorkOrderRouteStep), nameof(ProductionWorkOrderRouteStep.PlannedStartDateTime))]
    [InlineData(typeof(ProductionWorkOrderOperation), nameof(ProductionWorkOrderOperation.PlannedStartDateTime))]
    [InlineData(typeof(ProductionWorkOrderMachine), nameof(ProductionWorkOrderMachine.PlannedStartDateTime))]
    public void Schedule_timestamps_are_datetime2(Type entityType, string propertyName)
    {
        var property = Model.FindEntityType(entityType)!.FindProperty(propertyName)!;
        Assert.Equal("datetime2", property.GetColumnType());
    }

    [Fact]
    public void Definition_effective_date_maps_to_the_compatibility_column()
    {
        var property = Entity<ProductionWorkOrder>()
            .FindProperty(nameof(ProductionWorkOrder.DefinitionEffectiveDate))!;
        Assert.Equal("SnapshotAsOfDate", property.GetColumnName());
        Assert.Equal("date", property.GetColumnType());
    }

    [Fact]
    public void Snapshot_provenance_columns_are_present()
    {
        var expected = new[]
        {
            nameof(ProductionWorkOrder.SnapshotFormatVersion),
            nameof(ProductionWorkOrder.SnapshotHashVersion),
            nameof(ProductionWorkOrder.IsLegacySnapshot),
            nameof(ProductionWorkOrder.LegacySnapshotReason),
            nameof(ProductionWorkOrder.SourceEffectiveFrom),
            nameof(ProductionWorkOrder.SourceProductDefinitionRevisionId),
            nameof(ProductionWorkOrder.DefinitionSourceHash),
            nameof(ProductionWorkOrder.DefinitionSourceHashVersion),
            nameof(ProductionWorkOrder.ScheduleAnchorDateTime),
            nameof(ProductionWorkOrder.ScheduleCalculationTrace)
        };

        var entity = Entity<ProductionWorkOrder>();
        Assert.All(expected, name => Assert.NotNull(entity.FindProperty(name)));

        // Refresh confirms must compare an exact revision, so the physical ID is stored beside the
        // as-of date it was resolved from.
        Assert.Equal(
            "SourceProductDefinitionRevisionID",
            entity.FindProperty(nameof(ProductionWorkOrder.SourceProductDefinitionRevisionId))!.GetColumnName());
        Assert.Equal(
            "DefinitionSourceHash",
            entity.FindProperty(nameof(ProductionWorkOrder.DefinitionSourceHash))!.GetColumnName());
    }

    // ── Persisted-value contracts (plan §6.9) ─────────────────────────────────────────────────

    [Fact]
    public void Machine_cycle_quantity_mode_and_labour_rate_basis_are_known()
    {
        Assert.True(ProductionMachineCycleQuantityModes.IsKnown("DISCRETE"));
        Assert.False(ProductionMachineCycleQuantityModes.IsKnown("CONTINUOUS"));

        Assert.True(ProductionLabourRateBases.IsKnown("PER_OUTPUT_UNIT"));
        Assert.False(ProductionLabourRateBases.IsKnown("PER_HOUR"));

        Assert.True(ProductionCalendarSourceTypes.IsKnown("MACHINE"));
        Assert.True(ProductionCalendarSourceTypes.IsKnown("PLANT_DEFAULT"));
        Assert.False(ProductionCalendarSourceTypes.IsKnown("SHIFT"));

        Assert.True(ProductionSchedulingDirections.IsKnown("FORWARD"));
        Assert.True(PrProcessTypes.IsValid("MACHINE"));
        Assert.True(PrMaterialIssueMethods.IsValid("BACKFLUSH"));
        Assert.True(PrMaterialSupplySources.IsValid("INTERNAL_ROUTE_WIP"));
    }

    [Fact]
    public void Readiness_error_codes_are_stable_and_unique()
    {
        var codes = typeof(ProductionReadinessErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(codes);
        Assert.All(codes, code => Assert.StartsWith("WO_", code, StringComparison.Ordinal));
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());

        // The two codes the plan names for Milestone 1 must exist verbatim.
        Assert.Contains("WO_OPERATION_UOM_INVALID", codes);
        Assert.Contains("WO_PROCESS_TYPE_INVALID", codes);
    }

    private void AssertFilteredUnique<T>(string databaseName)
    {
        var index = Entity<T>().GetIndexes().Single(i => i.GetDatabaseName() == databaseName);
        Assert.True(index.IsUnique);
        Assert.False(string.IsNullOrWhiteSpace(index.GetFilter()));
    }
}

/// <summary>
/// Live constraint behaviour on a real (SQLite) schema created from the model. Metadata tests prove
/// the constraints are declared; these prove the engine actually rejects the rows the plan forbids.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionWorkOrderSchemaConstraintTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ProductionWorkOrderSchemaConstraintTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Labour_with_two_owners_is_rejected()
    {
        var (operationId, machineId) = await SeedOperationWithMachineAsync();

        await using var db = new AppDbContext(_options);
        db.ProductionWorkOrderLabours.Add(new ProductionWorkOrderLabour
        {
            OperationId = operationId,
            MachineId = machineId,
            LabourCode = "OP01",
            RateBasis = ProductionLabourRateBases.PerOutputUnit,
            Rate = 5m
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Labour_with_no_owner_is_rejected()
    {
        await SeedOperationWithMachineAsync();

        await using var db = new AppDbContext(_options);
        db.ProductionWorkOrderLabours.Add(new ProductionWorkOrderLabour
        {
            LabourCode = "OP01",
            RateBasis = ProductionLabourRateBases.PerOutputUnit,
            Rate = 5m
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Internal_route_wip_material_without_a_producer_is_rejected()
    {
        var (operationId, _) = await SeedOperationWithMachineAsync();

        await using var db = new AppDbContext(_options);
        db.ProductionWorkOrderMaterials.Add(new ProductionWorkOrderMaterial
        {
            WorkOrderId = await WorkOrderIdAsync(),
            WorkOrderOperationId = operationId,
            LineNo = 90,
            MaterialSequence = 1,
            BomPath = "FG001/RM001",
            ComponentCode = "RM001",
            MfgType = PrMfgTypes.Buy,
            IssueMethod = PrMaterialIssueMethods.Manual,
            SupplySource = PrMaterialSupplySources.InternalRouteWip,
            ProducingRouteStepId = null
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Purchased_material_with_a_producer_is_rejected()
    {
        var (operationId, _) = await SeedOperationWithMachineAsync();
        var workOrderId = await WorkOrderIdAsync();

        long routeStepId;
        await using (var db = new AppDbContext(_options))
        {
            routeStepId = (await db.ProductionWorkOrderRouteSteps.SingleAsync(x => x.WorkOrderId == workOrderId)).Uid;
        }

        await using var db2 = new AppDbContext(_options);
        db2.ProductionWorkOrderMaterials.Add(new ProductionWorkOrderMaterial
        {
            WorkOrderId = workOrderId,
            WorkOrderOperationId = operationId,
            LineNo = 91,
            MaterialSequence = 1,
            BomPath = "FG001/RM001",
            ComponentCode = "RM001",
            MfgType = PrMfgTypes.Buy,
            IssueMethod = PrMaterialIssueMethods.Manual,
            SupplySource = PrMaterialSupplySources.Purchased,
            ProducingRouteStepId = routeStepId
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    private async Task<long> WorkOrderIdAsync()
    {
        await using var db = new AppDbContext(_options);
        return (await db.ProductionWorkOrders.SingleAsync()).Uid;
    }

    private async Task<(long OperationId, long MachineId)> SeedOperationWithMachineAsync()
    {
        await using var db = new AppDbContext(_options);

        if (!await db.ProductionWorkOrders.AnyAsync())
        {
            var bomHeader = new PrBomHdr
            {
                CompanyCode = "DEMO",
                ProdCode = "FG001",
                Version = 1,
                Status = PrBomStatuses.Active,
                EffectiveFrom = new DateTime(2026, 1, 1),
                BaseQty = 1m,
                BaseUom = "PCS",
                BranchCode = "HQ",
                LocationCode = "SITE",
                RowVersion = [1]
            };
            db.PrBomHdrs.Add(bomHeader);
            await db.SaveChangesAsync();

            var header = new ProductionWorkOrder
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WorkOrderNo = "WO-0001",
                SnapshotRevision = 1,
                SnapshotHash = new string('a', 64),
                DefinitionEffectiveDate = new DateTime(2026, 1, 1),
                ProductCode = "FG001",
                SourceBomHdrId = bomHeader.Uid,
                SourceBomVersion = 1,
                BomBaseQty = 1m,
                BomBaseUom = "PCS",
                PlannedQty = 10m,
                RemainingQty = 10m,
                PlannedStartDateTime = new DateTime(2026, 2, 1, 8, 0, 0),
                PlannedCompletionDateTime = new DateTime(2026, 2, 1, 16, 0, 0),
                SchedulingDirection = ProductionSchedulingDirections.Forward,
                Status = ProductionWorkOrderStatuses.Draft,
                SourceType = ProductionSourceTypes.Manual,
                SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
                IsLegacySnapshot = false
            };

            var routeStep = new ProductionWorkOrderRouteStep
            {
                StageSequence = 10,
                WorkCentreCode = "WC01",
                OutputItemCode = "FG001",
                OutputBaseQty = 1m,
                OutputUom = "PCS",
                PlannedQty = 10m,
                RowVersion = [1]
            };

            var operation = new ProductionWorkOrderOperation
            {
                WorkCentreCode = "WC01",
                OperationCode = "OP10",
                ProcessType = PrProcessTypes.Machine,
                ProcessSequence = 10,
                IsFinalOperation = true,
                PlannedQty = 10m,
                PlannedOutputQty = 10m,
                PlannedOutputUom = "PCS",
                RowVersion = [1]
            };

            var machine = new ProductionWorkOrderMachine
            {
                MachineCode = "MC01",
                IsDefault = true,
                IsSelected = true,
                CycleQuantityMode = ProductionMachineCycleQuantityModes.Discrete,
                OutputPerCycle = 1m,
                OutputPerCycleUom = "PCS",
                ParallelMachineCount = 1,
                RowVersion = [1]
            };

            operation.Machines.Add(machine);
            routeStep.Operations.Add(operation);
            header.RouteSteps.Add(routeStep);
            db.ProductionWorkOrders.Add(header);
            await db.SaveChangesAsync();
        }

        var operationRow = await db.ProductionWorkOrderOperations
            .Include(x => x.Machines)
            .FirstAsync();
        return (operationRow.Uid, operationRow.Machines.Single().Uid);
    }
}
