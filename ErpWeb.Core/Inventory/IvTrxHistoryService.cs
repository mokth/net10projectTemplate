using ErpWeb.Core.Menus;
using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Inventory;

/// <summary>
/// Transaction-inquiry and stock-card service.
///
/// <para>
/// Tenant scope is resolved FIRST (<see cref="IInventoryTenantContext.TryBranchScope"/>, fail-closed),
/// then ACCESS on the caller's own menu. The money columns are computed here after materialisation and
/// cleared when the caller lacks <c>VIEW_PRICE</c> on that menu (D11 Option B) — the pages only hide the
/// column; the service is the enforcement point, exactly as the shipped Balance-by-Lot page does with
/// <c>CanViewPrice</c>.
/// </para>
///
/// <para>
/// Row-level <c>InQty</c>/<c>OutQty</c> are scope-aware (D12): only the leg that lands inside the
/// requested slice counts, so a transfer out of the slice never inflates its "in".
/// </para>
/// </summary>
public sealed class IvTrxHistoryService : IIvTrxHistoryService
{
    /// <summary>Menus this service is allowed to serve. A page cannot borrow another screen's rights.</summary>
    private static readonly HashSet<string> KnownMenus = new(StringComparer.OrdinalIgnoreCase)
    {
        MenuCodes.InventoryTrxInquiry,
        MenuCodes.InventoryStockCard,

        // The lot inquiry's child "movements" panel reuses this service so the scope-aware In/Out
        // semantics and the adjustment-reason parsing have ONE definition. It is ACCESS-gated on the
        // lot menu, so adding it here grants the lot page nothing it did not already need.
        MenuCodes.InventoryLotInquiry
    };

    private readonly IIvStockHistoryRepository _history;
    private readonly IIvStockInquiryRepository _inquiry;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;

    public IvTrxHistoryService(
        IIvStockHistoryRepository history,
        IIvStockInquiryRepository inquiry,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights)
    {
        _history = history;
        _inquiry = inquiry;
        _tenant = tenant;
        _accessRights = accessRights;
    }

    public async Task<IvMasterOperationResult<IvTrxHistoryPage>> SearchAsync(
        string menuCode,
        IvTrxHistoryQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(menuCode, cancellationToken);
        if (context.Error is not null)
        {
            return IvMasterOperationResult<IvTrxHistoryPage>.Fail(context.Code, context.Error);
        }

        query ??= new IvTrxHistoryQuery();
        var (rows, total) = await _history.SearchTrxHistoryPagedAsync(
            context.CompanyCode!, context.BranchCode!, query, cancellationToken);

        await DecorateRowsAsync(menuCode, IvTrxHistoryScope.FromQuery(query), rows, cancellationToken);

        return IvMasterOperationResult<IvTrxHistoryPage>.Ok(new IvTrxHistoryPage
        {
            Rows = rows,
            TotalCount = total
        });
    }

    public async Task<IvMasterOperationResult<IvTrxHistorySummary>> GetSummaryAsync(
        string menuCode,
        IvTrxHistoryQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(menuCode, cancellationToken);
        if (context.Error is not null)
        {
            return IvMasterOperationResult<IvTrxHistorySummary>.Fail(context.Code, context.Error);
        }

        query ??= new IvTrxHistoryQuery();
        var summary = await _history.SummariseTrxHistoryAsync(
            context.CompanyCode!, context.BranchCode!, query, cancellationToken);

        return IvMasterOperationResult<IvTrxHistorySummary>.Ok(Mask(summary, await CanViewValueAsync(menuCode, cancellationToken)));
    }

    public async Task<IvMasterOperationResult<IvTrxHistoryPage>> ExportRowsAsync(
        string menuCode,
        IvTrxHistoryQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(menuCode, cancellationToken);
        if (context.Error is not null)
        {
            return IvMasterOperationResult<IvTrxHistoryPage>.Fail(context.Code, context.Error);
        }

        query ??= new IvTrxHistoryQuery();
        var total = await _history.CountTrxHistoryAsync(
            context.CompanyCode!, context.BranchCode!, query, cancellationToken);
        var rows = await _history.ListTrxHistoryForExportAsync(
            context.CompanyCode!, context.BranchCode!, query, cancellationToken);

        await DecorateRowsAsync(menuCode, IvTrxHistoryScope.FromQuery(query), rows, cancellationToken);

        return IvMasterOperationResult<IvTrxHistoryPage>.Ok(new IvTrxHistoryPage
        {
            Rows = rows,
            TotalCount = total
        });
    }

    public async Task<IvMasterOperationResult<IvStockCardPage>> GetStockCardAsync(
        string menuCode,
        IvTrxHistoryQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(menuCode, cancellationToken);
        if (context.Error is not null)
        {
            return IvMasterOperationResult<IvStockCardPage>.Fail(context.Code, context.Error);
        }

        query ??= new IvTrxHistoryQuery();

        // A card is ONE item's ledger. Without an item this degenerates into the transaction inquiry,
        // and without a period start the opening balance has no meaning (D12).
        if (string.IsNullOrWhiteSpace(query.ICode))
        {
            return IvMasterOperationResult<IvStockCardPage>.Fail(
                IvMasterErrorCode.Validation, "Select an item — a stock card is one item's ledger.");
        }

        if (query.TrxDateFrom is not DateTime fromDate)
        {
            return IvMasterOperationResult<IvStockCardPage>.Fail(
                IvMasterErrorCode.Validation, "Select a start date — the opening balance is everything before it.");
        }

        var company = context.CompanyCode!;
        var branch = context.BranchCode!;
        var scope = IvTrxHistoryScope.FromQuery(query);
        var canViewValue = await CanViewValueAsync(menuCode, cancellationToken);

        var opening = await _history.SumScopeOpeningQtyAsync(
            company, branch, query, fromDate.Date, cancellationToken);

        var (ledger, matching) = await _history.ListTrxHistoryLedgerAsync(
            company, branch, query, cancellationToken);

        var truncated = ledger.Count > IvStockHistoryRepository.MaxStockCardRows;
        var rows = (truncated
                ? ledger.Take(IvStockHistoryRepository.MaxStockCardRows)
                : ledger)
            .ToList();

        await DecorateRowsAsync(menuCode, scope, rows, cancellationToken);

        // Running balance in the ledger's own chronological order — the only order in which it means
        // anything (D12). A cap on the ledger is reported, never silently trimmed.
        var running = opening;
        foreach (var row in rows)
        {
            running += row.NetQty;
            row.RunningQty = IvQty.Round(running);
        }

        var summary = Mask(
            await _history.SummariseTrxHistoryAsync(company, branch, query, cancellationToken),
            canViewValue);

        var liveQty = await _inquiry.SumOnHandForScopeAsync(company, branch, scope, cancellationToken);

        return IvMasterOperationResult<IvStockCardPage>.Ok(new IvStockCardPage
        {
            Rows = rows,
            TotalCount = rows.Count,
            MatchingCount = matching,
            OpeningQty = IvQty.Round(opening),
            InQty = summary.InQty,
            OutQty = summary.OutQty,
            AdjustNetQty = summary.AdjustNetQty,
            LiveQty = liveQty is null ? null : IvQty.Round(liveQty.Value),
            Truncated = truncated,
            TotalValue = summary.TotalValue
        });
    }

    /// <summary>
    /// Fills the service-owned row columns. <c>InQty</c>/<c>OutQty</c> are scope-aware: a leg counts
    /// only when it lands inside the requested slice, so an unfiltered query (empty scope) reduces to
    /// the plain <c>ToStdQty - FrStdQty</c> net.
    /// </summary>
    private async Task DecorateRowsAsync(
        string menuCode,
        IvTrxHistoryScope scope,
        IReadOnlyList<IvTrxHistoryRow> rows,
        CancellationToken cancellationToken)
    {
        var canViewValue = await CanViewValueAsync(menuCode, cancellationToken);

        foreach (var row in rows)
        {
            row.InQty = scope.MatchesToLeg(row.ICode, row.ToWarehouse, row.ToLocation, row.ToLotNo, row.IStatus)
                ? row.ToStdQty ?? 0m
                : 0m;

            row.OutQty = scope.MatchesFromLeg(row.ICode, row.FrWarehouse, row.FrLocation, row.FrLotNo, row.IStatus)
                ? row.FrStdQty ?? 0m
                : 0m;

            // Derived columns are populated here rather than declared as get-only expressions: the grid
            // resolves DxGridDataColumn.FieldName by property name and needs a settable property.
            row.NetQty = row.InQty - row.OutQty;
            row.StdUom = row.InQty > 0m
                ? row.ToStdUom
                : row.OutQty > 0m
                    ? row.FrStdUom
                    : row.ToStdUom ?? row.FrStdUom;

            // The reason format is an ADJ contract; other documents store free text in Remarks.
            if (string.Equals(row.TrxType, IvTrxHistoryTypes.StockAdjustment, StringComparison.OrdinalIgnoreCase))
            {
                row.Reason = IvStockAdjustmentLineInvariant.ParseStoredRemarks(row.Remarks).Reason;
            }

            row.EstValue = canViewValue ? IvQty.Round(row.NetQty * (row.UnitPrice ?? 0m)) : null;
        }
    }

    private static IvTrxHistorySummary Mask(IvTrxHistorySummary summary, bool canViewValue) =>
        new()
        {
            TotalRows = summary.TotalRows,
            BatchCount = summary.BatchCount,
            InQty = IvQty.Round(summary.InQty),
            OutQty = IvQty.Round(summary.OutQty),
            AdjustNetQty = IvQty.Round(summary.AdjustNetQty),
            TotalValue = canViewValue ? IvQty.Round(summary.TotalValue ?? 0m) : null
        };

    private Task<bool> CanViewValueAsync(string menuCode, CancellationToken cancellationToken) =>
        _accessRights.CanAsync(menuCode, PermissionCodes.ViewPrice, cancellationToken);

    private async Task<UserContext> ResolveAsync(string menuCode, CancellationToken cancellationToken)
    {
        if (!KnownMenus.Contains((menuCode ?? string.Empty).Trim()))
        {
            return UserContext.Fail(IvMasterErrorCode.Validation, $"Unknown inquiry menu '{menuCode}'.");
        }

        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return UserContext.Fail(IvMasterErrorCode.InvalidScope, "Invalid company or branch context.");
        }

        if (!await _accessRights.CanAsync(menuCode, PermissionCodes.Access, cancellationToken))
        {
            return UserContext.Fail(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        return UserContext.Ok(scope.CompanyCode, scope.BranchCode!);
    }

    private readonly record struct UserContext(
        string? CompanyCode,
        string? BranchCode,
        string? Error,
        IvMasterErrorCode Code)
    {
        public static UserContext Ok(string companyCode, string branchCode) =>
            new(companyCode, branchCode, null, IvMasterErrorCode.None);

        public static UserContext Fail(IvMasterErrorCode code, string error) =>
            new(null, null, error, code);
    }
}
