using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Costing;

public sealed class CostingStateRepairPlanner
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;

    public CostingStateRepairPlanner(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
    }

    public async Task<CostingRepairPlan> PlanAsync(
        string itemCode,
        string costMethod,
        string? findingCode,
        CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.InventoryCostingCenter, PermissionCodes.Access, cancellationToken))
            return CostingRepairPlannerResults.Blocked("Not authorized.");
        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
            return CostingRepairPlannerResults.Blocked("A trusted company and branch are required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var decision = await CostingStateRepairEvaluator.EvaluateAsync(
            db, scope.CompanyCode, scope.BranchCode, itemCode, costMethod, findingCode, cancellationToken);
        return decision.ToPlan();
    }
}

internal static class CostingRepairPlannerResults
{
    public static CostingRepairPlan Blocked(string reason) =>
        new(false, "PREVIEW_ONLY", reason, [], [], [], null, null, 0, 0, [], string.Empty);
}

internal sealed record CostingStateRepairDecision(
    bool CanRepair,
    string? BlockingReason,
    string PreviewHash,
    string ItemCode,
    string CostMethod,
    string FindingCode,
    long? ActiveEpochId,
    decimal ExpectedQty,
    decimal ExpectedValue,
    decimal CurrentQty,
    decimal CurrentValue,
    long? LatestFactId,
    long LatestPostingSequence,
    bool StateMissing)
{
    public CostingRepairPlan ToPlan()
    {
        if (!CanRepair)
            return CostingRepairPlannerResults.Blocked(BlockingReason ?? "Cost-state rebuild is blocked.");
        return new CostingRepairPlan(
            true,
            "REBUILD_COST_STATE",
            null,
            [],
            [ItemCode],
            [ItemCode],
            null,
            null,
            0,
            0,
            [new CostingRepairPoolImpact(ItemCode, CostMethod, "EA", CurrentQty, ExpectedQty, CurrentValue, ExpectedValue, ExpectedValue - CurrentValue)],
            PreviewHash);
    }
}

internal static class CostingStateRepairEvaluator
{
    public static async Task<CostingStateRepairDecision> EvaluateAsync(
        AppDbContext db,
        string company,
        string branch,
        string itemCode,
        string costMethod,
        string? findingCode,
        CancellationToken cancellationToken)
    {
        var item = itemCode.Trim();
        var method = costMethod.Trim();
        var empty = new CostingStateRepairDecision(false, null, string.Empty, item, method, findingCode ?? string.Empty, null, 0m, 0m, 0m, 0m, null, 0L, false);
        if (item.Length == 0 || method.Length == 0)
            return empty with { BlockingReason = "Item and cost method are required." };
        if (!StockCostMethods.All.Contains(method))
            return empty with { BlockingReason = "This cost method has no proven rebuild provider." };

        var coverage = await DescribeEpochAsync(db, company, branch, cancellationToken);
        if (coverage.Code != CostingEpochCoverage.V2 || coverage.ActiveEpochId is null)
        {
            return empty with
            {
                BlockingReason = coverage.Code == CostingEpochCoverage.UnresolvedCrossEpoch
                    ? "Cross-epoch evidence is unresolved. Cost-state rebuild stays blocked."
                    : "No active ledger epoch. Cost-state rebuild stays blocked."
            };
        }

        var facts = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.ItemCode == item && x.CostMethod == method
                && x.StockPosting!.SealedAtUtc != null)
            .Select(x => new FactEvidence(
                x.Id, x.Direction, x.BaseQty, x.CostAmount, x.ValuationStatus,
                x.ReversesValuationFactId, x.StockPosting!.PostingSequence, x.PostingLineNo, x.SplitOrdinal,
                x.StockPosting.SealedAtUtc != null))
            .ToListAsync(cancellationToken);

        var unsealed = await db.StockValuationFacts.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.ItemCode == item
            && x.StockPosting!.SealedAtUtc == null, cancellationToken)
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(h =>
                h.CompanyCode == company && h.BranchCode == branch && h.ICode == item
                && h.StockPostingId != null
                && db.StockPostings.Any(p => p.Id == h.StockPostingId && p.SealedAtUtc == null), cancellationToken);
        if (unsealed)
            return empty with { BlockingReason = "An unsealed posting still affects this item. Rebuild stays blocked." };

        if (await ReversalLineageBrokenAsync(db, company, branch, item, cancellationToken))
            return empty with { BlockingReason = "Reversal lineage is broken. Rebuild stays blocked." };

        var active = facts.Where(x =>
            x.ValuationStatus == StockValuationStatuses.Valued
            && x.ReversesValuationFactId == null
            && !facts.Any(r => r.ReversesValuationFactId == x.Id)).ToArray();
        var expectedQty = StockLedgerPrecision.Quantity(active.Sum(x => x.BaseQty * x.Direction));
        var expectedValue = StockLedgerPrecision.Money(active.Sum(x => x.CostAmount * x.Direction));
        var latest = facts
            .OrderByDescending(x => x.PostingSequence)
            .ThenByDescending(x => x.PostingLineNo)
            .ThenByDescending(x => x.SplitOrdinal)
            .FirstOrDefault();

        var snapshotBlock = await SnapshotBlockAsync(db, company, branch, coverage.ActiveEpochId.Value, item, method, cancellationToken);
        if (snapshotBlock is not null)
            return empty with { BlockingReason = snapshotBlock };

        var methodBlock = await MethodBlockAsync(db, company, branch, item, method, expectedQty, expectedValue, cancellationToken);
        if (methodBlock is not null)
            return empty with { BlockingReason = methodBlock };

        var state = await db.StockCostStates.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.ItemCode == item && x.CostMethod == method)
            .Select(x => new { x.OnHandBaseQty, x.InventoryValue, x.AverageUnitCost })
            .FirstOrDefaultAsync(cancellationToken);
        var missing = state is null;
        var currentQty = state?.OnHandBaseQty ?? 0m;
        var currentValue = state?.InventoryValue ?? 0m;
        var average = expectedQty == 0m ? 0m : StockLedgerPrecision.Money(expectedValue / expectedQty);
        var code = string.IsNullOrWhiteSpace(findingCode)
            ? InferCode(missing, expectedQty, expectedValue, currentQty, currentValue, state?.AverageUnitCost ?? 0m, average)
            : findingCode.Trim();
        var hash = Hash(company, branch, item, method, coverage.ActiveEpochId.Value, latest?.PostingSequence ?? 0L,
            expectedQty, expectedValue, currentQty, currentValue, latest?.Id);
        var decision = empty with
        {
            PreviewHash = hash,
            FindingCode = code,
            ActiveEpochId = coverage.ActiveEpochId,
            ExpectedQty = expectedQty,
            ExpectedValue = expectedValue,
            CurrentQty = currentQty,
            CurrentValue = currentValue,
            LatestFactId = latest?.Id,
            LatestPostingSequence = latest?.PostingSequence ?? 0L,
            StateMissing = missing
        };

        if (missing && expectedQty == 0m && expectedValue == 0m)
            return decision with { BlockingReason = "Historical facts net to zero. A missing zero cost state is not rebuilt." };
        if (!missing
            && currentQty == expectedQty
            && currentValue == expectedValue
            && StockLedgerPrecision.Money(state!.AverageUnitCost) == average)
            return decision with { BlockingReason = "The cost state already matches sealed valuation evidence." };

        return decision with { CanRepair = true };
    }

    private static string InferCode(bool missing, decimal expectedQty, decimal expectedValue, decimal currentQty, decimal currentValue, decimal actualAverage, decimal expectedAverage)
    {
        if (missing)
            return CostingFindingCodes.CostStateMissing;
        if (currentQty == 0m && currentValue != 0m && expectedQty == 0m && expectedValue == 0m)
            return CostingFindingCodes.ZeroQuantityResidue;
        if (currentQty != expectedQty)
            return CostingFindingCodes.CostStateQuantityMismatch;
        if (currentValue != expectedValue)
            return CostingFindingCodes.CostStateValueMismatch;
        if (StockLedgerPrecision.Money(actualAverage) != expectedAverage)
            return CostingFindingCodes.CostStateAverageMismatch;
        return CostingFindingCodes.CostStateValueMismatch;
    }

    public static string Hash(
        string company, string branch, string item, string method, long epochId, long lastSequence,
        decimal expectedQty, decimal expectedValue, decimal currentQty, decimal currentValue, long? latestFactId)
    {
        var text = string.Join('|',
            company, branch, item, method, epochId.ToString(CultureInfo.InvariantCulture),
            lastSequence.ToString(CultureInfo.InvariantCulture),
            expectedQty.ToString(CultureInfo.InvariantCulture),
            expectedValue.ToString(CultureInfo.InvariantCulture),
            currentQty.ToString(CultureInfo.InvariantCulture),
            currentValue.ToString(CultureInfo.InvariantCulture),
            latestFactId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static async Task<string?> MethodBlockAsync(
        AppDbContext db, string company, string branch, string item, string method,
        decimal expectedQty, decimal expectedValue, CancellationToken cancellationToken)
    {
        if (string.Equals(method, StockCostMethods.MovingAverage, StringComparison.Ordinal))
            return null;
        if (string.Equals(method, StockCostMethods.Fifo, StringComparison.Ordinal))
            return await FifoBlockAsync(db, company, branch, item, expectedQty, expectedValue, cancellationToken);
        if (string.Equals(method, StockCostMethods.Standard, StringComparison.Ordinal))
        {
            var open = await db.ItemStandardCostRevisions.AsNoTracking().CountAsync(x =>
                x.CompanyCode == company && x.BranchCode == branch && x.ItemCode == item
                && x.Status == ItemStandardCostRevisionStatuses.Approved
                && x.EffectiveTo == null, cancellationToken);
            return open == 1
                ? null
                : "Standard-cost evidence is ambiguous. Rebuild stays diagnostic.";
        }

        return "This cost method has no proven rebuild provider.";
    }

    private static async Task<string?> FifoBlockAsync(
        AppDbContext db, string company, string branch, string item,
        decimal expectedQty, decimal expectedValue, CancellationToken cancellationToken)
    {
        var layers = await db.StockFifoLayers.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.ItemCode == item)
            .Select(x => new { x.Id, x.OriginalQty, x.RemainingQty, x.OriginalValue, x.RemainingValue, x.AccumulatedAdjustment })
            .ToListAsync(cancellationToken);
        if (layers.Count == 0)
            return expectedQty == 0m && expectedValue == 0m
                ? null
                : "FIFO layers do not reconcile to sealed facts. Rebuild stays diagnostic.";

        var layerIds = layers.Select(x => x.Id).ToArray();
        var consumptions = await db.StockFifoLayerConsumptions.AsNoTracking()
            .Where(x => layerIds.Contains(x.FifoLayerId))
            .Select(x => new { x.FifoLayerId, x.ConsumedQty, x.ConsumedValue, x.ReversesConsumptionId })
            .ToListAsync(cancellationToken);
        decimal remainingQty = 0m;
        decimal remainingValue = 0m;
        foreach (var layer in layers)
        {
            var rows = consumptions.Where(x => x.FifoLayerId == layer.Id).ToArray();
            var consumedQty = StockLedgerPrecision.Quantity(
                rows.Where(x => x.ReversesConsumptionId == null).Sum(x => x.ConsumedQty)
                - rows.Where(x => x.ReversesConsumptionId != null).Sum(x => x.ConsumedQty));
            var consumedValue = StockLedgerPrecision.Money(
                rows.Where(x => x.ReversesConsumptionId == null).Sum(x => x.ConsumedValue)
                - rows.Where(x => x.ReversesConsumptionId != null).Sum(x => x.ConsumedValue));
            var layerQty = StockLedgerPrecision.Quantity(layer.OriginalQty - consumedQty);
            var layerValue = StockLedgerPrecision.Money(layer.OriginalValue + layer.AccumulatedAdjustment - consumedValue);
            if (layerQty != StockLedgerPrecision.Quantity(layer.RemainingQty)
                || layerValue != StockLedgerPrecision.Money(layer.RemainingValue))
                return "FIFO layers do not reconcile to their consumptions. Rebuild stays diagnostic.";
            remainingQty += layer.RemainingQty;
            remainingValue += layer.RemainingValue;
        }

        return StockLedgerPrecision.Quantity(remainingQty) == expectedQty
            && StockLedgerPrecision.Money(remainingValue) == expectedValue
            ? null
            : "FIFO layers do not reconcile to sealed facts. Rebuild stays diagnostic.";
    }

    private static async Task<string?> SnapshotBlockAsync(
        AppDbContext db, string company, string branch, long epochId, string item, string method, CancellationToken cancellationToken)
    {
        var header = await db.StockValuationPeriodSnapshotHdrs.AsNoTracking()
            .Include(x => x.Lines)
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.LedgerEpochId == epochId
                && x.ValuationStatus == "SEALED")
            .OrderByDescending(x => x.Revision)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (header is null)
            return null;

        var through = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.ItemCode == item && x.CostMethod == method
                && x.StockPosting!.SealedAtUtc != null
                && x.StockPosting.PostingSequence <= header.PostingSequenceWatermark
                && x.ValuationStatus == StockValuationStatuses.Valued
                && x.ReversesValuationFactId == null
                && !db.StockValuationFacts.Any(r => r.ReversesValuationFactId == x.Id))
            .Select(x => new { x.Direction, x.BaseQty, x.CostAmount })
            .ToListAsync(cancellationToken);
        var qty = StockLedgerPrecision.Quantity(through.Sum(x => x.BaseQty * x.Direction));
        var value = StockLedgerPrecision.Money(through.Sum(x => x.CostAmount * x.Direction));
        var line = header.Lines.FirstOrDefault(x =>
            string.Equals(x.ItemCode, item, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.CostMethod, method, StringComparison.Ordinal));
        if (line is null)
            return qty == 0m && value == 0m ? null : "Closed valuation snapshot evidence does not match sealed facts.";
        if (StockLedgerPrecision.Quantity(line.ClosingQty) != qty || StockLedgerPrecision.Money(line.ClosingValue) != value)
            return "Closed valuation snapshot evidence does not match sealed facts.";

        var physical = await db.StockPeriodSnapshotHdrs.AsNoTracking()
            .Include(x => x.Lines)
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.LedgerEpochId == epochId && x.PeriodKey == header.PeriodKey
                && x.QuantityStatus == "SEALED")
            .OrderByDescending(x => x.Revision)
            .FirstOrDefaultAsync(cancellationToken);
        if (physical is null)
            return null;
        var physicalQty = StockLedgerPrecision.Quantity(physical.Lines
            .Where(x => x.LedgerArea == "INVENTORY" && string.Equals(x.ItemCode, item, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.BaseQty));
        return physicalQty == StockLedgerPrecision.Quantity(line.ClosingQty)
            ? null
            : "Closed quantity snapshot does not reconcile to valuation evidence.";
    }

    private static async Task<bool> ReversalLineageBrokenAsync(
        AppDbContext db, string company, string branch, string item, CancellationToken cancellationToken)
    {
        var reversals = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.ItemCode == item && x.ReversesValuationFactId != null)
            .Select(x => x.ReversesValuationFactId!.Value)
            .ToListAsync(cancellationToken);
        if (reversals.Count == 0)
            return false;
        var originals = await db.StockValuationFacts.AsNoTracking()
            .Where(x => reversals.Contains(x.Id))
            .Select(x => new { x.Id, x.CompanyCode, x.BranchCode, Sealed = x.StockPosting!.SealedAtUtc != null })
            .ToListAsync(cancellationToken);
        return reversals.Any(id =>
        {
            var original = originals.FirstOrDefault(x => x.Id == id);
            return original is null || original.CompanyCode != company || original.BranchCode != branch || !original.Sealed;
        });
    }

    private static async Task<(string Code, long? ActiveEpochId)> DescribeEpochAsync(
        AppDbContext db, string company, string branch, CancellationToken cancellationToken)
    {
        var epochs = await db.StockLedgerEpochs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new { x.Id, x.Status })
            .ToListAsync(cancellationToken);
        var active = epochs.Where(x => string.Equals(x.Status, StockLedgerEpochStatuses.Active, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (active.Length == 0)
            return (CostingEpochCoverage.NoActiveEpoch, null);
        var activeIds = active.Select(x => x.Id).ToArray();
        var retiredIds = epochs.Where(x => string.Equals(x.Status, StockLedgerEpochStatuses.Retired, StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToArray();
        var retiredFacts = retiredIds.Length > 0 && await db.StockValuationFacts.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && retiredIds.Contains(x.LedgerEpochId)
            && x.StockPosting!.SealedAtUtc != null, cancellationToken);
        var activeFacts = await db.StockValuationFacts.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && activeIds.Contains(x.LedgerEpochId)
            && x.StockPosting!.SealedAtUtc != null, cancellationToken);
        return retiredFacts && activeFacts
            ? (CostingEpochCoverage.UnresolvedCrossEpoch, active[0].Id)
            : (CostingEpochCoverage.V2, active[0].Id);
    }

    private sealed record FactEvidence(
        long Id, int Direction, decimal BaseQty, decimal CostAmount, string ValuationStatus,
        long? ReversesValuationFactId, long PostingSequence, int PostingLineNo, int SplitOrdinal, bool Sealed);
}
