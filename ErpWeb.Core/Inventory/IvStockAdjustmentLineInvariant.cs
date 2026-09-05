using ErpWeb.Model.Entities.Inventory;

namespace ErpWeb.Core.Inventory;

public enum IvAdjLineDirection
{
    Increase,
    Decrease
}

public static class IvStockAdjustmentLineInvariant
{
    public static string? ValidateDetail(IvTrxBatchDetail detail, short lineNo)
    {
        var hasFrom = detail.FromBalLocId is > 0;
        var hasTo = detail.ToBalLocId is > 0;
        var frQty = IvQty.Round(detail.FrStdQty ?? 0m);
        var toQty = IvQty.Round(detail.ToStdQty ?? 0m);

        if (hasFrom && hasTo)
        {
            return $"Line {lineNo}: adjustment line cannot have both from and to balance.";
        }

        if (!hasFrom && !hasTo)
        {
            return $"Line {lineNo}: balance location is required.";
        }

        if (hasFrom)
        {
            if (frQty <= 0m)
            {
                return $"Line {lineNo}: decrease quantity must be greater than zero.";
            }

            if (toQty != 0m || detail.ToBalLocId is not null)
            {
                return $"Line {lineNo}: decrease line cannot have to-side quantity.";
            }
        }
        else
        {
            if (toQty <= 0m)
            {
                return $"Line {lineNo}: increase quantity must be greater than zero.";
            }

            if (frQty != 0m || detail.FromBalLocId is not null)
            {
                return $"Line {lineNo}: increase line cannot have from-side quantity.";
            }
        }

        return null;
    }

    public static bool TryGetDirection(IvTrxBatchDetail detail, out IvAdjLineDirection direction)
    {
        direction = default;
        if (detail.ToBalLocId is > 0 && IvQty.Round(detail.ToStdQty ?? 0m) > 0m)
        {
            direction = IvAdjLineDirection.Increase;
            return true;
        }

        if (detail.FromBalLocId is > 0 && IvQty.Round(detail.FrStdQty ?? 0m) > 0m)
        {
            direction = IvAdjLineDirection.Decrease;
            return true;
        }

        return false;
    }

    public static int GetBalLocId(IvTrxBatchDetail detail) =>
        detail.ToBalLocId ?? detail.FromBalLocId ?? 0;

    public static decimal GetSignedDelta(IvTrxBatchDetail detail)
    {
        if (TryGetDirection(detail, out var direction))
        {
            return direction == IvAdjLineDirection.Increase
                ? IvQty.Round(detail.ToStdQty ?? 0m)
                : -IvQty.Round(detail.FrStdQty ?? 0m);
        }

        return 0m;
    }

    public static decimal GetAbsoluteQty(IvTrxBatchDetail detail) =>
        Math.Abs(GetSignedDelta(detail));

    public static string? ValidateReasonCode(string? reason, short lineNo)
    {
        var rsn = reason?.Trim();
        if (string.IsNullOrEmpty(rsn))
        {
            return $"Line {lineNo}: reason is required.";
        }

        if (NormalizeReasonCode(rsn) is null)
        {
            return $"Line {lineNo}: reason '{rsn}' is not valid.";
        }

        return null;
    }

    public static string? NormalizeReasonCode(string? reason)
    {
        var rsn = reason?.Trim();
        if (string.IsNullOrEmpty(rsn))
        {
            return null;
        }

        return IvAdjustmentReasons.All.FirstOrDefault(c =>
            string.Equals(c, rsn, StringComparison.OrdinalIgnoreCase));
    }

    public static (string? Reason, string? Remarks) ParseStoredRemarks(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return (null, null);
        }

        foreach (var code in IvAdjustmentReasons.All)
        {
            if (string.Equals(stored, code, StringComparison.Ordinal))
            {
                return (code, null);
            }

            var prefix = $"{code}: ";
            if (stored.StartsWith(prefix, StringComparison.Ordinal))
            {
                var remainder = stored[prefix.Length..];
                return (code, string.IsNullOrEmpty(remainder) ? null : remainder);
            }
        }

        return (null, stored);
    }

    public static string? CombineRemarks(string? reason, string? remarks)
    {
        var rsn = reason?.Trim();
        var rem = remarks?.Trim();
        if (string.IsNullOrEmpty(rsn))
        {
            return TruncateOptional(rem, 250);
        }

        if (string.IsNullOrEmpty(rem))
        {
            return TruncateOptional(rsn, 250);
        }

        return TruncateOptional($"{rsn}: {rem}", 250);
    }

    private static string? TruncateOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
