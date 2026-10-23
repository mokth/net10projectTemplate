using System.Reflection;
using ErpWeb.Core.Authentication;
using ErpWeb.UI;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.Tests.Admin.Master;

/// <summary>
/// Verifies the forced-password-change authorization boundary at the route/endpoint level:
/// only the login/recovery pages are anonymous, the Change Password page is authenticated-only
/// (not ErpAccess), every other routable component is covered by the ErpAccess policy inherited from
/// <see cref="PageBase"/>, and no protected minimal-API endpoint opts out of that policy.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Admin)]
public class RouteAuthorizationCoverageTests
{
    [Fact]
    public void Login_forgot_and_reset_password_are_anonymous()
    {
        AssertAnonymous(typeof(Login));
        AssertAnonymous(typeof(ForgotPassword));
        AssertAnonymous(typeof(ResetPassword));
    }

    [Fact]
    public void ChangePassword_is_authenticated_only_not_ErpAccess()
    {
        var authorize = typeof(ChangePassword).BaseType!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single();
        Assert.Null(authorize.Policy);
    }

    [Fact]
    public void PageBase_requires_ErpAccess_policy()
    {
        var authorize = typeof(PageBase)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single();
        Assert.Equal(ErpAuthPolicies.ErpAccess, authorize.Policy);
    }

    /// <summary>
    /// Enumerates every routable component in the UI assembly instead of relying on a manual grep, so a
    /// new page cannot silently escape the forced-change gate. The allowed categories are deliberately
    /// small: anonymous auth/recovery pages, the Change Password exception, and ErpAccess.
    /// </summary>
    [Fact]
    public void Every_routable_component_is_anonymous_ChangePassword_or_ErpAccess()
    {
        var pages = typeof(UiAssemblyMarker).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetCustomAttributes(typeof(RouteAttribute), inherit: true).Length != 0)
            .ToList();

        Assert.NotEmpty(pages);

        var uncovered = pages
            .Where(page => !IsAnonymous(page)
                           && !HasErpAccess(page)
                           && !IsChangePasswordException(page))
            .Select(page => page.FullName)
            .ToList();

        Assert.True(
            uncovered.Count == 0,
            "These routable components are neither anonymous, nor the Change Password exception, nor "
            + "covered by the ErpAccess policy (inherit from PageBase or add [Authorize(Policy = "
            + "ErpAuthPolicies.ErpAccess)]): " + string.Join(", ", uncovered));
    }

    /// <summary>
    /// Regression guard for plan §15.6: a protected endpoint must not use the bare
    /// <c>RequireAuthorization()</c>, because that binds to the default policy (authenticated only) and
    /// would let a forced-password-change user reach business data by calling the endpoint directly.
    /// </summary>
    [Fact]
    public void No_protected_endpoint_uses_bare_RequireAuthorization()
    {
        var webProject = Path.Combine(FindRepositoryRoot(), "ErpWeb");
        var endpointFiles = Directory
            .GetFiles(webProject, "*Endpoints.cs", SearchOption.AllDirectories);

        Assert.NotEmpty(endpointFiles);

        var offenders = endpointFiles
            .Where(file => File.ReadAllText(file).Contains(".RequireAuthorization()", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(webProject, file))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Use .RequireAuthorization(ErpAuthPolicies.ErpAccess) instead of .RequireAuthorization(): "
            + string.Join(", ", offenders));
    }

    private static bool IsAnonymous(Type type) =>
        type.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Length != 0;

    private static bool HasErpAccess(Type type) =>
        type.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Any(a => string.Equals(a.Policy, ErpAuthPolicies.ErpAccess, StringComparison.Ordinal));

    /// <summary>The Change Password page must stay reachable while a change is required.</summary>
    private static bool IsChangePasswordException(Type type) =>
        type == typeof(ChangePassword);

    private static void AssertAnonymous(Type type) =>
        Assert.NotNull(type.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).SingleOrDefault());

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ErpWeb.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (the folder containing ErpWeb.slnx) from "
            + AppContext.BaseDirectory + ".");
    }
}
