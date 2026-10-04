using EInvoiceAPI.Utility;
using ErpWeb.Core.EInvoice;
using ErpWeb.EInvoiceLib.GenerateDoc;
using ErpWeb.Model.Repositories.Sales;

namespace ErpWeb.Tests.Sales.Transaction;
/// <summary>
/// Two rules that together make "correct the customer profile, then submit again" sufficient to repair a
/// rejected e-Invoice.
///
/// <list type="number">
/// <item><b>D-1</b> the buyer block of the payload is sourced from the CUSTOMER MASTER, not from the
/// document's own <c>Inv*</c> print snapshot;</item>
/// <item><b>D-4</b> a credit/debit note must be POSTED, and the service - not only the list UI - enforces
/// it.</item>
/// </list>
///
/// <para>
/// These tests deliberately assert on <see cref="FakeSubmitDocumentHelper.Submitted"/>, which holds the
/// <c>DocumentHeader</c> the vendored library receives. That is one step beyond the source object, so a
/// regression in the mapper or the generator is caught here rather than in a manual smoke test.
/// </para>
/// </summary>
[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.EInvoice)]
public class SaEInvoiceBuyerSourcingTests
{
    // ────────────────────────────── D-4: the POSTED gate ──────────────────────────────

    [Fact]
    public async Task A_draft_credit_note_is_refused_by_the_service_not_only_by_the_ui()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedCreditNoteAsync(status: SaCdnStatuses.New);
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.CdnKey());

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.StateRule, result.ErrorKind);
        Assert.Contains("Credit note", result.ErrorMessage);
        Assert.Contains("must be POSTED", result.ErrorMessage);

        // The refusal happens BEFORE the SUBMITTING claim, so nothing reached MyInvois and the draft is
        // still a draft - it was not left locked by a half-started submission.
        Assert.Empty(host.Helper.Calls);
        var saved = await host.GetCdnAsync();
        Assert.Equal(SaCdnStatuses.New, saved!.Status);
        Assert.Null(saved.IrbmStatus);
    }

    [Fact]
    public async Task A_posted_credit_note_still_submits()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedCreditNoteAsync();
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.CdnKey());

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(EInvoiceStatuses.Submitted, result.Status);
    }

    /// <summary>
    /// A debit note can never carry an origin invoice number (SaCdnService writes <c>InvNo</c> for a credit
    /// note only), so a POSTED DN is refused by the ORIGIN rule. That difference is exactly what proves the
    /// POSTED gate ran first: the draft DN is refused for being a draft, the posted one gets past that gate.
    /// </summary>
    [Fact]
    public async Task A_draft_debit_note_is_refused_before_the_origin_rules_and_a_posted_one_gets_past_them()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedCreditNoteAsync(docNo: "DN-1001", type: SaCdnTypes.DebitNote, status: SaCdnStatuses.New);
        await host.SeedCreditNoteAsync(docNo: "DN-1002", type: SaCdnTypes.DebitNote);
        var service = host.CreateService();
        var debitNote = EInvoiceDocumentTypes.DebitNote;

        var draft = await service.SubmitAsync(EInvoiceTestHost.CdnKey("DN-1001", debitNote));
        var posted = await service.SubmitAsync(EInvoiceTestHost.CdnKey("DN-1002", debitNote));

        Assert.False(draft.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.StateRule, draft.ErrorKind);
        Assert.Contains("Debit note", draft.ErrorMessage);
        Assert.Contains("must be POSTED", draft.ErrorMessage);

        // Past the POSTED gate, and now stopped by the origin rule instead - a different refusal, which is
        // the point of the pair.
        Assert.False(posted.Succeeded);
        Assert.Contains("OriginInvoice.No", posted.ValidationErrors.Keys);
        Assert.Empty(host.Helper.Calls);
    }

    // ───────────────────── D-1: the master is the payload's source ─────────────────────

    [Fact]
    public async Task Correcting_the_master_address_and_phone_is_enough_to_resubmit_an_invalid_invoice()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Invalid, irbmUuid: "UUID-INV-1001");

        // The operator corrects the profile after LHDN rejected the document: no unpost, no rebuild.
        await host.UpdateCustomerAsync(x =>
        {
            x.Address1 = "9 Jalan Baharu";
            x.City = "Shah Alam";
            x.Tel = "0311112222";
        });
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(result.Succeeded, result.ErrorMessage);
        var submitted = Assert.Single(host.Helper.Submitted);
        Assert.Equal("9 Jalan Baharu", submitted.Customer.Addr1);
        Assert.Equal("Shah Alam", submitted.Customer.CityName);
        Assert.Equal("+60311112222", submitted.Customer.PhoneNo);
    }

    [Fact]
    public async Task The_document_snapshot_is_untouched_while_the_payload_uses_the_master()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Invalid, irbmUuid: "UUID-INV-1001");
        await host.UpdateCustomerAsync(x => x.CustName = "Buyer Baharu Sdn Bhd");
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(result.Succeeded, result.ErrorMessage);
        // D-1: the payload follows the master...
        Assert.Equal("Buyer Baharu Sdn Bhd", Assert.Single(host.Helper.Submitted).Customer.CompanyName);

        // ...and D-3: the document's own column keeps its print/business snapshot.
        var saved = await host.GetInvoiceAsync();
        Assert.Equal("Buyer Sdn Bhd", saved!.InvName);
    }

    /// <summary>
    /// A note mirrors the invoice it references for the buyer IDENTITY (D-2 - MyInvois expects the note's
    /// buyer to match the referenced document), but takes the ADDRESS and CONTACT from the current master
    /// (D-1). The two sources are joined deliberately, so this pins the split.
    /// </summary>
    [Fact]
    public async Task A_credit_note_keeps_the_origin_identity_but_takes_the_address_from_the_master()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedCreditNoteAsync();
        await host.UpdateCustomerAsync(x =>
        {
            x.TinNo = "TIN-CORRECTED";
            x.Address1 = "9 Jalan Baharu";
        });
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.CdnKey());

        Assert.True(result.Succeeded, result.ErrorMessage);
        var submitted = Assert.Single(host.Helper.Submitted);
        // Identity: the origin invoice's submitted value, NOT the corrected master.
        Assert.Equal("C9876543210", submitted.Customer.TinNo);
        // Contact: the current master.
        Assert.Equal("9 Jalan Baharu", submitted.Customer.Addr1);
    }

    [Fact]
    public async Task AppInvoice_false_uses_the_billing_overrides()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        await host.UpdateCustomerAsync(x =>
        {
            x.AppInvoice = false;
            x.InvName = "Billing Entity Sdn Bhd";
            x.InvAddress1 = "5 Billing Road";
            x.InvCity = "Shah Alam";
            x.InvTel = "0355556666";
        });
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(result.Succeeded, result.ErrorMessage);
        var submitted = Assert.Single(host.Helper.Submitted);
        Assert.Equal("Billing Entity Sdn Bhd", submitted.Customer.CompanyName);
        Assert.Equal("5 Billing Road", submitted.Customer.Addr1);
        Assert.Equal("Shah Alam", submitted.Customer.CityName);
        Assert.Equal("+60355556666", submitted.Customer.PhoneNo);
    }

    // ─────────────── F9: the GENERATED payload, not just the mapped header ───────────────

    /// <summary>
    /// Drives the real generator over the captured <c>DocumentHeader</c> and asserts the serialized
    /// document. This is the layer a mapper-only assertion cannot reach.
    ///
    /// <para>
    /// The SIGNED payload is out of scope: <c>EInvSignatureHelper.StartProcess</c> needs the configured
    /// secrets and document version "1.1". The unsigned generated JSON is enough to prove which source
    /// won.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_generated_credit_note_payload_carries_the_master_values_not_the_snapshot()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedCreditNoteAsync();
        await host.UpdateCustomerAsync(x =>
        {
            x.Address1 = "9 Jalan Baharu";
            x.City = "Shah Alam";
        });
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.CdnKey());
        Assert.True(result.Succeeded, result.ErrorMessage);

        var header = Assert.Single(host.Helper.Submitted);
        var generator = new GenerateCreditNote(header);
        generator.Generate();
        var json = HashUtility.SerializeJsonIndented(generator.InvoiceRoot);

        Assert.Contains("9 Jalan Baharu", json, StringComparison.Ordinal);
        Assert.Contains("Shah Alam", json, StringComparison.Ordinal);
        // The stale values the note was raised with (cn.InvAddress1 / cn.City) must not appear at all.
        Assert.DoesNotContain("2 Jalan Buyer", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Petaling Jaya", json, StringComparison.Ordinal);
    }
}
