using System.Data;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// <c>SaDisGroupItem</c> — item discount rules (item + quantity band + date window + two discount slots).
///
/// Plan: plans/sales-item-family-v2-plan.md §8. The rule that matters here is <b>at most one rule may
/// match an item</b>: a new or edited rule that would overlap an existing one is rejected inside a
/// Serializable transaction (§10, the <c>SaLMW</c> precedent), so the future pricing engine never has to
/// guess between two matching rows.
/// </summary>
public sealed partial class SaSalesRefService
{
    private const int DiscountTypeMax = 10;
    private const int EffectPriceMax = 10;
    private const decimal MaxDiscountPercent = 100m;

    public async Task<IvMasterOperationResult<IReadOnlyList<SaDisGroupItemListRow>>> ListDisGroupItemsAsync(
        CancellationToken cancellationToken = default) =>
        await ListDisGroupItemsCoreAsync(PermissionCodes.Access, takeAll: false, cancellationToken);

    public async Task<IvMasterOperationResult<IReadOnlyList<SaDisGroupItemListRow>>> ExportDisGroupItemsAsync(
        CancellationToken cancellationToken = default) =>
        await ListDisGroupItemsCoreAsync(PermissionCodes.Export, takeAll: true, cancellationToken);

    private async Task<IvMasterOperationResult<IReadOnlyList<SaDisGroupItemListRow>>> ListDisGroupItemsCoreAsync(
        string permission,
        bool takeAll,
        CancellationToken cancellationToken)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesDisGroupItem, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<SaDisGroupItemListRow>(ctx.Error.Value);
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.SaDisGroupItems.AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .OrderBy(x => x.ICode).ThenBy(x => x.QtyFr).ThenBy(x => x.DateFr);

        var rows = await (takeAll ? query.Take(MaxExportRows) : query)
            .Select(x => new SaDisGroupItemListRow
            {
                Id = x.Id,
                ICode = x.ICode,
                IDesc = x.IDesc,
                IClass = x.IClass,
                QtyFr = x.QtyFr,
                QtyTo = x.QtyTo,
                DateFr = x.DateFr,
                DateTo = x.DateTo,
                Discount = x.Discount,
                DiscountType = x.DiscountType,
                Discount1 = x.Discount1,
                DiscountType1 = x.DiscountType1,
                EffectPrice = x.EffectPrice,
                RowVersion = x.RowVersion ?? System.Array.Empty<byte>()
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<SaDisGroupItemListRow>>.Ok(rows);
    }

    public async Task<IvMasterOperationResult<SaDisGroupItemEditVm>> GetDisGroupItemAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesDisGroupItem, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaDisGroupItemEditVm>(ctx.Error.Value);
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SaDisGroupItems.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.Id == id, cancellationToken);
        if (entity is null)
        {
            return FailVm<SaDisGroupItemEditVm>(IvMasterErrorCode.NotFound, "Item discount rule not found.");
        }

        return IvMasterOperationResult<SaDisGroupItemEditVm>.Ok(new SaDisGroupItemEditVm
        {
            Id = entity.Id,
            ICode = entity.ICode,
            IDesc = entity.IDesc,
            IClass = entity.IClass,
            QtyFr = entity.QtyFr,
            QtyTo = entity.QtyTo,
            DateFr = entity.DateFr,
            DateTo = entity.DateTo,
            Discount = entity.Discount,
            DiscountType = entity.DiscountType,
            Discount1 = entity.Discount1,
            DiscountType1 = entity.DiscountType1,
            EffectPrice = entity.EffectPrice,
            RowVersion = entity.RowVersion ?? []
        });
    }

    public async Task<IvMasterOperationResult<SaDisGroupItemEditVm>> SaveDisGroupItemAsync(
        SaDisGroupItemEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<SaDisGroupItemEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesDisGroupItem, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaDisGroupItemEditVm>(ctx.Error.Value);
        }

        var company = ctx.CompanyCode!;
        var errors = NewErrors();

        var iCode = ValidateAndNormalizeCode(errors, nameof(model.ICode), "Item", model.ICode, ItemCodeMax);
        var iClass = NormalizeOptionalCode(model.IClass);
        if (iClass.Length > ClassMax)
        {
            errors[nameof(model.IClass)] = $"Item class must be at most {ClassMax} characters.";
        }

        // §8.3 quantity band: 0/0 is rejected (the legacy "all qty" row can never match either lookup).
        if (model.QtyFr <= 0m)
        {
            errors[nameof(model.QtyFr)] = "Quantity from must be greater than zero.";
        }

        if (model.QtyTo < model.QtyFr)
        {
            errors[nameof(model.QtyTo)] = "Quantity to must be greater than or equal to quantity from.";
        }

        if (model.DateFr == default)
        {
            errors[nameof(model.DateFr)] = "Effective from date is required.";
        }

        if (model.DateTo is not null && model.DateTo.Value.Date < model.DateFr.Date)
        {
            errors[nameof(model.DateTo)] = "Effective to date cannot be before the effective from date.";
        }

        var discountType = NormalizeOptionalCode(model.DiscountType);
        var discountType1 = NormalizeOptionalCode(model.DiscountType1);
        if (!SaDiscountSlotTypes.IsValid(discountType, required: model.Discount is > 0m))
        {
            errors[nameof(model.DiscountType)] = "Discount 1 type must be PERCENTAGE or AMOUNT.";
        }

        if (!SaDiscountSlotTypes.IsValid(discountType1, required: model.Discount1 is > 0m))
        {
            errors[nameof(model.DiscountType1)] = "Discount 2 type must be PERCENTAGE or AMOUNT.";
        }

        ValidateDiscountSlot(errors, nameof(model.Discount), "Discount 1", model.Discount, discountType);
        ValidateDiscountSlot(errors, nameof(model.Discount1), "Discount 2", model.Discount1, discountType1);

        var effectPrice = NormalizeOptionalCode(model.EffectPrice);
        if (!SaEffectPriceOptions.IsValid(effectPrice))
        {
            errors[nameof(model.EffectPrice)] = "Effect price must be DEALER or SELLING.";
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        if (iCode.Length > 0)
        {
            var items = await ActiveItemCodesAsync(db, company, cancellationToken);
            if (!items.Contains(iCode))
            {
                errors[nameof(model.ICode)] = $"Item {iCode} does not exist or is not active.";
            }
        }

        if (iClass.Length > 0)
        {
            var classes = await ActiveClassCodesAsync(db, company, cancellationToken);
            if (!classes.Contains(iClass))
            {
                errors[nameof(model.IClass)] = $"Item class {iClass} does not exist.";
            }
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<SaDisGroupItemEditVm>.Fail(IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<SaDisGroupItemEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        var now = DateTime.UtcNow;
        var user = ctx.UserId!;

        // Serializable: two operators must not be able to create two overlapping rules at once.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var others = await db.SaDisGroupItems
                .Where(x => x.CompanyCode == company && x.ICode == iCode && (!isNew || x.Id != model.Id))
                .ToListAsync(cancellationToken);

            if (!isNew)
            {
                others = others.Where(x => x.Id != model.Id).ToList();
            }

            var conflict = others.FirstOrDefault(x =>
                BandsOverlap(model.QtyFr, model.QtyTo, x.QtyFr, x.QtyTo) &&
                WindowsOverlap(model.DateFr, model.DateTo, x.DateFr, x.DateTo) &&
                ClassesOverlap(iClass, x.IClass));

            if (conflict is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailVm<SaDisGroupItemEditVm>(
                    IvMasterErrorCode.Validation,
                    $"This rule overlaps an existing rule for item {iCode} (qty {conflict.QtyFr:0.####}-{conflict.QtyTo:0.####}, from {conflict.DateFr:yyyy-MM-dd}). Adjust the band or the window so only one rule can match.",
                    nameof(model.QtyFr));
            }

            if (isNew)
            {
                var entity = new SaDisGroupItem
                {
                    CompanyCode = company,
                    ICode = iCode,
                    IDesc = model.IDesc?.Trim().ToUpperInvariant(),
                    IClass = iClass.Length == 0 ? null : iClass,
                    QtyFr = model.QtyFr,
                    QtyTo = model.QtyTo,
                    DateFr = model.DateFr.Date,
                    DateTo = model.DateTo?.Date,
                    Discount = model.Discount,
                    DiscountType = discountType.Length == 0 ? null : discountType,
                    Discount1 = model.Discount1,
                    DiscountType1 = discountType1.Length == 0 ? null : discountType1,
                    EffectPrice = effectPrice.Length == 0 ? null : effectPrice,
                    // Legacy compatibility column: written, never authoritative.
                    GroupStatus = "NEW",
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.SaDisGroupItems.Add(entity);
            }
            else
            {
                var entity = await db.SaDisGroupItems
                    .FirstOrDefaultAsync(x => x.CompanyCode == company && x.Id == model.Id, cancellationToken)
                    ?? null!;
                if (entity is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailVm<SaDisGroupItemEditVm>(IvMasterErrorCode.NotFound, "Item discount rule not found.");
                }

                var entry = db.Entry(entity);
                var current = entry.Property("RowVersion").CurrentValue as byte[];
                if (!RowVersionsEqual(current, model.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailVm<SaDisGroupItemEditVm>(IvMasterErrorCode.Concurrency, "This rule was changed by another user. Reload and try again.");
                }

                entry.Property("RowVersion").OriginalValue = model.RowVersion;
                entity.ICode = iCode;
                entity.IDesc = model.IDesc?.Trim().ToUpperInvariant();
                entity.IClass = iClass.Length == 0 ? null : iClass;
                entity.QtyFr = model.QtyFr;
                entity.QtyTo = model.QtyTo;
                entity.DateFr = model.DateFr.Date;
                entity.DateTo = model.DateTo?.Date;
                entity.Discount = model.Discount;
                entity.DiscountType = discountType.Length == 0 ? null : discountType;
                entity.Discount1 = model.Discount1;
                entity.DiscountType1 = discountType1.Length == 0 ? null : discountType1;
                entity.EffectPrice = effectPrice.Length == 0 ? null : effectPrice;
                entity.ModifiedDate = now;
                entity.ModifiedBy = user;
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm<SaDisGroupItemEditVm>(IvMasterErrorCode.Concurrency, "This rule was changed by another user. Reload and try again.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm<SaDisGroupItemEditVm>(IvMasterErrorCode.DuplicateKey, "A rule with the same item, class, band and start date already exists.", nameof(model.QtyFr));
        }
        catch (Exception ex) when (IsSerializationConflict(ex))
        {
            // The Serializable range lock is the only thing that can stop two operators creating two
            // overlapping rules — so a deadlock victim here is an expected outcome, not a crash (§10).
            // Never retry silently: a silent retry would hide the overlap from the operator.
            await tx.RollbackAsync(cancellationToken);
            return FailVm<SaDisGroupItemEditVm>(
                IvMasterErrorCode.Concurrency,
                "Another user just saved a rule for this item. Reload and try again.",
                nameof(model.QtyFr));
        }

        if (isNew)
        {
            var created = await db.SaDisGroupItems.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.ICode == iCode && x.QtyFr == model.QtyFr
                            && x.QtyTo == model.QtyTo && x.DateFr == model.DateFr.Date)
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (created is not null)
            {
                return await GetDisGroupItemAsync(created.Id, cancellationToken);
            }
        }

        return await GetDisGroupItemAsync(model.Id, cancellationToken);
    }

    private static void ValidateDiscountSlot(
        Dictionary<string, string> errors,
        string field,
        string label,
        decimal? value,
        string type)
    {
        if (value is null)
        {
            return;
        }

        if (value < 0m)
        {
            errors[field] = $"{label} cannot be negative.";
            return;
        }

        if (string.Equals(type, SaDiscountSlotTypes.Percentage, StringComparison.OrdinalIgnoreCase) && value > MaxDiscountPercent)
        {
            errors[field] = $"{label} percentage cannot exceed {MaxDiscountPercent:0}.";
        }
    }

    /// <summary>
    /// Band/date/class overlap for the save-time rule. The definitions live in
    /// <see cref="SaItemFamilyRuleMatch"/> so the validator, the resolver and the tests cannot drift
    /// apart (§8.3, §9.1).
    /// </summary>
    private static bool BandsOverlap(decimal aFrom, decimal aTo, decimal bFrom, decimal bTo) =>
        SaItemFamilyRuleMatch.BandsOverlap(aFrom, aTo, bFrom, bTo);

    /// <summary>Inclusive window match; a NULL end date is open-ended.</summary>
    private static bool WindowsOverlap(DateTime aFrom, DateTime? aTo, DateTime bFrom, DateTime? bTo) =>
        SaItemFamilyRuleMatch.WindowsOverlap(aFrom, aTo, bFrom, bTo);

    /// <summary>A blank class applies to all classes, so it overlaps any class-specific rule.</summary>
    private static bool ClassesOverlap(string? left, string? right) =>
        SaItemFamilyRuleMatch.ClassesOverlap(left, right);

    /// <summary>No consumer reads item discount rules yet, so the answer says so explicitly (§10.5).</summary>
    public async Task<DeleteCheckResult> CanDeleteDisGroupItemsAsync(
        IReadOnlyList<int> ids,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesDisGroupItem, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        if (ids is null || ids.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        return DeleteCheckResult.Ok(
            "No module consumes item discount rules yet, so no reference check is possible. The rule will be deleted.");
    }

    public Task<IvMasterOperationResult<object>> DeleteDisGroupItemsAsync(
        IReadOnlyList<SaItemFamilyKeyToken> items,
        CancellationToken cancellationToken = default) =>
        DeleteItemFamilyAsync<SaDisGroupItem>(
            MenuCodes.SalesDisGroupItem,
            items,
            async (db, company, codes, ct) =>
            {
                var ids = codes
                    .Select(c => int.TryParse(c, out var id) ? id : -1)
                    .Where(id => id > 0)
                    .ToList();
                return await db.SaDisGroupItems
                    .Where(x => x.CompanyCode == company && ids.Contains(x.Id))
                    .ToListAsync(ct);
            },
            (entity, key) => string.Equals(entity.Id.ToString(), key.Trim(), StringComparison.OrdinalIgnoreCase),
            (_, _, _, _) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>>(
                new Dictionary<string, IReadOnlyList<IvReferenceCount>>(StringComparer.OrdinalIgnoreCase)),
            cancellationToken);
}
