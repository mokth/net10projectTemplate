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

    public StockPostingCoordinator(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IBranchStockTransactionLock branchLock,
        IStockPeriodGuard periodGuard,
        IStockFreezeGuard freezeGuard)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _branchLock = branchLock;
        _periodGuard = periodGuard;
        _freezeGuard = freezeGuard;
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
            await SetDatabaseWriteContextAsync(db, posting.Id, cancellationToken);

            var context = new StockPostingContext(db, epoch, posting, scope.UserId);
            var value = await handler(context, cancellationToken);
            context.EnsureUnsealed();
            await db.SaveChangesAsync(cancellationToken);

            posting.SealedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await ClearDatabaseWriteContextAsync(db, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(true, true, false, posting.Id, posting.PostingSequence, value, null);
        }
        catch (StockLedgerException ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Failure<T>(ex.Error);
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
        await SetDatabaseWriteContextAsync(db, posting.Id, cancellationToken);
        return new(true, false, new StockPostingContext(db, epoch, posting, scope.UserId), null);
    }

    public async Task CompleteInTransactionAsync(
        StockPostingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();
        await context.Db.SaveChangesAsync(cancellationToken);
        context.Posting.SealedAtUtc = DateTime.UtcNow;
        await context.Db.SaveChangesAsync(cancellationToken);
        await ClearDatabaseWriteContextAsync(context.Db, cancellationToken);
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
