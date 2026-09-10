using ErpWeb.Model.Data;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Compatibility facade only. Document relationships and qty projections are owned by
/// <see cref="ISaDocApplication"/>. Do not call this type from new posting paths.
/// </summary>
[Obsolete("Use ISaDocApplication.Allocate*/Reverse* instead of SaSoFulfillment.")]
public sealed class SaSoFulfillment : ISaSoFulfillment
{
    public Task<SaSoFulfillmentResult> ConsumeAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        IReadOnlyList<SaSoFulfillmentLine> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return Task.FromResult(SaSoFulfillmentResult.Fail(
            SaDocAllocationReasonCodes.NotFound,
            "SaSoFulfillment is retired. Use ISaDocApplication.Allocate* instead."));
    }

    public Task<SaSoFulfillmentResult> ReverseAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        IReadOnlyList<SaSoFulfillmentLine> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return Task.FromResult(SaSoFulfillmentResult.Fail(
            SaDocAllocationReasonCodes.NotFound,
            "SaSoFulfillment is retired. Use ISaDocApplication.ReverseDocumentAllocations instead."));
    }
}
