using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Purchase.Inquiry;

public partial class PoApprovalInbox : PageBase, IDisposable
{
    [Inject] private IProcurementApprovalService Approval { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "all")] public string? AllQuery { get; set; }

    private bool _inboxInitialized;
    private bool _applyQueueFallback;
    private string? _lastAllQuery;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private bool _disposed;
    private List<PoApprovalInboxGridRow> _allRows = [];
    private ProcurementApprovalInboxRow? _selected;

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool PreviewVisible;
    protected bool ReassignVisible;
    protected bool ConfirmVisible;
    protected bool CanViewAll;
    protected bool CanReassign;
    protected string? StatusMessage;
    protected string? PopupError;
    protected string? DecisionReason;
    protected string? ReassignRemoveUserId;
    protected string? ReassignAddUserId;
    protected string ReassignReason = string.Empty;
    protected string SearchText = string.Empty;
    protected string AppliedQueueKey = "mine";
    protected string DraftQueueKey = "mine";
    protected string PendingDecision = string.Empty;
    protected ProcurementApprovalKpiSummary? Kpi;
    protected ProcurementApprovalPreview? Preview;
    protected IReadOnlyList<ProcurementApprovalLookupRow> Users { get; set; } = [];
    protected List<PoApprovalInboxGridRow> GridRows { get; set; } = [];

    protected string AssigneeSummary
    {
        get
        {
            var ids = Preview?.Instance?.CurrentAssignees ?? [];
            return ids.Count == 0
                ? "—"
                : string.Join(", ", ids.Select(UserDisplay));
        }
    }

    protected IReadOnlyList<ProcurementApprovalLookupRow> ReassignFromUsers
    {
        get
        {
            var ids = Preview?.Instance?.CurrentAssignees ?? [];
            return ids
                .Select(id => Users.FirstOrDefault(u => string.Equals(u.Code, id, StringComparison.OrdinalIgnoreCase))
                    ?? new ProcurementApprovalLookupRow { Code = id, Name = id })
                .ToList();
        }
    }

    protected IReadOnlyList<ProcurementApprovalLookupRow> ReassignToUsers =>
        Users
            .Where(u => !string.Equals(u.Code, ReassignRemoveUserId, StringComparison.OrdinalIgnoreCase))
            .ToList();

    protected bool IsPending =>
        string.Equals(Preview?.Instance?.Status, ProcurementApprovalStatuses.Pending, StringComparison.OrdinalIgnoreCase);

    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.Equals(AppliedQueueKey, "mine", StringComparison.OrdinalIgnoreCase);

    protected string ScopeBranchLabel =>
        string.IsNullOrWhiteSpace(CurrentUser.BranchCode) ? "—" : CurrentUser.BranchCode.Trim();

    protected string PendingBranchLabel
    {
        get
        {
            if (Kpi is null)
            {
                return "—";
            }

            var total = Kpi.PendingPr + Kpi.PendingPo;
            return total == 1 ? "1 pending in branch" : $"{total:N0} pending in branch";
        }
    }

    protected string EmptyQueueHint => AppliedQueueKey switch
    {
        "mine" => "Mine lists only documents waiting for your approval at the current level. If you submitted the PR, open FILTER → Submitted by me, or sign in as the approver (for example admin).",
        "submitted" => "Submitted by me lists your pending requests still in approval. None match this branch and search.",
        "all" => "All pending lists every open approval in your current branch. Confirm the branch chip matches the PR branch (for example HQ).",
        "completed" => "Completed lists finished approval cycles.",
        _ => "No approval requests match the current filters."
    };

    protected string QueueLabel => QueueOptions.FirstOrDefault(x => x.Key == AppliedQueueKey)?.Name ?? "Mine";
    protected string RowCountLabel => GridRows.Count == 1 ? "1 request" : $"{GridRows.Count:N0} requests";
    protected string InboxGridKey => $"{AppliedQueueKey}:{GridRows.Count}:{SearchText.Trim()}";

    protected string ConfirmTitle => PendingDecision switch
    {
        ProcurementApprovalDecisions.Reject => "Confirm reject",
        ProcurementApprovalDecisions.Return => "Confirm return",
        _ => "Confirm approve"
    };

    protected string ConfirmButtonText => PendingDecision switch
    {
        ProcurementApprovalDecisions.Reject => "Reject",
        ProcurementApprovalDecisions.Return => "Return",
        _ => "Approve"
    };

    protected ButtonRenderStyle ConfirmButtonStyle => PendingDecision switch
    {
        ProcurementApprovalDecisions.Reject => ButtonRenderStyle.Danger,
        ProcurementApprovalDecisions.Return => ButtonRenderStyle.Warning,
        _ => ButtonRenderStyle.Primary
    };

    protected string ConfirmMessage
    {
        get
        {
            var doc = Preview is null ? "this document" : $"{Preview.DocType} {Preview.DocNo}";
            return PendingDecision switch
            {
                ProcurementApprovalDecisions.Reject => $"Reject {doc}? The request will be closed and cannot be converted until a new cycle is submitted.",
                ProcurementApprovalDecisions.Return => $"Return {doc} for editing? The submitter can change the document and submit again.",
                _ => $"Approve {doc}? This records your vote for the current approval level."
            };
        }
    }

    protected List<QueueOption> QueueOptions { get; private set; } =
    [
        new("mine", "Mine"),
        new("submitted", "Submitted by me"),
        new("completed", "Completed")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Type", FieldName = nameof(PoApprovalInboxGridRow.DocType), VisibleIndex = 1, Size = GridColumnSize.Tiny },
        new() { Caption = "Document", FieldName = nameof(PoApprovalInboxGridRow.DocNo), VisibleIndex = 2, Size = GridColumnSize.DocumentNo },
        new() { Caption = "Rev", FieldName = nameof(PoApprovalInboxGridRow.DocRev), VisibleIndex = 3, Size = GridColumnSize.Tiny },
        new() { Caption = "Status", FieldName = nameof(PoApprovalInboxGridRow.Status), VisibleIndex = 4, Size = GridColumnSize.Status },
        new() { Caption = "Submitted by", FieldName = nameof(PoApprovalInboxGridRow.SubmittedBy), VisibleIndex = 5, Size = GridColumnSize.Name },
        new() { Caption = "Age (days)", FieldName = nameof(PoApprovalInboxGridRow.AgeDays), VisibleIndex = 6, Size = GridColumnSize.Tiny },
        new() { Caption = "Curr", FieldName = nameof(PoApprovalInboxGridRow.Currency), VisibleIndex = 7, Size = GridColumnSize.Code },
        new() { Caption = "Total", FieldName = nameof(PoApprovalInboxGridRow.TotalDisplay), VisibleIndex = 8, Size = GridColumnSize.Amount }
    ];

    protected List<ButtonInfo> ActionButtons { get; } =
    [
        new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "Open approval" }
    ];

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        if (!_inboxInitialized)
        {
            await EnsureInboxInitializedAsync(forceReload: false);
            return;
        }

        var query = AllQuery?.Trim();
        if (string.Equals(_lastAllQuery, query, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastAllQuery = query;
        var target = ResolveQueueKeyFromQuery();
        if (string.Equals(AppliedQueueKey, target, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AppliedQueueKey = target;
        DraftQueueKey = target;
        await LoadInboxAsync();
    }

    private async Task EnsureInboxInitializedAsync(bool forceReload)
    {
        if (_inboxInitialized && !forceReload)
        {
            return;
        }

        CanViewAll = await AccessRights.CanAsync(MenuCodes.PurchaseApprovalAll, PermissionCodes.Access);
        CanReassign = await AccessRights.CanAsync(MenuCodes.PurchaseApprovalSettings, PermissionCodes.Edit);
        QueueOptions = CanViewAll
            ?
            [
                new("mine", "Mine"),
                new("submitted", "Submitted by me"),
                new("all", "All pending"),
                new("completed", "Completed")
            ]
            :
            [
                new("mine", "Mine"),
                new("submitted", "Submitted by me"),
                new("completed", "Completed")
            ];
        _lastAllQuery = AllQuery?.Trim();
        AppliedQueueKey = ResolveQueueKeyFromQuery();
        DraftQueueKey = AppliedQueueKey;
        _applyQueueFallback = true;
        await LoadLookupsAsync();
        await LoadInboxAsync();
        var kpi = await Approval.GetKpiAsync(3);
        if (kpi.Succeeded)
        {
            Kpi = kpi.Kpi;
        }

        IsBootstrapping = false;
        _inboxInitialized = true;
    }

    private string ResolveQueueKeyFromQuery()
    {
        if (CanViewAll
            && (string.IsNullOrWhiteSpace(AllQuery)
                || string.Equals(AllQuery, "1", StringComparison.OrdinalIgnoreCase)))
        {
            return "all";
        }

        return "mine";
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    protected async Task OnGridButtonAsync(SelectedButtonInfo<PoApprovalInboxGridRow> info)
    {
        if (string.Equals(info.SelectedButton.Text, "REFRESH", StringComparison.OrdinalIgnoreCase))
        {
            await LoadInboxAsync();
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoApprovalInboxGridRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return Task.CompletedTask;
        }

        return OpenPreview(info.SelectedRow);
    }

    protected void OnSearchTextChanged(string text)
    {
        SearchText = text ?? string.Empty;
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
        _searchDebounce = new Timer(400) { AutoReset = false };
        var version = Interlocked.Increment(ref _searchVersion);
        _searchDebounce.Elapsed += async (_, _) =>
        {
            if (version != Volatile.Read(ref _searchVersion))
            {
                return;
            }

            await InvokeAsync(() =>
            {
                if (_disposed || version != Volatile.Read(ref _searchVersion))
                {
                    return;
                }

                ApplySearch();
            });
        };
        _searchDebounce.Start();
    }

    protected void ApplySearch() => GridRows = FilterRows(_allRows);

    protected void OpenFilterPopup()
    {
        DraftQueueKey = AppliedQueueKey;
        FilterPopupVisible = true;
    }

    protected async Task ClearFiltersAsync()
    {
        SearchText = string.Empty;
        DraftQueueKey = "mine";
        AppliedQueueKey = "mine";
        FilterPopupVisible = false;
        await LoadInboxAsync();
    }

    protected async Task ApplyFiltersAsync()
    {
        if (string.Equals(DraftQueueKey, "all", StringComparison.OrdinalIgnoreCase) && !CanViewAll)
        {
            ErrorMessage = "Not authorized to view all pending approvals.";
            return;
        }

        if (!QueueOptions.Any(x => string.Equals(x.Key, DraftQueueKey, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = "Unknown queue selection.";
            return;
        }

        AppliedQueueKey = DraftQueueKey;
        FilterPopupVisible = false;
        await LoadInboxAsync();
    }

    protected async Task OpenPreview(PoApprovalInboxGridRow row)
    {
        PopupError = null;
        DecisionReason = null;
        _selected = row.Source;
        var result = await Approval.GetPreviewAsync(row.DocType, row.DocNo, row.DocRev);
        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        Preview = result.Preview;
        ReassignRemoveUserId = Preview?.Instance?.CurrentAssignees.FirstOrDefault();
        ReassignAddUserId = null;
        ReassignReason = string.Empty;
        PreviewVisible = true;
    }

    protected void ClosePreview()
    {
        PreviewVisible = false;
        PopupError = null;
    }

    protected void OpenReassign()
    {
        PopupError = null;
        ReassignVisible = true;
    }

    protected void CloseReassign()
    {
        ReassignVisible = false;
        PopupError = null;
    }

    protected void BeginDecision(string decision)
    {
        PopupError = null;
        if (decision is ProcurementApprovalDecisions.Reject or ProcurementApprovalDecisions.Return
            && string.IsNullOrWhiteSpace(DecisionReason))
        {
            PopupError = "A reason is required.";
            return;
        }

        PendingDecision = decision;
        ConfirmVisible = true;
    }

    protected async Task ConfirmDecisionAsync()
    {
        if (Preview?.Instance is null || IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        PopupError = null;
        try
        {
            var result = await Approval.DecideAsync(new ProcurementApprovalDecisionRequest
            {
                InstanceId = Preview.Instance.Uid,
                RowVersion = Preview.Instance.RowVersion,
                Decision = PendingDecision,
                Reason = DecisionReason
            });
            if (!result.Succeeded)
            {
                PopupError = result.ErrorMessage;
                ConfirmVisible = false;
                return;
            }

            StatusMessage = "Decision saved.";
            ConfirmVisible = false;
            PreviewVisible = false;
            await LoadInboxAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task ReassignAsync()
    {
        if (Preview?.Instance is null || IsSubmitting)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ReassignReason))
        {
            PopupError = "A reason is required for reassignment.";
            return;
        }

        IsSubmitting = true;
        PopupError = null;
        try
        {
            var result = await Approval.ReassignAsync(new ProcurementApprovalReassignRequest
            {
                InstanceId = Preview.Instance.Uid,
                RowVersion = Preview.Instance.RowVersion,
                LevelSequence = Preview.Instance.CurrentLevel,
                RemoveUserId = ReassignRemoveUserId ?? string.Empty,
                AddUserId = ReassignAddUserId ?? string.Empty,
                Reason = ReassignReason
            });
            if (!result.Succeeded)
            {
                PopupError = result.ErrorMessage;
                return;
            }

            StatusMessage = "Approver reassigned.";
            ReassignVisible = false;
            var preview = await Approval.GetPreviewAsync(Preview.DocType, Preview.DocNo, Preview.DocRev);
            Preview = preview.Preview ?? Preview;
            ReassignRemoveUserId = Preview?.Instance?.CurrentAssignees.FirstOrDefault();
            ReassignAddUserId = null;
            ReassignReason = string.Empty;
            await LoadInboxAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void OpenDocument()
    {
        if (_selected is null)
        {
            return;
        }

        var url = string.Equals(_selected.DocType, ProcurementApprovalDocTypes.Po, StringComparison.OrdinalIgnoreCase)
            ? $"/purchase/orders/view/{Uri.EscapeDataString(_selected.DocNo)}/{_selected.DocRev}"
            : $"/purchase/requisitions/view/{Uri.EscapeDataString(_selected.DocNo)}";
        Navigation.NavigateTo(url);
    }

    protected static string StatusChipClass(string? status) =>
        string.Equals(status, ProcurementApprovalStatuses.Approved, StringComparison.OrdinalIgnoreCase) ? "is-on" :
        string.Equals(status, ProcurementApprovalStatuses.Rejected, StringComparison.OrdinalIgnoreCase) ? "is-off" :
        string.Equals(status, ProcurementApprovalStatuses.Pending, StringComparison.OrdinalIgnoreCase) ? "is-hold" : "is-hold";

    private async Task LoadLookupsAsync()
    {
        var result = await Approval.GetLookupsAsync();
        Users = result.Succeeded ? result.Users ?? [] : [];
    }

    private string UserDisplay(string userId)
    {
        var match = Users.FirstOrDefault(x => string.Equals(x.Code, userId, StringComparison.OrdinalIgnoreCase));
        return match?.DisplayText ?? userId;
    }

    private async Task LoadInboxAsync()
    {
        ErrorMessage = null;
        var all = string.Equals(AppliedQueueKey, "all", StringComparison.OrdinalIgnoreCase);
        var completed = string.Equals(AppliedQueueKey, "completed", StringComparison.OrdinalIgnoreCase);
        var submitted = string.Equals(AppliedQueueKey, "submitted", StringComparison.OrdinalIgnoreCase);
        var result = await Approval.GetInboxAsync(all, completed, submitted);
        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
            _allRows = [];
            GridRows = [];
            return;
        }

        _allRows = (result.Inbox ?? []).Select(ToGridRow).ToList();
        GridRows = FilterRows(_allRows);

        if (_applyQueueFallback
            && GridRows.Count == 0
            && string.Equals(AppliedQueueKey, "mine", StringComparison.OrdinalIgnoreCase))
        {
            _applyQueueFallback = false;
            await TryExpandQueueAsync();
            return;
        }

        _applyQueueFallback = false;
    }

    private async Task TryExpandQueueAsync()
    {
        var submitted = await Approval.GetInboxAsync(allPending: false, completed: false, submittedByMe: true);
        if (submitted.Succeeded && submitted.Inbox is { Count: > 0 })
        {
            AppliedQueueKey = "submitted";
            DraftQueueKey = "submitted";
            _allRows = submitted.Inbox.Select(ToGridRow).ToList();
            GridRows = FilterRows(_allRows);
            return;
        }

        if (!CanViewAll)
        {
            return;
        }

        AppliedQueueKey = "all";
        DraftQueueKey = "all";
        var all = await Approval.GetInboxAsync(allPending: true, completed: false);
        if (!all.Succeeded)
        {
            ErrorMessage = all.ErrorMessage;
            _allRows = [];
            GridRows = [];
            return;
        }

        _allRows = (all.Inbox ?? []).Select(ToGridRow).ToList();
        GridRows = FilterRows(_allRows);
    }

    private List<PoApprovalInboxGridRow> FilterRows(IReadOnlyList<PoApprovalInboxGridRow> source)
    {
        var term = SearchText.Trim();
        if (term.Length == 0)
        {
            return source.ToList();
        }

        return source.Where(row =>
                Contains(row.DocNo, term)
                || Contains(row.DocType, term)
                || Contains(row.Status, term)
                || Contains(row.SubmittedBy, term)
                || Contains(row.TotalDisplay, term))
            .ToList();
    }

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static PoApprovalInboxGridRow ToGridRow(ProcurementApprovalInboxRow row) =>
        new()
        {
            InstanceId = row.InstanceId,
            DocType = row.DocType,
            DocNo = row.DocNo,
            DocRev = row.DocRev,
            Status = row.Status,
            SubmittedBy = row.SubmittedBy,
            AgeDays = row.AgeDays,
            Currency = row.Currency,
            TotalDisplay = row.AmountHidden ? "Hidden" : row.Total?.ToString("n2") ?? "—",
            Source = row
        };

    public void Dispose()
    {
        _disposed = true;
        _searchDebounce?.Dispose();
    }

    protected sealed record QueueOption(string Key, string Name);

    public sealed class PoApprovalInboxGridRow
    {
        public long InstanceId { get; init; }
        public string DocType { get; init; } = string.Empty;
        public string DocNo { get; init; } = string.Empty;
        public short DocRev { get; init; }
        public string Status { get; init; } = string.Empty;
        public string? SubmittedBy { get; init; }
        public int AgeDays { get; init; }
        public string Currency { get; init; } = string.Empty;
        public string TotalDisplay { get; init; } = string.Empty;
        public ProcurementApprovalInboxRow Source { get; init; } = default!;
    }
}
