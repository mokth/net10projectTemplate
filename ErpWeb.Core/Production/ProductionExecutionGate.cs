using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Production;

/// <summary>
/// Milestone 6 gate. Execution posting stays closed until the reservation, issue, WIP, receipt,
/// costing, and completion contracts in plan §11 are approved. Callers must stop here.
/// </summary>
public static class ProductionExecutionGate
{
    public const string BlockedCode = "WO_EXECUTION_NOT_APPROVED";

    public static bool IsOpen => false;

    public static IvMasterOperationResult<T> Block<T>(string action)
    {
        return IvMasterOperationResult<T>.Fail(
            IvMasterErrorCode.Validation,
            $"{BlockedCode}: {action} is blocked until the execution-posting contracts are approved.");
    }
}
