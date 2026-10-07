using ErpWeb.Core.Lookups;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Common.Lookups;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Lookups;

public partial class SaCustomerPicker
{
    private SmartLookupInput<SaCustomerLookupRow>? _smartLookup;
    private bool _popupVisible;
    private string? _popupSearchText;
    private string? _secondaryText;
    private string? _lastResolvedCode;

    [Inject] private ISaCustLookupService Lookups { get; set; } = default!;

    [Parameter] public string? CustCode { get; set; }
    [Parameter] public EventCallback<string?> CustCodeChanged { get; set; }
    [Parameter] public EventCallback<SaCustomerLookupRow> Selected { get; set; }
    /// <summary>Fired only on explicit clear — never when an invalid typed code is rejected.</summary>
    [Parameter] public EventCallback Cleared { get; set; }
    /// <summary>Optional name shown under the code (e.g. loaded document customer name).</summary>
    [Parameter] public string? SecondaryName { get; set; }
    [Parameter] public bool Enabled { get; set; } = true;
    [Parameter] public string? InputCssClass { get; set; }
    [Parameter] public string? NullText { get; set; }

    private string? EffectiveSecondaryText => _secondaryText ?? SecondaryName;

    protected override void OnParametersSet()
    {
        var code = CustCode?.Trim();
        if (!string.Equals(code, _lastResolvedCode, StringComparison.OrdinalIgnoreCase))
        {
            _secondaryText = null;
            _lastResolvedCode = code;
        }
    }

    private Task<LargeLookupResolveResult<SaCustomerLookupRow>> ResolveCustomerAsync(
        string text,
        CancellationToken cancellationToken) =>
        Lookups.ResolveCustomerAsync(text, cancellationToken);

    private async Task CommitSelectionAsync(SaCustomerLookupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        CustCode = row.CustCode;
        _secondaryText = row.CustName;
        _lastResolvedCode = row.CustCode;

        if (CustCodeChanged.HasDelegate)
        {
            await CustCodeChanged.InvokeAsync(row.CustCode);
        }

        if (Selected.HasDelegate)
        {
            await Selected.InvokeAsync(row);
        }
    }

    private async Task OnClearedAsync()
    {
        CustCode = null;
        _secondaryText = null;
        _lastResolvedCode = null;

        if (Cleared.HasDelegate)
        {
            await Cleared.InvokeAsync();
        }

        if (CustCodeChanged.HasDelegate)
        {
            await CustCodeChanged.InvokeAsync(null);
        }
    }

    private Task OpenSearchAsync(string? seedText)
    {
        if (!Enabled)
        {
            return Task.CompletedTask;
        }

        _popupSearchText = seedText;
        _popupVisible = true;
        return Task.CompletedTask;
    }

    private async Task OnPopupSelectedAsync(SaCustomerLookupRow row)
    {
        if (_smartLookup is not null)
        {
            await _smartLookup.CommitItemAsync(row);
        }
        else
        {
            await CommitSelectionAsync(row);
        }

        _popupVisible = false;
    }
}
