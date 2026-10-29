using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// A root-bound capability issued only after the Production material pool has
/// been planned and rechecked under the branch stock lock.
/// </summary>
public sealed class StockReplayFifoProductionPermit
{
    private readonly long _rootPostingId;
    private readonly long _rootSequence;
    private readonly long _epochId;
    private readonly string _companyCode;
    private readonly string _branchCode;
    private readonly string _itemCode;
    private readonly string _planHash;
    private readonly int _historyId;

    internal long MaterialMovementId { get; }

    internal StockReplayFifoProductionPermit(StockPostingContext context,
        string itemCode, string planHash, int historyId, long materialMovementId)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();
        if (context.CostMethod != StockCostMethods.Fifo || context.Posting.Id <= 0
            || context.Posting.PostingSequence <= 0 || context.Epoch.Id <= 0 || historyId <= 0
            || materialMovementId <= 0 || string.IsNullOrWhiteSpace(itemCode)
            || string.IsNullOrWhiteSpace(planHash)
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType is not
                (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive))
            throw Invalid();
        _rootPostingId = context.Posting.Id;
        _rootSequence = context.Posting.PostingSequence;
        _epochId = context.Epoch.Id;
        _companyCode = context.CompanyCode;
        _branchCode = context.BranchCode;
        _itemCode = itemCode;
        _planHash = planHash;
        _historyId = historyId;
        MaterialMovementId = materialMovementId;
    }

    internal bool Authorizes(StockPostingContext context, string itemCode,
        string planHash, int historyId)
    {
        if (context.Posting.Id != _rootPostingId || context.Posting.PostingSequence != _rootSequence
            || context.Epoch.Id != _epochId || context.CompanyCode != _companyCode
            || context.BranchCode != _branchCode || context.CostMethod != StockCostMethods.Fifo
            || itemCode != _itemCode || planHash != _planHash)
            throw Invalid();
        return historyId == _historyId;
    }

    private static StockLedgerException Invalid() =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch,
            "FIFO Production repricing needs its validated receipt replay permit."));
}
