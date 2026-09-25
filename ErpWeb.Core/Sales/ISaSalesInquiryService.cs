using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Read-only Sales Inquiry layer (plan-salesReportsAndInquiries Phase 1). Operational per-document
/// grids over the entities that already carry the fields, plus the e-Invoice status/history/reconciliation
/// reads. Nothing here writes to the transactional sales tables.
///
/// <para>
/// Every method takes a <c>menuCode</c> first so each screen carries its own ACCESS grant (the
/// <see cref="IIvStockCountService.Variance">IvStockCountService.Variance</see> precedent) — a caller
/// holding one inquiry menu must never read another screen's data. Tenant scope is resolved first
/// (fail closed), then ACCESS is checked on the caller's own menu, in that order. The CSV export calls
/// these same methods, so a downloaded file can never disagree with the grid (R9 parity).
/// </para>
/// </summary>
public interface ISaSalesInquiryService
{
    /// <summary>
    /// Customer transaction log — the union of QT / SO / DO / INV / CN / DN for the filtered customer,
    /// server-side paged. Every status is included; filter by <see cref="SaInquiryQuery.Status"/> to
    /// narrow.
    /// </summary>
    Task<IvMasterOperationResult<SaInquiryPage<SaCustomerTransactionRow>>> GetCustomerTransactionsAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Customer sales history — POSTED invoices netted with POSTED CN/DN, grouped by calendar month.
    /// Drafts never appear (the POSTED-only boundary copied from <c>SaSalesAnalysisService</c>).
    /// </summary>
    Task<IvMasterOperationResult<IReadOnlyList<SaCustomerSalesHistoryRow>>> GetCustomerSalesHistoryAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Quotation status / expiry — live (<c>IsCurrent</c>) revisions with their validity window.</summary>
    Task<IvMasterOperationResult<SaInquiryPage<SaQtStatusRow>>> GetQtStatusAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sales Order outstanding — current (<c>IsCurrent</c>) SO lines with the persisted quantity
    /// rollups. One query answers Open SO, Outstanding Quantity, Delivery Status, Invoice Status and
    /// Backorder.
    /// </summary>
    Task<IvMasterOperationResult<SaInquiryPage<SaSoOutstandingRow>>> GetSoOutstandingAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Delivery Order status — all DO lines with their billing state and SO / invoice references.</summary>
    Task<IvMasterOperationResult<SaInquiryPage<SaDoStatusRow>>> GetDoStatusAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Document relationships — SO↔DO, DO↔INV, INV↔DO and INV↔SO from the allocation ledger.</summary>
    Task<IvMasterOperationResult<SaInquiryPage<SaDocumentRelationshipRow>>> GetDocumentRelationshipAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Combined CN/DN inquiry, both types with a <see cref="SaInquiryQuery.Type"/> filter.</summary>
    Task<IvMasterOperationResult<SaInquiryPage<SaCdnInquiryRow>>> GetCdnInquiryAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>e-Invoice current status per ERP document, resolved against the latest submission row.</summary>
    Task<IvMasterOperationResult<SaInquiryPage<SaEInvoiceStatusRow>>> GetEInvoiceStatusAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>All submission attempts for one document key, chronological.</summary>
    Task<IvMasterOperationResult<IReadOnlyList<SaEInvoiceSubmissionRow>>> GetEInvoiceSubmissionHistoryAsync(
        string menuCode,
        string docType,
        string docNo,
        CancellationToken cancellationToken = default);

    /// <summary>e-Invoice reconciliation — each ERP document's <c>IRBM*</c> fields vs its latest submission row.</summary>
    Task<IvMasterOperationResult<SaInquiryPage<SaEInvoiceReconciliationRow>>> GetEInvoiceReconciliationAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default);
}
