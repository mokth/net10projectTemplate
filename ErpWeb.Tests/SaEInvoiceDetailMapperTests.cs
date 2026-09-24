using ErpWeb.Core.EInvoice;
using ErpWeb.EInvoiceLib.Model;
using ErpWeb.EInvoiceLib.Model.Document;

namespace ErpWeb.Tests;

/// <summary>
/// The pure mapping from the MyInvois Get Document Details payload to <see cref="SaEInvoiceDetailView"/>.
///
/// <para>
/// No host, no database, no fake MyInvois: <c>SaEInvoiceDetailMapper</c> is a pure static function by
/// design, so these pin the fiddly validation-result transformation (nullability, the one-level
/// <c>innerError</c> limit, and the English/Malay message split) directly.
/// </para>
/// </summary>
[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.EInvoice)]
public class SaEInvoiceDetailMapperTests
{
    private const string EnglishMessage =
        "State Code 17 should be used for Consolidated e-Invoice and non-Malaysian address only - Buyer. "
        + "Please refer to https://sdk.myinvois.hasil.gov.my/documentvalidationrules/code-validator-error/#CV317";

    private const string MalayMessage =
        "Kod Negeri 17 hendaklah digunakan untuk e-Invois yang disatukan dan alamat selain Malaysia sahaja. "
        + "- Pembeli. Sila rujuk https://sdk.myinvois.hasil.gov.my/documentvalidationrules/code-validator-error/#CV317";

    [Fact]
    public void Map_rejects_a_null_payload()
    {
        Assert.Throws<ArgumentNullException>(() => SaEInvoiceDetailMapper.Map(null!));
    }

    [Fact]
    public void Mapper_handles_null_validation_results()
    {
        var view = SaEInvoiceDetailMapper.Map(new DocumentValidatation
        {
            uuid = "UUID-1",
            status = "Valid",
            validationResults = null
        });

        Assert.Empty(view.Steps);
        Assert.Null(view.ValidationStatus);
        Assert.False(view.HasIssues);
        Assert.Equal(0, view.IssueCount);
    }

    [Fact]
    public void Mapper_handles_null_validation_steps()
    {
        var view = SaEInvoiceDetailMapper.Map(new DocumentValidatation
        {
            uuid = "UUID-1",
            status = "Invalid",
            validationResults = new ValidationResults { status = "Invalid", validationSteps = null }
        });

        Assert.Empty(view.Steps);
        Assert.Equal("Invalid", view.ValidationStatus);
        Assert.False(view.HasIssues);
    }

    [Fact]
    public void Mapper_maps_a_step_with_no_error()
    {
        // A step can be Valid with no error object at all. It must still be reported: which
        // validations ran is part of the diagnosis.
        var view = SaEInvoiceDetailMapper.Map(new DocumentValidatation
        {
            uuid = "UUID-1",
            status = "Valid",
            validationResults = new ValidationResults
            {
                status = "Valid",
                validationSteps =
                [
                    new ValidationStep { name = "Step01-Schema Validator", status = "Valid", error = null }
                ]
            }
        });

        var step = Assert.Single(view.Steps);
        Assert.Equal("Step01-Schema Validator", step.Name);
        Assert.Equal("Valid", step.Status);
        Assert.Empty(step.Issues);
        Assert.Equal(0, step.IssueCount);
        Assert.False(view.HasIssues);
    }

    [Fact]
    public void Mapper_preserves_step_order_and_maps_every_step()
    {
        // Guards the accidental validationSteps.First(), which would silently drop the step that
        // actually failed.
        var view = SaEInvoiceDetailMapper.Map(new DocumentValidatation
        {
            uuid = "UUID-1",
            status = "Invalid",
            validationResults = new ValidationResults
            {
                status = "Invalid",
                validationSteps =
                [
                    new ValidationStep { name = "Step01-Schema Validator", status = "Valid" },
                    new ValidationStep
                    {
                        name = "Step04-Code Field Validator",
                        status = "Invalid",
                        error = new DocErrorDetail { ErrorCode = "Error04", Error = "Step04-Invalid Code Field Validator" }
                    },
                    new ValidationStep { name = "Step05-Tax Validator", status = "Invalid", error = null }
                ]
            }
        });

        Assert.Equal(3, view.Steps.Count);
        Assert.Equal("Step01-Schema Validator", view.Steps[0].Name);
        Assert.Equal("Step04-Code Field Validator", view.Steps[1].Name);
        Assert.Equal("Step05-Tax Validator", view.Steps[2].Name);

        // The step that failed with no error object is reported with no issues, not dropped and not
        // reported as a failure the operator can act on.
        Assert.Empty(view.Steps[2].Issues);
        Assert.True(view.Steps[2].Status == "Invalid");
    }

    [Fact]
    public void Mapper_maps_every_issue_field()
    {
        var view = SaEInvoiceDetailMapper.Map(new DocumentValidatation
        {
            uuid = "UUID-1",
            status = "Invalid",
            validationResults = new ValidationResults
            {
                status = "Invalid",
                validationSteps =
                [
                    new ValidationStep
                    {
                        name = "Step04-Code Field Validator",
                        status = "Invalid",
                        error = new DocErrorDetail
                        {
                            PropertyName = "State",
                            PropertyPath = "/ubl:Invoice/cac:AccountingCustomerParty/cac:Party/cac:PostalAddress/cbc:CountrySubentityCode",
                            ErrorCode = "CV317",
                            Error = EnglishMessage,
                            ErrorMs = MalayMessage,
                            MetaData = "meta-1"
                        }
                    }
                ]
            }
        });

        var issue = Assert.Single(Assert.Single(view.Steps).Issues);
        Assert.Equal("State", issue.PropertyName);
        Assert.Equal(
            "/ubl:Invoice/cac:AccountingCustomerParty/cac:Party/cac:PostalAddress/cbc:CountrySubentityCode",
            issue.PropertyPath);
        Assert.Equal("CV317", issue.ErrorCode);

        // ErrorMs is Bahasa Melayu, never milliseconds: the two messages must stay distinct and intact.
        Assert.Equal(EnglishMessage, issue.Error);
        Assert.Equal(MalayMessage, issue.ErrorMs);
        Assert.NotEqual(issue.Error, issue.ErrorMs);
        Assert.Contains("#CV317", issue.Error);

        Assert.Equal("meta-1", issue.MetaData);
    }

    [Fact]
    public void Mapper_keeps_a_step_summary_and_its_nested_field_error_together()
    {
        // This is LHDN's own documented shape: the step-level error is only a summary (Error04) while
        // the actionable per-field cause (CV317) is nested. Rendering only the summary reproduces the
        // "I can't tell what's wrong" problem this screen exists to fix.
        var view = SaEInvoiceDetailMapper.Map(new DocumentValidatation
        {
            uuid = "UUID-1",
            status = "Invalid",
            validationResults = new ValidationResults
            {
                status = "Invalid",
                validationSteps =
                [
                    new ValidationStep
                    {
                        name = "Step04-Code Field Validator",
                        status = "Invalid",
                        error = new DocErrorDetail
                        {
                            ErrorCode = "Error04",
                            Error = "Step04-Invalid Code Field Validator",
                            ErrorMs = "Step04-Pengesah Medan Kod Tidak Sah",
                            InnerError =
                            [
                                new InnerErrorMsg
                                {
                                    PropertyName = "State",
                                    PropertyPath = "/ubl:Invoice/cac:AccountingCustomerParty/cac:Party/cac:PostalAddress/cbc:CountrySubentityCode",
                                    ErrorCode = "CV317",
                                    Error = EnglishMessage,
                                    ErrorMs = MalayMessage,
                                    MetaData = null,
                                    InnerError = null
                                }
                            ]
                        }
                    }
                ]
            }
        });

        var issue = Assert.Single(Assert.Single(view.Steps).Issues);
        Assert.Equal("Error04", issue.ErrorCode);

        var inner = Assert.Single(issue.InnerErrors);
        Assert.Equal("CV317", inner.ErrorCode);
        Assert.Equal("State", inner.PropertyName);
        Assert.Equal(EnglishMessage, inner.Error);
        Assert.Equal(MalayMessage, inner.ErrorMs);

        // The count includes the nested issue: the header KPI would otherwise understate the work.
        Assert.Equal(2, Assert.Single(view.Steps).IssueCount);
        Assert.Equal(2, view.IssueCount);
    }

    [Fact]
    public void Mapper_expands_only_one_level_of_inner_error()
    {
        // MyInvois documents innerError as recursive, but the repository model cannot bind past the
        // first level: DocErrorDetail.InnerError is List<InnerErrorMsg> and InnerErrorMsg.InnerError is
        // object. Pin that so nobody "fixes" it into a recursive walk that would silently yield nothing.
        var view = SaEInvoiceDetailMapper.Map(new DocumentValidatation
        {
            uuid = "UUID-1",
            status = "Invalid",
            validationResults = new ValidationResults
            {
                status = "Invalid",
                validationSteps =
                [
                    new ValidationStep
                    {
                        name = "Step04",
                        status = "Invalid",
                        error = new DocErrorDetail
                        {
                            ErrorCode = "Error04",
                            InnerError =
                            [
                                new InnerErrorMsg
                                {
                                    ErrorCode = "CV317",
                                    Error = "level 1",
                                    // Would-be level 2: typed object, so it cannot be mapped.
                                    InnerError = new { ErrorCode = "CV999", Error = "level 2" }
                                }
                            ]
                        }
                    }
                ]
            }
        });

        var issue = Assert.Single(Assert.Single(view.Steps).Issues);
        var inner = Assert.Single(issue.InnerErrors);
        Assert.Equal("CV317", inner.ErrorCode);
        Assert.Empty(inner.InnerErrors);
    }

    [Fact]
    public void Mapper_skips_a_blank_inner_error_entry()
    {
        var view = SaEInvoiceDetailMapper.Map(new DocumentValidatation
        {
            uuid = "UUID-1",
            status = "Invalid",
            validationResults = new ValidationResults
            {
                status = "Invalid",
                validationSteps =
                [
                    new ValidationStep
                    {
                        name = "Step04",
                        status = "Invalid",
                        error = new DocErrorDetail
                        {
                            ErrorCode = "Error04",
                            InnerError =
                            [
                                new InnerErrorMsg(),
                                new InnerErrorMsg { PropertyName = "  ", Error = "" },
                                new InnerErrorMsg { ErrorCode = "CV317", Error = "real" }
                            ]
                        }
                    }
                ]
            }
        });

        var issue = Assert.Single(Assert.Single(view.Steps).Issues);
        var inner = Assert.Single(issue.InnerErrors);
        Assert.Equal("CV317", inner.ErrorCode);
    }

    [Fact]
    public void Mapper_normalises_status_but_keeps_the_raw_myinvois_value()
    {
        var view = SaEInvoiceDetailMapper.Map(new DocumentValidatation
        {
            uuid = "UUID-1",
            status = "InPrOgReSs",
            validationResults = null
        });

        // The chip vocabulary is the ERP one, but an unrecognised MyInvois status must not be reported
        // to the operator as "NEW": the raw value stays available.
        Assert.Equal(EInvoiceStatuses.New, view.Status);
        Assert.Equal("InPrOgReSs", view.MyInvoisStatus);
    }

    [Fact]
    public void Mapper_carries_the_document_fields_through()
    {
        var issued = new DateTime(2024, 5, 12, 9, 30, 0, DateTimeKind.Utc);

        var view = SaEInvoiceDetailMapper.Map(new DocumentValidatation
        {
            uuid = "UUID-1",
            submissionUid = "SUB-1",
            longId = "LONG-1",
            internalId = "INV-1001",
            typeName = "invoice",
            typeVersionName = "Version 1",
            issuerTin = "C1234567890",
            issuerName = "Demo Sdn Bhd",
            receiverId = "201901234567",
            receiverName = "Buyer Sdn Bhd",
            dateTimeIssued = issued,
            totalExcludingTax = 10.10m,
            totalDiscount = 50.00m,
            totalNetAmount = 100.70m,
            totalPayableAmount = 124.09m,
            status = "Invalid",
            documentStatusReason = "Wrong buyer details",
            createdByUserId = "admin",
            validationResults = null
        });

        Assert.Equal("UUID-1", view.Uuid);
        Assert.Equal("SUB-1", view.SubmissionUid);
        Assert.Equal("LONG-1", view.LongId);
        Assert.Equal("INV-1001", view.InternalId);
        Assert.Equal("invoice", view.TypeName);
        Assert.Equal("Version 1", view.TypeVersionName);
        Assert.Equal("C1234567890", view.IssuerTin);
        Assert.Equal("Demo Sdn Bhd", view.IssuerName);
        Assert.Equal("201901234567", view.ReceiverId);
        Assert.Equal("Buyer Sdn Bhd", view.ReceiverName);
        Assert.Equal(issued, view.DateTimeIssued);
        Assert.Equal(10.10m, view.TotalExcludingTax);
        Assert.Equal(50.00m, view.TotalDiscount);
        Assert.Equal(100.70m, view.TotalNetAmount);
        Assert.Equal(124.09m, view.TotalPayableAmount);
        Assert.Equal("INVALID", view.Status);
        Assert.Equal("Wrong buyer details", view.DocumentStatusReason);
        Assert.Equal("admin", view.CreatedByUserId);
    }

    [Fact]
    public void Mapper_treats_a_null_uuid_as_empty()
    {
        var view = SaEInvoiceDetailMapper.Map(new DocumentValidatation { uuid = null, status = "Submitted" });

        Assert.Equal(string.Empty, view.Uuid);
        Assert.Equal(EInvoiceStatuses.Submitted, view.Status);
    }
}
