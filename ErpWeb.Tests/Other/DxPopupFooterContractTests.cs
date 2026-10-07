using System.Text.RegularExpressions;

namespace ErpWeb.Tests.Other;

/// <summary>
/// Source-level contract: every DxPopup that declares a footer template must explicitly
/// set ShowFooter="true". Without it, DevExpress hides the footer and action buttons
/// (Cancel/Delete/Post/Rollback/Apply) become invisible at runtime.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Shared)]
public class DxPopupFooterContractTests
{
    private static readonly Regex HeaderTextRegex = new(
        @"HeaderText\s*=\s*""([^""]+)""",
        RegexOptions.Compiled);

    [Fact]
    public void Every_DxPopup_With_FooterTemplate_Must_Show_Footer()
    {
        var repoRoot = ResolveRepositoryRoot();
        var uiRoot = Path.Combine(repoRoot, "ErpWeb.UI");
        Assert.True(Directory.Exists(uiRoot), $"Expected UI project at '{uiRoot}'.");

        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(uiRoot, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            if (!text.Contains("FooterTemplate", StringComparison.Ordinal)
                && !text.Contains("FooterContentTemplate", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
            var parts = Regex.Split(text, @"(?=<DxPopup\b)");

            foreach (var part in parts)
            {
                if (!part.StartsWith("<DxPopup", StringComparison.Ordinal))
                    continue;

                var hasFooterTemplate = part.Contains("<FooterTemplate>", StringComparison.Ordinal)
                    || part.Contains("<FooterContentTemplate>", StringComparison.Ordinal);
                if (!hasFooterTemplate)
                    continue;

                var openEnd = part.IndexOf('>');
                var opening = openEnd >= 0 ? part[..(openEnd + 1)] : part;
                if (HasShowFooterTrue(opening))
                    continue;

                var headerMatch = HeaderTextRegex.Match(opening);
                var header = headerMatch.Success ? headerMatch.Groups[1].Value : "(no HeaderText)";
                var footerKind = part.Contains("<FooterContentTemplate>", StringComparison.Ordinal)
                    ? "FooterContentTemplate"
                    : "FooterTemplate";

                violations.Add(
                    $"DxPopup footer contract violation:{Environment.NewLine}" +
                    $"{relative}{Environment.NewLine}" +
                    $"Header: {header}{Environment.NewLine}" +
                    $"{footerKind} exists but ShowFooter=\"true\" is missing.");
            }
        }

        Assert.True(
            violations.Count == 0,
            string.Join($"{Environment.NewLine}{Environment.NewLine}", violations));
    }

    private static bool HasShowFooterTrue(string openingTag) =>
        Regex.IsMatch(openingTag, @"ShowFooter\s*=\s*""true""", RegexOptions.IgnoreCase)
        || Regex.IsMatch(openingTag, @"ShowFooter\s*=\s*'true'", RegexOptions.IgnoreCase)
        || Regex.IsMatch(openingTag, @"ShowFooter\s*=\s*true\b", RegexOptions.IgnoreCase);

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

        throw new DirectoryNotFoundException(
            $"Unable to locate the repository root containing ErpWeb.slnx (started from '{AppContext.BaseDirectory}').");
    }
}
