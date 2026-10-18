using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Builds an end-of-day count anchor from frozen line and cost evidence before
/// the branch lock. The caller must recheck the sheet and physical plans under
/// that lock before writing any count generation.
/// </summary>
public static class StockCountAnchorCommandFactory
{
    /// <summary>
    /// The cost bases saved under the count root must be the exact values
    /// frozen in its source snapshot before the branch lock was acquired.
    /// </summary>
    public static bool MatchesPersistedBasisSnapshot(
        StockPostingContext context,
        IReadOnlyCollection<StockCountCostBasis> persistedBases)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(persistedBases);
        if (context.Posting.SourceDocumentType != "IV_STOCK_COUNT"
            || string.IsNullOrWhiteSpace(context.Posting.SourceSnapshotJson)
            || !StringComparer.Ordinal.Equals(
                StockPostingFingerprint.Hash(context.Posting.SourceSnapshotJson),
                context.Posting.SourceSnapshotHash))
            return false;
        try
        {
            using var snapshot = JsonDocument.Parse(context.Posting.SourceSnapshotJson);
            if (!snapshot.RootElement.TryGetProperty("frozenBases", out var expected)
                || expected.ValueKind != JsonValueKind.Array)
                return false;
            var frozenBases = persistedBases.OrderBy(b => b.StockCountLineId)
                .Select(b => new
                {
                    b.StockCountLineId, b.FrozenFallbackUnitCost, b.CostBasisKind,
                    b.StandardCostRevisionId, b.CostBasisWatermark, b.CostBasisHash,
                    b.CostEvidenceType, b.CostOverrideReason, b.CostApprovedBy,
                    b.CostApprovedAtUtc
                }).ToArray();
            var current = StockPostingFingerprint.Create(new { }, new { frozenBases });
            using var actual = JsonDocument.Parse(current.SourceSnapshotJson);
            return Equivalent(expected, actual.RootElement.GetProperty("frozenBases"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Equivalent(JsonElement expected, JsonElement actual,
        string? propertyName = null)
    {
        if (expected.ValueKind != actual.ValueKind) return false;
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = expected.EnumerateObject().ToArray();
                if (properties.Length != actual.EnumerateObject().Count()) return false;
                return properties.All(p => actual.TryGetProperty(p.Name, out var value)
                    && Equivalent(p.Value, value, p.Name));
            case JsonValueKind.Array:
                var left = expected.EnumerateArray().ToArray();
                var right = actual.EnumerateArray().ToArray();
                return left.Length == right.Length
                    && left.Zip(right).All(pair => Equivalent(pair.First, pair.Second));
            case JsonValueKind.Number:
                return expected.GetDecimal() == actual.GetDecimal();
            case JsonValueKind.String when propertyName == "costApprovedAtUtc":
                return expected.GetDateTime().Ticks == actual.GetDateTime().Ticks;
            case JsonValueKind.String:
                return StringComparer.Ordinal.Equals(expected.GetString(), actual.GetString());
            default:
                return expected.GetRawText() == actual.GetRawText();
        }
    }

    public static async Task<StockPostingCommand> CreateAsync(
        AppDbContext db, string companyCode, string branchCode, int countId,
        IReadOnlyCollection<StockCountCostBasis> proposedBases,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(proposedBases);
        if (string.IsNullOrWhiteSpace(companyCode) || string.IsNullOrWhiteSpace(branchCode)
            || countId <= 0)
            throw new ArgumentException("A tenant and count ID are required.");

        var header = await db.IvStockCountHdrs.AsNoTracking()
            .Where(h => h.Id == countId && h.CompanyCode == companyCode
                && h.BranchCode == branchCode)
            .Select(h => new { h.Id, h.CountNo, h.CountDate, h.Status, h.RowVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null || header.Status is not ("COUNTED" or "ROLLED_BACK")
            || header.CountDate != header.CountDate.Date)
            throw Invalid($"Count {countId} is not an eligible date-only frozen sheet.");

        var lines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.StockCountId == countId)
            .OrderBy(l => l.LineNumber).ThenBy(l => l.Id)
            .Select(l => new
            {
                l.Id, l.LineNumber, l.BalLocId, l.ICode, l.IStatus,
                l.WHCode, l.LocCode, l.LotNo, l.PhysicalQty, l.RowVersion
            })
            .ToArrayAsync(cancellationToken);
        var counted = lines.Where(l => l.PhysicalQty.HasValue).ToArray();
        if (counted.Length == 0 || counted.Any(l => l.BalLocId <= 0
                || l.PhysicalQty < 0m || IvQty.Round(l.PhysicalQty!.Value) != l.PhysicalQty
                || string.IsNullOrWhiteSpace(l.ICode))
            || counted.Select(l => l.BalLocId).Distinct().Count() != counted.Length)
            throw Invalid($"Count {countId} has no unique valid counted piles.");
        var countedIds = counted.Select(l => l.Id).ToArray();

        var positiveIds = counted.Where(l => l.PhysicalQty > 0m)
            .Select(l => l.Id).Order().ToArray();
        var watermark = await db.StockPostingBranchSequences.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode && s.BranchCode == branchCode)
            .Select(s => (long?)s.LastSequence)
            .SingleOrDefaultAsync(cancellationToken) ?? 0L;
        var bases = proposedBases.OrderBy(b => b.StockCountLineId).ToArray();
        if (!bases.Select(b => b.StockCountLineId).SequenceEqual(positiveIds)
            || bases.Any(b => b.CompanyCode != companyCode || b.BranchCode != branchCode
                || b.FrozenFallbackUnitCost < 0m
                || StockLedgerPrecision.Money(b.FrozenFallbackUnitCost) != b.FrozenFallbackUnitCost
                || (b.CostBasisKind is StockCountCostBasisKinds.LedgerWac
                    or StockCountCostBasisKinds.LedgerFifo)
                    && header.Status == "COUNTED"
                    && b.CostBasisWatermark != watermark
                || !ValidBasis(b)))
            throw Invalid($"Count {countId} lacks one frozen cost basis per positive line.");

        var effectiveAt = StockReplayEventKey.CountEndOfDay(header.CountDate);
        var countedById = counted.ToDictionary(l => l.Id);
        if (bases.Length > 0)
        {
            var costMethod = await new StockCostMethodResolver().ResolveAsync(
                db, companyCode, branchCode, effectiveAt, cancellationToken);
            if (bases.Any(b =>
                    (b.CostBasisKind == StockCountCostBasisKinds.LedgerWac
                        && costMethod != StockCostMethods.MovingAverage)
                    || (b.CostBasisKind == StockCountCostBasisKinds.StandardRevision
                        && costMethod != StockCostMethods.Standard)))
                throw Invalid($"Count {countId} has a frozen basis for a different cost method.");
            if (bases.Any(b => b.CostBasisKind == StockCountCostBasisKinds.LedgerFifo))
                throw Invalid($"Count {countId} requires a verified historical FIFO layer basis.");
        }
        foreach (var basis in bases.Where(b =>
                     b.CostBasisKind == StockCountCostBasisKinds.LedgerWac))
        {
            var itemCode = countedById[basis.StockCountLineId].ICode;
            var captured = await StockCountLedgerCostEvidence.CaptureWacAsync(
                db, companyCode, branchCode, basis.StockCountLineId,
                itemCode, effectiveAt, basis.CostBasisWatermark!.Value,
                cancellationToken);
            if (captured.UnitCost != basis.FrozenFallbackUnitCost
                || !StringComparer.Ordinal.Equals(captured.Hash, basis.CostBasisHash))
                throw Invalid($"Count {countId} line {basis.StockCountLineId} WAC basis differs from its original V2 evidence.");
        }
        var standardResolver = new ItemStandardCostResolver();
        foreach (var basis in bases.Where(b =>
                     b.CostBasisKind == StockCountCostBasisKinds.StandardRevision))
        {
            var itemCode = countedById[basis.StockCountLineId].ICode;
            var standard = await standardResolver.ResolveAsync(db, companyCode,
                branchCode, itemCode, effectiveAt, cancellationToken);
            if (standard.Id != basis.StandardCostRevisionId
                || standard.TotalStandardCost != basis.FrozenFallbackUnitCost)
                throw Invalid($"Count {countId} line {basis.StockCountLineId} Standard basis differs from its approved revision.");
        }

        var prior = await db.StockPostings.AsNoTracking()
            .Where(p => p.CompanyCode == companyCode && p.BranchCode == branchCode
                && p.SourceModule == "INVENTORY"
                && p.SourceDocumentType == "IV_STOCK_COUNT"
                && p.SourceDocumentId == countId.ToString(CultureInfo.InvariantCulture)
                && p.PostingRole == "PRIMARY")
            .Select(p => new { p.Id, p.DocumentRevision, p.SealedAtUtc })
            .ToArrayAsync(cancellationToken);
        if (prior.Any(p => p.SealedAtUtc is null)
            || header.Status == "COUNTED" && prior.Length != 0
            || header.Status == "ROLLED_BACK" && prior.Length == 0)
            throw Invalid($"Count {countId} has inconsistent anchor revision lineage.");
        if (!prior.Select(p => p.DocumentRevision).Order()
                .SequenceEqual(Enumerable.Range(0, prior.Length)))
            throw Invalid($"Count {countId} has invalid anchor revisions.");
        if (header.Status == "ROLLED_BACK")
        {
            var active = await StockCountGenerationResolver.ResolveActiveAsync(db,
                companyCode, branchCode, countedIds, cancellationToken);
            var priorIds = prior.Select(p => p.Id).ToHashSet();
            if (active.Values.Any(g => !g.IsRemoval || !priorIds.Contains(g.AnchorPostingId)))
                throw Invalid($"Count {countId} has no reconciled removal generation for repost.");
        }
        else if (await db.StockCountReplayGenerations.AsNoTracking()
            .AnyAsync(g => g.CompanyCode == companyCode && g.BranchCode == branchCode
                && countedIds.Contains(g.StockCountLineId), cancellationToken))
            throw Invalid($"Count {countId} already has posted line generations.");
        var revision = prior.Length;

        var frozenLines = counted.Select(l => new
        {
            l.Id, l.LineNumber, l.BalLocId, l.ICode, l.IStatus,
            l.WHCode, l.LocCode, l.LotNo, l.PhysicalQty
        }).ToArray();
        var frozenBases = bases.Select(b => new
        {
            b.StockCountLineId, b.FrozenFallbackUnitCost, b.CostBasisKind,
            b.StandardCostRevisionId, b.CostBasisWatermark, b.CostBasisHash,
            b.CostEvidenceType, b.CostOverrideReason, b.CostApprovedBy,
            b.CostApprovedAtUtc
        }).ToArray();
        var evidence = StockPostingFingerprint.Create(
            new { countId, header.CountNo, header.CountDate, revision, frozenLines, frozenBases },
            new { header.Id, header.CountNo, header.CountDate, header.RowVersion,
                lines, frozenBases });
        return new StockPostingCommand
        {
            RequestId = RequestId(companyCode, branchCode, countId, revision),
            CommandType = "IV_POST_STOCK_COUNT",
            SourceModule = "INVENTORY",
            SourceDocumentType = "IV_STOCK_COUNT",
            SourceDocumentId = countId.ToString(CultureInfo.InvariantCulture),
            SourceDocumentNo = header.CountNo,
            DocumentRevision = revision,
            EffectiveAt = effectiveAt,
            Evidence = evidence,
            SourceSnapshotSchemaVersion = 2,
            FreezeScopes = counted.Select(l => new StockFreezeScope(null, l.WHCode, l.LocCode))
                .Distinct().ToArray()
        };
    }

    private static Guid RequestId(string company, string branch, int id, int revision)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{company.Trim().ToUpperInvariant()}|{branch.Trim().ToUpperInvariant()}|IV_STOCK_COUNT|{id}|{revision}|PRIMARY"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    internal static bool ValidBasis(StockCountCostBasis basis)
    {
        if (basis.FrozenFallbackUnitCost < 0m
            || basis.FrozenFallbackUnitCost >= 10000000000000m
            || StockLedgerPrecision.Money(basis.FrozenFallbackUnitCost)
                != basis.FrozenFallbackUnitCost)
            return false;
        if (basis.CostBasisKind is StockCountCostBasisKinds.LedgerWac
            or StockCountCostBasisKinds.LedgerFifo)
            return basis.CostBasisWatermark is > 0
                && basis.CostBasisHash is { Length: 64 } hash
                && hash.All(Uri.IsHexDigit)
                && hash == hash.ToUpperInvariant()
                && basis.StandardCostRevisionId is null
                && basis.CostEvidenceType is null
                && basis.CostOverrideReason is null
                && basis.CostApprovedBy is null
                && basis.CostApprovedAtUtc is null;
        if (basis.CostBasisKind == StockCountCostBasisKinds.StandardRevision)
            return basis.StandardCostRevisionId is > 0
                && basis.CostBasisWatermark is null
                && basis.CostBasisHash is null
                && basis.CostEvidenceType is null
                && basis.CostOverrideReason is null
                && basis.CostApprovedBy is null
                && basis.CostApprovedAtUtc is null;
        var manual = basis.CostBasisKind == StockCountCostBasisKinds.ManualApproved;
        var zero = basis.CostBasisKind == StockCountCostBasisKinds.ZeroCostApproved;
        return (manual || zero)
            && basis.CostEvidenceType == basis.CostBasisKind
            && (manual ? basis.FrozenFallbackUnitCost > 0m
                : basis.FrozenFallbackUnitCost == 0m)
            && basis.CostBasisWatermark is null
            && basis.CostBasisHash is null
            && basis.StandardCostRevisionId is null
            && basis.CostOverrideReason is { Length: > 0 and <= 250 } reason
            && !string.IsNullOrWhiteSpace(reason)
            && basis.CostApprovedBy is { Length: > 0 and <= 100 } approver
            && !string.IsNullOrWhiteSpace(approver)
            && basis.CostApprovedAtUtc is not null;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
