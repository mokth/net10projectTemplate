using System.Data;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Pricing;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Sales;

public sealed partial class SaPriceMaintenanceService : ISaPriceMaintenanceService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ITenantScopeContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _dates;
    private readonly ILogger<SaPriceMaintenanceService>? _logger;

    public SaPriceMaintenanceService(
        IDbContextFactory<AppDbContext> dbFactory,
        ITenantScopeContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService dates,
        ILogger<SaPriceMaintenanceService>? logger = null)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _dates = dates;
        _logger = logger;
    }

    public async Task<IvMasterOperationResult<SaPriceReviewPage>> SearchAsync(
        SaPriceReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        query ??= new SaPriceReviewQuery();
        var check = await CheckReadAsync(cancellationToken);
        if (check.ErrorCode is not null)
        {
            return FailPage(check.ErrorCode.Value, check.Message!);
        }

        var target = NormalizeToken(query.TargetType);
        if (!SaPriceMaintenanceTargets.IsSupported(target))
        {
            return FailPage(IvMasterErrorCode.Validation, "Select a supported price maintenance target.");
        }

        var filterError = ValidateSearchScope(query, target);
        if (filterError is not null)
        {
            return FailPage(IvMasterErrorCode.Validation, filterError);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return target switch
        {
            SaPriceMaintenanceTargets.ItemDefault =>
                await SearchItemDefaultsAsync(db, check.Scope!.CompanyCode, query, cancellationToken),
            SaPriceMaintenanceTargets.PriceList =>
                await SearchPriceListsAsync(db, check.Scope!.CompanyCode, query, cancellationToken),
            SaPriceMaintenanceTargets.CustomerItem =>
                await SearchCustomerItemsAsync(db, check.Scope!.CompanyCode, query, cancellationToken),
            _ => FailPage(IvMasterErrorCode.Validation, "Select a supported price maintenance target.")
        };
    }

    private async Task<IvMasterOperationResult<SaPriceReviewPage>> SearchItemDefaultsAsync(
        AppDbContext db,
        string company,
        SaPriceReviewQuery query,
        CancellationToken cancellationToken)
    {
        var source = db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && (!query.ActiveItemsOnly || x.IsActive));
        source = ApplyItemFilters(source, query);

        var total = await source.CountAsync(cancellationToken);
        if (total > SaPriceMaintenanceLimits.MaxReviewRows)
        {
            return TooManyRows();
        }

        var take = NormalizeTake(query.Take);
        var rows = await source
            .OrderBy(x => x.ICode)
            .Skip(Math.Max(0, query.Skip))
            .Take(take)
            .Select(x => new SaPriceReviewRow
            {
                ReviewRowKey = $"{SaPriceMaintenanceTargets.ItemDefault}|{x.ICode}",
                TargetType = SaPriceMaintenanceTargets.ItemDefault,
                ItemCode = x.ICode,
                ItemDescription = x.IDesc,
                ItemType = x.IType,
                ItemClass = x.IClassCode,
                ItemSubClass = x.ISubClassCode,
                Brand = x.Brand,
                Uom = x.SellingUom,
                CurrentPrice = x.SellingPrice,
                Status = SaPriceReviewStatuses.Ready,
                RowVersion = x.RowVersion
            })
            .ToListAsync(cancellationToken);

        return Page(SaPriceMaintenanceTargets.ItemDefault, rows, total);
    }

    private async Task<IvMasterOperationResult<SaPriceReviewPage>> SearchPriceListsAsync(
        AppDbContext db,
        string company,
        SaPriceReviewQuery query,
        CancellationToken cancellationToken)
    {
        var priceCode = await ResolvePriceListCodeAsync(db, company, query, cancellationToken);
        if (priceCode.Error is not null)
        {
            return FailPage(IvMasterErrorCode.Validation, priceCode.Error);
        }

        var asOf = (query.ReviewAsOf ?? _dates.Today).Date;
        var source =
            from line in db.IvCustPrices.AsNoTracking()
            join header in db.IvCustPriceGroups.AsNoTracking()
                on new { line.CompanyCode, line.CustPriceCode }
                equals new { header.CompanyCode, header.CustPriceCode }
            join item in db.IvStockMasters.AsNoTracking()
                on new { line.CompanyCode, line.ICode }
                equals new { item.CompanyCode, item.ICode }
            where line.CompanyCode == company
                  && line.CustPriceCode == priceCode.Value
                  && header.IsActive
                  && (!query.ActiveItemsOnly || item.IsActive)
                  && line.ValidFrom.Date <= asOf
                  && (line.ValidTo == null || line.ValidTo.Value.Date >= asOf)
                  && (string.IsNullOrWhiteSpace(query.ItemCode) || line.ICode == query.ItemCode!.Trim())
                  && (string.IsNullOrWhiteSpace(query.ItemSearch)
                      || line.ICode.Contains(query.ItemSearch!.Trim())
                      || (item.IDesc != null && item.IDesc.Contains(query.ItemSearch!.Trim())))
                  && (string.IsNullOrWhiteSpace(query.ItemType) || item.IType == query.ItemType!.Trim())
                  && (string.IsNullOrWhiteSpace(query.ItemClass) || item.IClassCode == query.ItemClass!.Trim())
                  && (string.IsNullOrWhiteSpace(query.ItemSubClass) || item.ISubClassCode == query.ItemSubClass!.Trim())
                  && (string.IsNullOrWhiteSpace(query.Brand) || (item.Brand != null && item.Brand.Contains(query.Brand!.Trim())))
                  && (string.IsNullOrWhiteSpace(query.Uom) || line.UOM == query.Uom!.Trim())
                  && (string.IsNullOrWhiteSpace(query.CurrencyCode)
                      || (line.CurrencyCode ?? string.Empty) == query.CurrencyCode!.Trim())
            select new
            {
                Line = line,
                Header = header,
                Item = item
            };

        var total = await source.CountAsync(cancellationToken);
        if (total > SaPriceMaintenanceLimits.MaxReviewRows)
        {
            return TooManyRows();
        }

        var take = NormalizeTake(query.Take);
        var rows = await source
            .OrderBy(x => x.Line.CustPriceCode)
            .ThenBy(x => x.Line.ICode)
            .ThenBy(x => x.Line.UOM)
            .ThenBy(x => x.Line.MinQty)
            .ThenBy(x => x.Line.ValidFrom)
            .Skip(Math.Max(0, query.Skip))
            .Take(take)
            .Select(x => new SaPriceReviewRow
            {
                ReviewRowKey = $"{SaPriceMaintenanceTargets.PriceList}|{x.Line.CustPriceCode}|{x.Line.Id}",
                TargetType = SaPriceMaintenanceTargets.PriceList,
                ItemCode = x.Line.ICode,
                ItemDescription = x.Item.IDesc,
                ItemType = x.Item.IType,
                ItemClass = x.Item.IClassCode,
                ItemSubClass = x.Item.ISubClassCode,
                Brand = x.Item.Brand,
                Uom = x.Line.UOM,
                CurrentPrice = x.Line.SellingPrice,
                Status = SaPriceReviewStatuses.Ready,
                CustPriceCode = x.Line.CustPriceCode,
                PriceListDescription = x.Header.CustPriceDesc,
                PriceListLineId = x.Line.Id,
                MinQty = x.Line.MinQty,
                MaxQty = x.Line.MaxQty,
                ValidFrom = x.Line.ValidFrom,
                ValidTo = x.Line.ValidTo,
                CurrencyCode = x.Line.CurrencyCode,
                RowVersion = Array.Empty<byte>(),
                HeaderRowVersion = x.Header.RowVersion
            })
            .ToListAsync(cancellationToken);

        return Page(SaPriceMaintenanceTargets.PriceList, rows, total);
    }

    private async Task<IvMasterOperationResult<SaPriceReviewPage>> SearchCustomerItemsAsync(
        AppDbContext db,
        string company,
        SaPriceReviewQuery query,
        CancellationToken cancellationToken)
    {
        var source =
            from special in db.SaItemCusts.AsNoTracking()
            join customer in db.SaCusts.AsNoTracking()
                on new { special.CompanyCode, special.CustCode }
                equals new { customer.CompanyCode, customer.CustCode }
            join item in db.IvStockMasters.AsNoTracking()
                on new { special.CompanyCode, special.ICode }
                equals new { item.CompanyCode, item.ICode }
            where special.CompanyCode == company
                  && (!query.ActiveCustomersOnly || customer.IsActive)
                  && (!query.ActiveItemsOnly || item.IsActive)
                  && (string.IsNullOrWhiteSpace(query.CustCode) || special.CustCode == query.CustCode!.Trim())
                  && (string.IsNullOrWhiteSpace(query.CustType) || customer.CustType == query.CustType!.Trim())
                  && (string.IsNullOrWhiteSpace(query.CustGroup) || customer.CustGroupCode == query.CustGroup!.Trim())
                  && (string.IsNullOrWhiteSpace(query.ItemCode) || special.ICode == query.ItemCode!.Trim())
                  && (string.IsNullOrWhiteSpace(query.ItemSearch)
                      || special.ICode.Contains(query.ItemSearch!.Trim())
                      || (item.IDesc != null && item.IDesc.Contains(query.ItemSearch!.Trim())))
                  && (string.IsNullOrWhiteSpace(query.ItemType) || item.IType == query.ItemType!.Trim())
                  && (string.IsNullOrWhiteSpace(query.ItemClass) || item.IClassCode == query.ItemClass!.Trim())
                  && (string.IsNullOrWhiteSpace(query.ItemSubClass) || item.ISubClassCode == query.ItemSubClass!.Trim())
                  && (string.IsNullOrWhiteSpace(query.Brand) || (item.Brand != null && item.Brand.Contains(query.Brand!.Trim())))
                  && (string.IsNullOrWhiteSpace(query.Uom) || special.SellingUOM == query.Uom!.Trim())
                  && (!query.Moq.HasValue || special.MOQ == query.Moq.Value)
            select new
            {
                Special = special,
                Customer = customer,
                Item = item
            };

        var total = await source.CountAsync(cancellationToken);
        if (total > SaPriceMaintenanceLimits.MaxReviewRows)
        {
            return TooManyRows();
        }

        var take = NormalizeTake(query.Take);
        var rows = await source
            .OrderBy(x => x.Special.CustCode)
            .ThenBy(x => x.Special.ICode)
            .ThenBy(x => x.Special.SellingUOM)
            .ThenBy(x => x.Special.MOQ)
            .Skip(Math.Max(0, query.Skip))
            .Take(take)
            .Select(x => new
            {
                Special = x.Special,
                Customer = x.Customer,
                Item = x.Item
            })
            .ToListAsync(cancellationToken);

        return Page(
            SaPriceMaintenanceTargets.CustomerItem,
            rows.Select(x => new SaPriceReviewRow
            {
                ReviewRowKey = $"{SaPriceMaintenanceTargets.CustomerItem}|{x.Special.CustCode}|{x.Special.ICode}|{x.Special.SellingUOM}|{x.Special.MOQ}",
                TargetType = SaPriceMaintenanceTargets.CustomerItem,
                ItemCode = x.Special.ICode,
                ItemDescription = x.Item.IDesc,
                ItemType = x.Item.IType,
                ItemClass = x.Item.IClassCode,
                ItemSubClass = x.Item.ISubClassCode,
                Brand = x.Item.Brand,
                Uom = x.Special.SellingUOM,
                CurrentPrice = SalesPriceChangeAuditFactory.NormalizeLegacyMoney(x.Special.UnitPrice),
                Status = SaPriceReviewStatuses.Ready,
                CustCode = x.Special.CustCode,
                CustomerName = x.Customer.CustName,
                CustomerType = x.Customer.CustType,
                CustomerGroup = x.Customer.CustGroupCode,
                Moq = x.Special.MOQ,
                CurrencyCode = x.Special.Currency,
                RowVersion = x.Special.RowVersion
            }).ToList(),
            total);
    }

    public async Task<IvMasterOperationResult<SaPricePreviewResult>> PreviewAsync(
        SaPricePreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return FailPreview(IvMasterErrorCode.Validation, "Preview request is required.");
        }

        var check = await CheckReadAsync(cancellationToken);
        if (check.ErrorCode is not null)
        {
            return FailPreview(check.ErrorCode.Value, check.Message!);
        }

        var target = NormalizeToken(request.TargetType);
        if (!SaPriceMaintenanceTargets.IsSupported(target))
        {
            return FailPreview(IvMasterErrorCode.Validation, "Select a supported price maintenance target.");
        }

        var adjustmentError = ValidateAdjustmentContract(request);
        if (adjustmentError is not null)
        {
            return FailPreview(IvMasterErrorCode.Validation, adjustmentError);
        }

        var selections = request.Selections ?? [];
        if (selections.Count == 0)
        {
            return FailPreview(IvMasterErrorCode.Validation, "Select at least one price row.");
        }

        if (selections.Count > SaPriceMaintenanceLimits.MaxReviewRows)
        {
            return FailPreview(
                IvMasterErrorCode.Validation,
                $"A review may contain at most {SaPriceMaintenanceLimits.MaxReviewRows:N0} selected rows.");
        }

        var duplicate = selections
            .GroupBy(x => x.ReviewRowKey ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
        {
            return FailPreview(IvMasterErrorCode.Validation, $"Review row {duplicate.Key} was selected more than once.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var current = await LoadCurrentRowsAsync(
            db,
            check.Scope!.CompanyCode,
            target,
            selections.Select(x => x.ReviewRowKey).ToList(),
            cancellationToken);

        var rows = new List<SaPriceReviewRow>(selections.Count);
        foreach (var selection in selections)
        {
            if (!current.TryGetValue(selection.ReviewRowKey, out var row))
            {
                rows.Add(BlockedRow(
                    selection.ReviewRowKey,
                    target,
                    "The selected price row no longer exists. Reload the review."));
                continue;
            }

            var warning = ValidateSelectionBaseline(row, selection);
            if (warning is null && selection.NewPrice is null)
            {
                warning = "New price is required.";
            }

            if (warning is null && selection.NewPrice < 0m)
            {
                warning = "New price cannot be negative.";
            }

            if (warning is null && target == SaPriceMaintenanceTargets.PriceList
                && NormalizeToken(request.PriceListUpdateMode) == SaPriceListUpdateModes.ScheduleFromDate)
            {
                warning = ValidateSchedule(row, request.EffectiveFrom, _dates.Today);
            }

            var proposed = CopyRow(row);
            proposed.Selected = true;
            proposed.ProposedPrice = selection.NewPrice;
            if (warning is not null)
            {
                proposed.Status = SaPriceReviewStatuses.Blocked;
                proposed.Warning = warning;
            }
            else if (proposed.CurrentPrice == proposed.ProposedPrice)
            {
                proposed.Status = SaPriceReviewStatuses.Unchanged;
            }
            else
            {
                proposed.Status = SaPriceReviewStatuses.Ready;
            }

            proposed.DifferenceAmount = proposed.CurrentPrice is null || proposed.ProposedPrice is null
                ? null
                : proposed.ProposedPrice.Value - proposed.CurrentPrice.Value;
            proposed.DifferencePercent = CalculatePercent(proposed.CurrentPrice, proposed.ProposedPrice);
            rows.Add(proposed);
        }

        var summary = BuildSummary(rows);
        return IvMasterOperationResult<SaPricePreviewResult>.Ok(new SaPricePreviewResult
        {
            Summary = summary,
            Rows = rows
        });
    }

    private async Task<Dictionary<string, SaPriceReviewRow>> LoadCurrentRowsAsync(
        AppDbContext db,
        string company,
        string target,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SaPriceReviewRow>(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0)
        {
            return result;
        }

        switch (target)
        {
            case SaPriceMaintenanceTargets.ItemDefault:
            {
                var codes = keys
                    .Select(ParseItemDefaultKey)
                    .Where(x => x is not null)
                    .Select(x => x!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var rows = await db.IvStockMasters.AsNoTracking()
                    .Where(x => x.CompanyCode == company && codes.Contains(x.ICode))
                    .ToListAsync(cancellationToken);
                foreach (var item in rows)
                {
                    result[$"{target}|{item.ICode}"] = new SaPriceReviewRow
                    {
                        ReviewRowKey = $"{target}|{item.ICode}",
                        TargetType = target,
                        ItemCode = item.ICode,
                        ItemDescription = item.IDesc,
                        ItemType = item.IType,
                        ItemClass = item.IClassCode,
                        ItemSubClass = item.ISubClassCode,
                        Brand = item.Brand,
                        Uom = item.SellingUom,
                        CurrentPrice = item.SellingPrice,
                        RowVersion = item.RowVersion
                    };
                }

                break;
            }

            case SaPriceMaintenanceTargets.PriceList:
            {
                var lineIds = keys
                    .Select(ParsePriceListKey)
                    .Where(x => x is not null)
                    .Select(x => x!.Value.Id)
                    .Distinct()
                    .ToList();
                var rows = await (
                    from line in db.IvCustPrices.AsNoTracking()
                    join header in db.IvCustPriceGroups.AsNoTracking()
                        on new { line.CompanyCode, line.CustPriceCode }
                        equals new { header.CompanyCode, header.CustPriceCode }
                    join item in db.IvStockMasters.AsNoTracking()
                        on new { line.CompanyCode, line.ICode }
                        equals new { item.CompanyCode, item.ICode }
                    where line.CompanyCode == company && lineIds.Contains(line.Id)
                    select new { line, header, item })
                    .ToListAsync(cancellationToken);
                foreach (var current in rows)
                {
                    var key = $"{target}|{current.line.CustPriceCode}|{current.line.Id}";
                    result[key] = new SaPriceReviewRow
                    {
                        ReviewRowKey = key,
                        TargetType = target,
                        ItemCode = current.line.ICode,
                        ItemDescription = current.item.IDesc,
                        ItemType = current.item.IType,
                        ItemClass = current.item.IClassCode,
                        ItemSubClass = current.item.ISubClassCode,
                        Brand = current.item.Brand,
                        Uom = current.line.UOM,
                        CurrentPrice = current.line.SellingPrice,
                        CustPriceCode = current.line.CustPriceCode,
                        PriceListDescription = current.header.CustPriceDesc,
                        PriceListLineId = current.line.Id,
                        MinQty = current.line.MinQty,
                        MaxQty = current.line.MaxQty,
                        ValidFrom = current.line.ValidFrom,
                        ValidTo = current.line.ValidTo,
                        CurrencyCode = current.line.CurrencyCode,
                        HeaderRowVersion = current.header.RowVersion
                    };
                }

                break;
            }

            case SaPriceMaintenanceTargets.CustomerItem:
            {
                var parsed = keys.Select(ParseCustomerItemKey).Where(x => x is not null).Select(x => x!.Value).ToList();
                var customerCodes = parsed.Select(x => x.CustCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var itemCodes = parsed.Select(x => x.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var rows = await (
                    from special in db.SaItemCusts.AsNoTracking()
                    join customer in db.SaCusts.AsNoTracking()
                        on new { special.CompanyCode, special.CustCode }
                        equals new { customer.CompanyCode, customer.CustCode }
                    join item in db.IvStockMasters.AsNoTracking()
                        on new { special.CompanyCode, special.ICode }
                        equals new { item.CompanyCode, item.ICode }
                    where special.CompanyCode == company
                          && customerCodes.Contains(special.CustCode)
                          && itemCodes.Contains(special.ICode)
                    select new { special, customer, item })
                    .ToListAsync(cancellationToken);
                foreach (var current in rows)
                {
                    var key = CustomerItemKey(current.special.CustCode, current.special.ICode, current.special.SellingUOM, current.special.MOQ);
                    result[key] = new SaPriceReviewRow
                    {
                        ReviewRowKey = key,
                        TargetType = target,
                        ItemCode = current.special.ICode,
                        ItemDescription = current.item.IDesc,
                        ItemType = current.item.IType,
                        ItemClass = current.item.IClassCode,
                        ItemSubClass = current.item.ISubClassCode,
                        Brand = current.item.Brand,
                        Uom = current.special.SellingUOM,
                        CurrentPrice = SalesPriceChangeAuditFactory.NormalizeLegacyMoney(current.special.UnitPrice),
                        CustCode = current.special.CustCode,
                        CustomerName = current.customer.CustName,
                        CustomerType = current.customer.CustType,
                        CustomerGroup = current.customer.CustGroupCode,
                        Moq = current.special.MOQ,
                        CurrencyCode = current.special.Currency,
                        RowVersion = current.special.RowVersion
                    };
                }

                break;
            }
        }

        return result;
    }

    public async Task<IvMasterOperationResult<SaPriceApplyResult>> ApplyAsync(
        SaPriceApplyRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return FailApply(IvMasterErrorCode.Validation, "Apply request is required.");
        }

        var check = await CheckApplyAsync(cancellationToken);
        if (check.ErrorCode is not null)
        {
            return FailApply(check.ErrorCode.Value, check.Message!);
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return FailApply(IvMasterErrorCode.Validation, "A reason is required before applying price changes.");
        }

        if (request.Reason.Trim().Length > SaPriceMaintenanceLimits.MaxReasonLength)
        {
            return FailApply(
                IvMasterErrorCode.Validation,
                $"Reason must be at most {SaPriceMaintenanceLimits.MaxReasonLength} characters.");
        }

        var preview = await PreviewAsync(
            new SaPricePreviewRequest
            {
                TargetType = request.TargetType,
                AdjustmentMethod = request.AdjustmentMethod,
                AdjustmentValue = request.AdjustmentValue,
                DecimalPlaces = request.DecimalPlaces,
                RoundingMode = request.RoundingMode,
                PriceListUpdateMode = request.PriceListUpdateMode,
                EffectiveFrom = request.EffectiveFrom,
                Reason = request.Reason,
                ReviewScope = request.ReviewScope,
                Selections = request.Selections
            },
            cancellationToken);
        if (!preview.Succeeded || preview.Data is null)
        {
            return FailApply(preview.ErrorCode, preview.Message ?? "Preview failed.");
        }

        if (preview.Data.Summary.Blocked > 0)
        {
            return FailApply(
                IvMasterErrorCode.Validation,
                "Apply is blocked because one or more selected rows are stale or invalid.");
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailApply(
                IvMasterErrorCode.InvalidScope,
                "Invalid company, branch, or location context.");
        }

        var target = NormalizeToken(request.TargetType);
        var result = target switch
        {
            SaPriceMaintenanceTargets.ItemDefault =>
                await ApplyItemDefaultsAsync(writeScope, request, cancellationToken),
            SaPriceMaintenanceTargets.CustomerItem =>
                await ApplyCustomerItemsAsync(writeScope, request, cancellationToken),
            SaPriceMaintenanceTargets.PriceList =>
                await ApplyPriceListsAsync(writeScope, request, cancellationToken),
            _ => FailApply(IvMasterErrorCode.Validation, "Select a supported price maintenance target.")
        };

        if (result.Succeeded && result.Data is { ChangedRowCount: > 0 } applied)
        {
            _logger?.LogInformation(
                "Sales price bulk apply completed. BatchId={BatchId}, Company={CompanyCode}, Target={Target}, ChangedCount={ChangedCount}, User={UserId}",
                applied.PriceChangeBatchId,
                writeScope.CompanyCode,
                target,
                applied.ChangedRowCount,
                writeScope.UserId);
        }
        else if (!result.Succeeded && result.ErrorCode == IvMasterErrorCode.Concurrency)
        {
            _logger?.LogWarning(
                "Sales price bulk apply concurrency conflict. Company={CompanyCode}, Target={Target}, SelectedCount={SelectedCount}, User={UserId}",
                writeScope.CompanyCode,
                target,
                request.Selections?.Count ?? 0,
                writeScope.UserId);
        }

        return result;
    }

    private static string? ParseItemDefaultKey(string key)
    {
        var parts = (key ?? string.Empty).Split('|');
        return parts.Length == 2 && string.Equals(parts[0], SaPriceMaintenanceTargets.ItemDefault, StringComparison.OrdinalIgnoreCase)
            ? parts[1]
            : null;
    }

    private static (string Code, int Id)? ParsePriceListKey(string key)
    {
        var parts = (key ?? string.Empty).Split('|');
        return parts.Length == 3
               && string.Equals(parts[0], SaPriceMaintenanceTargets.PriceList, StringComparison.OrdinalIgnoreCase)
               && int.TryParse(parts[2], out var id)
            ? (parts[1], id)
            : null;
    }

    private static (string CustCode, string ItemCode, string Uom, int Moq)? ParseCustomerItemKey(string key)
    {
        var parts = (key ?? string.Empty).Split('|');
        return parts.Length == 5
               && string.Equals(parts[0], SaPriceMaintenanceTargets.CustomerItem, StringComparison.OrdinalIgnoreCase)
               && int.TryParse(parts[4], out var moq)
            ? (parts[1], parts[2], parts[3], moq)
            : null;
    }

    private static string CustomerItemKey(string custCode, string itemCode, string uom, int moq) =>
        $"{SaPriceMaintenanceTargets.CustomerItem}|{custCode}|{itemCode}|{uom}|{moq}";

    private static string? ValidateSelectionBaseline(
        SaPriceReviewRow row,
        SaPriceReviewSelection selection)
    {
        if (row.CurrentPrice != selection.BaselinePrice)
        {
            return "The current price changed after the review was loaded. Reload and try again.";
        }

        var expectedVersion = row.TargetType == SaPriceMaintenanceTargets.PriceList
            ? selection.HeaderRowVersion
            : selection.RowVersion;
        var actualVersion = row.TargetType == SaPriceMaintenanceTargets.PriceList
            ? row.HeaderRowVersion
            : row.RowVersion;
        if (!expectedVersion.AsSpan().SequenceEqual(actualVersion))
        {
            return "The selected master was changed by another user. Reload and try again.";
        }

        return null;
    }

    private static string? ValidateSchedule(SaPriceReviewRow row, DateTime? effectiveFrom, DateTime today)
    {
        if (!effectiveFrom.HasValue)
        {
            return "Schedule From Date is required for Price List scheduling.";
        }

        var effective = effectiveFrom.Value.Date;
        if (effective < today.Date)
        {
            return "Schedule From Date cannot be in the past.";
        }

        if (!row.ValidFrom.HasValue
            || row.ValidFrom.Value.Date > effective
            || (row.ValidTo.HasValue && row.ValidTo.Value.Date < effective))
        {
            return $"The selected price row is not effective on {effective:yyyy-MM-dd}. Reload Price List using that Review As Of date.";
        }

        return null;
    }

    private static SaPriceReviewRow CopyRow(SaPriceReviewRow row) =>
        new()
        {
            ReviewRowKey = row.ReviewRowKey,
            TargetType = row.TargetType,
            ItemCode = row.ItemCode,
            ItemDescription = row.ItemDescription,
            ItemType = row.ItemType,
            ItemClass = row.ItemClass,
            ItemSubClass = row.ItemSubClass,
            Brand = row.Brand,
            Uom = row.Uom,
            CurrentPrice = row.CurrentPrice,
            CustPriceCode = row.CustPriceCode,
            PriceListDescription = row.PriceListDescription,
            PriceListLineId = row.PriceListLineId,
            MinQty = row.MinQty,
            MaxQty = row.MaxQty,
            ValidFrom = row.ValidFrom,
            ValidTo = row.ValidTo,
            CurrencyCode = row.CurrencyCode,
            CustCode = row.CustCode,
            CustomerName = row.CustomerName,
            CustomerType = row.CustomerType,
            CustomerGroup = row.CustomerGroup,
            Moq = row.Moq,
            RowVersion = row.RowVersion,
            HeaderRowVersion = row.HeaderRowVersion,
            ProposedPrice = row.ProposedPrice,
            DifferenceAmount = row.DifferenceAmount,
            DifferencePercent = row.DifferencePercent,
            Status = row.Status,
            Warning = row.Warning,
            Selected = row.Selected
        };

    private static SaPriceReviewRow BlockedRow(string key, string target, string warning) =>
        new()
        {
            ReviewRowKey = key,
            TargetType = target,
            Status = SaPriceReviewStatuses.Blocked,
            Warning = warning,
            Selected = true
        };

    private static decimal? CalculatePercent(decimal? oldPrice, decimal? newPrice) =>
        oldPrice is null || newPrice is null || oldPrice == 0m
            ? null
            : (newPrice.Value - oldPrice.Value) / oldPrice.Value * 100m;

    private static SaPricePreviewSummary BuildSummary(IReadOnlyList<SaPriceReviewRow> rows)
    {
        var changing = rows.Where(x => x.Status == SaPriceReviewStatuses.Ready).ToList();
        var differences = changing
            .Where(x => x.DifferenceAmount.HasValue)
            .Select(x => x.DifferenceAmount!.Value)
            .ToList();
        var percents = changing
            .Where(x => x.DifferencePercent.HasValue)
            .Select(x => x.DifferencePercent!.Value)
            .ToList();

        return new SaPricePreviewSummary
        {
            Matched = rows.Count(x => x.Status != SaPriceReviewStatuses.Blocked),
            Selected = rows.Count,
            Changing = changing.Count,
            Unchanged = rows.Count(x => x.Status == SaPriceReviewStatuses.Unchanged),
            Blocked = rows.Count(x => x.Status == SaPriceReviewStatuses.Blocked),
            ItemsAffected = changing.Select(x => x.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            CustomersAffected = changing.Where(x => x.CustCode is not null)
                .Select(x => x.CustCode!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            PriceListsAffected = changing.Where(x => x.CustPriceCode is not null)
                .Select(x => x.CustPriceCode!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            MinimumDifference = differences.Count == 0 ? null : differences.Min(),
            MaximumDifference = differences.Count == 0 ? null : differences.Max(),
            AveragePercentChange = percents.Count == 0 ? null : percents.Average()
        };
    }

    private static IvMasterOperationResult<SaPricePreviewResult> FailPreview(
        IvMasterErrorCode code,
        string message) =>
        IvMasterOperationResult<SaPricePreviewResult>.Fail(code, message);

    private static IvMasterOperationResult<SaPriceApplyResult> FailApply(
        IvMasterErrorCode code,
        string message) =>
        IvMasterOperationResult<SaPriceApplyResult>.Fail(code, message);

    private async Task<ScopeCheck> CheckReadAsync(CancellationToken cancellationToken)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return ScopeCheck.Fail(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        if (!await _accessRights.CanAsync(MenuCodes.SalesPriceMaintenance, PermissionCodes.Access, cancellationToken))
        {
            return ScopeCheck.Fail(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        if (!await _accessRights.CanAsync(MenuCodes.SalesPriceMaintenance, PermissionCodes.ViewPrice, cancellationToken))
        {
            return ScopeCheck.Fail(IvMasterErrorCode.AccessDenied, "Price visibility permission is required.");
        }

        return ScopeCheck.Ok(scope);
    }

    private async Task<ScopeCheck> CheckApplyAsync(CancellationToken cancellationToken)
    {
        var check = await CheckReadAsync(cancellationToken);
        if (check.ErrorCode is not null)
        {
            return check;
        }

        if (!await _accessRights.CanAsync(MenuCodes.SalesPriceMaintenance, PermissionCodes.Edit, cancellationToken))
        {
            return ScopeCheck.Fail(IvMasterErrorCode.AccessDenied, "Price maintenance edit permission is required.");
        }

        return check;
    }

    private static string? ValidateSearchScope(SaPriceReviewQuery query, string target)
    {
        var hasMeaningfulFilter =
            !string.IsNullOrWhiteSpace(query.ItemCode)
            || !string.IsNullOrWhiteSpace(query.ItemSearch)
            || !string.IsNullOrWhiteSpace(query.ItemType)
            || !string.IsNullOrWhiteSpace(query.ItemClass)
            || !string.IsNullOrWhiteSpace(query.ItemSubClass)
            || !string.IsNullOrWhiteSpace(query.Brand)
            || !string.IsNullOrWhiteSpace(query.CustPriceCode)
            || !string.IsNullOrWhiteSpace(query.CustGroupCode)
            || !string.IsNullOrWhiteSpace(query.CustCode)
            || !string.IsNullOrWhiteSpace(query.CustType)
            || !string.IsNullOrWhiteSpace(query.CustGroup);

        if (target == SaPriceMaintenanceTargets.ItemDefault && query.LoadAllActiveItems)
        {
            return null;
        }

        if (!hasMeaningfulFilter)
        {
            return "Select at least one scope filter before loading prices.";
        }

        if (target == SaPriceMaintenanceTargets.PriceList
            && string.IsNullOrWhiteSpace(query.CustPriceCode)
            && string.IsNullOrWhiteSpace(query.CustGroupCode))
        {
            return "Select a Price List or Customer Group before loading Price List prices.";
        }

        return null;
    }

    private static IQueryable<IvStockMaster> ApplyItemFilters(
        IQueryable<IvStockMaster> source,
        SaPriceReviewQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.ItemCode))
        {
            source = source.Where(x => x.ICode == query.ItemCode!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.ItemSearch))
        {
            var search = query.ItemSearch!.Trim();
            source = source.Where(x => x.ICode.Contains(search) || (x.IDesc != null && x.IDesc.Contains(search)));
        }

        if (!string.IsNullOrWhiteSpace(query.ItemType))
        {
            source = source.Where(x => x.IType == query.ItemType!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.ItemClass))
        {
            source = source.Where(x => x.IClassCode == query.ItemClass!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.ItemSubClass))
        {
            source = source.Where(x => x.ISubClassCode == query.ItemSubClass!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.Brand))
        {
            var brand = query.Brand!.Trim();
            source = source.Where(x => x.Brand != null && x.Brand.Contains(brand));
        }

        return source;
    }

    private async Task<(string? Value, string? Error)> ResolvePriceListCodeAsync(
        AppDbContext db,
        string company,
        SaPriceReviewQuery query,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(query.CustPriceCode))
        {
            return (query.CustPriceCode!.Trim(), null);
        }

        var groupCode = query.CustGroupCode?.Trim();
        if (string.IsNullOrWhiteSpace(groupCode))
        {
            return (null, "Select a Price List or Customer Group before loading Price List prices.");
        }

        var group = await db.SaCustGroups.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.CustGroupCode == groupCode,
                cancellationToken);
        if (group is null || string.IsNullOrWhiteSpace(group.CustPriceCode))
        {
            return (null, $"Customer Group {groupCode} has no default price list. Select a Price List directly.");
        }

        return (group.CustPriceCode.Trim(), null);
    }

    private static int NormalizeTake(int take) =>
        take <= 0 ? SaPriceMaintenanceLimits.MaxReviewRows : Math.Min(take, SaPriceMaintenanceLimits.MaxReviewRows);

    private static string NormalizeToken(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string? ValidateAdjustmentContract(SaPriceChangeRequestBase request)
    {
        var method = NormalizeToken(request.AdjustmentMethod);
        if (!SaPriceAdjustmentMethods.All.Contains(method, StringComparer.Ordinal))
        {
            return "Select a supported price adjustment method.";
        }

        if (request.DecimalPlaces is not (2 or 4))
        {
            return "Price precision must be 2 or 4 decimal places.";
        }

        var rounding = NormalizeToken(request.RoundingMode);
        if (!SaPriceRoundingModes.All.Contains(rounding, StringComparer.Ordinal))
        {
            return "Select a supported rounding mode.";
        }

        if (request.AdjustmentValue < 0m)
        {
            return "Adjustment value cannot be negative.";
        }

        if (method == SaPriceAdjustmentMethods.DecreasePercent && request.AdjustmentValue > 100m)
        {
            return "Decrease percentage cannot be greater than 100%.";
        }

        var updateMode = NormalizeToken(request.PriceListUpdateMode);
        if (NormalizeToken(request.TargetType) == SaPriceMaintenanceTargets.PriceList
            && updateMode is not SaPriceListUpdateModes.UpdateSelectedRow and not SaPriceListUpdateModes.ScheduleFromDate)
        {
            return "Select a supported Price List update mode.";
        }

        return null;
    }

    private static IvMasterOperationResult<SaPriceReviewPage> Page(
        string target,
        IReadOnlyList<SaPriceReviewRow> rows,
        int total) =>
        IvMasterOperationResult<SaPriceReviewPage>.Ok(new SaPriceReviewPage
        {
            TargetType = target,
            Rows = rows,
            TotalCount = total
        });

    private static IvMasterOperationResult<SaPriceReviewPage> TooManyRows() =>
        FailPage(
            IvMasterErrorCode.Validation,
            $"More than {SaPriceMaintenanceLimits.MaxReviewRows:N0} rows match the scope. Narrow the filters before loading prices.");

    private static IvMasterOperationResult<SaPriceReviewPage> FailPage(
        IvMasterErrorCode code,
        string message) =>
        IvMasterOperationResult<SaPriceReviewPage>.Fail(code, message);

    private sealed record ScopeCheck(
        TenantScope? Scope,
        IvMasterErrorCode? ErrorCode,
        string? Message)
    {
        public static ScopeCheck Ok(TenantScope scope) => new(scope, null, null);
        public static ScopeCheck Fail(IvMasterErrorCode code, string message) => new(null, code, message);
    }
}
