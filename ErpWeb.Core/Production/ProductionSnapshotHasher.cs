using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ErpWeb.Core.Production;

internal sealed record ProductionSnapshotHashLine(
    int LineNo,
    long? SourceBomHdrId,
    int? SourceBomVersion,
    long? SourceBomLineId,
    string? ParentProductCode,
    string BomPath,
    string ComponentCode,
    string? ComponentDescription,
    string MfgType,
    decimal ComponentQtyPerParent,
    decimal BomOutputQty,
    string? BomOutputUom,
    decimal ScrapPercent,
    decimal Tolerance,
    decimal RequiredQty,
    string? RequiredUom,
    string? WarehouseCode,
    string? LocationCode);

internal sealed record ProductionSnapshotHashResource(
    int SequenceNo,
    string ResourceType,
    string ResourceCode,
    string? ResourceDescription,
    decimal PlannedUnits,
    decimal SetupMinutes,
    decimal RunMinutes,
    decimal QueueMinutes,
    decimal Rate,
    decimal PlannedAmount);

internal sealed record ProductionSnapshotHashOperation(
    int SequenceNo,
    string? WorkCentreCode,
    string? WorkCentreDescription,
    string OperationCode,
    string? OperationDescription,
    bool IsFinalOperation,
    DateTime? PlannedStartDate,
    DateTime? PlannedCompletionDate,
    decimal PlannedQty,
    decimal SetupLossQty,
    decimal OperationLossQty,
    IReadOnlyList<ProductionSnapshotHashResource> Resources);

internal static class ProductionSnapshotHasher
{
    public static string Compute(
        string productCode,
        string? productDescription,
        string? outputUom,
        long sourceBomHdrId,
        int sourceBomVersion,
        decimal bomBaseQty,
        string? bomBaseUom,
        decimal plannedQty,
        DateTime asOfDate,
        DateTime plannedStartDate,
        DateTime plannedCompletionDate,
        string schedulingDirection,
        string sourceType,
        string? sourceReference,
        string? remark,
        IEnumerable<ProductionSnapshotHashLine> materials,
        IEnumerable<ProductionSnapshotHashOperation> operations)
    {
        var sb = new StringBuilder();
        Append(sb, productCode);
        Append(sb, productDescription);
        Append(sb, outputUom);
        Append(sb, sourceBomHdrId);
        Append(sb, sourceBomVersion);
        Append(sb, bomBaseQty);
        Append(sb, bomBaseUom);
        Append(sb, plannedQty);
        Append(sb, asOfDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Append(sb, plannedStartDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Append(sb, plannedCompletionDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Append(sb, schedulingDirection);
        Append(sb, sourceType);
        Append(sb, sourceReference);
        Append(sb, remark);

        foreach (var line in materials.OrderBy(x => x.LineNo))
        {
            Append(sb, line.LineNo);
            Append(sb, line.SourceBomHdrId);
            Append(sb, line.SourceBomVersion);
            Append(sb, line.SourceBomLineId);
            Append(sb, line.ParentProductCode);
            Append(sb, line.BomPath);
            Append(sb, line.ComponentCode);
            Append(sb, line.ComponentDescription);
            Append(sb, line.MfgType);
            Append(sb, line.ComponentQtyPerParent);
            Append(sb, line.BomOutputQty);
            Append(sb, line.BomOutputUom);
            Append(sb, line.ScrapPercent);
            Append(sb, line.Tolerance);
            Append(sb, line.RequiredQty);
            Append(sb, line.RequiredUom);
            Append(sb, line.WarehouseCode);
            Append(sb, line.LocationCode);
        }

        foreach (var operation in operations.OrderBy(x => x.SequenceNo))
        {
            Append(sb, operation.SequenceNo);
            Append(sb, operation.WorkCentreCode);
            Append(sb, operation.WorkCentreDescription);
            Append(sb, operation.OperationCode);
            Append(sb, operation.OperationDescription);
            Append(sb, operation.IsFinalOperation);
            Append(sb, operation.PlannedStartDate?.ToString("O", CultureInfo.InvariantCulture));
            Append(sb, operation.PlannedCompletionDate?.ToString("O", CultureInfo.InvariantCulture));
            Append(sb, operation.PlannedQty);
            Append(sb, operation.SetupLossQty);
            Append(sb, operation.OperationLossQty);

            foreach (var resource in operation.Resources.OrderBy(x => x.SequenceNo))
            {
                Append(sb, resource.SequenceNo);
                Append(sb, resource.ResourceType);
                Append(sb, resource.ResourceCode);
                Append(sb, resource.ResourceDescription);
                Append(sb, resource.PlannedUnits);
                Append(sb, resource.SetupMinutes);
                Append(sb, resource.RunMinutes);
                Append(sb, resource.QueueMinutes);
                Append(sb, resource.Rate);
                Append(sb, resource.PlannedAmount);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static void Append(StringBuilder sb, object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            decimal number => number.ToString("0.############################", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
        // Hash the exact normalized snapshot value. Length-prefixing keeps the serialization
        // unambiguous even when user-entered text contains control or separator characters.
        // Callers normalize codes and trim editable fields before persistence; changing the case
        // or whitespace of stored descriptive text must still invalidate the snapshot.
        sb.Append(text.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(text);
    }
}

