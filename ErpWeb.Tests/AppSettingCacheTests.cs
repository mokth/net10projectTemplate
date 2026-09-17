using ErpWeb.Core.Settings;

namespace ErpWeb.Tests;

/// <summary>
/// Cache isolation and eviction. The counters are tested directly because the invalidation matrix is
/// pure logic, and then end to end through the service because a wrong cache key is exactly the kind of
/// bug that only shows up as one tenant seeing another tenant's setting.
/// </summary>
public class AppSettingCacheTests
{
    private const string Module = AppSettingModules.Sales;
    private const string AllowBelowCost = AppSettingCatalogue.SalesKeys.AllowBelowCost;
    private const string QuoteDays = AppSettingCatalogue.SalesKeys.QuoteValidDays;

    // ── the counters ─────────────────────────────────────────────────────────

    [Fact]
    public void A_company_bump_changes_that_companys_stamp_and_its_branch_stamp()
    {
        var versions = new AppSettingCacheVersions();

        var companyBefore = versions.Stamp("DEMO", null);
        var branchBefore = versions.Stamp("DEMO", "HQ");

        versions.BumpCompany("DEMO");

        // The company snapshot must be invalidated...
        Assert.NotEqual(companyBefore, versions.Stamp("DEMO", null));

        // ...and so must the branch snapshot, because it may have fallen back to the company value (I8).
        Assert.NotEqual(branchBefore, versions.Stamp("DEMO", "HQ"));
    }

    [Fact]
    public void A_company_bump_leaves_another_company_alone()
    {
        var versions = new AppSettingCacheVersions();

        var otherBefore = versions.Stamp("OTHER", "HQ");
        versions.BumpCompany("DEMO");

        Assert.Equal(otherBefore, versions.Stamp("OTHER", "HQ"));
    }

    [Fact]
    public void A_branch_bump_changes_only_that_branch()
    {
        var versions = new AppSettingCacheVersions();

        var companyBefore = versions.Stamp("DEMO", null);
        var hqBefore = versions.Stamp("DEMO", "HQ");
        var klBefore = versions.Stamp("DEMO", "KL");

        versions.BumpBranch("DEMO", "HQ");

        Assert.Equal(companyBefore, versions.Stamp("DEMO", null));
        Assert.NotEqual(hqBefore, versions.Stamp("DEMO", "HQ"));
        Assert.Equal(klBefore, versions.Stamp("DEMO", "KL"));
    }

    [Fact]
    public void A_global_bump_changes_every_stamp()
    {
        var versions = new AppSettingCacheVersions();

        var a = versions.Stamp("DEMO", "HQ");
        var b = versions.Stamp("OTHER", "KL");
        var c = versions.Stamp(null, null);

        versions.BumpGlobal();

        Assert.NotEqual(a, versions.Stamp("DEMO", "HQ"));
        Assert.NotEqual(b, versions.Stamp("OTHER", "KL"));
        Assert.NotEqual(c, versions.Stamp(null, null));
    }

    [Fact]
    public void A_branch_bump_without_a_branch_degrades_to_a_company_bump()
    {
        var versions = new AppSettingCacheVersions();

        var companyBefore = versions.Stamp("DEMO", null);
        versions.BumpBranch("DEMO", "");

        Assert.NotEqual(companyBefore, versions.Stamp("DEMO", null));
    }

    // ── end to end through the service ───────────────────────────────────────

    /// <summary>I7: one company's stored settings must never appear in another company's behaviour.</summary>
    [Fact]
    public async Task Two_companies_never_see_each_others_values()
    {
        await using var host = AppSettingTestHost.Create();

        var saved = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = QuoteDays,
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO",
            Value = "45"
        });
        Assert.True(saved.Succeeded);

        var demo = await host.Service.GetValueAsync(Module, QuoteDays, AppSettingScope.Company, "DEMO", null);
        var other = await host.Service.GetValueAsync(Module, QuoteDays, AppSettingScope.Company, "OTHER", null);

        Assert.Equal("45", demo.Data);
        Assert.Equal("30", other.Data);
    }

    /// <summary>
    /// I8 in the case that a naive per-key cache would get wrong: a branch read that FELL BACK to a company
    /// value must stop doing so once the company value changes.
    /// </summary>
    [Fact]
    public async Task A_company_write_invalidates_that_companys_branch_snapshot()
    {
        await using var host = AppSettingTestHost.Create();

        // Prime the branch snapshot with the code default.
        Assert.Equal("false", (await host.Service.GetValueAsync(Module, AllowBelowCost, AppSettingScope.Branch, "DEMO", "HQ")).Data);

        var saved = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = AllowBelowCost,
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO",
            Value = "true"
        });
        Assert.True(saved.Succeeded);

        var after = await host.Service.GetValueAsync(Module, AllowBelowCost, AppSettingScope.Branch, "DEMO", "HQ");

        Assert.Equal("true", after.Data);
    }

    [Fact]
    public async Task A_global_write_invalidates_every_tenant()
    {
        await using var host = AppSettingTestHost.Create();

        const string key = AppSettingCatalogue.AdminKeys.SessionTimeoutMinutes;
        const string module = AppSettingModules.Admin;

        Assert.Equal("60", (await host.Service.GetForCurrentUserAsync(module, key)).Data);

        var saved = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = module,
            Key = key,
            Scope = AppSettingScope.Global,
            Value = "15"
        });
        Assert.True(saved.Succeeded);

        Assert.Equal("15", (await host.Service.GetForCurrentUserAsync(module, key)).Data);
    }

    [Fact]
    public async Task A_branch_write_does_not_affect_a_sibling_branch()
    {
        await using var host = AppSettingTestHost.Create();

        var saved = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = AllowBelowCost,
            Scope = AppSettingScope.Branch,
            CompanyCode = "DEMO",
            BranchCode = "KL",
            Value = "true"
        });
        Assert.True(saved.Succeeded);

        Assert.Equal("true", (await host.Service.GetValueAsync(Module, AllowBelowCost, AppSettingScope.Branch, "DEMO", "KL")).Data);
        Assert.Equal("false", (await host.Service.GetValueAsync(Module, AllowBelowCost, AppSettingScope.Branch, "DEMO", "HQ")).Data);
    }

    [Fact]
    public async Task A_clear_also_invalidates_the_affected_snapshots()
    {
        await using var host = AppSettingTestHost.Create();
        byte[] stored = [1, 2, 3, 4, 5, 6, 7, 8];
        await host.SeedAsync(Module, QuoteDays, "COMPANY", "DEMO", number: 45m, rowVersion: stored);

        Assert.Equal("45", (await host.Service.GetValueAsync(Module, QuoteDays, AppSettingScope.Company, "DEMO", null)).Data);

        var cleared = await host.Service.ClearAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = QuoteDays,
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO",
            RowVersion = Convert.ToBase64String(stored)
        });
        Assert.True(cleared.Succeeded);

        Assert.Equal("30", (await host.Service.GetValueAsync(Module, QuoteDays, AppSettingScope.Company, "DEMO", null)).Data);
    }
}
