using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Inventory;

/// <inheritdoc cref="IIvLotInquiryService"/>
public sealed class IvLotInquiryService : IIvLotInquiryService
{
    /// <summary>Menus this service is allowed to serve. A page cannot borrow another screen's rights.</summary>
    private static readonly HashSet<string> KnownMenus = new(StringComparer.OrdinalIgnoreCase)
    {
        MenuCodes.InventoryLotInquiry
    };

    /// <summary>
    /// How many movements the passport's ledger panel loads. A lot's ledger is an inspection aid, not a
    /// report — the Transaction Inquiry page is where an unbounded movement list belongs.
    /// </summary>
    private const int MovementPanelLimit = 200;

    private readonly IIvStockInquiryRepository _inquiry;
    private readonly IIvTrxHistoryService _trxHistory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _dates;

    public IvLotInquiryService(
        IIvStockInquiryRepository inquiry,
        IIvTrxHistoryService trxHistory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService dates)
    {
        _inquiry = inquiry;
        _trxHistory = trxHistory;
        _tenant = tenant;
        _accessRights = accessRights;
        _dates = dates;
    }

    public async Task<IvMasterOperationResult<IvLotInquiryPage>> SearchAsync(
        string menuCode,
        IvLotInquiryQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(menuCode, cancellationToken);
        if (!context.Succeeded)
        {
            return IvMasterOperationResult<IvLotInquiryPage>.Fail(context.ErrorCode, context.Error!);
        }

        var prepared = Prepare(query);
        var (rows, total) = await _inquiry.SearchLotInquiryAsync(
            context.CompanyCode!, context.BranchCode!, prepared, cancellationToken);

        Decorate(rows, prepared.AsOfDate);

        return IvMasterOperationResult<IvLotInquiryPage>.Ok(new IvLotInquiryPage
        {
            Rows = rows,
            TotalCount = total
        });
    }

    public async Task<IvMasterOperationResult<IvLotInquirySummary>> GetSummaryAsync(
        string menuCode,
        IvLotInquiryQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(menuCode, cancellationToken);
        if (!context.Succeeded)
        {
            return IvMasterOperationResult<IvLotInquirySummary>.Fail(context.ErrorCode, context.Error!);
        }

        var prepared = Prepare(query);
        var summary = await _inquiry.SummariseLotInquiryAsync(
            context.CompanyCode!, context.BranchCode!, prepared, cancellationToken);

        return IvMasterOperationResult<IvLotInquirySummary>.Ok(summary);
    }

    public async Task<IvMasterOperationResult<IvLotInquiryDetail>> GetDetailAsync(
        string menuCode,
        IvLotInquiryRow? lot,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(menuCode, cancellationToken);
        if (!context.Succeeded)
        {
            return IvMasterOperationResult<IvLotInquiryDetail>.Fail(context.ErrorCode, context.Error!);
        }

        if (lot is null || lot.LotId <= 0)
        {
            return IvMasterOperationResult<IvLotInquiryDetail>.Fail(
                IvMasterErrorCode.Validation, "Select a lot to inspect.");
        }

        var company = context.CompanyCode!;
        var branch = context.BranchCode!;

        var piles = await _inquiry.ListLotPilesAsync(company, branch, lot.LotId, cancellationToken);

        // The ledger goes through the transaction-inquiry service so "in"/"out" (scope-aware), the
        // adjustment reason parsing and the price masking stay ONE definition. Pinning both the item and
        // the lot keeps the movement list on this lot: a transfer between lots shows on both ledgers.
        var movements = await _trxHistory.SearchAsync(
            menuCode,
            new IvTrxHistoryQuery
            {
                ICode = lot.ICode,
                LotNo = lot.LotNo,
                Take = MovementPanelLimit
            },
            cancellationToken);

        if (!movements.Succeeded || movements.Data is null)
        {
            return IvMasterOperationResult<IvLotInquiryDetail>.Fail(
                movements.ErrorCode, movements.Message ?? "Unable to load the lot's movements.");
        }

        var prepared = Prepare(null);
        Decorate([lot], prepared.AsOfDate);

        return IvMasterOperationResult<IvLotInquiryDetail>.Ok(new IvLotInquiryDetail
        {
            Lot = lot,
            Piles = piles,
            Movements = movements.Data.Rows,
            PileQty = piles.Sum(x => x.StdQty),
            MovementNetQty = movements.Data.Rows.Sum(x => x.NetQty)
        });
    }

    /// <summary>Stamps the single company-local as-of date every derived age is measured against (D14).</summary>
    private IvLotInquiryQuery Prepare(IvLotInquiryQuery? query)
    {
        var prepared = query ?? new IvLotInquiryQuery();
        prepared.AsOfDate = _dates.Today;
        return prepared;
    }

    /// <summary>
    /// Fills the two derived ages from the one as-of date the query used, so a row's "age" and its
    /// "days to expiry" always describe the same instant.
    /// </summary>
    private static void Decorate(IReadOnlyList<IvLotInquiryRow> rows, DateTime asOfDate)
    {
        var asOf = asOfDate.Date;
        foreach (var row in rows)
        {
            if (row.ReceiptDate is DateTime received)
            {
                row.AgeDays = (int)(asOf - received.Date).TotalDays;
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
