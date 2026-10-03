namespace ErpWeb.Core.StockLedger;

public sealed record StockMovementDefinition(string Code, int Direction, string? InverseCode);

public interface IStockMovementRegistry
{
    StockMovementDefinition GetRequired(string code);
    IReadOnlyCollection<StockMovementDefinition> All { get; }
}

public sealed class StockMovementRegistry : IStockMovementRegistry
{
    private static readonly IReadOnlyDictionary<string, StockMovementDefinition> Definitions =
        CreateDefinitions();

    public IReadOnlyCollection<StockMovementDefinition> All => Definitions.Values.ToArray();

    public StockMovementDefinition GetRequired(string code)
    {
        if (Definitions.TryGetValue((code ?? string.Empty).Trim().ToUpperInvariant(), out var definition))
            return definition;

        throw new StockLedgerException(new(
            StockLedgerErrorCodes.InvalidStockIdentity,
            $"Unknown stock movement code '{code}'."));
    }

    private static IReadOnlyDictionary<string, StockMovementDefinition> CreateDefinitions()
    {
        var rows = new[]
        {
            new StockMovementDefinition("OPENING_IN", 1, null),
            new StockMovementDefinition("ISSUE", 1, "ISSUE_REVERSAL"),
            new StockMovementDefinition("ISSUE_REVERSAL", -1, "ISSUE"),
            new StockMovementDefinition("PRODUCE", 1, "PRODUCE_REVERSAL"),
            new StockMovementDefinition("PRODUCE_REVERSAL", -1, "PRODUCE"),
            new StockMovementDefinition("CONSUME", -1, "CONSUME_REVERSAL"),
            new StockMovementDefinition("CONSUME_REVERSAL", 1, "CONSUME"),
            new StockMovementDefinition("RETURN", -1, "RETURN_REVERSAL"),
            new StockMovementDefinition("RETURN_REVERSAL", 1, "RETURN"),
            new StockMovementDefinition("TRANSFER_OUT", -1, "TRANSFER_IN"),
            new StockMovementDefinition("TRANSFER_IN", 1, "TRANSFER_OUT"),
            new StockMovementDefinition("STATUS_OUT", -1, "STATUS_IN"),
            new StockMovementDefinition("STATUS_IN", 1, "STATUS_OUT"),
            new StockMovementDefinition("ADJUST_IN", 1, "ADJUST_OUT"),
            new StockMovementDefinition("ADJUST_OUT", -1, "ADJUST_IN"),
            new StockMovementDefinition("SCRAP_OUT", -1, null),
            new StockMovementDefinition("FG_RECEIPT_OUT", -1, null),
        };
        return rows.ToDictionary(x => x.Code, StringComparer.Ordinal);
    }
}
