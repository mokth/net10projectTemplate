using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Services;

public interface ICurrentDateService
{
    DateTime Today { get; }
    DateTime Now { get; }
}

/// <summary>
/// Application Today/Now in the current company's IANA timezone (Company.TimeZoneId).
/// Fallback: Asia/Kuala_Lumpur. Never uses DateTime.Today or UtcNow.Date blindly.
/// </summary>
public sealed class CurrentDateService : ICurrentDateService
{
    public const string DefaultTimeZoneId = "Asia/Kuala_Lumpur";

    private readonly ICurrentUserService _currentUser;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public CurrentDateService(
        ICurrentUserService currentUser,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _currentUser = currentUser;
        _dbFactory = dbFactory;
    }

    public DateTime Today => GetCompanyLocalNow().Date;

    public DateTime Now => GetCompanyLocalNow();

    private DateTime GetCompanyLocalNow()
    {
        var zone = ResolveTimeZone();
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
    }

    private TimeZoneInfo ResolveTimeZone()
    {
        var timeZoneId = ResolveTimeZoneId();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZoneId);
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZoneId);
        }
    }

    private string ResolveTimeZoneId()
    {
        var companyCode = _currentUser.CompanyCode?.Trim();
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            return DefaultTimeZoneId;
        }

        // Sync read via factory is acceptable for clock; prefer cached company later if needed.
        using var db = _dbFactory.CreateDbContext();
        var id = db.Companies
            .AsNoTracking()
            .Where(x => x.CompanyCode == companyCode)
            .Select(x => x.TimeZoneId)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(id) ? DefaultTimeZoneId : id.Trim();
    }
}

/// <summary>Fixed clock for unit tests.</summary>
public sealed class FixedCurrentDateService : ICurrentDateService
{
    public FixedCurrentDateService(DateTime today, DateTime? now = null)
    {
        Today = today.Date;
        Now = now ?? today.Date;
    }

    public DateTime Today { get; }
    public DateTime Now { get; }
}
