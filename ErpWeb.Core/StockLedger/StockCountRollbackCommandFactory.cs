using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>Creates the reversal root for one prepared, active count sheet.</summary>
public static class StockCountRollbackCommandFactory
{
    public static async Task<StockPostingCommand> CreateAsync(
        AppDbContext db, StockReplayCountSheetRemovalPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        StockReplayCountSheetRemovalPlanner.ValidatePreparedPlan(plan);
        var header = await db.IvStockCountHdrs.AsNoTracking()
            .Where(h => h.Id == plan.CountHeaderId
                && h.CompanyCode == plan.CompanyCode
                && h.BranchCode == plan.BranchCode)
            .Select(h => new { h.CountNo, h.CountDate, h.Status,
                h.RowVersion })
            .SingleOrDefaultAsync(cancellationToken);
        var anchor = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == plan.AnchorPostingId
                && p.CompanyCode == plan.CompanyCode
                && p.BranchCode == plan.BranchCode,
                cancellationToken);
        if (header is null || header.Status != "POSTED"
            || header.CountDate != header.CountDate.Date
            || StockReplayEventKey.CountEndOfDay(header.CountDate)
                != plan.EffectiveAt
            || !header.RowVersion.SequenceEqual(plan.HeaderRowVersion)
            || anchor is null || anchor.SealedAtUtc is null
            || anchor.SourceModule != "INVENTORY"
            || anchor.SourceDocumentType != "IV_STOCK_COUNT"
            || anchor.SourceDocumentId != plan.CountHeaderId.ToString(
                CultureInfo.InvariantCulture)
            || anchor.SourceDocumentNo != header.CountNo
            || anchor.PostingRole != "PRIMARY"
            || anchor.PostingSequence != plan.AnchorPostingSequence
            || anchor.DocumentRevision != plan.AnchorDocumentRevision
            || anchor.EffectiveAt != plan.EffectiveAt)
            throw Invalid("The count rollback anchor changed after planning.");

        var lineIds = plan.Lines.Select(l => l.CountLineId).ToArray();
        var lines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.StockCountId == plan.CountHeaderId
                && l.PhysicalQty != null)
            .Select(l => new
            {
                l.Id, l.BalLocId, l.PhysicalQty,
                l.WHCode, l.LocCode, l.RowVersion
            }).ToArrayAsync(cancellationToken);
        var planned = plan.Lines.ToDictionary(l => l.CountLineId);
        if (lines.Length != lineIds.Length
            || lines.Any(l => !planned.TryGetValue(l.Id, out var p)
                || l.BalLocId != p.BalLocId
                || l.PhysicalQty != p.PhysicalQty
                || !l.RowVersion.SequenceEqual(p.CountLineRowVersion)))
            throw Invalid("The count rollback lines changed after planning.");

        var evidence = StockPostingFingerprint.Create(new
        {
            plan.CountHeaderId, header.CountNo, header.CountDate,
            plan.AnchorPostingId, plan.AnchorDocumentRevision,
            role = "REVERSAL", plan.EffectiveAt, plan.PlanHash
        }, new
        {
            header.RowVersion,
            lines = lines.OrderBy(l => l.Id).Select(l => new
            {
                l.Id, l.BalLocId, l.PhysicalQty, l.RowVersion
            }).ToArray(),
            plan.PlanHash
        });
        return new StockPostingCommand
        {
            RequestId = RequestId(plan),
            CommandType = "IV_ROLLBACK_STOCK_COUNT",
            SourceModule = "INVENTORY",
            SourceDocumentType = "IV_STOCK_COUNT",
            SourceDocumentId = plan.CountHeaderId.ToString(
                CultureInfo.InvariantCulture),
            SourceDocumentNo = header.CountNo,
            DocumentRevision = plan.AnchorDocumentRevision,
            PostingRole = "REVERSAL",
            EffectiveAt = plan.EffectiveAt,
            ReversesPostingId = plan.AnchorPostingId,
            Evidence = evidence,
            SourceSnapshotSchemaVersion = 2,
            FreezeScopes = lines.Select(l => new StockFreezeScope(
                null, l.WHCode, l.LocCode)).Distinct().ToArray()
        };
    }

    private static Guid RequestId(StockReplayCountSheetRemovalPlan plan)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{plan.CompanyCode.Trim().ToUpperInvariant()}|" +
            $"{plan.BranchCode.Trim().ToUpperInvariant()}|" +
            $"IV_STOCK_COUNT|{plan.CountHeaderId}|" +
            $"{plan.AnchorDocumentRevision}|REVERSAL"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
