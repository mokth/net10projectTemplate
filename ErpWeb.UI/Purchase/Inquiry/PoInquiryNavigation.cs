namespace ErpWeb.UI.Purchase.Inquiry;

/// <summary>
/// Centralized inquiry → document view URLs. Never invents a route from an unknown type;
/// callers must treat <see cref="TryResolve"/> failure as disable OPEN / "Document unavailable".
/// Document-level ACCESS remains enforced by the target page's MenuAuthorize — inquiry OPEN
/// does not bypass authorization.
/// </summary>
public static class PoInquiryNavigation
{
    public const string DocumentUnavailableMessage = "Document unavailable";

    public static bool TryResolvePo(string? poNo, short? poRelNo, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(poNo))
        {
            return false;
        }

        var no = poNo.Trim();
        // Prefer revision-specific route when known; otherwise Mode/PoNo view opens the latest.
        url = poRelNo is > 0
            ? $"/purchase/orders/view/{no}/{poRelNo.Value}"
            : $"/purchase/orders/view/{no}";
        return true;
    }

    public static bool TryResolvePr(string? prNo, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(prNo))
        {
            return false;
        }

        url = $"/purchase/requisitions/view/{prNo.Trim()}";
        return true;
    }

    public static bool TryResolveInvoice(string? docNo, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(docNo))
        {
            return false;
        }

        url = $"/purchase/invoices/view/{docNo.Trim()}";
        return true;
    }

    public static bool TryResolveCdn(string? docNo, string? type, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(docNo))
        {
            return false;
        }

        var t = (type ?? string.Empty).Trim().ToUpperInvariant();
        if (t is "DN" or "DEBIT" or "DEBITNOTE" or "DEBIT_NOTE")
        {
            url = $"/purchase/debit-notes/view/{docNo.Trim()}";
            return true;
        }

        if (t is "CN" or "CREDIT" or "CREDITNOTE" or "CREDIT_NOTE" or "")
        {
            // Default blank type to credit-note path (CDN inquiry rows always carry Type).
            url = $"/purchase/credit-notes/view/{docNo.Trim()}";
            return true;
        }

        return false;
    }

    public static bool TryResolveSelfBilled(string? docType, string? docNo, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(docType) || string.IsNullOrWhiteSpace(docNo))
        {
            return false;
        }

        var t = docType.Trim().ToUpperInvariant();
        var no = docNo.Trim();

        // Match EInvoiceDocumentTypes / SB list routes.
        if (t is "SBI" or "SELF_BILLED_INVOICE" or "SELFBILLEDINVOICE")
        {
            url = $"/purchase/self-billed-invoices/view/{no}";
            return true;
        }

        if (t is "SBC" or "SELF_BILLED_CREDIT_NOTE" or "SELFBILLEDCREDITNOTE")
        {
            url = $"/purchase/self-billed-credit-notes/view/{no}";
            return true;
        }

        if (t is "SBD" or "SELF_BILLED_DEBIT_NOTE" or "SELFBILLEDDEBITNOTE")
        {
            url = $"/purchase/self-billed-debit-notes/view/{no}";
            return true;
        }

        return false;
    }

    public static bool TryResolveGr(int? batchNo, out string url)
    {
        url = string.Empty;
        if (batchNo is null or <= 0)
        {
            return false;
        }

        url = $"/inventory/goods-receipts/view/{batchNo.Value}";
        return true;
    }

    public static bool TryResolveGr(string? batchNoText, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(batchNoText)
            || !int.TryParse(batchNoText.Trim(), out var batchNo))
        {
            return false;
        }

        return TryResolveGr(batchNo, out url);
    }

    /// <summary>
    /// Resolves a supplier-transaction / relationship DocType + DocNo pair.
    /// Unknown types return false (never invent a route).
    /// </summary>
    public static bool TryResolveByDocType(string? docType, string? docNo, short? poRelNo, out string url)
    {
        url = string.Empty;
        var t = (docType ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(docNo) && t is not "PR" and not "PO")
        {
            return false;
        }

        return t switch
        {
            "PR" => TryResolvePr(docNo, out url),
            "PO" => TryResolvePo(docNo, poRelNo, out url),
            "INV" => TryResolveInvoice(docNo, out url),
            "QTYCN" => TryResolveInvoice(docNo, out url),
            "CN" => TryResolveCdn(docNo, "CN", out url),
            "DN" => TryResolveCdn(docNo, "DN", out url),
            _ => TryResolveSelfBilled(docType, docNo, out url)
        };
    }

    /// <summary>
    /// Document-relationship OPEN for SOURCE or TARGET.
    /// Relation values from Phase 1/2: PR→PO, PO→GR, PO→INV, INV→CDN, INV→QtyCN.
    /// PO→GR target resolves to Inventory GR view when TargetDocNo is a BatchNo.
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
            "PR→PO" or "PR->PO" => openSource
                ? TryResolvePr(docNo, out url)
                : TryResolvePo(docNo, poRelNo: null, out url),
            "PO→INV" or "PO->INV" => openSource
                ? TryResolvePo(docNo, poRelNo: null, out url)
                : TryResolveInvoice(docNo, out url),
            "INV→CDN" or "INV->CDN" => openSource
                ? TryResolveInvoice(docNo, out url)
                : TryResolveCdn(docNo, "CN", out url),
            "INV→QTYCN" or "INV->QTYCN" => openSource
                ? TryResolveInvoice(docNo, out url)
                : TryResolveInvoice(docNo, out url),
            "PO→GR" or "PO->GR" => openSource
                ? TryResolvePo(docNo, poRelNo: null, out url)
                : TryResolveGr(docNo, out url),
            _ => false
        };
    }
}
