namespace ErpWeb.Model.Entities.Purchase;

/// <summary>
/// Self-billed purchase credit / debit note (LHDN e-Invoice document types <b>12</b> / <b>13</b>).
/// <para>
/// As with <see cref="PoSbInvoice"/> the parties are reversed: the VENDOR is the payload's <c>Supplier</c>
/// and our COMPANY (the issuer) is the <c>Customer</c>.
/// </para>
/// <para>
/// A note must reference an originating self-billed invoice, and that origin must already be
/// <c>VALID</c> at MyInvois with a UUID — the library refuses to generate a credit/debit note without an
/// <c>OriginInvoiceUUID</c>. The origin is resolved within the note's own company and branch.
/// </para>
/// </summary>
public class PoSbCdn
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }

    /// <summary><c>NEW</c> or <c>POSTED</c>. Only a POSTED document may be submitted to MyInvois.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary><c>CN</c> (12) or <c>DN</c> (13). Both store positive amounts.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Numbering prefix snapshot (display only).</summary>
    public string? Prefix { get; set; }

    public string VendorCode { get; set; } = string.Empty;
    public string? VendorName { get; set; }

    /// <summary>
    /// The originating <see cref="PoSbInvoice.DocNo"/>. A soft reference resolved within the note's own
    /// <c>CompanyCode</c> + <c>BranchCode</c> — never a bare number lookup.
    /// </summary>
    public string? OriginSbInvNo { get; set; }

    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;

    public string? TaxGrCode { get; set; }
    public string? Remarks { get; set; }

    /// <summary>Amount excluding tax — always the sum of the line net amounts.</summary>
    public decimal GrossAmnt { get; set; }

    /// <summary>Total tax — always the sum of the line tax amounts.</summary>
    public decimal Taxes { get; set; }

    /// <summary>Total payable = <see cref="GrossAmnt"/> + <see cref="Taxes"/>. Derived, never assigned on its own.</summary>
    public decimal TotAmnt { get; set; }

    public string? LocationCode { get; set; }

    // ── LHDN e-Invoice state. Column names/spellings mirror SaInvoice/SaCdn.
    public string? IrbmSubmitId { get; set; }
    public string? IrbmUuid { get; set; }

    /// <summary>The origin invoice's MyInvois UUID, written back at submit.</summary>
    public string? IrbmOriUuid { get; set; }

    public DateTime? IrbmSentOn { get; set; }
    public DateTime? IrbmValidOn { get; set; }
    public string? IrbmError { get; set; }
    public string? IrbmStatus { get; set; }

    /// <summary>Discriminates a FAILED status into ConfirmedFailure / Unknown.</summary>
    public string? IrbmOutcome { get; set; }

    /// <summary>When the LHDN cancellation succeeded (column <c>IRNMCancelOn</c>).</summary>
    public DateTime? IrnmCancelOn { get; set; }

    public DateTime? PostedDate { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? RollbackDate { get; set; }
    public string? RollbackBy { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PoSbCdnDetail> Details { get; set; } = new List<PoSbCdnDetail>();
}
