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

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class PrProductDefPhase1Tests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PrProductDefPhase1Tests()
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
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "WH01",
            WarehouseDesc = "Main",
            IsActive = true,
            RowVersion = [1]
        });
        db.IvStockMasters.AddRange(
            Stock("FG001", "Finished Cake", "PCS"),
            Stock("RM001", "Flour", "KG"),
            Stock("RM002", "Sugar", "KG"));
        db.PrWorkCentres.Add(new PrWorkCentre { WrkCtrCd = "WC01", WrkCtrDes = "Mixing", CompCode = "DEMO" });
        db.PrProcesses.AddRange(
            new PrProcess { ProcessCd = "MIX", ProcessDes = "Mix", WorkCentre = "WC01", CompCode = "DEMO" },
            new PrProcess { ProcessCd = "WASH", ProcessDes = "Wash", WorkCentre = "WC01", CompCode = "DEMO" });
        db.PrMachines.AddRange(
            new PrMachine { MachineCd = "MX01", MachineDes = "Mixer", ProcessCd = "MIX", CompCode = "DEMO" },
            new PrMachine { MachineCd = "WASH", MachineDes = "Washer", ProcessCd = "WASH", CompCode = "DEMO" });
        db.PrOperators.Add(new PrOperator { Code = "LAB01", Name = "Operator", Active = true, CompanyCode = "DEMO" });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void BranchDefault_census_nulls_are_divergent_and_trim_upper_matches()
    {
        Assert.False(PrProductDefService.IsBackfillEquivalentBranchDefault(null, null, null, null));
        Assert.False(PrProductDefService.IsBackfillEquivalentBranchDefault(null, "WH01", "HQ", "WH01"));
        Assert.False(PrProductDefService.IsBackfillEquivalentBranchDefault("HQ", "WH01", "HQ", null));
        Assert.True(PrProductDefService.IsBackfillEquivalentBranchDefault(" hq ", "wh01", "HQ", "WH01"));
        Assert.False(PrProductDefService.IsBackfillEquivalentBranchDefault("HQ", "WH01", "HQ", "WH02"));
        Assert.Null(PrProductDefService.CensusNormalize(null));
        Assert.Equal("", PrProductDefService.CensusNormalize(""));
        Assert.Equal("HQ", PrProductDefService.CensusNormalize(" hq "));
    }

    [Fact]
    public async Task Update_uses_exact_definition_identity_and_rejects_unknown_code()
    {
        var sut = CreateSut();
        var standard = await sut.SaveAsync(Bom("STANDARD", 0.2m), true, false);
        Assert.True(standard.Succeeded, standard.Message);
        var lineB = await sut.SaveAsync(Bom("LINE_B", 0.5m), true, false);
        Assert.True(lineB.Succeeded, lineB.Message);

        var loadedB = await sut.GetAsync("FG001", "LINE_B");
        loadedB.Data!.Lines[0].StdQty = 0.8m;
        var updateB = await sut.SaveAsync(loadedB.Data, false, false);
        Assert.True(updateB.Succeeded, updateB.Message);

        var stillStandard = await sut.GetAsync("FG001", PrProductDefinitionCodes.Standard);
        Assert.Equal(0.2m, stillStandard.Data!.Lines[0].StdQty);
        Assert.Equal(0.8m, updateB.Data!.Lines[0].StdQty);

        loadedB.Data.DefinitionCode = "LINE_C";
        var missing = await sut.SaveAsync(loadedB.Data, false, false);
        Assert.False(missing.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, missing.ErrorCode);
    }

    [Fact]
    public async Task Save_rejects_raw_definition_code_longer_than_30()
    {
        var sut = CreateSut();
        var result = await sut.SaveAsync(Bom(new string('A', 31), 0.2m), true, false);
        Assert.False(result.Succeeded);
        Assert.Contains("DefinitionCode", result.ValidationErrors.Keys);
    }

    [Fact]
    public async Task ActivateAsync_rejects_missing_rowversion_token()
    {
        var sut = CreateSut();
        var draft = await sut.SaveAsync(Bom("STANDARD", 0.2m), true, false);
        Assert.True(draft.Succeeded, draft.Message);

        var missing = await sut.ActivateAsync("FG001", PrProductDefinitionCodes.Standard, draft.Data!.Version, null);
        Assert.False(missing.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, missing.ErrorCode);

        var empty = await sut.ActivateAsync("FG001", PrProductDefinitionCodes.Standard, draft.Data.Version, []);
        Assert.False(empty.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, empty.ErrorCode);
    }

    [Fact]
    public async Task Bom_only_activate_remains_permitted()
    {
        var sut = CreateSut();
        var result = await sut.SaveAsync(Bom("STANDARD", 0.2m), true, true);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(PrBomStatuses.Active, result.Data!.Status);
        Assert.Empty(result.Data.Operations);
    }

    [Fact]
    public async Task Draft_save_still_reclassifies_buy_item_to_make()
    {
        var sut = CreateSut();
        var result = await sut.SaveAsync(Bom("STANDARD", 0.2m), true, false);
        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var fg = await db.IvStockMasters.SingleAsync(x => x.CompanyCode == "DEMO" && x.ICode == "FG001");
        Assert.Equal(PrMfgTypes.Make, fg.MfgType);
    }

    [Fact]
    public async Task Saved_dto_round_trips_header_line_and_route_fields()
    {
        var sut = CreateSut();
        var operationKey = Guid.NewGuid();
        var save = await sut.SaveAsync(Routed(operationKey), true, false);
        Assert.True(save.Succeeded, save.Message);
        var loaded = await sut.GetAsync("FG001", PrProductDefinitionCodes.Standard, save.Data!.Version);
        Assert.True(loaded.Succeeded, loaded.Message);
        AssertDto(save.Data, loaded.Data!);

        var again = await sut.SaveAsync(loaded.Data!, false, false);
        Assert.True(again.Succeeded, again.Message);
        AssertDto(loaded.Data!, again.Data!);
    }

    [Fact]
    public async Task Clone_refuses_labour_requirement_and_leaves_no_new_version()
    {
        var sut = CreateSut();
        var save = await sut.SaveAsync(Routed(Guid.NewGuid()), true, false);
        Assert.True(save.Succeeded, save.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var operationId = await db.PrBomOperations
                .Where(x => x.BomHdrId == save.Data!.BomHdrId)
                .Select(x => x.Uid)
                .SingleAsync();
            db.PrBomLabourRequirements.Add(new PrBomLabourRequirement
            {
                OperationId = operationId,
                LabourCode = "LAB01",
                RequiredHeadcount = 1m,
                CostBasis = PrLabourCostBases.PerHour,
                RowVersion = [1]
            });
            await db.SaveChangesAsync();
        }

        var clone = await sut.CreateNewVersionAsync("FG001", PrProductDefinitionCodes.Standard);
        Assert.False(clone.Succeeded);
        Assert.Contains("labour", clone.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await check.PrBomHdrs.CountAsync(x => x.ProdCode == "FG001"));
    }

    [Fact]
    public async Task Equivalent_branch_default_allows_save_divergent_refuses_clone()
    {
        var sut = CreateSut();
        var save = await sut.SaveAsync(Bom("STANDARD", 0.2m), true, false);
        Assert.True(save.Succeeded, save.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var line = await db.PrDefBOMs.SingleAsync(x => x.BomHdrId == save.Data!.BomHdrId);
            db.PrBomMaterialBranchDefaults.Add(new PrBomMaterialBranchDefault
            {
                MaterialId = line.Uid,
                BranchCode = line.BranchCode ?? "HQ",
                WarehouseCode = line.Warehouse ?? "WH01",
                RowVersion = [1]
            });
            await db.SaveChangesAsync();
        }

        var loaded = await sut.GetAsync("FG001", PrProductDefinitionCodes.Standard);
        var allowed = await sut.SaveAsync(loaded.Data!, false, false);
        Assert.True(allowed.Succeeded, allowed.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var line = await db.PrDefBOMs.SingleAsync(x => x.BomHdrId == allowed.Data!.BomHdrId);
            db.PrBomMaterialBranchDefaults.Add(new PrBomMaterialBranchDefault
            {
                MaterialId = line.Uid,
                BranchCode = line.BranchCode ?? "HQ",
                WarehouseCode = "WH99",
                RowVersion = [1]
            });
            await db.SaveChangesAsync();
        }

        var clone = await sut.CreateNewVersionAsync("FG001", PrProductDefinitionCodes.Standard);
        Assert.False(clone.Succeeded);
        Assert.Contains("branch warehouse default", clone.Message, StringComparison.OrdinalIgnoreCase);
        await using var check = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await check.PrBomHdrs.CountAsync(x => x.ProdCode == "FG001"));
    }

    [Fact]
    public async Task Purchased_line_does_not_false_positive_producer_classifier()
    {
        var sut = CreateSut();
        var save = await sut.SaveAsync(Routed(Guid.NewGuid()), true, false);
        Assert.True(save.Succeeded, save.Message);
        var clone = await sut.CreateNewVersionAsync("FG001", PrProductDefinitionCodes.Standard, save.Data!.Version);
        Assert.True(clone.Succeeded, clone.Message);
        Assert.Equal(2, clone.Data!.Version);
        Assert.Equal(PrMaterialSupplySources.Purchased, clone.Data.Lines[0].SupplySource);
        Assert.Null(clone.Data.Lines[0].ProducingRouteStepKey);
    }

    [Fact]
    public async Task Internal_wip_producer_key_survives_clone()
    {
        var sut = CreateSut();
        var mixKey = Guid.NewGuid();
        var washKey = Guid.NewGuid();
        var save = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            BaseQty = 5m,
            BaseUom = "PCS",
            Lines =
            [
                new PrProductDefLineVm
                {
                    OperationKey = washKey, ICode = "RM001", StdQty = 1m, Warehouse = "WH01"
                }
            ],
            Operations =
            [
                new PrProductDefOperationVm
                {
                    OperationKey = mixKey, WorkCentreCode = "WC01", OutputItemCode = "RM001",
                    CentralSequence = 10, OutputBaseQty = 1m, OutputUom = "KG", OperationCode = "MIX",
                    ProcessSequence = 10, ProcessType = PrProcessTypes.Machine, IsFinalOperation = true,
                    Machines =
                    [
                        new PrProductDefMachineVm
                        {
                            MachineCode = "MX01", ResourceSequence = 10, IsPrimary = true,
                            CycleSeconds = 30m, OutputPerCycle = 1m,
                            Labours = [new PrProductDefLabourVm { LabourCode = "LAB01", CostPerOutputUnit = 1m }]
                        }
                    ]
                },
                new PrProductDefOperationVm
                {
                    OperationKey = washKey, WorkCentreCode = "WC01", OutputItemCode = "FG001",
                    CentralSequence = 20, OutputBaseQty = 5m, OutputUom = "PCS", OperationCode = "WASH",
                    ProcessSequence = 20, ProcessType = PrProcessTypes.Machine, IsFinalOperation = true,
                    Machines =
                    [
                        new PrProductDefMachineVm
                        {
                            MachineCode = "WASH", ResourceSequence = 10, IsPrimary = true,
                            CycleSeconds = 30m, OutputPerCycle = 5m,
                            Labours = [new PrProductDefLabourVm { LabourCode = "LAB01", CostPerOutputUnit = 1m }]
                        }
                    ]
                }
            ]
        }, true, false);
        Assert.True(save.Succeeded, save.Message);

        Guid producerKey;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            producerKey = await db.PrBomRouteSteps
                .Where(x => x.BomHdrId == save.Data!.BomHdrId && x.OutputItemCode == "RM001")
                .Select(x => x.RouteStepKey)
                .SingleAsync();
        }

        var loaded = await sut.GetAsync("FG001", PrProductDefinitionCodes.Standard);
        loaded.Data!.Lines[0].SupplySource = PrMaterialSupplySources.InternalRouteWip;
        loaded.Data.Lines[0].ProducingRouteStepKey = producerKey;
        var updated = await sut.SaveAsync(loaded.Data, false, false);
        Assert.True(updated.Succeeded, updated.Message);

        var clone = await sut.CreateNewVersionAsync("FG001", PrProductDefinitionCodes.Standard);
        Assert.True(clone.Succeeded, clone.Message);
        Assert.Equal(producerKey, Assert.Single(clone.Data!.Lines).ProducingRouteStepKey);
        Assert.Equal(PrMaterialSupplySources.InternalRouteWip, clone.Data.Lines[0].SupplySource);
    }

    private static void AssertDto(PrProductDefEditVm expected, PrProductDefEditVm actual)
    {
        Assert.Equal(expected.ProdCode, actual.ProdCode);
        Assert.Equal(expected.DefinitionCode, actual.DefinitionCode);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.BaseQty, actual.BaseQty);
        Assert.Equal(expected.BaseUom, actual.BaseUom);
        Assert.Equal(expected.Lines.Count, actual.Lines.Count);
        Assert.Equal(expected.Operations.Count, actual.Operations.Count);
        Assert.Equal(expected.Lines[0].ICode, actual.Lines[0].ICode);
        Assert.Equal(expected.Lines[0].StdQty, actual.Lines[0].StdQty);
        Assert.Equal(expected.Lines[0].Warehouse, actual.Lines[0].Warehouse);
        Assert.Equal(expected.Lines[0].SupplySource, actual.Lines[0].SupplySource);
        Assert.Equal(expected.Lines[0].IssueMethod, actual.Lines[0].IssueMethod);
        Assert.Equal(expected.Operations[0].WorkCentreCode, actual.Operations[0].WorkCentreCode);
        Assert.Equal(expected.Operations[0].OutputItemCode, actual.Operations[0].OutputItemCode);
        Assert.Equal(expected.Operations[0].OperationKey, actual.Operations[0].OperationKey);
        Assert.Equal(expected.Operations[0].IsFinalOperation, actual.Operations[0].IsFinalOperation);
        Assert.Equal(expected.Lines[0].OperationKey, actual.Lines[0].OperationKey);
    }

    private static PrProductDefEditVm Bom(string definition, decimal qty) =>
        new()
        {
            ProdCode = "FG001",
            DefinitionCode = definition,
            BaseQty = 1m,
            Lines =
            [
                new PrProductDefLineVm { ICode = "RM001", StdQty = qty, Warehouse = "WH01", SeqNo = 1 }
            ]
        };

    private static PrProductDefEditVm Routed(Guid operationKey) =>
        new()
        {
            ProdCode = "FG001",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            BaseQty = 5m,
            BaseUom = "PCS",
            Lines =
            [
                new PrProductDefLineVm
                {
                    OperationKey = operationKey, ICode = "RM001", StdQty = 1m, Warehouse = "WH01"
                }
            ],
            Operations = [Op(operationKey, "FG001", 10, "MIX", final: true, machine: "MX01")]
        };

    private static PrProductDefOperationVm Op(
        Guid key,
        string output,
        int seq,
        string process,
        bool final,
        string machine) =>
        new()
        {
            OperationKey = key,
            WorkCentreCode = "WC01",
            OutputItemCode = output,
            CentralSequence = seq,
            OutputBaseQty = 5m,
            OutputUom = "PCS",
            OperationCode = process,
            ProcessSequence = seq,
            ProcessType = PrProcessTypes.Machine,
            IsFinalOperation = final,
            Machines =
            [
                new PrProductDefMachineVm
                {
                    MachineCode = machine,
                    ResourceSequence = 10,
                    IsPrimary = true,
                    CycleSeconds = 30m,
                    OutputPerCycle = 5m,
                    Labours = [new PrProductDefLabourVm { LabourCode = "LAB01", CostPerOutputUnit = 1m }]
                }
            ]
        };

    private static IvStockMaster Stock(string code, string desc, string uom) =>
        new()
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
        foreach (var permission in new[]
                 {
                     PermissionCodes.Access, PermissionCodes.Add, PermissionCodes.Edit, PermissionCodes.Delete
                 })
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
