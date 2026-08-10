using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

internal static class InventorySequenceHelper
{
    public static string PrefixFor(DocumentType docType) => docType switch
    {
        DocumentType.OB => "OB",
        DocumentType.GRN => "GRN",
        DocumentType.GI => "GI",
        DocumentType.ST => "ST",
        DocumentType.SA => "SA",
        _ => "INV"
    };

    public static async Task<string> NextDocNoAsync(
        AppDbContext db,
        int companyId,
        long branchId,
        DocumentType docType,
        DateTime docDate,
        string? createdBy,
        CancellationToken ct)
    {
        var yearMonth = docDate.Year * 100 + docDate.Month;
        var prefix = PrefixFor(docType);
        var key = docType.ToString();

        var seq = await db.DocumentSequences
            .FirstOrDefaultAsync(s =>
                s.CompanyId == companyId &&
                s.BranchId == branchId &&
                s.DocType == key &&
                s.YearMonth == yearMonth, ct);

        if (seq is null)
        {
            seq = new DocumentSequence
            {
                CompanyId = companyId,
                BranchId = branchId,
                DocType = key,
                Prefix = prefix,
                YearMonth = yearMonth,
                CurrentNumber = 0,
                NumberLength = 4,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = createdBy
            };
            db.DocumentSequences.Add(seq);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.Entry(seq).State = EntityState.Detached;
                seq = await db.DocumentSequences.SingleAsync(s =>
                    s.CompanyId == companyId &&
                    s.BranchId == branchId &&
                    s.DocType == key &&
                    s.YearMonth == yearMonth, ct);
            }
        }

        seq.CurrentNumber += 1;
        seq.ModifiedAtUtc = DateTime.UtcNow;
        seq.ModifiedBy = createdBy;
        await db.SaveChangesAsync(ct);

        var yyMM = yearMonth.ToString()[2..]; // e.g. 2608 from 202608
        return $"{seq.Prefix}{yyMM}{seq.CurrentNumber.ToString().PadLeft(seq.NumberLength, '0')}";
    }

    public static async Task<string> NextStockTakeNoAsync(
        AppDbContext db,
        int companyId,
        long branchId,
        DateTime countDate,
        string? createdBy,
        CancellationToken ct)
    {
        var yearMonth = countDate.Year * 100 + countDate.Month;
        const string key = "STK";
        var seq = await db.DocumentSequences
            .FirstOrDefaultAsync(s =>
                s.CompanyId == companyId &&
                s.BranchId == branchId &&
                s.DocType == key &&
                s.YearMonth == yearMonth, ct);

        if (seq is null)
        {
            seq = new DocumentSequence
            {
                CompanyId = companyId,
                BranchId = branchId,
                DocType = key,
                Prefix = "STK",
                YearMonth = yearMonth,
                CurrentNumber = 0,
                NumberLength = 4,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = createdBy
            };
            db.DocumentSequences.Add(seq);
            await db.SaveChangesAsync(ct);
        }

        seq.CurrentNumber += 1;
        await db.SaveChangesAsync(ct);
        var yyMM = yearMonth.ToString()[2..];
        return $"{seq.Prefix}{yyMM}{seq.CurrentNumber.ToString().PadLeft(seq.NumberLength, '0')}";
    }

    public static async Task<long> NextLedgerSequenceAsync(
        AppDbContext db,
        int companyId,
        long branchId,
        string? createdBy,
        CancellationToken ct)
    {
        var seq = await db.LedgerSequences
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && s.BranchId == branchId, ct);

        if (seq is null)
        {
            seq = new LedgerSequence
            {
                CompanyId = companyId,
                BranchId = branchId,
                CurrentNumber = 0,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = createdBy
            };
            db.LedgerSequences.Add(seq);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.Entry(seq).State = EntityState.Detached;
                seq = await db.LedgerSequences.SingleAsync(s =>
                    s.CompanyId == companyId && s.BranchId == branchId, ct);
            }
        }

        seq.CurrentNumber += 1;
        seq.ModifiedAtUtc = DateTime.UtcNow;
        seq.ModifiedBy = createdBy;
        await db.SaveChangesAsync(ct);
        return seq.CurrentNumber;
    }
}
