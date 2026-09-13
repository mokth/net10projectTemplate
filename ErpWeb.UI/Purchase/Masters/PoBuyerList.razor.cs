using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Masters;

public partial class PoBuyerList : PoRefListPageBase<PoBuyerListRow, PoBuyerEditVm>
{
    [Inject] private IPoMasterRefService Masters { get; set; } = default!;

    protected override string MenuCode => MenuCodes.PurchaseBuyer;
    protected override string EntityLabel => "Buyer";
    protected override bool SupportsActivate => true;
    protected override string ExportUrl => "/purchase/buyers/export";

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Code", FieldName = nameof(PoBuyerListRow.Code), DataType = "string", SortIndex = 0, SortOrder = GridColumnSortOrder.Ascending, VisibleIndex = 1, Width = "100px" },
        new() { Caption = "Name", FieldName = nameof(PoBuyerListRow.Name), DataType = "string", VisibleIndex = 2 },
        new() { Caption = "Description", FieldName = nameof(PoBuyerListRow.Desc), DataType = "string", VisibleIndex = 3 },
        new() { Caption = "Active", FieldName = nameof(PoBuyerListRow.IsActive), DataType = "bool", VisibleIndex = 4, Width = "80px" }
    ];

    protected override Task<IvMasterOperationResult<IReadOnlyList<PoBuyerListRow>>> LoadRowsAsync() =>
        Masters.ListBuyersAsync();

    protected override PoBuyerEditVm CreateNewModel() => new() { IsActive = true };

    protected override Task<IvMasterOperationResult<PoBuyerEditVm>> LoadModelAsync(string code) =>
        Masters.GetBuyerAsync(code);

    protected override Task<IvMasterOperationResult<PoBuyerEditVm>> SaveModelAsync(PoBuyerEditVm model, bool isNew) =>
        Masters.SaveBuyerAsync(model, isNew);

    protected override string GetRowCode(PoBuyerListRow row) => row.Code;
    protected override string GetModelCode(PoBuyerEditVm model) => model.Code;
    protected override IvMasterKeyToken ToKeyToken(PoBuyerListRow row) => Key(row.Code, row.RowVersion);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive) =>
        Masters.SetBuyerActiveAsync(items, isActive);

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<PoBuyerListRow> rows) =>
        Masters.CanDeleteBuyersAsync(rows.Select(r => r.Code).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<IvMasterKeyToken> items) =>
        Masters.DeleteBuyersAsync(items);
}
