using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Quality;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Quality;

public sealed class QaFgAuthorizationService : IQaFgAuthorization
{
    public const string FgStagingStage = "FG_STAGING";

    public async Task<string?> ClaimFinishedGoodAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        ProductionFinishedGoodReceipt receipt,
        IReadOnlyDictionary<long, ProductionBalLot> pools,
        CancellationToken cancellationToken = default)
    {
        var policy = await QaPolicyService.LoadPublishedAsync(db, companyCode, cancellationToken);
        if (policy is null || !policy.FinalFgInspectionEnabled)
        {
            return null;
        }

        var required = receipt.Sources
            .OrderBy(x => x.Id)
            .Select(source => new FgNeed(source, pools[source.ProductionBalLotId], IvQty.Round(source.BaseQty)))
            .Where(x => x.Qty > 0m)
            .ToList();
        if (required.Count == 0)
        {
            return null;
        }

        var existing = await db.QaFgAllocations
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && x.BatchId == receipt.BatchId
                        && x.Status == QaReceiptAllocationStatuses.Posted)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            var existingBySource = existing.GroupBy(x => x.SourceId)
                .ToDictionary(g => g.Key, g => IvQty.Round(g.Sum(x => x.Qty)));
            foreach (var need in required)
            {
                if (!existingBySource.TryGetValue(need.Source.Id, out var posted)
                    || posted != need.Qty)
                {
                    return "This finished-good receipt already has a quality allocation that does not match the current lines. Reload and try again.";
                }
            }

            return null;
        }

        foreach (var need in required)
        {
            var claimed = await ClaimLineAsync(db, companyCode, branchCode, receipt, need, cancellationToken);
            if (claimed is not null)
            {
                return claimed;
            }
        }

        return null;
    }

    public async Task<string?> RestoreFinishedGoodAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchId,
        CancellationToken cancellationToken = default)
    {
        var posted = await db.QaFgAllocations
            .Include(x => x.Grant)
            .ThenInclude(x => x.Case)
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && x.BatchId == batchId
                        && x.Status == QaReceiptAllocationStatuses.Posted)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        if (posted.Count == 0)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var cases = new Dictionary<long, QaCase>();
        foreach (var row in posted)
        {
            row.Status = QaReceiptAllocationStatuses.Reversed;
            row.ReversedAtUtc = now;
            cases[row.Grant.Case.Uid] = row.Grant.Case;
        }

        foreach (var qaCase in cases.Values.OrderBy(x => x.Uid))
        {
            var reopened = string.Equals(qaCase.Status, QaCaseStatuses.Closed, StringComparison.OrdinalIgnoreCase);
            if (reopened)
            {
                qaCase.Status = QaCaseStatuses.Approved;
                qaCase.ModifiedDate = now;
                qaCase.ModifiedBy = "SYSTEM";
            }

            db.QaAuditEvents.Add(new QaAuditEvent
            {
                CaseId = qaCase.Uid,
                EventType = reopened ? QaAuditEvents.ReopenedForReissue : QaAuditEvents.AllocationReversed,
                AtUtc = now,
                ByUser = "SYSTEM",
                Payload = reopened
                    ? $"Finished-good receipt {batchId} was rolled back. Accepted quantity is available again."
                    : $"Finished-good receipt {batchId} quality allocation reversed."
            });
        }

        return null;
    }

    public async Task<string?> AssertWipAdjustmentAllowedAsync(
        AppDbContext db,
        string companyCode,
        IEnumerable<ProductionBalLot> pools,
        CancellationToken cancellationToken = default)
    {
        var policy = await QaPolicyService.LoadPublishedAsync(db, companyCode, cancellationToken);
        if (policy is null || !policy.FinalFgInspectionEnabled)
        {
            return null;
        }

        var blocked = pools
            .Where(x => string.Equals(x.BalanceStage, FgStagingStage, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Uid)
            .FirstOrDefault();
        if (blocked is null)
        {
            return null;
        }

        return "Finished-goods inspection is on. Production stock adjustment cannot consume FG staging. "
            + "Inspect and approve the lot, then post a finished-good receipt for the accepted quantity.";
    }

    private async Task<string?> ClaimLineAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        ProductionFinishedGoodReceipt receipt,
        FgNeed need,
        CancellationToken cancellationToken)
    {
        var itemCode = (need.Pool.ItemCode ?? string.Empty).Trim();
        var uom = (need.Source.BaseUom ?? need.Pool.BaseUom ?? string.Empty).Trim();
        var lotNo = (need.Pool.PhysicalLotNo ?? need.Pool.LotNo ?? string.Empty).Trim();
        var candidates = await db.QaFgGrants
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && x.Status == QaReceiptGrantStatuses.Open
                        && x.ProductionBalLotId == need.Source.ProductionBalLotId
                        && x.ItemCode == itemCode
                        && x.Uom == uom)
            .OrderBy(x => x.Uid)
            .Select(x => x.Uid)
            .ToListAsync(cancellationToken);

        var remainingNeeded = need.Qty;
        var now = DateTime.UtcNow;
        foreach (var grantId in candidates)
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

            if (!string.IsNullOrWhiteSpace(grant.LotNo)
                && !string.Equals(grant.LotNo.Trim(), lotNo, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var posted = IvQty.Round(grant.Allocations
                .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                .Sum(x => x.Qty));
            var remaining = IvQty.Round(grant.AcceptedQty - posted);
            if (remaining <= 0m)
            {
                continue;
            }

            var take = remaining >= remainingNeeded ? remainingNeeded : remaining;
            grant.Allocations.Add(new QaFgAllocation
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                Qty = take,
                Status = QaReceiptAllocationStatuses.Posted,
                BatchId = receipt.BatchId,
                SourceId = need.Source.Id,
                DocumentRevision = need.Source.DocumentRevision,
                PostedAtUtc = now
            });
            remainingNeeded = IvQty.Round(remainingNeeded - take);
        }

        if (remainingNeeded > 0m)
        {
            return FriendlyShortage(need.Pool.WorkOrderNo, need.Source.ProductionBalLotId, need.Qty, remainingNeeded);
        }

        return null;
    }

    private static async Task<QaFgGrant?> LockGrantAsync(
        AppDbContext db, long id, CancellationToken cancellationToken)
    {
        QaFgGrant? grant;
        if (db.Database.IsSqlServer())
        {
            grant = await db.QaFgGrants
                .FromSqlInterpolated($"SELECT * FROM dbo.QaFgGrant WITH (UPDLOCK, HOLDLOCK) WHERE UID = {id}")
                .AsTracking()
                .SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            grant = await db.QaFgGrants.FirstOrDefaultAsync(x => x.Uid == id, cancellationToken);
        }

        if (grant is not null)
        {
            await db.Entry(grant).Collection(x => x.Allocations).LoadAsync(cancellationToken);
        }

        return grant;
    }

    private static string FriendlyShortage(string? workOrderNo, long poolId, decimal requested, decimal shortfall) =>
        $"This finished-good receipt needs an approved quality inspection before it can be posted. "
        + $"Work order {workOrderNo ?? "—"} lot {poolId} is short {shortfall:0.####} of the {requested:0.####} requested. "
        + "Inspect and approve the staging lot, then post only the accepted quantity.";

    private readonly record struct FgNeed(ProductionFinishedGoodSource Source, ProductionBalLot Pool, decimal Qty);
}
