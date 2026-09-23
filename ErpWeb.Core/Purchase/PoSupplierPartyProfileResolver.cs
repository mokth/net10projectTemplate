using ErpWeb.Model.Entities.Purchase;

namespace ErpWeb.Core.Purchase;

/// <summary>
/// The e-Invoice <c>Supplier</c> block for a self-billed document, resolved from the VENDOR master.
/// <para>
/// A self-billed e-Invoice (LHDN 11/12/13) reverses the parties: it is issued by the BUYER, so our
/// company is the payload's <c>Customer</c> and the VENDOR is its <c>Supplier</c>. This record is that
/// supplier block — the vendor's identity, address, contact and MSIC/business description.
/// </para>
/// <para>
/// It is deliberately read live, inside the source build: for a submission, "correct the vendor master"
/// has to be sufficient on its own. There is no snapshot column on the document and this record is not
/// persisted; the guarantee that an accepted payload can never change is the submit gate that refuses
/// SUBMITTED/VALID documents (see <c>SaEInvoiceService.BuildSourceAsync</c>).
/// </para>
/// <para>
/// This must NOT be confused with the submission identity: <c>EInvoiceDocumentState.Supplier</c> stays
/// the COMPANY because it feeds the MyInvois credentials and the submission grouping.
/// </para>
/// </summary>
/// <param name="Name">Vendor name.</param>
/// <param name="Address1">Address line 1.</param>
/// <param name="Address2">Address line 2.</param>
/// <param name="Address3">Address line 3.</param>
/// <param name="Address4">Address line 4.</param>
/// <param name="City">City.</param>
/// <param name="State">State, RAW — the LHDN state code when set, otherwise the free-text state.</param>
/// <param name="PostalCode">Postal code.</param>
/// <param name="Country">Country, RAW — the LHDN country code when set, otherwise the free-text country.</param>
/// <param name="Phone">Telephone (canonicalised to E.164 by the mapper).</param>
/// <param name="Email">E-mail.</param>
/// <param name="Tin">TIN (<c>PoSupplier.TinNo</c>).</param>
/// <param name="RegNo">Registration / identity number (<c>PoSupplier.SupplierBrn</c>).</param>
/// <param name="RegType">Registration type, RAW (normalised at the e-Invoice boundary).</param>
/// <param name="SstNo">SST number (<c>PoSupplier.GstregNo</c>).</param>
/// <param name="MsicCode">MSIC code (<c>PoSupplier.MiscCode</c>), the payload's IndustryClassificationCode.</param>
/// <param name="BusinessDescription">Business description (<c>PoSupplier.BizDesc</c>), the vendor's LHDN business description.</param>
public sealed record PoSupplierPartyProfile(
    string? Name,
    string? Address1,
    string? Address2,
    string? Address3,
    string? Address4,
    string? City,
    string? State,
    string? PostalCode,
    string? Country,
    string? Phone,
    string? Email,
    string? Tin,
    string? RegNo,
    string? RegType,
    string? SstNo,
    string? MsicCode,
    string? BusinessDescription);

/// <summary>
/// Resolves the payload's supplier block for a self-billed document from a vendor master row. Pure: no
/// EF, no tenant context and no state, so the rule set is unit-testable without a database.
/// </summary>
/// <remarks>
/// Unlike <c>SaCustBuyerProfileResolver</c> there is no billing-address branch: a vendor has one address
/// block, and the document has no transaction-level override for it. The dedicated LHDN code columns
/// (<c>StateCode</c>, <c>CountryCode</c>) win when set — the same precedence the company's own supplier
/// profile uses — because the free-text columns are not guaranteed to be translatable.
/// </remarks>
public static class PoSupplierPartyProfileResolver
{
    public static PoSupplierPartyProfile Resolve(PoSupplier vendor)
    {
        ArgumentNullException.ThrowIfNull(vendor);

        return new PoSupplierPartyProfile(
            vendor.SuppName,
            vendor.Address1,
            vendor.Address2,
            vendor.Address3,
            vendor.Address4,
            vendor.City,
            string.IsNullOrWhiteSpace(vendor.StateCode) ? vendor.State : vendor.StateCode,
            vendor.PostalCode,
            string.IsNullOrWhiteSpace(vendor.CountryCode) ? vendor.Country : vendor.CountryCode,
            vendor.Tel,
            vendor.Email,
            vendor.TinNo,
            vendor.SupplierBrn,
            vendor.RegType,
            vendor.GstregNo,
            vendor.MiscCode,
            vendor.BizDesc);
    }
}
