using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
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
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Purchase;

public sealed class PoOrderService : IPoOrderService
{
	private sealed class PrepareOutcome
	{
		public string? Error { get; init; }

		public PoOrderErrorKind Kind { get; init; }

		public IReadOnlyDictionary<string, string> Errors { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public List<PreparedLine>? Lines { get; init; }

		public static PrepareOutcome Ok(List<PreparedLine> lines)
		{
			return new PrepareOutcome
			{
				Lines = lines
			};
		}

		public static PrepareOutcome Validation(string message, IReadOnlyDictionary<string, string> errors)
		{
			return new PrepareOutcome
			{
				Error = message,
				Kind = PoOrderErrorKind.Validation,
				Errors = errors
			};
		}

		public PoOrderOperationResult ToFail()
		{
			return (Kind == PoOrderErrorKind.Validation)
				? PoOrderOperationResult.FailValidation(ValidationMessageFormat.ResolveServiceMessage(Errors, Error), Errors)
				: PoOrderOperationResult.Fail(Error ?? "Unable to save the Purchase Order.", Kind);
		}
	}

	private sealed class PreparedLine
	{
		public int RequestOrder { get; init; }

		public short? ExistingLineNo { get; init; }

		public string? PrNo { get; init; }

		public short? PrLineNo { get; init; }

		public bool? OneTime { get; init; }

		public string ICode { get; init; } = string.Empty;

		public string? IDesc { get; init; }

		public string? IType { get; init; }

		public decimal PoUnitPrice { get; init; }

		public decimal PoQty { get; init; }

		public decimal PoPurQty { get; init; }

		public decimal WtQty { get; init; }

		public decimal Amount { get; init; }

		public decimal RecvQty { get; init; }

		public decimal ReturnQty { get; init; }

		public decimal BalanceQty { get; init; }

		public decimal OverRecvQty { get; init; }

		public decimal InvoicedQty { get; init; }

		public decimal PackSz { get; init; }

		public string? StdUom { get; init; }

		public string? WtUom { get; init; }

		public string? PurchaseUom { get; init; }

		public DateTime? EtaDate { get; init; }

		public string? CurCode { get; init; }

		public string? Remarks { get; init; }

		public string? PoDesc { get; init; }

		public DateTime? RecvDate { get; init; }

		public string? RepairType { get; init; }

		public decimal Discount { get; init; }

		public decimal ItemDiscount { get; init; }

		public string? DiscountType { get; init; }

		public decimal NetAmount { get; init; }

		public decimal ItemDiscount1 { get; init; }

		public string? DiscountType1 { get; init; }

		public string? CjNo { get; init; }

		public int? CjRelNo { get; init; }

		public int? CjLine { get; init; }

		public string? ProjId { get; init; }

		public string? VendorPartNo { get; init; }

		public string? TaxGroup { get; init; }

		public decimal TaxAmount { get; init; }

		public bool IsInclusive { get; init; }

		public string? ToWarehouse { get; init; }

		public string? Requester { get; init; }
	}

	private sealed record ResolvedItem(bool IsIndirect, string ICode, string? IDesc, string? IType, string? PurchaseUom, string? StdUom, decimal PackSz, decimal UnitPrice, string? TaxGroup, string? Category, string? VendorPartNo, string? Currency, string? DefWarehouse)
	{
		public string? PoDesc => IDesc;
	}

	private readonly record struct UserContext(string? Error, string? CompanyCode, string? BranchCode, string? LocationCode, string? UserId)
	{
		public static UserContext Fail(string error)
		{
			return new UserContext(error, null, null, null, null);
		}

		public static UserContext Ok(string company, string? branch, string? location, string user)
		{
			return new UserContext(null, company, branch, location, user);
		}
	}

	public const string AttachDocKey = "PO";

	public const string TempDocIdPrefix = "POTMP-";

	private readonly IDbContextFactory<AppDbContext> _dbFactory;

	private readonly IInventoryTenantContext _tenant;

	private readonly IAccessRightService _accessRights;

	private readonly IDocumentNumberingService _documentNumbers;

	private readonly ICurrentDateService _dates;

	private readonly IPoOrderRepository _repository;

	private readonly PoOrderOptions _options;

	private readonly IPoOrderAttachmentService _attachments;

	private readonly ILogger<PoOrderService> _logger;

	public PoOrderService(IDbContextFactory<AppDbContext> dbFactory, IInventoryTenantContext tenant, IAccessRightService accessRights, IDocumentNumberingService documentNumbers, ICurrentDateService dates, IPoOrderRepository repository, IOptions<PoOrderOptions> options, IPoOrderAttachmentService attachments, ILogger<PoOrderService> logger)
	{
		_dbFactory = dbFactory;
		_tenant = tenant;
		_accessRights = accessRights;
		_documentNumbers = documentNumbers;
		_dates = dates;
		_repository = repository;
		_options = options.Value;
		_attachments = attachments;
		_logger = logger;
	}

	public async Task<PoOrderOperationResult> CreateTempDocIdAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		UserContext context = ValidateWriteContext();
		if (context.Error != null)
		{
			return PoOrderOperationResult.Fail(context.Error);
		}
		if (!(await CanAsync("ADD", cancellationToken)))
		{
			return PoOrderOperationResult.Fail("Not authorized.", PoOrderErrorKind.Authorization);
		}
		return PoOrderOperationResult.OkTempDocId($"{"POTMP-"}{Guid.NewGuid():D}");
	}

	public async Task<PoOrderOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		UserContext context = ValidateUserContext();
		if (context.Error != null)
		{
			return PoOrderOperationResult.Fail(context.Error);
		}
		if (!(await CanAsync("ACCESS", cancellationToken)))
		{
			return PoOrderOperationResult.Fail("Not authorized.", PoOrderErrorKind.Authorization);
		}
		PoOrderOperationResult result;
		await using (AppDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken))
		{
			string company = context.CompanyCode;
			string branch = context.BranchCode;
			List<PoOrderItemLookupRow> direct = await (from x in db.IvStockMasters.AsNoTracking()
				where x.CompanyCode == company && x.IsActive
				orderby x.ICode
				select new PoOrderItemLookupRow
				{
					ICode = x.ICode,
					IDesc = x.IDesc,
					IsIndirect = false,
					IType = x.IType,
					PurchaseUom = x.PurUom,
					StdUom = x.StdUom,
					PackSz = (x.PurStdPackSize ?? x.StdPackSize ?? 1m),
					UnitPrice = x.PurchasePrice,
					TaxGroup = (x.PurchaseTaxGroup ?? x.TaxGroup),
					DefWarehouse = x.DefWarehouse
				}).ToListAsync(cancellationToken);
			List<PoOrderItemLookupRow> indirect = await (from x in db.PoPurItems.AsNoTracking()
				where x.CompanyCode == company
				orderby x.ICode
				select new PoOrderItemLookupRow
				{
					ICode = x.ICode,
					IDesc = x.IDesc,
					IsIndirect = true,
					PurchaseUom = x.PurUom,
					PackSz = 1m,
					UnitPrice = x.UnitPrice,
					Category = x.Category,
					Moq = x.Moq
				}).ToListAsync(cancellationToken);
			List<PoOrderVendorLookupRow> vendors = await (from x in db.PoSuppliers.AsNoTracking()
				where x.CompanyCode == company && x.BranchCode == branch && x.IsActive
				orderby x.SuppCode
				select new PoOrderVendorLookupRow
				{
					SuppCode = x.SuppCode,
					SuppName = x.SuppName,
					Currency = x.Currency,
					PoPrefix = x.PoPrefix
				}).ToListAsync(cancellationToken);
			List<PoOrderTaxGroupLookupRow> taxGroups = await (from x in db.SaTaxGroups.AsNoTracking()
				where x.CompanyCode == company
				orderby x.TaxGrCode
				select new PoOrderTaxGroupLookupRow
				{
					TaxGrCode = x.TaxGrCode,
					TaxGrDesc = x.TaxGrDesc,
					Percentage = x.Percentage
				}).ToListAsync(cancellationToken);
			List<PoOrderCodeLookupRow> currencies = await (from x in db.SaCurrencies.AsNoTracking()
				where x.CompanyCode == company && (x.IsActive == (bool?)null || x.IsActive == (bool?)true)
				orderby x.CurrCode
				select new PoOrderCodeLookupRow
				{
					Code = x.CurrCode,
					Name = x.CurrDesc
				}).ToListAsync(cancellationToken);
			List<PoOrderCodeLookupRow> buyingTerms = await (from x in db.PoBuyingTerms.AsNoTracking()
				where x.CompanyCode == company && x.IsActive
				orderby x.BuyingTerm
				select new PoOrderCodeLookupRow
				{
					Code = x.BuyingTerm,
					Name = x.Description
				}).ToListAsync(cancellationToken);
			List<PoOrderCodeLookupRow> paymentTerms = await (from x in db.IvMsCodes.AsNoTracking()
				where x.CodeType == "PAYCODE"
				orderby x.Code
				select new PoOrderCodeLookupRow
				{
					Code = x.Code,
					Name = x.Name
				}).ToListAsync(cancellationToken);
			List<PoOrderCodeLookupRow> buyers = await (from x in db.PoBuyers.AsNoTracking()
				where x.CompanyCode == company && x.IsActive
				orderby x.BuyerCode
				select new PoOrderCodeLookupRow
				{
					Code = x.BuyerCode,
					Name = (x.BuyerName ?? x.BuyerDesc)
				}).ToListAsync(cancellationToken);
			List<IvWarehouseLookupRow> warehouses = await (from x in db.IvWarehouses.AsNoTracking()
				where x.CompanyCode == company && x.BranchCode == branch && x.IsActive
				orderby x.WarehouseCode
				select new IvWarehouseLookupRow
				{
					WarehouseCode = x.WarehouseCode,
					WarehouseDesc = x.WarehouseDesc
				}).ToListAsync(cancellationToken);
			// Department / Project masters — company + branch scoped, active only.
			List<PoOrderCodeLookupRow> departments = await (from x in db.MsDepts.AsNoTracking()
				where x.CompanyCode == company && x.BranchCode == branch && x.IsActive
				orderby x.DeptCode
				select new PoOrderCodeLookupRow
				{
					Code = x.DeptCode,
					Name = x.DeptName
				}).ToListAsync(cancellationToken);
			List<PoOrderCodeLookupRow> projects = await (from x in db.MsProjects.AsNoTracking()
				where x.CompanyCode == company && x.BranchCode == branch && x.Status == MsProjectStatus.Active
				orderby x.ProjCode
				select new PoOrderCodeLookupRow
				{
					Code = x.ProjCode,
					Name = x.ProjName
				}).ToListAsync(cancellationToken);
			PoOrderLookups poOrderLookups = new PoOrderLookups
			{
				DirectItems = direct,
				IndirectItems = indirect,
				Vendors = vendors,
				TaxGroups = taxGroups,
				Currencies = currencies,
				BuyingTerms = buyingTerms,
				PaymentTerms = paymentTerms,
				Buyers = buyers,
				Warehouses = warehouses,
				Departments = departments,
				Projects = projects,
				DefaultInclusive = _options.PurchaseItemTaxInclusive,
				UseWeight = _options.UseWeight,
				CanViewCost = await CanAsync("VIEW_COST", cancellationToken)
			};
			result = PoOrderOperationResult.OkLookups(poOrderLookups);
		}
		return result;
	}

	public async Task<PoOrderOperationResult> GetSupplierDefaultsAsync(string vendCode, CancellationToken cancellationToken = default(CancellationToken))
	{
		UserContext context = ValidateUserContext();
		if (context.Error != null)
		{
			return PoOrderOperationResult.Fail(context.Error);
		}
		if (!(await CanAsync("ACCESS", cancellationToken)))
		{
			return PoOrderOperationResult.Fail("Not authorized.", PoOrderErrorKind.Authorization);
		}
		string code = (vendCode ?? string.Empty).Trim();
		if (code.Length == 0)
		{
			return PoOrderOperationResult.FailValidation("Supplier code is required.");
		}
		PoOrderOperationResult result;
		await using (AppDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken))
		{
			PoSupplier supplier = await db.PoSuppliers.AsNoTracking().Include((PoSupplier x) => x.Addresses).FirstOrDefaultAsync((PoSupplier x) => x.CompanyCode == ((UserContext)context).CompanyCode && x.BranchCode == ((UserContext)context).BranchCode && x.SuppCode == code && x.IsActive, cancellationToken);
			result = ((supplier != null) ? PoOrderOperationResult.OkSupplierDefaults(new PoOrderSupplierDefaults
			{
				VendCode = supplier.SuppCode,
				VendName = supplier.SuppName,
				CurCode = supplier.Currency,
				TermCode = supplier.PayCode,
				BuyingTerm = supplier.BuyingTerm,
				TaxGrpCode = (supplier.TaxGrCode ?? supplier.TaxGroup),
				PoPrefix = supplier.PoPrefix,
				ContactPerson = supplier.ContactPerson,
				Email = (supplier.Email ?? supplier.ContactEmail),
				Website = supplier.Website,
				RegNo = (supplier.SupplierBrn ?? supplier.GstregNo),
				VendAddress1 = supplier.Address1,
				VendAddress2 = supplier.Address2,
				VendAddress3 = supplier.Address3,
				VendAddress4 = supplier.Address4,
				VendCity = supplier.City,
				VendState = supplier.State,
				VendPostal = supplier.PostalCode,
				VendCountryCode = (supplier.CountryCode ?? supplier.Country),
				VendTel = supplier.Tel,
				VendFax = supplier.Fax,
				ShipToAddresses = (from x in supplier.Addresses
					orderby x.Line
					select new PoOrderShipToLookupRow
					{
						Line = x.Line,
						Name = x.SuppName,
						Address1 = x.Address1,
						Address2 = x.Address2,
						Address3 = x.Address3,
						Address4 = x.Address4,
						City = x.City,
						State = x.State,
						Postal = x.PostalCode,
						CountryCode = x.Country,
						Tel = x.Tel,
						Fax = x.Fax
					}).ToList()
			}) : PoOrderOperationResult.Fail("Supplier was not found.", PoOrderErrorKind.NotFound));
		}
		return result;
	}

	public async Task<PoOrderOperationResult> SearchAsync(PoOrderListQuery query, CancellationToken cancellationToken = default(CancellationToken))
	{
		UserContext context = ValidateUserContext();
		if (context.Error != null)
		{
			return PoOrderOperationResult.Fail(context.Error);
		}
		if (!(await CanAsync("ACCESS", cancellationToken)))
		{
			return PoOrderOperationResult.Fail("Not authorized.", PoOrderErrorKind.Authorization);
		}
		if (query == null)
		{
			query = new PoOrderListQuery();
		}
		string uid = Truncate(context.UserId, 20);
		PoOrderOperationResult result;
		await using (AppDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken))
		{
			(IReadOnlyList<PoOrder> Rows, int TotalCount) tuple = await _repository.SearchLatestPagedAsync(db, context.CompanyCode, context.BranchCode, new PoOrderSearchArgs(string.IsNullOrWhiteSpace(query.SearchText) ? null : query.SearchText.Trim(), string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim(), query.DateFrom, query.DateTo, _options.SelfViewEdit ? uid : null, query.SortField, query.SortDescending, query.Skip, query.Take), cancellationToken);
			IReadOnlyList<PoOrder> rows = tuple.Rows;
			int total = tuple.TotalCount;
			IReadOnlyDictionary<string, string?> itemTypes = await LoadItemTypesAsync(db, context.CompanyCode, from x in rows.SelectMany((PoOrder x) => x.Details)
				select x.ICode, cancellationToken);
			bool canEdit = await CanAsync("EDIT", cancellationToken);
			bool canDelete = await CanAsync("DELETE", cancellationToken);
			bool canCancel = await CanAsync("CANCEL", cancellationToken);
			bool canRevise = canEdit;
			bool canClose = await CanAsync("CLOSE", cancellationToken);
			bool canReopen = await CanAsync("REOPEN", cancellationToken);
			result = PoOrderOperationResult.OkList(new PoOrderListPage
			{
				TotalCount = total,
				Rows = rows.Select((PoOrder x) => MapListRow(x, itemTypes, canEdit, canDelete, canCancel, canRevise, canClose, canReopen)).ToList()
			});
		}
		return result;
	}

	public Task<PoOrderOperationResult> GetAsync(string poNo, CancellationToken cancellationToken = default(CancellationToken))
	{
		return GetCoreAsync(poNo, null, cancellationToken);
	}

	public Task<PoOrderOperationResult> GetAsync(string poNo, short poRelNo, CancellationToken cancellationToken = default(CancellationToken))
	{
		return GetCoreAsync(poNo, poRelNo, cancellationToken);
	}

	public async Task<PoOrderOperationResult> GetReviseDraftAsync(string poNo, CancellationToken cancellationToken = default(CancellationToken))
	{
		PoOrderOperationResult result = await GetCoreAsync(poNo, null, cancellationToken);
		if (!result.Succeeded || result.Document == null)
		{
			return result;
		}
		PoOrderDocument source = result.Document;
		if (!source.IsLatest || !string.Equals(source.Status, "NEW", StringComparison.OrdinalIgnoreCase))
		{
			return PoOrderOperationResult.Fail("Only the latest NEW Purchase Order can be revised.");
		}
		return PoOrderOperationResult.OkDocument(CloneDraft(source, source.PoNo, (short)(source.PoRelNo + 1), resetPrAndReceive: false));
	}

	public async Task<PoOrderOperationResult> SearchPrForPoAsync(string? vendorCd, string? searchText, CancellationToken cancellationToken = default(CancellationToken))
	{
		UserContext context = ValidateUserContext();
		if (context.Error != null)
		{
			return PoOrderOperationResult.Fail(context.Error);
		}
		if (!(await CanAsync("ACCESS", cancellationToken)))
		{
			return PoOrderOperationResult.Fail("Not authorized.", PoOrderErrorKind.Authorization);
		}
		PoOrderOperationResult result;
		await using (AppDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken))
		{
			string company = context.CompanyCode;
			string branch = context.BranchCode;
			string vendor = (vendorCd ?? string.Empty).Trim();
			string term = (searchText ?? string.Empty).Trim();
			IQueryable<PoPr> query = from x in db.PoPrs.AsNoTracking()
				where x.CompanyCode == company && x.BranchCode == branch
					&& (x.Status == PoPrStatuses.New
						|| x.Status == PoPrStatuses.Approved
						|| x.Status == PoPrStatuses.PartiallyOrdered)
				select x;
			if (vendor.Length > 0)
			{
				query = query.Where((PoPr x) => x.Details.Any((PoPrDetail d) => d.VendorCd == vendor));
			}
			if (term.Length > 0)
			{
				query = query.Where((PoPr x) => x.PrNo.Contains(term) || (x.Requester != null && x.Requester.Contains(term)) || (x.Remarks != null && x.Remarks.Contains(term)) || x.Details.Any((PoPrDetail d) => (d.ICode != null && d.ICode.Contains(term)) || (d.IDesc != null && d.IDesc.Contains(term))));
			}
			result = PoOrderOperationResult.OkPrRows(await (from x in (from x in query
					orderby x.CreateDt descending, x.PrNo descending
					select x).Take(100)
				select new PoPrForPoRow
				{
					PrNo = x.PrNo,
					CreateDt = x.CreateDt,
					Status = x.Status,
					Requester = x.Requester,
					ProjId = x.ProjId,
					Remarks = x.Remarks
				}).ToListAsync(cancellationToken));
		}
		return result;
	}

	public async Task<PoOrderOperationResult> GetPrRemainingLinesAsync(string prNo, string? vendorCd = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		UserContext context = ValidateUserContext();
		if (context.Error != null)
		{
			return PoOrderOperationResult.Fail(context.Error);
		}
		if (!(await CanAsync("ACCESS", cancellationToken)))
		{
			return PoOrderOperationResult.Fail("Not authorized.", PoOrderErrorKind.Authorization);
		}
		string no = (prNo ?? string.Empty).Trim();
		if (no.Length == 0)
		{
			return PoOrderOperationResult.FailValidation("PR number is required.");
		}
		PoOrderOperationResult result;
		await using (AppDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken))
		{
			PoPr header = await db.PoPrs.AsNoTracking().FirstOrDefaultAsync((PoPr x) => x.CompanyCode == context.CompanyCode && x.BranchCode == context.BranchCode && x.PrNo == no, cancellationToken);
			if (header == null)
			{
				result = PoOrderOperationResult.Fail("Purchase Requisition was not found.", PoOrderErrorKind.NotFound);
			}
			else if (!PoPrCalc.IsAvailableForPo(header.Status))
			{
				result = PoOrderOperationResult.Fail("PR " + no + " is not available for PO.");
			}
			else
			{
				result = PoOrderOperationResult.OkPrRemainingLines((await BuildPrRemainingLinesAsync(db, context.CompanyCode, context.BranchCode, no, vendorCd, null, cancellationToken)).Where((PoPrRemainingLineDto x) => x.RemainingQty > 0m).ToList());
			}
		}
		return result;
	}

	public async Task<PoOrderOperationResult> SaveNewAsync(PoOrderSaveRequest? request, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (request == null)
		{
			return PoOrderOperationResult.FailValidation("Save request is required.");
		}
		UserContext context = ValidateWriteContext();
		if (context.Error != null)
		{
			return PoOrderOperationResult.Fail(context.Error);
		}
		if (!(await CanAsync("ADD", cancellationToken)))
		{
			return PoOrderOperationResult.Fail("Not authorized.", PoOrderErrorKind.Authorization);
		}
		string tempDocId = NormalizeTempDocId(request.TempDocId);
		PoOrderOperationResult result;
		await using (AppDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken))
		{
			PoOrderOperationResult poOrderOperationResult;
			await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync(cancellationToken))
			{
				try
				{
					// Legacy-aware Department / Project validation (shared rule — see MsRefLookupRules).
					Dictionary<string, string> refErrors = await ValidateDeptProjectAsync(
						db, context.CompanyCode, context.BranchCode, priorDeptCode: null, priorProjId: null,
						request.DeptCode, request.ProjId, cancellationToken);
					if (refErrors != null)
					{
						await tx.RollbackAsync(cancellationToken);
						await DiscardDraftSafeAsync(tempDocId, cancellationToken);
						poOrderOperationResult = PoOrderOperationResult.FailValidation(ValidationMessageFormat.JoinMessages(refErrors), refErrors);
					}
					else
					{
					PrepareOutcome prepared = await PrepareLinesAsync(db, context.CompanyCode, context.BranchCode, request, null, cancellationToken);
					if (prepared.Error != null)
					{
						await tx.RollbackAsync(cancellationToken);
						await DiscardDraftSafeAsync(tempDocId, cancellationToken);
						poOrderOperationResult = prepared.ToFail();
					}
					else
					{
						await LockConsumptionAsync(db, context.CompanyCode, context.BranchCode, Array.Empty<PoOrderDetail>(), prepared.Lines, cancellationToken);
						PoOrderOperationResult consumeError = await ValidateAndApplyPrConsumptionAsync(db, context.CompanyCode, context.BranchCode, null, prepared.Lines, cancellationToken);
						if (consumeError != null)
						{
							await tx.RollbackAsync(cancellationToken);
							await DiscardDraftSafeAsync(tempDocId, cancellationToken);
							poOrderOperationResult = consumeError;
						}
						else
						{
							ApplyCjConsumption(Array.Empty<PreparedLine>(), prepared.Lines, -1, out string cjError);
							if (cjError != null)
							{
								await tx.RollbackAsync(cancellationToken);
								await DiscardDraftSafeAsync(tempDocId, cancellationToken);
								poOrderOperationResult = PoOrderOperationResult.Fail(cjError);
							}
							else
							{
								DateTime poDate = request.PoDate?.Date ?? _dates.Today.Date;
								string supplierPrefix = await ResolveSupplierPrefixAsync(db, context.CompanyCode, context.BranchCode, request.VendCode, cancellationToken);
								DocumentNumberResult issued;
								try
								{
									issued = await _documentNumbers.NextAsync(db, "PO", supplierPrefix ?? string.Empty, poDate, DocumentNumberRequestMode.New, "AUTO", cancellationToken);
								}
								catch (DocumentNumberingNotConfiguredException)
								{
									await tx.RollbackAsync(cancellationToken);
									await DiscardDraftSafeAsync(tempDocId, cancellationToken);
									poOrderOperationResult = PoOrderOperationResult.Fail("PO numbering is not configured for this company/branch.");
									goto end_IL_05b5;
								}
								catch (DocumentNumberingConfigurationException)
								{
									await tx.RollbackAsync(cancellationToken);
									await DiscardDraftSafeAsync(tempDocId, cancellationToken);
									poOrderOperationResult = PoOrderOperationResult.Fail("PO numbering is not configured correctly. Contact an administrator.");
									goto end_IL_05b5;
								}
								catch (DocumentNumberingOverflowException)
								{
									await tx.RollbackAsync(cancellationToken);
									await DiscardDraftSafeAsync(tempDocId, cancellationToken);
									poOrderOperationResult = PoOrderOperationResult.Fail("The next PO number exceeds the configured length.");
									goto end_IL_05b5;
								}
								catch (DocumentNumberingConcurrencyException)
								{
									await tx.RollbackAsync(cancellationToken);
									await DiscardDraftSafeAsync(tempDocId, cancellationToken);
									poOrderOperationResult = PoOrderOperationResult.Fail("The PO could not be saved because of a database conflict. Try again.", PoOrderErrorKind.Unexpected);
									goto end_IL_05b5;
								}
								string uid = Truncate(context.UserId, 20);
								PoOrder header = new PoOrder
								{
									CompanyCode = context.CompanyCode,
									BranchCode = context.BranchCode,
									PoNo = issued.DocumentNumber,
									PoRelNo = 1,
									Status = "NEW",
									CreatedDate = _dates.Now,
									CreatedBy = uid,
									LocationCode = TruncateOptional(context.LocationCode, 10),
									Prefix = TruncateOptional(issued.PrefixUsed ?? supplierPrefix, 20),
									PrintCounter = 0
								};
								ApplyHeaderRequest(header, request, poDate);
								short lineNo = 1;
								foreach (PreparedLine line in prepared.Lines)
								{
									header.Details.Add(ToDetailEntity(header, line, lineNo++));
								}
								if (tempDocId != null)
								{
									await StampAttachDocIdsInDbAsync(db, context.CompanyCode, context.BranchCode, tempDocId, header.PoNo, header.PoRelNo, uid, cancellationToken);
								}
								TouchRowVersion(db, header);
								db.PoOrders.Add(header);
								// Persist PO lines first so LivePoConsumedForPrAsync (and PR stamps) see this document.
								await db.SaveChangesAsync(cancellationToken);
								await StampPrConsumptionAsync(db, context.CompanyCode, context.BranchCode, CollectPrKeys(header.Details), header.PoNo, cancellationToken);
								await db.SaveChangesAsync(cancellationToken);
								await tx.CommitAsync(cancellationToken);
								if (tempDocId != null)
								{
									await MoveDraftSafeAsync(context.CompanyCode, context.BranchCode, tempDocId, header.PoNo, cancellationToken);
								}
								poOrderOperationResult = await GetAsync(header.PoNo, cancellationToken);
							}
						}
					}
					}
					end_IL_05b5:;
				}
				catch (SqlException ex5) when (ex5.Number == 1205)
				{
					_logger.LogWarning(ex5, "PO save deadlock.");
					await tx.RollbackAsync(cancellationToken);
					await DiscardDraftSafeAsync(tempDocId, cancellationToken);
					poOrderOperationResult = PoOrderOperationResult.Fail("The PO could not be saved because of a database conflict. Try again.", PoOrderErrorKind.Unexpected);
				}
				catch (DbUpdateException ex6) when (IsUniqueViolation(ex6))
				{
					await tx.RollbackAsync(cancellationToken);
					await DiscardDraftSafeAsync(tempDocId, cancellationToken);
					poOrderOperationResult = PoOrderOperationResult.Fail("PO number is already used.", PoOrderErrorKind.Unexpected);
				}
				catch (Exception exception)
				{
					_logger.LogError(exception, "PO save failed.");
					await tx.RollbackAsync(cancellationToken);
					await DiscardDraftSafeAsync(tempDocId, cancellationToken);
					poOrderOperationResult = PoOrderOperationResult.Fail("Unable to save the Purchase Order.", PoOrderErrorKind.Unexpected);
				}
			}
			result = poOrderOperationResult;
		}
		return result;
	}

	public Task<PoOrderOperationResult> UpdateAsync(string poNo, PoOrderSaveRequest? request, CancellationToken cancellationToken = default(CancellationToken))
	{
		return SaveExistingAsync(poNo, request, revise: false, cancellationToken);
	}

	public async Task<PoOrderOperationResult> CopyAsync(string sourcePoNo, CancellationToken cancellationToken = default(CancellationToken))
	{
		UserContext context = ValidateUserContext();
		if (context.Error != null)
		{
			return PoOrderOperationResult.Fail(context.Error);
		}
		if (!(await CanAsync("ADD", cancellationToken)))
		{
			return PoOrderOperationResult.Fail("Not authorized.", PoOrderErrorKind.Authorization);
		}
		PoOrderOperationResult result = await GetCoreAsync(sourcePoNo, null, cancellationToken);
		if (!result.Succeeded || result.Document == null)
		{
			return result;
		}
		return PoOrderOperationResult.OkDocument(CloneDraft(result.Document, "AUTO", 1, resetPrAndReceive: true));
	}

	public Task<PoOrderOperationResult> ReviseAsync(string poNo, PoOrderSaveRequest? request, CancellationToken cancellationToken = default(CancellationToken))
	{
		return SaveExistingAsync(poNo, request, revise: true, cancellationToken);
	}

	public async Task<PoOrderOperationResult> CancelAsync(PoOrderKeyedRequest request, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(request, "request");
		UserContext context = ValidateWriteContext();
		if (context.Error != null)
		{
			return PoOrderOperationResult.Fail(context.Error);
		}
		if (!(await CanAsync("CANCEL", cancellationToken)))
		{
			return PoOrderOperationResult.Fail("Not authorized.", PoOrderErrorKind.Authorization);
		}
		PoOrderOperationResult result;
		await using (AppDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken))
		{
			PoOrderOperationResult poOrderOperationResult;
			await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync(cancellationToken))
			{
				try
				{
					(PoOrder? Header, PoOrderOperationResult? Result) locked = await LockRequestedPoAsync(db, context, request, "cancelling", cancellationToken);
					if (locked.Result != null)
					{
						await tx.RollbackAsync(cancellationToken);
						poOrderOperationResult = locked.Result;
					}
					else
					{
						var (header, _) = locked;
						if (IsTerminal(header.Status) || header.Details.Any((PoOrderDetail x) => x.RecvQty > 0m))
						{
							await tx.RollbackAsync(cancellationToken);
							poOrderOperationResult = PoOrderOperationResult.Fail("Purchase Order cannot be cancelled in its current state.");
						}
						else
						{
							await LockConsumptionAsync(db, context.CompanyCode, context.BranchCode, header.Details, Array.Empty<PreparedLine>(), cancellationToken);
							ApplyCjConsumption(header.Details.Select(MapExistingLine).ToList(), Array.Empty<PreparedLine>(), 1, out string cjError);
							if (cjError != null)
							{
								await tx.RollbackAsync(cancellationToken);
								poOrderOperationResult = PoOrderOperationResult.Fail(cjError);
							}
							else
							{
								PoStatusPolicy.Cancel(header);
								header.ModifiedDate = _dates.Now;
								header.ModifiedBy = Truncate(context.UserId, 20);
								TouchRowVersion(db, header);
								await RecalculatePrStampsAsync(db, context.CompanyCode, context.BranchCode, CollectPrKeys(header.Details), cancellationToken);
								await db.SaveChangesAsync(cancellationToken);
								await tx.CommitAsync(cancellationToken);
								poOrderOperationResult = await GetAsync(header.PoNo, cancellationToken);
							}
						}
					}
				}
				catch (DbUpdateConcurrencyException)
				{
					await tx.RollbackAsync(cancellationToken);
					poOrderOperationResult = PoOrderOperationResult.Fail("This Purchase Order was changed by another user. Reload before cancelling.", PoOrderErrorKind.Concurrency);
				}
				catch (Exception exception)
				{
					_logger.LogError(exception, "PO cancel failed.");
					await tx.RollbackAsync(cancellationToken);
					poOrderOperationResult = PoOrderOperationResult.Fail("Unable to cancel the Purchase Order.", PoOrderErrorKind.Unexpected);
				}
			}
			result = poOrderOperationResult;
		}
		return result;
	}

	public async Task<PoOrderOperationResult> DeleteAsync(PoOrderKeyedRequest request, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(request, "request");
		UserContext context = ValidateWriteContext();
		if (context.Error != null)
		{
			return PoOrderOperationResult.Fail(context.Error);
		}
		if (!(await CanAsync("DELETE", cancellationToken)))
		{
			return PoOrderOperationResult.Fail("Not authorized.", PoOrderErrorKind.Authorization);
		}
		PoOrderOperationResult result;
		await using (AppDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken))
		{
			PoOrderOperationResult poOrderOperationResult;
			await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync(cancellationToken))
			{
				Array.Empty<(string, string)>();
				try
				{
					(PoOrder? Header, PoOrderOperationResult? Result) locked = await LockRequestedPoAsync(db, context, request, "deleting", cancellationToken);
					if (locked.Result != null)
					{
						await tx.RollbackAsync(cancellationToken);
						poOrderOperationResult = locked.Result;
					}
					else
					{
						var (header, _) = locked;
						if (!string.Equals(header.Status, "NEW", StringComparison.OrdinalIgnoreCase) && !string.Equals(header.Status, "OPEN", StringComparison.OrdinalIgnoreCase))
						{
							await tx.RollbackAsync(cancellationToken);
							poOrderOperationResult = PoOrderOperationResult.Fail("Only NEW or OPEN Purchase Orders can be deleted.");
						}
						else if (header.Details.Any((PoOrderDetail x) => x.RecvQty > 0m))
						{
							await tx.RollbackAsync(cancellationToken);
							poOrderOperationResult = PoOrderOperationResult.Fail("Received Purchase Order lines cannot be deleted.");
						}
						else
						{
							await LockConsumptionAsync(db, context.CompanyCode, context.BranchCode, header.Details, Array.Empty<PreparedLine>(), cancellationToken);
							ApplyCjConsumption(header.Details.Select(MapExistingLine).ToList(), Array.Empty<PreparedLine>(), 1, out string _);
							IReadOnlyList<(string PrNo, short Line)> prKeys = CollectPrKeys(header.Details);
							List<PoAttachFile> attachRows = await db.PoAttachFiles.Where((PoAttachFile x) => x.CompanyCode == ((UserContext)context).CompanyCode && x.BranchCode == ((UserContext)context).BranchCode && x.DocKey == "PO" && x.DocId == header.PoNo).ToListAsync(cancellationToken);
							IReadOnlyList<(string DocName, string? DocName2)> attachFiles = attachRows.Select((PoAttachFile x) => (DocName: x.DocName, DocPath: x.DocPath)).ToList();
							if (attachRows.Count > 0)
							{
								db.PoAttachFiles.RemoveRange(attachRows);
							}
							db.PoOrders.Remove(header);
							await RecalculatePrStampsAsync(db, context.CompanyCode, context.BranchCode, prKeys, cancellationToken);
							await db.SaveChangesAsync(cancellationToken);
							await tx.CommitAsync(cancellationToken);
							await _attachments.DeletePhysicalForDocAsync(context.CompanyCode, context.BranchCode, header.PoNo, attachFiles, cancellationToken);
							poOrderOperationResult = PoOrderOperationResult.Ok();
						}
					}
				}
				catch (DbUpdateConcurrencyException)
				{
					await tx.RollbackAsync(cancellationToken);
					poOrderOperationResult = PoOrderOperationResult.Fail("This Purchase Order was changed by another user. Reload before deleting.", PoOrderErrorKind.Concurrency);
				}
				catch (Exception exception)
				{
					_logger.LogError(exception, "PO delete failed.");
					await tx.RollbackAsync(cancellationToken);
					poOrderOperationResult = PoOrderOperationResult.Fail("Unable to delete the Purchase Order.", PoOrderErrorKind.Unexpected);
				}
			}
			result = poOrderOperationResult;
		}
		return result;
	}

	public async Task<PoOrderOperationResult> ForceCloseAsync(PoOrderKeyedRequest request, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(request, "request");
		UserContext context = ValidateWriteContext();
		if (context.Error != null)
		{
			return PoOrderOperationResult.Fail(context.Error);
		}
		if (!(await CanAsync("CLOSE", cancellationToken)))
		{
			return PoOrderOperationResult.Fail("Not authorized.", PoOrderErrorKind.Authorization);
		}
		string reason = (request.CloseReason ?? string.Empty).Trim();
		if (reason.Length == 0)
		{
			return PoOrderOperationResult.FailValidation("Close reason is required.", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["CloseReason"] = "Close reason is required."
			});
		}
		return await ChangeStatusAsync(request, context, forceClose: true, cancellationToken);
	}

	public async Task<PoOrderOperationResult> ReopenAsync(PoOrderKeyedRequest request, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(request, "request");
		UserContext context = ValidateWriteContext();
		if (context.Error != null)
		{
			return PoOrderOperationResult.Fail(context.Error);
		}
		if (!(await CanAsync("REOPEN", cancellationToken)))
		{
			return PoOrderOperationResult.Fail("Not authorized.", PoOrderErrorKind.Authorization);
		}
		return await ChangeStatusAsync(request, context, forceClose: false, cancellationToken);
	}

	private async Task<PoOrderOperationResult> SaveExistingAsync(string poNo, PoOrderSaveRequest? request, bool revise, CancellationToken cancellationToken)
	{
		if (request == null)
		{
			return PoOrderOperationResult.FailValidation("Save request is required.");
		}
		string no = (poNo ?? string.Empty).Trim();
		if (no.Length == 0)
		{
			return PoOrderOperationResult.FailValidation("PO number is required.");
		}
		UserContext context = ValidateWriteContext();
		if (context.Error != null)
		{
			return PoOrderOperationResult.Fail(context.Error);
		}
		if (!(await CanAsync("EDIT", cancellationToken)))
		{
			return PoOrderOperationResult.Fail("Not authorized.", PoOrderErrorKind.Authorization);
		}
		if (request.RowVersion == null || request.RowVersion.Length == 0)
		{
			return PoOrderOperationResult.Fail("This Purchase Order was changed by another user. Reload before saving.", PoOrderErrorKind.Concurrency);
		}
		string tempDocId = NormalizeTempDocId(request.TempDocId);
		PoOrderOperationResult result;
		await using (AppDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken))
		{
			PoOrderOperationResult poOrderOperationResult;
			await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync(cancellationToken))
			{
				try
				{
					PoOrder header = await _repository.LockLatestForUpdateAsync(db, context.CompanyCode, context.BranchCode, no, cancellationToken);
					IReadOnlyDictionary<string, string?> itemTypes;
					if (header == null)
					{
						await tx.RollbackAsync(cancellationToken);
						poOrderOperationResult = PoOrderOperationResult.Fail("Purchase Order was not found.", PoOrderErrorKind.NotFound);
					}
					else
					{
						await db.Entry(header).Collection((PoOrder x) => x.Details).LoadAsync(cancellationToken);
						if (!RowVersionsEqual(header.RowVersion, request.RowVersion))
						{
							await tx.RollbackAsync(cancellationToken);
							poOrderOperationResult = PoOrderOperationResult.Fail("This Purchase Order was changed by another user. Reload before saving.", PoOrderErrorKind.Concurrency);
						}
						else if (_options.SelfViewEdit && !string.Equals(header.CreatedBy, Truncate(context.UserId, 20), StringComparison.OrdinalIgnoreCase))
						{
							await tx.RollbackAsync(cancellationToken);
							poOrderOperationResult = PoOrderOperationResult.Fail("Purchase Order was not found.", PoOrderErrorKind.NotFound);
						}
						else
						{
							itemTypes = await LoadItemTypesAsync(db, context.CompanyCode, header.Details.Select((PoOrderDetail x) => x.ICode).Concat<string>(request.Lines.Select((PoOrderLineDto x) => x.ICode)), cancellationToken);
							if (IsForceClosed(header, itemTypes))
							{
								await tx.RollbackAsync(cancellationToken);
								poOrderOperationResult = PoOrderOperationResult.Fail("Force-closed Purchase Orders must be reopened before editing.");
							}
							else if (!string.Equals(header.Status, "NEW", StringComparison.OrdinalIgnoreCase) && !string.Equals(header.Status, "OPEN", StringComparison.OrdinalIgnoreCase) && !string.Equals(header.Status, "RECEIVED", StringComparison.OrdinalIgnoreCase))
							{
								await tx.RollbackAsync(cancellationToken);
								poOrderOperationResult = PoOrderOperationResult.Fail("Purchase Order cannot be edited in its current state.");
							}
							else
							{
								if (!revise)
								{
									goto IL_0d7e;
								}
								if (!string.Equals(header.Status, "NEW", StringComparison.OrdinalIgnoreCase))
								{
									await tx.RollbackAsync(cancellationToken);
									poOrderOperationResult = PoOrderOperationResult.Fail("Only the latest NEW Purchase Order can be revised.");
								}
								else
								{
									if (!string.IsNullOrWhiteSpace(request.RevisionReason))
									{
										goto IL_0d7e;
									}
									await tx.RollbackAsync(cancellationToken);
									poOrderOperationResult = PoOrderOperationResult.FailValidation("Revision reason is required.", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["RevisionReason"] = "Revision reason is required." });
								}
							}
						}
					}
					goto end_IL_0604;
					IL_0d7e:
					if (!string.Equals(header.VendCode?.Trim(), request.VendCode?.Trim(), StringComparison.OrdinalIgnoreCase) && header.Details.Count > 0 && !request.ClearLinesOnSupplierChange)
					{
						await tx.RollbackAsync(cancellationToken);
						poOrderOperationResult = PoOrderOperationResult.Fail("Changing supplier with lines requires ClearLinesOnSupplierChange.");
					}
					else if (!string.Equals(header.VendCode?.Trim(), request.VendCode?.Trim(), StringComparison.OrdinalIgnoreCase) && header.Details.Any((PoOrderDetail x) => x.RecvQty > 0m))
					{
						await tx.RollbackAsync(cancellationToken);
						poOrderOperationResult = PoOrderOperationResult.Fail("Supplier cannot be changed after receiving.");
					}
					else
					{
						db.Entry(header).Property((PoOrder x) => x.RowVersion).OriginalValue = request.RowVersion;
						Dictionary<short, PoOrderDetail> existingByLine = header.Details.ToDictionary((PoOrderDetail x) => x.Line, (PoOrderDetail x) => x);
						// Legacy-aware Department / Project validation (shared rule — see MsRefLookupRules).
						Dictionary<string, string> refErrors = await ValidateDeptProjectAsync(
							db, context.CompanyCode, context.BranchCode, header.DeptCode, header.ProjId,
							request.DeptCode, request.ProjId, cancellationToken);
						if (refErrors != null)
						{
							await tx.RollbackAsync(cancellationToken);
							poOrderOperationResult = PoOrderOperationResult.FailValidation(ValidationMessageFormat.JoinMessages(refErrors), refErrors);
						}
						else
						{
						PrepareOutcome prepared = await PrepareLinesAsync(db, context.CompanyCode, context.BranchCode, request, existingByLine, cancellationToken);
						if (prepared.Error != null)
						{
							await tx.RollbackAsync(cancellationToken);
							poOrderOperationResult = prepared.ToFail();
						}
						else
						{
							await LockConsumptionAsync(db, context.CompanyCode, context.BranchCode, header.Details, prepared.Lines, cancellationToken);
							PoOrderOperationResult consumeError = await ValidateAndApplyPrConsumptionAsync(db, context.CompanyCode, context.BranchCode, (header.PoNo, header.PoRelNo), prepared.Lines, cancellationToken);
							if (consumeError != null)
							{
								await tx.RollbackAsync(cancellationToken);
								poOrderOperationResult = consumeError;
							}
							else
							{
								List<PreparedLine> oldPrepared = header.Details.Select(MapExistingLine).ToList();
								ApplyCjConsumption(oldPrepared, prepared.Lines, 1, out string cjError);
								if (cjError != null)
								{
									await tx.RollbackAsync(cancellationToken);
									poOrderOperationResult = PoOrderOperationResult.Fail(cjError);
								}
								else
								{
									IReadOnlyList<(string PrNo, short Line)> oldPrKeys = CollectPrKeys(header.Details);
									if (revise)
									{
										PoOrder newHeader = new PoOrder
										{
											CompanyCode = header.CompanyCode,
											BranchCode = header.BranchCode,
											PoNo = header.PoNo,
											PoRelNo = (short)(header.PoRelNo + 1),
											Status = "NEW",
											CreatedDate = _dates.Now,
											CreatedBy = Truncate(context.UserId, 20),
											LocationCode = header.LocationCode,
											Prefix = header.Prefix,
											PrintCounter = 0
										};
										ApplyHeaderRequest(newHeader, request, request.PoDate?.Date ?? header.PoDate ?? _dates.Today.Date);
										short lineNo = 1;
										foreach (PreparedLine line in prepared.Lines)
										{
											newHeader.Details.Add(ToDetailEntity(newHeader, line, lineNo++));
										}
										PoStatusPolicy.Cancel(header);
										header.ModifiedDate = _dates.Now;
										header.ModifiedBy = Truncate(context.UserId, 20);
										TouchRowVersion(db, header);
										TouchRowVersion(db, newHeader);
										db.PoOrders.Add(newHeader);
										await StampAttachRevNoAsync(db, context.CompanyCode, context.BranchCode, newHeader.PoNo, newHeader.PoRelNo, cancellationToken);
										// Persist cancelled prior rev + new rev so LivePoConsumed sees CANCELLED exclude correctly.
										await db.SaveChangesAsync(cancellationToken);
										await RecalculatePrStampsAsync(db, context.CompanyCode, context.BranchCode, oldPrKeys.Concat(CollectPrKeys(newHeader.Details)).ToList(), cancellationToken);
										await StampPrConsumptionAsync(db, context.CompanyCode, context.BranchCode, CollectPrKeys(newHeader.Details), newHeader.PoNo, cancellationToken);
										await db.SaveChangesAsync(cancellationToken);
										await tx.CommitAsync(cancellationToken);
										poOrderOperationResult = await GetAsync(newHeader.PoNo, newHeader.PoRelNo, cancellationToken);
									}
									else
									{
										PoOrderOperationResult syncError = SynchronizeDetails(db, header, prepared.Lines, existingByLine);
										if (syncError != null)
										{
											await tx.RollbackAsync(cancellationToken);
											poOrderOperationResult = syncError;
										}
										else
										{
											ApplyHeaderRequest(header, request, request.PoDate?.Date ?? header.PoDate ?? _dates.Today.Date);
											header.Status = PoStatusPolicy.CalculateOperationalStatus(header, (PoOrderDetail d) => IsServiceLine(d, itemTypes));
											header.ModifiedDate = _dates.Now;
											header.ModifiedBy = Truncate(context.UserId, 20);
											if (tempDocId != null)
											{
												await StampAttachDocIdsInDbAsync(db, context.CompanyCode, context.BranchCode, tempDocId, header.PoNo, header.PoRelNo, Truncate(context.UserId, 20), cancellationToken);
											}
											TouchRowVersion(db, header);
											await db.SaveChangesAsync(cancellationToken);
											await RecalculatePrStampsAsync(db, context.CompanyCode, context.BranchCode, oldPrKeys.Concat(CollectPrKeys(header.Details)).ToList(), cancellationToken);
											await StampPrConsumptionAsync(db, context.CompanyCode, context.BranchCode, CollectPrKeys(header.Details), header.PoNo, cancellationToken);
											await db.SaveChangesAsync(cancellationToken);
											await tx.CommitAsync(cancellationToken);
											if (tempDocId != null)
											{
												await MoveDraftSafeAsync(context.CompanyCode, context.BranchCode, tempDocId, header.PoNo, cancellationToken);
											}
											poOrderOperationResult = await GetAsync(header.PoNo, header.PoRelNo, cancellationToken);
										}
									}
								}
							}
						}
					}
					}
					end_IL_0604:;
				}
				catch (DbUpdateConcurrencyException)
				{
					await tx.RollbackAsync(cancellationToken);
					poOrderOperationResult = PoOrderOperationResult.Fail("This Purchase Order was changed by another user. Reload before saving.", PoOrderErrorKind.Concurrency);
				}
				catch (SqlException ex2) when (ex2.Number == 1205)
				{
					_logger.LogWarning(ex2, "PO update deadlock.");
					await tx.RollbackAsync(cancellationToken);
					poOrderOperationResult = PoOrderOperationResult.Fail("The PO could not be saved because of a database conflict. Try again.", PoOrderErrorKind.Unexpected);
				}
				catch (Exception exception)
				{
					_logger.LogError(exception, revise ? "PO revise failed." : "PO update failed.");
					await tx.RollbackAsync(cancellationToken);
					poOrderOperationResult = PoOrderOperationResult.Fail(revise ? "Unable to revise the Purchase Order." : "Unable to save the Purchase Order.", PoOrderErrorKind.Unexpected);
				}
			}
			result = poOrderOperationResult;
		}
		return result;
	}

	private async Task<PoOrderOperationResult> ChangeStatusAsync(PoOrderKeyedRequest request, UserContext context, bool forceClose, CancellationToken cancellationToken)
	{
		PoOrderOperationResult result;
		await using (AppDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken))
		{
			PoOrderOperationResult poOrderOperationResult;
			await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync(cancellationToken))
			{
				try
				{
					(PoOrder? Header, PoOrderOperationResult? Result) locked = await LockRequestedPoAsync(db, context, request, forceClose ? "closing" : "reopening", cancellationToken);
					PoOrder header;
					if (locked.Result != null)
					{
						await tx.RollbackAsync(cancellationToken);
						poOrderOperationResult = locked.Result;
					}
					else
					{
						header = locked.Header;
						IReadOnlyDictionary<string, string?> itemTypes = await LoadItemTypesAsync(db, context.CompanyCode, header.Details.Select((PoOrderDetail x) => x.ICode), cancellationToken);
						if (forceClose)
						{
							if (!string.Equals(header.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase) && PoOrderCalc.HasRemainingBalance(header.Details, (PoOrderDetail d) => IsServiceLine(d, itemTypes)))
							{
								string reason = (request.CloseReason ?? string.Empty).Trim();
								if (reason.Length == 0)
								{
									await tx.RollbackAsync(cancellationToken);
									poOrderOperationResult = PoOrderOperationResult.FailValidation("Close reason is required.", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
									{
										["CloseReason"] = "Close reason is required."
									});
								}
								else
								{
									PoStatusPolicy.ForceClose(header, reason, context.UserId ?? string.Empty, _dates.Now);
									goto IL_064d;
								}
							}
							else
							{
								await tx.RollbackAsync(cancellationToken);
								poOrderOperationResult = PoOrderOperationResult.Fail("Purchase Order cannot be force-closed.");
							}
						}
						else
						{
							string error = PoStatusPolicy.Reopen(header, (PoOrderDetail d) => IsServiceLine(d, itemTypes));
							if (error == null)
							{
								goto IL_064d;
							}
							await tx.RollbackAsync(cancellationToken);
							poOrderOperationResult = PoOrderOperationResult.Fail(error);
						}
					}
					goto end_IL_0263;
					IL_064d:
					header.ModifiedDate = _dates.Now;
					header.ModifiedBy = Truncate(context.UserId, 20);
					TouchRowVersion(db, header);
					await db.SaveChangesAsync(cancellationToken);
					await tx.CommitAsync(cancellationToken);
					poOrderOperationResult = await GetAsync(header.PoNo, header.PoRelNo, cancellationToken);
					end_IL_0263:;
				}
				catch (DbUpdateConcurrencyException)
				{
					await tx.RollbackAsync(cancellationToken);
					poOrderOperationResult = PoOrderOperationResult.Fail("This Purchase Order was changed by another user. Reload before saving.", PoOrderErrorKind.Concurrency);
				}
				catch (Exception exception)
				{
					_logger.LogError(exception, forceClose ? "PO force-close failed." : "PO reopen failed.");
					await tx.RollbackAsync(cancellationToken);
					poOrderOperationResult = PoOrderOperationResult.Fail(forceClose ? "Unable to force-close the Purchase Order." : "Unable to reopen the Purchase Order.", PoOrderErrorKind.Unexpected);
				}
			}
			result = poOrderOperationResult;
		}
		return result;
	}

	private async Task<(PoOrder? Header, PoOrderOperationResult? Result)> LockRequestedPoAsync(AppDbContext db, UserContext context, PoOrderKeyedRequest request, string verb, CancellationToken cancellationToken)
	{
		string no = (request.PoNo ?? string.Empty).Trim();
		if (no.Length == 0)
		{
			return (Header: null, Result: PoOrderOperationResult.FailValidation("PO number is required."));
		}
		if (request.RowVersion == null || request.RowVersion.Length == 0)
		{
			return (Header: null, Result: PoOrderOperationResult.Fail("This Purchase Order was changed by another user. Reload before " + verb + ".", PoOrderErrorKind.Concurrency));
		}
		short? poRelNo = request.PoRelNo;
		PoOrder poOrder;
		if (poRelNo.HasValue)
		{
			short relNo = poRelNo.GetValueOrDefault();
			poOrder = await _repository.LockForUpdateAsync(db, context.CompanyCode, context.BranchCode, no, relNo, cancellationToken);
		}
		else
		{
			poOrder = await _repository.LockLatestForUpdateAsync(db, context.CompanyCode, context.BranchCode, no, cancellationToken);
		}
		PoOrder header = poOrder;
		if (header == null)
		{
			return (Header: null, Result: PoOrderOperationResult.Fail("Purchase Order was not found.", PoOrderErrorKind.NotFound));
		}
		await db.Entry(header).Collection((PoOrder x) => x.Details).LoadAsync(cancellationToken);
		if (!RowVersionsEqual(header.RowVersion, request.RowVersion))
		{
			return (Header: null, Result: PoOrderOperationResult.Fail("This Purchase Order was changed by another user. Reload before " + verb + ".", PoOrderErrorKind.Concurrency));
		}
		db.Entry(header).Property((PoOrder x) => x.RowVersion).OriginalValue = request.RowVersion;
		return (Header: header, Result: null);
	}

	private async Task<PoOrderOperationResult> GetCoreAsync(string poNo, short? poRelNo, CancellationToken cancellationToken)
	{
		UserContext context = ValidateUserContext();
		if (context.Error != null)
		{
			return PoOrderOperationResult.Fail(context.Error);
		}
		if (!(await CanAsync("ACCESS", cancellationToken)))
		{
			return PoOrderOperationResult.Fail("Not authorized.", PoOrderErrorKind.Authorization);
		}
		string no = (poNo ?? string.Empty).Trim();
		if (no.Length == 0)
		{
			return PoOrderOperationResult.FailValidation("PO number is required.");
		}
		PoOrderOperationResult result;
		await using (AppDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken))
		{
			PoOrder header = await _repository.GetWithDetailsAsync(db, context.CompanyCode, context.BranchCode, no, poRelNo, cancellationToken);
			if (header == null)
			{
				result = PoOrderOperationResult.Fail("Purchase Order was not found.", PoOrderErrorKind.NotFound);
			}
			else if (_options.SelfViewEdit && !string.Equals(header.CreatedBy, Truncate(context.UserId, 20), StringComparison.OrdinalIgnoreCase))
			{
				result = PoOrderOperationResult.Fail("Purchase Order was not found.", PoOrderErrorKind.NotFound);
			}
			else
			{
				IReadOnlyDictionary<string, string?> itemTypes = await LoadItemTypesAsync(db, context.CompanyCode, header.Details.Select((PoOrderDetail x) => x.ICode), cancellationToken);
				short? maxRel = await _repository.GetMaxRelNoAsync(db, context.CompanyCode, context.BranchCode, header.PoNo, cancellationToken);
				IReadOnlyList<PoOrder> revisions = await _repository.ListRevisionsAsync(db, context.CompanyCode, context.BranchCode, header.PoNo, cancellationToken);
				PoOrder header2 = header;
				IReadOnlyDictionary<string, string?> itemTypes2 = itemTypes;
				bool isLatest = maxRel == header.PoRelNo;
				IReadOnlyList<PoOrder> revisions2 = revisions;
				result = PoOrderOperationResult.OkDocument(MapDocument(header2, itemTypes2, isLatest, revisions2, await CanAsync("EDIT", cancellationToken), await CanAsync("DELETE", cancellationToken), await CanAsync("CANCEL", cancellationToken), await CanAsync("EDIT", cancellationToken), await CanAsync("CLOSE", cancellationToken), await CanAsync("REOPEN", cancellationToken)));
			}
		}
		return result;
	}

	/// <summary>
	/// Legacy-aware Department / Project validation for the PO header (shared rule —
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
		Dictionary<string, string> errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		string deptError = await MsRefLookupRules.ValidateAsync(
			db, companyCode, branchCode, MsRefLookupKind.Department, priorDeptCode, deptCode, cancellationToken);
		if (deptError != null)
		{
			errors["DeptCode"] = deptError;
		}

		string projError = await MsRefLookupRules.ValidateAsync(
			db, companyCode, branchCode, MsRefLookupKind.Project, priorProjId, projId, cancellationToken);
		if (projError != null)
		{
			errors["ProjId"] = projError;
		}

		return (errors.Count == 0) ? null : errors;
	}

	private async Task<PrepareOutcome> PrepareLinesAsync(AppDbContext db, string companyCode, string branchCode, PoOrderSaveRequest request, IReadOnlyDictionary<short, PoOrderDetail>? existingByLine, CancellationToken cancellationToken)
	{
		List<PoOrderLineDto> sourceLines = StripEmptyLines(request.Lines);
		if (sourceLines.Count == 0)
		{
			return PrepareOutcome.Validation("At least one line is required.", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Lines"] = "At least one line is required." });
		}
		Dictionary<string, decimal> taxGroups = await (from x in db.SaTaxGroups.AsNoTracking()
			where x.CompanyCode == companyCode
			select x).ToDictionaryAsync<SaTaxGroup, string, decimal>((SaTaxGroup x) => x.TaxGrCode, (SaTaxGroup x) => x.Percentage, StringComparer.OrdinalIgnoreCase, cancellationToken);
		List<PreparedLine> prepared = new List<PreparedLine>(sourceLines.Count);
		Dictionary<string, string> errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		bool? documentInclusive = null;
		bool? documentOneTime = null;
		for (int i = 0; i < sourceLines.Count; i++)
		{
			PoOrderLineDto src = sourceLines[i];
			string prefix = $"Lines[{i}]";
			PoOrderDetail existingDetail;
			PoOrderDetail existing = ((existingByLine != null && src.Line > 0 && existingByLine.TryGetValue(src.Line, out existingDetail)) ? existingDetail : null);
			string iCode = (src.ICode ?? string.Empty).Trim();
			if (iCode.Length == 0)
			{
				errors[prefix + ".ICode"] = "Item code is required.";
				continue;
			}
			if (_options.POControlIcode && src.OneTime == true)
			{
				errors[prefix + ".OneTime"] = "One-time items are not allowed.";
			}
			if (existing != null && existing.RecvQty > 0m && (!string.Equals(existing.ICode, iCode, StringComparison.OrdinalIgnoreCase) || !string.Equals(existing.PurchaseUom, src.PurchaseUom, StringComparison.OrdinalIgnoreCase) || !string.Equals(existing.PrNo, src.PrNo, StringComparison.OrdinalIgnoreCase) || existing.PrLineNo != src.PrLineNo))
			{
				errors[prefix + ".ICode"] = "Received line item, UOM, and PR reference cannot be changed.";
			}
			ResolvedItem resolved = await ResolveItemAsync(db, companyCode, iCode, cancellationToken);
			if ((object)resolved == null)
			{
				errors[prefix + ".ICode"] = "Item was not found in stock or purchase item master.";
				continue;
			}
			decimal purchaseQty = PoOrderCalc.RoundQty((src.PoPurQty != 0m) ? src.PoPurQty : src.PoQty);
			decimal retainedRecv = existing?.RecvQty ?? 0m;
			decimal retainedReturn = existing?.ReturnQty ?? 0m;
			decimal retainedInvoiced = existing?.InvoicedQty ?? 0m;
			if (!PoOrderCalc.ValidateOrderQtyChange(purchaseQty, retainedRecv, retainedReturn, retainedInvoiced, out string qtyError))
			{
				errors[prefix + ".PoPurQty"] = qtyError;
			}
			string purchaseUom = TruncateOptional(src.PurchaseUom, 10) ?? TruncateOptional(resolved.PurchaseUom, 10);
			if (string.IsNullOrWhiteSpace(purchaseUom))
			{
				errors[prefix + ".PurchaseUom"] = "Purchase UOM is required.";
			}
			string currency = TruncateOptional(src.CurCode, 20) ?? TruncateOptional(request.CurCode, 20) ?? TruncateOptional(resolved.Currency, 20);
			if (string.IsNullOrWhiteSpace(currency))
			{
				errors[prefix + ".CurCode"] = "Currency is required.";
			}
			string taxGroup = TruncateOptional(src.TaxGroup, 20) ?? TruncateOptional(resolved.TaxGroup, 20) ?? TruncateOptional(request.TaxGrpCode, 20);
			decimal taxPercent = default(decimal);
			if (!string.IsNullOrWhiteSpace(taxGroup))
			{
				if (!taxGroups.TryGetValue(taxGroup, out taxPercent))
				{
					errors[prefix + ".TaxGroup"] = "Tax group was not found.";
				}
				else if (taxPercent < 0m)
				{
					errors[prefix + ".TaxGroup"] = "Tax rate cannot be negative.";
				}
			}
			bool sameICode = existing != null && string.Equals(existing.ICode?.Trim(), iCode, StringComparison.OrdinalIgnoreCase);
			decimal num = ((!sameICode) ? (await ResolveUnitPriceAsync(db, companyCode, branchCode, iCode, request.VendCode, purchaseUom, resolved, cancellationToken)) : existing.PoUnitPrice);
			decimal unitPrice = num;
			unitPrice = PoOrderCalc.RoundPrice(unitPrice, _options.POPriceDecimal);
			decimal packSz = ((src.PackSz != 0m) ? src.PackSz : resolved.PackSz);
			decimal stdQty = PoOrderCalc.ComputeStdQty(purchaseQty, packSz);
			decimal amount = PoOrderCalc.ComputeAmount(purchaseQty, unitPrice);
			decimal discounted = PoOrderCalc.ApplyTwoLevelDiscount(amount, src.ItemDiscount, src.DiscountType, src.ItemDiscount1, src.DiscountType1);
			bool isInclusive = ((existing != null && sameICode) ? existing.IsInclusive : src.IsInclusive);
			(decimal, decimal) tuple = PoOrderCalc.ComputeTax(discounted, taxPercent, isInclusive, _options.PurchaseTaxDec);
			decimal netAmount = tuple.Item1;
			decimal taxAmount = tuple.Item2;
			decimal balance = PoOrderCalc.ComputeBalance(purchaseQty, retainedRecv, retainedReturn);
			decimal overRecv = PoOrderCalc.ComputeOverRecv(purchaseQty, retainedRecv, retainedReturn);
			bool oneTime = src.OneTime ?? request.OneTime ?? resolved.IsIndirect;
			EnforceDocumentMode(ref documentInclusive, ref documentOneTime, isInclusive, oneTime, prefix, errors);
			decimal moq = await ResolveMoqAsync(db, companyCode, branchCode, iCode, request.VendCode, purchaseUom, resolved, cancellationToken);
			if (moq > 0m && purchaseQty < moq)
			{
				errors[prefix + ".PoPurQty"] = $"Quantity must be at least MOQ ({moq}).";
			}
			prepared.Add(new PreparedLine
			{
				RequestOrder = i,
				ExistingLineNo = existing?.Line,
				PrNo = TruncateOptional(src.PrNo, 30),
				PrLineNo = src.PrLineNo,
				OneTime = oneTime,
				ICode = iCode,
				IDesc = (TruncateOptional(src.IDesc, 200) ?? TruncateOptional(resolved.IDesc, 200)),
				IType = TruncateOptional(resolved.IType, 20),
				PoUnitPrice = unitPrice,
				PoQty = stdQty,
				PoPurQty = purchaseQty,
				WtQty = (_options.UseWeight ? src.WtQty : 0m),
				Amount = discounted,
				RecvQty = retainedRecv,
				ReturnQty = retainedReturn,
				BalanceQty = balance,
				OverRecvQty = overRecv,
				InvoicedQty = retainedInvoiced,
				PackSz = packSz,
				StdUom = (TruncateOptional(src.StdUom, 10) ?? TruncateOptional(resolved.StdUom, 10)),
				WtUom = (_options.UseWeight ? TruncateOptional(src.WtUom, 10) : null),
				PurchaseUom = purchaseUom,
				EtaDate = src.EtaDate,
				CurCode = currency,
				Remarks = TruncateOptional(src.Remarks, 250),
				PoDesc = (TruncateOptional(src.PoDesc, 200) ?? TruncateOptional(resolved.PoDesc, 200)),
				RecvDate = existing?.RecvDate,
				RepairType = TruncateOptional(src.RepairType, 20),
				Discount = src.Discount,
				ItemDiscount = src.ItemDiscount,
				DiscountType = TruncateOptional(src.DiscountType, 20),
				NetAmount = netAmount,
				ItemDiscount1 = src.ItemDiscount1,
				DiscountType1 = TruncateOptional(src.DiscountType1, 20),
				CjNo = TruncateOptional(src.CjNo, 30),
				CjRelNo = src.CjRelNo,
				CjLine = src.CjLine,
				ProjId = TruncateOptional(src.ProjId ?? request.ProjId, 20),
				VendorPartNo = TruncateOptional(src.VendorPartNo ?? resolved.VendorPartNo, 50),
				TaxGroup = taxGroup,
				TaxAmount = taxAmount,
				IsInclusive = isInclusive,
				ToWarehouse = (TruncateOptional(src.ToWarehouse, 20) ?? TruncateOptional(resolved.DefWarehouse, 20)),
				Requester = TruncateOptional(src.Requester, 50)
			});
			existingDetail = null;
			qtyError = null;
		}
		if (errors.Count > 0)
		{
			return PrepareOutcome.Validation("Validation failed.", errors);
		}
		return PrepareOutcome.Ok(prepared);
	}

	private async Task LockConsumptionAsync(AppDbContext db, string companyCode, string branchCode, IEnumerable<PoOrderDetail> oldLines, IEnumerable<PreparedLine> newLines, CancellationToken cancellationToken)
	{
		var prKeys = oldLines.Select(x => (x.PrNo, x.PrLineNo))
			.Concat(newLines.Select(x => (x.PrNo, x.PrLineNo)))
			.Where(x => !string.IsNullOrWhiteSpace(x.PrNo) && x.PrLineNo is not null)
			.Select(x => (PrNo: x.PrNo!, Line: x.PrLineNo!.Value))
			.ToList();
		await _repository.LockPrHeadersAsync(db, companyCode, branchCode, prKeys.Select(x => x.PrNo), cancellationToken);
		await _repository.LockPrDetailsAsync(db, companyCode, branchCode, prKeys, cancellationToken);

		var cjKeys = oldLines.Select(x => (CjNo: x.CjNo, CjRelNo: x.CjRelNo, Line: (int?)null))
			.Concat(newLines.Select(x => (CjNo: x.CjNo, CjRelNo: x.CjRelNo, Line: x.CjLine)))
			.Where(x => !string.IsNullOrWhiteSpace(x.CjNo) && x.CjRelNo is not null && x.Line is not null)
			.Select(x => (CjNo: x.CjNo!, RelNo: x.CjRelNo!.Value, Line: x.Line!.Value))
			.ToList();
		await _repository.LockCjDetailsAsync(db, companyCode, branchCode, cjKeys, cancellationToken);
	}

	private async Task<PoOrderOperationResult?> ValidateAndApplyPrConsumptionAsync(AppDbContext db, string companyCode, string branchCode, (string PoNo, short PoRelNo)? excludePo, IReadOnlyList<PreparedLine> requested, CancellationToken cancellationToken)
	{
		Dictionary<(string, short Value), decimal> requestedByPr = (from x in requested
			where !string.IsNullOrWhiteSpace(x.PrNo) && x.PrLineNo.HasValue
			group x by (x.PrNo, Value: x.PrLineNo.Value)).ToDictionary((IGrouping<(string, short Value), PreparedLine> g) => g.Key, (IGrouping<(string, short Value), PreparedLine> g) => g.Sum((PreparedLine x) => x.PoPurQty));
		foreach (KeyValuePair<(string, short), decimal> group in requestedByPr)
		{
			PoPrDetail prDetail = await db.PoPrDetails.FirstOrDefaultAsync((PoPrDetail x) => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.PrNo == group.Key.Item1 && x.Line == group.Key.Item2, cancellationToken);
			if (prDetail == null)
			{
				return PoOrderOperationResult.Fail($"PR line {group.Key.Item1}/{group.Key.Item2} was not found.");
			}
			string headerStatus = await (from x in db.PoPrs.AsNoTracking()
				where x.CompanyCode == companyCode && x.BranchCode == branchCode && x.PrNo == @group.Key.Item1
				select x.Status).FirstOrDefaultAsync(cancellationToken);
			if (headerStatus == null)
			{
				return PoOrderOperationResult.Fail("PR " + group.Key.Item1 + " is not available for PO.");
			}

			// New PO creation requires IsAvailableForPo. Update/revise may adjust an already-linked
			// PO against a FULLY_ORDERED PR because excludePo releases this document's own qty.
			var fullyOrderedUpdate = excludePo is not null
				&& string.Equals(headerStatus, PoPrStatuses.FullyOrdered, StringComparison.OrdinalIgnoreCase);
			if (!PoPrCalc.IsAvailableForPo(headerStatus) && !fullyOrderedUpdate)
			{
				return PoOrderOperationResult.Fail("PR " + group.Key.Item1 + " is not available for PO.");
			}
			decimal otherConsumed = await LivePoConsumedForPrAsync(db, companyCode, branchCode, group.Key.Item1, group.Key.Item2, excludePo, cancellationToken);
			decimal remaining = PoOrderCalc.RoundQty(prDetail.PurchaseQty - otherConsumed);
			if (group.Value > remaining)
			{
				return PoOrderOperationResult.Fail($"PR line {group.Key.Item1}/{group.Key.Item2} remaining quantity is insufficient.");
			}
		}
		return null;
	}

	private void ApplyCjConsumption(IReadOnlyList<PreparedLine> oldLines, IReadOnlyList<PreparedLine> newLines, int sign, out string? error)
	{
		error = null;
		Dictionary<(string, int, int), decimal> dictionary = (from x in oldLines
			where !string.IsNullOrWhiteSpace(x.CjNo) && x.CjRelNo.HasValue && x.CjLine.HasValue
			group x by (x.CjNo, x.CjRelNo.Value, x.CjLine.Value)).ToDictionary((IGrouping<(string, int, int), PreparedLine> g) => g.Key, (IGrouping<(string, int, int), PreparedLine> g) => g.Sum((PreparedLine x) => x.PoPurQty));
		Dictionary<(string, int, int), decimal> dictionary2 = (from x in newLines
			where !string.IsNullOrWhiteSpace(x.CjNo) && x.CjRelNo.HasValue && x.CjLine.HasValue
			group x by (x.CjNo, x.CjRelNo.Value, x.CjLine.Value)).ToDictionary((IGrouping<(string, int, int), PreparedLine> g) => g.Key, (IGrouping<(string, int, int), PreparedLine> g) => g.Sum((PreparedLine x) => x.PoPurQty));
		foreach (var item in dictionary.Keys.Union(dictionary2.Keys))
		{
			decimal value;
			decimal num = (dictionary.TryGetValue(item, out value) ? value : 0m);
			decimal value2;
			decimal num2 = (dictionary2.TryGetValue(item, out value2) ? value2 : 0m);
			decimal num3 = (num2 - num) * (decimal)sign;
			if (!(num3 == 0m))
			{
			}
		}
	}

	private PoOrderOperationResult? SynchronizeDetails(AppDbContext db, PoOrder header, List<PreparedLine> prepared, IReadOnlyDictionary<short, PoOrderDetail> existingByLine)
	{
		HashSet<short> requestedExisting = (from x in prepared
			where x.ExistingLineNo.HasValue
			select x.ExistingLineNo.Value).ToHashSet();
		foreach (PoOrderDetail item in header.Details.Where((PoOrderDetail x) => !requestedExisting.Contains(x.Line)).ToList())
		{
			if (item.RecvQty > 0m)
			{
				return PoOrderOperationResult.Fail($"Line {item.Line} cannot be deleted because it has received quantity.");
			}
			db.PoOrderDetails.Remove(item);
			header.Details.Remove(item);
		}
		foreach (PoOrderDetail item2 in header.Details.ToList())
		{
			db.PoOrderDetails.Remove(item2);
			header.Details.Remove(item2);
		}
		short num = 1;
		foreach (PreparedLine item3 in prepared.OrderBy((PreparedLine x) => x.RequestOrder))
		{
			header.Details.Add(ToDetailEntity(header, item3, num++));
		}
		return null;
	}

	private async Task StampPrConsumptionAsync(AppDbContext db, string companyCode, string branchCode, IReadOnlyList<(string PrNo, short Line)> keys, string poNo, CancellationToken cancellationToken)
	{
		foreach (var key in keys.Distinct())
		{
			PoPrDetail detail = await db.PoPrDetails.FirstOrDefaultAsync((PoPrDetail x) => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.PrNo == key.PrNo && x.Line == key.Line, cancellationToken);
			if (detail != null)
			{
				decimal consumed = await LivePoConsumedForPrAsync(db, companyCode, branchCode, key.PrNo, key.Line, null, cancellationToken);
				detail.PoNo = ((PoOrderCalc.RoundQty(detail.PurchaseQty - consumed) <= 0m) ? poNo : null);
				if (!string.Equals(detail.Status, PoPrStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
				{
					detail.Status = PoPrCalc.ComputeLineDerivedStatus(detail.PurchaseQty, consumed, detail.Status);
				}
			}
		}
		await RecalculatePrHeaderStampsAsync(db, companyCode, branchCode, keys.Select<(string, short), string>(((string PrNo, short Line) x) => x.PrNo), cancellationToken);
	}

	private async Task RecalculatePrStampsAsync(AppDbContext db, string companyCode, string branchCode, IReadOnlyList<(string PrNo, short Line)> keys, CancellationToken cancellationToken)
	{
		foreach (var key in keys.Distinct())
		{
			PoPrDetail detail = await db.PoPrDetails.FirstOrDefaultAsync((PoPrDetail x) => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.PrNo == key.PrNo && x.Line == key.Line, cancellationToken);
			if (detail != null)
			{
				decimal consumed = await LivePoConsumedForPrAsync(db, companyCode, branchCode, key.PrNo, key.Line, null, cancellationToken);
				PoPrDetail poPrDetail = detail;
				string stampedPoNo = ((!(PoOrderCalc.RoundQty(detail.PurchaseQty - consumed) <= 0m)) ? null : (await LatestLivePoNoForPrAsync(db, companyCode, branchCode, key.PrNo, key.Line, cancellationToken)));
				poPrDetail.PoNo = stampedPoNo;
				if (!string.Equals(detail.Status, PoPrStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
				{
					detail.Status = PoPrCalc.ComputeLineDerivedStatus(detail.PurchaseQty, consumed, detail.Status);
				}
			}
		}
		await RecalculatePrHeaderStampsAsync(db, companyCode, branchCode, keys.Select<(string, short), string>(((string PrNo, short Line) x) => x.PrNo), cancellationToken);
	}

	private async Task RecalculatePrHeaderStampsAsync(AppDbContext db, string companyCode, string branchCode, IEnumerable<string> prNos, CancellationToken cancellationToken)
	{
		foreach (string prNo in prNos.Where((string x) => !string.IsNullOrWhiteSpace(x)).Distinct<string>(StringComparer.OrdinalIgnoreCase))
		{
			PoPr header = await db.PoPrs.Include((PoPr x) => x.Details).FirstOrDefaultAsync((PoPr x) => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.PrNo == prNo, cancellationToken);
			if (header == null || header.Details.Count == 0)
			{
				continue;
			}
			List<(decimal PurchaseQty, decimal ConsumedQty)> consumption = new List<(decimal, decimal)>(header.Details.Count);
			bool allConsumed = true;
			foreach (PoPrDetail detail in header.Details)
			{
				decimal consumed = await LivePoConsumedForPrAsync(db, companyCode, branchCode, detail.PrNo, detail.Line, null, cancellationToken);
				consumption.Add((detail.PurchaseQty, consumed));
				if (!string.Equals(detail.Status, PoPrStatuses.Cancelled, StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(header.Status, PoPrStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
				{
					detail.Status = PoPrCalc.ComputeLineDerivedStatus(detail.PurchaseQty, consumed, detail.Status);
				}
				if (PoOrderCalc.RoundQty(detail.PurchaseQty - consumed) > 0m)
				{
					allConsumed = false;
				}
			}
			PoPr poPr = header;
			string stampedPoNo = ((!allConsumed) ? null : (await LatestLivePoNoForPrAsync(db, companyCode, branchCode, prNo, null, cancellationToken)));
			poPr.PoNo = stampedPoNo;
			if (!string.Equals(header.Status, PoPrStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
			{
				header.Status = PoPrCalc.ComputeDerivedStatus(header.Status, consumption);
			}
		}
	}

	private async Task<decimal> LivePoConsumedForPrAsync(AppDbContext db, string companyCode, string branchCode, string prNo, short prLineNo, (string PoNo, short PoRelNo)? excludePo, CancellationToken cancellationToken)
	{
		IQueryable<PoOrderDetail> query = db.PoOrderDetails.Where((PoOrderDetail d) => d.CompanyCode == companyCode && d.BranchCode == branchCode && d.PrNo == prNo && (int?)d.PrLineNo == (int?)prLineNo && d.Order.Status != PoOrderStatuses.Cancelled);
		if (excludePo.HasValue)
		{
			string po = excludePo.Value.PoNo;
			short rel = excludePo.Value.PoRelNo;
			query = query.Where((PoOrderDetail d) => d.PoNo != po || d.PoRelNo != rel);
		}
		return (await query.SumAsync((Expression<Func<PoOrderDetail, decimal?>>)((PoOrderDetail d) => d.PoPurQty), cancellationToken)).GetValueOrDefault();
	}

	private async Task<string?> LatestLivePoNoForPrAsync(AppDbContext db, string companyCode, string branchCode, string prNo, short? prLineNo, CancellationToken cancellationToken)
	{
		IQueryable<PoOrderDetail> query = from d in db.PoOrderDetails.AsNoTracking()
			where d.CompanyCode == companyCode && d.BranchCode == branchCode && d.PrNo == prNo && d.Order.Status != PoOrderStatuses.Cancelled
			select d;
		if (prLineNo.HasValue)
		{
			query = query.Where((PoOrderDetail d) => (int?)d.PrLineNo == (int?)((short?)prLineNo).Value);
		}
		return await (from d in query
			orderby d.Order.PoDate descending, d.PoNo descending
			select d.PoNo).FirstOrDefaultAsync(cancellationToken);
	}

	private async Task<IReadOnlyList<PoPrRemainingLineDto>> BuildPrRemainingLinesAsync(AppDbContext db, string companyCode, string branchCode, string prNo, string? vendorCd, (string PoNo, short PoRelNo)? excludePo, CancellationToken cancellationToken)
	{
		string vendor = (vendorCd ?? string.Empty).Trim();
		List<PoPrDetail> details = await (from x in db.PoPrDetails.AsNoTracking()
			where x.CompanyCode == companyCode && x.BranchCode == branchCode && x.PrNo == prNo
			orderby x.Line
			select x).ToListAsync(cancellationToken);
		List<PoPrRemainingLineDto> rows = new List<PoPrRemainingLineDto>();
		foreach (PoPrDetail d in details)
		{
			if (vendor.Length <= 0 || string.Equals(d.VendorCd, vendor, StringComparison.OrdinalIgnoreCase))
			{
				decimal consumed = await LivePoConsumedForPrAsync(db, companyCode, branchCode, d.PrNo, d.Line, excludePo, cancellationToken);
				rows.Add(new PoPrRemainingLineDto
				{
					PrNo = d.PrNo,
					Line = d.Line,
					ICode = d.ICode,
					IDesc = d.IDesc,
					PurchaseQty = d.PurchaseQty,
					RemainingQty = PoOrderCalc.RoundQty(d.PurchaseQty - consumed),
					PurchaseUom = d.PurchaseUom,
					PackSz = d.PackSz,
					StdUom = d.StdUom,
					UnitPrice = d.UnitPrice,
					Currency = d.Currency,
					VendorCd = d.VendorCd,
					VendNm = d.VendNm,
					TaxGroup = d.TaxGroup,
					IsInclusive = d.IsInclusive,
					ToWarehouse = d.ToWarehouse,
					EtaDt = d.EtaDt,
					Purpose = d.Purpose,
					OneTimeItemYn = d.OneTimeItemYn
				});
			}
		}
		return rows;
	}

	private static IReadOnlyList<(string PrNo, short Line)> CollectPrKeys(IEnumerable<PoOrderDetail> details)
	{
		return (from x in details
			where !string.IsNullOrWhiteSpace(x.PrNo) && x.PrLineNo.HasValue
			select (x.PrNo, Value: x.PrLineNo.Value)).Distinct().ToList();
	}

	private static PreparedLine MapExistingLine(PoOrderDetail x)
	{
		return new PreparedLine
		{
			ExistingLineNo = x.Line,
			PrNo = x.PrNo,
			PrLineNo = x.PrLineNo,
			OneTime = x.OneTime,
			ICode = (x.ICode ?? string.Empty),
			IDesc = x.IDesc,
			PoUnitPrice = x.PoUnitPrice,
			PoQty = x.PoQty,
			PoPurQty = x.PoPurQty,
			WtQty = x.WtQty,
			Amount = x.Amount,
			RecvQty = x.RecvQty,
			ReturnQty = x.ReturnQty,
			BalanceQty = x.BalanceQty,
			OverRecvQty = x.OverRecvQty,
			InvoicedQty = x.InvoicedQty,
			PackSz = x.PackSz,
			StdUom = x.StdUom,
			WtUom = x.WtUom,
			PurchaseUom = x.PurchaseUom,
			EtaDate = x.EtaDate,
			CurCode = x.CurCode,
			Remarks = x.Remarks,
			PoDesc = x.PoDesc,
			RecvDate = x.RecvDate,
			RepairType = x.RepairType,
			Discount = x.Discount,
			ItemDiscount = x.ItemDiscount,
			DiscountType = x.DiscountType,
			NetAmount = x.NetAmount,
			ItemDiscount1 = x.ItemDiscount1,
			DiscountType1 = x.DiscountType1,
			CjNo = x.CjNo,
			CjRelNo = x.CjRelNo,
			ProjId = x.ProjId,
			VendorPartNo = x.VendorPartNo,
			TaxGroup = x.TaxGroup,
			TaxAmount = x.TaxAmount,
			IsInclusive = x.IsInclusive,
			ToWarehouse = x.ToWarehouse,
			Requester = x.Requester
		};
	}

	private static void ApplyHeaderRequest(PoOrder header, PoOrderSaveRequest request, DateTime poDate)
	{
		header.PoDate = poDate;
		header.Buyer = TruncateOptional(request.Buyer, 20);
		header.OneTime = request.OneTime;
		header.VendCode = TruncateOptional(request.VendCode, 60);
		header.VendName = TruncateOptional(request.VendName, 200);
		header.VendAddress1 = TruncateOptional(request.VendAddress1, 100);
		header.VendAddress2 = TruncateOptional(request.VendAddress2, 100);
		header.VendAddress3 = TruncateOptional(request.VendAddress3, 100);
		header.VendAddress4 = TruncateOptional(request.VendAddress4, 100);
		header.VendCity = TruncateOptional(request.VendCity, 50);
		header.VendState = TruncateOptional(request.VendState, 50);
		header.VendPostal = TruncateOptional(request.VendPostal, 20);
		header.VendCountryCode = TruncateOptional(request.VendCountryCode, 50);
		header.VendTel = TruncateOptional(request.VendTel, 50);
		header.VendFax = TruncateOptional(request.VendFax, 50);
		header.CurCode = TruncateOptional(request.CurCode, 20);
		header.TermCode = TruncateOptional(request.TermCode, 20);
		header.ContactPerson = TruncateOptional(request.ContactPerson, 100);
		header.Email = TruncateOptional(request.Email, 100);
		header.Website = TruncateOptional(request.Website, 100);
		header.ShipName = TruncateOptional(request.ShipName, 100);
		header.ShipAddress1 = TruncateOptional(request.ShipAddress1, 100);
		header.ShipAddress2 = TruncateOptional(request.ShipAddress2, 100);
		header.ShipAddress3 = TruncateOptional(request.ShipAddress3, 100);
		header.ShipAddress4 = TruncateOptional(request.ShipAddress4, 100);
		header.ShipCity = TruncateOptional(request.ShipCity, 50);
		header.ShipState = TruncateOptional(request.ShipState, 50);
		header.ShipPostal = TruncateOptional(request.ShipPostal, 20);
		header.ShipCountryCode = TruncateOptional(request.ShipCountryCode, 50);
		header.ShipTel = TruncateOptional(request.ShipTel, 50);
		header.ShipFax = TruncateOptional(request.ShipFax, 50);
		header.TaxGrpCode = TruncateOptional(request.TaxGrpCode, 20);
		header.Discount = request.Discount;
		header.SiRemark = TruncateOptional(request.SiRemark, 500);
		header.RegNo = TruncateOptional(request.RegNo, 50);
		header.DeptCode = TruncateOptional(request.DeptCode, 20);
		header.BuyingTerm = TruncateOptional(request.BuyingTerm, 20);
		header.ProjId = TruncateOptional(request.ProjId, 20);
		header.CheckBy = TruncateOptional(request.CheckBy, 20);
		header.ApprovedBy = TruncateOptional(request.ApprovedBy, 20);
		header.AuthorisedBy = TruncateOptional(request.AuthorisedBy, 20);
		header.QuatationNo = TruncateOptional(request.QuatationNo, 50);
		header.Ref1 = TruncateOptional(request.Ref1, 50);
		header.Ref2 = TruncateOptional(request.Ref2, 50);
		header.Ref3 = TruncateOptional(request.Ref3, 50);
		header.Ref4 = TruncateOptional(request.Ref4, 50);
		header.HdrType = TruncateOptional(request.RevisionReason, 20);
	}

	private static PoOrderDetail ToDetailEntity(PoOrder header, PreparedLine line, short lineNo)
	{
		PoOrderDetail poOrderDetail = new PoOrderDetail
		{
			CompanyCode = header.CompanyCode,
			BranchCode = header.BranchCode,
			PoNo = header.PoNo,
			PoRelNo = header.PoRelNo,
			Line = lineNo
		};
		ApplyPreparedLine(poOrderDetail, line);
		return poOrderDetail;
	}

	private static void ApplyPreparedLine(PoOrderDetail detail, PreparedLine line)
	{
		detail.PrNo = line.PrNo;
		detail.PrLineNo = line.PrLineNo;
		detail.OneTime = line.OneTime;
		detail.ICode = line.ICode;
		detail.IDesc = line.IDesc;
		detail.PoUnitPrice = line.PoUnitPrice;
		detail.PoQty = line.PoQty;
		detail.PoPurQty = line.PoPurQty;
		detail.WtQty = line.WtQty;
		detail.Amount = line.Amount;
		detail.RecvQty = line.RecvQty;
		detail.ReturnQty = line.ReturnQty;
		detail.BalanceQty = line.BalanceQty;
		detail.OverRecvQty = line.OverRecvQty;
		detail.InvoicedQty = line.InvoicedQty;
		detail.PackSz = line.PackSz;
		detail.StdUom = line.StdUom;
		detail.WtUom = line.WtUom;
		detail.PurchaseUom = line.PurchaseUom;
		detail.EtaDate = line.EtaDate;
		detail.CurCode = line.CurCode;
		detail.Remarks = line.Remarks;
		detail.PoDesc = line.PoDesc;
		detail.RecvDate = line.RecvDate;
		detail.RepairType = line.RepairType;
		detail.Discount = line.Discount;
		detail.ItemDiscount = line.ItemDiscount;
		detail.DiscountType = line.DiscountType;
		detail.NetAmount = line.NetAmount;
		detail.ItemDiscount1 = line.ItemDiscount1;
		detail.DiscountType1 = line.DiscountType1;
		detail.CjNo = line.CjNo;
		detail.CjRelNo = line.CjRelNo;
		detail.ProjId = line.ProjId;
		detail.VendorPartNo = line.VendorPartNo;
		detail.TaxGroup = line.TaxGroup;
		detail.TaxAmount = line.TaxAmount;
		detail.IsInclusive = line.IsInclusive;
		detail.ToWarehouse = line.ToWarehouse;
		detail.Requester = line.Requester;
	}

	private PoOrderListRow MapListRow(PoOrder header, IReadOnlyDictionary<string, string?> itemTypes, bool canEdit, bool canDelete, bool canCancel, bool canRevise, bool canClose, bool canReopen)
	{
		(decimal, decimal, decimal) tuple = PoOrderCalc.SumTotals(header.Details.Select((PoOrderDetail x) => (NetAmount: x.NetAmount, TaxAmount: x.TaxAmount)));
		bool flag = IsForceClosed(header, itemTypes);
		bool flag2 = header.Details.Any((PoOrderDetail x) => x.RecvQty > 0m);
		bool flag3 = (header.Status == "NEW" || header.Status == "OPEN" || header.Status == "RECEIVED") && !flag;
		bool canDelete2 = canDelete && (header.Status == "NEW" || header.Status == "OPEN") && !flag2;
		bool canCancel2 = canCancel && !IsTerminal(header.Status) && !flag2 && !flag;
		bool canForceClose = canClose && !IsTerminal(header.Status) && PoOrderCalc.HasRemainingBalance(header.Details, (PoOrderDetail d) => IsServiceLine(d, itemTypes));
		bool canReopen2 = canReopen && flag;
		return new PoOrderListRow
		{
			PoNo = header.PoNo,
			PoRelNo = header.PoRelNo,
			PoDate = header.PoDate,
			Status = header.Status,
			VendCode = header.VendCode,
			VendName = header.VendName,
			Buyer = header.Buyer,
			CurCode = header.CurCode,
			Gross = tuple.Item1,
			Taxes = tuple.Item2,
			Total = tuple.Item3,
			EtaDate = (from x in header.Details
				orderby x.Line
				select x.EtaDate).FirstOrDefault((DateTime? x) => x.HasValue),
			LineCount = header.Details.Count,
			CreatedBy = header.CreatedBy,
			CreatedDate = header.CreatedDate,
			RowVersion = (header.RowVersion ?? Array.Empty<byte>()),
			CanEdit = (canEdit && flag3),
			CanDelete = canDelete2,
			CanCancel = canCancel2,
			CanRevise = (canRevise && header.Status == "NEW"),
			CanForceClose = canForceClose,
			CanReopen = canReopen2,
			IsForceClosed = flag
		};
	}

	private PoOrderDocument MapDocument(PoOrder header, IReadOnlyDictionary<string, string?> itemTypes, bool isLatest, IReadOnlyList<PoOrder> revisions, bool canEdit, bool canDelete, bool canCancel, bool canRevise, bool canClose, bool canReopen)
	{
		(decimal, decimal, decimal) tuple = PoOrderCalc.SumTotals(header.Details.Select((PoOrderDetail x) => (NetAmount: x.NetAmount, TaxAmount: x.TaxAmount)));
		bool flag = IsForceClosed(header, itemTypes);
		bool flag2 = header.Details.Any((PoOrderDetail x) => x.RecvQty > 0m);
		bool flag3 = isLatest && (header.Status == "NEW" || header.Status == "OPEN" || header.Status == "RECEIVED") && !flag;
		return new PoOrderDocument
		{
			PoNo = header.PoNo,
			PoRelNo = header.PoRelNo,
			IsLatest = isLatest,
			PoDate = header.PoDate,
			Status = header.Status,
			Buyer = header.Buyer,
			OneTime = header.OneTime,
			VendCode = header.VendCode,
			VendName = header.VendName,
			VendAddress1 = header.VendAddress1,
			VendAddress2 = header.VendAddress2,
			VendAddress3 = header.VendAddress3,
			VendAddress4 = header.VendAddress4,
			VendCity = header.VendCity,
			VendState = header.VendState,
			VendPostal = header.VendPostal,
			VendCountryCode = header.VendCountryCode,
			VendTel = header.VendTel,
			VendFax = header.VendFax,
			CurCode = header.CurCode,
			TermCode = header.TermCode,
			ContactPerson = header.ContactPerson,
			Email = header.Email,
			Website = header.Website,
			ShipName = header.ShipName,
			ShipAddress1 = header.ShipAddress1,
			ShipAddress2 = header.ShipAddress2,
			ShipAddress3 = header.ShipAddress3,
			ShipAddress4 = header.ShipAddress4,
			ShipCity = header.ShipCity,
			ShipState = header.ShipState,
			ShipPostal = header.ShipPostal,
			ShipCountryCode = header.ShipCountryCode,
			ShipTel = header.ShipTel,
			ShipFax = header.ShipFax,
			TaxGrpCode = header.TaxGrpCode,
			TaxAmount = header.TaxAmount,
			Discount = header.Discount,
			SiRemark = header.SiRemark,
			RegNo = header.RegNo,
			DeptCode = header.DeptCode,
			BuyingTerm = header.BuyingTerm,
			LocationCode = header.LocationCode,
			ProjId = header.ProjId,
			Prefix = header.Prefix,
			CheckBy = header.CheckBy,
			ApprovedBy = header.ApprovedBy,
			AuthorisedBy = header.AuthorisedBy,
			QuatationNo = header.QuatationNo,
			Ref1 = header.Ref1,
			Ref2 = header.Ref2,
			Ref3 = header.Ref3,
			Ref4 = header.Ref4,
			RevisionReason = header.HdrType,
			Gross = tuple.Item1,
			Taxes = tuple.Item2,
			Total = tuple.Item3,
			CreatedBy = header.CreatedBy,
			CreatedDate = header.CreatedDate,
			ModifiedBy = header.ModifiedBy,
			ModifiedDate = header.ModifiedDate,
			RowVersion = (header.RowVersion ?? Array.Empty<byte>()),
			CanEdit = (canEdit && flag3),
			CanDelete = (canDelete && flag3 && !flag2 && (header.Status == "NEW" || header.Status == "OPEN")),
			CanCancel = (canCancel && isLatest && !IsTerminal(header.Status) && !flag2 && !flag),
			CanRevise = (canRevise && isLatest && header.Status == "NEW"),
			CanForceClose = (canClose && isLatest && !IsTerminal(header.Status) && PoOrderCalc.HasRemainingBalance(header.Details, (PoOrderDetail d) => IsServiceLine(d, itemTypes))),
			CanReopen = (canReopen && isLatest && flag),
			IsForceClosed = flag,
			CloseReason = header.CloseReason,
			ClosedBy = header.ClosedBy,
			ClosedOn = header.ClosedOn,
			HasReceivedLines = flag2,
			Lines = (from x in header.Details
				orderby x.Line
				select MapLine(x, itemTypes)).ToList(),
			Revisions = revisions.Select((PoOrder x) => new PoOrderRevisionRow
			{
				PoRelNo = x.PoRelNo,
				Status = x.Status,
				CreatedBy = x.CreatedBy,
				CreatedDate = x.CreatedDate,
				RevisionReason = x.HdrType
			}).ToList()
		};
	}

	private static PoOrderLineDto MapLine(PoOrderDetail x, IReadOnlyDictionary<string, string?> itemTypes)
	{
		string value;
		string iType = ((!string.IsNullOrWhiteSpace(x.ICode) && itemTypes.TryGetValue(x.ICode, out value)) ? value : null);
		return new PoOrderLineDto
		{
			Line = x.Line,
			PrNo = x.PrNo,
			PrLineNo = x.PrLineNo,
			OneTime = x.OneTime,
			ICode = x.ICode,
			IDesc = x.IDesc,
			IType = iType,
			PoUnitPrice = x.PoUnitPrice,
			PoQty = x.PoQty,
			PoPurQty = x.PoPurQty,
			WtQty = x.WtQty,
			Amount = x.Amount,
			RecvQty = x.RecvQty,
			ReturnQty = x.ReturnQty,
			BalanceQty = x.BalanceQty,
			OverRecvQty = x.OverRecvQty,
			InvoicedQty = x.InvoicedQty,
			PackSz = x.PackSz,
			StdUom = x.StdUom,
			WtUom = x.WtUom,
			PurchaseUom = x.PurchaseUom,
			EtaDate = x.EtaDate,
			CurCode = x.CurCode,
			Remarks = x.Remarks,
			PoDesc = x.PoDesc,
			RecvDate = x.RecvDate,
			RepairType = x.RepairType,
			Discount = x.Discount,
			ItemDiscount = x.ItemDiscount,
			DiscountType = x.DiscountType,
			NetAmount = x.NetAmount,
			ItemDiscount1 = x.ItemDiscount1,
			DiscountType1 = x.DiscountType1,
			CjNo = x.CjNo,
			CjRelNo = x.CjRelNo,
			ProjId = x.ProjId,
			VendorPartNo = x.VendorPartNo,
			TaxGroup = x.TaxGroup,
			TaxAmount = x.TaxAmount,
			IsInclusive = x.IsInclusive,
			ToWarehouse = x.ToWarehouse,
			Requester = x.Requester
		};
	}

	private PoOrderDocument CloneDraft(PoOrderDocument source, string poNo, short poRelNo, bool resetPrAndReceive)
	{
		string text = Truncate(ValidateUserContext().UserId ?? string.Empty, 20);
		List<PoOrderLineDto> list = source.Lines.OrderBy((PoOrderLineDto x) => x.Line).Select(delegate(PoOrderLineDto x, int index)
		{
			decimal poPurQty = x.PoPurQty;
			return new PoOrderLineDto
			{
				Line = (short)(index + 1),
				PrNo = (resetPrAndReceive ? null : x.PrNo),
				PrLineNo = (resetPrAndReceive ? ((short?)null) : x.PrLineNo),
				OneTime = x.OneTime,
				ICode = x.ICode,
				IDesc = x.IDesc,
				IType = x.IType,
				PoUnitPrice = x.PoUnitPrice,
				PoQty = PoOrderCalc.ComputeStdQty(poPurQty, x.PackSz),
				PoPurQty = poPurQty,
				WtQty = (resetPrAndReceive ? x.WtQty : x.WtQty),
				Amount = x.Amount,
				RecvQty = 0m,
				ReturnQty = 0m,
				BalanceQty = poPurQty,
				OverRecvQty = 0m,
				InvoicedQty = 0m,
				PackSz = x.PackSz,
				StdUom = x.StdUom,
				WtUom = x.WtUom,
				PurchaseUom = x.PurchaseUom,
				EtaDate = x.EtaDate,
				CurCode = x.CurCode,
				Remarks = x.Remarks,
				PoDesc = x.PoDesc,
				RecvDate = null,
				RepairType = x.RepairType,
				Discount = x.Discount,
				ItemDiscount = x.ItemDiscount,
				DiscountType = x.DiscountType,
				NetAmount = x.NetAmount,
				ItemDiscount1 = x.ItemDiscount1,
				DiscountType1 = x.DiscountType1,
				CjNo = (resetPrAndReceive ? null : x.CjNo),
				CjRelNo = (resetPrAndReceive ? ((int?)null) : x.CjRelNo),
				ProjId = x.ProjId,
				VendorPartNo = x.VendorPartNo,
				TaxGroup = x.TaxGroup,
				TaxAmount = x.TaxAmount,
				IsInclusive = x.IsInclusive,
				ToWarehouse = x.ToWarehouse,
				Requester = x.Requester
			};
		}).ToList();
		(decimal, decimal, decimal) tuple = PoOrderCalc.SumTotals(list.Select((PoOrderLineDto x) => (NetAmount: x.NetAmount, TaxAmount: x.TaxAmount)));
		return new PoOrderDocument
		{
			PoNo = poNo,
			PoRelNo = poRelNo,
			IsLatest = true,
			PoDate = _dates.Today.Date,
			Status = "NEW",
			Buyer = text,
			OneTime = source.OneTime,
			VendCode = source.VendCode,
			VendName = source.VendName,
			VendAddress1 = source.VendAddress1,
			VendAddress2 = source.VendAddress2,
			VendAddress3 = source.VendAddress3,
			VendAddress4 = source.VendAddress4,
			VendCity = source.VendCity,
			VendState = source.VendState,
			VendPostal = source.VendPostal,
			VendCountryCode = source.VendCountryCode,
			VendTel = source.VendTel,
			VendFax = source.VendFax,
			CurCode = source.CurCode,
			TermCode = source.TermCode,
			ContactPerson = source.ContactPerson,
			Email = source.Email,
			Website = source.Website,
			ShipName = source.ShipName,
			ShipAddress1 = source.ShipAddress1,
			ShipAddress2 = source.ShipAddress2,
			ShipAddress3 = source.ShipAddress3,
			ShipAddress4 = source.ShipAddress4,
			ShipCity = source.ShipCity,
			ShipState = source.ShipState,
			ShipPostal = source.ShipPostal,
			ShipCountryCode = source.ShipCountryCode,
			ShipTel = source.ShipTel,
			ShipFax = source.ShipFax,
			TaxGrpCode = source.TaxGrpCode,
			Discount = source.Discount,
			SiRemark = source.SiRemark,
			RegNo = source.RegNo,
			DeptCode = source.DeptCode,
			BuyingTerm = source.BuyingTerm,
			LocationCode = source.LocationCode,
			ProjId = source.ProjId,
			QuatationNo = source.QuatationNo,
			Ref1 = source.Ref1,
			Ref2 = source.Ref2,
			Ref3 = source.Ref3,
			Ref4 = source.Ref4,
			Gross = tuple.Item1,
			Taxes = tuple.Item2,
			Total = tuple.Item3,
			CreatedBy = text,
			CreatedDate = _dates.Now,
			RowVersion = Array.Empty<byte>(),
			CanEdit = true,
			Lines = list
		};
	}

	private async Task<ResolvedItem?> ResolveItemAsync(AppDbContext db, string companyCode, string iCode, CancellationToken cancellationToken)
	{
		IvStockMaster stock = await db.IvStockMasters.AsNoTracking().FirstOrDefaultAsync((IvStockMaster x) => x.CompanyCode == companyCode && x.ICode == iCode && x.IsActive, cancellationToken);
		if (stock != null)
		{
			return new ResolvedItem(IsIndirect: false, stock.ICode, stock.IDesc, stock.IType, stock.PurUom, stock.StdUom, stock.PurStdPackSize ?? stock.StdPackSize ?? 1m, stock.PurchasePrice.GetValueOrDefault(), stock.PurchaseTaxGroup ?? stock.TaxGroup, null, null, null, stock.DefWarehouse);
		}
		PoPurItem pur = await (from x in db.PoPurItems.AsNoTracking()
			where x.CompanyCode == companyCode && x.ICode == iCode
			orderby x.ModifiedDate descending, x.Id descending
			select x).FirstOrDefaultAsync(cancellationToken);
		return (pur == null) ? null : new ResolvedItem(IsIndirect: true, pur.ICode, pur.IDesc, null, pur.PurUom, null, 1m, pur.UnitPrice.GetValueOrDefault(), null, pur.Category, pur.VendorPartNo, pur.Currency, null);
	}

	private async Task<decimal> ResolveUnitPriceAsync(AppDbContext db, string companyCode, string branchCode, string iCode, string? vendorCd, string? purchaseUom, ResolvedItem item, CancellationToken cancellationToken)
	{
		int mode = _options.SupplierPrice;
		if ((uint)(mode - 2) <= 1u)
		{
			PoVendorByItem vendorPrice = await FindVendorItemAsync(db, companyCode, branchCode, iCode, vendorCd, purchaseUom, cancellationToken);
			if ((vendorPrice?.UnitPrice).HasValue)
			{
				return vendorPrice.UnitPrice.Value;
			}
			if (mode == 2)
			{
				return default(decimal);
			}
		}
		return item.UnitPrice;
	}

	private async Task<decimal> ResolveMoqAsync(AppDbContext db, string companyCode, string branchCode, string iCode, string? vendorCd, string? purchaseUom, ResolvedItem item, CancellationToken cancellationToken)
	{
		return (await FindVendorItemAsync(db, companyCode, branchCode, iCode, vendorCd, purchaseUom, cancellationToken))?.OrdLevel ?? 0m;
	}

	private async Task<PoVendorByItem?> FindVendorItemAsync(AppDbContext db, string companyCode, string branchCode, string iCode, string? vendorCd, string? purchaseUom, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(vendorCd) || string.IsNullOrWhiteSpace(purchaseUom))
		{
			return null;
		}
		return (from x in await (from x in db.PoVendorByItems.AsNoTracking()
				where x.CompanyCode == companyCode && x.ICode == iCode && x.Vendor == vendorCd && x.PurUom == purchaseUom
				select x).ToListAsync(cancellationToken)
			where !PoPrCalc.IsInactiveVendorItemStatus(x.Status)
			orderby string.Equals(x.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase) descending, x.ModifiedDate descending, x.Id descending
			select x).FirstOrDefault();
	}

	private async Task<IReadOnlyDictionary<string, string?>> LoadItemTypesAsync(AppDbContext db, string companyCode, IEnumerable<string?> iCodes, CancellationToken cancellationToken)
	{
		List<string> codes = (from x in iCodes
			where !string.IsNullOrWhiteSpace(x)
			select x.Trim()).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		if (codes.Count == 0)
		{
			return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}
		return await (from x in db.IvStockMasters.AsNoTracking()
			where x.CompanyCode == companyCode && codes.Contains(x.ICode)
			select x).ToDictionaryAsync<IvStockMaster, string, string>((IvStockMaster x) => x.ICode, (IvStockMaster x) => x.IType, StringComparer.OrdinalIgnoreCase, cancellationToken);
	}

	private async Task<string?> ResolveSupplierPrefixAsync(AppDbContext db, string companyCode, string branchCode, string? vendCode, CancellationToken cancellationToken)
	{
		string vendor = (vendCode ?? string.Empty).Trim();
		if (vendor.Length == 0)
		{
			return string.Empty;
		}
		return (await (from x in db.PoSuppliers.AsNoTracking()
			where x.CompanyCode == companyCode && x.BranchCode == branchCode && x.SuppCode == vendor
			select x.PoPrefix).FirstOrDefaultAsync(cancellationToken)) ?? string.Empty;
	}

	private static void EnforceDocumentMode(ref bool? documentInclusive, ref bool? documentOneTime, bool inclusive, bool? oneTime, string prefix, IDictionary<string, string> errors)
	{
		bool valueOrDefault = documentInclusive == true;
		if (!documentInclusive.HasValue)
		{
			valueOrDefault = inclusive;
			documentInclusive = valueOrDefault;
		}
		if (documentInclusive.Value != inclusive)
		{
			errors[prefix + ".IsInclusive"] = "Inclusive and exclusive tax lines cannot be mixed.";
		}
		bool? flag = documentOneTime;
		if (!flag.HasValue)
		{
			documentOneTime = oneTime;
		}
		if (documentOneTime != oneTime)
		{
			errors[prefix + ".OneTime"] = "One-time and stock material lines cannot be mixed.";
		}
	}

	private static bool IsForceClosed(PoOrder header, IReadOnlyDictionary<string, string?> itemTypes)
	{
		return PoStatusPolicy.IsForceClosed(header, (PoOrderDetail d) => IsServiceLine(d, itemTypes));
	}

	private static bool IsServiceLine(PoOrderDetail detail, IReadOnlyDictionary<string, string?> itemTypes)
	{
		string value;
		return !string.IsNullOrWhiteSpace(detail.ICode) && itemTypes.TryGetValue(detail.ICode, out value) && PoOrderCalc.IsServiceIType(value);
	}

	private static bool IsTerminal(string? status)
	{
		return string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "CLOSED", StringComparison.OrdinalIgnoreCase);
	}

	private static List<PoOrderLineDto> StripEmptyLines(IReadOnlyList<PoOrderLineDto>? lines)
	{
		return (lines ?? Array.Empty<PoOrderLineDto>()).Where((PoOrderLineDto x) => x != null && !IsEmptyLine(x)).ToList();
	}

	private static bool IsEmptyLine(PoOrderLineDto line)
	{
		return string.IsNullOrWhiteSpace(line.ICode) && string.IsNullOrWhiteSpace(line.IDesc) && string.IsNullOrWhiteSpace(line.PrNo) && string.IsNullOrWhiteSpace(line.CjNo) && line.PoPurQty == 0m && line.PoQty == 0m && line.PoUnitPrice == 0m && line.Line <= 0;
	}

	private async Task StampAttachDocIdsInDbAsync(AppDbContext db, string companyCode, string branchCode, string tempDocId, string poNo, short poRelNo, string userId, CancellationToken cancellationToken)
	{
		foreach (PoAttachFile row in await db.PoAttachFiles.Where((PoAttachFile x) => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.DocKey == "PO" && x.DocId == tempDocId && x.CreatedBy == userId).ToListAsync(cancellationToken))
		{
			row.DocId = poNo;
			row.RevNo = poRelNo.ToString();
		}
	}

	private static async Task StampAttachRevNoAsync(AppDbContext db, string companyCode, string branchCode, string poNo, short poRelNo, CancellationToken cancellationToken)
	{
		foreach (PoAttachFile row in await db.PoAttachFiles.Where((PoAttachFile x) => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.DocKey == "PO" && x.DocId == poNo).ToListAsync(cancellationToken))
		{
			row.RevNo = poRelNo.ToString();
		}
	}

	private async Task MoveDraftSafeAsync(string company, string branch, string tempDocId, string poNo, CancellationToken cancellationToken)
	{
		try
		{
			await _attachments.MoveDraftToPoAsync(company, branch, tempDocId, poNo, cancellationToken);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			_logger.LogWarning(ex2, "PO attachment move failed after commit. Company={Company} Branch={Branch} Temp={Temp} PoNo={PoNo}", company, branch, tempDocId, poNo);
		}
	}

	private async Task DiscardDraftSafeAsync(string? tempDocId, CancellationToken cancellationToken)
	{
		if (tempDocId == null)
		{
			return;
		}
		try
		{
			await _attachments.DiscardDraftAsync(tempDocId, cancellationToken);
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "PO draft discard failed. TempDocId={TempDocId}", tempDocId);
		}
	}

	private static string? NormalizeTempDocId(string? tempDocId)
	{
		string text = (tempDocId ?? string.Empty).Trim();
		return text.StartsWith("POTMP-", StringComparison.OrdinalIgnoreCase) ? text : null;
	}

	private static void TouchRowVersion(AppDbContext db, PoOrder header)
	{
		if (!db.Database.IsSqlServer())
		{
			header.RowVersion = Guid.NewGuid().ToByteArray();
		}
	}

	private Task<bool> CanAsync(string permission, CancellationToken cancellationToken)
	{
		return _accessRights.CanAsync("PO_ORDER", permission, cancellationToken);
	}

	private UserContext ValidateUserContext()
	{
		InventoryTenantScope inventoryTenantScope = _tenant.TryBranchScope();
		if (inventoryTenantScope == null)
		{
			return UserContext.Fail("Invalid company or branch context.");
		}
		return UserContext.Ok(inventoryTenantScope.CompanyCode, inventoryTenantScope.BranchCode, inventoryTenantScope.LocationCode, inventoryTenantScope.UserId);
	}

	private UserContext ValidateWriteContext()
	{
		InventoryTenantScope inventoryTenantScope = _tenant.TryWriteScope();
		if (inventoryTenantScope == null)
		{
			return UserContext.Fail("Invalid company, branch, or location context.");
		}
		return UserContext.Ok(inventoryTenantScope.CompanyCode, inventoryTenantScope.BranchCode, inventoryTenantScope.LocationCode, inventoryTenantScope.UserId);
	}

	private static string Truncate(string value, int maxLength)
	{
		return (value.Length <= maxLength) ? value : value.Substring(0, maxLength);
	}

	private static string? TruncateOptional(string? value, int maxLength)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}
		string text = value.Trim();
		return (text.Length <= maxLength) ? text : text.Substring(0, maxLength);
	}

	private static bool RowVersionsEqual(byte[]? left, byte[]? right)
	{
		return left != null && right != null && left.SequenceEqual(right);
	}

	private static bool IsUniqueViolation(DbUpdateException ex)
	{
		if (ex.InnerException is SqlException { Number: var number })
		{
			return (number == 2601 || number == 2627) ? true : false;
		}
		string text = ex.InnerException?.Message ?? ex.Message;
		return text.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) || text.Contains("unique", StringComparison.OrdinalIgnoreCase);
	}
}
