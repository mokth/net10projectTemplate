using System.Data;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Pricing;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Sales;

public sealed partial class SaPriceMaintenanceService
{
    private async Task<IvMasterOperationResult<SaPriceApplyResult>> ApplyItemDefaultsAsync(
        TenantScope writeScope,
        SaPriceApplyRequest request,
        CancellationToken cancellationToken)
    {
        var selections = request.Selections ?? [];
        var parsed = selections
            .Select(x => (Selection: x, Code: ParseItemDefaultKey(x.ReviewRowKey)))
            .ToList();
        if (parsed.Any(x => x.Code is null))
        {
            return FailApply(IvMasterErrorCode.Validation, "One or more selected Item Default rows has an invalid identity.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var codes = parsed
                .Select(x => x.Code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var entities = await db.IvStockMasters
                .Where(x => x.CompanyCode == writeScope.CompanyCode && codes.Contains(x.ICode))
                .ToListAsync(cancellationToken);
            var byCode = entities.ToDictionary(x => x.ICode, StringComparer.OrdinalIgnoreCase);

            var pending = new List<ItemDefaultPending>();
            foreach (var entry in parsed)
            {
                var code = entry.Code!;
                if (!byCode.TryGetValue(code, out var item))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailApply(IvMasterErrorCode.Concurrency, $"Item {code} no longer exists. Reload prices before applying.");
                }

                var concurrencyError = ValidateRowVersion(item.RowVersion, entry.Selection.RowVersion, code);
                if (concurrencyError is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailApply(IvMasterErrorCode.Concurrency, concurrencyError);
                }

                var stalePrice = ValidateBaselinePrice(item.SellingPrice, entry.Selection.BaselinePrice, code);
                if (stalePrice is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailApply(IvMasterErrorCode.Concurrency, stalePrice);
                }

                var newPriceError = ValidateNewPrice(entry.Selection.NewPrice, code);
                if (newPriceError is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailApply(IvMasterErrorCode.Validation, newPriceError);
                }

                if (item.SellingPrice != entry.Selection.NewPrice)
                {
                    pending.Add(new ItemDefaultPending(entry.Selection, item, item.SellingPrice, entry.Selection.NewPrice!.Value));
                }
            }

            if (pending.Count == 0)
            {
                await tx.CommitAsync(cancellationToken);
                return ApplyResult(null, 0);
            }

            var now = _dates.Now;
            var auditLines = new List<SalesPriceChangeAuditLine>(pending.Count);
            foreach (var change in pending)
            {
                db.Entry(change.Entity).Property(x => x.RowVersion).OriginalValue = change.Selection.RowVersion;
                change.Entity.SellingPrice = change.NewPrice;
                change.Entity.ModifiedDate = now;
                change.Entity.ModifiedBy = writeScope.UserId;
                auditLines.Add(new SalesPriceChangeAuditLine(
                    SalesPriceChangeKinds.Update,
                    change.Entity.ICode,
                    change.Entity.IDesc,
                    change.Entity.IType,
                    change.Entity.IClassCode,
                    change.Entity.ISubClassCode,
                    change.Entity.Brand,
                    OldUom: change.Entity.SellingUom,
                    NewUom: change.Entity.SellingUom,
                    OldPrice: change.OldPrice,
                    NewPrice: change.NewPrice));
            }

            var batch = BuildWorkbenchBatch(
                writeScope,
                request,
                SaPriceMaintenanceTargets.ItemDefault,
                _dates.Today,
                auditLines);
            db.SaPriceChangeBatches.Add(batch);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return ApplyResult(batch, pending.Count);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailApply(IvMasterErrorCode.Concurrency, "One or more items was changed by another user. Reload prices before applying.");
        }
        catch (Exception ex) when (SqlErrorClassifier.IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailApply(IvMasterErrorCode.DuplicateKey, "A price change conflicted with another save. Reload prices before applying.");
        }
        catch (Exception ex) when (SqlErrorClassifier.IsSerializationConflict(ex))
        {
            await RollbackQuietlyAsync(tx);
            return FailApply(IvMasterErrorCode.Concurrency, "The price data was changed concurrently. Reload prices before applying.");
        }
        catch (Exception ex)
        {
            await RollbackQuietlyAsync(tx);
            _logger?.LogError(
                ex,
                "Sales price bulk apply failed while saving item defaults. Company={CompanyCode}, SelectedCount={SelectedCount}, User={UserId}",
                writeScope.CompanyCode,
                request.Selections?.Count ?? 0,
                writeScope.UserId);
            return FailApply(
                IvMasterErrorCode.Validation,
                "The price change was not saved. No item-default rows were changed. Check the server log and database deployment, then try again.");
        }
    }

    private async Task<IvMasterOperationResult<SaPriceApplyResult>> ApplyCustomerItemsAsync(
        TenantScope writeScope,
        SaPriceApplyRequest request,
        CancellationToken cancellationToken)
    {
        var selections = request.Selections ?? [];
        var parsed = selections
            .Select(x => (Selection: x, Key: ParseCustomerItemKey(x.ReviewRowKey)))
            .ToList();
        if (parsed.Any(x => x.Key is null))
        {
            return FailApply(IvMasterErrorCode.Validation, "One or more selected Customer Special rows has an invalid identity.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var keys = parsed.Select(x => x.Key!.Value).ToList();
            var customerCodes = keys.Select(x => x.CustCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var itemCodes = keys.Select(x => x.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var entities = await db.SaItemCusts
                .Where(x => x.CompanyCode == writeScope.CompanyCode
                            && customerCodes.Contains(x.CustCode)
                            && itemCodes.Contains(x.ICode))
                .ToListAsync(cancellationToken);
            var byKey = entities.ToDictionary(
                x => CustomerItemKey(x.CustCode, x.ICode, x.SellingUOM, x.MOQ),
                StringComparer.OrdinalIgnoreCase);

            var itemMap = await db.IvStockMasters.AsNoTracking()
                .Where(x => x.CompanyCode == writeScope.CompanyCode && itemCodes.Contains(x.ICode))
                .ToDictionaryAsync(x => x.ICode, StringComparer.OrdinalIgnoreCase, cancellationToken);
            var customerMap = await db.SaCusts.AsNoTracking()
                .Where(x => x.CompanyCode == writeScope.CompanyCode && customerCodes.Contains(x.CustCode))
                .ToDictionaryAsync(x => x.CustCode, StringComparer.OrdinalIgnoreCase, cancellationToken);

            var pending = new List<CustomerItemPending>();
            foreach (var entry in parsed)
            {
                var key = entry.Key!.Value;
                var encoded = CustomerItemKey(key.CustCode, key.ItemCode, key.Uom, key.Moq);
                if (!byKey.TryGetValue(encoded, out var special))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailApply(IvMasterErrorCode.Concurrency, $"Customer Special row {encoded} no longer exists. Reload prices before applying.");
                }

                var concurrencyError = ValidateRowVersion(special.RowVersion, entry.Selection.RowVersion, encoded);
                if (concurrencyError is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailApply(IvMasterErrorCode.Concurrency, concurrencyError);
                }

                var oldPrice = SalesPriceChangeAuditFactory.NormalizeLegacyMoney(special.UnitPrice);
                var stalePrice = ValidateBaselinePrice(oldPrice, entry.Selection.BaselinePrice, special.ICode);
                if (stalePrice is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailApply(IvMasterErrorCode.Concurrency, stalePrice);
                }

                var newPriceError = ValidateNewPrice(entry.Selection.NewPrice, special.ICode);
                if (newPriceError is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailApply(IvMasterErrorCode.Validation, newPriceError);
                }

                var newPrice = entry.Selection.NewPrice!.Value;
                if (oldPrice != newPrice)
                {
                    pending.Add(new CustomerItemPending(
                        entry.Selection,
                        special,
                        oldPrice,
                        newPrice,
                        itemMap.TryGetValue(special.ICode, out var item) ? item : null,
                        customerMap.TryGetValue(special.CustCode, out var customer) ? customer : null));
                }
            }

            if (pending.Count == 0)
            {
                await tx.CommitAsync(cancellationToken);
                return ApplyResult(null, 0);
            }

            var now = _dates.Now;
            var auditLines = new List<SalesPriceChangeAuditLine>(pending.Count);
            foreach (var change in pending)
            {
                db.Entry(change.Entity).Property(x => x.RowVersion).OriginalValue = change.Selection.RowVersion;
                change.Entity.UnitPrice = SalesPriceChangeAuditFactory.UnscaleLegacyMoney(change.NewPrice);
                change.Entity.ModifiedDate = now;
                change.Entity.ModifiedBy = writeScope.UserId;

                auditLines.Add(new SalesPriceChangeAuditLine(
                    SalesPriceChangeKinds.Update,
                    change.Entity.ICode,
                    change.Item?.IDesc ?? change.Entity.IDesc,
                    change.Item?.IType,
                    change.Item?.IClassCode,
                    change.Item?.ISubClassCode,
                    change.Item?.Brand,
                    CustCode: change.Entity.CustCode,
                    CustomerNameSnapshot: change.Customer?.CustName,
                    CustomerTypeSnapshot: change.Customer?.CustType,
                    CustomerGroupSnapshot: change.Customer?.CustGroupCode,
                    Moq: change.Entity.MOQ,
                    OldUom: change.Entity.SellingUOM,
                    NewUom: change.Entity.SellingUOM,
                    OldCurrencyCode: change.Entity.Currency,
                    NewCurrencyCode: change.Entity.Currency,
                    OldPrice: change.OldPrice,
                    NewPrice: change.NewPrice));
            }

            var batch = BuildWorkbenchBatch(
                writeScope,
                request,
                SaPriceMaintenanceTargets.CustomerItem,
                _dates.Today,
                auditLines);
            db.SaPriceChangeBatches.Add(batch);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return ApplyResult(batch, pending.Count);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailApply(IvMasterErrorCode.Concurrency, "One or more Customer Special rows was changed by another user. Reload prices before applying.");
        }
        catch (Exception ex) when (SqlErrorClassifier.IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailApply(IvMasterErrorCode.DuplicateKey, "A price change conflicted with another save. Reload prices before applying.");
        }
        catch (Exception ex) when (SqlErrorClassifier.IsSerializationConflict(ex))
        {
            await RollbackQuietlyAsync(tx);
            return FailApply(IvMasterErrorCode.Concurrency, "The price data was changed concurrently. Reload prices before applying.");
        }
        catch (Exception ex)
        {
            await RollbackQuietlyAsync(tx);
            _logger?.LogError(
                ex,
                "Sales price bulk apply failed while saving customer-item prices. Company={CompanyCode}, SelectedCount={SelectedCount}, User={UserId}",
                writeScope.CompanyCode,
                request.Selections?.Count ?? 0,
                writeScope.UserId);
            return FailApply(
                IvMasterErrorCode.Validation,
                "The price change was not saved. No customer-item rows were changed. Check the server log and database deployment, then try again.");
        }
    }

    private async Task<IvMasterOperationResult<SaPriceApplyResult>> ApplyPriceListsAsync(
        TenantScope writeScope,
        SaPriceApplyRequest request,
        CancellationToken cancellationToken)
    {
        var selections = request.Selections ?? [];
        var parsed = selections
            .Select(x => (Selection: x, Key: ParsePriceListKey(x.ReviewRowKey)))
            .ToList();
        if (parsed.Any(x => x.Key is null))
        {
            return FailApply(IvMasterErrorCode.Validation, "One or more selected Price List rows has an invalid identity.");
        }

        var updateMode = NormalizeToken(request.PriceListUpdateMode);
        if (updateMode is not SaPriceListUpdateModes.UpdateSelectedRow and not SaPriceListUpdateModes.ScheduleFromDate)
        {
            return FailApply(IvMasterErrorCode.Validation, "Select a supported Price List update mode.");
        }

        var effectiveDate = request.EffectiveFrom?.Date;
        if (updateMode == SaPriceListUpdateModes.ScheduleFromDate)
        {
            if (!effectiveDate.HasValue)
            {
                return FailApply(IvMasterErrorCode.Validation, "Schedule From Date is required for Price List scheduling.");
            }

            if (effectiveDate.Value < _dates.Today.Date)
            {
                return FailApply(IvMasterErrorCode.Validation, "Schedule From Date cannot be in the past.");
            }
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var priceCodes = parsed
                .Select(x => x.Key!.Value.Code)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var headers = await db.IvCustPriceGroups
                .Where(x => x.CompanyCode == writeScope.CompanyCode && priceCodes.Contains(x.CustPriceCode))
                .OrderBy(x => x.CustPriceCode)
                .ToListAsync(cancellationToken);
            var headersByCode = headers.ToDictionary(x => x.CustPriceCode, StringComparer.OrdinalIgnoreCase);
            if (headers.Count != priceCodes.Count)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailApply(IvMasterErrorCode.Concurrency, "One or more selected Price Lists no longer exists. Reload prices before applying.");
            }

            foreach (var entry in parsed)
            {
                var code = entry.Key!.Value.Code;
                if (!headersByCode.TryGetValue(code, out var header))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailApply(IvMasterErrorCode.Concurrency, $"Price List {code} no longer exists. Reload prices before applying.");
                }

                if (!RowVersionsEqual(header.RowVersion, entry.Selection.HeaderRowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailApply(IvMasterErrorCode.Concurrency, $"Price List {code} was changed by another user. Reload prices before applying.");
                }
            }

            var allLines = await db.IvCustPrices
                .Where(x => x.CompanyCode == writeScope.CompanyCode && priceCodes.Contains(x.CustPriceCode))
                .OrderBy(x => x.CustPriceCode)
                .ThenBy(x => x.ICode)
                .ThenBy(x => x.UOM)
                .ThenBy(x => x.MinQty)
                .ThenBy(x => x.ValidFrom)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);
            var linesById = allLines.ToDictionary(x => x.Id);
            var itemCodes = allLines.Select(x => x.ICode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var items = await db.IvStockMasters.AsNoTracking()
                .Where(x => x.CompanyCode == writeScope.CompanyCode && itemCodes.Contains(x.ICode))
                .ToDictionaryAsync(x => x.ICode, StringComparer.OrdinalIgnoreCase, cancellationToken);

            var pending = new List<PriceListPending>();
            foreach (var entry in parsed)
            {
                var key = entry.Key!.Value;
                if (!linesById.TryGetValue(key.Id, out var line)
                    || !string.Equals(line.CustPriceCode, key.Code, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailApply(IvMasterErrorCode.Concurrency, $"Price List row {key.Id} no longer exists. Reload prices before applying.");
                }

                var stalePrice = ValidateBaselinePrice(line.SellingPrice, entry.Selection.BaselinePrice, line.ICode);
                if (stalePrice is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailApply(IvMasterErrorCode.Concurrency, stalePrice);
                }

                var newPriceError = ValidateNewPrice(entry.Selection.NewPrice, line.ICode);
                if (newPriceError is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailApply(IvMasterErrorCode.Validation, newPriceError);
                }

                if (updateMode == SaPriceListUpdateModes.ScheduleFromDate)
                {
                    var scheduleError = ValidateSchedule(
                        new SaPriceReviewRow
                        {
                            ItemCode = line.ICode,
                            ValidFrom = line.ValidFrom,
                            ValidTo = line.ValidTo
                        },
                        effectiveDate,
                        _dates.Today);
                    if (scheduleError is not null)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return FailApply(IvMasterErrorCode.Validation, scheduleError);
                    }
                }

                if (line.SellingPrice != entry.Selection.NewPrice)
                {
                    pending.Add(new PriceListPending(
                        entry.Selection,
                        line,
                        headersByCode[key.Code],
                        entry.Selection.NewPrice!.Value,
                        effectiveDate,
                        items.TryGetValue(line.ICode, out var item) ? item : null));
                }
            }

            if (pending.Count == 0)
            {
                await tx.CommitAsync(cancellationToken);
                return ApplyResult(null, 0);
            }

            foreach (var header in headers)
            {
                var expected = parsed
                    .Where(x => string.Equals(x.Key!.Value.Code, header.CustPriceCode, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Selection.HeaderRowVersion)
                    .First();
                db.Entry(header).Property(x => x.RowVersion).OriginalValue = expected;
            }

            var auditLines = new List<SalesPriceChangeAuditLine>(pending.Count);
            var inventoryScope = ToInventoryScope(writeScope);
            var now = _dates.Now;
            foreach (var change in pending)
            {
                var oldSnapshot = PriceLineSnapshot.From(change.Entity);
                if (updateMode == SaPriceListUpdateModes.UpdateSelectedRow)
                {
                    change.Entity.SellingPrice = change.NewPrice;
                    change.Entity.ModifiedDate = now;
                    change.Entity.ModifiedBy = writeScope.UserId;
                    auditLines.Add(BuildWorkbenchPriceListAuditLine(
                        SalesPriceChangeKinds.Update,
                        oldSnapshot,
                        PriceLineSnapshot.From(change.Entity),
                        change.Header,
                        change.Entity.Id,
                        change.Item));
                    continue;
                }

                var scheduled = change.EffectiveFrom!.Value.Date;
                if (scheduled == change.Entity.ValidFrom.Date)
                {
                    change.Entity.SellingPrice = change.NewPrice;
                    change.Entity.ModifiedDate = now;
                    change.Entity.ModifiedBy = writeScope.UserId;
                    auditLines.Add(BuildWorkbenchPriceListAuditLine(
                        SalesPriceChangeKinds.Schedule,
                        oldSnapshot,
                        PriceLineSnapshot.From(change.Entity),
                        change.Header,
                        change.Entity.Id,
                        change.Item));
                    continue;
                }

                var successor = new IvCustPrice
                {
                    CompanyCode = change.Entity.CompanyCode,
                    CustPriceCode = change.Entity.CustPriceCode,
                    ICode = change.Entity.ICode,
                    UOM = change.Entity.UOM,
                    IDesc = change.Entity.IDesc,
                    CustPriceDesc = change.Header.CustPriceDesc,
                    SellingPrice = change.NewPrice,
                    SellPackSize = change.Entity.SellPackSize,
                    ValidFrom = scheduled,
                    ValidTo = change.Entity.ValidTo,
                    MinQty = change.Entity.MinQty,
                    MaxQty = change.Entity.MaxQty,
                    CurrencyCode = change.Entity.CurrencyCode,
                    CreatedDate = now,
                    CreatedBy = writeScope.UserId,
                    ModifiedDate = now,
                    ModifiedBy = writeScope.UserId
                };
                InventoryLeftoverSite.Apply(successor, inventoryScope);
                change.Entity.ValidTo = scheduled.AddDays(-1);
                change.Entity.ModifiedDate = now;
                change.Entity.ModifiedBy = writeScope.UserId;
                db.IvCustPrices.Add(successor);
                auditLines.Add(BuildWorkbenchPriceListAuditLine(
                    SalesPriceChangeKinds.Schedule,
                    oldSnapshot,
                    PriceLineSnapshot.From(successor),
                    change.Header,
                    change.Entity.Id,
                    change.Item));
            }

            foreach (var header in headers)
            {
                header.ModifiedDate = now;
                header.ModifiedBy = writeScope.UserId;
            }

            foreach (var priceCode in priceCodes)
            {
                var conflict = SaPriceListOverlapValidator.FindConflict(
                    allLines
                        .Where(x => string.Equals(x.CustPriceCode, priceCode, StringComparison.OrdinalIgnoreCase))
                        .Concat(db.ChangeTracker.Entries<IvCustPrice>()
                            .Where(x => x.State == EntityState.Added
                                        && string.Equals(x.Entity.CustPriceCode, priceCode, StringComparison.OrdinalIgnoreCase))
                            .Select(x => x.Entity))
                        .Select(x => new SaPriceListOverlapLine(
                            x.Id,
                            x.ICode,
                            x.UOM,
                            x.MinQty,
                            x.MaxQty,
                            x.ValidFrom,
                            x.ValidTo,
                            x.CurrencyCode))
                        .ToList());
                if (conflict is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailApply(IvMasterErrorCode.Validation, conflict);
                }
            }

            var batch = BuildWorkbenchBatch(
                writeScope,
                request,
                SaPriceMaintenanceTargets.PriceList,
                effectiveDate ?? _dates.Today,
                auditLines);
            db.SaPriceChangeBatches.Add(batch);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return ApplyResult(batch, pending.Count);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailApply(IvMasterErrorCode.Concurrency, "One or more Price Lists was changed by another user. Reload prices before applying.");
        }
        catch (Exception ex) when (SqlErrorClassifier.IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailApply(IvMasterErrorCode.DuplicateKey, "The scheduled price conflicts with an existing price row. Reload prices and review the validity window.");
        }
        catch (Exception ex) when (SqlErrorClassifier.IsSerializationConflict(ex))
        {
            await RollbackQuietlyAsync(tx);
            return FailApply(IvMasterErrorCode.Concurrency, "The price list was changed concurrently. Reload prices before applying.");
        }
        catch (Exception ex)
        {
            await RollbackQuietlyAsync(tx);
            _logger?.LogError(
                ex,
                "Sales price bulk apply failed while saving price-list prices. Company={CompanyCode}, SelectedCount={SelectedCount}, User={UserId}",
                writeScope.CompanyCode,
                request.Selections?.Count ?? 0,
                writeScope.UserId);
            return FailApply(
                IvMasterErrorCode.Validation,
                "The price change was not saved. No price-list rows were changed. Check the server log and database deployment, then try again.");
        }
    }

    private static SaPriceChangeBatch BuildWorkbenchBatch(
        TenantScope writeScope,
        SaPriceApplyRequest request,
        string target,
        DateTime effectiveDate,
        IReadOnlyList<SalesPriceChangeAuditLine> lines)
    {
        var scope = request.ReviewScope;
        return SalesPriceChangeAuditFactory.BuildBatch(
            new SalesPriceChangeAuditContext(
                writeScope.CompanyCode,
                SalesPriceChangeAuditOrigins.PriceReviewWorkbench,
                target,
                effectiveDate,
                writeScope.UserId,
                AdjustmentMethod: NormalizeToken(request.AdjustmentMethod),
                AdjustmentValue: request.AdjustmentValue,
                RoundingMode: NormalizeToken(request.RoundingMode),
                DecimalPlaces: request.DecimalPlaces,
                Reason: request.Reason,
                ItemSearchFilter: scope?.ItemSearch,
                ItemTypeFilter: scope?.ItemType,
                ItemClassFilter: scope?.ItemClass,
                ItemSubClassFilter: scope?.ItemSubClass,
                BrandFilter: scope?.Brand,
                CustCodeFilter: scope?.CustCode,
                CustTypeFilter: scope?.CustType,
                CustGroupFilter: scope?.CustGroupCode ?? scope?.CustGroup,
                CustPriceCodeFilter: scope?.CustPriceCode,
                CurrencyFilter: scope?.CurrencyCode,
                UomFilter: scope?.Uom),
            lines);
    }

    private static SalesPriceChangeAuditLine BuildWorkbenchPriceListAuditLine(
        string changeKind,
        PriceLineSnapshot oldLine,
        PriceLineSnapshot newLine,
        IvCustPriceGroup header,
        int sourceLineId,
        IvStockMaster? item)
    {
        var itemCode = newLine.ICode ?? oldLine.ICode;
        return new SalesPriceChangeAuditLine(
            changeKind,
            itemCode,
            item?.IDesc ?? newLine.IDesc ?? oldLine.IDesc,
            item?.IType,
            item?.IClassCode,
            item?.ISubClassCode,
            item?.Brand,
            CustPriceCode: header.CustPriceCode,
            PriceListDescriptionSnapshot: header.CustPriceDesc,
            SourcePriceListLineId: sourceLineId,
            OldUom: oldLine.Uom,
            NewUom: newLine.Uom,
            OldCurrencyCode: oldLine.CurrencyCode,
            NewCurrencyCode: newLine.CurrencyCode,
            OldMinQty: oldLine.MinQty,
            OldMaxQty: oldLine.MaxQty,
            NewMinQty: newLine.MinQty,
            NewMaxQty: newLine.MaxQty,
            OldValidFrom: oldLine.ValidFrom,
            OldValidTo: oldLine.ValidTo,
            NewValidFrom: newLine.ValidFrom,
            NewValidTo: newLine.ValidTo,
            OldPrice: oldLine.Price,
            NewPrice: newLine.Price);
    }

    private static string? ValidateRowVersion(byte[] actual, byte[] expected, string label)
    {
        return RowVersionsEqual(actual, expected)
            ? null
            : $"{label} was changed by another user. Reload prices before applying.";
    }

    private static string? ValidateBaselinePrice(decimal? actual, decimal? expected, string itemCode)
    {
        return actual == expected
            ? null
            : $"{itemCode} price changed from {FormatPrice(expected)} to {FormatPrice(actual)} after this review was loaded. Reload prices before applying.";
    }

    private static string? ValidateNewPrice(decimal? value, string itemCode)
    {
        if (!value.HasValue)
        {
            return $"New price is required for {itemCode}.";
        }

        if (value.Value < 0m)
        {
            return $"New price cannot be negative for {itemCode}.";
        }

        if (decimal.Round(value.Value, 4, MidpointRounding.AwayFromZero) != value.Value)
        {
            return $"New price for {itemCode} may contain at most 4 decimal places.";
        }

        return null;
    }

    private static string FormatPrice(decimal? value) =>
        value.HasValue ? $"RM{value.Value:0.####}" : "(blank)";

    private static bool RowVersionsEqual(byte[]? left, byte[]? right) =>
        (left ?? []).AsSpan().SequenceEqual(right ?? []);

    private static async Task RollbackQuietlyAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch
        {
            // Preserve the original save failure. The transaction is disposed by the caller.
        }
    }

    private static InventoryTenantScope ToInventoryScope(TenantScope scope) =>
        new()
        {
            CompanyCode = scope.CompanyCode,
            BranchCode = scope.BranchCode,
            LocationCode = scope.LocationCode,
            UserId = scope.UserId
        };

    private static IvMasterOperationResult<SaPriceApplyResult> ApplyResult(
        SaPriceChangeBatch? batch,
        int changedRows) =>
        IvMasterOperationResult<SaPriceApplyResult>.Ok(new SaPriceApplyResult
        {
            PriceChangeBatchId = batch?.PriceChangeBatchId,
            BatchReference = batch is null ? null : $"PRC-{batch.PriceChangeBatchId:00000000}",
            ChangedRowCount = changedRows
        });

    private sealed record ItemDefaultPending(
        SaPriceReviewSelection Selection,
        IvStockMaster Entity,
        decimal? OldPrice,
        decimal NewPrice);

    private sealed record CustomerItemPending(
        SaPriceReviewSelection Selection,
        SaItemCust Entity,
        decimal? OldPrice,
        decimal NewPrice,
        IvStockMaster? Item,
        SaCust? Customer);

    private sealed record PriceListPending(
        SaPriceReviewSelection Selection,
        IvCustPrice Entity,
        IvCustPriceGroup Header,
        decimal NewPrice,
        DateTime? EffectiveFrom,
        IvStockMaster? Item);

    private sealed record PriceLineSnapshot(
        int Id,
        string ICode,
        string? IDesc,
        string Uom,
        decimal? Price,
        string? CurrencyCode,
        decimal MinQty,
        decimal? MaxQty,
        DateTime ValidFrom,
        DateTime? ValidTo)
    {
        public static PriceLineSnapshot From(IvCustPrice line) =>
            new(
                line.Id,
                line.ICode,
                line.IDesc,
                line.UOM,
                line.SellingPrice,
                line.CurrencyCode,
                line.MinQty,
                line.MaxQty,
                line.ValidFrom,
                line.ValidTo);
    }
}
