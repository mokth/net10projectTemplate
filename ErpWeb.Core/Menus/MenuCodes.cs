namespace ErpWeb.Core.Menus;

public static class MenuCodes
{
    public const string Dashboard = "DASHBOARD";
    public const string Operations = "OPERATIONS";
    public const string Overview = "OVERVIEW";
    public const string Inventory = "INVENTORY";
    public const string InventoryDemo = "INVENTORY_DEMO";
    public const string InventoryMiscReceipt = "INV_MISC_RECEIPT";
    public const string InventoryGoodsReceipt = "INV_GOODS_RECEIPT";
    public const string InventoryMiscIssue = "INV_MISC_ISSUE";
    public const string InventoryStockTransfer = "INV_STOCK_TRANSFER";
    public const string InventoryScrap = "INV_SCRAP";
    public const string InventoryStockReturn = "INV_STOCK_RETURN";
    public const string InventoryVendorReturn = "INV_VENDOR_RETURN";
    public const string InventoryStockAdjustment = "INV_STOCK_ADJUSTMENT";
    public const string InventoryStockCount = "INV_STOCK_COUNT";
    public const string InventoryItemMaster = "INV_ITEM_MASTER";

    /// <summary>Inventory inquiry parent menu — Balance by Lot is its first child (D6).</summary>
    public const string InventoryInquiry = "INV_INQUIRY";

    /// <summary>Read-only pile-level on-hand inquiry over IvBalLoc (plan-inventoryBalanceByLot).</summary>
    public const string InventoryBalanceLot = "INV_BALANCE_LOT";

    /// <summary>
    /// Posted-movement inquiry over IvTrxHistory. The TrxType multi-select makes this one page
    /// deliver Transaction Inquiry, Adjustment Inquiry and Adjustment Analysis
    /// (plan-inventoryInquirySuite Phase 1).
    /// </summary>
    public const string InventoryTrxInquiry = "INV_TRX_INQUIRY";

    /// <summary>
    /// Stock Card / Stock Movement — the chronological ledger for one stock slice with an opening and
    /// a running balance (plan-inventoryInquirySuite Phase 1, D12/D13).
    /// </summary>
    public const string InventoryStockCard = "INV_STOCK_CARD";

    /// <summary>
    /// Stock control alerts over the item master's own Min/Max thresholds plus movement and expiry
    /// ageing — one page whose Rule selector delivers Low, Over, Slow, Dead, Never moved, Expiring and
    /// Expired (plan-inventoryInquirySuite Phase 2, D14/D15/D21).
    /// </summary>
    public const string InventoryStockAlerts = "INV_STOCK_ALERTS";

    /// <summary>
    /// Lot / Batch inquiry — the lot passport (origin, dates, QC) with its on-hand piles and its
    /// movement history (plan-inventoryInquirySuite Phase 2). Code artefact stays <c>IvLot*</c>
    /// although the screen is titled "Lot / Batch Inquiry".
    /// </summary>
    public const string InventoryLotInquiry = "INV_LOT_INQUIRY";

    /// <summary>
    /// Stock summary — server-side GROUP BY over the balance slice with an Item / Warehouse /
    /// Item×Warehouse / Class selector (plan-inventoryInquirySuite Phase 2, D16).
    /// </summary>
    public const string InventoryStockSummary = "INV_STOCK_SUMMARY";

    /// <summary>
    /// Stock-count variance — variance and line accuracy over POSTED count sheets
    /// (plan-inventoryInquirySuite Phase 3, D4/D17). Its own grant on purpose: reading count evidence
    /// is not the same right as creating, counting, posting or rolling back a sheet.
    /// </summary>
    public const string InventoryStockCountVar = "INV_STOCK_COUNT_VAR";

    /// <summary>
    /// Est. Inventory Value by Item / Warehouse / Class (plan-inventoryInquirySuite Phase 3, D5/D11).
    /// Deliberately NOT titled "Inventory Valuation": there is no costing method, no cost layer and no
    /// revaluation, so the figure is an estimate and cannot tie to a general ledger.
    /// </summary>
    public const string InventoryStockValue = "INV_STOCK_VALUE";

    /// <summary>
    /// Inventory reconciliation — the diagnostic findings of
    /// <c>IIvInventoryReconciliationService</c> (plan-inventoryInquirySuite Phase 3, D6/D18). ACCESS
    /// only, no export: findings are diagnostics, and exporting them invites treating them as an audit
    /// report.
    /// </summary>
    public const string InventoryReconciliation = "INV_RECONCILIATION";

    /// <summary>
    /// Inventory period close (month end) — the action screen that closes / reopens a period
    /// (plan-inventoryPeriodClose). CLOSE + REOPEN are built-in permissions.
    /// </summary>
    public const string InventoryPeriodClose = "INV_PERIOD_CLOSE";

    /// <summary>
    /// Stored closing-balance inquiry — the read-only page over <c>IvPeriodCloseBal</c>
    /// (plan-inventoryPeriodClose Phase 4). ACCESS + EXPORT + VIEW_PRICE.
    /// </summary>
    public const string InventoryPeriodCloseInq = "INV_PERIOD_CLOSE_INQ";

    public const string InventoryWarehouse = "INV_WAREHOUSE";
    public const string InventoryLocation = "INV_LOCATION";
    public const string InventoryStatus = "INV_STATUS";
    public const string InventoryUom = "INV_UOM";
    public const string InventoryType = "INV_TYPE";
    public const string InventoryClass = "INV_CLASS";
    public const string Sales = "SALES";

    /// <summary>
    /// Sales Dashboard (plan-salesReportsAndInquiries.prompt.md Phase 3) — read-only KPI chips and
    /// charts over the current company. ACCESS only. Its chart payloads reuse the analysis service,
    /// so a dashboard role should also hold the analysis menus it renders.
    /// </summary>
    public const string SalesDashboard = "SA_DASHBOARD";

    public const string SalesCustomerProfile = "SA_CUST";    public const string SalesCustType = "SA_CUST_TYPE";
    public const string SalesCustGroup = "SA_CUST_GROUP";
    public const string SalesArea = "SA_AREA";
    public const string SalesCountry = "SA_COUNTRY";
    public const string SalesCurrency = "SA_CURRENCY";
    public const string SalesDisGroup = "SA_DIS_GROUP";
    public const string SalesCurrRate = "SA_CURR_RATE";
    public const string SalesPayTerm = "SA_PAY_TERM";
    public const string SalesSalesRep = "SA_SALES_REP";
    public const string SalesTaxGroup = "SA_TAX_GROUP";
    // Flat sales code-reference family (docs/sales-master-plan.md D-15). One code per master —
    // replaces legacy screen id 700.1.9, which was shared by four unrelated masters.
    public const string SalesCustSubGroup = "SA_CUST_SUB_GROUP";
    public const string SalesShipVia = "SA_SHIP_VIA";
    public const string SalesSoType = "SA_SO_TYPE";
    public const string SalesComment = "SA_COMMENT";
    public const string SalesShipLeadTime = "SA_SHIP_LEAD_TIME";
    public const string SalesLmw = "SA_LMW";

    // Sales item family (plans/sales-item-family-v2-plan.md): price lists, price lines, customer items
    // and item discount rules. One code per screen, following the D-15 convention.
    public const string SalesCustPriceGroup = "SA_CUST_PRICE_GROUP";
    public const string SalesItemCust = "SA_ITEM_CUST";
    public const string SalesDisGroupItem = "SA_DIS_GROUP_ITEM";

    /// <summary>
    /// Phase 6 — the price-inquiry screen. Read-only: it explains where a price came from and why the
    /// other levels did not apply, so it needs ACCESS and nothing else.
    /// </summary>
    public const string SalesPriceInquiry = "SA_PRICE_INQUIRY";

    // Sales-analysis Phase 1 (docs/sales-analysis-phase1). Read-only aggregate inquiries over posted
    // invoices, company-wide sales-rep targets and quotation conversion. The parent carries no route.
    public const string SalesAnalysis = "SA_ANALYSIS";
    public const string SalesAnalysisSummary = "SA_SALES_SUMMARY";
    public const string SalesAnalysisAttainment = "SA_SALES_ATTAINMENT";
    public const string SalesAnalysisQtConversion = "SA_QT_CONVERSION";

    // Sales analysis Phase 2 (plan-salesReportsAndInquiries.prompt.md): item / category / warehouse
    // detail grids over POSTED invoice lines. Read-only — ACCESS only, CSV gated by the same ACCESS.
    public const string SalesByItem = "SA_SALES_ITEM";
    public const string SalesByCategory = "SA_SALES_CATEGORY";
    public const string SalesByWarehouse = "SA_SALES_WAREHOUSE";

    // Sales Inquiry (plan-salesReportsAndInquiries.prompt.md Phase 1) — read-only operational grids.
    // The parent carries no route. ACCESS only; the CSV downloads are gated by the same ACCESS check
    // inside the service, so no EXPORT permission is seeded (matches the analysis screens).
    public const string SalesInquiry = "SA_INQUIRY";
    public const string SalesCustomerTransaction = "SA_CUST_TRX";
    public const string SalesQtStatus = "SA_QT_STATUS";
    public const string SalesSoOutstanding = "SA_SO_OUTSTANDING";
    public const string SalesDoStatus = "SA_DO_STATUS";
    public const string SalesInvVsDoc = "SA_INV_VS_DOC";
    public const string SalesCdnInquiry = "SA_CDN_INQUIRY";
    public const string SalesEInvoiceInquiry = "SA_EINV_INQUIRY";
    public const string SalesInvoiceInquiry = "SA_INV_INQUIRY";
    public const string SalesSoTransactions = "SA_SO_TRX";
    public const string SalesPriceHistory = "SA_PRICE_HISTORY";

    // Sales Monitor (plan-salesDecisionSupport.prompt.md Phase A) — read-only decision-support screens
    // over the existing columns. The parent carries no route. ACCESS only; the CSV downloads call the
    // same service method as the grid, under the same ACCESS check.
    public const string SalesMonitor = "SA_MONITOR";
    public const string SalesSoAgeing = "SA_SO_AGEING";
    public const string SalesDoNotFullyInvoiced = "SA_DO_NOT_FULLY_INVOICED";
    public const string SalesQtExpiry = "SA_QT_EXPIRY";
    public const string SalesEInvoiceAction = "SA_EINV_ACTION";

    public const string SalesInvoice = "SA_INVOICE";
    public const string SalesDeliveryOrder = "SA_DO";
    public const string SalesOrder = "SA_SO";
    /// <summary>Sales Quotation — the commercial offer that precedes a Sales Order.</summary>
    public const string SalesQuotation = "SA_QT";
    public const string SalesCreditNote = "SA_CN";
    public const string SalesDebitNote = "SA_DN";
    /// <summary>E7: report-only credit-note reservation screen.</summary>
    public const string SalesCdnReservations = "SA_CN_RESERVATIONS";
    /// <summary>
    /// LHDN e-Invoice TIN tools (<c>/sales/einvoice/tin</c>): validate a TIN against an identity
    /// document and search taxpayers. Read-only against MyInvois - ACCESS only.
    /// </summary>
    public const string SalesEInvoiceTin = "SA_EINVOICE_TIN";
    public const string Purchase = "PURCHASE";
    public const string PurchaseMaster = "PO_MASTER";
    public const string PurchaseSupplierProfile = "PO_SUPPLIER";
    public const string PurchaseBuyer = "PO_BUYER";
    public const string PurchaseBuyingTerm = "PO_BUYING_TERM";
    public const string PurchaseCategory = "PO_CATEGORY";
    public const string PurchaseAuthorised = "PO_AUTHORISED";
    public const string PurchasePurItem = "PO_PUR_ITEM";
    public const string PurchaseTransactions = "PO_TRANSACTIONS";
    public const string PurchaseRequisition = "PO_PR";
    public const string PurchaseOrder = "PO_ORDER";
    public const string PurchaseInvoice = "PO_INVOICE";
    public const string PurchaseCreditNote = "PO_CN";
    public const string PurchaseDebitNote = "PO_DN";
    public const string PurchaseCreditNoteReservations = "PO_CN_RESERVATIONS";

    /// <summary>Self-billed purchase invoice (LHDN 11).</summary>
    public const string PurchaseSbInvoice = "PO_SB_INVOICE";

    /// <summary>Self-billed purchase credit note (LHDN 12).</summary>
    public const string PurchaseSbCreditNote = "PO_SB_CN";

    /// <summary>Self-billed purchase debit note (LHDN 13).</summary>
    public const string PurchaseSbDebitNote = "PO_SB_DN";

    // Purchase Inquiry (Phase 1) — read-only operational grids. Parent has no route. ACCESS only.
    public const string PurchaseInquiry = "PO_INQUIRY";
    public const string PurchaseOrderOutstanding = "PO_ORDER_OUTSTANDING";
    public const string PurchasePrStatus = "PO_PR_STATUS";
    public const string PurchaseSupplierTransaction = "PO_SUPP_TRX";
    public const string PurchaseInvoiceInquiry = "PO_INV_INQUIRY";
    public const string PurchaseCdnInquiry = "PO_CDN_INQUIRY";
    public const string PurchaseDocRelationship = "PO_DOC_REL";
    public const string PurchaseSbEInvoiceInquiry = "PO_SB_EINV_INQUIRY";

    // Purchase Inquiry Phase 2
    public const string PurchasePriceHistory = "PO_PRICE_HISTORY";
    public const string PurchaseMatching = "PO_MATCHING";
    public const string PurchaseDeliveryPerformance = "PO_DELIVERY_PERF";

    public const string Planning = "PLANNING";
    public const string PlanningMaster = "PLN_MASTER";
    public const string PlanningProductDef = "PLN_PRODUCT_DEF";
    public const string PlanningWorkCentre = "PLN_WORK_CENTRE";
    public const string PlanningWorkProcess = "PLN_WORK_PROCESS";
    public const string PlanningWorkMachine = "PLN_WORK_MACHINE";
    public const string PlanningWcHierarchy = "PLN_WC_HIERARCHY";
    public const string PlanningShift = "PLN_SHIFT";
    public const string PlanningShiftGroup = "PLN_SHIFT_GROUP";
    public const string PlanningCompanyCal = "PLN_COMPANY_CAL";
    public const string PlanningMacShiftCal = "PLN_MAC_SHIFT_CAL";
    public const string PlanningOperator = "PLN_OPERATOR";
    public const string PlanningWorkPrefix = "PLN_WORK_PREFIX";
    public const string PlanningMacSeq = "PLN_MAC_SEQ";
    public const string PlanningMacPreventive = "PLN_MAC_PREVENTIVE";
    public const string PlanningMaintenanceReason = "PLN_MAINT_REASON";
    public const string PlanningImportPrdDef = "PLN_IMPORT_PRDDEF";
    public const string PlanningTransactions = "PLN_TRANSACTIONS";
    public const string PlanningWorkOrder = "PLN_WORK_ORDER";
    public const string PlanningMaterialIssue = "PLN_MATERIAL_ISSUE";
    public const string PlanningDailyProduction = "PLN_DAILY_PRODUCTION";
    public const string PlanningProductionBalance = "PLN_PRODUCTION_BALANCE";
    public const string PlanningStockCard = "PLN_STOCK_CARD";
    public const string PlanningStockMovement = "PLN_STOCK_MOVEMENT";
    public const string PlanningStockAsOf = "PLN_STOCK_ASOF";
    public const string PlanningStockReconciliation = "PLN_STOCK_RECONCILIATION";
    public const string PlanningMachineMaintenance = "PLN_MAC_MAINT";
    public const string PlanningInquiry = "PLN_INQUIRY";
    public const string PlanningMachineSummary = "PLN_MAC_SUMMARY";


    public const string Security = "SECURITY";
    public const string Admin = "ADMIN";
    public const string AdminDemo = "ADMIN_DEMO";
    public const string AdminUsers = "ADMIN_USERS";
    public const string AdminRoles = "ADMIN_ROLES";
    public const string AdminPermissions = "ADMIN_PERMISSIONS";
    public const string AdminRolePermissions = "ADMIN_ROLE_PERMISSIONS";
    public const string AdminCompany = "ADMIN_COMPANY";
    public const string AdminMaster = "ADMIN_MASTER";
    public const string AdminSmNum = "SA_SM_NUM";
    public const string AdminSmNumDate = "SA_SM_NUM_DATE";
    public const string AdminDept = "ADMIN_DEPT";
    public const string AdminProject = "ADMIN_PROJECT";

    /// <summary>
    /// The dynamic application settings screen (<c>/admin/settings</c>). Reads of a setting are NOT gated
    /// by this code — they are a server capability — but listing, saving and clearing are, because those
    /// are the admin screen's operations.
    /// </summary>
    public const string AdminSettings = "ADMIN_SETTINGS";
    /// <summary>Obsolete alias — use <see cref="AdminSmNum"/>.</summary>
    public const string SalesSmNum = AdminSmNum;
    /// <summary>Obsolete alias — use <see cref="AdminSmNumDate"/>.</summary>
    public const string SalesSmNumDate = AdminSmNumDate;
    public const string ChangePassword = "CHANGE_PASSWORD";
}
