using ErpWeb.Model.Data;

namespace ErpWeb.Core.Sales;

public sealed class SaSoFulfillmentLine
{
    public string SoNo { get; set; } = string.Empty;
    public short SoLine { get; set; }
    public decimal Qty { get; set; }
    public decimal SoConsumedQty { get; set; }
    public bool LinkDo { get; set; }
    public string? SellingUom { get; set; }
    public string CustCode { get; set; } = string.Empty;
}

public sealed class SaSoFulfillmentResult
{
    public bool Succeeded { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }

    public static SaSoFulfillmentResult Ok() => new() { Succeeded = true };

    public static SaSoFulfillmentResult Fail(string code, string message) =>
        new()
        {
            Succeeded = false,
            Code = code,
            Message = message
        };
}

public interface ISaSoFulfillment
{
    Task<SaSoFulfillmentResult> ConsumeAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        IReadOnlyList<SaSoFulfillmentLine> lines,
        CancellationToken cancellationToken = default);

    Task<SaSoFulfillmentResult> ReverseAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        IReadOnlyList<SaSoFulfillmentLine> lines,
        CancellationToken cancellationToken = default);
}
