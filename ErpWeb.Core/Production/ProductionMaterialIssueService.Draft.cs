using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionMaterialIssueService
{
    public async Task<IvMasterOperationResult<int>> PeekNextBatchNoAsync(CancellationToken cancellationToken = default)
    {
        if (_runningNumbers is null)
            return IvMasterOperationResult<int>.Fail(IvMasterErrorCode.Validation, "Batch numbering is unavailable.");
        if (!await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<int>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");
        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<int>.Fail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return IvMasterOperationResult<int>.Ok(await _runningNumbers.PeekNextAsync(db, scope.CompanyCode, RunningNumberKeys.IvBatch, cancellationToken));
    }

    public Task<IvMasterOperationResult<ProductionMaterialIssueSaveResult>> CreateAsync(
        ProductionMaterialIssueSaveRequest request, CancellationToken cancellationToken = default) =>
        SaveDraftAsync(null, request, PermissionCodes.Add, cancellationToken);

    public Task<IvMasterOperationResult<ProductionMaterialIssueSaveResult>> UpdateAsync(
        int batchNo, ProductionMaterialIssueSaveRequest request, CancellationToken cancellationToken = default) =>
        SaveDraftAsync(batchNo, request, PermissionCodes.Edit, cancellationToken);

    private async Task<IvMasterOperationResult<ProductionMaterialIssueSaveResult>> SaveDraftAsync(
        int? existingBatchNo, ProductionMaterialIssueSaveRequest request, string permission, CancellationToken ct)
    {
        if (_runningNumbers is null)
            return DraftFail(IvMasterErrorCode.Validation, "Batch numbering is unavailable.");
        if (!await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access, ct)
            || !await _access.CanAsync(MenuCodes.PlanningMaterialIssue, permission, ct))
            return DraftFail(IvMasterErrorCode.AccessDenied, "Access denied.");
        var errors = ValidateSaveRequest(request, _clock.Now);
        if (errors.Count > 0)
            return IvMasterOperationResult<ProductionMaterialIssueSaveResult>.Fail(IvMasterErrorCode.Validation, "The draft is invalid.", errors);
        var scope = _tenant.TryWriteScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return DraftFail(IvMasterErrorCode.InvalidScope, "A company, branch and user scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        IvTrxBatch? batch = null;
        ProductionPostingLink? link = null;
        List<ProductionMaterialIssueLine> oldMaps = [];
        if (existingBatchNo.HasValue)
        {
            batch = await LockIssueBatchAsync(db, scope.CompanyCode, scope.BranchCode!, existingBatchNo.Value, ct);
            link = await LockIssueLinkByBatchAsync(db, scope.CompanyCode, scope.BranchCode!, existingBatchNo.Value, ct);
            if (batch is null || link is null)
                return DraftFail(IvMasterErrorCode.NotFound, "Material issue draft was not found.");
            if (batch.BatchStatus != IvBatchStatuses.New || link.Status != ProductionPostingLinkStatuses.Draft)
                return DraftFail(IvMasterErrorCode.Validation, "Only a NEW draft can be edited.");
            oldMaps = await db.ProductionMaterialIssueLines.Where(x => x.PostingLinkId == link.Uid).ToListAsync(ct);
        }

        var order = await LockWorkOrderAsync(db, scope.CompanyCode, scope.BranchCode!, request.WorkOrderNo.Trim(), ct);
        if (order is null)
            return DraftFail(IvMasterErrorCode.NotFound, "Work Order was not found.");
        if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
            return DraftFail(IvMasterErrorCode.Validation, "Work Order status does not allow material issue.");
        if (order.SnapshotRevision != request.SnapshotRevision || order.SnapshotHash != request.SnapshotHash
            || !ProductionSnapshotFormatVersions.IsFullHierarchy(order.SnapshotFormatVersion) || order.IsLegacySnapshot)
            return DraftFail(IvMasterErrorCode.Concurrency, "The Work Order snapshot is stale; recreate the draft.");
        if (existingBatchNo.HasValue && (link!.WorkOrderId != order.Uid
            || link.SnapshotRevision != order.SnapshotRevision || link.SnapshotHash != order.SnapshotHash))
            return DraftFail(IvMasterErrorCode.Concurrency, "The stored draft snapshot no longer matches the Work Order.");
        var operation = await db.ProductionWorkOrderOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == request.WorkOrderOperationId && x.WorkOrderId == order.Uid, ct);
        if (operation is null)
            return DraftFail(IvMasterErrorCode.Validation, "The selected operation does not belong to the Work Order.");
        if (operation.PlannedOutputQty <= 0m)
            return DraftFail(IvMasterErrorCode.Validation, "The selected operation has an invalid planned output quantity.");
        if (request.ProductionQtyThisIssue > operation.PlannedOutputQty)
            return DraftFail(IvMasterErrorCode.Validation, "Desired output quantity cannot exceed operation planned output.");
        var routeSteps = await db.ProductionWorkOrderRouteSteps.AsNoTracking()
            .Where(x => x.WorkOrderId == order.Uid).ToListAsync(ct);
        var operations = await db.ProductionWorkOrderOperations.AsNoTracking()
            .Where(x => x.WorkOrderId == order.Uid).ToListAsync(ct);
        var eligibility = _operationEligibility.Evaluate(operation, routeSteps, operations);
        if (!eligibility.IsEligible)
            return DraftFail(IvMasterErrorCode.Validation,
                eligibility.BlockingReason ?? "The selected operation is blocked by the Work Order execution sequence.");
        var basis = await LoadActiveBasisByOperationAsync(db, scope.CompanyCode, scope.BranchCode!, order.Uid,
            existingBatchNo, ct);
        var basisForOperation = basis.GetValueOrDefault(request.WorkOrderOperationId, OperationBasisTotals.Empty);
        if (IvQty.Round(basisForOperation.Posted + basisForOperation.OpenDraft + request.ProductionQtyThisIssue)
            > IvQty.Round(operation.PlannedOutputQty))
            return DraftFail(IvMasterErrorCode.Validation,
                $"Material Requirement Basis Qty exceeds the remaining operation planning basis of "
                + $"{Math.Max(operation.PlannedOutputQty - basisForOperation.Posted - basisForOperation.OpenDraft, 0m):n4}.");

        var requestedIds = request.Lines.Select(x => x.WorkOrderMaterialId).OrderBy(x => x).ToArray();
        var allLockIds = requestedIds.Concat(oldMaps.Select(x => x.WorkOrderMaterialId)).Distinct().OrderBy(x => x);
        var materials = new Dictionary<long, ProductionWorkOrderMaterial>();
        foreach (var id in allLockIds)
        {
            var material = await LockMaterialAsync(db, id, ct);
            if (material is null)
                return DraftFail(IvMasterErrorCode.NotFound, "A Work Order material was not found.");
            if (requestedIds.Contains(id)) materials[id] = material;
        }
        foreach (var material in materials.Values)
        {
            if (material.WorkOrderId != order.Uid || material.WorkOrderOperationId != request.WorkOrderOperationId)
                return DraftFail(IvMasterErrorCode.Validation, "Every material must belong to the selected Work Order operation.");
            if (ValidateMaterialPolicy(material) is string policy)
                return DraftFail(IvMasterErrorCode.Validation, policy);
        }

        var facts = await db.ProductionMaterialMovements.AsNoTracking()
            .Where(x => requestedIds.Contains(x.WorkOrderMaterialId))
            .Select(x => new { x.WorkOrderMaterialId, x.MovementType, x.Qty }).ToListAsync(ct);
        var otherDrafts = await (from map in db.ProductionMaterialIssueLines.AsNoTracking()
            join draftLink in db.ProductionPostingLinks.AsNoTracking() on map.PostingLinkId equals draftLink.Uid
            join draftBatch in db.IvTrxBatches.AsNoTracking() on map.InventoryBatchId equals draftBatch.Id
            where requestedIds.Contains(map.WorkOrderMaterialId)
                && draftLink.Status == ProductionPostingLinkStatuses.Draft && draftBatch.BatchStatus == IvBatchStatuses.New
                && draftBatch.DeletedAtUtc == null
                && (!existingBatchNo.HasValue || map.InventoryBatchNo != existingBatchNo.Value)
            select new { map.WorkOrderMaterialId, map.IssueQty }).ToListAsync(ct);
        foreach (var line in request.Lines)
        {
            var material = materials[line.WorkOrderMaterialId];
            var issued = facts.Where(x => x.WorkOrderMaterialId == material.Uid && x.MovementType == ProductionMaterialMovementTypes.Issue).Sum(x => x.Qty)
                - facts.Where(x => x.WorkOrderMaterialId == material.Uid && x.MovementType == ProductionMaterialMovementTypes.IssueReversal).Sum(x => x.Qty);
            var returned = facts.Where(x => x.WorkOrderMaterialId == material.Uid && x.MovementType == ProductionMaterialMovementTypes.Return).Sum(x => x.Qty);
            var other = otherDrafts.Where(x => x.WorkOrderMaterialId == material.Uid).Sum(x => x.IssueQty);
            var remainingWo = IvQty.Round(Math.Max(ProductionMaterialExecutionCalc.MaxAllowedNetIssue(material.RequiredQty, material.Tolerance) - (issued - returned) - other, 0m));
            var standardForDesired = ProductionMaterialExecutionCalc.RequestedForProductionQty(
                material.RequiredQty, operation.PlannedOutputQty, request.ProductionQtyThisIssue);
            var desiredMax = ProductionMaterialExecutionCalc.MaxForProductionQty(standardForDesired, material.Tolerance);
            var maxIssue = IvQty.Round(Math.Min(desiredMax, remainingWo));
            if (line.IssueQty > maxIssue)
                return DraftFail(IvMasterErrorCode.Validation,
                    $"Material {material.ComponentCode} exceeds the maximum allowed quantity {maxIssue:n4} for the selected desired output.");
            if (line.IssueQty > standardForDesired && string.IsNullOrWhiteSpace(line.ExcessIssueReason))
                return DraftFail(IvMasterErrorCode.Validation,
                    $"Material {material.ComponentCode} exceeds its standard BOM quantity {standardForDesired:n4}; an Excess Issue Reason is required.");
            var expected = ProductionMaterialExecutionCalc.BaseQtyForIssueQty(line.IssueQty, material.ConversionFactorToBase);
            if (Math.Abs(IvQty.Round(line.Allocations.Sum(x => x.BaseQty)) - expected) > 0.0001m)
                return DraftFail(IvMasterErrorCode.Validation, $"Material {material.ComponentCode} allocation does not match its issue quantity.");
        }

        var allocationIds = request.Lines.SelectMany(x => x.Allocations).Select(x => x.FromBalLocId).Distinct().ToArray();
        var balances = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && allocationIds.Contains(x.Id))
            .Include(x => x.StockMaster).Include(x => x.Lot).ToDictionaryAsync(x => x.Id, ct);
        if (balances.Count != allocationIds.Length)
            return DraftFail(IvMasterErrorCode.Validation, "A selected stock balance was not found.");
        var asOf = await new InventoryAsOfStockService().GetAsync(db, scope.CompanyCode, scope.BranchCode!, allocationIds, request.TrxDateTime, ct);
        foreach (var line in request.Lines)
        foreach (var allocation in line.Allocations)
        {
            var material = materials[line.WorkOrderMaterialId];
            var balance = balances[allocation.FromBalLocId];
            if (!balance.StockMaster.IsActive || !balance.StockMaster.StockControl || balance.IStatus != IvItemStatuses.Active
                || balance.ICode != material.ComponentCode || balance.WhCode != material.WarehouseCode)
                return DraftFail(IvMasterErrorCode.Validation, $"Stock balance {balance.Id} is not eligible for {material.ComponentCode}.");
            if (!string.IsNullOrWhiteSpace(balance.StdUom)
                && !string.Equals(balance.StdUom, material.BaseUom, StringComparison.OrdinalIgnoreCase))
                return DraftFail(IvMasterErrorCode.Validation,
                    $"Stock balance {balance.Id} UOM '{balance.StdUom}' does not match material base UOM '{material.BaseUom}'.");
            if (balance.StockMaster.LotControl && (balance.LotId is null || balance.Lot is null || !balance.Lot.IsActive
                || string.IsNullOrWhiteSpace(balance.LotNo) || balance.Lot.ExpiryDate?.Date < request.TrxDateTime.Date))
                return DraftFail(IvMasterErrorCode.Validation, $"Stock balance {balance.Id} has an invalid or expired lot.");
        }
        foreach (var used in request.Lines.SelectMany(x => x.Allocations).GroupBy(x => x.FromBalLocId))
            if (!asOf.TryGetValue(used.Key, out var stock) || IvQty.Round(used.Sum(x => x.BaseQty)) > stock.UsableBaseQty)
                return DraftFail(IvMasterErrorCode.Validation, $"Insufficient current/as-of stock on balance {used.Key}.");
        foreach (var used in request.Lines.SelectMany(x => x.Allocations).GroupBy(x => x.FromBalLocId))
        {
            var materialId = request.Lines.First(x => x.Allocations.Any(a => a.FromBalLocId == used.Key)).WorkOrderMaterialId;
            var candidates = await _allocation.GetStockCandidatesAsync(materialId, request.TrxDateTime, ct,
                excludeInventoryBatchNo: existingBatchNo);
            var available = candidates.Data?.SingleOrDefault(x => x.FromBalLocId == used.Key)?.AvailableToAllocateBaseQty ?? 0m;
            if (!candidates.Succeeded || IvQty.Round(used.Sum(x => x.BaseQty)) > available)
                return DraftFail(IvMasterErrorCode.Validation,
                    $"Stock balance {used.Key} is allocated by another open Issue-to-Production draft; reload allocation.");
        }
        if (await IvPeriodCloseGuard.EnsureOpenAsync(db, scope.CompanyCode, scope.BranchCode!, request.TrxDateTime, ct) is string periodError)
            return DraftFail(IvMasterErrorCode.Validation, periodError);

        var now = _clock.Now;
        var user = scope.UserId.Length > 10 ? scope.UserId[..10] : scope.UserId;
        var allocationPlan = new List<(ProductionWorkOrderMaterial Material, decimal IssueQty, IvBalLoc Balance, decimal BaseQty)>();
        foreach (var line in request.Lines.OrderBy(x => x.WorkOrderMaterialId))
        {
            var material = materials[line.WorkOrderMaterialId];
            var parts = ProductionMaterialExecutionCalc.AllocateIssueQty(
                line.IssueQty, line.Allocations.Select(x => x.BaseQty).ToList(), material.ConversionFactorToBase);
            for (var i = 0; i < line.Allocations.Count; i++)
            {
                var allocation = line.Allocations[i];
                allocationPlan.Add((material, parts[i], balances[allocation.FromBalLocId], IvQty.Round(allocation.BaseQty)));
            }
        }

        var pendingMaps = new List<(IvTrxBatchDetail Detail, ProductionWorkOrderMaterial Material, decimal IssueQty, string? ExcessIssueReason)>();
        if (batch is null)
        {
            var batchNo = await _runningNumbers.GetNextAsync(db, scope.CompanyCode, RunningNumberKeys.IvBatch, ct);
            batch = new IvTrxBatch { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, BatchNo = batchNo,
                TrxDtTime = request.TrxDateTime, TrxType = IvTrxTypes.IssueToProduction, BatchStatus = IvBatchStatuses.New,
                RefNo = NormalizeIssueRef(request.RefNo, batchNo), Remarks = Truncate(request.Remark, 250), LocationCode = scope.LocationCode,
                CreatedDate = now, CreatedBy = user };
            link = new ProductionPostingLink { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!,
                CommandType = ProductionPostingCommandTypes.MaterialIssuePost, PostingRequestId = Guid.NewGuid().ToString("N"),
                WorkOrderId = order.Uid, ProductionDocumentType = ProductionDocumentTypes.MaterialIssue, InventoryBatchNo = batchNo,
                SnapshotRevision = order.SnapshotRevision, SnapshotHash = order.SnapshotHash,
                ProductionQtyThisIssue = IvQty.Round(request.ProductionQtyThisIssue),
                Status = ProductionPostingLinkStatuses.Draft,
                CreatedDate = now, CreatedBy = user };
            db.IvTrxBatches.Add(batch); db.ProductionPostingLinks.Add(link);
            short trxLine = 0;
            foreach (var row in allocationPlan)
            {
                trxLine++;
                var detail = CreateIssueDetail(scope.CompanyCode, scope.BranchCode!, batch.BatchNo, trxLine, order, row.Material,
                    row.Balance, row.BaseQty, request.Remark, scope.LocationCode);
                batch.Details.Add(detail);
                pendingMaps.Add((detail, row.Material, row.IssueQty,
                    request.Lines.Single(x => x.WorkOrderMaterialId == row.Material.Uid).ExcessIssueReason));
            }
        }
        else
        {
            // Keep previously posted details immutable: historical movements and snapshots
            // continue to refer to them after a corrected generation is saved.
            db.ProductionMaterialIssueLines.RemoveRange(oldMaps);
            await db.SaveChangesAsync(ct);
            var oldDetails = await db.IvTrxBatchDetails.Where(x => x.BatchId == batch.Id)
                .OrderBy(x => x.DocumentRevision).ThenBy(x => x.TrxLineNo).ToListAsync(ct);
            var currentRevision = oldDetails.Count == 0 ? 0 : oldDetails.Max(x => x.DocumentRevision);
            var currentDetails = oldDetails.Where(x => x.DocumentRevision == currentRevision).ToList();
            var oldDetailIds = oldDetails.Select(x => x.Id).ToArray();
            var referencedIds = oldDetailIds.Length == 0
                ? new HashSet<int>()
                : (await db.ProductionMaterialMovements.AsNoTracking()
                    .Where(x => x.InventoryBatchDetailId.HasValue
                        && oldDetailIds.Contains(x.InventoryBatchDetailId.Value))
                    .Select(x => x.InventoryBatchDetailId!.Value)
                    .Distinct()
                    .ToListAsync(ct)).ToHashSet();
            short trxLine = 0;
            for (var i = 0; i < allocationPlan.Count; i++)
            {
                trxLine++;
                var row = allocationPlan[i];
                IvTrxBatchDetail detail;
                if (referencedIds.Count == 0 && i < currentDetails.Count)
                {
                    detail = currentDetails[i];
                    ApplyIssueDetail(detail, order, row.Material, row.Balance, row.BaseQty, request.Remark, scope.LocationCode);
                    detail.TrxLineNo = trxLine;
                }
                else
                {
                    detail = CreateIssueDetail(scope.CompanyCode, scope.BranchCode!, batch.BatchNo, trxLine, order, row.Material,
                        row.Balance, row.BaseQty, request.Remark, scope.LocationCode);
                    detail.DocumentRevision = referencedIds.Count > 0 ? checked(currentRevision + 1) : currentRevision;
                    batch.Details.Add(detail);
                }
                pendingMaps.Add((detail, row.Material, row.IssueQty,
                    request.Lines.Single(x => x.WorkOrderMaterialId == row.Material.Uid).ExcessIssueReason));
            }
            for (var i = allocationPlan.Count; i < currentDetails.Count; i++)
            {
                if (referencedIds.Count == 0)
                    db.IvTrxBatchDetails.Remove(currentDetails[i]);
            }

            batch.TrxDtTime = request.TrxDateTime; batch.RefNo = NormalizeIssueRef(request.RefNo, batch.BatchNo);
            batch.Remarks = Truncate(request.Remark, 250); batch.ModifiedDate = now; batch.ModifiedBy = user;
            link!.ProductionQtyThisIssue = IvQty.Round(request.ProductionQtyThisIssue);
        }

        await db.SaveChangesAsync(ct);
        foreach (var row in pendingMaps)
            db.ProductionMaterialIssueLines.Add(new ProductionMaterialIssueLine { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!,
                PostingLinkId = link!.Uid, InventoryBatchId = batch.Id, InventoryBatchDetailId = row.Detail.Id,
                InventoryBatchNo = batch.BatchNo, DocumentRevision = row.Detail.DocumentRevision,
                InventoryTrxLineNo = row.Detail.TrxLineNo, WorkOrderId = order.Uid,
                WorkOrderOperationId = request.WorkOrderOperationId, WorkOrderMaterialId = row.Material.Uid,
                IssueQty = row.IssueQty, BaseQty = row.Detail.FrStdQty!.Value,
                ExcessIssueReason = Truncate(row.ExcessIssueReason, 250), CreatedDate = now, CreatedBy = user });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return IvMasterOperationResult<ProductionMaterialIssueSaveResult>.Ok(new ProductionMaterialIssueSaveResult
            { BatchNo = batch.BatchNo, PostingRequestId = link!.PostingRequestId, Status = batch.BatchStatus });
    }

    private static IvTrxBatchDetail CreateIssueDetail(
        string company, string branch, int batchNo, short trxLine, ProductionWorkOrder order,
        ProductionWorkOrderMaterial material, IvBalLoc balance, decimal baseQty, string? remark, string? locationCode)
    {
        var detail = new IvTrxBatchDetail
        {
            CompanyCode = company,
            BranchCode = branch,
            BatchNo = batchNo,
            TrxLineNo = trxLine,
            TrxType = IvTrxTypes.IssueToProduction
        };
        ApplyIssueDetail(detail, order, material, balance, baseQty, remark, locationCode);
        return detail;
    }

    private static void ApplyIssueDetail(
        IvTrxBatchDetail detail, ProductionWorkOrder order, ProductionWorkOrderMaterial material,
        IvBalLoc balance, decimal baseQty, string? remark, string? locationCode)
    {
        detail.ProdCode = order.ProductCode;
        detail.ProdDesc = order.ProductDescription;
        detail.ICode = material.ComponentCode;
        detail.IDesc = material.ComponentDescription;
        detail.FromBalLocId = balance.Id;
        detail.FromLotId = balance.LotId;
        detail.FrWarehouse = balance.WhCode;
        detail.FrLocation = balance.LocCode;
        detail.FrLotNo = balance.LotNo;
        detail.FrStdQty = baseQty;
        detail.FrStdUom = material.BaseUom;
        detail.IStatus = balance.IStatus;
        detail.UnitPrice = balance.UnitPrice ?? 0m;
        detail.Remarks = Truncate(remark, 250);
        detail.LocationCode = locationCode;
    }

    private static IReadOnlyDictionary<string, string> ValidateSaveRequest(ProductionMaterialIssueSaveRequest? request, DateTime now)
    {
        var errors = new Dictionary<string, string>();
        if (request is null) { errors["Request"] = "Save request is required."; return errors; }
        if (string.IsNullOrWhiteSpace(request.WorkOrderNo)) errors[nameof(request.WorkOrderNo)] = "Work Order is required.";
        if (request.WorkOrderOperationId <= 0) errors[nameof(request.WorkOrderOperationId)] = "Operation is required.";
        if (request.SnapshotRevision <= 0 || string.IsNullOrWhiteSpace(request.SnapshotHash)) errors["Snapshot"] = "Snapshot fingerprint is required.";
        if (request.ProductionQtyThisIssue <= 0m) errors[nameof(request.ProductionQtyThisIssue)] = "Desired output quantity must be greater than zero.";
        if (request.TrxDateTime == default || request.TrxDateTime > now) errors[nameof(request.TrxDateTime)] = "Transaction date/time is required and cannot be in the future.";
        if (request.Lines.Count == 0 || request.Lines.Count > ProductionMaterialIssuePostValidator.MaxMaterialLines) errors[nameof(request.Lines)] = "Provide at least one valid material line.";
        if (request.Lines.GroupBy(x => x.WorkOrderMaterialId).Any(x => x.Count() > 1)) errors[nameof(request.Lines)] = "A Work Order material may appear only once.";
        if (request.Lines.SelectMany(x => x.Allocations).Count() > short.MaxValue) errors["Allocations"] = "Too many allocation rows.";
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            if (line.WorkOrderMaterialId <= 0 || line.IssueQty <= 0m) errors[$"Lines[{i}]"] = "Material and issue quantity must be positive.";
            if (!string.IsNullOrWhiteSpace(line.ExcessIssueReason) && line.ExcessIssueReason.Trim().Length > 250)
                errors[$"Lines[{i}].ExcessIssueReason"] = "Excess Issue Reason cannot exceed 250 characters.";
            if (line.Allocations.Count == 0 || line.Allocations.Any(x => x.FromBalLocId <= 0 || x.BaseQty <= 0m)) errors[$"Lines[{i}].Allocations"] = "Positive allocations are required.";
            if (line.Allocations.GroupBy(x => x.FromBalLocId).Any(x => x.Count() > 1)) errors[$"Lines[{i}].Allocations"] = "A balance may appear only once per material.";
        }
        return errors;
    }

    private static string? NormalizeIssueRef(string? value, int batchNo) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Equals("AUTO", StringComparison.OrdinalIgnoreCase)
            ? batchNo.ToString() : Truncate(value.Trim(), 50);
    private static IvMasterOperationResult<ProductionMaterialIssueSaveResult> DraftFail(IvMasterErrorCode code, string message) =>
        IvMasterOperationResult<ProductionMaterialIssueSaveResult>.Fail(code, message);
    private static async Task<IvTrxBatch?> LockIssueBatchAsync(AppDbContext db, string company, string branch, int batchNo, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.IvTrxBatches.FromSqlInterpolated($@"SELECT * FROM dbo.IvTrxBatch WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode={company} AND BranchCode={branch} AND BatchNo={batchNo} AND TrxType={IvTrxTypes.IssueToProduction}").SingleOrDefaultAsync(ct)
            : await db.IvTrxBatches.SingleOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.BatchNo == batchNo && x.TrxType == IvTrxTypes.IssueToProduction, ct);
    private static async Task<ProductionPostingLink?> LockIssueLinkByBatchAsync(AppDbContext db, string company, string branch, int batchNo, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionPostingLinks.FromSqlInterpolated($@"SELECT * FROM dbo.PrProductionPostingLink WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode={company} AND BranchCode={branch} AND CommandType={ProductionPostingCommandTypes.MaterialIssuePost} AND InventoryBatchNo={batchNo}").SingleOrDefaultAsync(ct)
            : await db.ProductionPostingLinks.SingleOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.CommandType == ProductionPostingCommandTypes.MaterialIssuePost && x.InventoryBatchNo == batchNo, ct);
}
