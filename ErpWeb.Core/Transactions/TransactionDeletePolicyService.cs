using System.Globalization;
using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Sales;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Transactions;

public sealed class TransactionDeletePolicyService : ITransactionDeletePolicyService
{
    public Task<TransactionDeleteDecision> EvaluateAsync(
        AppDbContext db,
        TransactionDeleteSubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(subject);
        var company = (subject.CompanyCode ?? string.Empty).Trim();
        var branch = (subject.BranchCode ?? string.Empty).Trim();
        var ownerType = (subject.OwnerType ?? string.Empty).Trim();
        return ownerType switch
        {
            TransactionDeleteOwnerTypes.InventoryBatch => EvaluateInventoryBatchAsync(db, company, branch, subject, cancellationToken),
            TransactionDeleteOwnerTypes.ProductionMaterialIssue => EvaluateMaterialIssueAsync(db, company, branch, subject, cancellationToken),
            TransactionDeleteOwnerTypes.SalesDeliveryOrder => EvaluateDeliveryOrderAsync(db, company, branch, subject, cancellationToken),
            TransactionDeleteOwnerTypes.SalesInvoice => EvaluateSalesInvoiceAsync(db, company, branch, subject, cancellationToken),
            TransactionDeleteOwnerTypes.SalesCdn => EvaluateSalesCdnAsync(db, company, branch, subject, cancellationToken),
            TransactionDeleteOwnerTypes.PurchaseInvoice => EvaluatePurchaseInvoiceAsync(db, company, branch, subject, cancellationToken),
            TransactionDeleteOwnerTypes.PurchaseCdn => EvaluatePurchaseCdnAsync(db, company, branch, subject, cancellationToken),
            TransactionDeleteOwnerTypes.ProductionOutput => EvaluateProductionOutputAsync(db, company, branch, subject, cancellationToken),
            TransactionDeleteOwnerTypes.ProductionFinishedGood => EvaluateFinishedGoodAsync(db, company, branch, subject, cancellationToken),
            _ => Task.FromResult(Block(TransactionDeleteMessages.NotDeletableStatus))
        };
    }

    private static async Task<TransactionDeleteDecision> EvaluateInventoryBatchAsync(
        AppDbContext db, string company, string branch, TransactionDeleteSubject subject, CancellationToken ct)
    {
        var batch = await FindBatchAsync(db, company, branch, subject, ct);
        if (batch is null)
            return Block(TransactionDeleteMessages.NotFound);
        var evidence = await CollectBatchEvidenceAsync(db, batch, ct);
        return Decide(
            archived: batch.DeletedAtUtc is not null,
            status: batch.BatchStatus,
            forceClosed: batch.ForceCloseDate is not null,
            historicalMarkers: evidence.Historical || batch.PostedCount > 0 || batch.RollbackCount > 0,
            incompleteLink: evidence.IncompleteLink,
            downstream: false,
            downstreamReason: null,
            eInvoiceReason: null,
            postings: evidence.Postings,
            reversedLifecycle: IsReversed(batch.BatchStatus));
    }

    private static async Task<TransactionDeleteDecision> EvaluateMaterialIssueAsync(
        AppDbContext db, string company, string branch, TransactionDeleteSubject subject, CancellationToken ct)
    {
        var batch = await FindBatchAsync(db, company, branch, subject, ct);
        if (batch is null || !string.Equals(batch.TrxType, IvTrxTypes.IssueToProduction, StringComparison.OrdinalIgnoreCase))
            return Block(TransactionDeleteMessages.NotFound);
        var evidence = await CollectBatchEvidenceAsync(db, batch, ct);
        return Decide(
            archived: batch.DeletedAtUtc is not null,
            status: batch.BatchStatus,
            forceClosed: batch.ForceCloseDate is not null,
            historicalMarkers: evidence.Historical || batch.PostedCount > 0 || batch.RollbackCount > 0,
            incompleteLink: evidence.IncompleteLink,
            downstream: false,
            downstreamReason: null,
            eInvoiceReason: null,
            postings: evidence.Postings,
            reversedLifecycle: IsReversed(batch.BatchStatus));
    }

    private static async Task<TransactionDeleteDecision> EvaluateDeliveryOrderAsync(
        AppDbContext db, string company, string branch, TransactionDeleteSubject subject, CancellationToken ct)
    {
        var doNo = DocumentNo(subject);
        var delivery = await db.SaDos.AsNoTracking().FirstOrDefaultAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.DoNo == doNo, ct);
        if (delivery is null)
            return Block(TransactionDeleteMessages.NotFound);

        var batch = await FindOwnedBatchAsync(db, company, branch, IvTrxTypes.SalesOut, SaDoSpRefs.ToRefNo(doNo), ct);
        var sources = new List<TransactionExecutionSource>();
        if (batch is not null)
            sources.Add(InventorySource(batch));
        var evidence = await CollectSourcesAsync(db, company, branch, sources, batch, ct);
        var postedInvoice = await db.SaInvoices.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.DoNo == doNo
            && x.DeletedAtUtc == null
            && x.Status == SaInvoiceStatuses.Posted, ct);
        return Decide(
            archived: delivery.DeletedAtUtc is not null || batch?.DeletedAtUtc is not null,
            status: delivery.Status,
            forceClosed: batch?.ForceCloseDate is not null,
            historicalMarkers: evidence.Historical || (batch?.PostedCount ?? 0) > 0 || (batch?.RollbackCount ?? 0) > 0 || delivery.RollbackDate is not null,
            incompleteLink: evidence.IncompleteLink,
            downstream: postedInvoice,
            downstreamReason: postedInvoice ? $"Delivery order {doNo} has a posted invoice and cannot be deleted." : null,
            eInvoiceReason: null,
            postings: evidence.Postings,
            reversedLifecycle: false,
            closedStatus: SaDoStatuses.Closed);
    }

    private static async Task<TransactionDeleteDecision> EvaluateSalesInvoiceAsync(
        AppDbContext db, string company, string branch, TransactionDeleteSubject subject, CancellationToken ct)
    {
        var invNo = DocumentNo(subject);
        var invoice = await db.SaInvoices.AsNoTracking().FirstOrDefaultAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.InvNo == invNo, ct);
        if (invoice is null)
            return Block(TransactionDeleteMessages.NotFound);

        var batch = await FindOwnedBatchAsync(db, company, branch, IvTrxTypes.SalesOut, invNo, ct);
        var sources = new List<TransactionExecutionSource>();
        if (batch is not null)
            sources.Add(InventorySource(batch));
        var evidence = await CollectSourcesAsync(db, company, branch, sources, batch, ct);
        var allocations = await db.SalesReturnCostAllocations.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.OriginalOwnerDocumentNo == invNo, ct);
        var creditNotes = await db.SaCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.InvNo == invNo && x.DeletedAtUtc == null
                && (x.Status == SaCdnStatuses.New || x.Status == SaCdnStatuses.Posted))
            .Select(x => x.DocNo)
            .ToListAsync(ct);
        string? downstream = null;
        if (creditNotes.Count > 0)
            downstream = $"Invoice {invNo} cannot be deleted because credit note(s) {string.Join(", ", creditNotes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))} depend on it.";
        return Decide(
            archived: invoice.DeletedAtUtc is not null || batch?.DeletedAtUtc is not null,
            status: invoice.Status,
            forceClosed: batch?.ForceCloseDate is not null,
            historicalMarkers: evidence.Historical || allocations || (batch?.PostedCount ?? 0) > 0 || (batch?.RollbackCount ?? 0) > 0 || invoice.RollbackDate is not null,
            incompleteLink: evidence.IncompleteLink,
            downstream: downstream is not null,
            downstreamReason: downstream,
            eInvoiceReason: EInvoiceStatuses.IsLocked(invoice.IrbmStatus)
                ? $"Invoice {invNo} cannot be deleted while its e-Invoice status is {EInvoiceStatuses.Normalize(invoice.IrbmStatus)}."
                : null,
            postings: evidence.Postings,
            reversedLifecycle: false);
    }

    private static async Task<TransactionDeleteDecision> EvaluateSalesCdnAsync(
        AppDbContext db, string company, string branch, TransactionDeleteSubject subject, CancellationToken ct)
    {
        var docNo = DocumentNo(subject);
        var cdn = await db.SaCdns.AsNoTracking().FirstOrDefaultAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.DocNo == docNo, ct);
        if (cdn is null)
            return Block(TransactionDeleteMessages.NotFound);

        var batch = await FindOwnedBatchAsync(
            db, company, branch, IvTrxTypes.CustomerReturn, SaCdnSpRefs.ToRefNo(docNo), ct);
        var sources = new List<TransactionExecutionSource>();
        if (batch is not null)
            sources.Add(InventorySource(batch));
        var evidence = await CollectSourcesAsync(db, company, branch, sources, batch, ct);
        var allocations = await db.SalesReturnCostAllocations.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.ReturnDocumentNo == docNo, ct);
        var variances = await db.SalesReturnStandardCostVariances.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.ReturnDocumentNo == docNo, ct);
        return Decide(
            archived: cdn.DeletedAtUtc is not null || batch?.DeletedAtUtc is not null,
            status: cdn.Status,
            forceClosed: false,
            historicalMarkers: evidence.Historical || allocations || variances || cdn.CostingRevision > 0
                || (batch?.PostedCount ?? 0) > 0 || (batch?.RollbackCount ?? 0) > 0 || cdn.RollbackDate is not null,
            incompleteLink: evidence.IncompleteLink,
            downstream: false,
            downstreamReason: null,
            eInvoiceReason: EInvoiceStatuses.IsLocked(cdn.IrbmStatus)
                ? $"Document {docNo} cannot be deleted while its e-Invoice status is {EInvoiceStatuses.Normalize(cdn.IrbmStatus)}."
                : null,
            postings: evidence.Postings,
            reversedLifecycle: false);
    }

    private static async Task<TransactionDeleteDecision> EvaluatePurchaseInvoiceAsync(
        AppDbContext db, string company, string branch, TransactionDeleteSubject subject, CancellationToken ct)
    {
        var docNo = DocumentNo(subject);
        var invoice = await db.PoInvoices.AsNoTracking().FirstOrDefaultAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.DocNo == docNo, ct);
        if (invoice is null)
            return Block(TransactionDeleteMessages.NotFound);

        var sourceType = string.Equals(invoice.Type, PoInvoiceTypes.CreditNote, StringComparison.OrdinalIgnoreCase)
            ? "PO_INVOICE_CN"
            : "PO_INVOICE";
        var sources = new List<TransactionExecutionSource>
        {
            new("PROCUREMENT", sourceType, docNo, docNo)
        };
        var evidence = await CollectSourcesAsync(db, company, branch, sources, physicalBatch: null, ct);
        var settlements = await db.PurchaseReceiptCostSettlements.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.PiDocNo == docNo, ct);
        var adjustments = await db.PurchaseCostAdjustments.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.SourceDocumentNo == docNo
            && (x.SourceDocumentType == "PO_INVOICE" || x.SourceDocumentType == "PO_INVOICE_CN"), ct);
        return Decide(
            archived: invoice.DeletedAtUtc is not null,
            status: invoice.Status,
            forceClosed: false,
            historicalMarkers: evidence.Historical || settlements || adjustments || invoice.CostingRevision > 0 || invoice.RollbackDate is not null,
            incompleteLink: evidence.IncompleteLink,
            downstream: false,
            downstreamReason: null,
            eInvoiceReason: null,
            postings: evidence.Postings,
            reversedLifecycle: false);
    }

    private static async Task<TransactionDeleteDecision> EvaluatePurchaseCdnAsync(
        AppDbContext db, string company, string branch, TransactionDeleteSubject subject, CancellationToken ct)
    {
        var docNo = DocumentNo(subject);
        var cdn = await db.PoCdns.AsNoTracking().FirstOrDefaultAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.DocNo == docNo, ct);
        if (cdn is null)
            return Block(TransactionDeleteMessages.NotFound);

        var sources = new List<TransactionExecutionSource>
        {
            new("PROCUREMENT", "PO_CDN", docNo, docNo)
        };
        IvTrxBatch? batch = null;
        if (cdn.VrBatchNo is int vrNo)
        {
            batch = await db.IvTrxBatches.AsNoTracking().FirstOrDefaultAsync(x =>
                x.CompanyCode == company && x.BranchCode == branch && x.BatchNo == vrNo
                && x.TrxType == IvTrxTypes.VendorReturn, ct);
        }
        batch ??= await FindOwnedBatchAsync(db, company, branch, IvTrxTypes.VendorReturn, PoCdnSpRefs.ToVrRefNo(docNo), ct);
        if (batch is not null)
            sources.Add(InventorySource(batch));
        var evidence = await CollectSourcesAsync(db, company, branch, sources, batch, ct);
        var adjustments = await db.PurchaseCostAdjustments.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.SourceDocumentNo == docNo
            && x.SourceDocumentType == "PO_CDN", ct);
        return Decide(
            archived: cdn.DeletedAtUtc is not null || batch?.DeletedAtUtc is not null,
            status: cdn.Status,
            forceClosed: false,
            historicalMarkers: evidence.Historical || adjustments || cdn.CostingRevision > 0
                || (batch?.PostedCount ?? 0) > 0 || (batch?.RollbackCount ?? 0) > 0 || cdn.RollbackDate is not null,
            incompleteLink: evidence.IncompleteLink,
            downstream: false,
            downstreamReason: null,
            eInvoiceReason: null,
            postings: evidence.Postings,
            reversedLifecycle: false);
    }

    private static async Task<TransactionDeleteDecision> EvaluateProductionOutputAsync(
        AppDbContext db, string company, string branch, TransactionDeleteSubject subject, CancellationToken ct)
    {
        if (!long.TryParse(subject.OwnerDocumentId?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid))
            return Block(TransactionDeleteMessages.NotFound);
        var output = await db.ProductionOutputs.AsNoTracking().FirstOrDefaultAsync(x =>
            x.Uid == uid && x.CompanyCode == company && x.BranchCode == branch, ct);
        if (output is null)
            return Block(TransactionDeleteMessages.NotFound);

        var sourceId = output.Uid.ToString(CultureInfo.InvariantCulture);
        var sources = new List<TransactionExecutionSource>
        {
            new("PRODUCTION", ProductionDocumentTypes.ProductionOutput, sourceId, output.DocumentNo)
        };
        var evidence = await CollectSourcesAsync(db, company, branch, sources, physicalBatch: null, ct);
        var movements = await db.ProductionBalLotMovements.AsNoTracking().AnyAsync(x => x.ProductionOutputId == output.Uid, ct);
        var material = await db.ProductionMaterialMovements.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.ProductionOutputId == output.Uid, ct);
        var links = await db.ProductionPostingLinks.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.ProductionDocumentType == ProductionDocumentTypes.ProductionOutput
                && x.ProductionDocumentNo == output.DocumentNo)
            .Select(x => x.Status)
            .ToListAsync(ct);
        var incomplete = evidence.IncompleteLink || links.Any(IsIncompleteLinkStatus);
        var executedLink = links.Any(x =>
            string.Equals(x, ProductionPostingLinkStatuses.Succeeded, StringComparison.OrdinalIgnoreCase)
            || string.Equals(x, ProductionPostingLinkStatuses.Reversed, StringComparison.OrdinalIgnoreCase));
        return Decide(
            archived: output.DeletedAtUtc is not null,
            status: output.Status,
            forceClosed: false,
            historicalMarkers: evidence.Historical || movements || material || executedLink || output.ReversedDate is not null || output.PostedDate is not null,
            incompleteLink: incomplete,
            downstream: false,
            downstreamReason: null,
            eInvoiceReason: null,
            postings: evidence.Postings,
            reversedLifecycle: string.Equals(output.Status, ProductionOutputStatuses.Reversed, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<TransactionDeleteDecision> EvaluateFinishedGoodAsync(
        AppDbContext db, string company, string branch, TransactionDeleteSubject subject, CancellationToken ct)
    {
        if (!int.TryParse(subject.OwnerDocumentId?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var batchId))
            return Block(TransactionDeleteMessages.NotFound);
        var receipt = await db.ProductionFinishedGoodReceiptRows.AsNoTracking()
            .FirstOrDefaultAsync(x => x.BatchId == batchId && x.CompanyCode == company && x.BranchCode == branch, ct);
        if (receipt is null)
            return Block(TransactionDeleteMessages.NotFound);
        var batch = await db.IvTrxBatches.AsNoTracking().FirstOrDefaultAsync(x =>
            x.Id == batchId && x.CompanyCode == company && x.BranchCode == branch, ct);
        if (batch is null)
            return Block(TransactionDeleteMessages.NotFound);

        var sources = new List<TransactionExecutionSource>
        {
            new("PRODUCTION", ProductionDocumentTypes.FinishedGoodReceipt, batch.Id.ToString(CultureInfo.InvariantCulture), batch.BatchNo.ToString(CultureInfo.InvariantCulture), batch.BatchNo),
            InventorySource(batch)
        };
        var evidence = await CollectSourcesAsync(db, company, branch, sources, batch, ct);
        var facts = await db.ProductionFinishedGoodFactRows.AsNoTracking().AnyAsync(x => x.BatchId == batch.Id, ct);
        var postingIds = evidence.Postings.Select(x => x.Id).ToArray();
        var snapshots = postingIds.Length > 0 && await db.ProductionFinishedGoodPriceSnapshotRows.AsNoTracking()
            .AnyAsync(x => postingIds.Contains(x.StockPostingId), ct);
        return Decide(
            archived: receipt.DeletedAtUtc is not null || batch.DeletedAtUtc is not null,
            status: batch.BatchStatus,
            forceClosed: batch.ForceCloseDate is not null,
            historicalMarkers: evidence.Historical || facts || snapshots || receipt.PostingId is not null || receipt.ReversalPostingId is not null
                || batch.PostedCount > 0 || batch.RollbackCount > 0,
            incompleteLink: evidence.IncompleteLink,
            downstream: false,
            downstreamReason: null,
            eInvoiceReason: null,
            postings: evidence.Postings,
            reversedLifecycle: IsReversed(batch.BatchStatus));
    }

    private static async Task<BatchEvidence> CollectBatchEvidenceAsync(AppDbContext db, IvTrxBatch batch, CancellationToken ct)
    {
        var sources = new List<TransactionExecutionSource> { InventorySource(batch) };
        return await CollectSourcesAsync(db, batch.CompanyCode, batch.BranchCode, sources, batch, ct);
    }

    private static async Task<BatchEvidence> CollectSourcesAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyList<TransactionExecutionSource> sources,
        IvTrxBatch? physicalBatch,
        CancellationToken ct)
    {
        var postings = new List<StockPosting>();
        foreach (var source in sources)
        {
            var rows = await db.StockPostings.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch
                    && x.SourceModule == source.SourceModule
                    && x.SourceDocumentType == source.SourceDocumentType
                    && x.SourceDocumentId == source.SourceDocumentId)
                .ToListAsync(ct);
            postings.AddRange(rows);
        }

        var historical = false;
        var incomplete = false;
        if (physicalBatch is not null)
        {
            historical = await InventoryBatchExecutionProbe.HasEverExecutedAsync(db, physicalBatch, ct);
            incomplete = await db.ProductionPostingLinks.AsNoTracking().AnyAsync(x =>
                x.CompanyCode == physicalBatch.CompanyCode
                && x.BranchCode == physicalBatch.BranchCode
                && x.InventoryBatchNo == physicalBatch.BatchNo
                && (x.Status == ProductionPostingLinkStatuses.Pending || x.Status == ProductionPostingLinkStatuses.Failed), ct);
        }

        if (postings.Count > 0)
            historical = true;
        return new BatchEvidence(postings, historical, incomplete);
    }

    private static TransactionDeleteDecision Decide(
        bool archived,
        string? status,
        bool forceClosed,
        bool historicalMarkers,
        bool incompleteLink,
        bool downstream,
        string? downstreamReason,
        string? eInvoiceReason,
        IReadOnlyList<StockPosting> postings,
        bool reversedLifecycle,
        string? closedStatus = null)
    {
        var (historicalPosting, unsealed, active) = Classify(postings);
        var historicalMovement = historicalMarkers || historicalPosting;
        var posted = IsPosted(status);
        var closed = closedStatus is not null && string.Equals(status, closedStatus, StringComparison.OrdinalIgnoreCase);
        var draft = string.Equals(status, "NEW", StringComparison.OrdinalIgnoreCase);

        if (unsealed || incompleteLink)
        {
            return new TransactionDeleteDecision(
                TransactionDeleteMode.Block, historicalPosting, active, true, historicalMovement, downstream,
                TransactionDeleteMessages.IncompletePosting);
        }

        if (active || (posted && !archived))
        {
            return new TransactionDeleteDecision(
                TransactionDeleteMode.Block, historicalPosting, true, false, historicalMovement, downstream,
                forceClosed ? TransactionDeleteMessages.ForceClosed : TransactionDeleteMessages.RollbackFirst);
        }

        if (archived)
        {
            return new TransactionDeleteDecision(
                TransactionDeleteMode.ArchiveHistorical, historicalPosting, false, false, historicalMovement, false, null);
        }

        if (forceClosed)
        {
            return new TransactionDeleteDecision(
                TransactionDeleteMode.Block, historicalPosting, false, false, historicalMovement, downstream,
                TransactionDeleteMessages.ForceClosed);
        }

        if (closed)
        {
            return new TransactionDeleteDecision(
                TransactionDeleteMode.Block, historicalPosting, false, false, historicalMovement, downstream,
                TransactionDeleteMessages.Closed);
        }

        if (eInvoiceReason is not null)
        {
            return new TransactionDeleteDecision(
                TransactionDeleteMode.Block, historicalPosting, false, false, historicalMovement, downstream, eInvoiceReason);
        }

        if (downstream)
        {
            return new TransactionDeleteDecision(
                TransactionDeleteMode.Block, historicalPosting, false, false, historicalMovement, true,
                downstreamReason ?? "This document has an active downstream dependency and cannot be deleted.");
        }

        if (historicalMovement || reversedLifecycle)
        {
            return new TransactionDeleteDecision(
                TransactionDeleteMode.ArchiveHistorical, historicalPosting, false, false, true, false, null);
        }

        if (draft)
        {
            return new TransactionDeleteDecision(
                TransactionDeleteMode.HardDeleteDraft, false, false, false, false, false, null);
        }

        return new TransactionDeleteDecision(
            TransactionDeleteMode.Block, historicalPosting, false, false, historicalMovement, false,
            TransactionDeleteMessages.NotDeletableStatus);
    }

    private static (bool Historical, bool Unsealed, bool Active) Classify(IReadOnlyList<StockPosting> postings)
    {
        var reversed = postings
            .Where(x => IsReversal(x) && x.SealedAtUtc is not null && x.ReversesPostingId is not null)
            .Select(x => x.ReversesPostingId!.Value)
            .ToHashSet();
        var unsealed = postings.Any(x => x.SealedAtUtc is null);
        var active = postings.Any(x => x.SealedAtUtc is not null && !IsReversal(x) && !reversed.Contains(x.Id));
        return (postings.Count > 0, unsealed, active);
    }

    private static bool IsReversal(StockPosting posting) =>
        string.Equals(posting.PostingRole, "REVERSAL", StringComparison.OrdinalIgnoreCase);

    private static bool IsPosted(string? status) =>
        string.Equals(status, "POSTED", StringComparison.OrdinalIgnoreCase);

    private static bool IsReversed(string? status) =>
        string.Equals(status, "REVERSED", StringComparison.OrdinalIgnoreCase);

    private static bool IsIncompleteLinkStatus(string? status) =>
        string.Equals(status, ProductionPostingLinkStatuses.Pending, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, ProductionPostingLinkStatuses.Failed, StringComparison.OrdinalIgnoreCase);

    private static TransactionExecutionSource InventorySource(IvTrxBatch batch) =>
        new(
            "INVENTORY",
            batch.TrxType,
            batch.Id.ToString(CultureInfo.InvariantCulture),
            batch.BatchNo.ToString(CultureInfo.InvariantCulture),
            batch.BatchNo);

    private static string DocumentNo(TransactionDeleteSubject subject)
    {
        var id = (subject.OwnerDocumentId ?? string.Empty).Trim();
        if (id.Length > 0)
            return id;
        return (subject.OwnerDocumentNo ?? string.Empty).Trim();
    }

    private static async Task<IvTrxBatch?> FindBatchAsync(
        AppDbContext db, string company, string branch, TransactionDeleteSubject subject, CancellationToken ct)
    {
        if (int.TryParse(subject.OwnerDocumentId?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            var byId = await db.IvTrxBatches.AsNoTracking().FirstOrDefaultAsync(x =>
                x.Id == id && x.CompanyCode == company && x.BranchCode == branch, ct);
            if (byId is not null)
                return byId;
        }

        if (int.TryParse(subject.OwnerDocumentNo?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var batchNo))
        {
            return await db.IvTrxBatches.AsNoTracking().FirstOrDefaultAsync(x =>
                x.CompanyCode == company && x.BranchCode == branch && x.BatchNo == batchNo, ct);
        }

        return null;
    }

    private static Task<IvTrxBatch?> FindOwnedBatchAsync(
        AppDbContext db, string company, string branch, string trxType, string refNo, CancellationToken ct) =>
        db.IvTrxBatches.AsNoTracking().FirstOrDefaultAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.TrxType == trxType && x.RefNo == refNo, ct);

    private static TransactionDeleteDecision Block(string reason) =>
        new(TransactionDeleteMode.Block, false, false, false, false, false, reason);

    private sealed record BatchEvidence(List<StockPosting> Postings, bool Historical, bool IncompleteLink);
}
