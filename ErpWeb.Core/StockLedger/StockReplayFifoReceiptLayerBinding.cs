namespace ErpWeb.Core.StockLedger;

/// <summary>Resolves the projected receipt's provisional layer key after insert.</summary>
internal static class StockReplayFifoReceiptLayerBinding
{
    internal static void Validate(StockReplayFifoInsertionPlan plan,
        long? receiptLayerId)
    {
        ValidatePlan(plan);
        if (receiptLayerId is null or <= 0 or long.MaxValue)
            throw Invalid("FIFO receipt correction needs its saved candidate layer.");
    }

    internal static void ValidatePlan(StockReplayFifoInsertionPlan plan)
    {
        StockReplayFifoInsertionPlanner.ValidatePreparedPlan(plan);
        if (plan.PhysicalPlan.Candidate.SignedDelta <= 0m
            || plan.Projection.CandidateReceipt?.LayerId != long.MaxValue)
            throw Invalid("FIFO receipt correction needs a projected candidate layer.");
    }

    internal static long Map(long projectedLayerId, long? receiptLayerId)
    {
        if (projectedLayerId == long.MaxValue)
            return receiptLayerId is > 0 and < long.MaxValue
                ? receiptLayerId.Value
                : throw Invalid("FIFO projected receipt layer has no saved identity.");
        return projectedLayerId;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
