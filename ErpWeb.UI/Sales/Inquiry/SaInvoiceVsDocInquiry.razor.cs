using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Inquiry;

/// <summary>Invoice vs DO / SO — the allocation ledger plus the invoice detail's persisted links.</summary>
public partial class SaInvoiceVsDocInquiry : SaInquiryPageBase
{
    protected SaInquiryGridDataSource<SaDocumentRelationshipRow> DataSource { get; private set; } = default!;

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    /// <summary>A relationship row has two ends, so the grid exposes one action per end.</summary>
    protected override List<ButtonInfo> ActionButtons =>
    [
        new() { Text = "SOURCE", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "View source document" },
        new() { Text = "TARGET", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "View target document" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "Relationship", FieldName = nameof(SaDocumentRelationshipRow.Relation), Width = "110px", VisibleIndex = 1 },
        new() { Caption = "Source doc", FieldName = nameof(SaDocumentRelationshipRow.SourceDocNo), Width = "130px", VisibleIndex = 2 },
        new() { Caption = "Target doc", FieldName = nameof(SaDocumentRelationshipRow.TargetDocNo), Width = "130px", VisibleIndex = 3 },
        new() { Caption = "Qty", FieldName = nameof(SaDocumentRelationshipRow.Qty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 4 },
        new() { Caption = "Related SO", FieldName = nameof(SaDocumentRelationshipRow.RelatedSoNo), Width = "130px", VisibleIndex = 5 },
        new() { Caption = "Date", FieldName = nameof(SaDocumentRelationshipRow.DocDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 6 },
        new() { Caption = "Customer", FieldName = nameof(SaDocumentRelationshipRow.CustCode), Width = "110px", VisibleIndex = 7 },
        new() { Caption = "Name", FieldName = nameof(SaDocumentRelationshipRow.CustName), VisibleIndex = 8 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new SaInquiryGridDataSource<SaDocumentRelationshipRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<SaInquiryPage<SaDocumentRelationshipRow>>> SearchPageAsync(
        SaInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetDocumentRelationshipAsync(MenuCodes.SalesInvVsDoc, query, cancellationToken);

    protected async Task OnButtonClick(SelectedButtonInfo<SaDocumentRelationshipRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/sales/inquiry/document-relationship/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<SaDocumentRelationshipRow> info)
    {
        if (info.SelectedRow is { } row)
        {
            var openSource = !string.Equals(info.SelectedButton.Text, "TARGET", StringComparison.OrdinalIgnoreCase);
            TryOpenByRelationship(row.Relation, row.SourceDocNo, row.TargetDocNo, openSource);
        }

        return Task.CompletedTask;
    }
}
