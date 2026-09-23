using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.EInvoiceLib.Store;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Sales;
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

    /// <summary>
    /// Optional hook that runs on every authorization check. A test uses it to make the permission layer
    /// THROW, which is the realistic way an exception escapes the batch primitive: the per-document
    /// MyInvois calls already catch their own failures, so the refresh-all guard can only be pinned from
    /// outside that catch.
    /// </summary>
    public Action? OnPermissionCheck { get; set; }

    /// <summary>
    /// Permissions this host must refuse, regardless of <c>authorized</c>. Lets a test withhold one
    /// right (for example SUBMIT) while keeping the rest of the menu usable.
    /// </summary>
    public HashSet<string> DeniedPermissions { get; } = new(StringComparer.OrdinalIgnoreCase);

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
                host.OnPermissionCheck?.Invoke();
                return authorized && !host.DeniedPermissions.Contains(permission);
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
            new SaInvoiceRepository(),
            new SaCdnRepository(),
            _accessRights.Object,
            Secrets,
            Helper,
            _configuration,
            Options.Create(Settings),
            Validator,
            Mapper,
            NullLogger<SaEInvoiceService>.Instance);
    }

    /// <summary>
    /// Company e-Invoice profile complete enough to pass ERP validation for the invoice path.
    /// <para>
    /// <paramref name="eInvOnBehalfTin"/> matters: since the company profile gained
    /// <c>EInvOnBehalfTin</c>, THAT column is the supplier TIN the service validates and maps
    /// (<c>SaEInvoiceService.GetSupplierAsync</c>), so a company seeded without it is refused with
    /// "Supplier TIN is required" before any MyInvois call. Pass null to exercise that refusal.
    /// </para>
    /// </summary>
    public async Task SeedCompanyAsync(
        string? companyCode = null,
        bool enabled = true,
        string? state = "Selangor",
        string? country = "Malaysia",
        string? msic = "62010",
        string? phone = "0312345678",
        string? eInvOnBehalfTin = "C1234567890")
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
            Phone = phone,
            Email = "billing@demo.test",
            Address1 = "1 Jalan Demo",
            City = "Shah Alam",
            State = state,
            PostCode = "40100",
            Country = country,
            EInvEnabled = enabled,
            EInvMsicCode = msic,
            EInvBizDescription = "Software development",
            EInvSstNo = "A01-2345-67890123",
            EInvRegType = "BRN",
            EInvStateCode = "10",
            EInvCountryCode = "MYS",
            EInvDocumentVersion = "1.0",
            EInvOnBehalfTin = eInvOnBehalfTin,
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
        decimal? totAmnt = null,
        string? branchCode = null)
    {
        var code = companyCode ?? _company;
        await using var db = await Factory.CreateDbContextAsync();

        // Buyer e-Invoice identity is read live from the customer master (and frozen onto the document
        // only when a submission claims SUBMITTING), so every seeded invoice needs a matching customer
        // row. It is kept in step with buyerTin so a test can control what the master holds.
        var customer = await db.SaCusts
            .FirstOrDefaultAsync(x => x.CompanyCode == code && x.CustCode == "CUST01");
        if (customer is null)
        {
            customer = new SaCust
            {
                CompanyCode = code,
                CustCode = "CUST01",
                CustName = "Buyer Sdn Bhd"
            };
            db.SaCusts.Add(customer);
        }

        customer.TinNo = buyerTin;
        customer.CustBrn = "202201234567";
        customer.RegType = "BRN";
        customer.InvEmail = null;
        customer.Email = "ap@buyer.test";
        customer.GstregNo = "A01-2345-67890123";

        // The e-Invoice payload resolves the whole buyer block from the master (D-1), so the master
        // must carry the billing address and phone the seeded document used to supply from its own
        // snapshot. AppInvoice = true selects the main address over the Inv* overrides.
        customer.AppInvoice = true;
        customer.Address1 = "2 Jalan Buyer";
        customer.City = "Petaling Jaya";
        customer.State = "Selangor";
        customer.PostalCode = "47300";
        customer.Country = "Malaysia";
        customer.Tel = "0398765432";

        var invoice = BuildInvoice(
            code,
            branchCode ?? Branch,
            invNo ?? InvNo,
            status ?? "POSTED",
            irbmStatus,
            irbmOutcome,
            irbmUuid,
            irbmSubmitId,
            irbmValidOn,
            irbmSentOn,
            modifiedDate,
            validLines,
            buyerTin,
            totAmnt);

        db.SaInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice;
    }

    /// <summary>
    /// Seeds many <c>SUBMITTED</c> invoices in ONE context and one save.
    ///
    /// <para>
    /// <see cref="SeedInvoiceAsync"/> pays a context, a customer lookup and a save per row, which is fine
    /// for the three-or-four-row batch tests but far too slow to build the candidate counts the
    /// refresh-all contracts need (100+). The row shape comes from the same <see cref="BuildInvoice"/>
    /// factory, so a bulk seed cannot drift from a single one.
    /// </para>
    /// </summary>
    /// <param name="uuid">Per-index MyInvois UUID; null yields <c>UUID-{invNo}</c>.</param>
    public async Task SeedSubmittedInvoicesAsync(
        int count,
        Func<int, string> invNo,
        Func<int, string?>? uuid = null,
        string? companyCode = null,
        string? branchCode = null)
    {
        var code = companyCode ?? _company;
        var branch = branchCode ?? Branch;

        await using var db = await Factory.CreateDbContextAsync();
        for (var i = 1; i <= count; i++)
        {
            var no = invNo(i);
            db.SaInvoices.Add(BuildInvoice(
                code,
                branch,
                no,
                "POSTED",
                EInvoiceStatuses.Submitted,
                irbmOutcome: null,
                irbmUuid: uuid?.Invoke(i) ?? $"UUID-{no}",
                irbmSubmitId: null,
                irbmValidOn: null,
                irbmSentOn: null,
                modifiedDate: null,
                validLines: true,
                buyerTin: "C9876543210",
                totAmnt: null));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The ONE definition of the seeded-invoice row shape, shared by <see cref="SeedInvoiceAsync"/> and
    /// <see cref="SeedSubmittedInvoicesAsync"/>. A header plus its single valid line, buyer identity
    /// included, so the row passes the ERP pre-submit validation if a test ever submits it.
    /// </summary>
    private static SaInvoice BuildInvoice(
        string companyCode,
        string branchCode,
        string invNo,
        string status,
        string? irbmStatus,
        string? irbmOutcome,
        string? irbmUuid,
        string? irbmSubmitId,
        DateTime? irbmValidOn,
        DateTime? irbmSentOn,
        DateTime? modifiedDate,
        bool validLines,
        string? buyerTin,
        decimal? totAmnt)
    {
        var invoice = new SaInvoice
        {
            CompanyCode = companyCode,
            BranchCode = branchCode,
            InvNo = invNo,
            CustCode = "CUST01",
            InvDate = DateTime.UtcNow.Date,
            Status = status,
            DoNo = invNo,
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
            CompanyCode = companyCode,
            BranchCode = branchCode,
            InvNo = invNo,
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

        return invoice;
    }

    /// <summary>A CN whose origin invoice is <paramref name="originIrbmStatus"/>.</summary>
    public async Task<SaCdn> SeedCreditNoteAsync(
        string? docNo = null,
        string? type = "CN",
        string? originInvNo = null,
        string originIrbmStatus = EInvoiceStatuses.Valid,
        string? originUuid = "UUID-INV-1001",
        string? companyCode = null,
        string? irbmStatus = null,
        string? irbmUuid = null,
        string status = "POSTED")
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
        var cdn = BuildCreditNote(
            code,
            Branch,
            docNo ?? CdnNo,
            type ?? "CN",
            invNo,
            irbmStatus,
            irbmUuid,
            status);

        db.SaCdns.Add(cdn);
        await db.SaveChangesAsync();
        return cdn;
    }

    /// <summary>
    /// Seeds many <c>SUBMITTED</c> credit or debit notes in ONE context and one save.
    ///
    /// <para>
    /// Mirrors <see cref="SeedSubmittedInvoicesAsync"/> for the CN/DN refresh-all contracts: the per-row
    /// <see cref="SeedCreditNoteAsync"/> pays a context, an origin-invoice lookup and a save per row,
    /// which is far too slow for the 100/200-row candidate counts. The refresh-all candidate query reads
    /// only the header, so no origin invoice is seeded here — the row shape comes from the same
    /// <see cref="BuildCreditNote"/> factory the single-row seeder uses, so the two cannot drift.
    /// </para>
    /// </summary>
    /// <param name="uuid">Per-index MyInvois UUID; null yields <c>UUID-{docNo}</c>.</param>
    public async Task SeedSubmittedCreditNotesAsync(
        int count,
        Func<int, string> docNo,
        string type = "CN",
        Func<int, string?>? uuid = null,
        string? companyCode = null,
        string? branchCode = null)
    {
        var code = companyCode ?? _company;
        var branch = branchCode ?? Branch;

        await using var db = await Factory.CreateDbContextAsync();
        for (var i = 1; i <= count; i++)
        {
            var no = docNo(i);
            db.SaCdns.Add(BuildCreditNote(
                code,
                branch,
                no,
                type,
                string.Empty,
                EInvoiceStatuses.Submitted,
                uuid?.Invoke(i) ?? $"UUID-{no}"));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The ONE definition of the seeded credit/debit-note row shape, shared by
    /// <see cref="SeedCreditNoteAsync"/> and <see cref="SeedSubmittedCreditNotesAsync"/>.
    /// </summary>
    private static SaCdn BuildCreditNote(
        string companyCode,
        string branchCode,
        string docNo,
        string type,
        string originInvNo,
        string? irbmStatus,
        string? irbmUuid,
        string status = "POSTED")
    {
        var cdn = new SaCdn
        {
            CompanyCode = companyCode,
            BranchCode = branchCode,
            DocNo = docNo,
            DocDate = DateTime.UtcNow.Date,
            Status = status,
            Type = type,
            CustCode = "CUST01",
            CustName = "Buyer Sdn Bhd",
            // Production parity: SaCdnService writes InvNo only for a CREDIT note
            // (SaCdnService.cs ~L563 / ~L680 / ~L772), so a debit note never carries an origin
            // invoice number and must be seeded the same way or the tests would hide that gap.
            InvNo = string.Equals(type, SaCdnTypes.DebitNote, StringComparison.OrdinalIgnoreCase)
                ? null
                : originInvNo,
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
            IrbmStatus = irbmStatus,
            IrbmUuid = irbmUuid,
            ModifiedDate = DateTime.UtcNow,
            CreatedDate = DateTime.UtcNow
        };

        cdn.Details.Add(new SaCdnDetail
        {
            CompanyCode = companyCode,
            BranchCode = branchCode,
            DocNo = docNo,
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
    /// Mutates the customer master backing the seeded document. The e-Invoice payload resolves the whole
    /// buyer block from this row, so a test corrects the profile by writing here - the same action an
    /// operator takes when a submission was rejected for a bad TIN, address or telephone.
    /// </summary>
    public async Task UpdateCustomerAsync(
        Action<SaCust> change,
        string? custCode = null,
        string? companyCode = null)
    {
        var company = companyCode ?? _company;
        var code = custCode ?? "CUST01";
        await using var db = await Factory.CreateDbContextAsync();
        var customer = await db.SaCusts.FirstAsync(x => x.CompanyCode == company && x.CustCode == code);
        change(customer);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Simulates a second session touching the row while a submission is in flight. SQL Server bumps
    /// <c>RowVersion</c> on any update, so the in-flight save must lose the optimistic concurrency check.
    /// </summary>
    public async Task BumpInvoiceRowVersionAsync(string? invNo = null)
    {
        await using var db = await Factory.CreateDbContextAsync();

        if (db.Database.IsSqlServer())
        {
            // SQL Server OWNS a rowversion column: it is illegal to assign it, and it bumps on ANY update
            // that touches the row. A no-op self-assignment is therefore the provider-correct equivalent
            // of the SQLite branch below, which writes a random blob into the column. Using
            // `randomblob(8)` here (the previous code) threw "not a recognized built-in function name",
            // which surfaced as an MyInvois transport failure instead of a concurrency conflict.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE SaInvoice SET InvNo = InvNo WHERE CompanyCode = {0} AND InvNo = {1}",
                _company,
                invNo ?? InvNo);
            return;
        }

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

    public async Task<PoSbInvoice> GetSbInvoiceAsync(string? docNo = null)
    {
        var no = docNo ?? SbInvoiceNo;
        await using var db = await Factory.CreateDbContextAsync();
        return await db.PoSbInvoices.AsNoTracking()
            .SingleAsync(x => x.CompanyCode == _company && x.DocNo == no);
    }

    public async Task<PoSbCdn> GetSbCdnAsync(string? docNo = null)
    {
        var no = docNo ?? SbCdnNo;
        await using var db = await Factory.CreateDbContextAsync();
        return await db.PoSbCdns.AsNoTracking()
            .SingleAsync(x => x.CompanyCode == _company && x.DocNo == no);
    }

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
        // Self-billed documents are issued by the buyer, so they use their own Purchase menus.
        EInvoiceDocumentTypes.SelfBilledInvoice => MenuCodes.PurchaseSbInvoice,
        EInvoiceDocumentTypes.SelfBilledCreditNote => MenuCodes.PurchaseSbCreditNote,
        EInvoiceDocumentTypes.SelfBilledDebitNote => MenuCodes.PurchaseSbDebitNote,
        _ => MenuCodes.SalesInvoice
    };

    // ─────────────────── Self-billed purchase documents (LHDN 11/12/13) ───────────────────

    public const string SbInvoiceNo = "SBI-1001";
    public const string SbCdnNo = "SBC-1001";
    public const string VendorCode = "VEND01";

    public static SaEInvoiceDocumentKey SbInvoiceKey(string? docNo = null) => new()
    {
        DocumentType = EInvoiceDocumentTypes.SelfBilledInvoice,
        DocumentNo = docNo ?? SbInvoiceNo
    };

    public static SaEInvoiceDocumentKey SbCdnKey(
        string? docNo = null, string documentType = EInvoiceDocumentTypes.SelfBilledCreditNote) => new()
    {
        DocumentType = documentType,
        DocumentNo = docNo ?? SbCdnNo
    };

    /// <summary>
    /// A vendor master row complete enough to pass the self-billed supplier validation. Pass a blank
    /// <paramref name="tin"/>, an unusable <paramref name="phone"/>, or a blank
    /// <paramref name="msic"/>/<paramref name="bizDesc"/> to exercise the refusals: for a self-billed
    /// document the VENDOR is the payload's Supplier block.
    /// </summary>
    public async Task SeedVendorAsync(
        string? suppCode = null,
        string? tin = "C9876543210",
        string? phone = "0398765432",
        string? address1 = "3 Jalan Vendor",
        string? msic = "01234",
        string? bizDesc = "Wholesale of building materials",
        bool isActive = true,
        string? companyCode = null)
    {
        var code = companyCode ?? _company;
        await using var db = await Factory.CreateDbContextAsync();
        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = code,
            SuppCode = suppCode ?? VendorCode,
            SuppName = "Vendor Sdn Bhd",
            Currency = "MYR",
            TinNo = tin,
            RegType = "BRN",
            SupplierBrn = "202201234567",
            GstregNo = "A01-2345-67890123",
            MiscCode = msic,
            BizDesc = bizDesc,
            Address1 = address1,
            City = "Klang",
            State = "Selangor",
            StateCode = "10",
            PostalCode = "41000",
            Country = "Malaysia",
            CountryCode = "MYS",
            Tel = phone,
            Email = "ar@vendor.test",
            IsActive = isActive
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Rewrites the seeded vendor's name and TIN, simulating a vendor-master edit AFTER a document was
    /// built or accepted. Used to pin that an accepted self-billed payload can never be rebuilt.
    /// </summary>
    public async Task UpdateVendorAsync(
        string? name = null,
        string? tin = null,
        string? suppCode = null,
        string? companyCode = null)
    {
        var code = companyCode ?? _company;
        var vendorCode = suppCode ?? VendorCode;
        await using var db = await Factory.CreateDbContextAsync();
        var vendor = await db.PoSuppliers
            .FirstAsync(x => x.CompanyCode == code && x.SuppCode == vendorCode);

        if (name is not null)
        {
            vendor.SuppName = name;
        }

        if (tin is not null)
        {
            vendor.TinNo = tin;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>A POSTED self-billed invoice with one valid line, ready to be submitted.</summary>
    public async Task<PoSbInvoice> SeedSbInvoiceAsync(
        string? docNo = null,
        string status = "POSTED",
        string? irbmStatus = null,
        string? irbmOutcome = null,
        string? irbmUuid = null,
        string? vendorCode = null,
        bool validLines = true,
        bool includeDetails = true,
        string? companyCode = null,
        string? branchCode = null)
    {
        var company = companyCode ?? _company;
        var branch = branchCode ?? Branch;
        var no = docNo ?? SbInvoiceNo;

        await using var db = await Factory.CreateDbContextAsync();
        var invoice = BuildSbInvoice(company, branch, no, status, irbmStatus, irbmOutcome, irbmUuid,
            vendorCode ?? VendorCode, validLines, includeDetails);
        db.PoSbInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice;
    }

    /// <summary>
    /// A POSTED self-billed credit / debit note plus the self-billed invoice it references. The origin is
    /// seeded with <paramref name="originIrbmStatus"/> so the VALID-origin rule can be exercised from both
    /// sides. Pass <paramref name="irbmStatus"/> to seed the note's own e-Invoice state (the refresh-all
    /// candidate tests need <c>SUBMITTED</c>).
    /// </summary>
    public async Task<PoSbCdn> SeedSbCdnAsync(
        string? docNo = null,
        string type = "CN",
        string? originSbInvNo = null,
        string originIrbmStatus = EInvoiceStatuses.Valid,
        string? originUuid = "UUID-SBI-1001",
        string status = "POSTED",
        string? companyCode = null,
        string? branchCode = null,
        bool validLines = true,
        string? irbmStatus = null,
        string? irbmUuid = null)
    {
        var company = companyCode ?? _company;
        var branch = branchCode ?? Branch;
        var originNo = originSbInvNo ?? SbInvoiceNo;

        await using (var db = await Factory.CreateDbContextAsync())
        {
            db.PoSbInvoices.Add(BuildSbInvoice(
                company, branch, originNo, "POSTED", originIrbmStatus, null, originUuid,
                VendorCode, validLines: true, includeDetails: true));
            await db.SaveChangesAsync();
        }

        await using (var db = await Factory.CreateDbContextAsync())
        {
            var note = BuildSbCdn(
                company,
                branch,
                docNo ?? (string.Equals(type, "DN", StringComparison.OrdinalIgnoreCase)
                    ? "SBD-1001"
                    : SbCdnNo),
                type,
                originNo,
                status,
                validLines,
                irbmStatus,
                irbmUuid);

            db.PoSbCdns.Add(note);
            await db.SaveChangesAsync();
            return note;
        }
    }

    /// <summary>
    /// The ONE definition of the seeded self-billed credit/debit-note row shape, shared by
    /// <see cref="SeedSbCdnAsync"/> and the bulk <see cref="SeedSubmittedSbNotesAsync"/>.
    /// </summary>
    private static PoSbCdn BuildSbCdn(
        string companyCode,
        string branchCode,
        string docNo,
        string type,
        string originSbInvNo,
        string status,
        bool validLines,
        string? irbmStatus = null,
        string? irbmUuid = null)
    {
        var note = new PoSbCdn
        {
            CompanyCode = companyCode,
            BranchCode = branchCode,
            DocNo = docNo,
            DocDate = DateTime.UtcNow.Date,
            Status = status,
            Type = type,
            VendorCode = VendorCode,
            VendorName = "Vendor Sdn Bhd",
            OriginSbInvNo = originSbInvNo,
            Currency = "MYR",
            CurrRate = 1m,
            GrossAmnt = 50m,
            Taxes = 0m,
            TotAmnt = 50m,
            IrbmStatus = irbmStatus,
            IrbmUuid = irbmUuid,
            CreatedDate = DateTime.UtcNow
        };

        note.Details.Add(new PoSbCdnDetail
        {
            CompanyCode = companyCode,
            BranchCode = branchCode,
            DocNo = docNo,
            Line = 1,
            ICode = "ITM01",
            IDesc = "Adjustment",
            StdUom = "UNIT",
            Qty = 1m,
            UnitPrice = 50m,
            Amount = 50m,
            NetAmount = 50m,
            TaxGroup = "SR-8",
            TaxAmt = 0m,
            Classification = validLines ? "022" : null
        });

        return note;
    }

    /// <summary>
    /// Seeds many <c>SUBMITTED</c> self-billed invoices in ONE context and one save.
    ///
    /// <para>
    /// Mirrors <see cref="SeedSubmittedInvoicesAsync"/> for the self-billed refresh-all contracts:
    /// <see cref="SeedSbInvoiceAsync"/> pays a context and a save per row, far too slow for the 100/200-row
    /// candidate counts. The row shape comes from the same <see cref="BuildSbInvoice"/> factory, so the two
    /// cannot drift.
    /// </para>
    /// </summary>
    /// <param name="uuid">Per-index MyInvois UUID; null yields <c>UUID-{docNo}</c>.</param>
    public async Task SeedSubmittedSbInvoicesAsync(
        int count,
        Func<int, string> docNo,
        Func<int, string?>? uuid = null,
        string? companyCode = null,
        string? branchCode = null)
    {
        var code = companyCode ?? _company;
        var branch = branchCode ?? Branch;

        await using var db = await Factory.CreateDbContextAsync();
        for (var i = 1; i <= count; i++)
        {
            var no = docNo(i);
            db.PoSbInvoices.Add(BuildSbInvoice(
                code,
                branch,
                no,
                "POSTED",
                EInvoiceStatuses.Submitted,
                irbmOutcome: null,
                uuid?.Invoke(i) ?? $"UUID-{no}",
                VendorCode,
                validLines: true,
                includeDetails: false));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds many <c>SUBMITTED</c> self-billed credit or debit notes in ONE context and one save. The
    /// refresh-all candidate query reads only the header and only <c>SUBMITTED</c> rows, so neither an
    /// origin invoice nor details are seeded — but each note still carries an <c>OriginSbInvNo</c>, which is
    /// the shape the real rows have.
    /// </summary>
    /// <param name="uuid">Per-index MyInvois UUID; null yields <c>UUID-{docNo}</c>.</param>
    public async Task SeedSubmittedSbNotesAsync(
        int count,
        Func<int, string> docNo,
        string type = "CN",
        Func<int, string?>? uuid = null,
        string? companyCode = null,
        string? branchCode = null)
    {
        var code = companyCode ?? _company;
        var branch = branchCode ?? Branch;

        await using var db = await Factory.CreateDbContextAsync();
        for (var i = 1; i <= count; i++)
        {
            var no = docNo(i);
            db.PoSbCdns.Add(BuildSbCdn(
                code,
                branch,
                no,
                type,
                originSbInvNo: SbInvoiceNo,
                status: "POSTED",
                validLines: true,
                irbmStatus: EInvoiceStatuses.Submitted,
                irbmUuid: uuid?.Invoke(i) ?? $"UUID-{no}"));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>The ONE definition of the seeded self-billed invoice shape.</summary>
    private static PoSbInvoice BuildSbInvoice(
        string companyCode,
        string branchCode,
        string docNo,
        string status,
        string? irbmStatus,
        string? irbmOutcome,
        string? irbmUuid,
        string vendorCode,
        bool validLines,
        bool includeDetails)
    {
        var invoice = new PoSbInvoice
        {
            CompanyCode = companyCode,
            BranchCode = branchCode,
            DocNo = docNo,
            DocDate = DateTime.UtcNow.Date,
            Status = status,
            VendorCode = vendorCode,
            VendorName = "Vendor Sdn Bhd",
            Currency = "MYR",
            CurrRate = 1m,
            GrossAmnt = 100m,
            Taxes = 0m,
            TotAmnt = 100m,
            IrbmStatus = irbmStatus,
            IrbmOutcome = irbmOutcome,
            IrbmUuid = irbmUuid,
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };

        if (includeDetails)
        {
            invoice.Details.Add(new PoSbInvoiceDetail
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                DocNo = docNo,
                Line = 1,
                ICode = "ITM01",
                IDesc = "Consulting",
                StdUom = "UNIT",
                Qty = 1m,
                UnitPrice = 100m,
                Amount = 100m,
                NetAmount = 100m,
                TaxGroup = "SR-8",
                TaxAmt = 0m,
                Classification = validLines ? "022" : null
            });
        }

        return invoice;
    }

    public ValueTask DisposeAsync()
    {
        // Null for a host built over a caller-owned factory (the SQL Server concurrency suite), where
        // there is no SQLite connection to dispose. Disposing it unconditionally threw an NRE in
        // teardown, which reported the SQL Server tests as failed *after* they had done their work.
        _connection?.Dispose();
        return ValueTask.CompletedTask;
    }
}
