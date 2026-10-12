using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>Reconciles immutable execution facts before a Work Order is allowed to complete.</summary>
public sealed class ProductionMaterialReconciliationService : IProductionMaterialReconciliationService
{
    private const decimal Tolerance = 0.0001m;

    public async Task<ProductionMaterialReconciliationResult> ReconcileAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long workOrderId,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<ProductionMaterialReconciliationFinding>();
        var drafts = await (from link in db.ProductionPostingLinks.AsNoTracking()
                            join batch in db.IvTrxBatches.AsNoTracking()
                                on new { link.CompanyCode, link.BranchCode, BatchNo = link.InventoryBatchNo ?? -1 }
                                equals new { batch.CompanyCode, batch.BranchCode, batch.BatchNo }
                            where link.CompanyCode == companyCode && link.BranchCode == branchCode
                                && link.WorkOrderId == workOrderId
                                && link.CommandType == ProductionPostingCommandTypes.MaterialIssuePost
                                && link.Status == ProductionPostingLinkStatuses.Draft
                                && batch.BatchStatus == IvBatchStatuses.New
                                && batch.DeletedAtUtc == null
                            select batch.BatchNo).ToListAsync(cancellationToken);
        foreach (var batchNo in drafts.Distinct().OrderBy(x => x))
            findings.Add(new ProductionMaterialReconciliationFinding
            {
                Code = "OPEN_IP_DRAFT", InventoryBatchNo = batchNo,
                Message = $"Issue-to-Production batch {batchNo} is still a NEW draft."
            });

        var returnDrafts = await db.ProductionStockReturns.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.WorkOrderId == workOrderId
                && x.DeletedAtUtc == null && x.Batch.DeletedAtUtc == null && x.Batch.BatchStatus == IvBatchStatuses.New)
            .Select(x => x.Batch.BatchNo).ToListAsync(cancellationToken);
        var adjustmentDrafts = await db.ProductionStockAdjustments.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.WorkOrderId == workOrderId
                && x.DeletedAtUtc == null && x.Batch.DeletedAtUtc == null && x.Batch.BatchStatus == IvBatchStatuses.New)
            .Select(x => x.Batch.BatchNo).ToListAsync(cancellationToken);
        foreach (var batchNo in returnDrafts)
            findings.Add(new() { Code = "OPEN_PRTN_DRAFT", InventoryBatchNo = batchNo,
                Message = $"Production Stock Return batch {batchNo} is still a NEW draft." });
        foreach (var batchNo in adjustmentDrafts)
            findings.Add(new() { Code = "OPEN_PADJ_DRAFT", InventoryBatchNo = batchNo,
                Message = $"Production Stock Adjustment batch {batchNo} is still a NEW draft." });

        var pending = await db.ProductionPostingLinks.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.WorkOrderId == workOrderId
                && x.Status == ProductionPostingLinkStatuses.Pending)
            .Select(x => x.ProductionDocumentNo ?? x.InventoryBatchNo!.Value.ToString())
            .ToListAsync(cancellationToken);
        foreach (var document in pending)
            findings.Add(new ProductionMaterialReconciliationFinding
            {
                Code = "UNRESOLVED_POSTING", Message = $"Production posting {document} is unresolved."
            });

        var materials = await db.ProductionWorkOrderMaterials.AsNoTracking()
            .Where(x => x.WorkOrderId == workOrderId)
            .Select(x => new { x.Uid, x.ComponentCode, x.BaseUom }).ToListAsync(cancellationToken);
        var movements = await db.ProductionMaterialMovements.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.WorkOrderId == workOrderId)
            .Select(x => new { x.WorkOrderMaterialId, x.MovementType, x.BaseQty, x.InventoryBatchNo })
            .ToListAsync(cancellationToken);
        var execution = await ProductionMaterialExecutionFacts.ReadAsync(db, companyCode, branchCode,
            materials.Select(x => x.Uid).ToArray(), cancellationToken);
        var materialLots = await db.ProductionBalLots.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode
                && x.WorkOrderId == workOrderId && x.Kind == ProductionBalLotKinds.MaterialIn)
            .Select(x => new { x.WorkOrderMaterialId, x.BaseQty, x.ItemCode, x.OriginalIssueMovementId })
            .ToListAsync(cancellationToken);

        foreach (var material in materials)
        {
            var rows = movements.Where(x => x.WorkOrderMaterialId == material.Uid).ToList();
            var remaining = execution.GetValueOrDefault(material.Uid, ProductionMaterialExecutionTotals.Empty).RemainingProductionMaterialBaseQty;
            var lotRemaining = IvQty.Round(materialLots.Where(x => x.WorkOrderMaterialId == material.Uid).Sum(x => x.BaseQty));
            var latestBatch = rows.Where(x => x.InventoryBatchNo.HasValue).Select(x => x.InventoryBatchNo).LastOrDefault();

            if (remaining < -Tolerance)
                findings.Add(new ProductionMaterialReconciliationFinding
                {
                    Code = "NEGATIVE_MATERIAL_BALANCE", ItemCode = material.ComponentCode,
                    RemainingBaseQty = remaining,
                    Message = $"{material.ComponentCode} has an inconsistent negative production-material balance of {remaining:n4} {material.BaseUom}."
                });
            else if (remaining > Tolerance)
                findings.Add(new ProductionMaterialReconciliationFinding
                {
                    Code = "UNUSED_ISSUED_MATERIAL", ItemCode = material.ComponentCode,
                    InventoryBatchNo = latestBatch, RemainingBaseQty = remaining,
                    Message = $"{material.ComponentCode} has {remaining:n4} {material.BaseUom} remaining in production"
                        + (latestBatch is null ? "." : $" from IP batch {latestBatch}.")
                });

            if (Math.Abs(remaining - lotRemaining) > Tolerance)
                findings.Add(new ProductionMaterialReconciliationFinding
                {
                    Code = "MATERIAL_LOT_MISMATCH", ItemCode = material.ComponentCode,
                    RemainingBaseQty = lotRemaining,
                    Message = $"{material.ComponentCode} material movements and production balance lots do not reconcile "
                        + $"({remaining:n4} movement vs {lotRemaining:n4} lot base quantity)."
                });
        }

        var wipLots = await db.ProductionBalLots.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode
                && x.WorkOrderId == workOrderId && x.Kind == ProductionBalLotKinds.Wip && x.BaseQty > 0m)
            .OrderBy(x => x.ItemCode).ThenBy(x => x.Uid)
            .Select(x => new { x.ItemCode, x.BaseQty, x.BaseUom, x.LotNo })
            .ToListAsync(cancellationToken);
        foreach (var lot in wipLots)
            findings.Add(new ProductionMaterialReconciliationFinding
            {
                Code = "OPEN_WIP", ItemCode = lot.ItemCode, RemainingBaseQty = lot.BaseQty,
                Message = $"WIP lot {lot.LotNo} for {lot.ItemCode} has {lot.BaseQty:n4} {lot.BaseUom} remaining."
            });

        return new ProductionMaterialReconciliationResult { Findings = findings };
    }
}
