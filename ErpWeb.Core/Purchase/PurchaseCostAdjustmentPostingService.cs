using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed record PurchaseCostAdjustmentRequest(
    string AdjustmentType,
    string SourceDocumentType,
    string SourceDocumentNo,
    int SourceDocumentLine,
    int SourceCostingRevision,
    string ItemCode,
    string CostMethod,
    decimal BaseQty,
    decimal ActualBaseAmount,
    decimal ReferenceBaseAmount,
    decimal? CommercialReferenceAmount,
    decimal TotalAdjustmentAmount,
    decimal InventoryAdjustmentAmount,
    decimal ConsumedVarianceAmount,
    StockValueAdjustmentRequest? ValueAdjustment,
    string? PoNo = null,
    short? PoRelNo = null,
    short? PoLineNo = null,
    long? ReversesAdjustmentId = null);

public interface IPurchaseCostAdjustmentPostingService
{
    Task<StockPostingExecutionResult<IReadOnlyList<PurchaseCostAdjustment>>> PostAsync(
        StockPostingCommand command,
        IReadOnlyCollection<PurchaseCostAdjustmentRequest> adjustments,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PurchaseCostAdjustment>> AppendInTransactionAsync(
        StockPostingContext context,
        IReadOnlyCollection<PurchaseCostAdjustmentRequest> adjustments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Procurement value adjustments share the ordinary stock posting envelope and transaction. The
/// service writes audit rows only after the shared zero-quantity writer has accepted the value.
/// </summary>
public sealed class PurchaseCostAdjustmentPostingService : IPurchaseCostAdjustmentPostingService
{
    private readonly IStockPostingCoordinator _coordinator;
    private readonly IStockValueAdjustmentWriter _valueWriter;

    public PurchaseCostAdjustmentPostingService(
        IStockPostingCoordinator coordinator,
        IStockValueAdjustmentWriter valueWriter)
    {
        _coordinator = coordinator;
        _valueWriter = valueWriter;
    }

    public async Task<StockPostingExecutionResult<IReadOnlyList<PurchaseCostAdjustment>>> PostAsync(
        StockPostingCommand command,
        IReadOnlyCollection<PurchaseCostAdjustmentRequest> adjustments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(adjustments);

        return await _coordinator.ExecuteAsync<IReadOnlyList<PurchaseCostAdjustment>>(
            command,
            (context, ct) => AppendInTransactionAsync(context, adjustments, ct),
            cancellationToken);
    }

    public async Task<IReadOnlyList<PurchaseCostAdjustment>> AppendInTransactionAsync(
        StockPostingContext context,
        IReadOnlyCollection<PurchaseCostAdjustmentRequest> adjustments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(adjustments);
        context.EnsureUnsealed();

        // FIFO corrections are allocated against the exact originating receipt layer. The
        // request builders intentionally share the WAC shape for compatibility, so normalize the
        // FIFO inventory/consumed split at the final posting seam while the branch lock is held.
        var preparedAdjustments = string.Equals(
                context.CostMethod, StockCostMethods.Fifo, StringComparison.OrdinalIgnoreCase)
            ? await PrepareFifoAdjustmentsAsync(context, adjustments, cancellationToken)
            : adjustments.ToArray();

        var valueRequests = preparedAdjustments.Where(x => x.ValueAdjustment is not null)
            .Select(x => x.ValueAdjustment!)
            .ToArray();
        var facts = await _valueWriter.AppendAsync(context, valueRequests, cancellationToken);
        var factsByLine = facts.ToDictionary(x => (x.PostingLineNo, x.SplitOrdinal));
        var rows = new List<PurchaseCostAdjustment>(preparedAdjustments.Count);
        foreach (var request in preparedAdjustments)
        {
            var fact = request.ValueAdjustment is null
                ? null
                : factsByLine[(request.ValueAdjustment.PostingLineNo,
                    request.ValueAdjustment.SplitOrdinal)];
            rows.Add(new PurchaseCostAdjustment
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                AdjustmentType = request.AdjustmentType,
                SourceDocumentType = request.SourceDocumentType,
                SourceDocumentNo = request.SourceDocumentNo,
                SourceDocumentLine = request.SourceDocumentLine,
                SourceCostingRevision = request.SourceCostingRevision,
                PoNo = request.PoNo,
                PoRelNo = request.PoRelNo,
                PoLineNo = request.PoLineNo,
                ItemCode = request.ItemCode,
                CostMethod = request.CostMethod,
                BaseQty = request.BaseQty,
                ActualBaseAmount = request.ActualBaseAmount,
                ReferenceBaseAmount = request.ReferenceBaseAmount,
                CommercialReferenceAmount = request.CommercialReferenceAmount,
                TotalAdjustmentAmount = request.TotalAdjustmentAmount,
                InventoryAdjustmentAmount = request.InventoryAdjustmentAmount,
                ConsumedVarianceAmount = request.ConsumedVarianceAmount,
                StockPostingId = context.Posting.Id,
                InventoryAdjustmentFact = fact,
                ReversesAdjustmentId = request.ReversesAdjustmentId,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = context.UserId
            });
        }
        context.Db.PurchaseCostAdjustments.AddRange(rows);
        return rows;
    }

    private static async Task<IReadOnlyList<PurchaseCostAdjustmentRequest>> PrepareFifoAdjustmentsAsync(
        StockPostingContext context,
        IReadOnlyCollection<PurchaseCostAdjustmentRequest> adjustments,
        CancellationToken cancellationToken)
    {
        var candidates = adjustments
            .Where(x => string.Equals(x.CostMethod, StockCostMethods.Fifo, StringComparison.OrdinalIgnoreCase)
                        && x.ValueAdjustment is not null
                        && x.TotalAdjustmentAmount != 0m)
            .ToArray();
        if (candidates.Length == 0)
            return adjustments.ToArray();

        var reversalFactIds = candidates
            .Where(x => x.ValueAdjustment?.ReversesValuationFactId is not null)
            .Select(x => x.ValueAdjustment!.ReversesValuationFactId!.Value)
            .Distinct()
            .ToArray();
        var reversalFacts = reversalFactIds.Length == 0
            ? new Dictionary<long, StockValuationFact>()
            : await context.Db.StockValuationFacts.AsNoTracking()
                .Where(x => reversalFactIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken);

        var originIds = candidates
            .Select(x => ResolveOriginFactId(x, reversalFacts))
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .Distinct()
            .ToArray();
        if (originIds.Length == 0)
        {
            throw Error(StockLedgerErrorCodes.ValuationRequired,
                "FIFO purchase variance is missing the exact originating receipt valuation fact.");
        }

        var layers = await context.Db.StockFifoLayers
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && originIds.Contains(x.OriginValuationFactId))
            .OrderBy(x => x.OriginValuationFactId)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var layersByOrigin = layers
            .GroupBy(x => x.OriginValuationFactId)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.Id).ToArray());
        var result = new List<PurchaseCostAdjustmentRequest>(adjustments.Count);

        foreach (var request in adjustments)
        {
            if (!string.Equals(request.CostMethod, StockCostMethods.Fifo, StringComparison.OrdinalIgnoreCase)
                || request.ValueAdjustment is null
                || request.TotalAdjustmentAmount == 0m)
            {
                result.Add(request);
                continue;
            }

            var originId = ResolveOriginFactId(request, reversalFacts);
            if (originId is null || !layersByOrigin.TryGetValue(originId.Value, out var originLayers))
            {
                throw Error(StockLedgerErrorCodes.ValuationRequired,
                    $"FIFO purchase variance for item '{request.ItemCode}' has no originating FIFO layer.");
            }

            var isExactReversal = request.ReversesAdjustmentId is not null
                                  && request.ValueAdjustment?.ReversesValuationFactId is not null;
            var desiredInventory = isExactReversal
                ? (decimal?)await ResolveExactReversalInventoryAsync(
                    context, request, cancellationToken)
                : null;
            var signedInventory = desiredInventory ?? AllocateNormalInventory(
                request.TotalAdjustmentAmount,
                request.BaseQty,
                originLayers);

            ApplyLayerValue(originLayers, signedInventory, isExactReversal);
            var consumed = Money(request.TotalAdjustmentAmount - signedInventory);
            var valueAdjustment = signedInventory == 0m
                ? null
                : (request.ValueAdjustment ?? throw Error(
                    StockLedgerErrorCodes.ValuationRequired,
                    $"FIFO purchase variance for item '{request.ItemCode}' has no value-fact identity.")) with
                {
                    Amount = Math.Abs(signedInventory),
                    Direction = signedInventory > 0m ? 1 : -1,
                    OriginalValuationFactId = originId
                };

            result.Add(request with
            {
                InventoryAdjustmentAmount = signedInventory,
                ConsumedVarianceAmount = consumed,
                ValueAdjustment = valueAdjustment
            });
        }

        return result;
    }

    private static long? ResolveOriginFactId(
        PurchaseCostAdjustmentRequest request,
        IReadOnlyDictionary<long, StockValuationFact> reversalFacts)
    {
        var direct = request.ValueAdjustment?.OriginalValuationFactId;
        if (request.ValueAdjustment?.ReversesValuationFactId is long reversedId
            && reversalFacts.TryGetValue(reversedId, out var originalAdjustment))
        {
            return originalAdjustment.OriginalValuationFactId ?? direct;
        }

        return direct;
    }

    private static decimal AllocateNormalInventory(
        decimal totalAdjustment,
        decimal baseQty,
        IReadOnlyCollection<StockFifoLayer> layers)
    {
        var openLayers = layers
            .Where(x => x.Status == StockFifoLayerStatuses.Open && x.RemainingQty > Epsilon)
            .ToArray();
        var openQty = Quantity(openLayers.Sum(x => x.RemainingQty));
        if (openQty <= Epsilon || baseQty <= Epsilon)
            return 0m;

        var eligibleQty = Quantity(Math.Min(openQty, baseQty));
        var proportional = Money(totalAdjustment * eligibleQty / baseQty);
        if (proportional >= 0m)
            return proportional;

        var availableValue = Money(openLayers.Sum(x => Math.Max(x.RemainingValue, 0m)));
        return Money(-Math.Min(Math.Abs(proportional), availableValue));
    }

    private static async Task<decimal> ResolveExactReversalInventoryAsync(
        StockPostingContext context,
        PurchaseCostAdjustmentRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ReversesAdjustmentId is not long adjustmentId)
            throw Error(StockLedgerErrorCodes.ReversalDependency,
                "FIFO reversal is missing the original purchase adjustment identity.");
        var original = await context.Db.PurchaseCostAdjustments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == adjustmentId, cancellationToken);
        if (original is null)
            throw Error(StockLedgerErrorCodes.ReversalDependency,
                $"Purchase adjustment {adjustmentId} was not found for exact FIFO reversal.");
        return Money(-original.InventoryAdjustmentAmount);
    }

    private static void ApplyLayerValue(
        IReadOnlyCollection<StockFifoLayer> layers,
        decimal signedAmount,
        bool exactReversal)
    {
        if (signedAmount == 0m)
            return;

        var openLayers = layers
            .Where(x => x.Status == StockFifoLayerStatuses.Open && x.RemainingQty > Epsilon)
            .OrderBy(x => x.Id)
            .ToArray();
        var remaining = Math.Abs(signedAmount);
        if (openLayers.Length == 0)
            throw Error(StockLedgerErrorCodes.ReversalDependency,
                "The originating FIFO layer is no longer open for this purchase variance.");

        var totalOpenQty = openLayers.Sum(x => x.RemainingQty);
        for (var index = 0; index < openLayers.Length; index++)
        {
            if (remaining <= Epsilon)
                break;
            var layer = openLayers[index];
            var available = layer.RemainingValue;
            var amount = signedAmount < 0m
                ? Math.Min(remaining, Math.Max(available, 0m))
                : index == openLayers.Length - 1
                    ? remaining
                    : Math.Min(remaining, Math.Abs(signedAmount) * layer.RemainingQty / totalOpenQty);
            amount = Money(amount);
            if (amount <= Epsilon)
                continue;
            if (signedAmount < 0m && amount > available + Epsilon)
                throw Error(StockLedgerErrorCodes.ReversalDependency,
                    $"FIFO layer {layer.Id} cannot absorb the negative purchase variance.");

            var signed = signedAmount < 0m ? -amount : amount;
            layer.RemainingValue = Money(layer.RemainingValue + signed);
            layer.AccumulatedAdjustment = Money(layer.AccumulatedAdjustment + signed);
            if (layer.RemainingValue < -Epsilon)
                throw Error(StockLedgerErrorCodes.ReversalDependency,
                    $"FIFO layer {layer.Id} would have negative remaining value.");
            layer.RemainingValue = Math.Max(0m, layer.RemainingValue);
            layer.CurrentUnitCost = layer.RemainingQty <= Epsilon
                ? 0m
                : Money(layer.RemainingValue / layer.RemainingQty);
            remaining = Money(remaining - amount);
        }

        if (remaining > Epsilon)
        {
            throw Error(exactReversal
                    ? StockLedgerErrorCodes.ReversalDependency
                    : StockLedgerErrorCodes.LedgerMismatch,
                "FIFO purchase variance could not be fully allocated to open originating layers.");
        }
    }

    private static decimal Quantity(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private static decimal Money(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private static readonly decimal Epsilon = 0.0000005m;

    private static StockLedgerException Error(string code, string message) =>
        new(new StockLedgerError(code, message));
}
