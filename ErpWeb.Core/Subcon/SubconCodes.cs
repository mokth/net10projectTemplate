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
    public const string Posted = "POSTED";
    public const string Cancelled = "CANCELLED";

    public static bool CanEdit(string? status) =>
        string.Equals(status, New, StringComparison.OrdinalIgnoreCase);

    public static bool IsPosted(string? status) =>
        string.Equals(status, Posted, StringComparison.OrdinalIgnoreCase);
}

public static class SubconPostingDocKinds
{
    public const string Dispatch = "DISPATCH";
    public const string Receipt = "RECEIPT";
}

public static class SubconPostingRoles
{
    public const string Primary = "PRIMARY";
    public const string Reversal = "REVERSAL";
}

public static class SubconJobKinds
{
    public const string Processing = "PROCESSING";
    public const string Assembly = "ASSEMBLY";
    public const string ReturnMaterial = "RETURN_MATERIAL";

    public static bool IsKnown(string? kind) =>
        string.Equals(kind, Processing, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, Assembly, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, ReturnMaterial, StringComparison.OrdinalIgnoreCase);

    public static bool IsAssembly(string? kind) =>
        string.Equals(kind, Assembly, StringComparison.OrdinalIgnoreCase);
}

public static class SubconSourceKinds
{
    public const string Bom = "BOM";
    public const string ManualKit = "MANUAL_KIT";
    public const string InventoryItem = "INVENTORY_ITEM";
}

public static class SubconOutputKinds
{
    public const string FinishedGood = "FG";
    public const string Scrap = "SCRAP";
    public const string Byproduct = "BYPRODUCT";
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

    public const string LotReturnFrozen =
        "Cannot enable lot-controlled return. Lot-preserving transfer lineage is not approved yet.";

    public const string BomConversionFrozen =
        "Cannot enable BOM/assembly conversion. Multi-item atomic V2 conversion owner is not approved yet.";

    public const string FeeCapitalizationFrozen =
        "Cannot enable fee capitalization. Processing fee valuation and COGS settlement policy is not approved yet.";

    public const string HistoricalReplayBlocked =
        "Backdated subcontracting posting or rollback with later stock or valuation movements is not enabled yet.";

    public const string V2LedgerRequired =
        "Active V2 Stock Ledger epoch and valuation coordinator are required before subcontracting custody posting or rollback can proceed.";
}

public static class SubconMessages
{
    public const string NotAuthorized = "Not authorized.";
    public const string CompanyBranchRequired = "A company and branch scope is required.";
    public const string ChangedReload = "This document changed. Reload before continuing.";
    public const string OnlyNewEditable = "Only NEW drafts can be edited.";
    public const string OnlyNewPostable = "Only NEW documents can be posted.";
    public const string OnlyPostedRollback = "Only POSTED documents can be rolled back.";
    public const string DispatchMustBePosted = "Receive-back can only reference or post against a POSTED send-out.";
    public const string CustodyWarehouseRequired =
        "Configure an active subcontractor custody warehouse (WarehouseType = SUBCON) in Subcontracting Settings before posting.";
    public const string CustodyLocationRequired =
        "Create an active location matching the supplier code in the subcontractor custody warehouse before posting.";
    public const string SupplierCustodyMapRequired =
        "Map this supplier to an active subcontractor custody warehouse and location (up to 10 characters) in Subcontracting Settings before posting.";
    public const string CustodyBinCollision =
        "This subcontractor custody warehouse/location bin is already mapped to another supplier.";
    public const string SupplierCustodyRemapBlocked =
        "Cannot change or deactivate a supplier custody mapping while outstanding subcontracting stock remains in that custody bin.";
    public const string ReceiveDestinationCustodyForbidden =
        "Receive-back destination must be a company warehouse, not a subcontractor custody warehouse.";
    public const string UomConversionInvalid = "Unable to convert quantity to the item's base UOM.";
    public const string RequestIdConflict = "This request ID was already used for a different subcontract document.";
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
    public const string WarehouseInvalid = "Select an active warehouse in the current branch.";
    public const string LocationInvalid = "Select an active location in the chosen warehouse.";
    public const string UomInvalid = "Select an active UOM valid for this item.";
    public const string PoInvalid = "Referenced purchase order line was not found for this supplier.";
    public const string DispatchLineReferenced = "This send-out line is referenced by a receive-back draft and cannot be removed or altered.";
    public const string DuplicateLineNo = "Line numbers within the same document must be unique.";
    public const string TargetItemRequired = "Target output item and quantity are required for assembly jobs.";
    public const string JobNotFound = "Subcontract job was not found.";
}
