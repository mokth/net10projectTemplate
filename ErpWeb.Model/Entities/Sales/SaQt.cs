namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Sales Quotation header. Mirrors the <see cref="SaSo"/> revision identity model
/// (<see cref="CustRel"/> / <see cref="IsCurrent"/> / <see cref="LastCustRel"/> /
/// <see cref="RevisionReason"/>) so quotation revisions behave exactly like SO revisions.
/// <para>
/// Deliberately does <b>not</b> carry SO fulfilment/billing state: no dual statuses, no
/// shipped/delivered/invoiced quantities and no stock reservation. A quotation is a commercial
/// offer, not a commitment to ship.
/// </para>
/// </summary>
public class SaQt
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string QtNo { get; set; } = string.Empty;

    /// <summary>Revision number (UI: Revision). Never reused within a QTNo chain.</summary>
    public short CustRel { get; set; } = 1;

    /// <summary>True only for the live revision of this QTNo.</summary>
    public bool IsCurrent { get; set; } = true;

    /// <summary>Chain-level high-water revision. Only the current row is authoritative.</summary>
    public short LastCustRel { get; set; } = 1;

    /// <summary>Why this revision was created (not why the previous one was superseded). Rev 1 is null.</summary>
    public string? RevisionReason { get; set; }

    public DateTime QtDate { get; set; }

    /// <summary>
    /// Offer validity limit (date only). <c>Today &gt; ValidUntil</c> is expired.
    /// Defaults to QT date + 30 days. Accepted quotations never auto-expire, but conversion is
    /// still refused once past this date.
    /// </summary>
    public DateTime ValidUntil { get; set; }

    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// <c>NONE</c> / <c>PARTIAL</c> / <c>FULL</c>. MVP operations only ever produce
    /// <c>NONE</c> → <c>FULL</c>; <c>PARTIAL</c> exists so partial conversion can be added later
    /// without a schema change.
    /// </summary>
    public string ConversionStatus { get; set; } = "NONE";

    /// <summary>MVP: <c>CONVERTED</c> only, and only when <see cref="Status"/> is CLOSED.</summary>
    public string? ClosedReason { get; set; }
    public DateTime? ClosedDate { get; set; }
    public string? ClosedBy { get; set; }

    public DateTime? SentDate { get; set; }
    public string? SentBy { get; set; }
    public DateTime? AcceptedDate { get; set; }
    public string? AcceptedBy { get; set; }
    public DateTime? LostDate { get; set; }
    public string? LostBy { get; set; }
    /// <summary>Required when the quotation is marked lost.</summary>
    public string? LostReason { get; set; }
    public DateTime? ExpiredDate { get; set; }

    public string CustCode { get; set; } = string.Empty;
    public string? CustName { get; set; }

    /// <summary>
    /// Generic customer-side reference, reused verbatim from SO. MVP does not distinguish
    /// RFQ vs PO vs tender semantics — it is free text. UI caption: "Customer RFQ / Reference".
    /// </summary>
    public string? CustPo { get; set; }

    public string? ShipName { get; set; }
    public string? ShipAddress1 { get; set; }
    public string? ShipAddress2 { get; set; }
    public string? ShipAddress3 { get; set; }
    public string? ShipAddress4 { get; set; }
    public string? ShipCity { get; set; }
    public string? ShipState { get; set; }
    public string? ShipPostalCode { get; set; }
    public string? ShipCountry { get; set; }
    public string? ShipTel { get; set; }
    public string? ShipFax { get; set; }

    public string? InvName { get; set; }
    public string? InvAddress1 { get; set; }
    public string? InvAddress2 { get; set; }
    public string? InvAddress3 { get; set; }
    public string? InvAddress4 { get; set; }
    public string? InvCity { get; set; }
    public string? InvState { get; set; }
    public string? InvPostalCode { get; set; }
    public string? InvCountry { get; set; }
    public string? InvTel { get; set; }
    public string? InvFax { get; set; }

    public string? ContactPerson { get; set; }
    public string? TaxGrCode { get; set; }
    public string? Currency { get; set; }
    public decimal CurrRate { get; set; } = 1m;
    public string? PayCode { get; set; }
    public string? Remarks { get; set; }
    /// <summary>Internal notes. Never printed on the customer-facing quotation.</summary>
    public string? InternalRemarks { get; set; }

    /// <summary>QT-only commercial term. SaSO has no ShipVia and this is not mapped onto the created SO.</summary>
    public string? ShipVia { get; set; }

    /// <summary>QT-only free-text delivery terms. SaSO has no DeliveryTerms; not mapped onto the created SO.</summary>
    public string? DeliveryTerms { get; set; }

    public decimal GrossAmnt { get; set; }
    public decimal Taxes { get; set; }
    public decimal TotAmnt { get; set; }
    public string? Prefix { get; set; }
    public string? LocationCode { get; set; }
    public decimal CustDiscount { get; set; }
    public string? ProjId { get; set; }
    public string? SalesRep { get; set; }
    public string? Ref1 { get; set; }
    public string? Ref2 { get; set; }
    public string? Ref3 { get; set; }
    public string? Ref4 { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<SaQtDetail> Details { get; set; } = new List<SaQtDetail>();
}
