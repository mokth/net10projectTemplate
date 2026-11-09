using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Quality;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Quality;

public sealed class QaLogicalHoldAuthorizationService : IQaLogicalHoldAuthorization
{
    public async Task<string?> PlaceFromApprovedCaseAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        QaCase entity,
        QaPolicy? policy,
        CancellationToken cancellationToken = default)
    {
        var logical = await PrepareLogicalAsync(db, cancellationToken);
        if (logical.Skip)
        {
            return null;
        }

        if (logical.Error is not null)
        {
            return logical.Error;
        }

        if (policy is null || !policy.LogicalHoldEnabled)
        {
            return null;
        }

        if (!IsHoldableSource(entity.SourceType)
            || entity.ProductionBalLotId is not long lotId)
        {
            return null;
        }

        var pool = await LockPoolAsync(db, lotId, cancellationToken);
        if (pool is null
            || !string.Equals(pool.CompanyCode, scope.CompanyCode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(pool.BranchCode, scope.BranchCode, StringComparison.OrdinalIgnoreCase))
        {
            return "The production lot is no longer available for this quality hold.";
        }

        var requested = new List<(QaCaseLine Line, decimal BaseQty)>();
        foreach (var line in entity.Lines.OrderBy(x => x.LineNo))
        {
            if (entity.LogicalHolds.Any(x => x.CaseLineId == line.Uid || ReferenceEquals(x.CaseLine, line)))
            {
                continue;
            }

            var qty = HoldQty(line);
            if (qty <= 0m)
            {
                continue;
            }

            var baseQty = ToBaseQty(line, pool, qty);
            if (baseQty is null)
            {
                return $"Quality hold UOM on line {line.LineNo} does not match production lot {pool.Uid}.";
            }

            requested.Add((line, baseQty.Value));
        }

        if (requested.Count == 0)
        {
            return null;
        }

        var existing = await ActiveReservedAsync(
            db, scope.CompanyCode, scope.BranchCode!, pool.Uid, entity.Uid, cancellationToken);
        var total = IvQty.Round(existing + requested.Sum(x => x.BaseQty));
        if (total > IvQty.Round(pool.BaseQty))
        {
            return FriendlyShortage(pool.Uid, total, IvQty.Round(pool.BaseQty));
        }

        var now = DateTime.UtcNow;
        foreach (var (line, baseQty) in requested)
        {
            entity.LogicalHolds.Add(new QaLogicalHold
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode!,
                Case = entity,
                CaseLine = line,
                ProductionBalLotId = pool.Uid,
                ReservedBaseQty = baseQty,
                Status = QaLogicalHoldStatuses.Active,
                Cause = QaLogicalHoldCauses.KeepHold,
                CreatedAtUtc = now
            });
        }

        entity.EligibilityNote = QaEligibilityNotes.WipReserved;
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
        foreach (var hold in entity.LogicalHolds.Where(x =>
                     string.Equals(x.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase)))
        {
            hold.Status = QaLogicalHoldStatuses.Released;
            hold.Cause = QaLogicalHoldCauses.Release;
            hold.ReleasedAtUtc = now;
            released++;
        }

        if (released == 0)
        {
            return Task.FromResult<string?>("There is no active quality hold to release.");
        }

        entity.EligibilityNote = QaEligibilityNotes.WipLot;
        return Task.FromResult<string?>(null);
    }

    public async Task<string?> AssertRemainingCoversHoldsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long productionBalLotId,
        decimal remainingBaseQty,
        CancellationToken cancellationToken = default)
    {
        var logical = await PrepareLogicalAsync(db, cancellationToken);
        if (logical.Skip)
        {
            return null;
        }

        if (logical.Error is not null)
        {
            return logical.Error;
        }

        var reserved = await ActiveReservedAsync(
            db, companyCode, branchCode, productionBalLotId, excludeCaseId: null, cancellationToken);
        if (reserved <= 0m)
        {
            return null;
        }

        var remaining = IvQty.Round(remainingBaseQty);
        if (remaining >= reserved)
        {
            return null;
        }

        return FriendlyShortage(productionBalLotId, reserved, IvQty.Round(remaining));
    }

    private static async Task<QaFeatureDecision> PrepareLogicalAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var ready = await QaSchemaReadiness.EvaluateAsync(db, cancellationToken);
        return ready.ForFeature(ready.Logical);
    }

    private static async Task<decimal> ActiveReservedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long productionBalLotId,
        long? excludeCaseId,
        CancellationToken cancellationToken)
    {
        var rows = db.QaLogicalHolds.Where(x =>
            x.CompanyCode == companyCode
            && x.BranchCode == branchCode
            && x.ProductionBalLotId == productionBalLotId
            && x.Status == QaLogicalHoldStatuses.Active);
        if (excludeCaseId is long caseId)
        {
            rows = rows.Where(x => x.CaseId != caseId);
        }

        var reserved = await rows.SumAsync(x => x.ReservedBaseQty, cancellationToken);
        return IvQty.Round(reserved);
    }

    private static async Task<ProductionBalLot?> LockPoolAsync(
        AppDbContext db,
        long id,
        CancellationToken cancellationToken) =>
        db.Database.IsSqlServer()
            ? await db.ProductionBalLots
                .FromSqlInterpolated($"SELECT * FROM dbo.PrProductionBalLot WITH (UPDLOCK, HOLDLOCK) WHERE UID={id}")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.ProductionBalLots.SingleOrDefaultAsync(x => x.Uid == id, cancellationToken);

    private static bool IsHoldableSource(string sourceType) =>
        string.Equals(sourceType, QaSourceTypes.ProductionWipLot, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sourceType, QaSourceTypes.FgStagingLot, StringComparison.OrdinalIgnoreCase);

    private static decimal HoldQty(QaCaseLine line) =>
        IvQty.Round(line.Dispositions
            .Where(x => string.Equals(x.Kind, QaDispositionKinds.Hold, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Qty));

    private static decimal? ToBaseQty(QaCaseLine line, ProductionBalLot pool, decimal qty)
    {
        if (string.Equals(line.Uom, pool.BaseUom, StringComparison.OrdinalIgnoreCase))
        {
            return IvQty.Round(qty);
        }

        if (string.Equals(line.Uom, pool.Uom, StringComparison.OrdinalIgnoreCase)
            && pool.ConversionFactorToBase > 0m)
        {
            return IvQty.Round(qty * pool.ConversionFactorToBase);
        }

        return null;
    }

    internal static string FriendlyShortage(long lotId, decimal reserved, decimal available) =>
        $"Quality hold reserves {reserved:0.####} of production lot {lotId}. Only {available:0.####} is available. This is a reservation, not a warehouse transfer.";
}
