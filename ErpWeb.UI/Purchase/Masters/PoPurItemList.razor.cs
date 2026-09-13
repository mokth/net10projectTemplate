using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Masters;

public partial class PoPurItemList : PoRefListPageBase<PoPurItemListRow, PoPurItemEditVm>
{
    [Inject] private IPoMasterRefService Masters { get; set; } = default!;

    protected override string MenuCode => MenuCodes.PurchasePurItem;
    protected override string EntityLabel => "Purchase item";
    protected override bool SupportsActivate => false;
    protected override string ExportUrl => "/purchase/items/export";

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Item Code", FieldName = nameof(PoPurItemListRow.ICode), DataType = "string", SortIndex = 0, SortOrder = GridColumnSortOrder.Ascending, VisibleIndex = 1, Width = "120px" },
        new() { Caption = "Description", FieldName = nameof(PoPurItemListRow.IDesc), DataType = "string", VisibleIndex = 2 },
        new() { Caption = "Category", FieldName = nameof(PoPurItemListRow.Category), DataType = "string", VisibleIndex = 3, Width = "100px" },
        new() { Caption = "Vendor", FieldName = nameof(PoPurItemListRow.Vendor), DataType = "string", VisibleIndex = 4, Width = "110px" },
        new() { Caption = "Vendor Name", FieldName = nameof(PoPurItemListRow.VendName), DataType = "string", VisibleIndex = 5 },
        new() { Caption = "Currency", FieldName = nameof(PoPurItemListRow.Currency), DataType = "string", VisibleIndex = 6, Width = "90px" },
        new() { Caption = "Unit Price", FieldName = nameof(PoPurItemListRow.UnitPrice), DataType = "decimal", VisibleIndex = 7, Width = "110px" },
        new() { Caption = "MOQ", FieldName = nameof(PoPurItemListRow.Moq), DataType = "decimal", VisibleIndex = 8, Width = "90px" },
        new() { Caption = "Status", FieldName = nameof(PoPurItemListRow.Status), DataType = "string", VisibleIndex = 9, Width = "90px" }
    ];

    protected override Task<IvMasterOperationResult<IReadOnlyList<PoPurItemListRow>>> LoadRowsAsync() =>
        Masters.ListPurItemsAsync();

    protected override PoPurItemEditVm CreateNewModel() => new();

    protected override Task<IvMasterOperationResult<PoPurItemEditVm>> LoadModelAsync(string code) =>
        Masters.GetPurItemAsync(code);

    protected override Task<IvMasterOperationResult<PoPurItemEditVm>> SaveModelAsync(PoPurItemEditVm model, bool isNew) =>
        Masters.SavePurItemAsync(model, isNew);

    protected override string GetRowCode(PoPurItemListRow row) => row.Id.ToString();
    protected override string GetModelCode(PoPurItemEditVm model) => model.Id.ToString();
    protected override IvMasterKeyToken ToKeyToken(PoPurItemListRow row) => Key(row.Id.ToString(), row.RowVersion);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive) =>
        Task.FromResult(IvMasterOperationResult<object>.Fail(
            IvMasterErrorCode.Validation, "Activate/deactivate is not supported."));

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<PoPurItemListRow> rows) =>
        Masters.CanDeletePurItemsAsync(rows.Select(r => r.Id.ToString()).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<IvMasterKeyToken> items) =>
        Masters.DeletePurItemsAsync(items);
}
