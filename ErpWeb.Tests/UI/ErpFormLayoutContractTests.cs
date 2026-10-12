using System.Text.RegularExpressions;

namespace ErpWeb.Tests.UI;

/// <summary>Source guards for the opt-in migration; runtime visual checks are still required.</summary>
[Trait(TestCategories.Name, TestCategories.Shared)]
public class ErpFormLayoutContractTests
{
    private static readonly string[] Sizes = ["xs", "sm", "md", "lg", "xl", "full"];

    [Fact]
    public void Shared_Stylesheet_Is_Registered_Once_Before_Isolated_Styles()
    {
        var app = ReadSource("ErpWeb/Components/App.razor");
        const string stylesheet = "css/erp-form-layout.css";
        Assert.Single(Regex.Matches(app, Regex.Escape(stylesheet)));
        Assert.Contains("href=\"@Assets[\"" + stylesheet + "\"]\"", app);
        Assert.True(app.IndexOf("css/inventory-chrome.css", StringComparison.Ordinal)
            < app.IndexOf(stylesheet, StringComparison.Ordinal));
        Assert.True(app.IndexOf(stylesheet, StringComparison.Ordinal)
            < app.IndexOf("ErpWeb.styles.css", StringComparison.Ordinal));
        Assert.True(app.IndexOf(stylesheet, StringComparison.Ordinal)
            < app.IndexOf("_content/ErpWeb.UI/ErpWeb.UI.styles.css", StringComparison.Ordinal));
    }

    [Fact]
    public void Shared_Stylesheet_Defines_All_Sizes_And_Requires_Opt_In_For_Every_Selector()
    {
        var css = ReadSource("ErpWeb/wwwroot/css/erp-form-layout.css");
        Assert.Contains(".erp-form-scope", css);
        foreach (var size in Sizes)
            Assert.Contains($".erp-form-control--{size}", css);

        var selectors = RuleSelectors(css).ToArray();
        Assert.NotEmpty(selectors);
        foreach (var selector in selectors)
            Assert.True(Regex.IsMatch(selector, @"^\.erp-form-scope(?=[\s.:#\[>+~]|$)"),
                $"Shared form rule must start at the explicit opt-in scope: {selector}");

        Assert.DoesNotContain("!important", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".w-100", css);
        Assert.DoesNotContain(".dxbl-", css);
    }

    [Fact]
    public void Compact_Sizes_Expand_At_The_Shared_Mobile_Breakpoint()
    {
        var css = ReadSource("ErpWeb/wwwroot/css/erp-form-layout.css");
        var mobile = Regex.Match(css, @"@media\s*\(max-width:\s*767px\)\s*\{(?<body>[\s\S]+)\}\s*$");
        Assert.True(mobile.Success, "Compact controls need the shared mobile fallback.");
        var reset = Regex.Match(mobile.Groups["body"].Value,
            @"(?<selectors>[^{}]+)\{\s*max-inline-size:\s*none;\s*\}");
        Assert.True(reset.Success, "Mobile sizing must remove the desktop cap.");
        foreach (var size in Sizes.Where(size => size != "full"))
            Assert.Contains($".erp-form-scope .erp-form-control--{size}", reset.Groups["selectors"].Value);
    }

    [Fact]
    public void Every_Razor_Artifact_Using_Semantic_Widths_Declares_A_Scope()
    {
        var root = ResolveRepositoryRoot();
        var violations = Directory.EnumerateFiles(Path.Combine(root, "ErpWeb.UI"), "*.razor", SearchOption.AllDirectories)
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("erp-form-control--", StringComparison.Ordinal)
                    && !source.Contains("erp-form-scope", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(root, path));
        Assert.Empty(violations);
    }

    [Fact]
    public void Admin_Settings_Canary_Uses_Scoped_Semantic_Widths()
    {
        var razor = ReadSource("ErpWeb.UI/Admin/AdminSettings.razor");
        Assert.Contains("as-editor__grid erp-form-scope erp-form-row", razor);
        Assert.Contains("w-100 erp-form-control--md", razor);
        Assert.Contains("w-100 erp-form-control--sm", razor);
        Assert.Contains("w-100 erp-form-control--lg", razor);
        Assert.Contains("w-100 erp-form-control--full", razor);
    }

    // The controlled stylesheet contains ordinary rules and media blocks only.
    // Inspect every comma-separated selector, including rules nested inside media blocks.
    private static IEnumerable<string> RuleSelectors(string css)
    {
        css = Regex.Replace(css, @"/\*[\s\S]*?\*/", "");
        return Regex.Matches(css, @"(?<selector>[^{}]+)\{")
            .Select(match => match.Groups["selector"].Value.Trim())
            .Where(selector => !selector.StartsWith("@media", StringComparison.Ordinal))
            .SelectMany(selector => selector.Split(','))
            .Select(selector => selector.Trim());
    }

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), relativePath));

    private static string ResolveRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }.Distinct())
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ErpWeb.slnx")))
                    return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root containing ErpWeb.slnx.");
    }
}
