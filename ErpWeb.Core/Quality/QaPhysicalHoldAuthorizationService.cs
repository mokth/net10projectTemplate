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
        var physical = await PreparePhysicalAsync(db, cancellationToken);
        if (physical.Skip)
        {
            return null;
        }

        if (physical.Error is not null)
        {
            return physical.Error;
        }

        if (policy is null || !policy.PhysicalHoldEnabled)
        {
            return null;
        }

        if (!IsHoldableSource(entity.SourceType))
        {
            return null;
        }

        var requested = entity.Lines
            .Where(line => !entity.PhysicalHolds.Any(
                hold => hold.CaseLineId == line.Uid || ReferenceEquals(hold.CaseLine, line)))
            .Select(line => new { Line = line, Qty = ReservedQty(line) })
            .Where(x => x.Qty > 0m)
            .OrderBy(x => x.Line.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Line.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Line.LocationCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Line.LotNo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Line.ItemStatus, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Line.LineNo)
            .ToList();
        if (requested.Count == 0)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        foreach (var group in requested.GroupBy(
                     x => new
                     {
                         Item = (x.Line.ItemCode ?? string.Empty).Trim().ToUpperInvariant(),
                         Warehouse = (x.Line.WarehouseCode ?? string.Empty).Trim().ToUpperInvariant(),
                         Location = (x.Line.LocationCode ?? string.Empty).Trim().ToUpperInvariant(),
                         Lot = (x.Line.LotNo ?? string.Empty).Trim().ToUpperInvariant(),
                         Status = (x.Line.ItemStatus ?? string.Empty).Trim().ToUpperInvariant()
                     }))
        {
            var key = group.Key;
            var pile = await LockPileAsync(
                db, scope.CompanyCode, scope.BranchCode!, key.Item, key.Warehouse,
                key.Location, key.Lot, key.Status, cancellationToken);
            if (pile is null)
            {
                return $"The warehouse pile for quality line {group.First().Line.LineNo} is no longer available.";
            }

            foreach (var row in group)
            {
                if (!string.IsNullOrWhiteSpace(pile.StdUom)
                    && !string.Equals(row.Line.Uom, pile.StdUom, StringComparison.OrdinalIgnoreCase))
                {
                    return $"Quality hold UOM on line {row.Line.LineNo} does not match warehouse pile {pile.Id}.";
                }
            }

            var existing = await ActiveRemainingAsync(
                db, scope.CompanyCode, scope.BranchCode!, pile.Id, entity.Uid, cancellationToken);
            var total = IvQty.Round(existing + group.Sum(x => x.Qty));
            if (total > IvQty.Round(pile.StdQty))
            {
                return FriendlyShortage(pile.Id, total, IvQty.Round(pile.StdQty));
            }

            foreach (var row in group)
            {
                entity.PhysicalHolds.Add(new QaPhysicalHold
                {
                    CompanyCode = scope.CompanyCode,
                    BranchCode = scope.BranchCode!,
                    Case = entity,
                    CaseLine = row.Line,
                    BalLocId = pile.Id,
                    ItemCode = pile.ICode,
                    WarehouseCode = pile.WhCode,
                    LocationCode = pile.LocCode,
                    LotNo = pile.LotNo,
                    ItemStatus = pile.IStatus,
                    ReservedBaseQty = row.Qty,
                    Status = QaLogicalHoldStatuses.Active,
                    Cause = QaLogicalHoldCauses.KeepHold,
                    CreatedAtUtc = now
                });
            }
        }

        entity.EligibilityNote = QaEligibilityNotes.PhysicalReserved;
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
        var physical = await PreparePhysicalAsync(db, cancellationToken);
        if (physical.Skip || needs.Count == 0)
        {
            return null;
        }

        if (physical.Error is not null)
        {
            return physical.Error;
        }

        var policy = await QaPolicyService.LoadPublishedAsync(db, companyCode, cancellationToken);
        var gateOn = policy?.PhysicalHoldEnabled == true;
        var isTransfer = string.Equals(trxType, IvTrxTypes.StockTransfer, StringComparison.OrdinalIgnoreCase);
        var positive = needs.Where(x => x.Qty > 0m).ToList();

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

            if (gateOn
                && string.Equals(sample.ItemStatus, IvItemStatuses.QcHold, StringComparison.OrdinalIgnoreCase))
            {
                return FriendlyQcHold(sample.BalLocId, trxType);
            }

            var remaining = IvQty.Round(sample.OnHand - group.Sum(x => x.Qty));
            if (remaining < reserved)
            {
                return FriendlyShortage(sample.BalLocId, reserved, remaining);
            }
        }

        _ = batchNo;
        return null;
    }

    public async Task<string?> AssertRemainingCoversHoldsAsync(
        AppDbContext db, string companyCode, string branchCode, int balLocId,
        decimal remainingBaseQty, CancellationToken cancellationToken = default)
    {
        var physical = await PreparePhysicalAsync(db, cancellationToken);
        if (physical.Skip) return null;
        if (physical.Error is not null) return physical.Error;
        var reserved = await ActiveRemainingAsync(db, companyCode, branchCode, balLocId, cancellationToken);
        var remaining = IvQty.Round(remainingBaseQty);
        return remaining >= reserved ? null : FriendlyShortage(balLocId, reserved, remaining);
    }

    public async Task<string?> RestoreStockOutAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        CancellationToken cancellationToken = default)
    {
        var physical = await PreparePhysicalAsync(db, cancellationToken);
        if (physical.Skip)
        {
            return null;
        }

        if (physical.Error is not null)
        {
            return physical.Error;
        }

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

    private static async Task<QaFeatureDecision> PreparePhysicalAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var ready = await QaSchemaReadiness.EvaluateAsync(db, cancellationToken);
        return ready.ForFeature(ready.Physical);
    }

    private static Task<decimal> ActiveRemainingAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int balLocId,
        CancellationToken cancellationToken) =>
        ActiveRemainingAsync(db, companyCode, branchCode, balLocId, excludeCaseId: null, cancellationToken);

    private static async Task<decimal> ActiveRemainingAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int balLocId,
        long? excludeCaseId,
        CancellationToken cancellationToken)
    {
        var rows = db.QaPhysicalHolds
            .Include(x => x.Allocations)
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && x.BalLocId == balLocId
                        && x.Status == QaLogicalHoldStatuses.Active);
        if (excludeCaseId is long caseId)
        {
            rows = rows.Where(x => x.CaseId != caseId);
        }

        var holds = await rows.ToListAsync(cancellationToken);
        var reserved = 0m;
        foreach (var hold in holds)
        {
            var posted = IvQty.Round(hold.Allocations
                .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                .Sum(x => x.Qty));
            reserved += Math.Max(0m, IvQty.Round(hold.ReservedBaseQty - posted));
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
  AND (LotNo={lot} OR (LotNo IS NULL AND {lot}='')) AND IStatus={status}")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.IvBalLocs.SingleOrDefaultAsync(
                x => x.CompanyCode == companyCode
                     && x.BranchCode == branchCode
                     && x.ICode == item
                     && x.WhCode == warehouse
                     && x.LocCode == location
                     && (x.LotNo ?? string.Empty) == lot
                     && x.IStatus == status,
                cancellationToken);

    private static bool IsHoldableSource(string sourceType) =>
        string.Equals(sourceType, QaSourceTypes.PostedGrn, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sourceType, QaSourceTypes.InventoryStock, StringComparison.OrdinalIgnoreCase);

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
        $"Warehouse pile {balLocId} is QCHOLD. {trxType} cannot take it while physical QA hold is on without explicit QA lineage.";
}
