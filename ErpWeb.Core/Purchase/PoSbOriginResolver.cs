using ErpWeb.Core.EInvoice;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

/// <summary>Why an origin could not be used. <see cref="None"/> means the origin is usable.</summary>
public enum PoSbOriginFailure
{
    None,

    /// <summary>The note did not name an originating invoice.</summary>
    Missing,

    /// <summary>No such invoice in this company and branch.</summary>
    NotFound,

    /// <summary>The origin is not VALID at MyInvois yet.</summary>
    NotValid,

    /// <summary>The origin reached VALID without a UUID (should not happen, but is refused rather than guessed).</summary>
    MissingUuid,

    /// <summary>The origin was cancelled at MyInvois.</summary>
    Cancelled
}

/// <summary>Outcome of <see cref="PoSbOriginResolver.ResolveValidAsync"/>.</summary>
public sealed record PoSbOriginResult(PoSbInvoice? Origin, PoSbOriginFailure Failure, string Message)
{
    public bool Ok => Failure == PoSbOriginFailure.None && Origin is not null;

    /// <summary>Field key for the entry screen / validator, matching the shipped origin error keys.</summary>
    public string ErrorKey => Failure switch
    {
        PoSbOriginFailure.Missing or PoSbOriginFailure.NotFound => "OriginInvoice.No",
        PoSbOriginFailure.Cancelled => "OriginInvoice.Cancelled",
        _ => "OriginInvoice.Uuid"
    };
}

/// <summary>
/// The ONE place a self-billed credit/debit note's originating invoice is resolved and validated.
/// <para>
/// Every rule lives here so the save-time warning and the submit-time hard gate cannot drift apart:
/// the origin is looked up within the <b>note's own company and branch</b> (never by a bare document
/// number), it must be a live self-billed invoice, it must be <c>VALID</c> at MyInvois, and it must
/// carry the UUID the payload's <c>BillingReference</c> needs.
/// </para>
/// </summary>
public static class PoSbOriginResolver
{
    public static async Task<PoSbOriginResult> ResolveValidAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string? originSbInvNo,
        CancellationToken cancellationToken = default)
    {
        var docNo = PoSbCalc.NullIfBlank(originSbInvNo);
        if (docNo is null)
        {
            return new PoSbOriginResult(null, PoSbOriginFailure.Missing,
                "The originating self-billed invoice number is required.");
        }

        var origin = await db.PoSbInvoices.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.DocNo == docNo,
                cancellationToken);

        if (origin is null)
        {
            return new PoSbOriginResult(null, PoSbOriginFailure.NotFound,
                $"Self-billed invoice {docNo} was not found in this company and branch.");
        }

        var status = EInvoiceStatuses.Normalize(origin.IrbmStatus);

        if (status == EInvoiceStatuses.Cancelled)
        {
            return new PoSbOriginResult(origin, PoSbOriginFailure.Cancelled,
                $"Self-billed invoice {docNo} was cancelled at MyInvois, so it cannot be referenced.");
        }

        if (status != EInvoiceStatuses.Valid)
        {
            return new PoSbOriginResult(origin, PoSbOriginFailure.NotValid,
                $"Self-billed invoice {docNo} must be VALID at MyInvois before a note can reference it (current status: {status}).");
        }

        if (string.IsNullOrWhiteSpace(origin.IrbmUuid))
        {
            return new PoSbOriginResult(origin, PoSbOriginFailure.MissingUuid,
                $"Self-billed invoice {docNo} has no MyInvois UUID, so a note cannot reference it.");
        }

        return new PoSbOriginResult(origin, PoSbOriginFailure.None, string.Empty);
    }
}
