using System.Security.Cryptography;
using System.Text;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public static class InventoryStockPostingCommandFactory
{
    public static async Task<StockPostingCommand> CreateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        bool reversal,
        DateTime effectiveAt,
        CancellationToken cancellationToken)
    {
        var batch = await db.IvTrxBatches.AsNoTracking().SingleAsync(x =>
            x.CompanyCode == companyCode && x.BranchCode == branchCode && x.BatchNo == batchNo,
            cancellationToken);
        var lines = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(x => x.BatchId == batch.Id)
            .OrderBy(x => x.DocumentRevision).ThenBy(x => x.TrxLineNo)
            .Select(x => new
            {
                x.Id, x.DocumentRevision, x.TrxLineNo, x.ICode,
                x.FrWarehouse, x.FrLocation, x.FrLotNo, x.FrStdQty, x.FrStdUom,
                x.ToWarehouse, x.ToLocation, x.ToLotNo, x.ToStdQty, x.ToStdUom
            }).ToListAsync(cancellationToken);
        var revision = lines.Count == 0 ? 0 : lines.Max(x => x.DocumentRevision);
        long? reversesPostingId = null;
        if (reversal)
        {
            reversesPostingId = await db.IvTrxHistories.AsNoTracking()
                .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode
                    && x.BatchNo == batchNo && x.LedgerVersion == 2 && x.EntryRole == "ORIGINAL")
                .OrderByDescending(x => x.DocumentRevision).ThenByDescending(x => x.Id)
                .Select(x => x.StockPostingId).FirstOrDefaultAsync(cancellationToken);
        }

        var role = reversal ? "REVERSAL" : "PRIMARY";
        var evidence = StockPostingFingerprint.Create(
            new { batchNo, batch.TrxType, revision, role, effectiveAt, reversesPostingId },
            new { batch.Id, batch.RefNo, batch.TrxDtTime, lines });
        return new StockPostingCommand
        {
            RequestId = DeterministicRequestId(companyCode, branchCode, batchNo, revision, role),
            CommandType = $"IV_{(reversal ? "ROLLBACK" : "POST")}_{batch.TrxType}".ToUpperInvariant(),
            SourceModule = "INVENTORY",
            SourceDocumentType = batch.TrxType,
            SourceDocumentId = batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SourceDocumentNo = batchNo.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DocumentRevision = revision,
            PostingRole = role,
            EffectiveAt = effectiveAt,
            Evidence = evidence,
            ReversesPostingId = reversesPostingId,
        };
    }

    private static Guid DeterministicRequestId(
        string company, string branch, int batchNo, int revision, string role)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{company.Trim().ToUpperInvariant()}|{branch.Trim().ToUpperInvariant()}|{batchNo}|{revision}|{role}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
