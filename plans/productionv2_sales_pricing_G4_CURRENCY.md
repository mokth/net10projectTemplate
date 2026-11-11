# Sales Pricing G4 — currency policy for new-direct prices (A5)

**Status:** **PASS** for G4 **A5**. **A6 remains BLOCKED.** **G5 / Phase B not started.**  
**Class:** B costing-adjacent. No posting, rollback, pack formula, `IvQty`, FIFO/WAC, schema, `CurrRate`, or local-amount changes.  
**Implementer SHA:** `e24f54069316a346171fed7f14017c09d593cb95` (local `productionv2`). G1–G4 remain uncommitted.

## Compatibility audit (GO)

Existing callers do **not** depend on blank-as-wildcard for a supported FX workflow:

| Caller / data | Before A5 | After A5 |
|---|---|---|
| QT/SO/DO/Invoice/CN popup | Always send header `Currency` (UI default `"MYR"`, else customer/document snapshot) | Unchanged path; matching now uses company base instead of treating blank as any-currency |
| Price Inquiry blank currency (`NullText="Base currency"`) | Wildcard-matched any candidate | Effective document currency = `Company.CurrencyCode` (intended) |
| Blank `IvCustPrice.CurrencyCode` / blank `SaItemCust.Currency` | Matched any document currency | Treated as company base. MYR documents still match |
| CUST9 USD-only list vs MYR document | Already blocked | Still blocked |
| CUST10 blank + explicit MYR vs MYR document | Explicit MYR won | Unchanged |
| USD document + only item default / blank list | **Silently used MYR as USD** | **Blocks** with “conversion is not implicit” |
| Linked/saved documents | G2 skips live resolve | Persisted prices untouched |
| `CurrRate` / local amount / customer FX | Not in the resolver | Not edited |

No existing test asserted that a blank list may price a USD document. `SelectPriceListLine_MatchingCurrencyWinsOverABlankBaseCurrencyLine` already ranked blank as base behind explicit USD. Deploying A5 therefore does not interrupt signed MYR/blank-list contracts; it closes the MYR-as-USD hole named in v2.1 C06.

**Operator impact (intentional):** a new-direct USD document needs an explicit USD customer-item or list row. A blank/MYR master is no longer reused as dollars. Historical USD quote/SO/DO/invoice/CN lines keep their stored unit prices.

## What changed

| File | Hunk |
|---|---|
| [`SaItemFamilyPricing.cs`](../ErpWeb.Core/Sales/SaItemFamilyPricing.cs) | `CompanyBaseCurrency` on the request. `CurrencyMatches` is effective-currency equality (blank candidate = base, blank document = base, missing base fails closed). `SelectItemDefault` now currency-checks. `ExactCurrencyMatch` ranks explicit over blank against the effective document currency. No FX conversion. |
| [`SaSalesRefService.ItemFamily.PriceSources.cs`](../ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.PriceSources.cs) | Loads `Company.CurrencyCode` server-side with the pricing method. Missing/blank base fails the price request. Client cannot supply company base. |
| [`SaLinePricingResults.cs`](../ErpWeb.Core/Sales/SaLinePricingResults.cs) | Comment only: blank document currency is company base, not a wildcard. |

`SaInvoiceCalc`, `CurrRate`, `ResolveCurrencyRateAsync`, tax/cost formulas, `SaPriceOverridePolicy`, pack/`IvQty`, Post/Rollback, and schema were not edited.

## A6 (not done)

Direct-new `UnitPrice == ResolveLinePricingAsync` is **BLOCKED**. The repository still allows legitimate manual and zero (FOC) prices. No `SaPriceOverridePolicy` or `PrepareLines` override change in this gate. C16–C18 / C21–C23 were not opened.

## Tests

```
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~SaLinePricingConsumerTests|FullyQualifiedName~SaItemFamilyPricingContractTests|FullyQualifiedName~SaCompanyPriceMethodTests|FullyQualifiedName~SaItemFamilyResolutionServiceTests|FullyQualifiedName~SaLinePricingTriggerTests|FullyQualifiedName~SaInvoiceFromDeliveryOrderTests|FullyQualifiedName~C12_so_linked|FullyQualifiedName~Linked_do_invoice_inherits_commercial|FullyQualifiedName~Submitted_zero_price_and_pack_gl_from_master|FullyQualifiedName~Pure_LinkDo_invoice_AddShipment_creates_no_batch"
```

**214 passed, 0 failed, 0 skipped.** Sqlite only. Isolated SQL Server scratch still **UNVERIFIED**.

C06: USD direct does not receive MYR item default; explicit USD customer-item and USD list resolve; blank list + explicit USD prefers USD; blank list does not price a USD document; MYR + blank candidate still resolves; blank document currency uses company base; missing `Company.CurrencyCode` fails closed.

Physical snapshots: `Submitted_zero_price_and_pack_gl_from_master` and `Pure_LinkDo_invoice_AddShipment_creates_no_batch` still passed. Linked commercial copy (`Linked_do_invoice_inherits_commercial`, C12) still passed.

## Not done (next gate)

G5 A7 CN / Price Info UX.  
A6 direct-new override enforcement remains **BLOCKED** until a proven FOC/manual rule exists.  
Phase B unauthorized.  
Live picker UI was not exercised in a browser.

**GO for independent G4 A5 review. NO-GO for G5 until that review. A6 is not part of this PASS.**
