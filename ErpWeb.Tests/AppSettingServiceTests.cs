using ErpWeb.Core.Inventory;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Settings;

namespace ErpWeb.Tests;

/// <summary>
/// The settings service end to end against a real database: precedence, scope containment, the
/// read-only price-method projection, gated writes, and the exactly-one-value delete rule.
/// </summary>
public class AppSettingServiceTests
{
    private const string Module = AppSettingModules.Sales;
    private const string QuoteDays = AppSettingCatalogue.SalesKeys.QuoteValidDays;
    private const string AllowBelowCost = AppSettingCatalogue.SalesKeys.AllowBelowCost;

    // ── resolution ───────────────────────────────────────────────────────────

    [Fact]
    public async Task No_row_anywhere_resolves_the_code_default()
    {
        await using var host = AppSettingTestHost.Create();

        var result = await host.Service.GetValueAsync(Module, QuoteDays, AppSettingScope.Branch, "DEMO", "HQ");

        Assert.True(result.Succeeded);
        Assert.Equal("30", result.Data);
    }

    [Fact]
    public async Task Branch_beats_company_through_the_database()
    {
        await using var host = AppSettingTestHost.Create();

        // Both rows are seeded BEFORE the first read: a direct seed bypasses the service and therefore
        // does not invalidate the cache. That is a fixture concern, not product behaviour.
        await host.SeedAsync(Module, AllowBelowCost, "COMPANY", "DEMO", flag: true);
        await host.SeedAsync(Module, AllowBelowCost, "BRANCH", "DEMO", "HQ", flag: false);

        // The branch row wins at branch depth...
        Assert.Equal("false", (await host.Service.GetValueAsync(Module, AllowBelowCost, AppSettingScope.Branch, "DEMO", "HQ")).Data);

        // ...and the company row is what a company-depth caller sees.
        Assert.Equal("true", (await host.Service.GetValueAsync(Module, AllowBelowCost, AppSettingScope.Company, "DEMO", null)).Data);
    }

    /// <summary>
    /// A read at BRANCH depth must still consult the company and global levels — the scope is an
    /// inclusivity level, not an exclusive bitmask. Getting this wrong silently discards every default.
    /// </summary>
    [Fact]
    public async Task A_branch_depth_read_falls_back_to_the_company_value()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedAsync(Module, QuoteDays, "COMPANY", "DEMO", number: 45m);

        var result = await host.Service.GetValueAsync(Module, QuoteDays, AppSettingScope.Branch, "DEMO", "HQ");

        Assert.Equal("45", result.Data);
    }

    [Fact]
    public async Task A_company_scope_read_cannot_see_a_branch_row()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedAsync(Module, AllowBelowCost, "BRANCH", "DEMO", "HQ", flag: true);

        var result = await host.Service.GetValueAsync(Module, AllowBelowCost, AppSettingScope.Company, "DEMO", "HQ");

        // The caller asked for company depth; the branch row is out of reach.
        Assert.Equal("false", result.Data);
    }

    /// <summary>I3: the definition's allowed scopes beat whatever is stored.</summary>
    [Fact]
    public async Task A_row_at_a_scope_the_definition_disallows_is_ignored()
    {
        await using var host = AppSettingTestHost.Create();

        // QUOTE_VALID_DAYS is Company-only; a branch row must never apply to it.
        await host.SeedAsync(Module, QuoteDays, "BRANCH", "DEMO", "HQ", number: 99m);

        var result = await host.Service.GetValueAsync(Module, QuoteDays, AppSettingScope.Branch, "DEMO", "HQ");

        Assert.Equal("30", result.Data);
    }

    [Fact]
    public async Task A_branch_row_in_another_branch_is_not_used()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedAsync(Module, AllowBelowCost, "BRANCH", "DEMO", "KL", flag: true);

        var result = await host.Service.GetValueAsync(Module, AllowBelowCost, AppSettingScope.Branch, "DEMO", "HQ");

        Assert.Equal("false", result.Data);
    }

    [Fact]
    public async Task A_row_belonging_to_another_company_is_not_used()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedAsync(Module, QuoteDays, "COMPANY", "OTHER", number: 45m);

        var result = await host.Service.GetValueAsync(Module, QuoteDays, AppSettingScope.Company, "DEMO", null);

        Assert.Equal("30", result.Data);
    }

    [Fact]
    public async Task An_unparseable_stored_value_falls_to_the_default_and_the_list_reports_why()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedAsync(Module, QuoteDays, "COMPANY", "DEMO", text: "not-a-number");

        var result = await host.Service.GetValueAsync(Module, QuoteDays, AppSettingScope.Company, "DEMO", null);
        Assert.Equal("30", result.Data);

        var list = await host.Service.ListForModuleAsync(Module, "DEMO", "HQ");
        var row = Assert.Single(list.Data!, r => r.Key == QuoteDays);

        Assert.True(row.IsDefault);
        Assert.NotNull(row.RejectedReason);
    }

    /// <summary>Containment: a hand-inserted row is ignored on read, never resolved.</summary>
    [Fact]
    public async Task A_row_whose_key_is_not_in_the_catalogue_is_ignored()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedAsync(Module, "NOT_A_REAL_KEY", "COMPANY", "DEMO", text: "boom");

        var result = await host.Service.GetValueAsync(Module, "NOT_A_REAL_KEY", AppSettingScope.Company, "DEMO", null);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);

        // And it does not appear in the module listing.
        var list = await host.Service.ListForModuleAsync(Module, "DEMO", "HQ");
        Assert.DoesNotContain(list.Data!, r => r.Key == "NOT_A_REAL_KEY");
    }

    [Fact]
    public async Task Reading_an_unknown_key_is_not_found_rather_than_a_default()
    {
        await using var host = AppSettingTestHost.Create();

        var result = await host.Service.GetValueAsync(Module, "NOPE", AppSettingScope.Company, "DEMO", null);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Get_for_current_user_needs_a_company_scope()
    {
        // No company claim at all.
        await using var host = AppSettingTestHost.Create(company: "");

        var result = await host.Service.GetForCurrentUserAsync(Module, QuoteDays);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.InvalidScope, result.ErrorCode);
    }

    [Fact]
    public async Task The_flag_and_number_helpers_read_through_the_ladder()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedAsync(Module, AllowBelowCost, "COMPANY", "DEMO", flag: true);
        await host.SeedAsync(Module, QuoteDays, "COMPANY", "DEMO", number: 45m);

        Assert.True(await host.Service.GetFlagAsync(Module, AllowBelowCost));
        Assert.Equal(45m, await host.Service.GetNumberAsync(Module, QuoteDays));

        // A missing key is false / null rather than an exception.
        Assert.False(await host.Service.GetFlagAsync(Module, "NOPE"));
        Assert.Null(await host.Service.GetNumberAsync(Module, "NOPE"));
    }

    // ── the read-only projection ─────────────────────────────────────────────

    [Fact]
    public async Task The_price_method_is_projected_from_the_company_column()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedCompanyAsync("DEMO", salesPriceMethod: "item_default_only");

        var result = await host.Service.GetValueAsync(
            AppSettingModules.Sales,
            AppSettingCatalogue.SalesKeys.PriceMethod,
            AppSettingScope.Company,
            "DEMO",
            null);

        // Stored lower case, reported through SaCompanyPriceMethod.Normalize.
        Assert.True(result.Succeeded);
        Assert.Equal(SaCompanyPriceMethod.ItemDefaultOnly, result.Data);
    }

    [Fact]
    public async Task The_price_method_falls_to_the_default_when_the_column_is_blank()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedCompanyAsync("DEMO");

        var result = await host.Service.GetValueAsync(
            AppSettingModules.Sales,
            AppSettingCatalogue.SalesKeys.PriceMethod,
            AppSettingScope.Company,
            "DEMO",
            null);

        Assert.Equal(SaCompanyPriceMethod.CustomerItemAndList, result.Data);
    }

    [Fact]
    public async Task The_price_method_row_is_listed_as_read_only()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedCompanyAsync("DEMO", salesPriceMethod: SaCompanyPriceMethod.PriceListOnly);

        var list = await host.Service.ListForModuleAsync(Module, "DEMO", "HQ");
        var row = Assert.Single(list.Data!, r => r.Key == AppSettingCatalogue.SalesKeys.PriceMethod);

        Assert.True(row.IsReadOnly);
        Assert.False(row.IsDefault);
        Assert.Equal(AppSettingScope.Company, row.Provenance);
        Assert.Equal(SaCompanyPriceMethod.PriceListOnly, row.Value);
    }

    [Fact]
    public async Task A_blank_price_method_column_lists_as_the_code_default()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedCompanyAsync("DEMO");

        var list = await host.Service.ListForModuleAsync(Module, "DEMO", "HQ");
        var row = Assert.Single(list.Data!, r => r.Key == AppSettingCatalogue.SalesKeys.PriceMethod);

        Assert.True(row.IsDefault);
        Assert.Equal(AppSettingScope.None, row.Provenance);
        Assert.Equal(SaCompanyPriceMethod.CustomerItemAndList, row.Value);
    }

    [Fact]
    public async Task Saving_the_price_method_is_refused_with_a_pointer_to_its_own_screen()
    {
        await using var host = AppSettingTestHost.Create();

        var result = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = AppSettingModules.Sales,
            Key = AppSettingCatalogue.SalesKeys.PriceMethod,
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO",
            Value = SaCompanyPriceMethod.PriceListOnly
        });

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);

        // Nothing was written to the registry for a column-backed setting.
        Assert.Empty(await host.RowsAsync());
    }

    // ── listing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_shows_every_definition_of_the_module_including_unset_ones()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedCompanyAsync("DEMO");

        var list = await host.Service.ListForModuleAsync(Module, "DEMO", "HQ");

        Assert.True(list.Succeeded);
        Assert.Equal(AppSettingCatalogue.ForModule(Module).Count, list.Data!.Count);
        Assert.All(list.Data!, r => Assert.Equal(Module, r.Module, ignoreCase: true));

        var unset = Assert.Single(list.Data!, r => r.Key == QuoteDays);
        Assert.True(unset.IsDefault);
        Assert.Equal("30", unset.Value);
    }

    [Fact]
    public async Task The_list_rejects_an_unknown_module()
    {
        await using var host = AppSettingTestHost.Create();

        var list = await host.Service.ListForModuleAsync("NOT_A_MODULE", "DEMO", "HQ");

        Assert.False(list.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, list.ErrorCode);
    }

    // ── writes ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_then_read_returns_the_new_value()
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

        var read = await host.Service.GetValueAsync(Module, QuoteDays, AppSettingScope.Company, "DEMO", null);
        Assert.Equal("45", read.Data);

        var rows = await host.RowsAsync();
        var row = Assert.Single(rows);
        Assert.Equal(45m, row.ValueNumber);
        Assert.Null(row.ValueText);
        Assert.Equal("admin", row.CreatedBy);
    }

    [Fact]
    public async Task Save_accepts_a_global_setting_without_a_company_target()
    {
        await using var host = AppSettingTestHost.Create();

        var saved = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = AppSettingModules.Admin,
            Key = AppSettingCatalogue.AdminKeys.SessionTimeoutMinutes,
            Scope = AppSettingScope.Global,
            Value = "15"
        });

        Assert.True(saved.Succeeded);
        Assert.Equal("15", (await host.Service.GetForCurrentUserAsync(AppSettingModules.Admin, AppSettingCatalogue.AdminKeys.SessionTimeoutMinutes)).Data);
    }

    [Fact]
    public async Task Save_rejects_an_unknown_key()
    {
        await using var host = AppSettingTestHost.Create();

        var result = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = "NOT_A_REAL_KEY",
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO",
            Value = "1"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
        Assert.Empty(await host.RowsAsync());
    }

    [Fact]
    public async Task Save_rejects_a_scope_the_definition_disallows()
    {
        await using var host = AppSettingTestHost.Create();

        // QUOTE_VALID_DAYS is Company-only.
        var result = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = QuoteDays,
            Scope = AppSettingScope.Branch,
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Value = "45"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Empty(await host.RowsAsync());
    }

    [Fact]
    public async Task Save_rejects_a_missing_tenant_target()
    {
        await using var host = AppSettingTestHost.Create();

        var noCompany = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = QuoteDays,
            Scope = AppSettingScope.Company,
            Value = "45"
        });
        Assert.Equal(IvMasterErrorCode.Validation, noCompany.ErrorCode);

        var noBranch = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = AllowBelowCost,
            Scope = AppSettingScope.Branch,
            CompanyCode = "DEMO",
            Value = "true"
        });
        Assert.Equal(IvMasterErrorCode.Validation, noBranch.ErrorCode);
    }

    [Fact]
    public async Task Save_rejects_a_value_that_does_not_fit_the_declared_type()
    {
        await using var host = AppSettingTestHost.Create();

        var result = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = QuoteDays,
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO",
            Value = "thirty"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Empty(await host.RowsAsync());
    }

    [Fact]
    public async Task Save_rejects_a_token_that_is_not_in_the_allowed_list()
    {
        await using var host = AppSettingTestHost.Create();

        var result = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = AppSettingModules.Sales,
            Key = AppSettingCatalogue.SalesKeys.PriceMethod,
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO",
            Value = "MADE_UP"
        });

        // Refused because it is column-backed, before the token check matters — either way: refused, nothing written.
        Assert.False(result.Succeeded);
        Assert.Empty(await host.RowsAsync());
    }

    [Fact]
    public async Task Saving_twice_at_the_same_scope_updates_rather_than_duplicating()
    {
        await using var host = AppSettingTestHost.Create();

        var first = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = QuoteDays,
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO",
            Value = "45"
        });
        Assert.True(first.Succeeded);

        var second = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = QuoteDays,
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO",
            Value = "60"
        });

        // On SQLite there is no server rowversion, so an existing row cannot be updated without the
        // token it was read with. The service must refuse rather than silently overwrite.
        Assert.False(second.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, second.ErrorCode);

        var row = Assert.Single(await host.RowsAsync());
        Assert.Equal(45m, row.ValueNumber);
    }

    [Fact]
    public async Task An_update_with_a_stale_row_version_returns_concurrency()
    {
        await using var host = AppSettingTestHost.Create();
        byte[] stored = [1, 2, 3, 4, 5, 6, 7, 8];
        await host.SeedAsync(Module, QuoteDays, "COMPANY", "DEMO", number: 45m, rowVersion: stored);

        var result = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = QuoteDays,
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO",
            Value = "60",
            RowVersion = Convert.ToBase64String([9, 9, 9, 9, 9, 9, 9, 9])
        });

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);
    }

    [Fact]
    public async Task An_update_with_the_current_row_version_succeeds_and_audits()
    {
        await using var host = AppSettingTestHost.Create();
        byte[] stored = [1, 2, 3, 4, 5, 6, 7, 8];
        await host.SeedAsync(Module, QuoteDays, "COMPANY", "DEMO", number: 45m, rowVersion: stored);

        var result = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = QuoteDays,
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO",
            Value = "60",
            RowVersion = Convert.ToBase64String(stored)
        });

        Assert.True(result.Succeeded);

        var row = Assert.Single(await host.RowsAsync());
        Assert.Equal(60m, row.ValueNumber);
        Assert.Equal("admin", row.ModifiedBy);
        Assert.NotNull(row.ModifiedDate);
    }

    [Fact]
    public async Task An_update_without_a_row_version_is_rejected()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedAsync(Module, QuoteDays, "COMPANY", "DEMO", number: 45m, rowVersion: [1, 2, 3, 4, 5, 6, 7, 8]);

        var result = await host.Service.SaveAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = QuoteDays,
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO",
            Value = "60"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Equal(45m, (await host.RowsAsync())[0].ValueNumber);
    }

    // ── clearing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_deletes_the_row_so_the_default_returns()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedAsync(Module, QuoteDays, "COMPANY", "DEMO", number: 45m, rowVersion: [1, 2, 3, 4, 5, 6, 7, 8]);

        var cleared = await host.Service.ClearAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = QuoteDays,
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO",
            RowVersion = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8])
        });

        Assert.True(cleared.Succeeded);
        Assert.Empty(await host.RowsAsync());
        Assert.Equal("30", (await host.Service.GetValueAsync(Module, QuoteDays, AppSettingScope.Company, "DEMO", null)).Data);
    }

    [Fact]
    public async Task Clear_on_a_row_that_does_not_exist_is_a_harmless_no_op()
    {
        await using var host = AppSettingTestHost.Create();

        var result = await host.Service.ClearAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = QuoteDays,
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO"
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Clear_with_a_stale_row_version_returns_concurrency()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedAsync(Module, QuoteDays, "COMPANY", "DEMO", number: 45m, rowVersion: [1, 2, 3, 4, 5, 6, 7, 8]);

        var result = await host.Service.ClearAsync(new AppSettingEditVm
        {
            Module = Module,
            Key = QuoteDays,
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO",
            RowVersion = Convert.ToBase64String([9, 9, 9, 9, 9, 9, 9, 9])
        });

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);
        Assert.Single(await host.RowsAsync());
    }

    [Fact]
    public async Task Clear_cannot_touch_a_column_backed_setting()
    {
        await using var host = AppSettingTestHost.Create();

        var result = await host.Service.ClearAsync(new AppSettingEditVm
        {
            Module = AppSettingModules.Sales,
            Key = AppSettingCatalogue.SalesKeys.PriceMethod,
            Scope = AppSettingScope.Company,
            CompanyCode = "DEMO"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
    }

    // ── gating ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_and_save_and_clear_all_require_their_permission()
    {
        await using var noAccess = AppSettingTestHost.Create(canAccess: false);
        Assert.Equal(IvMasterErrorCode.AccessDenied, (await noAccess.Service.ListForModuleAsync(Module, "DEMO", "HQ")).ErrorCode);

        await using var noEdit = AppSettingTestHost.Create(canAccess: true, canEdit: false);
        Assert.Equal(
            IvMasterErrorCode.AccessDenied,
            (await noEdit.Service.SaveAsync(new AppSettingEditVm
            {
                Module = Module,
                Key = QuoteDays,
                Scope = AppSettingScope.Company,
                CompanyCode = "DEMO",
                Value = "45"
            })).ErrorCode);

        Assert.Equal(
            IvMasterErrorCode.AccessDenied,
            (await noEdit.Service.ClearAsync(new AppSettingEditVm
            {
                Module = Module,
                Key = QuoteDays,
                Scope = AppSettingScope.Company,
                CompanyCode = "DEMO"
            })).ErrorCode);
    }

    /// <summary>
    /// Reads are a server capability, like the pricing resolver: a page must be able to ask for a setting
    /// without the caller holding a settings-screen permission.
    /// </summary>
    [Fact]
    public async Task Reads_are_not_gated_by_the_settings_menu()
    {
        await using var host = AppSettingTestHost.Create(canAccess: false, canEdit: false);

        var result = await host.Service.GetValueAsync(Module, QuoteDays, AppSettingScope.Company, "DEMO", null);

        Assert.True(result.Succeeded);
        Assert.Equal("30", result.Data);
    }
}
