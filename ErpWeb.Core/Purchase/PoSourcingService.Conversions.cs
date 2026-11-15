using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Purchase;

public sealed partial class PoSourcingService
{
    public async Task<PoSourcingOperationResult> PreviewConversionAsync(
        PoSourcingConversionPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
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

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = context.CompanyCode!;
        var branch = context.BranchCode!;

        var targetType = (request.TargetType ?? PoSourcingConversionTargetTypes.Po).Trim().ToUpperInvariant();
        if (targetType != PoSourcingConversionTargetTypes.Pr && targetType != PoSourcingConversionTargetTypes.Po)
        {
            return PoSourcingOperationResult.FailValidation("TargetType must be PR or PO.");
        }

        var header = await db.PoSourcingHdrs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.SourcingId == request.SourcingId, cancellationToken);
        if (header is null)
        {
            return PoSourcingOperationResult.Fail("Sourcing document was not found.", PoSourcingErrorKind.NotFound);
        }

        var award = await db.PoSourcingAwards.AsNoTracking()
            .Include(a => a.Lines)
            .FirstOrDefaultAsync(a => a.CompanyCode == company && a.BranchCode == branch && a.SourcingId == request.SourcingId && a.AwardId == request.AwardId, cancellationToken);
        if (award is null || award.Status != PoSourcingAwardStatuses.Approved)
        {
            return PoSourcingOperationResult.Fail("An approved Award is required before conversion.");
        }

        var rfqLines = await db.PoSourcingLines.AsNoTracking()
            .Where(l => l.CompanyCode == company && l.BranchCode == branch && l.SourcingId == request.SourcingId)
            .ToDictionaryAsync(l => l.SourcingLineId, cancellationToken);

        var activeConvByAwardLine = await GetActiveConvertedQtyByAwardLineAsync(db, company, branch, request.SourcingId, cancellationToken);

        var selectedInput = request.SelectedLines is { Count: > 0 }
            ? request.SelectedLines
            : award.Lines
                .Where(al => !al.IsQuarantined)
                .Select(al =>
                {
                    var convertedPur = activeConvByAwardLine.GetValueOrDefault(al.AwardLineId).PurQty;
                    return new PoSourcingConversionLineSelectionDto
                    {
                        AwardLineId = al.AwardLineId,
                        ConvertPurchaseQty = Math.Max(0m, al.AwardedQty - convertedPur)
                    };
                })
                .Where(x => x.ConvertPurchaseQty > 0m)
                .ToList();

        var blockingErrors = new List<string>();
        var validSelections = new List<(PoSourcingAwardLine AwardLine, PoSourcingLine RfqLine, decimal ConvertQty)>();

        var activeSuppliers = await db.PoSuppliers.AsNoTracking()
            .Where(s => s.CompanyCode == company && s.BranchCode == branch && s.IsActive)
            .Select(s => s.SuppCode)
            .ToListAsync(cancellationToken);
        var activeSupplierSet = new HashSet<string>(activeSuppliers, StringComparer.OrdinalIgnoreCase);

        foreach (var sel in selectedInput)
        {
            var al = award.Lines.FirstOrDefault(x => x.AwardLineId == sel.AwardLineId);
            if (al is null)
            {
                blockingErrors.Add($"Award line {sel.AwardLineId} was not found.");
                continue;
            }

            if (al.IsQuarantined)
            {
                blockingErrors.Add($"Award line {sel.AwardLineId} is quarantined ({al.QuarantineReason ?? "detached while disabled"}) and cannot be converted.");
                continue;
            }

            if (!rfqLines.TryGetValue(al.SourcingLineId, out var rfqLine))
            {
                blockingErrors.Add($"RFQ line {al.SourcingLineId} was not found.");
                continue;
            }

            var convertedPur = activeConvByAwardLine.GetValueOrDefault(al.AwardLineId).PurQty;
            var remaining = Math.Max(0m, al.AwardedQty - convertedPur);
            var qty = sel.ConvertPurchaseQty > 0m ? sel.ConvertPurchaseQty : remaining;
            if (qty <= 0m || qty > remaining + 0.0005m)
            {
                blockingErrors.Add($"Line {rfqLine.LineNo}: convert quantity ({qty}) exceeds remaining awarded quantity ({remaining}).");
                continue;
            }

            if (string.IsNullOrWhiteSpace(al.ICodeSnapshot))
            {
                blockingErrors.Add($"Line {rfqLine.LineNo}: free-text item '{rfqLine.Description}' must be mapped to a master ItemCode before converting to {targetType}.");
            }

            if (targetType == PoSourcingConversionTargetTypes.Po)
            {
                if (al.AwardedUnitPrice <= 0m)
                {
                    blockingErrors.Add($"Line {rfqLine.LineNo}: zero-price award lines cannot be converted to a Purchase Order.");
                }

                if (string.IsNullOrWhiteSpace(al.SuppCodeSnapshot) || !activeSupplierSet.Contains(al.SuppCodeSnapshot.Trim()))
                {
                    blockingErrors.Add($"Line {rfqLine.LineNo}: supplier '{al.SupplierNameSnapshot}' is not an active master supplier in PoSupplier. Onboard the supplier first or convert to PR.");
                }
            }

            validSelections.Add((al, rfqLine, qty));
        }

        if (validSelections.Count == 0 && blockingErrors.Count == 0)
        {
            blockingErrors.Add("No open awarded lines are available to convert.");
        }

        var groups = new List<PoSourcingConversionPreviewGroupDto>();
        if (targetType == PoSourcingConversionTargetTypes.Pr)
        {
            var prType = string.IsNullOrWhiteSpace(request.PrType) ? PoPrTypes.Purchasing : request.PrType.Trim().ToUpperInvariant();
            var grouped = validSelections
                .GroupBy(x => (
                    Currency: x.AwardLine.CurrencySnapshot.Trim().ToUpperInvariant(),
                    IsInclusive: x.AwardLine.IsInclusiveSnapshot,
                    PrType: prType))
                .OrderBy(g => g.Key.Currency, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Key.IsInclusive)
                .ToList();

            for (var i = 0; i < grouped.Count; i++)
            {
                var g = grouped[i];
                var first = g.First().AwardLine;
                groups.Add(new PoSourcingConversionPreviewGroupDto
                {
                    GroupIndex = i + 1,
                    GroupKeyDescription = $"PR Group #{i + 1}: {g.Key.Currency} / {(g.Key.IsInclusive ? "Tax Inclusive" : "Tax Exclusive")} / Type {g.Key.PrType}",
                    SupplierCode = first.SuppCodeSnapshot,
                    SupplierName = first.SupplierNameSnapshot,
                    Currency = g.Key.Currency,
                    IsInclusive = g.Key.IsInclusive,
                    PrType = g.Key.PrType,
                    LineCount = g.Count(),
                    TotalNetAmount = g.Sum(x => Math.Round(x.ConvertQty * x.AwardLine.AwardedUnitPrice, 2, MidpointRounding.AwayFromZero))
                });
            }
        }
        else
        {
            var grouped = validSelections
                .GroupBy(x => (
                    SupplierCode: (x.AwardLine.SuppCodeSnapshot ?? string.Empty).Trim().ToUpperInvariant(),
                    Currency: x.AwardLine.CurrencySnapshot.Trim().ToUpperInvariant(),
                    IsInclusive: x.AwardLine.IsInclusiveSnapshot,
                    OneTimeItemYn: x.RfqLine.OneTimeItemYn))
                .OrderBy(g => g.Key.SupplierCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Key.Currency, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Key.IsInclusive)
                .ThenBy(g => g.Key.OneTimeItemYn)
                .ToList();

            for (var i = 0; i < grouped.Count; i++)
            {
                var g = grouped[i];
                var first = g.First().AwardLine;
                groups.Add(new PoSourcingConversionPreviewGroupDto
                {
                    GroupIndex = i + 1,
                    GroupKeyDescription = $"PO Group #{i + 1}: {first.SuppCodeSnapshot} ({first.SupplierNameSnapshot}) / {g.Key.Currency} / {(g.Key.IsInclusive ? "Inclusive" : "Exclusive")}",
                    SupplierCode = first.SuppCodeSnapshot,
                    SupplierName = first.SupplierNameSnapshot,
                    Currency = g.Key.Currency,
                    IsInclusive = g.Key.IsInclusive,
                    OneTimeItemYn = g.Key.OneTimeItemYn,
                    LineCount = g.Count(),
                    TotalNetAmount = g.Sum(x => Math.Round(x.ConvertQty * x.AwardLine.AwardedUnitPrice, 2, MidpointRounding.AwayFromZero))
                });
            }
        }

        return PoSourcingOperationResult.OkConversionPreview(new PoSourcingConversionPreviewDto
        {
            TargetType = targetType,
            CanProceed = blockingErrors.Count == 0 && groups.Count > 0,
            BlockingErrors = blockingErrors,
            Groups = groups
        });
    }

    public async Task<PoSourcingOperationResult> ConvertToPrAsync(
        PoSourcingConvertToPrRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty)
        {
            return PoSourcingOperationResult.FailValidation("RequestId is required for idempotent PR conversion.");
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

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken) && !await CanAsync(PermissionCodes.Approve, cancellationToken))
        {
            return PoSourcingOperationResult.Fail("Not authorized on Purchase Sourcing.", PoSourcingErrorKind.Authorization);
        }

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseRequisition, PermissionCodes.Add, cancellationToken))
        {
            return PoSourcingOperationResult.Fail("Not authorized to create Purchase Requisitions (PO_PR/ADD).", PoSourcingErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = context.CompanyCode!;
        var branch = context.BranchCode!;

        var header = await db.PoSourcingHdrs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.SourcingId == request.SourcingId, cancellationToken);
        if (header is null)
        {
            return PoSourcingOperationResult.Fail("Sourcing document was not found.", PoSourcingErrorKind.NotFound);
        }

        var award = await db.PoSourcingAwards.AsNoTracking()
            .Include(a => a.Lines)
            .FirstOrDefaultAsync(a => a.CompanyCode == company && a.BranchCode == branch && a.SourcingId == request.SourcingId && a.AwardId == request.AwardId, cancellationToken);
        if (award is null || award.Status != PoSourcingAwardStatuses.Approved)
        {
            return PoSourcingOperationResult.Fail("An approved Award is required before converting to PR.");
        }

        var rfqLines = await db.PoSourcingLines.AsNoTracking()
            .Where(l => l.CompanyCode == company && l.BranchCode == branch && l.SourcingId == request.SourcingId)
            .ToDictionaryAsync(l => l.SourcingLineId, cancellationToken);

        var quotesById = await db.PoSourcingQuotes.AsNoTracking()
            .Where(q => q.CompanyCode == company && q.BranchCode == branch && q.SourcingId == request.SourcingId)
            .ToDictionaryAsync(q => q.QuoteId, cancellationToken);

        var prType = string.IsNullOrWhiteSpace(request.PrType) ? PoPrTypes.Purchasing : request.PrType.Trim().ToUpperInvariant();
        if (prType == "P")
        {
            prType = PoPrTypes.Purchasing;
        }
        else if (prType == "R")
        {
            prType = PoPrTypes.Repair;
        }

        var candidateRequestIds = award.Lines
            .Select(al =>
            {
                var gk = $"PR|{request.SourcingId}|{request.AwardId}|{al.CurrencySnapshot.Trim().ToUpperInvariant()}|{al.IsInclusiveSnapshot}|{prType}";
                return DeriveGroupRequestId(request.RequestId, gk).ToString("N");
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var activeConvByAwardLine = await GetActiveConvertedQtyByAwardLineAsync(
            db, company, branch, request.SourcingId, cancellationToken, candidateRequestIds);

        var selectedInput = request.SelectedLines is { Count: > 0 }
            ? request.SelectedLines
            : award.Lines
                .Where(al => !al.IsQuarantined)
                .Select(al =>
                {
                    var convertedPur = activeConvByAwardLine.GetValueOrDefault(al.AwardLineId).PurQty;
                    return new PoSourcingConversionLineSelectionDto
                    {
                        AwardLineId = al.AwardLineId,
                        ConvertPurchaseQty = Math.Max(0m, al.AwardedQty - convertedPur)
                    };
                })
                .Where(x => x.ConvertPurchaseQty > 0m)
                .ToList();

        if (selectedInput.Count == 0)
        {
            return PoSourcingOperationResult.FailValidation("At least one award line must be selected for PR conversion.");
        }

        var activeSuppliers = await db.PoSuppliers.AsNoTracking()
            .Where(s => s.CompanyCode == company && s.BranchCode == branch && s.IsActive)
            .ToDictionaryAsync(s => s.SuppCode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var itemsToConvert = new List<(PoSourcingAwardLine AwardLine, PoSourcingLine RfqLine, decimal ConvertQty)>();
        foreach (var sel in selectedInput)
        {
            var al = award.Lines.FirstOrDefault(x => x.AwardLineId == sel.AwardLineId);
            if (al is null)
            {
                return PoSourcingOperationResult.Fail($"Award line {sel.AwardLineId} was not found.");
            }

            if (al.IsQuarantined)
            {
                return PoSourcingOperationResult.Fail($"Award line {sel.AwardLineId} is quarantined and cannot be converted.");
            }

            if (!rfqLines.TryGetValue(al.SourcingLineId, out var rfqLine))
            {
                return PoSourcingOperationResult.Fail($"RFQ line {al.SourcingLineId} was not found.");
            }

            if (string.IsNullOrWhiteSpace(al.ICodeSnapshot))
            {
                return PoSourcingOperationResult.Fail(
                    $"Line {rfqLine.LineNo}: free-text item '{rfqLine.Description}' must be mapped to a master ItemCode before converting to PR.");
            }

            var qty = Math.Round(sel.ConvertPurchaseQty, 4, MidpointRounding.AwayFromZero);
            if (qty <= 0m)
            {
                return PoSourcingOperationResult.FailValidation($"Line {rfqLine.LineNo}: conversion quantity must be greater than zero.");
            }

            itemsToConvert.Add((al, rfqLine, qty));
        }

        var groups = itemsToConvert
            .GroupBy(x => (
                Currency: x.AwardLine.CurrencySnapshot.Trim().ToUpperInvariant(),
                IsInclusive: x.AwardLine.IsInclusiveSnapshot,
                PrType: prType))
            .OrderBy(g => g.Key.Currency, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.IsInclusive)
            .ToList();

        var createdPrNumbers = new List<string>();
        var anyReplayed = false;

        for (var groupIdx = 0; groupIdx < groups.Count; groupIdx++)
        {
            var group = groups[groupIdx].OrderBy(x => x.RfqLine.LineNo).ThenBy(x => x.AwardLine.AwardLineId).ToList();
            var groupKey = $"PR|{request.SourcingId}|{request.AwardId}|{groups[groupIdx].Key.Currency}|{groups[groupIdx].Key.IsInclusive}|{groups[groupIdx].Key.PrType}";
            var groupRequestId = DeriveGroupRequestId(request.RequestId, groupKey).ToString("N");
            var payloadHash = ComputePayloadHash(
                "PR",
                request.SourcingId,
                request.AwardId,
                groupKey,
                group.Select(x => (x.AwardLine.AwardLineId, x.ConvertQty, x.AwardLine.AwardedUnitPrice)));

            var existingConv = await db.PoSourcingConversions.AsNoTracking()
                .FirstOrDefaultAsync(c => c.CompanyCode == company && c.BranchCode == branch && c.RequestId == groupRequestId, cancellationToken);
            if (existingConv is not null)
            {
                if (!string.Equals(existingConv.PayloadHash, payloadHash, StringComparison.OrdinalIgnoreCase))
                {
                    return PoSourcingOperationResult.Fail(
                        "Idempotency conflict: RequestId was already used with a different conversion payload.",
                        PoSourcingErrorKind.IdempotencyConflict);
                }

                createdPrNumbers.Add(existingConv.TargetDocNo);
                anyReplayed = true;
                continue;
            }

            var prLines = new List<PoPrLineDto>(group.Count);
            foreach (var (al, rfqLine, convertQty) in group)
            {
                PoSupplier? masterVendor = null;
                if (!string.IsNullOrWhiteSpace(al.SuppCodeSnapshot))
                {
                    activeSuppliers.TryGetValue(al.SuppCodeSnapshot.Trim(), out masterVendor);
                }

                quotesById.TryGetValue(al.QuoteId, out var quote);
                var packSz = al.PackSzSnapshot > 0m ? al.PackSzSnapshot : rfqLine.PackSz;
                var stdQty = Math.Round(convertQty * packSz, 4, MidpointRounding.AwayFromZero);

                prLines.Add(new PoPrLineDto
                {
                    OneTimeItemYn = rfqLine.OneTimeItemYn,
                    ICode = al.ICodeSnapshot!.Trim(),
                    IDesc = rfqLine.Description,
                    Qty = stdQty,
                    PackSz = packSz,
                    StdUom = al.StdUomSnapshot,
                    PurchaseQty = convertQty,
                    PurchaseUom = al.AwardedUom,
                    Currency = al.CurrencySnapshot,
                    UnitPrice = al.AwardedUnitPrice,
                    OneTimeVendor = masterVendor is null,
                    VendorCd = masterVendor?.SuppCode,
                    VendNm = masterVendor?.SuppName ?? al.SupplierNameSnapshot,
                    Purpose = rfqLine.Notes ?? $"Awarded via Sourcing {header.SourcingNo}",
                    StdQty = stdQty,
                    PaymentTerm = quote?.PayTermCode ?? masterVendor?.PayCode,
                    BuyingTerm = quote?.BuyingTermCode ?? masterVendor?.BuyingTerm,
                    TaxGroup = al.TaxGroupSnapshot ?? masterVendor?.TaxGrCode ?? masterVendor?.TaxGroup,
                    IsInclusive = al.IsInclusiveSnapshot,
                    ToWarehouse = rfqLine.DeliveryWhse ?? header.DeliveryWhse,
                    EtaDt = rfqLine.RequiredDate ?? header.RequiredDate
                });
            }

            var prSaveRequest = new PoPrSaveRequest
            {
                CreateDt = request.CreateDt?.Date ?? _dates.Today.Date,
                Requester = header.Requester ?? context.UserId,
                DeptCode = header.DeptCode,
                ProjId = header.ProjectCode,
                PrType = groups[groupIdx].Key.PrType,
                Remarks = TruncateOptional(request.Remarks ?? $"Converted from Sourcing {header.SourcingNo} (Award v{award.AwardVersion})", 500),
                Lines = prLines
            };

            var firstAl = group[0].AwardLine;
            var hook = CreatePrConversionHook(
                company,
                branch,
                context.UserId!,
                request.SourcingId,
                request.AwardId,
                groupRequestId,
                payloadHash,
                firstAl.SuppCodeSnapshot,
                groups[groupIdx].Key.Currency,
                groups[groupIdx].Key.IsInclusive,
                group.Any(x => x.RfqLine.OneTimeItemYn),
                group.Select(x => (
                    x.AwardLine.AwardLineId,
                    x.RfqLine.SourcingLineId,
                    x.ConvertQty,
                    x.AwardLine.AwardedUom,
                    x.AwardLine.PackSzSnapshot > 0m ? x.AwardLine.PackSzSnapshot : x.RfqLine.PackSz,
                    x.AwardLine.AwardedUnitPrice,
                    x.RfqLine.PrNo,
                    x.RfqLine.PrLineNo)).ToList());

            var prResult = await _prService.SaveNewFromSourcingAsync(prSaveRequest, hook, cancellationToken);
            if (!prResult.Succeeded || prResult.Document is null)
            {
                return PoSourcingOperationResult.Fail(
                    prResult.ErrorMessage ?? "Failed to create Purchase Requisition from Sourcing award.");
            }

            createdPrNumbers.Add(prResult.Document.PrNo);
        }

        await using var reloadDb = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var docResult = await LoadDocumentDtoAsync(reloadDb, company, branch, request.SourcingId, cancellationToken);
        return PoSourcingOperationResult.OkConversion(
            new PoSourcingConversionBatchResultDto
            {
                TargetType = PoSourcingConversionTargetTypes.Pr,
                CreatedDocumentNumbers = createdPrNumbers,
                WasIdempotentReplay = anyReplayed && createdPrNumbers.Count == groups.Count
            },
            docResult.Document);
    }

    public async Task<PoSourcingOperationResult> ConvertToPoAsync(
        PoSourcingConvertToPoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty)
        {
            return PoSourcingOperationResult.FailValidation("RequestId is required for idempotent PO conversion.");
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

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken) && !await CanAsync(PermissionCodes.Approve, cancellationToken))
        {
            return PoSourcingOperationResult.Fail("Not authorized on Purchase Sourcing.", PoSourcingErrorKind.Authorization);
        }

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Add, cancellationToken))
        {
            return PoSourcingOperationResult.Fail("Not authorized to create Purchase Orders (PO_ORDER/ADD).", PoSourcingErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = context.CompanyCode!;
        var branch = context.BranchCode!;

        var header = await db.PoSourcingHdrs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.SourcingId == request.SourcingId, cancellationToken);
        if (header is null)
        {
            return PoSourcingOperationResult.Fail("Sourcing document was not found.", PoSourcingErrorKind.NotFound);
        }

        var award = await db.PoSourcingAwards.AsNoTracking()
            .Include(a => a.Lines)
            .FirstOrDefaultAsync(a => a.CompanyCode == company && a.BranchCode == branch && a.SourcingId == request.SourcingId && a.AwardId == request.AwardId, cancellationToken);
        if (award is null || award.Status != PoSourcingAwardStatuses.Approved)
        {
            return PoSourcingOperationResult.Fail("An approved Award is required before converting to PO.");
        }

        var rfqLines = await db.PoSourcingLines.AsNoTracking()
            .Where(l => l.CompanyCode == company && l.BranchCode == branch && l.SourcingId == request.SourcingId)
            .ToDictionaryAsync(l => l.SourcingLineId, cancellationToken);

        var quotesById = await db.PoSourcingQuotes.AsNoTracking()
            .Include(q => q.Lines)
            .Where(q => q.CompanyCode == company && q.BranchCode == branch && q.SourcingId == request.SourcingId)
            .ToDictionaryAsync(q => q.QuoteId, cancellationToken);

        var candidateRequestIds = award.Lines
            .Select(al =>
            {
                var oneTimeYn = rfqLines.TryGetValue(al.SourcingLineId, out var rl) && rl.OneTimeItemYn;
                var supp = (al.SuppCodeSnapshot ?? string.Empty).Trim().ToUpperInvariant();
                var gk = $"PO|{request.SourcingId}|{request.AwardId}|{supp}|{al.CurrencySnapshot.Trim().ToUpperInvariant()}|{al.IsInclusiveSnapshot}|{oneTimeYn}";
                return DeriveGroupRequestId(request.RequestId, gk).ToString("N");
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var activeConvByAwardLine = await GetActiveConvertedQtyByAwardLineAsync(
            db, company, branch, request.SourcingId, cancellationToken, candidateRequestIds);

        var selectedInput = request.SelectedLines is { Count: > 0 }
            ? request.SelectedLines
            : award.Lines
                .Where(al => !al.IsQuarantined)
                .Select(al =>
                {
                    var convertedPur = activeConvByAwardLine.GetValueOrDefault(al.AwardLineId).PurQty;
                    return new PoSourcingConversionLineSelectionDto
                    {
                        AwardLineId = al.AwardLineId,
                        ConvertPurchaseQty = Math.Max(0m, al.AwardedQty - convertedPur)
                    };
                })
                .Where(x => x.ConvertPurchaseQty > 0m)
                .ToList();

        if (selectedInput.Count == 0)
        {
            return PoSourcingOperationResult.FailValidation("At least one award line must be selected for PO conversion.");
        }

        var activeSuppliers = await db.PoSuppliers.AsNoTracking()
            .Where(s => s.CompanyCode == company && s.BranchCode == branch && s.IsActive)
            .ToDictionaryAsync(s => s.SuppCode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var itemsToConvert = new List<(PoSourcingAwardLine AwardLine, PoSourcingLine RfqLine, PoSupplier MasterSupplier, decimal ConvertQty)>();
        foreach (var sel in selectedInput)
        {
            var al = award.Lines.FirstOrDefault(x => x.AwardLineId == sel.AwardLineId);
            if (al is null)
            {
                return PoSourcingOperationResult.Fail($"Award line {sel.AwardLineId} was not found.");
            }

            if (al.IsQuarantined)
            {
                return PoSourcingOperationResult.Fail($"Award line {sel.AwardLineId} is quarantined and cannot be converted.");
            }

            if (!rfqLines.TryGetValue(al.SourcingLineId, out var rfqLine))
            {
                return PoSourcingOperationResult.Fail($"RFQ line {al.SourcingLineId} was not found.");
            }

            if (string.IsNullOrWhiteSpace(al.ICodeSnapshot))
            {
                return PoSourcingOperationResult.Fail(
                    $"Line {rfqLine.LineNo}: free-text item '{rfqLine.Description}' cannot be converted directly to a Purchase Order.");
            }

            if (al.AwardedUnitPrice <= 0m)
            {
                return PoSourcingOperationResult.Fail(
                    $"Line {rfqLine.LineNo}: zero-price award lines cannot be converted to a Purchase Order.");
            }

            if (string.IsNullOrWhiteSpace(al.SuppCodeSnapshot)
                || !activeSuppliers.TryGetValue(al.SuppCodeSnapshot.Trim(), out var masterSupplier))
            {
                return PoSourcingOperationResult.Fail(
                    $"Line {rfqLine.LineNo}: supplier '{al.SupplierNameSnapshot}' is not an active master supplier in PoSupplier. Onboard the supplier first or convert to PR.");
            }

            var qty = Math.Round(sel.ConvertPurchaseQty, 4, MidpointRounding.AwayFromZero);
            if (qty <= 0m)
            {
                return PoSourcingOperationResult.FailValidation($"Line {rfqLine.LineNo}: conversion quantity must be greater than zero.");
            }

            itemsToConvert.Add((al, rfqLine, masterSupplier, qty));
        }

        var groups = itemsToConvert
            .GroupBy(x => (
                SupplierCode: x.MasterSupplier.SuppCode.Trim().ToUpperInvariant(),
                Currency: x.AwardLine.CurrencySnapshot.Trim().ToUpperInvariant(),
                IsInclusive: x.AwardLine.IsInclusiveSnapshot,
                OneTimeItemYn: x.RfqLine.OneTimeItemYn))
            .OrderBy(g => g.Key.SupplierCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.Currency, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.IsInclusive)
            .ThenBy(g => g.Key.OneTimeItemYn)
            .ToList();

        var createdPoNumbers = new List<string>();
        var anyReplayed = false;

        for (var groupIdx = 0; groupIdx < groups.Count; groupIdx++)
        {
            var group = groups[groupIdx].OrderBy(x => x.RfqLine.LineNo).ThenBy(x => x.AwardLine.AwardLineId).ToList();
            var groupKey = $"PO|{request.SourcingId}|{request.AwardId}|{groups[groupIdx].Key.SupplierCode}|{groups[groupIdx].Key.Currency}|{groups[groupIdx].Key.IsInclusive}|{groups[groupIdx].Key.OneTimeItemYn}";
            var groupRequestId = DeriveGroupRequestId(request.RequestId, groupKey).ToString("N");
            var payloadHash = ComputePayloadHash(
                "PO",
                request.SourcingId,
                request.AwardId,
                groupKey,
                group.Select(x => (x.AwardLine.AwardLineId, x.ConvertQty, x.AwardLine.AwardedUnitPrice)));

            var existingConv = await db.PoSourcingConversions.AsNoTracking()
                .FirstOrDefaultAsync(c => c.CompanyCode == company && c.BranchCode == branch && c.RequestId == groupRequestId, cancellationToken);
            if (existingConv is not null)
            {
                if (!string.Equals(existingConv.PayloadHash, payloadHash, StringComparison.OrdinalIgnoreCase))
                {
                    return PoSourcingOperationResult.Fail(
                        "Idempotency conflict: RequestId was already used with a different PO conversion payload.",
                        PoSourcingErrorKind.IdempotencyConflict);
                }

                createdPoNumbers.Add(existingConv.TargetDocNo);
                anyReplayed = true;
                continue;
            }

            var firstItem = group[0];
            var supplier = firstItem.MasterSupplier;
            quotesById.TryGetValue(firstItem.AwardLine.QuoteId, out var firstQuote);

            var poLines = new List<PoOrderLineDto>(group.Count);
            foreach (var (al, rfqLine, _, convertQty) in group)
            {
                quotesById.TryGetValue(al.QuoteId, out var quote);
                var qLine = quote?.Lines.FirstOrDefault(ql => ql.QuoteLineId == al.QuoteLineId);
                var packSz = al.PackSzSnapshot > 0m ? al.PackSzSnapshot : rfqLine.PackSz;
                var stdQty = Math.Round(convertQty * packSz, 4, MidpointRounding.AwayFromZero);

                DateTime? etaDate = qLine?.EstimatedDeliveryDate ?? rfqLine.RequiredDate ?? header.RequiredDate;
                if (qLine?.LeadTimeDays is int leadDays && leadDays > 0)
                {
                    var leadEta = (request.PoDate?.Date ?? _dates.Today.Date).AddDays(leadDays);
                    if (etaDate is null || leadEta > etaDate.Value)
                    {
                        etaDate = leadEta;
                    }
                }

                poLines.Add(new PoOrderLineDto
                {
                    PrNo = rfqLine.PrNo,
                    PrLineNo = rfqLine.PrLineNo,
                    OneTime = rfqLine.OneTimeItemYn,
                    ICode = al.ICodeSnapshot!.Trim(),
                    IDesc = rfqLine.Description,
                    PoDesc = rfqLine.Description,
                    PoPurQty = convertQty,
                    PoQty = stdQty,
                    PurchaseUom = al.AwardedUom,
                    PackSz = packSz,
                    StdUom = al.StdUomSnapshot,
                    CurCode = al.CurrencySnapshot,
                    PoUnitPrice = al.AwardedUnitPrice,
                    DiscountType = al.DiscPerSnapshot > 0m ? "%" : null,
                    ItemDiscount = al.DiscPerSnapshot,
                    TaxGroup = al.TaxGroupSnapshot ?? supplier.TaxGrCode ?? supplier.TaxGroup,
                    IsInclusive = al.IsInclusiveSnapshot,
                    ToWarehouse = rfqLine.DeliveryWhse ?? header.DeliveryWhse,
                    EtaDate = etaDate,
                    ProjId = header.ProjectCode,
                    Requester = header.Requester,
                    Remarks = rfqLine.Notes ?? $"Sourcing {header.SourcingNo}"
                });
            }

            var poSaveRequest = new PoOrderSaveRequest
            {
                PoDate = request.PoDate?.Date ?? _dates.Today.Date,
                Buyer = header.BuyerCode,
                OneTime = groups[groupIdx].Key.OneTimeItemYn,
                VendCode = supplier.SuppCode,
                VendName = supplier.SuppName,
                VendAddress1 = supplier.Address1,
                VendAddress2 = supplier.Address2,
                VendAddress3 = supplier.Address3,
                VendAddress4 = supplier.Address4,
                VendCity = supplier.City,
                VendState = supplier.State,
                VendPostal = supplier.PostalCode,
                VendCountryCode = supplier.CountryCode ?? supplier.Country,
                VendTel = supplier.Tel,
                VendFax = supplier.Fax,
                CurCode = groups[groupIdx].Key.Currency,
                TermCode = firstQuote?.PayTermCode ?? supplier.PayCode,
                BuyingTerm = firstQuote?.BuyingTermCode ?? supplier.BuyingTerm,
                ContactPerson = supplier.ContactPerson,
                Email = supplier.Email ?? supplier.ContactEmail,
                Website = supplier.Website,
                TaxGrpCode = firstItem.AwardLine.TaxGroupSnapshot ?? supplier.TaxGrCode ?? supplier.TaxGroup,
                DeptCode = header.DeptCode,
                ProjId = header.ProjectCode,
                QuatationNo = firstQuote?.SupplierQuoteRef,
                Ref1 = header.SourcingNo,
                SiRemark = TruncateOptional(request.SiRemark ?? $"Converted from Sourcing {header.SourcingNo} (Award v{award.AwardVersion})", 500),
                Lines = poLines
            };

            var hook = CreatePoConversionHook(
                company,
                branch,
                context.UserId!,
                request.SourcingId,
                request.AwardId,
                groupRequestId,
                payloadHash,
                supplier.SuppCode,
                groups[groupIdx].Key.Currency,
                groups[groupIdx].Key.IsInclusive,
                groups[groupIdx].Key.OneTimeItemYn,
                group.Select(x => (
                    x.AwardLine.AwardLineId,
                    x.RfqLine.SourcingLineId,
                    x.ConvertQty,
                    x.AwardLine.AwardedUom,
                    x.AwardLine.PackSzSnapshot > 0m ? x.AwardLine.PackSzSnapshot : x.RfqLine.PackSz,
                    x.AwardLine.AwardedUnitPrice,
                    x.RfqLine.PrNo,
                    x.RfqLine.PrLineNo)).ToList());

            var poResult = await _poService.SaveNewFromSourcingAsync(poSaveRequest, hook, cancellationToken);
            if (!poResult.Succeeded || poResult.Document is null)
            {
                return PoSourcingOperationResult.Fail(
                    poResult.ErrorMessage ?? "Failed to create Purchase Order from Sourcing award.");
            }

            createdPoNumbers.Add(poResult.Document.PoNo);
        }

        await using var reloadDb = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var docResult = await LoadDocumentDtoAsync(reloadDb, company, branch, request.SourcingId, cancellationToken);
        return PoSourcingOperationResult.OkConversion(
            new PoSourcingConversionBatchResultDto
            {
                TargetType = PoSourcingConversionTargetTypes.Po,
                CreatedDocumentNumbers = createdPoNumbers,
                WasIdempotentReplay = anyReplayed && createdPoNumbers.Count == groups.Count
            },
            docResult.Document);
    }

    private PoSourcingPrSaveHook CreatePrConversionHook(
        string companyCode,
        string branchCode,
        string actorUserId,
        long sourcingId,
        long awardId,
        string groupRequestId,
        string payloadHash,
        string? targetSupplierCode,
        string targetCurrency,
        bool targetIsInclusive,
        bool targetOneTimeItemYn,
        IReadOnlyList<(long AwardLineId, long SourcingLineId, decimal ConvertPurchaseQty, string TargetUom, decimal PackSize, decimal TargetUnitPrice, string? SourcePrNo, short? SourcePrLineNo)> selections)
    {
        return new PoSourcingPrSaveHook
        {
            PreserveExplicitLineUnitPrice = true,
            BeforeDocumentLocksAsync = async (db, ct) =>
            {
                return await ValidateAndLockConversionSourceAsync(
                    db, companyCode, branchCode, sourcingId, awardId, groupRequestId, selections, ct);
            },
            BeforeCommitAsync = async (db, prHeader, prDetails, ct) =>
            {
                return await RecordCommittedConversionAsync(
                    db,
                    prHeader.CompanyCode,
                    prHeader.BranchCode,
                    sourcingId,
                    awardId,
                    groupRequestId,
                    payloadHash,
                    PoSourcingConversionTargetTypes.Pr,
                    prHeader.PrNo,
                    targetRelNo: 0,
                    targetSupplierCode,
                    targetCurrency,
                    targetIsInclusive,
                    targetOneTimeItemYn,
                    prDetails.Select(d => d.Line).ToList(),
                    selections,
                    actorUserId,
                    ct);
            }
        };
    }

    private PoSourcingPoSaveHook CreatePoConversionHook(
        string companyCode,
        string branchCode,
        string actorUserId,
        long sourcingId,
        long awardId,
        string groupRequestId,
        string payloadHash,
        string? targetSupplierCode,
        string targetCurrency,
        bool targetIsInclusive,
        bool targetOneTimeItemYn,
        IReadOnlyList<(long AwardLineId, long SourcingLineId, decimal ConvertPurchaseQty, string TargetUom, decimal PackSize, decimal TargetUnitPrice, string? SourcePrNo, short? SourcePrLineNo)> selections)
    {
        return new PoSourcingPoSaveHook
        {
            BeforeDocumentLocksAsync = async (db, ct) =>
            {
                return await ValidateAndLockConversionSourceAsync(
                    db, companyCode, branchCode, sourcingId, awardId, groupRequestId, selections, ct);
            },
            BeforeCommitAsync = async (db, poHeader, poDetails, ct) =>
            {
                return await RecordCommittedConversionAsync(
                    db,
                    poHeader.CompanyCode,
                    poHeader.BranchCode,
                    sourcingId,
                    awardId,
                    groupRequestId,
                    payloadHash,
                    PoSourcingConversionTargetTypes.Po,
                    poHeader.PoNo,
                    poHeader.PoRelNo,
                    targetSupplierCode,
                    targetCurrency,
                    targetIsInclusive,
                    targetOneTimeItemYn,
                    poDetails.Select(d => d.Line).ToList(),
                    selections,
                    actorUserId,
                    ct);
            }
        };
    }

    private async Task<string?> ValidateAndLockConversionSourceAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long sourcingId,
        long awardId,
        string groupRequestId,
        IReadOnlyList<(long AwardLineId, long SourcingLineId, decimal ConvertPurchaseQty, string TargetUom, decimal PackSize, decimal TargetUnitPrice, string? SourcePrNo, short? SourcePrLineNo)> selections,
        CancellationToken cancellationToken)
    {
        if (!await _featureGuard.IsEnabledAuthoritativeAsync(db, companyCode, lockRowForUpdate: true, cancellationToken))
        {
            return "Purchase Sourcing (RFQ) is disabled for this company.";
        }

        var header = await LockSourcingHeaderAsync(db, companyCode, branchCode, sourcingId, cancellationToken);
        if (header is null)
        {
            return "Sourcing document was not found.";
        }

        if (header.Status != PoSourcingStatuses.Awarded
            && header.Status != PoSourcingStatuses.PartiallyConverted)
        {
            return $"Sourcing document in status '{header.Status}' cannot be converted.";
        }

        var existingRequest = await db.PoSourcingConversions
            .AnyAsync(c => c.CompanyCode == companyCode && c.BranchCode == branchCode && c.RequestId == groupRequestId, cancellationToken);
        if (existingRequest)
        {
            return "Duplicate conversion RequestId detected inside transaction.";
        }

        var award = await db.PoSourcingAwards
            .Include(a => a.Lines)
            .FirstOrDefaultAsync(a => a.CompanyCode == companyCode && a.BranchCode == branchCode && a.SourcingId == sourcingId && a.AwardId == awardId, cancellationToken);
        if (award is null || award.Status != PoSourcingAwardStatuses.Approved)
        {
            return "Approved Sourcing Award was not found.";
        }

        var activeConvByAwardLine = await GetActiveConvertedQtyByAwardLineAsync(db, companyCode, branchCode, sourcingId, cancellationToken);

        foreach (var sel in selections)
        {
            var al = award.Lines.FirstOrDefault(x => x.AwardLineId == sel.AwardLineId);
            if (al is null)
            {
                return $"Award line {sel.AwardLineId} was not found.";
            }

            if (al.IsQuarantined)
            {
                return $"Award line {sel.AwardLineId} is quarantined and cannot be converted.";
            }

            var convertedPur = activeConvByAwardLine.GetValueOrDefault(al.AwardLineId).PurQty;
            var remaining = Math.Max(0m, al.AwardedQty - convertedPur);
            if (sel.ConvertPurchaseQty > remaining + 0.0005m)
            {
                return $"Award line {sel.AwardLineId} remaining open quantity ({remaining}) is insufficient for requested conversion ({sel.ConvertPurchaseQty}).";
            }
        }

        return null;
    }

    private static async Task<string?> RecordCommittedConversionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long sourcingId,
        long awardId,
        string groupRequestId,
        string payloadHash,
        string targetType,
        string targetDocNo,
        short targetRelNo,
        string? targetSupplierCode,
        string targetCurrency,
        bool targetIsInclusive,
        bool targetOneTimeItemYn,
        IReadOnlyList<short> targetLineNos,
        IReadOnlyList<(long AwardLineId, long SourcingLineId, decimal ConvertPurchaseQty, string TargetUom, decimal PackSize, decimal TargetUnitPrice, string? SourcePrNo, short? SourcePrLineNo)> selections,
        string actorUserId,
        CancellationToken cancellationToken)
    {
        if (targetLineNos.Count != selections.Count)
        {
            return "Target line count mismatch during Sourcing conversion commit.";
        }

        var nowUtc = DateTime.UtcNow;
        var userId = Truncate(actorUserId, 20);

        var header = await db.PoSourcingHdrs
            .FirstAsync(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.SourcingId == sourcingId, cancellationToken);
        var rfqLines = await db.PoSourcingLines
            .Where(l => l.CompanyCode == companyCode && l.BranchCode == branchCode && l.SourcingId == sourcingId)
            .ToListAsync(cancellationToken);

        var awardLines = await db.PoSourcingAwardLines
            .Where(al => al.CompanyCode == companyCode && al.BranchCode == branchCode && al.SourcingId == sourcingId && al.AwardId == awardId)
            .ToListAsync(cancellationToken);

        var conv = new PoSourcingConversion
        {
            CompanyCode = companyCode,
            BranchCode = branchCode,
            SourcingId = sourcingId,
            AwardId = awardId,
            RequestId = groupRequestId,
            PayloadHash = payloadHash,
            TargetDocType = targetType,
            TargetDocNo = targetDocNo,
            TargetDocRelNo = targetRelNo,
            TargetSupplierCode = TruncateOptional(targetSupplierCode, 20),
            TargetCurrency = Truncate(targetCurrency, 20),
            TargetIsInclusive = targetIsInclusive,
            TargetOneTimeItemYn = targetOneTimeItemYn,
            Status = PoSourcingConversionStatuses.Active,
            CreatedBy = userId,
            CreatedAtUtc = nowUtc
        };
        TouchRowVersion(db, conv);
        db.PoSourcingConversions.Add(conv);
        await db.SaveChangesAsync(cancellationToken);

        for (var i = 0; i < selections.Count; i++)
        {
            var sel = selections[i];
            var targetLineNo = targetLineNos[i];
            var stdQty = Math.Round(sel.ConvertPurchaseQty * sel.PackSize, 4, MidpointRounding.AwayFromZero);

            var convLine = new PoSourcingConversionLine
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                SourcingId = sourcingId,
                ConversionId = conv.ConversionId,
                AwardId = awardId,
                AwardLineId = sel.AwardLineId,
                SourcingLineId = sel.SourcingLineId,
                TargetDocType = targetType,
                TargetDocNo = targetDocNo,
                TargetDocRelNo = targetRelNo,
                TargetLineNo = targetLineNo,
                OriginalTargetLineNo = targetLineNo,
                TargetUom = Truncate(sel.TargetUom, 10),
                TargetPackSz = sel.PackSize,
                ConvertedPurQty = sel.ConvertPurchaseQty,
                ConvertedStdQty = stdQty,
                EffectiveActiveStdQty = stdQty,
                TargetUnitPrice = sel.TargetUnitPrice,
                SourcePrNo = TruncateOptional(sel.SourcePrNo, 30),
                SourcePrLineNo = sel.SourcePrLineNo,
                LineLinkStatus = PoSourcingConversionStatuses.Active
            };
            db.PoSourcingConversionLines.Add(convLine);
        }

        await db.SaveChangesAsync(cancellationToken);

        var activeConvByAwardLine = await GetActiveConvertedQtyByAwardLineAsync(db, companyCode, branchCode, sourcingId, cancellationToken);
        var activeConvByRfqLine = await GetActiveConvertedQtyByRfqLineAsync(db, companyCode, branchCode, sourcingId, cancellationToken);

        var allAwardConverted = awardLines.All(al =>
            activeConvByAwardLine.GetValueOrDefault(al.AwardLineId).StdQty + 0.0005m >= al.AwardedStdQty);
        var allRfqConverted = rfqLines.All(l =>
            activeConvByRfqLine.GetValueOrDefault(l.SourcingLineId).StdQty + 0.0005m >= l.RequestedStdQty);

        var priorStatus = header.Status;
        header.Status = (allAwardConverted && allRfqConverted)
            ? PoSourcingStatuses.Converted
            : PoSourcingStatuses.PartiallyConverted;
        header.ModifiedBy = userId;
        header.ModifiedDate = nowUtc;
        TouchRowVersion(db, header);

        db.PoSourcingEvents.Add(new PoSourcingEvent
        {
            CompanyCode = companyCode,
            BranchCode = branchCode,
            SourcingId = sourcingId,
            AwardId = awardId,
            ConversionId = conv.ConversionId,
            EventType = targetType == PoSourcingConversionTargetTypes.Pr
                ? PoSourcingEventTypes.ConvertedToPr
                : PoSourcingEventTypes.ConvertedToPo,
            PriorStatus = priorStatus,
            NewStatus = header.Status,
            Reason = $"Converted {selections.Count} award line(s) to {targetType} {targetDocNo}.",
            ActorUserId = userId,
            OccurredAtUtc = nowUtc
        });

        return null;
    }

    private async Task<string?> SynchronizeSourcingLinesAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        PoSourcingHdr header,
        IReadOnlyList<PoSourcingLineDto> requestedLines,
        IDictionary<string, string> fieldErrors,
        CancellationToken cancellationToken)
    {
        var existingLines = await db.PoSourcingLines
            .Where(l => l.CompanyCode == companyCode && l.BranchCode == branchCode && l.SourcingId == header.SourcingId)
            .OrderBy(l => l.LineNo)
            .ToListAsync(cancellationToken);

        var hasQuotesOrAwards = await db.PoSourcingQuotes.AnyAsync(q => q.CompanyCode == companyCode && q.BranchCode == branchCode && q.SourcingId == header.SourcingId, cancellationToken)
            || await db.PoSourcingAwards.AnyAsync(a => a.CompanyCode == companyCode && a.BranchCode == branchCode && a.SourcingId == header.SourcingId, cancellationToken);

        var existingById = existingLines.ToDictionary(l => l.SourcingLineId);
        var keptIds = requestedLines.Where(x => x.SourcingLineId > 0).Select(x => x.SourcingLineId).ToHashSet();

        foreach (var removed in existingLines.Where(l => !keptIds.Contains(l.SourcingLineId)).ToList())
        {
            if (hasQuotesOrAwards)
            {
                return $"Line {removed.LineNo} cannot be removed after supplier quotations or awards have been recorded.";
            }

            db.PoSourcingLines.Remove(removed);
        }

        short nextLineNo = 1;
        for (var i = 0; i < requestedLines.Count; i++)
        {
            var src = requestedLines[i];
            var prefix = $"Lines[{i}]";

            if (src.RequestedPurchaseQty <= 0m)
            {
                fieldErrors[$"{prefix}.RequestedPurchaseQty"] = "Requested quantity must be greater than zero.";
                continue;
            }

            var itemCode = TruncateOptional(src.ItemCode, 50);
            var itemDesc = TruncateOptional(src.ItemDescription, 200);
            if (string.IsNullOrWhiteSpace(itemCode) && string.IsNullOrWhiteSpace(itemDesc))
            {
                fieldErrors[$"{prefix}.ItemDescription"] = "Item code or description is required.";
                continue;
            }

            var purUom = TruncateOptional(src.RequestedPurchaseUom, 10) ?? "EA";
            var packSize = src.PackSize > 0m ? src.PackSize : 1m;
            var stdUom = TruncateOptional(src.StdUom, 10) ?? purUom;
            var stdQty = Math.Round(src.RequestedPurchaseQty * packSize, 4, MidpointRounding.AwayFromZero);

            if (!string.IsNullOrWhiteSpace(src.SourcePrNo) && src.SourcePrLine is short prLineNo)
            {
                var prNo = src.SourcePrNo.Trim();
                var prDetail = await db.PoPrDetails
                    .Include(d => d.Pr)
                    .FirstOrDefaultAsync(d => d.CompanyCode == companyCode && d.BranchCode == branchCode && d.PrNo == prNo && d.Line == prLineNo, cancellationToken);
                if (prDetail is null || prDetail.Pr is null || !PoPrCalc.IsAvailableForPo(prDetail.Pr.Status))
                {
                    fieldErrors[$"{prefix}.SourcePrNo"] = $"PR {prNo}/{prLineNo} is not open for sourcing.";
                    continue;
                }

                var poConsumed = await db.PoOrderDetails
                    .Where(d => d.CompanyCode == companyCode
                        && d.BranchCode == branchCode
                        && d.PrNo == prNo
                        && d.PrLineNo == prLineNo
                        && d.Order != null
                        && d.Order.Status != PoOrderStatuses.Cancelled)
                    .SumAsync(d => (decimal?)d.PoPurQty, cancellationToken) ?? 0m;

                var otherSourcingCommitted = await db.PoSourcingLines
                    .Where(l => l.CompanyCode == companyCode
                        && l.BranchCode == branchCode
                        && l.PrNo == prNo
                        && l.PrLineNo == prLineNo
                        && l.SourcingId != header.SourcingId
                        && l.Sourcing != null
                        && l.Sourcing.Status != PoSourcingStatuses.Cancelled
                        && l.Sourcing.Status != PoSourcingStatuses.Converted)
                    .SumAsync(l => (decimal?)l.RequestedQty, cancellationToken) ?? 0m;

                var availablePrQty = Math.Round(prDetail.PurchaseQty - poConsumed - otherSourcingCommitted, 4, MidpointRounding.AwayFromZero);
                if (src.RequestedPurchaseQty > availablePrQty + 0.0005m)
                {
                    fieldErrors[$"{prefix}.RequestedPurchaseQty"] =
                        $"PR {prNo}/{prLineNo} available open quantity ({availablePrQty}) is less than requested ({src.RequestedPurchaseQty}).";
                    continue;
                }
            }

            if (src.SourcingLineId > 0 && existingById.TryGetValue(src.SourcingLineId, out var existing))
            {
                existing.LineNo = nextLineNo++;
                existing.PrNo = TruncateOptional(src.SourcePrNo, 30);
                existing.PrLineNo = src.SourcePrLine;
                existing.OneTimeItemYn = src.OneTimeItemYn || string.IsNullOrWhiteSpace(itemCode);
                existing.ICode = itemCode;
                existing.Description = itemDesc ?? itemCode!;
                existing.Specification = TruncateOptional(src.ItemSpec, 500);
                existing.RequestedQty = Math.Round(src.RequestedPurchaseQty, 4, MidpointRounding.AwayFromZero);
                existing.RequestedUom = purUom;
                existing.PackSz = packSize;
                existing.StdUom = stdUom;
                existing.RequestedStdQty = stdQty;
                existing.RequiredDate = src.RequiredEtaDate?.Date ?? header.RequiredDate;
                existing.DeliveryWhse = TruncateOptional(src.ToWarehouse, 20) ?? header.DeliveryWhse;
                existing.PrOriginalVendorCd = TruncateOptional(src.PreferredSupplierCode, 20);
                existing.Notes = TruncateOptional(src.Remarks, 300);
            }
            else
            {
                var line = new PoSourcingLine
                {
                    CompanyCode = companyCode,
                    BranchCode = branchCode,
                    SourcingId = header.SourcingId,
                    LineNo = nextLineNo++,
                    PrNo = TruncateOptional(src.SourcePrNo, 30),
                    PrLineNo = src.SourcePrLine,
                    OneTimeItemYn = src.OneTimeItemYn || string.IsNullOrWhiteSpace(itemCode),
                    ICode = itemCode,
                    Description = itemDesc ?? itemCode!,
                    Specification = TruncateOptional(src.ItemSpec, 500),
                    RequestedQty = Math.Round(src.RequestedPurchaseQty, 4, MidpointRounding.AwayFromZero),
                    RequestedUom = purUom,
                    PackSz = packSize,
                    StdUom = stdUom,
                    RequestedStdQty = stdQty,
                    RequiredDate = src.RequiredEtaDate?.Date ?? header.RequiredDate,
                    DeliveryWhse = TruncateOptional(src.ToWarehouse, 20) ?? header.DeliveryWhse,
                    PrOriginalVendorCd = TruncateOptional(src.PreferredSupplierCode, 20),
                    Notes = TruncateOptional(src.Remarks, 300)
                };
                db.PoSourcingLines.Add(line);
            }

            if (string.IsNullOrWhiteSpace(header.SourcePrNo) && !string.IsNullOrWhiteSpace(src.SourcePrNo))
            {
                header.SourcePrNo = TruncateOptional(src.SourcePrNo, 30);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    private static async Task<string?> SynchronizeSourcingSuppliersAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        PoSourcingHdr header,
        IReadOnlyList<PoSourcingSupplierDto> requestedSuppliers,
        string userId,
        DateTime nowUtc,
        IDictionary<string, string> fieldErrors,
        CancellationToken cancellationToken)
    {
        var existingSuppliers = await db.PoSourcingSuppliers
            .Where(s => s.CompanyCode == companyCode && s.BranchCode == branchCode && s.SourcingId == header.SourcingId)
            .ToListAsync(cancellationToken);

        var quotesBySupplierId = await db.PoSourcingQuotes
            .Where(q => q.CompanyCode == companyCode && q.BranchCode == branchCode && q.SourcingId == header.SourcingId)
            .Select(q => q.SourcingSupplierId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var quotedSet = quotesBySupplierId.ToHashSet();

        var existingById = existingSuppliers.ToDictionary(s => s.SourcingSupplierId);
        var keptIds = requestedSuppliers.Where(x => x.SourcingSupplierId > 0).Select(x => x.SourcingSupplierId).ToHashSet();

        foreach (var removed in existingSuppliers.Where(s => !keptIds.Contains(s.SourcingSupplierId)).ToList())
        {
            if (quotedSet.Contains(removed.SourcingSupplierId))
            {
                return $"Supplier '{removed.SupplierName}' cannot be removed because a quotation has already been recorded for it.";
            }

            db.PoSourcingSuppliers.Remove(removed);
        }

        var masterCodes = requestedSuppliers
            .Where(s => !string.IsNullOrWhiteSpace(s.SupplierCode))
            .Select(s => s.SupplierCode!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mastersByCode = masterCodes.Count == 0
            ? new Dictionary<string, PoSupplier>(StringComparer.OrdinalIgnoreCase)
            : await db.PoSuppliers.AsNoTracking()
                .Where(s => s.CompanyCode == companyCode && s.BranchCode == branchCode && masterCodes.Contains(s.SuppCode))
                .ToDictionaryAsync(s => s.SuppCode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        for (var i = 0; i < requestedSuppliers.Count; i++)
        {
            var src = requestedSuppliers[i];
            var code = TruncateOptional(src.SupplierCode, 20);
            PoSupplier? master = null;
            if (!string.IsNullOrWhiteSpace(code))
            {
                mastersByCode.TryGetValue(code, out master);
            }

            var name = TruncateOptional(src.SupplierName, 120) ?? master?.SuppName;
            if (string.IsNullOrWhiteSpace(name))
            {
                fieldErrors[$"Suppliers[{i}].SupplierName"] = "Supplier name is required.";
                continue;
            }

            if (src.SourcingSupplierId > 0 && existingById.TryGetValue(src.SourcingSupplierId, out var existing))
            {
                existing.SuppCode = master?.SuppCode ?? code;
                existing.SupplierName = name;
                existing.ContactPerson = TruncateOptional(src.ContactPerson, 100) ?? master?.ContactPerson;
                existing.ContactEmail = TruncateOptional(src.Email, 120) ?? master?.Email ?? master?.ContactEmail;
                existing.ContactPhone = TruncateOptional(src.Phone, 50) ?? master?.Tel ?? master?.ContactTelp;
                existing.DefaultCurrency = TruncateOptional(src.DefaultCurrency, 20) ?? master?.Currency ?? header.BaseCurrency;
                existing.Notes = TruncateOptional(src.Remarks, 300);
            }
            else
            {
                var sup = new PoSourcingSupplier
                {
                    CompanyCode = companyCode,
                    BranchCode = branchCode,
                    SourcingId = header.SourcingId,
                    SuppCode = master?.SuppCode ?? code,
                    SupplierName = name,
                    ContactPerson = TruncateOptional(src.ContactPerson, 100) ?? master?.ContactPerson,
                    ContactEmail = TruncateOptional(src.Email, 120) ?? master?.Email ?? master?.ContactEmail,
                    ContactPhone = TruncateOptional(src.Phone, 50) ?? master?.Tel ?? master?.ContactTelp,
                    DefaultCurrency = TruncateOptional(src.DefaultCurrency, 20) ?? master?.Currency ?? header.BaseCurrency,
                    ResponseStatus = PoSourcingSupplierResponseStatuses.Invited,
                    Notes = TruncateOptional(src.Remarks, 300),
                    CreatedDate = nowUtc,
                    CreatedBy = userId
                };
                db.PoSourcingSuppliers.Add(sup);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    private async Task RefreshComparisonSnapshotsAsync(
        AppDbContext db,
        PoSourcingHdr header,
        IReadOnlyList<PoSourcingLine> lines,
        CancellationToken cancellationToken)
    {
        var suppliers = await db.PoSourcingSuppliers
            .Where(s => s.CompanyCode == header.CompanyCode
                && s.BranchCode == header.BranchCode
                && s.SourcingId == header.SourcingId)
            .ToListAsync(cancellationToken);

        var quotes = await db.PoSourcingQuotes
            .Include(q => q.Lines)
            .Where(q => q.CompanyCode == header.CompanyCode
                && q.BranchCode == header.BranchCode
                && q.SourcingId == header.SourcingId)
            .ToListAsync(cancellationToken);

        var matrix = PoSourcingComparisonCalculator.Evaluate(header, lines, suppliers, quotes, _dates.Today.Date);
        var cellByQuoteLineId = matrix.Lines
            .SelectMany(l => l.Offers)
            .ToDictionary(c => c.QuoteLineId);

        foreach (var q in quotes)
        {
            foreach (var ql in q.Lines)
            {
                if (cellByQuoteLineId.TryGetValue(ql.QuoteLineId, out var cell))
                {
                    ql.IsComparable = cell.IsComparable;
                    ql.NonComparableReason = cell.NonComparableReason;
                    ql.NormalizedBaseUnitCost = cell.NormalizedBaseUnitCost;
                    ql.NormalizedComparableLandedTotal = cell.NormalizedComparableLandedTotal;
                    ql.NormalizedTaxInclusivePayableTotal = cell.NormalizedTaxInclusivePayableTotal;
                }
            }
        }
    }

    private async Task<PoSourcingOperationResult> LoadDocumentDtoAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long sourcingId,
        CancellationToken cancellationToken)
    {
        var header = await db.PoSourcingHdrs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.SourcingId == sourcingId, cancellationToken);
        if (header is null)
        {
            return PoSourcingOperationResult.Fail("Sourcing document was not found.", PoSourcingErrorKind.NotFound);
        }

        var lines = await db.PoSourcingLines.AsNoTracking()
            .Where(l => l.CompanyCode == companyCode && l.BranchCode == branchCode && l.SourcingId == sourcingId)
            .OrderBy(l => l.LineNo)
            .ToListAsync(cancellationToken);
        var lineNoById = lines.ToDictionary(l => l.SourcingLineId, l => l.LineNo);
        var lineById = lines.ToDictionary(l => l.SourcingLineId);

        var suppliers = await db.PoSourcingSuppliers.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode && s.BranchCode == branchCode && s.SourcingId == sourcingId)
            .OrderBy(s => s.SupplierName)
            .ToListAsync(cancellationToken);
        var supplierById = suppliers.ToDictionary(s => s.SourcingSupplierId);

        var quotes = await db.PoSourcingQuotes.AsNoTracking()
            .Include(q => q.Lines)
            .Where(q => q.CompanyCode == companyCode && q.BranchCode == branchCode && q.SourcingId == sourcingId)
            .OrderBy(q => q.SourcingSupplierId)
            .ThenByDescending(q => q.QuoteRevNo)
            .ToListAsync(cancellationToken);

        var awards = await db.PoSourcingAwards.AsNoTracking()
            .Include(a => a.Lines)
            .Where(a => a.CompanyCode == companyCode && a.BranchCode == branchCode && a.SourcingId == sourcingId)
            .OrderByDescending(a => a.AwardVersion)
            .ToListAsync(cancellationToken);

        var conversions = await db.PoSourcingConversions.AsNoTracking()
            .Include(c => c.Lines)
            .Where(c => c.CompanyCode == companyCode && c.BranchCode == branchCode && c.SourcingId == sourcingId)
            .OrderByDescending(c => c.ConversionId)
            .ToListAsync(cancellationToken);

        var attachments = await db.PoSourcingAttachments.AsNoTracking()
            .Where(a => a.CompanyCode == companyCode && a.BranchCode == branchCode && a.SourcingId == sourcingId)
            .OrderByDescending(a => a.UploadedAtUtc)
            .ToListAsync(cancellationToken);

        var events = await db.PoSourcingEvents.AsNoTracking()
            .Where(e => e.CompanyCode == companyCode && e.BranchCode == branchCode && e.SourcingId == sourcingId)
            .OrderByDescending(e => e.EventId)
            .ToListAsync(cancellationToken);

        var canEdit = await CanAsync(PermissionCodes.Edit, cancellationToken);
        var canCancel = await CanAsync(PermissionCodes.Cancel, cancellationToken);
        var canDelete = await CanAsync(PermissionCodes.Delete, cancellationToken);
        var canApprove = await CanAsync(PermissionCodes.Approve, cancellationToken);
        var canReject = await CanAsync(PermissionCodes.Reject, cancellationToken);
        var canViewCost = await CanAsync(PermissionCodes.ViewCost, cancellationToken);
        var canCreatePr = await _accessRights.CanAsync(MenuCodes.PurchaseRequisition, PermissionCodes.Add, cancellationToken);
        var canCreatePo = await _accessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Add, cancellationToken);

        var activeConvByAwardLine = conversions
            .SelectMany(c => c.Lines)
            .Where(cl => cl.LineLinkStatus == PoSourcingConversionStatuses.Active)
            .GroupBy(cl => cl.AwardLineId)
            .ToDictionary(
                g => g.Key,
                g => (PurQty: g.Sum(x => x.ConvertedPurQty), StdQty: g.Sum(x => x.EffectiveActiveStdQty)));

        var activeConvByRfqLine = conversions
            .SelectMany(c => c.Lines)
            .Where(cl => cl.LineLinkStatus == PoSourcingConversionStatuses.Active)
            .GroupBy(cl => cl.SourcingLineId)
            .ToDictionary(
                g => g.Key,
                g => (PurQty: g.Sum(x => x.ConvertedPurQty), StdQty: g.Sum(x => x.EffectiveActiveStdQty)));

        var approvedAward = awards.FirstOrDefault(a => a.Status == PoSourcingAwardStatuses.Approved);
        var awardedPurByRfqLine = approvedAward?.Lines
            .GroupBy(al => al.SourcingLineId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.AwardedQty))
            ?? new Dictionary<long, decimal>();

        var doc = new PoSourcingDocumentDto
        {
            SourcingId = header.SourcingId,
            SourcingNo = header.SourcingNo,
            SourceType = header.SourceType,
            Status = header.Status,
            RequestDate = header.RequestDate,
            ResponseDueDate = header.ReplyDeadline,
            RequiredEtaDate = header.RequiredDate,
            Title = header.Title,
            Requester = header.Requester,
            BuyerCode = header.BuyerCode,
            DepartmentCode = header.DeptCode,
            ProjectId = header.ProjectCode,
            BaseCurrency = header.BaseCurrency,
            DefaultToWarehouse = header.DeliveryWhse,
            Remarks = header.Remarks,
            CancelReason = header.CancelReason,
            PricingRedacted = !canViewCost,
            RowVersion = header.RowVersion ?? Array.Empty<byte>(),
            Lines = lines.Select(l => new PoSourcingLineDto
            {
                SourcingLineId = l.SourcingLineId,
                LineNo = l.LineNo,
                SourcePrNo = l.PrNo,
                SourcePrLine = l.PrLineNo,
                OneTimeItemYn = l.OneTimeItemYn,
                ItemCode = l.ICode,
                ItemDescription = l.Description,
                ItemSpec = l.Specification,
                RequestedPurchaseQty = l.RequestedQty,
                RequestedPurchaseUom = l.RequestedUom,
                PackSize = l.PackSz,
                StdUom = l.StdUom,
                RequestedStdQty = l.RequestedStdQty,
                AwardedPurchaseQty = awardedPurByRfqLine.GetValueOrDefault(l.SourcingLineId, 0m),
                ConvertedPurchaseQty = activeConvByRfqLine.GetValueOrDefault(l.SourcingLineId).PurQty,
                RequiredEtaDate = l.RequiredDate,
                ToWarehouse = l.DeliveryWhse,
                PreferredSupplierCode = l.PrOriginalVendorCd,
                TargetCurrency = header.BaseCurrency,
                Remarks = l.Notes
            }).ToList(),
            Suppliers = suppliers.Select(s => new PoSourcingSupplierDto
            {
                SourcingSupplierId = s.SourcingSupplierId,
                SupplierCode = s.SuppCode,
                SupplierName = s.SupplierName,
                IsAdHocSupplier = string.IsNullOrWhiteSpace(s.SuppCode),
                ContactPerson = s.ContactPerson,
                Email = s.ContactEmail,
                Phone = s.ContactPhone,
                DefaultCurrency = s.DefaultCurrency,
                ResponseStatus = s.ResponseStatus,
                InvitedDate = s.CreatedDate,
                Remarks = s.Notes
            }).ToList(),
            Quotes = quotes.Select(q =>
            {
                supplierById.TryGetValue(q.SourcingSupplierId, out var sup);
                var netTotal = q.Lines.Sum(ql => Math.Round(ql.QuotedQty * ql.UnitPrice, 2, MidpointRounding.AwayFromZero));
                var totalComparable = q.Lines.Sum(ql => ql.NormalizedComparableLandedTotal ?? 0m);

                return new PoSourcingQuoteDto
                {
                    QuoteId = q.QuoteId,
                    SourcingSupplierId = q.SourcingSupplierId,
                    SupplierCode = sup?.SuppCode,
                    SupplierName = sup?.SupplierName ?? string.Empty,
                    SupplierQuoteNo = q.SupplierQuoteRef,
                    QuoteRevNo = q.QuoteRevNo,
                    IsLatestRevision = q.Status != PoSourcingQuoteStatuses.Superseded,
                    QuoteDate = q.QuoteDate,
                    ValidUntilDate = q.ExpiryDate,
                    Status = q.Status,
                    Currency = q.Currency,
                    ExchangeRate = q.ExchangeRateToBase ?? 1m,
                    PaymentTerm = q.PayTermCode,
                    BuyingTerm = q.BuyingTermCode,
                    IsInclusive = q.IsInclusive,
                    FreightAmount = canViewCost ? q.HeaderFreightAmount : null,
                    OtherChargesAmount = canViewCost ? q.HeaderOtherCharges : null,
                    FreightAllocationMode = q.FreightAllocationMode,
                    NetAmount = canViewCost ? netTotal : null,
                    TotalQuoteAmount = canViewCost ? netTotal + q.HeaderFreightAmount + q.HeaderOtherCharges : null,
                    TotalComparableBaseAmount = canViewCost ? totalComparable : null,
                    Remarks = q.BuyerNotes,
                    RowVersion = q.RowVersion ?? Array.Empty<byte>(),
                    Lines = q.Lines
                        .OrderBy(ql => lineNoById.GetValueOrDefault(ql.SourcingLineId, (short)999))
                        .Select(ql =>
                        {
                            lineById.TryGetValue(ql.SourcingLineId, out var rfqLine);
                            var lineAmt = Math.Round(ql.QuotedQty * ql.UnitPrice, 2, MidpointRounding.AwayFromZero);
                            return new PoSourcingQuoteLineDto
                            {
                                QuoteLineId = ql.QuoteLineId,
                                SourcingLineId = ql.SourcingLineId,
                                SourcingLineNo = rfqLine?.LineNo ?? 0,
                                ItemCode = rfqLine?.ICode,
                                ItemDescription = rfqLine?.Description ?? string.Empty,
                                LineStatus = ql.LineStatus,
                                VendorPartNo = ql.VendorPartNo,
                                AlternateSpec = ql.OfferedSpec,
                                OfferedQty = ql.QuotedQty,
                                QuoteUom = ql.QuotedUom,
                                PackSize = ql.QuotedPackSz,
                                StdUom = rfqLine?.StdUom ?? ql.QuotedUom,
                                OfferedStdQty = ql.QuotedStdQty,
                                QuotedUnitPrice = canViewCost ? ql.UnitPrice : null,
                                DiscountType1 = ql.DiscPer > 0m ? "%" : null,
                                ItemDiscount1 = canViewCost ? (ql.DiscPer > 0m ? ql.DiscPer : ql.DiscAmt) : 0m,
                                TaxGroup = ql.TaxGroupCode,
                                TaxPercent = ql.TaxPer,
                                NonRecoverableTaxPercent = ql.NonRecoverableTaxPer,
                                IsInclusive = q.IsInclusive,
                                LineAmount = canViewCost ? lineAmt : null,
                                NetAmount = canViewCost ? lineAmt : null,
                                ComparableUnitCostBase = canViewCost ? ql.NormalizedBaseUnitCost : null,
                                ComparableLandedCostBase = canViewCost ? ql.NormalizedComparableLandedTotal : null,
                                Moq = ql.Moq,
                                LeadTimeDays = ql.LeadTimeDays,
                                PromisedEtaDate = ql.EstimatedDeliveryDate,
                                Remarks = ql.ExceptionsNote
                            };
                        }).ToList()
                };
            }).ToList(),
            Awards = awards.Select(a => new PoSourcingAwardDto
            {
                AwardId = a.AwardId,
                AwardNo = (short)a.AwardVersion,
                Status = a.Status,
                AwardDate = a.DecidedAtUtc ?? a.ProposedAtUtc,
                ProposedBy = a.ProposedBy,
                ProposedDate = a.ProposedAtUtc,
                ApprovedBy = a.Status == PoSourcingAwardStatuses.Approved ? a.DecidedBy : null,
                ApprovedDate = a.Status == PoSourcingAwardStatuses.Approved ? a.DecidedAtUtc : null,
                RejectedBy = a.Status == PoSourcingAwardStatuses.Rejected ? a.DecidedBy : null,
                RejectedDate = a.Status == PoSourcingAwardStatuses.Rejected ? a.DecidedAtUtc : null,
                SummaryRemarks = a.OverallReason,
                RejectionReason = a.Status == PoSourcingAwardStatuses.Rejected ? a.DecisionReason : null,
                RowVersion = a.RowVersion ?? Array.Empty<byte>(),
                Lines = a.Lines
                    .OrderBy(al => lineNoById.GetValueOrDefault(al.SourcingLineId, (short)999))
                    .Select(al =>
                    {
                        lineById.TryGetValue(al.SourcingLineId, out var rfqLine);
                        var convTotals = activeConvByAwardLine.GetValueOrDefault(al.AwardLineId);
                        return new PoSourcingAwardLineDto
                        {
                            AwardLineId = al.AwardLineId,
                            SourcingLineId = al.SourcingLineId,
                            SourcingLineNo = rfqLine?.LineNo ?? 0,
                            QuoteId = al.QuoteId,
                            QuoteLineId = al.QuoteLineId,
                            AwardedPurchaseQty = al.AwardedQty,
                            AwardedStdQty = al.AwardedStdQty,
                            ConvertedPurchaseQty = convTotals.PurQty,
                            ConvertedStdQty = convTotals.StdQty,
                            SnapshotSupplierCode = al.SuppCodeSnapshot,
                            SnapshotSupplierName = al.SupplierNameSnapshot,
                            SnapshotItemCode = al.ICodeSnapshot,
                            SnapshotItemDesc = rfqLine?.Description ?? string.Empty,
                            SnapshotPurchaseUom = al.AwardedUom,
                            SnapshotPackSize = al.PackSzSnapshot,
                            SnapshotStdUom = al.StdUomSnapshot,
                            SnapshotCurrency = al.CurrencySnapshot,
                            SnapshotExchangeRate = al.ExchangeRateSnapshot,
                            SnapshotUnitPrice = canViewCost ? al.AwardedUnitPrice : null,
                            SnapshotTaxGroup = al.TaxGroupSnapshot,
                            SnapshotTaxPercent = al.TaxPerSnapshot,
                            SnapshotIsInclusive = al.IsInclusiveSnapshot,
                            SnapshotComparableUnitCostBase = canViewCost ? al.ComparableBaseUnitCostSnapshot : null,
                            SnapshotComparableLandedCostBase = canViewCost ? al.ComparableTotalSnapshot : null,
                            IsLowestCostSelection = al.IsLowestComparableOffer,
                            SelectionReasonCode = al.SelectionReason,
                            SelectionRemarks = al.SelectionReason,
                            ExceptionReason = al.QuarantineReason
                        };
                    }).ToList()
            }).ToList(),
            Conversions = conversions.Select(c => new PoSourcingConversionDto
            {
                ConversionId = c.ConversionId,
                AwardId = c.AwardId,
                TargetType = c.TargetDocType,
                TargetDocNo = c.TargetDocNo,
                TargetRelNo = c.TargetDocRelNo,
                Status = c.Status,
                StatusReason = c.ReconciliationNote,
                CreatedBy = c.CreatedBy,
                CreatedDate = c.CreatedAtUtc,
                Lines = c.Lines.OrderBy(cl => cl.TargetLineNo).Select(cl => new PoSourcingConversionLineDto
                {
                    ConversionLineId = cl.ConversionLineId,
                    AwardLineId = cl.AwardLineId,
                    SourcingLineId = cl.SourcingLineId,
                    TargetLineNo = cl.TargetLineNo,
                    ConvertedPurchaseQty = cl.ConvertedPurQty,
                    ConvertedStdQty = cl.EffectiveActiveStdQty,
                    SnapshotUnitPrice = canViewCost ? cl.TargetUnitPrice : null,
                    Status = cl.LineLinkStatus
                }).ToList()
            }).ToList(),
            Attachments = attachments.Select(att => new PoSourcingAttachmentDto
            {
                AttachmentId = att.AttachmentId,
                ScopeType = att.ScopeKind,
                QuoteId = att.QuoteId,
                FileName = att.OriginalFileName,
                ContentType = att.ContentType,
                FileSizeBytes = att.FileSizeBytes,
                ContainsSensitivePricing = att.ContainsSensitivePricing,
                UploadedBy = att.UploadedBy,
                UploadedDate = att.UploadedAtUtc
            }).ToList(),
            Events = events.Select(ev => new PoSourcingEventDto
            {
                EventId = ev.EventId,
                EventType = ev.EventType,
                EventSummary = ev.Reason ?? ev.EventType,
                ActorUserId = ev.ActorUserId,
                EventDateUtc = ev.OccurredAtUtc
            }).ToList(),
            CanEdit = canEdit && (header.Status == PoSourcingStatuses.Draft || header.Status == PoSourcingStatuses.CollectingQuotes || header.Status == PoSourcingStatuses.ReadyToCompare),
            CanCancel = canCancel && header.Status != PoSourcingStatuses.Cancelled && header.Status != PoSourcingStatuses.Converted && conversions.All(c => c.Status != PoSourcingConversionStatuses.Active),
            CanDelete = canDelete && header.Status == PoSourcingStatuses.Draft && quotes.Count == 0 && awards.Count == 0 && conversions.Count == 0,
            CanApprove = canApprove,
            CanReject = canReject,
            CanViewCost = canViewCost,
            CanCreatePr = canCreatePr && canEdit,
            CanCreatePo = canCreatePo && canEdit
        };

        return PoSourcingOperationResult.OkDocument(doc);
    }

    private static async Task<Dictionary<long, (decimal PurQty, decimal StdQty)>> GetActiveConvertedQtyByAwardLineAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long sourcingId,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? excludeRequestIds = null)
    {
        HashSet<long>? excludeConversionIds = null;
        if (excludeRequestIds is { Count: > 0 })
        {
            var matchedIds = await db.PoSourcingConversions.AsNoTracking()
                .Where(c => c.CompanyCode == companyCode
                    && c.BranchCode == branchCode
                    && c.SourcingId == sourcingId
                    && excludeRequestIds.Contains(c.RequestId))
                .Select(c => c.ConversionId)
                .ToListAsync(cancellationToken);
            if (matchedIds.Count > 0)
            {
                excludeConversionIds = new HashSet<long>(matchedIds);
            }
        }

        var query = db.PoSourcingConversionLines.AsNoTracking()
            .Where(cl => cl.CompanyCode == companyCode
                && cl.BranchCode == branchCode
                && cl.SourcingId == sourcingId
                && cl.LineLinkStatus == PoSourcingConversionStatuses.Active);

        if (excludeConversionIds is { Count: > 0 })
        {
            query = query.Where(cl => !excludeConversionIds.Contains(cl.ConversionId));
        }

        var rows = await query
            .GroupBy(cl => cl.AwardLineId)
            .Select(g => new
            {
                AwardLineId = g.Key,
                PurQty = g.Sum(x => x.ConvertedPurQty),
                StdQty = g.Sum(x => x.EffectiveActiveStdQty)
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(x => x.AwardLineId, x => (x.PurQty, x.StdQty));
    }

    private static async Task<Dictionary<long, (decimal PurQty, decimal StdQty)>> GetActiveConvertedQtyByRfqLineAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long sourcingId,
        CancellationToken cancellationToken)
    {
        var rows = await db.PoSourcingConversionLines.AsNoTracking()
            .Where(cl => cl.CompanyCode == companyCode
                && cl.BranchCode == branchCode
                && cl.SourcingId == sourcingId
                && cl.LineLinkStatus == PoSourcingConversionStatuses.Active)
            .GroupBy(cl => cl.SourcingLineId)
            .Select(g => new
            {
                SourcingLineId = g.Key,
                PurQty = g.Sum(x => x.ConvertedPurQty),
                StdQty = g.Sum(x => x.EffectiveActiveStdQty)
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(x => x.SourcingLineId, x => (x.PurQty, x.StdQty));
    }

    private static PoSourcingComparisonMatrix RedactComparisonMatrix(PoSourcingComparisonMatrix src)
    {
        return src with
        {
            Lines = src.Lines.Select(l => l with
            {
                LowestComparableTotal = null,
                LowestNormalizedBaseUnitCost = null,
                Offers = l.Offers.Select(o => o with
                {
                    UnitPrice = 0m,
                    DiscPer = 0m,
                    DiscAmt = 0m,
                    LineCharges = 0m,
                    AllocatedHeaderChargesQuoteCurr = 0m,
                    GoodsValueBase = 0m,
                    NormalizedBaseUnitCost = 0m,
                    NormalizedComparableLandedTotal = 0m,
                    NormalizedTaxInclusivePayableTotal = 0m
                }).ToList()
            }).ToList()
        };
    }

    private async Task<PoSourcingHdr?> LockSourcingHeaderAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long sourcingId,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM dbo.PoSourcingHdr WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode = {companyCode} AND BranchCode = {branchCode} AND SourcingId = {sourcingId}",
                cancellationToken);
        }

        return await db.PoSourcingHdrs
            .FirstOrDefaultAsync(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.SourcingId == sourcingId, cancellationToken);
    }

    private async Task<string> IssueSourcingNumberAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        DateTime requestDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var issued = await _documentNumbers.NextAsync(
                db,
                "RFQ",
                "",
                requestDate,
                DocumentNumberRequestMode.New,
                "AUTO",
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(issued.DocumentNumber))
            {
                return issued.DocumentNumber;
            }
        }
        catch
        {
            // Fallback to deterministic sequential RFQ number when AdDocNo has no "RFQ" row configured.
        }

        var prefix = $"RFQ-{requestDate:yyyyMM}-";
        var existingCount = await db.PoSourcingHdrs
            .CountAsync(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.SourcingNo.StartsWith(prefix), cancellationToken);
        return $"{prefix}{existingCount + 1:D4}";
    }

    private static async Task<string> ResolveDefaultCurrencyAsync(
        AppDbContext db,
        string companyCode,
        CancellationToken cancellationToken)
    {
        var curr = await db.SaCurrencies.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && (x.IsActive == null || x.IsActive == true))
            .OrderBy(x => x.CurrCode)
            .Select(x => x.CurrCode)
            .FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(curr) ? "MYR" : curr.Trim();
    }

    private static Guid DeriveGroupRequestId(Guid baseRequestId, string groupKey)
    {
        var bytes = Encoding.UTF8.GetBytes($"{baseRequestId:N}|{groupKey}");
        var hash = SHA256.HashData(bytes);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string ComputePayloadHash(
        string targetType,
        long sourcingId,
        long awardId,
        string groupKey,
        IEnumerable<(long AwardLineId, decimal ConvertQty, decimal UnitPrice)> lines)
    {
        var sb = new StringBuilder();
        sb.Append(targetType).Append('|')
          .Append(sourcingId).Append('|')
          .Append(awardId).Append('|')
          .Append(groupKey);
        foreach (var l in lines.OrderBy(x => x.AwardLineId))
        {
            sb.Append('|')
              .Append(l.AwardLineId).Append(':')
              .Append(l.ConvertQty.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
              .Append(l.UnitPrice.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private string ResolveAttachmentRoot()
    {
        if (!string.IsNullOrWhiteSpace(_attachmentOptions.RootPath))
        {
            return Path.GetFullPath(_attachmentOptions.RootPath);
        }

        return Path.Combine(Path.GetTempPath(), "ErpWebAttachments");
    }

    private static void TouchRowVersion<TEntity>(AppDbContext db, TEntity entity)
        where TEntity : class
    {
        if (!db.Database.IsSqlServer())
        {
            var prop = db.Entry(entity).Property<byte[]?>("RowVersion");
            prop.CurrentValue = Guid.NewGuid().ToByteArray();
        }
    }

    private static bool RowVersionsEqual(byte[]? left, byte[]? right) =>
        left is not null && right is not null && left.SequenceEqual(right);

    private static PoSourcingOperationResult FeatureDisabledResult() =>
        PoSourcingOperationResult.Fail(
            "Purchase Sourcing (RFQ) is disabled for this company.",
            PoSourcingErrorKind.FeatureDisabled);

    private Task<bool> CanAsync(string permissionCode, CancellationToken cancellationToken) =>
        _accessRights.CanAsync(MenuCodes.PurchaseSourcing, permissionCode, cancellationToken);

    private UserContext ValidateUserContext()
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.CompanyCode) || string.IsNullOrWhiteSpace(scope.BranchCode) || string.IsNullOrWhiteSpace(scope.UserId))
        {
            return UserContext.Fail("Active company, branch, and user context are required.");
        }

        return UserContext.Ok(scope.CompanyCode.Trim(), scope.BranchCode.Trim(), scope.UserId.Trim());
    }

    private UserContext ValidateWriteContext() => ValidateUserContext();

    private static string Truncate(string value, int maxLength)
    {
        var v = (value ?? string.Empty).Trim();
        return v.Length <= maxLength ? v : v[..maxLength];
    }

    private static string? TruncateOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private readonly record struct UserContext(string? Error, string? CompanyCode, string? BranchCode, string? UserId)
    {
        public static UserContext Fail(string error) => new(error, null, null, null);
        public static UserContext Ok(string company, string branch, string user) => new(null, company, branch, user);
    }
}
