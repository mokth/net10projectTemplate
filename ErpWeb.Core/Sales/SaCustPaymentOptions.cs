namespace ErpWeb.Core.Sales;

public static class SaCustPaymentOptions
{
    public const string DiscountJoin = "JOIN";
    public const string DiscountSplit = "SPLIT";

    public const string PriceSelling = "FOLLOW SELLING PRICE X DISCOUNT";
    public const string PriceDealer = "FOLLOW DEFAULT DEALER PRICE";

    public const string AgingInvoice = "INVOICE DATE";
    public const string AgingDue = "DUE DATE";

    public const string CreditLimit = "CREDIT LIMIT";
}
