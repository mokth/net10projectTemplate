using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

public sealed class IvGoodsReceiptService : IIvGoodsReceiptService
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
    private readonly ILogger<IvGoodsReceiptService> _logger;

    public IvGoodsReceiptService(
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
        ILogger<IvGoodsReceiptService> logger)
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

    public async Task<IvGoodsReceiptOperationResult> PeekNextBatchNoAsync(CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvGoodsReceiptOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryGoodsReceipt, PermissionCodes.Access, cancellationToken))
        {
            return IvGoodsReceiptOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var peek = await _runningNumbers.PeekNextAsync(db, context.CompanyCode!, RunningNumberKeys.IvBatch, cancellationToken);
        return IvGoodsReceiptOperationResult.OkPeek(peek);
    }

    public async Task<IvGoodsReceiptOperationResult> SearchAsync(IvGoodsReceiptListQuery? query, CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvGoodsReceiptOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryGoodsReceipt, PermissionCodes.Access, cancellationToken))
        {
            return IvGoodsReceiptOperationResult.Fail("Not authorized.");
        }

        query ??= new IvGoodsReceiptListQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = context.CompanyCode!;
        var branch = context.BranchCode!;
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take <= 0 ? 20 : query.Take, 1, 100);

        var trxTypes = new[] { IvTrxTypes.GoodsReceive, IvTrxTypes.NonStockGoodsReceive };
        var q = db.IvTrxBatches.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && trxTypes.Contains(x.TrxType));

        if (!string.IsNullOrWhiteSpace(query.BatchStatus))
        {
            var status = query.BatchStatus.Trim();
            q = q.Where(x => x.BatchStatus == status);
        }

        if (query.DateFrom is DateTime from)
        {
            q = q.Where(x => x.TrxDtTime >= from.Date);
        }

        if (query.DateTo is DateTime to)
        {
            var toExclusive = to.Date.AddDays(1);
            q = q.Where(x => x.TrxDtTime < toExclusive);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            if (int.TryParse(term, out var batchNo))
            {
                q = q.Where(x => x.BatchNo == batchNo
                    || (x.RefNo != null && x.RefNo.Contains(term))
                    || (x.Remarks != null && x.Remarks.Contains(term))
                    || x.Details.Any(d => d.PoNo != null && d.PoNo.Contains(term)));
            }
            else
            {
                q = q.Where(x => (x.RefNo != null && x.RefNo.Contains(term))
                    || (x.Remarks != null && x.Remarks.Contains(term))
                    || x.Details.Any(d => (d.PoNo != null && d.PoNo.Contains(term))
                        || (d.ICode != null && d.ICode.Contains(term))
                        || (d.IDesc != null && d.IDesc.Contains(term))));
            }
        }

        var total = await q.CountAsync(cancellationToken);
        var page = await ApplySort(q, query.SortField, query.SortDescending)
            .Skip(skip)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.BatchNo,
                x.TrxType,
                x.TrxDtTime,
                x.BatchStatus,
                x.RefNo,
                x.Remarks,
                LineCount = x.Details.Count,
                x.CreatedDate,
                x.CreatedBy
            })
            .ToListAsync(cancellationToken);

        var ids = page.Select(x => x.Id).ToList();
        var totals = ids.Count == 0
            ? new Dictionary<int, decimal>()
            : (await db.IvTrxBatchDetails.AsNoTracking()
                .Where(d => ids.Contains(d.BatchId))
                .Select(d => new { d.BatchId, d.ToPurQty, d.UnitPrice })
                .ToListAsync(cancellationToken))
                .GroupBy(d => d.BatchId)
                .ToDictionary(g => g.Key, g => g.Sum(d => (d.ToPurQty ?? 0m) * (d.UnitPrice ?? 0m)));

        return IvGoodsReceiptOperationResult.OkList(new IvGoodsReceiptListPage
        {
            Rows = page.Select(x => new IvGoodsReceiptListRow
            {
                Id = x.Id,
                BatchNo = x.BatchNo,
                TrxType = x.TrxType,
                TrxDate = x.TrxDtTime,
                BatchStatus = x.BatchStatus,
                RefNo = x.RefNo,
                Remarks = x.Remarks,
                LineCount = x.LineCount,
                TotalAmount = decimal.Round(totals.GetValueOrDefault(x.Id), 2),
                CreatedDate = x.CreatedDate,
                CreatedBy = x.CreatedBy
            }).ToList(),
            TotalCount = total
        });
    }

    public async Task<IvGoodsReceiptOperationResult> SearchPoLinesAsync(
        string trxType,
        string? searchText,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvGoodsReceiptOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryGoodsReceipt, PermissionCodes.Access, cancellationToken))
        {
            return IvGoodsReceiptOperationResult.Fail("Not authorized.");
        }

        var type = NormalizeTrxType(trxType);
        var indirect = string.Equals(type, IvTrxTypes.NonStockGoodsReceive, StringComparison.OrdinalIgnoreCase);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = context.CompanyCode!;
        var branch = context.BranchCode!;

        var latest =
            from h in db.PoOrders.AsNoTracking()
            where h.CompanyCode == company && h.BranchCode == branch
            group h by h.PoNo into g
            select new { PoNo = g.Key, PoRelNo = g.Max(x => x.PoRelNo) };

        var q =
            from h in db.PoOrders.AsNoTracking()
            join m in latest on new { h.PoNo, h.PoRelNo } equals new { m.PoNo, m.PoRelNo }
            from d in h.Details
            where h.CompanyCode == company
                && h.BranchCode == branch
                && d.BalanceQty > 0m
                && (h.Status == PoOrderStatuses.New || h.Status == PoOrderStatuses.Received)
                && ((d.OneTime ?? h.OneTime ?? false) == indirect)
            select new { h, d };

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var term = searchText.Trim();
            q = q.Where(x => x.h.PoNo.Contains(term)
                || (x.h.VendCode != null && x.h.VendCode.Contains(term))
                || (x.h.VendName != null && x.h.VendName.Contains(term))
                || (x.d.ICode != null && x.d.ICode.Contains(term))
                || (x.d.IDesc != null && x.d.IDesc.Contains(term)));
        }

        var rows = await q
            .OrderByDescending(x => x.h.PoDate)
            .ThenByDescending(x => x.h.PoNo)
            .ThenBy(x => x.d.Line)
            .Take(50)
            .Select(x => new IvGoodsReceiptPoLineLookupRow
            {
                PoNo = x.h.PoNo,
                PoRelNo = x.h.PoRelNo,
                PoLineNo = x.d.Line,
                VendCode = x.h.VendCode,
                VendName = x.h.VendName,
                ICode = x.d.ICode ?? string.Empty,
                IDesc = x.d.IDesc,
                OrderedQty = x.d.PoPurQty,
                ReceivedQty = x.d.RecvQty,
                BalanceQty = x.d.BalanceQty,
                DraftQty = 0m,
                AvailableQty = x.d.BalanceQty,
                PurchaseUom = x.d.PurchaseUom,
                StdUom = x.d.StdUom,
                PackSz = x.d.PackSz,
                ToWarehouse = x.d.ToWarehouse,
                IsIndirect = indirect
            })
            .ToListAsync(cancellationToken);

        if (rows.Count > 0)
        {
            var poNos = rows.Select(x => x.PoNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var draftGroups = await (
                from b in db.IvTrxBatches.AsNoTracking()
                from d in b.Details
                where b.CompanyCode == company
                    && b.BranchCode == branch
                    && b.BatchStatus == IvBatchStatuses.New
                    && b.TrxType == type
                    && d.PoNo != null
                    && poNos.Contains(d.PoNo)
                group d by new { d.PoNo, d.PoRelNo, d.PoLineNo } into g
                select new
                {
                    PoNo = g.Key.PoNo!,
                    PoRelNo = g.Key.PoRelNo ?? (short)0,
                    PoLineNo = g.Key.PoLineNo ?? (short)0,
                    DraftQty = g.Sum(x => x.ToPurQty ?? 0m)
                }).ToListAsync(cancellationToken);

            var draftByKey = draftGroups.ToDictionary(
                x => $"{x.PoNo}|{x.PoRelNo}|{x.PoLineNo}",
                x => IvQty.Round(x.DraftQty),
                StringComparer.OrdinalIgnoreCase);

            rows = rows.Select(x =>
            {
                var draft = draftByKey.GetValueOrDefault($"{x.PoNo}|{x.PoRelNo}|{x.PoLineNo}");
                var available = IvQty.Round(Math.Max(0m, x.BalanceQty - draft));
                return new IvGoodsReceiptPoLineLookupRow
                {
                    PoNo = x.PoNo,
                    PoRelNo = x.PoRelNo,
                    PoLineNo = x.PoLineNo,
                    VendCode = x.VendCode,
                    VendName = x.VendName,
                    ICode = x.ICode,
                    IDesc = x.IDesc,
                    OrderedQty = x.OrderedQty,
                    ReceivedQty = x.ReceivedQty,
                    BalanceQty = x.BalanceQty,
                    DraftQty = draft,
                    AvailableQty = available,
                    PurchaseUom = x.PurchaseUom,
                    StdUom = x.StdUom,
                    PackSz = x.PackSz,
                    ToWarehouse = x.ToWarehouse,
                    LotControl = x.LotControl,
                    IsIndirect = x.IsIndirect
                };
            }).ToList();
        }

        if (!indirect && rows.Count > 0)
        {
            var codes = rows.Select(x => x.ICode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var lotByCode = await db.IvStockMasters.AsNoTracking()
                .Where(x => x.CompanyCode == company && codes.Contains(x.ICode))
                .Select(x => new { x.ICode, x.LotControl })
                .ToDictionaryAsync(x => x.ICode, x => x.LotControl, StringComparer.OrdinalIgnoreCase, cancellationToken);
            rows = rows.Select(x => new IvGoodsReceiptPoLineLookupRow
            {
                PoNo = x.PoNo,
                PoRelNo = x.PoRelNo,
                PoLineNo = x.PoLineNo,
                VendCode = x.VendCode,
                VendName = x.VendName,
                ICode = x.ICode,
                IDesc = x.IDesc,
                OrderedQty = x.OrderedQty,
                ReceivedQty = x.ReceivedQty,
                BalanceQty = x.BalanceQty,
                DraftQty = x.DraftQty,
                AvailableQty = x.AvailableQty,
                PurchaseUom = x.PurchaseUom,
                StdUom = x.StdUom,
                PackSz = x.PackSz,
                ToWarehouse = x.ToWarehouse,
                LotControl = lotByCode.GetValueOrDefault(x.ICode),
                IsIndirect = false
            }).ToList();
        }

        return IvGoodsReceiptOperationResult.OkPoLines(rows);
    }

    public async Task<IvGoodsReceiptOperationResult> GetAsync(int batchNo, CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvGoodsReceiptOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryGoodsReceipt, PermissionCodes.Access, cancellationToken))
        {
            return IvGoodsReceiptOperationResult.Fail("Not authorized.");
        }

        if (batchNo <= 0)
        {
            return IvGoodsReceiptOperationResult.Fail("Batch number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await _transactions.GetByBatchNoAsync(db, context.CompanyCode!, context.BranchCode!, batchNo, cancellationToken);
        if (batch is null || !IsGoodsReceiptType(batch.TrxType))
        {
            return IvGoodsReceiptOperationResult.Fail("Goods receipt was not found.");
        }

        var stockCodes = batch.Details.Select(x => (x.ICode ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var lotByCode = stockCodes.Count == 0
            ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            : await db.IvStockMasters.AsNoTracking()
                .Where(x => x.CompanyCode == context.CompanyCode && stockCodes.Contains(x.ICode))
                .Select(x => new { x.ICode, x.LotControl })
                .ToDictionaryAsync(x => x.ICode, x => x.LotControl, StringComparer.OrdinalIgnoreCase, cancellationToken);

        return IvGoodsReceiptOperationResult.OkDocument(new IvGoodsReceiptDocument
        {
            Id = batch.Id,
            BatchNo = batch.BatchNo,
            TrxType = batch.TrxType,
            TrxDate = batch.TrxDtTime,
            BatchStatus = batch.BatchStatus,
            RefNo = batch.RefNo,
            Remark = batch.Remarks,
            Lines = batch.Details.OrderBy(x => x.TrxLineNo).Select(d =>
            {
                var code = (d.ICode ?? string.Empty).Trim();
                return new IvGoodsReceiptLineDto
                {
                    LineNo = d.TrxLineNo,
                    PoNo = d.PoNo ?? string.Empty,
                    PoRelNo = d.PoRelNo ?? 0,
                    PoLineNo = d.PoLineNo ?? 0,
                    ICode = code,
                    IDesc = d.IDesc,
                    ToWarehouse = d.ToWarehouse ?? string.Empty,
                    ToLocation = d.ToLocation,
                    ToLotNo = d.ToLotNo,
                    FrPurQty = d.FrPurQty ?? 0m,
                    ToRecvQty = d.ToPurQty ?? 0m,
                    ToStdQty = d.ToStdQty ?? 0m,
                    PurchaseUom = d.ToPurUom,
                    StdUom = d.ToStdUom,
                    PackSz = d.ToPurQty is > 0m ? PoOrderCalc.RoundQty((d.ToStdQty ?? 0m) / d.ToPurQty.Value) : 1m,
                    IClassCode = d.IClassCode,
                    IStatus = string.IsNullOrWhiteSpace(d.IStatus) ? IvItemStatuses.Active : d.IStatus,
                    UnitPrice = d.UnitPrice ?? 0m,
                    ExpiryDate = d.ExpiryDate,
                    Remarks = d.Remarks,
                    LotControl = lotByCode.GetValueOrDefault(code)
                };
            }).ToList()
        });
    }

    public async Task<IvGoodsReceiptOperationResult> SaveNewAsync(IvGoodsReceiptSaveRequest? request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return IvGoodsReceiptOperationResult.Fail("Save request is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvGoodsReceiptOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryGoodsReceipt, PermissionCodes.Add, cancellationToken))
        {
            return IvGoodsReceiptOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var trxType = NormalizeTrxType(request.TrxType);
        var validated = await ValidateLinesAsync(db, request.Lines, trxType, context.CompanyCode!, context.BranchCode!, cancellationToken);
        if (validated.ErrorMessage is not null)
        {
            return IvGoodsReceiptOperationResult.Fail(validated.ErrorMessage);
        }

        var batchNo = await _runningNumbers.GetNextAsync(db, context.CompanyCode!, RunningNumberKeys.IvBatch, cancellationToken);
        var now = DateTime.UtcNow;
        var userId = Truncate(context.UserId!, 10);
        var batch = new IvTrxBatch
        {
            CompanyCode = context.CompanyCode!,
            BranchCode = context.BranchCode!,
            BatchNo = batchNo,
            TrxDtTime = request.TrxDate == default ? _dates.Today.Date : request.TrxDate.Date,
            TrxType = trxType,
            BatchStatus = IvBatchStatuses.New,
            RefNo = NormalizeRefNo(request.RefNo, batchNo),
            Remarks = TruncateOptional(request.Remark, 250),
            LocationCode = context.LocationCode,
            CreatedDate = now,
            CreatedBy = userId
        };

        AddDetails(batch, validated.Lines!, context.CompanyCode!, context.BranchCode!, context.LocationCode, batchNo, trxType);
        await _transactions.InsertAsync(db, batch, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation("Goods receipt saved. Company={Company} Branch={Branch} BatchNo={BatchNo}", context.CompanyCode, context.BranchCode, batchNo);
        return IvGoodsReceiptOperationResult.OkSaved(batch.Id, batchNo);
    }

    public async Task<IvGoodsReceiptOperationResult> UpdateAsync(int batchNo, IvGoodsReceiptSaveRequest? request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return IvGoodsReceiptOperationResult.Fail("Save request is required.");
        }

        if (batchNo <= 0)
        {
            return IvGoodsReceiptOperationResult.Fail("Batch number is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvGoodsReceiptOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryGoodsReceipt, PermissionCodes.Edit, cancellationToken))
        {
            return IvGoodsReceiptOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var batch = await _postingRepo.LockBatchForUpdateAsync(db, context.CompanyCode!, context.BranchCode!, batchNo, cancellationToken);
        if (batch is null || !IsGoodsReceiptType(batch.TrxType))
        {
            return IvGoodsReceiptOperationResult.Fail("Goods receipt was not found.");
        }

        if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
        {
            return IvGoodsReceiptOperationResult.Fail("Only NEW goods receipts can be edited.");
        }

        var trxType = NormalizeTrxType(request.TrxType);
        var existingDetails = await _postingRepo.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
        var validated = await ValidateLinesAsync(db, request.Lines, trxType, context.CompanyCode!, context.BranchCode!, cancellationToken);
        if (validated.ErrorMessage is not null)
        {
            return IvGoodsReceiptOperationResult.Fail(validated.ErrorMessage);
        }

        var now = DateTime.UtcNow;
        var userId = Truncate(context.UserId!, 10);
        batch.TrxType = trxType;
        batch.TrxDtTime = request.TrxDate == default ? _dates.Today.Date : request.TrxDate.Date;
        batch.RefNo = NormalizeRefNo(request.RefNo, batch.BatchNo);
        batch.Remarks = TruncateOptional(request.Remark, 250);
        batch.ModifiedDate = now;
        batch.ModifiedBy = userId;

        db.IvTrxBatchDetails.RemoveRange(existingDetails);
        batch.Details.Clear();
        AddDetails(batch, validated.Lines!, context.CompanyCode!, context.BranchCode!, context.LocationCode, batch.BatchNo, trxType);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return IvGoodsReceiptOperationResult.OkSaved(batch.Id, batch.BatchNo);
    }

    public async Task<IvGoodsReceiptOperationResult> DeleteAsync(IReadOnlyList<int>? batchNos, CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvGoodsReceiptOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryGoodsReceipt, PermissionCodes.Delete, cancellationToken))
        {
            return IvGoodsReceiptOperationResult.Fail("Not authorized.");
        }

        var nos = (batchNos ?? Array.Empty<int>()).Where(n => n > 0).Distinct().ToList();
        if (nos.Count == 0)
        {
            return IvGoodsReceiptOperationResult.Fail("Select at least one goods receipt.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var batchNo in nos)
        {
            var batch = await _postingRepo.LockBatchForUpdateAsync(db, context.CompanyCode!, context.BranchCode!, batchNo, cancellationToken);
            if (batch is null || !IsGoodsReceiptType(batch.TrxType))
            {
                return IvGoodsReceiptOperationResult.Fail($"Goods receipt {batchNo} was not found.");
            }

            if (!string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                return IvGoodsReceiptOperationResult.Fail($"Goods receipt {batchNo} cannot be deleted because it is not NEW.");
            }

            var details = await _postingRepo.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
            db.IvTrxBatchDetails.RemoveRange(details);
            db.IvTrxBatches.Remove(batch);
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return IvGoodsReceiptOperationResult.Ok();
    }

    public async Task<IvGoodsReceiptOperationResult> PostAsync(IReadOnlyList<int> batchNos, CancellationToken cancellationToken = default)
    {
        var firstType = await ResolvePostingTrxTypeAsync(batchNos, cancellationToken);
        if (firstType.Error is not null)
        {
            return IvGoodsReceiptOperationResult.Fail(firstType.Error);
        }

        var posting = await _posting.PostAsync(firstType.TrxType!, batchNos, cancellationToken);
        return IvGoodsReceiptOperationResult.OkPosting(posting);
    }

    public async Task<IvGoodsReceiptOperationResult> RollbackAsync(IReadOnlyList<int> batchNos, CancellationToken cancellationToken = default)
    {
        var firstType = await ResolvePostingTrxTypeAsync(batchNos, cancellationToken);
        if (firstType.Error is not null)
        {
            return IvGoodsReceiptOperationResult.Fail(firstType.Error);
        }

        var posting = await _posting.RollbackAsync(firstType.TrxType!, batchNos, cancellationToken);
        return IvGoodsReceiptOperationResult.OkPosting(posting);
    }

    private async Task<(string? ErrorMessage, List<ValidatedLine>? Lines)> ValidateLinesAsync(
        AppDbContext db,
        IReadOnlyList<IvGoodsReceiptLineRequest>? lines,
        string trxType,
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken)
    {
        if (lines is null || lines.Count == 0)
        {
            return ("Add at least one goods receipt line.", null);
        }

        var indirect = string.Equals(trxType, IvTrxTypes.NonStockGoodsReceive, StringComparison.OrdinalIgnoreCase);
        var result = new List<ValidatedLine>();
        short lineNo = 1;
        foreach (var line in lines)
        {
            var validated = await ValidateLineAsync(db, line, trxType, indirect, companyCode, branchCode, lineNo, cancellationToken);
            if (validated.ErrorMessage is not null)
            {
                return (validated.ErrorMessage, null);
            }

            result.Add(validated.Line!);
            lineNo++;
        }

        return (null, result);
    }

    private async Task<(string? ErrorMessage, ValidatedLine? Line)> ValidateLineAsync(
        AppDbContext db,
        IvGoodsReceiptLineRequest line,
        string trxType,
        bool indirect,
        string companyCode,
        string branchCode,
        short lineNo,
        CancellationToken cancellationToken)
    {
        var poNo = (line.PoNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(poNo) || line.PoRelNo <= 0 || line.PoLineNo <= 0)
        {
            return ($"Line {lineNo}: PO line is required.", null);
        }

        if (line.ToRecvQty <= 0m)
        {
            return ($"Line {lineNo}: receive quantity must be greater than zero.", null);
        }

        var po = await db.PoOrders.AsNoTracking()
            .Include(x => x.Details)
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.PoNo == poNo)
            .OrderByDescending(x => x.PoRelNo)
            .FirstOrDefaultAsync(cancellationToken);
        if (po is null || po.PoRelNo != line.PoRelNo)
        {
            return ($"Line {lineNo}: PO {poNo}/{line.PoRelNo} is not the latest revision.", null);
        }

        if (!PoStatusPolicy.IsGrPickable(po.Status))
        {
            return ($"Line {lineNo}: PO {poNo} is not available for goods receipt.", null);
        }

        var poLine = po.Details.FirstOrDefault(x => x.Line == line.PoLineNo);
        if (poLine is null)
        {
            return ($"Line {lineNo}: PO line {poNo}/{line.PoLineNo} was not found.", null);
        }

        if ((poLine.OneTime ?? po.OneTime ?? false) != indirect)
        {
            return ($"Line {lineNo}: PO line type does not match this receipt type.", null);
        }

        var stdQty = PoOrderCalc.ComputeStdQty(line.ToRecvQty, poLine.PackSz);
        var itemStatus = string.IsNullOrWhiteSpace(line.IStatus) ? IvItemStatuses.Active : line.IStatus.Trim().ToUpperInvariant();
        var lotControl = false;
        string? toWarehouse = null;
        string? toLocation = null;
        string? toLotNo = null;
        DateTime? expiry = null;
        if (!indirect)
        {
            var item = await _stockMasters.GetByCodeAsync(db, companyCode, poLine.ICode ?? string.Empty, cancellationToken);
            if (item is null || !item.IsActive)
            {
                return ($"Line {lineNo}: stock item '{poLine.ICode}' was not found.", null);
            }

            lotControl = item.LotControl;
            toWarehouse = (line.ToWarehouse ?? poLine.ToWarehouse ?? item.DefWarehouse ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(toWarehouse))
            {
                return ($"Line {lineNo}: warehouse is required.", null);
            }

            var warehouse = await _common.GetActiveWarehouseAsync(db, companyCode, branchCode, toWarehouse, cancellationToken);
            if (warehouse is null)
            {
                return ($"Line {lineNo}: warehouse '{toWarehouse}' was not found for this branch.", null);
            }

            var hasLocations = await _common.HasActiveLocationsAsync(db, companyCode, branchCode, toWarehouse, cancellationToken);
            toLocation = (line.ToLocation ?? item.DefLocation ?? string.Empty).Trim();
            if (hasLocations && string.IsNullOrWhiteSpace(toLocation))
            {
                return ($"Line {lineNo}: location is required for warehouse '{toWarehouse}'.", null);
            }

            if (hasLocations)
            {
                var location = await _common.GetActiveLocationAsync(db, companyCode, branchCode, toWarehouse, toLocation, cancellationToken);
                if (location is null)
                {
                    return ($"Line {lineNo}: location '{toLocation}' was not found for warehouse '{toWarehouse}'.", null);
                }
            }
            else
            {
                toLocation = string.Empty;
            }

            var status = await _common.GetActiveStatusAsync(db, companyCode, itemStatus, cancellationToken);
            if (status is null)
            {
                return ($"Line {lineNo}: item status '{itemStatus}' was not found.", null);
            }

            if (item.LotControl)
            {
                toLotNo = (line.ToLotNo ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(toLotNo))
                {
                    return ($"Line {lineNo}: lot number is required for lot-controlled item '{item.ICode}'.", null);
                }

                expiry = line.ExpiryDate?.Date;
                if (expiry is null)
                {
                    return ($"Line {lineNo}: expiry date is required for lot-controlled item '{item.ICode}'.", null);
                }

                if (expiry.Value < _dates.Today.Date)
                {
                    return ($"Line {lineNo}: expiry date cannot be earlier than today.", null);
                }
            }
            else
            {
                toLotNo = string.Empty;
            }
        }

        return (null, new ValidatedLine(
            po,
            poLine,
            line.ToRecvQty,
            stdQty,
            toWarehouse,
            toLocation,
            toLotNo,
            itemStatus,
            expiry,
            lotControl,
            TruncateOptional(line.Remarks, 250),
            trxType));
    }

    private static void AddDetails(
        IvTrxBatch batch,
        IReadOnlyList<ValidatedLine> validated,
        string companyCode,
        string branchCode,
        string? locationCode,
        int batchNo,
        string trxType)
    {
        short trxLineNo = 1;
        foreach (var row in validated)
        {
            var poLine = row.PoLine;
            batch.Details.Add(new IvTrxBatchDetail
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                BatchNo = batchNo,
                TrxLineNo = trxLineNo,
                TrxType = trxType,
                ICode = poLine.ICode,
                IDesc = TruncateOptional(poLine.IDesc, 200),
                ProdCode = poLine.ICode,
                ProdDesc = TruncateOptional(poLine.IDesc, 200),
                ToWarehouse = row.ToWarehouse,
                ToLocation = row.ToLocation,
                ToLotNo = row.ToLotNo,
                FrPurQty = PoOrderCalc.RoundQty(poLine.BalanceQty),
                FrPurUom = poLine.PurchaseUom,
                ToPurQty = PoOrderCalc.RoundQty(row.ToRecvQty),
                ToPurUom = poLine.PurchaseUom,
                ToStdQty = row.ToStdQty,
                ToStdUom = poLine.StdUom,
                IStatus = row.IStatus,
                IClassCode = poLine.ICode,
                ExpiryDate = row.ExpiryDate,
                UnitPrice = row.PoLine.PoUnitPrice,
                Remarks = row.Remarks,
                PoNo = row.Po.PoNo,
                PoRelNo = row.Po.PoRelNo,
                PoLineNo = row.PoLine.Line,
                LocationCode = NullIfWhiteSpace(locationCode)
            });
            trxLineNo++;
        }
    }

    private async Task<(string? Error, string? TrxType)> ResolvePostingTrxTypeAsync(
        IReadOnlyList<int>? batchNos,
        CancellationToken cancellationToken)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return (context.Error, null);
        }

        var nos = (batchNos ?? Array.Empty<int>()).Where(x => x > 0).Distinct().ToList();
        if (nos.Count == 0)
        {
            return ("No record selected.", null);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var types = await db.IvTrxBatches.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode && x.BranchCode == context.BranchCode && nos.Contains(x.BatchNo))
            .Select(x => x.TrxType)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (types.Count != 1 || !IsGoodsReceiptType(types[0]))
        {
            return ("Select goods receipts of the same type.", null);
        }

        return (null, types[0]);
    }

    private static IQueryable<IvTrxBatch> ApplySort(IQueryable<IvTrxBatch> query, string? sortField, bool desc) =>
        (sortField, desc) switch
        {
            (nameof(IvGoodsReceiptListRow.TrxDate), false) => query.OrderBy(x => x.TrxDtTime).ThenBy(x => x.BatchNo),
            (nameof(IvGoodsReceiptListRow.BatchStatus), true) => query.OrderByDescending(x => x.BatchStatus).ThenByDescending(x => x.BatchNo),
            (nameof(IvGoodsReceiptListRow.BatchStatus), false) => query.OrderBy(x => x.BatchStatus).ThenBy(x => x.BatchNo),
            (_, false) => query.OrderBy(x => x.BatchNo),
            _ => query.OrderByDescending(x => x.BatchNo)
        };

    private static string NormalizeTrxType(string? trxType) =>
        string.Equals((trxType ?? string.Empty).Trim(), IvTrxTypes.NonStockGoodsReceive, StringComparison.OrdinalIgnoreCase)
            ? IvTrxTypes.NonStockGoodsReceive
            : IvTrxTypes.GoodsReceive;

    private static bool IsGoodsReceiptType(string? trxType) =>
        string.Equals(trxType, IvTrxTypes.GoodsReceive, StringComparison.OrdinalIgnoreCase)
        || string.Equals(trxType, IvTrxTypes.NonStockGoodsReceive, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeRefNo(string? refNo, int batchNo)
    {
        var value = (refNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            value = batchNo.ToString();
        }

        return TruncateOptional(value, 50);
    }

    private UserContext ValidateUserContext()
    {
        var scope = _tenant.TryBranchScope();
        return scope is null
            ? UserContext.Fail("Invalid company or branch context.")
            : UserContext.Ok(scope.CompanyCode, scope.BranchCode, scope.LocationCode, scope.UserId);
    }

    private UserContext ValidateWriteContext()
    {
        var scope = _tenant.TryWriteScope();
        return scope is null
            ? UserContext.Fail("Invalid company, branch, or location context.")
            : UserContext.Ok(scope.CompanyCode, scope.BranchCode!, scope.LocationCode!, scope.UserId);
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
    private static string? TruncateOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ValidatedLine(
        PoOrder Po,
        PoOrderDetail PoLine,
        decimal ToRecvQty,
        decimal ToStdQty,
        string? ToWarehouse,
        string? ToLocation,
        string? ToLotNo,
        string IStatus,
        DateTime? ExpiryDate,
        bool LotControl,
        string? Remarks,
        string TrxType);

    private readonly record struct UserContext(
        string? CompanyCode,
        string? BranchCode,
        string? LocationCode,
        string? UserId,
        string? Error)
    {
        public static UserContext Ok(string companyCode, string branchCode, string? locationCode, string userId) =>
            new(companyCode, branchCode, locationCode, userId, null);

        public static UserContext Fail(string error) => new(null, null, null, null, error);
    }
}
