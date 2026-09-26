using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Sales.Inquiry;

namespace ErpWeb.UI.Sales.Monitor;

/// <summary>
/// A4 — e-Invoice action queue (plan-salesDecisionSupport.prompt.md).
///
/// <para>
/// Read-only: the queue never calls the LHDN portal, never re-submits and never writes to a document or
/// to the submission registry. Its "pending too long" reason is a <b>monitoring hint</b> naming what the
/// screen highlights — it never gates a submission and never modifies processing.
/// </para>
/// </summary>
public partial class SaEInvoiceAction : SaInquiryPageBase
{
    protected SaInquiryGridDataSource<SaEInvoiceActionRow> DataSource { get; private set; } = default!;

    protected SaEInvoiceStatusBreakdown Summary { get; private set; } = new();
    protected bool SummaryLoading { get; private set; }

    protected override string MenuCode => MenuCodes.SalesEInvoiceAction;

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "Type", FieldName = nameof(SaEInvoiceActionRow.DocType), Width = "70px", VisibleIndex = 1 },
        new() { Caption = "Doc no.", FieldName = nameof(SaEInvoiceActionRow.DocNo), Width = "130px", VisibleIndex = 2 },
        new() { Caption = "Date", FieldName = nameof(SaEInvoiceActionRow.DocDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 3 },
        new() { Caption = "Customer", FieldName = nameof(SaEInvoiceActionRow.CustCode), Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Name", FieldName = nameof(SaEInvoiceActionRow.CustName), VisibleIndex = 5 },
        new() { Caption = "Status", FieldName = nameof(SaEInvoiceActionRow.StatusLabel), Width = "150px", VisibleIndex = 6 },
        new() { Caption = "IRBM status", FieldName = nameof(SaEInvoiceActionRow.IrbmStatus), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "Sent on", FieldName = nameof(SaEInvoiceActionRow.IrbmSentOn), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 8 },
        new() { Caption = "Days since submitted", FieldName = nameof(SaEInvoiceActionRow.DaysSinceSubmitted), DataType = "int", Width = "140px", VisibleIndex = 9 },
        new() { Caption = "Action", FieldName = nameof(SaEInvoiceActionRow.ActionReason), Width = "200px", VisibleIndex = 10 },
        new() { Caption = "Amount", FieldName = nameof(SaEInvoiceActionRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "130px", VisibleIndex = 11 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new SaInquiryGridDataSource<SaEInvoiceActionRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    protected override async Task ReloadAsync()
    {
        await base.ReloadAsync();
        await LoadSummaryAsync();
    }

    private async Task LoadSummaryAsync()
    {
        SummaryLoading = true;
        try
        {
            var summaryQuery = BuildQuery();
            summaryQuery.Skip = 0;
            summaryQuery.Take = 0;

            var result = await Inquiry.GetEInvoiceStatusBreakdownAsync(MenuCode, summaryQuery);
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                Summary = new SaEInvoiceStatusBreakdown();
            }
            else
            {
                Summary = result.Data ?? new SaEInvoiceStatusBreakdown();
            }
        }
        finally
        {
            SummaryLoading = false;
        }
    }

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<SaInquiryPage<SaEInvoiceActionRow>>> SearchPageAsync(
        SaInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetEInvoiceActionQueueAsync(MenuCode, query, cancellationToken);

    protected async Task OnButtonClick(SelectedButtonInfo<SaEInvoiceActionRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/sales/monitor/einvoice-action/export", []), forceLoad: true);
                break;
        }
    }

    /// <summary>Opens the ERP document behind the queue row (INV / CN / DN).</summary>
    protected Task OnActionClick(SelectedButtonInfo<SaEInvoiceActionRow> info)
    {
        if (info.SelectedRow is { } row)
        {
            TryOpenByDocType(row.DocType, row.DocNo);
        }

        return Task.CompletedTask;
    }
}
