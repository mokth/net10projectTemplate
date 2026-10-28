namespace ErpWeb.Core.Inventory;

/// <summary>Failure codes for the item UOM/pack policy. Never thrown; returned for callers to map.</summary>
public static class IvItemPackFailureCodes
{
    /// <summary>The source UOM (selling / purchase) is required.</summary>
    public const string SourceUomRequired = "IV_PACK_SOURCE_UOM_REQUIRED";

    /// <summary>The standard UOM is required.</summary>
    public const string StandardUomRequired = "IV_PACK_STD_UOM_REQUIRED";

    /// <summary>A different source UOM needs an explicit factor — never an assumed 1.</summary>
    public const string FactorRequired = "IV_PACK_FACTOR_REQUIRED";

    /// <summary>The supplied factor is zero or negative.</summary>
    public const string FactorNonPositive = "IV_PACK_FACTOR_NON_POSITIVE";

    /// <summary>The same item/UOM pair would mean two different physical quantities.</summary>
    public const string FactorConflict = "IV_PACK_FACTOR_CONFLICT";
}

/// <summary>Outcome of resolving one primary UOM pair to a standard-quantity factor.</summary>
public readonly record struct IvPackFactorResult(
    bool Succeeded,
    decimal Factor,
    string? FailureCode,
    string? FailureMessage)
{
    public static IvPackFactorResult Ok(decimal factor) =>
        new(true, factor, null, null);

    public static IvPackFactorResult Fail(string code, string message) =>
        new(false, 0m, code, message);
}

/// <summary>
/// Pure item UOM / pack-size policy (plan §4, §6, §10.2).
/// <para>
/// Responsibilities only: normalize a same-UOM factor to 1, require an explicit positive factor
/// when the source UOM differs from the standard UOM, compare factors at the persisted quantity
/// precision, and evaluate the selling/purchase pair consistency rule. No database access, no
/// posting, no costing, no document-status logic lives here.
/// </para>
/// </summary>
public static class IvItemPackPolicy
{
    /// <summary>Quantity/ratio precision used by <c>IvItemUomConversion</c> (decimal(18,8)).</summary>
    public const int FactorScale = 8;

    /// <summary>
    /// Comparison tolerance for persisted factors. Two factors closer than this are the same
    /// physical quantity; the value matches the existing "<c>0.00005m</c>" quantity tolerance used
    /// by the Stock/Production snapshot checks.
    /// </summary>
    public const decimal FactorTolerance = 0.00005m;

    public static string NormalizeUom(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    public static string? NormalizeUomOrNull(string? value)
    {
        var normalized = NormalizeUom(value);
        return normalized.Length == 0 ? null : normalized;
    }

    public static bool SameUom(string? left, string? right)
    {
        var a = NormalizeUom(left);
        return a.Length > 0 && string.Equals(a, NormalizeUom(right), StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="sourceUom"/> is the standard UOM (identity conversion).</summary>
    public static bool IsIdentity(string? sourceUom, string? stdUom) => SameUom(sourceUom, stdUom);

    /// <summary>
    /// Canonical primary factor. Identity pairs are always exactly 1 regardless of the supplied
    /// value; a different source UOM must carry an explicit positive factor.
    /// </summary>
    public static bool TryNormalizeFactor(
        string? sourceUom,
        string? stdUom,
        decimal? requestedFactor,
        out decimal factor,
        out string? failureCode,
        out string? failureMessage)
    {
        factor = 0m;
        failureCode = null;
        failureMessage = null;

        var source = NormalizeUom(sourceUom);
        var standard = NormalizeUom(stdUom);
        if (source.Length == 0)
        {
            failureCode = IvItemPackFailureCodes.SourceUomRequired;
            failureMessage = "A source UOM is required.";
            return false;
        }

        if (standard.Length == 0)
        {
            failureCode = IvItemPackFailureCodes.StandardUomRequired;
            failureMessage = "A standard UOM is required.";
            return false;
        }

        if (string.Equals(source, standard, StringComparison.Ordinal))
        {
            factor = 1m;
            return true;
        }

        if (requestedFactor is null)
        {
            failureCode = IvItemPackFailureCodes.FactorRequired;
            failureMessage =
                $"1 {source} = n {standard}: an explicit pack factor is required when the UOMs differ.";
            return false;
        }

        if (requestedFactor.Value <= 0m)
        {
            failureCode = IvItemPackFailureCodes.FactorNonPositive;
            failureMessage =
                $"1 {source} = n {standard}: the pack factor must be greater than zero when the UOMs differ.";
            return false;
        }

        factor = RoundFactor(requestedFactor.Value);
        return true;
    }

    /// <inheritdoc cref="TryNormalizeFactor(string?,string?,decimal?,out decimal,out string?,out string?)"/>
    public static IvPackFactorResult Resolve(string? sourceUom, string? stdUom, decimal? requestedFactor) =>
        TryNormalizeFactor(sourceUom, stdUom, requestedFactor, out var factor, out var code, out var message)
            ? IvPackFactorResult.Ok(factor)
            : IvPackFactorResult.Fail(code!, message!);

    /// <summary>
    /// Document-line factor for Sales/Purchase entry (plan §7.1, §8.1). Stock-controlled items never
    /// fall back to 1 for a different UOM: the failure message is returned for the caller to surface.
    /// Non-stock and indirect lines keep the legacy lenient behaviour.
    /// </summary>
    public static decimal ResolveDocumentFactor(
        bool stockControl,
        string? sourceUom,
        string? stdUom,
        decimal? packSize,
        out string? failureMessage)
    {
        failureMessage = null;

        if (!stockControl)
        {
            return packSize is > 0m ? packSize.Value : 1m;
        }

        var result = Resolve(sourceUom, stdUom, packSize);
        if (result.Succeeded)
        {
            return result.Factor;
        }

        failureMessage = result.FailureMessage;
        return 0m;
    }

    /// <summary>Same physical quantity at the persisted factor precision.</summary>
    public static bool FactorsEqual(decimal left, decimal right) =>
        Math.Abs(left - right) <= FactorTolerance;

    public static decimal RoundFactor(decimal value) =>
        decimal.Round(value, FactorScale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// When the selling and purchase UOMs are identical the two pack factors must describe the same
    /// physical quantity; one item/UOM pair cannot mean two different things.
    /// </summary>
    public static bool TryValidatePairConsistency(
        string? sellingUom,
        decimal sellingFactor,
        string? purchaseUom,
        decimal purchaseFactor,
        out string? failureMessage)
    {
        failureMessage = null;
        if (!SameUom(sellingUom, purchaseUom))
        {
            return true;
        }

        if (FactorsEqual(sellingFactor, purchaseFactor))
        {
            return true;
        }

        var uom = NormalizeUom(sellingUom);
        failureMessage =
            $"The selling and purchase UOM are both '{uom}', so the selling factor ({sellingFactor:0.########}) and purchase factor ({purchaseFactor:0.########}) must be the same.";
        return false;
    }
}
