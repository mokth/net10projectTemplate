using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ErpWeb.Tests.Procurement.Transaction;

/// <summary>
/// SQL Server races for PR/PO approval. Skipped unless DefaultConnection can open DEMO
/// and PoApprovalPolicy exists. Policy is snapshotted and restored in finally so DEMO
/// is not left with approval enabled.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Purchase)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public class ProcurementApprovalSqlServerConcurrencyTests
{
    private static readonly DateTime FixedToday = new(2026, 9, 12);
    private const string Submitter = "AVUSR1";
    private const string Manager1 = "AVMG1";
    private const string Manager2 = "AVMG2";
    private const string Admin = "AVADM";

    private static string? GetSqlServerConnectionString()
    {
        var testDir = AppContext.BaseDirectory;
        var repoAppSettings = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "ErpWeb", "appsettings.json"));
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddJsonFile(repoAppSettings, optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return null;
        }

        if (!cs.Contains("Database=", StringComparison.OrdinalIgnoreCase)
            && !cs.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return cs;
    }

    public static bool IsSqlServerAvailable()
    {
        var cs = GetSqlServerConnectionString();
        if (cs is null)
        {
            return false;
        }

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
            using var db = new AppDbContext(options);
            return db.Database.IsSqlServer() && db.Database.CanConnect();
        }
        catch
        {
            return false;
        }
    }

    private static IDbContextFactory<AppDbContext> CreateFactory()
    {
        var cs = GetSqlServerConnectionString()
            ?? throw new InvalidOperationException("SQL Server connection string is required.");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        return new TestDbContextFactory(options);
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    private static async Task RunWithPolicyAsync(Func<IDbContextFactory<AppDbContext>, PrFixture, Task> body)
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        if (!await SchemaReadyAsync(factory))
        {
            return;
        }

        var admin = CreateApproval(factory, Admin);
        var snapshot = await admin.GetPolicyAsync(ProcurementApprovalDocTypes.Pr);
        if (!snapshot.Succeeded)
        {
            return;
        }

        var restore = ClonePolicy(snapshot.Policy ?? new ProcurementApprovalPolicyDto
        {
            DocType = ProcurementApprovalDocTypes.Pr,
            Enabled = false
        });

        try
        {
            var fixture = await SeedFixtureAsync(factory);
            if (fixture is null)
            {
                return;
            }

            var enabled = await admin.SavePolicyAsync(EnabledPolicy());
            Assert.True(enabled.Succeeded, enabled.ErrorMessage);
            await body(factory, fixture);
        }
        finally
        {
            var restored = await CreateApproval(factory, Admin).SavePolicyAsync(restore);
            Assert.True(restored.Succeeded, restored.ErrorMessage);
            Assert.Equal(restore.Enabled, restored.Policy!.Enabled);
        }
    }

    private static async Task<bool> SchemaReadyAsync(IDbContextFactory<AppDbContext> factory)
    {
        try
        {
            await using var db = factory.CreateDbContext();
            _ = await db.ProcurementApprovalPolicies.CountAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<PrFixture?> SeedFixtureAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = factory.CreateDbContext();
        try
        {
            _ = await db.PoPrs.CountAsync();
        }
        catch
        {
            return null;
        }

        if (!await db.SaCurrencies.AnyAsync(x => x.CompanyCode == "DEMO" && x.CurrCode == "MYR"))
        {
            return null;
        }

        foreach (var id in new[] { Submitter, Manager1, Manager2, Admin })
        {
            if (!await db.UserLogins.AnyAsync(x => x.CompanyCode == "DEMO" && x.id == id))
            {
                db.UserLogins.Add(new UserLogin
                {
                    id = id,
                    name = id,
                    password = "x",
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    LocationCode = "MAIN",
                    active = true,
                    CanViewPrice = true
                });
            }
        }

        if (!await db.SaTaxGroups.AnyAsync(x => x.CompanyCode == "DEMO" && x.TaxGrCode == "SR"))
        {
            db.SaTaxGroups.Add(new SaTaxGroup
            {
                CompanyCode = "DEMO",
                TaxGrCode = "SR",
                TaxGrDesc = "Standard",
                Percentage = 6m,
                TaxGlCode = "GLTAX"
            });
        }

        if (!await db.PoSuppliers.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.SuppCode == "SUP01"))
        {
            db.PoSuppliers.Add(new PoSupplier
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SuppCode = "SUP01",
                SuppName = "Alpha Supplier",
                Currency = "MYR",
                GlCode = "AP001",
                IsActive = true
            });
        }

        if (!await db.IvWarehouses.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.WarehouseCode == "MAIN"))
        {
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "MAIN",
                IsActive = true
            });
        }

        var iCode = "A" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = iCode,
            IDesc = "Approval concurrency item",
            StdUom = "EA",
            PurUom = "EA",
            PurStdPackSize = 1m,
            PurchasePrice = 25m,
            PurchaseTaxGroup = "SR",
            IsActive = true,
            DefWarehouse = "MAIN",
            StockControl = false
        });

        if (!await db.AdSmNumDates.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.NumCd == "PO"
                && x.Year == 2026 && x.Month == 9))
        {
            db.AdSmNumDates.Add(new AdSmNumDate
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                Year = 2026,
                Month = 9,
                NumCd = "PO",
                NumDes = "Purchase Order",
                Prefix = "PO",
                TotLength = 4,
                NumberingDelimeter = "-",
                Seq = 1
            });
        }

        if (!await db.AdSmNums.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.NumCd == "PO"))
        {
            db.AdSmNums.Add(new AdSmNum
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                NumCd = "PO",
                NumDes = "Purchase Order",
                Prefix = "PO",
                TotLength = 10,
                Seq = 1
            });
        }

        var prNo = "APR" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.PoPrs.Add(new PoPr
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PrNo = prNo,
            CreateDt = FixedToday,
            Requester = Submitter,
            Status = PoPrStatuses.New,
            PrType = PoPrTypes.Purchasing,
            CreatedDate = FixedToday,
            CreatedBy = Submitter,
            Details =
            [
                new PoPrDetail
                {
                    CompanyCode = "DEMO",
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
        });

        try
        {
            await db.SaveChangesAsync();
        }
        catch
        {
            return null;
        }

        return new PrFixture(iCode, prNo);
    }

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

    private static ProcurementApprovalPolicyDto ClonePolicy(ProcurementApprovalPolicyDto source) =>
        new()
        {
            DocType = string.IsNullOrWhiteSpace(source.DocType) ? ProcurementApprovalDocTypes.Pr : source.DocType,
            Enabled = source.Enabled,
            LegacyDraftMode = source.LegacyDraftMode,
            Bands = source.Bands.Select(band => new ProcurementApprovalBandDto
            {
                Currency = band.Currency,
                MinInclusive = band.MinInclusive,
                MaxExclusive = band.MaxExclusive,
                NotRequired = band.NotRequired,
                Levels = band.Levels.Select(level => new ProcurementApprovalLevelDto
                {
                    Sequence = level.Sequence,
                    MatchMode = level.MatchMode,
                    Assignees = level.Assignees.Select(assignee => new ProcurementApprovalLevelAssigneeDto
                    {
                        Kind = assignee.Kind,
                        RefCode = assignee.RefCode
                    }).ToList()
                }).ToList()
            }).ToList()
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

    private static ProcurementApprovalService CreateApproval(IDbContextFactory<AppDbContext> factory, string userId) =>
        new(
            factory,
            InventoryTenantTestHelper.CreateTenantContext("DEMO", "HQ", "MAIN", userId),
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
            new DocumentNumberingService(tenant),
            new FixedCurrentDateService(FixedToday),
            new PoOrderRepository(),
            Options.Create(new PoOrderOptions()),
            attachments,
            NullLogger<PoOrderService>.Instance,
            approval);
    }

    private sealed record PrFixture(string ICode, string PrNo);
}
