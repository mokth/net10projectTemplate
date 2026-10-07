using System.Linq.Expressions;
using ErpWeb.Core.Lookups;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ErpWeb.UI.Components.Common.Lookups;

public partial class SmartLookupInput<TItem> : IAsyncDisposable
{
    private readonly SmartLookupState _state = new();
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _resolveCts;
    private CancellationTokenSource? _notFoundRestoreCts;
    private string? _lastSyncedValue;
    private string? _lastSyncedDisplay;
    private string? _lastSyncedSecondary;

    [Parameter] public string? Value { get; set; }
    [Parameter] public string? DisplayText { get; set; }
    [Parameter] public string? SecondaryText { get; set; }
    [Parameter] public bool Enabled { get; set; } = true;
    [Parameter] public bool ReadOnly { get; set; }
    [Parameter] public string? NullText { get; set; }
    [Parameter] public string? CssClass { get; set; }
    [Parameter] public string? InputCssClass { get; set; }
    [Parameter] public string SearchButtonAriaLabel { get; set; } = "Search";
    [Parameter] public string NotFoundMessage { get; set; } = "Not found";
    [Parameter] public int InputDelayMs { get; set; } = SmartLookupState.DefaultInputDelayMs;
    [Parameter] public int NotFoundRestoreDelayMs { get; set; } = SmartLookupState.DefaultNotFoundRestoreDelayMs;

    /// <summary>Exact resolve for the typed code. Must not run a broad Contains search.</summary>
    [Parameter] public Func<string, CancellationToken, Task<LargeLookupResolveResult<TItem>>>? ResolveAsync { get; set; }

    [Parameter] public Func<TItem, string>? GetCode { get; set; }
    [Parameter] public Func<TItem, string?>? GetDisplayText { get; set; }
    [Parameter] public Func<TItem, string?>? GetSecondaryText { get; set; }

    /// <summary>Fired only when a typed/popup resolve commits a new business value.</summary>
    [Parameter] public EventCallback<TItem> ItemResolved { get; set; }

    /// <summary>Fired only for an explicit clear (empty input), never for invalid replacement.</summary>
    [Parameter] public EventCallback Cleared { get; set; }

    /// <summary>Opens the domain search popup; argument is the current typed seed text.</summary>
    [Parameter] public EventCallback<string?> OpenSearch { get; set; }

    [Parameter] public Expression<Func<string?>>? TextExpression { get; set; }

    private string? EffectiveSecondaryText =>
        _state.SecondaryText ?? SecondaryText;

    protected override void OnParametersSet()
    {
        var disabled = !Enabled;
        if (_state.IsDisabled != disabled)
        {
            _state.SetDisabled(disabled);
        }

        var value = Value?.Trim();
        var display = DisplayText?.Trim();
        var secondary = SecondaryText?.Trim();

        var valueChanged = !string.Equals(value, _lastSyncedValue, StringComparison.Ordinal);
        var displayChanged = !string.Equals(display, _lastSyncedDisplay, StringComparison.Ordinal);
        var secondaryChanged = !string.Equals(secondary, _lastSyncedSecondary, StringComparison.Ordinal);

        if (valueChanged || (displayChanged && !_state.IsResolving && _state.Status != SmartLookupStatus.Editing))
        {
            // Avoid stomping in-progress typing when only secondary metadata refreshes.
            if (valueChanged || !_state.Status.IsTransientEdit())
            {
                _state.SyncFromExternal(value, display ?? value, secondary, cancelPending: valueChanged);
                CancelPendingWork();
            }

            _lastSyncedValue = value;
            _lastSyncedDisplay = display;
            _lastSyncedSecondary = secondary;
        }
        else if (secondaryChanged && _state.Status == SmartLookupStatus.Committed)
        {
            _state.SyncFromExternal(
                _state.CommittedValue,
                _state.CommittedDisplayText,
                secondary,
                cancelPending: false);
            _lastSyncedSecondary = secondary;
        }
    }

    private string BuildInputCssClass()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(InputCssClass))
        {
            parts.Add(InputCssClass);
        }

        if (_state.ShowInvalid)
        {
            parts.Add("smart-lookup__input--invalid");
        }

        return string.Join(' ', parts);
    }

    private async Task OnTextChangedAsync(string newText)
    {
        if (ReadOnly || !Enabled)
        {
            return;
        }

        CancelNotFoundRestore();
        _state.BeginUserEdit(newText, resolveNow: false);
        await ScheduleDebouncedResolveAsync();
    }

    private async Task OnKeyDownAsync(KeyboardEventArgs e)
    {
        if (ReadOnly || !Enabled)
        {
            return;
        }

        if (e.Key == "Enter")
        {
            CancelDebounce();
            CancelNotFoundRestore();
            await ResolveNowAsync();
        }
        else if (e.Key == "Escape")
        {
            CancelPendingWork();
            _state.CancelEdit();
        }
    }

    private async Task OpenSearchAsync()
    {
        if (!Enabled || ReadOnly || !OpenSearch.HasDelegate)
        {
            return;
        }

        CancelPendingWork();
        if (_state.Status is SmartLookupStatus.Editing or SmartLookupStatus.NotFound or SmartLookupStatus.Error)
        {
            // Keep typed seed; do not force-restore yet.
        }

        await OpenSearch.InvokeAsync(_state.GetSearchSeedText());
    }

    /// <summary>Commit a selection from the domain popup using the same path as typed resolve.</summary>
    public async Task CommitItemAsync(TItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        CancelPendingWork();

        var code = RequireCode(item);
        var display = GetDisplayText?.Invoke(item) ?? code;
        var secondary = GetSecondaryText?.Invoke(item);

        _state.SyncFromExternal(code, display, secondary, cancelPending: true);
        _lastSyncedValue = code;
        _lastSyncedDisplay = display;
        _lastSyncedSecondary = secondary;

        if (ItemResolved.HasDelegate)
        {
            await ItemResolved.InvokeAsync(item);
        }
    }

    private async Task ScheduleDebouncedResolveAsync()
    {
        CancelDebounce();
        var delay = Math.Max(0, InputDelayMs);
        var cts = new CancellationTokenSource();
        _debounceCts = cts;

        try
        {
            await Task.Delay(delay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        await ResolveNowAsync();
    }

    private async Task ResolveNowAsync()
    {
        var request = _state.BeginResolve();
        if (request is null)
        {
            return;
        }

        if (request.Value.IsClear)
        {
            if (_state.TryApplyExplicitClear(request.Value.Sequence))
            {
                _lastSyncedValue = null;
                _lastSyncedDisplay = null;
                _lastSyncedSecondary = null;
                if (Cleared.HasDelegate)
                {
                    await Cleared.InvokeAsync();
                }
            }

            return;
        }

        if (ResolveAsync is null)
        {
            _state.TryApplyError(request.Value.Sequence, "Lookup is not configured.");
            return;
        }

        CancelResolve();
        var resolveCts = new CancellationTokenSource();
        _resolveCts = resolveCts;

        LargeLookupResolveResult<TItem> result;
        try
        {
            result = await ResolveAsync(request.Value.Text, resolveCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (_state.TryApplyError(request.Value.Sequence, ex.Message))
            {
                await ScheduleNotFoundRestoreAsync(request.Value.Sequence);
            }

            return;
        }

        if (resolveCts.IsCancellationRequested || !_state.IsCurrent(request.Value.Sequence))
        {
            return;
        }

        if (result.Succeeded && result.Item is not null)
        {
            var item = result.Item;
            var code = RequireCode(item);
            var display = GetDisplayText?.Invoke(item) ?? code;
            var secondary = GetSecondaryText?.Invoke(item);
            if (_state.TryApplyResolved(request.Value.Sequence, code, display, secondary))
            {
                _lastSyncedValue = code;
                _lastSyncedDisplay = display;
                _lastSyncedSecondary = secondary;
                if (ItemResolved.HasDelegate)
                {
                    await ItemResolved.InvokeAsync(item);
                }
            }

            return;
        }

        var message = result.Ambiguous
            ? (result.ErrorMessage ?? "Multiple matches. Use search.")
            : (result.ErrorMessage ?? NotFoundMessage);

        if (_state.TryApplyNotFound(request.Value.Sequence, message))
        {
            await ScheduleNotFoundRestoreAsync(request.Value.Sequence);
        }
    }

    private async Task ScheduleNotFoundRestoreAsync(long sequence)
    {
        CancelNotFoundRestore();
        var delay = Math.Max(0, NotFoundRestoreDelayMs);
        var cts = new CancellationTokenSource();
        _notFoundRestoreCts = cts;

        try
        {
            await Task.Delay(delay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        _state.TryRestoreAfterNotFound(sequence);
        await InvokeAsync(StateHasChanged);
    }

    private string RequireCode(TItem item)
    {
        if (GetCode is null)
        {
            throw new InvalidOperationException($"{nameof(GetCode)} is required.");
        }

        var code = GetCode(item)?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            throw new InvalidOperationException("Resolved item did not provide a code.");
        }

        return code;
    }

    private void CancelPendingWork()
    {
        CancelDebounce();
        CancelResolve();
        CancelNotFoundRestore();
    }

    private void CancelDebounce()
    {
        try
        {
            _debounceCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _debounceCts?.Dispose();
        _debounceCts = null;
    }

    private void CancelResolve()
    {
        try
        {
            _resolveCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _resolveCts?.Dispose();
        _resolveCts = null;
    }

    private void CancelNotFoundRestore()
    {
        try
        {
            _notFoundRestoreCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _notFoundRestoreCts?.Dispose();
        _notFoundRestoreCts = null;
    }

    public ValueTask DisposeAsync()
    {
        CancelPendingWork();
        return ValueTask.CompletedTask;
    }
}

file static class SmartLookupStatusExtensions
{
    public static bool IsTransientEdit(this SmartLookupStatus status) =>
        status is SmartLookupStatus.Editing
            or SmartLookupStatus.Resolving
            or SmartLookupStatus.NotFound
            or SmartLookupStatus.Error;
}
