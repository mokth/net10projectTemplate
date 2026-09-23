namespace ErpWeb.Core.Purchase;

/// <summary>One row of a self-billed credit / debit note list.</summary>
public sealed class PoSbCdnListRow
{
    public string DocNo { get; init; } = string.Empty;
    public DateTime DocDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string VendorCode { get; init; } = string.Empty;
    public string? VendorName { get; init; }
    public string? OriginSbInvNo { get; init; }
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
    /// retired, so a note is editable/deletable while it is not locked.
    /// </summary>
    public bool CanEdit { get; init; }
    public bool CanDelete { get; init; }
}

/// <summary>The full note, for the entry screen.</summary>
public sealed class PoSbCdnDocument
{
    public string CompanyCode { get; init; } = string.Empty;
    public string BranchCode { get; init; } = string.Empty;
    public string DocNo { get; init; } = string.Empty;
    public DateTime DocDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? Prefix { get; init; }
    public string VendorCode { get; init; } = string.Empty;
    public string? VendorName { get; init; }
    public string? OriginSbInvNo { get; init; }
    public string? Currency { get; init; }
    public decimal CurrRate { get; init; } = 1m;
    public string? TaxGrCode { get; init; }
    public string? Remarks { get; init; }
    public decimal GrossAmnt { get; init; }
    public decimal Taxes { get; init; }
    public decimal TotAmnt { get; init; }

    public string? IrbmStatus { get; init; }
    public string? IrbmUuid { get; init; }
    public string? IrbmOriUuid { get; init; }
    public string? IrbmError { get; init; }

    public byte[] RowVersion { get; init; } = [];

    public bool CanEdit { get; init; }
    public bool CanDelete { get; init; }

    /// <summary>True while the e-Invoice status forbids structural edits.</summary>
    public bool IsEInvoiceLocked { get; init; }

    public IReadOnlyList<PoSbLineDto> Lines { get; init; } = [];
}

/// <summary>Save request. Amounts are recomputed server-side from the line inputs.</summary>
public sealed class PoSbCdnSaveRequest
{
    public DateTime DocDate { get; init; }

    /// <summary><c>CN</c> or <c>DN</c>. Required.</summary>
    public string Type { get; init; } = string.Empty;

    public string VendorCode { get; init; } = string.Empty;
    public string? VendorName { get; init; }

    /// <summary>The originating self-billed invoice number (resolved within this company and branch).</summary>
    public string? OriginSbInvNo { get; init; }

    public string? Currency { get; init; }
    public decimal CurrRate { get; init; } = 1m;
    public string? TaxGrCode { get; init; }
    public string? Remarks { get; init; }
    public IReadOnlyList<PoSbLineRequest> Lines { get; init; } = [];

    /// <summary>The row version the editor loaded. Ignored on create; enforced on update.</summary>
    public byte[]? RowVersion { get; init; }
}

/// <summary>Result of every self-billed note operation.</summary>
public sealed class PoSbCdnOperationResult
{
    public bool Succeeded { get; init; }
    public PoSbErrorKind ErrorKind { get; init; }
    public string Message { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> ValidationErrors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public PoSbCdnDocument? Document { get; init; }
    public PoSbPage<PoSbCdnListRow>? List { get; init; }
    public PoSbLookups? Lookups { get; init; }
    public PoSbVendorDefaults? VendorDefaults { get; init; }

    /// <summary>Set by a successful save, so the caller can navigate to the new document.</summary>
    public string? SavedDocNo { get; init; }

    public static PoSbCdnOperationResult Ok() =>
        new() { Succeeded = true, ErrorKind = PoSbErrorKind.None };

    public static PoSbCdnOperationResult OkSaved(string docNo) =>
        new() { Succeeded = true, ErrorKind = PoSbErrorKind.None, SavedDocNo = docNo };

    public static PoSbCdnOperationResult OkDocument(PoSbCdnDocument document) =>
        new() { Succeeded = true, ErrorKind = PoSbErrorKind.None, Document = document };

    public static PoSbCdnOperationResult OkList(PoSbPage<PoSbCdnListRow> page) =>
        new() { Succeeded = true, ErrorKind = PoSbErrorKind.None, List = page };

    public static PoSbCdnOperationResult OkLookups(PoSbLookups lookups) =>
        new() { Succeeded = true, ErrorKind = PoSbErrorKind.None, Lookups = lookups };

    public static PoSbCdnOperationResult OkVendorDefaults(PoSbVendorDefaults defaults) =>
        new() { Succeeded = true, ErrorKind = PoSbErrorKind.None, VendorDefaults = defaults };

    public static PoSbCdnOperationResult Fail(
        string message,
        PoSbErrorKind kind = PoSbErrorKind.Validation) =>
        new() { Succeeded = false, ErrorKind = kind, Message = message };

    public static PoSbCdnOperationResult FailValidation(
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

/// <summary>Self-billed purchase credit / debit note (LHDN types 12 / 13).</summary>
public interface IPoSbCdnService
{
    Task<PoSbCdnOperationResult> GetLookupsAsync(
        string type, CancellationToken cancellationToken = default);

    Task<PoSbCdnOperationResult> GetVendorDefaultsAsync(
        string vendorCode, CancellationToken cancellationToken = default);

    Task<PoSbCdnOperationResult> SearchAsync(
        PoSbQuery query, CancellationToken cancellationToken = default);

    Task<PoSbCdnOperationResult> GetAsync(
        string docNo, CancellationToken cancellationToken = default);

    Task<PoSbCdnOperationResult> SaveNewAsync(
        PoSbCdnSaveRequest request, CancellationToken cancellationToken = default);

    Task<PoSbCdnOperationResult> UpdateAsync(
        string docNo, PoSbCdnSaveRequest request, CancellationToken cancellationToken = default);

    Task<PoSbCdnOperationResult> DeleteAsync(
        IReadOnlyList<PoSbKeyedRequest> items, CancellationToken cancellationToken = default);

    /// <summary>UI-side mirror of this service's own authorization check (CN and DN differ).</summary>
    Task<bool> CanAsync(string type, string permissionCode, CancellationToken cancellationToken = default);
}
