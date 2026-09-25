using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Inventory;

/// <inheritdoc cref="IIvStockAlertService"/>
public sealed class IvStockAlertService : IIvStockAlertService
{
    /// <summary>Menus this service is allowed to serve. A page cannot borrow another screen's rights.</summary>
    private static readonly HashSet<string> KnownMenus = new(StringComparer.OrdinalIgnoreCase)
    {
        MenuCodes.InventoryStockAlerts
    };

    private readonly IIvStockInquiryRepository _inquiry;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _dates;

    public IvStockAlertService(
        IIvStockInquiryRepository inquiry,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService dates)
    {
        _inquiry = inquiry;
        _tenant = tenant;
        _accessRights = accessRights;
        _dates = dates;
    }

    public async Task<IvMasterOperationResult<IvStockAlertPage>> SearchAsync(
        string menuCode,
        IvStockAlertQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(menuCode, cancellationToken);
        if (!context.Succeeded)
        {
            return IvMasterOperationResult<IvStockAlertPage>.Fail(context.ErrorCode, context.Error!);
        }

        var prepared = Prepare(query);
        if (prepared.Error is not null)
        {
            return IvMasterOperationResult<IvStockAlertPage>.Fail(
                IvMasterErrorCode.Validation, prepared.Error);
        }

        var (rows, total) = await _inquiry.SearchStockAlertsAsync(
            context.CompanyCode!, context.BranchCode!, prepared.Query, cancellationToken);

        Decorate(rows, prepared.Query.AsOfDate);

        return IvMasterOperationResult<IvStockAlertPage>.Ok(new IvStockAlertPage
        {
            Rows = rows,
            TotalCount = total,
            AsOfDate = prepared.Query.AsOfDate
        });
    }

    public async Task<IvMasterOperationResult<IvStockAlertSummary>> GetSummaryAsync(
        string menuCode,
        IvStockAlertQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(menuCode, cancellationToken);
        if (!context.Succeeded)
        {
            return IvMasterOperationResult<IvStockAlertSummary>.Fail(context.ErrorCode, context.Error!);
        }

        var prepared = Prepare(query);
        if (prepared.Error is not null)
        {
            return IvMasterOperationResult<IvStockAlertSummary>.Fail(
                IvMasterErrorCode.Validation, prepared.Error);
        }

        var summary = await _inquiry.SummariseStockAlertsAsync(
            context.CompanyCode!, context.BranchCode!, prepared.Query, cancellationToken);

        return IvMasterOperationResult<IvStockAlertSummary>.Ok(summary);
    }

    public async Task<IvMasterOperationResult<IvStockAlertPage>> ExportRowsAsync(
        string menuCode,
        IvStockAlertQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(menuCode, cancellationToken);
        if (!context.Succeeded)
        {
            return IvMasterOperationResult<IvStockAlertPage>.Fail(context.ErrorCode, context.Error!);
        }

        var prepared = Prepare(query);
        if (prepared.Error is not null)
        {
            return IvMasterOperationResult<IvStockAlertPage>.Fail(
                IvMasterErrorCode.Validation, prepared.Error);
        }

        var total = await _inquiry.CountStockAlertsAsync(
            context.CompanyCode!, context.BranchCode!, prepared.Query, cancellationToken);
        var rows = await _inquiry.ListStockAlertsForExportAsync(
            context.CompanyCode!, context.BranchCode!, prepared.Query, cancellationToken);

        Decorate(rows, prepared.Query.AsOfDate);

        return IvMasterOperationResult<IvStockAlertPage>.Ok(new IvStockAlertPage
        {
            Rows = rows,
            TotalCount = total,
            AsOfDate = prepared.Query.AsOfDate
        });
    }

    /// <summary>
    /// Normalizes the rule, stamps the company-local <c>AsOfDate</c> (D14) and enforces the one
    /// threshold invariant the page cannot express by itself: <c>DeadDays</c> must be greater than
    /// <c>SlowDays</c>, or the two ageing rules describe the same population and "dead" loses its
    /// meaning. The message names BOTH numbers, so the user knows which one to change.
    /// </summary>
    private (IvStockAlertQuery Query, string? Error) Prepare(IvStockAlertQuery? query)
    {
        var prepared = query ?? new IvStockAlertQuery();
        prepared.Rule = IvStockAlertRules.Normalize(prepared.Rule);

        // The date part of the company-local clock, resolved once per call and used by every rule.
        prepared.AsOfDate = _dates.Today;

        prepared.SlowDays = Math.Max(1, prepared.SlowDays);
        prepared.DeadDays = Math.Max(1, prepared.DeadDays);
        prepared.ExpiryDays = Math.Max(1, prepared.ExpiryDays);

        if (prepared.DeadDays <= prepared.SlowDays)
        {
            return (prepared,
                $"Dead days ({prepared.DeadDays}) must be greater than slow days ({prepared.SlowDays}).");
        }

        return (prepared, null);
    }

    /// <summary>
    /// Fills the two age columns after materialisation. Both are pure date arithmetic against the
    /// single <paramref name="asOfDate"/> the rules were evaluated with, so a row can never report an
    /// age that disagrees with why it was selected.
    /// </summary>
    private static void Decorate(IReadOnlyList<IvStockAlertRow> rows, DateTime asOfDate)
    {
        var asOf = asOfDate.Date;
        foreach (var row in rows)
        {
            // Item alone is not unique on the expiry rules: one row exists per warehouse holding the lot.
            row.RowKey = $"{row.ICode}|{row.WhCode}|{row.LotNo}";

            if (row.LastMovement is DateTime last)
            {
                row.DaysSinceMovement = (int)(asOf - last.Date).TotalDays;
            }

            if (row.ExpiryDate is DateTime expiry)
            {
                row.DaysToExpiry = (int)(expiry.Date - asOf).TotalDays;
            }
        }
    }

    private Task<IvInquiryScopeContext> ResolveAsync(string menuCode, CancellationToken cancellationToken) =>
        IvInquiryScopeResolver.ResolveAsync(_tenant, _accessRights, menuCode, KnownMenus, cancellationToken);
}
