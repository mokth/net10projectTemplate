using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Purchase;

public sealed partial class PoSourcingService
{
    public async Task<PoSourcingOperationResult> SaveQuoteAsync(
        PoSourcingQuoteSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return PoSourcingOperationResult.FailValidation("Quote request is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return PoSourcingOperationResult.Fail(context.Error);
        }

        if (!await _featureGuard.IsEnabledAsync(cancellationToken))
        {
            return FeatureDisabledResult();
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken) && !await CanAsync(PermissionCodes.Add, cancellationToken))
        {
            return PoSourcingOperationResult.Fail("Not authorized.", PoSourcingErrorKind.Authorization);
        }

        var fieldErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var quoteNo = (request.SupplierQuoteNo ?? string.Empty).Trim();
        if (quoteNo.Length == 0)
        {
            fieldErrors["SupplierQuoteNo"] = "Supplier Quotation No. is required.";
        }

        var currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();
        if (currency.Length == 0)
        {
            fieldErrors["Currency"] = "Quote currency is required.";
        }

        if (request.FreightAmount < 0m)
        {
            fieldErrors["FreightAmount"] = "Freight amount cannot be negative.";
        }

        if (request.OtherChargesAmount < 0m)
        {
            fieldErrors["OtherChargesAmount"] = "Other charges amount cannot be negative.";
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            fieldErrors["Lines"] = "At least one quotation line is required.";
        }

        if (fieldErrors.Count > 0)
        {
            return PoSourcingOperationResult.FailValidation("Validation failed.", fieldErrors);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var company = context.CompanyCode!;
            var branch = context.BranchCode!;
            var userId = Truncate(context.UserId!, 20);
            var nowUtc = DateTime.UtcNow;

            if (!await _featureGuard.IsEnabledAuthoritativeAsync(db, company, lockRowForUpdate: true, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return FeatureDisabledResult();
            }

            var header = await LockSourcingHeaderAsync(db, company, branch, request.SourcingId, cancellationToken);
            if (header is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSourcingOperationResult.Fail("Sourcing document was not found.", PoSourcingErrorKind.NotFound);
            }

            if (header.Status != PoSourcingStatuses.Draft
                && header.Status != PoSourcingStatuses.CollectingQuotes
                && header.Status != PoSourcingStatuses.ReadyToCompare)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSourcingOperationResult.Fail(
                    $"Quotations cannot be recorded or revised while Sourcing status is '{header.Status}'.");
            }

            var supplier = await db.PoSourcingSuppliers.FirstOrDefaultAsync(
                s => s.CompanyCode == company
                    && s.BranchCode == branch
                    && s.SourcingId == header.SourcingId
                    && s.SourcingSupplierId == request.SourcingSupplierId,
                cancellationToken);
            if (supplier is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSourcingOperationResult.Fail("Selected RFQ supplier was not found on this Sourcing document.");
            }

            var isSameBaseCurrency = string.Equals(currency, header.BaseCurrency, StringComparison.OrdinalIgnoreCase);
            var exchangeRate = isSameBaseCurrency ? 1m : request.ExchangeRate;
            if (!isSameBaseCurrency && exchangeRate <= 0m)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSourcingOperationResult.FailValidation(
                    $"A positive exchange rate is required when quoting in {currency} (base currency is {header.BaseCurrency}).",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ExchangeRate"] = "Exchange rate must be greater than zero for foreign currency quotes."
                    });
            }

            var sourcingLines = await db.PoSourcingLines
                .Where(l => l.CompanyCode == company && l.BranchCode == branch && l.SourcingId == header.SourcingId)
                .OrderBy(l => l.LineNo)
                .ToListAsync(cancellationToken);
            var sourcingLineById = sourcingLines.ToDictionary(l => l.SourcingLineId);

            var taxGroups = await db.SaTaxGroups.AsNoTracking()
                .Where(x => x.CompanyCode == company)
                .ToDictionaryAsync(x => x.TaxGrCode, x => x.Percentage, StringComparer.OrdinalIgnoreCase, cancellationToken);

            var allocMode = (request.FreightAllocationMode ?? PoSourcingFreightAllocationModes.ValueProRata).Trim().ToUpperInvariant();
            if (allocMode != PoSourcingFreightAllocationModes.ValueProRata
                && allocMode != PoSourcingFreightAllocationModes.QtyProRata
                && allocMode != PoSourcingFreightAllocationModes.HeaderOnly)
            {
                allocMode = PoSourcingFreightAllocationModes.ValueProRata;
            }

            PoSourcingQuote quote;
            bool isRevision = false;
            if (request.QuoteId is null || request.QuoteId <= 0)
            {
                var existingLatest = await db.PoSourcingQuotes
                    .Where(q => q.CompanyCode == company
                        && q.BranchCode == branch
                        && q.SourcingId == header.SourcingId
                        && q.SourcingSupplierId == supplier.SourcingSupplierId
                        && q.Status == PoSourcingQuoteStatuses.Received)
                    .OrderByDescending(q => q.QuoteRevNo)
                    .FirstOrDefaultAsync(cancellationToken);

                short nextRev = 1;
                if (existingLatest is not null)
                {
                    existingLatest.Status = PoSourcingQuoteStatuses.Superseded;
                    TouchRowVersion(db, existingLatest);
                    nextRev = (short)(existingLatest.QuoteRevNo + 1);
                    isRevision = true;
                }

                quote = new PoSourcingQuote
                {
                    CompanyCode = company,
                    BranchCode = branch,
                    SourcingId = header.SourcingId,
                    SourcingSupplierId = supplier.SourcingSupplierId,
                    SupplierQuoteRef = Truncate(quoteNo, 60),
                    QuoteRevNo = nextRev,
                    QuoteDate = request.QuoteDate == default ? _dates.Today.Date : request.QuoteDate.Date,
                    ExpiryDate = request.ValidUntilDate?.Date,
                    Status = PoSourcingQuoteStatuses.Received,
                    Currency = Truncate(currency, 20),
                    ExchangeRateToBase = exchangeRate,
                    ExchangeRateDate = _dates.Today.Date,
                    PayTermCode = TruncateOptional(request.PaymentTerm, 20),
                    BuyingTermCode = TruncateOptional(request.BuyingTerm, 20),
                    IsInclusive = request.IsInclusive,
                    HeaderFreightAmount = request.FreightAmount,
                    HeaderOtherCharges = request.OtherChargesAmount,
                    FreightAllocationMode = allocMode,
                    BuyerNotes = TruncateOptional(request.Remarks, 500),
                    CreatedBy = userId,
                    CreatedDate = nowUtc
                };
                TouchRowVersion(db, quote);
                db.PoSourcingQuotes.Add(quote);
                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                var existingQuote = await db.PoSourcingQuotes
                    .Include(q => q.Lines)
                    .FirstOrDefaultAsync(q => q.CompanyCode == company
                        && q.BranchCode == branch
                        && q.SourcingId == header.SourcingId
                        && q.QuoteId == request.QuoteId.Value, cancellationToken);
                if (existingQuote is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoSourcingOperationResult.Fail("Supplier quotation was not found.", PoSourcingErrorKind.NotFound);
                }

                if (request.RowVersion is not null && request.RowVersion.Length > 0 && !RowVersionsEqual(existingQuote.RowVersion, request.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoSourcingOperationResult.Fail(
                        "Supplier quotation was modified by another user. Reload before saving.",
                        PoSourcingErrorKind.Concurrency);
                }

                if (request.CreateNewRevision)
                {
                    isRevision = true;
                    existingQuote.Status = PoSourcingQuoteStatuses.Superseded;
                    TouchRowVersion(db, existingQuote);

                    quote = new PoSourcingQuote
                    {
                        CompanyCode = company,
                        BranchCode = branch,
                        SourcingId = header.SourcingId,
                        SourcingSupplierId = supplier.SourcingSupplierId,
                        SupplierQuoteRef = Truncate(quoteNo, 60),
                        QuoteRevNo = (short)(existingQuote.QuoteRevNo + 1),
                        QuoteDate = request.QuoteDate == default ? _dates.Today.Date : request.QuoteDate.Date,
                        ExpiryDate = request.ValidUntilDate?.Date,
                        Status = PoSourcingQuoteStatuses.Received,
                        Currency = Truncate(currency, 20),
                        ExchangeRateToBase = exchangeRate,
                        ExchangeRateDate = _dates.Today.Date,
                        PayTermCode = TruncateOptional(request.PaymentTerm, 20),
                        BuyingTermCode = TruncateOptional(request.BuyingTerm, 20),
                        IsInclusive = request.IsInclusive,
                        HeaderFreightAmount = request.FreightAmount,
                        HeaderOtherCharges = request.OtherChargesAmount,
                        FreightAllocationMode = allocMode,
                        BuyerNotes = TruncateOptional(request.Remarks, 500),
                        CreatedBy = userId,
                        CreatedDate = nowUtc
                    };
                    TouchRowVersion(db, quote);
                    db.PoSourcingQuotes.Add(quote);
                    await db.SaveChangesAsync(cancellationToken);
                }
                else
                {
                    quote = existingQuote;
                    quote.SupplierQuoteRef = Truncate(quoteNo, 60);
                    quote.QuoteDate = request.QuoteDate == default ? quote.QuoteDate : request.QuoteDate.Date;
                    quote.ExpiryDate = request.ValidUntilDate?.Date;
                    quote.Status = PoSourcingQuoteStatuses.Received;
                    quote.Currency = Truncate(currency, 20);
                    quote.ExchangeRateToBase = exchangeRate;
                    quote.PayTermCode = TruncateOptional(request.PaymentTerm, 20);
                    quote.BuyingTermCode = TruncateOptional(request.BuyingTerm, 20);
                    quote.IsInclusive = request.IsInclusive;
                    quote.HeaderFreightAmount = request.FreightAmount;
                    quote.HeaderOtherCharges = request.OtherChargesAmount;
                    quote.FreightAllocationMode = allocMode;
                    quote.BuyerNotes = TruncateOptional(request.Remarks, 500);
                    quote.ModifiedBy = userId;
                    quote.ModifiedDate = nowUtc;
                    TouchRowVersion(db, quote);

                    db.PoSourcingQuoteLines.RemoveRange(quote.Lines);
                    quote.Lines.Clear();
                }
            }

            foreach (var srcLine in request.Lines!)
            {
                if (!sourcingLineById.TryGetValue(srcLine.SourcingLineId, out var rfqLine))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoSourcingOperationResult.Fail($"Quote line references unknown RFQ line ID {srcLine.SourcingLineId}.");
                }

                var lineStatus = (srcLine.LineStatus ?? PoSourcingQuoteLineStatuses.Quoted).Trim().ToUpperInvariant();
                if (lineStatus != PoSourcingQuoteLineStatuses.Quoted && lineStatus != PoSourcingQuoteLineStatuses.NoQuote)
                {
                    lineStatus = PoSourcingQuoteLineStatuses.Quoted;
                }

                var quoteUom = TruncateOptional(srcLine.QuoteUom, 10) ?? rfqLine.RequestedUom;
                var packSize = srcLine.PackSize > 0m ? srcLine.PackSize : rfqLine.PackSz;
                var offeredQty = lineStatus == PoSourcingQuoteLineStatuses.NoQuote
                    ? 0m
                    : (srcLine.OfferedQty > 0m ? srcLine.OfferedQty : rfqLine.RequestedQty);
                var unitPrice = lineStatus == PoSourcingQuoteLineStatuses.NoQuote
                    ? 0m
                    : Math.Max(0m, srcLine.QuotedUnitPrice ?? 0m);

                var taxGroup = TruncateOptional(srcLine.TaxGroup, 20) ?? TruncateOptional(request.TaxGroup, 20);
                decimal taxPercent = srcLine.TaxPercent;
                if (!string.IsNullOrWhiteSpace(taxGroup) && taxGroups.TryGetValue(taxGroup, out var masterTaxPct))
                {
                    taxPercent = masterTaxPct;
                }

                var qLine = new PoSourcingQuoteLine
                {
                    CompanyCode = company,
                    BranchCode = branch,
                    SourcingId = header.SourcingId,
                    QuoteId = quote.QuoteId,
                    SourcingLineId = rfqLine.SourcingLineId,
                    LineStatus = lineStatus,
                    VendorPartNo = TruncateOptional(srcLine.VendorPartNo, 50),
                    OfferedSpec = TruncateOptional(srcLine.AlternateSpec ?? srcLine.QuotedBrand, 500),
                    QuotedQty = offeredQty,
                    QuotedUom = quoteUom,
                    QuotedPackSz = packSize,
                    QuotedStdQty = Math.Round(offeredQty * packSize, 4, MidpointRounding.AwayFromZero),
                    UnitPrice = unitPrice,
                    DiscPer = string.Equals(srcLine.DiscountType1, "%", StringComparison.OrdinalIgnoreCase) ? srcLine.ItemDiscount1 : 0m,
                    DiscAmt = !string.Equals(srcLine.DiscountType1, "%", StringComparison.OrdinalIgnoreCase) ? srcLine.ItemDiscount1 : 0m,
                    TaxGroupCode = taxGroup,
                    TaxPer = taxPercent,
                    NonRecoverableTaxPer = Math.Max(0m, srcLine.NonRecoverableTaxPercent),
                    LineCharges = 0m,
                    Moq = srcLine.Moq,
                    LeadTimeDays = srcLine.LeadTimeDays ?? request.LeadTimeDays,
                    EstimatedDeliveryDate = srcLine.PromisedEtaDate?.Date ?? request.PromisedEtaDate?.Date,
                    ExceptionsNote = TruncateOptional(srcLine.Remarks, 250)
                };

                db.PoSourcingQuoteLines.Add(qLine);
                quote.Lines.Add(qLine);
            }

            await db.SaveChangesAsync(cancellationToken);

            // Recompute comparison metrics so stored quote line normalized costs stay synchronized.
            await RefreshComparisonSnapshotsAsync(db, header, sourcingLines, cancellationToken);

            supplier.ResponseStatus = PoSourcingSupplierResponseStatuses.Quoted;
            var priorStatus = header.Status;
            if (header.Status == PoSourcingStatuses.Draft || header.Status == PoSourcingStatuses.CollectingQuotes)
            {
                header.Status = PoSourcingStatuses.ReadyToCompare;
            }
            header.ModifiedBy = userId;
            header.ModifiedDate = nowUtc;
            TouchRowVersion(db, header);

            db.PoSourcingEvents.Add(new PoSourcingEvent
            {
                CompanyCode = company,
                BranchCode = branch,
                SourcingId = header.SourcingId,
                QuoteId = quote.QuoteId,
                EventType = isRevision ? PoSourcingEventTypes.QuoteRevised : PoSourcingEventTypes.QuoteRecorded,
                PriorStatus = priorStatus,
                NewStatus = header.Status,
                Reason = $"{(isRevision ? "Revised" : "Recorded")} quotation {quote.SupplierQuoteRef} (Rev {quote.QuoteRevNo}) for supplier {supplier.SupplierName}.",
                ActorUserId = userId,
                OccurredAtUtc = nowUtc
            });

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return await LoadDocumentDtoAsync(db, company, branch, header.SourcingId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveQuoteAsync failed.");
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Unable to save supplier quotation.", PoSourcingErrorKind.Unexpected);
        }
    }

    public async Task<PoSourcingOperationResult> WithdrawQuoteAsync(
        long sourcingId,
        long quoteId,
        byte[] rowVersion,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return PoSourcingOperationResult.Fail(context.Error);
        }

        if (!await _featureGuard.IsEnabledAsync(cancellationToken))
        {
            return FeatureDisabledResult();
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return PoSourcingOperationResult.Fail("Not authorized.", PoSourcingErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var company = context.CompanyCode!;
        var branch = context.BranchCode!;
        var userId = Truncate(context.UserId!, 20);
        var nowUtc = DateTime.UtcNow;

        if (!await _featureGuard.IsEnabledAuthoritativeAsync(db, company, lockRowForUpdate: true, cancellationToken))
        {
            await tx.RollbackAsync(cancellationToken);
            return FeatureDisabledResult();
        }

        var header = await LockSourcingHeaderAsync(db, company, branch, sourcingId, cancellationToken);
        if (header is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Sourcing document was not found.", PoSourcingErrorKind.NotFound);
        }

        var quote = await db.PoSourcingQuotes
            .FirstOrDefaultAsync(q => q.CompanyCode == company && q.BranchCode == branch && q.SourcingId == sourcingId && q.QuoteId == quoteId, cancellationToken);
        if (quote is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Supplier quotation was not found.", PoSourcingErrorKind.NotFound);
        }

        if (rowVersion is not null && rowVersion.Length > 0 && !RowVersionsEqual(quote.RowVersion, rowVersion))
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Supplier quotation was modified by another user.", PoSourcingErrorKind.Concurrency);
        }

        var usedInAward = await db.PoSourcingAwardLines
            .AnyAsync(al => al.CompanyCode == company
                && al.BranchCode == branch
                && al.SourcingId == sourcingId
                && al.QuoteId == quoteId
                && al.Award != null
                && al.Award.Status == PoSourcingAwardStatuses.Approved, cancellationToken);
        if (usedInAward)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Cannot withdraw a quotation that is part of an approved award.");
        }

        quote.Status = PoSourcingQuoteStatuses.Withdrawn;
        quote.ModifiedBy = userId;
        quote.ModifiedDate = nowUtc;
        TouchRowVersion(db, quote);

        db.PoSourcingEvents.Add(new PoSourcingEvent
        {
            CompanyCode = company,
            BranchCode = branch,
            SourcingId = sourcingId,
            QuoteId = quoteId,
            EventType = PoSourcingEventTypes.QuoteWithdrawn,
            Reason = $"Withdrew quotation {quote.SupplierQuoteRef} (Rev {quote.QuoteRevNo}). {reason}".Trim(),
            ActorUserId = userId,
            OccurredAtUtc = nowUtc
        });

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return await LoadDocumentDtoAsync(db, company, branch, sourcingId, cancellationToken);
    }

    public async Task<PoSourcingOperationResult> GetComparisonAsync(
        long sourcingId,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return PoSourcingOperationResult.Fail(context.Error);
        }

        if (!await _featureGuard.IsEnabledAsync(cancellationToken))
        {
            return FeatureDisabledResult();
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoSourcingOperationResult.Fail("Not authorized.", PoSourcingErrorKind.Authorization);
        }

        var canViewCost = await CanAsync(PermissionCodes.ViewCost, cancellationToken);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = context.CompanyCode!;
        var branch = context.BranchCode!;

        var header = await db.PoSourcingHdrs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.SourcingId == sourcingId, cancellationToken);
        if (header is null)
        {
            return PoSourcingOperationResult.Fail("Sourcing document was not found.", PoSourcingErrorKind.NotFound);
        }

        var lines = await db.PoSourcingLines.AsNoTracking()
            .Where(l => l.CompanyCode == company && l.BranchCode == branch && l.SourcingId == sourcingId)
            .OrderBy(l => l.LineNo)
            .ToListAsync(cancellationToken);

        var suppliers = await db.PoSourcingSuppliers.AsNoTracking()
            .Where(s => s.CompanyCode == company && s.BranchCode == branch && s.SourcingId == sourcingId)
            .ToListAsync(cancellationToken);

        var quotes = await db.PoSourcingQuotes.AsNoTracking()
            .Include(q => q.Lines)
            .Where(q => q.CompanyCode == company && q.BranchCode == branch && q.SourcingId == sourcingId)
            .ToListAsync(cancellationToken);

        var matrix = PoSourcingComparisonCalculator.Evaluate(header, lines, suppliers, quotes, _dates.Today.Date);

        var historicalPrices = new Dictionary<long, PoSourcingHistoricalPriceRefDto>();
        if (canViewCost)
        {
            var itemCodes = lines
                .Where(l => !string.IsNullOrWhiteSpace(l.ICode))
                .Select(l => l.ICode!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (itemCodes.Count > 0)
            {
                var recentPoLines = await db.PoOrderDetails.AsNoTracking()
                    .Include(d => d.Order)
                    .Where(d => d.CompanyCode == company
                        && d.BranchCode == branch
                        && d.ICode != null
                        && itemCodes.Contains(d.ICode)
                        && d.Order != null
                        && d.Order.Status != PoOrderStatuses.Cancelled
                        && d.PoUnitPrice > 0m)
                    .OrderByDescending(d => d.Order!.PoDate)
                    .ThenByDescending(d => d.PoNo)
                    .Take(100)
                    .Select(d => new
                    {
                        d.ICode,
                        d.PoNo,
                        d.Order!.PoDate,
                        d.Order.VendCode,
                        d.CurCode,
                        d.PoUnitPrice
                    })
                    .ToListAsync(cancellationToken);

                foreach (var rfqLine in lines.Where(l => !string.IsNullOrWhiteSpace(l.ICode)))
                {
                    var match = recentPoLines.FirstOrDefault(p => string.Equals(p.ICode, rfqLine.ICode, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        historicalPrices[rfqLine.SourcingLineId] = new PoSourcingHistoricalPriceRefDto
                        {
                            SourcingLineId = rfqLine.SourcingLineId,
                            LastDocNo = match.PoNo,
                            LastDocDate = match.PoDate,
                            LastSupplierCode = match.VendCode,
                            LastCurrency = match.CurCode,
                            LastUnitPrice = match.PoUnitPrice
                        };
                    }
                }
            }
        }
        else
        {
            matrix = RedactComparisonMatrix(matrix);
        }

        return PoSourcingOperationResult.OkComparison(new PoSourcingComparisonDto
        {
            SourcingId = sourcingId,
            BaseCurrency = header.BaseCurrency,
            PricingRedacted = !canViewCost,
            Matrix = matrix,
            HistoricalPricesByLine = historicalPrices
        });
    }

    public async Task<PoSourcingOperationResult> SaveAwardAsync(
        PoSourcingAwardRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return PoSourcingOperationResult.FailValidation("Award request is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return PoSourcingOperationResult.Fail(context.Error);
        }

        if (!await _featureGuard.IsEnabledAsync(cancellationToken))
        {
            return FeatureDisabledResult();
        }

        var requiredPerm = request.ApproveImmediately ? PermissionCodes.Approve : PermissionCodes.Edit;
        if (!await CanAsync(requiredPerm, cancellationToken))
        {
            return PoSourcingOperationResult.Fail("Not authorized.", PoSourcingErrorKind.Authorization);
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return PoSourcingOperationResult.FailValidation("At least one award line selection is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var company = context.CompanyCode!;
        var branch = context.BranchCode!;
        var userId = Truncate(context.UserId!, 20);
        var nowUtc = DateTime.UtcNow;

        if (!await _featureGuard.IsEnabledAuthoritativeAsync(db, company, lockRowForUpdate: true, cancellationToken))
        {
            await tx.RollbackAsync(cancellationToken);
            return FeatureDisabledResult();
        }

        var header = await LockSourcingHeaderAsync(db, company, branch, request.SourcingId, cancellationToken);
        if (header is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Sourcing document was not found.", PoSourcingErrorKind.NotFound);
        }

        if (request.SourcingRowVersion is not null
            && request.SourcingRowVersion.Length > 0
            && !RowVersionsEqual(header.RowVersion, request.SourcingRowVersion))
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail(
                "This Sourcing document was changed by another user. Reload before awarding.",
                PoSourcingErrorKind.Concurrency);
        }

        if (header.Status == PoSourcingStatuses.Cancelled || header.Status == PoSourcingStatuses.Converted)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail($"Cannot award a Sourcing document in status '{header.Status}'.");
        }

        var rfqLines = await db.PoSourcingLines
            .Where(l => l.CompanyCode == company && l.BranchCode == branch && l.SourcingId == header.SourcingId)
            .OrderBy(l => l.LineNo)
            .ToListAsync(cancellationToken);
        var rfqLineById = rfqLines.ToDictionary(l => l.SourcingLineId);

        var suppliers = await db.PoSourcingSuppliers
            .Where(s => s.CompanyCode == company && s.BranchCode == branch && s.SourcingId == header.SourcingId)
            .ToListAsync(cancellationToken);
        var supplierById = suppliers.ToDictionary(s => s.SourcingSupplierId);

        var quotes = await db.PoSourcingQuotes
            .Include(q => q.Lines)
            .Where(q => q.CompanyCode == company && q.BranchCode == branch && q.SourcingId == header.SourcingId)
            .ToListAsync(cancellationToken);
        var quoteById = quotes.ToDictionary(q => q.QuoteId);
        var quoteLineById = quotes.SelectMany(q => q.Lines).ToDictionary(ql => ql.QuoteLineId);

        var comparison = PoSourcingComparisonCalculator.Evaluate(header, rfqLines, suppliers, quotes, _dates.Today.Date);
        var cellByQuoteLineId = comparison.Lines
            .SelectMany(l => l.Offers)
            .ToDictionary(c => c.QuoteLineId);
        var comparableCountByLineId = comparison.Lines
            .ToDictionary(l => l.SourcingLineId, l => l.ComparableOfferCount);

        var fieldErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var requestedStdByRfqLine = new Dictionary<long, decimal>();

        for (var i = 0; i < request.Lines.Count; i++)
        {
            var input = request.Lines[i];
            var prefix = $"Lines[{i}]";

            if (input.AwardedPurchaseQty <= 0m)
            {
                fieldErrors[$"{prefix}.AwardedPurchaseQty"] = "Awarded quantity must be greater than zero.";
                continue;
            }

            if (!rfqLineById.TryGetValue(input.SourcingLineId, out var rfqLine))
            {
                fieldErrors[$"{prefix}.SourcingLineId"] = "RFQ line was not found.";
                continue;
            }

            if (!quoteLineById.TryGetValue(input.QuoteLineId, out var qLine) || qLine.SourcingLineId != rfqLine.SourcingLineId)
            {
                fieldErrors[$"{prefix}.QuoteLineId"] = "Selected supplier quote line does not match the RFQ line.";
                continue;
            }

            var quote = quoteById[qLine.QuoteId];
            if (quote.Status != PoSourcingQuoteStatuses.Received)
            {
                fieldErrors[$"{prefix}.QuoteLineId"] = $"Supplier quotation {quote.SupplierQuoteRef} is not active.";
                continue;
            }

            if (string.Equals(qLine.LineStatus, PoSourcingQuoteLineStatuses.NoQuote, StringComparison.OrdinalIgnoreCase))
            {
                fieldErrors[$"{prefix}.QuoteLineId"] = "Cannot award a NO_QUOTE supplier line.";
                continue;
            }

            if (!cellByQuoteLineId.TryGetValue(qLine.QuoteLineId, out var cell) || !cell.IsComparable)
            {
                fieldErrors[$"{prefix}.QuoteLineId"] = cell?.NonComparableReason ?? "Selected quote line is not comparable.";
                continue;
            }

            if (cell.IsExpired && string.IsNullOrWhiteSpace(input.ExceptionReason))
            {
                fieldErrors[$"{prefix}.ExceptionReason"] =
                    $"Quotation {quote.SupplierQuoteRef} expired on {quote.ExpiryDate:yyyy-MM-dd}. An exception reason is required to award an expired quote.";
            }

            if (cell.HasMoqWarning && string.IsNullOrWhiteSpace(input.ExceptionReason))
            {
                fieldErrors[$"{prefix}.ExceptionReason"] =
                    $"Offered/awarded quantity is below supplier MOQ ({qLine.Moq}). An exception reason is required.";
            }

            comparableCountByLineId.TryGetValue(rfqLine.SourcingLineId, out var comparableCount);
            if (comparableCount >= 2 && !cell.IsLowestComparableOffer)
            {
                if (string.IsNullOrWhiteSpace(input.SelectionReasonCode) && string.IsNullOrWhiteSpace(input.SelectionRemarks))
                {
                    fieldErrors[$"{prefix}.SelectionReasonCode"] =
                        "A selection reason is required when awarding a supplier that is not the lowest comparable landed cost.";
                }
            }

            var packSz = qLine.QuotedPackSz > 0m ? qLine.QuotedPackSz : rfqLine.PackSz;
            var awardStdQty = Math.Round(input.AwardedPurchaseQty * packSz, 4, MidpointRounding.AwayFromZero);
            requestedStdByRfqLine.TryGetValue(rfqLine.SourcingLineId, out var runningStdQty);
            requestedStdByRfqLine[rfqLine.SourcingLineId] = runningStdQty + awardStdQty;
        }

        foreach (var (sourcingLineId, totalAwardStdQty) in requestedStdByRfqLine)
        {
            var rfqLine = rfqLineById[sourcingLineId];
            if (totalAwardStdQty > rfqLine.RequestedStdQty + 0.0005m)
            {
                fieldErrors[$"Line_{rfqLine.LineNo}"] =
                    $"Line {rfqLine.LineNo}: total awarded standard quantity ({totalAwardStdQty}) cannot exceed requested standard quantity ({rfqLine.RequestedStdQty}).";
            }
        }

        if (fieldErrors.Count > 0)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.FailValidation(
                ValidationMessageFormat.JoinMessages(fieldErrors),
                fieldErrors);
        }

        var existingActiveConversions = await db.PoSourcingConversions
            .AnyAsync(c => c.CompanyCode == company && c.BranchCode == branch && c.SourcingId == header.SourcingId && c.Status == PoSourcingConversionStatuses.Active, cancellationToken);
        if (existingActiveConversions)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Cannot replace an award that already has active converted PR/PO documents.");
        }

        var existingAwards = await db.PoSourcingAwards
            .Where(a => a.CompanyCode == company && a.BranchCode == branch && a.SourcingId == header.SourcingId)
            .ToListAsync(cancellationToken);

        foreach (var prev in existingAwards.Where(a => a.Status != PoSourcingAwardStatuses.Superseded && a.Status != PoSourcingAwardStatuses.Rejected))
        {
            prev.Status = PoSourcingAwardStatuses.Superseded;
            TouchRowVersion(db, prev);
        }

        var nextAwardVersion = existingAwards.Count == 0 ? 1 : existingAwards.Max(a => a.AwardVersion) + 1;
        var awardStatus = request.ApproveImmediately ? PoSourcingAwardStatuses.Approved : PoSourcingAwardStatuses.Proposed;

        var award = new PoSourcingAward
        {
            CompanyCode = company,
            BranchCode = branch,
            SourcingId = header.SourcingId,
            AwardVersion = nextAwardVersion,
            Status = awardStatus,
            OverallReason = TruncateOptional(request.SummaryRemarks, 500),
            ProposedBy = userId,
            ProposedAtUtc = nowUtc,
            DecidedBy = request.ApproveImmediately ? userId : null,
            DecidedAtUtc = request.ApproveImmediately ? nowUtc : null,
            DecisionReason = TruncateOptional(request.SummaryRemarks, 500)
        };
        TouchRowVersion(db, award);
        db.PoSourcingAwards.Add(award);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var input in request.Lines)
        {
            var rfqLine = rfqLineById[input.SourcingLineId];
            var qLine = quoteLineById[input.QuoteLineId];
            var quote = quoteById[qLine.QuoteId];
            supplierById.TryGetValue(quote.SourcingSupplierId, out var sup);
            var cell = cellByQuoteLineId[qLine.QuoteLineId];

            var packSz = qLine.QuotedPackSz > 0m ? qLine.QuotedPackSz : rfqLine.PackSz;
            var awardPurQty = Math.Round(input.AwardedPurchaseQty, 4, MidpointRounding.AwayFromZero);
            var awardStdQty = Math.Round(awardPurQty * packSz, 4, MidpointRounding.AwayFromZero);

            var reasonCombined = string.Join(
                " | ",
                new[] { input.SelectionReasonCode, input.SelectionRemarks, input.ExceptionReason }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

            var awardLine = new PoSourcingAwardLine
            {
                CompanyCode = company,
                BranchCode = branch,
                SourcingId = header.SourcingId,
                AwardId = award.AwardId,
                SourcingLineId = rfqLine.SourcingLineId,
                QuoteId = quote.QuoteId,
                QuoteLineId = qLine.QuoteLineId,
                SourcingSupplierId = quote.SourcingSupplierId,
                SuppCodeSnapshot = sup?.SuppCode,
                SupplierNameSnapshot = sup?.SupplierName ?? cell.SupplierName,
                ICodeSnapshot = rfqLine.ICode,
                OneTimeItemYnSnapshot = rfqLine.OneTimeItemYn,
                CurrencySnapshot = quote.Currency,
                ExchangeRateSnapshot = quote.ExchangeRateToBase ?? 1m,
                IsInclusiveSnapshot = quote.IsInclusive,
                AwardedUom = qLine.QuotedUom,
                StdUomSnapshot = rfqLine.StdUom,
                PackSzSnapshot = packSz,
                AwardedQty = awardPurQty,
                AwardedStdQty = awardStdQty,
                AwardedUnitPrice = qLine.UnitPrice,
                DiscPerSnapshot = qLine.DiscPer,
                TaxGroupSnapshot = qLine.TaxGroupCode,
                TaxPerSnapshot = qLine.TaxPer,
                ComparableBaseUnitCostSnapshot = cell.NormalizedBaseUnitCost,
                ComparableTotalSnapshot = cell.NormalizedComparableLandedTotal,
                IsLowestComparableOffer = cell.IsLowestComparableOffer,
                SelectionReason = TruncateOptional(reasonCombined, 500)
            };

            db.PoSourcingAwardLines.Add(awardLine);
            award.Lines.Add(awardLine);
        }

        var priorStatus = header.Status;
        header.Status = request.ApproveImmediately ? PoSourcingStatuses.Awarded : PoSourcingStatuses.AwaitingAward;
        header.ModifiedBy = userId;
        header.ModifiedDate = nowUtc;
        TouchRowVersion(db, header);

        db.PoSourcingEvents.Add(new PoSourcingEvent
        {
            CompanyCode = company,
            BranchCode = branch,
            SourcingId = header.SourcingId,
            AwardId = award.AwardId,
            EventType = request.ApproveImmediately ? PoSourcingEventTypes.AwardApproved : PoSourcingEventTypes.AwardProposed,
            PriorStatus = priorStatus,
            NewStatus = header.Status,
            Reason = $"{(request.ApproveImmediately ? "Approved" : "Proposed")} Award v{award.AwardVersion} across {request.Lines.Count} line selection(s).",
            ActorUserId = userId,
            OccurredAtUtc = nowUtc
        });

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return await LoadDocumentDtoAsync(db, company, branch, header.SourcingId, cancellationToken);
    }

    public async Task<PoSourcingOperationResult> ApproveAwardAsync(
        PoSourcingAwardDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return PoSourcingOperationResult.Fail(context.Error);
        }

        if (!await _featureGuard.IsEnabledAsync(cancellationToken))
        {
            return FeatureDisabledResult();
        }

        if (!await CanAsync(PermissionCodes.Approve, cancellationToken))
        {
            return PoSourcingOperationResult.Fail("Not authorized.", PoSourcingErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var company = context.CompanyCode!;
        var branch = context.BranchCode!;
        var userId = Truncate(context.UserId!, 20);
        var nowUtc = DateTime.UtcNow;

        if (!await _featureGuard.IsEnabledAuthoritativeAsync(db, company, lockRowForUpdate: true, cancellationToken))
        {
            await tx.RollbackAsync(cancellationToken);
            return FeatureDisabledResult();
        }

        var header = await LockSourcingHeaderAsync(db, company, branch, request.SourcingId, cancellationToken);
        if (header is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Sourcing document was not found.", PoSourcingErrorKind.NotFound);
        }

        var award = await db.PoSourcingAwards
            .FirstOrDefaultAsync(a => a.CompanyCode == company && a.BranchCode == branch && a.SourcingId == request.SourcingId && a.AwardId == request.AwardId, cancellationToken);
        if (award is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Award record was not found.", PoSourcingErrorKind.NotFound);
        }

        if (award.Status != PoSourcingAwardStatuses.Proposed)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail($"Only PROPOSED awards can be approved (current status: {award.Status}).");
        }

        award.Status = PoSourcingAwardStatuses.Approved;
        award.DecidedBy = userId;
        award.DecidedAtUtc = nowUtc;
        award.DecisionReason = TruncateOptional(request.Remarks, 500);
        TouchRowVersion(db, award);

        var priorStatus = header.Status;
        header.Status = PoSourcingStatuses.Awarded;
        header.ModifiedBy = userId;
        header.ModifiedDate = nowUtc;
        TouchRowVersion(db, header);

        db.PoSourcingEvents.Add(new PoSourcingEvent
        {
            CompanyCode = company,
            BranchCode = branch,
            SourcingId = header.SourcingId,
            AwardId = award.AwardId,
            EventType = PoSourcingEventTypes.AwardApproved,
            PriorStatus = priorStatus,
            NewStatus = header.Status,
            Reason = $"Approved Award v{award.AwardVersion}.",
            ActorUserId = userId,
            OccurredAtUtc = nowUtc
        });

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return await LoadDocumentDtoAsync(db, company, branch, header.SourcingId, cancellationToken);
    }

    public async Task<PoSourcingOperationResult> RejectAwardAsync(
        PoSourcingAwardDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return PoSourcingOperationResult.Fail(context.Error);
        }

        if (!await _featureGuard.IsEnabledAsync(cancellationToken))
        {
            return FeatureDisabledResult();
        }

        if (!await CanAsync(PermissionCodes.Reject, cancellationToken))
        {
            return PoSourcingOperationResult.Fail("Not authorized.", PoSourcingErrorKind.Authorization);
        }

        if (string.IsNullOrWhiteSpace(request.Remarks))
        {
            return PoSourcingOperationResult.FailValidation("Rejection reason is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var company = context.CompanyCode!;
        var branch = context.BranchCode!;
        var userId = Truncate(context.UserId!, 20);
        var nowUtc = DateTime.UtcNow;

        if (!await _featureGuard.IsEnabledAuthoritativeAsync(db, company, lockRowForUpdate: true, cancellationToken))
        {
            await tx.RollbackAsync(cancellationToken);
            return FeatureDisabledResult();
        }

        var header = await LockSourcingHeaderAsync(db, company, branch, request.SourcingId, cancellationToken);
        if (header is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Sourcing document was not found.", PoSourcingErrorKind.NotFound);
        }

        var award = await db.PoSourcingAwards
            .FirstOrDefaultAsync(a => a.CompanyCode == company && a.BranchCode == branch && a.SourcingId == request.SourcingId && a.AwardId == request.AwardId, cancellationToken);
        if (award is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Award record was not found.", PoSourcingErrorKind.NotFound);
        }

        if (award.Status != PoSourcingAwardStatuses.Proposed)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Only PROPOSED awards can be rejected.");
        }

        award.Status = PoSourcingAwardStatuses.Rejected;
        award.DecidedBy = userId;
        award.DecidedAtUtc = nowUtc;
        award.DecisionReason = Truncate(request.Remarks.Trim(), 500);
        TouchRowVersion(db, award);

        db.PoSourcingEvents.Add(new PoSourcingEvent
        {
            CompanyCode = company,
            BranchCode = branch,
            SourcingId = header.SourcingId,
            AwardId = award.AwardId,
            EventType = PoSourcingEventTypes.AwardRejected,
            Reason = $"Rejected Award v{award.AwardVersion}: {award.DecisionReason}",
            ActorUserId = userId,
            OccurredAtUtc = nowUtc
        });

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return await LoadDocumentDtoAsync(db, company, branch, header.SourcingId, cancellationToken);
    }
}
