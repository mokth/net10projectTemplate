using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record CurrencyRateResult(
    bool Succeeded,
    string CompanyCurrency,
    string TransactionCurrency,
    decimal Rate,
    string? Error)
{
    public static CurrencyRateResult Fail(string companyCurrency, string transactionCurrency, string error) =>
        new(false, companyCurrency, transactionCurrency, 0m, error);

    public static CurrencyRateResult Ok(string companyCurrency, string transactionCurrency, decimal rate) =>
        new(true, companyCurrency, transactionCurrency, rate, null);
}

public interface ICompanyCurrencyRateResolver
{
    Task<CurrencyRateResult> ResolveAsync(
        AppDbContext db,
        string companyCode,
        string transactionCurrency,
        DateTime businessDate,
        CancellationToken cancellationToken = default);
}

public sealed class CompanyCurrencyRateResolver : ICompanyCurrencyRateResolver
{
    public async Task<CurrencyRateResult> ResolveAsync(
        AppDbContext db,
        string companyCode,
        string transactionCurrency,
        DateTime businessDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var currency = (transactionCurrency ?? string.Empty).Trim().ToUpperInvariant();
        if (company.Length == 0)
            return CurrencyRateResult.Fail(string.Empty, currency, "Company is required for currency resolution.");
        if (currency.Length == 0)
            return CurrencyRateResult.Fail(string.Empty, string.Empty, "Transaction currency is required.");

        var home = await db.Companies.AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .Select(x => x.CurrencyCode)
            .SingleOrDefaultAsync(cancellationToken);
        home = (home ?? string.Empty).Trim().ToUpperInvariant();
        if (home.Length == 0)
            return CurrencyRateResult.Fail(home, currency,
                $"Company {company} has no configured base currency.");

        if (string.Equals(home, currency, StringComparison.OrdinalIgnoreCase))
            return CurrencyRateResult.Ok(home, currency, 1m);

        var date = businessDate.Date;
        var rate = await db.SaCurrRates.AsNoTracking()
            .Where(x => x.CurrCode == currency
                        && x.Status
                        && x.StartDate.Date <= date
                        && x.EndDate.Date >= date)
            .OrderByDescending(x => x.StartDate)
            .Select(x => (double?)x.HomeCurPerUnit)
            .FirstOrDefaultAsync(cancellationToken);
        if (rate is null || rate <= 0d || double.IsNaN(rate.Value) || double.IsInfinity(rate.Value))
            return CurrencyRateResult.Fail(home, currency,
                $"No currency rate for {currency}; no approved {currency}/{home} exchange rate covers {date:yyyy-MM-dd}.");

        return CurrencyRateResult.Ok(home, currency, decimal.Round((decimal)rate.Value, 8,
            MidpointRounding.AwayFromZero));
    }
}
