using ErpWeb.EInvoiceLib.Model.InputData;
using ErpWeb.EInvoiceLib.Document;

namespace ErpWeb.Core.EInvoice;

/// <summary>
/// Translates the free-text values the ERP stores (state names, country names, registration type
/// tokens) into the codes MyInvois expects.
/// <para>
/// Everything returns <c>null</c> when the value cannot be resolved. The validator turns that into a
/// blocking ERP validation error, which is deliberate: submitting a guessed state/country code is
/// worse than refusing to submit.
/// </para>
/// </summary>
public static class LhdnCodeLookup
{
    /// <summary>MyInvois state codes.</summary>
    private static readonly Dictionary<string, string> StateCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["01"] = "01", ["johor"] = "01", ["jhr"] = "01",
        ["02"] = "02", ["kedah"] = "02", ["kdh"] = "02",
        ["03"] = "03", ["kelantan"] = "03", ["kltn"] = "03", ["kelantan darul naim"] = "03",
        ["04"] = "04", ["melaka"] = "04", ["malacca"] = "04", ["mlk"] = "04",
        ["05"] = "05", ["negeri sembilan"] = "05", ["n sembilan"] = "05", ["nsembilan"] = "05", ["nsn"] = "05",
        ["06"] = "06", ["pahang"] = "06", ["phg"] = "06",
        ["07"] = "07", ["pulau pinang"] = "07", ["penang"] = "07", ["pinang"] = "07", ["png"] = "07",
        ["08"] = "08", ["perak"] = "08", ["prk"] = "08",
        ["09"] = "09", ["perlis"] = "09", ["pls"] = "09",
        ["10"] = "10", ["selangor"] = "10", ["sgr"] = "10",
        ["11"] = "11", ["terengganu"] = "11", ["trengganu"] = "11", ["trg"] = "11",
        ["12"] = "12", ["sabah"] = "12", ["sbh"] = "12",
        ["13"] = "13", ["sarawak"] = "13", ["swk"] = "13",
        ["14"] = "14",
        ["kuala lumpur"] = "14", ["wp kuala lumpur"] = "14", ["wilayah persekutuan kuala lumpur"] = "14",
        ["kl"] = "14", ["w.p. kuala lumpur"] = "14", ["wp kl"] = "14",
        ["15"] = "15", ["labuan"] = "15", ["wp labuan"] = "15", ["wilayah persekutuan labuan"] = "15",
        ["16"] = "16", ["putrajaya"] = "16", ["wp putrajaya"] = "16", ["wilayah persekutuan putrajaya"] = "16",
        // 17 = "not applicable" and is the correct code for a foreign address.
        ["17"] = "17", ["not applicable"] = "17", ["n/a"] = "17", ["na"] = "17", ["foreign"] = "17"
    };

    /// <summary>Common country names / ISO alpha-2 to ISO alpha-3 (the form MyInvois expects).</summary>
    private static readonly Dictionary<string, string> CountryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MYS"] = "MYS", ["MY"] = "MYS", ["malaysia"] = "MYS",
        ["SGP"] = "SGP", ["SG"] = "SGP", ["singapore"] = "SGP",
        ["IDN"] = "IDN", ["ID"] = "IDN", ["indonesia"] = "IDN",
        ["THA"] = "THA", ["TH"] = "THA", ["thailand"] = "THA",
        ["VNM"] = "VNM", ["VN"] = "VNM", ["vietnam"] = "VNM", ["viet nam"] = "VNM",
        ["PHL"] = "PHL", ["PH"] = "PHL", ["philippines"] = "PHL",
        ["BRN"] = "BRN", ["BN"] = "BRN", ["brunei"] = "BRN", ["brunei darussalam"] = "BRN",
        ["CHN"] = "CHN", ["CN"] = "CHN", ["china"] = "CHN",
        ["HKG"] = "HKG", ["HK"] = "HKG", ["hong kong"] = "HKG",
        ["TWN"] = "TWN", ["TW"] = "TWN", ["taiwan"] = "TWN",
        ["JPN"] = "JPN", ["JP"] = "JPN", ["japan"] = "JPN",
        ["KOR"] = "KOR", ["KR"] = "KOR", ["south korea"] = "KOR", ["korea"] = "KOR",
        ["IND"] = "IND", ["IN"] = "IND", ["india"] = "IND",
        ["AUS"] = "AUS", ["AU"] = "AUS", ["australia"] = "AUS",
        ["NZL"] = "NZL", ["NZ"] = "NZL", ["new zealand"] = "NZL",
        ["USA"] = "USA", ["US"] = "USA", ["united states"] = "USA", ["united states of america"] = "USA",
        ["GBR"] = "GBR", ["GB"] = "GBR", ["UK"] = "GBR", ["united kingdom"] = "GBR",
        ["DEU"] = "DEU", ["DE"] = "DEU", ["germany"] = "DEU",
        ["FRA"] = "FRA", ["FR"] = "FRA", ["france"] = "FRA",
        ["NLD"] = "NLD", ["NL"] = "NLD", ["netherlands"] = "NLD",
        ["ARE"] = "ARE", ["AE"] = "ARE", ["united arab emirates"] = "ARE", ["uae"] = "ARE",
        ["SAU"] = "SAU", ["SA"] = "SAU", ["saudi arabia"] = "SAU",
        ["PAK"] = "PAK", ["PK"] = "PAK", ["pakistan"] = "PAK",
        ["BGD"] = "BGD", ["BD"] = "BGD", ["bangladesh"] = "BGD",
        ["LKA"] = "LKA", ["LK"] = "LKA", ["sri lanka"] = "LKA",
        ["MMR"] = "MMR", ["MM"] = "MMR", ["myanmar"] = "MMR",
        ["KHM"] = "KHM", ["KH"] = "KHM", ["cambodia"] = "KHM",
        ["LAO"] = "LAO", ["LA"] = "LAO", ["laos"] = "LAO",
        ["NPL"] = "NPL", ["NP"] = "NPL", ["nepal"] = "NPL"
    };

    /// <summary>Resolve an ERP state value (name or already-coded) to a MyInvois state code.</summary>
    public static string? TryStateCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var key = Normalize(value);
        return StateCodes.TryGetValue(key, out var code) ? code : null;
    }

    /// <summary>Resolve an ERP country value to an ISO 3166-1 alpha-3 country code.</summary>
    public static string? TryCountryCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var key = Normalize(value);
        if (CountryCodes.TryGetValue(key, out var code))
        {
            return code;
        }

        // An unrecognised value that already looks like an alpha-3 code is passed through; the
        // country list is not exhaustive, and MyInvois will reject a genuinely invalid code.
        return key.Length == 3 && key.All(char.IsLetter) ? key.ToUpperInvariant() : null;
    }

    /// <summary>Resolve the ERP registration type token to the LHDN identity type.</summary>
    public static TINRegistrationType? TryRegistrationType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Normalize(value) switch
        {
            "nric" or "ic" or "mykad" or "identity card" => TINRegistrationType.NRIC,
            "brn" or "business registration number" or "ssm" or "roc" or "rob" => TINRegistrationType.BRN,
            "passport" or "passport no" or "passport number" => TINRegistrationType.PASSPORT,
            "army" or "army number" or "military" => TINRegistrationType.ARMY,
            _ => null
        };
    }

    /// <summary>
    /// Resolve the ERP registration type token to the MyInvois taxpayer-validation identity type.
    /// Same vocabulary as <see cref="TryRegistrationType"/>; kept separate because the validate API
    /// takes its own enum.
    /// </summary>
    public static IDType? TryIdType(string? value) =>
        TryRegistrationType(value) switch
        {
            TINRegistrationType.NRIC => IDType.NRIC,
            TINRegistrationType.BRN => IDType.BRN,
            TINRegistrationType.PASSPORT => IDType.PASSPORT,
            TINRegistrationType.ARMY => IDType.ARMY,
            _ => null
        };

    private static string Normalize(string value) => value.Trim().ToLowerInvariant().Replace("  ", " ");
}
