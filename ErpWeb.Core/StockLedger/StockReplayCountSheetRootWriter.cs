using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayCountSheetRootPlan(
    StockPostingCommand Command,
    StockCountSheetPhysicalPlan PhysicalSheet,
    IReadOnlyList<StockCountCostBasis> FrozenBases,
    string CostMethod,
    StockReplayWacCountSheetPostingPlan? Wac,
    StockReplayStandardCountSheetPostingPlan? Standard,
    StockReplayFifoCountSheetPostingPlan? Fifo);

public sealed record StockReplayCountSheetRootResult(
    IReadOnlyList<int> RequiredActiveHistoryIds,
    StockReplayWacCountSheetPostResult? Wac,
    StockReplayStandardCountSheetPostResult? Standard,
    StockReplayFifoInitialCountSheetResult? Fifo);

/// <summary>
/// Connects initial count approvals, physical planning, cost planning, and the
/// immutable anchor command before the branch lock. The caller owns one root
/// transaction, completion, downstream reconciliation, and commit.
/// </summary>
public static class StockReplayCountSheetRootWriter
{
    public static async Task<StockReplayCountSheetRootPlan> BuildAsync(
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
        var bases = await StockCountCostBasisProposalFactory.CreateAsync(db,
            accessRights, companyCode, branchCode, countId, approvals,
            approvedBy, approvedAtUtc, cancellationToken);
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, companyCode, branchCode, countId, cancellationToken);
        var method = await new StockCostMethodResolver().ResolveAsync(db,
            companyCode, branchCode, sheet.EffectiveAt, cancellationToken);
        StockReplayWacCountSheetPostingPlan? wac = null;
        StockReplayStandardCountSheetPostingPlan? standard = null;
        StockReplayFifoCountSheetPostingPlan? fifo = null;
        switch (method)
        {
            case StockCostMethods.MovingAverage:
                wac = await StockReplayWacCountSheetPostingWriter.BuildAsync(
                    db, sheet, bases, cancellationToken);
                break;
            case StockCostMethods.Standard:
                standard = await StockReplayStandardCountSheetPostingWriter.BuildAsync(
                    db, sheet, bases, cancellationToken);
                break;
            case StockCostMethods.Fifo:
                fifo = await StockReplayFifoCountSheetPostingPlanner.BuildAsync(
                    db, sheet, bases, cancellationToken);
                break;
            default:
                throw Invalid($"Count {countId} has no supported costing policy.");
        }
        var command = await StockCountAnchorCommandFactory.CreateAsync(db,
            companyCode, branchCode, countId, bases, cancellationToken);
        if (command.EffectiveAt != sheet.EffectiveAt)
            throw Invalid($"Count {countId} anchor and physical sheet have different event keys.");
        return new(command, sheet, bases, method, wac, standard, fifo);
    }

    public static async Task<StockReplayCountSheetRootResult> AppendWithinRootAsync(
        StockPostingContext context,
        StockReplayCountSheetRootPlan plan,
        IRunningNumberService numbers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(numbers);
        context.EnsureUnsealed();
        if (context.Posting.RequestId != plan.Command.RequestId
            || context.Posting.RequestFingerprint != plan.Command.Evidence.RequestFingerprint
            || context.Posting.SourceDocumentType != "IV_STOCK_COUNT"
            || context.Posting.SourceDocumentId != plan.Command.SourceDocumentId
            || context.Posting.EffectiveAt != plan.PhysicalSheet.EffectiveAt
            || context.CompanyCode != plan.PhysicalSheet.CompanyCode
            || context.BranchCode != plan.PhysicalSheet.BranchCode
            || context.Epoch.Id != plan.PhysicalSheet.LedgerEpochId
            || context.Posting.PostingSequence != plan.PhysicalSheet.BranchWatermark + 1
            || new object?[] { plan.Wac, plan.Standard, plan.Fifo }
                .Count(x => x is not null) != 1)
            throw Invalid("The count sheet plan does not match its unsealed root.");
        var method = await new StockCostMethodResolver().ResolveAsync(context.Db,
            context.CompanyCode, context.BranchCode, context.Posting.EffectiveAt,
            cancellationToken);
        if (method != plan.CostMethod)
            throw Invalid("The count sheet costing policy changed after planning.");

        if (plan.CostMethod == StockCostMethods.MovingAverage
            && plan.Wac is { } wacPlan && plan.Standard is null)
        {
            var prepared = await StockReplayWacCountSheetPostingWriter
                .PrepareWithinRootAsync(context, wacPlan, cancellationToken);
            var result = await StockReplayWacCountSheetPostingWriter
                .AppendWithinRootAsync(context, prepared, plan.FrozenBases,
                    numbers, cancellationToken);
            return new(result.RequiredActiveHistoryIds, result, null, null);
        }
        if (plan.CostMethod == StockCostMethods.Standard
            && plan.Standard is { } standardPlan && plan.Wac is null)
        {
            var prepared = await StockReplayStandardCountSheetPostingWriter
                .PrepareWithinRootAsync(context, standardPlan, cancellationToken);
            var result = await StockReplayStandardCountSheetPostingWriter
                .AppendWithinRootAsync(context, prepared, plan.FrozenBases,
                    numbers, cancellationToken);
            return new(result.RequiredActiveHistoryIds, null, result, null);
        }
        if (plan.CostMethod == StockCostMethods.Fifo
            && plan.Fifo is { } fifoPlan
            && plan.Wac is null && plan.Standard is null)
        {
            var prepared = await StockReplayFifoCountSheetPostingPlanner
                .PrepareWithinRootAsync(context, fifoPlan, cancellationToken);
            var result = await StockReplayFifoInitialCountSheetWriter
                .AppendWithinRootAsync(context, prepared, numbers,
                    cancellationToken);
            return new(result.RequiredActiveHistoryIds, null, null, result);
        }
        throw Invalid("The count sheet has no supported financial writer.");
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
