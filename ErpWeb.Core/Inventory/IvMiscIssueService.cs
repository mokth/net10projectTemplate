using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

public sealed class IvMiscIssueService : IIvMiscIssueService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IRunningNumberService _runningNumbers;
    private readonly ICurrentDateService _dates;
    private readonly IIvStockMasterRepository _stockMasters;
    private readonly IIvStockCommonRepository _common;
    private readonly IIvStockTransactionRepository _transactions;
    private readonly IIvStockPostingRepository _postingRepo;
    private readonly IIvInventoryPostingService _posting;
    private readonly ILogger<IvMiscIssueService> _logger;

    public IvMiscIssueService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IRunningNumberService runningNumbers,
        ICurrentDateService dates,
        IIvStockMasterRepository stockMasters,
        IIvStockCommonRepository common,
        IIvStockTransactionRepository transactions,
        IIvStockPostingRepository postingRepo,
        IIvInventoryPostingService posting,
        ILogger<IvMiscIssueService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _runningNumbers = runningNumbers;
        _dates = dates;
        _stockMasters = stockMasters;
        _common = common;
        _transactions = transactions;
        _postingRepo = postingRepo;
        _posting = posting;
        _logger = logger;
    }

    public async Task<IvMiscIssueOperationResult> PeekNextBatchNoAsync(
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvMiscIssueOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryMiscIssue, PermissionCodes.Access, cancellationToken))
        {
            return IvMiscIssueOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var peek = await _runningNumbers.PeekNextAsync(db, context.CompanyCode!, RunningNumberKeys.IvBatch, cancellationToken);
        return IvMiscIssueOperationResult.OkPeek(peek);
    }

    public async Task<IvMiscIssueOperationResult> GetLookupsAsync(
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvMiscIssueOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryMiscIssue, PermissionCodes.Access, cancellationToken))
        {
            return IvMiscIssueOperationResult.Fail("Not authorized.");
        }

        var items = await _stockMasters.ListActiveForLookupAsync(context.CompanyCode!, cancellationToken);
        var warehouses = await _common.ListActiveWarehousesAsync(
            context.CompanyCode!,
            context.BranchCode!,
            cancellationToken);

        return IvMiscIssueOperationResult.OkLookups(
            items.Select(x => new IvStockLookupRow
            {
                ICode = x.ICode,
                IDesc = x.IDesc,
                IClassCode = x.IClassCode,
                StdUom = x.StdUom,
                DefWarehouse = x.DefWarehouse,
                DefLocation = x.DefLocation,
                LotControl = x.LotControl,
                PurchasePrice = x.PurchasePrice
            }).ToList(),
            warehouses.Select(x => new IvWarehouseLookupRow
            {
                WarehouseCode = x.WarehouseCode,
                WarehouseDesc = x.WarehouseDesc
            }).ToList());
    }

    public async Task<IvMiscIssueOperationResult> SearchAsync(
        IvMiscIssueListQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvMiscIssueOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryMiscIssue, PermissionCodes.Access, cancellationToken))
        {
            return IvMiscIssueOperationResult.Fail("Not authorized.");
        }

        query ??= new IvMiscIssueListQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var args = new IvTrxBatchSearchArgs(
            TrxType: IvTrxTypes.MiscellaneousIssue,
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

        return IvMiscIssueOperationResult.OkList(new IvMiscIssueListPage
        {
            Rows = rows.Select(x => new IvMiscIssueListRow
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

    public async Task<IvMiscIssueOperationResult> GetAsync(
        int batchNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvMiscIssueOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryMiscIssue, PermissionCodes.Access, cancellationToken))
        {
            return IvMiscIssueOperationResult.Fail("Not authorized.");
        }

        if (batchNo <= 0)
        {
            return IvMiscIssueOperationResult.Fail("Batch number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await _transactions.GetByBatchNoAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            batchNo,
            cancellationToken);

        if (batch is null
            || !string.Equals(batch.TrxType, IvTrxTypes.MiscellaneousIssue, StringComparison.OrdinalIgnoreCase))
        {
            return IvMiscIssueOperationResult.Fail("Miscellaneous issue was not found.");
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
            .Where(d => d.FromBalLocId.HasValue)
            .Select(d => d.FromBalLocId!.Value)
            .Distinct()
            .ToList();

        var availableQtyById = new Dictionary<int, decimal>();
        if (balLocIds.Count > 0)
        {
            var balRows = await db.IvBalLocs
                .AsNoTracking()
                .Where(x => balLocIds.Contains(x.Id))
                .Select(x => new { x.Id, x.StdQty })
                .ToListAsync(cancellationToken);
            availableQtyById = balRows.ToDictionary(x => x.Id, x => x.StdQty);
        }

        var lines = batch.Details
            .OrderBy(d => d.TrxLineNo)
            .Select(d =>
            {
                var code = (d.ICode ?? string.Empty).Trim();
                lotControlByCode.TryGetValue(code, out var lotControl);
                var availableQty = d.FromBalLocId.HasValue
                    && availableQtyById.TryGetValue(d.FromBalLocId.Value, out var aq) ? aq : 0m;
                return new IvMiscIssueLineDto
                {
                    LineNo = d.TrxLineNo,
                    FromBalLocId = d.FromBalLocId ?? 0,
                    ICode = code,
                    IDesc = d.IDesc,
                    FrWarehouse = d.FrWarehouse ?? string.Empty,
                    FrLocation = d.FrLocation,
                    FrLotNo = d.FrLotNo,
                    Quantity = d.FrStdQty ?? 0m,
                    Uom = d.FrStdUom,
                    AvailableQty = availableQty,
                    IClassCode = d.IClassCode,
                    IStatus = string.IsNullOrWhiteSpace(d.IStatus) ? IvItemStatuses.Active : d.IStatus,
                    UnitPrice = d.UnitPrice ?? 0m,
                    ExpiryDate = d.ExpiryDate,
                    Reason = null,
                    Remarks = d.Remarks,
                    LotControl = lotControl
                };
            })
            .ToList();

        return IvMiscIssueOperationResult.OkDocument(new IvMiscIssueDocument
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

    public async Task<IvMiscIssueOperationResult> SaveNewAsync(
        IvMiscIssueSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return IvMiscIssueOperationResult.Fail("Save request is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvMiscIssueOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryMiscIssue, PermissionCodes.Add, cancellationToken))
        {
            return IvMiscIssueOperationResult.Fail("Not authorized.");
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
            return IvMiscIssueOperationResult.Fail(validatedResult.ErrorMessage);
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
            TrxType = IvTrxTypes.MiscellaneousIssue,
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
            "Miscellaneous issue saved. UserId={UserId} CompanyCode={CompanyCode} BranchCode={BranchCode} BatchNo={BatchNo} Lines={LineCount}",
            context.UserId,
            context.CompanyCode,
            context.BranchCode,
            batchNo,
            batch.Details.Count);

        return IvMiscIssueOperationResult.OkSaved(batch.Id, batchNo);
    }

    public async Task<IvMiscIssueOperationResult> UpdateAsync(
        int batchNo,
        IvMiscIssueSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return IvMiscIssueOperationResult.Fail("Save request is required.");
        }

        if (batchNo <= 0)
        {
            return IvMiscIssueOperationResult.Fail("Batch number is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvMiscIssueOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryMiscIssue, PermissionCodes.Edit, cancellationToken))
        {
            return IvMiscIssueOperationResult.Fail("Not authorized.");
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
            || !string.Equals(batch.TrxType, IvTrxTypes.MiscellaneousIssue, StringComparison.OrdinalIgnoreCase))
        {
            return IvMiscIssueOperationResult.Fail("Miscellaneous issue was not found.");
        }

        if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
        {
            return IvMiscIssueOperationResult.Fail("Only NEW miscellaneous issues can be edited.");
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
            return IvMiscIssueOperationResult.Fail(validatedResult.ErrorMessage);
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

        if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
        {
            return IvMiscIssueOperationResult.Fail("Only NEW miscellaneous issues can be edited.");
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Miscellaneous issue updated. UserId={UserId} CompanyCode={CompanyCode} BranchCode={BranchCode} BatchNo={BatchNo} Lines={LineCount}",
            context.UserId,
            context.CompanyCode,
            context.BranchCode,
            batch.BatchNo,
            batch.Details.Count);

        return IvMiscIssueOperationResult.OkSaved(batch.Id, batch.BatchNo);
    }

    public async Task<IvMiscIssueOperationResult> DeleteAsync(
        IReadOnlyList<int>? batchNos,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvMiscIssueOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryMiscIssue, PermissionCodes.Delete, cancellationToken))
        {
            return IvMiscIssueOperationResult.Fail("Not authorized.");
        }

        var nos = (batchNos ?? Array.Empty<int>())
            .Where(n => n > 0)
            .Distinct()
            .ToList();

        if (nos.Count == 0)
        {
            return IvMiscIssueOperationResult.Fail("Select at least one miscellaneous issue.");
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
                || !string.Equals(batch.TrxType, IvTrxTypes.MiscellaneousIssue, StringComparison.OrdinalIgnoreCase))
            {
                return IvMiscIssueOperationResult.Fail($"Miscellaneous issue {no} was not found.");
            }

            if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                return IvMiscIssueOperationResult.Fail(
                    $"Miscellaneous issue {no} cannot be deleted because it is not NEW.");
            }

            var details = await _postingRepo.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
            db.IvTrxBatchDetails.RemoveRange(details);
            db.IvTrxBatches.Remove(batch);
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Miscellaneous issue(s) deleted. UserId={UserId} CompanyCode={CompanyCode} BranchCode={BranchCode} Count={Count}",
            context.UserId,
            context.CompanyCode,
            context.BranchCode,
            nos.Count);

        return IvMiscIssueOperationResult.Ok();
    }

    public async Task<IvMiscIssueOperationResult> CancelAsync(
        IReadOnlyList<int>? batchNos,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvMiscIssueOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryMiscIssue, PermissionCodes.Cancel, cancellationToken))
        {
            return IvMiscIssueOperationResult.Fail("Not authorized.");
        }

        var nos = (batchNos ?? Array.Empty<int>())
            .Where(n => n > 0)
            .Distinct()
            .ToList();

        if (nos.Count == 0)
        {
            return IvMiscIssueOperationResult.Fail("Select at least one miscellaneous issue.");
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
                || !string.Equals(batch.TrxType, IvTrxTypes.MiscellaneousIssue, StringComparison.OrdinalIgnoreCase))
            {
                return IvMiscIssueOperationResult.Fail($"Miscellaneous issue {no} was not found.");
            }

            if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                return IvMiscIssueOperationResult.Fail(
                    $"Miscellaneous issue {no} cannot be cancelled because it is not NEW.");
            }

            batch.BatchStatus = IvBatchStatuses.Cancelled;
            batch.ModifiedDate = DateTime.UtcNow;
            batch.ModifiedBy = Truncate(context.UserId!, 10);
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Miscellaneous issue(s) cancelled. UserId={UserId} CompanyCode={CompanyCode} BranchCode={BranchCode} Count={Count}",
            context.UserId,
            context.CompanyCode,
            context.BranchCode,
            nos.Count);

        return IvMiscIssueOperationResult.Ok();
    }

    public async Task<IvMiscIssueOperationResult> PostAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default)
    {
        var posting = await _posting.PostAsync(IvTrxTypes.MiscellaneousIssue, batchNos, cancellationToken);
        return IvMiscIssueOperationResult.OkPosting(posting);
    }

    public async Task<IvMiscIssueOperationResult> RollbackAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default)
    {
        var posting = await _posting.RollbackAsync(IvTrxTypes.MiscellaneousIssue, batchNos, cancellationToken);
        return IvMiscIssueOperationResult.OkPosting(posting);
    }

    private async Task<(string? ErrorMessage, List<ValidatedLine>? Lines)> ValidateLinesAsync(
        AppDbContext db,
        IReadOnlyList<IvMiscIssueLineRequest>? lines,
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken)
    {
        if (lines is null || lines.Count == 0)
        {
            return ("Add at least one issue line.", null);
        }

        if (lines.Count > short.MaxValue)
        {
            return ("Too many issue lines.", null);
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
            batch.Details.Add(new IvTrxBatchDetail
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                BatchNo = batchNo,
                TrxLineNo = trxLineNo,
                TrxType = IvTrxTypes.MiscellaneousIssue,
                ICode = row.Item.ICode,
                IDesc = TruncateOptional(row.IDesc, 200),
                ProdCode = row.Item.ICode,
                ProdDesc = TruncateOptional(row.IDesc, 200),
                FromBalLocId = row.FromBalLocId,
                FrWarehouse = row.FrWarehouse,
                FrLocation = row.FrLocation,
                FrLotNo = row.FrLotNo,
                FrStdQty = IvQty.Round(row.Quantity),
                FrStdUom = row.Uom,
                FromLotId = null,
                IStatus = row.IStatus,
                IClassCode = row.IClassCode,
                ExpiryDate = row.ExpiryDate,
                UnitPrice = IvQty.Round(row.UnitPrice),
                Remarks = row.Remarks,
                LocationCode = NullIfWhiteSpace(locationCode)
            });
            trxLineNo++;
        }
    }

    private async Task<(string? ErrorMessage, ValidatedLine? Line)> ValidateLineAsync(
        AppDbContext db,
        IvMiscIssueLineRequest line,
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
            return ($"Line {lineNo}: item '{iCode}' is not stock-controlled and cannot be issued.", null);
        }

        if (line.FromBalLocId <= 0)
        {
            return ($"Line {lineNo}: from balance location is required.", null);
        }

        var bal = await db.IvBalLocs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == line.FromBalLocId && x.CompanyCode == companyCode && x.BranchCode == branchCode,
                cancellationToken);

        if (bal is null)
        {
            return ($"Line {lineNo}: balance record was not found.", null);
        }

        if (!string.Equals(bal.ICode, iCode, StringComparison.OrdinalIgnoreCase))
        {
            return ($"Line {lineNo}: balance record does not belong to item '{iCode}'.", null);
        }

        if (bal.StdQty <= 0m)
        {
            return ($"Line {lineNo}: no stock available for item '{iCode}'.", null);
        }

        var frWarehouse = (line.FrWarehouse ?? string.Empty).Trim();
        var balWarehouse = (bal.WhCode ?? string.Empty).Trim();
        if (!string.Equals(frWarehouse, balWarehouse, StringComparison.OrdinalIgnoreCase))
        {
            return ($"Line {lineNo}: warehouse does not match the selected balance record.", null);
        }

        if (string.IsNullOrWhiteSpace(frWarehouse))
        {
            return ($"Line {lineNo}: warehouse is required.", null);
        }

        var warehouse = await _common.GetActiveWarehouseAsync(
            db, companyCode, branchCode, frWarehouse, cancellationToken);
        if (warehouse is null)
        {
            return ($"Line {lineNo}: warehouse '{frWarehouse}' was not found for this branch.", null);
        }

        var frLocation = (line.FrLocation ?? string.Empty).Trim();
        var balLocation = (bal.LocCode ?? string.Empty).Trim();
        if (!string.Equals(frLocation, balLocation, StringComparison.OrdinalIgnoreCase))
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

        var frLotNo = (line.FrLotNo ?? string.Empty).Trim();
        var balLotNo = (bal.LotNo ?? string.Empty).Trim();
        DateTime? expiry = line.ExpiryDate?.Date;

        if (item.LotControl)
        {
            if (string.IsNullOrWhiteSpace(frLotNo))
            {
                return ($"Line {lineNo}: lot number is required for lot-controlled item '{item.ICode}'.", null);
            }

            if (!string.Equals(frLotNo, balLotNo, StringComparison.OrdinalIgnoreCase))
            {
                return ($"Line {lineNo}: lot number does not match the selected balance record.", null);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(frLotNo))
            {
                return ($"Line {lineNo}: lot number is not allowed for non-lot-controlled item '{item.ICode}'.", null);
            }

            frLotNo = string.Empty;
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

        if (line.Quantity <= 0)
        {
            return ($"Line {lineNo}: quantity must be greater than zero.", null);
        }

        if (line.UnitPrice < 0)
        {
            return ($"Line {lineNo}: unit price cannot be negative.", null);
        }

        var desc = string.IsNullOrWhiteSpace(line.IDesc) ? item.IDesc : line.IDesc.Trim();
        var remarks = CombineRemarks(line.Reason, line.Remarks);

        return (null, new ValidatedLine(
            item,
            desc,
            line.FromBalLocId,
            frWarehouse,
            frLocation,
            frLotNo,
            line.Quantity,
            uom,
            iStatus,
            iClassCode,
            expiry,
            line.UnitPrice,
            remarks));
    }

    private static string? CombineRemarks(string? reason, string? remarks)
    {
        var rsn = reason?.Trim();
        var rem = remarks?.Trim();
        if (string.IsNullOrEmpty(rsn))
        {
            return TruncateOptional(rem, 250);
        }

        if (string.IsNullOrEmpty(rem))
        {
            return TruncateOptional(rsn, 250);
        }

        return TruncateOptional($"{rsn}: {rem}", 250);
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
        int FromBalLocId,
        string FrWarehouse,
        string FrLocation,
        string FrLotNo,
        decimal Quantity,
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
