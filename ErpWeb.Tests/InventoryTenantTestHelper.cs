using ErpWeb.Core.Inventory;
using ErpWeb.Core.Services;
using Moq;

namespace ErpWeb.Tests;

internal static class InventoryTenantTestHelper
{
    public static IInventoryTenantContext CreateTenantContext(
        string company = "DEMO",
        string branch = "HQ",
        string? location = "SITE",
        string userId = "admin",
        string subjectUid = "1")
    {
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.SubjectUid).Returns(subjectUid);
        current.SetupGet(x => x.UserId).Returns(userId);
        current.SetupGet(x => x.CompanyCode).Returns(company);
        current.SetupGet(x => x.BranchCode).Returns(branch);
        current.SetupGet(x => x.LocationCode).Returns(location);
        return new InventoryTenantContext(current.Object);
    }
}
