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
    /// §7 — resolves the tax-exclusive base price for (customer, item, UOM, qty) using the same
    /// authoritative context as live document pricing, and reports which source produced it.
    /// </summary>
    public async Task<IvMasterOperationResult<SaItemFamilyPriceResolution>> ResolveItemPriceAsync(
        SaItemFamilyPriceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return FailVm<SaItemFamilyPriceResolution>(IvMasterErrorCode.Validation, "A price request is required.");
        }

        var context = await LoadPriceContextAsync(
            new SaLinePricingRequest
            {
                CustCode = request.CustCode,
                ICode = request.ICode,
                UOM = request.UOM,
                Qty = request.Qty,
                DocDate = request.DocDate,
                DocumentCurrency = request.DocumentCurrency
            },
            cancellationToken);
        if (!context.Succeeded)
        {
            return FailVm<SaItemFamilyPriceResolution>(
                context.ErrorCode,
                context.Message ?? "Unable to resolve a price.");
        }

        var resolution = SaItemFamilyPriceResolver.Resolve(
            context.Data!.Request,
            context.Data.Candidates);

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
