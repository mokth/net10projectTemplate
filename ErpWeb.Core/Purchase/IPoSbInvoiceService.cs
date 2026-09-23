namespace ErpWeb.Core.Purchase;

/// <summary>One row of the self-billed invoice list.</summary>
public sealed class PoSbInvoiceListRow
{
    public string DocNo { get; init; } = string.Empty;
    public DateTime DocDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string VendorCode { get; init; } = string.Empty;
    public string? VendorName { get; init; }
    public string? Currency { get; init; }
    public decimal TotAmnt { get; init; }
    public int LineCount { get; init; }

    /// <summary>LHDN e-Invoice status — rendered as both "E-Inv" and "E-Status".</summary>
    public string? IrbmStatus { get; init; }
    public string? IrbmUuid { get; init; }

    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];

    /// <summary>
    /// Structural gates, both derived from the e-Invoice state alone: the ERP NEW/POSTED dimension is
    /// retired, so a document is editable/deletable while it is not locked.
    /// </summary>
    public bool CanEdit { get; init; }
    public bool CanDelete { get; init; }
}

/// <summary>The full document, for the entry screen.</summary>
public sealed class PoSbInvoiceDocument
{
    public string CompanyCode { get; init; } = string.Empty;
    public string BranchCode { get; init; } = string.Empty;
    public string DocNo { get; init; } = string.Empty;
    public DateTime DocDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Prefix { get; init; }
    public string VendorCode { get; init; } = string.Empty;
    public string? VendorName { get; init; }
    public string? Currency { get; init; }
    public decimal CurrRate { get; init; } = 1m;
    public string? TaxGrCode { get; init; }
    public string? Remarks { get; init; }
    public decimal GrossAmnt { get; init; }
    public decimal Taxes { get; init; }
    public decimal TotAmnt { get; init; }

    public string? IrbmStatus { get; init; }
    public string? IrbmUuid { get; init; }
    public string? IrbmError { get; init; }

    public byte[] RowVersion { get; init; } = [];

    public bool CanEdit { get; init; }
    public bool CanDelete { get; init; }


    /// <summary>
    /// True while the e-Invoice status forbids structural edits. The service refuses a save anyway —
    /// this only lets the screen disable the controls.
    /// </summary>
    public bool IsEInvoiceLocked { get; init; }

    public IReadOnlyList<PoSbLineDto> Lines { get; init; } = [];
}

/// <summary>Save request. Amounts are recomputed server-side from the line inputs.</summary>
public sealed class PoSbInvoiceSaveRequest
{
    public DateTime DocDate { get; init; }
    public string VendorCode { get; init; } = string.Empty;
    public string? VendorName { get; init; }
    public string? Currency { get; init; }
    public decimal CurrRate { get; init; } = 1m;
    public string? TaxGrCode { get; init; }
    public string? Remarks { get; init; }
    public IReadOnlyList<PoSbLineRequest> Lines { get; init; } = [];

    /// <summary>The row version the editor loaded. Ignored on create; enforced on update.</summary>
    public byte[]? RowVersion { get; init; }
}

/// <summary>Result of every self-billed invoice operation.</summary>
public sealed class PoSbInvoiceOperationResult
{
    public bool Succeeded { get; init; }
    public PoSbErrorKind ErrorKind { get; init; }
    public string Message { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> ValidationErrors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public PoSbInvoiceDocument? Document { get; init; }
    public PoSbPage<PoSbInvoiceListRow>? List { get; init; }
    public PoSbLookups? Lookups { get; init; }
    public PoSbVendorDefaults? VendorDefaults { get; init; }

    /// <summary>Set by a successful save, so the caller can navigate to the new document.</summary>
    public string? SavedDocNo { get; init; }

    public static PoSbInvoiceOperationResult Ok() =>
        new() { Succeeded = true, ErrorKind = PoSbErrorKind.None };

    public static PoSbInvoiceOperationResult OkSaved(string docNo) =>
        new() { Succeeded = true, ErrorKind = PoSbErrorKind.None, SavedDocNo = docNo };

    public static PoSbInvoiceOperationResult OkDocument(PoSbInvoiceDocument document) =>
        new() { Succeeded = true, ErrorKind = PoSbErrorKind.None, Document = document };

    public static PoSbInvoiceOperationResult OkList(PoSbPage<PoSbInvoiceListRow> page) =>
        new() { Succeeded = true, ErrorKind = PoSbErrorKind.None, List = page };

    public static PoSbInvoiceOperationResult OkLookups(PoSbLookups lookups) =>
        new() { Succeeded = true, ErrorKind = PoSbErrorKind.None, Lookups = lookups };

    public static PoSbInvoiceOperationResult OkVendorDefaults(PoSbVendorDefaults defaults) =>
        new() { Succeeded = true, ErrorKind = PoSbErrorKind.None, VendorDefaults = defaults };

    public static PoSbInvoiceOperationResult Fail(
        string message,
        PoSbErrorKind kind = PoSbErrorKind.Validation) =>
        new() { Succeeded = false, ErrorKind = kind, Message = message };

    public static PoSbInvoiceOperationResult FailValidation(
        string message,
        IReadOnlyDictionary<string, string>? errors = null) =>
        new()
        {
            Succeeded = false,
            ErrorKind = PoSbErrorKind.Validation,
            Message = message,
            ValidationErrors = errors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
}

/// <summary>Self-billed purchase invoice (LHDN type 11).</summary>
public interface IPoSbInvoiceService
{
    Task<PoSbInvoiceOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default);

    Task<PoSbInvoiceOperationResult> GetVendorDefaultsAsync(
        string vendorCode, CancellationToken cancellationToken = default);

    Task<PoSbInvoiceOperationResult> SearchAsync(
        PoSbQuery query, CancellationToken cancellationToken = default);

    Task<PoSbInvoiceOperationResult> GetAsync(
        string docNo, CancellationToken cancellationToken = default);

    Task<PoSbInvoiceOperationResult> SaveNewAsync(
        PoSbInvoiceSaveRequest request, CancellationToken cancellationToken = default);

    Task<PoSbInvoiceOperationResult> UpdateAsync(
        string docNo, PoSbInvoiceSaveRequest request, CancellationToken cancellationToken = default);

    Task<PoSbInvoiceOperationResult> DeleteAsync(
        IReadOnlyList<PoSbKeyedRequest> items, CancellationToken cancellationToken = default);

    /// <summary>UI-side mirror of this service's own authorization check.</summary>
    Task<bool> CanAsync(string permissionCode, CancellationToken cancellationToken = default);
}
