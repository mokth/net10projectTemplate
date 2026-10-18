using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayWacSingleCountRemovalPlan(
    StockPostingCommand Command,
    StockReplayCountSheetRemovalPlan Sheet,
    StockReplayWacRemovalPlan Financial);

/// <summary>
/// Binds one active WAC count line to its exact financial removal projection.
/// Multi-line sheets need a sheet-level projector before they can use this seam.
/// </summary>
public static class StockReplayWacSingleCountRemovalPlanner
{
    public static async Task<StockReplayWacSingleCountRemovalPlan> BuildAsync(
        AppDbContext db, string companyCode, string branchCode,
        int countId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var sheet = await StockReplayCountSheetRemovalPlanner.BuildAsync(
            db, companyCode, branchCode, countId, cancellationToken);
        if (sheet.Lines.Count != 1)
            throw Invalid("A multi-line count needs sheet-level financial removal.");
        var method = await new StockCostMethodResolver().ResolveAsync(db,
            companyCode, branchCode, sheet.EffectiveAt, cancellationToken);
        if (method != StockCostMethods.MovingAverage)
            throw Invalid("This count removal requires WAC cost authority.");
        var line = sheet.Lines[0];
        var itemCode = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.Id == line.CountLineId
                && l.StockCountId == countId)
            .Select(l => l.ICode)
            .SingleAsync(cancellationToken);
        var financial = await StockReplayWacRemovalPlanner.BuildAsync(
            db, companyCode, branchCode, itemCode,
            line.Physical, cancellationToken);
        var command = await StockCountRollbackCommandFactory.CreateAsync(
            db, sheet, cancellationToken);
        return new(command, sheet, financial);
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context,
        StockReplayWacSingleCountRemovalPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        if (plan.Sheet.Lines.Count != 1
            || plan.Financial.PhysicalPlan
                != plan.Sheet.Lines[0].Physical
            || context.Posting.RequestId != plan.Command.RequestId
            || context.Posting.RequestFingerprint
                != plan.Command.Evidence.RequestFingerprint)
            throw Stale("The count's financial removal differs from its root.");
        await StockReplayCountSheetRemovalPlanner.ValidateWithinRootAsync(
            context, plan.Sheet, cancellationToken);
        var currentCommand = await StockCountRollbackCommandFactory.CreateAsync(
            context.Db, plan.Sheet, cancellationToken);
        if (currentCommand.RequestId != plan.Command.RequestId
            || currentCommand.Evidence.RequestFingerprint
                != plan.Command.Evidence.RequestFingerprint)
            throw Stale("The count removal source snapshot changed.");
        var method = await new StockCostMethodResolver().ResolveAsync(
            context.Db, context.CompanyCode, context.BranchCode,
            plan.Sheet.EffectiveAt, cancellationToken);
        if (method != StockCostMethods.MovingAverage)
            throw Stale("The count's event-date costing policy changed.");
        await StockReplayWacRemovalPlanner.ValidateWithinRootAsync(
            context, plan.Financial, cancellationToken);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
