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
        var policy = await QaPolicyService.LoadPublishedAsync(db, companyCode, cancellationToken);
        if (policy is null || !policy.NonGoodEnabled)
        {
            return null;
        }

        var qty = NonGoodQty(output);
        if (qty <= 0m)
        {
            return null;
        }

        var existing = await db.QaNonGoodAllocations
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && x.ProductionOutputId == output.Uid
                        && x.Status == QaReceiptAllocationStatuses.Posted)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            return IvQty.Round(existing.Sum(x => x.Qty)) == qty
                ? null
                : "This Daily Production already has a quality allocation that does not match the current scrap/reject/hold qty. Reload and try again.";
        }

        var remainingNeeded = qty;
        var grantIds = await db.QaNonGoodGrants
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && x.ProductionOutputId == output.Uid
                        && x.Status == QaReceiptGrantStatuses.Open)
            .OrderBy(x => x.Uid)
            .Select(x => x.Uid)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var grantId in grantIds)
        {
            if (remainingNeeded <= 0m)
            {
                break;
            }

            var grant = await LockGrantAsync(db, grantId, cancellationToken);
            if (grant is null || !string.Equals(grant.Status, QaReceiptGrantStatuses.Open, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var posted = IvQty.Round(await db.QaNonGoodAllocations
                .Where(x => x.GrantId == grant.Uid && x.Status == QaReceiptAllocationStatuses.Posted)
                .SumAsync(x => x.Qty, cancellationToken));
            var remaining = IvQty.Round(grant.AcceptedQty - posted);
            if (remaining <= 0m)
            {
                continue;
            }

            var take = Math.Min(remaining, remainingNeeded);
            db.QaNonGoodAllocations.Add(new QaNonGoodAllocation
            {
                GrantId = grant.Uid,
                CompanyCode = companyCode,
                BranchCode = branchCode,
                Qty = take,
                Status = QaReceiptAllocationStatuses.Posted,
                ProductionOutputId = output.Uid,
                DocumentRevision = output.DocumentRevision,
                PostedAtUtc = now
            });
            remainingNeeded = IvQty.Round(remainingNeeded - take);
        }

        return remainingNeeded > 0m
            ? $"Daily Production {output.DocumentNo} needs an approved quality inspection for scrap/reject/hold qty {qty:0.####}. Only {IvQty.Round(qty - remainingNeeded):0.####} is allowed. This is a counter, not a valued lot."
            : null;
    }

    public async Task<string?> RestoreDailyProductionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long productionOutputId,
        CancellationToken cancellationToken = default)
    {
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
