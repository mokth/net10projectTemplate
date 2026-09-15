using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Shared bulk delete for the sales item family.
///
/// Plan §12.2: no database cascade and no silent deletes — every delete re-validates the key, the
/// company, the row version and the reference count, then removes the rows in one transaction. A
/// reference hit blocks the whole batch (the caller gets the reference list back through
/// <c>DeleteCheckResult</c>).
/// </summary>
public sealed partial class SaSalesRefService
{
    private async Task<IvMasterOperationResult<object>> DeleteItemFamilyAsync<TEntity>(
        string menuCode,
        IReadOnlyList<SaItemFamilyKeyToken> items,
        Func<AppDbContext, string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<TEntity>>> load,
        Func<TEntity, string, bool> match,
        Func<AppDbContext, string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>>> probe,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var tokens = (items ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Key))
            .GroupBy(i => i.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (tokens.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var company = ctx.CompanyCode!;
        var keys = tokens.Select(t => t.Key.Trim()).ToList();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await load(db, company, keys, cancellationToken);
            if (entities.Count != tokens.Count)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
            }

            foreach (var token in tokens)
            {
                var entity = entities.FirstOrDefault(e => match(e, token.Key));
                if (entity is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                var entry = db.Entry(entity);
                if (entry.Property("RowVersion") is null)
                {
                    continue;
                }

                var current = entry.Property("RowVersion").CurrentValue as byte[];
                if (!RowVersionsEqual(current, token.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                entry.Property("RowVersion").OriginalValue = token.RowVersion;
            }

            var refs = await probe(db, company, keys, cancellationToken);
            var check = BuildDeleteCheck(keys, refs);
            if (!check.CanDelete)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<object>.Fail(
                    IvMasterErrorCode.InUse,
                    check.Message ?? "One or more records are in use.",
                    deleteCheck: check);
            }

            foreach (var entity in entities)
            {
                db.Remove(entity);
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
        }
        catch (DbUpdateException ex) when (IsForeignKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.InUse, "One or more records are in use.");
        }
    }
}
