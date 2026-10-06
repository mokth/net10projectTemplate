using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed class StandardCostRevaluationRequest
{
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public DateTime EffectiveAt { get; init; }
    public IReadOnlyCollection<string> ItemCodes { get; init; } = [];
    public int DocumentRevision { get; init; } = 1;
    public string? Reason { get; init; }
}

public sealed record StandardCostRevaluationLine(
    string ItemCode,
    decimal OnHandBaseQty,
    decimal PriorInventoryValue,
    decimal StandardUnitCost,
    decimal TargetInventoryValue,
    decimal RevaluationAmount,
    int StandardCostRevision);

public interface IStandardCostRevaluationService
{
    Task<StockPostingExecutionResult<IReadOnlyList<StandardCostRevaluationLine>>> RevalueAsync(
        StandardCostRevaluationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Posts the value-only Standard revaluation leg. Existing facts remain immutable; each delta is
/// appended as STANDARD_REVALUE_IN/OUT and the normal stock-cost state is advanced by the writer.
/// </summary>
public sealed class StandardCostRevaluationService : IStandardCostRevaluationService
{
    private const decimal Epsilon = 0.0000005m;

    private readonly IStockPostingCoordinator _coordinator;
    private readonly IStockCostMethodResolver _methodResolver;
    private readonly IItemStandardCostResolver _standardResolver;
    private readonly IStockValueAdjustmentWriter _adjustmentWriter;

    public StandardCostRevaluationService(
        IStockPostingCoordinator coordinator,
        IStockCostMethodResolver methodResolver,
        IItemStandardCostResolver standardResolver,
        IStockValueAdjustmentWriter adjustmentWriter)
    {
        _coordinator = coordinator;
        _methodResolver = methodResolver;
        _standardResolver = standardResolver;
        _adjustmentWriter = adjustmentWriter;
    }

    public Task<StockPostingExecutionResult<IReadOnlyList<StandardCostRevaluationLine>>> RevalueAsync(
        StandardCostRevaluationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty)
            throw new ArgumentException("A revaluation request ID is required.", nameof(request));
        if (request.DocumentRevision < 0)
            throw new ArgumentException("Document revision cannot be negative.", nameof(request));

        var items = request.ItemCodes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var command = new StockPostingCommand
        {
            RequestId = request.RequestId,
            CommandType = "STANDARD_COST_REVALUATION",
            SourceModule = "INVENTORY_COSTING",
            SourceDocumentType = "STANDARD_COST_REVALUATION",
            SourceDocumentId = request.RequestId.ToString("N"),
            SourceDocumentNo = request.RequestId.ToString("N"),
            DocumentRevision = request.DocumentRevision,
            PostingRole = "PRIMARY",
            EffectiveAt = request.EffectiveAt,
            ReasonCode = "STANDARD_COST_REVALUATION",
            ReasonText = request.Reason,
            Evidence = StockPostingFingerprint.Create(
                new { request.RequestId, request.EffectiveAt, request.DocumentRevision, items },
                new { request.Reason, items })
        };

        return _coordinator.ExecuteAsync(
            command,
            (context, ct) => RevalueInTransactionAsync(context, items, ct),
            cancellationToken);
    }

    private async Task<IReadOnlyList<StandardCostRevaluationLine>> RevalueInTransactionAsync(
        StockPostingContext context,
        IReadOnlyCollection<string> requestedItems,
        CancellationToken cancellationToken)
    {
        var method = await _methodResolver.ResolveAsync(
            context.Db, context.CompanyCode, context.BranchCode,
            context.Posting.EffectiveAt, cancellationToken);
        if (!string.Equals(method, StockCostMethods.Standard, StringComparison.OrdinalIgnoreCase))
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.CostPolicyInvalid,
                "Standard cost revaluation requires an active STANDARD costing policy."));
        context.CostMethod = StockCostMethods.Standard;

        var statesQuery = context.Db.StockCostStates
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.CostMethod == StockCostMethods.Standard
                        && x.OnHandBaseQty > Epsilon);
        var states = requestedItems.Count == 0
            ? await statesQuery.OrderBy(x => x.ItemCode).ToListAsync(cancellationToken)
            : await statesQuery.Where(x => requestedItems.Contains(x.ItemCode))
                .OrderBy(x => x.ItemCode).ToListAsync(cancellationToken);
        if (states.Count == 0)
            return [];

        var itemCodes = states.Select(x => x.ItemCode).ToArray();
        var hasLater = await context.Db.StockValuationFacts.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == context.CompanyCode
            && x.BranchCode == context.BranchCode
            && itemCodes.Contains(x.ItemCode)
            && x.EffectiveAt > context.Posting.EffectiveAt, cancellationToken);
        if (hasLater)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.BackdatedStockEvent,
                "Standard revaluation cannot be backdated behind later valued movements."));

        var requests = new List<StockValueAdjustmentRequest>();
        var lines = new List<StandardCostRevaluationLine>(states.Count);
        var postingLine = 0;
        foreach (var state in states)
        {
            var standard = await _standardResolver.ResolveAsync(
                context.Db, context.CompanyCode, context.BranchCode,
                state.ItemCode, context.Posting.EffectiveAt, cancellationToken);
            var target = Money(state.OnHandBaseQty * standard.TotalStandardCost);
            var delta = Money(target - state.InventoryValue);
            lines.Add(new StandardCostRevaluationLine(
                state.ItemCode,
                Quantity(state.OnHandBaseQty),
                Money(state.InventoryValue),
                standard.TotalStandardCost,
                target,
                delta,
                standard.Revision));
            if (Math.Abs(delta) <= Epsilon)
                continue;

            postingLine++;
            requests.Add(new StockValueAdjustmentRequest(
                postingLine,
                0,
                state.ItemCode,
                await ResolveBaseUomAsync(context.Db, context, state.ItemCode, cancellationToken),
                Math.Abs(delta),
                delta > 0m ? 1 : -1,
                delta > 0m ? "STANDARD_REVALUE_IN" : "STANDARD_REVALUE_OUT",
                StockValuationSources.StandardRevaluation,
                SourceDocumentLine: postingLine.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        await _adjustmentWriter.AppendAsync(context, requests, cancellationToken);
        return lines;
    }

    private static async Task<string> ResolveBaseUomAsync(
        AppDbContext db,
        StockPostingContext context,
        string itemCode,
        CancellationToken cancellationToken)
    {
        var uom = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.ItemCode == itemCode)
            .OrderByDescending(x => x.EffectiveAt)
            .ThenByDescending(x => x.Id)
            .Select(x => x.BaseUom)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(uom))
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.ValuationRequired,
                $"No authoritative base UOM exists for Standard revaluation item '{itemCode}'."));
        return uom;
    }

    private static decimal Quantity(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private static decimal Money(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);
}
