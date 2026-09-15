namespace ErpWeb.Core.Sales;

// ============================================================================================
// Sales item family — DTOs and natural keys.
//
// Plan: plans/sales-item-family-v2-plan.md (§7 price contract, §8 discount contract, §8.8 keys).
// Money is decimal(18,4) on the DTO surface; SaItemCust.UnitPrice is scaled explicitly because the
// live legacy column is `float`.
// ============================================================================================

// ---------- Keys ----------

/// <summary>Natural key of a price list (single-part).</summary>
public sealed class IvCustPriceGroupKey
{
    public string CustPriceCode { get; init; } = string.Empty;
}

/// <summary>Natural key of one price-list line.</summary>
public sealed class IvCustPriceKey
{
    public string CustPriceCode { get; init; } = string.Empty;
    public string ICode { get; init; } = string.Empty;
    public string UOM { get; init; } = string.Empty;
}

/// <summary>Natural key of a customer-item row (one row per customer/item/UOM/MOQ band).</summary>
public sealed class SaItemCustKey
{
    public string CustCode { get; init; } = string.Empty;
    public string ICode { get; init; } = string.Empty;
    public string SellingUOM { get; init; } = string.Empty;
    public int MOQ { get; init; }
}

/// <summary>Key of an item discount rule (surrogate identity).</summary>
public sealed class SaDisGroupItemKey
{
    public int Id { get; init; }
}

/// <summary>
/// Bulk delete token for the item family. <see cref="Key"/> is the encoded natural key
/// (";"-separated, and ";" is rejected inside every code by <c>ValidateAndNormalizeCode</c>, so the
/// encoding cannot be ambiguous).
/// </summary>
public sealed class SaItemFamilyKeyToken
{
    public string Key { get; init; } = string.Empty;
    public byte[] RowVersion { get; init; } = [];
}

// ---------- IvCustPriceGroup + IvCustPrice ----------

public sealed class IvCustPriceGroupListRow
{
    public string CustPriceCode { get; init; } = string.Empty;
    public string? CustPriceDesc { get; init; }
    public int LineCount { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];

    /// <summary>Grid key and bulk-delete token.</summary>
    public string Key => CustPriceCode;
}

public sealed class IvCustPriceLineVm
{
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string UOM { get; set; } = string.Empty;

    /// <summary>Tax-exclusive; NULL when the caller may not view prices (VIEW_PRICE).</summary>
    public decimal? SellingPrice { get; set; }

    public decimal? SellPackSize { get; set; }

    /// <summary>Key inside one price group (the child grid keys on this).</summary>
    public string Key => $"{ICode};{UOM}";
}

public sealed class IvCustPriceGroupEditVm
{
    public string CustPriceCode { get; set; } = string.Empty;
    public string? CustPriceDesc { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[] RowVersion { get; set; } = [];
    public List<IvCustPriceLineVm> Lines { get; set; } = [];
}

public sealed class IvCustPriceListRow
{
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string UOM { get; init; } = string.Empty;
    public decimal? SellingPrice { get; init; }
    public decimal? SellPackSize { get; init; }

    /// <summary>Grid key within one price group.</summary>
    public string Key => $"{ICode};{UOM}";
}

// ---------- SaItemCust ----------

public sealed class SaItemCustListRow
{
    public string CustCode { get; init; } = string.Empty;
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string CustICode { get; init; } = string.Empty;
    public string SellingUOM { get; init; } = string.Empty;
    public int MOQ { get; init; }
    public decimal? UnitPrice { get; init; }
    public string? Currency { get; init; }
    public string? Status { get; init; }
    public byte[] RowVersion { get; init; } = [];

    /// <summary>Natural key in the "-"-free encoded form used by the bulk-delete token.</summary>
    public string Key => $"{CustCode};{ICode};{SellingUOM};{MOQ}";
}

public sealed class SaItemCustEditVm
{
    public string CustCode { get; set; } = string.Empty;
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string CustICode { get; set; } = string.Empty;
    public string? InvDesc { get; set; }
    public string SellingUOM { get; set; } = string.Empty;
    public int MOQ { get; set; }

    /// <summary>Tax-exclusive; NULL when the caller may not view prices (VIEW_PRICE).</summary>
    public decimal? UnitPrice { get; set; }

    public string? Currency { get; set; }
    public decimal? StdCustPSize { get; set; }
    public string? Status { get; set; }
    public string? DG { get; set; }
    public string? SG { get; set; }
    public string? ProjID { get; set; }
    public string? CustModel { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

// ---------- SaDisGroupItem ----------

public sealed class SaDisGroupItemListRow
{
    public int Id { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? IClass { get; init; }
    public decimal QtyFr { get; init; }
    public decimal QtyTo { get; init; }
    public DateTime DateFr { get; init; }
    public DateTime? DateTo { get; init; }
    public decimal? Discount { get; init; }
    public string? DiscountType { get; init; }
    public decimal? Discount1 { get; init; }
    public string? DiscountType1 { get; init; }
    public string? EffectPrice { get; init; }
    public byte[] RowVersion { get; init; } = [];

    /// <summary>Grid key and bulk-delete token (surrogate identity).</summary>
    public string Key => Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class SaDisGroupItemEditVm
{
    public int Id { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? IClass { get; set; }
    public decimal QtyFr { get; set; }
    public decimal QtyTo { get; set; }
    public DateTime DateFr { get; set; }
    public DateTime? DateTo { get; set; }
    public decimal? Discount { get; set; }
    public string? DiscountType { get; set; }
    public decimal? Discount1 { get; set; }
    public string? DiscountType1 { get; set; }
    public string? EffectPrice { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Slot types shared by both discount slots (legacy free text: PERCENTAGE | AMOUNT).</summary>
public static class SaDiscountSlotTypes
{
    public const string Percentage = "PERCENTAGE";
    public const string Amount = "AMOUNT";

    public static bool IsValid(string? value, bool required) =>
        string.IsNullOrWhiteSpace(value)
            ? !required
            : value.Trim().Equals(Percentage, StringComparison.OrdinalIgnoreCase) ||
              value.Trim().Equals(Amount, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Effect-price tokens. Stored and validated, but inert until a dealer price source exists.</summary>
public static class SaEffectPriceOptions
{
    public const string Dealer = "DEALER";
    public const string Selling = "SELLING";

    public static bool IsValid(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Trim().Equals(Dealer, StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals(Selling, StringComparison.OrdinalIgnoreCase);
}
