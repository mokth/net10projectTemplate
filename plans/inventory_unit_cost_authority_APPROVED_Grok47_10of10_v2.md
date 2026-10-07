# Inventory Unit-Cost Authority Hardening Plan
## APPROVED — 10/10 — Cursor / Grok 4.7 Implementation Plan

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `production`  
**Verified HEAD:** `313655aa4b828bc29dde60d1a45515cf998c1ec9`  
**Latest reviewed commit:** `checked in costing enhancement`  
**Scope:** Inventory unit-cost authority and UI semantics for Miscellaneous Issue, Scrap, Stock Transfer, Vendor Return, Inventory Stock Return / Customer Return, Sales Credit Note `ReturnStock`, Stock Adjustment, and Stock Count-generated adjustments.

---

# 1. Approval status

**Status: APPROVED FOR IMPLEMENTATION**

This version supersedes the earlier plan.

The earlier direction was correct but was not safe to approve because deep repository review found several material gaps:

1. positive Stock Adjustment behaves differently under Moving Average, FIFO, and Standard Cost;
2. `IvStockCountService` creates `ADJ` batches directly and was outside the earlier scope;
3. Customer Return source-line identity is not safely preserved through the Sales Credit Note `ReturnStock` flow;
4. stock-in history currently does not copy `InvNo` / `SoLineNo`;
5. existing inventory posting tests often run without the V2 ledger/coordinator and cannot prove `StockValuationFact` behavior;
6. inventory transaction list `TotalAmount` is calculated from `IvTrxBatchDetail.UnitPrice`, not authoritative valuation facts;
7. the earlier proposed stricter `OPENING_APPROVED > 0` rule was not current repository behavior and has therefore been removed.

This corrected plan explicitly resolves those issues.

---

# 2. Core architectural rule

The system must permanently separate:

- **Commercial price**
  - sales price;
  - PO price;
  - supplier return / CN price;
  - purchase price;
  - customer refund price.

from:

- **Inventory unit cost**
  - the monetary value assigned by the stock valuation engine.

For stock-controlled inventory, ordinary transaction users must not determine inventory valuation merely by typing `UnitPrice`.

The authoritative financial path remains:

```text
Inventory / Sales / Purchase transaction
        ↓
IvInventoryPostingService
        ↓
StockPostingCoordinator
        ↓
InventoryValuationService
        ↓
Moving Average / FIFO / Standard strategy
        ↓
StockValuationFact
```

`StockValuationFact` remains the historical monetary authority.

`IvTrxBatchDetail.UnitPrice`, `IvTrxHistory.UnitPrice`, `IvBalLoc.UnitPrice`, and Stock Master `PurchasePrice` may remain operational/reference data, but they must not silently override the costing strategy.

---

# 3. Existing repository architecture that must be preserved

The current repo already contains the correct central valuation architecture:

- `ErpWeb.Core/StockLedger/InventoryValuationService.cs`
- `ErpWeb.Core/StockLedger/Costing/MovingAverageCostingStrategy.cs`
- `ErpWeb.Core/StockLedger/Costing/FifoCostingStrategy.cs`
- `ErpWeb.Core/StockLedger/Costing/StandardCostingStrategy.cs`
- `ErpWeb.Core/StockLedger/StockPostingCoordinator.cs`
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`
- `ErpWeb.Core/StockLedger/StockValuationQueryService.cs`
- `ErpWeb.Model/Entities/StockLedger/StockValuationFact.cs`

Do not create a second inventory costing engine in:

- Razor UI;
- transaction services;
- DTO mapping;
- list pages;
- report DTOs.

This implementation is an **authority, lineage, UI, and test hardening change**, not a rewrite of the costing engine.

---

# 4. Approved transaction cost-authority matrix

## 4.1 Stock-out transactions

| Transaction | Direction | Cost authority | User-editable inventory cost |
|---|---:|---|---:|
| Miscellaneous Issue (`MI`) | OUT | configured costing strategy | No |
| Scrap (`SC`) | OUT | configured costing strategy | No |
| Vendor Return (`VR`) | OUT | configured costing strategy | No |
| Negative Stock Adjustment (`ADJ`) | OUT | configured costing strategy | No |
| Sales / DO shipment | OUT | existing sales posting path | No change in this plan |
| Issue to Production | OUT | existing production posting path | No change in this plan |

For all system-cost stock-out paths:

```text
Moving Average -> current pool average
FIFO           -> actual consumed FIFO layers
Standard       -> effective standard cost
```

User `UnitPrice` is never financial authority.

---

## 4.2 Stock Transfer

| Movement | Cost authority |
|---|---|
| Source OUT | costing strategy |
| Destination IN | exact source-out value |
| Net branch/item value | zero |

No user-entered transfer cost.

Existing transfer value-neutral behavior must be preserved.

---

## 4.3 Customer Return

Customer Return inventory value must come from the **exact original outbound sale valuation**, not:

- PurchasePrice;
- current WAC;
- current FIFO layer;
- arbitrary typed UnitPrice.

The return flow must preserve:

```text
Source Invoice
Source Invoice Line
Original outbound valuation facts
Prior return allocations
```

Standard Cost remains method-specific: inventory receipt is at effective standard cost and the existing standard-return variance mechanism reconciles original COGS vs standard receipt value.

---

## 4.4 Positive Stock Adjustment

This is method-specific.

### Moving Average

If the item already has a valid valued pool:

```text
positive ADJ without manual evidence
    -> StockCostState.CurrentUnitCost
```

If no valid current valued pool exists:

```text
posting blocks
    -> approved inbound cost evidence required
```

Approved evidence may be used when permitted.

### FIFO

Repository fact: current FIFO receipt logic calls `ResolveReceiptUnitCost(...)` and creates a FIFO layer.

Approved new rule for this implementation:

```text
positive ADJ with no manual evidence
    AND existing positive valued FIFO pool
        -> use StockCostState.CurrentUnitCost
        -> create one adjustment FIFO receipt layer at that system-derived cost

positive ADJ with no valued FIFO pool
        -> block
        -> require approved cost evidence

positive ADJ with approved evidence
        -> use approved evidence cost
        -> create FIFO adjustment layer at approved cost
```

Do not use Stock Master `PurchasePrice` as fallback.

### Standard Cost

For Standard Cost:

```text
positive ADJ
    -> effective ItemStandardCostRevision
```

A user-entered adjustment price must **not override Standard Cost inventory valuation**.

If business needs a different standard, the correct process is Standard Cost revision/revaluation, not transaction-level cost override.

Manual cost evidence may remain stored/audited if the existing workflow requires it, but it must not become the inventory `StockValuationFact.UnitCost` under Standard Cost.

---

# 5. Approved evidence semantics

Existing repository evidence values:

```text
MANUAL_APPROVED
ZERO_COST_APPROVED
OPENING_APPROVED
```

File:

`ErpWeb.Model/Entities/Inventory/InventoryCostEvidenceTypes.cs`

Preserve current semantics unless this implementation already enforces more specific rules.

Do not introduce the previously proposed rule that `OPENING_APPROVED` must always be greater than zero.

Required server rules:

### `MANUAL_APPROVED`

- only positive inbound adjustment;
- `PRICE_OVERRIDE` permission required;
- valid explicit cost required according to existing service rules;
- reason required where current service already requires it.

### `ZERO_COST_APPROVED`

- only positive inbound adjustment;
- zero cost;
- `PRICE_OVERRIDE` permission;
- reason required.

### `OPENING_APPROVED`

- only positive inbound adjustment;
- `PRICE_OVERRIDE` permission;
- preserve current repository validation semantics;
- reason/audit values preserved.

Do not change the meaning of existing historical records.

---

# 6. Do not add `InventoryCostAuthorityPolicy`

The earlier plan suggested a new `InventoryCostAuthorityPolicy.cs`.

Deep review found that the real rules are already method-specific and belong in the existing transaction service + costing strategy boundaries.

Do **not** add another generic policy class unless implementation proves a concrete reuse requirement.

Reasons:

- WAC, FIFO, and Standard have different positive Adjustment rules.
- A simple `trxType + qty + evidence` helper cannot make the correct decision without the active cost method.
- Duplicating method authority outside `InventoryValuationService` risks divergence.

Use the existing strategy architecture as the financial authority.

---

# 7. Workstream A — Miscellaneous Issue

## Files

Modify:

- `ErpWeb.UI/Inventory/Transactions/IvMiscIssue.razor`
- `ErpWeb.UI/Inventory/Transactions/IvMiscIssue.razor.cs`
- `ErpWeb.UI/Inventory/Transactions/IvMiscIssueList.razor`
- `ErpWeb.Core/Inventory/IIvMiscIssueService.cs`
- `ErpWeb.Core/Inventory/IvMiscIssueService.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvMiscIssuePostingServiceTests.cs`

## Current repo behavior

UI initializes:

```csharp
Popup.UnitPrice = item.PurchasePrice ?? 0m;
```

and exposes editable `Unit price`.

Service persists user/request `UnitPrice`.

Financial V2 outbound valuation does not need this user-entered value.

## Required UI change

Remove editable:

```text
Unit price
Line amount
```

from NEW / EDIT line popup.

Remove purchase-price initialization.

Display a small explanation:

```text
Inventory cost is calculated automatically when posted.
```

For VIEW of an existing NEW document, do not imply that stored line UnitPrice is financial truth.

## Required service change

Keep `UnitPrice` in request/DTO temporarily for compatibility, but do not trust it.

On save/update:

```text
incoming UnitPrice
    -> ignored for financial authority
```

If an operational reference value must still be populated for old code paths, use selected source balance only:

```csharp
bal.UnitPrice ?? 0m
```

Do not fall back to:

```csharp
item.PurchasePrice
```

Add code comment:

```text
Outbound valuation is determined by InventoryValuationService.
This field is retained only for backward-compatible operational display.
```

## List page

Remove `TotalAmount` chip from `IvMiscIssueList.razor` because current `IvStockTransactionRepository` calculates it from document `UnitPrice`.

Do not replace it with another guessed amount in this change.

## Tests

Physical lifecycle tests may remain.

Add ledger-enabled tests proving:

- WAC ignores `UnitPrice = 999999`;
- FIFO ignores `UnitPrice = 999999`;
- Standard ignores `UnitPrice = 999999`;
- StockValuationFact amount matches active method;
- rollback creates exact reversal;
- Stock Master PurchasePrice difference does not change valuation.

---

# 8. Workstream B — Scrap

## Files

Modify:

- `ErpWeb.UI/Inventory/Transactions/IvScrap.razor`
- `ErpWeb.UI/Inventory/Transactions/IvScrap.razor.cs`
- `ErpWeb.UI/Inventory/Transactions/IvScrapList.razor`
- `ErpWeb.Core/Inventory/IIvScrapService.cs`
- `ErpWeb.Core/Inventory/IvScrapService.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvScrapPostingServiceTests.cs`

## Required change

Same stock-out authority rule as MI.

Remove editable Unit Price and purchase-price initialization.

If old contract requires a persisted reference:

```csharp
bal.UnitPrice ?? 0m
```

only.

Do not use master PurchasePrice as valuation fallback.

Remove list `TotalAmount` display.

## Tests

Ledger-enabled:

- WAC;
- FIFO layer consumption;
- Standard;
- absurd request UnitPrice ignored;
- `MovementCode == SCRAP_OUT`;
- exact rollback.

---

# 9. Workstream C — Stock Transfer

## Files

Modify:

- `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor`
- `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor.cs`
- `ErpWeb.UI/Inventory/Transactions/IvStockTransferList.razor`
- `ErpWeb.Core/Inventory/IIvStockTransferService.cs`
- `ErpWeb.Core/Inventory/IvStockTransferService.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvStockTransferPostingServiceTests.cs`

Do not rewrite the existing transfer posting algorithm in `IvInventoryPostingService.cs` unless a failing test proves a defect.

## Current service pattern

Current logic includes:

```csharp
var unitPrice =
    line.UnitPrice > 0m
        ? line.UnitPrice
        : bal.UnitPrice ?? item.PurchasePrice ?? 0m;
```

## Required service behavior

Replace with server-owned operational reference:

```csharp
var unitPrice = bal.UnitPrice ?? 0m;
```

User request UnitPrice must not affect transfer valuation.

Do not use item PurchasePrice.

## UI

Remove editable Unit Price.

Show:

```text
Transfer preserves the source inventory value automatically.
```

Remove list `TotalAmount`.

## Preserve

Existing:

- source locking;
- destination locking;
- lock ordering;
- source balance identity;
- two-leg history;
- exact source-out value to destination;
- rollback/reversal behavior;
- branch-item value neutrality.

## Tests

Ledger-enabled test cases:

### WAC

```text
source OUT amount == destination IN amount
net item/branch value delta == 0
```

### FIFO

Example:

```text
5 @ 3
10 @ 4
transfer 8
```

Expected:

```text
source OUT = 27
destination IN = 27
```

Request `UnitPrice = 999999` must not change either leg.

### Standard

Both legs use effective standard cost and net branch/item value is zero.

### Rollback

Exact reversal of both facts.

---

# 10. Workstream D — Vendor Return

## Files

Modify:

- `ErpWeb.UI/Inventory/Transactions/IvVendorReturn.razor`
- `ErpWeb.UI/Inventory/Transactions/IvVendorReturn.razor.cs`
- `ErpWeb.UI/Inventory/Transactions/IvVendorReturnList.razor`
- `ErpWeb.Core/Inventory/IIvVendorReturnService.cs`
- `ErpWeb.Core/Inventory/IvVendorReturnService.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvVendorReturnPostingServiceTests.cs`

## Business separation

Keep separate:

```text
inventory cost removed from stock
vs
supplier commercial return/CN value
```

Inventory Vendor Return owns the physical stock-out.

Procurement / Purchase CN owns supplier commercial pricing.

## UI

Remove editable inventory Unit Price.

Preserve:

- PO No;
- PO Release;
- PO Line;
- item;
- source balance;
- warehouse/location/lot;
- quantity;
- reason;
- remarks.

If PO price is displayed, label:

```text
PO Price (reference)
```

It must be read-only.

## Service

Continue exact PO/release/line validation and cumulative received/return checks.

Ignore request UnitPrice for financial valuation.

If a legacy reference value is required:

```csharp
bal.UnitPrice ?? 0m
```

Do not use master PurchasePrice.

## Purchase-CN owned VR

Preserve the existing ownership/guard path.

Do not break Procurement CN behavior.

This change only removes inventory cost authority from the typed inventory UnitPrice.

## List

Remove non-authoritative `TotalAmount`.

## Tests

Ledger-enabled:

- WAC/FIFO/Standard;
- PO price != inventory cost;
- request UnitPrice cannot alter StockValuationFact;
- PO return quantities remain correct;
- purchase-CN ownership guard remains correct;
- exact rollback.

---

# 11. Workstream E — Inventory Stock Return / Customer Return

This is a structural correctness change.

## Files

Modify:

- `ErpWeb.UI/Inventory/Transactions/IvStockReturn.razor`
- `ErpWeb.UI/Inventory/Transactions/IvStockReturn.razor.cs`
- `ErpWeb.UI/Inventory/Transactions/IvStockReturnList.razor`
- `ErpWeb.Core/Inventory/IIvStockReturnService.cs`
- `ErpWeb.Core/Inventory/IvStockReturnService.cs`
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvStockReturnServiceTests.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvStockReturnPostingServiceTests.cs`

## Target workflow

Replace:

```text
select arbitrary item
enter qty
enter UnitPrice
return stock
```

with:

```text
select source POSTED invoice
select exact source invoice line
enter return qty
select destination WH/location/lot/status
save
post
restore original outbound valuation
```

## DTO changes

Add immutable source fields to `IvStockReturnLineDto` and `IvStockReturnLineRequest`.

Preferred naming:

```csharp
public string SourceInvNo { get; set; } = string.Empty;
public short SourceInvoiceLine { get; set; }
```

Map to existing inventory batch columns:

```text
IvTrxBatchDetail.InvNo    <- SourceInvNo
IvTrxBatchDetail.SoLineNo <- SourceInvoiceLine
```

No inventory-table migration is required for this path.

## Lookup APIs

Add to `IIvStockReturnService`:

```csharp
Task<IvStockReturnOperationResult> SearchSourceInvoicesAsync(
    string? searchText,
    int take = 50,
    CancellationToken cancellationToken = default);

Task<IvStockReturnOperationResult> GetSourceInvoiceLinesAsync(
    string invNo,
    CancellationToken cancellationToken = default);
```

DTOs:

```text
IvStockReturnInvoiceLookupRow
IvStockReturnInvoiceLineLookupRow
```

## Source invoice lookup requirements

Only expose:

- same CompanyCode;
- same BranchCode;
- POSTED invoices;
- valid stock-controlled lines.

Return at minimum:

```text
InvNo
Invoice Line
Item Code
Description
Qty
StdQty
StdUom
StockControl
DO lineage when present
```

Do not expose another company's/branch's document.

## Save-time validation

Server must validate:

1. source invoice required;
2. source invoice line required;
3. invoice exists;
4. same tenant;
5. POSTED;
6. exact source line exists;
7. return item equals source item;
8. source line is stock controlled;
9. return qty > 0;
10. destination warehouse/location/lot/status valid;
11. user UnitPrice is ignored as cost authority.

Do not trust item code from the client without re-reading invoice line.

## UI

Source first:

```text
Source Invoice
Source Invoice Line
Item - derived/read-only
Description - derived
Return Qty
Destination Warehouse
Destination Location
Lot
Expiry
Status
Reason
Remarks
```

Remove editable Unit Price.

Display:

```text
Inventory cost is restored from the original sale.
```

## Posting-core history correction

Deep review confirmed `PostInventoryMRCoreAsync()` currently copies:

```text
DoNo
PoNo
PoRelNo
PoLineNo
```

but does not copy:

```text
InvNo
SoLineNo
```

Add to the stock-in `IvTrxHistory` creation:

```csharp
InvNo = detail.InvNo,
SoLineNo = detail.SoLineNo,
```

Do not claim these are already preserved.

This is required for durable lineage, diagnostics, repair, and future reads even though the current valuation service can also read detail evidence during the same posting.

## List

Remove `TotalAmount` chip.

## Tests

Ledger-enabled:

- missing source invoice rejected;
- missing source line rejected;
- wrong item rejected;
- cross-tenant rejected;
- unposted invoice rejected;
- request UnitPrice ignored;
- direct invoice source restores original COGS;
- DO-linked invoice source restores DO valuation;
- partial return;
- repeated partial return;
- over-return rejected;
- FIFO original valuation allocation restored;
- Standard return uses current Standard receipt value plus existing standard-return variance behavior;
- rollback reverses exact facts and return allocations;
- `IvTrxHistory.InvNo` and `SoLineNo` preserved.

---

# 12. Workstream F — Sales Credit Note `ReturnStock` source-line provenance

This is mandatory because Sales CN also creates `IvTrxTypes.CustomerReturn`.

Fixing only `IvStockReturn` would leave a second customer-return path with weak lineage.

## Files

Modify:

- `ErpWeb.Core/Sales/ISaCdnService.cs`
- `ErpWeb.Core/Sales/SaCdnService.cs`
- `ErpWeb.UI/Sales/Transactions/SaCdn.razor`
- `ErpWeb.UI/Sales/Transactions/SaCdn.razor.cs`
- `ErpWeb.Model/Entities/Sales/SaCdnDetail.cs`
- `ErpWeb.Model/Configurations/Sales/SaCdnDetailConfiguration.cs`
- `ErpWeb.Tests/Sales/Transaction/SaCdnServiceTests.cs`
- relevant SQL schema/alter script for `SaCDNDetail`

## Proven current issue

`CopyFromInvoiceAsync()` maps invoice details into CN draft lines using sequential CN line numbers.

The UI subsequently calls `Renumber()` after line removal/edit.

`PrepareLinesAsync()` also rebuilds sequential line numbers.

Later `AddCrBatchDetails()` writes:

```csharp
SoLineNo = d.Line;
```

That is not guaranteed to be the original invoice line.

## Required schema

Add an immutable original invoice-line field to `SaCdnDetail`.

Preferred:

```csharp
public short? SourceInvLine { get; set; }
```

Configuration:

```text
SaCDNDetail.SourceInvLine smallint NULL
```

Create the repo-standard idempotent SQL alter script.

Do not repurpose `SaCdnDetail.Line`.

`Line` remains the CN's own presentation/document line.

`SourceInvLine` is original invoice provenance.

## DTO/request/view-model propagation

Add `SourceInvLine` through:

- `SaCdnLineDto`;
- `SaCdnLineRequest`;
- `SaCdnLineVm`;
- mapping methods;
- copy/clone methods;
- persistence.

## Copy from invoice

When copying invoice lines:

```text
CN Line          = normal sequential editable line
SourceInvLine    = original SaInvoiceDetail.Line
```

If the user removes line 1 and the UI renumbers the remaining CN lines, `SourceInvLine` must remain unchanged.

Example:

```text
Invoice:
1 A
2 B
3 C

CN removes A:
CN Line 1 B  SourceInvLine 2
CN Line 2 C  SourceInvLine 3
```

## ReturnStock gate

If:

```text
Credit Note
AND ReturnStock == true
AND there is at least one stock-controlled line
```

require:

```text
InvNo not null
every stock-controlled line has SourceInvLine
source invoice exists in same tenant and is POSTED
SourceInvLine exists on that invoice
source item matches CN return item
```

Do not allow stock-returning CN with unresolvable source invoice lineage.

## AddCrBatchDetails

Change:

```csharp
SoLineNo = d.Line;
```

to:

```csharp
SoLineNo = d.SourceInvLine;
```

For a stock-returning CN, null must be rejected before batch creation.

Keep:

```csharp
InvNo = cdn.InvNo;
DoNo = cdn.DoNo;
```

## Existing non-stock CN

Do not require source-line lineage for:

```text
ReturnStock == false
```

Commercial credit notes without physical inventory movement remain unaffected.

## Existing legacy NEW CN records

For pre-deployment NEW CNs with `ReturnStock=true` and no SourceInvLine:

- do not guess;
- require user to recopy/reselect the source invoice or otherwise re-establish exact source provenance before posting.

## Existing POSTED CNs

Do not rewrite them automatically.

Costing diagnostics/repair remain responsible for historical inconsistencies.

## Tests

Add cases:

1. copied invoice preserves original source line;
2. remove first copied line -> remaining `SourceInvLine` stays original;
3. CN own line renumber does not mutate SourceInvLine;
4. ReturnStock with no InvNo rejected;
5. ReturnStock stock line with no SourceInvLine rejected;
6. wrong source item rejected;
7. source invoice not POSTED rejected;
8. CR batch `SoLineNo` equals SourceInvLine;
9. V2 customer return resolves original COGS for subset/reordered invoice lines.

---

# 13. Workstream G — Stock Adjustment service

## Files

Modify:

- `ErpWeb.UI/Inventory/Transactions/IvStockAdjustment.razor`
- `ErpWeb.UI/Inventory/Transactions/IvStockAdjustment.razor.cs`
- `ErpWeb.UI/Inventory/Transactions/IvStockAdjustmentList.razor`
- `ErpWeb.Core/Inventory/IIvStockAdjustmentService.cs`
- `ErpWeb.Core/Inventory/IvStockAdjustmentService.cs`
- `ErpWeb.Core/StockLedger/InventoryValuationService.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvStockAdjustmentPostingServiceTests.cs`

## Negative adjustment

UI:

- no editable Unit Price;
- no manual evidence controls;
- no override reason.

Server:

- incoming UnitPrice is not authority;
- no PurchasePrice fallback;
- no positive-inbound evidence behavior.

Valuation:

```text
WAC      -> pool average
FIFO     -> FIFO layers
Standard -> effective standard cost
```

## Positive adjustment UI

Default:

```text
Use system cost
```

When active cost method is Standard:

- no inventory-cost override control;
- display that Standard Cost will be used.

When WAC/FIFO and user has `PRICE_OVERRIDE`:

optional:

```text
Override system cost
```

If checked, show:

```text
Override Unit Cost
Evidence Type
Reason
```

Do not rely only on UI permission.

Server still enforces `PRICE_OVERRIDE`.

## Positive adjustment service

Remove:

```csharp
bal.UnitPrice ?? item.PurchasePrice ?? 0m
```

as financial fallback.

Do not silently convert PurchasePrice to approved cost evidence.

For no-evidence positive adjustment:

- save can keep legacy DTO compatibility;
- financial decision happens in `InventoryValuationService`.

For evidence-backed positive adjustment:

- preserve current evidence validation;
- permission required;
- evidence persisted to detail/history.

## InventoryValuationService — WAC

Preserve existing special rule:

```text
ADJ receipt
no evidence
valid positive current cost state
    -> state.CurrentUnitCost
```

If no valid state:

```text
ValuationRequired
```

## InventoryValuationService — FIFO

Add explicit Stock Adjustment receipt rule before generic receipt `ResolveReceiptUnitCost()`:

```text
if TrxType == ADJ
and no cost evidence
    if state has positive valid valued quantity
         unitCost = state.CurrentUnitCost
         source = FIFO / system-derived adjustment source as appropriate
    else
         throw ValuationRequired
```

Then create normal FIFO receipt layer from that resolved unit cost.

Evidence-backed FIFO ADJ continues to use approved explicit receipt cost.

Do not derive from item PurchasePrice.

## InventoryValuationService — Standard

Preserve:

```text
quantity × effective standard cost
```

for positive adjustment.

Do not let approved UnitPrice replace Standard Cost.

If evidence is persisted for audit, ensure validation does not accidentally reinterpret it as Standard inventory unit cost.

## List

Remove `TotalAmount`.

## Tests

Ledger-enabled:

### Negative

- user UnitPrice ignored under WAC;
- ignored under FIFO;
- ignored under Standard.

### Positive WAC

- existing pool -> current system cost;
- no pool -> ValuationRequired;
- approved evidence -> explicit approved cost according to current evidence semantics.

### Positive FIFO

- existing valued pool -> `StockCostState.CurrentUnitCost`;
- new FIFO layer created at system-derived cost;
- no valued pool -> ValuationRequired;
- approved evidence -> FIFO layer at approved cost.

### Positive Standard

- effective Standard Cost used;
- request/approved manual UnitPrice does not override standard valuation.

### Evidence

- unauthorized PRICE_OVERRIDE blocked;
- current MANUAL_APPROVED rules preserved;
- ZERO_COST_APPROVED rules preserved;
- OPENING_APPROVED current repo semantics preserved.

### Rollback

- exact reversal.

---

# 14. Workstream H — Stock Count generated Adjustment

This is mandatory.

## Files

Modify:

- `ErpWeb.Core/Inventory/IvStockCountService.cs`
- stock-count tests under `ErpWeb.Tests/Inventory/Transaction/...` that cover posting/variance

## Proven current path

`IvStockCountService` directly creates:

```text
IvTrxBatch TrxType = ADJ
IvTrxBatchDetail
```

and currently calculates:

```csharp
var unitPrice = IvQty.Round(
    balance.UnitPrice ?? master?.PurchasePrice ?? 0m);
```

It then posts through:

```csharp
PostStockAdjustmentInTransactionAsync(...)
```

Therefore fixing only `IvStockAdjustmentService` is insufficient.

## Required change

Stock Count must follow the same method-specific ADJ rules.

### Count variance that reduces stock

No transaction-entered cost authority.

### Count variance that increases stock

Do not use Stock Master PurchasePrice as financial fallback.

For the generated ADJ detail:

- do not manufacture approved evidence;
- do not mark PurchasePrice as authoritative;
- allow `InventoryValuationService` to apply the no-evidence ADJ policy.

That means:

```text
WAC -> current cost state or block
FIFO -> current FIFO pool system cost or block
Standard -> effective Standard Cost
```

## Important sign convention

Current Stock Count code derives `variance` using the repo's existing convention where:

```text
variance > 0
    -> live stock higher than count
    -> decrease stock

variance < 0
    -> count higher than live
    -> increase stock
```

Preserve this existing direction logic.

Do not "simplify" the sign behavior.

## Tests

Ledger-enabled stock-count scenarios:

- negative stock movement under WAC/FIFO/Standard;
- positive adjustment under WAC existing pool;
- positive adjustment under FIFO existing valued pool;
- positive adjustment under Standard;
- no WAC/FIFO valuation anchor -> posting fails rather than using PurchasePrice;
- Stock Master PurchasePrice intentionally differs from authoritative cost and is ignored;
- transaction remains atomic with Stock Count header;
- rollback remains valid.

---

# 15. Workstream I — remove misleading inventory transaction amounts

## Proven current behavior

`ErpWeb.Model/Repositories/Inventory/IvStockTransactionRepository.cs`

calculates list `TotalAmount` from:

```text
quantity × IvTrxBatchDetail.UnitPrice
```

All affected list pages display that value.

This is not authoritative under FIFO, WAC system valuation, customer-return original COGS, or Standard Cost.

## Affected list pages

Remove the `TotalAmount` chip/column from:

- `IvMiscIssueList.razor`
- `IvScrapList.razor`
- `IvStockTransferList.razor`
- `IvVendorReturnList.razor`
- `IvStockReturnList.razor`
- `IvStockAdjustmentList.razor`

For this implementation, **do not replace it with another cost calculation**.

The goal is to stop presenting wrong money.

## Draft/detail pages

Remove:

```text
Qty × UnitPrice
```

amount displays where the UnitPrice is no longer authoritative.

Instead show:

```text
Cost is calculated when posted.
```

## Existing shared repository

Do not delete `IvTrxBatchListRow.TotalAmount` globally because other transaction types may still use it.

Only stop showing it in these affected list pages.

A later dedicated enhancement may expose actual posted valuation from `StockValuationFact`.

---

# 16. Optional future enhancement — actual posted cost display

This is explicitly **out of scope for this implementation**.

Existing `IStockValuationQueryService` is designed for valuation reads, but it does not currently provide a generic "inventory transaction batch/line actual cost" API.

Do not create a rushed new read service in this patch.

Future enhancement can add:

```text
Posted Actual Cost
Effective Unit Cost
Cost Method
Valuation Source
```

from `StockValuationFact`.

Until then, hiding non-authoritative amount is safer.

---

# 17. DTO backward compatibility

Do not remove `UnitPrice` from existing inventory transaction DTOs in this change.

Keep it temporarily so:

- old code compiles;
- old NEW documents can still be loaded;
- internal call sites remain compatible.

But add XML comments where useful:

```text
Legacy operational/reference value.
Not authoritative inventory valuation for system-cost movements.
```

System-cost transactions must not trust client-supplied value.

This applies to:

- MI;
- Scrap;
- Transfer;
- Vendor Return;
- Customer Return;
- negative Adjustment.

Positive Adjustment is method/evidence-specific as defined above.

---

# 18. Existing NEW documents

For existing NEW documents created before deployment:

## MI / Scrap / Transfer / VR

On next save/update:

- ignore old user-entered UnitPrice as authority;
- resolve the selected stock source;
- preserve only server-owned reference data where needed;
- posting uses active valuation strategy.

## Inventory Stock Return

If source invoice/line identity is missing:

- do not infer from item/price;
- force source invoice + exact line selection before posting.

## Sales CN ReturnStock

If legacy NEW CN has no `SourceInvLine`:

- do not guess;
- require re-copy/reselection from the source invoice before posting.

## Adjustment / Stock Count

Do not grandfather PurchasePrice as approved cost.

No-evidence positive adjustment follows active method rules.

---

# 19. Existing POSTED documents

Do not rewrite:

- posted `IvTrxBatchDetail`;
- posted `IvTrxHistory`;
- existing `StockValuationFact`;
- existing Sales CN source data.

Historical repair belongs to existing costing diagnostics/repair infrastructure.

This implementation affects new/reposted movements going forward.

---

# 20. Database changes

## Inventory transaction tables

No new monetary column is required.

Existing fields already include:

```text
IvTrxBatchDetail.UnitPrice
IvTrxBatchDetail.CostEvidenceType
IvTrxBatchDetail.CostOverrideReason
IvTrxBatchDetail.CostApprovedBy
IvTrxBatchDetail.CostApprovedAtUtc
IvTrxBatchDetail.InvNo
IvTrxBatchDetail.SoLineNo

IvTrxHistory equivalents
StockValuationFact
SalesReturnCostAllocation
```

## Sales Credit Note

A schema addition is required to safely preserve immutable original invoice line provenance:

```text
SaCDNDetail.SourceInvLine smallint NULL
```

Update:

- entity;
- EF configuration;
- idempotent SQL alter script;
- tests/model assertions as appropriate.

Do not add another cost column.

---

# 21. `IvInventoryPostingService` changes

Only make targeted changes.

## Mandatory CR history mapping

In stock-in history creation, add:

```csharp
InvNo = detail.InvNo,
SoLineNo = detail.SoLineNo,
```

Preserve all existing mappings.

## Do not rewrite

- branch transaction locking;
- stock-date validation;
- period guards;
- find/create balance logic;
- history generation model;
- posting operation ID;
- V2 coordinator integration;
- reversal handling.

Transfer logic should remain unchanged unless a test fails.

---

# 22. Ledger-enabled inventory test fixture

The earlier plan's anti-tampering tests cannot rely on the existing simple posting-service helpers because many instantiate:

```csharp
new IvInventoryPostingService(...)
```

without:

- `IvInventoryHistoryWriter`;
- `StockPostingCoordinator`;
- `InventoryValuationService`.

When coordinator is null, the V2 ledger is disabled.

## Create

`ErpWeb.Tests/Inventory/Transaction/InventoryLedgerTestFixture.cs`

Model it after:

`ErpWeb.Tests/Production/Transaction/ProductionLedgerTestFixture.cs`

## Fixture responsibilities

Provide helpers to:

1. create SQLite factory;
2. seed Company/base currency as existing tests require;
3. seed active `StockLedgerEpoch`;
4. seed `StockCostPolicyRevision` for:
   - Moving Average;
   - FIFO;
   - Standard;
5. seed Standard Cost revision when needed;
6. construct:
   - `InventoryValuationService`;
   - `StockPostingCoordinator`;
   - `IvInventoryHistoryWriter`;
   - ledger-enabled `IvInventoryPostingService`;
7. seed starting `StockCostState`;
8. seed FIFO layers / opening valuation facts where required;
9. seed source sales valuation for Customer Return tests.

Use the existing Production fixture patterns rather than inventing another ledger lifecycle.

---

# 23. Anti-tampering test requirement

For every system-cost stock-out route:

```csharp
UnitPrice = 999999m;
```

Then POST through the ledger-enabled path.

Assert:

```text
StockValuationFact.UnitCost != 999999
StockValuationFact.CostAmount matches active method
```

Required:

- MI;
- Scrap;
- Transfer;
- Vendor Return;
- Customer Return;
- negative Adjustment.

Also intentionally set:

```text
StockMaster.PurchasePrice != authoritative inventory cost
```

and prove PurchasePrice does not leak into valuation.

---

# 24. Customer Return test matrix

Both return routes must be tested.

## Inventory Stock Return

- exact invoice;
- exact invoice line;
- direct invoice;
- DO-linked invoice;
- partial return;
- repeated partial return;
- over-return;
- WAC;
- FIFO;
- Standard;
- rollback;
- history lineage.

## Sales CN ReturnStock

- full copied invoice;
- subset after deleting earlier invoice lines;
- renumbered CN lines preserve source invoice line;
- `ReturnStock=true` without InvNo rejected;
- missing SourceInvLine rejected;
- item mismatch rejected;
- original COGS resolved correctly;
- rollback existing CN/CR ownership behavior preserved.

---

# 25. Adjustment test matrix

## Moving Average

### Negative

Current pool average.

### Positive, existing pool

Current `StockCostState.CurrentUnitCost`.

### Positive, no pool

Blocks with valuation-required error unless approved evidence exists.

### Approved evidence

Existing permission/evidence semantics preserved.

---

## FIFO

### Negative

Consume FIFO layers.

### Positive, existing valued pool, no evidence

Use current `StockCostState.CurrentUnitCost`.

Create adjustment receipt FIFO layer.

### Positive, no valued pool

Block.

### Positive approved evidence

Use approved evidence price for adjustment FIFO receipt layer.

---

## Standard

### Negative

Effective standard cost.

### Positive

Effective standard cost.

Manual UnitPrice does not override Standard valuation.

---

# 26. Transfer test matrix

### WAC

```text
OUT amount == IN amount
net pool value delta == zero
```

### FIFO

Example:

```text
5 @ RM3
10 @ RM4
transfer 8
```

Expected:

```text
OUT = RM27
IN  = RM27
```

### Standard

```text
OUT qty × Standard
IN  exact same
```

### Rollback

Exact append-only reversal.

---

# 27. Stock Count test matrix

Use ledger-enabled posting.

Test:

1. count reduces stock under WAC;
2. count reduces stock under FIFO;
3. count reduces stock under Standard;
4. count increases stock under WAC with valued pool;
5. count increases stock under FIFO with valued pool;
6. count increases stock under Standard;
7. WAC no anchor -> block;
8. FIFO no anchor -> block;
9. PurchasePrice mismatch does not determine valuation;
10. header + ADJ posting remain atomic;
11. concurrency behavior unchanged;
12. rollback unchanged.

---

# 28. Server-side invariants

UI is not a security boundary.

Mandatory:

1. MI request UnitPrice cannot change financial valuation.
2. Scrap request UnitPrice cannot change financial valuation.
3. Transfer request UnitPrice cannot change source/destination financial value.
4. VR request UnitPrice cannot change financial valuation.
5. Customer Return request UnitPrice cannot change original COGS restoration.
6. negative ADJ UnitPrice cannot change valuation.
7. positive WAC/FIFO manual value is allowed only through valid evidence + permission.
8. Standard Cost ignores transaction-level cost override for inventory valuation.
9. `PRICE_OVERRIDE` remains server enforced.
10. PurchasePrice is not approved cost evidence.
11. Customer Return source invoice/line must be tenant-safe.
12. Sales CN ReturnStock source invoice/line must be tenant-safe.
13. posting/rollback remain atomic.
14. backdated guards unchanged.
15. period/freeze guards unchanged.
16. exact reversal unchanged.
17. stock-transfer value conservation unchanged.

---

# 29. User-facing error requirements

Use clear messages.

## Missing Customer Return source

```text
Select the original posted invoice and invoice line before saving this stock return.
```

## Sales CN ReturnStock no source

```text
Stock return requires the original posted invoice and source invoice line for every stock item.
```

## Positive WAC/FIFO adjustment has no valuation anchor

```text
The system has no approved current inventory cost for item ITEM001. Use an authorized cost override with supporting evidence before posting this positive adjustment.
```

## Unauthorized cost override

```text
You are not authorized to override inventory cost.
```

## Standard Cost

```text
This item is valued by Standard Cost. Inventory value uses the effective standard cost; change the standard cost through the Standard Cost process.
```

Preserve existing detailed FIFO insufficiency and rollback dependency messages.

---

# 30. Implementation order

## Phase 0 — baseline

Before editing:

1. confirm HEAD matches the reviewed branch;
2. run focused existing tests;
3. inspect exact signatures before changing DTOs/services.

Do not implement against guessed method names.

## Phase 1 — ledger-enabled test infrastructure

1. add `InventoryLedgerTestFixture`;
2. prove one existing MI posting produces `StockValuationFact`;
3. add method-policy seeding helpers.

This must happen first so every later claim can be tested against the actual ledger.

## Phase 2 — stock-out authority hardening

1. MI;
2. Scrap;
3. Transfer;
4. Vendor Return;
5. negative Adjustment.

Server first, then UI.

Remove PurchasePrice fallbacks from these flows.

Add anti-tampering tests.

## Phase 3 — Adjustment method-specific behavior

1. WAC positive ADJ;
2. FIFO positive ADJ explicit rule;
3. Standard Cost override behavior;
4. permission/evidence tests;
5. Stock Count integration.

Do not change evidence semantics outside what this plan explicitly states.

## Phase 4 — Customer Return lineage

### Inventory Stock Return

1. DTO/source fields;
2. source invoice lookup;
3. source line lookup;
4. save-time validation;
5. UI;
6. history mapping.

### Sales CN ReturnStock

1. add `SaCDNDetail.SourceInvLine`;
2. SQL script;
3. DTO/request/view-model propagation;
4. copy-from-invoice preservation;
5. ReturnStock source validation;
6. `AddCrBatchDetails()` uses SourceInvLine;
7. subset/reorder tests.

## Phase 5 — remove misleading amounts

Remove non-authoritative amount display from six affected inventory transaction list pages and draft entry areas.

Do not build a new actual-cost inquiry in this phase.

## Phase 6 — regression

Run:

- all affected Inventory transaction tests;
- Stock Ledger tests;
- Sales CN tests;
- Stock Count tests;
- costing valuation tests;
- full Inventory transaction category;
- relevant Sales transaction category.

---

# 31. Required existing test files

At minimum update/run:

```text
ErpWeb.Tests/Inventory/Transaction/IvMiscIssuePostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvScrapPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockTransferPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvVendorReturnPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockReturnPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockReturnServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockAdjustmentPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/InventoryValuationServiceTests.cs
ErpWeb.Tests/Sales/Transaction/SaCdnServiceTests.cs
```

Also locate and update the current Stock Count posting tests.

Add:

```text
ErpWeb.Tests/Inventory/Transaction/InventoryLedgerTestFixture.cs
```

Additional dedicated test classes are allowed if they improve separation.

---

# 32. Do not change these areas without a failing test

Do not rewrite:

- general Moving Average issue algorithm;
- general FIFO issue allocation;
- Standard Cost resolver;
- Standard Cost cutover/revaluation;
- `StockPostingCoordinator` transaction model;
- posting-sequence model;
- `StockValuationFact` identity;
- exact reversal mechanism;
- cost-method cutover;
- stock period close;
- stock freeze guard;
- production costing;
- finished-good transfer valuation;
- purchase GR valuation;
- purchase invoice cost settlement;
- costing repair center.

Only touch `InventoryValuationService` where required for the explicit positive FIFO ADJ behavior and verified lineage handling.

---

# 33. Code quality constraints for Grok 4.7

1. Use current project conventions.
2. Razor pages continue using code-behind.
3. Preserve DevExpress component patterns.
4. Preserve tenant isolation.
5. Preserve cancellation tokens.
6. Preserve current DI lifetime conventions.
7. Avoid static service locators.
8. Do not introduce duplicate repositories for data already accessible through current services.
9. No hidden fallback from PurchasePrice.
10. No client-side-only authorization.
11. No broad refactor unrelated to this plan.
12. No mass rename of `UnitPrice` database fields.
13. Do not delete legacy fields solely for aesthetic cleanup.
14. Prefer focused commits/work units by phase.
15. Update comments that currently imply wrong authority.

---

# 34. Definition of Done

Implementation is complete only when:

## MI

- [ ] no editable inventory Unit Price;
- [ ] no PurchasePrice valuation fallback;
- [ ] ledger anti-tampering tests pass.

## Scrap

- [ ] same.

## Transfer

- [ ] no editable cost;
- [ ] exact source-out value reaches destination;
- [ ] WAC/FIFO/Standard transfer tests pass;
- [ ] zero net value impact.

## Vendor Return

- [ ] no editable inventory cost;
- [ ] PO commercial price separated from stock valuation;
- [ ] purchase-CN ownership preserved.

## Inventory Stock Return

- [ ] exact source invoice required;
- [ ] exact source invoice line required;
- [ ] item derived/validated from source;
- [ ] original COGS restored;
- [ ] history stores InvNo + SoLineNo.

## Sales CN ReturnStock

- [ ] `SaCDNDetail.SourceInvLine` exists;
- [ ] copied invoice preserves original line;
- [ ] CN renumber never changes source line;
- [ ] ReturnStock requires valid source;
- [ ] CR batch uses SourceInvLine;
- [ ] subset/reorder test passes.

## Adjustment

- [ ] negative cost fully system controlled;
- [ ] positive WAC no-evidence behavior correct;
- [ ] positive FIFO no-evidence behavior explicitly implemented;
- [ ] positive Standard always uses Standard Cost;
- [ ] evidence/permission rules pass;
- [ ] PurchasePrice not used as valuation fallback.

## Stock Count

- [ ] generated ADJ follows same rules;
- [ ] PurchasePrice bypass removed;
- [ ] atomic behavior preserved.

## UI amounts

- [ ] non-authoritative TotalAmount hidden from six affected list pages;
- [ ] draft fake amount removed.

## Tests

- [ ] ledger-enabled fixture exists;
- [ ] anti-tampering tests use actual V2 ledger;
- [ ] WAC passes;
- [ ] FIFO passes;
- [ ] Standard passes;
- [ ] rollback passes;
- [ ] relevant Sales CN tests pass;
- [ ] relevant Stock Count tests pass.

## Historical safety

- [ ] no posted history rewritten automatically;
- [ ] no StockValuationFact rewritten;
- [ ] legacy NEW missing source lineage is blocked rather than guessed.

---

# 35. Explicit acceptance scenarios

## Scenario A — MI WAC

Pool:

```text
100 pcs
RM350
WAC RM3.50
```

User enters:

```text
Qty 10
legacy UnitPrice 999999
```

Expected:

```text
StockValuationFact Qty 10
Cost RM35
not RM9,999,990
```

## Scenario B — FIFO Scrap

Layers:

```text
5 @ 3
10 @ 4
```

Scrap 8.

Expected:

```text
5 @ 3 = 15
3 @ 4 = 12
Total = 27
```

Typed UnitPrice ignored.

## Scenario C — Transfer FIFO

Same layers, transfer 8.

Expected:

```text
source OUT 27
destination IN 27
net branch/item value 0
```

## Scenario D — Positive WAC Adjustment

Existing:

```text
100 pcs
RM350
```

Adjustment:

```text
+10
no evidence
```

Expected:

```text
cost = RM3.50
inbound value = RM35
```

## Scenario E — Positive FIFO Adjustment

Existing FIFO pool state:

```text
on hand > 0
current pool unit cost = RM3.50
```

Adjustment:

```text
+10
no evidence
```

Expected:

```text
new FIFO adjustment layer:
10 @ RM3.50
```

If no valued pool exists:

```text
posting blocked
```

## Scenario F — Standard Adjustment

Standard:

```text
RM3.80
```

Adjustment:

```text
+10
typed override RM9.00
```

Expected inventory valuation:

```text
10 × RM3.80 = RM38
```

Transaction override does not replace Standard Cost.

## Scenario G — Sales CN subset return

Invoice:

```text
Line 1 A
Line 2 B
Line 3 C
```

Copy invoice into CN.

User removes A.

CN:

```text
Line 1 B SourceInvLine 2
Line 2 C SourceInvLine 3
```

CR batch:

```text
B SoLineNo 2
C SoLineNo 3
```

Valuation resolves original B/C outbound facts.

## Scenario H — Stock Count positive variance

Live:

```text
90
```

Count:

```text
100
```

System generates +10 ADJ.

Stock Master PurchasePrice:

```text
RM9.00
```

Authoritative WAC state:

```text
RM3.50
```

Expected:

```text
+10 valued at RM3.50
```

not RM9.00.

Under FIFO use approved FIFO positive-ADJ system rule.

Under Standard use effective Standard Cost.

---

# 36. Final architectural outcome

After implementation:

```text
             USER CONTROLS PHYSICAL TRANSACTION
                         |
                         v
                 Transaction Service
                         |
              validates source / qty /
             warehouse / lot / lineage
                         |
                         v
                  Save NEW document
                         |
                         v
                       POST
                         |
                         v
              IvInventoryPostingService
                         |
                         v
              StockPostingCoordinator
                         |
                         v
              InventoryValuationService
                /         |         \
               /          |          \
             WAC         FIFO       STANDARD
               \          |          /
                \         |         /
                         v
               StockValuationFact
                  FINANCIAL TRUTH
```

The user controls:

```text
what moved
where
how much
why
which source document
```

The costing architecture controls:

```text
what that movement is financially worth
```

The only controlled exception is permitted inbound adjustment cost evidence under the method-specific rules above.

---

# 37. Approval score

**Architecture:** 10/10  
**Repo grounding:** 10/10  
**Costing integrity:** 10/10  
**FIFO correctness:** 10/10  
**Standard Cost correctness:** 10/10  
**Customer Return lineage:** 10/10  
**Stock Count coverage:** 10/10  
**Auditability:** 10/10  
**User experience:** 10/10  
**Testability:** 10/10  
**Implementation scope:** Controlled  
**Historical safety:** Controlled  
**Database change:** one focused nullable Sales CN source-line provenance column  
**Ready for Cursor / Grok 4.7:** **YES**

# APPROVED

Grok 4.7 may implement this plan against the verified `production` branch, provided it follows the exact method-specific costing rules, lineage requirements, test infrastructure, and non-regression constraints above.
