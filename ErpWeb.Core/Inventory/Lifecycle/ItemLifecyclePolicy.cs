using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory.Lifecycle;

/// <summary>
/// The single authoritative item lifecycle policy. Every module that changes an item's status or
/// creates new demand asks THIS type; no UI or module-local rule may diverge from it.
/// </summary>
public sealed class ItemLifecyclePolicy
{
    public static readonly ItemLifecyclePolicy Default = new();

    private readonly ItemRetirementEvaluator _retirementEvaluator = new();

    /// <summary>
    /// Read-only retirement preview for the UI and for pre-validation. It is never the final authority;
    /// the transition re-evaluates inside its own transaction.
    /// </summary>
    public Task<ItemRetirementCheckResult> PreviewRetirementAsync(
        AppDbContext db,
        string companyCode,
        string itemCode,
        CancellationToken cancellationToken = default) =>
        _retirementEvaluator.EvaluateAsync(db, companyCode, itemCode, DateTime.UtcNow, cancellationToken);

    /// <summary>
    /// Mandatory in-transaction retirement evaluation. Callers must already hold the item key lock in
    /// this transaction so nothing can be created between the check and the commit.
    /// </summary>
    public Task<ItemRetirementCheckResult> EvaluateRetirementInTransactionAsync(
        AppDbContext db,
        string companyCode,
        string itemCode,
        CancellationToken cancellationToken = default) =>
        _retirementEvaluator.EvaluateAsync(db, companyCode, itemCode, DateTime.UtcNow, cancellationToken);

    public ItemLifecycleTransitionResult ValidateTransition(
        IvStockItemLifecycleStatus from,
        IvStockItemLifecycleStatus to,
        string? reason,
        out string normalizedReason)
    {
        normalizedReason = string.Empty;

        if (from == to)
        {
            return new ItemLifecycleTransitionResult
            {
                Succeeded = true,
                NewStatus = to
            };
        }

        if (!IvStockItemLifecycle.IsTransitionAllowed(from, to))
        {
            return new ItemLifecycleTransitionResult
            {
                Succeeded = false,
                ErrorMessage = IvStockItemLifecycle.DescribeTransitionRejection(from, to)
            };
        }

        if (!IvStockItemLifecycle.TryNormalizeReason(reason, out normalizedReason, out var reasonError))
        {
            return new ItemLifecycleTransitionResult
            {
                Succeeded = false,
                ErrorMessage = reasonError
            };
        }

        return new ItemLifecycleTransitionResult
        {
            Succeeded = true,
            NewStatus = to
        };
    }

    /// <summary>
    /// Acquires the per-item SQL Server key lock used by posting (<c>UPDLOCK, HOLDLOCK</c>) so a status
    /// transition cannot race a new demand/balance insertion. On non-SQL-Server providers (tests) the
    /// command transaction itself serialises writers, matching the existing repository behaviour.
    /// </summary>
    public async Task<IvStockMaster?> LockItemAsync(
        AppDbContext db,
        string companyCode,
        string itemCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (itemCode ?? string.Empty).Trim();
        if (company.Length == 0 || code.Length == 0)
        {
            return null;
        }

        if (db.Database.IsSqlServer())
        {
            return await db.IvStockMasters
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.IvStockMaster WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND ICode = {code}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.IvStockMasters
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.ICode == code,
                cancellationToken);
    }

    /// <summary>
    /// Locks every stock item a document is about to demand, in a deterministic code order, and returns
    /// the current committed masters. Demand-creating write paths MUST call this inside their save
    /// transaction before evaluating <see cref="AuthorizeDemand"/>: a plain re-read under READ COMMITTED
    /// does not block a concurrent retirement, and would let new demand slip in after the precheck.
    /// Non-stock codes simply return no entry.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IvStockMaster>> LockDemandMastersAsync(
        AppDbContext db,
        string companyCode,
        IEnumerable<string?> itemCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var codes = (itemCodes ?? [])
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var masters = new Dictionary<string, IvStockMaster>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            var master = await LockItemAsync(db, companyCode, code, cancellationToken);
            if (master is not null)
            {
                masters[master.ICode] = master;
            }
        }

        return masters;
    }

    /// <summary>Loads item masters for a document validation loop in one query.</summary>
    public static async Task<IReadOnlyDictionary<string, IvStockMaster>> LoadMastersAsync(
        AppDbContext db,
        string companyCode,
        IEnumerable<string> itemCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var codes = (itemCodes ?? [])
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (company.Length == 0 || codes.Count == 0)
        {
            return new Dictionary<string, IvStockMaster>(StringComparer.OrdinalIgnoreCase);
        }

        var rows = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && codes.Contains(x.ICode))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(x => x.ICode, x => x, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Server-side demand authorization. The intent is derived from the real document identity by the
    /// caller — never from a browser flag such as <c>isExisting=true</c>.
    /// </summary>
    public async Task<ItemDemandPolicyResult> AuthorizeDemandAsync(
        AppDbContext db,
        string companyCode,
        string itemCode,
        ItemDemandIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (itemCode ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(code))
        {
            return ItemDemandPolicyResult.Reject(intent, "Item code is required.");
        }

        var item = await db.IvStockMasters.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.ICode == code,
                cancellationToken);

        return AuthorizeDemand(item, code, intent);
    }

    /// <summary>Pure overload for callers that already loaded the master in the same query.</summary>
    public ItemDemandPolicyResult AuthorizeDemand(
        IvStockMaster? item,
        string itemCode,
        ItemDemandIntent intent)
    {
        if (intent != ItemDemandIntent.NewDemand)
        {
            // Completion, correction, history and rollback keep their existing module rules. The
            // lifecycle layer must not introduce a blanket inactive/rollback prohibition.
            return ItemDemandPolicyResult.Allow(intent);
        }

        if (item is null)
        {
            return ItemDemandPolicyResult.Reject(intent, $"Item '{itemCode}' was not found.");
        }

        if (!IvStockItemLifecycle.TryResolve(item.IsActive, item.IsDiscontinued, out var status))
        {
            return ItemDemandPolicyResult.Reject(
                intent,
                $"Item '{itemCode}' has an invalid lifecycle state and cannot be used.");
        }

        return status switch
        {
            IvStockItemLifecycleStatus.Active => ItemDemandPolicyResult.Allow(intent),
            IvStockItemLifecycleStatus.Discontinued => ItemDemandPolicyResult.Reject(
                intent,
                $"Item '{itemCode}' is discontinued and cannot be used for new demand. Existing obligations can still be completed."),
            _ => ItemDemandPolicyResult.Reject(
                intent,
                $"Item '{itemCode}' is inactive and cannot be used for new demand.")
        };
    }

    /// <summary>
    /// Classifies a document line for demand authorization. A brand-new line, a changed item or an
    /// increased quantity is <see cref="ItemDemandIntent.NewDemand"/>; repeating or reducing a
    /// persisted line is <see cref="ItemDemandIntent.ExistingFulfilment"/>. The facts come from the
    /// persisted document line, never from a browser flag.
    /// </summary>
    public static ItemDemandIntent ClassifyLineIntent(
        bool isExistingLine,
        bool sameItem,
        decimal previousQuantity,
        decimal newQuantity) =>
        !isExistingLine || !sameItem || newQuantity > previousQuantity
            ? ItemDemandIntent.NewDemand
            : ItemDemandIntent.ExistingFulfilment;

    /// <summary>Convenience for validation loops over an already-loaded master dictionary.</summary>
    public string? FindNewDemandError(
        IReadOnlyDictionary<string, IvStockMaster> masters,
        string itemCode)
    {
        masters.TryGetValue(itemCode, out var item);
        var result = AuthorizeDemand(item, itemCode, ItemDemandIntent.NewDemand);
        return result.Allowed ? null : result.ErrorMessage;
    }

    /// <summary>
    /// Writes one append-only audit row. Callers must add this inside the same transaction as the
    /// status change; the row is never updated or deleted afterwards.
    /// </summary>
    public static void AppendAudit(
        AppDbContext db,
        string companyCode,
        string itemCode,
        IvStockItemLifecycleStatus oldStatus,
        IvStockItemLifecycleStatus newStatus,
        DateTime occurredAtUtc,
        string actorUserId,
        string reason,
        string sourceAction,
        string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (oldStatus == newStatus)
        {
            return;
        }

        db.IvStockMasterLifecycleEvents.Add(new IvStockMasterLifecycleEvent
        {
            CompanyCode = (companyCode ?? string.Empty).Trim(),
            ICode = (itemCode ?? string.Empty).Trim(),
            OldStatus = IvStockItemLifecycle.CodeOf(oldStatus),
            NewStatus = IvStockItemLifecycle.CodeOf(newStatus),
            OccurredAtUtc = occurredAtUtc,
            ActorUserId = (actorUserId ?? string.Empty).Trim(),
            Reason = reason,
            SourceAction = sourceAction,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? null : correlationId.Trim()
        });
    }

    /// <summary>Applies a status to the entity using the single flag mapper.</summary>
    public static void ApplyStatus(IvStockMaster entity, IvStockItemLifecycleStatus status, DateTime utcNow)
    {
        var (isActive, isDiscontinued) = IvStockItemLifecycle.ToFlags(status);
        entity.IsActive = isActive;
        entity.IsDiscontinued = isDiscontinued;
        if (isDiscontinued)
        {
            // Capture the cutover boundary once; a later discontinue cycle writes a new audit row.
            entity.DiscontinuedAtUtc ??= utcNow;
        }
        else if (status == IvStockItemLifecycleStatus.Active)
        {
            // Reactivation clears the cutover boundary; the audit table keeps the history.
            entity.DiscontinuedAtUtc = null;
        }
    }
}
