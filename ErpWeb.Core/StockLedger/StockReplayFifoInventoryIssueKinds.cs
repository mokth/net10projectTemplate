using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.StockLedger;

internal static class StockReplayFifoInventoryIssueKinds
{
    internal static bool IsSupported(string? type) =>
        type is IvTrxTypes.MiscellaneousIssue or IvTrxTypes.Scrap;

    internal static bool IsSupportedWac(string? type) =>
        IsSupported(type) || type == IvTrxTypes.VendorReturn;

    internal static bool IsSupportedStandard(string? type) =>
        IsSupportedWac(type);

    // A Vendor Return may be the replay root; later FIFO descendants remain
    // limited to Inventory MI/Scrap until PO-dependent corrections are modeled.
    internal static bool IsSupportedFifoRoot(string? type) =>
        IsSupportedWac(type);

    internal static string MovementCode(string type) => type switch
    {
        IvTrxTypes.MiscellaneousIssue => "ISSUE_OUT",
        IvTrxTypes.Scrap => "SCRAP_OUT",
        IvTrxTypes.VendorReturn => "VENDOR_RETURN_OUT",
        IvTrxTypes.SalesOut => "SALE_OUT",
        _ => throw new StockLedgerException(new(
            StockLedgerErrorCodes.LedgerMismatch,
            "Unsupported FIFO Inventory issue type."))
    };
}
