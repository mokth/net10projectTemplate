namespace ErpWeb.Core.Numbering;

public sealed class AdSmNumListRow
{
    public string NumCd { get; init; } = string.Empty;
    public string? Prefix { get; init; }
    public short TotLength { get; init; }
    public long Seq { get; init; }
    public string? NumDes { get; init; }
}

public sealed class AdSmNumEditVm
{
    public string NumCd { get; set; } = string.Empty;
    public string? Prefix { get; set; }
    public short TotLength { get; set; } = 10;
    public long Seq { get; set; } = 1;
    public string? NumDes { get; set; }

    /// <summary>Persisted Seq when loaded for edit; used for never-lower and freeze checks on the client.</summary>
    public long OriginalSeq { get; set; }
}

public sealed class AdSmNumDateListRow
{
    public int Uid { get; init; }
    public string NumCd { get; init; } = string.Empty;
    public short Year { get; init; }
    public short Month { get; init; }
    public string? Prefix { get; init; }
    public short TotLength { get; init; }
    public long Seq { get; init; }
    public string? NumberingDelimeter { get; init; }
    public string? NumberingFormat { get; init; }
    public string? NumDes { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class AdSmNumDateEditVm
{
    public int Uid { get; set; }
    public string NumCd { get; set; } = string.Empty;
    public short Year { get; set; }
    public short Month { get; set; }
    public string? Prefix { get; set; }
    public short TotLength { get; set; } = 4;
    public long Seq { get; set; } = 1;
    public string? NumberingDelimeter { get; set; }
    public string? NumberingFormat { get; set; }
    public string? NumDes { get; set; }
    public byte[]? RowVersion { get; set; }

    /// <summary>Persisted Seq when loaded for edit.</summary>
    public long OriginalSeq { get; set; }
}

public sealed class AdSmNumDateKey
{
    public int Uid { get; init; }
    public byte[] RowVersion { get; init; } = [];
}
