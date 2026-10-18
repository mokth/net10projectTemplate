using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockCountCostApprovalRequest(
    int StockCountLineId,
    string CostEvidenceType,
    decimal UnitCost,
    string Reason);

/// <summary>
/// Prepares one immutable basis for every positive count observation before the
/// anchor command is frozen. The returned entities are not persisted here; the
/// count root writes them with the physical and valuation generations.
/// </summary>
public static class StockCountCostBasisProposalFactory
{
    public static async Task<IReadOnlyList<StockCountCostBasis>> CreateAsync(
        AppDbContext db,
        IAccessRightService accessRights,
        string companyCode,
        string branchCode,
        int countId,
        IReadOnlyCollection<StockCountCostApprovalRequest> approvals,
        string approvedBy,
        DateTime approvedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(accessRights);
        ArgumentNullException.ThrowIfNull(approvals);
        if (string.IsNullOrWhiteSpace(companyCode) || string.IsNullOrWhiteSpace(branchCode)
            || countId <= 0 || approvedAtUtc.Kind != DateTimeKind.Utc)
            throw Invalid("A count tenant, document and UTC approval time are required.");
        var company = companyCode.Trim();
        var branch = branchCode.Trim();
        var header = await db.IvStockCountHdrs.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == countId && h.CompanyCode == company
                && h.BranchCode == branch, cancellationToken);
        if (header is null || header.Status is not
                (IvStockCountStatuses.Counted or IvStockCountStatuses.RolledBack)
            || header.CountDate != header.CountDate.Date)
            throw Invalid($"Count {countId} is not an eligible frozen sheet.");

        var lines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.StockCountId == countId && l.PhysicalQty > 0m)
            .OrderBy(l => l.Id).ToArrayAsync(cancellationToken);
        if (lines.Select(l => l.Id).Distinct().Count() != lines.Length
            || lines.Any(l => string.IsNullOrWhiteSpace(l.ICode)))
            throw Invalid($"Count {countId} has an invalid positive line.");
        var byLine = lines.ToDictionary(l => l.Id);
        var lineIds = lines.Select(l => l.Id).ToArray();
        var requested = approvals.ToArray();
        if (header.Status == IvStockCountStatuses.RolledBack)
        {
            if (requested.Length != 0)
                throw Invalid("A repost reuses the count's immutable approved cost bases.");
            var countedIds = await db.IvStockCountLines.AsNoTracking()
                .Where(l => l.StockCountId == countId && l.PhysicalQty != null)
                .Select(l => l.Id).ToArrayAsync(cancellationToken);
            var active = await StockCountGenerationResolver.ResolveActiveAsync(db,
                company, branch, countedIds, cancellationToken);
            if (active.Count != countedIds.Length
                || active.Values.Any(g => !g.IsRemoval))
                throw Invalid($"Count {countId} has no complete removed generation for repost.");
            var existingBases = await db.StockCountCostBases.AsNoTracking()
                .Where(b => countedIds.Contains(b.StockCountLineId))
                .OrderBy(b => b.StockCountLineId)
                .ToArrayAsync(cancellationToken);
            if (existingBases.Length != lineIds.Length
                || !existingBases.Select(b => b.StockCountLineId)
                    .SequenceEqual(lineIds.Order())
                || existingBases.Any(b => b.CompanyCode != company
                    || b.BranchCode != branch
                    || !StockCountAnchorCommandFactory.ValidBasis(b)))
                throw Invalid($"Count {countId} has incomplete immutable repost cost bases.");
            return existingBases;
        }
        if (requested.Select(a => a.StockCountLineId).Distinct().Count() != requested.Length
            || requested.Any(a => !byLine.ContainsKey(a.StockCountLineId)))
            throw Invalid($"Count {countId} has an approval outside its positive lines.");
        if (requested.Length > 0
            && !await accessRights.CanAsync(MenuCodes.InventoryStockCount,
                PermissionCodes.PriceOverride, cancellationToken))
            throw Invalid("Inventory Stock Count price override permission is required for explicit cost approval.");

        var actor = approvedBy?.Trim() ?? string.Empty;
        if (requested.Length > 0 && (actor.Length is 0 or > 100))
            throw Invalid("A valid cost approver is required.");
        var method = await new StockCostMethodResolver().ResolveAsync(db,
            company, branch, StockReplayEventKey.CountEndOfDay(header.CountDate),
            cancellationToken);
        if (method == StockCostMethods.Standard && requested.Length > 0)
            throw Invalid("Standard count cost must use the approved event-date revision.");
        var existing = await db.StockCountCostBases.AsNoTracking()
            .Where(b => lineIds.Contains(b.StockCountLineId))
            .AnyAsync(cancellationToken);
        if (existing)
            throw Invalid($"Count {countId} already has immutable cost bases.");

        var approvalsByLine = requested.ToDictionary(a => a.StockCountLineId);
        var effectiveAt = StockReplayEventKey.CountEndOfDay(header.CountDate);
        var watermark = await db.StockPostingBranchSequences.AsNoTracking()
            .Where(s => s.CompanyCode == company && s.BranchCode == branch)
            .Select(s => (long?)s.LastSequence)
            .SingleOrDefaultAsync(cancellationToken) ?? 0L;
        var bases = new List<StockCountCostBasis>(lines.Length);
        foreach (var line in lines)
        {
            var basis = new StockCountCostBasis
            {
                CompanyCode = company, BranchCode = branch,
                StockCountLineId = line.Id, CreatedAtUtc = approvedAtUtc
            };
            if (approvalsByLine.TryGetValue(line.Id, out var approval))
            {
                var kind = InventoryCostEvidenceTypes.Normalize(approval.CostEvidenceType);
                var reason = approval.Reason?.Trim();
                if (kind is not (InventoryCostEvidenceTypes.ManualApproved
                        or InventoryCostEvidenceTypes.ZeroCostApproved)
                    || reason is null || reason.Length is 0 or > 250
                    || approval.UnitCost < 0m || approval.UnitCost >= 10000000000000m
                    || StockLedgerPrecision.Money(approval.UnitCost) != approval.UnitCost
                    || kind == InventoryCostEvidenceTypes.ManualApproved && approval.UnitCost == 0m
                    || kind == InventoryCostEvidenceTypes.ZeroCostApproved && approval.UnitCost != 0m)
                    throw Invalid($"Count {countId} line {line.LineNumber} has invalid approved cost evidence.");
                basis.CostBasisKind = kind;
                basis.CostEvidenceType = kind;
                basis.FrozenFallbackUnitCost = approval.UnitCost;
                basis.CostOverrideReason = reason;
                basis.CostApprovedBy = actor;
                basis.CostApprovedAtUtc = approvedAtUtc;
            }
            else if (method == StockCostMethods.MovingAverage)
            {
                var captured = await StockCountLedgerCostEvidence.CaptureWacAsync(
                    db, company, branch, line.Id, line.ICode, effectiveAt,
                    watermark, cancellationToken);
                basis.CostBasisKind = StockCountCostBasisKinds.LedgerWac;
                basis.FrozenFallbackUnitCost = captured.UnitCost;
                basis.CostBasisWatermark = watermark;
                basis.CostBasisHash = captured.Hash;
            }
            else if (method == StockCostMethods.Standard)
            {
                var revision = await new ItemStandardCostResolver().ResolveAsync(
                    db, company, branch, line.ICode, effectiveAt, cancellationToken);
                basis.CostBasisKind = StockCountCostBasisKinds.StandardRevision;
                basis.FrozenFallbackUnitCost = revision.TotalStandardCost;
                basis.StandardCostRevisionId = revision.Id;
            }
            else
            {
                throw Invalid($"Count {countId} line {line.LineNumber} requires verified FIFO layer cost evidence or explicit approval.");
            }
            if (!StockCountAnchorCommandFactory.ValidBasis(basis))
                throw Invalid($"Count {countId} line {line.LineNumber} has invalid frozen cost basis.");
            bases.Add(basis);
        }
        return bases;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.ValuationRequired, message));
}
