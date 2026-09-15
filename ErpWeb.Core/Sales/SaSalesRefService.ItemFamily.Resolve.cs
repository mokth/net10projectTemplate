using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Sales item family — the resolution entry points (§7 price, §8 discount, §8.9 consumer contract).
///
/// The masters elsewhere in this partial class are the *data*; these two methods are the single place
/// where a price or a discount is decided. The future SO/DO/INV/CN consumer loads nothing itself: it
/// calls these, then persists what they return.
///
/// Deliberate properties:
/// <list type="bullet">
/// <item>Tenant-scoped but <b>not screen-gated</b> — this is a server capability used from document
/// entry, not a page of its own, so requiring ACCESS on a price-master menu would block legitimate
/// sales users. Every read is filtered by the caller's company.</item>
/// <item><c>VIEW_PRICE</c> is deliberately <b>not</b> applied here. §11.1 restricts a read path whose
/// only purpose is to render a price; a document line needs the number to exist at all, and the line's
/// unit price is already visible to whoever may author the document.</item>
/// <item>"No price" is a blocking result, never <c>0</c> (§7 step 4, §8.9 rule 5).</item>
/// </list>
/// </summary>
public sealed partial class SaSalesRefService
{
    /// <summary>
    /// §7 — resolves the price for (customer, item, UOM, qty) using the shipped masters, and reports
    /// which source produced it. Fails closed for dealer-mode customers and for a customer-item price
    /// stored in another currency.
    /// </summary>
    public async Task<IvMasterOperationResult<SaItemFamilyPriceResolution>> ResolveItemPriceAsync(
        SaItemFamilyPriceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return FailVm<SaItemFamilyPriceResolution>(IvMasterErrorCode.Validation, "A price request is required.");
        }

        var ctx = ValidateCompanyContext();
        if (ctx.Error is not null)
        {
            return FailVm<SaItemFamilyPriceResolution>(ctx.Error.Value);
        }

        var company = ctx.CompanyCode!;
        var custCode = NormalizeOptionalCode(request.CustCode);
        var iCode = NormalizeOptionalCode(request.ICode);
        var uom = NormalizeOptionalCode(request.UOM);

        if (custCode.Length == 0 || iCode.Length == 0 || uom.Length == 0)
        {
            return FailVm<SaItemFamilyPriceResolution>(
                IvMasterErrorCode.Validation,
                "Customer, item and UOM are required to resolve a price.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var customer = await db.SaCusts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.CustCode == custCode, cancellationToken);
        if (customer is null)
        {
            return FailVm<SaItemFamilyPriceResolution>(
                IvMasterErrorCode.NotFound,
                $"Customer {custCode} was not found in this company.");
        }

        var custPriceCode = NormalizeOptionalCode(customer.CustPriceCode);

        // Candidates are loaded per source and filtered in memory by the pure resolver, so the rule that
        // decides precedence lives in exactly one place (§9.1).
        var customerItems = await db.SaItemCusts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.CustCode == custCode && x.ICode == iCode)
            .Select(x => new { x.ICode, x.SellingUOM, x.MOQ, x.UnitPrice, x.Currency })
            .ToListAsync(cancellationToken);

        // An unknown/retired CustPriceCode simply yields no lines (D-6 clause 3).
        var priceLines = custPriceCode.Length == 0
            ? new List<IvCustPriceCandidate>()
            : (await db.IvCustPrices.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.CustPriceCode == custPriceCode && x.ICode == iCode)
                .Select(x => new { x.CustPriceCode, x.ICode, x.UOM, x.SellingPrice })
                .ToListAsync(cancellationToken))
              .Select(x => new IvCustPriceCandidate
              {
                  CustPriceCode = x.CustPriceCode,
                  ICode = x.ICode,
                  UOM = x.UOM,
                  SellingPrice = x.SellingPrice
              })
              .ToList();

        var item = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.ICode == iCode)
            .Select(x => new { x.ICode, x.IsActive, x.SellingUom, x.StdUom, x.SellingPrice })
            .FirstOrDefaultAsync(cancellationToken);

        var candidates = new SaItemFamilyPriceCandidates
        {
            CustomerItems = customerItems
                .Select(x => new SaItemCustPriceCandidate
                {
                    ICode = x.ICode,
                    UOM = x.SellingUOM,
                    MOQ = x.MOQ,
                    // The live column is `float`; scaling happens here, once (§8.5).
                    UnitPrice = ScaleLegacyMoney(x.UnitPrice),
                    Currency = x.Currency
                })
                .ToList(),
            PriceListLines = priceLines,
            Items = item is null
                ? []
                :
                [
                    new IvStockMasterPriceCandidate
                    {
                        ICode = item.ICode,
                        IsActive = item.IsActive,
                        SellingUom = item.SellingUom,
                        StdUom = item.StdUom,
                        SellingPrice = item.SellingPrice
                    }
                ]
        };

        var resolution = SaItemFamilyPriceResolver.Resolve(
            new SaItemFamilyPriceRequest
            {
                CustCode = custCode,
                ICode = iCode,
                UOM = uom,
                Qty = request.Qty,
                DocDate = request.DocDate,
                PayCode = request.PayCode,
                PriceMethod = customer.PriceMethod,
                CustPriceCode = custPriceCode,
                DocumentCurrency = request.DocumentCurrency
            },
            candidates);

        return resolution.Found
            ? IvMasterOperationResult<SaItemFamilyPriceResolution>.Ok(resolution)
            : FailVm<SaItemFamilyPriceResolution>(IvMasterErrorCode.Validation, resolution.Message);
    }

    /// <summary>
    /// §8 — resolves the item discount rule for (item, class, qty, date) and returns the engine-ready
    /// slots plus the unrounded per-unit discount. No match is a valid outcome (no discount), never an
    /// error. When legacy data still holds overlapping rules the winners/losers are deterministic and
    /// <see cref="SaItemFamilyDiscountSelection.CompetingRuleIds"/> names the losers (§8.3.1).
    /// </summary>
    public async Task<IvMasterOperationResult<SaItemFamilyDiscountSelection>> ResolveItemDiscountAsync(
        SaItemFamilyDiscountRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return FailVm<SaItemFamilyDiscountSelection>(
                IvMasterErrorCode.Validation,
                "A discount request is required.");
        }

        var ctx = ValidateCompanyContext();
        if (ctx.Error is not null)
        {
            return FailVm<SaItemFamilyDiscountSelection>(ctx.Error.Value);
        }

        var company = ctx.CompanyCode!;
        var iCode = NormalizeOptionalCode(request.ICode);

        if (iCode.Length == 0)
        {
            return FailVm<SaItemFamilyDiscountSelection>(
                IvMasterErrorCode.Validation,
                "An item is required to resolve a discount.");
        }

        if (request.Qty <= 0m)
        {
            return FailVm<SaItemFamilyDiscountSelection>(
                IvMasterErrorCode.Validation,
                "Quantity must be greater than zero to resolve a quantity-band discount.",
                field: nameof(request.Qty));
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Bounded by design: rules are keyed by item, so this is the item's rule set and nothing else.
        var rules = await db.SaDisGroupItems.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.ICode == iCode)
            .Select(x => new
            {
                x.Id,
                x.ICode,
                x.IClass,
                x.QtyFr,
                x.QtyTo,
                x.DateFr,
                x.DateTo,
                x.Discount,
                x.DiscountType,
                x.Discount1,
                x.DiscountType1
            })
            .ToListAsync(cancellationToken);

        var selection = SaItemFamilyDiscountResolver.Resolve(
            new SaItemFamilyDiscountRequest
            {
                ICode = iCode,
                IClass = request.IClass,
                Qty = request.Qty,
                DocDate = request.DocDate,
                DiscountMethod = request.DiscountMethod,
                UnitPrice = request.UnitPrice
            },
            rules.Select(x => new SaItemFamilyDiscountRule
            {
                Id = x.Id,
                ICode = x.ICode,
                IClass = x.IClass,
                QtyFr = x.QtyFr,
                QtyTo = x.QtyTo,
                DateFr = x.DateFr,
                DateTo = x.DateTo,
                Discount = x.Discount,
                DiscountType = x.DiscountType,
                Discount1 = x.Discount1,
                DiscountType1 = x.DiscountType1
            }).ToList());

        return IvMasterOperationResult<SaItemFamilyDiscountSelection>.Ok(selection);
    }
}
