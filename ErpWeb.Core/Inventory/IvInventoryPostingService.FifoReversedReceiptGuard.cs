using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    // A reversed issue has no live FIFO consumption, but its historical splits
    // still depend on the layers it originally consumed. Changing an earlier
    // receipt is safe only if every split came from a verified, earlier FIFO
    // layer and the bounded intervening history is unchanged. Equal-time
    // ordinary receipts are ordered by their sealed posting sequence.
    // The caller holds the branch lock.
    private static async Task<bool> FifoReceiptChangePreservesReversedIssuesAsync(
        AppDbContext db, string company, string branch, IvTrxBatch batch,
        string[] itemCodes, CancellationToken ct) =>
        await FifoReversedIssueEvidenceMatchesAsync(db, company, branch, batch, itemCodes,
            requireOlderLayers: true, requireTransaction: true, ct);

    // A changed-layer projection may inspect the same sealed evidence before a
    // lock, but it is never a permit to post. Public guards always require the
    // older-layer check above; correction writers must have a separate proof.
    private static async Task<bool> FifoReversedIssueEvidenceMatchesAsync(
        AppDbContext db, string company, string branch, IvTrxBatch batch,
        string[] itemCodes, bool requireOlderLayers, bool requireTransaction, CancellationToken ct,
        bool allowVerifiedCorrections = false)
    {
        if (requireTransaction && db.Database.CurrentTransaction is null) return false;
        var states = await db.StockCostStates.AsNoTracking().Where(s =>
            s.CompanyCode == company && s.BranchCode == branch && itemCodes.Contains(s.ItemCode))
            .ToArrayAsync(ct);
        var fifoItems = states.Where(s => s.CostMethod == StockCostMethods.Fifo).Select(s => s.ItemCode).ToArray();
        if (fifoItems.Length == 0) return true;
        var issues = await db.IvTrxHistories.AsNoTracking().Where(h =>
            h.CompanyCode == company && h.BranchCode == branch && fifoItems.Contains(h.ICode)
            && h.TrxType == IvTrxTypes.IssueToProduction && h.TrxDtTime >= batch.TrxDtTime
            && db.IvTrxHistories.Any(r => r.ReversesHistoryId == h.Id)).ToArrayAsync(ct);
        if (issues.Length == 0) return true;
        if (itemCodes.Length != 1 || fifoItems.Length != 1 || issues.Length != 1) return false;
        var issue = issues[0];
        if (issue.LedgerVersion != 2 || issue.EntryRole != "ORIGINAL"
            || issue.BatchStatus != IvBatchStatuses.Posted || issue.StockPostingId is not > 0
            || issue.LedgerEpochId is not > 0 || issue.FrStdQty is not > 0m
            || issue.ReversesHistoryId is not null) return false;
        var historyReversals = await db.IvTrxHistories.AsNoTracking().Where(h =>
            h.ReversesHistoryId == issue.Id).ToArrayAsync(ct);
        if (historyReversals.Length != 1) return false;
        var historyReverse = historyReversals[0];
        if (historyReverse.CompanyCode != company || historyReverse.BranchCode != branch
            || historyReverse.EntryRole != "REVERSAL" || historyReverse.LedgerVersion != 2
            || historyReverse.BatchStatus != IvBatchStatuses.Posted
            || historyReverse.LedgerEpochId != issue.LedgerEpochId || historyReverse.StockPostingId is not > 0
            || historyReverse.TrxType != issue.TrxType || historyReverse.BatchNo != issue.BatchNo
            || historyReverse.ICode != issue.ICode || historyReverse.ToStdQty != issue.FrStdQty
            || historyReverse.TrxLineNo != issue.TrxLineNo || historyReverse.DocumentRevision != issue.DocumentRevision
            || historyReverse.FromBalLocId != issue.ToBalLocId || historyReverse.ToBalLocId != issue.FromBalLocId
            || historyReverse.FrStdQty != issue.ToStdQty || historyReverse.ToStdUom != issue.FrStdUom
            || historyReverse.TrxDtTime < issue.TrxDtTime)
            return false;
        var issueBatch = await db.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == company && b.BranchCode == branch && b.BatchNo == issue.BatchNo, ct);
        var issueOwnerIds = new[] { issue.StockPostingId!.Value, historyReverse.StockPostingId!.Value };
        var issueOwners = await db.StockPostings.AsNoTracking().Where(p => issueOwnerIds.Contains(p.Id))
            .ToArrayAsync(ct);
        if (issueBatch is null || issueBatch.TrxType != IvTrxTypes.IssueToProduction || issueOwners.Length != 2)
            return false;
        var issueOwner = issueOwners.Single(p => p.Id == issue.StockPostingId);
        var reverseOwner = issueOwners.Single(p => p.Id == historyReverse.StockPostingId);
        if (issueOwner.CompanyCode != company || issueOwner.BranchCode != branch
            || issueOwner.SealedAtUtc is null || issueOwner.PostingRole != "PRIMARY"
            || issueOwner.SourceModule != "INVENTORY" || issueOwner.SourceDocumentType != issue.TrxType
            || issueOwner.SourceDocumentId != issueBatch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || issueOwner.SourceDocumentNo != issue.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || issueOwner.DocumentRevision != issue.DocumentRevision || issueOwner.PostingSequence <= 0
            || issueOwner.LedgerEpochId != issue.LedgerEpochId || issueOwner.EffectiveAt != issue.TrxDtTime
            || reverseOwner.CompanyCode != company || reverseOwner.BranchCode != branch
            || reverseOwner.SealedAtUtc is null || reverseOwner.PostingRole != "REVERSAL"
            || reverseOwner.SourceModule != issueOwner.SourceModule
            || reverseOwner.SourceDocumentType != issueOwner.SourceDocumentType
            || reverseOwner.SourceDocumentId != issueOwner.SourceDocumentId
            || reverseOwner.SourceDocumentNo != issueOwner.SourceDocumentNo
            || reverseOwner.DocumentRevision != issueOwner.DocumentRevision
            || reverseOwner.LedgerEpochId != issueOwner.LedgerEpochId
            || reverseOwner.EffectiveAt != historyReverse.TrxDtTime
            || reverseOwner.ReversesPostingId != issueOwner.Id
            || reverseOwner.PostingSequence <= issueOwner.PostingSequence)
            return false;
        if (await IvPeriodCloseGuard.EnsureOpenAsync(db, company, branch, issue.TrxDtTime, ct) is not null
            || await IvPeriodCloseGuard.EnsureOpenAsync(db, company, branch, historyReverse.TrxDtTime, ct) is not null)
            return false;
        var through = historyReverse.TrxDtTime.Date > issue.TrxDtTime.Date
            ? historyReverse.TrxDtTime.Date : issue.TrxDtTime.Date;
        var policies = await db.StockCostPolicyRevisions.AsNoTracking().Where(p =>
            p.CompanyCode == company && p.BranchCode == branch && p.Status == StockCostPolicyStatuses.Active
            && p.EffectiveFrom <= through && (p.EffectiveTo == null || p.EffectiveTo > batch.TrxDtTime.Date))
            .ToArrayAsync(ct);
        if (policies.Length != 1 || policies[0].CostMethod != StockCostMethods.Fifo
            || policies[0].EffectiveFrom > batch.TrxDtTime.Date
            || policies[0].EffectiveTo is DateTime policyEnd && policyEnd <= through)
            return false;
        if (await db.IvStockCountLines.AsNoTracking().AnyAsync(l =>
                l.ICode == issue.ICode && l.Header.CompanyCode == company && l.Header.BranchCode == branch
                && l.Header.CountDate >= batch.TrxDtTime.Date && l.Header.CountDate <= issue.TrxDtTime.Date
                && (l.Header.Status == "POSTED" || l.Header.Status == "ROLLED_BACK"
                    || db.StockCountReplayGenerations.Any(g => g.CompanyCode == company && g.BranchCode == branch
                        && g.StockCountLineId == l.Id)), ct))
            return false;
        var histories = new[] { issue.Id, historyReverse.Id };
        var facts = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.InventoryHistoryId.HasValue && histories.Contains(f.InventoryHistoryId.Value)).ToArrayAsync(ct);
        var originals = facts.Where(f => f.InventoryHistoryId == issue.Id).ToArray();
        var reversals = facts.Where(f => f.InventoryHistoryId == historyReverse.Id).ToArray();
        // The FIFO writer stores the exact allocated value first, then rounds
        // its unit cost. Multiplying that rounded unit cost back by the split
        // quantity can lose a final decimal even for an intact allocation.
        if (originals.Length == 0 || reversals.Length != originals.Length
            || originals.Sum(f => f.BaseQty) != issue.FrStdQty
            || originals.Any(f => f.CompanyCode != company || f.BranchCode != branch
                || f.StockPostingId != issue.StockPostingId || f.LedgerEpochId != issue.LedgerEpochId
                || f.ItemCode != issue.ICode || f.CostMethod != StockCostMethods.Fifo
                || f.MovementCode != "PRODUCTION_MATERIAL_OUT" || f.EffectiveAt != issue.TrxDtTime
                || f.Direction != -1 || f.ValuationStatus != StockValuationStatuses.Valued
                || f.BaseQty <= 0m || f.CostAmount < 0m || f.BaseCostAmount != f.CostAmount
                || f.UnitCost != StockLedgerPrecision.Money(f.CostAmount / f.BaseQty)
                || f.OriginalValuationFactId is not null || f.ReversesValuationFactId is not null))
            return false;
        var materials = await db.ProductionMaterialMovements.AsNoTracking().Where(m =>
            m.InventoryHistoryId == issue.Id && m.MovementType == ProductionMaterialMovementTypes.Issue)
            .ToArrayAsync(ct);
        if (materials.Length != 1 || materials[0].ProductionBalLotMovementId is not long movementId)
            return false;
        var material = materials[0];
        var movements = await db.ProductionBalLotMovements.AsNoTracking().Where(m =>
            m.Uid == movementId || m.OriginalMovementId == movementId).ToArrayAsync(ct);
        var movement = movements.SingleOrDefault(m => m.Uid == movementId);
        var movementReversals = movements.Where(m => m.OriginalMovementId == movementId).ToArray();
        var materialReversals = await db.ProductionMaterialMovements.AsNoTracking().Where(m =>
            m.OriginalMovementId == material.Uid && m.MovementType == ProductionMaterialMovementTypes.IssueReversal)
            .ToArrayAsync(ct);
        if (movement is null || movementReversals.Length != 1 || materialReversals.Length != 1
            || material.CompanyCode != company || material.BranchCode != branch || material.ItemCode != issue.ICode
            || material.StockPostingId != issue.StockPostingId || material.BaseQty != issue.FrStdQty
            || material.ProductionBalLotId != movement.ProductionBalLotId
            || material.TotalCost != originals.Sum(f => f.CostAmount)
            || movement.CompanyCode != company || movement.BranchCode != branch || movement.ItemCode != issue.ICode
            || movement.MovementType != ProductionBalLotMovementTypes.Issue || movement.OriginalMovementId is not null
            || movement.StockPostingId != issue.StockPostingId || movement.InventoryHistoryId != issue.Id
            || movement.LedgerVersion != 2 || movement.LedgerEpochId != issue.LedgerEpochId
            || movement.ValuationStatus != ProductionPoolValuationService.Verified
            || movement.PostingLinkId != material.PostingLinkId || movement.WorkOrderId != material.WorkOrderId
            || movement.WorkOrderMaterialId != material.WorkOrderMaterialId
            || movement.WorkOrderOperationId != material.WorkOrderOperationId
            || movement.BaseQty != material.BaseQty || movement.TotalCost != material.TotalCost
            || movement.UnitCost != material.UnitCost
            || originals.Any(f => f.ProductionMovementId != material.Uid || f.WorkOrderId != material.WorkOrderId
                || f.WorkOrderOperationId != material.WorkOrderOperationId || f.ProductionPostingLinkId != material.PostingLinkId)
            || movementReversals[0].MovementType != ProductionBalLotMovementTypes.IssueReversal
            || movementReversals[0].StockPostingId != historyReverse.StockPostingId
            || movementReversals[0].CompanyCode != company || movementReversals[0].BranchCode != branch
            || movementReversals[0].ItemCode != movement.ItemCode || movementReversals[0].LedgerVersion != 2
            || movementReversals[0].LedgerEpochId != movement.LedgerEpochId
            || movementReversals[0].ValuationStatus != ProductionPoolValuationService.Verified
            || movementReversals[0].ProductionBalLotId != movement.ProductionBalLotId
            || movementReversals[0].WorkOrderId != movement.WorkOrderId
            || movementReversals[0].WorkOrderMaterialId != movement.WorkOrderMaterialId
            || movementReversals[0].WorkOrderOperationId != movement.WorkOrderOperationId
            || movementReversals[0].BaseQty != movement.BaseQty || movementReversals[0].TotalCost != movement.TotalCost
            || movementReversals[0].UnitCost != movement.UnitCost
            || materialReversals[0].CompanyCode != company || materialReversals[0].BranchCode != branch
            || materialReversals[0].ItemCode != material.ItemCode
            || materialReversals[0].StockPostingId != historyReverse.StockPostingId
            || (materialReversals[0].ProductionBalLotMovementId is long reverseMovementId
                && reverseMovementId != movementReversals[0].Uid)
            || materialReversals[0].PostingLinkId != movementReversals[0].PostingLinkId
            || materialReversals[0].WorkOrderId != material.WorkOrderId
            || materialReversals[0].WorkOrderMaterialId != material.WorkOrderMaterialId
            || materialReversals[0].WorkOrderOperationId != material.WorkOrderOperationId
            || materialReversals[0].BaseQty != material.BaseQty || materialReversals[0].TotalCost != material.TotalCost
            || materialReversals[0].UnitCost != material.UnitCost)
            return false;
        if (await db.ProductionBalLotMovements.AsNoTracking().CountAsync(m =>
                m.ProductionBalLotId == movement.ProductionBalLotId, ct) != 2
            || await db.ProductionMaterialMovements.AsNoTracking().CountAsync(m =>
                m.OriginalMovementId == material.Uid, ct) != 1
            || !allowVerifiedCorrections && await db.ProductionCostRestatementFacts.AsNoTracking().AnyAsync(f =>
                f.ProductionBalLotMovementId == movement.Uid
                || f.ProductionBalLotMovementId == movementReversals[0].Uid, ct)
            || await db.ProductionPoolDependencyRows.AsNoTracking().AnyAsync(e =>
                e.ContributorMovementId == movement.Uid || e.ConsumerMovementId == movement.Uid
                || e.ContributorMovementId == movementReversals[0].Uid
                || e.ConsumerMovementId == movementReversals[0].Uid, ct))
            return false;
        var originalIds = originals.Select(f => f.Id).ToArray();
        if (!allowVerifiedCorrections && await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.OriginalValuationFactId.HasValue && originalIds.Contains(f.OriginalValuationFactId.Value)
                && f.InventoryHistoryId != historyReverse.Id, ct)) return false;
        foreach (var original in originals)
        {
            var matching = reversals.Where(f => f.ReversesValuationFactId == original.Id).ToArray();
            if (matching.Length != 1 || matching[0].CompanyCode != company || matching[0].BranchCode != branch
                || matching[0].StockPostingId != historyReverse.StockPostingId
                || matching[0].LedgerEpochId != original.LedgerEpochId || matching[0].ItemCode != issue.ICode
                || matching[0].Direction != 1 || matching[0].CostMethod != StockCostMethods.Fifo
                || matching[0].ValuationStatus != StockValuationStatuses.Reversed
                || matching[0].BaseQty != original.BaseQty || matching[0].CostAmount != original.CostAmount
                || matching[0].BaseCostAmount != original.BaseCostAmount || matching[0].BaseUom != original.BaseUom
                || matching[0].UnitCost != original.UnitCost || matching[0].SplitOrdinal != original.SplitOrdinal
                || matching[0].OriginalValuationFactId != original.Id
                || matching[0].EffectiveAt != historyReverse.TrxDtTime)
                return false;
        }
        var allFactIds = facts.Select(f => f.Id).ToArray();
        var receiptHistories = await db.IvTrxHistories.AsNoTracking().Where(h =>
            h.CompanyCode == company && h.BranchCode == branch && h.BatchNo == batch.BatchNo
            && h.TrxType == batch.TrxType).Select(h => h.Id).ToArrayAsync(ct);
        var otherHistories = await db.IvTrxHistories.AsNoTracking().Where(h =>
            h.CompanyCode == company && h.BranchCode == branch && h.ICode == issue.ICode
            && h.TrxDtTime >= batch.TrxDtTime && h.TrxDtTime <= issue.TrxDtTime
            && h.Id != issue.Id && !receiptHistories.Contains(h.Id)).ToArrayAsync(ct);
        if (otherHistories.Any(h => h.LedgerVersion != 2 || h.EntryRole != "ORIGINAL"
                || h.BatchStatus != IvBatchStatuses.Posted || h.ReversesHistoryId is not null
                || h.TrxType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
                || h.FromBalLocId is not null || h.ToBalLocId is not > 0 || h.ToStdQty is not > 0m
                || h.StockPostingId is not > 0 || h.LedgerEpochId != issue.LedgerEpochId))
            return false;
        var otherHistoryIds = otherHistories.Select(h => h.Id).ToArray();
        if (await db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.ReversesHistoryId.HasValue
                && otherHistoryIds.Contains(h.ReversesHistoryId.Value), ct))
            return false;
        var correctionValueIds = allowVerifiedCorrections
            ? await db.StockValuationFacts.AsNoTracking().Where(f => f.CompanyCode == company
                && f.BranchCode == branch && f.ItemCode == issue.ICode
                && f.MovementCode == StockReplayFifoReversedIssueGenerationReader.ValueMovement)
                .Select(f => f.Id).ToArrayAsync(ct)
            : [];
        var otherFacts = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.CompanyCode == company && f.BranchCode == branch && f.ItemCode == issue.ICode
            && f.EffectiveAt >= batch.TrxDtTime && f.EffectiveAt <= issue.TrxDtTime
            && !allFactIds.Contains(f.Id)
            && (!f.InventoryHistoryId.HasValue || !receiptHistories.Contains(f.InventoryHistoryId.Value)))
            .ToArrayAsync(ct);
        if (allowVerifiedCorrections)
            otherFacts = otherFacts.Where(f => !correctionValueIds.Contains(f.Id)).ToArray();
        if (otherFacts.Length != otherHistories.Length || otherHistories.Any(h => otherFacts.Count(f =>
                f.InventoryHistoryId == h.Id && f.StockPostingId == h.StockPostingId
                && f.LedgerEpochId == h.LedgerEpochId && f.EffectiveAt == h.TrxDtTime
                && f.CostMethod == StockCostMethods.Fifo && f.Direction == 1
                && f.ValuationStatus == StockValuationStatuses.Valued
                && f.OriginalValuationFactId is null && f.ReversesValuationFactId is null
                && f.BaseQty == h.ToStdQty && f.ToBalLocId == h.ToBalLocId) != 1))
            return false;
        var otherOwners = otherHistories.Select(h => h.StockPostingId!.Value).Distinct().ToArray();
        if (await db.StockPostings.AsNoTracking().CountAsync(p => otherOwners.Contains(p.Id)
                && p.CompanyCode == company && p.BranchCode == branch && p.SealedAtUtc != null
                && p.PostingRole == "PRIMARY" && p.LedgerEpochId == issue.LedgerEpochId, ct) != otherOwners.Length)
            return false;
        var consumption = await db.StockFifoLayerConsumptions.AsNoTracking()
            .Where(c => allFactIds.Contains(c.IssueValuationFactId)).ToArrayAsync(ct);
        var originalConsumption = consumption.Where(c => originalIds.Contains(c.IssueValuationFactId)).ToArray();
        if (originalConsumption.Length != originals.Length) return false;
        var originalConsumptionIds = originalConsumption.Select(c => c.Id).ToArray();
        if (await db.StockFifoLayerConsumptions.AsNoTracking().CountAsync(c =>
                c.ReversesConsumptionId.HasValue && originalConsumptionIds.Contains(c.ReversesConsumptionId.Value), ct)
            != originalConsumption.Length) return false;
        var layerIds = originalConsumption.Select(c => c.FifoLayerId).Distinct().ToArray();
        var layers = await db.StockFifoLayers.AsNoTracking().Where(l =>
            layerIds.Contains(l.Id)).ToArrayAsync(ct);
        var originIds = layers.Select(l => l.OriginValuationFactId).ToArray();
        var origins = await db.StockValuationFacts.AsNoTracking().Where(f => originIds.Contains(f.Id)).ToArrayAsync(ct);
        if (!allowVerifiedCorrections && await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.ReversesValuationFactId.HasValue && originIds.Contains(f.ReversesValuationFactId.Value), ct))
            return false;
        var ownerIds = origins.Select(f => f.StockPostingId).ToArray();
        var owners = await db.StockPostings.AsNoTracking().Where(p => ownerIds.Contains(p.Id)).ToArrayAsync(ct);
        long? sameTimeReceiptSequence = null;
        if (requireOlderLayers && layers.Any(l => l.ReceiptEffectiveAt == batch.TrxDtTime))
        {
            if (batch.BatchStatus == IvBatchStatuses.New)
            {
                var sequences = await db.StockPostingBranchSequences.AsNoTracking().Where(s =>
                    s.CompanyCode == company && s.BranchCode == branch).Select(s => s.LastSequence).ToArrayAsync(ct);
                if (sequences.Length != 1 || sequences[0] < reverseOwner.PostingSequence
                    || sequences[0] == long.MaxValue) return false;
                sameTimeReceiptSequence = sequences[0] + 1;
            }
            else if (batch.BatchStatus == IvBatchStatuses.Posted)
            {
                var activeSources = await db.IvTrxHistories.AsNoTracking().Where(h =>
                    h.CompanyCode == company && h.BranchCode == branch && h.BatchNo == batch.BatchNo
                    && h.TrxType == batch.TrxType && h.EntryRole == "ORIGINAL" && h.StockPostingId != null
                    && !db.IvTrxHistories.Any(r => r.ReversesHistoryId == h.Id)).ToArrayAsync(ct);
                if (activeSources.Length != 1) return false;
                var source = activeSources[0];
                var sourceOwner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p =>
                    p.Id == source.StockPostingId, ct);
                if (sourceOwner is null || sourceOwner.CompanyCode != company || sourceOwner.BranchCode != branch
                    || sourceOwner.SealedAtUtc is null || sourceOwner.PostingRole != "PRIMARY"
                    || sourceOwner.SourceModule != "INVENTORY" || sourceOwner.SourceDocumentType != batch.TrxType
                    || sourceOwner.SourceDocumentId != batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    || sourceOwner.SourceDocumentNo != batch.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    || sourceOwner.DocumentRevision != source.DocumentRevision || sourceOwner.EffectiveAt != batch.TrxDtTime
                    || source.TrxDtTime != batch.TrxDtTime || sourceOwner.LedgerEpochId != issue.LedgerEpochId
                    || sourceOwner.PostingSequence <= 0) return false;
                sameTimeReceiptSequence = sourceOwner.PostingSequence;
            }
            else return false;
        }
        foreach (var fact in originals)
        {
            var rows = originalConsumption.Where(c => c.IssueValuationFactId == fact.Id).ToArray();
            if (rows.Length != 1) return false;
            var row = rows[0];
            var layer = layers.SingleOrDefault(l => l.Id == row.FifoLayerId);
            var reverseFact = reversals.Single(f => f.ReversesValuationFactId == fact.Id);
            var rowReversals = consumption.Where(c => c.ReversesConsumptionId == row.Id).ToArray();
            if (layer is null || row.CompanyCode != company || row.BranchCode != branch
                || row.ReversesConsumptionId is not null || row.ConsumedQty != fact.BaseQty
                || row.ConsumedValue != fact.CostAmount || row.SplitOrdinal != fact.SplitOrdinal
                || layer.CompanyCode != company || layer.BranchCode != branch || layer.ItemCode != issue.ICode
                || layer.BaseUom != fact.BaseUom
                || requireOlderLayers && layer.ReceiptEffectiveAt > batch.TrxDtTime
                || layer.AccumulatedAdjustment != 0m || rowReversals.Length != 1
                || rowReversals[0].CompanyCode != company || rowReversals[0].BranchCode != branch
                || rowReversals[0].IssueValuationFactId != reverseFact.Id
                || rowReversals[0].FifoLayerId != row.FifoLayerId
                || rowReversals[0].ConsumedQty != row.ConsumedQty
                || rowReversals[0].ConsumedValue != row.ConsumedValue
                || rowReversals[0].SplitOrdinal != row.SplitOrdinal) return false;
            var origin = origins.SingleOrDefault(f => f.Id == layer.OriginValuationFactId);
            var owner = owners.SingleOrDefault(p => p.Id == layer.OriginStockPostingId);
            if (origin is null || owner is null || origin.CompanyCode != company || origin.BranchCode != branch
                || origin.ItemCode != issue.ICode || origin.StockPostingId != owner.Id
                || origin.EffectiveAt != layer.ReceiptEffectiveAt || origin.CostMethod != StockCostMethods.Fifo
                || origin.Direction != 1 || origin.ValuationStatus != StockValuationStatuses.Valued
                || origin.OriginalValuationFactId is not null || origin.ReversesValuationFactId is not null
                || origin.BaseQty != layer.OriginalQty || origin.CostAmount != layer.OriginalValue
                || owner.CompanyCode != company || owner.BranchCode != branch || owner.SealedAtUtc is null
                || owner.PostingRole != "PRIMARY" || owner.LedgerEpochId != origin.LedgerEpochId) return false;
            if (requireOlderLayers && layer.ReceiptEffectiveAt == batch.TrxDtTime)
            {
                // Count layers use a different end-of-day event class. For a
                // tie, admit only an independently verified ordinary MR/GR
                // whose posting precedes the receipt being changed.
                var earlier = otherHistories.SingleOrDefault(h => h.Id == origin.InventoryHistoryId);
                if (earlier is null || sameTimeReceiptSequence is not long receiptSequence
                    || owner.PostingSequence <= 0 || owner.PostingSequence >= receiptSequence
                    || earlier.StockPostingId != owner.Id || earlier.TrxDtTime != layer.ReceiptEffectiveAt
                    || owner.EffectiveAt != earlier.TrxDtTime || owner.SourceModule != "INVENTORY"
                    || owner.SourceDocumentType != earlier.TrxType
                    || owner.SourceDocumentNo != earlier.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    || owner.DocumentRevision != earlier.DocumentRevision) return false;
                var earlierBatch = await db.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
                    b.CompanyCode == company && b.BranchCode == branch && b.BatchNo == earlier.BatchNo
                    && b.TrxType == earlier.TrxType, ct);
                if (earlierBatch is null || owner.SourceDocumentId !=
                    earlierBatch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)) return false;
            }
        }
        if (allowVerifiedCorrections)
        {
            if (await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                    f.OriginalValuationFactId.HasValue && allFactIds.Contains(f.OriginalValuationFactId.Value)
                    && f.InventoryHistoryId != historyReverse.Id && !correctionValueIds.Contains(f.Id), ct))
                return false;
            var key = new StockReplayEventKey(issue.TrxDtTime, StockReplayEventKey.OrdinaryClass,
                issueOwner.PostingSequence, $"H{issue.Id:D10}", 0);
            var baseline = originalConsumption.Select(c => new StockReplayFifoConsumptionPosition(c.Id,
                c.IssueValuationFactId, c.FifoLayerId, c.ConsumedQty, c.ConsumedValue, c.SplitOrdinal, true, key)).ToArray();
            try
            {
                var correction = await StockReplayFifoReversedIssueGenerationReader.ReadAsync(db, company, branch,
                    issue, historyReverse, movement, movementReversals[0], baseline, ct);
                if (correction is null && (correctionValueIds.Length != 0
                        || await db.ProductionCostRestatementFacts.AsNoTracking().AnyAsync(f =>
                            f.ProductionBalLotMovementId == movement.Uid
                            || f.ProductionBalLotMovementId == movementReversals[0].Uid, ct)
                        || await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                            f.ReversesValuationFactId.HasValue && originIds.Contains(f.ReversesValuationFactId.Value), ct))
                    || correction is not null && !correction.ValueFactIds.Order().SequenceEqual(correctionValueIds.Order()))
                    return false;
            }
            catch (StockLedgerException)
            {
                return false;
            }
        }
        return true;
    }
}
