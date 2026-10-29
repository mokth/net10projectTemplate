using System.Globalization;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

/// <summary>
/// Reads the single history row a replay event id refers to, for explaining a refusal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only and bounded.</b> One <c>AsNoTracking</c> primary-key lookup, filtered by the trusted
/// company and branch, no navigation expansion, no aggregate. It is called only after the posting path
/// has already refused the write, outside the failed batch's transaction, and it never writes.
/// </para>
/// <para>
/// <b>Fails closed.</b> Any problem — deleted row, foreign tenant, reversal, stale status, cancelled or
/// timed-out lookup, database error — returns <c>null</c> so the operator gets the safe generic message
/// instead of a wrong document name.
/// </para>
/// </remarks>
public sealed class IvFailureEvidenceReader : IIvFailureEvidenceReader
{
    /// <summary>History rows created by an original posting, including legacy rows that carry no role.</summary>
    private const string OriginalEntryRole = "ORIGINAL";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<IvFailureEvidenceReader>? _logger;

    public IvFailureEvidenceReader(
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<IvFailureEvidenceReader>? logger = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _logger = logger;
    }

    public async Task<IvFailureEvidence?> TryReadAsync(
        string companyCode,
        string branchCode,
        long eventId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyCode) || string.IsNullOrWhiteSpace(branchCode))
        {
            return null;
        }

        // Negative ids are count generations in the replay timeline; they have no history row and no
        // document number, so only the family is reported.
        if (eventId < 0)
        {
            return new IvFailureEvidence
            {
                DocumentKind = ErpDocumentKind.StockCount,
                IsStockCountGeneration = true
            };
        }

        if (eventId == 0 || eventId > int.MaxValue)
        {
            return null;
        }

        try
        {
            var id = (int)eventId;
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var row = await db.IvTrxHistories
                .AsNoTracking()
                .Where(x => x.Id == id
                    && x.CompanyCode == companyCode
                    && x.BranchCode == branchCode)
                .Select(x => new HistoryRow
                {
                    BatchNo = x.BatchNo,
                    TrxType = x.TrxType,
                    TrxDtTime = x.TrxDtTime,
                    BatchStatus = x.BatchStatus,
                    EntryRole = x.EntryRole,
                    ReversesHistoryId = x.ReversesHistoryId,
                    ICode = x.ICode,
                    IDesc = x.IDesc,
                    FrWarehouse = x.FrWarehouse,
                    FrLocation = x.FrLocation,
                    FrLotNo = x.FrLotNo,
                    FrStdQty = x.FrStdQty,
                    ToWarehouse = x.ToWarehouse,
                    ToLocation = x.ToLocation,
                    ToLotNo = x.ToLotNo,
                    ToStdQty = x.ToStdQty
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (row is null)
            {
                return null;
            }

            // A reversal is not the transaction the operator needs to look at, and a stale or
            // non-posted row cannot be relied on. Omit rather than mislead.
            if (row.EntryRole is not (null or OriginalEntryRole)
                || row.ReversesHistoryId is not null
                || !string.Equals(row.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var fromSide = row.FrStdQty.HasValue;
            var toSide = row.ToStdQty.HasValue;
            var unambiguous = fromSide != toSide;

            return new IvFailureEvidence
            {
                DocumentKind = IvInventoryOperatorErrors.ToDocumentKind(row.TrxType),
                DocumentNumber = row.BatchNo.ToString(CultureInfo.InvariantCulture),
                TransactionDate = row.TrxDtTime,
                ItemCode = row.ICode,
                ItemDescription = row.IDesc,
                // A transfer carries both sides; the operated location is ambiguous, so only the item
                // is reported instead of picking one side at random.
                Warehouse = unambiguous ? fromSide ? row.FrWarehouse : row.ToWarehouse : null,
                Location = unambiguous ? fromSide ? row.FrLocation : row.ToLocation : null,
                LotNo = unambiguous ? fromSide ? row.FrLotNo : row.ToLotNo : null
            };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Explaining a failure must never fail the operation a second time.
            _logger?.LogDebug(ex, "Failure evidence lookup failed for event {EventId}", eventId);
            return null;
        }
    }

    private sealed class HistoryRow
    {
        public int BatchNo { get; init; }
        public string TrxType { get; init; } = string.Empty;
        public DateTime TrxDtTime { get; init; }
        public string BatchStatus { get; init; } = string.Empty;
        public string? EntryRole { get; init; }
        public int? ReversesHistoryId { get; init; }
        public string ICode { get; init; } = string.Empty;
        public string? IDesc { get; init; }
        public string? FrWarehouse { get; init; }
        public string? FrLocation { get; init; }
        public string? FrLotNo { get; init; }
        public decimal? FrStdQty { get; init; }
        public string? ToWarehouse { get; init; }
        public string? ToLocation { get; init; }
        public string? ToLotNo { get; init; }
        public decimal? ToStdQty { get; init; }
    }
}
