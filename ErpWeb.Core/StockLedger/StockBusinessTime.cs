using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public static class StockBusinessTime
{
    public static async Task<DateTime> NowAsync(
        AppDbContext db, string companyCode, CancellationToken cancellationToken = default)
    {
        var id = await db.Companies.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode)
            .Select(x => x.TimeZoneId)
            .SingleOrDefaultAsync(cancellationToken);
        var zoneId = string.IsNullOrWhiteSpace(id) ? CurrentDateService.DefaultTimeZoneId : id.Trim();
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(CurrentDateService.DefaultTimeZoneId);
        }
        catch (InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(CurrentDateService.DefaultTimeZoneId);
        }
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone), DateTimeKind.Unspecified);
    }
}
