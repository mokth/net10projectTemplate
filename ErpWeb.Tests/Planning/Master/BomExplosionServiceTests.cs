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
public class BomExplosionServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public BomExplosionServiceTests()
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

        // A, AA, BB, BBA, BBB, CC, CCA, CCB, CCBA, CCBB, C
        foreach (var code in new[] { "A", "AA", "BB", "BBA", "BBB", "CC", "CCA", "CCB", "CCBA", "CCBB", "C", "P", "X", "Y" })
        {
            db.IvStockMasters.Add(new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = code,
                IDesc = code,
                StdUom = "PCS",
                DefWarehouse = "WH01",
                MfgType = code is "AA" or "BBA" or "BBB" or "CCA" or "CCBA" or "CCBB" or "X" or "Y"
                    ? PrMfgTypes.Buy
                    : code == "P" ? PrMfgTypes.Phantom : PrMfgTypes.Make,
                IsActive = true,
                RowVersion = [1]
            });
        }

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Material_requirement_A_times_1_matches_matrix()
    {
        var defs = CreateDefs();
        await SaveBom(defs, "BB", ("BBA", 1m), ("BBB", 2m));
        await SaveBom(defs, "CCB", ("CCBA", 3m), ("CCBB", 5m));
        await SaveBom(defs, "CC", ("CCA", 1m), ("CCB", 2m));
        await SaveBom(defs, "A", ("AA", 1m), ("BB", 1m), ("CC", 2m));

        var explode = CreateExplosion();
        var result = await explode.ExplodeAsync(new BomExplosionRequest
        {
            DefinitionCode = PrProductDefinitionCodes.Standard,
            ProdCode = "A",
            Quantity = 1m,
            Mode = BomExplosionMode.MaterialRequirement
        });

        Assert.True(result.Succeeded, result.Message);
        var map = result.Data!.Nodes.ToDictionary(x => x.ItemCode, x => x.ExtendedQty, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1m, map["AA"]);
        Assert.Equal(1m, map["BB"]);
        Assert.Equal(1m, map["BBA"]);
        Assert.Equal(2m, map["BBB"]);
        Assert.Equal(2m, map["CC"]);
        Assert.Equal(2m, map["CCA"]);
        Assert.Equal(4m, map["CCB"]);
        Assert.Equal(12m, map["CCBA"]);
        Assert.Equal(20m, map["CCBB"]);
    }

    [Fact]
    public async Task Production_issue_for_A_is_direct_children_only()
    {
        var defs = CreateDefs();
        await SaveBom(defs, "BB", ("BBA", 1m), ("BBB", 2m));
        await SaveBom(defs, "CCB", ("CCBA", 3m), ("CCBB", 5m));
        await SaveBom(defs, "CC", ("CCA", 1m), ("CCB", 2m));
        await SaveBom(defs, "A", ("AA", 1m), ("BB", 1m), ("CC", 2m));

        var explode = CreateExplosion();
        var result = await explode.ExplodeAsync(new BomExplosionRequest
        {
            DefinitionCode = PrProductDefinitionCodes.Standard,
            ProdCode = "A",
            Quantity = 1m,
            Mode = BomExplosionMode.ProductionIssueRequirement
        });

        Assert.True(result.Succeeded, result.Message);
        var codes = result.Data!.Nodes.Select(x => x.ItemCode).OrderBy(x => x).ToList();
        Assert.Equal(["AA", "BB", "CC"], codes);
        Assert.DoesNotContain(result.Data.Nodes, x => x.ItemCode is "BBA" or "BBB" or "CCA" or "CCB");
    }

    [Fact]
    public async Task Reusable_subassembly_BB_shared_by_A_and_C()
    {
        var defs = CreateDefs();
        await SaveBom(defs, "BB", ("BBA", 1m), ("BBB", 2m));
        await SaveBom(defs, "A", ("BB", 1m));
        await SaveBom(defs, "C", ("BB", 3m));

        var explode = CreateExplosion();
        var a = await explode.ExplodeAsync(new BomExplosionRequest
        {
            DefinitionCode = PrProductDefinitionCodes.Standard,
            ProdCode = "A", Quantity = 1m, Mode = BomExplosionMode.MaterialRequirement
        });
        var c = await explode.ExplodeAsync(new BomExplosionRequest
        {
            DefinitionCode = PrProductDefinitionCodes.Standard,
            ProdCode = "C", Quantity = 1m, Mode = BomExplosionMode.MaterialRequirement
        });

        Assert.Equal(1m, a.Data!.Nodes.First(x => x.ItemCode == "BBA").ExtendedQty);
        Assert.Equal(3m, c.Data!.Nodes.First(x => x.ItemCode == "BBA").ExtendedQty);

        await using var db = await _factory.CreateDbContextAsync();
        var hdrs = await db.PrBomHdrs.CountAsync(x => x.CompanyCode == "DEMO" && x.ProdCode == "BB");
        Assert.Equal(1, hdrs);
    }

    [Fact]
    public async Task Phantom_explodes_through_not_issued()
    {
        var defs = CreateDefs();
        await SaveBom(defs, "P", ("X", 2m), ("Y", 3m));
        await SaveBom(defs, "A", ("P", 1m));

        var explode = CreateExplosion();
        var mat = await explode.ExplodeAsync(new BomExplosionRequest
        {
            DefinitionCode = PrProductDefinitionCodes.Standard,
            ProdCode = "A", Quantity = 1m, Mode = BomExplosionMode.MaterialRequirement
        });
        Assert.True(mat.Succeeded, mat.Message);
        Assert.DoesNotContain(mat.Data!.Nodes, x => x.ItemCode == "P");
        Assert.Equal(2m, mat.Data.Nodes.First(x => x.ItemCode == "X").ExtendedQty);
        Assert.Equal(3m, mat.Data.Nodes.First(x => x.ItemCode == "Y").ExtendedQty);

        var issue = await explode.ExplodeAsync(new BomExplosionRequest
        {
            DefinitionCode = PrProductDefinitionCodes.Standard,
            ProdCode = "A", Quantity = 1m, Mode = BomExplosionMode.ProductionIssueRequirement
        });
        Assert.DoesNotContain(issue.Data!.Nodes, x => x.ItemCode == "P");
        Assert.Contains(issue.Data.Nodes, x => x.ItemCode == "X");
        Assert.Contains(issue.Data.Nodes, x => x.ItemCode == "Y");
    }

    [Fact]
    public async Task Make_without_bom_is_hard_error()
    {
        var defs = CreateDefs();
        await SaveBom(defs, "A", ("BB", 1m)); // BB is MAKE but has no BOM

        var explode = CreateExplosion();
        var result = await explode.ExplodeAsync(new BomExplosionRequest
        {
            DefinitionCode = PrProductDefinitionCodes.Standard,
            ProdCode = "A", Quantity = 1m, Mode = BomExplosionMode.MaterialRequirement
        });
        Assert.False(result.Succeeded);
        Assert.Contains("BB", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no active BOM", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explicit_version_selects_historical_revision()
    {
        var defs = CreateDefs();
        var v1 = await defs.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "A",
            BaseQty = 1m,
            Lines = [new PrProductDefLineVm { ICode = "AA", StdQty = 1m, Warehouse = "WH01", SeqNo = 1 }]
        }, true, true);
        Assert.True(v1.Succeeded, v1.Message);

        var draft = await defs.CreateNewVersionAsync("A", PrProductDefinitionCodes.Standard);
        draft.Data!.Lines[0].StdQty = 5m;
        var v2 = await defs.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "A",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            Version = draft.Data.Version,
            BomHdrId = draft.Data.BomHdrId,
            HeaderRowVersion = draft.Data.HeaderRowVersion,
            Status = draft.Data.Status,
            BaseQty = 1m,
            Lines = draft.Data.Lines
        }, false, true);
        Assert.True(v2.Succeeded, v2.Message);

        var explode = CreateExplosion();
        var historical = await explode.ExplodeAsync(new BomExplosionRequest
        {
            DefinitionCode = PrProductDefinitionCodes.Standard,
            ProdCode = "A",
            Quantity = 1m,
            Version = 1,
            Mode = BomExplosionMode.ProductionIssueRequirement
        });
        Assert.Equal(1m, historical.Data!.Nodes.Single().ExtendedQty);

        var active = await explode.ExplodeAsync(new BomExplosionRequest
        {
            DefinitionCode = PrProductDefinitionCodes.Standard,
            ProdCode = "A",
            Quantity = 1m,
            Mode = BomExplosionMode.ProductionIssueRequirement
        });
        Assert.Equal(5m, active.Data!.Nodes.Single().ExtendedQty);
    }

    private async Task SaveBom(IPrProductDefService defs, string prod, params (string Code, decimal Qty)[] lines)
    {
        var result = await defs.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = prod,
            DefinitionCode = PrProductDefinitionCodes.Standard,
            BaseQty = 1m,
            Lines = lines.Select((x, i) => new PrProductDefLineVm
            {
                ICode = x.Code,
                StdQty = x.Qty,
                Warehouse = "WH01",
                SeqNo = i + 1
            }).ToList()
        }, isNew: true, activate: true);
        Assert.True(result.Succeeded, $"{prod}: {result.Message}");
    }

    private IPrProductDefService CreateDefs() =>
        new PrProductDefService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            Access().Object,
            new IvUomConversionService(_factory));

    private IBomExplosionService CreateExplosion() =>
        new BomExplosionService(_factory, InventoryTenantTestHelper.CreateTenantContext(location: "SITE"), Access().Object);

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }
}
