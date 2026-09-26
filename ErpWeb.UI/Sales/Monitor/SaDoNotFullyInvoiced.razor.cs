using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Sales.Inquiry;

namespace ErpWeb.UI.Sales.Monitor;

/// <summary>
/// A2 — Delivered <b>Not Fully</b> Invoiced (plan-salesDecisionSupport.prompt.md).
///
/// <para>
/// "Not fully invoiced", deliberately not "not invoiced": a <c>PARTIAL</c> delivery order legitimately
/// carries invoice numbers on the lines it has already billed while an unbilled remainder stays
/// outstanding. The per-line state is <c>NO_INVOICE</c> / <c>PARTIAL</c> / <c>INVOICED</c> /
/// <c>WRITTEN_OFF</c>.
/// </para>
///
/// <para>
/// No "balance to invoice" quantity is shown: <c>SaDoDetail</c> persists neither an invoiced quantity
/// nor a balance, and Gate 1 forbids inventing a formula to manufacture one. The persisted delivered
/// quantity and the line's own invoice number are shown instead.
/// </para>
/// </summary>
public partial class SaDoNotFullyInvoiced : SaInquiryPageBase
{
    protected SaInquiryGridDataSource<SaDoNotFullyInvoicedRow> DataSource { get; private set; } = default!;

    protected SaDoNotFullyInvoicedSummary Summary { get; private set; } = new();
    protected bool SummaryLoading { get; private set; }

    protected override string MenuCode => MenuCodes.SalesDoNotFullyInvoiced;

    protected IReadOnlyList<StatusFilterOption> StatusOptions { get; } =
    [
        new(SaDoStatuses.Posted, "POSTED"),
        new(SaDoStatuses.Closed, "CLOSED")
    ];

    protected IReadOnlyList<string> StateOptions { get; } =
    [
        SaDoInvoiceStates.NoInvoice,
        SaDoInvoiceStates.Partial,
        SaDoInvoiceStates.Invoiced,
        SaDoInvoiceStates.WrittenOff
    ];

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "DO no.", FieldName = nameof(SaDoNotFullyInvoicedRow.DoNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "DO date", FieldName = nameof(SaDoNotFullyInvoicedRow.DoDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 2 },
        new() { Caption = "Posted", FieldName = nameof(SaDoNotFullyInvoicedRow.PostedDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 3 },
        new() { Caption = "Days since delivered", FieldName = nameof(SaDoNotFullyInvoicedRow.DaysSinceDelivered), DataType = "int", Width = "130px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 4 },
        new() { Caption = "Status", FieldName = nameof(SaDoNotFullyInvoicedRow.Status), Width = "100px", VisibleIndex = 5 },
        new() { Caption = "Billing", FieldName = nameof(SaDoNotFullyInvoicedRow.BillingStatus), Width = "100px", VisibleIndex = 6 },
        new() { Caption = "Line state", FieldName = nameof(SaDoNotFullyInvoicedRow.InvoiceState), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "Customer", FieldName = nameof(SaDoNotFullyInvoicedRow.CustCode), Width = "110px", VisibleIndex = 8 },
        new() { Caption = "Name", FieldName = nameof(SaDoNotFullyInvoicedRow.CustName), VisibleIndex = 9 },
        new() { Caption = "Sales rep", FieldName = nameof(SaDoNotFullyInvoicedRow.SalesRep), Width = "100px", VisibleIndex = 10 },
        new() { Caption = "Amount", FieldName = nameof(SaDoNotFullyInvoicedRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "130px", VisibleIndex = 11 },
        new() { Caption = "Line", FieldName = nameof(SaDoNotFullyInvoicedRow.Line), DataType = "int", Width = "70px", VisibleIndex = 12 },
        new() { Caption = "Item", FieldName = nameof(SaDoNotFullyInvoicedRow.ICode), Width = "120px", VisibleIndex = 13 },
        new() { Caption = "Description", FieldName = nameof(SaDoNotFullyInvoicedRow.IDesc), VisibleIndex = 14 },
        new() { Caption = "Delivered qty", FieldName = nameof(SaDoNotFullyInvoicedRow.Qty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 15 },
        new() { Caption = "SO no.", FieldName = nameof(SaDoNotFullyInvoicedRow.SoNo), Width = "120px", VisibleIndex = 16 },
        new() { Caption = "Line invoice no.", FieldName = nameof(SaDoNotFullyInvoicedRow.LineInvNo), Width = "140px", VisibleIndex = 17 },
        new() { Caption = "Pending", FieldName = nameof(SaDoNotFullyInvoicedRow.IsPendingInvoice), DataType = "bool", Width = "90px", VisibleIndex = 18 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        // This screen exists to show the leakage, so "still owes billing" is its default filter.
        PendingOnly = true;

        DataSource = new SaInquiryGridDataSource<SaDoNotFullyInvoicedRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    /// <summary>Clear restores this screen's default, which is "pending only" — not "everything".</summary>
    protected override void ResetMonitorFilters()
    {
        base.ResetMonitorFilters();
        PendingOnly = true;
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

            var result = await Inquiry.GetDeliveredNotFullyInvoicedSummaryAsync(MenuCode, summaryQuery);
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                Summary = new SaDoNotFullyInvoicedSummary();
            }
            else
            {
                Summary = result.Data ?? new SaDoNotFullyInvoicedSummary();
            }
        }
        finally
        {
            SummaryLoading = false;
        }
    }

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<SaInquiryPage<SaDoNotFullyInvoicedRow>>> SearchPageAsync(
        SaInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetDeliveredNotFullyInvoicedAsync(MenuCode, query, cancellationToken);

    protected Task OnStateClick(string? state)
    {
        InvoiceState = string.Equals(InvoiceState, state, StringComparison.Ordinal) ? null : state;
        // An explicit line state wins over the "pending only" default, so picking INVOICED shows it.
        if (InvoiceState is not null)
        {
            PendingOnly = false;
        }

        return ReloadAsync();
    }

    protected string StateChipClass(string state) =>
        string.Equals(InvoiceState, state, StringComparison.Ordinal) ? "iv-chip iv-chip--active" : "iv-chip";

    protected Task OnPendingChipClick()
    {
        PendingOnly = !PendingOnly;
        if (PendingOnly)
        {
            InvoiceState = null;
        }

        return ReloadAsync();
    }

    protected async Task OnButtonClick(SelectedButtonInfo<SaDoNotFullyInvoicedRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(
                    BuildExportUrl("/sales/monitor/delivered-not-fully-invoiced/export", []),
                    forceLoad: true);
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<SaDoNotFullyInvoicedRow> info)
    {
        if (info.SelectedRow is { } row)
        {
            TryOpenByDocType("DO", row.DoNo);
        }

        return Task.CompletedTask;
    }
}
