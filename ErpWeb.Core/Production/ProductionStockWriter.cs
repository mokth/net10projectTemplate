using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

public sealed record ProductionStockLeg(
    ProductionBalLot Balance,
    string MovementType,
    decimal Qty,
    decimal BaseQty,
    long WorkOrderId,
    long? WorkOrderMaterialId,
    long? WorkOrderOperationId,
    long? RouteStepId,
    long PostingLinkId,
    string DocumentType,
    string DocumentNo,
    string SourceLineId,
    int SplitOrdinal,
    long? OriginalMovementId = null,
    int? InventoryHistoryId = null,
    decimal? ExactTotalValue = null,
    string ValuationStatus = "UNVALUED");

public interface IProductionStockWriter
{
    Task<IReadOnlyList<ProductionBalLotMovement>> ApplyAsync(
        StockPostingContext context,
        IReadOnlyCollection<ProductionStockLeg> legs,
        CancellationToken cancellationToken = default);
}

/// <summary>Only V2 production quantity mutator. It appends facts and updates projections without committing.</summary>
public sealed class ProductionStockWriter(IStockMovementRegistry registry) : IProductionStockWriter
{
    public Task<IReadOnlyList<ProductionBalLotMovement>> ApplyAsync(
        StockPostingContext context,
        IReadOnlyCollection<ProductionStockLeg> legs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(legs);
        context.EnsureUnsealed();
        cancellationToken.ThrowIfCancellationRequested();

        var movements = new List<ProductionBalLotMovement>(legs.Count);
        var lineNo = 0;
        foreach (var leg in legs
            .OrderBy(x => x.Balance.Uid)
            .ThenBy(x => x.SourceLineId, StringComparer.Ordinal)
            .ThenBy(x => x.SplitOrdinal))
        {
            Validate(context, leg);
            var definition = registry.GetRequired(leg.MovementType);
            var roundedBase = IvQty.Round(leg.BaseQty);
            var nextBase = IvQty.Round(leg.Balance.BaseQty + definition.Direction * roundedBase);
            if (nextBase < 0m)
                throw new StockLedgerException(new(
                    StockLedgerErrorCodes.InsufficientBaseQty,
                    $"Production balance {leg.Balance.Uid} would become negative."));

            var factor = leg.Balance.ConversionFactorToBase;
            var nextQty = IvQty.Round(nextBase / factor);
            var value = leg.ExactTotalValue is decimal exact
                ? StockLedgerPrecision.Money(exact)
                : StockLedgerPrecision.Money(roundedBase * leg.Balance.AverageUnitCost);
            if (value < 0m) throw new InvalidOperationException("Transferred value cannot be negative.");
            if (leg.ExactTotalValue.HasValue)
            {
                var nextValue = StockLedgerPrecision.Money(leg.Balance.TotalCost + definition.Direction * value);
                if (nextValue < 0m || (nextBase == 0m && nextValue != 0m))
                    throw new InvalidOperationException("Production value would become negative or stranded.");
                leg.Balance.TotalCost = nextValue;
                leg.Balance.AverageUnitCost = nextBase > 0m ? StockLedgerPrecision.Money(nextValue / nextBase) : 0m;
            }
            leg.Balance.BaseQty = nextBase;
            leg.Balance.Qty = nextQty;
            leg.Balance.LastMovementDate = context.Posting.EffectiveAt;
            leg.Balance.LastStockEventEffectiveAt = context.Posting.EffectiveAt;

            var movement = new ProductionBalLotMovement
            {
                ProductionBalLotId = leg.Balance.Uid,
                MovementType = definition.Code,
                Qty = IvQty.Round(leg.Qty),
                Uom = leg.Balance.Uom,
                BaseQty = roundedBase,
                BaseUom = leg.Balance.BaseUom,
                UnitCost = StockLedgerPrecision.Money(value / roundedBase),
                TotalCost = value,
                WorkOrderId = leg.WorkOrderId,
                WorkOrderMaterialId = leg.WorkOrderMaterialId,
                WorkOrderOperationId = leg.WorkOrderOperationId,
                RouteStepId = leg.RouteStepId,
                PostingLinkId = leg.PostingLinkId,
                OriginalMovementId = leg.OriginalMovementId,
                LedgerVersion = 2,
                LedgerEpochId = context.Epoch.Id,
                StockPostingId = context.Posting.Id,
                PostingLineNo = ++lineNo,
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                ItemCode = leg.Balance.ItemCode,
                ItemDescription = leg.Balance.Description,
                BalanceStage = leg.Balance.BalanceStage,
                ProductionLocationId = leg.Balance.ProductionLocationId,
                StockStatusCode = leg.Balance.StockStatusCode,
                WorkOrderNo = leg.Balance.WorkOrderNo,
                LotIdentity = leg.Balance.PoolCode ?? leg.Balance.LotNo,
                PhysicalLotNo = leg.Balance.PhysicalLotNo,
                ConversionFactorToBase = factor,
                SourceLineId = leg.SourceLineId,
                SplitOrdinal = leg.SplitOrdinal,
                InventoryHistoryId = leg.InventoryHistoryId,
                ValuationStatus = leg.ValuationStatus,
                DocumentType = leg.DocumentType,
                DocumentNo = leg.DocumentNo,
                MovementDate = context.Posting.EffectiveAt,
                CreatedDate = context.Posting.PostedAtUtc,
                CreatedBy = context.UserId,
            };
            context.Db.ProductionBalLotMovements.Add(movement);
            movements.Add(movement);
        }

        return Task.FromResult<IReadOnlyList<ProductionBalLotMovement>>(movements);
    }

    private static void Validate(StockPostingContext context, ProductionStockLeg leg)
    {
        if (leg.BaseQty <= 0m || leg.Qty <= 0m || leg.Balance.ConversionFactorToBase <= 0m)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.InvalidUomConversion,
                "Production stock quantities and conversion factor must be positive."));
        if (!string.Equals(leg.Balance.CompanyCode, context.CompanyCode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(leg.Balance.BranchCode, context.BranchCode, StringComparison.OrdinalIgnoreCase))
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.InvalidStockIdentity,
                "Production balance does not belong to the posting branch."));
        if (string.IsNullOrWhiteSpace(leg.SourceLineId))
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.InvalidStockIdentity,
                "A stable source line identity is required."));
    }
}
