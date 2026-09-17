using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Transactions;

/// <summary>
/// The single UI surface allowed to touch MyInvois. It only ever calls
/// <see cref="ISaEInvoiceService"/> - never <c>IE_InvoiceRepository</c> / <c>ISubmitDocumentHelper</c>.
/// <para>
/// The panel is deliberately state-gated: the server remains authoritative for every rule, and the
/// buttons here merely mirror <see cref="SaEInvoiceStatusView"/> so an operator cannot attempt a
/// transition the lifecycle forbids. All actions are disabled while one is in flight, which stops
/// double-click submissions from the UI; the pre-submit gate stops them everywhere else.
/// </para>
/// </summary>
public partial class SaEInvoicePanel : ComponentBase
{
    [Inject] private ISaEInvoiceService EInvoices { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    /// <summary><see cref="EInvoiceDocumentTypes"/> - INV, CN or DN.</summary>
    [Parameter] public string? DocType { get; set; }

    [Parameter] public string? DocNo { get; set; }

    /// <summary>Menu the SUBMIT / CANCEL permissions are checked against.</summary>
    [Parameter] public string? MenuCode { get; set; }

    /// <summary>
    /// Host gate: true only for a saved document the operator may act on (posted, not dirty, not
    /// being saved). The panel never enables an action from status alone.
    /// </summary>
    [Parameter] public bool Enabled { get; set; } = true;

    /// <summary>
    /// Raised with the current e-Invoice status after the status is (re)read or an operation ran. The
    /// host uses it to enforce the structural edit lock in the UI, so a SUBMITTING/SUBMITTED/VALID
    /// document cannot be edited under a submission.
    /// </summary>
    [Parameter] public EventCallback<string?> OnStateChanged { get; set; }

    private SaEInvoiceStatusView? _status;
    private string _loadedKey = string.Empty;
    private string? _message;
    private bool _messageIsError;
    private IReadOnlyDictionary<string, string> _validationErrors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private bool _busy;
    private string? _busyAction;
    private bool _canSubmitPermission;
    private bool _canCancelPermission;
    private bool _cancelVisible;
    private string _cancelReason = string.Empty;
    private string? _cancelError;

    private SaEInvoiceDocumentKey Key => new()
    {
        DocumentType = (DocType ?? string.Empty).Trim().ToUpperInvariant(),
        DocumentNo = (DocNo ?? string.Empty).Trim()
    };

    private bool CanAct => Enabled && !_busy;

    private string StatusText => EInvoiceStatuses.Normalize(_status?.Status);

    private string StatusCss => StatusText.ToLowerInvariant() switch
    {
        "valid" => "ok",
        "submitted" or "submitting" => "pending",
        "invalid" or "rejected" or "failed" => "error",
        "cancelled" => "muted",
        _ => "new"
    };

    /// <summary>Status plus outcome, e.g. <c>FAILED (Unknown)</c>.</summary>
    private string StatusDetail =>
        string.IsNullOrWhiteSpace(_status?.Outcome) ? StatusText : $"{StatusText} ({_status!.Outcome})";

    protected override async Task OnParametersSetAsync()
    {
        var type = (DocType ?? string.Empty).Trim().ToUpperInvariant();
        var no = (DocNo ?? string.Empty).Trim();
        var key = $"{type}:{no}:{Enabled}";
        if (string.Equals(_loadedKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _loadedKey = key;

        // A new / unsaved document has no identity yet - there is nothing to load or act on.
        if (!EInvoiceDocumentTypes.IsKnown(type) || no.Length == 0)
        {
            _status = null;
            _message = null;
            return;
        }

        await LoadPermissionsAsync();
        await LoadStatusAsync();
        await OnStateChanged.InvokeAsync(_status?.Status);
    }

    /// <summary>Re-reads the status. Exposed so the host can call it after saving.</summary>
    public async Task RefreshAsync()
    {
        await LoadStatusAsync();
        await OnStateChanged.InvokeAsync(_status?.Status);
        StateHasChanged();
    }

    private async Task LoadPermissionsAsync()
    {
        var menu = (MenuCode ?? string.Empty).Trim();
        if (menu.Length == 0)
        {
            _canSubmitPermission = false;
            _canCancelPermission = false;
            return;
        }

        _canSubmitPermission = await AccessRights.CanAsync(menu, PermissionCodes.Submit);
        _canCancelPermission = await AccessRights.CanAsync(menu, PermissionCodes.Cancel);
    }

    private async Task LoadStatusAsync()
    {
        try
        {
            _status = await EInvoices.GetStatusAsync(Key);
        }
        catch (Exception ex)
        {
            _status = null;
            _message = $"Could not read the e-Invoice status: {ex.Message}";
            _messageIsError = true;
        }
    }

    private bool CanSubmit => CanAct && _canSubmitPermission && (_status?.CanSubmit ?? false);
    private bool CanRetry => CanAct && _canSubmitPermission && (_status?.CanRetry ?? false);
    private bool CanRecover => CanAct && (_canSubmitPermission || _canCancelPermission) && (_status?.CanRecover ?? false);
    private bool CanRefresh => CanAct && (_status?.CanRefresh ?? false);
    private bool CanCancel => CanAct && _canCancelPermission && (_status?.CanCancel ?? false);

    private Task OnValidateAsync() =>
        RunAsync("Validating", () => EInvoices.ValidateAsync(Key));

    private Task OnSubmitAsync() =>
        RunAsync("Submitting", () => EInvoices.SubmitAsync(Key));

    private Task OnRetryAsync() =>
        RunAsync("Retrying", () => EInvoices.RetryAsync(Key));

    private Task OnRecoverAsync() =>
        RunAsync("Recovering", () => EInvoices.RecoverAsync(Key, operatorInitiated: true));

    private Task OnRefreshAsync() =>
        RunAsync("Refreshing", () => EInvoices.RefreshAsync(Key));

    private async Task RunAsync(string label, Func<Task<SaEInvoiceResult>> action)
    {
        if (!CanAct)
        {
            return;
        }

        _busy = true;
        _busyAction = label;
        _message = null;
        _messageIsError = false;
        _validationErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var result = await action();
            ApplyResult(result);

            // Even a refused action can have written an audit row (e.g. a validation failure),
            // so always re-read the state the operator is looking at.
            await LoadStatusAsync();

            // Reported for every outcome: a submit that timed out leaves the document locked, and the
            // host must stop offering structural edits for it.
            await OnStateChanged.InvokeAsync(_status?.Status);
        }
        catch (Exception ex)
        {
            _message = ex.Message;
            _messageIsError = true;
        }
        finally
        {
            _busy = false;
            _busyAction = null;
        }
    }

    private void ApplyResult(SaEInvoiceResult result)
    {
        if (result.Succeeded)
        {
            var status = string.IsNullOrWhiteSpace(result.Status) ? string.Empty : $" Status is now {result.Status}.";
            var uuid = string.IsNullOrWhiteSpace(result.Uuid) ? string.Empty : $" UUID {result.Uuid}.";
            _message = $"{result.DocumentType} {result.DocumentNo} completed.{status}{uuid}";
            return;
        }

        _messageIsError = true;
        _validationErrors = result.ValidationErrors;
        _message = result.RecoveryRequired
            ? $"{result.ErrorMessage} Run Recover before submitting again."
            : result.ErrorMessage;
    }

    private void OpenCancel()
    {
        _cancelReason = string.Empty;
        _cancelError = null;
        _cancelVisible = true;
    }

    private async Task ConfirmCancelAsync()
    {
        var reason = (_cancelReason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            // The service enforces this too; the popup just avoids a pointless round trip.
            _cancelError = "A cancellation reason is required.";
            return;
        }

        _cancelVisible = false;
        await RunAsync("Cancelling", () => EInvoices.CancelAsync(Key, reason));
    }
}
