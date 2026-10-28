using DevExpress.Blazor;
using ErpWeb.Core.Admin;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Common.Lookups;

/// <summary>
/// Shared maintenance surface for the approved <c>AdLookupValue</c> families.
///
/// <para>
/// One instance serves the all-family admin screen (<c>/admin/lookups</c>) and every per-family module
/// screen. The routable wrapper is responsible for authorization via <c>MenuAuthorize</c> on the
/// family's own menu code; this component only decides what to query and how to render it, and it
/// refuses to query when <see cref="MenuCode"/> and <see cref="LookupType"/> do not form a registered,
/// maintainable pair.
/// </para>
///
/// <para>
/// It inherits <see cref="ComponentBase"/>, not <c>PageBase</c>, on purpose: it is not routable, so it
/// must never become a route that escapes the ErpAccess policy.
/// </para>
/// </summary>
public partial class AdLookupMaintenance : ComponentBase
{
    [Inject] private IAdLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    /// <summary>The family's own menu code (or <c>ADMIN_LOOKUPS</c> for the all-family screen).</summary>
    [Parameter, EditorRequired] public string MenuCode { get; set; } = string.Empty;

    /// <summary>Fixed family for a module screen; null on the all-family admin screen.</summary>
    [Parameter] public string? LookupType { get; set; }

    [Parameter] public string Eyebrow { get; set; } = "Admin";

    [Parameter] public string Heading { get; set; } = "Lookup Values";

    /// <summary>True only on the admin screen, where the operator picks the family.</summary>
    [Parameter] public bool ShowFamilySelector { get; set; }

    /// <summary>
    /// Persisted grid-layout key. The admin screen keeps <c>ad-lookup-list</c> so existing saved
    /// layouts survive; module screens default to <c>ad-lookup-{family}</c>.
    /// </summary>
    [Parameter] public string? GridKey { get; set; }

    [Parameter] public string PopupWidth { get; set; } = "50vw";

    private DxGrid? Grid;
    private bool PopupVisible;
    private bool IsEditMode;
    private bool EditEnabled;
    private bool CanEditFromView;
    private bool IsLoading = true;
    private bool IsSubmitting;
    private string? StatusMessage;
    private string? ErrorMessage;
    private IEnumerable<AdLookupRow>? Data;
    private readonly List<AdLookupRow> SelectedRows = [];

    /// <summary>Monotonic load token: a response whose family is no longer current is discarded.</summary>
    private int _loadSequence;

    private string? _initializedKey;
    private string? _guardError;

    protected string? SelectedLookupType { get; set; }

    protected AdLookupEditVm EditModel { get; set; } = new();

    protected IReadOnlyList<AdLookupTypeDefinition> LookupTypes => AdLookupTypeRegistry.Maintainable;

    private AdLookupTypeDefinition? FixedDefinition => AdLookupTypeRegistry.Find(LookupType);

    private AdLookupTypeDefinition? EditDefinition =>
        AdLookupTypeRegistry.Find(EditModel.LookupType) ?? AdLookupTypeRegistry.Find(SelectedLookupType);

    protected string GridKeyValue =>
        !string.IsNullOrWhiteSpace(GridKey)
            ? GridKey
            : FixedDefinition is { } definition
                ? $"ad-lookup-{definition.Code}"
                : "ad-lookup-list";

    protected List<ButtonInfo> ToolbarButtons { get; } =
    [
        new() { IConClass = "fas fa-plus", Style = "primary", Text = "NEW" },
        new() { IConClass = "fa-solid fa-check", Style = "success", Text = "ACTIVATE", ToolTip = "Activate selected" },
        new() { IConClass = "fa-solid fa-ban", Style = "warning", Text = "DEACTIVATE", ToolTip = "Deactivate selected" },
        new() { IConClass = "fa-solid fa-rotate", Style = "primary", Text = "REFRESH", ToolTip = "Reload" }
    ];

    protected List<ButtonInfo> RowActionButtons { get; } =
    [
        new() { IConClass = "fa-regular fa-eye", Style = "primary", Text = "VIEW", ToolTip = "View" },
        new() { IConClass = "far fa-edit", Style = "primary", Text = "EDIT", ToolTip = "Edit" }
    ];

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(AdLookupRow.Code),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "180px"
        },
        new()
        {
            Caption = "Description",
            FieldName = nameof(AdLookupRow.Description),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Sort",
            FieldName = nameof(AdLookupRow.SortOrder),
            DataType = "number",
            VisibleIndex = 3,
            Width = "80px"
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(AdLookupRow.IsActive),
            DataType = "bool",
            VisibleIndex = 4,
            Width = "80px"
        },
        ..AuditColumns.For(startVisibleIndex: 5)
    ];

    /// <summary>
    /// Initialises once per (menu, family) pair. Re-running for the same pair would wipe an in-progress
    /// edit on every parent render, so the pair is remembered rather than the render count.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        var key = $"{MenuCode}|{LookupType}";
        if (string.Equals(_initializedKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _initializedKey = key;
        ResetForFamilyChange();

        _guardError = ValidatePlacement();
        if (_guardError is not null)
        {
            Data = [];
            IsLoading = false;
            return;
        }

        SelectedLookupType = !ShowFamilySelector && FixedDefinition is { } definition
            ? definition.Code
            : AdLookupTypeRegistry.Maintainable.FirstOrDefault()?.Code;

        await ReloadListAsync();
    }

    /// <summary>
    /// A supplied family is only honoured when it is registered, maintainable, and actually owned by
    /// the menu code that guards this instance. Anything else renders a refusal, never another
    /// family's values.
    /// </summary>
    private string? ValidatePlacement()
    {
        if (string.IsNullOrWhiteSpace(MenuCode))
        {
            return "This screen is not configured for a lookup family.";
        }

        if (string.IsNullOrWhiteSpace(LookupType))
        {
            return ShowFamilySelector
                ? null
                : "This screen is not configured for a lookup family.";
        }

        var definition = AdLookupTypeRegistry.Find(LookupType);
        if (definition is not { UserMaintainable: true })
        {
            return "This lookup family is not available for maintenance.";
        }

        return string.Equals(definition.MaintenanceMenuCode, MenuCode, StringComparison.OrdinalIgnoreCase)
            ? null
            : "This lookup family is not available for maintenance.";
    }

    /// <summary>
    /// Clears every piece of per-family state, so a pending popup, selection, error or in-flight load
    /// from the previous family can never appear against the newly selected one.
    /// </summary>
    private void ResetForFamilyChange()
    {
        _loadSequence++;
        PopupVisible = false;
        IsEditMode = false;
        EditEnabled = false;
        CanEditFromView = false;
        IsSubmitting = false;
        EditModel = new AdLookupEditVm();
        SelectedRows.Clear();
        Data = null;
        StatusMessage = null;
        ErrorMessage = null;
        IsLoading = true;
    }

    protected async Task OnLookupTypeChanged(string? lookupType)
    {
        if (string.Equals(SelectedLookupType, lookupType, StringComparison.Ordinal))
        {
            return;
        }

        SelectedLookupType = lookupType;
        ResetForFamilyChange();
        await ReloadListAsync();
    }

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError() => ErrorMessage = null;

    protected void OnGridInstance(DxGrid grid) => Grid = grid;

    protected void ClosePopup()
    {
        PopupVisible = false;
        ErrorMessage = null;
        IsSubmitting = false;
    }

    protected void OnSelectionsEvent(List<AdLookupRow> list)
    {
        SelectedRows.Clear();
        if (Data is null)
        {
            return;
        }

        SelectedRows.AddRange(list.Where(item => Data.Contains(item)));
    }

    protected async Task OnToolbarButtonAsync(SelectedButtonInfo<AdLookupRow> info)
    {
        switch ((info.SelectedButton.Text ?? string.Empty).ToUpperInvariant())
        {
            case "NEW":
                await OnNewClickAsync();
                break;
            case "ACTIVATE":
                await SetActiveBulkAsync(isActive: true);
                break;
            case "DEACTIVATE":
                await SetActiveBulkAsync(isActive: false);
                break;
            case "REFRESH":
                await ReloadListAsync();
                break;
        }
    }

    protected async Task OnRowActionAsync(SelectedButtonInfo<AdLookupRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return;
        }

        switch ((info.SelectedButton.Text ?? string.Empty).ToUpperInvariant())
        {
            case "VIEW":
                await OnViewClickAsync(info.SelectedRow);
                break;
            case "EDIT":
                await OnEditClickAsync(info.SelectedRow);
                break;
        }
    }

    protected async Task ReloadListAsync()
    {
        var familyCode = SelectedLookupType;

        if (string.IsNullOrWhiteSpace(familyCode))
        {
            Data = [];
            IsLoading = false;
            return;
        }

        var sequence = ++_loadSequence;
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var rows = await Lookups.ListForMaintenanceAsync(familyCode);

            // A newer family owns the grid now: drop this response instead of overwriting it.
            if (sequence != _loadSequence)
            {
                return;
            }

            Data = rows;
            SelectedRows.Clear();
            Grid?.Reload();
        }
        finally
        {
            if (sequence == _loadSequence)
            {
                IsLoading = false;
            }
        }
    }

    private async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        var definition = AdLookupTypeRegistry.Find(SelectedLookupType);
        if (definition is not { UserMaintainable: true })
        {
            StatusMessage = "Select a lookup type first.";
            return;
        }

        EditModel = new AdLookupEditVm
        {
            LookupType = definition.Code,
            IsActive = true,
            SortOrder = NextSortOrder(),
            MaxCodeLength = definition.MaxCodeLength
        };
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    private async Task OnViewClickAsync(AdLookupRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access))
        {
            return;
        }

        if (!await LoadEditModelAsync(row))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        PopupVisible = true;
    }

    private async Task OnEditClickAsync(AdLookupRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        if (!await LoadEditModelAsync(row))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    private async Task SwitchViewToEdit()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        EditEnabled = true;
        CanEditFromView = false;
    }

    private async Task<bool> LoadEditModelAsync(AdLookupRow row)
    {
        var result = await Lookups.GetAsync(row.Uid);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = FormatResult(result);
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        return true;
    }

    private async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await Lookups.SaveAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Lookup value updated successfully." : "Lookup value added successfully.";
                await ReloadListAsync();
            }
            else
            {
                ErrorMessage = FormatResult(result);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task SetActiveBulkAsync(bool isActive)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        if (SelectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return;
        }

        if (IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var tokens = SelectedRows
                .Select(r => new AdLookupKeyToken { Uid = r.Uid, RowVersion = r.RowVersion })
                .ToList();
            var result = await Lookups.SetActiveAsync(tokens, isActive);
            if (result.Succeeded)
            {
                StatusMessage = isActive
                    ? "Lookup value(s) activated."
                    : "Lookup value(s) deactivated.";
                await ReloadListAsync();
            }
            else
            {
                ErrorMessage = FormatResult(result);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    /// <summary>
    /// UI-side check only, and always against the menu code the wrapper supplied — never the
    /// all-family admin code, so a module screen cannot enable controls the operator has no grant for.
    /// The server re-checks against the persisted family regardless.
    /// </summary>
    private async Task<bool> EnsurePermissionAsync(string permissionCode)
    {
        if (await AccessRights.CanAsync(MenuCode, permissionCode))
        {
            return true;
        }

        StatusMessage = "Access Denied!!";
        return false;
    }

    private int NextSortOrder()
    {
        var max = Data?.Select(x => x.SortOrder).DefaultIfEmpty(0).Max() ?? 0;
        return max + AdLookupDefaults.SeedSortStep;
    }

    private static string FormatResult<T>(IvMasterOperationResult<T> result) =>
        result.ValidationErrors.Count > 0
            ? string.Join(" ", result.ValidationErrors.Select(kv => $"{kv.Key}: {kv.Value}"))
            : result.Message ?? "Operation failed.";
}
