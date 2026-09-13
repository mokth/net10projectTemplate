using ErpWeb.Core.Sales;

namespace ErpWeb.Core.Purchase;

public static class PoSupplierPaymentOptions
{
    public const string StatementOpenItem = "OPEN_ITEM";
    public const string StatementBalanceForward = "BALANCE_FORWARD";
    public const string StatementNone = "NO_STATEMENT";

    public const string AgingInvoice = SaCustPaymentOptions.AgingInvoice;
    public const string AgingDue = SaCustPaymentOptions.AgingDue;
}
