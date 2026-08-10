using ErpWeb.Authentication;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Models;
using ErpWeb.Core.Services;
using Microsoft.AspNetCore.Mvc;

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
            IAccessRightService accessRights) =>
        {
            var result = await authService.ValidateCredentialsAsync(
                model.CompanyCode?.Trim() ?? string.Empty,
                model.Username?.Trim() ?? string.Empty,
                model.Password ?? string.Empty);

            if (!result.Succeeded || result.User is null)
            {
                return Results.Redirect("/login?error=1");
            }

            await cookieSignIn.SignInAsync(result.User, model.RememberMe);
            await accessRights.RefreshPermissionsAsync();

            return result.MustChangePassword
                ? Results.Redirect("/change-password")
                : Results.Redirect("/home");
        }).DisableAntiforgery().AllowAnonymous();

        group.MapGet("/logout", async (
            ICookieSignInService cookieSignIn,
            IAccessRightService accessRights) =>
        {
            await accessRights.RefreshPermissionsAsync();
            await cookieSignIn.SignOutAsync();
            return Results.Redirect("/login");
        }).AllowAnonymous();

        group.MapPost("/logout", async (
            ICookieSignInService cookieSignIn,
            IAccessRightService accessRights) =>
        {
            await accessRights.RefreshPermissionsAsync();
            await cookieSignIn.SignOutAsync();
            return Results.Redirect("/login");
        }).DisableAntiforgery().AllowAnonymous();

        group.MapPost("/change-password", async (
            [FromForm] ChangePasswordInputModel model,
            IAuthService authService,
            ICookieSignInService cookieSignIn,
            IAccessRightService accessRights) =>
        {
            if (!string.Equals(model.NewPassword, model.ConfirmPassword, StringComparison.Ordinal))
            {
                return Results.Redirect("/change-password?error=mismatch");
            }

            var result = await authService.ChangePasswordAsync(model.CurrentPassword, model.NewPassword);
            if (!result.Succeeded || result.User is null)
            {
                return Results.Redirect("/change-password?error=1");
            }

            await cookieSignIn.SignInAsync(result.User);
            await accessRights.RefreshPermissionsAsync();
            return Results.Redirect("/home");
        }).DisableAntiforgery();

        return endpoints;
    }
}
