using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

public sealed class SaDocApplicationService : ISaDocApplication
{
    private readonly ISaSoRepository _salesOrders;
    private readonly ISaDoRepository _deliveryOrders;

    public SaDocApplicationService(ISaSoRepository salesOrders, ISaDoRepository deliveryOrders)
    {
        _salesOrders = salesOrders;
        _deliveryOrders = deliveryOrders;
    }

    public Task<SaDocAllocationResult> AllocateSOToDOAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        string? documentCurrency,
        IReadOnlyList<SaDocAllocationLine> lines,
        CancellationToken cancellationToken = default) =>
        AllocateAsync(
            db,
            company,
            branch,
            userId,
            documentCurrency,
            lines,
            sourceIsDo: false,
            targetDocType: SaDocTypes.Do,
            cancellationToken);

    public Task<SaDocAllocationResult> AllocateSOToInvoiceAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        string? documentCurrency,
        IReadOnlyList<SaDocAllocationLine> lines,
        CancellationToken cancellationToken = default) =>
        AllocateAsync(
            db,
            company,
            branch,
            userId,
            documentCurrency,
            lines,
            sourceIsDo: false,
            targetDocType: SaDocTypes.Inv,
            cancellationToken);

    public Task<SaDocAllocationResult> AllocateDOToInvoiceAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        string? documentCurrency,
        IReadOnlyList<SaDocAllocationLine> lines,
        CancellationToken cancellationToken = default) =>
        AllocateAsync(
            db,
            company,
            branch,
            userId,
            documentCurrency,
            lines,
            sourceIsDo: true,
            targetDocType: SaDocTypes.Inv,
            cancellationToken);

    public async Task<SaDocAllocationResult> ReverseDocumentAllocationsAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        string targetDocType,
        string targetDocId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var companyCode = (company ?? string.Empty).Trim();
        var branchCode = (branch ?? string.Empty).Trim();
        var type = (targetDocType ?? string.Empty).Trim().ToUpperInvariant();
        var id = (targetDocId ?? string.Empty).Trim();
        if (companyCode.Length == 0 || branchCode.Length == 0 || type.Length == 0 || id.Length == 0)
        {
            return SaDocAllocationResult.Fail(SaDocAllocationReasonCodes.NotFound, "Allocation target was not found.");
        }

        var rows = await db.SaDocApplications
            .Where(x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.TargetDocType == type
                && x.TargetDocId == id)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return SaDocAllocationResult.Ok();
        }

        var soNos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var doNos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (string.Equals(row.SourceDocType, SaDocTypes.So, StringComparison.OrdinalIgnoreCase))
            {
                soNos.Add(row.SourceDocId);
            }

            if (string.Equals(row.SourceDocType, SaDocTypes.Do, StringComparison.OrdinalIgnoreCase))
            {
                doNos.Add(row.SourceDocId);
            }

            if (!string.IsNullOrWhiteSpace(row.RelatedSoNo))
            {
                soNos.Add(row.RelatedSoNo);
            }
        }

        if (string.Equals(type, SaDocTypes.Do, StringComparison.OrdinalIgnoreCase))
        {
            doNos.Add(id);
        }

        // Force-closed SOs cannot have delivery/invoice allocations reversed.
        foreach (var soNo in soNos.OrderBy(x => x, SaSoLockOrder.Comparer))
        {
            var salesOrder = await _salesOrders.LockForUpdateAsync(db, companyCode, branchCode, soNo, cancellationToken)
                ?? await db.SaSos.FirstOrDefaultAsync(
                    x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.SoNo == soNo,
                    cancellationToken);
            if (salesOrder is null)
            {
                continue;
            }

            if (string.Equals(salesOrder.ClosedReason, SaSoClosedReasons.ForceClosed, StringComparison.OrdinalIgnoreCase))
            {
                return SaDocAllocationResult.Fail(
                    SaSoReasonCodes.ForceClosed,
                    $"Sales Order {salesOrder.SoNo} is force closed and cannot be rolled back.");
            }
        }

        db.SaDocApplications.RemoveRange(rows);
        await RecalculateAffectedLinesAsync(db, companyCode, branchCode, userId, soNos, doNos, cancellationToken);
        return SaDocAllocationResult.Ok();
    }

    public async Task RecalculateAffectedLinesAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        IReadOnlyCollection<string> soNos,
        IReadOnlyCollection<string> doNos,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var companyCode = (company ?? string.Empty).Trim();
        var branchCode = (branch ?? string.Empty).Trim();
        var soKeys = NormalizeKeys(soNos);
        var doKeys = NormalizeKeys(doNos);
        if (soKeys.Count == 0 && doKeys.Count == 0)
        {
            return;
        }

        // Include Added and exclude Deleted so projections stay correct before the caller's SaveChanges.
        var effective = await LoadEffectiveAllocationsAsync(
            db, companyCode, branchCode, soKeys, doKeys, cancellationToken);

        var deliveredBySo = SumSource(
            effective.Where(x => x.SourceDocType == SaDocTypes.So && x.TargetDocType == SaDocTypes.Do),
            x => x.SourceDocId,
            x => x.SourceCustRel > 0 ? x.SourceCustRel : (short)1,
            x => x.SourceLineId);
        var invoicedDirectBySo = SumSource(
            effective.Where(x => x.SourceDocType == SaDocTypes.So && x.TargetDocType == SaDocTypes.Inv),
            x => x.SourceDocId,
            x => x.SourceCustRel > 0 ? x.SourceCustRel : (short)1,
            x => x.SourceLineId);
        var invoicedViaDoBySo = SumSource(
            effective.Where(x =>
                x.SourceDocType == SaDocTypes.Do
                && x.TargetDocType == SaDocTypes.Inv
                && soKeys.Contains(x.RelatedSoNo)),
            x => x.RelatedSoNo,
            x => x.RelatedCustRel > 0 ? x.RelatedCustRel : (short)1,
            x => x.RelatedSoLine);
        var billedByDo = SumSource(
            effective.Where(x => x.SourceDocType == SaDocTypes.Do && x.TargetDocType == SaDocTypes.Inv),
            x => x.SourceDocId,
            x => (short)1,
            x => x.SourceLineId);

        var now = DateTime.UtcNow;
        var uid = Truncate(userId, 20);

        var custRelsBySo = effective
            .SelectMany(x => new[]
            {
                x.SourceDocType == SaDocTypes.So && soKeys.Contains(x.SourceDocId)
                    ? (SoNo: x.SourceDocId, CustRel: x.SourceCustRel > 0 ? x.SourceCustRel : (short)1)
                    : default,
                !string.IsNullOrWhiteSpace(x.RelatedSoNo) && soKeys.Contains(x.RelatedSoNo)
                    ? (SoNo: x.RelatedSoNo, CustRel: x.RelatedCustRel > 0 ? x.RelatedCustRel : (short)1)
                    : default
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.SoNo) && x.CustRel > 0)
            .GroupBy(x => x.SoNo, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.CustRel).ToHashSet(), StringComparer.OrdinalIgnoreCase);

        foreach (var soNo in soKeys.OrderBy(x => x, SaSoLockOrder.Comparer))
        {
            custRelsBySo.TryGetValue(soNo, out var affectedCustRels);
            var affectedRelList = affectedCustRels?.ToList() ?? [];
            var salesOrders = await db.SaSos
                .Include(x => x.Details)
                .Where(x =>
                    x.CompanyCode == companyCode
                    && x.BranchCode == branchCode
                    && x.SoNo == soNo
                    && (x.IsCurrent || affectedRelList.Contains(x.CustRel)))
                .ToListAsync(
                    cancellationToken);
            if (salesOrders.Count == 0)
            {
                continue;
            }

            foreach (var salesOrder in salesOrders)
            {
                foreach (var detail in salesOrder.Details.Where(x => x.CustRel == salesOrder.CustRel))
                {
                    var key = (soNo.ToUpperInvariant(), salesOrder.CustRel, detail.Line);
                    var delivered = SaSoQty.RoundQty(deliveredBySo.GetValueOrDefault(key));
                    var invoiced = SaSoQty.RoundQty(
                        invoicedDirectBySo.GetValueOrDefault(key) + invoicedViaDoBySo.GetValueOrDefault(key));
                    detail.DeliveredQty = delivered;
                    detail.InvoicedQty = invoiced;
                    detail.ShippedQty = delivered;
                    detail.BalanceQty = SaSoQty.RoundQty(detail.OrderQty - delivered);
                }

                DeriveSoStatus(salesOrder, uid, now);
                salesOrder.ModifiedDate = now;
                salesOrder.ModifiedBy = uid;
                TouchRowVersion(db, salesOrder);
            }
        }

        foreach (var doNo in doKeys.OrderBy(x => x, SaSoLockOrder.Comparer))
        {
            var deliveryOrder = await db.SaDos
                .Include(x => x.Details)
                .FirstOrDefaultAsync(
                    x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.DoNo == doNo,
                    cancellationToken);
            if (deliveryOrder is null)
            {
                continue;
            }

            var anyBilled = false;
            var allFull = deliveryOrder.Details.Count > 0;
            foreach (var detail in deliveryOrder.Details)
            {
                var billed = SaSoQty.RoundQty(billedByDo.GetValueOrDefault((doNo.ToUpperInvariant(), (short)1, detail.Line)));
                if (billed > 0m)
                {
                    anyBilled = true;
                }

                if (billed < SaSoQty.RoundQty(detail.Qty))
                {
                    allFull = false;
                }
            }

            deliveryOrder.BillingStatus = !anyBilled
                ? SaDualStatuses.None
                : allFull
                    ? SaDualStatuses.Full
                    : SaDualStatuses.Partial;
            deliveryOrder.ModifiedDate = now;
            deliveryOrder.ModifiedBy = uid;
            TouchDoRowVersion(db, deliveryOrder);
        }
    }

    private static async Task<List<SaDocApplication>> LoadEffectiveAllocationsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<string> soKeys,
        IReadOnlyCollection<string> doKeys,
        CancellationToken cancellationToken)
    {
        var deletedIds = db.ChangeTracker.Entries<SaDocApplication>()
            .Where(e => e.State == EntityState.Deleted)
            .Select(e => e.Entity.Id)
            .Where(id => id != 0)
            .ToHashSet();

        var fromDb = await db.SaDocApplications.AsNoTracking()
            .Where(x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && (
                    (x.SourceDocType == SaDocTypes.So && soKeys.Contains(x.SourceDocId))
                    || (x.SourceDocType == SaDocTypes.Do && doKeys.Contains(x.SourceDocId))
                    || (x.TargetDocType == SaDocTypes.Do && doKeys.Contains(x.TargetDocId))
                    || (x.TargetDocType == SaDocTypes.Inv && soKeys.Contains(x.RelatedSoNo))))
            .ToListAsync(cancellationToken);

        var added = db.ChangeTracker.Entries<SaDocApplication>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .Where(x =>
                string.Equals(x.CompanyCode, companyCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase)
                && (
                    (x.SourceDocType == SaDocTypes.So && soKeys.Contains(x.SourceDocId))
                    || (x.SourceDocType == SaDocTypes.Do && doKeys.Contains(x.SourceDocId))
                    || (x.TargetDocType == SaDocTypes.Do && doKeys.Contains(x.TargetDocId))
                    || (x.TargetDocType == SaDocTypes.Inv && soKeys.Contains(x.RelatedSoNo))))
            .ToList();

        return fromDb.Where(x => !deletedIds.Contains(x.Id)).Concat(added).ToList();
    }

    private static Dictionary<(string, short, short), decimal> SumSource(
        IEnumerable<SaDocApplication> rows,
        Func<SaDocApplication, string> idSelector,
        Func<SaDocApplication, short> custRelSelector,
        Func<SaDocApplication, short> lineSelector) =>
        rows
            .GroupBy(x => (idSelector(x).ToUpperInvariant(), custRelSelector(x), lineSelector(x)))
            .ToDictionary(g => g.Key, g => SaSoQty.RoundQty(g.Sum(x => x.AppliedQty)));

    public Task<bool> HasAllocationForDoLineAsync(
        AppDbContext db,
        string company,
        string branch,
        string doNo,
        short line,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var companyCode = (company ?? string.Empty).Trim();
        var branchCode = (branch ?? string.Empty).Trim();
        var no = (doNo ?? string.Empty).Trim();
        return db.SaDocApplications.AsNoTracking().AnyAsync(
            x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && ((x.SourceDocType == SaDocTypes.Do && x.SourceDocId == no && x.SourceLineId == line)
                    || (x.TargetDocType == SaDocTypes.Do && x.TargetDocId == no && x.TargetLineId == line)),
            cancellationToken);
    }

    public Task<bool> HasAllocationsForSoAsync(
        AppDbContext db,
        string company,
        string branch,
        string soNo,
        short? custRel = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var companyCode = (company ?? string.Empty).Trim();
        var branchCode = (branch ?? string.Empty).Trim();
        var no = (soNo ?? string.Empty).Trim();
        var rel = custRel.GetValueOrDefault();
        return db.SaDocApplications.AsNoTracking().AnyAsync(
            x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && ((x.SourceDocType == SaDocTypes.So
                        && x.SourceDocId == no
                        && (rel <= 0 || x.SourceCustRel == rel))
                    || (x.RelatedSoNo == no
                        && x.SourceDocType == SaDocTypes.Do
                        && x.TargetDocType == SaDocTypes.Inv
                        && (rel <= 0 || x.RelatedCustRel == rel))),
            cancellationToken);
    }

    public async Task<IReadOnlySet<SaDocSoRevisionKey>> ListAllocatedSoKeysAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<SaDocSoRevisionKey> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return new HashSet<SaDocSoRevisionKey>();
        }

        var companyCode = (company ?? string.Empty).Trim();
        var branchCode = (branch ?? string.Empty).Trim();
        var soNos = keys.Select(x => x.SoNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var custRels = keys.Select(x => x.CustRel).Distinct().ToList();
        var wanted = keys.ToHashSet();

        var hits = await db.SaDocApplications.AsNoTracking()
            .Where(x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && ((x.SourceDocType == SaDocTypes.So
                        && soNos.Contains(x.SourceDocId)
                        && custRels.Contains(x.SourceCustRel))
                    || (soNos.Contains(x.RelatedSoNo)
                        && x.SourceDocType == SaDocTypes.Do
                        && x.TargetDocType == SaDocTypes.Inv
                        && custRels.Contains(x.RelatedCustRel))))
            .Select(x => new
            {
                SoNo = x.SourceDocType == SaDocTypes.So ? x.SourceDocId : x.RelatedSoNo,
                CustRel = x.SourceDocType == SaDocTypes.So ? x.SourceCustRel : x.RelatedCustRel
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        var result = new HashSet<SaDocSoRevisionKey>();
        foreach (var hit in hits)
        {
            var key = new SaDocSoRevisionKey(hit.SoNo, hit.CustRel);
            if (wanted.Contains(key))
            {
                result.Add(key);
            }
            else
            {
                // Case-insensitive SoNo match against wanted keys.
                foreach (var w in wanted)
                {
                    if (w.CustRel == hit.CustRel
                        && string.Equals(w.SoNo, hit.SoNo, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(w);
                        break;
                    }
                }
            }
        }

        return result;
    }

    public async Task<decimal> SumDoInvoicedQtyAsync(
        AppDbContext db,
        string company,
        string branch,
        string doNo,
        short line,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var companyCode = (company ?? string.Empty).Trim();
        var branchCode = (branch ?? string.Empty).Trim();
        var no = (doNo ?? string.Empty).Trim();
        var sum = await db.SaDocApplications.AsNoTracking()
            .Where(x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.SourceDocType == SaDocTypes.Do
                && x.SourceDocId == no
                && x.SourceCustRel == 0
                && x.SourceLineId == line
                && x.TargetDocType == SaDocTypes.Inv)
            .SumAsync(x => (decimal?)x.AppliedQty, cancellationToken);
        return SaSoQty.RoundQty(sum ?? 0m);
    }

    private async Task<SaDocAllocationResult> AllocateAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        string? documentCurrency,
        IReadOnlyList<SaDocAllocationLine> lines,
        bool sourceIsDo,
        string targetDocType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var companyCode = (company ?? string.Empty).Trim();
        var branchCode = (branch ?? string.Empty).Trim();
        var normalized = NormalizeLines(lines);
        if (normalized.Count == 0)
        {
            return SaDocAllocationResult.Ok();
        }

        foreach (var line in normalized)
        {
            if (SaSoQty.RoundQty(line.AppliedQty) <= 0m)
            {
                return SaDocAllocationResult.Fail(
                    SaDocAllocationReasonCodes.InvalidQty,
                    "Applied quantity must be greater than zero.");
            }
        }

        // Discover complete key sets before any additional locks.
        var doNos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var soNos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sourceIsDo)
        {
            foreach (var line in normalized)
            {
                doNos.Add(line.SourceDocId);
            }
        }
        else
        {
            foreach (var line in normalized)
            {
                soNos.Add(line.SourceDocId);
            }
        }

        if (string.Equals(targetDocType, SaDocTypes.Do, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in normalized)
            {
                doNos.Add(line.TargetDocId);
            }
        }

        var lockedDos = new Dictionary<string, SaDo>(StringComparer.OrdinalIgnoreCase);
        foreach (var doNo in doNos.OrderBy(x => x, SaSoLockOrder.Comparer))
        {
            var deliveryOrder = await _deliveryOrders.LockForUpdateAsync(
                db, companyCode, branchCode, doNo, cancellationToken);
            if (deliveryOrder is null)
            {
                return SaDocAllocationResult.Fail(
                    SaDocAllocationReasonCodes.NotFound,
                    $"Delivery order {doNo} was not found.");
            }

            await db.Entry(deliveryOrder).Collection(x => x.Details).LoadAsync(cancellationToken);
            lockedDos[doNo] = deliveryOrder;
        }

        // DO→INV: lock SO only for allocated lines that have SO identity (never SO "").
        if (sourceIsDo)
        {
            foreach (var line in normalized)
            {
                if (!lockedDos.TryGetValue(line.SourceDocId, out var deliveryOrder))
                {
                    continue;
                }

                var detail = deliveryOrder.Details.FirstOrDefault(x => x.Line == line.SourceLineId);
                if (detail is not null && HasSoReference(detail.SoNo, detail.SoLine))
                {
                    soNos.Add(detail.SoNo.Trim());
                }
            }
        }

        if (soNos.Count > SaSoLimits.MaxDistinctSoHeaders)
        {
            return SaDocAllocationResult.Fail(
                SaDocAllocationReasonCodes.TooManyHeaders,
                SaSoReasonCodes.TooManyHeadersMessage);
        }

        var lockedSos = new Dictionary<string, SaSo>(StringComparer.OrdinalIgnoreCase);
        foreach (var soNo in soNos.OrderBy(x => x, SaSoLockOrder.Comparer))
        {
            var salesOrder = await _salesOrders.LockForUpdateAsync(
                db, companyCode, branchCode, soNo, cancellationToken);
            if (salesOrder is null)
            {
                return SaDocAllocationResult.Fail(
                    SaDocAllocationReasonCodes.NotFound,
                    $"Sales Order {soNo} was not found.");
            }

            await db.Entry(salesOrder).Collection(x => x.Details).LoadAsync(cancellationToken);
            lockedSos[soNo] = salesOrder;
        }

        // Batched remaining sums for locked keys (ledger + soft-reserve live docs).
        // Post-transition: exclude target DO/INV from NEW live SUMs before inserting SaDocApplication.
        var soDoApplied = await SumBySourceAsync(
            db, companyCode, branchCode, SaDocTypes.So, soNos, SaDocTypes.Do, cancellationToken);
        var soInvApplied = await SumBySourceAsync(
            db, companyCode, branchCode, SaDocTypes.So, soNos, SaDocTypes.Inv, cancellationToken);
        var doInvByRelatedSo = await SumDoInvByRelatedSoAsync(
            db, companyCode, branchCode, soNos, cancellationToken);
        var doInvByDoLine = await SumBySourceAsync(
            db, companyCode, branchCode, SaDocTypes.Do, doNos, SaDocTypes.Inv, cancellationToken);

        SaSoLineReserve.DocIdentity? excludeDo = null;
        SaSoLineReserve.DocIdentity? excludeInv = null;
        if (string.Equals(targetDocType, SaDocTypes.Do, StringComparison.OrdinalIgnoreCase))
        {
            var targetDo = normalized.Select(x => x.TargetDocId).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(targetDo))
            {
                excludeDo = new SaSoLineReserve.DocIdentity(companyCode, branchCode, targetDo.Trim());
            }
        }
        else if (string.Equals(targetDocType, SaDocTypes.Inv, StringComparison.OrdinalIgnoreCase) && !sourceIsDo)
        {
            var targetInv = normalized.Select(x => x.TargetDocId).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(targetInv))
            {
                excludeInv = new SaSoLineReserve.DocIdentity(companyCode, branchCode, targetInv.Trim());
            }
        }

        var liveSums = await SaSoLineReserve.SumBySoLinesAsync(
            db, companyCode, branchCode, soNos.ToList(), excludeDo, excludeInv, cancellationToken);

        var pendingSoDo = new Dictionary<(string, short, short), decimal>();
        var pendingSoInv = new Dictionary<(string, short, short), decimal>();
        var pendingDoInv = new Dictionary<(string, short), decimal>();
        var pendingRelatedSoInv = new Dictionary<(string, short, short), decimal>();
        var targetLineOwners = new Dictionary<(string, short), string>();
        var now = DateTime.UtcNow;
        var uid = Truncate(userId, 20);
        var inserts = new List<SaDocApplication>();

        foreach (var line in normalized)
        {
            var applied = SaSoQty.RoundQty(line.AppliedQty);
            var targetKey = (line.TargetDocId.ToUpperInvariant(), line.TargetLineId);
            var ownerKey = $"{(sourceIsDo ? SaDocTypes.Do : SaDocTypes.So)}|{line.SourceDocId.ToUpperInvariant()}|{line.SourceLineId}";
            if (targetLineOwners.TryGetValue(targetKey, out var existingOwner) &&
                !string.Equals(existingOwner, ownerKey, StringComparison.OrdinalIgnoreCase))
            {
                return SaDocAllocationResult.Fail(
                    SaDocAllocationReasonCodes.MergeForbidden,
                    "Many source lines cannot allocate onto one target line.");
            }

            targetLineOwners[targetKey] = ownerKey;

            var sourceCustRel = sourceIsDo ? (short)0 : lockedSos[line.SourceDocId].CustRel;
            var duplicateExists = await db.SaDocApplications.AnyAsync(
                x =>
                    x.CompanyCode == companyCode
                    && x.BranchCode == branchCode
                    && x.SourceDocType == (sourceIsDo ? SaDocTypes.Do : SaDocTypes.So)
                    && x.SourceDocId == line.SourceDocId
                    && x.SourceCustRel == sourceCustRel
                    && x.SourceLineId == line.SourceLineId
                    && x.TargetDocType == targetDocType
                    && x.TargetDocId == line.TargetDocId
                    && x.TargetLineId == line.TargetLineId,
                cancellationToken);
            if (duplicateExists)
            {
                return SaDocAllocationResult.Fail(
                    SaDocAllocationReasonCodes.Duplicate,
                    "Allocation already exists for this source and target line.");
            }

            var targetTaken = await db.SaDocApplications.AnyAsync(
                x =>
                    x.CompanyCode == companyCode
                    && x.BranchCode == branchCode
                    && x.TargetDocType == targetDocType
                    && x.TargetDocId == line.TargetDocId
                    && x.TargetLineId == line.TargetLineId,
                cancellationToken);
            if (targetTaken)
            {
                return SaDocAllocationResult.Fail(
                    SaDocAllocationReasonCodes.MergeForbidden,
                    "Target line is already allocated from another source.");
            }

            string relatedSoNo;
            short relatedCustRel;
            short relatedSoLine;
            string? sourceUom;
            string sourceCust;
            string? sourceCurrency;

            if (sourceIsDo)
            {
                if (!lockedDos.TryGetValue(line.SourceDocId, out var deliveryOrder))
                {
                    return SaDocAllocationResult.Fail(
                        SaDocAllocationReasonCodes.NotFound,
                        $"Delivery order {line.SourceDocId} was not found.");
                }

                if (string.Equals(deliveryOrder.Status, SaDoStatuses.Closed, StringComparison.OrdinalIgnoreCase))
                {
                    return SaDocAllocationResult.Fail(
                        SaDocAllocationReasonCodes.Closed,
                        $"Delivery order {deliveryOrder.DoNo} is closed.");
                }

                if (!string.Equals(deliveryOrder.Status, SaDoStatuses.Posted, StringComparison.OrdinalIgnoreCase))
                {
                    return SaDocAllocationResult.Fail(
                        SaDocAllocationReasonCodes.Closed,
                        $"Delivery order {deliveryOrder.DoNo} is not posted.");
                }

                var doDetail = deliveryOrder.Details.FirstOrDefault(x => x.Line == line.SourceLineId);
                if (doDetail is null)
                {
                    return SaDocAllocationResult.Fail(
                        SaDocAllocationReasonCodes.NotFound,
                        $"Delivery order {deliveryOrder.DoNo} line {line.SourceLineId} was not found.");
                }

                if (!string.Equals(deliveryOrder.CustCode, line.CustCode, StringComparison.OrdinalIgnoreCase))
                {
                    return SaDocAllocationResult.Fail(
                        SaDocAllocationReasonCodes.CustomerMismatch,
                        $"Delivery order {deliveryOrder.DoNo} customer does not match document customer.");
                }

                if (!CurrenciesEqual(documentCurrency, deliveryOrder.Currency) ||
                    !CurrenciesEqual(line.Currency, deliveryOrder.Currency))
                {
                    return SaDocAllocationResult.Fail(
                        SaDocAllocationReasonCodes.CurrencyMismatch,
                        $"Delivery order {deliveryOrder.DoNo} currency does not match.");
                }

                sourceUom = doDetail.SellingUom;
                sourceCust = deliveryOrder.CustCode;
                sourceCurrency = deliveryOrder.Currency;

                var doKey = (line.SourceDocId.ToUpperInvariant(), line.SourceLineId);
                var doSumKey = (line.SourceDocId.ToUpperInvariant(), (short)1, line.SourceLineId);
                var doUsed = doInvByDoLine.GetValueOrDefault(doSumKey) + pendingDoInv.GetValueOrDefault(doKey);
                if (SaSoQty.RoundQty(doUsed + applied) > SaSoQty.RoundQty(doDetail.Qty))
                {
                    return SaDocAllocationResult.Fail(
                        SaDocAllocationReasonCodes.OverAllocate,
                        $"Delivery order {line.SourceDocId} line {line.SourceLineId} does not have enough remaining billable quantity.");
                }

                if (HasSoReference(doDetail.SoNo, doDetail.SoLine))
                {
                    relatedSoNo = doDetail.SoNo.Trim();
                    relatedCustRel = doDetail.CustRel is > 0 ? doDetail.CustRel.Value : (short)1;
                    relatedSoLine = doDetail.SoLine!.Value;

                    if (!lockedSos.TryGetValue(relatedSoNo, out var relatedSo))
                    {
                        return SaDocAllocationResult.Fail(
                            SaDocAllocationReasonCodes.NotFound,
                            $"Sales Order {relatedSoNo} was not found.");
                    }

                    relatedCustRel = relatedSo.CustRel;
                    var soGate = ValidateSoSource(relatedSo, relatedSoLine, line.CustCode, line.SellingUom ?? sourceUom);
                    if (soGate is not null)
                    {
                        return soGate;
                    }

                    var soDetail = relatedSo.Details.First(x => x.Line == relatedSoLine && x.CustRel == relatedSo.CustRel);
                    if (!UomsEqual(soDetail.SellingUom, line.SellingUom ?? sourceUom))
                    {
                        return SaDocAllocationResult.Fail(
                            SaDocAllocationReasonCodes.UomMismatch,
                            $"Sales Order {relatedSoNo} line {relatedSoLine} UOM does not match.");
                    }

                    var soKey = (relatedSoNo.ToUpperInvariant(), relatedCustRel, relatedSoLine);
                    var soUsed = soInvApplied.GetValueOrDefault(soKey)
                        + doInvByRelatedSo.GetValueOrDefault(soKey)
                        + pendingSoInv.GetValueOrDefault(soKey)
                        + pendingRelatedSoInv.GetValueOrDefault(soKey);

                    if (SaSoQty.RoundQty(soUsed + applied) > SaSoQty.RoundQty(soDetail.OrderQty))
                    {
                        return SaDocAllocationResult.Fail(
                            SaDocAllocationReasonCodes.OverAllocate,
                            $"Sales Order {relatedSoNo} line {relatedSoLine} does not have enough remaining billable quantity.");
                    }

                    pendingRelatedSoInv[soKey] = pendingRelatedSoInv.GetValueOrDefault(soKey) + applied;
                }
                else
                {
                    // Standalone DO_INV: no SO relationship (RelatedSoNo="", RelatedSoLine=0, RelatedCustRel=0).
                    relatedSoNo = string.Empty;
                    relatedCustRel = 0;
                    relatedSoLine = 0;
                }

                pendingDoInv[doKey] = pendingDoInv.GetValueOrDefault(doKey) + applied;
            }
            else
            {
                if (!lockedSos.TryGetValue(line.SourceDocId, out var salesOrder))
                {
                    return SaDocAllocationResult.Fail(
                        SaDocAllocationReasonCodes.NotFound,
                        $"Sales Order {line.SourceDocId} was not found.");
                }

                var soGate = ValidateSoSource(salesOrder, line.SourceLineId, line.CustCode, line.SellingUom);
                if (soGate is not null)
                {
                    return soGate;
                }

                var soDetail = salesOrder.Details.First(x => x.Line == line.SourceLineId && x.CustRel == salesOrder.CustRel);
                relatedSoNo = salesOrder.SoNo;
                relatedCustRel = salesOrder.CustRel;
                relatedSoLine = line.SourceLineId;
                sourceUom = soDetail.SellingUom;
                sourceCust = salesOrder.CustCode;
                sourceCurrency = salesOrder.Currency;

                if (!CurrenciesEqual(documentCurrency, sourceCurrency) ||
                    !CurrenciesEqual(line.Currency, sourceCurrency))
                {
                    return SaDocAllocationResult.Fail(
                        SaDocAllocationReasonCodes.CurrencyMismatch,
                        $"Sales Order {salesOrder.SoNo} currency does not match.");
                }

                var soKey = (line.SourceDocId.ToUpperInvariant(), salesOrder.CustRel, line.SourceLineId);
                if (string.Equals(targetDocType, SaDocTypes.Do, StringComparison.OrdinalIgnoreCase))
                {
                    // Post-state once: DeliveredQty = existing SO_DO; NewDoQty excludes this DO;
                    // thisDoQty = AppliedQty being inserted (never also count this DO as NEW).
                    var lineSums = SaSoLineReserve.GetSums(liveSums, line.SourceDocId, salesOrder.CustRel, line.SourceLineId);
                    var pendingDo = pendingSoDo.GetValueOrDefault(soKey);
                    var eval = SaSoLineReserve.Evaluate(
                        line.SourceDocId,
                        line.SourceLineId,
                        soDetail.OrderQty,
                        deliveredQty: soDoApplied.GetValueOrDefault(soKey),
                        new SaSoLineReserve.SoLineSums
                        {
                            NewDoQty = lineSums.NewDoQty,
                            LiveDoQty = lineSums.LiveDoQty,
                            NewSoInvQty = lineSums.NewSoInvQty,
                            PostedSoInv = soInvApplied.GetValueOrDefault(soKey)
                        },
                        thisDoQty: applied + pendingDo,
                        thisSoInvQty: 0m);
                    if (!eval.Succeeded)
                    {
                        return SaDocAllocationResult.Fail(
                            SaDocAllocationReasonCodes.OverAllocate,
                            SaSoLineReserve.FormatOverAllocate(eval));
                    }

                    pendingSoDo[soKey] = pendingDo + applied;
                }
                else
                {
                    // SO_INV post-state: exclude this invoice from NewSoInvQty; LiveDoQty reserves DO path.
                    var lineSums = SaSoLineReserve.GetSums(liveSums, line.SourceDocId, salesOrder.CustRel, line.SourceLineId);
                    var pendingInv = pendingSoInv.GetValueOrDefault(soKey)
                        + pendingRelatedSoInv.GetValueOrDefault(soKey);
                    var eval = SaSoLineReserve.Evaluate(
                        line.SourceDocId,
                        line.SourceLineId,
                        soDetail.OrderQty,
                        deliveredQty: soDoApplied.GetValueOrDefault(soKey),
                        new SaSoLineReserve.SoLineSums
                        {
                            NewDoQty = lineSums.NewDoQty,
                            LiveDoQty = lineSums.LiveDoQty,
                            NewSoInvQty = lineSums.NewSoInvQty,
                            PostedSoInv = soInvApplied.GetValueOrDefault(soKey)
                        },
                        thisDoQty: 0m,
                        thisSoInvQty: applied + pendingInv);
                    if (!eval.Succeeded)
                    {
                        return SaDocAllocationResult.Fail(
                            SaDocAllocationReasonCodes.OverAllocate,
                            SaSoLineReserve.FormatOverAllocate(eval));
                    }

                    pendingSoInv[soKey] = pendingSoInv.GetValueOrDefault(soKey) + applied;
                }
            }

            if (line.SellingUom is not null && !UomsEqual(sourceUom, line.SellingUom))
            {
                return SaDocAllocationResult.Fail(
                    SaDocAllocationReasonCodes.UomMismatch,
                    "Source and target selling UOM must match.");
            }

            if (!string.Equals(sourceCust, line.CustCode, StringComparison.OrdinalIgnoreCase))
            {
                return SaDocAllocationResult.Fail(
                    SaDocAllocationReasonCodes.CustomerMismatch,
                    "Source customer does not match document customer.");
            }

            inserts.Add(new SaDocApplication
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                SourceDocType = sourceIsDo ? SaDocTypes.Do : SaDocTypes.So,
                SourceDocId = line.SourceDocId,
                SourceCustRel = sourceIsDo ? (short)0 : relatedCustRel,
                SourceLineId = line.SourceLineId,
                TargetDocType = targetDocType,
                TargetDocId = line.TargetDocId,
                TargetCustRel = 0,
                TargetLineId = line.TargetLineId,
                RelatedSoNo = relatedSoNo,
                RelatedCustRel = relatedCustRel,
                RelatedSoLine = relatedSoLine,
                AppliedQty = applied,
                AppliedAmount = line.AppliedAmount,
                Created = now,
                CreatedUid = uid
            });

            line.SoConsumedQty = applied;
        }

        try
        {
            db.SaDocApplications.AddRange(inserts);
            await RecalculateAffectedLinesAsync(
                db,
                companyCode,
                branchCode,
                userId,
                soNos,
                doNos,
                cancellationToken);
        }
        catch (DbUpdateException)
        {
            return SaDocAllocationResult.Fail(
                SaDocAllocationReasonCodes.Duplicate,
                "Allocation unique key collision.");
        }

        return SaDocAllocationResult.Ok(normalized);
    }

    private static SaDocAllocationResult? ValidateSoSource(
        SaSo salesOrder,
        short soLine,
        string custCode,
        string? sellingUom)
    {
        if (string.Equals(salesOrder.Status, SaSoStatuses.Closed, StringComparison.OrdinalIgnoreCase))
        {
            return SaDocAllocationResult.Fail(
                SaDocAllocationReasonCodes.Closed,
                $"Sales Order {salesOrder.SoNo} is closed.");
        }

        if (string.Equals(salesOrder.ClosedReason, SaSoClosedReasons.ForceClosed, StringComparison.OrdinalIgnoreCase))
        {
            return SaDocAllocationResult.Fail(
                SaDocAllocationReasonCodes.ForceClosed,
                $"Sales Order {salesOrder.SoNo} is force closed.");
        }

        if (!string.Equals(salesOrder.CustCode, custCode, StringComparison.OrdinalIgnoreCase))
        {
            return SaDocAllocationResult.Fail(
                SaDocAllocationReasonCodes.CustomerMismatch,
                $"Sales Order {salesOrder.SoNo} customer does not match document customer.");
        }

        var detail = salesOrder.Details.FirstOrDefault(x => x.Line == soLine && x.CustRel == salesOrder.CustRel);
        if (detail is null)
        {
            return SaDocAllocationResult.Fail(
                SaDocAllocationReasonCodes.NotFound,
                $"Sales Order {salesOrder.SoNo} line {soLine} was not found.");
        }

        if (sellingUom is not null && !UomsEqual(detail.SellingUom, sellingUom))
        {
            return SaDocAllocationResult.Fail(
                SaDocAllocationReasonCodes.UomMismatch,
                $"Sales Order {salesOrder.SoNo} line {soLine} UOM does not match.");
        }

        return null;
    }

    public static void DeriveSoHeaderStatus(SaSo salesOrder, string userId, DateTime utcNow) =>
        DeriveSoStatus(salesOrder, userId, utcNow);

    internal static void DeriveSoStatus(SaSo salesOrder, string userId, DateTime utcNow)
    {
        if (!salesOrder.IsCurrent
            || string.Equals(salesOrder.Status, SaSoStatuses.Superseded, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(salesOrder.ClosedReason, SaSoClosedReasons.ForceClosed, StringComparison.OrdinalIgnoreCase))
        {
            salesOrder.Status = SaSoStatuses.Closed;
            return;
        }

        if (salesOrder.Details.Count == 0)
        {
            salesOrder.Status = SaSoStatuses.New;
            salesOrder.FulfillmentStatus = SaDualStatuses.None;
            salesOrder.BillingStatus = SaDualStatuses.None;
            salesOrder.ClosedReason = null;
            salesOrder.ClosedDate = null;
            salesOrder.ClosedBy = null;
            return;
        }

        var anyDelivered = salesOrder.Details.Any(x => SaSoQty.RoundQty(x.DeliveredQty) > 0m);
        var allDelivered = salesOrder.Details.All(x => SaSoQty.RoundQty(x.DeliveredQty) >= SaSoQty.RoundQty(x.OrderQty));

        // R3: billing is terminal when a line is fully invoiced OR its remainder was written off by a
        // DO force-close. WrittenOffQty must never be counted as revenue — only as consumed capacity.
        var anyBillingProgress = salesOrder.Details.Any(x =>
            SaSoQty.RoundQty(x.InvoicedQty + x.WrittenOffQty) > 0m);
        var allBillingTerminal = salesOrder.Details.All(x =>
            SaSoQty.RoundQty(x.InvoicedQty + x.WrittenOffQty) >= SaSoQty.RoundQty(x.OrderQty));
        var anyWrittenOff = salesOrder.Details.Any(x => SaSoQty.RoundQty(x.WrittenOffQty) > 0m);

        salesOrder.FulfillmentStatus = allDelivered
            ? SaDualStatuses.Full
            : anyDelivered
                ? SaDualStatuses.Partial
                : SaDualStatuses.None;
        salesOrder.BillingStatus = allBillingTerminal
            ? anyWrittenOff
                ? SaDualStatuses.WrittenOff
                : SaDualStatuses.Full
            : anyBillingProgress
                ? SaDualStatuses.Partial
                : SaDualStatuses.None;

        if (allDelivered && allBillingTerminal)
        {
            salesOrder.Status = SaSoStatuses.Closed;
            salesOrder.ClosedReason = SaSoClosedReasons.FullyConsumed;
            salesOrder.ClosedDate = utcNow;
            salesOrder.ClosedBy = Truncate(userId, 20);
            return;
        }

        salesOrder.ClosedReason = null;
        salesOrder.ClosedDate = null;
        salesOrder.ClosedBy = null;
        salesOrder.Status = anyDelivered ? SaSoStatuses.Shipped : SaSoStatuses.New;
    }

    private static async Task<Dictionary<(string, short, short), decimal>> SumBySourceAsync(
        AppDbContext db,
        string company,
        string branch,
        string sourceType,
        IReadOnlyCollection<string> sourceIds,
        string targetType,
        CancellationToken cancellationToken)
    {
        if (sourceIds.Count == 0)
        {
            return new Dictionary<(string, short, short), decimal>();
        }

        var rows = await db.SaDocApplications.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company
                && x.BranchCode == branch
                && x.SourceDocType == sourceType
                && sourceIds.Contains(x.SourceDocId)
                && x.TargetDocType == targetType)
            .GroupBy(x => new
            {
                x.SourceDocId,
                CustRel = x.SourceCustRel > 0 ? x.SourceCustRel : (short)1,
                x.SourceLineId
            })
            .Select(g => new { g.Key.SourceDocId, g.Key.CustRel, g.Key.SourceLineId, Qty = g.Sum(x => x.AppliedQty) })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(x => (x.SourceDocId.ToUpperInvariant(), x.CustRel, x.SourceLineId), x => x.Qty);
    }

    private static async Task<Dictionary<(string, short, short), decimal>> SumDoInvByRelatedSoAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<string> soNos,
        CancellationToken cancellationToken)
    {
        if (soNos.Count == 0)
        {
            return new Dictionary<(string, short, short), decimal>();
        }

        var rows = await db.SaDocApplications.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company
                && x.BranchCode == branch
                && x.SourceDocType == SaDocTypes.Do
                && x.TargetDocType == SaDocTypes.Inv
                && soNos.Contains(x.RelatedSoNo))
            .GroupBy(x => new
            {
                x.RelatedSoNo,
                CustRel = x.RelatedCustRel > 0 ? x.RelatedCustRel : (short)1,
                x.RelatedSoLine
            })
            .Select(g => new { g.Key.RelatedSoNo, g.Key.CustRel, g.Key.RelatedSoLine, Qty = g.Sum(x => x.AppliedQty) })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(x => (x.RelatedSoNo.ToUpperInvariant(), x.CustRel, x.RelatedSoLine), x => x.Qty);
    }

    private static List<SaDocAllocationLine> NormalizeLines(IReadOnlyList<SaDocAllocationLine> lines) =>
        (lines ?? [])
            .Where(x => x is not null)
            .Select(x =>
            {
                x.SourceDocId = (x.SourceDocId ?? string.Empty).Trim();
                x.TargetDocId = (x.TargetDocId ?? string.Empty).Trim();
                x.CustCode = (x.CustCode ?? string.Empty).Trim();
                x.SellingUom = NormalizeOptional(x.SellingUom);
                x.Currency = NormalizeOptional(x.Currency);
                x.AppliedQty = SaSoQty.RoundQty(x.AppliedQty);
                x.SoConsumedQty = SaSoQty.RoundQty(x.SoConsumedQty);
                return x;
            })
            .Where(x => x.SourceDocId.Length > 0 && x.TargetDocId.Length > 0 && x.SourceLineId > 0 && x.TargetLineId > 0)
            .ToList();

    private static HashSet<string> NormalizeKeys(IReadOnlyCollection<string> keys) =>
        (keys ?? [])
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool HasSoReference(string? soNo, short? soLine) =>
        !string.IsNullOrWhiteSpace(soNo) && soLine is > 0;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool UomsEqual(string? left, string? right) =>
        string.Equals(NormalizeOptional(left), NormalizeOptional(right), StringComparison.OrdinalIgnoreCase);

    private static bool CurrenciesEqual(string? left, string? right)
    {
        var l = NormalizeOptional(left);
        var r = NormalizeOptional(right);
        if (l is null || r is null)
        {
            return true;
        }

        return string.Equals(l, r, StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return text.Length <= maxLength ? text : text[..maxLength];
    }

    public static void TouchRowVersion(AppDbContext db, SaSo salesOrder)
    {
        if (!db.Database.IsSqlServer())
        {
            salesOrder.RowVersion = Guid.NewGuid().ToByteArray();
        }
    }

    internal static void TouchDoRowVersion(AppDbContext db, SaDo deliveryOrder)
    {
        if (!db.Database.IsSqlServer())
        {
            deliveryOrder.RowVersion = Guid.NewGuid().ToByteArray();
        }
    }
}
