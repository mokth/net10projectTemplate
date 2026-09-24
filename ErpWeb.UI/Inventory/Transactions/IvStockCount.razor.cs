using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Transactions;

public partial class IvStockCount : PageBase
{
    [Inject] private IIvStockCountService StockCount { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    [Parameter] public string Mode { get; set; } = "new";
    [Parameter] public string? CountNo { get; set; }

    private int _id;
    private string? _countNo;
    private string? _rowVersion;
    private string _loadedKey = string.Empty;

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected string? StatusMessage;

    protected bool CanAdd;
    protected bool CanEditPermission;
    protected bool CanPostPermission;
    protected bool CanRollbackPermission;

    protected IvStockCountHeaderVm Header { get; set; } = new();
    protected List<IvStockCountLineVm> Lines { get; set; } = [];

    protected List<ScopeOption> Warehouses { get; private set; } = [];
    protected List<ScopeOption> Locations { get; private set; } = [];
    protected List<ScopeOption> Classes { get; private set; } = [];
    protected List<ScopeOption> SubClasses { get; private set; } = [];
    protected List<ScopeOption> Types { get; private set; } = [];

    protected bool GenerateConfirmVisible;
    protected string GenerateConfirmMessage = string.Empty;

    protected bool EnterByItemVisible;
    protected string EnterByItemCode = string.Empty;
    protected decimal? EnterByItemQty;
    protected List<EnterByItemSlice> EnterByItemSlices = [];
    protected string? EnterByItemSliceKey;
    protected string EnterByItemHint = "Type an item code and press Find.";

    protected bool PreviewVisible;
    protected IvStockCountPostPreview? Preview;
    protected bool StaleAcknowledged;

    protected bool RollbackVisible;
    protected string RollbackReason = string.Empty;

    private string NormalizedMode => (Mode ?? "new").Trim().ToLowerInvariant();

    protected bool IsViewMode => NormalizedMode == "view";
    protected bool IsCountMode => NormalizedMode == "count";
    protected bool CanConfigureScope => NormalizedMode is "new" or "edit";
    // Count entry is gated by the SHEET's status, not the route: a POSTED/CANCELLED sheet must not offer
    // an editor whose Save the service will always refuse.
    protected bool CanCountLines => IsCountMode && Header.CanCount;
    protected bool CanSaveHeader => CanConfigureScope;
    protected bool CanEditDocument => NormalizedMode == "edit";
    protected bool CanPostDocument => IsViewMode && CanPostPermission
        && Header.Status is IvStockCountStatuses.Counted or IvStockCountStatuses.RolledBack;
    protected bool CanRollbackDocument => IsViewMode && CanRollbackPermission
        && Header.Status == IvStockCountStatuses.Posted;
    protected bool CanRecover => IsViewMode && CanEditPermission
        && Header.Status == IvStockCountStatuses.Posted
        && Header.PostedBatchNo is not null;
    protected bool ShowPostButton => IsViewMode;
    protected bool ShowRollbackButton => IsViewMode;

    protected string PageHeading => NormalizedMode switch
    {
        "new" => "New Stock Count",
        "edit" => "Edit Stock Count",
        "count" => "Count Entry",
        _ => "Stock Count"
    };

    protected string ModeChip => NormalizedMode switch
    {
        "new" => "New",
        "edit" => "Edit",
        "count" => "Counting",
        _ => "View"
    };

    protected string StatusDisplay => string.IsNullOrWhiteSpace(Header.Status) ? "DRAFT" : Header.Status;

    protected string BatchDisplay => Header.PostedBatchNo is int batchNo ? batchNo.ToString() : "—";

    protected int StaleLineCount => Lines.Count(l => l.IsStale);

    protected decimal NetDifference => Lines
        .Where(l => l.Variance is not null)
        .Sum(l => l.Variance!.Value);

    protected string ScopeWarehouseText => string.IsNullOrWhiteSpace(Header.WHCode) ? "All warehouses" : Header.WHCode!;
    protected string ScopeClassText => string.IsNullOrWhiteSpace(Header.IClassCode) ? "All classes" : Header.IClassCode!;
    protected string ScopeStatusText => string.IsNullOrWhiteSpace(Header.IStatus) ? "All except SCRAPS" : $"Status: {Header.IStatus}";

    protected bool CanConfirmPost =>
        Preview is not null
        && Preview.DateError is null
        && Preview.ErrorLines == 0
        && (Preview.IncreaseLines + Preview.DecreaseLines + Preview.ZeroVarianceLines) > 0
        && (!Preview.RequiresStaleConfirmation || StaleAcknowledged);

    protected override async Task OnPageInitializedAsync()
    {
        CanAdd = await AccessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Add);
        CanEditPermission = await AccessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Edit);
        CanPostPermission = await AccessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Post);
        CanRollbackPermission = await AccessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Rollback);

        await LoadScopeLookupsAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        var key = $"{NormalizedMode}|{CountNo}";
        if (string.Equals(key, _loadedKey, StringComparison.Ordinal))
        {
            return;
        }

        _loadedKey = key;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            if (NormalizedMode == "new" || string.IsNullOrWhiteSpace(CountNo))
            {
                Header = new IvStockCountHeaderVm { CountDate = DateTime.Today, IncludeZeroQty = true };
                Lines = [];
                _id = 0;
                _countNo = null;
                _rowVersion = null;
            }
            else
            {
                await LoadDocumentAsync(CountNo!);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadDocumentAsync(string countNo)
    {
        var result = await StockCount.GetAsync(countNo);
        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? $"Stock count {countNo} was not found.";
            return;
        }

        var doc = result.Document;
        _id = doc.Id;
        _countNo = doc.CountNo;
        _rowVersion = doc.RowVersion;
        Header = new IvStockCountHeaderVm
        {
            CountDate = doc.CountDate,
            Status = doc.Status,
            WHCode = doc.WHCode,
            LocCode = doc.LocCode,
            IClassCode = doc.IClassCode,
            ISubClassCode = doc.ISubClassCode,
            IType = doc.IType,
            IStatus = string.Join(",", doc.Statuses),
            ICodeList = string.Join(",", doc.ICodes),
            IncludeZeroQty = doc.IncludeZeroQty,
            CountedBy = doc.CountedBy,
            Remark = doc.Remark,
            PostedBatchNo = doc.PostedBatchNo,
            RollbackReason = doc.RollbackReason
        };
        Lines = doc.Lines.Select(IvStockCountLineVm.FromDto).ToList();
        await RefreshLocationsAsync();

        // Count entry is open until POSTED; explain the two states that refuse it rather than showing an
        // editor whose Save the server will always reject.
        if (NormalizedMode == "count" && !Header.CanCount)
        {
            ErrorMessage = Header.Status == IvStockCountStatuses.Posted
                ? $"Stock count {doc.CountNo} is POSTED, so its counted quantities are frozen. Roll it back to re-count."
                : $"Stock count {doc.CountNo} was cancelled and is kept as evidence. Create a new count.";
        }
    }

    // ── Scope lookups ────────────────────────────────────────────────────────────────────────────

    protected async Task LoadScopeLookupsAsync()
    {
        Warehouses = await LoadOptionsAsync(Lookups.ListActiveWarehousesAsync);
        Classes = await LoadOptionsAsync(Lookups.ListActiveClassesAsync);
        Types = await LoadOptionsAsync(Lookups.ListActiveTypesAsync);
    }

    private async Task RefreshLocationsAsync()
    {
        SubClasses = string.IsNullOrWhiteSpace(Header.IClassCode)
            ? []
            : await LoadOptionsAsync(ct => Lookups.ListActiveSubClassesAsync(Header.IClassCode!, ct));

        Locations = string.IsNullOrWhiteSpace(Header.WHCode)
            ? []
            : await LoadOptionsAsync(ct => Lookups.ListActiveLocationsAsync(Header.WHCode!, ct));
    }

    /// <summary>Re-reads the dependent lists after the warehouse or class scope changed.</summary>
    protected async Task OnScopeDependencyChangedAsync()
    {
        // A warehouse/class change invalidates a location/sub-class that belonged to the old parent.
        Header.LocCode = null;
        Header.ISubClassCode = null;
        await RefreshLocationsAsync();
    }

    private async Task<List<ScopeOption>> LoadOptionsAsync(
        Func<CancellationToken, Task<IvInventoryLookupResult>> loader)
    {
        var result = await loader(CancellationToken.None);
        if (!result.Succeeded)
        {
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                ErrorMessage = result.ErrorMessage;
            }

            return [];
        }

        return result.Rows
            .Select(r => new ScopeOption(r.Code, string.IsNullOrWhiteSpace(r.Desc) ? r.Code : $"{r.Code} — {r.Desc}"))
            .ToList();
    }

    // ── Save / generate ──────────────────────────────────────────────────────────────────────────

    protected async Task OnSaveAsync()
    {
        if (!CanSaveHeader)
        {
            return;
        }

        if (NormalizedMode == "new" && !CanAdd)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        if (NormalizedMode == "edit" && !CanEditPermission)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        string? navigateTo = null;
        using (BeginBlockingWork("Please wait. Saving the stock count."))
        {
            IsSubmitting = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                var request = BuildRequest();
                if (NormalizedMode == "new")
                {
                    var saved = await StockCount.SaveAsync(request);
                    if (!saved.Succeeded)
                    {
                        ErrorMessage = saved.ErrorMessage;
                        return;
                    }

                    StatusMessage = $"Stock count {saved.CountNo} created.";
                }
                else
                {
                    request.RowVersion = _rowVersion;
                    var updated = await StockCount.UpdateAsync(_id, request);
                    if (!updated.Succeeded)
                    {
                        ErrorMessage = updated.ErrorMessage;
                        return;
                    }

                    StatusMessage = $"Stock count {updated.CountNo} saved.";
                }

                // House convention: a successful save returns to the list.
                navigateTo = "/inventory/stock-count";
            }
            finally
            {
                IsSubmitting = false;
            }
        }

        if (navigateTo is not null)
        {
            Navigation.NavigateTo(navigateTo);
        }
    }

    protected async Task OnGenerateClickAsync()
    {
        if (!CanConfigureScope || !CanEditPermission)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        if (NormalizedMode == "new")
        {
            await SaveAndGenerateAsync();
            return;
        }

        var counted = Lines.Count(l => l.PhysicalQty is not null);
        if (counted > 0)
        {
            GenerateConfirmMessage =
                $"This sheet has {counted} counted line(s). Regenerating replaces every line and discards them. Continue?";
            GenerateConfirmVisible = true;
            return;
        }

        await RunGenerateAsync(discardCounts: false);
    }

    protected async Task OnGenerateConfirmedAsync()
    {
        GenerateConfirmVisible = false;
        await RunGenerateAsync(discardCounts: true);
    }

    private async Task SaveAndGenerateAsync()
    {
        string? navigateTo = null;
        using (BeginBlockingWork("Please wait. Generating count lines."))
        {
            IsSubmitting = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                var saved = await StockCount.SaveAsync(BuildRequest());
                if (!saved.Succeeded)
                {
                    ErrorMessage = saved.ErrorMessage;
                    return;
                }

                var generated = await StockCount.GenerateAsync(saved.Id, discardCounts: false);
                if (!generated.Succeeded)
                {
                    ErrorMessage = generated.ErrorMessage;
                    return;
                }

                StatusMessage = $"Stock count {saved.CountNo} created with {generated.GeneratedLines} line(s).";
                navigateTo = $"/inventory/stock-count/edit/{saved.CountNo}";
            }
            finally
            {
                IsSubmitting = false;
            }
        }

        if (navigateTo is not null)
        {
            Navigation.NavigateTo(navigateTo);
        }
    }

    private async Task RunGenerateAsync(bool discardCounts)
    {
        using (BeginBlockingWork("Please wait. Generating count lines."))
        {
            IsSubmitting = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                if (NormalizedMode == "edit")
                {
                    // Persist the scope first so Generate reads exactly what the operator sees.
                    var request = BuildRequest();
                    request.RowVersion = _rowVersion;
                    var updated = await StockCount.UpdateAsync(_id, request);
                    if (!updated.Succeeded)
                    {
                        ErrorMessage = updated.ErrorMessage;
                        return;
                    }
                }

                var result = await StockCount.GenerateAsync(_id, discardCounts);
                if (!result.Succeeded)
                {
                    ErrorMessage = result.ErrorMessage;
                    return;
                }

                StatusMessage = $"Generated {result.GeneratedLines} line(s).";
                await LoadDocumentAsync(_countNo!);
            }
            finally
            {
                IsSubmitting = false;
            }
        }
    }

    // ── Count entry ──────────────────────────────────────────────────────────────────────────────

    protected void OnPhysicalQtyChanged(IvStockCountLineVm line, decimal? value)
    {
        line.PhysicalQty = value;
        line.Recompute();
    }

    protected async Task OnSaveCountsAsync()
    {
        if (!CanCountLines || !CanEditPermission)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        using (BeginBlockingWork("Please wait. Saving counted quantities."))
        {
            IsSubmitting = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                var requests = Lines
                    .Select(l => new IvStockCountLineCountRequest
                    {
                        BalLocId = l.BalLocId,
                        PhysicalQty = l.PhysicalQty
                    })
                    .ToList();

                var result = await StockCount.SaveCountsAsync(_id, requests, _rowVersion);
                if (!result.Succeeded)
                {
                    ErrorMessage = result.ErrorMessage;
                    return;
                }

                StatusMessage = "Counted quantities saved.";
                await LoadDocumentAsync(_countNo!);
            }
            finally
            {
                IsSubmitting = false;
            }
        }
    }

    protected void OpenEnterByItem()
    {
        if (!CanCountLines)
        {
            return;
        }

        EnterByItemCode = string.Empty;
        EnterByItemQty = null;
        EnterByItemSlices = [];
        EnterByItemSliceKey = null;
        EnterByItemHint = "Type an item code and press Find.";
        EnterByItemVisible = true;
    }

    protected Task OnEnterByItemFindAsync()
    {
        var code = (EnterByItemCode ?? string.Empty).Trim();
        EnterByItemSlices = [];
        EnterByItemSliceKey = null;

        if (code.Length == 0)
        {
            EnterByItemHint = "Type an item code first.";
            return Task.CompletedTask;
        }

        var matches = Lines.Where(l => string.Equals(l.ICode, code, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            EnterByItemHint = $"Item {code} is not on this count sheet.";
            return Task.CompletedTask;
        }

        EnterByItemSlices = matches
            .Select(l => new EnterByItemSlice(
                l.BalLocId.ToString(),
                $"{l.SliceLabel} · system {l.SystemQty:n4} · line {l.LineNumber}"))
            .ToList();
        EnterByItemSliceKey = matches.Count == 1 ? matches[0].BalLocId.ToString() : null;
        EnterByItemHint = matches.Count == 1
            ? "One pile — the quantity goes straight to it."
            : $"{matches.Count} piles hold this item. Pick the one you counted — a single total is never spread across them.";
        return Task.CompletedTask;
    }

    protected bool EnterByItemSameSheet => EnterByItemSlices.Count > 0;

    protected async Task OnEnterByItemApplyAsync()
    {
        if (!CanCountLines || !CanEditPermission)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        if (EnterByItemQty is null)
        {
            ErrorMessage = "Enter the counted quantity.";
            return;
        }

        if (!int.TryParse(EnterByItemSliceKey, out var balLocId) || balLocId <= 0)
        {
            ErrorMessage = "Pick the pile you counted.";
            return;
        }

        using (BeginBlockingWork("Please wait. Recording the count."))
        {
            IsSubmitting = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                var result = await StockCount.SetItemCountAsync(
                    _id, EnterByItemCode.Trim(), EnterByItemQty.Value, [balLocId]);
                if (!result.Succeeded)
                {
                    ErrorMessage = result.ErrorMessage;
                    return;
                }

                StatusMessage = $"Counted {EnterByItemQty.Value:n4} of {EnterByItemCode.Trim()}.";
                EnterByItemVisible = false;
                await LoadDocumentAsync(_countNo!);
            }
            finally
            {
                IsSubmitting = false;
            }
        }
    }

    // ── Post / rollback / recover ────────────────────────────────────────────────────────────────

    protected async Task OnPreviewPostAsync()
    {
        using (BeginBlockingWork("Please wait. Building the post preview."))
        {
            IsSubmitting = true;
            ErrorMessage = null;
            try
            {
                var result = await StockCount.PreviewPostAsync(_id);
                if (!result.Succeeded || result.Preview is null)
                {
                    ErrorMessage = result.ErrorMessage;
                    return;
                }

                Preview = result.Preview;
                StaleAcknowledged = false;
                PreviewVisible = true;
            }
            finally
            {
                IsSubmitting = false;
            }
        }
    }

    protected async Task OnPostConfirmedAsync()
    {
        PreviewVisible = false;

        using (BeginBlockingWork("Please wait. Posting the stock count."))
        {
            IsSubmitting = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                var result = await StockCount.PostAsync(_id);
                if (!result.Succeeded)
                {
                    ErrorMessage = result.ErrorMessage;
                    return;
                }

                StatusMessage = result.PostedBatchNo is int batchNo
                    ? $"Posted to batch {batchNo}."
                        + (result.PostedStaleLines > 0 ? $" {result.PostedStaleLines} stale line(s) flagged." : string.Empty)
                    : "No variance found. The sheet is POSTED with no batch.";

                await LoadDocumentAsync(_countNo!);
            }
            finally
            {
                IsSubmitting = false;
            }
        }
    }

    protected void OpenRollback()
    {
        RollbackReason = string.Empty;
        RollbackVisible = true;
    }

    protected async Task OnRollbackConfirmedAsync()
    {
        RollbackVisible = false;

        using (BeginBlockingWork("Please wait. Rolling back the stock count."))
        {
            IsSubmitting = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                var result = await StockCount.RollbackAsync(_id, RollbackReason);
                if (!result.Succeeded)
                {
                    ErrorMessage = result.ErrorMessage;
                    return;
                }

                StatusMessage = "Stock count rolled back. Stock has been restored.";
                await LoadDocumentAsync(_countNo!);
            }
            finally
            {
                IsSubmitting = false;
            }
        }
    }

    protected async Task OnRecoverAsync()
    {
        using (BeginBlockingWork("Please wait. Recovering the stock count."))
        {
            IsSubmitting = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                var result = await StockCount.RecoverAsync(_id);
                if (!result.Succeeded)
                {
                    ErrorMessage = result.ErrorMessage;
                    return;
                }

                StatusMessage = "Stock count recovered and reset to COUNTED.";
                await LoadDocumentAsync(_countNo!);
            }
            finally
            {
                IsSubmitting = false;
            }
        }
    }

    // ── Navigation ───────────────────────────────────────────────────────────────────────────────

    protected void NavigateList() => Navigation.NavigateTo("/inventory/stock-count");

    protected void NavigateEdit()
    {
        if (NormalizedMode != "view" || string.IsNullOrWhiteSpace(_countNo))
        {
            return;
        }

        var target = Header.Status switch
        {
            IvStockCountStatuses.Draft => "edit",
            IvStockCountStatuses.Counted or IvStockCountStatuses.RolledBack => "count",
            _ => null
        };

        if (target is null)
        {
            StatusMessage = $"A {Header.Status} sheet cannot be edited. Roll it back first.";
            return;
        }

        Navigation.NavigateTo($"/inventory/stock-count/{target}/{_countNo}");
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private IvStockCountSaveRequest BuildRequest() => new()
    {
        CountDate = Header.CountDate,
        WHCode = NullIfBlank(Header.WHCode),
        LocCode = NullIfBlank(Header.LocCode),
        IClassCode = NullIfBlank(Header.IClassCode),
        ISubClassCode = NullIfBlank(Header.ISubClassCode),
        IType = NullIfBlank(Header.IType),
        Statuses = SplitList(Header.IStatus),
        ICodes = SplitList(Header.ICodeList),
        IncludeZeroQty = Header.IncludeZeroQty,
        CountedBy = NullIfBlank(Header.CountedBy),
        Remark = NullIfBlank(Header.Remark)
    };

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    protected sealed record ScopeOption(string Code, string Name);

    protected sealed record EnterByItemSlice(string Key, string Name);

    protected sealed class IvStockCountHeaderVm
    {
        public DateTime CountDate { get; set; } = DateTime.Today;
        public string Status { get; set; } = IvStockCountStatuses.Draft;

        /// <summary>True while count entry is allowed — everything except POSTED (roll back) and CANCELLED.</summary>
        public bool CanCount => Status is IvStockCountStatuses.Draft
            or IvStockCountStatuses.Counted
            or IvStockCountStatuses.RolledBack;
        public string? WHCode { get; set; }
        public string? LocCode { get; set; }
        public string? IClassCode { get; set; }
        public string? ISubClassCode { get; set; }
        public string? IType { get; set; }
        public string? IStatus { get; set; }
        public string? ICodeList { get; set; }
        public bool IncludeZeroQty { get; set; } = true;
        public string? CountedBy { get; set; }
        public string? Remark { get; set; }
        public int? PostedBatchNo { get; set; }
        public string? RollbackReason { get; set; }
    }
}

/// <summary>
/// One row of the count grid. <see cref="Variance"/> / <see cref="Direction"/> / <see cref="IsStale"/>
/// are live projections recomputed on every edit — the stored <c>PhysicalQty</c> is the document.
/// </summary>
public sealed class IvStockCountLineVm
{
    public int Id { get; set; }
    public short LineNumber { get; set; }
    public int BalLocId { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? WHCode { get; set; }
    public string? LocCode { get; set; }
    public string? LotNo { get; set; }
    public string IStatus { get; set; } = string.Empty;
    public string? IClassCode { get; set; }
    public string? StdUom { get; set; }
    public decimal SystemQty { get; set; }
    public decimal? PhysicalQty { get; set; }
    public decimal? LiveQty { get; set; }
    public decimal? Variance { get; set; }
    public string? Direction { get; set; }
    public bool IsStale { get; set; }

    public string UiKey => BalLocId.ToString();

    public string SliceLabel =>
        $"{WHCode ?? "—"}/{(string.IsNullOrWhiteSpace(LocCode) ? "—" : LocCode)}/{(string.IsNullOrWhiteSpace(LotNo) ? "—" : LotNo)}";

    public string StaleText => PhysicalQty is null ? string.Empty : IsStale ? "moved" : "ok";

    public static IvStockCountLineVm FromDto(IvStockCountLineDto dto) => new()
    {
        Id = dto.Id,
        LineNumber = dto.LineNumber,
        BalLocId = dto.BalLocId,
        ICode = dto.ICode,
        IDesc = dto.IDesc,
        WHCode = dto.WHCode,
        LocCode = dto.LocCode,
        LotNo = dto.LotNo,
        IStatus = dto.IStatus,
        IClassCode = dto.IClassCode,
        StdUom = dto.StdUom,
        SystemQty = dto.SystemQty,
        PhysicalQty = dto.PhysicalQty,
        LiveQty = dto.LiveQty,
        Variance = dto.Variance,
        Direction = dto.Direction,
        IsStale = dto.IsStale
    };

    /// <summary>Recomputes the live columns after the operator edits the counted quantity.</summary>
    public void Recompute()
    {
        if (PhysicalQty is null || LiveQty is null)
        {
            Variance = null;
            Direction = null;
            IsStale = false;
            return;
        }

        Variance = Round(LiveQty.Value) - Round(PhysicalQty.Value);
        Direction = Variance > 0m ? "DECREASE" : Variance < 0m ? "INCREASE" : "NONE";
        IsStale = Round(LiveQty.Value) != SystemQty;
    }

    private static decimal Round(decimal value) =>
        decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}
