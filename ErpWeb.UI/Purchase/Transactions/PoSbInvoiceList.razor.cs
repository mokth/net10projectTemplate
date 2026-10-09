using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Purchase.Transactions;

/// <summary>
/// Self-billed purchase invoice list (LHDN type 11).
///
/// <para>
/// The e-Invoice toolbar mirrors the sales lists: every batch action is pre-flighted with
/// <c>GetStatusManyAsync</c> so an ineligible document is named before the operator commits, and the
/// server re-checks everything (the UI is never the gate). There is no POST/ROLLBACK: the self-billed
/// documents have no ERP finalisation step, so the e-Invoice state is their whole lifecycle.
/// </para>
/// </summary>
public partial class PoSbInvoiceList : PageBase, IDisposable
{
    [Inject] private IPoSbInvoiceService Invoices { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private ISaEInvoiceService EInvoices { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private readonly List<PoSbInvoiceListRow> _selectedRows = [];
    private CancellationTokenSource? _einvCts;

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected List<PoSbInvoiceListRow> CompactRows { get; set; } = [];

    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDelete;
    protected bool CanSubmitEInv;
    protected bool CanCancelEInv;
    protected bool CanAccessEInvoiceTin;

    protected string? AppliedStatus;
    protected DateTime? AppliedDateFrom;
    protected DateTime? AppliedDateTo;
    protected string DraftStatusKey = "all";
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;

    protected string ConfirmMessage { get; set; } = string.Empty;
    protected string ConfirmAction { get; set; } = "DELETE";

    // The same action keys the sales lists use, so the two families cannot drift apart.
    private const string EInvSubmitAction = "EINV_SUBMIT";
    private const string EInvStatusAction = "EINV_STATUS";
    private const string EInvCancelAction = "EINV_CANCEL";

    protected string ConfirmButtonText => ConfirmAction switch
    {
        EInvSubmitAction => "Submit",
        EInvStatusAction => "Refresh",
        EInvCancelAction => "Cancel e-Invoice",
        _ => "Delete"
    };

    protected ButtonRenderStyle ConfirmButtonStyle => ConfirmAction switch
    {
        EInvSubmitAction => ButtonRenderStyle.Primary,
        EInvStatusAction => ButtonRenderStyle.Primary,
        EInvCancelAction => ButtonRenderStyle.Danger,
        _ => ButtonRenderStyle.Danger
    };

    protected PoSbInvoiceGridDataSource DataSource { get; private set; } = default!;

    // ── e-Invoice batch state ────────────────────────────────────────────────

    protected string EInvoiceCancelReason { get; set; } = string.Empty;

    protected bool IsEInvoiceBusy { get; private set; }

    /// <summary>
    /// Progress of a running refresh-all run, e.g. "Refreshing e-Invoice status… 12 of 43 completed".
    /// Null when no run is active, which is also what hides the progress strip.
    /// </summary>
    protected string? EInvoiceProgress;

    protected bool EInvoiceResultsVisible { get; set; }

    protected string EInvoiceResultsTitle { get; set; } = string.Empty;

    protected List<SaEInvoiceBatchItemResult> EInvoiceResults { get; set; } = [];

    protected bool IsEInvoiceAction =>
        ConfirmAction is EInvSubmitAction or EInvStatusAction or EInvCancelAction;

    /// <summary>True while the confirmation dialog is for a CANCEL, which needs a reason.</summary>
    protected bool IsCancelAction => ConfirmAction == EInvCancelAction;

    protected bool CanConfirmAction =>
        !IsSubmitting && (!IsCancelAction || !string.IsNullOrWhiteSpace(EInvoiceCancelReason));

    protected string TotalCountLabel =>
        TotalCount == 1 ? "1 self-billed invoice" : $"{TotalCount:N0} self-billed invoices";

    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedStatus)
        || AppliedDateFrom is not null
        || AppliedDateTo is not null;

    protected IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new("all", "All"),
        new(PoSbStatuses.New, PoSbStatuses.New),
        // Kept for legacy rows only: nothing writes POSTED any more (the ERP NEW/POSTED dimension is
        // retired for self-billed documents), but a pre-change row may still hold it.
        new(PoSbStatuses.Posted, PoSbStatuses.Posted)
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Doc No.", FieldName = nameof(PoSbInvoiceListRow.DocNo), SortIndex = 0, VisibleIndex = 1, Size = GridColumnSize.DocumentNo },
        new() { Caption = "Date", FieldName = nameof(PoSbInvoiceListRow.DocDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", VisibleIndex = 2, Size = GridColumnSize.Date },
        new() { Caption = "Status", FieldName = nameof(PoSbInvoiceListRow.Status), Width = "150px", VisibleIndex = 3, Size = GridColumnSize.Status },
        new() { Caption = "Vendor", FieldName = nameof(PoSbInvoiceListRow.VendorCode), VisibleIndex = 4, Size = GridColumnSize.Code },
        new() { Caption = "Name", FieldName = nameof(PoSbInvoiceListRow.VendorName), VisibleIndex = 5, Size = GridColumnSize.Name },
        new() { Caption = "Total (incl. tax)", FieldName = nameof(PoSbInvoiceListRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", VisibleIndex = 6, Size = GridColumnSize.Amount },
        new() { Caption = "Lines", FieldName = nameof(PoSbInvoiceListRow.LineCount), Width = "80px", VisibleIndex = 7, Size = GridColumnSize.Tiny },
        // "E-Inv" and "E-Status" deliberately bind the SAME IrbmStatus property: that is what the sales
        // lists do (one value rendered twice), so VisibleIndex must stay unique.
        new() { Caption = "E-Inv", FieldName = nameof(PoSbInvoiceListRow.IrbmStatus), VisibleIndex = 8, Size = GridColumnSize.Status },
        new() { Caption = "E-UUID", FieldName = nameof(PoSbInvoiceListRow.IrbmUuid), DataType = "link", VisibleIndex = 9, Size = GridColumnSize.Reference },
        new() { Caption = "E-Status", FieldName = nameof(PoSbInvoiceListRow.IrbmStatus), DataType = "string", VisibleIndex = 10, Size = GridColumnSize.Status },
        ..AuditColumns.For(startVisibleIndex: 11)
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new PoSbInvoiceGridDataSource(SearchPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.PurchaseSbInvoice, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.PurchaseSbInvoice, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.PurchaseSbInvoice, PermissionCodes.Delete);
        CanSubmitEInv = await AccessRights.CanAsync(MenuCodes.PurchaseSbInvoice, PermissionCodes.Submit);
        CanCancelEInv = await AccessRights.CanAsync(MenuCodes.PurchaseSbInvoice, PermissionCodes.Cancel);
        CanAccessEInvoiceTin = await AccessRights.CanAccessAsync(MenuCodes.SalesEInvoiceTin);

        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "DELETE", IConClass = "far fa-trash-alt", Style = "danger", Enabled = CanDelete },
            new() { Text = "SUBMIT", IConClass = "fas fa-paper-plane", Style = "primary", Enabled = CanSubmitEInv, ToolTip = "Submit the selected invoices to MyInvois" },
            new() { Text = "E-STATUS", IConClass = "fas fa-arrows-rotate", Style = "primary", Enabled = CanSubmitEInv, ToolTip = "Refresh the MyInvois status of the selected invoices; with nothing selected, every submitted invoice in this branch" },
            new() { Text = "CANCEL", IConClass = "fas fa-ban", Style = "danger", Enabled = CanCancelEInv, ToolTip = "Cancel the selected e-Invoices at MyInvois" }
        ];

        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View document" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit document", Enabled = CanEdit },
            new() { Text = "LHDN", IConClass = "fa-solid fa-triangle-exclamation", Style = "primary", ToolTip = "View the LHDN validation detail (INVALID documents only)", Enabled = CanAccessEInvoiceTin }
        ];

        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected void OnSelectionsEvent(List<PoSbInvoiceListRow> list)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(list);
    }

    protected async Task OnButtonClick(SelectedButtonInfo<PoSbInvoiceListRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();

        // A batch e-Invoice action is in flight: ignore re-entry instead of starting a second batch.
        if (IsEInvoiceBusy && mode is "SUBMIT" or "E-STATUS" or "CANCEL")
        {
            return;
        }

        switch (mode)
        {
            case "NEW":
                if (!CanAdd) { StatusMessage = "Access Denied!!"; return; }
                Navigation.NavigateTo("/purchase/self-billed-invoices/new");
                break;
            case "DELETE": await BeginDeleteAsync(); break;
            case "SUBMIT": await BeginEInvoiceAsync(EInvSubmitAction); break;
            case "E-STATUS": await BeginEInvoiceAsync(EInvStatusAction); break;
            case "CANCEL": await BeginEInvoiceAsync(EInvCancelAction); break;
            case "REFRESH": await ReloadGridAsync(); break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoSbInvoiceListRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return Task.CompletedTask;
        }

        var row = info.SelectedRow;
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();

        if (mode == "VIEW")
        {
            NavigateView(row.DocNo);
        }
        else if (mode == "EDIT")
        {
            if (!CanEdit) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
            if (!row.CanEdit)
            {
                ErrorMessage = "Only NEW, unlocked documents can be edited.";
                return Task.CompletedTask;
            }

            Navigation.NavigateTo($"/purchase/self-billed-invoices/edit/{row.DocNo}");
        }
        else if (mode == "LHDN")
        {
            // The gate lives in EInvoiceDetailLink: INVALID-only, because MyInvois directs that Get
            // Document Details be used for invalid-document error details only.
            var (url, error) = EInvoiceDetailLink.Resolve(
                row.IrbmStatus, row.IrbmUuid, CanAccessEInvoiceTin);
            if (url is null)
            {
                ErrorMessage = error;
                return Task.CompletedTask;
            }

            Navigation.NavigateTo(url);
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(string docNo) =>
        Navigation.NavigateTo($"/purchase/self-billed-invoices/view/{docNo}");

    /// <summary>
    /// Click on the grid's E-UUID cell. The shared rules live in
    /// <see cref="EInvoicePortalLinkOpener"/>, so this page reuses them verbatim instead of repeating the
    /// logic — exactly as the sales lists and the self-billed note list do.
    ///
    /// <para>
    /// An <b>INVALID</b> document opens the LHDN detail page (a portal page does not exist for them);
    /// anything else opens the share link and repairs the submission history on the way through.
    /// </para>
    /// </summary>
    protected async Task OnSelectedColumnHandle(SelectedColumnInfo info)
    {
        // The grid sets Context to the row item, so the status is available without another round trip.
        var row = info.Context as PoSbInvoiceListRow;

        var outcome = await EInvoicePortalLinkOpener.HandleUuidClickAsync(
            EInvoices, JsRuntime, info, row?.IrbmStatus, CanAccessEInvoiceTin);

        if (outcome.NavigateUrl is not null)
        {
            Navigation.NavigateTo(outcome.NavigateUrl);
            return;
        }

        if (outcome.Message is not null)
        {
            ErrorMessage = outcome.Message;
            return;
        }

        var result = outcome.Portal!;
        if (result.Opened)
        {
            ErrorMessage = null;
            StatusMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        // The repair commits through its own DbContext, so this page's rows are stale once it writes:
        // reload so E-Inv / E-Status show what it just recorded.
        if (result.StateChanged)
        {
            await ReloadGridAsync();
        }
    }

    // ── e-Invoice batch ─────────────────────────────────────────────────────

    private async Task BeginEInvoiceAsync(string action)
    {
        if (IsEInvoiceBusy)
        {
            return;
        }

        var permitted = action == EInvCancelAction ? CanCancelEInv : CanSubmitEInv;
        if (!permitted)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        var rows = DistinctSelectedRows();
        if (rows.Count == 0)
        {
            // Nothing selected + E-STATUS is the "refresh everything I can see" mode. SUBMIT and CANCEL
            // stay selection-only: they are write actions and must never run over the whole list.
            if (action == EInvStatusAction)
            {
                await BeginRefreshAllAsync();
                return;
            }

            StatusMessage = "No Record Selected!";
            return;
        }

        if (rows.Count > SaEInvoiceLimits.MaxBatchSelection)
        {
            ErrorMessage = $"Select at most {SaEInvoiceLimits.MaxBatchSelection} documents at a time.";
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;

        // Pre-flight snapshot for the confirmation prompt ONLY. It is not the eligibility verdict: the
        // service re-authorizes, reloads and re-validates every row when the action is confirmed.
        var keys = rows.Select(ToEInvoiceKey).ToList();
        IReadOnlyList<SaEInvoiceStatusView?> preview;
        try
        {
            preview = await EInvoices.GetStatusManyAsync(keys);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        var skipped = new List<string>();
        var eligible = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var reason = EInvoiceIneligibleReason(rows[i], i < preview.Count ? preview[i] : null, action);
            if (reason is null)
            {
                eligible++;
            }
            else
            {
                skipped.Add($"{rows[i].DocNo} ({reason})");
            }
        }

        if (eligible == 0)
        {
            ErrorMessage = "None of the selected documents can be used for this action. "
                + string.Join("; ", skipped);
            return;
        }

        ConfirmAction = action;
        ConfirmMessage = BuildEInvoiceConfirmMessage(action, eligible, rows.Count, skipped);
        EInvoiceCancelReason = string.Empty;
        ConfirmVisible = true;
    }

    private static string BuildEInvoiceConfirmMessage(
        string action, int eligible, int total, List<string> skipped)
    {
        var verb = action switch
        {
            EInvStatusAction => "Refresh the MyInvois status of",
            EInvCancelAction => "Cancel at MyInvois",
            _ => "Submit to MyInvois"
        };

        var message = $"{verb} {eligible} of {total} selected document(s)?";
        if (skipped.Count > 0)
        {
            message += " Skipped: " + string.Join("; ", skipped.Take(5));
            if (skipped.Count > 5)
            {
                message += $" (+{skipped.Count - 5} more)";
            }
        }

        if (action == EInvSubmitAction)
        {
            message += " The document is finalised at MyInvois and becomes read-only while it is being submitted.";
        }

        return message;
    }

    /// <summary>
    /// Why a row cannot take part in <paramref name="action"/>, or null when it can. Mirrors
    /// <see cref="SaEInvoiceStatusView"/> so the operator is told before committing; the server remains
    /// authoritative.
    /// </summary>
    private static string? EInvoiceIneligibleReason(
        PoSbInvoiceListRow row, SaEInvoiceStatusView? view, string action)
    {
        switch (action)
        {
            case EInvSubmitAction:
                if (view is null)
                {
                    return null;
                }

                if (!view.CanSubmit)
                {
                    return view.CanRecover
                        ? "needs Recover first"
                        : $"cannot be submitted from {view.Status}";
                }

                return null;

            case EInvStatusAction:
                if (string.IsNullOrWhiteSpace(row.IrbmUuid))
                {
                    return "no MyInvois UUID";
                }

                return view is not null && !view.CanRefresh ? $"cannot refresh from {view.Status}" : null;

            case EInvCancelAction:
                if (string.IsNullOrWhiteSpace(row.IrbmUuid))
                {
                    return "no MyInvois UUID";
                }

                return view is not null && !view.CanCancel ? $"cannot be cancelled from {view.Status}" : null;

            default:
                return null;
        }
    }

    private static SaEInvoiceDocumentKey ToEInvoiceKey(PoSbInvoiceListRow row) => new()
    {
        DocumentType = EInvoiceDocumentTypes.SelfBilledInvoice,
        DocumentNo = row.DocNo.Trim()
    };

    private async Task ExecuteEInvoiceBatchAsync()
    {
        using var blocking = BeginBlockingWork("Please wait. The e-Invoice action is still running.");
        IsSubmitting = true;
        IsEInvoiceBusy = true;
        SetEInvoiceButtonsEnabled(false);
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var action = ConfirmAction;
            var keys = DistinctSelectedRows().Select(ToEInvoiceKey).ToList();
            var reason = EInvoiceCancelReason;

            SaEInvoiceBatchResult result = action switch
            {
                EInvSubmitAction => await EInvoices.SubmitManyAsync(keys),
                EInvStatusAction => await EInvoices.RefreshManyAsync(keys),
                _ => await EInvoices.CancelManyAsync(keys, reason)
            };

            ConfirmVisible = false;
            EInvoiceCancelReason = string.Empty;

            if (result.Refused)
            {
                ErrorMessage = result.ErrorMessage;
                return;
            }

            EInvoiceResults = result.Items.ToList();
            EInvoiceResultsTitle = action switch
            {
                EInvSubmitAction => "Submit to MyInvois",
                EInvStatusAction => "MyInvois status refresh",
                _ => "Cancel e-Invoice at MyInvois"
            };
            EInvoiceResultsVisible = true;

            StatusMessage = $"{EInvoiceResultsTitle}: {result.SucceededCount} succeeded, "
                            + $"{result.FailedCount} failed, {result.SkippedCount} skipped.";

            // Keep the selection when a row needs attention so the operator can retry just those rows.
            if (result.FailedCount == 0 && result.SkippedCount == 0)
            {
                _selectedRows.Clear();
            }

            await ReloadGridAsync();
        }
        catch (Exception ex)
        {
            ConfirmVisible = false;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsEInvoiceBusy = false;
            IsSubmitting = false;
            SetEInvoiceButtonsEnabled(true);
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// The no-selection E-STATUS mode: refresh every <c>SUBMITTED</c> self-billed invoice the grid is
    /// currently showing. The scope is the grid's OWN query (<see cref="DataSource"/>.<c>CurrentQuery</c>),
    /// so what gets refreshed is exactly what the operator can see; the service decides the run cap before
    /// it makes any MyInvois call.
    ///
    /// <para>
    /// Unlike <see cref="ExecuteEInvoiceBatchAsync"/> there is no confirmation popup: a refresh is
    /// read-only, so the only thing worth showing is progress and a way out of a long run.
    /// </para>
    /// </summary>
    private async Task BeginRefreshAllAsync()
    {
        using var blocking = BeginBlockingWork("Please wait. The e-Invoice status refresh is still running.");
        IsSubmitting = true;
        IsEInvoiceBusy = true;
        SetEInvoiceButtonsEnabled(false);
        ErrorMessage = null;
        StatusMessage = null;
        EInvoiceProgress = null;
        _einvCts?.Dispose();
        _einvCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<SaEInvoiceRefreshProgress>(p =>
            {
                EInvoiceProgress = $"Refreshing e-Invoice status… {p.Done} of {p.Total} completed";
                _ = InvokeAsync(StateHasChanged);
            });

            var result = await EInvoices.RefreshSubmittedAsync(
                EInvoiceDocumentTypes.SelfBilledInvoice,
                DataSource.CurrentQuery,
                progress,
                _einvCts.Token);

            if (result.Refused)
            {
                ErrorMessage = result.ErrorMessage;
                return;
            }

            if (result.Items.Count == 0)
            {
                StatusMessage = "No submitted invoice found...";
                return;
            }

            EInvoiceResults = result.Items.ToList();
            EInvoiceResultsTitle = "MyInvois status refresh";
            EInvoiceResultsVisible = true;
            StatusMessage = $"{EInvoiceResultsTitle}: {result.SucceededCount} succeeded, "
                            + $"{result.FailedCount} failed, {result.SkippedCount} skipped.";

            await ReloadGridAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            EInvoiceProgress = null;
            IsEInvoiceBusy = false;
            IsSubmitting = false;
            SetEInvoiceButtonsEnabled(true);
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Stops a refresh-all run at the next chunk boundary. The in-flight chunk finishes and every result
    /// already produced is kept, so a Stop never discards work that has actually been done.
    /// </summary>
    protected void StopEInvoiceRefresh()
    {
        if (_einvCts is null || _einvCts.IsCancellationRequested)
        {
            return;
        }

        _einvCts.Cancel();
        EInvoiceProgress = "Stopping…";
    }

    /// <summary>Trimmed, deduplicated selection. The cap is applied to this list, never to the raw selection.</summary>
    private List<PoSbInvoiceListRow> DistinctSelectedRows() =>
        _selectedRows
            .Where(x => !string.IsNullOrWhiteSpace(x.DocNo))
            .GroupBy(x => x.DocNo.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    protected void DismissEInvoiceResults() => EInvoiceResultsVisible = false;

    protected static string EInvoiceResultOutcome(SaEInvoiceBatchItemResult item) =>
        item.Succeeded ? "OK" : item.Skipped ? "Skipped" : item.RecoveryRequired ? "Recover" : "Failed";

    protected static string EInvoiceResultClass(SaEInvoiceBatchItemResult item) =>
        item.Succeeded ? "iv-einv-ok" : item.Skipped ? "iv-einv-skip" : "iv-einv-fail";

    private void SetEInvoiceButtonsEnabled(bool enabled)
    {
        foreach (var button in Buttons)
        {
            var text = (button.Text ?? string.Empty).ToUpperInvariant();
            button.Enabled = text switch
            {
                "SUBMIT" => enabled && CanSubmitEInv,
                "E-STATUS" => enabled && CanSubmitEInv,
                "CANCEL" => enabled && CanCancelEInv,
                _ => button.Enabled
            };
        }
    }

    // ── Standard list behaviour ─────────────────────────────────────────────

    protected async Task OnSearchTextChanged(string text)
    {
        SearchText = text ?? string.Empty;
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
        _searchDebounce = new Timer(400) { AutoReset = false };
        var version = Interlocked.Increment(ref _searchVersion);
        _searchDebounce.Elapsed += async (_, _) =>
        {
            if (version != _searchVersion) return;
            await InvokeAsync(async () =>
            {
                SyncDataSourceFilters();
                await ReloadGridAsync();
            });
        };
        _searchDebounce.Start();
        await Task.CompletedTask;
    }

    protected void OpenFilterPopup()
    {
        DraftStatusKey = string.IsNullOrWhiteSpace(AppliedStatus) ? "all" : AppliedStatus;
        DraftDateFrom = AppliedDateFrom;
        DraftDateTo = AppliedDateTo;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedStatus = string.Equals(DraftStatusKey, "all", StringComparison.OrdinalIgnoreCase) ? null : DraftStatusKey;
        AppliedDateFrom = DraftDateFrom;
        AppliedDateTo = DraftDateTo;
        FilterPopupVisible = false;
        SyncDataSourceFilters();
        await ReloadGridAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftStatusKey = "all";
        DraftDateFrom = null;
        DraftDateTo = null;
        AppliedStatus = null;
        AppliedDateFrom = null;
        AppliedDateTo = null;
        FilterPopupVisible = false;
        SyncDataSourceFilters();
        await ReloadGridAsync();
    }

    protected async Task ConfirmActionAsync()
    {
        if (IsSubmitting)
        {
            return;
        }

        if (IsEInvoiceAction)
        {
            // The confirmation button is already disabled while the action cannot run
            // (Enabled="@CanConfirmAction"). The blocking-work scope lives in
            // ExecuteEInvoiceBatchAsync, exactly like the sales lists.
            if (!CanConfirmAction)
            {
                return;
            }

            await ExecuteEInvoiceBatchAsync();
            return;
        }

        if (_selectedRows.Count == 0)
        {
            ConfirmVisible = false;
            return;
        }

        using var blocking = BeginBlockingWork("Please wait. This action is still running.");
        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var keyed = _selectedRows
                .Select(x => new PoSbKeyedRequest { DocNo = x.DocNo, RowVersion = x.RowVersion })
                .ToList();

            // DELETE is the only remaining batch action; the e-Invoice batch actions never reach here.
            var result = await Invoices.DeleteAsync(keyed);

            ConfirmVisible = false;

            if (result.Succeeded)
            {
                StatusMessage = ConfirmAction == "DELETE" ? "Document(s) deleted." : "Done.";
                _selectedRows.Clear();
            }
            else
            {
                ErrorMessage = result.Message;
            }

            await ReloadGridAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError() => ErrorMessage = null;

    public void Dispose()
    {
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();

        // A refresh-all run must not outlive the component: Cancel makes the service stop at the next
        // chunk boundary instead of continuing to call MyInvois for an unrendered page.
        _einvCts?.Cancel();
        _einvCts?.Dispose();
    }

    protected static string StatusChipClass(string? status) =>
        string.Equals(status, PoSbStatuses.Posted, StringComparison.OrdinalIgnoreCase) ? "is-on" : "is-hold";

    private Task BeginDeleteAsync()
    {
        if (!CanDelete) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return Task.CompletedTask; }

        // The e-Invoice state is the only structural gate (the ERP NEW/POSTED dimension is retired), so
        // this pre-flight is the same lock check the service applies when it deletes.
        var locked = _selectedRows.Where(x => x.CanDelete == false).Select(x => x.DocNo).ToList();
        if (locked.Count > 0)
        {
            ErrorMessage = $"These documents are locked by their e-Invoice state: {string.Join(", ", locked)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "DELETE";
        ConfirmMessage = $"Permanently delete {_selectedRows.Count} selected document(s)?";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private async Task ReloadGridAsync()
    {
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private void SyncDataSourceFilters()
    {
        DataSource.UpdateFilters(new PoSbQuery
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            Status = AppliedStatus,
            DateFrom = AppliedDateFrom,
            DateTo = AppliedDateTo,
            SortDescending = true
        });
    }

    private async Task RefreshCompactPreviewAsync()
    {
        var query = DataSource.CurrentQuery;
        query.Skip = 0;
        query.Take = 50;
        var result = await Invoices.SearchAsync(query);
        if (result.Succeeded && result.List is not null)
        {
            CompactRows = result.List.Rows.ToList();
            TotalCount = result.List.TotalCount;
        }
        else
        {
            CompactRows = [];
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                ErrorMessage = result.Message;
            }
        }
    }

    private async Task<(IReadOnlyList<PoSbInvoiceListRow> Rows, int TotalCount)> SearchPageAsync(
        PoSbQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Invoices.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.List is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.Message ?? "Unable to load documents.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.List.TotalCount);
        return (result.List.Rows, result.List.TotalCount);
    }

    protected sealed record StatusFilterOption(string Key, string Name);
}

public sealed class PoSbInvoiceGridDataSource : GridCustomDataSource
{
    private readonly Func<PoSbQuery, CancellationToken, Task<(IReadOnlyList<PoSbInvoiceListRow> Rows, int TotalCount)>> _loader;
    private PoSbQuery _filters = new();

    public PoSbInvoiceGridDataSource(
        Func<PoSbQuery, CancellationToken, Task<(IReadOnlyList<PoSbInvoiceListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    /// <summary>
    /// The query the grid is currently showing. Exposed so a page-level action can never drift from what
    /// the operator sees.
    /// </summary>
    public PoSbQuery CurrentQuery => Clone(_filters);

    public void UpdateFilters(PoSbQuery query) => _filters = Clone(query);

    public override async Task<int> GetItemCountAsync(
        GridCustomDataSourceCountOptions options, CancellationToken cancellationToken)
    {
        var query = Clone(_filters);
        query.Skip = 0;
        query.Take = 1;
        var (_, total) = await _loader(query, cancellationToken);
        return total;
    }

    public override async Task<IList> GetItemsAsync(
        GridCustomDataSourceItemsOptions options, CancellationToken cancellationToken)
    {
        var query = Clone(_filters);
        query.Skip = Math.Max(0, options.StartIndex);
        query.Take = Math.Clamp(options.Count <= 0 ? 20 : options.Count, 1, PoSbLimits.MaxPageSize);
        if (options.SortInfo is { Count: > 0 })
        {
            var sort = options.SortInfo[0];
            query.SortField = sort.FieldName;
            query.SortDescending = sort.DescendingSortOrder;
        }

        var (rows, _) = await _loader(query, cancellationToken);
        return rows.ToList();
    }

    private static PoSbQuery Clone(PoSbQuery source) =>
        new()
        {
            SearchText = source.SearchText,
            Status = source.Status,
            VendorCode = source.VendorCode,
            Type = source.Type,
            DateFrom = source.DateFrom,
            DateTo = source.DateTo,
            SortField = source.SortField,
            SortDescending = source.SortDescending,
            Skip = source.Skip,
            Take = source.Take
        };
}
