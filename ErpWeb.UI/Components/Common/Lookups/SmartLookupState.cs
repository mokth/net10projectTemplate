namespace ErpWeb.UI.Components.Common.Lookups;

/// <summary>
/// Pure state controller for smart large lookups: separates pending input text from the
/// committed business value, and protects against stale resolve / not-found restore races.
/// Testable without a Razor renderer.
/// </summary>
public sealed class SmartLookupState
{
    public const int DefaultInputDelayMs = 550;
    public const int DefaultNotFoundRestoreDelayMs = 900;

    private long _requestSequence;
    private bool _disabled;
    private string? _pendingNotFoundMessage;

    public string InputText { get; private set; } = string.Empty;
    public string? CommittedValue { get; private set; }
    public string CommittedDisplayText { get; private set; } = string.Empty;
    public string? SecondaryText { get; private set; }
    public SmartLookupStatus Status { get; private set; } = SmartLookupStatus.Empty;
    public string? ErrorMessage { get; private set; }
    public long RequestSequence => _requestSequence;
    public bool IsDisabled => _disabled;
    public bool IsResolving => Status == SmartLookupStatus.Resolving;
    public bool HasCommittedValue => !string.IsNullOrEmpty(CommittedValue);
    public bool ShowInvalid => Status is SmartLookupStatus.NotFound or SmartLookupStatus.Error;

    /// <summary>
    /// Sync display from an externally committed value (parent parameter / popup selection applied upstream).
    /// Does not bump the request sequence unless <paramref name="cancelPending"/> is true.
    /// </summary>
    public void SyncFromExternal(
        string? value,
        string? displayText = null,
        string? secondaryText = null,
        bool cancelPending = true)
    {
        if (cancelPending)
        {
            BumpSequence();
        }

        var trimmed = Normalize(value);
        if (trimmed.Length == 0)
        {
            CommittedValue = null;
            CommittedDisplayText = string.Empty;
            SecondaryText = null;
            InputText = string.Empty;
            ErrorMessage = null;
            Status = _disabled ? SmartLookupStatus.Disabled : SmartLookupStatus.Empty;
            return;
        }

        CommittedValue = trimmed;
        CommittedDisplayText = string.IsNullOrWhiteSpace(displayText) ? trimmed : displayText.Trim();
        SecondaryText = string.IsNullOrWhiteSpace(secondaryText) ? null : secondaryText.Trim();
        InputText = CommittedDisplayText;
        ErrorMessage = null;
        Status = _disabled ? SmartLookupStatus.Disabled : SmartLookupStatus.Committed;
    }

    public void SetDisabled(bool disabled)
    {
        _disabled = disabled;
        if (disabled)
        {
            BumpSequence();
            ErrorMessage = null;
            Status = SmartLookupStatus.Disabled;
            return;
        }

        if (HasCommittedValue)
        {
            Status = SmartLookupStatus.Committed;
            InputText = CommittedDisplayText;
        }
        else if (string.IsNullOrWhiteSpace(InputText))
        {
            Status = SmartLookupStatus.Empty;
        }
        else
        {
            Status = SmartLookupStatus.Editing;
        }
    }

    /// <summary>
    /// User changed the input. Returns a resolve request when the text should be resolved
    /// (immediate for Enter, or when the caller has already applied debounce).
    /// Empty text after a committed value is treated as an explicit clear request.
    /// </summary>
    public SmartLookupResolveRequest? BeginUserEdit(string? text, bool resolveNow)
    {
        if (_disabled)
        {
            return null;
        }

        var normalized = Normalize(text);
        InputText = text ?? string.Empty;
        ErrorMessage = null;

        if (normalized.Length == 0)
        {
            if (!HasCommittedValue && string.IsNullOrEmpty(CommittedDisplayText))
            {
                Status = SmartLookupStatus.Empty;
                BumpSequence();
                return null;
            }

            // Explicit clear of the field (user deleted the committed code).
            var clearSeq = BumpSequence();
            Status = SmartLookupStatus.Resolving;
            return new SmartLookupResolveRequest(clearSeq, string.Empty, IsClear: true);
        }

        if (string.Equals(normalized, Normalize(CommittedValue), StringComparison.OrdinalIgnoreCase)
            && string.Equals(normalized, Normalize(CommittedDisplayText), StringComparison.OrdinalIgnoreCase))
        {
            // User retyped the same committed code — keep committed, no resolve churn.
            InputText = CommittedDisplayText;
            SecondaryText = SecondaryText;
            Status = SmartLookupStatus.Committed;
            BumpSequence();
            return null;
        }

        // Always bump so in-flight resolve / not-found restore cannot overwrite newer typing.
        var sequence = BumpSequence();
        Status = SmartLookupStatus.Editing;
        if (!resolveNow)
        {
            return null;
        }

        Status = SmartLookupStatus.Resolving;
        return new SmartLookupResolveRequest(sequence, normalized, IsClear: false);
    }

    /// <summary>
    /// Start a resolve for the current input (after debounce or Enter). Returns null when disabled/empty-noop.
    /// </summary>
    public SmartLookupResolveRequest? BeginResolve()
    {
        if (_disabled)
        {
            return null;
        }

        var normalized = Normalize(InputText);
        if (normalized.Length == 0)
        {
            if (!HasCommittedValue)
            {
                Status = SmartLookupStatus.Empty;
                BumpSequence();
                return null;
            }

            var clearSeq = BumpSequence();
            Status = SmartLookupStatus.Resolving;
            return new SmartLookupResolveRequest(clearSeq, string.Empty, IsClear: true);
        }

        if (string.Equals(normalized, Normalize(CommittedValue), StringComparison.OrdinalIgnoreCase)
            && Status == SmartLookupStatus.Committed)
        {
            return null;
        }

        var sequence = BumpSequence();
        Status = SmartLookupStatus.Resolving;
        ErrorMessage = null;
        return new SmartLookupResolveRequest(sequence, normalized, IsClear: false);
    }

    public bool IsCurrent(long sequence) => sequence == _requestSequence;

    /// <summary>
    /// Apply a successful resolve. Returns false when the result is stale.
    /// </summary>
    public bool TryApplyResolved(
        long sequence,
        string value,
        string? displayText = null,
        string? secondaryText = null)
    {
        if (!IsCurrent(sequence) || _disabled)
        {
            return false;
        }

        var trimmed = Normalize(value);
        if (trimmed.Length == 0)
        {
            return false;
        }

        CommittedValue = trimmed;
        CommittedDisplayText = string.IsNullOrWhiteSpace(displayText) ? trimmed : displayText.Trim();
        SecondaryText = string.IsNullOrWhiteSpace(secondaryText) ? null : secondaryText.Trim();
        InputText = CommittedDisplayText;
        ErrorMessage = null;
        _pendingNotFoundMessage = null;
        Status = SmartLookupStatus.Committed;
        return true;
    }

    /// <summary>
    /// Apply an explicit clear (empty input resolve). Emits a business null only via the return flag.
    /// </summary>
    public bool TryApplyExplicitClear(long sequence)
    {
        if (!IsCurrent(sequence) || _disabled)
        {
            return false;
        }

        CommittedValue = null;
        CommittedDisplayText = string.Empty;
        SecondaryText = null;
        InputText = string.Empty;
        ErrorMessage = null;
        _pendingNotFoundMessage = null;
        Status = SmartLookupStatus.Empty;
        return true;
    }

    /// <summary>
    /// Apply not-found / error for a resolve attempt. Does not clear the committed business value.
    /// </summary>
    public bool TryApplyNotFound(long sequence, string? message)
    {
        if (!IsCurrent(sequence) || _disabled)
        {
            return false;
        }

        _pendingNotFoundMessage = string.IsNullOrWhiteSpace(message) ? "Not found" : message.Trim();
        ErrorMessage = _pendingNotFoundMessage;
        Status = SmartLookupStatus.NotFound;
        return true;
    }

    public bool TryApplyError(long sequence, string? message)
    {
        if (!IsCurrent(sequence) || _disabled)
        {
            return false;
        }

        ErrorMessage = string.IsNullOrWhiteSpace(message) ? "Lookup failed." : message.Trim();
        Status = SmartLookupStatus.Error;
        return true;
    }

    /// <summary>
    /// After the not-found restore delay: restore committed display, or clear the input when nothing
    /// was committed. Never reports a business null for an invalid replacement.
    /// </summary>
    public bool TryRestoreAfterNotFound(long sequence)
    {
        if (!IsCurrent(sequence) || _disabled)
        {
            return false;
        }

        if (Status is not SmartLookupStatus.NotFound and not SmartLookupStatus.Error)
        {
            return false;
        }

        ErrorMessage = null;
        _pendingNotFoundMessage = null;

        if (HasCommittedValue)
        {
            InputText = CommittedDisplayText;
            Status = SmartLookupStatus.Committed;
        }
        else
        {
            InputText = string.Empty;
            SecondaryText = null;
            Status = SmartLookupStatus.Empty;
        }

        return true;
    }

    /// <summary>
    /// Escape: cancel pending edit and restore the last committed display without emitting null.
    /// </summary>
    public void CancelEdit()
    {
        if (_disabled)
        {
            return;
        }

        BumpSequence();
        ErrorMessage = null;
        _pendingNotFoundMessage = null;

        if (HasCommittedValue)
        {
            InputText = CommittedDisplayText;
            Status = SmartLookupStatus.Committed;
        }
        else
        {
            InputText = string.Empty;
            SecondaryText = null;
            Status = SmartLookupStatus.Empty;
        }
    }

    /// <summary>
    /// Text currently typed that should seed the search popup.
    /// </summary>
    public string GetSearchSeedText()
    {
        var pending = Normalize(InputText);
        if (pending.Length > 0
            && !string.Equals(pending, Normalize(CommittedValue), StringComparison.OrdinalIgnoreCase))
        {
            return pending;
        }

        return Normalize(InputText);
    }

    private long BumpSequence() => Interlocked.Increment(ref _requestSequence);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();
}

public enum SmartLookupStatus
{
    Empty,
    Committed,
    Editing,
    Resolving,
    Resolved,
    NotFound,
    Error,
    Disabled
}

public readonly record struct SmartLookupResolveRequest(long Sequence, string Text, bool IsClear);
