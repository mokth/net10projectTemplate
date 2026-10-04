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
public class PrBomStructureTreeTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PrBomStructureTreeTests()
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
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Structure_fails_for_missing_product()
    {
        var sut = CreateSut();
        var result = await sut.GetStructureTreeAsync("NOPE", PrProductDefinitionCodes.Standard);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Structure_empty_bom_returns_root_only()
    {
        await EnsureStock("FG002", PrMfgTypes.Make);
        await EnsureStock("RM001", PrMfgTypes.Buy);
        var sut = CreateSut();
        var created = await sut.SaveAsync(Bom("FG002", ("RM001", 1m)), true, false);
        Assert.True(created.Succeeded, created.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var lines = db.PrDefBOMs.Where(x => x.ProdCode == "FG002");
            db.PrDefBOMs.RemoveRange(lines);
            await db.SaveChangesAsync();
        }

        var tree = await sut.GetStructureTreeAsync("FG002", PrProductDefinitionCodes.Standard);
        Assert.True(tree.Succeeded, tree.Message);
        Assert.Single(tree.Data!.Nodes);
        Assert.Equal(PrBomStructureKeys.Root("FG002", PrProductDefinitionCodes.Standard), tree.Data.Nodes[0].Key);
        Assert.Null(tree.Data.Nodes[0].ParentKey);
        Assert.Null(tree.Data.Nodes[0].OwnerProdCode);
    }

    [Fact]
    public async Task Structure_single_level_buy_does_not_recurse()
    {
        await EnsureStock("FG001", PrMfgTypes.Make);
        await EnsureStock("RM001", PrMfgTypes.Buy);
        var sut = CreateSut();
        Assert.True((await sut.SaveAsync(Bom("FG001", ("RM001", 2m)), true, true)).Succeeded);

        var tree = await sut.GetStructureTreeAsync("FG001", PrProductDefinitionCodes.Standard);
        Assert.True(tree.Succeeded, tree.Message);
        Assert.Equal(2, tree.Data!.Nodes.Count);
        var rm = tree.Data.Nodes.Single(x => x.ItemCode == "RM001");
        Assert.Equal("FG001", rm.OwnerProdCode);
        Assert.Equal(PrBomStructureKeys.Root("FG001", PrProductDefinitionCodes.Standard), rm.ParentKey);
        Assert.Equal(PrBomStructureNodeStatus.Normal, rm.Status);
        Assert.True(rm.SourceLineUid > 0);
        Assert.Equal(PrBomStructureKeys.Line(rm.ParentKey!, "FG001", PrProductDefinitionCodes.Standard, rm.SourceLineUid!.Value.ToString()), rm.Key);
        Assert.DoesNotContain(tree.Data.Nodes, x => x.ParentKey == rm.Key);
    }

    [Fact]
    public async Task Structure_multi_level_make_make_buy()
    {
        await EnsureStock("FG001", PrMfgTypes.Make);
        await EnsureStock("SA001", PrMfgTypes.Make);
        await EnsureStock("RM001", PrMfgTypes.Buy);
        var sut = CreateSut();
        Assert.True((await sut.SaveAsync(Bom("SA001", ("RM001", 3m)), true, true)).Succeeded);
        Assert.True((await sut.SaveAsync(Bom("FG001", ("SA001", 1m)), true, true)).Succeeded);

        var tree = await sut.GetStructureTreeAsync("FG001", PrProductDefinitionCodes.Standard);
        Assert.True(tree.Succeeded, tree.Message);
        Assert.Equal(3, tree.Data!.Nodes.Count);

        var sa = tree.Data.Nodes.Single(x => x.ItemCode == "SA001");
        Assert.Equal("FG001", sa.OwnerProdCode);
        var rm = tree.Data.Nodes.Single(x => x.ItemCode == "RM001");
        Assert.Equal("SA001", rm.OwnerProdCode);
        Assert.Equal(sa.Key, rm.ParentKey);
        Assert.StartsWith(sa.Key + "/", rm.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Structure_phantom_recurses()
    {
        await EnsureStock("FG001", PrMfgTypes.Make);
        await EnsureStock("PH001", PrMfgTypes.Phantom);
        await EnsureStock("RM001", PrMfgTypes.Buy);
        var sut = CreateSut();
        Assert.True((await sut.SaveAsync(Bom("PH001", ("RM001", 1m)), true, true)).Succeeded);
        Assert.True((await sut.SaveAsync(Bom("FG001", ("PH001", 1m)), true, true)).Succeeded);

        var tree = await sut.GetStructureTreeAsync("FG001", PrProductDefinitionCodes.Standard);
        Assert.True(tree.Succeeded, tree.Message);
        Assert.Contains(tree.Data!.Nodes, x => x.ItemCode == "RM001" && x.OwnerProdCode == "PH001");
    }

    [Fact]
    public async Task Structure_missing_make_and_phantom_bom_status()
    {
        await EnsureStock("FG001", PrMfgTypes.Make);
        await EnsureStock("MK001", PrMfgTypes.Make);
        await EnsureStock("PH001", PrMfgTypes.Phantom);
        await EnsureStock("RM001", PrMfgTypes.Buy);
        var sut = CreateSut();
        Assert.True((await sut.SaveAsync(Bom("FG001", ("MK001", 1m), ("PH001", 1m), ("RM001", 1m)), true, true)).Succeeded);

        var tree = await sut.GetStructureTreeAsync("FG001", PrProductDefinitionCodes.Standard);
        Assert.True(tree.Succeeded, tree.Message);
        Assert.Equal(PrBomStructureNodeStatus.MissingBom,
            tree.Data!.Nodes.Single(x => x.ItemCode == "MK001").Status);
        Assert.Equal(PrBomStructureNodeStatus.MissingBom,
            tree.Data.Nodes.Single(x => x.ItemCode == "PH001").Status);
        Assert.Equal(PrBomStructureNodeStatus.Normal,
            tree.Data.Nodes.Single(x => x.ItemCode == "RM001").Status);
        Assert.Equal(4, tree.Data.Nodes.Count); // root + 3
    }

    [Fact]
    public async Task Structure_same_component_under_different_parents_has_distinct_keys()
    {
        await EnsureStock("FG001", PrMfgTypes.Make);
        await EnsureStock("BB001", PrMfgTypes.Make);
        await EnsureStock("BB002", PrMfgTypes.Make);
        await EnsureStock("RM001", PrMfgTypes.Buy);
        var sut = CreateSut();
        Assert.True((await sut.SaveAsync(Bom("BB001", ("RM001", 2m)), true, true)).Succeeded);
        Assert.True((await sut.SaveAsync(Bom("BB002", ("RM001", 5m)), true, true)).Succeeded);
        Assert.True((await sut.SaveAsync(Bom("FG001", ("BB001", 1m), ("BB002", 1m)), true, true)).Succeeded);

        var tree = await sut.GetStructureTreeAsync("FG001", PrProductDefinitionCodes.Standard);
        Assert.True(tree.Succeeded, tree.Message);
        var rms = tree.Data!.Nodes.Where(x => x.ItemCode == "RM001").ToList();
        Assert.Equal(2, rms.Count);
        Assert.NotEqual(rms[0].Key, rms[1].Key);
        Assert.Equal("BB001", rms.Single(x => x.OwnerProdCode == "BB001").OwnerProdCode);
        Assert.Equal("BB002", rms.Single(x => x.OwnerProdCode == "BB002").OwnerProdCode);
    }

    [Fact]
    public async Task Save_still_rejects_duplicate_same_parent()
    {
        await EnsureStock("FG001", PrMfgTypes.Make);
        await EnsureStock("RM001", PrMfgTypes.Buy);
        var sut = CreateSut();
        var dup = await sut.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "FG001",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            BaseQty = 1m,
            Lines =
            [
                new PrProductDefLineVm { ICode = "RM001", StdQty = 2m, Warehouse = "WH01", SeqNo = 1 },
                new PrProductDefLineVm { ICode = "RM001", StdQty = 5m, Warehouse = "WH01", SeqNo = 2 }
            ]
        }, true, true);
        Assert.False(dup.Succeeded);
    }

    [Fact]
    public async Task Structure_draft_root_and_draft_child_visible()
    {
        await EnsureStock("FG001", PrMfgTypes.Make);
        await EnsureStock("SA001", PrMfgTypes.Make);
        await EnsureStock("RM001", PrMfgTypes.Buy);
        var sut = CreateSut();
        Assert.True((await sut.SaveAsync(Bom("SA001", ("RM001", 1m)), true, false)).Succeeded); // draft child
        Assert.True((await sut.SaveAsync(Bom("FG001", ("SA001", 1m)), true, false)).Succeeded); // draft root

        var getRoot = await sut.GetAsync("FG001", PrProductDefinitionCodes.Standard);
        var getChild = await sut.GetAsync("SA001", PrProductDefinitionCodes.Standard);
        Assert.Equal(PrBomStatuses.Draft, getRoot.Data!.Status);
        Assert.Equal(PrBomStatuses.Draft, getChild.Data!.Status);

        var tree = await sut.GetStructureTreeAsync("FG001", PrProductDefinitionCodes.Standard);
        Assert.True(tree.Succeeded, tree.Message);
        Assert.Equal(PrBomStatuses.Draft, tree.Data!.RootBomStatus);
        Assert.Contains(tree.Data.Nodes, x => x.ItemCode == "RM001" && x.OwnerProdCode == "SA001");
    }

    [Fact]
    public async Task Structure_version_resolution_matches_GetAsync()
    {
        await EnsureStock("FG001", PrMfgTypes.Make);
        await EnsureStock("RM001", PrMfgTypes.Buy);
        await EnsureStock("RM002", PrMfgTypes.Buy);
        var sut = CreateSut();
        var v1 = await sut.SaveAsync(Bom("FG001", ("RM001", 1m)), true, true);
        Assert.True(v1.Succeeded, v1.Message);
        var draft = await sut.CreateNewVersionAsync("FG001", PrProductDefinitionCodes.Standard, 1);
        Assert.True(draft.Succeeded, draft.Message);
        draft.Data!.Lines =
        [
            new PrProductDefLineVm { ICode = "RM002", StdQty = 9m, Warehouse = "WH01", SeqNo = 1 }
        ];
        Assert.True((await sut.SaveAsync(draft.Data, false, false)).Succeeded);

        var latestGet = await sut.GetAsync("FG001", PrProductDefinitionCodes.Standard);
        var latestTree = await sut.GetStructureTreeAsync("FG001", PrProductDefinitionCodes.Standard);
        Assert.Equal(latestGet.Data!.Version, latestTree.Data!.RootBomVersion);
        Assert.Contains(latestTree.Data.Nodes, x => x.ItemCode == "RM002");

        var v1Get = await sut.GetAsync("FG001", PrProductDefinitionCodes.Standard, 1);
        var v1Tree = await sut.GetStructureTreeAsync("FG001", PrProductDefinitionCodes.Standard, 1);
        Assert.Equal(v1Get.Data!.Version, v1Tree.Data!.RootBomVersion);
        Assert.Contains(v1Tree.Data.Nodes, x => x.ItemCode == "RM001");
        Assert.DoesNotContain(v1Tree.Data.Nodes, x => x.ItemCode == "RM002");
    }

    [Fact]
    public async Task Structure_circular_marks_node_and_continues()
    {
        await EnsureStock("A", PrMfgTypes.Make);
        await EnsureStock("B", PrMfgTypes.Make);
        await EnsureStock("RM001", PrMfgTypes.Buy);
        var sut = CreateSut();
        Assert.True((await sut.SaveAsync(Bom("B", ("RM001", 1m)), true, true)).Succeeded);
        Assert.True((await sut.SaveAsync(Bom("A", ("B", 1m)), true, true)).Succeeded);

        // Force cycle A → B → A by inserting line directly (bypass Save cycle check on active graph)
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var bHdr = await db.PrBomHdrs.SingleAsync(x => x.ProdCode == "B");
            db.PrDefBOMs.Add(new PrDefBOM
            {
                BomHdrId = bHdr.Uid,
                CompanyCode = "DEMO",
                ProdCode = "B",
                ICode = "A",
                StdQty = 1m,
                SeqNo = 2,
                Warehouse = "WH01",
                BomDefault = true,
                RowVersion = []
            });
            await db.SaveChangesAsync();
        }

        var tree = await sut.GetStructureTreeAsync("A", PrProductDefinitionCodes.Standard);
        Assert.True(tree.Succeeded, tree.Message);
        var circular = tree.Data!.Nodes.Single(x => x.ItemCode == "A" && x.OwnerProdCode == "B");
        Assert.Equal(PrBomStructureNodeStatus.Circular, circular.Status);
        Assert.DoesNotContain(tree.Data.Nodes, x => x.ParentKey == circular.Key);
        Assert.Contains(tree.Data.Nodes, x => x.ItemCode == "RM001");
    }

    [Fact]
    public async Task Structure_max_depth_marks_node()
    {
        // Build chain MAKE0 → MAKE1 → ... deeper than MaxDepth
        var sut = CreateSut();
        const int depth = PrBomStructureKeys.MaxDepth + 2;
        for (var i = 0; i <= depth; i++)
        {
            await EnsureStock($"M{i:D2}", PrMfgTypes.Make);
        }

        await EnsureStock("RM001", PrMfgTypes.Buy);
        Assert.True((await sut.SaveAsync(Bom($"M{depth:D2}", ("RM001", 1m)), true, true)).Succeeded);
        for (var i = depth - 1; i >= 0; i--)
        {
            Assert.True((await sut.SaveAsync(Bom($"M{i:D2}", ($"M{i + 1:D2}", 1m)), true, true)).Succeeded);
        }

        var tree = await sut.GetStructureTreeAsync("M00", PrProductDefinitionCodes.Standard);
        Assert.True(tree.Succeeded, tree.Message);
        Assert.Contains(tree.Data!.Nodes, x => x.Status == PrBomStructureNodeStatus.MaxDepth);
        // Should not have exploded all the way to RM001 if depth stopped early
        var maxNode = tree.Data.Nodes.First(x => x.Status == PrBomStructureNodeStatus.MaxDepth);
        Assert.True(maxNode.Level >= PrBomStructureKeys.MaxDepth);
    }

    private async Task EnsureStock(string code, string mfgType)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var existing = await db.IvStockMasters
            .FirstOrDefaultAsync(x => x.CompanyCode == "DEMO" && x.ICode == code);
        if (existing is null)
        {
            db.IvStockMasters.Add(new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = code,
                IDesc = code,
                StdUom = "PCS",
                DefWarehouse = "WH01",
                MfgType = mfgType,
                IsActive = true,
                RowVersion = [1]
            });
        }
        else
        {
            existing.MfgType = mfgType;
        }

        await db.SaveChangesAsync();
    }

    private static PrProductDefEditVm Bom(string prod, params (string ICode, decimal Qty)[] lines) =>
        new()
        {
            ProdCode = prod,
            DefinitionCode = PrProductDefinitionCodes.Standard,
            BaseQty = 1m,
            Lines = lines.Select((x, i) => new PrProductDefLineVm
            {
                ICode = x.ICode,
                StdQty = x.Qty,
                Warehouse = "WH01",
                SeqNo = i + 1
            }).ToList()
        };

    private PrProductDefService CreateSut()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(MenuCodes.PlanningProductDef, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new PrProductDefService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            access.Object,
            new IvUomConversionService(_factory));
    }
}
