using ErpWeb.Core.EInvoice;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Sales.Transaction;
/// <summary>
/// Buyer-identity policy for e-Invoice: the customer master is the live source of truth, and the
/// invoice's <c>BuyerTin</c> / <c>BuyerBrn</c> / <c>BuyerRegType</c> / <c>InvEmail</c> columns become
/// the frozen audit snapshot the moment a submission claims <c>SUBMITTING</c>.
///
/// <para>
/// These tests exist because the previous behaviour let a stale copy on the invoice block a
/// submission after the customer profile was corrected. Fixing a customer must be enough: no unpost,
/// no rollback, no "re-open the invoice" step.
/// </para>
/// </summary>
[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.EInvoice)]
public class SaEInvoiceBuyerIdentityTests
{
    private const string OriginalTin = "C9876543210";
    private const string CorrectedTin = "TIN-CORRECTED";

    // ─────────────────── live rebuild while the document is rebuildable ───────────────────

    [Theory]
    [InlineData(null, null)]                                   // NEW
    [InlineData(EInvoiceStatuses.Invalid, null)]
    [InlineData(EInvoiceStatuses.Rejected, null)]
    [InlineData(EInvoiceStatuses.Cancelled, null)]
    [InlineData(EInvoiceStatuses.Failed, EInvoiceOutcomes.ConfirmedFailure)]
    public async Task Resubmit_uses_the_corrected_customer_identity(string? irbmStatus, string? irbmOutcome)
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(irbmStatus: irbmStatus, irbmOutcome: irbmOutcome);
        var service = host.CreateService();

        // The profile is corrected after the invoice was raised.
        await SetCustomerIdentityAsync(host, CorrectedTin);

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(result.Succeeded, result.ErrorMessage);
        var submitted = Assert.Single(host.Helper.Submitted);
        Assert.Equal(CorrectedTin, submitted.Customer.TinNo);

        // The claim also froze exactly what was sent onto the document.
        var saved = await host.GetInvoiceAsync();
        Assert.Equal(CorrectedTin, saved!.BuyerTin);
        Assert.Equal(EInvoiceStatuses.Submitted, saved.IrbmStatus);
    }

    // ─────────────────── frozen once sent: audit + credit notes ───────────────────

    [Fact]
    public async Task Submitted_identity_is_frozen_through_valid_and_reused_by_a_credit_note()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        var service = host.CreateService();

        var submit = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());
        Assert.True(submit.Succeeded, submit.ErrorMessage);
        Assert.Equal(OriginalTin, Assert.Single(host.Helper.Submitted).Customer.TinNo);

        // The customer is corrected after the invoice was accepted: the sent identity must not move.
        await SetCustomerIdentityAsync(host, CorrectedTin);
        var saved = await host.GetInvoiceAsync();
        Assert.Equal(OriginalTin, saved!.BuyerTin);

        // Mark it VALID as MyInvois would, then raise a credit note against it.
        await host.UpdateInvoiceAsync(EInvoiceTestHost.InvNo, x => x.IrbmStatus = EInvoiceStatuses.Valid);
        await host.SeedCreditNoteAsync();
        host.Helper.Submitted.Clear();

        var cn = await service.SubmitAsync(EInvoiceTestHost.CdnKey());

        Assert.True(cn.Succeeded, cn.ErrorMessage);
        Assert.Equal(OriginalTin, Assert.Single(host.Helper.Submitted).Customer.TinNo);
    }

    // ─────────────────── in flight: recover, never rebuild ───────────────────

    [Fact]
    public async Task Submitting_recovery_keeps_the_frozen_identity_and_never_resubmits()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(
            irbmStatus: EInvoiceStatuses.Submitting,
            irbmSubmitId: "SUB-1",
            modifiedDate: DateTime.UtcNow.AddHours(-2));
        var service = host.CreateService();

        // Correcting the profile mid-flight must not change what may already be at MyInvois.
        await SetCustomerIdentityAsync(host, CorrectedTin);

        var submit = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());
        Assert.False(submit.Succeeded);
        Assert.True(submit.RecoveryRequired);

        host.Helper.SubmissionHandler = _ =>
            FakeSubmitDocumentHelper.SubmissionWith(EInvoiceTestHost.InvNo, "Valid", "UUID-INV-1001");
        var recovered = await service.RecoverAsync(EInvoiceTestHost.InvoiceKey(), operatorInitiated: true);

        Assert.True(recovered.Succeeded, recovered.ErrorMessage);
        Assert.Equal(EInvoiceStatuses.Valid, recovered.Status);
        // Recover reconciles status only: it must never send the document again.
        Assert.Equal(0, host.Helper.SubmitCallCount);

        var saved = await host.GetInvoiceAsync();
        Assert.Equal(OriginalTin, saved!.BuyerTin);
    }

    [Fact]
    public async Task Unknown_outcome_recovery_never_rebuilds_the_buyer_from_the_customer()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(
            irbmStatus: EInvoiceStatuses.Failed,
            irbmOutcome: EInvoiceOutcomes.Unknown,
            irbmSubmitId: "SUB-1");
        var service = host.CreateService();

        await SetCustomerIdentityAsync(host, CorrectedTin);

        // MyInvois confirms the document was never accepted.
        host.Helper.SubmissionHandler = _ => FakeSubmitDocumentHelper.SubmissionWithout(EInvoiceTestHost.InvNo);
        var recovered = await service.RecoverAsync(EInvoiceTestHost.InvoiceKey(), operatorInitiated: true);

        Assert.True(recovered.Succeeded, recovered.ErrorMessage);
        Assert.Equal(EInvoiceStatuses.Failed, recovered.Status);
        Assert.Equal(EInvoiceOutcomes.ConfirmedFailure, recovered.Outcome);

        // Recover read the claimed identity; the corrected customer is only picked up by the NEXT claim.
        var saved = await host.GetInvoiceAsync();
        Assert.Equal(OriginalTin, saved!.BuyerTin);

        // And that next claim is live: a retry now sends the corrected identity.
        var retry = await service.RetryAsync(EInvoiceTestHost.InvoiceKey());
        Assert.True(retry.Succeeded, retry.ErrorMessage);
        Assert.Equal(CorrectedTin, Assert.Single(host.Helper.Submitted).Customer.TinNo);
    }

    // ─────────────────── atomic claim: freeze + SUBMITTING before HTTP ───────────────────

    [Fact]
    public async Task Claim_commits_the_frozen_identity_before_myinvois_is_called()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        var service = host.CreateService();

        await SetCustomerIdentityAsync(host, CorrectedTin);

        string? statusDuringHttp = null;
        string? buyerDuringHttp = null;
        host.Helper.OnSubmitCalled = () =>
        {
            // Runs inside the HTTP phase: the claim transaction is already committed, so a reader sees
            // SUBMITTING together with the identity that is being signed.
            var inFlight = host.GetInvoiceAsync().GetAwaiter().GetResult();
            statusDuringHttp = inFlight!.IrbmStatus;
            buyerDuringHttp = inFlight.BuyerTin;
        };

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(EInvoiceStatuses.Submitting, statusDuringHttp);
        Assert.Equal(CorrectedTin, buyerDuringHttp);
    }

    [Fact]
    public async Task Refused_validation_leaves_the_document_rebuildable_and_unfrozen()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync(buyerTin: "STALE-A");
        var service = host.CreateService();

        // The live master loses the identity, so the readiness gate fails before the claim.
        await SetCustomerIdentityAsync(host, tin: null, brn: null, regType: "BRN");

        var result = await service.SubmitAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(result.Succeeded);
        Assert.Equal(SaEInvoiceErrorKind.Validation, result.ErrorKind);
        Assert.Empty(host.Helper.Calls);

        // No partial outcome: still rebuildable, and nothing was frozen over the existing snapshot.
        var saved = await host.GetInvoiceAsync();
        Assert.Null(saved!.IrbmStatus);
        Assert.Equal("STALE-A", saved.BuyerTin);
    }

    // ─────────────────── RegType + identity readiness ───────────────────

    [Theory]
    [InlineData(EInvoiceRegistrationTypes.Brn, "202201234567", true)]
    [InlineData(EInvoiceRegistrationTypes.Nric, "900101011234", true)]
    [InlineData(EInvoiceRegistrationTypes.Passport, "A12345678", true)]
    [InlineData(EInvoiceRegistrationTypes.Army, "ARMY-123", true)]
    [InlineData(EInvoiceRegistrationTypes.Brn, null, false)]
    [InlineData(EInvoiceRegistrationTypes.Nric, null, false)]
    [InlineData(EInvoiceRegistrationTypes.Passport, null, false)]
    [InlineData(EInvoiceRegistrationTypes.Army, null, false)]
    public async Task Registration_type_requires_its_identity_number(string regType, string? identityNo, bool expectedValid)
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        var service = host.CreateService();

        await SetCustomerIdentityAsync(host, OriginalTin, brn: identityNo, regType: regType);

        var result = await service.ValidateAsync(EInvoiceTestHost.InvoiceKey());

        Assert.Equal(expectedValid, result.Succeeded);
        if (!expectedValid)
        {
            Assert.Contains("Buyer.RegNo", result.ValidationErrors.Keys);
        }
    }

    [Theory]
    [InlineData("")]        // blank
    [InlineData("IC")]      // legacy alias, not a canonical value
    [InlineData("SSM")]
    [InlineData("MYKAD")]
    [InlineData("ABC")]
    public async Task Non_canonical_registration_type_is_refused_with_master_data_guidance(string regType)
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        await host.SeedInvoiceAsync();
        var service = host.CreateService();

        await SetCustomerIdentityAsync(host, OriginalTin, regType: regType);

        var result = await service.ValidateAsync(EInvoiceTestHost.InvoiceKey());

        Assert.False(result.Succeeded);
        Assert.Contains("Buyer.RegType", result.ValidationErrors.Keys);
        Assert.Contains("customer profile", result.ValidationErrors["Buyer.RegType"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Frozen_document_identity_is_used_even_when_the_customer_master_is_empty()
    {
        await using var host = EInvoiceTestHost.Create();
        await host.SeedCompanyAsync();
        // An already-accepted invoice (frozen identity) whose customer master has since been cleared:
        // a credit note must still describe the identity the invoice was accepted with.
        await host.SeedInvoiceAsync(irbmStatus: EInvoiceStatuses.Valid, irbmUuid: "UUID-INV-1001");
        await host.SeedCreditNoteAsync(originUuid: "UUID-INV-1001");
        var service = host.CreateService();

        await SetCustomerIdentityAsync(host, tin: null, brn: null, regType: "");

        var cn = await service.SubmitAsync(EInvoiceTestHost.CdnKey());

        Assert.True(cn.Succeeded, cn.ErrorMessage);
        Assert.Equal(OriginalTin, Assert.Single(host.Helper.Submitted).Customer.TinNo);
    }

    // ─────────────────── shared vocabulary + lifecycle table (pure units) ───────────────────

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("brn", EInvoiceRegistrationTypes.Brn)]
    [InlineData(" Brn ", EInvoiceRegistrationTypes.Brn)]
    [InlineData("nric", EInvoiceRegistrationTypes.Nric)]
    [InlineData("passport", EInvoiceRegistrationTypes.Passport)]
    [InlineData("army", EInvoiceRegistrationTypes.Army)]
    [InlineData("IC", null)]
    [InlineData("SSM", null)]
    [InlineData("ABC", null)]
    public void Registration_type_normalization_only_accepts_the_four_types(string? input, string? expected)
    {
        Assert.Equal(expected, EInvoiceRegistrationTypes.Normalize(input));
        Assert.Equal(expected is not null, EInvoiceRegistrationTypes.IsValid(input));
    }

    [Fact]
    public void Registration_type_vocabulary_is_centralised()
    {
        string[] expected =
        [
            EInvoiceRegistrationTypes.Brn,
            EInvoiceRegistrationTypes.Nric,
            EInvoiceRegistrationTypes.Passport,
            EInvoiceRegistrationTypes.Army
        ];

        Assert.Equal(expected, EInvoiceRegistrationTypes.All);
        Assert.Equal(expected, EInvoiceRegistrationTypes.Options.Select(x => x.Value));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(EInvoiceStatuses.New, null, false)]
    [InlineData(EInvoiceStatuses.Invalid, null, false)]
    [InlineData(EInvoiceStatuses.Rejected, null, false)]
    [InlineData(EInvoiceStatuses.Cancelled, null, false)]
    [InlineData(EInvoiceStatuses.Failed, EInvoiceOutcomes.ConfirmedFailure, false)]
    [InlineData(EInvoiceStatuses.Failed, EInvoiceOutcomes.Unknown, true)]
    [InlineData(EInvoiceStatuses.Submitting, null, true)]
    [InlineData(EInvoiceStatuses.Submitted, null, true)]
    [InlineData(EInvoiceStatuses.Valid, null, true)]
    public void Buyer_identity_is_frozen_only_from_the_claim_onwards(string? status, string? outcome, bool expected)
    {
        Assert.Equal(expected, EInvoiceStatuses.IsBuyerIdentityFrozen(status, outcome));
    }

    private static async Task SetCustomerIdentityAsync(
        EInvoiceTestHost host,
        string? tin = OriginalTin,
        string? brn = "202201234567",
        string? regType = EInvoiceRegistrationTypes.Brn)
    {
        await using var db = await host.Factory.CreateDbContextAsync();
        var customer = await db.SaCusts.SingleAsync(
            x => x.CompanyCode == EInvoiceTestHost.Company && x.CustCode == "CUST01");
        customer.TinNo = tin;
        customer.CustBrn = brn;
        customer.RegType = regType;
        await db.SaveChangesAsync();
    }
}
