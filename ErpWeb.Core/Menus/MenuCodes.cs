namespace ErpWeb.Core.Menus;

public static class MenuCodes
{
    public const string Home = "HOME";
    public const string Operations = "OPERATIONS";
    public const string Overview = "OVERVIEW";
    public const string Dashboard = "DASHBOARD";
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
    public const string InventoryItemMaster = "INV_ITEM_MASTER";
    public const string InventoryWarehouse = "INV_WAREHOUSE";
    public const string InventoryLocation = "INV_LOCATION";
    public const string InventoryStatus = "INV_STATUS";
    public const string InventoryUom = "INV_UOM";
    public const string InventoryType = "INV_TYPE";
    public const string InventoryClass = "INV_CLASS";
    public const string Sales = "SALES";
    public const string SalesCustomerProfile = "SA_CUST";
    public const string SalesCustType = "SA_CUST_TYPE";
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
