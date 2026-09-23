using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.EInvoiceLib.Document;
using ErpWeb.EInvoiceLib.GenerateDoc;
using ErpWeb.EInvoiceLib.Model.InputData;
using Newtonsoft.Json;

namespace ErpWeb.Tests;

/// <summary>
/// The self-billed e-Invoice payload (LHDN 11 / 12 / 13) built from the Purchase self-billed documents.
///
/// <para>
/// The single most important assertion here is the party DIRECTION: a self-billed document is issued by
/// the buyer, so the VENDOR is the payload's <c>Supplier</c> and the COMPANY is its <c>Customer</c>. A
/// silent swap would produce a completely wrong — but perfectly well-formed — document.
/// </para>
/// </summary>
public class SaEInvoiceSelfBilledPayloadTests
{
    [Fact]
    public async Task A_self_billed_invoice_is_submitted_as_lhdn_11_with_the_vendor_as_supplier_and_the_company_as_customer()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedVendorAsync();
        await host.SeedSbInvoiceAsync();
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.SbInvoiceKey());

        Assert.True(result.Succeeded, result.ErrorMessage);
        var header = Assert.Single(host.Helper.Submitted);
        Assert.Equal(EInvoiceDocumentType.sb_invoice, header.docType);
        Assert.Equal(EInvoiceTestHost.SbInvoiceNo, header.DocumentNo);

        // Party direction: the VENDOR is the Supplier, our COMPANY is the Buyer (a self-billed document
        // is issued by the buyer).
        Assert.Equal("Vendor Sdn Bhd", header.Supplier.CompanyName);
        Assert.Equal("C9876543210", header.Supplier.TinNo);
        Assert.Equal("202201234567", header.Supplier.RegNo);
        Assert.Equal(TINRegistrationType.BRN, header.Supplier.RegType);
        Assert.Equal("3 Jalan Vendor", header.Supplier.Addr1);
        Assert.Equal("Klang", header.Supplier.CityName);
        Assert.Equal("41000", header.Supplier.PostalCode);
        Assert.Equal("+60398765432", header.Supplier.PhoneNo);
        // The vendor's MSIC + business description are the payload's supplier classification fields, and
        // the MSIC keeps its leading zero (it is a string, never a number).
        Assert.Equal("01234", header.Supplier.IndustryClassificationCode);
        Assert.Equal("Wholesale of building materials", header.Supplier.BizDesciption);

        Assert.Equal("Demo Sdn Bhd", header.Customer.CompanyName);
        Assert.Equal("C1234567890", header.Customer.TinNo);
        Assert.Equal("202301234567", header.Customer.RegNo);
        Assert.Equal("1 Jalan Demo", header.Customer.Addr1);
        Assert.Equal("Shah Alam", header.Customer.CityName);
        Assert.Equal("40100", header.Customer.PostalCode);
        Assert.Equal("+60312345678", header.Customer.PhoneNo);

        // An invoice is never a note: no BillingReference.
        Assert.True(string.IsNullOrEmpty(header.RefDocumentNo));
        Assert.True(string.IsNullOrEmpty(header.OriginInvoiceUUID));

        var line = Assert.Single(header.documentDetails);
        Assert.Equal("ITM01", line.ItemCode);
        Assert.Equal("022", line.ClassificationCode);

        // Authorized through its own Purchase self-billed menu.
        Assert.Contains(host.PermissionChecks, x =>
            x.Menu == MenuCodes.PurchaseSbInvoice && x.Permission == PermissionCodes.Submit);
    }

    [Fact]
    public async Task A_self_billed_credit_note_is_submitted_as_lhdn_12_and_carries_the_origin_uuid()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedVendorAsync();
        await host.SeedSbCdnAsync(type: "CN", docNo: EInvoiceTestHost.SbCdnNo);
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.SbCdnKey());

        Assert.True(result.Succeeded, result.ErrorMessage);
        var header = Assert.Single(host.Helper.Submitted);
        Assert.Equal(EInvoiceDocumentType.sb_creditnote, header.docType);

        // The note references the self-billed invoice it adjusts.
        Assert.Equal(EInvoiceTestHost.SbInvoiceNo, header.RefDocumentNo);
        Assert.Equal("UUID-SBI-1001", header.OriginInvoiceUUID);

        // Party direction is unchanged for a note: vendor Supplier, company Buyer.
        Assert.Equal("Vendor Sdn Bhd", header.Supplier.CompanyName);
        Assert.Equal("C9876543210", header.Supplier.TinNo);
        Assert.Equal("Demo Sdn Bhd", header.Customer.CompanyName);
        Assert.Equal("C1234567890", header.Customer.TinNo);

        Assert.Contains(host.PermissionChecks, x => x.Menu == MenuCodes.PurchaseSbCreditNote);
    }

    [Fact]
    public async Task A_self_billed_debit_note_is_submitted_as_lhdn_13()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedVendorAsync();
        await host.SeedSbCdnAsync(type: "DN", docNo: "SBD-1001");
        var service = host.CreateService();

        var result = await service.SubmitAsync(
            EInvoiceTestHost.SbCdnKey("SBD-1001", EInvoiceDocumentTypes.SelfBilledDebitNote));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var header = Assert.Single(host.Helper.Submitted);
        Assert.Equal(EInvoiceDocumentType.sb_debitnote, header.docType);
        Assert.Equal("UUID-SBI-1001", header.OriginInvoiceUUID);

        // A self-billed debit note (13) shares the CN/DN builder, so the direction must be identical.
        Assert.Equal("Vendor Sdn Bhd", header.Supplier.CompanyName);
        Assert.Equal("Demo Sdn Bhd", header.Customer.CompanyName);

        Assert.Contains(host.PermissionChecks, x => x.Menu == MenuCodes.PurchaseSbDebitNote);
    }

    [Fact]
    public async Task The_generated_payload_emits_the_self_billed_invoice_type_code()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedVendorAsync();
        await host.SeedSbInvoiceAsync();
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.SbInvoiceKey());
        Assert.True(result.Succeeded, result.ErrorMessage);

        // Belt and braces: the ERP token → library enum mapping is asserted above, and this proves the
        // generator turns that enum into LHDN type 11 (never 01).
        var header = Assert.Single(host.Helper.Submitted);
        var json = new GenerateInvoice(header).Generate();
        var root = JsonConvert.DeserializeObject<Root>(json);

        Assert.Equal(EInvoiceDocumentTypeMap.SelfBilledInvoice, root!.Invoice![0].InvoiceTypeCode![0]._);
    }

    [Fact]
    public async Task A_note_whose_origin_is_not_valid_yet_is_refused_before_any_myinvois_call()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedVendorAsync();
        await host.SeedSbCdnAsync(
            type: "CN", docNo: EInvoiceTestHost.SbCdnNo,
            originIrbmStatus: EInvoiceStatuses.Submitted);
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.SbCdnKey());

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.Validation, result.ErrorKind);
        Assert.True(result.ValidationErrors.ContainsKey("OriginInvoice.Uuid"));
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task A_self_billed_submit_is_refused_when_the_vendor_identity_is_incomplete()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedVendorAsync(tin: null, phone: null);
        await host.SeedSbInvoiceAsync();
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.SbInvoiceKey());

        // For a self-billed document the VENDOR is the payload's supplier block, so the supplier rules
        // are exactly the vendor rules - keyed Supplier.*, not Buyer.*.
        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("Supplier.Tin"));
        Assert.True(result.ValidationErrors.ContainsKey("Supplier.Phone"));
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task A_self_billed_submit_is_refused_when_the_vendor_has_no_msic_or_business_description()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedVendorAsync(msic: null, bizDesc: null);
        await host.SeedSbInvoiceAsync();
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.SbInvoiceKey());

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("Supplier.Msic"));
        Assert.True(result.ValidationErrors.ContainsKey("Supplier.BizDescription"));
        // The vendor master is the fix location, and no MyInvois call is made.
        Assert.Contains("vendor master", result.ValidationErrors["Supplier.Msic"], StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task A_self_billed_document_is_never_rebuilt_once_accepted_even_if_the_vendor_master_changes()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedVendorAsync();
        await host.SeedSbInvoiceAsync();
        var service = host.CreateService();

        var first = await service.SubmitAsync(EInvoiceTestHost.SbInvoiceKey());
        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.Equal("Vendor Sdn Bhd", Assert.Single(host.Helper.Submitted).Supplier.CompanyName);

        // The vendor block is read live at build time and there is deliberately no snapshot, so the only
        // thing that stops an accepted payload from changing is the submit gate. Edit the master AFTER
        // the accept...
        await host.UpdateVendorAsync(name: "Renamed Vendor Sdn Bhd", tin: "C1111111111");

        // ...and the document can never be rebuilt or resubmitted: exactly one payload was ever sent,
        // and it still names the original vendor.
        var second = await service.SubmitAsync(EInvoiceTestHost.SbInvoiceKey());
        Assert.False(second.Succeeded);
        Assert.Single(host.Helper.Submitted);
        Assert.Equal("Vendor Sdn Bhd", host.Helper.Submitted[0].Supplier.CompanyName);
        Assert.Equal("C9876543210", host.Helper.Submitted[0].Supplier.TinNo);
    }

    [Fact]
    public void The_mapper_reverses_the_parties_when_the_source_carries_a_vendor_party()
    {
        var mapped = new EInvoiceDocumentMapper().Map(SelfBilledSource(), CompanyProfile(), "1.0");

        Assert.True(mapped.IsValid);
        Assert.Equal("Vendor Sdn Bhd", mapped.Header.Supplier.CompanyName);
        Assert.Equal("C9876543210", mapped.Header.Supplier.TinNo);
        Assert.Equal("01234", mapped.Header.Supplier.IndustryClassificationCode);
        Assert.Equal("Wholesale of building materials", mapped.Header.Supplier.BizDesciption);
        Assert.Equal("Demo Sdn Bhd", mapped.Header.Customer.CompanyName);
        Assert.Equal("C1234567890", mapped.Header.Customer.TinNo);
    }

    [Fact]
    public void The_mapper_keeps_the_company_as_supplier_when_the_source_has_no_vendor_party()
    {
        // The sales direction must be untouched: no vendor party on the source = today's behaviour.
        var mapped = new EInvoiceDocumentMapper().Map(SelfBilledSource(withVendorParty: false), CompanyProfile(), "1.0");

        Assert.True(mapped.IsValid);
        Assert.Equal("Demo Sdn Bhd", mapped.Header.Supplier.CompanyName);
        Assert.Equal("C1234567890", mapped.Header.Supplier.TinNo);
    }

    [Fact]
    public async Task A_self_billed_invoice_can_be_submitted_while_its_erp_status_is_NEW()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedVendorAsync();
        await host.SeedSbInvoiceAsync(status: "NEW");
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.SbInvoiceKey());

        // The self-billed families have no ERP finalisation step — the NEW/POSTED dimension is retired —
        // so the e-Invoice state alone decides whether a document may be sent. The sales POSTED rule must
        // NOT have leaked onto them.
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Single(host.Helper.Submitted);
    }

    [Fact]
    public async Task An_incomplete_line_is_refused_by_the_validator()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedVendorAsync();
        await host.SeedSbInvoiceAsync(validLines: false);
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.SbInvoiceKey());

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("Line1.Classification"));
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task Submitting_an_unknown_self_billed_document_reports_not_found()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedVendorAsync();
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.SbInvoiceKey("SBI-NOPE"));

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.NotFound, result.ErrorKind);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task A_self_billed_document_is_persisted_with_its_myinvois_identity_after_submission()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedVendorAsync();
        await host.SeedSbInvoiceAsync();
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.SbInvoiceKey());
        Assert.True(result.Succeeded, result.ErrorMessage);

        var stored = await host.GetSbInvoiceAsync(EInvoiceTestHost.SbInvoiceNo);
        Assert.Equal(EInvoiceStatuses.Submitted, EInvoiceStatuses.Normalize(stored.IrbmStatus));
        Assert.False(string.IsNullOrWhiteSpace(stored.IrbmUuid));
        Assert.False(string.IsNullOrWhiteSpace(stored.IrbmSubmitId));
    }

    [Fact]
    public async Task A_self_billed_note_records_the_origin_uuid_on_the_erp_document()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedVendorAsync();
        await host.SeedSbCdnAsync(type: "CN", docNo: EInvoiceTestHost.SbCdnNo);
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.SbCdnKey());
        Assert.True(result.Succeeded, result.ErrorMessage);

        var stored = await host.GetSbCdnAsync(EInvoiceTestHost.SbCdnNo);
        Assert.Equal(EInvoiceStatuses.Submitted, EInvoiceStatuses.Normalize(stored.IrbmStatus));
        Assert.Equal("UUID-SBI-1001", stored.IrbmOriUuid);
    }

    /// <summary>The company exactly as <c>SaEInvoiceService.LoadSupplierAsync</c> resolves it.</summary>
    private static EInvoiceSupplierProfile CompanyProfile() => new()
    {
        CompanyCode = "DEMO",
        Enabled = true,
        CompanyName = "Demo Sdn Bhd",
        TinNo = "C1234567890",
        RegistrationNo = "202301234567",
        RegType = "BRN",
        SstNo = "A01-2345-67890123",
        MsicCode = "62010",
        BusinessDescription = "Software development",
        Addr1 = "1 Jalan Demo",
        City = "Shah Alam",
        State = "10",
        PostalCode = "40100",
        Country = "MYS",
        Phone = "0312345678",
        Email = "billing@demo.test"
    };

    /// <summary>The vendor party exactly as <see cref="PoSupplierPartyProfileResolver"/> builds it.</summary>
    private static PoSupplierPartyProfile VendorProfile() => new(
        "Vendor Sdn Bhd", "3 Jalan Vendor", null, null, null, "Klang", "10", "41000", "MYS",
        "0398765432", "ar@vendor.test", "C9876543210", "202201234567", "BRN",
        "A01-2345-67890123", "01234", "Wholesale of building materials");

    /// <summary>A self-billed source document with the company in the buyer block.</summary>
    private static EInvoiceSourceDocument SelfBilledSource(bool withVendorParty = true) => new()
    {
        DocumentType = EInvoiceDocumentTypes.SelfBilledInvoice,
        DocumentNo = "SBI-1001",
        DocumentDate = new DateTime(2026, 9, 23),
        Currency = "MYR",
        AmountExclTax = 100m,
        TaxAmount = 6m,
        AmountIncTax = 106m,
        SupplierParty = withVendorParty ? VendorProfile() : null,
        CustomerName = "Demo Sdn Bhd",
        CustomerTin = "C1234567890",
        CustomerRegNo = "202301234567",
        CustomerRegType = "BRN",
        CustomerSstNo = "A01-2345-67890123",
        CustomerAddr1 = "1 Jalan Demo",
        CustomerCity = "Shah Alam",
        CustomerState = "10",
        CustomerPostalCode = "40100",
        CustomerCountry = "MYS",
        CustomerPhone = "0312345678",
        CustomerEmail = "billing@demo.test",
        Lines = []
    };
}
