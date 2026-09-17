using ErpWeb.Model.Entities;

namespace ErpWeb.Core.Settings;

/// <summary>One stored row at one scope, as the admin screen needs to see it.</summary>
public sealed class AppSettingStoredRow
{
    public int ParamId { get; init; }
    public string Scope { get; init; } = "";
    public string? Value { get; init; }

    /// <summary>Base64 of the row's <c>RowVersion</c>. The edit round-trip MUST carry this back.</summary>
    public string? RowVersion { get; init; }

    public string? Remark { get; init; }
    public string? ModifiedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
}

/// <summary>
/// One row of the admin grid: a catalogue definition plus what it currently resolves to, where that came
/// from, and any stored row at each level the operator may edit.
/// </summary>
public sealed class AppSettingListRow
{
    public string Module { get; init; } = "";
    public string Key { get; init; } = "";
    public AppSettingType Type { get; init; }
    public AppSettingScope AllowedScopes { get; init; }
    public string? DefaultValue { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> AllowedTokens { get; init; } = [];

    /// <summary>True for a projection over an existing column: shown disabled, with a link to its own screen.</summary>
    public bool IsReadOnly { get; init; }

    public string? Value { get; init; }
    public AppSettingScope Provenance { get; init; }
    public bool IsDefault { get; init; }

    /// <summary>Why a stored value was unusable and the default applied instead. Null when nothing was rejected.</summary>
    public string? RejectedReason { get; init; }

    public AppSettingStoredRow? StoredGlobal { get; init; }
    public AppSettingStoredRow? StoredCompany { get; init; }
    public AppSettingStoredRow? StoredBranch { get; init; }

    /// <summary>Audit of whichever stored row actually governed the resolved value.</summary>
    public string? ModifiedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public string? Remark { get; init; }

    /// <summary>True when any level holds a stored row, so "clear" is meaningful.</summary>
    public bool HasAnyStoredRow =>
        StoredGlobal is not null || StoredCompany is not null || StoredBranch is not null;
}

/// <summary>
/// A write request for one setting at one scope. <see cref="RowVersion"/> is required when a row already
/// exists: without it a concurrent update is undetectable and the second write silently wins.
/// </summary>
public sealed class AppSettingEditVm
{
    public string Module { get; set; } = "";
    public string Key { get; set; } = "";
    public AppSettingScope Scope { get; set; } = AppSettingScope.Company;
    public string? CompanyCode { get; set; }
    public string? BranchCode { get; set; }
    public string? Value { get; set; }
    public string? RowVersion { get; set; }
    public string? Remark { get; set; }
}

/// <summary>Internal carrier for the three raw stored values of one key.</summary>
internal sealed record AppSettingRawLevels(string? Global, string? Company, string? Branch);

/// <summary>One cached snapshot: every row this tenant can see for one module.</summary>
internal sealed class AppSettingModuleSnapshot
{
    public required string Module { get; init; }
    public required IReadOnlyList<AdSmParam> Rows { get; init; }
    public required IReadOnlyDictionary<string, AppSettingRawLevels> Levels { get; init; }

    /// <summary>Rows whose key is not in the catalogue. Ignored on read; reported as a diagnostic only.</summary>
    public required int OrphanCount { get; init; }
}
