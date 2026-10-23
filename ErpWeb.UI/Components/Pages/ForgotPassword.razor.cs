using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Pages;

public partial class ForgotPassword
{
    // Bound as a string, not a bool: Blazor parses bool query values with bool.TryParse, which does
    // NOT accept "1", and a failed parse throws from SetParametersAsync during static prerender,
    // producing an unhandled HTTP 500 instead of the confirmation panel. The redirect target in
    // AccountEndpoints is "?submitted=1", so the comparison is explicit and any unexpected value
    // simply falls back to the form.
    [SupplyParameterFromQuery(Name = "submitted")]
    public string? Submitted { get; set; }

    private bool RequestSubmitted => string.Equals(Submitted, "1", StringComparison.Ordinal);
}
