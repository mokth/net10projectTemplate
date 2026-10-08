using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionFinishedGoodReceiptService
{
    public async Task<IvMasterOperationResult<FinishedGoodCostTrace>> GetCostTraceAsync(
        int receiptId, long sourceId, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.ViewCost, ct);
            await using var db = await factory.CreateDbContextAsync(ct);

            var receipt = await db.ProductionFinishedGoodReceiptRows.AsNoTracking()
                .Include(x => x.Batch)
                .Include(x => x.Sources).ThenInclude(x => x.Detail)
                .SingleOrDefaultAsync(x => x.BatchId == receiptId
                    && x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode, ct);
            if (receipt is null)
                return Fail<FinishedGoodCostTrace>("Finished Good Receipt was not found.", IvMasterErrorCode.NotFound);

            var source = receipt.Sources.SingleOrDefault(x => x.Id == sourceId);
            if (source is null)
                return Fail<FinishedGoodCostTrace>("Finished Good Receipt source line was not found.", IvMasterErrorCode.NotFound);

            if (receipt.Batch.BatchStatus is not ("POSTED" or "REVERSED"))
                return Fail<FinishedGoodCostTrace>("Authoritative posted cost trace is available after posting.");
            if (receipt.PostingId is not long originalPostingId)
                return Fail<FinishedGoodCostTrace>("The original FG posting identity is missing.");

            var pool = await db.ProductionBalLots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Uid == source.ProductionBalLotId
                    && x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode, ct);
            var fact = await db.ProductionFinishedGoodFactRows.AsNoTracking()
                .SingleOrDefaultAsync(x => x.BatchId == receiptId
                    && x.SourceId == sourceId
                    && x.StockPostingId == originalPostingId
                    && x.ReversesFactId == null, ct);

            if (fact is null)
                return Fail<FinishedGoodCostTrace>("The original FG value fact is missing.");
            if (pool is null)
            {
                return IvMasterOperationResult<FinishedGoodCostTrace>.Ok(BuildInconsistentTrace(
                    receipt, source, null, fact, null, null, null, null,
                    ["The source production pool is missing from the stored evidence."]));
            }

            var orderNo = await db.ProductionWorkOrders.AsNoTracking()
                .Where(x => x.Uid == receipt.WorkOrderId)
                .Select(x => x.WorkOrderNo)
                .SingleOrDefaultAsync(ct) ?? pool.WorkOrderNo;
            var originalPosting = await db.StockPostings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == originalPostingId
                    && x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode, ct);
            var movement = await db.ProductionBalLotMovements.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Uid == fact.ProductionMovementId, ct);
            var history = await db.IvTrxHistories.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == fact.InventoryHistoryId, ct);
            var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking()
                .SingleOrDefaultAsync(x => x.MovementId == fact.ProductionMovementId, ct);
            var snapshot = await db.ProductionFinishedGoodPriceSnapshotRows.AsNoTracking()
                .SingleOrDefaultAsync(x => x.StockPostingId == originalPostingId
                    && x.DestinationBalanceId == fact.DestinationBalanceId, ct);

            var metadata = new TraceMetadata(receipt, source, pool, fact, orderNo, originalPostingId,
                movement, history, evidence, snapshot);
            AddAuthorityWarnings(metadata, scope, originalPosting);

            var originalFacts = await db.ProductionFinishedGoodFactRows.AsNoTracking()
                .Where(x => x.BatchId == receiptId
                    && x.StockPostingId == originalPostingId
                    && x.ReversesFactId == null)
                .OrderBy(x => x.SourceId)
                .ToListAsync(ct);
            var sourceById = receipt.Sources.ToDictionary(x => x.Id);
            if (originalFacts.Count == 0 || originalFacts.Any(x => !sourceById.ContainsKey(x.SourceId)))
                metadata.Warnings.Add("The original FG source/fact group is incomplete.");

            if (snapshot is not null && history is not null
                && (history.UnitPrice != snapshot.PostedUnitPrice || history.Cost != snapshot.PostedUnitPrice))
                metadata.Warnings.Add("The destination price snapshot does not match the original inventory history.");
            if (snapshot is null)
                metadata.Warnings.Add("The destination price snapshot is missing.");

            if (snapshot is not null)
            {
                var destinationFacts = originalFacts
                    .Where(x => x.DestinationBalanceId == fact.DestinationBalanceId)
                    .ToArray();
                var destinationQty = destinationFacts
                    .Where(x => sourceById.ContainsKey(x.SourceId))
                    .Sum(x => sourceById[x.SourceId].Detail.ToStdQty ?? 0m);
                var destinationValue = StockLedgerPrecision.Money(destinationFacts.Sum(x => x.TotalValue));
                if (destinationQty <= 0m
                    || IvQty.Round(destinationValue / destinationQty) != snapshot.PostedUnitPrice)
                    metadata.Warnings.Add("The destination price snapshot does not reconcile to the original destination slice.");
            }

            AddReversalWarnings(metadata, await ValidateReversalAsync(
                db, receipt, fact, movement, history, ct));

            if (metadata.Warnings.Count > 0)
            {
                return IvMasterOperationResult<FinishedGoodCostTrace>.Ok(BuildInconsistentTrace(
                    receipt, source, pool, fact, movement, history, evidence, snapshot, metadata.Warnings));
            }

            try
            {
                var replay = new CostTraceReplayContext(db, scope, evidence!.Generation);
                var prePool = await replay.ReplayPoolAsync(pool.Uid, originalPosting!.PostingSequence, ct);
                metadata.PoolBaseQtyBeforePosting = prePool.BaseQty;
                metadata.PoolValueBeforePosting = prePool.Value;
                metadata.Warnings.AddRange(replay.Warnings);

                var group = originalFacts
                    .Where(x => sourceById[x.SourceId].ProductionBalLotId == pool.Uid)
                    .OrderBy(x => x.SourceId)
                    .ToArray();
                if (group.Length == 0)
                    throw new TraceFailure("No original FG facts belong to the selected production pool.");

                var groupMovements = await db.ProductionBalLotMovements.AsNoTracking()
                    .Where(x => group.Select(f => f.ProductionMovementId).Contains(x.Uid))
                    .ToDictionaryAsync(x => x.Uid, ct);
                foreach (var lineFact in group)
                {
                    if (!groupMovements.TryGetValue(lineFact.ProductionMovementId, out var lineMovement)
                        || lineMovement.ProductionBalLotId != pool.Uid
                        || lineMovement.StockPostingId != originalPostingId
                        || lineMovement.MovementType != ProductionBalLotMovementTypes.FgReceiptOut)
                        throw new TraceFailure($"Original FG fact {lineFact.Id} does not belong to the selected source pool posting.");
                }

                var expected = FinishedGoodReceiptMath.AllocateValue(
                    prePool.BaseQty,
                    prePool.Value,
                    group.Select(x => (x.SourceId, x.BaseQty)).ToArray());
                foreach (var lineFact in group)
                {
                    if (!expected.TryGetValue(lineFact.SourceId, out var expectedValue)
                        || expectedValue != lineFact.TotalValue)
                        throw new TraceFailure($"Stored FG value for source {lineFact.SourceId} does not reproduce the original numeric SourceId allocation.");
                }

                var currentQty = prePool.BaseQty;
                var currentValue = prePool.Value;
                var currentAtoms = prePool.Atoms;
                IReadOnlyList<TraceAtom>? targetAtoms = null;
                foreach (var lineFact in group)
                {
                    var allocated = ProductionPooledCostTraceMath.Allocate(
                        currentQty,
                        currentValue,
                        lineFact.BaseQty,
                        lineFact.TotalValue,
                        currentAtoms.Select(ToMathAtom).ToArray());
                    var lineAtoms = allocated.Allocations
                        .Where(x => x.Amount > 0m)
                        .Select(x => currentAtoms.Single(atom => atom.Key == x.Key) with { Amount = x.Amount })
                        .ToArray();
                    if (lineFact.SourceId == sourceId)
                        targetAtoms = lineAtoms;

                    currentAtoms = SubtractTraceAtoms(currentAtoms, allocated.Allocations);
                    currentQty = IvQty.Round(currentQty - lineFact.BaseQty);
                    currentValue = StockLedgerPrecision.Money(currentValue - lineFact.TotalValue);
                }

                if (targetAtoms is null)
                    throw new TraceFailure("The selected FG source line was not present in the original posting group.");
                if (StockLedgerPrecision.Money(targetAtoms.Sum(x => x.Amount)) != fact.TotalValue)
                    throw new TraceFailure("The derived FG component vector does not reconcile to the frozen line value.");

                metadata.ValuationGeneration = evidence.Generation;
                var components = BuildComponents(targetAtoms);
                var details = targetAtoms.Select(x => x.Detail with { Amount = x.Amount }).ToArray();
                var status = components.Any(x => x.ComponentType == CostTraceComponentTypes.UnclassifiedVerified)
                    ? CostTraceStatuses.VerifiedWithUnclassified
                    : CostTraceStatuses.Verified;
                return IvMasterOperationResult<FinishedGoodCostTrace>.Ok(BuildTrace(
                    metadata, status, components, details, []));
            }
            catch (TraceFailure e)
            {
                metadata.Warnings.Add(e.Message);
                return IvMasterOperationResult<FinishedGoodCostTrace>.Ok(BuildInconsistentTrace(
                    receipt, source, pool, fact, movement, history, evidence, snapshot, metadata.Warnings,
                    metadata.PoolBaseQtyBeforePosting, metadata.PoolValueBeforePosting));
            }
        }
        catch (FgException e) { return Fail<FinishedGoodCostTrace>(e.Message, e.Code); }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException e) { return Fail<FinishedGoodCostTrace>(e.Message); }
    }

    private static void AddAuthorityWarnings(
        TraceMetadata metadata, InventoryTenantScope scope, StockPosting? posting)
    {
        var fact = metadata.Fact;
        var movement = metadata.Movement;
        var history = metadata.History;
        var evidence = metadata.Evidence;

        if (posting is null)
            metadata.Warnings.Add("The original StockPosting is missing.");
        else
        {
            if (posting.SealedAtUtc is null)
                metadata.Warnings.Add("The original StockPosting is not sealed.");
            if (posting.CompanyCode != scope.CompanyCode || posting.BranchCode != scope.BranchCode)
                metadata.Warnings.Add("The original StockPosting is outside the active company and branch scope.");
            if (posting.ReversesPostingId is not null)
                metadata.Warnings.Add("The original FG authority points to a reversal posting.");
        }

        if (movement is null)
            metadata.Warnings.Add("The original production FG movement is missing.");
        else
        {
            if (movement.StockPostingId != fact.StockPostingId
                || movement.ProductionBalLotId != metadata.Pool.Uid
                || movement.MovementType != ProductionBalLotMovementTypes.FgReceiptOut
                || movement.LedgerVersion != 2
                || movement.InventoryHistoryId != fact.InventoryHistoryId
                || movement.TotalCost != fact.TotalValue
                || IvQty.Round(movement.BaseQty) != IvQty.Round(fact.BaseQty))
                metadata.Warnings.Add("The original FG production movement does not match the frozen FG fact.");
        }

        if (IvQty.Round(metadata.Source.BaseQty) != IvQty.Round(fact.BaseQty))
            metadata.Warnings.Add("The original FG source quantity does not match the frozen FG fact.");

        if (history is null)
            metadata.Warnings.Add("The original inventory history is missing.");
        else if (history.StockPostingId != fact.StockPostingId
            || history.LedgerVersion != 2
            || history.ExactTransferredValue != fact.TotalValue
            || history.EvidenceBaseQty is null
            || IvQty.Round(history.EvidenceBaseQty.Value) != IvQty.Round(fact.BaseQty))
            metadata.Warnings.Add("The original inventory history does not match the frozen FG fact.");

        if (evidence is null)
            metadata.Warnings.Add("The original production valuation evidence is missing.");
        else if (evidence.MovementId != fact.ProductionMovementId
            || evidence.ProductionBalLotId != metadata.Pool.Uid
            || evidence.Status != ProductionPoolValuationService.Verified
            || evidence.Basis != "POOLED_AVERAGE"
            || evidence.Generation <= 0)
            metadata.Warnings.Add("The original FG valuation evidence is not verified pooled-average evidence.");
    }

    private static async Task<IReadOnlyList<string>> ValidateReversalAsync(
        AppDbContext db,
        ProductionFinishedGoodReceipt receipt,
        ProductionFinishedGoodFact originalFact,
        ProductionBalLotMovement? originalMovement,
        IvTrxHistory? originalHistory,
        CancellationToken ct)
    {
        if (receipt.Batch.BatchStatus != "REVERSED")
            return [];

        var warnings = new List<string>();
        if (receipt.ReversalPostingId is not long reversalPostingId)
        {
            warnings.Add("The reversed receipt has no reversal StockPosting.");
            return warnings;
        }

        var reversalPosting = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == reversalPostingId, ct);
        if (reversalPosting is null || reversalPosting.SealedAtUtc is null
            || reversalPosting.ReversesPostingId != receipt.PostingId)
            warnings.Add("The reversal StockPosting does not link to the original sealed posting.");

        var reversalFact = await db.ProductionFinishedGoodFactRows.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BatchId == receipt.BatchId
                && x.StockPostingId == reversalPostingId
                && x.ReversesFactId == originalFact.Id, ct);
        if (reversalFact is null
            || reversalFact.TotalValue != originalFact.TotalValue
            || IvQty.Round(reversalFact.BaseQty) != IvQty.Round(originalFact.BaseQty))
            warnings.Add("The reversal FG fact does not conserve the original quantity and value.");

        if (originalMovement is not null)
        {
            var reversalMovement = await db.ProductionBalLotMovements.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OriginalMovementId == originalMovement.Uid
                    && x.StockPostingId == reversalPostingId
                    && x.MovementType == ProductionBalLotMovementTypes.FgReceiptReversal, ct);
            if (reversalMovement is null
                || reversalMovement.TotalCost != originalMovement.TotalCost
                || IvQty.Round(reversalMovement.BaseQty) != IvQty.Round(originalMovement.BaseQty))
                warnings.Add("The reversal production movement does not link exactly to the original movement.");
        }

        if (originalHistory is not null)
        {
            var reversalHistory = await db.IvTrxHistories.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ReversesHistoryId == originalHistory.Id
                    && x.StockPostingId == reversalPostingId, ct);
            if (reversalHistory is null
                || reversalHistory.ExactTransferredValue != originalHistory.ExactTransferredValue
                || reversalHistory.EvidenceBaseQty != originalHistory.EvidenceBaseQty)
                warnings.Add("The reversal inventory history does not link exactly to the original history.");
        }

        return warnings;
    }

    private static void AddReversalWarnings(TraceMetadata metadata, IReadOnlyList<string> warnings)
    {
        foreach (var warning in warnings)
            metadata.Warnings.Add(warning);
    }

    private static FinishedGoodCostTrace BuildInconsistentTrace(
        ProductionFinishedGoodReceipt receipt,
        ProductionFinishedGoodSource source,
        ProductionBalLot? pool,
        ProductionFinishedGoodFact fact,
        ProductionBalLotMovement? movement,
        IvTrxHistory? history,
        ProductionValuationEvidence? evidence,
        ProductionFinishedGoodPriceSnapshot? snapshot,
        IReadOnlyList<string> warnings,
        decimal poolQty = 0m,
        decimal poolValue = 0m) => new()
        {
            ReceiptId = receipt.BatchId,
            BatchNo = receipt.Batch.BatchNo,
            SourceId = source.Id,
            ProductionBalLotId = source.ProductionBalLotId,
            DocumentStatus = receipt.Batch.BatchStatus,
            TraceStatus = CostTraceStatuses.Inconsistent,
            WorkOrderNo = pool?.WorkOrderNo ?? "",
            ItemCode = pool?.ItemCode ?? "",
            SourceLot = pool?.PhysicalLotNo ?? pool?.LotNo ?? "",
            SourceQty = source.RequestedQty,
            SourceUom = source.SourceUom,
            BaseQty = fact.BaseQty,
            DestinationQty = source.Detail.ToStdQty ?? 0m,
            DestinationUom = source.DestinationUom,
            ExactPostedValue = fact.TotalValue,
            EffectiveLineUnitCost = source.Detail.ToStdQty is > 0m
                ? StockLedgerPrecision.Money(fact.TotalValue / source.Detail.ToStdQty.Value)
                : 0m,
            DestinationPostedUnitPrice = snapshot?.PostedUnitPrice,
            ValuationGeneration = evidence?.Generation ?? 0,
            PoolBaseQtyBeforePosting = poolQty,
            PoolValueBeforePosting = poolValue,
            OriginalPostingId = fact.StockPostingId,
            ProductionMovementId = fact.ProductionMovementId,
            ReversalPostingId = receipt.ReversalPostingId,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray()
        };

    private static FinishedGoodCostTrace BuildTrace(
        TraceMetadata metadata,
        string status,
        IReadOnlyList<FinishedGoodCostTraceComponent> components,
        IReadOnlyList<FinishedGoodCostTraceDetail> details,
        IReadOnlyList<string> warnings) => new()
        {
            ReceiptId = metadata.Receipt.BatchId,
            BatchNo = metadata.Receipt.Batch.BatchNo,
            SourceId = metadata.Source.Id,
            ProductionBalLotId = metadata.Pool.Uid,
            DocumentStatus = metadata.Receipt.Batch.BatchStatus,
            TraceStatus = status,
            WorkOrderNo = metadata.WorkOrderNo,
            ItemCode = metadata.Pool.ItemCode,
            SourceLot = metadata.Pool.PhysicalLotNo ?? metadata.Pool.LotNo,
            SourceQty = metadata.Source.RequestedQty,
            SourceUom = metadata.Source.SourceUom,
            BaseQty = metadata.Fact.BaseQty,
            DestinationQty = metadata.Source.Detail.ToStdQty ?? 0m,
            DestinationUom = metadata.Source.DestinationUom,
            ExactPostedValue = metadata.Fact.TotalValue,
            EffectiveLineUnitCost = metadata.Source.Detail.ToStdQty is > 0m
                ? StockLedgerPrecision.Money(metadata.Fact.TotalValue / metadata.Source.Detail.ToStdQty.Value)
                : 0m,
            DestinationPostedUnitPrice = metadata.Snapshot?.PostedUnitPrice,
            ValuationGeneration = metadata.ValuationGeneration,
            PoolBaseQtyBeforePosting = metadata.PoolBaseQtyBeforePosting,
            PoolValueBeforePosting = metadata.PoolValueBeforePosting,
            OriginalPostingId = metadata.OriginalPostingId,
            ProductionMovementId = metadata.Fact.ProductionMovementId,
            ReversalPostingId = metadata.Receipt.ReversalPostingId,
            Components = components,
            Details = details,
            Warnings = metadata.Warnings.Concat(warnings).Distinct(StringComparer.Ordinal).ToArray()
        };

    private static IReadOnlyList<FinishedGoodCostTraceComponent> BuildComponents(
        IReadOnlyCollection<TraceAtom> atoms) => atoms
        .GroupBy(x => x.ComponentType, StringComparer.Ordinal)
        .Select(group => new FinishedGoodCostTraceComponent
        {
            ComponentType = group.Key,
            Label = CostTraceComponentTypes.Label(group.Key),
            Amount = StockLedgerPrecision.Money(group.Sum(x => x.Amount))
        })
        .OrderBy(x => CostTraceComponentTypes.Order(x.ComponentType))
        .ToArray();

    private static ProductionPooledCostTraceMath.ComponentAtom ToMathAtom(TraceAtom atom) =>
        new(atom.Key, atom.ComponentType, atom.Amount);

    private static IReadOnlyList<TraceAtom> SubtractTraceAtoms(
        IReadOnlyCollection<TraceAtom> atoms,
        IReadOnlyCollection<ProductionPooledCostTraceMath.ComponentAllocation> allocations)
    {
        var remaining = ProductionPooledCostTraceMath.Subtract(
            atoms.Select(ToMathAtom).ToArray(), allocations);
        var details = atoms.ToDictionary(x => x.Key, StringComparer.Ordinal);
        return remaining
            .Where(x => x.Amount > 0m)
            .Select(x => details[x.Key] with { Amount = x.Amount })
            .ToArray();
    }

    private sealed class TraceMetadata
    {
        public TraceMetadata(
            ProductionFinishedGoodReceipt receipt,
            ProductionFinishedGoodSource source,
            ProductionBalLot pool,
            ProductionFinishedGoodFact fact,
            string workOrderNo,
            long originalPostingId,
            ProductionBalLotMovement? movement,
            IvTrxHistory? history,
            ProductionValuationEvidence? evidence,
            ProductionFinishedGoodPriceSnapshot? snapshot)
        {
            Receipt = receipt;
            Source = source;
            Pool = pool;
            Fact = fact;
            WorkOrderNo = workOrderNo;
            OriginalPostingId = originalPostingId;
            Movement = movement;
            History = history;
            Evidence = evidence;
            Snapshot = snapshot;
        }

        public ProductionFinishedGoodReceipt Receipt { get; }
        public ProductionFinishedGoodSource Source { get; }
        public ProductionBalLot Pool { get; }
        public ProductionFinishedGoodFact Fact { get; }
        public string WorkOrderNo { get; }
        public long OriginalPostingId { get; }
        public ProductionBalLotMovement? Movement { get; }
        public IvTrxHistory? History { get; }
        public ProductionValuationEvidence? Evidence { get; }
        public ProductionFinishedGoodPriceSnapshot? Snapshot { get; }
        public int ValuationGeneration { get; set; }
        public decimal PoolBaseQtyBeforePosting { get; set; }
        public decimal PoolValueBeforePosting { get; set; }
        public List<string> Warnings { get; } = [];
    }

    private sealed record TraceAtom(
        string Key,
        string ComponentType,
        decimal Amount,
        FinishedGoodCostTraceDetail Detail);

    private sealed record PoolReplay(
        decimal BaseQty,
        decimal Value,
        IReadOnlyList<TraceAtom> Atoms);

    private sealed record MovementRow(
        ProductionBalLotMovement Movement,
        long PostingSequence);

    private sealed record FifoTraceRow(
        StockFifoLayerConsumption Consumption,
        StockFifoLayer Layer);

    private sealed record PoolState(
        decimal Qty,
        decimal Value,
        IReadOnlyList<TraceAtom> Atoms);

    private sealed class TraceFailure(string message) : Exception(message);

    private sealed class CostTraceReplayContext(
        AppDbContext db, InventoryTenantScope scope, int targetGeneration)
    {
        private readonly AppDbContext _db = db;
        private readonly InventoryTenantScope _scope = scope;
        private readonly int _targetGeneration = targetGeneration;
        private readonly IStockMovementRegistry _registry = new StockMovementRegistry();
        private readonly Dictionary<long, MovementRow> _movementCache = [];
        private readonly Dictionary<long, ProductionValuationEvidence?> _evidenceCache = [];
        private readonly Dictionary<long, IReadOnlyList<TraceAtom>> _vectorCache = [];
        private readonly HashSet<long> _resolving = [];
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings => _warnings;

        public async Task<PoolReplay> ReplayPoolAsync(long poolId, long beforePostingSequence, CancellationToken ct)
        {
            var rows = await (from movement in _db.ProductionBalLotMovements.AsNoTracking()
                              join posting in _db.StockPostings.AsNoTracking()
                                  on movement.StockPostingId equals posting.Id
                              where movement.CompanyCode == _scope.CompanyCode
                                  && movement.BranchCode == _scope.BranchCode
                                  && movement.ProductionBalLotId == poolId
                                  && posting.SealedAtUtc != null
                                  && posting.PostingSequence < beforePostingSequence
                              orderby posting.PostingSequence, movement.PostingLineNo, movement.SplitOrdinal, movement.Uid
                              select new MovementRow(movement, posting.PostingSequence))
                .ToListAsync(ct);

            decimal qty = 0m;
            decimal value = 0m;
            IReadOnlyList<TraceAtom> atoms = [];
            foreach (var group in rows.GroupBy(x => x.Movement.StockPostingId))
            {
                var groupRows = group.ToArray();
                if (groupRows.All(x => x.Movement.MovementType == ProductionBalLotMovementTypes.FgReceiptOut))
                {
                    var facts = await _db.ProductionFinishedGoodFactRows.AsNoTracking()
                        .Where(x => x.StockPostingId == group.Key && x.ReversesFactId == null)
                        .OrderBy(x => x.SourceId)
                        .ToListAsync(ct);
                    groupRows = groupRows
                        .OrderBy(x => facts.FirstOrDefault(f => f.ProductionMovementId == x.Movement.Uid)?.SourceId ?? long.MaxValue)
                        .ThenBy(x => x.Movement.Uid)
                        .ToArray();
                }

                foreach (var row in groupRows)
                {
                    var state = await ApplyMovementAsync(row, qty, value, atoms, ct);
                    qty = state.Qty;
                    value = state.Value;
                    atoms = state.Atoms;
                }
            }

            EnsureScalarState(qty, value, atoms);
            return new PoolReplay(qty, value, atoms);
        }

        private async Task<PoolState> ApplyMovementAsync(
            MovementRow row,
            decimal qty,
            decimal value,
            IReadOnlyList<TraceAtom> atoms,
            CancellationToken ct)
        {
            var movement = row.Movement;
            var evidence = await GetEvidenceAsync(movement.Uid, ct)
                ?? throw new TraceFailure($"Movement {movement.Uid} has no valuation evidence.");
            ValidateEvidence(movement, evidence);
            var direction = _registry.GetRequired(movement.MovementType).Direction;
            if (direction > 0)
            {
                var inbound = await BuildInboundAtomsAsync(movement, evidence, ct);
                if (StockLedgerPrecision.Money(inbound.Sum(x => x.Amount)) != movement.TotalCost)
                    throw new TraceFailure($"Inbound movement {movement.Uid} component value does not reconcile.");
                atoms = atoms.Concat(inbound).ToArray();
            }
            else if (direction < 0)
            {
                if (atoms.Count == 0 && movement.TotalCost > 0m)
                    throw new TraceFailure($"Outbound movement {movement.Uid} has no component pool evidence.");
                var allocation = ProductionPooledCostTraceMath.Allocate(
                    qty,
                    value,
                    movement.BaseQty,
                    movement.TotalCost,
                    atoms.Select(ToMathAtom).ToArray());
                _vectorCache[movement.Uid] = allocation.Allocations
                    .Where(x => x.Amount > 0m)
                    .Select(x => atoms.Single(atom => atom.Key == x.Key) with { Amount = x.Amount })
                    .ToArray();
                atoms = SubtractTraceAtoms(atoms, allocation.Allocations);
            }
            else
            {
                throw new TraceFailure($"Movement {movement.Uid} has no quantity direction.");
            }

            qty = IvQty.Round(qty + direction * movement.BaseQty);
            value = StockLedgerPrecision.Money(value + direction * movement.TotalCost);
            if (qty < 0m || value < 0m || (qty == 0m && value != 0m))
                throw new TraceFailure($"Production pool state became invalid at movement {movement.Uid}.");
            EnsureScalarState(qty, value, atoms);
            return new PoolState(qty, value, atoms);
        }

        private async Task<IReadOnlyList<TraceAtom>> BuildInboundAtomsAsync(
            ProductionBalLotMovement movement,
            ProductionValuationEvidence evidence,
            CancellationToken ct)
        {
            if (movement.OriginalMovementId is long originalMovementId)
            {
                var original = await ResolveMovementVectorAsync(originalMovementId, ct);
                return CloneAtoms(original, $"reversal:{movement.Uid}");
            }

            if (movement.MovementType == ProductionBalLotMovementTypes.Issue)
                return await BuildIssueAtomsAsync(movement, evidence, ct);
            if (movement.MovementType == ProductionBalLotMovementTypes.Produce)
                return await BuildProduceAtomsAsync(movement, evidence, ct);

            return
            [
                new TraceAtom(
                    $"movement:{movement.Uid}",
                    CostTraceComponentTypes.UnclassifiedVerified,
                    movement.TotalCost,
                    new FinishedGoodCostTraceDetail
                    {
                        ComponentType = CostTraceComponentTypes.UnclassifiedVerified,
                        SourceKind = "VERIFIED_EXTERNAL_OR_LEGACY",
                        SourceMovementId = movement.Uid,
                        SourceDocumentType = movement.DocumentType,
                        SourceDocumentNo = movement.DocumentNo,
                        ItemCode = movement.ItemCode ?? "",
                        Lot = movement.PhysicalLotNo ?? movement.LotIdentity,
                        Quantity = movement.BaseQty,
                        Uom = movement.BaseUom,
                        Rate = movement.BaseQty > 0m ? StockLedgerPrecision.Money(movement.TotalCost / movement.BaseQty) : 0m,
                        CostMethod = "VERIFIED_EXTERNAL",
                        ValuationSource = evidence.Basis,
                        Amount = movement.TotalCost
                    }
                )
            ];
        }

        private async Task<IReadOnlyList<TraceAtom>> BuildIssueAtomsAsync(
            ProductionBalLotMovement movement,
            ProductionValuationEvidence evidence,
            CancellationToken ct)
        {
            if (movement.StockPostingId is not long postingId || movement.InventoryHistoryId is not int historyId)
                throw new TraceFailure($"Production ISSUE movement {movement.Uid} has incomplete inventory authority.");
            if (!evidence.Basis.StartsWith("STOCK_VALUATION:", StringComparison.Ordinal))
                throw new TraceFailure($"Production ISSUE movement {movement.Uid} has an invalid valuation basis.");

            var facts = await _db.StockValuationFacts.AsNoTracking()
                .Where(x => x.StockPostingId == postingId
                    && x.CompanyCode == _scope.CompanyCode
                    && x.BranchCode == _scope.BranchCode
                    && x.InventoryHistoryId == historyId
                    && x.Direction < 0
                    && x.MovementCode == "PRODUCTION_MATERIAL_OUT"
                    && x.ValuationStatus == StockValuationStatuses.Valued)
                .OrderBy(x => x.PostingLineNo).ThenBy(x => x.SplitOrdinal).ThenBy(x => x.Id)
                .ToListAsync(ct);
            if (facts.Count == 0
                || IvQty.Round(facts.Sum(x => x.BaseQty)) != IvQty.Round(movement.BaseQty)
                || StockLedgerPrecision.Money(facts.Sum(x => x.CostAmount)) != movement.TotalCost)
                throw new TraceFailure($"Production ISSUE movement {movement.Uid} does not reconcile to StockValuationFact evidence.");

            var result = new List<TraceAtom>(facts.Count);
            foreach (var fact in facts)
            {
                var fifo = fact.CostMethod == StockCostMethods.Fifo
                    ? await (from consumption in _db.StockFifoLayerConsumptions.AsNoTracking()
                             join layer in _db.StockFifoLayers.AsNoTracking()
                                 on consumption.FifoLayerId equals layer.Id
                             where consumption.IssueValuationFactId == fact.Id
                                 && consumption.CompanyCode == _scope.CompanyCode
                                 && consumption.BranchCode == _scope.BranchCode
                                 && layer.CompanyCode == _scope.CompanyCode
                                 && layer.BranchCode == _scope.BranchCode
                                 && consumption.ReversesConsumptionId == null
                             orderby consumption.SplitOrdinal, consumption.Id
                             select new FifoTraceRow(consumption, layer)).ToListAsync(ct)
                    : [];

                if (fact.CostMethod == StockCostMethods.Fifo && fifo.Count > 0
                    && (IvQty.Round(fifo.Sum(x => x.Consumption.ConsumedQty)) != IvQty.Round(fact.BaseQty)
                        || StockLedgerPrecision.Money(fifo.Sum(x => x.Consumption.ConsumedValue))
                            != StockLedgerPrecision.Money(fact.CostAmount)))
                {
                    _warnings.Add($"FIFO provenance for valuation fact {fact.Id} does not reconcile; origin receipt details were withheld.");
                    fifo = [];
                }
                else if (fact.CostMethod == StockCostMethods.Fifo && fifo.Count == 0)
                {
                    _warnings.Add($"FIFO provenance for valuation fact {fact.Id} is missing; origin receipt details were withheld.");
                }

                if (fact.CostMethod == StockCostMethods.Fifo && fifo.Count > 0)
                {
                    foreach (var row in fifo)
                    {
                        result.Add(new TraceAtom(
                            $"valuation:{fact.Id}/fifo:{row.Consumption.Id}",
                            CostTraceComponentTypes.Material,
                            row.Consumption.ConsumedValue,
                            new FinishedGoodCostTraceDetail
                            {
                                ComponentType = CostTraceComponentTypes.Material,
                                SourceKind = "STOCK_VALUATION_FACT_FIFO",
                                SourceFactId = fact.Id,
                                SourceMovementId = movement.Uid,
                                SourceDocumentType = fact.SourceDocumentType,
                                SourceDocumentNo = fact.SourceDocumentNo,
                                OriginDocumentType = row.Layer.SourceDocumentType,
                                OriginDocumentNo = row.Layer.SourceDocumentNo,
                                ItemCode = fact.ItemCode,
                                Lot = row.Layer.LotNo ?? fact.LotNo,
                                Quantity = row.Consumption.ConsumedQty,
                                Uom = fact.BaseUom,
                                Rate = fact.UnitCost,
                                CostMethod = fact.CostMethod,
                                ValuationSource = fact.ValuationSource,
                                Amount = row.Consumption.ConsumedValue
                            }));
                    }
                }
                else
                {
                    result.Add(new TraceAtom(
                        $"valuation:{fact.Id}",
                        CostTraceComponentTypes.Material,
                        fact.CostAmount,
                        new FinishedGoodCostTraceDetail
                        {
                            ComponentType = CostTraceComponentTypes.Material,
                            SourceKind = "STOCK_VALUATION_FACT",
                            SourceFactId = fact.Id,
                            SourceMovementId = movement.Uid,
                            SourceDocumentType = fact.SourceDocumentType,
                            SourceDocumentNo = fact.SourceDocumentNo,
                            ItemCode = fact.ItemCode,
                            Lot = fact.LotNo,
                            Quantity = fact.BaseQty,
                            Uom = fact.BaseUom,
                            Rate = fact.UnitCost,
                            CostMethod = fact.CostMethod,
                            ValuationSource = fact.ValuationSource,
                            Amount = fact.CostAmount
                        }));
                }
            }
            return result;
        }

        private async Task<IReadOnlyList<TraceAtom>> BuildProduceAtomsAsync(
            ProductionBalLotMovement movement,
            ProductionValuationEvidence evidence,
            CancellationToken ct)
        {
            var basis = evidence.Basis;
            var knownBasis = basis is "CONSUMED_INPUTS" or "ABSORBED_CONVERSION" or "CONSUMED_INPUTS+ABSORBED_CONVERSION";
            if (!knownBasis)
            {
                return
                [
                    new TraceAtom(
                        $"movement:{movement.Uid}",
                        CostTraceComponentTypes.UnclassifiedVerified,
                        movement.TotalCost,
                        new FinishedGoodCostTraceDetail
                        {
                            ComponentType = CostTraceComponentTypes.UnclassifiedVerified,
                            SourceKind = "VERIFIED_EXTERNAL_OR_LEGACY",
                            SourceMovementId = movement.Uid,
                            SourceDocumentType = movement.DocumentType,
                            SourceDocumentNo = movement.DocumentNo,
                            ItemCode = movement.ItemCode ?? "",
                            Lot = movement.PhysicalLotNo ?? movement.LotIdentity,
                            Quantity = movement.BaseQty,
                            Uom = movement.BaseUom,
                            Rate = movement.BaseQty > 0m ? StockLedgerPrecision.Money(movement.TotalCost / movement.BaseQty) : 0m,
                            CostMethod = "VERIFIED_EXTERNAL",
                            ValuationSource = basis,
                            Amount = movement.TotalCost
                        }
                    )
                ];
            }

            var edges = await _db.ProductionPoolDependencyRows.AsNoTracking()
                .Where(x => x.ConsumerMovementId == movement.Uid && x.ReversesDependencyId == null)
                .OrderBy(x => x.Id)
                .ToListAsync(ct);
            var inputAtoms = new List<TraceAtom>();
            var producer = await GetMovementAsync(movement.Uid, ct)
                ?? throw new TraceFailure($"Production PRODUCE movement {movement.Uid} is missing its sealed posting.");
            foreach (var edge in edges)
            {
                if (edge.StockPostingId != movement.StockPostingId)
                    throw new TraceFailure($"Production dependency {edge.Id} does not belong to the PRODUCE posting.");

                var contributor = await GetMovementAsync(edge.ContributorMovementId, ct)
                    ?? throw new TraceFailure($"Production dependency {edge.Id} contributor is missing.");
                if (contributor.Movement.MovementType != ProductionBalLotMovementTypes.Consume
                    || contributor.Movement.StockPostingId != movement.StockPostingId
                    || contributor.Movement.ProductionOutputId != movement.ProductionOutputId
                    || contributor.Movement.PostingLinkId != movement.PostingLinkId
                    || contributor.PostingSequence > producer.PostingSequence)
                    throw new TraceFailure($"Production dependency {edge.Id} contributor does not match the PRODUCE posting lineage.");

                var vector = await ResolveMovementVectorAsync(edge.ContributorMovementId, ct);
                inputAtoms.AddRange(CloneAtoms(vector, $"produce:{movement.Uid}/input:{edge.ContributorMovementId}"));
            }

            var conversionFacts = await _db.ProductionConversionCostFacts.AsNoTracking()
                .Where(x => x.ProductionMovementId == movement.Uid
                    && x.CompanyCode == _scope.CompanyCode
                    && x.BranchCode == _scope.BranchCode
                    && x.StockPostingId == movement.StockPostingId
                    && x.ReversesFactId == null)
                .OrderBy(x => x.Id)
                .ToListAsync(ct);
            var hasInputs = inputAtoms.Count > 0;
            var hasConversion = conversionFacts.Count > 0;
            if ((basis == "CONSUMED_INPUTS" && !hasInputs)
                || (basis == "ABSORBED_CONVERSION" && !hasConversion)
                || (basis == "CONSUMED_INPUTS+ABSORBED_CONVERSION" && (!hasInputs || !hasConversion)))
                throw new TraceFailure($"Verified PRODUCE movement {movement.Uid} is missing evidence required by basis {basis}.");

            var result = new List<TraceAtom>(inputAtoms);
            foreach (var fact in conversionFacts)
            {
                if (fact.CostAmount < 0m || fact.ProductionOutputId != movement.ProductionOutputId)
                    throw new TraceFailure($"Conversion fact {fact.Id} does not match the PRODUCE movement.");
                var component = fact.CostType switch
                {
                    ProductionConversionCostTypes.Labour => CostTraceComponentTypes.Labour,
                    ProductionConversionCostTypes.Machine => CostTraceComponentTypes.Machine,
                    ProductionConversionCostTypes.UtilitiesOverhead => CostTraceComponentTypes.UtilitiesOverhead,
                    ProductionConversionCostTypes.Other => CostTraceComponentTypes.Other,
                    _ => throw new TraceFailure($"Conversion fact {fact.Id} has an unknown cost type.")
                };
                result.Add(new TraceAtom(
                    $"conversion:{fact.Id}",
                    component,
                    fact.CostAmount,
                    new FinishedGoodCostTraceDetail
                    {
                        ComponentType = component,
                        SourceKind = "PRODUCTION_CONVERSION_COST_FACT",
                        SourceFactId = fact.Id,
                        SourceMovementId = movement.Uid,
                        SourceDocumentType = ProductionDocumentTypes.ProductionOutput,
                        SourceDocumentNo = movement.DocumentNo,
                        ItemCode = movement.ItemCode ?? "",
                        Quantity = fact.BasisQty,
                        Uom = fact.BasisUom,
                        Rate = fact.RatePerOutputUnit,
                        CostMethod = "FROZEN_CONVERSION_RATE",
                        ValuationSource = "PRODUCTION_CONVERSION_COST_FACT",
                        Amount = fact.CostAmount
                    }));
            }

            if (StockLedgerPrecision.Money(result.Sum(x => x.Amount)) != movement.TotalCost)
                throw new TraceFailure($"Verified PRODUCE movement {movement.Uid} does not reconcile consumed inputs plus conversion facts.");
            return result;
        }

        private async Task<IReadOnlyList<TraceAtom>> ResolveMovementVectorAsync(long movementId, CancellationToken ct)
        {
            if (_vectorCache.TryGetValue(movementId, out var cached))
                return cached;
            if (!_resolving.Add(movementId))
                throw new TraceFailure($"Production valuation dependency cycle detected at movement {movementId}.");

            try
            {
                var row = await GetMovementAsync(movementId, ct)
                    ?? throw new TraceFailure($"Production movement {movementId} is missing.");
                var evidence = await GetEvidenceAsync(movementId, ct)
                    ?? throw new TraceFailure($"Production movement {movementId} has no valuation evidence.");
                ValidateEvidence(row.Movement, evidence);
                var direction = _registry.GetRequired(row.Movement.MovementType).Direction;
                if (direction > 0)
                {
                    var inbound = await BuildInboundAtomsAsync(row.Movement, evidence, ct);
                    if (StockLedgerPrecision.Money(inbound.Sum(x => x.Amount)) != row.Movement.TotalCost)
                        throw new TraceFailure($"Movement {movementId} component value does not reconcile.");
                    _vectorCache[movementId] = inbound;
                    return inbound;
                }

                var replay = await ReplayPoolAsync(row.Movement.ProductionBalLotId, row.PostingSequence, ct);
                var allocation = ProductionPooledCostTraceMath.Allocate(
                    replay.BaseQty,
                    replay.Value,
                    row.Movement.BaseQty,
                    row.Movement.TotalCost,
                    replay.Atoms.Select(ToMathAtom).ToArray());
                var vector = allocation.Allocations
                    .Where(x => x.Amount > 0m)
                    .Select(x => replay.Atoms.Single(atom => atom.Key == x.Key) with { Amount = x.Amount })
                    .ToArray();
                _vectorCache[movementId] = vector;
                return vector;
            }
            finally
            {
                _resolving.Remove(movementId);
            }
        }

        private async Task<MovementRow?> GetMovementAsync(long movementId, CancellationToken ct)
        {
            if (_movementCache.TryGetValue(movementId, out var cached))
                return cached;
            var row = await (from movement in _db.ProductionBalLotMovements.AsNoTracking()
                             join posting in _db.StockPostings.AsNoTracking()
                                 on movement.StockPostingId equals posting.Id
                              where movement.Uid == movementId
                                 && movement.CompanyCode == _scope.CompanyCode
                                 && movement.BranchCode == _scope.BranchCode
                                  && posting.SealedAtUtc != null
                             select new MovementRow(movement, posting.PostingSequence))
                .SingleOrDefaultAsync(ct);
            if (row is not null)
                _movementCache[movementId] = row;
            return row;
        }

        private async Task<ProductionValuationEvidence?> GetEvidenceAsync(long movementId, CancellationToken ct)
        {
            if (_evidenceCache.TryGetValue(movementId, out var cached))
                return cached;
            var evidence = await _db.ProductionValuationEvidenceRows.AsNoTracking()
                .SingleOrDefaultAsync(x => x.MovementId == movementId, ct);
            _evidenceCache[movementId] = evidence;
            return evidence;
        }

        private void ValidateEvidence(
            ProductionBalLotMovement movement, ProductionValuationEvidence evidence)
        {
            if (movement.LedgerVersion != 2
                || evidence.MovementId != movement.Uid
                || evidence.ProductionBalLotId != movement.ProductionBalLotId
                || evidence.Status != ProductionPoolValuationService.Verified
                || movement.ValuationStatus != ProductionPoolValuationService.Verified
                || evidence.Generation <= 0
                || evidence.Generation != _targetGeneration)
                throw new TraceFailure($"Movement {movement.Uid} has incomplete or unverified production evidence.");
        }

        private static void EnsureScalarState(
            decimal qty, decimal value, IReadOnlyCollection<TraceAtom> atoms)
        {
            if (qty < 0m || value < 0m || (qty == 0m && value != 0m))
                throw new TraceFailure("Production pool quantity/value state became negative or stranded.");
            if (StockLedgerPrecision.Money(atoms.Sum(x => x.Amount)) != StockLedgerPrecision.Money(value))
                throw new TraceFailure("Production component pool does not reconcile to scalar pool value.");
        }

        private static IReadOnlyList<TraceAtom> CloneAtoms(
            IReadOnlyCollection<TraceAtom> atoms, string prefix) => atoms
            .Select(x => x with { Key = $"{prefix}/{x.Key}" })
            .ToArray();
    }

    private static class CostTraceStatuses
    {
        public const string Verified = "VERIFIED";
        public const string VerifiedWithUnclassified = "VERIFIED_WITH_UNCLASSIFIED";
        public const string Inconsistent = "INCONSISTENT";
    }

    private static class CostTraceComponentTypes
    {
        public const string Material = "MATERIAL";
        public const string Labour = "LABOUR";
        public const string Machine = "MACHINE";
        public const string UtilitiesOverhead = "UTILITIES_OVERHEAD";
        public const string Other = "OTHER";
        public const string UnclassifiedVerified = "UNCLASSIFIED_VERIFIED";

        public static string Label(string componentType) => componentType switch
        {
            Material => "Material",
            Labour => "Labour",
            Machine => "Machine",
            UtilitiesOverhead => "Utilities / Overhead",
            Other => "Other",
            UnclassifiedVerified => "Verified Unclassified",
            _ => componentType
        };

        public static int Order(string componentType) => componentType switch
        {
            Material => 1,
            Labour => 2,
            Machine => 3,
            UtilitiesOverhead => 4,
            Other => 5,
            UnclassifiedVerified => 6,
            _ => 99
        };
    }
}
