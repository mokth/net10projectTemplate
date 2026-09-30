using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

/// <summary>Outcome of an item UOM conversion request. Never throws for a missing conversion.</summary>
public sealed class UomConversionResult
{
    public bool Succeeded { get; init; }
    public decimal Quantity { get; init; }
    public int RoundingScale { get; init; } = IvQty.Scale;
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }

    public static UomConversionResult Ok(decimal quantity, int scale) =>
        new() { Succeeded = true, Quantity = quantity, RoundingScale = scale };

    public static UomConversionResult Fail(string code, string message) =>
        new() { Succeeded = false, FailureCode = code, FailureMessage = message };
}

public static class UomConversionFailureCodes
{
    /// <summary>
    /// No approved conversion exists for the item/UOM pair. Work Order release treats this as a
    /// blocking readiness failure rather than guessing a factor.
    /// </summary>
    public const string MissingConversion = "IV_UOM_CONVERSION_MISSING";

    /// <summary>The request itself was incomplete or invalid.</summary>
    public const string InvalidRequest = "IV_UOM_CONVERSION_INVALID";
}

/// <summary>
/// Single owner of item UOM conversion (plan §5.5). Product Definition, Work Order snapshotting and
/// material issue all resolve quantities through this service so a conversion factor is never
/// re-derived or interpolated locally.
/// </summary>
public interface IUomConversionService
{
    /// <summary>True when an approved, active conversion exists in either direction.</summary>
    Task<bool> HasConversionAsync(
        string companyCode,
        string itemCode,
        string fromUom,
        string toUom,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts <paramref name="quantity"/> from one item UOM to another using the approved
    /// conversion. Identical UOMs are an identity conversion.
    /// </summary>
    Task<UomConversionResult> ConvertAsync(
        string companyCode,
        string itemCode,
        decimal quantity,
        string fromUom,
        string toUom,
        CancellationToken cancellationToken = default);
}

public sealed class IvUomConversionService : IUomConversionService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public IvUomConversionService(IDbContextFactory<AppDbContext> dbFactory) =>
        _dbFactory = dbFactory;

    public async Task<bool> HasConversionAsync(
        string companyCode,
        string itemCode,
        string fromUom,
        string toUom,
        CancellationToken cancellationToken = default)
    {
        var request = Normalize(itemCode, fromUom, toUom);
        if (request is null)
        {
            return false;
        }

        var (item, from, to) = request.Value;
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return true;
        }

        var company = NormalizeCode(companyCode);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.IvItemUomConversions.AsNoTracking()
            .AnyAsync(x => x.CompanyCode == company
                           && x.ItemCode == item
                           && x.IsActive
                           && ((x.FromUom == from && x.ToUom == to)
                               || (x.FromUom == to && x.ToUom == from)),
                cancellationToken);
    }

    public async Task<UomConversionResult> ConvertAsync(
        string companyCode,
        string itemCode,
        decimal quantity,
        string fromUom,
        string toUom,
        CancellationToken cancellationToken = default)
    {
        if (quantity < 0m)
        {
            return UomConversionResult.Fail(
                UomConversionFailureCodes.InvalidRequest, "Quantity cannot be negative.");
        }

        var request = Normalize(itemCode, fromUom, toUom);
        if (request is null)
        {
            return UomConversionResult.Fail(
                UomConversionFailureCodes.InvalidRequest, "Item and both UOMs are required.");
        }

        var (item, from, to) = request.Value;
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return UomConversionResult.Ok(IvQty.Round(quantity), IvQty.Scale);
        }

        var company = NormalizeCode(companyCode);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var matches = await db.IvItemUomConversions.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.ItemCode == item
                        && x.IsActive
                        && ((x.FromUom == from && x.ToUom == to)
                            || (x.FromUom == to && x.ToUom == from)))
            .ToListAsync(cancellationToken);

        if (matches.Count == 0)
        {
            return UomConversionResult.Fail(
                UomConversionFailureCodes.MissingConversion,
                $"No approved conversion from {from} to {to} for item {item}.");
        }

        // Prefer the row authored in the requested direction; otherwise use the reverse factor.
        var forward = matches.FirstOrDefault(x => string.Equals(x.FromUom, from, StringComparison.Ordinal));
        var row = forward ?? matches[0];
        var factor = forward is not null
            ? SafeDivide(row.ToQty, row.FromQty)
            : SafeDivide(row.FromQty, row.ToQty);
        if (factor is null)
        {
            return UomConversionResult.Fail(
                UomConversionFailureCodes.MissingConversion,
                $"Conversion for item {item} has a non-positive quantity pair.");
        }

        return UomConversionResult.Ok(
            Round(quantity * factor.Value, row.RoundingScale, row.RoundingMode),
            Math.Clamp(row.RoundingScale, 0, 6));
    }

    private static decimal? SafeDivide(decimal numerator, decimal denominator) =>
        denominator == 0m ? null : numerator / denominator;

    private static decimal Round(decimal value, int scale, string? mode)
    {
        var clamped = Math.Clamp(scale, 0, 6);
        var normalizedMode = (mode ?? IvUomRoundingModes.AwayFromZero).Trim().ToUpperInvariant();
        return normalizedMode switch
        {
            IvUomRoundingModes.ToEven => decimal.Round(value, clamped, MidpointRounding.ToEven),
            IvUomRoundingModes.Ceiling =>
                decimal.Round(Math.Ceiling(value), clamped, MidpointRounding.AwayFromZero),
            IvUomRoundingModes.Floor =>
                decimal.Round(Math.Floor(value), clamped, MidpointRounding.AwayFromZero),
            _ => decimal.Round(value, clamped, MidpointRounding.AwayFromZero)
        };
    }

    private static (string Item, string From, string To)? Normalize(string? itemCode, string? fromUom, string? toUom)
    {
        var item = NormalizeCode(itemCode);
        var from = NormalizeCode(fromUom);
        var to = NormalizeCode(toUom);
        return item.Length == 0 || from.Length == 0 || to.Length == 0
            ? null
            : (item, from, to);
    }

    private static string NormalizeCode(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();
}
