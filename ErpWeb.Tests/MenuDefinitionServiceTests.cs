using ErpWeb.Core.Menus;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ErpWeb.Tests;

public class MenuDefinitionServiceTests
{
    [Fact]
    public void Parse_depth_3_parent_chain()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Menus>
              <Menu Code="OPERATIONS" Name="Operations" SortOrder="1">
                <Menu Code="OVERVIEW" Name="Overview" SortOrder="1">
                  <Menu Code="DASHBOARD" Name="Dashboard" Route="/dashboard" SortOrder="1" />
                </Menu>
              </Menu>
            </Menus>
            """;

        using var env = new TempHostEnvironment(xml);
        var sut = CreateSut(env);

        Assert.Empty(sut.Validate());
        var flat = sut.GetFlatByCode();
        Assert.Equal("OVERVIEW", flat["DASHBOARD"].ParentCode);
        Assert.Equal("OPERATIONS", flat["OVERVIEW"].ParentCode);
        Assert.Null(flat["OPERATIONS"].ParentCode);
        Assert.True(flat["OPERATIONS"].IsGroup);
        Assert.True(flat["DASHBOARD"].IsLeaf);
    }

    [Fact]
    public void Parse_depth_4_without_code_changes()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Menus>
              <Menu Code="A" Name="A" SortOrder="1">
                <Menu Code="B" Name="B" SortOrder="1">
                  <Menu Code="C" Name="C" SortOrder="1">
                    <Menu Code="D" Name="D" Route="/d" SortOrder="1" />
                  </Menu>
                </Menu>
              </Menu>
            </Menus>
            """;

        using var env = new TempHostEnvironment(xml);
        var sut = CreateSut(env);
        Assert.Empty(sut.Validate());
        var flat = sut.GetFlatByCode();
        Assert.Equal("C", flat["D"].ParentCode);
        Assert.Equal("B", flat["C"].ParentCode);
        Assert.Equal("A", flat["B"].ParentCode);
    }

    [Fact]
    public void Detects_duplicate_MenuCode()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Menus>
              <Menu Code="HOME" Name="Home" Route="/home" SortOrder="1" />
              <Menu Code="OPERATIONS" Name="Operations" SortOrder="2">
                <Menu Code="HOME" Name="Home2" Route="/home2" SortOrder="1" />
              </Menu>
            </Menus>
            """;

        using var env = new TempHostEnvironment(xml);
        var errors = CreateSut(env).Validate();
        Assert.Contains(errors, e => e.Contains("Duplicate MenuCode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_group_with_Route()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Menus>
              <Menu Code="OPERATIONS" Name="Operations" Route="/operations" SortOrder="1">
                <Menu Code="DASHBOARD" Name="Dashboard" Route="/dashboard" SortOrder="1" />
              </Menu>
            </Menus>
            """;

        using var env = new TempHostEnvironment(xml);
        var errors = CreateSut(env).Validate();
        Assert.Contains(errors, e => e.Contains("Group menu cannot have Route", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_leaf_without_Route()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Menus>
              <Menu Code="DASHBOARD" Name="Dashboard" SortOrder="1" />
            </Menus>
            """;

        using var env = new TempHostEnvironment(xml);
        var errors = CreateSut(env).Validate();
        Assert.Contains(errors, e => e.Contains("Leaf menu requires Route", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_duplicate_leaf_Route()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Menus>
              <Menu Code="A" Name="A" Route="/dashboard" SortOrder="1" />
              <Menu Code="B" Name="B" Route="/dashboard" SortOrder="2" />
            </Menus>
            """;

        using var env = new TempHostEnvironment(xml);
        var errors = CreateSut(env).Validate();
        Assert.Contains(errors, e => e.Contains("Duplicate Route", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Classifies_group_and_leaf()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Menus>
              <Menu Code="G" Name="G" SortOrder="1">
                <Menu Code="L" Name="L" Route="/l" SortOrder="1" />
              </Menu>
            </Menus>
            """;

        using var env = new TempHostEnvironment(xml);
        var sut = CreateSut(env);
        Assert.Empty(sut.Validate());
        Assert.True(sut.GetFlatByCode()["G"].IsGroup);
        Assert.False(sut.GetFlatByCode()["G"].IsLeaf);
        Assert.True(sut.GetFlatByCode()["L"].IsLeaf);
        Assert.False(sut.GetFlatByCode()["L"].IsGroup);
    }

    private static MenuDefinitionService CreateSut(TempHostEnvironment env) =>
        new(
            Options.Create(new MenusOptions { XmlPath = "menus.xml" }),
            env,
            NullLogger<MenuDefinitionService>.Instance);

    private sealed class TempHostEnvironment : IHostEnvironment, IDisposable
    {
        private readonly string _dir;

        public TempHostEnvironment(string xml)
        {
            _dir = Path.Combine(Path.GetTempPath(), "erpweb-menus-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "menus.xml"), xml);
            ContentRootPath = _dir;
            ApplicationName = "ErpWeb.Tests";
            EnvironmentName = "Development";
            ContentRootFileProvider = new PhysicalFileProvider(_dir);
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}
