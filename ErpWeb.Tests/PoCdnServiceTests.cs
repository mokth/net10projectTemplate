using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

/// <summary>Issues PCN/PDN document numbers; anything else gets PINV so invoice seeding works too.</summary>
internal sealed class FakePoCdnDocumentNumberingService : IDocumentNumberingService
{
    private int _pcn;
    private int _pdn;
    private int _other;

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
        if (mod == "PCN")
        {
            var n = Interlocked.Increment(ref _pcn);
            return Task.FromResult(new DocumentNumberResult($"PCN{documentDate:yy}{documentDate:MM}-{n:D4}", "PCN"));
        }

        if (mod == "PDN")
        {
            var n = Interlocked.Increment(ref _pdn);
            return Task.FromResult(new DocumentNumberResult($"PDN{documentDate:yy}{documentDate:MM}-{n:D4}", "PDN"));
        }

        var o = Interlocked.Increment(ref _other);
        return Task.FromResult(new DocumentNumberResult($"PINV{documentDate:yy}{documentDate:MM}-{o:D4}", "PINV"));
    }
}

public class PoCdnServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 2);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly FakePoCdnDocumentNumberingService _numbering = new();

    public PoCdnServiceTests()
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

        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", IsActive = true
        });
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", LocCode = "BIN1", IsActive = true
        });
        db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", IsActive = true });

        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "MYR", IsActive = true });
        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "USD", IsActive = true });
        db.SaCurrRates.Add(new SaCurrRate
        {
            CurrCode = "USD",
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2026, 12, 31),
            HomeCurPerUnit = 4.5,
            Status = true
        });

        db.SaPaymentTerms.Add(new SaPaymentTerm
        {
            CompanyCode = "DEMO", PayCode = "NET30", PayDesc = "Net 30", Days = 30, IsActive = true
        });
        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = "DEMO", TaxGrCode = "SR", TaxGrDesc = "Standard", Percentage = 6m, TaxGlCode = "GLTAX"
        });

        // Vendors: one healthy, one suspended, one inactive, one alternate.
        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = "DEMO", SuppCode = "VEND01", SuppName = "Alpha Supplier",
            Currency = "MYR", PayCode = "NET30", TaxGrCode = "SR",
            Address1 = "A1", City = "City", State = "State", PostalCode = "50000",
            Country = "MY", Tel = "011", Fax = "011", IsActive = true, Suspend = false
        });
        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = "DEMO", SuppCode = "VENDSUS", SuppName = "Suspended Supplier",
            Currency = "MYR", IsActive = true, Suspend = true
        });
        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = "DEMO", SuppCode = "VENDINACT", SuppName = "Inactive Supplier",
            Currency = "MYR", IsActive = false
        });
        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = "DEMO", SuppCode = "VEND02", SuppName = "Beta Supplier",
            Currency = "MYR", IsActive = true, Suspend = false
        });

        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO", ICode = "ITEM1", IDesc = "Stock item",
            IType = "STOCK", StdUom = "EA", PurUom = "EA",
            StockControl = true, LotControl = false, IsActive = true,
            PurStdPackSize = 1m, PurchasePrice = 100m,
            PurchaseGlCode = "GLITEM1", PurchaseTaxGroup = "SR"
        });

        // A posted purchase invoice with one line: 10 units at 100 = 1000.
        var invoice = new PoInvoice
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            DocNo = "PINV01",
            DocDate = new DateTime(2026, 8, 1),
            Status = "POSTED",
            Type = "INV",
            VendorCode = "VEND01",
            VendorName = "Alpha Supplier",
            Currency = "MYR",
            CurrRate = 1m,
            GrossAmnt = 1000m,
            Taxes = 60m,
            TotAmnt = 1060m,
            RowVersion = [1, 0, 0, 0, 0, 0, 0, 0]
        };
        invoice.Details.Add(new PoInvoiceDetail
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            DocNo = "PINV01",
            Line = 1,
            ICode = "ITEM1",
            IDesc = "Stock item",
            Qty = 10m,
            UnitPrice = 100m,
            StdQty = 10m,
            StdCustPsize = 1m,
            StdUom = "EA",
            Amount = 1060m,
            NetAmount = 1000m,
            TaxAmt = 60m,
            TaxGroup = "SR",
            IsInclusive = false,
            ItemGlCode = "GLITEM1",
            PoNo = "PO1",
            PoRelNo = 1,
            PoLineNo = 1
        });
        db.PoInvoices.Add(invoice);

        // The PO line behind the invoice, fully received and not yet returned.
        var order = new PoOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PoNo = "PO1",
            PoRelNo = 1,
            Status = "RECEIVED",
            VendCode = "VEND01",
            PoDate = new DateTime(2026, 7, 1)
        };
        order.Details.Add(new PoOrderDetail
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PoNo = "PO1",
            PoRelNo = 1,
            Line = 1,
            ICode = "ITEM1",
            PoPurQty = 10m,
            PoQty = 10m,
            PackSz = 1m,
            StdUom = "EA",
            RecvQty = 10m,
            ReturnQty = 0m
        });
        db.PoOrders.Add(order);

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    // ─────────────────────────── Harness ───────────────────────────

    private PoCdnService CreateSut(Mock<IAccessRightService>? access = null)
    {
        access ??= AlwaysAllowed();
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: "MAIN");
        var postingRepo = new IvStockPostingRepository();
        var common = new IvStockCommonRepository(_factory);
        return new PoCdnService(
            _factory,
            tenant,
            access.Object,
            _numbering,
            new RunningNumberService(),
            new FixedCurrentDateService(FixedToday),
            new PoCdnRepository(),
            new PoInvoiceRepository(),
            postingRepo,
            new IvStockMasterRepository(_factory),
            common,
            new IvInventoryPostingService(
                _factory, tenant, access.Object, new IvStockPostingRepository(),
                common, new PoOrderRepository(), NullLogger<IvInventoryPostingService>.Instance),
            NullLogger<PoCdnService>.Instance);
    }

    private static Mock<IAccessRightService> AlwaysAllowed()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    private static Mock<IAccessRightService> DenyingInternalAdjustment()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(
                It.IsAny<string>(), PermissionCodes.InternalAdjustment, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        return access;
    }

    /// <summary>A minimal valid CN save request against PINV01, reason PRICE_ADJUSTMENT.</summary>
    private static PoCdnSaveRequest CNRequest(
        decimal qty = 1m,
        decimal unitPrice = 100m,
        string? supplierDocNo = "SUP-CN-1",
        DateTime? supplierDocDate = null,
        string vendorCode = "VEND01",
        string? invNo = "PINV01",
        string reasonCode = "PRICE_ADJUSTMENT",
        string? remarks = null,
        string? externalDocNo = null,
        bool returnStock = false,
        bool isStockReturn = false,
        short? invLineNo = 1,
        string? taxGroup = "SR") => new()
    {
        Type = "CN",
        DocDate = FixedToday,
        VendorCode = vendorCode,
        InvNo = invNo,
        ReturnStock = returnStock,
        Currency = "MYR",
        ReasonCode = reasonCode,
        SupplierDocNo = supplierDocNo,
        SupplierDocDate = supplierDocDate ?? new DateTime(2026, 8, 20),
        Remarks = remarks,
        ExternalDocNo = externalDocNo,
        TaxGrCode = taxGroup,
        Lines =
        [
            new PoCdnLineRequest
            {
                InvLineNo = invLineNo,
                IsStockReturn = isStockReturn,
                ICode = "ITEM1",
                Qty = qty,
                UnitPrice = unitPrice,
                IsInclusive = false,
                TaxGroup = taxGroup,
                FromBalLocId = isStockReturn ? 1 : null,
                PoNo = isStockReturn ? "PO1" : null,
                PoRelNo = isStockReturn ? (short)1 : null,
                PoLineNo = isStockReturn ? (short)1 : null,
                FrWarehouse = isStockReturn ? "MAIN" : null,
                LocCode = isStockReturn ? "BIN1" : null,
                IStatus = isStockReturn ? "ACTIVE" : null,
                StdCustPSize = 1m
            }
        ]
    };

    private async Task<PoCdn?> LoadAsync(string docNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.PoCdns.Include(x => x.Details)
            .FirstOrDefaultAsync(x => x.DocNo == docNo);
    }

    // ─────────────────────────── Save: invoice target (C4) ───────────────────────────

    [Fact]
    public async Task Credit_note_against_a_posted_invoice_saves_as_new()
    {
        var sut = CreateSut();

        var result = await sut.SaveNewAsync(CNRequest(qty: 2m));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var saved = await LoadAsync(result.DocNo!);
        Assert.NotNull(saved);
        Assert.Equal("NEW", saved!.Status);
        Assert.Equal("PCN2609-0001", saved.DocNo);
        // 2 x 100 net + 6% tax = 212.
        Assert.Equal(212m, saved.TotAmnt);
        Assert.Equal(200m, saved.GrossAmnt);
        Assert.Equal(12m, saved.Taxes);
        Assert.Equal(1, saved.Details.Count);
        Assert.Equal((short)1, saved.Details.First().InvLineNo!.Value);
    }

    [Fact]
    public async Task Credit_note_without_an_invoice_is_rejected()
    {
        var sut = CreateSut();

        var result = await sut.SaveNewAsync(CNRequest(invNo: null, invLineNo: null, taxGroup: "SR"));

        Assert.False(result.Succeeded);
        Assert.Contains("requires a referenced purchase invoice", result.ErrorMessage);
    }

    [Fact]
    public async Task Credit_note_against_an_unposted_invoice_is_rejected()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var inv = await db.PoInvoices.FirstAsync(x => x.DocNo == "PINV01");
            inv.Status = "NEW";
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var result = await sut.SaveNewAsync(CNRequest());

        Assert.False(result.Succeeded);
        Assert.Contains("must be POSTED", result.ErrorMessage);
    }

    [Fact]
    public async Task Credit_note_against_an_invoice_of_another_vendor_is_rejected()
    {
        var sut = CreateSut();

        var result = await sut.SaveNewAsync(CNRequest(vendorCode: "VEND02"));

        Assert.False(result.Succeeded);
        Assert.Contains("different vendor", result.ErrorMessage);
    }

    // ─────────────────────────── Save: supplier document (C2/C14/C16) ───────────────────────────

    [Fact]
    public async Task Duplicate_supplier_document_is_rejected()
    {
        var sut = CreateSut();
        var first = await sut.SaveNewAsync(CNRequest(supplierDocNo: "SUP-DUP"));
        Assert.True(first.Succeeded, first.ErrorMessage);

        var second = await sut.SaveNewAsync(CNRequest(supplierDocNo: "SUP-DUP"));

        Assert.False(second.Succeeded);
        Assert.Contains("is already recorded on", second.ErrorMessage);
    }

    [Fact]
    public async Task The_same_supplier_document_for_another_vendor_is_allowed()
    {
        var sut = CreateSut();
        Assert.True((await sut.SaveNewAsync(CNRequest(supplierDocNo: "SUP-X"))).Succeeded);

        var other = await sut.SaveNewAsync(CNRequest(supplierDocNo: "SUP-X", vendorCode: "VEND02"));

        // VEND02 has no posted invoice, so this must fail on the invoice rule — not on the
        // supplier-document rule, which is per-vendor.
        Assert.False(other.Succeeded);
        Assert.DoesNotContain("already recorded", other.ErrorMessage);
    }

    [Fact]
    public async Task Supplier_document_date_is_required_when_a_number_is_entered()
    {
        var sut = CreateSut();
        var request = CNRequest(supplierDocNo: "SUP-NODATE");
        request.SupplierDocDate = null;

        var result = await sut.SaveNewAsync(request);

        Assert.False(result.Succeeded);
        Assert.Contains("Supplier document date is required", result.ErrorMessage);
    }

    [Fact]
    public async Task External_document_must_not_repeat_the_supplier_document()
    {
        var sut = CreateSut();

        var result = await sut.SaveNewAsync(
            CNRequest(supplierDocNo: "SUP-SAME", externalDocNo: "SUP-SAME"));

        Assert.False(result.Succeeded);
        Assert.Contains("must not repeat the supplier document number", result.ErrorMessage);
    }

    // ─────────────────────────── Save: vendor controls (C13/C15) ───────────────────────────

    [Fact]
    public async Task A_suspended_vendor_blocks_save()
    {
        var sut = CreateSut();

        var result = await sut.SaveNewAsync(CNRequest(vendorCode: "VENDSUS"));

        Assert.False(result.Succeeded);
        Assert.Contains("is suspended", result.ErrorMessage);
    }

    [Fact]
    public async Task An_inactive_vendor_blocks_save()
    {
        var sut = CreateSut();

        var result = await sut.SaveNewAsync(CNRequest(vendorCode: "VENDINACT"));

        Assert.False(result.Succeeded);
        Assert.Contains("is inactive", result.ErrorMessage);
    }

    [Fact]
    public async Task Vendor_defaults_flag_a_suspended_vendor_as_blocked_for_save()
    {
        var sut = CreateSut();

        var result = await sut.GetVendorDefaultsAsync("VENDSUS", FixedToday);

        Assert.True(result.Succeeded);
        Assert.True(result.VendorDefaults!.Suspended);
        Assert.True(result.VendorDefaults.BlockedForSave);
    }

    // ─────────────────────────── Save: reason code + internal adjustment (C9/C44) ───────────────────────────

    [Fact]
    public async Task A_debit_note_reason_from_the_credit_note_taxonomy_is_rejected()
    {
        var sut = CreateSut();
        var request = CNRequest(reasonCode: "RETURN");
        request.Type = "DN";
        request.InvNo = null;
        request.Lines![0].InvLineNo = null;

        var result = await sut.SaveNewAsync(request);

        Assert.False(result.Succeeded);
        Assert.Contains("not valid for a DN", result.ErrorMessage);
    }

    [Fact]
    public async Task Internal_adjustment_requires_its_own_permission()
    {
        var sut = CreateSut(DenyingInternalAdjustment());

        var result = await sut.SaveNewAsync(
            CNRequest(supplierDocNo: null, reasonCode: "INTERNAL_ADJUSTMENT", remarks: "Reason"));

        Assert.False(result.Succeeded);
        Assert.Contains("not authorized to use INTERNAL_ADJUSTMENT", result.ErrorMessage);
    }

    [Fact]
    public async Task Internal_adjustment_requires_remarks()
    {
        var sut = CreateSut();

        var result = await sut.SaveNewAsync(
            CNRequest(supplierDocNo: null, reasonCode: "INTERNAL_ADJUSTMENT", remarks: null));

        Assert.False(result.Succeeded);
        Assert.Contains("Remarks are required for an internal adjustment", result.ErrorMessage);
    }

    [Fact]
    public async Task Internal_adjustment_saves_with_a_blank_supplier_document()
    {
        var sut = CreateSut();

        var result = await sut.SaveNewAsync(
            CNRequest(supplierDocNo: null, reasonCode: "INTERNAL_ADJUSTMENT", remarks: "Management adjustment"));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var saved = await LoadAsync(result.DocNo!);
        Assert.Null(saved!.SupplierDocNo);
    }

    [Fact]
    public async Task A_blank_supplier_document_is_rejected_for_a_normal_reason()
    {
        var sut = CreateSut();

        var result = await sut.SaveNewAsync(CNRequest(supplierDocNo: null));

        Assert.False(result.Succeeded);
        Assert.Contains("Supplier document number is required", result.ErrorMessage);
    }

    [Fact]
    public async Task An_unknown_reason_code_is_rejected()
    {
        var sut = CreateSut();

        var result = await sut.SaveNewAsync(CNRequest(reasonCode: "NOT_A_REASON"));

        Assert.False(result.Succeeded);
        Assert.Contains("not valid for a CN", result.ErrorMessage);
    }

    // ─────────────────────────── Save: line contract (C30/C46/C38) ───────────────────────────

    [Fact]
    public async Task A_zero_unit_price_on_a_normal_line_is_rejected()
    {
        var sut = CreateSut();

        var result = await sut.SaveNewAsync(CNRequest(unitPrice: 0m));

        Assert.False(result.Succeeded);
        Assert.Contains("unit price must be greater than zero", result.ErrorMessage);
    }

    [Fact]
    public async Task A_line_without_a_resolvable_item_is_rejected()
    {
        var sut = CreateSut();
        var request = CNRequest();
        request.Lines![0].ICode = "MISSING";
        request.Lines[0].InvLineNo = null;

        var result = await sut.SaveNewAsync(request);

        Assert.False(result.Succeeded);
        Assert.Contains("was not found", result.ErrorMessage);
    }

    // ─────────────────────────── Save: source-line ceilings (C34) ───────────────────────────

    [Fact]
    public async Task A_quantity_line_beyond_the_invoice_line_is_rejected()
    {
        var sut = CreateSut();

        // Invoice line 1 is 10 units; reason RETURN is quantity-capped, so 11 must fail.
        var result = await sut.SaveNewAsync(CNRequest(qty: 11m, reasonCode: "RETURN"));

        Assert.False(result.Succeeded);
        Assert.Contains("exceeds the invoice line remaining", result.ErrorMessage);
    }

    [Fact]
    public async Task A_quantity_line_exactly_at_the_invoice_line_is_accepted()
    {
        var sut = CreateSut();

        var result = await sut.SaveNewAsync(CNRequest(qty: 10m, reasonCode: "RETURN"));

        Assert.True(result.Succeeded, result.ErrorMessage);
    }

    [Fact]
    public async Task Source_line_consumption_by_another_credit_note_reduces_the_ceiling()
    {
        var sut = CreateSut();
        var first = await sut.SaveNewAsync(CNRequest(qty: 7m, reasonCode: "RETURN", supplierDocNo: "SUP-A"));
        Assert.True(first.Succeeded, first.ErrorMessage);

        // 7 already consumed of 10 → only 3 remain.
        var second = await sut.SaveNewAsync(CNRequest(qty: 4m, reasonCode: "RETURN", supplierDocNo: "SUP-B"));
        Assert.False(second.Succeeded);
        Assert.Contains("exceeds the invoice line remaining", second.ErrorMessage);

        var fits = await sut.SaveNewAsync(CNRequest(qty: 3m, reasonCode: "RETURN", supplierDocNo: "SUP-C"));
        Assert.True(fits.Succeeded, fits.ErrorMessage);
    }

    // ─────────────────────────── Save: reservation (C3) ───────────────────────────

    [Fact]
    public async Task A_credit_note_beyond_the_remaining_line_value_is_rejected()
    {
        var sut = CreateSut();
        var first = await sut.SaveNewAsync(CNRequest(qty: 6m, unitPrice: 100m));
        Assert.True(first.Succeeded, first.ErrorMessage);

        // PRICE_ADJUSTMENT is value-capped (C34). Line 1 nets 1000; the first CN consumed 600,
        // so the second is refused by the *source-line* ceiling. That gate is deliberately more
        // specific than the document-level reservation and reports first.
        var second = await sut.SaveNewAsync(CNRequest(qty: 6m, unitPrice: 100m, supplierDocNo: "SUP-CN-2"));

        Assert.False(second.Succeeded);
        Assert.Contains("source-line value", second.ErrorMessage);
    }

    [Fact]
    public async Task Deleting_a_draft_releases_the_reservation()
    {
        var sut = CreateSut();
        var first = await sut.SaveNewAsync(CNRequest(qty: 6m));
        Assert.True(first.Succeeded, first.ErrorMessage);

        var saved = await LoadAsync(first.DocNo!);
        var deleted = await sut.DeleteAsync([new PoCdnKeyedRequest
        {
            DocNo = first.DocNo!,
            RowVersion = saved!.RowVersion
        }]);
        Assert.True(deleted.Succeeded, deleted.ErrorMessage);

        var second = await sut.SaveNewAsync(CNRequest(qty: 6m, supplierDocNo: "SUP-CN-2"));
        Assert.True(second.Succeeded, second.ErrorMessage);
    }

    // ─────────────────────────── Currency (C18) ──────────────────────────────

    [Fact]
    public async Task A_foreign_currency_with_no_rate_window_fails_closed()
    {
        var sut = CreateSut();

        var result = await sut.ResolveCurrencyRateAsync("GBP", FixedToday);

        Assert.False(result.Succeeded);
        Assert.Contains("No currency rate for GBP", result.ErrorMessage);
    }

    [Fact]
    public async Task A_credit_note_rejects_a_currency_that_differs_from_the_invoice()
    {
        var sut = CreateSut();
        var request = CNRequest();
        request.Currency = "USD";

        // C18: the invoice currency governs. Reject the conflict rather than silently overriding,
        // so the operator is told their choice was discarded.
        var result = await sut.SaveNewAsync(request);

        Assert.False(result.Succeeded);
        Assert.Contains("does not match the currency MYR of invoice PINV01", result.ErrorMessage);
    }

    [Fact]
    public async Task A_credit_note_with_a_blank_currency_inherits_the_invoice_currency()
    {
        var sut = CreateSut();
        var request = CNRequest();
        request.Currency = null;

        var result = await sut.SaveNewAsync(request);

        Assert.True(result.Succeeded, result.ErrorMessage);
        var saved = await LoadAsync(result.DocNo!);
        Assert.Equal("MYR", saved!.Currency);
    }

    // ─────────────────────────── Lifecycle (C21/C39) ───────────────────────────

    [Fact]
    public async Task A_posted_document_cannot_be_edited()
    {
        var sut = CreateSut();
        var saved = await LoadAsync((await sut.SaveNewAsync(CNRequest())).DocNo!);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var doc = await db.PoCdns.FirstAsync(x => x.DocNo == saved!.DocNo);
            doc.Status = "POSTED";
            await db.SaveChangesAsync();
        }

        var reread = await LoadAsync(saved!.DocNo);
        var updateRequest = CNRequest(qty: 2m);
        updateRequest.RowVersion = reread!.RowVersion;
        var result = await sut.UpdateAsync(saved.DocNo, updateRequest);

        Assert.False(result.Succeeded);
        Assert.Contains("Only NEW documents can be edited", result.ErrorMessage);
    }

    [Fact]
    public async Task Posting_a_value_only_credit_note_sets_posted_without_a_vendor_return()
    {
        var sut = CreateSut();
        var saved = await LoadAsync((await sut.SaveNewAsync(CNRequest())).DocNo!);

        var result = await sut.PostAsync([new PoCdnKeyedRequest
        {
            DocNo = saved!.DocNo,
            RowVersion = saved.RowVersion
        }]);

        Assert.True(result.Succeeded, result.ErrorMessage);
        var posted = await LoadAsync(saved.DocNo);
        Assert.Equal("POSTED", posted!.Status);
        Assert.Null(posted.VrBatchNo);
        Assert.NotNull(posted.PostedDate);
    }

    [Fact]
    public async Task Posting_a_debit_note_with_no_invoice_is_allowed()
    {
        var sut = CreateSut();
        var request = CNRequest(reasonCode: "FREIGHT_ADJUSTMENT", supplierDocNo: "SUP-DN-1");
        request.Type = "DN";
        request.InvNo = null;
        request.Lines![0].InvLineNo = null;

        var save = await sut.SaveNewAsync(request);
        Assert.True(save.Succeeded, save.ErrorMessage);

        var saved = await LoadAsync(save.DocNo!);
        var post = await sut.PostAsync([new PoCdnKeyedRequest
        {
            DocNo = saved!.DocNo,
            RowVersion = saved.RowVersion
        }]);

        Assert.True(post.Succeeded, post.ErrorMessage);
        Assert.Equal("POSTED", (await LoadAsync(saved.DocNo))!.Status);
    }

    [Fact]
    public async Task Rollback_returns_the_document_to_new_and_keeps_the_reservation()
    {
        var sut = CreateSut();
        var saved = await LoadAsync((await sut.SaveNewAsync(CNRequest(qty: 6m))).DocNo!);
        await sut.PostAsync([new PoCdnKeyedRequest { DocNo = saved!.DocNo, RowVersion = saved.RowVersion }]);

        var posted = await LoadAsync(saved.DocNo);
        var rollback = await sut.RollbackAsync([new PoCdnKeyedRequest
        {
            DocNo = posted!.DocNo,
            RowVersion = posted.RowVersion
        }]);

        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        Assert.Equal("NEW", (await LoadAsync(posted.DocNo))!.Status);

        // C39: the rolled-back draft still consumes the invoice, so a second 6 cannot be added.
        var second = await sut.SaveNewAsync(CNRequest(qty: 6m, supplierDocNo: "SUP-CN-9"));
        Assert.False(second.Succeeded);
    }

    [Fact]
    public async Task Posting_an_already_posted_document_is_rejected()
    {
        var sut = CreateSut();
        var saved = await LoadAsync((await sut.SaveNewAsync(CNRequest())).DocNo!);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var doc = await db.PoCdns.FirstAsync(x => x.DocNo == saved!.DocNo);
            doc.Status = "POSTED";
            await db.SaveChangesAsync();
        }

        var reread = await LoadAsync(saved!.DocNo);
        var result = await sut.PostAsync([new PoCdnKeyedRequest
        {
            DocNo = saved.DocNo,
            RowVersion = reread!.RowVersion
        }]);

        Assert.False(result.Succeeded);
        Assert.Contains("Only NEW documents can be posted", result.ErrorMessage);
    }

    // ─────────────────────────── Invoice picker / copy (C5/C24/C34) ───────────────────────────

    [Fact]
    public async Task Invoice_lines_report_the_remaining_after_other_credit_notes()
    {
        var sut = CreateSut();
        var first = await sut.SaveNewAsync(CNRequest(qty: 4m, reasonCode: "RETURN"));
        Assert.True(first.Succeeded, first.ErrorMessage);

        var result = await sut.GetInvoiceLinesAsync("PINV01", null);

        Assert.True(result.Succeeded);
        var line = Assert.Single(result.InvoiceLinePickerRows);
        Assert.Equal(10m, line.StdQty);
        Assert.Equal(6m, line.RemainingStdQty);
    }

    [Fact]
    public async Task Copy_from_invoice_produces_a_purchase_credit_note_not_a_quantity_correction()
    {
        var sut = CreateSut();

        var result = await sut.CopyFromInvoiceAsync("PINV01");

        Assert.True(result.Succeeded, result.ErrorMessage);
        var doc = result.Document!;
        Assert.Equal("CN", doc.Type);
        Assert.Equal("PINV01", doc.InvNo);
        Assert.False(doc.ReturnStock);
        Assert.Equal("VEND01", doc.VendorCode);
        Assert.All(doc.Lines, l => Assert.NotNull(l.InvLineNo));
    }

    [Fact]
    public async Task Copy_from_invoice_rejects_an_unposted_invoice()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var inv = await db.PoInvoices.FirstAsync(x => x.DocNo == "PINV01");
            inv.Status = "NEW";
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var result = await sut.CopyFromInvoiceAsync("PINV01");

        Assert.False(result.Succeeded);
    }

    // ─────────────────────────── Reservation reporting ───────────────────────────

    [Fact]
    public async Task The_reservation_summary_separates_drafts_from_posted_notes()
    {
        var sut = CreateSut();
        var posted = await LoadAsync((await sut.SaveNewAsync(CNRequest(qty: 3m))).DocNo!);
        await sut.PostAsync([new PoCdnKeyedRequest { DocNo = posted!.DocNo, RowVersion = posted.RowVersion }]);
        await sut.SaveNewAsync(CNRequest(qty: 2m, supplierDocNo: "SUP-CN-DRAFT"));

        var result = await sut.GetInvoiceReservationsAsync("PINV01", null);

        Assert.True(result.Succeeded);
        var summary = result.Reservations!;
        Assert.Equal(1060m, summary.InvoiceTotal);
        // Totals are tax-inclusive so they compare like-for-like with the invoice total:
        // 3 x 100 + 6% = 318, 2 x 100 + 6% = 212.
        Assert.Equal(318m, summary.PostedCnTotal);
        Assert.Equal(212m, summary.DraftCnTotal);
        Assert.Equal(530m, summary.Remaining);
        Assert.False(summary.OverReserved);
        Assert.Contains(summary.Rows, r => r.IsDraft);
    }
}
