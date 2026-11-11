# Sales Pricing G1 — DO→Invoice commercial DTO

**Status:** **PASS** for G1 commercial transport. **G2–G5 / Phase B not started.**  
**Class:** B costing-adjacent. No posting, rollback, pack, `IvQty`, FIFO/WAC, or schema changes.  
**Implementer SHA:** `e24f54069316a346171fed7f14017c09d593cb95` (local `productionv2`). Sales production code matches origin `584411e` for this hunk.

## What changed

| File | Hunk |
|---|---|
| [`ErpWeb.Core/Sales/ISaDoService.cs`](../ErpWeb.Core/Sales/ISaDoService.cs) | `SaDoBillableLineDto` now carries six % slots, both amount slots, `IsInclusive`, `TaxGroup`, `OrderType`, display `StdUom`. |
| [`ErpWeb.Core/Sales/SaDoService.cs`](../ErpWeb.Core/Sales/SaDoService.cs) | `SearchBillableLinesCoreAsync` (~1618) copies those fields from `SaDoDetail`. **Only this initializer** in the file. |
| [`ErpWeb.UI/Sales/Transactions/SaInvoice.razor.cs`](../ErpWeb.UI/Sales/Transactions/SaInvoice.razor.cs) | `FromDeliveryOrder` copies commercial terms; nulls override. `OrderType` on VM, Clone, `ToRequest`, `FromDto`. `StdUom` display only. Does not copy `Amount`/`TaxAmt`/`NetAmount`/`StdQty`. |

`RecalcDocument` still computes money from qty + copied slots via existing `SaInvoiceCalc`. Server `PrepareLines` still owns frozen pack / `StdQty`.

## Physical non-impact

- Linked PACK1 invoice: master pack changed to 10 after DO snapshot 12; save kept **Qty=2, StdQty=24**. No `IvTrxHistory` on save.
- `Pure_LinkDo_invoice_AddShipment_creates_no_batch` still passed.
- `Submitted_zero_price_and_pack_gl_from_master` still passed (direct path unchanged).

## Tests

```
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~SaInvoiceFromDeliveryOrderTests|FullyQualifiedName~Billable_lines_project_source_commercial|FullyQualifiedName~Linked_do_invoice_inherits_commercial|FullyQualifiedName~Linked_do_invoice_persists_amount|FullyQualifiedName~SaDocPickerLinesTests|FullyQualifiedName~Submitted_zero_price_and_pack_gl_from_master|FullyQualifiedName~Pure_LinkDo_invoice_AddShipment_creates_no_batch|FullyQualifiedName~Mixed_invoice_resolves_linked_do|FullyQualifiedName~SaDocApplicationTests.Pure_LinkDo"
```

**16 passed, 0 failed, 0 skipped.** Sqlite only. Isolated SQL Server scratch still **UNVERIFIED**.

C09 mixed DOs: per-source price/discount/tax/order type, no averaging.  
C10: six percents copied; amount slots copied on mapping and on amounts-only persist (save still rejects mixed % + amount, existing rule).  
C11: `TaxGroup` → `TaxGrCode`, `IsInclusive` copied; tax amount calculated by existing engine.

## Not done (next gate)

G2: stop `OnPopupQuantityChangedAsync` from calling `ResolvePopupPriceAsync` on source-inherited lines.  
G3: discount class / failed lookup (see G3 amendments).  
Live invoice picker UI was not exercised in a browser.

**GO for independent G1 review. NO-GO for G2 until that review.**
