using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

public sealed class IvMiscReceiptService : IIvMiscReceiptService
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
    private readonly ILogger<IvMiscReceiptService> _logger;

    public IvMiscReceiptService(
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
        ILogger<IvMiscReceiptService> logger)
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

    public async Task<IvMiscReceiptOperationResult> PeekNextBatchNoAsync(
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvMiscReceiptOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryMiscReceipt, PermissionCodes.Access, cancellationToken))
        {
            return IvMiscReceiptOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var peek = await _runningNumbers.PeekNextAsync(db, context.CompanyCode!, RunningNumberKeys.IvBatch, cancellationToken);
        return IvMiscReceiptOperationResult.OkPeek(peek);
    }

    public async Task<IvMiscReceiptOperationResult> GetLookupsAsync(
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvMiscReceiptOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryMiscReceipt, PermissionCodes.Access, cancellationToken))
        {
            return IvMiscReceiptOperationResult.Fail("Not authorized.");
        }

        var items = await _stockMasters.ListActiveForLookupAsync(context.CompanyCode!, cancellationToken);
        var warehouses = await _common.ListActiveWarehousesAsync(
            context.CompanyCode!,
            context.BranchCode!,
            cancellationToken);

        return IvMiscReceiptOperationResult.OkLookups(
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

    public async Task<IvMiscReceiptOperationResult> SearchAsync(
        IvMiscReceiptListQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvMiscReceiptOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryMiscReceipt, PermissionCodes.Access, cancellationToken))
        {
            return IvMiscReceiptOperationResult.Fail("Not authorized.");
        }

        query ??= new IvMiscReceiptListQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var args = new IvTrxBatchSearchArgs(
            TrxType: IvTrxTypes.MiscellaneousReceipt,
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

        return IvMiscReceiptOperationResult.OkList(new IvMiscReceiptListPage
        {
            Rows = rows.Select(x => new IvMiscReceiptListRow
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

    public async Task<IvMiscReceiptOperationResult> GetAsync(
        int batchNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvMiscReceiptOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryMiscReceipt, PermissionCodes.Access, cancellationToken))
        {
            return IvMiscReceiptOperationResult.Fail("Not authorized.");
        }

        if (batchNo <= 0)
        {
            return IvMiscReceiptOperationResult.Fail("Batch number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await _transactions.GetByBatchNoAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            batchNo,
            cancellationToken);

        if (batch is null
            || !string.Equals(batch.TrxType, IvTrxTypes.MiscellaneousReceipt, StringComparison.OrdinalIgnoreCase))
        {
            return IvMiscReceiptOperationResult.Fail("Miscellaneous receipt was not found.");
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

        var lines = batch.Details
            .OrderBy(d => d.TrxLineNo)
            .Select(d =>
            {
                var code = (d.ICode ?? string.Empty).Trim();
                lotControlByCode.TryGetValue(code, out var lotControl);
                return new IvMiscReceiptLineDto
                {
                    LineNo = d.TrxLineNo,
                    ICode = code,
                    IDesc = d.IDesc,
                    ToWarehouse = d.ToWarehouse ?? string.Empty,
                    ToLocation = d.ToLocation,
                    ToLotNo = d.ToLotNo,
                    Quantity = d.ToStdQty ?? 0m,
                    Uom = d.ToStdUom,
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

        return IvMiscReceiptOperationResult.OkDocument(new IvMiscReceiptDocument
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

    public async Task<IvMiscReceiptOperationResult> SaveNewAsync(
        IvMiscReceiptSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return IvMiscReceiptOperationResult.Fail("Save request is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvMiscReceiptOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryMiscReceipt, PermissionCodes.Add, cancellationToken))
        {
            return IvMiscReceiptOperationResult.Fail("Not authorized.");
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
            return IvMiscReceiptOperationResult.Fail(validatedResult.ErrorMessage);
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
            TrxType = IvTrxTypes.MiscellaneousReceipt,
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
            "Miscellaneous receipt saved. UserId={UserId} CompanyCode={CompanyCode} BranchCode={BranchCode} BatchNo={BatchNo} Lines={LineCount}",
            context.UserId,
            context.CompanyCode,
            context.BranchCode,
            batchNo,
            batch.Details.Count);

        return IvMiscReceiptOperationResult.OkSaved(batch.Id, batchNo);
    }

    public async Task<IvMiscReceiptOperationResult> UpdateAsync(
        int batchNo,
        IvMiscReceiptSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return IvMiscReceiptOperationResult.Fail("Save request is required.");
        }

        if (batchNo <= 0)
        {
            return IvMiscReceiptOperationResult.Fail("Batch number is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvMiscReceiptOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryMiscReceipt, PermissionCodes.Edit, cancellationToken))
        {
            return IvMiscReceiptOperationResult.Fail("Not authorized.");
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
            || !string.Equals(batch.TrxType, IvTrxTypes.MiscellaneousReceipt, StringComparison.OrdinalIgnoreCase))
        {
            return IvMiscReceiptOperationResult.Fail("Miscellaneous receipt was not found.");
        }

        if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
        {
            return IvMiscReceiptOperationResult.Fail("Only NEW miscellaneous receipts can be edited.");
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
            return IvMiscReceiptOperationResult.Fail(validatedResult.ErrorMessage);
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
            return IvMiscReceiptOperationResult.Fail("Only NEW miscellaneous receipts can be edited.");
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Miscellaneous receipt updated. UserId={UserId} CompanyCode={CompanyCode} BranchCode={BranchCode} BatchNo={BatchNo} Lines={LineCount}",
            context.UserId,
            context.CompanyCode,
            context.BranchCode,
            batch.BatchNo,
            batch.Details.Count);

        return IvMiscReceiptOperationResult.OkSaved(batch.Id, batch.BatchNo);
    }

    public async Task<IvMiscReceiptOperationResult> DeleteAsync(
        IReadOnlyList<int>? batchNos,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvMiscReceiptOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryMiscReceipt, PermissionCodes.Delete, cancellationToken))
        {
            return IvMiscReceiptOperationResult.Fail("Not authorized.");
        }

        var nos = (batchNos ?? Array.Empty<int>())
            .Where(n => n > 0)
            .Distinct()
            .ToList();

        if (nos.Count == 0)
        {
            return IvMiscReceiptOperationResult.Fail("Select at least one miscellaneous receipt.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var batchNo in nos)
        {
            var batch = await _postingRepo.LockBatchForUpdateAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                batchNo,
                cancellationToken);

            if (batch is null
                || !string.Equals(batch.TrxType, IvTrxTypes.MiscellaneousReceipt, StringComparison.OrdinalIgnoreCase))
            {
                return IvMiscReceiptOperationResult.Fail($"Miscellaneous receipt {batchNo} was not found.");
            }

            if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                return IvMiscReceiptOperationResult.Fail(
                    $"Miscellaneous receipt {batchNo} cannot be deleted because it is not NEW.");
            }

            var details = await _postingRepo.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
            db.IvTrxBatchDetails.RemoveRange(details);
            db.IvTrxBatches.Remove(batch);
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Miscellaneous receipt(s) deleted. UserId={UserId} CompanyCode={CompanyCode} BranchCode={BranchCode} Count={Count}",
            context.UserId,
            context.CompanyCode,
            context.BranchCode,
            nos.Count);

        return IvMiscReceiptOperationResult.Ok();
    }

    public async Task<IvMiscReceiptOperationResult> PostAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default)
    {
        var posting = await _posting.PostAsync(IvTrxTypes.MiscellaneousReceipt, batchNos, cancellationToken);
        return IvMiscReceiptOperationResult.OkPosting(posting);
    }

    public async Task<IvMiscReceiptOperationResult> RollbackAsync(
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default)
    {
        var posting = await _posting.RollbackAsync(IvTrxTypes.MiscellaneousReceipt, batchNos, cancellationToken);
        return IvMiscReceiptOperationResult.OkPosting(posting);
    }

    private async Task<(string? ErrorMessage, List<ValidatedLine>? Lines)> ValidateLinesAsync(
        AppDbContext db,
        IReadOnlyList<IvMiscReceiptLineRequest>? lines,
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken)
    {
        if (lines is null || lines.Count == 0)
        {
            return ("Add at least one receipt line.", null);
        }

        if (lines.Count > short.MaxValue)
        {
            return ("Too many receipt lines.", null);
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
                TrxType = IvTrxTypes.MiscellaneousReceipt,
                ICode = row.Item.ICode,
                IDesc = TruncateOptional(row.IDesc, 200),
                ProdCode = row.Item.ICode,
                ProdDesc = TruncateOptional(row.IDesc, 200),
                ToWarehouse = row.ToWarehouse,
                ToLocation = row.ToLocation,
                ToLotNo = row.ToLotNo,
                ToStdQty = IvQty.Round(row.Quantity),
                ToStdUom = row.Uom,
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
        IvMiscReceiptLineRequest line,
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

        var toWarehouse = (line.ToWarehouse ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(toWarehouse))
        {
            return ($"Line {lineNo}: warehouse is required.", null);
        }

        var warehouse = await _common.GetActiveWarehouseAsync(
            db, companyCode, branchCode, toWarehouse, cancellationToken);
        if (warehouse is null)
        {
            return ($"Line {lineNo}: warehouse '{toWarehouse}' was not found for this branch.", null);
        }

        var hasLocations = await _common.HasActiveLocationsAsync(
            db, companyCode, branchCode, toWarehouse, cancellationToken);
        var toLocation = (line.ToLocation ?? string.Empty).Trim();
        if (hasLocations)
        {
            if (string.IsNullOrWhiteSpace(toLocation))
            {
                return ($"Line {lineNo}: location is required for warehouse '{toWarehouse}'.", null);
            }

            var location = await _common.GetActiveLocationAsync(
                db, companyCode, branchCode, toWarehouse, toLocation, cancellationToken);
            if (location is null)
            {
                return ($"Line {lineNo}: location '{toLocation}' was not found for warehouse '{toWarehouse}'.", null);
            }
        }
        else
        {
            toLocation = string.Empty;
        }

        if (toLocation.Length > 10)
        {
            return ($"Line {lineNo}: location must be at most 10 characters.", null);
        }

        var uom = (line.Uom ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(uom))
        {
            uom = (item.StdUom ?? string.Empty).Trim();
        }

        if (string.IsNullOrWhiteSpace(uom))
        {
            return ($"Line {lineNo}: UOM is required.", null);
        }

        var uomRow = await _common.GetActiveUomAsync(db, companyCode, uom, cancellationToken);
        if (uomRow is null)
        {
            return ($"Line {lineNo}: UOM '{uom}' was not found.", null);
        }

        if (uom.Length > 10)
        {
            uom = uom[..10];
        }

        var iClassCode = (line.IClassCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(iClassCode))
        {
            return ($"Line {lineNo}: item class is required.", null);
        }

        var classRow = await _common.GetActiveClassAsync(db, companyCode, iClassCode, cancellationToken);
        if (classRow is null)
        {
            return ($"Line {lineNo}: item class '{iClassCode}' was not found.", null);
        }

        var iStatus = string.IsNullOrWhiteSpace(line.IStatus)
            ? string.Empty
            : line.IStatus.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(iStatus))
        {
            return ($"Line {lineNo}: item status is required.", null);
        }

        if (iStatus.Length > 10)
        {
            return ($"Line {lineNo}: item status must be at most 10 characters.", null);
        }

        var statusRow = await _common.GetActiveStatusAsync(db, companyCode, iStatus, cancellationToken);
        if (statusRow is null)
        {
            return ($"Line {lineNo}: item status '{iStatus}' was not found.", null);
        }

        if (line.Quantity <= 0)
        {
            return ($"Line {lineNo}: quantity must be greater than zero.", null);
        }

        if (line.UnitPrice < 0)
        {
            return ($"Line {lineNo}: unit price cannot be negative.", null);
        }

        var lot = (line.ToLotNo ?? string.Empty).Trim();
        DateTime? expiry = line.ExpiryDate?.Date;
        string toLotNo;

        if (item.LotControl)
        {
            if (string.IsNullOrWhiteSpace(lot))
            {
                return ($"Line {lineNo}: lot number is required for lot-controlled item '{item.ICode}'.", null);
            }

            if (lot.Length > 50)
            {
                return ($"Line {lineNo}: lot number must be at most 50 characters.", null);
            }

            if (expiry is null)
            {
                return ($"Line {lineNo}: expiry date is required for lot-controlled item '{item.ICode}'.", null);
            }

            var today = _dates.Today.Date;
            if (expiry.Value.Date < today)
            {
                return ($"Line {lineNo}: expiry date cannot be earlier than today.", null);
            }

            toLotNo = lot;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(lot))
            {
                return ($"Line {lineNo}: lot number is not allowed for non-lot-controlled item '{item.ICode}'.", null);
            }

            if (line.ExpiryDate is not null)
            {
                return ($"Line {lineNo}: expiry date is not allowed for non-lot-controlled item '{item.ICode}'.", null);
            }

            toLotNo = string.Empty;
            expiry = null;
        }

        var desc = string.IsNullOrWhiteSpace(line.IDesc) ? item.IDesc : line.IDesc.Trim();
        var remarks = CombineRemarks(line.Reason, line.Remarks);

        return (null, new ValidatedLine(
            item,
            desc,
            toWarehouse,
            toLocation,
            toLotNo,
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
        string ToWarehouse,
        string ToLocation,
        string ToLotNo,
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
