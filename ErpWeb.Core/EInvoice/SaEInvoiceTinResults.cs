namespace ErpWeb.Core.EInvoice;

/// <summary>Outcome of a MyInvois taxpayer (TIN) validation.</summary>
public sealed class SaEInvoiceTinCheckResult
{
    public bool Succeeded { get; init; }

    /// <summary>True only when MyInvois confirmed the TIN/ID pair.</summary>
    public bool IsValid { get; init; }

    public SaEInvoiceErrorKind ErrorKind { get; init; }
    public string? ErrorMessage { get; init; }

    public string Tin { get; init; } = string.Empty;
    public string IdType { get; init; } = string.Empty;
    public string IdValue { get; init; } = string.Empty;

    public static SaEInvoiceTinCheckResult Ok(string tin, string idType, string idValue) => new()
    {
        Succeeded = true,
        IsValid = true,
        ErrorKind = SaEInvoiceErrorKind.None,
        Tin = tin,
        IdType = idType,
        IdValue = idValue
    };

    public static SaEInvoiceTinCheckResult Invalid(string tin, string idType, string idValue, string? message) => new()
    {
        Succeeded = false,
        IsValid = false,
        ErrorKind = SaEInvoiceErrorKind.MyInvois,
        ErrorMessage = message ?? "MyInvois did not recognise this TIN and identity pair.",
        Tin = tin,
        IdType = idType,
        IdValue = idValue
    };

    public static SaEInvoiceTinCheckResult Fail(string message, SaEInvoiceErrorKind kind) => new()
    {
        Succeeded = false,
        IsValid = false,
        ErrorKind = kind,
        ErrorMessage = message
    };
}

/// <summary>One taxpayer returned by the MyInvois TIN search.</summary>
public sealed class SaEInvoiceTinSearchRow
{
    public string Tin { get; init; } = string.Empty;
}

/// <summary>Outcome of a TIN search: the matches, or the reason there are none.</summary>
public sealed class SaEInvoiceTinSearchResult
{
    public bool Succeeded { get; init; }
    public SaEInvoiceErrorKind ErrorKind { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<SaEInvoiceTinSearchRow> Rows { get; init; } = [];

    public static SaEInvoiceTinSearchResult Ok(IReadOnlyList<SaEInvoiceTinSearchRow> rows) =>
        new() { Succeeded = true, ErrorKind = SaEInvoiceErrorKind.None, Rows = rows };

    public static SaEInvoiceTinSearchResult Fail(string message, SaEInvoiceErrorKind kind = SaEInvoiceErrorKind.MyInvois) =>
        new() { Succeeded = false, ErrorKind = kind, ErrorMessage = message };
}

/// <summary>
/// Filters for the TIN search screen. MyInvois requires at least one filter, so the caller must
/// supply one; the repository reports the same rule if none arrives.
/// </summary>
public sealed class SaEInvoiceTinSearchQuery
{
    public string? TaxpayerName { get; init; }
    public string? IdType { get; init; }
    public string? IdValue { get; init; }
}
