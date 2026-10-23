using System.Reflection;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.Tests.Admin.Master;

/// <summary>
/// Guards the auth pages against a querystring type that Blazor cannot bind.
///
/// <para>
/// <c>[SupplyParameterFromQuery]</c> on a <see cref="bool"/> routes through
/// <c>UrlValueConstraint&lt;bool&gt;</c>, which uses <c>bool.TryParse</c>. That rejects <c>"1"</c>,
/// and the resulting <see cref="InvalidOperationException"/> is thrown from
/// <c>SetParametersAsync</c> during static prerender — before any circuit or error boundary exists —
/// so the request fails with an HTTP 500 instead of rendering the page. The redirects in
/// <c>AccountEndpoints</c> use <c>?error=1</c> / <c>?submitted=1</c>, so auth pages must bind these
/// values as strings and interpret them explicitly.
/// </para>
/// </summary>
[Trait(TestCategories.Name, TestCategories.Admin)]
public class AuthPageQueryBindingTests
{
    [Theory]
    [InlineData(typeof(ForgotPassword))]
    [InlineData(typeof(ResetPassword))]
    [InlineData(typeof(Login))]
    [InlineData(typeof(ChangePassword))]
    public void Auth_pages_do_not_bind_query_values_as_bool(Type page)
    {
        var boolParameters = page
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<SupplyParameterFromQueryAttribute>() is not null)
            .Where(p => p.PropertyType == typeof(bool) || p.PropertyType == typeof(bool?))
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(boolParameters);
    }

    [Fact]
    public void ForgotPassword_recognises_the_submitted_redirect()
    {
        var page = new ForgotPassword { Submitted = "1" };
        Assert.True(ReadRequestSubmitted(page));

        // Any other value must fall back to the form rather than fail.
        page.Submitted = "0";
        Assert.False(ReadRequestSubmitted(page));

        page.Submitted = null;
        Assert.False(ReadRequestSubmitted(page));
    }

    private static bool ReadRequestSubmitted(ForgotPassword page) =>
        (bool)typeof(ForgotPassword)
            .GetProperty("RequestSubmitted", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(page)!;
}
