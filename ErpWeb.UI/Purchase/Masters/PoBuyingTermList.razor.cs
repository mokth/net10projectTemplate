using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Masters;

public partial class PoBuyingTermList : PoRefListPageBase<PoBuyingTermListRow, PoBuyingTermEditVm>
{
    [Inject] private IPoMasterRefService Masters { get; set; } = default!;

    protected override string MenuCode => MenuCodes.PurchaseBuyingTerm;
    protected override string EntityLabel => "Buying term";
    protected override bool SupportsActivate => true;
    protected override string ExportUrl => "/purchase/buying-terms/export";

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Code", FieldName = nameof(PoBuyingTermListRow.Code), DataType = "string", SortIndex = 0, SortOrder = GridColumnSortOrder.Ascending, VisibleIndex = 1, Width = "120px" },
        new() { Caption = "Description", FieldName = nameof(PoBuyingTermListRow.Description), DataType = "string", VisibleIndex = 2 },
        new() { Caption = "Active", FieldName = nameof(PoBuyingTermListRow.IsActive), DataType = "bool", VisibleIndex = 3, Width = "80px" }
    ];

    protected override Task<IvMasterOperationResult<IReadOnlyList<PoBuyingTermListRow>>> LoadRowsAsync() =>
        Masters.ListBuyingTermsAsync();

    protected override PoBuyingTermEditVm CreateNewModel() => new() { IsActive = true };

    protected override Task<IvMasterOperationResult<PoBuyingTermEditVm>> LoadModelAsync(string code) =>
        Masters.GetBuyingTermAsync(code);

    protected override Task<IvMasterOperationResult<PoBuyingTermEditVm>> SaveModelAsync(PoBuyingTermEditVm model, bool isNew) =>
        Masters.SaveBuyingTermAsync(model, isNew);

    protected override string GetRowCode(PoBuyingTermListRow row) => row.Code;
    protected override string GetModelCode(PoBuyingTermEditVm model) => model.Code;
    protected override IvMasterKeyToken ToKeyToken(PoBuyingTermListRow row) => Key(row.Code, row.RowVersion);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive) =>
        Masters.SetBuyingTermActiveAsync(items, isActive);

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<PoBuyingTermListRow> rows) =>
        Masters.CanDeleteBuyingTermsAsync(rows.Select(r => r.Code).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<IvMasterKeyToken> items) =>
        Masters.DeleteBuyingTermsAsync(items);
}
