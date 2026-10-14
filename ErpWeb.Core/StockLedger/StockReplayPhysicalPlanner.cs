using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Read-only physical impact captured before a posting takes the branch lock.
/// A writer must validate it again inside its root transaction before mutation.
/// This plan does not authorize financial replay or bypass a posting guard.
/// </summary>
public sealed record StockReplayPhysicalPlan(
    string CompanyCode,
    string BranchCode,
    int BalLocId,
    long LedgerEpochId,
    long BranchWatermark,
    decimal CurrentQuantity,
    byte[] BalanceRowVersion,
    StockReplayPhysicalEvent Candidate,
    IReadOnlyList<StockReplayPhysicalEvent> LaterEvents,
    StockReplayPhysicalResult Projection,
    string PlanHash);

public static class StockReplayPhysicalPlanner
{
    public static async Task<StockReplayPhysicalPlan> BuildInsertionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int balLocId,
        StockReplayPhysicalEvent candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (candidate.EventId == 0 || balLocId <= 0)
            throw new ArgumentException("A candidate identity and pile are required.");

        var epochs = await db.StockLedgerEpochs.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode
                && x.Status == StockLedgerEpochStatuses.Active)
            .ToArrayAsync(cancellationToken);
        if (epochs.Length != 1 || epochs[0].Version != 2
            || epochs[0].EffectiveFrom.Date > candidate.Key.EffectiveAt.Date)
            throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerDisabled,
                "A single compatible active V2 epoch must cover the replay candidate."));

        var balance = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode
                && x.Id == balLocId)
            .Select(x => new { x.StdQty, x.RowVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (balance is null || balance.StdQty < 0m
            || IvQty.Round(balance.StdQty) != balance.StdQty)
            throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                $"Pile {balLocId} is missing or has an invalid live quantity."));

        var watermark = await db.StockPostingBranchSequences.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode)
            .Select(x => (long?)x.LastSequence)
            .SingleOrDefaultAsync(cancellationToken) ?? 0L;
        var latestSealed = await db.StockPostings.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode
                && x.SealedAtUtc != null)
            .MaxAsync(x => (long?)x.PostingSequence, cancellationToken) ?? 0L;
        if (watermark < latestSealed)
            throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                "The branch sequence counter trails a sealed posting."));

        var tail = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, companyCode, branchCode, balLocId, candidate.Key, cancellationToken);
        var projection = StockReplayPhysicalSimulator.Simulate(balance.StdQty, candidate, tail);
        var rowVersion = balance.RowVersion.ToArray();
        var hash = Hash(companyCode, branchCode, balLocId, epochs[0].Id,
            watermark, balance.StdQty, rowVersion, candidate, tail);
        return new(companyCode, branchCode, balLocId, epochs[0].Id,
            watermark, balance.StdQty, rowVersion, candidate, tail, projection, hash);
    }

    /// <summary>
    /// Rechecks the pre-lock evidence after BeginInTransactionAsync has created the
    /// current unsealed root. On staleness the caller rolls back and replans outside
    /// the branch lock; this method does not retry while holding it.
    /// </summary>
    public static async Task ValidateWithinRootAsync(
        StockPostingContext context,
        StockReplayPhysicalPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        if (context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode
            || context.Epoch.Id != plan.LedgerEpochId
            || context.Posting.EffectiveAt != plan.Candidate.Key.EffectiveAt
            || context.Posting.PostingSequence != checked(plan.BranchWatermark + 1))
            throw Stale("The branch posting sequence changed after replay planning.");

        var db = context.Db;
        var balance = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == plan.CompanyCode && x.BranchCode == plan.BranchCode
                && x.Id == plan.BalLocId)
            .Select(x => new { x.StdQty, x.RowVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (balance is null || balance.StdQty != plan.CurrentQuantity
            || !balance.RowVersion.SequenceEqual(plan.BalanceRowVersion))
            throw Stale($"Pile {plan.BalLocId} changed after replay planning.");

        var tail = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, plan.CompanyCode, plan.BranchCode, plan.BalLocId,
            plan.Candidate.Key, cancellationToken);
        var hash = Hash(plan.CompanyCode, plan.BranchCode, plan.BalLocId,
            plan.LedgerEpochId, plan.BranchWatermark, balance.StdQty,
            balance.RowVersion, plan.Candidate, tail);
        if (!StringComparer.Ordinal.Equals(hash, plan.PlanHash))
            throw Stale("The active physical tail changed after replay planning.");
    }

    private static string Hash(
        string companyCode, string branchCode, int balLocId, long epochId,
        long watermark, decimal currentQuantity, byte[] rowVersion,
        StockReplayPhysicalEvent candidate,
        IReadOnlyList<StockReplayPhysicalEvent> later) =>
        StockPostingFingerprint.Create(new
        {
            companyCode, branchCode, balLocId, epochId, watermark,
            currentQuantity, rowVersion,
            candidate = Event(candidate),
            later = later.Select(Event).ToArray()
        }, new { }).RequestFingerprint;

    private static object Event(StockReplayPhysicalEvent value) => new
    {
        value.EventId,
        value.Key.EffectiveAt,
        value.Key.ClassPriority,
        value.Key.OriginalPostingSequence,
        value.Key.LineIdentity,
        value.Key.AttachedSuborder,
        value.SignedDelta,
        value.AbsoluteCountQty
    };

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument, message, Retryable: true));
}
