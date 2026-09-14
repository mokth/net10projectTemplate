using DevExpress.Blazor;
using ErpWeb.Core.Admin;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Admin.Master;

public partial class MsProjectList : MsRefListPageBase<MsProjectListRow>
{
    protected override string MenuCode => MenuCodes.AdminProject;
    protected override string EntityLabel => "Project";

    protected MsProjectEditVm EditModel { get; set; } = new();
    protected IReadOnlyList<IvCodeLookupRow> CustomerLookups { get; private set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> DeptLookups { get; private set; } = [];
    protected bool LookupsLoading { get; private set; }
    protected bool LookupsLoaded { get; private set; }

    protected static IReadOnlyList<string> StatusOptions { get; } =
        [MsProjectStatus.Active, MsProjectStatus.Closed];

    /// <summary>
    /// True when the stored value predates the master — it is preserved on save unless changed.
    /// </summary>
    protected bool IsOrphanCustCode =>
        IsOrphan(EditModel.CustCode, CustomerLookups);

    protected bool IsOrphanDeptCode =>
        IsOrphan(EditModel.DeptCode, DeptLookups);

    private static bool IsOrphan(string? code, IReadOnlyList<IvCodeLookupRow> lookups)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var trimmed = code.Trim();
        return !lookups.Any(x => string.Equals(x.Code, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(MsProjectListRow.Code),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "110px"
        },
        new()
        {
            Caption = "Name",
            FieldName = nameof(MsProjectListRow.Name),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Customer",
            FieldName = nameof(MsProjectListRow.CustCode),
            DataType = "string",
            VisibleIndex = 3,
            Width = "110px"
        },
        new()
        {
            Caption = "Dept",
            FieldName = nameof(MsProjectListRow.DeptCode),
            DataType = "string",
            VisibleIndex = 4,
            Width = "100px"
        },
        new()
        {
            Caption = "Start",
            FieldName = nameof(MsProjectListRow.StartDate),
            DataType = "date",
            VisibleIndex = 5,
            Width = "110px"
        },
        new()
        {
            Caption = "End",
            FieldName = nameof(MsProjectListRow.EndDate),
            DataType = "date",
            VisibleIndex = 6,
            Width = "110px"
        },
        new()
        {
            Caption = "Status",
            FieldName = nameof(MsProjectListRow.Status),
            DataType = "string",
            VisibleIndex = 7,
            Width = "90px"
        },
        new()
        {
            Caption = "Budget",
            FieldName = nameof(MsProjectListRow.BudgetAmnt),
            DataType = "decimal",
            VisibleIndex = 8,
            Width = "110px"
        }
    ];

    protected override async Task OnPageInitializedAsync() => await ReloadListAsync();

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await MsRef.ListProjectsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load projects.";
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

    protected override string GetRowCode(MsProjectListRow row) => row.Code;

    protected override IvMasterKeyToken ToKeyToken(MsProjectListRow row) => Key(row.Code, row.RowVersion);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive) =>
        MsRef.SetProjectActiveAsync(items, isActive);

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<string> codes) =>
        MsRef.CanDeleteProjectsAsync(codes);

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items) =>
        MsRef.DeleteProjectsAsync(items);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        await EnsureLookupsAsync();
        EditModel = new MsProjectEditVm();
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(MsProjectListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access))
        {
            return;
        }

        await EnsureLookupsAsync();
        if (!await LoadEditModelAsync(row.Code))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(MsProjectListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        await EnsureLookupsAsync();
        if (!await LoadEditModelAsync(row.Code))
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
            var result = await MsRef.SaveProjectAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode
                    ? "Project updated successfully."
                    : "Project added successfully.";
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

    private async Task EnsureLookupsAsync()
    {
        if (LookupsLoaded || LookupsLoading)
        {
            return;
        }

        LookupsLoading = true;
        try
        {
            var result = await MsRef.ListActiveLookupsAsync();
            if (result.Succeeded && result.Data is not null)
            {
                DeptLookups = result.Data.Departments;
                CustomerLookups = result.Data.Customers;
                LookupsLoaded = true;
            }
        }
        finally
        {
            LookupsLoading = false;
        }
    }

    private async Task<bool> LoadEditModelAsync(string code)
    {
        var result = await MsRef.GetProjectAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load project.";
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        return true;
    }
}
