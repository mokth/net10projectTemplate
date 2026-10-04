using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using ErpWeb.EInvoiceLib.GenerateDoc;
using ErpWeb.EInvoiceLib.Model.Document;
using ErpWeb.EInvoiceLib.Model.InputData;

namespace ErpWeb.Tests;

/// <summary>
/// Phase 4 add-ons: the additive self-billed document-type mapping (LHDN 11/12/13) and the read-only
/// TIN tools.
///
/// <para>
/// Self-billed submission is now wired to the Purchase self-billed documents (`PoSbInvoice` / `PoSbCdn`),
/// so these tests pin the vocabulary and the authorization routing rather than a refusal.
/// </para>
/// </summary>
[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.EInvoice)]
public class SaEInvoiceSelfBillAndTinTests
{
    // ─────────────────────────────── Document type map ───────────────────────────────

    [Theory]
    [InlineData(EInvoiceDocumentType.invoice, "01")]
    [InlineData(EInvoiceDocumentType.creditnote, "02")]
    [InlineData(EInvoiceDocumentType.debitnote, "03")]
    [InlineData(EInvoiceDocumentType.sb_invoice, "11")]
    [InlineData(EInvoiceDocumentType.sb_creditnote, "12")]
    [InlineData(EInvoiceDocumentType.sb_debitnote, "13")]
    public void Each_document_family_maps_to_its_own_lhdn_code(EInvoiceDocumentType docType, string expected)
    {
        Assert.Equal(expected, EInvoiceDocumentTypeMap.GetDocumentTypeCode(docType));
    }

    [Fact]
    public void A_self_billed_invoice_is_never_emitted_as_invoice_type_01()
    {
        var selfBilled = EInvoiceDocumentTypeMap.GetDocumentTypeCode(EInvoiceDocumentType.sb_invoice);

        Assert.NotEqual(EInvoiceDocumentTypeMap.Invoice, selfBilled);
        Assert.True(EInvoiceDocumentTypeMap.IsSelfBilled(selfBilled));
        Assert.False(EInvoiceDocumentTypeMap.IsSelfBilled(EInvoiceDocumentTypeMap.Invoice));
        Assert.False(EInvoiceDocumentTypeMap.IsSelfBilled(EInvoiceDocumentTypeMap.CreditNote));
    }

    [Theory]
    [InlineData("11", "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2")]
    [InlineData("12", "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2")]
    [InlineData("13", "urn:oasis:names:specification:ubl:schema:xsd:DebitNote-2")]
    public void Self_billed_types_use_the_note_and_invoice_ubl_roots(string code, string expectedNamespace)
    {
        Assert.Equal(expectedNamespace, EInvoiceDocumentTypeMap.GetDocumentNamespace(code));
    }

    [Fact]
    public void Self_billed_credit_and_debit_notes_need_a_billing_reference()
    {
        Assert.True(EInvoiceDocumentTypeMap.IsCreditOrDebitNote(EInvoiceDocumentTypeMap.SelfBilledCreditNote));
        Assert.True(EInvoiceDocumentTypeMap.IsCreditOrDebitNote(EInvoiceDocumentTypeMap.SelfBilledDebitNote));
        Assert.False(EInvoiceDocumentTypeMap.IsCreditOrDebitNote(EInvoiceDocumentTypeMap.SelfBilledInvoice));
    }

    [Fact]
    public void Self_billed_credit_notes_sign_with_the_credit_note_signature()
    {
        Assert.Equal(
            EInvoiceDocumentTypeMap.GetSignatureId(EInvoiceDocumentTypeMap.CreditNote),
            EInvoiceDocumentTypeMap.GetSignatureId(EInvoiceDocumentTypeMap.SelfBilledCreditNote));
        Assert.Equal(
            EInvoiceDocumentTypeMap.GetSignatureId(EInvoiceDocumentTypeMap.DebitNote),
            EInvoiceDocumentTypeMap.GetSignatureId(EInvoiceDocumentTypeMap.SelfBilledDebitNote));
    }

    [Fact]
    public void The_erp_self_billed_tokens_are_known_document_types()
    {
        Assert.True(EInvoiceDocumentTypes.IsSelfBilled(EInvoiceDocumentTypes.SelfBilledInvoice));
        Assert.True(EInvoiceDocumentTypes.IsSelfBilled(EInvoiceDocumentTypes.SelfBilledCreditNote));
        Assert.True(EInvoiceDocumentTypes.IsSelfBilled(EInvoiceDocumentTypes.SelfBilledDebitNote));

        // The façade can load a source document for all six families.
        Assert.True(EInvoiceDocumentTypes.IsKnown(EInvoiceDocumentTypes.SelfBilledInvoice));
        Assert.True(EInvoiceDocumentTypes.IsKnown(EInvoiceDocumentTypes.SelfBilledCreditNote));
        Assert.True(EInvoiceDocumentTypes.IsKnown(EInvoiceDocumentTypes.SelfBilledDebitNote));
        Assert.True(EInvoiceDocumentTypes.IsKnown(EInvoiceDocumentTypes.Invoice));

        // Only the note families reference an origin — a self-billed invoice never does.
        Assert.True(EInvoiceDocumentTypes.IsNote(EInvoiceDocumentTypes.SelfBilledCreditNote));
        Assert.True(EInvoiceDocumentTypes.IsNote(EInvoiceDocumentTypes.SelfBilledDebitNote));
        Assert.True(EInvoiceDocumentTypes.IsNote(EInvoiceDocumentTypes.CreditNote));
        Assert.False(EInvoiceDocumentTypes.IsNote(EInvoiceDocumentTypes.SelfBilledInvoice));
        Assert.False(EInvoiceDocumentTypes.IsNote(EInvoiceDocumentTypes.Invoice));
    }

    [Fact]
    public async Task A_self_billed_submit_for_a_missing_document_is_not_found_and_never_calls_myinvois()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        var service = host.CreateService();

        var result = await service.SubmitAsync(new SaEInvoiceDocumentKey
        {
            DocumentType = EInvoiceDocumentTypes.SelfBilledInvoice,
            DocumentNo = "SBI-1001"
        });

        // No such self-billed invoice exists, so the load fails — but only after authorization, and
        // without ever reaching MyInvois.
        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.NotFound, result.ErrorKind);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task A_self_billed_submit_is_authorized_through_its_own_purchase_menu()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        var service = host.CreateService();

        await service.SubmitAsync(new SaEInvoiceDocumentKey
        {
            DocumentType = EInvoiceDocumentTypes.SelfBilledCreditNote,
            DocumentNo = "SBC-1001"
        });

        Assert.Contains(host.PermissionChecks, x =>
            x.Menu == MenuCodes.PurchaseSbCreditNote && x.Permission == PermissionCodes.Submit);

        // The ordinary purchase CN menu must never be consulted for a self-billed note.
        Assert.DoesNotContain(host.PermissionChecks, x => x.Menu == MenuCodes.PurchaseCreditNote);
    }

    [Fact]
    public async Task An_unknown_document_type_is_still_reported_as_a_validation_error()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        var service = host.CreateService();

        var result = await service.SubmitAsync(new SaEInvoiceDocumentKey
        {
            DocumentType = "XX",
            DocumentNo = "NOPE-1"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.Validation, result.ErrorKind);
        Assert.Empty(host.PermissionChecks);
    }

    // ─────────────────────────────── TIN tools ───────────────────────────────

    [Fact]
    public async Task Tin_validation_passes_the_erp_identity_type_through_as_the_lhdn_enum()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        var service = host.CreateService();

        var result = await service.ValidateTinAsync("BRN", "202201234567", "C9876543210");

        Assert.True(result.IsValid);
        Assert.Equal("C9876543210", host.Helper.LastValidatedTin);
        Assert.Equal("BRN", host.Helper.LastValidatedIdType);
        Assert.Equal(MenuCodes.SalesEInvoiceTin, host.PermissionChecks[^1].Menu);
        Assert.Equal(PermissionCodes.Access, host.PermissionChecks[^1].Permission);
    }

    [Fact]
    public async Task Tin_validation_reports_a_tin_myinvois_says_is_wrong()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        host.Helper.ValidateTinHandler = (_, _, _) => FakeSubmitDocumentHelper.Failure<bool>("TIN and identity do not match.");
        var service = host.CreateService();

        var result = await service.ValidateTinAsync("BRN", "202201234567", "C0000000000");

        Assert.False(result.IsValid);
        Assert.Contains("do not match", result.ErrorMessage);
    }

    [Theory]
    [InlineData("BRN", "", "C1")]
    [InlineData("BRN", "202201234567", "")]
    [InlineData("NOT-A-TYPE", "202201234567", "C1")]
    public async Task Tin_validation_refuses_incomplete_or_unknown_input_without_calling_myinvois(
        string idType,
        string idValue,
        string tin)
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        var service = host.CreateService();

        var result = await service.ValidateTinAsync(idType, idValue, tin);

        Assert.False(result.IsValid);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task Tin_search_returns_the_tins_myinvois_sent_back()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        host.Helper.SearchTinHandler = _ => FakeSubmitDocumentHelper.Success<List<TINInfo>>([
            new TINInfo { tin = "C1111111110" },
            new TINInfo { tin = "C2222222220" },
            new TINInfo { tin = "  " }
        ]);
        var service = host.CreateService();

        var result = await service.SearchTinAsync(new SaEInvoiceTinSearchQuery { TaxpayerName = "Demo" });

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("C1111111110", result.Rows[0].Tin);
        Assert.Equal("Demo", host.Helper.LastSearchTinQuery!.taxpayerName);
    }

    [Fact]
    public async Task Tin_search_refuses_a_query_with_nothing_to_search_on()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        var service = host.CreateService();

        var result = await service.SearchTinAsync(new SaEInvoiceTinSearchQuery());

        Assert.False(result.Succeeded);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task Tin_tools_need_the_tin_permission()
    {
        await using var host = EInvoiceTestHost.Create(authorized: false);
        await host.SeedCompanyAsync();
        var service = host.CreateService();

        var check = await service.ValidateTinAsync("BRN", "202201234567", "C9876543210");
        var search = await service.SearchTinAsync(new SaEInvoiceTinSearchQuery { TaxpayerName = "Demo" });

        Assert.False(check.IsValid);
        Assert.False(search.Succeeded);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task Each_company_runs_the_tin_tools_with_its_own_credentials()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync(EInvoiceTestHost.Company);
        await host.SeedCompanyAsync("OTHER");
        var service = host.CreateService();

        host.SwitchCompany(EInvoiceTestHost.Company);
        await service.ValidateTinAsync("BRN", "202201234567", "C9876543210");
        Assert.Equal("app-secret-id", host.Secrets.getSecretID());

        // OTHER overrides its credentials in configuration; DEMO must not inherit them and vice versa.
        host.SwitchCompany("OTHER");
        await service.ValidateTinAsync("BRN", "202201234567", "C9876543210");
        Assert.Equal("other-secret-id", host.Secrets.getSecretID());

        host.SwitchCompany(EInvoiceTestHost.Company);
        await service.ValidateTinAsync("BRN", "202201234567", "C9876543210");
        Assert.Equal("app-secret-id", host.Secrets.getSecretID());
    }
}
