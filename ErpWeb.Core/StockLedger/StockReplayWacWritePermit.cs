using System.Collections.Immutable;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Immutable evidence captured only after a WAC plan survives root-lock
/// revalidation. Financial and physical writers consume it in that same root.
/// </summary>
public sealed class StockReplayWacWritePermit
{
    internal StockReplayWacWritePermit(
        StockPostingContext context,
        string itemCode,
        string planHash,
        decimal currentQuantity,
        decimal currentValue,
        decimal finalQuantity,
        decimal finalValue,
        int changedPileId,
        decimal changedPileFinalQuantity)
        : this(context, itemCode, planHash, currentQuantity, currentValue,
            finalQuantity, finalValue,
            ImmutableDictionary<int, decimal>.Empty.Add(
                changedPileId, changedPileFinalQuantity))
    {
    }

    internal StockReplayWacWritePermit(
        StockPostingContext context,
        string itemCode,
        string planHash,
        decimal currentQuantity,
        decimal currentValue,
        decimal finalQuantity,
        decimal finalValue,
        ImmutableDictionary<int, decimal> changedPiles)
    {
        CompanyCode = context.CompanyCode;
        BranchCode = context.BranchCode;
        LedgerEpochId = context.Epoch.Id;
        RootPostingId = context.Posting.Id;
        RootPostingSequence = context.Posting.PostingSequence;
        ItemCode = itemCode;
        PlanHash = planHash;
        CurrentQuantity = currentQuantity;
        CurrentValue = currentValue;
        FinalQuantity = finalQuantity;
        FinalValue = finalValue;
        ChangedPiles = changedPiles;
        if (changedPiles.Count == 1)
        {
            var changedPile = changedPiles.Single();
            ChangedPileId = changedPile.Key;
            ChangedPileFinalQuantity = changedPile.Value;
        }
    }

    public string CompanyCode { get; }
    public string BranchCode { get; }
    public long LedgerEpochId { get; }
    public long RootPostingId { get; }
    public long RootPostingSequence { get; }
    public string ItemCode { get; }
    public string PlanHash { get; }
    public decimal CurrentQuantity { get; }
    public decimal CurrentValue { get; }
    public decimal FinalQuantity { get; }
    public decimal FinalValue { get; }
    public int ChangedPileId { get; }
    public decimal ChangedPileFinalQuantity { get; }
    public ImmutableDictionary<int, decimal> ChangedPiles { get; }
}
