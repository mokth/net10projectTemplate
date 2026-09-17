namespace ErpWeb.Core.EInvoice;

/// <summary>
/// A document flattened out of its ERP entity into exactly the fields e-Invoicing needs. Building
/// this is the ERP-side of mapping; <see cref="EInvoiceDocumentMapper"/> then turns it into the
/// library's <c>DocumentHeader</c>. Keeping it a plain DTO makes the validator easy to unit test
/// without a database.
/// </summary>
public sealed class EInvoiceSourceDocument
{
    /// <summary><see cref="EInvoiceDocumentTypes"/>: INV, CN or DN.</summary>
    public string DocumentType { get; init; } = string.Empty;

    public string DocumentNo { get; init; } = string.Empty;

    /// <summary>Issue date of the ERP document. Used as the invoice issue date.</summary>
    public DateTime DocumentDate { get; init; }

    /// <summary>CN/DN only: the ERP invoice number being credited / debited.</summary>
    public string? RefDocumentNo { get; init; }

    /// <summary>CN/DN only: the MyInvois UUID of the source invoice.</summary>
    public string? OriginUuid { get; init; }

    public string? Currency { get; init; }

    public decimal CurrRate { get; init; } = 1m;

    // ── Header totals (from the ERP document, already rounded by the ERP calculator) ──

    /// <summary>Amount excluding tax (ERP <c>GrossAmnt</c>).</summary>
    public decimal AmountExclTax { get; init; }

    /// <summary>Total tax (ERP <c>Taxes</c>).</summary>
    public decimal TaxAmount { get; init; }

    /// <summary>Amount including tax (ERP <c>TotAmnt</c>).</summary>
    public decimal AmountIncTax { get; init; }

    // ── Buyer / customer snapshot (frozen on the document) ──

    public string? CustomerName { get; init; }
    public string? CustomerTin { get; init; }
    public string? CustomerRegNo { get; init; }
    public string? CustomerRegType { get; init; }
    public string? CustomerSstNo { get; init; }
    public string? CustomerAddr1 { get; init; }
    public string? CustomerAddr2 { get; init; }
    public string? CustomerAddr3 { get; init; }
    public string? CustomerAddr4 { get; init; }
    public string? CustomerCity { get; init; }
    public string? CustomerState { get; init; }
    public string? CustomerPostalCode { get; init; }
    public string? CustomerCountry { get; init; }
    public string? CustomerPhone { get; init; }
    public string? CustomerEmail { get; init; }

    public IReadOnlyList<EInvoiceSourceLine> Lines { get; init; } = [];
}

/// <summary>One invoice line flattened for e-Invoicing.</summary>
public sealed class EInvoiceSourceLine
{
    public int Line { get; init; }
    public string? ItemCode { get; init; }
    public string? ItemDesc { get; init; }

    /// <summary>UN/ECE unit-of-measure code (the ERP stores the LHDN code in <c>StdUom</c>).</summary>
    public string? Uom { get; init; }

    public decimal Qty { get; init; }
    public decimal UnitPrice { get; init; }

    /// <summary>Quantity x unit price, before line discount (ERP <c>Amount</c>).</summary>
    public decimal GrossAmount { get; init; }

    /// <summary>Net of discount, excluding tax (ERP <c>NetAmount</c>).</summary>
    public decimal AmountExclTax { get; init; }

    public decimal TaxAmount { get; init; }

    /// <summary>LHDN tax type code (= the ERP tax group code).</summary>
    public string? TaxType { get; init; }

    public double? TaxPercent { get; init; }

    /// <summary>LHDN item classification code (ERP <c>SaInvoiceDetail.Classification</c>).</summary>
    public string? ClassificationCode { get; init; }

    /// <summary>Line discount; derived as <see cref="GrossAmount"/> - <see cref="AmountExclTax"/>.</summary>
    public decimal DiscountAmount => GrossAmount - AmountExclTax;
}

/// <summary>
/// The supplier (issuer) party, resolved from the company's e-Invoice profile. Secrets are never
/// part of this object.
/// </summary>
public sealed class EInvoiceSupplierProfile
{
    public string CompanyCode { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string? CompanyName { get; init; }
    public string? TinNo { get; init; }
    public string? RegistrationNo { get; init; }
    public string? RegType { get; init; }
    public string? SstNo { get; init; }
    public string? MsicCode { get; init; }
    public string? BusinessDescription { get; init; }
    public string? Addr1 { get; init; }
    public string? Addr2 { get; init; }
    public string? Addr3 { get; init; }
    public string? Addr4 { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? PostalCode { get; init; }
    public string? Country { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }

    /// <summary>Per-company document version override; blank uses the application setting.</summary>
    public string? DocumentVersion { get; init; }

    /// <summary>Per-company intermediary on-behalf TIN; blank means "submit directly".</summary>
    public string? OnBehalfTin { get; init; }
}

/// <summary>Field-keyed ERP validation issues. Distinct from anything MyInvois returns.</summary>
public sealed class EInvoiceValidationReport
{
    public static EInvoiceValidationReport Valid { get; } = new();

    public IReadOnlyDictionary<string, string> Errors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool IsValid => Errors.Count == 0;

    public static EInvoiceValidationReport From(IReadOnlyDictionary<string, string> errors) =>
        new() { Errors = errors };

    /// <summary>Flattened single-line summary, safe to store in the short <c>IRBMError</c> column.</summary>
    public string Summary(int maxLength = 500)
    {
        var text = string.Join("; ", Errors.Select(x => $"{x.Key}: {x.Value}"));
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
