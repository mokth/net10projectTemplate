using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Admin;

public partial class AdminRoleMenuPermission : PageBase
{
    [Inject] private IRoleMenuPermissionAdminService RoleMenuPermissionAdmin { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    protected bool IsLoading = true;
    protected bool IsMatrixLoading;
    protected bool IsSubmitting;
    protected bool CanEditMatrix;
    protected string? StatusMessage;

    protected IEnumerable<RoleOptionAdminRow> roleOptions { get; set; } = [];
    protected IEnumerable<MenuOptionRow> moduleOptions { get; set; } = [];

    protected int SelectedRoleId { get; set; }
    protected int SelectedModuleId { get; set; }

    private int _loadedRoleId;
    private int _loadedModuleId;

    protected List<RolePermissionMatrixColumn> matrixColumns { get; set; } = [];
    protected List<RolePermissionMatrixRow> matrixRows { get; set; } = [];

    /// <summary>Baseline grants for the selected role (MenuId, PermissionId) → allowed.</summary>
    private readonly Dictionary<(int MenuId, int PermissionId), bool> _baseline = new();

    /// <summary>Pending edits over the baseline.</summary>
    private readonly Dictionary<(int MenuId, int PermissionId), bool> _pending = new();

    protected bool HasUnsavedChanges => _pending.Count > 0;

    protected string? SelectedRoleLabel =>
        roleOptions.FirstOrDefault(r => r.RoleId == SelectedRoleId)?.RoleCode;

    protected override async Task OnPageInitializedAsync()
    {
        CanEditMatrix =
            await AccessRights.CanAsync(MenuCodes.AdminRolePermissions, PermissionCodes.Edit)
            || await AccessRights.CanAsync(MenuCodes.AdminRolePermissions, PermissionCodes.Add);

        await LoadLookupsAsync();
    }

    private async Task LoadLookupsAsync()
    {
        IsLoading = true;
        StatusMessage = null;
        ErrorMessage = string.Empty;

        var lookups = await RoleMenuPermissionAdmin.GetLookupsAsync();
        if (!lookups.Succeeded)
        {
            StatusMessage = lookups.ErrorMessage ?? "Unable to load role permissions.";
            roleOptions = [];
            moduleOptions = [];
            IsLoading = false;
            return;
        }

        roleOptions = lookups.Roles;
        moduleOptions = lookups.Modules.Count > 0 ? lookups.Modules : lookups.Menus;

        SelectedRoleId = roleOptions.FirstOrDefault()?.RoleId ?? 0;
        SelectedModuleId = moduleOptions.FirstOrDefault()?.MenuId ?? 0;

        IsLoading = false;

        if (SelectedRoleId > 0)
        {
            await LoadRoleGrantsAsync();
            _loadedRoleId = SelectedRoleId;
        }

        if (SelectedRoleId > 0 && SelectedModuleId > 0)
        {
            await LoadMatrixAsync();
            _loadedModuleId = SelectedModuleId;
        }
    }

    private async Task LoadRoleGrantsAsync()
    {
        _baseline.Clear();
        _pending.Clear();

        if (SelectedRoleId <= 0)
        {
            return;
        }

        var result = await RoleMenuPermissionAdmin.GetRoleGrantsAsync(SelectedRoleId);
        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to load role grants.";
            return;
        }

        foreach (var grant in result.Grants)
        {
            // Matrix treats "granted" as checked; explicit denies show unchecked and are cleared on save.
            if (grant.IsAllowed)
            {
                _baseline[(grant.MenuId, grant.PermissionId)] = true;
            }
        }
    }

    private async Task LoadMatrixAsync()
    {
        if (SelectedModuleId <= 0)
        {
            matrixColumns = [];
            matrixRows = [];
            return;
        }

        IsMatrixLoading = true;
        ErrorMessage = string.Empty;

        var result = await RoleMenuPermissionAdmin.GetMatrixAsync(SelectedModuleId);
        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to load module permissions.";
            matrixColumns = [];
            matrixRows = [];
            IsMatrixLoading = false;
            return;
        }

        matrixColumns = result.MatrixColumns.ToList();
        matrixRows = result.MatrixRows.ToList();
        IsMatrixLoading = false;
    }

    protected bool IsGranted(int menuId, int permissionId)
    {
        var key = (menuId, permissionId);
        if (_pending.TryGetValue(key, out var pending))
        {
            return pending;
        }

        return _baseline.TryGetValue(key, out var baseline) && baseline;
    }

    protected void OnGrantChanged(int menuId, int permissionId, bool value)
    {
        if (!CanEditMatrix || IsSubmitting)
        {
            return;
        }

        var key = (menuId, permissionId);
        var baseline = _baseline.TryGetValue(key, out var b) && b;

        if (value == baseline)
        {
            _pending.Remove(key);
        }
        else
        {
            _pending[key] = value;
        }
    }

    protected void SetRowGrants(int menuId, bool allowed)
    {
        if (!CanEditMatrix || IsSubmitting || matrixColumns.Count == 0)
        {
            return;
        }

        foreach (var column in matrixColumns)
        {
            OnGrantChanged(menuId, column.PermissionId, allowed);
        }
    }

    protected void SetAllVisibleGrants(bool allowed)
    {
        if (!CanEditMatrix || IsSubmitting || matrixRows.Count == 0 || matrixColumns.Count == 0)
        {
            return;
        }

        foreach (var row in matrixRows)
        {
            foreach (var column in matrixColumns)
            {
                OnGrantChanged(row.MenuId, column.PermissionId, allowed);
            }
        }
    }

    protected async Task OnRoleSelectionChangedAsync()
    {
        if (SelectedRoleId == _loadedRoleId)
        {
            return;
        }

        if (HasUnsavedChanges)
        {
            var confirmed = await JsRuntime.InvokeAsync<bool>(
                "confirm",
                "You have unsaved permission changes. Discard them and switch role?");
            if (!confirmed)
            {
                SelectedRoleId = _loadedRoleId;
                await InvokeAsync(StateHasChanged);
                return;
            }
        }

        StatusMessage = null;
        ErrorMessage = string.Empty;
        await LoadRoleGrantsAsync();
        _loadedRoleId = SelectedRoleId;
        await LoadMatrixAsync();
        _loadedModuleId = SelectedModuleId;
    }

    protected async Task OnModuleSelectionChangedAsync()
    {
        if (SelectedModuleId == _loadedModuleId)
        {
            return;
        }

        StatusMessage = null;
        ErrorMessage = string.Empty;
        await LoadMatrixAsync();
        _loadedModuleId = SelectedModuleId;
    }

    protected async Task OnSaveAsync()
    {
        if (!CanEditMatrix || IsSubmitting || !HasUnsavedChanges)
        {
            return;
        }

        if (SelectedRoleId <= 0)
        {
            ErrorMessage = "Select a role first.";
            return;
        }

        IsSubmitting = true;
        ErrorMessage = string.Empty;
        StatusMessage = null;

        var changes = _pending
            .Select(kv => new RolePermissionMatrixChange
            {
                MenuId = kv.Key.MenuId,
                PermissionId = kv.Key.PermissionId,
                IsAllowed = kv.Value
            })
            .ToList();

        var result = await RoleMenuPermissionAdmin.SaveMatrixAsync(SelectedRoleId, changes);
        if (result.Succeeded)
        {
            StatusMessage = "Role permissions saved successfully.";
            await LoadRoleGrantsAsync();
        }
        else
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to save role permissions.";
        }

        IsSubmitting = false;
    }

    protected async Task OnCancelAsync()
    {
        if (!HasUnsavedChanges || IsSubmitting)
        {
            return;
        }

        var confirmed = await JsRuntime.InvokeAsync<bool>(
            "confirm",
            "Discard all unsaved permission changes?");
        if (!confirmed)
        {
            return;
        }

        _pending.Clear();
        StatusMessage = null;
        ErrorMessage = string.Empty;
        await InvokeAsync(StateHasChanged);
    }

    protected static string GetIndentedName(RolePermissionMatrixRow row)
    {
        if (row.Depth <= 0)
        {
            return row.MenuName;
        }

        return $"{new string('\u00A0', row.Depth * 4)}{row.MenuName}";
    }
}
