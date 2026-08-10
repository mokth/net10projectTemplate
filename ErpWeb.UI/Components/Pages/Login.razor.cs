using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Pages;

public partial class Login
{
    [SupplyParameterFromQuery(Name = "error")]
    public string? Error { get; set; }

    private string CompanyCode { get; set; } = "DEMO";

    private bool ShowPassword { get; set; }

    private string? ErrorMessage =>
        Error == "1" ? ErpWeb.Core.Services.AuthService.GenericLoginFailure : null;

    private void TogglePassword() => ShowPassword = !ShowPassword;
}
