using ErpWeb.Core.Inventory;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Subcon;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Subcon;

/// <summary>
/// Gate G3 atomic Inventory custody adapter for non-lot, same-SKU Subcon Send-Out and Receive-Back.
/// Owns a single SQL transaction + BranchStockTransactionLock + single Inventory TR batch + single V2 StockPosting root.
/// Fails closed on lot-controlled items, backdated historical replay (RequiresReplay == true), and disabled policy flags.
/// </summary>
public sealed class SubconInventoryTransferAdapter : ISubconInventorySourceAdapter
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IIvInventoryPostingService _inventoryPosting;
    private readonly IRunningNumberService _numbers;
    private readonly IUomConversionService _uomConverter;
    private readonly IInventorySoftReservationReader? _softReservations;

    /// <summary>Test-only crash-injection hook invoked after Subcon links/allocations/status are staged, before seal/commit.</summary>
    internal Action? TestHookAfterSubconLinkWrite { get; set; }

    public SubconInventoryTransferAdapter(
        IDbContextFactory<AppDbContext> dbFactory,
        IIvInventoryPostingService inventoryPosting,
        IRunningNumberService numbers,
        IUomConversionService uomConverter,
        IInventorySoftReservationReader? softReservations = null)
    {
        _dbFactory = dbFactory;
        _inventoryPosting = inventoryPosting;
        _numbers = numbers;
        _uomConverter = uomConverter;
        _softReservations = softReservations;
    }

    public bool SupportsLiveCustodyPosting => true;

    public Task<SubconResult> ValidateBeforePostAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default) =>
        Task.FromResult(SubconResult.OkDispatch(dispatch));

    public Task<SubconResult> PostAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default) =>
        Task.FromResult(SubconResult.Fail(SubconMessages.CompanyBranchRequired));

    public Task<SubconResult> RollbackAsync(SubconDispatchDto dispatch, CancellationToken cancellationToken = default) =>
        Task.FromResult(SubconResult.Fail(SubconMessages.CompanyBranchRequired));

    public Task<SubconResult> ValidateReceiptBeforePostAsync(SubconReceiptDto receipt, CancellationToken cancellationToken = default) =>
        Task.FromResult(SubconResult.OkReceipt(receipt));

    public Task<SubconResult> PostReceiptAsync(SubconReceiptDto receipt, CancellationToken cancellationToken = default) =>
        Task.FromResult(SubconResult.Fail(SubconMessages.CompanyBranchRequired));

    public Task<SubconResult> RollbackReceiptAsync(SubconReceiptDto receipt, CancellationToken cancellationToken = default) =>
        Task.FromResult(SubconResult.Fail(SubconMessages.CompanyBranchRequired));

    public async Task<SubconResult> PostAsync(
        SubconKeyCommand command,
        SubconDispatchDto loaded,
        InventoryTenantScope scope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scope.BranchCode))
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);

        var docNo = (command.DocNo ?? string.Empty).Trim();
        var requestId = command.RequestId is { } rid && rid != Guid.Empty ? rid : Guid.NewGuid();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        StockPostingContext? postingContext = null;
        try
        {
            await new BranchStockTransactionLock().AcquireAsync(
                db, scope.CompanyCode, scope.BranchCode, cancellationToken);

            var entity = await LockDispatchAsync(db, scope, docNo, cancellationToken);
            if (entity is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.DispatchNotFound);
            }

            // Idempotent timeout recovery when caller supplies an explicit RequestId.
            if (command.RequestId is { } explicitReqId && explicitReqId != Guid.Empty)
            {
                var priorByReq = await db.SubconPostingLinks.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
                                               && x.BranchCode == scope.BranchCode
                                               && x.DocKind == SubconPostingDocKinds.Dispatch
                                               && x.PostingRole == SubconPostingRoles.Primary
                                               && x.RequestId == explicitReqId,
                        cancellationToken);
                if (priorByReq is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    if (priorByReq.DocId != entity.Uid)
                        return SubconResult.Fail(SubconMessages.RequestIdConflict);
                    if (SubconStatuses.IsPosted(entity.Status))
                        return SubconResult.OkDispatch(MapPostedDispatch(entity, priorByReq));
                    return SubconResult.Fail(SubconMessages.ChangedReload);
                }
            }

            var policy = await LoadPublishedPolicyAsync(db, scope.CompanyCode, cancellationToken);
            if (policy is null || !policy.SubconEnabled || !policy.InventoryCustodyPostEnabled)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconBlockedReasons.PostingNotEnabled);
            }

            if (!SubconModes.IsInventory(entity.Mode))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconBlockedReasons.NoProductionCustody);
            }

            if (!SubconStatuses.CanEdit(entity.Status))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.OnlyNewPostable);
            }

            if (Stale(entity.RowVersion, command.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.ChangedReload);
            }

            if (entity.Lines.Count == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.LineRequired);
            }

            // Resolve and validate supplier custody warehouse + supplier location.
            var custodyWh = (policy.DefaultCustodyWarehouseCode ?? string.Empty).Trim();
            if (custodyWh.Length == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.CustodyWarehouseRequired);
            }

            var whRow = await db.IvWarehouses.AsNoTracking()
                .SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
                                           && x.BranchCode == scope.BranchCode
                                           && x.WarehouseCode == custodyWh
                                           && x.IsActive,
                    cancellationToken);
            if (whRow is null || !SubconCustodyGuardService.IsCustodyWarehouseType(whRow.WarehouseType))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.CustodyWarehouseRequired);
            }

            var custodyLoc = entity.SupplierCode.Trim();
            var locExists = await db.IvLocations.AsNoTracking()
                .AnyAsync(x => x.CompanyCode == scope.CompanyCode
                               && x.BranchCode == scope.BranchCode
                               && x.WarehouseCode == custodyWh
                               && x.LocCode == custodyLoc
                               && x.IsActive,
                    cancellationToken);
            if (!locExists)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail($"{SubconMessages.CustodyLocationRequired} ({custodyWh}/{custodyLoc})");
            }

            // Validate every line, convert UOM -> BaseQty, resolve non-lot source IvBalLoc, and check soft reservations.
            var orderedLines = entity.Lines.OrderBy(x => x.LineNo).ToList();
            var stagedLines = new List<(SubconDispatchLine Line, IvStockMaster Item, IvBalLoc SourceBal, decimal BaseQty, decimal ConvFactor)>(orderedLines.Count);
            var requiredBaseByBalLoc = new Dictionary<int, decimal>();

            foreach (var line in orderedLines)
            {
                var item = await db.IvStockMasters.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
                                               && x.ICode == line.ItemCode
                                               && x.IsActive,
                        cancellationToken);
                if (item is null || !item.StockControl)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail($"Line {line.LineNo}: {SubconMessages.StockControlRequired}");
                }

                if (item.LotControl || !string.IsNullOrWhiteSpace(line.LotNo))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail(SubconBlockedReasons.LotControlled);
                }

                var stdUom = (item.StdUom ?? string.Empty).Trim();
                if (stdUom.Length == 0)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail($"Line {line.LineNo}: item '{item.ICode}' has no base UOM.");
                }

                var (convOk, baseQty, convFactor, convErr) = await ResolveBaseQuantityAsync(
                    scope.CompanyCode, item.ICode, line.Qty, line.Uom, stdUom, cancellationToken);
                if (!convOk)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail($"Line {line.LineNo}: {convErr}");
                }

                var srcWh = (line.WarehouseCode ?? string.Empty).Trim();
                var srcLoc = (line.LocationCode ?? string.Empty).Trim();
                if (await SubconCustodyGuardService.Instance.IsCustodyWarehouseAsync(
                        db, scope.CompanyCode, scope.BranchCode, srcWh, cancellationToken))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail($"Line {line.LineNo}: {SubconCustodyConstants.SourceCustodyBlocked}");
                }

                var status = string.IsNullOrWhiteSpace(line.StockStatus) ? IvItemStatuses.Active : line.StockStatus.Trim().ToUpperInvariant();
                var srcBal = await db.IvBalLocs.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
                                               && x.BranchCode == scope.BranchCode
                                               && x.ICode == item.ICode
                                               && x.WhCode == srcWh
                                               && x.LocCode == srcLoc
                                               && x.IStatus == status
                                               && (x.LotNo == null || x.LotNo == "")
                                               && x.LotId == null,
                        cancellationToken);
                if (srcBal is null || srcBal.StdQty <= 0m)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail($"Line {line.LineNo}: insufficient available stock for '{item.ICode}' in {srcWh}/{srcLoc}.");
                }

                requiredBaseByBalLoc[srcBal.Id] = requiredBaseByBalLoc.GetValueOrDefault(srcBal.Id) + baseQty;
                stagedLines.Add((line, item, srcBal, baseQty, convFactor));
            }

            // Honor existing Sales soft reservations (SP / Delivery Request).
            if (_softReservations is not null && requiredBaseByBalLoc.Count > 0)
            {
                var locationScope = string.IsNullOrWhiteSpace(scope.LocationCode) ? scope.BranchCode : scope.LocationCode;
                var reserved = await _softReservations.GetReservedByBalanceAsync(
                    db, scope.CompanyCode, scope.BranchCode, locationScope,
                    requiredBaseByBalLoc.Keys.ToList(), cancellationToken: cancellationToken);
                foreach (var (balId, reqQty) in requiredBaseByBalLoc)
                {
                    var bal = stagedLines.First(x => x.SourceBal.Id == balId).SourceBal;
                    var resQty = reserved.GetValueOrDefault(balId)?.TotalReservedQty ?? 0m;
                    var available = IvQty.Round(bal.StdQty - resQty);
                    if (reqQty > available)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return SubconResult.Fail(
                            $"Insufficient unreserved stock on balance Id {balId} (on hand {bal.StdQty:0.####}, reserved {resQty:0.####}, required {reqQty:0.####}).");
                    }
                }
            }

            // Stage ONE underlying Inventory TR batch inside the same SQL transaction.
            var batchNo = await _numbers.GetNextAsync(db, scope.CompanyCode, RunningNumberKeys.IvBatch, cancellationToken);
            var now = DateTime.UtcNow;
            var userId = Truncate(scope.UserId, 10);
            var batch = new IvTrxBatch
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode,
                BatchNo = batchNo,
                TrxDtTime = entity.TrxDate.Date,
                TrxType = IvTrxTypes.StockTransfer,
                BatchStatus = IvBatchStatuses.New,
                RefNo = Truncate(entity.DocNo, 50),
                Remarks = Truncate($"Subcon Send-Out {entity.DocNo} ({entity.SupplierCode})", 250),
                LocationCode = Truncate(string.IsNullOrWhiteSpace(scope.LocationCode) ? scope.BranchCode : scope.LocationCode, 10),
                CreatedDate = now,
                CreatedBy = userId
            };

            short trxLineNo = 1;
            var detailBySubconLineUid = new Dictionary<long, IvTrxBatchDetail>(stagedLines.Count);
            foreach (var s in stagedLines)
            {
                var stdUom = (s.SourceBal.StdUom ?? s.Item.StdUom ?? s.Line.Uom).Trim();
                var detail = new IvTrxBatchDetail
                {
                    CompanyCode = scope.CompanyCode,
                    BranchCode = scope.BranchCode,
                    BatchNo = batchNo,
                    TrxLineNo = trxLineNo++,
                    TrxType = IvTrxTypes.StockTransfer,
                    ICode = s.Item.ICode,
                    IDesc = Truncate(s.Item.IDesc ?? s.Line.ItemName ?? s.Item.ICode, 200),
                    ProdCode = s.Item.ICode,
                    ProdDesc = Truncate(s.Item.IDesc ?? s.Line.ItemName ?? s.Item.ICode, 200),
                    FromBalLocId = s.SourceBal.Id,
                    FrWarehouse = s.SourceBal.WhCode,
                    FrLocation = s.SourceBal.LocCode,
                    FrLotNo = string.Empty,
                    ToWarehouse = custodyWh,
                    ToLocation = custodyLoc,
                    ToLotNo = string.Empty,
                    FrStdQty = IvQty.Round(s.BaseQty),
                    FrStdUom = stdUom,
                    ToStdQty = IvQty.Round(s.BaseQty),
                    ToStdUom = stdUom,
                    FromLotId = null,
                    IStatus = s.SourceBal.IStatus,
                    IClassCode = s.Item.IClassCode ?? string.Empty,
                    Cost = s.SourceBal.Cost,
                    UnitPrice = IvQty.Round(s.SourceBal.UnitPrice ?? 0m),
                    Remarks = Truncate(s.Line.Remarks, 250),
                    LocationCode = batch.LocationCode
                };
                batch.Details.Add(detail);
                detailBySubconLineUid[s.Line.Uid] = detail;
            }

            db.IvTrxBatches.Add(batch);
            await db.SaveChangesAsync(cancellationToken);

            // Open ONE V2 StockPosting root for this TR batch.
            var begin = await _inventoryPosting.BeginPostingInTransactionAsync(
                db, scope.CompanyCode, scope.BranchCode, batch.BatchNo,
                reversal: false, effectiveAt: batch.TrxDtTime, cancellationToken);
            postingContext = begin.Context;
            if (begin.Error is not null)
            {
                await AbortAndRollbackAsync(postingContext, tx);
                return SubconResult.Fail(begin.Error.Message);
            }

            var postResult = await _inventoryPosting.PostStockTransferInTransactionAsync(
                postingContext, db, scope.CompanyCode, scope.BranchCode, scope.UserId,
                batch.BatchNo, isAuthorizedSubconCaller: true, cancellationToken);
            if (!postResult.Succeeded)
            {
                await AbortAndRollbackAsync(postingContext, tx);
                return SubconResult.Fail(postResult.ErrorMessage ?? "Stock transfer posting failed.");
            }

            // Record immutable SubconPostingLink + SubconCustodyAllocation inside the same transaction.
            var link = new SubconPostingLink
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode,
                DocKind = SubconPostingDocKinds.Dispatch,
                DocId = entity.Uid,
                DocNo = entity.DocNo,
                JobId = entity.JobId,
                DocumentRevision = entity.DocumentRevision,
                PostingRole = SubconPostingRoles.Primary,
                TrxType = IvTrxTypes.StockTransfer,
                TrxBatchId = batch.Id,
                TrxBatchNo = batch.BatchNo,
                StockPostingId = postingContext?.Posting.Id,
                OperationId = postResult.OperationId ?? Guid.NewGuid(),
                RequestId = requestId,
                EffectiveAt = batch.TrxDtTime,
                IsActive = true,
                CreatedDate = now,
                CreatedBy = Truncate(scope.UserId, 20)
            };
            db.SubconPostingLinks.Add(link);
            await db.SaveChangesAsync(cancellationToken);

            foreach (var s in stagedLines)
            {
                var postedDetail = detailBySubconLineUid[s.Line.Uid];
                if (postedDetail.ToBalLocId is not > 0)
                {
                    await AbortAndRollbackAsync(postingContext, tx);
                    return SubconResult.Fail($"Line {s.Line.LineNo}: destination custody balance ID was not materialized.");
                }

                s.Line.BaseUom = (s.SourceBal.StdUom ?? s.Item.StdUom ?? s.Line.Uom).Trim();
                s.Line.BaseQty = IvQty.Round(s.BaseQty);
                s.Line.ConvFactor = s.ConvFactor;
                s.Line.StockStatus = s.SourceBal.IStatus;
                s.Line.IvBalLocId = s.SourceBal.Id;
                s.Line.CustodyWarehouseCode = custodyWh;
                s.Line.CustodyLocationCode = custodyLoc;
                s.Line.CustodyBalLocId = postedDetail.ToBalLocId.Value;

                db.SubconCustodyAllocations.Add(new SubconCustodyAllocation
                {
                    CompanyCode = scope.CompanyCode,
                    BranchCode = scope.BranchCode,
                    JobId = entity.JobId,
                    DispatchId = entity.Uid,
                    DispatchLineId = s.Line.Uid,
                    SupplierCode = entity.SupplierCode,
                    ItemCode = s.Item.ICode,
                    BaseUom = s.Line.BaseUom,
                    StockStatus = s.SourceBal.IStatus,
                    CustodyBalLocId = postedDetail.ToBalLocId.Value,
                    CustodyWarehouseCode = custodyWh,
                    CustodyLocationCode = custodyLoc,
                    DispatchedBaseQty = IvQty.Round(s.BaseQty),
                    ReturnedBaseQty = 0m,
                    ConsumedBaseQty = 0m,
                    ScrapBaseQty = 0m,
                    IsActive = true,
                    PostingLinkId = link.Uid,
                    CreatedDate = now,
                    CreatedBy = Truncate(scope.UserId, 20)
                });
            }

            entity.Status = SubconStatuses.Posted;
            entity.PostedDate = now;
            entity.PostedBy = Truncate(scope.UserId, 20);
            entity.PostedCount += 1;
            entity.ModifiedDate = now;
            entity.ModifiedBy = Truncate(scope.UserId, 20);

            TestHookAfterSubconLinkWrite?.Invoke();

            // Value pending V2 facts (WAC/FIFO/Standard) and seal posting atomically before commit.
            await _inventoryPosting.CompletePostingInTransactionAsync(postingContext, cancellationToken);
            postingContext = null;
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return SubconResult.OkDispatch(MapPostedDispatch(entity, link), info: $"Posted send-out {entity.DocNo} (TR Batch {batch.BatchNo}).");
        }
        catch
        {
            await AbortAndRollbackAsync(postingContext, tx);
            throw;
        }
    }

    public async Task<SubconResult> RollbackAsync(
        SubconKeyCommand command,
        SubconDispatchDto loaded,
        InventoryTenantScope scope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scope.BranchCode))
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);

        var docNo = (command.DocNo ?? string.Empty).Trim();
        var requestId = command.RequestId is { } rid && rid != Guid.Empty ? rid : Guid.NewGuid();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        StockPostingContext? postingContext = null;
        try
        {
            await new BranchStockTransactionLock().AcquireAsync(
                db, scope.CompanyCode, scope.BranchCode, cancellationToken);

            var entity = await LockDispatchAsync(db, scope, docNo, cancellationToken);
            if (entity is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.DispatchNotFound);
            }

            if (!SubconModes.IsInventory(entity.Mode))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconBlockedReasons.NoProductionCustody);
            }

            if (!SubconStatuses.IsPosted(entity.Status))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.OnlyPostedRollback);
            }

            if (Stale(entity.RowVersion, command.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.ChangedReload);
            }

            // Refuse rollback if any non-cancelled Receive-Back (draft or posted) still references this dispatch.
            var dependentReceipts = await db.SubconReceiptLines.AsNoTracking()
                .Where(x => x.DispatchId == entity.Uid && x.Receipt.Status != SubconStatuses.Cancelled)
                .Select(x => new { x.Receipt.DocNo, x.Receipt.Status })
                .Distinct()
                .ToListAsync(cancellationToken);
            if (dependentReceipts.Count > 0)
            {
                var docList = string.Join(", ", dependentReceipts.Select(r => $"{r.DocNo} ({r.Status})"));
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(
                    $"Cannot roll back send-out {entity.DocNo} while receive-back document(s) depend on it: {docList}. Roll back or delete those receive-back document(s) first.");
            }

            var activeLink = await db.SubconPostingLinks
                .SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
                                           && x.BranchCode == scope.BranchCode
                                           && x.DocKind == SubconPostingDocKinds.Dispatch
                                           && x.DocId == entity.Uid
                                           && x.PostingRole == SubconPostingRoles.Primary
                                           && x.IsActive,
                    cancellationToken);
            if (activeLink is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail("Active posting link for this send-out was not found.");
            }

            var allocations = await LockAllocationsByDispatchAsync(db, entity.Uid, cancellationToken);
            if (allocations.Any(a => a.ReturnedBaseQty > 0m || a.ConsumedBaseQty > 0m || a.ScrapBaseQty > 0m))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(
                    $"Cannot roll back send-out {entity.DocNo}: supplier custody allocation has already been returned, consumed, or scrapped.");
            }

            var begin = await _inventoryPosting.BeginPostingInTransactionAsync(
                db, scope.CompanyCode, scope.BranchCode, activeLink.TrxBatchNo,
                reversal: true, effectiveAt: activeLink.EffectiveAt, cancellationToken);
            postingContext = begin.Context;
            if (begin.Error is not null)
            {
                await AbortAndRollbackAsync(postingContext, tx);
                return SubconResult.Fail(begin.Error.Message);
            }

            var rollbackResult = await _inventoryPosting.RollBackStockTransferInTransactionAsync(
                postingContext, db, scope.CompanyCode, scope.BranchCode, scope.UserId,
                activeLink.TrxBatchNo, isAuthorizedSubconCaller: true, cancellationToken);
            if (!rollbackResult.Succeeded)
            {
                await AbortAndRollbackAsync(postingContext, tx);
                return SubconResult.Fail(rollbackResult.ErrorMessage ?? "Stock transfer rollback failed.");
            }

            var now = DateTime.UtcNow;
            var revLink = new SubconPostingLink
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode,
                DocKind = SubconPostingDocKinds.Dispatch,
                DocId = entity.Uid,
                DocNo = entity.DocNo,
                JobId = entity.JobId,
                DocumentRevision = entity.DocumentRevision,
                PostingRole = SubconPostingRoles.Reversal,
                TrxType = IvTrxTypes.StockTransfer,
                TrxBatchId = activeLink.TrxBatchId,
                TrxBatchNo = activeLink.TrxBatchNo,
                StockPostingId = postingContext?.Posting.Id,
                OperationId = rollbackResult.OperationId ?? Guid.NewGuid(),
                RequestId = requestId,
                EffectiveAt = activeLink.EffectiveAt,
                IsActive = false,
                CreatedDate = now,
                CreatedBy = Truncate(scope.UserId, 20)
            };
            db.SubconPostingLinks.Add(revLink);
            await db.SaveChangesAsync(cancellationToken);

            activeLink.IsActive = false;
            activeLink.ReversedByLinkId = revLink.Uid;

            foreach (var alloc in allocations)
            {
                alloc.IsActive = false;
                alloc.ModifiedDate = now;
                alloc.ModifiedBy = Truncate(scope.UserId, 20);
            }

            foreach (var line in entity.Lines)
            {
                line.CustodyBalLocId = null;
            }

            entity.Status = SubconStatuses.New;
            entity.DocumentRevision += 1;
            entity.RollbackDate = now;
            entity.RollbackBy = Truncate(scope.UserId, 20);
            entity.RollbackCount += 1;
            entity.ModifiedDate = now;
            entity.ModifiedBy = Truncate(scope.UserId, 20);

            TestHookAfterSubconLinkWrite?.Invoke();

            await _inventoryPosting.CompletePostingInTransactionAsync(postingContext, cancellationToken);
            postingContext = null;
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return SubconResult.OkDispatch(MapPostedDispatch(entity, null), info: $"Rolled back send-out {entity.DocNo} (TR Batch {activeLink.TrxBatchNo}).");
        }
        catch
        {
            await AbortAndRollbackAsync(postingContext, tx);
            throw;
        }
    }

    public async Task<SubconResult> PostReceiptAsync(
        SubconKeyCommand command,
        SubconReceiptDto loaded,
        InventoryTenantScope scope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scope.BranchCode))
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);

        var docNo = (command.DocNo ?? string.Empty).Trim();
        var requestId = command.RequestId is { } rid && rid != Guid.Empty ? rid : Guid.NewGuid();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        StockPostingContext? postingContext = null;
        try
        {
            await new BranchStockTransactionLock().AcquireAsync(
                db, scope.CompanyCode, scope.BranchCode, cancellationToken);

            var entity = await LockReceiptAsync(db, scope, docNo, cancellationToken);
            if (entity is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.ReceiptNotFound);
            }

            if (command.RequestId is { } explicitReqId && explicitReqId != Guid.Empty)
            {
                var priorByReq = await db.SubconPostingLinks.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
                                               && x.BranchCode == scope.BranchCode
                                               && x.DocKind == SubconPostingDocKinds.Receipt
                                               && x.PostingRole == SubconPostingRoles.Primary
                                               && x.RequestId == explicitReqId,
                        cancellationToken);
                if (priorByReq is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    if (priorByReq.DocId != entity.Uid)
                        return SubconResult.Fail(SubconMessages.RequestIdConflict);
                    if (SubconStatuses.IsPosted(entity.Status))
                        return SubconResult.OkReceipt(MapPostedReceipt(entity, priorByReq));
                    return SubconResult.Fail(SubconMessages.ChangedReload);
                }
            }

            var policy = await LoadPublishedPolicyAsync(db, scope.CompanyCode, cancellationToken);
            if (policy is null || !policy.SubconEnabled || !policy.InventoryCustodyPostEnabled)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconBlockedReasons.PostingNotEnabled);
            }

            if (!SubconStatuses.CanEdit(entity.Status))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.OnlyNewPostable);
            }

            if (Stale(entity.RowVersion, command.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.ChangedReload);
            }

            if (entity.Lines.Count == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.LineRequired);
            }

            var orderedLines = entity.Lines.OrderBy(x => x.LineNo).ToList();
            var dispatchLineIds = orderedLines.Select(x => x.DispatchLineId).Distinct().OrderBy(x => x).ToArray();
            var allocationsByDispatchLine = (await LockAllocationsByDispatchLinesAsync(db, dispatchLineIds, cancellationToken))
                .ToDictionary(x => x.DispatchLineId);

            var stagedLines = new List<(SubconReceiptLine Line, SubconCustodyAllocation Alloc, IvStockMaster Item, decimal BaseQty, decimal ConvFactor, string ToWh, string ToLoc)>(orderedLines.Count);
            var incomingBaseByAlloc = new Dictionary<long, decimal>();

            foreach (var line in orderedLines)
            {
                if (!SubconStatuses.IsPosted(line.Dispatch.Status))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail($"Line {line.LineNo}: {SubconMessages.DispatchMustBePosted}");
                }

                if (!string.Equals(line.Dispatch.SupplierCode, entity.SupplierCode, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail($"Line {line.LineNo}: receive-back supplier must match the send-out supplier.");
                }

                if (entity.TrxDate.Date < line.Dispatch.TrxDate.Date)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail($"Line {line.LineNo}: receive-back date ({entity.TrxDate:yyyy-MM-dd}) cannot precede send-out date ({line.Dispatch.TrxDate:yyyy-MM-dd}).");
                }

                if (!allocationsByDispatchLine.TryGetValue(line.DispatchLineId, out var alloc) || !alloc.IsActive)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail($"Line {line.LineNo}: no active supplier custody allocation found for {line.Dispatch.DocNo} line {line.DispatchLine.LineNo}.");
                }

                var item = await db.IvStockMasters.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode && x.ICode == alloc.ItemCode && x.IsActive,
                        cancellationToken);
                if (item is null || !item.StockControl)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail($"Line {line.LineNo}: {SubconMessages.StockControlRequired}");
                }

                if (item.LotControl)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail(SubconBlockedReasons.LotControlled);
                }

                var (convOk, baseQty, convFactor, convErr) = await ResolveBaseQuantityAsync(
                    scope.CompanyCode, alloc.ItemCode, line.Qty, line.Uom, alloc.BaseUom, cancellationToken);
                if (!convOk)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail($"Line {line.LineNo}: {convErr}");
                }

                var toWh = (string.IsNullOrWhiteSpace(line.ToWarehouseCode) ? line.DispatchLine.WarehouseCode : line.ToWarehouseCode ?? string.Empty).Trim();
                var toLoc = (string.IsNullOrWhiteSpace(line.ToLocationCode) ? line.DispatchLine.LocationCode : line.ToLocationCode ?? string.Empty).Trim();
                var destWh = await db.IvWarehouses.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
                                               && x.BranchCode == scope.BranchCode
                                               && x.WarehouseCode == toWh
                                               && x.IsActive,
                        cancellationToken);
                if (destWh is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail($"Line {line.LineNo}: destination warehouse '{toWh}' was not found or is inactive.");
                }

                if (SubconCustodyGuardService.IsCustodyWarehouseType(destWh.WarehouseType))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail($"Line {line.LineNo}: {SubconMessages.ReceiveDestinationCustodyForbidden}");
                }

                if (toLoc.Length > 0)
                {
                    var destLocValid = await db.IvLocations.AsNoTracking()
                        .AnyAsync(x => x.CompanyCode == scope.CompanyCode
                                       && x.BranchCode == scope.BranchCode
                                       && x.WarehouseCode == toWh
                                       && x.LocCode == toLoc
                                       && x.IsActive,
                            cancellationToken);
                    if (!destLocValid)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return SubconResult.Fail($"Line {line.LineNo}: destination location '{toLoc}' was not found in warehouse '{toWh}'.");
                    }
                }

                incomingBaseByAlloc[alloc.Uid] = incomingBaseByAlloc.GetValueOrDefault(alloc.Uid) + baseQty;
                var remainingBase = alloc.DispatchedBaseQty - alloc.ReturnedBaseQty - alloc.ConsumedBaseQty - alloc.ScrapBaseQty;
                if (incomingBaseByAlloc[alloc.Uid] > remainingBase)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail(
                        $"Line {line.LineNo}: receive quantity ({incomingBaseByAlloc[alloc.Uid]:0.####} {alloc.BaseUom}) exceeds remaining supplier custody balance ({Math.Max(0m, remainingBase):0.####} {alloc.BaseUom}).");
                }

                stagedLines.Add((line, alloc, item, baseQty, convFactor, toWh, toLoc));
            }

            // Stage ONE underlying Inventory TR batch (Supplier Custody -> Company Warehouse).
            var batchNo = await _numbers.GetNextAsync(db, scope.CompanyCode, RunningNumberKeys.IvBatch, cancellationToken);
            var now = DateTime.UtcNow;
            var userId = Truncate(scope.UserId, 10);
            var batch = new IvTrxBatch
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode,
                BatchNo = batchNo,
                TrxDtTime = entity.TrxDate.Date,
                TrxType = IvTrxTypes.StockTransfer,
                BatchStatus = IvBatchStatuses.New,
                RefNo = Truncate(entity.DocNo, 50),
                Remarks = Truncate($"Subcon Receive-Back {entity.DocNo} ({entity.SupplierCode})", 250),
                LocationCode = Truncate(string.IsNullOrWhiteSpace(scope.LocationCode) ? scope.BranchCode : scope.LocationCode, 10),
                CreatedDate = now,
                CreatedBy = userId
            };

            short trxLineNo = 1;
            var detailByReceiptLineUid = new Dictionary<long, IvTrxBatchDetail>(stagedLines.Count);
            foreach (var s in stagedLines)
            {
                var detail = new IvTrxBatchDetail
                {
                    CompanyCode = scope.CompanyCode,
                    BranchCode = scope.BranchCode,
                    BatchNo = batchNo,
                    TrxLineNo = trxLineNo++,
                    TrxType = IvTrxTypes.StockTransfer,
                    ICode = s.Item.ICode,
                    IDesc = Truncate(s.Item.IDesc ?? s.Item.ICode, 200),
                    ProdCode = s.Item.ICode,
                    ProdDesc = Truncate(s.Item.IDesc ?? s.Item.ICode, 200),
                    FromBalLocId = s.Alloc.CustodyBalLocId,
                    FrWarehouse = s.Alloc.CustodyWarehouseCode,
                    FrLocation = s.Alloc.CustodyLocationCode,
                    FrLotNo = string.Empty,
                    ToWarehouse = s.ToWh,
                    ToLocation = s.ToLoc,
                    ToLotNo = string.Empty,
                    FrStdQty = IvQty.Round(s.BaseQty),
                    FrStdUom = s.Alloc.BaseUom,
                    ToStdQty = IvQty.Round(s.BaseQty),
                    ToStdUom = s.Alloc.BaseUom,
                    FromLotId = null,
                    IStatus = s.Alloc.StockStatus,
                    IClassCode = s.Item.IClassCode ?? string.Empty,
                    Remarks = Truncate(s.Line.Remarks, 250),
                    LocationCode = batch.LocationCode
                };
                batch.Details.Add(detail);
                detailByReceiptLineUid[s.Line.Uid] = detail;
            }

            db.IvTrxBatches.Add(batch);
            await db.SaveChangesAsync(cancellationToken);

            var begin = await _inventoryPosting.BeginPostingInTransactionAsync(
                db, scope.CompanyCode, scope.BranchCode, batch.BatchNo,
                reversal: false, effectiveAt: batch.TrxDtTime, cancellationToken);
            postingContext = begin.Context;
            if (begin.Error is not null)
            {
                await AbortAndRollbackAsync(postingContext, tx);
                return SubconResult.Fail(begin.Error.Message);
            }

            var postResult = await _inventoryPosting.PostStockTransferInTransactionAsync(
                postingContext, db, scope.CompanyCode, scope.BranchCode, scope.UserId,
                batch.BatchNo, isAuthorizedSubconCaller: true, cancellationToken);
            if (!postResult.Succeeded)
            {
                await AbortAndRollbackAsync(postingContext, tx);
                return SubconResult.Fail(postResult.ErrorMessage ?? "Receive-back stock transfer posting failed.");
            }

            var link = new SubconPostingLink
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode,
                DocKind = SubconPostingDocKinds.Receipt,
                DocId = entity.Uid,
                DocNo = entity.DocNo,
                DocumentRevision = entity.DocumentRevision,
                PostingRole = SubconPostingRoles.Primary,
                TrxType = IvTrxTypes.StockTransfer,
                TrxBatchId = batch.Id,
                TrxBatchNo = batch.BatchNo,
                StockPostingId = postingContext?.Posting.Id,
                OperationId = postResult.OperationId ?? Guid.NewGuid(),
                RequestId = requestId,
                EffectiveAt = batch.TrxDtTime,
                IsActive = true,
                CreatedDate = now,
                CreatedBy = Truncate(scope.UserId, 20)
            };
            db.SubconPostingLinks.Add(link);

            foreach (var s in stagedLines)
            {
                var postedDetail = detailByReceiptLineUid[s.Line.Uid];
                s.Line.BaseQty = IvQty.Round(s.BaseQty);
                s.Line.ConvFactor = s.ConvFactor;
                s.Line.ToWarehouseCode = s.ToWh;
                s.Line.ToLocationCode = s.ToLoc;
                s.Line.ToBalLocId = postedDetail.ToBalLocId;

                s.Alloc.ReturnedBaseQty = IvQty.Round(s.Alloc.ReturnedBaseQty + s.BaseQty);
                s.Alloc.ModifiedDate = now;
                s.Alloc.ModifiedBy = Truncate(scope.UserId, 20);
            }

            entity.Status = SubconStatuses.Posted;
            entity.PostedDate = now;
            entity.PostedBy = Truncate(scope.UserId, 20);
            entity.PostedCount += 1;
            entity.ModifiedDate = now;
            entity.ModifiedBy = Truncate(scope.UserId, 20);

            TestHookAfterSubconLinkWrite?.Invoke();

            await _inventoryPosting.CompletePostingInTransactionAsync(postingContext, cancellationToken);
            postingContext = null;
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return SubconResult.OkReceipt(MapPostedReceipt(entity, link), info: $"Posted receive-back {entity.DocNo} (TR Batch {batch.BatchNo}).");
        }
        catch
        {
            await AbortAndRollbackAsync(postingContext, tx);
            throw;
        }
    }

    public async Task<SubconResult> RollbackReceiptAsync(
        SubconKeyCommand command,
        SubconReceiptDto loaded,
        InventoryTenantScope scope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scope.BranchCode))
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);

        var docNo = (command.DocNo ?? string.Empty).Trim();
        var requestId = command.RequestId is { } rid && rid != Guid.Empty ? rid : Guid.NewGuid();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        StockPostingContext? postingContext = null;
        try
        {
            await new BranchStockTransactionLock().AcquireAsync(
                db, scope.CompanyCode, scope.BranchCode, cancellationToken);

            var entity = await LockReceiptAsync(db, scope, docNo, cancellationToken);
            if (entity is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.ReceiptNotFound);
            }

            if (!SubconStatuses.IsPosted(entity.Status))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.OnlyPostedRollback);
            }

            if (Stale(entity.RowVersion, command.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.ChangedReload);
            }

            var activeLink = await db.SubconPostingLinks
                .SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
                                           && x.BranchCode == scope.BranchCode
                                           && x.DocKind == SubconPostingDocKinds.Receipt
                                           && x.DocId == entity.Uid
                                           && x.PostingRole == SubconPostingRoles.Primary
                                           && x.IsActive,
                    cancellationToken);
            if (activeLink is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail("Active posting link for this receive-back was not found.");
            }

            var dispatchLineIds = entity.Lines.Select(x => x.DispatchLineId).Distinct().OrderBy(x => x).ToArray();
            var allocationsByDispatchLine = (await LockAllocationsByDispatchLinesAsync(db, dispatchLineIds, cancellationToken))
                .ToDictionary(x => x.DispatchLineId);

            var begin = await _inventoryPosting.BeginPostingInTransactionAsync(
                db, scope.CompanyCode, scope.BranchCode, activeLink.TrxBatchNo,
                reversal: true, effectiveAt: activeLink.EffectiveAt, cancellationToken);
            postingContext = begin.Context;
            if (begin.Error is not null)
            {
                await AbortAndRollbackAsync(postingContext, tx);
                return SubconResult.Fail(begin.Error.Message);
            }

            var rollbackResult = await _inventoryPosting.RollBackStockTransferInTransactionAsync(
                postingContext, db, scope.CompanyCode, scope.BranchCode, scope.UserId,
                activeLink.TrxBatchNo, isAuthorizedSubconCaller: true, cancellationToken);
            if (!rollbackResult.Succeeded)
            {
                await AbortAndRollbackAsync(postingContext, tx);
                return SubconResult.Fail(rollbackResult.ErrorMessage ?? "Receive-back stock transfer rollback failed.");
            }

            var now = DateTime.UtcNow;
            var revLink = new SubconPostingLink
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode,
                DocKind = SubconPostingDocKinds.Receipt,
                DocId = entity.Uid,
                DocNo = entity.DocNo,
                DocumentRevision = entity.DocumentRevision,
                PostingRole = SubconPostingRoles.Reversal,
                TrxType = IvTrxTypes.StockTransfer,
                TrxBatchId = activeLink.TrxBatchId,
                TrxBatchNo = activeLink.TrxBatchNo,
                StockPostingId = postingContext?.Posting.Id,
                OperationId = rollbackResult.OperationId ?? Guid.NewGuid(),
                RequestId = requestId,
                EffectiveAt = activeLink.EffectiveAt,
                IsActive = false,
                CreatedDate = now,
                CreatedBy = Truncate(scope.UserId, 20)
            };
            db.SubconPostingLinks.Add(revLink);
            await db.SaveChangesAsync(cancellationToken);

            activeLink.IsActive = false;
            activeLink.ReversedByLinkId = revLink.Uid;

            foreach (var line in entity.Lines)
            {
                var lineBase = line.BaseQty ?? line.Qty;
                if (!allocationsByDispatchLine.TryGetValue(line.DispatchLineId, out var alloc)
                    || alloc.ReturnedBaseQty < lineBase)
                {
                    await AbortAndRollbackAsync(postingContext, tx);
                    return SubconResult.Fail($"Line {line.LineNo}: supplier custody allocation mismatch during rollback.");
                }

                alloc.ReturnedBaseQty = IvQty.Round(alloc.ReturnedBaseQty - lineBase);
                alloc.ModifiedDate = now;
                alloc.ModifiedBy = Truncate(scope.UserId, 20);
                line.ToBalLocId = null;
            }

            entity.Status = SubconStatuses.New;
            entity.DocumentRevision += 1;
            entity.RollbackDate = now;
            entity.RollbackBy = Truncate(scope.UserId, 20);
            entity.RollbackCount += 1;
            entity.ModifiedDate = now;
            entity.ModifiedBy = Truncate(scope.UserId, 20);

            TestHookAfterSubconLinkWrite?.Invoke();

            await _inventoryPosting.CompletePostingInTransactionAsync(postingContext, cancellationToken);
            postingContext = null;
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return SubconResult.OkReceipt(MapPostedReceipt(entity, null), info: $"Rolled back receive-back {entity.DocNo} (TR Batch {activeLink.TrxBatchNo}).");
        }
        catch
        {
            await AbortAndRollbackAsync(postingContext, tx);
            throw;
        }
    }

    private async Task AbortAndRollbackAsync(
        StockPostingContext? postingContext,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx)
    {
        if (postingContext is not null)
        {
            try
            {
                await _inventoryPosting.AbortPostingInTransactionAsync(postingContext, CancellationToken.None);
            }
            catch
            {
                // Preserve original exception while guaranteeing transaction rollback.
            }
        }

        try
        {
            await tx.RollbackAsync(CancellationToken.None);
        }
        catch
        {
            // Ignore double-rollback if connection already closed.
        }
    }

    private async Task<(bool Ok, decimal BaseQty, decimal ConvFactor, string? Error)> ResolveBaseQuantityAsync(
        string companyCode,
        string itemCode,
        decimal enteredQty,
        string enteredUom,
        string baseUom,
        CancellationToken cancellationToken)
    {
        if (enteredQty <= 0m)
            return (false, 0m, 0m, SubconMessages.QtyRequired);

        var from = (enteredUom ?? string.Empty).Trim();
        var to = (baseUom ?? string.Empty).Trim();
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            var rounded = IvQty.Round(enteredQty);
            return rounded > 0m
                ? (true, rounded, 1m, null)
                : (false, 0m, 0m, SubconMessages.QtyRequired);
        }

        var result = await _uomConverter.ConvertAsync(companyCode, itemCode, enteredQty, from, to, cancellationToken);
        if (!result.Succeeded || result.Quantity <= 0m)
            return (false, 0m, 0m, result.FailureMessage ?? SubconMessages.UomConversionInvalid);

        var baseQty = IvQty.Round(result.Quantity);
        if (baseQty <= 0m)
            return (false, 0m, 0m, SubconMessages.UomConversionInvalid);

        var factor = decimal.Round(baseQty / enteredQty, 6, MidpointRounding.AwayFromZero);
        return (true, baseQty, factor, null);
    }

    private static async Task<SubconPolicy?> LoadPublishedPolicyAsync(
        AppDbContext db, string companyCode, CancellationToken cancellationToken) =>
        await db.SubconPolicies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyCode == companyCode && x.IsPublished, cancellationToken);

    private static async Task<SubconDispatch?> LockDispatchAsync(
        AppDbContext db, InventoryTenantScope scope, string docNo, CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlServer())
        {
            await db.SubconDispatches
                .FromSqlInterpolated(
                    $"SELECT * FROM dbo.SubconDispatch WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode = {scope.CompanyCode} AND BranchCode = {scope.BranchCode!} AND DocNo = {docNo}")
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.SubconDispatches
            .Include(x => x.Lines)
            .SingleOrDefaultAsync(
                x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.DocNo == docNo,
                cancellationToken);
    }

    private static async Task<SubconReceipt?> LockReceiptAsync(
        AppDbContext db, InventoryTenantScope scope, string docNo, CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlServer())
        {
            await db.SubconReceipts
                .FromSqlInterpolated(
                    $"SELECT * FROM dbo.SubconReceipt WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode = {scope.CompanyCode} AND BranchCode = {scope.BranchCode!} AND DocNo = {docNo}")
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.SubconReceipts
            .Include(x => x.Lines).ThenInclude(x => x.Dispatch)
            .Include(x => x.Lines).ThenInclude(x => x.DispatchLine)
            .SingleOrDefaultAsync(
                x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.DocNo == docNo,
                cancellationToken);
    }

    private static async Task<List<SubconCustodyAllocation>> LockAllocationsByDispatchAsync(
        AppDbContext db, long dispatchId, CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlServer())
        {
            await db.SubconCustodyAllocations
                .FromSqlInterpolated(
                    $"SELECT * FROM dbo.SubconCustodyAllocation WITH (UPDLOCK, HOLDLOCK) WHERE DispatchId = {dispatchId} AND IsActive = 1 ORDER BY UID")
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }

        return await db.SubconCustodyAllocations
            .Where(x => x.DispatchId == dispatchId && x.IsActive)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
    }

    private static async Task<List<SubconCustodyAllocation>> LockAllocationsByDispatchLinesAsync(
        AppDbContext db, long[] dispatchLineIds, CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlServer())
        {
            foreach (var lineId in dispatchLineIds.OrderBy(x => x))
            {
                await db.SubconCustodyAllocations
                    .FromSqlInterpolated(
                        $"SELECT * FROM dbo.SubconCustodyAllocation WITH (UPDLOCK, HOLDLOCK) WHERE DispatchLineId = {lineId} AND IsActive = 1")
                    .AsNoTracking()
                    .FirstOrDefaultAsync(cancellationToken);
            }
        }

        return await db.SubconCustodyAllocations
            .Where(x => dispatchLineIds.Contains(x.DispatchLineId) && x.IsActive)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
    }

    private static SubconDispatchDto MapPostedDispatch(SubconDispatch entity, SubconPostingLink? activeLink) => new()
    {
        DocNo = entity.DocNo,
        TrxDate = entity.TrxDate,
        Status = entity.Status,
        Mode = entity.Mode,
        SupplierCode = entity.SupplierCode,
        SupplierName = entity.SupplierName,
        PromisedReturnDate = entity.PromisedReturnDate,
        PoNo = entity.PoNo,
        PoRelNo = entity.PoRelNo,
        PoLineNo = entity.PoLineNo,
        Remarks = entity.Remarks,
        DocumentRevision = entity.DocumentRevision,
        PostedDate = entity.PostedDate,
        PostedBy = entity.PostedBy,
        PostedTrxBatchNo = activeLink?.TrxBatchNo,
        PostedOperationId = activeLink?.OperationId,
        CanPost = SubconStatuses.CanEdit(entity.Status),
        CanRollback = SubconStatuses.IsPosted(entity.Status),
        RowVersion = entity.RowVersion,
        Lines = entity.Lines.OrderBy(x => x.LineNo).Select(x => new SubconDispatchLineDto
        {
            LineNo = x.LineNo,
            ItemCode = x.ItemCode,
            ItemName = x.ItemName,
            Uom = x.Uom,
            BaseUom = x.BaseUom,
            Qty = x.Qty,
            BaseQty = x.BaseQty,
            StockStatus = x.StockStatus,
            WarehouseCode = x.WarehouseCode,
            LocationCode = x.LocationCode,
            CustodyWarehouseCode = x.CustodyWarehouseCode,
            CustodyLocationCode = x.CustodyLocationCode,
            CustodyBalLocId = x.CustodyBalLocId,
            LotNo = x.LotNo,
            WorkOrderNo = x.WorkOrderNo,
            OperationCode = x.OperationCode,
            PromisedReturnDate = x.PromisedReturnDate,
            Remarks = x.Remarks
        }).ToArray(),
        PostBlockedReason = string.Empty
    };

    private static SubconReceiptDto MapPostedReceipt(SubconReceipt entity, SubconPostingLink? activeLink) => new()
    {
        DocNo = entity.DocNo,
        TrxDate = entity.TrxDate,
        Status = entity.Status,
        SupplierCode = entity.SupplierCode,
        SupplierName = entity.SupplierName,
        Remarks = entity.Remarks,
        DocumentRevision = entity.DocumentRevision,
        PostedDate = entity.PostedDate,
        PostedBy = entity.PostedBy,
        PostedTrxBatchNo = activeLink?.TrxBatchNo,
        PostedOperationId = activeLink?.OperationId,
        CanPost = SubconStatuses.CanEdit(entity.Status),
        CanRollback = SubconStatuses.IsPosted(entity.Status),
        RowVersion = entity.RowVersion,
        Lines = entity.Lines.OrderBy(x => x.LineNo).Select(x => new SubconReceiptLineDto
        {
            LineNo = x.LineNo,
            DispatchDocNo = x.Dispatch?.DocNo ?? string.Empty,
            DispatchLineNo = x.DispatchLine?.LineNo ?? 0,
            ItemCode = x.DispatchLine?.ItemCode,
            ItemName = x.DispatchLine?.ItemName,
            Qty = x.Qty,
            Uom = x.Uom,
            BaseQty = x.BaseQty,
            ToWarehouseCode = x.ToWarehouseCode,
            ToLocationCode = x.ToLocationCode
        }).ToArray(),
        PostBlockedReason = string.Empty
    };

    private static bool Stale(byte[] stored, byte[]? incoming) =>
        stored is { Length: > 0 }
        && (incoming is not { Length: > 0 } || !stored.SequenceEqual(incoming));

    private static string Truncate(string? value, int max)
    {
        var s = (value ?? string.Empty).Trim();
        return s.Length <= max ? s : s[..max];
    }
}
