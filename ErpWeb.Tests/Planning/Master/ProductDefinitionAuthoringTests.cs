using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Planning.Master;
/// <summary>
/// Milestone 0 of the Production Work Order Enhancement Plan: Product Definition must author every
/// field the Work Order snapshot needs (route steps, process type, output per cycle, supply source,
/// in-house producer, UOM conversion). See <c>plans/work-order-plan.md</c> §5.6.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductDefinitionAuthoringTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProductDefinitionAuthoringTests()
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

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "WH01",
            WarehouseDesc = "Main", IsActive = true, RowVersion = [1]
        });
        db.IvStockMasters.AddRange(
            Stock("FG001", "Finished", "PCS"),
            Stock("RM001", "Flour", "KG"));
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Route_step_is_persisted_and_links_operations_and_materials()
    {
        await SeedMastersAsync();
        var sut = CreateSut();
        var operationKey = Guid.NewGuid();

        var save = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            BaseQty = 5m,
            BaseUom = "PCS",
            Lines = [new PrProductDefLineVm { OperationKey = operationKey, ICode = "RM001", StdQty = 1m, Warehouse = "WH01" }],
            Operations =
            [
                new PrProductDefOperationVm
                {
                    OperationKey = operationKey,
                    WorkCentreCode = "WC01",
                    OutputItemCode = "FG001",
                    CentralSequence = 10,
                    OutputBaseQty = 5m,
                    OutputUom = "PCS",
                    OperationCode = "MIX",
                    ProcessSequence = 10,
                    ProcessType = PrProcessTypes.Machine,
                    IsFinalOperation = true,
                    Machines =
                    [
                        new PrProductDefMachineVm
                        {
                            MachineCode = "MX01", ResourceSequence = 10, IsPrimary = true, Priority = 1,
                            OutputPerCycle = 5m, CycleSeconds = 30m,
                            Labours = [new PrProductDefLabourVm { LabourCode = "LAB01", CostPerOutputUnit = 1m }]
                        }
                    ]
                }
            ]
        }, isNew: true, activate: false);

        Assert.True(save.Succeeded, save.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var step = await db.PrBomRouteSteps.SingleAsync(x => x.CompanyCode == "DEMO");
        Assert.Equal("WC01", step.WorkCentreCode);
        Assert.Equal("FG001", step.OutputItemCode);
        Assert.Equal(PrRouteOutputTypes.FinishedGoods, step.OutputType);
        Assert.Equal(10, step.StageSequence);
        Assert.Equal(5m, step.StandardOutputQty);
        Assert.Equal("PCS", step.OutputUom);
        Assert.Equal(100m, step.YieldPercent);

        var operation = await db.PrBomOperations.SingleAsync(x => x.CompanyCode == "DEMO");
        Assert.Equal(step.Uid, operation.RouteStepId);
        Assert.Equal(PrProcessTypes.Machine, operation.ProcessType);

        var machine = await db.PrBomMachineOptions.SingleAsync();
        Assert.Equal(5m, machine.OutputPerCycle);
        Assert.Equal(1, machine.Priority);

        var material = await db.PrDefBOMs.SingleAsync(x => x.CompanyCode == "DEMO");
        Assert.Equal(operation.Uid, material.OperationId);
        Assert.Null(material.ProducingRouteStepId);
        Assert.Equal(PrMaterialIssueMethods.Manual, material.IssueMethod);
        Assert.Equal(PrMaterialSupplySources.Purchased, material.SupplySource);
    }

    [Fact]
    public async Task Duration_based_step_requires_standard_duration_and_rejects_a_machine()
    {
        await SeedMastersAsync();
        var sut = CreateSut();
        var operationKey = Guid.NewGuid();

        PrProductDefEditVm Build(decimal duration, bool withMachine) => new()
        {
            ProdCode = "FG001",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            BaseQty = 5m,
            BaseUom = "PCS",
            Lines = [new PrProductDefLineVm { OperationKey = operationKey, ICode = "RM001", StdQty = 1m, Warehouse = "WH01" }],
            Operations =
            [
                new PrProductDefOperationVm
                {
                    OperationKey = operationKey,
                    WorkCentreCode = "WC01",
                    OutputItemCode = "FG001",
                    CentralSequence = 10,
                    OutputBaseQty = 5m,
                    OutputUom = "PCS",
                    OperationCode = "MIX",
                    ProcessSequence = 10,
                    ProcessType = PrProcessTypes.Manual,
                    StandardDurationMinutes = duration,
                    IsFinalOperation = true,
                    Machines = withMachine
                        ? [new PrProductDefMachineVm { MachineCode = "MX01", ResourceSequence = 10 }]
                        : []
                }
            ]
        };

        var zero = await sut.SaveAsync(Build(0m, false), isNew: true, activate: true);
        Assert.False(zero.Succeeded);
        Assert.Contains("Operations[0].StandardDurationMinutes", zero.ValidationErrors.Keys);

        var withMachine = await sut.SaveAsync(Build(12m, true), isNew: true, activate: false);
        Assert.False(withMachine.Succeeded);
        Assert.Contains("Operations[0].Machines", withMachine.ValidationErrors.Keys);

        var ok = await sut.SaveAsync(Build(12m, false), isNew: true, activate: false);
        Assert.True(ok.Succeeded, ok.Message);
        Assert.Equal(PrProcessTypes.Manual, ok.Data!.Operations[0].ProcessType);
        Assert.Equal(12m, ok.Data.Operations[0].StandardDurationMinutes);
    }

    [Fact]
    public async Task Internal_wip_producer_is_validated_and_round_trips()
    {
        await SeedMastersAsync();
        var sut = CreateSut();
        var mixKey = Guid.NewGuid();
        var washKey = Guid.NewGuid();

        var draft = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            BaseQty = 5m,
            BaseUom = "PCS",
            Lines = [new PrProductDefLineVm { OperationKey = washKey, ICode = "RM001", StdQty = 1m, Warehouse = "WH01" }],
            Operations =
            [
                new PrProductDefOperationVm
                {
                    OperationKey = mixKey, WorkCentreCode = "WC01", OutputItemCode = "RM001",
                    CentralSequence = 10, OutputBaseQty = 1m, OutputUom = "KG", OperationCode = "MIX",
                    ProcessSequence = 10, IsFinalOperation = true,
                    Machines = [new PrProductDefMachineVm { MachineCode = "MX01", ResourceSequence = 10 }]
                },
                new PrProductDefOperationVm
                {
                    OperationKey = washKey, WorkCentreCode = "WC01", OutputItemCode = "FG001",
                    CentralSequence = 20, OutputBaseQty = 5m, OutputUom = "PCS", OperationCode = "WASH",
                    ProcessSequence = 20, IsFinalOperation = true,
                    Machines = [new PrProductDefMachineVm { MachineCode = "WASH", ResourceSequence = 10 }]
                }
            ]
        }, isNew: true, activate: false);

        Assert.True(draft.Succeeded, draft.Message);

        Guid producerKey;
        Guid finishedKey;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            producerKey = await db.PrBomRouteSteps
                .Where(x => x.CompanyCode == "DEMO" && x.OutputItemCode == "RM001")
                .Select(x => x.RouteStepKey).SingleAsync();
            finishedKey = await db.PrBomRouteSteps
                .Where(x => x.CompanyCode == "DEMO" && x.OutputItemCode == "FG001")
                .Select(x => x.RouteStepKey).SingleAsync();
        }

        var loaded = await sut.GetAsync("FG001", PrProductDefinitionCodes.Standard);
        loaded.Data!.Lines[0].SupplySource = PrMaterialSupplySources.InternalRouteWip;
        loaded.Data.Lines[0].ProducingRouteStepKey = producerKey;
        loaded.Data.Lines[0].IssueMethod = PrMaterialIssueMethods.Backflush;

        var saved = await sut.SaveAsync(loaded.Data, isNew: false, activate: false);
        Assert.True(saved.Succeeded, saved.Message);
        Assert.Equal(PrMaterialSupplySources.InternalRouteWip, saved.Data!.Lines[0].SupplySource);
        Assert.Equal(PrMaterialIssueMethods.Backflush, saved.Data.Lines[0].IssueMethod);
        Assert.Equal(producerKey, saved.Data.Lines[0].ProducingRouteStepKey);

        var reloaded = await sut.GetAsync("FG001", PrProductDefinitionCodes.Standard);
        reloaded.Data!.Lines[0].ProducingRouteStepKey = finishedKey;
        var mismatched = await sut.SaveAsync(reloaded.Data, isNew: false, activate: false);
        Assert.False(mismatched.Succeeded);
        Assert.Contains("Lines[0].ProducingRouteStepKey", mismatched.ValidationErrors.Keys);

        reloaded.Data.Lines[0].SupplySource = PrMaterialSupplySources.Purchased;
        reloaded.Data.Lines[0].ProducingRouteStepKey = producerKey;
        var wrongSource = await sut.SaveAsync(reloaded.Data, isNew: false, activate: false);
        Assert.False(wrongSource.Succeeded);
        Assert.Contains("Lines[0].ProducingRouteStepKey", wrongSource.ValidationErrors.Keys);
    }

    [Fact]
    public async Task Activation_blocks_internal_wip_material_without_a_producer()
    {
        await SeedMastersAsync();
        var sut = CreateSut();
        var operationKey = Guid.NewGuid();

        var draft = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            BaseQty = 5m,
            BaseUom = "PCS",
            Lines = [new PrProductDefLineVm { OperationKey = operationKey, ICode = "RM001", StdQty = 1m, Warehouse = "WH01" }],
            Operations =
            [
                new PrProductDefOperationVm
                {
                    OperationKey = operationKey, WorkCentreCode = "WC01", OutputItemCode = "FG001",
                    CentralSequence = 10, OutputBaseQty = 5m, OutputUom = "PCS", OperationCode = "MIX",
                    ProcessSequence = 10, IsFinalOperation = true,
                    Machines = [new PrProductDefMachineVm { MachineCode = "MX01", ResourceSequence = 10 }]
                }
            ]
        }, isNew: true, activate: false);
        Assert.True(draft.Succeeded, draft.Message);

        var loaded = await sut.GetAsync("FG001", PrProductDefinitionCodes.Standard);
        loaded.Data!.Lines[0].SupplySource = PrMaterialSupplySources.InternalRouteWip;
        loaded.Data.Lines[0].ProducingRouteStepKey = null;

        var activate = await sut.SaveAsync(loaded.Data, isNew: false, activate: true);
        Assert.False(activate.Succeeded);
        Assert.Contains("Lines[0].ProducingRouteStepKey", activate.ValidationErrors.Keys);
    }

    [Fact]
    public async Task Uom_conversion_resolves_forward_reverse_and_missing()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvItemUomConversions.Add(new IvItemUomConversion
            {
                CompanyCode = "DEMO",
                ItemCode = "RM001",
                FromUom = "KG",
                ToUom = "G",
                FromQty = 1m,
                ToQty = 1000m,
                RoundingScale = 4,
                RoundingMode = IvUomRoundingModes.AwayFromZero,
                IsActive = true,
                RowVersion = [1]
            });
            await db.SaveChangesAsync();
        }

        var sut = new IvUomConversionService(_factory);

        var identity = await sut.ConvertAsync("DEMO", "RM001", 2.5m, "KG", "KG");
        Assert.True(identity.Succeeded);
        Assert.Equal(2.5m, identity.Quantity);

        var forward = await sut.ConvertAsync("DEMO", "RM001", 0.5m, "KG", "G");
        Assert.True(forward.Succeeded);
        Assert.Equal(500m, forward.Quantity);

        var reverse = await sut.ConvertAsync("DEMO", "RM001", 250m, "G", "KG");
        Assert.True(reverse.Succeeded);
        Assert.Equal(0.25m, reverse.Quantity);

        var missing = await sut.ConvertAsync("DEMO", "RM001", 1m, "KG", "PCS");
        Assert.False(missing.Succeeded);
        Assert.Equal(UomConversionFailureCodes.MissingConversion, missing.FailureCode);
        Assert.Contains("KG", missing.FailureMessage!, StringComparison.Ordinal);

        Assert.True(await sut.HasConversionAsync("DEMO", "RM001", "G", "KG"));
        Assert.False(await sut.HasConversionAsync("DEMO", "RM002", "KG", "G"));
    }

    [Fact]
    public void Material_producer_reference_is_no_action_and_indexed()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=ErpWebModelOnly;Trusted_Connection=True")
            .Options;
        using var db = new AppDbContext(options);

        var entity = db.Model.FindEntityType(typeof(PrDefBOM));
        Assert.NotNull(entity);
        var fk = Assert.Single(entity!.GetForeignKeys(), x =>
            x.Properties.Single().Name == nameof(PrDefBOM.ProducingRouteStepId));

        Assert.False(fk.IsRequired);
        Assert.Equal(DeleteBehavior.NoAction, fk.DeleteBehavior);
        Assert.Contains(entity.GetIndexes(), x =>
            x.GetDatabaseName() == "IX_PrDefBOM_ProducingRouteStepID");
    }

    [Fact]
    public async Task Save_blocks_material_uom_without_an_approved_conversion()
    {
        await SeedMastersAsync();
        var sut = CreateSut();
        var operationKey = Guid.NewGuid();
        PrProductDefEditVm Request(string uom) => new()
        {
            ProdCode = "FG001",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            BaseQty = 5m,
            BaseUom = "PCS",
            Lines =
            [
                new PrProductDefLineVm
                {
                    OperationKey = operationKey,
                    ICode = "RM001",
                    StdQty = 1m,
                    StdUom = uom,
                    Warehouse = "WH01"
                }
            ],
            Operations =
            [
                new PrProductDefOperationVm
                {
                    OperationKey = operationKey,
                    WorkCentreCode = "WC01",
                    OutputItemCode = "FG001",
                    CentralSequence = 10,
                    OutputBaseQty = 5m,
                    OutputUom = "PCS",
                    OperationCode = "MIX",
                    ProcessSequence = 10,
                    ProcessType = PrProcessTypes.Machine,
                    IsFinalOperation = true,
                    Machines =
                    [
                        new PrProductDefMachineVm
                        {
                            MachineCode = "MX01", ResourceSequence = 10, IsPrimary = true, Priority = 1,
                            OutputPerCycle = 5m, CycleSeconds = 30m,
                            Labours = [new PrProductDefLabourVm { LabourCode = "LAB01", CostPerOutputUnit = 1m }]
                        }
                    ]
                }
            ]
        };

        var rejected = await sut.SaveAsync(Request("BOX"), isNew: true, activate: false);
        Assert.False(rejected.Succeeded);
        Assert.Contains("Lines[0].StdUom", rejected.ValidationErrors.Keys);
        Assert.Contains(UomConversionFailureCodes.MissingConversion, rejected.ValidationErrors["Lines[0].StdUom"]);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvItemUomConversions.Add(new IvItemUomConversion
            {
                CompanyCode = "DEMO",
                ItemCode = "RM001",
                FromUom = "BOX",
                ToUom = "KG",
                FromQty = 1m,
                ToQty = 25m,
                IsActive = true,
                RoundingMode = IvUomRoundingModes.AwayFromZero,
                RowVersion = [1]
            });
            await db.SaveChangesAsync();
        }

        var accepted = await sut.SaveAsync(Request("BOX"), isNew: true, activate: false);
        Assert.True(accepted.Succeeded, accepted.Message);
        Assert.Equal("BOX", accepted.Data!.Lines[0].StdUom);
    }

    private async Task SeedMastersAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.PrWorkCentres.Add(new PrWorkCentre { WrkCtrCd = "WC01", WrkCtrDes = "Mixing", CompCode = "DEMO" });
        db.PrProcesses.AddRange(
            new PrProcess { ProcessCd = "MIX", ProcessDes = "Mix", WorkCentre = "WC01", CompCode = "DEMO" },
            new PrProcess { ProcessCd = "WASH", ProcessDes = "Wash", WorkCentre = "WC01", CompCode = "DEMO" });
        db.PrMachines.AddRange(
            new PrMachine { MachineCd = "MX01", MachineDes = "Mixer 1", ProcessCd = "MIX", CompCode = "DEMO" },
            new PrMachine { MachineCd = "WASH", MachineDes = "Washer", ProcessCd = "WASH", CompCode = "DEMO" });
        db.PrOperators.Add(new PrOperator { Code = "LAB01", Name = "Operator", Active = true, CompanyCode = "DEMO" });
        await db.SaveChangesAsync();
    }

    private static IvStockMaster Stock(string code, string desc, string uom) => new()
    {
        CompanyCode = "DEMO",
        ICode = code,
        IDesc = desc,
        StdUom = uom,
        DefWarehouse = "WH01",
        MfgType = PrMfgTypes.Buy,
        IsActive = true,
        RowVersion = [1]
    };

    private PrProductDefService CreateSut()
    {
        var access = new Mock<IAccessRightService>();
        foreach (var permission in new[] { PermissionCodes.Access, PermissionCodes.Add, PermissionCodes.Edit, PermissionCodes.Delete })
        {
            access.Setup(x => x.CanAsync(MenuCodes.PlanningProductDef, permission, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        }

        return new PrProductDefService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            access.Object,
            new IvUomConversionService(_factory));
    }
}
