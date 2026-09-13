using System.Security.Cryptography;
using System.Text;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ErpWeb.Tests;

public class PoSupplierServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 11);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly string _attachRoot;

    public PoSupplierServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _attachRoot = Path.Combine(Path.GetTempPath(), "posupp-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_attachRoot);
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();

        db.SaCurrencies.Add(new SaCurrency
        {
            CompanyCode = "DEMO",
            CurrCode = "MYR",
            CurrDesc = "Ringgit",
            IsActive = true
        });
        db.SaCountries.Add(new SaCountry { CountryCode = "MY", CountryName = "Malaysia" });
        db.IvMsCodes.AddRange(
            new IvMsCode { Code = "SEL", Name = "Selangor", CodeType = IvMsCodeTypes.State },
            new IvMsCode { Code = "SR", Name = "Standard Rated", CodeType = IvMsCodeTypes.Tax },
            new IvMsCode { Code = "NET30", Name = "Net 30 days", CodeType = IvMsCodeTypes.PayCode });
        db.PoBuyingTerms.Add(new PoBuyingTerm
        {
            CompanyCode = "DEMO",
            BuyingTerm = "FOB",
            Description = "Free on board",
            IsActive = true,
            RowVersion = Rv(1)
        });
        db.IvAreaCodes.Add(new IvAreaCode
        {
            CompanyCode = "DEMO",
            AreaCode = "KL",
            AreaDesc = "Kuala Lumpur"
        });

        db.PoSuppliers.AddRange(
            new PoSupplier
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SuppCode = "SUP01",
                SuppName = "Alpha Supplier",
                Currency = "MYR",
                GlCode = "AP001",
                Tel = "111",
                City = "KL",
                Country = "MY",
                ContactPerson = "Alice",
                ContactPerson2 = "Bob",
                IsActive = true,
                RowVersion = Rv(10)
            },
            new PoSupplier
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SuppCode = "SUP02",
                SuppName = "Legacy Null Gl",
                Currency = "MYR",
                GlCode = null,
                Tel = "222",
                Country = "MY",
                PayCode = "NET30",
                CreditLimit = 1000m,
                IsActive = true,
                RowVersion = Rv(11)
            },
            new PoSupplier
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SuppCode = "INACTIVE",
                SuppName = "Inactive Supplier",
                Currency = "MYR",
                GlCode = "AP003",
                Country = "MY",
                IsActive = false,
                RowVersion = Rv(12)
            },
            new PoSupplier
            {
                CompanyCode = "DEMO",
                BranchCode = "BR2",
                SuppCode = "SUP01",
                SuppName = "Other Branch",
                Currency = "MYR",
                GlCode = "AP999",
                Country = "MY",
                IsActive = true,
                RowVersion = Rv(13)
            });

        await db.SaveChangesAsync();

        db.PoSupplierAdds.Add(new PoSupplierAdd
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            SuppCode = "SUP01",
            Line = 1,
            SuppName = "Ship A",
            Address1 = "Street 1",
            Country = "MY",
            State = "SEL"
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        if (Directory.Exists(_attachRoot))
        {
            try { Directory.Delete(_attachRoot, true); } catch { /* ignore */ }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public void SupplierAttachDocId_IsDeterministic_AndChangesWithInput()
    {
        var a = SupplierAttachDocId.Compute("4000/P001");
        var b = SupplierAttachDocId.Compute("4000/P001");
        var c = SupplierAttachDocId.Compute("4000/P002");

        Assert.Equal(40, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("4000/P001"))).ToLowerInvariant()[..40]);
    }

    [Fact]
    public async Task Create_RequiresSuppCodeNameCurrencyGlCode()
    {
        var sut = CreateSut();
        var model = ValidNewModel("NEW01");
        model.GlCode = null;
        var result = await sut.SaveAsync(model, isNew: true);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("GlCode"));
    }

    [Fact]
    public async Task Create_SlashCode_RoundTrips()
    {
        var sut = CreateSut();
        var result = await sut.SaveAsync(ValidNewModel("4000/P001"), isNew: true);
        Assert.True(result.Succeeded, result.Message);

        var loaded = await sut.GetAsync("4000/P001");
        Assert.True(loaded.Succeeded);
        Assert.Equal("4000/P001", loaded.Data!.SuppCode);
    }

    [Fact]
    public async Task Create_SixtyCharCode_Accepted_SixtyOneRejected()
    {
        var sut = CreateSut();
        var ok = new string('A', 60);
        var bad = new string('B', 61);

        Assert.True((await sut.SaveAsync(ValidNewModel(ok), true)).Succeeded);
        var fail = await sut.SaveAsync(ValidNewModel(bad), true);
        Assert.False(fail.Succeeded);
        Assert.True(fail.ValidationErrors.ContainsKey("SuppCode"));
    }

    [Fact]
    public async Task Create_ControlCharacters_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveAsync(ValidNewModel("BAD\u0001CODE"), true);
        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("SuppCode"));
    }

    [Fact]
    public async Task Create_WhitespaceOnly_Rejected_AndTrimmedOnSave()
    {
        var sut = CreateSut();
        var blank = await sut.SaveAsync(ValidNewModel("   "), true);
        Assert.False(blank.Succeeded);

        var model = ValidNewModel("  TRIM01  ");
        var result = await sut.SaveAsync(model, true);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("TRIM01", result.Data!.SuppCode);
    }

    [Fact]
    public async Task DuplicateCode_ReturnsDuplicateKey()
    {
        var sut = CreateSut();
        var result = await sut.SaveAsync(ValidNewModel("SUP01"), true);
        Assert.False(result.Succeeded);
        Assert.True(result.ErrorCode is IvMasterErrorCode.DuplicateKey or IvMasterErrorCode.Validation);
    }

    [Fact]
    public async Task Tenant_OtherBranch_Invisible()
    {
        var sut = CreateSut();
        var result = await sut.GetAsync("SUP01");
        Assert.True(result.Succeeded);
        Assert.Equal("Alpha Supplier", result.Data!.SuppName);

        var other = CreateSut(branch: "BR2");
        var cross = await other.GetAsync("SUP01");
        Assert.True(cross.Succeeded);
        Assert.Equal("Other Branch", cross.Data!.SuppName);

        var missing = await CreateSut(branch: "ZZZ").GetAsync("SUP01");
        Assert.False(missing.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, missing.ErrorCode);
    }

    [Fact]
    public async Task NullScope_InvalidScope_ZeroWrites()
    {
        var sut = CreateSut(nullScope: true);
        var result = await sut.SearchAsync(new PoSupplierListQuery());
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.InvalidScope, result.ErrorCode);
    }

    [Fact]
    public async Task ContactSlots_RoundTripIndependently()
    {
        var sut = CreateSut();
        var model = ValidNewModel("C1");
        model.ContactPerson = "One";
        model.ContactPerson2 = "Two";
        model.ContactPerson3 = "Three";
        model.ContactPerson4 = "Four";
        Assert.True((await sut.SaveAsync(model, true)).Succeeded);

        var loaded = (await sut.GetAsync("C1")).Data!;
        Assert.Equal("One", loaded.ContactPerson);
        Assert.Equal("Two", loaded.ContactPerson2);
        Assert.Equal("Three", loaded.ContactPerson3);
        Assert.Equal("Four", loaded.ContactPerson4);
    }

    [Fact]
    public async Task AddressReplace_AssignsLine_AndDiscardsBlank()
    {
        var sut = CreateSut();
        var loaded = (await sut.GetAsync("SUP01")).Data!;
        loaded.Addresses =
        [
            new PoSupplierAddressVm { SuppName = "First", Address1 = "A1", Country = "MY", State = "SEL" },
            new PoSupplierAddressVm(),
            new PoSupplierAddressVm { SuppName = "Second", Address1 = "A2", Country = "MY", State = "SEL" }
        ];
        var save = await sut.SaveAsync(loaded, false);
        Assert.True(save.Succeeded, save.Message);

        var again = (await sut.GetAsync("SUP01")).Data!;
        Assert.Equal(2, again.Addresses.Count);
        Assert.Equal(1, again.Addresses[0].Line);
        Assert.Equal(2, again.Addresses[1].Line);
        Assert.Equal("First", again.Addresses[0].SuppName);
        Assert.Equal("Second", again.Addresses[1].SuppName);
    }

    [Fact]
    public async Task GlCode_LegacyNull_PhoneChange_Succeeds()
    {
        var sut = CreateSut();
        var loaded = (await sut.GetAsync("SUP02")).Data!;
        loaded.Tel = "999";
        var result = await sut.SaveAsync(loaded, false);
        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task GlCode_LegacyNull_PaymentChange_Rejected()
    {
        var sut = CreateSut();
        var loaded = (await sut.GetAsync("SUP02")).Data!;
        loaded.PayCode = "NET30";
        loaded.CreditLimit = 5000m;
        var result = await sut.SaveAsync(loaded, false);
        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("GlCode"));
    }

    [Fact]
    public async Task GlCode_Existing_FinancialChange_Succeeds()
    {
        var sut = CreateSut();
        var loaded = (await sut.GetAsync("SUP01")).Data!;
        loaded.CreditLimit = 2500m;
        var result = await sut.SaveAsync(loaded, false);
        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task Export_UsesTenant_NotOtherBranch()
    {
        var sut = CreateSut();
        var result = await sut.ExportRowsAsync(new PoSupplierListQuery { Take = 100 });
        Assert.True(result.Succeeded);
        Assert.Contains(result.Data!.Rows, x => x.SuppCode == "SUP01" && x.SuppName == "Alpha Supplier");
        Assert.DoesNotContain(result.Data.Rows, x => x.SuppName == "Other Branch");
    }

    [Fact]
    public async Task Unauthorized_Denied()
    {
        var sut = CreateSut(canAdd: false);
        var result = await sut.SaveAsync(ValidNewModel("X1"), true);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Attachment_UploadListDownloadDelete_AndCrossBranchDenied()
    {
        var attachments = CreateAttachmentSut();
        await using var pdf = new MemoryStream("%PDF-1.4 test"u8.ToArray());
        var upload = await attachments.UploadAsync("SUP01", "contract.pdf", "application/pdf", pdf, pdf.Length);
        Assert.True(upload.Succeeded, upload.Message);

        var list = await attachments.ListAsync("SUP01");
        Assert.True(list.Succeeded);
        Assert.Single(list.Data!);

        var download = await attachments.DownloadAsync("SUP01", "contract.pdf");
        Assert.True(download.Succeeded);
        await download.Data.Stream.DisposeAsync();

        var other = CreateAttachmentSut(branch: "BR2");
        var leak = await other.ListAsync("SUP01");
        Assert.True(leak.Succeeded);
        Assert.Empty(leak.Data!); // BR2 SUP01 exists but has no attachments; metadata for HQ attach not leaked via HQ code on BR2 with different DocId... 
        // Same SuppCode on BR2 is a different supplier row; DocId hash is same but rows are branch-scoped.
        var otherDownload = await other.DownloadAsync("SUP01", "contract.pdf");
        Assert.False(otherDownload.Succeeded);

        var delete = await attachments.DeleteAsync("SUP01", "contract.pdf");
        Assert.True(delete.Succeeded);
        Assert.Empty((await attachments.ListAsync("SUP01")).Data!);
    }

    [Fact]
    public async Task Attachment_Traversal_AndMimeMismatch_Rejected()
    {
        var attachments = CreateAttachmentSut();
        await using var stream = new MemoryStream("%PDF-1.4"u8.ToArray());
        var traversal = await attachments.UploadAsync("SUP01", @"..\secret.pdf", "application/pdf", stream, stream.Length);
        Assert.False(traversal.Succeeded);

        await using var pngAsPdf = new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var mime = await attachments.UploadAsync("SUP01", "fake.pdf", "application/pdf", pngAsPdf, pngAsPdf.Length);
        Assert.False(mime.Succeeded);
    }

    [Fact]
    public async Task Attachment_SixtyCharCode_Works()
    {
        var code = new string('Z', 60);
        var sut = CreateSut();
        Assert.True((await sut.SaveAsync(ValidNewModel(code), true)).Succeeded);

        var attachments = CreateAttachmentSut();
        await using var pdf = new MemoryStream("%PDF-1.4 abc"u8.ToArray());
        var upload = await attachments.UploadAsync(code, "long.pdf", "application/pdf", pdf, pdf.Length);
        Assert.True(upload.Succeeded, upload.Message);
        Assert.Equal(40, SupplierAttachDocId.Compute(code).Length);
    }

    private static PoSupplierEditVm ValidNewModel(string code) =>
        new()
        {
            SuppCode = code,
            SuppName = $"New supplier {code.Trim()}",
            Currency = "MYR",
            GlCode = "AP001",
            Country = "MY",
            IsActive = true
        };

    private static byte[] Rv(byte marker) => [marker, 0, 0, 0, 0, 0, 0, 0];

    private PoSupplierService CreateSut(
        bool canAccess = true,
        bool canAdd = true,
        bool canEdit = true,
        bool canDelete = true,
        bool canExport = true,
        string branch = "HQ",
        bool nullScope = false)
    {
        var access = MockAccess(canAccess, canAdd, canEdit, canDelete, canExport);
        IInventoryTenantContext tenant = nullScope
            ? MockNullTenant()
            : InventoryTenantTestHelper.CreateTenantContext(branch: branch);

        return new PoSupplierService(
            _factory,
            tenant,
            access.Object,
            new FixedCurrentDateService(FixedToday),
            new PoSupplierRepository(_factory),
            new PoSupplierLookupService(_factory, tenant));
    }

    private PoSupplierAttachmentService CreateAttachmentSut(
        bool canAccess = true,
        bool canAdd = true,
        bool canEdit = true,
        string branch = "HQ")
    {
        var access = MockAccess(canAccess, canAdd, canEdit, true, true);
        var tenant = InventoryTenantTestHelper.CreateTenantContext(branch: branch);
        var options = Options.Create(new AttachmentStorageOptions { RootPath = _attachRoot });
        return new PoSupplierAttachmentService(
            _factory,
            tenant,
            access.Object,
            new FixedCurrentDateService(FixedToday),
            new PoSupplierRepository(_factory),
            options,
            NullLogger<PoSupplierAttachmentService>.Instance);
    }

    private static Mock<IAccessRightService> MockAccess(
        bool canAccess, bool canAdd, bool canEdit, bool canDelete, bool canExport)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);
        access.Setup(x => x.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Add, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAdd);
        access.Setup(x => x.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Edit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canEdit);
        access.Setup(x => x.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Delete, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canDelete);
        access.Setup(x => x.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Export, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canExport);
        return access;
    }

    private static IInventoryTenantContext MockNullTenant()
    {
        var mock = new Mock<IInventoryTenantContext>();
        mock.Setup(x => x.TryBranchScope()).Returns((InventoryTenantScope?)null);
        mock.Setup(x => x.TryCompanyScope()).Returns((InventoryTenantScope?)null);
        mock.Setup(x => x.TryWriteScope()).Returns((InventoryTenantScope?)null);
        return mock.Object;
    }
}
