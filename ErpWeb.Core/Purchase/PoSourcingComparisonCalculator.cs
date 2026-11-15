using ErpWeb.Model.Entities.Purchase;

namespace ErpWeb.Core.Purchase;

public sealed record PoSourcingOfferEvaluation
{
    public long QuoteId { get; init; }
    public long QuoteLineId { get; init; }
    public long SourcingLineId { get; init; }
    public long SourcingSupplierId { get; init; }
    public string? SuppCode { get; init; }
    public string SupplierName { get; init; } = string.Empty;
    public string SupplierQuoteRef { get; init; } = string.Empty;
    public short QuoteRevNo { get; init; }
    public string LineStatus { get; init; } = PoSourcingQuoteLineStatuses.Quoted;
    public bool IsNoQuote => string.Equals(LineStatus, PoSourcingQuoteLineStatuses.NoQuote, StringComparison.OrdinalIgnoreCase);
    public bool IsExpired { get; init; }
    public bool IsQuoteActive { get; init; }
    public bool IsComparable { get; init; }
    public string? NonComparableReason { get; init; }
    public bool HasMoqWarning { get; init; }
    public string? MoqWarningMessage { get; init; }
    public bool IsZeroPriceOffer { get; init; }
    public string Currency { get; init; } = string.Empty;
    public decimal? ExchangeRateToBase { get; init; }
    public DateTime? ExchangeRateDate { get; init; }
    public bool IsInclusive { get; init; }
    public string QuotedUom { get; init; } = string.Empty;
    public decimal QuotedPackSz { get; init; }
    public decimal QuotedQty { get; init; }
    public decimal QuotedStdQty { get; init; }
    public decimal EquivalentRequestedPurQty { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal DiscPer { get; init; }
    public decimal DiscAmt { get; init; }
    public string? TaxGroupCode { get; init; }
    public decimal TaxPer { get; init; }
    public decimal NonRecoverableTaxPer { get; init; }
    public decimal LineCharges { get; init; }
    public decimal AllocatedHeaderChargesQuoteCurr { get; init; }
    public decimal GoodsValueBase { get; init; }
    public decimal NormalizedBaseUnitCost { get; init; }
    public decimal NormalizedComparableLandedTotal { get; init; }
    public decimal NormalizedTaxInclusivePayableTotal { get; init; }
    public int? LeadTimeDays { get; init; }
    public DateTime? EstimatedDeliveryDate { get; init; }
    public decimal? Moq { get; init; }
    public bool IsLowestComparableOffer { get; init; }
    public bool IsShortestLeadTimeOffer { get; init; }
}

public sealed record PoSourcingLineComparisonResult
{
    public long SourcingLineId { get; init; }
    public short LineNo { get; init; }
    public string? ICode { get; init; }
    public string Description { get; init; } = string.Empty;
    public bool OneTimeItemYn { get; init; }
    public string RequestedUom { get; init; } = string.Empty;
    public string StdUom { get; init; } = string.Empty;
    public decimal PackSz { get; init; }
    public decimal RequestedQty { get; init; }
    public decimal RequestedStdQty { get; init; }
    public int TotalQuotesReceived { get; init; }
    public int ComparableOfferCount { get; init; }
    public bool OnlyOneSupplierQuoted => TotalQuotesReceived == 1;
    public long? RecommendedQuoteLineId { get; init; }
    public decimal? LowestComparableTotal { get; init; }
    public decimal? LowestNormalizedBaseUnitCost { get; init; }
    public long? ShortestLeadTimeQuoteLineId { get; init; }
    public IReadOnlyList<PoSourcingOfferEvaluation> Offers { get; init; } = [];
}

public sealed record PoSourcingComparisonMatrix
{
    public long SourcingId { get; init; }
    public string BaseCurrency { get; init; } = string.Empty;
    public DateTime EvaluationDate { get; init; }
    public bool OnlyOneSupplierQuotedOverall { get; init; }
    public IReadOnlyList<PoSourcingLineComparisonResult> Lines { get; init; } = [];
}

public static class PoSourcingComparisonCalculator
{
    private const decimal QtyTolerance = 0.0005m;

    public static PoSourcingComparisonMatrix Evaluate(
        PoSourcingHdr header,
        IReadOnlyList<PoSourcingLine> requirementLines,
        IReadOnlyList<PoSourcingSupplier> suppliers,
        IReadOnlyList<PoSourcingQuote> quotes,
        DateTime evaluationDate)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(requirementLines);
        ArgumentNullException.ThrowIfNull(suppliers);
        ArgumentNullException.ThrowIfNull(quotes);

        var baseCurrency = (header.BaseCurrency ?? string.Empty).Trim().ToUpperInvariant();
        var evalDate = evaluationDate.Date;
        var supplierById = suppliers.ToDictionary(s => s.SourcingSupplierId);
        var reqById = requirementLines.ToDictionary(r => r.SourcingLineId);

        // Consider only the latest non-superseded quote revision per supplier (or active received/expired/withdrawn quotes).
        var activeQuotes = quotes
            .Where(q => !string.Equals(q.Status, PoSourcingQuoteStatuses.Superseded, StringComparison.OrdinalIgnoreCase))
            .OrderBy(q => q.SourcingSupplierId)
            .ThenByDescending(q => q.QuoteRevNo)
            .ToList();

        // Pre-calculate per-quote header freight/other charge allocation across quoted lines.
        var headerAllocationsByQuoteLineId = ComputeHeaderAllocations(activeQuotes, reqById);

        var lineResults = new List<PoSourcingLineComparisonResult>(requirementLines.Count);
        var distinctQuotedSuppliers = new HashSet<long>();

        foreach (var req in requirementLines.OrderBy(x => x.LineNo))
        {
            var offers = new List<PoSourcingOfferEvaluation>();

            foreach (var quote in activeQuotes)
            {
                supplierById.TryGetValue(quote.SourcingSupplierId, out var supplier);
                var qLine = quote.Lines.FirstOrDefault(l => l.SourcingLineId == req.SourcingLineId);
                if (qLine is null)
                {
                    continue;
                }

                var eval = EvaluateSingleOffer(
                    baseCurrency,
                    evalDate,
                    req,
                    supplier,
                    quote,
                    qLine,
                    headerAllocationsByQuoteLineId.GetValueOrDefault(qLine.QuoteLineId, 0m));

                if (!eval.IsNoQuote && eval.IsQuoteActive)
                {
                    distinctQuotedSuppliers.Add(quote.SourcingSupplierId);
                }

                offers.Add(eval);
            }

            var comparableEligible = offers
                .Where(o => o.IsComparable && o.IsQuoteActive && !o.IsExpired && !o.IsNoQuote)
                .ToList();

            long? recommendedQuoteLineId = null;
            decimal? lowestComparableTotal = null;
            decimal? lowestBaseUnitCost = null;

            if (comparableEligible.Count > 0)
            {
                var best = comparableEligible
                    .OrderBy(o => o.NormalizedComparableLandedTotal)
                    .ThenBy(o => o.NormalizedBaseUnitCost)
                    .ThenBy(o => o.LeadTimeDays ?? int.MaxValue)
                    .ThenBy(o => o.QuoteLineId)
                    .First();
                recommendedQuoteLineId = best.QuoteLineId;
                lowestComparableTotal = best.NormalizedComparableLandedTotal;
                lowestBaseUnitCost = best.NormalizedBaseUnitCost;
            }

            long? shortestLeadQuoteLineId = null;
            var withLead = comparableEligible.Where(o => o.LeadTimeDays.HasValue && o.LeadTimeDays.Value >= 0).ToList();
            if (withLead.Count > 0)
            {
                shortestLeadQuoteLineId = withLead
                    .OrderBy(o => o.LeadTimeDays!.Value)
                    .ThenBy(o => o.NormalizedComparableLandedTotal)
                    .ThenBy(o => o.QuoteLineId)
                    .First()
                    .QuoteLineId;
            }

            var markedOffers = offers
                .Select(o => o with
                {
                    IsLowestComparableOffer = recommendedQuoteLineId.HasValue && o.QuoteLineId == recommendedQuoteLineId.Value,
                    IsShortestLeadTimeOffer = shortestLeadQuoteLineId.HasValue && o.QuoteLineId == shortestLeadQuoteLineId.Value
                })
                .ToList();

            var totalQuotesReceived = markedOffers.Count(o => !o.IsNoQuote && o.IsQuoteActive);

            lineResults.Add(new PoSourcingLineComparisonResult
            {
                SourcingLineId = req.SourcingLineId,
                LineNo = req.LineNo,
                ICode = req.ICode,
                Description = req.Description,
                OneTimeItemYn = req.OneTimeItemYn,
                RequestedUom = req.RequestedUom,
                StdUom = req.StdUom,
                PackSz = req.PackSz,
                RequestedQty = req.RequestedQty,
                RequestedStdQty = req.RequestedStdQty,
                TotalQuotesReceived = totalQuotesReceived,
                ComparableOfferCount = comparableEligible.Count,
                RecommendedQuoteLineId = recommendedQuoteLineId,
                LowestComparableTotal = lowestComparableTotal,
                LowestNormalizedBaseUnitCost = lowestBaseUnitCost,
                ShortestLeadTimeQuoteLineId = shortestLeadQuoteLineId,
                Offers = markedOffers
            });
        }

        return new PoSourcingComparisonMatrix
        {
            SourcingId = header.SourcingId,
            BaseCurrency = baseCurrency,
            EvaluationDate = evalDate,
            OnlyOneSupplierQuotedOverall = distinctQuotedSuppliers.Count == 1,
            Lines = lineResults
        };
    }

    private static Dictionary<long, decimal> ComputeHeaderAllocations(
        IReadOnlyList<PoSourcingQuote> quotes,
        IReadOnlyDictionary<long, PoSourcingLine> reqById)
    {
        var result = new Dictionary<long, decimal>();

        foreach (var quote in quotes)
        {
            var totalHeaderPool = Math.Max(0m, quote.HeaderFreightAmount) + Math.Max(0m, quote.HeaderOtherCharges);
            if (totalHeaderPool <= 0m ||
                string.Equals(quote.FreightAllocationMode, PoSourcingFreightAllocationModes.HeaderOnly, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var quotedLines = quote.Lines
                .Where(l => !string.Equals(l.LineStatus, PoSourcingQuoteLineStatuses.NoQuote, StringComparison.OrdinalIgnoreCase)
                            && l.QuotedPackSz > 0m
                            && l.QuotedQty > 0m
                            && reqById.ContainsKey(l.SourcingLineId))
                .OrderBy(l => l.SourcingLineId)
                .ToList();

            if (quotedLines.Count == 0)
            {
                continue;
            }

            var weights = new List<(long QuoteLineId, decimal Weight)>(quotedLines.Count);
            foreach (var l in quotedLines)
            {
                var req = reqById[l.SourcingLineId];
                var eqPurQty = req.RequestedStdQty / l.QuotedPackSz;
                if (string.Equals(quote.FreightAllocationMode, PoSourcingFreightAllocationModes.QtyProRata, StringComparison.OrdinalIgnoreCase))
                {
                    weights.Add((l.QuoteLineId, req.RequestedStdQty));
                }
                else
                {
                    var gross = eqPurQty * l.UnitPrice;
                    var disc = l.DiscPer > 0m
                        ? gross * (l.DiscPer / 100m)
                        : (l.QuotedQty > 0m ? l.DiscAmt * (eqPurQty / l.QuotedQty) : 0m);
                    var net = Math.Max(0m, gross - disc);
                    var exTax = quote.IsInclusive && l.TaxPer > 0m
                        ? net / (1m + (l.TaxPer / 100m))
                        : net;
                    weights.Add((l.QuoteLineId, exTax));
                }
            }

            var totalWeight = weights.Sum(w => w.Weight);
            decimal allocatedSoFar = 0m;
            for (var i = 0; i < weights.Count; i++)
            {
                var (quoteLineId, weight) = weights[i];
                decimal share;
                if (i == weights.Count - 1)
                {
                    share = Round2(totalHeaderPool - allocatedSoFar);
                }
                else if (totalWeight > 0m)
                {
                    share = Round2(totalHeaderPool * (weight / totalWeight));
                    allocatedSoFar += share;
                }
                else
                {
                    share = Round2(totalHeaderPool / weights.Count);
                    allocatedSoFar += share;
                }

                result[quoteLineId] = share;
            }
        }

        return result;
    }

    private static PoSourcingOfferEvaluation EvaluateSingleOffer(
        string baseCurrency,
        DateTime evalDate,
        PoSourcingLine req,
        PoSourcingSupplier? supplier,
        PoSourcingQuote quote,
        PoSourcingQuoteLine qLine,
        decimal allocatedHeaderChargesQuoteCurr)
    {
        var isQuoteActive = string.Equals(quote.Status, PoSourcingQuoteStatuses.Received, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(quote.Status, PoSourcingQuoteStatuses.Draft, StringComparison.OrdinalIgnoreCase);
        var isExpired = string.Equals(quote.Status, PoSourcingQuoteStatuses.Expired, StringComparison.OrdinalIgnoreCase)
                        || (quote.ExpiryDate.HasValue && quote.ExpiryDate.Value.Date < evalDate);
        var isNoQuote = string.Equals(qLine.LineStatus, PoSourcingQuoteLineStatuses.NoQuote, StringComparison.OrdinalIgnoreCase);

        var quoteCurrency = (quote.Currency ?? string.Empty).Trim().ToUpperInvariant();
        var reasons = new List<string>();

        if (isNoQuote)
        {
            reasons.Add("Supplier declined / no quote for this line.");
        }

        if (!isQuoteActive)
        {
            reasons.Add($"Quotation status is {quote.Status}.");
        }

        if (string.IsNullOrWhiteSpace(baseCurrency) || string.IsNullOrWhiteSpace(quoteCurrency))
        {
            reasons.Add("Currency is missing.");
        }

        decimal effectiveFxRate = 0m;
        if (!string.IsNullOrWhiteSpace(baseCurrency) && !string.IsNullOrWhiteSpace(quoteCurrency))
        {
            if (string.Equals(baseCurrency, quoteCurrency, StringComparison.OrdinalIgnoreCase))
            {
                effectiveFxRate = 1m;
            }
            else if (quote.ExchangeRateToBase.HasValue && quote.ExchangeRateToBase.Value > 0m && quote.ExchangeRateDate.HasValue)
            {
                effectiveFxRate = quote.ExchangeRateToBase.Value;
            }
            else
            {
                reasons.Add("Missing dated exchange rate for cross-currency quotation.");
            }
        }

        if (!isNoQuote)
        {
            if (qLine.QuotedPackSz <= 0m || qLine.QuotedQty <= 0m || qLine.QuotedStdQty <= 0m)
            {
                reasons.Add("Quoted quantity or UOM pack size must be positive.");
            }
            else
            {
                var expectedStdQty = Math.Round(qLine.QuotedQty * qLine.QuotedPackSz, 4, MidpointRounding.AwayFromZero);
                if (Math.Abs(expectedStdQty - qLine.QuotedStdQty) > QtyTolerance)
                {
                    reasons.Add("Quoted standard quantity does not match quoted quantity * pack size.");
                }

                if (string.Equals(qLine.QuotedUom?.Trim(), req.RequestedUom?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(qLine.QuotedPackSz - req.PackSz) > 0.000001m)
                {
                    reasons.Add("Same UOM quoted with conflicting pack size factor.");
                }
            }

            if (qLine.UnitPrice < 0m)
            {
                reasons.Add("Quoted unit price cannot be negative.");
            }

            if (string.Equals(quote.FreightAllocationMode, PoSourcingFreightAllocationModes.HeaderOnly, StringComparison.OrdinalIgnoreCase) &&
                (quote.HeaderFreightAmount > 0m || quote.HeaderOtherCharges > 0m))
            {
                reasons.Add("Header freight/charges are unallocated (HEADER_ONLY mode).");
            }
        }

        var equivalentReqPurQty = qLine.QuotedPackSz > 0m
            ? Math.Round(req.RequestedStdQty / qLine.QuotedPackSz, 4, MidpointRounding.AwayFromZero)
            : 0m;

        var hasMoqWarning = false;
        string? moqWarningMsg = null;
        if (!isNoQuote && qLine.Moq.HasValue && qLine.Moq.Value > 0m && equivalentReqPurQty < qLine.Moq.Value)
        {
            hasMoqWarning = true;
            moqWarningMsg = $"Quoted MOQ ({qLine.Moq.Value:N4} {qLine.QuotedUom}) exceeds requested quantity ({equivalentReqPurQty:N4} {qLine.QuotedUom}).";
        }

        var isComparable = !isNoQuote && reasons.Count == 0;
        decimal goodsValueBase = 0m;
        decimal normalizedBaseUnitCost = 0m;
        decimal comparableLandedTotalBase = 0m;
        decimal taxInclusivePayableBase = 0m;

        if (isComparable && effectiveFxRate > 0m && req.RequestedStdQty > 0m)
        {
            var rawGrossQuote = equivalentReqPurQty * qLine.UnitPrice;
            var discQuote = qLine.DiscPer > 0m
                ? rawGrossQuote * (qLine.DiscPer / 100m)
                : (qLine.QuotedQty > 0m ? qLine.DiscAmt * (equivalentReqPurQty / qLine.QuotedQty) : 0m);
            var netQuote = Math.Max(0m, rawGrossQuote - discQuote);

            decimal goodsExTaxQuote;
            decimal recoverableTaxQuote;
            if (quote.IsInclusive && qLine.TaxPer > 0m)
            {
                goodsExTaxQuote = netQuote / (1m + (qLine.TaxPer / 100m));
                recoverableTaxQuote = netQuote - goodsExTaxQuote;
            }
            else
            {
                goodsExTaxQuote = netQuote;
                recoverableTaxQuote = goodsExTaxQuote * (qLine.TaxPer / 100m);
            }

            var nonRecoverableTaxQuote = goodsExTaxQuote * (qLine.NonRecoverableTaxPer / 100m);
            var lineChargesQuote = Math.Max(0m, qLine.LineCharges);

            goodsValueBase = Round2(goodsExTaxQuote * effectiveFxRate);
            comparableLandedTotalBase = Round2(
                (goodsExTaxQuote + nonRecoverableTaxQuote + lineChargesQuote + allocatedHeaderChargesQuoteCurr) * effectiveFxRate);
            taxInclusivePayableBase = Round2(
                (goodsExTaxQuote + recoverableTaxQuote + nonRecoverableTaxQuote + lineChargesQuote + allocatedHeaderChargesQuoteCurr) * effectiveFxRate);
            normalizedBaseUnitCost = Math.Round(
                comparableLandedTotalBase / req.RequestedStdQty,
                6,
                MidpointRounding.AwayFromZero);
        }

        return new PoSourcingOfferEvaluation
        {
            QuoteId = quote.QuoteId,
            QuoteLineId = qLine.QuoteLineId,
            SourcingLineId = req.SourcingLineId,
            SourcingSupplierId = quote.SourcingSupplierId,
            SuppCode = supplier?.SuppCode,
            SupplierName = supplier?.SupplierName ?? string.Empty,
            SupplierQuoteRef = quote.SupplierQuoteRef,
            QuoteRevNo = quote.QuoteRevNo,
            LineStatus = qLine.LineStatus,
            IsExpired = isExpired,
            IsQuoteActive = isQuoteActive,
            IsComparable = isComparable,
            NonComparableReason = reasons.Count > 0 ? string.Join(" ", reasons) : null,
            HasMoqWarning = hasMoqWarning,
            MoqWarningMessage = moqWarningMsg,
            IsZeroPriceOffer = !isNoQuote && qLine.UnitPrice == 0m,
            Currency = quoteCurrency,
            ExchangeRateToBase = effectiveFxRate > 0m ? effectiveFxRate : quote.ExchangeRateToBase,
            ExchangeRateDate = quote.ExchangeRateDate,
            IsInclusive = quote.IsInclusive,
            QuotedUom = qLine.QuotedUom,
            QuotedPackSz = qLine.QuotedPackSz,
            QuotedQty = qLine.QuotedQty,
            QuotedStdQty = qLine.QuotedStdQty,
            EquivalentRequestedPurQty = equivalentReqPurQty,
            UnitPrice = qLine.UnitPrice,
            DiscPer = qLine.DiscPer,
            DiscAmt = qLine.DiscAmt,
            TaxGroupCode = qLine.TaxGroupCode,
            TaxPer = qLine.TaxPer,
            NonRecoverableTaxPer = qLine.NonRecoverableTaxPer,
            LineCharges = qLine.LineCharges,
            AllocatedHeaderChargesQuoteCurr = allocatedHeaderChargesQuoteCurr,
            GoodsValueBase = goodsValueBase,
            NormalizedBaseUnitCost = normalizedBaseUnitCost,
            NormalizedComparableLandedTotal = comparableLandedTotalBase,
            NormalizedTaxInclusivePayableTotal = taxInclusivePayableBase,
            LeadTimeDays = qLine.LeadTimeDays,
            EstimatedDeliveryDate = qLine.EstimatedDeliveryDate,
            Moq = qLine.Moq
        };
    }

    private static decimal Round2(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
