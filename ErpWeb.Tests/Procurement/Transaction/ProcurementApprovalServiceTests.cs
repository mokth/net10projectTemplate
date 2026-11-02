using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ErpWeb.Tests.Procurement.Transaction;

[Trait(TestCategories.Name, TestCategories.Purchase)]
[Trait(TestCategories.Name, TestCategories.PurchasePr)]
public class ProcurementApprovalServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 12);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly string _attachRoot;

    public ProcurementApprovalServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _attachRoot = Path.Combine(Path.GetTempPath(), "po-approval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_attachRoot);
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.IvWarehouses.Add(new IvWarehouse { CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", IsActive = true });
        db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "MYR", CurrDesc = "Ringgit", IsActive = true });
        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = "DEMO",
            TaxGrCode = "SR",
            TaxGrDesc = "Standard",
            Percentage = 6m,
            TaxGlCode = "GLTAX"
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "A100",
            IDesc = "Stock A",
            IClassCode = "RAW",
            StdUom = "EA",
            PurUom = "EA",
            PurStdPackSize = 1m,
            PurchasePrice = 25m,
            PurchaseTaxGroup = "SR",
            IsActive = true,
            DefWarehouse = "MAIN",
            RowVersion = Guid.NewGuid().ToByteArray()
        });
        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            SuppCode = "SUP01",
            SuppName = "Alpha Supplier",
            Currency = "MYR",
            GlCode = "AP001",
            Country = "MY",
            IsActive = true,
            RowVersion = Guid.NewGuid().ToByteArray()
        });
        db.UserLogins.AddRange(
            User("user"),
            User("mgr"),
            User("mgr2"),
            User("admin"));
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        if (Directory.Exists(_attachRoot))
        {
            Directory.Delete(_attachRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Policy_off_allows_existing_PR_conversion()
    {
        var prs = CreatePr("user");
        var save = await prs.SaveNewAsync(PrRequest(PrLine("A100", 1m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var orders = CreatePo("user", CreateApproval("user"));
        var po = await orders.SaveNewAsync(PoRequest(PoLine("A100", 1m, prNo: save.PrNo, prLine: 1)));
        Assert.True(po.Succeeded, po.ErrorMessage);
    }

    [Fact]
    public async Task Missing_currency_band_fails_closed()
    {
        var admin = CreateApproval("admin");
        var savedPolicy = await admin.SavePolicyAsync(new ProcurementApprovalPolicyDto
        {
            DocType = ProcurementApprovalDocTypes.Pr,
            Enabled = true,
            Bands =
            [
                new ProcurementApprovalBandDto
                {
                    Currency = "EUR",
                    MinInclusive = 0m,
                    NotRequired = true
                }
            ]
        });
        Assert.True(savedPolicy.Succeeded, savedPolicy.ErrorMessage);

        var approval = CreateApproval("user");
        var prs = CreatePr("user", approval);
        var save = await prs.SaveNewAsync(PrRequest(PrLine("A100", 1m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var submit = await approval.SubmitAsync(new ProcurementApprovalSubmitRequest
        {
            DocType = ProcurementApprovalDocTypes.Pr,
            DocNo = save.PrNo!
        });
        Assert.False(submit.Succeeded);
        Assert.Contains("MYR", submit.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pending_PR_blocks_conversion_and_material_edit()
    {
        await EnablePolicyAsync(notRequired: false, maxExclusive: null);
        var userApproval = CreateApproval("user");
        var prs = CreatePr("user", userApproval);
        var save = await prs.SaveNewAsync(PrRequest(PrLine("A100", 50m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var submitted = await userApproval.SubmitAsync(new ProcurementApprovalSubmitRequest
        {
            DocType = ProcurementApprovalDocTypes.Pr,
            DocNo = save.PrNo!
        });
        Assert.True(submitted.Succeeded, submitted.ErrorMessage);
        Assert.Equal(ProcurementApprovalStatuses.Pending, submitted.Instance!.Status);

        var edit = await prs.UpdateAsync(
            save.PrNo!,
            PrRequest(PrLine("A100", 80m, line: 1), save.Document!.RowVersion));
        Assert.False(edit.Succeeded);
        Assert.Contains("approval", edit.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var orders = CreatePo("user", userApproval);
        var po = await orders.SaveNewAsync(PoRequest(PoLine("A100", 1m, prNo: save.PrNo, prLine: 1)));
        Assert.False(po.Succeeded);
        Assert.Contains("waiting for approval", po.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Approved_PR_can_convert_and_self_approval_is_blocked()
    {
        await EnablePolicyAsync(notRequired: false, maxExclusive: null);
        var userApproval = CreateApproval("user");
        var prs = CreatePr("user", userApproval);
        var save = await prs.SaveNewAsync(PrRequest(PrLine("A100", 50m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var submitted = await userApproval.SubmitAsync(new ProcurementApprovalSubmitRequest
        {
            DocType = ProcurementApprovalDocTypes.Pr,
            DocNo = save.PrNo!
        });
        Assert.True(submitted.Succeeded, submitted.ErrorMessage);

        var self = await userApproval.DecideAsync(new ProcurementApprovalDecisionRequest
        {
            InstanceId = submitted.Instance!.Uid,
            RowVersion = submitted.Instance.RowVersion,
            Decision = ProcurementApprovalDecisions.Approve
        });
        Assert.False(self.Succeeded);
        Assert.Contains("cannot approve", self.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var mgrApproval = CreateApproval("mgr");
        var approved = await mgrApproval.DecideAsync(new ProcurementApprovalDecisionRequest
        {
            InstanceId = submitted.Instance.Uid,
            RowVersion = submitted.Instance.RowVersion,
            Decision = ProcurementApprovalDecisions.Approve
        });
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        Assert.Equal(ProcurementApprovalStatuses.Approved, approved.Instance!.Status);

        var orders = CreatePo("user", userApproval);
        var po = await orders.SaveNewAsync(PoRequest(PoLine("A100", 1m, prNo: save.PrNo, prLine: 1)));
        Assert.True(po.Succeeded, po.ErrorMessage);
    }

    [Fact]
    public async Task NotRequired_band_records_evidence_and_allows_conversion()
    {
        await EnablePolicyAsync(notRequired: true, maxExclusive: null);
        var approval = CreateApproval("user");
        var prs = CreatePr("user", approval);
        var save = await prs.SaveNewAsync(PrRequest(PrLine("A100", 1m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var submitted = await approval.SubmitAsync(new ProcurementApprovalSubmitRequest
        {
            DocType = ProcurementApprovalDocTypes.Pr,
            DocNo = save.PrNo!
        });
        Assert.True(submitted.Succeeded, submitted.ErrorMessage);
        Assert.Equal(ProcurementApprovalStatuses.NotRequired, submitted.Instance!.Status);

        var orders = CreatePo("user", approval);
        var po = await orders.SaveNewAsync(PoRequest(PoLine("A100", 1m, prNo: save.PrNo, prLine: 1)));
        Assert.True(po.Succeeded, po.ErrorMessage);
    }

    [Fact]
    public async Task Inbox_hides_amounts_without_VIEW_COST()
    {
        await EnablePolicyAsync(notRequired: false, maxExclusive: null);
        var userApproval = CreateApproval("user");
        var prs = CreatePr("user", userApproval);
        var save = await prs.SaveNewAsync(PrRequest(PrLine("A100", 50m)));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var submitted = await userApproval.SubmitAsync(new ProcurementApprovalSubmitRequest
        {
            DocType = ProcurementApprovalDocTypes.Pr,
            DocNo = save.PrNo!
        });
        Assert.True(submitted.Succeeded, submitted.ErrorMessage);

        var hiddenAccess = Access(viewCost: false);
        var inbox = CreateApproval("mgr", hiddenAccess);
        var result = await inbox.GetInboxAsync(allPending: true, completed: false);
        Assert.True(result.Succeeded, result.ErrorMessage);
        var row = Assert.Single(result.Inbox!);
        Assert.True(row.AmountHidden);
        Assert.Null(row.Total);
    }

    [Fact]
    public async Task Inbox_submitted_by_me_lists_pending_requests_for_submitter()
    {
        await EnablePolicyAsync(notRequired: false, maxExclusive: null);
        var userApproval = CreateApproval("user");
        var prs = CreatePr("user", userApproval);
        var save = await prs.SaveNewAsync(PrRequest(PrLine("A100", 50m)));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var submitted = await userApproval.SubmitAsync(new ProcurementApprovalSubmitRequest
        {
            DocType = ProcurementApprovalDocTypes.Pr,
            DocNo = save.PrNo!
        });
        Assert.True(submitted.Succeeded, submitted.ErrorMessage);

        var mgrInbox = CreateApproval("mgr");
        var mgrMine = await mgrInbox.GetInboxAsync(allPending: false, completed: false);
        Assert.True(mgrMine.Succeeded, mgrMine.ErrorMessage);
        Assert.Single(mgrMine.Inbox!);

        var submitterInbox = await userApproval.GetInboxAsync(allPending: false, completed: false, submittedByMe: true);
        Assert.True(submitterInbox.Succeeded, submitterInbox.ErrorMessage);
        var row = Assert.Single(submitterInbox.Inbox!);
        Assert.Equal(save.PrNo, row.DocNo);

        var submitterMine = await userApproval.GetInboxAsync(allPending: false, completed: false);
        Assert.True(submitterMine.Succeeded, submitterMine.ErrorMessage);
        Assert.Empty(submitterMine.Inbox!);
    }

    [Fact]
    public async Task Assignee_lookups_return_active_company_users_and_roles()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.Roles.AddRange(
            new Role { CompanyCode = "DEMO", RoleCode = "BUYER", RoleName = "Buyer", IsActive = true },
            new Role { CompanyCode = "OTHR", RoleCode = "BUYER", RoleName = "Other buyer", IsActive = true },
            new Role { CompanyCode = "DEMO", RoleCode = "OLD", RoleName = "Inactive", IsActive = false });
        db.UserLogins.Add(new UserLogin
        {
            id = "gone",
            name = "Gone User",
            password = "x",
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            LocationCode = "MAIN",
            active = false,
            CanViewPrice = true
        });
        await db.SaveChangesAsync();

        var result = await CreateApproval("admin").GetLookupsAsync();
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Contains(result.Users!, u => u.Code == "admin");
        Assert.Contains(result.Users!, u => u.Code == "mgr" && u.DisplayText == "mgr");
        Assert.DoesNotContain(result.Users!, u => u.Code == "gone");
        Assert.Contains(result.Roles!, r => r.Code == "BUYER" && r.DisplayText == "BUYER - Buyer");
        Assert.DoesNotContain(result.Roles!, r => r.Code == "OLD");
        Assert.DoesNotContain(result.Roles!, r => string.Equals(r.Name, "Other buyer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabling_policy_does_not_unlock_pending_cycle()
    {
        await EnablePolicyAsync(notRequired: false, maxExclusive: null);
        var userApproval = CreateApproval("user");
        var prs = CreatePr("user", userApproval);
        var save = await prs.SaveNewAsync(PrRequest(PrLine("A100", 50m)));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var submitted = await userApproval.SubmitAsync(new ProcurementApprovalSubmitRequest
        {
            DocType = ProcurementApprovalDocTypes.Pr,
            DocNo = save.PrNo!
        });
        Assert.True(submitted.Succeeded, submitted.ErrorMessage);

        var admin = CreateApproval("admin");
        var current = await admin.GetPolicyAsync(ProcurementApprovalDocTypes.Pr);
        Assert.True(current.Succeeded, current.ErrorMessage);
        current.Policy!.Enabled = false;
        var saved = await admin.SavePolicyAsync(current.Policy);
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        var orders = CreatePo("user", userApproval);
        var po = await orders.SaveNewAsync(PoRequest(PoLine("A100", 1m, prNo: save.PrNo, prLine: 1)));
        Assert.False(po.Succeeded);
        Assert.Contains("waiting for approval", po.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reassign_swaps_current_approver_without_casting_a_vote()
    {
        await EnablePolicyAsync(notRequired: false, maxExclusive: null);
        var userApproval = CreateApproval("user");
        var prs = CreatePr("user", userApproval);
        var save = await prs.SaveNewAsync(PrRequest(PrLine("A100", 50m)));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var submitted = await userApproval.SubmitAsync(new ProcurementApprovalSubmitRequest
        {
            DocType = ProcurementApprovalDocTypes.Pr,
            DocNo = save.PrNo!
        });
        Assert.True(submitted.Succeeded, submitted.ErrorMessage);

        var admin = CreateApproval("admin");
        var reassigned = await admin.ReassignAsync(new ProcurementApprovalReassignRequest
        {
            InstanceId = submitted.Instance!.Uid,
            RowVersion = submitted.Instance.RowVersion,
            LevelSequence = submitted.Instance.CurrentLevel,
            RemoveUserId = "mgr",
            AddUserId = "mgr2",
            Reason = "Cover while mgr is away"
        });
        Assert.True(reassigned.Succeeded, reassigned.ErrorMessage);
        Assert.Contains("mgr2", reassigned.Instance!.CurrentAssignees, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("mgr", reassigned.Instance.CurrentAssignees, StringComparer.OrdinalIgnoreCase);

        var denied = await CreateApproval("mgr").DecideAsync(new ProcurementApprovalDecisionRequest
        {
            InstanceId = submitted.Instance.Uid,
            RowVersion = reassigned.Instance.RowVersion,
            Decision = ProcurementApprovalDecisions.Approve
        });
        Assert.False(denied.Succeeded);
        Assert.Contains("not an assigned approver", denied.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var approved = await CreateApproval("mgr2").DecideAsync(new ProcurementApprovalDecisionRequest
        {
            InstanceId = submitted.Instance.Uid,
            RowVersion = reassigned.Instance.RowVersion,
            Decision = ProcurementApprovalDecisions.Approve
        });
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        Assert.Equal(ProcurementApprovalStatuses.Approved, approved.Instance!.Status);
    }

    [Fact]
    public async Task Duplicate_submit_is_idempotent()
    {
        await EnablePolicyAsync(notRequired: false, maxExclusive: null);
        var approval = CreateApproval("user");
        var prs = CreatePr("user", approval);
        var save = await prs.SaveNewAsync(PrRequest(PrLine("A100", 50m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var first = await approval.SubmitAsync(new ProcurementApprovalSubmitRequest
        {
            DocType = ProcurementApprovalDocTypes.Pr,
            DocNo = save.PrNo!,
            IdempotencyKey = "k1"
        });
        var second = await approval.SubmitAsync(new ProcurementApprovalSubmitRequest
        {
            DocType = ProcurementApprovalDocTypes.Pr,
            DocNo = save.PrNo!,
            IdempotencyKey = "k1"
        });
        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.Equal(first.Instance!.Uid, second.Instance!.Uid);
        Assert.Equal(first.Instance.Cycle, second.Instance.Cycle);
    }

    [Fact]
    public async Task Header_discount_rejects_PO_submit()
    {
        await EnablePolicyAsync(notRequired: false, maxExclusive: null, docType: ProcurementApprovalDocTypes.Po);
        var approval = CreateApproval("user");
        var orders = CreatePo("user", approval);
        var request = PoRequest(PoLine("A100", 50m));
        request.Discount = 10m;
        var save = await orders.SaveNewAsync(request);
        Assert.True(save.Succeeded, save.ErrorMessage);

        var submit = await approval.SubmitAsync(new ProcurementApprovalSubmitRequest
        {
            DocType = ProcurementApprovalDocTypes.Po,
            DocNo = save.Document!.PoNo,
            DocRev = save.Document.PoRelNo
        });
        Assert.False(submit.Succeeded);
        Assert.Contains("header discount", submit.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnablePolicyAsync(bool notRequired, decimal? maxExclusive, string docType = ProcurementApprovalDocTypes.Pr)
    {
        var admin = CreateApproval("admin");
        var policy = new ProcurementApprovalPolicyDto
        {
            DocType = docType,
            Enabled = true,
            LegacyDraftMode = ProcurementApprovalLegacyModes.RequireGoingForward,
            Bands =
            [
                new ProcurementApprovalBandDto
                {
                    Currency = "MYR",
                    MinInclusive = 0m,
                    MaxExclusive = maxExclusive,
                    NotRequired = notRequired,
                    Levels = notRequired
                        ? []
                        :
                        [
                            new ProcurementApprovalLevelDto
                            {
                                Sequence = 1,
                                MatchMode = ProcurementApprovalMatchModes.Any,
                                Assignees =
                                [
                                    new ProcurementApprovalLevelAssigneeDto
                                    {
                                        Kind = ProcurementApprovalAssigneeKinds.User,
                                        RefCode = "mgr"
                                    }
                                ]
                            }
                        ]
                }
            ]
        };
        var saved = await admin.SavePolicyAsync(policy);
        Assert.True(saved.Succeeded, saved.ErrorMessage);
    }

    private PoPrService CreatePr(string userId, IProcurementApprovalGuard? approval = null)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext("DEMO", "HQ", "MAIN", userId);
        var attach = new PoPrAttachmentService(
            _factory,
            tenant,
            Access().Object,
            new FixedCurrentDateService(FixedToday),
            Options.Create(new AttachmentStorageOptions { RootPath = _attachRoot }),
            Options.Create(new PoPrOptions()),
            NullLogger<PoPrAttachmentService>.Instance,
            approval);
        return new PoPrService(
            _factory,
            tenant,
            Access().Object,
            new FakePoPrDocumentNumberingService(),
            new FixedCurrentDateService(FixedToday),
            new PoPrRepository(),
            Options.Create(new PoPrOptions()),
            attach,
            NullLogger<PoPrService>.Instance,
            approval: approval);
    }

    private PoOrderService CreatePo(string userId, IProcurementApprovalGuard? approval = null)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext("DEMO", "HQ", "MAIN", userId);
        var attach = new PoOrderAttachmentService(
            _factory,
            tenant,
            Access().Object,
            new FixedCurrentDateService(FixedToday),
            Options.Create(new AttachmentStorageOptions { RootPath = _attachRoot }),
            Options.Create(new PoOrderOptions()),
            NullLogger<PoOrderAttachmentService>.Instance,
            approval);
        return new PoOrderService(
            _factory,
            tenant,
            Access().Object,
            new FakePoOrderDocumentNumberingService(),
            new FixedCurrentDateService(FixedToday),
            new PoOrderRepository(),
            Options.Create(new PoOrderOptions()),
            attach,
            NullLogger<PoOrderService>.Instance,
            approval);
    }

    private ProcurementApprovalService CreateApproval(string userId, Mock<IAccessRightService>? access = null) =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext("DEMO", "HQ", "MAIN", userId),
            (access ?? Access()).Object);

    private static Mock<IAccessRightService> Access(bool viewCost = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                It.IsAny<string>(),
                It.Is<string>(p => string.Equals(p, PermissionCodes.ViewCost, StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(viewCost);
        access.Setup(x => x.CanAsync(
                It.IsAny<string>(),
                It.Is<string>(p => !string.Equals(p, PermissionCodes.ViewCost, StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    private static UserLogin User(string id) =>
        new()
        {
            id = id,
            name = id,
            password = "x",
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            LocationCode = "MAIN",
            active = true,
            CanViewPrice = true
        };

    private static PoPrSaveRequest PrRequest(PoPrLineDto line, byte[]? rowVersion = null) =>
        new()
        {
            CreateDt = FixedToday,
            Requester = "user",
            PrType = PoPrTypes.Purchasing,
            Remarks = "test",
            RowVersion = rowVersion,
            Lines = [line]
        };

    private static PoPrLineDto PrLine(string iCode, decimal qty, string currency = "MYR", short line = 0) =>
        new()
        {
            Line = line,
            ICode = iCode,
            PurchaseQty = qty,
            Qty = qty,
            PurchaseUom = "EA",
            StdUom = "EA",
            PackSz = 1m,
            Currency = currency,
            UnitPrice = 25m,
            Amount = qty * 25m,
            TaxAmount = 0m,
            TaxGroup = "SR",
            VendorCd = "SUP01",
            ToWarehouse = "MAIN"
        };

    private static PoOrderSaveRequest PoRequest(PoOrderLineDto line) =>
        new()
        {
            PoDate = FixedToday,
            VendCode = "SUP01",
            VendName = "Alpha Supplier",
            CurCode = "MYR",
            Lines = [line]
        };

    private static PoOrderLineDto PoLine(string iCode, decimal qty, string? prNo = null, short? prLine = null) =>
        new()
        {
            ICode = iCode,
            PoPurQty = qty,
            PoQty = qty,
            PurchaseUom = "EA",
            StdUom = "EA",
            PackSz = 1m,
            CurCode = "MYR",
            PoUnitPrice = 25m,
            TaxGroup = "SR",
            ToWarehouse = "MAIN",
            PrNo = prNo,
            PrLineNo = prLine
        };
}
