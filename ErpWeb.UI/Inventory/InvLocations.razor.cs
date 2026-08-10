using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Entities;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Inventory;

public partial class InvLocations : PageBase
{
    [Inject] private IWarehouseLocationService Service { get; set; } = default!;
    [Inject] private IWarehouseService WarehouseService { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private DxGrid? _grid;
    protected bool PopupVisible, IsEditMode, IsLoading = true, IsSubmitting, editEnable;
    protected string? StatusMessage;
    protected WarehouseLocation selectedRow { get; set; } = new() { IsActive = true };
    protected IEnumerable<WarehouseLocation>? Data;
    protected List<Warehouse> WarehouseOptions { get; set; } = [];
    private readonly List<WarehouseLocation> _selected = [];

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "WarehouseId", FieldName = nameof(WarehouseLocation.WarehouseId), DataType = "long", VisibleIndex = 1, Width = "110px" },
        new() { Caption = "Code", FieldName = nameof(WarehouseLocation.LocationCode), DataType = "string", SortIndex = 0, SortOrder = GridColumnSortOrder.Ascending, VisibleIndex = 2, Width = "120px" },
        new() { Caption = "Name", FieldName = nameof(WarehouseLocation.LocationName), DataType = "string", VisibleIndex = 3 },
        new() { Caption = "Active", FieldName = nameof(WarehouseLocation.IsActive), DataType = "bool", VisibleIndex = 4, Width = "80px" }
    ];

    public List<ButtonInfo> Buttons { get; set; } =
    [
        new() { IConClass = "fas fa-plus", Style = "primary", Text = "New" },
        new() { IConClass = "far fa-trash-alt", Style = "danger", Text = "Delete" }
    ];

    public List<ButtonInfo> ActionButtons { get; set; } =
    [
        new() { IConClass = "fa-regular fa-eye", Style = "primary", Text = "View" },
        new() { IConClass = "far fa-edit", Style = "primary", Text = "Edit" }
    ];

    protected override async Task OnPageInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        IsLoading = true;
        var wh = await WarehouseService.GetAsync();
        WarehouseOptions = wh.Succeeded ? wh.Items.Where(w => w.IsActive).ToList() : [];
        var result = await Service.GetAsync();
        StatusMessage = result.Succeeded ? null : result.ErrorMessage;
        Data = result.Succeeded ? result.Items : [];
        IsLoading = false;
        _selected.Clear();
        _grid?.Reload();
    }

    public void OnCancel() { PopupVisible = false; ErrorMessage = string.Empty; IsSubmitting = false; }
    public void OnGridInstance(DxGrid g) => _grid = g;
    public void OnSelectionsEvent(IReadOnlyList<object> rows)
    {
        _selected.Clear();
        _selected.AddRange(rows.OfType<WarehouseLocation>());
    }

    public async Task OnActionClick(SelectedButtonInfo<WarehouseLocation> info)
    {
        if (info.SelectedRow is null) return;
        var view = info.SelectedButton.Text.Equals("View", StringComparison.OrdinalIgnoreCase);
        if (!await AccessRights.CanAsync(MenuCodes.InvLocations, view ? PermissionCodes.Access : PermissionCodes.Edit))
        { StatusMessage = "Access Denied!!"; return; }
        selectedRow = Clone(info.SelectedRow);
        IsEditMode = true; editEnable = !view; PopupVisible = true; ErrorMessage = string.Empty;
    }

    public async Task OnButtonClick(SelectedButtonInfo<WarehouseLocation> info)
    {
        if (info.SelectedButton.Text.Equals("New", StringComparison.OrdinalIgnoreCase))
        {
            if (!await AccessRights.CanAsync(MenuCodes.InvLocations, PermissionCodes.Add)) { StatusMessage = "Access Denied!!"; return; }
            if (WarehouseOptions.Count == 0) { StatusMessage = "Create a warehouse first."; return; }
            selectedRow = new WarehouseLocation { IsActive = true, WarehouseId = WarehouseOptions[0].Id };
            IsEditMode = false; editEnable = true; PopupVisible = true; ErrorMessage = string.Empty;
            return;
        }
        if (info.SelectedButton.Text.Equals("Delete", StringComparison.OrdinalIgnoreCase))
        {
            if (!await AccessRights.CanAsync(MenuCodes.InvLocations, PermissionCodes.Delete)) { StatusMessage = "Access Denied!!"; return; }
            if (_selected.Count == 0) { StatusMessage = "No Record Selected!"; return; }
            if (!await Js.InvokeAsync<bool>("confirm", $"Delete {_selected.Count} location(s)?")) return;
            var result = await Service.DeleteAsync(_selected.Select(x => x.Id).ToList());
            StatusMessage = result.Succeeded ? "Deleted." : result.ErrorMessage;
            await LoadAsync();
        }
    }

    protected async Task HandleValidSubmit()
    {
        if (IsSubmitting) return;
        IsSubmitting = true;
        try
        {
            var result = IsEditMode ? await Service.UpdateAsync(selectedRow) : await Service.AddAsync(selectedRow);
            if (!result.Succeeded) { ErrorMessage = result.ErrorMessage; return; }
            PopupVisible = false;
            StatusMessage = IsEditMode ? "Updated." : "Created.";
            await LoadAsync();
        }
        finally { IsSubmitting = false; }
    }

    private static WarehouseLocation Clone(WarehouseLocation s) => new()
    {
        Id = s.Id, CompanyId = s.CompanyId, BranchId = s.BranchId, WarehouseId = s.WarehouseId,
        LocationCode = s.LocationCode, LocationName = s.LocationName, IsActive = s.IsActive, RowVersion = s.RowVersion
    };
}
