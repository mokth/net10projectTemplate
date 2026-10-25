using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger;

internal static class StockReplayInventoryMiscPostingOwner
{
    public static bool Matches(StockPosting owner, string trxType)
    {
        if (owner.SourceModule != "INVENTORY"
            || trxType is not (IvTrxTypes.MiscellaneousReceipt
                or IvTrxTypes.MiscellaneousIssue))
            return false;

        // Current inventory posting uses the transaction type as its document
        // type. Earlier V2 postings used the IV_TRX identity.
        return (owner.SourceDocumentType == trxType
                && owner.CommandType == $"IV_POST_{trxType}")
            || (owner.SourceDocumentType == "IV_TRX"
                && owner.CommandType == (trxType == IvTrxTypes.MiscellaneousReceipt
                    ? "IV_MR_POST" : "IV_MI_POST"));
    }
}
