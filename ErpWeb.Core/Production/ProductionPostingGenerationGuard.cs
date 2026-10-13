using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>Checks that a rollback targets the sealed PRIMARY for this exact business generation.</summary>
public static class ProductionPostingGenerationGuard
{
    public static async Task<string?> ValidateRollbackTargetAsync(
        AppDbContext db,
        long postingId,
        string companyCode,
        string branchCode,
        string sourceModule,
        string sourceDocumentType,
        string sourceDocumentId,
        int documentRevision,
        CancellationToken cancellationToken)
    {
        var primary = db.Database.IsSqlServer()
            ? await db.StockPostings.FromSqlInterpolated(
                $"SELECT * FROM dbo.StockPosting WITH (UPDLOCK, HOLDLOCK) WHERE Id={postingId}")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.StockPostings.SingleOrDefaultAsync(x => x.Id == postingId, cancellationToken);

        if (primary is null
            || primary.SealedAtUtc is null
            || !string.Equals(primary.PostingRole, "PRIMARY", StringComparison.Ordinal)
            || !string.Equals(primary.SourceModule, sourceModule, StringComparison.Ordinal)
            || !string.Equals(primary.SourceDocumentType, sourceDocumentType, StringComparison.Ordinal)
            || !string.Equals(primary.SourceDocumentId, sourceDocumentId, StringComparison.Ordinal)
            || primary.DocumentRevision != documentRevision
            || !string.Equals(primary.CompanyCode, companyCode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(primary.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase))
        {
            return "The sealed PRIMARY posting does not match this document and revision.";
        }

        if (await db.StockPostings.AsNoTracking().AnyAsync(x =>
                x.ReversesPostingId == primary.Id
                && x.PostingRole == "REVERSAL"
                && x.SealedAtUtc != null,
                cancellationToken))
        {
            return "This PRIMARY posting already has a sealed REVERSAL.";
        }

        return null;
    }
}
