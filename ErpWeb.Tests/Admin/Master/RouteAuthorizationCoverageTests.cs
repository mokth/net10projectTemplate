using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Authorization;

namespace ErpWeb.Tests.Admin.Master;

/// <summary>
/// Verifies the forced-password-change authorization boundary at the route level: only the login and
/// forgot-password pages are anonymous, the Change Password page is authenticated-only (not ErpAccess),
/// and every PageBase-derived page requires the ErpAccess policy.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Admin)]
public class RouteAuthorizationCoverageTests
{
    [Fact]
    public void Login_and_forgot_password_are_anonymous()
    {
        AssertAnonymous(typeof(Login));
        AssertAnonymous(typeof(ForgotPassword));
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
        Assert.Equal("ErpAccess", authorize.Policy);
    }

    private static void AssertAnonymous(Type type)
    {
        Assert.NotNull(type.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).SingleOrDefault());
    }
}
