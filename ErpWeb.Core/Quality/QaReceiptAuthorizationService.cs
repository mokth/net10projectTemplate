using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Quality;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Quality;

public sealed class QaReceiptAuthorizationService : IQaReceiptAuthorization
{
    public async Task<string?> ClaimGoodsReceiptAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string trxType,
        IvTrxBatch batch,
        IReadOnlyList<IvTrxBatchDetail> details,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(trxType, IvTrxTypes.NonStockGoodsReceive, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var incoming = await PrepareIncomingAsync(db, cancellationToken);
        if (incoming.Skip)
        {
            return null;
        }

        if (incoming.Error is not null)
        {
            return incoming.Error;
        }

        var policy = await QaPolicyService.LoadPublishedAsync(db, companyCode, cancellationToken);
        if (policy is null || !policy.IncomingInspectionEnabled)
        {
            return null;
        }

        _ = branchCode;
        _ = batch;
        _ = details;
        return QaReadinessMessages.IncomingFrozen;
    }

    public async Task<string?> RestoreGoodsReceiptAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        IReadOnlyList<IvTrxBatchDetail> details,
        CancellationToken cancellationToken = default)
    {
        var incoming = await PrepareIncomingAsync(db, cancellationToken);
        if (incoming.Skip)
        {
            return null;
        }

        if (incoming.Error is not null)
        {
            return incoming.Error;
        }

        var posted = await db.QaReceiptAllocations
            .Include(x => x.Grant)
            .ThenInclude(x => x.Case)
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && x.BatchNo == batchNo
                        && x.Status == QaReceiptAllocationStatuses.Posted)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        if (posted.Count == 0)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var cases = new Dictionary<long, QaCase>();
        foreach (var row in posted)
        {
            row.Status = QaReceiptAllocationStatuses.Reversed;
            row.ReversedAtUtc = now;
            cases[row.Grant.Case.Uid] = row.Grant.Case;
        }

        foreach (var qaCase in cases.Values.OrderBy(x => x.Uid))
        {
            var reopened = string.Equals(qaCase.Status, QaCaseStatuses.Closed, StringComparison.OrdinalIgnoreCase);
            if (reopened)
            {
                qaCase.Status = QaCaseStatuses.Approved;
                qaCase.ModifiedDate = now;
                qaCase.ModifiedBy = "SYSTEM";
            }

            db.QaAuditEvents.Add(new QaAuditEvent
            {
                CaseId = qaCase.Uid,
                EventType = reopened ? QaAuditEvents.ReopenedForReissue : QaAuditEvents.AllocationReversed,
                AtUtc = now,
                ByUser = "SYSTEM",
                Payload = reopened
                    ? $"Goods receipt {batchNo} was rolled back. Accepted quantity is available again."
                    : $"Goods receipt {batchNo} quality allocation reversed."
            });
        }

        _ = details;
        return null;
    }

    private static async Task<QaFeatureDecision> PrepareIncomingAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var ready = await QaSchemaReadiness.EvaluateAsync(db, cancellationToken);
        return ready.ForFeature(ready.Incoming);
    }

    private async Task<string?> ClaimLineAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string deliveryNote,
        ReceiptNeed need,
        CancellationToken cancellationToken)
    {
        var poNo = (need.Detail.PoNo ?? string.Empty).Trim();
        var itemCode = (need.Detail.ICode ?? string.Empty).Trim();
        var uom = (need.Detail.ToPurUom ?? need.Detail.ToStdUom ?? string.Empty).Trim();
        var lotNo = (need.Detail.ToLotNo ?? string.Empty).Trim();
        var candidates = await db.QaReceiptGrants
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && x.Status == QaReceiptGrantStatuses.Open
                        && x.PoNo == poNo
                        && x.PoRelNo == need.Detail.PoRelNo
                        && x.PoLineNo == need.Detail.PoLineNo
                        && x.ItemCode == itemCode
                        && x.Uom == uom)
            .OrderBy(x => x.Uid)
            .Select(x => x.Uid)
            .ToListAsync(cancellationToken);

        var remainingNeeded = need.Qty;
        var now = DateTime.UtcNow;
        foreach (var grantId in candidates)
        {
            if (remainingNeeded <= 0m)
            {
                break;
            }

            var grant = await LockGrantAsync(db, grantId, cancellationToken);
            if (grant is null || !string.Equals(grant.Status, QaReceiptGrantStatuses.Open, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(grant.DeliveryNote)
                && !string.Equals(grant.DeliveryNote.Trim(), deliveryNote, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(grant.LotNo)
                && !string.Equals(grant.LotNo.Trim(), lotNo, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var posted = IvQty.Round(grant.Allocations
                .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                .Sum(x => x.Qty));
            var remaining = IvQty.Round(grant.AcceptedQty - posted);
            if (remaining <= 0m)
            {
                continue;
            }

            var take = remaining >= remainingNeeded ? remainingNeeded : remaining;
            grant.Allocations.Add(new QaReceiptAllocation
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                Qty = take,
                Status = QaReceiptAllocationStatuses.Posted,
                BatchNo = need.Detail.BatchNo,
                BatchDetailId = need.Detail.Id,
                DocumentRevision = need.Detail.DocumentRevision,
                PostedAtUtc = now
            });
            remainingNeeded = IvQty.Round(remainingNeeded - take);
        }

        if (remainingNeeded > 0m)
        {
            return FriendlyShortage(poNo, need.Detail.PoLineNo, need.Qty, remainingNeeded);
        }

        return null;
    }

    private static async Task<QaReceiptGrant?> LockGrantAsync(
        AppDbContext db, long id, CancellationToken cancellationToken)
    {
        QaReceiptGrant? grant;
        if (db.Database.IsSqlServer())
        {
            grant = await db.QaReceiptGrants
                .FromSqlInterpolated($"SELECT * FROM dbo.QaReceiptGrant WITH (UPDLOCK, HOLDLOCK) WHERE UID = {id}")
                .AsTracking()
                .SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            grant = await db.QaReceiptGrants.FirstOrDefaultAsync(x => x.Uid == id, cancellationToken);
        }

        if (grant is not null)
        {
            await db.Entry(grant).Collection(x => x.Allocations).LoadAsync(cancellationToken);
        }

        return grant;
    }

    private static string FriendlyShortage(string poNo, short? poLineNo, decimal requested, decimal shortfall) =>
        $"This goods receipt needs an approved incoming quality inspection before it can be posted. "
        + $"PO {poNo} line {poLineNo} is short {shortfall:0.####} of the {requested:0.####} received. "
        + "Inspect and approve the delivery, then post only the accepted quantity.";

    private static decimal QtyOf(IvTrxBatchDetail detail) =>
        IvQty.Round(detail.ToPurQty ?? detail.ToStdQty ?? 0m);

    private readonly record struct ReceiptNeed(IvTrxBatchDetail Detail, decimal Qty, bool StockControlled);
}
