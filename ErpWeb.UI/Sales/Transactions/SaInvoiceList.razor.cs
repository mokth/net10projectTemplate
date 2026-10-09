using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Security;
using ErpWeb.Model.Repositories.Sales;
using ErpWeb.UI.Components.Common;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Sales.Transactions;

public partial class SaInvoiceList : PageBase, IDisposable
{
    [Inject] private ISaInvoiceService Invoices { get; set; } = default!;
    [Inject] private ISaEInvoiceService EInvoices { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private readonly List<SaInvoiceListRow> _selectedRows = [];

    /// <summary>Token for the running refresh-all run, so Stop can interrupt it between chunks.</summary>
    private CancellationTokenSource? _einvCts;

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected List<SaInvoiceListRow> CompactRows { get; set; } = [];

    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDelete;
    protected bool CanPost;
    protected bool CanRollback;
    protected bool CanSubmitEInv;
    protected bool CanCancelEInv;

    /// <summary>
    /// ACCESS on the read-only LHDN inquiry menu, which is also what gates
    /// <c>ISaEInvoiceService.GetDocumentDetailAsync</c>. Checked here so the row action is never
    /// offered to an operator the service would refuse.
    /// </summary>
    protected bool CanAccessEInvoiceTin;

    /// <summary>
    /// True while a batch e-Invoice action is running. The three e-Invoice toolbar buttons and the
    /// confirm action are disabled so a double click cannot fire two batches.
    /// </summary>
    protected bool IsEInvoiceBusy;

    /// <summary>Shared reason for a batch cancellation; required and limited to 300 characters by MyInvois.</summary>
    protected string EInvoiceCancelReason = string.Empty;

    /// <summary>
    /// Progress of the running refresh-all run, e.g. "Refreshing e-Invoice status… 12 of 43 completed".
    /// Null when no run is active, which is also what hides the progress strip.
    /// </summary>
    protected string? EInvoiceProgress;

    protected bool EInvoiceResultsVisible;
    protected string EInvoiceResultsTitle = string.Empty;
    protected List<SaEInvoiceBatchItemResult> EInvoiceResults { get; set; } = [];

    protected string? AppliedStatus;
    protected DateTime? AppliedDateFrom;
    protected DateTime? AppliedDateTo;
    protected string DraftStatusKey = "all";
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;

    protected string ConfirmMessage { get; set; } = string.Empty;
    protected string ConfirmAction { get; set; } = "DELETE";

    private const string EInvSubmitAction = "EINV_SUBMIT";
    private const string EInvStatusAction = "EINV_STATUS";
    private const string EInvCancelAction = "EINV_CANCEL";

    private static bool IsEInvoiceAction(string action) =>
        action is EInvSubmitAction or EInvStatusAction or EInvCancelAction;

    protected bool IsCancelAction => ConfirmAction == EInvCancelAction;
    protected bool CanConfirmAction =>
        !IsSubmitting && (!IsCancelAction || !string.IsNullOrWhiteSpace(EInvoiceCancelReason));

    protected string ConfirmButtonText => ConfirmAction switch
    {
        "POST" => "Post",
        "ROLLBACK" => "Rollback",
        EInvSubmitAction => "Submit",
        EInvStatusAction => "Refresh",
        EInvCancelAction => "Cancel e-Invoice",
        _ => "Delete"
    };
    protected ButtonRenderStyle ConfirmButtonStyle => ConfirmAction switch
    {
        "POST" => ButtonRenderStyle.Primary,
        "ROLLBACK" => ButtonRenderStyle.Warning,
        EInvSubmitAction => ButtonRenderStyle.Primary,
        EInvStatusAction => ButtonRenderStyle.Primary,
        EInvCancelAction => ButtonRenderStyle.Danger,
        _ => ButtonRenderStyle.Danger
    };

    protected SaInvoiceGridDataSource DataSource { get; private set; } = default!;
    protected string TotalCountLabel => TotalCount == 1 ? "1 invoice" : $"{TotalCount:N0} invoices";
    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedStatus)
        || AppliedDateFrom is not null
        || AppliedDateTo is not null;

    protected IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new("all", "All"),
        new(SaInvoiceStatuses.New, "NEW"),
        new(SaInvoiceStatuses.Posted, "POSTED")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Invoice", FieldName = nameof(SaInvoiceListRow.InvNo), SortIndex = 0, VisibleIndex = 1, Size = GridColumnSize.DocumentNo },
        new() { Caption = "Date", FieldName = nameof(SaInvoiceListRow.InvDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", VisibleIndex = 2, Size = GridColumnSize.Date },
        new() { Caption = "Status", FieldName = nameof(SaInvoiceListRow.Status), VisibleIndex = 3, Size = GridColumnSize.Status },
        new() { Caption = "E-Inv", FieldName = nameof(SaInvoiceListRow.IrbmStatus), VisibleIndex = 4, Size = GridColumnSize.Status },
        new() { Caption = "Customer", FieldName = nameof(SaInvoiceListRow.CustCode), VisibleIndex = 5, Size = GridColumnSize.Code },
        new() { Caption = "Name", FieldName = nameof(SaInvoiceListRow.CustName), VisibleIndex = 6, Size = GridColumnSize.Name },
        new() { Caption = "Total (incl. tax)", FieldName = nameof(SaInvoiceListRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", VisibleIndex = 7, Size = GridColumnSize.Amount },
        new() { Caption="E-UUID"   , FieldName=nameof(SaInvoiceListRow.IrbmUuid)     ,DataType="link",VisibleIndex=8, Size = GridColumnSize.Reference},
        // VisibleIndex 9, not 8: two columns may not share an index, and the UUID link needs a
        // deterministic position for its click target.
        new()  { Caption="E-Status"     , FieldName=nameof(SaInvoiceListRow.IrbmStatus)        ,DataType="string",VisibleIndex=9, Size = GridColumnSize.Status },

        new() { Caption = "Lines", FieldName = nameof(SaInvoiceListRow.LineCount), Width = "80px", VisibleIndex = 10, Size = GridColumnSize.Tiny },
        ..AuditColumns.For(startVisibleIndex: 11)
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new SaInvoiceGridDataSource(SearchPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.SalesInvoice, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.SalesInvoice, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.SalesInvoice, PermissionCodes.Delete);
        CanPost = await AccessRights.CanAsync(MenuCodes.SalesInvoice, PermissionCodes.Post);
        CanRollback = await AccessRights.CanAsync(MenuCodes.SalesInvoice, PermissionCodes.Rollback);
        CanSubmitEInv = await AccessRights.CanAsync(MenuCodes.SalesInvoice, PermissionCodes.Submit);
        CanCancelEInv = await AccessRights.CanAsync(MenuCodes.SalesInvoice, PermissionCodes.Cancel);
        CanAccessEInvoiceTin = await AccessRights.CanAccessAsync(MenuCodes.SalesEInvoiceTin);
        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "POST", IConClass = "fas fa-check", Style = "success", Enabled = CanPost },
            new() { Text = "ROLLBACK", IConClass = "fas fa-rotate-left", Style = "warning", Enabled = CanRollback },
            new() { Text = "DELETE", IConClass = "far fa-trash-alt", Style = "danger", Enabled = CanDelete },
            new() { Text = "SUBMIT", IConClass = "fas fa-paper-plane", Style = "primary", Enabled = CanSubmitEInv, ToolTip = "Submit the selected invoices to MyInvois" },
            new() { Text = "E-STATUS", IConClass = "fas fa-arrows-rotate", Style = "primary", Enabled = CanSubmitEInv, ToolTip = "Refresh the MyInvois status of the selected invoices; with nothing selected, every submitted invoice in this branch" },
            new() { Text = "CANCEL", IConClass = "fas fa-ban", Style = "danger", Enabled = CanCancelEInv, ToolTip = "Cancel the selected e-Invoices at MyInvois" }
        ];
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View invoice" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit invoice", Enabled = CanEdit },
            new() { Text = "LHDN", IConClass = "fa-solid fa-triangle-exclamation", Style = "primary", ToolTip = "View the LHDN validation detail (INVALID documents only)", Enabled = CanAccessEInvoiceTin }
        ];
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected void OnSelectionsEvent(List<SaInvoiceListRow> list)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(list);
    }

    protected async Task OnButtonClick(SelectedButtonInfo<SaInvoiceListRow> info)
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
                Navigation.NavigateTo("/sales/invoices/new");
                break;
            case "DELETE": await BeginDeleteAsync(); break;
            case "POST": await BeginPostAsync(); break;
            case "ROLLBACK": await BeginRollbackAsync(); break;
            case "SUBMIT": await BeginEInvoiceAsync(EInvSubmitAction); break;
            case "E-STATUS": await BeginEInvoiceAsync(EInvStatusAction); break;
            case "CANCEL": await BeginEInvoiceAsync(EInvCancelAction); break;
            case "REFRESH": await ReloadGridAsync(); break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<SaInvoiceListRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (mode == "VIEW")
        {
            NavigateView(info.SelectedRow.InvNo);
        }
        else if (mode == "EDIT")
        {
            if (!CanEdit)
            {
                StatusMessage = "Access Denied!!";
                return Task.CompletedTask;
            }

            if (!string.Equals(info.SelectedRow.Status, SaInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "Only NEW invoices can be edited.";
                return Task.CompletedTask;
            }

            Navigation.NavigateTo($"/sales/invoices/edit/{info.SelectedRow.InvNo}");
        }
        else if (mode == "LHDN")
        {
            // The gate lives in EInvoiceDetailLink: INVALID-only, because MyInvois directs that Get
            // Document Details be used for invalid-document error details only.
            var (url, error) = EInvoiceDetailLink.Resolve(
                info.SelectedRow.IrbmStatus, info.SelectedRow.IrbmUuid, CanAccessEInvoiceTin);
            if (url is null)
            {
                ErrorMessage = error;
                return Task.CompletedTask;
            }

            Navigation.NavigateTo(url);
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(string invNo) =>
        Navigation.NavigateTo($"/sales/invoices/view/{invNo}");

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
        if (IsSubmitting || _selectedRows.Count == 0)
        {
            ConfirmVisible = false;
            return;
        }

        // The e-Invoice actions report per-row outcomes, so they run on their own path (and own popup)
        // rather than the single summary toast used by POST / ROLLBACK / DELETE.
        if (IsEInvoiceAction(ConfirmAction))
        {
            await ExecuteEInvoiceBatchAsync();
            return;
        }

        using var blocking = BeginBlockingWork("Please wait. This action is still running.");
        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var nos = _selectedRows.Select(x => x.InvNo).ToList();
            SaInvoiceOperationResult result = ConfirmAction switch
            {
                "POST" => await Invoices.PostAsync(nos),
                "ROLLBACK" => await Invoices.RollbackAsync(nos),
                _ => await Invoices.DeleteAsync(_selectedRows
                    .Select(x => new SaInvoiceKeyedRequest { InvNo = x.InvNo, RowVersion = x.RowVersion })
                    .ToList())
            };

            if (result.Posting.Count > 0)
            {
                var lines = result.Posting.Select(x => $"{x.InvNo}: {x.Outcome}");
                var summary = string.Join(" · ", lines);
                if (result.Succeeded)
                {
                    StatusMessage = summary;
                    ConfirmVisible = false;
                    _selectedRows.Clear();
                    await ReloadGridAsync();
                }
                else
                {
                    ErrorMessage = summary;
                    ConfirmVisible = false;
                    await ReloadGridAsync();
                }
            }
            else if (result.Succeeded)
            {
                StatusMessage = ConfirmAction == "DELETE" ? "Invoice(s) deleted." : "Done.";
                ConfirmVisible = false;
                _selectedRows.Clear();
                await ReloadGridAsync();
            }
            else
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to complete the action.";
                ConfirmVisible = false;
            }
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

    /// <summary>
    /// Click on the grid's E-UUID cell. The shared rules live in
    /// <see cref="EInvoicePortalLinkOpener"/>, so the CN/DN and self-billed list screens reuse them
    /// verbatim instead of repeating the logic.
    ///
    /// <para>
    /// An <b>INVALID</b> document opens the LHDN detail page
    /// (<see cref="EInvoicePortalLinkOpener.HandleUuidClickAsync"/>) instead of the portal: MyInvois only
    /// returns the failure reason through Get Document Details, and an INVALID document has no long id to
    /// build a portal link from. Every other status opens the LHDN portal and, behind it, repairs the
    /// e-Invoice submission history: the document is re-read from MyInvois and its
    /// <c>dbo.EInvDocSubmission</c> row is created when it was never written, or refreshed when LHDN has
    /// moved on.
    /// </para>
    /// </summary>
    protected async Task onSelectColHandle(SelectedColumnInfo info)
    {
        if (!EInvoicePortalLinkOpener.IsUuidColumn(info))
        {
            return;
        }

        // The grid sets Context to the row item, so the status is available without another round trip.
        var row = info.Context as SaInvoiceListRow;

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

    protected static string StatusChipClass(string? status) =>
        string.Equals(status, SaInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase) ? "is-on" : "is-hold";

    private Task BeginDeleteAsync()
    {
        if (!CanDelete) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return Task.CompletedTask; }
        var notNew = _selectedRows.Where(x => !string.Equals(x.Status, SaInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase)).Select(x => x.InvNo).ToList();
        if (notNew.Count > 0)
        {
            ErrorMessage = $"Only NEW invoices can be deleted. Non-NEW: {string.Join(", ", notNew)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "DELETE";
        ConfirmMessage = $"Permanently delete {_selectedRows.Count} selected invoice(s)?";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private Task BeginPostAsync()
    {
        if (!CanPost) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return Task.CompletedTask; }
        if (_selectedRows.Count > SaInvoiceLimits.MaxPostSelection)
        {
            ErrorMessage = $"Select at most {SaInvoiceLimits.MaxPostSelection} invoices to post.";
            return Task.CompletedTask;
        }

        var notNew = _selectedRows.Where(x => !string.Equals(x.Status, SaInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase)).Select(x => x.InvNo).ToList();
        if (notNew.Count > 0)
        {
            ErrorMessage = $"Only NEW invoices can be posted. Non-NEW: {string.Join(", ", notNew)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "POST";
        ConfirmMessage = $"Post {_selectedRows.Count} selected invoice(s)? Stock will be issued from the shipment piles.";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private Task BeginRollbackAsync()
    {
        if (!CanRollback) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return Task.CompletedTask; }
        if (_selectedRows.Count > SaInvoiceLimits.MaxPostSelection)
        {
            ErrorMessage = $"Select at most {SaInvoiceLimits.MaxPostSelection} invoices to roll back.";
            return Task.CompletedTask;
        }

        var notPosted = _selectedRows.Where(x => !string.Equals(x.Status, SaInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase)).Select(x => x.InvNo).ToList();
        if (notPosted.Count > 0)
        {
            ErrorMessage = $"Only POSTED invoices can be rolled back. Non-POSTED: {string.Join(", ", notPosted)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "ROLLBACK";
        ConfirmMessage = $"Roll back {_selectedRows.Count} selected invoice(s)? Stock will be restored.";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

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
            // Nothing selected + E-STATUS is the "refresh everything I can see" mode (the legacy
            // GetEStatus behaviour). SUBMIT and CANCEL stay selection-only: they are write actions and
            // must never run over the whole list.
            if (action == EInvStatusAction)
            {
                await BeginRefreshAllAsync();
                return;
            }

            StatusMessage = "No Record Selected!";
            return;
        }

        if (rows.Count > SaInvoiceLimits.MaxEInvoiceBatchSelection)
        {
            ErrorMessage = $"Select at most {SaInvoiceLimits.MaxEInvoiceBatchSelection} invoices per e-Invoice action.";
            return;
        }

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

        var eligible = new List<string>();
        var skipped = new List<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            var reason = EInvoiceIneligibleReason(rows[i], i < preview.Count ? preview[i] : null, action);
            if (reason is null)
            {
                eligible.Add(rows[i].InvNo);
            }
            else
            {
                skipped.Add($"{rows[i].InvNo} ({reason})");
            }
        }

        if (eligible.Count == 0)
        {
            ErrorMessage = "None of the selected invoices can be used for this action. " + string.Join("; ", skipped);
            return;
        }

        ConfirmAction = action;
        ConfirmMessage = BuildEInvoiceConfirmMessage(action, eligible.Count, rows.Count, skipped);
        EInvoiceCancelReason = string.Empty;
        ConfirmVisible = true;
    }

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
    /// The no-selection E-STATUS mode: refresh every <c>SUBMITTED</c> invoice the grid is currently
    /// showing. The scope is the grid's OWN query (<see cref="DataSource"/>.<c>CurrentQuery</c>), so
    /// what gets refreshed is exactly what the operator can see; the service decides the run cap before
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

            var result = await EInvoices.RefreshSubmittedAsync(DataSource.CurrentQuery, progress, _einvCts.Token);

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

    /// <summary>
    /// Disables (or restores) the three e-Invoice toolbar buttons while a batch runs, so the toolbar
    /// matches the <see cref="IsEInvoiceBusy"/> guard that already rejects re-entry.
    /// </summary>
    private void SetEInvoiceButtonsEnabled(bool enabled)
    {
        foreach (var button in Buttons)
        {
            switch (button.Text)
            {
                case "SUBMIT":
                case "E-STATUS":
                    button.Enabled = enabled && CanSubmitEInv;
                    break;
                case "CANCEL":
                    button.Enabled = enabled && CanCancelEInv;
                    break;
            }
        }
    }

    /// <summary>Trimmed, deduplicated selection. The cap is applied to this list, never to the raw selection.</summary>
    private List<SaInvoiceListRow> DistinctSelectedRows() =>
        _selectedRows
            .Where(x => !string.IsNullOrWhiteSpace(x.InvNo))
            .GroupBy(x => x.InvNo.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    
    private static SaEInvoiceDocumentKey ToEInvoiceKey(SaInvoiceListRow row) =>
        new() { DocumentType = EInvoiceDocumentTypes.Invoice, DocumentNo = row.InvNo.Trim() };

    /// <summary>
    /// Why this row would be skipped, or null when it looks eligible. Mirrors <see cref="SaEInvoiceStatusView"/>
    /// and the POSTED rule; the service is still the authority.
    /// </summary>
    private static string? EInvoiceIneligibleReason(SaInvoiceListRow row, SaEInvoiceStatusView? view, string action)
    {
        switch (action)
        {
            case EInvSubmitAction:
                if (!string.Equals(row.Status, SaInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase))
                {
                    return "not POSTED";
                }

                if (view is null)
                {
                    return "no e-Invoice record";
                }

                return view.CanRecover ? "run Recover first" : view.CanSubmit ? null : "e-Invoice " + view.Status;

            case EInvStatusAction:
                if (view is null)
                {
                    return "no e-Invoice record";
                }

                return view.CanRefresh ? null : "e-Invoice " + view.Status;

            case EInvCancelAction:
                if (view is null)
                {
                    return "no e-Invoice record";
                }

                return view.CanCancel ? null : "e-Invoice " + view.Status;

            default:
                return null;
        }
    }

    private static string BuildEInvoiceConfirmMessage(string action, int eligibleCount, int total, List<string> skipped)
    {
        var message = action switch
        {
            EInvStatusAction => $"Refresh the MyInvois status of {eligibleCount} selected invoice(s)?",
            EInvSubmitAction => $"Submit {eligibleCount} selected invoice(s) to MyInvois?",
            _ => $"Cancel {eligibleCount} selected e-Invoice(s) at MyInvois?"
        };

        if (skipped.Count > 0)
        {
            message += $" {skipped.Count} of {total} will be skipped: {string.Join("; ", skipped)}.";
        }

        return message;
    }

    protected void DismissEInvoiceResults() => EInvoiceResultsVisible = false;

    protected static string EInvoiceResultOutcome(SaEInvoiceBatchItemResult item) =>
        item.Succeeded ? "OK"
        : item.Skipped ? "Skipped"
        : item.RecoveryRequired ? "Recover"
        : "Failed";

    protected static string EInvoiceResultClass(SaEInvoiceBatchItemResult item) =>
        item.Succeeded ? "iv-einv-ok"
        : item.Skipped ? "iv-einv-skip"
        : "iv-einv-fail";

    private async Task ReloadGridAsync()
    {
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private void SyncDataSourceFilters()
    {
        DataSource.UpdateFilters(new SaInvoiceListQuery
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
        if (result.Succeeded && result.ListPage is not null)
        {
            CompactRows = result.ListPage.Rows.ToList();
            TotalCount = result.ListPage.TotalCount;
        }
        else
        {
            CompactRows = [];
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                ErrorMessage = result.ErrorMessage;
            }
        }
    }

    private async Task<(IReadOnlyList<SaInvoiceListRow> Rows, int TotalCount)> SearchPageAsync(
        SaInvoiceListQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Invoices.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.ListPage is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load invoices.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.ListPage.TotalCount);
        return (result.ListPage.Rows, result.ListPage.TotalCount);
    }

    protected sealed record StatusFilterOption(string Key, string Name);
}

public sealed class SaInvoiceGridDataSource : GridCustomDataSource
{
    private readonly Func<SaInvoiceListQuery, CancellationToken, Task<(IReadOnlyList<SaInvoiceListRow> Rows, int TotalCount)>> _loader;
    private SaInvoiceListQuery _filters = new();

    public SaInvoiceGridDataSource(
        Func<SaInvoiceListQuery, CancellationToken, Task<(IReadOnlyList<SaInvoiceListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public SaInvoiceListQuery CurrentQuery => Clone(_filters);
    public void UpdateFilters(SaInvoiceListQuery query) => _filters = Clone(query);

    public override async Task<int> GetItemCountAsync(GridCustomDataSourceCountOptions options, CancellationToken cancellationToken)
    {
        var query = Clone(_filters);
        query.Skip = 0;
        query.Take = 1;
        var (_, total) = await _loader(query, cancellationToken);
        return total;
    }

    public override async Task<IList> GetItemsAsync(GridCustomDataSourceItemsOptions options, CancellationToken cancellationToken)
    {
        var query = Clone(_filters);
        query.Skip = Math.Max(0, options.StartIndex);
        query.Take = Math.Clamp(options.Count <= 0 ? 20 : options.Count, 1, 100);
        if (options.SortInfo is { Count: > 0 })
        {
            var sort = options.SortInfo[0];
            query.SortField = sort.FieldName;
            query.SortDescending = sort.DescendingSortOrder;
        }

        var (rows, _) = await _loader(query, cancellationToken);
        return rows.ToList();
    }

    private static SaInvoiceListQuery Clone(SaInvoiceListQuery source) =>
        new()
        {
            SearchText = source.SearchText,
            Status = source.Status,
            DateFrom = source.DateFrom,
            DateTo = source.DateTo,
            SortField = source.SortField,
            SortDescending = source.SortDescending,
            Skip = source.Skip,
            Take = source.Take
        };
}
