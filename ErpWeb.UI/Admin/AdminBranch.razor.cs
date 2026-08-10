using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Admin;

public partial class AdminBranch : PageBase
{
    [Inject] private IBranchService BranchAdmin { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    private DxGrid? _grid;

    protected bool PopupVisible;
    protected Branch selectedRow { get; set; } = new();
    protected bool IsEditMode;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool editEnable;
    protected string? StatusMessage;

    protected IEnumerable<Branch>? Data;
    private readonly List<Branch> _selectedRows = [];

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Branch Code",
            FieldName = nameof(Branch.BranchCode),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "120px"
        },
        new()
        {
            Caption = "Branch Name",
            FieldName = nameof(Branch.BranchName),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(Branch.IsActive),
            DataType = "bool",
            VisibleIndex = 3,
            Width = "80px"
        },
        new()
        {
            Caption = "Created By",
            FieldName = nameof(Branch.CreatedBy),
            DataType = "string",
            VisibleIndex = 4,
            Width = "100px"
        },
        new()
        {
            Caption = "Created On",
            FieldName = nameof(Branch.CreatedAtUtc),
            DataType = "datetime",
            VisibleIndex = 5,
            Visible = false,
            Width = "120px"
        }
    ];

    public List<ButtonInfo> Buttons { get; set; } =
    [
        new()
        {
            IConClass = "fas fa-plus",
            Style = "primary",
            Text = "New"
        },
        new()
        {
            IConClass = "far fa-trash-alt",
            Style = "danger",
            Text = "Delete",
            ToolTip = "Delete Branch"
        }
    ];

    public List<ButtonInfo> ActionButtons { get; set; } =
    [
        new()
        {
            IConClass = "fa-regular fa-eye",
            Style = "primary",
            Text = "View",
            ToolTip = "View Branch"
        },
        new()
        {
            IConClass = "far fa-edit",
            Style = "primary",
            Text = "Edit",
            ToolTip = "Edit Branch"
        }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        await LoadPageDataAsync();
    }

    private async Task LoadPageDataAsync()
    {
        IsLoading = true;
        StatusMessage = null;
        ErrorMessage = string.Empty;

        var result = await BranchAdmin.GetBranchesAsync();
        if (!result.Succeeded)
        {
            StatusMessage = result.ErrorMessage ?? "Unable to load branches.";
            Data = [];
        }
        else
        {
            Data = result.Branches;
            selectedRow = Data.FirstOrDefault() ?? new Branch();
        }

        IsLoading = false;
        _selectedRows.Clear();
        _grid?.Reload();
    }

    public void OnCancel()
    {
        PopupVisible = false;
        ErrorMessage = string.Empty;
        IsSubmitting = false;
    }

    public async Task OnActionClick(SelectedButtonInfo<Branch> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return;
        }

        switch (info.SelectedButton.Text.ToLowerInvariant())
        {
            case "edit":
                await OnEditClick(info.SelectedRow);
                break;
            case "view":
                await OnViewClick(info.SelectedRow);
                break;
        }
    }

    public async Task OnButtonClick(SelectedButtonInfo<Branch> info)
    {
        switch (info.SelectedButton.Text.ToLowerInvariant())
        {
            case "new":
                await OnAddNewClick();
                break;
            case "delete":
                await OnDeleteClick();
                break;
        }
    }

    public async Task OnAddNewClick()
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminBranch, PermissionCodes.Add))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        selectedRow = new Branch { IsActive = true };
        ErrorMessage = string.Empty;
        IsEditMode = false;
        PopupVisible = true;
        editEnable = true;
    }

    public async Task OnEditClick(Branch data)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminBranch, PermissionCodes.Edit))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        selectedRow = CloneForEdit(data);
        ErrorMessage = string.Empty;
        IsEditMode = true;
        PopupVisible = true;
        editEnable = true;
    }

    protected async Task OnViewClick(Branch data)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminBranch, PermissionCodes.Access))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        selectedRow = CloneForEdit(data);
        ErrorMessage = string.Empty;
        IsEditMode = true;
        editEnable = false;
        PopupVisible = true;
    }

    public void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    public async Task OnDeleteClick()
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminBranch, PermissionCodes.Delete))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        if (_selectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return;
        }

        var confirmed = await JsRuntime.InvokeAsync<bool>(
            "confirm",
            $"Delete {_selectedRows.Count} selected branch(es)? HQ cannot be deleted.");
        if (!confirmed)
        {
            return;
        }

        var result = await BranchAdmin.DeleteBranchesAsync(_selectedRows.Select(b => b.Id).ToList());
        StatusMessage = result.Succeeded
            ? "Branch(es) deleted."
            : result.ErrorMessage ?? "Delete failed.";
        await LoadPageDataAsync();
    }

    public void OnSelectionsEvent(IReadOnlyList<object> rows)
    {
        _selectedRows.Clear();
        foreach (var row in rows.OfType<Branch>())
        {
            _selectedRows.Add(row);
        }
    }

    protected async Task HandleValidSubmit()
    {
        if (IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = string.Empty;
        try
        {
            var result = IsEditMode
                ? await BranchAdmin.UpdateBranchAsync(selectedRow)
                : await BranchAdmin.AddBranchAsync(selectedRow);

            if (!result.Succeeded)
            {
                ErrorMessage = result.ErrorMessage ?? "Save failed.";
                return;
            }

            PopupVisible = false;
            StatusMessage = IsEditMode ? "Branch updated." : "Branch created.";
            await LoadPageDataAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private static Branch CloneForEdit(Branch source) =>
        new()
        {
            Id = source.Id,
            CompanyId = source.CompanyId,
            BranchCode = source.BranchCode,
            BranchName = source.BranchName,
            IsActive = source.IsActive,
            CreatedAtUtc = source.CreatedAtUtc,
            CreatedBy = source.CreatedBy,
            ModifiedAtUtc = source.ModifiedAtUtc,
            ModifiedBy = source.ModifiedBy,
            RowVersion = source.RowVersion
        };
}
