using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Masters;

/// <summary>
/// Item Discounts (<c>SaDisGroupItem</c>) — list + popup CRUD.
/// The band/date overlap rule is enforced in the service inside a Serializable transaction; this page
/// only surfaces the rejected message.
/// </summary>
public partial class SaDisGroupItemList : SaRefListPageBase<SaDisGroupItemListRow>
{
    [Inject] private IIvInventoryLookupService InventoryLookups { get; set; } = default!;

    protected override string MenuCode => MenuCodes.SalesDisGroupItem;
    protected override string EntityLabel => "Item Discount";
    protected override string? ExportRoute => "/sales/item-discounts/export";

    protected SaDisGroupItemEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }

    /// <summary>Ungated item-class list. Blank is meaningful here: it spans every class.</summary>
    protected IReadOnlyList<IvCodeLookupRow> ClassOptions { get; set; } = [];

    /// <summary>The two date editors bind nullable values; the VM keeps the required start date.</summary>
    protected DateTime? DateFrValue { get; set; }
    protected DateTime? DateToValue { get; set; }

    protected List<string> DiscountTypes { get; } =
        [SaDiscountSlotTypes.Percentage, SaDiscountSlotTypes.Amount];

    protected List<string> EffectPrices { get; } =
        [SaEffectPriceOptions.Dealer, SaEffectPriceOptions.Selling];

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Item",
            FieldName = nameof(SaDisGroupItemListRow.ICode),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "130px"
        },
        new()
        {
            Caption = "Description",
            FieldName = nameof(SaDisGroupItemListRow.IDesc),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Class",
            FieldName = nameof(SaDisGroupItemListRow.IClass),
            DataType = "string",
            VisibleIndex = 3,
            Width = "90px"
        },
        new()
        {
            Caption = "Qty from",
            FieldName = nameof(SaDisGroupItemListRow.QtyFr),
            DataType = "number",
            VisibleIndex = 4,
            Width = "90px"
        },
        new()
        {
            Caption = "Qty to",
            FieldName = nameof(SaDisGroupItemListRow.QtyTo),
            DataType = "number",
            VisibleIndex = 5,
            Width = "90px"
        },
        new()
        {
            Caption = "From",
            FieldName = nameof(SaDisGroupItemListRow.DateFr),
            DataType = "date",
            VisibleIndex = 6,
            Width = "110px"
        },
        new()
        {
            Caption = "To",
            FieldName = nameof(SaDisGroupItemListRow.DateTo),
            DataType = "date",
            VisibleIndex = 7,
            Width = "110px"
        },
        new()
        {
            Caption = "Discount 1",
            FieldName = nameof(SaDisGroupItemListRow.Discount),
            DataType = "number",
            VisibleIndex = 8,
            Width = "100px"
        },
        new()
        {
            Caption = "Type 1",
            FieldName = nameof(SaDisGroupItemListRow.DiscountType),
            DataType = "string",
            VisibleIndex = 9,
            Width = "110px"
        },
        new()
        {
            Caption = "Discount 2",
            FieldName = nameof(SaDisGroupItemListRow.Discount1),
            DataType = "number",
            VisibleIndex = 10,
            Width = "100px"
        },
        new()
        {
            Caption = "Type 2",
            FieldName = nameof(SaDisGroupItemListRow.DiscountType1),
            DataType = "string",
            VisibleIndex = 11,
            Width = "110px"
        },
        new()
        {
            Caption = "Effect price",
            FieldName = nameof(SaDisGroupItemListRow.EffectPrice),
            DataType = "string",
            VisibleIndex = 12,
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
            var result = await RefService.ListDisGroupItemsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load item discount rules.";
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

    protected override IvMasterKeyToken ToKeyToken(SaDisGroupItemListRow row) =>
        Key(row.Key, row.RowVersion);

    /// <summary>GroupStatus is a legacy compatibility column, not an activation flag.</summary>
    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive) =>
        Task.FromResult(IvMasterOperationResult<object>.Fail(
            IvMasterErrorCode.Validation,
            "Item discount rules have no Active flag."));

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<SaDisGroupItemListRow> rows) =>
        RefService.CanDeleteDisGroupItemsAsync(rows.Select(r => r.Id).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items) =>
        RefService.DeleteDisGroupItemsAsync(items
            .Select(i => new SaItemFamilyKeyToken { Key = i.Code, RowVersion = i.RowVersion })
            .ToList());

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new SaDisGroupItemEditVm
        {
            DateFr = DateTime.Today,
            DiscountType = SaDiscountSlotTypes.Percentage,
            EffectPrice = SaEffectPriceOptions.Selling
        };
        DateFrValue = DateTime.Today;
        DateToValue = null;
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        await LoadLookupsAsync();
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(SaDisGroupItemListRow row)
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

    protected override async Task OnEditClickAsync(SaDisGroupItemListRow row)
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

    /// <summary>Ungated class list (INV_CLASS is not required to author a discount rule).</summary>
    private async Task LoadLookupsAsync()
    {
        var classes = await InventoryLookups.ListActiveClassesAsync();
        ClassOptions = classes.Succeeded ? classes.Rows : [];
    }

    /// <summary>Prefills the code and the item description the picker actually carries.</summary>
    protected void OnItemSelectedAsync(IvStockMasterLookupRow item)
    {
        EditModel.ICode = item.ICode;
        EditModel.IDesc = item.IDesc;
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
            EditModel.DateFr = DateFrValue ?? default;
            EditModel.DateTo = DateToValue;

            var result = await RefService.SaveDisGroupItemAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode
                    ? "Item discount rule updated successfully."
                    : "Item discount rule added successfully.";
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

    private async Task<bool> LoadEditModelAsync(SaDisGroupItemListRow row)
    {
        var result = await RefService.GetDisGroupItemAsync(row.Id);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load item discount rule.";
            return false;
        }

        EditModel = result.Data;
        DateFrValue = result.Data.DateFr == default ? null : result.Data.DateFr;
        DateToValue = result.Data.DateTo;
        ErrorMessage = null;
        return true;
    }
}
