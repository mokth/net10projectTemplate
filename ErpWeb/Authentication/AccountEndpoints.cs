using ErpWeb.Core.Menus;
using ErpWeb.Core.Models;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
            HttpContext http) =>
        {
            await recovery.RequestResetAsync(
                model.CompanyCode?.Trim() ?? string.Empty,
                model.Username?.Trim() ?? string.Empty);

            // Enumeration-safe: always the same redirect regardless of whether an account matched.
            return RedirectToApp(http, "/forgot-password?submitted=1");
        }).AllowAnonymous().RequireRateLimiting("auth-recovery");

        return endpoints;
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
