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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ErpWeb.Tests.Procurement.Transaction;

/// <summary>
/// Gate 3 SQL Server races for PR/PO approval. Uses only
/// <see cref="PoApprovalSqlGate3Fixture.ConnectionEnv"/> against an isolated catalog named
/// erpweb_poapr_test_*. Never DefaultConnection / shared ERPWeb DEMO.
/// </summary>
[Collection("PoApprovalSqlGate3")]
[Trait(TestCategories.Name, TestCategories.Purchase)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public class ProcurementApprovalSqlServerConcurrencyTests
{
    private static readonly DateTime FixedToday = new(2026, 9, 12);
    private const string Submitter = "AVUSR1";
    private const string Manager1 = "AVMG1";
    private const string Manager2 = "AVMG2";
    private const string Admin = "AVADM";
    private const string OtherCompany = "ISO2";
    private const string OtherUser = "ISO2USR";

    private readonly PoApprovalSqlGate3Fixture _harness;

    public ProcurementApprovalSqlServerConcurrencyTests(PoApprovalSqlGate3Fixture harness)
    {
        _harness = harness;
    }

    private IDbContextFactory<AppDbContext> Factory => _harness.RequireFactory();

    [PoApprovalSqlFact]
    public async Task Preflight_isolated_catalog_has_filtered_unique_index()
    {
        await using var db = Factory.CreateDbContext();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_NAME()";
        var catalog = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.StartsWith(
            PoApprovalSqlGate3Fixture.CatalogPrefix,
            catalog,
            StringComparison.OrdinalIgnoreCase);
        await PoApprovalSqlGate3Fixture.AssertFilteredUniqueIndexAsync(db);
    }

    [PoApprovalSqlFact]
    public async Task SqlServer_two_policy_publishers_leave_one_published_row()
    {
        var factory = Factory;
        await SeedMastersAsync(factory);
        var first = CreateApproval(factory, Admin);
        var second = CreateApproval(factory, Manager1);
        var results = await Task.WhenAll(
            first.SavePolicyAsync(EnabledPolicy()),
            second.SavePolicyAsync(EnabledPolicy()));

        Assert.Contains(results, r => r.Succeeded);
        await using var db = factory.CreateDbContext();
        var published = await db.ProcurementApprovalPolicies.AsNoTracking()
            .Where(x => x.CompanyCode == "DEMO"
                && x.DocType == ProcurementApprovalDocTypes.Pr
                && x.IsPublished)
            .ToListAsync();
        Assert.Single(published);
        Assert.True(published[0].Enabled);
    }

    [PoApprovalSqlFact]
    public async Task SqlServer_concurrent_submit_returns_the_same_pending_instance()
    {
        await RunWithPolicyAsync(async (factory, fixture) =>
        {
            var first = CreateApproval(factory, Submitter);
            var second = CreateApproval(factory, Submitter);
            var results = await Task.WhenAll(
                first.SubmitAsync(new ProcurementApprovalSubmitRequest
                {
                    DocType = ProcurementApprovalDocTypes.Pr,
                    DocNo = fixture.PrNo
                }),
                second.SubmitAsync(new ProcurementApprovalSubmitRequest
                {
                    DocType = ProcurementApprovalDocTypes.Pr,
                    DocNo = fixture.PrNo
                }));

            Assert.Contains(results, r => r.Succeeded);
            await using var db = factory.CreateDbContext();
            var live = await db.ProcurementApprovalInstances.AsNoTracking()
                .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ"
                    && x.DocType == ProcurementApprovalDocTypes.Pr && x.DocNo == fixture.PrNo)
                .ToListAsync();
            var active = live.Where(x =>
                    string.Equals(x.Status, ProcurementApprovalStatuses.Pending, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Status, ProcurementApprovalStatuses.Approved, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Status, ProcurementApprovalStatuses.NotRequired, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Single(active);
            var succeeded = results.Where(r => r.Succeeded).ToList();
            Assert.All(succeeded, r => Assert.Equal(active[0].Uid, r.Instance!.Uid));
        });
    }

    [PoApprovalSqlFact]
    public async Task SqlServer_concurrent_approve_and_reject_exactly_one_wins()
    {
        await RunWithPolicyAsync(async (factory, fixture) =>
        {
            var submit = await CreateApproval(factory, Submitter).SubmitAsync(new ProcurementApprovalSubmitRequest
            {
                DocType = ProcurementApprovalDocTypes.Pr,
                DocNo = fixture.PrNo
            });
            Assert.True(submit.Succeeded, submit.ErrorMessage);
            var instance = submit.Instance!;

            var approve = CreateApproval(factory, Manager1).DecideAsync(new ProcurementApprovalDecisionRequest
            {
                InstanceId = instance.Uid,
                RowVersion = instance.RowVersion,
                Decision = ProcurementApprovalDecisions.Approve
            });
            var reject = CreateApproval(factory, Manager2).DecideAsync(new ProcurementApprovalDecisionRequest
            {
                InstanceId = instance.Uid,
                RowVersion = instance.RowVersion,
                Decision = ProcurementApprovalDecisions.Reject,
                Reason = "Concurrent reject"
            });
            var results = await Task.WhenAll(approve, reject);

            Assert.Equal(1, results.Count(r => r.Succeeded));
            Assert.Equal(1, results.Count(r => !r.Succeeded));
            await using var db = factory.CreateDbContext();
            var live = await db.ProcurementApprovalInstances.AsNoTracking()
                .SingleAsync(x => x.Uid == instance.Uid);
            Assert.Contains(live.Status, new[]
            {
                ProcurementApprovalStatuses.Approved,
                ProcurementApprovalStatuses.Rejected
            });
        });
    }

    [PoApprovalSqlFact]
    public async Task SqlServer_disabling_policy_does_not_unlock_pending_conversion()
    {
        await RunWithPolicyAsync(async (factory, fixture) =>
        {
            var userApproval = CreateApproval(factory, Submitter);
            var submitted = await userApproval.SubmitAsync(new ProcurementApprovalSubmitRequest
            {
                DocType = ProcurementApprovalDocTypes.Pr,
                DocNo = fixture.PrNo
            });
            Assert.True(submitted.Succeeded, submitted.ErrorMessage);

            var admin = CreateApproval(factory, Admin);
            var current = await admin.GetPolicyAsync(ProcurementApprovalDocTypes.Pr);
            Assert.True(current.Succeeded, current.ErrorMessage);
            current.Policy!.Enabled = false;
            var disabled = await admin.SavePolicyAsync(current.Policy);
            Assert.True(disabled.Succeeded, disabled.ErrorMessage);

            var orders = CreatePo(factory, Submitter, userApproval);
            var po = await orders.SaveNewAsync(PoRequest(fixture, 1m));
            Assert.False(po.Succeeded);
            Assert.Contains("waiting for approval", po.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        });
    }

    [PoApprovalSqlFact]
    public async Task SqlServer_cross_company_pending_is_not_visible_to_demo_inbox()
    {
        var factory = Factory;
        await SeedMastersAsync(factory);
        var demoAdmin = CreateApproval(factory, Admin);
        Assert.True((await demoAdmin.SavePolicyAsync(EnabledPolicy())).Succeeded);

        var otherAdmin = CreateApproval(factory, Admin, OtherCompany);
        Assert.True((await otherAdmin.SavePolicyAsync(EnabledPolicy())).Succeeded);

        var otherPr = await SeedPrAsync(factory, OtherCompany, OtherUser);
        var submitted = await CreateApproval(factory, OtherUser, OtherCompany).SubmitAsync(
            new ProcurementApprovalSubmitRequest
            {
                DocType = ProcurementApprovalDocTypes.Pr,
                DocNo = otherPr
            });
        Assert.True(submitted.Succeeded, submitted.ErrorMessage);

        var inbox = await CreateApproval(factory, Manager1).GetInboxAsync(allPending: true, completed: false);
        Assert.True(inbox.Succeeded, inbox.ErrorMessage);
        Assert.DoesNotContain(inbox.Inbox ?? [], row =>
            string.Equals(row.DocNo, otherPr, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RunWithPolicyAsync(Func<IDbContextFactory<AppDbContext>, PrFixture, Task> body)
    {
        var factory = Factory;
        var fixture = await SeedFixtureAsync(factory);
        var admin = CreateApproval(factory, Admin);
        var enabled = await admin.SavePolicyAsync(EnabledPolicy());
        Assert.True(enabled.Succeeded, enabled.ErrorMessage);
        await body(factory, fixture);
    }

    private static async Task SeedMastersAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = factory.CreateDbContext();
        if (!await db.SaCurrencies.AnyAsync(x => x.CompanyCode == "DEMO" && x.CurrCode == "MYR"))
        {
            db.SaCurrencies.Add(new SaCurrency
            {
                CompanyCode = "DEMO",
                CurrCode = "MYR",
                CurrDesc = "Ringgit",
                IsActive = true
            });
        }

        if (!await db.SaCurrencies.AnyAsync(x => x.CompanyCode == OtherCompany && x.CurrCode == "MYR"))
        {
            db.SaCurrencies.Add(new SaCurrency
            {
                CompanyCode = OtherCompany,
                CurrCode = "MYR",
                CurrDesc = "Ringgit",
                IsActive = true
            });
        }

        foreach (var company in new[] { "DEMO", OtherCompany })
        {
            if (!await db.MsUoms.AnyAsync(x => x.CompanyCode == company && x.UomCode == "EA"))
            {
                db.MsUoms.Add(new MsUom { CompanyCode = company, UomCode = "EA", IsActive = true });
            }

            if (!await db.IvClasses.AnyAsync(x => x.CompanyCode == company && x.IClassCode == "RAW"))
            {
                db.IvClasses.Add(new IvClass { CompanyCode = company, IClassCode = "RAW", IsActive = true });
            }
        }

        foreach (var id in new[] { Submitter, Manager1, Manager2, Admin })
        {
            if (!await db.UserLogins.AnyAsync(x => x.CompanyCode == "DEMO" && x.id == id))
            {
                db.UserLogins.Add(User(id, "DEMO"));
            }
        }

        foreach (var id in new[] { OtherUser, Admin, Manager1, Manager2 })
        {
            if (!await db.UserLogins.AnyAsync(x => x.CompanyCode == OtherCompany && x.id == id))
            {
                db.UserLogins.Add(User(id, OtherCompany));
            }
        }

        if (!await db.SaTaxGroups.AnyAsync(x => x.CompanyCode == "DEMO" && x.TaxGrCode == "SR"))
        {
            db.SaTaxGroups.Add(TaxGroup("DEMO"));
        }

        if (!await db.SaTaxGroups.AnyAsync(x => x.CompanyCode == OtherCompany && x.TaxGrCode == "SR"))
        {
            db.SaTaxGroups.Add(TaxGroup(OtherCompany));
        }

        await EnsureSupplierAndWarehouseAsync(db, "DEMO");
        await EnsureSupplierAndWarehouseAsync(db, OtherCompany);
        await db.SaveChangesAsync();
    }

    private static async Task<PrFixture> SeedFixtureAsync(IDbContextFactory<AppDbContext> factory)
    {
        await SeedMastersAsync(factory);
        await using var db = factory.CreateDbContext();
        var iCode = "A" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.IvStockMasters.Add(Stock(iCode, "DEMO"));
        var prNo = "APR" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.PoPrs.Add(Pr(prNo, iCode, "DEMO", Submitter));
        await db.SaveChangesAsync();
        return new PrFixture(iCode, prNo);
    }

    private static async Task<string> SeedPrAsync(
        IDbContextFactory<AppDbContext> factory, string company, string requester)
    {
        await using var db = factory.CreateDbContext();
        var iCode = "B" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.IvStockMasters.Add(Stock(iCode, company));
        var prNo = "BPR" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.PoPrs.Add(Pr(prNo, iCode, company, requester));
        await db.SaveChangesAsync();
        return prNo;
    }

    private static async Task EnsureSupplierAndWarehouseAsync(AppDbContext db, string company)
    {
        if (!await db.PoSuppliers.AnyAsync(x =>
                x.CompanyCode == company && x.BranchCode == "HQ" && x.SuppCode == "SUP01"))
        {
            db.PoSuppliers.Add(new PoSupplier
            {
                CompanyCode = company,
                BranchCode = "HQ",
                SuppCode = "SUP01",
                SuppName = "Alpha Supplier",
                Currency = "MYR",
                GlCode = "AP001",
                Country = "MY",
                IsActive = true
            });
        }

        if (!await db.IvWarehouses.AnyAsync(x =>
                x.CompanyCode == company && x.BranchCode == "HQ" && x.WarehouseCode == "MAIN"))
        {
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = company,
                BranchCode = "HQ",
                WarehouseCode = "MAIN",
                IsActive = true
            });
        }
    }

    private static UserLogin User(string id, string company) =>
        new()
        {
            id = id,
            name = id,
            password = "x",
            CompanyCode = company,
            BranchCode = "HQ",
            LocationCode = "MAIN",
            active = true,
            CanViewPrice = true
        };

    private static SaTaxGroup TaxGroup(string company) =>
        new()
        {
            CompanyCode = company,
            TaxGrCode = "SR",
            TaxGrDesc = "Standard",
            Percentage = 6m,
            TaxGlCode = "GLTAX"
        };

    private static IvStockMaster Stock(string iCode, string company) =>
        new()
        {
            CompanyCode = company,
            ICode = iCode,
            IDesc = "Approval concurrency item",
            IClassCode = "RAW",
            StdUom = "EA",
            PurUom = "EA",
            PurStdPackSize = 1m,
            PurchasePrice = 25m,
            PurchaseTaxGroup = "SR",
            IsActive = true,
            DefWarehouse = "MAIN"
        };

    private static PoPr Pr(string prNo, string iCode, string company, string requester) =>
        new()
        {
            CompanyCode = company,
            BranchCode = "HQ",
            PrNo = prNo,
            CreateDt = FixedToday,
            Requester = requester,
            Status = PoPrStatuses.New,
            PrType = PoPrTypes.Purchasing,
            CreatedDate = FixedToday,
            CreatedBy = requester,
            Details =
            [
                new PoPrDetail
                {
                    CompanyCode = company,
                    BranchCode = "HQ",
                    PrNo = prNo,
                    Line = 1,
                    ICode = iCode,
                    IDesc = "Approval concurrency item",
                    Qty = 50m,
                    PurchaseQty = 50m,
                    StdQty = 50m,
                    PackSz = 1m,
                    StdUom = "EA",
                    PurchaseUom = "EA",
                    Currency = "MYR",
                    UnitPrice = 25m,
                    Amount = 1250m,
                    VendorCd = "SUP01",
                    VendNm = "Alpha Supplier",
                    TaxGroup = "SR",
                    ToWarehouse = "MAIN",
                    Status = PoPrStatuses.New
                }
            ]
        };

    private static ProcurementApprovalPolicyDto EnabledPolicy() =>
        new()
        {
            DocType = ProcurementApprovalDocTypes.Pr,
            Enabled = true,
            LegacyDraftMode = ProcurementApprovalLegacyModes.RequireGoingForward,
            Bands =
            [
                new ProcurementApprovalBandDto
                {
                    Currency = "MYR",
                    MinInclusive = 0m,
                    NotRequired = false,
                    Levels =
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
                                    RefCode = Manager1
                                },
                                new ProcurementApprovalLevelAssigneeDto
                                {
                                    Kind = ProcurementApprovalAssigneeKinds.User,
                                    RefCode = Manager2
                                }
                            ]
                        }
                    ]
                }
            ]
        };

    private static PoOrderSaveRequest PoRequest(PrFixture fixture, decimal qty) =>
        new()
        {
            PoDate = FixedToday,
            VendCode = "SUP01",
            VendName = "Alpha Supplier",
            CurCode = "MYR",
            Lines =
            [
                new PoOrderLineDto
                {
                    ICode = fixture.ICode,
                    PoPurQty = qty,
                    PoQty = qty,
                    PurchaseUom = "EA",
                    StdUom = "EA",
            PackSz = 1m,
            CurCode = "MYR",
            PoUnitPrice = 25m,
            TaxGroup = "SR",
                    ToWarehouse = "MAIN",
                    PrNo = fixture.PrNo,
                    PrLineNo = 1
                }
            ]
        };

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    private static ProcurementApprovalService CreateApproval(
        IDbContextFactory<AppDbContext> factory, string userId, string company = "DEMO") =>
        new(
            factory,
            InventoryTenantTestHelper.CreateTenantContext(company, "HQ", "MAIN", userId),
            Access().Object);

    private static PoOrderService CreatePo(
        IDbContextFactory<AppDbContext> factory,
        string userId,
        IProcurementApprovalGuard approval)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext("DEMO", "HQ", "MAIN", userId);
        var attachRoot = Path.Combine(Path.GetTempPath(), "po-approval-sql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachRoot);
        var attachments = new PoOrderAttachmentService(
            factory,
            tenant,
            Access().Object,
            new FixedCurrentDateService(FixedToday),
            Options.Create(new AttachmentStorageOptions { RootPath = attachRoot }),
            Options.Create(new PoOrderOptions()),
            NullLogger<PoOrderAttachmentService>.Instance,
            approval);
        return new PoOrderService(
            factory,
            tenant,
            Access().Object,
            new FakePoOrderDocumentNumberingService(),
            new FixedCurrentDateService(FixedToday),
            new PoOrderRepository(),
            Options.Create(new PoOrderOptions()),
            attachments,
            NullLogger<PoOrderService>.Instance,
            approval);
    }

    private sealed record PrFixture(string ICode, string PrNo);
}
