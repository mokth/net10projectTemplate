using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.StockLedger;

internal static class StockReplayFifoInventoryIssueKinds
{
    internal static bool IsSupported(string? type) =>
        type is IvTrxTypes.MiscellaneousIssue or IvTrxTypes.Scrap;

    internal static bool IsSupportedWac(string? type) =>
        IsSupported(type) || type == IvTrxTypes.VendorReturn;

    internal static string MovementCode(string type) => type switch
    {
        IvTrxTypes.MiscellaneousIssue => "ISSUE_OUT",
        IvTrxTypes.Scrap => "SCRAP_OUT",
        IvTrxTypes.VendorReturn => "VENDOR_RETURN_OUT",
        _ => throw new StockLedgerException(new(
            StockLedgerErrorCodes.LedgerMismatch,
            "Unsupported FIFO Inventory issue type."))
    };
}
