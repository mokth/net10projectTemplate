namespace ErpWeb.Model.Repositories.Inventory;

/// <summary>A posted history row that is chronologically later for a given BalLoc.</summary>
public sealed record IvLaterMovementHit(
    int BalLocId,
    int HistoryId,
    int BatchNo,
    DateTime TrxDtTime,
    string ICode,
    string? Warehouse,
    string? LotNo);
