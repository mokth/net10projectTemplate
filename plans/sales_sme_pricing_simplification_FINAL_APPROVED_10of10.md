# Sales SME Pricing Simplification — FINAL AI Coding Agent Plan

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `production`  
**Current verified commit:** `6b729eb24be8f00e3b02ba200cb2e02e920d1e05`  
**Verified date:** 2026-10-08  
**Plan readiness:** **10.0/10 — APPROVED FOR IMPLEMENTATION**  
**Target:** Malaysian SME ERP. Preserve the current practical pricing engine; remove conflicting legacy customer-level pricing authority without introducing enterprise/SAP-style complexity.

# Objective

Make `Company.SalesPriceMethod` the **single active authority for price-source eligibility** while preserving the current fixed SME-friendly priority:

`Customer Item Price -> Customer Price List -> Customer Group Price List -> Item Default Selling Price`

Remove the misleading customer-level Price Method controls from Customer Master. Existing `SaCust.PriceMethod` values remain stored as legacy data but MUST NOT block, select, reorder, or otherwise influence a selling price.

Unify the older `ResolveItemPriceAsync(...)` base-price path with the same server-side price context used by live sales documents and Price Inquiry.

# Confirmed Problems

## 1. Customer Master exposes an obsolete/conflicting Price Method

**Verified files**
- `ErpWeb.UI/Sales/Masters/SaCustEntry.razor`
- `ErpWeb.UI/Sales/Masters/SaCustEntry.razor.cs`
- `ErpWeb.Core/Sales/SaCustService.cs`
- `ErpWeb.Core/Sales/SaCustResults.cs`

**Current behavior**
- Customer UI exposes:
  - `FOLLOW SELLING PRICE X DISCOUNT`
  - `FOLLOW DEFAULT DEALER PRICE`
- Values persist in `SaCust.PriceMethod`.
- Customer view displays `Price method`.
- `CreateBlank()` defaults new customers to `SaCustPaymentOptions.PriceSelling`.
- `Copy From` clones `PriceMethod` from the source customer.

**Problem**
- Live pricing already has a separate company authority: `Company.SalesPriceMethod`.
- `FOLLOW SELLING PRICE X DISCOUNT` does not describe the real source chain.
- `FOLLOW DEFAULT DEALER PRICE` currently blocks pricing.
- Removing only the blank-customer default is insufficient because `Copy From` would still copy the obsolete value.

## 2. Legacy DEALER metadata blocks the pure resolver

**Verified file**
- `ErpWeb.Core/Sales/SaItemFamilyPricing.cs`

**Verified symbol**
- `SaItemFamilyPriceResolver.Resolve(...)`

**Current behavior**
- `Resolve(...)` checks `request.PriceMethod`.
- `IsDealerPriceMethod(...) == true` returns:
  `Dealer pricing is not supported for this customer...`
- The company source chain is never evaluated for that customer.

**Required behavior**
- `request.PriceMethod` MUST NOT gate pricing.
- Legacy DEALER metadata MUST be ignored by active price selection.
- Pricing MUST continue through `SaCompanyPriceMethod.ResolveSources(request.CompanyPriceMethod)`.

## 3. Price Inquiry has a second DEALER short-circuit

**Verified file**
- `ErpWeb.Core/Sales/SaItemFamilyPriceExplanation.cs`

**Verified symbol**
- `SaItemFamilyPriceExplainer.Explain(...)`

**Current behavior**
- The explainer calls `SaItemFamilyPriceResolver.IsDealerPriceMethod(request.PriceMethod)`.
- For DEALER metadata it marks every level not applicable instead of walking the company pricing chain.

**Problem**
- Removing the DEALER block only from `Resolve(...)` would make live document pricing and Price Inquiry disagree.

**Required behavior**
- Remove the DEALER-specific explainer branch.
- Explanation MUST use the same company eligibility and `ResolveSource(...)` rules as actual resolution.

## 4. `ResolveItemPriceAsync(...)` duplicates an older, weaker candidate-loading path

**Verified files**
- `ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.Resolve.cs`
- `ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.PriceSources.cs`

**Current behavior**
- Live sales documents call `ResolveLinePricingAsync(...)`.
- `ResolveLinePricingAsync(...)` uses `LoadPriceContextAsync(...)`.
- That shared context reads `Company.SalesPriceMethod` server-side and loads:
  - customer item price;
  - customer price list;
  - customer group price list;
  - item default;
  - active price-list header state;
  - quantity bands;
  - validity dates;
  - currency.
- Older `ResolveItemPriceAsync(...)` independently loads customer item, customer list and item default.
- The old path passes `customer.PriceMethod`.
- The old path does not share the live document group-list/header/date/band context.

**Required behavior**
- `ResolveItemPriceAsync(...)` MUST reuse the authoritative base-price context.
- Do not retain a second independent source-loading implementation.

## 5. Retired/inactive price-list behavior differs between the old and live paths

**Verified files**
- `ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.PriceSources.cs`
- `ErpWeb.Tests/Sales/Master/SaItemFamilyResolutionServiceTests.cs`

**Current live-document behavior**
- `LoadPriceListCandidatesAsync(...)` joins `IvCustPriceGroup` and requires `listHeader.IsActive`.
- An inactive/retired header contributes no price lines.
- Resolution then falls through to the next eligible source.

**Current old-service test**
- `PriceResolution_RetiredPriceList_IsTolerated_AndFallsThrough()` currently expects the old `ResolveItemPriceAsync(...)` loader to use the retired list's RM9 line.

**Required correction**
- When `ResolveItemPriceAsync(...)` is unified with live-document pricing, this test MUST be updated.
- Using the existing fixture where item default is RM14:
  - inactive PL2 RM9 MUST NOT be used;
  - resolution MUST fall through to item default RM14;
  - source MUST be Item Default / existing legacy item-selling-price source label.

## 6. Company pricing engine is correct; admin wording is the confusing part

**Verified files**
- `ErpWeb.Core/Sales/SaCompanyPriceMethod.cs`
- `ErpWeb.UI/Admin/AdminCompany.razor`
- `ErpWeb.UI/Admin/AdminCompany.razor.cs`

**Current behavior**
- Four valid company modes already exist.
- Order is fixed by specificity.
- Null/blank/unknown normalizes to the full chain.
- Item default is always the final fallback.

**Required behavior**
- Keep tokens and source chains unchanged.
- Improve operator-facing wording from abstract "method" language to clear "price priority" language.

## 7. Price traceability and manual override controls already exist

**Verified existing behavior**
- Quotation, SO, DO and Invoice already call `ResolveLinePricingAsync(...)`.
- Existing transaction UI already shows `Price source: ...`.
- `PricingSource` / `PricingRef` already persist.
- Existing services enforce `SaPriceOverridePolicy`.
- Quotation/SO/Invoice already retain `OriginalUnitPrice` / `OverrideReason`.
- `PriceInquiry` already displays the full pricing ladder.

**Required behavior**
- Reuse these features.
- MUST NOT create another price-trace popup, pricing engine, override framework or duplicate audit mechanism.

# Scope / Non-Goals

## In Scope

1. Remove `SaCust.PriceMethod` from active pricing decisions.
2. Remove customer Price Method UI.
3. Ensure blank and copied new customers do not intentionally receive legacy PriceMethod.
4. Preserve existing legacy values on edits to existing customers.
5. Make `Company.SalesPriceMethod` the only active source-eligibility selector.
6. Remove DEALER short-circuit from both resolver and explainer behavior.
7. Unify `ResolveItemPriceAsync(...)` with the live base-price context.
8. Clarify Company Admin pricing labels/help.
9. Update tests to pin all corrected behavior.

## Non-Goals

MUST NOT:

- add SAP-style condition pricing;
- add a new dealer-price field/table/engine;
- add a new pricing service;
- use last invoice price as automatic authority;
- choose the lowest/cheapest candidate;
- reorder source specificity;
- change discount formulas;
- change tax-inclusive/exclusive calculation;
- change quotation/SO/DO/invoice calculation logic;
- change `SaPriceOverridePolicy`;
- change posting/unposting/rollback;
- change stock quantity or inventory valuation;
- change inventory/production costing;
- change GL/accounting;
- change SST/e-Invoice logic;
- remove or migrate `SaCust.PriceMethod`;
- redesign existing Price Inquiry or transaction price-source displays.

# Exact Files

## MUST Change — Core

1. `ErpWeb.Core/Sales/SaItemFamilyPricing.cs`
2. `ErpWeb.Core/Sales/SaItemFamilyPriceExplanation.cs`
3. `ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.Resolve.cs`
4. `ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.PriceSources.cs`
5. `ErpWeb.Core/Sales/SaCompanyPriceMethod.cs`

## MUST Change — UI

6. `ErpWeb.UI/Sales/Masters/SaCustEntry.razor`
7. `ErpWeb.UI/Sales/Masters/SaCustEntry.razor.cs`
8. `ErpWeb.UI/Admin/AdminCompany.razor`
9. `ErpWeb.UI/Admin/AdminCompany.razor.cs`

## MUST Change — Tests

10. `ErpWeb.Tests/Sales/Master/SaItemFamilyPricingContractTests.cs`
11. `ErpWeb.Tests/Sales/Master/SaItemFamilyResolutionServiceTests.cs`
12. `ErpWeb.Tests/Sales/Transaction/SaLinePricingConsumerTests.cs`
13. `ErpWeb.Tests/Sales/Master/SaCompanyPriceMethodTests.cs`

## Documentation-Only If Needed

14. `ErpWeb.Core/Sales/ISaSalesRefService.cs`
- Only update XML comments if they still describe the old duplicated/legacy authority.
- Do not change method signatures.

## Explicitly Preserve / Do Not Schema-Edit

- `ErpWeb.Model/Entities/CustomerProfile/SaCust.cs`
- `ErpWeb.Model/Configurations/Sales/SaCustConfiguration.cs`
- `ErpWeb.Core/Sales/SaCustResults.cs`
- `ErpWeb.Core/Sales/SaCustService.cs`
- `ErpWeb.Core/Sales/SaCustPaymentOptions.cs`
- `ErpWeb.Model/Entities/Company.cs`
- `ErpWeb.Model/Configurations/CompanyConfiguration.cs`
- sales transaction entity schemas;
- sales posting services;
- inventory/costing schemas.

# Exact Changes

## 1. `ErpWeb.Core/Sales/SaItemFamilyPricing.cs`

### `SaItemFamilyPriceResolver.Resolve(...)`

Remove the active customer PriceMethod gate.

Current conceptual flow:

```text
normalize item/UOM
if legacy customer PriceMethod is DEALER:
    block
walk Company.SalesPriceMethod sources
```

Required flow:

```text
normalize item/UOM
walk SaCompanyPriceMethod.ResolveSources(request.CompanyPriceMethod)
for each eligible source:
    ResolveSource(...)
    if candidate is blocking:
        fail closed
    if candidate found:
        return it
no source found:
    block with existing no-price behavior
```

MUST preserve:

1. `CustomerItem`
2. `CustomerPriceList`
3. `CustomerGroupPriceList`
4. `ItemDefault`

MUST NOT choose cheapest price.

### `SaItemFamilyPriceRequest.PriceMethod`

Keep the property for compatibility in this change.

Update its XML comment to state:

- legacy customer metadata;
- ignored by active pricing resolution;
- retained to avoid an unrelated public-contract cleanup.

### `IsDealerPriceMethod(...)`

Do not use this helper from active resolver or explainer logic.

It MAY remain as legacy compatibility code in this change if removing it would create unnecessary API/test churn.

The Coding Agent MUST NOT introduce any new runtime call to it.

## 2. `ErpWeb.Core/Sales/SaItemFamilyPriceExplanation.cs`

### `SaItemFamilyPriceExplainer.Explain(...)`

Remove:

- `dealer = IsDealerPriceMethod(...)`;
- the branch that marks every level not applicable for dealer customers.

Required ladder logic:

```text
mode = Normalize(request.CompanyPriceMethod)
eligible = ResolveSources(mode)

for every fixed level:
    if not eligible:
        report excluded-by-company-method
        continue

    if an earlier level already decided the result:
        report not reached / earlier source won
        continue

    result = ResolveSource(level, request, candidates)

    if blocking:
        record blocking result
        mark decision complete
    else if found:
        record winning result
        mark decision complete
    else:
        record rejection reason
```

MUST preserve all existing explanation semantics except legacy DEALER blocking.

Price Inquiry MUST agree with the pure resolver for equivalent inputs.

## 3. `ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.PriceSources.cs`

### `LoadPriceContextAsync(...)`

Keep this as the authoritative base-price context loader.

MUST continue to:

- validate company context;
- validate customer/item/UOM;
- load customer in the active company only;
- read `Company.SalesPriceMethod` server-side;
- resolve customer group price list only when eligible;
- load candidates only for eligible source levels;
- enforce active price-list header through `LoadPriceListCandidatesAsync(...)`;
- include date/band/currency fields;
- load item default only when eligible.

Change request construction so active pricing does not depend on customer PriceMethod.

Preferred:

```csharp
PriceMethod = null // or omit initializer
```

Do not copy `customer.PriceMethod` into the authoritative price request.

`CompanyPriceMethod` MUST still be populated from `GetSalesPriceMethodAsync(...)`.

### `LoadPriceListCandidatesAsync(...)`

Do not change its active-header filter.

`IvCustPriceGroup.IsActive == false` MUST continue to yield no price-list candidates and allow normal fallback to the next source.

## 4. `ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.Resolve.cs`

### `ResolveItemPriceAsync(...)`

Remove the duplicated base-price loading path.

MUST keep the public method signature unchanged.

Construct a `SaLinePricingRequest` using:

- `CustCode`;
- `ICode`;
- `UOM`;
- `Qty`;
- `DocDate`;
- `DocumentCurrency`.

Then call the existing private `LoadPriceContextAsync(...)`.

Required flow:

```text
validate request != null

lineRequest = new SaLinePricingRequest
{
    CustCode = request.CustCode,
    ICode = request.ICode,
    UOM = request.UOM,
    Qty = request.Qty,
    DocDate = request.DocDate,
    DocumentCurrency = request.DocumentCurrency
}

context = await LoadPriceContextAsync(lineRequest, cancellationToken)

if context failed:
    return equivalent error

resolution = SaItemFamilyPriceResolver.Resolve(
    context.Data.Request,
    context.Data.Candidates)

if resolution not found:
    return Validation failure using resolution.Message

return Ok(resolution)
```

MUST NOT call `ResolveLinePricingAsync(...)`, because that method additionally performs:

- tax-basis conversion;
- discount resolution.

`ResolveItemPriceAsync(...)` remains a **base tax-exclusive price resolver**.

After this change it MUST obey the same:

- company source eligibility;
- customer/group lists;
- active list headers;
- quantity bands;
- date windows;
- currency matching;
- item fallback

as live document pricing.

Remove obsolete comments stating dealer-mode pricing intentionally fails.

## 5. `ErpWeb.Core/Sales/SaCompanyPriceMethod.cs`

Do not change stored tokens:

- `CUSTOMER_ITEM_AND_LIST`
- `CUSTOMER_ITEM_ONLY`
- `PRICE_LIST_ONLY`
- `ITEM_DEFAULT_ONLY`

Do not change:

- `Normalize(...)`;
- source arrays;
- `ResolveSources(...)`;
- `IsEligible(...)`;
- fixed priority.

Update `Describe(...)` operator text only.

Recommended wording:

- Full/default:
  `Customer special -> customer price list -> group price list -> item selling price`
- Customer item:
  `Customer special -> item selling price`
- Price-list:
  `Customer price list -> group price list -> item selling price`
- Item only:
  `Item selling price only`

## 6. `ErpWeb.UI/Admin/AdminCompany.razor`

Change caption:

`Sales Pricing Method`

to:

`Sales Price Priority`

Update help text to clearly state:

`Controls which price sources may apply. The order is fixed from the most specific customer price to the general item price. Standard is recommended for most companies.`

Do not add another setting.

## 7. `ErpWeb.UI/Admin/AdminCompany.razor.cs`

Keep option values unchanged.

Use clear display labels:

| Token | UI label |
|---|---|
| `CUSTOMER_ITEM_AND_LIST` | `Standard — Customer special, customer/group price list, then item price (recommended)` |
| `CUSTOMER_ITEM_ONLY` | `Customer special, then item price` |
| `PRICE_LIST_ONLY` | `Customer/group price list, then item price` |
| `ITEM_DEFAULT_ONLY` | `Item selling price only` |

Preserve the existing narrowing confirmation for `ITEM_DEFAULT_ONLY`.

Do not alter the persisted token.

## 8. `ErpWeb.UI/Sales/Masters/SaCustEntry.razor`

Remove active/read-only presentation of customer PriceMethod:

- remove `@Detail("Price method", Model.PriceMethod)`;
- remove `FOLLOW SELLING PRICE X DISCOUNT` radio;
- remove `FOLLOW DEFAULT DEALER PRICE` radio;
- remove the Price Method label/container.

Keep Price Group:

`Model.CustPriceCode`

Add concise help beside Price Group:

`Optional customer price list. When allowed by the company's Sales Price Priority, it is checked before the customer's group price list and item selling price.`

Do not add:

- dealer checkbox;
- dealer price field;
- extra priority selector.

## 9. `ErpWeb.UI/Sales/Masters/SaCustEntry.razor.cs`

Remove UI-only members:

- `IsPriceDealer`;
- `IsPriceSelling`;
- `SetPriceMethod(...)`.

### Blank new customer

In `CreateBlank()` remove:

```csharp
PriceMethod = SaCustPaymentOptions.PriceSelling
```

Result: blank new customers carry `PriceMethod == null`.

### Copy From — mandatory correction

Current new-copy flow:

```csharp
Model = Clone(copyResult.Data);
Model.CustCode = string.Empty;
...
```

Immediately after cloning/clearing the identity, also execute:

```csharp
Model.PriceMethod = null;
```

This is required so a newly copied customer does not inherit obsolete:

- `FOLLOW SELLING PRICE X DISCOUNT`;
- `FOLLOW DEFAULT DEALER PRICE`.

Do NOT clear:

- `CustPriceCode`;
- `CustGroupCode`;
- other intentional copied customer settings

unless existing Copy From rules already do so.

### Existing customer edit

Continue cloning/loading `PriceMethod` for existing customers.

Do not change `SaCustService` mapping.

This preserves existing legacy stored values when the user edits unrelated fields.

# Database Changes

**None.**

Verified existing schema:

- `SaCust.PriceMethod` remains nullable, max length 50.
- `SaCust.CustPriceCode` remains the customer price-list assignment.
- `Company.SalesPriceMethod` remains the company price-priority token, max length 32.

MUST NOT:

- drop `SaCust.PriceMethod`;
- rename the column;
- alter type/length/nullability;
- backfill or clear existing values;
- create EF migration;
- create SQL migration/preflight;
- change indexes/constraints.

Legacy customer PriceMethod becomes **stored but non-authoritative metadata**.

# Transaction / Execution Order

For live sales line pricing:

1. Validate company scope.
2. Validate customer/item/UOM.
3. Read `Company.SalesPriceMethod` server-side.
4. Resolve customer `CustPriceCode`.
5. Resolve customer group's `CustPriceCode` only when group-list level is eligible.
6. Load candidates only for eligible levels.
7. Walk fixed specificity order.
8. Fail closed on incompatible declared currency.
9. If no source prices the line, block; never substitute RM0.
10. In `ResolveLinePricingAsync(...)` only:
    - convert tax basis;
    - resolve discount after basis conversion.
11. Persist existing pricing provenance on document line.
12. Apply existing manual override permission/reason logic if price is edited.

`ResolveItemPriceAsync(...)` stops after base-price resolution.

`ExplainLinePriceAsync(...)` explains the same base-price decision and does not change data.

# Pricing Authority / Invariants

1. `Company.SalesPriceMethod` is the only active price-source eligibility authority.
2. `SaCust.PriceMethod` MUST NOT affect price.
3. Legacy DEALER metadata MUST NOT block a sale.
4. Customer Item Price MUST outrank customer price list when both are eligible.
5. Customer price list MUST outrank customer group price list.
6. Customer group price list MUST outrank item default.
7. Item default MUST remain the final fallback in every existing company mode.
8. System MUST select the first valid source by specificity, not the cheapest price.
9. Declared currency mismatch MUST remain fail-closed.
10. No price MUST remain a blocking result, never RM0.
11. UOM mismatch MUST NOT borrow another UOM's item-default price.
12. Inactive price-list headers MUST NOT contribute price lines.
13. Inactive/retired list assignment MUST fall through to the next valid eligible source.
14. Existing quantity-band/date-window ranking MUST remain unchanged.
15. Existing discount ordering MUST remain unchanged.
16. Existing tax basis calculation MUST remain unchanged.
17. Existing `PricingSource` / `PricingRef` persistence MUST remain unchanged.
18. Existing price override security/audit MUST remain unchanged.
19. `ResolveItemPriceAsync(...)`, `ResolveLinePricingAsync(...)` and `ExplainLinePriceAsync(...)` MUST identify the same base price/source for equivalent inputs.
20. Existing saved sales lines MUST NOT be automatically repriced.

Example specificity rule:

```text
Customer special      RM95
Customer price list   RM90
Item selling price   RM100

Selected price = RM95
```

RM90 MUST NOT win merely because it is cheaper.

# Rollback / Posting / Costing

No posting, rollback, stock, costing or accounting code changes are required.

MUST NOT modify:

- quotation/SO/DO/invoice posting flow;
- unpost/rollback;
- inventory ledger;
- stock balances;
- Moving Average/FIFO/Standard Cost;
- production costing;
- month-end;
- GL;
- e-Invoice posting/submission logic.

This change affects **price selection before line save/post only**.

Existing posted/historical line price remains the historical authority for that document.

# Concurrency / Locking

No new transaction or locking architecture.

Preserve:

- tenant/company filtering;
- existing EF `AsNoTracking()` pricing reads;
- existing document save transactions;
- existing RowVersion/concurrency behavior;
- no database writes during resolution/explanation.

# Tests

## 1. `ErpWeb.Tests/Sales/Master/SaItemFamilyPricingContractTests.cs`

### Replace dealer-block contract

Replace:

`E6_DealerCustomer_FailsClosed_AndDoesNotFallBackToSellingPrice`

with a test such as:

`LegacyDealerPriceMethod_DoesNotAffectCompanySourceWalk`

Input:
- `PriceMethod = FOLLOW DEFAULT DEALER PRICE`;
- Customer Item candidate = RM12.50;
- Item default = RM14;
- default company source chain.

Assert:
- `Found == true`;
- selected source = Customer Item;
- `UnitPrice == 12.50`;
- no dealer-block message.

The existing `DealerPriceMethod_IsDetectedCaseInsensitively` helper test MAY remain only if `IsDealerPriceMethod(...)` is intentionally kept as legacy compatibility code. It MUST NOT imply runtime authority.

Keep all existing MOQ/UOM/currency/date/quantity source tests.

## 2. `ErpWeb.Tests/Sales/Master/SaItemFamilyResolutionServiceTests.cs`

### Dealer test

Replace:

`PriceResolution_DealerCustomer_FailsClosed_FromTheStoredPriceMethod`

with:

`PriceResolution_LegacyDealerMetadata_DoesNotBlock`

Using the existing fixture where I1 item default is RM14:

Assert:
- result succeeds;
- UnitPrice = RM14 unless a more-specific valid source is deliberately seeded;
- source = item default/item selling price.

### Retired-list test — mandatory

Rename/update:

`PriceResolution_RetiredPriceList_IsTolerated_AndFallsThrough`

to:

`PriceResolution_RetiredPriceList_IsIgnored_AndFallsThroughToItemDefault`

Existing fixture:
- inactive PL2 contains RM9;
- customer points to PL2;
- item default I1 = RM14.

Assert:
- success;
- RM9 is NOT selected;
- UnitPrice = RM14;
- source = item default/item selling price.

### Unified-context coverage

Add focused tests proving `ResolveItemPriceAsync(...)` now respects:

- company `ITEM_DEFAULT_ONLY`;
- active list header;
- quantity band;
- effective date;
- customer group price list;
- currency mismatch.

At minimum, one equivalence test MUST compare `ResolveItemPriceAsync(...)` with `ResolveLinePricingAsync(...)` for an exclusive/no-discount line and assert:

- base UnitPrice equality;
- same pricing source;
- same PricingRef where applicable.

## 3. `ErpWeb.Tests/Sales/Transaction/SaLinePricingConsumerTests.cs`

### Dealer document test

Change DEALER1 seed to retain legacy:

`PriceMethod = SaCustPaymentOptions.PriceDealer`

and give the customer an actual list such as `PL1`.

Replace:

`DealerCustomer_FailsClosed_EvenThoughAnItemPriceExists`

with:

`LegacyDealerCustomer_UsesNormalCompanyPricingPriority`

Assert:
- succeeds;
- PL1 price is selected;
- source = `CustomerPriceList`;
- `PricingRef` identifies PL1.

This proves legacy metadata does not override the company chain.

### Dealer Price Inquiry test — mandatory

Replace:

`Explain_ReportsABlockAsTheOutcome`

for DEALER1 with:

`Explain_LegacyDealerCustomer_AgreesWithResolvedPrice`

Assert:
- explanation succeeds;
- `Found == true`;
- winning source/price equals `ResolveLinePricingAsync(...)` base price/source;
- levels are still reported normally;
- no dealer-specific "not applicable" reason exists.

### Existing resolver/explainer equivalence

Extend the existing:

`Explain_AgressWithTheResolvedPrice`

data set to include `DEALER1`.

This permanently pins the no-divergence invariant.

### Preserve all existing pricing regression tests

Keep coverage for:

- item default;
- customer special price;
- customer list;
- group list;
- own list beats group;
- company restricted modes;
- null/unknown company method;
- inactive item;
- missing price;
- UOM mismatch;
- quantity bands;
- effective dates;
- explicit/base currency preference;
- currency mismatch block;
- inclusive tax basis;
- discount pipeline;
- pricing source/ref.

## 4. `ErpWeb.Tests/Sales/Master/SaCompanyPriceMethodTests.cs`

Keep source-chain/token tests unchanged.

Add/update tests for operator-facing `Describe(...)` only.

Assert no source arrays/tokens changed.

## 5. Customer Master UI regression

No new UI test framework is required if the repository does not already provide one for this component.

Code review/build verification MUST prove:

### Blank new
`CreateBlank().PriceMethod == null`

### Copy From
after `Clone(copyResult.Data)`:
`Model.PriceMethod == null`

### Existing edit
an existing stored `PriceMethod` remains in `Model` and is persisted unchanged when editing unrelated data.

Do not introduce a new testing framework solely for these private UI helpers.

# Implementation Order

## STEP 0 — Branch drift gate

Before editing:

```text
git rev-parse HEAD
```

Expected reviewed baseline:

`6b729eb24be8f00e3b02ba200cb2e02e920d1e05`

If `production` has moved:
- diff all files listed in this plan against the reviewed commit;
- revalidate symbols/tests before applying changes;
- do not blindly apply stale line-level assumptions.

## STEP 1 — Update failing behavioral tests first

Update/add:
- pure DEALER metadata test;
- service DEALER test;
- retired price-list expected fallback test;
- line-pricing DEALER test;
- Price Inquiry DEALER agreement test;
- resolver/explainer equivalence case.

Run them and confirm they fail against current code for the intended reasons.

## STEP 2 — Remove legacy DEALER authority from pure resolution

Edit `SaItemFamilyPricing.cs`.

Remove active DEALER gate; preserve source selection.

## STEP 3 — Remove legacy DEALER authority from explanation

Edit `SaItemFamilyPriceExplanation.cs`.

Remove dealer special ladder branch.

Run pure + consumer tests.

## STEP 4 — Unify base-price service context

Refactor `ResolveItemPriceAsync(...)` to use `LoadPriceContextAsync(...)`.

Delete duplicated candidate-loading code from that method.

Run service tests, including inactive-header fallback.

## STEP 5 — Stop feeding customer PriceMethod to active context

Edit `LoadPriceContextAsync(...)`.

Do not assign `customer.PriceMethod` to the active request.

Run pricing contract/service/consumer tests.

## STEP 6 — Simplify Customer Master

Remove Price Method UI.

Remove blank new default.

Clear `Model.PriceMethod` in Copy From new-customer flow.

Preserve existing customer legacy value.

Build UI.

## STEP 7 — Improve Company Admin wording

Update only caption/help/display descriptions.

Do not change tokens/source arrays.

## STEP 8 — Focused Sales regression

Run:

```text
SaCompanyPriceMethodTests
SaItemFamilyPricingContractTests
SaItemFamilyResolutionServiceTests
SaLinePricingConsumerTests
SaCustServiceTests
SaQtServiceTests
SaSoServiceTests
SaDoServiceTests
SaInvoiceServiceTests
SalesCalcMatrixTests
SalesDocTotalsTests
```

## STEP 9 — Full test/build gate

Run complete `ErpWeb.Tests`.

Build the solution/projects used by production.

Implementation is not complete until focused tests, full regression and build succeed.

# Regression Areas

MUST verify:

- Customer Master new;
- Customer Master Copy From;
- Customer Master existing edit/view;
- Company Admin;
- Price Inquiry;
- Quotation line pricing;
- SO line pricing;
- DO line pricing;
- Invoice line pricing;
- customer item special price;
- customer direct price list;
- customer group price list;
- item default;
- inactive/retired price-list header;
- quantity bands;
- effective dates;
- currency;
- UOM;
- discounts;
- tax-inclusive pricing;
- manual override;
- price provenance.

MUST remain unchanged:

- document totals for unchanged source data;
- tax rounding;
- discount arithmetic;
- posting;
- inventory quantity;
- inventory valuation;
- production costing;
- accounting/GL;
- e-Invoice.

# Do-Not Rules

DO NOT:

- delete `SaCust.PriceMethod`;
- migrate/backfill/clear existing legacy values;
- create dealer-price schema;
- create another pricing engine/service;
- allow `SaCust.PriceMethod` to influence resolver or explainer;
- use inactive price-list headers;
- choose cheapest price;
- use last historical selling price automatically;
- reorder source priority;
- bypass `SaCompanyPriceMethod.ResolveSources(...)`;
- accept company price priority from browser input;
- change pricing-source persistence;
- change price override policy;
- change tax/discount formulas;
- reprice historical documents;
- change posting/costing/accounting;
- create a schema migration;
- perform unrelated refactoring.

# Acceptance Criteria

- [ ] Agent verifies it is implementing against current `production`; branch drift is reviewed before edits.
- [ ] `Company.SalesPriceMethod` is the only active source-eligibility authority.
- [ ] `SaCust.PriceMethod` remains stored but has no pricing effect.
- [ ] Legacy `FOLLOW DEFAULT DEALER PRICE` no longer blocks resolver.
- [ ] Legacy DEALER metadata no longer blocks Price Inquiry/explainer.
- [ ] Customer Master no longer displays or edits customer Price Method.
- [ ] Blank new customer has no default legacy PriceMethod.
- [ ] Copy From new customer explicitly clears copied legacy PriceMethod.
- [ ] Existing customer edits preserve the pre-existing stored legacy PriceMethod value.
- [ ] Default priority remains Customer Item -> Customer List -> Group List -> Item Default.
- [ ] Customer special price wins even if a later source is cheaper.
- [ ] Company restricted modes continue to remove only configured source levels without reordering remaining levels.
- [ ] `ResolveItemPriceAsync(...)` uses the same authoritative base-price context as live document pricing.
- [ ] Inactive/retired price-list header cannot supply a price.
- [ ] Existing retired-list service fixture now resolves RM14 item default, not inactive RM9 list price.
- [ ] Customer group list works through `ResolveItemPriceAsync(...)`.
- [ ] Quantity bands work through `ResolveItemPriceAsync(...)`.
- [ ] Effective dates work through `ResolveItemPriceAsync(...)`.
- [ ] Currency mismatch remains fail-closed.
- [ ] UOM mismatch remains fail-closed/no implicit item-default conversion.
- [ ] Missing price still blocks and never becomes RM0.
- [ ] `ResolveItemPriceAsync(...)`, `ResolveLinePricingAsync(...)` and `ExplainLinePriceAsync(...)` agree on base price/source for equivalent inputs.
- [ ] DEALER1 is included in resolver/explainer agreement regression.
- [ ] Existing sales transaction price-source hint remains.
- [ ] Existing `PricingSource` / `PricingRef` persistence remains.
- [ ] Existing `PRICE_OVERRIDE` permission/reason behavior remains.
- [ ] Tax/discount results remain unchanged.
- [ ] No EF/database migration is created.
- [ ] No posting/inventory/costing/production/procurement/GL code is changed.
- [ ] Focused pricing tests pass.
- [ ] Customer service regression tests pass.
- [ ] Quotation/SO/DO/Invoice service regression tests pass.
- [ ] Complete `ErpWeb.Tests` suite passes.
- [ ] Production solution/projects build without new errors.

# Agent Completion Report

Coding Agent MUST return:

1. current commit implemented against;
2. files changed;
3. exact pricing behavior changed;
4. confirmation that Customer Master Copy From clears legacy PriceMethod;
5. tests added/updated;
6. focused test results;
7. full test/build results;
8. confirmation that no migration was created;
9. confirmation that posting/costing/accounting paths were not modified;
10. any branch drift or unexpected dependency discovered.

# Approval Status

**APPROVED FOR IMPLEMENTATION**

**Implementation-readiness score: 10.0/10**

This approval applies to repository `mokth/net10projectTemplate`, branch `production`, verified at commit `6b729eb24be8f00e3b02ba200cb2e02e920d1e05`.
