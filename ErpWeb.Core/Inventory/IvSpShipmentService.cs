using ErpWeb.Core.Numbering;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed class IvSpShipmentService : IIvSpShipmentService
{
    private readonly IIvStockPostingRepository _postingRepo;
    private readonly IIvStockTransactionRepository _transactions;
    private readonly IRunningNumberService _runningNumbers;

    public IvSpShipmentService(
        IIvStockPostingRepository postingRepo,
        IIvStockTransactionRepository transactions,
        IRunningNumberService runningNumbers)
    {
        _postingRepo = postingRepo;
        _transactions = transactions;
        _runningNumbers = runningNumbers;
    }

    public async Task<IvSpShipmentResult> CreateOrReplaceShipmentAsync(
        AppDbContext db,
        IvSpCreateOrReplaceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(command);

        var company = command.CompanyCode.Trim();
        var branch = command.BranchCode.Trim();
        var location = command.LocationCode.Trim();
        var invNo = command.InvNo.Trim();
        var invDate = command.InvDate.Date;
        var uid = Truncate(command.UserId, 10);
        var now = DateTime.UtcNow;

        var required = command.RequiredLines
            .Where(x => IvSpFifoEligibility.IsShipmentRequired(x.StockControl, x.StdQty))
            .OrderBy(x => x.Line)
            .ToList();

        var batch = await _postingRepo.LockSpBatchByInvoiceRefAsync(db, company, branch, invNo, cancellationToken);
        if (batch is not null
            && string.Equals(batch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            return IvSpShipmentResult.Fail("Posted shipment cannot be rebuilt.", IvSpShipmentErrorKind.BusinessRule);
        }

        IReadOnlyList<IvTrxBatchDetail> existingDetails = [];
        if (batch is not null)
        {
            existingDetails = await _postingRepo.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
            if (existingDetails.Count > 0 && !command.OverwriteExisting)
            {
                return new IvSpShipmentResult
                {
                    Succeeded = false,
                    ErrorKind = IvSpShipmentErrorKind.BusinessRule,
                    ErrorMessage = "ST000001: Shipment already exists. Confirm to overwrite.",
                    BatchId = batch.Id,
                    BatchNo = batch.BatchNo,
                    Lots = existingDetails.OrderBy(x => x.TrxLineNo).Select(MapLot).ToList()
                };
            }
        }

        if (required.Count == 0)
        {
            if (batch is not null)
            {
                await LockReleasedBalancesAsync(db, company, branch, existingDetails, cancellationToken);
                db.IvTrxBatchDetails.RemoveRange(existingDetails);
                db.IvTrxBatches.Remove(batch);
            }

            return IvSpShipmentResult.Ok(0, 0, [], []);
        }

        var releasedIds = existingDetails
            .Where(x => x.FromBalLocId is > 0)
            .Select(x => x.FromBalLocId!.Value)
            .Distinct()
            .ToList();

        var candidateIds = new HashSet<int>();
        foreach (var line in required)
        {
            var piles = await DiscoverFifoPilesAsync(
                db, company, branch, location, line.ICode, line.FrWarehouse, invDate, cancellationToken);
            foreach (var pile in piles)
            {
                candidateIds.Add(pile.Id);
            }
        }

        var allIds = releasedIds.Concat(candidateIds).Distinct().ToList();
        var locked = await LockBalancesOrderedAsync(db, company, branch, allIds, cancellationToken);
        if (locked is null)
        {
            return IvSpShipmentResult.Fail("A stock balance could not be locked.", IvSpShipmentErrorKind.Unexpected);
        }

        var excludeDetailIds = existingDetails.Select(x => x.Id).ToHashSet();
        var remainingByBalLoc = new Dictionary<int, decimal>();
        foreach (var (id, row) in locked)
        {
            var reserved = await SumOtherNewSpReservationsAsync(
                db, company, branch, location, id, excludeDetailIds, cancellationToken);
            remainingByBalLoc[id] = IvQty.Round(row.StdQty - reserved);
        }

        var allocation = IvSpShipmentAllocator.Allocate(
            required,
            locked,
            remainingByBalLoc,
            company,
            branch,
            location,
            invDate);

        if (batch is null)
        {
            var batchNo = await _runningNumbers.GetNextAsync(db, company, RunningNumberKeys.IvBatch, cancellationToken);
            batch = new IvTrxBatch
            {
                CompanyCode = company,
                BranchCode = branch,
                BatchNo = batchNo,
                TrxDtTime = invDate,
                TrxType = IvTrxTypes.SalesOut,
                BatchStatus = IvBatchStatuses.New,
                RefNo = invNo,
                LocationCode = location,
                CreatedDate = now,
                CreatedBy = uid
            };
            await _transactions.InsertAsync(db, batch, cancellationToken);
        }
        else
        {
            batch.TrxDtTime = invDate;
            batch.ModifiedDate = now;
            batch.ModifiedBy = uid;
            batch.LocationCode = location;
            db.IvTrxBatchDetails.RemoveRange(existingDetails);
            command.AfterSpDelete?.Invoke();
        }

        short trxLine = 1;
        var lotResults = new List<IvSpLotResult>();
        foreach (var take in allocation.Takes)
        {
            var pile = locked[take.FromBalLocId];
            var line = required.First(x => x.Line == take.SoLineNo);
            batch.Details.Add(new IvTrxBatchDetail
            {
                CompanyCode = company,
                BranchCode = branch,
                BatchNo = batch.BatchNo,
                TrxLineNo = trxLine++,
                TrxType = IvTrxTypes.SalesOut,
                ICode = line.ICode,
                IDesc = line.IDesc,
                FrWarehouse = pile.WhCode,
                FrLocation = pile.LocCode,
                FrLotNo = pile.LotNo ?? string.Empty,
                FrStdQty = take.FrStdQty,
                FrStdUom = line.StdUom,
                IStatus = pile.IStatus,
                FromBalLocId = pile.Id,
                InvNo = invNo,
                SoLineNo = (short)take.SoLineNo,
                LocationCode = location,
                UnitPrice = line.UnitPrice
            });
            lotResults.Add(new IvSpLotResult
            {
                SoLineNo = take.SoLineNo,
                FromBalLocId = pile.Id,
                ICode = line.ICode,
                FrWarehouse = pile.WhCode,
                FrLocation = pile.LocCode,
                FrLotNo = pile.LotNo,
                IStatus = pile.IStatus,
                FrStdQty = take.FrStdQty,
                CurrentAvailableQty = remainingByBalLoc.GetValueOrDefault(pile.Id),
                FailReason = IvSpLotFailReason.None
            });
        }

        return IvSpShipmentResult.Ok(batch.Id, batch.BatchNo, allocation.Lines, lotResults);
    }

    public async Task<IvSpShipmentResult> GetShipmentEditAsync(
        AppDbContext db,
        IvSpShipmentEditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);

        var company = query.CompanyCode.Trim();
        var branch = query.BranchCode.Trim();
        var location = query.LocationCode.Trim();
        var invNo = query.InvNo.Trim();

        var batch = await db.IvTrxBatches.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company
                && x.BranchCode == branch
                && x.TrxType == IvTrxTypes.SalesOut
                && x.RefNo == invNo)
            .SingleOrDefaultAsync(cancellationToken);

        var lots = new List<IvSpLotResult>();
        var seenBalLocIds = new HashSet<int>();
        HashSet<int>? excludeDetailIds = null;
        if (batch is not null)
        {
            var details = await db.IvTrxBatchDetails.AsNoTracking()
                .Where(x => x.BatchId == batch.Id && x.SoLineNo == query.SoLineNo)
                .OrderBy(x => x.TrxLineNo)
                .ToListAsync(cancellationToken);
            excludeDetailIds = details.Select(x => x.Id).ToHashSet();

            foreach (var d in details)
            {
                decimal? available = null;
                if (d.FromBalLocId is > 0)
                {
                    seenBalLocIds.Add(d.FromBalLocId.Value);
                    var bal = await db.IvBalLocs.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == d.FromBalLocId.Value, cancellationToken);
                    if (bal is not null)
                    {
                        var reserved = await SumOtherNewSpReservationsAsync(
                            db, company, branch, location, bal.Id, excludeDetailIds, cancellationToken);
                        available = IvQty.Round(bal.StdQty - reserved);
                    }
                }

                lots.Add(new IvSpLotResult
                {
                    SoLineNo = query.SoLineNo,
                    FromBalLocId = d.FromBalLocId,
                    ICode = d.ICode,
                    FrWarehouse = d.FrWarehouse,
                    FrLocation = d.FrLocation,
                    FrLotNo = d.FrLotNo,
                    IStatus = d.IStatus,
                    FrStdQty = IvQty.Round(d.FrStdQty ?? 0m),
                    CurrentAvailableQty = available,
                    FailReason = IvSpLotFailReason.None
                });
            }
        }

        var piles = await DiscoverFifoPilesAsync(
            db, company, branch, location, query.ICode, query.FrWarehouse, query.InvDate.Date, cancellationToken);
        foreach (var pile in piles)
        {
            if (!seenBalLocIds.Add(pile.Id))
            {
                continue;
            }

            var reserved = await SumOtherNewSpReservationsAsync(
                db, company, branch, location, pile.Id, excludeDetailIds, cancellationToken);
            var available = IvQty.Round(pile.StdQty - reserved);
            if (available <= 0m)
            {
                continue;
            }

            lots.Add(new IvSpLotResult
            {
                SoLineNo = query.SoLineNo,
                FromBalLocId = pile.Id,
                ICode = pile.ICode,
                FrWarehouse = pile.WhCode,
                FrLocation = pile.LocCode,
                FrLotNo = pile.LotNo,
                IStatus = pile.IStatus,
                FrStdQty = 0m,
                CurrentAvailableQty = available,
                FailReason = IvSpLotFailReason.None
            });
        }

        var requested = IvQty.Round(query.RequestedStdQty);
        if (lots.TrueForAll(x => x.FrStdQty <= 0m))
        {
            lots = PrefillFifoIssueQty(lots, requested);
        }

        var allocated = IvQty.Round(lots.Sum(x => x.FrStdQty));
        var line = new IvSpLineResult
        {
            SoLineNo = query.SoLineNo,
            Status = allocated == requested
                ? IvSpLineStatus.Allocated
                : allocated == 0m
                    ? IvSpLineStatus.NoStock
                    : IvSpLineStatus.Incomplete,
            RequestedStdQty = requested,
            AllocatedStdQty = allocated,
            CurrentAvailableQty = lots.Count > 0 ? lots[0].CurrentAvailableQty : null
        };

        return IvSpShipmentResult.Ok(batch?.Id ?? 0, batch?.BatchNo ?? 0, [line], lots);
    }

    public async Task<IvSpShipmentResult> ReplaceShipmentLineAsync(
        AppDbContext db,
        IvSpReplaceLineCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(command);

        var company = command.CompanyCode.Trim();
        var branch = command.BranchCode.Trim();
        var location = command.LocationCode.Trim();
        var invNo = command.InvNo.Trim();
        var invDate = command.InvDate.Date;
        var uid = Truncate(command.UserId, 10);
        var now = DateTime.UtcNow;
        var persistedStdQty = IvQty.Round(command.PersistedStdQty);
        var submitted = command.Lots ?? [];

        if (submitted.Select(x => x.FromBalLocId).Distinct().Count() != submitted.Count)
        {
            return FailReplace(
                "Duplicate lot on the same line is not allowed.",
                IvSpLotFailReason.DuplicateLot,
                command,
                submitted);
        }

        var submittedTotal = IvQty.Round(submitted.Sum(x => IvQty.Round(x.IssueQty)));
        if (submittedTotal != persistedStdQty)
        {
            return FailReplace(
                $"Submitted quantity {submittedTotal} must equal line standard quantity {persistedStdQty}.",
                IvSpLotFailReason.QtyDoesNotMatchLineStdQty,
                command,
                submitted);
        }

        if (submitted.Any(x => IvQty.Round(x.IssueQty) <= 0m))
        {
            return IvSpShipmentResult.Fail("Issue quantity must be greater than zero.", IvSpShipmentErrorKind.Validation);
        }

        var batch = await _postingRepo.LockSpBatchByInvoiceRefAsync(db, company, branch, invNo, cancellationToken);
        if (batch is null)
        {
            return IvSpShipmentResult.Fail("Add shipment before editing.", IvSpShipmentErrorKind.BusinessRule);
        }

        if (string.Equals(batch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            return IvSpShipmentResult.Fail("Posted shipment cannot be edited.", IvSpShipmentErrorKind.BusinessRule);
        }

        var allDetails = await _postingRepo.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
        var lineDetails = allDetails.Where(x => x.SoLineNo == command.SoLineNo).ToList();
        var releasedIds = lineDetails
            .Where(x => x.FromBalLocId is > 0)
            .Select(x => x.FromBalLocId!.Value)
            .Concat(submitted.Select(x => x.FromBalLocId))
            .Distinct()
            .ToList();

        var locked = await LockBalancesOrderedAsync(db, company, branch, releasedIds, cancellationToken);
        if (locked is null)
        {
            return IvSpShipmentResult.Fail("A stock balance could not be locked.", IvSpShipmentErrorKind.Unexpected);
        }

        var excludeDetailIds = lineDetails.Select(x => x.Id).ToHashSet();
        var evaluated = new List<IvSpLotResult>();
        foreach (var lot in submitted)
        {
            if (!locked.TryGetValue(lot.FromBalLocId, out var row))
            {
                evaluated.Add(SubmittedLotResult(command, lot, null, IvSpLotFailReason.LotNoLongerEligible));
                continue;
            }

            if (!IvSpFifoEligibility.MatchesCandidate(
                    row, company, branch, location, command.ICode, command.FrWarehouse, invDate))
            {
                evaluated.Add(SubmittedLotResult(command, lot, IvQty.Round(row.StdQty), IvSpLotFailReason.LotNoLongerEligible));
                continue;
            }

            var reservedOther = await SumOtherNewSpReservationsAsync(
                db, company, branch, location, lot.FromBalLocId, excludeDetailIds, cancellationToken);
            var available = IvQty.Round(row.StdQty - reservedOther);
            var issue = IvQty.Round(lot.IssueQty);
            if (issue > available)
            {
                evaluated.Add(SubmittedLotResult(
                    command,
                    lot,
                    available,
                    available <= 0m ? IvSpLotFailReason.StaleQuantity : IvSpLotFailReason.QtyExceedsAvailable));
                continue;
            }

            evaluated.Add(SubmittedLotResult(command, lot, available, IvSpLotFailReason.None));
        }

        if (evaluated.Any(x => x.FailReason != IvSpLotFailReason.None))
        {
            var first = evaluated.First(x => x.FailReason != IvSpLotFailReason.None);
            return new IvSpShipmentResult
            {
                Succeeded = false,
                ErrorKind = IvSpShipmentErrorKind.BusinessRule,
                ErrorMessage = first.FailReason switch
                {
                    IvSpLotFailReason.StaleQuantity => "Stock quantity changed. Reload and try again.",
                    IvSpLotFailReason.LotNoLongerEligible => "One or more lots are no longer eligible.",
                    IvSpLotFailReason.QtyExceedsAvailable => "Issue quantity exceeds available stock.",
                    _ => "Shipment line could not be updated."
                },
                BatchId = batch.Id,
                BatchNo = batch.BatchNo,
                Lines =
                [
                    new IvSpLineResult
                    {
                        SoLineNo = command.SoLineNo,
                        Status = IvSpLineStatus.Incomplete,
                        RequestedStdQty = persistedStdQty,
                        AllocatedStdQty = 0m,
                        CurrentAvailableQty = first.CurrentAvailableQty
                    }
                ],
                Lots = evaluated
            };
        }

        db.IvTrxBatchDetails.RemoveRange(lineDetails);
        batch.ModifiedDate = now;
        batch.ModifiedBy = uid;

        var maxLine = allDetails.Count == 0 ? (short)0 : allDetails.Max(x => x.TrxLineNo);
        short trxLine = (short)(maxLine + 1);
        var lotResults = new List<IvSpLotResult>();
        foreach (var lot in submitted)
        {
            var pile = locked[lot.FromBalLocId];
            var qty = IvQty.Round(lot.IssueQty);
            batch.Details.Add(new IvTrxBatchDetail
            {
                CompanyCode = company,
                BranchCode = branch,
                BatchNo = batch.BatchNo,
                TrxLineNo = trxLine++,
                TrxType = IvTrxTypes.SalesOut,
                ICode = command.ICode,
                IDesc = command.IDesc,
                FrWarehouse = pile.WhCode,
                FrLocation = pile.LocCode,
                FrLotNo = pile.LotNo ?? string.Empty,
                FrStdQty = qty,
                FrStdUom = command.StdUom,
                IStatus = pile.IStatus,
                FromBalLocId = pile.Id,
                InvNo = invNo,
                SoLineNo = (short)command.SoLineNo,
                LocationCode = location,
                UnitPrice = command.UnitPrice
            });
            lotResults.Add(new IvSpLotResult
            {
                SoLineNo = command.SoLineNo,
                FromBalLocId = pile.Id,
                ICode = command.ICode,
                FrWarehouse = pile.WhCode,
                FrLocation = pile.LocCode,
                FrLotNo = pile.LotNo,
                IStatus = pile.IStatus,
                FrStdQty = qty,
                CurrentAvailableQty = evaluated.First(x => x.FromBalLocId == pile.Id).CurrentAvailableQty,
                FailReason = IvSpLotFailReason.None
            });
        }

        return IvSpShipmentResult.Ok(
            batch.Id,
            batch.BatchNo,
            [
                new IvSpLineResult
                {
                    SoLineNo = command.SoLineNo,
                    Status = IvSpLineStatus.Allocated,
                    RequestedStdQty = persistedStdQty,
                    AllocatedStdQty = persistedStdQty
                }
            ],
            lotResults);
    }

    public async Task<IvSpValidatePostResult> ValidateShipmentForPostAsync(
        AppDbContext db,
        IvSpValidatePostQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);

        var company = query.CompanyCode.Trim();
        var branch = query.BranchCode.Trim();
        var location = query.LocationCode.Trim();
        var invNo = query.InvNo.Trim();
        var invDate = query.InvDate.Date;
        var batch = query.Batch;
        var details = query.Details;

        if (!string.Equals(batch.CompanyCode, company, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(batch.BranchCode, branch, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(batch.LocationCode ?? string.Empty, location, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(batch.TrxType, IvTrxTypes.SalesOut, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(batch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(batch.RefNo, invNo, StringComparison.OrdinalIgnoreCase))
        {
            return IvSpValidatePostResult.Fail("Shipment batch does not match the invoice.");
        }

        if (details.Any(d => d.BatchId != batch.Id))
        {
            return IvSpValidatePostResult.Fail("Shipment has orphan detail rows.");
        }

        if (details.Any(d =>
                !string.Equals(d.CompanyCode, company, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(d.BranchCode, branch, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(d.LocationCode ?? string.Empty, location, StringComparison.OrdinalIgnoreCase)))
        {
            return IvSpValidatePostResult.Fail("Shipment detail tenant does not match.");
        }

        var required = query.InvoiceLines
            .Where(x => IvSpFifoEligibility.IsShipmentRequired(x.StockControl, x.StdQty))
            .OrderBy(x => x.Line)
            .ToList();

        foreach (var line in required)
        {
            var lineDetails = details
                .Where(d => d.SoLineNo == line.Line && string.Equals(d.InvNo, invNo, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var shipped = IvQty.Round(lineDetails.Sum(d => d.FrStdQty ?? 0m));
            var needed = IvQty.Round(line.StdQty);
            if (shipped != needed)
            {
                return IvSpValidatePostResult.Fail(
                    $"Shipment incomplete for line {line.Line} (shipped {shipped}, required {needed}).");
            }

            if (lineDetails.GroupBy(d => d.FromBalLocId).Any(g => g.Count() > 1))
            {
                return IvSpValidatePostResult.Fail(
                    $"Shipment line {line.Line} has duplicate lot assignments.");
            }

            foreach (var detail in lineDetails)
            {
                if (detail.FromBalLocId is not > 0
                    || !query.LockedBalances.TryGetValue(detail.FromBalLocId.Value, out var lockedRow))
                {
                    return IvSpValidatePostResult.Fail(
                        $"Source balance Id {detail.FromBalLocId} was not found for this company/branch.");
                }

                if (!IvSpFifoEligibility.MatchesPersistedDetail(
                        lockedRow,
                        detail,
                        company,
                        branch,
                        location,
                        line.ICode ?? string.Empty,
                        line.FrWarehouse ?? string.Empty,
                        invDate))
                {
                    return IvSpValidatePostResult.Fail(
                        $"Shipment lot on line {line.Line} is no longer eligible.");
                }
            }
        }

        var thisBatchDetailIds = details.Select(x => x.Id).ToHashSet();
        var byBalLoc = details
            .Where(d => d.FromBalLocId is > 0)
            .GroupBy(d => d.FromBalLocId!.Value)
            .ToDictionary(g => g.Key, g => IvQty.Round(g.Sum(x => x.FrStdQty ?? 0m)));

        foreach (var (balLocId, thisQty) in byBalLoc)
        {
            if (!query.LockedBalances.TryGetValue(balLocId, out var lockedRow))
            {
                return IvSpValidatePostResult.Fail(
                    $"Source balance Id {balLocId} was not found for this company/branch.");
            }

            var other = await SumOtherNewSpReservationsAsync(
                db, company, branch, location, balLocId, thisBatchDetailIds, cancellationToken);
            var available = IvQty.Round(lockedRow.StdQty - other);
            if (thisQty > available)
            {
                return IvSpValidatePostResult.Fail(
                    $"Insufficient quantity on balance Id {balLocId} (on hand {lockedRow.StdQty}, reserved by others {other}, required {thisQty}).");
            }
        }

        return IvSpValidatePostResult.Ok();
    }

    public async Task ReleaseShipmentReservationAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string locationCode,
        string invNo,
        bool removeBatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (invNo ?? string.Empty).Trim();

        var batch = await _postingRepo.LockSpBatchByInvoiceRefAsync(db, company, branch, no, cancellationToken);
        if (batch is null)
        {
            return;
        }

        var details = await _postingRepo.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
        await LockReleasedBalancesAsync(db, company, branch, details, cancellationToken);
        db.IvTrxBatchDetails.RemoveRange(details);
        if (removeBatch)
        {
            db.IvTrxBatches.Remove(batch);
        }
    }

    private async Task LockReleasedBalancesAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyList<IvTrxBatchDetail> details,
        CancellationToken cancellationToken)
    {
        var ids = details
            .Where(x => x.FromBalLocId is > 0)
            .Select(x => x.FromBalLocId!.Value)
            .Distinct()
            .ToList();
        _ = await LockBalancesOrderedAsync(db, company, branch, ids, cancellationToken);
    }

    private async Task<Dictionary<int, IvBalLocLockResult>?> LockBalancesOrderedAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyList<int> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<int, IvBalLocLockResult>();
        }

        var rows = await db.IvBalLocs.AsNoTracking()
            .Where(x => ids.Contains(x.Id) && x.CompanyCode == company && x.BranchCode == branch)
            .ToListAsync(cancellationToken);

        var foundIds = rows.Select(x => x.Id).ToHashSet();
        var orderedIds = rows
            .Select(x => new
            {
                x.Id,
                Slice = IvStockSliceKey.Create(x.CompanyCode, x.BranchCode, x.ICode, x.WhCode, x.LocCode, x.LotNo, x.IStatus)
            })
            .OrderBy(x => x.Slice)
            .ThenBy(x => x.Id)
            .Select(x => x.Id)
            .ToList();

        foreach (var missing in ids.Where(id => !foundIds.Contains(id)).OrderBy(x => x))
        {
            orderedIds.Add(missing);
        }

        var locked = new Dictionary<int, IvBalLocLockResult>();
        foreach (var id in orderedIds)
        {
            var row = await _postingRepo.LockBalLocByIdForTenantAsync(db, id, company, branch, cancellationToken);
            if (row is null)
            {
                // Released ids that vanished are skipped; missing candidate that was discovered fails.
                if (!foundIds.Contains(id))
                {
                    continue;
                }

                return null;
            }

            locked[id] = row;
        }

        return locked;
    }

    private static Task<List<IvBalLoc>> DiscoverFifoPilesAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string tenantLocation,
        string iCode,
        string warehouse,
        DateTime invDate,
        CancellationToken cancellationToken) =>
        db.IvBalLocs
            .AsNoTracking()
            .Where(x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.ICode == iCode
                && x.WhCode == warehouse
                && (x.LocationCode == tenantLocation
                    || x.LocationCode == null
                    || x.LocationCode == "")
                && x.StdQty > 0
                && x.IStatus == IvItemStatuses.Active
                && x.TransDate != null
                && x.TransDate < invDate.Date.AddDays(1))
            .OrderBy(x => x.TransDate)
            .ThenBy(x => x.LotNo)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

    private static async Task<decimal> SumOtherNewSpReservationsAsync(
        AppDbContext db,
        string company,
        string branch,
        string location,
        int balLocId,
        HashSet<int>? excludeDetailIds,
        CancellationToken cancellationToken)
    {
        var query =
            from detail in db.IvTrxBatchDetails.AsNoTracking()
            join batch in db.IvTrxBatches.AsNoTracking() on detail.BatchId equals batch.Id
            where detail.CompanyCode == company
                  && detail.BranchCode == branch
                  && detail.LocationCode == location
                  && detail.FromBalLocId == balLocId
                  && batch.CompanyCode == company
                  && batch.BranchCode == branch
                  && batch.LocationCode == location
                  && batch.TrxType == IvTrxTypes.SalesOut
                  && batch.BatchStatus == IvBatchStatuses.New
            select detail;

        if (excludeDetailIds is { Count: > 0 })
        {
            query = query.Where(d => !excludeDetailIds.Contains(d.Id));
        }

        var sum = await query.SumAsync(d => d.FrStdQty ?? 0m, cancellationToken);
        return IvQty.Round(sum);
    }

    private static List<IvSpLotResult> PrefillFifoIssueQty(List<IvSpLotResult> lots, decimal requested)
    {
        var remaining = requested;
        var filled = new List<IvSpLotResult>(lots.Count);
        foreach (var lot in lots)
        {
            var avail = IvQty.Round(Math.Max(0m, lot.CurrentAvailableQty ?? 0m));
            var take = remaining > 0m ? IvQty.Round(Math.Min(remaining, avail)) : 0m;
            filled.Add(new IvSpLotResult
            {
                SoLineNo = lot.SoLineNo,
                FromBalLocId = lot.FromBalLocId,
                ICode = lot.ICode,
                FrWarehouse = lot.FrWarehouse,
                FrLocation = lot.FrLocation,
                FrLotNo = lot.FrLotNo,
                IStatus = lot.IStatus,
                FrStdQty = take,
                CurrentAvailableQty = lot.CurrentAvailableQty,
                FailReason = lot.FailReason
            });
            remaining = IvQty.Round(remaining - take);
        }

        return filled;
    }

    private static IvSpLotResult MapLot(IvTrxBatchDetail d) =>
        new()
        {
            SoLineNo = d.SoLineNo ?? 0,
            FromBalLocId = d.FromBalLocId,
            ICode = d.ICode,
            FrWarehouse = d.FrWarehouse,
            FrLocation = d.FrLocation,
            FrLotNo = d.FrLotNo,
            IStatus = d.IStatus,
            FrStdQty = IvQty.Round(d.FrStdQty ?? 0m),
            FailReason = IvSpLotFailReason.None
        };

    private static IvSpShipmentResult FailReplace(
        string message,
        IvSpLotFailReason reason,
        IvSpReplaceLineCommand command,
        IReadOnlyList<IvSpSubmittedLot> submitted) =>
        new()
        {
            Succeeded = false,
            ErrorKind = IvSpShipmentErrorKind.Validation,
            ErrorMessage = message,
            Lines =
            [
                new IvSpLineResult
                {
                    SoLineNo = command.SoLineNo,
                    Status = IvSpLineStatus.Incomplete,
                    RequestedStdQty = IvQty.Round(command.PersistedStdQty),
                    AllocatedStdQty = 0m
                }
            ],
            Lots = submitted.Select(lot => SubmittedLotResult(command, lot, null, reason)).ToList()
        };

    private static IvSpLotResult SubmittedLotResult(
        IvSpReplaceLineCommand command,
        IvSpSubmittedLot lot,
        decimal? available,
        IvSpLotFailReason reason) =>
        new()
        {
            SoLineNo = command.SoLineNo,
            FromBalLocId = lot.FromBalLocId,
            ICode = command.ICode,
            FrWarehouse = command.FrWarehouse,
            FrStdQty = IvQty.Round(lot.IssueQty),
            CurrentAvailableQty = available,
            FailReason = reason
        };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
