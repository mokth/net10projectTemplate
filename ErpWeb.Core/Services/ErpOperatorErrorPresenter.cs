using System.Globalization;
using System.Text;
using ErpWeb.Core.StockLedger;

namespace ErpWeb.Core.Services;

/// <summary>
/// Turns a structured stock/costing failure into operator language.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure and deterministic.</b> No database, no clock, no random identifier, no dependency on
/// <c>AppDbContext</c>, tenancy or Blazor. The same context always produces the same text, which is
/// what makes the presentation testable and keeps it out of the posting decision.
/// </para>
/// <para>
/// <b>Classification is structured, never textual.</b> The family comes from
/// <see cref="StockOperatorFailureContext.Family"/> when a producer supplied one, otherwise from the
/// persisted code combined with the attempted action and the replay phase. No error string is ever
/// parsed to recover an event id, an item, a document or a quantity.
/// </para>
/// <para>
/// <b>Truthful under uncertainty.</b> A value the producer did not prove is omitted rather than
/// guessed: there are no <c>{item}</c>-style placeholders, no invented decimals, no invented document
/// numbers and no claim about which document owns a timeline identifier.
/// </para>
/// <para>
/// This type must never change a <c>Succeeded</c> flag, a status transition, an amount or a stored
/// quantity. It only decides what a failure reads like once the business code has already refused it.
/// </para>
/// </remarks>
public static class ErpOperatorErrorPresenter
{
    /// <summary>Maximum length of a single producer-supplied value shown to an operator.</summary>
    public const int MaxValueLength = 80;

    /// <summary>Maximum number of detail rows. Keeps the "Why?" panel readable on a phone.</summary>
    public const int MaxDetails = 12;

    /// <summary>Maximum number of related-document links.</summary>
    public const int MaxRelatedDocuments = 5;

    /// <summary>Operator date format. ISO dates are reserved for logs and diagnostics.</summary>
    public const string DateFormat = "dd/MM/yyyy";

    /// <summary>
    /// Maps one failure to operator text. A null context still produces a truthful generic message
    /// rather than a blank or an exception.
    /// </summary>
    public static ErpOperatorError Present(StockOperatorFailureContext? context)
    {
        var failure = context ?? new StockOperatorFailureContext();
        var family = ResolveFamily(failure);
        var target = TargetPhrase(failure);
        var verb = Verb(failure.Action);

        return new ErpOperatorError
        {
            PresentationCode = PresentationCode(family, failure),
            Title = $"Cannot {verb} {target}.",
            Explanation = Explain(family, failure),
            NextStep = Next(family, failure),
            ReferenceId = Safe(failure.ReferenceId, 40),
            MayRetryAfterRefresh = MayRetryAfterRefresh(family),
            Details = BuildDetails(family, failure),
            RelatedDocuments = BuildRelatedDocuments(failure)
        };
    }

    // ─────────────────────────────── classification ───────────────────────────────

    /// <summary>
    /// Structured family first, then the code default. Exposed for tests so every persisted code has
    /// a provable, documented destination.
    /// </summary>
    public static ErpErrorFamily ResolveFamily(StockOperatorFailureContext? context)
    {
        var failure = context ?? new StockOperatorFailureContext();
        if (failure.Family is ErpErrorFamily supplied)
        {
            return supplied;
        }

        return NormalizeCode(failure.TechnicalCode) switch
        {
            StockLedgerErrorCodes.LedgerDisabled => ErpErrorFamily.UnsupportedReplay,
            StockLedgerErrorCodes.StockBusy => ErpErrorFamily.StockBusy,
            StockLedgerErrorCodes.RequestIdReused => ErpErrorFamily.AlreadyProcessed,
            StockLedgerErrorCodes.DocumentAlreadyPosted => ErpErrorFamily.AlreadyProcessed,
            StockLedgerErrorCodes.StaleDocument => ErpErrorFamily.StaleDocument,
            StockLedgerErrorCodes.ClosedPeriod => ErpErrorFamily.ClosedPeriod,
            StockLedgerErrorCodes.BackdatedStockEvent => ErpErrorFamily.UnsupportedReplay,
            StockLedgerErrorCodes.CountScopeFrozen => ErpErrorFamily.CountLocked,
            StockLedgerErrorCodes.InvalidStockIdentity => ErpErrorFamily.StockIdentityUnverified,
            // The one code that genuinely means two different things. Rollback and cancel act on
            // history; a plain post means current availability. A supplied replay phase means history.
            StockLedgerErrorCodes.InsufficientBaseQty => IsHistorical(failure)
                ? ErpErrorFamily.HistoricalNegativeStock
                : ErpErrorFamily.StockShortageNow,
            StockLedgerErrorCodes.InvalidUomConversion => ErpErrorFamily.UomConversion,
            StockLedgerErrorCodes.ReversalDependency => ErpErrorFamily.DependentDocument,
            StockLedgerErrorCodes.ReversalAlreadyExists => ErpErrorFamily.AlreadyProcessed,
            StockLedgerErrorCodes.LedgerMismatch => ErpErrorFamily.ReconciliationRequired,
            StockLedgerErrorCodes.LegacyLineageUnverified => ErpErrorFamily.ReconciliationRequired,
            StockLedgerErrorCodes.HistoryBeforeCutover => ErpErrorFamily.UnsupportedReplay,
            StockLedgerErrorCodes.ValuationRequired => ErpErrorFamily.MissingReceiptCost,
            StockLedgerErrorCodes.CountSnapshotChanged => ErpErrorFamily.StaleDocument,
            StockLedgerErrorCodes.CostPolicyMissing => ErpErrorFamily.CostMethodSetup,
            StockLedgerErrorCodes.CostPolicyInvalid => ErpErrorFamily.CostMethodSetup,
            StockLedgerErrorCodes.CostMethodNotImplemented => ErpErrorFamily.CostMethodSetup,
            _ => ErpErrorFamily.UnknownFailure
        };
    }

    /// <summary>
    /// True when the attempted action can only be judged against history. Rollback and cancel remove
    /// or rewind an existing event, and a supplied replay phase is itself historical evidence.
    /// </summary>
    public static bool IsHistorical(StockOperatorFailureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RejectionPhase != ErpRejectionPhase.NotSpecified
            || context.Action is ErpErrorAction.Rollback or ErpErrorAction.Cancel;
    }

    // ─────────────────────────────── headline ───────────────────────────────

    private static string Verb(ErpErrorAction action) => action switch
    {
        ErpErrorAction.Post => "post",
        ErpErrorAction.Rollback => "roll back",
        ErpErrorAction.Cancel => "cancel",
        ErpErrorAction.Save => "save",
        ErpErrorAction.Delete => "delete",
        ErpErrorAction.StockCount => "complete",
        ErpErrorAction.Reconcile => "reconcile",
        _ => "complete"
    };

    /// <summary>
    /// Names the document only when the producer supplied a known kind (and number). An unknown kind
    /// never becomes free text, so a stored code such as <c>IV_GR</c> cannot reach an operator.
    /// </summary>
    private static string TargetPhrase(StockOperatorFailureContext context)
    {
        var label = DocumentLabel(context.DocumentKind);
        var number = Safe(context.DocumentNumber, 24);
        if (label is not null)
        {
            return number is null ? $"this {label.ToLowerInvariant()}" : $"{label} {number}";
        }

        return number is null
            ? context.Action == ErpErrorAction.Unknown ? "this action" : "this record"
            : $"document {number}";
    }

    /// <summary>Operator-facing document name, or null when the kind is unknown.</summary>
    public static string? DocumentLabel(ErpDocumentKind kind) => kind switch
    {
        ErpDocumentKind.GoodsReceipt => "Goods Receipt",
        ErpDocumentKind.NonStockGoodsReceipt => "Non-Stock Goods Receipt",
        ErpDocumentKind.MiscellaneousIssue => "Miscellaneous Issue",
        ErpDocumentKind.MiscellaneousReceipt => "Miscellaneous Receipt",
        ErpDocumentKind.StockTransfer => "Stock Transfer",
        ErpDocumentKind.StockAdjustment => "Stock Adjustment",
        ErpDocumentKind.StockReturn => "Stock Return",
        ErpDocumentKind.VendorReturn => "Vendor Return",
        ErpDocumentKind.Scrap => "Scrap",
        ErpDocumentKind.StockCount => "Stock Count",
        ErpDocumentKind.DeliveryOrder => "Delivery Order",
        ErpDocumentKind.SalesInvoice => "Sales Invoice",
        ErpDocumentKind.SalesCreditDebitNote => "Credit/Debit Note",
        ErpDocumentKind.PurchaseInvoice => "Purchase Invoice",
        ErpDocumentKind.PurchaseCreditDebitNote => "Purchase Credit/Debit Note",
        ErpDocumentKind.PurchaseOrder => "Purchase Order",
        ErpDocumentKind.WorkOrder => "Work Order",
        ErpDocumentKind.WorkOrderTransfer => "Work Order Transfer",
        ErpDocumentKind.MaterialIssue => "Material Issue",
        ErpDocumentKind.DailyProduction => "Daily Production",
        ErpDocumentKind.FinishedGoodReceipt => "Finished Good Receipt",
        ErpDocumentKind.ProductionStockReturn => "Production Stock Return",
        ErpDocumentKind.ProductionStockAdjustment => "Production Stock Adjustment",
        _ => null
    };

    // ─────────────────────────────── reason ───────────────────────────────

    private static string Explain(ErpErrorFamily family, StockOperatorFailureContext context) => family switch
    {
        ErpErrorFamily.HistoricalNegativeStock => ExplainHistorical(context),
        ErpErrorFamily.StockShortageNow => ExplainShortage(context),
        ErpErrorFamily.MissingReceiptCost => WithItem(
            "The cost of this stock movement could not be verified", context, "."),
        ErpErrorFamily.CostStateNeedsReview => WithItem(
            "The stock quantity and the cost records do not agree", context, "."),
        ErpErrorFamily.UnsupportedReplay =>
            "This older transaction cannot be corrected automatically with the current stock costing setup.",
        ErpErrorFamily.DependentDocument => context.Action is ErpErrorAction.Rollback or ErpErrorAction.Cancel
            ? "A later stock or costing transaction depends on this record."
            : "A related stock or costing transaction depends on this record.",
        ErpErrorFamily.ClosedPeriod => ExplainClosedPeriod(context),
        ErpErrorFamily.FutureDocumentDate => ExplainFutureDate(context),
        ErpErrorFamily.UomConversion => ExplainUomConversion(context),
        ErpErrorFamily.CurrencyRate => ExplainCurrencyRate(context),
        ErpErrorFamily.StaleDocument => "This record or its stock balance changed while you were working.",
        ErpErrorFamily.AlreadyProcessed => ExplainAlreadyProcessed(context),
        ErpErrorFamily.CountLocked =>
            "Stock changes are temporarily blocked for this warehouse or location because a stock count is in progress.",
        ErpErrorFamily.PermissionDenied => "You do not have permission to perform this action.",
        ErpErrorFamily.CostMethodSetup => ExplainCostMethod(context),
        ErpErrorFamily.ReconciliationRequired => "The related stock or costing records need reconciliation.",
        ErpErrorFamily.StockBusy => "Another stock transaction is being processed for this branch.",
        ErpErrorFamily.StockIdentityUnverified =>
            "The stock record, or your company and branch selection, could not be verified.",
        _ => "The action could not be completed."
    };

    private static string ExplainHistorical(StockOperatorFailureContext context)
    {
        var removed = context.Action is ErpErrorAction.Rollback or ErpErrorAction.Cancel;
        return context.RejectionPhase switch
        {
            ErpRejectionPhase.Removing => removed
                ? "Removing this record would make the stock balance at its original date fall below zero."
                : "This transaction would make the stock balance at that date fall below zero.",
            ErpRejectionPhase.Rewinding =>
                "A later stock movement for this item cannot be reversed against the current stock balance.",
            _ => HistoricalReplayText(context, removed)
        };
    }

    private static string HistoricalReplayText(StockOperatorFailureContext context, bool removed)
    {
        var basis = removed
            ? "Removing this record would cause insufficient historical stock when a later transaction is applied"
            : "Recording this transaction would leave insufficient stock when a later transaction is applied";
        return WithDate(basis, context.AffectedTransactionDate, ".");
    }

    private static string ExplainShortage(StockOperatorFailureContext context)
    {
        var production = context.Origin == ErpErrorOrigin.Production
            && context.Action is ErpErrorAction.Post or ErpErrorAction.Save;
        if (production)
        {
            var code = Safe(context.ItemCode, 40);
            return code is null
                ? "There is not enough eligible stock to issue these materials to Production."
                : $"There is not enough eligible stock to issue {code} to Production.";
        }

        return WithItem("There is not enough available stock for", context, ".");
    }

    private static string ExplainClosedPeriod(StockOperatorFailureContext context)
    {
        var date = OperatorDate(context.DocumentDate);
        var closedThrough = OperatorDate(context.ClosedThrough);
        if (date is not null && closedThrough is not null)
        {
            return $"A transaction dated {date} falls in a closed inventory period (closed through {closedThrough}).";
        }

        return date is not null
            ? $"A transaction dated {date} falls in a closed inventory period."
            : "This date falls in a closed inventory period.";
    }

    private static string ExplainFutureDate(StockOperatorFailureContext context)
    {
        var date = OperatorDate(context.DocumentDate);
        return date is not null
            ? $"A stock transaction dated {date} cannot be posted yet."
            : "This stock transaction cannot be posted yet because its date is in the future.";
    }

    private static string ExplainUomConversion(StockOperatorFailureContext context)
    {
        var from = Safe(context.FromUom, 16);
        var to = Safe(context.ToUom, 16);
        var conversion = from is not null && to is not null
            ? $"No approved conversion from {from} to {to} is available"
            : "An approved unit conversion is missing";
        return WithItem(conversion, context, ".");
    }

    private static string ExplainCurrencyRate(StockOperatorFailureContext context)
    {
        var currency = Safe(context.CurrencyCode, 8);
        var home = Safe(context.HomeCurrencyCode, 8);
        var date = OperatorDate(context.DocumentDate);
        if (currency is not null && home is not null)
        {
            return date is null
                ? $"No approved exchange rate from {currency} to {home} covers this date."
                : $"No approved exchange rate from {currency} to {home} covers {date}.";
        }

        if (currency is not null)
        {
            return date is null
                ? $"No approved exchange rate for {currency} covers this date."
                : $"No approved exchange rate for {currency} covers {date}.";
        }

        return date is null
            ? "No approved exchange rate covers this date."
            : $"No approved exchange rate covers {date}.";
    }

    private static string ExplainAlreadyProcessed(StockOperatorFailureContext context) =>
        NormalizeCode(context.TechnicalCode) switch
        {
            StockLedgerErrorCodes.DocumentAlreadyPosted => "This document is already posted.",
            StockLedgerErrorCodes.ReversalAlreadyExists => "This transaction has already been reversed.",
            StockLedgerErrorCodes.RequestIdReused => "This request may already have been processed.",
            _ => "This document has already been processed."
        };

    private static string ExplainCostMethod(StockOperatorFailureContext context)
    {
        var invalid = NormalizeCode(context.TechnicalCode) == StockLedgerErrorCodes.CostPolicyInvalid;
        var basis = invalid
            ? "The costing settings are conflicting or incomplete"
            : "No valid costing method is configured";
        var date = OperatorDate(context.DocumentDate);
        return date is null ? $"{basis}." : $"{basis} for {date}.";
    }

    // ─────────────────────────────── guidance ───────────────────────────────

    private static string Next(ErpErrorFamily family, StockOperatorFailureContext context) => family switch
    {
        ErpErrorFamily.HistoricalNegativeStock => context.RejectionPhase switch
        {
            ErpRejectionPhase.Removing => "Review the stock history for that date before retrying.",
            ErpRejectionPhase.Rewinding => "Review the later stock movements for this item.",
            _ => "Review the later stock movements for this item before retrying."
        },
        ErpErrorFamily.StockShortageNow =>
            "Select another eligible stock location or reduce the quantity, then try again.",
        ErpErrorFamily.MissingReceiptCost =>
            "Review the original receipt, or ask an authorised costing user to check Costing Center.",
        ErpErrorFamily.CostStateNeedsReview =>
            "Ask an authorised costing user to check the costing health. Do not adjust quantities to bypass it.",
        ErpErrorFamily.UnsupportedReplay =>
            "Ask the inventory manager to review the permitted correction process.",
        ErpErrorFamily.DependentDocument =>
            "Review the affected document and the supported correction options.",
        ErpErrorFamily.ClosedPeriod => "Ask an authorised period administrator to review the date.",
        ErpErrorFamily.FutureDocumentDate =>
            "Save it as NEW and post it when the business date reaches that date.",
        ErpErrorFamily.UomConversion => "Check the item's unit conversion settings.",
        ErpErrorFamily.CurrencyRate =>
            "Ask the authorised finance administrator to enter the exchange rate.",
        ErpErrorFamily.StaleDocument => "Refresh, review the latest information and try again.",
        ErpErrorFamily.AlreadyProcessed =>
            "Refresh to see the current status. Do not submit again.",
        ErpErrorFamily.CountLocked => "Finish or release the stock count first.",
        ErpErrorFamily.PermissionDenied =>
            "Ask an authorised user to review your access rights.",
        ErpErrorFamily.CostMethodSetup =>
            "Ask the costing administrator to check the costing settings for that date.",
        ErpErrorFamily.ReconciliationRequired =>
            "Give the reference to your administrator, or ask an authorised inventory manager to check Costing Center.",
        ErpErrorFamily.StockBusy =>
            "Check the current document status, then try again if it is still needed.",
        ErpErrorFamily.StockIdentityUnverified =>
            "Refresh the session, or check the selected warehouse, location, lot and status.",
        _ => "Refresh and check the document status. If the problem continues, give the reference to your administrator."
    };

    /// <summary>
    /// Advisory refresh hint. Deliberately narrow: only a branch lock (the action provably did not
    /// run) and a stale document (the version check refused it) are safe to describe as "try again".
    /// </summary>
    private static bool MayRetryAfterRefresh(ErpErrorFamily family) =>
        family is ErpErrorFamily.StockBusy or ErpErrorFamily.StaleDocument;

    // ─────────────────────────────── details ───────────────────────────────

    private static IReadOnlyList<ErpOperatorErrorDetail> BuildDetails(
        ErpErrorFamily family,
        StockOperatorFailureContext context)
    {
        var details = new List<ErpOperatorErrorDetail>();

        void Add(string label, string? value, bool monetary = false)
        {
            if (details.Count >= MaxDetails)
            {
                return;
            }

            var safe = Safe(value, MaxValueLength);
            if (safe is null || (monetary && !context.CanViewCost))
            {
                return;
            }

            if (details.Any(x => x.Label.Equals(label, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            details.Add(new ErpOperatorErrorDetail(label, safe, monetary));
        }

        Add("Item", ItemText(context));
        Add("Line", context.LineNo?.ToString(CultureInfo.InvariantCulture));
        Add("Warehouse", context.Warehouse);
        Add("Location", context.Location);
        Add("Lot", context.LotNo);
        Add("Stock status", context.StockStatus);
        Add("Document date", OperatorDate(context.DocumentDate));

        if (family is ErpErrorFamily.HistoricalNegativeStock or ErpErrorFamily.DependentDocument)
        {
            Add("Affected transaction", AffectedDocumentText(context));
            Add("Affected transaction date", AffectedDateOnly(context));
        }

        if (family is ErpErrorFamily.StockShortageNow or ErpErrorFamily.HistoricalNegativeStock)
        {
            var uom = Safe(context.Uom, 16);
            Add("Available", QuantityText(context.Available, uom));
            Add("Required", QuantityText(context.Required, uom));
            Add("Shortage", QuantityText(context.Shortage, uom));
        }

        foreach (var extra in context.ExtraDetails ?? [])
        {
            if (extra is null)
            {
                continue;
            }

            Add(extra.Label, extra.Value, extra.IsMonetary);
        }

        return details;
    }

    private static IReadOnlyList<ErpRelatedDocumentHint> BuildRelatedDocuments(
        StockOperatorFailureContext context)
    {
        var linked = new List<ErpRelatedDocumentHint>();
        foreach (var hint in context.RelatedDocuments ?? [])
        {
            if (linked.Count >= MaxRelatedDocuments || hint is null)
            {
                continue;
            }

            var type = Safe(hint.DocumentType, 40);
            var number = Safe(hint.DocumentNumber, 24);
            if (type is null || number is null)
            {
                continue;
            }

            linked.Add(new ErpRelatedDocumentHint(type, number, Safe(hint.TargetRoute, 200)));
        }

        return linked;
    }

    private static string? ItemText(StockOperatorFailureContext context)
    {
        var code = Safe(context.ItemCode, 40);
        var description = Safe(context.ItemDescription, 60);
        return (code, description) switch
        {
            (null, null) => null,
            (null, _) => description,
            (_, null) => code,
            _ => $"{code} — {description}"
        };
    }

    private static string? AffectedDocumentText(StockOperatorFailureContext context)
    {
        var label = DocumentLabel(context.AffectedDocumentKind);
        var number = Safe(context.AffectedDocumentNumber, 24);
        if (label is null || number is null)
        {
            return null;
        }

        var date = OperatorDate(context.AffectedTransactionDate);
        return date is null ? $"{label} {number}" : $"{label} {number}, dated {date}";
    }

    private static string? AffectedDateOnly(StockOperatorFailureContext context) =>
        AffectedDocumentText(context) is null ? OperatorDate(context.AffectedTransactionDate) : null;

    // ─────────────────────────────── formatting ───────────────────────────────

    private static string PresentationCode(ErpErrorFamily family, StockOperatorFailureContext context)
    {
        var origin = context.Origin switch
        {
            ErpErrorOrigin.Inventory => "INVENTORY",
            ErpErrorOrigin.Sales => "SALES",
            ErpErrorOrigin.Purchase => "PURCHASE",
            ErpErrorOrigin.Production => "PRODUCTION",
            _ => "STOCK"
        };
        var key = UpperSnake(family.ToString());
        if (family == ErpErrorFamily.HistoricalNegativeStock
            && context.RejectionPhase != ErpRejectionPhase.NotSpecified)
        {
            key += "_" + UpperSnake(context.RejectionPhase.ToString());
        }

        return $"{origin}.{key}";
    }

    private static string UpperSnake(string value)
    {
        var builder = new StringBuilder(value.Length + 6);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (char.IsUpper(current) && i > 0 && value[i - 1] is not '_')
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(current));
        }

        return builder.ToString();
    }

    /// <summary>Operator date. Returns null for a missing or default value rather than 01/01/0001.</summary>
    public static string? OperatorDate(DateTime? value) =>
        value is { } date && date != default
            ? date.ToString(DateFormat, CultureInfo.InvariantCulture)
            : null;

    /// <summary>Quantity with the item's base UOM, up to the inventory scale of four decimals.</summary>
    public static string? QuantityText(decimal? value, string? uom)
    {
        if (value is not { } quantity)
        {
            return null;
        }

        var text = quantity.ToString("#,0.####", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(uom) ? text : $"{text} {uom.Trim()}";
    }

    private static string WithItem(string basis, StockOperatorFailureContext context, string suffix)
    {
        var code = Safe(context.ItemCode, 40);
        var description = Safe(context.ItemDescription, 60);
        if (code is not null && description is not null)
        {
            return $"{basis} for {code} ({description}){suffix}";
        }

        return code is not null ? $"{basis} for {code}{suffix}" : $"{basis}{suffix}";
    }

    private static string WithDate(string basis, DateTime? date, string suffix)
    {
        var text = OperatorDate(date);
        return text is null ? $"{basis}{suffix}" : $"{basis} on {text}{suffix}";
    }

    /// <summary>
    /// Collapses whitespace and caps length. A producer value is data — it may contain a newline or a
    /// pasted stack trace fragment — so it is never echoed verbatim into a toast.
    /// </summary>
    public static string? Safe(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        var collapsed = builder.ToString().Trim();
        if (collapsed.Length == 0)
        {
            return null;
        }

        if (maxLength > 0 && collapsed.Length > maxLength)
        {
            collapsed = collapsed[..(maxLength - 1)].TrimEnd() + "…";
        }

        return collapsed;
    }

    private static string? NormalizeCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
}
