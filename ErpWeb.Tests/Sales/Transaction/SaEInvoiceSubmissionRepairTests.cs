using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using ErpWeb.EInvoiceLib.Model.Document;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests;

/// <summary>
/// <see cref="ISaEInvoiceService.RepairSubmissionAsync"/> — the UUID-addressed repair that runs behind
/// the E-UUID click on the sales invoice list.
///
/// <para>
/// The two failures this exists to fix: a registry row that was never written (most often because the
/// document carried no submission id at the time), and a registry row that has drifted from what
/// MyInvois actually holds. Both are repaired by the same path, so these tests pin the properties that
/// make that safe:
/// </para>
///
/// <list type="bullet">
/// <item>the missing row is <b>created</b>, and the submission id it needed is recovered from the API
/// and written back onto the ERP document;</item>
/// <item><b>at most one</b> MyInvois read per repair, and no duplicate row when it runs twice;</item>
/// <item>nothing is written when the UUID is not ours, when the caller lacks SUBMIT rights, or when the
/// row belongs to <b>another branch</b>;</item>
/// <item>a missing right is reported as "not repaired", never as an error — the caller's real job is
/// opening the LHDN portal.</item>
/// </list>
/// </summary>
[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.EInvoice)]
public class SaEInvoiceSubmissionRepairTests
{
    private const string Uuid = "UUID-INV-1001";
    private const string SubmissionId = "SUB-1";

    // ─────────────────────────────────── helpers ───────────────────────────────────

    private static async Task<List<EInvDocSubmission>> RowsAsync(
        EInvoiceTestHost host,
        string? documentNo = null)
    {
        await using var db = await host.Factory.CreateDbContextAsync();
        var query = db.EInvDocSubmissions.AsNoTracking().AsQueryable();
        if (documentNo is not null)
        {
            query = query.Where(x => x.DocumentNo == documentNo);
        }

        return await query.OrderBy(x => x.Id).ToListAsync();
    }

    /// <summary>
    /// Writes a registry row directly — the "the row exists but the API has newer data" starting point,
    /// and the cheapest way to control the document type / branch the resolver has to cope with.
    /// </summary>
    private static async Task<EInvDocSubmission> SeedHistoryRowAsync(
        EInvoiceTestHost host,
        string uuid,
        string submissionUuid,
        string documentNo = "INV-1001",
        string documentType = "INV",
        string? branchCode = "HQ",
        string status = EInvoiceStatuses.Submitted)
    {
        await using var db = await host.Factory.CreateDbContextAsync();
        var row = new EInvDocSubmission
        {
            CompanyId = EInvoiceTestHost.Company,
            BranchCode = branchCode,
            DocumentType = documentType,
            DocumentNo = documentNo,
            SubmissionUuid = submissionUuid,
            Uuid = uuid,
            Status = status,
            CreatedOn = DateTime.UtcNow
        };

        db.EInvDocSubmissions.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private static async Task SetCdnEInvoiceStateAsync(
        EInvoiceTestHost host,
        string docNo,
        string uuid,
        string? submitId,
        string status)
    {
        await using var db = await host.Factory.CreateDbContextAsync();
        var cdn = await db.SaCdns
            .FirstAsync(x => x.CompanyCode == EInvoiceTestHost.Company && x.DocNo == docNo);
        cdn.IrbmUuid = uuid;
        cdn.IrbmSubmitId = submitId;
        cdn.IrbmStatus = status;
        await db.SaveChangesAsync();
    }

    /// <summary>A detail response that carries the submission id — the shape that makes recovery possible.</summary>
    private static void RespondWithSubmissionId(
        EInvoiceTestHost host,
        string? submissionUid,
        string status = "Valid",
        decimal? totalPayable = null)
    {
        host.Helper.DocumentDetailHandler = _ => FakeSubmitDocumentHelper.Success(new DocumentValidatation
        {
            status = status,
            uuid = Uuid,
            submissionUid = submissionUid,
            longId = "LONG-1",
            internalId = "INV-1001",
            totalPayableAmount = totalPayable
        });
    }

    // ───────────────────────────── CREATE (the missing row) ─────────────────────────────

    [Fact]
    public async Task Repair_inserts_the_missing_row_from_the_api_submissionUid()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();

        // The failure this feature exists to fix: MyInvois accepted the document, the ERP document knows
        // the uuid, but IRBMSubmitID was never recorded and no registry row was written.
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: Uuid, irbmSubmitId: null);
        Assert.Empty(await RowsAsync(host));

        RespondWithSubmissionId(host, SubmissionId);
        var service = host.CreateService();

        var result = await service.RepairSubmissionAsync(Uuid);

        Assert.True(result.Attempted);
        Assert.True(result.SubmissionIdRecovered);
        Assert.Equal(EInvoiceHistoryWriteResult.Inserted, result.HistoryWrite);
        Assert.Equal("INV", result.DocumentType);
        Assert.Equal("INV-1001", result.DocumentNo);

        var row = Assert.Single(await RowsAsync(host));

        // Keyed on the id recovered from the API, because the ERP document had none.
        Assert.Equal(SubmissionId, row.SubmissionUuid);
        Assert.Equal(Uuid, row.Uuid);
        Assert.Equal("INV", row.DocumentType);
        Assert.Equal("INV-1001", row.DocumentNo);
        Assert.Equal(EInvoiceTestHost.Company, row.CompanyId);
        Assert.Equal(EInvoiceStatuses.Valid, row.Status);
        Assert.NotNull(row.LastSyncedOn);

        // ...and the recovered id is persisted on the ERP document, which is what makes the NEXT repair
        // (and the status Refresh button) work without relying on recovery again.
        var invoice = await host.GetInvoiceAsync();
        Assert.Equal(SubmissionId, invoice!.IrbmSubmitId);
    }

    [Fact]
    public async Task Refresh_adopts_the_submission_uid_and_creates_the_missing_row()
    {
        // The same recovery through the plain Refresh entry point: the status button gains the repair,
        // which is intended rather than incidental.
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: Uuid, irbmSubmitId: null);

        RespondWithSubmissionId(host, SubmissionId);
        var service = host.CreateService();

        var result = await service.RefreshAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(result.Succeeded);
        Assert.True(result.SubmissionIdRecovered);
        Assert.Equal(EInvoiceHistoryWriteResult.Inserted, result.HistoryWrite);
        Assert.Single(await RowsAsync(host));
    }

    // ───────────────────────────── UPDATE (the drifted row) ─────────────────────────────

    [Fact]
    public async Task Repair_updates_the_row_when_lhdn_changed_the_document()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(
            irbmStatus: EInvoiceStatuses.Submitted, irbmUuid: Uuid, irbmSubmitId: SubmissionId);

        // A row written at submission time, carrying the provisional values.
        var original = await SeedHistoryRowAsync(host, Uuid, SubmissionId);

        RespondWithSubmissionId(host, SubmissionId, status: "Valid", totalPayable: 100.70m);
        var service = host.CreateService();

        var result = await service.RepairSubmissionAsync(Uuid);

        Assert.True(result.Attempted);
        Assert.False(result.SubmissionIdRecovered);
        Assert.Equal(EInvoiceHistoryWriteResult.Updated, result.HistoryWrite);
        Assert.Equal(EInvoiceStatuses.Valid, result.Status);

        var row = Assert.Single(await RowsAsync(host));

        // Same row, refreshed in place — not a second one.
        Assert.Equal(original.Id, row.Id);
        Assert.Equal(EInvoiceStatuses.Valid, row.Status);
        Assert.Equal(100.70m, row.Total);
        Assert.Equal("LONG-1", row.LongId);
        Assert.NotNull(row.LastSyncedOn);
    }

    [Fact]
    public async Task Repair_does_not_erase_history_when_lhdn_returns_a_sparse_detail()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(
            irbmStatus: EInvoiceStatuses.Submitted, irbmUuid: Uuid, irbmSubmitId: SubmissionId);
        await SeedHistoryRowAsync(host, Uuid, SubmissionId);

        // A rich response first, then one that says nothing about longId or the totals.
        RespondWithSubmissionId(host, SubmissionId, totalPayable: 100.70m);
        var service = host.CreateService();
        await service.RepairSubmissionAsync(Uuid);

        host.Helper.DocumentDetailHandler = _ => FakeSubmitDocumentHelper.Success(new DocumentValidatation
        {
            status = "Valid",
            submissionUid = SubmissionId
        });
        await service.RepairSubmissionAsync(Uuid);

        var row = Assert.Single(await RowsAsync(host));
        Assert.Equal(100.70m, row.Total);
        Assert.Equal("LONG-1", row.LongId);
    }

    [Fact]
    public async Task Repair_does_not_create_duplicate_history_rows_when_called_twice()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: Uuid, irbmSubmitId: null);

        RespondWithSubmissionId(host, SubmissionId);
        var service = host.CreateService();

        await service.RepairSubmissionAsync(Uuid);
        var afterFirst = Assert.Single(await RowsAsync(host));

        await service.RepairSubmissionAsync(Uuid);

        // The whole design rests on the writer's find-or-insert; a second click must refresh that row.
        var afterSecond = Assert.Single(await RowsAsync(host));
        Assert.Equal(afterFirst.Id, afterSecond.Id);
    }

    // ───────────────────────────── refusals (nothing written) ─────────────────────────────

    [Fact]
    public async Task Repair_is_not_applicable_when_the_uuid_is_not_linked()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        var service = host.CreateService();

        var result = await service.RepairSubmissionAsync("UUID-NOT-OURS");

        // Not an error: the E-UUID click still has to open the portal link.
        Assert.False(result.Attempted);
        Assert.Equal(EInvoiceHistoryWriteResult.NotAttempted, result.HistoryWrite);
        Assert.Contains("not linked", result.Message, StringComparison.OrdinalIgnoreCase);

        // Nothing was asked of MyInvois, and nothing was written.
        Assert.DoesNotContain(nameof(FakeSubmitDocumentHelper.GetDocumentDetail), host.Helper.Calls);
        Assert.Empty(await RowsAsync(host));
    }

    [Fact]
    public async Task Repair_refuses_without_submit_permission()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: Uuid, irbmSubmitId: null);
        host.DeniedPermissions.Add(PermissionCodes.Submit);

        RespondWithSubmissionId(host, SubmissionId);
        var service = host.CreateService();

        var result = await service.RepairSubmissionAsync(Uuid);

        // Reported as "not repaired" rather than an error, so the click can still open the portal.
        Assert.False(result.Attempted);
        Assert.Equal(EInvoiceHistoryWriteResult.NotAttempted, result.HistoryWrite);
        Assert.Contains("SUBMIT", result.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(nameof(FakeSubmitDocumentHelper.GetDocumentDetail), host.Helper.Calls);
        Assert.Empty(await RowsAsync(host));
    }

    [Fact]
    public async Task Repair_creates_no_row_when_neither_source_has_a_submission_id()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: Uuid, irbmSubmitId: null);

        // The API answers, but does not tell us which submission the document belonged to: without a key
        // there is genuinely nothing to write, and inventing one would be worse than a missing row.
        RespondWithSubmissionId(host, submissionUid: null);
        var service = host.CreateService();

        var result = await service.RepairSubmissionAsync(Uuid);

        Assert.True(result.Attempted);
        Assert.Equal(EInvoiceHistoryWriteResult.Skipped, result.HistoryWrite);
        Assert.Contains("no submission id", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await RowsAsync(host));
    }

    [Fact]
    public async Task Repair_of_a_self_billed_uuid_with_no_erp_document_never_calls_myinvois()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();

        // The registry is the first thing the resolver reads, so a self-billed row is enough to resolve
        // the key. There is no matching ERP document here, so the refresh has no state to load — and
        // must not ask MyInvois for a document it cannot write the answer onto.
        await SeedHistoryRowAsync(
            host, "UUID-SBI-1", "SUB-SBI-1", documentNo: "SBI-1001", documentType: EInvoiceDocumentTypes.SelfBilledInvoice);

        var service = host.CreateService();
        var result = await service.RepairSubmissionAsync("UUID-SBI-1");

        Assert.True(result.Attempted);
        Assert.False(result.Succeeded);
        Assert.Equal(EInvoiceDocumentTypes.SelfBilledInvoice, result.DocumentType);
        Assert.Equal("SBI-1001", result.DocumentNo);
        Assert.DoesNotContain(nameof(FakeSubmitDocumentHelper.GetDocumentDetail), host.Helper.Calls);
    }

    // ─────────────────────────────────── branch scope ───────────────────────────────────

    [Fact]
    public async Task Repair_allows_a_legacy_row_with_no_branch_to_repair()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(
            irbmStatus: EInvoiceStatuses.Submitted, irbmUuid: Uuid, irbmSubmitId: SubmissionId);

        // Every row the legacy system wrote has a blank BranchCode. That carries no claim, so it must not
        // be treated as "some other branch".
        await SeedHistoryRowAsync(host, Uuid, SubmissionId, branchCode: null);

        RespondWithSubmissionId(host, SubmissionId);
        var service = host.CreateService();

        var result = await service.RepairSubmissionAsync(Uuid);

        Assert.True(result.Attempted);
        Assert.Equal(EInvoiceHistoryWriteResult.Updated, result.HistoryWrite);
    }

    [Fact]
    public async Task Repair_does_not_write_across_branches()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(
            irbmStatus: EInvoiceStatuses.Submitted, irbmUuid: Uuid, irbmSubmitId: SubmissionId);

        // A row naming a DIFFERENT branch. The registry is company-scoped but the ERP write-back resolves
        // the document by (CompanyCode, BranchCode, InvNo), so this must be refused rather than silently
        // written to another branch's data.
        var original = await SeedHistoryRowAsync(host, Uuid, SubmissionId, branchCode: "BR2");

        RespondWithSubmissionId(host, SubmissionId, status: "Valid");
        var service = host.CreateService();

        var result = await service.RepairSubmissionAsync(Uuid);

        Assert.False(result.Attempted);
        Assert.Contains("BR2", result.Message, StringComparison.Ordinal);

        // Nothing was read from MyInvois...
        Assert.DoesNotContain(nameof(FakeSubmitDocumentHelper.GetDocumentDetail), host.Helper.Calls);

        // ...the ERP document was not touched...
        var invoice = await host.GetInvoiceAsync();
        Assert.Equal(EInvoiceStatuses.Submitted, invoice!.IrbmStatus);

        // ...and the registry row is byte-identical.
        var row = Assert.Single(await RowsAsync(host));
        Assert.Equal(original.Id, row.Id);
        Assert.Equal(EInvoiceStatuses.Submitted, row.Status);
        Assert.Null(row.LastSyncedOn);
    }

    // ─────────────────────────── one repair, one MyInvois read ───────────────────────────

    [Fact]
    public async Task Repair_calls_GetDocumentDetail_exactly_once()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: Uuid, irbmSubmitId: null);

        RespondWithSubmissionId(host, SubmissionId);
        var service = host.CreateService();

        await service.RepairSubmissionAsync(Uuid);

        // The re-entry invariant: the click path may retry the portal LINK, never the repair. A second
        // MyInvois read here would mean someone turned that fallback into a retry loop.
        Assert.Equal(
            1,
            host.Helper.Calls.Count(x => x == nameof(FakeSubmitDocumentHelper.GetDocumentDetail)));
    }

    // ──────────────────────────────── credit / debit notes ────────────────────────────────

    [Fact]
    public async Task Repair_works_for_credit_notes()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedCreditNoteAsync();

        // The CN's own MyInvois identity (the seeded origin invoice keeps its own uuid, so the resolver
        // must not confuse the two).
        await SetCdnEInvoiceStateAsync(host, "CN-1001", "UUID-CN-1001", submitId: null, EInvoiceStatuses.Valid);

        host.Helper.DocumentDetailHandler = _ => FakeSubmitDocumentHelper.Success(new DocumentValidatation
        {
            status = "Valid",
            uuid = "UUID-CN-1001",
            submissionUid = "SUB-CN-1",
            internalId = "CN-1001"
        });

        var service = host.CreateService();
        var result = await service.RepairSubmissionAsync("UUID-CN-1001");

        Assert.True(result.Attempted);
        Assert.Equal(EInvoiceDocumentTypes.CreditNote, result.DocumentType);
        Assert.Equal("CN-1001", result.DocumentNo);
        Assert.Equal(EInvoiceHistoryWriteResult.Inserted, result.HistoryWrite);

        var row = Assert.Single(await RowsAsync(host, "CN-1001"));
        Assert.Equal("CN", row.DocumentType);
        Assert.Equal("SUB-CN-1", row.SubmissionUuid);

        var cdn = await host.GetCdnAsync();
        Assert.Equal("SUB-CN-1", cdn!.IrbmSubmitId);
    }
}
