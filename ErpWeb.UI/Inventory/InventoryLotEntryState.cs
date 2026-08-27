namespace ErpWeb.UI.Inventory;

public enum LotEntryChange
{
    Keep,
    Clear,
    NewLot
}

public sealed record LotEntryResult(LotEntryChange Change, string? LotNo = null);

/// <summary>
/// Item-scoped lot-entry decisions for inventory receive/entry popups.
/// Lot and expiry belong to the selected ICode; never carry them across items.
/// </summary>
public sealed class InventoryLotEntryState
{
    public string? ICode { get; private set; }

    public void Reset() => ICode = null;

    public void InitializeExisting(string? iCode) => ICode = Normalize(iCode);

    public bool IsSameItem(string? iCode)
    {
        var a = Normalize(iCode);
        var b = ICode;
        if (a is null && b is null)
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public LotEntryResult SelectItem(string? iCode, bool lotControl, Func<string> nextLotNo)
    {
        ArgumentNullException.ThrowIfNull(nextLotNo);

        var selected = Normalize(iCode);
        if (selected is null)
        {
            return new LotEntryResult(LotEntryChange.Keep);
        }

        if (!lotControl)
        {
            ICode = selected;
            return new LotEntryResult(LotEntryChange.Clear);
        }

        if (IsSameItem(selected))
        {
            return new LotEntryResult(LotEntryChange.Keep);
        }

        ICode = selected;
        var lotNo = nextLotNo();
        return new LotEntryResult(LotEntryChange.NewLot, lotNo);
    }

    private static string? Normalize(string? iCode)
    {
        if (string.IsNullOrWhiteSpace(iCode))
        {
            return null;
        }

        return iCode.Trim();
    }
}
