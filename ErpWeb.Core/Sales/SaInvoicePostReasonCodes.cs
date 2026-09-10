namespace ErpWeb.Core.Sales;

public static class SaInvoicePostReasonCodes
{
    public const string Concurrency = "POST_CONCURRENCY";
    public const string SalesmanInvalid = "POST_SALESMAN_INVALID";
    public const string DueDateMissing = "POST_DUE_DATE_MISSING";
    public const string ArGlMissing = "POST_AR_GL_MISSING";
    public const string TaxGlMissing = "POST_TAX_GL_MISSING";
    public const string BuyerAddress = "POST_BUYER_ADDRESS";
    public const string BuyerContact = "POST_BUYER_CONTACT";
    public const string BuyerId = "POST_BUYER_ID";
    public const string LineSalesGlMissing = "POST_LINE_SALES_GL_MISSING";
    public const string LineClassification = "POST_LINE_CLASSIFICATION";
}
