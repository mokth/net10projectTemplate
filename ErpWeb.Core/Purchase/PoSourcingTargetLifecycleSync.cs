using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed record PoSourcingLineRemap(
    short? OldLineNo,
    short NewLineNo,
    string? ICode,
    string? PurchaseUom,
    decimal PurchaseQty,
    decimal StdQty,
    decimal UnitPrice,
    string? VendorCd);

internal sealed class PoSourcingPrSaveHook
{
    public bool PreserveExplicitLineUnitPrice { get; init; } = true;
    public required Func<AppDbContext, CancellationToken, Task<string?>> BeforeDocumentLocksAsync { get; init; }
    public required Func<AppDbContext, PoPr, IReadOnlyList<PoPrDetail>, CancellationToken, Task<string?>> BeforeCommitAsync { get; init; }
}

internal sealed class PoSourcingPoSaveHook
{
    public required Func<AppDbContext, CancellationToken, Task<string?>> BeforeDocumentLocksAsync { get; init; }
    public required Func<AppDbContext, PoOrder, IReadOnlyList<PoOrderDetail>, CancellationToken, Task<string?>> BeforeCommitAsync { get; init; }
}

internal static class PoSourcingTargetLifecycleSync
{
    public static async Task<string?> OnTargetUpdatedOrRevisedAsync(
        AppDbContext db,
        IPurchaseSourcingFeatureGuard? featureGuard,
        string companyCode,
        string branchCode,
        string targetDocType,
        string targetDocNo,
        short oldRelNo,
        short newRelNo,
        string? newHeaderSupplierCode,
        bool isRevise,
        IReadOnlyList<PoSourcingLineRemap> remappedLines,
        string actorUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        List<PoSourcingConversion> conversions;
        try
        {
            conversions = await db.PoSourcingConversions
                .Include(x => x.Lines)
                .Where(x => x.CompanyCode == companyCode
                            && x.BranchCode == branchCode
                            && x.TargetDocType == targetDocType
                            && x.TargetDocNo == targetDocNo
                            && x.TargetDocRelNo == oldRelNo
                            && x.Status == PoSourcingConversionStatuses.Active)
                .ToListAsync(cancellationToken);
        }
        catch (SqlException ex) when (ex.Number == 208) // Invalid object name (optional table not installed)
        {
            return null;
        }
        catch (Exception) when (featureGuard is null)
        {
            return null;
        }

        if (conversions.Count == 0)
        {
            return null;
        }

        var featureEnabled = featureGuard is not null
            && await featureGuard.IsEnabledAuthoritativeAsync(db, companyCode, lockRowForUpdate: false, cancellationToken);

        var awardLineIds = conversions
            .SelectMany(c => c.Lines.Select(l => l.AwardLineId))
            .Distinct()
            .ToList();

        var awardLines = await db.PoSourcingAwardLines
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && awardLineIds.Contains(x.AwardLineId))
            .ToDictionaryAsync(x => x.AwardLineId, cancellationToken);

        var otherCommittedByAwardLine = await db.PoSourcingConversionLines
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && awardLineIds.Contains(x.AwardLineId)
                        && x.LineLinkStatus == PoSourcingConversionStatuses.Active
                        && !(x.TargetDocType == targetDocType && x.TargetDocNo == targetDocNo && x.TargetDocRelNo == oldRelNo))
            .GroupBy(x => x.AwardLineId)
            .Select(g => new { AwardLineId = g.Key, TotalStdQty = g.Sum(x => x.EffectiveActiveStdQty) })
            .ToDictionaryAsync(x => x.AwardLineId, x => x.TotalStdQty, cancellationToken);

        foreach (var conv in conversions)
        {
            var detachedReasons = new List<string>();

            if (string.Equals(targetDocType, PoSourcingConversionTargetTypes.Po, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(conv.TargetSupplierCode) &&
                !string.Equals(conv.TargetSupplierCode.Trim(), newHeaderSupplierCode?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var msg = $"Supplier cannot be changed from awarded supplier '{conv.TargetSupplierCode}' on sourced PO '{targetDocNo}'.";
                if (featureEnabled)
                {
                    return msg;
                }

                detachedReasons.Add(msg);
            }

            foreach (var convLine in conv.Lines.Where(l => l.LineLinkStatus == PoSourcingConversionStatuses.Active))
            {
                var remap = remappedLines.FirstOrDefault(r => r.OldLineNo.HasValue && r.OldLineNo.Value == convLine.TargetLineNo);
                awardLines.TryGetValue(convLine.AwardLineId, out var awardLine);

                if (remap is null)
                {
                    // Line was removed from target document.
                    convLine.LineLinkStatus = PoSourcingConversionStatuses.Released;
                    convLine.EffectiveActiveStdQty = 0m;
                    continue;
                }

                if (awardLine is not null)
                {
                    var itemMismatch = !string.IsNullOrWhiteSpace(awardLine.ICodeSnapshot) &&
                                       !string.Equals(awardLine.ICodeSnapshot.Trim(), remap.ICode?.Trim(), StringComparison.OrdinalIgnoreCase);
                    var uomMismatch = !string.IsNullOrWhiteSpace(awardLine.AwardedUom) &&
                                      !string.Equals(awardLine.AwardedUom.Trim(), remap.PurchaseUom?.Trim(), StringComparison.OrdinalIgnoreCase);
                    var prVendorMismatch = string.Equals(targetDocType, PoSourcingConversionTargetTypes.Pr, StringComparison.OrdinalIgnoreCase) &&
                                           !string.IsNullOrWhiteSpace(awardLine.SuppCodeSnapshot) &&
                                           !string.IsNullOrWhiteSpace(remap.VendorCd) &&
                                           !string.Equals(awardLine.SuppCodeSnapshot.Trim(), remap.VendorCd.Trim(), StringComparison.OrdinalIgnoreCase);

                    if (itemMismatch || uomMismatch || prVendorMismatch)
                    {
                        var msg = $"Line {remap.NewLineNo} cannot change item, UOM, or awarded supplier because it is linked to Sourcing Award #{conv.AwardId}.";
                        if (featureEnabled)
                        {
                            return msg;
                        }

                        detachedReasons.Add(msg);
                    }

                    var otherCommitted = otherCommittedByAwardLine.GetValueOrDefault(convLine.AwardLineId, 0m);
                    var maxAllowedStdQty = awardLine.AwardedStdQty - otherCommitted;
                    if (remap.StdQty > maxAllowedStdQty + 0.0005m)
                    {
                        var msg = $"Line {remap.NewLineNo} standard quantity ({remap.StdQty:N4}) exceeds remaining approved sourcing award quantity ({maxAllowedStdQty:N4}).";
                        if (featureEnabled)
                        {
                            return msg;
                        }

                        detachedReasons.Add(msg);
                    }
                }

                convLine.TargetDocRelNo = newRelNo;
                convLine.TargetLineNo = remap.NewLineNo;
                convLine.ConvertedPurQty = remap.PurchaseQty;
                convLine.EffectiveActiveStdQty = remap.StdQty;
                convLine.TargetUnitPrice = remap.UnitPrice;
            }

            conv.TargetDocRelNo = newRelNo;
            conv.LastReconciledAtUtc = nowUtc;

            if (detachedReasons.Count > 0)
            {
                var combinedReason = Truncate(string.Join(" ", detachedReasons), 500);
                conv.Status = PoSourcingConversionStatuses.DetachedQuarantined;
                conv.ReconciliationNote = combinedReason;
                foreach (var convLine in conv.Lines)
                {
                    if (convLine.LineLinkStatus == PoSourcingConversionStatuses.Active)
                    {
                        convLine.LineLinkStatus = PoSourcingConversionStatuses.DetachedQuarantined;
                    }

                    if (awardLines.TryGetValue(convLine.AwardLineId, out var awardLine))
                    {
                        awardLine.IsQuarantined = true;
                        awardLine.QuarantineReason = combinedReason;
                    }
                }

                db.PoSourcingEvents.Add(new PoSourcingEvent
                {
                    SourcingId = conv.SourcingId,
                    AwardId = conv.AwardId,
                    ConversionId = conv.ConversionId,
                    CompanyCode = companyCode,
                    BranchCode = branchCode,
                    EventType = PoSourcingEventTypes.TargetDetachedWhileDisabled,
                    PriorStatus = PoSourcingConversionStatuses.Active,
                    NewStatus = PoSourcingConversionStatuses.DetachedQuarantined,
                    Reason = combinedReason,
                    ActorUserId = Truncate(actorUserId, 20),
                    OccurredAtUtc = nowUtc
                });
            }
            else if (conv.Lines.All(l => l.LineLinkStatus == PoSourcingConversionStatuses.Released))
            {
                conv.Status = PoSourcingConversionStatuses.Released;
                conv.ReconciliationNote = $"All sourced lines removed on {targetDocType} {targetDocNo} rev {newRelNo}.";
                db.PoSourcingEvents.Add(new PoSourcingEvent
                {
                    SourcingId = conv.SourcingId,
                    AwardId = conv.AwardId,
                    ConversionId = conv.ConversionId,
                    CompanyCode = companyCode,
                    BranchCode = branchCode,
                    EventType = PoSourcingEventTypes.TargetReleased,
                    PriorStatus = PoSourcingConversionStatuses.Active,
                    NewStatus = PoSourcingConversionStatuses.Released,
                    Reason = conv.ReconciliationNote,
                    ActorUserId = Truncate(actorUserId, 20),
                    OccurredAtUtc = nowUtc
                });
            }
            else
            {
                conv.ReconciliationNote = isRevise
                    ? $"Synchronized with {targetDocType} {targetDocNo} revision {newRelNo}."
                    : $"Synchronized with {targetDocType} {targetDocNo} update.";
                db.PoSourcingEvents.Add(new PoSourcingEvent
                {
                    SourcingId = conv.SourcingId,
                    AwardId = conv.AwardId,
                    ConversionId = conv.ConversionId,
                    CompanyCode = companyCode,
                    BranchCode = branchCode,
                    EventType = isRevise ? PoSourcingEventTypes.TargetRevised : PoSourcingEventTypes.TargetUpdated,
                    PriorStatus = PoSourcingConversionStatuses.Active,
                    NewStatus = PoSourcingConversionStatuses.Active,
                    Reason = conv.ReconciliationNote,
                    ActorUserId = Truncate(actorUserId, 20),
                    OccurredAtUtc = nowUtc
                });
            }
        }

        return null;
    }

    public static async Task OnTargetCancelledOrDeletedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string targetDocType,
        string targetDocNo,
        short targetDocRelNo,
        string actionVerb,
        string actorUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        List<PoSourcingConversion> conversions;
        try
        {
            conversions = await db.PoSourcingConversions
                .Include(x => x.Lines)
                .Where(x => x.CompanyCode == companyCode
                            && x.BranchCode == branchCode
                            && x.TargetDocType == targetDocType
                            && x.TargetDocNo == targetDocNo
                            && x.TargetDocRelNo == targetDocRelNo
                            && x.Status == PoSourcingConversionStatuses.Active)
                .ToListAsync(cancellationToken);
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            return;
        }
        catch
        {
            return;
        }

        if (conversions.Count == 0)
        {
            return;
        }

        var note = Truncate($"Released when {targetDocType} {targetDocNo} (rev {targetDocRelNo}) was {actionVerb}.", 500);
        foreach (var conv in conversions)
        {
            var prior = conv.Status;
            conv.Status = PoSourcingConversionStatuses.Released;
            conv.LastReconciledAtUtc = nowUtc;
            conv.ReconciliationNote = note;
            foreach (var line in conv.Lines)
            {
                line.LineLinkStatus = PoSourcingConversionStatuses.Released;
                line.EffectiveActiveStdQty = 0m;
            }

            db.PoSourcingEvents.Add(new PoSourcingEvent
            {
                SourcingId = conv.SourcingId,
                AwardId = conv.AwardId,
                ConversionId = conv.ConversionId,
                CompanyCode = companyCode,
                BranchCode = branchCode,
                EventType = PoSourcingEventTypes.TargetReleased,
                PriorStatus = prior,
                NewStatus = PoSourcingConversionStatuses.Released,
                Reason = note,
                ActorUserId = Truncate(actorUserId, 20),
                OccurredAtUtc = nowUtc
            });
        }
    }

    private static string Truncate(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLen ? trimmed : trimmed[..maxLen];
    }
}
