using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Purchase.Inquiry;

/// <summary>Document relationships — OPEN SOURCE / OPEN TARGET with unavailable handling.</summary>
public partial class PoDocRelationshipInquiry : PoInquiryPageBase
{
    protected PoInquiryGridDataSource<PoDocumentRelationshipRow> DataSource { get; private set; } = default!;

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> ActionButtons =>
    [
        new() { Text = "OPEN SOURCE", IConClass = "fa-solid fa-arrow-up-right-from-square", ToolTip = "Open source document" },
        new() { Text = "OPEN TARGET", IConClass = "fa-solid fa-arrow-up-right-from-square", ToolTip = "Open target document" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "Relationship", FieldName = nameof(PoDocumentRelationshipRow.Relation), Width = "110px", VisibleIndex = 1 },
        new() { Caption = "Source doc", FieldName = nameof(PoDocumentRelationshipRow.SourceDocNo), Width = "130px", VisibleIndex = 2 },
        new() { Caption = "Target doc", FieldName = nameof(PoDocumentRelationshipRow.TargetDocNo), Width = "130px", VisibleIndex = 3 },
        new() { Caption = "Qty", FieldName = nameof(PoDocumentRelationshipRow.Qty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 4 },
        new() { Caption = "Related PO", FieldName = nameof(PoDocumentRelationshipRow.RelatedPoNo), Width = "130px", VisibleIndex = 5 },
        new() { Caption = "Related PR", FieldName = nameof(PoDocumentRelationshipRow.RelatedPrNo), Width = "130px", VisibleIndex = 6 },
        new() { Caption = "Date", FieldName = nameof(PoDocumentRelationshipRow.DocDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 7 },
        new() { Caption = "Supplier", FieldName = nameof(PoDocumentRelationshipRow.VendCode), Width = "110px", VisibleIndex = 8 },
        new() { Caption = "Name", FieldName = nameof(PoDocumentRelationshipRow.VendName), VisibleIndex = 9 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new PoInquiryGridDataSource<PoDocumentRelationshipRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<PoInquiryPage<PoDocumentRelationshipRow>>> SearchPageAsync(
        PoInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetDocumentRelationshipAsync(MenuCodes.PurchaseDocRelationship, query, cancellationToken);

    protected async Task OnButtonClick(SelectedButtonInfo<PoDocumentRelationshipRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/purchase/inquiry/document-relationship/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoDocumentRelationshipRow> info)
    {
        if (info.SelectedRow is null)
        {
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        var openSource = mode == "OPEN SOURCE";
        if (!openSource && mode != "OPEN TARGET")
        {
            return Task.CompletedTask;
        }

        if (PoInquiryNavigation.TryResolveRelationship(
                info.SelectedRow.Relation,
                info.SelectedRow.SourceDocNo,
                info.SelectedRow.TargetDocNo,
                openSource,
                out var url))
        {
            OpenDocument(url);
        }
        else
        {
            NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
        }

        return Task.CompletedTask;
    }
}
