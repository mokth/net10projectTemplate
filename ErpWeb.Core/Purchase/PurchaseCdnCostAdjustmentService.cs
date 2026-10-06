using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public interface IPurchaseCdnCostAdjustmentService
{
    Task<PurchaseReceiptSettlementResult> BuildAsync(
        AppDbContext db,
        PoCdn cdn,
        PoInvoice? referencedInvoice,
        string costMethod,
        CancellationToken cancellationToken = default);

    Task<PurchaseReceiptSettlementResult> ReverseAsync(
        AppDbContext db,
        PoCdn cdn,
        long primaryStockPostingId,
        string userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds the financial leg for the newer Purchase CN/DN document. Physical vendor returns are
/// valued by the inventory posting service; this service records only the supplier-credit/debit
/// variance against immutable PI settlement evidence.
/// </summary>
public sealed class PurchaseCdnCostAdjustmentService : IPurchaseCdnCostAdjustmentService
{
    private const decimal Epsilon = 0.0000005m;

    public async Task<PurchaseReceiptSettlementResult> BuildAsync(
        AppDbContext db,
        PoCdn cdn,
        PoInvoice? referencedInvoice,
        string costMethod,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(cdn);

        var type = PoCdnCalc.NormalizeType(cdn.Type);
        if (type is not (PoCdnTypes.CreditNote or PoCdnTypes.DebitNote))
            return PurchaseReceiptSettlementResult.Fail("Purchase CN/DN type is invalid.");
        if (cdn.CurrRate <= 0m)
            return PurchaseReceiptSettlementResult.Fail("Purchase CN/DN currency rate must be greater than zero.");
        if (string.IsNullOrWhiteSpace(costMethod))
            return PurchaseReceiptSettlementResult.Fail("An active inventory cost method is required for Purchase CN/DN costing.");

        var lines = cdn.Details
            .Where(x => x.NetAmount > 0m && !string.IsNullOrWhiteSpace(x.ICode))
            .OrderBy(x => x.Line)
            .ToArray();
        if (lines.Length == 0)
            return PurchaseReceiptSettlementResult.Success([], []);
        if (referencedInvoice is null
            || !string.Equals(referencedInvoice.Type, PoInvoiceTypes.Invoice, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(referencedInvoice.Status, PoInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            return PurchaseReceiptSettlementResult.Fail(
                "Active V2 Purchase CN/DN costing requires a posted referenced purchase invoice.");
        }

        var missingReference = lines.FirstOrDefault(x => x.InvLineNo is null);
        if (missingReference is not null)
        {
            return PurchaseReceiptSettlementResult.Fail(
                $"Line {missingReference.Line}: exact referenced purchase-invoice line is required for V2 costing.");
        }

        var referencedLineNos = lines.Select(x => x.InvLineNo!.Value).Distinct().ToArray();
        var settlementRows = await db.PurchaseReceiptCostSettlements.AsNoTracking()
            .Where(x => x.CompanyCode == referencedInvoice.CompanyCode
                        && x.BranchCode == referencedInvoice.BranchCode
                        && x.PiDocNo == referencedInvoice.DocNo
                        && x.PiCostingRevision == referencedInvoice.CostingRevision
                        && referencedLineNos.Contains(x.PiLineNo))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var settlementRoots = settlementRows.Where(x => x.ReversesSettlementId is null).ToList();
        if (settlementRoots.Count == 0)
            return PurchaseReceiptSettlementResult.Fail(
                $"No active PI settlement evidence was found for purchase invoice {referencedInvoice.DocNo}.");

        var duplicateSettlementParents = settlementRows
            .Where(x => x.ReversesSettlementId is not null)
            .GroupBy(x => x.ReversesSettlementId!.Value)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicateSettlementParents is not null)
            return PurchaseReceiptSettlementResult.Fail(
                $"PI settlement {duplicateSettlementParents.Key} has more than one reversal child.");
        var settlementChildren = settlementRows
            .Where(x => x.ReversesSettlementId is not null)
            .ToDictionary(x => x.ReversesSettlementId!.Value);
        var activeRows = new List<PurchaseReceiptCostSettlement>(settlementRoots.Count);
        foreach (var root in settlementRoots)
        {
            var chain = BuildSettlementChain(root, settlementChildren);
            var activeQty = SignedSettlementQuantity(chain);
            if (activeQty <= Epsilon)
                continue;
            if (chain.Count % 2 == 0)
                return PurchaseReceiptSettlementResult.Fail(
                    $"PI settlement {root.Id} is partially reversed and cannot be reused safely.");
            activeRows.Add(chain[^1]);
        }
        if (activeRows.Count == 0)
            return PurchaseReceiptSettlementResult.Fail("The referenced PI settlement quantity is already fully reversed.");

        var remainingSettlementQty = activeRows.ToDictionary(
            x => x.Id,
            x => StockLedgerPrecision.Quantity(x.SettledBaseQty));
        var remainingSettlementReference = activeRows.ToDictionary(
            x => x.Id,
            x => StockLedgerPrecision.Money(x.ReceiptCommercialBaseAmount));

        var factIds = activeRows.Select(x => x.ReceiptValuationFactId).Distinct().ToArray();
        var baseUoms = await db.StockValuationFacts.AsNoTracking()
            .Where(x => factIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.BaseUom, cancellationToken);
        var method = costMethod.Trim().ToUpperInvariant();
        var itemCodes = activeRows.Select(x => x.ItemCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var states = await db.StockCostStates
            .Where(x => x.CompanyCode == referencedInvoice.CompanyCode
                        && x.BranchCode == referencedInvoice.BranchCode
                        && x.CostMethod == method
                        && itemCodes.Contains(x.ItemCode))
            .ToListAsync(cancellationToken);
        var remainingCapitalizableQty = states.ToDictionary(
            x => x.ItemCode,
            x => StockLedgerPrecision.Quantity(Math.Max(x.OnHandBaseQty, 0m)),
            StringComparer.OrdinalIgnoreCase);
        var runningValues = states.ToDictionary(
            x => x.ItemCode,
            x => StockLedgerPrecision.Money(x.InventoryValue),
            StringComparer.OrdinalIgnoreCase);

        var sign = type == PoCdnTypes.CreditNote ? -1m : 1m;
        var adjustmentType = cdn.ReturnStock
            ? "PURCHASE_RETURN_VARIANCE"
            : type == PoCdnTypes.CreditNote
                ? "PURCHASE_CN_PRICE_ADJUSTMENT"
                : "PURCHASE_DN_PRICE_ADJUSTMENT";
        var result = new List<PurchaseCostAdjustmentRequest>();
        var splitByLine = new Dictionary<int, int>();

        foreach (var line in lines)
        {
            var itemCode = line.ICode!.Trim();
            var sourceLine = referencedInvoice.Details.FirstOrDefault(x => x.Line == line.InvLineNo!.Value);
            if (sourceLine is null)
                return PurchaseReceiptSettlementResult.Fail(
                    $"Line {line.Line}: referenced purchase-invoice line {line.InvLineNo} was not found.");

            var candidates = activeRows
                .Where(x => x.PiLineNo == sourceLine.Line
                            && string.Equals(x.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Id)
                .ToArray();
            if (candidates.Length == 0)
                return PurchaseReceiptSettlementResult.Fail(
                    $"Line {line.Line}: no active PI settlement matches referenced invoice line {sourceLine.Line} and item {line.ICode}.");

            var lineQty = StockLedgerPrecision.Quantity(line.StdQty);
            if (lineQty <= 0m)
                lineQty = StockLedgerPrecision.Quantity(candidates.Sum(x => remainingSettlementQty[x.Id]));
            if (lineQty <= 0m)
                return PurchaseReceiptSettlementResult.Fail($"Line {line.Line}: a positive settled quantity is required.");

            var remainingQty = lineQty;
            var actualRemaining = StockLedgerPrecision.Money(line.NetAmount * cdn.CurrRate);
            foreach (var candidate in candidates)
            {
                if (remainingQty <= Epsilon)
                    break;

                var candidateRemainingQty = remainingSettlementQty[candidate.Id];
                if (candidateRemainingQty <= Epsilon)
                    continue;
                var sliceQty = StockLedgerPrecision.Quantity(
                    Math.Min(remainingQty, candidateRemainingQty));
                if (sliceQty <= Epsilon)
                    continue;
                var finalSlice = remainingQty <= sliceQty + Epsilon;
                var ratio = sliceQty / lineQty;
                var actual = finalSlice
                    ? actualRemaining
                    : StockLedgerPrecision.Money(line.NetAmount * cdn.CurrRate * ratio);
                var candidateReferenceRemaining = remainingSettlementReference[candidate.Id];
                var reference = StockLedgerPrecision.Money(
                    sliceQty >= candidateRemainingQty - Epsilon
                        ? candidateReferenceRemaining
                        : candidateReferenceRemaining * sliceQty / candidateRemainingQty);
                var signedTotal = StockLedgerPrecision.Money(sign * actual);
                var appliedInventory = 0m;

                if (!cdn.ReturnStock && method != StockCostMethods.Standard)
                {
                    var budget = remainingCapitalizableQty.GetValueOrDefault(itemCode);
                    var capitalizableQty = StockLedgerPrecision.Quantity(Math.Min(budget, sliceQty));
                    appliedInventory = capitalizableQty <= 0m
                        ? 0m
                        : StockLedgerPrecision.Money(signedTotal * capitalizableQty / sliceQty);
                    var runningValue = runningValues.GetValueOrDefault(itemCode);
                    if (appliedInventory < 0m)
                        appliedInventory = Math.Max(-runningValue, appliedInventory);
                    appliedInventory = StockLedgerPrecision.Money(appliedInventory);
                    remainingCapitalizableQty[itemCode] = StockLedgerPrecision.Quantity(
                        budget - capitalizableQty);
                    runningValues[itemCode] = StockLedgerPrecision.Money(
                        runningValue + appliedInventory);
                }

                var consumedVariance = StockLedgerPrecision.Money(signedTotal - appliedInventory);
                StockValueAdjustmentRequest? valueAdjustment = null;
                if (appliedInventory != 0m)
                {
                    if (!baseUoms.TryGetValue(candidate.ReceiptValuationFactId, out var baseUom)
                        || string.IsNullOrWhiteSpace(baseUom))
                    {
                        return PurchaseReceiptSettlementResult.Fail(
                            $"PI settlement {candidate.Id} is missing immutable base-UOM evidence.");
                    }

                    var split = splitByLine.GetValueOrDefault(line.Line);
                    splitByLine[line.Line] = split + 1;
                    valueAdjustment = new StockValueAdjustmentRequest(
                        line.Line,
                        split,
                        itemCode,
                        baseUom,
                        Math.Abs(appliedInventory),
                        appliedInventory > 0m ? 1 : -1,
                        appliedInventory > 0m
                            ? "PURCHASE_PRICE_VARIANCE_IN"
                            : "PURCHASE_PRICE_VARIANCE_OUT",
                        StockValuationSources.PurchasePriceVariance,
                        SourceDocumentLine: line.Line.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        OriginalValuationFactId: candidate.ReceiptValuationFactId);
                }

                result.Add(new PurchaseCostAdjustmentRequest(
                    AdjustmentType: adjustmentType,
                    SourceDocumentType: "PO_CDN",
                    SourceDocumentNo: cdn.DocNo,
                    SourceDocumentLine: line.Line,
                    SourceCostingRevision: cdn.CostingRevision,
                    ItemCode: itemCode,
                    CostMethod: method,
                    BaseQty: sliceQty,
                    ActualBaseAmount: actual,
                    ReferenceBaseAmount: reference,
                    CommercialReferenceAmount: reference,
                    TotalAdjustmentAmount: signedTotal,
                    InventoryAdjustmentAmount: appliedInventory,
                    ConsumedVarianceAmount: consumedVariance,
                    ValueAdjustment: valueAdjustment,
                    PoNo: candidate.PoNo,
                    PoRelNo: candidate.PoRelNo,
                    PoLineNo: candidate.PoLineNo));

                remainingQty = StockLedgerPrecision.Quantity(remainingQty - sliceQty);
                actualRemaining = StockLedgerPrecision.Money(actualRemaining - actual);
                remainingSettlementQty[candidate.Id] = StockLedgerPrecision.Quantity(
                    candidateRemainingQty - sliceQty);
                remainingSettlementReference[candidate.Id] = StockLedgerPrecision.Money(
                    candidateReferenceRemaining - reference);
            }

            if (remainingQty > Epsilon)
            {
                return PurchaseReceiptSettlementResult.Fail(
                    $"Line {line.Line}: quantity exceeds active PI settlement quantity.");
            }
        }

        return PurchaseReceiptSettlementResult.Success([], result);
    }

    private static IReadOnlyList<PurchaseReceiptCostSettlement> BuildSettlementChain(
        PurchaseReceiptCostSettlement root,
        IReadOnlyDictionary<long, PurchaseReceiptCostSettlement> children)
    {
        var chain = new List<PurchaseReceiptCostSettlement> { root };
        var seen = new HashSet<long> { root.Id };
        var current = root;
        while (children.TryGetValue(current.Id, out var child))
        {
            if (!seen.Add(child.Id))
                break;
            chain.Add(child);
            current = child;
        }

        return chain;
    }

    private static decimal SignedSettlementQuantity(
        IReadOnlyList<PurchaseReceiptCostSettlement> chain)
    {
        decimal total = 0m;
        for (var index = 0; index < chain.Count; index++)
            total += (index % 2 == 0 ? 1m : -1m) * chain[index].SettledBaseQty;
        return StockLedgerPrecision.Quantity(total);
    }

    public async Task<PurchaseReceiptSettlementResult> ReverseAsync(
        AppDbContext db,
        PoCdn cdn,
        long primaryStockPostingId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(cdn);
        if (primaryStockPostingId <= 0)
            return PurchaseReceiptSettlementResult.Fail("A primary Purchase CN/DN costing posting is required for rollback.");

        var originals = await db.PurchaseCostAdjustments.AsNoTracking()
            .Where(x => x.CompanyCode == cdn.CompanyCode
                        && x.BranchCode == cdn.BranchCode
                        && x.SourceDocumentType == "PO_CDN"
                        && x.SourceDocumentNo == cdn.DocNo
                        && x.SourceCostingRevision == cdn.CostingRevision
                        && x.StockPostingId == primaryStockPostingId
                        && x.ReversesAdjustmentId == null)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (originals.Count == 0)
            return PurchaseReceiptSettlementResult.Success([], []);

        var ids = originals.Select(x => x.Id).ToArray();
        if (await db.PurchaseCostAdjustments.AsNoTracking().AnyAsync(
                x => x.ReversesAdjustmentId != null && ids.Contains(x.ReversesAdjustmentId.Value),
                cancellationToken))
        {
            return PurchaseReceiptSettlementResult.Fail("The Purchase CN/DN costing adjustment has already been reversed.");
        }

        var factIds = originals.Where(x => x.InventoryAdjustmentFactId is not null)
            .Select(x => x.InventoryAdjustmentFactId!.Value)
            .Distinct()
            .ToArray();
        var baseUoms = factIds.Length == 0
            ? new Dictionary<long, string>()
            : await db.StockValuationFacts.AsNoTracking()
                .Where(x => factIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.BaseUom, cancellationToken);
        var method = originals.First().CostMethod;
        var itemCodes = originals.Select(x => x.ItemCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var states = await db.StockCostStates
            .Where(x => x.CompanyCode == cdn.CompanyCode
                        && x.BranchCode == cdn.BranchCode
                        && x.CostMethod == method
                        && itemCodes.Contains(x.ItemCode))
            .ToListAsync(cancellationToken);
        var runningValues = states.ToDictionary(
            x => x.ItemCode,
            x => StockLedgerPrecision.Money(x.InventoryValue),
            StringComparer.OrdinalIgnoreCase);
        var result = new List<PurchaseCostAdjustmentRequest>(originals.Count);
        var splitByLine = new Dictionary<int, int>();
        foreach (var original in originals)
        {
            var reversalTotal = StockLedgerPrecision.Money(-original.TotalAdjustmentAmount);
            var desiredInventory = StockLedgerPrecision.Money(-original.InventoryAdjustmentAmount);
            var runningValue = runningValues.GetValueOrDefault(original.ItemCode);
            var appliedInventory = desiredInventory < 0m
                ? Math.Max(-runningValue, desiredInventory)
                : desiredInventory;
            appliedInventory = StockLedgerPrecision.Money(appliedInventory);
            runningValues[original.ItemCode] = StockLedgerPrecision.Money(runningValue + appliedInventory);
            var consumedVariance = StockLedgerPrecision.Money(reversalTotal - appliedInventory);

            StockValueAdjustmentRequest? valueAdjustment = null;
            if (appliedInventory != 0m)
            {
                if (original.InventoryAdjustmentFactId is not long factId
                    || !baseUoms.TryGetValue(factId, out var baseUom)
                    || string.IsNullOrWhiteSpace(baseUom))
                {
                    return PurchaseReceiptSettlementResult.Fail(
                        $"Purchase CN/DN adjustment {original.Id} is missing immutable base-UOM evidence.");
                }

                var split = splitByLine.GetValueOrDefault(original.SourceDocumentLine);
                splitByLine[original.SourceDocumentLine] = split + 1;
                valueAdjustment = new StockValueAdjustmentRequest(
                    original.SourceDocumentLine,
                    split,
                    original.ItemCode,
                    baseUom,
                    Math.Abs(appliedInventory),
                    appliedInventory > 0m ? 1 : -1,
                    appliedInventory > 0m
                        ? "PURCHASE_PRICE_VARIANCE_IN"
                        : "PURCHASE_PRICE_VARIANCE_OUT",
                    StockValuationSources.PurchasePriceVariance,
                    SourceDocumentLine: original.SourceDocumentLine.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    OriginalValuationFactId: original.InventoryAdjustmentFactId,
                    ReversesValuationFactId: original.InventoryAdjustmentFactId);
            }

            result.Add(new PurchaseCostAdjustmentRequest(
                AdjustmentType: original.AdjustmentType,
                SourceDocumentType: "PO_CDN",
                SourceDocumentNo: cdn.DocNo,
                SourceDocumentLine: original.SourceDocumentLine,
                SourceCostingRevision: cdn.CostingRevision,
                ItemCode: original.ItemCode,
                CostMethod: original.CostMethod,
                BaseQty: original.BaseQty,
                ActualBaseAmount: original.ActualBaseAmount,
                ReferenceBaseAmount: original.ReferenceBaseAmount,
                CommercialReferenceAmount: original.CommercialReferenceAmount,
                TotalAdjustmentAmount: reversalTotal,
                InventoryAdjustmentAmount: appliedInventory,
                ConsumedVarianceAmount: consumedVariance,
                ValueAdjustment: valueAdjustment,
                PoNo: original.PoNo,
                PoRelNo: original.PoRelNo,
                PoLineNo: original.PoLineNo,
                ReversesAdjustmentId: original.Id));
        }

        return PurchaseReceiptSettlementResult.Success([], result);
    }
}
