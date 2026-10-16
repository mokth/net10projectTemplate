using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;

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
    string PlanHash)
{
    public int? CountHeaderId { get; init; }
    public int? CountLineId { get; init; }
    public string? CountStatus { get; init; }
    public byte[]? CountHeaderRowVersion { get; init; }
    public byte[]? CountLineRowVersion { get; init; }
}

public sealed record StockReplayPhysicalRemovalPlan(
    string CompanyCode,
    string BranchCode,
    int BalLocId,
    long LedgerEpochId,
    long BranchWatermark,
    decimal CurrentQuantity,
    byte[] BalanceRowVersion,
    StockReplayPhysicalEvent RemovedActiveEvent,
    IReadOnlyList<StockReplayPhysicalEvent> LaterEvents,
    StockReplayPhysicalRemovalResult Projection,
    string PlanHash);

public sealed record StockCountSheetPhysicalPlan(
    string CompanyCode,
    string BranchCode,
    int CountHeaderId,
    DateTime EffectiveAt,
    long LedgerEpochId,
    long BranchWatermark,
    byte[] HeaderRowVersion,
    IReadOnlyList<StockReplayPhysicalPlan> Lines,
    string PlanHash);

public sealed record StockCountPreparedLine(
    int LineId,
    int BalLocId,
    decimal PhysicalQty,
    decimal SignedAdjustmentQty,
    decimal FinalQuantity);

/// <summary>
/// Granted only after the complete sheet was checked under the branch lock.
/// Its immutable projections survive the expected balance and header updates
/// before the append-only generation writer runs in the same root.
/// </summary>
public sealed class StockCountSheetWritePermit
{
    internal StockCountSheetWritePermit(
        StockCountSheetPhysicalPlan plan, long rootPostingId,
        ImmutableArray<StockCountPreparedLine> lines)
    {
        CompanyCode = plan.CompanyCode;
        BranchCode = plan.BranchCode;
        CountHeaderId = plan.CountHeaderId;
        CountStatus = plan.Lines[0].CountStatus!;
        EffectiveAt = plan.EffectiveAt;
        LedgerEpochId = plan.LedgerEpochId;
        PostingSequence = checked(plan.BranchWatermark + 1);
        RootPostingId = rootPostingId;
        PlanHash = plan.PlanHash;
        Lines = lines;
    }

    public string CompanyCode { get; }
    public string BranchCode { get; }
    public int CountHeaderId { get; }
    public string CountStatus { get; }
    public DateTime EffectiveAt { get; }
    public long LedgerEpochId { get; }
    public long PostingSequence { get; }
    public long RootPostingId { get; }
    public string PlanHash { get; }
    public ImmutableArray<StockCountPreparedLine> Lines { get; }
}

public sealed record StockCountCorrectionLine(
    int LineId,
    long PreviousGenerationId,
    long AnchorPostingId,
    long AnchorPostingSequence,
    DateTime EffectiveAt,
    decimal PhysicalQty,
    decimal OldSignedAdjustmentQty,
    decimal NewSignedAdjustmentQty);

/// <summary>
/// Count descendants changed by an earlier physical insertion. Created only
/// after the complete pre-lock plan and sealed count lineage are rechecked in
/// the triggering root. The writer must still verify the persisted histories.
/// </summary>
public sealed class StockCountCorrectionWritePermit
{
    internal StockCountCorrectionWritePermit(
        StockReplayPhysicalPlan plan, long rootPostingId,
        ImmutableArray<StockCountCorrectionLine> lines)
    {
        CompanyCode = plan.CompanyCode;
        BranchCode = plan.BranchCode;
        BalLocId = plan.BalLocId;
        LedgerEpochId = plan.LedgerEpochId;
        RootPostingId = rootPostingId;
        PlanHash = plan.PlanHash;
        Candidate = plan.Candidate.Key.ClassPriority == StockReplayEventKey.CountClass
            ? plan.Candidate with
            {
                SignedDelta = plan.Projection.Candidate.SignedDelta
            }
            : plan.Candidate;
        FinalQuantity = plan.Projection.FinalQuantity;
        Lines = lines;
    }

    public string CompanyCode { get; }
    public string BranchCode { get; }
    public int BalLocId { get; }
    public long LedgerEpochId { get; }
    public long RootPostingId { get; }
    public string PlanHash { get; }
    public StockReplayPhysicalEvent Candidate { get; }
    public decimal FinalQuantity { get; }
    public ImmutableArray<StockCountCorrectionLine> Lines { get; }
}

public static class StockReplayPhysicalPlanner
{
    /// <summary>One immutable pre-lock plan for every counted pile on a sheet.</summary>
    public static async Task<StockCountSheetPhysicalPlan> BuildCountSheetInsertionAsync(
        AppDbContext db, string companyCode, string branchCode, int countHeaderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var header = await db.IvStockCountHdrs.AsNoTracking()
            .Where(h => h.Id == countHeaderId && h.CompanyCode == companyCode
                && h.BranchCode == branchCode)
            .Select(h => new { h.Id, h.CountDate, h.Status, h.RowVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null || header.Status is not ("COUNTED" or "ROLLED_BACK")
            || header.CountDate != header.CountDate.Date)
            throw new StockLedgerException(new(StockLedgerErrorCodes.InvalidStockIdentity,
                $"Count {countHeaderId} is not an eligible sheet."));
        var lineIds = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.StockCountId == countHeaderId && l.PhysicalQty != null)
            .OrderBy(l => l.BalLocId).ThenBy(l => l.Id)
            .Select(l => l.Id).ToArrayAsync(cancellationToken);
        if (lineIds.Length == 0)
            throw new StockLedgerException(new(StockLedgerErrorCodes.InvalidStockIdentity,
                $"Count {countHeaderId} has no counted lines."));

        var plans = new List<StockReplayPhysicalPlan>(lineIds.Length);
        foreach (var lineId in lineIds)
            plans.Add(await BuildCountInsertionAsync(db, companyCode, branchCode,
                lineId, cancellationToken));
        var first = plans[0];
        var effectiveAt = StockReplayEventKey.CountEndOfDay(header.CountDate);
        if (plans.Any(p => p.CountHeaderId != countHeaderId
                || p.Candidate.Key.EffectiveAt != effectiveAt
                || p.LedgerEpochId != first.LedgerEpochId
                || p.BranchWatermark != first.BranchWatermark
                || p.CountStatus != header.Status
                || p.CountHeaderRowVersion is null
                || !p.CountHeaderRowVersion.SequenceEqual(header.RowVersion))
            || plans.Select(p => p.BalLocId).Distinct().Count() != plans.Count)
            throw Stale($"Count {countHeaderId} changed while its physical plan was built.");
        var rowVersion = header.RowVersion.ToArray();
        var hash = HashSheet(companyCode, branchCode, countHeaderId,
            effectiveAt, first.LedgerEpochId, first.BranchWatermark,
            rowVersion, plans);
        return new(companyCode, branchCode, countHeaderId, effectiveAt,
            first.LedgerEpochId, first.BranchWatermark, rowVersion, plans.ToArray(), hash);
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context,
        StockCountSheetPhysicalPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        if (context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode
            || context.Epoch.Id != plan.LedgerEpochId
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != "IV_STOCK_COUNT"
            || context.Posting.SourceDocumentId
                != plan.CountHeaderId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.EffectiveAt != plan.EffectiveAt
            || context.Posting.PostingSequence != checked(plan.BranchWatermark + 1))
            throw Stale("The count anchor changed after physical planning.");
        var countedIds = await context.Db.IvStockCountLines.AsNoTracking()
            .Where(l => l.StockCountId == plan.CountHeaderId && l.PhysicalQty != null)
            .Select(l => l.Id).ToArrayAsync(cancellationToken);
        if (countedIds.Length != plan.Lines.Count
            || !countedIds.Order().SequenceEqual(plan.Lines.Select(p => p.CountLineId!.Value).Order())
            || !StringComparer.Ordinal.Equals(plan.PlanHash, HashSheet(
                plan.CompanyCode, plan.BranchCode, plan.CountHeaderId,
                plan.EffectiveAt, plan.LedgerEpochId, plan.BranchWatermark,
                plan.HeaderRowVersion, plan.Lines)))
            throw Stale($"Count {plan.CountHeaderId} has a changed set of counted lines.");
        foreach (var line in plan.Lines)
            await ValidateWithinRootAsync(context, line, cancellationToken);
    }

    public static async Task<StockCountSheetWritePermit> PrepareCountSheetWithinRootAsync(
        StockPostingContext context,
        StockCountSheetPhysicalPlan plan,
        CancellationToken cancellationToken = default)
    {
        await ValidateWithinRootAsync(context, plan, cancellationToken);
        var lines = plan.Lines.Select(p => new StockCountPreparedLine(
            p.CountLineId!.Value, p.BalLocId,
            p.Candidate.AbsoluteCountQty!.Value,
            p.Projection.Candidate.SignedDelta,
            p.Projection.FinalQuantity)).ToImmutableArray();
        return new StockCountSheetWritePermit(plan, context.Posting.Id, lines);
    }

    private static string HashSheet(
        string companyCode, string branchCode, int countHeaderId,
        DateTime effectiveAt, long epochId, long watermark,
        byte[] rowVersion, IReadOnlyCollection<StockReplayPhysicalPlan> plans) =>
        StockPostingFingerprint.Create(new
        {
            companyCode, branchCode, countHeaderId, effectiveAt,
            epochId, watermark, rowVersion,
            lines = plans.Select(p => new { p.CountLineId, p.PlanHash }).ToArray()
        }, new { }).RequestFingerprint;

    /// <summary>
    /// Derives a new absolute end-of-day reset from the frozen count observation.
    /// The provisional event ID is outside persisted history/generation ID ranges;
    /// the writer uses the projected signed delta, never the provisional zero.
    /// </summary>
    public static async Task<StockReplayPhysicalPlan> BuildCountInsertionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int countLineId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var line = await db.IvStockCountLines.AsNoTracking()
            .Include(l => l.Header)
            .Where(l => l.Id == countLineId
                && l.Header.CompanyCode == companyCode
                && l.Header.BranchCode == branchCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (line is null || line.BalLocId <= 0
            || line.PhysicalQty is not decimal physical || physical < 0m
            || IvQty.Round(physical) != physical
            || line.Header.CountDate != line.Header.CountDate.Date
            || line.Header.Status is not ("COUNTED" or "ROLLED_BACK"))
            throw new StockLedgerException(new(StockLedgerErrorCodes.InvalidStockIdentity,
                $"Count line {countLineId} is not an eligible frozen physical observation."));
        var countedBalance = await db.IvBalLocs.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == line.BalLocId
                && b.CompanyCode == companyCode && b.BranchCode == branchCode,
                cancellationToken);
        if (countedBalance is null
            || !StockReplayCountSheetPreviewReader.Matches(line, countedBalance))
            throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                $"Count line {countLineId} no longer matches its counted stock pile."));
        if (line.Header.Status == "COUNTED"
            && await db.StockCountReplayGenerations.AsNoTracking()
                .AnyAsync(g => g.StockCountLineId == line.Id, cancellationToken))
            throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                $"Count line {countLineId} is COUNTED but already has posted lineage."));
        if (line.Header.Status == "ROLLED_BACK")
        {
            var active = await StockCountGenerationResolver.ResolveActiveAsync(
                db, companyCode, branchCode, [line.Id], cancellationToken);
            if (!active[line.Id].IsRemoval)
                throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                    $"Count line {countLineId} has no active removal generation."));
        }

        var watermark = await db.StockPostingBranchSequences.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode && s.BranchCode == branchCode)
            .Select(s => (long?)s.LastSequence)
            .SingleOrDefaultAsync(cancellationToken) ?? 0L;
        var candidate = new StockReplayPhysicalEvent(
            -long.MaxValue + line.Id,
            new StockReplayEventKey(StockReplayEventKey.CountEndOfDay(line.Header.CountDate),
                StockReplayEventKey.CountClass, checked(watermark + 1),
                $"C{line.Id:D10}", 0),
            SignedDelta: 0m,
            AbsoluteCountQty: physical);
        var plan = await BuildInsertionAsync(db, companyCode, branchCode,
            line.BalLocId, candidate, cancellationToken);
        return plan with
        {
            CountHeaderId = line.StockCountId,
            CountLineId = line.Id,
            CountStatus = line.Header.Status,
            CountHeaderRowVersion = line.Header.RowVersion.ToArray(),
            CountLineRowVersion = line.RowVersion.ToArray()
        };
    }

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
        if (candidate.Key.OriginalPostingSequence != checked(watermark + 1))
            throw Stale("The candidate sequence is not the next branch posting sequence.");

        var tail = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, companyCode, branchCode, balLocId, candidate.Key, cancellationToken);
        var projection = StockReplayPhysicalSimulator.Simulate(balance.StdQty, candidate, tail);
        var rowVersion = balance.RowVersion.ToArray();
        var hash = Hash(companyCode, branchCode, balLocId, epochs[0].Id,
            watermark, balance.StdQty, rowVersion, candidate, tail);
        return new(companyCode, branchCode, balLocId, epochs[0].Id,
            watermark, balance.StdQty, rowVersion, candidate, tail, projection, hash);
    }

    /// <summary>Checks that a previously prepared insertion plan was not edited in memory.</summary>
    public static void ValidatePreparedInsertionPlan(StockReplayPhysicalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        StockReplayPhysicalResult projection;
        try
        {
            projection = StockReplayPhysicalSimulator.Simulate(
                plan.CurrentQuantity, plan.Candidate, plan.LaterEvents);
        }
        catch (ArgumentException)
        {
            throw Stale("The prepared physical insertion plan changed.");
        }
        catch (StockLedgerException)
        {
            throw Stale("The prepared physical insertion plan changed.");
        }
        if (plan.PlanHash != Hash(plan.CompanyCode, plan.BranchCode,
                plan.BalLocId, plan.LedgerEpochId, plan.BranchWatermark,
                plan.CurrentQuantity, plan.BalanceRowVersion,
                plan.Candidate, plan.LaterEvents)
            || plan.Projection.HistoricalQuantityBeforeCandidate
                != projection.HistoricalQuantityBeforeCandidate
            || plan.Projection.Candidate != projection.Candidate
            || !plan.Projection.LaterSteps.SequenceEqual(projection.LaterSteps)
            || plan.Projection.FinalQuantity != projection.FinalQuantity)
            throw Stale("The prepared physical insertion plan changed.");
    }

    /// <summary>
    /// Plans removal of a currently active physical event. The target is read back
    /// from the sealed timeline, so a caller cannot submit an invented adjustment.
    /// </summary>
    public static async Task<StockReplayPhysicalRemovalPlan> BuildRemovalAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int balLocId,
        StockReplayPhysicalEvent removedActiveEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (removedActiveEvent.EventId == 0 || balLocId <= 0
            || removedActiveEvent.Key.AttachedSuborder != 0)
            throw new ArgumentException("An active physical event and pile are required.");

        var epochs = await db.StockLedgerEpochs.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode
                && x.Status == StockLedgerEpochStatuses.Active)
            .ToArrayAsync(cancellationToken);
        if (epochs.Length != 1 || epochs[0].Version != 2
            || epochs[0].EffectiveFrom.Date > removedActiveEvent.Key.EffectiveAt.Date)
            throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerDisabled,
                "A single compatible active V2 epoch must cover the removed event."));

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

        var later = await ReadRemovalTailAsync(db, companyCode, branchCode,
            balLocId, removedActiveEvent, cancellationToken);
        var projection = StockReplayPhysicalSimulator.SimulateRemoval(
            balance.StdQty, removedActiveEvent, later);
        var rowVersion = balance.RowVersion.ToArray();
        var hash = Hash(companyCode, branchCode, balLocId, epochs[0].Id,
            watermark, balance.StdQty, rowVersion, removedActiveEvent, later, "REMOVE");
        return new(companyCode, branchCode, balLocId, epochs[0].Id,
            watermark, balance.StdQty, rowVersion, removedActiveEvent, later, projection, hash);
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
        ValidatePreparedInsertionPlan(plan);
        if (context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode
            || context.Epoch.Id != plan.LedgerEpochId
            || context.Posting.EffectiveAt != plan.Candidate.Key.EffectiveAt
            || context.Posting.PostingSequence != plan.Candidate.Key.OriginalPostingSequence
            || context.Posting.PostingSequence != checked(plan.BranchWatermark + 1))
            throw Stale("The branch posting sequence changed after replay planning.");

        var db = context.Db;
        var balance = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == plan.CompanyCode && x.BranchCode == plan.BranchCode
                && x.Id == plan.BalLocId)
            .SingleOrDefaultAsync(cancellationToken);
        if (balance is null || balance.StdQty != plan.CurrentQuantity
            || !balance.RowVersion.SequenceEqual(plan.BalanceRowVersion))
            throw Stale($"Pile {plan.BalLocId} changed after replay planning.");

        if (plan.CountLineId is int countLineId)
        {
            var observation = await db.IvStockCountLines.AsNoTracking()
                .Include(l => l.Header)
                .Where(l => l.Id == countLineId
                    && l.Header.CompanyCode == plan.CompanyCode
                    && l.Header.BranchCode == plan.BranchCode)
                .SingleOrDefaultAsync(cancellationToken);
            if (observation is null
                || observation.StockCountId != plan.CountHeaderId
                || observation.BalLocId != plan.BalLocId
                || observation.PhysicalQty != plan.Candidate.AbsoluteCountQty
                || observation.Header.CountDate != plan.Candidate.Key.EffectiveAt.Date
                || observation.Header.Status != plan.CountStatus
                || plan.CountLineRowVersion is null
                || plan.CountHeaderRowVersion is null
                || !observation.RowVersion.SequenceEqual(plan.CountLineRowVersion)
                || !observation.Header.RowVersion.SequenceEqual(plan.CountHeaderRowVersion)
                || !StockReplayCountSheetPreviewReader.Matches(observation, balance))
                throw Stale($"Count line {countLineId} changed after replay planning.");
        }

        var tail = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, plan.CompanyCode, plan.BranchCode, plan.BalLocId,
            plan.Candidate.Key, cancellationToken);
        var hash = Hash(plan.CompanyCode, plan.BranchCode, plan.BalLocId,
            plan.LedgerEpochId, plan.BranchWatermark, balance.StdQty,
            balance.RowVersion, plan.Candidate, tail);
        if (!StringComparer.Ordinal.Equals(hash, plan.PlanHash))
            throw Stale("The active physical tail changed after replay planning.");
        await EnsureTailOpenAsync(db, plan.CompanyCode, plan.BranchCode,
            plan.LaterEvents.Select(e => e.Key.EffectiveAt)
                .Prepend(plan.Candidate.Key.EffectiveAt).Min(), cancellationToken);
    }

    public static async Task<StockCountCorrectionWritePermit> PrepareCountCorrectionsWithinRootAsync(
        StockPostingContext context,
        StockReplayPhysicalPlan plan,
        CancellationToken cancellationToken = default)
    {
        await ValidateWithinRootAsync(context, plan, cancellationToken);
        var ordinary = plan.Candidate.Key.ClassPriority
            == StockReplayEventKey.OrdinaryClass
            && plan.Candidate.AbsoluteCountQty is null;
        var initialCount = plan.Candidate.Key.ClassPriority
            == StockReplayEventKey.CountClass
            && plan.CountStatus == IvStockCountStatuses.Counted
            && plan.CountLineId is > 0
            && plan.Candidate.AbsoluteCountQty is not null;
        if (!ordinary && !initialCount)
            throw Stale("Count correction preparation requires a supported physical insertion.");
        var steps = plan.Projection.LaterSteps.ToDictionary(s => s.EventId);
        if (steps.Count != plan.LaterEvents.Count)
            throw Stale("The replay projection has ambiguous descendant identities.");
        var changes = plan.LaterEvents.Where(e =>
                e.Key.ClassPriority == StockReplayEventKey.CountClass
                && steps.TryGetValue(e.EventId, out var step)
                && step.SignedDelta != e.SignedDelta).ToArray();
        if (changes.Length == 0)
            return new StockCountCorrectionWritePermit(plan, context.Posting.Id, []);
        if (changes.Any(e => e.EventId >= 0 || e.Key.AttachedSuborder != 0
                || e.AbsoluteCountQty is null))
            throw Stale("A count descendant has an invalid physical identity.");

        var lineIds = changes.Select(e =>
        {
            var identity = e.Key.LineIdentity;
            if (identity.Length < 2 || identity[0] != 'C'
                || !int.TryParse(identity.AsSpan(1), out var id) || id <= 0
                || identity != $"C{id:D10}")
                throw Stale("A count descendant has an invalid line identity.");
            return id;
        }).ToArray();
        if (lineIds.Distinct().Count() != lineIds.Length)
            throw Stale("A counted line appears twice in the replay tail.");
        var active = await StockCountGenerationResolver.ResolveActiveAsync(
            context.Db, plan.CompanyCode, plan.BranchCode, lineIds, cancellationToken);
        var lines = new List<StockCountCorrectionLine>(changes.Length);
        for (var i = 0; i < changes.Length; i++)
        {
            var movement = changes[i];
            var prior = active[lineIds[i]];
            if (prior.IsRemoval || prior.Id != -movement.EventId
                || prior.SignedAdjustmentQty != movement.SignedDelta
                || prior.EffectiveAt != movement.Key.EffectiveAt
                || prior.AnchorPostingSequence != movement.Key.OriginalPostingSequence)
                throw Stale($"Count line {lineIds[i]} changed active generation after planning.");
            lines.Add(new StockCountCorrectionLine(lineIds[i], prior.Id,
                prior.AnchorPostingId, prior.AnchorPostingSequence,
                prior.EffectiveAt, movement.AbsoluteCountQty!.Value,
                prior.SignedAdjustmentQty, steps[movement.EventId].SignedDelta));
        }
        return new StockCountCorrectionWritePermit(plan, context.Posting.Id,
            lines.ToImmutableArray());
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context,
        StockReplayPhysicalRemovalPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        if (context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode
            || context.Epoch.Id != plan.LedgerEpochId
            || context.Posting.EffectiveAt != plan.RemovedActiveEvent.Key.EffectiveAt
            || context.Posting.PostingSequence != checked(plan.BranchWatermark + 1))
            throw Stale("The branch posting sequence changed after removal planning.");

        var db = context.Db;
        var balance = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == plan.CompanyCode && x.BranchCode == plan.BranchCode
                && x.Id == plan.BalLocId)
            .Select(x => new { x.StdQty, x.RowVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (balance is null || balance.StdQty != plan.CurrentQuantity
            || !balance.RowVersion.SequenceEqual(plan.BalanceRowVersion))
            throw Stale($"Pile {plan.BalLocId} changed after removal planning.");

        IReadOnlyList<StockReplayPhysicalEvent> later;
        try
        {
            later = await ReadRemovalTailAsync(db, plan.CompanyCode, plan.BranchCode,
                plan.BalLocId, plan.RemovedActiveEvent, cancellationToken);
        }
        catch (StockLedgerException ex) when (ex.Error.Code == StockLedgerErrorCodes.StaleDocument)
        {
            throw Stale("The active event changed after removal planning.");
        }
        var hash = Hash(plan.CompanyCode, plan.BranchCode, plan.BalLocId,
            plan.LedgerEpochId, plan.BranchWatermark, balance.StdQty,
            balance.RowVersion, plan.RemovedActiveEvent, later, "REMOVE");
        if (!StringComparer.Ordinal.Equals(hash, plan.PlanHash))
            throw Stale("The active physical tail changed after removal planning.");
        await EnsureTailOpenAsync(db, plan.CompanyCode, plan.BranchCode,
            plan.LaterEvents.Select(e => e.Key.EffectiveAt)
                .Prepend(plan.RemovedActiveEvent.Key.EffectiveAt).Min(), cancellationToken);
    }

    private static async Task<IReadOnlyList<StockReplayPhysicalEvent>> ReadRemovalTailAsync(
        AppDbContext db, string companyCode, string branchCode, int balLocId,
        StockReplayPhysicalEvent target, CancellationToken cancellationToken)
    {
        // Physical base events use suborder zero. The predecessor key includes
        // that event in the timeline read without changing its date or class.
        var beforeTarget = target.Key with { AttachedSuborder = -1 };
        var timeline = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, companyCode, branchCode, balLocId, beforeTarget, cancellationToken);
        if (timeline.Count(e => e.Key == target.Key) != 1
            || !timeline.Any(e => e == target))
            throw Stale($"Physical event {target.EventId} is no longer active with the planned adjustment.");
        return timeline.Where(e => e.Key.CompareTo(target.Key) > 0).ToArray();
    }

    private static string Hash(
        string companyCode, string branchCode, int balLocId, long epochId,
        long watermark, decimal currentQuantity, byte[] rowVersion,
        StockReplayPhysicalEvent candidate,
        IReadOnlyList<StockReplayPhysicalEvent> later,
        string operation = "INSERT") =>
        StockPostingFingerprint.Create(new
        {
            operation, companyCode, branchCode, balLocId, epochId, watermark,
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

    private static async Task EnsureTailOpenAsync(
        AppDbContext db, string companyCode, string branchCode,
        DateTime earliestEffectiveAt, CancellationToken cancellationToken)
    {
        var refusal = await IvPeriodCloseGuard.EnsureOpenAsync(db,
            companyCode, branchCode, earliestEffectiveAt, cancellationToken);
        if (refusal is not null)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.ClosedPeriod, refusal));
    }
}
