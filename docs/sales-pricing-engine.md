# Sales Pricing Engine — plan of record

Status: **implemented 2026-09-17**. Supersedes the pricing sections of
`docs/sales-item-family-logic.md`, which documents the LEGACY adapter behaviour and the
pre-Phase-3 schema.

The full phased plan, its review record and its verification steps live in
`plans/plan-salesPricingEngine.prompt.md`.

e-Invoice (LHDN MyInvois) is explicitly out of scope.

---

## 1. One engine, three masters

There is no central `SalesPrice` table. A line price comes from one of three masters, walked in a
fixed order by one engine.

| Master | Role |
|---|---|
| `SaItemCust` | **Negotiated price** — permanent, undated, keyed (customer, item, UOM, MOQ) |
| `IvCustPrice` (+ `IvCustPriceGroup`) | **Price list line** — the dated / tiered / multi-currency vehicle |
| `IvStockMaster.SellingPrice` | **Item default** — the guaranteed last resort in every mode |

The customer-group default list is an **assignment fallback, not a price level of its own**:
`SaCust.CustPriceCode` wins, and `SaCustGroup.CustPriceCode` (Phase 5) is only reached when the
customer level produced nothing.

## 2. The four-stage pipeline

Strictly ordered, one owner per stage, and **only stage 4 may round**.

| # | Stage | Owner | Output |
|---|---|---|---|
| 1 | Price resolution | `ResolveLinePricingAsync` + the pure `Select*`/`Resolve` | ONE **tax-EXCLUSIVE** unit price + source |
| 2 | Tax-basis conversion | `SaItemFamilyPriceBasis.ToInclusive` | the value stored on the line (grossed up when `IsInclusive`) |
| 3 | Discount resolution | `ResolveItemDiscountAsync` + `SaItemFamilyDiscountResolver` | two engine slots + an unrounded per-unit discount |
| 4 | Document calculation | `SaInvoiceCalc.CalculateLine` / `CalculateHeader` | `Amount`, `NetAmount`, `TaxAmt`, header totals |

**The ordering is load-bearing.** Stage 3 must consume the STAGE 2 price, not the stage 1 price.
`SaInvoiceCalc.CalculateLine` derives the per-unit discount from the line's raw `UnitPrice` and only
then divides by `(1 + t)` on the inclusive path, so resolving the discount against the tax-exclusive
price would produce a discount the document engine then re-scales. Pinned by
`SaCompanyPriceMethodTests.PipelineOrder_...`.

Masters are tax-exclusive. The gross-up happens once, inside the orchestrator.

## 3. Company pricing mode

`Company.SalesPriceMethod` (nullable) selects which sources are **eligible**. NULL = the full chain.

| Mode token | Chain actually walked |
|---|---|
| `CUSTOMER_ITEM_AND_LIST` *(default)* | Customer Item → Cust List → Group List → **Item Default** |
| `CUSTOMER_ITEM_ONLY` | Customer Item → **Item Default** |
| `PRICE_LIST_ONLY` | Cust List → Group List → **Item Default** |
| `ITEM_DEFAULT_ONLY` | **Item Default** |

The setting **cannot reorder** the sources — the order is fixed by specificity, so a company cannot
make a group price beat a negotiated customer price. `Item Default` is never removable, because a
missing price must still resolve rather than become RM 0.00.

The mode is read **server-side** (`GetSalesPriceMethodAsync`); a value posted by a page is ignored.

## 4. Resolution order

0. `SaCust.PriceMethod` contains `DEALER` → **fail closed** (no price source exists; never mapped to
   selling price).
1. Read the company mode → build the ordered list of eligible sources.
2. **Customer item** — `SaItemCust` for (customer, item, UOM); highest `MOQ <= Qty` wins; price > 0;
   currency mismatch fails closed.
3. **Customer list**, then **group list** — an `IvCustPrice` line qualifies when the date window
   contains the document date, the quantity band contains the qty, the currency matches (or the line
   is blank = company base), and the header is active. Ordering: `MinQty DESC` → `ValidFrom DESC` →
   `ICode`. If lines exist for the item but **all** are another currency, the walk **BLOCKS** rather
   than falling through — that is fail-closed, not an error.
4. **Item default** — `IvStockMaster.SellingPrice` when the document UOM equals the item's
   `SellingUom`/`StdUom`, the item is active, and the price > 0. No UOM conversion, ever.
5. Nothing produced a price → **BLOCK** with an actionable message. Never `0`.

## 5. Price-list line columns (Phase 3)

`IvCustPrice` gained a **surrogate** `Id int IDENTITY` primary key plus
`ValidFrom`, `ValidTo`, `MinQty`, `MaxQty`, `CurrencyCode`.

**Why a surrogate key:** the natural key cannot contain `ValidFrom` and stay unique — two quantity
bands legitimately start on the SAME `ValidFrom` (a `MinQty 1` tier and a `MinQty 10` tier both
effective 2026-09-01). The natural key is therefore a separate unique index.

| Index | Columns | Purpose |
|---|---|---|
| `UX_IvCustPrice_BusinessKey` (unique) | CompanyCode, CustPriceCode, ICode, UOM, ValidFrom, MinQty, **CurrencyCode** | exactly one row per band start |
| `IX_IvCustPrice_Resolve` | CompanyCode, CustPriceCode, ICode, UOM, ValidFrom, ValidTo | the resolver's filters |
| `IX_IvCustPrice_Currency` | CompanyCode, CustPriceCode, ICode, UOM, CurrencyCode | the currency filter |

`ValidFrom` and `MinQty` are **NOT NULL** so the unique index can contain them — SQL Server treats
NULLs as equal in a unique index, which would admit only one undated row per key. The sentinel
`1900-01-01` means "always"; `MinQty 0` means "any quantity". Every pre-existing row migrated to both.

**Deliberate deviation from plan 3.2:** `CurrencyCode` IS in the unique index. Plan 3.2 says to leave
it out, but 3.4 requires a MYR tier and a USD tier on the same band to coexist, and those two
statements cannot both hold. Leaving currency out would silently make mixed-currency lists illegal.

### Overlap rule

A new or edited line is rejected when an existing line with the same (company, list, item, UOM,
currency) overlaps in **both** the quantity range AND the validity window. Either alone is legitimate:
bands that overlap in different periods are accepted, and windows that overlap over different bands
are accepted. Implemented once, in the service (`FindOverlappingPriceLines`), reusing
`SaItemFamilyRuleMatch.BandsOverlap`/`WindowsOverlap` so the price-list and discount rules cannot
drift apart.

## 6. Price override governance (Phase 4)

Rule, implemented once in `SaPriceOverridePolicy` and enforced **server-side** at each document
service's single prepare choke point:

- A caller that **echoes the resolved price back is NOT an override** — the ordinary path needs
  neither the permission nor a reason, and stores `NULL`.
- A price that **differs** from the resolved one needs `PRICE_OVERRIDE` **and** a reason. The engine
  price is retained in `OriginalUnitPrice`; the reason in `OverrideReason`.
- `NULL OriginalUnitPrice` means "never overridden".

`PRICE_OVERRIDE` is a built-in permission (constant + `PermissionCodes.All` + a
`PermissionType='Data'` seed row granted to `SA_SO`, `SA_DO`, `SA_INVOICE`, `SA_CN`, `SA_DN`).

### Which screens can override

| Screen | Editable price? | Override UI |
|---|---|---|
| Sales Order | yes | price gated on `CanOverridePrice`; reason required when changed |
| Invoice | yes | as above |
| Credit / Debit Note | yes | as above |
| Delivery Order | **no** | none — the DO popup has no price control |

### The copy rule, and why it is asymmetric

Provenance (`PricingSource`/`PricingRef`) is **copied verbatim** from SO→INV, DO→INV and INV→CDN; a
document never re-resolves a copied price.

The **override declaration is deliberately NOT copied** onto a derived document. Copying
`OriginalUnitPrice` would make an invoice or delivery order that merely *carries* an inherited price
demand `PRICE_OVERRIDE` from whoever bills or ships — a clerk who never made the decision. The audit
trail stays on the document that took it, and the copied price becomes the derived line's new
baseline. `FromDto` (loading an existing document) **does** carry the record, so an unrelated edit
cannot erase it.

## 7. Traceability

Each of `SaSoDetail`, `SaDoDetail`, `SaInvoiceDetail`, `SaCdnDetail` carries
`PricingSource nvarchar(40)`, `PricingRef nvarchar(60)`, `OriginalUnitPrice decimal(18,4)`,
`OverrideReason nvarchar(100)`.

`PricingRef` is a human-readable token (`PL1`, `MOQ=100`, `QTY 10-99`) so support can explain a price
without a join. `PricingSource` is explanatory only — the price is frozen on the line and is never
re-derived from it.

## 8. Price Inquiry and the explanation ladder

`SaItemFamilyPriceExplainer.Explain` (in `SaItemFamilyPriceExplanation.cs`) reports **every** level in
the fixed order — candidate, band, window, currency, applied or not, and why — **including levels the
company mode excluded**. That answers the top support question directly: "why didn't the customer's
special price apply?" → "this company is set to `PRICE_LIST_ONLY`".

It walks the same sources through the **same public `ResolveSource` entry point** as the resolver, so
an inquiry can never disagree with a document. Asserted by a theory across all five price sources
(`SaLinePricingConsumerTests.Explain_AgressWithTheResolvedPrice`).

Service: `ISaSalesRefService.ExplainLinePriceAsync`. Screen: `/sales/price-inquiry`
(`ErpWeb.UI/Sales/PriceInquiry.razor`). Read-only; menu `SA_PRICE_INQUIRY`.

## 9. Deployment

| Script | Purpose |
|---|---|
| `init-sales-item-family.sql` | creates the tables. **Includes the Phase 3 shape** for `IvCustPrice` |
| `alter-ivcustprice-phase3.sql` | migrates a PRE-Phase-3 `IvCustPrice` (adds columns, swaps the PK) |
| `alter-sa-detail-pricing-source.sql` | `PricingSource` / `PricingRef` on the four detail tables |
| `alter-sa-detail-price-override.sql` | `OriginalUnitPrice` / `OverrideReason` on the four detail tables |
| `alter-company-sales-price-method.sql` | `Company.SalesPriceMethod` |
| `alter-sacustgroup-pricelist.sql` | `SaCustGroup.CustPriceCode` |
| `init-sales-item-family-menu.sql` | the pricing menus plus `VIEW_PRICE` and `PRICE_OVERRIDE` |
| `init-sales-price-inquiry-menu.sql` | the Price Inquiry menu |

**Fresh install:** run `init-sales-item-family.sql`, then the `alter-*` scripts are unnecessary for
`IvCustPrice` — but ARE required for the four detail tables and `Company` if those tables predate the
phases. The init script prints a loud `STOP:` message when it finds an `IvCustPrice` without the
Phase 3 columns, so a divergent schema is never silent.

**Migrating an existing database:** run `alter-ivcustprice-phase3.sql` **before** using the pricing
screens.

Two batching traps, both already handled in the scripts: a batch that ADDS a column and then
REFERENCES it in the same batch fails to compile (SQL Server compiles the whole batch first), and
`RETURN` exits only its OWN batch — so a guard and the statement it guards must share a batch.

## 10. Deliberate exclusions

e-Invoice; a central `SalesPrice`/`PricingLevel` table; dealer pricing; a price-history ledger; UOM
conversion; branch-level pricing; customer-level `SaDisGroup`/`CustDiscount`; final-price caching;
promotional / contract / bundle / rebate pricing.

`SaQtDetail` **was** on this list and is no longer: a quotation is the first place a price is offered,
which is exactly where an override most needs recording. It carries `OriginalUnitPrice` /
`OverrideReason` and is enforced by the same `SaPriceOverridePolicy` as the four transaction
documents. See `scripts/alter-saqt-detail-price-override.sql`.

If a company ever needs a source ORDER different from the fixed specificity order, add a new named
mode — do not make the order configurable. A configurable order is how pricing becomes
unexplainable and support tickets become unfixable.
