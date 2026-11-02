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
        if (policy is null || !policy.LogicalHoldEnabled)
        {
            return null;
        }

        if (!IsHoldableSource(entity.SourceType))
        {
            return null;
        }

        var holdQty = HoldQty(entity);
        if (holdQty <= 0m)
        {
            return null;
        }

        if (entity.ProductionBalLotId is not > 0)
        {
            return "Select a production lot before approving a hold.";
        }

        if (entity.LogicalHolds.Any(x =>
                string.Equals(x.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var lot = await LockPoolAsync(db, entity.ProductionBalLotId.Value, cancellationToken);
        if (lot is null
            || !string.Equals(lot.CompanyCode, entity.CompanyCode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(lot.BranchCode, entity.BranchCode, StringComparison.OrdinalIgnoreCase))
        {
            return "The production lot for this quality hold was not found.";
        }

        var alreadyReserved = await ActiveReservedAsync(
            db, entity.CompanyCode, entity.BranchCode, lot.Uid, excludeCaseId: entity.Uid, cancellationToken);
        var available = IvQty.Round(lot.BaseQty - alreadyReserved);
        if (holdQty > available)
        {
            return FriendlyShortage(lot.Uid, holdQty, available);
        }

        foreach (var line in entity.Lines.OrderBy(x => x.LineNo))
        {
            var lineHold = IvQty.Round(line.Dispositions
                .Where(x => string.Equals(x.Kind, QaDispositionKinds.Hold, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Qty));
            if (lineHold <= 0m)
            {
                continue;
            }

            entity.LogicalHolds.Add(new QaLogicalHold
            {
                CompanyCode = entity.CompanyCode,
                BranchCode = entity.BranchCode,
                Case = entity,
                CaseLine = line,
                ProductionBalLotId = lot.Uid,
                ReservedBaseQty = lineHold,
                Status = QaLogicalHoldStatuses.Active,
                Cause = QaLogicalHoldCauses.KeepHold,
                CreatedAtUtc = DateTime.UtcNow
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
        var policy = await QaPolicyService.LoadPublishedAsync(db, companyCode, cancellationToken);
        if (policy is null || !policy.LogicalHoldEnabled)
        {
            return null;
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

    private static decimal HoldQty(QaCase entity) =>
        IvQty.Round(entity.Lines
            .SelectMany(x => x.Dispositions)
            .Where(x => string.Equals(x.Kind, QaDispositionKinds.Hold, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Qty));

    internal static string FriendlyShortage(long lotId, decimal reserved, decimal available) =>
        $"Quality hold reserves {reserved:0.####} of production lot {lotId}. Only {available:0.####} is available. This is a reservation, not a warehouse transfer.";
}
