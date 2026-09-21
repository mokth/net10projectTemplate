using ErpWeb.Core.EInvoice;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests;

/// <summary>
/// The LHDN code translation the e-Invoice payload depends on: an ERP UOM is sent as its
/// <c>MsUOM.UNECE_UOM</c> code and an ERP tax group as its <c>SaTaxGroup.TaxType</c>, with <c>H87</c>
/// and <c>06</c> as the documented fallbacks.
///
/// <para>
/// Line documents deliberately keep the ERP codes (<c>StdUom</c>, <c>TaxGrCode</c> / <c>TaxGroup</c>) —
/// only the payload is translated — so the invoice and credit-note paths are asserted separately.
/// </para>
/// </summary>
public class SaEInvoiceLhdnCodeResolutionTests
{
    private const string ErpUom = "UNIT";
    private const string ErpTaxGroup = "SR-8";

    // ─────────────────────────────── Invoice path ───────────────────────────────

    [Fact]
    public async Task Invoice_lines_send_the_LHDN_UNECE_unit_and_tax_type_not_the_ERP_codes()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        await SeedUomAsync(host, ErpUom, uneceUom: "C62");
        await SeedTaxGroupAsync(host, ErpTaxGroup, taxType: "01");
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(result.Succeeded, result.ErrorMessage);
        var line = Assert.Single(Assert.Single(host.Helper.Submitted).documentDetails);
        Assert.Equal("C62", line.UOM);
        Assert.Equal("01", line.TaxType);
    }

    [Fact]
    public async Task Invoice_lines_fall_back_to_H87_and_06_when_the_masters_have_no_LHDN_mapping()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        // No MsUOM row for UNIT and no SaTaxGroup row for SR-8 at all.
        await host.SeedInvoiceAsync();
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(result.Succeeded, result.ErrorMessage);
        var line = Assert.Single(Assert.Single(host.Helper.Submitted).documentDetails);
        Assert.Equal(LhdnDefaults.UneceUom, line.UOM);
        Assert.Equal(LhdnDefaults.TaxType, line.TaxType);
    }

    [Fact]
    public async Task Invoice_lines_fall_back_when_the_masters_exist_but_their_mapping_is_blank()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        await SeedUomAsync(host, ErpUom, uneceUom: null);
        await SeedTaxGroupAsync(host, ErpTaxGroup, taxType: null);
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(result.Succeeded, result.ErrorMessage);
        var line = Assert.Single(Assert.Single(host.Helper.Submitted).documentDetails);
        Assert.Equal("H87", line.UOM);
        Assert.Equal("06", line.TaxType);
    }

    [Fact]
    public async Task Invoice_document_keeps_the_ERP_UOM_and_tax_group_on_its_lines()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        await SeedUomAsync(host, ErpUom, uneceUom: "C62");
        await SeedTaxGroupAsync(host, ErpTaxGroup, taxType: "01");
        var service = host.CreateService();

        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        // The translation must not leak back into the ERP document: reporting, inventory and posting
        // still read the ERP codes.
        await using var db = await host.Factory.CreateDbContextAsync();
        var detail = await db.SaInvoiceDetails
            .AsNoTracking()
            .SingleAsync(x => x.CompanyCode == EInvoiceTestHost.Company && x.InvNo == EInvoiceTestHost.InvNo);
        Assert.Equal(ErpUom, detail.StdUom);
        Assert.Equal(ErpTaxGroup, detail.TaxGrCode);
    }

    // ─────────────────────────── Credit / debit note path ───────────────────────────

    [Fact]
    public async Task Credit_note_lines_send_the_LHDN_UNECE_unit_and_tax_type_not_the_ERP_codes()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedCreditNoteAsync();
        await SeedUomAsync(host, ErpUom, uneceUom: "C62");
        await SeedTaxGroupAsync(host, ErpTaxGroup, taxType: "01");
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.CdnKey());

        Assert.True(result.Succeeded, result.ErrorMessage);
        var line = Assert.Single(Assert.Single(host.Helper.Submitted).documentDetails);
        Assert.Equal("C62", line.UOM);
        Assert.Equal("01", line.TaxType);
    }

    [Fact]
    public async Task Credit_note_lines_fall_back_to_H87_and_06_without_a_LHDN_mapping()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedCreditNoteAsync();
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.CdnKey());

        Assert.True(result.Succeeded, result.ErrorMessage);
        var line = Assert.Single(Assert.Single(host.Helper.Submitted).documentDetails);
        Assert.Equal("H87", line.UOM);
        Assert.Equal("06", line.TaxType);
    }

    [Fact]
    public async Task Credit_note_document_keeps_the_ERP_UOM_and_tax_group_on_its_lines()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedCreditNoteAsync();
        await SeedUomAsync(host, ErpUom, uneceUom: "C62");
        await SeedTaxGroupAsync(host, ErpTaxGroup, taxType: "01");
        var service = host.CreateService();

        await service.SubmitAsync(EInvoiceTestHost.CdnKey());

        await using var db = await host.Factory.CreateDbContextAsync();
        var detail = await db.SaCdnDetails
            .AsNoTracking()
            .SingleAsync(x => x.CompanyCode == EInvoiceTestHost.Company && x.DocNo == EInvoiceTestHost.CdnNo);
        Assert.Equal(ErpUom, detail.StdUom);
        Assert.Equal(ErpTaxGroup, detail.TaxGroup);
    }

    // ─────────────────────────── Mapper safety net ───────────────────────────

    [Fact]
    public void Mapper_substitutes_the_LHDN_defaults_for_a_blank_line_code()
    {
        var mapper = new EInvoiceDocumentMapper();

        var mapped = mapper.Map(SourceWithCodes(uom: null, taxType: null), Supplier(), "1.0");

        Assert.True(mapped.IsValid, mapped.Report.Summary());
        var line = Assert.Single(mapped.Header.documentDetails);
        Assert.Equal(LhdnDefaults.UneceUom, line.UOM);
        Assert.Equal(LhdnDefaults.TaxType, line.TaxType);
    }

    [Fact]
    public void Mapper_leaves_resolved_LHDN_codes_untouched()
    {
        var mapper = new EInvoiceDocumentMapper();

        var mapped = mapper.Map(SourceWithCodes(uom: "C62", taxType: "01"), Supplier(), "1.0");

        Assert.True(mapped.IsValid, mapped.Report.Summary());
        var line = Assert.Single(mapped.Header.documentDetails);
        Assert.Equal("C62", line.UOM);
        Assert.Equal("01", line.TaxType);
    }

    // ────────────────────── Telephone canonicalisation ──────────────────────

    [Theory]
    [InlineData("0312345678", "+60312345678")]
    [InlineData("03-9876 5432", "+60398765432")]
    [InlineData("  0398765432  ", "+60398765432")]
    [InlineData("+60 3-9876 5432", "+60398765432")]
    public void Mapper_canonicalises_the_supplier_telephone_to_E164(string raw, string expected)
    {
        var mapper = new EInvoiceDocumentMapper();

        var mapped = mapper.Map(SourceWithCodes(uom: "C62", taxType: "01"), Supplier(raw), "1.0");

        Assert.True(mapped.IsValid, mapped.Report.Summary());
        Assert.Equal(expected, mapped.Header.Supplier.PhoneNo);
    }

    [Theory]
    [InlineData("0398765432", "+60398765432")]
    [InlineData("  0398765432  ", "+60398765432")]
    [InlineData("+60 3-9876 5432", "+60398765432")]
    public void Mapper_canonicalises_the_buyer_telephone_to_E164(string raw, string expected)
    {
        var mapper = new EInvoiceDocumentMapper();

        var mapped = mapper.Map(
            SourceWithCodes(uom: "C62", taxType: "01", customerPhone: raw), Supplier(), "1.0");

        Assert.True(mapped.IsValid, mapped.Report.Summary());
        Assert.Equal(expected, mapped.Header.Customer.PhoneNo);
    }

    [Fact]
    public void Mapper_passes_an_unusable_telephone_through_for_the_validator_to_refuse()
    {
        var mapper = new EInvoiceDocumentMapper();

        var mapped = mapper.Map(SourceWithCodes(uom: "C62", taxType: "01"), Supplier("A-phone"), "1.0");

        // Fail-soft by design: refusing the value is the validator's job, so the user only ever sees the
        // validator's field-keyed message and never a second, differently-worded one from here.
        Assert.Equal("A-phone", mapped.Header.Supplier.PhoneNo);
    }

    [Fact]
    public void Mapper_leaves_a_blank_telephone_blank_so_the_library_guard_still_fires()
    {
        var mapper = new EInvoiceDocumentMapper();

        var mapped = mapper.Map(
            SourceWithCodes(uom: "C62", taxType: "01", customerPhone: "   "), Supplier(null), "1.0");

        Assert.Null(mapped.Header.Supplier.PhoneNo);
        Assert.Null(mapped.Header.Customer.PhoneNo);
    }

    // ─────────────────────────────── Helpers ───────────────────────────────

    private static async Task SeedUomAsync(EInvoiceTestHost host, string uomCode, string? uneceUom)
    {
        await using var db = await host.Factory.CreateDbContextAsync();
        db.MsUoms.Add(new MsUom
        {
            CompanyCode = EInvoiceTestHost.Company,
            UomCode = uomCode,
            UomDesc = uomCode,
            UneceUom = uneceUom,
            IsActive = true,
            RowVersion = [1]
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedTaxGroupAsync(EInvoiceTestHost host, string taxGrCode, string? taxType)
    {
        await using var db = await host.Factory.CreateDbContextAsync();
        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = EInvoiceTestHost.Company,
            TaxGrCode = taxGrCode,
            TaxGrDesc = taxGrCode,
            Percentage = 8m,
            TaxType = taxType
        });
        await db.SaveChangesAsync();
    }

    private static EInvoiceSourceDocument SourceWithCodes(
        string? uom,
        string? taxType,
        string? customerPhone = null) => new()
    {
        DocumentType = EInvoiceDocumentTypes.Invoice,
        DocumentNo = "INV-1",
        DocumentDate = new DateTime(2026, 9, 21),
        Currency = "MYR",
        AmountExclTax = 100m,
        TaxAmount = 8m,
        AmountIncTax = 108m,
        CustomerName = "Buyer Sdn Bhd",
        CustomerTin = "C9876543210",
        CustomerRegNo = "202201234567",
        CustomerRegType = "BRN",
        CustomerAddr1 = "2 Jalan Buyer",
        CustomerCity = "Petaling Jaya",
        CustomerState = "Selangor",
        CustomerPostalCode = "47300",
        CustomerCountry = "Malaysia",
        CustomerPhone = customerPhone,
        Lines =
        [
            new EInvoiceSourceLine
            {
                Line = 1,
                ItemCode = "ITM01",
                ItemDesc = "Consulting",
                Uom = uom,
                Qty = 1m,
                UnitPrice = 100m,
                GrossAmount = 100m,
                AmountExclTax = 100m,
                TaxAmount = 8m,
                TaxType = taxType,
                TaxPercent = 8d,
                ClassificationCode = "022"
            }
        ]
    };

    private static EInvoiceSupplierProfile Supplier(string? phone = "0312345678") => new()
    {
        CompanyCode = EInvoiceTestHost.Company,
        Enabled = true,
        CompanyName = "Demo Sdn Bhd",
        TinNo = "C1234567890",
        RegistrationNo = "202301234567",
        RegType = "BRN",
        MsicCode = "62010",
        BusinessDescription = "Software development",
        Addr1 = "1 Jalan Demo",
        City = "Shah Alam",
        State = "Selangor",
        PostalCode = "40100",
        Country = "Malaysia",
        Phone = phone,
        Email = "billing@demo.test"
    };
}
