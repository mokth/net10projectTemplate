using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Deployment gate for V2 stock writers. An older binary lacks this type and cannot
/// safely post after an ACTIVE epoch exists because V2 history is append-only.
/// </summary>
public static class StockLedgerCompatibility
{
    public const int ProtocolVersion = 2;

    public static async Task EnsureCompatibleAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var active = await db.StockLedgerEpochs.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == companyCode
            && x.BranchCode == branchCode
            && x.Status == StockLedgerEpochStatuses.Active, cancellationToken);
        if (!active)
            return;

        var postingType = db.Model.FindEntityType(typeof(StockPosting));
        if (postingType is null)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.LedgerMismatch,
                "An ACTIVE stock-ledger epoch exists but this binary cannot write V2 postings."));
    }
}
