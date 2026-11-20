using ErpWeb.UI.Components.Layout;

namespace ErpWeb.Tests.Admin.Inquiry;

[Trait(TestCategories.Name, TestCategories.Menus)]
public class NavMenuTreeFilterTests
{
    private static IReadOnlyList<NavTreeItem> SampleMenu() =>
    [
        new NavTreeItem
        {
            Code = "DASHBOARD",
            Name = "Dashboard",
            Route = "/dashboard",
            Icon = "fa-solid fa-chart-pie"
        },
        new NavTreeItem
        {
            Code = "INVENTORY",
            Name = "Inventory",
            Icon = "fa-solid fa-boxes-stacked",
            Children =
            [
                new NavTreeItem
                {
                    Code = "INV_MASTER",
                    Name = "Master",
                    Children =
                    [
                        new NavTreeItem { Code = "INV_ITEM_MASTER", Name = "Item Master", Route = "/inventory/items" },
                        new NavTreeItem { Code = "INV_WAREHOUSE", Name = "Warehouses", Route = "/inventory/warehouses" }
                    ]
                },
                new NavTreeItem
                {
                    Code = "INV_TRANSACTIONS",
                    Name = "Transactions",
                    Children =
                    [
                        new NavTreeItem { Code = "INV_GOODS_RECEIPT", Name = "Goods Receipt", Route = "/inventory/goods-receipts" },
                        new NavTreeItem { Code = "INV_MISC_RECEIPT", Name = "Miscellaneous Receipt", Route = "/inventory/misc-receipt" },
                        new NavTreeItem { Code = "INV_STOCK_TRANSFER", Name = "Stock Transfer", Route = "/inventory/stock-transfer" }
                    ]
                }
            ]
        },
        new NavTreeItem
        {
            Code = "SALES",
            Name = "Sales",
            Icon = "fa-solid fa-cart-shopping",
            Children =
            [
                new NavTreeItem
                {
                    Code = "SA_TRANSACTIONS",
                    Name = "Transactions",
                    Children =
                    [
                        new NavTreeItem { Code = "SA_INVOICE", Name = "Sales Invoice", Route = "/sales/invoices" },
                        new NavTreeItem { Code = "SA_SO", Name = "Sales Order", Route = "/sales/orders" }
                    ]
                }
            ]
        }
    ];

    [Fact]
    public void Blank_search_returns_original_menu_reference()
    {
        var menu = SampleMenu();

        Assert.Same(menu, NavMenuTreeFilter.Filter(menu, null));
        Assert.Same(menu, NavMenuTreeFilter.Filter(menu, "   "));
    }

    [Fact]
    public void Leaf_search_keeps_only_matching_branches_and_ancestors()
    {
        var filtered = NavMenuTreeFilter.Filter(SampleMenu(), "receipt");

        var inv = Assert.Single(filtered);
        Assert.Equal("INVENTORY", inv.Code);
        var trx = Assert.Single(inv.Children);
        Assert.Equal("INV_TRANSACTIONS", trx.Code);
        Assert.Equal(["INV_GOODS_RECEIPT", "INV_MISC_RECEIPT"], trx.Children.Select(x => x.Code));
    }

    [Fact]
    public void Group_search_keeps_full_subtree_for_matched_group()
    {
        var filtered = NavMenuTreeFilter.Filter(SampleMenu(), "inventory");

        var inv = Assert.Single(filtered);
        Assert.Equal("INVENTORY", inv.Code);
        Assert.Equal(2, inv.Children.Count);
        Assert.Equal(3, inv.Children[1].Children.Count);
    }

    [Fact]
    public void Multi_token_search_matches_across_ancestor_and_leaf_names()
    {
        var filtered = NavMenuTreeFilter.Filter(SampleMenu(), "inv transfer");

        var inv = Assert.Single(filtered);
        var trx = Assert.Single(inv.Children);
        var leaf = Assert.Single(trx.Children);
        Assert.Equal("INV_STOCK_TRANSFER", leaf.Code);
    }

    [Fact]
    public void Unmatched_search_returns_empty_list()
    {
        var filtered = NavMenuTreeFilter.Filter(SampleMenu(), "nonexistent-module");

        Assert.Empty(filtered);
    }

    [Fact]
    public void HasExpandableNodes_detects_groups_with_children()
    {
        Assert.True(NavMenuTreeFilter.HasExpandableNodes(SampleMenu()));
        Assert.False(NavMenuTreeFilter.HasExpandableNodes([SampleMenu()[0]]));
        Assert.False(NavMenuTreeFilter.HasExpandableNodes(Array.Empty<NavTreeItem>()));
    }

    [Fact]
    public void AreTreesEqual_compares_tree_structure_and_values()
    {
        var a = SampleMenu();
        var b = SampleMenu();

        Assert.True(NavMenuTreeFilter.AreTreesEqual(a, b));

        var modified = new List<NavTreeItem>(b)
        {
            [0] = b[0] with { Name = "Home Dashboard" }
        };

        Assert.False(NavMenuTreeFilter.AreTreesEqual(a, modified));
    }
}
