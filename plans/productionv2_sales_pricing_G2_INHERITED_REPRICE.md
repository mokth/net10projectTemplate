# Sales Pricing G2 — prevent inherited / saved-line live repricing

**Status:** **PASS** for G2 quantity/tax/basis live-resolve skip. **G3–G5 / Phase B not started.**  
**Class:** B costing-adjacent. No posting, rollback, pack, `IvQty`, FIFO/WAC, or schema changes.  
**Implementer SHA:** `e24f54069316a346171fed7f14017c09d593cb95` (local `productionv2`). G1 + G2 remain uncommitted.

## What changed

| File | Hunk |
|---|---|
| [`ErpWeb.Core/Sales/SaLinePricingTrigger.cs`](../ErpWeb.Core/Sales/SaLinePricingTrigger.cs) | Source identity = document keys (`SoNo`/`DoNo`/`SourceInvLine`), never `PricingSource`. Live resolve only for a **new direct** line that is not a manual override. |
| [`ErpWeb.UI/Sales/Transactions/SaInvoice.razor.cs`](../ErpWeb.UI/Sales/Transactions/SaInvoice.razor.cs) | Qty / tax / basis go through `ResolvePopupPriceOnQtyTaxOrBasisAsync`. Skip when `_editingLine != null`, `Popup.HasSource`, or `IsPriceOverridden()`. Bump `_priceApplySeq` on skip, EditLine, new line, picker add. **Item change still resolves.** |
| [`ErpWeb.UI/Sales/Transactions/SaDo.razor.cs`](../ErpWeb.UI/Sales/Transactions/SaDo.razor.cs) | Same skip on quantity (`HasSource` = SO identity). |
| [`ErpWeb.UI/Sales/Transactions/SaSo.razor.cs`](../ErpWeb.UI/Sales/Transactions/SaSo.razor.cs) / [`SaQt.razor.cs`](../ErpWeb.UI/Sales/Transactions/SaQt.razor.cs) | Skip on existing-line / override. QT→SO has no `QtNo`; C07/C19 are covered by `_editingLine != null`. |
| [`ErpWeb.UI/Sales/Transactions/SaCdn.razor.cs`](../ErpWeb.UI/Sales/Transactions/SaCdn.razor.cs) | Skip when invoice identity `HasInheritedSource(InvNo, SourceInvLine)` or existing line. |
| [`ErpWeb.UI/Sales/Transactions/SaPriceInfoPresentation.cs`](../ErpWeb.UI/Sales/Transactions/SaPriceInfoPresentation.cs) | Inherited tooltip includes document type and number (`Inherited from Delivery Order DO-1`). |

Quantity fields still update. `RecalcDocument` / `SaInvoiceCalc.CalculateLine` still recompute `Amount`/`TaxAmt`/`NetAmount` from the **kept** `UnitPrice`. Server `PrepareLines` still owns frozen pack / `StdQty`. Source allocation paths were not edited.

## Physical non-impact

- `Pure_LinkDo_invoice_AddShipment_creates_no_batch` still passed.
- `Submitted_zero_price_and_pack_gl_from_master` still passed (direct path unchanged).
- No `SaDoService` Post/Rollback hunks in this gate.

## Tests

```
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~SaLinePricingTriggerTests|FullyQualifiedName~SaInvoiceFromDeliveryOrderTests|FullyQualifiedName~SaPriceInfoPresentationTests|FullyQualifiedName~Billable_lines_project_source_commercial|FullyQualifiedName~Linked_do_invoice_inherits_commercial|FullyQualifiedName~SaDocPickerLinesTests|FullyQualifiedName~Pure_LinkDo_invoice_AddShipment_creates_no_batch|FullyQualifiedName~Submitted_zero_price_and_pack_gl_from_master"
```

**42 passed, 0 failed, 0 skipped.** Sqlite only. Isolated SQL Server scratch still **UNVERIFIED**.

C07: saved QT/SO lines skip live resolve (`_editingLine != null`). SO VM has no `QtNo`; quote consumption / persist path unchanged.  
C08: DO-from-SO is source-inherited; invoice-from-DO qty 40→25 keeps `UnitPrice` 10 and Recalc amount 400→250.  
C09: mixed-DO commercial mapping from G1 still in this filter.  
C19: saved direct invoice line does not live-resolve on qty; new add popup still may.  
C20 (slice): skip / EditLine / picker add increment `_priceApplySeq` so a stale live resolve cannot overwrite an inherited popup.

## Not done (next gate)

G3: discount class / failed lookup (see G3 amendments).  
G4–G5 / Phase B unauthorized.  
Live invoice picker UI was not exercised in a browser.  
Full C07 persist (QT RM10 → SO after master RM12) is skip-on-edit, not a new snapshot-import test.

**GO for independent G2 review. NO-GO for G3 until that review.**
