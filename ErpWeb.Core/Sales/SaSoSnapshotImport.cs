namespace ErpWeb.Core.Sales;

/// <summary>
/// Outcome of <see cref="SaSoService.ImportQuotationSnapshotAsync"/>. Deliberately its own type
/// rather than <see cref="SaSoOperationResult"/>: the snapshot import is a narrow internal step of
/// quotation conversion and must not expose the full SO service surface to its caller.
/// </summary>
public sealed class SaSoSnapshotImportResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public string? SoNo { get; init; }
    public byte[] RowVersion { get; init; } = [];

    public static SaSoSnapshotImportResult Ok(string soNo, byte[] rowVersion) =>
        new() { Succeeded = true, SoNo = soNo, RowVersion = rowVersion };

    public static SaSoSnapshotImportResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}
