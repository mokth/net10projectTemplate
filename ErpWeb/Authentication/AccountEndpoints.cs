using ErpWeb.Core.Menus;
using ErpWeb.Core.Models;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ErpWeb.Authentication;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/account");

        group.MapPost("/login", async (
            [FromForm] LoginInputModel model,
            IAuthService authService,
            ICookieSignInService cookieSignIn,
            IAccessRightService accessRights,
            HttpContext http) =>
        {
            var result = await authService.ValidateCredentialsAsync(
                model.CompanyCode?.Trim() ?? string.Empty,
                model.Username?.Trim() ?? string.Empty,
                model.Password ?? string.Empty);

            if (!result.Succeeded || result.User is null)
            {
                return RedirectToApp(http, "/login?error=1");
            }

            await cookieSignIn.SignInAsync(result.User, model.RememberMe);
            await accessRights.RefreshPermissionsAsync();

            return result.MustChangePassword
                ? RedirectToApp(http, "/change-password")
                : RedirectToApp(http, "/home");
        }).AllowAnonymous().RequireRateLimiting("auth-login");

        // Logout is a POST only, protected by antiforgery, so a GET link cannot sign a user out.
        group.MapPost("/logout", async (
            ICookieSignInService cookieSignIn,
            IAccessRightService accessRights,
            HttpContext http) =>
        {
            await accessRights.RefreshPermissionsAsync();
            await cookieSignIn.SignOutAsync();
            return RedirectToApp(http, "/login");
        }).AllowAnonymous();

        group.MapPost("/change-password", async (
            [FromForm] ChangePasswordInputModel model,
            IAuthService authService,
            ICookieSignInService cookieSignIn,
            IAccessRightService accessRights,
            HttpContext http) =>
        {
            if (!string.Equals(model.NewPassword, model.ConfirmPassword, StringComparison.Ordinal))
            {
                return RedirectToApp(http, "/change-password?error=mismatch");
            }

            var result = await authService.ChangePasswordAsync(model.CurrentPassword, model.NewPassword);
            if (!result.Succeeded || result.User is null)
            {
                return RedirectToApp(http, "/change-password?error=1");
            }

            await cookieSignIn.SignInAsync(result.User);
            await accessRights.RefreshPermissionsAsync();
            return RedirectToApp(http, "/home");
        });

        group.MapPost("/forgot-password", async (
            [FromForm] ForgotPasswordInputModel model,
            IPasswordRecoveryService recovery,
            IOptions<PasswordRecoveryOptions> recoveryOptions,
            HttpContext http) =>
        {
            // A reset link must never point at a host taken from an untrusted header, so the
            // configured PublicBaseUrl wins when present; otherwise fall back to the request.
            await recovery.RequestResetAsync(
                model.CompanyCode?.Trim() ?? string.Empty,
                model.Username?.Trim() ?? string.Empty,
                ResetLinkBaseUrl(http, recoveryOptions.Value),
                http.Connection.RemoteIpAddress?.ToString());

            // Enumeration-safe: always the same redirect regardless of whether an account matched.
            return RedirectToApp(http, "/forgot-password?submitted=1");
        }).AllowAnonymous().RequireRateLimiting("auth-recovery");

        group.MapPost("/reset-password", async (
            [FromForm] ResetPasswordInputModel model,
            IPasswordRecoveryService recovery,
            HttpContext http) =>
        {
            if (!string.Equals(model.NewPassword, model.ConfirmPassword, StringComparison.Ordinal))
            {
                return RedirectToResetPage(http, model.Token, "mismatch");
            }

            var result = await recovery.RedeemAsync(model.Token, model.NewPassword ?? string.Empty);
            if (result.Succeeded)
            {
                return RedirectToApp(http, "/login?reset=1");
            }

            // The token is echoed back so a password-policy rejection does not cost the user the
            // link. It already travelled in this request's own URL.
            return result.Status == PasswordResetRedeemStatus.PolicyRejected
                ? RedirectToResetPage(http, model.Token, "policy")
                : RedirectToResetPage(http, model.Token, "invalid");
        }).AllowAnonymous().RequireRateLimiting("auth-reset");

        return endpoints;
    }

    /// <summary>
    /// Absolute base URL for the emailed reset link, without a trailing slash.
    /// </summary>
    private static string ResetLinkBaseUrl(HttpContext http, PasswordRecoveryOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            return options.PublicBaseUrl.TrimEnd('/');
        }

        return $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}".TrimEnd('/');
    }

    /// <summary>
    /// Sends the browser back to the reset form with an error code, keeping the token so the user can
    /// correct a typo instead of requesting a brand-new link. An absent token drops the parameter
    /// rather than sending <c>token=</c>.
    /// </summary>
    private static IResult RedirectToResetPage(HttpContext http, string? token, string errorCode)
    {
        var query = string.IsNullOrWhiteSpace(token)
            ? $"?error={errorCode}"
            : $"?token={Uri.EscapeDataString(token)}&error={errorCode}";

        return RedirectToApp(http, "/reset-password" + query);
    }

    /// <summary>
    /// Builds a redirect that stays inside the app when it is deployed below the site root.
    ///
    /// <para>
    /// <c>Results.Redirect("/home")</c> emits <c>Location: /home</c>, and a Location header is resolved
    /// by the browser against the ORIGIN - <c>&lt;base href&gt;</c> does not apply to it - so on an IIS
    /// sub-application at <c>/erpweb</c> the operator would be sent to the root site and never come
    /// back. Prefixing the request's path base keeps the redirect local, and is a no-op at the site
    /// root. (The cookie handler does this itself for LoginPath/LogoutPath/AccessDeniedPath, which is
    /// why those need no change.)
    /// </para>
    /// </summary>
    private static IResult RedirectToApp(HttpContext http, string appRelativePath) =>
        Results.Redirect($"{http.Request.PathBase}/{appRelativePath.TrimStart('/')}");
}
