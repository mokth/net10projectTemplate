using ErpWeb.Core.Inventory;
using ErpWeb.Core.Lookups;
using ErpWeb.UI.Components.Common.Lookups;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Lookups;

public partial class IvStockMasterPicker
{
    private SmartLookupInput<IvStockMasterLookupRow>? _smartLookup;
    private bool _popupVisible;
    private string? _popupSearchText;
    private string? _secondaryText;
    private string? _lastResolvedCode;

    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;

    [Parameter] public string? ICode { get; set; }
    [Parameter] public EventCallback<string?> ICodeChanged { get; set; }
    [Parameter] public EventCallback<IvStockMasterLookupRow> Selected { get; set; }
    /// <summary>Fired only on explicit clear — never when an invalid typed code is rejected.</summary>
    [Parameter] public EventCallback Cleared { get; set; }
    [Parameter] public bool Enabled { get; set; } = true;
    [Parameter] public string? InputCssClass { get; set; }
    [Parameter] public string? NullText { get; set; }

    protected override void OnParametersSet()
    {
        var code = ICode?.Trim();
        if (!string.Equals(code, _lastResolvedCode, StringComparison.OrdinalIgnoreCase))
        {
            // Parent changed the code without going through our resolve path (e.g. load document).
            _secondaryText = null;
            _lastResolvedCode = code;
        }
    }

    private async Task<LargeLookupResolveResult<IvStockMasterLookupRow>> ResolveItemAsync(
        string text,
        CancellationToken cancellationToken)
    {
        var result = await Lookups.ResolveItemAsync(text, cancellationToken);
        if (result.Succeeded && result.Item is not null)
        {
            return LargeLookupResolveResult<IvStockMasterLookupRow>.Ok(result.Item);
        }

        var message = result.ErrorMessage ?? "Item not found";
        var ambiguous = message.Contains("multiple", StringComparison.OrdinalIgnoreCase);
        return LargeLookupResolveResult<IvStockMasterLookupRow>.Fail(message, ambiguous);
    }

    private async Task CommitSelectionAsync(IvStockMasterLookupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        ICode = row.ICode;
        _secondaryText = row.IDesc;
        _lastResolvedCode = row.ICode;

        if (ICodeChanged.HasDelegate)
        {
            await ICodeChanged.InvokeAsync(row.ICode);
        }

        if (Selected.HasDelegate)
        {
            await Selected.InvokeAsync(row);
        }
    }

    private async Task OnClearedAsync()
    {
        ICode = null;
        _secondaryText = null;
        _lastResolvedCode = null;

        if (Cleared.HasDelegate)
        {
            await Cleared.InvokeAsync();
        }

        if (ICodeChanged.HasDelegate)
        {
            await ICodeChanged.InvokeAsync(null);
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

    private async Task OnPopupSelectedAsync(IvStockMasterLookupRow row)
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
