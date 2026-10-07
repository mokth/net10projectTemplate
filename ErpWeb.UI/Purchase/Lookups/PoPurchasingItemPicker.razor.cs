using ErpWeb.Core.Lookups;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.Lookups;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Lookups;

public partial class PoPurchasingItemPicker
{
    private SmartLookupInput<PoPurchasingItemLookupRow>? _smartLookup;
    private bool _popupVisible;
    private string? _popupSearchText;
    private string? _secondaryText;
    private string? _lastResolvedCode;

    [Inject] private IPoPurchasingItemLookupService Lookups { get; set; } = default!;

    [Parameter] public string? ICode { get; set; }
    [Parameter] public EventCallback<string?> ICodeChanged { get; set; }
    [Parameter] public EventCallback<PoPurchasingItemLookupRow> Selected { get; set; }
    [Parameter] public EventCallback Cleared { get; set; }
    [Parameter] public string? SecondaryName { get; set; }
    [Parameter] public bool Enabled { get; set; } = true;
    [Parameter] public string? InputCssClass { get; set; }
    [Parameter] public string? NullText { get; set; }
    [Parameter] public bool IncludeIndirect { get; set; } = true;
    [Parameter] public string? MenuCode { get; set; }
    [Parameter] public bool ShowUnitPrice { get; set; }

    private string? EffectiveSecondaryText => _secondaryText ?? SecondaryName;

    protected override void OnParametersSet()
    {
        var code = ICode?.Trim();
        if (!string.Equals(code, _lastResolvedCode, StringComparison.OrdinalIgnoreCase))
        {
            _secondaryText = null;
            _lastResolvedCode = code;
        }
    }

    private Task<LargeLookupResolveResult<PoPurchasingItemLookupRow>> ResolveItemAsync(
        string text,
        CancellationToken cancellationToken) =>
        Lookups.ResolveAsync(text, MenuCode, IncludeIndirect, cancellationToken);

    private async Task CommitSelectionAsync(PoPurchasingItemLookupRow row)
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

    private async Task OnPopupSelectedAsync(PoPurchasingItemLookupRow row)
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
