using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.EInvoiceLib.Model;
using ErpWeb.EInvoiceLib.Model.Document;
using ErpWeb.Model.Repositories.Sales;

namespace ErpWeb.Tests;

/// <summary>
/// The batch e-Invoice surface on the Sales Invoice list: Submit, E-Status (refresh) and Cancel over a
/// selection of documents.
///
/// <para>
/// The contracts under test are the ones the list page depends on and the single-invoice panel must not
/// lose: normalization and cap order, per-row result semantics, revalidation after a pre-flight reading,
/// skip-not-abort behaviour, and independent commits for cancel. The submit path shares one engine with
/// the single-document panel, so these tests also guard that delegation.
/// </para>
/// </summary>
public class SaEInvoiceBatchTests
{
    private static SaEInvoiceDocumentKey Key(string invNo) => EInvoiceTestHost.InvoiceKey(invNo);

    private static string[] TenInvoiceNumbers() =>
        [.. Enumerable.Range(1, 10).Select(i => $"INV-{i:0000}")];

    // ─────────────────────────────── Submit: happy and mixed paths ───────────────────────────────

    [Fact]
    public async Task SubmitMany_submits_every_eligible_invoice_in_one_call_and_logs_each()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync("INV-1001");
        await host.SeedInvoiceAsync("INV-2001");
        await host.SeedInvoiceAsync("INV-3001");
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.AcceptedMany(infos.Select(x => x.DocumentNo));
        var service = host.CreateService();

        var result = await service.SubmitManyAsync([Key("INV-1001"), Key("INV-2001"), Key("INV-3001")]);

        Assert.False(result.Refused);
        Assert.Equal(3, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(
            ["INV-1001", "INV-2001", "INV-3001"],
            result.Items.Select(x => x.DocumentNo));
        Assert.All(result.Items, x => Assert.Equal(EInvoiceStatuses.Submitted, x.Status));

        // One MyInvois submission for the whole batch, carrying all three documents.
        Assert.Equal(1, host.Helper.Calls.Count(x => x == nameof(FakeSubmitDocumentHelper.SubmitInvoices)));
        Assert.Equal(3, host.Helper.Submitted.Count);

        // One audit row per document, all on the same attempt number (each document has its own chain).
        foreach (var invNo in new[] { "INV-1001", "INV-2001", "INV-3001" })
        {
            var log = Assert.Single(await host.LogsAsync(invNo), x => x.Action == EInvoiceActions.Submit);
            Assert.Equal(EInvoiceStatuses.Submitted, log.Status);
            Assert.Equal(1, log.AttemptNo);
        }
    }

    [Fact]
    public async Task SubmitMany_reports_accepted_and_rejected_rows_individually()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync("INV-1001");
        await host.SeedInvoiceAsync("INV-2001");
        host.Helper.SubmitHandler = _ => FakeSubmitDocumentHelper.Mixed(
            ["INV-1001"],
            [("INV-2001", "Classification code is not valid.")]);
        var service = host.CreateService();

        var result = await service.SubmitManyAsync([Key("INV-1001"), Key("INV-2001")]);

        var accepted = result.Items.Single(x => x.DocumentNo == "INV-1001");
        Assert.True(accepted.Succeeded);
        Assert.False(accepted.Skipped);
        Assert.Equal(EInvoiceStatuses.Submitted, accepted.Status);
        Assert.Equal("UUID-INV-1001", accepted.Uuid);

        var rejected = result.Items.Single(x => x.DocumentNo == "INV-2001");
        Assert.False(rejected.Succeeded);
        Assert.False(rejected.Skipped);
        Assert.False(rejected.RecoveryRequired);
        Assert.Equal(EInvoiceStatuses.Rejected, rejected.Status);
        Assert.Contains("Classification", rejected.ErrorMessage);

        Assert.Equal(EInvoiceStatuses.Rejected, (await host.GetInvoiceAsync(invNo: "INV-2001"))!.IrbmStatus);
    }

    [Fact]
    public async Task SubmitMany_marks_a_document_absent_from_the_response_as_unknown_and_recovery_required()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync("INV-1001");
        await host.SeedInvoiceAsync("INV-2001");

        // The response carries per-document lists but never mentions INV-2001.
        host.Helper.SubmitHandler = _ => FakeSubmitDocumentHelper.Mixed(["INV-1001"], []);
        var service = host.CreateService();

        var result = await service.SubmitManyAsync([Key("INV-1001"), Key("INV-2001")]);

        var missing = result.Items.Single(x => x.DocumentNo == "INV-2001");
        Assert.False(missing.Succeeded);
        Assert.False(missing.Skipped);
        Assert.True(missing.RecoveryRequired);
        Assert.Equal(EInvoiceStatuses.Failed, missing.Status);
        Assert.Equal(EInvoiceOutcomes.Unknown, missing.Outcome);

        var saved = await host.GetInvoiceAsync(invNo: "INV-2001");
        Assert.Equal(EInvoiceStatuses.Failed, saved!.IrbmStatus);
        Assert.Equal(EInvoiceOutcomes.Unknown, saved.IrbmOutcome);
    }

    [Fact]
    public async Task SubmitMany_marks_every_claimed_row_unknown_when_the_submit_call_throws()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync("INV-1001");
        await host.SeedInvoiceAsync("INV-2001");
        await host.SeedInvoiceAsync("INV-3001");
        host.Helper.SubmitHandler = _ => throw new InvalidOperationException("MyInvois is unreachable.");
        var service = host.CreateService();

        var result = await service.SubmitManyAsync([Key("INV-1001"), Key("INV-2001"), Key("INV-3001")]);

        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(3, result.FailedCount);
        Assert.All(result.Items, x =>
        {
            Assert.False(x.Skipped);
            Assert.True(x.RecoveryRequired);
            Assert.Equal(EInvoiceStatuses.Failed, x.Status);
            Assert.Equal(EInvoiceOutcomes.Unknown, x.Outcome);
        });

        // No claimed document may be left in SUBMITTING, and each one owns an audit row.
        foreach (var invNo in new[] { "INV-1001", "INV-2001", "INV-3001" })
        {
            var saved = await host.GetInvoiceAsync(invNo: invNo);
            Assert.Equal(EInvoiceStatuses.Failed, saved!.IrbmStatus);
            var log = Assert.Single(await host.LogsAsync(invNo), x => x.Action == EInvoiceActions.Submit);
            Assert.Equal(EInvoiceStatuses.Failed, log.Status);
        }
    }

    [Fact]
    public async Task SubmitMany_skips_a_stuck_submitting_row_and_asks_for_recovery()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        // Older than SubmittingStuckMinutes (15), so the row is genuinely stuck.
        await host.SeedInvoiceAsync("INV-1001",
            irbmStatus: EInvoiceStatuses.Submitting,
            modifiedDate: DateTime.UtcNow.AddMinutes(-30));
        var service = host.CreateService();

        var result = await service.SubmitManyAsync([Key("INV-1001")]);

        var item = Assert.Single(result.Items);
        Assert.True(item.Skipped);
        Assert.True(item.RecoveryRequired);
        Assert.Equal(EInvoiceStatuses.Submitting, item.Status);
        Assert.Empty(host.Helper.Calls);
    }

    // ─────────────────────────────── Normalization and cap ───────────────────────────────

    [Fact]
    public async Task SubmitMany_deduplicates_case_insensitively_before_applying_the_cap()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync("INV-1001");
        await host.SeedInvoiceAsync("INV-2001");
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.AcceptedMany(infos.Select(x => x.DocumentNo));
        var service = host.CreateService();

        // Three selections, two documents. The duplicate is dropped, not counted.
        var result = await service.SubmitManyAsync([Key("INV-1001"), Key("inv-1001"), Key("INV-2001")]);

        Assert.False(result.Refused);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(2, host.Helper.Submitted.Count);
        Assert.Equal(1, host.Helper.Calls.Count(x => x == nameof(FakeSubmitDocumentHelper.SubmitInvoices)));
    }

    [Fact]
    public async Task SubmitMany_allows_ten_unique_keys_with_duplicates_and_refuses_eleven()
    {
        var invNos = TenInvoiceNumbers();

        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        foreach (var invNo in invNos)
        {
            await host.SeedInvoiceAsync(invNo);
        }

        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.AcceptedMany(infos.Select(x => x.DocumentNo));
        var service = host.CreateService();

        // 10 unique documents plus a duplicate: the cap is measured on the deduplicated set.
        var selection = invNos.Select(invNo => Key(invNo)).Append(Key("INV-0001")).ToList();
        var accepted = await service.SubmitManyAsync(selection);

        Assert.False(accepted.Refused);
        Assert.Equal(10, accepted.SucceededCount);
        Assert.Equal(10, host.Helper.Submitted.Count);

        host.Helper.Calls.Clear();

        // 11 unique documents: refused before any load or HTTP call.
        var refused = await service.SubmitManyAsync(invNos.Append("INV-9999").Select(invNo => Key(invNo)).ToList());

        Assert.True(refused.Refused);
        Assert.Equal(SaEInvoiceErrorKind.Validation, refused.ErrorKind);
        Assert.Empty(refused.Items);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task SubmitMany_makes_no_call_when_every_row_is_ineligible()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync("INV-1001", status: SaInvoiceStatuses.New);
        var service = host.CreateService();

        var result = await service.SubmitManyAsync([Key("INV-1001")]);

        var item = Assert.Single(result.Items);
        Assert.True(item.Skipped);
        Assert.False(item.Succeeded);
        Assert.Contains("POSTED", item.ErrorMessage);
        Assert.Empty(host.Helper.Calls);
    }

    // ─────────────────────────────── Authorization and revalidation ───────────────────────────────

    [Fact]
    public async Task SubmitMany_is_refused_without_the_submit_permission()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.DeniedPermissions.Add(PermissionCodes.Submit);
        var service = host.CreateService();

        var result = await service.SubmitManyAsync([Key("INV-1001")]);

        Assert.True(result.Refused);
        Assert.Equal(SaEInvoiceErrorKind.Authorization, result.ErrorKind);
        Assert.Empty(result.Items);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task CancelMany_is_refused_without_the_cancel_permission()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-INV-1001");
        host.DeniedPermissions.Add(PermissionCodes.Cancel);
        var service = host.CreateService();

        var result = await service.CancelManyAsync([Key("INV-1001")], "Wrong buyer details");

        Assert.True(result.Refused);
        Assert.Equal(SaEInvoiceErrorKind.Authorization, result.ErrorKind);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task SubmitMany_revalidates_eligibility_instead_of_trusting_the_pre_flight()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        var service = host.CreateService();
        var key = Key("INV-1001");

        // The pre-flight snapshot says the invoice can be submitted...
        var preview = await service.GetStatusManyAsync([key]);
        Assert.True(preview[0]!.CanSubmit);

        // ...but another session submitted it before the user confirmed.
        await host.UpdateInvoiceAsync(EInvoiceTestHost.InvNo, x =>
        {
            x.IrbmStatus = EInvoiceStatuses.Valid;
            x.IrbmUuid = "UUID-RACED";
        });

        var result = await service.SubmitManyAsync([key]);

        var item = Assert.Single(result.Items);
        Assert.True(item.Skipped);
        Assert.False(item.Succeeded);
        Assert.Equal(EInvoiceStatuses.Valid, item.Status);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task GetStatusManyAsync_is_aligned_with_the_keys_and_nulls_unknown_documents()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync("INV-1001", irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-INV-1001");
        var service = host.CreateService();

        var views = await service.GetStatusManyAsync([Key("INV-1001"), Key("INV-MISSING")]);

        Assert.Equal(2, views.Count);
        Assert.NotNull(views[0]);
        Assert.Equal(EInvoiceStatuses.Valid, views[0]!.Status);
        Assert.True(views[0]!.CanCancel);
        Assert.Null(views[1]);
    }

    // ─────────────────────────────── Cancel ───────────────────────────────

    [Fact]
    public async Task CancelMany_keeps_an_earlier_success_when_a_later_cancel_fails()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync("INV-1001", irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-INV-1001");
        await host.SeedInvoiceAsync("INV-2001", irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-INV-2001");
        await host.SeedInvoiceAsync("INV-3001", irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-INV-3001");

        var cancelCalls = 0;
        host.Helper.CancelHandler = _ => ++cancelCalls == 2
            ? FakeSubmitDocumentHelper.Failure<CancelRespone>("MyInvois rejected the cancellation.", "IncorrectState")
            : FakeSubmitDocumentHelper.Success(new CancelRespone { status = "cancelled" });
        var service = host.CreateService();

        var result = await service.CancelManyAsync(
            [Key("INV-1001"), Key("INV-2001"), Key("INV-3001")],
            "Wrong buyer details");

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(EInvoiceStatuses.Cancelled, result.Items[0].Status);
        Assert.Equal(EInvoiceStatuses.Valid, result.Items[1].Status);
        Assert.Equal(EInvoiceStatuses.Cancelled, result.Items[2].Status);

        // A later failure must never roll back an earlier success.
        Assert.Equal(EInvoiceStatuses.Cancelled, (await host.GetInvoiceAsync(invNo: "INV-1001"))!.IrbmStatus);
        Assert.Equal(EInvoiceStatuses.Valid, (await host.GetInvoiceAsync(invNo: "INV-2001"))!.IrbmStatus);
        Assert.Equal(EInvoiceStatuses.Cancelled, (await host.GetInvoiceAsync(invNo: "INV-3001"))!.IrbmStatus);

        Assert.Single(await host.LogsAsync("INV-1001"), x => x.Action == EInvoiceActions.Cancel);
        Assert.Single(await host.LogsAsync("INV-2001"), x => x.Action == EInvoiceActions.Cancel);
        Assert.Single(await host.LogsAsync("INV-3001"), x => x.Action == EInvoiceActions.Cancel);
    }

    [Fact]
    public async Task CancelMany_batch_cancels_each_document_with_one_call_and_exactly_one_uuid()
    {
        var invNos = TenInvoiceNumbers();

        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        foreach (var invNo in invNos)
        {
            await host.SeedInvoiceAsync(invNo, irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-" + invNo);
        }

        var service = host.CreateService();

        var result = await service.CancelManyAsync(
            invNos.Select(invNo => Key(invNo)).ToList(),
            "Wrong buyer details");

        Assert.Equal(10, result.SucceededCount);
        Assert.Equal(10, host.Helper.CancelledUuids.Count);
        Assert.All(host.Helper.CancelledUuids, uuids => Assert.Single(uuids));
        Assert.All(result.Items, x => Assert.Equal(EInvoiceStatuses.Cancelled, x.Status));
    }

    [Fact]
    public async Task CancelMany_skips_a_row_that_is_not_cancellable_without_calling_myinvois()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync("INV-1001", irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-INV-1001");
        await host.SeedInvoiceAsync("INV-2001", status: SaInvoiceStatuses.New, irbmStatus: EInvoiceStatuses.New);
        var service = host.CreateService();

        var result = await service.CancelManyAsync([Key("INV-1001"), Key("INV-2001")], "Wrong buyer details");

        Assert.True(result.Items[0].Succeeded);
        var skipped = result.Items[1];
        Assert.True(skipped.Skipped);
        Assert.Single(host.Helper.CancelledUuids);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CancelMany_rejects_a_blank_reason(string? reason)
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-INV-1001");
        var service = host.CreateService();

        var result = await service.CancelManyAsync([Key("INV-1001")], reason!);

        Assert.True(result.Refused);
        Assert.Equal(SaEInvoiceErrorKind.Validation, result.ErrorKind);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task Cancel_reason_is_limited_to_300_characters_at_the_service_boundary()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-INV-1001");
        var service = host.CreateService();
        var key = Key("INV-1001");

        var tooLong = new string('x', 301);
        var refused = await service.CancelManyAsync([key], tooLong);
        Assert.True(refused.Refused);
        Assert.Equal(SaEInvoiceErrorKind.Validation, refused.ErrorKind);
        Assert.Empty(host.Helper.Calls);

        // The single-document path shares the same rule.
        var single = await service.CancelAsync(key, tooLong);
        Assert.False(single.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.Validation, single.ErrorKind);
        Assert.Empty(host.Helper.Calls);

        // Exactly 300 characters is allowed and reaches MyInvois.
        var exact = new string('y', 300);
        var accepted = await service.CancelManyAsync([key], exact);
        Assert.Equal(1, accepted.SucceededCount);
        Assert.Single(host.Helper.CancelledUuids);
        Assert.Equal(exact, host.Helper.CancelledDocuments[0].reason);
    }

    // ─────────────────────────────── Refresh ───────────────────────────────

    [Fact]
    public async Task RefreshMany_keeps_the_status_and_records_the_error_when_a_detail_read_fails()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync("INV-1001", irbmStatus: EInvoiceStatuses.Submitted, irbmUuid: "UUID-INV-1001");
        await host.SeedInvoiceAsync("INV-2001", irbmStatus: EInvoiceStatuses.Submitted, irbmUuid: "UUID-INV-2001");
        host.Helper.DocumentDetailHandler = uuid => uuid == "UUID-INV-1001"
            ? FakeSubmitDocumentHelper.Failure<DocumentValidatation>("Document not found.", "404")
            : FakeSubmitDocumentHelper.Success(new DocumentValidatation { status = "Valid", dateTimeValidated = DateTime.UtcNow });
        var service = host.CreateService();

        var result = await service.RefreshManyAsync([Key("INV-1001"), Key("INV-2001")]);

        var failed = result.Items.Single(x => x.DocumentNo == "INV-1001");
        Assert.False(failed.Succeeded);
        Assert.Equal(EInvoiceStatuses.Submitted, failed.Status);
        Assert.False(failed.RecoveryRequired);

        var refreshed = result.Items.Single(x => x.DocumentNo == "INV-2001");
        Assert.True(refreshed.Succeeded);
        Assert.Equal(EInvoiceStatuses.Valid, refreshed.Status);

        var saved = await host.GetInvoiceAsync(invNo: "INV-1001");
        Assert.Equal(EInvoiceStatuses.Submitted, saved!.IrbmStatus);
        Assert.NotNull(saved.IrbmError);
        Assert.Equal(EInvoiceStatuses.Valid, (await host.GetInvoiceAsync(invNo: "INV-2001"))!.IrbmStatus);
    }

    [Fact]
    public async Task RefreshMany_does_not_abort_when_one_detail_read_throws()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync("INV-1001", irbmStatus: EInvoiceStatuses.Submitted, irbmUuid: "UUID-INV-1001");
        await host.SeedInvoiceAsync("INV-2001", irbmStatus: EInvoiceStatuses.Submitted, irbmUuid: "UUID-INV-2001");
        host.Helper.DocumentDetailHandler = uuid => uuid == "UUID-INV-1001"
            ? throw new InvalidOperationException("MyInvois timed out.")
            : FakeSubmitDocumentHelper.Success(new DocumentValidatation { status = "Valid", dateTimeValidated = DateTime.UtcNow });
        var service = host.CreateService();

        var result = await service.RefreshManyAsync([Key("INV-1001"), Key("INV-2001")]);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(EInvoiceStatuses.Submitted, result.Items[0].Status);
        Assert.Equal(EInvoiceStatuses.Valid, result.Items[1].Status);
    }

    // ─────────────────────────────── Grouping guard ───────────────────────────────

    [Fact]
    public async Task SubmitMany_calls_the_note_generator_for_credit_notes_and_groups_by_family()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();

        // The CN needs a VALID origin invoice, which is a different document from the one being submitted.
        await host.SeedInvoiceAsync("INV-9001", irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-INV-9001");
        await host.SeedCreditNoteAsync("CN-1001", originInvNo: "INV-9001");
        await host.SeedInvoiceAsync("INV-1001");

        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.AcceptedMany(infos.Select(x => x.DocumentNo));
        host.Helper.NoteSubmitHandler = infos => FakeSubmitDocumentHelper.AcceptedMany(infos.Select(x => x.DocumentNo));
        var service = host.CreateService();

        var result = await service.SubmitManyAsync([Key("INV-1001"), EInvoiceTestHost.CdnKey("CN-1001")]);

        Assert.Equal(2, result.SucceededCount);
        Assert.Contains(nameof(FakeSubmitDocumentHelper.SubmitInvoices), host.Helper.Calls);
        Assert.Contains(nameof(FakeSubmitDocumentHelper.SubmitCreditDebitNotes), host.Helper.Calls);
        Assert.Equal(2, host.Helper.Submitted.Count);
    }

    // ───────────── Refresh-all: E-STATUS with nothing selected (legacy GetEStatus) ─────────────

    /// <summary>The grid's own filter object, exactly as <c>DataSource.CurrentQuery</c> hands it over.</summary>
    private static SaInvoiceListQuery Scope(string? searchText = null)
        => new() { SearchText = searchText, SortDescending = true };

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
    /// total, so the run has passed the cap gate and finished counting but has not started a chunk yet —
    /// exactly the point a cap-boundary assertion wants to inspect without paying for the whole run.
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

    [Fact]
    public async Task SearchPagedAsync_filters_the_list_on_irbm_status()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync("INV-1001", irbmStatus: EInvoiceStatuses.Submitted, irbmUuid: "UUID-1001");
        await host.SeedInvoiceAsync("INV-2001", irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-2001");
        await host.SeedInvoiceAsync("INV-3001");

        // Built through the mapper the grid and the refresh-all share, so this pins the ONE definition of
        // the filter rather than a lookalike copy of it.
        var submitted = SaInvoiceQueryMapper.ToSearchArgs(
            new SaInvoiceListQuery { IrbmStatus = EInvoiceStatuses.Submitted }, skip: 0, take: 50);
        var unfiltered = SaInvoiceQueryMapper.ToSearchArgs(new SaInvoiceListQuery(), skip: 0, take: 50);

        await using var db = await host.Factory.CreateDbContextAsync();
        var repo = new SaInvoiceRepository();

        var (filtered, filteredTotal) = await repo.SearchPagedAsync(
            db, EInvoiceTestHost.Company, EInvoiceTestHost.Branch, submitted);
        Assert.Equal(1, filteredTotal);
        Assert.Equal("INV-1001", Assert.Single(filtered).InvNo);

        // Null means "no e-Invoice filter", not "match NULL".
        var (all, allTotal) = await repo.SearchPagedAsync(
            db, EInvoiceTestHost.Company, EInvoiceTestHost.Branch, unfiltered);
        Assert.Equal(3, allTotal);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task RefreshSubmitted_refreshes_nothing_when_no_invoice_is_submitted()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();

        // VALID is deliberately not a candidate: a refresh cannot move it any further.
        await host.SeedInvoiceAsync("INV-1001", irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-INV-1001");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(Scope());

        Assert.False(result.Refused);
        Assert.Empty(result.Items);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task RefreshSubmitted_refreshes_only_the_rows_the_grid_filter_matches()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedInvoicesAsync(100, i => i <= 12 ? $"INV-MATCH-{i:000}" : $"INV-OTHER-{i:000}");
        var service = host.CreateService();

        // The scope is the grid's own query. The service must not widen it or reinterpret the search text:
        // this is the test that catches a second, drifting copy of the filter code.
        var result = await service.RefreshSubmittedAsync(Scope(searchText: "INV-MATCH"));

        Assert.False(result.Refused);
        Assert.Equal(12, result.Items.Count);
        Assert.All(result.Items, x => Assert.StartsWith("INV-MATCH", x.DocumentNo));
        Assert.Equal(12, DetailCalls(host));
    }

    [Fact]
    public async Task RefreshSubmitted_refuses_a_run_over_the_cap_and_makes_no_api_call()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedInvoicesAsync(SaInvoiceLimits.MaxEInvoiceRefreshAllRun + 1, i => $"INV-{i:0000}");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(Scope());

        Assert.True(result.Refused);
        Assert.Contains((SaInvoiceLimits.MaxEInvoiceRefreshAllRun + 1).ToString(), result.ErrorMessage);
        Assert.Contains(SaInvoiceLimits.MaxEInvoiceRefreshAllRun.ToString(), result.ErrorMessage);
        Assert.Empty(result.Items);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task RefreshSubmitted_allows_a_run_at_the_cap_and_counts_every_candidate()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();

        // The cap count also spans two repository pages (MaxPageSize is 100), so the reported total proves
        // the enumeration walked the whole set instead of stopping at the first page.
        await host.SeedSubmittedInvoicesAsync(SaInvoiceLimits.MaxEInvoiceRefreshAllRun, i => $"INV-{i:0000}");
        var service = host.CreateService();

        // Stopping from the first progress report asserts the cap DECISION and the full count: the run was
        // allowed to start, and it enumerated every candidate before any MyInvois traffic. Paying 200
        // round trips to prove the same thing would be pure waste, and a token cancelled up front would
        // abort the candidate query itself instead of the run.
        using var cts = new CancellationTokenSource();
        var progress = new StopOnFirstReport(cts);

        var result = await service.RefreshSubmittedAsync(Scope(), progress, cts.Token);

        Assert.False(result.Refused);
        Assert.Empty(result.Items);
        Assert.Equal(SaInvoiceLimits.MaxEInvoiceRefreshAllRun, Assert.Single(progress.Reports).Total);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task RefreshSubmitted_refreshes_in_batches_of_the_interactive_cap()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedInvoicesAsync(25, i => $"INV-{i:0000}");
        var service = host.CreateService();
        var progress = new RecordingProgress();

        var result = await service.RefreshSubmittedAsync(Scope(), progress);

        Assert.False(result.Refused);
        Assert.Equal(25, result.SucceededCount);
        Assert.Equal(25, DetailCalls(host));

        // A read-only run: nothing was ever submitted or cancelled.
        Assert.DoesNotContain(nameof(FakeSubmitDocumentHelper.SubmitInvoices), host.Helper.Calls);
        Assert.DoesNotContain(nameof(FakeSubmitDocumentHelper.CancelDocument), host.Helper.Calls);

        // 25 keys at a batch cap of 10 puts boundaries at 10 and 20, which only progress can show.
        Assert.Equal(
            new[] { (0, 25), (10, 25), (20, 25), (25, 25) },
            progress.Reports.Select(x => (x.Done, x.Total)));
    }

    [Fact]
    public async Task RefreshSubmitted_keeps_completed_results_when_stopped_between_batches()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedInvoicesAsync(25, i => $"INV-{i:0000}");
        var service = host.CreateService();

        using var cts = new CancellationTokenSource();
        var reads = 0;
        host.Helper.DocumentDetailHandler = _ =>
        {
            // Pull the plug inside the second batch (a batch is 10 keys), never between batches.
            if (++reads == SaInvoiceLimits.MaxEInvoiceBatchSelection + 1)
            {
                cts.Cancel();
            }

            return FakeSubmitDocumentHelper.Success(new DocumentValidatation
            {
                status = "Valid",
                dateTimeValidated = DateTime.UtcNow
            });
        };

        var result = await service.RefreshSubmittedAsync(Scope(), null, cts.Token);

        // The in-flight batch finishes, the third never starts, and nothing already attempted is dropped.
        // The exact read count inside batch 2 is EF's business (a cancelled token can throw before the
        // HTTP call), so the boundary is asserted through which documents made it into the result.
        Assert.False(result.Refused);
        Assert.Equal(20, result.Items.Count);
        Assert.True(reads >= SaInvoiceLimits.MaxEInvoiceBatchSelection + 1);
        Assert.All(
            Enumerable.Range(1, 20),
            i => Assert.Contains(result.Items, x => x.DocumentNo == $"INV-{i:0000}"));
        Assert.DoesNotContain(result.Items, x => x.DocumentNo == "INV-0021");
    }

    [Fact]
    public async Task RefreshSubmitted_reports_a_missing_uuid_as_skipped_and_never_calls_the_api_for_it()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync("INV-1001", irbmStatus: EInvoiceStatuses.Submitted);
        await host.SeedInvoiceAsync("INV-2001", irbmStatus: EInvoiceStatuses.Submitted, irbmUuid: "UUID-INV-2001");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(Scope());

        var skipped = Assert.Single(result.Items, x => x.DocumentNo == "INV-1001");
        Assert.True(skipped.Skipped);
        Assert.False(skipped.Succeeded);
        Assert.Equal(EInvoiceStatuses.Submitted, skipped.Status);
        Assert.Equal("Missing IRBMUUID", skipped.ErrorMessage);

        // Skipped means never attempted: only the addressable document reached MyInvois.
        Assert.Equal(1, DetailCalls(host));
        Assert.True(result.Items.Single(x => x.DocumentNo == "INV-2001").Succeeded);
    }

    [Fact]
    public async Task RefreshSubmitted_ignores_another_branchs_submitted_invoices()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedInvoicesAsync(3, i => $"INV-HQ-{i}", branchCode: EInvoiceTestHost.Branch);
        await host.SeedSubmittedInvoicesAsync(4, i => $"INV-B2-{i}", branchCode: "B2");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(Scope());

        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, x => Assert.StartsWith("INV-HQ", x.DocumentNo));
    }

    [Fact]
    public async Task RefreshSubmitted_is_refused_without_the_submit_right_and_touches_nothing()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedInvoicesAsync(3, i => $"INV-{i:0000}");
        host.DeniedPermissions.Add(PermissionCodes.Submit);
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(Scope());

        Assert.True(result.Refused);
        Assert.Empty(host.Helper.Calls);
    }

    // ──────────── Refresh-all on the Credit / Debit Note list (SaCdnList parity) ────────────
    //
    // The CN/DN twin of the region above. It reuses the same driver, so what these tests pin is the part
    // that is genuinely new: the family comes from the scope's Type, the authorizing menu follows the
    // family, and the shared failure semantics hold for notes exactly as they do for invoices.

    /// <summary>The CN/DN grid's own filter object, as <c>DataSource.CurrentQuery</c> hands it over.</summary>
    private static SaCdnListQuery NoteScope(string? type = "CN", string? searchText = null)
        => new() { Type = type ?? string.Empty, SearchText = searchText, SortDescending = true };

    /// <summary>A successful MyInvois document-detail read, the canned response every refresh expects.</summary>
    private static GeneralResult<DocumentValidatation> ValidDetail() =>
        FakeSubmitDocumentHelper.Success(new DocumentValidatation
        {
            status = "Valid",
            dateTimeValidated = DateTime.UtcNow
        });

    [Fact]
    public async Task RefreshSubmittedNotes_refreshes_only_the_requested_family()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedCreditNotesAsync(3, i => $"CN-{i:0000}", type: "CN");
        await host.SeedSubmittedCreditNotesAsync(4, i => $"DN-{i:0000}", type: "DN");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(NoteScope("CN"));

        Assert.False(result.Refused);
        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, x => Assert.StartsWith("CN-", x.DocumentNo));
        Assert.Equal(3, DetailCalls(host));

        // The debit notes were not refreshed as a side effect: they are still SUBMITTED, not VALID.
        Assert.Equal(EInvoiceStatuses.Submitted, (await host.GetCdnAsync(docNo: "DN-0001"))!.IrbmStatus);
    }

    [Fact]
    public async Task RefreshSubmittedNotes_for_a_debit_note_run_checks_the_debit_note_menu()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedCreditNotesAsync(2, i => $"CN-{i:0000}", type: "CN");
        await host.SeedSubmittedCreditNotesAsync(2, i => $"DN-{i:0000}", type: "DN");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(NoteScope("DN"));

        Assert.False(result.Refused);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, x => Assert.StartsWith("DN-", x.DocumentNo));

        // Authorization is per family, so the DN run never consults the CN menu (contract C9).
        Assert.Contains((MenuCodes.SalesDebitNote, PermissionCodes.Submit), host.PermissionChecks);
        Assert.DoesNotContain((MenuCodes.SalesCreditNote, PermissionCodes.Submit), host.PermissionChecks);
        Assert.Equal(EInvoiceStatuses.Submitted, (await host.GetCdnAsync(docNo: "CN-0001"))!.IrbmStatus);
    }

    [Fact]
    public async Task RefreshSubmittedNotes_refreshes_only_the_rows_the_grid_filter_matches()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedCreditNotesAsync(100, i => i <= 12 ? $"CN-MATCH-{i:000}" : $"CN-OTHER-{i:000}");
        var service = host.CreateService();

        // The scope is the grid's own query, resolved through SaCdnQueryMapper. The service must not widen
        // it or reinterpret the search text: this catches a second, drifting copy of the filter code (C2).
        var result = await service.RefreshSubmittedAsync(NoteScope("CN", searchText: "CN-MATCH"));

        Assert.False(result.Refused);
        Assert.Equal(12, result.Items.Count);
        Assert.All(result.Items, x => Assert.StartsWith("CN-MATCH", x.DocumentNo));
        Assert.Equal(12, DetailCalls(host));
    }

    [Fact]
    public async Task RefreshSubmittedNotes_refuses_a_run_over_the_cap_and_makes_no_api_call()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedCreditNotesAsync(SaEInvoiceLimits.MaxRefreshAllRun + 1, i => $"CN-{i:0000}");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(NoteScope("CN"));

        Assert.True(result.Refused);
        Assert.Contains((SaEInvoiceLimits.MaxRefreshAllRun + 1).ToString(), result.ErrorMessage);
        Assert.Contains(SaEInvoiceLimits.MaxRefreshAllRun.ToString(), result.ErrorMessage);
        Assert.Contains("credit notes", result.ErrorMessage);
        Assert.Empty(result.Items);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task RefreshSubmittedNotes_allows_a_run_at_the_cap_and_counts_every_candidate()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();

        // 200 candidates span two repository pages (MaxPageSize is 100), so the reported total proves the
        // enumeration walked the whole set instead of stopping at the first page.
        await host.SeedSubmittedCreditNotesAsync(SaEInvoiceLimits.MaxRefreshAllRun, i => $"CN-{i:0000}");
        var service = host.CreateService();

        // Stopping from the first progress report asserts the pre-flight cap DECISION without paying for
        // 200 round trips; a token cancelled up front would abort the candidate query instead.
        using var cts = new CancellationTokenSource();
        var progress = new StopOnFirstReport(cts);

        var result = await service.RefreshSubmittedAsync(NoteScope("CN"), progress, cts.Token);

        Assert.False(result.Refused);
        Assert.Empty(result.Items);
        Assert.Equal(SaEInvoiceLimits.MaxRefreshAllRun, Assert.Single(progress.Reports).Total);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task RefreshSubmittedNotes_refreshes_in_batches_of_the_interactive_cap()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedCreditNotesAsync(25, i => $"CN-{i:0000}");
        var service = host.CreateService();
        var progress = new RecordingProgress();

        var result = await service.RefreshSubmittedAsync(NoteScope("CN"), progress);

        Assert.False(result.Refused);
        Assert.Equal(25, result.SucceededCount);
        Assert.Equal(25, DetailCalls(host));

        // Read-only: nothing was ever submitted or cancelled (C10).
        Assert.DoesNotContain(nameof(FakeSubmitDocumentHelper.SubmitInvoices), host.Helper.Calls);
        Assert.DoesNotContain(nameof(FakeSubmitDocumentHelper.CancelDocument), host.Helper.Calls);

        Assert.Equal(
            new[] { (0, 25), (10, 25), (20, 25), (25, 25) },
            progress.Reports.Select(x => (x.Done, x.Total)));
    }

    [Fact]
    public async Task RefreshSubmittedNotes_keeps_completed_results_when_stopped_between_batches()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedCreditNotesAsync(25, i => $"CN-{i:0000}");
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

        var result = await service.RefreshSubmittedAsync(NoteScope("CN"), null, cts.Token);

        // The in-flight batch finishes, the third never starts, nothing attempted is dropped (C7).
        Assert.False(result.Refused);
        Assert.Equal(20, result.Items.Count);
        Assert.True(reads >= SaEInvoiceLimits.MaxBatchSelection + 1);
        Assert.All(
            Enumerable.Range(1, 20),
            i => Assert.Contains(result.Items, x => x.DocumentNo == $"CN-{i:0000}"));
        Assert.DoesNotContain(result.Items, x => x.DocumentNo == "CN-0021");
    }

    [Fact]
    public async Task RefreshSubmittedNotes_reports_a_missing_uuid_as_skipped_and_never_calls_the_api_for_it()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedCreditNoteAsync(docNo: "CN-1001", irbmStatus: EInvoiceStatuses.Submitted);
        await host.SeedCreditNoteAsync(docNo: "CN-2001", irbmStatus: EInvoiceStatuses.Submitted, irbmUuid: "UUID-CN-2001");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(NoteScope("CN"));

        var skipped = Assert.Single(result.Items, x => x.DocumentNo == "CN-1001");
        Assert.True(skipped.Skipped);
        Assert.False(skipped.Succeeded);
        Assert.Equal(EInvoiceStatuses.Submitted, skipped.Status);
        Assert.Equal("Missing IRBMUUID", skipped.ErrorMessage);

        // Skipped means never attempted: only the addressable note reached MyInvois (C8).
        Assert.Equal(1, DetailCalls(host));
        Assert.True(result.Items.Single(x => x.DocumentNo == "CN-2001").Succeeded);
    }

    [Fact]
    public async Task RefreshSubmittedNotes_is_refused_without_the_familys_submit_right_and_touches_nothing()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedCreditNotesAsync(3, i => $"CN-{i:0000}");
        host.DeniedPermissions.Add(PermissionCodes.Submit);
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(NoteScope("CN"));

        Assert.True(result.Refused);
        Assert.Empty(host.Helper.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("INV")]
    [InlineData("XX")]
    public async Task RefreshSubmittedNotes_refuses_an_unknown_family_before_any_call(string? type)
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedCreditNotesAsync(3, i => $"CN-{i:0000}");
        var service = host.CreateService();

        var result = await service.RefreshSubmittedAsync(NoteScope(type));

        Assert.True(result.Refused);
        Assert.Equal(SaEInvoiceErrorKind.Validation, result.ErrorKind);
        Assert.Empty(result.Items);
        Assert.Empty(host.Helper.Calls);
    }

    // ─────────────────────────────── Failure semantics (C12) ───────────────────────────────

    [Fact]
    public async Task RefreshSubmittedNotes_continues_the_batch_when_one_document_fails()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedCreditNotesAsync(10, i => $"CN-{i:0000}");
        var service = host.CreateService();

        // One document's MyInvois read throws. RefreshAsync catches that per document, so the failure must
        // not stop the rest of its chunk or any later chunk.
        host.Helper.DocumentDetailHandler = uuid => uuid == "UUID-CN-0003"
            ? throw new InvalidOperationException("MyInvois is unreachable.")
            : ValidDetail();

        var result = await service.RefreshSubmittedAsync(NoteScope("CN"));

        Assert.False(result.Refused);
        Assert.Equal(10, result.Items.Count);
        Assert.Equal(9, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);

        var failed = Assert.Single(result.Items, x => x.DocumentNo == "CN-0003");
        Assert.False(failed.Succeeded);
        Assert.False(failed.Skipped);
        Assert.Equal(EInvoiceStatuses.Submitted, failed.Status);
    }

    [Fact]
    public async Task RefreshSubmittedNotes_converts_an_escaping_exception_instead_of_losing_completed_work()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedCreditNotesAsync(25, i => $"CN-{i:0000}");
        var service = host.CreateService();

        // Break the permission layer once the first chunk's documents have been read, so the SECOND chunk
        // throws during its authorization — the one place an exception escapes the batch primitive, since
        // every document's MyInvois call catches its own failures. Keyed on the detail reads (a document
        // processed) rather than on permission-check counts, because RefreshAsync re-authorizes per
        // document and those counts are an implementation detail.
        host.Helper.DocumentDetailHandler = _ => ValidDetail();
        host.OnPermissionCheck = () =>
        {
            if (ChunksCompleted(host) >= 1)
            {
                throw new InvalidOperationException("The permission store is unavailable.");
            }
        };

        var result = await service.RefreshSubmittedAsync(NoteScope("CN"));

        // Nothing is thrown away: every candidate is accounted for, the completed chunk is kept, and the
        // outstanding keys are reported as Failed rather than the exception reaching the caller.
        Assert.Equal(25, result.Items.Count);
        Assert.Equal(SaEInvoiceLimits.MaxBatchSelection, result.SucceededCount);
        Assert.Equal(25 - SaEInvoiceLimits.MaxBatchSelection, result.FailedCount);
        Assert.All(
            result.Items.Where(x => !x.Succeeded),
            x => Assert.Contains("permission store", x.ErrorMessage));
    }

    [Fact]
    public async Task RefreshSubmittedNotes_converts_the_remainder_when_a_later_chunk_is_refused()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedSubmittedCreditNotesAsync(25, i => $"CN-{i:0000}");
        var service = host.CreateService();

        // The right is revoked once the first chunk's documents have been read, so the SECOND chunk's
        // authorization gate refuses. A refused chunk must convert the current chunk AND every remaining
        // key into Failed items; none may be silently dropped.
        host.Helper.DocumentDetailHandler = _ => ValidDetail();
        host.OnPermissionCheck = () =>
        {
            if (ChunksCompleted(host) >= 1)
            {
                host.DeniedPermissions.Add(PermissionCodes.Submit);
            }
        };

        var result = await service.RefreshSubmittedAsync(NoteScope("CN"));

        Assert.Equal(25, result.Items.Count);
        Assert.Equal(SaEInvoiceLimits.MaxBatchSelection, result.SucceededCount);
        Assert.Equal(25 - SaEInvoiceLimits.MaxBatchSelection, result.FailedCount);
        Assert.All(
            result.Items.Where(x => !x.Succeeded),
            x => Assert.Contains("Not authorized", x.ErrorMessage));
    }

    /// <summary>
    /// How many full batches of documents have already been read from MyInvois. A deterministic chunk
    /// boundary: <see cref="SaEInvoiceLimits.MaxBatchSelection"/> reads means the next authorization check
    /// belongs to the following chunk.
    /// </summary>
    private static int ChunksCompleted(EInvoiceTestHost host) =>
        host.Helper.Calls.Count(x => x == nameof(FakeSubmitDocumentHelper.GetDocumentDetail))
        / SaEInvoiceLimits.MaxBatchSelection;
}
