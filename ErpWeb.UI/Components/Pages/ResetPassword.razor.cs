using ErpWeb.Core.Security;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Pages;

/// <summary>
/// Landing page for an emailed password-reset link (<c>/reset-password?token=...</c>).
///
/// <para>
/// Both query parameters are bound as strings: Blazor parses <see cref="bool"/> query values with
/// <c>bool.TryParse</c>, which rejects <c>"1"</c> and throws from <c>SetParametersAsync</c> during
/// static prerender, turning the page into an HTTP 500. See <c>AuthPageQueryBindingTests</c>.
/// </para>
///
/// <para>
/// The token is only forwarded to the POST body. The page never validates it beyond a shape check —
/// validity, expiry and single-use are decided server-side in <c>IPasswordRecoveryService.RedeemAsync</c>.
/// </para>
/// </summary>
public partial class ResetPassword
{
    [SupplyParameterFromQuery(Name = "token")]
    public string? Token { get; set; }

    [SupplyParameterFromQuery(Name = "error")]
    public string? Error { get; set; }

    private bool ShowNewPassword { get; set; }

    private bool ShowConfirmPassword { get; set; }

    private bool HasToken => PasswordResetTokens.LooksWellFormed(Token);

    private string? ErrorMessage => Error switch
    {
        "mismatch" => "New password and confirmation do not match.",
        "policy" => "That password does not meet the password policy. Use at least 8 characters with a letter and a digit.",
        "invalid" => "This password reset link is invalid, has expired, or has already been used. Request a new one.",
        _ => null
    };
}
