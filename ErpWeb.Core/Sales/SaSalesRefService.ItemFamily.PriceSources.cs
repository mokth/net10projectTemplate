using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Sales line pricing orchestration — plan sections "Company pricing mode", "Line pricing pipeline"
/// and "Target resolution order".
///
/// This is the ONLY place a document line price is decided. It runs the four pipeline stages in the
/// order the plan requires:
/// <list type="number">
/// <item>price resolution — the company method's eligible sources, walked by the pure resolver;</item>
/// <item>tax-basis conversion — the exclusive master price grossed up when the line is inclusive;</item>
/// <item>discount resolution — against the STAGE 2 price, because <c>SaInvoiceCalc</c> derives the
/// per-unit discount from the line's raw <c>UnitPrice</c> before un-taxing it;</item>
/// <item>document calculation — owned by <c>SaInvoiceCalc</c> on the page, the only rounding point.</item>
/// </list>
///
/// Tenant-scoped but deliberately NOT menu-gated: this is a server capability used from document
/// entry, not a screen. <c>VIEW_PRICE</c> governs price <i>projections</i>; a document line needs the
/// number to exist at all. "No price" is always a blocking result, never <c>0</c>.
/// </summary>
public sealed partial class SaSalesRefService
{
    /// <summary>
    /// Stages 1-3 for ONE document line. Returns a basis-corrected unit price plus the engine-ready
    /// discount slots, or a blocking message the operator can act on.
    /// </summary>
    public async Task<IvMasterOperationResult<SaLinePricingResult>> ResolveLinePricingAsync(
        SaLinePricingRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = await LoadPriceContextAsync(request, cancellationToken);
        if (!context.Succeeded)
        {
            return FailVm<SaLinePricingResult>(
                context.ErrorCode,
                context.Message ?? "Unable to resolve a price.");
        }

        var priceRequest = context.Data!.Request;
        var candidates = context.Data.Candidates;

        // ---------- stage 1: price resolution (tax-EXCLUSIVE result) ----------
        var resolution = SaItemFamilyPriceResolver.Resolve(priceRequest, candidates);
        if (!resolution.Found)
        {
            return FailVm<SaLinePricingResult>(IvMasterErrorCode.Validation, resolution.Message);
        }

        var exclusivePrice = resolution.UnitPrice!.Value;

        // ---------- stage 2: tax-basis conversion, BEFORE the discount ----------
        // Masters store tax-exclusive; an inclusive line stores the grossed-up value, which
        // SaInvoiceCalc will un-tax again. Rounding is deliberately NOT done here (§8.4).
        var unitPrice = request.IsInclusive
            ? SaItemFamilyPriceBasis.ToInclusive(exclusivePrice, request.TaxPercent / 100m)
            : exclusivePrice;

        // ---------- stage 3: discount, resolved against the STAGE 2 price ----------
        var discount = await ResolveItemDiscountAsync(
            new SaItemFamilyDiscountRequest
            {
                ICode = priceRequest.ICode,
                IClass = request.IClass,
                Qty = request.Qty,
                DocDate = request.DocDate,
                DiscountMethod = request.DiscountMethod,
                UnitPrice = unitPrice
            },
            cancellationToken);

        // A failed discount lookup is NOT a pricing failure: "no matching rule" is a valid no-discount.
        var selection = discount.Succeeded && discount.Data is not null
            ? discount.Data
            : SaItemFamilyDiscountSelection.None;

        return IvMasterOperationResult<SaLinePricingResult>.Ok(new SaLinePricingResult
        {
            UnitPrice = unitPrice,
            BaseUnitPrice = exclusivePrice,
            PricingSource = resolution.PricingSource,
            PricingRef = resolution.PricingRef,
            MatchedMoq = resolution.MatchedMoq,
            MatchedMinQty = resolution.MatchedMinQty,
            MatchedMaxQty = resolution.MatchedMaxQty,
            ValidFrom = resolution.ValidFrom,
            ValidTo = resolution.ValidTo,
            Currency = resolution.Currency,
            ItemDiscount = selection.PercentSlot1,
            ItemDiscount2 = selection.PercentSlot2,
            ItemDiscAmount = selection.AmountSlot1,
            ItemDiscAmount1 = selection.AmountSlot2,
            DiscountPerUnit = selection.DiscountPerUnit,
            DiscountRuleId = selection.RuleId,
            CompetingDiscountRuleIds = selection.CompetingRuleIds
        });
    }

    /// <summary>
    /// Stage 0 — the company's pricing method. Defaults to the full specificity chain when the column
    /// is NULL, blank or holds a token this build does not recognise.
    /// </summary>
    private async Task<string> GetSalesPriceMethodAsync(
        AppDbContext db,
        string company,
        CancellationToken cancellationToken)
    {
        var stored = await db.Companies.AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .Select(x => x.SalesPriceMethod)
            .FirstOrDefaultAsync(cancellationToken);

        return SaCompanyPriceMethod.Normalize(stored);
    }

    /// <summary>
    /// Phase 6 — the explanation ladder for ONE line. Same inputs as
    /// <see cref="ResolveLinePricingAsync"/>, but instead of stopping at the first usable price it
    /// reports EVERY level: the candidate, the band, the window, the currency, whether it applied and,
    /// when it did not, why — including levels this company's pricing method excluded.
    ///
    /// That answers the top support question directly: "why didn't the customer's special price apply?"
    /// → "this company is set to PRICE_LIST_ONLY".
    ///
    /// It runs through the SAME <c>Select*</c> helpers as the resolver, so an inquiry can never disagree
    /// with the price a document receives. Tenant-scoped but deliberately NOT menu-gated, like the
    /// resolver: it is a server capability, not a screen.
    /// </summary>
    public async Task<IvMasterOperationResult<SaPriceExplanation>> ExplainLinePriceAsync(
        SaLinePricingRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = await LoadPriceContextAsync(request, cancellationToken);
        if (!context.Succeeded)
        {
            return FailVm<SaPriceExplanation>(
                context.ErrorCode,
                context.Message ?? "Unable to explain a price.");
        }

        var explanation = SaItemFamilyPriceExplainer.Explain(
            context.Data!.Request,
            context.Data.Candidates);

        return IvMasterOperationResult<SaPriceExplanation>.Ok(explanation);
    }

    /// <summary>
    /// The customer, company method, group default and eligible candidate set for ONE line. Shared by
    /// <see cref="ResolveLinePricingAsync"/> and <see cref="ExplainLinePriceAsync"/> so the two can never
    /// load a different picture of the same line.
    /// </summary>
    private sealed record SaPriceContext(
        SaItemFamilyPriceRequest Request,
        SaItemFamilyPriceCandidates Candidates);

    private async Task<IvMasterOperationResult<SaPriceContext>> LoadPriceContextAsync(
        SaLinePricingRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return FailVm<SaPriceContext>(IvMasterErrorCode.Validation, "A pricing request is required.");
        }

        var ctx = ValidateCompanyContext();
        if (ctx.Error is not null)
        {
            return FailVm<SaPriceContext>(ctx.Error.Value);
        }

        var company = ctx.CompanyCode!;
        var custCode = NormalizeOptionalCode(request.CustCode);
        var iCode = NormalizeOptionalCode(request.ICode);
        var uom = NormalizeOptionalCode(request.UOM);

        if (custCode.Length == 0 || iCode.Length == 0 || uom.Length == 0)
        {
            return FailVm<SaPriceContext>(
                IvMasterErrorCode.Validation,
                "Customer, item and UOM are required to resolve a price.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var customer = await db.SaCusts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.CustCode == custCode, cancellationToken);
        if (customer is null)
        {
            return FailVm<SaPriceContext>(
                IvMasterErrorCode.NotFound,
                $"Customer {custCode} was not found in this company.");
        }

        // The company pricing method is read SERVER-SIDE and never accepted from the caller, so a page
        // cannot widen its own eligibility. NULL / unknown normalises to the full specificity chain.
        var companyMethod = await GetSalesPriceMethodAsync(db, company, cancellationToken);

        // Phase 5: the customer's GROUP default price list. Loaded only when the company method makes
        // the group level eligible, so an excluded source is never even queried. The walk still tries
        // the customer's own list FIRST, so a customer assignment always beats its group's default.
        var groupPriceCode = string.Empty;
        if (SaCompanyPriceMethod.ResolveSources(companyMethod).Contains(SaPriceSource.CustomerGroupPriceList))
        {
            var groupCode = NormalizeOptionalCode(customer.CustGroupCode);
            if (groupCode.Length > 0)
            {
                groupPriceCode = NormalizeOptionalCode(
                    await db.SaCustGroups.AsNoTracking()
                        .Where(x => x.CompanyCode == company && x.CustGroupCode == groupCode)
                        .Select(x => x.CustPriceCode)
                        .FirstOrDefaultAsync(cancellationToken));
            }
        }

        var priceRequest = new SaItemFamilyPriceRequest
        {
            CustCode = custCode,
            ICode = iCode,
            UOM = uom,
            Qty = request.Qty,
            DocDate = request.DocDate,
            PriceMethod = customer.PriceMethod,
            CustPriceCode = NormalizeOptionalCode(customer.CustPriceCode),
            GroupPriceCode = groupPriceCode,
            DocumentCurrency = request.DocumentCurrency,
            CompanyPriceMethod = companyMethod
        };

        var candidates = await LoadPriceCandidatesAsync(db, company, custCode, iCode, priceRequest, cancellationToken);

        return IvMasterOperationResult<SaPriceContext>.Ok(new SaPriceContext(priceRequest, candidates));
    }

    /// <summary>
    /// Loads ONLY the candidates the company method can use. A source the method excludes is never
    /// queried, so an ineligible source cannot influence the result even if its data is dirty.
    /// </summary>
    private async Task<SaItemFamilyPriceCandidates> LoadPriceCandidatesAsync(
        AppDbContext db,
        string company,
        string custCode,
        string iCode,
        SaItemFamilyPriceRequest request,
        CancellationToken cancellationToken)
    {
        var eligible = SaCompanyPriceMethod.ResolveSources(request.CompanyPriceMethod);

        var customerItems = new List<SaItemCustPriceCandidate>();
        if (eligible.Contains(SaPriceSource.CustomerItem))
        {
            var rows = await db.SaItemCusts.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.CustCode == custCode && x.ICode == iCode)
                .Select(x => new { x.ICode, x.SellingUOM, x.MOQ, x.UnitPrice, x.Currency })
                .ToListAsync(cancellationToken);

            customerItems = rows
                .Select(x => new SaItemCustPriceCandidate
                {
                    ICode = x.ICode,
                    UOM = x.SellingUOM,
                    MOQ = x.MOQ,
                    // The live column is `float`; the explicit scale happens here, once (§8.5).
                    UnitPrice = ScaleLegacyMoney(x.UnitPrice),
                    Currency = x.Currency
                })
                .ToList();
        }

        var priceListLines = eligible.Contains(SaPriceSource.CustomerPriceList)
            ? await LoadPriceListCandidatesAsync(db, company, request.CustPriceCode, iCode, cancellationToken)
            : [];

        // Phase 5: the group's default list, loaded only when the group level is eligible. A blank
        // group code (or a group with no default) yields no lines and the walk continues.
        var groupPriceListLines = eligible.Contains(SaPriceSource.CustomerGroupPriceList)
            ? await LoadPriceListCandidatesAsync(db, company, request.GroupPriceCode, iCode, cancellationToken)
            : [];

        IvStockMasterPriceCandidate[] items = [];
        if (eligible.Contains(SaPriceSource.ItemDefault))
        {
            var item = await db.IvStockMasters.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.ICode == iCode)
                .Select(x => new { x.ICode, x.IsActive, x.SellingUom, x.StdUom, x.SellingPrice })
                .FirstOrDefaultAsync(cancellationToken);

            if (item is not null)
            {
                items =
                [
                    new IvStockMasterPriceCandidate
                    {
                        ICode = item.ICode,
                        IsActive = item.IsActive,
                        SellingUom = item.SellingUom,
                        StdUom = item.StdUom,
                        SellingPrice = item.SellingPrice
                    }
                ];
            }
        }

        return new SaItemFamilyPriceCandidates
        {
            CustomerItems = customerItems,
            PriceListLines = priceListLines,
            GroupPriceListLines = groupPriceListLines,
            Items = items
        };
    }

    /// <summary>
    /// Price-list lines for one list and item. The HEADER's <c>IsActive</c> is part of eligibility, so
    /// a retired list yields no lines rather than silently continuing to price (D-6 clause 3 keeps an
    /// unknown code a fall-through, which is different from a retired list).
    /// </summary>
    private static async Task<IReadOnlyList<IvCustPriceCandidate>> LoadPriceListCandidatesAsync(
        AppDbContext db,
        string company,
        string? priceCode,
        string iCode,
        CancellationToken cancellationToken)
    {
        var code = (priceCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return [];
        }

        var rows = await (
            from price in db.IvCustPrices.AsNoTracking()
            join listHeader in db.IvCustPriceGroups.AsNoTracking()
                on new { price.CompanyCode, price.CustPriceCode }
                equals new { listHeader.CompanyCode, listHeader.CustPriceCode }
            where price.CompanyCode == company
                  && price.CustPriceCode == code
                  && price.ICode == iCode
                  && listHeader.IsActive
            select new
            {
                price.CustPriceCode,
                price.ICode,
                price.UOM,
                price.SellingPrice,
                price.ValidFrom,
                price.ValidTo,
                price.MinQty,
                price.MaxQty,
                price.CurrencyCode
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(x => new IvCustPriceCandidate
            {
                CustPriceCode = x.CustPriceCode,
                ICode = x.ICode,
                UOM = x.UOM,
                SellingPrice = x.SellingPrice,
                // Phase 3: the window, the quantity band and the currency now come from the row, so the
                // selector's filters (WithinWindow / WithinBand / currency block) are LIVE. A legacy row
                // migrated to the 1900-01-01 sentinel reads as "always", which is the pre-Phase-3
                // behaviour exactly. Projected as nullable so an unmigrated NULL still means "always".
                ValidFrom = x.ValidFrom,
                ValidTo = x.ValidTo,
                MinQty = x.MinQty,
                MaxQty = x.MaxQty,
                Currency = x.CurrencyCode
            })
            .ToList();
    }
}
