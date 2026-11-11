# Sales Pricing G5 — CN commercial copy and Price Info (A7)

**Status:** **PASS** for G5 **A7**. **A6 remains BLOCKED.** **Phase B (B1–B4) not started.**  
**Class:** B costing-adjacent. No posting, rollback, pack formula, `IvQty`, FIFO/WAC, schema, `CurrRate`, CostPrice, or return-qty changes.  
**Implementer SHA:** `e24f54069316a346171fed7f14017c09d593cb95` (local `productionv2`). G1–G5 remain uncommitted.

## What changed

| File | Hunk |
|---|---|
| [`SaCdnService.CopyFromInvoiceAsync`](../ErpWeb.Core/Sales/SaCdnService.cs) | Still copies unit price, six % discounts, first amount slot, tax basis/group, `PricingSource`/`PricingRef`, `SourceInvLine`. **Nulls** `OriginalUnitPrice`/`OverrideReason` so a billing clerk does not need `PRICE_OVERRIDE`. Warns when invoice `ItemDiscAmount1 != 0`. Does not copy amount2 (no CN field). Does not write `CostPrice`. |
| [`SaCdnOperationResult`](../ErpWeb.Core/Sales/ISaCdnService.cs) | `WarningMessage` on successful copy. |
| CN picker UI | Surfaces the amount2 warning after copy. No extra click on ordinary CN create. |
| Price Info context/presentation/dialog | One-glance recorded agreed price: price / commercial UOM / source / discount / inherited-or-agreed state. Distinguishes recorded vs “what current rules would select today.” Auto-runs the current-rule check when the dialog opens. Invoice/CN Price Info uses `SaLinePricingTrigger.CommercialUom`. |
| Price Inquiry | Copy states it is a **current-rule** check, not a recorded document price. Item picker defaults to selling UOM. |

`SaInvoiceCalc`, Post/Rollback, pack/`IvQty`, `SaPriceOverridePolicy` rule itself, and CN entity schema were not edited.

## C14 / C15

- **C14:** posted invoice with an upstream override copies the agreed unit price and % discount. CN save **without** `PRICE_OVERRIDE` succeeds. Provenance is `InvNo` + `SourceInvLine`.
- **C15 (known limitation, DEFERRED / NEEDS APPROVAL — Phase B2):** invoice `ItemDiscAmount1 = 2` is **not** stored on CN. Copy warns. `CostPrice` stays 0 (not used as a hidden amount2). Manual review of such credits is required. Do not claim Invoice→CN is 100% commercially faithful.

## A6 / Phase B (not done)

Direct-new `UnitPrice == ResolveLinePricingAsync` remains **BLOCKED**.  
B1 DiscountMethod snapshot, B2 CN amount2 schema, B3 FOC/manual policy, B4 historic tax — **unauthorized**.

## Tests

```
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~SaCdnServiceTests|FullyQualifiedName~SaPriceInfoPresentationTests|FullyQualifiedName~SaLinePricingConsumerTests|FullyQualifiedName~SaItemFamilyPricingContractTests|FullyQualifiedName~SaCompanyPriceMethodTests|FullyQualifiedName~SaItemFamilyResolutionServiceTests|FullyQualifiedName~SaLinePricingTriggerTests|FullyQualifiedName~SaInvoiceFromDeliveryOrderTests|FullyQualifiedName~C12_so_linked|FullyQualifiedName~Linked_do_invoice_inherits_commercial|FullyQualifiedName~Submitted_zero_price_and_pack_gl_from_master|FullyQualifiedName~Pure_LinkDo_invoice_AddShipment_creates_no_batch"
```

**284 passed, 0 failed, 0 skipped.** Sqlite only. Isolated SQL Server P01–P08 still **UNVERIFIED**. Live picker UI was not exercised in a browser.

Physical snapshots still in the filter: `Submitted_zero_price_and_pack_gl_from_master`, `Pure_LinkDo_invoice_AddShipment_creates_no_batch`, plus existing CN `ReturnStock_*` cases in `SaCdnServiceTests`. No CostLayer/`IvTrxHistory` assertions were loosened.

C21: `ExplainLinePriceAsync` still succeeds when `VIEW_PRICE` is denied. Line resolution is not menu-gated.

**GO for independent G5 A7 review. NO-GO for Phase B. A6 is not part of this PASS.**
