using System.Globalization;
using ErpWeb.Core.Purchase;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.UI.Purchase.Masters;

namespace ErpWeb.Tests.Procurement.Master;

[Trait(TestCategories.Name, TestCategories.Purchase)]
[Trait(TestCategories.Name, TestCategories.PurchaseMasters)]
public class PoApprovalSettingsDisplayTests
{
    [Theory]
    [InlineData("0", "1000", "Below MYR 1,000.00")]
    [InlineData("1000", null, "MYR 1,000.00 and above")]
    [InlineData("1000", "5000", "MYR 1,000.00 to below 5,000.00")]
    [InlineData("0", null, "MYR 0.00 and above")]
    [InlineData("999.99", "1000", "MYR 999.99 to below 1,000.00")]
    [InlineData("1000.001", "2000.5", "MYR 1,000.001 to below 2,000.50")]
    [InlineData("0", "999.999", "Below MYR 999.999")]
    public void FormatBandRange_uses_below_not_subtracted_inclusive_bound(string minText, string? maxText, string expected)
    {
        var min = decimal.Parse(minText, CultureInfo.InvariantCulture);
        decimal? exclusive = maxText is null ? null : decimal.Parse(maxText, CultureInfo.InvariantCulture);
        Assert.Equal(expected, PoApprovalSettings.FormatBandRange("myr", min, exclusive));
    }

    [Fact]
    public void FormatAmount_keeps_significant_fractional_digits()
    {
        Assert.Equal("1,000.00", PoApprovalSettings.FormatAmount(1000m));
        Assert.Equal("999.999", PoApprovalSettings.FormatAmount(999.999m));
        Assert.Equal("1,000.001", PoApprovalSettings.FormatAmount(1000.001m));
    }

    [Fact]
    public void FormatPreview_is_labeled_preview_only()
    {
        var editor = new PoApprovalSettings.PolicyEditor
        {
            Bands =
            [
                new PoApprovalSettings.BandEditor
                {
                    Currency = "MYR",
                    MinInclusive = 0m,
                    MaxExclusive = 1000m,
                    NotRequired = true
                },
                new PoApprovalSettings.BandEditor
                {
                    Currency = "MYR",
                    MinInclusive = 1000m,
                    Levels =
                    [
                        new PoApprovalSettings.LevelEditor { Sequence = 1 },
                        new PoApprovalSettings.LevelEditor { Sequence = 2 }
                    ]
                }
            ]
        };

        Assert.Equal("Preview only (not the live result): MYR 5,000.00 → Step 1 then Step 2", PoApprovalSettings.FormatPreview(editor));
    }

    [Fact]
    public void CollectIssues_reports_overlap_gap_and_missing_approver_without_changing_values()
    {
        var editor = new PoApprovalSettings.PolicyEditor
        {
            Enabled = true,
            Bands =
            [
                new PoApprovalSettings.BandEditor
                {
                    Currency = "MYR",
                    MinInclusive = 0m,
                    MaxExclusive = 1000m,
                    NotRequired = true
                },
                new PoApprovalSettings.BandEditor
                {
                    Currency = "MYR",
                    MinInclusive = 1500m,
                    Levels = [new PoApprovalSettings.LevelEditor()]
                }
            ]
        };

        var issues = PoApprovalSettings.CollectIssues(editor);
        Assert.Contains(issues, issue => issue.Message.Contains("gap or overlap", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Message.Contains("needs at least one person", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0m, editor.Bands[0].MinInclusive);
        Assert.Equal(1000m, editor.Bands[0].MaxExclusive);
        Assert.Equal(1500m, editor.Bands[1].MinInclusive);
    }

    [Fact]
    public void CollectIssues_allows_empty_rules_when_approval_is_off()
    {
        var editor = new PoApprovalSettings.PolicyEditor { Enabled = false };
        Assert.Empty(PoApprovalSettings.CollectIssues(editor));
    }

    [Fact]
    public void CollectIssues_requires_a_rule_when_approval_is_on()
    {
        var editor = new PoApprovalSettings.PolicyEditor { Enabled = true };
        var issues = PoApprovalSettings.CollectIssues(editor);
        Assert.Contains(issues, issue => issue.Message.Contains("Add at least one approval rule", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BandHasSignificantData_ignores_a_blank_starter_rule()
    {
        var blank = new PoApprovalSettings.BandEditor
        {
            Currency = "MYR",
            Levels = [new PoApprovalSettings.LevelEditor()]
        };
        var filled = new PoApprovalSettings.BandEditor
        {
            Currency = "MYR",
            MaxExclusive = 1000m,
            Levels =
            [
                new PoApprovalSettings.LevelEditor
                {
                    Assignees = [new ProcurementApprovalLevelAssigneeDto { Kind = "USER", RefCode = "admin" }]
                }
            ]
        };

        Assert.False(PoApprovalSettings.BandHasSignificantData(blank));
        Assert.True(PoApprovalSettings.BandHasSignificantData(filled));
    }

    [Fact]
    public void ApplyRequirement_keeps_hidden_levels_and_creates_one_blank_step_when_needed()
    {
        var withAssignees = new PoApprovalSettings.BandEditor
        {
            Levels =
            [
                new PoApprovalSettings.LevelEditor
                {
                    Assignees = [new ProcurementApprovalLevelAssigneeDto { Kind = "USER", RefCode = "mgr" }]
                }
            ]
        };

        PoApprovalSettings.ApplyRequirement(withAssignees, notRequired: true);
        Assert.True(withAssignees.NotRequired);
        Assert.Single(withAssignees.Levels);
        Assert.Equal("mgr", withAssignees.Levels[0].Assignees[0].RefCode);

        PoApprovalSettings.ApplyRequirement(withAssignees, notRequired: false);
        Assert.False(withAssignees.NotRequired);
        Assert.Single(withAssignees.Levels);

        var empty = new PoApprovalSettings.BandEditor();
        PoApprovalSettings.ApplyRequirement(empty, notRequired: false);
        Assert.Single(empty.Levels);
        Assert.Empty(empty.Levels[0].Assignees);
    }

    [Fact]
    public void ToDto_keeps_levels_when_no_approval_is_needed()
    {
        var editor = new PoApprovalSettings.PolicyEditor
        {
            DocType = ProcurementApprovalDocTypes.Po,
            Bands =
            [
                new PoApprovalSettings.BandEditor
                {
                    Currency = "MYR",
                    NotRequired = true,
                    Levels =
                    [
                        new PoApprovalSettings.LevelEditor
                        {
                            MatchMode = ProcurementApprovalMatchModes.All,
                            Assignees =
                            [
                                new ProcurementApprovalLevelAssigneeDto
                                {
                                    Kind = ProcurementApprovalAssigneeKinds.Role,
                                    RefCode = "PURCHASE"
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var dto = PoApprovalSettings.ToDto(editor);
        Assert.True(dto.Bands[0].NotRequired);
        Assert.Single(dto.Bands[0].Levels);
        Assert.Equal(ProcurementApprovalMatchModes.All, dto.Bands[0].Levels[0].MatchMode);
        Assert.Equal("PURCHASE", dto.Bands[0].Levels[0].Assignees[0].RefCode);
    }

    [Fact]
    public void ToDto_carries_version_and_rowversion()
    {
        var rowVersion = new byte[] { 1, 2, 3, 4 };
        var editor = PoApprovalSettings.FromDto(new ProcurementApprovalPolicyDto
        {
            DocType = ProcurementApprovalDocTypes.Pr,
            Version = 7,
            RowVersion = rowVersion,
            Enabled = true,
            Bands =
            [
                new ProcurementApprovalBandDto
                {
                    Currency = "MYR",
                    NotRequired = true
                }
            ]
        });

        var dto = PoApprovalSettings.ToDto(editor);
        Assert.Equal(7, dto.Version);
        Assert.Equal(rowVersion, dto.RowVersion);
        Assert.True(dto.Enabled);
    }

    [Theory]
    [InlineData("Enable approval only after amount bands are configured.", "Add at least one approval rule before turning approval on.")]
    [InlineData("Each approval level must have at least one user or role.", "Choose at least one person or role for every approval step.")]
    [InlineData("Each amount band upper limit must be greater than its lower limit.", "‘Amount below’ must be greater than ‘Amount from’.")]
    [InlineData("Currency MYR has a gap or overlap in amount bands.", "The MYR amount ranges leave a gap or overlap. Please check where one rule ends and the next begins.")]
    [InlineData("Currency USD must include a band starting at 0.00.", "The USD rules must include an amount starting at 0.")]
    [InlineData("These approval settings were changed by another user. Reload and try again.", "Someone else saved these approval settings. Reload and try again.")]
    public void MapServerError_translates_known_messages(string source, string expected)
    {
        Assert.Equal(expected, PoApprovalSettings.MapServerError(source));
    }

    [Fact]
    public void MapServerError_keeps_unknown_messages()
    {
        const string original = "Not authorized.";
        Assert.Equal(original, PoApprovalSettings.MapServerError(original));
    }
}
