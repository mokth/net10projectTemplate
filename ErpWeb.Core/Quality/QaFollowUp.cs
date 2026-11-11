using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Quality;

namespace ErpWeb.Core.Quality;

internal static class QaFollowUp
{
    public static bool HasOpenActions(QaCase entity) =>
        entity.CorrectiveActions.Any(x => QaActionStatuses.IsOpen(x.Status));

    public static bool HasActiveHold(QaCase entity)
    {
        var logical = entity.LogicalHolds.Any(x =>
            string.Equals(x.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase));
        var physical = IvQty.Round(
            entity.PhysicalHolds
                .Where(x => string.Equals(x.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.ReservedBaseQty)
            - entity.PhysicalHolds.SelectMany(x => x.Allocations)
                .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                .Sum(x => x.Qty));
        return logical || physical > 0m;
    }

    public static bool HasSealedAllocation(QaCase entity) =>
        entity.Grants.SelectMany(x => x.Allocations).Any(Posted)
        || entity.FgGrants.SelectMany(x => x.Allocations).Any(Posted)
        || entity.NonGoodGrants.SelectMany(x => x.Allocations).Any(Posted)
        || entity.PhysicalHolds.SelectMany(x => x.Allocations).Any(Posted);

    public static bool CanReinspect(QaCase entity) =>
        !HasActiveHold(entity) && !HasSealedAllocation(entity)
        && (string.Equals(entity.Status, QaCaseStatuses.Rejected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.Status, QaCaseStatuses.Approved, StringComparison.OrdinalIgnoreCase));

    public static decimal DecisionQty(QaCaseLine line, string actionType) =>
        IvQty.Round(line.Dispositions
            .Where(x => string.Equals(x.Kind, actionType, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Qty));

    public static decimal AssignedQty(QaCase entity, QaCaseLine line, string actionType, int? excludeActionNo = null) =>
        IvQty.Round(entity.CorrectiveActions
            .Where(x => (x.CaseLineId == line.Uid || ReferenceEquals(x.CaseLine, line))
                && string.Equals(x.ActionType, actionType, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(x.Status, QaActionStatuses.Cancelled, StringComparison.OrdinalIgnoreCase)
                && (excludeActionNo is null || x.ActionNo != excludeActionNo))
            .Sum(x => x.Qty));

    public static string? ValidateNewQty(QaCase entity, QaCaseLine line, string actionType, decimal qty)
    {
        if (!QaActionTypes.IsQuantitative(actionType))
        {
            return qty == 0m ? null : "This follow-up type does not use quantity.";
        }

        var requested = IvQty.Round(qty);
        if (requested <= 0m)
        {
            return "Follow-up quantity must be greater than zero.";
        }

        var remaining = IvQty.Round(DecisionQty(line, actionType) - AssignedQty(entity, line, actionType));
        return requested > remaining
            ? "Follow-up quantity cannot exceed the remaining reject, rework, or hold decision quantity."
            : null;
    }

    public static string Snapshot(IReadOnlyList<QaDispositionDto> dispositions) =>
        string.Join("; ", dispositions
            .Where(x => x.Qty > 0m)
            .Select(x => $"{x.Kind}:{IvQty.Round(x.Qty):0.####}"));

    public static void AppendAttempt(
        QaCase entity,
        QaCaseLine line,
        string inspector,
        decimal inspectedQty,
        string result,
        IReadOnlyList<QaDispositionDto> dispositions,
        string? notes,
        string? measuredValues,
        string? reason)
    {
        var next = (line.InspectionAttempts.Concat(entity.InspectionAttempts.Where(x =>
                x.CaseLineId == line.Uid || ReferenceEquals(x.CaseLine, line)))
            .Select(x => x.AttemptNo)
            .DefaultIfEmpty(0)
            .Max()) + 1;
        var attempt = new QaInspectionAttempt
        {
            Case = entity,
            CaseLine = line,
            AttemptNo = next,
            Result = result.Trim().ToUpperInvariant(),
            Inspector = inspector,
            InspectedAtUtc = DateTime.UtcNow,
            InspectedQty = inspectedQty,
            Notes = notes,
            MeasuredValues = measuredValues,
            DispositionSnapshot = Snapshot(dispositions),
            SourceIdentity = line.SourceIdentity,
            SourceHeaderId = entity.SourceHeaderId,
            ProductionBalLotId = entity.ProductionBalLotId,
            Reason = reason
        };
        entity.InspectionAttempts.Add(attempt);
        line.InspectionAttempts.Add(attempt);
    }

    public static void EnsureApprovedActions(QaCase entity, string user)
    {
        foreach (var line in entity.Lines)
        {
            foreach (var kind in new[] { QaActionTypes.Reject, QaActionTypes.Rework, QaActionTypes.Hold })
            {
                var qty = DecisionQty(line, kind);
                if (qty <= 0m)
                {
                    continue;
                }

                if (AssignedQty(entity, line, kind) > 0m)
                {
                    continue;
                }

                AddAction(entity, line, kind, qty, user, "Created from the inspection decision. This is a QA task, not a stock posting.");
            }
        }
    }

    public static QaCorrectiveAction AddAction(
        QaCase entity,
        QaCaseLine line,
        string actionType,
        decimal qty,
        string user,
        string? reason,
        string? assignedTo = null,
        string? assignedDepartment = null,
        DateTime? dueDate = null,
        string? remarks = null,
        Guid? requestId = null)
    {
        var action = new QaCorrectiveAction
        {
            Case = entity,
            CaseLine = line,
            ActionNo = NextActionNo(entity),
            ActionType = actionType.Trim().ToUpperInvariant(),
            Qty = IvQty.Round(qty),
            Status = QaActionStatuses.Open,
            AssignedTo = assignedTo,
            AssignedDepartment = assignedDepartment,
            Reason = reason,
            DueDate = dueDate,
            Remarks = remarks,
            RequestId = requestId,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = user
        };
        entity.CorrectiveActions.Add(action);
        line.CorrectiveActions.Add(action);
        return action;
    }

    private static int NextActionNo(QaCase entity) =>
        (entity.CorrectiveActions.Select(x => x.ActionNo).DefaultIfEmpty(0).Max()) + 1;

    private static bool Posted(QaReceiptAllocation x) =>
        string.Equals(x.Status, QaReceiptAllocationStatuses.Posted, StringComparison.OrdinalIgnoreCase);

    private static bool Posted(QaFgAllocation x) =>
        string.Equals(x.Status, QaReceiptAllocationStatuses.Posted, StringComparison.OrdinalIgnoreCase);

    private static bool Posted(QaNonGoodAllocation x) =>
        string.Equals(x.Status, QaReceiptAllocationStatuses.Posted, StringComparison.OrdinalIgnoreCase);

    private static bool Posted(QaPhysicalAllocation x) =>
        string.Equals(x.Status, QaReceiptAllocationStatuses.Posted, StringComparison.OrdinalIgnoreCase);
}
