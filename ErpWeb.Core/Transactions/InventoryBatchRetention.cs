using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Transactions;

/// <summary>
/// Physical removal is allowed only for a true draft. A batch that has executed keeps its header.
/// </summary>
public static class InventoryBatchRetention
{
    public static async Task<bool> TryRemoveTrueDraftAsync(
        AppDbContext db, IvTrxBatch batch, CancellationToken cancellationToken = default)
    {
        if (batch.DeletedAtUtc is not null)
            return false;
        if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            return false;
        if (batch.PostedCount != 0 || batch.RollbackCount != 0 || batch.ForceCloseDate is not null)
            return false;
        if (await InventoryBatchExecutionProbe.HasEverExecutedAsync(db, batch, cancellationToken))
            return false;

        var details = await db.IvTrxBatchDetails.Where(x => x.BatchId == batch.Id).ToListAsync(cancellationToken);
        if (await HasImmutableDetailReferenceAsync(db, details.Select(x => x.Id).ToArray(), cancellationToken))
            return false;

        if (details.Count > 0)
            db.IvTrxBatchDetails.RemoveRange(details);
        db.IvTrxBatches.Remove(batch);
        return true;
    }

    /// <summary>
    /// True-draft headers are removed. Historical headers stay, with only unreferenced draft details cleared.
    /// Returns true when the header was physically removed.
    /// </summary>
    public static async Task<bool> ReleaseOwnedNewBatchAsync(
        AppDbContext db, IvTrxBatch batch, CancellationToken cancellationToken = default)
    {
        if (batch.DeletedAtUtc is not null)
        {
            throw new InvalidOperationException(
                TransactionLifecycleGuard.ArchivedError(batch.DeletedAtUtc, $"Batch {batch.BatchNo}")!);
        }

        if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Stock batch {batch.BatchNo} cannot be deleted because it is not NEW (status: {batch.BatchStatus}).");
        }

        if (await TryRemoveTrueDraftAsync(db, batch, cancellationToken))
            return true;

        await RemoveMutableDetailsAsync(db, batch, cancellationToken);
        return false;
    }

    public static async Task RemoveMutableDetailsAsync(
        AppDbContext db, IvTrxBatch batch, CancellationToken cancellationToken = default)
    {
        var details = await db.IvTrxBatchDetails.Where(x => x.BatchId == batch.Id).ToListAsync(cancellationToken);
        if (details.Count == 0)
            return;
        var blocked = await ImmutableDetailIdsAsync(db, details.Select(x => x.Id).ToArray(), cancellationToken);
        db.IvTrxBatchDetails.RemoveRange(details.Where(x => !blocked.Contains(x.Id)));
    }

    public static void Archive(IvTrxBatch batch, string? userId, string? reason)
    {
        if (batch.DeletedAtUtc is not null)
            return;
        var now = DateTime.UtcNow;
        batch.DeletedAtUtc = now;
        batch.DeletedBy = TransactionArchiveStamp.TruncateUser(userId, 10);
        batch.DeleteReason = TransactionArchiveStamp.NormalizeReason(reason);
        batch.ModifiedDate = now;
        batch.ModifiedBy = TransactionArchiveStamp.TruncateUser(userId, 10);
    }

    public static void Archive(SaDo document, string? userId, string? reason) =>
        Stamp(document.DeletedAtUtc, at => document.DeletedAtUtc = at, by => document.DeletedBy = by,
            reasonValue => document.DeleteReason = reasonValue, at => document.ModifiedDate = at,
            by => document.ModifiedBy = by, userId, reason, 20);

    public static void Archive(SaInvoice document, string? userId, string? reason) =>
        Stamp(document.DeletedAtUtc, at => document.DeletedAtUtc = at, by => document.DeletedBy = by,
            reasonValue => document.DeleteReason = reasonValue, at => document.ModifiedDate = at,
            by => document.ModifiedBy = by, userId, reason, 20);

    public static void Archive(SaCdn document, string? userId, string? reason) =>
        Stamp(document.DeletedAtUtc, at => document.DeletedAtUtc = at, by => document.DeletedBy = by,
            reasonValue => document.DeleteReason = reasonValue, at => document.ModifiedDate = at,
            by => document.ModifiedBy = by, userId, reason, 20);

    public static void Archive(PoInvoice document, string? userId, string? reason) =>
        Stamp(document.DeletedAtUtc, at => document.DeletedAtUtc = at, by => document.DeletedBy = by,
            reasonValue => document.DeleteReason = reasonValue, at => document.ModifiedDate = at,
            by => document.ModifiedBy = by, userId, reason, 20);

    public static void Archive(PoCdn document, string? userId, string? reason) =>
        Stamp(document.DeletedAtUtc, at => document.DeletedAtUtc = at, by => document.DeletedBy = by,
            reasonValue => document.DeleteReason = reasonValue, at => document.ModifiedDate = at,
            by => document.ModifiedBy = by, userId, reason, 20);

    public static void Archive(ProductionOutput document, string? userId, string? reason) =>
        Stamp(document.DeletedAtUtc, at => document.DeletedAtUtc = at, by => document.DeletedBy = by,
            reasonValue => document.DeleteReason = reasonValue, at => document.ModifiedDate = at,
            by => document.ModifiedBy = by, userId, reason, 10);

    public static void Archive(ProductionFinishedGoodReceipt document, string? userId, string? reason)
    {
        if (document.DeletedAtUtc is not null)
            return;
        var now = DateTime.UtcNow;
        document.DeletedAtUtc = now;
        document.DeletedBy = TransactionArchiveStamp.TruncateUser(userId, 10);
        document.DeleteReason = TransactionArchiveStamp.NormalizeReason(reason);
    }

    private static void Stamp(
        DateTime? current,
        Action<DateTime?> setAt,
        Action<string?> setBy,
        Action<string?> setReason,
        Action<DateTime?> setModified,
        Action<string?> setModifiedBy,
        string? userId,
        string? reason,
        int userWidth)
    {
        if (current is not null)
            return;
        var now = DateTime.UtcNow;
        var user = TransactionArchiveStamp.TruncateUser(userId, userWidth);
        setAt(now);
        setBy(user);
        setReason(TransactionArchiveStamp.NormalizeReason(reason));
        setModified(now);
        setModifiedBy(user);
    }

    private static async Task<bool> HasImmutableDetailReferenceAsync(
        AppDbContext db, IReadOnlyCollection<int> detailIds, CancellationToken cancellationToken)
    {
        if (detailIds.Count == 0)
            return false;
        return await ImmutableDetailIdsAsync(db, detailIds, cancellationToken) is { Count: > 0 };
    }

    private static async Task<HashSet<int>> ImmutableDetailIdsAsync(
        AppDbContext db, IReadOnlyCollection<int> detailIds, CancellationToken cancellationToken)
    {
        if (detailIds.Count == 0)
            return [];
        var ids = await db.ProductionMaterialMovements.AsNoTracking()
            .Where(x => x.InventoryBatchDetailId != null && detailIds.Contains(x.InventoryBatchDetailId.Value))
            .Select(x => x.InventoryBatchDetailId!.Value)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }
}

public static class TransactionDeleteApplicator
{
    public static async Task<TransactionDeleteDecision> DecideAsync(
        AppDbContext db, TransactionDeleteSubject subject, CancellationToken cancellationToken = default) =>
        await new TransactionDeletePolicyService().EvaluateAsync(db, subject, cancellationToken);

    public static async Task<string?> ApplyInventoryBatchAsync(
        AppDbContext db,
        IvTrxBatch batch,
        string ownerType,
        string? userId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (batch.DeletedAtUtc is not null)
            return null;

        var decision = await DecideAsync(
            db,
            new TransactionDeleteSubject(
                batch.CompanyCode,
                batch.BranchCode,
                ownerType,
                batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                batch.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            cancellationToken);
        if (decision.Mode == TransactionDeleteMode.Block)
            return decision.BlockingReason ?? TransactionDeleteMessages.NotDeletableStatus;
        if (decision.Mode == TransactionDeleteMode.ArchiveHistorical
            || !await InventoryBatchRetention.TryRemoveTrueDraftAsync(db, batch, cancellationToken))
        {
            InventoryBatchRetention.Archive(batch, userId, reason);
        }

        return null;
    }
}
