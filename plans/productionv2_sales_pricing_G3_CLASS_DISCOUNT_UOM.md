# Sales Pricing G3 — discount class, failed lookup, commercial UOM

**Status:** **PASS** for G3 A3/A4. **G4–G5 / Phase B not started.**  
**Class:** B costing-adjacent. No posting, rollback, pack formula, `IvQty`, FIFO/WAC, or schema changes.  
**Implementer SHA:** `e24f54069316a346171fed7f14017c09d593cb95` (local `productionv2`). G1–G3 remain uncommitted.

## What changed

| File | Hunk |
|---|---|
| [`SaItemFamilyPricing.cs`](../ErpWeb.Core/Sales/SaItemFamilyPricing.cs) | New directional `RuleMatchesActualClass`. `Applies` uses it. `ClassesOverlap` unchanged for save-time overlap. |
| [`SaSalesRefService.ItemFamily.PriceSources.cs`](../ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.PriceSources.cs) | Loads `IvStockMaster.IClassCode` server-side. Ignores `SaLinePricingRequest.IClass`. Failed discount lookup fails the whole price request. Qty≤0 skips the discount query (does not swallow Fail as 0%). Successful `None` still Ok. |
| [`SaLinePricingTrigger.cs`](../ErpWeb.Core/Sales/SaLinePricingTrigger.cs) | `CommercialUom(selling, std)` for live price lookup. |
| QT/SO/DO/Invoice/CN `ResolvePopupPriceAsync` | Direct lines send `SellingUom ?? StdUom`. Invoice `SellingUom` is lookup-only, not persisted. |
| [`SaInvoiceService.cs`](../ErpWeb.Core/Sales/SaInvoiceService.cs) ~2295 | SO-linked invoice no longer rejects when current master `SellingUom` differs from the SO snapshot. Frozen pack path unchanged. |

`SaInvoiceCalc` JOIN/SPLIT, slot order, rounding, `ResolveFrozenStdFactor`, and Post/Rollback were not edited.

## Qty≤0 contract

Popup qty can be 0 while typing. G3 skips `ResolveItemDiscountAsync` in that case and still returns the resolved price with zero discounts. Company/item context failures still fail the whole `ResolveLinePricingAsync`.

## Tests

```
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~SaLinePricingConsumerTests|FullyQualifiedName~SaItemFamilyPricingContractTests|FullyQualifiedName~SaLinePricingTriggerTests|FullyQualifiedName~SaInvoiceFromDeliveryOrderTests|FullyQualifiedName~C12_so_linked|FullyQualifiedName~Linked_do_invoice_inherits_commercial|FullyQualifiedName~Submitted_zero_price_and_pack_gl_from_master|FullyQualifiedName~Pure_LinkDo_invoice_AddShipment_creates_no_batch|FullyQualifiedName~SaItemFamilyResolutionServiceTests"
```

**155 passed, 0 failed, 0 skipped.** Sqlite only. Isolated SQL Server scratch still **UNVERIFIED**.

C03: rule `c1` matches item `C1`; `C1` does not match `C2`; blank rule matches any including blank actual; class-specific does not match blank actual; client `Classification`/`IClass` is ignored.  
C04: missing company context still fails the whole price request (`BlankCompanyCode_IsAnInvalidScope`); no-rule remains Ok.  
C05: I6 BOX selling / PCS stock prices at BOX 90; PCS request finds no price.  
C12: SO-linked PACK1 invoice after master SellingUom PCS and pack 10 keeps Qty=2, StdQty=24, UnitPrice=10. No `IvTrxHistory`.

Physical snapshots: `Submitted_zero_price_and_pack_gl_from_master` and `Pure_LinkDo_invoice_AddShipment_creates_no_batch` still passed.

## Not done (next gate)

G4 A5 currency for **new direct** prices.  
G5 / Phase B unauthorized.  
Live picker UI was not exercised in a browser.

**GO for independent G3 review. NO-GO for G4 until that review.**
