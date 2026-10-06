using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Costing;

public sealed class CostingTraceService : ICostingTraceService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;

    public CostingTraceService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
    }

    public async Task<CostingTracePage> GetItemTimelineAsync(
        CostingTraceQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.InventoryCostingCenter, PermissionCodes.Access, cancellationToken))
            return new CostingTracePage { Denied = true, Error = "Not authorized." };
        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
            return new CostingTracePage { Error = "A trusted company and branch are required." };
        if (string.IsNullOrWhiteSpace(query.ItemCode))
            return new CostingTracePage { Error = "Item is required." };

        var showMoney = await _access.CanAsync(MenuCodes.InventoryCostingCenter, PermissionCodes.ViewCost, cancellationToken);
        var item = query.ItemCode.Trim();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var coverage = await DescribeEpochAsync(db, scope.CompanyCode, scope.BranchCode, cancellationToken);
        if (coverage != CostingEpochCoverage.V2)
        {
            return new CostingTracePage
            {
                MonetaryValuesVisible = showMoney,
                EpochCoverage = coverage,
                Error = coverage == CostingEpochCoverage.NoActiveEpoch
                    ? "No active ledger epoch. The item trace is not authoritative."
                    : "Retired and active epochs both contain sealed facts. The item trace stays blocked until epoch coverage is proven."
            };
        }
        var facts = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.ItemCode == item
                && x.StockPosting!.SealedAtUtc != null)
            .OrderBy(x => x.StockPosting!.PostingSequence)
            .ThenBy(x => x.PostingLineNo)
            .ThenBy(x => x.SplitOrdinal)
            .Select(x => new FactRow(
                x.StockPostingId,
                x.StockPosting!.PostingSequence,
                x.PostingLineNo,
                x.SplitOrdinal,
                x.EffectiveAt,
                x.SourceDocumentType,
                x.SourceDocumentNo,
                x.MovementCode,
                x.WarehouseCode,
                x.Direction,
                x.BaseQty,
                x.CostAmount,
                x.UnitCost,
                x.ValuationSource,
                x.ValuationStatus,
                x.ReversesValuationFactId != null))
            .ToListAsync(cancellationToken);

        decimal qty = 0m;
        decimal value = 0m;
        var anchorQty = 0m;
        var anchorValue = 0m;
        var lines = new List<CostingTraceLine>();
        foreach (var fact in facts)
        {
            var beforeWindow = query.From is not null && fact.EffectiveAt < query.From.Value;
            qty = StockLedgerPrecision.Quantity(qty + (fact.Direction * fact.BaseQty));
            value = StockLedgerPrecision.Money(value + (fact.Direction * fact.CostAmount));
            if (beforeWindow)
            {
                anchorQty = qty;
                anchorValue = value;
                continue;
            }
            if (query.To is not null && fact.EffectiveAt > query.To.Value)
                continue;

            var average = qty == 0m ? 0m : StockLedgerPrecision.Money(value / qty);
            var displayed = string.IsNullOrWhiteSpace(query.WarehouseCode)
                || string.Equals(fact.WarehouseCode, query.WarehouseCode.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!displayed)
                continue;

            lines.Add(new CostingTraceLine(
                fact.StockPostingId,
                fact.PostingSequence,
                fact.PostingLineNo,
                fact.SplitOrdinal,
                fact.EffectiveAt,
                fact.SourceDocumentType,
                fact.SourceDocumentNo,
                fact.MovementCode,
                fact.WarehouseCode,
                fact.Direction > 0 ? fact.BaseQty : 0m,
                fact.Direction < 0 ? fact.BaseQty : 0m,
                qty,
                showMoney && fact.Direction > 0 ? fact.CostAmount : null,
                showMoney && fact.Direction < 0 ? fact.CostAmount : null,
                showMoney ? value : null,
                showMoney ? fact.UnitCost : null,
                showMoney ? average : null,
                fact.ValuationSource,
                fact.ValuationStatus,
                fact.IsReversal));
        }

        var anchorAverage = anchorQty == 0m ? 0m : StockLedgerPrecision.Money(anchorValue / anchorQty);
        return new CostingTracePage
        {
            MonetaryValuesVisible = showMoney,
            EpochCoverage = coverage,
            Anchor = new CostingTraceAnchor(
                anchorQty,
                showMoney ? anchorValue : 0m,
                showMoney ? anchorAverage : 0m),
            Lines = lines
        };
    }

    public async Task<CostingBackdateImpactPage> GetBackdateImpactAsync(
        string itemCode,
        DateTime asOf,
        CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.InventoryCostingCenter, PermissionCodes.Access, cancellationToken))
            return new CostingBackdateImpactPage { Denied = true, Error = "Not authorized." };
        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
            return new CostingBackdateImpactPage { Error = "A trusted company and branch are required." };
        if (string.IsNullOrWhiteSpace(itemCode))
            return new CostingBackdateImpactPage { Error = "Item is required." };

        var item = itemCode.Trim();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var coverage = await DescribeEpochAsync(db, scope.CompanyCode, scope.BranchCode, cancellationToken);
        if (coverage != CostingEpochCoverage.V2)
        {
            return new CostingBackdateImpactPage
            {
                ItemCode = item,
                AsOf = asOf,
                Error = coverage == CostingEpochCoverage.NoActiveEpoch
                    ? "No active ledger epoch."
                    : "Epoch coverage is unresolved; backdate impact stays blocked."
            };
        }

        var rows = await (
            from f in db.StockValuationFacts.AsNoTracking()
            join p in db.StockPostings.AsNoTracking() on f.StockPostingId equals p.Id
            where f.CompanyCode == scope.CompanyCode
                  && f.BranchCode == scope.BranchCode
                  && f.ItemCode == item
                  && f.EffectiveAt > asOf
                  && p.SealedAtUtc != null
                  && p.PostingRole == "PRIMARY"
            group new { f, p } by new { p.Id, p.EffectiveAt, p.SourceDocumentType, p.SourceDocumentNo, p.PostingRole } into g
            select new
            {
                g.Key.Id,
                g.Key.EffectiveAt,
                g.Key.SourceDocumentType,
                g.Key.SourceDocumentNo,
                g.Key.PostingRole
            })
            .OrderByDescending(x => x.EffectiveAt)
            .ThenByDescending(x => x.Id)
            .Take(50)
            .ToListAsync(cancellationToken);

        var postingIds = rows.Select(x => x.Id).ToArray();
        var reversed = postingIds.Length == 0
            ? new HashSet<long>()
            : (await db.StockPostings.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.ReversesPostingId != null
                    && postingIds.Contains(x.ReversesPostingId.Value)
                    && x.SealedAtUtc != null)
                .Select(x => x.ReversesPostingId!.Value)
                .ToListAsync(cancellationToken))
                .ToHashSet();

        var blockers = rows.Select(x => new CostingBackdateBlocker(
            x.Id,
            x.EffectiveAt,
            x.SourceDocumentType,
            x.SourceDocumentNo,
            InventoryValuationService.FormatBlockingDocumentLabel(
                x.SourceDocumentType, x.SourceDocumentNo, null),
            x.PostingRole,
            reversed.Contains(x.Id))).ToArray();

        var actionable = blockers.Count(x => !x.AlreadyReversed);
        var asOfText = asOf.ToString("yyyy-MM-dd HH:mm");
        var guidance = actionable == 0
            ? $"No open later PRIMARY cost postings for {item} after {asOfText}. You can post a document dated on/before this time for this item."
            : $"To post {item} on/before {asOfText}, roll back these {actionable} later document(s) newest-first (Preview rollback on each), then post, then re-post them in date order.";

        return new CostingBackdateImpactPage
        {
            ItemCode = item,
            AsOf = asOf,
            Guidance = guidance,
            Blockers = blockers
        };
    }

    private static async Task<string> DescribeEpochAsync(
        AppDbContext db, string company, string branch, CancellationToken cancellationToken)
    {
        var epochs = await db.StockLedgerEpochs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new { x.Id, x.Status })
            .ToListAsync(cancellationToken);
        var active = epochs.Where(x => string.Equals(x.Status, StockLedgerEpochStatuses.Active, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (active.Length == 0)
            return CostingEpochCoverage.NoActiveEpoch;

        var activeIds = active.Select(x => x.Id).ToArray();
        var retiredIds = epochs
            .Where(x => string.Equals(x.Status, StockLedgerEpochStatuses.Retired, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id)
            .ToArray();
        var retiredFacts = retiredIds.Length > 0 && await db.StockValuationFacts.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && retiredIds.Contains(x.LedgerEpochId)
            && x.StockPosting!.SealedAtUtc != null, cancellationToken);
        var activeFacts = await db.StockValuationFacts.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && activeIds.Contains(x.LedgerEpochId)
            && x.StockPosting!.SealedAtUtc != null, cancellationToken);
        return retiredFacts && activeFacts
            ? CostingEpochCoverage.UnresolvedCrossEpoch
            : CostingEpochCoverage.V2;
    }

    private sealed record FactRow(
        long StockPostingId,
        long PostingSequence,
        int PostingLineNo,
        int SplitOrdinal,
        DateTime EffectiveAt,
        string SourceDocumentType,
        string SourceDocumentNo,
        string MovementCode,
        string? WarehouseCode,
        int Direction,
        decimal BaseQty,
        decimal CostAmount,
        decimal UnitCost,
        string ValuationSource,
        string ValuationStatus,
        bool IsReversal);
}
