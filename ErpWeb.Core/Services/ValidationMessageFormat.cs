using System.Globalization;

namespace ErpWeb.Core.Services;

/// <summary>
/// Presentation-neutral formatting for the server-side validation error dictionary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract.</b> The field-keyed validation dictionary is the AUTHORITATIVE payload; the
/// accompanying message is a convenience headline. Nothing here may reduce, replace or silently drop
/// a dictionary entry, and no caller may read the headline instead of the dictionary.
/// </para>
/// <para>
/// <b>De-duplication.</b> Two different rows can legitimately carry identical message text
/// (<c>Lines[0].TaxGrCode</c> and <c>Lines[1].TaxGrCode</c> can both resolve to "Tax group 'SR' was
/// not found."), so raw messages are never collapsed. The headline composes
/// <c>label — message</c> per entry, which keeps rows distinguishable, and the panel renders every
/// key/value pair unconditionally.
/// </para>
/// <para>
/// <b>Ordering.</b> Every surface renders in the same deterministic order: document-level keys first
/// (alphabetical), then <c>Lines[n]</c> keys by index ascending. Dictionary enumeration order is
/// never relied upon.
/// </para>
/// <para>
/// Deliberately free of EF, tenancy and UI dependencies so it can be unit-tested directly.
/// </para>
/// </remarks>
public static class ValidationMessageFormat
{
    /// <summary>Last-resort text when neither the dictionary nor the server supplied anything.</summary>
    public const string GenericFallback = "Validation failed.";

    private const string LinePrefix = "Lines[";

    private const string FieldSeparator = " — ";

    private const string MessageSeparator = ": ";

    private static readonly Dictionary<string, string> KnownLabels =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ---- Sales documents ----
            ["CustCode"] = "Customer",
            ["InvDate"] = "Invoice date",
            ["SoDate"] = "Sales order date",
            ["DoDate"] = "Delivery order date",
            ["CustPo"] = "Customer PO",
            ["ValidUntil"] = "Valid until",
            ["PayCode"] = "Payment term",
            ["TaxGrCode"] = "Tax group",
            ["TaxGroup"] = "Tax group",
            ["TaxGlCode"] = "Tax GL",
            ["ArGlCode"] = "Customer AR GL",
            ["SalesmanCode"] = "Salesman",
            ["DueDate"] = "Due date",
            ["InvName"] = "Billing name",
            ["InvAddress1"] = "Billing address",
            ["InvCity"] = "Billing city",
            ["InvPostalCode"] = "Billing postal code",
            ["InvCountry"] = "Billing country",
            ["InvTel"] = "Telephone / email",
            ["InvEmail"] = "Email",
            ["BuyerTin"] = "Buyer TIN / BRN",
            ["BuyerBrn"] = "Buyer BRN",

            // ---- Purchase documents ----
            ["VendorCode"] = "Supplier",
            ["VendCode"] = "Supplier",
            ["VenCode"] = "Supplier",
            ["PoDate"] = "Order date",
            ["DocDate"] = "Document date",
            ["CurCode"] = "Currency",
            ["Requester"] = "Requester",
            ["PrType"] = "Requisition type",
            ["Type"] = "Document type",
            ["ReasonCode"] = "Reason",
            ["ReturnStock"] = "Return stock",
            ["CloseReason"] = "Close reason",
            ["RevisionReason"] = "Revision reason",
            ["SiRemark"] = "Remarks",
            ["SupplierDocNo"] = "Supplier document no.",
            ["SupplierDocDate"] = "Supplier document date",
            ["ExternalDocNo"] = "External document no.",
            ["PriceTolerance"] = "Price tolerance",

            // ---- Shared header fields ----
            ["Dept"] = "Department",
            ["DeptCode"] = "Department",
            ["ProjId"] = "Project",
            ["Remark"] = "Remarks",
            ["Remarks"] = "Remarks",
            ["Lines"] = "Lines",
            ["UnitPrice"] = "Unit price",
            ["Etd"] = "ETD",
            ["Eta"] = "ETA",

            // ---- Line fields ----
            ["ICode"] = "Item",
            ["IDesc"] = "Description",
            ["Qty"] = "Quantity",
            ["PoPurQty"] = "Quantity",
            ["PurchaseQty"] = "Quantity",
            ["FrWarehouse"] = "Warehouse",
            ["StdUom"] = "UOM",
            ["PurchaseUom"] = "UOM",
            ["SellingUom"] = "UOM",
            ["SellingGlCode"] = "Sales GL",
            ["PurchaseGlCode"] = "Purchase GL",
            ["ItemGlCode"] = "Item GL",
            ["Classification"] = "Classification",
            ["IsInclusive"] = "Tax inclusive",
            ["OneTime"] = "One-time item",
            ["OneTimeItemYn"] = "One-time item",
            ["RepairType"] = "Repair type",
            ["SoNo"] = "Sales order no.",
            ["SoLine"] = "Sales order line",
            ["DoNo"] = "Delivery order no.",
            ["DoLine"] = "Delivery order line",
            ["PoNo"] = "Purchase order no.",
            ["PoRelNo"] = "PO revision",
            ["PoLineNo"] = "PO line",
        };

    /// <summary>
    /// Deterministic display order: document-level entries first (alphabetical by key), then
    /// <c>Lines[n]</c> entries by index ascending. Blank keys are dropped; nothing else is filtered.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Order(
        IEnumerable<KeyValuePair<string, string>>? errors)
    {
        if (errors is null)
        {
            return [];
        }

        var staged = new List<(KeyValuePair<string, string> Entry, int Group, int Index)>();
        foreach (var entry in errors)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            var isLine = TryGetLineIndex(entry.Key, out var index);
            staged.Add((entry, isLine ? 1 : 0, isLine ? index : 0));
        }

        return staged
            .OrderBy(x => x.Group)
            .ThenBy(x => x.Index)
            .ThenBy(x => x.Entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Entry)
            .ToList();
    }

    /// <summary>
    /// Convenience headline: every message composed as <c>label — message</c> and joined. Never the
    /// generic fallback while the dictionary holds a non-blank message, and never de-duplicated.
    /// </summary>
    public static string BuildHeadline(
        IReadOnlyDictionary<string, string>? errors,
        string? serverMessage)
    {
        var parts = new List<string>();
        foreach (var entry in Order(errors))
        {
            var message = entry.Value?.Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            parts.Add(Label(entry.Key) + MessageSeparator + message);
        }

        if (parts.Count > 0)
        {
            return string.Join(" ", parts);
        }

        return string.IsNullOrWhiteSpace(serverMessage)
            ? GenericFallback
            : serverMessage.Trim();
    }

    /// <summary>
    /// Joins the raw messages (no labels, no de-duplication) for use as a service-side
    /// <c>ErrorMessage</c>. Convenience only — the dictionary remains the authoritative payload, so
    /// callers must still return it alongside this text. Falls back to <paramref name="fallback"/>,
    /// then to <see cref="GenericFallback"/>, so a caller can never surface the generic literal while
    /// the dictionary holds a real cause.
    /// </summary>
    public static string JoinMessages(
        IEnumerable<KeyValuePair<string, string>>? errors,
        string? fallback = null)
    {
        var messages = new List<string>();
        foreach (var entry in Order(errors))
        {
            var message = entry.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(message))
            {
                messages.Add(message);
            }
        }

        if (messages.Count > 0)
        {
            return string.Join(" ", messages);
        }

        return string.IsNullOrWhiteSpace(fallback) ? GenericFallback : fallback.Trim();
    }

    /// <summary>
    /// Picks the message a service should surface for a failed prepare step. A caller-supplied
    /// specific message — for example a documented reason code such as
    /// <c>SaDocAllocationReasonCodes.MixForbidden</c> — is a contract and always wins; only the
    /// generic literal is replaced by the concrete causes collected in the dictionary.
    /// </summary>
    public static string ResolveServiceMessage(
        IEnumerable<KeyValuePair<string, string>>? errors,
        string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)
            && !string.Equals(message.Trim(), GenericFallback, StringComparison.OrdinalIgnoreCase))
        {
            return message.Trim();
        }

        return JoinMessages(errors, message);
    }

    /// <summary>
    /// Every message for one grid row, in <see cref="Order"/> order. Matches <c>Lines[n]</c> and
    /// <c>Lines[n].Field</c> keys only — never a neighbouring row such as <c>Lines[10]</c> when asked
    /// for row 2. Returns all of them; multiples are never collapsed.
    /// </summary>
    public static IReadOnlyList<string> LineMessages(
        IReadOnlyDictionary<string, string>? errors,
        int lineNo)
    {
        if (errors is null || lineNo < 1)
        {
            return [];
        }

        var wanted = lineNo - 1;
        return Order(errors)
            .Where(entry => TryGetLineIndex(entry.Key, out var index) && index == wanted)
            .Select(entry => entry.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    /// <summary>
    /// Operator-facing label for a validation key. Known fields get a friendly name; anything else is
    /// returned verbatim so an unmapped key is still readable and greppable. Row-scoped keys become
    /// <c>Line {n} — field</c> using the 1-based row number.
    /// </summary>
    public static string Label(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var trimmed = key.Trim();
        if (TryGetLineIndex(trimmed, out var index))
        {
            var dot = trimmed.IndexOf('.');
            return dot >= 0 && dot < trimmed.Length - 1
                ? $"Line {index + 1}{FieldSeparator}{Known(trimmed[(dot + 1)..])}"
                : $"Line {index + 1}";
        }

        return Known(trimmed);
    }

    /// <summary>
    /// True when <paramref name="key"/> addresses a grid row, i.e. it is <c>Lines[n]</c> or
    /// <c>Lines[n].Field</c>. <paramref name="index"/> is the 0-based row index the server used.
    /// </summary>
    public static bool TryGetLineIndex(string? key, out int index)
    {
        index = 0;
        if (string.IsNullOrWhiteSpace(key)
            || !key.StartsWith(LinePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var close = key.IndexOf(']', LinePrefix.Length);
        if (close < 0)
        {
            return false;
        }

        var digits = key.AsSpan(LinePrefix.Length, close - LinePrefix.Length);
        return digits.Length > 0
            && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private static string Known(string field) =>
        KnownLabels.TryGetValue(field, out var label) ? label : field;
}
