using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.EInvoiceLib.Model;
using ErpWeb.EInvoiceLib.Model.Document;

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
}
