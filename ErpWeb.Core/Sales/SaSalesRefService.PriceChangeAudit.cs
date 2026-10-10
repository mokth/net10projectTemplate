using ErpWeb.Core.Pricing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

public sealed partial class SaSalesRefService
{
    private async Task AddPriceListAuditAsync(
        AppDbContext db,
        string company,
        string user,
        DateTime effectiveDate,
        string custPriceCode,
        string? priceListDescription,
        IReadOnlyList<IvCustPrice> oldLines,
        IReadOnlyList<NormalizedPriceLine> requestedLines,
        CancellationToken cancellationToken)
    {
        var currentLines = await db.IvCustPrices.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.CustPriceCode == custPriceCode)
            .ToListAsync(cancellationToken);

        var itemCodes = oldLines.Select(x => x.ICode)
            .Concat(requestedLines.Select(x => x.ICode))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var items = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && itemCodes.Contains(x.ICode))
            .ToDictionaryAsync(x => x.ICode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var auditLines = new List<SalesPriceChangeAuditLine>();
        foreach (var oldLine in oldLines)
        {
            var requested = requestedLines.FirstOrDefault(x => MatchesStoredLine(x, oldLine));
            if (requested is null)
            {
                if (oldLine.SellingPrice is null)
                {
                    continue;
                }

                auditLines.Add(BuildPriceListAuditLine(
                    SalesPriceChangeKinds.Delete,
                    oldLine,
                    null,
                    null,
                    priceListDescription,
                    custPriceCode,
                    items));
                continue;
            }

            if (!PriceSemanticsEqual(oldLine, requested)
                && (oldLine.SellingPrice is not null || requested.Price is not null))
            {
                var current = currentLines.FirstOrDefault(x => MatchesStoredLine(requested, x));
                auditLines.Add(BuildPriceListAuditLine(
                    SalesPriceChangeKinds.Update,
                    oldLine,
                    requested,
                    current?.Id,
                    priceListDescription,
                    custPriceCode,
                    items));
            }
        }

        foreach (var requested in requestedLines)
        {
            if (oldLines.Any(x => MatchesStoredLine(requested, x)))
            {
                continue;
            }

            if (requested.Price is null)
            {
                continue;
            }

            var current = currentLines.FirstOrDefault(x => MatchesStoredLine(requested, x));
            auditLines.Add(BuildPriceListAuditLine(
                SalesPriceChangeKinds.Create,
                null,
                requested,
                current?.Id,
                priceListDescription,
                custPriceCode,
                items));
        }

        if (auditLines.Count == 0)
        {
            return;
        }

        db.SaPriceChangeBatches.Add(
            SalesPriceChangeAuditFactory.BuildBatch(
                new SalesPriceChangeAuditContext(
                    company,
                    SalesPriceChangeAuditOrigins.PriceListMaster,
                    SalesPriceChangeAuditTargets.PriceList,
                    effectiveDate,
                    user,
                    AdjustmentMethod: SalesPriceChangeAuditAdjustmentMethods.DirectEdit),
                auditLines));
    }

    private static bool PriceSemanticsEqual(IvCustPrice oldLine, NormalizedPriceLine newLine) =>
        KeysEqual(oldLine.ICode, newLine.ICode)
        && KeysEqual(oldLine.UOM, newLine.UOM)
        && string.Equals(
            NormalizeOptionalCode(oldLine.CurrencyCode),
            newLine.CurrencyCode ?? string.Empty,
            StringComparison.OrdinalIgnoreCase)
        && oldLine.ValidFrom.Date == newLine.ValidFrom.Date
        && oldLine.ValidTo?.Date == newLine.ValidTo?.Date
        && oldLine.MinQty == newLine.MinQty
        && oldLine.MaxQty == newLine.MaxQty
        && oldLine.SellingPrice == newLine.Price;

    private static SalesPriceChangeAuditLine BuildPriceListAuditLine(
        string changeKind,
        IvCustPrice? oldLine,
        NormalizedPriceLine? newLine,
        int? newSourceId,
        string? priceListDescription,
        string custPriceCode,
        IReadOnlyDictionary<string, IvStockMaster> items)
    {
        var itemCode = newLine?.ICode ?? oldLine?.ICode ?? string.Empty;
        items.TryGetValue(itemCode, out var item);

        return new SalesPriceChangeAuditLine(
            changeKind,
            itemCode,
            item?.IDesc ?? newLine?.IDesc ?? oldLine?.IDesc,
            item?.IType,
            item?.IClassCode,
            item?.ISubClassCode,
            item?.Brand,
            CustPriceCode: custPriceCode,
            PriceListDescriptionSnapshot: priceListDescription,
            SourcePriceListLineId: newSourceId ?? oldLine?.Id,
            OldUom: oldLine?.UOM,
            NewUom: newLine?.UOM,
            OldCurrencyCode: oldLine?.CurrencyCode,
            NewCurrencyCode: newLine?.CurrencyCode,
            OldMinQty: oldLine?.MinQty,
            OldMaxQty: oldLine?.MaxQty,
            NewMinQty: newLine?.MinQty,
            NewMaxQty: newLine?.MaxQty,
            OldValidFrom: oldLine?.ValidFrom,
            OldValidTo: oldLine?.ValidTo,
            NewValidFrom: newLine?.ValidFrom,
            NewValidTo: newLine?.ValidTo,
            OldPrice: oldLine?.SellingPrice,
            NewPrice: newLine?.Price);
    }

    private async Task AddCustomerItemAuditAsync(
        AppDbContext db,
        string company,
        string user,
        DateTime effectiveDate,
        SaItemCust? oldEntity,
        SaItemCust newEntity,
        CancellationToken cancellationToken)
    {
        var item = await db.IvStockMasters.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.ICode == newEntity.ICode,
                cancellationToken);
        var customer = await db.SaCusts.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.CustCode == newEntity.CustCode,
                cancellationToken);

        var oldPrice = SalesPriceChangeAuditFactory.NormalizeLegacyMoney(oldEntity?.UnitPrice);
        var newPrice = SalesPriceChangeAuditFactory.NormalizeLegacyMoney(newEntity.UnitPrice);
        var currencyChanged = oldEntity is not null
            && !string.Equals(oldEntity.Currency, newEntity.Currency, StringComparison.OrdinalIgnoreCase);
        var changed = oldEntity is null
            ? newPrice is not null
            : oldPrice != newPrice
              || (oldPrice is not null || newPrice is not null) && currencyChanged;
        if (!changed)
        {
            return;
        }

        db.SaPriceChangeBatches.Add(
            SalesPriceChangeAuditFactory.BuildBatch(
                new SalesPriceChangeAuditContext(
                    company,
                    SalesPriceChangeAuditOrigins.CustomerItemMaster,
                    SalesPriceChangeAuditTargets.CustomerItem,
                    effectiveDate,
                    user,
                    AdjustmentMethod: SalesPriceChangeAuditAdjustmentMethods.DirectEdit),
                [new SalesPriceChangeAuditLine(
                    oldEntity is null ? SalesPriceChangeKinds.Create : SalesPriceChangeKinds.Update,
                    newEntity.ICode,
                    item?.IDesc ?? newEntity.IDesc,
                    item?.IType,
                    item?.IClassCode,
                    item?.ISubClassCode,
                    item?.Brand,
                    newEntity.CustCode,
                    customer?.CustName,
                    customer?.CustType,
                    customer?.CustGroupCode,
                    Moq: newEntity.MOQ,
                    OldUom: oldEntity?.SellingUOM,
                    NewUom: newEntity.SellingUOM,
                    OldCurrencyCode: oldEntity?.Currency,
                    NewCurrencyCode: newEntity.Currency,
                    OldPrice: oldPrice,
                    NewPrice: newPrice)]));
    }

    private async Task AddCustomerItemDeleteAuditAsync(
        AppDbContext db,
        string company,
        string user,
        DateTime effectiveDate,
        IReadOnlyList<SaItemCust> entities,
        CancellationToken cancellationToken)
    {
        var pricedEntities = entities.Where(x => x.UnitPrice is not null).ToList();
        if (pricedEntities.Count == 0)
        {
            return;
        }

        var itemCodes = pricedEntities.Select(x => x.ICode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var customerCodes = pricedEntities.Select(x => x.CustCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var itemMap = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && itemCodes.Contains(x.ICode))
            .ToDictionaryAsync(x => x.ICode, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var customerMap = await db.SaCusts.AsNoTracking()
            .Where(x => x.CompanyCode == company && customerCodes.Contains(x.CustCode))
            .ToDictionaryAsync(x => x.CustCode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var lines = pricedEntities.Select(entity =>
        {
            itemMap.TryGetValue(entity.ICode, out var item);
            customerMap.TryGetValue(entity.CustCode, out var customer);
            return new SalesPriceChangeAuditLine(
                SalesPriceChangeKinds.Delete,
                entity.ICode,
                item?.IDesc ?? entity.IDesc,
                item?.IType,
                item?.IClassCode,
                item?.ISubClassCode,
                item?.Brand,
                entity.CustCode,
                customer?.CustName,
                customer?.CustType,
                customer?.CustGroupCode,
                Moq: entity.MOQ,
                OldUom: entity.SellingUOM,
                OldCurrencyCode: entity.Currency,
                OldPrice: SalesPriceChangeAuditFactory.NormalizeLegacyMoney(entity.UnitPrice));
        }).ToList();

        db.SaPriceChangeBatches.Add(
            SalesPriceChangeAuditFactory.BuildBatch(
                new SalesPriceChangeAuditContext(
                    company,
                    SalesPriceChangeAuditOrigins.CustomerItemMaster,
                    SalesPriceChangeAuditTargets.CustomerItem,
                    effectiveDate,
                    user,
                    AdjustmentMethod: SalesPriceChangeAuditAdjustmentMethods.Delete),
                lines));
    }
}
