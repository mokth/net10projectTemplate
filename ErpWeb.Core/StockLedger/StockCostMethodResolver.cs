using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public interface IStockCostMethodResolver
{
    /// <summary>
    /// Resolves the one financial method covering the posting's business date. Resolution is
    /// deliberately fail-closed: an active V2 branch without an explicit policy cannot post.
    /// </summary>
    Task<string> ResolveAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        DateTime effectiveAt,
        CancellationToken cancellationToken = default);
}

public sealed class StockCostMethodResolver : IStockCostMethodResolver
{
    public async Task<string> ResolveAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        DateTime effectiveAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var company = RequiredScope(companyCode, nameof(companyCode));
        var branch = RequiredScope(branchCode, nameof(branchCode));
        var businessDate = effectiveAt.Date;

        var revisions = await db.StockCostPolicyRevisions.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.BranchCode == branch
                        && x.Status == StockCostPolicyStatuses.Active
                        && x.EffectiveFrom <= businessDate
                        && (x.EffectiveTo == null || x.EffectiveTo > businessDate))
            .OrderByDescending(x => x.EffectiveFrom)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        if (revisions.Count == 0)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.CostPolicyMissing,
                $"No active stock costing policy covers {businessDate:yyyy-MM-dd} for {company}/{branch}."));

        if (revisions.Count > 1)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.CostPolicyInvalid,
                $"Overlapping active stock costing policies cover {businessDate:yyyy-MM-dd} for {company}/{branch}."));

        var method = revisions[0].CostMethod.Trim().ToUpperInvariant();
        if (!StockCostMethods.All.Contains(method))
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.CostPolicyInvalid,
                $"Stock costing policy {revisions[0].Id} has unsupported method '{revisions[0].CostMethod}'."));

        return method;
    }

    private static string RequiredScope(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty tenant scope is required.", name);
        return value.Trim();
    }
}
