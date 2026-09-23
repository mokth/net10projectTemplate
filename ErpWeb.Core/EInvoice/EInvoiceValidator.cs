using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;

namespace ErpWeb.Core.EInvoice;

/// <summary>
/// ERP-side pre-submit validation. Runs BEFORE any document generation or MyInvois call, so a bad
/// address or a missing classification code is reported as an ERP validation error instead of an
/// opaque MyInvois rejection.
/// <para>
/// This deliberately duplicates a few checks the generator also performs (it throws on a blank
/// classification code, for example). That is intentional: the generator's messages are terse and
/// arrive as a "generation failed" transport error, whereas these are field-keyed and actionable.
/// </para>
/// </summary>
public sealed class EInvoiceValidator
{
    /// <summary>Money tolerance when cross-checking header totals against the sum of the lines.</summary>
    public const decimal AmountTolerance = 0.05m;

    public EInvoiceValidationReport Validate(
        EInvoiceSourceDocument document,
        EInvoiceSupplierProfile supplier)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The company-level enablement gate always runs: without a company e-Invoice profile there is no
        // submission identity at all, for either direction.
        if (!supplier.Enabled)
        {
            errors["Supplier"] =
                "LHDN e-Invoice is not enabled for this company. Enable it on the company e-Invoice profile.";
        }

        if (document.SupplierParty is { } vendor)
        {
            // Self-billed (LHDN 11/12/13). The issuer is the buyer, so the parties are reversed: the
            // payload's Supplier block is the VENDOR and its Buyer block is OUR COMPANY. The error keys
            // follow the PAYLOAD, not the master: Supplier.* = the vendor, Buyer.* = the company.
            ValidateVendorAsSupplier(vendor, errors);
            ValidateCompanyAsBuyer(document, errors);
        }
        else
        {
            ValidateSupplier(supplier, errors);
            ValidateBuyer(document, errors);
        }

        ValidateHeader(document, errors);
        ValidateLines(document, errors);
        ValidateNoteOrigin(document, errors);
        ValidateAmounts(document, errors);

        return errors.Count == 0 ? EInvoiceValidationReport.Valid : EInvoiceValidationReport.From(errors);
    }

    private static void ValidateSupplier(EInvoiceSupplierProfile supplier, Dictionary<string, string> errors)
    {
        Require(errors, "Supplier.Name", supplier.CompanyName, "Supplier company name is required.");
        Require(errors, "Supplier.Tin", supplier.TinNo, "Supplier TIN is required.");
        Require(errors, "Supplier.RegNo", supplier.RegistrationNo, "Supplier registration number is required.");
        Require(errors, "Supplier.Msic", supplier.MsicCode, "Supplier MSIC code is required.");
        Require(errors, "Supplier.BizDescription", supplier.BusinessDescription,
            "Supplier business description is required.");
        Require(errors, "Supplier.Addr1", supplier.Addr1, "Supplier address line 1 is required.");
        Require(errors, "Supplier.City", supplier.City, "Supplier city is required.");
        Require(errors, "Supplier.PostalCode", supplier.PostalCode, "Supplier postal code is required.");
        Require(errors, "Supplier.Phone", supplier.Phone, "Supplier phone number is required.");
        RequireE164(errors, "Supplier.Phone", supplier.Phone, "Supplier telephone",
            "Correct it on the company profile.");

        if (!string.IsNullOrWhiteSpace(supplier.CompanyName) && supplier.CompanyName!.Length > 300)
        {
            errors["Supplier.Name"] = "Supplier company name cannot exceed 300 characters.";
        }

        if (LhdnCodeLookup.TryRegistrationType(supplier.RegType) is null)
        {
            errors["Supplier.RegType"] =
                "Supplier registration type must be one of NRIC, PASSPORT, BRN or ARMY.";
        }

        if (LhdnCodeLookup.TryStateCode(supplier.State) is null)
        {
            errors["Supplier.State"] =
                $"Supplier state '{supplier.State}' is not a recognised Malaysian state (or LHDN state code).";
        }

        if (LhdnCodeLookup.TryCountryCode(supplier.Country) is null)
        {
            errors["Supplier.Country"] =
                $"Supplier country '{supplier.Country}' is not a recognised LHDN country code.";
        }
    }

    /// <summary>
    /// The self-billed SUPPLIER rule set, applied to the VENDOR master (which becomes the payload's
    /// <c>AccountingSupplierParty</c>). Field keys stay <c>Supplier.*</c> because they name the payload
    /// block; every message says to fix it on the vendor master.
    /// </summary>
    private static void ValidateVendorAsSupplier(
        PoSupplierPartyProfile vendor, Dictionary<string, string> errors)
    {
        const string fix = " Fix it on the vendor master.";

        Require(errors, "Supplier.Name", vendor.Name, "Vendor name is required for a self-billed e-Invoice." + fix);
        Require(errors, "Supplier.Tin", vendor.Tin, "Vendor TIN is required for a self-billed e-Invoice." + fix);
        Require(errors, "Supplier.RegNo", vendor.RegNo,
            "Vendor registration/identity number is required for a self-billed e-Invoice." + fix);
        Require(errors, "Supplier.Msic", vendor.MsicCode,
            "Vendor MSIC code is required for a self-billed e-Invoice." + fix);
        Require(errors, "Supplier.BizDescription", vendor.BusinessDescription,
            "Vendor business description is required for a self-billed e-Invoice." + fix);

        // Format/length only, and the code stays a STRING: an MSIC is 5 digits and may start with zero
        // (01234 must never be parsed into 1234).
        if (!string.IsNullOrWhiteSpace(vendor.MsicCode) && !IsFiveDigitMsic(vendor.MsicCode!))
        {
            errors["Supplier.Msic"] =
                $"Vendor MSIC code '{vendor.MsicCode}' must be 5 digits." + fix;
        }

        // The column is nvarchar(200); the LHDN maximum of 300 is deliberately not adopted (that would
        // need a schema change) so the stored value and the payload agree at 200.
        if (vendor.BusinessDescription?.Length > 200)
        {
            errors["Supplier.BizDescription"] =
                "Vendor business description cannot exceed 200 characters." + fix;
        }
        Require(errors, "Supplier.Addr1", vendor.Address1, "Vendor address line 1 is required." + fix);
        Require(errors, "Supplier.City", vendor.City, "Vendor city is required." + fix);
        Require(errors, "Supplier.PostalCode", vendor.PostalCode, "Vendor postal code is required." + fix);
        Require(errors, "Supplier.Phone", vendor.Phone, "Vendor phone number is required." + fix);
        RequireE164(errors, "Supplier.Phone", vendor.Phone, "Vendor telephone", fix.Trim());

        if (!string.IsNullOrWhiteSpace(vendor.Name) && vendor.Name!.Length > 300)
        {
            errors["Supplier.Name"] = "Vendor name cannot exceed 300 characters.";
        }

        if (LhdnCodeLookup.TryRegistrationType(vendor.RegType) is null)
        {
            errors["Supplier.RegType"] =
                "Vendor registration type must be one of BRN, NRIC, PASSPORT or ARMY." + fix;
        }

        if (LhdnCodeLookup.TryStateCode(vendor.State) is null)
        {
            errors["Supplier.State"] =
                $"Vendor state '{vendor.State}' is not a recognised Malaysian state (or LHDN state code)." + fix;
        }

        if (LhdnCodeLookup.TryCountryCode(vendor.Country) is null)
        {
            errors["Supplier.Country"] =
                $"Vendor country '{vendor.Country}' is not a recognised LHDN country code." + fix;
        }
    }

    /// <summary>
    /// The self-billed BUYER rule set, applied to OUR COMPANY (the issuer, which is the buyer of a
    /// self-billed document). MSIC and business description are NOT required here — they belong to the
    /// supplier block. The values are read from the source document, which the builder filled from the
    /// company profile, so a row the builder failed to copy cannot be masked by a second profile read.
    /// </summary>
    private static void ValidateCompanyAsBuyer(
        EInvoiceSourceDocument document, Dictionary<string, string> errors)
    {
        const string fix = " Fix it on the company e-Invoice profile.";

        Require(errors, "Buyer.Name", document.CustomerName,
            "The company name is required for a self-billed e-Invoice." + fix);
        Require(errors, "Buyer.Tin", document.CustomerTin,
            "The company TIN is required for a self-billed e-Invoice." + fix);
        Require(errors, "Buyer.RegNo", document.CustomerRegNo,
            "The company registration number is required for a self-billed e-Invoice." + fix);
        Require(errors, "Buyer.Addr1", document.CustomerAddr1,
            "The company address line 1 is required." + fix);
        Require(errors, "Buyer.City", document.CustomerCity, "The company city is required." + fix);
        Require(errors, "Buyer.PostalCode", document.CustomerPostalCode,
            "The company postal code is required." + fix);
        Require(errors, "Buyer.Phone", document.CustomerPhone,
            "The company phone number is required." + fix);
        RequireE164(errors, "Buyer.Phone", document.CustomerPhone, "Company telephone", fix.Trim());

        if (LhdnCodeLookup.TryRegistrationType(document.CustomerRegType) is null)
        {
            errors["Buyer.RegType"] =
                "The company registration type must be one of BRN, NRIC, PASSPORT or ARMY." + fix;
        }

        if (LhdnCodeLookup.TryStateCode(document.CustomerState) is null)
        {
            errors["Buyer.State"] =
                $"The company state '{document.CustomerState}' is not a recognised Malaysian state (or LHDN state code)." + fix;
        }

        if (LhdnCodeLookup.TryCountryCode(document.CustomerCountry) is null)
        {
            errors["Buyer.Country"] =
                $"The company country '{document.CustomerCountry}' is not a recognised LHDN country code." + fix;
        }
    }

    private static void ValidateBuyer(EInvoiceSourceDocument document, Dictionary<string, string> errors)
    {
        Require(errors, "Buyer.Name", document.CustomerName, "Customer name is required for e-Invoice.");
        Require(errors, "Buyer.Tin", document.CustomerTin, "Customer TIN is required for e-Invoice.");
        Require(errors, "Buyer.RegNo", document.CustomerRegNo,
            "Customer registration/identity number is required for e-Invoice.");
        Require(errors, "Buyer.Addr1", document.CustomerAddr1, "Customer address line 1 is required.");
        Require(errors, "Buyer.City", document.CustomerCity, "Customer city is required.");
        Require(errors, "Buyer.PostalCode", document.CustomerPostalCode, "Customer postal code is required.");
        Require(errors, "Buyer.Phone", document.CustomerPhone, "Customer phone number is required.");
        // The buyer block is read live from the customer master on every submit, so correcting the profile
        // is all that is needed - there is no "re-open and re-save the document" step any more.
        RequireE164(errors, "Buyer.Phone", document.CustomerPhone, "Customer telephone",
            "Correct it on the customer profile, then submit again.");

        if (LhdnCodeLookup.TryRegistrationType(document.CustomerRegType) is null)
        {
            errors["Buyer.RegType"] =
                "Customer registration type must be one of BRN, NRIC, PASSPORT or ARMY (correct it on the customer profile).";
        }

        if (LhdnCodeLookup.TryStateCode(document.CustomerState) is null)
        {
            errors["Buyer.State"] =
                $"Customer state '{document.CustomerState}' is not a recognised Malaysian state (or LHDN state code).";
        }

        if (LhdnCodeLookup.TryCountryCode(document.CustomerCountry) is null)
        {
            errors["Buyer.Country"] =
                $"Customer country '{document.CustomerCountry}' is not a recognised LHDN country code.";
        }
    }

    private static void ValidateHeader(EInvoiceSourceDocument document, Dictionary<string, string> errors)
    {
        if (document.DocumentDate == default)
        {
            errors["DocumentDate"] = "Document date is required for e-Invoice.";
        }
        else if (document.DocumentDate.Date > DateTime.UtcNow.Date.AddDays(1))
        {
            errors["DocumentDate"] = "Document date cannot be in the future.";
        }

        if (string.IsNullOrWhiteSpace(document.Currency))
        {
            errors["Currency"] = "Currency code is required for e-Invoice.";
        }
        else if (!string.Equals(document.Currency.Trim(), "MYR", StringComparison.OrdinalIgnoreCase)
                 && document.CurrRate <= 0m)
        {
            errors["Currency"] = "A positive exchange rate is required for a non-MYR currency.";
        }
    }

    private static void ValidateLines(EInvoiceSourceDocument document, Dictionary<string, string> errors)
    {
        if (document.Lines.Count == 0)
        {
            errors["Lines"] = "At least one line is required before an e-Invoice can be submitted.";
            return;
        }

        foreach (var line in document.Lines)
        {
            var key = $"Line{line.Line}";

            if (line.Qty <= 0m)
            {
                errors[key + ".Qty"] = "Quantity must be greater than zero.";
            }

            if (string.IsNullOrWhiteSpace(line.ClassificationCode))
            {
                errors[key + ".Classification"] =
                    $"Item classification code is required (item {line.ItemCode ?? line.Line.ToString()}).";
            }

            if (string.IsNullOrWhiteSpace(line.Uom))
            {
                errors[key + ".Uom"] = $"Unit of measure is required (item {line.ItemCode ?? line.Line.ToString()}).";
            }

            // A blank line tax type is deliberately NOT an error. It is resolved to the LHDN default
            // (06, "Not Applicable") by SaEInvoiceService.ResolveTaxType - for a blank code, an unknown
            // tax group, or a tax group whose TaxType is blank - and again by EInvoiceDocumentMapper, so
            // the payload always carries a code. Refusing it here aborted the whole submission before the
            // payload was generated, which blocked any document whose item tax was left empty.

            if (line.TaxPercent is null or < 0)
            {
                errors[key + ".TaxPercent"] = "Tax percentage cannot be negative.";
            }

            if (line.AmountExclTax < 0m)
            {
                errors[key + ".Amount"] = "Line amount excluding tax cannot be negative.";
            }

            if (line.DiscountAmount < -AmountTolerance)
            {
                errors[key + ".Discount"] =
                    "Line discount is negative: the net amount exceeds the gross amount.";
            }
        }
    }

    private static void ValidateNoteOrigin(EInvoiceSourceDocument document, Dictionary<string, string> errors)
    {
        // Only a credit/debit note references an origin. An invoice never does — including the
        // self-billed invoice (LHDN 11), which is issued by the buyer and has no BillingReference.
        if (!EInvoiceDocumentTypes.IsNote(document.DocumentType))
        {
            return;
        }

        Require(errors, "OriginInvoice.No", document.RefDocumentNo,
            "The original invoice number is required for a credit/debit note.");

        Require(errors, "OriginInvoice.Uuid", document.OriginUuid,
            "The original invoice must have a valid MyInvois UUID before a credit/debit note can be submitted.");
    }

    private static void ValidateAmounts(EInvoiceSourceDocument document, Dictionary<string, string> errors)
    {
        if (document.Lines.Count == 0)
        {
            return;
        }

        var expectedExclTax = document.Lines.Sum(x => x.AmountExclTax);
        var expectedTax = document.Lines.Sum(x => x.TaxAmount);

        if (Math.Abs(expectedExclTax - document.AmountExclTax) > AmountTolerance)
        {
            errors["Amounts.ExclTax"] =
                $"Document amount excluding tax ({document.AmountExclTax:0.00}) does not match the sum of its lines ({expectedExclTax:0.00}).";
        }

        if (Math.Abs(expectedTax - document.TaxAmount) > AmountTolerance)
        {
            errors["Amounts.Tax"] =
                $"Document tax amount ({document.TaxAmount:0.00}) does not match the sum of its lines ({expectedTax:0.00}).";
        }

        if (Math.Abs((document.AmountExclTax + document.TaxAmount) - document.AmountIncTax) > AmountTolerance)
        {
            errors["Amounts.IncTax"] =
                $"Document total ({document.AmountIncTax:0.00}) does not equal amount excluding tax plus tax ({document.AmountExclTax + document.TaxAmount:0.00}).";
        }
    }

    private static void Require(
        Dictionary<string, string> errors,
        string key,
        string? value,
        string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[key] = message;
        }
    }

    /// <summary>
    /// True when the value is exactly five digits. The MSIC is a CODE, not a number: it is compared as
    /// text so a leading zero survives (<c>01234</c> must never become <c>1234</c>).
    /// </summary>
    private static bool IsFiveDigitMsic(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length != 5)
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A supplied telephone number must be E.164. Blank is <see cref="Require"/>'s business, so a missing
    /// number and a malformed number never produce two competing messages for the same field.
    /// </summary>
    /// <remarks>
    /// "Supplied" deliberately includes an ERP placeholder such as <c>NA</c>, <c>-</c> or <c>0</c>: those
    /// are not blank, so <see cref="Require"/> passes them, yet they carry no number at all.
    /// </remarks>
    /// <param name="fieldLabel">How the field is named in the message.</param>
    /// <param name="hint">Appended when malformed, to say where the value has to be corrected.</param>
    private static void RequireE164(
        Dictionary<string, string> errors,
        string key,
        string? value,
        string fieldLabel,
        string hint)
    {
        if (PhoneNumberFormat.Validate(value, fieldLabel) is { } message)
        {
            errors[key] = $"{message} {hint}";
        }
        else if (!string.IsNullOrWhiteSpace(value) && PhoneNumberFormat.Normalize(value) is null)
        {
            // Neither blank (that is Require's business) nor malformed, yet still meaningless: an ERP
            // placeholder such as "NA", "-" or "0". Require lets it through because it is not blank, and
            // Validate accepts it as "no value supplied", so without this branch the placeholder would
            // reach the payload verbatim and be refused by MyInvois with an opaque error instead of being
            // stopped here with a field-keyed one.
            errors[key] = $"{fieldLabel} '{value.Trim()}' is a placeholder, not a telephone number. {hint}";
        }
    }
}
