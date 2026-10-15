using System.Globalization;
using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public interface IStockPostingCoordinator
{
    Task<StockPostingExecutionResult<T>> ExecuteAsync<T>(
        StockPostingCommand command,
        Func<StockPostingContext, CancellationToken, Task<T>> handler,
        CancellationToken cancellationToken = default);

    Task<StockPostingBeginResult> BeginInTransactionAsync(
        AppDbContext db,
        StockPostingCommand command,
        CancellationToken cancellationToken = default);

    Task CompleteInTransactionAsync(
        StockPostingContext context,
        CancellationToken cancellationToken = default);

    Task CompleteReplayInTransactionAsync(
        StockPostingContext context,
        IReadOnlyCollection<int> requiredActiveHistoryIds,
        CancellationToken cancellationToken = default);

    Task AbortInTransactionAsync(
        StockPostingContext context,
        CancellationToken cancellationToken = default);

    Task ValuePendingInTransactionAsync(
        StockPostingContext context,
        CancellationToken cancellationToken = default);
}

public sealed record StockPostingBeginResult(
    bool LedgerEnabled,
    bool WasReplay,
    StockPostingContext? Context,
    StockLedgerError? Error);

public sealed class StockPostingCoordinator : IStockPostingCoordinator
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IBranchStockTransactionLock _branchLock;
    private readonly IStockPeriodGuard _periodGuard;
    private readonly IStockFreezeGuard _freezeGuard;
    private readonly IInventoryValuationService? _valuation;

    public StockPostingCoordinator(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IBranchStockTransactionLock branchLock,
        IStockPeriodGuard periodGuard,
        IStockFreezeGuard freezeGuard,
        IInventoryValuationService? valuation = null)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _branchLock = branchLock;
        _periodGuard = periodGuard;
        _freezeGuard = freezeGuard;
        _valuation = valuation;
    }

    public async Task<StockPostingExecutionResult<T>> ExecuteAsync<T>(
        StockPostingCommand command,
        Func<StockPostingContext, CancellationToken, Task<T>> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(handler);
        Validate(command);

        var scope = _tenant.TryWriteScope();
        if (scope?.BranchCode is null)
            return Failure<T>(new(StockLedgerErrorCodes.InvalidStockIdentity, "A trusted company/branch write scope is required."));

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var writeContextInstalled = false;
        try
        {
            await _branchLock.AcquireAsync(
                db, scope.CompanyCode, scope.BranchCode, cancellationToken);

            var epoch = await db.StockLedgerEpochs
                .SingleOrDefaultAsync(x =>
                    x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.Status == StockLedgerEpochStatuses.Active,
                    cancellationToken);
            if (epoch is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return StockPostingExecutionResult<T>.Disabled();
            }

            await StockLedgerCompatibility.EnsureCompatibleAsync(
                db, scope.CompanyCode, scope.BranchCode, cancellationToken);

            var replay = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.CommandType == command.CommandType
                && x.RequestId == command.RequestId,
                cancellationToken);
            if (replay is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                if (!string.Equals(replay.RequestFingerprint, command.Evidence.RequestFingerprint, StringComparison.Ordinal))
                    return Failure<T>(new(
                        StockLedgerErrorCodes.RequestIdReused,
                        "The request ID was already used with different semantic input."));
                if (replay.SealedAtUtc is null)
                    return Failure<T>(new(
                        StockLedgerErrorCodes.LedgerMismatch,
                        "An unsealed posting exists for this request."));
                return new(true, true, true, replay.Id, replay.PostingSequence, default, null);
            }

            var sourceExists = await db.StockPostings.AsNoTracking().AnyAsync(x =>
                x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.SourceModule == command.SourceModule
                && x.SourceDocumentType == command.SourceDocumentType
                && x.SourceDocumentId == command.SourceDocumentId
                && x.DocumentRevision == command.DocumentRevision
                && x.PostingRole == command.PostingRole,
                cancellationToken);
            if (sourceExists)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failure<T>(new(
                    StockLedgerErrorCodes.DocumentAlreadyPosted,
                    "This source document revision and posting role already has a stock posting."));
            }

            if (command.ReversesPostingId is long reversesId)
            {
                var validTarget = await db.StockPostings.AsNoTracking().AnyAsync(x =>
                    x.Id == reversesId
                    && x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.SealedAtUtc != null,
                    cancellationToken);
                if (!validTarget)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Failure<T>(new(
                        StockLedgerErrorCodes.InvalidStockIdentity,
                        "The reversal target is not a sealed posting in the current tenant."));
                }
            }

            var periodError = await _periodGuard.ValidateAsync(
                db, scope.CompanyCode, scope.BranchCode, command.EffectiveAt, cancellationToken);
            if (periodError is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failure<T>(periodError);
            }

            var freezeError = await _freezeGuard.ValidateAsync(
                db, scope.CompanyCode, scope.BranchCode, command.FreezeScopes, cancellationToken);
            if (freezeError is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failure<T>(freezeError);
            }

            var sequence = await NextSequenceAsync(
                db, scope.CompanyCode, scope.BranchCode, cancellationToken);
            var posting = BuildPosting(command, scope, epoch.Id, sequence);
            db.StockPostings.Add(posting);
            await db.SaveChangesAsync(cancellationToken);
            writeContextInstalled = true;
            await SetDatabaseWriteContextAsync(db, posting.Id, cancellationToken);

            var context = new StockPostingContext(db, epoch, posting, scope.UserId);
            var value = await handler(context, cancellationToken);
            context.EnsureUnsealed();
            if (_valuation is not null)
                await _valuation.ValuePendingAsync(context, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            posting.SealedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await ClearDatabaseWriteContextAsync(db, cancellationToken);
            writeContextInstalled = false;
            await transaction.CommitAsync(cancellationToken);
            return new(true, true, false, posting.Id, posting.PostingSequence, value, null);
        }
        catch (StockLedgerException ex)
        {
            try
            {
                if (writeContextInstalled)
                    await ClearDatabaseWriteContextAsync(db, CancellationToken.None);
            }
            finally
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            return Failure<T>(ex.Error);
        }
        catch
        {
            try
            {
                if (writeContextInstalled)
                    await ClearDatabaseWriteContextAsync(db, CancellationToken.None);
            }
            finally
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            throw;
        }
    }

    public async Task<StockPostingBeginResult> BeginInTransactionAsync(
        AppDbContext db,
        StockPostingCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An existing transaction is required.");

        var scope = _tenant.TryWriteScope();
        if (scope?.BranchCode is null)
            return new(true, false, null, new(
                StockLedgerErrorCodes.InvalidStockIdentity,
                "The posting command does not match the trusted branch scope."));

        await _branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode, cancellationToken);
        var epoch = await db.StockLedgerEpochs.SingleOrDefaultAsync(x =>
            x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode
            && x.Status == StockLedgerEpochStatuses.Active, cancellationToken);
        if (epoch is null)
            return new(false, false, null, null);

        await StockLedgerCompatibility.EnsureCompatibleAsync(
            db, scope.CompanyCode, scope.BranchCode, cancellationToken);

        var replay = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode
            && x.CommandType == command.CommandType
            && x.RequestId == command.RequestId, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.RequestFingerprint, command.Evidence.RequestFingerprint, StringComparison.Ordinal))
                return new(true, false, null, new(
                    StockLedgerErrorCodes.RequestIdReused,
                    "The request ID was already used with different semantic input."));
            return new(true, true, null, replay.SealedAtUtc is null
                ? new(StockLedgerErrorCodes.LedgerMismatch, "An unsealed posting exists for this request.")
                : null);
        }

        var sourceExists = await db.StockPostings.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode
            && x.SourceModule == command.SourceModule
            && x.SourceDocumentType == command.SourceDocumentType
            && x.SourceDocumentId == command.SourceDocumentId
            && x.DocumentRevision == command.DocumentRevision
            && x.PostingRole == command.PostingRole, cancellationToken);
        if (sourceExists)
            return new(true, false, null, new(
                StockLedgerErrorCodes.DocumentAlreadyPosted,
                "This source document revision and posting role already has a stock posting."));

        if (command.ReversesPostingId is long reversesId
            && !await db.StockPostings.AsNoTracking().AnyAsync(x =>
                x.Id == reversesId && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode && x.SealedAtUtc != null, cancellationToken))
            return new(true, false, null, new(
                StockLedgerErrorCodes.InvalidStockIdentity,
                "The reversal target is not a sealed posting in the current tenant."));

        var periodError = await _periodGuard.ValidateAsync(
            db, scope.CompanyCode, scope.BranchCode, command.EffectiveAt, cancellationToken);
        if (periodError is not null) return new(true, false, null, periodError);
        var freezeError = await _freezeGuard.ValidateAsync(
            db, scope.CompanyCode, scope.BranchCode, command.FreezeScopes, cancellationToken);
        if (freezeError is not null) return new(true, false, null, freezeError);

        var sequence = await NextSequenceAsync(db, scope.CompanyCode, scope.BranchCode, cancellationToken);
        var posting = BuildPosting(command, scope, epoch.Id, sequence);
        db.StockPostings.Add(posting);
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            await SetDatabaseWriteContextAsync(db, posting.Id, cancellationToken);
        }
        catch
        {
            // A failed caller-owned begin has no context to pass to AbortInTransactionAsync.
            // Clear any marker that was installed before the provider reported failure.
            try { await ClearDatabaseWriteContextAsync(db, CancellationToken.None); }
            catch { /* Preserve the begin failure; caller still rolls back its transaction. */ }
            throw;
        }
        return new(true, false, new StockPostingContext(db, epoch, posting, scope.UserId), null);
    }

    public async Task CompleteInTransactionAsync(
        StockPostingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();
        if (_valuation is not null)
            await _valuation.ValuePendingAsync(context, cancellationToken);
        await context.Db.SaveChangesAsync(cancellationToken);
        context.Posting.SealedAtUtc = DateTime.UtcNow;
        await context.Db.SaveChangesAsync(cancellationToken);
        await ClearDatabaseWriteContextAsync(context.Db, cancellationToken);
    }

    /// <summary>
    /// Replay has already valued its affected chronology. Verify complete active
    /// Inventory generations and exact root reversals, then seal without invoking
    /// normal live-state valuation a second time.
    /// </summary>
    public async Task CompleteReplayInTransactionAsync(
        StockPostingContext context,
        IReadOnlyCollection<int> requiredActiveHistoryIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requiredActiveHistoryIds);
        context.EnsureUnsealed();
        if (_valuation is null)
            throw new StockLedgerException(new(StockLedgerErrorCodes.ValuationRequired,
                "V2 valuation is required before replay can be sealed."));

        var ids = requiredActiveHistoryIds.Distinct().ToArray();
        if (ids.Length != requiredActiveHistoryIds.Count || ids.Any(id => id <= 0))
            throw new StockLedgerException(new(StockLedgerErrorCodes.InvalidStockIdentity,
                "Replay valuation history IDs must be unique and positive."));
        var db = context.Db;
        await db.SaveChangesAsync(cancellationToken);

        var rootHistories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.StockPostingId == context.Posting.Id && h.LedgerVersion == 2)
            .ToArrayAsync(cancellationToken);
        if (rootHistories.Any(h => h.CompanyCode != context.CompanyCode
                || h.BranchCode != context.BranchCode
                || h.EntryRole is not ("ORIGINAL" or "REVERSAL")))
            throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                "Replay root contains an invalid Inventory history identity."));
        var countWrites = await db.StockCountReplayGenerations.AsNoTracking()
            .Where(g => g.CompanyCode == context.CompanyCode
                && g.BranchCode == context.BranchCode
                && g.WritePostingId == context.Posting.Id)
            .Select(g => new { g.AdjustmentHistoryId, g.ReversalHistoryId })
            .ToArrayAsync(cancellationToken);
        var linkedCountIds = countWrites
            .SelectMany(g => new[] { g.AdjustmentHistoryId, g.ReversalHistoryId })
            .Where(id => id.HasValue).Select(id => id!.Value).ToArray();
        var rootHistoryIds = rootHistories.Select(h => h.Id).ToHashSet();
        var reversedOriginalIds = rootHistories
            .Where(h => h.EntryRole == "REVERSAL" && h.ReversesHistoryId.HasValue)
            .Select(h => h.ReversesHistoryId!.Value).Distinct().ToArray();
        var countOriginalIds = reversedOriginalIds.Length == 0
            ? []
            : await db.StockCountReplayGenerations.AsNoTracking()
                .Where(g => g.CompanyCode == context.CompanyCode
                    && g.BranchCode == context.BranchCode
                    && g.AdjustmentHistoryId.HasValue
                    && reversedOriginalIds.Contains(g.AdjustmentHistoryId.Value))
                .Select(g => g.AdjustmentHistoryId!.Value)
                .ToArrayAsync(cancellationToken);
        var countOriginalSet = countOriginalIds.ToHashSet();
        var linkedCountSet = linkedCountIds.ToHashSet();
        if (linkedCountIds.Length != linkedCountSet.Count
            || linkedCountIds.Any(id => !rootHistoryIds.Contains(id))
            || rootHistories.Any(h =>
                (h.StockCountCostBasisId.HasValue
                    || h.EntryRole == "REVERSAL"
                    && h.ReversesHistoryId is int reversedId
                    && countOriginalSet.Contains(reversedId))
                && !linkedCountSet.Contains(h.Id)))
            throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                "A replay root count history lacks exact generation ownership."));
        var requiredIds = ids.ToHashSet();
        if (rootHistories.Any(h => h.EntryRole == "ORIGINAL" && !requiredIds.Contains(h.Id)))
            throw new StockLedgerException(new(StockLedgerErrorCodes.ValuationRequired,
                "A replay root Inventory history was omitted from valuation verification."));

        var active = await db.IvTrxHistories.AsNoTracking()
            .Where(h => ids.Contains(h.Id) && h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode && h.LedgerVersion == 2
                && h.EntryRole == "ORIGINAL")
            .ToArrayAsync(cancellationToken);
        if (active.Length != ids.Length
            || active.Any(h => h.ValuationStatus != StockValuationStatuses.Valued))
            throw new StockLedgerException(new(StockLedgerErrorCodes.ValuationRequired,
                "A required active replay history is missing or has invalid lineage."));
        await StockReplayValuationGenerationResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode, active, cancellationToken);

        var rootReversals = rootHistories.Where(h => h.EntryRole == "REVERSAL").ToArray();
        if (rootReversals.Length > 0)
        {
            var reversalIds = rootReversals.Select(h => h.Id).ToArray();
            var originalIds = rootReversals.Select(h => h.ReversesHistoryId)
                .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
            if (originalIds.Length != rootReversals.Length)
                throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                    "Replay root has an incomplete or duplicate Inventory reversal lineage."));
            if (rootReversals.Any(h => h.ValuationStatus != StockValuationStatuses.Reversed))
                throw new StockLedgerException(new(StockLedgerErrorCodes.ValuationRequired,
                    "A replay root Inventory reversal has not been valued."));
            var originalFacts = await db.StockValuationFacts.AsNoTracking()
                .Where(f => f.InventoryHistoryId.HasValue
                    && originalIds.Contains(f.InventoryHistoryId.Value)
                    && f.ReversesValuationFactId == null
                    && f.StockPostingId != context.Posting.Id)
                .ToArrayAsync(cancellationToken);
            var originalFactIds = originalFacts.Select(f => f.Id).ToArray();
            var previouslyReversedIds = (await db.StockValuationFacts.AsNoTracking()
                    .Where(f => f.ReversesValuationFactId.HasValue
                        && originalFactIds.Contains(f.ReversesValuationFactId.Value)
                        && f.StockPostingId != context.Posting.Id)
                    .Select(f => f.ReversesValuationFactId!.Value)
                    .ToArrayAsync(cancellationToken))
                .ToHashSet();
            var activeOriginalFacts = originalFacts
                .Where(f => !previouslyReversedIds.Contains(f.Id)).ToArray();
            var reversalFacts = await db.StockValuationFacts.AsNoTracking()
                .Where(f => f.InventoryHistoryId.HasValue
                    && reversalIds.Contains(f.InventoryHistoryId.Value))
                .ToArrayAsync(cancellationToken);
            var originalsById = activeOriginalFacts.ToDictionary(f => f.Id);
            foreach (var history in rootReversals)
            {
                var originals = activeOriginalFacts.Where(f =>
                    f.InventoryHistoryId == history.ReversesHistoryId).ToArray();
                var reversals = reversalFacts.Where(f => f.InventoryHistoryId == history.Id).ToArray();
                if (originals.Length == 0 || originals.Length != reversals.Length
                    || reversals.Select(f => f.ReversesValuationFactId).Distinct().Count()
                        != reversals.Length
                    || reversals.Any(f => f.ReversesValuationFactId is not long originalId
                        || !originalsById.TryGetValue(originalId, out var original)
                        || original.InventoryHistoryId != history.ReversesHistoryId
                        || f.CompanyCode != context.CompanyCode
                        || f.BranchCode != context.BranchCode
                        || f.ValuationStatus != StockValuationStatuses.Reversed
                        || f.ItemCode != original.ItemCode
                        || f.CostMethod != original.CostMethod
                        || f.Direction != -original.Direction
                        || f.BaseQty != original.BaseQty
                        || f.CostAmount != original.CostAmount))
                    throw new StockLedgerException(new(StockLedgerErrorCodes.ValuationRequired,
                        $"Inventory reversal history {history.Id} lacks exact valuation reversal facts."));
            }
        }

        context.Posting.SealedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await ClearDatabaseWriteContextAsync(db, cancellationToken);
    }

    public Task AbortInTransactionAsync(
        StockPostingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Caller owns the transaction. Clear the pooled connection's session marker before
        // its rollback/disposal; clearing twice is harmless after a partial failure.
        return ClearDatabaseWriteContextAsync(context.Db, cancellationToken);
    }

    public async Task ValuePendingInTransactionAsync(
        StockPostingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();
        if (_valuation is null)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.ValuationRequired,
                "V2 inventory valuation is required before this posting can continue."));

        await _valuation.ValuePendingAsync(context, cancellationToken);
        await context.Db.SaveChangesAsync(cancellationToken);
    }

    private static async Task SetDatabaseWriteContextAsync(AppDbContext db, long postingId, CancellationToken ct)
    {
        if (db.Database.IsSqlServer())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"EXEC sys.sp_set_session_context @key=N'STOCK_LEDGER_V2_POSTING_ID', @value={postingId}", ct);
    }

    private static async Task ClearDatabaseWriteContextAsync(AppDbContext db, CancellationToken ct)
    {
        if (db.Database.IsSqlServer())
            await db.Database.ExecuteSqlRawAsync(
                "EXEC sys.sp_set_session_context @key=N'STOCK_LEDGER_V2_POSTING_ID', @value=NULL", ct);
    }

    private static async Task<long> NextSequenceAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken)
    {
        var counter = await db.StockPostingBranchSequences.SingleOrDefaultAsync(x =>
            x.CompanyCode == companyCode && x.BranchCode == branchCode, cancellationToken);
        if (counter is null)
        {
            counter = new StockPostingBranchSequence
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                LastSequence = 0,
            };
            db.StockPostingBranchSequences.Add(counter);
        }

        counter.LastSequence = checked(counter.LastSequence + 1);
        counter.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return counter.LastSequence;
    }

    private static StockPosting BuildPosting(
        StockPostingCommand command,
        InventoryTenantScope scope,
        long epochId,
        long sequence) => new()
    {
        CompanyCode = scope.CompanyCode,
        BranchCode = scope.BranchCode!,
        LedgerEpochId = epochId,
        PostingSequence = sequence,
        RequestId = command.RequestId,
        CommandType = command.CommandType.Trim(),
        RequestFingerprint = command.Evidence.RequestFingerprint,
        SourceModule = command.SourceModule.Trim(),
        SourceDocumentType = command.SourceDocumentType.Trim(),
        SourceDocumentId = command.SourceDocumentId.Trim(),
        SourceDocumentNo = command.SourceDocumentNo.Trim(),
        DocumentRevision = command.DocumentRevision,
        PostingRole = command.PostingRole.Trim(),
        SourceSnapshotJson = command.Evidence.SourceSnapshotJson,
        SourceSnapshotHash = command.Evidence.SourceSnapshotHash,
        SourceSnapshotSchemaVersion = command.SourceSnapshotSchemaVersion,
        EffectiveAt = command.EffectiveAt,
        BusinessDate = command.EffectiveAt.Date,
        PeriodKey = command.EffectiveAt.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        PostedAtUtc = DateTime.UtcNow,
        PostedBy = scope.UserId,
        ProductionPostingLinkId = command.ProductionPostingLinkId,
        ReversesPostingId = command.ReversesPostingId,
        ReasonCode = command.ReasonCode,
        ReasonText = command.ReasonText,
    };

    private static void Validate(StockPostingCommand command)
    {
        if (command.RequestId == Guid.Empty)
            throw new ArgumentException("RequestId is required.", nameof(command));
        if (command.DocumentRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(command), "DocumentRevision cannot be negative.");
        if (string.IsNullOrWhiteSpace(command.CommandType)
            || string.IsNullOrWhiteSpace(command.SourceModule)
            || string.IsNullOrWhiteSpace(command.SourceDocumentType)
            || string.IsNullOrWhiteSpace(command.SourceDocumentId)
            || string.IsNullOrWhiteSpace(command.PostingRole))
            throw new ArgumentException("Posting source identity is incomplete.", nameof(command));
        if (command.Evidence.RequestFingerprint.Length != 64
            || command.Evidence.SourceSnapshotHash.Length != 64)
            throw new ArgumentException("Posting evidence must use SHA-256 hashes.", nameof(command));
    }

    private static StockPostingExecutionResult<T> Failure<T>(StockLedgerError error) =>
        new(false, true, false, null, null, default, error);
}
