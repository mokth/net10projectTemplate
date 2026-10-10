using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

public sealed class SaPriceChangeHistoryService : ISaPriceChangeHistoryService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ITenantScopeContext _tenant;
    private readonly IAccessRightService _accessRights;

    public SaPriceChangeHistoryService(
        IDbContextFactory<AppDbContext> dbFactory,
        ITenantScopeContext tenant,
        IAccessRightService accessRights)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
    }

    public async Task<IvMasterOperationResult<SaPriceChangeHistoryPage>> SearchAsync(
        SaPriceChangeHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await CheckReadAsync(cancellationToken);
        if (gate.ErrorCode is not null)
        {
            return FailPage(gate.ErrorCode.Value, gate.Message!);
        }

        query ??= new SaPriceChangeHistoryQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var source = ApplyFilters(db, gate.Scope!.CompanyCode, query);
        var total = await source.CountAsync(cancellationToken);
        var rows = await source
            .OrderByDescending(x => x.Batch.ChangedAtUtc)
            .ThenByDescending(x => x.Batch.PriceChangeBatchId)
            .ThenByDescending(x => x.Line.PriceChangeLineId)
            .Skip(Math.Max(0, query.Skip))
            .Take(NormalizeTake(query.Take))
            .Select(x => new HistoryProjection
            {
                Batch = x.Batch,
                Line = x.Line
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<SaPriceChangeHistoryPage>.Ok(new SaPriceChangeHistoryPage
        {
            TotalCount = total,
            Rows = rows.Select(Map).ToList()
        });
    }

    public async Task<IvMasterOperationResult<SaPriceChangeHistoryBatch>> GetBatchAsync(
        long priceChangeBatchId,
        int skip = 0,
        int take = SaPriceChangeHistoryLimits.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var gate = await CheckReadAsync(cancellationToken);
        if (gate.ErrorCode is not null)
        {
            return FailBatch(gate.ErrorCode.Value, gate.Message!);
        }

        if (priceChangeBatchId <= 0)
        {
            return FailBatch(IvMasterErrorCode.Validation, "A valid price change batch is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.SaPriceChangeBatches.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == gate.Scope!.CompanyCode && x.PriceChangeBatchId == priceChangeBatchId,
                cancellationToken);
        if (batch is null)
        {
            return FailBatch(IvMasterErrorCode.NotFound, "Price change batch not found.");
        }

        var lineSource = from line in db.SaPriceChangeLines.AsNoTracking()
                         join header in db.SaPriceChangeBatches.AsNoTracking()
                             on line.PriceChangeBatchId equals header.PriceChangeBatchId
                         where header.CompanyCode == gate.Scope!.CompanyCode
                               && header.PriceChangeBatchId == priceChangeBatchId
                         select new HistoryProjection
                         {
                             Batch = header,
                             Line = line
                         };
        var total = await lineSource.CountAsync(cancellationToken);
        var lines = await lineSource
            .OrderBy(x => x.Line.PriceChangeLineId)
            .Skip(Math.Max(0, skip))
            .Take(NormalizeTake(take))
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<SaPriceChangeHistoryBatch>.Ok(new SaPriceChangeHistoryBatch
        {
            PriceChangeBatchId = batch.PriceChangeBatchId,
            BatchReference = BatchReference(batch.PriceChangeBatchId),
            ChangedAtUtc = batch.ChangedAtUtc,
            EffectiveDate = batch.EffectiveDate,
            Origin = batch.Origin,
            TargetType = batch.TargetType,
            AdjustmentMethod = batch.AdjustmentMethod,
            AdjustmentValue = batch.AdjustmentValue,
            RoundingMode = batch.RoundingMode,
            DecimalPlaces = batch.DecimalPlaces,
            Reason = batch.Reason,
            ItemSearchFilter = batch.ItemSearchFilter,
            ItemTypeFilter = batch.ItemTypeFilter,
            ItemClassFilter = batch.ItemClassFilter,
            ItemSubClassFilter = batch.ItemSubClassFilter,
            BrandFilter = batch.BrandFilter,
            CustCodeFilter = batch.CustCodeFilter,
            CustTypeFilter = batch.CustTypeFilter,
            CustGroupFilter = batch.CustGroupFilter,
            CustPriceCodeFilter = batch.CustPriceCodeFilter,
            CurrencyFilter = batch.CurrencyFilter,
            UomFilter = batch.UomFilter,
            ChangedRowCount = batch.ChangedRowCount,
            ChangedBy = batch.ChangedBy,
            TotalLineCount = total,
            Lines = lines.Select(Map).ToList()
        });
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<SaPriceChangeHistoryRow>>> ExportAsync(
        SaPriceChangeHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await CheckExportAsync(cancellationToken);
        if (gate.ErrorCode is not null)
        {
            return IvMasterOperationResult<IReadOnlyList<SaPriceChangeHistoryRow>>.Fail(
                gate.ErrorCode.Value,
                gate.Message!);
        }

        query ??= new SaPriceChangeHistoryQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var source = ApplyFilters(db, gate.Scope!.CompanyCode, query);
        var total = await source.CountAsync(cancellationToken);
        if (total > SaPriceChangeHistoryLimits.MaxExportRows)
        {
            return IvMasterOperationResult<IReadOnlyList<SaPriceChangeHistoryRow>>.Fail(
                IvMasterErrorCode.Validation,
                $"Export is limited to {SaPriceChangeHistoryLimits.MaxExportRows:N0} rows. Refine the filters first.");
        }

        var rows = await source
            .OrderByDescending(x => x.Batch.ChangedAtUtc)
            .ThenByDescending(x => x.Batch.PriceChangeBatchId)
            .ThenByDescending(x => x.Line.PriceChangeLineId)
            .Select(x => new HistoryProjection
            {
                Batch = x.Batch,
                Line = x.Line
            })
            .ToListAsync(cancellationToken);
        return IvMasterOperationResult<IReadOnlyList<SaPriceChangeHistoryRow>>.Ok(rows.Select(Map).ToList());
    }

    private async Task<ScopeCheck> CheckReadAsync(CancellationToken cancellationToken)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return ScopeCheck.Fail(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        if (!await _accessRights.CanAsync(MenuCodes.SalesPriceChangeHistory, PermissionCodes.Access, cancellationToken)
            || !await _accessRights.CanAsync(MenuCodes.SalesPriceChangeHistory, PermissionCodes.ViewPrice, cancellationToken))
        {
            return ScopeCheck.Fail(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        return ScopeCheck.Ok(scope);
    }

    private async Task<ScopeCheck> CheckExportAsync(CancellationToken cancellationToken)
    {
        var read = await CheckReadAsync(cancellationToken);
        if (read.ErrorCode is not null)
        {
            return read;
        }

        if (!await _accessRights.CanAsync(MenuCodes.SalesPriceChangeHistory, PermissionCodes.Export, cancellationToken))
        {
            return ScopeCheck.Fail(IvMasterErrorCode.AccessDenied, "Export permission is required.");
        }

        return read;
    }

    private static IQueryable<HistoryProjection> ApplyFilters(
        AppDbContext db,
        string company,
        SaPriceChangeHistoryQuery query)
    {
        var source = from batch in db.SaPriceChangeBatches.AsNoTracking()
                     join line in db.SaPriceChangeLines.AsNoTracking()
                         on batch.PriceChangeBatchId equals line.PriceChangeBatchId
                     where batch.CompanyCode == company
                     select new HistoryProjection
                     {
                         Batch = batch,
                         Line = line
                     };

        if (query.ChangedDateFromUtc.HasValue)
        {
            source = source.Where(x => x.Batch.ChangedAtUtc >= query.ChangedDateFromUtc.Value);
        }

        if (query.ChangedDateToUtc.HasValue)
        {
            var toExclusive = query.ChangedDateToUtc.Value.Date.AddDays(1);
            source = source.Where(x => x.Batch.ChangedAtUtc < toExclusive);
        }

        if (query.EffectiveDateFrom.HasValue)
        {
            source = source.Where(x => x.Batch.EffectiveDate >= query.EffectiveDateFrom.Value.Date);
        }

        if (query.EffectiveDateTo.HasValue)
        {
            source = source.Where(x => x.Batch.EffectiveDate <= query.EffectiveDateTo.Value.Date);
        }

        source = WhereEquals(source, query.Origin, x => x.Batch.Origin);
        source = WhereEquals(source, query.TargetType, x => x.Batch.TargetType);
        source = WhereEquals(source, query.ChangeKind, x => x.Line.ChangeKind);
        source = WhereEquals(source, query.ItemCode, x => x.Line.ItemCode);
        source = WhereEquals(source, query.ItemType, x => x.Line.ItemTypeSnapshot);
        source = WhereEquals(source, query.ItemClass, x => x.Line.ItemClassSnapshot);
        source = WhereEquals(source, query.ItemSubClass, x => x.Line.ItemSubClassSnapshot);
        source = WhereEquals(source, query.Brand, x => x.Line.BrandSnapshot);
        source = WhereEquals(source, query.CustCode, x => x.Line.CustCode);
        source = WhereEquals(source, query.CustType, x => x.Line.CustomerTypeSnapshot);
        source = WhereEquals(source, query.CustGroup, x => x.Line.CustomerGroupSnapshot);
        source = WhereEquals(source, query.CustPriceCode, x => x.Line.CustPriceCode);
        source = WhereEquals(source, query.ChangedBy, x => x.Batch.ChangedBy);

        if (!string.IsNullOrWhiteSpace(query.ReasonSearch))
        {
            var search = query.ReasonSearch.Trim();
            source = source.Where(x => x.Batch.Reason != null && x.Batch.Reason.Contains(search));
        }

        return source;
    }

    private static IQueryable<HistoryProjection> WhereEquals(
        IQueryable<HistoryProjection> source,
        string? value,
        System.Linq.Expressions.Expression<Func<HistoryProjection, string?>> selector)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return source;
        }

        var normalized = value.Trim();
        return source.Where(BuildEquals(selector, normalized));
    }

    private static System.Linq.Expressions.Expression<Func<HistoryProjection, bool>> BuildEquals(
        System.Linq.Expressions.Expression<Func<HistoryProjection, string?>> selector,
        string value)
    {
        var parameter = selector.Parameters[0];
        var body = System.Linq.Expressions.Expression.Equal(
            selector.Body,
            System.Linq.Expressions.Expression.Constant(value, typeof(string)));
        return System.Linq.Expressions.Expression.Lambda<Func<HistoryProjection, bool>>(body, parameter);
    }

    private static SaPriceChangeHistoryRow Map(HistoryProjection projection)
    {
        var batch = projection.Batch;
        var line = projection.Line;
        var difference = line.OldPrice.HasValue && line.NewPrice.HasValue
            ? (decimal?)(line.NewPrice.Value - line.OldPrice.Value)
            : null;
        var percent = line.OldPrice is null or 0m || !line.NewPrice.HasValue
            ? (decimal?)null
            : (decimal?)((line.NewPrice.Value - line.OldPrice.Value) / line.OldPrice.Value * 100m);

        return new SaPriceChangeHistoryRow
        {
            PriceChangeLineId = line.PriceChangeLineId,
            PriceChangeBatchId = batch.PriceChangeBatchId,
            BatchReference = BatchReference(batch.PriceChangeBatchId),
            ChangedAtUtc = batch.ChangedAtUtc,
            EffectiveDate = batch.EffectiveDate,
            Origin = batch.Origin,
            TargetType = batch.TargetType,
            ChangeKind = line.ChangeKind,
            ItemCode = line.ItemCode,
            ItemDescription = line.ItemDescriptionSnapshot,
            ItemType = line.ItemTypeSnapshot,
            ItemClass = line.ItemClassSnapshot,
            ItemSubClass = line.ItemSubClassSnapshot,
            Brand = line.BrandSnapshot,
            Uom = line.NewUom ?? line.OldUom,
            CustCode = line.CustCode,
            CustomerName = line.CustomerNameSnapshot,
            CustomerType = line.CustomerTypeSnapshot,
            CustomerGroup = line.CustomerGroupSnapshot,
            CustPriceCode = line.CustPriceCode,
            PriceListDescription = line.PriceListDescriptionSnapshot,
            SourcePriceListLineId = line.SourcePriceListLineId,
            Moq = line.Moq,
            CurrencyCode = line.NewCurrencyCode ?? line.OldCurrencyCode,
            OldPrice = line.OldPrice,
            NewPrice = line.NewPrice,
            DifferenceAmount = difference,
            DifferencePercent = percent,
            Reason = batch.Reason,
            ChangedBy = batch.ChangedBy,
            AdjustmentMethod = batch.AdjustmentMethod,
            AdjustmentValue = batch.AdjustmentValue,
            RoundingMode = batch.RoundingMode,
            DecimalPlaces = batch.DecimalPlaces
        };
    }

    private static int NormalizeTake(int take) =>
        take <= 0 ? SaPriceChangeHistoryLimits.DefaultPageSize : Math.Min(take, SaPriceChangeHistoryLimits.MaxPageSize);

    private static string BatchReference(long id) => $"PRC-{id:00000000}";

    private static IvMasterOperationResult<SaPriceChangeHistoryPage> FailPage(
        IvMasterErrorCode code,
        string message) =>
        IvMasterOperationResult<SaPriceChangeHistoryPage>.Fail(code, message);

    private static IvMasterOperationResult<SaPriceChangeHistoryBatch> FailBatch(
        IvMasterErrorCode code,
        string message) =>
        IvMasterOperationResult<SaPriceChangeHistoryBatch>.Fail(code, message);

    private sealed class HistoryProjection
    {
        public SaPriceChangeBatch Batch { get; init; } = null!;
        public SaPriceChangeLine Line { get; init; } = null!;
    }

    private sealed record ScopeCheck(
        TenantScope? Scope,
        IvMasterErrorCode? ErrorCode,
        string? Message)
    {
        public static ScopeCheck Ok(TenantScope scope) => new(scope, null, null);
        public static ScopeCheck Fail(IvMasterErrorCode code, string message) => new(null, code, message);
    }
}
