using System.Globalization;
using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
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

    public async Task<IReadOnlyList<StockValuationFact>> ValuePendingAsync(
        StockPostingContext context,
        CancellationToken cancellationToken = default)
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
                    && x.CostMethod == StockCostMethods.MovingAverage
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

    private static bool IsFinanciallyRelevant(IvTrxHistory history) =>
        (history.FromBalLocId is not null && (history.FrStdQty ?? 0m) > 0m)
        || (history.ToBalLocId is not null && (history.ToStdQty ?? 0m) > 0m);

    private static async Task RejectBackdatedPoolsAsync(
        StockPostingContext context,
        IReadOnlyCollection<string> itemCodes,
        CancellationToken cancellationToken)
    {
        var later = await context.Db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && itemCodes.Contains(x.ItemCode)
                        && x.EffectiveAt > context.Posting.EffectiveAt)
            .Select(x => new { x.ItemCode, x.EffectiveAt })
            .OrderBy(x => x.EffectiveAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (later is not null)
            throw LedgerError(StockLedgerErrorCodes.BackdatedStockEvent,
                $"Item '{later.ItemCode}' already has a later valued movement at {later.EffectiveAt:O}; rollback and repost the later movement first.");
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
                x.PriceEvidence))
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
            var original = await ResolveOriginalSaleCostAsync(context, history, cancellationToken);
            if (original is not null)
            {
                forcedUnit = original.UnitCost;
                forcedAmount = RoundMoney(quantity * original.UnitCost);
                originalFactId = original.FactId;
                source = StockValuationSources.OriginalSaleReturn;
            }
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

    private static void AppendReceipt(
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

        var unitCost = forcedUnitCost ?? ResolveReceiptUnitCost(history, detail);
        var amount = forcedAmount ?? RoundMoney(quantity * unitCost);
        if (amount < 0m || unitCost < 0m)
            throw LedgerError(StockLedgerErrorCodes.ValuationRequired,
                $"Receipt valuation cannot be negative for item '{itemCode}'.");

        var transactionUnit = detail?.UnitPrice ?? history.UnitPrice;
        decimal? transactionAmount = transactionUnit is null
            ? null
            : RoundMoney(quantity * transactionUnit.Value);
        decimal? exchangeRate = transactionUnit is > 0m
            ? RoundRate(unitCost / transactionUnit.Value)
            : null;

        var fact = NewFact(
            context, history, splitOrdinal, direction: 1, quantity, unitCost, amount,
            MovementCode(history, direction: 1), forcedSource, baseCurrency,
            detail?.Currency, transactionAmount, exchangeRate, originalFactId, null);
        ApplyInbound(state, quantity, amount, fact, context.Posting.PostingSequence);
        result.Add(fact);
    }

    private static async Task<StockValuationFact> AppendIssueAsync(
        StockPostingContext context,
        IvTrxHistory history,
        DetailEvidence? detail,
        IDictionary<string, StockCostState> states,
        int splitOrdinal,
        ICollection<StockValuationFact> result,
        string? baseCurrency,
        CancellationToken cancellationToken)
    {
        var quantity = Positive(history.FrStdQty, "issue quantity");
        var itemCode = Required(history.ICode, "item code");
        if (!states.TryGetValue(itemCode, out var state))
            throw LedgerError(StockLedgerErrorCodes.ValuationRequired,
                $"Item '{itemCode}' has no approved moving-average opening or receipt value.");
        if (quantity > state.OnHandBaseQty)
            throw LedgerError(StockLedgerErrorCodes.InsufficientBaseQty,
                $"Valuation quantity for item '{itemCode}' is {state.OnHandBaseQty}, but {quantity} is required.");

        var finalDepletion = quantity == state.OnHandBaseQty;
        var amount = finalDepletion
            ? state.InventoryValue
            : RoundMoney(quantity * state.AverageUnitCost);
        var unitCost = quantity == 0m ? 0m : RoundMoney(amount / quantity);

        var fact = NewFact(
            context, history, splitOrdinal, direction: -1, quantity, unitCost, amount,
            MovementCode(history, direction: -1), StockValuationSources.MovingAverage,
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
            CostMethod = StockCostMethods.MovingAverage
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
            SourceLineId = $"{documentType}:{documentId}:{history.TrxLineNo}:{splitOrdinal}",
            SourceDocumentType = documentType,
            SourceDocumentId = documentId,
            SourceDocumentNo = documentNo,
            SourceDocumentLine = history.TrxLineNo.ToString(CultureInfo.InvariantCulture),
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
            CostMethod = StockCostMethods.MovingAverage,
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

    private static async Task<OriginalSaleCost?> ResolveOriginalSaleCostAsync(
        StockPostingContext context,
        IvTrxHistory history,
        CancellationToken cancellationToken)
    {
        var documentNo = NullIfBlank(history.InvNo) ?? NullIfBlank(history.DoNo);
        if (documentNo is null)
            return null;

        var rows = await context.Db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.ItemCode == history.ICode
                        && x.Direction == -1
                        && x.SourceDocumentNo == documentNo
                        && x.ValuationStatus == StockValuationStatuses.Valued)
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.BaseQty, x.CostAmount })
            .ToListAsync(cancellationToken);
        var qty = rows.Sum(x => x.BaseQty);
        if (qty <= 0m)
            return null;
        return new OriginalSaleCost(rows[0].Id, RoundMoney(rows.Sum(x => x.CostAmount) / qty));
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
        string? PriceEvidence);

    private sealed record OriginalSaleCost(long FactId, decimal UnitCost);
}
