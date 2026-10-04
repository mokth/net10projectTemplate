using ErpWeb.Core.EInvoice;

namespace ErpWeb.Tests;

/// <summary>
/// <see cref="SaEInvoicePortalLink.Create"/> / <see cref="SaEInvoicePortalLink.IsPortalViewable"/> - the
/// ONE gate that decides whether a grid's MyInvois UUID cell may be opened at the LHDN portal.
///
/// <para>
/// The status rule is pinned here because the sales invoice, sales CN/DN and both self-billed screens
/// all share this code: relaxing or narrowing the viewable set changes every one of them, so it must
/// fail loudly rather than drift with a code comment.
/// </para>
///
/// <para>
/// SUBMITTED is part of the viewable set on purpose. It is NOT sufficient by itself - the share URL
/// also needs a MyInvois <c>longId</c>, which LHDN returns only once the document is valid - and the
/// missing-long-id case is pinned below so the relaxation can never turn into "open a broken page".
/// </para>
/// </summary>
[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.EInvoice)]
public class SaEInvoicePortalLinkTests
{
    private const string Uuid = "UUID-INV-1001";
    private const string LongId = "LONG-1";
    private const string Portal = "https://portal.test";

    // ───────────────────────────── which statuses may open ─────────────────────────────

    [Fact]
    public void Submitted_is_viewable()
    {
        // The regression this file exists for: a document MyInvois has accepted but not yet validated
        // must still reach the portal from the E-UUID cell.
        Assert.True(SaEInvoicePortalLink.IsPortalViewable(EInvoiceStatuses.Submitted));
    }

    [Theory]
    [InlineData(EInvoiceStatuses.Submitted)]
    [InlineData(EInvoiceStatuses.Valid)]
    [InlineData(EInvoiceStatuses.Cancelled)]
    public void Create_builds_the_link_for_every_viewable_status(string status)
    {
        var link = SaEInvoicePortalLink.Create(Uuid, LongId, status, Portal);

        Assert.NotNull(link);
        Assert.Equal(status, link!.Status);
        Assert.Equal($"{Portal}/{Uuid}/share/{LongId}", link.Url);
    }

    [Theory]
    [InlineData("submitted")]
    [InlineData("Submitted")]
    [InlineData("valid")]
    [InlineData("Valid")]
    [InlineData("cancelled")]
    [InlineData("Cancelled")]
    public void Create_is_case_insensitive_about_the_status(string status)
    {
        // Legacy rows hold title-case literals while ErpWeb writes uppercase ones; both must work.
        var link = SaEInvoicePortalLink.Create(Uuid, LongId, status, Portal);

        Assert.NotNull(link);
        Assert.Equal(EInvoiceStatuses.Normalize(status), link!.Status);
    }

    [Theory]
    [InlineData(EInvoiceStatuses.New)]
    [InlineData(EInvoiceStatuses.Submitting)]
    [InlineData(EInvoiceStatuses.Invalid)]
    [InlineData(EInvoiceStatuses.Failed)]
    [InlineData(EInvoiceStatuses.Rejected)]
    [InlineData("")]
    public void Create_returns_null_for_a_status_that_has_no_portal_page(string status) =>
        Assert.Null(SaEInvoicePortalLink.Create(Uuid, LongId, status, Portal));

    [Fact]
    public void Create_returns_null_for_a_null_status() =>
        Assert.Null(SaEInvoicePortalLink.Create(Uuid, LongId, null, Portal));

    [Fact]
    public void IsPortalViewable_matches_what_Create_accepts()
    {
        // One definition: the predicate and the builder must never disagree about a known status.
        foreach (var status in EInvoiceStatuses.All)
        {
            var created = SaEInvoicePortalLink.Create(Uuid, LongId, status, Portal) is not null;

            Assert.Equal(SaEInvoicePortalLink.IsPortalViewable(status), created);
        }
    }

    // ───────────────────────────── the other two requirements ─────────────────────────────

    [Fact]
    public void Create_returns_null_without_a_long_id()
    {
        // MyInvois returns longId only for a valid document, so a genuinely pending SUBMITTED document
        // must resolve to no link - the caller then reports "No LHDN link..." instead of opening a URL
        // the portal would refuse.
        Assert.Null(SaEInvoicePortalLink.Create(Uuid, null, EInvoiceStatuses.Submitted, Portal));
        Assert.Null(SaEInvoicePortalLink.Create(Uuid, "   ", EInvoiceStatuses.Submitted, Portal));
    }

    [Fact]
    public void Create_returns_null_without_a_uuid_or_a_portal_url()
    {
        Assert.Null(SaEInvoicePortalLink.Create(null, LongId, EInvoiceStatuses.Valid, Portal));
        Assert.Null(SaEInvoicePortalLink.Create("  ", LongId, EInvoiceStatuses.Valid, Portal));
        Assert.Null(SaEInvoicePortalLink.Create(Uuid, LongId, EInvoiceStatuses.Valid, null));
        Assert.Null(SaEInvoicePortalLink.Create(Uuid, LongId, EInvoiceStatuses.Valid, "  "));
    }

    [Fact]
    public void Create_trims_the_identifiers_and_a_trailing_portal_slash()
    {
        var link = SaEInvoicePortalLink.Create($"  {Uuid}  ", $"  {LongId}  ", EInvoiceStatuses.Valid, $"{Portal}/");

        Assert.NotNull(link);
        Assert.Equal(Uuid, link!.Uuid);
        Assert.Equal(LongId, link.LongId);
        Assert.Equal($"{Portal}/{Uuid}/share/{LongId}", link.Url);
    }
}
