using System.Xml.Linq;
using ErpWeb.Core.Admin;
using ErpWeb.Core.Menus;

namespace ErpWeb.Tests.Admin;

/// <summary>
/// Guards the server-owned placement of the per-family lookup maintenance screens: the registry is the
/// only source of the menu code and route, and <c>ErpWeb/Menus/menus.xml</c> must agree with it.
///
/// <para>
/// This is deliberately a metadata test, not a UI test: a family whose menu code or route drifts from
/// the XML either disappears from navigation (MenuSyncService soft-disables the row) or silently points
/// at another family's screen. Neither is visible to the compiler.
/// </para>
/// </summary>
[Trait(TestCategories.Name, TestCategories.Admin)]
public class AdLookupTypeRegistryTests
{
    private static readonly (string Code, string Parent, string Route, int Sort)[] ApprovedMenus =
    [
        ("INV_WAREHOUSE_TYPE", "INV_MASTER", "/inventory/lookups/WAREHOUSE_TYPE", 8),
        ("PO_SUPPLIER_TYPE", "PO_MASTER", "/purchase/lookups/SUPPLIER_TYPE", 7),
        ("PO_SUPPLIER_SUBGROUP", "PO_MASTER", "/purchase/lookups/SUPPLIER_SUBGROUP", 8),
        ("PLN_MACHINE_TYPE", "PLN_MASTER", "/planning/lookups/MACHINE_TYPE", 16),
        ("PLN_WORK_CENTRE_CLASS", "PLN_MASTER", "/planning/lookups/WORK_CENTRE_CLASS", 17)
    ];

    [Fact]
    public void EveryMaintainableFamily_HasAUniqueMenuCodeAndRoute()
    {
        var maintainable = AdLookupTypeRegistry.Maintainable;

        Assert.Equal(ApprovedMenus.Length, maintainable.Count);
        Assert.Equal(
            ApprovedMenus.Length,
            maintainable.Select(x => x.MaintenanceMenuCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            ApprovedMenus.Length,
            maintainable.Select(x => x.Route).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var definition in maintainable)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(definition.MaintenanceMenuCode),
                $"{definition.Code} is maintainable but has no MaintenanceMenuCode.");
            Assert.False(
                string.IsNullOrWhiteSpace(definition.Route),
                $"{definition.Code} is maintainable but has no Route.");

            var expectedPrefix = $"/{definition.Module.ToLowerInvariant()}/lookups/";
            Assert.StartsWith(expectedPrefix, definition.Route, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(definition.Code, definition.Route, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MenuConstants_MatchTheApprovedLiterals()
    {
        Assert.Equal("INV_WAREHOUSE_TYPE", MenuCodes.InventoryWarehouseType);
        Assert.Equal("PLN_MACHINE_TYPE", MenuCodes.PlanningMachineType);
        Assert.Equal("PLN_WORK_CENTRE_CLASS", MenuCodes.PlanningWorkCentreClass);
        Assert.Equal("PO_SUPPLIER_TYPE", MenuCodes.PurchaseSupplierType);
        Assert.Equal("PO_SUPPLIER_SUBGROUP", MenuCodes.PurchaseSupplierSubGroup);
    }

    [Fact]
    public void ShippedMenusXml_ContainsTheFiveEntries_WithTheApprovedPlacement()
    {
        var menus = LoadShippedMenus();

        foreach (var (code, parent, route, sort) in ApprovedMenus)
        {
            var node = Assert.Single(menus.Where(m => (string?)m.Attribute("Code") == code));

            Assert.Equal(route, (string?)node.Attribute("Route"));
            Assert.Equal(sort, (int?)node.Attribute("SortOrder"));
            Assert.NotNull(node.Parent);
            Assert.Equal(parent, (string?)node.Parent!.Attribute("Code"));
        }
    }

    [Fact]
    public void ShippedMenusXml_HasNoDuplicateCodeOrRoute()
    {
        var menus = LoadShippedMenus();

        var duplicateCodes = menus
            .GroupBy(m => (string?)m.Attribute("Code"), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key is not null && g.Count() > 1)
            .Select(g => g.Key!)
            .ToList();
        Assert.Empty(duplicateCodes);

        var duplicateRoutes = menus
            .Select(m => (string?)m.Attribute("Route"))
            .Where(route => !string.IsNullOrWhiteSpace(route))
            .GroupBy(route => route!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(duplicateRoutes);
    }

    [Fact]
    public void ShippedMenusXml_AgreesWithTheRegistryPlacement()
    {
        var menus = LoadShippedMenus();

        foreach (var definition in AdLookupTypeRegistry.Maintainable)
        {
            var node = Assert.Single(menus.Where(m => (string?)m.Attribute("Code") == definition.MaintenanceMenuCode));

            Assert.Equal(definition.Route, (string?)node.Attribute("Route"));
            Assert.True((bool?)node.Attribute("AlwaysVisible") is not true, $"{definition.Code} must not be AlwaysVisible.");
        }
    }

    [Fact]
    public void ReservedCustomerFamilies_RemainNonMaintainable_WithoutPlacement()
    {
        foreach (var code in new[]
                 {
                     AdLookupTypes.CustomerIndustry,
                     AdLookupTypes.CustomerChannel,
                     AdLookupTypes.CustomerSource
                 })
        {
            var definition = AdLookupTypeRegistry.Find(code);

            Assert.NotNull(definition);
            Assert.False(definition!.UserMaintainable);
            Assert.Null(definition.MaintenanceMenuCode);
            Assert.Null(definition.Route);
        }
    }

    private static List<XElement> LoadShippedMenus() =>
        XDocument
            .Load(Path.Combine(RepoRoot(), "ErpWeb", "Menus", "menus.xml"))
            .Descendants("Menu")
            .ToList();

    /// <summary>
    /// Locates the repository root by walking up from the test output folder, so this reads the
    /// <b>shipped</b> file rather than a copy.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ErpWeb", "Menus", "menus.xml")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate ErpWeb/Menus/menus.xml by walking up from '{AppContext.BaseDirectory}'.");
    }
}
