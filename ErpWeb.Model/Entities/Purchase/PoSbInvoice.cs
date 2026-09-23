namespace ErpWeb.Model.Entities.Purchase;

/// <summary>
/// Self-billed purchase invoice (LHDN e-Invoice document type <b>11</b>).
/// <para>
/// A self-billed document is issued by the BUYER, so the parties are reversed: in the MyInvois payload
/// the VENDOR is the <c>Supplier</c> and our COMPANY (the issuer) is the <c>Customer</c>.
/// </para>
/// </summary>
/// <remarks>
/// Deliberately slim: it carries only what an e-Invoice submission needs. There is no PO/GR lineage, no
/// stock or accounting effect, and no vendor or company address snapshot — both party blocks are read
/// live when the payload is built, and the company's identity/credentials/submission identity always
/// stay the company's. See <c>plans/plan-selfBilledPartyReversal.prompt.md</c>.
/// </remarks>
public class PoSbInvoice
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }

    /// <summary><c>NEW</c> or <c>POSTED</c>. Only a POSTED document may be submitted to MyInvois.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Numbering prefix snapshot (display only).</summary>
    public string? Prefix { get; set; }

    public string VendorCode { get; set; } = string.Empty;
    public string? VendorName { get; set; }

    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;

    /// <summary>Default tax group for new lines; the LHDN tax type is resolved per line.</summary>
    public string? TaxGrCode { get; set; }

    public string? Remarks { get; set; }

    /// <summary>Amount excluding tax — always the sum of the line net amounts.</summary>
    public decimal GrossAmnt { get; set; }

    /// <summary>Total tax — always the sum of the line tax amounts.</summary>
    public decimal Taxes { get; set; }

    /// <summary>Total payable = <see cref="GrossAmnt"/> + <see cref="Taxes"/>. Derived, never assigned on its own.</summary>
    public decimal TotAmnt { get; set; }

    public string? LocationCode { get; set; }

    // ── LHDN e-Invoice state. Column names/spellings mirror SaInvoice/SaCdn so the façade's
    //    ApplyState/LoadStateAsync cases read and write the same columns as every other family.
    public string? IrbmSubmitId { get; set; }
    public string? IrbmUuid { get; set; }
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

    public ICollection<PoSbInvoiceDetail> Details { get; set; } = new List<PoSbInvoiceDetail>();
}
