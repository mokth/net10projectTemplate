using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using ErpWeb.EInvoiceLib.Model;
using ErpWeb.EInvoiceLib.Model.Document;

namespace ErpWeb.Tests.Sales.Transaction;
/// <summary>
/// <see cref="ISaEInvoiceService.GetDocumentDetailAsync"/> — the read-only MyInvois Get Document
/// Details read behind the LHDN detail screen.
///
/// <para>
/// The contracts pinned here are (a) the validation results survive the mapping, (b) the call is
/// genuinely read-only — no audit row, no status change — and (c) exactly one MyInvois request is made,
/// because LHDN throttles repeat requests for the same document.
/// </para>
/// </summary>
[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.EInvoice)]
public class SaEInvoiceDetailTests
{
    private const string Uuid = "UUID-INV-1001";

    private const string EnglishMessage =
        "State Code 17 should be used for Consolidated e-Invoice and non-Malaysian address only - Buyer.";

    private const string MalayMessage =
        "Kod Negeri 17 hendaklah digunakan untuk e-Invois yang disatukan dan alamat selain Malaysia sahaja.";

    private static int DetailCalls(EInvoiceTestHost host) =>
        host.Helper.Calls.Count(x => x == nameof(FakeSubmitDocumentHelper.GetDocumentDetail));

    /// <summary>An invalid document whose failing step carries a summary plus the nested field cause.</summary>
    private static void RespondWithInvalidDocument(EInvoiceTestHost host) =>
        host.Helper.DocumentDetailHandler = _ => FakeSubmitDocumentHelper.Success(new DocumentValidatation
        {
            uuid = Uuid,
            submissionUid = "SUB-1",
            internalId = "INV-1001",
            typeName = "invoice",
            typeVersionName = "Version 1",
            status = "Invalid",
            validationResults = new ValidationResults
            {
                status = "Invalid",
                validationSteps =
                [
                    new ValidationStep { name = "Step01-Schema Validator", status = "Valid", error = null },
                    new ValidationStep
                    {
                        name = "Step04-Code Field Validator",
                        status = "Invalid",
                        error = new DocErrorDetail
                        {
                            ErrorCode = "Error04",
                            Error = "Step04-Invalid Code Field Validator",
                            InnerError =
                            [
                                new InnerErrorMsg
                                {
                                    PropertyName = "State",
                                    PropertyPath = "/ubl:Invoice/cac:AccountingCustomerParty/cac:Party/cac:PostalAddress/cbc:CountrySubentityCode",
                                    ErrorCode = "CV317",
                                    Error = EnglishMessage,
                                    ErrorMs = MalayMessage
                                }
                            ]
                        }
                    }
                ]
            }
        });

    // ─────────────────────────────── Happy path ───────────────────────────────

    [Fact]
    public async Task Detail_maps_validation_steps_and_messages()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Invalid, irbmUuid: Uuid);
        RespondWithInvalidDocument(host);
        var service = host.CreateService();

        var result = await service.GetDocumentDetailAsync(Uuid);

        Assert.True(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.None, result.ErrorKind);

        var detail = result.Detail!;
        Assert.Equal(Uuid, detail.Uuid);
        Assert.Equal("Invalid", detail.MyInvoisStatus);
        Assert.Equal(EInvoiceStatuses.Invalid, detail.Status);
        Assert.Equal("Invalid", detail.ValidationStatus);
        Assert.True(detail.HasIssues);

        var failingStep = detail.Steps.Single(s => s.Name == "Step04-Code Field Validator");
        var issue = Assert.Single(failingStep.Issues);
        Assert.Equal("Error04", issue.ErrorCode);

        // The actionable cause is the nested field error, not the step summary.
        var inner = Assert.Single(issue.InnerErrors);
        Assert.Equal("CV317", inner.ErrorCode);
        Assert.Equal(
            "/ubl:Invoice/cac:AccountingCustomerParty/cac:Party/cac:PostalAddress/cbc:CountrySubentityCode",
            inner.PropertyPath);
        Assert.Equal(EnglishMessage, inner.Error);
        Assert.Equal(MalayMessage, inner.ErrorMs);
    }

    [Fact]
    public async Task Detail_maps_multiple_validation_steps()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Invalid, irbmUuid: Uuid);
        RespondWithInvalidDocument(host);
        var service = host.CreateService();

        var result = await service.GetDocumentDetailAsync(Uuid);

        // Both steps, in order - a validationSteps.First() would lose the one that failed.
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Detail!.Steps.Count);
        Assert.Equal("Step01-Schema Validator", result.Detail.Steps[0].Name);
        Assert.Equal("Step04-Code Field Validator", result.Detail.Steps[1].Name);
    }

    [Fact]
    public async Task Detail_handles_valid_document_without_validation_results()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: Uuid);
        host.Helper.DocumentDetailHandler = _ => FakeSubmitDocumentHelper.Success(new DocumentValidatation
        {
            uuid = Uuid,
            longId = "LONG-1",
            internalId = "INV-1001",
            status = "Valid",
            dateTimeValidated = new DateTime(2024, 5, 12, 14, 0, 0, DateTimeKind.Utc),
            totalPayableAmount = 124.09m,
            validationResults = null
        });
        var service = host.CreateService();

        var result = await service.GetDocumentDetailAsync(Uuid);

        // validationResults is null for SUBMITTED/VALID documents: this must succeed quietly, not
        // throw and not invent a failure the operator would chase.
        Assert.True(result.Succeeded);
        var detail = result.Detail!;
        Assert.Equal(EInvoiceStatuses.Valid, detail.Status);
        Assert.Null(detail.ValidationStatus);
        Assert.Empty(detail.Steps);
        Assert.False(detail.HasIssues);

        // The document information still renders.
        Assert.Equal("LONG-1", detail.LongId);
        Assert.Equal("INV-1001", detail.InternalId);
        Assert.Equal(124.09m, detail.TotalPayableAmount);
        Assert.NotNull(detail.DateTimeValidated);
    }

    // ─────────────────────────────── Read-only pin ───────────────────────────────

    [Fact]
    public async Task Detail_writes_no_audit_row_and_does_not_change_the_status()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Invalid, irbmUuid: Uuid);
        RespondWithInvalidDocument(host);
        var service = host.CreateService();

        var result = await service.GetDocumentDetailAsync(Uuid);

        Assert.True(result.Succeeded);

        // Looking at why a document failed must never change its e-Invoice state...
        var invoice = await host.GetInvoiceAsync();
        Assert.Equal(EInvoiceStatuses.Invalid, invoice!.IrbmStatus);
        Assert.Equal(Uuid, invoice.IrbmUuid);

        // ...and must not write an audit row, unlike every mutating operation.
        Assert.Empty(await host.LogsAsync(EInvoiceTestHost.InvNo));
    }

    [Fact]
    public async Task Detail_calls_GetDocumentDetail_exactly_once()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Invalid, irbmUuid: Uuid);
        RespondWithInvalidDocument(host);
        var service = host.CreateService();

        await service.GetDocumentDetailAsync(Uuid);

        // One operator action, one MyInvois request: LHDN throttles repeat requests for the same
        // document, so no retry may be bolted onto this path.
        Assert.Equal(1, DetailCalls(host));
    }

    // ─────────────────────────────── Refusals ───────────────────────────────

    [Fact]
    public async Task Detail_fails_without_the_tin_tools_access_right()
    {
        await using var host = EInvoiceTestHost.Create(authorized: false);
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Invalid, irbmUuid: Uuid);
        RespondWithInvalidDocument(host);
        var service = host.CreateService();

        var result = await service.GetDocumentDetailAsync(Uuid);

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.Authorization, result.ErrorKind);

        // Nothing was asked of MyInvois.
        Assert.DoesNotContain(nameof(FakeSubmitDocumentHelper.GetDocumentDetail), host.Helper.Calls);
    }

    [Fact]
    public async Task Detail_rejects_a_blank_uuid()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        var service = host.CreateService();

        var result = await service.GetDocumentDetailAsync("   ");

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.Validation, result.ErrorKind);
        Assert.Empty(host.Helper.Calls);
    }

    [Fact]
    public async Task Detail_reports_a_myinvois_failure()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Invalid, irbmUuid: Uuid);
        host.Helper.DocumentDetailHandler =
            _ => FakeSubmitDocumentHelper.Failure<DocumentValidatation>("MyInvois is unavailable.");
        var service = host.CreateService();

        var result = await service.GetDocumentDetailAsync(Uuid);

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.MyInvois, result.ErrorKind);
        Assert.Equal("MyInvois is unavailable.", result.ErrorMessage);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task Detail_reports_not_found_distinctly()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Invalid, irbmUuid: Uuid);

        // E_InvoiceRepository collapses the response to code + message and never sets errorCode, so
        // this is the shape a not-found really arrives in.
        host.Helper.DocumentDetailHandler =
            _ => FakeSubmitDocumentHelper.Failure<DocumentValidatation>("NotFound", errorCode: null);
        var service = host.CreateService();

        var result = await service.GetDocumentDetailAsync(Uuid);

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.NotFound, result.ErrorKind);
        Assert.Contains("no detail for this document UUID", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Detail_classifies_a_throttled_response()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Invalid, irbmUuid: Uuid);
        host.Helper.DocumentDetailHandler =
            _ => FakeSubmitDocumentHelper.Failure<DocumentValidatation>("TooManyRequests");
        var service = host.CreateService();

        var result = await service.GetDocumentDetailAsync(Uuid);

        Assert.False(result.Succeeded);
        Assert.Contains("throttling", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Detail_does_not_mistake_an_unrelated_failure_for_throttling()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Invalid, irbmUuid: Uuid);

        // A message that merely CONTAINS digits is not a throttle: the code check is an exact match.
        host.Helper.DocumentDetailHandler = _ => FakeSubmitDocumentHelper.Failure<DocumentValidatation>(
            "Document INV-4290 was rejected by the validator.");
        var service = host.CreateService();

        var result = await service.GetDocumentDetailAsync(Uuid);

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.MyInvois, result.ErrorKind);
        Assert.Equal("Document INV-4290 was rejected by the validator.", result.ErrorMessage);
    }

    // ─────────────────────────────── Authorization shape ───────────────────────────────

    [Fact]
    public async Task Detail_is_authorized_against_the_read_only_lhdn_tools_menu()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Invalid, irbmUuid: Uuid);
        RespondWithInvalidDocument(host);
        var service = host.CreateService();

        await service.GetDocumentDetailAsync(Uuid);

        Assert.Contains((MenuCodes.SalesEInvoiceTin, PermissionCodes.Access), host.PermissionChecks);
    }
}
