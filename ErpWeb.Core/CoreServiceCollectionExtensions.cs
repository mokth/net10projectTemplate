using ErpWeb.Core.Admin;
using ErpWeb.Core.Costing;
using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Production;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.Transactions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;



namespace ErpWeb.Core;



public static class CoreServiceCollectionExtensions

{

    public static IServiceCollection AddErpWebCore(this IServiceCollection services, IConfiguration configuration)

    {

        services.AddSingleton<IValidateOptions<PasswordPolicyOptions>, PasswordPolicyOptionsValidator>();



        services.AddOptions<PasswordPolicyOptions>()

            .Bind(configuration.GetSection(PasswordPolicyOptions.SectionName))

            .ValidateOnStart();



        services.AddSingleton<IValidateOptions<MenusOptions>, MenusOptionsValidator>();

        services.AddOptions<MenusOptions>()

            .Bind(configuration.GetSection(MenusOptions.SectionName))

            .ValidateOnStart();



        services.AddOptions<AttachmentStorageOptions>()

            .Bind(configuration.GetSection(AttachmentStorageOptions.SectionName));



        services.AddOptions<PoPrOptions>()

            .Bind(configuration.GetSection(PoPrOptions.SectionName));

        services.AddOptions<PoOrderOptions>()

            .Bind(configuration.GetSection(PoOrderOptions.SectionName));



        services.AddSingleton<IPasswordPolicy, PasswordPolicy>();

        services.AddSingleton<IMenuDefinitionService, MenuDefinitionService>();

        services.AddSingleton<MenuCache>();



        services.AddScoped<IAuthService, AuthService>();

        services.AddScoped<ICurrentUserService, CurrentUserService>();

        services.AddScoped<ITenantScopeContext, TenantScopeContext>();

        services.AddScoped<IInventoryTenantContext, InventoryTenantContext>();
        services.AddScoped<IStockPostingCoordinator, StockPostingCoordinator>();
        services.AddScoped<IInventoryValuationService, InventoryValuationService>();
        services.AddScoped<IStockCostMethodResolver, StockCostMethodResolver>();
        services.AddScoped<IItemStandardCostResolver, ItemStandardCostResolver>();
        services.AddScoped<ICompanyCurrencyRateResolver, CompanyCurrencyRateResolver>();
        services.AddScoped<IStockValueAdjustmentWriter, StockValueAdjustmentWriter>();
        services.AddScoped<IStandardCostRevaluationService, StandardCostRevaluationService>();
        services.AddScoped<IStockCostMethodCutoverService, StockCostMethodCutoverService>();
        services.AddScoped<IPurchaseCostPostingCommandFactory, PurchaseCostPostingCommandFactory>();
        services.AddScoped<IPurchaseCostAdjustmentPostingService, PurchaseCostAdjustmentPostingService>();
        services.AddScoped<IPurchaseReceiptCostSettlementService, PurchaseReceiptCostSettlementService>();
        services.AddScoped<IPurchaseCdnCostAdjustmentService, PurchaseCdnCostAdjustmentService>();
        services.AddScoped<IPurchaseCostInquiryService, PurchaseCostInquiryService>();
        services.AddScoped<IStockValuationQueryService, StockValuationQueryService>();
        services.AddScoped<ICostingDiagnosticService, CostingDiagnosticService>();
        services.AddScoped<ITransactionDeletePolicyService, TransactionDeletePolicyService>();
        services.AddScoped<ICostingTraceService, CostingTraceService>();
        services.AddScoped<ICostingRepairOwnershipResolver, CostingRepairOwnershipResolver>();
        services.AddScoped<CostingStateRepairPlanner>();
        services.AddScoped<CostingStateRepairService>();
        services.AddScoped<ICostingRepairPlanner, CostingRepairPlanner>();
        services.AddScoped<ICostingRepairService, CostingRepairService>();
        services.AddScoped<ICostingRepairAdapter, InventoryCostingRepairAdapter>();
        services.AddScoped<ICostingRepairAdapter, SalesInvoiceCostingRepairAdapter>();
        services.AddScoped<ICostingRepairAdapter, SalesDeliveryOrderCostingRepairAdapter>();
        services.AddScoped<ICostingRepairAdapter, SalesCreditNoteCostingRepairAdapter>();
        services.AddScoped<ICostingRepairAdapter, PurchaseGoodsReceiptRepairAdapter>();
        services.AddScoped<ICostingRepairAdapter, PurchaseCdnStockRepairAdapter>();
        services.AddScoped<ICostingRepairAdapter, ProductionMaterialIssueRepairAdapter>();
        services.AddScoped<ICostingRepairAdapter, ProductionOutputRepairAdapter>();
        services.AddScoped<ICostingRepairAdapter, ProductionFinishedGoodReceiptRepairAdapter>();
        services.AddScoped<IStockValuationReportService, StockValuationReportService>();
        services.AddScoped<IBranchStockTransactionLock, BranchStockTransactionLock>();
        services.AddScoped<IStockPeriodGuard, StockPeriodGuard>();
        services.AddScoped<IStockFreezeGuard, NoActiveStockFreezeGuard>();
        services.AddSingleton<IStockMovementRegistry, StockMovementRegistry>();

        services.AddMemoryCache();

        services.AddSingleton<ErpWeb.Core.Settings.AppSettingCacheVersions>();

        services.AddScoped<ErpWeb.Core.Settings.IAppSettingValueProvider, ErpWeb.Core.Settings.SaPriceMethodSettingProvider>();

        services.AddScoped<ErpWeb.Core.Settings.AppSettingProviderRegistry>();

        services.AddScoped<ErpWeb.Core.Settings.IAppSettingService, ErpWeb.Core.Settings.AppSettingService>();

        services.AddScoped<IUserAdminService, UserAdminService>();

        services.AddScoped<IRoleAdminService, RoleAdminService>();

        services.AddScoped<IPermissionAdminService, PermissionAdminService>();

        services.AddScoped<IRoleMenuPermissionAdminService, RoleMenuPermissionAdminService>();

        services.AddScoped<ICompanyService, CompanyService>();

        services.AddScoped<IUserRoleSyncService, UserRoleSyncService>();

        services.AddScoped<IMenuService, MenuService>();

        services.AddScoped<IMenuSyncService, MenuSyncService>();

        services.AddScoped<IAccessRightService, AccessRightService>();

        services.AddScoped<INavigationService, NavigationService>();

        services.AddScoped<IRunningNumberService, RunningNumberService>();

        services.AddScoped<IDocumentNumberingService, DocumentNumberingService>();

        services.AddScoped<IAdSmNumAdminService, AdSmNumAdminService>();

        services.AddScoped<IMsRefService, MsRefService>();

        services.AddScoped<ICurrentDateService, CurrentDateService>();

        services.AddScoped<IIvInventoryLookupService, IvInventoryLookupService>();

        services.AddScoped<IIvMiscReceiptService, IvMiscReceiptService>();

        services.AddScoped<IIvGoodsReceiptService, IvGoodsReceiptService>();

        services.AddScoped<IIvMiscIssueService, IvMiscIssueService>();

        services.AddScoped<IIvVendorReturnService, IvVendorReturnService>();

        services.AddScoped<IIvStockTransferService, IvStockTransferService>();

        services.AddScoped<IIvScrapService, IvScrapService>();

        services.AddScoped<IIvStockReturnService, IvStockReturnService>();

        services.AddScoped<IIvStockAdjustmentService, IvStockAdjustmentService>();

        services.AddScoped<IIvStockCountService, IvStockCountService>();

        services.AddScoped<IIvBalanceLotService, IvBalanceLotService>();

        services.AddScoped<IIvTrxHistoryService, IvTrxHistoryService>();

        // Inventory inquiry suite, Phase 2 — alerts, lot passport and grouped summary
        // (plans/plan-inventoryInquirySuite.prompt.md §Phase 2).
        services.AddScoped<IIvStockAlertService, IvStockAlertService>();
        services.AddScoped<IIvLotInquiryService, IvLotInquiryService>();
        services.AddScoped<IIvStockSummaryService, IvStockSummaryService>();

        // Inventory period close (month end) — the close/reopen workflow + stored closing balances
        // (plans/plan-inventoryPeriodClose.prompt.md Phase 2).
        services.AddScoped<IIvPeriodCloseService, IvPeriodCloseService>();

        services.AddScoped<IIvInventoryPostingService, IvInventoryPostingService>();
        services.AddScoped<IIvInventoryHistoryWriter, IvInventoryHistoryWriter>();
        services.AddScoped<IInventoryAsOfStockService, InventoryAsOfStockService>();

        services.AddScoped<IIvSpShipmentService, IvSpShipmentService>();

        services.AddScoped<IIvInventoryReconciliationService, IvInventoryReconciliationService>();
        services.AddScoped<ISaAllocationReconciliationService, SaAllocationReconciliationService>();
        services.AddScoped<ISaDocFlowQuery, SaDocFlowQuery>();

        services.AddScoped<IIvStockMasterService, IvStockMasterService>();
        // Single owner of item UOM conversion (Production Work Order Enhancement Plan §5.5).
        services.AddScoped<IUomConversionService, IvUomConversionService>();


        services.AddScoped<IPrProductDefService, PrProductDefService>();
        services.AddScoped<BomExplosionService>();
        services.AddScoped<IBomExplosionService>(sp => sp.GetRequiredService<BomExplosionService>());
        services.AddScoped<IProductionWorkOrderService, ProductionWorkOrderService>();
        services.AddScoped<IProductionMaterialAllocationService, ProductionMaterialAllocationService>();
        services.AddSingleton<IProductionOperationEligibilityService, ProductionOperationEligibilityService>();
        services.AddScoped<IProductionMaterialIssueDraftReservationReader, ProductionMaterialIssueDraftReservationReader>();
        services.AddScoped<IProductionMaterialReconciliationService, ProductionMaterialReconciliationService>();
        services.AddSingleton<IProductionContributionAllocator, ProductionContributionAllocator>();
        services.AddScoped<IProductionStockWriter, ProductionStockWriter>();
        services.AddScoped<IProductionMaterialIssueService, ProductionMaterialIssueService>();
        services.AddOptions<FinishedGoodReceiptOptions>().BindConfiguration("Production:FinishedGoodReceipt");
        services.AddScoped<IProductionFinishedGoodReceiptService, ProductionFinishedGoodReceiptService>();
        services.AddScoped<IProductionOutputService, ProductionOutputService>();
        services.AddScoped<IProductionBalanceInquiryService, ProductionBalanceInquiryService>();
        services.AddScoped<IProductionMaterialConsumeVarianceInquiryService, ProductionMaterialConsumeVarianceInquiryService>();
        services.AddScoped<IProductionStockHistoryService, ProductionStockHistoryService>();
        // Version-2 snapshot quantity contract (plan §7.3).
        services.AddScoped<IWorkOrderQuantityCalculator, WorkOrderQuantityCalculator>();
        services.AddScoped<IWorkOrderReadinessValidator, WorkOrderReadinessValidator>();
        // Resolves the exact Product Definition revision for snapshot creation (plan §7.2).
        services.AddScoped<IProductDefinitionSnapshotLoader, ProductDefinitionSnapshotLoader>();
        // Builds the version-2 snapshot graph from the resolved revision (plan §7.1–§7.4).
        services.AddScoped<IWorkOrderSnapshotBuilder, WorkOrderSnapshotBuilder>();
        services.AddScoped<IWorkOrderCalendarProvider, WorkOrderCalendarProvider>();
        services.AddScoped<IWorkOrderScheduleCalculator, WorkOrderScheduleCalculator>();
        services.Configure<ProductionWorkOrderOptions>(
            configuration.GetSection(ProductionWorkOrderOptions.SectionName));
        services.AddScoped<IProductionCalendarScheduleDataLoader, ProductionCalendarScheduleDataLoader>();

        services.AddScoped<IPlanningMasterAccess, PlanningMasterAccess>();
        services.AddScoped<IPlanningDependencyChecker, PlanningDependencyChecker>();
        services.AddScoped<IPrWorkCentreService, PrWorkCentreService>();
        services.AddScoped<IPrProcessService, PrProcessService>();
        services.AddScoped<IPrMachineService, PrMachineService>();
        services.AddScoped<IPrHierarchyService, PrHierarchyService>();
        services.AddScoped<IPrShiftService, PrShiftService>();
        services.AddScoped<IPrShiftGroupService, PrShiftGroupService>();
        services.AddScoped<IPrShiftGroupPlanningService, PrShiftGroupPlanningService>();
        services.AddScoped<IPrCalendarService, PrCalendarService>();
        services.AddScoped<IPrShiftCalendarService, PrShiftCalendarService>();
        services.AddScoped<IPrHolidayService, PrHolidayService>();
        services.AddHttpClient(nameof(CalendarificHolidayClient));
        services.AddScoped<ICalendarificHolidayClient, CalendarificHolidayClient>();
        services.AddScoped<IPrOperatorService, PrOperatorService>();
        services.AddScoped<IPrWorkPrefixService, PrWorkPrefixService>();
        services.AddScoped<IPrMacSeqService, PrMacSeqService>();
        services.AddScoped<IPrPreventiveService, PrPreventiveService>();
        services.AddScoped<IPrMaintenanceReasonService, PrMaintenanceReasonService>();
        services.AddScoped<IPrMachineMaintenanceService, PrMachineMaintenanceService>();
        services.AddScoped<IPrMachineMaintenanceSummaryService, PrMachineMaintenanceSummaryService>();
        services.AddScoped<IPrDefImportService, PrDefImportService>();

        services.AddScoped<IIvInventoryRefService, IvInventoryRefService>();

        services.AddScoped<ISaCustService, SaCustService>();

        services.AddScoped<ISaCustLookupService, SaCustLookupService>();

        services.AddScoped<IPoSupplierService, PoSupplierService>();

        services.AddScoped<IPoSupplierLookupService, PoSupplierLookupService>();

        services.AddScoped<IPoPurchasingItemLookupService, PoPurchasingItemLookupService>();

        services.AddScoped<IPoSupplierAttachmentService, PoSupplierAttachmentService>();

        services.AddScoped<IPoSupplierAttachmentFileCleanup>(sp =>
            (IPoSupplierAttachmentFileCleanup)sp.GetRequiredService<IPoSupplierAttachmentService>());

        services.AddScoped<IPoMasterRefService, PoMasterRefService>();

        services.AddScoped<IPoPrService, PoPrService>();

        services.AddScoped<IPoPrAttachmentService, PoPrAttachmentService>();

        services.AddScoped<IPoOrderService, PoOrderService>();

        services.AddScoped<IPoOrderAttachmentService, PoOrderAttachmentService>();

        services.AddScoped<IPoInvoiceService, PoInvoiceService>();

        services.AddScoped<IPoCdnService, PoCdnService>();

        // Self-billed e-Invoice documents (SBI / SBC / SBD) — slim purchase documents whose only job is
        // to carry a valid MyInvois payload. See plans/plan-poSelfBilledEInvoice.prompt.md.
        services.AddScoped<IPoSbInvoiceService, PoSbInvoiceService>();
        services.AddScoped<IPoSbCdnService, PoSbCdnService>();

        services.AddScoped<ISaSalesRefService, SaSalesRefService>();

        services.AddScoped<ISaInvoiceService, SaInvoiceService>();
        services.AddScoped<ISaDoService, SaDoService>();
        // The quotation converter needs SaSoService's snapshot-import entry point (it is deliberately
        // off the ISaSoService contract), so the concrete type is registered and both registrations
        // resolve to the SAME scoped instance.
        services.AddScoped<SaSoService>();
        services.AddScoped<ISaSoService>(sp => sp.GetRequiredService<SaSoService>());
        services.AddScoped<ISaQtService, SaQtService>();
        services.AddScoped<ISaCdnService, SaCdnService>();
        services.AddScoped<ISalesGrossProfitInquiryService, SalesGrossProfitInquiryService>();
        services.AddScoped<ISaDocApplication, SaDocApplicationService>();

        services.AddScoped<ISaSalesAnalysisService, SaSalesAnalysisService>();

        // Sales Inquiry — read-only operational grids (plan-salesReportsAndInquiries.prompt.md Phase 1).
        services.AddScoped<ISaSalesInquiryService, SaSalesInquiryService>();

        // Purchase Inquiry — read-only operational grids (plans/Procurement-Inquiry-Assessment.md Phase 1).
        services.AddScoped<IPoPurchaseInquiryService, PoPurchaseInquiryService>();

        // Sales Dashboard (plan-salesReportsAndInquiries.prompt.md Phase 3).
        services.AddScoped<ISaSalesDashboardService, SaSalesDashboardService>();



        // LHDN MyInvois e-Invoice adapter. Registered scoped so per-company credentials stay

        // isolated per request.

        services.AddErpWebEInvoice(configuration);



        return services;

    }

}

