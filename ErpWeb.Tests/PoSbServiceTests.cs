using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

/// <summary>Issues SBI/SBC/SBD numbers, one sequence per self-billed module.</summary>
internal sealed class FakePoSbDocumentNumberingService : IDocumentNumberingService
{
    private int _sbi;
    private int _sbc;
    private int _sbd;

    public Task<DocumentNumberResult> NextAsync(
        AppDbContext db,
        string module,
        string extraPrefix,
        DateTime documentDate,
        DocumentNumberRequestMode requestMode,
        string currentDocNo,
        CancellationToken ct)
    {
        if (requestMode == DocumentNumberRequestMode.Edit
            && !string.Equals(currentDocNo, "AUTO", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(currentDocNo))
        {
            return Task.FromResult(new DocumentNumberResult(currentDocNo.Trim(), null));
        }

        var mod = (module ?? string.Empty).Trim().ToUpperInvariant();
        return mod switch
        {
            "PO_SBC" => Task.FromResult(new DocumentNumberResult(
                $"SBC{documentDate:yyMM}-{Interlocked.Increment(ref _sbc):D4}", "SBC")),
            "PO_SBD" => Task.FromResult(new DocumentNumberResult(
                $"SBD{documentDate:yyMM}-{Interlocked.Increment(ref _sbd):D4}", "SBD")),
            _ => Task.FromResult(new DocumentNumberResult(
                $"SBI{documentDate:yyMM}-{Interlocked.Increment(ref _sbi):D4}", "SBI"))
        };
    }
}

/// <summary>
/// The self-billed purchase documents (SBI / SBC / SBD): save, post, the server-side e-Invoice lock,
/// derived header totals, and every origin rule for a note.
/// </summary>
public class PoSbServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 23);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly FakePoSbDocumentNumberingService _numbering = new();

    public PoSbServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();

        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "MYR", IsActive = true });
        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "USD", IsActive = true });

        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = "DEMO", TaxGrCode = "SR", TaxGrDesc = "Standard", Percentage = 6m, TaxGlCode = "GLTAX"
        });
        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = "DEMO", TaxGrCode = "ZR", TaxGrDesc = "Zero rated", Percentage = 0m, TaxGlCode = "GLTAX0"
        });

        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = "DEMO", SuppCode = "VEND01", SuppName = "Alpha Supplier",
            Currency = "MYR", TaxGrCode = "SR", IsActive = true
        });
        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = "DEMO", SuppCode = "VEND02", SuppName = "Beta Supplier",
            Currency = "USD", TaxGrCode = "ZR", IsActive = true
        });
        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = "DEMO", SuppCode = "VENDINACT", SuppName = "Inactive Supplier",
            Currency = "MYR", IsActive = false
        });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    // ─────────────────────────── Harness ───────────────────────────

    private PoSbInvoiceService CreateInvoiceSut(Mock<IAccessRightService>? access = null) =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "MAIN"),
            (access ?? Allowed()).Object,
            _numbering,
            new FixedCurrentDateService(FixedToday),
            NullLogger<PoSbInvoiceService>.Instance);

    private PoSbCdnService CreateCdnSut(Mock<IAccessRightService>? access = null) =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "MAIN"),
            (access ?? Allowed()).Object,
            _numbering,
            new FixedCurrentDateService(FixedToday),
            NullLogger<PoSbCdnService>.Instance);

    private static Mock<IAccessRightService> Allowed()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    private static Mock<IAccessRightService> AllowedOnlyFor(string menuCode)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                It.Is<string>(menu => menu == menuCode),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(
                It.Is<string>(menu => menu != menuCode),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        return access;
    }

    private static PoSbInvoiceSaveRequest InvoiceRequest(
        string vendor = "VEND01",
        string currency = "MYR",
        IReadOnlyList<PoSbLineRequest>? lines = null) => new()
    {
        DocDate = FixedToday,
        VendorCode = vendor,
        Currency = currency,
        CurrRate = 1m,
        Lines = lines ?? [Line()]
    };

    private static PoSbLineRequest Line(
        string iCode = "ITEM1",
        decimal qty = 10m,
        decimal unitPrice = 100m,
        string taxGroup = "SR",
        string classification = "022",
        string uom = "EA") => new()
    {
        ICode = iCode,
        IDesc = "Item " + iCode,
        Qty = qty,
        UnitPrice = unitPrice,
        StdUom = uom,
        TaxGroup = taxGroup,
        Classification = classification
    };

    /// <summary>Saves a self-billed invoice and returns its document number.</summary>
    private async Task<string> SaveInvoiceAsync(
        PoSbInvoiceService service,
        string vendor = "VEND01",
        IReadOnlyList<PoSbLineRequest>? lines = null,
        string currency = "MYR")
    {
        var saved = await service.SaveNewAsync(InvoiceRequest(vendor, currency, lines));
        Assert.True(saved.Succeeded, saved.Message);
        return saved.SavedDocNo!;
    }

    /// <summary>
    /// Saves a self-billed invoice and stamps its ERP <c>Status</c> and/or e-Invoice state directly. The
    /// ERP NEW/POSTED dimension is retired — there is no PostAsync any more — so a test that needs a
    /// legacy POSTED row or a submitted row writes the columns itself.
    /// </summary>
    private async Task<string> InvoiceWithStateAsync(
        PoSbInvoiceService service,
        string? irbmStatus = null,
        string? irbmUuid = null,
        string vendor = "VEND01",
        string currency = "MYR",
        string? erpStatus = null)
    {
        var docNo = await SaveInvoiceAsync(service, vendor, lines: null, currency: currency);

        if (irbmStatus is not null || erpStatus is not null)
        {
            await SetInvoiceEInvoiceStateAsync(docNo, irbmStatus, irbmUuid, erpStatus);
        }

        return docNo;
    }

    private async Task SetInvoiceEInvoiceStateAsync(
        string docNo, string? irbmStatus, string? irbmUuid, string? erpStatus = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var header = await db.PoSbInvoices.SingleAsync(x => x.DocNo == docNo);
        if (erpStatus is not null)
        {
            header.Status = erpStatus;
        }

        if (irbmStatus is not null)
        {
            header.IrbmStatus = irbmStatus;
            header.IrbmUuid = irbmUuid;
        }

        await db.SaveChangesAsync();
    }

    private async Task<PoSbInvoice> GetInvoiceAsync(string docNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.PoSbInvoices.AsNoTracking().SingleAsync(x => x.DocNo == docNo);
    }

    // ─────────────────────────── Invoice: totals ───────────────────────────

    [Fact]
    public async Task Header_totals_are_derived_from_the_lines()
    {
        var service = CreateInvoiceSut();

        var docNo = await SaveInvoiceAsync(service, lines:
        [
            Line(qty: 10m, unitPrice: 100m, taxGroup: "SR"),
            Line(iCode: "ITEM2", qty: 5m, unitPrice: 20m, taxGroup: "ZR")
        ]);

        var header = await GetInvoiceAsync(docNo);

        // 1000 + 60 tax  +  100 + 0 tax
        Assert.Equal(1100m, header.GrossAmnt);
        Assert.Equal(60m, header.Taxes);
        Assert.Equal(1160m, header.TotAmnt);

        // The identity the e-Invoice validator cross-checks (within 0.05) holds by construction.
        Assert.Equal(header.GrossAmnt + header.Taxes, header.TotAmnt);

        var doc = await service.GetAsync(docNo);
        Assert.True(doc.Succeeded);
        Assert.Equal(2, doc.Document!.Lines.Count);
        Assert.Equal(1000m, doc.Document.Lines[0].NetAmount);
        Assert.Equal(60m, doc.Document.Lines[0].TaxAmt);
        Assert.Equal(100m, doc.Document.Lines[1].NetAmount);
        Assert.Equal(0m, doc.Document.Lines[1].TaxAmt);
    }

    // ─────────────────────────── Invoice: the e-Invoice state is the ONLY gate ───────────────────────────

    /// <summary>
    /// The lifecycle contract: a document is editable/deletable while the e-Invoice status does not lock
    /// it. The ERP NEW/POSTED dimension is retired, so a legacy POSTED row is treated exactly like a NEW
    /// one — only SUBMITTING / SUBMITTED / VALID refuse.
    /// </summary>
    [Theory]
    [InlineData(PoSbStatuses.New, EInvoiceStatuses.Submitting, false)]
    [InlineData(PoSbStatuses.New, EInvoiceStatuses.Submitted, false)]
    [InlineData(PoSbStatuses.New, EInvoiceStatuses.Valid, false)]
    [InlineData(PoSbStatuses.New, null, true)]
    [InlineData(PoSbStatuses.New, EInvoiceStatuses.Invalid, true)]
    [InlineData(PoSbStatuses.New, EInvoiceStatuses.Rejected, true)]
    [InlineData(PoSbStatuses.New, EInvoiceStatuses.Cancelled, true)]
    [InlineData(PoSbStatuses.New, EInvoiceStatuses.Failed, true)]
    [InlineData(PoSbStatuses.Posted, EInvoiceStatuses.New, true)]      // legacy row: still editable
    [InlineData(PoSbStatuses.Posted, EInvoiceStatuses.Valid, false)]
    public async Task The_e_invoice_state_is_the_only_structural_gate(
        string erpStatus, string? irbmStatus, bool expectEditable)
    {
        var service = CreateInvoiceSut();
        var docNo = await InvoiceWithStateAsync(service, irbmStatus, "UUID-1", erpStatus: erpStatus);

        var loaded = await service.GetAsync(docNo);
        Assert.True(loaded.Succeeded);
        Assert.Equal(expectEditable, loaded.Document!.CanEdit);
        Assert.Equal(expectEditable, loaded.Document.CanDelete);
        Assert.Equal(expectEditable, !loaded.Document.IsEInvoiceLocked);

        var update = await service.UpdateAsync(docNo, InvoiceRequest());
        Assert.Equal(expectEditable, update.Succeeded);
        if (!expectEditable)
        {
            Assert.Contains("cannot be edited while its e-Invoice status is", update.Message);
        }
    }

    [Theory]
    [InlineData(EInvoiceStatuses.Invalid)]
    [InlineData(EInvoiceStatuses.Cancelled)]
    [InlineData(PoSbStatuses.Posted)]
    public async Task A_non_locked_document_can_be_deleted(string state)
    {
        var service = CreateInvoiceSut();
        var docNo = state == PoSbStatuses.Posted
            ? await InvoiceWithStateAsync(service, erpStatus: state)
            : await InvoiceWithStateAsync(service, state, "UUID-D");

        var delete = await service.DeleteAsync([new PoSbKeyedRequest { DocNo = docNo }]);

        Assert.True(delete.Succeeded, delete.Message);
    }

    [Fact]
    public async Task A_submitted_document_cannot_be_deleted()
    {
        var service = CreateInvoiceSut();
        var docNo = await InvoiceWithStateAsync(service, EInvoiceStatuses.Submitted, "UUID-3");

        var delete = await service.DeleteAsync([new PoSbKeyedRequest { DocNo = docNo }]);

        Assert.False(delete.Succeeded);
        Assert.Contains("cannot be deleted while its e-Invoice status is", delete.Message);
    }

    /// <summary>
    /// The service is authoritative: a page that loaded a NEW document must not be able to save over a
    /// submission that landed in between. The claim does NOT bump RowVersion, so the lock check — not the
    /// row version — is what refuses the write.
    /// </summary>
    [Fact]
    public async Task A_submission_between_load_and_save_is_refused_by_the_service()
    {
        var service = CreateInvoiceSut();
        var docNo = await SaveInvoiceAsync(service);

        // The page loads the document...
        var loadedByPage = await service.GetAsync(docNo);
        Assert.True(loadedByPage.Succeeded);
        Assert.True(loadedByPage.Document!.CanEdit);

        // ...another operator submits it...
        await SetInvoiceEInvoiceStateAsync(docNo, EInvoiceStatuses.Submitting, "UUID-RACE");

        // ...and the first operator saves.
        var update = await service.UpdateAsync(
            docNo,
            new PoSbInvoiceSaveRequest
            {
                DocDate = FixedToday,
                VendorCode = "VEND01",
                Currency = "MYR",
                CurrRate = 1m,
                Lines = [Line()],
                RowVersion = loadedByPage.Document.RowVersion
            });

        Assert.False(update.Succeeded);
        Assert.Equal(PoSbErrorKind.BusinessRule, update.ErrorKind);
        Assert.Contains("cannot be edited while its e-Invoice status is", update.Message);
    }

    [Fact]
    public async Task A_failed_e_invoice_does_not_lock_the_document()
    {
        var service = CreateInvoiceSut();
        var docNo = await InvoiceWithStateAsync(service, EInvoiceStatuses.Failed);

        // FAILED is not a locked status: the operator must be able to fix and resubmit.
        var update = await service.UpdateAsync(docNo, InvoiceRequest());

        Assert.True(update.Succeeded, update.Message);
    }

    // ─────────────────────────── Invoice: validation ───────────────────────────

    [Fact]
    public async Task A_line_without_a_classification_code_or_uom_is_refused()
    {
        var service = CreateInvoiceSut();

        var result = await service.SaveNewAsync(InvoiceRequest(lines:
        [
            new PoSbLineRequest
            {
                ICode = "ITEM1", IDesc = "Item", Qty = 1m, UnitPrice = 1m, TaxGroup = "SR",
                StdUom = null, Classification = null
            }
        ]));

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("Line1.Classification"));
        Assert.True(result.ValidationErrors.ContainsKey("Line1.Uom"));
    }

    [Fact]
    public async Task A_zero_quantity_line_is_refused()
    {
        var service = CreateInvoiceSut();

        var result = await service.SaveNewAsync(InvoiceRequest(lines: [Line(qty: 0m)]));

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("Line1.Qty"));
    }

    [Fact]
    public async Task An_unknown_tax_group_is_refused()
    {
        var service = CreateInvoiceSut();

        var result = await service.SaveNewAsync(InvoiceRequest(lines: [Line(taxGroup: "NOPE")]));

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("Line1.TaxGroup"));
    }

    [Fact]
    public async Task A_document_without_lines_is_refused()
    {
        var service = CreateInvoiceSut();

        var result = await service.SaveNewAsync(InvoiceRequest(lines: []));

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("Lines"));
    }

    [Fact]
    public async Task An_inactive_vendor_is_refused()
    {
        var service = CreateInvoiceSut();

        var result = await service.SaveNewAsync(InvoiceRequest(vendor: "VENDINACT"));

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("VendorCode"));
    }

    [Fact]
    public async Task A_batch_over_the_cap_is_refused()
    {
        var service = CreateInvoiceSut();
        var docNo = await SaveInvoiceAsync(service);

        // Delete is the only remaining batch action (post/rollback were retired).
        var result = await service.DeleteAsync(
        [
            new PoSbKeyedRequest { DocNo = docNo },
            new PoSbKeyedRequest { DocNo = docNo },
            new PoSbKeyedRequest { DocNo = docNo },
            new PoSbKeyedRequest { DocNo = docNo }
        ]);

        Assert.False(result.Succeeded);
        Assert.Contains("at most", result.Message);
    }

    // ─────────────────────────── Invoice: authorization + tenancy ───────────────────────────

    [Fact]
    public async Task Deleting_requires_the_delete_permission()
    {
        var service = CreateInvoiceSut();
        var docNo = await SaveInvoiceAsync(service);

        var denied = CreateInvoiceSut(Denied(PermissionCodes.Delete));
        var result = await denied.DeleteAsync([new PoSbKeyedRequest { DocNo = docNo }]);

        Assert.False(result.Succeeded);
        Assert.Equal(PoSbErrorKind.Authorization, result.ErrorKind);
    }

    private static Mock<IAccessRightService> Denied(string permissionCode)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                It.IsAny<string>(),
                It.Is<string>(p => p == permissionCode),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        access.Setup(x => x.CanAsync(
                It.IsAny<string>(),
                It.Is<string>(p => p != permissionCode),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    [Fact]
    public async Task A_document_from_another_company_is_not_found()
    {
        // A foreign-company row is inserted directly: the service must never see it, whatever its status.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PoSbInvoices.Add(new PoSbInvoice
            {
                CompanyCode = "OTHR",
                BranchCode = "HQ",
                DocNo = "SBI-FOREIGN",
                DocDate = FixedToday,
                Status = PoSbStatuses.New,
                VendorCode = "VEND01",
                Currency = "MYR",
                CurrRate = 1m,
                Details =
                {
                    new PoSbInvoiceDetail
                    {
                        CompanyCode = "OTHR",
                        BranchCode = "HQ",
                        DocNo = "SBI-FOREIGN",
                        Line = 1,
                        ICode = "ITEM1",
                        Qty = 1m,
                        UnitPrice = 1m,
                        Amount = 1m,
                        NetAmount = 1m
                    }
                }
            });
            await db.SaveChangesAsync();
        }

        var service = CreateInvoiceSut();
        var result = await service.GetAsync("SBI-FOREIGN");

        Assert.False(result.Succeeded);
        Assert.Equal(PoSbErrorKind.NotFound, result.ErrorKind);
    }

    [Fact]
    public async Task A_stale_row_version_is_reported_as_a_concurrency_failure()
    {
        var service = CreateInvoiceSut();
        var docNo = await SaveInvoiceAsync(service);

        var stale = InvoiceRequest();
        var request = new PoSbInvoiceSaveRequest
        {
            DocDate = stale.DocDate,
            VendorCode = stale.VendorCode,
            Currency = stale.Currency,
            CurrRate = stale.CurrRate,
            Lines = stale.Lines,
            RowVersion = [1, 2, 3, 4, 5, 6, 7, 9]
        };

        var result = await service.UpdateAsync(docNo, request);

        Assert.False(result.Succeeded);
        Assert.Equal(PoSbErrorKind.Concurrency, result.ErrorKind);
    }

    // ─────────────────────────── Notes: origin rules ───────────────────────────

    private async Task<PoSbCdnSaveRequest> CdnRequestAsync(
        string originDocNo,
        string type = PoSbTypes.CreditNote,
        string vendor = "VEND01",
        string? currency = null,
        IReadOnlyList<PoSbLineRequest>? lines = null) => new()
    {
        DocDate = FixedToday,
        Type = type,
        VendorCode = vendor,
        OriginSbInvNo = originDocNo,
        Currency = currency,
        CurrRate = 0m,
        Lines = lines ?? [Line(qty: 2m)]
    };

    private async Task<string> SaveCdnAsync(
        PoSbCdnService service,
        PoSbCdnSaveRequest request)
    {
        var saved = await service.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.Message);
        return saved.SavedDocNo!;
    }

    [Fact]
    public async Task A_credit_note_requires_an_origin()
    {
        var cdnService = CreateCdnSut();

        var result = await cdnService.SaveNewAsync(new PoSbCdnSaveRequest
        {
            DocDate = FixedToday,
            Type = PoSbTypes.CreditNote,
            VendorCode = "VEND01",
            Lines = [Line()]
        });

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("OriginSbInvNo"));
    }

    [Fact]
    public async Task An_unknown_origin_is_refused()
    {
        var cdnService = CreateCdnSut();

        var result = await cdnService.SaveNewAsync(
            await CdnRequestAsync("SBI-DOES-NOT-EXIST"));

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("OriginSbInvNo"));
    }

    [Fact]
    public async Task A_cancelled_origin_is_refused()
    {
        var invoiceService = CreateInvoiceSut();
        var origin = await InvoiceWithStateAsync(invoiceService, EInvoiceStatuses.Cancelled, "UUID-C");
        var cdnService = CreateCdnSut();

        var result = await cdnService.SaveNewAsync(await CdnRequestAsync(origin));

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("OriginSbInvNo"));
    }

    [Fact]
    public async Task A_note_may_be_drafted_while_its_origin_is_still_being_submitted()
    {
        // Deviation D-3: save time only requires the origin to EXIST; "VALID at MyInvois with a UUID" is
        // the payload build's rule, so a note can be prepared while the invoice is in flight.
        var invoiceService = CreateInvoiceSut();
        var origin = await InvoiceWithStateAsync(invoiceService, EInvoiceStatuses.Submitting, null);
        var cdnService = CreateCdnSut();

        var result = await cdnService.SaveNewAsync(await CdnRequestAsync(origin));

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task The_note_vendor_must_match_the_origin_vendor()
    {
        var invoiceService = CreateInvoiceSut();
        var origin = await InvoiceWithStateAsync(invoiceService, vendor: "VEND01");
        var cdnService = CreateCdnSut();

        var result = await cdnService.SaveNewAsync(
            await CdnRequestAsync(origin, vendor: "VEND02"));

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("VendorCode"));
    }

    [Fact]
    public async Task A_blank_note_currency_inherits_the_origins_currency_and_a_conflict_is_refused()
    {
        var invoiceService = CreateInvoiceSut();
        var origin = await InvoiceWithStateAsync(invoiceService, vendor: "VEND02", currency: "USD");
        var cdnService = CreateCdnSut();

        var inherited = await SaveCdnAsync(cdnService, await CdnRequestAsync(origin, vendor: "VEND02"));
        var doc = await cdnService.GetAsync(inherited);
        Assert.Equal("USD", doc.Document!.Currency);

        var conflict = await cdnService.SaveNewAsync(
            await CdnRequestAsync(origin, vendor: "VEND02", currency: "MYR"));
        Assert.False(conflict.Succeeded);
        Assert.True(conflict.ValidationErrors.ContainsKey("Currency"));
    }

    [Fact]
    public async Task The_origin_picker_lists_only_VALID_invoices_of_the_same_vendor()
    {
        var invoiceService = CreateInvoiceSut();

        var usable = await InvoiceWithStateAsync(invoiceService, EInvoiceStatuses.Valid, "UUID-VALID");
        var notYetValid = await InvoiceWithStateAsync(invoiceService, EInvoiceStatuses.Submitting, null);
        var otherVendor = await InvoiceWithStateAsync(
            invoiceService, EInvoiceStatuses.Valid, "UUID-OTHER", vendor: "VEND02", currency: "USD");

        var result = await invoiceService.SearchAsync(new PoSbQuery
        {
            VendorCode = "VEND01",
            IrbmStatus = EInvoiceStatuses.Valid,
            Take = 100
        });

        Assert.True(result.Succeeded, result.Message);
        var rows = result.List!.Rows;
        Assert.Contains(rows, x => x.DocNo == usable);
        Assert.DoesNotContain(rows, x => x.DocNo == notYetValid);
        Assert.DoesNotContain(rows, x => x.DocNo == otherVendor);

        // A legacy POSTED row that is VALID at MyInvois IS a usable origin: the ERP status is irrelevant.
        var legacy = await InvoiceWithStateAsync(
            invoiceService, EInvoiceStatuses.Valid, "UUID-LEGACY", erpStatus: PoSbStatuses.Posted);

        var afterLegacy = await invoiceService.SearchAsync(new PoSbQuery
        {
            VendorCode = "VEND01",
            IrbmStatus = EInvoiceStatuses.Valid,
            Take = 100
        });

        Assert.Contains(afterLegacy.List!.Rows, x => x.DocNo == legacy);
    }

    [Fact]
    public async Task The_note_type_cannot_be_changed_after_creation()
    {
        var invoiceService = CreateInvoiceSut();
        var origin = await InvoiceWithStateAsync(invoiceService);
        var cdnService = CreateCdnSut();
        var docNo = await SaveCdnAsync(cdnService, await CdnRequestAsync(origin));

        var request = await CdnRequestAsync(origin, type: PoSbTypes.DebitNote);
        var result = await cdnService.UpdateAsync(docNo, request);

        Assert.False(result.Succeeded);
        Assert.Contains("cannot be changed", result.Message);
    }

    [Fact]
    public async Task Credit_and_debit_notes_are_separate_rights()
    {
        var invoiceService = CreateInvoiceSut();
        var origin = await InvoiceWithStateAsync(invoiceService);

        // A user holding only the credit-note menu may save a CN but not a DN.
        var creditOnly = CreateCdnSut(AllowedOnlyFor(MenuCodes.PurchaseSbCreditNote));

        var credit = await creditOnly.SaveNewAsync(
            await CdnRequestAsync(origin, type: PoSbTypes.CreditNote));
        Assert.True(credit.Succeeded, credit.Message);

        var debit = await creditOnly.SaveNewAsync(
            await CdnRequestAsync(origin, type: PoSbTypes.DebitNote));
        Assert.False(debit.Succeeded);
        Assert.Equal(PoSbErrorKind.Authorization, debit.ErrorKind);
    }

    [Fact]
    public async Task A_debit_note_is_numbered_from_the_debit_note_module()
    {
        var invoiceService = CreateInvoiceSut();
        var origin = await InvoiceWithStateAsync(invoiceService);
        var cdnService = CreateCdnSut();

        var credit = await SaveCdnAsync(cdnService, await CdnRequestAsync(origin));
        var debit = await SaveCdnAsync(
            cdnService, await CdnRequestAsync(origin, type: PoSbTypes.DebitNote));

        Assert.StartsWith("SBC", credit);
        Assert.StartsWith("SBD", debit);
    }
}
