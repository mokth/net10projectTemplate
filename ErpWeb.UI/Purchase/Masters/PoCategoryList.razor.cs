using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Masters;

public partial class PoCategoryList : PoRefListPageBase<PoCategoryListRow, PoCategoryEditVm>
{
    [Inject] private IPoMasterRefService Masters { get; set; } = default!;

    protected override string MenuCode => MenuCodes.PurchaseCategory;
    protected override string EntityLabel => "Category";
    protected override bool SupportsActivate => true;
    protected override string ExportUrl => "/purchase/categories/export";

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Code", FieldName = nameof(PoCategoryListRow.Code), DataType = "string", SortIndex = 0, SortOrder = GridColumnSortOrder.Ascending, VisibleIndex = 1, Width = "120px" },
        new() { Caption = "Description", FieldName = nameof(PoCategoryListRow.Description), DataType = "string", VisibleIndex = 2 },
        new() { Caption = "Active", FieldName = nameof(PoCategoryListRow.IsActive), DataType = "bool", VisibleIndex = 3, Width = "80px" }
    ];

    protected override Task<IvMasterOperationResult<IReadOnlyList<PoCategoryListRow>>> LoadRowsAsync() =>
        Masters.ListCategoriesAsync();

    protected override PoCategoryEditVm CreateNewModel() => new() { IsActive = true };

    protected override Task<IvMasterOperationResult<PoCategoryEditVm>> LoadModelAsync(string code) =>
        Masters.GetCategoryAsync(code);

    protected override Task<IvMasterOperationResult<PoCategoryEditVm>> SaveModelAsync(PoCategoryEditVm model, bool isNew) =>
        Masters.SaveCategoryAsync(model, isNew);

    protected override string GetRowCode(PoCategoryListRow row) => row.Code;
    protected override string GetModelCode(PoCategoryEditVm model) => model.Code;
    protected override IvMasterKeyToken ToKeyToken(PoCategoryListRow row) => Key(row.Code, row.RowVersion);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive) =>
        Masters.SetCategoryActiveAsync(items, isActive);

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<PoCategoryListRow> rows) =>
        Masters.CanDeleteCategoriesAsync(rows.Select(r => r.Code).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<IvMasterKeyToken> items) =>
        Masters.DeleteCategoriesAsync(items);
}
