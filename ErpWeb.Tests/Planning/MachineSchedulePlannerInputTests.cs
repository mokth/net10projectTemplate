using System.Globalization;
using ErpWeb.UI.Planning.WorkOrders;

namespace ErpWeb.Tests.Planning;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class MachineSchedulePlannerInputTests
{
    [Theory]
    [InlineData("2026-10-09", "08:30", "en-US")]
    [InlineData("09/10/2026", "08:30 AM", "en-GB")]
    public void Requested_start_accepts_html_and_localized_browser_values(
        string dateText,
        string timeText,
        string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);

        var parsed = MachineSchedulePlannerInputParser.TryParseRequestedStart(
            dateText, timeText, culture, out var requestedStart);

        Assert.True(parsed);
        Assert.Equal(new DateTime(2026, 10, 9, 8, 30, 0), requestedStart);
        Assert.Equal(DateTimeKind.Unspecified, requestedStart.Kind);
    }

    [Theory]
    [InlineData("", "08:30")]
    [InlineData("2026-10-09", "not a time")]
    public void Requested_start_rejects_invalid_values(string dateText, string timeText)
    {
        var parsed = MachineSchedulePlannerInputParser.TryParseRequestedStart(
            dateText, timeText, CultureInfo.GetCultureInfo("en-GB"), out _);

        Assert.False(parsed);
    }
}
