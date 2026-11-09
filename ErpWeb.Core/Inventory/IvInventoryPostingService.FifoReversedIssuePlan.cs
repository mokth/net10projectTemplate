using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    internal sealed record FifoReversedReceiptPlan(
        string CompanyCode, string BranchCode, int BatchId, int BatchNo, string ReceiptType,
        int ReceiptRevision, DateTime ReceiptEffectiveAt, long? OriginalReceiptPostingId, bool Removal,
        Guid RootRequestId, string RootRequestFingerprint, string RootSourceSnapshotHash, string SourceEvidenceHash,
        int IssueHistoryId, int ReversalHistoryId, long IssueMovementId, long ReversalMovementId,
        int BalanceId, long LedgerEpochId, long BranchWatermark,
        decimal ReceiptQuantity, decimal ReceiptValue,
        StockReplayFifoReversedIssueProjection Projection,
        string PhysicalPlanHash, string FinancialPlanHash, string PlanHash,
        string ItemCode, decimal FinalQuantity, decimal FinalValue,
        long? PreviousGenerationId, long? ActiveIssueRestatementId, long? ActiveReversalRestatementId,
        IReadOnlyList<long> ExistingValueFactIds);

    // Bound to the transaction in which the complete plan was rebuilt under
    // the branch lock. It is not a public posting route or a writer by itself.
    internal sealed class FifoReversedReceiptPermit
    {
        private readonly AppDbContext _db;
        private readonly Guid _transactionId;
        private long? _activatedRootId;
        private bool _correctionClaimed;
        private FifoReversedReceiptPermit(AppDbContext db, Guid transactionId, FifoReversedReceiptPlan plan)
        {
            _db = db;
            _transactionId = transactionId;
            Plan = plan;
        }

        internal FifoReversedReceiptPlan Plan { get; }

        internal static async Task<FifoReversedReceiptPermit?> PrepareAsync(
            AppDbContext db, FifoReversedReceiptPlan prepared, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(db);
            ArgumentNullException.ThrowIfNull(prepared);
            var transaction = db.Database.CurrentTransaction;
            if (transaction is null) return null;
            var locked = await PlanIsolatedFifoReversedReceiptAsync(db, prepared.CompanyCode,
                prepared.BranchCode, prepared.BatchNo, prepared.Removal, ct);
            if (locked is null || locked.PlanHash != prepared.PlanHash
                || db.Database.CurrentTransaction?.TransactionId != transaction.TransactionId) return null;
            return new(db, transaction.TransactionId, locked);
        }

        // Identity and source freshness only. Receipt/PO authorization, QA,
        // period, and final reconciliation still belong to the root writer.
        internal async Task ValidateForRootAsync(StockPostingContext context, CancellationToken ct = default)
        {
            ValidateRootIdentity(context);
            if (context.Db.Database.IsSqlServer())
            {
                await using var command = context.Db.Database.GetDbConnection().CreateCommand();
                command.Transaction = context.Db.Database.CurrentTransaction!.GetDbTransaction();
                command.CommandText = "SELECT TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID'))";
                var sessionRoot = await command.ExecuteScalarAsync(ct);
                if (sessionRoot is null or DBNull || Convert.ToInt64(sessionRoot,
                        System.Globalization.CultureInfo.InvariantCulture) != context.Posting.Id)
                    throw new StockLedgerException(new(StockLedgerErrorCodes.StaleDocument,
                        "The FIFO correction has no matching active posting session.", Retryable: true));
            }
            var sourceHash = await ReadFifoReceiptSourceHashAsync(context.Db, Plan.CompanyCode,
                Plan.BranchCode, Plan.BatchNo, ct);
            if (sourceHash is null || sourceHash != Plan.SourceEvidenceHash)
                throw new StockLedgerException(new(StockLedgerErrorCodes.StaleDocument,
                    "The receipt or purchase order changed after the reversed FIFO issue was planned.", Retryable: true));
            ValidateRootIdentity(context);
        }

        // Called after all source, QA, purchase and period checks, immediately
        // before the root writes its receipt. The source will legitimately change
        // during that write, so the correction consumes this one-use capability.
        internal async Task ActivateForCorrectionAsync(StockPostingContext context, CancellationToken ct = default)
        {
            if (_activatedRootId is not null)
                throw new InvalidOperationException("The FIFO correction permit was already activated.");
            await ValidateForRootAsync(context, ct);
            _activatedRootId = context.Posting.Id;
        }

        internal void ClaimCorrection(StockPostingContext context)
        {
            ValidateRootIdentity(context);
            if (_correctionClaimed || _activatedRootId != context.Posting.Id)
                throw new StockLedgerException(new(StockLedgerErrorCodes.StaleDocument,
                    "The FIFO correction has no unused permit for this receipt root.", Retryable: true));
            _correctionClaimed = true;
        }

        private void ValidateRootIdentity(StockPostingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.EnsureUnsealed();
            var root = context.Posting;
            var transaction = context.Db.Database.CurrentTransaction;
            if (!ReferenceEquals(context.Db, _db) || transaction?.TransactionId != _transactionId
                || transaction.GetDbTransaction().Connection is null
                || context.Db.Entry(root).State == EntityState.Detached || root.Id <= 0
                || context.CompanyCode != Plan.CompanyCode || context.BranchCode != Plan.BranchCode
                || context.CostMethod != StockCostMethods.Fifo || context.Epoch.Id != Plan.LedgerEpochId
                || root.LedgerEpochId != Plan.LedgerEpochId || root.PostingSequence != checked(Plan.BranchWatermark + 1)
                || root.SourceModule != "INVENTORY" || root.SourceDocumentType != Plan.ReceiptType
                || root.SourceDocumentId != Plan.BatchId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || root.SourceDocumentNo != Plan.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || root.DocumentRevision != Plan.ReceiptRevision || root.EffectiveAt != Plan.ReceiptEffectiveAt
                || root.RequestId != Plan.RootRequestId || root.RequestFingerprint != Plan.RootRequestFingerprint
                || root.SourceSnapshotHash != Plan.RootSourceSnapshotHash
                || root.PostingRole != (Plan.Removal ? "REVERSAL" : "PRIMARY")
                || root.ReversesPostingId != Plan.OriginalReceiptPostingId)
                throw new StockLedgerException(new(StockLedgerErrorCodes.StaleDocument,
                    "The reversed FIFO issue plan does not match this receipt posting transaction.", Retryable: true));
        }
    }

    // Read-only preparation for an isolated changed-cost pair. The result does
    // not authorize a write or bypass the public reversed-issue guard. A future
    // writer must rebuild and compare it under the branch lock, prove its root
    // ownership and persist complete FIFO and Production correction lineage.
    internal static async Task<FifoReversedReceiptPlan?> PlanIsolatedFifoReversedReceiptAsync(
        AppDbContext db, string company, string branch, int batchNo, bool removal,
        CancellationToken ct = default, bool requireTransaction = true)
    {
        if (requireTransaction && db.Database.CurrentTransaction is null) return null;
        var batch = await db.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == company && b.BranchCode == branch && b.BatchNo == batchNo, ct);
        if (batch is null || batch.TrxType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
            || batch.BatchStatus != (removal ? IvBatchStatuses.Posted : IvBatchStatuses.New)
            || batch.DeletedAtUtc is not null || batch.ForceCloseDate is not null)
            return null;
        if (!await HasFifoReversedIssueEvidenceStoreAsync(db, ct)) return null;
        var details = await db.IvTrxBatchDetails.AsNoTracking().Where(d => d.BatchId == batch.Id).ToArrayAsync(ct);
        if (details.Length != 1 || details[0].CompanyCode != company || details[0].BranchCode != branch
            || details[0].BatchNo != batchNo || details[0].TrxType != batch.TrxType
            || details[0].TrxLineNo <= 0 || details[0].DocumentRevision < 0
            || string.IsNullOrWhiteSpace(details[0].ICode)) return null;
        var sourceDetail = details[0];
        var item = details[0].ICode!;
        if (!await FifoReversedIssueEvidenceMatchesAsync(db, company, branch, batch, [item],
                requireOlderLayers: false, requireTransaction, ct, allowVerifiedCorrections: true)) return null;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(db, company, branch, batch.TrxDtTime, ct) is not null)
            return null;
        if (batch.TrxDtTime.Date > (await StockBusinessTime.NowAsync(db, company, ct)).Date) return null;

        var issues = await db.IvTrxHistories.AsNoTracking().Where(h => h.CompanyCode == company
            && h.BranchCode == branch && h.ICode == item && h.TrxType == IvTrxTypes.IssueToProduction
            && h.EntryRole == "ORIGINAL" && h.TrxDtTime > batch.TrxDtTime
            && db.IvTrxHistories.Any(r => r.ReversesHistoryId == h.Id)).ToArrayAsync(ct);
        if (issues.Length != 1) return null;
        var issue = issues[0];
        var reversals = await db.IvTrxHistories.AsNoTracking().Where(h => h.ReversesHistoryId == issue.Id).ToArrayAsync(ct);
        if (reversals.Length != 1 || issue.StockPostingId is not > 0 || reversals[0].StockPostingId is not > 0)
            return null;
        var reverse = reversals[0];
        var ownerIds = new[] { issue.StockPostingId!.Value, reverse.StockPostingId!.Value };
        var owners = await db.StockPostings.AsNoTracking().Where(p => ownerIds.Contains(p.Id)).ToArrayAsync(ct);
        if (owners.Length != 2) return null;
        var issueOwner = owners.Single(p => p.Id == issue.StockPostingId);
        var reverseOwner = owners.Single(p => p.Id == reverse.StockPostingId);
        var issueKey = new StockReplayEventKey(issue.TrxDtTime, StockReplayEventKey.OrdinaryClass,
            issueOwner.PostingSequence, $"H{issue.Id:D10}", 0);
        var reverseKey = new StockReplayEventKey(reverse.TrxDtTime, StockReplayEventKey.OrdinaryClass,
            reverseOwner.PostingSequence, $"H{reverse.Id:D10}", 0);

        // Reversed events are absent from the active FIFO snapshot. Until the
        // writer can replay them too, refuse every other event in this interval.
        if (await db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.CompanyCode == company
                && h.BranchCode == branch && h.ICode == item && h.Id != issue.Id && h.Id != reverse.Id
                && h.TrxDtTime >= issue.TrxDtTime && h.TrxDtTime <= reverse.TrxDtTime, ct)
            || await db.StockValuationFacts.AsNoTracking().AnyAsync(f => f.CompanyCode == company
                && f.BranchCode == branch && f.ItemCode == item && f.EffectiveAt >= issue.TrxDtTime
                && f.EffectiveAt <= reverse.TrxDtTime
                && f.MovementCode != StockReplayFifoReversedIssueGenerationReader.ValueMovement
                && (!f.InventoryHistoryId.HasValue || f.InventoryHistoryId != issue.Id
                    && f.InventoryHistoryId != reverse.Id), ct)
            || await db.IvStockCountLines.AsNoTracking().AnyAsync(l => l.ICode == item
                && l.Header.CompanyCode == company && l.Header.BranchCode == branch
                && l.Header.CountDate >= issue.TrxDtTime.Date && l.Header.CountDate <= reverse.TrxDtTime.Date
                && (l.Header.Status == "POSTED" || l.Header.Status == "ROLLED_BACK"
                    || db.StockCountReplayGenerations.Any(g => g.CompanyCode == company && g.BranchCode == branch
                        && g.StockCountLineId == l.Id)), ct)) return null;

        var piles = await db.IvBalLocs.AsNoTracking().Where(b => b.CompanyCode == company
            && b.BranchCode == branch && b.ICode == item).ToArrayAsync(ct);
        if (piles.Length != 1 || issue.FromBalLocId != piles[0].Id) return null;
        var sourceCycles = await ReadFifoReceiptCyclesAsync(db, company, branch, batch, sourceDetail,
            piles[0].Id, issue, issueOwner.PostingSequence, reverseOwner.PostingSequence, removal, ct);
        if (sourceCycles is null) return null;
        var source = sourceCycles.Active;

        object? purchaseEvidence = null;
        if (batch.TrxType == IvTrxTypes.GoodsReceive)
        {
            var detail = details[0];
            if (string.IsNullOrWhiteSpace(detail.PoNo) || detail.PoRelNo is null || detail.PoLineNo is null
                || removal && (source!.PoNo != detail.PoNo || source.PoRelNo != detail.PoRelNo
                    || source.PoLineNo != detail.PoLineNo)) return null;
            var orders = await db.PoOrders.AsNoTracking().Where(p => p.CompanyCode == company && p.BranchCode == branch
                && p.PoNo == detail.PoNo && p.PoRelNo == detail.PoRelNo).ToArrayAsync(ct);
            var lines = await db.PoOrderDetails.AsNoTracking().Where(p => p.CompanyCode == company && p.BranchCode == branch
                && p.PoNo == detail.PoNo && p.PoRelNo == detail.PoRelNo && p.Line == detail.PoLineNo).ToArrayAsync(ct);
            if (orders.Length != 1 || lines.Length != 1 || lines[0].ICode != item || lines[0].InvoicedQty != 0m
                || removal && await db.PurchaseReceiptCostSettlements.AsNoTracking().AnyAsync(s =>
                    s.CompanyCode == company && s.BranchCode == branch && s.ReceiptInventoryHistoryId == source!.Id
                    && s.ReversesSettlementId == null && !db.PurchaseReceiptCostSettlements.Any(r =>
                        r.ReversesSettlementId == s.Id), ct)) return null;
            var order = orders[0];
            var line = lines[0];
            purchaseEvidence = new
            {
                order.PoNo, order.PoRelNo, order.PoDate, order.Status, order.VendCode, orderCurrency = order.CurCode, order.RowVersion,
                line.Line, line.ICode, line.PoUnitPrice, line.PoQty, line.PoPurQty, line.RecvQty, line.ReturnQty,
                line.OverRecvQty, line.InvoicedQty, line.PackSz, line.StdUom, line.PurchaseUom, lineCurrency = line.CurCode, line.NetAmount
            };
        }

        var facts = await db.StockValuationFacts.AsNoTracking().Where(f => f.InventoryHistoryId == issue.Id
            || f.InventoryHistoryId == reverse.Id).ToArrayAsync(ct);
        var originalFactIds = facts.Where(f => f.InventoryHistoryId == issue.Id).Select(f => f.Id).ToArray();
        var allFactIds = facts.Select(f => f.Id).ToArray();
        var rows = await db.StockFifoLayerConsumptions.AsNoTracking()
            .Where(c => allFactIds.Contains(c.IssueValuationFactId)).ToArrayAsync(ct);
        var previous = rows.Where(c => originalFactIds.Contains(c.IssueValuationFactId) && c.ReversesConsumptionId == null)
            .Select(c => new StockReplayFifoConsumptionPosition(c.Id, c.IssueValuationFactId,
            c.FifoLayerId, c.ConsumedQty, c.ConsumedValue, c.SplitOrdinal, true, issueKey)).ToArray();
        var movements = await db.ProductionBalLotMovements.AsNoTracking().Where(m =>
            m.InventoryHistoryId == issue.Id && m.MovementType == ProductionBalLotMovementTypes.Issue).ToArrayAsync(ct);
        if (movements.Length != 1) return null;
        var movement = movements[0];
        var reverseMovements = await db.ProductionBalLotMovements.AsNoTracking().Where(m =>
            m.OriginalMovementId == movement.Uid && m.MovementType == ProductionBalLotMovementTypes.IssueReversal).ToArrayAsync(ct);
        if (reverseMovements.Length != 1) return null;
        var reverseMovement = reverseMovements[0];
        var materials = await db.ProductionMaterialMovements.AsNoTracking().Where(m =>
            m.InventoryHistoryId == issue.Id && m.MovementType == ProductionMaterialMovementTypes.Issue).ToArrayAsync(ct);
        if (materials.Length != 1) return null;
        var material = materials[0];
        var materialReversals = await db.ProductionMaterialMovements.AsNoTracking().Where(m =>
            m.OriginalMovementId == material.Uid && m.MovementType == ProductionMaterialMovementTypes.IssueReversal)
            .ToArrayAsync(ct);
        if (materialReversals.Length != 1) return null;
        var materialReverse = materialReversals[0];
        var pools = await db.ProductionBalLots.AsNoTracking().Where(p => p.Uid == movement.ProductionBalLotId).ToArrayAsync(ct);
        var poolStates = await db.ProductionPoolValuationRows.AsNoTracking()
            .Where(p => p.ProductionBalLotId == movement.ProductionBalLotId).ToArrayAsync(ct);
        if (pools.Length != 1 || poolStates.Length != 1) return null;
        var pool = pools[0];
        var poolState = poolStates[0];
        if (pool.CompanyCode != company || pool.BranchCode != branch || pool.Kind != ProductionBalLotKinds.MaterialIn
            || pool.ItemCode != item || pool.OriginalIssueMovementId != material.Uid || pool.SourceIvBalLocId != piles[0].Id
            || pool.Qty != 0m || pool.BaseQty != 0m || pool.TotalCost != 0m || pool.AverageUnitCost != 0m
            || poolState.TrackedBaseQty != 0m || poolState.TrackedValue != 0m) return null;

        try
        {
            var correction = await StockReplayFifoReversedIssueGenerationReader.ReadAsync(db, company, branch,
                issue, reverse, movement, reverseMovement, previous, ct);
            if (correction is null && sourceCycles.LaterRootIds.Count != 0
                || correction is not null && !correction.RootPostingIds.Order().SequenceEqual(sourceCycles.LaterRootIds.Order()))
                return null;
            StockReplayFifoReversedIssueProjection projection;
            long epoch, watermark;
            decimal receiptQuantity, receiptValue, finalQuantity, finalValue;
            string physicalHash, financialHash;
            if (!removal)
            {
                var authority = batch.TrxType == IvTrxTypes.GoodsReceive
                    ? await StockReplayGrReceiptAuthorityReader.ReadAsync(db, company, branch, batchNo, piles[0].Id, ct)
                    : await StockReplayFifoMrReceiptAuthorityReader.ReadAsync(db, company, branch, batchNo, piles[0].Id, ct,
                        allowExistingLot: true);
                var physical = await StockReplayInventoryCandidatePlanner.BuildSingleAsync(db, company, branch,
                    authority.BalLocId, authority.EffectiveAt, authority.Quantity, ct);
                var financial = await StockReplayFifoInsertionPlanner.BuildMiscReceiptAsync(db, company, branch,
                    batchNo, physical, ct, goodsReceipt: batch.TrxType == IvTrxTypes.GoodsReceive);
                if (financial.Projection.CandidateReceipt is null) return null;
                if (StockReplayFifoMiRootWriter.ChangedIssues(financial.Projection).Count != 0) return null;
                projection = correction is null
                    ? StockReplayFifoReversedIssueProjector.InsertReceipt(financial.Snapshot,
                        issueKey, reverseKey, previous, financial.Projection.CandidateReceipt)
                    : StockReplayFifoReversedIssueProjector.InsertReceipt(financial.Snapshot,
                        issueKey, reverseKey, correction.Splits, financial.Projection.CandidateReceipt);
                epoch = physical.LedgerEpochId; watermark = physical.BranchWatermark;
                receiptQuantity = authority.Quantity; receiptValue = authority.Value;
                finalQuantity = financial.Projection.FinalQuantity; finalValue = financial.Projection.FinalValue;
                physicalHash = physical.PlanHash; financialHash = financial.PlanHash;
            }
            else
            {
                var owner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == source!.StockPostingId, ct);
                if (owner is null || owner.CompanyCode != company || owner.BranchCode != branch
                    || owner.SealedAtUtc is null || owner.PostingRole != "PRIMARY" || owner.SourceModule != "INVENTORY"
                    || owner.SourceDocumentType != batch.TrxType
                    || owner.SourceDocumentId != batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    || owner.SourceDocumentNo != batchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    || owner.DocumentRevision != source.DocumentRevision || owner.EffectiveAt != source.TrxDtTime
                    || owner.LedgerEpochId != issue.LedgerEpochId) return null;
                var sourceKey = new StockReplayEventKey(source.TrxDtTime, StockReplayEventKey.OrdinaryClass,
                    owner.PostingSequence, $"H{source.Id:D10}", 0);
                var physical = await StockReplayPhysicalPlanner.BuildRemovalAsync(db, company, branch, piles[0].Id,
                    new StockReplayPhysicalEvent(source.Id, sourceKey, source.ToStdQty!.Value), ct);
                var financial = await StockReplayFifoRemovalPlanner.BuildAsync(db, company, branch, item, physical, ct);
                var sourceLayer = financial.Projection.CandidateReceipt;
                if (sourceLayer is null) return null;
                if (StockReplayFifoMiRootWriter.ChangedIssues(financial.Projection).Count != 0) return null;
                projection = correction is null
                    ? StockReplayFifoReversedIssueProjector.RemoveReceipt(financial.Snapshot,
                        issueKey, reverseKey, previous, sourceLayer.LayerId)
                    : StockReplayFifoReversedIssueProjector.RemoveReceipt(financial.Snapshot,
                        issueKey, reverseKey, correction.Splits, sourceLayer.LayerId);
                epoch = physical.LedgerEpochId; watermark = physical.BranchWatermark;
                receiptQuantity = sourceLayer.OriginalQuantity; receiptValue = sourceLayer.OriginalValue;
                finalQuantity = financial.Projection.FinalQuantity; finalValue = financial.Projection.FinalValue;
                physicalHash = physical.PlanHash; financialHash = financial.PlanHash;
            }
            if (epoch != issue.LedgerEpochId
                || projection.OriginalValue != (correction?.Generation.TotalCost ?? movement.TotalCost)
                || correction is null && projection.OriginalSplits.Select(s => (s.LayerId, s.Quantity, s.Value))
                    .SequenceEqual(projection.ProjectedSplits.Select(s => (s.LayerId, s.Quantity, s.Value)))) return null;
            var sourceEvidenceHash = await ReadFifoReceiptSourceHashAsync(db, company, branch, batchNo, ct);
            if (sourceEvidenceHash is null) return null;
            var command = await InventoryStockPostingCommandFactory.CreateAsync(
                db, company, branch, batchNo, removal, batch.TrxDtTime, ct);
            if (command.DocumentRevision != sourceDetail.DocumentRevision || command.SourceDocumentType != batch.TrxType
                || command.ReversesPostingId != (removal ? source!.StockPostingId : null)) return null;
            var hash = StockPostingFingerprint.Create(new
            {
                company, branch, batchId = batch.Id, batchNo, batch.TrxType, batch.BatchStatus, removal,
                receiptDetail = new { sourceDetail.Id, sourceDetail.BatchId, sourceDetail.DocumentRevision,
                    sourceDetail.TrxLineNo, sourceDetail.ICode, sourceDetail.ToWarehouse, sourceDetail.ToLocation,
                    sourceDetail.ToLotNo, sourceDetail.ToStdQty, sourceDetail.ToStdUom, sourceDetail.ToPurQty,
                    sourceDetail.ToPurUom, sourceDetail.IStatus, sourceDetail.ExpiryDate, sourceDetail.Reason,
                    sourceDetail.UnitPrice, sourceDetail.BaseUnitPrices, sourceDetail.CostPrice, sourceDetail.Currency,
                    sourceDetail.PoNo, sourceDetail.PoRelNo, sourceDetail.PoLineNo, sourceDetail.PriceEvidence,
                    sourceDetail.CostEvidenceType, sourceDetail.CostOverrideReason, sourceDetail.CostApprovedBy,
                    sourceDetail.CostApprovedAtUtc },
                purchaseEvidence, sourceEvidenceHash, sourceCycles.EvidenceHash,
                correction = correction is null ? null : new { correction.Generation.Id, correction.Generation.SplitHash,
                    correction.Generation.TotalCost, correction.ActiveIssueRestatementId, correction.ActiveReversalRestatementId,
                    correction.RootPostingIds, correction.ValueFactIds },
                command.RequestId, command.Evidence.RequestFingerprint,
                command.Evidence.SourceSnapshotHash,
                issueId = issue.Id, reverseId = reverse.Id, issueOwnerId = issueOwner.Id, reverseOwnerId = reverseOwner.Id,
                issueHistory = new { issue.TrxDtTime, issue.TrxLineNo, issue.DocumentRevision, issue.FrStdQty,
                    issue.FrStdUom, issue.FromBalLocId, issue.ToBalLocId, issue.LedgerEpochId, issue.StockPostingId },
                reverseHistory = new { reverse.TrxDtTime, reverse.TrxLineNo, reverse.DocumentRevision, reverse.ToStdQty,
                    reverse.ToStdUom, reverse.FromBalLocId, reverse.ToBalLocId, reverse.LedgerEpochId,
                    reverse.StockPostingId, reverse.ReversesHistoryId },
                originalOwner = new { issueOwner.PostingSequence, issueOwner.SourceModule, issueOwner.SourceDocumentType,
                    issueOwner.SourceDocumentId, issueOwner.SourceDocumentNo, issueOwner.DocumentRevision,
                    issueOwner.LedgerEpochId, issueOwner.EffectiveAt, issueOwner.SealedAtUtc },
                reversalOwner = new { reverseOwner.PostingSequence, reverseOwner.SourceModule, reverseOwner.SourceDocumentType,
                    reverseOwner.SourceDocumentId, reverseOwner.SourceDocumentNo, reverseOwner.DocumentRevision,
                    reverseOwner.LedgerEpochId, reverseOwner.EffectiveAt, reverseOwner.SealedAtUtc, reverseOwner.ReversesPostingId },
                originalMovement = new { movement.Uid, movement.ProductionBalLotId, movement.WorkOrderId,
                    movement.WorkOrderMaterialId, movement.WorkOrderOperationId, movement.PostingLinkId,
                    movement.StockPostingId, movement.InventoryHistoryId, movement.LedgerEpochId,
                    movement.MovementDate, movement.BaseQty, movement.BaseUom, movement.TotalCost, movement.UnitCost },
                reversalMovement = new { reverseMovement.Uid, reverseMovement.OriginalMovementId,
                    reverseMovement.ProductionBalLotId, reverseMovement.WorkOrderId, reverseMovement.WorkOrderMaterialId,
                    reverseMovement.WorkOrderOperationId, reverseMovement.PostingLinkId, reverseMovement.StockPostingId,
                    reverseMovement.InventoryHistoryId, reverseMovement.LedgerEpochId, reverseMovement.MovementDate,
                    reverseMovement.BaseQty, reverseMovement.BaseUom, reverseMovement.TotalCost, reverseMovement.UnitCost },
                material = new { material.Uid, material.ProductionBalLotId, material.ProductionBalLotMovementId,
                    material.WorkOrderId, material.WorkOrderMaterialId, material.WorkOrderOperationId,
                    material.PostingLinkId, material.StockPostingId, material.InventoryHistoryId,
                    material.BaseQty, material.BaseUom, material.TotalCost, material.UnitCost },
                materialReverse = new { materialReverse.Uid, materialReverse.OriginalMovementId,
                    materialReverse.ProductionBalLotId, materialReverse.ProductionBalLotMovementId,
                    materialReverse.WorkOrderId, materialReverse.WorkOrderMaterialId, materialReverse.WorkOrderOperationId,
                    materialReverse.PostingLinkId, materialReverse.StockPostingId, materialReverse.InventoryHistoryId,
                    materialReverse.BaseQty, materialReverse.BaseUom, materialReverse.TotalCost, materialReverse.UnitCost },
                pool = new { pool.Uid, pool.Kind, pool.ItemCode, pool.OriginalIssueMovementId, pool.SourceIvBalLocId,
                    pool.Qty, pool.BaseQty, pool.TotalCost, pool.AverageUnitCost, pool.RowVersion },
                poolState = new { poolState.ProductionBalLotId, poolState.Generation, poolState.Status,
                    poolState.TrackedBaseQty, poolState.TrackedValue },
                facts = facts.OrderBy(f => f.Id).Select(f => new { f.Id, f.StockPostingId, f.InventoryHistoryId,
                    f.SplitOrdinal, f.BaseQty, f.CostAmount, f.UnitCost, f.OriginalValuationFactId,
                    f.ReversesValuationFactId, f.EffectiveAt, f.ItemCode, f.BaseUom, f.LedgerEpochId,
                    f.CostMethod, f.MovementCode, f.ValuationStatus, f.ProductionMovementId,
                    f.WorkOrderId, f.WorkOrderOperationId, f.ProductionPostingLinkId }).ToArray(),
                consumptions = rows.OrderBy(c => c.Id).Select(c => new { c.Id, c.IssueValuationFactId,
                    c.FifoLayerId, c.ConsumedQty, c.ConsumedValue, c.SplitOrdinal, c.ReversesConsumptionId }).ToArray(),
                balanceId = piles[0].Id, epoch, watermark, receiptQuantity, receiptValue, finalQuantity, finalValue,
                projection, physicalHash, financialHash
            }, new { }).RequestFingerprint;
            return new(company, branch, batch.Id, batchNo, batch.TrxType, sourceDetail.DocumentRevision,
                batch.TrxDtTime, removal ? source!.StockPostingId : null, removal,
                command.RequestId, command.Evidence.RequestFingerprint, command.Evidence.SourceSnapshotHash,
                sourceEvidenceHash, issue.Id, reverse.Id,
                movement.Uid, reverseMovement.Uid, piles[0].Id, epoch, watermark, receiptQuantity,
                receiptValue, projection, physicalHash, financialHash, hash, item, finalQuantity, finalValue,
                correction?.Generation.Id, correction?.ActiveIssueRestatementId, correction?.ActiveReversalRestatementId,
                correction?.ValueFactIds ?? []);
        }
        catch (StockLedgerException)
        {
            return null;
        }
    }

    private static async Task<bool> HasFifoReversedIssueEvidenceStoreAsync(AppDbContext db, CancellationToken ct)
    {
        var sql = db.Database.IsSqlServer()
            ? "SELECT CASE WHEN OBJECT_ID(N'dbo.StockFifoReversedIssueGeneration', N'U') IS NOT NULL "
                + "AND OBJECT_ID(N'dbo.StockFifoReversedIssueSplit', N'U') IS NOT NULL "
                + "AND (SELECT COUNT(*) FROM sys.triggers WHERE is_disabled = 0 AND "
                + "((parent_id = OBJECT_ID(N'dbo.StockFifoReversedIssueGeneration') AND name IN "
                + "(N'TR_FifoReversedIssueGeneration_Insert', N'TR_FifoReversedIssueGeneration_Immutable')) OR "
                + "(parent_id = OBJECT_ID(N'dbo.StockFifoReversedIssueSplit') AND name IN "
                + "(N'TR_FifoReversedIssueSplit_Insert', N'TR_FifoReversedIssueSplit_Immutable')))) = 4 THEN 1 ELSE 0 END"
            : db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
                ? "SELECT CASE WHEN (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN "
                    + "('StockFifoReversedIssueGeneration', 'StockFifoReversedIssueSplit')) = 2 THEN 1 ELSE 0 END"
                : null;
        if (sql is null) return false;
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        finally
        {
            if (opened) await db.Database.CloseConnectionAsync();
        }
    }
}
