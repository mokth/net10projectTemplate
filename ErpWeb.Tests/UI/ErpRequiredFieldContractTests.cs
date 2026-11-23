using System.Text.RegularExpressions;

namespace ErpWeb.Tests.UI;

/// <summary>
/// Source guards for the required-field visual standard in <c>ErpWeb/wwwroot/css/site.css</c>.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Shared)]
public class ErpRequiredFieldContractTests
{
    private const string SiteCssPath = "ErpWeb/wwwroot/css/site.css";

    [Fact]
    public void Site_Css_Defines_Light_And_Dark_Required_Field_Background_Tokens()
    {
        var css = ReadSource(SiteCssPath);

        var rootBlock = Regex.Match(css, @":root\s*\{(?<body>[\s\S]*?)\}");
        Assert.True(rootBlock.Success, "site.css must define :root tokens.");
        Assert.Contains("--required-field-bg:", rootBlock.Groups["body"].Value, StringComparison.Ordinal);

        var darkBlock = Regex.Match(css, @"\[data-bs-theme=""dark""\]\s*\{(?<body>[\s\S]*?)\}");
        Assert.True(darkBlock.Success, "site.css must define [data-bs-theme=\"dark\"] tokens.");
        Assert.Contains("--required-field-bg:", darkBlock.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Site_Css_Required_Field_Rule_Covers_All_Supported_Inputs()
    {
        var css = ReadSource(SiteCssPath);

        var rule = Regex.Match(css, @"(?<selectors>\.required-field[\s\S]*?)\{(?<body>[^}]*--required-field-bg[^}]*)\}");
        Assert.True(rule.Success, "site.css must define .required-field using var(--required-field-bg).");

        var selectors = rule.Groups["selectors"].Value;
        Assert.Contains(".required-field input", selectors, StringComparison.Ordinal);
        Assert.Contains(".required-field textarea", selectors, StringComparison.Ordinal);
        Assert.Contains(".required-field select", selectors, StringComparison.Ordinal);
        Assert.Contains(".required-field .dxbl-text-edit-input", selectors, StringComparison.Ordinal);
        Assert.Contains(".required-field.dxbl-text-edit .dxbl-text-edit-input", selectors, StringComparison.Ordinal);
        Assert.Contains(".required-field.dxbl-spin-edit .dxbl-text-edit-input", selectors, StringComparison.Ordinal);
        Assert.Contains(".required-field.dxbl-memo .dxbl-text-edit-input", selectors, StringComparison.Ordinal);
    }

    [Fact]
    public void Razor_And_Css_Do_Not_Use_Deprecated_Local_Required_Asterisk_Classes()
    {
        var root = ResolveRepositoryRoot();
        var uiRoot = Path.Combine(root, "ErpWeb.UI");
        string[] deprecatedTokens =
        [
            "iv-field__required",
            "po-supp-req",
            "sa-cust-req",
            "pwo-required",
            "pdf-required"
        ];

        var violations = Directory.EnumerateFiles(uiRoot, "*.*", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                (path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)))
            .SelectMany(path =>
            {
                var content = File.ReadAllText(path);
                return deprecatedTokens
                    .Where(token => content.Contains(token, StringComparison.Ordinal))
                    .Select(token => $"{Path.GetRelativePath(root, path)}: {token}");
            })
            .ToArray();

        Assert.Empty(violations);
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
