using ErpWeb.Core.Admin;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Purchase;

public sealed class PoPrService : IPoPrService
{
    public const string AttachDocKey = "PR";
    public const string TempDocIdPrefix = "PRTMP-";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IDocumentNumberingService _documentNumbers;
    private readonly ICurrentDateService _dates;
    private readonly IPoPrRepository _repository;
    private readonly IPoOrderRepository _poOrders;
    private readonly PoPrOptions _options;
    private readonly IPoPrAttachmentService _attachments;
    private readonly ILogger<PoPrService> _logger;

    public PoPrService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IDocumentNumberingService documentNumbers,
        ICurrentDateService dates,
        IPoPrRepository repository,
        IOptions<PoPrOptions> options,
        IPoPrAttachmentService attachments,
        ILogger<PoPrService> logger,
        IPoOrderRepository? poOrders = null)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _documentNumbers = documentNumbers;
        _dates = dates;
        _repository = repository;
        _poOrders = poOrders ?? new PoOrderRepository();
        _options = options.Value;
        _attachments = attachments;
        _logger = logger;
    }

    public async Task<PoPrOperationResult> CreateTempDocIdAsync(CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return PoPrOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Add, cancellationToken))
        {
            return PoPrOperationResult.Fail("Not authorized.", PoPrErrorKind.Authorization);
        }

        var tempDocId = $"{TempDocIdPrefix}{Guid.NewGuid():D}";
        return PoPrOperationResult.OkTempDocId(tempDocId);
    }

    public async Task<PoPrOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return PoPrOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoPrOperationResult.Fail("Not authorized.", PoPrErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = context.CompanyCode!;
        var branch = context.BranchCode!;

        var direct = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive)
            .OrderBy(x => x.ICode)
            .Select(x => new PoPrItemLookupRow
            {
                ICode = x.ICode,
                IDesc = x.IDesc,
                IsIndirect = false,
                PurchaseUom = x.PurUom,
                StdUom = x.StdUom,
                PackSz = x.PurStdPackSize ?? x.StdPackSize ?? 1m,
                UnitPrice = x.PurchasePrice,
                TaxGroup = x.PurchaseTaxGroup ?? x.TaxGroup,
                DefWarehouse = x.DefWarehouse
            })
            .ToListAsync(cancellationToken);

        var indirect = await db.PoPurItems.AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .OrderBy(x => x.ICode)
            .Select(x => new PoPrItemLookupRow
            {
                ICode = x.ICode,
                IDesc = x.IDesc,
                IsIndirect = true,
                PurchaseUom = x.PurUom,
                PackSz = 1m,
                UnitPrice = x.UnitPrice,
                Category = x.Category,
                VendorCd = x.Vendor,
                VendNm = x.VendName,
                Moq = x.Moq
            })
            .ToListAsync(cancellationToken);

        var vendors = await db.PoSuppliers.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.IsActive)
            .OrderBy(x => x.SuppCode)
            .Select(x => new PoPrVendorLookupRow
            {
                SuppCode = x.SuppCode,
                SuppName = x.SuppName,
                Currency = x.Currency
            })
            .ToListAsync(cancellationToken);

        var taxGroups = await db.SaTaxGroups.AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .OrderBy(x => x.TaxGrCode)
            .Select(x => new PoPrTaxGroupLookupRow
            {
                TaxGrCode = x.TaxGrCode,
                TaxGrDesc = x.TaxGrDesc,
                Percentage = x.Percentage
            })
            .ToListAsync(cancellationToken);

        var currencies = await db.SaCurrencies.AsNoTracking()
            .Where(x => x.CompanyCode == company && (x.IsActive == null || x.IsActive == true))
            .OrderBy(x => x.CurrCode)
            .Select(x => new PoPrCodeLookupRow { Code = x.CurrCode, Name = x.CurrDesc })
            .ToListAsync(cancellationToken);

        var buyingTerms = await db.PoBuyingTerms.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive)
            .OrderBy(x => x.BuyingTerm)
            .Select(x => new PoPrCodeLookupRow { Code = x.BuyingTerm, Name = x.Description })
            .ToListAsync(cancellationToken);

        var paymentTerms = await db.IvMsCodes.AsNoTracking()
            .Where(x => x.CodeType == IvMsCodeTypes.PayCode)
            .OrderBy(x => x.Code)
            .Select(x => new PoPrCodeLookupRow { Code = x.Code, Name = x.Name })
            .ToListAsync(cancellationToken);

        var authorised = await db.PoAuthoriseds.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive)
            .OrderBy(x => x.Authorised)
            .Select(x => new PoPrCodeLookupRow { Code = x.Authorised, Name = x.Name })
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

        var categories = await db.PoCategories.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive)
            .OrderBy(x => x.Category)
            .Select(x => new PoPrCodeLookupRow { Code = x.Category, Name = x.Description })
            .ToListAsync(cancellationToken);

        var canViewCost = await CanAsync(PermissionCodes.ViewCost, cancellationToken);

        // Department / Project masters — company + branch scoped, active only.
        var departments = await db.MsDepts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.IsActive)
            .OrderBy(x => x.DeptCode)
            .Select(x => new PoPrCodeLookupRow { Code = x.DeptCode, Name = x.DeptName })
            .ToListAsync(cancellationToken);

        var projects = await db.MsProjects.AsNoTracking()
            .Where(x => x.CompanyCode == company
                && x.BranchCode == branch
                && x.Status == MsProjectStatus.Active)
            .OrderBy(x => x.ProjCode)
            .Select(x => new PoPrCodeLookupRow { Code = x.ProjCode, Name = x.ProjName })
            .ToListAsync(cancellationToken);

        return PoPrOperationResult.OkLookups(new PoPrLookups
        {
            DirectItems = direct,
            IndirectItems = indirect,
            Vendors = vendors,
            TaxGroups = taxGroups,
            Currencies = currencies,
            BuyingTerms = buyingTerms,
            PaymentTerms = paymentTerms,
            AuthorisedPersons = authorised,
            Warehouses = warehouses,
            Categories = categories,
            Departments = departments,
            Projects = projects,
            DefaultInclusive = _options.PurchaseItemTaxInclusive,
            UseWeight = _options.UseWeight,
            CanViewCost = canViewCost
        });
    }

    public async Task<PoPrOperationResult> SearchAsync(PoPrListQuery query, CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return PoPrOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoPrOperationResult.Fail("Not authorized.", PoPrErrorKind.Authorization);
        }

        query ??= new PoPrListQuery();
        var uid = Truncate(context.UserId!, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var (rows, total) = await _repository.SearchPagedAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            new PoPrSearchArgs(
                SearchText: string.IsNullOrWhiteSpace(query.SearchText) ? null : query.SearchText.Trim(),
                Status: string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim(),
                DateFrom: query.DateFrom,
                DateTo: query.DateTo,
                CreatedBy: _options.SelfViewEdit ? uid : null,
                SortField: query.SortField,
                SortDescending: query.SortDescending,
                Skip: query.Skip,
                Take: query.Take),
            cancellationToken);

        var prNos = rows.Select(x => x.PrNo).ToList();
        var detailStats = prNos.Count == 0
            ? []
            : await db.PoPrDetails.AsNoTracking()
                .Where(x =>
                    x.CompanyCode == context.CompanyCode
                    && x.BranchCode == context.BranchCode
                    && prNos.Contains(x.PrNo))
                .GroupBy(x => x.PrNo)
                .Select(g => new
                {
                    PrNo = g.Key,
                    LineCount = g.Count(),
                    HasConsumed = g.Any(d => d.PoNo != null && d.PoNo != ""),
                    Gross = g.Sum(d => d.IsInclusive ? d.Amount - d.TaxAmount : d.Amount),
                    Taxes = g.Sum(d => d.TaxAmount),
                    Currency = g.OrderBy(d => d.Line).Select(d => d.Currency).FirstOrDefault()
                })
                .ToListAsync(cancellationToken);

        var remainingByPr = prNos.Count == 0
            ? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            : await ComputeRemainingQtyByPrAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                prNos,
                cancellationToken);

        var statsByPr = detailStats.ToDictionary(x => x.PrNo, StringComparer.OrdinalIgnoreCase);
        var canEdit = await CanAsync(PermissionCodes.Edit, cancellationToken);
        var canDelete = await CanAsync(PermissionCodes.Delete, cancellationToken);
        var canCancel = await CanAsync(PermissionCodes.Cancel, cancellationToken);

        return PoPrOperationResult.OkList(new PoPrListPage
        {
            TotalCount = total,
            Rows = rows.Select(x =>
            {
                statsByPr.TryGetValue(x.PrNo, out var stats);
                var hasAnyPo = !string.IsNullOrWhiteSpace(x.PoNo) || (stats?.HasConsumed ?? false);
                var editable = IsEditableStatus(x.Status);
                var gross = PoPrCalc.RoundMoney(stats?.Gross ?? 0m);
                var taxes = PoPrCalc.RoundMoney(stats?.Taxes ?? 0m);
                return new PoPrListRow
                {
                    PrNo = x.PrNo,
                    CreateDt = x.CreateDt,
                    Status = x.Status,
                    Requester = x.Requester,
                    DeptCode = x.DeptCode,
                    PrType = x.PrType,
                    Remarks = x.Remarks,
                    PoNo = x.PoNo,
                    LineCount = stats?.LineCount ?? 0,
                    RemainingQty = remainingByPr.GetValueOrDefault(x.PrNo),
                    HasConsumedLines = stats?.HasConsumed ?? false,
                    Gross = gross,
                    Taxes = taxes,
                    Total = PoPrCalc.RoundMoney(gross + taxes),
                    Currency = stats?.Currency,
                    CreatedBy = x.CreatedBy,
                    CreatedDate = x.CreatedDate,
                    RowVersion = x.RowVersion ?? [],
                    CanEdit = canEdit && editable,
                    CanDelete = canDelete && editable && !hasAnyPo,
                    CanCancel = canCancel && editable && !hasAnyPo
                };
            }).ToList()
        });
    }

    public async Task<PoPrOperationResult> GetAsync(string prNo, CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return PoPrOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoPrOperationResult.Fail("Not authorized.", PoPrErrorKind.Authorization);
        }

        var no = (prNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return PoPrOperationResult.FailValidation("PR number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var header = await _repository.GetWithDetailsAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            no,
            cancellationToken);
        if (header is null)
        {
            return PoPrOperationResult.Fail("Purchase Requisition was not found.", PoPrErrorKind.NotFound);
        }

        if (_options.SelfViewEdit
            && !string.Equals(header.CreatedBy, Truncate(context.UserId!, 20), StringComparison.OrdinalIgnoreCase))
        {
            return PoPrOperationResult.Fail("Purchase Requisition was not found.", PoPrErrorKind.NotFound);
        }

        var canEdit = await CanAsync(PermissionCodes.Edit, cancellationToken);
        var canDelete = await CanAsync(PermissionCodes.Delete, cancellationToken);
        var canCancel = await CanAsync(PermissionCodes.Cancel, cancellationToken);
        return PoPrOperationResult.OkDocument(MapDocument(header, canEdit, canDelete, canCancel));
    }

    public async Task<PoPrOperationResult> SaveNewAsync(
        PoPrSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return PoPrOperationResult.FailValidation("Save request is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return PoPrOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Add, cancellationToken))
        {
            return PoPrOperationResult.Fail("Not authorized.", PoPrErrorKind.Authorization);
        }

        var tempDocId = NormalizeTempDocId(request.TempDocId);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // Legacy-aware Department / Project validation (shared rule — see MsRefLookupRules).
            var refErrors = await ValidateDeptProjectAsync(
                db, context.CompanyCode!, context.BranchCode!, priorDeptCode: null, priorProjId: null,
                request.DeptCode, request.ProjId, cancellationToken);
            if (refErrors is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.FailValidation(ValidationMessageFormat.JoinMessages(refErrors), refErrors);
            }

            var lines = StripEmptyLines(request.Lines);
            var prepared = await PrepareLinesAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                request.PrType,
                lines,
                existingByLine: null,
                cancellationToken);
            if (prepared.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                await DiscardDraftSafeAsync(tempDocId, cancellationToken);
                return prepared.ToFail();
            }

            var createDt = request.CreateDt == default ? _dates.Today.Date : request.CreateDt.Date;
            DocumentNumberResult issued;
            try
            {
                issued = await _documentNumbers.NextAsync(
                    db,
                    "PR",
                    "",
                    createDt,
                    DocumentNumberRequestMode.New,
                    "AUTO",
                    cancellationToken);
            }
            catch (DocumentNumberingNotConfiguredException)
            {
                await tx.RollbackAsync(cancellationToken);
                await DiscardDraftSafeAsync(tempDocId, cancellationToken);
                return PoPrOperationResult.Fail(
                    "PR numbering is not configured for this company/branch.",
                    PoPrErrorKind.BusinessRule);
            }
            catch (DocumentNumberingConfigurationException)
            {
                await tx.RollbackAsync(cancellationToken);
                await DiscardDraftSafeAsync(tempDocId, cancellationToken);
                return PoPrOperationResult.Fail(
                    "PR numbering is not configured correctly. Contact an administrator.",
                    PoPrErrorKind.BusinessRule);
            }
            catch (DocumentNumberingOverflowException)
            {
                await tx.RollbackAsync(cancellationToken);
                await DiscardDraftSafeAsync(tempDocId, cancellationToken);
                return PoPrOperationResult.Fail(
                    "The next PR number exceeds the configured length.",
                    PoPrErrorKind.BusinessRule);
            }
            catch (DocumentNumberingConcurrencyException)
            {
                await tx.RollbackAsync(cancellationToken);
                await DiscardDraftSafeAsync(tempDocId, cancellationToken);
                return PoPrOperationResult.Fail(
                    "The PR could not be saved because of a database conflict. Try again.",
                    PoPrErrorKind.Unexpected);
            }

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 20);
            var header = new PoPr
            {
                CompanyCode = context.CompanyCode!,
                BranchCode = context.BranchCode!,
                PrNo = issued.DocumentNumber,
                CreateDt = createDt,
                Status = PoPrStatuses.New,
                Requester = TruncateOptional(request.Requester, 50) ?? uid,
                DeptCode = TruncateOptional(request.DeptCode, 20),
                CheckedBy = TruncateOptional(request.CheckedBy, 20),
                AuthorisedBy = TruncateOptional(request.AuthorisedBy, 20),
                AuthorisedBy2nd = TruncateOptional(request.AuthorisedBy2nd, 20),
                PrType = TruncateOptional(request.PrType, 20),
                LocationCode = TruncateOptional(context.LocationCode, 10),
                ProjId = TruncateOptional(request.ProjId, 20),
                Remarks = TruncateOptional(request.Remarks, 500),
                CreatedDate = now,
                CreatedBy = uid
            };

            short lineNo = 1;
            foreach (var line in prepared.Lines!)
            {
                header.Details.Add(ToDetailEntity(header, line, lineNo++));
            }

            if (tempDocId is not null)
            {
                await StampAttachDocIdsInDbAsync(
                    db,
                    context.CompanyCode!,
                    context.BranchCode!,
                    tempDocId,
                    header.PrNo,
                    uid,
                    cancellationToken);
            }

            TouchRowVersion(db, header);
            db.PoPrs.Add(header);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            if (tempDocId is not null)
            {
                await MoveDraftSafeAsync(context.CompanyCode!, context.BranchCode!, tempDocId, header.PrNo, cancellationToken);
            }

            _logger.LogInformation(
                "PR saved. UserId={UserId} Company={Company} PrNo={PrNo}",
                context.UserId,
                context.CompanyCode,
                header.PrNo);

            return await GetAsync(header.PrNo, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            await DiscardDraftSafeAsync(tempDocId, cancellationToken);
            return PoPrOperationResult.Fail("PR number is already used.", PoPrErrorKind.Unexpected);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "PR save deadlock.");
            await tx.RollbackAsync(cancellationToken);
            await DiscardDraftSafeAsync(tempDocId, cancellationToken);
            return PoPrOperationResult.Fail(
                "The PR could not be saved because of a database conflict. Try again.",
                PoPrErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PR save failed.");
            await tx.RollbackAsync(cancellationToken);
            await DiscardDraftSafeAsync(tempDocId, cancellationToken);
            return PoPrOperationResult.Fail("Unable to save the Purchase Requisition.", PoPrErrorKind.Unexpected);
        }
    }

    public async Task<PoPrOperationResult> UpdateAsync(
        string prNo,
        PoPrSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return PoPrOperationResult.FailValidation("Save request is required.");
        }

        var no = (prNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return PoPrOperationResult.FailValidation("PR number is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return PoPrOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return PoPrOperationResult.Fail("Not authorized.", PoPrErrorKind.Authorization);
        }

        if (request.RowVersion is null || request.RowVersion.Length == 0)
        {
            return PoPrOperationResult.Fail(
                "This Purchase Requisition was changed by another user. Reload before saving.",
                PoPrErrorKind.Concurrency);
        }

        var tempDocId = NormalizeTempDocId(request.TempDocId);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var livePoRefs = await FindLivePoReferencesForPrAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                no,
                cancellationToken);
            await _poOrders.LockPoHeadersAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                livePoRefs,
                cancellationToken);

            var header = await _repository.LockForUpdateAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                no,
                cancellationToken);
            if (header is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.Fail("Purchase Requisition was not found.", PoPrErrorKind.NotFound);
            }

            if (!RowVersionsEqual(header.RowVersion, request.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.Fail(
                    "This Purchase Requisition was changed by another user. Reload before saving.",
                    PoPrErrorKind.Concurrency);
            }

            if (_options.SelfViewEdit
                && !string.Equals(header.CreatedBy, Truncate(context.UserId!, 20), StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.Fail("Purchase Requisition was not found.", PoPrErrorKind.NotFound);
            }

            if (!IsEditableStatus(header.Status))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.Fail(
                    "Only NEW or OPEN Purchase Requisitions can be edited.",
                    PoPrErrorKind.BusinessRule);
            }

            db.Entry(header).Property(x => x.RowVersion).OriginalValue = request.RowVersion;
            await db.Entry(header).Collection(x => x.Details).LoadAsync(cancellationToken);

            var existingByLine = header.Details.ToDictionary(x => x.Line, x => x);
            var lines = StripEmptyLines(request.Lines);

            // Legacy-aware Department / Project validation (shared rule — see MsRefLookupRules).
            var refErrors = await ValidateDeptProjectAsync(
                db, context.CompanyCode!, context.BranchCode!, header.DeptCode, header.ProjId,
                request.DeptCode, request.ProjId, cancellationToken);
            if (refErrors is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.FailValidation(ValidationMessageFormat.JoinMessages(refErrors), refErrors);
            }

            var prepared = await PrepareLinesAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                request.PrType,
                lines,
                existingByLine,
                cancellationToken);
            if (prepared.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return prepared.ToFail();
            }

            var syncError = SynchronizeDetails(db, header, prepared.Lines!, existingByLine);
            if (syncError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return syncError;
            }

            header.CreateDt = request.CreateDt == default ? header.CreateDt : request.CreateDt.Date;
            header.Requester = TruncateOptional(request.Requester, 50) ?? header.Requester;
            header.DeptCode = TruncateOptional(request.DeptCode, 20);
            header.CheckedBy = TruncateOptional(request.CheckedBy, 20);
            header.AuthorisedBy = TruncateOptional(request.AuthorisedBy, 20);
            header.AuthorisedBy2nd = TruncateOptional(request.AuthorisedBy2nd, 20);
            header.PrType = TruncateOptional(request.PrType, 20);
            header.ProjId = TruncateOptional(request.ProjId, 20);
            header.Remarks = TruncateOptional(request.Remarks, 500);
            header.Status = PoPrStatuses.New;
            header.ModifiedDate = DateTime.UtcNow;
            header.ModifiedBy = Truncate(context.UserId!, 20);

            if (tempDocId is not null)
            {
                await StampAttachDocIdsInDbAsync(
                    db,
                    context.CompanyCode!,
                    context.BranchCode!,
                    tempDocId,
                    header.PrNo,
                    Truncate(context.UserId!, 20),
                    cancellationToken);
            }

            TouchRowVersion(db, header);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            if (tempDocId is not null)
            {
                await MoveDraftSafeAsync(context.CompanyCode!, context.BranchCode!, tempDocId, header.PrNo, cancellationToken);
            }

            return await GetAsync(no, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoPrOperationResult.Fail(
                "This Purchase Requisition was changed by another user. Reload before saving.",
                PoPrErrorKind.Concurrency);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "PR update deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return PoPrOperationResult.Fail(
                "The PR could not be saved because of a database conflict. Try again.",
                PoPrErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PR update failed.");
            await tx.RollbackAsync(cancellationToken);
            return PoPrOperationResult.Fail("Unable to save the Purchase Requisition.", PoPrErrorKind.Unexpected);
        }
    }

    public async Task<PoPrOperationResult> CopyAsync(string sourcePrNo, CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return PoPrOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Add, cancellationToken))
        {
            return PoPrOperationResult.Fail("Not authorized.", PoPrErrorKind.Authorization);
        }

        var no = (sourcePrNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return PoPrOperationResult.FailValidation("Source PR number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var source = await _repository.GetWithDetailsAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            no,
            cancellationToken);
        if (source is null)
        {
            return PoPrOperationResult.Fail("Purchase Requisition was not found.", PoPrErrorKind.NotFound);
        }

        if (_options.SelfViewEdit
            && !string.Equals(source.CreatedBy, Truncate(context.UserId!, 20), StringComparison.OrdinalIgnoreCase))
        {
            return PoPrOperationResult.Fail("Purchase Requisition was not found.", PoPrErrorKind.NotFound);
        }

        var uid = Truncate(context.UserId!, 20);
        var now = _dates.Now;
        var lines = source.Details
            .OrderBy(x => x.Line)
            .Select(x => new PoPrLineDto
            {
                Line = 0,
                EtaDt = x.EtaDt,
                OneTimeItemYn = x.OneTimeItemYn,
                ICode = x.ICode,
                IDesc = x.IDesc,
                Category = x.Category,
                Qty = x.Qty,
                PackSz = x.PackSz,
                StdUom = x.StdUom,
                PurchaseQty = x.PurchaseQty,
                PurchaseUom = x.PurchaseUom,
                Currency = x.Currency,
                UnitPrice = x.UnitPrice,
                OneTimeVendor = x.OneTimeVendor,
                VendorCd = x.VendorCd,
                VendNm = x.VendNm,
                Purpose = x.Purpose,
                Status = null,
                StdQty = x.StdQty,
                WtQty = x.WtQty,
                WtUom = x.WtUom,
                PaymentTerm = x.PaymentTerm,
                BuyingTerm = x.BuyingTerm,
                RepairType = x.RepairType,
                Amount = x.Amount,
                PoNo = null,
                TaxGroup = x.TaxGroup,
                TaxAmount = x.TaxAmount,
                IsInclusive = x.IsInclusive,
                ToWarehouse = x.ToWarehouse,
                SoNo = null,
                SoLine = null,
                NetAmount = x.IsInclusive ? x.Amount - x.TaxAmount : x.Amount
            })
            .ToList();

        var (gross, taxes, total) = PoPrCalc.SumTotals(
            lines.Select(x => (x.NetAmount, x.TaxAmount)));
        var currency = lines
            .OrderBy(x => x.Line == 0 ? short.MaxValue : x.Line)
            .Select(x => x.Currency)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        short lineNo = 1;
        foreach (var line in lines)
        {
            line.Line = lineNo++;
        }

        var draft = new PoPrDocument
        {
            PrNo = "AUTO",
            CreateDt = _dates.Today.Date,
            Status = PoPrStatuses.New,
            Requester = uid,
            DeptCode = source.DeptCode,
            CheckedBy = null,
            AuthorisedBy = null,
            AuthorisedBy2nd = null,
            PrType = source.PrType,
            LocationCode = context.LocationCode,
            PoNo = null,
            ApprReason = null,
            ProjId = source.ProjId,
            Remarks = source.Remarks,
            Currency = currency,
            Gross = gross,
            Taxes = taxes,
            Total = total,
            CreatedBy = uid,
            CreatedDate = now,
            ModifiedBy = null,
            ModifiedDate = null,
            RowVersion = [],
            CanEdit = true,
            CanDelete = false,
            CanCancel = false,
            HasConsumedLines = false,
            Lines = lines
        };

        return PoPrOperationResult.OkDocument(draft);
    }

    public async Task<PoPrOperationResult> CancelAsync(
        PoPrCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return PoPrOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Cancel, cancellationToken))
        {
            return PoPrOperationResult.Fail("Not authorized.", PoPrErrorKind.Authorization);
        }

        var no = (request.PrNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return PoPrOperationResult.FailValidation("PR number is required.");
        }

        if (request.RowVersion is null || request.RowVersion.Length == 0)
        {
            return PoPrOperationResult.Fail(
                "This Purchase Requisition was changed by another user. Reload before cancelling.",
                PoPrErrorKind.Concurrency);
        }

        var reason = (request.ApprReason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            return PoPrOperationResult.FailValidation(
                "Cancel reason is required.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ApprReason"] = "Cancel reason is required."
                });
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var livePoRefs = await FindLivePoReferencesForPrAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                no,
                cancellationToken);
            await _poOrders.LockPoHeadersAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                livePoRefs,
                cancellationToken);

            var header = await _repository.LockForUpdateAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                no,
                cancellationToken);
            if (header is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.Fail("Purchase Requisition was not found.", PoPrErrorKind.NotFound);
            }

            if (!RowVersionsEqual(header.RowVersion, request.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.Fail(
                    "This Purchase Requisition was changed by another user. Reload before cancelling.",
                    PoPrErrorKind.Concurrency);
            }

            if (_options.SelfViewEdit
                && !string.Equals(header.CreatedBy, Truncate(context.UserId!, 20), StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.Fail("Purchase Requisition was not found.", PoPrErrorKind.NotFound);
            }

            if (!IsEditableStatus(header.Status))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.Fail(
                    "Only NEW or OPEN Purchase Requisitions can be cancelled.",
                    PoPrErrorKind.BusinessRule);
            }

            await db.Entry(header).Collection(x => x.Details).LoadAsync(cancellationToken);
            livePoRefs = await FindLivePoReferencesForPrAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                no,
                cancellationToken);
            if (livePoRefs.Count > 0 || HasAnyPoNo(header))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.Fail(
                    "Purchase Requisition cannot be cancelled because a live PO reference exists.",
                    PoPrErrorKind.BusinessRule);
            }

            db.Entry(header).Property(x => x.RowVersion).OriginalValue = request.RowVersion;
            header.Status = PoPrStatuses.Cancelled;
            header.ApprReason = TruncateOptional(reason, 200);
            header.ModifiedDate = DateTime.UtcNow;
            header.ModifiedBy = Truncate(context.UserId!, 20);
            foreach (var detail in header.Details)
            {
                detail.Status = PoPrStatuses.Cancelled;
            }
            TouchRowVersion(db, header);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(no, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoPrOperationResult.Fail(
                "This Purchase Requisition was changed by another user. Reload before cancelling.",
                PoPrErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PR cancel failed.");
            await tx.RollbackAsync(cancellationToken);
            return PoPrOperationResult.Fail("Unable to cancel the Purchase Requisition.", PoPrErrorKind.Unexpected);
        }
    }

    public async Task<PoPrOperationResult> DeleteAsync(
        PoPrKeyedRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return PoPrOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Delete, cancellationToken))
        {
            return PoPrOperationResult.Fail("Not authorized.", PoPrErrorKind.Authorization);
        }

        var no = (request.PrNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return PoPrOperationResult.FailValidation("PR number is required.");
        }

        if (request.RowVersion is null || request.RowVersion.Length == 0)
        {
            return PoPrOperationResult.Fail(
                "This Purchase Requisition was changed by another user. Reload before deleting.",
                PoPrErrorKind.Concurrency);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        IReadOnlyList<(string DocName, string? DocName2)> attachFiles = [];
        try
        {
            var livePoRefs = await FindLivePoReferencesForPrAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                no,
                cancellationToken);
            await _poOrders.LockPoHeadersAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                livePoRefs,
                cancellationToken);

            var header = await _repository.LockForUpdateAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                no,
                cancellationToken);
            if (header is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.Fail("Purchase Requisition was not found.", PoPrErrorKind.NotFound);
            }

            if (!RowVersionsEqual(header.RowVersion, request.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.Fail(
                    "This Purchase Requisition was changed by another user. Reload before deleting.",
                    PoPrErrorKind.Concurrency);
            }

            if (_options.SelfViewEdit
                && !string.Equals(header.CreatedBy, Truncate(context.UserId!, 20), StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.Fail("Purchase Requisition was not found.", PoPrErrorKind.NotFound);
            }

            if (!IsEditableStatus(header.Status))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.Fail(
                    "Only NEW or OPEN Purchase Requisitions can be deleted.",
                    PoPrErrorKind.BusinessRule);
            }

            await db.Entry(header).Collection(x => x.Details).LoadAsync(cancellationToken);
            livePoRefs = await FindLivePoReferencesForPrAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                no,
                cancellationToken);
            if (livePoRefs.Count > 0 || HasAnyPoNo(header))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoPrOperationResult.Fail(
                    "Purchase Requisition cannot be deleted because a live PO reference exists.",
                    PoPrErrorKind.BusinessRule);
            }

            db.Entry(header).Property(x => x.RowVersion).OriginalValue = request.RowVersion;

            var attachRows = await db.PoPrAttachFiles
                .Where(x =>
                    x.CompanyCode == header.CompanyCode
                    && x.BranchCode == header.BranchCode
                    && x.DocKey == AttachDocKey
                    && x.DocId == header.PrNo)
                .ToListAsync(cancellationToken);
            attachFiles = attachRows.Select(x => (x.DocName, x.DocName2)).ToList();
            if (attachRows.Count > 0)
            {
                db.PoPrAttachFiles.RemoveRange(attachRows);
            }

            db.PoPrs.Remove(header);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            await _attachments.DeletePhysicalForDocAsync(
                context.CompanyCode!,
                context.BranchCode!,
                no,
                attachFiles,
                cancellationToken);

            return PoPrOperationResult.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoPrOperationResult.Fail(
                "This Purchase Requisition was changed by another user. Reload before deleting.",
                PoPrErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PR delete failed.");
            await tx.RollbackAsync(cancellationToken);
            return PoPrOperationResult.Fail("Unable to delete the Purchase Requisition.", PoPrErrorKind.Unexpected);
        }
    }

    /// <summary>
    /// Legacy-aware Department / Project validation for the PR header (shared rule —
    /// see MsRefLookupRules). Returns null when valid, otherwise the field error map.
    /// </summary>
    private static async Task<Dictionary<string, string>?> ValidateDeptProjectAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string? priorDeptCode,
        string? priorProjId,
        string? deptCode,
        string? projId,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var deptError = await MsRefLookupRules.ValidateAsync(
            db, companyCode, branchCode, MsRefLookupKind.Department, priorDeptCode, deptCode, cancellationToken);
        if (deptError is not null)
        {
            errors["DeptCode"] = deptError;
        }

        var projError = await MsRefLookupRules.ValidateAsync(
            db, companyCode, branchCode, MsRefLookupKind.Project, priorProjId, projId, cancellationToken);
        if (projError is not null)
        {
            errors["ProjId"] = projError;
        }

        return errors.Count == 0 ? null : errors;
    }

    private async Task<PrepareOutcome> PrepareLinesAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string? prType,
        IReadOnlyList<PoPrLineDto> lines,
        IReadOnlyDictionary<short, PoPrDetail>? existingByLine,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            return PrepareOutcome.Validation(
                "At least one line is required.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Lines"] = "At least one line is required."
                });
        }

        var isRepair = string.Equals(prType?.Trim(), PoPrTypes.Repair, StringComparison.OrdinalIgnoreCase);
        var taxGroups = await db.SaTaxGroups.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode)
            .ToDictionaryAsync(x => x.TaxGrCode, x => x.Percentage, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var prepared = new List<PreparedLine>(lines.Count);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? documentCurrency = null;
        bool? documentInclusive = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var src = lines[i];
            var prefix = $"Lines[{i}]";
            var existing = existingByLine is not null
                && src.Line > 0
                && existingByLine.TryGetValue(src.Line, out var existingDetail)
                    ? existingDetail
                    : null;

            if (existing is not null && !string.IsNullOrWhiteSpace(existing.PoNo))
            {
                var consumed = MapConsumedPrepared(existing, i);
                EnforceCurrencyInclusive(ref documentCurrency, ref documentInclusive, consumed, prefix, errors);
                prepared.Add(consumed);
                continue;
            }

            var iCode = (src.ICode ?? string.Empty).Trim();
            if (iCode.Length == 0)
            {
                errors[$"{prefix}.ICode"] = "Item code is required.";
                continue;
            }

            if (_options.POControlIcode && src.OneTimeItemYn == true)
            {
                errors[$"{prefix}.OneTimeItemYn"] = "One-time items are not allowed.";
            }

            var purchaseQty = src.PurchaseQty != 0m ? src.PurchaseQty : src.Qty;
            if (purchaseQty == 0m)
            {
                errors[$"{prefix}.PurchaseQty"] = "Quantity cannot be zero.";
            }

            var purchaseUom = TruncateOptional(src.PurchaseUom, 10);
            if (string.IsNullOrWhiteSpace(purchaseUom))
            {
                errors[$"{prefix}.PurchaseUom"] = "Purchase UOM is required.";
            }

            var currency = TruncateOptional(src.Currency, 20);
            if (string.IsNullOrWhiteSpace(currency))
            {
                errors[$"{prefix}.Currency"] = "Currency is required.";
            }

            if (isRepair && string.IsNullOrWhiteSpace(src.RepairType))
            {
                errors[$"{prefix}.RepairType"] = "Repair type is required.";
            }

            var resolved = await ResolveItemAsync(db, companyCode, iCode, cancellationToken);
            if (resolved is null)
            {
                errors[$"{prefix}.ICode"] = "Item was not found in stock or purchase item master.";
                continue;
            }

            var taxGroup = TruncateOptional(src.TaxGroup, 20);
            decimal taxPercent = 0m;
            if (!string.IsNullOrWhiteSpace(taxGroup))
            {
                if (!taxGroups.TryGetValue(taxGroup, out taxPercent))
                {
                    errors[$"{prefix}.TaxGroup"] = "Tax group was not found.";
                }
                else if (taxPercent < 0m)
                {
                    errors[$"{prefix}.TaxGroup"] = "Tax rate cannot be negative.";
                }
            }

            var sameICode = existing is not null
                && string.Equals(existing.ICode?.Trim(), iCode, StringComparison.OrdinalIgnoreCase);
            decimal unitPrice;
            if (sameICode)
            {
                unitPrice = existing!.UnitPrice;
            }
            else
            {
                unitPrice = await ResolveUnitPriceAsync(
                    db,
                    companyCode,
                    branchCode,
                    iCode,
                    TruncateOptional(src.VendorCd, 60),
                    purchaseUom,
                    resolved,
                    cancellationToken);
            }

            var packSz = src.PackSz;
            if (packSz == 0m && resolved.PackSz != 0m)
            {
                packSz = resolved.PackSz;
            }

            var stdQty = PoPrCalc.ComputeStdQty(purchaseQty, packSz);
            var amount = PoPrCalc.ComputeAmount(purchaseQty, unitPrice);
            var isInclusive = existing is not null && sameICode
                ? existing.IsInclusive
                : src.IsInclusive;

            var (net, taxAmount) = PoPrCalc.ComputeTax(amount, taxPercent, isInclusive, _options.PurchaseTaxDec);
            var moq = await ResolveMoqAsync(
                db,
                companyCode,
                branchCode,
                iCode,
                TruncateOptional(src.VendorCd, 60),
                purchaseUom,
                resolved,
                cancellationToken);
            if (moq > 0m && Math.Abs(purchaseQty) < moq)
            {
                errors[$"{prefix}.PurchaseQty"] = $"Quantity must be at least MOQ ({moq}).";
            }

            var line = new PreparedLine
            {
                RequestOrder = i,
                ExistingLineNo = existing?.Line,
                IsConsumed = false,
                EtaDt = src.EtaDt,
                OneTimeItemYn = src.OneTimeItemYn,
                ICode = iCode,
                IDesc = TruncateOptional(src.IDesc, 200) ?? TruncateOptional(resolved.IDesc, 200),
                Category = TruncateOptional(src.Category, 20) ?? TruncateOptional(resolved.Category, 20),
                Qty = src.Qty != 0m ? src.Qty : stdQty,
                PackSz = packSz,
                StdUom = TruncateOptional(src.StdUom, 10) ?? TruncateOptional(resolved.StdUom, 10),
                PurchaseQty = purchaseQty,
                PurchaseUom = purchaseUom,
                Currency = currency,
                UnitPrice = unitPrice,
                OneTimeVendor = src.OneTimeVendor,
                VendorCd = TruncateOptional(src.VendorCd, 60),
                VendNm = TruncateOptional(src.VendNm, 200),
                Purpose = TruncateOptional(src.Purpose, 250),
                Status = TruncateOptional(src.Status, 20),
                StdQty = stdQty,
                WtQty = _options.UseWeight ? src.WtQty : 0m,
                WtUom = _options.UseWeight ? TruncateOptional(src.WtUom, 10) : null,
                PaymentTerm = TruncateOptional(src.PaymentTerm, 20),
                BuyingTerm = TruncateOptional(src.BuyingTerm, 20),
                RepairType = TruncateOptional(src.RepairType, 20),
                Amount = amount,
                TaxGroup = taxGroup,
                TaxAmount = taxAmount,
                IsInclusive = isInclusive,
                ToWarehouse = TruncateOptional(src.ToWarehouse, 20),
                SoNo = TruncateOptional(src.SoNo, 30),
                SoLine = src.SoLine,
                NetAmount = net,
                PoNo = null
            };

            EnforceCurrencyInclusive(ref documentCurrency, ref documentInclusive, line, prefix, errors);
            prepared.Add(line);
        }

        if (errors.Count > 0)
        {
            return PrepareOutcome.Validation("Validation failed.", errors);
        }

        return PrepareOutcome.Ok(prepared);
    }

    private static void EnforceCurrencyInclusive(
        ref string? documentCurrency,
        ref bool? documentInclusive,
        PreparedLine line,
        string prefix,
        IDictionary<string, string> errors)
    {
        if (!string.IsNullOrWhiteSpace(line.Currency))
        {
            if (documentCurrency is null)
            {
                documentCurrency = line.Currency;
            }
            else if (!string.Equals(documentCurrency, line.Currency, StringComparison.OrdinalIgnoreCase))
            {
                errors[$"{prefix}.Currency"] = "All lines must use the same currency.";
            }
        }

        if (documentInclusive is null)
        {
            documentInclusive = line.IsInclusive;
        }
        else if (documentInclusive.Value != line.IsInclusive)
        {
            errors[$"{prefix}.IsInclusive"] = "Inclusive and exclusive tax lines cannot be mixed.";
        }
    }

    private static PreparedLine MapConsumedPrepared(PoPrDetail existing, int requestOrder)
    {
        var net = existing.IsInclusive ? existing.Amount - existing.TaxAmount : existing.Amount;
        return new PreparedLine
        {
            RequestOrder = requestOrder,
            ExistingLineNo = existing.Line,
            IsConsumed = true,
            EtaDt = existing.EtaDt,
            OneTimeItemYn = existing.OneTimeItemYn,
            ICode = existing.ICode ?? string.Empty,
            IDesc = existing.IDesc,
            Category = existing.Category,
            Qty = existing.Qty,
            PackSz = existing.PackSz,
            StdUom = existing.StdUom,
            PurchaseQty = existing.PurchaseQty,
            PurchaseUom = existing.PurchaseUom,
            Currency = existing.Currency,
            UnitPrice = existing.UnitPrice,
            OneTimeVendor = existing.OneTimeVendor,
            VendorCd = existing.VendorCd,
            VendNm = existing.VendNm,
            Purpose = existing.Purpose,
            Status = existing.Status,
            StdQty = existing.StdQty,
            WtQty = existing.WtQty,
            WtUom = existing.WtUom,
            PaymentTerm = existing.PaymentTerm,
            BuyingTerm = existing.BuyingTerm,
            RepairType = existing.RepairType,
            Amount = existing.Amount,
            TaxGroup = existing.TaxGroup,
            TaxAmount = existing.TaxAmount,
            IsInclusive = existing.IsInclusive,
            ToWarehouse = existing.ToWarehouse,
            SoNo = existing.SoNo,
            SoLine = existing.SoLine,
            NetAmount = net,
            PoNo = existing.PoNo
        };
    }

    private static PoPrOperationResult? SynchronizeDetails(
        AppDbContext db,
        PoPr header,
        List<PreparedLine> prepared,
        IReadOnlyDictionary<short, PoPrDetail> existingByLine)
    {
        var requestedExisting = prepared
            .Where(x => x.ExistingLineNo is not null)
            .Select(x => x.ExistingLineNo!.Value)
            .ToHashSet();

        foreach (var existing in header.Details.Where(x => !requestedExisting.Contains(x.Line)).ToList())
        {
            if (!string.IsNullOrWhiteSpace(existing.PoNo))
            {
                return PoPrOperationResult.Fail(
                    $"Line {existing.Line} cannot be deleted because it is linked to a PO.",
                    PoPrErrorKind.BusinessRule);
            }

            db.PoPrDetails.Remove(existing);
            header.Details.Remove(existing);
        }

        var hasConsumed = header.Details.Any(x => !string.IsNullOrWhiteSpace(x.PoNo))
            || prepared.Any(x => x.IsConsumed);

        if (!hasConsumed)
        {
            foreach (var existing in header.Details.ToList())
            {
                db.PoPrDetails.Remove(existing);
                header.Details.Remove(existing);
            }

            short lineNo = 1;
            foreach (var line in prepared.OrderBy(x => x.RequestOrder))
            {
                header.Details.Add(ToDetailEntity(header, line, lineNo++));
            }

            return null;
        }

        var nextLine = header.Details.Count == 0
            ? (short)1
            : (short)(header.Details.Max(x => x.Line) + 1);

        foreach (var line in prepared.OrderBy(x => x.RequestOrder))
        {
            PoPrDetail detail;
            if (line.ExistingLineNo is short existingLineNo
                && existingByLine.TryGetValue(existingLineNo, out var existingDetail)
                && header.Details.Contains(existingDetail))
            {
                detail = existingDetail;
                if (line.IsConsumed)
                {
                    continue;
                }
            }
            else
            {
                detail = new PoPrDetail
                {
                    CompanyCode = header.CompanyCode,
                    BranchCode = header.BranchCode,
                    PrNo = header.PrNo,
                    Line = nextLine++
                };
                header.Details.Add(detail);
            }

            ApplyPreparedLine(detail, line);
        }

        return null;
    }

    private static void ApplyPreparedLine(PoPrDetail detail, PreparedLine line)
    {
        detail.EtaDt = line.EtaDt;
        detail.OneTimeItemYn = line.OneTimeItemYn;
        detail.ICode = line.ICode;
        detail.IDesc = line.IDesc;
        detail.Category = line.Category;
        detail.Qty = line.Qty;
        detail.PackSz = line.PackSz;
        detail.StdUom = line.StdUom;
        detail.PurchaseQty = line.PurchaseQty;
        detail.PurchaseUom = line.PurchaseUom;
        detail.Currency = line.Currency;
        detail.UnitPrice = line.UnitPrice;
        detail.OneTimeVendor = line.OneTimeVendor;
        detail.VendorCd = line.VendorCd;
        detail.VendNm = line.VendNm;
        detail.Purpose = line.Purpose;
        detail.Status = line.Status;
        detail.StdQty = line.StdQty;
        detail.WtQty = line.WtQty;
        detail.WtUom = line.WtUom;
        detail.PaymentTerm = line.PaymentTerm;
        detail.BuyingTerm = line.BuyingTerm;
        detail.RepairType = line.RepairType;
        detail.Amount = line.Amount;
        detail.PoNo = line.PoNo;
        detail.TaxGroup = line.TaxGroup;
        detail.TaxAmount = line.TaxAmount;
        detail.IsInclusive = line.IsInclusive;
        detail.ToWarehouse = line.ToWarehouse;
        detail.SoNo = line.SoNo;
        detail.SoLine = line.SoLine;
    }

    private static PoPrDetail ToDetailEntity(PoPr header, PreparedLine line, short lineNo)
    {
        var detail = new PoPrDetail
        {
            CompanyCode = header.CompanyCode,
            BranchCode = header.BranchCode,
            PrNo = header.PrNo,
            Line = lineNo
        };
        ApplyPreparedLine(detail, line);
        return detail;
    }

    private async Task<ResolvedItem?> ResolveItemAsync(
        AppDbContext db,
        string companyCode,
        string iCode,
        CancellationToken cancellationToken)
    {
        var stock = await db.IvStockMasters.AsNoTracking().FirstOrDefaultAsync(
            x => x.CompanyCode == companyCode && x.ICode == iCode && x.IsActive,
            cancellationToken);
        if (stock is not null)
        {
            return new ResolvedItem(
                false,
                stock.ICode,
                stock.IDesc,
                stock.PurUom,
                stock.StdUom,
                stock.PurStdPackSize ?? stock.StdPackSize ?? 1m,
                stock.PurchasePrice ?? 0m,
                stock.PurchaseTaxGroup ?? stock.TaxGroup,
                null,
                null,
                0m,
                stock.DefWarehouse);
        }

        var pur = await db.PoPurItems.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.ICode == iCode)
            .OrderByDescending(x => x.ModifiedDate)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (pur is null)
        {
            return null;
        }

        return new ResolvedItem(
            true,
            pur.ICode,
            pur.IDesc,
            pur.PurUom,
            null,
            1m,
            pur.UnitPrice ?? 0m,
            null,
            pur.Category,
            pur.Vendor,
            pur.Moq,
            null);
    }

    private async Task<decimal> ResolveUnitPriceAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string iCode,
        string? vendorCd,
        string? purchaseUom,
        ResolvedItem item,
        CancellationToken cancellationToken)
    {
        var mode = _options.SupplierPrice;
        if (mode is 2 or 3)
        {
            var vendorPrice = await FindVendorItemPriceAsync(
                db, companyCode, branchCode, iCode, vendorCd, purchaseUom, cancellationToken);
            if (vendorPrice is not null)
            {
                return vendorPrice.Value;
            }

            if (mode == 2)
            {
                return 0m;
            }
        }

        return item.UnitPrice;
    }

    private async Task<decimal?> FindVendorItemPriceAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string iCode,
        string? vendorCd,
        string? purchaseUom,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vendorCd) || string.IsNullOrWhiteSpace(purchaseUom))
        {
            return null;
        }

        var rows = await db.PoVendorByItems.AsNoTracking()
            .Where(x =>
                x.CompanyCode == companyCode
                && x.ICode == iCode
                && x.Vendor == vendorCd
                && x.PurUom == purchaseUom)
            .ToListAsync(cancellationToken);

        var active = rows
            .Where(x => !PoPrCalc.IsInactiveVendorItemStatus(x.Status))
            .OrderByDescending(x => string.Equals(x.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.ModifiedDate)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();

        return active?.UnitPrice;
    }

    private async Task<decimal> ResolveMoqAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string iCode,
        string? vendorCd,
        string? purchaseUom,
        ResolvedItem item,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(vendorCd) && !string.IsNullOrWhiteSpace(purchaseUom))
        {
            var rows = await db.PoVendorByItems.AsNoTracking()
                .Where(x =>
                    x.CompanyCode == companyCode
                    && x.ICode == iCode
                    && x.Vendor == vendorCd
                    && x.PurUom == purchaseUom)
                .ToListAsync(cancellationToken);

            var match = rows
                .Where(x => !PoPrCalc.IsInactiveVendorItemStatus(x.Status))
                .OrderByDescending(x => string.Equals(x.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.ModifiedDate)
                .ThenByDescending(x => x.Id)
                .FirstOrDefault();
            if (match is not null)
            {
                return match.OrdLevel;
            }
        }

        return item.IsIndirect ? item.Moq : 0m;
    }

    private static async Task StampAttachDocIdsInDbAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string tempDocId,
        string prNo,
        string userId,
        CancellationToken cancellationToken)
    {
        var rows = await db.PoPrAttachFiles
            .Where(x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.DocKey == AttachDocKey
                && x.DocId == tempDocId
                && x.CreatedBy == userId)
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            row.DocId = prNo;
        }
    }

    private async Task MoveDraftSafeAsync(
        string company,
        string branch,
        string tempDocId,
        string prNo,
        CancellationToken cancellationToken)
    {
        try
        {
            await _attachments.MoveDraftToPrAsync(company, branch, tempDocId, prNo, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "PR attachment move failed after commit. Company={Company} Branch={Branch} Temp={Temp} PrNo={PrNo}",
                company,
                branch,
                tempDocId,
                prNo);
        }
    }

    private async Task DiscardDraftSafeAsync(string? tempDocId, CancellationToken cancellationToken)
    {
        if (tempDocId is null)
        {
            return;
        }

        try
        {
            await _attachments.DiscardDraftAsync(tempDocId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PR draft discard failed. TempDocId={TempDocId}", tempDocId);
        }
    }

    private static string? NormalizeTempDocId(string? tempDocId)
    {
        var value = (tempDocId ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return null;
        }

        return value.StartsWith(TempDocIdPrefix, StringComparison.OrdinalIgnoreCase) ? value : null;
    }

    private static List<PoPrLineDto> StripEmptyLines(IReadOnlyList<PoPrLineDto>? lines) =>
        (lines ?? []).Where(x => x is not null && !IsEmptyLine(x)).ToList();

    private static bool IsEmptyLine(PoPrLineDto line) =>
        string.IsNullOrWhiteSpace(line.ICode)
        && string.IsNullOrWhiteSpace(line.IDesc)
        && string.IsNullOrWhiteSpace(line.VendorCd)
        && string.IsNullOrWhiteSpace(line.VendNm)
        && string.IsNullOrWhiteSpace(line.Purpose)
        && string.IsNullOrWhiteSpace(line.TaxGroup)
        && string.IsNullOrWhiteSpace(line.ToWarehouse)
        && string.IsNullOrWhiteSpace(line.PurchaseUom)
        && string.IsNullOrWhiteSpace(line.Currency)
        && string.IsNullOrWhiteSpace(line.RepairType)
        && line.PurchaseQty == 0m
        && line.Qty == 0m
        && line.UnitPrice == 0m
        && line.Amount == 0m
        && line.TaxAmount == 0m
        && line.Line <= 0;

    private static bool HasAnyPoNo(PoPr header) =>
        !string.IsNullOrWhiteSpace(header.PoNo)
        || header.Details.Any(x => !string.IsNullOrWhiteSpace(x.PoNo));

    private static async Task<Dictionary<string, decimal>> ComputeRemainingQtyByPrAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyList<string> prNos,
        CancellationToken cancellationToken)
    {
        var details = await db.PoPrDetails.AsNoTracking()
            .Where(x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && prNos.Contains(x.PrNo))
            .Select(x => new { x.PrNo, x.Line, x.PurchaseQty })
            .ToListAsync(cancellationToken);

        var consumed = await db.PoOrderDetails.AsNoTracking()
            .Where(d =>
                d.CompanyCode == companyCode
                && d.BranchCode == branchCode
                && d.PrNo != null
                && prNos.Contains(d.PrNo)
                && d.Order.Status != PoOrderStatuses.Cancelled)
            .GroupBy(d => new { d.PrNo, d.PrLineNo })
            .Select(g => new
            {
                PrNo = g.Key.PrNo!,
                PrLineNo = g.Key.PrLineNo,
                Qty = g.Sum(x => x.PoPurQty)
            })
            .ToListAsync(cancellationToken);

        var consumedByLine = consumed
            .Where(x => x.PrLineNo.HasValue)
            .ToDictionary(
                x => $"{x.PrNo}|{x.PrLineNo!.Value}",
                x => x.Qty,
                StringComparer.OrdinalIgnoreCase);

        return details
            .GroupBy(x => x.PrNo, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => PoPrCalc.RoundQty(g.Sum(d =>
                {
                    var used = consumedByLine.GetValueOrDefault($"{d.PrNo}|{d.Line}");
                    return Math.Max(0m, d.PurchaseQty - used);
                })),
                StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<(string PoNo, short PoRelNo)>> FindLivePoReferencesForPrAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string prNo,
        CancellationToken cancellationToken)
    {
        var rows = await db.PoOrderDetails.AsNoTracking()
            .Where(d =>
                d.CompanyCode == companyCode
                && d.BranchCode == branchCode
                && d.PrNo == prNo
                && d.Order.Status != PoOrderStatuses.Cancelled)
            .Select(d => new { d.PoNo, d.PoRelNo })
            .Distinct()
            .OrderBy(x => x.PoNo)
            .ThenBy(x => x.PoRelNo)
            .ToListAsync(cancellationToken);

        return rows
            .Select(x => (x.PoNo, x.PoRelNo))
            .ToList();
    }

    private static bool IsEditableStatus(string? status) =>
        string.Equals(status, PoPrStatuses.New, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, PoPrStatuses.Open, StringComparison.OrdinalIgnoreCase);

    private PoPrDocument MapDocument(PoPr header, bool canEdit, bool canDelete, bool canCancel)
    {
        var lines = header.Details
            .OrderBy(x => x.Line)
            .Select(MapLine)
            .ToList();
        var (gross, taxes, total) = PoPrCalc.SumTotals(lines.Select(x => (x.NetAmount, x.TaxAmount)));
        var currency = lines
            .OrderBy(x => x.Line)
            .Select(x => x.Currency)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var hasConsumed = lines.Any(x => x.IsConsumed) || !string.IsNullOrWhiteSpace(header.PoNo);
        var editable = IsEditableStatus(header.Status);
        var hasAnyPo = HasAnyPoNo(header);

        return new PoPrDocument
        {
            PrNo = header.PrNo,
            CreateDt = header.CreateDt,
            Status = header.Status,
            Requester = header.Requester,
            DeptCode = header.DeptCode,
            CheckedBy = header.CheckedBy,
            AuthorisedBy = header.AuthorisedBy,
            AuthorisedBy2nd = header.AuthorisedBy2nd,
            PrType = header.PrType,
            LocationCode = header.LocationCode,
            PoNo = header.PoNo,
            ApprReason = header.ApprReason,
            ProjId = header.ProjId,
            Remarks = header.Remarks,
            Currency = currency,
            Gross = gross,
            Taxes = taxes,
            Total = total,
            CreatedBy = header.CreatedBy,
            CreatedDate = header.CreatedDate,
            ModifiedBy = header.ModifiedBy,
            ModifiedDate = header.ModifiedDate,
            RowVersion = header.RowVersion ?? [],
            CanEdit = canEdit && editable,
            CanDelete = canDelete && editable && !hasAnyPo,
            CanCancel = canCancel && editable && !hasAnyPo,
            HasConsumedLines = hasConsumed,
            Lines = lines
        };
    }

    private static PoPrLineDto MapLine(PoPrDetail x)
    {
        var net = x.IsInclusive ? x.Amount - x.TaxAmount : x.Amount;
        return new PoPrLineDto
        {
            Line = x.Line,
            EtaDt = x.EtaDt,
            OneTimeItemYn = x.OneTimeItemYn,
            ICode = x.ICode,
            IDesc = x.IDesc,
            Category = x.Category,
            Qty = x.Qty,
            PackSz = x.PackSz,
            StdUom = x.StdUom,
            PurchaseQty = x.PurchaseQty,
            PurchaseUom = x.PurchaseUom,
            Currency = x.Currency,
            UnitPrice = x.UnitPrice,
            OneTimeVendor = x.OneTimeVendor,
            VendorCd = x.VendorCd,
            VendNm = x.VendNm,
            Purpose = x.Purpose,
            Status = x.Status,
            StdQty = x.StdQty,
            WtQty = x.WtQty,
            WtUom = x.WtUom,
            PaymentTerm = x.PaymentTerm,
            BuyingTerm = x.BuyingTerm,
            RepairType = x.RepairType,
            Amount = x.Amount,
            PoNo = x.PoNo,
            TaxGroup = x.TaxGroup,
            TaxAmount = x.TaxAmount,
            IsInclusive = x.IsInclusive,
            ToWarehouse = x.ToWarehouse,
            SoNo = x.SoNo,
            SoLine = x.SoLine,
            NetAmount = net
        };
    }

    private static void TouchRowVersion(AppDbContext db, PoPr header)
    {
        if (!db.Database.IsSqlServer())
        {
            header.RowVersion = Guid.NewGuid().ToByteArray();
        }
    }

    private Task<bool> CanAsync(string permission, CancellationToken cancellationToken) =>
        _accessRights.CanAsync(MenuCodes.PurchaseRequisition, permission, cancellationToken);

    private UserContext ValidateUserContext()
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return UserContext.Fail("Invalid company or branch context.");
        }

        return UserContext.Ok(scope.CompanyCode, scope.BranchCode, scope.LocationCode, scope.UserId);
    }

    private UserContext ValidateWriteContext()
    {
        var scope = _tenant.TryWriteScope();
        if (scope is null)
        {
            return UserContext.Fail("Invalid company, branch, or location context.");
        }

        return UserContext.Ok(scope.CompanyCode, scope.BranchCode!, scope.LocationCode!, scope.UserId);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string? TruncateOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static bool RowVersionsEqual(byte[]? left, byte[]? right) =>
        left is not null && right is not null && left.SequenceEqual(right);

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        if (ex.InnerException is SqlException sql)
        {
            return sql.Number is 2601 or 2627;
        }

        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PrepareOutcome
    {
        public string? Error { get; init; }
        public PoPrErrorKind Kind { get; init; }
        public IReadOnlyDictionary<string, string> Errors { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<PreparedLine>? Lines { get; init; }

        public static PrepareOutcome Ok(List<PreparedLine> lines) => new() { Lines = lines };

        public static PrepareOutcome Validation(string message, IReadOnlyDictionary<string, string> errors) =>
            new() { Error = message, Kind = PoPrErrorKind.Validation, Errors = errors };

        public PoPrOperationResult ToFail() =>
            Kind == PoPrErrorKind.Validation
                ? PoPrOperationResult.FailValidation(ValidationMessageFormat.ResolveServiceMessage(Errors, Error), Errors)
                : PoPrOperationResult.Fail(Error ?? "Unable to save the Purchase Requisition.", Kind);
    }

    private sealed class PreparedLine
    {
        public int RequestOrder { get; init; }
        public short? ExistingLineNo { get; init; }
        public bool IsConsumed { get; init; }
        public DateTime? EtaDt { get; init; }
        public bool? OneTimeItemYn { get; init; }
        public string ICode { get; init; } = string.Empty;
        public string? IDesc { get; init; }
        public string? Category { get; init; }
        public decimal Qty { get; init; }
        public decimal PackSz { get; init; }
        public string? StdUom { get; init; }
        public decimal PurchaseQty { get; init; }
        public string? PurchaseUom { get; init; }
        public string? Currency { get; init; }
        public decimal UnitPrice { get; init; }
        public bool? OneTimeVendor { get; init; }
        public string? VendorCd { get; init; }
        public string? VendNm { get; init; }
        public string? Purpose { get; init; }
        public string? Status { get; init; }
        public decimal StdQty { get; init; }
        public decimal WtQty { get; init; }
        public string? WtUom { get; init; }
        public string? PaymentTerm { get; init; }
        public string? BuyingTerm { get; init; }
        public string? RepairType { get; init; }
        public decimal Amount { get; init; }
        public string? TaxGroup { get; init; }
        public decimal TaxAmount { get; init; }
        public bool IsInclusive { get; init; }
        public string? ToWarehouse { get; init; }
        public string? SoNo { get; init; }
        public int? SoLine { get; init; }
        public decimal NetAmount { get; init; }
        public string? PoNo { get; init; }
    }

    private sealed record ResolvedItem(
        bool IsIndirect,
        string ICode,
        string? IDesc,
        string? PurchaseUom,
        string? StdUom,
        decimal PackSz,
        decimal UnitPrice,
        string? TaxGroup,
        string? Category,
        string? VendorCd,
        decimal Moq,
        string? DefWarehouse);

    private readonly record struct UserContext(
        string? Error,
        string? CompanyCode,
        string? BranchCode,
        string? LocationCode,
        string? UserId)
    {
        public static UserContext Fail(string error) => new(error, null, null, null, null);

        public static UserContext Ok(string company, string? branch, string? location, string user) =>
            new(null, company, branch, location, user);
    }
}
