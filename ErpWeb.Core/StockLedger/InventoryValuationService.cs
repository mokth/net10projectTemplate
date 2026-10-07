using System.Globalization;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public interface IInventoryValuationService
{
    /// <summary>
    /// Values every new V2 inventory-history row in the supplied posting context. The caller owns
    /// the DbContext, branch lock and transaction; this method never saves or commits.
    /// </summary>
    Task<IReadOnlyList<StockValuationFact>> ValuePendingAsync(
        StockPostingContext context,
        CancellationToken cancellationToken = default);
}

public sealed class InventoryValuationService : IInventoryValuationService
{
    private const int MoneyScale = 6;
    private const decimal Epsilon = 0.0000005m;
    private readonly IStockCostMethodResolver _costMethodResolver;
    private readonly IItemStandardCostResolver _standardCostResolver;
    private readonly InventoryCostingStrategyResolver _strategyResolver;

    public InventoryValuationService(
        IStockCostMethodResolver? costMethodResolver = null,
        IItemStandardCostResolver? standardCostResolver = null)
    {
        _costMethodResolver = costMethodResolver ?? new StockCostMethodResolver();
        _standardCostResolver = standardCostResolver ?? new ItemStandardCostResolver();
        _strategyResolver = new InventoryCostingStrategyResolver(
        [
            new MovingAverageCostingStrategy(ValueMovingAveragePendingAsync),
            new FifoCostingStrategy(ValueFifoPendingAsync),
            new StandardCostingStrategy(ValueStandardPendingAsync)
        ]);
    }

    public async Task<IReadOnlyList<StockValuationFact>> ValuePendingAsync(
        StockPostingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();

        var method = await _costMethodResolver.ResolveAsync(
            context.Db,
            context.CompanyCode,
            context.BranchCode,
            context.Posting.EffectiveAt,
            cancellationToken);
        context.CostMethod = method;

        return await _strategyResolver.Resolve(method)
            .ValuePendingAsync(context, [], cancellationToken);
    }

    private async Task<IReadOnlyList<StockValuationFact>> ValueMovingAveragePendingAsync(
        StockPostingContext context,
        IReadOnlyList<IvTrxHistory> _,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();

        var db = context.Db;
        var trackedHistories = db.ChangeTracker.Entries<IvTrxHistory>()
            .Where(x => x.State == EntityState.Added
                        && x.Entity.StockPostingId == context.Posting.Id
                        && x.Entity.LedgerVersion == 2)
            .Select(x => x.Entity)
            .ToList();
        var persistedHistories = await db.IvTrxHistories
            .Where(x => x.StockPostingId == context.Posting.Id
                        && x.LedgerVersion == 2
                        && !db.StockValuationFacts.Any(f => f.InventoryHistoryId == x.Id))
            .ToListAsync(cancellationToken);
        foreach (var persisted in persistedHistories)
        {
            if (!trackedHistories.Any(x => x.Id > 0 && x.Id == persisted.Id))
                trackedHistories.Add(persisted);
        }
        var histories = trackedHistories
            .OrderBy(x => x.PostingLineNo)
            .ThenBy(x => x.TrxLineNo)
            .ToArray();
        if (histories.Length == 0)
            return [];

        var itemCodes = histories
            .Where(IsFinanciallyRelevant)
            .Select(x => x.ICode.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var states = itemCodes.Length == 0
            ? new Dictionary<string, StockCostState>(StringComparer.OrdinalIgnoreCase)
            : (await db.StockCostStates.Where(x =>
                    x.CompanyCode == context.CompanyCode
                    && x.BranchCode == context.BranchCode
                    && x.CostMethod == context.CostMethod
                    && itemCodes.Contains(x.ItemCode))
                .ToListAsync(cancellationToken))
                .ToDictionary(x => x.ItemCode, StringComparer.OrdinalIgnoreCase);

        if (itemCodes.Length > 0)
            await RejectBackdatedPoolsAsync(context, itemCodes, cancellationToken);

        var detailEvidence = await LoadDetailEvidenceAsync(context, histories, cancellationToken);
        var baseCurrency = await db.Companies.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode)
            .Select(x => x.CurrencyCode)
            .FirstOrDefaultAsync(cancellationToken);

        var result = new List<StockValuationFact>();
        foreach (var history in histories)
        {
            if (!IsFinanciallyRelevant(history))
                continue;

            if (string.Equals(history.EntryRole, "REVERSAL", StringComparison.OrdinalIgnoreCase))
            {
                await AppendExactReversalAsync(
                    context, history, states, result, baseCurrency, cancellationToken);
                history.ValuationStatus = StockValuationStatuses.Reversed;
                continue;
            }

            var detail = detailEvidence.GetValueOrDefault((history.BatchNo, history.TrxLineNo));
            if (history.FromBalLocId is not null && (history.FrStdQty ?? 0m) > 0m)
            {
                var fact = await AppendIssueAsync(
                    context, history, detail, states, splitOrdinal: 0, result, baseCurrency,
                    cancellationToken);
                if (history.ToBalLocId is not null && (history.ToStdQty ?? 0m) > 0m)
                {
                    AppendReceipt(
                        context, history, detail, states, splitOrdinal: 1,
                        forcedAmount: fact.CostAmount,
                        forcedUnitCost: fact.UnitCost,
                        forcedSource: StockValuationSources.MovingAverage,
                        originalFactId: null,
                        result, baseCurrency);
                }
            }
            else if (history.ToBalLocId is not null && (history.ToStdQty ?? 0m) > 0m)
            {
                await AppendReceiptWithResolvedCostAsync(
                    context, history, detail, states, result, baseCurrency, cancellationToken);
            }

            history.ValuationStatus = StockValuationStatuses.Valued;
        }

        await SynchronizeProductionMaterialCostAsync(db, result, cancellationToken);
        db.StockValuationFacts.AddRange(result);
        return result;
    }

    private async Task<IReadOnlyList<StockValuationFact>> ValueStandardPendingAsync(
        StockPostingContext context,
        IReadOnlyList<IvTrxHistory> _,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();

        var db = context.Db;
        var trackedHistories = db.ChangeTracker.Entries<IvTrxHistory>()
            .Where(x => x.State == EntityState.Added
                        && x.Entity.StockPostingId == context.Posting.Id
                        && x.Entity.LedgerVersion == 2)
            .Select(x => x.Entity)
            .ToList();
        var persistedHistories = await db.IvTrxHistories
            .Where(x => x.StockPostingId == context.Posting.Id
                        && x.LedgerVersion == 2
                        && !db.StockValuationFacts.Any(f => f.InventoryHistoryId == x.Id))
            .ToListAsync(cancellationToken);
        foreach (var persisted in persistedHistories)
        {
            if (!trackedHistories.Any(x => x.Id > 0 && x.Id == persisted.Id))
                trackedHistories.Add(persisted);
        }

        var histories = trackedHistories
            .OrderBy(x => x.PostingLineNo)
            .ThenBy(x => x.TrxLineNo)
            .ToArray();
        if (histories.Length == 0)
            return [];

        var itemCodes = histories
            .Where(IsFinanciallyRelevant)
            .Select(x => x.ICode.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var states = itemCodes.Length == 0
            ? new Dictionary<string, StockCostState>(StringComparer.OrdinalIgnoreCase)
            : (await db.StockCostStates.Where(x =>
                    x.CompanyCode == context.CompanyCode
                    && x.BranchCode == context.BranchCode
                    && x.CostMethod == context.CostMethod
                    && itemCodes.Contains(x.ItemCode))
                .ToListAsync(cancellationToken))
                .ToDictionary(x => x.ItemCode, StringComparer.OrdinalIgnoreCase);

        if (itemCodes.Length > 0)
            await RejectBackdatedPoolsAsync(context, itemCodes, cancellationToken);

        var detailEvidence = await LoadDetailEvidenceAsync(context, histories, cancellationToken);
        var baseCurrency = await db.Companies.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode)
            .Select(x => x.CurrencyCode)
            .FirstOrDefaultAsync(cancellationToken);
        var standardCosts = new Dictionary<string, EffectiveStandardCost>(StringComparer.OrdinalIgnoreCase);

        async Task<EffectiveStandardCost> StandardForAsync(string itemCode)
        {
            if (standardCosts.TryGetValue(itemCode, out var cached))
                return cached;
            var resolved = await _standardCostResolver.ResolveAsync(
                db, context.CompanyCode, context.BranchCode, itemCode,
                context.Posting.EffectiveAt, cancellationToken);
            standardCosts[itemCode] = resolved;
            return resolved;
        }

        var result = new List<StockValuationFact>();
        foreach (var history in histories)
        {
            if (!IsFinanciallyRelevant(history))
                continue;

            if (string.Equals(history.EntryRole, "REVERSAL", StringComparison.OrdinalIgnoreCase))
            {
                await AppendExactReversalAsync(
                    context, history, states, result, baseCurrency, cancellationToken);
                await AppendProductionStandardVarianceReversalsAsync(
                    context, history, result, cancellationToken);
                await AppendStandardReturnVarianceReversalsAsync(
                    context, history, result, cancellationToken);
                history.ValuationStatus = StockValuationStatuses.Reversed;
                continue;
            }

            var detail = detailEvidence.GetValueOrDefault((history.BatchNo, history.TrxLineNo));
            var standard = await StandardForAsync(Required(history.ICode, "item code"));

            if (history.FromBalLocId is not null && (history.FrStdQty ?? 0m) > 0m)
            {
                var issue = await AppendIssueAsync(
                    context, history, detail, states, splitOrdinal: 0, result, baseCurrency,
                    cancellationToken, standard.TotalStandardCost, StockValuationSources.Standard);
                if (history.ToBalLocId is not null && (history.ToStdQty ?? 0m) > 0m)
                {
                    AppendReceipt(
                        context, history, detail, states, splitOrdinal: 1,
                        forcedAmount: issue.CostAmount,
                        forcedUnitCost: issue.UnitCost,
                        forcedSource: StockValuationSources.Standard,
                        originalFactId: null,
                        result, baseCurrency);
                }
            }
            else if (history.ToBalLocId is not null && (history.ToStdQty ?? 0m) > 0m)
            {
                if (string.Equals(history.TrxType, IvTrxTypes.CustomerReturn, StringComparison.OrdinalIgnoreCase))
                {
                    await AppendStandardCustomerReturnAsync(
                        context, history, detail, states, standard, result, baseCurrency, cancellationToken);
                }
                else
                {
                    var standardAmount = RoundMoney(
                        Positive(history.ToStdQty, "receipt quantity") * standard.TotalStandardCost);
                    var fact = AppendReceipt(
                        context, history, detail, states, splitOrdinal: 0,
                        forcedAmount: standardAmount,
                        forcedUnitCost: standard.TotalStandardCost,
                        forcedSource: StockValuationSources.Standard,
                        originalFactId: null,
                        result, baseCurrency);
                    AppendProductionStandardVarianceIfNeeded(context, history, fact);
                }
            }

            history.ValuationStatus = StockValuationStatuses.Valued;
        }

        await SynchronizeProductionMaterialCostAsync(db, result, cancellationToken);
        db.StockValuationFacts.AddRange(result);
        return result;
    }

    private static async Task AppendStandardCustomerReturnAsync(
        StockPostingContext context,
        IvTrxHistory history,
        DetailEvidence? detail,
        IDictionary<string, StockCostState> states,
        EffectiveStandardCost standard,
        ICollection<StockValuationFact> result,
        string? baseCurrency,
        CancellationToken cancellationToken)
    {
        var quantity = Positive(history.ToStdQty, "return quantity");
        var original = await ResolveOriginalSaleCostAsync(
            context, history, detail, quantity, cancellationToken);
        var fact = AppendReceipt(
            context, history, detail, states, splitOrdinal: 0,
            forcedAmount: RoundMoney(quantity * standard.TotalStandardCost),
            forcedUnitCost: standard.TotalStandardCost,
            forcedSource: StockValuationSources.Standard,
            originalFactId: null,
            result, baseCurrency);

        foreach (var allocation in original.Allocations)
        {
            context.Db.SalesReturnCostAllocations.Add(new SalesReturnCostAllocation
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                ReturnDocumentType = "SA_CDN",
                ReturnDocumentNo = ResolveReturnDocumentNo(history, detail, context),
                ReturnDocumentLine = history.TrxLineNo,
                ReturnCostingRevision = context.Posting.DocumentRevision,
                OriginalValuationFactId = allocation.OriginalFactId,
                OriginalOwnerType = allocation.OwnerType,
                OriginalOwnerDocumentNo = allocation.OwnerDocumentNo,
                OriginalOwnerDocumentLine = allocation.OwnerDocumentLine,
                ReturnedBaseQty = allocation.Quantity,
                ReturnedCostAmount = allocation.CostAmount,
                StockPostingId = context.Posting.Id,
                ReturnValuationFact = fact,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = context.UserId
            });
        }

        // Keep the exact original COGS allocation as report evidence and persist the difference
        // outside Inventory value. The return receipt remains at the current effective Standard
        // Cost; this row is the explicit management/GL bridge for the mismatch.
        context.Db.SalesReturnStandardCostVariances.Add(new SalesReturnStandardCostVariance
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            StockPostingId = context.Posting.Id,
            ReturnValuationFactId = fact.Id,
            ReturnValuationFact = fact,
            ReturnDocumentType = "SA_CDN",
            ReturnDocumentNo = ResolveReturnDocumentNo(history, detail, context),
            ReturnDocumentLine = history.TrxLineNo,
            ReturnCostingRevision = context.Posting.DocumentRevision,
            ItemCode = fact.ItemCode,
            BaseQty = fact.BaseQty,
            CurrentStandardReceiptValue = RoundMoney(fact.CostAmount),
            OriginalCogsReversalValue = RoundMoney(original.TotalCost),
            VarianceAmount = RoundMoney(fact.CostAmount - original.TotalCost),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = context.UserId
        });
    }

    private static void AppendProductionStandardVarianceIfNeeded(
        StockPostingContext context,
        IvTrxHistory history,
        StockValuationFact inventoryFact)
    {
        if (!string.Equals(history.TrxType, IvTrxTypes.FinishedGoods, StringComparison.OrdinalIgnoreCase)
            || history.ExactTransferredValue is not decimal actual)
            return;
        if (actual < 0m)
            throw LedgerError(StockLedgerErrorCodes.ValuationRequired,
                $"Finished-good actual production value for item '{history.ICode}' cannot be negative.");

        var actualValue = RoundMoney(Math.Abs(actual));
        var standardValue = RoundMoney(inventoryFact.CostAmount);
        context.Db.ProductionStandardCostVariances.Add(new ProductionStandardCostVariance
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            StockPostingId = context.Posting.Id,
            ProductionPostingLinkId = context.Posting.ProductionPostingLinkId,
            InventoryValuationFact = inventoryFact,
            BaseQty = inventoryFact.BaseQty,
            ActualProductionValue = actualValue,
            StandardInventoryValue = standardValue,
            VarianceAmount = RoundMoney(actualValue - standardValue),
            ItemCode = inventoryFact.ItemCode,
            EffectiveAt = inventoryFact.EffectiveAt,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = context.UserId
        });
    }

    private static async Task AppendProductionStandardVarianceReversalsAsync(
        StockPostingContext context,
        IvTrxHistory reversalHistory,
        IReadOnlyCollection<StockValuationFact> reversalFacts,
        CancellationToken cancellationToken)
    {
        if (reversalHistory.ReversesHistoryId is not int historyId)
            return;
        var originalFacts = await context.Db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.InventoryHistoryId == historyId)
            .Select(x => x.Id)
            .ToArrayAsync(cancellationToken);
        if (originalFacts.Length == 0)
            return;
        var originals = await context.Db.ProductionStandardCostVariances.AsNoTracking()
            .Where(x => originalFacts.Contains(x.InventoryValuationFactId)
                        && x.ReversesVarianceId == null)
            .ToListAsync(cancellationToken);
        foreach (var original in originals)
        {
            if (await context.Db.ProductionStandardCostVariances.AsNoTracking().AnyAsync(
                    x => x.ReversesVarianceId == original.Id, cancellationToken))
                throw LedgerError(StockLedgerErrorCodes.ReversalAlreadyExists,
                    $"Production Standard variance {original.Id} already has a reversal.");
            var reversedFact = reversalFacts.SingleOrDefault(
                x => x.ReversesValuationFactId == original.InventoryValuationFactId);
            if (reversedFact is null)
                throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                    $"Standard variance {original.Id} has no exact Inventory fact reversal.");
            context.Db.ProductionStandardCostVariances.Add(new ProductionStandardCostVariance
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                StockPostingId = context.Posting.Id,
                ProductionPostingLinkId = context.Posting.ProductionPostingLinkId,
                InventoryValuationFact = reversedFact,
                BaseQty = original.BaseQty,
                ActualProductionValue = -original.ActualProductionValue,
                StandardInventoryValue = -original.StandardInventoryValue,
                VarianceAmount = -original.VarianceAmount,
                ItemCode = original.ItemCode,
                EffectiveAt = context.Posting.EffectiveAt,
                ReversesVarianceId = original.Id,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = context.UserId
            });
        }
    }

    private static async Task AppendStandardReturnVarianceReversalsAsync(
        StockPostingContext context,
        IvTrxHistory reversalHistory,
        IReadOnlyCollection<StockValuationFact> reversalFacts,
        CancellationToken cancellationToken)
    {
        if (reversalHistory.ReversesHistoryId is not int historyId)
            return;

        var originalFactIds = await context.Db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.InventoryHistoryId == historyId)
            .Select(x => x.Id)
            .ToArrayAsync(cancellationToken);
        if (originalFactIds.Length == 0)
            return;

        var originals = await context.Db.SalesReturnStandardCostVariances.AsNoTracking()
            .Where(x => originalFactIds.Contains(x.ReturnValuationFactId)
                        && x.ReversesVarianceId == null)
            .ToListAsync(cancellationToken);
        foreach (var original in originals)
        {
            if (await context.Db.SalesReturnStandardCostVariances.AsNoTracking()
                    .AnyAsync(x => x.ReversesVarianceId == original.Id, cancellationToken))
                throw LedgerError(StockLedgerErrorCodes.ReversalAlreadyExists,
                    $"Standard return variance {original.Id} already has a reversal.");

            var reversedFact = reversalFacts.SingleOrDefault(
                x => x.ReversesValuationFactId == original.ReturnValuationFactId);
            if (reversedFact is null)
                throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                    $"Standard return variance {original.Id} has no exact Inventory fact reversal.");

            context.Db.SalesReturnStandardCostVariances.Add(new SalesReturnStandardCostVariance
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                StockPostingId = context.Posting.Id,
                ReturnValuationFactId = reversedFact.Id,
                ReturnValuationFact = reversedFact,
                ReturnDocumentType = original.ReturnDocumentType,
                ReturnDocumentNo = original.ReturnDocumentNo,
                ReturnDocumentLine = original.ReturnDocumentLine,
                ReturnCostingRevision = context.Posting.DocumentRevision,
                ItemCode = original.ItemCode,
                BaseQty = original.BaseQty,
                CurrentStandardReceiptValue = -original.CurrentStandardReceiptValue,
                OriginalCogsReversalValue = -original.OriginalCogsReversalValue,
                VarianceAmount = -original.VarianceAmount,
                ReversesVarianceId = original.Id,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = context.UserId
            });
        }
    }

    private async Task<IReadOnlyList<StockValuationFact>> ValueFifoPendingAsync(
        StockPostingContext context,
        IReadOnlyList<IvTrxHistory> _,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();

        var db = context.Db;
        var trackedHistories = db.ChangeTracker.Entries<IvTrxHistory>()
            .Where(x => x.State == EntityState.Added
                        && x.Entity.StockPostingId == context.Posting.Id
                        && x.Entity.LedgerVersion == 2)
            .Select(x => x.Entity)
            .ToList();
        var persistedHistories = await db.IvTrxHistories
            .Where(x => x.StockPostingId == context.Posting.Id
                        && x.LedgerVersion == 2
                        && !db.StockValuationFacts.Any(f => f.InventoryHistoryId == x.Id))
            .ToListAsync(cancellationToken);
        foreach (var persisted in persistedHistories)
        {
            if (!trackedHistories.Any(x => x.Id > 0 && x.Id == persisted.Id))
                trackedHistories.Add(persisted);
        }

        var histories = trackedHistories
            .OrderBy(x => x.PostingLineNo)
            .ThenBy(x => x.TrxLineNo)
            .ToArray();
        if (histories.Length == 0)
            return [];

        var itemCodes = histories
            .Where(IsFinanciallyRelevant)
            .Select(x => x.ICode.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var states = itemCodes.Length == 0
            ? new Dictionary<string, StockCostState>(StringComparer.OrdinalIgnoreCase)
            : (await db.StockCostStates.Where(x =>
                    x.CompanyCode == context.CompanyCode
                    && x.BranchCode == context.BranchCode
                    && x.CostMethod == context.CostMethod
                    && itemCodes.Contains(x.ItemCode))
                .ToListAsync(cancellationToken))
                .ToDictionary(x => x.ItemCode, StringComparer.OrdinalIgnoreCase);
        if (itemCodes.Length > 0)
            await RejectBackdatedPoolsAsync(context, itemCodes, cancellationToken);

        var layers = await db.StockFifoLayers
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && itemCodes.Contains(x.ItemCode))
            .OrderBy(x => x.ReceiptEffectiveAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        foreach (var tracked in db.ChangeTracker.Entries<StockFifoLayer>()
                     .Where(x => x.State == EntityState.Added
                                 && x.Entity.CompanyCode == context.CompanyCode
                                 && x.Entity.BranchCode == context.BranchCode
                                 && itemCodes.Contains(x.Entity.ItemCode))
                     .Select(x => x.Entity))
        {
            if (!layers.Contains(tracked))
                layers.Add(tracked);
        }

        var layersByItem = layers
            .GroupBy(x => x.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.ReceiptEffectiveAt).ThenBy(y => y.Id).ToList(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var itemCode in itemCodes)
            layersByItem.TryAdd(itemCode, []);
        var detailEvidence = await LoadDetailEvidenceAsync(context, histories, cancellationToken);
        var baseCurrency = await db.Companies.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode)
            .Select(x => x.CurrencyCode)
            .FirstOrDefaultAsync(cancellationToken);

        var result = new List<StockValuationFact>();
        foreach (var history in histories)
        {
            if (!IsFinanciallyRelevant(history))
                continue;

            if (string.Equals(history.EntryRole, "REVERSAL", StringComparison.OrdinalIgnoreCase))
            {
                await AppendFifoExactReversalAsync(
                    context, history, states, layersByItem, result, baseCurrency, cancellationToken);
                history.ValuationStatus = StockValuationStatuses.Reversed;
                continue;
            }

            var detail = detailEvidence.GetValueOrDefault((history.BatchNo, history.TrxLineNo));
            if (history.FromBalLocId is not null && (history.FrStdQty ?? 0m) > 0m)
            {
                var issueFacts = await AppendFifoIssueAsync(
                    context, history, states, layersByItem, result, baseCurrency, cancellationToken);
                if (history.ToBalLocId is not null && (history.ToStdQty ?? 0m) > 0m)
                {
                    var quantity = Positive(history.ToStdQty, "transfer receipt quantity");
                    var amount = RoundMoney(issueFacts.Sum(x => x.CostAmount));
                    // A multi-layer issue already uses split 0..n-1. The destination fact must
                    // take the next ordinal or the posting-line identity collides.
                    AppendReceipt(
                        context, history, detail, states, splitOrdinal: issueFacts.Count,
                        forcedAmount: amount,
                        forcedUnitCost: quantity == 0m ? 0m : RoundMoney(amount / quantity),
                        forcedSource: StockValuationSources.Fifo,
                        originalFactId: null,
                        result, baseCurrency);
                    // Transfers are value-neutral in the branch/item financial pool. The receipt
                    // fact restores quantity/value but deliberately does not create a new layer.
                }
            }
            else if (history.ToBalLocId is not null && (history.ToStdQty ?? 0m) > 0m)
            {
                if (string.Equals(history.TrxType, IvTrxTypes.CustomerReturn, StringComparison.OrdinalIgnoreCase))
                {
                    await AppendFifoCustomerReturnAsync(
                        context, history, detail, states, layersByItem, result, baseCurrency, cancellationToken);
                }
                else
                {
                    var quantity = Positive(history.ToStdQty, "receipt quantity");
                    var unitCost = ResolveFifoReceiptUnitCost(history, detail, states);
                    var fact = AppendReceipt(
                        context, history, detail, states, splitOrdinal: 0,
                        forcedAmount: RoundMoney(quantity * unitCost),
                        forcedUnitCost: unitCost,
                        forcedSource: StockValuationSources.Fifo,
                        originalFactId: null,
                        result, baseCurrency);
                    AddFifoLayer(context, layersByItem, fact);
                }
            }

            history.ValuationStatus = StockValuationStatuses.Valued;
        }

        await SynchronizeProductionMaterialCostAsync(db, result, cancellationToken);
        db.StockValuationFacts.AddRange(result);
        return result;
    }

    private static async Task<IReadOnlyList<StockValuationFact>> AppendFifoIssueAsync(
        StockPostingContext context,
        IvTrxHistory history,
        IDictionary<string, StockCostState> states,
        IReadOnlyDictionary<string, List<StockFifoLayer>> layersByItem,
        ICollection<StockValuationFact> result,
        string? baseCurrency,
        CancellationToken cancellationToken)
    {
        var itemCode = Required(history.ICode, "item code");
        var quantity = Positive(history.FrStdQty, "issue quantity");
        if (!states.TryGetValue(itemCode, out var state))
            throw LedgerError(StockLedgerErrorCodes.ValuationRequired,
                $"Item '{itemCode}' has no approved FIFO opening or receipt layer.");
        if (!layersByItem.TryGetValue(itemCode, out var layers))
            throw LedgerError(StockLedgerErrorCodes.ValuationRequired,
                $"Item '{itemCode}' has no FIFO layers to consume.");

        EnsureFifoStateReconciles(itemCode, state, layers);
        if (quantity > state.OnHandBaseQty + Epsilon)
            throw LedgerError(StockLedgerErrorCodes.InsufficientBaseQty,
                $"FIFO quantity for item '{itemCode}' is {state.OnHandBaseQty}, but {quantity} is required.");

        var remaining = quantity;
        var ordinal = 0;
        var facts = new List<StockValuationFact>();
        foreach (var layer in layers
                     .Where(x => x.Status == StockFifoLayerStatuses.Open && x.RemainingQty > Epsilon)
                     .OrderBy(x => x.ReceiptEffectiveAt)
                     .ThenBy(x => x.OriginValuationFactId)
                     .ThenBy(x => x.Id))
        {
            if (remaining <= Epsilon)
                break;
            var take = RoundQuantity(Math.Min(remaining, layer.RemainingQty));
            if (take <= 0m)
                continue;
            var value = take >= layer.RemainingQty - Epsilon
                ? layer.RemainingValue
                : RoundMoney(layer.RemainingValue * take / layer.RemainingQty);
            var unitCost = take == 0m ? 0m : RoundMoney(value / take);
            var fact = NewFact(
                context, history, ordinal, direction: -1, take, unitCost, value,
                MovementCode(history, direction: -1), StockValuationSources.Fifo,
                baseCurrency, null, null, null, null, null);
            var consumption = new StockFifoLayerConsumption
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                IssueValuationFact = fact,
                FifoLayer = layer,
                ConsumedQty = take,
                ConsumedValue = value,
                SplitOrdinal = ordinal,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = context.UserId
            };
            context.Db.StockFifoLayerConsumptions.Add(consumption);
            layer.RemainingQty = RoundQuantity(layer.RemainingQty - take);
            layer.RemainingValue = RoundMoney(layer.RemainingValue - value);
            layer.CurrentUnitCost = layer.RemainingQty <= Epsilon
                ? 0m
                : RoundMoney(layer.RemainingValue / layer.RemainingQty);
            layer.Status = layer.RemainingQty <= Epsilon ? StockFifoLayerStatuses.Closed : StockFifoLayerStatuses.Open;
            facts.Add(fact);
            result.Add(fact);
            remaining = RoundQuantity(remaining - take);
            ordinal++;
        }

        if (remaining > Epsilon)
            throw LedgerError(StockLedgerErrorCodes.InsufficientBaseQty,
                $"FIFO layers for item '{itemCode}' cannot satisfy issue quantity {quantity}.");

        var amount = RoundMoney(facts.Sum(x => x.CostAmount));
        ApplyOutbound(state, quantity, amount, facts[^1], context.Posting.PostingSequence);
        await Task.CompletedTask;
        return facts;
    }

    private static async Task AppendFifoCustomerReturnAsync(
        StockPostingContext context,
        IvTrxHistory history,
        DetailEvidence? detail,
        IDictionary<string, StockCostState> states,
        IReadOnlyDictionary<string, List<StockFifoLayer>> layersByItem,
        ICollection<StockValuationFact> result,
        string? baseCurrency,
        CancellationToken cancellationToken)
    {
        var quantity = Positive(history.ToStdQty, "return quantity");
        var original = await ResolveOriginalSaleCostAsync(
            context, history, detail, quantity, cancellationToken);
        var fact = AppendReceipt(
            context, history, detail, states, 0,
            original.TotalCost,
            original.UnitCost,
            StockValuationSources.OriginalSaleReturn,
            null,
            result, baseCurrency);
        AddFifoLayer(context, layersByItem, fact);

        foreach (var allocation in original.Allocations)
        {
            context.Db.SalesReturnCostAllocations.Add(new SalesReturnCostAllocation
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                ReturnDocumentType = "SA_CDN",
                ReturnDocumentNo = ResolveReturnDocumentNo(history, detail, context),
                ReturnDocumentLine = history.TrxLineNo,
                ReturnCostingRevision = context.Posting.DocumentRevision,
                OriginalValuationFactId = allocation.OriginalFactId,
                OriginalOwnerType = allocation.OwnerType,
                OriginalOwnerDocumentNo = allocation.OwnerDocumentNo,
                OriginalOwnerDocumentLine = allocation.OwnerDocumentLine,
                ReturnedBaseQty = allocation.Quantity,
                ReturnedCostAmount = allocation.CostAmount,
                StockPostingId = context.Posting.Id,
                ReturnValuationFact = fact,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = context.UserId
            });
        }
    }

    private static async Task AppendFifoExactReversalAsync(
        StockPostingContext context,
        IvTrxHistory reversalHistory,
        IDictionary<string, StockCostState> states,
        IReadOnlyDictionary<string, List<StockFifoLayer>> layersByItem,
        ICollection<StockValuationFact> result,
        string? baseCurrency,
        CancellationToken cancellationToken)
    {
        if (reversalHistory.ReversesHistoryId is not int historyId)
            throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                "A FIFO history reversal must identify the original history row.");

        var originals = await context.Db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.InventoryHistoryId == historyId)
            .OrderBy(x => x.SplitOrdinal)
            .ToListAsync(cancellationToken);
        if (originals.Count == 0)
            throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                $"History {historyId} has no authoritative FIFO valuation to reverse.");
        var originalIds = originals.Select(x => x.Id).ToArray();
        if (await context.Db.StockValuationFacts.AsNoTracking().AnyAsync(
                x => x.ReversesValuationFactId != null
                     && originalIds.Contains(x.ReversesValuationFactId.Value), cancellationToken))
            throw LedgerError(StockLedgerErrorCodes.ReversalAlreadyExists,
                $"History {historyId} already has a FIFO valuation reversal.");

        var consumptions = await context.Db.StockFifoLayerConsumptions.AsNoTracking()
            .Where(x => originalIds.Contains(x.IssueValuationFactId))
            .OrderBy(x => x.IssueValuationFactId)
            .ThenBy(x => x.SplitOrdinal)
            .ToListAsync(cancellationToken);
        var consumptionByFact = consumptions
            .GroupBy(x => x.IssueValuationFactId)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.SplitOrdinal).ToArray());

        foreach (var original in originals)
        {
            var itemCode = original.ItemCode;
            if (!states.TryGetValue(itemCode, out var state))
                state = GetOrCreateState(context, states, itemCode);
            if (!layersByItem.TryGetValue(itemCode, out var layers))
                throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                    $"Item '{itemCode}' has no FIFO layers for exact reversal.");

            var reversalFact = NewFact(
                context, reversalHistory, original.SplitOrdinal, -original.Direction,
                original.BaseQty, original.UnitCost, original.CostAmount,
                original.MovementCode + "_REVERSAL", StockValuationSources.OriginalReversal,
                baseCurrency ?? original.BaseCurrency,
                original.TransactionCurrency, original.TransactionCostAmount, original.ExchangeRate,
                original.OriginalValuationFactId ?? original.Id, original.Id);

            if (original.Direction < 0)
            {
                if (!consumptionByFact.TryGetValue(original.Id, out var originalConsumptions)
                    || originalConsumptions.Length == 0)
                    throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                        $"FIFO issue fact {original.Id} has no layer consumption evidence.");

                foreach (var consumption in originalConsumptions)
                {
                    var layer = layers.SingleOrDefault(x => x.Id == consumption.FifoLayerId);
                    if (layer is null)
                        throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                            $"FIFO layer {consumption.FifoLayerId} was not found for issue fact {original.Id}.");
                    layer.RemainingQty = RoundQuantity(layer.RemainingQty + consumption.ConsumedQty);
                    layer.RemainingValue = RoundMoney(layer.RemainingValue + consumption.ConsumedValue);
                    layer.CurrentUnitCost = layer.RemainingQty <= Epsilon
                        ? 0m
                        : RoundMoney(layer.RemainingValue / layer.RemainingQty);
                    layer.Status = StockFifoLayerStatuses.Open;
                    context.Db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
                    {
                        CompanyCode = context.CompanyCode,
                        BranchCode = context.BranchCode,
                        IssueValuationFact = reversalFact,
                        FifoLayer = layer,
                        ConsumedQty = consumption.ConsumedQty,
                        ConsumedValue = consumption.ConsumedValue,
                        SplitOrdinal = consumption.SplitOrdinal,
                        ReversesConsumptionId = consumption.Id,
                        CreatedAtUtc = DateTime.UtcNow,
                        CreatedBy = context.UserId
                    });
                }
                ApplyInbound(state, reversalFact.BaseQty, reversalFact.CostAmount,
                    reversalFact, context.Posting.PostingSequence);
            }
            else
            {
                var layer = layers.SingleOrDefault(x => x.OriginValuationFactId == original.Id);
                var isTransferReceipt = string.Equals(original.MovementCode, "TRANSFER_IN", StringComparison.OrdinalIgnoreCase);
                if (layer is null && !isTransferReceipt)
                    throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                        $"FIFO receipt fact {original.Id} has no originating layer.");
                if (layer is not null)
                {
                    if (layer.RemainingQty + Epsilon < original.BaseQty
                        || layer.RemainingValue + Epsilon < original.CostAmount)
                        throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                            $"FIFO receipt fact {original.Id} has already been consumed; reverse later issues first.");
                    layer.RemainingQty = RoundQuantity(layer.RemainingQty - original.BaseQty);
                    layer.RemainingValue = RoundMoney(layer.RemainingValue - original.CostAmount);
                    layer.CurrentUnitCost = layer.RemainingQty <= Epsilon
                        ? 0m
                        : RoundMoney(layer.RemainingValue / layer.RemainingQty);
                    layer.Status = layer.RemainingQty <= Epsilon ? StockFifoLayerStatuses.Closed : StockFifoLayerStatuses.Open;
                }
                ApplyOutbound(state, reversalFact.BaseQty, reversalFact.CostAmount,
                    reversalFact, context.Posting.PostingSequence);
            }

            result.Add(reversalFact);
            var returnAllocations = await context.Db.SalesReturnCostAllocations.AsNoTracking()
                .Where(x => x.ReturnValuationFactId == original.Id
                            && x.CompanyCode == context.CompanyCode
                            && x.BranchCode == context.BranchCode)
                .ToListAsync(cancellationToken);
            foreach (var allocation in returnAllocations)
            {
                context.Db.SalesReturnCostAllocations.Add(new SalesReturnCostAllocation
                {
                    CompanyCode = context.CompanyCode,
                    BranchCode = context.BranchCode,
                    ReturnDocumentType = allocation.ReturnDocumentType,
                    ReturnDocumentNo = allocation.ReturnDocumentNo,
                    ReturnDocumentLine = allocation.ReturnDocumentLine,
                    ReturnCostingRevision = context.Posting.DocumentRevision,
                    OriginalValuationFactId = allocation.OriginalValuationFactId,
                    OriginalOwnerType = allocation.OriginalOwnerType,
                    OriginalOwnerDocumentNo = allocation.OriginalOwnerDocumentNo,
                    OriginalOwnerDocumentLine = allocation.OriginalOwnerDocumentLine,
                    ReturnedBaseQty = allocation.ReturnedBaseQty,
                    ReturnedCostAmount = allocation.ReturnedCostAmount,
                    StockPostingId = context.Posting.Id,
                    ReturnValuationFact = reversalFact,
                    ReversesAllocationId = allocation.Id,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = context.UserId
                });
            }
        }
    }

    private static void AddFifoLayer(
        StockPostingContext context,
        IReadOnlyDictionary<string, List<StockFifoLayer>> layersByItem,
        StockValuationFact receiptFact)
    {
        var layer = new StockFifoLayer
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            ItemCode = receiptFact.ItemCode,
            BaseUom = receiptFact.BaseUom,
            OriginValuationFact = receiptFact,
            OriginStockPosting = context.Posting,
            ReceiptEffectiveAt = receiptFact.EffectiveAt,
            OriginalQty = receiptFact.BaseQty,
            RemainingQty = receiptFact.BaseQty,
            OriginalValue = receiptFact.CostAmount,
            AccumulatedAdjustment = 0m,
            RemainingValue = receiptFact.CostAmount,
            CurrentUnitCost = receiptFact.UnitCost,
            SourceDocumentType = receiptFact.SourceDocumentType,
            SourceDocumentNo = receiptFact.SourceDocumentNo,
            SourceDocumentLine = receiptFact.SourceDocumentLine,
            WarehouseCode = receiptFact.WarehouseCode,
            LotId = receiptFact.LotId,
            LotNo = receiptFact.LotNo,
            Status = StockFifoLayerStatuses.Open,
        };
        context.Db.StockFifoLayers.Add(layer);
        if (!layersByItem.TryGetValue(receiptFact.ItemCode, out var layers))
            throw LedgerError(StockLedgerErrorCodes.LedgerMismatch,
                $"FIFO layer pool for item '{receiptFact.ItemCode}' was not initialized.");
        layers.Add(layer);
    }

    private static void EnsureFifoStateReconciles(
        string itemCode,
        StockCostState state,
        IReadOnlyCollection<StockFifoLayer> layers)
    {
        var qty = RoundQuantity(layers.Sum(x => x.RemainingQty));
        var value = RoundMoney(layers.Sum(x => x.RemainingValue));
        if (Math.Abs(qty - state.OnHandBaseQty) > Epsilon
            || Math.Abs(value - state.InventoryValue) > Epsilon)
            throw LedgerError(StockLedgerErrorCodes.LedgerMismatch,
                $"FIFO layers for item '{itemCode}' do not reconcile to the current cost state.");
    }

    private static bool IsFinanciallyRelevant(IvTrxHistory history) =>
        (history.FromBalLocId is not null && (history.FrStdQty ?? 0m) > 0m)
        || (history.ToBalLocId is not null && (history.ToStdQty ?? 0m) > 0m);

    private static async Task RejectBackdatedPoolsAsync(
        StockPostingContext context,
        IReadOnlyCollection<string> itemCodes,
        CancellationToken cancellationToken)
    {
        var sourceDocumentId = (context.Posting.SourceDocumentId ?? string.Empty).Trim();
        var laterRows = await context.Db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && itemCodes.Contains(x.ItemCode)
                        && x.EffectiveAt > context.Posting.EffectiveAt
                        && x.StockPosting!.SealedAtUtc != null
                        // Re-posting the same physical document must ignore its own prior sealed facts
                        // (post → rollback → post again, including orphan wall-clock EffectiveAt rows).
                        && (sourceDocumentId.Length == 0 || x.SourceDocumentId != sourceDocumentId))
            .Select(x => new
            {
                x.ItemCode,
                x.EffectiveAt,
                x.SourceDocumentType,
                x.SourceDocumentNo,
                x.SourceDocumentId,
                x.StockPostingId
            })
            .OrderBy(x => x.EffectiveAt)
            .ThenBy(x => x.StockPostingId)
            .Take(20)
            .ToListAsync(cancellationToken);
        if (laterRows.Count == 0)
            return;

        var blockers = laterRows
            .GroupBy(x => new
            {
                x.ItemCode,
                Type = (x.SourceDocumentType ?? string.Empty).Trim(),
                No = (x.SourceDocumentNo ?? string.Empty).Trim(),
                Id = (x.SourceDocumentId ?? string.Empty).Trim()
            })
            .Select(g => new BackdatedBlocker(
                g.Key.ItemCode,
                g.Min(x => x.EffectiveAt),
                g.Key.Type,
                g.Key.No,
                g.Key.Id,
                g.OrderBy(x => x.EffectiveAt).Select(x => x.StockPostingId).First()))
            .OrderBy(x => x.EffectiveAt)
            .Take(5)
            .ToArray();

        throw LedgerError(StockLedgerErrorCodes.BackdatedStockEvent,
            FormatBackdatedPostingMessage(context.Posting.EffectiveAt, blockers));
    }

    internal readonly record struct BackdatedBlocker(
        string ItemCode,
        DateTime EffectiveAt,
        string SourceDocumentType,
        string SourceDocumentNo,
        string SourceDocumentId,
        long StockPostingId);

    internal static string FormatBackdatedPostingMessage(
        DateTime attemptedEffectiveAt,
        IReadOnlyList<BackdatedBlocker> blockers)
    {
        if (blockers.Count == 0)
            return "Cannot post: a later cost movement already exists. Open Costing Center for the item.";

        var item = blockers[0].ItemCode;
        var asOf = attemptedEffectiveAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var lines = blockers.Select(b =>
        {
            var label = FormatBlockingDocumentLabel(b.SourceDocumentType, b.SourceDocumentNo, b.SourceDocumentId);
            var when = b.EffectiveAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return $"{label} on {when}";
        }).ToArray();

        var list = string.Join("; ", lines);
        var first = FormatBlockingDocumentLabel(
            blockers[0].SourceDocumentType, blockers[0].SourceDocumentNo, blockers[0].SourceDocumentId);
        return
            $"Cannot post for item {item} on {asOf}: later cost movement(s) already exist ({list}). " +
            $"Roll back those documents newest-first (start with {first}), post this document, then re-post them in date order. " +
            "Open Costing Center → enter the item → set From to this date → Later movements to roll back → Preview rollback on each later row.";
    }

    /// <summary>Legacy single-blocker formatter kept for unit tests and callers.</summary>
    internal static string FormatBackdatedPostingMessage(
        string itemCode,
        DateTime laterEffectiveAt,
        string? sourceDocumentType,
        string? sourceDocumentNo,
        string? sourceDocumentId) =>
        FormatBackdatedPostingMessage(
            laterEffectiveAt.Date,
            [
                new BackdatedBlocker(
                    itemCode,
                    laterEffectiveAt,
                    sourceDocumentType ?? string.Empty,
                    sourceDocumentNo ?? string.Empty,
                    sourceDocumentId ?? string.Empty,
                    0)
            ]);

    internal static string FormatBlockingDocumentLabel(
        string? sourceDocumentType,
        string? sourceDocumentNo,
        string? sourceDocumentId)
    {
        var type = (sourceDocumentType ?? string.Empty).Trim();
        var number = !string.IsNullOrWhiteSpace(sourceDocumentNo)
            ? sourceDocumentNo.Trim()
            : (sourceDocumentId ?? string.Empty).Trim();
        var label = type.ToUpperInvariant() switch
        {
            "MR" => "Misc Receipt",
            "MI" => "Misc Issue",
            "SC" => "Scrap",
            "VR" => "Vendor Return",
            "TR" => "Stock Transfer",
            "ADJ" => "Stock Adjustment",
            "CR" => "Customer Return",
            "GR" or "NG" => "Goods Receipt",
            "IP" => "Material Issue",
            "FG" => "Finished Good Receipt",
            "FG_RECEIPT" => "Finished Good Receipt",
            "PRODUCTION_OUTPUT" or "DAILY_PRODUCTION" => "Daily Production",
            "MATERIAL_ISSUE" => "Material Issue",
            _ when type.Length > 0 => type,
            _ => "document"
        };
        return number.Length > 0 ? $"{label} {number}" : label;
    }

    private static async Task<Dictionary<(int BatchNo, short LineNo), DetailEvidence>> LoadDetailEvidenceAsync(
        StockPostingContext context,
        IReadOnlyCollection<IvTrxHistory> histories,
        CancellationToken cancellationToken)
    {
        var batchNos = histories.Select(x => x.BatchNo).Distinct().ToArray();
        if (batchNos.Length == 0)
            return [];

        var rows = await context.Db.IvTrxBatchDetails.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && batchNos.Contains(x.BatchNo)
                        && x.DocumentRevision <= context.Posting.DocumentRevision)
            .Select(x => new DetailEvidence(
                x.BatchNo, x.TrxLineNo, x.DocumentRevision,
                x.Currency, x.UnitPrice, x.BaseUnitPrices, x.CostPrice, x.Cost,
                x.PriceEvidence, x.CostEvidenceType, x.CostOverrideReason,
                x.InvNo, x.DoNo, x.SoLineNo))
            .ToListAsync(cancellationToken);

        return rows.GroupBy(x => (x.BatchNo, x.LineNo))
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.Revision).First());
    }

    private static async Task AppendReceiptWithResolvedCostAsync(
        StockPostingContext context,
        IvTrxHistory history,
        DetailEvidence? detail,
        IDictionary<string, StockCostState> states,
        ICollection<StockValuationFact> result,
        string? baseCurrency,
        CancellationToken cancellationToken)
    {
        var quantity = Positive(history.ToStdQty, "receipt quantity");
        decimal? forcedAmount = null;
        decimal? forcedUnit = null;
        long? originalFactId = null;
        var source = ResolveReceiptSource(history);

        if (string.Equals(history.TrxType, IvTrxTypes.CustomerReturn, StringComparison.OrdinalIgnoreCase))
        {
            var original = await ResolveOriginalSaleCostAsync(
                context, history, detail, quantity, cancellationToken);
            forcedUnit = original.UnitCost;
            forcedAmount = original.TotalCost;
            source = StockValuationSources.OriginalSaleReturn;

            var fact = AppendReceipt(
                context, history, detail, states, 0, forcedAmount, forcedUnit, source,
                originalFactId: null, result, baseCurrency);
            foreach (var allocation in original.Allocations)
            {
                context.Db.SalesReturnCostAllocations.Add(new SalesReturnCostAllocation
                {
                    CompanyCode = context.CompanyCode,
                    BranchCode = context.BranchCode,
                    ReturnDocumentType = "SA_CDN",
                    ReturnDocumentNo = ResolveReturnDocumentNo(history, detail, context),
                    ReturnDocumentLine = history.TrxLineNo,
                    ReturnCostingRevision = context.Posting.DocumentRevision,
                    OriginalValuationFactId = allocation.OriginalFactId,
                    OriginalOwnerType = allocation.OwnerType,
                    OriginalOwnerDocumentNo = allocation.OwnerDocumentNo,
                    OriginalOwnerDocumentLine = allocation.OwnerDocumentLine,
                    ReturnedBaseQty = allocation.Quantity,
                    ReturnedCostAmount = allocation.CostAmount,
                    StockPostingId = context.Posting.Id,
                    ReturnValuationFact = fact,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = context.UserId
                });
            }

            return;
        }

        if (string.Equals(history.TrxType, IvTrxTypes.MiscellaneousReceipt, StringComparison.OrdinalIgnoreCase)
            && ResolveCostEvidenceType(history, detail) is null)
        {
            throw LedgerError(StockLedgerErrorCodes.ValuationRequired,
                $"Miscellaneous receipt for item '{history.ICode}' has no approved cost evidence.");
        }

        if (history.ExactTransferredValue is decimal exact)
        {
            forcedAmount = RoundMoney(Math.Abs(exact));
            forcedUnit = RoundMoney(forcedAmount.Value / quantity);
            source = StockValuationSources.ProductionActual;
        }

        AppendReceipt(
            context, history, detail, states, 0, forcedAmount, forcedUnit, source,
            originalFactId, result, baseCurrency);
    }

    private static StockValuationFact AppendReceipt(
        StockPostingContext context,
        IvTrxHistory history,
        DetailEvidence? detail,
        IDictionary<string, StockCostState> states,
        int splitOrdinal,
        decimal? forcedAmount,
        decimal? forcedUnitCost,
        string forcedSource,
        long? originalFactId,
        ICollection<StockValuationFact> result,
        string? baseCurrency)
    {
        var quantity = Positive(history.ToStdQty, "receipt quantity");
        var itemCode = Required(history.ICode, "item code");
        var state = GetOrCreateState(context, states, itemCode);

        var unitCost = forcedUnitCost;
        var source = forcedSource;
        if (unitCost is null
            && string.Equals(history.TrxType, IvTrxTypes.StockAdjustment, StringComparison.OrdinalIgnoreCase)
            && ResolveCostEvidenceType(history, detail) is null)
        {
            if (state.OnHandBaseQty <= 0m || state.InventoryValue < 0m)
                throw LedgerError(StockLedgerErrorCodes.ValuationRequired,
                    MissingAdjustmentCostMessage(itemCode));

            unitCost = state.CurrentUnitCost;
            source = StockValuationSources.MovingAverage;
        }

        unitCost ??= ResolveReceiptUnitCost(history, detail);
        var resolvedUnitCost = unitCost.Value;
        var evidenceType = ResolveCostEvidenceType(history, detail);
        if (evidenceType is not null)
        {
            // Standard inventory value stays on the effective standard cost. Evidence is audited
            // against the approved unit on the document, not reinterpreted as that standard cost.
            var evidenceUnit = string.Equals(forcedSource, StockValuationSources.Standard, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(history.TrxType, IvTrxTypes.StockAdjustment, StringComparison.OrdinalIgnoreCase)
                ? detail?.UnitPrice ?? history.UnitPrice ?? resolvedUnitCost
                : resolvedUnitCost;
            ValidateApprovedCostEvidence(history, detail, evidenceType, evidenceUnit);
        }

        var amount = forcedAmount ?? RoundMoney(quantity * resolvedUnitCost);
        if (amount < 0m || resolvedUnitCost < 0m)
            throw LedgerError(StockLedgerErrorCodes.ValuationRequired,
                $"Receipt valuation cannot be negative for item '{itemCode}'.");

        var transactionUnit = detail?.CostPrice
                              ?? history.CostPrice
                              ?? detail?.UnitPrice
                              ?? history.UnitPrice;
        var transactionQty = history.ToPurQty is > 0m
            ? history.ToPurQty.Value
            : quantity;
        decimal? transactionAmount = transactionUnit is null
            ? null
            : RoundMoney(transactionQty * transactionUnit.Value);
        decimal? exchangeRate = transactionAmount is > 0m
            ? RoundRate(amount / transactionAmount.Value)
            : null;

        var fact = NewFact(
            context, history, splitOrdinal, direction: 1, quantity, resolvedUnitCost, amount,
            MovementCode(history, direction: 1), source, baseCurrency,
            detail?.Currency, transactionAmount, exchangeRate, originalFactId, null);
        ApplyInbound(state, quantity, amount, fact, context.Posting.PostingSequence);
        result.Add(fact);
        return fact;
    }

    private static async Task<StockValuationFact> AppendIssueAsync(
        StockPostingContext context,
        IvTrxHistory history,
        DetailEvidence? detail,
        IDictionary<string, StockCostState> states,
        int splitOrdinal,
        ICollection<StockValuationFact> result,
        string? baseCurrency,
        CancellationToken cancellationToken,
        decimal? fixedUnitCost = null,
        string? fixedValuationSource = null)
    {
        var quantity = Positive(history.FrStdQty, "issue quantity");
        var itemCode = Required(history.ICode, "item code");
        if (!states.TryGetValue(itemCode, out var state))
            throw LedgerError(StockLedgerErrorCodes.ValuationRequired,
                $"Item '{itemCode}' has no approved {context.CostMethod} opening or receipt value.");
        if (quantity > state.OnHandBaseQty)
            throw LedgerError(StockLedgerErrorCodes.InsufficientBaseQty,
                $"Valuation quantity for item '{itemCode}' is {state.OnHandBaseQty}, but {quantity} is required.");

        var finalDepletion = quantity == state.OnHandBaseQty;
        var amount = fixedUnitCost is decimal standardUnit
            ? RoundMoney(quantity * standardUnit)
            : finalDepletion
                ? state.InventoryValue
                : RoundMoney(quantity * state.AverageUnitCost);
        var unitCost = quantity == 0m ? 0m : RoundMoney(amount / quantity);

        var fact = NewFact(
            context, history, splitOrdinal, direction: -1, quantity, unitCost, amount,
            MovementCode(history, direction: -1), fixedValuationSource ?? StockValuationSources.MovingAverage,
            baseCurrency, null, null, null, null, null);
        ApplyOutbound(state, quantity, amount, fact, context.Posting.PostingSequence);
        result.Add(fact);
        await Task.CompletedTask;
        return fact;
    }

    private static async Task AppendExactReversalAsync(
        StockPostingContext context,
        IvTrxHistory reversalHistory,
        IDictionary<string, StockCostState> states,
        ICollection<StockValuationFact> result,
        string? baseCurrency,
        CancellationToken cancellationToken)
    {
        if (reversalHistory.ReversesHistoryId is not int historyId)
            throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                "A V2 history reversal must identify the original history row.");

        var originals = await context.Db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.InventoryHistoryId == historyId)
            .OrderBy(x => x.SplitOrdinal)
            .ToListAsync(cancellationToken);
        if (originals.Count == 0)
            throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                $"History {historyId} has no authoritative valuation to reverse.");

        var originalIds = originals.Select(x => x.Id).ToArray();
        var alreadyReversed = await context.Db.StockValuationFacts.AsNoTracking()
            .AnyAsync(x => x.ReversesValuationFactId != null
                           && originalIds.Contains(x.ReversesValuationFactId.Value), cancellationToken);
        if (alreadyReversed)
            throw LedgerError(StockLedgerErrorCodes.ReversalAlreadyExists,
                $"History {historyId} already has a valuation reversal.");

        foreach (var original in originals)
        {
            var itemCode = original.ItemCode;
            if (!states.TryGetValue(itemCode, out var state))
            {
                if (original.Direction < 0)
                    state = GetOrCreateState(context, states, itemCode);
                else
                    throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                        $"The current cost state for item '{itemCode}' cannot support exact receipt reversal.");
            }

            var direction = -original.Direction;
            var fact = NewFact(
                context, reversalHistory, original.SplitOrdinal, direction,
                original.BaseQty, original.UnitCost, original.CostAmount,
                original.MovementCode + "_REVERSAL", StockValuationSources.OriginalReversal,
                baseCurrency ?? original.BaseCurrency,
                original.TransactionCurrency, original.TransactionCostAmount,
                original.ExchangeRate,
                original.OriginalValuationFactId ?? original.Id,
                original.Id);

            if (direction > 0)
                ApplyInbound(state, fact.BaseQty, fact.CostAmount, fact, context.Posting.PostingSequence);
            else
                ApplyOutbound(state, fact.BaseQty, fact.CostAmount, fact, context.Posting.PostingSequence);
            result.Add(fact);

            // A customer-return valuation may be split across several original outbound
            // facts. Preserve that exact evidence through rollback as immutable allocation
            // reversal rows; otherwise a later audit could see the stock reversal without
            // knowing which original sale slices it cancelled.
            var returnAllocations = await context.Db.SalesReturnCostAllocations.AsNoTracking()
                .Where(x => x.ReturnValuationFactId == original.Id
                            && x.CompanyCode == context.CompanyCode
                            && x.BranchCode == context.BranchCode)
                .ToListAsync(cancellationToken);
            foreach (var allocation in returnAllocations)
            {
                context.Db.SalesReturnCostAllocations.Add(new SalesReturnCostAllocation
                {
                    CompanyCode = context.CompanyCode,
                    BranchCode = context.BranchCode,
                    ReturnDocumentType = allocation.ReturnDocumentType,
                    ReturnDocumentNo = allocation.ReturnDocumentNo,
                    ReturnDocumentLine = allocation.ReturnDocumentLine,
                    ReturnCostingRevision = context.Posting.DocumentRevision,
                    OriginalValuationFactId = allocation.OriginalValuationFactId,
                    OriginalOwnerType = allocation.OriginalOwnerType,
                    OriginalOwnerDocumentNo = allocation.OriginalOwnerDocumentNo,
                    OriginalOwnerDocumentLine = allocation.OriginalOwnerDocumentLine,
                    ReturnedBaseQty = allocation.ReturnedBaseQty,
                    ReturnedCostAmount = allocation.ReturnedCostAmount,
                    StockPostingId = context.Posting.Id,
                    ReturnValuationFact = fact,
                    ReversesAllocationId = allocation.Id,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = context.UserId
                });
            }
        }
    }

    private static StockCostState GetOrCreateState(
        StockPostingContext context,
        IDictionary<string, StockCostState> states,
        string itemCode)
    {
        if (states.TryGetValue(itemCode, out var state))
            return state;
        state = new StockCostState
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            ItemCode = itemCode,
            CostMethod = context.CostMethod
        };
        context.Db.StockCostStates.Add(state);
        states[itemCode] = state;
        return state;
    }

    private static void ApplyInbound(
        StockCostState state,
        decimal quantity,
        decimal amount,
        StockValuationFact fact,
        long postingSequence)
    {
        state.OnHandBaseQty = RoundQuantity(state.OnHandBaseQty + quantity);
        state.InventoryValue = RoundMoney(state.InventoryValue + amount);
        state.AverageUnitCost = state.OnHandBaseQty == 0m
            ? 0m
            : RoundMoney(state.InventoryValue / state.OnHandBaseQty);
        state.CurrentUnitCost = state.AverageUnitCost;
        state.LastValuationFact = fact;
        state.LastPostingSequence = postingSequence;
    }

    private static void ApplyOutbound(
        StockCostState state,
        decimal quantity,
        decimal amount,
        StockValuationFact fact,
        long postingSequence)
    {
        if (quantity > state.OnHandBaseQty)
            throw LedgerError(StockLedgerErrorCodes.InsufficientBaseQty,
                $"Valuation quantity for item '{state.ItemCode}' is {state.OnHandBaseQty}, but {quantity} is required.");
        if (amount > state.InventoryValue && quantity != state.OnHandBaseQty)
            throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                $"Exact reversal value for item '{state.ItemCode}' exceeds its current inventory value; rollback later dependent movements first.");

        state.OnHandBaseQty = RoundQuantity(state.OnHandBaseQty - quantity);
        state.InventoryValue = quantity == state.OnHandBaseQty + quantity
            ? 0m
            : RoundMoney(state.InventoryValue - amount);
        if (state.InventoryValue < 0m)
            throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                $"Exact valuation would make inventory value negative for item '{state.ItemCode}'.");
        state.AverageUnitCost = state.OnHandBaseQty == 0m
            ? 0m
            : RoundMoney(state.InventoryValue / state.OnHandBaseQty);
        state.CurrentUnitCost = state.AverageUnitCost;
        state.LastValuationFact = fact;
        state.LastPostingSequence = postingSequence;
    }

    private static StockValuationFact NewFact(
        StockPostingContext context,
        IvTrxHistory history,
        int splitOrdinal,
        int direction,
        decimal quantity,
        decimal unitCost,
        decimal amount,
        string movementCode,
        string valuationSource,
        string? baseCurrency,
        string? transactionCurrency,
        decimal? transactionAmount,
        decimal? exchangeRate,
        long? originalFactId,
        long? reversesFactId)
    {
        var (documentType, documentId, documentNo) = ResolveSourceIdentity(context, history);
        var warehouse = direction < 0 ? history.FrWarehouse : history.ToWarehouse;
        var location = direction < 0 ? history.FrLocation : history.ToLocation;
        var lotNo = direction < 0 ? history.FrLotNo : history.ToLotNo;
        var lotId = direction < 0 ? history.FromLotId : history.ToLotId;
        var uom = direction < 0 ? history.FrStdUom : history.ToStdUom;
        var line = history.PostingLineNo
                   ?? throw LedgerError(StockLedgerErrorCodes.LedgerMismatch,
                       "V2 history requires a posting line before valuation.");

        return new StockValuationFact
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            LedgerEpochId = context.Epoch.Id,
            StockPostingId = context.Posting.Id,
            PostingLineNo = line,
            SplitOrdinal = splitOrdinal,
            SourceLineId = $"{documentType}:{documentId}:{ResolveSourceDocumentLine(history)}:{line}:{splitOrdinal}",
            SourceDocumentType = documentType,
            SourceDocumentId = documentId,
            SourceDocumentNo = documentNo,
            SourceDocumentLine = ResolveSourceDocumentLine(history),
            EffectiveAt = context.Posting.EffectiveAt,
            BusinessDate = context.Posting.BusinessDate,
            PeriodKey = context.Posting.PeriodKey,
            ItemCode = Required(history.ICode, "item code"),
            WarehouseCode = NullIfBlank(warehouse),
            LocationCode = NullIfBlank(location),
            LotId = lotId,
            LotNo = NullIfBlank(lotNo),
            ItemStatus = NullIfBlank(history.IStatus),
            BaseUom = Required(uom, "base UOM"),
            MovementCode = movementCode,
            Direction = direction,
            BaseQty = RoundQuantity(quantity),
            CostMethod = context.CostMethod,
            UnitCost = RoundMoney(unitCost),
            CostAmount = RoundMoney(amount),
            TransactionCurrency = NullIfBlank(transactionCurrency),
            TransactionCostAmount = transactionAmount,
            ExchangeRate = exchangeRate,
            BaseCurrency = NullIfBlank(baseCurrency),
            BaseCostAmount = RoundMoney(amount),
            ValuationSource = valuationSource,
            ValuationStatus = reversesFactId is null
                ? StockValuationStatuses.Valued
                : StockValuationStatuses.Reversed,
            ValuationVersion = 1,
            InventoryHistoryId = history.Id > 0 ? history.Id : null,
            InventoryHistory = history,
            FromBalLocId = history.FromBalLocId,
            ToBalLocId = history.ToBalLocId,
            ProductionPostingLinkId = context.Posting.ProductionPostingLinkId,
            OriginalValuationFactId = originalFactId,
            ReversesValuationFactId = reversesFactId,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = context.UserId
        };
    }

    private static async Task<SaleReturnCostResolution> ResolveOriginalSaleCostAsync(
        StockPostingContext context,
        IvTrxHistory history,
        DetailEvidence? detail,
        decimal quantity,
        CancellationToken cancellationToken)
    {
        var invoiceNo = NullIfBlank(detail?.InvNo) ?? NullIfBlank(history.InvNo);
        var invoiceLine = detail?.SoLineNo ?? history.SoLineNo;
        if (invoiceNo is null || invoiceLine is null)
        {
            throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                "Customer return is missing the exact referenced invoice line.");
        }

        var invoice = await context.Db.SaInvoiceDetails.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.InvNo == invoiceNo
                        && x.Line == invoiceLine.Value)
            .Select(x => new
            {
                x.Line,
                x.ICode,
                x.LinkDo,
                x.DoNo,
                x.DoLine
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (invoice is null)
        {
            throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                $"Customer return references invoice {invoiceNo} line {invoiceLine}, but that exact line was not found.");
        }

        var itemCode = Required(history.ICode, "item code");
        if (!string.Equals(invoice.ICode, itemCode, StringComparison.OrdinalIgnoreCase))
        {
            throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                $"Customer return item '{itemCode}' does not match invoice {invoiceNo} line {invoiceLine}.");
        }

        var ownerType = invoice.LinkDo ? "SA_DO" : "SA_INVOICE";
        var ownerNo = invoice.LinkDo ? NullIfBlank(invoice.DoNo) : invoiceNo;
        var ownerLine = invoice.LinkDo
            ? invoice.DoLine?.ToString(CultureInfo.InvariantCulture)
            : invoice.Line.ToString(CultureInfo.InvariantCulture);
        if (ownerNo is null || ownerLine is null)
        {
            throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                $"Customer return cannot resolve the exact {ownerType} owner for invoice {invoiceNo} line {invoiceLine}.");
        }

        var rows = await context.Db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.ItemCode == itemCode
                        && x.Direction == -1
                        && x.SourceDocumentType == ownerType
                        && x.SourceDocumentNo == ownerNo
                        && x.SourceDocumentLine == ownerLine
                        && x.ValuationStatus == StockValuationStatuses.Valued
                        && x.StockPosting!.SealedAtUtc != null
                        && !context.Db.StockValuationFacts.Any(r => r.ReversesValuationFactId == x.Id))
            .OrderBy(x => x.PostingLineNo)
            .ThenBy(x => x.SplitOrdinal)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                $"No active outbound valuation facts exist for {ownerType} {ownerNo} line {ownerLine}.");
        }

        var originalIds = rows.Select(x => x.Id).ToArray();
        var priorAllocations = await context.Db.SalesReturnCostAllocations.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && originalIds.Contains(x.OriginalValuationFactId))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var duplicateParents = priorAllocations
            .Where(x => x.ReversesAllocationId is not null)
            .GroupBy(x => x.ReversesAllocationId!.Value)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicateParents is not null)
        {
            throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                $"Sales-return allocation {duplicateParents.Key} has more than one reversal child.");
        }

        var children = priorAllocations
            .Where(x => x.ReversesAllocationId is not null)
            .ToDictionary(x => x.ReversesAllocationId!.Value);
        var candidates = new List<ReturnCostCandidate>(rows.Count);
        foreach (var row in rows)
        {
            var chains = priorAllocations
                .Where(x => x.OriginalValuationFactId == row.Id && x.ReversesAllocationId is null)
                .OrderBy(x => x.Id)
                .Select(x => BuildAllocationChain(x, children))
                .ToArray();
            var returnedQty = chains.Sum(SignedAllocationQuantity);
            var returnedValue = chains.Sum(SignedAllocationValue);
            var remainingQty = RoundQuantity(row.BaseQty - returnedQty);
            var remainingValue = RoundMoney(row.CostAmount - returnedValue);
            if (remainingQty < -0.0000005m || remainingValue < -0.0000005m)
            {
                throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                    $"Sales-return allocations exceed outbound valuation fact {row.Id}.");
            }
            if (remainingQty > 0m)
            {
                candidates.Add(new ReturnCostCandidate(
                    row.Id, row.BaseQty, row.CostAmount, remainingQty, Math.Max(remainingValue, 0m),
                    ownerType, ownerNo, ownerLine));
            }
        }

        var remaining = RoundQuantity(quantity);
        if (remaining <= 0m)
        {
            throw LedgerError(StockLedgerErrorCodes.InvalidStockIdentity,
                "Customer return quantity must be positive for valuation.");
        }

        var allocations = new List<ReturnCostAllocationSlice>();
        foreach (var candidate in candidates)
        {
            if (remaining <= 0m)
                break;
            var slice = RoundQuantity(Math.Min(remaining, candidate.RemainingQty));
            if (slice <= 0m)
                continue;
            var amount = slice >= candidate.RemainingQty - 0.0000005m
                ? candidate.RemainingValue
                : RoundMoney(candidate.RemainingValue * slice / candidate.RemainingQty);
            allocations.Add(new ReturnCostAllocationSlice(
                candidate.OriginalFactId,
                candidate.OwnerType,
                candidate.OwnerDocumentNo,
                candidate.OwnerDocumentLine,
                slice,
                amount));
            remaining = RoundQuantity(remaining - slice);
        }
        if (remaining > 0m)
        {
            throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                $"Customer return quantity {quantity:0.######} exceeds the active returnable quantity for {ownerType} {ownerNo} line {ownerLine}.");
        }

        var totalCost = RoundMoney(allocations.Sum(x => x.CostAmount));
        return new SaleReturnCostResolution(
            totalCost,
            quantity == 0m ? 0m : RoundMoney(totalCost / quantity),
            allocations);
    }

    private static string ResolveReturnDocumentNo(
        IvTrxHistory history,
        DetailEvidence? detail,
        StockPostingContext context)
    {
        var reference = NullIfBlank(history.RefNo);
        if (reference is not null && reference.StartsWith("CN/", StringComparison.OrdinalIgnoreCase))
            return reference[3..];
        return reference ?? context.Posting.SourceDocumentNo;
    }

    private static IReadOnlyList<SalesReturnCostAllocation> BuildAllocationChain(
        SalesReturnCostAllocation root,
        IReadOnlyDictionary<long, SalesReturnCostAllocation> children)
    {
        var chain = new List<SalesReturnCostAllocation> { root };
        var seen = new HashSet<long> { root.Id };
        var current = root;
        while (children.TryGetValue(current.Id, out var child))
        {
            if (!seen.Add(child.Id))
                throw LedgerError(StockLedgerErrorCodes.ReversalDependency,
                    $"Sales-return allocation chain beginning at {root.Id} contains a cycle.");
            chain.Add(child);
            current = child;
        }
        return chain;
    }

    private static decimal SignedAllocationQuantity(
        IReadOnlyList<SalesReturnCostAllocation> chain)
    {
        decimal value = 0m;
        for (var index = 0; index < chain.Count; index++)
            value += (index % 2 == 0 ? 1m : -1m) * chain[index].ReturnedBaseQty;
        return RoundQuantity(value);
    }

    private static decimal SignedAllocationValue(
        IReadOnlyList<SalesReturnCostAllocation> chain)
    {
        decimal value = 0m;
        for (var index = 0; index < chain.Count; index++)
            value += (index % 2 == 0 ? 1m : -1m) * chain[index].ReturnedCostAmount;
        return RoundMoney(value);
    }

    private static async Task SynchronizeProductionMaterialCostAsync(
        AppDbContext db,
        IReadOnlyCollection<StockValuationFact> facts,
        CancellationToken cancellationToken)
    {
        var issueFacts = facts
            .Where(x => x.Direction < 0
                        && x.InventoryHistoryId is > 0
                        && string.Equals(x.MovementCode, "PRODUCTION_MATERIAL_OUT", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => x.InventoryHistoryId!.Value);
        if (issueFacts.Count == 0)
            return;

        var historyIds = issueFacts.Keys.ToArray();
        var movements = await db.ProductionMaterialMovements
            .Where(x => x.InventoryHistoryId != null && historyIds.Contains(x.InventoryHistoryId.Value))
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        foreach (var group in movements.GroupBy(x => x.InventoryHistoryId!.Value))
        {
            var fact = issueFacts[group.Key];
            var rows = group.ToArray();
            var totalBaseQty = rows.Sum(x => x.BaseQty);
            if (totalBaseQty <= 0m)
                throw LedgerError(StockLedgerErrorCodes.LedgerMismatch,
                    $"Production movements for inventory history {group.Key} have no positive base quantity.");

            var allocated = 0m;
            for (var i = 0; i < rows.Length; i++)
            {
                var movement = rows[i];
                var amount = i == rows.Length - 1
                    ? fact.CostAmount - allocated
                    : RoundMoney(fact.CostAmount * movement.BaseQty / totalBaseQty);
                allocated += amount;
                movement.UnitCost = RoundMoney(amount / movement.BaseQty);
                movement.TotalCost = amount;
                movement.StockPostingId = fact.StockPostingId;
                movement.SourceLineId = fact.SourceLineId;
                movement.SplitOrdinal = fact.SplitOrdinal;
                fact.ProductionMovementId ??= movement.Uid;
                fact.WorkOrderId ??= movement.WorkOrderId;
                fact.WorkOrderOperationId ??= movement.WorkOrderOperationId;
            }
        }
    }

    private static decimal ResolveFifoReceiptUnitCost(
        IvTrxHistory history,
        DetailEvidence? detail,
        IDictionary<string, StockCostState> states)
    {
        if (string.Equals(history.TrxType, IvTrxTypes.StockAdjustment, StringComparison.OrdinalIgnoreCase)
            && ResolveCostEvidenceType(history, detail) is null)
        {
            var itemCode = Required(history.ICode, "item code");
            if (!states.TryGetValue(itemCode, out var state)
                || state.OnHandBaseQty <= 0m
                || state.InventoryValue < 0m)
            {
                throw LedgerError(StockLedgerErrorCodes.ValuationRequired,
                    MissingAdjustmentCostMessage(itemCode));
            }

            return state.CurrentUnitCost;
        }

        return ResolveReceiptUnitCost(history, detail);
    }

    private static string MissingAdjustmentCostMessage(string itemCode) =>
        $"The system has no approved current inventory cost for item {itemCode}. Use an authorized cost override with supporting evidence before posting this positive adjustment.";

    private static decimal ResolveReceiptUnitCost(IvTrxHistory history, DetailEvidence? detail)
    {
        var value = detail?.BaseUnitPrice
                    ?? history.BaseUnitPrices
                    ?? detail?.UnitPrice
                    ?? history.UnitPrice
                    ?? detail?.CostPrice
                    ?? history.CostPrice
                    ?? detail?.Cost
                    ?? history.Cost;
        if (value is null)
            throw LedgerError(StockLedgerErrorCodes.ValuationRequired,
                $"Receipt for item '{history.ICode}' has no approved base-currency cost evidence.");
        return RoundMoney(value.Value);
    }

    private static string? ResolveCostEvidenceType(IvTrxHistory history, DetailEvidence? detail)
    {
        var explicitType = InventoryCostEvidenceTypes.Normalize(
            detail?.CostEvidenceType ?? history.CostEvidenceType);
        if (explicitType is not null)
            return explicitType;

        // Preserve legacy approved rows created before the structured columns existed.
        var legacy = detail?.PriceEvidence?.Trim().ToUpperInvariant()
                     ?? history.PriceEvidence?.Trim().ToUpperInvariant();
        return legacy switch
        {
            "EXPLICIT_COMPANY_BASE_PRICE" => InventoryCostEvidenceTypes.ManualApproved,
            InventoryCostEvidenceTypes.ManualApproved => InventoryCostEvidenceTypes.ManualApproved,
            InventoryCostEvidenceTypes.ZeroCostApproved => InventoryCostEvidenceTypes.ZeroCostApproved,
            InventoryCostEvidenceTypes.OpeningApproved => InventoryCostEvidenceTypes.OpeningApproved,
            _ => null
        };
    }

    private static void ValidateApprovedCostEvidence(
        IvTrxHistory history,
        DetailEvidence? detail,
        string evidenceType,
        decimal unitCost)
    {
        if (evidenceType == InventoryCostEvidenceTypes.ManualApproved && unitCost <= 0m)
        {
            throw LedgerError(StockLedgerErrorCodes.ValuationRequired,
                $"MANUAL_APPROVED cost evidence for item '{history.ICode}' must be positive.");
        }

        if (evidenceType == InventoryCostEvidenceTypes.ZeroCostApproved && unitCost != 0m)
        {
            throw LedgerError(StockLedgerErrorCodes.ValuationRequired,
                $"ZERO_COST_APPROVED cost evidence for item '{history.ICode}' must resolve to zero.");
        }

        if (evidenceType is InventoryCostEvidenceTypes.ZeroCostApproved or InventoryCostEvidenceTypes.OpeningApproved
            && string.IsNullOrWhiteSpace(detail?.CostOverrideReason ?? history.CostOverrideReason))
        {
            throw LedgerError(StockLedgerErrorCodes.ValuationRequired,
                $"{evidenceType} cost evidence for item '{history.ICode}' requires an approval reason.");
        }
    }

    private static string ResolveReceiptSource(IvTrxHistory history) => history.TrxType switch
    {
        IvTrxTypes.GoodsReceive => StockValuationSources.ReceiptActual,
        IvTrxTypes.FinishedGoods => StockValuationSources.ProductionActual,
        _ => StockValuationSources.ManualApproved
    };

    private static (string Type, string Id, string No) ResolveSourceIdentity(
        StockPostingContext context,
        IvTrxHistory history)
    {
        if (string.Equals(history.TrxType, IvTrxTypes.SalesOut, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(history.DoNo))
                return ("SA_DO", history.DoNo.Trim(), history.DoNo.Trim());
            if (!string.IsNullOrWhiteSpace(history.InvNo))
                return ("SA_INVOICE", history.InvNo.Trim(), history.InvNo.Trim());
        }
        return (context.Posting.SourceDocumentType, context.Posting.SourceDocumentId,
            string.IsNullOrWhiteSpace(context.Posting.SourceDocumentNo)
                ? context.Posting.SourceDocumentId
                : context.Posting.SourceDocumentNo);
    }

    private static string ResolveSourceDocumentLine(IvTrxHistory history) =>
        string.Equals(history.TrxType, IvTrxTypes.SalesOut, StringComparison.OrdinalIgnoreCase)
        && history.SoLineNo is short sourceLine
            ? sourceLine.ToString(CultureInfo.InvariantCulture)
            : history.TrxLineNo.ToString(CultureInfo.InvariantCulture);

    private static string MovementCode(IvTrxHistory history, int direction) => history.TrxType switch
    {
        IvTrxTypes.StockTransfer when direction < 0 => "TRANSFER_OUT",
        IvTrxTypes.StockTransfer => "TRANSFER_IN",
        IvTrxTypes.StockAdjustment when direction < 0 => "ADJUST_OUT",
        IvTrxTypes.StockAdjustment => "ADJUST_IN",
        IvTrxTypes.SalesOut => "SALE_OUT",
        IvTrxTypes.IssueToProduction => "PRODUCTION_MATERIAL_OUT",
        IvTrxTypes.FinishedGoods => "FINISHED_GOOD_IN",
        IvTrxTypes.CustomerReturn => "SALE_RETURN_IN",
        IvTrxTypes.VendorReturn => "VENDOR_RETURN_OUT",
        IvTrxTypes.Scrap => "SCRAP_OUT",
        _ when direction < 0 => "ISSUE_OUT",
        _ => "RECEIPT_IN"
    };

    private static decimal Positive(decimal? value, string label)
    {
        if (value is null or <= 0m)
            throw LedgerError(StockLedgerErrorCodes.InvalidStockIdentity, $"A positive {label} is required.");
        return RoundQuantity(value.Value);
    }

    private static string Required(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw LedgerError(StockLedgerErrorCodes.InvalidStockIdentity, $"Inventory {label} is required for valuation.");
        return value.Trim();
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal RoundMoney(decimal value) =>
        decimal.Round(value, MoneyScale, MidpointRounding.AwayFromZero);

    private static decimal RoundRate(decimal value) =>
        decimal.Round(value, 8, MidpointRounding.AwayFromZero);

    private static decimal RoundQuantity(decimal value) =>
        decimal.Round(value, MoneyScale, MidpointRounding.AwayFromZero);

    private static StockLedgerException LedgerError(string code, string message) =>
        new(new StockLedgerError(code, message));

    private sealed record DetailEvidence(
        int BatchNo,
        short LineNo,
        int Revision,
        string? Currency,
        decimal? UnitPrice,
        decimal? BaseUnitPrice,
        decimal? CostPrice,
        decimal? Cost,
        string? PriceEvidence,
        string? CostEvidenceType,
        string? CostOverrideReason,
        string? InvNo,
        string? DoNo,
        short? SoLineNo);

    private sealed record SaleReturnCostResolution(
        decimal TotalCost,
        decimal UnitCost,
        IReadOnlyList<ReturnCostAllocationSlice> Allocations);

    private sealed record ReturnCostAllocationSlice(
        long OriginalFactId,
        string OwnerType,
        string OwnerDocumentNo,
        string OwnerDocumentLine,
        decimal Quantity,
        decimal CostAmount);

    private sealed record ReturnCostCandidate(
        long OriginalFactId,
        decimal OriginalQty,
        decimal OriginalValue,
        decimal RemainingQty,
        decimal RemainingValue,
        string OwnerType,
        string OwnerDocumentNo,
        string OwnerDocumentLine);
}
