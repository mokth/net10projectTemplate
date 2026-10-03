using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

/// <inheritdoc cref="IIvPeriodCloseService"/>
public sealed partial class IvPeriodCloseService : IIvPeriodCloseService
{
    private const string ConcurrencyMessage = "The period was changed by another user. Reload and try again.";

    /// <summary>Menus this service serves: the action screen and the read-only inquiry screen.</summary>
    private static readonly HashSet<string> KnownMenus = new(StringComparer.OrdinalIgnoreCase)
    {
        MenuCodes.InventoryPeriodClose,
        MenuCodes.InventoryPeriodCloseInq
    };

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _currentDate;
    private readonly IIvInventoryReconciliationService _reconciliation;
    private readonly ILogger<IvPeriodCloseService> _logger;

    public IvPeriodCloseService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService currentDate,
        IIvInventoryReconciliationService reconciliation,
        ILogger<IvPeriodCloseService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _currentDate = currentDate;
        _reconciliation = reconciliation;
        _logger = logger;
    }

    // ── Read ─────────────────────────────────────────────────────────────────────────────────────

    public async Task<IvPeriodCloseResult> ListAsync(CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(MenuCodes.InventoryPeriodClose, cancellationToken);
        if (!context.Succeeded)
        {
            return IvPeriodCloseResult.Fail(context.Error!);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var headers = await db.IvPeriodCloseHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode && x.BranchCode == context.BranchCode)
            .OrderByDescending(x => x.PeriodFrom)
            .ToListAsync(cancellationToken);

        return IvPeriodCloseResult.OkList(new IvPeriodCloseListPage
        {
            Headers = headers.Select(MapHeader).ToList(),
            Next = ComputeNextPeriod(headers, _currentDate.Today)
        });
    }

    public async Task<IvPeriodCloseResult> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(MenuCodes.InventoryPeriodClose, cancellationToken);
        if (!context.Succeeded)
        {
            return IvPeriodCloseResult.Fail(context.Error!);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var header = await db.IvPeriodCloseHdrs.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == id && x.CompanyCode == context.CompanyCode && x.BranchCode == context.BranchCode,
                cancellationToken);
        if (header is null)
        {
            return IvPeriodCloseResult.Fail("Closed period was not found.");
        }

        return IvPeriodCloseResult.OkDocument(await BuildDocumentAsync(db, header, cancellationToken));
    }

    // ── Close / reopen ───────────────────────────────────────────────────────────────────────────

    public async Task<IvPeriodCloseResult> CloseAsync(
        IvPeriodCloseRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return IvPeriodCloseResult.Fail("Save request is required.");
        }

        var context = await ResolveAsync(MenuCodes.InventoryPeriodClose, cancellationToken);
        if (!context.Succeeded)
        {
            return IvPeriodCloseResult.Fail(context.Error!);
        }

        if (!await _accessRights.CanAsync(context.MenuCode!, PermissionCodes.Close, cancellationToken))
        {
            return IvPeriodCloseResult.Fail("Not authorized.");
        }

        var periodFrom = request.PeriodFrom.Date;
        var periodTo = request.PeriodTo.Date;
        if (periodFrom > periodTo)
        {
            return IvPeriodCloseResult.Fail("Period start date must be on or before the period end date.");
        }

        if (periodTo > _currentDate.Today)
        {
            return IvPeriodCloseResult.Fail(
                "Period end date cannot be in the future — a partial month cannot be closed.");
        }

        var company = context.CompanyCode!;
        var branch = context.BranchCode!;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await new ErpWeb.Core.StockLedger.BranchStockTransactionLock()
            .AcquireAsync(db, company, branch, cancellationToken);

        var existing = await db.IvPeriodCloseHdrs
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.PeriodFrom == periodFrom,
                cancellationToken);
        if (existing is not null && string.Equals(existing.Status, IvPeriodCloseStatuses.Closed, StringComparison.OrdinalIgnoreCase))
        {
            return IvPeriodCloseResult.Fail(
                $"Period {periodFrom:yyyy-MM} for {branch} has already been closed.");
        }

        var closedHeaders = await db.IvPeriodCloseHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                        && x.Status == IvPeriodCloseStatuses.Closed)
            .ToListAsync(cancellationToken);

        var priorClosed = closedHeaders
            .Where(x => x.PeriodTo < periodFrom)
            .OrderByDescending(x => x.PeriodTo)
            .ToList();
        var firstClose = priorClosed.Count == 0;

        // D6: contiguous. A re-close (existing REOPENED header) already satisfied this on its first close.
        if (existing is null && closedHeaders.Count > 0)
        {
            var maxClosedTo = closedHeaders.Max(x => x.PeriodTo);
            if (periodFrom != maxClosedTo.AddDays(1))
            {
                return IvPeriodCloseResult.Fail(
                    $"Periods must be contiguous. The next period must start on {maxClosedTo.AddDays(1):yyyy-MM-dd}.");
            }
        }

        // D4: a NEW batch dated inside the period blocks the close.
        var blockingBatchNos = await db.IvTrxBatches.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                        && x.BatchStatus == IvBatchStatuses.New
                        && x.TrxDtTime >= periodFrom && x.TrxDtTime < periodTo.AddDays(1))
            .Select(x => x.BatchNo)
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
        if (blockingBatchNos.Count > 0)
        {
            return IvPeriodCloseResult.Fail(
                $"Cannot close: {blockingBatchNos.Count} unposted batch(es) are dated inside the period "
                + $"({FormatBatchList(blockingBatchNos)}). Delete or post them first.");
        }

        // D9: reconciliation precondition (blocking findings refuse; UNEXPECTED_BALANCE is advisory).
        var recon = await _reconciliation.ReconcileAsync(
            MenuCodes.InventoryPeriodClose, null, null, cancellationToken);
        if (!recon.Succeeded)
        {
            return IvPeriodCloseResult.Fail(recon.ErrorMessage ?? "Reconciliation failed.");
        }

        var blockingFindings = recon.Findings
            .Where(f => BlockingFindingCodes.Contains(f.Code))
            .ToList();

        // Insight 3: on the FIRST close a pile with no ledger is the opening baseline being
        // established (UNEXPECTED_BALANCE), and its MISMATCH is absorbed as OpeningAdjustQty —
        // it must not block. The reconciliation service reports BOTH for a no-ledger pile, so
        // the MISMATCH whose slice also carries an UNEXPECTED_BALANCE is excluded here.
        if (firstClose)
        {
            var baselineSlices = recon.Findings
                .Where(f => string.Equals(f.Code, "UNEXPECTED_BALANCE", StringComparison.OrdinalIgnoreCase)
                            && f.Slice is not null)
                .Select(f => f.Slice!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            blockingFindings = blockingFindings
                .Where(f => !string.Equals(f.Code, "MISMATCH", StringComparison.OrdinalIgnoreCase)
                            || f.Slice is null
                            || !baselineSlices.Contains(f.Slice))
                .ToList();
        }

        if (blockingFindings.Count > 0)
        {
            return IvPeriodCloseResult.Fail(
                $"Cannot close: {blockingFindings.Count} blocking reconciliation finding(s) — "
                + string.Join("; ", blockingFindings.Take(5).Select(f => $"{f.Code} {f.Message}"))
                + (blockingFindings.Count > 5 ? $" (+{blockingFindings.Count - 5} more)" : string.Empty));
        }

        // Carry-forward source: the immediately preceding CLOSED period's stored closing quantities.
        var priorClosingBySlice = new Dictionary<IvStockSliceKey, decimal>();
        if (!firstClose)
        {
            var priorId = priorClosed[0].Id;
            var priorRows = await db.IvPeriodCloseBals.AsNoTracking()
                .Where(x => x.PeriodCloseId == priorId)
                .ToListAsync(cancellationToken);
            foreach (var row in priorRows)
            {
                priorClosingBySlice[IvStockSliceKey.Create(
                    row.CompanyCode, row.BranchCode, row.ICode, row.WhCode, row.LocCode, row.LotNo, row.IStatus)] = row.ClosingQty;
            }
        }

        var snapshot = await BuildSnapshotAsync(
            db, company, branch, periodFrom, periodTo, priorClosingBySlice, firstClose, cancellationToken);

        if (snapshot.CarryForwardMismatchSlices > 0)
        {
            return IvPeriodCloseResult.Fail(
                $"Cannot close: {snapshot.CarryForwardMismatchSlices} slice(s) fail carry-forward against the prior "
                + "close — history inside a previously closed period was mutated after the close.");
        }

        if (snapshot.D11Mismatches.Count > 0)
        {
            return IvPeriodCloseResult.Fail(FormatD11Refusal(snapshot.D11Mismatches));
        }

        var now = _currentDate.Now;
        var uid = Truncate(CurrentUserId(), 10);

        IvPeriodCloseHdr header;
        if (existing is not null)
        {
            header = existing;
            header.Status = IvPeriodCloseStatuses.Closed;
            header.ClosedBy = uid;
            header.ClosedOn = now;
            header.Remark = TruncateOptional(request.Remark, 250);
            header.ModifiedDate = now;
            header.ModifiedBy = uid;
            var oldLines = await db.IvPeriodCloseBals
                .Where(x => x.PeriodCloseId == header.Id)
                .ToListAsync(cancellationToken);
            db.IvPeriodCloseBals.RemoveRange(oldLines);
        }
        else
        {
            header = new IvPeriodCloseHdr
            {
                CompanyCode = company,
                BranchCode = branch,
                PeriodFrom = periodFrom,
                PeriodTo = periodTo,
                Status = IvPeriodCloseStatuses.Closed,
                ClosedBy = uid,
                ClosedOn = now,
                Remark = TruncateOptional(request.Remark, 250),
                CreatedDate = now,
                CreatedBy = uid,
                ModifiedDate = now,
                ModifiedBy = uid
            };
            db.IvPeriodCloseHdrs.Add(header);
        }

        header.LineCount = snapshot.Lines.Count;
        header.SkippedZeroSlices = snapshot.SkippedZeroSlices;
        header.OpeningAdjustSlices = snapshot.OpeningAdjustSlices;
        header.CarryForwardMismatchSlices = snapshot.CarryForwardMismatchSlices;
        header.CurrentBalanceCheckApplies = snapshot.CurrentBalanceCheckApplies;
        header.CurrentBalanceMismatchSlices = snapshot.CurrentBalanceMismatchSlices;
        header.UnpostedBatchCount = 0;
        header.ReconcileFindingCount = recon.Findings.Count;

        decimal totalOpening = 0m, totalIn = 0m, totalOut = 0m, totalClosing = 0m;
        foreach (var line in snapshot.Lines)
        {
            totalOpening += IvQty.Round(line.OpeningQty * line.UnitPrice);
            totalIn += IvQty.Round(line.InQty * line.UnitPrice);
            totalOut += IvQty.Round(line.OutQty * line.UnitPrice);
            totalClosing += line.ClosingValue;

            header.Lines.Add(new IvPeriodCloseBal
            {
                CompanyCode = company,
                BranchCode = branch,
                ICode = line.Slice.ICode,
                WhCode = line.Slice.WhCode,
                LocCode = line.Slice.LocCode,
                LotNo = line.Slice.LotNo,
                IStatus = line.Slice.IStatus,
                OpeningQty = line.OpeningQty,
                OpeningAdjustQty = line.OpeningAdjustQty,
                InQty = line.InQty,
                OutQty = line.OutQty,
                AdjustNetQty = line.AdjustNetQty,
                ClosingQty = line.ClosingQty,
                StdUom = line.StdUom,
                UnitPrice = line.UnitPrice,
                ClosingValue = line.ClosingValue,
                LegCount = line.LegCount,
                CarryForwardOk = line.CarryForwardOk,
                CurrentBalanceDelta = line.CurrentBalanceDelta,
                CreatedDate = now,
                CreatedBy = uid
            });
        }

        header.TotalOpeningValue = totalOpening;
        header.TotalInValue = totalIn;
        header.TotalOutValue = totalOut;
        header.TotalClosingValue = totalClosing;

        await AppendQuantityPeriodSnapshotAsync(
            db, company, branch, periodFrom, snapshot, uid, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Period close saved. Company={Company} Branch={Branch} Period={PeriodFrom:yyyy-MM}..{PeriodTo:yyyy-MM} Lines={Lines}",
            company, branch, periodFrom, periodTo, snapshot.Lines.Count);

        return IvPeriodCloseResult.OkClosed(header.Id);
    }

    public async Task<IvPeriodCloseResult> ReopenAsync(
        IvPeriodCloseRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return IvPeriodCloseResult.Fail("Save request is required.");
        }

        var context = await ResolveAsync(MenuCodes.InventoryPeriodClose, cancellationToken);
        if (!context.Succeeded)
        {
            return IvPeriodCloseResult.Fail(context.Error!);
        }

        if (!await _accessRights.CanAsync(context.MenuCode!, PermissionCodes.Reopen, cancellationToken))
        {
            return IvPeriodCloseResult.Fail("Not authorized.");
        }

        if (request.Id <= 0)
        {
            return IvPeriodCloseResult.Fail("Period is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ReopenReason))
        {
            return IvPeriodCloseResult.Fail("A reopen reason is required.");
        }

        var company = context.CompanyCode!;
        var branch = context.BranchCode!;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await new ErpWeb.Core.StockLedger.BranchStockTransactionLock()
            .AcquireAsync(db, company, branch, cancellationToken);

        var header = await db.IvPeriodCloseHdrs
            .FirstOrDefaultAsync(
                x => x.Id == request.Id && x.CompanyCode == company && x.BranchCode == branch,
                cancellationToken);
        if (header is null)
        {
            return IvPeriodCloseResult.Fail("Closed period was not found.");
        }

        if (!string.Equals(header.Status, IvPeriodCloseStatuses.Closed, StringComparison.OrdinalIgnoreCase))
        {
            return IvPeriodCloseResult.Fail("Only a CLOSED period can be reopened.");
        }

        // D8: reopen reverses in strict order — a later period must be reopened first.
        var laterClosed = await db.IvPeriodCloseHdrs.AsNoTracking()
            .AnyAsync(
                x => x.CompanyCode == company && x.BranchCode == branch
                     && x.Status == IvPeriodCloseStatuses.Closed
                     && x.PeriodFrom > header.PeriodTo,
                cancellationToken);
        if (laterClosed)
        {
            return IvPeriodCloseResult.Fail(
                "A later period is still closed. Reopen the later period first.");
        }

        if (!TryApplyRowVersion(db, header, request.RowVersion, out var tokenError))
        {
            return IvPeriodCloseResult.Fail(tokenError!);
        }

        // Stamp the shape of what is about to be withdrawn, THEN delete the derived rows.
        var lineCount = await db.IvPeriodCloseBals
            .Where(x => x.PeriodCloseId == header.Id)
            .CountAsync(cancellationToken);
        var closingValue = await db.IvPeriodCloseBals
            .Where(x => x.PeriodCloseId == header.Id)
            .SumAsync(x => (decimal?)x.ClosingValue, cancellationToken) ?? 0m;

        var lines = await db.IvPeriodCloseBals
            .Where(x => x.PeriodCloseId == header.Id)
            .ToListAsync(cancellationToken);
        db.IvPeriodCloseBals.RemoveRange(lines);

        var now = _currentDate.Now;
        var uid = Truncate(CurrentUserId(), 10);
        header.Status = IvPeriodCloseStatuses.Reopened;
        header.ReopenCount += 1;
        header.ReopenedBy = uid;
        header.ReopenedOn = now;
        header.ReopenReason = TruncateOptional(request.ReopenReason, 250);
        header.LastReopenLineCount = lineCount;
        header.LastReopenClosingValue = closingValue;
        header.ModifiedDate = now;
        header.ModifiedBy = uid;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return IvPeriodCloseResult.Fail(ConcurrencyMessage);
        }

        _logger.LogInformation(
            "Period reopened. Company={Company} Branch={Branch} Period={PeriodFrom:yyyy-MM}..{PeriodTo:yyyy-MM}",
            company, branch, header.PeriodFrom, header.PeriodTo);

        return IvPeriodCloseResult.OkClosed(header.Id);
    }

    // ── Inquiry ───────────────────────────────────────────────────────────────────────────────────

    public async Task<IvPeriodCloseResult> InquiryAsync(int periodCloseId, CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(MenuCodes.InventoryPeriodCloseInq, cancellationToken);
        if (!context.Succeeded)
        {
            return IvPeriodCloseResult.Fail(context.Error!);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var header = await db.IvPeriodCloseHdrs.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == periodCloseId && x.CompanyCode == context.CompanyCode && x.BranchCode == context.BranchCode,
                cancellationToken);
        if (header is null)
        {
            return IvPeriodCloseResult.Fail("Closed period was not found.");
        }

        var lines = await db.IvPeriodCloseBals.AsNoTracking()
            .Where(x => x.PeriodCloseId == header.Id)
            .OrderBy(x => x.ICode).ThenBy(x => x.WhCode).ThenBy(x => x.LocCode).ThenBy(x => x.LotNo).ThenBy(x => x.IStatus)
            .ToListAsync(cancellationToken);

        return IvPeriodCloseResult.OkInquiry(new IvPeriodCloseInquiryPage
        {
            Header = MapHeader(header),
            Lines = lines.Select(MapBal).ToList()
        });
    }

    public async Task<IvPeriodCloseResult> ListInquiryPeriodsAsync(CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(MenuCodes.InventoryPeriodCloseInq, cancellationToken);
        if (!context.Succeeded)
        {
            return IvPeriodCloseResult.Fail(context.Error!);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var headers = await db.IvPeriodCloseHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode && x.BranchCode == context.BranchCode)
            .OrderByDescending(x => x.PeriodFrom)
            .ToListAsync(cancellationToken);

        return IvPeriodCloseResult.OkList(new IvPeriodCloseListPage
        {
            Headers = headers.Select(MapHeader).ToList(),
            Next = new IvPeriodCloseNextPeriod()
        });
    }

    // ── Fixture helpers ──────────────────────────────────────────────────────────────────────────

    private async Task<IvInquiryScopeContext> ResolveAsync(string menuCode, CancellationToken cancellationToken) =>
        await IvInquiryScopeResolver.ResolveAsync(_tenant, _accessRights, menuCode, KnownMenus, cancellationToken);

    private string CurrentUserId() => _tenant.TryBranchScope()?.UserId ?? string.Empty;

    private async Task<IvPeriodCloseDocument> BuildDocumentAsync(
        AppDbContext db,
        IvPeriodCloseHdr header,
        CancellationToken cancellationToken)
    {
        var blockingBatches = header.Status == IvPeriodCloseStatuses.Closed
            ? []
            : await db.IvTrxBatches.AsNoTracking()
                .Where(x => x.CompanyCode == header.CompanyCode && x.BranchCode == header.BranchCode
                            && x.BatchStatus == IvBatchStatuses.New
                            && x.TrxDtTime >= header.PeriodFrom && x.TrxDtTime < header.PeriodTo.AddDays(1))
                .Select(x => x.BatchNo)
                .OrderBy(x => x)
                .ToListAsync(cancellationToken);

        var lines = await db.IvPeriodCloseBals.AsNoTracking()
            .Where(x => x.PeriodCloseId == header.Id)
            .OrderBy(x => x.ICode).ThenBy(x => x.WhCode).ThenBy(x => x.LocCode).ThenBy(x => x.LotNo).ThenBy(x => x.IStatus)
            .ToListAsync(cancellationToken);

        return new IvPeriodCloseDocument
        {
            Id = header.Id,
            Header = MapHeader(header),
            BlockingNewBatches = blockingBatches
                .Select(x => new IvPeriodClosePrecondition { Code = "NEW_BATCH", Message = $"Batch {x} is unposted and dated inside the period." })
                .ToList(),
            Lines = lines.Select(MapBal).ToList()
        };
    }

    private static IvPeriodCloseHeaderRow MapHeader(IvPeriodCloseHdr h) =>
        new()
        {
            Id = h.Id,
            CompanyCode = h.CompanyCode,
            BranchCode = h.BranchCode,
            PeriodFrom = h.PeriodFrom,
            PeriodTo = h.PeriodTo,
            Status = h.Status,
            ClosedBy = h.ClosedBy,
            ClosedOn = h.ClosedOn,
            ReopenCount = h.ReopenCount,
            ReopenedBy = h.ReopenedBy,
            ReopenedOn = h.ReopenedOn,
            ReopenReason = h.ReopenReason,
            LineCount = h.LineCount,
            SkippedZeroSlices = h.SkippedZeroSlices,
            OpeningAdjustSlices = h.OpeningAdjustSlices,
            CarryForwardMismatchSlices = h.CarryForwardMismatchSlices,
            CurrentBalanceCheckApplies = h.CurrentBalanceCheckApplies,
            CurrentBalanceMismatchSlices = h.CurrentBalanceMismatchSlices,
            TotalOpeningValue = h.TotalOpeningValue,
            TotalInValue = h.TotalInValue,
            TotalOutValue = h.TotalOutValue,
            TotalClosingValue = h.TotalClosingValue,
            LastReopenLineCount = h.LastReopenLineCount,
            LastReopenClosingValue = h.LastReopenClosingValue,
            UnpostedBatchCount = h.UnpostedBatchCount,
            ReconcileFindingCount = h.ReconcileFindingCount,
            Remark = h.Remark,
            RowVersion = h.RowVersion.Length == 0 ? string.Empty : Convert.ToBase64String(h.RowVersion),
            CreatedDate = h.CreatedDate,
            CreatedBy = h.CreatedBy,
            ModifiedDate = h.ModifiedDate,
            ModifiedBy = h.ModifiedBy
        };

    private static IvPeriodCloseBalRow MapBal(IvPeriodCloseBal b) =>
        new()
        {
            Id = b.Id,
            ICode = b.ICode,
            WhCode = b.WhCode,
            LocCode = b.LocCode,
            LotNo = b.LotNo,
            IStatus = b.IStatus,
            OpeningQty = b.OpeningQty,
            OpeningAdjustQty = b.OpeningAdjustQty,
            InQty = b.InQty,
            OutQty = b.OutQty,
            AdjustNetQty = b.AdjustNetQty,
            ClosingQty = b.ClosingQty,
            StdUom = b.StdUom,
            UnitPrice = b.UnitPrice,
            ClosingValue = b.ClosingValue,
            LegCount = b.LegCount,
            CarryForwardOk = b.CarryForwardOk,
            CurrentBalanceDelta = b.CurrentBalanceDelta,
            CreatedDate = b.CreatedDate,
            CreatedBy = b.CreatedBy
        };

    private static IvPeriodCloseNextPeriod ComputeNextPeriod(
        IReadOnlyList<IvPeriodCloseHdr> headers,
        DateTime today)
    {
        var closed = headers
            .Where(x => string.Equals(x.Status, IvPeriodCloseStatuses.Closed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (closed.Count > 0)
        {
            var last = closed.MaxBy(x => x.PeriodTo)!;
            var from = last.PeriodTo.AddDays(1);
            return new IvPeriodCloseNextPeriod
            {
                PeriodFrom = from,
                PeriodTo = EndOfMonth(from),
                HasPriorClose = true
            };
        }

        // No prior close: suggest the previous complete month (a partial month cannot be closed, D7).
        var firstOfThisMonth = new DateTime(today.Year, today.Month, 1);
        var periodTo = firstOfThisMonth.AddDays(-1);
        return new IvPeriodCloseNextPeriod
        {
            PeriodFrom = new DateTime(periodTo.Year, periodTo.Month, 1),
            PeriodTo = periodTo,
            HasPriorClose = false
        };
    }

    private static DateTime EndOfMonth(DateTime date) =>
        new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));

    private static string FormatBatchList(IReadOnlyList<int> batchNos) =>
        batchNos.Count <= 5
            ? string.Join(", ", batchNos)
            : $"{string.Join(", ", batchNos.Take(5))}, … (+{batchNos.Count - 5} more)";

    private static string FormatD11Refusal(IReadOnlyList<IvPeriodCloseSnapshotLine> mismatches)
    {
        var named = mismatches.Take(5).Select(m => m.Slice.ToString());
        var tail = mismatches.Count > 5 ? $" (+{mismatches.Count - 5} more)" : string.Empty;
        return "Cannot close: the pile and the ledger disagree on "
            + $"{mismatches.Count} slice(s) — {string.Join("; ", named)}{tail}. "
            + "Investigate the reconciliation findings before closing.";
    }

    private static bool TryApplyRowVersion(
        AppDbContext db,
        IvPeriodCloseHdr header,
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

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string? TruncateOptional(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? null
        : value.Length <= maxLength ? value
        : value[..maxLength];

    private static async Task AppendQuantityPeriodSnapshotAsync(
        AppDbContext db,
        string company,
        string branch,
        DateTime periodFrom,
        IvPeriodCloseSnapshotResult snapshot,
        string userId,
        CancellationToken cancellationToken)
    {
        var epoch = await db.StockLedgerEpochs.AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.CompanyCode == company
                && x.BranchCode == branch
                && x.Status == StockLedgerEpochStatuses.Active,
                cancellationToken);
        if (epoch is null)
            return;

        var periodKey = periodFrom.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var revision = 1 + (await db.StockPeriodSnapshotHdrs
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.PeriodKey == periodKey)
            .Select(x => (int?)x.Revision)
            .MaxAsync(cancellationToken) ?? 0);
        var watermark = await db.StockPostings
            .Where(x => x.CompanyCode == company
                && x.BranchCode == branch
                && x.LedgerEpochId == epoch.Id
                && x.SealedAtUtc != null)
            .Select(x => (long?)x.PostingSequence)
            .MaxAsync(cancellationToken) ?? 0L;

        var productionBalances = await db.ProductionBalLots.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.BaseQty > 0m)
            .OrderBy(x => x.Uid)
            .Select(x => new { x.Uid, x.ItemCode, x.BaseUom, x.BaseQty })
            .ToListAsync(cancellationToken);

        var header = new StockPeriodSnapshotHdr
        {
            CompanyCode = company,
            BranchCode = branch,
            LedgerEpochId = epoch.Id,
            PeriodKey = periodKey,
            Revision = revision,
            PostingSequenceWatermark = watermark,
            QuantityStatus = "SEALED",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = userId
        };
        foreach (var line in snapshot.Lines)
        {
            header.Lines.Add(new StockPeriodSnapshotLine
            {
                LedgerArea = "INVENTORY",
                StockIdentity = $"{line.Slice.ICode}|{line.Slice.WhCode}|{line.Slice.LocCode}|{line.Slice.LotNo}|{line.Slice.IStatus}",
                ItemCode = line.Slice.ICode,
                BaseUom = line.StdUom ?? string.Empty,
                BaseQty = line.ClosingQty
            });
        }

        foreach (var balance in productionBalances)
        {
            header.Lines.Add(new StockPeriodSnapshotLine
            {
                LedgerArea = "PRODUCTION",
                StockIdentity = balance.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ItemCode = balance.ItemCode,
                BaseUom = balance.BaseUom,
                BaseQty = balance.BaseQty
            });
        }

        header.SourceDataHash = StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                company, branch, periodKey, revision, watermark,
                lines = header.Lines.OrderBy(x => x.LedgerArea, StringComparer.Ordinal)
                    .ThenBy(x => x.StockIdentity, StringComparer.Ordinal)
                    .Select(x => new { x.LedgerArea, x.StockIdentity, x.ItemCode, x.BaseUom, x.BaseQty })
                    .ToArray()
            })));

        db.StockPeriodSnapshotHdrs.Add(header);
    }
}
