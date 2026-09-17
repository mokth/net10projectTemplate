using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace ErpWeb.UI.Sales.Masters;

/// <summary>
/// Price Groups (<c>IvCustPriceGroup</c>) with their item prices (<c>IvCustPrice</c>).
///
/// The header and its lines are saved as ONE aggregate in one transaction (plan §10): this page only
/// assembles the payload, the service does header → lines → duplicates → item/UOM existence → write, and
/// rolls everything back if any line fails.
///
/// This is the <b>only</b> screen that maintains <c>IvCustPrice</c> lines. The separate read-only
/// "Customer Prices" screen (<c>/sales/customer-prices</c>) was merged into this page, so the per-group
/// workbook download lives here too.
/// </summary>
public partial class SaCustPriceGroupList : SaRefListPageBase<IvCustPriceGroupListRow>
{
    [Inject] private IIvInventoryLookupService InventoryLookups { get; set; } = default!;
    [Inject] private ISaCustLookupService CustLookups { get; set; } = default!;

    protected override string MenuCode => MenuCodes.SalesCustPriceGroup;
    protected override string EntityLabel => "Price Group";

    /// <summary>The header carries the Active flag, so this screen supports the activate toolbar.</summary>
    protected override bool SupportsActivate => true;

    protected override string? ExportRoute => "/sales/price-groups/export";

    protected IvCustPriceGroupEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }
    protected bool CanViewPrice { get; set; }
    protected bool CanExport { get; set; }

    /// <summary>Ungated active UOM list (INV_UOM is not required to author a price list).</summary>
    protected IReadOnlyList<IvCodeLookupRow> UomOptions { get; set; } = [];

    /// <summary>Phase 3: active currencies for a line. Blank is deliberately allowed = company base.</summary>
    protected IReadOnlyList<IvCodeLookupRow> CurrencyOptions { get; set; } = [];

    /// <summary>Description carried from the item picker into the line being added/updated.</summary>
    private string? _lineItemDesc;

    // Line editor state.
    protected string? LineItem { get; set; }
    protected string? LineUom { get; set; }
    protected decimal? LinePrice { get; set; }
    protected decimal? LinePack { get; set; }

    // Phase 3: the item and UOM stopped being a unique identity once quantity tiers and promotions
    // arrived, so the editor must also carry the band, the window and the currency to know which line
    // it is editing.
    protected decimal? LineMinQty { get; set; }
    protected decimal? LineMaxQty { get; set; }
    protected DateTime? LineValidFrom { get; set; }
    protected DateTime? LineValidTo { get; set; }
    protected string? LineCurrency { get; set; }

    /// <summary>Key of the line currently being edited; null = the editor is adding a new line.</summary>
    protected string? LineEditKey { get; set; }

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(IvCustPriceGroupListRow.CustPriceCode),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "150px"
        },
        new()
        {
            Caption = "Description",
            FieldName = nameof(IvCustPriceGroupListRow.CustPriceDesc),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Prices",
            FieldName = nameof(IvCustPriceGroupListRow.LineCount),
            DataType = "number",
            VisibleIndex = 3,
            Width = "90px"
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(IvCustPriceGroupListRow.IsActive),
            DataType = "boolean",
            VisibleIndex = 4,
            Width = "90px"
        }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        CanViewPrice = await AccessRights.CanAsync(MenuCode, PermissionCodes.ViewPrice);
        CanExport = await AccessRights.CanAsync(MenuCode, PermissionCodes.Export);
        await ReloadListAsync();
    }

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await RefService.ListCustPriceGroupsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load price groups.";
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

    protected override IvMasterKeyToken ToKeyToken(IvCustPriceGroupListRow row) =>
        Key(row.CustPriceCode, row.RowVersion);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive) =>
        RefService.SetCustPriceGroupActiveAsync(ToTokens(items), isActive);

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<IvCustPriceGroupListRow> rows) =>
        RefService.CanDeleteCustPriceGroupsAsync(rows.Select(r => r.CustPriceCode).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items) =>
        RefService.DeleteCustPriceGroupsAsync(ToTokens(items));

    private static List<SaItemFamilyKeyToken> ToTokens(IReadOnlyList<IvMasterKeyToken> items) =>
        items.Select(i => new SaItemFamilyKeyToken { Key = i.Code, RowVersion = i.RowVersion }).ToList();

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new IvCustPriceGroupEditVm { IsActive = true };
        ResetLineEditor();
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        await LoadLookupsAsync();
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(IvCustPriceGroupListRow row)
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

    protected override async Task OnEditClickAsync(IvCustPriceGroupListRow row)
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
    /// Loads the ungated UOM list when the popup opens. UOMs are deliberately NOT limited to the item's
    /// standard UOM: the same item priced in several UOMs is intended behaviour (item-family plan
    /// §8.5/§8.6).
    /// </summary>
    private async Task LoadLookupsAsync()
    {
        var uoms = await InventoryLookups.ListActiveUomsAsync();
        UomOptions = uoms.Succeeded ? uoms.Rows : [];

        // Phase 3: the currency combo is optional by design - blank means the company base currency.
        CurrencyOptions = await CustLookups.ListCurrenciesForAssignmentAsync();
    }

    /// <summary>
    /// Downloads the workbook for the price list open in the popup. The line grid is the only place item
    /// prices are maintained, so this is the sole caller of the per-group export.
    /// </summary>
    protected void OnExportPrices()
    {
        if (!CanExport)
        {
            ErrorMessage = "Access Denied!!";
            return;
        }

        if (!IsEditMode || string.IsNullOrWhiteSpace(EditModel.CustPriceCode))
        {
            return;
        }

        var url = QueryHelpers.AddQueryString(
            "/sales/price-groups/prices/export",
            "custPriceCode",
            EditModel.CustPriceCode);
        Navigation.NavigateTo(url, forceLoad: true);
    }

    /// <summary>
    /// Prefills only what the picker knows: the code, the description (the line VM already had the field
    /// but nothing ever filled it) and the standard UOM when the UOM box is still blank. It never
    /// touches the price — VIEW_PRICE owns that and the picker carries no selling price.
    /// </summary>
    protected void OnLineItemSelectedAsync(IvStockMasterLookupRow item)
    {
        LineItem = item.ICode;
        _lineItemDesc = item.IDesc;

        if (string.IsNullOrWhiteSpace(LineUom))
        {
            LineUom = item.StdUom;
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

    /// <summary>
    /// Phase 3: the natural identity of a price line. Item + UOM stopped being unique when quantity
    /// tiers and promotions arrived (two bands legitimately start on the SAME ValidFrom), so the band
    /// floor, the effective-from and the currency are all part of the identity. This mirrors
    /// <see cref="IvCustPriceLineVm.Key"/> so the grid and the editor agree on what "the same line" is.
    /// </summary>
    private static string BuildLineKey(
        string item, string uom, DateTime? validFrom, decimal? minQty, string? currency) =>
        $"{item};{uom};{validFrom?.ToString("yyyyMMdd") ?? "always"};{minQty ?? 0m};{(currency ?? string.Empty).Trim().ToUpperInvariant()}";

    /// <summary>Adds the editor's line, or replaces the one being edited, keyed on the NATURAL key.</summary>
    protected void OnLineAddOrUpdate()
    {
        var item = (LineItem ?? string.Empty).Trim().ToUpperInvariant();
        var uom = (LineUom ?? string.Empty).Trim().ToUpperInvariant();
        if (item.Length == 0 || uom.Length == 0)
        {
            ErrorMessage = "Item and UOM are required for a price line.";
            return;
        }

        var currency = (LineCurrency ?? string.Empty).Trim().ToUpperInvariant();
        if (currency.Length > 5)
        {
            ErrorMessage = "Currency must be at most 5 characters.";
            return;
        }

        if (LineMinQty is < 0m || LineMaxQty is < 0m)
        {
            ErrorMessage = "Quantity band values cannot be negative.";
            return;
        }

        // Caught here for a clear message; the SERVICE still re-validates the band against every
        // other line on the list (plan 3.4) inside the aggregate save.
        if (LineMinQty is { } min && LineMaxQty is { } max && max < min)
        {
            ErrorMessage = "A line's maximum quantity cannot be less than its minimum.";
            return;
        }

        if (LineValidFrom?.Date is { } from && LineValidTo?.Date is { } to && to < from)
        {
            ErrorMessage = "A line's valid-to date cannot be before its valid-from date.";
            return;
        }

        var key = BuildLineKey(item, uom, LineValidFrom, LineMinQty, currency);

        var existing = LineEditKey is null
            ? null
            : EditModel.Lines.FirstOrDefault(l =>
                string.Equals(l.Key, LineEditKey, StringComparison.OrdinalIgnoreCase));

        if (existing is not null && !string.Equals(LineEditKey, key, StringComparison.OrdinalIgnoreCase))
        {
            // The identity was edited: drop the old line before adding the new one.
            EditModel.Lines.Remove(existing);
            existing = null;
        }

        if (existing is null)
        {
            if (EditModel.Lines.Any(l => string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                ErrorMessage = $"Item {item} / UOM {uom} already has a price for this band and window on this list.";
                return;
            }

            EditModel.Lines.Add(new IvCustPriceLineVm
            {
                ICode = item,
                IDesc = _lineItemDesc,
                UOM = uom,
                SellingPrice = LinePrice,
                SellPackSize = LinePack,
                ValidFrom = LineValidFrom?.Date,
                ValidTo = LineValidTo?.Date,
                MinQty = LineMinQty ?? 0m,
                MaxQty = LineMaxQty,
                CurrencyCode = currency.Length == 0 ? null : currency
            });
        }
        else
        {
            existing.IDesc = _lineItemDesc ?? existing.IDesc;
            existing.SellingPrice = LinePrice;
            existing.SellPackSize = LinePack;
            existing.ValidFrom = LineValidFrom?.Date;
            existing.ValidTo = LineValidTo?.Date;
            existing.MinQty = LineMinQty ?? 0m;
            existing.MaxQty = LineMaxQty;
            existing.CurrencyCode = currency.Length == 0 ? null : currency;
        }

        ErrorMessage = null;
        ResetLineEditor();
    }

    protected void OnLineEdit(IvCustPriceLineVm line)
    {
        LineItem = line.ICode;
        LineUom = line.UOM;
        LinePrice = line.SellingPrice;
        LinePack = line.SellPackSize;
        LineMinQty = line.MinQty;
        LineMaxQty = line.MaxQty;
        LineValidFrom = line.ValidFrom;
        LineValidTo = line.ValidTo;
        LineCurrency = line.CurrencyCode;
        _lineItemDesc = line.IDesc;
        LineEditKey = line.Key;
    }

    protected void OnLineRemove(IvCustPriceLineVm line)
    {
        EditModel.Lines.Remove(line);
        if (LineEditKey is not null && string.Equals(LineEditKey, line.Key, StringComparison.OrdinalIgnoreCase))
        {
            ResetLineEditor();
        }
    }

    protected void ResetLineEditor()
    {
        LineItem = null;
        LineUom = null;
        LinePrice = null;
        LinePack = null;
        LineMinQty = null;
        LineMaxQty = null;
        LineValidFrom = null;
        LineValidTo = null;
        LineCurrency = null;
        _lineItemDesc = null;
        LineEditKey = null;
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
            var result = await RefService.SaveCustPriceGroupAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode
                    ? "Price group updated successfully."
                    : "Price group added successfully.";
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

    private async Task<bool> LoadEditModelAsync(IvCustPriceGroupListRow row)
    {
        var result = await RefService.GetCustPriceGroupAsync(row.CustPriceCode);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load price group.";
            return false;
        }

        EditModel = result.Data;
        ResetLineEditor();
        ErrorMessage = null;
        return true;
    }
}
