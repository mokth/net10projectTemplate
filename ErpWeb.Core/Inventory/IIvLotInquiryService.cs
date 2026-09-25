using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Inventory;

/// <summary>
/// The Lot / Batch inquiry — a lot passport with its on-hand piles and its posted movements.
///
/// <para>
/// <b>Terminology.</b> "Lot" is the inventory traceability lot (<c>IvLot</c>). A "batch" in this ERP is
/// <c>IvTrxBatch</c> — a <em>document</em> — and is reachable from the Transaction Inquiry page, not
/// here. The screen is titled "Lot / Batch Inquiry" so the word is findable; the code artefact stays
/// <c>IvLot*</c>.
/// </para>
///
/// <para>
/// The movements panel delegates to <see cref="IIvTrxHistoryService"/> rather than querying
/// <c>IvTrxHistory</c> again, so the lot ledger and the transaction inquiry can never define "in" and
/// "out" differently.
/// </para>
///
/// <para>
/// There is no money column on this screen, so ACCESS is the only permission gate.
/// </para>
/// </summary>
public interface IIvLotInquiryService
{
    /// <summary>Paged lot passports. ACCESS-gated on the lot menu.</summary>
    Task<IvMasterOperationResult<IvLotInquiryPage>> SearchAsync(
        string menuCode,
        IvLotInquiryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Aggregate over the SAME predicate as <see cref="SearchAsync"/>.</summary>
    Task<IvMasterOperationResult<IvLotInquirySummary>> GetSummaryAsync(
        string menuCode,
        IvLotInquiryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One lot's child panels: the piles holding it and its movement ledger. The lot row comes from the
    /// grid the user selected, so the page needs no second lot lookup — but the tenant and the ACCESS
    /// check are re-run here, because a client-supplied row is not evidence.
    /// </summary>
    Task<IvMasterOperationResult<IvLotInquiryDetail>> GetDetailAsync(
        string menuCode,
        IvLotInquiryRow lot,
        CancellationToken cancellationToken = default);
}
