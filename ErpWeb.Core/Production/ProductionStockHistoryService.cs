using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed class ProductionStockHistoryService : IProductionStockHistoryService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;

    public ProductionStockHistoryService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
    }

    public async Task<IvMasterOperationResult<ProductionStockCardResult>> GetCardAsync(
        ProductionStockHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var gate = await AuthorizeAsync(cancellationToken);
        if (gate is not null)
            return IvMasterOperationResult<ProductionStockCardResult>.Fail(gate.Value.Code, gate.Value.Message);

        if (!TryRequireItemScope(query, out var itemError))
            return IvMasterOperationResult<ProductionStockCardResult>.Fail(IvMasterErrorCode.Validation, itemError);
        if (query.From is null || query.To is null)
            return IvMasterOperationResult<ProductionStockCardResult>.Fail(
                IvMasterErrorCode.Validation, "A half-open period [From, To) is required.");
        if (query.To.Value <= query.From.Value)
            return IvMasterOperationResult<ProductionStockCardResult>.Fail(
                IvMasterErrorCode.Validation, "To must be later than From.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = _tenant.TryBranchScope()!;
        var epoch = await LoadActiveEpochAsync(db, scope, cancellationToken);
        if (epoch is null)
            return IvMasterOperationResult<ProductionStockCardResult>.Ok(EmptyCard(ProductionStockCoverageCodes.NoActiveEpoch));

        var watermark = await ResolveWatermarkAsync(db, scope, epoch.Id, query.Watermark, cancellationToken);
        var coverage = CoverageFor(query.From.Value, epoch);
        var movements = await LoadSealedMovementsAsync(db, scope, epoch.Id, watermark, query, cancellationToken);

        var from = query.From.Value;
        var to = query.To.Value;
        var opening = IvQty.Round(movements.Where(x => x.EffectiveAt < from).Sum(x => x.SignedBaseQty));
        var period = movements.Where(x => x.EffectiveAt >= from && x.EffectiveAt < to).ToList();
        var periodIn = IvQty.Round(period.Where(x => x.SignedBaseQty > 0m).Sum(x => x.SignedBaseQty));
        var periodOut = IvQty.Round(period.Where(x => x.SignedBaseQty < 0m).Sum(x => -x.SignedBaseQty));
        var closing = IvQty.Round(opening + periodIn - periodOut);

        var running = opening;
        var rows = new List<ProductionStockHistoryRow>(period.Count);
        foreach (var movement in period)
        {
            running = IvQty.Round(running + movement.SignedBaseQty);
            rows.Add(ToRow(movement, running));
        }

        // Document type is a display filter: opening/closing stay on the true balance.
        var displayRows = ApplyDocumentTypeFilter(rows, query.SourceDocumentType);

        return IvMasterOperationResult<ProductionStockCardResult>.Ok(new ProductionStockCardResult
        {
            CoverageCode = coverage.Code,
            CoverageWarning = coverage.Warning,
            EpochEffectiveFrom = epoch.EffectiveFrom,
            Watermark = watermark,
            OpeningQty = opening,
            PeriodInQty = periodIn,
            PeriodOutQty = periodOut,
            ClosingQty = closing,
            Rows = displayRows,
        });
    }

    public async Task<IvMasterOperationResult<ProductionStockMovementResult>> GetMovementsAsync(
        ProductionStockHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var gate = await AuthorizeAsync(cancellationToken);
        if (gate is not null)
            return IvMasterOperationResult<ProductionStockMovementResult>.Fail(gate.Value.Code, gate.Value.Message);

        if (!TryRequireItemScope(query, out var itemError))
            return IvMasterOperationResult<ProductionStockMovementResult>.Fail(IvMasterErrorCode.Validation, itemError);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = _tenant.TryBranchScope()!;
        var epoch = await LoadActiveEpochAsync(db, scope, cancellationToken);
        if (epoch is null)
        {
            return IvMasterOperationResult<ProductionStockMovementResult>.Ok(new ProductionStockMovementResult
            {
                CoverageCode = ProductionStockCoverageCodes.NoActiveEpoch,
                CoverageWarning = NoEpochWarning,
            });
        }

        var watermark = await ResolveWatermarkAsync(db, scope, epoch.Id, query.Watermark, cancellationToken);
        var coverage = CoverageFor(query.From ?? epoch.EffectiveFrom, epoch);
        var movements = await LoadSealedMovementsAsync(db, scope, epoch.Id, watermark, query, cancellationToken);
        if (query.From is DateTime from)
            movements = movements.Where(x => x.EffectiveAt >= from).ToList();
        if (query.To is DateTime to)
            movements = movements.Where(x => x.EffectiveAt < to).ToList();

        var rows = ApplyDocumentTypeFilter(movements.Select(x => ToRow(x, 0m)).ToList(), query.SourceDocumentType);
        return IvMasterOperationResult<ProductionStockMovementResult>.Ok(new ProductionStockMovementResult
        {
            CoverageCode = coverage.Code,
            CoverageWarning = coverage.Warning,
            EpochEffectiveFrom = epoch.EffectiveFrom,
            Watermark = watermark,
            Rows = rows,
        });
    }

    public async Task<IvMasterOperationResult<ProductionStockAsOfResult>> GetAsOfAsync(
        ProductionStockHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var gate = await AuthorizeAsync(cancellationToken);
        if (gate is not null)
            return IvMasterOperationResult<ProductionStockAsOfResult>.Fail(gate.Value.Code, gate.Value.Message);

        if (!TryRequireItemScope(query, out var itemError))
            return IvMasterOperationResult<ProductionStockAsOfResult>.Fail(IvMasterErrorCode.Validation, itemError);
        if (query.AsOf is null)
            return IvMasterOperationResult<ProductionStockAsOfResult>.Fail(
                IvMasterErrorCode.Validation, "An as-of instant is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = _tenant.TryBranchScope()!;
        var epoch = await LoadActiveEpochAsync(db, scope, cancellationToken);
        if (epoch is null)
        {
            return IvMasterOperationResult<ProductionStockAsOfResult>.Ok(new ProductionStockAsOfResult
            {
                CoverageCode = ProductionStockCoverageCodes.NoActiveEpoch,
                CoverageWarning = NoEpochWarning,
            });
        }

        var watermark = await ResolveWatermarkAsync(db, scope, epoch.Id, query.Watermark, cancellationToken);
        var coverage = CoverageFor(query.AsOf.Value, epoch);
        var movements = await LoadSealedMovementsAsync(db, scope, epoch.Id, watermark, query, cancellationToken);
        var asOf = query.AsOf.Value;
        var rows = movements
            .Where(x => x.EffectiveAt < asOf)
            .GroupBy(x => new StockIdentity(
                x.ItemCode, x.BaseUom, x.WorkOrderNo, x.BalanceStage,
                x.ProductionLocationCode, x.LotIdentity, x.StockStatusCode))
            .Select(g => new ProductionStockAsOfRow
            {
                ItemCode = g.Key.ItemCode,
                BaseUom = g.Key.BaseUom,
                WorkOrderNo = g.Key.WorkOrderNo,
                BalanceStage = g.Key.BalanceStage,
                ProductionLocationCode = g.Key.ProductionLocationCode,
                LotIdentity = g.Key.LotIdentity,
                StockStatusCode = g.Key.StockStatusCode,
                BaseQty = IvQty.Round(g.Sum(x => x.SignedBaseQty)),
            })
            .OrderBy(x => x.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.WorkOrderNo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.BalanceStage, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.LotIdentity, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return IvMasterOperationResult<ProductionStockAsOfResult>.Ok(new ProductionStockAsOfResult
        {
            CoverageCode = coverage.Code,
            CoverageWarning = coverage.Warning,
            EpochEffectiveFrom = epoch.EffectiveFrom,
            Watermark = watermark,
            Rows = rows,
        });
    }

    public async Task<IvMasterOperationResult<ProductionStockReconcileResult>> ReconcileAsync(
        ProductionStockHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var gate = await AuthorizeAsync(cancellationToken);
        if (gate is not null)
            return IvMasterOperationResult<ProductionStockReconcileResult>.Fail(gate.Value.Code, gate.Value.Message);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = _tenant.TryBranchScope()!;
        var epoch = await LoadActiveEpochAsync(db, scope, cancellationToken);
        if (epoch is null)
        {
            return IvMasterOperationResult<ProductionStockReconcileResult>.Ok(new ProductionStockReconcileResult
            {
                CoverageCode = ProductionStockCoverageCodes.NoActiveEpoch,
                CoverageWarning = NoEpochWarning,
            });
        }

        var watermark = await ResolveWatermarkAsync(db, scope, epoch.Id, query.Watermark, cancellationToken);
        var movements = await LoadSealedMovementsAsync(db, scope, epoch.Id, watermark, query, applyItemFilter: true, cancellationToken);
        var ledgerByLot = movements
            .Where(x => x.ProductionBalLotId > 0)
            .GroupBy(x => x.ProductionBalLotId)
            .ToDictionary(g => g.Key, g => IvQty.Round(g.Sum(x => x.SignedBaseQty)));

        var liveQuery = db.ProductionBalLots.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);
        if (!string.IsNullOrWhiteSpace(query.ItemCode))
        {
            var item = query.ItemCode.Trim();
            liveQuery = liveQuery.Where(x => x.ItemCode == item);
        }
        if (!string.IsNullOrWhiteSpace(query.BaseUom))
        {
            var uom = query.BaseUom.Trim();
            liveQuery = liveQuery.Where(x => x.BaseUom == uom);
        }

        var liveLots = await liveQuery.ToListAsync(cancellationToken);
        var findings = new List<ProductionStockReconcileFinding>();
        var seen = new HashSet<long>();

        foreach (var lot in liveLots)
        {
            seen.Add(lot.Uid);
            ledgerByLot.TryGetValue(lot.Uid, out var ledgerQty);
            var liveQty = IvQty.Round(lot.BaseQty);
            var hasLedger = ledgerByLot.ContainsKey(lot.Uid);

            if (!hasLedger)
            {
                findings.Add(Finding(
                    liveQty == 0m ? ProductionStockReconcileCodes.ZeroIdentity : ProductionStockReconcileCodes.MissingLedger,
                    liveQty == 0m ? "INFO" : "ERROR",
                    liveQty == 0m
                        ? "Live identity has zero base quantity and no sealed V2 movements."
                        : "Live identity has no sealed V2 movements.",
                    lot, liveQty, hasLedger ? ledgerQty : null));
                continue;
            }

            if (liveQty != ledgerQty)
            {
                findings.Add(Finding(
                    ProductionStockReconcileCodes.QtyMismatch,
                    "ERROR",
                    "Live BaseQty does not equal opening plus signed V2 movements.",
                    lot, liveQty, ledgerQty));
            }
            else if (liveQty == 0m)
            {
                findings.Add(Finding(
                    ProductionStockReconcileCodes.ZeroIdentity,
                    "INFO",
                    "Zero-quantity identity is present on both live and ledger sides.",
                    lot, liveQty, ledgerQty));
            }
        }

        foreach (var pair in ledgerByLot.Where(x => !seen.Contains(x.Key)))
        {
            var lotId = pair.Key;
            var ledgerQty = pair.Value;
            var sample = movements.First(x => x.ProductionBalLotId == lotId);
            findings.Add(new ProductionStockReconcileFinding
            {
                Code = ledgerQty == 0m
                    ? ProductionStockReconcileCodes.ZeroIdentity
                    : ProductionStockReconcileCodes.MissingLive,
                Severity = ledgerQty == 0m ? "INFO" : "ERROR",
                Message = ledgerQty == 0m
                    ? "Ledger identity reconstructs to zero and has no live balance."
                    : "Sealed V2 movements exist for an identity that is missing from live balances.",
                ProductionBalLotId = lotId,
                ItemCode = sample.ItemCode,
                WorkOrderNo = sample.WorkOrderNo,
                BalanceStage = sample.BalanceStage,
                ProductionLocationCode = sample.ProductionLocationCode,
                LotIdentity = sample.LotIdentity,
                StockStatusCode = sample.StockStatusCode,
                LiveBaseQty = null,
                LedgerBaseQty = ledgerQty,
                Delta = ledgerQty,
            });
        }

        return IvMasterOperationResult<ProductionStockReconcileResult>.Ok(new ProductionStockReconcileResult
        {
            CoverageCode = ProductionStockCoverageCodes.V2,
            EpochEffectiveFrom = epoch.EffectiveFrom,
            Watermark = watermark,
            Findings = findings
                .OrderBy(x => x.Severity, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ItemCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ProductionBalLotId)
                .ToList(),
        });
    }

    private async Task<(IvMasterErrorCode Code, string Message)?> AuthorizeAsync(CancellationToken cancellationToken)
    {
        if (!await _access.CanAsync(MenuCodes.PlanningProductionBalance, PermissionCodes.Access, cancellationToken))
            return (IvMasterErrorCode.AccessDenied, "Access denied.");
        if (_tenant.TryBranchScope() is null)
            return (IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");
        return null;
    }

    private static bool TryRequireItemScope(ProductionStockHistoryQuery query, out string error)
    {
        if (string.IsNullOrWhiteSpace(query.ItemCode) || string.IsNullOrWhiteSpace(query.BaseUom))
        {
            error = "Item and base UOM are required.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static async Task<StockLedgerEpoch?> LoadActiveEpochAsync(
        AppDbContext db, InventoryTenantScope scope, CancellationToken cancellationToken) =>
        await db.StockLedgerEpochs.AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode
            && x.Status == StockLedgerEpochStatuses.Active, cancellationToken);

    private static async Task<long> ResolveWatermarkAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        long epochId,
        long? requested,
        CancellationToken cancellationToken)
    {
        if (requested is > 0)
            return requested.Value;

        return await db.StockPostings.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.LedgerEpochId == epochId
                && x.SealedAtUtc != null)
            .MaxAsync(x => (long?)x.PostingSequence, cancellationToken) ?? 0L;
    }

    private static (string Code, string? Warning) CoverageFor(DateTime instant, StockLedgerEpoch epoch) =>
        instant < epoch.EffectiveFrom
            ? (ProductionStockCoverageCodes.HistoryBeforeCutover,
                "Exact V2 coverage begins at the active epoch cutover. Earlier history is a legacy inquiry.")
            : (ProductionStockCoverageCodes.V2, null);

    private static ProductionStockCardResult EmptyCard(string coverageCode) => new()
    {
        CoverageCode = coverageCode,
        CoverageWarning = coverageCode == ProductionStockCoverageCodes.NoActiveEpoch ? NoEpochWarning : null,
    };

    private const string NoEpochWarning = "No ACTIVE stock-ledger epoch is configured for this branch. V2 production history is empty.";

    private static async Task<IReadOnlyList<SealedMovement>> LoadSealedMovementsAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        long epochId,
        long watermark,
        ProductionStockHistoryQuery query,
        CancellationToken cancellationToken) =>
        await LoadSealedMovementsAsync(db, scope, epochId, watermark, query, applyItemFilter: true, cancellationToken);

    private static async Task<IReadOnlyList<SealedMovement>> LoadSealedMovementsAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        long epochId,
        long watermark,
        ProductionStockHistoryQuery query,
        bool applyItemFilter,
        CancellationToken cancellationToken)
    {
        var q =
            from m in db.ProductionBalLotMovements.AsNoTracking()
            join p in db.StockPostings.AsNoTracking() on m.StockPostingId equals p.Id
            where m.LedgerVersion == 2
                && m.StockPostingId != null
                && p.SealedAtUtc != null
                && p.CompanyCode == scope.CompanyCode
                && p.BranchCode == scope.BranchCode
                && m.CompanyCode == scope.CompanyCode
                && m.BranchCode == scope.BranchCode
                && p.LedgerEpochId == epochId
                && p.PostingSequence <= watermark
            select new { m, p };

        if (applyItemFilter && !string.IsNullOrWhiteSpace(query.ItemCode))
        {
            var item = query.ItemCode.Trim();
            q = q.Where(x => x.m.ItemCode == item);
        }
        if (applyItemFilter && !string.IsNullOrWhiteSpace(query.BaseUom))
        {
            var uom = query.BaseUom.Trim();
            q = q.Where(x => x.m.BaseUom == uom);
        }
        if (!string.IsNullOrWhiteSpace(query.WorkOrderNo))
        {
            var wo = query.WorkOrderNo.Trim();
            q = q.Where(x => x.m.WorkOrderNo == wo);
        }
        if (!string.IsNullOrWhiteSpace(query.BalanceStage))
        {
            var stage = query.BalanceStage.Trim();
            q = q.Where(x => x.m.BalanceStage == stage);
        }
        if (!string.IsNullOrWhiteSpace(query.ProductionLocationCode))
        {
            var loc = query.ProductionLocationCode.Trim();
            q = q.Where(x => x.m.ProductionLocationCode == loc);
        }
        if (!string.IsNullOrWhiteSpace(query.LotIdentity))
        {
            var lot = query.LotIdentity.Trim();
            q = q.Where(x => x.m.LotIdentity == lot);
        }
        if (!string.IsNullOrWhiteSpace(query.StockStatusCode))
        {
            var status = query.StockStatusCode.Trim();
            q = q.Where(x => x.m.StockStatusCode == status);
        }

        var raw = await q
            .OrderBy(x => x.p.EffectiveAt)
            .ThenBy(x => x.p.PostingSequence)
            .ThenBy(x => x.m.PostingLineNo)
            .ThenBy(x => x.m.Uid)
            .Select(x => new
            {
                x.m.Uid,
                x.p.EffectiveAt,
                x.p.PostedAtUtc,
                x.p.PostingSequence,
                PostingLineNo = x.m.PostingLineNo ?? 0,
                x.p.SourceDocumentType,
                x.p.SourceDocumentNo,
                ItemCode = x.m.ItemCode ?? string.Empty,
                BaseUom = x.m.BaseUom,
                x.m.BalanceStage,
                x.m.WorkOrderNo,
                x.m.ProcessCode,
                x.m.ProductionLocationCode,
                x.m.LotIdentity,
                x.m.StockStatusCode,
                x.m.MovementType,
                x.m.Qty,
                x.m.BaseQty,
                x.m.OriginalMovementId,
                x.m.ValuationStatus,
                x.m.ProductionBalLotId,
            })
            .ToListAsync(cancellationToken);

        return raw
            .Select(x => new SealedMovement(
                x.Uid,
                x.EffectiveAt,
                x.PostedAtUtc,
                x.PostingSequence,
                x.PostingLineNo,
                x.SourceDocumentType,
                x.SourceDocumentNo,
                x.ItemCode,
                x.BaseUom,
                x.BalanceStage,
                x.WorkOrderNo,
                x.ProcessCode,
                x.ProductionLocationCode,
                x.LotIdentity,
                x.StockStatusCode,
                x.MovementType,
                IvQty.Round(x.Qty),
                IvQty.Round(x.BaseQty),
                IvQty.Round(ProductionBalLotSignedQty.SignedBaseQty(x.MovementType, x.BaseQty)),
                x.OriginalMovementId,
                x.ValuationStatus,
                x.ProductionBalLotId))
            .ToList();
    }

    private static IReadOnlyList<ProductionStockHistoryRow> ApplyDocumentTypeFilter(
        IReadOnlyList<ProductionStockHistoryRow> rows, string? documentType)
    {
        if (string.IsNullOrWhiteSpace(documentType))
            return rows;

        var type = documentType.Trim();
        return rows
            .Where(x => string.Equals(x.SourceDocumentType, type, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static ProductionStockHistoryRow ToRow(SealedMovement movement, decimal running) => new()
    {
        EffectiveAt = movement.EffectiveAt,
        SourceDocumentType = movement.SourceDocumentType,
        SourceDocumentNo = movement.SourceDocumentNo,
        WorkOrderNo = movement.WorkOrderNo,
        ItemCode = movement.ItemCode,
        BalanceStage = movement.BalanceStage,
        ProductionLocationCode = movement.ProductionLocationCode,
        LotIdentity = movement.LotIdentity,
        StockStatusCode = movement.StockStatusCode,
        MovementType = movement.MovementType,
        InQty = movement.SignedBaseQty > 0m ? movement.SignedBaseQty : 0m,
        OutQty = movement.SignedBaseQty < 0m ? IvQty.Round(-movement.SignedBaseQty) : 0m,
        RunningQty = running,
        ValuationStatus = movement.ValuationStatus,
        MovementId = movement.MovementId,
        PostingSequence = movement.PostingSequence,
        PostingLineNo = movement.PostingLineNo,
        SignedBaseQty = movement.SignedBaseQty,
    };

    private static ProductionStockReconcileFinding Finding(
        string code,
        string severity,
        string message,
        ProductionBalLot lot,
        decimal liveQty,
        decimal? ledgerQty) =>
        new()
        {
            Code = code,
            Severity = severity,
            Message = message,
            ProductionBalLotId = lot.Uid,
            ItemCode = lot.ItemCode,
            WorkOrderNo = lot.WorkOrderNo,
            BalanceStage = lot.BalanceStage,
            ProductionLocationCode = lot.ProductionLocation?.Code,
            LotIdentity = lot.PoolCode ?? lot.LotNo,
            StockStatusCode = lot.StockStatusCode,
            LiveBaseQty = liveQty,
            LedgerBaseQty = ledgerQty,
            Delta = ledgerQty is decimal expected ? IvQty.Round(liveQty - expected) : liveQty,
        };

    private sealed record SealedMovement(
        long MovementId,
        DateTime EffectiveAt,
        DateTime PostedAtUtc,
        long PostingSequence,
        int PostingLineNo,
        string SourceDocumentType,
        string SourceDocumentNo,
        string ItemCode,
        string BaseUom,
        string? BalanceStage,
        string? WorkOrderNo,
        string? ProcessCode,
        string? ProductionLocationCode,
        string? LotIdentity,
        string? StockStatusCode,
        string MovementType,
        decimal Qty,
        decimal BaseQty,
        decimal SignedBaseQty,
        long? ReversesMovementId,
        string? ValuationStatus,
        long ProductionBalLotId);

    private sealed record StockIdentity(
        string ItemCode,
        string BaseUom,
        string? WorkOrderNo,
        string? BalanceStage,
        string? ProductionLocationCode,
        string? LotIdentity,
        string? StockStatusCode);
}
