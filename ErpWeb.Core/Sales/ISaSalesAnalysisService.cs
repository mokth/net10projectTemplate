using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Sales-analysis Phase 1: read-only aggregate inquiries over posted invoices, company-wide sales-rep
/// targets and quotation conversion.
/// <para>
/// Deliberately separate from the document list services: those exist to page documents for an
/// operator, these return aggregate DTOs for a decision-maker. Nothing here writes to the transactional
/// sales tables. CSV export must call these same methods so an exported file can never disagree with
/// the grid on screen (R9).
/// </para>
/// </summary>
public interface ISaSalesAnalysisService
{
    /// <summary>
    /// Period sales grouped by one dimension, plus the period KPI chips. Facts are POSTED invoices;
    /// the Source dimension joins the live customer master and is labelled current attribution (R5).
    /// </summary>
    Task<IvMasterOperationResult<SaSalesSummaryResult>> GetSalesSummaryAsync(
        SaSalesAnalysisQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Company-wide target vs actual per sales rep. Targets are monthly and never prorated; a rep with
    /// sales but no target still appears (target 0, attainment N/A), and so does a rep with a target and
    /// no sales (actual 0).
    /// </summary>
    Task<IvMasterOperationResult<IReadOnlyList<SaSalesRepAttainmentRow>>> GetSalesRepAttainmentAsync(
        SaSalesAnalysisQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Quotation conversion buckets, win rate and lost-reason rollup for CURRENT revisions only.
    /// </summary>
    Task<IvMasterOperationResult<SaQtConversionResult>> GetQtConversionAsync(
        SaSalesAnalysisQuery query,
        CancellationToken cancellationToken = default);
}
