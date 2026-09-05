namespace ErpWeb.Model.Entities.Sales;

public class SaCountry
{
    public string CountryCode { get; set; } = string.Empty;
    public string? CountryName { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
}
