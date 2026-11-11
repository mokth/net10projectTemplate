using ErpWeb.Core.Inventory;
using ErpWeb.Core.Purchase;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Quality;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Quality;

public static class QaSourceMessages
{
    public const string MixedLot = "This case can inspect only one production lot.";
    public const string MixedDocument = "This case can inspect only one source document.";
    public const string DuplicatePoLine = "Each PO line can appear once on this case.";
    public const string SelectLot = "Select one production lot for this case.";
    public const string PoNotFound = "Purchase order was not found for this company and branch.";
    public const string PoCancelled = "This purchase order cannot be inspected.";
    public const string LotNotFound = "Production lot was not found for this company and branch.";
    public const string OutputNotFound = "Daily Production document was not found for this company and branch.";
    public const string BatchNotFound = "Posted inventory document was not found for this company and branch.";
    public const string PileNotFound = "Warehouse pile was not found for this company and branch.";
    public const string WrongLotStage = "This production lot is not the source type selected on the case.";
    public const string NeedSourceHeader =
        "This source needs the posted document identity. Reload the source and try again.";

    public static string Injected(int lineNo) =>
        $"Line {lineNo} is not on the selected source document.";

    public static string StaleQty(int lineNo) =>
        $"Quantity on line {lineNo} is greater than the live source quantity. Reload the source and try again.";

    public static string ItemMismatch(int lineNo) =>
        $"Item on line {lineNo} does not match the source document.";

    public static string UomMismatch(int lineNo) =>
        $"UOM on line {lineNo} does not match the source document.";

    public static string NeedPoLine(int lineNo) =>
        $"Incoming line {lineNo} needs a PO line number.";
}

public static class QaSourceResolver
{
    public static string? FreezeGeneration(QaCaseSaveCommand command)
    {
        var source = (command.SourceType ?? string.Empty).Trim();
        if (string.Equals(source, QaSourceTypes.IncomingPreGrn, StringComparison.OrdinalIgnoreCase))
        {
            var poLines = command.Lines.Where(x => x.PoLineNo is > 0).Select(x => x.PoLineNo!.Value).ToList();
            if (poLines.Count != poLines.Distinct().Count())
            {
                return QaSourceMessages.DuplicatePoLine;
            }
        }

        if (IsLotSource(source))
        {
            if (command.ProductionBalLotId is not > 0)
            {
                return QaSourceMessages.SelectLot;
            }

            var lots = command.Lines
                .Select(x => (x.LotNo ?? string.Empty).Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (lots.Count > 1)
            {
                return QaSourceMessages.MixedLot;
            }
        }

        if (command.Lines.Select(x => x.SourceLineId).Where(x => x is > 0).Select(x => x!.Value).Distinct().Count() > 1
            && command.SourceHeaderId is not > 0
            && IsPostedOrOutput(source))
        {
            return QaSourceMessages.MixedDocument;
        }

        return null;
    }

    public static async Task<string?> RevalidateDraftAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        QaCaseSaveCommand command,
        CancellationToken cancellationToken = default)
    {
        var source = (command.SourceType ?? string.Empty).Trim();
        if (string.Equals(source, QaSourceTypes.IncomingPreGrn, StringComparison.OrdinalIgnoreCase))
        {
            return await RevalidateIncomingAsync(
                db, scope.CompanyCode, scope.BranchCode!, command.PoNo, command.PoRelNo, command.Lines, cancellationToken);
        }

        if (IsLotSource(source))
        {
            return await RevalidateLotAsync(
                db, scope.CompanyCode, scope.BranchCode!, source, command.ProductionBalLotId,
                command.WorkOrderNo, command.Lines, cancellationToken);
        }

        if (string.Equals(source, QaSourceTypes.PostedGrn, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, QaSourceTypes.InventoryStock, StringComparison.OrdinalIgnoreCase))
        {
            return await RevalidatePostedOrPileAsync(
                db, scope.CompanyCode, scope.BranchCode!, source, command.SourceHeaderId,
                command.SourceDocumentRevision, command.Lines, cancellationToken);
        }

        if (string.Equals(source, QaSourceTypes.ProductionOutputGood, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, QaSourceTypes.ProductionOutputNonGood, StringComparison.OrdinalIgnoreCase))
        {
            return await RevalidateOutputAsync(
                db, scope.CompanyCode, scope.BranchCode!, source, command.SourceHeaderId, command.Lines, cancellationToken);
        }

        return null;
    }

    public static Task<string?> RevalidateCaseAsync(
        AppDbContext db,
        QaCase entity,
        CancellationToken cancellationToken = default) =>
        RevalidateDraftAsync(db, ScopeOf(entity), ToCommand(entity), cancellationToken);

    public static async Task<string?> RevalidateInspectionAsync(
        AppDbContext db,
        QaCase entity,
        IReadOnlyList<QaInspectionLineCommand> lines,
        CancellationToken cancellationToken = default)
    {
        var command = ToCommand(entity);
        command = new QaCaseSaveCommand
        {
            SourceType = command.SourceType,
            SourceHeaderId = command.SourceHeaderId,
            SourceDocumentRevision = command.SourceDocumentRevision,
            PoNo = command.PoNo,
            PoRelNo = command.PoRelNo,
            WorkOrderNo = command.WorkOrderNo,
            ProductionBalLotId = command.ProductionBalLotId,
            Lines = command.Lines.Select(line =>
            {
                var inspected = lines.FirstOrDefault(x => x.LineNo == line.LineNo);
                return inspected is null
                    ? line
                    : new QaCaseLineSaveCommand
                    {
                        LineNo = line.LineNo,
                        ItemCode = line.ItemCode,
                        Uom = line.Uom,
                        Qty = inspected.InspectedQty,
                        LotNo = line.LotNo,
                        WarehouseCode = line.WarehouseCode,
                        LocationCode = line.LocationCode,
                        ItemStatus = line.ItemStatus,
                        SourceLineId = line.SourceLineId,
                        PoLineNo = line.PoLineNo
                    };
            }).ToList()
        };
        return await RevalidateDraftAsync(db, ScopeOf(entity), command, cancellationToken);
    }

    private static async Task<string?> RevalidateIncomingAsync(
        AppDbContext db,
        string company,
        string branch,
        string? poNo,
        short? poRelNo,
        IReadOnlyList<QaCaseLineSaveCommand> lines,
        CancellationToken cancellationToken)
    {
        var po = (poNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(po))
        {
            return null;
        }

        var rel = poRelNo ?? 0;
        var header = await db.PoOrders.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.PoNo == po && x.PoRelNo == rel,
                cancellationToken);
        if (header is null)
        {
            return QaSourceMessages.PoNotFound;
        }

        if (string.Equals(header.Status, PoOrderStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            return QaSourceMessages.PoCancelled;
        }

        var details = await db.PoOrderDetails.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.PoNo == po && x.PoRelNo == rel)
            .ToListAsync(cancellationToken);

        foreach (var line in lines)
        {
            if (line.PoLineNo is not > 0)
            {
                return QaSourceMessages.NeedPoLine(line.LineNo);
            }

            var detail = details.FirstOrDefault(x => x.Line == line.PoLineNo);
            if (detail is null)
            {
                return QaSourceMessages.Injected(line.LineNo);
            }

            if (!SameText(line.ItemCode, detail.ICode))
            {
                return QaSourceMessages.ItemMismatch(line.LineNo);
            }

            var uom = detail.PurchaseUom ?? detail.StdUom;
            if (!SameText(line.Uom, uom) && !SameText(line.Uom, detail.StdUom))
            {
                return QaSourceMessages.UomMismatch(line.LineNo);
            }

            if (IvQty.Round(line.Qty) > IvQty.Round(detail.PoQty))
            {
                return QaSourceMessages.StaleQty(line.LineNo);
            }
        }

        return ExceedsAggregated(
            lines,
            line => line.PoLineNo?.ToString(),
            line => details.FirstOrDefault(x => x.Line == line.PoLineNo)?.PoQty ?? 0m);
    }

    private static async Task<string?> RevalidateLotAsync(
        AppDbContext db,
        string company,
        string branch,
        string sourceType,
        long? lotId,
        string? workOrderNo,
        IReadOnlyList<QaCaseLineSaveCommand> lines,
        CancellationToken cancellationToken)
    {
        if (lotId is not > 0)
        {
            return QaSourceMessages.SelectLot;
        }

        var lot = await db.ProductionBalLots.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Uid == lotId && x.CompanyCode == company && x.BranchCode == branch,
                cancellationToken);
        if (lot is null)
        {
            return QaSourceMessages.LotNotFound;
        }

        var expectedStage = string.Equals(sourceType, QaSourceTypes.FgStagingLot, StringComparison.OrdinalIgnoreCase)
            ? "FG_STAGING"
            : "PROCESS_WIP";
        if (!SameText(lot.BalanceStage, expectedStage))
        {
            return QaSourceMessages.WrongLotStage;
        }

        if (!string.IsNullOrWhiteSpace(workOrderNo) && !SameText(workOrderNo, lot.WorkOrderNo))
        {
            return QaSourceMessages.Injected(lines[0].LineNo);
        }

        foreach (var line in lines)
        {
            if (!SameText(line.ItemCode, lot.ItemCode))
            {
                return QaSourceMessages.ItemMismatch(line.LineNo);
            }

            if (!SameText(line.Uom, lot.Uom) && !SameText(line.Uom, lot.BaseUom))
            {
                return QaSourceMessages.UomMismatch(line.LineNo);
            }

            if (!string.IsNullOrWhiteSpace(line.LotNo) && !SameText(line.LotNo, lot.LotNo)
                && !SameText(line.LotNo, lot.PhysicalLotNo))
            {
                return QaSourceMessages.MixedLot;
            }

            var liveQty = SameText(line.Uom, lot.BaseUom) ? lot.BaseQty : lot.Qty;
            if (IvQty.Round(line.Qty) > IvQty.Round(liveQty))
            {
                return QaSourceMessages.StaleQty(line.LineNo);
            }
        }

        return ExceedsAggregated(
            lines,
            _ => "lot",
            line => SameText(line.Uom, lot.BaseUom) ? lot.BaseQty : lot.Qty);
    }

    private static async Task<string?> RevalidatePostedOrPileAsync(
        AppDbContext db,
        string company,
        string branch,
        string sourceType,
        long? sourceHeaderId,
        int? revision,
        IReadOnlyList<QaCaseLineSaveCommand> lines,
        CancellationToken cancellationToken)
    {
        if (sourceHeaderId is not > 0)
        {
            if (string.Equals(sourceType, QaSourceTypes.InventoryStock, StringComparison.OrdinalIgnoreCase))
            {
                return await RevalidatePileObservationAsync(db, company, branch, lines, cancellationToken);
            }

            return QaSourceMessages.NeedSourceHeader;
        }

        if (sourceHeaderId is > 0 and <= int.MaxValue)
        {
            var batchId = (int)sourceHeaderId.Value;
            var batch = await db.IvTrxBatches.AsNoTracking()
                .Include(x => x.Details)
                .SingleOrDefaultAsync(
                    x => x.Id == batchId
                         && x.CompanyCode == company
                         && x.BranchCode == branch
                         && x.DeletedAtUtc == null,
                    cancellationToken);
            if (batch is null
                || !string.Equals(batch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                return QaSourceMessages.BatchNotFound;
            }

            var expectedType = string.Equals(sourceType, QaSourceTypes.PostedGrn, StringComparison.OrdinalIgnoreCase)
                ? IvTrxTypes.GoodsReceive
                : IvTrxTypes.MiscellaneousReceipt;
            if (!string.Equals(batch.TrxType, expectedType, StringComparison.OrdinalIgnoreCase))
            {
                return QaSourceMessages.BatchNotFound;
            }

            foreach (var line in lines)
            {
                if (line.SourceLineId is not > 0)
                {
                    return QaSourceMessages.Injected(line.LineNo);
                }

                var detail = line.SourceLineId is > 0 and <= int.MaxValue
                    ? batch.Details.FirstOrDefault(x => x.Id == (int)line.SourceLineId.Value)
                    : null;
                if (detail is null)
                {
                    return QaSourceMessages.Injected(line.LineNo);
                }

                if (revision is int expected && detail.DocumentRevision != expected)
                {
                    return QaSourceMessages.StaleQty(line.LineNo);
                }

                if (!SameText(line.ItemCode, detail.ICode))
                {
                    return QaSourceMessages.ItemMismatch(line.LineNo);
                }

                var uom = detail.ToStdUom ?? detail.ToPurUom;
                if (!SameText(line.Uom, uom) && !SameText(line.Uom, detail.ToPurUom))
                {
                    return QaSourceMessages.UomMismatch(line.LineNo);
                }

                var liveQty = detail.ToStdQty ?? detail.ToPurQty ?? 0m;
                if (IvQty.Round(line.Qty) > IvQty.Round(liveQty))
                {
                    return QaSourceMessages.StaleQty(line.LineNo);
                }
            }

            return ExceedsAggregated(
                lines,
                line => line.SourceLineId?.ToString(),
                line =>
                {
                    var detail = line.SourceLineId is > 0 and <= int.MaxValue
                        ? batch.Details.FirstOrDefault(x => x.Id == (int)line.SourceLineId.Value)
                        : null;
                    return detail?.ToStdQty ?? detail?.ToPurQty ?? 0m;
                });
        }

        return QaSourceMessages.NeedSourceHeader;
    }

    private static async Task<string?> RevalidatePileObservationAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyList<QaCaseLineSaveCommand> lines,
        CancellationToken cancellationToken)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.WarehouseCode)
                || string.IsNullOrWhiteSpace(line.LocationCode)
                || string.IsNullOrWhiteSpace(line.LotNo)
                || string.IsNullOrWhiteSpace(line.ItemStatus))
            {
                continue;
            }

            var pile = await db.IvBalLocs.AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.CompanyCode == company
                         && x.BranchCode == branch
                         && x.ICode == line.ItemCode
                         && x.WhCode == line.WarehouseCode
                         && x.LocCode == line.LocationCode
                         && x.LotNo == line.LotNo
                         && x.IStatus == line.ItemStatus,
                    cancellationToken);
            if (pile is null)
            {
                return QaSourceMessages.PileNotFound;
            }

            if (!SameText(line.Uom, pile.StdUom) && !string.IsNullOrWhiteSpace(pile.StdUom))
            {
                return QaSourceMessages.UomMismatch(line.LineNo);
            }

            if (IvQty.Round(line.Qty) > IvQty.Round(pile.StdQty))
            {
                return QaSourceMessages.StaleQty(line.LineNo);
            }
        }

        return null;
    }

    private static async Task<string?> RevalidateOutputAsync(
        AppDbContext db,
        string company,
        string branch,
        string sourceType,
        long? sourceHeaderId,
        IReadOnlyList<QaCaseLineSaveCommand> lines,
        CancellationToken cancellationToken)
    {
        if (sourceHeaderId is not > 0)
        {
            return QaSourceMessages.NeedSourceHeader;
        }

        var output = await db.ProductionOutputs.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Uid == sourceHeaderId && x.CompanyCode == company && x.BranchCode == branch && x.DeletedAtUtc == null,
                cancellationToken);
        if (output is null)
        {
            return QaSourceMessages.OutputNotFound;
        }

        var liveQty = string.Equals(sourceType, QaSourceTypes.ProductionOutputGood, StringComparison.OrdinalIgnoreCase)
            ? output.GoodQty
            : output.ScrapQty + output.RejectQty + output.HoldQty;
        foreach (var line in lines)
        {
            if (!SameText(line.ItemCode, output.OutputItemCode))
            {
                return QaSourceMessages.ItemMismatch(line.LineNo);
            }

            if (!SameText(line.Uom, output.OutputUom))
            {
                return QaSourceMessages.UomMismatch(line.LineNo);
            }

            if (IvQty.Round(line.Qty) > IvQty.Round(liveQty))
            {
                return QaSourceMessages.StaleQty(line.LineNo);
            }
        }

        return ExceedsAggregated(lines, _ => "output", _ => liveQty);
    }

    private static string? ExceedsAggregated(
        IReadOnlyList<QaCaseLineSaveCommand> lines,
        Func<QaCaseLineSaveCommand, string?> groupKey,
        Func<QaCaseLineSaveCommand, decimal> liveQty)
    {
        foreach (var group in lines.GroupBy(x => groupKey(x) ?? $"line:{x.LineNo}", StringComparer.OrdinalIgnoreCase))
        {
            var sample = group.First();
            var live = IvQty.Round(liveQty(sample));
            var sum = IvQty.Round(group.Sum(x => x.Qty));
            if (sum > live)
            {
                return QaSourceMessages.StaleQty(sample.LineNo);
            }
        }

        return null;
    }

    private static bool IsLotSource(string source) =>
        string.Equals(source, QaSourceTypes.ProductionWipLot, StringComparison.OrdinalIgnoreCase)
        || string.Equals(source, QaSourceTypes.FgStagingLot, StringComparison.OrdinalIgnoreCase);

    private static bool IsPostedOrOutput(string source) =>
        string.Equals(source, QaSourceTypes.PostedGrn, StringComparison.OrdinalIgnoreCase)
        || string.Equals(source, QaSourceTypes.InventoryStock, StringComparison.OrdinalIgnoreCase)
        || string.Equals(source, QaSourceTypes.ProductionOutputGood, StringComparison.OrdinalIgnoreCase)
        || string.Equals(source, QaSourceTypes.ProductionOutputNonGood, StringComparison.OrdinalIgnoreCase);

    private static InventoryTenantScope ScopeOf(QaCase entity) => new()
    {
        CompanyCode = entity.CompanyCode,
        BranchCode = entity.BranchCode,
        UserId = "SYSTEM"
    };

    private static QaCaseSaveCommand ToCommand(QaCase entity) => new()
    {
        SourceType = entity.SourceType,
        SourceHeaderId = entity.SourceHeaderId,
        SourceDocumentRevision = entity.SourceDocumentRevision,
        PoNo = entity.PoNo,
        PoRelNo = entity.PoRelNo,
        WorkOrderNo = entity.WorkOrderNo,
        ProductionBalLotId = entity.ProductionBalLotId,
        Lines = entity.Lines.OrderBy(x => x.LineNo).Select(x => new QaCaseLineSaveCommand
        {
            LineNo = x.LineNo,
            ItemCode = x.ItemCode,
            Uom = x.Uom,
            Qty = x.Qty,
            LotNo = x.LotNo,
            WarehouseCode = x.WarehouseCode,
            LocationCode = x.LocationCode,
            ItemStatus = x.ItemStatus,
            SourceLineId = x.SourceLineId,
            PoLineNo = x.PoLineNo
        }).ToList()
    };

    private static bool SameText(string? left, string? right) =>
        string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
}
