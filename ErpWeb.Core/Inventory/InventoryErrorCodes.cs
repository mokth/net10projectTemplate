namespace ErpWeb.Core.Inventory;

public static class InventoryErrorCodes
{
    public const string PeriodClosed = "PeriodClosed";
    public const string BackdatedPostingNotAllowed = "BackdatedPostingNotAllowed";
    public const string DocumentAlreadyPosted = "DocumentAlreadyPosted";
    public const string InsufficientStock = "InsufficientStock";
    public const string InvalidWarehouse = "InvalidWarehouse";
    public const string InvalidBranch = "InvalidBranch";
    public const string InvalidCompany = "InvalidCompany";
    public const string InvalidUOM = "InvalidUOM";
    public const string InvalidConversion = "InvalidConversion";
    public const string DuplicatePosting = "DuplicatePosting";
    public const string DocumentAlreadyReversed = "DocumentAlreadyReversed";
    public const string CrossBranchTransferNotAllowed = "CrossBranchTransferNotAllowed";
    public const string LotNotAllowedInPhase = "LotNotAllowedInPhase";
    public const string ZeroQtyNotAllowed = "ZeroQtyNotAllowed";
    public const string ZeroCostNotAllowed = "ZeroCostNotAllowed";
    public const string ViewCostDenied = "ViewCostDenied";
    public const string StockTakeNotEditable = "StockTakeNotEditable";
    public const string DocumentNotEditableWhenPosted = "DocumentNotEditableWhenPosted";
    public const string DeadlockRetryExhausted = "DeadlockRetryExhausted";
    public const string InvalidStatus = "InvalidStatus";
    public const string InvalidDocument = "InvalidDocument";
    public const string ReasonCodeRequired = "ReasonCodeRequired";
}
