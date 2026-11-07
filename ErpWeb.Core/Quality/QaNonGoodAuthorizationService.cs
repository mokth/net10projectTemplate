using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Quality;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Quality;

public sealed class QaNonGoodAuthorizationService : IQaNonGoodAuthorization
{
    public async Task<string?> ClaimDailyProductionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        ProductionOutput output,
        CancellationToken cancellationToken = default)
    {
        var nonGood = await PrepareNonGoodAsync(db, cancellationToken);
        if (nonGood.Skip)
        {
            return null;
        }

        if (nonGood.Error is not null)
        {
            return nonGood.Error;
        }

        var policy = await QaPolicyService.LoadPublishedAsync(db, companyCode, cancellationToken);
        if (policy is null || !policy.NonGoodEnabled)
        {
            return null;
        }

        _ = branchCode;
        _ = output;
        return QaReadinessMessages.NonGoodFrozen;
    }

    public async Task<string?> RestoreDailyProductionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long productionOutputId,
        CancellationToken cancellationToken = default)
    {
        var nonGood = await PrepareNonGoodAsync(db, cancellationToken);
        if (nonGood.Skip)
        {
            return null;
        }

        if (nonGood.Error is not null)
        {
            return nonGood.Error;
        }

        var rows = await db.QaNonGoodAllocations
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && x.ProductionOutputId == productionOutputId
                        && x.Status == QaReceiptAllocationStatuses.Posted)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            row.Status = QaReceiptAllocationStatuses.Reversed;
            row.ReversedAtUtc = now;
        }

        return null;
    }

    private static async Task<QaFeatureDecision> PrepareNonGoodAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var ready = await QaSchemaReadiness.EvaluateAsync(db, cancellationToken);
        return ready.ForFeature(ready.NonGood);
    }

    private static async Task<QaNonGoodGrant?> LockGrantAsync(
        AppDbContext db,
        long id,
        CancellationToken cancellationToken) =>
        db.Database.IsSqlServer()
            ? await db.QaNonGoodGrants
                .FromSqlInterpolated($"SELECT * FROM dbo.QaNonGoodGrant WITH (UPDLOCK, HOLDLOCK) WHERE UID={id}")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.QaNonGoodGrants.SingleOrDefaultAsync(x => x.Uid == id, cancellationToken);

    internal static decimal NonGoodQty(ProductionOutput output) =>
        IvQty.Round(output.ScrapQty + output.RejectQty + output.HoldQty);
}
