using ErpWeb.Core.Menus;
using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Inventory;

/// <inheritdoc cref="IIvStockSummaryService"/>
public sealed class IvStockSummaryService : IIvStockSummaryService
{
    /// <summary>
    /// Menus this service is allowed to serve. A page cannot borrow another screen's rights.
    ///
    /// <para>
    /// <b>Est. Inventory Value reuses this service on purpose.</b> "How much stock is there, by group"
    /// and "what is it worth, by group" are the same composition with a different emphasis — shipping a
    /// second grouped query would let the two drift. The value screen passes its OWN menu code, so its
    /// <c>ACCESS</c> and <c>VIEW_PRICE</c> are checked against <c>INV_STOCK_VALUE</c>, not against the
    /// summary screen's grant (D11 Option B).
    /// </para>
    /// </summary>
    private static readonly HashSet<string> KnownMenus = new(StringComparer.OrdinalIgnoreCase)
    {
        MenuCodes.InventoryStockSummary,
        MenuCodes.InventoryStockValue
    };

    /// <summary>
    /// Shown in the UOM column when a group genuinely mixes units. Never rendered as a blank cell: a
    /// blank cell reads as "unknown", and this one is known to be many.
    /// </summary>
    public const string MixedUomDisplay = "(mixed)";

    private readonly IIvStockInquiryRepository _inquiry;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;

    public IvStockSummaryService(
        IIvStockInquiryRepository inquiry,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights)
    {
        _inquiry = inquiry;
        _tenant = tenant;
        _accessRights = accessRights;
    }

    public async Task<IvMasterOperationResult<IvStockSummaryPage>> SearchAsync(
        string menuCode,
        IvStockSummaryQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(menuCode, cancellationToken);
        if (!context.Succeeded)
        {
            return IvMasterOperationResult<IvStockSummaryPage>.Fail(context.ErrorCode, context.Error!);
        }

        var prepared = Prepare(query);
        var canViewValue = await CanViewValueAsync(context.MenuCode!, cancellationToken);

        var (rows, total) = await _inquiry.SearchStockSummaryAsync(
            context.CompanyCode!, context.BranchCode!, prepared, cancellationToken);

        Decorate(rows, prepared.GroupBy, canViewValue);

        return IvMasterOperationResult<IvStockSummaryPage>.Ok(new IvStockSummaryPage
        {
            Rows = rows,
            TotalCount = total
        });
    }

    public async Task<IvMasterOperationResult<IvStockSummarySummary>> GetSummaryAsync(
        string menuCode,
        IvStockSummaryQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(menuCode, cancellationToken);
        if (!context.Succeeded)
        {
            return IvMasterOperationResult<IvStockSummarySummary>.Fail(context.ErrorCode, context.Error!);
        }

        var prepared = Prepare(query);
        var canViewValue = await CanViewValueAsync(context.MenuCode!, cancellationToken);

        var summary = await _inquiry.SummariseStockSummaryAsync(
            context.CompanyCode!, context.BranchCode!, prepared, cancellationToken);

        return IvMasterOperationResult<IvStockSummarySummary>.Ok(new IvStockSummarySummary
        {
            GroupCount = summary.GroupCount,
            ItemCount = summary.ItemCount,
            PileCount = summary.PileCount,
            ZeroQtyPileCount = summary.ZeroQtyPileCount,
            TotalQty = summary.TotalQty,
            TotalValue = canViewValue ? IvQty.Round(summary.TotalValue ?? 0m) : null
        });
    }

    public async Task<IvMasterOperationResult<IvStockSummaryPage>> ExportRowsAsync(
        string menuCode,
        IvStockSummaryQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(menuCode, cancellationToken);
        if (!context.Succeeded)
        {
            return IvMasterOperationResult<IvStockSummaryPage>.Fail(context.ErrorCode, context.Error!);
        }

        var prepared = Prepare(query);
        var canViewValue = await CanViewValueAsync(context.MenuCode!, cancellationToken);

        var total = await _inquiry.CountStockSummaryAsync(
            context.CompanyCode!, context.BranchCode!, prepared, cancellationToken);
        var rows = await _inquiry.ListStockSummaryForExportAsync(
            context.CompanyCode!, context.BranchCode!, prepared, cancellationToken);

        Decorate(rows, prepared.GroupBy, canViewValue);

        return IvMasterOperationResult<IvStockSummaryPage>.Ok(new IvStockSummaryPage
        {
            Rows = rows,
            TotalCount = total
        });
    }

    /// <summary>Normalizes the grouping mode so an unknown token can never reach the repository switch.</summary>
    private static IvStockSummaryQuery Prepare(IvStockSummaryQuery? query)
    {
        var prepared = query ?? new IvStockSummaryQuery();
        prepared.GroupBy = IvStockSummaryGroupBys.Normalize(prepared.GroupBy);
        return prepared;
    }

    /// <summary>
    /// Fills the two service-owned columns: the UOM display (D16) and the rounded/masked estimate.
    /// Rounding happens HERE, once, so the grid, the summary and the export can never show a value the
    /// other two disagree with.
    /// </summary>
    private static void Decorate(
        IReadOnlyList<IvStockSummaryRow> rows,
        string groupBy,
        bool canViewValue)
    {
        var singleUomPerGroup = IvStockSummaryGroupBys.HasSingleUomPerGroup(groupBy);

        foreach (var row in rows)
        {
            // The group key differs per mode, so the row identity is composed here rather than being a
            // single column the grid could use directly.
            row.RowKey = IvStockSummaryGroupBys.Normalize(groupBy) switch
            {
                IvStockSummaryGroupBys.Warehouse => $"WH:{row.WhCode}",
                IvStockSummaryGroupBys.Class => $"CLS:{row.IClassCode}",
                IvStockSummaryGroupBys.ItemWarehouse => $"ITEM:{row.ICode}|WH:{row.WhCode}",
                _ => $"ITEM:{row.ICode}"
            };

            // Warehouse/Class groups are mixed-unit BY DEFINITION of the decision, so no UOM is claimed
            // for them at all — the page hides the column and captions the opt-in total instead.
            row.UomDisplay = singleUomPerGroup
                ? (string.IsNullOrWhiteSpace(row.StdUom) ? null : row.StdUom)
                : null;

            row.EstValue = canViewValue ? IvQty.Round(row.EstValue ?? 0m) : null;
        }
    }

    private Task<bool> CanViewValueAsync(string menuCode, CancellationToken cancellationToken) =>
        _accessRights.CanAsync(menuCode, PermissionCodes.ViewPrice, cancellationToken);

    private Task<IvInquiryScopeContext> ResolveAsync(string menuCode, CancellationToken cancellationToken) =>
        IvInquiryScopeResolver.ResolveAsync(_tenant, _accessRights, menuCode, KnownMenus, cancellationToken);
}
