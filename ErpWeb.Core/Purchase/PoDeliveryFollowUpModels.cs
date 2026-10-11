namespace ErpWeb.Core.Purchase;

public static class PoDeliveryFollowUpOutcomes
{
    public const string NoResponse = "No Response";
    public const string Confirmed = "Confirmed";
    public const string Delayed = "Delayed";
    public const string Resolved = "Resolved";

    public static IReadOnlyList<string> All { get; } = [NoResponse, Confirmed, Delayed, Resolved];
}

public sealed class PoDeliveryFollowUpRequest
{
    public string PoNo { get; set; } = string.Empty;
    public short PoRelNo { get; set; }
    public short PoLineNo { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public DateOnly? SupplierStatedDate { get; set; }
    public DateOnly? NextFollowUpDate { get; set; }
    public string? Note { get; set; }
}

public sealed class PoDeliveryFollowUpRow
{
    public long Id { get; set; }
    public DateTime ContactAtUtc { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public DateOnly? SupplierStatedDate { get; set; }
    public DateOnly? NextFollowUpDate { get; set; }
    public string? Note { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
