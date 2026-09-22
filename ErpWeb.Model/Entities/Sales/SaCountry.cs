namespace ErpWeb.Model.Entities.Sales;

public class SaCountry
{
    public string CountryCode { get; set; } = string.Empty;
    public string? CountryName { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}
