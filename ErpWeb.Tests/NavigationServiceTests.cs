using ErpWeb.Core.Menus;
using Moq;

namespace ErpWeb.Tests;

public class NavigationServiceTests
{
    [Fact]
    public async Task Denied_leaf_hidden()
    {
        var tree = new List<MenuNavItem>
        {
            Leaf("DASHBOARD", "/dashboard")
        };

        var sut = CreateSut(tree, code => false);
        Assert.Empty(await sut.GetSidebarAsync());
    }

    [Fact]
    public async Task Mixed_authorization_keeps_allowed_leaf_and_parents()
    {
        var tree = new List<MenuNavItem>
        {
            Group("OPERATIONS",
                Group("OVERVIEW",
                    Leaf("DASHBOARD", "/dashboard"),
                    Leaf("INVENTORY_DEMO", "/inventory-demo")))
        };

        var sut = CreateSut(tree, code => code == "DASHBOARD");
        var sidebar = await sut.GetSidebarAsync();

        Assert.Single(sidebar);
        Assert.Equal("OPERATIONS", sidebar[0].Code);
        Assert.Single(sidebar[0].Children);
        Assert.Equal("OVERVIEW", sidebar[0].Children[0].Code);
        Assert.Single(sidebar[0].Children[0].Children);
        Assert.Equal("DASHBOARD", sidebar[0].Children[0].Children[0].Code);
    }

    [Fact]
    public async Task Empty_mid_level_and_grandparent_hidden()
    {
        var tree = new List<MenuNavItem>
        {
            Group("OPERATIONS",
                Group("OVERVIEW",
                    Leaf("A", "/a"),
                    Leaf("B", "/b")))
        };

        var sut = CreateSut(tree, _ => false);
        Assert.Empty(await sut.GetSidebarAsync());
    }

    [Fact]
    public async Task Deep_recursive_pruning_four_levels()
    {
        var tree = new List<MenuNavItem>
        {
            Group("A",
                Group("B",
                    Group("C",
                        Leaf("D", "/d"),
                        Leaf("E", "/e"))))
        };

        var sut = CreateSut(tree, code => code == "D");
        var sidebar = await sut.GetSidebarAsync();
        Assert.Single(sidebar);
        Assert.Equal("A", sidebar[0].Code);
        Assert.Equal("D", sidebar[0].Children[0].Children[0].Children[0].Code);
        Assert.DoesNotContain(sidebar[0].Children[0].Children[0].Children, c => c.Code == "E");
    }

    [Fact]
    public async Task AlwaysVisible_leaf_kept()
    {
        var tree = new List<MenuNavItem>
        {
            new()
            {
                Code = "HOME",
                Name = "Home",
                Route = "/home",
                AlwaysVisible = true,
                SortOrder = 1
            }
        };

        var sut = CreateSut(tree, _ => false);
        var sidebar = await sut.GetSidebarAsync();
        Assert.Single(sidebar);
        Assert.Equal("HOME", sidebar[0].Code);
    }

    [Fact]
    public async Task Shared_tree_not_mutated_when_filtering_differs_per_user()
    {
        var shared = new List<MenuNavItem>
        {
            Group("OPERATIONS",
                Group("OVERVIEW",
                    Leaf("DASHBOARD", "/dashboard"),
                    Leaf("INVENTORY_DEMO", "/inventory-demo")))
        };

        var userA = CreateSut(shared, code => code == "DASHBOARD");
        var userB = CreateSut(shared, code => code == "INVENTORY_DEMO");

        var sidebarA = await userA.GetSidebarAsync();
        var sidebarB = await userB.GetSidebarAsync();

        Assert.Equal("DASHBOARD", sidebarA[0].Children[0].Children[0].Code);
        Assert.Equal("INVENTORY_DEMO", sidebarB[0].Children[0].Children[0].Code);

        // Shared source tree remains complete for both users.
        Assert.Equal(2, shared[0].Children[0].Children.Count);
        Assert.Contains(shared[0].Children[0].Children, c => c.Code == "DASHBOARD");
        Assert.Contains(shared[0].Children[0].Children, c => c.Code == "INVENTORY_DEMO");
    }

    private static NavigationService CreateSut(IReadOnlyList<MenuNavItem> tree, Func<string, bool> canAccess)
    {
        var menus = new Mock<IMenuService>();
        menus.Setup(m => m.GetActiveTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(tree);
        var access = new Mock<IAccessRightService>();
        access.Setup(a => a.CanAccessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string code, CancellationToken _) => canAccess(code));
        return new NavigationService(menus.Object, access.Object);
    }

    private static MenuNavItem Leaf(string code, string route) => new()
    {
        Code = code,
        Name = code,
        Route = route,
        SortOrder = 1
    };

    private static MenuNavItem Group(string code, params MenuNavItem[] children) => new()
    {
        Code = code,
        Name = code,
        SortOrder = 1,
        Children = children
    };
}
