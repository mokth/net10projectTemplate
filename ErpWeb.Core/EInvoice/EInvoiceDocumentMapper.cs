using ErpWeb.Core.Services;
using ErpWeb.EInvoiceLib.Model.InputData;

namespace ErpWeb.Core.EInvoice;

/// <summary>The library input produced by <see cref="EInvoiceDocumentMapper"/>.</summary>
public sealed class MappedEInvoiceDocument
{
    public DocumentHeader Header { get; init; } = new();
    public EInvoiceValidationReport Report { get; init; } = EInvoiceValidationReport.Valid;

    public bool IsValid => Report.IsValid;
}

/// <summary>
/// Translates an <see cref="EInvoiceSourceDocument"/> into the e-Invoice library's
/// <see cref="DocumentHeader"/>.
/// <para>
/// This is a pure translation: it never talks to the database or to MyInvois. Any value it cannot
/// translate (an unrecognised state or country code, for example) is returned as an ERP validation
/// issue rather than being guessed.
/// </para>
/// <para>
/// Telephone numbers are the one value it rewrites instead of rejecting, because the UBL payload must
/// carry E.164. That is a formatting rule, not inference: a recognisable number is canonicalised for the
/// payload (<c>03-9876 5432</c> becomes <c>+60398765432</c>) and an unrecognisable one is passed through
/// untouched, because refusing it is <see cref="EInvoiceValidator"/>'s job - a submission should fail
/// with a field-keyed ERP message rather than reaching MyInvois.
/// </para>
/// </summary>
public sealed class EInvoiceDocumentMapper
{
    public MappedEInvoiceDocument Map(
        EInvoiceSourceDocument document,
        EInvoiceSupplierProfile supplier,
        string? documentVersion)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var supplierRegType = LhdnCodeLookup.TryRegistrationType(supplier.RegType);
        var supplierState = LhdnCodeLookup.TryStateCode(supplier.State);
        var supplierCountry = LhdnCodeLookup.TryCountryCode(supplier.Country);

        var buyerRegType = LhdnCodeLookup.TryRegistrationType(document.CustomerRegType);
        var buyerState = LhdnCodeLookup.TryStateCode(document.CustomerState);
        var buyerCountry = LhdnCodeLookup.TryCountryCode(document.CustomerCountry);

        if (supplierRegType is null)
        {
            errors["Supplier.RegType"] = "Supplier registration type could not be mapped to an LHDN identity type.";
        }

        if (supplierState is null)
        {
            errors["Supplier.State"] = $"Supplier state '{supplier.State}' could not be mapped to an LHDN state code.";
        }

        if (supplierCountry is null)
        {
            errors["Supplier.Country"] = $"Supplier country '{supplier.Country}' could not be mapped to an LHDN country code.";
        }

        if (buyerRegType is null)
        {
            errors["Buyer.RegType"] = "Customer registration type could not be mapped to an LHDN identity type.";
        }

        if (buyerState is null)
        {
            errors["Buyer.State"] = $"Customer state '{document.CustomerState}' could not be mapped to an LHDN state code.";
        }

        if (buyerCountry is null)
        {
            errors["Buyer.Country"] = $"Customer country '{document.CustomerCountry}' could not be mapped to an LHDN country code.";
        }

        if (errors.Count > 0)
        {
            return new MappedEInvoiceDocument
            {
                Header = new DocumentHeader(),
                Report = EInvoiceValidationReport.From(errors)
            };
        }

        var currency = document.Currency!.Trim().ToUpperInvariant();
        var isHomeCurrency = string.Equals(currency, "MYR", StringComparison.OrdinalIgnoreCase);

        var header = new DocumentHeader
        {
            DocumentVersion = string.IsNullOrWhiteSpace(documentVersion) ? "1.0" : documentVersion!,
            DocumentNo = document.DocumentNo,
            RefDocumentNo = document.RefDocumentNo,
            OriginInvoiceUUID = document.OriginUuid,
            IssueDate = document.DocumentDate.AddMinutes(-15),
            InvoicePeriodStartDate= document.DocumentDate.ToString("yyyy-MM-dd"),
            InvoicePeriodEndDate = document.DocumentDate.ToString("yyyy-MM-dd"),
            docType = MapDocumentType(document.DocumentType),
            Currency = currency,
            ForeignCurrency = currency,
            ExchangeRate = isHomeCurrency ? null : (double)document.CurrRate,

            AmountIncTax = (double)document.AmountIncTax,
            AmountExlTax = (double)document.AmountExclTax,
            TaxAmount = (double)document.TaxAmount,
            TotalPayableAmount = (double)document.AmountIncTax,
            TotalNetAmount = (double)document.AmountExclTax,
            DiscountAmount = (double)document.Lines.Sum(x => x.DiscountAmount),
            AdditionalDiscountAmount = 0d,

            Supplier = new PartyInfo
            {
                CompanyName = supplier.CompanyName,
                TinNo = supplier.TinNo,
                RegType = supplierRegType!.Value,
                RegNo = supplier.RegistrationNo,
                SSTNo = supplier.SstNo,
                IndustryClassificationCode = supplier.MsicCode,
                BizDesciption = supplier.BusinessDescription,
                Addr1 = supplier.Addr1,
                Addr2 = supplier.Addr2,
                Addr3 = supplier.Addr3,
                Addr4 = supplier.Addr4,
                CityName = supplier.City,
                StateCode = supplierState,
                CountryCode = supplierCountry,
                PostalCode = supplier.PostalCode,
                // LHDN expects E.164. Canonicalizing here — not only at master-data save time — is what
                // rescues rows captured before the rule existed and what cleans an operator's
                // transaction-level override. ToStored is trim-preserving, so this is formatting rather
                // than inference: an unrecognisable value is left exactly as the user typed it, and
                // EInvoiceValidator is the gate that refuses it with an actionable message.
                PhoneNo = ToE164OrNull(supplier.Phone),
                Email = supplier.Email
            },
            Customer = new PartyInfo
            {
                CompanyName = document.CustomerName,
                TinNo = document.CustomerTin,
                RegType = buyerRegType!.Value,
                RegNo = document.CustomerRegNo,
                SSTNo = document.CustomerSstNo,
                Addr1 = document.CustomerAddr1,
                Addr2 = document.CustomerAddr2,
                Addr3 = document.CustomerAddr3,
                Addr4 = document.CustomerAddr4,
                CityName = document.CustomerCity,
                StateCode = buyerState,
                CountryCode = buyerCountry,
                PostalCode = document.CustomerPostalCode,
                // This is the snapshot taken onto the document, not the live customer row, so a legacy value
                // is canonicalised here rather than being rejected.
                PhoneNo = ToE164OrNull(document.CustomerPhone),
                Email = document.CustomerEmail
            },
            documentDetails = document.Lines.Select(line => new DocumentDetail
            {
                Line = line.Line.ToString(),
                ItemCode = line.ItemCode,
                ItemDesc = line.ItemDesc,
                Qty = (double)line.Qty,
                // Safety net only: SaEInvoiceService already translates the ERP UOM/tax group into LHDN
                // codes. A blank here means a caller bypassed that resolution (or a hand-built source),
                // so substitute the LHDN default rather than sending an empty code.
                UOM = string.IsNullOrWhiteSpace(line.Uom) ? LhdnDefaults.UneceUom : line.Uom,
                UnitPrice = (double)line.UnitPrice,
                GrossAmount = (double)line.GrossAmount,
                AmountExclTax = (double)line.AmountExclTax,
                AmountIncTax = (double)(line.AmountExclTax + line.TaxAmount),
                DiscountAmount = (double)line.DiscountAmount,
                TaxAmount = (double)line.TaxAmount,
                TaxType = string.IsNullOrWhiteSpace(line.TaxType) ? LhdnDefaults.TaxType : line.TaxType,
                TaxPerCent = line.TaxPercent,
                ClassificationCode = line.ClassificationCode
            }).ToList()
        };

        return new MappedEInvoiceDocument { Header = header };
    }

    /// <summary>
    /// The E.164 form of a telephone number for the payload, or <c>null</c> when the source had no usable
    /// value.
    /// </summary>
    /// <remarks>
    /// This is a fallback for values that predate the format rule or were overridden on a document: the
    /// payload gets the canonical form when it can be derived, and otherwise the original value goes
    /// through unchanged so <see cref="EInvoiceValidator"/> can refuse it with an actionable message.
    /// A blank or placeholder value becomes <c>null</c> rather than whitespace or <c>"NA"</c>, which is what
    /// keeps the library's existing "telephone is blank" guard meaningful.
    /// </remarks>
    private static string? ToE164OrNull(string? phone) => PhoneNumberFormat.ToStored(phone);

    /// <summary>
    /// ERP <c>SaEInvoiceDocumentType</c> code (INV/CN/DN/…, see <c>EInvoiceDocumentTypes</c>) mapped to
    /// the library document type. Public so callers that only have the ERP string (for example the
    /// recovery search) resolve the code through <c>EInvoiceDocumentTypeMap</c> instead of keeping a
    /// second, divergent mapping table.
    /// </summary>
    public static EInvoiceDocumentType MapDocumentType(string documentType) =>
        documentType switch
        {
            EInvoiceDocumentTypes.CreditNote => EInvoiceDocumentType.creditnote,
            EInvoiceDocumentTypes.DebitNote => EInvoiceDocumentType.debitnote,
            // Self-billed families are mapped additively (LHDN 11/12/13) and never fall back to 01.
            EInvoiceDocumentTypes.SelfBilledInvoice => EInvoiceDocumentType.sb_invoice,
            EInvoiceDocumentTypes.SelfBilledCreditNote => EInvoiceDocumentType.sb_creditnote,
            EInvoiceDocumentTypes.SelfBilledDebitNote => EInvoiceDocumentType.sb_debitnote,
            _ => EInvoiceDocumentType.invoice
        };
}
