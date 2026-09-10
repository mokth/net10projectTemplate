namespace ErpWeb.Core.Sales;

/// <summary>
/// Service-internal revision usage snapshot. Does not cross the Core/UI contract —
/// UI receives only <see cref="SaSoListRow.CanRevise"/> / <see cref="SaSoListRow.CanDelete"/>.
/// </summary>
internal sealed class SaSoRevisionUsage
{
    public bool HasDeliveredQty { get; init; }
    public bool HasInvoicedQty { get; init; }
    public bool HasShippedQty { get; init; }
    public bool HasAllocation { get; init; }
    public bool HasDraftDeliveryOrder { get; init; }
    public bool HasDraftInvoice { get; init; }
    public bool HasDeliveryOrderReference { get; init; }
    public bool HasInvoiceReference { get; init; }

    public bool IsUnused =>
        !(HasDeliveredQty || HasInvoicedQty || HasShippedQty
          || HasAllocation || HasDraftDeliveryOrder || HasDraftInvoice
          || HasDeliveryOrderReference || HasInvoiceReference);

    public static SaSoRevisionUsage Empty { get; } = new();

    /// <summary>
    /// Deterministic user-facing message for a used revision.
    /// Draft-only messages ignore the corresponding detail-reference flag (a draft always creates a ref).
    /// </summary>
    public string BlockMessage(SaSoUsageMutation mutation)
    {
        var revise = mutation == SaSoUsageMutation.Revise;
        if (IsDraftDeliveryOrderOnly())
        {
            return revise
                ? "This Sales Order cannot be revised because a draft Delivery Order is holding quantity on the current revision."
                : "This Sales Order cannot be deleted because a draft Delivery Order is holding quantity on the current revision.";
        }

        if (IsDraftInvoiceOnly())
        {
            return revise
                ? "This Sales Order cannot be revised because a draft Invoice is holding quantity on the current revision."
                : "This Sales Order cannot be deleted because a draft Invoice is holding quantity on the current revision.";
        }

        return revise
            ? "This Sales Order cannot be revised because the current revision is already in use."
            : "This Sales Order cannot be deleted because the current revision is already in use.";
    }

    /// <summary>Short reason fragment for multi-select list UX (no SO number prefix).</summary>
    public string ListBlockReason()
    {
        if (IsDraftDeliveryOrderOnly())
        {
            return "a draft Delivery Order is holding quantity";
        }

        if (IsDraftInvoiceOnly())
        {
            return "a draft Invoice is holding quantity";
        }

        return "already in use";
    }

    private bool IsDraftDeliveryOrderOnly() =>
        HasDraftDeliveryOrder
        && !HasDeliveredQty
        && !HasInvoicedQty
        && !HasShippedQty
        && !HasAllocation
        && !HasDraftInvoice
        && !HasInvoiceReference;

    private bool IsDraftInvoiceOnly() =>
        HasDraftInvoice
        && !HasDeliveredQty
        && !HasInvoicedQty
        && !HasShippedQty
        && !HasAllocation
        && !HasDraftDeliveryOrder
        && !HasDeliveryOrderReference;
}

internal enum SaSoUsageMutation
{
    Revise,
    Delete
}
