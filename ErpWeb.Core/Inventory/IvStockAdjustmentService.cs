using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

public sealed class IvStockAdjustmentService : IIvStockAdjustmentService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IRunningNumberService _runningNumbers;
    private readonly IIvStockMasterRepository _stockMasters;
    private readonly IIvStockCommonRepository _common;
    private readonly IIvStockTransactionRepository _transactions;
    private readonly IIvStockPostingRepository _postingRepo;
    private readonly IIvInventoryPostingService _posting;
    private readonly ILogger<IvStockAdjustmentService> _logger;

    public IvStockAdjustmentService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IRunningNumberService runningNumbers,
        IIvStockMasterRepository stockMasters,
        IIvStockCommonRepository common,
        IIvStockTransactionRepository transactions,
        IIvStockPostingRepository postingRepo,
        IIvInventoryPostingService posting,
        ILogger<IvStockAdjustmentService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _runningNumbers = runningNumbers;
        _stockMasters = stockMasters;
        _common = common;
        _transactions = transactions;
        _postingRepo = postingRepo;
        _posting = posting;
        _logger = logger;
    }

    public async Task<IvStockAdjustmentOperationResult> PeekNextBatchNoAsync(
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvStockAdjustmentOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockAdjustment, PermissionCodes.Access, cancellationToken))
        {
            return IvStockAdjustmentOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var peek = await _runningNumbers.PeekNextAsync(db, context.CompanyCode!, RunningNumberKeys.IvBatch, cancellationToken);
        return IvStockAdjustmentOperationResult.OkPeek(peek);
    }

    public async Task<IvStockAdjustmentOperationResult> SearchAsync(
        IvStockAdjustmentListQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvStockAdjustmentOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockAdjustment, PermissionCodes.Access, cancellationToken))
        {
            return IvStockAdjustmentOperationResult.Fail("Not authorized.");
        }

        query ??= new IvStockAdjustmentListQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var args = new IvTrxBatchSearchArgs(
            TrxType: IvTrxTypes.StockAdjustment,
            SearchText: string.IsNullOrWhiteSpace(query.SearchText) ? null : query.SearchText.Trim(),
            BatchStatus: string.IsNullOrWhiteSpace(query.BatchStatus) ? null : query.BatchStatus.Trim(),
            DateFrom: query.DateFrom,
            DateTo: query.DateTo,
            SortField: query.SortField,
            SortDescending: query.SortDescending,
            Skip: query.Skip,
            Take: query.Take);

        var (rows, total) = await _transactions.SearchPagedAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            args,
            cancellationToken);

        return IvStockAdjustmentOperationResult.OkList(new IvStockAdjustmentListPage
        {
            Rows = rows.Select(x => new IvStockAdjustmentListRow
            {
                Id = x.Id,
                BatchNo = x.BatchNo,
                TrxDate = x.TrxDtTime,
                BatchStatus = x.BatchStatus,
                RefNo = x.RefNo,
                Remarks = x.Remarks,
                LineCount = x.LineCount,
                TotalAmount = decimal.Round(x.TotalAmount, 2),
                CreatedDate = x.CreatedDate,
                CreatedBy = x.CreatedBy
            }).ToList(),
            TotalCount = total
        });
    }

    public async Task<IvStockAdjustmentOperationResult> GetAsync(
        int batchNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvStockAdjustmentOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockAdjustment, PermissionCodes.Access, cancellationToken))
        {
            return IvStockAdjustmentOperationResult.Fail("Not authorized.");
        }

        if (batchNo <= 0)
        {
            return IvStockAdjustmentOperationResult.Fail("Batch number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await _transactions.GetByBatchNoAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            batchNo,
            cancellationToken);

        if (batch is null
            || !string.Equals(batch.TrxType, IvTrxTypes.StockAdjustment, StringComparison.OrdinalIgnoreCase))
        {
            return IvStockAdjustmentOperationResult.Fail("Stock adjustment was not found.");
        }

        var itemCodes = batch.Details
            .Select(d => (d.ICode ?? string.Empty).Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lotControlByCode = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in itemCodes)
        {
            var item = await _stockMasters.GetByCodeAsync(db, context.CompanyCode!, code, cancellationToken);
            lotControlByCode[code] = item?.LotControl ?? false;
        }

        var balLocIds = batch.Details
            .Select(IvStockAdjustmentLineInvariant.GetBalLocId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var currentQtyById = new Dictionary<int, decimal>();
        if (balLocIds.Count > 0)
        {
            var balRows = await db.IvBalLocs
                .AsNoTracking()
                .Where(x => balLocIds.Contains(x.Id))
                .Select(x => new { x.Id, x.StdQty })
                .ToListAsync(cancellationToken);
            currentQtyById = balRows.ToDictionary(x => x.Id, x => x.StdQty);
        }

        var lines = batch.Details
            .OrderBy(d => d.TrxLineNo)
            .Select(d =>
            {
                var code = (d.ICode ?? string.Empty).Trim();
                lotControlByCode.TryGetValue(code, out var lotControl);
                var balLocId = IvStockAdjustmentLineInvariant.GetBalLocId(d);
                var adjustQty = IvStockAdjustmentLineInvariant.GetSignedDelta(d);
                var currentQty = balLocId > 0 && currentQtyById.TryGetValue(balLocId, out var cq) ? cq : 0m;
                var (reason, remarks) = IvStockAdjustmentLineInvariant.ParseStoredRemarks(d.Remarks);
                var warehouse = d.ToWarehouse ?? d.FrWarehouse ?? string.Empty;
                var location = d.ToLocation ?? d.FrLocation;
                var lotNo = d.ToLotNo ?? d.FrLotNo;

                return new IvStockAdjustmentLineDto
                {
                    LineNo = d.TrxLineNo,
                    BalLocId = balLocId,
                    ICode = code,
                    IDesc = d.IDesc,
                    Warehouse = warehouse,
                    Location = location,
                    LotNo = lotNo,
                    AdjustQty = adjustQty,
                    CurrentQty = currentQty,
                    NewQty = currentQty + adjustQty,
                    Uom = d.ToStdUom ?? d.FrStdUom,
                    IClassCode = d.IClassCode,
                    IStatus = string.IsNullOrWhiteSpace(d.IStatus) ? IvItemStatuses.Active : d.IStatus,
                    UnitPrice = d.UnitPrice ?? 0m,
                    ExpiryDate = d.ExpiryDate,
                    Reason = reason,
                    Remarks = remarks,
                    LotControl = lotControl
                };
            })
            .ToList();

        return IvStockAdjustmentOperationResult.OkDocument(new IvStockAdjustmentDocument
        {
            Id = batch.Id,
            BatchNo = batch.BatchNo,
            TrxDate = batch.TrxDtTime,
            BatchStatus = batch.BatchStatus,
            RefNo = batch.RefNo,
            Remark = batch.Remarks,
            Lines = lines
        });
    }

    public async Task<IvStockAdjustmentOperationResult> SaveNewAsync(
        IvStockAdjustmentSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return IvStockAdjustmentOperationResult.Fail("Save request is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvStockAdjustmentOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockAdjustment, PermissionCodes.Add, cancellationToken))
        {
            return IvStockAdjustmentOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var validatedResult = await ValidateLinesAsync(
            db,
            request.Lines,
            context.CompanyCode!,
            context.BranchCode!,
            cancellationToken);
        if (validatedResult.ErrorMessage is not null)
        {
            return IvStockAdjustmentOperationResult.Fail(validatedResult.ErrorMessage);
        }

        var batchNo = await _runningNumbers.GetNextAsync(
            db,
            context.CompanyCode!,
            RunningNumberKeys.IvBatch,
            cancellationToken);

        var now = DateTime.UtcNow;
        var userId = Truncate(context.UserId!, 10);
        var refNo = NormalizeRefNo(request.RefNo, batchNo);
        var trxDate = request.TrxDate == default ? DateTime.Today : request.TrxDate.Date;

        var batch = new IvTrxBatch
        {
            CompanyCode = context.CompanyCode!,
            BranchCode = context.BranchCode!,
            BatchNo = batchNo,
            TrxDtTime = trxDate,
            TrxType = IvTrxTypes.StockAdjustment,
            BatchStatus = IvBatchStatuses.New,
            RefNo = refNo,
            Remarks = TruncateOptional(request.Remark, 250),
            LocationCode = context.LocationCode,
            CreatedDate = now,
            CreatedBy = userId
        };

        AddDetails(batch, validatedResult.Lines!, context.CompanyCode!, context.BranchCode!, context.LocationCode, batchNo);

        await _transactions.InsertAsync(db, batch, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Stock adjustment saved. UserId={UserId} CompanyCode={CompanyCode} BranchCode={BranchCode} BatchNo={BatchNo} Lines={LineCount}",
            context.UserId,
            context.CompanyCode,
            context.BranchCode,
            batchNo,
            batch.Details.Count);

        return IvStockAdjustmentOperationResult.OkSaved(batch.Id, batchNo);
    }

    public async Task<IvStockAdjustmentOperationResult> UpdateAsync(
        int batchNo,
        IvStockAdjustmentSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return IvStockAdjustmentOperationResult.Fail("Save request is required.");
        }

        if (batchNo <= 0)
        {
            return IvStockAdjustmentOperationResult.Fail("Batch number is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvStockAdjustmentOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockAdjustment, PermissionCodes.Edit, cancellationToken))
        {
            return IvStockAdjustmentOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var batch = await _postingRepo.LockBatchForUpdateAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            batchNo,
            cancellationToken);

        if (batch is null
            || !string.Equals(batch.TrxType, IvTrxTypes.StockAdjustment, StringComparison.OrdinalIgnoreCase))
        {
            return IvStockAdjustmentOperationResult.Fail("Stock adjustment was not found.");
        }

        if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
        {
            return IvStockAdjustmentOperationResult.Fail("Only NEW stock adjustments can be edited.");
        }

        var existingDetails = await _postingRepo.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);

        var validatedResult = await ValidateLinesAsync(
            db,
            request.Lines,
            context.CompanyCode!,
            context.BranchCode!,
            cancellationToken);
        if (validatedResult.ErrorMessage is not null)
        {
            return IvStockAdjustmentOperationResult.Fail(validatedResult.ErrorMessage);
        }

        var now = DateTime.UtcNow;
        var userId = Truncate(context.UserId!, 10);
        batch.TrxDtTime = request.TrxDate == default ? DateTime.Today : request.TrxDate.Date;
        batch.RefNo = NormalizeRefNo(request.RefNo, batch.BatchNo);
        batch.Remarks = TruncateOptional(request.Remark, 250);
        batch.ModifiedDate = now;
        batch.ModifiedBy = userId;

        db.IvTrxBatchDetails.RemoveRange(existingDetails);
        batch.Details.Clear();
        AddDetails(batch, validatedResult.Lines!, context.CompanyCode!, context.BranchCode!, context.LocationCode, batch.BatchNo);

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Stock adjustment updated. UserId={UserId} CompanyCode={CompanyCode} BranchCode={BranchCode} BatchNo={BatchNo} Lines={LineCount}",
            context.UserId,
            context.CompanyCode,
            context.BranchCode,
            batch.BatchNo,
            batch.Details.Count);

        return IvStockAdjustmentOperationResult.OkSaved(batch.Id, batch.BatchNo);
    }

    public async Task<IvStockAdjustmentOperationResult> DeleteAsync(
        IReadOnlyList<int>? batchNos,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvStockAdjustmentOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockAdjustment, PermissionCodes.Delete, cancellationToken))
        {
            return IvStockAdjustmentOperationResult.Fail("Not authorized.");
        }

        var nos = (batchNos ?? Array.Empty<int>())
            .Where(n => n > 0)
            .Distinct()
            .ToList();

        if (nos.Count == 0)
        {
            return IvStockAdjustmentOperationResult.Fail("Select at least one stock adjustment.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var no in nos)
        {
            var batch = await _postingRepo.LockBatchForUpdateAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                no,
                cancellationToken);

            if (batch is null
                || !string.Equals(batch.TrxType, IvTrxTypes.StockAdjustment, StringComparison.OrdinalIgnoreCase))
            {
                return IvStockAdjustmentOperationResult.Fail($"Stock adjustment {no} was not found.");
            }

            if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                return IvStockAdjustmentOperationResult.Fail(
                    $"Stock adjustment {no} cannot be deleted because it is not NEW.");
            }

            var details = await _postingRepo.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
            db.IvTrxBatchDetails.RemoveRange(details);
            db.IvTrxBatches.Remove(batch);
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        return IvStockAdjustmentOperationResult.Ok();
    }

    public async Task<IvStockAdjustmentOperationResult> CancelAsync(
        IReadOnlyList<int>? batchNos,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvStockAdjustmentOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockAdjustment, PermissionCodes.Cancel, cancellationToken))
        {
            return IvStockAdjustmentOperationResult.Fail("Not authorized.");
        }

        var nos = (batchNos ?? Array.Empty<int>())
            .Where(n => n > 0)
            .Distinct()
            .ToList();

        if (nos.Count == 0)
        {
            return IvStockAdjustmentOperationResult.Fail("Select at least one stock adjustment.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var no in nos)
        {
            var batch = await _postingRepo.LockBatchForUpdateAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                no,
                cancellationToken);

            if (batch is null
                || !string.Equals(batch.TrxType, IvTrxTypes.StockAdjustment, StringComparison.OrdinalIgnoreCase))
            {
                return IvStockAdjustmentOperationResult.Fail($"Stock adjustment {no} was not found.");
            }

            if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                return IvStockAdjustmentOperationResult.Fail(
                    $"Stock adjustment {no} cannot be cancelled because it is not NEW.");
            }

            batch.BatchStatus = IvBatchStatuses.Cancelled;
            batch.ModifiedDate = DateTime.UtcNow;
            batch.ModifiedBy = Truncate(context.UserId!, 10);
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        return IvStockAdjustmentOperationResult.Ok();
    }

    public async Task<IvStockAdjustmentOperationResult> PostAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default)
    {
        var posting = await _posting.PostAsync(IvTrxTypes.StockAdjustment, batchNos, cancellationToken);
        return IvStockAdjustmentOperationResult.OkPosting(posting);
    }

    public async Task<IvStockAdjustmentOperationResult> RollbackAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default)
    {
        var posting = await _posting.RollbackAsync(IvTrxTypes.StockAdjustment, batchNos, cancellationToken);
        return IvStockAdjustmentOperationResult.OkPosting(posting);
    }

    private async Task<(string? ErrorMessage, List<ValidatedLine>? Lines)> ValidateLinesAsync(
        AppDbContext db,
        IReadOnlyList<IvStockAdjustmentLineRequest>? lines,
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken)
    {
        if (lines is null || lines.Count == 0)
        {
            return ("Add at least one adjustment line.", null);
        }

        if (lines.Count > short.MaxValue)
        {
            return ("Too many adjustment lines.", null);
        }

        var validated = new List<ValidatedLine>();
        short lineNo = 1;
        foreach (var line in lines)
        {
            if (line is null)
            {
                return ($"Line {lineNo}: line data is required.", null);
            }

            var error = await ValidateLineAsync(
                db,
                line,
                companyCode,
                branchCode,
                lineNo,
                cancellationToken);
            if (error.ErrorMessage is not null)
            {
                return (error.ErrorMessage, null);
            }

            validated.Add(error.Line!);
            lineNo++;
        }

        var netByBalLoc = new Dictionary<int, decimal>();
        var onHandByBalLoc = new Dictionary<int, decimal>();
        foreach (var row in validated)
        {
            netByBalLoc[row.BalLocId] = netByBalLoc.GetValueOrDefault(row.BalLocId) + row.AdjustQty;
            onHandByBalLoc[row.BalLocId] = row.OnHandQty;
        }

        foreach (var (balLocId, net) in netByBalLoc)
        {
            if (net < 0m && Math.Abs(net) > onHandByBalLoc[balLocId])
            {
                return (
                    $"Insufficient quantity on balance Id {balLocId} (on hand {onHandByBalLoc[balLocId]}, required decrease {Math.Abs(net)}).",
                    null);
            }
        }

        return (null, validated);
    }

    private static void AddDetails(
        IvTrxBatch batch,
        IReadOnlyList<ValidatedLine> validated,
        string companyCode,
        string branchCode,
        string? locationCode,
        int batchNo)
    {
        short trxLineNo = 1;
        foreach (var row in validated)
        {
            var detail = new IvTrxBatchDetail
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                BatchNo = batchNo,
                TrxLineNo = trxLineNo,
                TrxType = IvTrxTypes.StockAdjustment,
                ICode = row.Item.ICode,
                IDesc = TruncateOptional(row.IDesc, 200),
                ProdCode = row.Item.ICode,
                ProdDesc = TruncateOptional(row.IDesc, 200),
                IStatus = row.IStatus,
                IClassCode = row.IClassCode,
                ExpiryDate = row.ExpiryDate,
                UnitPrice = IvQty.Round(row.UnitPrice),
                Remarks = row.Remarks,
                LocationCode = NullIfWhiteSpace(locationCode)
            };

            if (row.AdjustQty > 0m)
            {
                detail.ToBalLocId = row.BalLocId;
                detail.ToWarehouse = row.Warehouse;
                detail.ToLocation = row.Location;
                detail.ToLotNo = row.LotNo;
                detail.ToStdQty = IvQty.Round(row.AdjustQty);
                detail.ToStdUom = row.Uom;
            }
            else
            {
                detail.FromBalLocId = row.BalLocId;
                detail.FrWarehouse = row.Warehouse;
                detail.FrLocation = row.Location;
                detail.FrLotNo = row.LotNo;
                detail.FrStdQty = IvQty.Round(Math.Abs(row.AdjustQty));
                detail.FrStdUom = row.Uom;
            }

            batch.Details.Add(detail);
            trxLineNo++;
        }
    }

    private async Task<(string? ErrorMessage, ValidatedLine? Line)> ValidateLineAsync(
        AppDbContext db,
        IvStockAdjustmentLineRequest line,
        string companyCode,
        string branchCode,
        short lineNo,
        CancellationToken cancellationToken)
    {
        var iCode = (line.ICode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(iCode))
        {
            return ($"Line {lineNo}: item code is required.", null);
        }

        var item = await _stockMasters.GetByCodeAsync(db, companyCode, iCode, cancellationToken);
        if (item is null || !item.IsActive)
        {
            return ($"Line {lineNo}: item '{iCode}' was not found.", null);
        }

        if (!item.StockControl)
        {
            return ($"Line {lineNo}: item '{iCode}' is not stock-controlled and cannot be adjusted.", null);
        }

        if (line.BalLocId <= 0)
        {
            return ($"Line {lineNo}: balance location is required.", null);
        }

        var adjustQty = IvQty.Round(line.AdjustQty);
        if (adjustQty == 0m)
        {
            return ($"Line {lineNo}: adjust quantity cannot be zero.", null);
        }

        var bal = await db.IvBalLocs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == line.BalLocId && x.CompanyCode == companyCode && x.BranchCode == branchCode,
                cancellationToken);

        if (bal is null)
        {
            return ($"Line {lineNo}: balance record was not found.", null);
        }

        if (!string.Equals(bal.ICode, iCode, StringComparison.OrdinalIgnoreCase))
        {
            return ($"Line {lineNo}: balance record does not belong to item '{iCode}'.", null);
        }

        var warehouse = (line.Warehouse ?? string.Empty).Trim();
        var balWarehouse = (bal.WhCode ?? string.Empty).Trim();
        if (!string.Equals(warehouse, balWarehouse, StringComparison.OrdinalIgnoreCase))
        {
            return ($"Line {lineNo}: warehouse does not match the selected balance record.", null);
        }

        if (string.IsNullOrWhiteSpace(warehouse))
        {
            return ($"Line {lineNo}: warehouse is required.", null);
        }

        var warehouseRow = await _common.GetActiveWarehouseAsync(
            db, companyCode, branchCode, warehouse, cancellationToken);
        if (warehouseRow is null)
        {
            return ($"Line {lineNo}: warehouse '{warehouse}' was not found for this branch.", null);
        }

        var location = (line.Location ?? string.Empty).Trim();
        var balLocation = (bal.LocCode ?? string.Empty).Trim();
        if (!string.Equals(location, balLocation, StringComparison.OrdinalIgnoreCase))
        {
            return ($"Line {lineNo}: location does not match the selected balance record.", null);
        }

        var iStatus = string.IsNullOrWhiteSpace(line.IStatus)
            ? string.Empty
            : line.IStatus.Trim().ToUpperInvariant();
        var balStatus = (bal.IStatus ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.Equals(iStatus, balStatus, StringComparison.OrdinalIgnoreCase))
        {
            return ($"Line {lineNo}: item status does not match the selected balance record.", null);
        }

        if (string.IsNullOrWhiteSpace(iStatus))
        {
            return ($"Line {lineNo}: item status is required.", null);
        }

        var statusRow = await _common.GetActiveStatusAsync(db, companyCode, iStatus, cancellationToken);
        if (statusRow is null)
        {
            return ($"Line {lineNo}: item status '{iStatus}' was not found.", null);
        }

        var lotNo = (line.LotNo ?? string.Empty).Trim();
        var balLotNo = (bal.LotNo ?? string.Empty).Trim();
        DateTime? expiry = line.ExpiryDate?.Date;

        if (item.LotControl)
        {
            if (string.IsNullOrWhiteSpace(lotNo))
            {
                return ($"Line {lineNo}: lot number is required for lot-controlled item '{item.ICode}'.", null);
            }

            if (!string.Equals(lotNo, balLotNo, StringComparison.OrdinalIgnoreCase))
            {
                return ($"Line {lineNo}: lot number does not match the selected balance record.", null);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(lotNo))
            {
                return ($"Line {lineNo}: lot number is not allowed for non-lot-controlled item '{item.ICode}'.", null);
            }

            lotNo = string.Empty;
            expiry = null;
        }

        var uom = (bal.StdUom ?? item.StdUom ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(uom))
        {
            return ($"Line {lineNo}: UOM is required.", null);
        }

        var iClassCode = (item.IClassCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(iClassCode))
        {
            return ($"Line {lineNo}: item class is required.", null);
        }

        var classRow = await _common.GetActiveClassAsync(db, companyCode, iClassCode, cancellationToken);
        if (classRow is null)
        {
            return ($"Line {lineNo}: item class '{iClassCode}' was not found.", null);
        }

        if (adjustQty < 0m && Math.Abs(adjustQty) > bal.StdQty)
        {
            return (
                $"Line {lineNo}: adjust quantity would make new quantity negative (on hand {bal.StdQty}, adjust {adjustQty}).",
                null);
        }

        if (line.UnitPrice < 0)
        {
            return ($"Line {lineNo}: unit price cannot be negative.", null);
        }

        var reasonError = IvStockAdjustmentLineInvariant.ValidateReasonCode(line.Reason, lineNo);
        if (reasonError is not null)
        {
            return (reasonError, null);
        }

        var canonicalReason = IvStockAdjustmentLineInvariant.NormalizeReasonCode(line.Reason)!;
        if (string.Equals(canonicalReason, IvAdjustmentReasons.Other, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(line.Remarks))
        {
            return ($"Line {lineNo}: remark is required when reason is OTHER.", null);
        }

        var desc = string.IsNullOrWhiteSpace(line.IDesc) ? item.IDesc : line.IDesc.Trim();
        var unitPrice = line.UnitPrice;
        if (unitPrice == 0m)
        {
            unitPrice = bal.UnitPrice ?? item.PurchasePrice ?? 0m;
        }

        var remarks = IvStockAdjustmentLineInvariant.CombineRemarks(canonicalReason, line.Remarks);

        return (null, new ValidatedLine(
            item,
            desc,
            line.BalLocId,
            warehouse,
            location,
            lotNo,
            adjustQty,
            bal.StdQty,
            uom,
            iStatus,
            iClassCode,
            expiry,
            unitPrice,
            remarks));
    }

    private static string? NormalizeRefNo(string? refNo, int batchNo)
    {
        var value = (refNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            value = batchNo.ToString();
        }

        return TruncateOptional(value, 50);
    }

    private UserContext ValidateUserContext()
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return UserContext.Fail("Invalid company or branch context.");
        }

        return UserContext.Ok(scope.CompanyCode, scope.BranchCode, scope.LocationCode, scope.UserId);
    }

    private UserContext ValidateWriteContext()
    {
        var scope = _tenant.TryWriteScope();
        if (scope is null)
        {
            return UserContext.Fail("Invalid company, branch, or location context.");
        }

        return UserContext.Ok(scope.CompanyCode, scope.BranchCode!, scope.LocationCode!, scope.UserId);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string? TruncateOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ValidatedLine(
        IvStockMaster Item,
        string? IDesc,
        int BalLocId,
        string Warehouse,
        string Location,
        string LotNo,
        decimal AdjustQty,
        decimal OnHandQty,
        string Uom,
        string IStatus,
        string IClassCode,
        DateTime? ExpiryDate,
        decimal UnitPrice,
        string? Remarks);

    private readonly record struct UserContext(
        string? CompanyCode,
        string? BranchCode,
        string? LocationCode,
        string? UserId,
        string? Error)
    {
        public static UserContext Ok(string companyCode, string branchCode, string? locationCode, string userId) =>
            new(companyCode, branchCode, locationCode, userId, null);

        public static UserContext Fail(string error) =>
            new(null, null, null, null, error);
    }
}
