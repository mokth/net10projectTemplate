using System.Globalization;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Core.Settings;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Admin;

/// <summary>
/// Dynamic settings registry — a tab per module, one row per catalogue definition.
///
/// <para>
/// The screen renders the CATALOGUE left-joined with the stored rows, so a setting nobody has set is
/// visible as its code default rather than being absent. It never derives its rows from the table, which
/// is what stops a hand-inserted row from appearing as a supported setting.
/// </para>
///
/// <para>
/// A column-backed setting (the sales pricing method) is rendered read-only with a pointer to the screen
/// that owns it; the service refuses a write for it as well, so the gate is not merely cosmetic.
/// </para>
/// </summary>
public partial class AdminSettings : PageBase
{
    [Inject]
    private IAppSettingService Settings { get; set; } = default!;

    [Inject]
    private IAccessRightService AccessRights { get; set; } = default!;

    [Inject]
    private ITenantScopeContext Tenant { get; set; } = default!;

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool CanEdit;
    protected string? StatusMessage;

    protected string ActiveModule { get; set; } = AppSettingModules.Admin;

    protected IReadOnlyList<AppSettingListRow> Rows { get; set; } = [];

    protected bool PopupVisible { get; set; }
    protected string? EditError { get; set; }
    protected AppSettingListRow? EditRow { get; set; }
    protected AppSettingScope EditScope { get; set; } = AppSettingScope.Company;
    protected string? EditText { get; set; }
    protected bool EditBool { get; set; }
    protected decimal? EditNumber { get; set; }
    protected string? EditRemark { get; set; }

    private string? EditRowVersion { get; set; }

    protected static IReadOnlyList<string> Modules => AppSettingModules.All;

    protected string? CompanyCode => Tenant.TryCompanyScope()?.CompanyCode;

    protected string? BranchCode => Tenant.TryBranchScope()?.BranchCode;

    /// <summary>Scope choices for the current definition, restricted to what the definition allows.</summary>
    protected IReadOnlyList<ScopeOption> EditScopeOptions =>
        EditRow is null
            ? []
            : AllScopes
                .Where(s => EditRow.AllowedScopes.HasFlag(s))
                .Select(s => new ScopeOption(s, ScopeLabel(s)))
                .ToList();

    /// <summary>Token choices for the current definition. A token is always picked, never typed.</summary>
    protected IReadOnlyList<TokenOption> EditTokenOptions =>
        EditRow?.AllowedTokens.Select(t => new TokenOption(t, t)).ToList() ?? [];

    private static readonly AppSettingScope[] AllScopes =
    [
        AppSettingScope.Global, AppSettingScope.Company, AppSettingScope.Branch
    ];

    protected override async Task OnPageInitializedAsync()
    {
        CanEdit = await AccessRights.CanAsync(MenuCodes.AdminSettings, PermissionCodes.Edit);
        await LoadAsync();
    }

    protected async Task SelectModuleAsync(string module)
    {
        if (string.Equals(module, ActiveModule, StringComparison.Ordinal))
        {
            return;
        }

        ActiveModule = module;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var result = await Settings.ListForModuleAsync(ActiveModule, CompanyCode, BranchCode);

            if (!result.Succeeded)
            {
                Rows = [];
                ErrorMessage = result.Message ?? "Unable to load settings.";
                return;
            }

            Rows = result.Data ?? [];
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── editor ──────────────────────────────────────────────────────────────

    protected void OpenEditor(AppSettingListRow row)
    {
        if (row.IsReadOnly)
        {
            // The owning screen is the only place this value may change.
            Navigation.NavigateTo("/admin/company");
            return;
        }

        EditRow = row;
        EditError = null;
        EditScope = DefaultScope(row);
        LoadEditorForScope();

        PopupVisible = true;
    }

    protected void OnScopeChanged(AppSettingScope scope)
    {
        EditScope = scope;
        EditError = null;
        LoadEditorForScope();
    }

    /// <summary>
    /// Loads the stored row for the chosen scope. The editor starts EMPTY when nothing is stored at that
    /// level: pre-filling the inherited value would make saving an accidental echo of the parent.
    /// </summary>
    private void LoadEditorForScope()
    {
        if (EditRow is null)
        {
            return;
        }

        var stored = StoredFor(EditRow, EditScope);

        EditRowVersion = stored?.RowVersion;
        EditRemark = stored?.Remark;
        EditText = stored?.Value;
        EditBool = string.Equals(stored?.Value, "true", StringComparison.OrdinalIgnoreCase);
        EditNumber = decimal.TryParse(
            stored?.Value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var number)
            ? number
            : null;
    }

    protected void ClosePopup()
    {
        PopupVisible = false;
        EditError = null;
        EditRow = null;
    }

    protected async Task SaveAsync()
    {
        if (EditRow is null || !CanEdit)
        {
            return;
        }

        IsSubmitting = true;
        EditError = null;

        try
        {
            var result = await Settings.SaveAsync(new AppSettingEditVm
            {
                Module = EditRow.Module,
                Key = EditRow.Key,
                Scope = EditScope,
                CompanyCode = EditScope == AppSettingScope.Global ? null : CompanyCode,
                BranchCode = EditScope == AppSettingScope.Branch ? BranchCode : null,
                Value = ValueFromEditor(),
                RowVersion = EditRowVersion,
                Remark = EditRemark
            });

            if (!result.Succeeded)
            {
                EditError = result.Message ?? "The setting could not be saved.";
                return;
            }

            StatusMessage = $"{EditRow.Key} saved at {ScopeLabel(EditScope)} scope.";
            ClosePopup();
            await LoadAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task ClearAsync()
    {
        if (EditRow is null || !CanEdit)
        {
            return;
        }

        var stored = StoredFor(EditRow, EditScope);
        if (stored is null)
        {
            EditError = "Nothing is stored at that scope, so the default already applies.";
            return;
        }

        IsSubmitting = true;
        EditError = null;

        try
        {
            var result = await Settings.ClearAsync(new AppSettingEditVm
            {
                Module = EditRow.Module,
                Key = EditRow.Key,
                Scope = EditScope,
                CompanyCode = EditScope == AppSettingScope.Global ? null : CompanyCode,
                BranchCode = EditScope == AppSettingScope.Branch ? BranchCode : null,
                RowVersion = stored.RowVersion
            });

            if (!result.Succeeded)
            {
                EditError = result.Message ?? "The setting could not be cleared.";
                return;
            }

            StatusMessage = $"{EditRow.Key} reverted to its default ({(EditRow.DefaultValue ?? "none")}).";
            ClosePopup();
            await LoadAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    /// <summary>
    /// A date is entered as text: no shipped definition uses <see cref="AppSettingType.Date"/> yet, and a
    /// date picker is not worth adding until one does.
    /// </summary>
    private string? ValueFromEditor() => EditRow?.Type switch
    {
        AppSettingType.Flag => EditBool ? "true" : "false",
        AppSettingType.Number => EditNumber?.ToString("0.############################", CultureInfo.InvariantCulture),
        _ => EditText
    };

    // ── display helpers ─────────────────────────────────────────────────────

    protected static AppSettingScope DefaultScope(AppSettingListRow row)
    {
        if (row.AllowedScopes.HasFlag(AppSettingScope.Branch))
        {
            return AppSettingScope.Branch;
        }

        return row.AllowedScopes.HasFlag(AppSettingScope.Company)
            ? AppSettingScope.Company
            : AppSettingScope.Global;
    }

    protected static AppSettingStoredRow? StoredFor(AppSettingListRow row, AppSettingScope scope) => scope switch
    {
        AppSettingScope.Global => row.StoredGlobal,
        AppSettingScope.Company => row.StoredCompany,
        AppSettingScope.Branch => row.StoredBranch,
        _ => null
    };

    protected static string ScopeLabel(AppSettingScope scope) => scope switch
    {
        AppSettingScope.Global => "Global",
        AppSettingScope.Company => "Company",
        AppSettingScope.Branch => "Branch",
        _ => "Default"
    };

    protected static string ProvenanceLabel(AppSettingListRow row) =>
        row.IsDefault ? "Default" : ScopeLabel(row.Provenance);

    protected static string TypeLabel(AppSettingType type) => type switch
    {
        AppSettingType.Flag => "Yes / No",
        AppSettingType.Number => "Number",
        AppSettingType.Date => "Date",
        AppSettingType.Token => "Token",
        _ => "Text"
    };

    protected static string ScopesLabel(AppSettingListRow row) =>
        string.Join(
            ", ",
            AllScopes.Where(s => row.AllowedScopes.HasFlag(s)).Select(ScopeLabel));

    protected static string EffectiveValue(AppSettingListRow row) =>
        row.Type == AppSettingType.Flag
            ? (string.Equals(row.Value, "true", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No")
            : row.Value ?? "—";

    protected static string ModuleLabel(string module) => AppSettingModules.Describe(module);

    protected static string ModuleIcon(string module) => module.Trim().ToUpperInvariant() switch
    {
        AppSettingModules.Admin => "fa-solid fa-shield-halved",
        AppSettingModules.Sales => "fa-solid fa-chart-line",
        AppSettingModules.Procurement => "fa-solid fa-cart-shopping",
        AppSettingModules.Inventory => "fa-solid fa-boxes-stacked",
        AppSettingModules.Planning => "fa-solid fa-calendar-days",
        AppSettingModules.Production => "fa-solid fa-industry",
        AppSettingModules.Qa => "fa-solid fa-clipboard-check",
        _ => "fa-solid fa-sliders"
    };

    /// <summary>Turns <c>QUOTE_VALID_DAYS</c> into a readable title for the card header.</summary>
    protected static string KeyTitle(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        var parts = key.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts.Select(p =>
            p.Length <= 3 && p.All(char.IsUpper)
                ? p
                : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    protected static string ProvenanceChipClass(AppSettingListRow row) =>
        row.IsDefault
            ? "as-chip--default"
            : row.Provenance switch
            {
                AppSettingScope.Branch => "as-chip--branch",
                AppSettingScope.Company => "as-chip--company",
                AppSettingScope.Global => "as-chip--global",
                _ => "as-chip--default"
            };

    protected sealed record ScopeOption(AppSettingScope Value, string Name);

    protected sealed record TokenOption(string Value, string Name);
}
