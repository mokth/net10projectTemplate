using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Masters;

public partial class SaDisGroupList : SaKeyedRefListPageBase<SaDisGroupListRow, SaDisGroupKey>
{
    [Inject] private ISaCustService CustService { get; set; } = default!;

    protected override string MenuCode => MenuCodes.SalesDisGroup;
    protected override string EntityLabel => "Discount Group";

    protected SaDisGroupEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }
    protected string NewMemberCode { get; set; } = string.Empty;

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Group Name",
            FieldName = nameof(SaDisGroupListRow.GroupName),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "160px"
        },
        new()
        {
            Caption = "Pay Code",
            FieldName = nameof(SaDisGroupListRow.PayCode),
            DataType = "string",
            VisibleIndex = 2,
            Width = "120px"
        },
        new()
        {
            Caption = "Level",
            FieldName = nameof(SaDisGroupListRow.GroupLevel),
            DataType = "number",
            VisibleIndex = 3,
            Width = "80px"
        },
        new()
        {
            Caption = "Discount",
            FieldName = nameof(SaDisGroupListRow.Discount),
            DataType = "number",
            VisibleIndex = 4,
            Width = "100px"
        },
        new()
        {
            Caption = "Status",
            FieldName = nameof(SaDisGroupListRow.GroupStatus),
            DataType = "string",
            VisibleIndex = 5,
            Width = "100px"
        },
        new()
        {
            Caption = "Members",
            FieldName = nameof(SaDisGroupListRow.MemberCount),
            DataType = "number",
            VisibleIndex = 6,
            Width = "90px"
        }
    ];

    protected override async Task OnPageInitializedAsync() => await ReloadListAsync();

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await RefService.ListDisGroupsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load discount groups.";
                Data = [];
            }
            else
            {
                Data = result.Data ?? [];
            }

            SelectedRows.Clear();
            Grid?.Reload();
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected override SaDisGroupKey ToKey(SaDisGroupListRow row) =>
        new() { GroupName = row.GroupName, PayCode = row.PayCode };

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<SaDisGroupListRow> rows) =>
        RefService.CanDeleteDisGroupsAsync(rows.Select(ToKey).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<SaDisGroupKey> keys) =>
        RefService.DeleteDisGroupsAsync(keys);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new SaDisGroupEditVm { Members = [] };
        NewMemberCode = string.Empty;
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(SaDisGroupListRow row)
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

    protected override async Task OnEditClickAsync(SaDisGroupListRow row)
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

    protected async Task SwitchViewToEditAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        EditEnabled = true;
        CanEditFromView = false;
    }

    protected void RemoveMemberRow(SaDisGroupMemberVm row)
    {
        EditModel.Members.Remove(row);
    }

    protected async Task AddMemberAsync()
    {
        var code = NewMemberCode.Trim();
        if (code.Length == 0)
        {
            ErrorMessage = "Enter a customer code.";
            return;
        }

        if (EditModel.Members.Any(m => string.Equals(m.CustCode, code, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = "Customer is already in the member list.";
            return;
        }

        var result = await CustService.GetAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            ErrorMessage = result.Message ?? "Customer not found.";
            return;
        }

        EditModel.Members.Add(new SaDisGroupMemberVm
        {
            CustCode = result.Data.CustCode,
            CustName = result.Data.CustName ?? result.Data.CustCode
        });
        NewMemberCode = string.Empty;
        ErrorMessage = null;
    }

    protected async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await RefService.SaveDisGroupAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Discount group updated successfully." : "Discount group added successfully.";
                await ReloadListAsync();
            }
            else
            {
                ErrorMessage = SaRefListMessages.FormatResultMessage(result);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task<bool> LoadEditModelAsync(SaDisGroupListRow row)
    {
        var result = await RefService.GetDisGroupAsync(ToKey(row));
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load discount group.";
            return false;
        }

        EditModel = result.Data;
        EditModel.Members ??= [];
        NewMemberCode = string.Empty;
        ErrorMessage = null;
        return true;
    }
}
