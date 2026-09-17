namespace ErpWeb.Model.Entities;

public class Company
{
    public int CompanyId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string? LegalName { get; set; }
    public string? RegistrationNo { get; set; }
    public string? TaxNo { get; set; }

    public string? Phone { get; set; }
    public string? Fax { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }

    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? Address3 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostCode { get; set; }
    public string? Country { get; set; }

    public string? LogoUrl { get; set; }
    public string? CurrencyCode { get; set; }
    public string? TimeZoneId { get; set; }
    public byte? FiscalYearStartMonth { get; set; }

    /// <summary>
    /// Per-company sales pricing method — which price SOURCES are eligible when resolving a sales
    /// line price (<c>CUSTOMER_ITEM_AND_LIST</c> / <c>CUSTOMER_ITEM_ONLY</c> / <c>PRICE_LIST_ONLY</c> /
    /// <c>ITEM_DEFAULT_ONLY</c>).
    /// <para>
    /// NULL or blank means <c>CUSTOMER_ITEM_AND_LIST</c> (the full specificity chain), so a company
    /// created before this column existed keeps the shipped behaviour. The token list and the
    /// mode-to-source map live in <c>ErpWeb.Core.Sales.SaCompanyPriceMethod</c>; this entity only
    /// carries the value, so the model layer takes no dependency on Core.
    /// </para>
    /// </summary>
    public string? SalesPriceMethod { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    /// <summary>Whether LHDN e-Invoice submission is enabled for this company.</summary>
    public bool EInvEnabled { get; set; }

    /// <summary>MSIC code / industry classification code required by the LHDN supplier party.</summary>
    public string? EInvMsicCode { get; set; }

    /// <summary>Business description registered with LHDN (supplier party "biz desc").</summary>
    public string? EInvBizDescription { get; set; }

    /// <summary>SST registration number, when applicable.</summary>
    public string? EInvSstNo { get; set; }

    /// <summary>LHDN registration identity type: NRIC, PASSPORT, BRN or ARMY.</summary>
    public string? EInvRegType { get; set; }

    /// <summary>LHDN state code for the supplier address.</summary>
    public string? EInvStateCode { get; set; }

    /// <summary>LHDN country code for the supplier address.</summary>
    public string? EInvCountryCode { get; set; }

    /// <summary>Intermediary on-behalf TIN. Blank when the company submits directly.</summary>
    public string? EInvOnBehalfTin { get; set; }

    /// <summary>LHDN document version for this company (e.g. 1.0 / 1.1). Blank falls back to the app setting.</summary>
    public string? EInvDocumentVersion { get; set; }
}
