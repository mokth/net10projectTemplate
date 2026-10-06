using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockValueAdjustmentRequest(
    int PostingLineNo,
    int SplitOrdinal,
    string ItemCode,
    string BaseUom,
    decimal Amount,
    int Direction,
    string MovementCode,
    string ValuationSource,
    string? WarehouseCode = null,
    string? LocationCode = null,
    string? SourceDocumentLine = null,
    long? OriginalValuationFactId = null,
    long? ReversesValuationFactId = null);

public interface IStockValueAdjustmentWriter
{
    Task<IReadOnlyList<StockValuationFact>> AppendAsync(
        StockPostingContext context,
        IReadOnlyCollection<StockValueAdjustmentRequest> adjustments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Appends zero-quantity financial value facts through the existing posting context. This is the
/// only writer used by procurement PPV, landed cost, Standard revaluation and cutover value legs.
/// </summary>
public sealed class StockValueAdjustmentWriter : IStockValueAdjustmentWriter
{
    public async Task<IReadOnlyList<StockValuationFact>> AppendAsync(
        StockPostingContext context,
        IReadOnlyCollection<StockValueAdjustmentRequest> adjustments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(adjustments);
        context.EnsureUnsealed();

        if (adjustments.Count == 0)
            return [];

        var itemCodes = adjustments.Select(x => Required(x.ItemCode, "item code"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var states = await context.Db.StockCostStates
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.CostMethod == context.CostMethod
                        && itemCodes.Contains(x.ItemCode))
            .ToListAsync(cancellationToken);
        var stateMap = states.ToDictionary(x => x.ItemCode, StringComparer.OrdinalIgnoreCase);
        var result = new List<StockValuationFact>(adjustments.Count);

        foreach (var request in adjustments.OrderBy(x => x.PostingLineNo).ThenBy(x => x.SplitOrdinal))
        {
            if (request.Direction is not (-1 or 1))
                throw Error(StockLedgerErrorCodes.InvalidStockIdentity, "A value adjustment direction must be -1 or 1.");
            if (request.PostingLineNo <= 0 || request.SplitOrdinal < 0)
                throw Error(StockLedgerErrorCodes.InvalidStockIdentity, "Value adjustment line identity is invalid.");
            if (request.Amount < 0m)
                throw Error(StockLedgerErrorCodes.ValuationRequired, "A value adjustment amount cannot be negative.");

            var itemCode = Required(request.ItemCode, "item code");
            var baseUom = Required(request.BaseUom, "base UOM");
            if (!stateMap.TryGetValue(itemCode, out var state))
            {
                state = new StockCostState
                {
                    CompanyCode = context.CompanyCode,
                    BranchCode = context.BranchCode,
                    ItemCode = itemCode,
                    CostMethod = context.CostMethod
                };
                context.Db.StockCostStates.Add(state);
                stateMap[itemCode] = state;
            }

            var amount = StockLedgerPrecision.Money(request.Amount);
            var nextValue = request.Direction > 0
                ? StockLedgerPrecision.Money(state.InventoryValue + amount)
                : StockLedgerPrecision.Money(state.InventoryValue - amount);
            if (nextValue < 0m)
                throw Error(StockLedgerErrorCodes.ReversalDependency,
                    $"Value adjustment for item '{itemCode}' exceeds its current inventory value.");

            var fact = new StockValuationFact
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                LedgerEpochId = context.Epoch.Id,
                StockPostingId = context.Posting.Id,
                PostingLineNo = request.PostingLineNo,
                SplitOrdinal = request.SplitOrdinal,
                SourceLineId = $"{context.Posting.SourceDocumentType}:{context.Posting.SourceDocumentId}:{request.PostingLineNo}:{request.SplitOrdinal}",
                SourceDocumentType = context.Posting.SourceDocumentType,
                SourceDocumentId = context.Posting.SourceDocumentId,
                SourceDocumentNo = context.Posting.SourceDocumentNo,
                SourceDocumentLine = request.SourceDocumentLine,
                EffectiveAt = context.Posting.EffectiveAt,
                BusinessDate = context.Posting.BusinessDate,
                PeriodKey = context.Posting.PeriodKey,
                ItemCode = itemCode,
                WarehouseCode = NullIfBlank(request.WarehouseCode),
                LocationCode = NullIfBlank(request.LocationCode),
                BaseUom = baseUom,
                MovementCode = Required(request.MovementCode, "movement code"),
                Direction = request.Direction,
                BaseQty = 0m,
                CostMethod = context.CostMethod,
                UnitCost = 0m,
                CostAmount = amount,
                BaseCurrency = null,
                BaseCostAmount = amount,
                ValuationSource = Required(request.ValuationSource, "valuation source"),
                ValuationStatus = request.ReversesValuationFactId is null
                    ? StockValuationStatuses.Valued
                    : StockValuationStatuses.Reversed,
                ValuationVersion = 1,
                OriginalValuationFactId = request.OriginalValuationFactId,
                ReversesValuationFactId = request.ReversesValuationFactId,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = context.UserId
            };

            state.InventoryValue = nextValue;
            state.CurrentUnitCost = state.OnHandBaseQty == 0m
                ? 0m
                : StockLedgerPrecision.Money(nextValue / state.OnHandBaseQty);
            state.AverageUnitCost = state.CurrentUnitCost;
            state.LastValuationFact = fact;
            state.LastPostingSequence = context.Posting.PostingSequence;
            context.Db.StockValuationFacts.Add(fact);
            result.Add(fact);
        }

        return result;
    }

    private static string Required(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Error(StockLedgerErrorCodes.InvalidStockIdentity, $"{label} is required for value adjustment.");
        return value.Trim();
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static StockLedgerException Error(string code, string message) =>
        new(new StockLedgerError(code, message));
}
