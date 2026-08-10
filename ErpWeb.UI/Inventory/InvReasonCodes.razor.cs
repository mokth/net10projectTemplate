using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Entities;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Inventory;

public partial class InvReasonCodes : PageBase
{
    [Inject] private IReasonCodeService Service { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private DxGrid? _grid;
    protected bool PopupVisible, IsEditMode, IsLoading = true, IsSubmitting, editEnable;
    protected string? StatusMessage;
    protected ReasonCode selectedRow { get; set; } = new() { IsActive = true, AppliesTo = "SA" };
    protected IEnumerable<ReasonCode>? Data;
    private readonly List<ReasonCode> _selected = [];

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Code", FieldName = nameof(ReasonCode.ReasonCodeValue), DataType = "string", SortIndex = 0, SortOrder = GridColumnSortOrder.Ascending, VisibleIndex = 1, Width = "120px" },
        new() { Caption = "Name", FieldName = nameof(ReasonCode.ReasonName), DataType = "string", VisibleIndex = 2 },
        new() { Caption = "Applies To", FieldName = nameof(ReasonCode.AppliesTo), DataType = "string", VisibleIndex = 3, Width = "120px" },
        new() { Caption = "Active", FieldName = nameof(ReasonCode.IsActive), DataType = "bool", VisibleIndex = 4, Width = "80px" }
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
        _selected.AddRange(rows.OfType<ReasonCode>());
    }

    public async Task OnActionClick(SelectedButtonInfo<ReasonCode> info)
    {
        if (info.SelectedRow is null) return;
        var view = info.SelectedButton.Text.Equals("View", StringComparison.OrdinalIgnoreCase);
        if (!await AccessRights.CanAsync(MenuCodes.InvReasonCodes, view ? PermissionCodes.Access : PermissionCodes.Edit))
        { StatusMessage = "Access Denied!!"; return; }
        selectedRow = Clone(info.SelectedRow);
        IsEditMode = true; editEnable = !view; PopupVisible = true; ErrorMessage = string.Empty;
    }

    public async Task OnButtonClick(SelectedButtonInfo<ReasonCode> info)
    {
        if (info.SelectedButton.Text.Equals("New", StringComparison.OrdinalIgnoreCase))
        {
            if (!await AccessRights.CanAsync(MenuCodes.InvReasonCodes, PermissionCodes.Add)) { StatusMessage = "Access Denied!!"; return; }
            selectedRow = new ReasonCode { IsActive = true, AppliesTo = "SA" };
            IsEditMode = false; editEnable = true; PopupVisible = true; ErrorMessage = string.Empty;
            return;
        }
        if (info.SelectedButton.Text.Equals("Delete", StringComparison.OrdinalIgnoreCase))
        {
            if (!await AccessRights.CanAsync(MenuCodes.InvReasonCodes, PermissionCodes.Delete)) { StatusMessage = "Access Denied!!"; return; }
            if (_selected.Count == 0) { StatusMessage = "No Record Selected!"; return; }
            if (!await Js.InvokeAsync<bool>("confirm", $"Delete {_selected.Count} reason code(s)?")) return;
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

    private static ReasonCode Clone(ReasonCode s) => new()
    {
        Id = s.Id, CompanyId = s.CompanyId, ReasonCodeValue = s.ReasonCodeValue,
        ReasonName = s.ReasonName, AppliesTo = s.AppliesTo, IsActive = s.IsActive, RowVersion = s.RowVersion
    };
}
