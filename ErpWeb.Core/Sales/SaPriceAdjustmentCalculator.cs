namespace ErpWeb.Core.Sales;

public static class SaPriceAdjustmentMethods
{
    public const string SetPrice = "SET_PRICE";
    public const string IncreasePercent = "INCREASE_PERCENT";
    public const string DecreasePercent = "DECREASE_PERCENT";
    public const string IncreaseAmount = "INCREASE_AMOUNT";
    public const string DecreaseAmount = "DECREASE_AMOUNT";

    public static IReadOnlyList<string> All { get; } =
    [
        SetPrice, IncreasePercent, DecreasePercent, IncreaseAmount, DecreaseAmount
    ];
}

public static class SaPriceRoundingModes
{
    public const string Normal = "NORMAL";
    public const string Up = "UP";
    public const string Down = "DOWN";

    public static IReadOnlyList<string> All { get; } = [Normal, Up, Down];
}

public sealed record SaPriceAdjustmentResult(bool Succeeded, decimal? NewPrice, string? Error)
{
    public static SaPriceAdjustmentResult Success(decimal value) => new(true, value, null);
    public static SaPriceAdjustmentResult Failure(string message) => new(false, null, message);
}

/// <summary>
/// Pure decimal-only calculator for the five approved bulk price adjustments. It has no database,
/// tenant, permission, or UI dependencies.
/// </summary>
public static class SaPriceAdjustmentCalculator
{
    public static SaPriceAdjustmentResult Calculate(
        decimal? oldPrice,
        string? method,
        decimal value,
        int decimalPlaces = 2,
        string? roundingMode = SaPriceRoundingModes.Normal)
    {
        var normalizedMethod = (method ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedRounding = (roundingMode ?? SaPriceRoundingModes.Normal).Trim().ToUpperInvariant();

        if (!SaPriceAdjustmentMethods.All.Contains(normalizedMethod, StringComparer.Ordinal))
        {
            return SaPriceAdjustmentResult.Failure("Select a supported price adjustment method.");
        }

        if (decimalPlaces is not (2 or 4))
        {
            return SaPriceAdjustmentResult.Failure("Price precision must be 2 or 4 decimal places.");
        }

        if (!SaPriceRoundingModes.All.Contains(normalizedRounding, StringComparer.Ordinal))
        {
            return SaPriceAdjustmentResult.Failure("Select a supported rounding mode.");
        }

        if (value < 0m)
        {
            return SaPriceAdjustmentResult.Failure("Adjustment value cannot be negative.");
        }

        if (oldPrice is < 0m)
        {
            return SaPriceAdjustmentResult.Failure("Current price cannot be negative.");
        }

        try
        {
            decimal raw;
            switch (normalizedMethod)
            {
                case SaPriceAdjustmentMethods.SetPrice:
                    raw = value;
                    break;

                case SaPriceAdjustmentMethods.IncreasePercent:
                    if (oldPrice is null)
                    {
                        return SaPriceAdjustmentResult.Failure("An existing price is required for an increase percentage.");
                    }

                    raw = oldPrice.Value * (1m + value / 100m);
                    break;

                case SaPriceAdjustmentMethods.DecreasePercent:
                    if (value > 100m)
                    {
                        return SaPriceAdjustmentResult.Failure("Decrease percentage cannot be greater than 100%.");
                    }

                    if (oldPrice is null)
                    {
                        return SaPriceAdjustmentResult.Failure("An existing price is required for a decrease percentage.");
                    }

                    raw = oldPrice.Value * (1m - value / 100m);
                    break;

                case SaPriceAdjustmentMethods.IncreaseAmount:
                    if (oldPrice is null)
                    {
                        return SaPriceAdjustmentResult.Failure("An existing price is required for an increase amount.");
                    }

                    raw = oldPrice.Value + value;
                    break;

                case SaPriceAdjustmentMethods.DecreaseAmount:
                    if (oldPrice is null)
                    {
                        return SaPriceAdjustmentResult.Failure("An existing price is required for a decrease amount.");
                    }

                    raw = oldPrice.Value - value;
                    if (raw < 0m)
                    {
                        return SaPriceAdjustmentResult.Failure("Decrease amount cannot produce a negative price.");
                    }

                    break;

                default:
                    return SaPriceAdjustmentResult.Failure("Select a supported price adjustment method.");
            }

            var rounded = Round(raw, decimalPlaces, normalizedRounding);
            return rounded < 0m
                ? SaPriceAdjustmentResult.Failure("The calculated price cannot be negative.")
                : SaPriceAdjustmentResult.Success(rounded);
        }
        catch (OverflowException)
        {
            return SaPriceAdjustmentResult.Failure("The calculated price is outside the supported decimal range.");
        }
    }

    private static decimal Round(decimal value, int places, string mode)
    {
        var scale = places == 2 ? 100m : 10_000m;
        return mode switch
        {
            SaPriceRoundingModes.Up => decimal.Ceiling(value * scale) / scale,
            SaPriceRoundingModes.Down => decimal.Floor(value * scale) / scale,
            _ => decimal.Round(value, places, MidpointRounding.AwayFromZero)
        };
    }
}
