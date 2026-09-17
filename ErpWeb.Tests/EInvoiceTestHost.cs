using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.EInvoiceLib.Store;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// Fixture for the <see cref="SaEInvoiceService"/> lifecycle tests: a real SQLite database, the real
/// <see cref="EInvoiceValidator"/>, mapper and <see cref="ClientSecretStore"/>, and a scripted
/// <see cref="FakeSubmitDocumentHelper"/> standing in for MyInvois.
///
/// <para>
/// The database, the validator and the secret store are deliberately real. The behaviours under test —
/// the pre-submit gate, the recovery algorithm, per-company credentials — live in the interaction
/// between the façade, the persisted status columns and the scoped credential store, which a mocked
/// layer would hide.
/// </para>
/// </summary>
internal sealed class EInvoiceTestHost : IAsyncDisposable
{
    public const string Company = "DEMO";
    public const string Branch = "HQ";
    public const string InvNo = "INV-1001";
    public const string CdnNo = "CN-1001";

    private readonly SqliteConnection? _connection;
    private readonly Mock<ICurrentUserService> _currentUser;
    private readonly Mock<IAccessRightService> _accessRights;
    private readonly IConfiguration _configuration;

    private EInvoiceTestHost(
        SqliteConnection? connection,
        IDbContextFactory<AppDbContext> factory,
        Mock<ICurrentUserService> currentUser,
        Mock<IAccessRightService> accessRights,
        IConfiguration configuration,
        FakeSubmitDocumentHelper helper)
    {
        _connection = connection;
        _currentUser = currentUser;
        _accessRights = accessRights;
        _configuration = configuration;
        Helper = helper;

        Factory = factory;
        Validator = new EInvoiceValidator();
        Mapper = new EInvoiceDocumentMapper();
    }

    public IDbContextFactory<AppDbContext> Factory { get; }

    public FakeSubmitDocumentHelper Helper { get; }

    public EInvoiceValidator Validator { get; }

    public EInvoiceDocumentMapper Mapper { get; }

    /// <summary>
    /// The scoped credential store the façade applies the per-company credentials to. Exposed so the
    /// no-leak tests can assert which credentials the last operation ran with.
    /// </summary>
    public ClientSecretStore Secrets { get; private set; } = null!;

    /// <summary>Every menu code that <see cref="IAccessRightService"/> was asked about, in order.</summary>
    public List<(string Menu, string Permission)> PermissionChecks { get; } = [];

    public EInvoiceOptions Settings { get; set; } = new()
    {
        SubmittingStuckMinutes = 15,
        CancelWindowHours = 72,
        RecoverySearchDays = 30
    };

    public static EInvoiceTestHost Create(
        string company = Company,
        string branch = Branch,
        bool authorized = true,
        string? onBehalfTin = null,
        string? documentVersion = "1.0")
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        return Build(connection, factory, company, branch, authorized, onBehalfTin, documentVersion);
    }

    /// <summary>
    /// Builds a host over a database the caller owns. The SQL Server concurrency suite uses this so the
    /// same façade setup runs against a scratch SQL Server database, where <c>RowVersion</c> really bumps.
    /// </summary>
    public static EInvoiceTestHost CreateWithFactory(
        IDbContextFactory<AppDbContext> factory,
        string company = Company,
        string branch = Branch,
        bool authorized = true,
        string? onBehalfTin = null,
        string? documentVersion = "1.0") =>
        Build(null, factory, company, branch, authorized, onBehalfTin, documentVersion);

    private static EInvoiceTestHost Build(
        SqliteConnection? connection,
        IDbContextFactory<AppDbContext> factory,
        string company,
        string branch,
        bool authorized,
        string? onBehalfTin,
        string? documentVersion)
    {
        var current = new Mock<ICurrentUserService>();
        var activeCompany = company;
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.SubjectUid).Returns("1");
        current.SetupGet(x => x.UserId).Returns("admin");
        current.SetupGet(x => x.CompanyCode).Returns(() => activeCompany);
        current.SetupGet(x => x.BranchCode).Returns(branch);
        current.SetupGet(x => x.LocationCode).Returns("SITE");

        var rights = new Mock<IAccessRightService>();
        var host = new EInvoiceTestHost(
            connection,
            factory,
            current,
            rights,
            BuildConfiguration(onBehalfTin, documentVersion),
            new FakeSubmitDocumentHelper())
        {
            _company = company,
            ChangeCompany = value => activeCompany = value
        };

        rights
            .Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string menu, string permission, CancellationToken _) =>
            {
                host.PermissionChecks.Add((menu, permission));
                return authorized;
            });

        return host;
    }

    private string _company = Company;

    private Action<string> ChangeCompany { get; init; } = _ => { };

    /// <summary>Act as another company for the next operation (the SQLite database is shared).</summary>
    public void SwitchCompany(string companyCode)
    {
        _company = companyCode;
        ChangeCompany(companyCode);
    }

    private static IConfiguration BuildConfiguration(string? onBehalfTin, string? documentVersion) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Einvoice:EInv_Url"] = "https://preprod.myinvois.hasil.gov.my",
                ["Einvoice:EInv_SecretID"] = "app-secret-id",
                ["Einvoice:EInv_SecretKey"] = "app-secret-key",
                ["Einvoice:EInv_docversion"] = documentVersion,
                ["Einvoice:EInv_OnBehalfTin"] = onBehalfTin ?? string.Empty,
                ["Einvoice:SubmittingStuckMinutes"] = "15",
                ["Einvoice:CancelWindowHours"] = "72",
                // Each company may override its own credentials; nothing here may leak between them.
                ["Einvoice:Companies:OTHER:EInv_SecretID"] = "other-secret-id",
                ["Einvoice:Companies:OTHER:EInv_SecretKey"] = "other-secret-key"
            })
            .Build();

    /// <summary>The healthiest possible façade: the production class with real collaborators.</summary>
    public SaEInvoiceService CreateService()
    {
        Secrets = new ClientSecretStore(NullLogger<ClientSecretStore>.Instance, _configuration);

        return new SaEInvoiceService(
            Factory,
            new TenantScopeContext(_currentUser.Object),
            _accessRights.Object,
            Secrets,
            Helper,
            _configuration,
            Options.Create(Settings),
            Validator,
            Mapper,
            NullLogger<SaEInvoiceService>.Instance);
    }

    /// <summary>Company e-Invoice profile complete enough to pass ERP validation for the invoice path.</summary>
    public async Task SeedCompanyAsync(
        string? companyCode = null,
        bool enabled = true,
        string? state = "Selangor",
        string? country = "Malaysia")
    {
        var code = companyCode ?? _company;
        await using var db = await Factory.CreateDbContextAsync();
        db.Companies.Add(new Company
        {
            CompanyCode = code,
            CompanyName = $"Demo {code} Sdn Bhd",
            LegalName = "Demo Sdn Bhd",
            RegistrationNo = "202301234567",
            TaxNo = "C1234567890",
            Phone = "0312345678",
            Email = "billing@demo.test",
            Address1 = "1 Jalan Demo",
            City = "Shah Alam",
            State = state,
            PostCode = "40100",
            Country = country,
            EInvEnabled = enabled,
            EInvMsicCode = "62010",
            EInvBizDescription = "Software development",
            EInvSstNo = "A01-2345-67890123",
            EInvRegType = "BRN",
            EInvStateCode = "10",
            EInvCountryCode = "MYS",
            EInvDocumentVersion = "1.0",
            IsActive = true
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A posted invoice that satisfies every ERP validation rule, so a test reaches the MyInvois call.
    /// Pass <paramref name="validLines"/> = false to seed a line the validator must reject.
    /// </summary>
    public async Task<SaInvoice> SeedInvoiceAsync(
        string? invNo = null,
        string? status = null,
        string? irbmStatus = null,
        string? irbmOutcome = null,
        string? irbmUuid = null,
        string? irbmSubmitId = null,
        DateTime? irbmValidOn = null,
        DateTime? irbmSentOn = null,
        DateTime? modifiedDate = null,
        bool validLines = true,
        string? buyerTin = "C9876543210",
        string? companyCode = null,
        decimal? totAmnt = null)
    {
        var code = companyCode ?? _company;
        await using var db = await Factory.CreateDbContextAsync();

        var invoice = new SaInvoice
        {
            CompanyCode = code,
            BranchCode = Branch,
            InvNo = invNo ?? InvNo,
            CustCode = "CUST01",
            InvDate = DateTime.UtcNow.Date,
            Status = status ?? "POSTED",
            DoNo = invNo ?? InvNo,
            Currency = "MYR",
            CurrRate = 1m,
            GrossAmnt = 100m,
            Taxes = 8m,
            TotAmnt = totAmnt ?? 108m,
            CustName = "Buyer Sdn Bhd",
            InvName = "Buyer Sdn Bhd",
            InvAddress1 = "2 Jalan Buyer",
            InvCity = "Petaling Jaya",
            InvState = "Selangor",
            InvPostalCode = "47300",
            InvCountry = "Malaysia",
            InvTel = "0398765432",
            InvEmail = "ap@buyer.test",
            BuyerTin = buyerTin,
            BuyerBrn = "202201234567",
            BuyerRegType = "BRN",
            IrbmStatus = irbmStatus,
            IrbmOutcome = irbmOutcome,
            IrbmUuid = irbmUuid,
            IrbmSubmitId = irbmSubmitId,
            IrbmValidOn = irbmValidOn,
            IrbmSentOn = irbmSentOn,
            ModifiedDate = modifiedDate ?? DateTime.UtcNow,
            CreatedDate = DateTime.UtcNow
        };

        invoice.Details.Add(new SaInvoiceDetail
        {
            CompanyCode = code,
            BranchCode = Branch,
            InvNo = invoice.InvNo,
            Line = 1,
            ICode = "ITM01",
            IDesc = "Consulting",
            StdUom = "UNIT",
            Qty = 1m,
            StdQty = 1m,
            UnitPrice = 100m,
            Amount = 100m,
            NetAmount = 100m,
            TaxGrCode = validLines ? "SR-8" : null,
            TaxAmt = 8m,
            Classification = validLines ? "022" : null
        });

        db.SaInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice;
    }

    /// <summary>A CN whose origin invoice is <paramref name="originIrbmStatus"/>.</summary>
    public async Task<SaCdn> SeedCreditNoteAsync(
        string? docNo = null,
        string? type = "CN",
        string? originInvNo = null,
        string originIrbmStatus = EInvoiceStatuses.Valid,
        string? originUuid = "UUID-INV-1001",
        string? companyCode = null)
    {
        var code = companyCode ?? _company;
        var invNo = originInvNo ?? InvNo;
        var invoice = await GetInvoiceAsync(code, invNo);
        if (invoice is null)
        {
            await SeedInvoiceAsync(invNo, irbmStatus: originIrbmStatus, irbmUuid: originUuid, companyCode: code);
        }
        else
        {
            await UpdateInvoiceAsync(invNo, x =>
            {
                x.IrbmStatus = originIrbmStatus;
                x.IrbmUuid = originUuid;
            }, code);
        }

        await using var db = await Factory.CreateDbContextAsync();
        var cdn = new SaCdn
        {
            CompanyCode = code,
            BranchCode = Branch,
            DocNo = docNo ?? CdnNo,
            DocDate = DateTime.UtcNow.Date,
            Status = "POSTED",
            Type = type ?? "CN",
            CustCode = "CUST01",
            CustName = "Buyer Sdn Bhd",
            InvNo = invNo,
            InvAddress1 = "2 Jalan Buyer",
            City = "Petaling Jaya",
            State = "Selangor",
            PostalCode = "47300",
            Country = "Malaysia",
            Tel = "0398765432",
            Currency = "MYR",
            CurrRate = 1m,
            GrossAmnt = 50m,
            Taxes = 4m,
            TotAmnt = 54m,
            ModifiedDate = DateTime.UtcNow,
            CreatedDate = DateTime.UtcNow
        };

        cdn.Details.Add(new SaCdnDetail
        {
            CompanyCode = code,
            BranchCode = Branch,
            DocNo = cdn.DocNo,
            Line = 1,
            ICode = "ITM01",
            IDesc = "Consulting",
            StdUom = "UNIT",
            Qty = 1m,
            UnitPrice = 50m,
            Amount = 50m,
            NetAmount = 50m,
            TaxGroup = "SR-8",
            TaxAmt = 4m,
            Classification = "022"
        });

        db.SaCdns.Add(cdn);
        await db.SaveChangesAsync();
        return cdn;
    }

    public async Task<SaInvoice?> GetInvoiceAsync(string? companyCode = null, string? invNo = null)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var company = companyCode ?? _company;
        var no = invNo ?? InvNo;
        return await db.SaInvoices.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.InvNo == no);
    }

    public async Task<SaCdn?> GetCdnAsync(string? docNo = null, string? companyCode = null)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var company = companyCode ?? _company;
        var no = docNo ?? CdnNo;
        return await db.SaCdns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.DocNo == no);
    }

    public async Task UpdateInvoiceAsync(string invNo, Action<SaInvoice> change, string? companyCode = null)
    {
        var company = companyCode ?? _company;
        await using var db = await Factory.CreateDbContextAsync();
        var invoice = await db.SaInvoices.FirstAsync(x => x.CompanyCode == company && x.InvNo == invNo);
        change(invoice);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Simulates a second session touching the row while a submission is in flight. SQL Server bumps
    /// <c>RowVersion</c> on any update, so the in-flight save must lose the optimistic concurrency check.
    /// </summary>
    public async Task BumpInvoiceRowVersionAsync(string? invNo = null)
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE SaInvoice SET RowVersion = randomblob(8) WHERE CompanyCode = {0} AND InvNo = {1}",
            _company,
            invNo ?? InvNo);
    }

    /// <summary>
    /// Simulates the document being removed by another session while a submission is in flight. The
    /// in-flight write then matches no rows, which is the provider-independent way to surface an
    /// optimistic concurrency failure (SQLite does not enforce a <c>RowVersion</c> WHERE clause).
    /// </summary>
    public async Task DeleteInvoiceAsync(string? invNo = null)
    {
        var no = invNo ?? InvNo;
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM SaInvoiceDetail WHERE CompanyCode = {0} AND InvNo = {1}", _company, no);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM SaInvoice WHERE CompanyCode = {0} AND InvNo = {1}", _company, no);
    }

    public async Task<List<SaEInvoiceLog>> LogsAsync(
        string documentNo,
        string documentType = EInvoiceDocumentTypes.Invoice,
        string? companyCode = null)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var company = companyCode ?? _company;
        return await db.SaEInvoiceLogs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.DocumentType == documentType && x.DocumentNo == documentNo)
            .OrderBy(x => x.Id)
            .ToListAsync();
    }

    public static SaEInvoiceDocumentKey InvoiceKey(string? invNo = null) => new()
    {
        DocumentType = EInvoiceDocumentTypes.Invoice,
        DocumentNo = invNo ?? InvNo
    };

    public static SaEInvoiceDocumentKey CdnKey(string? docNo = null, string documentType = EInvoiceDocumentTypes.CreditNote) => new()
    {
        DocumentType = documentType,
        DocumentNo = docNo ?? CdnNo
    };

    /// <summary>The documented menu an e-Invoice action must be authorized against.</summary>
    public static string ExpectedMenu(string documentType) => documentType switch
    {
        EInvoiceDocumentTypes.CreditNote => MenuCodes.SalesCreditNote,
        EInvoiceDocumentTypes.DebitNote => MenuCodes.SalesDebitNote,
        _ => MenuCodes.SalesInvoice
    };

    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }
}
