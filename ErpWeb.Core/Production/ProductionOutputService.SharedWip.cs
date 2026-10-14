using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Settings;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionOutputService
{
    private async Task<string> ResolveWipConsumptionPolicyAsync(
        string companyCode,
        CancellationToken cancellationToken)
    {
        if (_settings is null)
            return ProductionWipConsumptionPolicies.Strict;

        try
        {
            var result = await _settings.GetValueAsync(
                AppSettingModules.Production,
                AppSettingCatalogue.ProductionKeys.WipConsumptionPolicy,
                AppSettingScope.Company,
                companyCode,
                branchCode: null,
                cancellationToken: cancellationToken);
            return result.Succeeded
                ? ProductionWipConsumptionPolicies.NormalizeOrStrict(result.Data)
                : ProductionWipConsumptionPolicies.Strict;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A settings read failure must never widen stock eligibility.
            return ProductionWipConsumptionPolicies.Strict;
        }
    }

    private static async Task<decimal> AvailableSharedBomWipQtyAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionWorkOrderRouteStep consumerRouteStep,
        ProductionWorkOrderMaterial material,
        DateTime productionDate,
        CancellationToken cancellationToken)
    {
        if (material.ConversionFactorToBase <= 0m)
            return 0m;

        var locationId = await FindConfiguredProductionLocationIdAsync(
            db, scope.CompanyCode, scope.BranchCode!, consumerRouteStep.WorkCentreCode,
            lockRow: false, cancellationToken: cancellationToken);
        if (locationId is null)
            return 0m;

        var lots = await LoadEligibleSharedBomWipLotsAsync(
            db, scope, material, productionDate, locationId.Value,
            lockRows: false, cancellationToken: cancellationToken);
        var availableBase = lots.Sum(x => x.BaseQty);
        return ProductionMaterialExecutionCalc.IssueQtyForBaseQty(
            availableBase, material.ConversionFactorToBase);
    }

    private static async Task<List<ProductionBalLot>> LockEligibleSharedBomWipLotsAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionWorkOrderRouteStep consumerRouteStep,
        ProductionWorkOrderMaterial material,
        DateTime productionDate,
        CancellationToken cancellationToken)
    {
        var locationId = await FindConfiguredProductionLocationIdAsync(
            db, scope.CompanyCode, scope.BranchCode!, consumerRouteStep.WorkCentreCode,
            lockRow: true, cancellationToken: cancellationToken);
        if (locationId is null)
            return [];

        return await LoadEligibleSharedBomWipLotsAsync(
            db, scope, material, productionDate, locationId.Value,
            lockRows: true, cancellationToken: cancellationToken);
    }

    private static async Task<List<ProductionBalLot>> LoadEligibleSharedBomWipLotsAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionWorkOrderMaterial material,
        DateTime productionDate,
        long locationId,
        bool lockRows,
        CancellationToken cancellationToken,
        int? candidateLimit = null)
    {
        var baseUom = material.BaseUom?.Trim();
        if (string.IsNullOrWhiteSpace(baseUom)
            || material.ConversionFactorToBase <= 0m
            || string.IsNullOrWhiteSpace(material.ComponentCode))
        {
            return [];
        }

        var locationIsActive = await db.ProductionLocations.AsNoTracking().AnyAsync(x =>
            x.Id == locationId
            && x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode
            && x.IsActive, cancellationToken);
        if (!locationIsActive)
            return [];

        IQueryable<ProductionBalLot> candidates = db.ProductionBalLots.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.Kind == ProductionBalLotKinds.Wip
                && x.ItemCode == material.ComponentCode
                && x.ProducingRouteStepId != null
                && x.BaseQty > 0m
                && x.Qty > 0m
                && x.BaseUom == baseUom
                && x.Uom != ""
                && x.ConversionFactorToBase > 0m
                && x.BalanceStage == "PROCESS_WIP"
                && x.OutputType == PrRouteOutputTypes.WipStocked
                && x.StockStatusCode == "AVAILABLE"
                && x.OriginType == "PRODUCED"
                && x.ProductionLocationId == locationId
                && x.LastMovementDate.HasValue
                && x.LastMovementDate <= productionDate
                && db.ProductionWorkOrders.Any(order =>
                    order.Uid == x.WorkOrderId
                    && order.CompanyCode == scope.CompanyCode
                    && order.BranchCode == scope.BranchCode)
                && db.ProductionWorkOrderRouteSteps.Any(route =>
                    route.Uid == x.ProducingRouteStepId
                    && route.WorkOrderId == x.WorkOrderId
                    && route.OutputType == PrRouteOutputTypes.WipStocked))
            .OrderBy(x => x.LastMovementDate ?? DateTime.MinValue)
            .ThenBy(x => x.LotNo)
            .ThenBy(x => x.Uid);
        var ids = await candidates
            .Select(x => x.Uid)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
            return [];

        // Candidate discovery is by tenant-scoped IDs; SQL Server locks each lot in ascending UID.
        var lots = lockRows
            ? await LockBalLotsByIdsAsync(db, ids, cancellationToken)
            : await db.ProductionBalLots.AsNoTracking()
                .Where(x => ids.Contains(x.Uid))
                .ToListAsync(cancellationToken);

        var eligible = new List<ProductionBalLot>(lots.Count);
        foreach (var lot in lots)
        {
            if (!IsSharedBomWipShapeEligible(lot, scope, material, productionDate, locationId))
                continue;

            var sourceRouteStep = lot.ProducingRouteStepId is long routeStepId
                ? await db.ProductionWorkOrderRouteSteps.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Uid == routeStepId && x.WorkOrderId == lot.WorkOrderId, cancellationToken)
                : null;
            if (sourceRouteStep is null
                || !string.Equals(sourceRouteStep.OutputType, PrRouteOutputTypes.WipStocked, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(sourceRouteStep.OutputBaseUom)
                || !string.Equals(sourceRouteStep.OutputBaseUom.Trim(), baseUom, StringComparison.OrdinalIgnoreCase)
                || sourceRouteStep.OutputConversionFactorToBase is not > 0m)
            {
                continue;
            }
            if (!string.Equals(sourceRouteStep.OutputItemCode, lot.ItemCode, StringComparison.OrdinalIgnoreCase)
                || sourceRouteStep.OutputConversionFactorToBase.Value != lot.ConversionFactorToBase)
            {
                continue;
            }

            if (await ProductionCostReadiness.PoolValuationError(db, lot, cancellationToken) is not null)
                continue;
            if (!await HasVerifiedCurrentSourceGenerationAsync(db, lot, scope.CompanyCode, scope.BranchCode!, cancellationToken))
                continue;

            eligible.Add(lot);
        }

        var ordered = eligible
            .OrderBy(x => x.LastMovementDate ?? DateTime.MinValue)
            .ThenBy(x => x.LotNo, StringComparer.Ordinal)
            .ThenBy(x => x.Uid);
        return candidateLimit is > 0
            ? ordered.Take(candidateLimit.Value).ToList()
            : ordered.ToList();
    }

    private static bool IsSharedBomWipShapeEligible(
        ProductionBalLot lot,
        InventoryTenantScope scope,
        ProductionWorkOrderMaterial material,
        DateTime productionDate,
        long locationId) =>
        lot.CompanyCode == scope.CompanyCode
        && lot.BranchCode == scope.BranchCode
        && lot.Kind == ProductionBalLotKinds.Wip
        && lot.ItemCode == material.ComponentCode
        && lot.ProducingRouteStepId.HasValue
        && lot.BaseQty > 0m
        && lot.Qty > 0m
        && !string.IsNullOrWhiteSpace(lot.Uom)
        && !string.IsNullOrWhiteSpace(lot.BaseUom)
        && !string.IsNullOrWhiteSpace(material.BaseUom)
        && string.Equals(lot.BaseUom.Trim(), material.BaseUom.Trim(), StringComparison.OrdinalIgnoreCase)
        && lot.ConversionFactorToBase > 0m
        && !string.IsNullOrWhiteSpace(lot.LotNo)
        && string.Equals(lot.BalanceStage, "PROCESS_WIP", StringComparison.OrdinalIgnoreCase)
        && string.Equals(lot.OutputType, PrRouteOutputTypes.WipStocked, StringComparison.OrdinalIgnoreCase)
        && string.Equals(lot.StockStatusCode, "AVAILABLE", StringComparison.OrdinalIgnoreCase)
        && string.Equals(lot.OriginType, "PRODUCED", StringComparison.OrdinalIgnoreCase)
        && lot.ProductionLocationId == locationId
        && lot.LastMovementDate.HasValue
        && lot.LastMovementDate.Value <= productionDate;

    private static async Task<long?> FindConfiguredProductionLocationIdAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string workCentreCode,
        bool lockRow,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workCentreCode))
            return null;

        // Match ResolveProductionLocationAsync's established work-centre-to-location code rule.
        var code = workCentreCode.Length > 20 ? workCentreCode[..20] : workCentreCode;
        ProductionLocation? location;
        if (lockRow && db.Database.IsSqlServer())
        {
            location = await db.ProductionLocations.FromSqlInterpolated(
                $@"SELECT * FROM dbo.PrProductionLocation WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode={companyCode} AND BranchCode={branchCode} AND Code={code}")
                .SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            location = await db.ProductionLocations.AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.Code == code, cancellationToken);
        }

        return location is { IsActive: true } ? location.Id : null;
    }

    private static async Task<bool> HasVerifiedCurrentSourceGenerationAsync(
        AppDbContext db,
        ProductionBalLot lot,
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken)
    {
        var pool = await db.ProductionPoolValuationRows.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProductionBalLotId == lot.Uid, cancellationToken);
        if (pool is null
            || pool.Generation <= 0
            || pool.Status != ProductionPoolValuationService.Verified
            || pool.TrackedBaseQty != lot.BaseQty
            || pool.TrackedValue != lot.TotalCost)
        {
            return false;
        }

        var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking()
            .Where(x => x.ProductionBalLotId == lot.Uid && x.Generation == pool.Generation)
            .Select(x => new { x.MovementId, x.Status })
            .ToListAsync(cancellationToken);
        if (evidence.Count == 0
            || evidence.Any(x => x.Status != ProductionPoolValuationService.Verified))
        {
            return false;
        }

        var evidenceIds = evidence.Select(x => x.MovementId).ToArray();
        var sealedEvidenceCount = await (
            from movement in db.ProductionBalLotMovements.AsNoTracking()
            join posting in db.StockPostings.AsNoTracking()
                on movement.StockPostingId equals (long?)posting.Id
            where evidenceIds.Contains(movement.Uid)
                && movement.ProductionBalLotId == lot.Uid
                && movement.CompanyCode == companyCode
                && movement.BranchCode == branchCode
                && posting.CompanyCode == companyCode
                && posting.BranchCode == branchCode
                && posting.SealedAtUtc != null
            select movement.Uid)
            .Distinct()
            .CountAsync(cancellationToken);
        if (sealedEvidenceCount != evidence.Count)
            return false;

        var hasNaturalProduction = await db.ProductionBalLotMovements.AsNoTracking().AnyAsync(m =>
            evidenceIds.Contains(m.Uid)
            && m.ProductionBalLotId == lot.Uid
            && m.MovementType == ProductionBalLotMovementTypes.Produce
            && m.OriginalMovementId == null
            && m.WorkOrderId == lot.WorkOrderId
            && m.RouteStepId == lot.ProducingRouteStepId
                && m.BaseUom == lot.BaseUom
                && m.ConversionFactorToBase == lot.ConversionFactorToBase
            && m.CompanyCode == companyCode
            && m.BranchCode == branchCode
            && m.StockPostingId.HasValue
            && m.ProductionOutputId.HasValue
            && db.ProductionOutputs.Any(output =>
                output.Uid == m.ProductionOutputId
                && output.WorkOrderId == lot.WorkOrderId
                && output.RouteStepId == lot.ProducingRouteStepId
                && output.OutputItemCode == lot.ItemCode
                && output.OutputType == PrRouteOutputTypes.WipStocked
                && output.CompanyCode == companyCode
                && output.BranchCode == branchCode
                && output.Status == ProductionOutputStatuses.Posted
                && output.DeletedAtUtc == null)
            && db.StockPostings.Any(posting =>
                posting.Id == m.StockPostingId
                && posting.CompanyCode == companyCode
                && posting.BranchCode == branchCode
                && posting.SealedAtUtc != null
                && !db.StockPostings.Any(reversal =>
                    reversal.ReversesPostingId == posting.Id && reversal.SealedAtUtc != null))
            && !db.ProductionBalLotMovements.Any(reversal =>
                reversal.OriginalMovementId == m.Uid
                && reversal.MovementType == ProductionBalLotMovementTypes.ProduceReversal),
            cancellationToken);
        if (hasNaturalProduction) return true;

        // A verified sealed TransferIn is valid current-generation provenance when its transfer
        // document, matching OUT movement and genealogy edge all agree with this natural producer pool.
        // The caller's existing production-location checks remain unchanged.
        return await db.ProductionBalLotMovements.AsNoTracking().AnyAsync(m =>
            evidenceIds.Contains(m.Uid)
            && m.ProductionBalLotId == lot.Uid
            && m.MovementType == ProductionBalLotMovementTypes.TransferIn
            && m.OriginalMovementId == null
            && m.WorkOrderId == lot.WorkOrderId
            && m.RouteStepId == lot.ProducingRouteStepId
            && m.WorkOrderOperationId == lot.WorkOrderOperationId
            && m.ItemCode == lot.ItemCode
            && m.BaseUom == lot.BaseUom
            && m.ConversionFactorToBase == lot.ConversionFactorToBase
            && m.CompanyCode == companyCode
            && m.BranchCode == branchCode
            && m.StockPostingId.HasValue
            && m.MovementGroupId.HasValue
            && db.ProductionValuationEvidenceRows.Any(e => e.MovementId == m.Uid
                && e.ProductionBalLotId == lot.Uid && e.Status == ProductionPoolValuationService.Verified)
            && db.ProductionWorkOrderTransfers.Any(transfer =>
                transfer.TransferInMovementId == m.Uid
                && transfer.TransferOutMovementId != null
                && transfer.Status == ProductionWorkOrderTransferStatuses.Posted
                && transfer.DeletedAtUtc == null
                && transfer.StockPostingId == m.StockPostingId
                && transfer.MovementGroupId == m.MovementGroupId
                && transfer.DestinationProductionBalLotId == lot.Uid
                && transfer.TargetWorkOrderId == lot.WorkOrderId
                && transfer.TargetProducingRouteStepId == lot.ProducingRouteStepId
                && transfer.TargetProducerOperationId == lot.WorkOrderOperationId
                && transfer.ItemCode == lot.ItemCode
                && transfer.LotNo == lot.LotNo
                && transfer.CompanyCode == companyCode && transfer.BranchCode == branchCode
                && db.ProductionBalLotMovements.Any(outbound =>
                    outbound.Uid == transfer.TransferOutMovementId
                    && outbound.MovementType == ProductionBalLotMovementTypes.TransferOut
                    && outbound.OriginalMovementId == null
                    && outbound.StockPostingId == m.StockPostingId
                    && outbound.MovementGroupId == m.MovementGroupId
                    && outbound.ItemCode == m.ItemCode
                    && outbound.BaseUom == m.BaseUom
                    && outbound.BaseQty == m.BaseQty
                    && outbound.TotalCost == m.TotalCost
                    && db.ProductionValuationEvidenceRows.Any(outEvidence =>
                        outEvidence.MovementId == outbound.Uid
                        && outEvidence.Status == ProductionPoolValuationService.Verified)
                    && ProductionActiveEdgeResolver.Dependencies(db).Any(edge =>
                        edge.ContributorMovementId == outbound.Uid
                        && edge.ConsumerMovementId == m.Uid
                        && edge.StockPostingId == m.StockPostingId))
                && db.StockPostings.Any(posting =>
                    posting.Id == m.StockPostingId
                    && posting.CompanyCode == companyCode
                    && posting.BranchCode == branchCode
                    && posting.SourceDocumentType == ProductionDocumentTypes.WorkOrderTransfer
                    && posting.SourceDocumentNo == transfer.TransferNo
                    && posting.SealedAtUtc != null
                    && !db.StockPostings.Any(reversal =>
                        reversal.ReversesPostingId == posting.Id && reversal.SealedAtUtc != null))
                && db.ProductionPostingLinks.Any(link =>
                    link.Uid == transfer.ProductionPostingLinkId
                    && link.Status == ProductionPostingLinkStatuses.Succeeded)),
            cancellationToken);
    }

    public async Task<IvMasterOperationResult<ProductionSharedWipSourceInquiry>> GetSharedWipSourceInquiryAsync(
        ProductionSharedWipSourceInquiryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<ProductionSharedWipSourceInquiry>.Fail(
                IvMasterErrorCode.AccessDenied, "Access denied.");

        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<ProductionSharedWipSourceInquiry>.Fail(
                IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");
        if (request.WorkOrderMaterialId <= 0)
            return IvMasterOperationResult<ProductionSharedWipSourceInquiry>.Fail(
                IvMasterErrorCode.Validation, "A BOM material line is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        ProductionOutput? output = null;
        if (request.ProductionOutputId is > 0)
        {
            output = await db.ProductionOutputs.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Uid == request.ProductionOutputId
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode, cancellationToken);
            if (output is null)
                return IvMasterOperationResult<ProductionSharedWipSourceInquiry>.Fail(
                    IvMasterErrorCode.NotFound, "Daily Production document was not found.");
        }

        var material = await db.ProductionWorkOrderMaterials.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Uid == request.WorkOrderMaterialId
            && db.ProductionWorkOrders.Any(order =>
                order.Uid == x.WorkOrderId
                && order.CompanyCode == scope.CompanyCode
                && order.BranchCode == scope.BranchCode), cancellationToken);
        if (material is null
            || material.WorkOrderOperationId is null
            || !string.Equals(material.SupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.OrdinalIgnoreCase)
            || material.ProducingRouteStepId is null)
        {
            return IvMasterOperationResult<ProductionSharedWipSourceInquiry>.Fail(
                IvMasterErrorCode.Validation, "This line does not have a valid internal-route WIP source contract.");
        }

        if (output is not null
            && (output.WorkOrderId != material.WorkOrderId
                || output.WorkOrderOperationId != material.WorkOrderOperationId
                || output.Status is not (ProductionOutputStatuses.New or ProductionOutputStatuses.Posted)))
        {
            return IvMasterOperationResult<ProductionSharedWipSourceInquiry>.Fail(
                IvMasterErrorCode.Validation, "The BOM line does not belong to this Daily Production document.");
        }

        var policy = output is null
            ? await ResolveWipConsumptionPolicyAsync(scope.CompanyCode, cancellationToken)
            : ProductionWipConsumptionPolicies.NormalizeOrStrict(output.WipConsumptionPolicy);
        if (policy != ProductionWipConsumptionPolicies.SharedBomWip)
        {
            return IvMasterOperationResult<ProductionSharedWipSourceInquiry>.Fail(
                IvMasterErrorCode.Validation, "Shared BOM WIP is not enabled for this draft.");
        }

        var consumerRouteStep = await db.ProductionWorkOrderRouteSteps.AsNoTracking().SingleOrDefaultAsync(x =>
            x.WorkOrderId == material.WorkOrderId
            && db.ProductionWorkOrderOperations.Any(operation =>
                operation.Uid == material.WorkOrderOperationId && operation.RouteStepId == x.Uid),
            cancellationToken);
        if (consumerRouteStep is null)
            return IvMasterOperationResult<ProductionSharedWipSourceInquiry>.Fail(
                IvMasterErrorCode.NotFound, "The consuming route step was not found.");

        var canViewCost = await _access.CanAsync(
            MenuCodes.PlanningDailyProduction, PermissionCodes.ViewCost, cancellationToken);
        var consumingWorkOrderNo = await db.ProductionWorkOrders.AsNoTracking()
            .Where(x => x.Uid == material.WorkOrderId
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .Select(x => x.WorkOrderNo)
            .SingleOrDefaultAsync(cancellationToken) ?? string.Empty;
        var consumingDocumentNo = output?.DocumentNo ?? "UNSAVED DRAFT";
        var consumingQty = request.ConsumeQty ?? 0m;
        if (output is not null)
        {
            consumingQty = await db.ProductionOutputMaterials.AsNoTracking()
                .Where(x => x.ProductionOutputId == output.Uid
                    && x.WorkOrderMaterialId == material.Uid
                    && x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.DocumentRevision == output.DocumentRevision)
                .Select(x => (decimal?)x.ConsumeQty)
                .SingleOrDefaultAsync(cancellationToken) ?? 0m;
        }
        var historical = output?.Status == ProductionOutputStatuses.Posted;
        if (historical && output is not null)
        {
            var historic = await BuildHistoricalSharedWipSourceInquiryAsync(
                db, scope, output, material, canViewCost, cancellationToken);
            return IvMasterOperationResult<ProductionSharedWipSourceInquiry>.Ok(historic);
        }

        var productionDate = output?.ProductionDate ?? request.ProductionDate ?? DateTime.Today;
        var locationId = await FindConfiguredProductionLocationIdAsync(
            db, scope.CompanyCode, scope.BranchCode!, consumerRouteStep.WorkCentreCode,
            lockRow: false, cancellationToken: cancellationToken);
        if (locationId is null)
        {
            return IvMasterOperationResult<ProductionSharedWipSourceInquiry>.Ok(new()
            {
                ComponentCode = material.ComponentCode,
                BaseUom = material.BaseUom ?? string.Empty,
                ConsumingDocumentNo = consumingDocumentNo,
                ConsumingWorkOrderNo = consumingWorkOrderNo,
                ConsumingQty = consumingQty,
                ConsumingUom = material.RequiredUom ?? string.Empty,
                WipConsumptionPolicy = policy,
                CanViewCost = canViewCost,
                Message = "No active production location is configured for this work centre; shared WIP inquiry is unavailable."
            });
        }

        var lots = await LoadEligibleSharedBomWipLotsAsync(
            db, scope, material, productionDate, locationId.Value,
            lockRows: false, cancellationToken: cancellationToken, candidateLimit: 101);
        const int maxSourceLots = 100;
        var shownLots = lots.Take(maxSourceLots).ToList();
        var sourceRows = new List<ProductionSharedWipSourceRow>(shownLots.Count);
        foreach (var lot in shownLots)
        {
            var route = lot.ProducingRouteStepId is long sourceRouteStepId
                ? await db.ProductionWorkOrderRouteSteps.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Uid == sourceRouteStepId && x.WorkOrderId == lot.WorkOrderId, cancellationToken)
                : null;
            var receipts = await ReadCurrentGenerationReceiptsAsync(db, lot, cancellationToken);
            sourceRows.Add(new ProductionSharedWipSourceRow
            {
                ProductionBalLotId = lot.Uid,
                LotNo = lot.LotNo,
                SourceWorkOrderNo = lot.WorkOrderNo,
                SourceRouteStageSequence = route?.StageSequence,
                SourceWorkCentreCode = route?.WorkCentreCode,
                LastMovementDate = lot.LastMovementDate,
                AvailableBaseQty = lot.BaseQty,
                Receipts = receipts
            });
        }

        var message = lots.Count == 0
            ? "No eligible shared WIP lots are available for this item, location, UOM, date, status, and verified valuation scope."
            : lots.Count > maxSourceLots
                ? $"Showing the first {maxSourceLots} eligible lots. Posting rechecks and locks the required sources."
                : "Source preview is advisory; posting rechecks and locks the eligible lots.";
        return IvMasterOperationResult<ProductionSharedWipSourceInquiry>.Ok(new()
        {
            ComponentCode = material.ComponentCode,
            BaseUom = material.BaseUom ?? string.Empty,
            ConsumingDocumentNo = consumingDocumentNo,
            ConsumingWorkOrderNo = consumingWorkOrderNo,
            ConsumingQty = consumingQty,
            ConsumingUom = material.RequiredUom ?? string.Empty,
            WipConsumptionPolicy = policy,
            CanViewCost = canViewCost,
            Message = message,
            Sources = sourceRows
        });
    }

    private static async Task<ProductionSharedWipSourceInquiry> BuildHistoricalSharedWipSourceInquiryAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionOutput output,
        ProductionWorkOrderMaterial material,
        bool canViewCost,
        CancellationToken cancellationToken)
    {
        var consumes = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(x => x.ProductionOutputId == output.Uid
                && x.WorkOrderMaterialId == material.Uid
                && x.StockPostingId == output.PostingId
                && x.MovementType == ProductionBalLotMovementTypes.Consume
                && x.OriginalMovementId == null
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        var lotIds = consumes.Select(x => x.ProductionBalLotId).Distinct().ToArray();
        var lots = await db.ProductionBalLots.AsNoTracking()
            .Where(x => lotIds.Contains(x.Uid)
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .ToDictionaryAsync(x => x.Uid, cancellationToken);

        var consumeIds = consumes.Select(x => x.Uid).ToArray();
        List<ProductionMovementAllocation> allocations = [];
        if (consumeIds.Length > 0)
        {
            allocations = await ProductionActiveEdgeResolver.Allocations(db).AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && consumeIds.Contains(x.OutboundMovementId)
                    && db.StockPostings.Any(posting =>
                        posting.Id == x.StockPostingId
                        && posting.CompanyCode == scope.CompanyCode
                        && posting.BranchCode == scope.BranchCode
                        && posting.SealedAtUtc != null))
                .ToListAsync(cancellationToken);
        }
        var receiptIds = allocations.Select(x => x.ReceiptMovementId).Distinct().ToArray();
        var receiptFacts = new List<HistoricalReceiptFact>();
        if (receiptIds.Length > 0)
        {
            receiptFacts = await (from movement in db.ProductionBalLotMovements.AsNoTracking()
                     join producer in db.ProductionOutputs.AsNoTracking() on movement.ProductionOutputId equals producer.Uid
                     join order in db.ProductionWorkOrders.AsNoTracking() on producer.WorkOrderId equals order.Uid
                     join route in db.ProductionWorkOrderRouteSteps.AsNoTracking() on producer.RouteStepId equals route.Uid
                     where receiptIds.Contains(movement.Uid)
                         && movement.MovementType == ProductionBalLotMovementTypes.Produce
                         && movement.OriginalMovementId == null
                         && movement.CompanyCode == scope.CompanyCode
                         && movement.BranchCode == scope.BranchCode
                         && producer.CompanyCode == scope.CompanyCode
                         && producer.BranchCode == scope.BranchCode
                         && order.CompanyCode == scope.CompanyCode
                         && order.BranchCode == scope.BranchCode
                     select new
                     {
                         MovementId = movement.Uid,
                         movement.ProductionBalLotId,
                         ProducerDocumentNo = producer.DocumentNo,
                         ProducerWorkOrderNo = order.WorkOrderNo,
                         ProducerRouteStageSequence = (int?)route.StageSequence,
                         ProducerWorkCentreCode = route.WorkCentreCode,
                         movement.BaseQty
                     }).Select(x => new HistoricalReceiptFact(
                         x.MovementId,
                         x.ProductionBalLotId,
                         x.ProducerDocumentNo,
                         x.ProducerWorkOrderNo,
                         x.ProducerRouteStageSequence,
                         x.ProducerWorkCentreCode,
                         x.BaseQty))
                .ToListAsync(cancellationToken);
        }

        var receiptsByMovementId = receiptFacts.ToDictionary(x => x.MovementId);
        var sourceRows = new List<ProductionSharedWipSourceRow>(consumes.Count);
        foreach (var movement in consumes)
        {
            if (!lots.TryGetValue(movement.ProductionBalLotId, out var lot))
                continue;

            var sourceRoute = lot.ProducingRouteStepId is long sourceRouteStepId
                ? await db.ProductionWorkOrderRouteSteps.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Uid == sourceRouteStepId && x.WorkOrderId == lot.WorkOrderId, cancellationToken)
                : null;
            var receipts = allocations
                .Where(x => x.OutboundMovementId == movement.Uid
                    && receiptsByMovementId.TryGetValue(x.ReceiptMovementId, out var receipt)
                    && receipt.ProductionBalLotId == lot.Uid)
                .GroupBy(x => x.ReceiptMovementId)
                .Select(group =>
                {
                    var receipt = receiptsByMovementId[group.Key];
                    return new ProductionSharedWipReceiptRow
                    {
                        ProducerDocumentNo = receipt.ProducerDocumentNo,
                        ProducerWorkOrderNo = receipt.ProducerWorkOrderNo,
                        ProducerRouteStageSequence = receipt.ProducerRouteStageSequence,
                        ProducerWorkCentreCode = receipt.ProducerWorkCentreCode,
                        ReceiptBaseQty = receipt.BaseQty,
                        AllocatedBaseQty = group.Sum(x => x.BaseQty)
                    };
                })
                .OrderBy(x => x.ProducerWorkOrderNo, StringComparer.Ordinal)
                .ThenBy(x => x.ProducerDocumentNo, StringComparer.Ordinal)
                .ToList();

            sourceRows.Add(new ProductionSharedWipSourceRow
            {
                ProductionBalLotId = lot.Uid,
                LotNo = lot.LotNo,
                SourceWorkOrderNo = lot.WorkOrderNo,
                SourceRouteStageSequence = sourceRoute?.StageSequence,
                SourceWorkCentreCode = sourceRoute?.WorkCentreCode,
                ConsumedBaseQty = movement.BaseQty,
                ActualPooledConsumptionCost = canViewCost ? movement.TotalCost : null,
                Receipts = receipts
            });
        }

        return new ProductionSharedWipSourceInquiry
        {
            ConsumingDocumentNo = output.DocumentNo,
            ConsumingWorkOrderNo = await db.ProductionWorkOrders.AsNoTracking()
                .Where(x => x.Uid == output.WorkOrderId
                    && x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode)
                .Select(x => x.WorkOrderNo)
                .SingleOrDefaultAsync(cancellationToken) ?? string.Empty,
            ConsumingQty = await db.ProductionOutputMaterials.AsNoTracking()
                .Where(x => x.ProductionOutputId == output.Uid
                    && x.WorkOrderMaterialId == material.Uid
                    && x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.DocumentRevision == output.DocumentRevision)
                .Select(x => (decimal?)x.ConsumeQty)
                .SingleOrDefaultAsync(cancellationToken) ?? 0m,
            ConsumingUom = material.RequiredUom ?? string.Empty,
            ComponentCode = material.ComponentCode,
            BaseUom = material.BaseUom ?? string.Empty,
            WipConsumptionPolicy = ProductionWipConsumptionPolicies.NormalizeOrStrict(output.WipConsumptionPolicy),
            IsHistorical = true,
            IsReversed = false,
            CanViewCost = canViewCost,
            Message = sourceRows.Count == 0
                ? "No shared WIP consume movement was recorded for this BOM line."
                : "Receipt allocations show genealogy quantities. Consumption cost is the actual pooled-average cost for each source lot.",
            Sources = sourceRows
        };
    }

    private static async Task<IReadOnlyList<ProductionSharedWipReceiptRow>> ReadCurrentGenerationReceiptsAsync(
        AppDbContext db,
        ProductionBalLot lot,
        CancellationToken cancellationToken)
    {
        var pool = await db.ProductionPoolValuationRows.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProductionBalLotId == lot.Uid, cancellationToken);
        if (pool is null)
            return [];

        return await (from movement in db.ProductionBalLotMovements.AsNoTracking()
                      join evidence in db.ProductionValuationEvidenceRows.AsNoTracking()
                          on movement.Uid equals evidence.MovementId
                      join producer in db.ProductionOutputs.AsNoTracking()
                          on movement.ProductionOutputId equals producer.Uid
                      join order in db.ProductionWorkOrders.AsNoTracking()
                          on producer.WorkOrderId equals order.Uid
                      join route in db.ProductionWorkOrderRouteSteps.AsNoTracking()
                          on producer.RouteStepId equals route.Uid
                      where movement.ProductionBalLotId == lot.Uid
                          && movement.CompanyCode == lot.CompanyCode
                          && movement.BranchCode == lot.BranchCode
                          && movement.MovementType == ProductionBalLotMovementTypes.Produce
                          && movement.OriginalMovementId == null
                          && evidence.Generation == pool.Generation
                          && evidence.Status == ProductionPoolValuationService.Verified
                          && producer.CompanyCode == lot.CompanyCode
                          && producer.BranchCode == lot.BranchCode
                          && producer.WorkOrderId == lot.WorkOrderId
                          && producer.Status == ProductionOutputStatuses.Posted
                          && producer.DeletedAtUtc == null
                          && order.CompanyCode == lot.CompanyCode
                          && order.BranchCode == lot.BranchCode
                          && route.WorkOrderId == producer.WorkOrderId
                          && producer.OutputItemCode == lot.ItemCode
                          && producer.OutputType == PrRouteOutputTypes.WipStocked
                          && route.OutputType == PrRouteOutputTypes.WipStocked
                          && route.OutputItemCode == lot.ItemCode
                          && route.OutputBaseUom == lot.BaseUom
                          && route.OutputConversionFactorToBase == lot.ConversionFactorToBase
                          && movement.BaseUom == lot.BaseUom
                          && movement.ConversionFactorToBase == lot.ConversionFactorToBase
                          && movement.StockPostingId.HasValue
                          && db.StockPostings.Any(posting =>
                              posting.Id == movement.StockPostingId
                              && posting.CompanyCode == lot.CompanyCode
                              && posting.BranchCode == lot.BranchCode
                              && posting.SealedAtUtc != null
                              && !db.StockPostings.Any(reversal =>
                                  reversal.ReversesPostingId == posting.Id && reversal.SealedAtUtc != null))
                          && !db.ProductionBalLotMovements.Any(reversal =>
                              reversal.OriginalMovementId == movement.Uid
                              && reversal.MovementType == ProductionBalLotMovementTypes.ProduceReversal)
                      orderby order.WorkOrderNo, producer.DocumentNo, movement.Uid
                      select new ProductionSharedWipReceiptRow
                      {
                          ProducerDocumentNo = producer.DocumentNo,
                          ProducerWorkOrderNo = order.WorkOrderNo,
                          ProducerRouteStageSequence = route.StageSequence,
                          ProducerWorkCentreCode = route.WorkCentreCode,
                          ReceiptBaseQty = movement.BaseQty
                      }).ToListAsync(cancellationToken);
    }

    private sealed record HistoricalReceiptFact(
        long MovementId,
        long ProductionBalLotId,
        string ProducerDocumentNo,
        string ProducerWorkOrderNo,
        int? ProducerRouteStageSequence,
        string ProducerWorkCentreCode,
        decimal BaseQty);
}
