namespace ErpWeb.Model.Entities.Purchase;

/// <summary>
/// Purchase Credit / Debit Note (PoCdn). Financial supplier adjustment — <b>not</b> the
/// quantity-only PO/GR correction, which stays on <c>PoInvoice.Type = CN</c>.
/// </summary>
/// <remarks>
/// Phase 1 is operational only: <c>Status = POSTED</c> means finalized and any owned
/// Vendor Return (VR) batch has been posted. It does <b>not</b> imply AP/GL posting, and
/// no accounting entries exist yet. <c>AccountingStatus</c> arrives with the AP/GL phase.
/// </remarks>
public class PoCdn
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }

    /// <summary><c>NEW</c> or <c>POSTED</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary><c>CN</c> (credit note) or <c>DN</c> (debit note). Both store positive amounts.</summary>
    public string Type { get; set; } = string.Empty;
    public string? Prefix { get; set; }

    public string VendorCode { get; set; } = string.Empty;
    public string? VendorName { get; set; }
    public string? InvAddress1 { get; set; }
    public string? InvAddress2 { get; set; }
    public string? InvAddress3 { get; set; }
    public string? InvAddress4 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Tel { get; set; }
    public string? Fax { get; set; }
    public string? PayCode { get; set; }

    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;
    public string? TaxGrCode { get; set; }
    public string? Remarks { get; set; }

    public decimal GrossAmnt { get; set; }
    public decimal Taxes { get; set; }
    public decimal TotAmnt { get; set; }

    public string? LocationCode { get; set; }
    public string? ProjId { get; set; }
    public string? BuyerCode { get; set; }
    public string? Dept { get; set; }
    public bool? ExportStatus { get; set; }

    /// <summary>Internal general reference. Never the supplier's own document number.</summary>
    public string? RefNo { get; set; }

    /// <summary>External-system / integration reference. Never the supplier's own document number.</summary>
    public string? ExternalDocNo { get; set; }

    /// <summary>
    /// The supplier's own CN/DN number — the only place it is stored. Blank is normalised to
    /// <c>null</c> and is only permitted for an internal-adjustment reason code.
    /// </summary>
    public string? SupplierDocNo { get; set; }

    /// <summary>Supplier's document date. Required whenever <see cref="SupplierDocNo"/> is present.</summary>
    public DateTime? SupplierDocDate { get; set; }

    /// <summary>Business classification (e.g. <c>RETURN</c>, <c>PRICE_ADJUSTMENT</c>). Not a GL account key.</summary>
    public string? ReasonCode { get; set; }

    /// <summary>Referenced posted <c>PoInvoice</c> (<c>Type = INV</c>). Required for a CN.</summary>
    public string? InvNo { get; set; }

    /// <summary>Header gate for physical return. When false, no line may set <c>IsStockReturn</c>.</summary>
    public bool ReturnStock { get; set; }

    /// <summary>The single Vendor Return batch owned by this document (Phase 1 one-to-one).</summary>
    public int? VrBatchNo { get; set; }

    // Reserved for the MyInvois phase; nullable and unused in Phase 1.
    public string? IrbmSubmitId { get; set; }
    public string? IrbmUuid { get; set; }
    public string? IrbmOriUuid { get; set; }
    public DateTime? IrbmSentOn { get; set; }
    public DateTime? IrbmValidOn { get; set; }
    public string? IrbmError { get; set; }
    public string? IrbmStatus { get; set; }
    public bool? SelfBilled { get; set; }

    public DateTime? PostedDate { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? RollbackDate { get; set; }
    public string? RollbackBy { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PoCdnDetail> Details { get; set; } = new List<PoCdnDetail>();
}
