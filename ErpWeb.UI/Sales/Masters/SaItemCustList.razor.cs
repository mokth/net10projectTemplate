using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Masters;

/// <summary>
/// Customer Items (<c>SaItemCust</c>) — list + popup CRUD.
/// Price columns are rendered only when the caller holds VIEW_PRICE (visible, not merely hidden: the
/// service does not send the value either).
/// </summary>
public partial class SaItemCustList : SaRefListPageBase<SaItemCustListRow>
{
    [Inject] private ISaCustLookupService Lookups { get; set; } = default!;
    [Inject] private IIvInventoryLookupService InventoryLookups { get; set; } = default!;

    protected override string MenuCode => MenuCodes.SalesItemCust;
    protected override string EntityLabel => "Customer Item";
    protected override string? ExportRoute => "/sales/customer-items/export";

    protected SaItemCustEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }
    protected bool CanViewPrice { get; set; }

    /// <summary>Ungated option lists (ISaCustLookupService / IIvInventoryLookupService — never the
    /// menu-gated ref services). Loaded when the popup opens so the picker stays fresh.</summary>
    protected IReadOnlyList<IvCodeLookupRow> CustomerOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> UomOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> CurrencyOptions { get; set; } = [];

    public List<GridColumnData> Columns()
    {
        var columns = new List<GridColumnData>
        {
            new()
            {
                Caption = "Customer",
                FieldName = nameof(SaItemCustListRow.CustCode),
                DataType = "string",
                SortIndex = 0,
                SortOrder = GridColumnSortOrder.Ascending,
                VisibleIndex = 1,
                Width = "140px"
            },
            new()
            {
                Caption = "Item",
                FieldName = nameof(SaItemCustListRow.ICode),
                DataType = "string",
                SortIndex = 1,
                SortOrder = GridColumnSortOrder.Ascending,
                VisibleIndex = 2,
                Width = "140px"
            },
            new()
            {
                Caption = "Description",
                FieldName = nameof(SaItemCustListRow.IDesc),
                DataType = "string",
                VisibleIndex = 3
            },
            new()
            {
                Caption = "Customer item code",
                FieldName = nameof(SaItemCustListRow.CustICode),
                DataType = "string",
                VisibleIndex = 4,
                Width = "160px"
            },
            new()
            {
                Caption = "UOM",
                FieldName = nameof(SaItemCustListRow.SellingUOM),
                DataType = "string",
                VisibleIndex = 5,
                Width = "80px"
            },
            new()
            {
                Caption = "MOQ",
                FieldName = nameof(SaItemCustListRow.MOQ),
                DataType = "number",
                VisibleIndex = 6,
                Width = "80px"
            }
        };

        if (CanViewPrice)
        {
            columns.Add(new GridColumnData
            {
                Caption = "Unit price",
                FieldName = nameof(SaItemCustListRow.UnitPrice),
                DataType = "number",
                VisibleIndex = 7,
                Width = "110px"
            });
            columns.Add(new GridColumnData
            {
                Caption = "Currency",
                FieldName = nameof(SaItemCustListRow.Currency),
                DataType = "string",
                VisibleIndex = 8,
                Width = "90px"
            });
        }

        columns.Add(new GridColumnData
        {
            Caption = "Status",
            FieldName = nameof(SaItemCustListRow.Status),
            DataType = "string",
            VisibleIndex = 9,
            Width = "90px"
        });

        return columns;
    }

    protected override async Task OnPageInitializedAsync()
    {
        CanViewPrice = await AccessRights.CanAsync(MenuCode, PermissionCodes.ViewPrice);
        await ReloadListAsync();
    }

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await RefService.ListItemCustsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load customer items.";
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

    protected override IvMasterKeyToken ToKeyToken(SaItemCustListRow row) =>
        Key(row.Key, row.RowVersion);

    /// <summary>Status is a refresh flag on this master, not activation — there is no Active toggle.</summary>
    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive) =>
        Task.FromResult(IvMasterOperationResult<object>.Fail(
            IvMasterErrorCode.Validation,
            "Customer items have no Active flag."));

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<SaItemCustListRow> rows) =>
        RefService.CanDeleteItemCustsAsync(rows
            .Select(r => new SaItemCustKey
            {
                CustCode = r.CustCode,
                ICode = r.ICode,
                SellingUOM = r.SellingUOM,
                MOQ = r.MOQ
            })
            .ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items) =>
        RefService.DeleteItemCustsAsync(items
            .Select(i => new SaItemFamilyKeyToken { Key = i.Code, RowVersion = i.RowVersion })
            .ToList());

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new SaItemCustEditVm { Status = "NEW" };
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        await LoadLookupsAsync();
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(SaItemCustListRow row)
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
        await LoadLookupsAsync();
        PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(SaItemCustListRow row)
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
        await LoadLookupsAsync();
        PopupVisible = true;
    }

    /// <summary>
    /// Ungated lookups only: <see cref="ISaCustLookupService"/> for customers and currencies and
    /// <see cref="IIvInventoryLookupService"/> for UOMs. Reaching for the menu-gated ref services
    /// (INV_UOM / customer master) would render an empty picker for a sales-master-only user.
    /// </summary>
    private async Task LoadLookupsAsync()
    {
        CustomerOptions = await Lookups.SearchCustomersAsync();
        CurrencyOptions = await Lookups.ListCurrenciesForAssignmentAsync();

        var uoms = await InventoryLookups.ListActiveUomsAsync();
        UomOptions = uoms.Succeeded ? uoms.Rows : [];
    }

    /// <summary>
    /// Prefills only what the picker actually knows (plan §4.1 rule 4): the code, the description and
    /// the standard UOM when the row is new and its UOM is still blank. It never touches the price —
    /// VIEW_PRICE owns that and <c>IvStockMasterLookupRow</c> carries no selling price anyway.
    /// </summary>
    protected void OnItemSelectedAsync(IvStockMasterLookupRow item)
    {
        EditModel.ICode = item.ICode;
        EditModel.IDesc = item.IDesc;

        if (!IsEditMode && string.IsNullOrWhiteSpace(EditModel.SellingUOM))
        {
            EditModel.SellingUOM = item.StdUom ?? string.Empty;
        }
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
            var result = await RefService.SaveItemCustAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode
                    ? "Customer item updated successfully."
                    : "Customer item added successfully.";
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

    private async Task<bool> LoadEditModelAsync(SaItemCustListRow row)
    {
        var result = await RefService.GetItemCustAsync(new SaItemCustKey
        {
            CustCode = row.CustCode,
            ICode = row.ICode,
            SellingUOM = row.SellingUOM,
            MOQ = row.MOQ
        });

        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load customer item.";
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        return true;
    }
}
