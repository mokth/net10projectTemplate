using System.Reflection;
using ErpWeb.Core.Menus;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ErpWeb.Tests;

/// <summary>
/// Guards the deployment coupling between <see cref="MenuCodes"/>, <c>ErpWeb/Menus/menus.xml</c> and
/// the startup menu sync.
///
/// <c>MenuSyncService</c> runs at startup and soft-disables every <c>dbo.Menu</c> row whose
/// <c>MenuCode</c> is absent from <c>menus.xml</c>, and <c>AccessRightService</c> filters the
/// permission cache on <c>menu.IsActive</c>. A code that exists as a constant and is even seeded
/// into the database by a script — but is missing from the XML — therefore vanishes from navigation
/// and its pages redirect to <c>/unauthorized</c>.
///
/// That failure is invisible to the compiler and to any test that only uses synthetic XML, which is
/// exactly how the PoCdn menu entries were first shipped. It is asserted here instead.
/// </summary>
public class MenuDeploymentParityTests
{
    [Fact]
    public void The_shipped_menus_xml_is_valid()
    {
        Assert.Empty(CreateSut().Validate());
    }

    [Fact]
    public void Every_declared_menu_code_is_present_in_the_shipped_menus_xml()
    {
        var defined = CreateSut().GetFlatByCode();

        var declared = typeof(MenuCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (f.Name, Code: (string)f.GetRawConstantValue()!))
            // Obsolete aliases share a value with the canonical constant; de-duplicate by value.
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var missing = declared
            .Where(x => !defined.ContainsKey(x.Code))
            .Select(x => $"{x.Name} ({x.Code})")
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These MenuCodes are declared but absent from ErpWeb/Menus/menus.xml. MenuSyncService will "
            + "soft-disable their dbo.Menu rows on the next startup, and AccessRightService will hide "
            + "them from navigation and lock out their pages: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Locates the repository root by walking up from the test output folder, so the tests read the
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

    private static MenuDefinitionService CreateSut() =>
        new(
            Options.Create(new MenusOptions { XmlPath = "Menus/menus.xml" }),
            new StubHostEnvironment(Path.Combine(RepoRoot(), "ErpWeb")),
            NullLogger<MenuDefinitionService>.Instance);

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public StubHostEnvironment(string contentRootPath) => ContentRootPath = contentRootPath;

        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "ErpWeb.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
