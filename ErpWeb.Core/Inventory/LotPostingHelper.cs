using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

internal static class LotPostingHelper
{
    public sealed class ItemLotContext
    {
        public bool IsBatchItem { get; init; }
        public long ItemId { get; init; }
        public long BaseUomId { get; init; }
    }

    public static async Task<ItemLotContext?> LoadItemContextAsync(
        AppDbContext db, int companyId, long itemVariantId, CancellationToken ct)
    {
        var row = await (
            from v in db.ItemVariants.AsNoTracking()
            join i in db.Items.AsNoTracking() on v.ItemId equals i.Id
            where v.Id == itemVariantId && v.CompanyId == companyId
            select new ItemLotContext
            {
                IsBatchItem = i.IsBatchItem,
                ItemId = i.Id,
                BaseUomId = i.BaseUOMId
            }).FirstOrDefaultAsync(ct);
        return row;
    }

    public static PostingResultDto? ValidateLineLotFields(
        DocumentType docType,
        bool isBatch,
        string? lotNo,
        long? lotId,
        IReadOnlyList<LotAllocationInput>? allocations)
    {
        var hasLot = !string.IsNullOrWhiteSpace(lotNo) || lotId is not null
                     || (allocations is { Count: > 0 });

        if (!isBatch)
        {
            if (hasLot)
                return PostingResultDto.Fail(InventoryErrorCodes.LotNotAllowedInPhase,
                    "Lot fields are not allowed for non-batch items.");
            return null;
        }

        // Batch item: lot identity required
        if (allocations is { Count: > 0 })
        {
            if (allocations.Any(a => a.Qty <= 0))
                return PostingResultDto.Fail(InventoryErrorCodes.ZeroQtyNotAllowed);
            if (allocations.Any(a => a.LotId <= 0 && string.IsNullOrWhiteSpace(a.LotNo)))
                return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "Each lot allocation needs LotId or LotNo.");
            return null;
        }

        // Inbound / increase: LotNo preferred (create on post); LotId ok if existing
        if (docType is DocumentType.OB or DocumentType.GRN)
        {
            if (string.IsNullOrWhiteSpace(lotNo) && lotId is null)
                return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "Batch inbound requires LotNo or LotId.");
            return null;
        }

        // Outbound / transfer / decrease: LotId required (or LotNo resolving to existing)
        if (lotId is null && string.IsNullOrWhiteSpace(lotNo))
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "Batch movement requires LotId or LotNo.");

        return null;
    }

    public static async Task<Lot> ResolveOrCreateLotAsync(
        AppDbContext db,
        int companyId,
        long itemVariantId,
        string lotNo,
        InventoryDocument doc,
        InventoryDocumentLine line,
        decimal qtyInBase,
        decimal unitCost,
        string? user,
        CancellationToken ct)
    {
        var normalized = lotNo.Trim().ToUpperInvariant();
        var existing = await db.Lots.FirstOrDefaultAsync(l =>
            l.CompanyId == companyId &&
            l.ItemVariantId == itemVariantId &&
            l.LotNo == normalized, ct);

        if (existing is not null)
            return existing;

        var lot = new Lot
        {
            CompanyId = companyId,
            ItemVariantId = itemVariantId,
            LotNo = normalized,
            SourceDocType = doc.DocType.ToString(),
            SourceDocNo = doc.DocNo,
            SourceDocLineNo = line.LineNo,
            ReceivedDate = doc.DocDate.Date,
            ReceivedQty = qtyInBase,
            ReceivedUnitCost = unitCost,
            CostCurrencyCode = "MYR",
            Status = LotStatus.ACTIVE,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = user
        };
        db.Lots.Add(lot);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(lot).State = EntityState.Detached;
            lot = await db.Lots.SingleAsync(l =>
                l.CompanyId == companyId &&
                l.ItemVariantId == itemVariantId &&
                l.LotNo == normalized, ct);
        }

        return lot;
    }

    public static async Task<Lot?> FindLotAsync(
        AppDbContext db, int companyId, long itemVariantId, long? lotId, string? lotNo, CancellationToken ct)
    {
        if (lotId is long id)
        {
            return await db.Lots.FirstOrDefaultAsync(l =>
                l.Id == id && l.CompanyId == companyId && l.ItemVariantId == itemVariantId, ct);
        }

        if (!string.IsNullOrWhiteSpace(lotNo))
        {
            var normalized = lotNo.Trim().ToUpperInvariant();
            return await db.Lots.FirstOrDefaultAsync(l =>
                l.CompanyId == companyId &&
                l.ItemVariantId == itemVariantId &&
                l.LotNo == normalized, ct);
        }

        return null;
    }

    public static async Task EnsureLotBalanceAsync(
        AppDbContext db,
        int companyId,
        long branchId,
        long lotId,
        long warehouseId,
        long locationId,
        string? user,
        CancellationToken ct)
    {
        var bal = await db.LotBalances.FirstOrDefaultAsync(b =>
            b.CompanyId == companyId && b.BranchId == branchId &&
            b.LotId == lotId && b.WarehouseId == warehouseId && b.LocationId == locationId, ct);
        if (bal is not null) return;

        var created = new LotBalance
        {
            CompanyId = companyId,
            BranchId = branchId,
            LotId = lotId,
            WarehouseId = warehouseId,
            LocationId = locationId,
            QtyOnHand = 0,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = user
        };
        db.LotBalances.Add(created);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(created).State = EntityState.Detached;
        }
    }

    public static async Task LockLotBalanceAsync(
        AppDbContext db,
        int companyId,
        long branchId,
        long lotId,
        long warehouseId,
        long locationId,
        CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                SELECT Id FROM LotBalance WITH (UPDLOCK, HOLDLOCK)
                WHERE CompanyId={0} AND BranchId={1} AND LotId={2} AND WarehouseId={3} AND LocationId={4};
                """,
                [companyId, branchId, lotId, warehouseId, locationId], ct);
        }
        else
        {
            _ = await db.LotBalances.FirstAsync(b =>
                b.CompanyId == companyId && b.BranchId == branchId &&
                b.LotId == lotId && b.WarehouseId == warehouseId && b.LocationId == locationId, ct);
        }
    }

    public static async Task ApplyLotBalanceAsync(
        AppDbContext db,
        int companyId,
        long branchId,
        long lotId,
        long warehouseId,
        long locationId,
        decimal delta,
        string? user,
        CancellationToken ct)
    {
        var bal = await db.LotBalances.FirstAsync(b =>
            b.CompanyId == companyId && b.BranchId == branchId &&
            b.LotId == lotId && b.WarehouseId == warehouseId && b.LocationId == locationId, ct);
        bal.QtyOnHand += delta;
        if (bal.QtyOnHand < 0)
            throw new InvalidOperationException(InventoryErrorCodes.InsufficientStock);
        bal.ModifiedAtUtc = DateTime.UtcNow;
        bal.ModifiedBy = user;
    }
}
