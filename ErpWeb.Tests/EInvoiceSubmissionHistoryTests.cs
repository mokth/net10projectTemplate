using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Services;
using ErpWeb.EInvoiceLib.Model.Document;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ErpWeb.Tests;

/// <summary>
/// <c>dbo.EInvDocSubmission</c> — the e-Invoice submission registry: one row per <b>accepted</b>
/// submitted document, written by <see cref="EInvoiceSubmissionWriter"/> from the four lifecycle hooks
/// in <c>SaEInvoiceService</c>.
///
/// <para>
/// These tests pin the decisions that are easy to get wrong later: row granularity (one row per
/// document, not per submission), Model A (rejected documents are never recorded), the four-part key
/// including the company, "a sparse MyInvois response never erases history", the value that the
/// registry writes instead of the legacy <c>"POS"</c>, and — most importantly — that a history failure
/// can never fail the e-Invoice action.
/// </para>
/// </summary>
public class EInvoiceSubmissionHistoryTests
{
    private const string SubmissionKeyIndex = "UX_EInvDocSubmission_Submission";

    private static async Task<List<EInvDocSubmission>> RowsAsync(
        EInvoiceTestHost host,
        string? companyCode = null)
    {
        await using var db = await host.Factory.CreateDbContextAsync();
        var query = db.EInvDocSubmissions.AsNoTracking().AsQueryable();
        if (companyCode is not null)
        {
            query = query.Where(x => x.CompanyId == companyCode);
        }

        return await query.OrderBy(x => x.Id).ToListAsync();
    }

    private static async Task<EInvDocSubmission?> RowAsync(EInvoiceTestHost host, string documentNo)
    {
        await using var db = await host.Factory.CreateDbContextAsync();
        return await db.EInvDocSubmissions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.DocumentNo == documentNo);
    }

    /// <summary>
    /// Removes the registry table so the next history write fails. Dropping the table (rather than
    /// mocking) exercises the real failure path: the caller's transaction has already committed, so the
    /// e-Invoice action must still succeed.
    /// </summary>
    private static async Task DropHistoryTableAsync(EInvoiceTestHost host)
    {
        await using var db = await host.Factory.CreateDbContextAsync();
        var names = new List<string>();
        await db.Database.OpenConnectionAsync();
        await using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE '%EInvDocSubmission%'";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }

        Assert.NotEmpty(names);

        foreach (var name in names)
        {
            await db.Database.ExecuteSqlRawAsync($"DROP TABLE \"{name}\"");
        }
    }

    /// <summary>True when the registry table is still present (used to prove a write had nowhere to go).</summary>
    private static async Task<bool> HistoryTableExistsAsync(EInvoiceTestHost host)
    {
        await using var db = await host.Factory.CreateDbContextAsync();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name LIKE '%EInvDocSubmission%'";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    // ────────────────────────────────── CREATE (Submit) ──────────────────────────────────

    [Fact]
    public async Task Submit_records_one_row_per_accepted_document_with_the_locked_values()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted("INV-1001", "UUID-INV-1001");
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(result.Succeeded);

        var row = Assert.Single(await RowsAsync(host));

        // Identity: the ERP family, NOT the legacy hard-coded "POS".
        Assert.Equal("INV", row.DocumentType);
        Assert.Equal("INV-1001", row.DocumentNo);
        Assert.Equal("DEMO", row.CompanyId);
        Assert.Equal("HQ", row.BranchCode);

        // MyInvois identity.
        Assert.Equal("SUB-1", row.SubmissionUuid);
        Assert.Equal("UUID-INV-1001", row.Uuid);
        Assert.Equal("INV-1001", row.InternalId);

        // Status vocabulary and the submission-level value.
        Assert.Equal(EInvoiceStatuses.Submitted, row.Status);
        Assert.Equal(EInvoiceStatuses.Submitted, row.OverallStatus);
        Assert.Equal(1, row.DocumentCount);

        // Timestamps: CreatedOn is ours, LastSyncedOn belongs to a sync and is not set by a submit.
        Assert.NotNull(row.CreatedOn);
        Assert.NotNull(row.DateTimeIssued);
        Assert.Null(row.LastSyncedOn);

        // Never written: the raw signed payload and the meaningless legacy document id.
        Assert.Null(row.Document);
        Assert.Null(row.DocumentId);
    }

    [Fact]
    public async Task Submit_of_a_rejected_document_records_no_row()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = _ => FakeSubmitDocumentHelper.Rejected("INV-1001");
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(result.Succeeded);
        Assert.Equal(EInvoiceStatuses.Rejected, result.Status);

        // Model A: rejected documents carry no uuid/internalId, and the rejection is already recorded by
        // SaEInvoiceLog plus the document's own IRBMStatus/IRBMOutcome.
        Assert.Empty(await RowsAsync(host));
        var log = Assert.Single(await host.LogsAsync("INV-1001"), x => x.Action == EInvoiceActions.Submit);
        Assert.Equal(EInvoiceStatuses.Rejected, log.Status);
    }

    [Fact]
    public async Task A_batch_records_one_row_per_document_all_sharing_the_submission_id()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync("INV-1001");
        await host.SeedInvoiceAsync("INV-2001");
        await host.SeedInvoiceAsync("INV-3001");
        host.Helper.SubmitHandler = infos =>
            FakeSubmitDocumentHelper.AcceptedMany(infos.Select(x => x.DocumentNo));
        var service = host.CreateService();

        var result = await service.SubmitManyAsync(
        [
            EInvoiceTestHost.InvoiceKey("INV-1001"),
            EInvoiceTestHost.InvoiceKey("INV-2001"),
            EInvoiceTestHost.InvoiceKey("INV-3001")
        ]);

        Assert.Equal(3, result.SucceededCount);

        // R1: one row per DOCUMENT, not per submission - all three share one submissionUUID.
        var rows = await RowsAsync(host);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, x => Assert.Equal("SUB-1", x.SubmissionUuid));
        Assert.All(rows, x => Assert.Equal(3, x.DocumentCount));
        Assert.All(rows, x => Assert.Equal(EInvoiceStatuses.Submitted, x.OverallStatus));
        Assert.Equal(
            ["INV-1001", "INV-2001", "INV-3001"],
            rows.Select(x => x.DocumentNo));
    }

    [Fact]
    public async Task A_mixed_batch_records_only_the_accepted_document_and_no_overall_status()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync("INV-1001");
        await host.SeedInvoiceAsync("INV-2001");
        host.Helper.SubmitHandler = _ => FakeSubmitDocumentHelper.Mixed(
            ["INV-1001"],
            [("INV-2001", "Classification code is not valid.")]);
        var service = host.CreateService();

        var result = await service.SubmitManyAsync(
        [
            EInvoiceTestHost.InvoiceKey("INV-1001"),
            EInvoiceTestHost.InvoiceKey("INV-2001")
        ]);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);

        var row = Assert.Single(await RowsAsync(host));
        Assert.Equal("INV-1001", row.DocumentNo);
        Assert.Equal(2, row.DocumentCount);

        // The submission carried a rejected member, so it has no single submission-level status: it is
        // left null for a later Refresh/Recover rather than guessed at.
        Assert.Null(row.OverallStatus);
    }

    // ────────────────────────────────── UPDATE (Refresh) ──────────────────────────────────

    [Fact]
    public async Task Refresh_updates_the_existing_row_in_place()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted("INV-1001", "UUID-INV-1001");
        var service = host.CreateService();
        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());
        var original = Assert.Single(await RowsAsync(host));

        var validatedOn = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        host.Helper.DocumentDetailHandler = _ => FakeSubmitDocumentHelper.Success(new DocumentValidatation
        {
            status = "Valid",
            uuid = "UUID-INV-1001",
            submissionUid = "SUB-1",
            longId = "LONG-1",
            internalId = "INV-1001",
            typeName = "invoice",
            typeVersionName = "1.0",
            issuerTin = "C1234567890",
            issuerName = "Demo Sdn Bhd",
            receiverId = "CUST-1",
            receiverName = "Customer One",
            dateTimeValidated = validatedOn,
            totalExcludingTax = 100m,
            totalDiscount = 5m,
            totalNetAmount = 95m,
            totalPayableAmount = 100.70m
        });

        var result = await service.RefreshAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(result.Succeeded);

        var row = Assert.Single(await RowsAsync(host));

        // Same row, not a second one.
        Assert.Equal(original.Id, row.Id);
        Assert.Equal(EInvoiceStatuses.Valid, row.Status);

        // Totals map from the API's names onto the legacy column names.
        Assert.Equal(100m, row.TotalSales);
        Assert.Equal(5m, row.TotalDiscount);
        Assert.Equal(95m, row.NetAmount);
        Assert.Equal(100.70m, row.Total);

        Assert.Equal(validatedOn, row.DateTimeValidated);
        Assert.Equal("LONG-1", row.LongId);
        Assert.Equal("Customer One", row.ReceiverName);
        Assert.NotNull(row.LastSyncedOn);

        // A submit does not set LastSyncedOn; the sync that followed does.
        Assert.True(row.LastSyncedOn >= row.CreatedOn);
    }

    [Fact]
    public async Task Refreshing_twice_leaves_exactly_one_row()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted("INV-1001", "UUID-INV-1001");
        var service = host.CreateService();
        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());
        var original = Assert.Single(await RowsAsync(host));

        await service.RefreshAsync(EInvoiceTestHost.InvoiceKey());
        await service.RefreshAsync(EInvoiceTestHost.InvoiceKey());

        var row = Assert.Single(await RowsAsync(host));
        Assert.Equal(original.Id, row.Id);
        Assert.Equal(EInvoiceStatuses.Valid, row.Status);
    }

    [Fact]
    public async Task A_sparse_refresh_response_never_erases_stored_history()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted("INV-1001", "UUID-INV-1001");
        var service = host.CreateService();
        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        // A rich response first.
        host.Helper.DocumentDetailHandler = _ => FakeSubmitDocumentHelper.Success(new DocumentValidatation
        {
            status = "Valid",
            longId = "LONG-1",
            internalId = "INV-1001",
            totalExcludingTax = 100m,
            totalPayableAmount = 100.70m,
            dateTimeValidated = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc)
        });
        await service.RefreshAsync(EInvoiceTestHost.InvoiceKey());

        // Then a sparse one that says nothing about totals, longId or the validation time.
        host.Helper.DocumentDetailHandler = _ => FakeSubmitDocumentHelper.Success(new DocumentValidatation
        {
            status = "Valid"
        });
        await service.RefreshAsync(EInvoiceTestHost.InvoiceKey());

        var row = Assert.Single(await RowsAsync(host));
        Assert.Equal(100m, row.TotalSales);
        Assert.Equal(100.70m, row.Total);
        Assert.Equal("LONG-1", row.LongId);
        Assert.NotNull(row.DateTimeValidated);
    }

    [Fact]
    public async Task An_over_long_api_value_is_truncated_instead_of_failing_the_write()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted("INV-1001", "UUID-INV-1001");
        var service = host.CreateService();
        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        // documentStatusReason is nvarchar(250) in the live table. A 400-character reason must not turn
        // the history write into a failure - a silently missing row is the bug this feature exists to fix.
        host.Helper.DocumentDetailHandler = _ => FakeSubmitDocumentHelper.Success(new DocumentValidatation
        {
            status = "Invalid",
            documentStatusReason = new string('x', 400)
        });
        await service.RefreshAsync(EInvoiceTestHost.InvoiceKey());

        var row = Assert.Single(await RowsAsync(host));
        Assert.Equal(250, row.DocumentStatusReason!.Length);
        Assert.Equal(EInvoiceStatuses.Invalid, row.Status);
    }

    // ────────────────────────────────── UPDATE (Recover) ──────────────────────────────────

    [Fact]
    public async Task Recover_creates_the_row_for_a_submission_that_never_recorded_one()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        // A claimed but unresolved submission: the transport failed, so no history row exists yet.
        await host.SeedInvoiceAsync(
            irbmStatus: EInvoiceStatuses.Failed,
            irbmOutcome: EInvoiceOutcomes.Unknown,
            irbmSubmitId: "SUB-1");
        host.Helper.SubmissionHandler = _ =>
            FakeSubmitDocumentHelper.SubmissionWith("INV-1001", "Valid", "UUID-INV-1001");
        var service = host.CreateService();

        var result = await service.RecoverAsync(EInvoiceTestHost.InvoiceKey(), operatorInitiated: true);

        Assert.True(result.Succeeded);
        Assert.Equal(EInvoiceStatuses.Valid, result.Status);

        // The upsert path: no row existed, so Recover establishes it rather than losing the submission.
        var row = Assert.Single(await RowsAsync(host));
        Assert.Equal("SUB-1", row.SubmissionUuid);
        Assert.Equal("UUID-INV-1001", row.Uuid);
        Assert.Equal(EInvoiceStatuses.Valid, row.Status);

        // The submission-level status came from the API's own vocabulary, stored verbatim.
        Assert.Equal("VALID", row.OverallStatus);
        Assert.NotNull(row.LastSyncedOn);
    }

    [Fact]
    public async Task Recover_updates_the_same_row_when_one_already_exists()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted("INV-1001", "UUID-INV-1001");
        var service = host.CreateService();
        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());
        var original = Assert.Single(await RowsAsync(host));

        // The submission did land and is now Valid.
        await host.UpdateInvoiceAsync("INV-1001", inv =>
        {
            inv.IrbmStatus = EInvoiceStatuses.Failed;
            inv.IrbmOutcome = EInvoiceOutcomes.Unknown;
        });
        host.Helper.SubmissionHandler = _ =>
            FakeSubmitDocumentHelper.SubmissionWith("INV-1001", "Valid", "UUID-INV-1001");

        var result = await service.RecoverAsync(EInvoiceTestHost.InvoiceKey(), operatorInitiated: true);

        Assert.True(result.Succeeded);

        // Exactly one row: the recover refreshed the row the submit created.
        var row = Assert.Single(await RowsAsync(host));
        Assert.Equal(original.Id, row.Id);
        Assert.Equal(EInvoiceStatuses.Valid, row.Status);
        Assert.Equal("VALID", row.OverallStatus);
    }

    // ────────────────────────────────── UPDATE (Cancel) ──────────────────────────────────

    [Fact]
    public async Task Cancel_marks_the_row_cancelled()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted("INV-1001", "UUID-INV-1001");
        var service = host.CreateService();
        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());
        var original = Assert.Single(await RowsAsync(host));
        Assert.Null(original.CancelDateTime);

        var result = await service.CancelAsync(EInvoiceTestHost.InvoiceKey(), "Duplicate submission");

        Assert.True(result.Succeeded);

        var row = Assert.Single(await RowsAsync(host));
        Assert.Equal(original.Id, row.Id);
        Assert.Equal(EInvoiceStatuses.Cancelled, row.Status);
        Assert.NotNull(row.CancelDateTime);

        // OverallStatus is SUBMISSION-level and holds the API's own vocabulary (see Recover, which stores
        // "VALID"). Cancelling ONE document therefore does not rewrite it: the writer only fills it when
        // nothing was known. A later Recover/Refresh replaces it with MyInvois' own "cancelled".
        Assert.Equal(EInvoiceStatuses.Submitted, row.OverallStatus);
    }

    [Fact]
    public async Task A_failed_cancel_does_not_touch_the_row()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted("INV-1001", "UUID-INV-1001");
        var service = host.CreateService();
        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());
        var original = Assert.Single(await RowsAsync(host));

        host.Helper.CancelHandler = _ =>
            FakeSubmitDocumentHelper.Failure<CancelRespone>("OperationPeriodOver", "400");
        var result = await service.CancelAsync(EInvoiceTestHost.InvoiceKey(), "Too late");

        Assert.False(result.Succeeded);

        // The financial document is unchanged, so history must not claim a cancellation either.
        var row = Assert.Single(await RowsAsync(host));
        Assert.Equal(original.Status, row.Status);
        Assert.Null(row.CancelDateTime);
    }

    // ────────────────────────────────── Tenant isolation ──────────────────────────────────

    [Fact]
    public async Task The_same_submission_and_document_number_in_another_company_is_a_separate_row()
    {
        await using var host = EInvoiceTestHost.Create();
        var service = host.CreateService();
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted("INV-1001", "UUID-INV-1001");

        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        // The same document number, the same submission id, a different tenant.
        host.SwitchCompany("OTHER");
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        var rows = await RowsAsync(host);

        // If companyID were not part of the key, the second submit would have UPDATED the DEMO row and
        // leaked one tenant's submission into another's registry.
        Assert.Equal(2, rows.Count);
        Assert.Equal(["DEMO", "OTHER"], rows.Select(x => x.CompanyId));
        Assert.All(rows, x => Assert.Equal("SUB-1", x.SubmissionUuid));
        Assert.All(rows, x => Assert.Equal("INV-1001", x.DocumentNo));
    }

    [Fact]
    public async Task Refresh_in_another_company_cannot_see_or_change_the_first_tenants_row()
    {
        await using var host = EInvoiceTestHost.Create();
        var service = host.CreateService();
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted("INV-1001", "UUID-INV-1001");

        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        host.SwitchCompany("OTHER");
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        host.Helper.DocumentDetailHandler = _ => FakeSubmitDocumentHelper.Success(new DocumentValidatation
        {
            status = "Valid",
            totalExcludingTax = 999m
        });
        await service.RefreshAsync(EInvoiceTestHost.InvoiceKey());

        var demo = await RowAsync(host, "INV-1001");
        var rows = await RowsAsync(host);
        var other = rows.Single(x => x.CompanyId == "OTHER");
        var first = rows.Single(x => x.CompanyId == "DEMO");

        Assert.Equal(999m, other.TotalSales);

        // The DEMO row was created by the submit and never refreshed, so it keeps no sync marker.
        Assert.Null(first.TotalSales);
        Assert.Null(first.LastSyncedOn);
        Assert.NotNull(demo);
    }

    // ────────────────────────────────── Failure isolation (plan R8 / D-8) ──────────────────────────────────

    [Fact]
    public async Task A_history_write_failure_does_not_fail_the_invoice_action()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted("INV-1001", "UUID-INV-1001");
        var service = host.CreateService();

        await DropHistoryTableAsync(host);

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        // THE contract of this feature: history must never be able to fail an e-Invoice action.
        Assert.True(result.Succeeded);
        Assert.Equal(EInvoiceStatuses.Submitted, result.Status);

        // The business state and the audit trail are intact...
        var invoice = await host.GetInvoiceAsync();
        Assert.Equal(EInvoiceStatuses.Submitted, invoice!.IrbmStatus);
        Assert.Equal("UUID-INV-1001", invoice.IrbmUuid);
        Assert.Single(await host.LogsAsync("INV-1001"), x => x.Action == EInvoiceActions.Submit);

        // ...and the registry genuinely had nowhere to write, so nothing was recorded elsewhere either.
        Assert.False(await HistoryTableExistsAsync(host));
    }

    // ────────────────────────────────── The unique index is the authority ──────────────────────────────────

    [Fact]
    public async Task The_unique_index_rejects_a_second_row_with_the_same_four_part_key()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted("INV-1001", "UUID-INV-1001");
        var service = host.CreateService();
        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        await using var db = await host.Factory.CreateDbContextAsync();
        db.EInvDocSubmissions.Add(new EInvDocSubmission
        {
            CompanyId = "DEMO",
            SubmissionUuid = "SUB-1",
            DocumentType = "INV",
            DocumentNo = "INV-1001",
            Status = EInvoiceStatuses.Submitted
        });

        // The database enforces the contract; the writer is not trusted to be the only guardian.
        // The message is provider-specific (SQLite names no index), so assert on the violation itself.
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var detail = ex.InnerException?.Message ?? ex.Message;
        Assert.True(
            detail.Contains(SubmissionKeyIndex, StringComparison.OrdinalIgnoreCase)
            || detail.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase),
            $"Expected a unique-index violation, got: {detail}");
    }

    [Fact]
    public async Task A_new_submission_id_for_the_same_document_is_an_additional_row()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = infos =>
            FakeSubmitDocumentHelper.Accepted("INV-1001", "UUID-INV-1001", "SUB-1");
        var service = host.CreateService();
        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        // Re-submitting the same ERP document (after a rejection or a cancellation) gets a NEW submission
        // id from MyInvois, so the registry keeps the earlier attempt as well. That is rule R5, and it is
        // why a reader must never assume one row per document number.
        var scope = new TenantScope { CompanyCode = "DEMO", BranchCode = "HQ", UserId = "tester" };
        var write = await EInvoiceSubmissionWriter.RecordSubmitAsync(
            host.Factory,
            scope,
            EInvoiceTestHost.InvoiceKey(),
            submissionId: "SUB-2",
            uuid: "UUID-RETRY",
            internalId: "INV-1001",
            documentCount: 1,
            overallStatus: EInvoiceStatuses.Submitted,
            submittedOnUtc: DateTime.UtcNow,
            logger: NullLogger.Instance);

        Assert.Equal(EInvoiceHistoryWrite.Inserted, write);

        var rows = await RowsAsync(host);
        Assert.Equal(2, rows.Count);
        Assert.Equal(["SUB-1", "SUB-2"], rows.Select(x => x.SubmissionUuid));
        Assert.All(rows, x => Assert.Equal("INV-1001", x.DocumentNo));

        // The "current" row for a document is the highest ID - which is exactly what
        // IX_EInvDocSubmission_Document exists to serve.
        Assert.Equal("SUB-2", rows[^1].SubmissionUuid);
    }

    [Fact]
    public async Task Recording_the_same_submission_twice_updates_rather_than_duplicating()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        host.Helper.SubmitHandler = infos =>
            FakeSubmitDocumentHelper.Accepted("INV-1001", "UUID-INV-1001", "SUB-1");
        var service = host.CreateService();
        await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());
        var original = Assert.Single(await RowsAsync(host));

        var scope = new TenantScope { CompanyCode = "DEMO", BranchCode = "HQ", UserId = "tester" };
        var write = await EInvoiceSubmissionWriter.RecordSubmitAsync(
            host.Factory,
            scope,
            EInvoiceTestHost.InvoiceKey(),
            submissionId: "SUB-1",
            uuid: "UUID-INV-1001",
            internalId: "INV-1001",
            documentCount: 1,
            overallStatus: EInvoiceStatuses.Submitted,
            submittedOnUtc: DateTime.UtcNow,
            logger: NullLogger.Instance);

        Assert.Equal(EInvoiceHistoryWrite.Updated, write);
        var row = Assert.Single(await RowsAsync(host));
        Assert.Equal(original.Id, row.Id);
    }

    [Fact]
    public async Task A_document_with_no_submission_id_records_nothing()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        // The request never left the process: no submission id is ever assigned.
        host.Helper.SubmitHandler = _ => FakeSubmitDocumentHelper.NeverSent();
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(result.Succeeded);
        Assert.Empty(await RowsAsync(host));
    }

    [Fact]
    public async Task A_credit_note_is_recorded_under_its_own_document_type()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedCreditNoteAsync();
        host.Helper.SubmitHandler = infos => FakeSubmitDocumentHelper.Accepted("CN-1001", "UUID-CN-1001");
        var service = host.CreateService();

        var result = await service.SubmitAsync(EInvoiceTestHost.CdnKey());

        Assert.True(result.Succeeded);
        var row = Assert.Single(await RowsAsync(host));
        Assert.Equal("CN", row.DocumentType);
        Assert.Equal("CN-1001", row.DocumentNo);
    }
}
