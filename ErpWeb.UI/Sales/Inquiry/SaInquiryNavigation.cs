using ErpWeb.Core.Sales;

namespace ErpWeb.UI.Sales.Inquiry;

/// <summary>
/// Centralised Sales inquiry → document view URLs (the mirror of
/// <see cref="Purchase.Inquiry.PoInquiryNavigation"/>). Never invents a route from an unknown type:
/// callers must treat a <c>false</c> result as "OPEN disabled / Document unavailable".
///
/// <para>
/// Opening a document from an inquiry does <b>not</b> bypass authorisation — the target page's own
/// <c>MenuAuthorize</c> still applies, and it is reached with a <c>returnUrl</c> so its Close button
/// comes back to this inquiry instead of the transaction list (see
/// <see cref="ErpWeb.UI.Services.DocumentReturnNavigation"/>).
/// </para>
/// </summary>
public static class SaInquiryNavigation
{
    public const string DocumentUnavailableMessage = "Document unavailable";

    /// <summary>Quotation view. A known revision opens that revision; otherwise the latest is opened.</summary>
    public static bool TryResolveQt(string? qtNo, short? custRel, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(qtNo))
        {
            return false;
        }

        var no = qtNo.Trim();
        url = custRel is > 0
            ? $"/sales/quotations/view/{Uri.EscapeDataString(no)}/{custRel.Value}"
            : $"/sales/quotations/view/{Uri.EscapeDataString(no)}";
        return true;
    }

    /// <summary>Sales order view. A known revision opens that revision; otherwise the latest is opened.</summary>
    public static bool TryResolveSo(string? soNo, short? custRel, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(soNo))
        {
            return false;
        }

        var no = soNo.Trim();
        url = custRel is > 0
            ? $"/sales/sales-orders/view/{Uri.EscapeDataString(no)}/{custRel.Value}"
            : $"/sales/sales-orders/view/{Uri.EscapeDataString(no)}";
        return true;
    }

    public static bool TryResolveDo(string? doNo, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(doNo))
        {
            return false;
        }

        url = $"/sales/delivery-orders/view/{Uri.EscapeDataString(doNo.Trim())}";
        return true;
    }

    public static bool TryResolveInvoice(string? invNo, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(invNo))
        {
            return false;
        }

        url = $"/sales/invoices/view/{Uri.EscapeDataString(invNo.Trim())}";
        return true;
    }

    /// <summary>
    /// Credit / debit note view. CN and DN are two menus over one page, so the type decides the route;
    /// a blank type defaults to the credit-note path.
    /// </summary>
    public static bool TryResolveCdn(string? docNo, string? type, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(docNo))
        {
            return false;
        }

        var no = Uri.EscapeDataString(docNo.Trim());
        var t = (type ?? string.Empty).Trim().ToUpperInvariant();

        if (t is "DN" or "DEBIT" or "DEBITNOTE" or "DEBIT_NOTE")
        {
            url = $"/sales/debit-notes/view/{no}";
            return true;
        }

        if (t is "CN" or "CREDIT" or "CREDITNOTE" or "CREDIT_NOTE" or "")
        {
            url = $"/sales/credit-notes/view/{no}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the document-type + document-number pair a Sales inquiry row carries
    /// (<c>QT</c>/<c>SO</c>/<c>DO</c>/<c>INV</c>/<c>CN</c>/<c>DN</c>). An unknown type returns false —
    /// a route is never guessed.
    /// </summary>
    public static bool TryResolveByDocType(string? docType, string? docNo, short? custRel, out string url)
    {
        url = string.Empty;
        var t = (docType ?? string.Empty).Trim().ToUpperInvariant();

        return t switch
        {
            "QT" => TryResolveQt(docNo, custRel, out url),
            "SO" => TryResolveSo(docNo, custRel, out url),
            "DO" => TryResolveDo(docNo, out url),
            "INV" => TryResolveInvoice(docNo, out url),
            SaCdnTypes.CreditNote => TryResolveCdn(docNo, SaCdnTypes.CreditNote, out url),
            SaCdnTypes.DebitNote => TryResolveCdn(docNo, SaCdnTypes.DebitNote, out url),
            _ => false
        };
    }

    /// <summary>
    /// Document-relationship OPEN for SOURCE or TARGET. The relation vocabulary is
    /// <c>SO→DO</c>, <c>DO→INV</c>, <c>INV→DO</c>, <c>INV→SO</c> (Phase 1). An unknown relation returns
    /// false rather than guessing a route.
    /// </summary>
    public static bool TryResolveRelationship(
        string? relation,
        string? sourceDocNo,
        string? targetDocNo,
        bool openSource,
        out string url)
    {
        url = string.Empty;
        var rel = (relation ?? string.Empty).Trim().ToUpperInvariant();
        var docNo = openSource ? sourceDocNo : targetDocNo;
        if (string.IsNullOrWhiteSpace(docNo))
        {
            return false;
        }

        return rel switch
        {
            "SO→DO" or "SO->DO" => openSource
                ? TryResolveSo(docNo, custRel: null, out url)
                : TryResolveDo(docNo, out url),
            "DO→INV" or "DO->INV" => openSource
                ? TryResolveDo(docNo, out url)
                : TryResolveInvoice(docNo, out url),
            "INV→DO" or "INV->DO" => openSource
                ? TryResolveInvoice(docNo, out url)
                : TryResolveDo(docNo, out url),
            "INV→SO" or "INV->SO" => openSource
                ? TryResolveInvoice(docNo, out url)
                : TryResolveSo(docNo, custRel: null, out url),
            _ => false
        };
    }
}
