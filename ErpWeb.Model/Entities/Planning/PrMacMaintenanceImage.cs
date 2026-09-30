namespace ErpWeb.Model.Entities.Planning;

/// <summary>Machine maintenance image (<c>dbo.PrMacMaintenanceImages</c>).</summary>
public class PrMacMaintenanceImage
{
    public int Uid { get; set; }
    public string? RefCode { get; set; }
    public string? ImageUrl { get; set; }
    public string? Filename { get; set; }
}
