using ErpWeb.Model.Data;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed class StockCostMethodCutoverRequest
{
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public DateTime EffectiveAt { get; init; }
    public string NewMethod { get; init; } = string.Empty;
    public IReadOnlyCollection<string> ItemCodes { get; init; } = [];
    public int DocumentRevision { get; init; } = 1;
    public string? Reason { get; init; }
}

public sealed class StockCostMethodCutoverRollbackRequest
{
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public long CutoverPostingId { get; init; }
    public DateTime EffectiveAt { get; init; } = DateTime.UtcNow;
    public int DocumentRevision { get; init; } = 1;
    public string? Reason { get; init; }
}

public sealed record StockCostMethodCutoverLine(
    string ItemCode,
    string OldMethod,
    string NewMethod,
    decimal BaseQty,
    decimal TransferredValue,
    decimal? StandardRevaluationAmount);

public interface IStockCostMethodCutoverService
{
    Task<StockPostingExecutionResult<IReadOnlyList<StockCostMethodCutoverLine>>> CutoverAsync(
        StockCostMethodCutoverRequest request,
        CancellationToken cancellationToken = default);

    Task<StockPostingExecutionResult<IReadOnlyList<StockCostMethodCutoverLine>>> RollbackAsync(
        StockCostMethodCutoverRollbackRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Performs a value-neutral old-method OUT/new-method IN transfer in one sealed posting. The
/// policy revision and method states are changed in the same transaction, so the new method cannot
/// become active while the old pool is still authoritative.
/// </summary>
public sealed class StockCostMethodCutoverService : IStockCostMethodCutoverService
{
    private const decimal Epsilon = 0.0000005m;

    private readonly IStockPostingCoordinator _coordinator;
    private readonly IStockCostMethodResolver _methodResolver;
    private readonly IItemStandardCostResolver _standardResolver;
    private readonly IStockValueAdjustmentWriter _adjustmentWriter;
    private readonly IAccessRightService _accessRights;

    public StockCostMethodCutoverService(
        IStockPostingCoordinator coordinator,
        IStockCostMethodResolver methodResolver,
        IItemStandardCostResolver standardResolver,
        IStockValueAdjustmentWriter adjustmentWriter,
        IAccessRightService accessRights)
    {
        _coordinator = coordinator;
        _methodResolver = methodResolver;
        _standardResolver = standardResolver;
        _adjustmentWriter = adjustmentWriter;
        _accessRights = accessRights;
    }

    public Task<StockPostingExecutionResult<IReadOnlyList<StockCostMethodCutoverLine>>> CutoverAsync(
        StockCostMethodCutoverRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty)
            throw new ArgumentException("A cutover request ID is required.", nameof(request));
        var method = NormalizeMethod(request.NewMethod);
        if (!StockCostMethods.All.Contains(method))
            throw new ArgumentException($"Unsupported stock cost method '{request.NewMethod}'.", nameof(request));
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
            CommandType = "COST_METHOD_CUTOVER",
            SourceModule = "INVENTORY_COSTING",
            SourceDocumentType = "COST_METHOD_POLICY",
            SourceDocumentId = request.RequestId.ToString("N"),
            SourceDocumentNo = request.RequestId.ToString("N"),
            DocumentRevision = request.DocumentRevision,
            PostingRole = "PRIMARY",
            EffectiveAt = request.EffectiveAt,
            ReasonCode = "COSTING_METHOD_CHANGE",
            ReasonText = request.Reason,
            Evidence = StockPostingFingerprint.Create(
                new { request.RequestId, request.EffectiveAt, method, request.DocumentRevision, items },
                new { request.Reason, items })
        };

        return _coordinator.ExecuteAsync(
            command,
            (context, ct) => CutoverInTransactionAsync(context, method, items, request.Reason, ct),
            cancellationToken);
    }

    public Task<StockPostingExecutionResult<IReadOnlyList<StockCostMethodCutoverLine>>> RollbackAsync(
        StockCostMethodCutoverRollbackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty)
            throw new ArgumentException("A cutover rollback request ID is required.", nameof(request));
        if (request.CutoverPostingId <= 0)
            throw new ArgumentException("A sealed cutover posting ID is required.", nameof(request));
        if (request.DocumentRevision < 0)
            throw new ArgumentException("Document revision cannot be negative.", nameof(request));

        var command = new StockPostingCommand
        {
            RequestId = request.RequestId,
            CommandType = "COST_METHOD_CUTOVER_ROLLBACK",
            SourceModule = "INVENTORY_COSTING",
            SourceDocumentType = "COST_METHOD_POLICY",
            SourceDocumentId = $"CUTOVER_ROLLBACK:{request.CutoverPostingId}",
            SourceDocumentNo = $"CUTOVER_ROLLBACK:{request.CutoverPostingId}",
            DocumentRevision = request.DocumentRevision,
            PostingRole = "REVERSAL",
            EffectiveAt = request.EffectiveAt,
            ReversesPostingId = request.CutoverPostingId,
            ReasonCode = "COST_METHOD_CUTOVER_ROLLBACK",
            ReasonText = request.Reason,
            Evidence = StockPostingFingerprint.Create(
                new { request.RequestId, request.CutoverPostingId, request.EffectiveAt, request.DocumentRevision },
                new { request.Reason })
        };

        return _coordinator.ExecuteAsync(
            command,
            (context, ct) => RollbackInTransactionAsync(
                context, request.CutoverPostingId, request.Reason, ct),
            cancellationToken);
    }

    private async Task<IReadOnlyList<StockCostMethodCutoverLine>> CutoverInTransactionAsync(
        StockPostingContext context,
        string newMethod,
        IReadOnlyCollection<string> requestedItems,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (!await _accessRights.CanAsync(
                MenuCodes.InventoryStockValue,
                PermissionCodes.CostingMethodChange,
                cancellationToken))
            throw Error(StockLedgerErrorCodes.CostPolicyInvalid,
                "The caller lacks COSTING_METHOD_CHANGE permission.");

        if (context.Posting.EffectiveAt.Date.Day != 1)
            throw Error(StockLedgerErrorCodes.CostPolicyInvalid,
                "Cost-method cutover must begin on the first day of an open accounting period.");
        var closedThrough = await IvPeriodCloseGuard.ClosedThroughAsync(
            context.Db, context.CompanyCode, context.BranchCode, cancellationToken);
        if (closedThrough is not DateTime priorClose
            || context.Posting.EffectiveAt.Date != priorClose.Date.AddDays(1))
            throw Error(StockLedgerErrorCodes.CostPolicyInvalid,
                "The accounting period immediately before a cost-method cutover must be closed.");

        var oldMethod = await _methodResolver.ResolveAsync(
            context.Db, context.CompanyCode, context.BranchCode,
            context.Posting.EffectiveAt, cancellationToken);
        if (string.Equals(oldMethod, newMethod, StringComparison.OrdinalIgnoreCase))
            throw Error(StockLedgerErrorCodes.CostPolicyInvalid,
                $"The branch is already using {newMethod}.");

        var oldPolicy = await context.Db.StockCostPolicyRevisions
            .SingleOrDefaultAsync(x => x.CompanyCode == context.CompanyCode
                                       && x.BranchCode == context.BranchCode
                                       && x.Status == StockCostPolicyStatuses.Active
                                       && x.CostMethod == oldMethod
                                       && x.EffectiveFrom <= context.Posting.EffectiveAt.Date
                                       && (x.EffectiveTo == null || x.EffectiveTo > context.Posting.EffectiveAt.Date),
                cancellationToken);
        if (oldPolicy is null)
            throw Error(StockLedgerErrorCodes.CostPolicyInvalid,
                "The active costing policy could not be locked for cutover.");
        if (oldPolicy.EffectiveFrom >= context.Posting.EffectiveAt.Date)
            throw Error(StockLedgerErrorCodes.CostPolicyInvalid,
                "Cost-method cutover must begin after the current policy effective date.");

        var allStates = context.Db.StockCostStates
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.CostMethod == oldMethod
                        && x.OnHandBaseQty > Epsilon);
        var allPositiveStates = await allStates.OrderBy(x => x.ItemCode)
            .ToListAsync(cancellationToken);
        var states = allPositiveStates;
        if (requestedItems.Count > 0)
        {
            var requested = requestedItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var omitted = allPositiveStates
                .Where(x => !requested.Contains(x.ItemCode))
                .Select(x => x.ItemCode)
                .ToArray();
            if (omitted.Length > 0)
                throw Error(StockLedgerErrorCodes.CostPolicyInvalid,
                    $"Branch-wide cutover omitted positive-stock item(s): {string.Join(", ", omitted)}.");
            states = allPositiveStates.Where(x => requested.Contains(x.ItemCode)).ToList();
        }
        if (states.Count == 0)
            throw Error(StockLedgerErrorCodes.CostPolicyInvalid,
                "No positive stock-cost pools are available for cutover.");

        var itemCodes = states.Select(x => x.ItemCode).ToArray();
        var hasLater = await context.Db.StockValuationFacts.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == context.CompanyCode
            && x.BranchCode == context.BranchCode
            && itemCodes.Contains(x.ItemCode)
            && x.EffectiveAt > context.Posting.EffectiveAt, cancellationToken);
        if (hasLater)
            throw Error(StockLedgerErrorCodes.BackdatedStockEvent,
                "Cost-method cutover cannot be backdated behind later valued movements.");

        await EnsureCutoverReconciliationAsync(context, states, cancellationToken);

        oldPolicy.EffectiveTo = context.Posting.EffectiveAt.Date;
        oldPolicy.Status = StockCostPolicyStatuses.Superseded;
        var now = DateTime.UtcNow;
        context.Db.StockCostPolicyRevisions.Add(new StockCostPolicyRevision
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            CostMethod = newMethod,
            EffectiveFrom = context.Posting.EffectiveAt.Date,
            Status = StockCostPolicyStatuses.Active,
            Reason = reason,
            ApprovedBy = context.UserId,
            ApprovedAtUtc = now,
            CreatedAtUtc = now,
            CreatedBy = context.UserId
        });

        var latestFacts = await context.Db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && itemCodes.Contains(x.ItemCode))
            .GroupBy(x => x.ItemCode)
            .Select(x => x.OrderByDescending(y => y.EffectiveAt).ThenByDescending(y => y.Id).First())
            .ToDictionaryAsync(x => x.ItemCode, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var baseCurrency = await context.Db.Companies.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode)
            .Select(x => x.CurrencyCode)
            .FirstOrDefaultAsync(cancellationToken);
        var oldFifoLayers = oldMethod == StockCostMethods.Fifo
            ? await context.Db.StockFifoLayers.Where(x => x.CompanyCode == context.CompanyCode
                                                           && x.BranchCode == context.BranchCode
                                                           && itemCodes.Contains(x.ItemCode)
                                                           && x.Status == StockFifoLayerStatuses.Open)
                .ToListAsync(cancellationToken)
            : [];

        var lines = new List<StockCostMethodCutoverLine>(states.Count);
        var standardAdjustments = new List<StockValueAdjustmentRequest>();
        var postingLine = 0;
        foreach (var oldState in states)
        {
            if (!latestFacts.TryGetValue(oldState.ItemCode, out var latest))
                throw Error(StockLedgerErrorCodes.ValuationRequired,
                    $"No authoritative valuation fact exists for cutover item '{oldState.ItemCode}'.");
            var baseUom = latest.BaseUom;
            var qty = Quantity(oldState.OnHandBaseQty);
            var value = Money(oldState.InventoryValue);
            var lineNo = ++postingLine;
            var oldFact = NewCutoverFact(context, latest, lineNo, 0, oldMethod, -1, qty, value,
                "COST_METHOD_CUTOVER_OUT", baseCurrency);
            var newFact = NewCutoverFact(context, latest, lineNo, 1, newMethod, 1, qty, value,
                "COST_METHOD_CUTOVER_IN", baseCurrency);
            context.Db.StockValuationFacts.Add(oldFact);
            context.Db.StockValuationFacts.Add(newFact);

            oldState.OnHandBaseQty = 0m;
            oldState.InventoryValue = 0m;
            oldState.AverageUnitCost = 0m;
            oldState.CurrentUnitCost = 0m;
            oldState.LastValuationFact = oldFact;
            oldState.LastPostingSequence = context.Posting.PostingSequence;

            var newState = await GetOrCreateStateAsync(
                context, newMethod, oldState.ItemCode, cancellationToken);
            if (newState.OnHandBaseQty > Epsilon || newState.InventoryValue > Epsilon)
                throw Error(StockLedgerErrorCodes.CostPolicyInvalid,
                    $"Target {newMethod} state for item '{oldState.ItemCode}' is not empty.");
            newState.OnHandBaseQty = qty;
            newState.InventoryValue = value;
            newState.AverageUnitCost = qty <= Epsilon ? 0m : Money(value / qty);
            newState.CurrentUnitCost = newState.AverageUnitCost;
            newState.LastValuationFact = newFact;
            newState.LastPostingSequence = context.Posting.PostingSequence;

            if (oldMethod == StockCostMethods.Fifo)
            {
                foreach (var layer in oldFifoLayers.Where(x =>
                             string.Equals(x.ItemCode, oldState.ItemCode, StringComparison.OrdinalIgnoreCase)))
                {
                    // Preserve the exact pre-cutover layer remainder for a later exact rollback;
                    // status, rather than data deletion/zeroing, removes it from the active pool.
                    layer.Status = StockFifoLayerStatuses.Closed;
                }
            }
            if (newMethod == StockCostMethods.Fifo)
            {
                context.Db.StockFifoLayers.Add(new StockFifoLayer
                {
                    CompanyCode = context.CompanyCode,
                    BranchCode = context.BranchCode,
                    ItemCode = oldState.ItemCode,
                    BaseUom = baseUom,
                    OriginValuationFact = newFact,
                    OriginStockPosting = context.Posting,
                    ReceiptEffectiveAt = context.Posting.EffectiveAt,
                    OriginalQty = qty,
                    RemainingQty = qty,
                    OriginalValue = value,
                    RemainingValue = value,
                    CurrentUnitCost = qty <= Epsilon ? 0m : Money(value / qty),
                    SourceDocumentType = context.Posting.SourceDocumentType,
                    SourceDocumentNo = context.Posting.SourceDocumentNo,
                    SourceDocumentLine = oldState.ItemCode,
                    Status = StockFifoLayerStatuses.Open
                });
            }

            decimal? standardRevalue = null;
            if (newMethod == StockCostMethods.Standard)
            {
                var standard = await _standardResolver.ResolveAsync(
                    context.Db, context.CompanyCode, context.BranchCode,
                    oldState.ItemCode, context.Posting.EffectiveAt, cancellationToken);
                var target = Money(qty * standard.TotalStandardCost);
                var delta = Money(target - value);
                standardRevalue = delta;
                if (Math.Abs(delta) > Epsilon)
                {
                    standardAdjustments.Add(new StockValueAdjustmentRequest(
                        postingLine + 1,
                        0,
                        oldState.ItemCode,
                        baseUom,
                        Math.Abs(delta),
                        delta > 0m ? 1 : -1,
                        delta > 0m ? "STANDARD_REVALUE_IN" : "STANDARD_REVALUE_OUT",
                        StockValuationSources.StandardRevaluation,
                        SourceDocumentLine: oldState.ItemCode));
                    postingLine++;
                }
            }

            lines.Add(new StockCostMethodCutoverLine(
                oldState.ItemCode, oldMethod, newMethod, qty, value, standardRevalue));
        }

        context.CostMethod = newMethod;
        await _adjustmentWriter.AppendAsync(context, standardAdjustments, cancellationToken);
        return lines;
    }

    private static async Task<IReadOnlyList<StockCostMethodCutoverLine>> RollbackInTransactionAsync(
        StockPostingContext context,
        long cutoverPostingId,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (!await context.Db.StockPostings.AsNoTracking().AnyAsync(
                x => x.Id == cutoverPostingId
                     && x.CompanyCode == context.CompanyCode
                     && x.BranchCode == context.BranchCode
                     && x.CommandType == "COST_METHOD_CUTOVER"
                     && x.SealedAtUtc != null,
                cancellationToken))
            throw Error(StockLedgerErrorCodes.InvalidStockIdentity,
                "The requested cutover rollback target is not a sealed cutover posting.");

        var originalFacts = await context.Db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.StockPostingId == cutoverPostingId)
            .OrderBy(x => x.PostingLineNo)
            .ThenBy(x => x.SplitOrdinal)
            .ToListAsync(cancellationToken);
        var outbound = originalFacts
            .Where(x => string.Equals(x.MovementCode, "COST_METHOD_CUTOVER_OUT", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var inbound = originalFacts
            .Where(x => string.Equals(x.MovementCode, "COST_METHOD_CUTOVER_IN", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (outbound.Length == 0 || inbound.Length != outbound.Length)
            throw Error(StockLedgerErrorCodes.ReversalDependency,
                "The cutover posting does not contain a complete old-method OUT/new-method IN pair set.");

        var itemCodes = outbound.Select(x => x.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var laterFacts = await context.Db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && itemCodes.Contains(x.ItemCode)
                        && x.StockPostingId != cutoverPostingId
                        && x.StockPosting!.PostingSequence >
                           context.Db.StockPostings.Where(p => p.Id == cutoverPostingId)
                               .Select(p => p.PostingSequence).First())
            .Select(x => x.Id)
            .AnyAsync(cancellationToken);
        if (laterFacts)
            throw Error(StockLedgerErrorCodes.ReversalDependency,
                "The cutover cannot be rolled back while later valued postings depend on the new method.");

        var oldMethod = outbound[0].CostMethod;
        var newMethod = inbound[0].CostMethod;
        if (outbound.Any(x => !string.Equals(x.CostMethod, oldMethod, StringComparison.OrdinalIgnoreCase))
            || inbound.Any(x => !string.Equals(x.CostMethod, newMethod, StringComparison.OrdinalIgnoreCase)))
            throw Error(StockLedgerErrorCodes.LedgerMismatch,
                "The cutover posting contains inconsistent costing methods.");

        var cutover = await context.Db.StockPostings.AsNoTracking()
            .SingleAsync(x => x.Id == cutoverPostingId, cancellationToken);
        var oldPolicy = await context.Db.StockCostPolicyRevisions.SingleOrDefaultAsync(x =>
            x.CompanyCode == context.CompanyCode
            && x.BranchCode == context.BranchCode
            && x.CostMethod == oldMethod
            && x.EffectiveFrom < cutover.EffectiveAt.Date
            && x.Status == StockCostPolicyStatuses.Superseded
            && x.EffectiveTo == cutover.EffectiveAt.Date, cancellationToken);
        var newPolicy = await context.Db.StockCostPolicyRevisions.SingleOrDefaultAsync(x =>
            x.CompanyCode == context.CompanyCode
            && x.BranchCode == context.BranchCode
            && x.CostMethod == newMethod
            && x.Status == StockCostPolicyStatuses.Active
            && x.EffectiveFrom == cutover.EffectiveAt.Date, cancellationToken);
        if (oldPolicy is null || newPolicy is null)
            throw Error(StockLedgerErrorCodes.CostPolicyInvalid,
                "The policy revisions required for exact cutover rollback were not found.");

        var methods = new[] { oldMethod, newMethod };
        var states = await context.Db.StockCostStates
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && itemCodes.Contains(x.ItemCode)
                        && methods.Contains(x.CostMethod))
            .ToListAsync(cancellationToken);
        var stateMap = states.ToDictionary(
            x => (x.ItemCode, x.CostMethod),
            x => x,
            ItemCostMethodComparer.Instance);
        var oldFifoLayers = oldMethod == StockCostMethods.Fifo
            ? await context.Db.StockFifoLayers
                .Where(x => x.CompanyCode == context.CompanyCode
                            && x.BranchCode == context.BranchCode
                            && itemCodes.Contains(x.ItemCode)
                            && x.Status == StockFifoLayerStatuses.Closed
                            && !inbound.Select(f => f.Id).Contains(x.OriginValuationFactId))
                .ToListAsync(cancellationToken)
            : [];

        var reversedFacts = new List<StockValuationFact>(originalFacts.Count);
        var lines = new List<StockCostMethodCutoverLine>(outbound.Length);
        foreach (var oldFact in outbound.OrderBy(x => x.PostingLineNo))
        {
            var newFact = inbound.Single(x => x.PostingLineNo == oldFact.PostingLineNo);
            var oldState = GetState(stateMap, oldFact.ItemCode, oldMethod);
            var newState = GetState(stateMap, newFact.ItemCode, newMethod);
            var targetRevaluation = originalFacts
                .Where(x => x.ItemCode == oldFact.ItemCode
                            && x.CostMethod == newMethod
                            && x.BaseQty == 0m
                            && (x.MovementCode == "STANDARD_REVALUE_IN"
                                || x.MovementCode == "STANDARD_REVALUE_OUT"))
                .Sum(x => x.CostAmount * x.Direction);

            var reverseOld = NewCutoverReversalFact(context, oldFact, oldFact.PostingLineNo, 0);
            var reverseNew = NewCutoverReversalFact(context, newFact, newFact.PostingLineNo, 1);
            reversedFacts.Add(reverseOld);
            reversedFacts.Add(reverseNew);

            oldState.OnHandBaseQty = Quantity(oldState.OnHandBaseQty + oldFact.BaseQty);
            oldState.InventoryValue = Money(oldState.InventoryValue + oldFact.CostAmount);
            oldState.CurrentUnitCost = oldState.OnHandBaseQty <= Epsilon
                ? 0m : Money(oldState.InventoryValue / oldState.OnHandBaseQty);
            oldState.AverageUnitCost = oldState.CurrentUnitCost;
            oldState.LastValuationFact = reverseOld;
            oldState.LastPostingSequence = context.Posting.PostingSequence;

            newState.OnHandBaseQty = Quantity(newState.OnHandBaseQty - newFact.BaseQty);
            newState.InventoryValue = Money(newState.InventoryValue - newFact.CostAmount - targetRevaluation);
            if (newState.OnHandBaseQty < -Epsilon || newState.InventoryValue < -Epsilon)
                throw Error(StockLedgerErrorCodes.ReversalDependency,
                    $"Target method state for item '{newFact.ItemCode}' cannot be restored exactly.");
            newState.OnHandBaseQty = Math.Max(0m, newState.OnHandBaseQty);
            newState.InventoryValue = Math.Max(0m, newState.InventoryValue);
            newState.CurrentUnitCost = newState.OnHandBaseQty <= Epsilon
                ? 0m : Money(newState.InventoryValue / newState.OnHandBaseQty);
            newState.AverageUnitCost = newState.CurrentUnitCost;
            newState.LastValuationFact = reverseNew;
            newState.LastPostingSequence = context.Posting.PostingSequence;

            if (oldMethod == StockCostMethods.Fifo)
            {
                var layers = oldFifoLayers.Where(x =>
                    string.Equals(x.ItemCode, oldFact.ItemCode, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (var layer in layers)
                    layer.Status = StockFifoLayerStatuses.Open;
                if (Quantity(layers.Sum(x => x.RemainingQty)) != oldState.OnHandBaseQty
                    || Money(layers.Sum(x => x.RemainingValue)) != oldState.InventoryValue)
                    throw Error(StockLedgerErrorCodes.ReversalDependency,
                        $"Closed FIFO layers for item '{oldFact.ItemCode}' cannot be restored exactly.");
            }
            if (newMethod == StockCostMethods.Fifo)
            {
                var targetLayer = await context.Db.StockFifoLayers.SingleOrDefaultAsync(x =>
                    x.CompanyCode == context.CompanyCode
                    && x.BranchCode == context.BranchCode
                    && x.OriginValuationFactId == newFact.Id, cancellationToken);
                if (targetLayer is null)
                    throw Error(StockLedgerErrorCodes.ReversalDependency,
                        $"FIFO cutover opening layer for item '{newFact.ItemCode}' was not found.");
                if (targetLayer.RemainingQty != targetLayer.OriginalQty
                    || targetLayer.RemainingValue != targetLayer.OriginalValue)
                    throw Error(StockLedgerErrorCodes.ReversalDependency,
                        $"FIFO cutover opening layer for item '{newFact.ItemCode}' has dependent consumption.");
                targetLayer.Status = StockFifoLayerStatuses.Closed;
            }

            lines.Add(new StockCostMethodCutoverLine(
                oldFact.ItemCode, oldMethod, newMethod, oldFact.BaseQty, oldFact.CostAmount,
                targetRevaluation));
        }

        foreach (var revaluation in originalFacts.Where(x => x.BaseQty == 0m
                     && (x.MovementCode == "STANDARD_REVALUE_IN"
                         || x.MovementCode == "STANDARD_REVALUE_OUT")))
        {
            var state = GetState(stateMap, revaluation.ItemCode, newMethod);
            var signed = revaluation.CostAmount * revaluation.Direction;
            state.InventoryValue = Money(state.InventoryValue - signed);
            if (state.InventoryValue < -Epsilon)
                throw Error(StockLedgerErrorCodes.ReversalDependency,
                    $"Standard revaluation reversal would make item '{state.ItemCode}' negative.");
            state.InventoryValue = Math.Max(0m, state.InventoryValue);
            state.CurrentUnitCost = state.OnHandBaseQty <= Epsilon
                ? 0m : Money(state.InventoryValue / state.OnHandBaseQty);
            state.AverageUnitCost = state.CurrentUnitCost;
            var reversal = NewCutoverReversalFact(
                context, revaluation, revaluation.PostingLineNo, revaluation.SplitOrdinal);
            reversedFacts.Add(reversal);
            state.LastValuationFact = reversal;
            state.LastPostingSequence = context.Posting.PostingSequence;
        }

        oldPolicy.Status = StockCostPolicyStatuses.Active;
        oldPolicy.EffectiveTo = null;
        newPolicy.Status = StockCostPolicyStatuses.Superseded;
        newPolicy.EffectiveTo = cutover.EffectiveAt.Date;
        context.Db.StockValuationFacts.AddRange(reversedFacts);
        return lines;
    }

    private static StockCostState GetState(
        IReadOnlyDictionary<(string ItemCode, string CostMethod), StockCostState> states,
        string itemCode,
        string method)
    {
        if (!states.TryGetValue((itemCode, method), out var state))
            throw Error(StockLedgerErrorCodes.ReversalDependency,
                $"Cost state for item '{itemCode}' and method '{method}' was not found.");
        return state;
    }

    private static StockValuationFact NewCutoverReversalFact(
        StockPostingContext context,
        StockValuationFact original,
        int postingLine,
        int splitOrdinal) => new()
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            LedgerEpochId = context.Epoch.Id,
            StockPostingId = context.Posting.Id,
            PostingLineNo = postingLine,
            SplitOrdinal = splitOrdinal,
            SourceLineId = $"{context.Posting.SourceDocumentType}:{context.Posting.SourceDocumentId}:{postingLine}:{splitOrdinal}",
            SourceDocumentType = context.Posting.SourceDocumentType,
            SourceDocumentId = context.Posting.SourceDocumentId,
            SourceDocumentNo = context.Posting.SourceDocumentNo,
            SourceDocumentLine = original.SourceDocumentLine,
            EffectiveAt = context.Posting.EffectiveAt,
            BusinessDate = context.Posting.BusinessDate,
            PeriodKey = context.Posting.PeriodKey,
            ItemCode = original.ItemCode,
            WarehouseCode = original.WarehouseCode,
            LocationCode = original.LocationCode,
            LotId = original.LotId,
            LotNo = original.LotNo,
            ItemStatus = original.ItemStatus,
            BaseUom = original.BaseUom,
            MovementCode = original.MovementCode + "_REVERSAL",
            Direction = -original.Direction,
            BaseQty = original.BaseQty,
            CostMethod = original.CostMethod,
            UnitCost = original.UnitCost,
            CostAmount = original.CostAmount,
            BaseCurrency = original.BaseCurrency,
            BaseCostAmount = original.BaseCostAmount,
            ValuationSource = StockValuationSources.OriginalReversal,
            ValuationStatus = StockValuationStatuses.Reversed,
            ValuationVersion = 1,
            OriginalValuationFactId = original.OriginalValuationFactId ?? original.Id,
            ReversesValuationFactId = original.Id,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = context.UserId
        };

    private static async Task EnsureCutoverReconciliationAsync(
        StockPostingContext context,
        IReadOnlyCollection<StockCostState> states,
        CancellationToken cancellationToken)
    {
        var unvalued = await context.Db.StockValuationFacts.AsNoTracking()
            .AnyAsync(x => x.CompanyCode == context.CompanyCode
                           && x.BranchCode == context.BranchCode
                           && x.ValuationStatus == StockValuationStatuses.Unvalued,
                cancellationToken);
        if (unvalued)
            throw Error(StockLedgerErrorCodes.ValuationRequired,
                "Cost-method cutover requires all existing valuation facts to be resolved.");

        var physical = await context.Db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode)
            .GroupBy(x => x.ICode)
            .Select(x => new { ItemCode = x.Key, Qty = x.Sum(y => y.StdQty) })
            .ToListAsync(cancellationToken);
        var stateByItem = states.ToDictionary(x => x.ItemCode, StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            var physicalQty = physical.FirstOrDefault(x =>
                string.Equals(x.ItemCode, state.ItemCode, StringComparison.OrdinalIgnoreCase))?.Qty ?? 0m;
            if (Quantity(physicalQty) != Quantity(state.OnHandBaseQty))
                throw Error(StockLedgerErrorCodes.LedgerMismatch,
                    $"Physical/value reconciliation failed for cutover item '{state.ItemCode}'.");
        }
        foreach (var row in physical.Where(x => x.Qty > Epsilon))
        {
            if (!stateByItem.ContainsKey(row.ItemCode))
                throw Error(StockLedgerErrorCodes.LedgerMismatch,
                    $"Physical item '{row.ItemCode}' has no active cost state for cutover.");
        }

        var postedPurchaseDocs = await context.Db.PoInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.Status == PoInvoiceStatuses.Posted
                        && x.Type == PoInvoiceTypes.Invoice)
            .Select(x => x.DocNo)
            .ToListAsync(cancellationToken);
        if (postedPurchaseDocs.Count > 0)
        {
            var settledDocs = await context.Db.PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(x => x.CompanyCode == context.CompanyCode
                            && x.BranchCode == context.BranchCode
                            && postedPurchaseDocs.Contains(x.PiDocNo))
                .Select(x => x.PiDocNo)
                .Distinct()
                .ToListAsync(cancellationToken);
            if (postedPurchaseDocs.Any(x => !settledDocs.Contains(x)))
                throw Error(StockLedgerErrorCodes.ValuationRequired,
                    "Cost-method cutover requires posted purchase invoices to have exact receipt settlement evidence.");
        }
    }

    private static async Task<StockCostState> GetOrCreateStateAsync(
        StockPostingContext context,
        string method,
        string itemCode,
        CancellationToken cancellationToken)
    {
        var state = await context.Db.StockCostStates.SingleOrDefaultAsync(x =>
            x.CompanyCode == context.CompanyCode
            && x.BranchCode == context.BranchCode
            && x.CostMethod == method
            && x.ItemCode == itemCode, cancellationToken);
        if (state is not null)
            return state;
        state = new StockCostState
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            ItemCode = itemCode,
            CostMethod = method
        };
        context.Db.StockCostStates.Add(state);
        return state;
    }

    private static StockValuationFact NewCutoverFact(
        StockPostingContext context,
        StockValuationFact latest,
        int postingLine,
        int splitOrdinal,
        string costMethod,
        int direction,
        decimal quantity,
        decimal value,
        string movementCode,
        string? baseCurrency)
    {
        var unit = quantity <= Epsilon ? 0m : Money(value / quantity);
        return new StockValuationFact
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            LedgerEpochId = context.Epoch.Id,
            StockPostingId = context.Posting.Id,
            PostingLineNo = postingLine,
            SplitOrdinal = splitOrdinal,
            SourceLineId = $"{context.Posting.SourceDocumentType}:{context.Posting.SourceDocumentId}:{latest.ItemCode}:{splitOrdinal}",
            SourceDocumentType = context.Posting.SourceDocumentType,
            SourceDocumentId = context.Posting.SourceDocumentId,
            SourceDocumentNo = context.Posting.SourceDocumentNo,
            SourceDocumentLine = latest.ItemCode,
            EffectiveAt = context.Posting.EffectiveAt,
            BusinessDate = context.Posting.BusinessDate,
            PeriodKey = context.Posting.PeriodKey,
            ItemCode = latest.ItemCode,
            WarehouseCode = latest.WarehouseCode,
            LocationCode = latest.LocationCode,
            LotId = latest.LotId,
            LotNo = latest.LotNo,
            ItemStatus = latest.ItemStatus,
            BaseUom = latest.BaseUom,
            MovementCode = movementCode,
            Direction = direction,
            BaseQty = quantity,
            CostMethod = costMethod,
            UnitCost = unit,
            CostAmount = value,
            BaseCurrency = baseCurrency,
            BaseCostAmount = value,
            ValuationSource = "COST_METHOD_CUTOVER",
            ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = 1,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = context.UserId
        };
    }

    private static string NormalizeMethod(string value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    private static decimal Quantity(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private static decimal Money(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private static StockLedgerException Error(string code, string message) =>
        new(new StockLedgerError(code, message));

    private sealed class ItemCostMethodComparer : IEqualityComparer<(string ItemCode, string CostMethod)>
    {
        public static ItemCostMethodComparer Instance { get; } = new();

        public bool Equals((string ItemCode, string CostMethod) x, (string ItemCode, string CostMethod) y) =>
            string.Equals(x.ItemCode, y.ItemCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.CostMethod, y.CostMethod, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ItemCode, string CostMethod) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ItemCode ?? string.Empty),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.CostMethod ?? string.Empty));
    }
}
