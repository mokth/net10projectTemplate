namespace ErpWeb.Model.Entities.Sales;

public class IvMsCode
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string CodeType { get; set; } = string.Empty;
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
}

public static class IvMsCodeTypes
{
    public const string State = "STATE";
    public const string Tax = "TAX";
    public const string PayCode = "PAYCODE";
    public const string Industry = "INDUSTRY";
    public const string Channel = "CHANNEL";
}
