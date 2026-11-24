using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Costing;

public sealed partial class CostingDiagnosticService
{
    /// <summary>
    /// Read-only branch preflight mirroring scripts/preflight-open-period-backdated-replay.sql.
    /// Any finding blocks enabling OpenPeriodBackdatedReplayEnabled for the branch.
    /// Deliberately never guesses the posting order of a legacy zero-variance count.
    /// </summary>
    public async Task<BranchReplayPreflightReport> CheckReplayReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.InventoryCostingCenter, PermissionCodes.Access, cancellationToken))
            return new BranchReplayPreflightReport { Denied = true, Error = "Not authorized." };
        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return new BranchReplayPreflightReport { Error = "Select a company and branch." };

        var company = scope.CompanyCode;
        var branch = scope.BranchCode;
        var findings = new List<BranchReplayPreflightFinding>();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var activeEpochIds = await db.StockLedgerEpochs.AsNoTracking()
            .Where(e => e.CompanyCode == company && e.BranchCode == branch && e.Status == StockLedgerEpochStatuses.Active)
            .Select(e => e.Id).ToArrayAsync(cancellationToken);
        if (activeEpochIds.Length != 1)
        {
            findings.Add(new(ReplayPreflightFindingCodes.NoActiveEpoch, null, null,
                $"Expected one active V2 stock ledger epoch for {company}/{branch}; found {activeEpochIds.Length}."));
        }
        else
        {
            var epochId = activeEpochIds[0];
            var unsealed = await db.StockPostings.AsNoTracking()
                .Where(p => p.CompanyCode == company && p.BranchCode == branch
                    && p.LedgerEpochId == epochId && p.SealedAtUtc == null)
                .OrderBy(p => p.Id)
                .Select(p => p.Id).ToArrayAsync(cancellationToken);
            foreach (var id in unsealed)
                findings.Add(new(ReplayPreflightFindingCodes.UnsealedStockPosting, null, null,
                    $"StockPosting {id} is unsealed."));
        }

        var closedPeriods = await db.IvPeriodCloseHdrs.AsNoTracking()
            .Where(pc => pc.CompanyCode == company && pc.BranchCode == branch
                && pc.Status == IvPeriodCloseStatuses.Closed)
            .Select(pc => new { pc.PeriodFrom, pc.PeriodTo })
            .ToArrayAsync(cancellationToken);

        var counts = await db.IvStockCountHdrs.AsNoTracking()
            .Where(h => h.CompanyCode == company && h.BranchCode == branch
                && (h.Status == IvStockCountStatuses.Posted || h.Status == IvStockCountStatuses.RolledBack))
            .OrderBy(h => h.CountNo)
            .Select(h => new { h.Id, h.CountNo, h.CountDate, h.Status, h.PostedBatchNo })
            .ToArrayAsync(cancellationToken);
        var countIds = counts.Select(c => c.Id).ToArray();
        var countById = counts.ToDictionary(c => c.Id);

        var lines = countIds.Length == 0 ? [] : await db.IvStockCountLines.AsNoTracking()
            .Where(l => countIds.Contains(l.StockCountId))
            .OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.StockCountId, l.ICode, l.BalLocId, l.PhysicalQty })
            .ToArrayAsync(cancellationToken);
        var lineIds = lines.Select(l => l.Id).ToArray();

        var generations = lineIds.Length == 0 ? [] : await db.StockCountReplayGenerations.AsNoTracking()
            .Where(g => lineIds.Contains(g.StockCountLineId))
            .ToArrayAsync(cancellationToken);
        var generationsByLine = generations.GroupBy(g => g.StockCountLineId)
            .ToDictionary(g => g.Key, g => g.ToArray());

        var bases = lineIds.Length == 0 ? [] : await db.StockCountCostBases.AsNoTracking()
            .Where(b => lineIds.Contains(b.StockCountLineId))
            .ToArrayAsync(cancellationToken);
        var basisByLine = bases.ToDictionary(b => b.StockCountLineId);

        var standardIds = bases.Where(b => b.StandardCostRevisionId.HasValue)
            .Select(b => b.StandardCostRevisionId!.Value).Distinct().ToArray();
        var standardsById = standardIds.Length == 0
            ? new Dictionary<long, ItemStandardCostRevision>()
            : await db.ItemStandardCostRevisions.AsNoTracking()
                .Where(s => standardIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, cancellationToken);

        var anchorIds = generations.Select(g => g.AnchorPostingId).Distinct().ToArray();
        var anchorsById = anchorIds.Length == 0
            ? new Dictionary<long, StockPosting>()
            : await db.StockPostings.AsNoTracking()
                .Where(p => anchorIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, cancellationToken);

        foreach (var line in lines)
        {
            var header = countById[line.StockCountId];
            var isClosed = closedPeriods.Any(pc => header.CountDate.Date >= pc.PeriodFrom.Date
                && header.CountDate.Date <= pc.PeriodTo.Date);
            var hasGen = generationsByLine.TryGetValue(line.Id, out var lineGens) && lineGens.Length > 0;

            if (header.Status == IvStockCountStatuses.Posted && line.PhysicalQty is not null && !isClosed && !hasGen)
            {
                if (header.PostedBatchNo is null)
                    findings.Add(new(ReplayPreflightFindingCodes.LegacyZeroCountUnordered, header.CountNo, line.Id,
                        "No V2 anchor or trustworthy posting sequence exists for this zero-variance count."));
                else
                    findings.Add(new(ReplayPreflightFindingCodes.LegacyCountUnanchored, header.CountNo, line.Id,
                        $"Batch {header.PostedBatchNo} requires exact sealed V2 history/sequence reconciliation before backfill."));
            }

            if (hasGen && line.PhysicalQty > 0m && !basisByLine.ContainsKey(line.Id))
                findings.Add(new(ReplayPreflightFindingCodes.PositiveCountMissingBasis, header.CountNo, line.Id,
                    "A positive physical observation has no frozen approved cost basis."));

            if (basisByLine.TryGetValue(line.Id, out var basis))
            {
                if (basis.CompanyCode != company || basis.BranchCode != branch)
                    findings.Add(new(ReplayPreflightFindingCodes.BasisTenantMismatch, header.CountNo, line.Id,
                        $"Basis {basis.Id} tenant differs from count header."));
                var invalidBasis = basis.FrozenFallbackUnitCost < 0m
                    || (basis.CostBasisKind is StockCountCostBasisKinds.LedgerWac or StockCountCostBasisKinds.LedgerFifo
                        && (basis.CostBasisWatermark is null || string.IsNullOrWhiteSpace(basis.CostBasisHash)))
                    || (basis.CostBasisKind == StockCountCostBasisKinds.StandardRevision
                        && (basis.StandardCostRevisionId is not long revId
                            || !standardsById.TryGetValue(revId, out var std)
                            || std.CompanyCode != company || std.BranchCode != branch
                            || !string.Equals(std.ItemCode, line.ICode, StringComparison.OrdinalIgnoreCase)
                            || std.Status != ItemStandardCostRevisionStatuses.Approved
                            || std.EffectiveFrom > header.CountDate
                            || (std.EffectiveTo.HasValue && std.EffectiveTo.Value <= header.CountDate)))
                    || (basis.CostBasisKind == StockCountCostBasisKinds.ManualApproved
                        && (basis.CostEvidenceType != InventoryCostEvidenceTypes.ManualApproved
                            || basis.FrozenFallbackUnitCost <= 0m
                            || string.IsNullOrWhiteSpace(basis.CostOverrideReason)
                            || string.IsNullOrWhiteSpace(basis.CostApprovedBy)
                            || basis.CostApprovedAtUtc is null))
                    || (basis.CostBasisKind == StockCountCostBasisKinds.ZeroCostApproved
                        && (basis.CostEvidenceType != InventoryCostEvidenceTypes.ZeroCostApproved
                            || basis.FrozenFallbackUnitCost != 0m
                            || string.IsNullOrWhiteSpace(basis.CostOverrideReason)
                            || string.IsNullOrWhiteSpace(basis.CostApprovedBy)
                            || basis.CostApprovedAtUtc is null));
                if (invalidBasis)
                    findings.Add(new(ReplayPreflightFindingCodes.CountBasisInvalid, header.CountNo, line.Id,
                        $"Basis {basis.Id} has invalid item, revision, or approved cost evidence."));
            }

            if (hasGen)
            {
                var expectedEffectiveAt = StockReplayEventKey.CountEndOfDay(header.CountDate);
                var parentIds = lineGens!.Where(g => g.PreviousGenerationId.HasValue)
                    .Select(g => g.PreviousGenerationId!.Value).ToHashSet();
                var leaves = lineGens!.Where(g => !parentIds.Contains(g.Id)).ToArray();
                if (leaves.Length != 1)
                    findings.Add(new(ReplayPreflightFindingCodes.CountGenerationCorrupt, header.CountNo, line.Id,
                        $"Expected one active leaf; found {leaves.Length}."));
                else
                {
                    var leaf = leaves[0];
                    if ((header.Status == IvStockCountStatuses.Posted && leaf.IsRemoval)
                        || (header.Status == IvStockCountStatuses.RolledBack && !leaf.IsRemoval)
                        || line.PhysicalQty is null)
                        findings.Add(new(ReplayPreflightFindingCodes.CountActiveStatusMismatch, header.CountNo, line.Id,
                            $"Count status {header.Status} disagrees with active generation {leaf.Id}."));
                }

                foreach (var gen in lineGens!)
                {
                    if (!anchorsById.TryGetValue(gen.AnchorPostingId, out var anchor)
                        || anchor.SealedAtUtc is null
                        || anchor.CompanyCode != company || anchor.BranchCode != branch
                        || gen.CompanyCode != company || gen.BranchCode != branch
                        || gen.AnchorPostingSequence != anchor.PostingSequence
                        || gen.EffectiveAt != expectedEffectiveAt)
                        findings.Add(new(ReplayPreflightFindingCodes.CountAnchorMismatch, header.CountNo, line.Id,
                            $"Generation {gen.Id} anchor/tenant/sequence/effective date is invalid."));
                }
            }
        }

        var effects = await db.StockReplayEffects.AsNoTracking()
            .Where(e => e.CompanyCode == company && e.BranchCode == branch)
            .Select(e => new { e.Id, e.ReplayRunId, e.RootStockPostingId, e.BusinessOwnerPostingId, e.CompanyCode, e.BranchCode })
            .ToArrayAsync(cancellationToken);
        if (effects.Length > 0)
        {
            var runIds = effects.Select(e => e.ReplayRunId).Distinct().ToArray();
            var runsById = await db.StockReplayRuns.AsNoTracking()
                .Where(r => runIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, cancellationToken);
            var ownerIds = effects.Select(e => e.BusinessOwnerPostingId).Distinct().ToArray();
            var ownersById = await db.StockPostings.AsNoTracking()
                .Where(p => ownerIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, cancellationToken);
            foreach (var e in effects)
            {
                if (!runsById.TryGetValue(e.ReplayRunId, out var run)
                    || !ownersById.TryGetValue(e.BusinessOwnerPostingId, out var owner)
                    || run.RootStockPostingId != e.RootStockPostingId
                    || run.CompanyCode != e.CompanyCode || run.BranchCode != e.BranchCode
                    || owner.CompanyCode != e.CompanyCode || owner.BranchCode != e.BranchCode
                    || owner.SealedAtUtc is null)
                    findings.Add(new(ReplayPreflightFindingCodes.ReplayEffectOwnerInvalid, null, null,
                        $"Replay effect {e.Id} root or business owner is inconsistent."));
            }
        }

        return new BranchReplayPreflightReport
        {
            CompanyCode = company,
            BranchCode = branch,
            Findings = findings
        };
    }
}
