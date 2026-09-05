using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

public sealed class IvInventoryPostingService : IIvInventoryPostingService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IIvStockPostingRepository _posting;
    private readonly IIvStockCommonRepository _common;
    private readonly ILogger<IvInventoryPostingService> _logger;

    public IvInventoryPostingService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IIvStockPostingRepository posting,
        IIvStockCommonRepository common,
        ILogger<IvInventoryPostingService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _posting = posting;
        _common = common;
        _logger = logger;
    }

    public Task<IvInventoryPostingResult> PostAsync(
        string trxType,
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(trxType, batchNos, post: true, cancellationToken);

    public Task<IvInventoryPostingResult> RollbackAsync(
        string trxType,
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(trxType, batchNos, post: false, cancellationToken);

    private async Task<IvInventoryPostingResult> DispatchAsync(
        string trxType,
        IReadOnlyList<int> batchNos,
        bool post,
        CancellationToken cancellationToken)
    {
        var type = (trxType ?? string.Empty).Trim().ToUpperInvariant();
        var isMr = string.Equals(type, IvTrxTypes.MiscellaneousReceipt, StringComparison.OrdinalIgnoreCase);
        var isCr = string.Equals(type, IvTrxTypes.CustomerReturn, StringComparison.OrdinalIgnoreCase);
        var isMi = string.Equals(type, IvTrxTypes.MiscellaneousIssue, StringComparison.OrdinalIgnoreCase);
        var isSc = string.Equals(type, IvTrxTypes.Scrap, StringComparison.OrdinalIgnoreCase);
        var isTr = string.Equals(type, IvTrxTypes.StockTransfer, StringComparison.OrdinalIgnoreCase);
        var isAdj = string.Equals(type, IvTrxTypes.StockAdjustment, StringComparison.OrdinalIgnoreCase);
        if (!isMr && !isCr && !isMi && !isSc && !isTr && !isAdj)
        {
            return IvInventoryPostingResult.Fail($"Posting is not implemented for transaction type '{type}'.");
        }

        if (batchNos is null || batchNos.Count == 0)
        {
            return IvInventoryPostingResult.Fail("No record selected.");
        }

        if (batchNos.Count > IvPostingLimits.MaxPostSelection)
        {
            return IvInventoryPostingResult.Fail(
                $"Select at most {IvPostingLimits.MaxPostSelection} batches.");
        }

        var scope = _tenant.TryWriteScope();
        if (scope is null)
        {
            return IvInventoryPostingResult.Fail("Company, branch, and location are required.");
        }

        var menuCode = isMr
            ? MenuCodes.InventoryMiscReceipt
            : isCr
                ? MenuCodes.InventoryStockReturn
                : isMi
                    ? MenuCodes.InventoryMiscIssue
                    : isSc
                        ? MenuCodes.InventoryScrap
                        : isAdj
                            ? MenuCodes.InventoryStockAdjustment
                            : MenuCodes.InventoryStockTransfer;
        var permission = post ? PermissionCodes.Post : PermissionCodes.Rollback;
        if (!await _accessRights.CanAsync(menuCode, permission, cancellationToken))
        {
            return IvInventoryPostingResult.Fail("Not authorized.");
        }

        var results = new List<IvInventoryPostingBatchResult>();
        foreach (var batchNo in batchNos.Distinct())
        {
            try
            {
                IvInventoryPostingBatchResult batchResult;
                if (isMr || isCr)
                {
                    var expectedTrxType = isCr ? IvTrxTypes.CustomerReturn : IvTrxTypes.MiscellaneousReceipt;
                    batchResult = post
                        ? await PostInventoryMRAsync(scope.CompanyCode, scope.BranchCode!, scope.UserId, batchNo, expectedTrxType, cancellationToken)
                        : await RollBackInventoryMRAsync(scope.CompanyCode, scope.BranchCode!, scope.UserId, batchNo, expectedTrxType, cancellationToken);
                }
                else if (isMi || isSc)
                {
                    var expectedTrxType = isSc ? IvTrxTypes.Scrap : IvTrxTypes.MiscellaneousIssue;
                    batchResult = post
                        ? await PostInventoryMIAsync(scope.CompanyCode, scope.BranchCode!, scope.UserId, batchNo, expectedTrxType, cancellationToken)
                        : await RollBackInventoryMIAsync(scope.CompanyCode, scope.BranchCode!, scope.UserId, batchNo, expectedTrxType, cancellationToken);
                }
                else if (isAdj)
                {
                    batchResult = post
                        ? await PostInventoryADJAsync(scope.CompanyCode, scope.BranchCode!, scope.UserId, batchNo, cancellationToken)
                        : await RollBackInventoryADJAsync(scope.CompanyCode, scope.BranchCode!, scope.UserId, batchNo, cancellationToken);
                }
                else
                {
                    batchResult = post
                        ? await PostInventoryTRAsync(scope.CompanyCode, scope.BranchCode!, scope.UserId, batchNo, cancellationToken)
                        : await RollBackInventoryTRAsync(scope.CompanyCode, scope.BranchCode!, scope.UserId, batchNo, cancellationToken);
                }

                results.Add(batchResult);
            }
            catch (DbUpdateConcurrencyException)
            {
                results.Add(IvInventoryPostingBatchResult.Fail(batchNo, "Stock was modified by another user. Retry."));
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                results.Add(IvInventoryPostingBatchResult.Fail(batchNo, "Posting conflict (duplicate history or balance)."));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inventory {Action} failed for batch {BatchNo}", post ? "POST" : "ROLLBACK", batchNo);
                results.Add(IvInventoryPostingBatchResult.Fail(batchNo, ex.Message));
            }
        }

        var aggregated = IvInventoryPostingResult.FromBatches(results);
        if (post && aggregated.SucceededCount > 0 && aggregated.FailedCount > 0)
        {
            aggregated = new IvInventoryPostingResult
            {
                Succeeded = false,
                SucceededCount = aggregated.SucceededCount,
                FailedCount = aggregated.FailedCount,
                Batches = aggregated.Batches,
                ErrorMessage =
                    $"Posted {aggregated.SucceededCount} of {results.Count}. " +
                    string.Join(" ", results.Where(x => !x.Succeeded).Select(x => $"Batch {x.BatchNo}: {x.ErrorMessage}"))
            };
        }
        else if (!post && aggregated.SucceededCount > 0 && aggregated.FailedCount > 0)
        {
            aggregated = new IvInventoryPostingResult
            {
                Succeeded = false,
                SucceededCount = aggregated.SucceededCount,
                FailedCount = aggregated.FailedCount,
                Batches = aggregated.Batches,
                ErrorMessage =
                    $"Rolled back {aggregated.SucceededCount} of {results.Count}. " +
                    string.Join(" ", results.Where(x => !x.Succeeded).Select(x => $"Batch {x.BatchNo}: {x.ErrorMessage}"))
            };
        }

        return aggregated;
    }

    private async Task<IvInventoryPostingBatchResult> PostInventoryMRAsync(
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        string expectedTrxType,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Phase 1 — Validate (lock batch, re-read authoritative document + masters)
        var batch = await _posting.LockBatchForUpdateAsync(db, companyCode, branchCode, batchNo, cancellationToken);
        if (batch is null
            || !string.Equals(batch.TrxType, expectedTrxType, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, StockInNotFoundMessage(expectedTrxType));
        }

        if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, StockInOnlyNewCanPostMessage(expectedTrxType));
        }

        var details = await _posting.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
        if (details.Count == 0)
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, StockInNoLinesMessage(expectedTrxType));
        }

        if (await _posting.HistoryExistsForBatchAsync(db, companyCode, branchCode, batchNo, cancellationToken))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "History already exists for this batch.");
        }

        var masters = await _posting.LockStockMastersAsync(
            db,
            companyCode,
            details.Select(d => d.ICode ?? string.Empty),
            cancellationToken);

        var linePlans = new List<MrLinePlan>(details.Count);
        foreach (var detail in details)
        {
            var planResult = await BuildMrPostLineAsync(db, companyCode, branchCode, detail, masters, cancellationToken);
            if (planResult.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, planResult.Error);
            }

            linePlans.Add(planResult.Plan!);
        }

        // Phase 2 — Lock / find-or-create (aggregate stock-controlled deltas by slice)
        var deltaBySlice = new Dictionary<IvStockSliceKey, decimal>();
        var lotByKey = new Dictionary<(string ICode, string LotNo), IvLot>();
        foreach (var plan in linePlans.Where(x => x.StockControl && x.ToSlice is not null))
        {
            var slice = plan.ToSlice!.Value;
            deltaBySlice[slice] = deltaBySlice.GetValueOrDefault(slice) + plan.Quantity;
        }

        var ordered = _posting.GetOrderedStockSlices(deltaBySlice.Keys);
        var locked = (await _posting.LockBalanceSlicesAsync(db, ordered, cancellationToken))
            .ToDictionary(x => x.Key, x => x.Value);

        foreach (var plan in linePlans)
        {
            if (plan.LotControl)
            {
                var lotKey = (plan.ICode, plan.LotNo!);
                if (!lotByKey.ContainsKey(lotKey))
                {
                    lotByKey[lotKey] = await _posting.FindOrCreateLotAsync(
                        db,
                        companyCode,
                        plan.ICode,
                        plan.LotNo!,
                        expectedTrxType,
                        batchNo.ToString(),
                        batch.TrxDtTime.Date,
                        plan.ExpiryDate,
                        userId,
                        cancellationToken);
                }

                plan.LotId = lotByKey[lotKey].Id;
            }

            if (plan.StockControl && plan.ToSlice is not null)
            {
                var slice = plan.ToSlice.Value;
                if (!locked.TryGetValue(slice, out var bal))
                {
                    bal = await _posting.FindOrCreateBalLocAsync(
                        db,
                        slice,
                        plan.LotId,
                        plan.Uom,
                        userId,
                        batch.TrxDtTime,
                        batch.LocationCode,
                        cancellationToken);
                    locked[slice] = bal;
                }
                else if (plan.LotId is not null && bal.LotId is null)
                {
                    bal.LotId = plan.LotId;
                }

                plan.BalLocId = bal.Id;
            }
        }

        // Phase 3 — Calculate (never apply if any would go negative — N/A for stock-in)
        foreach (var slice in ordered)
        {
            var bal = locked[slice];
            var next = bal.StdQty + deltaBySlice[slice];
            if (next < 0m)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Stock would go negative for {slice} (on hand {bal.StdQty}, change {deltaBySlice[slice]}).");
            }
        }

        // Phase 4 — Apply aggregated BalLoc updates
        var now = DateTime.UtcNow;
        var uid = Truncate(userId, 10);
        foreach (var slice in ordered)
        {
            var bal = locked[slice];
            bal.StdQty += deltaBySlice[slice];
            bal.ModifiedDate = now;
            bal.TransDate = batch.TrxDtTime;
            if (string.IsNullOrWhiteSpace(bal.StdUom))
            {
                var uom = linePlans.FirstOrDefault(x => x.ToSlice == slice)?.Uom;
                bal.StdUom = Truncate(uom, 10);
            }
        }

        // Phase 5 — History (one row per document line)
        var opId = Guid.NewGuid();
        foreach (var plan in linePlans)
        {
            var detail = plan.Detail;
            detail.ToBalLocId = plan.BalLocId;
            detail.ToLotId = plan.LotId;

            _posting.AddHistory(db, new IvTrxHistory
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                BatchNo = batchNo,
                TrxLineNo = detail.TrxLineNo,
                TrxDtTime = batch.TrxDtTime,
                TrxType = expectedTrxType,
                BatchStatus = IvBatchStatuses.Posted,
                RefNo = batch.RefNo,
                ProdCode = detail.ProdCode ?? detail.ICode,
                ProdDesc = detail.ProdDesc ?? detail.IDesc,
                ICode = detail.ICode ?? plan.ICode,
                IDesc = detail.IDesc,
                ToWarehouse = detail.ToWarehouse,
                ToLocation = detail.ToLocation,
                ToLotNo = detail.ToLotNo,
                ToStdQty = detail.ToStdQty,
                ToStdUom = detail.ToStdUom,
                IStatus = detail.IStatus,
                Remarks = detail.Remarks,
                UnitPrice = detail.UnitPrice,
                Cost = detail.Cost,
                CostPrice = detail.CostPrice,
                BaseUnitPrices = detail.BaseUnitPrices,
                LocationCode = detail.LocationCode,
                ToBalLocId = plan.BalLocId,
                ToLotId = plan.LotId,
                CreatedDate = now,
                CreatedBy = uid
            });
        }

        // Phase 6 — Status / audit
        batch.BatchStatus = IvBatchStatuses.Posted;
        batch.PostedDate = now;
        batch.PostedBy = uid;
        batch.PostedCount += 1;
        batch.PostingOperationId = opId;
        batch.ModifiedDate = now;
        batch.ModifiedBy = uid;

        // Phase 7 — Commit
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Posted {TrxType}. Company={Company} Branch={Branch} BatchNo={BatchNo} OpId={OpId} User={User}",
            expectedTrxType, companyCode, branchCode, batchNo, opId, uid);

        return IvInventoryPostingBatchResult.Ok(batchNo, opId);
    }

    private async Task<IvInventoryPostingBatchResult> RollBackInventoryMRAsync(
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        string expectedTrxType,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var batch = await _posting.LockBatchForUpdateAsync(db, companyCode, branchCode, batchNo, cancellationToken);
        if (batch is null
            || !string.Equals(batch.TrxType, expectedTrxType, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, StockInNotFoundMessage(expectedTrxType));
        }

        if (!string.Equals(batch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, StockInOnlyPostedCanRollbackMessage(expectedTrxType));
        }

        var details = await _posting.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
        var history = await _posting.LoadHistoryForBatchAsync(db, companyCode, branchCode, batchNo, cancellationToken);
        if (history.Count == 0)
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "No posted history found for this batch.");
        }

        if (details.Count > 0 && history.Count != details.Count)
        {
            return IvInventoryPostingBatchResult.Fail(
                batchNo,
                $"History incomplete (history {history.Count}, detail {details.Count}).");
        }

        var deltaBySlice = new Dictionary<IvStockSliceKey, decimal>();

        foreach (var h in history)
        {
            if (h.ToBalLocId is null)
            {
                continue; // non-stock history
            }

            var bal = await db.IvBalLocs.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == h.ToBalLocId.Value, cancellationToken);
            if (bal is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"ORPHAN_HISTORY: BalLoc Id {h.ToBalLocId} missing for line {h.TrxLineNo}.");
            }

            if (!string.Equals(bal.CompanyCode, companyCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(bal.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, "Tenant mismatch on balance row.");
            }

            var slice = IvStockSliceKey.Create(
                bal.CompanyCode, bal.BranchCode, bal.ICode, bal.WhCode, bal.LocCode, bal.LotNo, bal.IStatus);
            var qty = h.ToStdQty ?? 0m;
            deltaBySlice[slice] = deltaBySlice.GetValueOrDefault(slice) + qty;
        }

        var ordered = _posting.GetOrderedStockSlices(deltaBySlice.Keys);
        var locked = await _posting.LockBalanceSlicesAsync(db, ordered, cancellationToken);

        foreach (var slice in ordered)
        {
            if (!locked.TryGetValue(slice, out var bal))
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, $"Balance slice missing: {slice}");
            }

            var next = bal.StdQty - deltaBySlice[slice];
            if (next < 0m)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Stock would go negative for {slice} (on hand {bal.StdQty}, rollback {deltaBySlice[slice]}).");
            }
        }

        // Apply all after all checks
        var now = DateTime.UtcNow;
        var uid = Truncate(userId, 10);
        foreach (var slice in ordered)
        {
            var bal = locked[slice];
            bal.StdQty -= deltaBySlice[slice];
            bal.ModifiedDate = now;
        }

        _posting.RemoveHistory(db, history);

        foreach (var detail in details)
        {
            detail.ToBalLocId = null;
            detail.ToLotId = null;
        }

        var opId = Guid.NewGuid();
        batch.BatchStatus = IvBatchStatuses.New;
        batch.RollbackDate = now;
        batch.RollbackBy = uid;
        batch.RollbackCount += 1;
        batch.RollbackOperationId = opId;
        batch.ModifiedDate = now;
        batch.ModifiedBy = uid;

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Rolled back {TrxType}. Company={Company} Branch={Branch} BatchNo={BatchNo} OpId={OpId} User={User}",
            expectedTrxType, companyCode, branchCode, batchNo, opId, uid);

        return IvInventoryPostingBatchResult.Ok(batchNo, opId);
    }

    private async Task<IvInventoryPostingBatchResult> PostInventoryADJAsync(
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var batch = await _posting.LockBatchForUpdateAsync(db, companyCode, branchCode, batchNo, cancellationToken);
        if (batch is null
            || !string.Equals(batch.TrxType, IvTrxTypes.StockAdjustment, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "Stock adjustment was not found.");
        }

        if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "Only NEW stock adjustments can be posted.");
        }

        var details = await _posting.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
        if (details.Count == 0)
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "Stock adjustment has no lines.");
        }

        if (await _posting.HistoryExistsForBatchAsync(db, companyCode, branchCode, batchNo, cancellationToken))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "History already exists for this batch.");
        }

        foreach (var detail in details)
        {
            var invariantError = IvStockAdjustmentLineInvariant.ValidateDetail(detail, detail.TrxLineNo);
            if (invariantError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, invariantError);
            }
        }

        var balLocIds = details
            .Select(IvStockAdjustmentLineInvariant.GetBalLocId)
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        var locked = new Dictionary<int, IvBalLocLockResult>();
        foreach (var balLocId in balLocIds)
        {
            var lockedRow = await _posting.LockBalLocByIdForTenantAsync(
                db, balLocId, companyCode, branchCode, cancellationToken);
            if (lockedRow is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Balance Id {balLocId} was not found for this company/branch.");
            }

            var detail = details.First(d => IvStockAdjustmentLineInvariant.GetBalLocId(d) == balLocId);
            if (!ValidateAdjSliceMatch(detail, lockedRow, balLocId, out var sliceError))
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, sliceError);
            }

            locked[balLocId] = lockedRow;
        }

        var netByBalLoc = new Dictionary<int, decimal>();
        foreach (var detail in details)
        {
            var balLocId = IvStockAdjustmentLineInvariant.GetBalLocId(detail);
            netByBalLoc[balLocId] = netByBalLoc.GetValueOrDefault(balLocId)
                + IvStockAdjustmentLineInvariant.GetSignedDelta(detail);
        }

        foreach (var (balLocId, net) in netByBalLoc)
        {
            if (net < 0m && Math.Abs(net) > locked[balLocId].StdQty)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Insufficient quantity on balance Id {balLocId} (on hand {locked[balLocId].StdQty}, required decrease {Math.Abs(net)}).");
            }
        }

        var now = DateTime.UtcNow;
        var uid = Truncate(userId, 10);
        foreach (var (balLocId, net) in netByBalLoc.OrderBy(kv => kv.Key))
        {
            if (net == 0m)
            {
                continue;
            }

            if (net > 0m)
            {
                var affected = await _posting.IncreaseBalLocQtyAsync(
                    db, balLocId, companyCode, branchCode, net, batch.TrxDtTime, cancellationToken);
                if (affected != 1)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return IvInventoryPostingBatchResult.Fail(
                        batchNo,
                        $"Stock increase failed for balance Id {balLocId}.");
                }
            }
            else
            {
                var decrease = Math.Abs(net);
                var affected = await _posting.DecreaseBalLocQtyAsync(
                    db, balLocId, companyCode, branchCode, decrease, batch.TrxDtTime, cancellationToken);
                if (affected != 1)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return IvInventoryPostingBatchResult.Fail(
                        batchNo,
                        $"Stock decrease failed for balance Id {balLocId} (insufficient quantity or missing row).");
                }
            }
        }

        var opId = Guid.NewGuid();
        foreach (var detail in details.OrderBy(d => d.TrxLineNo))
        {
            var balLocId = IvStockAdjustmentLineInvariant.GetBalLocId(detail);
            var lockedRow = locked[balLocId];
            var isIncrease = IvStockAdjustmentLineInvariant.TryGetDirection(detail, out var direction)
                && direction == IvAdjLineDirection.Increase;

            if (isIncrease)
            {
                detail.ToBalLocId = balLocId;
                detail.ToLotId = lockedRow.LotId;
            }
            else
            {
                detail.FromBalLocId = balLocId;
                detail.FromLotId = lockedRow.LotId;
            }

            var history = new IvTrxHistory
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                BatchNo = batchNo,
                TrxLineNo = detail.TrxLineNo,
                TrxDtTime = batch.TrxDtTime,
                TrxType = IvTrxTypes.StockAdjustment,
                BatchStatus = IvBatchStatuses.Posted,
                RefNo = batch.RefNo,
                ProdCode = detail.ProdCode ?? detail.ICode,
                ProdDesc = detail.ProdDesc ?? detail.IDesc,
                ICode = detail.ICode,
                IDesc = detail.IDesc,
                IStatus = detail.IStatus,
                Remarks = detail.Remarks,
                UnitPrice = detail.UnitPrice,
                Cost = detail.Cost,
                CostPrice = detail.CostPrice,
                BaseUnitPrices = detail.BaseUnitPrices,
                LocationCode = detail.LocationCode,
                CreatedDate = now,
                CreatedBy = uid
            };

            if (isIncrease)
            {
                history.ToWarehouse = detail.ToWarehouse;
                history.ToLocation = detail.ToLocation;
                history.ToLotNo = detail.ToLotNo;
                history.ToStdQty = detail.ToStdQty;
                history.ToStdUom = detail.ToStdUom;
                history.ToBalLocId = balLocId;
                history.ToLotId = lockedRow.LotId;
            }
            else
            {
                history.FrWarehouse = detail.FrWarehouse;
                history.FrLocation = detail.FrLocation;
                history.FrLotNo = detail.FrLotNo;
                history.FrStdQty = detail.FrStdQty;
                history.FrStdUom = detail.FrStdUom;
                history.FromBalLocId = balLocId;
                history.FromLotId = lockedRow.LotId;
            }

            _posting.AddHistory(db, history);
        }

        batch.BatchStatus = IvBatchStatuses.Posted;
        batch.PostedDate = now;
        batch.PostedBy = uid;
        batch.PostedCount += 1;
        batch.PostingOperationId = opId;
        batch.ModifiedDate = now;
        batch.ModifiedBy = uid;

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Posted {TrxType}. Company={Company} Branch={Branch} BatchNo={BatchNo} OpId={OpId} User={User}",
            IvTrxTypes.StockAdjustment, companyCode, branchCode, batchNo, opId, uid);

        return IvInventoryPostingBatchResult.Ok(batchNo, opId);
    }

    private async Task<IvInventoryPostingBatchResult> RollBackInventoryADJAsync(
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var batch = await _posting.LockBatchForUpdateAsync(db, companyCode, branchCode, batchNo, cancellationToken);
        if (batch is null
            || !string.Equals(batch.TrxType, IvTrxTypes.StockAdjustment, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "Stock adjustment was not found.");
        }

        if (!string.Equals(batch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "Only POSTED stock adjustments can be rolled back.");
        }

        var details = await _posting.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
        var history = await _posting.LoadHistoryForBatchAsync(db, companyCode, branchCode, batchNo, cancellationToken);
        if (history.Count == 0)
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "No posted history found for this batch.");
        }

        var integrityError = ValidateAdjHistoryIntegrity(details, history, companyCode, branchCode, batchNo);
        if (integrityError is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, integrityError);
        }

        var inverseByBalLoc = new Dictionary<int, decimal>();
        foreach (var h in history)
        {
            var balLocId = h.ToBalLocId ?? h.FromBalLocId ?? 0;
            if (balLocId <= 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, $"History line {h.TrxLineNo}: BalLocId is required.");
            }

            var delta = h.ToBalLocId is > 0
                ? -(h.ToStdQty ?? 0m)
                : (h.FrStdQty ?? 0m);
            inverseByBalLoc[balLocId] = inverseByBalLoc.GetValueOrDefault(balLocId) + delta;
        }

        foreach (var balLocId in inverseByBalLoc.Keys.OrderBy(id => id))
        {
            var lockedRow = await _posting.LockBalLocByIdForTenantAsync(
                db, balLocId, companyCode, branchCode, cancellationToken);
            if (lockedRow is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"ORPHAN_HISTORY: BalLoc Id {balLocId} missing for rollback.");
            }

            var inverse = inverseByBalLoc[balLocId];
            if (inverse < 0m && Math.Abs(inverse) > lockedRow.StdQty)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Insufficient quantity on balance Id {balLocId} for rollback (on hand {lockedRow.StdQty}, required decrease {Math.Abs(inverse)}).");
            }
        }

        foreach (var (balLocId, inverse) in inverseByBalLoc.OrderBy(kv => kv.Key))
        {
            if (inverse == 0m)
            {
                continue;
            }

            if (inverse > 0m)
            {
                var affected = await _posting.IncreaseBalLocQtyAsync(
                    db, balLocId, companyCode, branchCode, inverse, batch.TrxDtTime, cancellationToken);
                if (affected != 1)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return IvInventoryPostingBatchResult.Fail(
                        batchNo,
                        $"Stock restore failed for balance Id {balLocId}.");
                }
            }
            else
            {
                var decrease = Math.Abs(inverse);
                var affected = await _posting.DecreaseBalLocQtyAsync(
                    db, balLocId, companyCode, branchCode, decrease, batch.TrxDtTime, cancellationToken);
                if (affected != 1)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return IvInventoryPostingBatchResult.Fail(
                        batchNo,
                        $"Stock rollback decrease failed for balance Id {balLocId}.");
                }
            }
        }

        _posting.RemoveHistory(db, history);

        var now = DateTime.UtcNow;
        var uid = Truncate(userId, 10);
        var opId = Guid.NewGuid();
        batch.BatchStatus = IvBatchStatuses.New;
        batch.RollbackDate = now;
        batch.RollbackBy = uid;
        batch.RollbackCount += 1;
        batch.RollbackOperationId = opId;
        batch.ModifiedDate = now;
        batch.ModifiedBy = uid;

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Rolled back {TrxType}. Company={Company} Branch={Branch} BatchNo={BatchNo} OpId={OpId} User={User}",
            IvTrxTypes.StockAdjustment, companyCode, branchCode, batchNo, opId, uid);

        return IvInventoryPostingBatchResult.Ok(batchNo, opId);
    }

    private static bool ValidateAdjSliceMatch(
        IvTrxBatchDetail detail,
        IvBalLocLockResult lockedRow,
        int balLocId,
        out string error)
    {
        error = string.Empty;
        var iCode = (detail.ICode ?? string.Empty).Trim();
        var wh = (detail.ToWarehouse ?? detail.FrWarehouse ?? string.Empty).Trim();
        var loc = (detail.ToLocation ?? detail.FrLocation ?? string.Empty).Trim();
        var lot = (detail.ToLotNo ?? detail.FrLotNo ?? string.Empty).Trim();
        var iStatus = (detail.IStatus ?? string.Empty).Trim();

        if (!string.Equals(lockedRow.ICode, iCode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(lockedRow.WhCode, wh, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(lockedRow.LocCode, loc, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(lockedRow.LotNo, lot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(lockedRow.IStatus, iStatus, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Balance Id {balLocId} no longer matches the adjustment line (item/warehouse/location/lot/status).";
            return false;
        }

        return true;
    }

    private static string? ValidateAdjHistoryIntegrity(
        IReadOnlyList<IvTrxBatchDetail> details,
        IReadOnlyList<IvTrxHistory> history,
        string companyCode,
        string branchCode,
        int batchNo)
    {
        var detailByLine = details.ToDictionary(d => d.TrxLineNo);
        var historyByLine = new Dictionary<short, IvTrxHistory>();

        foreach (var h in history)
        {
            if (h.BatchNo != batchNo)
            {
                return $"History BatchNo mismatch (expected {batchNo}).";
            }

            if (!string.Equals(h.TrxType, IvTrxTypes.StockAdjustment, StringComparison.OrdinalIgnoreCase))
            {
                return $"History line {h.TrxLineNo}: TrxType must be {IvTrxTypes.StockAdjustment}.";
            }

            if (!string.Equals(h.CompanyCode, companyCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(h.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase))
            {
                return $"History line {h.TrxLineNo}: company/branch mismatch.";
            }

            if (!historyByLine.TryAdd(h.TrxLineNo, h))
            {
                return $"History has duplicate TrxLineNo {h.TrxLineNo}.";
            }
        }

        foreach (var detail in details)
        {
            if (!historyByLine.TryGetValue(detail.TrxLineNo, out var h))
            {
                return $"Missing history for detail line {detail.TrxLineNo}.";
            }

            var detailBalLocId = IvStockAdjustmentLineInvariant.GetBalLocId(detail);
            var historyBalLocId = h.ToBalLocId ?? h.FromBalLocId ?? 0;
            if (detailBalLocId != historyBalLocId)
            {
                return $"History line {h.TrxLineNo}: BalLocId mismatch with detail.";
            }

            if (IvStockAdjustmentLineInvariant.TryGetDirection(detail, out var detailDir))
            {
                var historyIsIncrease = h.ToBalLocId is > 0 && (h.ToStdQty ?? 0m) > 0m;
                var detailIsIncrease = detailDir == IvAdjLineDirection.Increase;
                if (historyIsIncrease != detailIsIncrease)
                {
                    return $"History line {h.TrxLineNo}: direction mismatch with detail.";
                }

                var detailQty = IvStockAdjustmentLineInvariant.GetAbsoluteQty(detail);
                var historyQty = historyIsIncrease ? (h.ToStdQty ?? 0m) : (h.FrStdQty ?? 0m);
                if (detailQty != historyQty)
                {
                    return $"History line {h.TrxLineNo}: quantity mismatch with detail.";
                }
            }

            var detailLotId = detail.ToLotId ?? detail.FromLotId;
            var historyLotId = h.ToLotId ?? h.FromLotId;
            if (detailLotId != historyLotId)
            {
                return $"History line {h.TrxLineNo}: LotId mismatch with detail.";
            }
        }

        foreach (var lineNo in historyByLine.Keys)
        {
            if (!detailByLine.ContainsKey(lineNo))
            {
                return $"Orphan history for TrxLineNo {lineNo} (no matching detail).";
            }
        }

        return null;
    }

    /// <summary>Test-only hook invoked after MI stock decrease, before history. Throws to force rollback.</summary>
    internal Action? TestHookAfterMiStockUpdate { get; set; }

    /// <summary>Test-only hook invoked after MI history insert (before SaveChanges commits POSTED).</summary>
    internal Action? TestHookAfterMiHistory { get; set; }

    /// <summary>Test-only hook invoked after MI rollback stock restore, before history delete.</summary>
    internal Action? TestHookAfterMiRollbackStock { get; set; }

    public Task<IvInventoryPostingBatchResult> PostStockOutInTransactionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        string expectedTrxType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return PostInventoryMICoreAsync(
            db, companyCode, branchCode, userId, batchNo, expectedTrxType, cancellationToken);
    }

    public Task<IvInventoryPostingBatchResult> RollBackStockOutInTransactionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        string expectedTrxType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return RollBackInventoryMICoreAsync(
            db, companyCode, branchCode, userId, batchNo, expectedTrxType, cancellationToken);
    }

    private async Task<IvInventoryPostingBatchResult> PostInventoryMIAsync(
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        string expectedTrxType,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var result = await PostInventoryMICoreAsync(
            db, companyCode, branchCode, userId, batchNo, expectedTrxType, cancellationToken);
        if (!result.Succeeded)
        {
            await tx.RollbackAsync(cancellationToken);
            return result;
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Posted {TrxType}. Company={Company} Branch={Branch} BatchNo={BatchNo} OpId={OpId} User={User}",
            expectedTrxType, companyCode, branchCode, batchNo, result.OperationId, Truncate(userId, 10));

        return result;
    }

    private async Task<IvInventoryPostingBatchResult> PostInventoryMICoreAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        string expectedTrxType,
        CancellationToken cancellationToken)
    {
        var batch = await _posting.LockBatchForUpdateAsync(db, companyCode, branchCode, batchNo, cancellationToken);
        if (batch is null
            || !string.Equals(batch.TrxType, expectedTrxType, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, StockOutNotFoundMessage(expectedTrxType));
        }

        if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, StockOutOnlyNewCanPostMessage(expectedTrxType));
        }

        var details = await _posting.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
        if (details.Count == 0)
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, StockOutNoLinesMessage(expectedTrxType));
        }

        if (await _posting.HistoryExistsForBatchAsync(db, companyCode, branchCode, batchNo, cancellationToken))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "History already exists for this batch.");
        }

        var masters = await _posting.LockStockMastersAsync(
            db,
            companyCode,
            details.Select(d => d.ICode ?? string.Empty),
            cancellationToken);

        var linePlans = new List<MiLinePlan>(details.Count);
        foreach (var detail in details)
        {
            var planResult = BuildMiPostLine(detail, masters);
            if (planResult.Error is not null)
            {
                return IvInventoryPostingBatchResult.Fail(batchNo, planResult.Error);
            }

            linePlans.Add(planResult.Plan!);
        }

        var deltaById = new Dictionary<int, decimal>();
        var sliceById = new Dictionary<int, IvStockSliceKey>();
        foreach (var plan in linePlans)
        {
            deltaById[plan.FromBalLocId] = deltaById.GetValueOrDefault(plan.FromBalLocId) + plan.Quantity;
            sliceById[plan.FromBalLocId] = plan.FromSlice;
        }

        var orderedIds = sliceById
            .OrderBy(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();
        var locked = new Dictionary<int, IvBalLocLockResult>();

        foreach (var balLocId in orderedIds)
        {
            var lockedRow = await _posting.LockBalLocByIdForTenantAsync(
                db, balLocId, companyCode, branchCode, cancellationToken);
            if (lockedRow is null)
            {
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Source balance Id {balLocId} was not found for this company/branch.");
            }

            var expected = sliceById[balLocId];
            if (!string.Equals(lockedRow.ICode, expected.ICode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(lockedRow.WhCode, expected.WhCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(lockedRow.LocCode, expected.LocCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(lockedRow.LotNo, expected.LotNo, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(lockedRow.IStatus, expected.IStatus, StringComparison.OrdinalIgnoreCase))
            {
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    StockOutSourceMismatchMessage(expectedTrxType, balLocId));
            }

            locked[balLocId] = lockedRow;
        }

        foreach (var (balLocId, required) in deltaById)
        {
            var actual = locked[balLocId].StdQty;
            if (required > actual)
            {
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Insufficient quantity on balance Id {balLocId} (on hand {actual}, required {required}).");
            }
        }

        var now = DateTime.UtcNow;
        var uid = Truncate(userId, 10);
        foreach (var balLocId in orderedIds)
        {
            var required = deltaById[balLocId];
            var affected = await _posting.DecreaseBalLocQtyAsync(
                db, balLocId, companyCode, branchCode, required, batch.TrxDtTime, cancellationToken);
            if (affected != 1)
            {
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Stock decrease failed for balance Id {balLocId} (insufficient quantity or missing row).");
            }
        }

        TestHookAfterMiStockUpdate?.Invoke();

        var opId = Guid.NewGuid();
        foreach (var plan in linePlans)
        {
            var detail = plan.Detail;
            var lockedRow = locked[plan.FromBalLocId];
            detail.FromBalLocId = plan.FromBalLocId;
            detail.FromLotId = lockedRow.LotId;

            _posting.AddHistory(db, new IvTrxHistory
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                BatchNo = batchNo,
                TrxLineNo = detail.TrxLineNo,
                TrxDtTime = batch.TrxDtTime,
                TrxType = expectedTrxType,
                BatchStatus = IvBatchStatuses.Posted,
                RefNo = batch.RefNo,
                ProdCode = detail.ProdCode ?? detail.ICode,
                ProdDesc = detail.ProdDesc ?? detail.IDesc,
                ICode = detail.ICode ?? plan.ICode,
                IDesc = detail.IDesc,
                FrWarehouse = detail.FrWarehouse,
                FrLocation = detail.FrLocation,
                FrLotNo = detail.FrLotNo,
                FrStdQty = detail.FrStdQty,
                FrStdUom = detail.FrStdUom,
                IStatus = detail.IStatus,
                Remarks = detail.Remarks,
                UnitPrice = detail.UnitPrice,
                Cost = detail.Cost,
                CostPrice = detail.CostPrice,
                BaseUnitPrices = detail.BaseUnitPrices,
                LocationCode = detail.LocationCode,
                FromBalLocId = plan.FromBalLocId,
                FromLotId = lockedRow.LotId,
                CreatedDate = now,
                CreatedBy = uid
            });
        }

        TestHookAfterMiHistory?.Invoke();

        batch.BatchStatus = IvBatchStatuses.Posted;
        batch.PostedDate = now;
        batch.PostedBy = uid;
        batch.PostedCount += 1;
        batch.PostingOperationId = opId;
        batch.ModifiedDate = now;
        batch.ModifiedBy = uid;

        return IvInventoryPostingBatchResult.Ok(batchNo, opId);
    }

    private async Task<IvInventoryPostingBatchResult> RollBackInventoryMIAsync(
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        string expectedTrxType,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var result = await RollBackInventoryMICoreAsync(
            db, companyCode, branchCode, userId, batchNo, expectedTrxType, cancellationToken);
        if (!result.Succeeded)
        {
            await tx.RollbackAsync(cancellationToken);
            return result;
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Rolled back {TrxType}. Company={Company} Branch={Branch} BatchNo={BatchNo} OpId={OpId} User={User}",
            expectedTrxType, companyCode, branchCode, batchNo, result.OperationId, Truncate(userId, 10));

        return result;
    }

    private async Task<IvInventoryPostingBatchResult> RollBackInventoryMICoreAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        string expectedTrxType,
        CancellationToken cancellationToken)
    {
        var batch = await _posting.LockBatchForUpdateAsync(db, companyCode, branchCode, batchNo, cancellationToken);
        if (batch is null
            || !string.Equals(batch.TrxType, expectedTrxType, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, StockOutNotFoundMessage(expectedTrxType));
        }

        if (!string.Equals(batch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, StockOutOnlyPostedCanRollbackMessage(expectedTrxType));
        }

        var details = await _posting.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
        var history = await _posting.LoadHistoryForBatchAsync(db, companyCode, branchCode, batchNo, cancellationToken);
        if (history.Count == 0)
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "No posted history found for this batch.");
        }

        var integrityError = ValidateMiHistoryIntegrity(details, history, companyCode, branchCode, batchNo, expectedTrxType);
        if (integrityError is not null)
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, integrityError);
        }

        var restoreById = new Dictionary<int, decimal>();
        var sliceById = new Dictionary<int, IvStockSliceKey>();
        foreach (var h in history)
        {
            var balLocId = h.FromBalLocId!.Value;
            var qty = h.FrStdQty!.Value;
            restoreById[balLocId] = restoreById.GetValueOrDefault(balLocId) + qty;

            // Slice key only for lock order; restore uses original FromBalLocId.
            var frWh = (h.FrWarehouse ?? string.Empty).Trim();
            var frLoc = (h.FrLocation ?? string.Empty).Trim();
            var frLot = (h.FrLotNo ?? string.Empty).Trim();
            var iStatus = (h.IStatus ?? string.Empty).Trim();
            sliceById[balLocId] = IvStockSliceKey.Create(
                companyCode, branchCode, h.ICode, frWh, frLoc, frLot, iStatus);
        }

        var orderedIds = sliceById
            .OrderBy(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var balLocId in orderedIds)
        {
            var lockedRow = await _posting.LockBalLocByIdForTenantAsync(
                db, balLocId, companyCode, branchCode, cancellationToken);
            if (lockedRow is null)
            {
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"ORPHAN_HISTORY: BalLoc Id {balLocId} missing for rollback.");
            }

            if (lockedRow.Id != balLocId
                || !string.Equals(lockedRow.CompanyCode, companyCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(lockedRow.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase))
            {
                return IvInventoryPostingBatchResult.Fail(batchNo, "Tenant mismatch on balance row.");
            }

            var restoreQty = restoreById[balLocId];
            var affected = await _posting.IncreaseBalLocQtyAsync(
                db, balLocId, companyCode, branchCode, restoreQty, batch.TrxDtTime, cancellationToken);
            if (affected != 1)
            {
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Stock restore failed for balance Id {balLocId}.");
            }
        }

        TestHookAfterMiRollbackStock?.Invoke();

        _posting.RemoveHistory(db, history);

        foreach (var detail in details)
        {
            detail.FromLotId = null;
            // Keep FromBalLocId and Fr* for possible re-post.
        }

        var now = DateTime.UtcNow;
        var uid = Truncate(userId, 10);
        var opId = Guid.NewGuid();
        batch.BatchStatus = IvBatchStatuses.New;
        batch.RollbackDate = now;
        batch.RollbackBy = uid;
        batch.RollbackCount += 1;
        batch.RollbackOperationId = opId;
        batch.ModifiedDate = now;
        batch.ModifiedBy = uid;

        return IvInventoryPostingBatchResult.Ok(batchNo, opId);
    }

    /// <summary>Test-only hook invoked after TR stock mutation, before history.</summary>
    internal Action? TestHookAfterTrStockUpdate { get; set; }

    /// <summary>Test-only hook invoked after TR history insert (before SaveChanges commits POSTED).</summary>
    internal Action? TestHookAfterTrHistory { get; set; }

    private async Task<IvInventoryPostingBatchResult> PostInventoryTRAsync(
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var batch = await _posting.LockBatchForUpdateAsync(db, companyCode, branchCode, batchNo, cancellationToken);
        if (batch is null
            || !string.Equals(batch.TrxType, IvTrxTypes.StockTransfer, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "Stock transfer was not found.");
        }

        if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "Only NEW stock transfers can be posted.");
        }

        var details = await _posting.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
        if (details.Count == 0)
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "Stock transfer has no lines.");
        }

        if (await _posting.HistoryExistsForBatchAsync(db, companyCode, branchCode, batchNo, cancellationToken))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "History already exists for this batch.");
        }

        var masters = await _posting.LockStockMastersAsync(
            db,
            companyCode,
            details.Select(d => d.ICode ?? string.Empty),
            cancellationToken);

        var linePlans = new List<TrLinePlan>(details.Count);
        foreach (var detail in details)
        {
            var planResult = await BuildTrPostLineAsync(
                db, companyCode, branchCode, detail, masters, cancellationToken);
            if (planResult.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, planResult.Error);
            }

            linePlans.Add(planResult.Plan!);
        }

        var decreaseBySourceId = new Dictionary<int, decimal>();
        var increaseByDestSlice = new Dictionary<IvStockSliceKey, decimal>();
        var sourceSliceById = new Dictionary<int, IvStockSliceKey>();
        var sourceCostById = new Dictionary<int, (decimal? Cost, decimal? UnitPrice)>();

        foreach (var plan in linePlans)
        {
            decreaseBySourceId[plan.FromBalLocId] =
                decreaseBySourceId.GetValueOrDefault(plan.FromBalLocId) + plan.Quantity;
            sourceSliceById[plan.FromBalLocId] = plan.FromSlice;
            increaseByDestSlice[plan.ToSlice] =
                increaseByDestSlice.GetValueOrDefault(plan.ToSlice) + plan.Quantity;
        }

        var orderedSourceIds = sourceSliceById
            .OrderBy(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();
        var lockedSources = new Dictionary<int, IvBalLocLockResult>();

        foreach (var balLocId in orderedSourceIds)
        {
            var lockedRow = await _posting.LockBalLocByIdForTenantAsync(
                db, balLocId, companyCode, branchCode, cancellationToken);
            if (lockedRow is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Source balance Id {balLocId} was not found for this company/branch.");
            }

            var expected = sourceSliceById[balLocId];
            if (!string.Equals(lockedRow.CompanyCode, expected.CompanyCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(lockedRow.BranchCode, expected.BranchCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(lockedRow.ICode, expected.ICode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(lockedRow.WhCode, expected.WhCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(lockedRow.LocCode, expected.LocCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(lockedRow.LotNo, expected.LotNo, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(lockedRow.IStatus, expected.IStatus, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Source balance Id {balLocId} no longer matches the transfer line (item/warehouse/location/lot/status).");
            }

            var stagedUom = linePlans.First(p => p.FromBalLocId == balLocId).Uom ?? string.Empty;
            var actualUom = (lockedRow.StdUom ?? string.Empty).Trim();
            if (!string.Equals(actualUom, stagedUom.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Source balance Id {balLocId} UOM no longer matches the transfer line.");
            }

            lockedSources[balLocId] = lockedRow;
            if (!sourceCostById.ContainsKey(balLocId))
            {
                var srcBal = await db.IvBalLocs
                    .AsNoTracking()
                    .Where(x => x.Id == balLocId)
                    .Select(x => new { x.Cost, x.UnitPrice })
                    .FirstOrDefaultAsync(cancellationToken);
                sourceCostById[balLocId] = (srcBal?.Cost, srcBal?.UnitPrice);
            }
        }

        foreach (var (balLocId, required) in decreaseBySourceId)
        {
            var actual = lockedSources[balLocId].StdQty;
            if (required > actual)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Insufficient quantity on balance Id {balLocId} (on hand {actual}, required {required}).");
            }
        }

        var now = DateTime.UtcNow;
        var uid = Truncate(userId, 10);
        var destLotByKey = new Dictionary<(string ICode, string LotNo), IvLot>();
        foreach (var plan in linePlans.Where(p => p.LotControl))
        {
            var lotNo = (plan.ToLotNo ?? string.Empty).Trim();
            if (lotNo.Length == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Line {plan.Detail.TrxLineNo}: destination lot number is required.");
            }

            var lotKey = (plan.ICode, lotNo);
            if (destLotByKey.ContainsKey(lotKey))
            {
                continue;
            }

            var existingLot = await _posting.TryLockLotAsync(
                db, companyCode, plan.ICode, lotNo, cancellationToken);
            if (existingLot is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Destination lot '{lotNo}' already exists for item '{plan.ICode}'.");
            }

            destLotByKey[lotKey] = _posting.StageNewTransferLot(
                db,
                companyCode,
                plan.ICode,
                lotNo,
                IvTrxTypes.StockTransfer,
                batchNo.ToString(),
                batch.TrxDtTime.Date,
                uid);
            plan.DestLot = destLotByKey[lotKey];
        }

        var orderedDestSlices = _posting.GetOrderedStockSlices(increaseByDestSlice.Keys);
        var lockedDest = new Dictionary<IvStockSliceKey, IvBalLoc>();

        foreach (var slice in orderedDestSlices)
        {
            var plan = linePlans.First(p => p.ToSlice.Equals(slice));
            int? destLotId = null;
            if (plan.LotControl && !string.IsNullOrWhiteSpace(plan.ToLotNo))
            {
                var lot = destLotByKey[(plan.ICode, plan.ToLotNo.Trim())];
                destLotId = lot.Id > 0 ? lot.Id : null;
            }

            var bal = await _posting.FindOrCreateBalLocAsync(
                db,
                slice,
                destLotId,
                plan.Uom,
                userId,
                batch.TrxDtTime,
                batch.LocationCode,
                cancellationToken);

            if (plan.LotControl && !string.IsNullOrWhiteSpace(plan.ToLotNo) && bal.LotId is null)
            {
                bal.Lot = destLotByKey[(plan.ICode, plan.ToLotNo.Trim())];
            }

            lockedDest[slice] = bal;
        }

        foreach (var balLocId in orderedSourceIds)
        {
            var required = decreaseBySourceId[balLocId];
            var affected = await _posting.DecreaseBalLocQtyAsync(
                db, balLocId, companyCode, branchCode, required, batch.TrxDtTime, cancellationToken);
            if (affected != 1)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Stock decrease failed for balance Id {balLocId} (insufficient quantity or missing row).");
            }
        }

        foreach (var slice in orderedDestSlices)
        {
            var bal = lockedDest[slice];
            var qtyBefore = bal.StdQty;
            var delta = increaseByDestSlice[slice];
            var sourceId = linePlans.First(p => p.ToSlice.Equals(slice)).FromBalLocId;
            var (srcCost, srcUnitPrice) = sourceCostById[sourceId];

            if (qtyBefore == 0m)
            {
                ApplyDestCostFromSource(bal, srcCost, srcUnitPrice);
            }

            bal.StdQty += delta;
            bal.ModifiedDate = now;
            bal.TransDate = batch.TrxDtTime;
            if (string.IsNullOrWhiteSpace(bal.StdUom))
            {
                bal.StdUom = Truncate(linePlans.First(p => p.ToSlice.Equals(slice)).Uom, 10);
            }
        }

        TestHookAfterTrStockUpdate?.Invoke();

        var opId = Guid.NewGuid();
        foreach (var plan in linePlans)
        {
            var detail = plan.Detail;
            var lockedSource = lockedSources[plan.FromBalLocId];
            var destBal = lockedDest[plan.ToSlice];
            var (srcCost, srcUnitPrice) = sourceCostById[plan.FromBalLocId];

            detail.FromBalLocId = plan.FromBalLocId;
            detail.FromLotId = lockedSource.LotId;
            detail.ToBalLocId = destBal.Id;
            detail.ToLotNo = plan.LotControl ? (plan.ToLotNo ?? detail.ToLotNo) : string.Empty;
            if (plan.LotControl && plan.DestLot is not null)
            {
                detail.ToLot = plan.DestLot;
            }
            else
            {
                detail.ToLotId = null;
            }

            _posting.AddHistory(db, new IvTrxHistory
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                BatchNo = batchNo,
                TrxLineNo = detail.TrxLineNo,
                TrxDtTime = batch.TrxDtTime,
                TrxType = IvTrxTypes.StockTransfer,
                BatchStatus = IvBatchStatuses.Posted,
                RefNo = batch.RefNo,
                ProdCode = detail.ProdCode ?? detail.ICode,
                ProdDesc = detail.ProdDesc ?? detail.IDesc,
                ICode = detail.ICode ?? plan.ICode,
                IDesc = detail.IDesc,
                FrWarehouse = detail.FrWarehouse,
                FrLocation = detail.FrLocation,
                FrLotNo = detail.FrLotNo,
                FrStdQty = detail.FrStdQty,
                FrStdUom = detail.FrStdUom,
                ToWarehouse = detail.ToWarehouse,
                ToLocation = detail.ToLocation,
                ToLotNo = detail.ToLotNo,
                ToStdQty = detail.ToStdQty,
                ToStdUom = detail.ToStdUom,
                IStatus = detail.IStatus,
                Remarks = detail.Remarks,
                UnitPrice = srcUnitPrice,
                Cost = srcCost,
                CostPrice = detail.CostPrice,
                BaseUnitPrices = detail.BaseUnitPrices,
                LocationCode = detail.LocationCode,
                FromBalLocId = plan.FromBalLocId,
                FromLotId = lockedSource.LotId,
                ToBalLocId = destBal.Id,
                ToLot = plan.LotControl ? plan.DestLot : null,
                CreatedDate = now,
                CreatedBy = uid
            });
        }

        TestHookAfterTrHistory?.Invoke();

        batch.BatchStatus = IvBatchStatuses.Posted;
        batch.PostedDate = now;
        batch.PostedBy = uid;
        batch.PostedCount += 1;
        batch.PostingOperationId = opId;
        batch.ModifiedDate = now;
        batch.ModifiedBy = uid;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            var lotPlan = linePlans.FirstOrDefault(p => p.LotControl);
            if (lotPlan is not null && !string.IsNullOrWhiteSpace(lotPlan.ToLotNo))
            {
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Destination lot '{lotPlan.ToLotNo}' already exists for item '{lotPlan.ICode}'.");
            }

            return IvInventoryPostingBatchResult.Fail(batchNo, "Destination lot already exists.");
        }

        _logger.LogInformation(
            "Posted TR. Company={Company} Branch={Branch} BatchNo={BatchNo} OpId={OpId} User={User}",
            companyCode, branchCode, batchNo, opId, uid);

        return IvInventoryPostingBatchResult.Ok(batchNo, opId);
    }

    private async Task<IvInventoryPostingBatchResult> RollBackInventoryTRAsync(
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var batch = await _posting.LockBatchForUpdateAsync(db, companyCode, branchCode, batchNo, cancellationToken);
        if (batch is null
            || !string.Equals(batch.TrxType, IvTrxTypes.StockTransfer, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "Stock transfer was not found.");
        }

        if (!string.Equals(batch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "Only POSTED stock transfers can be rolled back.");
        }

        var details = await _posting.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
        var history = await _posting.LoadHistoryForBatchAsync(db, companyCode, branchCode, batchNo, cancellationToken);
        if (history.Count == 0)
        {
            return IvInventoryPostingBatchResult.Fail(batchNo, "No posted history found for this batch.");
        }

        var integrityError = ValidateTrHistoryIntegrity(details, history, companyCode, branchCode, batchNo);
        if (integrityError is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvInventoryPostingBatchResult.Fail(batchNo, integrityError);
        }

        var destDecreaseById = new Dictionary<int, decimal>();
        var sourceRestoreById = new Dictionary<int, decimal>();
        var sliceById = new Dictionary<int, IvStockSliceKey>();

        foreach (var h in history)
        {
            var toId = h.ToBalLocId!.Value;
            var frId = h.FromBalLocId!.Value;
            var qty = h.FrStdQty!.Value;
            destDecreaseById[toId] = destDecreaseById.GetValueOrDefault(toId) + qty;
            sourceRestoreById[frId] = sourceRestoreById.GetValueOrDefault(frId) + qty;

            var frWh = (h.FrWarehouse ?? string.Empty).Trim();
            var frLoc = (h.FrLocation ?? string.Empty).Trim();
            var frLot = (h.FrLotNo ?? string.Empty).Trim();
            var iStatus = (h.IStatus ?? string.Empty).Trim();
            sliceById[frId] = IvStockSliceKey.Create(
                companyCode, branchCode, h.ICode, frWh, frLoc, frLot, iStatus);

            var toWh = (h.ToWarehouse ?? string.Empty).Trim();
            var toLoc = (h.ToLocation ?? string.Empty).Trim();
            var toLot = (h.ToLotNo ?? string.Empty).Trim();
            sliceById[toId] = IvStockSliceKey.Create(
                companyCode, branchCode, h.ICode, toWh, toLoc, toLot, iStatus);
        }

        var orderedIds = sliceById
            .OrderBy(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var balLocId in orderedIds)
        {
            var lockedRow = await _posting.LockBalLocByIdForTenantAsync(
                db, balLocId, companyCode, branchCode, cancellationToken);
            if (lockedRow is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"ORPHAN_HISTORY: BalLoc Id {balLocId} missing for rollback.");
            }

            if (!string.Equals(lockedRow.CompanyCode, companyCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(lockedRow.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(batchNo, "Tenant mismatch on balance row.");
            }
        }

        foreach (var (balLocId, decrease) in destDecreaseById)
        {
            var locked = await _posting.LockBalLocByIdForTenantAsync(
                db, balLocId, companyCode, branchCode, cancellationToken);
            if (locked!.StdQty < decrease)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Insufficient quantity on destination balance Id {balLocId} for rollback (on hand {locked.StdQty}, required {decrease}).");
            }
        }

        foreach (var (balLocId, decrease) in destDecreaseById)
        {
            var affected = await _posting.DecreaseBalLocQtyAsync(
                db, balLocId, companyCode, branchCode, decrease, batch.TrxDtTime, cancellationToken);
            if (affected != 1)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Destination stock decrease failed for balance Id {balLocId}.");
            }
        }

        foreach (var (balLocId, restore) in sourceRestoreById)
        {
            var affected = await _posting.IncreaseBalLocQtyAsync(
                db, balLocId, companyCode, branchCode, restore, batch.TrxDtTime, cancellationToken);
            if (affected != 1)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvInventoryPostingBatchResult.Fail(
                    batchNo,
                    $"Source stock restore failed for balance Id {balLocId}.");
            }
        }

        _posting.RemoveHistory(db, history);

        foreach (var detail in details)
        {
            detail.ToBalLocId = null;
            detail.ToLotId = null;
            detail.FromLotId = null;
        }

        var now = DateTime.UtcNow;
        var uid = Truncate(userId, 10);
        var opId = Guid.NewGuid();
        batch.BatchStatus = IvBatchStatuses.New;
        batch.RollbackDate = now;
        batch.RollbackBy = uid;
        batch.RollbackCount += 1;
        batch.RollbackOperationId = opId;
        batch.ModifiedDate = now;
        batch.ModifiedBy = uid;

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Rolled back TR. Company={Company} Branch={Branch} BatchNo={BatchNo} OpId={OpId} User={User}",
            companyCode, branchCode, batchNo, opId, uid);

        return IvInventoryPostingBatchResult.Ok(batchNo, opId);
    }

    private static string? ValidateTrHistoryIntegrity(
        IReadOnlyList<IvTrxBatchDetail> details,
        IReadOnlyList<IvTrxHistory> history,
        string companyCode,
        string branchCode,
        int batchNo)
    {
        var detailByLine = details.ToDictionary(d => d.TrxLineNo);
        var historyByLine = new Dictionary<short, IvTrxHistory>();
        foreach (var h in history)
        {
            if (h.BatchNo != batchNo)
            {
                return $"History BatchNo mismatch (expected {batchNo}).";
            }

            if (!string.Equals(h.TrxType, IvTrxTypes.StockTransfer, StringComparison.OrdinalIgnoreCase))
            {
                return $"History line {h.TrxLineNo}: TrxType must be TR.";
            }

            if (!string.Equals(h.CompanyCode, companyCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(h.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase))
            {
                return $"History line {h.TrxLineNo}: company/branch mismatch.";
            }

            if (h.FromBalLocId is null or <= 0 || h.ToBalLocId is null or <= 0)
            {
                return $"History line {h.TrxLineNo}: FromBalLocId and ToBalLocId are required.";
            }

            if (h.FrStdQty is null || h.FrStdQty <= 0m || h.ToStdQty is null || h.ToStdQty <= 0m)
            {
                return $"History line {h.TrxLineNo}: posted quantity must be greater than zero.";
            }

            if (!historyByLine.TryAdd(h.TrxLineNo, h))
            {
                return $"History has duplicate TrxLineNo {h.TrxLineNo}.";
            }
        }

        foreach (var detail in details)
        {
            if (!historyByLine.TryGetValue(detail.TrxLineNo, out var h))
            {
                return $"Missing history for detail line {detail.TrxLineNo}.";
            }

            if (detail.FromBalLocId != h.FromBalLocId
                || detail.ToBalLocId != h.ToBalLocId
                || detail.FrStdQty != h.FrStdQty
                || detail.ToStdQty != h.ToStdQty)
            {
                return $"History/detail mismatch on line {detail.TrxLineNo}.";
            }
        }

        foreach (var lineNo in historyByLine.Keys)
        {
            if (!detailByLine.ContainsKey(lineNo))
            {
                return $"Orphan history for TrxLineNo {lineNo} (no matching detail).";
            }
        }

        return null;
    }

    private async Task<(string? Error, TrLinePlan? Plan)> BuildTrPostLineAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IvTrxBatchDetail detail,
        IReadOnlyDictionary<string, IvStockMaster> masters,
        CancellationToken cancellationToken)
    {
        var iCode = (detail.ICode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(iCode))
        {
            return ($"Line {detail.TrxLineNo}: item code is required.", null);
        }

        if (!masters.TryGetValue(iCode, out var item) || !item.IsActive)
        {
            return ($"Line {detail.TrxLineNo}: item '{iCode}' was not found or is inactive.", null);
        }

        if (!item.StockControl)
        {
            return ($"Line {detail.TrxLineNo}: item '{iCode}' is not stock-controlled.", null);
        }

        if (detail.FromBalLocId is null or <= 0)
        {
            return ($"Line {detail.TrxLineNo}: source balance is required.", null);
        }

        var frQty = detail.FrStdQty ?? 0m;
        var toQty = detail.ToStdQty ?? 0m;
        if (frQty <= 0m || toQty <= 0m)
        {
            return ($"Line {detail.TrxLineNo}: quantity must be greater than zero.", null);
        }

        if (frQty != toQty)
        {
            return ($"Line {detail.TrxLineNo}: source and destination quantity must match.", null);
        }

        var frWh = (detail.FrWarehouse ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(frWh))
        {
            return ($"Line {detail.TrxLineNo}: source warehouse is required.", null);
        }

        var frLoc = (detail.FrLocation ?? string.Empty).Trim();
        var frLot = (detail.FrLotNo ?? string.Empty).Trim();
        var toLot = (detail.ToLotNo ?? string.Empty).Trim();

        if (item.LotControl)
        {
            if (string.IsNullOrWhiteSpace(frLot))
            {
                return ($"Line {detail.TrxLineNo}: source lot number is required.", null);
            }

            if (string.IsNullOrWhiteSpace(toLot))
            {
                return ($"Line {detail.TrxLineNo}: destination lot number is required.", null);
            }

            if (string.Equals(frLot, toLot, StringComparison.OrdinalIgnoreCase))
            {
                return ($"Line {detail.TrxLineNo}: destination lot must differ from source lot.", null);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(frLot) || !string.IsNullOrWhiteSpace(toLot))
            {
                return ($"Line {detail.TrxLineNo}: lot number is not allowed for non-lot-controlled item.", null);
            }

            frLot = string.Empty;
            toLot = string.Empty;
        }

        var iStatus = (detail.IStatus ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(iStatus))
        {
            return ($"Line {detail.TrxLineNo}: item status is required.", null);
        }

        var frUom = (detail.FrStdUom ?? string.Empty).Trim();
        var toUom = (detail.ToStdUom ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(frUom) || string.IsNullOrWhiteSpace(toUom))
        {
            return ($"Line {detail.TrxLineNo}: UOM is required.", null);
        }

        if (!string.Equals(frUom, toUom, StringComparison.OrdinalIgnoreCase))
        {
            return ($"Line {detail.TrxLineNo}: source and destination UOM must match.", null);
        }

        var toWh = (detail.ToWarehouse ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(toWh))
        {
            return ($"Line {detail.TrxLineNo}: destination warehouse is required.", null);
        }

        var warehouse = await _common.GetActiveWarehouseAsync(db, companyCode, branchCode, toWh, cancellationToken);
        if (warehouse is null)
        {
            return ($"Line {detail.TrxLineNo}: warehouse '{toWh}' was not found for this branch.", null);
        }

        var hasLocations = await _common.HasActiveLocationsAsync(db, companyCode, branchCode, toWh, cancellationToken);
        var toLoc = (detail.ToLocation ?? string.Empty).Trim();
        if (hasLocations)
        {
            if (string.IsNullOrWhiteSpace(toLoc))
            {
                return ($"Line {detail.TrxLineNo}: location is required for warehouse '{toWh}'.", null);
            }

            var location = await _common.GetActiveLocationAsync(
                db, companyCode, branchCode, toWh, toLoc, cancellationToken);
            if (location is null)
            {
                return ($"Line {detail.TrxLineNo}: location '{toLoc}' was not found for warehouse '{toWh}'.", null);
            }
        }
        else
        {
            toLoc = string.Empty;
        }

        var fromSlice = IvStockSliceKey.Create(
            companyCode, branchCode, iCode, frWh, frLoc, frLot, iStatus);
        var toSlice = IvStockSliceKey.Create(
            companyCode, branchCode, iCode, toWh, toLoc, toLot, iStatus);

        if (fromSlice.Equals(toSlice))
        {
            return ($"Line {detail.TrxLineNo}: source and destination stock slice must be different.", null);
        }

        return (null, new TrLinePlan
        {
            Detail = detail,
            ICode = iCode,
            Quantity = frQty,
            Uom = frUom,
            LotControl = item.LotControl,
            ToLotNo = item.LotControl ? toLot : null,
            FromBalLocId = detail.FromBalLocId.Value,
            FromSlice = fromSlice,
            ToSlice = toSlice
        });
    }

    private static void ApplyDestCostFromSource(IvBalLoc bal, decimal? sourceCost, decimal? sourceUnitPrice)
    {
        if (bal.StdQty > 0m)
        {
            return;
        }

        bal.Cost = sourceCost;
        bal.UnitPrice = sourceUnitPrice;
    }

    private static string? ValidateMiHistoryIntegrity(
        IReadOnlyList<IvTrxBatchDetail> details,
        IReadOnlyList<IvTrxHistory> history,
        string companyCode,
        string branchCode,
        int batchNo,
        string expectedTrxType)
    {
        var detailByLine = details.ToDictionary(d => d.TrxLineNo);
        var historyByLine = new Dictionary<short, IvTrxHistory>();
        foreach (var h in history)
        {
            if (h.BatchNo != batchNo)
            {
                return $"History BatchNo mismatch (expected {batchNo}).";
            }

            if (!string.Equals(h.TrxType, expectedTrxType, StringComparison.OrdinalIgnoreCase))
            {
                return $"History line {h.TrxLineNo}: TrxType must be {expectedTrxType}.";
            }

            if (!string.Equals(h.CompanyCode, companyCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(h.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase))
            {
                return $"History line {h.TrxLineNo}: company/branch mismatch.";
            }

            if (h.FromBalLocId is null or <= 0)
            {
                return $"History line {h.TrxLineNo}: FromBalLocId is required.";
            }

            if (h.FrStdQty is null || h.FrStdQty <= 0m)
            {
                return $"History line {h.TrxLineNo}: posted quantity must be greater than zero.";
            }

            if (!historyByLine.TryAdd(h.TrxLineNo, h))
            {
                return $"History has duplicate TrxLineNo {h.TrxLineNo}.";
            }
        }

        foreach (var detail in details)
        {
            if (!historyByLine.ContainsKey(detail.TrxLineNo))
            {
                return $"Missing history for detail line {detail.TrxLineNo}.";
            }
        }

        foreach (var lineNo in historyByLine.Keys)
        {
            if (!detailByLine.ContainsKey(lineNo))
            {
                return $"Orphan history for TrxLineNo {lineNo} (no matching detail).";
            }
        }

        return null;
    }

    private static (string? Error, MiLinePlan? Plan) BuildMiPostLine(
        IvTrxBatchDetail detail,
        IReadOnlyDictionary<string, IvStockMaster> masters)
    {
        var iCode = (detail.ICode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(iCode))
        {
            return ($"Line {detail.TrxLineNo}: item code is required.", null);
        }

        if (!masters.TryGetValue(iCode, out var item) || !item.IsActive)
        {
            return ($"Line {detail.TrxLineNo}: item '{iCode}' was not found or is inactive.", null);
        }

        if (!item.StockControl)
        {
            return ($"Line {detail.TrxLineNo}: item '{iCode}' is not stock-controlled.", null);
        }

        var qty = detail.FrStdQty ?? 0m;
        if (qty <= 0m)
        {
            return ($"Line {detail.TrxLineNo}: quantity must be greater than zero.", null);
        }

        if (detail.FromBalLocId is null or <= 0)
        {
            return ($"Line {detail.TrxLineNo}: source balance is required.", null);
        }

        var frWh = (detail.FrWarehouse ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(frWh))
        {
            return ($"Line {detail.TrxLineNo}: warehouse is required.", null);
        }

        var frLoc = (detail.FrLocation ?? string.Empty).Trim();
        var iStatus = (detail.IStatus ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(iStatus))
        {
            return ($"Line {detail.TrxLineNo}: item status is required.", null);
        }

        var frLot = (detail.FrLotNo ?? string.Empty).Trim();
        if (item.LotControl)
        {
            if (string.IsNullOrWhiteSpace(frLot))
            {
                return ($"Line {detail.TrxLineNo}: lot number is required for lot-controlled item '{iCode}'.", null);
            }
        }
        else if (!string.IsNullOrWhiteSpace(frLot))
        {
            return ($"Line {detail.TrxLineNo}: lot number is not allowed for non-lot-controlled item '{iCode}'.", null);
        }

        var slice = IvStockSliceKey.Create(
            detail.CompanyCode,
            detail.BranchCode,
            iCode,
            frWh,
            frLoc,
            item.LotControl ? frLot : string.Empty,
            iStatus);

        return (null, new MiLinePlan
        {
            Detail = detail,
            ICode = iCode,
            Quantity = qty,
            FromBalLocId = detail.FromBalLocId.Value,
            FromSlice = slice
        });
    }

    private async Task<(string? Error, MrLinePlan? Plan)> BuildMrPostLineAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IvTrxBatchDetail detail,
        IReadOnlyDictionary<string, IvStockMaster> masters,
        CancellationToken cancellationToken)
    {
        var iCode = (detail.ICode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(iCode))
        {
            return ($"Line {detail.TrxLineNo}: item code is required.", null);
        }

        if (!masters.TryGetValue(iCode, out var item) || !item.IsActive)
        {
            return ($"Line {detail.TrxLineNo}: item '{iCode}' was not found or is inactive.", null);
        }

        var qty = detail.ToStdQty ?? 0m;
        if (qty <= 0m)
        {
            return ($"Line {detail.TrxLineNo}: quantity must be greater than zero.", null);
        }

        var toWh = (detail.ToWarehouse ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(toWh))
        {
            return ($"Line {detail.TrxLineNo}: warehouse is required.", null);
        }

        var warehouse = await _common.GetActiveWarehouseAsync(db, companyCode, branchCode, toWh, cancellationToken);
        if (warehouse is null)
        {
            return ($"Line {detail.TrxLineNo}: warehouse '{toWh}' was not found for this branch.", null);
        }

        var hasLocations = await _common.HasActiveLocationsAsync(db, companyCode, branchCode, toWh, cancellationToken);
        var toLoc = (detail.ToLocation ?? string.Empty).Trim();
        if (hasLocations)
        {
            if (string.IsNullOrWhiteSpace(toLoc))
            {
                return ($"Line {detail.TrxLineNo}: location is required for warehouse '{toWh}'.", null);
            }

            var location = await _common.GetActiveLocationAsync(
                db, companyCode, branchCode, toWh, toLoc, cancellationToken);
            if (location is null)
            {
                return ($"Line {detail.TrxLineNo}: location '{toLoc}' was not found for warehouse '{toWh}'.", null);
            }
        }
        else
        {
            toLoc = string.Empty;
        }

        var iStatus = (detail.IStatus ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(iStatus))
        {
            return ($"Line {detail.TrxLineNo}: item status is required.", null);
        }

        var status = await _common.GetActiveStatusAsync(db, companyCode, iStatus, cancellationToken);
        if (status is null)
        {
            return ($"Line {detail.TrxLineNo}: item status '{iStatus}' was not found.", null);
        }

        string? lotNo = null;
        DateTime? expiry = detail.ExpiryDate?.Date;
        if (item.LotControl)
        {
            lotNo = (detail.ToLotNo ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(lotNo))
            {
                return ($"Line {detail.TrxLineNo}: lot number is required for lot-controlled item '{iCode}'.", null);
            }

            if (expiry is null)
            {
                return ($"Line {detail.TrxLineNo}: expiry date is required for lot-controlled item '{iCode}'.", null);
            }
        }
        else
        {
            lotNo = string.Empty;
            expiry = null;
        }

        IvStockSliceKey? slice = null;
        if (item.StockControl)
        {
            slice = IvStockSliceKey.Create(
                companyCode,
                branchCode,
                iCode,
                toWh,
                toLoc,
                item.LotControl ? lotNo : string.Empty,
                iStatus);
        }

        return (null, new MrLinePlan
        {
            Detail = detail,
            ICode = iCode,
            Quantity = qty,
            Uom = detail.ToStdUom,
            StockControl = item.StockControl,
            LotControl = item.LotControl,
            LotNo = item.LotControl ? lotNo : null,
            ExpiryDate = expiry,
            ToSlice = slice
        });
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
               || message.Contains("2627", StringComparison.Ordinal)
               || message.Contains("2601", StringComparison.Ordinal);
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static bool IsCustomerReturnTrxType(string expectedTrxType) =>
        string.Equals(expectedTrxType, IvTrxTypes.CustomerReturn, StringComparison.OrdinalIgnoreCase);

    private static string StockInNotFoundMessage(string expectedTrxType) =>
        IsCustomerReturnTrxType(expectedTrxType)
            ? "Stock return was not found."
            : "Miscellaneous receipt was not found.";

    private static string StockInOnlyNewCanPostMessage(string expectedTrxType) =>
        IsCustomerReturnTrxType(expectedTrxType)
            ? "Only NEW stock returns can be posted."
            : "Only NEW receipts can be posted.";

    private static string StockInNoLinesMessage(string expectedTrxType) =>
        IsCustomerReturnTrxType(expectedTrxType)
            ? "Stock return has no lines."
            : "Receipt has no lines.";

    private static string StockInOnlyPostedCanRollbackMessage(string expectedTrxType) =>
        IsCustomerReturnTrxType(expectedTrxType)
            ? "Only POSTED stock returns can be rolled back."
            : "Only POSTED receipts can be rolled back.";

    private static bool IsScrapTrxType(string expectedTrxType) =>
        string.Equals(expectedTrxType, IvTrxTypes.Scrap, StringComparison.OrdinalIgnoreCase);

    private static string StockOutNotFoundMessage(string expectedTrxType) =>
        IsScrapTrxType(expectedTrxType)
            ? "Scrap was not found."
            : "Miscellaneous issue was not found.";

    private static string StockOutOnlyNewCanPostMessage(string expectedTrxType) =>
        IsScrapTrxType(expectedTrxType)
            ? "Only NEW scrap documents can be posted."
            : "Only NEW issues can be posted.";

    private static string StockOutNoLinesMessage(string expectedTrxType) =>
        IsScrapTrxType(expectedTrxType)
            ? "Scrap has no lines."
            : "Issue has no lines.";

    private static string StockOutOnlyPostedCanRollbackMessage(string expectedTrxType) =>
        IsScrapTrxType(expectedTrxType)
            ? "Only POSTED scrap documents can be rolled back."
            : "Only POSTED issues can be rolled back.";

    private static string StockOutSourceMismatchMessage(string expectedTrxType, int balLocId) =>
        IsScrapTrxType(expectedTrxType)
            ? $"Source balance Id {balLocId} no longer matches the scrap line (item/warehouse/location/lot/status)."
            : $"Source balance Id {balLocId} no longer matches the issue line (item/warehouse/location/lot/status).";

    private sealed class MrLinePlan
    {
        public required IvTrxBatchDetail Detail { get; init; }
        public required string ICode { get; init; }
        public required decimal Quantity { get; init; }
        public string? Uom { get; init; }
        public bool StockControl { get; init; }
        public bool LotControl { get; init; }
        public string? LotNo { get; init; }
        public DateTime? ExpiryDate { get; init; }
        public IvStockSliceKey? ToSlice { get; init; }
        public int? LotId { get; set; }
        public int? BalLocId { get; set; }
    }

    private sealed class MiLinePlan
    {
        public required IvTrxBatchDetail Detail { get; init; }
        public required string ICode { get; init; }
        public required decimal Quantity { get; init; }
        public required int FromBalLocId { get; init; }
        public required IvStockSliceKey FromSlice { get; init; }
    }

    private sealed class TrLinePlan
    {
        public required IvTrxBatchDetail Detail { get; init; }
        public required string ICode { get; init; }
        public required decimal Quantity { get; init; }
        public string? Uom { get; init; }
        public bool LotControl { get; init; }
        public string? ToLotNo { get; init; }
        public IvLot? DestLot { get; set; }
        public required int FromBalLocId { get; init; }
        public required IvStockSliceKey FromSlice { get; init; }
        public required IvStockSliceKey ToSlice { get; init; }
    }
}
