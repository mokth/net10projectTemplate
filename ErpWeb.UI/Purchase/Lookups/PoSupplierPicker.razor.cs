using ErpWeb.Core.Lookups;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.Lookups;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Lookups;

public partial class PoSupplierPicker
{
    private SmartLookupInput<PoSupplierLookupRow>? _smartLookup;
    private bool _popupVisible;
    private string? _popupSearchText;
    private string? _secondaryText;
    private string? _lastResolvedCode;

    [Inject] private IPoSupplierLookupService Lookups { get; set; } = default!;

    [Parameter] public string? SuppCode { get; set; }
    [Parameter] public EventCallback<string?> SuppCodeChanged { get; set; }
    [Parameter] public EventCallback<PoSupplierLookupRow> Selected { get; set; }
    [Parameter] public EventCallback Cleared { get; set; }
    [Parameter] public bool Enabled { get; set; } = true;
    [Parameter] public string? InputCssClass { get; set; }
    [Parameter] public string? NullText { get; set; }

    protected override void OnParametersSet()
    {
        var code = SuppCode?.Trim();
        if (!string.Equals(code, _lastResolvedCode, StringComparison.OrdinalIgnoreCase))
        {
            _secondaryText = null;
            _lastResolvedCode = code;
        }
    }

    private Task<LargeLookupResolveResult<PoSupplierLookupRow>> ResolveSupplierAsync(
        string text,
        CancellationToken cancellationToken) =>
        Lookups.ResolveSupplierAsync(text, cancellationToken);

    private async Task CommitSelectionAsync(PoSupplierLookupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        SuppCode = row.SuppCode;
        _secondaryText = row.SuppName;
        _lastResolvedCode = row.SuppCode;

        if (SuppCodeChanged.HasDelegate)
        {
            await SuppCodeChanged.InvokeAsync(row.SuppCode);
        }

        if (Selected.HasDelegate)
        {
            await Selected.InvokeAsync(row);
        }
    }

    private async Task OnClearedAsync()
    {
        SuppCode = null;
        _secondaryText = null;
        _lastResolvedCode = null;

        if (Cleared.HasDelegate)
        {
            await Cleared.InvokeAsync();
        }

        if (SuppCodeChanged.HasDelegate)
        {
            await SuppCodeChanged.InvokeAsync(null);
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

    private async Task OnPopupSelectedAsync(PoSupplierLookupRow row)
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
