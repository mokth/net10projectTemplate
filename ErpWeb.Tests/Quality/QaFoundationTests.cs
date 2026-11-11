using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Quality;
using ErpWeb.Core.Settings;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Quality;

[Trait(TestCategories.Name, TestCategories.Quality)]
public class QaFoundationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly Mock<IInventoryTenantContext> _tenant = new();
    private readonly Mock<IAccessRightService> _access = new();
    private InventoryTenantScope _scope = Scope("tester");

    public QaFoundationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        BindTenant();
        _access
            .Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public void Catalogue_receipt_inspection_flag_stays_off_and_unwired()
    {
        Assert.Equal("false", AppSettingCatalogue.QaRequireInspectionOnReceipt.DefaultValue);
        Assert.Contains(
            "does not block",
            AppSettingCatalogue.QaRequireInspectionOnReceipt.Description,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Policy_defaults_all_posting_gates_off()
    {
        var result = await CreatePolicyService().GetAsync();
        Assert.True(result.Succeeded);
        Assert.False(result.Policy!.Enabled);
        Assert.False(result.Policy.IncomingInspectionEnabled);
        Assert.False(result.Policy.FinalFgInspectionEnabled);
        Assert.False(result.Policy.LogicalHoldEnabled);
        Assert.False(result.Policy.PhysicalHoldEnabled);
        Assert.False(result.Policy.NonGoodEnabled);
        Assert.True(result.Policy.InspectorApproverSeparation);
    }

    [Fact]
    public async Task Policy_refuses_to_enable_physical_warehouse_hold()
    {
        var result = await CreatePolicyService().SaveAsync(new QaPolicySaveCommand
        {
            InspectorApproverSeparation = true,
            PhysicalHoldEnabled = true
        });
        Assert.False(result.Succeeded);
        Assert.Contains("frozen", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("physical hold", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Policy_refuses_to_enable_nongood_daily_production_gate()
    {
        var result = await CreatePolicyService().SaveAsync(new QaPolicySaveCommand
        {
            InspectorApproverSeparation = true,
            NonGoodEnabled = true
        });
        Assert.False(result.Succeeded);
        Assert.Contains("frozen", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-good", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Policy_refuses_to_enable_finished_goods_inspection_gate()
    {
        var result = await CreatePolicyService().SaveAsync(new QaPolicySaveCommand
        {
            InspectorApproverSeparation = true,
            FinalFgInspectionEnabled = true
        });
        Assert.False(result.Succeeded);
        Assert.Contains("frozen", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("finished-goods", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Policy_refuses_to_enable_incoming_inspection_gate()
    {
        var result = await CreatePolicyService().SaveAsync(new QaPolicySaveCommand
        {
            InspectorApproverSeparation = true,
            IncomingInspectionEnabled = true
        });
        Assert.False(result.Succeeded);
        Assert.Contains("frozen", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incoming", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Policy_refuses_to_enable_logical_wip_hold()
    {
        var result = await CreatePolicyService().SaveAsync(new QaPolicySaveCommand
        {
            InspectorApproverSeparation = true,
            LogicalHoldEnabled = true
        });
        Assert.False(result.Succeeded);
        Assert.Contains("frozen", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logical hold", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Policy_can_save_inspector_separation_without_enabling_gates()
    {
        var result = await CreatePolicyService().SaveAsync(new QaPolicySaveCommand
        {
            InspectorApproverSeparation = false
        });
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.False(result.Policy!.Enabled);
        Assert.False(result.Policy.InspectorApproverSeparation);
        Assert.True(result.Policy.IsPublished);
    }

    [Fact]
    public async Task Draft_submit_inspect_approve_does_not_mutate_stock_or_wip()
    {
        var cases = CreateCaseService();
        var saved = await cases.SaveDraftAsync(GoodDraft());
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var submitted = await cases.SubmitAsync(Review(saved.Case!));
        Assert.True(submitted.Succeeded, submitted.ErrorMessage);
        var started = await cases.StartInspectionAsync(Review(submitted.Case!));
        Assert.True(started.Succeeded, started.ErrorMessage);
        var inspected = await cases.SaveInspectionAsync(PassInspection(started.Case!));
        Assert.True(inspected.Succeeded, inspected.ErrorMessage);
        var review = await cases.SubmitForReviewAsync(Review(inspected.Case!));
        Assert.True(review.Succeeded, review.ErrorMessage);

        _scope = Scope("approver");
        BindTenant();
        var approved = await cases.ApproveAsync(Review(review.Case!));
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        Assert.Equal(QaCaseStatuses.Approved, approved.Case!.Status);
        Assert.Contains(approved.Case.Audit, x => x.EventType == QaAuditEvents.Approved);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(0, await db.IvBalLocs.CountAsync());
            Assert.Equal(0, await db.ProductionBalLots.CountAsync());
            Assert.Equal(0, await db.IvTrxBatches.CountAsync());
            Assert.Equal(0, await db.StockPostings.CountAsync());
        }
    }

    [Fact]
    public async Task Incoming_approve_is_record_only_without_grant_stock_or_vendor_return()
    {
        await SeedPoAsync("PO1", rel: 0, line: 1, item: "A100", qty: 100m);
        var cases = CreateCaseService();
        var saved = await cases.SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.IncomingPreGrn,
            PoNo = "PO1",
            PoRelNo = 0,
            DeliveryNote = "DN-1",
            SupplierCode = "SUP01",
            Lines =
            [
                new QaCaseLineSaveCommand
                {
                    LineNo = 1,
                    ItemCode = "A100",
                    Uom = "EA",
                    Qty = 100m,
                    PoLineNo = 1
                }
            ]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        Assert.NotNull(saved.Case!.DeliveryInstanceId);
        var submitted = await cases.SubmitAsync(Review(saved.Case!));
        var started = await cases.StartInspectionAsync(Review(submitted.Case!));
        var inspected = await cases.SaveInspectionAsync(new QaInspectionSaveCommand
        {
            CaseNo = started.Case!.CaseNo,
            RowVersion = started.Case.RowVersion,
            Lines =
            [
                new QaInspectionLineCommand
                {
                    LineNo = 1,
                    Result = QaInspectionResults.Conditional,
                    InspectedQty = 100m,
                    Dispositions =
                    [
                        new QaDispositionDto { Kind = QaDispositionKinds.Accept, Qty = 85m },
                        new QaDispositionDto { Kind = QaDispositionKinds.Reject, Qty = 15m }
                    ]
                }
            ]
        });
        Assert.True(inspected.Succeeded, inspected.ErrorMessage);
        var review = await cases.SubmitForReviewAsync(Review(inspected.Case!));
        _scope = Scope("approver");
        BindTenant();
        var approved = await cases.ApproveAsync(Review(review.Case!));
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        Assert.Equal(85m, approved.Case!.AcceptedQty);
        Assert.Equal(15m, approved.Case.RejectedQty);
        Assert.Equal(85m, approved.Case.RemainingQty);
        Assert.Equal(0m, approved.Case.PostedQty);
        Assert.DoesNotContain(approved.Case.Audit, x => x.EventType == QaAuditEvents.GrantIssued);
        Assert.Equal(QaEligibilityNotes.IncomingPreGrn, approved.Case.EligibilityNote);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.QaReceiptGrants.CountAsync());
        Assert.Equal(0, await db.QaReceiptAllocations.CountAsync());
        Assert.Equal(0, await db.IvBalLocs.CountAsync());
        Assert.Equal(0, await db.IvTrxBatches.CountAsync());
        Assert.Equal(0, await db.PoCdns.CountAsync());
    }

    [Fact]
    public async Task Fg_staging_approve_is_record_only_without_grant_stock_or_padj()
    {
        var lotId = await SeedFgLotAsync(10m);
        var cases = CreateCaseService();
        var saved = await cases.SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.FgStagingLot,
            WorkOrderNo = "WO-FG",
            ProductionBalLotId = lotId,
            Lines =
            [
                new QaCaseLineSaveCommand
                {
                    LineNo = 1,
                    ItemCode = "FG1",
                    Uom = "EA",
                    Qty = 10m,
                    LotNo = "PROD-1"
                }
            ]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var submitted = await cases.SubmitAsync(Review(saved.Case!));
        var started = await cases.StartInspectionAsync(Review(submitted.Case!));
        var inspected = await cases.SaveInspectionAsync(new QaInspectionSaveCommand
        {
            CaseNo = started.Case!.CaseNo,
            RowVersion = started.Case.RowVersion,
            Lines =
            [
                new QaInspectionLineCommand
                {
                    LineNo = 1,
                    Result = QaInspectionResults.Conditional,
                    InspectedQty = 10m,
                    Dispositions =
                    [
                        new QaDispositionDto { Kind = QaDispositionKinds.Accept, Qty = 8m },
                        new QaDispositionDto { Kind = QaDispositionKinds.Reject, Qty = 2m }
                    ]
                }
            ]
        });
        Assert.True(inspected.Succeeded, inspected.ErrorMessage);
        var review = await cases.SubmitForReviewAsync(Review(inspected.Case!));
        _scope = Scope("approver");
        BindTenant();
        var approved = await cases.ApproveAsync(Review(review.Case!));
        Assert.True(approved.Succeeded, approved.ErrorMessage);
        Assert.Equal(8m, approved.Case!.AcceptedQty);
        Assert.Equal(2m, approved.Case.RejectedQty);
        Assert.Equal(8m, approved.Case.RemainingQty);
        Assert.Equal(0m, approved.Case.PostedQty);
        Assert.DoesNotContain(approved.Case.Audit, x => x.EventType == QaAuditEvents.GrantIssued);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.QaFgGrants.CountAsync());
        Assert.Equal(0, await db.QaFgAllocations.CountAsync());
        var lot = await db.ProductionBalLots.SingleAsync();
        Assert.Equal(10m, lot.BaseQty);
        Assert.Equal(0, await db.IvBalLocs.CountAsync());
    }

    [Fact]
    public async Task Inspector_cannot_approve_when_separation_is_on()
    {
        await CreatePolicyService().SaveAsync(new QaPolicySaveCommand { InspectorApproverSeparation = true });
        var cases = CreateCaseService();
        var saved = await cases.SaveDraftAsync(GoodDraft());
        var submitted = await cases.SubmitAsync(Review(saved.Case!));
        var started = await cases.StartInspectionAsync(Review(submitted.Case!));
        var inspected = await cases.SaveInspectionAsync(PassInspection(started.Case!));
        var review = await cases.SubmitForReviewAsync(Review(inspected.Case!));
        var approved = await cases.ApproveAsync(Review(review.Case!));
        Assert.False(approved.Succeeded);
        Assert.Contains("inspector cannot approve", approved.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Incoming_draft_requires_po_or_delivery_note()
    {
        var result = await CreateCaseService().SaveDraftAsync(new QaCaseSaveCommand
        {
            CaseDate = DateTime.Today,
            SourceType = QaSourceTypes.IncomingPreGrn,
            Lines =
            [
                new QaCaseLineSaveCommand { LineNo = 1, ItemCode = "RM1", Uom = "PCS", Qty = 1m }
            ]
        });
        Assert.False(result.Succeeded);
        Assert.Contains("delivery note", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspection_rejects_disposition_that_does_not_balance()
    {
        var cases = CreateCaseService();
        var saved = await cases.SaveDraftAsync(GoodDraft());
        var submitted = await cases.SubmitAsync(Review(saved.Case!));
        var started = await cases.StartInspectionAsync(Review(submitted.Case!));
        var inspected = await cases.SaveInspectionAsync(new QaInspectionSaveCommand
        {
            CaseNo = started.Case!.CaseNo,
            RowVersion = started.Case.RowVersion,
            Lines =
            [
                new QaInspectionLineCommand
                {
                    LineNo = 1,
                    Result = QaInspectionResults.Fail,
                    InspectedQty = 5m,
                    Dispositions = [new QaDispositionDto { Kind = QaDispositionKinds.Reject, Qty = 1m }]
                }
            ]
        });
        Assert.False(inspected.Succeeded);
        Assert.Contains("equal the inspected quantity", inspected.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Access_is_denied_without_permission()
    {
        _access.Reset();
        _access
            .Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var result = await CreateCaseService().SearchAsync(new QaListQuery());
        Assert.False(result.Succeeded);
        Assert.Equal("Not authorized.", result.ErrorMessage);
    }

    private IQaPolicyService CreatePolicyService() =>
        new QaPolicyService(_factory, _tenant.Object, _access.Object);

    private IQaCaseService CreateCaseService() =>
        new QaCaseService(_factory, _tenant.Object, _access.Object, new RunningNumberService());

    private void BindTenant()
    {
        _tenant.Setup(x => x.TryCompanyScope()).Returns(_scope);
        _tenant.Setup(x => x.TryBranchScope()).Returns(_scope);
        _tenant.Setup(x => x.TryWriteScope()).Returns(_scope);
    }

    private static InventoryTenantScope Scope(string user) => new()
    {
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        UserId = user
    };

    private async Task SeedPoAsync(string poNo, short rel, short line, string item, decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.PoOrders.Add(new PoOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PoNo = poNo,
            PoRelNo = rel,
            Status = "OPEN",
            VendCode = "SUP01",
            Details =
            [
                new PoOrderDetail
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    PoNo = poNo,
                    PoRelNo = rel,
                    Line = line,
                    ICode = item,
                    PoQty = qty,
                    PurchaseUom = "EA",
                    StdUom = "EA"
                }
            ]
        });
        await db.SaveChangesAsync();
    }

    private async Task<long> SeedFgLotAsync(decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        var location = new ProductionLocation
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Code = "FG",
            Description = "FG staging"
        };
        db.ProductionLocations.Add(location);
        await db.SaveChangesAsync();
        var lot = new ProductionBalLot
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Kind = ProductionBalLotKinds.Wip,
            ItemCode = "FG1",
            Qty = qty,
            Uom = "EA",
            BaseQty = qty,
            BaseUom = "EA",
            ConversionFactorToBase = 1m,
            WorkOrderId = 1,
            WorkOrderNo = "WO-FG",
            WarehouseCode = "MAIN",
            LocationCode = "SITE",
            LotNo = "PROD-1",
            BalanceStage = "FG_STAGING",
            ProductionLocationId = location.Id,
            StockStatusCode = "AVAILABLE",
            OriginType = "PRODUCED"
        };
        db.ProductionBalLots.Add(lot);
        await db.SaveChangesAsync();
        return lot.Uid;
    }

    private static QaCaseSaveCommand GoodDraft() => new()
    {
        CaseDate = DateTime.Today,
        SourceType = QaSourceTypes.IncomingPreGrn,
        DeliveryNote = "DN-TEST",
        SourceDocNo = "DN-TEST",
        Lines =
        [
            new QaCaseLineSaveCommand
            {
                LineNo = 1,
                ItemCode = "FG1",
                ItemName = "Finished",
                Uom = "PCS",
                Qty = 5m
            }
        ]
    };

    private static QaReviewCommand Review(QaCaseDto value) => new()
    {
        CaseNo = value.CaseNo,
        RowVersion = value.RowVersion
    };

    private static QaInspectionSaveCommand PassInspection(QaCaseDto value) => new()
    {
        CaseNo = value.CaseNo,
        RowVersion = value.RowVersion,
        Lines =
        [
            new QaInspectionLineCommand
            {
                LineNo = 1,
                Result = QaInspectionResults.Pass,
                InspectedQty = 5m,
                Dispositions =
                [
                    new QaDispositionDto { Kind = QaDispositionKinds.Accept, Qty = 5m }
                ]
            }
        ]
    };
}
