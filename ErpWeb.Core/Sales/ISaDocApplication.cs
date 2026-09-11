using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;

namespace ErpWeb.Core.Sales;

public static class SaDocTypes
{
    public const string So = "SO";
    public const string Do = "DO";
    public const string Inv = "INV";
}

public static class SaDualStatuses
{
    public const string None = "NONE";
    public const string Partial = "PARTIAL";
    public const string Full = "FULL";
    /// <summary>
    /// R3: every remaining unit of the line was either invoiced or written off by a DO force-close.
    /// Terminal for billing — no further invoicing is possible and none is outstanding.
    /// </summary>
    public const string WrittenOff = "WRITTEN_OFF";
}

public static class SaDocAllocationReasonCodes
{
    public const string NotFound = "ALLOC_NOT_FOUND";
    public const string Closed = "ALLOC_CLOSED";
    public const string ForceClosed = "ALLOC_FORCE_CLOSED";
    public const string OverAllocate = "ALLOC_OVER";
    public const string UomMismatch = "ALLOC_UOM_MISMATCH";
    public const string CustomerMismatch = "ALLOC_CUSTOMER_MISMATCH";
    public const string CurrencyMismatch = "ALLOC_CURRENCY_MISMATCH";
    /// <summary>Phase-1 reject retired; blank Related SO is valid for DO_INV. Kept for docs/tests.</summary>
    public const string StandaloneDo = "ALLOC_STANDALONE_DO";
    public const string Duplicate = "ALLOC_DUPLICATE";
    public const string MergeForbidden = "ALLOC_MERGE_FORBIDDEN";
    public const string InvalidQty = "ALLOC_INVALID_QTY";
    public const string MixForbidden = "ALLOC_MIX_FORBIDDEN";
    public const string TooManyHeaders = "ALLOC_TOO_MANY_HEADERS";
    public const string LineageFrozen = "ALLOC_LINEAGE_FROZEN";
}

public sealed class SaDocAllocationLine
{
    public string SourceDocId { get; set; } = string.Empty;
    public short SourceLineId { get; set; }
    public string TargetDocId { get; set; } = string.Empty;
    public short TargetLineId { get; set; }
    public decimal AppliedQty { get; set; }
    public decimal? AppliedAmount { get; set; }
    public string? SellingUom { get; set; }
    public string CustCode { get; set; } = string.Empty;
    public string? Currency { get; set; }

    /// <summary>Set by Allocate* on success — denormalized AppliedQty for SoConsumedQty stamp.</summary>
    public decimal SoConsumedQty { get; set; }
}

public sealed class SaDocAllocationResult
{
    public bool Succeeded { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<SaDocAllocationLine> Lines { get; init; } = [];

    public static SaDocAllocationResult Ok(IReadOnlyList<SaDocAllocationLine>? lines = null) =>
        new() { Succeeded = true, Lines = lines ?? [] };

    public static SaDocAllocationResult Fail(string code, string message) =>
        new()
        {
            Succeeded = false,
            Code = code,
            Message = message
        };
}

public readonly record struct SaDocSoKey(string SoNo, short CustRel, short Line);

/// <summary>SO header/revision identity for allocation existence checks (no line).</summary>
public readonly record struct SaDocSoRevisionKey(string SoNo, short CustRel);

public readonly record struct SaDocDoKey(string DoNo, short Line);

public interface ISaDocApplication
{
    Task<SaDocAllocationResult> AllocateSOToDOAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        string? documentCurrency,
        IReadOnlyList<SaDocAllocationLine> lines,
        CancellationToken cancellationToken = default);

    Task<SaDocAllocationResult> AllocateSOToInvoiceAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        string? documentCurrency,
        IReadOnlyList<SaDocAllocationLine> lines,
        CancellationToken cancellationToken = default);

    Task<SaDocAllocationResult> AllocateDOToInvoiceAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        string? documentCurrency,
        IReadOnlyList<SaDocAllocationLine> lines,
        CancellationToken cancellationToken = default);

    Task<SaDocAllocationResult> ReverseDocumentAllocationsAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        string targetDocType,
        string targetDocId,
        CancellationToken cancellationToken = default);

    Task RecalculateAffectedLinesAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        IReadOnlyCollection<string> soNos,
        IReadOnlyCollection<string> doNos,
        CancellationToken cancellationToken = default);

    Task<bool> HasAllocationForDoLineAsync(
        AppDbContext db,
        string company,
        string branch,
        string doNo,
        short line,
        CancellationToken cancellationToken = default);

    Task<bool> HasAllocationsForSoAsync(
        AppDbContext db,
        string company,
        string branch,
        string soNo,
        short? custRel = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Batched equivalent of <see cref="HasAllocationsForSoAsync"/> for a page of (SoNo, CustRel) keys.
    /// Uses the same SO-source and DO→INV RelatedSoNo/RelatedCustRel predicates.
    /// </summary>
    Task<IReadOnlySet<SaDocSoRevisionKey>> ListAllocatedSoKeysAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyCollection<SaDocSoRevisionKey> keys,
        CancellationToken cancellationToken = default);

    Task<decimal> SumDoInvoicedQtyAsync(
        AppDbContext db,
        string company,
        string branch,
        string doNo,
        short line,
        CancellationToken cancellationToken = default);
}
