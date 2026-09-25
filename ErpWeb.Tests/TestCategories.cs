namespace ErpWeb.Tests;

/// <summary>
/// xUnit trait values used to group the suite into runnable categories.
/// </summary>
/// <remarks>
/// <para>
/// The whole suite is ~2,000 tests in one assembly, so an unfiltered <c>dotnet test</c> runs
/// everything. Every test class carries <c>[Trait(TestCategories.Name, ...)]</c> so a run can be
/// narrowed with <c>dotnet test --filter "Category=Sales"</c>.
/// </para>
/// <para>
/// All dimensions use the same trait name, so they compose in a single filter:
/// <list type="bullet">
///   <item><description><b>module</b> — <see cref="Sales"/>, <see cref="Purchase"/>, <see cref="Inventory"/>,
///   <see cref="Menus"/>, <see cref="Settings"/>, <see cref="Admin"/>, <see cref="Shared"/>. Exactly one per class.</description></item>
///   <item><description><b>screen / document family</b> — e.g. <see cref="SalesInvoice"/>, <see cref="PurchaseOrder"/>,
///   <see cref="InventoryTransfer"/>. An EXTRA trait on the classes that belong to one screen, so
///   <c>Category=Sales</c> is the whole module while <c>Category=SalesInvoice</c> is just that document.</description></item>
///   <item><description><see cref="SqlServer"/> — an EXTRA trait on <c>*SqlServerConcurrencyTests</c>, which
///   need a live SQL Server scratch database and are the slowest tests in the suite.</description></item>
/// </list>
/// Because <c>!=</c> also matches tests that do not carry the trait at all,
/// <c>--filter "Category!=SqlServer"</c> is the fast day-to-day run, and
/// <c>--filter "Category=SalesInvoice&amp;Category!=SqlServer"</c> is one document with no DB suites.
/// </para>
/// <para>
/// Adding a new test class: give it exactly one module trait, plus a screen trait when it belongs to a
/// specific document/screen, plus <see cref="SqlServer"/> when it needs a real SQL Server. Class-level
/// traits cover every test in the class, so no per-test attribute is needed.
/// </para>
/// </remarks>
public static class TestCategories
{
    /// <summary>The trait NAME every category is published under, i.e. the <c>Category=...</c> in a filter.</summary>
    public const string Name = "Category";

    // ── Module axis: the feature area. Every test class carries exactly one. ──────────────────────

    /// <summary>All sales: masters, pricing, quotations, orders, deliveries, invoices, credit/debit notes.</summary>
    public const string Sales = "Sales";

    /// <summary>Sales e-Invoice submission / payload / LHDN handling.</summary>
    public const string EInvoice = "EInvoice";

    /// <summary>All purchase: masters, requisitions, orders, invoices, credit/debit notes, self-billed.</summary>
    public const string Purchase = "Purchase";

    /// <summary>All inventory: masters, receipts, issues, transfers, returns, adjustments, postings.</summary>
    public const string Inventory = "Inventory";

    /// <summary>Menu definition, deployment parity and navigation chrome.</summary>
    public const string Menus = "Menus";

    /// <summary>Global settings registry (AdSmParam) and app-setting resolution/caching.</summary>
    public const string Settings = "Settings";

    /// <summary>Users, roles, access rights and passwords.</summary>
    public const string Admin = "Admin";

    /// <summary>Cross-cutting helpers: numbering, formatting, validation text, DI wiring, navigation.</summary>
    public const string Shared = "Shared";

    // ── Screen axis: the screen / document family. Carried IN ADDITION to a module trait. ─────────

    /// <summary>Sales — customer/ref masters, sales reps, payment terms, item family CRUD, LMW.</summary>
    public const string SalesMasters = "SalesMasters";

    /// <summary>Sales — pricing engine: source resolution, discount maths, document totals.</summary>
    public const string SalesPricing = "SalesPricing";

    /// <summary>Sales — quotation (SaQt).</summary>
    public const string SalesQt = "SalesQt";

    /// <summary>Sales — sales order (SaSo), including revisions and line reservations.</summary>
    public const string SalesSo = "SalesSo";

    /// <summary>Sales — delivery order (SaDo).</summary>
    public const string SalesDo = "SalesDo";

    /// <summary>Sales — invoice (SaInvoice).</summary>
    public const string SalesInvoice = "SalesInvoice";

    /// <summary>Sales — credit/debit note (SaCdn).</summary>
    public const string SalesCdn = "SalesCdn";

    /// <summary>Sales — cross-document plumbing shared by every sales entry screen.</summary>
    public const string SalesShared = "SalesShared";

    /// <summary>Purchase — vendor/ref masters (supplier, master reference).</summary>
    public const string PurchaseMasters = "PurchaseMasters";

    /// <summary>Purchase — requisition (PoPr).</summary>
    public const string PurchasePr = "PurchasePr";

    /// <summary>Purchase — order (PoOrder), including the pure order-calc tests.</summary>
    public const string PurchaseOrder = "PurchaseOrder";

    /// <summary>Purchase — invoice (PoInvoice).</summary>
    public const string PurchaseInvoice = "PurchaseInvoice";

    /// <summary>Purchase — credit/debit note (PoCdn), including the pure CN/DN calc tests.</summary>
    public const string PurchaseCdn = "PurchaseCdn";

    /// <summary>Purchase — self-billed invoice / credit / debit note (PoSb*).</summary>
    public const string PurchaseSelfBilled = "PurchaseSelfBilled";

    /// <summary>Inventory — stock master, code reference data, lot numbering.</summary>
    public const string InventoryMasters = "InventoryMasters";

    /// <summary>Inventory — goods receipt.</summary>
    public const string InventoryGoodsReceipt = "InventoryGoodsReceipt";

    /// <summary>Inventory — miscellaneous issue.</summary>
    public const string InventoryMiscIssue = "InventoryMiscIssue";

    /// <summary>Inventory — miscellaneous receipt.</summary>
    public const string InventoryMiscReceipt = "InventoryMiscReceipt";

    /// <summary>Inventory — scrap.</summary>
    public const string InventoryScrap = "InventoryScrap";

    /// <summary>Inventory — stock adjustment.</summary>
    public const string InventoryAdjustment = "InventoryAdjustment";

    /// <summary>Inventory — physical stock count (cycle count).</summary>
    public const string InventoryStockCount = "InventoryStockCount";

    /// <summary>Inventory — Balance by Lot pile-level on-hand inquiry.</summary>
    public const string InventoryBalanceLot = "InventoryBalanceLot";

    /// <summary>Inventory — posted-movement inquiry over IvTrxHistory (transaction / adjustment).</summary>
    public const string InventoryTrxInquiry = "InventoryTrxInquiry";

    /// <summary>Inventory — Stock Card ledger with opening and running balance.</summary>
    public const string InventoryStockCard = "InventoryStockCard";

    /// <summary>Inventory — stock control alerts (low/over/slow/dead/never-moved/expiring/expired).</summary>
    public const string InventoryStockAlerts = "InventoryStockAlerts";

    /// <summary>Inventory — Lot / Batch inquiry (lot passport, piles, movements).</summary>
    public const string InventoryLotInquiry = "InventoryLotInquiry";

    /// <summary>Inventory — Stock summary (server-side GROUP BY over the balance slice).</summary>
    public const string InventoryStockSummary = "InventoryStockSummary";

    /// <summary>Inventory — stock-count variance and line accuracy over POSTED sheets.</summary>
    public const string InventoryStockCountVar = "InventoryStockCountVar";

    /// <summary>Inventory — Est. Inventory Value grouped by item / warehouse / class.</summary>
    public const string InventoryStockValue = "InventoryStockValue";

    /// <summary>Inventory — reconciliation findings (diagnostic slice-vs-ledger comparison).</summary>
    public const string InventoryReconciliation = "InventoryReconciliation";

    /// <summary>Inventory — stock return.</summary>
    public const string InventoryStockReturn = "InventoryStockReturn";

    /// <summary>Inventory — vendor return.</summary>
    public const string InventoryVendorReturn = "InventoryVendorReturn";

    /// <summary>Inventory — stock transfer.</summary>
    public const string InventoryTransfer = "InventoryTransfer";

    /// <summary>Inventory — the posting engine shared by every inventory document.</summary>
    public const string InventoryPosting = "InventoryPosting";

    /// <summary>Inventory — period close (month end), the guard and the close/reopen workflow.</summary>
    public const string InventoryPeriodClose = "InventoryPeriodClose";

    // ── Environment axis ──────────────────────────────────────────────────────────────────────────

    /// <summary>Needs a live SQL Server scratch database. Slow — excluded from the default fast run.</summary>
    public const string SqlServer = "SqlServer";
}
