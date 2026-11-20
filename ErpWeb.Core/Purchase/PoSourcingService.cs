using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Purchase;

public sealed partial class PoSourcingService : IPoSourcingService
{
    public const int MaxAttachmentBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> AllowedAttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".docx", ".xlsx", ".txt", ".csv"
    };

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IPurchaseSourcingFeatureGuard _featureGuard;
    private readonly IDocumentNumberingService _documentNumbers;
    private readonly ICurrentDateService _dates;
    private readonly PoPrService _prService;
    private readonly PoOrderService _poService;
    private readonly AttachmentStorageOptions _attachmentOptions;
    private readonly ILogger<PoSourcingService> _logger;

    public PoSourcingService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IPurchaseSourcingFeatureGuard featureGuard,
        IDocumentNumberingService documentNumbers,
        ICurrentDateService dates,
        PoPrService prService,
        PoOrderService poService,
        IOptions<AttachmentStorageOptions>? attachmentOptions = null,
        ILogger<PoSourcingService>? logger = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _accessRights = accessRights ?? throw new ArgumentNullException(nameof(accessRights));
        _featureGuard = featureGuard ?? throw new ArgumentNullException(nameof(featureGuard));
        _documentNumbers = documentNumbers ?? throw new ArgumentNullException(nameof(documentNumbers));
        _dates = dates ?? throw new ArgumentNullException(nameof(dates));
        _prService = prService ?? throw new ArgumentNullException(nameof(prService));
        _poService = poService ?? throw new ArgumentNullException(nameof(poService));
        _attachmentOptions = attachmentOptions?.Value ?? new AttachmentStorageOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PoSourcingService>.Instance;
    }

    public async Task<PoSourcingOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default)
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

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = context.CompanyCode!;
        var branch = context.BranchCode!;

        var currencies = await db.SaCurrencies.AsNoTracking()
            .Where(x => x.CompanyCode == company && (x.IsActive == null || x.IsActive == true))
            .OrderBy(x => x.CurrCode)
            .Select(x => new PoOrderCodeLookupRow { Code = x.CurrCode, Name = x.CurrDesc })
            .ToListAsync(cancellationToken);

        var baseCurrency = currencies.FirstOrDefault()?.Code ?? "MYR";

        var taxGroups = await db.SaTaxGroups.AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .OrderBy(x => x.TaxGrCode)
            .Select(x => new PoOrderTaxGroupLookupRow
            {
                TaxGrCode = x.TaxGrCode,
                TaxGrDesc = x.TaxGrDesc,
                Percentage = x.Percentage
            })
            .ToListAsync(cancellationToken);

        var buyingTerms = await db.PoBuyingTerms.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive)
            .OrderBy(x => x.BuyingTerm)
            .Select(x => new PoOrderCodeLookupRow { Code = x.BuyingTerm, Name = x.Description })
            .ToListAsync(cancellationToken);

        var paymentTerms = await db.IvMsCodes.AsNoTracking()
            .Where(x => x.CodeType == "PAYCODE")
            .OrderBy(x => x.Code)
            .Select(x => new PoOrderCodeLookupRow { Code = x.Code, Name = x.Name })
            .ToListAsync(cancellationToken);

        var buyers = await db.PoBuyers.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive)
            .OrderBy(x => x.BuyerCode)
            .Select(x => new PoOrderCodeLookupRow { Code = x.BuyerCode, Name = x.BuyerName ?? x.BuyerDesc })
            .ToListAsync(cancellationToken);

        var warehouses = await db.IvWarehouses.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.IsActive)
            .OrderBy(x => x.WarehouseCode)
            .Select(x => new IvWarehouseLookupRow
            {
                WarehouseCode = x.WarehouseCode,
                WarehouseDesc = x.WarehouseDesc
            })
            .ToListAsync(cancellationToken);

        var departments = await db.MsDepts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.IsActive)
            .OrderBy(x => x.DeptCode)
            .Select(x => new PoOrderCodeLookupRow { Code = x.DeptCode, Name = x.DeptName })
            .ToListAsync(cancellationToken);

        var projects = await db.MsProjects.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.Status == MsProjectStatus.Active)
            .OrderBy(x => x.ProjCode)
            .Select(x => new PoOrderCodeLookupRow { Code = x.ProjCode, Name = x.ProjName })
            .ToListAsync(cancellationToken);

        return PoSourcingOperationResult.OkLookups(new PoSourcingLookupsDto
        {
            BaseCurrency = baseCurrency,
            Currencies = currencies,
            TaxGroups = taxGroups,
            BuyingTerms = buyingTerms,
            PaymentTerms = paymentTerms,
            Buyers = buyers,
            Warehouses = warehouses,
            Departments = departments,
            Projects = projects,
            CanAdd = await CanAsync(PermissionCodes.Add, cancellationToken),
            CanEdit = await CanAsync(PermissionCodes.Edit, cancellationToken),
            CanDelete = await CanAsync(PermissionCodes.Delete, cancellationToken),
            CanCancel = await CanAsync(PermissionCodes.Cancel, cancellationToken),
            CanApprove = await CanAsync(PermissionCodes.Approve, cancellationToken),
            CanReject = await CanAsync(PermissionCodes.Reject, cancellationToken),
            CanViewCost = await CanAsync(PermissionCodes.ViewCost, cancellationToken),
            CanCreatePr = await _accessRights.CanAsync(MenuCodes.PurchaseRequisition, PermissionCodes.Add, cancellationToken),
            CanCreatePo = await _accessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Add, cancellationToken)
        });
    }

    public async Task<PoSourcingOperationResult> SearchAsync(
        PoSourcingSearchFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
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

        var canEdit = await CanAsync(PermissionCodes.Edit, cancellationToken);
        var canCancel = await CanAsync(PermissionCodes.Cancel, cancellationToken);
        var canDelete = await CanAsync(PermissionCodes.Delete, cancellationToken);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.PoSourcingHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode && x.BranchCode == context.BranchCode);

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = filter.Status.Trim();
            query = query.Where(x => x.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.SourceType))
        {
            var st = NormalizeSourceType(filter.SourceType);
            query = query.Where(x => x.SourceType == st);
        }

        if (!string.IsNullOrWhiteSpace(filter.BuyerCode))
        {
            var buyer = filter.BuyerCode.Trim();
            query = query.Where(x => x.BuyerCode == buyer);
        }

        if (filter.FromDate is DateTime fromDate)
        {
            var fd = fromDate.Date;
            query = query.Where(x => x.RequestDate >= fd);
        }

        if (filter.ToDate is DateTime toDate)
        {
            var td = toDate.Date;
            query = query.Where(x => x.RequestDate <= td);
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var s = filter.SearchText.Trim();
            query = query.Where(x =>
                x.SourcingNo.Contains(s) ||
                x.Title.Contains(s) ||
                (x.Requester != null && x.Requester.Contains(s)) ||
                (x.BuyerCode != null && x.BuyerCode.Contains(s)) ||
                x.Lines.Any(l => (l.ICode != null && l.ICode.Contains(s)) || l.Description.Contains(s) || (l.PrNo != null && l.PrNo.Contains(s))));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var pageSize = Math.Clamp(filter.PageSize <= 0 ? 50 : filter.PageSize, 1, 200);
        var pageIndex = Math.Max(0, filter.PageIndex);

        var rows = await query
            .OrderByDescending(x => x.RequestDate)
            .ThenByDescending(x => x.SourcingId)
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .Select(x => new PoSourcingListRowDto
            {
                SourcingId = x.SourcingId,
                SourcingNo = x.SourcingNo,
                RequestDate = x.RequestDate,
                ResponseDueDate = x.ReplyDeadline,
                SourceType = x.SourceType,
                Status = x.Status,
                Title = x.Title,
                BuyerCode = x.BuyerCode,
                DepartmentCode = x.DeptCode,
                ProjectId = x.ProjectCode,
                BaseCurrency = x.BaseCurrency,
                LineCount = x.Lines.Count,
                SupplierCount = x.Suppliers.Count,
                ActiveQuoteCount = x.Quotes.Count(q => q.Status == PoSourcingQuoteStatuses.Received),
                CreatedBy = x.CreatedBy,
                CreatedDate = x.CreatedDate,
                RowVersion = x.RowVersion ?? Array.Empty<byte>(),
                CanEdit = canEdit && (x.Status == PoSourcingStatuses.Draft || x.Status == PoSourcingStatuses.CollectingQuotes || x.Status == PoSourcingStatuses.ReadyToCompare),
                CanCancel = canCancel && x.Status != PoSourcingStatuses.Cancelled && x.Status != PoSourcingStatuses.Converted && !x.Conversions.Any(c => c.Status == PoSourcingConversionStatuses.Active),
                CanDelete = canDelete && x.Status == PoSourcingStatuses.Draft && !x.Quotes.Any() && !x.Awards.Any() && !x.Conversions.Any()
            })
            .ToListAsync(cancellationToken);

        return PoSourcingOperationResult.OkPage(new PoSourcingSearchPageDto
        {
            Rows = rows,
            TotalCount = totalCount
        });
    }

    public async Task<PoSourcingOperationResult> GetAsync(
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

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await LoadDocumentDtoAsync(db, context.CompanyCode!, context.BranchCode!, sourcingId, cancellationToken);
    }

    public async Task<PoSourcingOperationResult> GetEligiblePrLinesAsync(
        string? prNo,
        string? search,
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

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = context.CompanyCode!;
        var branch = context.BranchCode!;

        var prQuery = db.PoPrDetails.AsNoTracking()
            .Include(d => d.Pr)
            .Where(d => d.CompanyCode == company
                && d.BranchCode == branch
                && d.Pr != null
                && (d.Pr.Status == PoPrStatuses.New || d.Pr.Status == PoPrStatuses.Open || d.Pr.Status == PoPrStatuses.Approved || d.Pr.Status == PoPrStatuses.PartiallyOrdered)
                && (d.Status == null || d.Status != PoPrStatuses.Cancelled));

        if (!string.IsNullOrWhiteSpace(prNo))
        {
            var prFilter = prNo.Trim();
            prQuery = prQuery.Where(d => d.PrNo == prFilter);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            prQuery = prQuery.Where(d =>
                d.PrNo.Contains(s) ||
                (d.ICode != null && d.ICode.Contains(s)) ||
                (d.IDesc != null && d.IDesc.Contains(s)));
        }

        var details = await prQuery
            .OrderByDescending(d => d.Pr!.CreateDt)
            .ThenBy(d => d.PrNo)
            .ThenBy(d => d.Line)
            .Take(200)
            .ToListAsync(cancellationToken);

        if (details.Count == 0)
        {
            return PoSourcingOperationResult.OkEligiblePrLines(Array.Empty<PoSourcingEligiblePrLineDto>());
        }

        var prNos = details.Select(d => d.PrNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var poConsumedByPrLine = await db.PoOrderDetails.AsNoTracking()
            .Where(d => d.CompanyCode == company
                && d.BranchCode == branch
                && d.PrNo != null
                && prNos.Contains(d.PrNo)
                && d.PrLineNo != null
                && d.Order != null
                && d.Order.Status != PoOrderStatuses.Cancelled)
            .GroupBy(d => new { PrNo = d.PrNo!, PrLine = d.PrLineNo!.Value })
            .Select(g => new { g.Key.PrNo, g.Key.PrLine, ConsumedQty = g.Sum(x => x.PoPurQty) })
            .ToListAsync(cancellationToken);

        var poConsumedMap = poConsumedByPrLine.ToDictionary(
            x => (x.PrNo, x.PrLine),
            x => x.ConsumedQty);

        var sourcingCommittedByPrLine = await db.PoSourcingLines.AsNoTracking()
            .Where(l => l.CompanyCode == company
                && l.BranchCode == branch
                && l.PrNo != null
                && prNos.Contains(l.PrNo)
                && l.PrLineNo != null
                && l.Sourcing != null
                && l.Sourcing.Status != PoSourcingStatuses.Cancelled
                && l.Sourcing.Status != PoSourcingStatuses.Converted)
            .GroupBy(l => new { PrNo = l.PrNo!, PrLine = l.PrLineNo!.Value })
            .Select(g => new
            {
                g.Key.PrNo,
                g.Key.PrLine,
                OpenSourcingQty = g.Sum(x => x.RequestedQty)
            })
            .ToListAsync(cancellationToken);

        var sourcingMap = sourcingCommittedByPrLine.ToDictionary(
            x => (x.PrNo, x.PrLine),
            x => x.OpenSourcingQty);

        var eligible = new List<PoSourcingEligiblePrLineDto>();
        foreach (var d in details)
        {
            poConsumedMap.TryGetValue((d.PrNo, d.Line), out var poConsumed);
            sourcingMap.TryGetValue((d.PrNo, d.Line), out var sourcingOpen);
            var remaining = Math.Round(d.PurchaseQty - poConsumed - sourcingOpen, 4, MidpointRounding.AwayFromZero);
            if (remaining <= 0m)
            {
                continue;
            }

            eligible.Add(new PoSourcingEligiblePrLineDto
            {
                PrNo = d.PrNo,
                PrLine = d.Line,
                PrDate = d.Pr!.CreateDt,
                Requester = d.Pr.Requester,
                DeptCode = d.Pr.DeptCode,
                ProjId = d.Pr.ProjId,
                ICode = d.ICode,
                IDesc = d.IDesc,
                Category = d.Category,
                OneTimeItemYn = d.OneTimeItemYn,
                PrPurchaseQty = d.PurchaseQty,
                RemainingPurchaseQty = remaining,
                PurchaseUom = d.PurchaseUom ?? d.StdUom ?? "EA",
                PackSz = d.PackSz <= 0m ? 1m : d.PackSz,
                StdUom = d.StdUom ?? d.PurchaseUom ?? "EA",
                Currency = d.Currency,
                UnitPrice = d.UnitPrice,
                VendorCd = d.VendorCd,
                VendNm = d.VendNm,
                TaxGroup = d.TaxGroup,
                IsInclusive = d.IsInclusive,
                ToWarehouse = d.ToWarehouse,
                EtaDt = d.EtaDt,
                WorkOrderMaterialId = d.WorkOrderMaterialId
            });
        }

        return PoSourcingOperationResult.OkEligiblePrLines(eligible);
    }

    public async Task<PoSourcingOperationResult> SaveDraftAsync(
        PoSourcingSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return PoSourcingOperationResult.FailValidation("Save request is required.");
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

        var isNew = request.SourcingId is null || request.SourcingId <= 0;
        var requiredPerm = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        if (!await CanAsync(requiredPerm, cancellationToken))
        {
            return PoSourcingOperationResult.Fail("Not authorized.", PoSourcingErrorKind.Authorization);
        }

        var fieldErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            fieldErrors["Title"] = "Title is required.";
        }

        var sourceType = NormalizeSourceType(request.SourceType);

        if (request.Lines is null || request.Lines.Count == 0)
        {
            fieldErrors["Lines"] = "At least one RFQ line is required.";
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

            PoSourcingHdr header;
            if (isNew)
            {
                var reqDate = request.RequestDate == default ? _dates.Today.Date : request.RequestDate.Date;
                var sourcingNo = await IssueSourcingNumberAsync(db, company, branch, reqDate, cancellationToken);
                var baseCurr = TruncateOptional(request.BaseCurrency, 20)
                    ?? await ResolveDefaultCurrencyAsync(db, company, cancellationToken);

                header = new PoSourcingHdr
                {
                    CompanyCode = company,
                    BranchCode = branch,
                    SourcingNo = sourcingNo,
                    SourceType = sourceType,
                    Status = request.MarkAsOpen ? PoSourcingStatuses.CollectingQuotes : PoSourcingStatuses.Draft,
                    RequestDate = reqDate,
                    ReplyDeadline = request.ResponseDueDate?.Date,
                    RequiredDate = request.RequiredEtaDate?.Date,
                    Title = Truncate(title, 200),
                    Requester = TruncateOptional(request.Requester, 50) ?? userId,
                    BuyerCode = TruncateOptional(request.BuyerCode, 20),
                    DeptCode = TruncateOptional(request.DepartmentCode, 20),
                    ProjectCode = TruncateOptional(request.ProjectId, 20),
                    BaseCurrency = baseCurr,
                    DeliveryWhse = TruncateOptional(request.DefaultToWarehouse, 20),
                    Remarks = TruncateOptional(request.Remarks, 500),
                    CreatedBy = userId,
                    CreatedDate = nowUtc
                };
                TouchRowVersion(db, header);
                db.PoSourcingHdrs.Add(header);
                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                header = await LockSourcingHeaderAsync(db, company, branch, request.SourcingId!.Value, cancellationToken)
                    ?? throw new InvalidOperationException("NOT_FOUND");

                if (request.RowVersion is null || request.RowVersion.Length == 0 || !RowVersionsEqual(header.RowVersion, request.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoSourcingOperationResult.Fail(
                        "This Sourcing document was changed by another user. Reload before saving.",
                        PoSourcingErrorKind.Concurrency);
                }

                if (header.Status != PoSourcingStatuses.Draft
                    && header.Status != PoSourcingStatuses.CollectingQuotes
                    && header.Status != PoSourcingStatuses.ReadyToCompare)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoSourcingOperationResult.Fail(
                        $"Sourcing document in status '{header.Status}' cannot be modified.",
                        PoSourcingErrorKind.BusinessRule);
                }

                header.RequestDate = request.RequestDate == default ? header.RequestDate : request.RequestDate.Date;
                header.ReplyDeadline = request.ResponseDueDate?.Date;
                header.RequiredDate = request.RequiredEtaDate?.Date;
                header.Title = Truncate(title, 200);
                header.Requester = TruncateOptional(request.Requester, 50) ?? header.Requester;
                header.BuyerCode = TruncateOptional(request.BuyerCode, 20);
                header.DeptCode = TruncateOptional(request.DepartmentCode, 20);
                header.ProjectCode = TruncateOptional(request.ProjectId, 20);
                if (!string.IsNullOrWhiteSpace(request.BaseCurrency))
                {
                    header.BaseCurrency = Truncate(request.BaseCurrency.Trim(), 20);
                }
                header.DeliveryWhse = TruncateOptional(request.DefaultToWarehouse, 20);
                header.Remarks = TruncateOptional(request.Remarks, 500);
                if (request.MarkAsOpen && header.Status == PoSourcingStatuses.Draft)
                {
                    header.Status = PoSourcingStatuses.CollectingQuotes;
                }
                header.ModifiedBy = userId;
                header.ModifiedDate = nowUtc;
                TouchRowVersion(db, header);
            }

            var lineValidationError = await SynchronizeSourcingLinesAsync(
                db, company, branch, header, request.Lines!, fieldErrors, cancellationToken);
            if (lineValidationError is not null || fieldErrors.Count > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return fieldErrors.Count > 0
                    ? PoSourcingOperationResult.FailValidation(lineValidationError ?? "Line validation failed.", fieldErrors)
                    : PoSourcingOperationResult.Fail(lineValidationError!);
            }

            var supplierError = await SynchronizeSourcingSuppliersAsync(
                db, company, branch, header, request.Suppliers ?? new List<PoSourcingSupplierDto>(), userId, nowUtc, fieldErrors, cancellationToken);
            if (supplierError is not null || fieldErrors.Count > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return fieldErrors.Count > 0
                    ? PoSourcingOperationResult.FailValidation(supplierError ?? "Supplier validation failed.", fieldErrors)
                    : PoSourcingOperationResult.Fail(supplierError!);
            }

            db.PoSourcingEvents.Add(new PoSourcingEvent
            {
                CompanyCode = company,
                BranchCode = branch,
                SourcingId = header.SourcingId,
                EventType = isNew ? PoSourcingEventTypes.Created : PoSourcingEventTypes.Updated,
                NewStatus = header.Status,
                Reason = isNew
                    ? $"Created Sourcing RFQ {header.SourcingNo} ({header.SourceType}) with {request.Lines!.Count} line(s)."
                    : $"Updated Sourcing RFQ {header.SourcingNo} (Status={header.Status}).",
                ActorUserId = userId,
                OccurredAtUtc = nowUtc
            });

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return await LoadDocumentDtoAsync(db, company, branch, header.SourcingId, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message == "NOT_FOUND")
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Sourcing document was not found.", PoSourcingErrorKind.NotFound);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveDraftAsync failed.");
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Unable to save the Sourcing document.", PoSourcingErrorKind.Unexpected);
        }
    }

    public async Task<PoSourcingOperationResult> CancelAsync(
        PoSourcingCancelRequest request,
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

        if (!await CanAsync(PermissionCodes.Cancel, cancellationToken))
        {
            return PoSourcingOperationResult.Fail("Not authorized.", PoSourcingErrorKind.Authorization);
        }

        var reason = (request.CancelReason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            return PoSourcingOperationResult.FailValidation(
                "Cancel reason is required.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CancelReason"] = "Cancel reason is required."
                });
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

        if (!RowVersionsEqual(header.RowVersion, request.RowVersion))
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail(
                "This Sourcing document was changed by another user. Reload before cancelling.",
                PoSourcingErrorKind.Concurrency);
        }

        if (header.Status == PoSourcingStatuses.Cancelled)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Sourcing document is already cancelled.");
        }

        var hasActiveConversions = await db.PoSourcingConversions
            .AnyAsync(c => c.CompanyCode == company
                && c.BranchCode == branch
                && c.SourcingId == header.SourcingId
                && c.Status == PoSourcingConversionStatuses.Active, cancellationToken);
        if (hasActiveConversions)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail(
                "Cannot cancel a Sourcing document that has active converted PR/PO documents. Cancel or delete the target PR/PO first.");
        }

        var awards = await db.PoSourcingAwards
            .Where(a => a.CompanyCode == company && a.BranchCode == branch && a.SourcingId == header.SourcingId)
            .ToListAsync(cancellationToken);
        foreach (var award in awards.Where(a => a.Status != PoSourcingAwardStatuses.Superseded && a.Status != PoSourcingAwardStatuses.Rejected))
        {
            award.Status = PoSourcingAwardStatuses.Superseded;
            TouchRowVersion(db, award);
        }

        var priorStatus = header.Status;
        header.Status = PoSourcingStatuses.Cancelled;
        header.CancelReason = Truncate(reason, 500);
        header.ModifiedBy = userId;
        header.ModifiedDate = nowUtc;
        TouchRowVersion(db, header);

        db.PoSourcingEvents.Add(new PoSourcingEvent
        {
            CompanyCode = company,
            BranchCode = branch,
            SourcingId = header.SourcingId,
            EventType = PoSourcingEventTypes.Cancelled,
            PriorStatus = priorStatus,
            NewStatus = PoSourcingStatuses.Cancelled,
            Reason = header.CancelReason,
            ActorUserId = userId,
            OccurredAtUtc = nowUtc
        });

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return await LoadDocumentDtoAsync(db, company, branch, header.SourcingId, cancellationToken);
    }

    public async Task<PoSourcingOperationResult> DeleteDraftAsync(
        long sourcingId,
        byte[] rowVersion,
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

        if (!await CanAsync(PermissionCodes.Delete, cancellationToken))
        {
            return PoSourcingOperationResult.Fail("Not authorized.", PoSourcingErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var company = context.CompanyCode!;
        var branch = context.BranchCode!;

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

        if (!RowVersionsEqual(header.RowVersion, rowVersion))
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail(
                "This Sourcing document was changed by another user. Reload before deleting.",
                PoSourcingErrorKind.Concurrency);
        }

        if (header.Status != PoSourcingStatuses.Draft)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Only DRAFT Sourcing documents can be deleted.");
        }

        var hasQuotesOrAwards = await db.PoSourcingQuotes.AnyAsync(q => q.CompanyCode == company && q.BranchCode == branch && q.SourcingId == sourcingId, cancellationToken)
            || await db.PoSourcingAwards.AnyAsync(a => a.CompanyCode == company && a.BranchCode == branch && a.SourcingId == sourcingId, cancellationToken);
        if (hasQuotesOrAwards)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSourcingOperationResult.Fail("Sourcing document with recorded quotes or awards cannot be deleted. Cancel it instead.");
        }

        var events = await db.PoSourcingEvents.Where(e => e.CompanyCode == company && e.BranchCode == branch && e.SourcingId == sourcingId).ToListAsync(cancellationToken);
        var attachments = await db.PoSourcingAttachments.Where(a => a.CompanyCode == company && a.BranchCode == branch && a.SourcingId == sourcingId).ToListAsync(cancellationToken);
        var suppliers = await db.PoSourcingSuppliers.Where(s => s.CompanyCode == company && s.BranchCode == branch && s.SourcingId == sourcingId).ToListAsync(cancellationToken);
        var lines = await db.PoSourcingLines.Where(l => l.CompanyCode == company && l.BranchCode == branch && l.SourcingId == sourcingId).ToListAsync(cancellationToken);

        db.PoSourcingEvents.RemoveRange(events);
        db.PoSourcingAttachments.RemoveRange(attachments);
        db.PoSourcingSuppliers.RemoveRange(suppliers);
        db.PoSourcingLines.RemoveRange(lines);
        db.PoSourcingHdrs.Remove(header);

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return PoSourcingOperationResult.Ok();
    }

    public async Task<PoSourcingOperationResult> UploadAttachmentAsync(
        PoSourcingAttachmentUploadRequest request,
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

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken) && !await CanAsync(PermissionCodes.Add, cancellationToken))
        {
            return PoSourcingOperationResult.Fail("Not authorized.", PoSourcingErrorKind.Authorization);
        }

        var fileName = Path.GetFileName(request.FileName ?? string.Empty).Trim();
        if (fileName.Length == 0)
        {
            return PoSourcingOperationResult.FailValidation("File name is required.");
        }

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext) || !AllowedAttachmentExtensions.Contains(ext))
        {
            return PoSourcingOperationResult.FailValidation($"File extension '{ext}' is not allowed.");
        }

        if (request.Content is null || request.Content.Length == 0)
        {
            return PoSourcingOperationResult.FailValidation("Attachment content cannot be empty.");
        }

        if (request.Content.Length > MaxAttachmentBytes)
        {
            return PoSourcingOperationResult.FailValidation($"Attachment exceeds maximum size of {MaxAttachmentBytes / (1024 * 1024)} MB.");
        }

        var scopeType = (request.ScopeType ?? PoSourcingAttachmentScopes.Rfq).Trim().ToUpperInvariant();
        if (scopeType != PoSourcingAttachmentScopes.Rfq && scopeType != PoSourcingAttachmentScopes.Quote)
        {
            scopeType = PoSourcingAttachmentScopes.Rfq;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = context.CompanyCode!;
        var branch = context.BranchCode!;
        var userId = Truncate(context.UserId!, 20);
        var nowUtc = DateTime.UtcNow;

        var header = await db.PoSourcingHdrs.FirstOrDefaultAsync(
            x => x.CompanyCode == company && x.BranchCode == branch && x.SourcingId == request.SourcingId,
            cancellationToken);
        if (header is null)
        {
            return PoSourcingOperationResult.Fail("Sourcing document was not found.", PoSourcingErrorKind.NotFound);
        }

        var storedFileName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var root = ResolveAttachmentRoot();
        var dir = Path.Combine(root, company, branch, "PO_SOURCING", request.SourcingId.ToString());
        Directory.CreateDirectory(dir);
        var fullPath = Path.Combine(dir, storedFileName);
        await File.WriteAllBytesAsync(fullPath, request.Content, cancellationToken);

        var containsSensitivePricing = request.ContainsSensitivePricing
            ?? (scopeType == PoSourcingAttachmentScopes.Quote);

        var attachment = new PoSourcingAttachment
        {
            CompanyCode = company,
            BranchCode = branch,
            SourcingId = request.SourcingId,
            ScopeKind = scopeType,
            QuoteId = request.QuoteId,
            OriginalFileName = Truncate(fileName, 200),
            StoredRelativePath = Path.Combine(company, branch, "PO_SOURCING", request.SourcingId.ToString(), storedFileName),
            ContentType = TruncateOptional(request.ContentType, 100) ?? "application/octet-stream",
            FileSizeBytes = request.Content.Length,
            ContainsSensitivePricing = containsSensitivePricing,
            UploadedBy = userId,
            UploadedAtUtc = nowUtc
        };

        db.PoSourcingAttachments.Add(attachment);
        await db.SaveChangesAsync(cancellationToken);

        return await LoadDocumentDtoAsync(db, company, branch, request.SourcingId, cancellationToken);
    }

    public async Task<PoSourcingOperationResult> DownloadAttachmentAsync(
        long sourcingId,
        long attachmentId,
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

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = context.CompanyCode!;
        var branch = context.BranchCode!;

        var attachment = await db.PoSourcingAttachments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company
                && x.BranchCode == branch
                && x.SourcingId == sourcingId
                && x.AttachmentId == attachmentId, cancellationToken);
        if (attachment is null)
        {
            return PoSourcingOperationResult.Fail("Attachment was not found.", PoSourcingErrorKind.NotFound);
        }

        if (attachment.ContainsSensitivePricing && !await CanAsync(PermissionCodes.ViewCost, cancellationToken))
        {
            return PoSourcingOperationResult.Fail(
                "VIEW_COST permission is required to download supplier quotation or award pricing attachments.",
                PoSourcingErrorKind.Authorization);
        }

        var root = ResolveAttachmentRoot();
        var fullPath = Path.Combine(root, attachment.StoredRelativePath);
        if (!File.Exists(fullPath))
        {
            return PoSourcingOperationResult.Fail("Attachment file was not found on storage.", PoSourcingErrorKind.NotFound);
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        return PoSourcingOperationResult.OkAttachment(new PoSourcingAttachmentDownloadDto
        {
            FileName = attachment.OriginalFileName,
            ContentType = string.IsNullOrWhiteSpace(attachment.ContentType) ? "application/octet-stream" : attachment.ContentType,
            Content = bytes
        });
    }

    public async Task<PoSourcingOperationResult> DeleteAttachmentAsync(
        long sourcingId,
        long attachmentId,
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

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken) && !await CanAsync(PermissionCodes.Delete, cancellationToken))
        {
            return PoSourcingOperationResult.Fail("Not authorized.", PoSourcingErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = context.CompanyCode!;
        var branch = context.BranchCode!;

        var attachment = await db.PoSourcingAttachments
            .FirstOrDefaultAsync(x => x.CompanyCode == company
                && x.BranchCode == branch
                && x.SourcingId == sourcingId
                && x.AttachmentId == attachmentId, cancellationToken);
        if (attachment is null)
        {
            return PoSourcingOperationResult.Fail("Attachment was not found.", PoSourcingErrorKind.NotFound);
        }

        var root = ResolveAttachmentRoot();
        var fullPath = Path.Combine(root, attachment.StoredRelativePath);
        db.PoSourcingAttachments.Remove(attachment);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete physical sourcing attachment {Path}", fullPath);
        }

        return await LoadDocumentDtoAsync(db, company, branch, sourcingId, cancellationToken);
    }

    private static string NormalizeSourceType(string? raw)
    {
        var v = (raw ?? string.Empty).Trim().ToUpperInvariant();
        if (v == "PR" || v == "PR_SOURCING")
        {
            return PoSourcingSourceTypes.Pr;
        }

        return PoSourcingSourceTypes.Direct;
    }
}
