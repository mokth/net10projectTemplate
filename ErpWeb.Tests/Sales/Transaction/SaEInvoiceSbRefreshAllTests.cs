using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.EInvoiceLib.Model;
using ErpWeb.EInvoiceLib.Model.Document;

namespace ErpWeb.Tests.Sales.Transaction;
/// <summary>
/// The self-billed (LHDN 11 / 12 / 13) no-selection E-STATUS mode:
/// <see cref="ISaEInvoiceService.RefreshSubmittedAsync(string, PoSbQuery?, IProgress{SaEInvoiceRefreshProgress}?, CancellationToken)"/>.
///
/// <para>
/// This is the parity work that makes the self-billed lists behave like the sales lists' E-STATUS button.
/// The contracts are the same ones the invoice and CN/DN suites already pin — candidate set == the grid's
/// own query, pre-flight cap with zero MyInvois calls, blank-UUID skip, chunk boundaries, chunk-boundary
/// cancellation — plus two that are specific to a 3-family entry point: the family selects the source
/// table AND the authorizing menu, and it must not require the list's ACCESS right.
/// </para>
/// </summary>
[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.EInvoice)]
public class SaEInvoiceSbRefreshAllTests
{
    /// <summary>The self-billed grid's own filter object, as <c>DataSource.CurrentQuery</c> hands it over.</summary>
    private static PoSbQuery Scope(string? searchText = null) =>
        new() { SearchText = searchText, SortDescending = true };

    /// <summary>
    /// Synchronous progress sink. <see cref="Progress{T}"/> posts to the captured context and would race
    /// the assertions on a build agent with no SynchronizationContext.
    /// </summary>
    private sealed class RecordingProgress : IProgress<SaEInvoiceRefreshProgress>
    {
        public List<SaEInvoiceRefreshProgress> Reports { get; } = [];

        public void Report(SaEInvoiceRefreshProgress value) => Reports.Add(value);
    }

    /// <summary>
    /// Progress sink that stops the run the moment the first report lands. That report is the candidate
    /// total, so the run has passed the cap gate and finished counting but has not started a chunk yet.
    /// </summary>
    private sealed class StopOnFirstReport : IProgress<SaEInvoiceRefreshProgress>
    {
        private readonly CancellationTokenSource _cts;

        public StopOnFirstReport(CancellationTokenSource cts) => _cts = cts;

        public List<SaEInvoiceRefreshProgress> Reports { get; } = [];

        public void Report(SaEInvoiceRefreshProgress value)
        {
            Reports.Add(value);
            _cts.Cancel();
        }
    }

    private static int DetailCalls(EInvoiceTestHost host) =>
        host.Helper.Calls.Count(x => x == nameof(FakeSubmitDocumentHelper.GetDocumentDetail));

    /// <summary>A successful MyInvois document-detail read, the canned response every refresh expects.</summary>
    private static GeneralResult<DocumentValidatation> ValidDetail() =>
        FakeSubmitDocumentHelper.Success(new DocumentValidatation
        {
            status = "Valid",
            dateTimeValidated = DateTime.UtcNow
        });

    // ───────────────────────── Family selection ─────────────────────────

    [Fact]
    public async Task RefreshSubmittedSb_an_invoice_run_never_touches_a_note()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedSbInvoicesAsync(3, i => $"SBI-{i:0000}");
        await host.SeedSubmittedSbNotesAsync(4, i => $"SBC-{i:0000}", type: "CN");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(
            EInvoiceDocumentTypes.SelfBilledInvoice, Scope());

        Assert.False(result.Refused, result.ErrorMessage);
        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, x => Assert.StartsWith("SBI-", x.DocumentNo));
        Assert.Equal(3, DetailCalls(host));

        // The note was not refreshed as a side effect: it is still SUBMITTED, not VALID.
        Assert.Equal(EInvoiceStatuses.Submitted, (await host.GetSbCdnAsync("SBC-0001"))!.IrbmStatus);
    }

    [Fact]
    public async Task RefreshSubmittedSb_a_credit_note_run_never_touches_a_debit_note()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedSbNotesAsync(3, i => $"SBC-{i:0000}", type: "CN");
        await host.SeedSubmittedSbNotesAsync(4, i => $"SBD-{i:0000}", type: "DN");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(
            EInvoiceDocumentTypes.SelfBilledCreditNote, Scope());

        Assert.False(result.Refused, result.ErrorMessage);
        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, x => Assert.StartsWith("SBC-", x.DocumentNo));
        Assert.Equal(3, DetailCalls(host));
        Assert.Equal(EInvoiceStatuses.Submitted, (await host.GetSbCdnAsync("SBD-0001"))!.IrbmStatus);
    }

    [Fact]
    public async Task RefreshSubmittedSb_a_debit_note_run_is_covered_and_isolated()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedSbNotesAsync(2, i => $"SBC-{i:0000}", type: "CN");
        await host.SeedSubmittedSbNotesAsync(2, i => $"SBD-{i:0000}", type: "DN");
        var service = host.CreateService();

        // SBD shares the PoSbCdn table but is its own LHDN family (13) and its own menu.
        var result = await service.RefreshSubmittedAsync(
            EInvoiceDocumentTypes.SelfBilledDebitNote, Scope());

        Assert.False(result.Refused, result.ErrorMessage);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, x => Assert.StartsWith("SBD-", x.DocumentNo));
        Assert.Equal(EInvoiceStatuses.Submitted, (await host.GetSbCdnAsync("SBC-0001"))!.IrbmStatus);
    }

    [Theory]
    [InlineData("", "SBI, SBC or SBD")]
    [InlineData("  ", "SBI, SBC or SBD")]
    [InlineData("INV", "SBI, SBC or SBD")]
    [InlineData("CN", "SBI, SBC or SBD")]
    public async Task RefreshSubmittedSb_rejects_a_type_that_is_not_a_self_billed_family(
        string documentType, string expectedMessage)
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedSbInvoicesAsync(2, i => $"SBI-{i:0000}");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(documentType, Scope());

        // A refusal, never a throw and never a silent empty run.
        Assert.True(result.Refused);
        Assert.Contains(expectedMessage, result.ErrorMessage);
        Assert.Empty(result.Items);
        Assert.Empty(host.Helper.Calls);
    }

    // ───────────────────────── Authorization ─────────────────────────

    [Theory]
    [InlineData(EInvoiceDocumentTypes.SelfBilledInvoice, MenuCodes.PurchaseSbInvoice)]
    [InlineData(EInvoiceDocumentTypes.SelfBilledCreditNote, MenuCodes.PurchaseSbCreditNote)]
    [InlineData(EInvoiceDocumentTypes.SelfBilledDebitNote, MenuCodes.PurchaseSbDebitNote)]
    public async Task RefreshSubmittedSb_checks_the_familys_own_menu(string documentType, string menuCode)
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedSbInvoicesAsync(1, i => $"SBI-{i:0000}");
        await host.SeedSubmittedSbNotesAsync(1, i => $"SBC-{i:0000}", type: "CN");
        await host.SeedSubmittedSbNotesAsync(1, i => $"SBD-{i:0000}", type: "DN");
        var service = host.CreateService();

        await service.RefreshSubmittedAsync(documentType, Scope());

        Assert.Contains((menuCode, PermissionCodes.Submit), host.PermissionChecks);

        // Authorization is per family: a run never consults the other families' menus.
        foreach (var other in new[]
                 {
                     MenuCodes.PurchaseSbInvoice,
                     MenuCodes.PurchaseSbCreditNote,
                     MenuCodes.PurchaseSbDebitNote
                 })
        {
            if (other != menuCode)
            {
                Assert.DoesNotContain((other, PermissionCodes.Submit), host.PermissionChecks);
            }
        }
    }

    [Fact]
    public async Task RefreshSubmittedSb_is_refused_without_the_familys_submit_right_and_touches_nothing()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedSbInvoicesAsync(3, i => $"SBI-{i:0000}");
        host.DeniedPermissions.Add(PermissionCodes.Submit);
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(
            EInvoiceDocumentTypes.SelfBilledInvoice, Scope());

        Assert.True(result.Refused);
        Assert.Empty(result.Items);
        Assert.Empty(host.Helper.Calls);
    }

    /// <summary>
    /// A Submit-only role must be able to refresh. The self-billed overload deliberately gates on
    /// <c>Submit</c> and NOT on <c>Access</c>: the list screen already requires Access to be read at all,
    /// so demanding it again here would lock out exactly the operator the button is for. This is the test
    /// that fails if the implementation ever routes the candidate query through the list SERVICE (whose
    /// <c>SearchAsync</c> checks Access) instead of through the shared applier.
    /// </summary>
    [Fact]
    public async Task RefreshSubmittedSb_works_for_a_submit_only_role_without_the_access_right()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedSbInvoicesAsync(2, i => $"SBI-{i:0000}");
        host.DeniedPermissions.Add(PermissionCodes.Access);
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(
            EInvoiceDocumentTypes.SelfBilledInvoice, Scope());

        Assert.False(result.Refused, result.ErrorMessage);
        Assert.Equal(2, result.Items.Count);
    }

    // ───────────────────────── Candidate set == the grid's query ─────────────────────────

    [Fact]
    public async Task RefreshSubmittedSb_refreshes_only_the_rows_the_grid_filter_matches()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();

        // 100 candidates span two pages (PoSbLimits.MaxPageSize is 100), so this also proves the
        // enumeration pages rather than reading a single page and stopping.
        await host.SeedSubmittedSbInvoicesAsync(
            100, i => i <= 12 ? $"SBI-MATCH-{i:000}" : $"SBI-OTHER-{i:000}");
        var service = host.CreateService();

        // The scope is the grid's own query, resolved through PoSbQueryApplier. The service must not widen
        // it or reinterpret the search text: this catches a second, drifting copy of the filter code.
        var result = await service.RefreshSubmittedAsync(
            EInvoiceDocumentTypes.SelfBilledInvoice, Scope(searchText: "SBI-MATCH"));

        Assert.False(result.Refused, result.ErrorMessage);
        Assert.Equal(12, result.Items.Count);
        Assert.All(result.Items, x => Assert.StartsWith("SBI-MATCH", x.DocumentNo));
        Assert.Equal(12, DetailCalls(host));
    }

    [Fact]
    public async Task RefreshSubmittedSb_pins_the_candidate_status_to_submitted()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();

        // VALID cannot move further, FAILED needs Recover and a blank status was never submitted: none of
        // them is a refresh candidate.
        await host.SeedSbInvoiceAsync("SBI-VALID", irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-V");
        await host.SeedSbInvoiceAsync("SBI-FAILED", irbmStatus: EInvoiceStatuses.Failed, irbmUuid: "UUID-F");
        await host.SeedSbInvoiceAsync("SBI-NEW");
        await host.SeedSbInvoiceAsync("SBI-SENT", irbmStatus: EInvoiceStatuses.Submitted, irbmUuid: "UUID-S");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(
            EInvoiceDocumentTypes.SelfBilledInvoice, Scope());

        Assert.False(result.Refused, result.ErrorMessage);
        Assert.Equal("SBI-SENT", Assert.Single(result.Items).DocumentNo);
        Assert.Equal(1, DetailCalls(host));
    }

    [Fact]
    public async Task RefreshSubmittedSb_ignores_another_branch()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedSbInvoicesAsync(3, i => $"SBI-HQ-{i}", branchCode: EInvoiceTestHost.Branch);
        await host.SeedSubmittedSbInvoicesAsync(4, i => $"SBI-B2-{i}", branchCode: "B2");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(
            EInvoiceDocumentTypes.SelfBilledInvoice, Scope());

        Assert.False(result.Refused, result.ErrorMessage);
        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, x => Assert.StartsWith("SBI-HQ-", x.DocumentNo));
        Assert.Equal(3, DetailCalls(host));
    }

    // ───────────────────────── Cap, chunking, skip, cancellation ─────────────────────────

    [Fact]
    public async Task RefreshSubmittedSb_refuses_a_run_over_the_cap_and_makes_no_api_call()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedSbInvoicesAsync(
            SaEInvoiceLimits.MaxRefreshAllRun + 1, i => $"SBI-{i:0000}");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(
            EInvoiceDocumentTypes.SelfBilledInvoice, Scope());

        Assert.True(result.Refused);
        Assert.Contains((SaEInvoiceLimits.MaxRefreshAllRun + 1).ToString(), result.ErrorMessage);
        Assert.Contains(SaEInvoiceLimits.MaxRefreshAllRun.ToString(), result.ErrorMessage);
        Assert.Contains("self-billed invoices", result.ErrorMessage);
        Assert.Empty(result.Items);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task RefreshSubmittedSb_names_the_note_family_in_an_over_cap_refusal()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedSbNotesAsync(
            SaEInvoiceLimits.MaxRefreshAllRun + 1, i => $"SBD-{i:0000}", type: "DN");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(
            EInvoiceDocumentTypes.SelfBilledDebitNote, Scope());

        Assert.True(result.Refused);
        Assert.Contains("self-billed debit notes", result.ErrorMessage);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task RefreshSubmittedSb_allows_a_run_at_the_cap_and_counts_every_candidate()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();

        // 200 candidates span two pages (PoSbLimits.MaxPageSize is 100), so the reported total proves the
        // enumeration walked the whole set instead of stopping at the first page.
        await host.SeedSubmittedSbInvoicesAsync(
            SaEInvoiceLimits.MaxRefreshAllRun, i => $"SBI-{i:0000}");
        var service = host.CreateService();

        // Stopping from the first progress report asserts the pre-flight cap DECISION without paying for
        // 200 round trips; a token cancelled up front would abort the candidate query instead.
        using var cts = new CancellationTokenSource();
        var progress = new StopOnFirstReport(cts);

        var result = await service.RefreshSubmittedAsync(
            EInvoiceDocumentTypes.SelfBilledInvoice, Scope(), progress, cts.Token);

        Assert.False(result.Refused, result.ErrorMessage);
        Assert.Empty(result.Items);
        Assert.Equal(SaEInvoiceLimits.MaxRefreshAllRun, Assert.Single(progress.Reports).Total);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task RefreshSubmittedSb_refreshes_in_batches_of_the_interactive_cap()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedSbInvoicesAsync(25, i => $"SBI-{i:0000}");
        var service = host.CreateService();
        var progress = new RecordingProgress();

        var result = await service.RefreshSubmittedAsync(
            EInvoiceDocumentTypes.SelfBilledInvoice, Scope(), progress);

        Assert.False(result.Refused, result.ErrorMessage);
        Assert.Equal(25, result.SucceededCount);
        Assert.Equal(25, DetailCalls(host));

        // Read-only: nothing was ever submitted or cancelled.
        Assert.DoesNotContain(nameof(FakeSubmitDocumentHelper.SubmitInvoices), host.Helper.Calls);
        Assert.DoesNotContain(nameof(FakeSubmitDocumentHelper.CancelDocument), host.Helper.Calls);

        // 25 keys at a batch cap of 10 puts boundaries at 10 and 20, which only progress can show.
        Assert.Equal(
            new[] { (0, 25), (10, 25), (20, 25), (25, 25) },
            progress.Reports.Select(x => (x.Done, x.Total)));
    }

    [Fact]
    public async Task RefreshSubmittedSb_reports_a_missing_uuid_as_skipped_and_never_calls_the_api_for_it()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSbInvoiceAsync("SBI-1001", irbmStatus: EInvoiceStatuses.Submitted);
        await host.SeedSbInvoiceAsync(
            "SBI-2001", irbmStatus: EInvoiceStatuses.Submitted, irbmUuid: "UUID-SBI-2001");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(
            EInvoiceDocumentTypes.SelfBilledInvoice, Scope());

        var skipped = Assert.Single(result.Items, x => x.DocumentNo == "SBI-1001");
        Assert.True(skipped.Skipped);
        Assert.False(skipped.Succeeded);
        Assert.Equal(EInvoiceStatuses.Submitted, skipped.Status);
        Assert.Equal("Missing IRBMUUID", skipped.ErrorMessage);

        // Skipped means never attempted: only the addressable document reached MyInvois. It is also never
        // escalated to Recover — a refresh is read-only.
        Assert.Equal(1, DetailCalls(host));
        Assert.True(result.Items.Single(x => x.DocumentNo == "SBI-2001").Succeeded);
    }

    [Fact]
    public async Task RefreshSubmittedSb_keeps_completed_results_when_stopped_between_batches()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedSbInvoicesAsync(25, i => $"SBI-{i:0000}");
        var service = host.CreateService();

        using var cts = new CancellationTokenSource();
        var reads = 0;
        host.Helper.DocumentDetailHandler = _ =>
        {
            // Pull the plug inside the second batch (a batch is 10 keys), never between batches.
            if (++reads == SaEInvoiceLimits.MaxBatchSelection + 1)
            {
                cts.Cancel();
            }

            return ValidDetail();
        };

        var result = await service.RefreshSubmittedAsync(
            EInvoiceDocumentTypes.SelfBilledInvoice, Scope(), null, cts.Token);

        // The in-flight batch finishes, the third never starts, nothing attempted is dropped.
        Assert.False(result.Refused, result.ErrorMessage);
        Assert.Equal(20, result.Items.Count);
        Assert.True(reads >= SaEInvoiceLimits.MaxBatchSelection + 1);
        Assert.All(
            Enumerable.Range(1, 20),
            i => Assert.Contains(result.Items, x => x.DocumentNo == $"SBI-{i:0000}"));
        Assert.DoesNotContain(result.Items, x => x.DocumentNo == "SBI-0021");
    }
}
