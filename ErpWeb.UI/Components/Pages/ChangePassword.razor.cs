using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Pages;

public partial class ChangePassword : PageBase
{
    [SupplyParameterFromQuery(Name = "error")]
    public string? Error { get; set; }

    private bool ShowCurrentPassword { get; set; }

    private bool ShowNewPassword { get; set; }

    private bool ShowConfirmPassword { get; set; }

    private string Heading =>
        CurrentUser.MustChangePassword ? "Set a new password" : "Change password";

    private string Subheading =>
        CurrentUser.MustChangePassword
            ? "Your account requires a password update before you can continue."
            : "Choose a strong password you don’t use elsewhere.";

    private string VisualTagline =>
        CurrentUser.MustChangePassword
            ? "Almost there — update your password to unlock the workspace."
            : "Keep your account secure with a fresh password.";

    protected override Task OnPageInitializedAsync()
    {
        ErrorMessage = Error switch
        {
            "1" => "Unable to change password. Check your current password and try again.",
            "mismatch" => "New password and confirmation do not match.",
            _ => null
        };
        return Task.CompletedTask;
    }

    private void ToggleCurrentPassword() => ShowCurrentPassword = !ShowCurrentPassword;

    private void ToggleNewPassword() => ShowNewPassword = !ShowNewPassword;

    private void ToggleConfirmPassword() => ShowConfirmPassword = !ShowConfirmPassword;
}
