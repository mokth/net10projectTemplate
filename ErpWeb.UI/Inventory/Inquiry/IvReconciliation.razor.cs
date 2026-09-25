using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Inquiry;

/// <summary>
/// Inventory Reconciliation — a thin UI over the existing
/// <see cref="IIvInventoryReconciliationService"/> (Phase 3, item 16).
///
/// <para>
/// <b>ACCESS only, and deliberately no export.</b> The findings are diagnostics: exporting them invites
/// treating a diagnostic list as an audit report, and the service's own "diagnostic only" caveat is
/// preserved verbatim on the page (D6). There is also no EXPORT button and no export endpoint.
/// </para>
///
/// <para>
/// Findings are bounded by the number of disagreeing slices, not by the size of the ledger, so the grid
/// is served from the one materialised result rather than re-querying per page.
/// </para>
/// </summary>
public partial class IvReconciliation : PageBase
{
    [Inject] private IIvInventoryReconciliationService Reconciliation { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;

    protected bool IsBootstrapping = true;
    protected bool IsRunning;

    protected string? AppliedICode;
    protected string? AppliedWhCode;

    protected string? DraftICode;
    protected string? DraftWhCode;

    protected string StatusCaption = "Not run";
    protected IReadOnlyList<IvInventoryReconcileFinding> Findings { get; set; } = [];

    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];

    protected IvListGridDataSource<IvInventoryReconcileFinding> DataSource { get; private set; } = default!;

    protected bool HasFindings => Findings.Count > 0;

    protected string FindingCountLabel =>
        Findings.Count == 1 ? "1 finding" : $"{Findings.Count:N0} findings";

    /// <summary>
    /// The findings grid. <c>Slice</c> is the identity that matters — a finding names WHICH stock slice
    /// disagrees, not just which row — so it is a visible column rather than a hidden detail.
    /// </summary>
    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "Code", FieldName = nameof(IvInventoryReconcileFinding.Code), Width = "190px", VisibleIndex = 1 },
        new() { Caption = "Message", FieldName = nameof(IvInventoryReconcileFinding.Message), VisibleIndex = 2 },
        new() { Caption = "Slice", FieldName = nameof(IvInventoryReconcileFinding.Slice), Width = "280px", VisibleIndex = 3 },
        new() { Caption = "Balance qty", FieldName = nameof(IvInventoryReconcileFinding.BalLocQty), DataType = "decimal", DisplayFormat = "n4", Width = "120px", VisibleIndex = 4 },
        new() { Caption = "History net", FieldName = nameof(IvInventoryReconcileFinding.HistoryNetQty), DataType = "decimal", DisplayFormat = "n4", Width = "120px", VisibleIndex = 5 },
        new() { Caption = "Pile id", FieldName = nameof(IvInventoryReconcileFinding.BalLocId), DataType = "int", Visible = false, VisibleIndex = 6 },
        new() { Caption = "History id", FieldName = nameof(IvInventoryReconcileFinding.HistoryId), DataType = "int", Visible = false, VisibleIndex = 7 }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new IvListGridDataSource<IvInventoryReconcileFinding>();

        var warehouses = await Lookups.ListActiveWarehousesAsync();
        Warehouses = warehouses.Succeeded ? warehouses.Rows : [];
        if (!warehouses.Succeeded)
        {
            ErrorMessage = warehouses.ErrorMessage ?? "Unable to load the warehouse list.";
        }

        IsBootstrapping = false;

        // Run once on open with no filter: an empty page would make the screen look broken, and the
        // service's findings are what the user came for.
        await RunAsync();
    }

    protected async Task RunAsync()
    {
        IsRunning = true;
        await InvokeAsync(StateHasChanged);

        AppliedICode = Normalize(DraftICode);
        AppliedWhCode = Normalize(DraftWhCode);

        var result = await Reconciliation.ReconcileAsync(AppliedICode, AppliedWhCode);

        if (result.Succeeded)
        {
            Findings = result.Findings;
            StatusCaption = result.Status;
        }
        else
        {
            Findings = [];
            StatusCaption = "Not run";
            ErrorMessage = result.ErrorMessage ?? "Unable to reconcile.";
        }

        DataSource.SetRows(Findings);
        IsRunning = false;
        await InvokeAsync(StateHasChanged);
    }

    protected void DismissError() => ErrorMessage = null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
