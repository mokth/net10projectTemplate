using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Quality;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Quality;

public sealed class QaPhysicalHoldAuthorizationService : IQaPhysicalHoldAuthorization
{
    public async Task<string?> PlaceFromApprovedCaseAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        QaCase entity,
        QaPolicy? policy,
        CancellationToken cancellationToken = default)
    {
        if (policy is null || !policy.PhysicalHoldEnabled)
        {
            return null;
        }

        if (!IsHoldableSource(entity.SourceType))
        {
            return null;
        }

        if (entity.PhysicalHolds.Any(x =>
                string.Equals(x.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        foreach (var line in entity.Lines.OrderBy(x => x.LineNo))
        {
            var reserved = ReservedQty(line);
            if (reserved <= 0m)
            {
                continue;
            }

            var item = (line.ItemCode ?? string.Empty).Trim();
            var warehouse = (line.WarehouseCode ?? string.Empty).Trim();
            var location = (line.LocationCode ?? string.Empty).Trim();
            var lot = (line.LotNo ?? string.Empty).Trim();
            var status = string.IsNullOrWhiteSpace(line.ItemStatus)
                ? IvItemStatuses.Active
                : line.ItemStatus.Trim();
            if (string.IsNullOrWhiteSpace(item) || string.IsNullOrWhiteSpace(warehouse))
            {
                return $"Line {line.LineNo}: warehouse pile is required before approving a physical hold.";
            }

            var pile = await LockPileAsync(
                db, entity.CompanyCode, entity.BranchCode, item, warehouse, location, lot, status, cancellationToken);
            if (pile is null)
            {
                return $"Line {line.LineNo}: the warehouse pile for this quality hold was not found.";
            }

            var already = await ActiveReservedAsync(
                db, entity.CompanyCode, entity.BranchCode, pile.Id, excludeCaseId: entity.Uid, cancellationToken);
            var available = IvQty.Round(pile.StdQty - already);
            if (reserved > available)
            {
                return FriendlyShortage(pile.Id, reserved, available);
            }

            var cause = HoldQty(line) > 0m ? QaLogicalHoldCauses.KeepHold : "REJECT";

            entity.PhysicalHolds.Add(new QaPhysicalHold
            {
                CompanyCode = entity.CompanyCode,
                BranchCode = entity.BranchCode,
                Case = entity,
                CaseLine = line,
                BalLocId = pile.Id,
                ItemCode = item,
                WarehouseCode = warehouse,
                LocationCode = location,
                LotNo = lot,
                ItemStatus = status,
                ReservedBaseQty = reserved,
                Status = QaLogicalHoldStatuses.Active,
                Cause = cause,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        if (entity.PhysicalHolds.Count > 0)
        {
            entity.EligibilityNote = QaEligibilityNotes.PhysicalReserved;
        }

        return null;
    }

    public Task<string?> ReleaseCaseAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        QaCase entity,
        CancellationToken cancellationToken = default)
    {
        _ = db;
        _ = scope;
        _ = cancellationToken;
        var now = DateTime.UtcNow;
        var released = 0;
        foreach (var hold in entity.PhysicalHolds.Where(x =>
                     string.Equals(x.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase)))
        {
            hold.Status = QaLogicalHoldStatuses.Released;
            hold.ReleasedAtUtc = now;
            released++;
        }

        return Task.FromResult<string?>(released == 0 ? "There is no active physical quality hold to release." : null);
    }

    public async Task<string?> AssertAndClaimStockOutAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string trxType,
        int batchNo,
        IReadOnlyList<QaStockOutNeed> needs,
        CancellationToken cancellationToken = default)
    {
        var policy = await QaPolicyService.LoadPublishedAsync(db, companyCode, cancellationToken);
        if (policy is null || !policy.PhysicalHoldEnabled || needs.Count == 0)
        {
            return null;
        }

        var authorizedClaim = IsAuthorizedClaim(trxType);
        var isTransfer = string.Equals(trxType, IvTrxTypes.StockTransfer, StringComparison.OrdinalIgnoreCase);
        var positive = needs.Where(x => x.Qty > 0m).ToList();
        if (authorizedClaim)
        {
            foreach (var need in positive.OrderBy(x => x.BalLocId).ThenBy(x => x.BatchDetailId))
            {
                var isQcHold = string.Equals(need.ItemStatus, IvItemStatuses.QcHold, StringComparison.OrdinalIgnoreCase);
                var reserved = await ActiveRemainingAsync(
                    db, companyCode, branchCode, need.BalLocId, cancellationToken);
                if (isQcHold && reserved <= 0m)
                {
                    return FriendlyQcHold(need.BalLocId, trxType);
                }

                var claimed = await ClaimLineAsync(
                    db, companyCode, branchCode, trxType, batchNo, need, cancellationToken);
                if (claimed is not null)
                {
                    return claimed;
                }
            }

            return null;
        }

        foreach (var group in positive.GroupBy(x => x.BalLocId).OrderBy(x => x.Key))
        {
            var sample = group.First();
            var reserved = await ActiveRemainingAsync(
                db, companyCode, branchCode, sample.BalLocId, cancellationToken);
            if (isTransfer)
            {
                if (reserved > 0m)
                {
                    return $"Release or claim the quality hold on warehouse pile {sample.BalLocId} before transferring it. Transfer is not a quality release.";
                }

                continue;
            }

            if (string.Equals(sample.ItemStatus, IvItemStatuses.QcHold, StringComparison.OrdinalIgnoreCase))
            {
                return FriendlyQcHold(sample.BalLocId, trxType);
            }

            var remaining = IvQty.Round(sample.OnHand - group.Sum(x => x.Qty));
            if (remaining < reserved)
            {
                return FriendlyShortage(sample.BalLocId, reserved, remaining);
            }
        }

        return null;
    }

    public async Task<string?> RestoreStockOutAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.QaPhysicalAllocations
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && x.BatchNo == batchNo
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

    private async Task<string?> ClaimLineAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string trxType,
        int batchNo,
        QaStockOutNeed need,
        CancellationToken cancellationToken)
    {
        var existing = await db.QaPhysicalAllocations
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && x.BatchNo == batchNo
                        && x.BatchDetailId == need.BatchDetailId
                        && x.Status == QaReceiptAllocationStatuses.Posted)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            return IvQty.Round(existing.Sum(x => x.Qty)) == need.Qty
                ? null
                : "This stock-out already has a quality allocation that does not match the current lines. Reload and try again.";
        }

        var remainingNeeded = need.Qty;
        var holdIds = await db.QaPhysicalHolds
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && x.BalLocId == need.BalLocId
                        && x.Status == QaLogicalHoldStatuses.Active)
            .OrderBy(x => x.Uid)
            .Select(x => x.Uid)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var holdId in holdIds)
        {
            if (remainingNeeded <= 0m)
            {
                break;
            }

            var hold = await LockHoldAsync(db, holdId, cancellationToken);
            if (hold is null || !string.Equals(hold.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var posted = IvQty.Round(await db.QaPhysicalAllocations
                .Where(x => x.HoldId == hold.Uid && x.Status == QaReceiptAllocationStatuses.Posted)
                .SumAsync(x => x.Qty, cancellationToken));
            var remaining = IvQty.Round(hold.ReservedBaseQty - posted);
            if (remaining <= 0m)
            {
                continue;
            }

            var take = Math.Min(remaining, remainingNeeded);
            db.QaPhysicalAllocations.Add(new QaPhysicalAllocation
            {
                HoldId = hold.Uid,
                CompanyCode = companyCode,
                BranchCode = branchCode,
                Qty = take,
                Status = QaReceiptAllocationStatuses.Posted,
                TrxType = trxType,
                BatchNo = batchNo,
                BatchDetailId = need.BatchDetailId,
                PostedAtUtc = now
            });
            remainingNeeded = IvQty.Round(remainingNeeded - take);
        }

        return remainingNeeded > 0m
            ? FriendlyShortage(need.BalLocId, need.Qty, IvQty.Round(need.Qty - remainingNeeded))
            : null;
    }

    private static async Task<decimal> ActiveReservedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int balLocId,
        long? excludeCaseId,
        CancellationToken cancellationToken)
    {
        var rows = db.QaPhysicalHolds.Where(x =>
            x.CompanyCode == companyCode
            && x.BranchCode == branchCode
            && x.BalLocId == balLocId
            && x.Status == QaLogicalHoldStatuses.Active);
        if (excludeCaseId is long caseId)
        {
            rows = rows.Where(x => x.CaseId != caseId);
        }

        return IvQty.Round(await rows.SumAsync(x => x.ReservedBaseQty, cancellationToken));
    }

    private static async Task<decimal> ActiveRemainingAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int balLocId,
        CancellationToken cancellationToken)
    {
        var holds = await db.QaPhysicalHolds
            .Include(x => x.Allocations)
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && x.BalLocId == balLocId
                        && x.Status == QaLogicalHoldStatuses.Active)
            .ToListAsync(cancellationToken);
        var reserved = 0m;
        foreach (var hold in holds)
        {
            var posted = IvQty.Round(hold.Allocations
                .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                .Sum(x => x.Qty));
            reserved += IvQty.Round(hold.ReservedBaseQty - posted);
        }

        return IvQty.Round(reserved);
    }

    private static async Task<IvBalLoc?> LockPileAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string item,
        string warehouse,
        string location,
        string lot,
        string status,
        CancellationToken cancellationToken) =>
        db.Database.IsSqlServer()
            ? await db.IvBalLocs
                .FromSqlInterpolated($@"
SELECT * FROM dbo.IvBalLoc WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode={companyCode} AND BranchCode={branchCode}
  AND ICode={item} AND WHCode={warehouse} AND LocCode={location}
  AND LotNo={lot} AND IStatus={status}")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.IvBalLocs.SingleOrDefaultAsync(
                x => x.CompanyCode == companyCode
                     && x.BranchCode == branchCode
                     && x.ICode == item
                     && x.WhCode == warehouse
                     && x.LocCode == location
                     && x.LotNo == lot
                     && x.IStatus == status,
                cancellationToken);

    private static async Task<QaPhysicalHold?> LockHoldAsync(
        AppDbContext db,
        long id,
        CancellationToken cancellationToken) =>
        db.Database.IsSqlServer()
            ? await db.QaPhysicalHolds
                .FromSqlInterpolated($"SELECT * FROM dbo.QaPhysicalHold WITH (UPDLOCK, HOLDLOCK) WHERE UID={id}")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.QaPhysicalHolds.SingleOrDefaultAsync(x => x.Uid == id, cancellationToken);

    private static bool IsHoldableSource(string sourceType) =>
        string.Equals(sourceType, QaSourceTypes.PostedGrn, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sourceType, QaSourceTypes.InventoryStock, StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthorizedClaim(string trxType) =>
        string.Equals(trxType, IvTrxTypes.Scrap, StringComparison.OrdinalIgnoreCase)
        || string.Equals(trxType, IvTrxTypes.VendorReturn, StringComparison.OrdinalIgnoreCase);

    private static decimal ReservedQty(QaCaseLine line) =>
        IvQty.Round(HoldQty(line) + RejectQty(line));

    private static decimal HoldQty(QaCaseLine line) =>
        IvQty.Round(line.Dispositions
            .Where(x => string.Equals(x.Kind, QaDispositionKinds.Hold, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Qty));

    private static decimal RejectQty(QaCaseLine line) =>
        IvQty.Round(line.Dispositions
            .Where(x => string.Equals(x.Kind, QaDispositionKinds.Reject, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Qty));

    internal static string FriendlyShortage(int balLocId, decimal reserved, decimal available) =>
        $"Quality holds {reserved:0.####} on warehouse pile {balLocId}. Only {available:0.####} is available. This is a reservation, not a QCHOLD status conversion.";

    internal static string FriendlyQcHold(int balLocId, string trxType) =>
        $"Warehouse pile {balLocId} is QCHOLD. {trxType} cannot take it while physical QA hold is on, except an approved quality scrap or vendor return.";
}
