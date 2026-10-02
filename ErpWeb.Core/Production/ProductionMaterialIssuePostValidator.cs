using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Production;

internal static class ProductionMaterialIssuePostValidator
{
    public const int MaxMaterialLines = 200;
    public const int MaxAllocationRows = 1000;

    public static IReadOnlyDictionary<string, string> Validate(
        ProductionMaterialIssuePostRequest? request,
        DateTime now)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request is null)
        {
            errors["Request"] = "Posting request is required.";
            return errors;
        }

        if (string.IsNullOrWhiteSpace(request.PostingRequestId)
            || request.PostingRequestId.Length > 64
            || !Guid.TryParseExact(request.PostingRequestId, "N", out _))
            errors[nameof(request.PostingRequestId)] = "PostingRequestId must be a 32-character GUID.";
        if (string.IsNullOrWhiteSpace(request.WorkOrderNo))
            errors[nameof(request.WorkOrderNo)] = "Work Order number is required.";
        if (request.SnapshotRevision <= 0 || string.IsNullOrWhiteSpace(request.SnapshotHash))
            errors["Snapshot"] = "A valid Work Order snapshot fingerprint is required.";
        if (request.ProductionQtyThisIssue <= 0m)
            errors[nameof(request.ProductionQtyThisIssue)] = "Desired output quantity must be greater than zero.";
        if (request.IssueDate == default || request.IssueDate > now)
            errors[nameof(request.IssueDate)] = "Issue date/time is required and cannot be in the future.";
        if (request.Lines.Count is 0 or > MaxMaterialLines)
            errors[nameof(request.Lines)] = $"Provide between 1 and {MaxMaterialLines} material lines.";
        if (request.Lines.GroupBy(x => x.WorkOrderMaterialId).Any(x => x.Count() > 1))
            errors[nameof(request.Lines)] = "A Work Order material may appear only once.";
        if (request.Lines.Sum(x => x.Allocations.Count) > MaxAllocationRows)
            errors["Allocations"] = $"A posting may contain at most {MaxAllocationRows} allocation rows.";

        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            if (line.WorkOrderMaterialId <= 0 || line.IssueQty <= 0m)
                errors[$"Lines[{i}]"] = "Material identity and issue quantity must be positive.";
            if (line.Allocations.Count == 0 || line.Allocations.Any(x => x.FromBalLocId <= 0 || x.BaseQty <= 0m))
                errors[$"Lines[{i}].Allocations"] = "At least one positive stock allocation is required.";
            if (line.Allocations.GroupBy(x => x.FromBalLocId).Any(x => x.Count() > 1))
                errors[$"Lines[{i}].Allocations"] = "A balance row may appear only once per material line.";
            if (line.Allocations.Sum(x => IvQty.Round(x.BaseQty)) <= 0m)
                errors[$"Lines[{i}].Allocations"] = "Allocated base quantity must be positive.";
        }

        return errors;
    }
}
