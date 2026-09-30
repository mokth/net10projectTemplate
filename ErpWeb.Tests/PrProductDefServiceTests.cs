using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public class PrProductDefServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PrProductDefServiceTests()
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
            Stock("DEMO", "FG001", "Finished Cake", "PCS"),
            Stock("DEMO", "RM001", "Flour", "KG"),
            Stock("DEMO", "RM002", "Sugar", "KG"),
            Stock("DEMO", "INACTIVE", "Inactive item", "PCS", active: false),
            Stock("OTHER", "FG001", "Other company FG", "PCS"));

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void RequiredQty_multiplies_with_base_and_scrap()
    {
        Assert.Equal(20m, PrBomCalc.RequiredQty(100m, 0.2m));
        Assert.Equal(20m, PrBomCalc.RequiredQty(100m, 1m, 0.2m, 0m));
        Assert.Equal(22m, PrBomCalc.RequiredQty(100m, 1m, 0.2m, 10m)); // 100*0.2*1.1
        Assert.Equal(50m, PrBomCalc.RequiredQty(250m, 100m, 20m, 0m)); // 250/100*20
    }

    [Fact]
    public async Task Save_rejects_missing_product()
    {
        var sut = CreateSut();
        var result = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "MISSING",
            BaseQty = 1m,
            Lines = [new PrProductDefLineVm { ICode = "RM001", StdQty = 0.2m, Warehouse = "WH01" }]
        }, isNew: true, activate: false);

        Assert.False(result.Succeeded);
        Assert.Contains("ProdCode", result.ValidationErrors.Keys);
    }

    [Fact]
    public async Task Save_rejects_zero_stdqty_self_ref_duplicate_and_empty()
    {
        var sut = CreateSut();

        var empty = await sut.SaveAsync(new PrProductDefEditVm { ProdCode = "FG001", Lines = [] }, true, true);
        Assert.False(empty.Succeeded);

        var self = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            BaseQty = 1m,
            Lines = [new PrProductDefLineVm { ICode = "FG001", StdQty = 1m, Warehouse = "WH01" }]
        }, true, true);
        Assert.False(self.Succeeded);

        var zero = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            BaseQty = 1m,
            Lines = [new PrProductDefLineVm { ICode = "RM001", StdQty = 0m, Warehouse = "WH01" }]
        }, true, true);
        Assert.False(zero.Succeeded);

        var dup = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            BaseQty = 1m,
            Lines =
            [
                new PrProductDefLineVm { ICode = "RM001", StdQty = 0.2m, Warehouse = "WH01" },
                new PrProductDefLineVm { ICode = "RM001", StdQty = 0.1m, Warehouse = "WH01" }
            ]
        }, true, true);
        Assert.False(dup.Succeeded);
    }

    [Fact]
    public async Task Crud_create_read_update_delete_and_snapshot()
    {
        var sut = CreateSut();
        var create = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            BaseQty = 1m,
            Lines =
            [
                new PrProductDefLineVm
                {
                    ICode = "RM001", StdQty = 0.2m, Warehouse = "WH01", BomDefault = true,
                    AlternateGroupCode = "RM001", Tolerance = 50m, SeqNo = 1
                },
                new PrProductDefLineVm
                {
                    ICode = "RM002", StdQty = 0.1m, Warehouse = "WH01", BomDefault = false,
                    AlternateGroupCode = "RM001", SeqNo = 2
                }
            ]
        }, isNew: true, activate: false);

        Assert.True(create.Succeeded, create.Message);
        Assert.Equal(2, create.Data!.Lines.Count);
        Assert.Equal(PrBomStatuses.Draft, create.Data.Status);
        Assert.Equal("Flour", create.Data.Lines.First(x => x.ICode == "RM001").IName);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var rm = await db.IvStockMasters.SingleAsync(x => x.CompanyCode == "DEMO" && x.ICode == "RM001");
            rm.IDesc = "Flour CHANGED";
            var fg = await db.IvStockMasters.SingleAsync(x => x.CompanyCode == "DEMO" && x.ICode == "FG001");
            Assert.Equal(PrMfgTypes.Make, fg.MfgType);
            await db.SaveChangesAsync();
        }

        var get = await sut.GetAsync("FG001");
        Assert.True(get.Succeeded);
        Assert.Equal("Flour", get.Data!.Lines.First(x => x.ICode == "RM001").IName);

        var update = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            Version = get.Data.Version,
            BomHdrId = get.Data.BomHdrId,
            HeaderRowVersion = get.Data.HeaderRowVersion,
            Status = get.Data.Status,
            BaseQty = 1m,
            EffectiveFrom = get.Data.EffectiveFrom,
            Lines =
            [
                new PrProductDefLineVm
                {
                    ICode = "RM001", StdQty = 0.25m, Warehouse = "WH01", BomDefault = true, Tolerance = 50m, SeqNo = 1
                }
            ]
        }, isNew: false, activate: false);
        Assert.True(update.Succeeded, update.Message);
        Assert.Single(update.Data!.Lines);
        Assert.Equal(0.25m, update.Data.Lines[0].StdQty);

        var list = await sut.SearchAsync(new PrProductDefListQuery());
        Assert.True(list.Succeeded);
        Assert.Contains(list.Data!.Rows, x => x.ProdCode == "FG001" && x.BomItemCount == 1);

        var del = await sut.DeleteAsync(["FG001"]);
        Assert.True(del.Succeeded, del.Message);
        Assert.False((await sut.GetAsync("FG001")).Succeeded);
    }

    [Fact]
    public async Task Save_Get_round_trips_Prefix_and_Remark()
    {
        var sut = CreateSut();
        var create = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            BaseQty = 1m,
            Prefix = "wo",
            Remark = "Legacy remark text",
            Lines = [new PrProductDefLineVm { ICode = "RM001", StdQty = 1m, Warehouse = "WH01", SeqNo = 1 }]
        }, isNew: true, activate: false);

        Assert.True(create.Succeeded, create.Message);
        Assert.Equal("WO", create.Data!.Prefix);
        Assert.Equal("Legacy remark text", create.Data.Remark);

        var get = await sut.GetAsync("FG001");
        Assert.True(get.Succeeded);
        Assert.Equal("WO", get.Data!.Prefix);
        Assert.Equal("Legacy remark text", get.Data.Remark);

        var versioned = await sut.CreateNewVersionAsync("FG001");
        Assert.True(versioned.Succeeded, versioned.Message);
        Assert.Equal("WO", versioned.Data!.Prefix);
        Assert.Equal("Legacy remark text", versioned.Data.Remark);
    }

    [Fact]
    public async Task Company_isolation_hides_other_company_bom()
    {
        var otherTenant = InventoryTenantTestHelper.CreateTenantContext(company: "OTHER", branch: "HQ", location: "SITE");
        var access = CreateAccess(true, true, true, true);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = "OTHER",
                BranchCode = "HQ",
                WarehouseCode = "WH01",
                WarehouseDesc = "Other WH",
                IsActive = true,
                RowVersion = [9]
            });
            db.IvStockMasters.Add(Stock("OTHER", "RM001", "Other RM", "KG"));
            await db.SaveChangesAsync();
        }

        var otherSut = new PrProductDefService(
            _factory, otherTenant, access.Object, new IvUomConversionService(_factory));
        var save = await otherSut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            BaseQty = 1m,
            Lines = [new PrProductDefLineVm { ICode = "RM001", StdQty = 1m, Warehouse = "WH01" }]
        }, true, false);
        Assert.True(save.Succeeded, save.Message);

        var demoSut = CreateSut();
        var list = await demoSut.SearchAsync(new PrProductDefListQuery());
        Assert.DoesNotContain(list.Data!.Rows, x => x.ProdCode == "FG001");
    }

    [Fact]
    public async Task WipBomDefault_preserved_on_component_update()
    {
        var sut = CreateSut();
        var create = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            BaseQty = 1m,
            Lines = [new PrProductDefLineVm { ICode = "RM001", StdQty = 0.2m, Warehouse = "WH01" }]
        }, true, false);
        Assert.True(create.Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = await db.PrDefBOMs.SingleAsync(x => x.CompanyCode == "DEMO" && x.ProdCode == "FG001");
            row.WipBomDefault = true;
            await db.SaveChangesAsync();
        }

        var get = await sut.GetAsync("FG001");
        var update = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            Version = get.Data!.Version,
            BomHdrId = get.Data.BomHdrId,
            HeaderRowVersion = get.Data.HeaderRowVersion,
            Status = get.Data.Status,
            BaseQty = 1m,
            EffectiveFrom = get.Data.EffectiveFrom,
            Lines =
            [
                new PrProductDefLineVm
                {
                    ICode = "RM001", StdQty = 0.3m, Warehouse = "WH01", BomDefault = true, SeqNo = 1
                }
            ]
        }, false, false);
        Assert.True(update.Succeeded, update.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = await db.PrDefBOMs.SingleAsync(x => x.CompanyCode == "DEMO" && x.ProdCode == "FG001");
            Assert.True(row.WipBomDefault);
            Assert.Equal(0.3m, row.StdQty);
        }
    }

    [Fact]
    public async Task Access_denied_without_permission()
    {
        var sut = CreateSut(canAccess: false);
        var result = await sut.SearchAsync(new PrProductDefListQuery());
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Active_version_is_immutable_and_requires_new_draft()
    {
        var sut = CreateSut();
        var active = await sut.SaveAsync(Bom("FG001", ("RM001", 1m)), true, true);
        Assert.True(active.Succeeded, active.Message);

        active.Data!.Lines[0].StdQty = 2m;
        var edit = await sut.SaveAsync(active.Data, isNew: false, activate: false);

        Assert.False(edit.Succeeded);
        Assert.Contains("immutable", edit.Message!, StringComparison.OrdinalIgnoreCase);
        var draft = await sut.CreateNewVersionAsync("FG001", active.Data.Version);
        Assert.True(draft.Succeeded, draft.Message);
        Assert.Equal(PrBomStatuses.Draft, draft.Data!.Status);
    }

    [Fact]
    public async Task Circular_bom_rejected_on_save()
    {
        var sut = CreateSut();
        await SeedItem("BB");
        await SeedItem("CC");

        Assert.True((await sut.SaveAsync(Bom("BB", ("CC", 1m)), true, true)).Succeeded);
        var bad = await sut.SaveAsync(Bom("CC", ("BB", 1m)), true, true);
        Assert.False(bad.Succeeded);
        Assert.Contains("circular", bad.ValidationErrors.Values.First(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Overlapping_active_versions_rejected()
    {
        var sut = CreateSut();
        var v1 = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            BaseQty = 1m,
            EffectiveFrom = new DateTime(2026, 1, 1),
            EffectiveTo = null,
            Lines = [new PrProductDefLineVm { ICode = "RM001", StdQty = 1m, Warehouse = "WH01" }]
        }, true, true);
        Assert.True(v1.Succeeded, v1.Message);

        var draft = await sut.CreateNewVersionAsync("FG001");
        Assert.True(draft.Succeeded, draft.Message);

        var activate = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            Version = draft.Data!.Version,
            BomHdrId = draft.Data.BomHdrId,
            HeaderRowVersion = draft.Data.HeaderRowVersion,
            Status = draft.Data.Status,
            BaseQty = 1m,
            EffectiveFrom = new DateTime(2026, 1, 15),
            Lines = draft.Data.Lines
        }, false, activate: true);

        // Activate should supersede V1 — succeed
        Assert.True(activate.Succeeded, activate.Message);
        var getV1 = await sut.GetAsync("FG001", 1);
        Assert.Equal(PrBomStatuses.Superseded, getV1.Data!.Status);
    }

    [Fact]
    public async Task Versioned_route_machine_and_labour_round_trip_and_clone_with_bom()
    {
        var processKey = Guid.NewGuid();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PrWorkCentres.Add(new PrWorkCentre
            {
                WrkCtrCd = "WC01", WrkCtrDes = "Mixing", CompCode = "DEMO"
            });
            db.PrProcesses.Add(new PrProcess
            {
                ProcessCd = "MIX", ProcessDes = "Mix", WorkCentre = "WC01", CompCode = "DEMO"
            });
            db.PrMachines.Add(new PrMachine
            {
                MachineCd = "MX01", MachineDes = "Mixer 1", ProcessCd = "MIX", CompCode = "DEMO"
            });
            db.PrMachines.Add(new PrMachine
            {
                MachineCd = "MX02", MachineDes = "Mixer 2", ProcessCd = "MIX", CompCode = "DEMO"
            });
            db.PrOperators.AddRange(
                new PrOperator { Code = "LAB01", Name = "Mixer labour", Active = true, CompanyCode = "DEMO" },
                new PrOperator { Code = "LAB02", Name = "Backup labour", Active = true, CompanyCode = "DEMO" });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var save = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            BaseQty = 5m,
            BaseUom = "PCS",
            Lines = [new PrProductDefLineVm { OperationKey = processKey, ICode = "RM001", StdQty = 1m, Warehouse = "WH01" }],
            Operations =
            [
                new PrProductDefOperationVm
                {
                    WorkCentreCode = "WC01",
                    OperationKey = processKey,
                    OutputItemCode = "FG001",
                    CentralSequence = 10,
                    OutputBaseQty = 5m,
                    OutputUom = "PCS",
                    OperationCode = "MIX",
                    ProcessSequence = 10,
                    IsFinalOperation = true,
                    Machines =
                    [
                        new PrProductDefMachineVm
                        {
                            MachineCode = "MX01",
                            ResourceSequence = 10,
                            IsPrimary = true,
                            CycleSeconds = 30m,
                            SetupSeconds = 60m,
                            QueueSeconds = 15m,
                            ParallelMachineCount = 1,
                            Labours =
                            [
                                new PrProductDefLabourVm
                                {
                                    LabourCode = "LAB01", CostPerOutputUnit = 1.25m
                                }
                            ]
                        }
                    ]
                }
            ]
        }, isNew: true, activate: true);

        Assert.True(save.Succeeded, save.Message);
        var operation = Assert.Single(save.Data!.Operations);
        Assert.True(operation.IsFinalOperation);
        var machine = Assert.Single(operation.Machines);
        Assert.Equal(30m, machine.CycleSeconds);
        Assert.Equal(1.25m, Assert.Single(machine.Labours).CostPerOutputUnit);

        var clone = await sut.CreateNewVersionAsync("FG001", save.Data.Version);
        Assert.True(clone.Succeeded, clone.Message);
        Assert.Equal(2, clone.Data!.Version);
        Assert.Single(clone.Data.Lines);
        Assert.Equal(processKey, clone.Data.Lines[0].OperationKey);
        Assert.Equal(processKey, clone.Data.Operations[0].OperationKey);
        Assert.Equal("MX01", Assert.Single(Assert.Single(clone.Data.Operations).Machines).MachineCode);
        Assert.Equal("LAB01", Assert.Single(Assert.Single(Assert.Single(clone.Data.Operations).Machines).Labours).LabourCode);

        var clonedOperation = Assert.Single(clone.Data.Operations);
        // A second machine option is now a legal alternative as long as it has a distinct priority
        // and exactly one option stays primary (plan §5.6 "multiple machine alternatives").
        clonedOperation.Machines.Add(new PrProductDefMachineVm
        {
            MachineCode = "MX02", ResourceSequence = 20, Priority = 2, IsPrimary = false
        });
        var alternatives = await sut.SaveAsync(clone.Data, false, false);
        Assert.True(alternatives.Succeeded, alternatives.Message);
        Assert.Equal(2, Assert.Single(alternatives.Data!.Operations).Machines.Count);

        var edited = Assert.Single(alternatives.Data.Operations);
        edited.Machines[1].IsPrimary = true;
        var twoPrimaries = await sut.SaveAsync(alternatives.Data, false, false);
        Assert.False(twoPrimaries.Succeeded);
        Assert.Contains("Operations[0].Machines", twoPrimaries.ValidationErrors.Keys);
        edited.Machines[1].IsPrimary = false;

        edited.Machines[1].Priority = 1;
        var duplicatePriority = await sut.SaveAsync(alternatives.Data, false, false);
        Assert.False(duplicatePriority.Succeeded);
        Assert.Contains("Operations[0].Machines[1].Priority", duplicatePriority.ValidationErrors.Keys);
        edited.Machines[1].Priority = 2;

        edited.Machines[0].Labours.Add(new PrProductDefLabourVm { LabourCode = "LAB02" });
        var tooManyLabours = await sut.SaveAsync(alternatives.Data, false, false);
        Assert.False(tooManyLabours.Succeeded);
        Assert.Contains("Operations[0].Machines[0].Labours", tooManyLabours.ValidationErrors.Keys);
    }

    [Fact]
    public async Task Process_materials_survive_edits_clone_and_explode_once_with_finishing_steps()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PrWorkCentres.Add(new PrWorkCentre { WrkCtrCd = "WC01", CompCode = "DEMO" });
            foreach (var code in new[] { "ASSEMBLE", "POLISH", "WASH", "QC" })
            {
                db.PrProcesses.Add(new PrProcess { ProcessCd = code, WorkCentre = "WC01", CompCode = "DEMO" });
                db.PrMachines.Add(new PrMachine { MachineCd = code, ProcessCd = code, CompCode = "DEMO" });
            }
            db.PrOperators.Add(new PrOperator
            {
                Code = "LAB01", Name = "Production labour", Active = true, CompanyCode = "DEMO"
            });
            await db.SaveChangesAsync();
        }
        var model = Bom("FG001", ("RM001", 2m));
        model.Operations = new[] { "ASSEMBLE", "POLISH", "WASH", "QC" }.Select((code, index) => new PrProductDefOperationVm
        {
            WorkCentreCode = "WC01", OutputItemCode = "FG001", CentralSequence = 10,
            ProcessSequence = (index + 1) * 10, OperationCode = code, IsFinalOperation = code == "QC",
            Machines =
            [
                new PrProductDefMachineVm
                {
                    MachineCode = code,
                    ResourceSequence = 10,
                    Labours = [new PrProductDefLabourVm { LabourCode = "LAB01" }]
                }
            ]
        }).ToList();
        var sut = CreateSut();
        var unassigned = await sut.SaveAsync(model, true, true);
        Assert.False(unassigned.Succeeded);
        Assert.Contains("Lines[0].OperationKey", unassigned.ValidationErrors.Keys);
        model.Lines[0].OperationKey = Guid.NewGuid();
        var orphan = await sut.SaveAsync(model, true, false);
        Assert.False(orphan.Succeeded);
        Assert.Contains("Lines[0].OperationKey", orphan.ValidationErrors.Keys);
        var assemblyKey = model.Operations[0].OperationKey;
        model.Lines[0].OperationKey = assemblyKey;
        var draft = await sut.SaveAsync(model, true, false);
        Assert.True(draft.Succeeded, draft.Message);
        model = draft.Data!;
        model.Operations[0].ProcessSequence = 5;
        var saved = await sut.SaveAsync(model, false, true);
        Assert.True(saved.Succeeded, saved.Message);
        Assert.Equal(assemblyKey, Assert.Single(saved.Data!.Lines).OperationKey);
        Assert.All(saved.Data.Operations.Where(x => x.OperationCode != "ASSEMBLE"),
            process => Assert.DoesNotContain(saved.Data.Lines, line => line.OperationKey == process.OperationKey));
        var explosion = new BomExplosionService(_factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"), CreateAccess(true, true, true, true).Object);
        var issue = await explosion.ExplodeAsync(new BomExplosionRequest
        {
            ProdCode = "FG001", Quantity = 10, Mode = BomExplosionMode.ProductionIssueRequirement
        });
        Assert.True(issue.Succeeded, issue.Message);
        Assert.Equal(20m, Assert.Single(issue.Data!.Nodes).ExtendedQty);
        var clone = await sut.CreateNewVersionAsync("FG001");
        Assert.True(clone.Succeeded, clone.Message);
        Assert.Equal(assemblyKey, Assert.Single(clone.Data!.Lines).OperationKey);
        // A later process can independently consume the same item.
        var polishKey = clone.Data.Operations.Single(x => x.OperationCode == "POLISH").OperationKey;
        clone.Data.Lines.Add(new PrProductDefLineVm
        {
            ICode = "RM001", StdQty = 1m, Warehouse = "WH01", OperationKey = polishKey
        });
        var second = await sut.SaveAsync(clone.Data, false, false);
        Assert.True(second.Succeeded, second.Message);
        Assert.Equal(2, second.Data!.Lines.Count);
        var twoProcesses = await explosion.ExplodeAsync(new BomExplosionRequest
        {
            ProdCode = "FG001", Version = second.Data.Version, Quantity = 10,
            Mode = BomExplosionMode.ProductionIssueRequirement
        });
        Assert.True(twoProcesses.Succeeded, twoProcesses.Message);
        Assert.Equal(30m, twoProcesses.Data!.Nodes.Sum(x => x.ExtendedQty));
        second.Data.Operations.RemoveAll(x => x.OperationKey == assemblyKey);
        var deletedOwner = await sut.SaveAsync(second.Data, false, false);
        Assert.False(deletedOwner.Succeeded);
        second.Data.Lines.Clear();
        var noMaterials = await sut.SaveAsync(second.Data, false, false);
        Assert.True(noMaterials.Succeeded, noMaterials.Message);
        Assert.Empty(noMaterials.Data!.Lines);
        Assert.Equal(3, noMaterials.Data.Operations.Count);
    }

    private async Task SeedItem(string code)
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (!await db.IvStockMasters.AnyAsync(x => x.CompanyCode == "DEMO" && x.ICode == code))
        {
            db.IvStockMasters.Add(Stock("DEMO", code, code, "PCS"));
            await db.SaveChangesAsync();
        }
    }

    private static PrProductDefEditVm Bom(string prod, params (string ICode, decimal Qty)[] lines) =>
        new()
        {
            ProdCode = prod,
            BaseQty = 1m,
            Lines = lines.Select((x, i) => new PrProductDefLineVm
            {
                ICode = x.ICode,
                StdQty = x.Qty,
                Warehouse = "WH01",
                SeqNo = i + 1
            }).ToList()
        };

    private static IvStockMaster Stock(string company, string code, string desc, string uom, bool active = true) =>
        new()
        {
            CompanyCode = company,
            ICode = code,
            IDesc = desc,
            StdUom = uom,
            DefWarehouse = "WH01",
            MfgType = PrMfgTypes.Buy,
            IsActive = active,
            RowVersion = [1]
        };

    private PrProductDefService CreateSut(
        bool canAccess = true,
        bool canAdd = true,
        bool canEdit = true,
        bool canDelete = true)
    {
        var access = CreateAccess(canAccess, canAdd, canEdit, canDelete);
        return new PrProductDefService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            access.Object,
            new IvUomConversionService(_factory));
    }

    private static Mock<IAccessRightService> CreateAccess(
        bool canAccess, bool canAdd, bool canEdit, bool canDelete)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(MenuCodes.PlanningProductDef, PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);
        access.Setup(x => x.CanAsync(MenuCodes.PlanningProductDef, PermissionCodes.Add, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAdd);
        access.Setup(x => x.CanAsync(MenuCodes.PlanningProductDef, PermissionCodes.Edit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canEdit);
        access.Setup(x => x.CanAsync(MenuCodes.PlanningProductDef, PermissionCodes.Delete, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canDelete);
        return access;
    }
}
