using ErpWeb.Core.Lookups;
using ErpWeb.Core.Production;
using ErpWeb.UI.Components.Common.Lookups;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Lookups;

public partial class PrProductionStockWorkOrderPicker
{
    private SmartLookupInput<ProductionStockWorkOrderLookupRow>? _smartLookup;
    private bool _popupVisible;
    private string? _popupSearchText;
    private string? _secondaryText;
    private string? _lastResolvedCode;

    [Parameter, EditorRequired] public Func<string, CancellationToken, Task<LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>>> ResolveAsync { get; set; } = default!;
    [Parameter, EditorRequired] public Func<LargeLookupSearchRequest, CancellationToken, Task<LargeLookupPage<ProductionStockWorkOrderLookupRow>>> SearchAsync { get; set; } = default!;
    [Parameter] public string? WorkOrderNo { get; set; }
    [Parameter] public EventCallback<string?> WorkOrderNoChanged { get; set; }
    [Parameter] public EventCallback<ProductionStockWorkOrderLookupRow> Selected { get; set; }
    [Parameter] public EventCallback Cleared { get; set; }
    [Parameter] public string? ProductCode { get; set; }
    [Parameter] public string? ProductDescription { get; set; }
    [Parameter] public string? WorkOrderStatus { get; set; }
    [Parameter] public bool Enabled { get; set; } = true;
    [Parameter] public string? InputCssClass { get; set; }
    [Parameter] public string NullText { get; set; } = "Select Work Order";
    [Parameter] public string NotFoundMessage { get; set; } = "Work Order not found or not eligible";
    [Parameter] public string SearchButtonAriaLabel { get; set; } = "Search Work Orders";
    [Parameter] public string PopupHeaderText { get; set; } = "SELECT WORK ORDER";
    [Parameter] public string SearchPlaceholder { get; set; } = "WO / product / description";
    [Parameter] public string CandidateCountCaption { get; set; } = "SOURCE POOLS";
    [Parameter] public string EmptyListMessage { get; set; } = "No eligible Work Orders with candidate Production stock were found.";
    [Parameter] public string NoSearchMatchMessage { get; set; } = "No eligible Work Orders match this search.";

    private string? EffectiveSecondaryText => _secondaryText ?? FormatContext(ProductCode, ProductDescription, WorkOrderStatus);

    protected override void OnParametersSet()
    {
        var code = WorkOrderNo?.Trim();
        if (!string.Equals(code, _lastResolvedCode, StringComparison.OrdinalIgnoreCase))
        {
            _secondaryText = FormatContext(ProductCode, ProductDescription, WorkOrderStatus);
            _lastResolvedCode = code;
        }
    }

    private static string? FormatSecondaryText(ProductionStockWorkOrderLookupRow row) =>
        FormatContext(row.ProductCode, row.ProductDescription, row.Status);

    private static string? FormatContext(string? productCode, string? description, string? status)
    {
        var product = string.IsNullOrWhiteSpace(description) ? productCode : $"{productCode} — {description}";
        return string.IsNullOrWhiteSpace(status) ? product : string.IsNullOrWhiteSpace(product) ? status : $"{product} · {status}";
    }

    private async Task CommitSelectionAsync(ProductionStockWorkOrderLookupRow row)
    {
        _secondaryText = FormatSecondaryText(row);
        _lastResolvedCode = row.WorkOrderNo;
        if (WorkOrderNoChanged.HasDelegate)
            await WorkOrderNoChanged.InvokeAsync(row.WorkOrderNo);
        if (Selected.HasDelegate)
            await Selected.InvokeAsync(row);
    }

    private async Task OnClearedAsync()
    {
        _secondaryText = null;
        _lastResolvedCode = null;
        if (Cleared.HasDelegate)
            await Cleared.InvokeAsync();
        if (WorkOrderNoChanged.HasDelegate)
            await WorkOrderNoChanged.InvokeAsync(null);
    }

    private Task OpenSearchAsync(string? seedText)
    {
        if (!Enabled)
            return Task.CompletedTask;
        _popupSearchText = seedText;
        _popupVisible = true;
        return Task.CompletedTask;
    }

    private async Task OnPopupSelectedAsync(ProductionStockWorkOrderLookupRow row)
    {
        if (_smartLookup is not null)
            await _smartLookup.CommitItemAsync(row);
        else
            await CommitSelectionAsync(row);
        _popupVisible = false;
    }
}
