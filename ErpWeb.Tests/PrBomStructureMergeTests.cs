using ErpWeb.Core.Planning;
using ErpWeb.Model.Entities.Planning;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public class PrBomStructureMergeTests
{
    [Fact]
    public void Process_filter_keeps_multilevel_descendants_only_for_selected_process()
    {
        var assembly = Guid.NewGuid();
        var wash = Guid.NewGuid();
        List<PrProductDefLineVm> lines =
        [
            new() { Uid = 1, OperationKey = assembly },
            new() { Uid = 2, OperationKey = wash }
        ];
        List<PrBomStructureNode> nodes =
        [
            new() { Key = "root", ItemCode = "FG" },
            new() { Key = "assembly", ParentKey = "root", OwnerProdCode = "FG", SourceLineUid = 1 },
            new() { Key = "nested", ParentKey = "assembly", OwnerProdCode = "SUB", SourceLineUid = 3 },
            new() { Key = "wash", ParentKey = "root", OwnerProdCode = "FG", SourceLineUid = 2 }
        ];
        var selected = ErpWeb.UI.Planning.Masters.PrProductDefEntry.FilterProcessBomNodes(nodes, lines, "FG", assembly);
        Assert.Equal(new[] { "root", "assembly", "nested" }, selected.Select(x => x.Key));
        var finishing = ErpWeb.UI.Planning.Masters.PrProductDefEntry.FilterProcessBomNodes(nodes, lines, "FG", Guid.NewGuid());
        Assert.Equal("root", Assert.Single(finishing).Key);
        Assert.Empty(ErpWeb.UI.Planning.Masters.PrProductDefEntry.FilterProcessBomNodes(nodes, lines, "FG", null));
    }

    [Fact]
    public void Merge_replaces_owner_children_and_keeps_descendant_subtree()
    {
        var root = PrBomStructureKeys.Root("FG001");
        var bbKey = PrBomStructureKeys.Line(root, "FG001", "10");
        var rmKey = PrBomStructureKeys.Line(bbKey, "BB001", "20");
        var persisted = new List<PrBomStructureNode>
        {
            new()
            {
                Key = root, ParentKey = null, Level = 0, ItemCode = "FG001",
                MfgType = PrMfgTypes.Make, Status = PrBomStructureNodeStatus.Normal
            },
            new()
            {
                Key = bbKey, ParentKey = root, Level = 1, ItemCode = "BB001",
                OwnerProdCode = "FG001", SourceLineUid = 10, MfgType = PrMfgTypes.Make,
                StdQty = 1m, Status = PrBomStructureNodeStatus.Normal
            },
            new()
            {
                Key = rmKey, ParentKey = bbKey, Level = 2, ItemCode = "RM001",
                OwnerProdCode = "BB001", SourceLineUid = 20, MfgType = PrMfgTypes.Buy,
                StdQty = 2m, Status = PrBomStructureNodeStatus.Normal
            }
        };

        var model = new PrProductDefEditVm
        {
            ProdCode = "FG001",
            BomHdrId = 1,
            Version = 1,
            Status = PrBomStatuses.Draft,
            Lines =
            [
                new PrProductDefLineVm
                {
                    Uid = 10, ICode = "BB001", StdQty = 3m, MfgType = PrMfgTypes.Make, SeqNo = 1
                },
                new PrProductDefLineVm
                {
                    TempId = "T1", ICode = "RM002", StdQty = 1m, MfgType = PrMfgTypes.Buy, SeqNo = 2
                }
            ]
        };

        var merged = ErpWeb.UI.Planning.Masters.PrProductDefEntry.MergeStructureWithCurrentOwner(
            persisted, "FG001", "FG001", model);

        Assert.Contains(merged, x => x.ItemCode == "RM002" && x.OwnerProdCode == "FG001");
        var bb = merged.Single(x => x.ItemCode == "BB001");
        Assert.Equal(3m, bb.StdQty);
        Assert.Contains(merged, x => x.ItemCode == "RM001" && x.OwnerProdCode == "BB001" && x.ParentKey == bb.Key);
        Assert.Equal(2, merged.Count(x => x.OwnerProdCode == "FG001"));
    }
}
