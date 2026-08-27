using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Masters;

public partial class IvLocationList : IvRefListPageBase<IvLocationListRow>
{
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;

    protected override string MenuCode => MenuCodes.InventoryLocation;
    protected override string EntityLabel => "Location";

    protected IvLocationEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }
    protected string? FilterWarehouseCode { get; set; }
    protected IReadOnlyList<IvCodeLookupRow> WarehouseOptions { get; set; } = [];
    protected bool WarehousesLoading { get; set; }

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Warehouse",
            FieldName = nameof(IvLocationListRow.WarehouseCode),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "120px"
        },
        new()
        {
            Caption = "LocCode",
            FieldName = nameof(IvLocationListRow.Code),
            DataType = "string",
            VisibleIndex = 2,
            Width = "110px"
        },
        new()
        {
            Caption = "Desc",
            FieldName = nameof(IvLocationListRow.Desc),
            DataType = "string",
            VisibleIndex = 3
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(IvLocationListRow.IsActive),
            DataType = "bool",
            VisibleIndex = 4,
            Width = "80px"
        }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        await LoadWarehousesAsync();
        if (string.IsNullOrWhiteSpace(FilterWarehouseCode) && WarehouseOptions.Count > 0)
        {
            FilterWarehouseCode = WarehouseOptions[0].Code;
        }

        await ReloadListAsync();
    }

    protected async Task OnWarehouseFilterChangedAsync(string? value)
    {
        FilterWarehouseCode = value;
        await ReloadListAsync();
    }

    private async Task LoadWarehousesAsync()
    {
        WarehousesLoading = true;
        try
        {
            var result = await Lookups.ListActiveWarehousesAsync();
            if (!result.Succeeded)
            {
                WarehouseOptions = [];
                StatusMessage = result.ErrorMessage ?? "Unable to load warehouses.";
                return;
            }

            WarehouseOptions = result.Rows;
        }
        finally
        {
            WarehousesLoading = false;
        }
    }

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            if (string.IsNullOrWhiteSpace(FilterWarehouseCode))
            {
                Data = [];
                SelectedRows.Clear();
                Grid?.Reload();
                return;
            }

            var result = await RefService.ListLocationsAsync(FilterWarehouseCode);
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load locations.";
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

    protected override IvMasterKeyToken ToKeyToken(IvLocationListRow row) =>
        Key(row.Code, row.RowVersion, row.WarehouseCode);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive) =>
        RefService.SetLocationActiveAsync(items, isActive);

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<IvLocationListRow> rows) =>
        RefService.CanDeleteLocationsAsync(rows.Select(ToKeyToken).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items) =>
        RefService.DeleteLocationsAsync(items);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(FilterWarehouseCode))
        {
            StatusMessage = "Select a warehouse first.";
            return;
        }

        EditModel = new IvLocationEditVm
        {
            WarehouseCode = FilterWarehouseCode,
            IsActive = true
        };
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(IvLocationListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access))
        {
            return;
        }

        if (!await LoadEditModelAsync(row.WarehouseCode, row.Code))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(IvLocationListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        if (!await LoadEditModelAsync(row.WarehouseCode, row.Code))
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
            var result = await RefService.SaveLocationAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Location updated successfully." : "Location added successfully.";
                if (!string.IsNullOrWhiteSpace(EditModel.WarehouseCode))
                {
                    FilterWarehouseCode = EditModel.WarehouseCode;
                }

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

    private async Task<bool> LoadEditModelAsync(string warehouseCode, string locCode)
    {
        var result = await RefService.GetLocationAsync(warehouseCode, locCode);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load location.";
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        return true;
    }
}
