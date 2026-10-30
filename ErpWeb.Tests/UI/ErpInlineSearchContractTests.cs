using System.Text.RegularExpressions;

namespace ErpWeb.Tests.UI;

/// <summary>
/// Source guards for the global inline search standard: <c>Caption [ input ][magnifier]</c> on one
/// non-wrapping row. Runtime visual checks (responsive width, zoom, theme) still apply.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Shared)]
public class ErpInlineSearchContractTests
{
    private const string StylesheetPath = "ErpWeb/wwwroot/css/inventory-chrome.css";

    /// <summary>
    /// Rows that are intentionally outside the single-text-search standard.
    /// Each entry documents why the row keeps its own layout.
    /// </summary>
    private static readonly Dictionary<string, string> DocumentedExceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ErpWeb.UI/Inventory/Inquiry/IvReconciliation.razor"] =
            "Multi-criteria item + warehouse row with a single RUN apply action.",
        ["ErpWeb.UI/Purchase/Inquiry/PoSupplierInquiry.razor"] =
            "Text box filters the adjacent supplier combo option list, not the result grid.",
        ["ErpWeb.UI/Sales/Inquiry/SaCustomerInquiry.razor"] =
            "Text box filters the adjacent customer combo option list, not the result grid.",
        ["ErpWeb.UI/Purchase/Transactions/PoCdnReservations.razor"] =
            "Multi-criteria live filter bar (invoice no + vendor code + over-reserved) with a shared REFRESH apply.",
        ["ErpWeb.UI/Sales/Transactions/SaCdnReservations.razor"] =
            "Multi-criteria live filter bar (invoice no + customer code + over-reserved) with a shared REFRESH apply."
    };

    [Fact]
    public void Shared_Row_Is_Non_Wrapping_With_A_Usable_Input_Floor_And_Visible_Magnifier()
    {
        var css = ReadSource(StylesheetPath);
        var row = RuleBody(css, ".erp-inline-search");
        var editor = RuleBody(css, ".erp-inline-search__editor");
        var button = RuleBody(css, ".erp-inline-search__button");
        var caption = RuleBody(css, ".erp-inline-search__caption");

        Assert.Contains("flex-wrap: nowrap", row, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow-x", row, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("overflow: hidden", row, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("min-inline-size: 8rem", editor, StringComparison.Ordinal);
        Assert.Contains("flex: 1 1", editor, StringComparison.Ordinal);

        Assert.Contains("2.25rem", button, StringComparison.Ordinal);
        Assert.Contains("flex: 0 0 auto", button, StringComparison.Ordinal);

        Assert.Contains("text-overflow: ellipsis", caption, StringComparison.Ordinal);
        Assert.Contains("white-space: nowrap", caption, StringComparison.Ordinal);

        Assert.Contains("min-inline-size: 0", RuleBody(css, ".iv-toolbar-row__search.erp-inline-search"),
            StringComparison.Ordinal);

        // Narrow screens keep the magnifier on screen by shrinking the input floor, never by hiding it.
        var mobile = MediaBlock(css, 575);
        Assert.Contains("min-inline-size: 6rem", mobile, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_Inline_Search_Row_Has_A_Caption_Editor_And_Icon_Only_Magnifier()
    {
        var violations = new List<string>();

        foreach (var (relativePath, source) in EnumerateRazorSources())
        {
            foreach (var (openingTag, block) in InlineSearchBlocks(source))
            {
                var caption = Regex.Match(block,
                    @"<label\s+class=""erp-inline-search__caption""[^>]*for=""(?<for>[^""]+)""");
                if (!caption.Success)
                {
                    violations.Add($"{relativePath}: caption label with for= is missing");
                    continue;
                }

                var textBox = Regex.Match(block, @"<DxTextBox(?<attrs>[\s\S]*?)/>");
                if (!textBox.Success)
                {
                    violations.Add($"{relativePath}: editor has no DxTextBox");
                    continue;
                }

                var inputId = Regex.Match(textBox.Groups["attrs"].Value, @"InputId=""(?<id>[^""]+)""");
                if (!inputId.Success)
                {
                    violations.Add($"{relativePath}: DxTextBox has no InputId");
                }
                else if (!string.Equals(inputId.Groups["id"].Value, caption.Groups["for"].Value, StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}: label for= does not match InputId");
                }

                if (!textBox.Groups["attrs"].Value.Contains("BindValueMode=\"BindValueMode.OnInput\"", StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}: DxTextBox must bind on input so Enter and the magnifier see the typed text");
                }

                var hasText = Regex.IsMatch(textBox.Groups["attrs"].Value, @"(?:^|\s)Text=""");
                var hasChanged = Regex.IsMatch(textBox.Groups["attrs"].Value, @"(?:^|\s)TextChanged=""");
                if (hasText && hasChanged
                    && !textBox.Groups["attrs"].Value.Contains("TextExpression=", StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}: explicit Text + TextChanged requires TextExpression");
                }

                var buttons = Regex.Matches(block, @"<DxButton(?<attrs>[\s\S]*?)/>");

                // A live filter row has no explicit search action, so it must not render an inert button.
                if (openingTag.Contains("erp-inline-search--live", StringComparison.Ordinal))
                {
                    if (buttons.Count != 0)
                    {
                        violations.Add($"{relativePath}: live filter row must not render a search button");
                    }

                    continue;
                }

                if (buttons.Count != 1)
                {
                    violations.Add($"{relativePath}: row must contain exactly one magnifier button");
                    continue;
                }

                var buttonAttrs = buttons[0].Groups["attrs"].Value;
                if (!buttonAttrs.Contains("fa-magnifying-glass", StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}: row button must use the magnifying-glass icon");
                }

                if (Regex.IsMatch(buttonAttrs, "(?:^|\\s)Text=\""))
                {
                    violations.Add($"{relativePath}: magnifier button must be icon-only");
                }

                if (!buttonAttrs.Contains("aria-label=", StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}: magnifier button needs an accessible name");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void No_Single_Text_Toolbar_Search_Is_Left_In_The_Legacy_Shape()
    {
        var violations = new List<string>();

        foreach (var (relativePath, source) in EnumerateRazorSources())
        {
            if (DocumentedExceptions.ContainsKey(relativePath)) continue;

            foreach (var block in LegacyToolbarSearchBlocks(source))
            {
                if (!block.Contains("<DxTextBox", StringComparison.Ordinal)) continue;
                if (block.Contains("erp-inline-search", StringComparison.Ordinal)) continue;
                violations.Add($"{relativePath}: toolbar search text box is not in the inline standard");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void No_Visible_Search_Text_Button_Sits_Next_To_A_Search_Input()
    {
        var violations = new List<string>();

        foreach (var (relativePath, source) in EnumerateRazorSources())
        {
            // Only rows that are actually search rows are constrained: a multi-criteria form with a
            // single shared apply action may keep a text button.
            var rows = LegacyToolbarSearchBlocks(source).Concat(InlineSearchBlocks(source).Select(x => x.Block));
            foreach (var row in rows)
            {
                if (!row.Contains("<DxTextBox", StringComparison.Ordinal)) continue;
                if (!Regex.IsMatch(row, @"Text=""(?:SEARCH|Search)""")) continue;
                violations.Add($"{relativePath}: visible SEARCH text button inside a search row");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Combo_And_Picker_Wrappers_Expose_An_InputId_For_Label_Association()
    {
        var combo = ReadSource("ErpWeb.UI/Inventory/Lookups/IvCodeComboBox.razor");
        Assert.Contains("InputId=\"@InputId\"", combo);
        Assert.Contains("InputId", ReadSource("ErpWeb.UI/Inventory/Lookups/IvCodeComboBox.razor.cs"));

        var smartLookup = ReadSource("ErpWeb.UI/Components/Common/Lookups/SmartLookupInput.razor");
        Assert.Contains("InputId=\"@InputId\"", smartLookup);
        Assert.Contains("fa-solid fa-magnifying-glass", smartLookup);
        Assert.Contains("aria-label=\"@SearchButtonAriaLabel\"", smartLookup);
        Assert.DoesNotContain("Text=\"SEARCH\"", smartLookup);
        Assert.DoesNotContain("Text=\"Search\"", smartLookup);
    }

    [Fact]
    public void Documented_Exceptions_Still_Exist_So_The_Allow_List_Cannot_Rot()
    {
        foreach (var relativePath in DocumentedExceptions.Keys)
        {
            Assert.True(File.Exists(Path.Combine(ResolveRepositoryRoot(), relativePath)),
                $"Documented exception no longer exists: {relativePath}");
        }
    }

    private static IEnumerable<(string RelativePath, string Source)> EnumerateRazorSources()
    {
        var root = ResolveRepositoryRoot();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "ErpWeb.UI"), "*.razor", SearchOption.AllDirectories))
        {
            yield return (Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText(path));
        }
    }

    private static IEnumerable<(string OpeningTag, string Block)> InlineSearchBlocks(string source)
    {
        foreach (Match match in Regex.Matches(source, @"<div class=""[^""]*\berp-inline-search\b[^""]*"">"))
        {
            var end = FindMatchingDivEnd(source, match.Index);
            if (end > match.Index) yield return (match.Value, source.Substring(match.Index, end - match.Index));
        }
    }

    private static IEnumerable<string> LegacyToolbarSearchBlocks(string source)
    {
        foreach (Match match in Regex.Matches(source, @"<div class=""iv-toolbar-row__search"">"))
        {
            var end = FindMatchingDivEnd(source, match.Index);
            if (end > match.Index) yield return source.Substring(match.Index, end - match.Index);
        }
    }

    private static int FindMatchingDivEnd(string source, int start)
    {
        var depth = 0;
        for (var i = start; i < source.Length; i++)
        {
            if (string.CompareOrdinal(source, i, "<div", 0, 4) == 0) { depth++; i += 3; continue; }
            if (string.CompareOrdinal(source, i, "</div>", 0, 6) == 0)
            {
                depth--;
                if (depth == 0) return i + 6;
                i += 5;
            }
        }

        return -1;
    }

    private static string RuleBody(string css, string selector)
    {
        css = Regex.Replace(css, @"/\*[\s\S]*?\*/", string.Empty);
        var match = Regex.Match(css, Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(match.Success, $"Missing rule for {selector}");
        return match.Groups["body"].Value;
    }

    private static string MediaBlock(string css, int maxWidth)
    {
        css = Regex.Replace(css, @"/\*[\s\S]*?\*/", string.Empty);
        var match = Regex.Match(css, @"@media\s*\(max-width:\s*" + maxWidth + @"(?:\.\d+)?px\)\s*\{(?<body>[\s\S]*?)\n\}");
        Assert.True(match.Success, $"Missing @media (max-width: {maxWidth}px) block");
        return match.Groups["body"].Value;
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
