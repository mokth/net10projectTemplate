using DevExpress.Blazor;
using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Masters;

public partial class IvUomList : IvRefListPageBase<IvUomListRow>
{
    protected override string MenuCode => MenuCodes.InventoryUom;
    protected override string EntityLabel => "UOM";

    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;

    protected IvUomEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }

    /// <summary>LHDN UNECE unit codes offered by the UneceUom picker (global MsLHDNUOM table).</summary>
    protected IReadOnlyList<IvCodeLookupRow> UneceUomOptions => _uneceUomOptions;

    protected bool IsLoadingUneceUoms { get; set; }

    private readonly List<IvCodeLookupRow> _uneceUomOptions = [];

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(IvUomListRow.Code),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "100px"
        },
        new()
        {
            Caption = "Desc",
            FieldName = nameof(IvUomListRow.Desc),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "UneceUom",
            FieldName = nameof(IvUomListRow.UneceUom),
            DataType = "string",
            VisibleIndex = 3,
            Width = "110px"
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(IvUomListRow.IsActive),
            DataType = "bool",
            VisibleIndex = 4,
            Width = "80px"
        },
        ..AuditColumns.For(startVisibleIndex: 5)
    ];

    protected override async Task OnPageInitializedAsync()
    {
        await LoadUneceUomOptionsAsync();
        await ReloadListAsync();
    }

    private async Task LoadUneceUomOptionsAsync()
    {
        IsLoadingUneceUoms = true;
        try
        {
            var result = await Lookups.ListLhdnUomsAsync();
            _uneceUomOptions.Clear();
            if (result.Succeeded)
            {
                _uneceUomOptions.AddRange(result.Rows);
            }
        }
        finally
        {
            IsLoadingUneceUoms = false;
        }
    }

    /// <summary>
    /// A value already on the row (legacy free text, or a code since removed from MsLHDNUOM) is not in
    /// the picker list yet is the row's real value. Keep it visible instead of showing an empty combo,
    /// and the service tolerates saving it unchanged. The same guard covers the H87 default when the
    /// reference table has not been loaded.
    /// </summary>
    private void EnsureUneceUomOption(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var trimmed = code.Trim();
        if (_uneceUomOptions.Any(x => string.Equals(x.Code, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _uneceUomOptions.Insert(0, new IvCodeLookupRow { Code = trimmed, Desc = "(not in LHDN list)" });
    }

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await RefService.ListUomsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load UOMs.";
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

    protected override IvMasterKeyToken ToKeyToken(IvUomListRow row) =>
        Key(row.Code, row.RowVersion);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive) =>
        RefService.SetUomActiveAsync(items, isActive);

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<IvUomListRow> rows) =>
        RefService.CanDeleteUomsAsync(rows.Select(r => r.Code).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items) =>
        RefService.DeleteUomsAsync(items);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new IvUomEditVm { IsActive = true, UneceUom = LhdnDefaults.UneceUom };
        EnsureUneceUomOption(EditModel.UneceUom);
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(IvUomListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access))
        {
            return;
        }

        if (!await LoadEditModelAsync(row.Code))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(IvUomListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

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
            var result = await RefService.SaveUomAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "UOM updated successfully." : "UOM added successfully.";
                await ReloadListAsync();
            }
            else
            {
                ErrorMessage = FormatResultMessage(result);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task<bool> LoadEditModelAsync(string code)
    {
        var result = await RefService.GetUomAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load UOM.";
            return false;
        }

        EditModel = result.Data;
        EnsureUneceUomOption(EditModel.UneceUom);
        ErrorMessage = null;
        return true;
    }
}
