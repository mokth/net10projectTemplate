using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Quality;

namespace ErpWeb.Core.Quality;

public static class QaDestinationComposer
{
    public static IReadOnlyList<QaDestinationDto> Compose(QaCase entity)
    {
        var destinations = new List<QaDestinationDto>();
        var caseNo = entity.CaseNo;
        var returnUrl = CaseHref(caseNo);
        var remaining = RemainingQty(entity);
        var rejected = RejectedQty(entity);

        if (Is(entity.SourceType, QaSourceTypes.IncomingPreGrn))
        {
            AddPosted(
                destinations,
                entity.Grants.SelectMany(x => x.Allocations),
                x => x.BatchNo,
                QaDestinationKinds.GoodsReceipt,
                "Goods receipt",
                "Linked to a sealed posting of the existing goods receipt. Quality did not create a second stock batch.",
                "The linked goods receipt was reversed. Remaining accepted qty can be posted again on a new document generation.",
                batch => $"/inventory/goods-receipts/view/{batch}");
            if (remaining > 0m && IsApproved(entity))
            {
                destinations.Add(Navigate(
                    QaDestinationKinds.GoodsReceipt,
                    "Open goods receipt",
                    QaDestinationStatuses.Suggested,
                    "Suggested",
                    "Create and post the goods receipt here for remaining accepted qty. Quality does not post stock.",
                    GrnNewHref(entity, caseNo, returnUrl),
                    remaining));
            }

            if (rejected > 0m)
            {
                destinations.Add(Blocked(
                    QaDestinationKinds.VendorReturn,
                    "Vendor return is not available",
                    QaDestinationStatuses.NotStock,
                    "Not inventory",
                    "Rejected-before-GRN qty never entered stock. Do not open a vendor return, scrap, or purchase credit note for it.",
                    rejected));
            }
        }
        else if (Is(entity.SourceType, QaSourceTypes.FgStagingLot))
        {
            AddPosted(
                destinations,
                entity.FgGrants.SelectMany(x => x.Allocations),
                x => x.BatchId,
                QaDestinationKinds.FinishedGoodReceipt,
                "Finished-good receipt",
                "Linked to a sealed finished-good receipt. Quality did not create a second inventory receipt.",
                "The linked finished-good receipt was reversed. Remaining accepted qty can be posted on the new generation.",
                batch => $"/planning/finished-good-receipts/{batch}/view");
            if (remaining > 0m && IsApproved(entity))
            {
                destinations.Add(Navigate(
                    QaDestinationKinds.FinishedGoodReceipt,
                    "Open finished-good receipt",
                    QaDestinationStatuses.Suggested,
                    "Suggested",
                    "Create and post the finished-good receipt here for remaining accepted qty. Quality does not post stock.",
                    FgNewHref(entity, caseNo, returnUrl),
                    remaining));
            }

            if (rejected > 0m)
            {
                destinations.Add(Blocked(
                    QaDestinationKinds.LaterPhase,
                    "Rejected FG staging stays in staging",
                    QaDestinationStatuses.NotStock,
                    "Not inventory",
                    "Rejected finished-good qty stays in FG staging. It is not inventory and is not a scrap posting.",
                    rejected));
            }
        }
        else if (Is(entity.SourceType, QaSourceTypes.PostedGrn) && IsApproved(entity))
        {
            AddPhysicalPosted(
                destinations,
                entity,
                IvTrxTypes.VendorReturn,
                QaDestinationKinds.VendorReturn,
                "Vendor return",
                "Linked to a sealed vendor return of reserved reject qty. Quality did not create a second stock batch.",
                batch => $"/inventory/vendor-return/view/{batch}");
            if (rejected > 0m)
            {
                destinations.Add(Navigate(
                    QaDestinationKinds.VendorReturn,
                    "Open vendor return",
                    QaDestinationStatuses.LinkedUnverified,
                    "Linked, unverified",
                    "Navigation only. Posting stays on the vendor-return document. This is not a QCHOLD conversion.",
                    $"/inventory/vendor-return/new?qaCase={Uri.EscapeDataString(caseNo)}&returnUrl={Uri.EscapeDataString(returnUrl)}",
                    rejected));
                destinations.Add(Navigate(
                    QaDestinationKinds.PurchaseCreditNote,
                    "Open purchase credit note",
                    QaDestinationStatuses.LinkedUnverified,
                    "Linked, unverified",
                    "If this return is owned by a purchase credit note, change it from that credit note. Do not post the vendor return separately.",
                    $"/purchase/credit-notes/new?qaCase={Uri.EscapeDataString(caseNo)}&returnUrl={Uri.EscapeDataString(returnUrl)}",
                    rejected));
            }

            AddReservation(destinations, entity);
        }
        else if (Is(entity.SourceType, QaSourceTypes.InventoryStock) && IsApproved(entity))
        {
            AddPhysicalPosted(
                destinations,
                entity,
                IvTrxTypes.Scrap,
                QaDestinationKinds.InventoryScrap,
                "Inventory scrap",
                "Linked to a sealed scrap of reserved reject qty. Quality did not create a second stock batch.",
                batch => $"/inventory/scrap/view/{batch}");
            if (rejected > 0m)
            {
                destinations.Add(Navigate(
                    QaDestinationKinds.InventoryScrap,
                    "Open inventory scrap",
                    QaDestinationStatuses.LinkedUnverified,
                    "Linked, unverified",
                    "Navigation only. Posting stays on the scrap document. Transfer is not a quality release.",
                    $"/inventory/scrap/new?qaCase={Uri.EscapeDataString(caseNo)}&returnUrl={Uri.EscapeDataString(returnUrl)}",
                    rejected));
            }

            AddReservation(destinations, entity);
        }
        else if (Is(entity.SourceType, QaSourceTypes.ProductionOutputNonGood))
        {
            foreach (var group in entity.NonGoodGrants.SelectMany(x => x.Allocations)
                         .GroupBy(x => x.ProductionOutputId)
                         .OrderBy(x => x.Key))
            {
                var posted = IvQty.Round(group
                    .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                    .Sum(x => x.Qty));
                var reversed = IvQty.Round(group
                    .Where(x => x.Status == QaReceiptAllocationStatuses.Reversed)
                    .Sum(x => x.Qty));
                if (posted > 0m)
                {
                    destinations.Add(Navigate(
                        QaDestinationKinds.DailyProduction,
                        "Daily Production",
                        QaDestinationStatuses.Executed,
                        "Executed",
                        "Linked to a sealed Daily Production posting of scrap/reject/hold counters. Quality did not create a valued lot.",
                        $"/planning/daily-production/view/{group.Key}",
                        posted,
                        group.Key.ToString()));
                }
                else if (reversed > 0m)
                {
                    destinations.Add(Navigate(
                        QaDestinationKinds.DailyProduction,
                        "Daily Production",
                        QaDestinationStatuses.Reversed,
                        "Reversed",
                        "The linked Daily Production was reversed. Remaining accepted counter qty can be posted on the new generation.",
                        $"/planning/daily-production/view/{group.Key}",
                        reversed,
                        group.Key.ToString()));
                }
            }

            destinations.Add(Blocked(
                QaDestinationKinds.DailyProduction,
                "Non-good qty is not inventory",
                QaDestinationStatuses.NotStock,
                "Not inventory",
                "Scrap, reject, and hold qty stay Daily Production counters. This is not a valued lot, not inventory scrap, and not a QCHOLD conversion.",
                IvQty.Round(entity.Lines.Sum(x => x.Qty))));
        }

        return destinations;
    }

    public static string Summary(IReadOnlyList<QaDestinationDto> destinations)
    {
        if (destinations.Count == 0)
        {
            return "None";
        }

        return string.Join(" · ", destinations.Select(x => $"{x.Caption} ({x.StatusLabel})"));
    }

    public static bool ReachedInspection(QaCase entity) =>
        entity.InspectedAtUtc.HasValue
        || entity.Inspections.Count > 0
        || entity.Lines.Any(x => x.InspectedQty > 0m)
        || Is(entity.Status, QaCaseStatuses.InInspection)
        || Is(entity.Status, QaCaseStatuses.PendingReview);

    public static bool IsFirstPass(QaCase entity) =>
        ReachedInspection(entity)
        && RejectedQty(entity) == 0m
        && (Is(entity.Status, QaCaseStatuses.Approved) || Is(entity.Status, QaCaseStatuses.Closed));

    public static int AgingDays(QaCase entity, DateTime asOfDate)
    {
        if (Is(entity.Status, QaCaseStatuses.Closed) || Is(entity.Status, QaCaseStatuses.Cancelled))
        {
            return 0;
        }

        return Math.Max(0, (asOfDate.Date - entity.CaseDate.Date).Days);
    }

    private static void AddPosted<T>(
        List<QaDestinationDto> destinations,
        IEnumerable<T> allocations,
        Func<T, int> batchOf,
        string kind,
        string caption,
        string executedGuidance,
        string reversedGuidance,
        Func<int, string> href)
        where T : class
    {
        var groups = allocations
            .Select(x => new
            {
                Batch = batchOf(x),
                Qty = QtyOf(x),
                Status = StatusOf(x)
            })
            .GroupBy(x => x.Batch)
            .OrderBy(x => x.Key);

        foreach (var group in groups)
        {
            var posted = IvQty.Round(group
                .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                .Sum(x => x.Qty));
            var reversed = IvQty.Round(group
                .Where(x => x.Status == QaReceiptAllocationStatuses.Reversed)
                .Sum(x => x.Qty));
            if (posted > 0m)
            {
                destinations.Add(Navigate(
                    kind,
                    caption,
                    QaDestinationStatuses.Executed,
                    "Executed",
                    executedGuidance,
                    href(group.Key),
                    posted,
                    group.Key.ToString()));
            }
            else if (reversed > 0m)
            {
                destinations.Add(Navigate(
                    kind,
                    caption,
                    QaDestinationStatuses.Reversed,
                    "Reversed",
                    reversedGuidance,
                    href(group.Key),
                    reversed,
                    group.Key.ToString()));
            }
        }
    }

    private static void AddPhysicalPosted(
        List<QaDestinationDto> destinations,
        QaCase entity,
        string trxType,
        string kind,
        string caption,
        string executedGuidance,
        Func<int, string> href)
    {
        var groups = entity.PhysicalHolds
            .SelectMany(x => x.Allocations)
            .Where(x => string.Equals(x.TrxType, trxType, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.BatchNo)
            .OrderBy(x => x.Key);

        foreach (var group in groups)
        {
            var posted = IvQty.Round(group
                .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                .Sum(x => x.Qty));
            var reversed = IvQty.Round(group
                .Where(x => x.Status == QaReceiptAllocationStatuses.Reversed)
                .Sum(x => x.Qty));
            if (posted > 0m)
            {
                destinations.Add(Navigate(
                    kind,
                    caption,
                    QaDestinationStatuses.Executed,
                    "Executed",
                    executedGuidance,
                    href(group.Key),
                    posted,
                    group.Key.ToString()));
            }
            else if (reversed > 0m)
            {
                destinations.Add(Navigate(
                    kind,
                    caption,
                    QaDestinationStatuses.Reversed,
                    "Reversed",
                    "The linked quality stock-out was reversed. Remaining reserved qty can be claimed again.",
                    href(group.Key),
                    reversed,
                    group.Key.ToString()));
            }
        }
    }

    private static void AddReservation(List<QaDestinationDto> destinations, QaCase entity)
    {
        var remaining = RemainingPhysical(entity);
        if (remaining <= 0m)
        {
            return;
        }

        destinations.Add(Blocked(
            QaDestinationKinds.LaterPhase,
            "Physical warehouse reservation",
            QaDestinationStatuses.LinkedUnverified,
            "Reserved",
            "Hold and reject qty are reserved on the source warehouse pile. Available qty is on-hand minus reserved. This is not a QCHOLD conversion and not a transfer.",
            remaining));
    }

    internal static decimal RemainingPhysical(QaCase entity)
    {
        var reserved = IvQty.Round(entity.PhysicalHolds
            .Where(x => string.Equals(x.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.ReservedBaseQty));
        var posted = IvQty.Round(entity.PhysicalHolds.SelectMany(x => x.Allocations)
            .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
            .Sum(x => x.Qty));
        return IvQty.Round(Math.Max(0m, reserved - posted));
    }

    private static decimal QtyOf<T>(T allocation) => allocation switch
    {
        QaReceiptAllocation receipt => receipt.Qty,
        QaFgAllocation fg => fg.Qty,
        QaPhysicalAllocation physical => physical.Qty,
        QaNonGoodAllocation nonGood => nonGood.Qty,
        _ => 0m
    };

    private static string StatusOf<T>(T allocation) => allocation switch
    {
        QaReceiptAllocation receipt => receipt.Status,
        QaFgAllocation fg => fg.Status,
        QaPhysicalAllocation physical => physical.Status,
        QaNonGoodAllocation nonGood => nonGood.Status,
        _ => string.Empty
    };

    private static QaDestinationDto Navigate(
        string kind,
        string caption,
        string status,
        string statusLabel,
        string guidance,
        string href,
        decimal qty,
        string? documentNo = null) => new()
    {
        Kind = kind,
        Caption = caption,
        Status = status,
        StatusLabel = statusLabel,
        Guidance = guidance,
        Href = href,
        DocumentNo = documentNo,
        Qty = qty,
        CanNavigate = true
    };

    private static QaDestinationDto Blocked(
        string kind,
        string caption,
        string status,
        string statusLabel,
        string guidance,
        decimal qty) => new()
    {
        Kind = kind,
        Caption = caption,
        Status = status,
        StatusLabel = statusLabel,
        Guidance = guidance,
        Qty = qty,
        CanNavigate = false
    };

    private static bool Is(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsApproved(QaCase entity) =>
        Is(entity.Status, QaCaseStatuses.Approved) || Is(entity.Status, QaCaseStatuses.Closed);

    internal static decimal RemainingQty(QaCase entity)
    {
        decimal accepted;
        decimal posted;
        if (entity.NonGoodGrants.Count > 0)
        {
            accepted = IvQty.Round(entity.NonGoodGrants.Sum(x => x.AcceptedQty));
            posted = IvQty.Round(entity.NonGoodGrants.SelectMany(x => x.Allocations)
                .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                .Sum(x => x.Qty));
        }
        else if (entity.FgGrants.Count > 0)
        {
            accepted = IvQty.Round(entity.FgGrants.Sum(x => x.AcceptedQty));
            posted = IvQty.Round(entity.FgGrants.SelectMany(x => x.Allocations)
                .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                .Sum(x => x.Qty));
        }
        else if (entity.Grants.Count > 0)
        {
            accepted = IvQty.Round(entity.Grants.Sum(x => x.AcceptedQty));
            posted = IvQty.Round(entity.Grants.SelectMany(x => x.Allocations)
                .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                .Sum(x => x.Qty));
        }
        else
        {
            accepted = IvQty.Round(entity.Lines.SelectMany(x => x.Dispositions)
                .Where(x => string.Equals(x.Kind, QaDispositionKinds.Accept, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Qty));
            posted = 0m;
        }

        return IvQty.Round(Math.Max(0m, accepted - posted));
    }

    internal static decimal RejectedQty(QaCase entity)
    {
        if (entity.NonGoodGrants.Count > 0)
        {
            return IvQty.Round(entity.NonGoodGrants.Sum(x => x.RejectedQty));
        }

        if (entity.FgGrants.Count > 0)
        {
            return IvQty.Round(entity.FgGrants.Sum(x => x.RejectedQty));
        }

        if (entity.Grants.Count > 0)
        {
            return IvQty.Round(entity.Grants.Sum(x => x.RejectedQty));
        }

        return IvQty.Round(entity.Lines.SelectMany(x => x.Dispositions)
            .Where(x => string.Equals(x.Kind, QaDispositionKinds.Reject, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Qty));
    }

    private static string CaseHref(string caseNo) => $"/quality/cases/view/{Uri.EscapeDataString(caseNo)}";

    private static string GrnNewHref(QaCase entity, string caseNo, string returnUrl)
    {
        var href = $"/inventory/goods-receipts/new?qaCase={Uri.EscapeDataString(caseNo)}&returnUrl={Uri.EscapeDataString(returnUrl)}";
        if (!string.IsNullOrWhiteSpace(entity.PoNo))
        {
            href += $"&poNo={Uri.EscapeDataString(entity.PoNo)}";
        }

        if (entity.PoRelNo is short rel)
        {
            href += $"&poRelNo={rel}";
        }

        return href;
    }

    private static string FgNewHref(QaCase entity, string caseNo, string returnUrl)
    {
        var href = $"/planning/finished-good-receipts/new?qaCase={Uri.EscapeDataString(caseNo)}&returnUrl={Uri.EscapeDataString(returnUrl)}";
        if (entity.ProductionBalLotId is long lot)
        {
            href += $"&lotId={lot}";
        }

        if (!string.IsNullOrWhiteSpace(entity.WorkOrderNo))
        {
            href += $"&workOrderNo={Uri.EscapeDataString(entity.WorkOrderNo)}";
        }

        return href;
    }
}

public static class QaOpenedFromCase
{
    public static string? Banner(string? caseNo)
    {
        var no = (caseNo ?? string.Empty).Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(no)
            ? null
            : $"Opened from quality case {no}. Quality does not post this document and does not create lines.";
    }
}
