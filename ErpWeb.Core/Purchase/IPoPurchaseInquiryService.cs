using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Purchase;

/// <summary>
/// Read-only Purchase Inquiry Phase 1. Every method takes <c>menuCode</c> first (ACCESS per screen).
/// Tenant scope resolves before ACCESS. CSV export calls these same methods (R9 parity).
/// </summary>
public interface IPoPurchaseInquiryService
{
    Task<IvMasterOperationResult<PoInquiryPage<PoOrderOutstandingRow>>> GetPoOutstandingAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoOrderOutstandingSummary>> GetPoOutstandingSummaryAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoInquiryPage<PoPrStatusRow>>> GetPrStatusAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoPrStatusSummary>> GetPrStatusSummaryAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoInquiryPage<PoSupplierTransactionRow>>> GetSupplierTransactionsAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IReadOnlyList<PoSupplierPurchaseHistoryRow>>> GetSupplierPurchaseHistoryAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoSupplierPurchaseHistoryTotals>> GetSupplierPurchaseHistoryTotalsAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoInquiryPage<PoInvoiceInquiryRow>>> GetInvoiceInquiryAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoInquiryPage<PoCdnInquiryRow>>> GetCdnInquiryAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoInquiryPage<PoDocumentRelationshipRow>>> GetDocumentRelationshipAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoInquiryPage<PoSbEInvoiceStatusRow>>> GetSbEInvoiceStatusAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoSbEInvoiceSummary>> GetSbEInvoiceSummaryAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IReadOnlyList<PoSbEInvoiceSubmissionRow>>> GetSbEInvoiceSubmissionHistoryAsync(
        string menuCode, string docType, string docNo, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoInquiryPage<PoSbEInvoiceReconciliationRow>>> GetSbEInvoiceReconciliationAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    // Phase 2
    Task<IvMasterOperationResult<PoInquiryPage<PoPurchasePriceHistoryRow>>> GetPurchasePriceHistoryAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoPurchasePriceHistorySummary>> GetPurchasePriceHistorySummaryAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoInquiryPage<PoMatchingRow>>> GetPoMatchingAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoMatchingSummary>> GetPoMatchingSummaryAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PoInquiryPage<PoDeliveryPerformanceRow>>> GetSupplierDeliveryPerformanceAsync(
        string menuCode, PoInquiryQuery query, CancellationToken cancellationToken = default);
}
