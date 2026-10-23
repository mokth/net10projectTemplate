using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Pages;

public partial class Login
{
    [SupplyParameterFromQuery(Name = "error")]
    public string? Error { get; set; }

    // String, not bool: Blazor binds bool query values through bool.TryParse, which rejects "1" and
    // throws from SetParametersAsync during prerender (HTTP 500). See AuthPageQueryBindingTests.
    [SupplyParameterFromQuery(Name = "reset")]
    public string? Reset { get; set; }

    private string CompanyCode { get; set; } = "DEMO";

    private bool ShowPassword { get; set; }

    private bool PasswordReset => string.Equals(Reset, "1", StringComparison.Ordinal);

    private string? ErrorMessage =>
        Error == "1" ? ErpWeb.Core.Services.AuthService.GenericLoginFailure : null;

    private void TogglePassword() => ShowPassword = !ShowPassword;
}
