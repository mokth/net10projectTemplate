# Sales Transaction Selling Price Info / "Why This Price?" Enhancement
## FINAL APPROVED FOR IMPLEMENTATION — AI Coding Agent Plan

**Repository:** `mokth/net10projectTemplate`  
**Target branch:** `productionv2`  
**Repo HEAD verified:** `e893d79b291da2088c7d3d1e17bb79bdeab0fe55`  
**Review date:** 2026-10-10  
**Status:** **APPROVED FOR IMPLEMENTATION**  
**Plan score:** **10 / 10**  
**Change type:** Additive Sales UI/traceability enhancement  
**Pricing engine change:** **NO**  
**Database schema change:** **NO**  
**Posting / costing / tax / discount logic change:** **NO**

---

# 0. Final implementation verdict

Add a small **information icon beside every user-visible selling/unit price in Sales transaction entry** so an operator can immediately understand:

```text
Where did this price come from?
Was it a Customer Special price?
Was it a Customer Price List?
Was it a Customer Group Price List?
Was it the Item Default?
Was this line manually overridden?
Was this price inherited from an earlier Sales document?
```

The enhancement must be implemented as **price provenance/explanation UI only**.

The existing Sales pricing engine remains authoritative:

```text
Customer Special
→ Customer Price List
→ Customer Group Price List
→ Item Default
```

Do not create a second pricing resolver.

Do not recalculate or replace a saved line price merely because the user opens the information popup.

The most important design rule is:

> **The info icon explains the price already recorded/resolved on the Sales line. It must never silently re-price the line.**

---

# 1. Repo findings — verified

The current branch already contains most of the data required for this enhancement.

## 1.1 Existing runtime price resolution

Sales transaction pages already call:

```text
ISaSalesRefService.ResolveLinePricingAsync(...)
```

and store:

```text
PricingSource
PricingRef
OriginalUnitPrice
OverrideReason
```

on their line view models / requests / DTOs.

The resolver returns:

```text
SaLinePricingResult.UnitPrice
SaLinePricingResult.PricingSource
SaLinePricingResult.PricingSourceToken
SaLinePricingResult.PricingSourceLabel
SaLinePricingResult.PricingRef
```

and already has:

```text
SaLinePricingResult.Describe()
```

The current transaction pages already display a basic text hint such as:

```text
Price source: Customer item price (MOQ=10)
```

through `_priceHint`.

This enhancement should replace/augment that raw hint with a consistent and user-friendly info experience.

---

# 2. Existing exact "Why this price?" engine — reuse, do not duplicate

The repo already contains:

```text
ISaSalesRefService.ExplainLinePriceAsync(...)
SaPriceExplanation
SaPriceExplanationLevel
SaItemFamilyPriceExplainer
```

The explanation engine uses the same source-selection helpers as runtime pricing.

It can explain:

```text
Customer Special
Customer Price List
Customer Group Price List
Item Default
```

including:

```text
eligible / excluded
applied / not applied
price
MOQ / quantity band
validity window
currency
reason a level did not apply
```

Therefore:

> **Do not implement new pricing rules inside the UI component.**

The UI may call `ExplainLinePriceAsync(...)` only for an explicitly labelled **current pricing rules check**.

---

# 3. Mandatory historical/provenance rule

There are two different concepts and the UI must not mix them.

## A. Recorded price information

This is what the Sales document line already carries:

```text
UnitPrice
PricingSource
PricingRef
OriginalUnitPrice
OverrideReason
```

This is the primary information shown by the icon.

It explains the line as stored/resolved by the document workflow.

## B. Current pricing-rule explanation

`ExplainLinePriceAsync(...)` reads the **current master data**.

For an old or posted document, current masters may have changed since the document's original price was decided.

Therefore the popup must never label a fresh explanation as:

```text
"Original price calculation"
```

unless the system truly has historical master snapshots sufficient to prove that.

Instead label the optional live call:

```text
CHECK CURRENT PRICING RULES
```

and show:

```text
Current pricing rules are evaluated from today's/current master setup
using this document's customer, item, UOM, quantity and document date.
They may differ from the price information originally recorded on this line.
```

This rule is mandatory.

---

# 4. Scope — Sales documents

## 4.1 Mandatory rollout

Add the info icon to Sales transactions where Unit Price is already visible to the operator.

### Quotation

```text
ErpWeb.UI/Sales/Transactions/SaQt.razor
ErpWeb.UI/Sales/Transactions/SaQt.razor.cs
```

Current:

```text
SaQtLineVm.UnitPrice
PricingSource
PricingRef
OriginalUnitPrice
OverrideReason
```

### Sales Order

```text
ErpWeb.UI/Sales/Transactions/SaSo.razor
ErpWeb.UI/Sales/Transactions/SaSo.razor.cs
```

Current:

```text
SaSoLineVm.UnitPrice
PricingSource
PricingRef
OriginalUnitPrice
OverrideReason
```

### Sales Invoice

```text
ErpWeb.UI/Sales/Transactions/SaInvoice.razor
ErpWeb.UI/Sales/Transactions/SaInvoice.razor.cs
```

Current:

```text
SaInvoiceLineVm.UnitPrice
PricingSource
PricingRef
OriginalUnitPrice
OverrideReason
```

Invoice can also inherit pricing provenance from:

```text
Sales Order
Delivery Order
```

without re-resolving it.

### Credit Note / Debit Note

One page handles both:

```text
ErpWeb.UI/Sales/Transactions/SaCdn.razor
ErpWeb.UI/Sales/Transactions/SaCdn.razor.cs
```

Current:

```text
SaCdnLineVm.UnitPrice
PricingSource
PricingRef
OriginalUnitPrice
OverrideReason
```

---

## 4.2 Delivery Order — deliberately DO NOT expose new price UI

Current:

```text
ErpWeb.UI/Sales/Transactions/SaDo.razor
ErpWeb.UI/Sales/Transactions/SaDo.razor.cs
```

The DO line VM carries:

```text
UnitPrice
PricingSource
PricingRef
OriginalUnitPrice
OverrideReason
```

but the current DO grid and popup deliberately do **not** expose an editable/displayed Unit Price.

The code explicitly documents that:

```text
the Delivery Order popup has no editable price
and the screen does not create a price override
```

DO is primarily a fulfilment/logistics screen.

Therefore:

> **Do not add a selling-price column or a price popup to DO solely for this enhancement.**

This avoids:

- exposing commercial price information to delivery/store users who do not currently see it;
- changing the current DO UX;
- introducing a new price-visibility permission issue.

The existing internal provenance must continue to round-trip untouched.

If a future approved requirement adds visible price to DO, the same shared Price Info component must then be used.

---

## 4.3 Delivery Request — out of scope

Current Delivery Request entry has no selling-price field.

Do not add one.

```text
ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor
```

---

# 5. User experience contract

Normal grid display:

```text
Item       Qty     UOM     Unit Price
FG001      10      PCS     85.0000  ⓘ
```

Do not add permanent columns such as:

```text
Pricing Method
Price Priority
Price Rule
Price List
Pricing Ref
MOQ
Price Origin
```

to every Sales transaction grid.

Those details belong behind the info icon.

The normal Sales entry must remain clean.

---

# 6. Info icon behavior

Use a small text-style icon:

```text
ⓘ
```

or repository-consistent Font Awesome:

```text
fa-solid fa-circle-info
```

Requirements:

- keyboard accessible;
- `title="Price information"` or equivalent accessible label;
- no accidental line edit;
- no row selection side effect;
- no automatic server query during grid render;
- no pricing recalculation;
- no document dirty-state change.

Click:

```text
ⓘ
→ open Selling Price Information popup
```

---

# 7. One shared popup component — mandatory

Do not build five slightly different pricing popups.

Add one reusable component.

Recommended:

```text
ErpWeb.UI/Sales/Transactions/SaPriceInfoDialog.razor
ErpWeb.UI/Sales/Transactions/SaPriceInfoDialog.razor.cs
ErpWeb.UI/Sales/Transactions/SaPriceInfoDialog.razor.css
```

Alternative naming is acceptable if repo conventions strongly prefer another name.

Use **one dialog instance per transaction page**, not one `DxPopup` per grid row.

Reason:

```text
20-100 grid rows
× one hidden popup each
= unnecessary component/UI overhead
```

Correct pattern:

```text
row icon
→ page OpenPriceInfo(line)
→ set one PriceInfoContext
→ one shared SaPriceInfoDialog opens
```

---

# 8. Shared context model

Add a small presentation context.

Recommended file:

```text
ErpWeb.UI/Sales/Transactions/SaPriceInfoContext.cs
```

Suggested contract:

```csharp
public sealed class SaPriceInfoContext
{
    public string DocumentType { get; init; } = string.Empty;
    public string? DocumentNo { get; init; }
    public int? LineNo { get; init; }

    public string CustCode { get; init; } = string.Empty;
    public string ItemCode { get; init; } = string.Empty;
    public string? ItemDescription { get; init; }
    public string Uom { get; init; } = string.Empty;
    public decimal Qty { get; init; }
    public DateTime DocDate { get; init; }
    public string? Currency { get; init; }

    public decimal UnitPrice { get; init; }
    public string? PricingSource { get; init; }
    public string? PricingRef { get; init; }

    public decimal? OriginalUnitPrice { get; init; }
    public string? OverrideReason { get; init; }

    public bool IsInclusive { get; init; }
    public decimal TaxPercent { get; init; }

    public string? SourceDocumentType { get; init; }
    public string? SourceDocumentNo { get; init; }
    public int? SourceDocumentLine { get; init; }
}
```

Do not include mutable entity references.

The popup must be read-only.

---

# 9. Friendly source labels

Persisted tokens are currently:

```text
CUSTOMER_ITEM
CUSTOMER_PRICE_LIST
CUSTOMER_GROUP_PRICE_LIST
ITEM_DEFAULT
```

Do not display raw tokens to normal users.

UI labels:

```text
CUSTOMER_ITEM
→ Customer Special

CUSTOMER_PRICE_LIST
→ Customer Price List

CUSTOMER_GROUP_PRICE_LIST
→ Customer Group Price List

ITEM_DEFAULT
→ Item Default
```

Unknown/nonblank token:

```text
Price source: Recorded source (<token>)
```

Do not crash or guess a different source.

Blank token:

```text
Price source: Not recorded
```

This is important for older/legacy document lines.

Keep the existing persisted token values unchanged.

Do not rename DB values.

---

# 10. Basic popup — no server call required

Opening the popup must first use the line's already available fields.

Example:

```text
SELLING PRICE INFORMATION

Current line price
RM 85.0000

Price source
Customer Special

Price reference
MOQ=10

Customer
CUST001

Item
FG001 · Finished Good 001

UOM / Quantity
PCS · 10

Document date
10/10/2026
```

If source:

```text
CUSTOMER_PRICE_LIST
```

example:

```text
Price source
Customer Price List

Price reference
DEALER QTY 10+
```

If source:

```text
CUSTOMER_GROUP_PRICE_LIST
```

example:

```text
Price source
Customer Group Price List

Price reference
DEALER QTY 10+
```

If:

```text
ITEM_DEFAULT
```

example:

```text
Price source
Item Default

Price reference
Standard item selling price
```

No server request is required to show this basic information.

---

# 11. Manual override states

The popup must clearly distinguish four cases.

## 11.1 Normal system-resolved price

Condition conceptually:

```text
PricingSource exists
AND
OriginalUnitPrice is null or equals UnitPrice
AND
no meaningful OverrideReason
```

Display:

```text
Price status
System resolved
```

---

## 11.2 Resolved price manually overridden on this document

Condition:

```text
OriginalUnitPrice has value
AND
OriginalUnitPrice != UnitPrice
AND
PricingSource is not blank
```

Display prominently:

```text
PRICE OVERRIDDEN

System resolved price
RM 85.0000

Current line price
RM 82.0000

Difference
-RM 3.0000 (-3.53%)

Original source
Customer Special

Override reason
Special approval for customer
```

Use warning styling, not error styling.

The override was already authorised by the existing `PRICE_OVERRIDE` behavior.

The info popup does not grant override rights.

---

## 11.3 Manual price because no system price was available

The current Sales Order flow can allow a permitted operator to enter a positive manual price when no system price is available.

Possible state:

```text
PricingSource is blank
OriginalUnitPrice == 0
UnitPrice > 0
OverrideReason is not blank
```

Display:

```text
Price status
Manual price

System price
No usable system price was available

Current line price
RM 50.0000

Reason
...
```

Do not mislabel this as:

```text
Item Default
```

or any other source.

---

## 11.4 Inherited price from an upstream document

Some downstream documents intentionally copy:

```text
UnitPrice
PricingSource
PricingRef
```

without re-resolving.

Verified example:

```text
SO → Invoice
DO → Invoice
```

and `OriginalUnitPrice` is intentionally not copied because the override decision belonged to the upstream document.

When source-document linkage is known, display:

```text
Price status
Inherited from source document

Source document
Sales Order SO000123 · Line 2

Recorded price source
Customer Price List

Price reference
DEALER QTY 10+
```

Do not classify:

```text
OriginalUnitPrice == null
```

as proof that the line was system-resolved directly on the current document.

---

# 12. Price column rendering

Replace plain price grid cells on mandatory pages with a template that preserves the field binding for sorting/metadata.

Concept:

```razor
<DxGridDataColumn FieldName="@nameof(SaSoLineVm.UnitPrice)"
                  Caption="Price"
                  Width="135px"
                  MinWidth="110"
                  DisplayFormat="n4"
                  TextAlignment="GridTextAlignment.Right">
    <CellDisplayTemplate>
        ...
        price + info button
    </CellDisplayTemplate>
</DxGridDataColumn>
```

Use the repository grid-readability skill.

Do not keep a 100px price column if the amount + info icon truncates.

Suggested semantic size:

```text
Amount
Width ~135px
MinWidth ~110
```

Exact width may be adjusted after runtime check.

---

# 13. Line edit popup integration

Where Unit Price is already visible/editable in the line popup:

```text
Quotation
Sales Order
Invoice
Credit/Debit Note
```

render:

```text
[ Unit Price editor ] [ ⓘ ]
```

using a flex wrapper.

The icon opens the same shared `SaPriceInfoDialog`.

Do not create a second explanation UI.

Current `_priceHint` inline paragraph should be removed from the normal operator UI after the icon/popup provides the same information cleanly.

It may remain temporarily during implementation but must not leave duplicated permanent UI such as:

```text
Unit Price  RM85 ⓘ
Price source: Customer item price (MOQ=10)
```

Final UX should be compact.

---

# 14. No repricing on info click

This is a hard guardrail.

`OpenPriceInfo(...)` must not call:

```text
ResolveLinePricingAsync(...)
```

and must not assign:

```text
Popup.UnitPrice
PricingSource
PricingRef
discounts
tax
```

The icon is informational only.

Opening/closing the dialog must not:

```text
MarkDirty()
change RowVersion
change UnitPrice
change discount
change tax
change source
change reference
trigger save
```

---

# 15. Optional "Check current pricing rules"

Inside the popup add a secondary action:

```text
CHECK CURRENT PRICING RULES
```

This is useful for:

```text
Why would the system price this line today?
Why did Customer Special not apply?
Which level currently wins?
```

The action calls existing:

```text
ISaSalesRefService.ExplainLinePriceAsync(...)
```

using:

```text
CustCode
ItemCode
UOM
Qty
DocDate
Currency
TaxPercent
IsInclusive
```

No new resolver/service is allowed.

Do not call it until the user explicitly clicks the action.

---

# 16. Live explanation display

When loaded, show:

```text
CURRENT PRICING RULE CHECK
```

not:

```text
ORIGINAL CALCULATION
```

Display a ladder:

| Source | Result | Price | Basis / Reference | Explanation |
|---|---|---:|---|---|
| Customer Special | Used / Not used | ... | MOQ... | ... |
| Customer Price List | Used / Not used | ... | list/band/window | ... |
| Customer Group Price List | Used / Not used | ... | list/band/window | ... |
| Item Default | Used / Not used | ... | ... | ... |

Use:

```text
SaPriceExplanation.Levels
```

Do not rebuild source eligibility in Razor.

Use:

```text
level.SourceLabel
level.Eligible
level.Applied
level.UnitPrice
level.Ref
level.MinQty
level.MaxQty
level.ValidFrom
level.ValidTo
level.Currency
level.Reason
```

---

# 17. Current-rule comparison

After current explanation is loaded, compare it to the recorded line.

Example if same:

```text
Current rules still resolve to the recorded source.
```

Example if different:

```text
Current pricing rules now resolve differently.

Recorded line:
RM85.0000 · Customer Special

Current rule check:
RM90.0000 · Customer Price List
```

Add text:

```text
The document price has NOT been changed.
```

This is critical.

Do not offer an automatic:

```text
APPLY CURRENT PRICE
```

button in this enhancement.

Repricing must continue through the document's existing pricing-input-change flow.

---

# 18. Posted / historical document behavior

The icon remains useful in View mode.

For saved or posted documents:

1. show recorded line provenance immediately;
2. do not auto-call live explanation;
3. optionally allow `CHECK CURRENT PRICING RULES`;
4. clearly label any result as current master-data evaluation;
5. never overwrite the saved line.

This makes the icon useful for:

- support;
- audit investigation;
- customer price disputes;
- user training;
- understanding why two documents have different prices.

---

# 19. Quotation implementation

Files:

```text
SaQt.razor
SaQt.razor.cs
```

Changes:

1. replace plain grid Price cell with price + info icon;
2. add icon beside popup Unit Price editor;
3. add one page-level `SaPriceInfoDialog`;
4. add `OpenPriceInfo(SaQtLineVm line)`;
5. add `OpenPopupPriceInfo()` for unsaved/editing popup line;
6. build context from:
   - customer;
   - item;
   - UOM;
   - quantity;
   - document date;
   - currency;
   - price/provenance/override fields;
7. remove duplicated permanent `_priceHint` rendering after verification.

Do not change:

```text
ResolvePopupPriceAsync(...)
OnPopupQuantityChangedAsync(...)
OnPopupBasisChangedAsync(...)
OnPopupTaxChangedAsync(...)
price override logic
quotation conversion logic
```

---

# 20. Sales Order implementation

Files:

```text
SaSo.razor
SaSo.razor.cs
```

Same shared integration.

Special care:

Current Sales Order has explicit:

```text
OnPopupUnitPriceChangedAsync(...)
```

and manual fallback behavior when no system price is available.

The info icon must not interfere with:

```text
CanOverridePrice
_priceBlockMessage
OriginalUnitPrice
OverrideReason
SaPriceOverridePolicy
```

For manual fallback show:

```text
Manual price — no usable system price was available
```

Do not fabricate a PricingSource.

---

# 21. Invoice implementation

Files:

```text
SaInvoice.razor
SaInvoice.razor.cs
```

Same grid/editor icon.

Invoice has important inherited-price behavior.

Verified mappings:

```text
SaInvoiceLineVm.FromSalesOrder(...)
SaInvoiceLineVm.FromDeliveryOrder(...)
```

copy:

```text
UnitPrice
PricingSource
PricingRef
```

verbatim.

They deliberately set:

```text
OriginalUnitPrice = null
OverrideReason = null
```

because the override decision belongs to the upstream document.

The popup must respect this.

If line has:

```text
SoNo
```

display source Sales Order.

If:

```text
LinkDo == true
DoNo
DoLine
```

display source Delivery Order.

Do not tell the invoice user that it was directly re-resolved on Invoice when it was copied.

---

# 22. Credit Note / Debit Note implementation

Files:

```text
SaCdn.razor
SaCdn.razor.cs
```

One implementation serves both CN and DN.

Use:

```text
_type / ResolveFamily()
```

for document label.

If copied from Invoice:

```text
InvNo
SourceInvLine
```

are available.

Display:

```text
Source document: Invoice <InvNo> · Line <SourceInvLine>
```

when known.

Do not alter:

```text
return-stock behavior
invoice copy
credit/debit accounting behavior
tax calculation
posting
e-Invoice behavior
```

---

# 23. Delivery Order behavior — preserve

Do not add Unit Price display.

Do not add a grid info icon because there is no visible price cell.

Do not change:

```text
SaDoLineVm.FromSalesOrder(...)
```

provenance copy behavior.

Do not change its deliberate rule:

```text
OriginalUnitPrice = null
OverrideReason = null
```

when taking a price from Sales Order.

Do not require delivery/store staff to understand pricing to ship goods.

---

# 24. Source icon tooltip

Before click, use a short tooltip based only on existing line state.

Examples:

```text
Customer Special · MOQ=10
```

```text
Customer Price List · DEALER QTY 10+
```

```text
Customer Group Price List · DEALER
```

```text
Item Default
```

Override:

```text
Manual override · Customer Special
```

Manual no-system-price:

```text
Manual price
```

Legacy missing source:

```text
Price information
```

Tooltip must not query the server.

---

# 25. Unknown / legacy lines

Some older data may have:

```text
PricingSource = null
PricingRef = null
```

but a valid UnitPrice.

The popup must still work.

Display:

```text
Current line price
RM85.0000

Price source
Not recorded for this line
```

Optionally show:

```text
CHECK CURRENT PRICING RULES
```

Do not guess:

```text
Item Default
```

from the numeric value.

Two price sources can have the same amount.

---

# 26. Error handling

If current-rule check fails:

```text
Recorded Price Information
```

must remain visible.

Show secondary error only inside the current-rule section:

```text
Unable to check current pricing rules.
```

Do not turn the whole document into an error state.

Do not clear the line's recorded provenance.

---

# 27. Performance requirements

The icon enhancement must add **zero price-resolution queries during normal grid render**.

Required:

```text
grid load
→ no new explanation query

icon open
→ recorded context only, no query

CHECK CURRENT PRICING RULES
→ exactly one ExplainLinePriceAsync call
```

Do not:

- explain every line on page load;
- preload explanation for every grid row;
- call explanation from `CellDisplayTemplate`;
- call resolver merely to build a tooltip.

---

# 28. Security / permission behavior

Do not introduce a new pricing permission in this enhancement.

Rules:

- only add the icon where Unit Price is already visible in the current UI;
- do not make hidden DO prices visible;
- do not bypass existing document ACCESS;
- do not change `PRICE_OVERRIDE`;
- info popup is read-only;
- current-rule check uses the existing tenant-scoped `ExplainLinePriceAsync(...)`.

Do not expose cost/margin/profit information.

This is selling-price provenance only.

---

# 29. DevExpress binding requirements

Before implementation read:

```text
.agents/skills/devexpress-blazor-editor-binding-safety/skill.md
```

Do not modify existing price editor bindings in a way that reintroduces:

```text
ValueExpression / TextExpression
```

runtime errors.

Sales Order's explicit Unit Price callback is intentional.

Preserve it.

Do not convert it back to an unsafe two-way bind.

---

# 30. Grid readability requirements

Read:

```text
.agents/skills/devexpress-dxgrid-column-readability/skill.md
```

Price + icon must remain readable.

Use:

```text
ColumnResizeMode="GridColumnResizeMode.ColumnsContainer"
TextWrapEnabled="false"
horizontal scroll when required
```

Price columns should use amount-sized widths rather than squeezing the new icon into the existing narrow 100px cell.

Do not globally enable text wrapping.

---

# 31. Popup safety requirements

Read:

```text
.agents/skills/devexpress-popup-action-safety/skill.md
```

Price Info popup:

- read-only;
- one Close button;
- optional `CHECK CURRENT PRICING RULES`;
- no Save;
- no Apply Price;
- no Delete;
- no navigation that mutates the line.

If source-document navigation is added, it must be read-only navigation using existing authorized routes.

Source navigation is optional and not required for approval.

---

# 32. New files

Recommended:

```text
ErpWeb.UI/Sales/Transactions/SaPriceInfoDialog.razor
ErpWeb.UI/Sales/Transactions/SaPriceInfoDialog.razor.cs
ErpWeb.UI/Sales/Transactions/SaPriceInfoDialog.razor.css
ErpWeb.UI/Sales/Transactions/SaPriceInfoContext.cs
```

Optional pure helper if useful:

```text
ErpWeb.UI/Sales/Transactions/SaPriceInfoPresentation.cs
```

Responsibilities:

```text
friendly source label
override-state classification
difference amount / percentage
short tooltip
source document description
```

No EF/database logic.

---

# 33. Existing files to modify

Mandatory:

```text
ErpWeb.UI/Sales/Transactions/SaQt.razor
ErpWeb.UI/Sales/Transactions/SaQt.razor.cs

ErpWeb.UI/Sales/Transactions/SaSo.razor
ErpWeb.UI/Sales/Transactions/SaSo.razor.cs

ErpWeb.UI/Sales/Transactions/SaInvoice.razor
ErpWeb.UI/Sales/Transactions/SaInvoice.razor.cs

ErpWeb.UI/Sales/Transactions/SaCdn.razor
ErpWeb.UI/Sales/Transactions/SaCdn.razor.cs
```

Only modify page CSS files if needed after shared component CSS is insufficient.

Normally no changes required to:

```text
SaDo.*
SaDeliveryRequestEntry.*
SaSalesRefService.*
SaItemFamilyPricing.*
database entities
database mappings
migrations
```

No Core pricing change should be necessary.

---

# 34. Recommended context-builder pattern

Do not let each page manually construct source labels.

Each page only maps its line to the common context.

Example concept:

```csharp
private SaPriceInfoContext BuildPriceInfo(SaSoLineVm line) => new()
{
    DocumentType = "SO",
    DocumentNo = SoNoDisplay,
    LineNo = line.Line,

    CustCode = CustCode ?? string.Empty,
    ItemCode = line.ICode,
    ItemDescription = line.IDesc,
    Uom = line.StdUom ?? line.SellingUom ?? string.Empty,
    Qty = line.OrderQty,
    DocDate = SoDate,
    Currency = Currency,

    UnitPrice = line.UnitPrice,
    PricingSource = line.PricingSource,
    PricingRef = line.PricingRef,
    OriginalUnitPrice = line.OriginalUnitPrice,
    OverrideReason = line.OverrideReason,

    IsInclusive = line.IsInclusive,
    TaxPercent = ResolveTaxPercent(line.TaxGrCode)
};
```

Use exact page field names from current code.

Do not change a document's pricing UOM just for the info popup.

---

# 35. Mandatory automated tests

Prefer pure tests for shared presentation rules.

Recommended:

```text
ErpWeb.Tests/Sales/Transaction/SaPriceInfoPresentationTests.cs
```

If UI component testing infrastructure already exists, add component tests; otherwise do not introduce a heavy new test framework just for this task.

Tests:

1. `CUSTOMER_ITEM` → `Customer Special`;
2. `CUSTOMER_PRICE_LIST` → `Customer Price List`;
3. `CUSTOMER_GROUP_PRICE_LIST` → `Customer Group Price List`;
4. `ITEM_DEFAULT` → `Item Default`;
5. blank source → `Not recorded`;
6. unknown token does not throw;
7. normal resolved line classified `System resolved`;
8. different OriginalUnitPrice classified `Manual override`;
9. override difference amount correct;
10. override percent correct;
11. zero original price does not divide by zero;
12. manual no-system-price state detected;
13. inherited line is not falsely classified as direct current-document override;
14. tooltip uses persisted source/ref only;
15. tooltip does not require explanation result.

Existing pricing tests must remain green:

```text
SaCompanyPriceMethodTests
SaItemFamilyPricingContractTests
SaItemFamilyResolutionServiceTests
SaItemFamilyServiceTests
SaLinePricingConsumerTests
SaSoServiceTests
SaInvoiceServiceTests
```

---

# 36. Mandatory manual acceptance tests

## Scenario 1 — Customer Special

Customer/item has qualifying Customer Special.

Create SO line.

Expected:

```text
Unit Price 85.0000 ⓘ
```

Popup:

```text
Price source: Customer Special
Reference: MOQ=...
Current line price: 85.0000
```

No new pricing query until `CHECK CURRENT PRICING RULES`.

---

## Scenario 2 — Customer Price List

Expected:

```text
Price source: Customer Price List
Reference: <list code / band>
```

---

## Scenario 3 — Customer Group Price List

Expected:

```text
Price source: Customer Group Price List
```

Do not label it merely:

```text
Price List
```

---

## Scenario 4 — Item Default

Expected:

```text
Price source: Item Default
```

---

## Scenario 5 — manual override

System resolves:

```text
85.0000
```

authorised user changes:

```text
82.0000
```

Expected popup:

```text
PRICE OVERRIDDEN
Resolved: 85.0000
Current: 82.0000
Difference: -3.0000
Override reason: ...
Original source: ...
```

---

## Scenario 6 — no system price / manual SO price

Authorised Sales Order user enters manual positive price.

Expected:

```text
Price status: Manual price
Price source: No usable system source recorded
Reason: ...
```

Do not show Item Default.

---

## Scenario 7 — invoice from SO

SO line price/provenance copied to Invoice.

Expected:

```text
Price status: Inherited from source document
Source document: Sales Order ...
Recorded price source: ...
```

Clicking info does not reprice Invoice.

---

## Scenario 8 — invoice from DO

Expected:

```text
Source document: Delivery Order ...
```

PricingSource/PricingRef remain copied.

---

## Scenario 9 — CN copied from Invoice

Expected:

```text
Source document: Invoice ...
Recorded price source: ...
```

---

## Scenario 10 — old line without provenance

Existing legacy line:

```text
UnitPrice = 50
PricingSource = null
```

Expected:

```text
Current line price: 50
Price source: Not recorded
```

No guess.

---

## Scenario 11 — current rules have changed

Saved SO line:

```text
Recorded:
85 · Customer Special
```

Change current pricing master.

Open icon.

Expected first section still:

```text
85 · Customer Special
```

Click:

```text
CHECK CURRENT PRICING RULES
```

If current result:

```text
90 · Customer Price List
```

show both and:

```text
The document price has NOT been changed.
```

---

## Scenario 12 — DO

Open Delivery Order.

Expected:

```text
no new Unit Price column
no commercial price popup added
current logistics UX unchanged
```

---

## Scenario 13 — icon does not dirty document

Open/close info popup.

Expected:

```text
_isDirty unchanged
Save state unchanged
RowVersion unchanged
```

---

## Scenario 14 — performance

Open Sales Order with many lines.

Expected:

```text
no ExplainLinePriceAsync calls during grid rendering
```

Click one icon and then `CHECK CURRENT PRICING RULES`.

Expected:

```text
one explanation request for that selected line
```

---

# 37. Build / regression gate

Mandatory:

```powershell
dotnet build ErpWeb.slnx --nologo -v:q
```

Then:

```powershell
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --nologo
```

Razor compilation is mandatory because this enhancement modifies:

```text
DxGrid CellDisplayTemplate
DxButton
DxPopup
line editor templates
```

Unit tests alone are insufficient.

---

# 38. Non-regression rejection conditions

Reject implementation if it changes any of the following:

- price source precedence;
- `ResolveLinePricingAsync(...)`;
- `ExplainLinePriceAsync(...)` pricing behavior;
- customer special MOQ rules;
- Price List quantity bands;
- Price List dates;
- Price List currency matching;
- Item Default fallback;
- tax basis;
- discount logic;
- manual price override permission;
- override reason requirement;
- Sales Order save behavior;
- Quotation conversion;
- SO → DO provenance;
- SO → Invoice provenance;
- DO → Invoice provenance;
- Invoice → CN/DN copy behavior;
- DO price visibility;
- posting;
- rollback;
- stock movement;
- e-Invoice;
- costing;
- database schema.

---

# 39. Implementation order for AI Coding Agent

## Phase 1 — shared presentation model

1. add `SaPriceInfoContext`;
2. add friendly source-label mapping;
3. add override/manual/inherited-state helper;
4. add pure tests.

**Gate:** helper tests green.

---

## Phase 2 — shared dialog

1. create `SaPriceInfoDialog`;
2. recorded-price section;
3. source/ref display;
4. override display;
5. inherited source-document display;
6. optional current-rules button;
7. live explanation ladder;
8. loading/error states.

**Gate:** component compiles and opens without mutating parent document.

---

## Phase 3 — Sales Order first

Implement SO first because it has the richest pricing edge cases:

```text
system pricing
manual fallback
PRICE_OVERRIDE
explicit UnitPrice callback
```

Verify scenarios:

```text
1-6
13-14
```

before copying the pattern elsewhere.

---

## Phase 4 — Quotation

Use same component.

No resolver redesign.

---

## Phase 5 — Invoice

Add inherited-source handling:

```text
SO
DO
```

Verify no false override status.

---

## Phase 6 — Credit / Debit Note

Use same component.

Add Invoice source reference when available.

---

## Phase 7 — final regression

1. full build;
2. full tests;
3. all manual scenarios;
4. confirm DO unchanged;
5. confirm no migration;
6. confirm zero extra explanation requests on normal grid render.

---

# 40. AI agent stop conditions

Stop and report rather than guessing if current branch changes materially such that:

1. `PricingSource/PricingRef` are no longer persisted;
2. `OriginalUnitPrice/OverrideReason` semantics changed;
3. SO manual no-price fallback changed;
4. Invoice no longer copies source provenance from SO/DO;
5. CN/DN no longer carries source Invoice relationship;
6. transaction page price visibility changed;
7. `ExplainLinePriceAsync` no longer uses the authoritative resolver helpers;
8. implementation would require a pricing-engine change;
9. implementation would require exposing prices on DO.

Do not redesign pricing to satisfy an info-icon requirement.

---

# 41. Definition of Done

## User experience

- [ ] visible Sales transaction prices have ⓘ;
- [ ] normal grids remain clean;
- [ ] source uses business-friendly labels;
- [ ] user can understand where price came from;
- [ ] manual override clearly visible;
- [ ] no-system manual price clearly visible;
- [ ] inherited price clearly identified;
- [ ] legacy missing provenance handled safely;
- [ ] optional current-rule explanation clearly labelled as current.

## Safety

- [ ] icon click never reprices;
- [ ] icon click never marks dirty;
- [ ] no save/post action in popup;
- [ ] no automatic Apply Current Price;
- [ ] current rule comparison never overwrites recorded price;
- [ ] DO price visibility remains unchanged;
- [ ] override permission unchanged.

## Performance

- [ ] no explanation query during grid render;
- [ ] no explanation query merely opening popup;
- [ ] one explanation query only when requested;
- [ ] one popup instance per page.

## Architecture

- [ ] existing `ResolveLinePricingAsync` remains authoritative;
- [ ] existing `ExplainLinePriceAsync` reused;
- [ ] no duplicate pricing resolver;
- [ ] no database migration;
- [ ] no pricing source token changes.

## Regression

- [ ] solution build green;
- [ ] full tests green;
- [ ] Quotation pricing unchanged;
- [ ] Sales Order pricing unchanged;
- [ ] Invoice pricing unchanged;
- [ ] CN/DN pricing unchanged;
- [ ] DO behavior unchanged.

---

# 42. Final approval

## **APPROVED FOR IMPLEMENTATION — 10 / 10**

This enhancement is strongly recommended for the current three-source SME pricing design.

It solves the user-understanding problem without making Sales entry more complicated.

The normal user sees only:

```text
RM85.0000  ⓘ
```

and can ask:

```text
Why this price?
```

when needed.

The system continues to decide pricing automatically.

The enhancement is therefore:

- additive;
- user-friendly;
- low-risk;
- traceable;
- support-friendly;
- suitable for SME ERP usage;
- consistent with the current `productionv2` pricing architecture.

**AI Coding Agent instruction:** implement the info/traceability layer only. Preserve all existing Sales pricing, override, tax, discount, copy, posting and fulfilment behavior.
