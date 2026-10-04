using System.Security.Claims;
using ErpWeb.Core.Authentication;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ErpWeb.Tests.Admin.Master;
[Trait(TestCategories.Name, TestCategories.Admin)]
public class CurrentUserServiceTests
{
    [Fact]
    public void IsInRole_matches_Role_claim()
    {
        var sut = CreateSut(Principal(role: "SYSTEM_ADMIN", level: "SYSTEM_ADMIN"));
        Assert.True(sut.IsInRole(AccessRightService.SystemAdminRole));
        Assert.False(sut.IsInRole(AccessRightService.AdminRole));
    }

    [Fact]
    public void IsInRole_falls_back_to_Level_claim_when_Role_missing()
    {
        // Simulates cookie/PathBase quirks: Level present, Role claim type not mapped for IsInRole.
        var claims = new List<Claim>
        {
            new(AppClaimTypes.Subject, "1"),
            new(AppClaimTypes.Level, "SYSTEM_ADMIN"),
            new(AppClaimTypes.CompanyCode, "DEMO"),
        };
        var identity = new ClaimsIdentity(claims, authenticationType: "Cookies");
        var sut = CreateSut(new ClaimsPrincipal(identity));

        Assert.True(sut.IsAuthenticated);
        Assert.Equal("SYSTEM_ADMIN", sut.UserLevel);
        Assert.True(sut.IsInRole("SYSTEM_ADMIN"));
        Assert.True(sut.IsInRole("system_admin"));
    }

    [Fact]
    public void IsInRole_false_when_neither_Role_nor_Level_match()
    {
        var sut = CreateSut(Principal(role: "USER", level: "USER"));
        Assert.False(sut.IsInRole(AccessRightService.SystemAdminRole));
        Assert.False(sut.IsInRole(AccessRightService.AdminRole));
    }

    private static CurrentUserService CreateSut(ClaimsPrincipal user)
    {
        var http = new DefaultHttpContext { User = user };
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(x => x.HttpContext).Returns(http);
        return new CurrentUserService(accessor.Object, new ServiceCollection().BuildServiceProvider());
    }

    private static ClaimsPrincipal Principal(string role, string level)
    {
        var claims = new List<Claim>
        {
            new(AppClaimTypes.Subject, "1"),
            new(AppClaimTypes.Level, level),
            new(AppClaimTypes.CompanyCode, "DEMO"),
            new(ClaimTypes.Role, role),
        };
        var identity = new ClaimsIdentity(
            claims,
            authenticationType: "Cookies",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }
}
