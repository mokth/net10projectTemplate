using ErpWeb.Core.Purchase;
using ErpWeb.Core.Sales;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory.Lifecycle;

/// <summary>
/// Authoritative, read-only retirement dependency evaluator (plan §B4 / G2). It answers ONE question:
/// are there still live obligations that require this item to remain operational?
///
/// Rules that deliberately differ from <c>IvStockMasterService.EvaluateStructuralLockAsync</c>:
/// <list type="bullet">
/// <item>Historical posted invoices/history alone never block retirement.</item>
/// <item>Terminal documents (closed/cancelled/superseded/fully ordered) never block.</item>
/// <item>Quotations are reported as warnings only; they cannot create demand after discontinuation.</item>
/// <item>Zero quantity is not automatically "clear": unbalanced valuation and negative quantities block.</item>
/// </list>
/// Any failure to establish an answer fails closed.
/// </summary>
public sealed class ItemRetirementEvaluator
{
    public const int MaxSamplesPerCategory = 8;

    private const string QueryRoutePurchaseOrders = "/purchase/orders";
    private const string QueryRoutePurchaseRequests = "/purchase/requests";
    private const string QueryRouteSalesOrders = "/sales/orders";
    private const string QueryRouteDeliveryOrders = "/sales/delivery-orders";
    private const string QueryRouteWorkOrders = "/production/work-orders";
    private const string QueryRouteStockBalances = "/inventory/stock-balances";
    private const string QueryRouteInventoryTransactions = "/inventory/transactions";

    /// <summary>
    /// Evaluates every applicable retirement blocker for one company + item across all branches,
    /// warehouses, locations and lots. Never touches inventory, valuation or production balances.
    /// </summary>
    public async Task<ItemRetirementCheckResult> EvaluateAsync(
        AppDbContext db,
        string companyCode,
        string itemCode,
        DateTime evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (itemCode ?? string.Empty).Trim();
        if (company.Length == 0 || code.Length == 0)
        {
            return ItemRetirementCheckResult.Indeterminate(
                "Item retirement cannot be evaluated without a company and item code.",
                evaluatedAtUtc);
        }

        var blockers = new List<ItemRetirementBlocker>();
        var warnings = new List<ItemRetirementBlocker>();

        try
        {
            var balances = await EvaluateInventoryBalancesAsync(
                db, company, code, evaluatedAtUtc, blockers, cancellationToken);
            var value = await EvaluateValuationAsync(
                db, company, code, evaluatedAtUtc, blockers, cancellationToken);

            await EvaluateOpenInventoryAsync(db, company, code, evaluatedAtUtc, blockers, cancellationToken);
            await EvaluateProcurementAsync(db, company, code, evaluatedAtUtc, blockers, cancellationToken);
            await EvaluateSalesAsync(db, company, code, evaluatedAtUtc, blockers, warnings, cancellationToken);
            await EvaluateProductionAsync(db, company, code, evaluatedAtUtc, blockers, cancellationToken);
            await EvaluateReservationsAsync(db, company, code, evaluatedAtUtc, blockers, cancellationToken);

            return blockers.Count == 0
                ? ItemRetirementCheckResult.Retirable(balances, value, warnings, evaluatedAtUtc)
                : ItemRetirementCheckResult.Blocked(
                    blockers, warnings, balances, value, evaluatedAtUtc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed with an actionable diagnostic. Never assume a clean retirement.
            return ItemRetirementCheckResult.Indeterminate(
                $"Retirement checks could not be completed ({ex.GetType().Name}). The item cannot be retired until the check succeeds.",
                evaluatedAtUtc);
        }
    }

    private static ItemRetirementBlocker Blocker(
        string category,
        string sourceModule,
        string referenceType,
        string reason,
        DateTime evaluatedAtUtc,
        string? referenceNo = null,
        string? branch = null,
        decimal? quantity = null,
        string? uom = null,
        decimal? value = null,
        string? queryRoute = null) =>
        new()
        {
            Category = category,
            SourceModule = sourceModule,
            ReferenceType = referenceType,
            ReferenceNo = referenceNo,
            Branch = branch,
            Quantity = quantity,
            Uom = uom,
            Value = value,
            Reason = reason,
            QueryRoute = queryRoute,
            Severity = ItemRetirementBlocker.SeverityBlocker,
            EvaluatedAtUtc = evaluatedAtUtc
        };

    private static ItemRetirementBlocker Warning(
        string category,
        string sourceModule,
        string referenceType,
        string reason,
        DateTime evaluatedAtUtc,
        string? referenceNo = null,
        decimal? quantity = null,
        string? uom = null,
        string? queryRoute = null) =>
        new()
        {
            Category = category,
            SourceModule = sourceModule,
            ReferenceType = referenceType,
            ReferenceNo = referenceNo,
            Quantity = quantity,
            Uom = uom,
            Reason = reason,
            QueryRoute = queryRoute,
            Severity = ItemRetirementBlocker.SeverityWarning,
            EvaluatedAtUtc = evaluatedAtUtc
        };

    private static async Task<decimal> EvaluateInventoryBalancesAsync(
        AppDbContext db,
        string company,
        string code,
        DateTime evaluatedAtUtc,
        List<ItemRetirementBlocker> blockers,
        CancellationToken cancellationToken)
    {
        // Negative quantities are obligations too: the filter is "not zero", never "greater than zero".
        var rows = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.ICode == code && x.StdQty != 0m)
            .OrderBy(x => x.BranchCode).ThenBy(x => x.WhCode).ThenBy(x => x.LocCode).ThenBy(x => x.LotNo)
            .Select(x => new { x.BranchCode, x.WhCode, x.LocCode, x.LotNo, x.IStatus, x.StdQty, x.StdUom })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return 0m;
        }

        var onHand = rows.Sum(x => x.StdQty);
        foreach (var row in rows.Take(MaxSamplesPerCategory))
        {
            blockers.Add(Blocker(
                "Inventory Balance",
                "Inventory",
                "Stock balance",
                $"On-hand quantity {row.StdQty:n4} {row.StdUom} remains in {row.WhCode}/{row.LocCode} (lot {row.LotNo}, status {row.IStatus}).",
                evaluatedAtUtc,
                referenceNo: $"{row.WhCode}/{row.LocCode}",
                branch: row.BranchCode,
                quantity: row.StdQty,
                uom: row.StdUom,
                queryRoute: QueryRouteStockBalances));
        }

        if (rows.Count > MaxSamplesPerCategory)
        {
            blockers.Add(Blocker(
                "Inventory Balance",
                "Inventory",
                "Stock balance",
                $"{rows.Count - MaxSamplesPerCategory} further stock balance slice(s) also carry a non-zero quantity.",
                evaluatedAtUtc,
                quantity: onHand,
                queryRoute: QueryRouteStockBalances));
        }

        return onHand;
    }

    private static async Task<decimal> EvaluateValuationAsync(
        AppDbContext db,
        string company,
        string code,
        DateTime evaluatedAtUtc,
        List<ItemRetirementBlocker> blockers,
        CancellationToken cancellationToken)
    {
        var value = 0m;

        // The cost state is the authoritative financial residual for WAC and STANDARD. A zero stock
        // quantity with a non-zero value is still a blocker; it is never inferred from IvBalLoc.
        var costStates = await db.StockCostStates.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.ItemCode == code
                        && (x.InventoryValue != 0m || x.OnHandBaseQty != 0m))
            .Take(MaxSamplesPerCategory)
            .Select(x => new { x.BranchCode, x.CostMethod, x.OnHandBaseQty, x.InventoryValue })
            .ToListAsync(cancellationToken);

        foreach (var state in costStates)
        {
            value += state.InventoryValue;
            blockers.Add(Blocker(
                "Valuation",
                "Costing",
                "Cost state residual",
                $"The {state.CostMethod} cost state still carries {state.OnHandBaseQty:n4} base quantity and {state.InventoryValue:n2} inventory value.",
                evaluatedAtUtc,
                referenceNo: state.BranchCode,
                branch: state.BranchCode,
                quantity: state.OnHandBaseQty,
                value: state.InventoryValue));
        }

        var openLayers = await db.StockFifoLayers.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.ItemCode == code
                        && (x.RemainingQty != 0m || x.RemainingValue != 0m))
            .Take(MaxSamplesPerCategory)
            .Select(x => new
            {
                x.BranchCode,
                x.SourceDocumentType,
                x.SourceDocumentNo,
                x.RemainingQty,
                x.RemainingValue,
                x.BaseUom
            })
            .ToListAsync(cancellationToken);

        foreach (var layer in openLayers)
        {
            value += layer.RemainingValue;
            blockers.Add(Blocker(
                "Valuation",
                "Costing",
                "FIFO cost layer",
                $"An open FIFO layer from {layer.SourceDocumentType} {layer.SourceDocumentNo} still holds {layer.RemainingQty:n4} {layer.BaseUom} / {layer.RemainingValue:n2}.",
                evaluatedAtUtc,
                referenceNo: layer.SourceDocumentNo,
                branch: layer.BranchCode,
                quantity: layer.RemainingQty,
                uom: layer.BaseUom,
                value: layer.RemainingValue));
        }

        return value;
    }

    private static async Task EvaluateOpenInventoryAsync(
        AppDbContext db,
        string company,
        string code,
        DateTime evaluatedAtUtc,
        List<ItemRetirementBlocker> blockers,
        CancellationToken cancellationToken)
    {
        var openDrafts = await (
            from d in db.IvTrxBatchDetails.AsNoTracking()
            join b in db.IvTrxBatches.AsNoTracking() on d.BatchId equals b.Id
            where d.CompanyCode == company
                  && d.ICode == code
                  && b.BatchStatus == IvBatchStatuses.New
                  && b.DeletedAtUtc == null
            orderby b.BatchNo
            select new { b.BatchNo, b.TrxType, d.BranchCode, Qty = d.ToStdQty ?? d.FrStdQty })
            .ToListAsync(cancellationToken);

        if (openDrafts.Count == 0)
        {
            return;
        }

        foreach (var draft in openDrafts.Take(MaxSamplesPerCategory))
        {
            blockers.Add(Blocker(
                "Open Inventory",
                "Inventory",
                "Unposted inventory transaction",
                $"Inventory transaction {draft.BatchNo} ({draft.TrxType}) is still open and will post quantity for this item.",
                evaluatedAtUtc,
                referenceNo: draft.BatchNo.ToString(),
                branch: draft.BranchCode,
                quantity: draft.Qty,
                queryRoute: QueryRouteInventoryTransactions));
        }

        if (openDrafts.Count > MaxSamplesPerCategory)
        {
            blockers.Add(Blocker(
                "Open Inventory",
                "Inventory",
                "Unposted inventory transaction",
                $"{openDrafts.Count - MaxSamplesPerCategory} further unposted inventory transaction(s) reference this item.",
                evaluatedAtUtc,
                queryRoute: QueryRouteInventoryTransactions));
        }
    }

    private static async Task EvaluateProcurementAsync(
        AppDbContext db,
        string company,
        string code,
        DateTime evaluatedAtUtc,
        List<ItemRetirementBlocker> blockers,
        CancellationToken cancellationToken)
    {
        // Purchase requests: a cancelled or fully ordered line is terminal and never blocks.
        var openPr = await (
            from d in db.PoPrDetails.AsNoTracking()
            join h in db.PoPrs.AsNoTracking() on new { d.CompanyCode, d.PrNo } equals new { h.CompanyCode, h.PrNo }
            where d.CompanyCode == company
                  && d.ICode == code
                  && h.Status != PoPrStatuses.Cancelled
                  && d.Status != PoPrStatuses.Cancelled
                  && d.Status != PoPrStatuses.FullyOrdered
            orderby d.PrNo
            select new { d.PrNo, d.StdQty, d.BranchCode })
            .Take(MaxSamplesPerCategory)
            .ToListAsync(cancellationToken);

        foreach (var pr in openPr)
        {
            blockers.Add(Blocker(
                "Procurement",
                "Purchase",
                "Open purchase request",
                $"Purchase request {pr.PrNo} still has outstanding purchasing quantity.",
                evaluatedAtUtc,
                referenceNo: pr.PrNo,
                branch: pr.BranchCode,
                quantity: pr.StdQty,
                queryRoute: QueryRoutePurchaseRequests));
        }

        // Purchase orders: only the latest revision with remaining quantity blocks.
        var openPoLines = await (
            from d in db.PoOrderDetails.AsNoTracking()
            join h in db.PoOrders.AsNoTracking()
                on new { d.CompanyCode, d.PoNo, d.PoRelNo } equals new { h.CompanyCode, h.PoNo, h.PoRelNo }
            where d.CompanyCode == company
                  && d.ICode == code
                  && h.Status != PoOrderStatuses.Cancelled
                  && h.Status != PoOrderStatuses.Closed
                  && !h.FinClosed
                  && (d.BalanceQty > 0m || d.PoQty - d.RecvQty - d.ReturnQty - d.ReturnQtyCn > 0m)
            select new
            {
                d.PoNo,
                d.PoRelNo,
                d.BalanceQty,
                d.PoQty,
                d.RecvQty,
                d.ReturnQty,
                d.ReturnQtyCn,
                d.BranchCode
            })
            .ToListAsync(cancellationToken);

        if (openPoLines.Count == 0)
        {
            return;
        }

        var poNos = openPoLines.Select(x => x.PoNo).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var latest = await db.PoOrders.AsNoTracking()
            .Where(h => h.CompanyCode == company && poNos.Contains(h.PoNo))
            .GroupBy(h => h.PoNo)
            .Select(g => new { PoNo = g.Key, PoRelNo = g.Max(x => x.PoRelNo) })
            .ToListAsync(cancellationToken);
        var latestByPo = latest
            .GroupBy(x => x.PoNo, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(x => x.PoRelNo), StringComparer.OrdinalIgnoreCase);

        foreach (var line in openPoLines
                     .Where(x => latestByPo.TryGetValue(x.PoNo, out var rel) && rel == x.PoRelNo)
                     .Take(MaxSamplesPerCategory))
        {
            var remaining = Math.Max(
                line.BalanceQty,
                line.PoQty - line.RecvQty - line.ReturnQty - line.ReturnQtyCn);
            blockers.Add(Blocker(
                "Procurement",
                "Purchase",
                "Open purchase order",
                $"Purchase order {line.PoNo} revision {line.PoRelNo} still has {remaining:n4} outstanding quantity.",
                evaluatedAtUtc,
                referenceNo: line.PoNo,
                branch: line.BranchCode,
                quantity: remaining,
                queryRoute: QueryRoutePurchaseOrders));
        }
    }

    private static async Task EvaluateSalesAsync(
        AppDbContext db,
        string company,
        string code,
        DateTime evaluatedAtUtc,
        List<ItemRetirementBlocker> blockers,
        List<ItemRetirementBlocker> warnings,
        CancellationToken cancellationToken)
    {
        // Current, unshipped sales order with remaining fulfilment.
        var openSo = await (
            from d in db.SaSoDetails.AsNoTracking()
            join h in db.SaSos.AsNoTracking() on new { d.CompanyCode, d.SoNo } equals new { h.CompanyCode, h.SoNo }
            where d.CompanyCode == company
                  && d.ICode == code
                  && h.IsCurrent
                  && h.Status != SaSoStatuses.Closed
                  && h.Status != SaSoStatuses.Superseded
                  && d.BalanceQty > 0m
            orderby d.SoNo
            select new { d.SoNo, d.BalanceQty, h.BranchCode })
            .Take(MaxSamplesPerCategory)
            .ToListAsync(cancellationToken);

        foreach (var so in openSo)
        {
            blockers.Add(Blocker(
                "Sales",
                "Sales",
                "Open sales order",
                $"Sales order {so.SoNo} still has {so.BalanceQty:n4} undelivered quantity.",
                evaluatedAtUtc,
                referenceNo: so.SoNo,
                branch: so.BranchCode,
                quantity: so.BalanceQty,
                queryRoute: QueryRouteSalesOrders));
        }

        // Delivery orders are terminal at CLOSED; NEW/POSTED still demand dispatch or invoicing.
        var openDo = await (
            from d in db.SaDoDetails.AsNoTracking()
            join h in db.SaDos.AsNoTracking() on new { d.CompanyCode, d.DoNo } equals new { h.CompanyCode, h.DoNo }
            where d.CompanyCode == company
                  && d.ICode == code
                  && h.Status != SaDoStatuses.Closed
            orderby d.DoNo
            select new { d.DoNo, h.Status, h.BranchCode })
            .Take(MaxSamplesPerCategory)
            .ToListAsync(cancellationToken);

        foreach (var deliveryOrder in openDo)
        {
            blockers.Add(Blocker(
                "Sales",
                "Sales",
                "Open delivery order",
                $"Delivery order {deliveryOrder.DoNo} is {deliveryOrder.Status} and not yet closed.",
                evaluatedAtUtc,
                referenceNo: deliveryOrder.DoNo,
                branch: deliveryOrder.BranchCode,
                queryRoute: QueryRouteDeliveryOrders));
        }

        // Active delivery-request source still feeding production/delivery demand.
        var activeDr = await (
            from s in db.SaDeliveryRequestSources.AsNoTracking()
            join h in db.SaDeliveryRequests.AsNoTracking() on s.DeliveryRequestId equals h.Uid
            where s.CompanyCode == company
                  && s.ProductCode == code
                  && s.IsActive
                  && h.Status != SaDeliveryRequestStatuses.Cancelled
                  && h.Status != SaDeliveryRequestStatuses.Completed
            select new { h.DeliveryRequestNo, h.Status, s.BranchCode })
            .Take(MaxSamplesPerCategory)
            .ToListAsync(cancellationToken);

        foreach (var dr in activeDr)
        {
            blockers.Add(Blocker(
                "Sales",
                "Sales",
                "Active delivery request",
                $"Delivery request {dr.DeliveryRequestNo} ({dr.Status}) still requires this item.",
                evaluatedAtUtc,
                referenceNo: dr.DeliveryRequestNo,
                branch: dr.BranchCode,
                queryRoute: QueryRouteDeliveryOrders));
        }

        // Quotations are commercial references only. After discontinuation a quote cannot create a
        // valid new SO, so it is reported but never freezes the item forever.
        var openQuotations = await (
            from d in db.SaQtDetails.AsNoTracking()
            join h in db.SaQts.AsNoTracking() on new { d.CompanyCode, d.QtNo } equals new { h.CompanyCode, h.QtNo }
            where d.CompanyCode == company
                  && d.ICode == code
                  && h.IsCurrent
                  && (h.Status == SaQtStatuses.New || h.Status == SaQtStatuses.Sent || h.Status == SaQtStatuses.Accepted)
                  && d.OrderQty - d.ConvertedQty > 0m
            orderby d.QtNo
            select new { d.QtNo, Remaining = d.OrderQty - d.ConvertedQty })
            .Take(MaxSamplesPerCategory)
            .ToListAsync(cancellationToken);

        foreach (var quotation in openQuotations)
        {
            warnings.Add(Warning(
                "Sales",
                "Sales",
                "Open quotation",
                $"Quotation {quotation.QtNo} still shows {quotation.Remaining:n4} unconverted quantity. It cannot create new demand after discontinuation; cancel or expire it if the customer is no longer interested.",
                evaluatedAtUtc,
                referenceNo: quotation.QtNo,
                quantity: quotation.Remaining));
        }
    }

    private static async Task EvaluateProductionAsync(
        AppDbContext db,
        string company,
        string code,
        DateTime evaluatedAtUtc,
        List<ItemRetirementBlocker> blockers,
        CancellationToken cancellationToken)
    {
        var openAsProduct = await db.ProductionWorkOrders.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.ProductCode == code
                        && x.Status != ProductionWorkOrderStatuses.Closed
                        && x.Status != ProductionWorkOrderStatuses.Cancelled)
            .OrderBy(x => x.WorkOrderNo)
            .Take(MaxSamplesPerCategory)
            .Select(x => new { x.WorkOrderNo, x.Status, x.BranchCode, x.PlannedQty })
            .ToListAsync(cancellationToken);

        foreach (var workOrder in openAsProduct)
        {
            blockers.Add(Blocker(
                "Production/WIP",
                "Production",
                "Open work order product",
                $"Work order {workOrder.WorkOrderNo} ({workOrder.Status}) produces this item.",
                evaluatedAtUtc,
                referenceNo: workOrder.WorkOrderNo,
                branch: workOrder.BranchCode,
                quantity: workOrder.PlannedQty,
                queryRoute: QueryRouteWorkOrders));
        }

        var openAsMaterial = await (
            from m in db.ProductionWorkOrderMaterials.AsNoTracking()
            join w in db.ProductionWorkOrders.AsNoTracking() on m.WorkOrderId equals w.Uid
            where w.CompanyCode == company
                  && m.ComponentCode == code
                  && w.Status != ProductionWorkOrderStatuses.Closed
                  && w.Status != ProductionWorkOrderStatuses.Cancelled
            orderby w.WorkOrderNo
            select new { w.WorkOrderNo, w.Status, w.BranchCode, m.RequiredQty, m.IssuedQty, m.ReturnedQty })
            .Take(MaxSamplesPerCategory)
            .ToListAsync(cancellationToken);

        foreach (var material in openAsMaterial)
        {
            blockers.Add(Blocker(
                "Production/WIP",
                "Production",
                "Open work order material",
                $"Work order {material.WorkOrderNo} ({material.Status}) still requires this component.",
                evaluatedAtUtc,
                referenceNo: material.WorkOrderNo,
                branch: material.BranchCode,
                quantity: material.RequiredQty - material.IssuedQty + material.ReturnedQty,
                queryRoute: QueryRouteWorkOrders));
        }

        // Production balances: negative quantities are obligations too — never treat as zero.
        var productionBalances = await db.ProductionBalLots.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.ItemCode == code && x.Qty != 0m)
            .OrderBy(x => x.WorkOrderNo)
            .Take(MaxSamplesPerCategory)
            .Select(x => new { x.WorkOrderNo, x.Kind, x.Qty, x.Uom, x.BranchCode })
            .ToListAsync(cancellationToken);

        foreach (var balance in productionBalances)
        {
            blockers.Add(Blocker(
                "Production/WIP",
                "Production",
                "Production balance",
                $"Production balance {balance.Kind} on {balance.WorkOrderNo} still carries {balance.Qty:n4} {balance.Uom}.",
                evaluatedAtUtc,
                referenceNo: balance.WorkOrderNo,
                branch: balance.BranchCode,
                quantity: balance.Qty,
                uom: balance.Uom,
                queryRoute: QueryRouteWorkOrders));
        }

        // Legacy WIP balance has no company column: it is a company-agnostic legacy table.
        var wipBalances = await db.WipItemBalLocs.AsNoTracking()
            .Where(x => x.ICode == code && x.StdQty != 0)
            .Take(MaxSamplesPerCategory)
            .Select(x => new { x.WcCode, x.ProcessCode, x.LotNo, Qty = x.StdQty ?? 0, x.StdUom })
            .ToListAsync(cancellationToken);

        foreach (var wip in wipBalances)
        {
            blockers.Add(Blocker(
                "Production/WIP",
                "Production",
                "WIP balance",
                $"WIP balance at work centre {wip.WcCode} process {wip.ProcessCode} still carries {wip.Qty:n4} {wip.StdUom}.",
                evaluatedAtUtc,
                referenceNo: $"{wip.WcCode}/{wip.ProcessCode}",
                quantity: (decimal)wip.Qty,
                uom: wip.StdUom,
                queryRoute: QueryRouteWorkOrders));
        }
    }

    private static async Task EvaluateReservationsAsync(
        AppDbContext db,
        string company,
        string code,
        DateTime evaluatedAtUtc,
        List<ItemRetirementBlocker> blockers,
        CancellationToken cancellationToken)
    {
        var reservations = await (
            from r in db.SaDeliveryRequestStockReservations.AsNoTracking()
            join b in db.IvBalLocs.AsNoTracking() on r.BalLocId equals b.Id
            join h in db.SaDeliveryRequests.AsNoTracking() on r.DeliveryRequestId equals h.Uid
            where r.CompanyCode == company
                  && b.ICode == code
                  && r.IsActive
                  && r.ReservedQty != 0m
            select new { r.ReservedQty, h.DeliveryRequestNo, r.BranchCode })
            .Take(MaxSamplesPerCategory)
            .ToListAsync(cancellationToken);

        foreach (var reservation in reservations)
        {
            blockers.Add(Blocker(
                "Reservations",
                "Sales",
                "Stock reservation",
                $"Stock is still reserved for delivery request {reservation.DeliveryRequestNo} ({reservation.ReservedQty:n4}).",
                evaluatedAtUtc,
                referenceNo: reservation.DeliveryRequestNo,
                branch: reservation.BranchCode,
                quantity: reservation.ReservedQty));
        }
    }
}
