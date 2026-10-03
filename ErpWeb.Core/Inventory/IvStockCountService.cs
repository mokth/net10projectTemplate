using System.Globalization;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

/// <summary>
/// Physical stock-count (cycle count) document.
///
/// The design in one paragraph: Generate snapshots the live <c>IvBalLoc</c> piles into
/// <c>IvStockCountLine</c>; the operator records a physical quantity per pile; POST computes each
/// variance against the LIVE balance and generates a normal <c>ADJ</c> batch that is posted through
/// the EXISTING <see cref="IIvInventoryPostingService"/> — in one transaction with the batch insert
/// and the header stamp, via <c>PostStockAdjustmentInTransactionAsync</c>. There is no second posting
/// engine, no new posting table and no new transaction type.
///
/// Semantic departure from the legacy cycle count (deliberate): the adjustment delta is computed at
/// POST against live stock, never frozen at Generate. <c>SystemQty</c> is evidence and is displayed in
/// the preview; a line that moved in the meantime is flagged stale and the post still proceeds.
///
/// <para>
/// The variance report (plan-inventoryInquirySuite Phase 3) is a read over this same evidence, so it
/// lives in <c>IvStockCountService.Variance.cs</c> as the other half of this class rather than in a
/// parallel service.
/// </para>
/// </summary>
public sealed partial class IvStockCountService : IIvStockCountService
{
    private const string CountNoPrefix = "CC";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IRunningNumberService _runningNumbers;
    private readonly ICurrentDateService _currentDate;
    private readonly IIvStockCommonRepository _common;
    private readonly IIvStockTransactionRepository _transactions;
    private readonly IIvStockPostingRepository _postingRepo;
    private readonly IIvInventoryPostingService _posting;
    private readonly ILogger<IvStockCountService> _logger;

    public IvStockCountService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IRunningNumberService runningNumbers,
        ICurrentDateService currentDate,
        IIvStockCommonRepository common,
        IIvStockTransactionRepository transactions,
        IIvStockPostingRepository postingRepo,
        IIvInventoryPostingService posting,
        ILogger<IvStockCountService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _runningNumbers = runningNumbers;
        _currentDate = currentDate;
        _common = common;
        _transactions = transactions;
        _postingRepo = postingRepo;
        _posting = posting;
        _logger = logger;
    }

    // ── Read ─────────────────────────────────────────────────────────────────────────────────────

    public async Task<IvStockCountOperationResult> PeekNextCountNoAsync(
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvStockCountOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Access, cancellationToken))
        {
            return IvStockCountOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var peek = await _runningNumbers.PeekNextAsync(
            db, context.CompanyCode!, RunningNumberKeys.InventoryStockCount, cancellationToken);
        return IvStockCountOperationResult.OkPeek(peek);
    }

    public async Task<IvStockCountOperationResult> SearchAsync(
        IvStockCountListQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvStockCountOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Access, cancellationToken))
        {
            return IvStockCountOperationResult.Fail("Not authorized.");
        }

        query ??= new IvStockCountListQuery();
        var company = context.CompanyCode!;
        var branch = context.BranchCode!;
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take <= 0 ? 20 : query.Take, 1, 100);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var counts =
            from h in db.IvStockCountHdrs.AsNoTracking()
            where h.CompanyCode == company && h.BranchCode == branch
            select h;

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            counts = counts.Where(h =>
                h.CountNo.Contains(term)
                || (h.Remark != null && h.Remark.Contains(term))
                || (h.CountedBy != null && h.CountedBy.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim();
            counts = counts.Where(h => h.Status == status);
        }

        if (query.DateFrom is DateTime from)
        {
            counts = counts.Where(h => h.CountDate >= from.Date);
        }

        if (query.DateTo is DateTime to)
        {
            var end = to.Date.AddDays(1);
            counts = counts.Where(h => h.CountDate < end);
        }

        var total = await counts.CountAsync(cancellationToken);

        var descending = query.SortDescending;
        var sortField = (query.SortField ?? string.Empty).Trim();
        counts = sortField switch
        {
            nameof(IvStockCountListRow.CountNo) => descending
                ? counts.OrderByDescending(h => h.CountNo)
                : counts.OrderBy(h => h.CountNo),
            nameof(IvStockCountListRow.Status) => descending
                ? counts.OrderByDescending(h => h.Status).ThenByDescending(h => h.Id)
                : counts.OrderBy(h => h.Status).ThenBy(h => h.Id),
            nameof(IvStockCountListRow.CreatedDate) => descending
                ? counts.OrderByDescending(h => h.CreatedDate).ThenByDescending(h => h.Id)
                : counts.OrderBy(h => h.CreatedDate).ThenBy(h => h.Id),
            _ => descending
                ? counts.OrderByDescending(h => h.CountDate).ThenByDescending(h => h.Id)
                : counts.OrderBy(h => h.CountDate).ThenBy(h => h.Id)
        };

        var rows = await counts
            .Skip(skip)
            .Take(take)
            .Select(h => new IvStockCountListRow
            {
                Id = h.Id,
                CountNo = h.CountNo,
                CountDate = h.CountDate,
                Status = h.Status,
                WHCode = h.WHCode,
                IClassCode = h.IClassCode,
                LineCount = h.Lines.Count,
                CountedLines = h.Lines.Count(l => l.PhysicalQty != null),
                PostedBatchNo = h.PostedBatchNo,
                PostedStaleLines = h.PostedStaleLines,
                CountedBy = h.CountedBy,
                Remark = h.Remark,
                CreatedDate = h.CreatedDate,
                CreatedBy = h.CreatedBy,
                ModifiedDate = h.ModifiedDate,
                ModifiedBy = h.ModifiedBy
            })
            .ToListAsync(cancellationToken);

        return IvStockCountOperationResult.OkList(new IvStockCountListPage
        {
            Rows = rows,
            TotalCount = total
        });
    }

    public async Task<IvStockCountOperationResult> GetAsync(
        string countNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvStockCountOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Access, cancellationToken))
        {
            return IvStockCountOperationResult.Fail("Not authorized.");
        }

        var no = (countNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return IvStockCountOperationResult.Fail("Count number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var header = await db.IvStockCountHdrs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                h => h.CompanyCode == context.CompanyCode!
                     && h.BranchCode == context.BranchCode!
                     && h.CountNo == no,
                cancellationToken);

        if (header is null)
        {
            return IvStockCountOperationResult.Fail($"Stock count {no} was not found.");
        }

        var lines = await db.IvStockCountLines
            .AsNoTracking()
            .Where(l => l.StockCountId == header.Id)
            .OrderBy(l => l.LineNumber)
            .ToListAsync(cancellationToken);

        var live = await LoadLiveQtyAsync(
            db, context.CompanyCode!, context.BranchCode!, lines.Select(l => l.BalLocId), cancellationToken);

        return IvStockCountOperationResult.OkDocument(
            MapDocument(header, lines, live));
    }

    // ── Header lifecycle ─────────────────────────────────────────────────────────────────────────

    public async Task<IvStockCountOperationResult> SaveAsync(
        IvStockCountSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return IvStockCountOperationResult.Fail("Save request is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvStockCountOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Add, cancellationToken))
        {
            return IvStockCountOperationResult.Fail("Not authorized.");
        }

        var dateError = ValidateCountDate(request.CountDate);
        if (dateError is not null)
        {
            return IvStockCountOperationResult.Fail(dateError);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        if (await IvPeriodCloseGuard.EnsureOpenAsync(db, context.CompanyCode!, context.BranchCode!, request.CountDate.Date, cancellationToken) is string periodGuard)
        {
            return IvStockCountOperationResult.Fail(periodGuard);
        }

        var sequence = await _runningNumbers.GetNextAsync(
            db, context.CompanyCode!, RunningNumberKeys.InventoryStockCount, cancellationToken);

        var now = _currentDate.Now;
        var uid = Truncate(context.UserId!, 10);
        var header = new IvStockCountHdr
        {
            CompanyCode = context.CompanyCode!,
            BranchCode = context.BranchCode!,
            CountNo = FormatCountNo(sequence),
            CountDate = request.CountDate.Date,
            Status = IvStockCountStatuses.Draft,
            CreatedDate = now,
            CreatedBy = uid
        };

        ApplyScope(header, request);
        db.IvStockCountHdrs.Add(header);

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Stock count saved. Company={Company} Branch={Branch} CountNo={CountNo} User={User}",
            context.CompanyCode, context.BranchCode, header.CountNo, uid);

        return IvStockCountOperationResult.OkSaved(header.Id, header.CountNo);
    }

    public async Task<IvStockCountOperationResult> UpdateAsync(
        int id,
        IvStockCountSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return IvStockCountOperationResult.Fail("Save request is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvStockCountOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Edit, cancellationToken))
        {
            return IvStockCountOperationResult.Fail("Not authorized.");
        }

        var dateError = ValidateCountDate(request.CountDate);
        if (dateError is not null)
        {
            return IvStockCountOperationResult.Fail(dateError);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var header = await LoadHeaderAsync(db, context.CompanyCode!, context.BranchCode!, id, cancellationToken);
        if (header is null)
        {
            return IvStockCountOperationResult.Fail("Stock count was not found.");
        }

        if (header.Status != IvStockCountStatuses.Draft)
        {
            return IvStockCountOperationResult.Fail(
                $"Only DRAFT stock counts can be edited (status: {header.Status}).");
        }

        if (!TryApplyRowVersion(db, header, request.RowVersion, out var tokenError))
        {
            return IvStockCountOperationResult.Fail(tokenError!);
        }

        if (await IvPeriodCloseGuard.EnsureOpenAsync(db, context.CompanyCode!, context.BranchCode!, request.CountDate.Date, cancellationToken) is string periodGuard)
        {
            return IvStockCountOperationResult.Fail(periodGuard);
        }

        header.CountDate = request.CountDate.Date;
        ApplyScope(header, request);
        header.ModifiedDate = _currentDate.Now;
        header.ModifiedBy = Truncate(context.UserId!, 10);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return IvStockCountOperationResult.Fail(ConcurrencyMessage);
        }

        return IvStockCountOperationResult.OkSaved(header.Id, header.CountNo);
    }

    public async Task<IvStockCountOperationResult> DeleteAsync(
        IReadOnlyList<int>? ids,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvStockCountOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Delete, cancellationToken))
        {
            return IvStockCountOperationResult.Fail("Not authorized.");
        }

        var targets = NormalizeIds(ids);
        if (targets.Count == 0)
        {
            return IvStockCountOperationResult.Fail("Select at least one stock count.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var id in targets)
        {
            var header = await LoadHeaderAsync(db, context.CompanyCode!, context.BranchCode!, id, cancellationToken);
            if (header is null)
            {
                return IvStockCountOperationResult.Fail($"Stock count {id} was not found.");
            }

            if (header.Status != IvStockCountStatuses.Draft)
            {
                return IvStockCountOperationResult.Fail(
                    $"Stock count {header.CountNo} cannot be deleted because it is not DRAFT (status: {header.Status}).");
            }

            var lines = await db.IvStockCountLines
                .Where(l => l.StockCountId == header.Id)
                .ToListAsync(cancellationToken);
            db.IvStockCountLines.RemoveRange(lines);
            db.IvStockCountHdrs.Remove(header);
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return IvStockCountOperationResult.Ok();
    }

    /// <summary>
    /// CANCELLED is terminal: a cancelled sheet is retained as evidence and can never be re-counted,
    /// posted or re-opened (I5).
    /// </summary>
    public async Task<IvStockCountOperationResult> CancelAsync(
        IReadOnlyList<int>? ids,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvStockCountOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Cancel, cancellationToken))
        {
            return IvStockCountOperationResult.Fail("Not authorized.");
        }

        var targets = NormalizeIds(ids);
        if (targets.Count == 0)
        {
            return IvStockCountOperationResult.Fail("Select at least one stock count.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var now = _currentDate.Now;
        var uid = Truncate(context.UserId!, 10);
        foreach (var id in targets)
        {
            var header = await LoadHeaderAsync(db, context.CompanyCode!, context.BranchCode!, id, cancellationToken);
            if (header is null)
            {
                return IvStockCountOperationResult.Fail($"Stock count {id} was not found.");
            }

            if (header.Status is not (IvStockCountStatuses.Draft
                or IvStockCountStatuses.Counted
                or IvStockCountStatuses.RolledBack))
            {
                return IvStockCountOperationResult.Fail(
                    $"Stock count {header.CountNo} cannot be cancelled (status: {header.Status}).");
            }

            header.Status = IvStockCountStatuses.Cancelled;
            if (!string.IsNullOrWhiteSpace(reason))
            {
                header.Remark = TruncateOptional($"CANCELLED: {reason.Trim()}", 250);
            }

            header.ModifiedDate = now;
            header.ModifiedBy = uid;
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return IvStockCountOperationResult.Ok();
    }

    // ── Generate ─────────────────────────────────────────────────────────────────────────────────

    public async Task<IvStockCountOperationResult> GenerateAsync(
        int id,
        bool discardCounts,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvStockCountOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Edit, cancellationToken))
        {
            return IvStockCountOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var header = await LoadHeaderAsync(db, context.CompanyCode!, context.BranchCode!, id, cancellationToken);
        if (header is null)
        {
            return IvStockCountOperationResult.Fail("Stock count was not found.");
        }

        if (header.Status != IvStockCountStatuses.Draft)
        {
            return IvStockCountOperationResult.Fail(
                $"A stock count can only be generated in DRAFT (status: {header.Status}). "
                + "Use Cancel and create a new count, or re-count this one.");
        }

        var existing = await db.IvStockCountLines
            .Where(l => l.StockCountId == header.Id)
            .ToListAsync(cancellationToken);

        // D10: a sheet containing ANY physical quantity is never silently regenerated — not even in
        // DRAFT. Regeneration is a full replace, so the counts would be lost with no trace.
        var counted = existing.Count(l => l.PhysicalQty is not null);
        if (counted > 0 && !discardCounts)
        {
            return IvStockCountOperationResult.Fail(
                $"{counted} line(s) on this sheet have already been counted. "
                + "Regenerate and discard them, or create a new count.");
        }

        var scope = BuildScope(header);
        var (rows, total) = await _common.ListStockCountCandidatesAsync(
            context.CompanyCode!,
            context.BranchCode!,
            scope,
            skip: 0,
            take: IvStockCountLimits.MaxCountLines + 1,
            asOfDate: header.CountDate,
            cancellationToken);

        if (total > IvStockCountLimits.MaxCountLines)
        {
            return IvStockCountOperationResult.Fail(
                $"The selected scope matches {total} piles, which exceeds the "
                + $"{IvStockCountLimits.MaxCountLines} line limit. Narrow the scope (warehouse, class, or item list).");
        }

        db.IvStockCountLines.RemoveRange(existing);
        header.Lines.Clear();

        short lineNo = 1;
        foreach (var row in rows)
        {
            header.Lines.Add(new IvStockCountLine
            {
                StockCountId = header.Id,
                LineNumber = lineNo,
                BalLocId = row.Id,
                ICode = row.ICode,
                IDesc = TruncateOptional(row.IDesc, 200),
                WHCode = NullIfWhiteSpace(row.WhCode),
                LocCode = NullIfWhiteSpace(row.LocCode),
                LotNo = NullIfWhiteSpace(row.LotNo),
                IStatus = (row.IStatus ?? string.Empty).Trim(),
                IClassCode = NullIfWhiteSpace(row.IClassCode),
                StdUom = TruncateOptional(row.StdUom, 10),
                SystemQty = IvQty.Round(row.StdQty),
                // Evidence only: posting resolves its own price from the locked balance (D15).
                SnapshotUnitPrice = row.PurchasePrice is decimal price ? IvQty.Round(price) : null,
                ExpiryDate = row.ExpiryDate,
                PhysicalQty = null
            });

            lineNo++;
        }

        header.ModifiedDate = _currentDate.Now;
        header.ModifiedBy = Truncate(context.UserId!, 10);

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Stock count generated. Company={Company} Branch={Branch} CountNo={CountNo} Lines={Lines} Discarded={Discarded} User={User}",
            context.CompanyCode, context.BranchCode, header.CountNo, rows.Count, counted, header.ModifiedBy);

        return IvStockCountOperationResult.OkGenerated(header.Id, header.CountNo, rows.Count);
    }

    // ── Count entry ──────────────────────────────────────────────────────────────────────────────

    public async Task<IvStockCountOperationResult> SaveCountsAsync(
        int id,
        IReadOnlyList<IvStockCountLineCountRequest>? lines,
        string? rowVersion,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvStockCountOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Edit, cancellationToken))
        {
            return IvStockCountOperationResult.Fail("Not authorized.");
        }

        if (lines is null || lines.Count == 0)
        {
            return IvStockCountOperationResult.Fail("No counted lines were supplied.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var header = await LoadHeaderAsync(db, context.CompanyCode!, context.BranchCode!, id, cancellationToken);
        if (header is null)
        {
            return IvStockCountOperationResult.Fail("Stock count was not found.");
        }

        // I2 (revised 2026-09-24): PhysicalQty is writable until the sheet is POSTED. COUNTED only means
        // "at least one line has been counted so far" — a working state, NOT evidence, because nothing
        // has been posted from it yet. Freezing here made SAVE and SIGN the same act, which is unusable
        // for the normal case: a long sheet keyed in over several days. Only POSTED (frozen evidence)
        // and CANCELLED (terminal) refuse a write; a POSTED sheet is corrected with Rollback + re-count.
        if (header.Status is not (IvStockCountStatuses.Draft
            or IvStockCountStatuses.Counted
            or IvStockCountStatuses.RolledBack))
        {
            return IvStockCountOperationResult.Fail(header.Status == IvStockCountStatuses.Cancelled
                ? $"Stock count {header.CountNo} was cancelled and is kept as evidence. Create a new count."
                : $"Stock count {header.CountNo} is POSTED, so its counted quantities are frozen. "
                  + "Roll the sheet back first if it needs re-counting.");
        }

        // Optional here (unlike Update): the count screen round-trips the token, and the in-transaction
        // item-level entry path reads it itself. A token that IS supplied must still match.
        if (rowVersion is not null)
        {
            if (!TryApplyRowVersion(db, header, rowVersion, out var tokenError))
            {
                return IvStockCountOperationResult.Fail(tokenError!);
            }
        }

        var sheetLines = await db.IvStockCountLines
            .Where(l => l.StockCountId == header.Id)
            .ToListAsync(cancellationToken);

        var byBalLoc = sheetLines.ToDictionary(l => l.BalLocId);
        var now = _currentDate.Now;
        var uid = Truncate(context.UserId!, 10);

        // I6: every referenced pile must still belong to this company AND branch.
        var ids = lines.Select(l => l.BalLocId).Where(x => x > 0).Distinct().ToList();
        var validIds = await LoadValidBalLocIdsAsync(
            db, context.CompanyCode!, context.BranchCode!, ids, cancellationToken);

        foreach (var request in lines)
        {
            if (!byBalLoc.TryGetValue(request.BalLocId, out var line))
            {
                return IvStockCountOperationResult.Fail(
                    $"Balance Id {request.BalLocId} is not part of stock count {header.CountNo}.");
            }

            if (!validIds.Contains(request.BalLocId))
            {
                return IvStockCountOperationResult.Fail(
                    $"Balance Id {request.BalLocId} no longer belongs to {context.CompanyCode}/{context.BranchCode}.");
            }

            if (request.PhysicalQty is decimal qty && qty < 0m)
            {
                return IvStockCountOperationResult.Fail(
                    $"Line {line.LineNumber} ({line.ICode}): counted quantity cannot be negative.");
            }

            if (request.PhysicalQty is not null && line.PhysicalQty is not null)
            {
                line.RecountCount++;
            }

            line.PhysicalQty = request.PhysicalQty is decimal value ? IvQty.Round(value) : null;
            line.CountedBy = request.PhysicalQty is null ? null : uid;
            line.CountedOn = request.PhysicalQty is null ? null : now;
        }

        // COUNTED means "at least one line counted"; a save with no quantity anywhere stays DRAFT.
        if (header.Status == IvStockCountStatuses.Draft
            && sheetLines.Any(l => l.PhysicalQty is not null))
        {
            header.Status = IvStockCountStatuses.Counted;
        }

        header.CountedBy = uid;
        header.ModifiedDate = now;
        header.ModifiedBy = uid;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return IvStockCountOperationResult.Fail(ConcurrencyMessage);
        }

        return IvStockCountOperationResult.OkSaved(header.Id, header.CountNo);
    }

    /// <summary>
    /// "Enter by item" (D12). An item can legitimately live in several warehouse/location/lot piles, so
    /// a single total applied to an arbitrary slice would post a wrong adjustment. A direct write is
    /// therefore allowed only when the item has exactly ONE countable line here, or when the caller
    /// names exactly one slice. The service enforces this, not only the UI.
    /// </summary>
    public async Task<IvStockCountOperationResult> SetItemCountAsync(
        int id,
        string iCode,
        decimal physicalQty,
        IReadOnlyList<int>? balLocIds,
        CancellationToken cancellationToken = default)
    {
        var code = (iCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return IvStockCountOperationResult.Fail("Item code is required.");
        }

        if (physicalQty < 0m)
        {
            return IvStockCountOperationResult.Fail("Counted quantity cannot be negative.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvStockCountOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Edit, cancellationToken))
        {
            return IvStockCountOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var header = await db.IvStockCountHdrs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                h => h.Id == id && h.CompanyCode == context.CompanyCode! && h.BranchCode == context.BranchCode!,
                cancellationToken);

        if (header is null)
        {
            return IvStockCountOperationResult.Fail("Stock count was not found.");
        }

        var itemLines = await db.IvStockCountLines
            .AsNoTracking()
            .Where(l => l.StockCountId == id && l.ICode == code)
            .OrderBy(l => l.LineNumber)
            .Select(l => new { l.LineNumber, l.BalLocId, l.WHCode, l.LocCode, l.LotNo })
            .ToListAsync(cancellationToken);

        if (itemLines.Count == 0)
        {
            return IvStockCountOperationResult.Fail($"Item {code} is not on stock count {header.CountNo}.");
        }

        var named = (balLocIds ?? [])
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        if (named.Count > 1)
        {
            return IvStockCountOperationResult.Fail(
                $"Item {code} has {itemLines.Count} piles on this sheet. Pick a single slice to count.");
        }

        int targetBalLocId;
        if (named.Count == 1)
        {
            targetBalLocId = named[0];
            if (itemLines.All(l => l.BalLocId != targetBalLocId))
            {
                return IvStockCountOperationResult.Fail(
                    $"Balance Id {targetBalLocId} is not one of item {code}'s piles on this sheet.");
            }
        }
        else if (itemLines.Count == 1)
        {
            targetBalLocId = itemLines[0].BalLocId;
        }
        else
        {
            var slices = string.Join(", ", itemLines.Select(l =>
                $"{l.WHCode}/{l.LocCode}/{l.LotNo} (line {l.LineNumber})"));
            return IvStockCountOperationResult.Fail(
                $"Item {code} has {itemLines.Count} piles on this sheet ({slices}). "
                + "Pick the slice to count.");
        }

        return await SaveCountsAsync(
            id,
            [new IvStockCountLineCountRequest { BalLocId = targetBalLocId, PhysicalQty = physicalQty }],
            rowVersion: null,
            cancellationToken);
    }

    // ── Posting ──────────────────────────────────────────────────────────────────────────────────

    public async Task<IvStockCountOperationResult> PreviewPostAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvStockCountOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Post, cancellationToken))
        {
            return IvStockCountOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var header = await db.IvStockCountHdrs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                h => h.Id == id && h.CompanyCode == context.CompanyCode! && h.BranchCode == context.BranchCode!,
                cancellationToken);

        if (header is null)
        {
            return IvStockCountOperationResult.Fail("Stock count was not found.");
        }

        var lines = await db.IvStockCountLines
            .AsNoTracking()
            .Where(l => l.StockCountId == header.Id)
            .OrderBy(l => l.LineNumber)
            .ToListAsync(cancellationToken);

        var balances = await LoadLockedBalancesAsync(
            db, context.CompanyCode!, context.BranchCode!, lines.Select(l => l.BalLocId), cancellationToken);

        var previewLines = new List<IvStockCountPreviewLine>(lines.Count);
        int increase = 0, decrease = 0, zero = 0, notCounted = 0, stale = 0, errors = 0;
        decimal totalIncrease = 0m, totalDecrease = 0m;

        foreach (var line in lines)
        {
            balances.TryGetValue(line.BalLocId, out var balance);

            if (line.PhysicalQty is null)
            {
                notCounted++;
                previewLines.Add(new IvStockCountPreviewLine
                {
                    LineNumber = line.LineNumber,
                    BalLocId = line.BalLocId,
                    ICode = line.ICode,
                    IDesc = line.IDesc,
                    WHCode = line.WHCode,
                    LocCode = line.LocCode,
                    LotNo = line.LotNo,
                    Uom = line.StdUom,
                    SystemQty = line.SystemQty,
                    LiveQty = balance?.StdQty,
                    PhysicalQty = null,
                    SnapshotUnitPrice = line.SnapshotUnitPrice
                });
                continue;
            }

            var error = balance is null
                ? $"Line {line.LineNumber}: balance Id {line.BalLocId} was not found for this company/branch."
                : SliceMismatchError(line, balance);

            decimal? variance = null;
            string? direction = null;
            var isStale = false;
            if (error is null && line.PhysicalQty is decimal physical)
            {
                variance = IvQty.Round(balance!.StdQty) - IvQty.Round(physical);
                direction = variance > 0m ? "DECREASE" : variance < 0m ? "INCREASE" : "NONE";
                isStale = IvQty.Round(balance.StdQty) != line.SystemQty;

                if (variance > 0m)
                {
                    decrease++;
                    totalDecrease += variance.Value;
                }
                else if (variance < 0m)
                {
                    increase++;
                    totalIncrease += Math.Abs(variance.Value);
                }
                else
                {
                    zero++;
                }

                if (isStale)
                {
                    stale++;
                }
            }

            if (error is not null)
            {
                errors++;
            }

            previewLines.Add(new IvStockCountPreviewLine
            {
                LineNumber = line.LineNumber,
                BalLocId = line.BalLocId,
                ICode = line.ICode,
                IDesc = line.IDesc,
                WHCode = line.WHCode,
                LocCode = line.LocCode,
                LotNo = line.LotNo,
                Uom = line.StdUom,
                SystemQty = line.SystemQty,
                LiveQty = balance?.StdQty,
                PhysicalQty = line.PhysicalQty,
                Variance = variance,
                Direction = direction,
                IsStale = isStale,
                SnapshotUnitPrice = line.SnapshotUnitPrice,
                Error = error
            });
        }

        return IvStockCountOperationResult.OkPreview(new IvStockCountPostPreview
        {
            Id = header.Id,
            CountNo = header.CountNo,
            CountDate = header.CountDate,
            Status = header.Status,
            DateError = ValidateCountDate(header.CountDate),
            IncreaseLines = increase,
            DecreaseLines = decrease,
            ZeroVarianceLines = zero,
            NotCountedLines = notCounted,
            StaleLines = stale,
            ErrorLines = errors,
            TotalIncrease = totalIncrease,
            TotalDecrease = totalDecrease,
            Lines = previewLines
        });
    }

    /// <summary>
    /// Posts the count as one transaction: batch insert + stock move + history + header stamp, or
    /// nothing at all. The adjustment delta is computed here against the LIVE balance (D1).
    /// </summary>
    public async Task<IvStockCountOperationResult> PostAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvStockCountOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Post, cancellationToken))
        {
            return IvStockCountOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // ANY step of a post can lose an optimistic-concurrency race: the shared IV_BATCH counter row,
        // the header's rowversion, or a locked balance. All of them must surface as the house
        // concurrency message — an unhandled DbUpdateConcurrencyException is not a valid outcome for a
        // second post of the same sheet (the SQL Server concurrency suite pins this).
        try
        {
            return await PostWithinTransactionAsync(db, tx, context, id, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackQuietlyAsync(tx, cancellationToken);
            return IvStockCountOperationResult.Fail(ConcurrencyMessage);
        }
        catch (DbUpdateException ex) when (SqlErrorClassifier.IsSerializationConflict(ex))
        {
            await RollbackQuietlyAsync(tx, cancellationToken);
            return IvStockCountOperationResult.Fail(ConcurrencyMessage);
        }
    }

    /// <summary>
    /// The post body, inside the caller's transaction. Business refusals are returned as failed
    /// results; a lost race is allowed to bubble so the caller can map it to the concurrency message.
    /// </summary>
    private async Task<IvStockCountOperationResult> PostWithinTransactionAsync(
        AppDbContext db,
        IDbContextTransaction tx,
        UserContext context,
        int id,
        CancellationToken cancellationToken)
    {
        var header = await LoadHeaderAsync(db, context.CompanyCode!, context.BranchCode!, id, cancellationToken);
        if (header is null)
        {
            return IvStockCountOperationResult.Fail("Stock count was not found.");
        }

        if (header.Status is not (IvStockCountStatuses.Counted or IvStockCountStatuses.RolledBack))
        {
            return IvStockCountOperationResult.Fail(
                $"Only a COUNTED or ROLLED_BACK stock count can be posted (status: {header.Status}).");
        }

        // I1: at most one live batch. COUNTED ⇒ no batch linked yet.
        if (header.Status == IvStockCountStatuses.Counted && header.PostedBatchNo is not null)
        {
            return IvStockCountOperationResult.Fail(
                $"Stock count {header.CountNo} already references batch {header.PostedBatchNo}. "
                + "Run Recover or roll the batch back first.");
        }

        if (header.Status == IvStockCountStatuses.RolledBack && header.PostedBatchNo is int previousBatchNo)
        {
            var previous = await _postingRepo.LockBatchForUpdateAsync(
                db, context.CompanyCode!, context.BranchCode!, previousBatchNo, cancellationToken);
            if (previous is not null
                && string.Equals(previous.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                return IvStockCountOperationResult.Fail(
                    $"Batch {previousBatchNo} is still POSTED. Roll it back from the Stock Adjustment list, "
                    + "then run Recover before re-posting.");
            }
        }

        // D9: the batch carries TrxDtTime = CountDate and the stock-move helpers write that into
        // IvBalLoc.TransDate, so an unbounded back-date would re-order piles for FIFO allocation.
        var dateError = ValidateCountDate(header.CountDate);
        if (dateError is not null)
        {
            return IvStockCountOperationResult.Fail(dateError);
        }

        var lines = await db.IvStockCountLines
            .Where(l => l.StockCountId == header.Id)
            .OrderBy(l => l.LineNumber)
            .ToListAsync(cancellationToken);

        var counted = lines.Where(l => l.PhysicalQty is not null).ToList();
        var negative = counted.FirstOrDefault(l => l.PhysicalQty is < 0m);
        if (negative is not null)
        {
            return IvStockCountOperationResult.Fail(
                $"Line {negative.LineNumber} ({negative.ICode}): counted quantity cannot be negative.");
        }

        if (counted.Count == 0)
        {
            return IvStockCountOperationResult.Fail("No counted lines.");
        }

        var masters = await _postingRepo.LockStockMastersAsync(
            db,
            context.CompanyCode!,
            counted.Select(l => l.ICode),
            cancellationToken);

        // BalLocId ascending — the same order the ADJ core locks in, so a concurrent ADJ cannot deadlock.
        var ordered = counted.OrderBy(l => l.BalLocId).ToList();
        var locked = new Dictionary<int, IvBalLocLockResult>();
        var plan = new List<(IvStockCountLine Line, IvBalLocLockResult Balance, decimal Variance, bool IsStale)>();

        foreach (var line in ordered)
        {
            var balance = await _postingRepo.LockBalLocByIdForTenantAsync(
                db, line.BalLocId, context.CompanyCode!, context.BranchCode!, cancellationToken);
            if (balance is null)
            {
                return IvStockCountOperationResult.Fail(
                    $"Line {line.LineNumber}: balance Id {line.BalLocId} was not found for this company/branch.");
            }

            var sliceError = SliceMismatchError(line, balance);
            if (sliceError is not null)
            {
                return IvStockCountOperationResult.Fail(sliceError);
            }

            locked[line.BalLocId] = balance;
            var variance = IvQty.Round(balance.StdQty) - IvQty.Round(line.PhysicalQty!.Value);
            plan.Add((line, balance, variance, IvQty.Round(balance.StdQty) != line.SystemQty));
        }

        var staleLines = plan.Count(p => p.IsStale);
        var now = _currentDate.Now;
        var uid = Truncate(context.UserId!, 10);

        // All-zero variance is a valid, auditable outcome: POSTED with no batch at all.
        if (plan.All(p => p.Variance == 0m))
        {
            header.Status = IvStockCountStatuses.Posted;
            header.PostedBatchNo = null;
            header.PostedBy = uid;
            header.PostedOn = now;
            header.PostedStaleLines = staleLines;
            header.ModifiedDate = now;
            header.ModifiedBy = uid;

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Stock count posted with no variance. Company={Company} Branch={Branch} CountNo={CountNo} User={User}",
                context.CompanyCode, context.BranchCode, header.CountNo, uid);

            return IvStockCountOperationResult.OkPosted(header.Id, header.CountNo, batchNo: null, staleLines);
        }

        // The batch number is allocated inside this transaction: a failed post consumes no number.
        var batchNo = await _runningNumbers.GetNextAsync(
            db, context.CompanyCode!, RunningNumberKeys.IvBatch, cancellationToken);

        var batch = new IvTrxBatch
        {
            CompanyCode = context.CompanyCode!,
            BranchCode = context.BranchCode!,
            BatchNo = batchNo,
            TrxDtTime = header.CountDate,
            TrxType = IvTrxTypes.StockAdjustment,
            BatchStatus = IvBatchStatuses.New,
            RefNo = Truncate(header.CountNo, 50),
            Remarks = TruncateOptional($"STOCK COUNT {header.CountNo}", 250),
            LocationCode = NullIfWhiteSpace(context.LocationCode),
            CreatedDate = now,
            CreatedBy = uid
        };

        short trxLineNo = 1;
        foreach (var (line, balance, variance, _) in plan)
        {
            if (variance == 0m)
            {
                continue;
            }

            masters.TryGetValue(line.ICode, out var master);
            var unitPrice = IvQty.Round(balance.UnitPrice ?? master?.PurchasePrice ?? 0m);

            var detail = new IvTrxBatchDetail
            {
                CompanyCode = context.CompanyCode!,
                BranchCode = context.BranchCode!,
                BatchNo = batchNo,
                TrxLineNo = trxLineNo,
                TrxType = IvTrxTypes.StockAdjustment,
                ICode = line.ICode,
                IDesc = line.IDesc,
                ProdCode = line.ICode,
                ProdDesc = line.IDesc,
                IStatus = line.IStatus,
                IClassCode = line.IClassCode,
                ExpiryDate = line.ExpiryDate,
                UnitPrice = unitPrice,
                LocationCode = NullIfWhiteSpace(context.LocationCode),
                Remarks = IvStockAdjustmentLineInvariant.CombineRemarks(IvAdjustmentReasons.Count, header.CountNo)
            };

            if (variance > 0m)
            {
                // Live is higher than the count ⇒ write the pile down.
                detail.FromBalLocId = line.BalLocId;
                detail.FrWarehouse = balance.WhCode;
                detail.FrLocation = balance.LocCode;
                detail.FrLotNo = balance.LotNo;
                detail.FrStdQty = variance;
                detail.FrStdUom = balance.StdUom ?? line.StdUom;
            }
            else
            {
                detail.ToBalLocId = line.BalLocId;
                detail.ToWarehouse = balance.WhCode;
                detail.ToLocation = balance.LocCode;
                detail.ToLotNo = balance.LotNo;
                detail.ToStdQty = Math.Abs(variance);
                detail.ToStdUom = balance.StdUom ?? line.StdUom;
            }

            batch.Details.Add(detail);
            trxLineNo++;
        }

        await _transactions.InsertAsync(db, batch, cancellationToken);

        header.Status = IvStockCountStatuses.Posted;
        header.PostedBatchNo = batchNo;
        header.PostedBy = uid;
        header.PostedOn = now;
        header.PostedStaleLines = staleLines;
        header.ModifiedDate = now;
        header.ModifiedBy = uid;

        // The ADJ core re-reads the batch from the database, so the insert must be flushed inside this
        // same transaction first. Still atomic: a failure below rolls the whole thing back.
        //
        // Losing the race is a normal outcome, not an exception: a second post of the same sheet (or a
        // concurrent scope edit) fails the header's rowversion check here and must report the house
        // concurrency message instead of surfacing DbUpdateConcurrencyException to the caller.
        try
        {
            await db.SaveChangesAsync(cancellationToken);

            var ledger = await _posting.BeginPostingInTransactionAsync(
                db, context.CompanyCode!, context.BranchCode!, batchNo, false,
                batch.TrxDtTime, cancellationToken);
            if (ledger.Error is not null)
                return IvStockCountOperationResult.Fail(ledger.Error.Message);
            var result = await _posting.PostStockAdjustmentInTransactionAsync(
                ledger.Context,
                db, context.CompanyCode!, context.BranchCode!, uid, batchNo, cancellationToken);
            if (!result.Succeeded)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvStockCountOperationResult.Fail(result.ErrorMessage ?? "Stock post failed.");
            }
            await _posting.CompletePostingInTransactionAsync(ledger.Context, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvStockCountOperationResult.Fail(ConcurrencyMessage);
        }
        catch (DbUpdateException ex) when (SqlErrorClassifier.IsSerializationConflict(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return IvStockCountOperationResult.Fail(ConcurrencyMessage);
        }

        _logger.LogInformation(
            "Stock count posted. Company={Company} Branch={Branch} CountNo={CountNo} BatchNo={BatchNo} Stale={Stale} User={User}",
            context.CompanyCode, context.BranchCode, header.CountNo, batchNo, staleLines, uid);

        return IvStockCountOperationResult.OkPosted(header.Id, header.CountNo, batchNo, staleLines);
    }

    public async Task<IvStockCountOperationResult> RollbackAsync(
        int id,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvStockCountOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Rollback, cancellationToken))
        {
            return IvStockCountOperationResult.Fail("Not authorized.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return IvStockCountOperationResult.Fail("A rollback reason is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var header = await LoadHeaderAsync(db, context.CompanyCode!, context.BranchCode!, id, cancellationToken);
        if (header is null)
        {
            return IvStockCountOperationResult.Fail("Stock count was not found.");
        }

        if (header.Status != IvStockCountStatuses.Posted)
        {
            return IvStockCountOperationResult.Fail(
                $"Only a POSTED stock count can be rolled back (status: {header.Status}).");
        }

        // Deliberately NOT wrapped in a local transaction: the posting service owns its own, and a
        // nested transaction is unsupported (and pointless) here. If the header stamp below somehow
        // failed after the batch was restored, the divergence is exactly what Recover + the
        // reconciliation finding exist to detect and repair (D8).
        if (header.PostedBatchNo is int batchNo)
        {
            var posting = await _posting.RollbackAsync(IvTrxTypes.StockAdjustment, [batchNo], cancellationToken);
            if (!posting.Succeeded)
            {
                return IvStockCountOperationResult.Fail(posting.ErrorMessage ?? "Rollback failed.");
            }
        }

        var now = _currentDate.Now;
        var uid = Truncate(context.UserId!, 10);

        // PostedBatchNo is deliberately KEPT: it is the audit trail of what was rolled back (I1).
        header.Status = header.PostedBatchNo is null
            ? IvStockCountStatuses.Counted
            : IvStockCountStatuses.RolledBack;
        header.RolledBackBy = uid;
        header.RolledBackOn = now;
        header.RollbackReason = TruncateOptional(reason, 250);
        header.ModifiedDate = now;
        header.ModifiedBy = uid;

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Stock count rolled back. Company={Company} Branch={Branch} CountNo={CountNo} BatchNo={BatchNo} User={User}",
            context.CompanyCode, context.BranchCode, header.CountNo, header.PostedBatchNo, uid);

        return IvStockCountOperationResult.OkSaved(header.Id, header.CountNo);
    }

    /// <summary>
    /// The ONE repair path. The count screen is the authoritative rollback home, but the Stock
    /// Adjustment list can roll the same batch back under its own menu permission (D8). When that
    /// happens the header still says POSTED while its batch is NEW; this resets the header to COUNTED
    /// and stamps the reason. Status only — the frozen evidence is never touched (I3).
    /// </summary>
    public async Task<IvStockCountOperationResult> RecoverAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return IvStockCountOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Edit, cancellationToken))
        {
            return IvStockCountOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var header = await LoadHeaderAsync(db, context.CompanyCode!, context.BranchCode!, id, cancellationToken);
        if (header is null)
        {
            return IvStockCountOperationResult.Fail("Stock count was not found.");
        }

        if (header.Status != IvStockCountStatuses.Posted)
        {
            return IvStockCountOperationResult.Fail(
                $"Recover only applies to a POSTED stock count (status: {header.Status}).");
        }

        if (header.PostedBatchNo is not int batchNo)
        {
            return IvStockCountOperationResult.Fail(
                $"Stock count {header.CountNo} was posted with no variance, so there is nothing to recover.");
        }

        var batch = await _postingRepo.LockBatchForUpdateAsync(
            db, context.CompanyCode!, context.BranchCode!, batchNo, cancellationToken);
        if (batch is null)
        {
            return IvStockCountOperationResult.Fail($"Batch {batchNo} was not found.");
        }

        if (string.Equals(batch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            return IvStockCountOperationResult.Fail(
                $"Batch {batchNo} is still POSTED — there is nothing to recover.");
        }

        var now = _currentDate.Now;
        var uid = Truncate(context.UserId!, 10);
        header.Status = IvStockCountStatuses.Counted;
        // I1: COUNTED means "no batch is live for this sheet", so the pointer is cleared. The batch
        // number survives in the rollback reason (and in the batch row itself), which is the audit trail.
        header.PostedBatchNo = null;
        header.RolledBackBy = uid;
        header.RolledBackOn = now;
        header.RollbackReason = TruncateOptional(
            $"Recovered: batch {batchNo} was rolled back outside the count screen.", 250);
        header.ModifiedDate = now;
        header.ModifiedBy = uid;

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogWarning(
            "Stock count recovered. Company={Company} Branch={Branch} CountNo={CountNo} BatchNo={BatchNo} User={User}",
            context.CompanyCode, context.BranchCode, header.CountNo, batchNo, uid);

        return IvStockCountOperationResult.OkSaved(header.Id, header.CountNo);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private const string ConcurrencyMessage =
        "This stock count was changed by another user. Reload and try again.";

    /// <summary>
    /// Rolls back without masking the original failure: if the transaction already completed (or the
    /// connection is gone) the rollback itself must not throw over the top of the real diagnosis.
    /// </summary>
    private static async Task RollbackQuietlyAsync(
        IDbContextTransaction tx,
        CancellationToken cancellationToken)
    {
        try
        {
            await tx.RollbackAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Intentionally swallowed - the caller already has a better message.
        }
    }

    private static string FormatCountNo(int sequence) =>
        CountNoPrefix + sequence.ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>
    /// D9: the physical-count business date must be a real, recent business date. One definition, used
    /// by Save / Update / Preview / Post so the screen and the server can never disagree.
    /// </summary>
    private string? ValidateCountDate(DateTime countDate)
    {
        var date = countDate.Date;
        var today = _currentDate.Today;

        if (date > today)
        {
            return $"Count date {date:yyyy-MM-dd} is in the future. Use today or an earlier date.";
        }

        var oldest = today.AddDays(-IvStockCountLimits.MaxBackdateDays);
        if (date < oldest)
        {
            return $"Count date {date:yyyy-MM-dd} is older than the allowed {IvStockCountLimits.MaxBackdateDays} day(s) "
                + $"(earliest {oldest:yyyy-MM-dd}).";
        }

        return null;
    }

    private static void ApplyScope(IvStockCountHdr header, IvStockCountSaveRequest request)
    {
        header.WHCode = TruncateOptional(request.WHCode, 20);
        header.LocCode = TruncateOptional(request.LocCode, 10);
        header.IClassCode = TruncateOptional(request.IClassCode, 10);
        header.ISubClassCode = TruncateOptional(request.ISubClassCode, 10);
        header.IType = TruncateOptional(request.IType, 20);
        header.IStatus = JoinList(request.Statuses, 20);
        header.ICodeList = JoinList(request.ICodes, 1000);
        header.IncludeZeroQty = request.IncludeZeroQty;
        header.CountedBy = TruncateOptional(request.CountedBy, 10);
        header.Remark = TruncateOptional(request.Remark, 250);
    }

    private static IvStockCountScope BuildScope(IvStockCountHdr header) => new()
    {
        WHCode = header.WHCode,
        LocCode = header.LocCode,
        IClassCode = header.IClassCode,
        ISubClassCode = header.ISubClassCode,
        IType = header.IType,
        Statuses = SplitList(header.IStatus),
        ICodes = SplitList(header.ICodeList),
        IncludeZeroQty = header.IncludeZeroQty
    };

    private static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? JoinList(IReadOnlyList<string>? values, int maxLength)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var joined = string.Join(
            ',',
            values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct());
        return joined.Length == 0 ? null : Truncate(joined, maxLength);
    }

    private static IvStockCountDocument MapDocument(
        IvStockCountHdr header,
        IReadOnlyList<IvStockCountLine> lines,
        IReadOnlyDictionary<int, decimal> live)
    {
        return new IvStockCountDocument
        {
            Id = header.Id,
            CountNo = header.CountNo,
            CountDate = header.CountDate,
            Status = header.Status,
            WHCode = header.WHCode,
            LocCode = header.LocCode,
            IClassCode = header.IClassCode,
            ISubClassCode = header.ISubClassCode,
            IType = header.IType,
            Statuses = SplitList(header.IStatus),
            ICodes = SplitList(header.ICodeList),
            IncludeZeroQty = header.IncludeZeroQty,
            CountedBy = header.CountedBy,
            Remark = header.Remark,
            PostedBatchNo = header.PostedBatchNo,
            PostedBy = header.PostedBy,
            PostedOn = header.PostedOn,
            PostedStaleLines = header.PostedStaleLines,
            RolledBackBy = header.RolledBackBy,
            RolledBackOn = header.RolledBackOn,
            RollbackReason = header.RollbackReason,
            RowVersion = header.RowVersion.Length == 0 ? string.Empty : Convert.ToBase64String(header.RowVersion),
            Lines = lines.Select(l => MapLine(l, live)).ToList()
        };
    }

    private static IvStockCountLineDto MapLine(IvStockCountLine line, IReadOnlyDictionary<int, decimal> live)
    {
        live.TryGetValue(line.BalLocId, out var liveQty);
        decimal? variance = null;
        string? direction = null;
        var isStale = false;

        if (line.PhysicalQty is decimal physical)
        {
            variance = IvQty.Round(liveQty) - IvQty.Round(physical);
            direction = variance > 0m ? "DECREASE" : variance < 0m ? "INCREASE" : "NONE";
            isStale = IvQty.Round(liveQty) != line.SystemQty;
        }

        return new IvStockCountLineDto
        {
            Id = line.Id,
            LineNumber = line.LineNumber,
            BalLocId = line.BalLocId,
            ICode = line.ICode,
            IDesc = line.IDesc,
            WHCode = line.WHCode,
            LocCode = line.LocCode,
            LotNo = line.LotNo,
            IStatus = line.IStatus,
            IClassCode = line.IClassCode,
            StdUom = line.StdUom,
            SystemQty = line.SystemQty,
            PhysicalQty = line.PhysicalQty,
            SnapshotUnitPrice = line.SnapshotUnitPrice,
            ExpiryDate = line.ExpiryDate,
            RecountCount = line.RecountCount,
            CountedBy = line.CountedBy,
            CountedOn = line.CountedOn,
            LiveQty = liveQty,
            Variance = variance,
            Direction = direction,
            IsStale = isStale,
            RowVersion = line.RowVersion
        };
    }

    private static string? SliceMismatchError(IvStockCountLine line, IvBalLocLockResult balance)
    {
        var matches = string.Equals(balance.ICode, line.ICode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(balance.WhCode, (line.WHCode ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(balance.LocCode, (line.LocCode ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(balance.LotNo, (line.LotNo ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(balance.IStatus, (line.IStatus ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

        return matches
            ? null
            : $"Line {line.LineNumber}: balance Id {line.BalLocId} no longer matches the counted slice "
              + "(item/warehouse/location/lot/status).";
    }

    private static async Task<Dictionary<int, decimal>> LoadLiveQtyAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IEnumerable<int> balLocIds,
        CancellationToken cancellationToken)
    {
        var ids = balLocIds.Where(x => x > 0).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.IvBalLocs
            .AsNoTracking()
            .Where(b => ids.Contains(b.Id) && b.CompanyCode == companyCode && b.BranchCode == branchCode)
            .Select(b => new { b.Id, b.StdQty })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.Id, r => r.StdQty);
    }

    private static async Task<Dictionary<int, IvBalLocLockResult>> LoadLockedBalancesAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IEnumerable<int> balLocIds,
        CancellationToken cancellationToken)
    {
        var ids = balLocIds.Where(x => x > 0).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.IvBalLocs
            .AsNoTracking()
            .Where(b => ids.Contains(b.Id) && b.CompanyCode == companyCode && b.BranchCode == branchCode)
            .Select(b => new IvBalLocLockResult
            {
                Id = b.Id,
                CompanyCode = b.CompanyCode,
                BranchCode = b.BranchCode,
                ICode = b.ICode,
                WhCode = b.WhCode,
                LocCode = b.LocCode,
                LotNo = b.LotNo,
                IStatus = b.IStatus,
                LocationCode = b.LocationCode,
                TransDate = b.TransDate,
                StdQty = b.StdQty,
                StdUom = b.StdUom,
                LotId = b.LotId,
                UnitPrice = b.UnitPrice
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.Id);
    }

    private static async Task<HashSet<int>> LoadValidBalLocIdsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyList<int> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.IvBalLocs
            .AsNoTracking()
            .Where(b => ids.Contains(b.Id) && b.CompanyCode == companyCode && b.BranchCode == branchCode)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

        return [.. rows];
    }

    private static Task<IvStockCountHdr?> LoadHeaderAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int id,
        CancellationToken cancellationToken) =>
        db.IvStockCountHdrs.FirstOrDefaultAsync(
            h => h.Id == id && h.CompanyCode == companyCode && h.BranchCode == branchCode,
            cancellationToken);

    /// <summary>
    /// Feeds the browser-supplied token into EF as the ORIGINAL value, which is what makes the Level A
    /// rowversion check fire on SaveChanges. A null token is a caller error, not a concurrency loss.
    /// </summary>
    private static bool TryApplyRowVersion(
        AppDbContext db,
        IvStockCountHdr header,
        string? token,
        out string? error)
    {
        error = null;
        if (token is null)
        {
            error = "Concurrency token is missing. Reload and try again.";
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = token.Length == 0 ? [] : Convert.FromBase64String(token);
        }
        catch (FormatException)
        {
            error = "Concurrency token is invalid. Reload and try again.";
            return false;
        }

        db.Entry(header).Property(h => h.RowVersion).OriginalValue = bytes;
        return true;
    }

    private static List<int> NormalizeIds(IReadOnlyList<int>? ids) =>
        (ids ?? [])
            .Where(id => id > 0)
            .Distinct()
            .ToList();

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
