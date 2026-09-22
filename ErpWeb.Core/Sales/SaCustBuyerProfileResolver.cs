using ErpWeb.Model.Entities.CustomerProfile;

namespace ErpWeb.Core.Sales;

/// <summary>
/// The complete e-Invoice buyer block, resolved from the customer master.
///
/// <para>
/// This is the ONE definition of the <c>AppInvoice</c> billing-address choice. The sales save path
/// (<c>SaInvoiceService.BackfillCommercialHeader</c>) applies the same choice when it fills a blank
/// request field; an e-Invoice submission uses the whole resolved block, because for a submission
/// "correct the customer master" has to be sufficient on its own.
/// </para>
///
/// <para>
/// This is <b>not</b> the frozen audit snapshot. The frozen <c>SaInvoice.Buyer*</c> columns record what
/// was actually sent to MyInvois and are a different concern — see
/// <c>EInvoiceStatuses.IsBuyerIdentityFrozen</c>.
/// </para>
/// </summary>
/// <param name="Name">Billing name.</param>
/// <param name="Address1">Billing address line 1.</param>
/// <param name="Address2">Billing address line 2.</param>
/// <param name="Address3">Billing address line 3.</param>
/// <param name="Address4">Billing address line 4.</param>
/// <param name="City">Billing city.</param>
/// <param name="State">Billing state.</param>
/// <param name="PostalCode">Billing postal code.</param>
/// <param name="Country">Billing country.</param>
/// <param name="Phone">Billing telephone.</param>
/// <param name="Email">Billing e-mail.</param>
/// <param name="Tin">Tax identification number.</param>
/// <param name="RegNo">Registration / identity number.</param>
/// <param name="RegType">Registration type, RAW (see the resolver's remarks).</param>
/// <param name="SstNo">SST registration number.</param>
public sealed record SaCustBuyerProfile(
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
    string? SstNo);

/// <summary>
/// Resolves the e-Invoice buyer block from a customer master row. Pure: no EF, no tenant context and no
/// state, so the whole rule set is unit-testable without a database.
/// </summary>
public static class SaCustBuyerProfileResolver
{
    /// <summary>
    /// Resolves the buyer block for <paramref name="customer"/>. The caller supplies the row for the
    /// document's <c>CustCode</c>; a missing customer is the caller's error to report, not this method's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>AppInvoice picks the block.</b> <c>true</c> = the main address (<c>CustName</c>,
    /// <c>Address1..4</c>, <c>City</c>, <c>State</c>, <c>PostalCode</c>, <c>Country</c>, <c>Tel</c>);
    /// anything else = the billing overrides (<c>InvName</c>, <c>InvAddress1..3</c>, <c>InvCity</c>,
    /// <c>InvState</c>, <c>InvPostalCode</c>, <c>InvCountry</c>, <c>InvTel</c>). That is exactly the choice
    /// <c>SaInvoiceService.BackfillCommercialHeader</c> makes.
    /// </para>
    /// <para>
    /// A blank field then falls back to the other block, because the payload no longer reads the document
    /// snapshot and a blank buyer address would otherwise refuse the submission. The fallback differs by
    /// field shape:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Address lines 1-3 are a block.</b> Line 1 decides for all three, so a billing override
    /// that never filled line 1 carries no address and the main lines are used — lines 2 and 3 can never be
    /// spliced into a different address.</item>
    /// <item><b>Every scalar field falls back on its own</b> — name, city, state, postal code, country and
    /// telephone are single-valued, so an override that only filled line 1 still gets the main city,
    /// state and postal code rather than being refused.</item>
    /// </list>
    /// <para>
    /// <c>Address4</c> is a deliberate exception to all of it: <see cref="SaCust"/> has no
    /// <c>InvAddress4</c>, so line 4 always comes from the main <c>Address4</c> in both branches.
    /// </para>
    /// <para>
    /// <c>RegType</c> is returned RAW. Normalising it to the four canonical LHDN types stays at the
    /// e-Invoice boundary (<c>EInvoiceRegistrationTypes</c>), which is where that vocabulary is defined.
    /// </para>
    /// </remarks>
    public static SaCustBuyerProfile Resolve(SaCust customer)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var useMain = customer.AppInvoice == true;

        // InvEmail is preferred over Email in both branches: that is the shipped e-Invoice rule
        // (SaInvoiceService.ResolveInvoiceEmail), kept verbatim.
        var email = string.IsNullOrWhiteSpace(customer.InvEmail) ? customer.Email : customer.InvEmail;

        var chosen = useMain
            ? new SaCustBuyerProfile(
                customer.CustName,
                customer.Address1,
                customer.Address2,
                customer.Address3,
                customer.Address4,
                customer.City,
                customer.State,
                customer.PostalCode,
                customer.Country,
                customer.Tel,
                email,
                customer.TinNo,
                customer.CustBrn,
                customer.RegType,
                customer.GstregNo)
            : new SaCustBuyerProfile(
                NullIfBlank(customer.InvName) ?? customer.CustName,
                customer.InvAddress1,
                customer.InvAddress2,
                customer.InvAddress3,
                customer.Address4,
                customer.InvCity,
                customer.InvState,
                customer.InvPostalCode,
                customer.InvCountry,
                customer.InvTel,
                email,
                customer.TinNo,
                customer.CustBrn,
                customer.RegType,
                customer.GstregNo);

        var linesFromMain = useMain || string.IsNullOrWhiteSpace(chosen.Address1);

        return chosen with
        {
            Address1 = linesFromMain ? customer.Address1 : customer.InvAddress1,
            Address2 = linesFromMain ? customer.Address2 : customer.InvAddress2,
            Address3 = linesFromMain ? customer.Address3 : customer.InvAddress3,
            City = NullIfBlank(chosen.City) ?? (useMain ? customer.InvCity : customer.City),
            State = NullIfBlank(chosen.State) ?? (useMain ? customer.InvState : customer.State),
            PostalCode = NullIfBlank(chosen.PostalCode) ?? (useMain ? customer.InvPostalCode : customer.PostalCode),
            Country = NullIfBlank(chosen.Country) ?? (useMain ? customer.InvCountry : customer.Country),
            Phone = NullIfBlank(chosen.Phone) ?? (useMain ? customer.InvTel : customer.Tel),
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
