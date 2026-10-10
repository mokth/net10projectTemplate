using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Traceability;

/// <summary>
/// Read-only genealogy projection over immutable posting evidence. This service intentionally depends
/// only on the database context factory and inquiry authorization; it has no posting or costing writer.
/// </summary>
public sealed class LotGenealogyService : ILotGenealogyService
{
    private const int MaxDepth = 25;
    private const int MaxNodes = 2_000;
    private const int BatchSize = 300;
    private const int MaxSearchResults = 100;

    private static readonly HashSet<string> KnownMenus = new(StringComparer.OrdinalIgnoreCase)
    {
        MenuCodes.InventoryLotTrace
    };

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;

    public LotGenealogyService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
    }

    public async Task<IvMasterOperationResult<LotGenealogySearchPage>> SearchAsync(
        LotGenealogySearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveAsync(cancellationToken);
        if (!scope.Succeeded)
            return IvMasterOperationResult<LotGenealogySearchPage>.Fail(scope.ErrorCode, scope.Error!);

        var term = (query?.SearchText ?? string.Empty).Trim();
        if (term.Length < 2)
            return IvMasterOperationResult<LotGenealogySearchPage>.Fail(
                IvMasterErrorCode.Validation, "Enter at least two characters to search.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = scope.CompanyCode!;
        var branch = scope.BranchCode!;
        var take = Math.Clamp(query?.Take ?? 50, 1, MaxSearchResults);
        var candidates = new Dictionary<(LotGenealogyRootKind Kind, long Id), LotGenealogySearchCandidate>();

        var visibleLots = db.IvLots.AsNoTracking()
            .Where(l => l.CompanyCode == company
                && (db.IvBalLocs.Any(b => b.CompanyCode == company && b.BranchCode == branch && b.LotId == l.Id)
                    || db.IvTrxHistories.Any(h => h.CompanyCode == company && h.BranchCode == branch
                        && (h.FromLotId == l.Id || h.ToLotId == l.Id))));

        var lotCandidates = await (from lot in visibleLots
            join item in db.IvStockMasters.AsNoTracking()
                on new { lot.CompanyCode, lot.ICode } equals new { item.CompanyCode, item.ICode } into items
            from item in items.DefaultIfEmpty()
            where lot.LotNo.Contains(term)
                || lot.ICode.Contains(term)
                || (item != null && item.IDesc != null && item.IDesc.Contains(term))
                || (lot.SupplierCode != null && lot.SupplierCode.Contains(term))
                || (lot.SourceDocNo != null && lot.SourceDocNo.Contains(term))
            orderby lot.ICode, lot.LotNo
            select new LotGenealogySearchCandidate
            {
                RootKind = LotGenealogyRootKind.InventoryLot,
                RootId = lot.Id,
                Title = lot.ICode + " / " + lot.LotNo,
                ItemCode = lot.ICode,
                LotNo = lot.LotNo,
                BranchCode = branch,
                Detail = ((item == null ? null : item.IDesc) ?? "Inventory lot")
                    + (lot.SupplierCode == null ? string.Empty : " · Supplier " + lot.SupplierCode)
            }).Take(MaxSearchResults).ToListAsync(cancellationToken);
        AddCandidates(candidates, lotCandidates);

        var productionLots = await db.ProductionBalLots.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && (x.WorkOrderNo.Contains(term) || x.ItemCode.Contains(term)
                    || x.LotNo.Contains(term)
                    || (x.PhysicalLotNo != null && x.PhysicalLotNo.Contains(term))
                    || (x.Description != null && x.Description.Contains(term))))
            .OrderBy(x => x.ItemCode).ThenBy(x => x.LotNo).ThenBy(x => x.Uid)
            .Take(MaxSearchResults)
            .Select(x => new LotGenealogySearchCandidate
            {
                RootKind = LotGenealogyRootKind.ProductionLot,
                RootId = x.Uid,
                Title = x.ItemCode + " / " + (x.PhysicalLotNo ?? x.LotNo),
                ItemCode = x.ItemCode,
                LotNo = x.PhysicalLotNo ?? x.LotNo,
                WorkOrderNo = x.WorkOrderNo,
                BranchCode = x.BranchCode,
                Detail = x.Kind + " · WO " + x.WorkOrderNo + (x.Description == null ? string.Empty : " · " + x.Description)
            }).ToListAsync(cancellationToken);
        AddCandidates(candidates, productionLots);

        var workOrders = await db.ProductionWorkOrders.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && (x.WorkOrderNo.Contains(term) || x.ProductCode.Contains(term)
                    || (x.ProductDescription != null && x.ProductDescription.Contains(term))))
            .OrderBy(x => x.WorkOrderNo).Take(MaxSearchResults)
            .Select(x => new LotGenealogySearchCandidate
            {
                RootKind = LotGenealogyRootKind.WorkOrder,
                RootId = x.Uid,
                Title = x.WorkOrderNo,
                ItemCode = x.ProductCode,
                WorkOrderNo = x.WorkOrderNo,
                BranchCode = x.BranchCode,
                Detail = x.ProductDescription ?? "Production Work Order"
            }).ToListAsync(cancellationToken);
        AddCandidates(candidates, workOrders);

        // PO and supplier searches resolve through posted history's exact PO reference to the
        // inventory lot. LotNo is never used to infer this relationship.
        var purchaseOrderNos = await db.PoOrders.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && (x.PoNo.Contains(term)
                    || (x.VendCode != null && x.VendCode.Contains(term))
                    || (x.VendName != null && x.VendName.Contains(term))))
            .Select(x => x.PoNo).Distinct().Take(MaxSearchResults).ToListAsync(cancellationToken);
        var purchaseLotIds = await db.IvTrxHistories.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.ToLotId != null && x.PoNo != null
                && (purchaseOrderNos.Contains(x.PoNo) || x.PoNo.Contains(term) || (x.RefNo != null && x.RefNo.Contains(term))))
            .Select(x => x.ToLotId!.Value).Distinct().Take(MaxSearchResults).ToListAsync(cancellationToken);
        AddCandidates(candidates, await LoadInventoryLotCandidatesAsync(
            db, company, branch, purchaseLotIds, cancellationToken));

        // DO/customer searches resolve only through exact posted SP rows. SaDoDetail is not a lot source.
        var matchingCustomerCodes = await db.SaCusts.AsNoTracking()
            .Where(x => x.CompanyCode == company
                && (x.CustCode.Contains(term) || x.CustName.Contains(term)))
            .Select(x => x.CustCode).Distinct().Take(MaxSearchResults).ToListAsync(cancellationToken);
        var deliveryNos = await db.SaDos.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && (x.DoNo.Contains(term) || x.CustCode.Contains(term)
                    || (x.CustName != null && x.CustName.Contains(term))
                    || matchingCustomerCodes.Contains(x.CustCode)))
            .Select(x => x.DoNo).Distinct().Take(MaxSearchResults).ToListAsync(cancellationToken);
        var shipmentLotIds = await db.IvTrxHistories.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.TrxType == IvTrxTypes.SalesOut && x.FromLotId != null && x.DoNo != null
                && deliveryNos.Contains(x.DoNo))
            .Select(x => x.FromLotId!.Value).Distinct().Take(MaxSearchResults).ToListAsync(cancellationToken);
        AddCandidates(candidates, await LoadInventoryLotCandidatesAsync(
            db, company, branch, shipmentLotIds, cancellationToken));

        var ordered = candidates.Values
            .OrderBy(x => x.RootKind)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.RootId)
            .ToList();

        return IvMasterOperationResult<LotGenealogySearchPage>.Ok(new LotGenealogySearchPage
        {
            Candidates = ordered.Take(take).ToArray(),
            TotalCount = ordered.Count
        });
    }

    public async Task<IvMasterOperationResult<LotGenealogyResult>> TraceInventoryLotAsync(
        int lotId,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveAsync(cancellationToken);
        if (!scope.Succeeded)
            return IvMasterOperationResult<LotGenealogyResult>.Fail(scope.ErrorCode, scope.Error!);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = scope.CompanyCode!;
        var branch = scope.BranchCode!;
        var lot = await db.IvLots.AsNoTracking()
            .Where(x => x.Id == lotId && x.CompanyCode == company
                && (db.IvBalLocs.Any(b => b.CompanyCode == company && b.BranchCode == branch && b.LotId == x.Id)
                    || db.IvTrxHistories.Any(h => h.CompanyCode == company && h.BranchCode == branch
                        && (h.FromLotId == x.Id || h.ToLotId == x.Id))))
            .FirstOrDefaultAsync(cancellationToken);
        if (lot is null)
            return IvMasterOperationResult<LotGenealogyResult>.Fail(IvMasterErrorCode.NotFound, "Inventory lot was not found in the visible branch evidence.");

        var item = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.ICode == lot.ICode)
            .Select(x => new { x.IDesc, x.StdUom })
            .FirstOrDefaultAsync(cancellationToken);
        var origin = await db.ProductionFinishedGoodLotOriginRows.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.LotId == lotId && x.OriginatingBranch == branch)
            .FirstOrDefaultAsync(cancellationToken);
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && (x.FromLotId == lotId || x.ToLotId == lotId))
            .OrderBy(x => x.TrxDtTime).ThenBy(x => x.Id)
            .Take(MaxNodes + 1).ToListAsync(cancellationToken);
        var isFinishedGood = origin is not null || histories.Any(x => x.TrxType == IvTrxTypes.FinishedGoods);
        var root = InventoryLotNode(lot, item?.IDesc, item?.StdUom, branch, isFinishedGood);
        var graph = new TraceGraph(root, branch);
        if (histories.Count > MaxNodes)
        {
            histories.RemoveAt(histories.Count - 1);
            graph.MarkTruncated("Inventory movement history exceeded the trace node limit.");
        }

        var visibleOnHand = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.LotId == lotId)
            .SumAsync(x => (decimal?)x.StdQty, cancellationToken) ?? 0m;

        await AddLotOriginSnapshotAsync(db, company, branch, lot, origin, graph, cancellationToken);
        await AddInventoryHistoryAsync(db, company, branch, lot, histories, graph, cancellationToken);
        await AddMaterialIssueBridgeForLotAsync(db, company, branch, lotId, null, graph, cancellationToken);
        await TraceProductionMovementsAsync(db, company, branch,
            await GetFinishedGoodMovementSeedsAsync(db, company, branch, histories, graph, cancellationToken),
            LotGenealogyDirection.Backward, graph, cancellationToken);

        var issueSeeds = await GetMaterialIssueSeedsAsync(db, company, branch, lotId, graph, cancellationToken);
        await TraceProductionMovementsAsync(db, company, branch, issueSeeds,
            LotGenealogyDirection.Forward, graph, cancellationToken);

        await AddFinishedGoodOutputsForMovementIdsAsync(db, company, branch,
            graph.ProductionMovementIds.ToArray(), graph, cancellationToken);
        await AddShipmentCustomersAsync(db, company, branch, graph.FinishedGoodLotIds.ToArray(), graph, cancellationToken);
        await AddCostEvidenceAsync(db, company, branch, graph, cancellationToken);
        return IvMasterOperationResult<LotGenealogyResult>.Ok(graph.Build(visibleOnHand));
    }

    public async Task<IvMasterOperationResult<LotGenealogyResult>> TraceProductionLotAsync(
        long productionBalLotId,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveAsync(cancellationToken);
        if (!scope.Succeeded)
            return IvMasterOperationResult<LotGenealogyResult>.Fail(scope.ErrorCode, scope.Error!);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = scope.CompanyCode!;
        var branch = scope.BranchCode!;
        var lot = await db.ProductionBalLots.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Uid == productionBalLotId
                && x.CompanyCode == company && x.BranchCode == branch, cancellationToken);
        if (lot is null)
            return IvMasterOperationResult<LotGenealogyResult>.Fail(IvMasterErrorCode.NotFound, "Production balance lot was not found in the visible branch.");

        var root = ProductionLotNode(lot, branch);
        var graph = new TraceGraph(root, branch);
        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(x => x.ProductionBalLotId == lot.Uid)
            .OrderBy(x => x.MovementDate).ThenBy(x => x.Uid)
            .Take(MaxNodes + 1).ToListAsync(cancellationToken);
        if (movements.Count > MaxNodes)
        {
            movements.RemoveAt(movements.Count - 1);
            graph.MarkTruncated("Production movement history exceeded the trace node limit.");
        }

        var movementStates = await GetProductionMovementStatesAsync(db, company, branch,
            movements.Select(x => x.Uid).ToArray(), cancellationToken);
        var supportedReceipts = movements.Where(x => IsProductionReceiptMovement(x.MovementType)).ToArray();
        foreach (var movement in supportedReceipts)
        {
            var state = movementStates.GetValueOrDefault(movement.Uid, EvidenceState.Missing);
            graph.AddNode(ProductionMovementNode(movement, lot, state, branch));
            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = root.Id,
                ToNodeId = ProductionMovementNodeId(movement.Uid),
                Relationship = "Production balance movement",
                EvidenceState = state.Label,
                Explanation = "Movement belongs to this exact ProductionBalLot ID.",
                ExactBaseQty = movement.BaseQty,
                BaseUom = movement.BaseUom,
                EventDate = movement.MovementDate,
                IsActiveImpact = state.IsActive
            }, LotGenealogyDirection.Backward);
            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = root.Id,
                ToNodeId = ProductionMovementNodeId(movement.Uid),
                Relationship = "Production balance movement",
                EvidenceState = state.Label,
                Explanation = "Movement belongs to this exact ProductionBalLot ID.",
                ExactBaseQty = movement.BaseQty,
                BaseUom = movement.BaseUom,
                EventDate = movement.MovementDate,
                IsActiveImpact = state.IsActive
            }, LotGenealogyDirection.Forward);
            if (state.IsIncomplete)
                graph.Warn($"Production movement {movement.Uid} has no sealed, complete posting evidence.");
        }

        if (movements.Count == 0)
            graph.Warn("No persisted production movement rows are available for this balance lot.");

        var seeds = supportedReceipts.Where(x => movementStates.GetValueOrDefault(x.Uid).IsActive)
            .Select(x => x.Uid).Take(MaxNodes).ToArray();
        await AddMaterialIssueBridgesForBalanceLotsAsync(db, company, branch, [lot], graph, cancellationToken);
        await TraceProductionMovementsAsync(db, company, branch, seeds,
            LotGenealogyDirection.Backward, graph, cancellationToken);
        await TraceProductionMovementsAsync(db, company, branch, seeds,
            LotGenealogyDirection.Forward, graph, cancellationToken);
        await AddFinishedGoodOutputsForMovementIdsAsync(db, company, branch,
            graph.ProductionMovementIds.ToArray(), graph, cancellationToken);
        await AddShipmentCustomersAsync(db, company, branch, graph.FinishedGoodLotIds.ToArray(), graph, cancellationToken);
        await AddCostEvidenceAsync(db, company, branch, graph, cancellationToken);
        return IvMasterOperationResult<LotGenealogyResult>.Ok(graph.Build(lot.BaseQty));
    }

    public async Task<IvMasterOperationResult<LotGenealogyResult>> TraceWorkOrderAsync(
        long workOrderId,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveAsync(cancellationToken);
        if (!scope.Succeeded)
            return IvMasterOperationResult<LotGenealogyResult>.Fail(scope.ErrorCode, scope.Error!);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = scope.CompanyCode!;
        var branch = scope.BranchCode!;
        var order = await db.ProductionWorkOrders.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Uid == workOrderId
                && x.CompanyCode == company && x.BranchCode == branch, cancellationToken);
        if (order is null)
            return IvMasterOperationResult<LotGenealogyResult>.Fail(IvMasterErrorCode.NotFound, "Work Order was not found in the visible branch.");

        var root = new LotGenealogyNode
        {
            Id = WorkOrderNodeId(order.Uid),
            Kind = "Work Order",
            Title = order.WorkOrderNo,
            Detail = order.ProductCode + (order.ProductDescription == null ? string.Empty : " · " + order.ProductDescription),
            BranchCode = branch,
            EvidenceState = order.Status
        };
        var graph = new TraceGraph(root, branch);
        var movements = await (from movement in db.ProductionBalLotMovements.AsNoTracking()
            join lot in db.ProductionBalLots.AsNoTracking() on movement.ProductionBalLotId equals lot.Uid
            where movement.WorkOrderId == workOrderId
                && lot.CompanyCode == company && lot.BranchCode == branch
            orderby movement.MovementDate, movement.Uid
            select movement).Take(MaxNodes + 1).ToListAsync(cancellationToken);
        if (movements.Count > MaxNodes)
        {
            movements.RemoveAt(movements.Count - 1);
            graph.MarkTruncated("Work Order movement set exceeded the trace node limit.");
        }

        var states = await GetProductionMovementStatesAsync(db, company, branch,
            movements.Select(x => x.Uid).ToArray(), cancellationToken);
        await EnsureProductionMovementNodesAsync(db, company, branch,
            movements.Select(x => x.Uid).ToArray(), graph, cancellationToken);
        foreach (var movement in movements)
        {
            var state = states.GetValueOrDefault(movement.Uid, EvidenceState.Missing);
            var nodeId = ProductionMovementNodeId(movement.Uid);
            if (!graph.HasNode(nodeId))
            {
                graph.Warn($"Production movement {movement.Uid} could not be resolved in the current branch.");
                continue;
            }
            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = root.Id,
                ToNodeId = nodeId,
                Relationship = "Work Order movement",
                EvidenceState = state.Label,
                Explanation = "Movement carries this exact WorkOrderId.",
                ExactBaseQty = movement.BaseQty,
                BaseUom = movement.BaseUom,
                EventDate = movement.MovementDate,
                IsActiveImpact = state.IsActive
            }, LotGenealogyDirection.Backward);
            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = root.Id,
                ToNodeId = nodeId,
                Relationship = "Work Order movement",
                EvidenceState = state.Label,
                Explanation = "Movement carries this exact WorkOrderId.",
                ExactBaseQty = movement.BaseQty,
                BaseUom = movement.BaseUom,
                EventDate = movement.MovementDate,
                IsActiveImpact = state.IsActive
            }, LotGenealogyDirection.Forward);
            if (state.IsIncomplete)
                graph.Warn($"Work Order movement {movement.Uid} has no sealed active posting evidence.");
        }

        var backwardSeeds = movements.Where(x => x.MovementType == ProductionBalLotMovementTypes.Produce)
            .Where(x => states.GetValueOrDefault(x.Uid).IsActive).Select(x => x.Uid).Take(MaxNodes).ToArray();
        var forwardSeeds = movements.Where(x => IsProductionReceiptMovement(x.MovementType))
            .Where(x => states.GetValueOrDefault(x.Uid).IsActive).Select(x => x.Uid).Take(MaxNodes).ToArray();
        await TraceProductionMovementsAsync(db, company, branch, backwardSeeds,
            LotGenealogyDirection.Backward, graph, cancellationToken);
        await TraceProductionMovementsAsync(db, company, branch, forwardSeeds,
            LotGenealogyDirection.Forward, graph, cancellationToken);
        await AddFinishedGoodOutputsForMovementIdsAsync(db, company, branch,
            graph.ProductionMovementIds.ToArray(), graph, cancellationToken);
        await AddShipmentCustomersAsync(db, company, branch, graph.FinishedGoodLotIds.ToArray(), graph, cancellationToken);
        await AddCostEvidenceAsync(db, company, branch, graph, cancellationToken);
        return IvMasterOperationResult<LotGenealogyResult>.Ok(graph.Build(null));
    }

    private async Task<IvInquiryScopeContext> ResolveAsync(CancellationToken cancellationToken) =>
        await IvInquiryScopeResolver.ResolveAsync(
            _tenant, _accessRights, MenuCodes.InventoryLotTrace, KnownMenus, cancellationToken);

    private static void AddCandidates(
        IDictionary<(LotGenealogyRootKind Kind, long Id), LotGenealogySearchCandidate> target,
        IEnumerable<LotGenealogySearchCandidate> candidates)
    {
        foreach (var candidate in candidates)
            target.TryAdd((candidate.RootKind, candidate.RootId), candidate);
    }

    private static async Task<IReadOnlyList<LotGenealogySearchCandidate>> LoadInventoryLotCandidatesAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<int> lotIds,
        CancellationToken cancellationToken)
    {
        if (lotIds.Count == 0)
            return [];

        return await (from lot in db.IvLots.AsNoTracking()
            join item in db.IvStockMasters.AsNoTracking()
                on new { lot.CompanyCode, lot.ICode } equals new { item.CompanyCode, item.ICode } into items
            from item in items.DefaultIfEmpty()
            where lot.CompanyCode == company && lotIds.Contains(lot.Id)
                && (db.IvBalLocs.Any(b => b.CompanyCode == company && b.BranchCode == branch && b.LotId == lot.Id)
                    || db.IvTrxHistories.Any(h => h.CompanyCode == company && h.BranchCode == branch
                        && (h.FromLotId == lot.Id || h.ToLotId == lot.Id)))
            orderby lot.ICode, lot.LotNo
            select new LotGenealogySearchCandidate
            {
                RootKind = LotGenealogyRootKind.InventoryLot,
                RootId = lot.Id,
                Title = lot.ICode + " / " + lot.LotNo,
                ItemCode = lot.ICode,
                LotNo = lot.LotNo,
                BranchCode = branch,
                Detail = (item == null ? null : item.IDesc) ?? "Inventory lot"
            }).Take(MaxSearchResults).ToListAsync(cancellationToken);
    }

    private async Task AddLotOriginSnapshotAsync(
        AppDbContext db,
        string company,
        string branch,
        IvLot lot,
        ProductionFinishedGoodLotOrigin? origin,
        TraceGraph graph,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(lot.SupplierCode))
        {
            var supplierId = SupplierNodeId(company, lot.SupplierCode);
            graph.AddNode(new LotGenealogyNode
            {
                Id = supplierId,
                Kind = "Supplier snapshot",
                Title = lot.SupplierCode,
                Detail = "SupplierCode stored on the company-wide inventory lot master.",
                EvidenceState = "Lot snapshot",
                BranchCode = branch
            });
            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = supplierId,
                ToNodeId = InventoryLotNodeId(lot.Id),
                Relationship = "Lot supplier snapshot",
                EvidenceState = "Lot snapshot",
                Explanation = "Supplier is displayed from IvLot.SupplierCode.",
                IsActiveImpact = false
            }, LotGenealogyDirection.Backward);
        }

        if (origin is null)
            return;

        var workOrder = await db.ProductionWorkOrders.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Uid == origin.WorkOrderId
                && x.CompanyCode == company && x.BranchCode == branch, cancellationToken);
        if (workOrder is null)
        {
            graph.Warn("Finished Good lot origin metadata names a Work Order outside the visible branch or an unavailable Work Order.");
            return;
        }

        var workOrderNode = new LotGenealogyNode
        {
            Id = WorkOrderNodeId(workOrder.Uid),
            Kind = "Origin Work Order",
            Title = workOrder.WorkOrderNo,
            Detail = $"Route step {origin.RouteStepId} · operation {origin.OperationId} · physical lot {origin.PhysicalLotNo}",
            EvidenceState = "Lot origin metadata",
            BranchCode = branch
        };
        graph.AddNode(workOrderNode);
        graph.AddEdge(new LotGenealogyEdge
        {
            FromNodeId = workOrderNode.Id,
            ToNodeId = InventoryLotNodeId(lot.Id),
            Relationship = "FG lot origin metadata",
            EvidenceState = "Metadata only",
            Explanation = "Origin metadata is descriptive; the ProductionFinishedGoodFact is the movement bridge.",
            IsActiveImpact = false
        }, LotGenealogyDirection.Backward);
    }

    private async Task AddInventoryHistoryAsync(
        AppDbContext db,
        string company,
        string branch,
        IvLot lot,
        IReadOnlyList<IvTrxHistory> histories,
        TraceGraph graph,
        CancellationToken cancellationToken)
    {
        var statuses = await GetInventoryHistoryStatesAsync(db, company, branch, histories, cancellationToken);
        foreach (var history in histories)
        {
            var state = statuses.GetValueOrDefault(history.Id, EvidenceState.Missing);
            var historyNode = InventoryHistoryNode(history, state, branch);
            graph.AddNode(historyNode);

            if (history.ToLotId == lot.Id)
            {
                graph.AddEdge(new LotGenealogyEdge
                {
                    FromNodeId = historyNode.Id,
                    ToNodeId = InventoryLotNodeId(lot.Id),
                    Relationship = "Inventory receipt / transfer in",
                    EvidenceState = state.Label,
                    Explanation = "IvTrxHistory.ToLotId matches the selected IvLot.Id.",
                    ExactBaseQty = history.ToStdQty,
                    BaseUom = history.ToStdUom,
                    EventDate = history.TrxDtTime,
                    IsActiveImpact = state.IsActive
                }, LotGenealogyDirection.Backward);

                if (history.TrxType == IvTrxTypes.GoodsReceive)
                    await AddPurchaseSupplierBridgeAsync(db, company, branch, history, state, graph, cancellationToken);
            }

            if (history.FromLotId == lot.Id)
            {
                graph.AddEdge(new LotGenealogyEdge
                {
                    FromNodeId = InventoryLotNodeId(lot.Id),
                    ToNodeId = historyNode.Id,
                    Relationship = "Inventory issue / transfer out",
                    EvidenceState = state.Label,
                    Explanation = "IvTrxHistory.FromLotId matches the selected IvLot.Id.",
                    ExactBaseQty = history.FrStdQty,
                    BaseUom = history.FrStdUom,
                    EventDate = history.TrxDtTime,
                    IsActiveImpact = state.IsActive
                }, LotGenealogyDirection.Forward);
            }

            if (state.IsIncomplete)
                graph.Warn($"Inventory history {history.Id} is legacy, unsealed, or has incomplete posting evidence.");
        }

        await AddFinishedGoodFactBridgesAsync(db, company, branch, histories, graph, cancellationToken);
        await AddShipmentCustomersAsync(db, company, branch, [lot.Id], graph, cancellationToken);
    }

    private async Task AddPurchaseSupplierBridgeAsync(
        AppDbContext db,
        string company,
        string branch,
        IvTrxHistory history,
        EvidenceState historyState,
        TraceGraph graph,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(history.PoNo) || history.PoRelNo is null)
        {
            graph.Warn($"Goods receipt history {history.Id} has no exact PO number and release reference.");
            return;
        }

        var po = await db.PoOrders.AsNoTracking().FirstOrDefaultAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch
            && x.PoNo == history.PoNo && x.PoRelNo == history.PoRelNo.Value, cancellationToken);
        if (po is null)
        {
            graph.Warn($"Goods receipt history {history.Id} references PO {history.PoNo}/{history.PoRelNo}, but that PO is not visible in this branch.");
            return;
        }

        var poNodeId = PurchaseOrderNodeId(company, branch, po.PoNo, po.PoRelNo);
        graph.AddNode(new LotGenealogyNode
        {
            Id = poNodeId,
            Kind = "Purchase Order",
            Title = $"{po.PoNo} / release {po.PoRelNo}",
            Detail = "Resolved from the posted goods receipt's PO number and release.",
            EvidenceState = historyState.IsIncomplete ? "Legacy exact PO reference" : historyState.Label,
            BranchCode = branch,
            EventDate = po.PoDate
        });
        graph.AddEdge(new LotGenealogyEdge
        {
            FromNodeId = InventoryHistoryNodeId(history.Id),
            ToNodeId = poNodeId,
            Relationship = "Posted GR to PO",
            EvidenceState = historyState.Label,
            Explanation = "Exact IvTrxHistory.PoNo + PoRelNo reference.",
            EventDate = history.TrxDtTime,
            IsActiveImpact = historyState.IsActive
        }, LotGenealogyDirection.Backward);

        if (string.IsNullOrWhiteSpace(po.VendCode))
        {
            graph.Warn($"PO {po.PoNo}/{po.PoRelNo} has no supplier code snapshot.");
            return;
        }

        var supplierNodeId = SupplierNodeId(company, po.VendCode);
        graph.AddNode(new LotGenealogyNode
        {
            Id = supplierNodeId,
            Kind = "Supplier",
            Title = po.VendCode,
            Detail = po.VendName ?? "Derived from posted GR/PO",
            EvidenceState = "Derived from posted GR/PO",
            BranchCode = branch
        });
        graph.AddEdge(new LotGenealogyEdge
        {
            FromNodeId = poNodeId,
            ToNodeId = supplierNodeId,
            Relationship = "PO supplier",
            EvidenceState = "PO supplier snapshot",
            Explanation = string.IsNullOrWhiteSpace(po.VendName)
                ? "Supplier code derived from the exact posted GR/PO link."
                : "Supplier code and name are the PO supplier snapshot.",
            IsActiveImpact = historyState.IsActive
        }, LotGenealogyDirection.Backward);
    }

    private async Task AddFinishedGoodFactBridgesAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyList<IvTrxHistory> histories,
        TraceGraph graph,
        CancellationToken cancellationToken)
    {
        var historyIds = histories.Select(x => x.Id).Distinct().ToArray();
        if (historyIds.Length == 0)
            return;

        var historyById = histories.ToDictionary(x => x.Id);
        var facts = new List<ProductionFinishedGoodFact>();
        foreach (var batch in Chunks(historyIds))
        {
            facts.AddRange(await (from fact in db.ProductionFinishedGoodFactRows.AsNoTracking()
                join posting in db.StockPostings.AsNoTracking() on fact.StockPostingId equals posting.Id
                where batch.Contains(fact.InventoryHistoryId)
                    && posting.CompanyCode == company && posting.BranchCode == branch
                select fact).Take(MaxNodes + 1).ToListAsync(cancellationToken));
        }
        if (facts.Count > MaxNodes)
        {
            facts = facts.Take(MaxNodes).ToList();
            graph.MarkTruncated("Finished Good fact set exceeded the trace node limit.");
        }

        var reverseFacts = await LoadReversingFinishedGoodFactsAsync(db, company, branch,
            facts.Where(x => x.ReversesFactId is null).Select(x => x.Id).ToArray(), cancellationToken);
        var postingIds = facts.Select(x => x.StockPostingId).Concat(reverseFacts.Select(x => x.StockPostingId)).Distinct().ToArray();
        var postingStates = await GetPostingStatesAsync(db, company, branch, postingIds, cancellationToken);
        var reversalsByFact = reverseFacts.Where(x => x.ReversesFactId is not null)
            .GroupBy(x => x.ReversesFactId!.Value).ToDictionary(x => x.Key, x => x.ToArray());
        var factMovementIds = facts.Select(x => x.ProductionMovementId).Distinct().ToArray();
        var linkedMovements = await LoadProductionMovementsAsync(db, company, branch, factMovementIds, cancellationToken);
        var linkedMovementIds = linkedMovements.Select(x => x.Uid).ToHashSet();
        var linkedMovementStates = await GetProductionMovementStatesAsync(db, company, branch, factMovementIds, cancellationToken);
        var historyStates = await GetInventoryHistoryStatesAsync(db, company, branch, histories, cancellationToken);

        foreach (var fact in facts)
        {
            if (!historyById.TryGetValue(fact.InventoryHistoryId, out var history))
                continue;

            var factState = ClassifyFinishedGoodFact(fact, postingStates,
                reversalsByFact.GetValueOrDefault(fact.Id) ?? []);
            var exactHistory = IsExactFinishedGoodHistory(fact, history);
            var historyState = exactHistory
                ? historyStates.GetValueOrDefault(history.Id, EvidenceState.Missing)
                : EvidenceState.Incomplete("FG fact does not match its branch-scoped Finished Goods inventory history.");
            var movementState = linkedMovementStates.GetValueOrDefault(fact.ProductionMovementId, EvidenceState.Missing);
            var movementEdgeState = CombineEvidence(factState, movementState);
            var historyEdgeState = CombineEvidence(factState, historyState);
            var factNodeId = FinishedGoodFactNodeId(fact.Id);
            graph.AddNode(new LotGenealogyNode
            {
                Id = factNodeId,
                Kind = "Finished Good receipt fact",
                Title = $"FG fact {fact.Id}",
                Detail = $"Batch {fact.BatchId} · source {fact.SourceId} · history {fact.InventoryHistoryId}",
                EvidenceState = CombineEvidence(factState, historyState, movementState).Label,
                BranchCode = branch,
                BaseQty = fact.BaseQty,
                BaseUom = history.ToStdUom,
                EventDate = history.TrxDtTime
            });
            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = ProductionMovementNodeId(fact.ProductionMovementId),
                ToNodeId = factNodeId,
                Relationship = "Production to FG receipt fact",
                EvidenceState = movementEdgeState.Label,
                Explanation = "ProductionFinishedGoodFact links the exact production movement and inventory history IDs.",
                ExactBaseQty = fact.BaseQty,
                BaseUom = history.ToStdUom,
                EventDate = history.TrxDtTime,
                IsActiveImpact = movementEdgeState.IsActive
            }, LotGenealogyDirection.Backward);
            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = factNodeId,
                ToNodeId = InventoryHistoryNodeId(history.Id),
                Relationship = "FG fact to inventory history",
                EvidenceState = historyEdgeState.Label,
                Explanation = "Exact InventoryHistoryId persisted on the immutable FG fact.",
                ExactBaseQty = fact.BaseQty,
                BaseUom = history.ToStdUom,
                EventDate = history.TrxDtTime,
                IsActiveImpact = historyEdgeState.IsActive
            }, LotGenealogyDirection.Backward);

            // These same edges are useful from the supplier side; graph identity de-duplicates them.
            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = ProductionMovementNodeId(fact.ProductionMovementId),
                ToNodeId = factNodeId,
                Relationship = "Production to FG receipt fact",
                EvidenceState = movementEdgeState.Label,
                Explanation = "ProductionFinishedGoodFact links the exact production movement and inventory history IDs.",
                ExactBaseQty = fact.BaseQty,
                BaseUom = history.ToStdUom,
                EventDate = history.TrxDtTime,
                IsActiveImpact = movementEdgeState.IsActive
            }, LotGenealogyDirection.Forward);
            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = factNodeId,
                ToNodeId = InventoryHistoryNodeId(history.Id),
                Relationship = "FG fact to inventory history",
                EvidenceState = historyEdgeState.Label,
                Explanation = "Exact InventoryHistoryId persisted on the immutable FG fact.",
                ExactBaseQty = fact.BaseQty,
                BaseUom = history.ToStdUom,
                EventDate = history.TrxDtTime,
                IsActiveImpact = historyEdgeState.IsActive
            }, LotGenealogyDirection.Forward);

            graph.AddProductionMovementId(fact.ProductionMovementId);
            if (!exactHistory)
                graph.Warn($"FG fact {fact.Id} does not match the type or StockPostingId of inventory history {history.Id}.");
            if (factState.IsIncomplete || historyState.IsIncomplete || movementState.IsIncomplete)
                graph.Warn($"FG fact {fact.Id}, inventory history {history.Id}, or its production movement has incomplete sealed evidence.");
            if (!linkedMovementIds.Contains(fact.ProductionMovementId))
                graph.Warn($"FG fact {fact.Id} points to missing production movement {fact.ProductionMovementId}; trace stops at this bridge.");
            else if (factState.IsActive && !movementState.IsActive)
                graph.Warn($"Production movement {fact.ProductionMovementId} linked from FG fact {fact.Id} has no sealed active posting evidence.");
        }

        foreach (var history in histories.Where(x => x.TrxType == IvTrxTypes.FinishedGoods
                && !facts.Any(f => f.InventoryHistoryId == x.Id)))
            graph.Warn($"FG inventory history {history.Id} has no ProductionFinishedGoodFact bridge; trace stops at the last proven inventory node.");
    }

    private static async Task<IReadOnlyList<ProductionFinishedGoodFact>> LoadReversingFinishedGoodFactsAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<long> factIds,
        CancellationToken cancellationToken)
    {
        if (factIds.Count == 0)
            return [];

        var result = new List<ProductionFinishedGoodFact>();
        foreach (var batch in Chunks(factIds))
        {
            result.AddRange(await (from fact in db.ProductionFinishedGoodFactRows.AsNoTracking()
                join posting in db.StockPostings.AsNoTracking() on fact.StockPostingId equals posting.Id
                where fact.ReversesFactId != null && batch.Contains(fact.ReversesFactId.Value)
                    && posting.CompanyCode == company && posting.BranchCode == branch
                select fact).ToListAsync(cancellationToken));
        }
        return result;
    }

    private async Task<IReadOnlyList<long>> GetFinishedGoodMovementSeedsAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyList<IvTrxHistory> histories,
        TraceGraph graph,
        CancellationToken cancellationToken)
    {
        // The fact helper creates and classifies the exact bridge nodes. Here we return active movement
        // identities only; there is no LotNo or date fallback when the bridge is absent.
        var historyIds = histories.Select(x => x.Id).Distinct().ToArray();
        if (historyIds.Length == 0)
            return [];

        var facts = new List<ProductionFinishedGoodFact>();
        foreach (var batch in Chunks(historyIds))
        {
            facts.AddRange(await (from fact in db.ProductionFinishedGoodFactRows.AsNoTracking()
                join posting in db.StockPostings.AsNoTracking() on fact.StockPostingId equals posting.Id
                where batch.Contains(fact.InventoryHistoryId)
                    && posting.CompanyCode == company && posting.BranchCode == branch
                select fact).ToListAsync(cancellationToken));
        }
        if (facts.Count == 0)
            return [];

        var reverseFacts = await LoadReversingFinishedGoodFactsAsync(db, company, branch,
            facts.Where(x => x.ReversesFactId is null).Select(x => x.Id).ToArray(), cancellationToken);
        var postingStates = await GetPostingStatesAsync(db, company, branch,
            facts.Select(x => x.StockPostingId).Concat(reverseFacts.Select(x => x.StockPostingId)).Distinct().ToArray(), cancellationToken);
        var reversalsByFact = reverseFacts.Where(x => x.ReversesFactId is not null)
            .GroupBy(x => x.ReversesFactId!.Value).ToDictionary(x => x.Key, x => x.ToArray());

        var historyById = histories.ToDictionary(x => x.Id);
        var historyStates = await GetInventoryHistoryStatesAsync(db, company, branch, histories, cancellationToken);
        var activeFactMovementIds = facts.Where(x => historyById.TryGetValue(x.InventoryHistoryId, out var history)
                && IsExactFinishedGoodHistory(x, history)
                && historyStates.GetValueOrDefault(history.Id, EvidenceState.Missing).IsActive
                && ClassifyFinishedGoodFact(x, postingStates,
                    reversalsByFact.GetValueOrDefault(x.Id) ?? []).IsActive)
            .Select(x => x.ProductionMovementId).Distinct().ToArray();
        var movementStates = await GetProductionMovementStatesAsync(db, company, branch, activeFactMovementIds, cancellationToken);
        return activeFactMovementIds.Where(x => movementStates.GetValueOrDefault(x).IsActive)
            .Take(MaxNodes).ToArray();
    }

    private async Task AddShipmentCustomersAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<int> lotIds,
        TraceGraph graph,
        CancellationToken cancellationToken)
    {
        if (lotIds.Count == 0)
            return;

        var histories = new List<IvTrxHistory>();
        foreach (var batch in Chunks(lotIds.Distinct().ToArray()))
        {
            histories.AddRange(await db.IvTrxHistories.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch
                    && x.TrxType == IvTrxTypes.SalesOut && x.FromLotId != null
                    && batch.Contains(x.FromLotId.Value))
                .OrderBy(x => x.TrxDtTime).ThenBy(x => x.Id)
                .Take(MaxNodes + 1).ToListAsync(cancellationToken));
        }
        if (histories.Count > MaxNodes)
        {
            histories = histories.Take(MaxNodes).ToList();
            graph.MarkTruncated("Sales shipment evidence exceeded the trace node limit.");
        }

        var historyStates = await GetInventoryHistoryStatesAsync(db, company, branch, histories, cancellationToken);
        var doNos = histories.Select(x => x.DoNo).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Cast<string>().ToArray();
        var deliveries = new Dictionary<string, ErpWeb.Model.Entities.Sales.SaDo>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in Chunks(doNos))
        {
            var rows = await db.SaDos.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch && batch.Contains(x.DoNo))
                .ToListAsync(cancellationToken);
            foreach (var delivery in rows)
                deliveries[delivery.DoNo] = delivery;
        }

        var customerCodes = deliveries.Values.Select(x => x.CustCode).Distinct().ToArray();
        var customers = new Dictionary<string, ErpWeb.Model.Entities.CustomerProfile.SaCust>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in Chunks(customerCodes))
        {
            var rows = await db.SaCusts.AsNoTracking()
                .Where(x => x.CompanyCode == company && batch.Contains(x.CustCode))
                .ToListAsync(cancellationToken);
            foreach (var customer in rows)
                customers[customer.CustCode] = customer;
        }

        foreach (var history in histories)
        {
            var state = historyStates.GetValueOrDefault(history.Id, EvidenceState.Missing);
            graph.AddNode(InventoryHistoryNode(history, state, branch));
            if (history.FromLotId is not int lotId)
                continue;

            if (string.IsNullOrWhiteSpace(history.DoNo) || !deliveries.TryGetValue(history.DoNo, out var delivery))
            {
                graph.Warn($"SP inventory history {history.Id} has no visible exact DO/customer bridge.");
                continue;
            }

            var doNodeId = DeliveryOrderNodeId(company, branch, delivery.DoNo);
            graph.AddNode(new LotGenealogyNode
            {
                Id = doNodeId,
                Kind = "Delivery Order",
                Title = delivery.DoNo,
                Detail = $"Posted SP reference {history.RefNo ?? history.BatchNo.ToString()} · DO date {delivery.DoDate:yyyy-MM-dd}",
                EvidenceState = state.Label,
                BranchCode = branch,
                EventDate = delivery.DoDate
            });
            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = InventoryLotNodeId(lotId),
                ToNodeId = doNodeId,
                Relationship = "Posted SP shipment",
                EvidenceState = state.Label,
                Explanation = "Shipment quantity comes from the lot-specific IvTrxHistory row; one DO may contain split lots.",
                ExactBaseQty = history.FrStdQty,
                BaseUom = history.FrStdUom,
                EventDate = history.TrxDtTime,
                IsActiveImpact = state.IsActive
            }, LotGenealogyDirection.Forward);
            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = InventoryLotNodeId(lotId),
                ToNodeId = InventoryHistoryNodeId(history.Id),
                Relationship = "Shipment history",
                EvidenceState = state.Label,
                Explanation = "SP history retains the exact FromLotId and posted shipment quantity.",
                ExactBaseQty = history.FrStdQty,
                BaseUom = history.FrStdUom,
                EventDate = history.TrxDtTime,
                IsActiveImpact = state.IsActive
            }, LotGenealogyDirection.Forward);

            var customerCode = delivery.CustCode;
            var customer = customers.GetValueOrDefault(customerCode);
            var customerNodeId = CustomerNodeId(company, customerCode);
            graph.AddNode(new LotGenealogyNode
            {
                Id = customerNodeId,
                Kind = "Customer",
                Title = customer?.CustName ?? delivery.CustName ?? customerCode,
                Detail = customerCode,
                EvidenceState = "Resolved from exact SaDo.CustCode",
                BranchCode = branch,
                IsCustomer = state.IsActive
            });
            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = doNodeId,
                ToNodeId = customerNodeId,
                Relationship = "DO customer",
                EvidenceState = "DO customer snapshot",
                Explanation = "Customer identity comes from SaDo.CustCode; display name uses the DO snapshot or customer master.",
                EventDate = delivery.DoDate,
                IsActiveImpact = state.IsActive
            }, LotGenealogyDirection.Forward);

            if (state.IsIncomplete)
                graph.Warn($"SP history {history.Id} is not backed by sealed active posting evidence; customer impact is shown as historical only.");
        }
    }

    private async Task<IReadOnlyList<long>> GetMaterialIssueSeedsAsync(
        AppDbContext db,
        string company,
        string branch,
        int lotId,
        TraceGraph graph,
        CancellationToken cancellationToken)
    {
        var issues = await db.ProductionMaterialMovements.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.LotId == lotId && x.MovementType == ProductionMaterialMovementTypes.Issue)
            .OrderBy(x => x.MovementDate).ThenBy(x => x.Uid)
            .Take(MaxNodes + 1).ToListAsync(cancellationToken);
        if (issues.Count > MaxNodes)
        {
            issues = issues.Take(MaxNodes).ToList();
            graph.MarkTruncated("Material issue evidence exceeded the trace node limit.");
        }
        if (issues.Count == 0)
            return [];

        var states = await GetProductionMaterialStatesAsync(db, company, branch, issues, cancellationToken);
        var (inventoryHistories, historyStates) = await LoadMaterialHistoriesAsync(
            db, company, branch, issues, cancellationToken);
        var seedIds = new List<long>();
        foreach (var issue in issues)
        {
            var state = states.GetValueOrDefault(issue.Uid, EvidenceState.Missing);
            var materialNodeId = MaterialMovementNodeId(issue.Uid);
            graph.AddNode(MaterialMovementNode(issue, state, branch));
            IvTrxHistory? history = null;
            if (issue.InventoryHistoryId is int historyId
                && inventoryHistories.TryGetValue(historyId, out var matchingHistory)
                && IsExactMaterialHistory(issue, matchingHistory))
                history = matchingHistory;
            var hasExactHistory = history is not null;
            var historyState = hasExactHistory
                ? historyStates.GetValueOrDefault(issue.InventoryHistoryId!.Value, EvidenceState.Missing)
                : EvidenceState.Incomplete("Material issue has no matching inventory history ID and lot link.");
            if (hasExactHistory)
            {
                graph.AddNode(InventoryHistoryNode(history!, historyState, branch));
                graph.AddEdge(new LotGenealogyEdge
                {
                    FromNodeId = InventoryLotNodeId(lotId),
                    ToNodeId = InventoryHistoryNodeId(history!.Id),
                    Relationship = "Inventory issue history",
                    EvidenceState = historyState.Label,
                    Explanation = "Exact InventoryHistoryId and FromLotId persisted on the production material issue.",
                    ExactBaseQty = history.FrStdQty,
                    BaseUom = history.FrStdUom,
                    EventDate = history.TrxDtTime,
                    IsActiveImpact = historyState.IsActive
                }, LotGenealogyDirection.Forward);
                graph.AddEdge(new LotGenealogyEdge
                {
                    FromNodeId = InventoryHistoryNodeId(history.Id),
                    ToNodeId = materialNodeId,
                    Relationship = "Inventory history to material issue",
                    EvidenceState = CombineEvidence(state, historyState).Label,
                    Explanation = "ProductionMaterialMovement.InventoryHistoryId is the exact inventory posting bridge.",
                    ExactBaseQty = issue.BaseQty,
                    BaseUom = issue.BaseUom,
                    EventDate = issue.MovementDate,
                    IsActiveImpact = state.IsActive && historyState.IsActive
                }, LotGenealogyDirection.Forward);
            }
            else
            {
                graph.Warn($"Material issue {issue.Uid} has no matching branch-scoped inventory history with the same FromLotId.");
                graph.AddEdge(new LotGenealogyEdge
                {
                    FromNodeId = InventoryLotNodeId(lotId),
                    ToNodeId = materialNodeId,
                    Relationship = "Inventory lot to material issue",
                    EvidenceState = historyState.Label,
                    Explanation = "LotId matches, but the exact posted inventory-history bridge is incomplete.",
                    ExactBaseQty = issue.BaseQty,
                    BaseUom = issue.BaseUom,
                    EventDate = issue.MovementDate,
                    IsActiveImpact = false
                }, LotGenealogyDirection.Forward);
            }
            if (issue.ProductionBalLotMovementId is not long balanceMovementId
                || issue.ProductionBalLotId is not long productionLotId)
            {
                graph.Warn($"Material issue {issue.Uid} has no exact production balance movement link.");
                continue;
            }

            var balanceMovement = await db.ProductionBalLotMovements.AsNoTracking()
                .Where(x => x.Uid == balanceMovementId
                    && db.ProductionBalLots.Any(l => l.Uid == x.ProductionBalLotId
                        && l.Uid == productionLotId && l.CompanyCode == company && l.BranchCode == branch))
                .FirstOrDefaultAsync(cancellationToken);
            if (balanceMovement is null)
            {
                graph.Warn($"Material issue {issue.Uid} points to missing or out-of-scope production movement {balanceMovementId}.");
                continue;
            }

            await EnsureProductionMovementNodesAsync(db, company, branch, [balanceMovementId], graph, cancellationToken);
            var balanceState = (await GetProductionMovementStatesAsync(db, company, branch,
                [balanceMovementId], cancellationToken)).GetValueOrDefault(balanceMovementId, EvidenceState.Missing);
            var active = state.IsActive && historyState.IsActive && balanceState.IsActive && hasExactHistory;
            var evidence = CombineEvidence(state, historyState, balanceState, active);
            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = materialNodeId,
                ToNodeId = ProductionMovementNodeId(balanceMovementId),
                Relationship = "Material issue to production balance",
                EvidenceState = evidence.Label,
                Explanation = "ProductionMaterialMovement.ProductionBalLotMovementId and ProductionBalLotId are the persisted bridge.",
                ExactBaseQty = issue.BaseQty,
                BaseUom = issue.BaseUom,
                EventDate = issue.MovementDate,
                IsActiveImpact = active
            }, LotGenealogyDirection.Forward);
            if (active)
                seedIds.Add(balanceMovementId);
            if (state.IsIncomplete || historyState.IsIncomplete || balanceState.IsIncomplete)
                graph.Warn($"Material issue {issue.Uid} or its production balance movement has incomplete posting evidence.");
        }

        return seedIds.Distinct().Take(MaxNodes).ToArray();
    }

    private async Task<(Dictionary<int, IvTrxHistory> Histories, Dictionary<int, EvidenceState> States)> LoadMaterialHistoriesAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<ProductionMaterialMovement> materialMovements,
        CancellationToken cancellationToken)
    {
        var historyIds = materialMovements.Where(x => x.InventoryHistoryId is not null)
            .Select(x => x.InventoryHistoryId!.Value).Distinct().ToArray();
        var histories = new List<IvTrxHistory>();
        foreach (var batch in Chunks(historyIds))
        {
            histories.AddRange(await db.IvTrxHistories.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch && batch.Contains(x.Id))
                .ToListAsync(cancellationToken));
        }
        return (histories.ToDictionary(x => x.Id),
            await GetInventoryHistoryStatesAsync(db, company, branch, histories, cancellationToken));
    }

    private static bool IsExactMaterialHistory(ProductionMaterialMovement issue, IvTrxHistory history) =>
        issue.InventoryHistoryId == history.Id
        && issue.LotId is int lotId
        && history.FromLotId == lotId
        && string.Equals(history.TrxType, IvTrxTypes.IssueToProduction, StringComparison.OrdinalIgnoreCase)
        && (issue.StockPostingId is null || history.StockPostingId == issue.StockPostingId);

    private async Task TraceProductionMovementsAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<long> seedIds,
        LotGenealogyDirection direction,
        TraceGraph graph,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<long>();
        var frontier = seedIds.Distinct().Take(MaxNodes).ToArray();
        if (frontier.Length == 0)
            return;

        await EnsureProductionMovementNodesAsync(db, company, branch, frontier, graph, cancellationToken);
        foreach (var id in frontier)
            visited.Add(id);

        var depthLimitReached = false;
        var nodeLimitReached = false;
        for (var depth = 0; frontier.Length > 0 && depth < MaxDepth && !nodeLimitReached; depth++)
        {
            var dependencies = new List<ProductionPoolDependency>();
            foreach (var batch in Chunks(frontier))
            {
                var chunkRows = await (from edge in db.ProductionPoolDependencyRows.AsNoTracking()
                    join posting in db.StockPostings.AsNoTracking() on edge.StockPostingId equals posting.Id
                    where posting.CompanyCode == company && posting.BranchCode == branch
                        && (direction == LotGenealogyDirection.Forward
                            ? batch.Contains(edge.ContributorMovementId)
                            : batch.Contains(edge.ConsumerMovementId))
                    orderby edge.Id
                    select edge).Take(MaxNodes + 1).ToListAsync(cancellationToken);
                dependencies.AddRange(chunkRows);
            }
            if (dependencies.Count > MaxNodes)
            {
                dependencies = dependencies.Take(MaxNodes).ToList();
                graph.MarkTruncated("Production dependency traversal exceeded the trace node limit.");
            }
            if (dependencies.Count == 0)
                break;

            var directOriginalIds = dependencies.Where(x => x.ReversesDependencyId is null).Select(x => x.Id).Distinct().ToArray();
            var reversingDependencies = await LoadReversingDependenciesAsync(db, company, branch, directOriginalIds, cancellationToken);
            var depPostingIds = dependencies.Select(x => x.StockPostingId)
                .Concat(reversingDependencies.Select(x => x.StockPostingId)).Distinct().ToArray();
            var depPostingStates = await GetPostingStatesAsync(db, company, branch, depPostingIds, cancellationToken);
            var reversalsByDependency = reversingDependencies.Where(x => x.ReversesDependencyId is not null)
                .GroupBy(x => x.ReversesDependencyId!.Value).ToDictionary(x => x.Key, x => x.ToArray());

            var allocations = await LoadAllocationsForDependenciesAsync(db, company, branch, dependencies, direction, cancellationToken);
            if (allocations.Count > MaxNodes)
            {
                allocations = allocations.Take(MaxNodes).ToArray();
                graph.MarkTruncated("Production quantity allocation evidence exceeded the trace node limit.");
            }
            var reverseAllocationIds = allocations.Where(x => x.ReversesAllocationId is null).Select(x => x.Id).Distinct().ToArray();
            var reversingAllocations = await LoadReversingAllocationsAsync(db, company, branch, reverseAllocationIds, cancellationToken);
            var allocationPostings = await GetPostingStatesAsync(db, company, branch,
                allocations.Select(x => x.StockPostingId).Concat(reversingAllocations.Select(x => x.StockPostingId)).Distinct().ToArray(), cancellationToken);
            var reversalsByAllocation = reversingAllocations.Where(x => x.ReversesAllocationId is not null)
                .GroupBy(x => x.ReversesAllocationId!.Value).ToDictionary(x => x.Key, x => x.ToArray());

            var movementIds = dependencies.SelectMany(x => new[] { x.ContributorMovementId, x.ConsumerMovementId })
                .Concat(reversingDependencies.SelectMany(x => new[] { x.ContributorMovementId, x.ConsumerMovementId }))
                .Distinct().ToArray();
            var movements = await LoadProductionMovementsAsync(db, company, branch, movementIds, cancellationToken);
            var movementById = movements.ToDictionary(x => x.Uid);
            var movementStates = await GetProductionMovementStatesAsync(db, company, branch, movementIds, cancellationToken);
            await EnsureProductionMovementNodesAsync(db, company, branch, movementIds, graph, cancellationToken);

            var allocationsByPair = new Dictionary<(long Contributor, long Consumer), decimal>();
            foreach (var group in allocations.Where(x => x.ReversesAllocationId is null)
                         .GroupBy(x => (x.ReceiptMovementId, x.OutboundMovementId)))
            {
                var total = 0m;
                foreach (var allocation in group)
                {
                    var allocationState = ClassifyAllocation(allocation, allocationPostings,
                        reversalsByAllocation.GetValueOrDefault(allocation.Id) ?? []);
                    if (allocationState.IsActive)
                        total += allocation.BaseQty;
                }
                if (total > 0m)
                    allocationsByPair[group.Key] = total;
            }

            var next = new List<long>();
            foreach (var edge in dependencies)
            {
                if (!movementById.TryGetValue(edge.ContributorMovementId, out var contributor)
                    || !movementById.TryGetValue(edge.ConsumerMovementId, out var consumer))
                {
                    graph.Warn($"Production dependency {edge.Id} refers to a movement outside the visible branch or missing movement evidence.");
                    continue;
                }

                var edgeState = ClassifyDependency(edge, depPostingStates,
                    reversalsByDependency.GetValueOrDefault(edge.Id) ?? []);
                var contributorState = movementStates.GetValueOrDefault(contributor.Uid, EvidenceState.Missing);
                var consumerState = movementStates.GetValueOrDefault(consumer.Uid, EvidenceState.Missing);
                var active = edgeState.IsActive && contributorState.IsActive && consumerState.IsActive;
                var evidence = active
                    ? (allocationsByPair.ContainsKey((contributor.Uid, consumer.Uid)) ? "Exact quantity allocation" : "Contributor dependency")
                    : CombineEvidence(edgeState, contributorState, consumerState).Label;
                var allocationKey = (contributor.Uid, consumer.Uid);
                var quantity = allocationsByPair.GetValueOrDefault(allocationKey);
                var exactQuantityAvailable = allocationsByPair.ContainsKey(allocationKey);
                var explanation = exactQuantityAvailable
                    ? "Base quantity is summed from active ProductionMovementAllocation rows."
                    : "Contributor dependency is proven; an authoritative exact quantity split is unavailable.";
                graph.AddEdge(new LotGenealogyEdge
                {
                    FromNodeId = ProductionMovementNodeId(contributor.Uid),
                    ToNodeId = ProductionMovementNodeId(consumer.Uid),
                    Relationship = exactQuantityAvailable ? "Production contributor allocation" : "Production contributor dependency",
                    EvidenceState = evidence,
                    Explanation = explanation,
                    ExactBaseQty = exactQuantityAvailable ? quantity : null,
                    BaseUom = exactQuantityAvailable ? consumer.BaseUom : null,
                    EventDate = consumer.MovementDate,
                    IsActiveImpact = active
                }, direction);

                if (!active)
                {
                    if (edgeState.IsIncomplete || contributorState.IsIncomplete || consumerState.IsIncomplete)
                        graph.Warn($"Production genealogy edge {edge.Id} has legacy, unsealed, or incomplete posting evidence.");
                    continue;
                }

                var nextId = direction == LotGenealogyDirection.Forward ? consumer.Uid : contributor.Uid;
                if (!visited.Contains(nextId))
                {
                    if (visited.Count >= MaxNodes)
                    {
                        nodeLimitReached = true;
                        graph.MarkTruncated("Production traversal reached the maximum node count.");
                        break;
                    }
                    visited.Add(nextId);
                    next.Add(nextId);
                }
            }

            foreach (var reversal in reversingDependencies)
            {
                if (!movementById.TryGetValue(reversal.ContributorMovementId, out var contributor)
                    || !movementById.TryGetValue(reversal.ConsumerMovementId, out var consumer))
                    continue;
                var reversalState = ClassifyReversalRecord(reversal.StockPostingId, depPostingStates, "Reversal dependency");
                graph.AddEdge(new LotGenealogyEdge
                {
                    FromNodeId = ProductionMovementNodeId(contributor.Uid),
                    ToNodeId = ProductionMovementNodeId(consumer.Uid),
                    Relationship = "Reversal dependency audit",
                    EvidenceState = reversalState.Label,
                    Explanation = $"Reverses dependency {reversal.ReversesDependencyId}; retained as history and excluded from impact.",
                    IsActiveImpact = false
                }, direction);
            }

            if (next.Count == 0 || nodeLimitReached)
                break;
            if (depth == MaxDepth - 1)
            {
                depthLimitReached = true;
                break;
            }
            frontier = next.Distinct().ToArray();
        }

        if (nodeLimitReached)
            graph.MarkTruncated("Production traversal reached the maximum node count.");
        else if (depthLimitReached)
            graph.MarkTruncated($"Production traversal reached the maximum depth of {MaxDepth}.");
    }

    private async Task EnsureProductionMovementNodesAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<long> movementIds,
        TraceGraph graph,
        CancellationToken cancellationToken)
    {
        var missingIds = movementIds.Distinct().Where(x => !graph.HasNode(ProductionMovementNodeId(x))).ToArray();
        if (missingIds.Length == 0)
            return;

        foreach (var batch in Chunks(missingIds))
        {
            var movements = await LoadProductionMovementsAsync(db, company, branch, batch, cancellationToken);
            if (movements.Count == 0)
                continue;
            var states = await GetProductionMovementStatesAsync(db, company, branch,
                movements.Select(x => x.Uid).ToArray(), cancellationToken);
            var lotIds = movements.Select(x => x.ProductionBalLotId).Distinct().ToArray();
            var lots = await db.ProductionBalLots.AsNoTracking()
                .Where(x => lotIds.Contains(x.Uid) && x.CompanyCode == company && x.BranchCode == branch)
                .ToDictionaryAsync(x => x.Uid, cancellationToken);
            var orderIds = movements.Select(x => x.WorkOrderId).Distinct().ToArray();
            var orderNos = await db.ProductionWorkOrders.AsNoTracking()
                .Where(x => orderIds.Contains(x.Uid) && x.CompanyCode == company && x.BranchCode == branch)
                .ToDictionaryAsync(x => x.Uid, x => x.WorkOrderNo, cancellationToken);

            foreach (var movement in movements)
            {
                if (!lots.TryGetValue(movement.ProductionBalLotId, out var lot))
                    continue;
                var state = states.GetValueOrDefault(movement.Uid, EvidenceState.Missing);
                var workOrderNo = movement.WorkOrderNo ?? orderNos.GetValueOrDefault(movement.WorkOrderId);
                graph.AddNode(ProductionMovementNode(movement, lot, state, branch, workOrderNo));
            }

            await AddMaterialIssueBridgesForBalanceLotsAsync(db, company, branch,
                lots.Values.ToArray(), graph, cancellationToken);
        }
    }

    private async Task AddMaterialIssueBridgesForBalanceLotsAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<ProductionBalLot> balanceLots,
        TraceGraph graph,
        CancellationToken cancellationToken)
    {
        var fallbackBalanceLotIds = balanceLots.Where(x => x.Kind == ProductionBalLotKinds.MaterialIn
                && x.OriginalIssueMovementId is null)
            .Select(x => x.Uid).Distinct().ToArray();
        var fallbackIssues = new List<ProductionMaterialMovement>();
        foreach (var batch in Chunks(fallbackBalanceLotIds))
        {
            fallbackIssues.AddRange(await db.ProductionMaterialMovements.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch
                    && x.MovementType == ProductionMaterialMovementTypes.Issue
                    && x.ProductionBalLotId != null && batch.Contains(x.ProductionBalLotId.Value))
                .OrderBy(x => x.Uid).Take(MaxNodes + 1).ToListAsync(cancellationToken));
        }
        if (fallbackIssues.Count > MaxNodes)
        {
            fallbackIssues = fallbackIssues.Take(MaxNodes).ToList();
            graph.MarkTruncated("Fallback material issue evidence exceeded the trace node limit.");
        }
        foreach (var balanceLot in balanceLots.Where(x => x.Kind == ProductionBalLotKinds.MaterialIn
                     && x.OriginalIssueMovementId is null
                     && !fallbackIssues.Any(issue => issue.ProductionBalLotId == x.Uid)))
            graph.Warn($"Material balance lot {balanceLot.Uid} has no persisted OriginalIssueMovementId or linked material issue.");

        var issueIds = balanceLots.Where(x => x.OriginalIssueMovementId is not null)
            .Select(x => x.OriginalIssueMovementId!.Value)
            .Concat(fallbackIssues.Select(x => x.Uid)).Distinct().Take(MaxNodes + 1).ToArray();
        if (issueIds.Length > MaxNodes)
        {
            issueIds = issueIds.Take(MaxNodes).ToArray();
            graph.MarkTruncated("Linked material issue evidence exceeded the trace node limit.");
        }
        if (issueIds.Length == 0)
            return;
        var allowedIssueIds = issueIds.ToHashSet();

        var issueMovements = new List<ProductionMaterialMovement>();
        foreach (var batch in Chunks(issueIds))
        {
            issueMovements.AddRange(await db.ProductionMaterialMovements.AsNoTracking()
                .Where(x => batch.Contains(x.Uid) && x.CompanyCode == company && x.BranchCode == branch)
                .ToListAsync(cancellationToken));
        }
        var issuesById = issueMovements.ToDictionary(x => x.Uid);
        var states = await GetProductionMaterialStatesAsync(db, company, branch, issueMovements, cancellationToken);
        var (inventoryHistories, historyStates) = await LoadMaterialHistoriesAsync(
            db, company, branch, issueMovements, cancellationToken);
        var lotIds = issueMovements.Where(x => x.LotId is not null).Select(x => x.LotId!.Value).Distinct().ToArray();
        var inventoryLots = new Dictionary<int, IvLot>();
        foreach (var batch in Chunks(lotIds))
        {
            var rows = await db.IvLots.AsNoTracking()
                .Where(x => batch.Contains(x.Id) && x.CompanyCode == company)
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
                inventoryLots[row.Id] = row;
        }
        var itemCodes = inventoryLots.Values.Select(x => x.ICode).Distinct().ToArray();
        var itemRows = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in Chunks(itemCodes))
        {
            var rows = await db.IvStockMasters.AsNoTracking()
                .Where(x => x.CompanyCode == company && batch.Contains(x.ICode))
                .Select(x => new { x.ICode, x.IDesc })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
                itemRows[row.ICode] = row.IDesc;
        }

        var linkedIssues = new List<(ProductionBalLot BalanceLot, long IssueId)>();
        foreach (var balanceLot in balanceLots)
        {
            if (balanceLot.OriginalIssueMovementId is long originalIssueId)
            {
                if (allowedIssueIds.Contains(originalIssueId))
                    linkedIssues.Add((balanceLot, originalIssueId));
            }
            else
                linkedIssues.AddRange(fallbackIssues.Where(issue => issue.ProductionBalLotId == balanceLot.Uid)
                    .Where(issue => allowedIssueIds.Contains(issue.Uid))
                    .Select(issue => (balanceLot, issue.Uid)));
        }
        foreach (var (balanceLot, issueId) in linkedIssues)
        {
            if (!issuesById.TryGetValue(issueId, out var issue))
            {
                graph.Warn($"Production balance lot {balanceLot.Uid} points to missing or out-of-scope material issue {issueId}.");
                continue;
            }
            if (issue.MovementType != ProductionMaterialMovementTypes.Issue || issue.LotId is not int inventoryLotId
                || !inventoryLots.TryGetValue(inventoryLotId, out var inventoryLot))
            {
                graph.Warn($"Original material issue {issue.Uid} for production balance lot {balanceLot.Uid} has no exact inventory LotId.");
                continue;
            }

            var state = states.GetValueOrDefault(issue.Uid, EvidenceState.Missing);
            graph.AddNode(MaterialMovementNode(issue, state, branch));
            graph.AddNode(InventoryLotNode(inventoryLot, itemRows.GetValueOrDefault(inventoryLot.ICode), null, branch, false));
            IvTrxHistory? history = null;
            if (issue.InventoryHistoryId is int historyId
                && inventoryHistories.TryGetValue(historyId, out var matchingHistory)
                && IsExactMaterialHistory(issue, matchingHistory))
                history = matchingHistory;
            var hasExactHistory = history is not null;
            var historyState = hasExactHistory
                ? historyStates.GetValueOrDefault(issue.InventoryHistoryId!.Value, EvidenceState.Missing)
                : EvidenceState.Incomplete("Material issue has no matching inventory history ID and lot link.");
            var lotNodeId = InventoryLotNodeId(inventoryLotId);
            var materialNodeId = MaterialMovementNodeId(issue.Uid);
            if (hasExactHistory)
            {
                graph.AddNode(InventoryHistoryNode(history!, historyState, branch));
                foreach (var direction in new[] { LotGenealogyDirection.Backward, LotGenealogyDirection.Forward })
                {
                    graph.AddEdge(new LotGenealogyEdge
                    {
                        FromNodeId = lotNodeId,
                        ToNodeId = InventoryHistoryNodeId(history!.Id),
                        Relationship = "Inventory issue history",
                        EvidenceState = historyState.Label,
                        Explanation = "Exact InventoryHistoryId and FromLotId persisted on the production material issue.",
                        ExactBaseQty = history.FrStdQty,
                        BaseUom = history.FrStdUom,
                        EventDate = history.TrxDtTime,
                        IsActiveImpact = historyState.IsActive
                    }, direction);
                    graph.AddEdge(new LotGenealogyEdge
                    {
                        FromNodeId = InventoryHistoryNodeId(history.Id),
                        ToNodeId = materialNodeId,
                        Relationship = "Inventory history to material issue",
                        EvidenceState = CombineEvidence(state, historyState).Label,
                        Explanation = "ProductionMaterialMovement.InventoryHistoryId is the exact inventory posting bridge.",
                        ExactBaseQty = issue.BaseQty,
                        BaseUom = issue.BaseUom,
                        EventDate = issue.MovementDate,
                        IsActiveImpact = state.IsActive && historyState.IsActive
                    }, direction);
                }
            }
            else
            {
                graph.Warn($"Material issue {issue.Uid} has no matching branch-scoped inventory history with the same FromLotId.");
                foreach (var direction in new[] { LotGenealogyDirection.Backward, LotGenealogyDirection.Forward })
                    graph.AddEdge(new LotGenealogyEdge
                    {
                        FromNodeId = lotNodeId,
                        ToNodeId = materialNodeId,
                        Relationship = "Inventory lot to material issue",
                        EvidenceState = historyState.Label,
                        Explanation = "LotId matches, but the exact posted inventory-history bridge is incomplete.",
                        ExactBaseQty = issue.BaseQty,
                        BaseUom = issue.BaseUom,
                        EventDate = issue.MovementDate,
                        IsActiveImpact = false
                    }, direction);
            }
            var exactBalanceMovementId = issue.ProductionBalLotMovementId;
            if (exactBalanceMovementId is long linkedMovementId
                && balanceLot.Uid == issue.ProductionBalLotId)
            {
                var linkedMovement = await db.ProductionBalLotMovements.AsNoTracking()
                    .Where(x => x.Uid == linkedMovementId && x.ProductionBalLotId == balanceLot.Uid)
                    .FirstOrDefaultAsync(cancellationToken);
                if (linkedMovement is null)
                    graph.Warn($"Material issue {issue.Uid} points to missing balance movement {linkedMovementId}.");
                else
                    graph.AddProductionMovementId(linkedMovementId);

                var balanceState = linkedMovement is null
                    ? EvidenceState.Missing
                    : (await GetProductionMovementStatesAsync(db, company, branch, [linkedMovementId], cancellationToken))
                        .GetValueOrDefault(linkedMovementId, EvidenceState.Missing);
                var active = hasExactHistory && state.IsActive && historyState.IsActive && balanceState.IsActive;
                var bridge = CombineEvidence(state, historyState, balanceState, active);
                var balanceNodeId = ProductionMovementNodeId(linkedMovementId);
                graph.AddEdge(new LotGenealogyEdge
                {
                    FromNodeId = materialNodeId,
                    ToNodeId = balanceNodeId,
                    Relationship = "Material issue to production balance",
                    EvidenceState = bridge.Label,
                    Explanation = "ProductionBalLot.OriginalIssueMovementId and ProductionMaterialMovement.ProductionBalLotMovementId are exact IDs.",
                    ExactBaseQty = issue.BaseQty,
                    BaseUom = issue.BaseUom,
                    EventDate = issue.MovementDate,
                    IsActiveImpact = active
                }, LotGenealogyDirection.Backward);
                graph.AddEdge(new LotGenealogyEdge
                {
                    FromNodeId = materialNodeId,
                    ToNodeId = balanceNodeId,
                    Relationship = "Material issue to production balance",
                    EvidenceState = bridge.Label,
                    Explanation = "ProductionBalLot.OriginalIssueMovementId and ProductionMaterialMovement.ProductionBalLotMovementId are exact IDs.",
                    ExactBaseQty = issue.BaseQty,
                    BaseUom = issue.BaseUom,
                    EventDate = issue.MovementDate,
                    IsActiveImpact = active
                }, LotGenealogyDirection.Forward);
                if (state.IsIncomplete || historyState.IsIncomplete || balanceState.IsIncomplete)
                    graph.Warn($"Material issue {issue.Uid}, inventory history, or production movement has incomplete sealed posting evidence.");
            }
            else
            {
                graph.Warn($"Material issue {issue.Uid} has no matching ProductionBalLotId / ProductionBalLotMovementId bridge.");
            }
        }
    }

    private async Task AddMaterialIssueBridgeForLotAsync(
        AppDbContext db,
        string company,
        string branch,
        int lotId,
        long? productionBalanceLotId,
        TraceGraph graph,
        CancellationToken cancellationToken)
    {
        var query = db.ProductionMaterialMovements.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.LotId == lotId && x.MovementType == ProductionMaterialMovementTypes.Issue);
        if (productionBalanceLotId is long balanceLotId)
            query = query.Where(x => x.ProductionBalLotId == balanceLotId);
        var lotIds = await query.Select(x => x.ProductionBalLotId).Where(x => x != null)
            .Select(x => x!.Value).Distinct().Take(MaxNodes).ToListAsync(cancellationToken);
        if (lotIds.Count == 0)
            return;
        var balanceLots = await db.ProductionBalLots.AsNoTracking()
            .Where(x => lotIds.Contains(x.Uid) && x.CompanyCode == company && x.BranchCode == branch)
            .ToListAsync(cancellationToken);
        await AddMaterialIssueBridgesForBalanceLotsAsync(db, company, branch, balanceLots, graph, cancellationToken);
    }

    private async Task<IReadOnlyList<ProductionBalLotMovement>> LoadProductionMovementsAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<long> movementIds,
        CancellationToken cancellationToken)
    {
        if (movementIds.Count == 0)
            return [];

        var rows = new List<ProductionBalLotMovement>();
        foreach (var batch in Chunks(movementIds.Distinct().ToArray()))
        {
            rows.AddRange(await (from movement in db.ProductionBalLotMovements.AsNoTracking()
                join lot in db.ProductionBalLots.AsNoTracking() on movement.ProductionBalLotId equals lot.Uid
                where batch.Contains(movement.Uid)
                    && lot.CompanyCode == company && lot.BranchCode == branch
                select movement).ToListAsync(cancellationToken));
        }
        return rows;
    }

    private async Task<Dictionary<long, EvidenceState>> GetProductionMovementStatesAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<long> movementIds,
        CancellationToken cancellationToken)
    {
        var movements = await LoadProductionMovementsAsync(db, company, branch, movementIds, cancellationToken);
        if (movements.Count == 0)
            return [];

        var originalIds = movements.Where(x => x.OriginalMovementId is null).Select(x => x.Uid).ToArray();
        var reversing = await LoadReversingProductionMovementsAsync(db, company, branch, originalIds, cancellationToken);
        var all = movements.Concat(reversing).ToArray();
        var postingStates = await GetPostingStatesAsync(db, company, branch,
            all.Where(x => x.StockPostingId is not null).Select(x => x.StockPostingId!.Value).Distinct().ToArray(), cancellationToken);
        var reversalsByMovement = reversing.Where(x => x.OriginalMovementId is not null)
            .GroupBy(x => x.OriginalMovementId!.Value).ToDictionary(x => x.Key, x => x.ToArray());
        var result = new Dictionary<long, EvidenceState>();
        foreach (var movement in movements)
        {
            var isReversal = movement.OriginalMovementId is not null || IsReversalMovementType(movement.MovementType);
            var current = ClassifyPostedFact(movement.StockPostingId, postingStates,
                isReversal ? "Reversal movement" : "Active production movement", isReversal);
            if (!isReversal && reversalsByMovement.TryGetValue(movement.Uid, out var reversingRows))
            {
                var reverseStates = reversingRows.Select(x => ClassifyPostedFact(x.StockPostingId, postingStates,
                    "Reversal movement", true)).ToArray();
                if (reverseStates.Any(x => x.IsActive))
                    current = EvidenceState.Reversed;
                else if (reverseStates.Any(x => x.IsIncomplete))
                    current = EvidenceState.Incomplete("Production reversal posting is not sealed or is missing.");
            }
            result[movement.Uid] = current;
        }
        return result;
    }

    private static async Task<IReadOnlyList<ProductionBalLotMovement>> LoadReversingProductionMovementsAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<long> originalMovementIds,
        CancellationToken cancellationToken)
    {
        if (originalMovementIds.Count == 0)
            return [];

        var result = new List<ProductionBalLotMovement>();
        foreach (var batch in Chunks(originalMovementIds))
        {
            result.AddRange(await (from movement in db.ProductionBalLotMovements.AsNoTracking()
                join lot in db.ProductionBalLots.AsNoTracking() on movement.ProductionBalLotId equals lot.Uid
                where movement.OriginalMovementId != null && batch.Contains(movement.OriginalMovementId.Value)
                    && lot.CompanyCode == company && lot.BranchCode == branch
                select movement).ToListAsync(cancellationToken));
        }
        return result;
    }

    private async Task<Dictionary<long, EvidenceState>> GetProductionMaterialStatesAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyList<ProductionMaterialMovement> movements,
        CancellationToken cancellationToken)
    {
        if (movements.Count == 0)
            return [];

        var originalIds = movements.Where(x => x.ReversesMaterialMovementId is null && x.OriginalMovementId is null)
            .Select(x => x.Uid).ToArray();
        var reversals = new List<ProductionMaterialMovement>();
        foreach (var batch in Chunks(originalIds))
        {
            reversals.AddRange(await db.ProductionMaterialMovements.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch
                    && (x.ReversesMaterialMovementId != null && batch.Contains(x.ReversesMaterialMovementId.Value)
                        || x.OriginalMovementId != null && batch.Contains(x.OriginalMovementId.Value)))
                .ToListAsync(cancellationToken));
        }
        var postingStates = await GetPostingStatesAsync(db, company, branch,
            movements.Concat(reversals).Where(x => x.StockPostingId is not null)
                .Select(x => x.StockPostingId!.Value).Distinct().ToArray(), cancellationToken);
        var reversalsByMovement = reversals.Select(x => (Movement: x,
                OriginalId: x.ReversesMaterialMovementId ?? x.OriginalMovementId))
            .Where(x => x.OriginalId is not null)
            .GroupBy(x => x.OriginalId!.Value).ToDictionary(x => x.Key, x => x.Select(y => y.Movement).ToArray());

        var result = new Dictionary<long, EvidenceState>();
        foreach (var movement in movements)
        {
            var isReversal = movement.ReversesMaterialMovementId is not null
                || movement.OriginalMovementId is not null
                || movement.MovementType is ProductionMaterialMovementTypes.IssueReversal
                    or ProductionMaterialMovementTypes.ConsumeReversal;
            var current = ClassifyPostedFact(movement.StockPostingId, postingStates,
                isReversal ? "Reversal material movement" : "Active material movement", isReversal);
            if (!isReversal && reversalsByMovement.TryGetValue(movement.Uid, out var reversingRows))
            {
                var reverseStates = reversingRows.Select(x => ClassifyPostedFact(x.StockPostingId, postingStates,
                    "Reversal material movement", true)).ToArray();
                if (reverseStates.Any(x => x.IsActive))
                    current = EvidenceState.Reversed;
                else if (reverseStates.Any(x => x.IsIncomplete))
                    current = EvidenceState.Incomplete("Material reversal posting is not sealed or is missing.");
            }
            result[movement.Uid] = current;
        }
        return result;
    }

    private async Task<Dictionary<int, EvidenceState>> GetInventoryHistoryStatesAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<IvTrxHistory> histories,
        CancellationToken cancellationToken)
    {
        if (histories.Count == 0)
            return [];

        var ids = histories.Select(x => x.Id).ToArray();
        var reversing = new List<IvTrxHistory>();
        foreach (var batch in Chunks(ids))
        {
            reversing.AddRange(await db.IvTrxHistories.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch
                    && x.ReversesHistoryId != null && batch.Contains(x.ReversesHistoryId.Value))
                .ToListAsync(cancellationToken));
        }
        var postingStates = await GetPostingStatesAsync(db, company, branch,
            histories.Concat(reversing).Where(x => x.StockPostingId is not null)
                .Select(x => x.StockPostingId!.Value).Distinct().ToArray(), cancellationToken);
        var reversalByHistory = reversing.Where(x => x.ReversesHistoryId is not null)
            .GroupBy(x => x.ReversesHistoryId!.Value).ToDictionary(x => x.Key, x => x.ToArray());

        var result = new Dictionary<int, EvidenceState>();
        foreach (var history in histories)
        {
            if (history.ReversesHistoryId is not null)
            {
                result[history.Id] = ClassifyPostedFact(history.StockPostingId, postingStates,
                    "Reversal inventory history", true);
                continue;
            }

            var current = history.StockPostingId is long postingId
                ? postingStates.GetValueOrDefault(postingId, EvidenceState.Missing)
                : string.Equals(history.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase)
                    ? EvidenceState.Legacy("Legacy exact inventory history")
                    : EvidenceState.Incomplete("Inventory history has no sealed posting evidence.");
            if (reversalByHistory.TryGetValue(history.Id, out var rows))
            {
                var reverseStates = rows.Select(x => x.StockPostingId is long reversePostingId
                    ? postingStates.GetValueOrDefault(reversePostingId, EvidenceState.Missing)
                    : EvidenceState.Incomplete("Inventory reversal has no posting identity.")).ToArray();
                if (reverseStates.Any(x => x.IsActive))
                    current = EvidenceState.Reversed;
                else if (reverseStates.Any(x => x.IsIncomplete))
                    current = EvidenceState.Incomplete("Inventory reversal posting is not sealed or is missing.");
            }
            result[history.Id] = current;
        }
        return result;
    }

    private async Task<Dictionary<long, EvidenceState>> GetPostingStatesAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<long> postingIds,
        CancellationToken cancellationToken)
    {
        if (postingIds.Count == 0)
            return [];

        var ids = postingIds.Distinct().ToArray();
        var postings = new List<ErpWeb.Model.Entities.StockLedger.StockPosting>();
        foreach (var batch in Chunks(ids))
        {
            postings.AddRange(await db.StockPostings.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch
                    && (batch.Contains(x.Id) || (x.ReversesPostingId != null && batch.Contains(x.ReversesPostingId.Value))))
                .ToListAsync(cancellationToken));
        }

        var result = new Dictionary<long, EvidenceState>();
        foreach (var id in ids)
        {
            var original = postings.FirstOrDefault(x => x.Id == id);
            if (original is null)
            {
                result[id] = EvidenceState.Missing;
                continue;
            }
            if (original.ReversesPostingId is not null)
            {
                result[id] = original.SealedAtUtc is null
                    ? EvidenceState.Incomplete("Reversal posting is unsealed.")
                    : EvidenceState.Reversal("Sealed reversal posting");
                continue;
            }
            if (original.SealedAtUtc is null)
            {
                result[id] = EvidenceState.Incomplete("Stock posting is unsealed.");
                continue;
            }

            var reversals = postings.Where(x => x.ReversesPostingId == id).ToArray();
            if (reversals.Any(x => x.SealedAtUtc is not null))
                result[id] = EvidenceState.Reversed;
            else if (reversals.Length > 0)
                result[id] = EvidenceState.Incomplete("An unsealed reversal posting makes the evidence ambiguous.");
            else
                result[id] = EvidenceState.Active("Sealed stock posting");
        }
        return result;
    }

    private static async Task<IReadOnlyList<ProductionPoolDependency>> LoadReversingDependenciesAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<long> dependencyIds,
        CancellationToken cancellationToken)
    {
        if (dependencyIds.Count == 0)
            return [];

        var result = new List<ProductionPoolDependency>();
        foreach (var batch in Chunks(dependencyIds))
        {
            result.AddRange(await (from edge in db.ProductionPoolDependencyRows.AsNoTracking()
                join posting in db.StockPostings.AsNoTracking() on edge.StockPostingId equals posting.Id
                where edge.ReversesDependencyId != null && batch.Contains(edge.ReversesDependencyId.Value)
                    && posting.CompanyCode == company && posting.BranchCode == branch
                select edge).ToListAsync(cancellationToken));
        }
        return result;
    }

    private async Task<IReadOnlyList<ProductionMovementAllocation>> LoadAllocationsForDependenciesAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<ProductionPoolDependency> dependencies,
        LotGenealogyDirection direction,
        CancellationToken cancellationToken)
    {
        var movementIds = dependencies.Select(x => direction == LotGenealogyDirection.Forward
            ? x.ContributorMovementId : x.ConsumerMovementId).Distinct().ToArray();
        if (movementIds.Length == 0)
            return [];

        var result = new List<ProductionMovementAllocation>();
        foreach (var batch in Chunks(movementIds))
        {
            var query = db.ProductionMovementAllocations.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch
                    && (direction == LotGenealogyDirection.Forward
                        ? batch.Contains(x.ReceiptMovementId)
                        : batch.Contains(x.OutboundMovementId)));
            result.AddRange(await query.Take(MaxNodes + 1).ToListAsync(cancellationToken));
        }
        return result.Take(MaxNodes + 1).ToArray();
    }

    private static async Task<IReadOnlyList<ProductionMovementAllocation>> LoadReversingAllocationsAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<long> allocationIds,
        CancellationToken cancellationToken)
    {
        if (allocationIds.Count == 0)
            return [];

        var result = new List<ProductionMovementAllocation>();
        foreach (var batch in Chunks(allocationIds))
        {
            result.AddRange(await db.ProductionMovementAllocations.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch
                    && x.ReversesAllocationId != null && batch.Contains(x.ReversesAllocationId.Value))
                .ToListAsync(cancellationToken));
        }
        return result;
    }

    private async Task AddCostEvidenceAsync(
        AppDbContext db,
        string company,
        string branch,
        TraceGraph graph,
        CancellationToken cancellationToken)
    {
        var movementIds = graph.ProductionMovementIds.ToArray();
        if (movementIds.Length == 0)
            return;

        var evidence = new List<ProductionValuationEvidence>();
        foreach (var batch in Chunks(movementIds))
        {
            evidence.AddRange(await (from row in db.ProductionValuationEvidenceRows.AsNoTracking()
                join movement in db.ProductionBalLotMovements.AsNoTracking() on row.MovementId equals movement.Uid
                join lot in db.ProductionBalLots.AsNoTracking() on movement.ProductionBalLotId equals lot.Uid
                where batch.Contains(row.MovementId) && lot.CompanyCode == company && lot.BranchCode == branch
                select row).ToListAsync(cancellationToken));
        }
        var byMovement = evidence.GroupBy(x => x.MovementId).ToDictionary(x => x.Key, x => x.First());
        foreach (var id in movementIds)
        {
            if (byMovement.TryGetValue(id, out var row))
            {
                graph.AddCostEvidence(new LotGenealogyCostEvidence
                {
                    MovementId = row.MovementId,
                    MovementType = graph.GetMovementType(row.MovementId) ?? "Production movement",
                    Status = row.Status,
                    Basis = row.Basis
                });
            }
            else
            {
                graph.AddCostEvidence(new LotGenealogyCostEvidence
                {
                    MovementId = id,
                    MovementType = graph.GetMovementType(id) ?? "Production movement",
                    Status = "LEGACY / NO EVIDENCE",
                    Basis = "No immutable valuation evidence row is available."
                });
            }
        }
    }

    private async Task AddFinishedGoodOutputsForMovementIdsAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<long> movementIds,
        TraceGraph graph,
        CancellationToken cancellationToken)
    {
        if (movementIds.Count == 0)
            return;

        var facts = new List<ProductionFinishedGoodFact>();
        foreach (var batch in Chunks(movementIds.Distinct().ToArray()))
        {
            facts.AddRange(await (from fact in db.ProductionFinishedGoodFactRows.AsNoTracking()
                join posting in db.StockPostings.AsNoTracking() on fact.StockPostingId equals posting.Id
                where batch.Contains(fact.ProductionMovementId)
                    && posting.CompanyCode == company && posting.BranchCode == branch
                select fact).Take(MaxNodes + 1).ToListAsync(cancellationToken));
        }
        if (facts.Count > MaxNodes)
        {
            facts = facts.Take(MaxNodes).ToList();
            graph.MarkTruncated("FG output facts exceeded the trace node limit.");
        }
        if (facts.Count == 0)
            return;

        var reverseFacts = await LoadReversingFinishedGoodFactsAsync(db, company, branch,
            facts.Where(x => x.ReversesFactId is null).Select(x => x.Id).ToArray(), cancellationToken);
        var postingStates = await GetPostingStatesAsync(db, company, branch,
            facts.Select(x => x.StockPostingId).Concat(reverseFacts.Select(x => x.StockPostingId)).Distinct().ToArray(), cancellationToken);
        var reversalsByFact = reverseFacts.Where(x => x.ReversesFactId is not null)
            .GroupBy(x => x.ReversesFactId!.Value).ToDictionary(x => x.Key, x => x.ToArray());
        var movementStates = await GetProductionMovementStatesAsync(db, company, branch,
            facts.Select(x => x.ProductionMovementId).Distinct().ToArray(), cancellationToken);
        var histories = new List<IvTrxHistory>();
        var historyIds = facts.Select(x => x.InventoryHistoryId).Distinct().ToArray();
        foreach (var batch in Chunks(historyIds))
        {
            histories.AddRange(await db.IvTrxHistories.AsNoTracking()
                .Where(x => batch.Contains(x.Id) && x.CompanyCode == company && x.BranchCode == branch)
                .ToListAsync(cancellationToken));
        }
        var historyById = histories.ToDictionary(x => x.Id);
        var historyStates = await GetInventoryHistoryStatesAsync(db, company, branch, histories, cancellationToken);
        var inventoryLotIds = histories.Where(x => x.ToLotId is not null).Select(x => x.ToLotId!.Value).Distinct().ToArray();
        var inventoryLots = new Dictionary<int, IvLot>();
        foreach (var batch in Chunks(inventoryLotIds))
        {
            var rows = await db.IvLots.AsNoTracking()
                .Where(x => x.CompanyCode == company && batch.Contains(x.Id))
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
                inventoryLots[row.Id] = row;
        }
        var itemCodes = inventoryLots.Values.Select(x => x.ICode).Distinct().ToArray();
        var itemInfo = new Dictionary<string, (string? Description, string? Uom)>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in Chunks(itemCodes))
        {
            var rows = await db.IvStockMasters.AsNoTracking()
                .Where(x => x.CompanyCode == company && batch.Contains(x.ICode))
                .Select(x => new { x.ICode, x.IDesc, x.StdUom })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
                itemInfo[row.ICode] = (row.IDesc, row.StdUom);
        }

        foreach (var fact in facts)
        {
            if (!historyById.TryGetValue(fact.InventoryHistoryId, out var history))
            {
                graph.Warn($"FG fact {fact.Id} points to missing inventory history {fact.InventoryHistoryId} in this branch.");
                continue;
            }
            if (history.ToLotId is not int lotId || !inventoryLots.TryGetValue(lotId, out var inventoryLot))
            {
                graph.Warn($"FG fact {fact.Id} has no exact destination IvLot.Id in its linked inventory history.");
                continue;
            }

            var factState = ClassifyFinishedGoodFact(fact, postingStates,
                reversalsByFact.GetValueOrDefault(fact.Id) ?? []);
            var hasExactHistory = IsExactFinishedGoodHistory(fact, history);
            var historyState = hasExactHistory
                ? historyStates.GetValueOrDefault(history.Id, EvidenceState.Missing)
                : EvidenceState.Incomplete("FG fact does not match its branch-scoped Finished Goods inventory history.");
            var movementState = movementStates.GetValueOrDefault(fact.ProductionMovementId, EvidenceState.Missing);
            var movementEdgeState = CombineEvidence(factState, movementState);
            var historyEdgeState = CombineEvidence(factState, historyState);
            var activeImpact = hasExactHistory && factState.IsActive && historyState.IsActive && movementState.IsActive;
            graph.AddNode(new LotGenealogyNode
            {
                Id = FinishedGoodFactNodeId(fact.Id),
                Kind = "Finished Good receipt fact",
                Title = $"FG fact {fact.Id}",
                Detail = $"Batch {fact.BatchId} · source {fact.SourceId} · history {fact.InventoryHistoryId}",
                EvidenceState = CombineEvidence(factState, historyState, movementState).Label,
                BranchCode = branch,
                EventDate = history.TrxDtTime,
                BaseQty = fact.BaseQty,
                BaseUom = history.ToStdUom
            });
            graph.AddNode(InventoryHistoryNode(history, historyState, branch));
            var item = itemInfo.GetValueOrDefault(inventoryLot.ICode);
            graph.AddNode(InventoryLotNode(inventoryLot, item.Description, item.Uom, branch, true));
            graph.AddFinishedGoodLotId(inventoryLot.Id, activeImpact);

            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = ProductionMovementNodeId(fact.ProductionMovementId),
                ToNodeId = FinishedGoodFactNodeId(fact.Id),
                Relationship = "Production to FG receipt fact",
                EvidenceState = movementEdgeState.Label,
                Explanation = "ProductionFinishedGoodFact contains the exact ProductionMovementId.",
                ExactBaseQty = fact.BaseQty,
                BaseUom = history.ToStdUom,
                EventDate = history.TrxDtTime,
                IsActiveImpact = movementEdgeState.IsActive
            }, LotGenealogyDirection.Forward);
            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = FinishedGoodFactNodeId(fact.Id),
                ToNodeId = InventoryHistoryNodeId(history.Id),
                Relationship = "FG fact to inventory history",
                EvidenceState = historyEdgeState.Label,
                Explanation = "ProductionFinishedGoodFact.InventoryHistoryId is the exact posted inventory boundary.",
                ExactBaseQty = fact.BaseQty,
                BaseUom = history.ToStdUom,
                EventDate = history.TrxDtTime,
                IsActiveImpact = historyEdgeState.IsActive
            }, LotGenealogyDirection.Forward);
            graph.AddEdge(new LotGenealogyEdge
            {
                FromNodeId = InventoryHistoryNodeId(history.Id),
                ToNodeId = InventoryLotNodeId(inventoryLot.Id),
                Relationship = "FG history to inventory lot",
                EvidenceState = historyState.Label,
                Explanation = "IvTrxHistory.ToLotId resolves the exact finished-goods IvLot.Id.",
                ExactBaseQty = history.ToStdQty,
                BaseUom = history.ToStdUom,
                EventDate = history.TrxDtTime,
                IsActiveImpact = historyState.IsActive
            }, LotGenealogyDirection.Forward);

            if (!hasExactHistory)
                graph.Warn($"FG fact {fact.Id} does not match the type or StockPostingId of inventory history {history.Id}.");
            if (factState.IsIncomplete || historyState.IsIncomplete || movementState.IsIncomplete)
                graph.Warn($"FG receipt fact {fact.Id}, inventory history {history.Id}, or production movement {fact.ProductionMovementId} has incomplete sealed evidence.");
            else if (factState.IsActive && !movementState.IsActive)
                graph.Warn($"Production movement {fact.ProductionMovementId} linked from FG fact {fact.Id} has no sealed active posting evidence.");
        }
    }

    private static LotGenealogyNode InventoryLotNode(
        IvLot lot,
        string? description,
        string? uom,
        string branch,
        bool isFinishedGood) => new()
    {
        Id = InventoryLotNodeId(lot.Id),
        Kind = isFinishedGood ? "Finished Good inventory lot" : "Inventory lot",
        Title = $"{lot.ICode} / {lot.LotNo}",
        Detail = $"{description ?? lot.SourceType ?? "Inventory lot"} · IvLot is company-wide; balance and history shown are for branch {branch}.",
        EvidenceState = string.IsNullOrWhiteSpace(lot.SupplierCode) ? "Inventory lot master" : $"Supplier snapshot {lot.SupplierCode}",
        BranchCode = branch,
        EventDate = lot.ReceiptDate,
        BaseUom = uom,
        IsFinishedGoodLot = isFinishedGood
    };

    private static LotGenealogyNode ProductionLotNode(ProductionBalLot lot, string branch) => new()
    {
        Id = ProductionLotNodeId(lot.Uid),
        Kind = "Production balance lot",
        Title = $"{lot.ItemCode} / {lot.PhysicalLotNo ?? lot.LotNo}",
        Detail = $"{lot.Kind} · source owner WO {lot.WorkOrderNo} · pool {lot.PoolCode ?? "—"}",
        EvidenceState = "Current production balance identity",
        BranchCode = branch,
        EventDate = lot.LastMovementDate,
        BaseQty = lot.BaseQty,
        BaseUom = lot.BaseUom
    };

    private static LotGenealogyNode ProductionMovementNode(
        ProductionBalLotMovement movement,
        ProductionBalLot lot,
        EvidenceState state,
        string branch,
        string? workOrderNo = null) => new()
    {
        Id = ProductionMovementNodeId(movement.Uid),
        Kind = "Production movement",
        Title = $"{movement.MovementType} · {movement.DocumentType} {movement.DocumentNo}",
        Detail = $"Item {movement.ItemCode ?? lot.ItemCode} · movement WO {workOrderNo ?? movement.WorkOrderNo ?? movement.WorkOrderId.ToString()} · source lot owner WO {lot.WorkOrderNo} · route {movement.RouteStepId?.ToString() ?? "—"} · operation {movement.WorkOrderOperationId?.ToString() ?? "—"} · lot {movement.PhysicalLotNo ?? lot.PhysicalLotNo ?? lot.LotNo}",
        EvidenceState = state.Label,
        BranchCode = movement.BranchCode ?? branch,
        EventDate = movement.MovementDate,
        BaseQty = movement.BaseQty,
        BaseUom = movement.BaseUom
    };

    private static LotGenealogyNode MaterialMovementNode(
        ProductionMaterialMovement movement,
        EvidenceState state,
        string branch) => new()
    {
        Id = MaterialMovementNodeId(movement.Uid),
        Kind = movement.MovementType == ProductionMaterialMovementTypes.Issue ? "Production material issue" : "Production material movement",
        Title = $"{movement.MovementType} · {movement.ItemCode} / {movement.LotNo}",
        Detail = $"Production movement {movement.Uid} · work-order ID {movement.WorkOrderId} · lot ID {movement.LotId?.ToString() ?? "legacy/unavailable"}",
        EvidenceState = state.Label,
        BranchCode = movement.BranchCode,
        EventDate = movement.MovementDate,
        BaseQty = movement.BaseQty,
        BaseUom = movement.BaseUom
    };

    private static LotGenealogyNode InventoryHistoryNode(
        IvTrxHistory history,
        EvidenceState state,
        string branch) => new()
    {
        Id = InventoryHistoryNodeId(history.Id),
        Kind = "Inventory history",
        Title = $"{history.TrxType} · {history.RefNo ?? history.BatchNo.ToString()}",
        Detail = $"History {history.Id} · {history.ICode} · DO {history.DoNo ?? "—"} · PO {history.PoNo ?? "—"}/{history.PoRelNo?.ToString() ?? "—"}",
        EvidenceState = state.Label,
        BranchCode = branch,
        EventDate = history.TrxDtTime,
        BaseQty = history.ToLotId is not null ? history.ToStdQty : history.FrStdQty,
        BaseUom = history.ToLotId is not null ? history.ToStdUom : history.FrStdUom
    };

    private static bool IsProductionReceiptMovement(string movementType) => movementType is
        ProductionBalLotMovementTypes.Issue
        or ProductionBalLotMovementTypes.Produce
        or ProductionBalLotMovementTypes.OpeningIn
        or ProductionBalLotMovementTypes.TransferIn
        or ProductionBalLotMovementTypes.Return
        or ProductionBalLotMovementTypes.AdjustIn;

    private static bool IsReversalMovementType(string movementType) =>
        movementType.EndsWith("REVERSAL", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<IReadOnlyCollection<T>> Chunks<T>(IReadOnlyCollection<T> values)
    {
        for (var i = 0; i < values.Count; i += BatchSize)
        {
            var length = Math.Min(BatchSize, values.Count - i);
            yield return values.Skip(i).Take(length).ToArray();
        }
    }

    private static string InventoryLotNodeId(int id) => $"inventory-lot:{id}";
    private static string ProductionLotNodeId(long id) => $"production-lot:{id}";
    private static string ProductionMovementNodeId(long id) => $"production-movement:{id}";
    private static string MaterialMovementNodeId(long id) => $"material-movement:{id}";
    private static string InventoryHistoryNodeId(int id) => $"inventory-history:{id}";
    private static string FinishedGoodFactNodeId(long id) => $"fg-fact:{id}";
    private static string WorkOrderNodeId(long id) => $"work-order:{id}";
    private static string SupplierNodeId(string company, string code) => $"supplier:{company}:{code}";
    private static string CustomerNodeId(string company, string code) => $"customer:{company}:{code}";
    private static string PurchaseOrderNodeId(string company, string branch, string poNo, short release) =>
        $"purchase-order:{company}:{branch}:{poNo}:{release}";
    private static string DeliveryOrderNodeId(string company, string branch, string doNo) =>
        $"delivery-order:{company}:{branch}:{doNo}";

    private static EvidenceState ClassifyPostedFact(
        long? stockPostingId,
        IReadOnlyDictionary<long, EvidenceState> postingStates,
        string label,
        bool isReversal)
    {
        if (stockPostingId is not long id)
            return isReversal
                ? EvidenceState.Incomplete($"{label} has no posting identity.")
                : EvidenceState.Legacy($"{label} has no V2 posting identity.");
        var state = postingStates.GetValueOrDefault(id, EvidenceState.Missing);
        if (isReversal)
        {
            if (state.IsIncomplete || state == EvidenceState.Missing)
                return EvidenceState.Incomplete($"{label} is missing a sealed reversal posting.");
            return EvidenceState.Reversal(label);
        }
        return state;
    }

    private static EvidenceState ClassifyFinishedGoodFact(
        ProductionFinishedGoodFact fact,
        IReadOnlyDictionary<long, EvidenceState> postingStates,
        IReadOnlyList<ProductionFinishedGoodFact> reversingFacts)
    {
        if (fact.ReversesFactId is not null)
            return ClassifyPostedFact(fact.StockPostingId, postingStates, "Reversal FG fact", true);

        var current = ClassifyPostedFact(fact.StockPostingId, postingStates, "FG fact", false);
        if (reversingFacts.Count == 0)
            return current;

        var states = reversingFacts.Select(x => ClassifyPostedFact(x.StockPostingId, postingStates,
            "Reversal FG fact", true)).ToArray();
        if (states.Any(x => x.IsReversal && !x.IsIncomplete))
            return EvidenceState.Reversed;
        if (states.Any(x => x.IsIncomplete))
            return EvidenceState.Incomplete("A linked FG reversal fact is unsealed or incomplete.");
        return current;
    }

    private static bool IsExactFinishedGoodHistory(ProductionFinishedGoodFact fact, IvTrxHistory history) =>
        fact.InventoryHistoryId == history.Id
        && fact.StockPostingId == history.StockPostingId
        && string.Equals(history.TrxType, IvTrxTypes.FinishedGoods, StringComparison.OrdinalIgnoreCase)
        && history.ToLotId is not null;

    private static EvidenceState ClassifyDependency(
        ProductionPoolDependency dependency,
        IReadOnlyDictionary<long, EvidenceState> postingStates,
        IReadOnlyList<ProductionPoolDependency> reversingRows)
    {
        if (dependency.ReversesDependencyId is not null)
            return ClassifyReversalRecord(dependency.StockPostingId, postingStates, "Reversal dependency");

        var current = postingStates.GetValueOrDefault(dependency.StockPostingId, EvidenceState.Missing);
        if (reversingRows.Count == 0)
            return current;
        var reversals = reversingRows.Select(x => ClassifyReversalRecord(x.StockPostingId, postingStates,
            "Reversal dependency")).ToArray();
        if (reversals.Any(x => x.IsReversal && !x.IsIncomplete))
            return EvidenceState.Reversed;
        if (reversals.Any(x => x.IsIncomplete))
            return EvidenceState.Incomplete("A linked dependency reversal is unsealed or incomplete.");
        return current;
    }

    private static EvidenceState ClassifyAllocation(
        ProductionMovementAllocation allocation,
        IReadOnlyDictionary<long, EvidenceState> postingStates,
        IReadOnlyList<ProductionMovementAllocation> reversingRows)
    {
        if (allocation.ReversesAllocationId is not null)
            return ClassifyReversalRecord(allocation.StockPostingId, postingStates, "Reversal allocation");

        var current = postingStates.GetValueOrDefault(allocation.StockPostingId, EvidenceState.Missing);
        if (reversingRows.Count == 0)
            return current;
        var reversals = reversingRows.Select(x => ClassifyReversalRecord(x.StockPostingId, postingStates,
            "Reversal allocation")).ToArray();
        if (reversals.Any(x => x.IsReversal && !x.IsIncomplete))
            return EvidenceState.Reversed;
        if (reversals.Any(x => x.IsIncomplete))
            return EvidenceState.Incomplete("A linked allocation reversal is unsealed or incomplete.");
        return current;
    }

    private static EvidenceState ClassifyReversalRecord(
        long? stockPostingId,
        IReadOnlyDictionary<long, EvidenceState> postingStates,
        string label)
    {
        if (stockPostingId is not long id)
            return EvidenceState.Incomplete($"{label} has no posting identity.");
        var state = postingStates.GetValueOrDefault(id, EvidenceState.Missing);
        if (state.IsIncomplete || state == EvidenceState.Missing)
            return EvidenceState.Incomplete($"{label} is unsealed or missing.");
        return EvidenceState.Reversal(label);
    }

    private static EvidenceState CombineEvidence(params EvidenceState[] states)
    {
        if (states.Any(x => x.IsIncomplete || x == EvidenceState.Missing))
            return EvidenceState.Incomplete("One or more linked evidence rows are legacy, unsealed, or missing.");
        if (states.Any(x => x.Label == EvidenceState.Reversed.Label || x.IsReversal))
            return EvidenceState.Reversed;
        return states.All(x => x.IsActive)
            ? EvidenceState.Active("Sealed linked evidence")
            : EvidenceState.Incomplete("One or more linked evidence rows are not active.");
    }

    private static EvidenceState CombineEvidence(EvidenceState first, EvidenceState second, bool active) =>
        active ? EvidenceState.Active("Sealed linked evidence") : CombineEvidence(first, second);

    private static EvidenceState CombineEvidence(
        EvidenceState first,
        EvidenceState second,
        EvidenceState third,
        bool active) =>
        active ? EvidenceState.Active("Sealed linked evidence") : CombineEvidence(first, second, third);

    private readonly record struct EvidenceState(
        string Label,
        bool IsActive,
        bool IsIncomplete,
        bool IsReversal = false)
    {
        public static EvidenceState Missing { get; } = new("Missing evidence", false, true);
        public static EvidenceState Reversed { get; } = new("Reversed", false, false);
        public static EvidenceState Active(string label) => new(label, true, false);
        public static EvidenceState Incomplete(string label) => new(label, false, true);
        public static EvidenceState Legacy(string label) => new(label, false, true);
        public static EvidenceState Reversal(string label) => new(label, false, false, true);
    }

    private sealed class TraceGraph
    {
        private readonly Dictionary<string, LotGenealogyNode> _nodes = new(StringComparer.Ordinal);
        private readonly List<LotGenealogyEdge> _backwardEdges = [];
        private readonly List<LotGenealogyEdge> _forwardEdges = [];
        private readonly HashSet<string> _backwardEdgeKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _forwardEdgeKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _warnings = new(StringComparer.Ordinal);
        private readonly HashSet<long> _movementIds = [];
        private readonly HashSet<int> _finishedGoodLotIds = [];
        private readonly HashSet<int> _activeFinishedGoodLotIds = [];
        private readonly Dictionary<long, LotGenealogyCostEvidence> _costEvidence = [];

        public TraceGraph(LotGenealogyNode root, string branch)
        {
            Root = root;
            CurrentBranch = branch;
            AddNode(root);
            if (root.IsFinishedGoodLot
                && root.Id.StartsWith("inventory-lot:", StringComparison.Ordinal)
                && int.TryParse(root.Id["inventory-lot:".Length..], out var rootLotId))
                _activeFinishedGoodLotIds.Add(rootLotId);
        }

        public LotGenealogyNode Root { get; }
        public string CurrentBranch { get; }
        public bool WasTruncated { get; private set; }
        public IReadOnlyCollection<long> ProductionMovementIds => _movementIds;
        public IReadOnlyCollection<int> FinishedGoodLotIds => _finishedGoodLotIds;

        public bool HasNode(string id) => _nodes.ContainsKey(id);

        public void AddNode(LotGenealogyNode node)
        {
            if (_nodes.TryGetValue(node.Id, out var current))
            {
                if (node.IsFinishedGoodLot && !current.IsFinishedGoodLot
                    || node.IsCustomer && !current.IsCustomer)
                {
                    _nodes[node.Id] = new LotGenealogyNode
                    {
                        Id = current.Id,
                        Kind = node.IsFinishedGoodLot ? node.Kind : current.Kind,
                        Title = current.Title,
                        Detail = current.Detail ?? node.Detail,
                        EvidenceState = current.EvidenceState ?? node.EvidenceState,
                        BranchCode = current.BranchCode ?? node.BranchCode,
                        EventDate = current.EventDate ?? node.EventDate,
                        BaseQty = current.BaseQty ?? node.BaseQty,
                        BaseUom = current.BaseUom ?? node.BaseUom,
                        IsFinishedGoodLot = current.IsFinishedGoodLot || node.IsFinishedGoodLot,
                        IsCustomer = current.IsCustomer || node.IsCustomer
                    };
                }
            }
            else
                _nodes[node.Id] = node;

            if (node.IsFinishedGoodLot && node.Id.StartsWith("inventory-lot:", StringComparison.Ordinal)
                && int.TryParse(node.Id["inventory-lot:".Length..], out var lotId))
                _finishedGoodLotIds.Add(lotId);
            if (node.Kind == "Production movement"
                && node.Id.StartsWith("production-movement:", StringComparison.Ordinal)
                && long.TryParse(node.Id["production-movement:".Length..], out var movementId))
                _movementIds.Add(movementId);
        }

        public void AddEdge(LotGenealogyEdge edge, LotGenealogyDirection direction)
        {
            var key = $"{edge.FromNodeId}\u001f{edge.ToNodeId}\u001f{edge.Relationship}\u001f{edge.EvidenceState}\u001f{edge.ExactBaseQty}\u001f{edge.IsActiveImpact}";
            if (direction == LotGenealogyDirection.Backward)
            {
                if (_backwardEdgeKeys.Add(key))
                    _backwardEdges.Add(edge);
            }
            else if (_forwardEdgeKeys.Add(key))
                _forwardEdges.Add(edge);
        }

        public void AddProductionMovementId(long id) => _movementIds.Add(id);
        public void AddFinishedGoodLotId(int id, bool activeImpact)
        {
            _finishedGoodLotIds.Add(id);
            if (activeImpact)
                _activeFinishedGoodLotIds.Add(id);
        }
        public void AddCostEvidence(LotGenealogyCostEvidence evidence) => _costEvidence[evidence.MovementId] = evidence;

        public string? GetMovementType(long id)
        {
            if (!_nodes.TryGetValue(ProductionMovementNodeId(id), out var node))
                return null;
            var separator = node.Title.IndexOf('·');
            return separator < 0 ? node.Title : node.Title[..separator].Trim();
        }

        public void Warn(string warning)
        {
            if (!string.IsNullOrWhiteSpace(warning))
                _warnings.Add(warning.Trim());
        }

        public void MarkTruncated(string warning)
        {
            WasTruncated = true;
            Warn(warning);
        }

        public LotGenealogyResult Build(decimal? visibleOnHand)
        {
            var completeness = WasTruncated
                ? LotGenealogyCompleteness.Truncated
                : _warnings.Count > 0
                    ? LotGenealogyCompleteness.IncompleteLegacyEvidence
                    : LotGenealogyCompleteness.Complete;
            return new LotGenealogyResult
            {
                Root = Root,
                Completeness = completeness,
                WasTruncated = WasTruncated,
                CurrentBranchOnHandQty = visibleOnHand,
                CurrentBranch = CurrentBranch,
                AffectedFinishedGoodLotCount = _activeFinishedGoodLotIds.Count,
                CustomersShippedCount = _nodes.Values.Count(x => x.IsCustomer),
                Nodes = _nodes.Values.OrderBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToArray(),
                BackwardEdges = _backwardEdges,
                ForwardEdges = _forwardEdges,
                CostEvidence = _costEvidence.Values.OrderBy(x => x.MovementId).ToArray(),
                Warnings = _warnings.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }
    }
}





