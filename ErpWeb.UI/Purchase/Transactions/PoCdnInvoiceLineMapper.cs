using ErpWeb.Core.Purchase;

namespace ErpWeb.UI.Purchase.Transactions;

/// <summary>
/// Pure field copy from a verified purchase-invoice picker row onto a CN/DN line view-model.
/// No arithmetic, remaining-qty formulas, tax, FX, or stock costing.
/// </summary>
public static class PoCdnInvoiceLineMapper
{
    public static PoCdnInvoiceLineMapResult TryMap(PoCdnInvoiceLinePickerRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var reason = DescribeBlock(row);
        if (reason is not null)
        {
            return PoCdnInvoiceLineMapResult.Blocked(row.Line, reason);
        }

        return PoCdnInvoiceLineMapResult.Mapped(row.Line, CreateLine(row));
    }

    /// <summary>
    /// Maps every selected row or rejects the whole selection. Never returns a partial list.
    /// </summary>
    public static PoCdnInvoiceLineMapBatchResult MapSelection(IReadOnlyList<PoCdnInvoiceLinePickerRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var mapped = new List<PoCdnLineVm>(rows.Count);
        var blocked = new List<PoCdnInvoiceLineMapResult>();
        foreach (var row in rows)
        {
            var result = TryMap(row);
            if (!result.IsSupported)
            {
                blocked.Add(result);
                continue;
            }

            mapped.Add(result.Line!);
        }

        if (blocked.Count > 0)
        {
            return PoCdnInvoiceLineMapBatchResult.Rejected(blocked);
        }

        return PoCdnInvoiceLineMapBatchResult.Accepted(mapped);
    }

    private static string? DescribeBlock(PoCdnInvoiceLinePickerRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.UnsupportedSourceFields))
        {
            return $"Invoice line {row.Line} cannot be imported ({row.UnsupportedSourceFields}).";
        }

        if (string.IsNullOrWhiteSpace(row.ICode))
        {
            return $"Invoice line {row.Line} has no item code.";
        }

        if (row.StdCustPSize <= 0m)
        {
            return $"Invoice line {row.Line} has no usable pack size.";
        }

        if (row.ItemDiscount2 != 0m)
        {
            return $"Invoice line {row.Line} uses ItemDiscount2, which this note cannot store.";
        }

        if (row.ItemDiscount3 != 0m)
        {
            return $"Invoice line {row.Line} uses ItemDiscount3, which this note cannot store.";
        }

        return null;
    }

    private static PoCdnLineVm CreateLine(PoCdnInvoiceLinePickerRow row) => new()
    {
        ClientKey = Guid.NewGuid().ToString("N"),
        InvLineNo = row.Line,
        ICode = row.ICode,
        IDesc = row.IDesc,
        Qty = row.Qty,
        UnitPrice = row.UnitPrice,
        ItemDiscount = row.ItemDiscount,
        ItemDiscount1 = row.ItemDiscount1,
        IDiscountType = row.IDiscountType,
        IDiscountType1 = row.IDiscountType1,
        IsInclusive = row.IsInclusive,
        TaxGroup = row.TaxGroup,
        ItemGlCode = row.ItemGlCode,
        Classification = row.Classification,
        StdCustPSize = row.StdCustPSize,
        PoNo = row.PoNo,
        PoRelNo = row.PoRelNo,
        PoLineNo = row.PoLineNo,
        StdUom = row.StdUom,
        CostPrice = 0m,
        IsStockReturn = false,
        FromBalLocId = null,
        FrWarehouse = null,
        LocCode = null,
        IStatus = null,
        LotNo = null,
        ExpiryDate = null,
        BalLocDisplay = null,
        AvailableQty = 0m,
        LotControl = false,
        Remarks = null,
        LineRemainingStdQty = row.RemainingStdQty,
        LineRemainingAmount = row.RemainingAmount
    };
}

public sealed class PoCdnInvoiceLineMapResult
{
    public bool IsSupported { get; init; }
    public short SourceLine { get; init; }
    public string? BlockReason { get; init; }
    public PoCdnLineVm? Line { get; init; }

    public static PoCdnInvoiceLineMapResult Mapped(short sourceLine, PoCdnLineVm line) => new()
    {
        IsSupported = true,
        SourceLine = sourceLine,
        Line = line
    };

    public static PoCdnInvoiceLineMapResult Blocked(short sourceLine, string reason) => new()
    {
        IsSupported = false,
        SourceLine = sourceLine,
        BlockReason = reason
    };
}

public sealed class PoCdnInvoiceLineMapBatchResult
{
    public bool IsSupported { get; init; }
    public IReadOnlyList<PoCdnLineVm> Lines { get; init; } = [];
    public IReadOnlyList<PoCdnInvoiceLineMapResult> Blocked { get; init; } = [];

    public string? ErrorMessage =>
        Blocked.Count == 0
            ? null
            : string.Join(" ", Blocked.Select(x => x.BlockReason).Where(x => !string.IsNullOrWhiteSpace(x)));

    public static PoCdnInvoiceLineMapBatchResult Accepted(IReadOnlyList<PoCdnLineVm> lines) => new()
    {
        IsSupported = true,
        Lines = lines
    };

    public static PoCdnInvoiceLineMapBatchResult Rejected(IReadOnlyList<PoCdnInvoiceLineMapResult> blocked) => new()
    {
        IsSupported = false,
        Blocked = blocked
    };
}
