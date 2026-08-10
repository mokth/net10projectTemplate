using ErpWeb.Core.Menus;

namespace ErpWeb.Tests;

public class MenuExpansionStateTests
{
    private static IReadOnlyList<MenuNavItem> SampleTree() =>
    [
        new MenuNavItem
        {
            Code = "OPERATIONS",
            Name = "Operations",
            Children =
            [
                new MenuNavItem
                {
                    Code = "OVERVIEW",
                    Name = "Overview",
                    Children =
                    [
                        new MenuNavItem { Code = "DASHBOARD", Name = "Dashboard", Route = "/dashboard" }
                    ]
                }
            ]
        },
        new MenuNavItem
        {
            Code = "SECURITY",
            Name = "Security",
            Children =
            [
                new MenuNavItem
                {
                    Code = "ADMIN",
                    Name = "Admin",
                    Children =
                    [
                        new MenuNavItem { Code = "ADMIN_DEMO", Name = "Admin Demo", Route = "/admin-demo" }
                    ]
                }
            ]
        }
    ];

    [Fact]
    public void Active_path_ancestors_are_found()
    {
        var ancestors = MenuExpansionState.FindAncestorGroupCodes(SampleTree(), "/dashboard");
        Assert.Equal(["OPERATIONS", "OVERVIEW"], ancestors);
    }

    [Fact]
    public void ExpandActiveBranch_opens_active_path()
    {
        var state = new MenuExpansionState();
        state.ExpandActiveBranch(MenuExpansionState.FindAncestorGroupCodes(SampleTree(), "/dashboard"));
        Assert.True(state.IsExpanded("OPERATIONS"));
        Assert.True(state.IsExpanded("OVERVIEW"));
    }

    [Fact]
    public void Manual_collapse_is_respected_by_ApplyActivePath()
    {
        var state = new MenuExpansionState();
        var ancestors = MenuExpansionState.FindAncestorGroupCodes(SampleTree(), "/dashboard");
        state.ExpandActiveBranch(ancestors);
        state.Toggle("OPERATIONS"); // collapse + mark user collapsed
        Assert.False(state.IsExpanded("OPERATIONS"));

        state.ApplyActivePath(ancestors);
        Assert.False(state.IsExpanded("OPERATIONS"));
    }

    [Fact]
    public void New_active_branch_expands_even_if_previously_collapsed()
    {
        var state = new MenuExpansionState();
        var adminPath = MenuExpansionState.FindAncestorGroupCodes(SampleTree(), "/admin-demo");
        state.ExpandActiveBranch(adminPath);
        state.Toggle("SECURITY"); // user collapses
        Assert.False(state.IsExpanded("SECURITY"));

        // Navigate again to that branch — ExpandActiveBranch clears collapse and opens path
        state.ExpandActiveBranch(adminPath);
        Assert.True(state.IsExpanded("SECURITY"));
        Assert.True(state.IsExpanded("ADMIN"));
    }

    [Fact]
    public void Group_nodes_have_no_Route_for_navigation()
    {
        var ops = SampleTree()[0];
        Assert.True(ops.Children.Count > 0);
        Assert.True(string.IsNullOrWhiteSpace(ops.Route));
    }

    [Fact]
    public void RoutesMatch_home_aliases()
    {
        Assert.True(MenuExpansionState.RoutesMatch("/home", "/"));
        Assert.True(MenuExpansionState.RoutesMatch("/home", "/home"));
        Assert.False(MenuExpansionState.RoutesMatch("/dashboard", "/home"));
    }
}
