namespace ErpWeb.Core.Admin;

/// <summary>One row of the lookup maintenance grid.</summary>
public sealed class AdLookupRow
{
    public long Uid { get; init; }
    public string LookupType { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int SortOrder { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedBy { get; init; }
}

/// <summary>Edit/view model for one lookup value.</summary>
public sealed class AdLookupEditVm
{
    public long Uid { get; set; }

    /// <summary>Controlled family name. Chosen from <see cref="AdLookupTypeRegistry"/>, never typed.</summary>
    public string LookupType { get; set; } = string.Empty;

    /// <summary>Stable after creation — the UI makes it read-only in edit mode.</summary>
    public string Code { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public byte[]? RowVersion { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    /// <summary>Real consumer-column limit for the family; used by the UI for maxlength and hints.</summary>
    public int MaxCodeLength { get; set; } = AdLookupDefaults.CodeCeiling;
}

/// <summary>Bulk activate/deactivate token — Uid + optimistic concurrency token.</summary>
public sealed class AdLookupKeyToken
{
    public long Uid { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

/// <summary>Shared constants for the generic lookup infrastructure.</summary>
public static class AdLookupDefaults
{
    /// <summary>Physical column ceiling. NEVER the assignment limit — use the family's MaxCodeLength.</summary>
    public const int CodeCeiling = 50;

    public const int DescriptionMaxLength = 150;

    /// <summary>SortOrder step between seeded defaults.</summary>
    public const int SeedSortStep = 10;
}
