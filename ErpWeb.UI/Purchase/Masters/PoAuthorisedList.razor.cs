using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Masters;

public partial class PoAuthorisedList : PoRefListPageBase<PoAuthorisedListRow, PoAuthorisedEditVm>
{
    [Inject] private IPoMasterRefService Masters { get; set; } = default!;

    protected override string MenuCode => MenuCodes.PurchaseAuthorised;
    protected override string EntityLabel => "Authorised person";
    protected override bool SupportsActivate => true;
    protected override string ExportUrl => "/purchase/authorised/export";

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Code", FieldName = nameof(PoAuthorisedListRow.Code), DataType = "string", SortIndex = 0, SortOrder = GridColumnSortOrder.Ascending, VisibleIndex = 1, Width = "100px" },
        new() { Caption = "Name", FieldName = nameof(PoAuthorisedListRow.Name), DataType = "string", VisibleIndex = 2 },
        new() { Caption = "Email", FieldName = nameof(PoAuthorisedListRow.Email), DataType = "string", VisibleIndex = 3, Width = "180px" },
        new() { Caption = "Mobile", FieldName = nameof(PoAuthorisedListRow.MobileNo), DataType = "string", VisibleIndex = 4, Width = "120px" },
        new() { Caption = "Active", FieldName = nameof(PoAuthorisedListRow.IsActive), DataType = "bool", VisibleIndex = 5, Width = "80px" }
    ];

    protected override Task<IvMasterOperationResult<IReadOnlyList<PoAuthorisedListRow>>> LoadRowsAsync() =>
        Masters.ListAuthorisedAsync();

    protected override PoAuthorisedEditVm CreateNewModel() => new() { IsActive = true };

    protected override Task<IvMasterOperationResult<PoAuthorisedEditVm>> LoadModelAsync(string code) =>
        Masters.GetAuthorisedAsync(code);

    protected override Task<IvMasterOperationResult<PoAuthorisedEditVm>> SaveModelAsync(PoAuthorisedEditVm model, bool isNew) =>
        Masters.SaveAuthorisedAsync(model, isNew);

    protected override string GetRowCode(PoAuthorisedListRow row) => row.Code;
    protected override string GetModelCode(PoAuthorisedEditVm model) => model.Code;
    protected override IvMasterKeyToken ToKeyToken(PoAuthorisedListRow row) => Key(row.Code, row.RowVersion);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive) =>
        Masters.SetAuthorisedActiveAsync(items, isActive);

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<PoAuthorisedListRow> rows) =>
        Masters.CanDeleteAuthorisedAsync(rows.Select(r => r.Code).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<IvMasterKeyToken> items) =>
        Masters.DeleteAuthorisedAsync(items);
}
