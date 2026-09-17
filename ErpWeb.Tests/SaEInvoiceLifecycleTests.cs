using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using ErpWeb.EInvoiceLib.Model.Document;

namespace ErpWeb.Tests;

/// <summary>
/// The production rules the LHDN e-Invoice façade owns: the lifecycle and its locking, the pre-submit
/// gate and duplicate protection, the recovery algorithm for an uncertain submission, the cancellation
/// window, the CN/DN origin rule, and per-company credential isolation.
///
/// <para>
/// These are the paths that protect the ERP from double-submitting a document to LHDN, so most
/// assertions are about what must NOT happen: <see cref="FakeSubmitDocumentHelper.Calls"/> proves a
/// refused action never reached MyInvois.
/// </para>
/// </summary>
public class SaEInvoiceLifecycleTests
{
    // ─────────────────────────────── Submit ───────────────────────────────

    [Fact]
    public async Task Submit_persists_submitting_then_records_the_accepted_uuid()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(result.Succeeded);
        Assert.Equal(EInvoiceStatuses.Submitted, result.Status);
        Assert.Equal("UUID-INV-1001", result.Uuid);
        Assert.Equal("SUB-1", result.SubmissionId);

        var saved = await host.GetInvoiceAsync();
        Assert.Equal(EInvoiceStatuses.Submitted, saved!.IrbmStatus);
        Assert.Equal("UUID-INV-1001", saved.IrbmUuid);
        Assert.Equal("SUB-1", saved.IrbmSubmitId);
        Assert.Null(saved.IrbmOutcome);
        Assert.NotNull(saved.IrbmSentOn);

        var logs = await host.LogsAsync(EInvoiceTestHost.InvNo);
        Assert.Equal(EInvoiceActions.Submit, logs[^1].Action);
        Assert.Equal(1, logs[^1].AttemptNo);
        Assert.Equal(EInvoiceStatuses.Submitted, logs[^1].Status);
    }

    [Fact]
    public async Task Submit_is_refused_for_a_document_that_was_already_submitted()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-EXISTING");
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.StateRule, result.ErrorKind);
        Assert.Equal(0, host.Helper.SubmitCallCount);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task Submit_is_refused_while_a_submission_is_still_in_flight()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Submitting, modifiedDate: DateTime.UtcNow);
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(result.Succeeded);
        Assert.Equal(0, host.Helper.SubmitCallCount);
    }

    [Fact]
    public async Task Rejected_document_is_recorded_with_the_myinvois_reason_and_can_be_retried()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Rejected(infos[0].DocumentNo, "Classification code 999 is not valid.");
        var service = host.CreateService();

        var rejected = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(rejected.Succeeded);
        Assert.Equal(EInvoiceStatuses.Rejected, rejected.Status);

        var saved = await host.GetInvoiceAsync();
        Assert.Equal(EInvoiceStatuses.Rejected, saved!.IrbmStatus);
        Assert.Contains("999", saved.IrbmError);

        // A rejection carries no UUID, so a retry after the fix is allowed.
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted(infos[0].DocumentNo, "UUID-AFTER-FIX");
        var retried = await service.RetryAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(retried.Succeeded);
        Assert.Equal(EInvoiceStatuses.Submitted, retried.Status);
    }

    [Fact]
    public async Task Erp_validation_failure_never_reaches_myinvois()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        // A line without a classification code is refused before any generation happens.
        await host.SeedInvoiceAsync(validLines: false);
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.Validation, result.ErrorKind);
        Assert.NotEmpty(result.ValidationErrors);
        Assert.Empty(host.Helper.Calls);

        // The status is left where it was, and the audit trail still records the attempt.
        var saved = await host.GetInvoiceAsync();
        Assert.Null(saved!.IrbmStatus);
        Assert.NotNull(saved.IrbmError);

        var logs = await host.LogsAsync(EInvoiceTestHost.InvNo);
        Assert.Single(logs);
        Assert.Equal(EInvoiceActions.Validate, logs[0].Action);
        Assert.Equal("ERP_VALIDATION", logs[0].ErrorCode);
    }

    [Fact]
    public async Task Disabled_company_profile_is_refused_before_any_myinvois_call()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync(enabled: false);
        await host.SeedInvoiceAsync();
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.Validation, result.ErrorKind);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task Unauthorized_user_is_refused_and_nothing_is_sent()
    {
        await using var host = EInvoiceTestHost.Create(authorized: false);
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.Authorization, result.ErrorKind);
        Assert.Empty(host.Helper.Calls);
        Assert.Contains(host.PermissionChecks, x =>
            x.Menu == MenuCodes.SalesInvoice && x.Permission == PermissionCodes.Submit);
    }

    // ─────────────────────────────── Timeout → Recover ───────────────────────────────

    [Fact]
    public async Task A_timeout_after_the_post_is_unknown_and_blocks_retry_until_recovery()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        // The transport died after the request left the process: no status code, no response body.
        host.Helper.SubmitHandler = _ => FakeSubmitDocumentHelper.Failure<ErpWeb.EInvoiceLib.Model.Document.SuccessSubmit>(
            "The operation was canceled.",
            errorCode: null);
        var service = host.CreateService();

        var submit = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(submit.Succeeded);
        Assert.Equal(EInvoiceStatuses.Failed, submit.Status);
        Assert.Equal(EInvoiceOutcomes.Unknown, submit.Outcome);
        Assert.True(submit.RecoveryRequired);

        var saved = await host.GetInvoiceAsync();
        Assert.Equal(EInvoiceStatuses.Failed, saved!.IrbmStatus);
        Assert.Equal(EInvoiceOutcomes.Unknown, saved.IrbmOutcome);

        // Retry must be refused: the first attempt may already exist at MyInvois.
        host.Helper.Calls.Clear();
        var retry = await service.RetryAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(retry.Succeeded);
        Assert.True(retry.RecoveryRequired);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task A_blank_token_is_a_confirmed_failure_so_retry_may_submit_again()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = _ => FakeSubmitDocumentHelper.NeverSent();
        var service = host.CreateService();

        var submit = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(submit.Succeeded);
        Assert.Equal(EInvoiceStatuses.Failed, submit.Status);
        Assert.Equal(EInvoiceOutcomes.ConfirmedFailure, submit.Outcome);
        Assert.False(submit.RecoveryRequired);

        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted(infos[0].DocumentNo, "UUID-RETRY");
        var retry = await service.RetryAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(retry.Succeeded);
        Assert.Equal(2, host.Helper.SubmitCallCount);
    }

    [Fact]
    public async Task Recover_finds_the_document_myinvois_accepted_and_never_submits_again()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Failed, irbmOutcome: EInvoiceOutcomes.Unknown, irbmSubmitId: "SUB-1");
        // The submission did land: MyInvois has the document and it is already Valid.
        host.Helper.SubmissionHandler = _ => FakeSubmitDocumentHelper.SubmissionWith(EInvoiceTestHost.InvNo, "Valid", "UUID-INV-1001");
        var service = host.CreateService();

        var result = await service.RecoverAsync(EInvoiceTestHost.InvoiceKey(), operatorInitiated: true);

        Assert.True(result.Succeeded);
        Assert.Equal(EInvoiceStatuses.Valid, result.Status);
        Assert.Equal("UUID-INV-1001", result.Uuid);

        var saved = await host.GetInvoiceAsync();
        Assert.Equal(EInvoiceStatuses.Valid, saved!.IrbmStatus);
        Assert.Equal("UUID-INV-1001", saved.IrbmUuid);
        Assert.Null(saved.IrbmOutcome);
        Assert.NotNull(saved.IrbmValidOn);

        Assert.Equal(0, host.Helper.SubmitCallCount);
        Assert.Contains(nameof(FakeSubmitDocumentHelper.GetSubmission), host.Helper.Calls);

        var logs = await host.LogsAsync(EInvoiceTestHost.InvNo);
        Assert.Equal(EInvoiceActions.Recover, logs[^1].Action);
    }

    [Fact]
    public async Task Recover_falls_back_to_the_document_search_when_there_is_no_submission_id()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Failed, irbmOutcome: EInvoiceOutcomes.Unknown);
        host.Helper.SearchHandler = _ => FakeSubmitDocumentHelper.SearchFound(EInvoiceTestHost.InvNo, "Valid", "UUID-FROM-SEARCH");
        var service = host.CreateService();

        var result = await service.RecoverAsync(EInvoiceTestHost.InvoiceKey(), operatorInitiated: true);

        Assert.True(result.Succeeded);
        Assert.Equal(EInvoiceStatuses.Valid, result.Status);
        Assert.Contains(nameof(FakeSubmitDocumentHelper.SearchDocument), host.Helper.Calls);
        Assert.DoesNotContain(nameof(FakeSubmitDocumentHelper.GetSubmission), host.Helper.Calls);

        var saved = await host.GetInvoiceAsync();
        Assert.Equal("UUID-FROM-SEARCH", saved!.IrbmUuid);
    }

    [Fact]
    public async Task Recover_treats_a_completed_submission_without_the_document_as_a_confirmed_failure()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Submitting, irbmSubmitId: "SUB-1", modifiedDate: DateTime.UtcNow.AddHours(-2));
        host.Helper.SubmissionHandler = _ => FakeSubmitDocumentHelper.SubmissionWithout(EInvoiceTestHost.InvNo);
        var service = host.CreateService();

        var result = await service.RecoverAsync(EInvoiceTestHost.InvoiceKey(), operatorInitiated: true);

        Assert.True(result.Succeeded);
        Assert.Equal(EInvoiceStatuses.Failed, result.Status);
        Assert.Equal(EInvoiceOutcomes.ConfirmedFailure, result.Outcome);

        // Confirmed absent, so Retry is now allowed and does submit.
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted(infos[0].DocumentNo, "UUID-AFTER-RECOVER");
        var retry = await service.RetryAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(retry.Succeeded);
        Assert.Equal(1, host.Helper.SubmitCallCount);
    }

    [Fact]
    public async Task Recover_stays_uncertain_when_myinvois_cannot_answer()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Failed, irbmOutcome: EInvoiceOutcomes.Unknown);
        host.Helper.SearchHandler = _ => FakeSubmitDocumentHelper.Failure<ErpWeb.EInvoiceLib.Model.Document.RecentDocument>("MyInvois is unavailable.");
        var service = host.CreateService();

        var result = await service.RecoverAsync(EInvoiceTestHost.InvoiceKey(), operatorInitiated: true);

        Assert.False(result.Succeeded);
        Assert.Equal(EInvoiceOutcomes.Unknown, result.Outcome);
        Assert.True(result.RecoveryRequired);

        // Still unknown: a submit must remain blocked.
        host.Helper.Calls.Clear();
        var submit = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());
        Assert.False(submit.Succeeded);
        Assert.True(submit.RecoveryRequired);
        Assert.Empty(host.Helper.Calls);
    }

    // ─────────────────────────────── Stuck SUBMITTING ───────────────────────────────

    [Fact]
    public async Task A_fresh_submitting_row_cannot_be_recovered_by_someone_who_did_not_ask()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Submitting, modifiedDate: DateTime.UtcNow);
        var service = host.CreateService();

        var result = await service.RecoverAsync(EInvoiceTestHost.InvoiceKey(), operatorInitiated: false);

        Assert.False(result.Succeeded);
        Assert.Empty(host.Helper.Calls);

        // It is untouched - never silently reset to NEW.
        var saved = await host.GetInvoiceAsync();
        Assert.Equal(EInvoiceStatuses.Submitting, saved!.IrbmStatus);
    }

    [Fact]
    public async Task A_stuck_submitting_row_is_recoverable_after_the_timeout()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(
            irbmStatus: EInvoiceStatuses.Submitting,
            irbmSubmitId: "SUB-1",
            modifiedDate: DateTime.UtcNow.AddMinutes(-30));
        host.Helper.SubmissionHandler = _ => FakeSubmitDocumentHelper.SubmissionWith(EInvoiceTestHost.InvNo, "Submitted");
        var service = host.CreateService();

        // An operator-initiated Recover is always allowed; the timeout gates the automatic path.
        var operatorResult = await service.RecoverAsync(EInvoiceTestHost.InvoiceKey(), operatorInitiated: true);
        Assert.True(operatorResult.Succeeded);
        Assert.Equal(EInvoiceStatuses.Submitted, operatorResult.Status);

        // Even a fresh SUBMITTING row is recoverable when the operator asks, but it is never re-submitted.
        Assert.Equal(0, host.Helper.SubmitCallCount);
        Assert.DoesNotContain(nameof(FakeSubmitDocumentHelper.SubmitInvoices), host.Helper.Calls);
    }

    [Fact]
    public async Task Submit_on_a_stuck_submitting_row_asks_for_recovery_instead_of_resubmitting()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(
            irbmStatus: EInvoiceStatuses.Submitting,
            modifiedDate: DateTime.UtcNow.AddMinutes(-30));
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(result.Succeeded);
        Assert.True(result.RecoveryRequired);
        Assert.Equal(0, host.Helper.SubmitCallCount);
    }

    // ─────────────────────────────── Refresh ───────────────────────────────

    [Fact]
    public async Task Refresh_maps_the_myinvois_status_and_clears_the_outcome()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Submitted, irbmUuid: "UUID-INV-1001");
        host.Helper.DocumentDetailHandler = _ => FakeSubmitDocumentHelper.Success(new DocumentValidatation
        {
            uuid = "UUID-INV-1001",
            status = "Valid",
            dateTimeValidated = DateTime.UtcNow
        });
        var service = host.CreateService();

        var result = await service.RefreshAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(result.Succeeded);
        Assert.Equal(EInvoiceStatuses.Valid, result.Status);

        var saved = await host.GetInvoiceAsync();
        Assert.Equal(EInvoiceStatuses.Valid, saved!.IrbmStatus);
        Assert.NotNull(saved.IrbmValidOn);
        Assert.Null(saved.IrbmOutcome);
    }

    [Fact]
    public async Task Refresh_is_refused_when_the_document_has_no_uuid()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.New);
        var service = host.CreateService();

        var result = await service.RefreshAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(result.Succeeded);
        Assert.Empty(host.Helper.Calls);
    }

    // ─────────────────────────────── Cancel ───────────────────────────────

    [Fact]
    public async Task Cancel_inside_the_window_cancels_keeps_the_uuid_and_does_not_unpost()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-INV-1001", status: "POSTED");
        await host.UpdateInvoiceAsync(EInvoiceTestHost.InvNo, x => x.IrbmValidOn = DateTime.UtcNow.AddHours(-1));
        var service = host.CreateService();

        var result = await service.CancelAsync(EInvoiceTestHost.InvoiceKey(), "Wrong buyer details.");

        Assert.True(result.Succeeded);
        Assert.Equal(EInvoiceStatuses.Cancelled, result.Status);

        var saved = await host.GetInvoiceAsync();
        Assert.Equal(EInvoiceStatuses.Cancelled, saved!.IrbmStatus);
        Assert.Equal("UUID-INV-1001", saved.IrbmUuid);
        Assert.NotNull(saved.IrnmCancelOn);
        // The financial document is untouched.
        Assert.Equal("POSTED", saved.Status);
        Assert.True(saved.TotAmnt > 0m);
    }

    [Fact]
    public async Task Cancel_outside_the_window_is_refused_without_calling_myinvois()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-INV-1001");
        await host.UpdateInvoiceAsync(EInvoiceTestHost.InvNo, x => x.IrbmValidOn = DateTime.UtcNow.AddHours(-73));
        var service = host.CreateService();

        var result = await service.CancelAsync(EInvoiceTestHost.InvoiceKey(), "Too late.");

        Assert.False(result.Succeeded);
        Assert.Empty(host.Helper.Calls);

        var saved = await host.GetInvoiceAsync();
        Assert.Equal(EInvoiceStatuses.Valid, saved!.IrbmStatus);
        Assert.Null(saved.IrnmCancelOn);
    }

    [Fact]
    public async Task Cancel_requires_a_reason()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-INV-1001");
        var service = host.CreateService();

        var result = await service.CancelAsync(EInvoiceTestHost.InvoiceKey(), "   ");

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.Validation, result.ErrorKind);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task A_failed_cancellation_keeps_the_previous_status_and_records_the_full_error()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-INV-1001");
        host.Helper.CancelHandler = _ => FakeSubmitDocumentHelper.Failure<CancelRespone>("Cancellation is not permitted.");
        var service = host.CreateService();

        var result = await service.CancelAsync(EInvoiceTestHost.InvoiceKey(), "Try anyway.");

        Assert.False(result.Succeeded);
        var saved = await host.GetInvoiceAsync();
        Assert.Equal(EInvoiceStatuses.Valid, saved!.IrbmStatus);
        Assert.Contains("not permitted", saved.IrbmError);

        var logs = await host.LogsAsync(EInvoiceTestHost.InvNo);
        Assert.Equal(EInvoiceActions.Cancel, logs[^1].Action);
        Assert.Contains("not permitted", logs[^1].ErrorMessage);
    }

    [Fact]
    public async Task A_cancellation_after_the_window_explains_the_credit_note_alternative()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        // Past the verified 72-hour window measured from validation.
        await host.SeedInvoiceAsync(
            irbmStatus: EInvoiceStatuses.Valid,
            irbmUuid: "UUID-INV-1001",
            irbmValidOn: DateTime.UtcNow.AddHours(-80));
        var service = host.CreateService();

        var result = await service.CancelAsync(EInvoiceTestHost.InvoiceKey(), "Too late.");

        Assert.False(result.Succeeded);
        // The window is enforced locally, so MyInvois is never called.
        Assert.Equal(SaEInvoiceErrorKind.StateRule, result.ErrorKind);
        Assert.Contains("cancellation window has passed", result.ErrorMessage);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task MyInvois_OperationPeriodOver_is_explained_not_echoed_verbatim()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(
            irbmStatus: EInvoiceStatuses.Valid,
            irbmUuid: "UUID-INV-1001",
            irbmValidOn: DateTime.UtcNow.AddHours(-1));
        // The portal can still refuse (for example when the document-type limit is shorter than the default).
        host.Helper.CancelHandler = _ =>
            FakeSubmitDocumentHelper.Failure<CancelRespone>("You cannot cancel this document.", "OperationPeriodOver");
        var service = host.CreateService();

        var result = await service.CancelAsync(EInvoiceTestHost.InvoiceKey(), "Try anyway.");

        Assert.False(result.Succeeded);
        Assert.Equal("OperationPeriodOver", result.ErrorCode);
        Assert.Contains("credit/debit note", result.ErrorMessage);

        // The short field is friendly; the audit row keeps the raw API code and message.
        var saved = await host.GetInvoiceAsync();
        Assert.Equal(EInvoiceStatuses.Valid, saved!.IrbmStatus);
        var logs = await host.LogsAsync(EInvoiceTestHost.InvNo);
        Assert.Equal("OperationPeriodOver", logs[^1].ErrorCode);
        Assert.Contains("cannot cancel this document", logs[^1].ErrorMessage);
    }

    [Fact]
    public async Task A_timeout_then_a_recovery_reconciles_the_submission_without_resubmitting()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = _ =>
            FakeSubmitDocumentHelper.Failure<ErpWeb.EInvoiceLib.Model.Document.SuccessSubmit>("The operation was canceled.");
        var service = host.CreateService();

        var submit = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());
        Assert.Equal(EInvoiceOutcomes.Unknown, submit.Outcome);

        // The submission id was never returned on the timeout path, so recovery has to search by number.
        host.Helper.SearchHandler = _ =>
            FakeSubmitDocumentHelper.SearchFound(EInvoiceTestHost.InvNo, "Valid", "UUID-INV-1001");

        var recovered = await service.RecoverAsync(EInvoiceTestHost.InvoiceKey(), operatorInitiated: true);

        Assert.True(recovered.Succeeded);
        Assert.Equal(EInvoiceStatuses.Valid, recovered.Status);
        Assert.Equal(1, host.Helper.SubmitCallCount);

        // The audit trail reads as one story: Submit attempt 1 (timed out), then Recover attempt 2.
        var logs = await host.LogsAsync(EInvoiceTestHost.InvNo);
        Assert.Equal(2, logs.Count);
        Assert.Equal(EInvoiceActions.Submit, logs[0].Action);
        Assert.Equal(EInvoiceStatuses.Failed, logs[0].Status);
        Assert.Equal(1, logs[0].AttemptNo);
        Assert.NotNull(logs[0].RequestTime);
        Assert.NotNull(logs[0].ResponseTime);
        Assert.NotNull(logs[0].DurationMs);
        Assert.Equal(EInvoiceActions.Recover, logs[1].Action);
        Assert.Equal(EInvoiceStatuses.Valid, logs[1].Status);
        Assert.Equal(2, logs[1].AttemptNo);
        Assert.NotEqual(Guid.Empty, logs[0].CorrelationId);
        Assert.NotEqual(Guid.Empty, logs[1].CorrelationId);
    }

    [Fact]
    public async Task A_stuck_submitting_row_is_recoverable_without_an_operator_once_the_timeout_passed()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(
            irbmStatus: EInvoiceStatuses.Submitting,
            irbmSubmitId: "SUB-1",
            modifiedDate: DateTime.UtcNow.AddMinutes(-20));
        host.Helper.SubmissionHandler = _ =>
            FakeSubmitDocumentHelper.SubmissionWith(EInvoiceTestHost.InvNo, "Valid", "UUID-INV-1001");
        var service = host.CreateService();

        // No operator involvement: the row is past the stuck timeout, so a sweep may reconcile it.
        var result = await service.RecoverAsync(EInvoiceTestHost.InvoiceKey(), operatorInitiated: false);

        Assert.True(result.Succeeded);
        Assert.Equal(EInvoiceStatuses.Valid, result.Status);
        Assert.Equal(0, host.Helper.SubmitCallCount);
    }

    [Fact]
    public async Task An_amount_mismatch_is_an_erp_validation_error_not_a_myinvois_call()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        // The invoice total no longer equals its lines plus tax.
        await host.SeedInvoiceAsync(totAmnt: 140m);
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.Validation, result.ErrorKind);
        Assert.Contains("Amounts.IncTax", result.ValidationErrors.Keys);
        Assert.Empty(host.Helper.Calls);
    }

    // ─────────────────────────────── CN / DN origin rules ───────────────────────────────

    [Fact]
    public async Task Credit_note_is_submitted_through_the_note_generator_with_the_origin_uuid()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedCreditNoteAsync();
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.CdnKey());

        Assert.True(result.Succeeded);
        Assert.Equal(EInvoiceStatuses.Submitted, result.Status);
        // 02 is generated by GenerateCreditNote, not by the frozen invoice path.
        Assert.Equal([nameof(FakeSubmitDocumentHelper.SubmitCreditDebitNotes)], host.Helper.Calls);
        Assert.Equal("UUID-INV-1001", host.Helper.Submitted[0].OriginInvoiceUUID);
        var saved = await host.GetCdnAsync();
        Assert.Equal(EInvoiceStatuses.Submitted, saved!.IrbmStatus);
        Assert.Equal("UUID-INV-1001", saved.IrbmOriUuid);
        Assert.Equal("UUID-INV-1001", host.Helper.Submitted[0].OriginInvoiceUUID);
    }

    [Fact]
    public async Task Credit_note_without_a_valid_origin_invoice_is_refused()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedCreditNoteAsync(originIrbmStatus: EInvoiceStatuses.Submitted, originUuid: null);
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.CdnKey());

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.Validation, result.ErrorKind);
        Assert.Contains("OriginInvoice.Uuid", result.ValidationErrors.Keys);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task Credit_note_against_an_origin_that_is_not_valid_is_refused_even_with_a_uuid()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        // The origin has a UUID but is only SUBMITTED: MyInvois has not validated it yet.
        await host.SeedCreditNoteAsync(originIrbmStatus: EInvoiceStatuses.Submitted, originUuid: "UUID-INV-1001");
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.CdnKey());

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.Validation, result.ErrorKind);
        Assert.Contains("OriginInvoice.Uuid", result.ValidationErrors.Keys);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task Credit_note_uses_the_credit_note_permissions_and_menu()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedCreditNoteAsync();
        var service = host.CreateService();

        await service.SubmitAsync(EInvoiceTestHost.CdnKey());

        Assert.Contains(host.PermissionChecks, x =>
            x.Menu == MenuCodes.SalesCreditNote && x.Permission == PermissionCodes.Submit);
    }

    // ─────────────────────────────── Multi-company ───────────────────────────────

    [Fact]
    public async Task Two_companies_submit_independently_and_each_keeps_its_own_state()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync(EInvoiceTestHost.Company);
        await host.SeedInvoiceAsync(companyCode: EInvoiceTestHost.Company);

        host.SwitchCompany("OTHER");
        await host.SeedCompanyAsync("OTHER");
        await host.SeedInvoiceAsync(invNo: "INV-2001", companyCode: "OTHER");

        var service = host.CreateService();

        host.SwitchCompany(EInvoiceTestHost.Company);
        var first = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());
        Assert.True(first.Succeeded);

        host.SwitchCompany("OTHER");
        var second = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey("INV-2001"));
        Assert.True(second.Succeeded);

        // Each company's document carries only its own result.
        var demoSaved = await host.GetInvoiceAsync(EInvoiceTestHost.Company, EInvoiceTestHost.InvNo);
        var otherSaved = await host.GetInvoiceAsync("OTHER", "INV-2001");
        Assert.Equal("UUID-INV-1001", demoSaved!.IrbmUuid);
        Assert.Equal("UUID-INV-2001", otherSaved!.IrbmUuid);

        // And the audit trail is scoped too.
        Assert.Single(await host.LogsAsync(EInvoiceTestHost.InvNo, companyCode: EInvoiceTestHost.Company));
        Assert.Single(await host.LogsAsync("INV-2001", companyCode: "OTHER"));
    }

    [Fact]
    public async Task A_document_belonging_to_another_company_is_invisible()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync(EInvoiceTestHost.Company);
        await host.SeedInvoiceAsync(companyCode: EInvoiceTestHost.Company);
        var service = host.CreateService();

        host.SwitchCompany("OTHER");

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.NotFound, result.ErrorKind);
        Assert.Empty(host.Helper.Calls);
    }

    // ─────────────────────────────── Optimistic concurrency ───────────────────────────────

    [Fact]
    public async Task A_row_that_vanished_under_an_in_flight_submission_is_reported_and_not_resubmitted()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        var service = host.CreateService();

        // MyInvois accepts the document, but the row is gone before the outcome is written back. The
        // in-flight write must fail loudly instead of silently applying a state for a row that is no
        // longer there, and it must not trigger a second submission.
        host.Helper.OnSubmitCalled = () => host.DeleteInvoiceAsync().GetAwaiter().GetResult();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.NotFound, result.ErrorKind);
        Assert.Equal(1, host.Helper.SubmitCallCount);
        Assert.Null(await host.GetInvoiceAsync());

        var recovered = await service.RecoverAsync(EInvoiceTestHost.InvoiceKey(), operatorInitiated: true);
        Assert.False(recovered.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.NotFound, recovered.ErrorKind);
    }

    // ─────────────────────────────── Status view ───────────────────────────────

    [Fact]
    public async Task The_status_view_exposes_the_history_and_the_action_gates()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        var service = host.CreateService();

        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());
        host.Helper.DocumentDetailHandler = _ => FakeSubmitDocumentHelper.Success(new DocumentValidatation
        {
            uuid = "UUID-INV-1001",
            status = "Valid",
            dateTimeValidated = DateTime.UtcNow
        });
        await service.RefreshAsync(EInvoiceTestHost.InvoiceKey());

        var view = await service.GetStatusAsync(EInvoiceTestHost.InvoiceKey());

        Assert.NotNull(view);
        Assert.Equal(EInvoiceStatuses.Valid, view!.Status);
        Assert.True(view.IsLocked);
        Assert.False(view.CanSubmit);
        Assert.True(view.CanRefresh);
        Assert.True(view.CanCancel);
        Assert.Equal(2, view.History.Count);
        Assert.Equal(EInvoiceActions.Refresh, view.History[0].Action);
        Assert.Equal(EInvoiceActions.Submit, view.History[^1].Action);
        Assert.Equal(1, view.History[^1].AttemptNo);
    }
}
