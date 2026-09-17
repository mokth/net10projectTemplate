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

        ValidateSupplier(supplier, errors);
        ValidateBuyer(document, errors);
        ValidateHeader(document, errors);
        ValidateLines(document, errors);
        ValidateNoteOrigin(document, errors);
        ValidateAmounts(document, errors);

        return errors.Count == 0 ? EInvoiceValidationReport.Valid : EInvoiceValidationReport.From(errors);
    }

    private static void ValidateSupplier(EInvoiceSupplierProfile supplier, Dictionary<string, string> errors)
    {
        if (!supplier.Enabled)
        {
            errors["Supplier"] = "LHDN e-Invoice is not enabled for this company. Enable it on the company e-Invoice profile.";
        }

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

        if (LhdnCodeLookup.TryRegistrationType(document.CustomerRegType) is null)
        {
            errors["Buyer.RegType"] =
                "Customer registration type must be one of NRIC, PASSPORT, BRN or ARMY.";
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

            if (string.IsNullOrWhiteSpace(line.TaxType))
            {
                errors[key + ".TaxType"] = $"Tax type is required (item {line.ItemCode ?? line.Line.ToString()}).";
            }

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
        if (document.DocumentType == EInvoiceDocumentTypes.Invoice)
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
}
