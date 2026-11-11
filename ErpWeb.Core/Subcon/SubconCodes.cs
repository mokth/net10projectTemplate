namespace ErpWeb.Core.Subcon;

public static class SubconModes
{
    public const string Inventory = "INVENTORY";
    public const string Production = "PRODUCTION";

    public static bool IsKnown(string? value) =>
        string.Equals(value, Inventory, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Production, StringComparison.OrdinalIgnoreCase);

    public static bool IsInventory(string? value) =>
        string.Equals(value, Inventory, StringComparison.OrdinalIgnoreCase);
}

public static class SubconStatuses
{
    public const string New = "NEW";
    public const string Cancelled = "CANCELLED";

    public static bool CanEdit(string? status) =>
        string.Equals(status, New, StringComparison.OrdinalIgnoreCase);
}

public static class SubconBlockedReasons
{
    public const string NoAtomicInventorySeam =
        "Cannot post: stock transfer cannot be saved together with this subcontract document yet.";

    public const string NoProductionCustody =
        "Work Order send-out is not available. Partial WIP cannot be held at a supplier yet.";

    public const string LotControlled =
        "This item is lot-controlled. Send-out is not available until return lots are designed.";

    public const string InventoryModeFrozen =
        "Cannot enable inventory send-out. Supplier stock is not isolated from sales, issues, or stock count yet.";

    public const string ProductionModeFrozen =
        "Cannot enable Work Order send-out. Offsite WIP is not excluded from Daily Production and finished goods yet.";

    public const string LiveEnablementFrozen =
        "Cannot enable live subcontracting. Dispatch, return, rollback, and supplier-stock isolation are not proven.";

    public const string PostingNotEnabled =
        "Posting is not enabled. You can save drafts. Stock and WIP will not move.";

    public const string NoOutstanding =
        "No outstanding items. Receipts post only after a send-out is posted, and posting is not enabled yet.";

    public const string ConversionRequired =
        "Conversion required. This version cannot change the item code.";
}

public static class SubconMessages
{
    public const string NotAuthorized = "Not authorized.";
    public const string CompanyBranchRequired = "A company and branch scope is required.";
    public const string ChangedReload = "This document changed. Reload before continuing.";
    public const string OnlyNewEditable = "Only NEW drafts can be edited.";
    public const string SupplierRequired = "Select a supplier.";
    public const string LineRequired = "Add at least one line.";
    public const string QtyRequired = "Enter a quantity greater than zero.";
    public const string ItemRequired = "Item is required.";
    public const string UomRequired = "UOM is required.";
    public const string WarehouseRequired = "Warehouse is required for inventory send-out.";
    public const string StockControlRequired = "Only stock-controlled items can be sent out.";
    public const string ProductionLineOnInventory =
        "This send-out is inventory stock. Remove Work Order lines.";
    public const string MixedMode = "A document cannot mix inventory stock and Work Order lines.";
    public const string DispatchNotFound = "Send-out was not found.";
    public const string ReceiptNotFound = "Receive-back was not found.";
    public const string OverReturn = "You can receive only the remaining quantity on this dispatch line.";
    public const string UnknownMode = "Select Inventory stock or Work Order WIP.";
}
