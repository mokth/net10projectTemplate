using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Quality;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Quality;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Quality;

[Trait(TestCategories.Name, TestCategories.Quality)]
public class QaInquiryTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly Mock<IInventoryTenantContext> _tenant = new();
    private readonly Mock<IAccessRightService> _access = new();
    private InventoryTenantScope _scope = Scope("DEMO", "HQ", "tester");

    public QaInquiryTests()
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
    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Inquiry_pages_without_loading_every_case_graph()
    {
        for (var i = 1; i <= 12; i++)
        {
            await SeedCaseAsync($"QC{i:D8}", QaCaseStatuses.Approved, item: "RM1", reject: 0m);
        }

        var page = await CreateCases().GetInquiryAsync(new QaInquiryQuery { Skip = 0, Take = 5 });
        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.Equal(12, page.InquiryPage!.TotalCount);
        Assert.Equal(12, page.InquiryPage.CaseCount);
        Assert.Equal(5, page.InquiryPage.Rows.Count);
        Assert.Equal(12, page.InquiryPage.OpenCaseCount);
        Assert.Equal(12, page.InquiryPage.ApprovedWithoutRejectCount);
    }

    [Fact]
    public async Task Filtered_kpis_match_the_filtered_set_not_the_page()
    {
        await SeedCaseAsync("QC00000001", QaCaseStatuses.PendingReview, item: "RM1", reject: 0m);
        await SeedCaseAsync("QC00000002", QaCaseStatuses.PendingReview, item: "RM1", reject: 0m);
        await SeedCaseAsync("QC00000003", QaCaseStatuses.Approved, item: "RM1", reject: 4m);
        await SeedCaseAsync("QC00000004", QaCaseStatuses.InInspection, item: "RM2", reject: 0m);

        var result = await CreateCases().GetInquiryAsync(new QaInquiryQuery
        {
            ItemCode = "RM1",
            Skip = 0,
            Take = 1
        });
        Assert.True(result.Succeeded, result.ErrorMessage);
        var page = result.InquiryPage!;
        Assert.Equal(3, page.TotalCount);
        Assert.Single(page.Rows);
        Assert.Equal(2, page.PendingReviewCount);
        Assert.Equal(4m, page.RejectedDecisionQty);
        Assert.Equal(0, page.ApprovedWithoutRejectCount);
        Assert.Equal(QaCaseMessages.ApprovedWithoutRejectNote, page.FirstPassYieldNote);
        Assert.Contains("not a true first-pass yield", page.FirstPassYieldNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no automatic stock posting", page.RecordOnlyNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inquiry_stays_inside_the_caller_company_and_branch()
    {
        await SeedCaseAsync("QC-DEMO", QaCaseStatuses.Approved, item: "RM1", reject: 0m);
        await SeedCaseAsync("QC-OTHER", QaCaseStatuses.Approved, item: "RM1", reject: 0m, company: "OTH", branch: "BR2");

        var result = await CreateCases().GetInquiryAsync(new QaInquiryQuery());
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal("QC-DEMO", Assert.Single(result.InquiryPage!.Rows).CaseNo);
        Assert.Equal(1, result.InquiryPage.TotalCount);
    }

    [Fact]
    public async Task Workload_uses_workbench_access_and_does_not_change_stock()
    {
        await SeedCaseAsync("QC00000001", QaCaseStatuses.PendingReview, item: "RM1", reject: 0m);
        await SeedCaseAsync("QC00000002", QaCaseStatuses.Submitted, item: "RM1", reject: 0m);

        var result = await CreateCases().GetWorkloadAsync();
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(2, result.InquiryPage!.OpenCaseCount);
        Assert.Equal(1, result.InquiryPage.PendingReviewCount);
        Assert.Equal(1, result.InquiryPage.PendingInspectionCount);
        Assert.Empty(result.InquiryPage.Rows);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.IvBalLocs.CountAsync());
        Assert.Equal(0, await db.IvTrxBatches.CountAsync());
    }

    [Fact]
    public async Task Export_requires_export_permission_and_includes_the_record_only_note()
    {
        await SeedCaseAsync("QC00000001", QaCaseStatuses.Approved, item: "RM1", reject: 2m);
        var denied = CreateCases();
        _access
            .Setup(x => x.CanAsync(MenuCodes.QualityInquiry, PermissionCodes.Export, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var blocked = await denied.ExportInquiryAsync(new QaInquiryQuery());
        Assert.False(blocked.Succeeded);
        Assert.Equal("Not authorized.", blocked.ErrorMessage);

        _access
            .Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var exported = await CreateCases().ExportInquiryAsync(new QaInquiryQuery());
        Assert.True(exported.Succeeded, exported.ErrorMessage);
        Assert.NotNull(exported.FileBytes);
        Assert.EndsWith(".csv", exported.FileName);
        var csv = System.Text.Encoding.UTF8.GetString(exported.FileBytes!);
        Assert.Contains("QA records — no automatic stock posting", csv);
        Assert.Contains("QC00000001", csv);
        Assert.DoesNotContain("CostLayer", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profit", csv, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Closed_case_days_are_computed_from_header_dates()
    {
        await SeedClosedAsync("QC-CLOSE", createdDaysAgo: 8, closedDaysAgo: 2);
        var result = await CreateCases().GetInquiryAsync(new QaInquiryQuery());
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(6m, result.InquiryPage!.AverageCloseDays);
        Assert.Equal(6m, result.InquiryPage.MedianCloseDays);
    }

    private IQaCaseService CreateCases() =>
        new QaCaseService(_factory, _tenant.Object, _access.Object, new RunningNumberService());

    private void BindTenant()
    {
        _tenant.Setup(x => x.TryCompanyScope()).Returns(_scope);
        _tenant.Setup(x => x.TryBranchScope()).Returns(_scope);
        _tenant.Setup(x => x.TryWriteScope()).Returns(_scope);
    }

    private static InventoryTenantScope Scope(string company, string branch, string user) => new()
    {
        CompanyCode = company,
        BranchCode = branch,
        UserId = user
    };

    private async Task SeedClosedAsync(string caseNo, int createdDaysAgo, int closedDaysAgo)
    {
        await SeedCaseAsync(
            caseNo,
            QaCaseStatuses.Closed,
            item: "RM1",
            reject: 0m,
            created: DateTime.Today.AddDays(-createdDaysAgo),
            modified: DateTime.Today.AddDays(-closedDaysAgo));
    }

    private async Task SeedCaseAsync(
        string caseNo,
        string status,
        string item,
        decimal reject,
        string company = "DEMO",
        string branch = "HQ",
        DateTime? created = null,
        DateTime? modified = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var entity = new QaCase
        {
            CompanyCode = company,
            BranchCode = branch,
            CaseNo = caseNo,
            CaseDate = DateTime.Today.AddDays(-2),
            Status = status,
            SourceType = QaSourceTypes.IncomingPreGrn,
            EligibilityNote = QaEligibilityNotes.RecordOnly,
            InspectedAtUtc = DateTime.UtcNow,
            InspectedBy = "tester",
            CreatedDate = created ?? DateTime.UtcNow,
            CreatedBy = "tester",
            ModifiedDate = modified
        };
        var line = new QaCaseLine
        {
            LineNo = 1,
            ItemCode = item,
            Uom = "PCS",
            Qty = 10m,
            InspectedQty = 10m
        };
        var accept = new QaDispositionLine
        {
            Case = entity,
            CaseLine = line,
            Kind = QaDispositionKinds.Accept,
            Qty = 10m - reject
        };
        var rejectRow = new QaDispositionLine
        {
            Case = entity,
            CaseLine = line,
            Kind = QaDispositionKinds.Reject,
            Qty = reject
        };
        line.Dispositions.Add(accept);
        entity.Dispositions.Add(accept);
        if (reject > 0m)
        {
            line.Dispositions.Add(rejectRow);
            entity.Dispositions.Add(rejectRow);
        }

        entity.Lines.Add(line);
        db.QaCases.Add(entity);
        await db.SaveChangesAsync();
    }
}
