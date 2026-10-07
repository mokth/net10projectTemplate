# Inventory Unit-Cost Authority Hardening Plan
## APPROVED — 10/10 — Grok 4.7 Implementation Plan

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `production`  
**Verified HEAD:** `313655aa4b828bc29dde60d1a45515cf998c1ec9`  
**Latest commit reviewed:** `checked in costing enhancement`  
**Scope:** Inventory transaction unit-cost authority for Miscellaneous Issue, Scrap, Stock Transfer, Vendor Return, Stock Return / Customer Return, and Stock Adjustment.

---

# 1. Approval status

**Status: APPROVED FOR IMPLEMENTATION**

This plan is approved because it preserves the existing authoritative V2 inventory valuation architecture instead of introducing a second costing path.

The current repository already has the hard part implemented correctly in:

- `ErpWeb.Core/StockLedger/InventoryValuationService.cs`
- `ErpWeb.Core/StockLedger/Costing/MovingAverageCostingStrategy.cs`
- `ErpWeb.Core/StockLedger/Costing/FifoCostingStrategy.cs`
- `ErpWeb.Core/StockLedger/Costing/StandardCostingStrategy.cs`
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`

The defect is primarily that several transaction screens and save services still expose or trust an old-style editable `UnitPrice`, even though the financial ledger already determines authoritative inventory cost independently.

The implementation must therefore **align the transaction UI and save contracts with the existing valuation engine**. Do **not** create another cost calculator and do **not** let transaction-line `UnitPrice` override the stock ledger for stock-out transactions.

---

# 2. Business rule to implement

## 2.1 ERP-wide rule

Separate these two concepts permanently:

- **Commercial price** = purchase price, PO price, supplier return price, sales price, customer refund price.
- **Inventory unit cost** = financial value assigned by the inventory costing engine.

For stock-controlled inventory, ordinary users must not determine inventory valuation by typing a price into an inventory movement screen.

### Authoritative inventory cost source

| Cost method | Authoritative source |
|---|---|
| Moving / weighted average | `StockCostState` / `InventoryValuationService` current pool cost |
| FIFO | Actual `StockFifoLayer` consumption |
| Standard cost | Effective `ItemStandardCostRevision` resolved by `IItemStandardCostResolver` |
| Reversal | Exact original `StockValuationFact` |
| Customer return | Exact original outbound valuation fact(s) |
| Transfer | Exact source-out value transferred to destination |

`StockValuationFact` remains the historical monetary authority.

`IvBalLoc.UnitPrice`, `IvTrxBatchDetail.UnitPrice`, and master `PurchasePrice` must be treated as operational/reference values unless the valuation engine explicitly accepts approved inbound cost evidence.

---

# 3. Target transaction behavior

| Transaction | Physical direction | Inventory cost authority | User may edit inventory cost? |
|---|---:|---|---:|
| Miscellaneous Issue (`MI`) | OUT | Current costing engine | **No** |
| Scrap (`SC`) | OUT | Current costing engine | **No** |
| Vendor Return (`VR`) | OUT | Current costing engine | **No** |
| Stock Transfer (`TR`) | OUT + IN | Exact source-out value -> destination | **No** |
| Stock Return / Customer Return (`CR`) | IN | Exact original sale outbound cost | **No** |
| Stock Adjustment negative | OUT | Current costing engine | **No** |
| Stock Adjustment positive, existing authoritative cost | IN | System current cost | **No by default** |
| Stock Adjustment positive, exceptional/manual/opening | IN | Approved cost evidence | **Yes, only with `PRICE_OVERRIDE`** |

---

# 4. Current repository findings

## 4.1 Miscellaneous Issue

Files:

- `ErpWeb.UI/Inventory/Transactions/IvMiscIssue.razor`
- `ErpWeb.UI/Inventory/Transactions/IvMiscIssue.razor.cs`
- `ErpWeb.Core/Inventory/IIvMiscIssueService.cs`
- `ErpWeb.Core/Inventory/IvMiscIssueService.cs`

Current UI exposes editable:

```text
Unit price
Line amount
```

Current item selection initializes:

```csharp
Popup.UnitPrice = item.PurchasePrice ?? 0m;
```

Current service validates and persists request `UnitPrice`.

This is misleading because `InventoryValuationService` already values the outbound issue using the configured costing method rather than this user-entered value.

### Required result

`UnitPrice` must not be user-authoritative.

---

## 4.2 Scrap

Files:

- `ErpWeb.UI/Inventory/Transactions/IvScrap.razor`
- `ErpWeb.UI/Inventory/Transactions/IvScrap.razor.cs`
- `ErpWeb.Core/Inventory/IIvScrapService.cs`
- `ErpWeb.Core/Inventory/IvScrapService.cs`

Current item selection also initializes:

```csharp
Popup.UnitPrice = item.PurchasePrice ?? 0m;
```

and UI allows editing.

Scrap is an inventory OUT movement. `InventoryValuationService` correctly classifies it as:

```text
SCRAP_OUT
```

and values it from WAC/FIFO/Standard.

### Required result

Remove user authority over inventory cost.

---

## 4.3 Stock Transfer

Files:

- `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor`
- `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor.cs`
- `ErpWeb.Core/Inventory/IIvStockTransferService.cs`
- `ErpWeb.Core/Inventory/IvStockTransferService.cs`
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`

Current UI exposes editable `Unit price`.

Current service contains:

```csharp
var unitPrice =
    line.UnitPrice > 0m
        ? line.UnitPrice
        : bal.UnitPrice ?? item.PurchasePrice ?? 0m;
```

This must be hardened because user input should never affect the source reference value.

The posting layer is already substantially correct:

- it locks the source balance,
- captures source cost / source unit-price reference,
- posts source OUT,
- posts destination IN,
- transfers the source value,
- the valuation engine makes the transfer financially value-neutral.

`InventoryValuationService` already restores the exact source-out valuation to the transfer receipt for WAC, FIFO, and Standard.

### Required result

The UI/service must stop pretending the user controls transfer valuation.

---

## 4.4 Vendor Return

Files:

- `ErpWeb.UI/Inventory/Transactions/IvVendorReturn.razor`
- `ErpWeb.UI/Inventory/Transactions/IvVendorReturn.razor.cs`
- `ErpWeb.Core/Inventory/IIvVendorReturnService.cs`
- `ErpWeb.Core/Inventory/IvVendorReturnService.cs`
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`

Current UI lets the inventory user edit `Unit price`.

The transaction is linked to PO number/release/line, but the physical inventory movement is an OUT transaction.

The inventory value removed from stock must be determined by the costing method.

The commercial amount credited by the supplier belongs to Procurement / Purchase CN logic and must not be conflated with inventory cost.

### Required result

Generic Inventory Vendor Return cannot accept user-entered inventory cost.

---

## 4.5 Stock Return / Customer Return

Files:

- `ErpWeb.UI/Inventory/Transactions/IvStockReturn.razor`
- `ErpWeb.UI/Inventory/Transactions/IvStockReturn.razor.cs`
- `ErpWeb.Core/Inventory/IIvStockReturnService.cs`
- `ErpWeb.Core/Inventory/IvStockReturnService.cs`
- `ErpWeb.Core/StockLedger/InventoryValuationService.cs`

Current `IvStockReturnService` saves:

```csharp
TrxType = IvTrxTypes.CustomerReturn
```

The valuation engine has strong return costing logic:

```text
Customer Return
 -> resolve exact invoice line
 -> find original outbound valuation facts
 -> account for prior return allocations
 -> restore original cost
```

`ResolveOriginalSaleCostAsync(...)` requires exact invoice identity and line.

However, the generic Inventory Stock Return UI/service currently does not persist the exact invoice reference required by that resolver.

Existing inventory detail/history schema already contains:

- `InvNo`
- `SoLineNo`
- `DoNo`

Therefore **no database schema change is needed** merely to persist source invoice and source invoice line.

### Required result

Stock Return must become an exact-source customer return workflow. It must not rely on user-entered `UnitPrice`.

---

## 4.6 Stock Adjustment

Files:

- `ErpWeb.UI/Inventory/Transactions/IvStockAdjustment.razor`
- `ErpWeb.UI/Inventory/Transactions/IvStockAdjustment.razor.cs`
- `ErpWeb.Core/Inventory/IIvStockAdjustmentService.cs`
- `ErpWeb.Core/Inventory/IvStockAdjustmentService.cs`
- `ErpWeb.Model/Entities/Inventory/InventoryCostEvidenceTypes.cs`

The current service already has the right architecture:

```text
MANUAL_APPROVED
ZERO_COST_APPROVED
OPENING_APPROVED
```

and checks:

```csharp
PermissionCodes.PriceOverride
```

It also correctly recognizes:

```csharp
// A negative adjustment is an issue and its entered price is never financial authority.
```

The remaining problem is UX and persistence ambiguity: the UI still exposes `Unit price` as if any user can type it, and the service currently falls back to `bal.UnitPrice ?? item.PurchasePrice`.

### Required result

- Negative adjustments: system cost only.
- Positive adjustments: system cost by default.
- Manual/opening/zero-cost evidence: explicit privileged override only.
- `PurchasePrice` must not silently become authoritative inventory valuation.

---

# 5. Architectural decisions

## AD-01 — Keep `InventoryValuationService` as the only financial authority

Do not duplicate WAC/FIFO/Standard calculations inside Razor pages or transaction services.

The transaction service may persist operational/reference values, but final financial amount is produced only by the stock-ledger valuation pipeline.

---

## AD-02 — Do not use `StockMaster.PurchasePrice` as fallback inventory valuation

Remove this pattern from the affected transaction workflows:

```csharp
item.PurchasePrice ?? 0m
```

where it is being used to populate or default inventory valuation for:

- MI
- Scrap
- Transfer
- Vendor Return
- Customer Return
- normal Stock Adjustment

Purchase price is commercial/master data, not authoritative inventory cost.

---

## AD-03 — Keep existing `UnitPrice` DTO/request properties temporarily for compatibility

Do **not** perform a broad breaking contract removal in this change.

For these existing request DTOs, keep `UnitPrice` so existing tests/internal callers continue compiling, but make the server authoritative:

- MI request `UnitPrice`: ignored for financial authority.
- Scrap request `UnitPrice`: ignored.
- Transfer request `UnitPrice`: ignored.
- Vendor Return request `UnitPrice`: ignored.
- Customer Return request `UnitPrice`: ignored.
- Negative Adjustment `UnitPrice`: ignored.
- Positive Adjustment `UnitPrice`: honored only when valid structured cost evidence is supplied and permission is granted.

Add XML comments stating that the field is legacy/backward-compatible and is not authoritative unless specifically allowed by positive adjustment cost evidence.

This avoids an unnecessary all-at-once API break while closing the financial-control hole.

---

## AD-04 — Do not add a new database column for “ActualUnitCost”

Actual financial values already exist in `StockValuationFact`.

Do not create another monetary authority in `IvTrxBatchDetail`.

If a future posted-document UI must show actual cost, query the valuation facts.

---

## AD-05 — Draft transaction screens must not present a fake exact cost

For outbound FIFO documents, exact cost can span multiple FIFO layers.

Therefore a draft line must not show an editable or falsely authoritative “Unit Price”.

For this implementation:

- Remove editable cost controls from the affected outbound transaction popups.
- Remove or relabel draft `Amount` totals that are simply `Qty x UnitPrice`.
- Prefer text such as:

```text
Inventory cost: Calculated by system when posted
```

For posted transactions, authoritative cost is available in costing inquiry / valuation facts.

Do not manufacture a fake exact FIFO amount from `PurchasePrice`.

---

# 6. Implementation workstream A — shared cost-authority guardrail

## 6.1 Add a central policy helper

Create:

`ErpWeb.Core/Inventory/InventoryCostAuthorityPolicy.cs`

Suggested responsibilities:

```csharp
public static class InventoryCostAuthorityPolicy
{
    public static bool IsSystemCostOutbound(string trxType);

    public static bool AllowsManualPositiveAdjustment(
        string trxType,
        decimal signedQty,
        string? costEvidenceType);

    public static bool IsUserPriceFinancialAuthority(
        string trxType,
        decimal signedQty,
        string? costEvidenceType);
}
```

Minimum rules:

```text
MI                 -> false
SC                 -> false
VR                 -> false
TR                 -> false
CR                 -> false
ADJ negative       -> false
ADJ positive/null evidence -> false
ADJ positive + approved evidence -> true
```

The helper must **not calculate cost**. It only expresses authority rules.

### Tests

Create:

`ErpWeb.Tests/Inventory/Transaction/InventoryCostAuthorityPolicyTests.cs`

Cover every transaction/direction.

---

# 7. Implementation workstream B — Miscellaneous Issue

## Files

Modify:

- `ErpWeb.UI/Inventory/Transactions/IvMiscIssue.razor`
- `ErpWeb.UI/Inventory/Transactions/IvMiscIssue.razor.cs`
- `ErpWeb.Core/Inventory/IIvMiscIssueService.cs`
- `ErpWeb.Core/Inventory/IvMiscIssueService.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvMiscIssuePostingServiceTests.cs`

## UI changes

Remove editable:

```text
Unit price
Line amount = Qty x Unit price
```

from Add/Edit line popup.

Remove `Unit Price` / `Amount` columns from the editable grid, or label them clearly as non-authoritative reference values if retained for posted view.

Add small read-only help text:

```text
Inventory cost is calculated automatically when the document is posted.
```

Remove:

```csharp
Popup.UnitPrice = item.PurchasePrice ?? 0m;
```

Do not use item master PurchasePrice to initialize valuation.

## Service changes

Inside line validation:

1. Continue resolving selected `IvBalLoc`.
2. Ignore incoming `line.UnitPrice` for financial authority.
3. If an operational display snapshot is still required by existing DTOs, resolve:

```csharp
var referenceUnitPrice = bal.UnitPrice ?? 0m;
```

Do **not** fall back to `item.PurchasePrice`.

4. Persist only this server-resolved reference into `IvTrxBatchDetail.UnitPrice`.
5. Add comment that outbound valuation ignores this field; authoritative cost is produced by `InventoryValuationService`.

## Tests

Add tests proving:

- Request `UnitPrice = 9999m` does not become financial issue cost.
- WAC MI posts at current WAC.
- FIFO MI consumes FIFO layers regardless of request UnitPrice.
- Standard-cost MI uses effective standard cost.
- PurchasePrice different from inventory cost does not change valuation.
- rollback still performs exact valuation reversal.

---

# 8. Implementation workstream C — Scrap

## Files

Modify:

- `ErpWeb.UI/Inventory/Transactions/IvScrap.razor`
- `ErpWeb.UI/Inventory/Transactions/IvScrap.razor.cs`
- `ErpWeb.Core/Inventory/IIvScrapService.cs`
- `ErpWeb.Core/Inventory/IvScrapService.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvScrapPostingServiceTests.cs`

## UI

Same treatment as MI.

Remove editable Unit Price.

Remove misleading line amount based on user input.

Display:

```text
Inventory cost is calculated automatically when posted.
```

Remove:

```csharp
Popup.UnitPrice = item.PurchasePrice ?? 0m;
```

## Service

Replace trust in:

```csharp
line.UnitPrice
```

with a server-owned reference derived from the selected balance if a reference field must still be populated:

```csharp
bal.UnitPrice ?? 0m
```

Never use `PurchasePrice` as fallback.

## Tests

Verify:

- malicious/extreme request UnitPrice is ignored.
- WAC scrap amount is costing-state amount.
- FIFO scrap consumes correct layers.
- Standard scrap uses standard cost.
- `StockValuationFact.MovementCode == "SCRAP_OUT"`.
- rollback reverses exact original fact.

---

# 9. Implementation workstream D — Stock Transfer

## Files

Modify:

- `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor`
- `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor.cs`
- `ErpWeb.Core/Inventory/IIvStockTransferService.cs`
- `ErpWeb.Core/Inventory/IvStockTransferService.cs`
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs` only where comments/assertions are useful; do not replace its existing transfer algorithm.
- `ErpWeb.Tests/Inventory/Transaction/IvStockTransferPostingServiceTests.cs`

## UI

Remove editable Unit Price.

Remove `Popup.UnitPrice = item.PurchasePrice`.

Do not present transfer price as user-entered data.

Recommended line message:

```text
Transfer preserves the source inventory value automatically.
```

## Service

Change this existing logic:

```csharp
var unitPrice =
    line.UnitPrice > 0m
        ? line.UnitPrice
        : bal.UnitPrice ?? item.PurchasePrice ?? 0m;
```

to server-owned reference behavior:

```csharp
var unitPrice = bal.UnitPrice ?? 0m;
```

Do not use request UnitPrice.

Do not fall back to PurchasePrice.

Preserve:

```csharp
bal.Cost
```

as the existing operational source snapshot where currently needed.

## Posting

Preserve the current correct flow:

```text
source balance lock
 -> capture source
 -> source OUT
 -> destination IN
 -> exact same financial value
```

Do not replace:

- source balance locking
- destination lock ordering
- exact transfer value behavior
- V2 history creation
- valuation sealing

## Tests

Extend existing tests to prove:

1. Request UnitPrice cannot alter destination valuation.
2. Existing test where source has:
   - `unitPrice = 5`
   - `cost = 120`
   remains correct operationally.
3. WAC transfer:
   - source cost amount = destination receipt cost amount.
   - net item/branch inventory value change = zero.
4. FIFO transfer across multiple layers:
   - transfer OUT consumes correct layer values,
   - transfer IN receives exact aggregate source-out amount,
   - no artificial gain/loss.
5. Standard transfer:
   - OUT and IN at standard cost,
   - zero net branch/item valuation impact.
6. rollback restores exact original values.

---

# 10. Implementation workstream E — Vendor Return

## Files

Modify:

- `ErpWeb.UI/Inventory/Transactions/IvVendorReturn.razor`
- `ErpWeb.UI/Inventory/Transactions/IvVendorReturn.razor.cs`
- `ErpWeb.Core/Inventory/IIvVendorReturnService.cs`
- `ErpWeb.Core/Inventory/IvVendorReturnService.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvVendorReturnPostingServiceTests.cs`

## Critical business separation

Vendor Return has two monetary concepts:

```text
A. inventory cost removed from stock
B. supplier commercial credit / PO return price
```

This inventory screen owns A.

Purchase Return / Purchase Credit Note owns B.

Do not let the generic inventory Vendor Return screen write a “Unit Price” and imply it is both.

## UI

Remove editable inventory Unit Price.

Keep:

- PO No
- PO Release
- PO Line
- item
- source warehouse/location/lot
- return qty
- reason/remark

Optionally show PO unit price as a **read-only reference** only if already readily available, labelled:

```text
PO Price (reference only)
```

Never label it as Inventory Unit Cost.

## Service

Continue validating exact PO line and received/return quantity rules.

Ignore incoming `line.UnitPrice` as inventory cost.

For legacy reference persistence, use selected source balance:

```csharp
var referenceUnitPrice = bal.UnitPrice ?? 0m;
```

Do not use `PurchasePrice`.

## Purchase-owned Vendor Returns

Do not break existing Purchase Credit Note ownership guard:

```text
PoCdn-owned VR batch
```

The Procurement document may still own its commercial price.

This change applies to the inventory valuation authority only.

## Tests

Verify:

- request UnitPrice cannot alter inventory valuation.
- PO price different from inventory cost does not alter inventory OUT valuation.
- WAC/FIFO/Standard use authoritative stock cost.
- cumulative PO return quantity validations remain unchanged.
- purchase-CN-owned VR protections remain unchanged.
- rollback restores quantity and exact valuation.

---

# 11. Implementation workstream F — Stock Return / Customer Return

This is the most important structural correction.

## 11.1 Target workflow

Replace:

```text
Choose arbitrary item
 -> type qty
 -> type Unit Price
 -> post customer return
```

with:

```text
Select original posted invoice
 -> select exact invoice line
 -> item/description/UOM auto-resolve
 -> enter return qty
 -> choose destination warehouse/location/lot/status
 -> save
 -> post
 -> restore original outbound cost
```

## Files

Modify:

- `ErpWeb.Core/Inventory/IIvStockReturnService.cs`
- `ErpWeb.Core/Inventory/IvStockReturnService.cs`
- `ErpWeb.UI/Inventory/Transactions/IvStockReturn.razor`
- `ErpWeb.UI/Inventory/Transactions/IvStockReturn.razor.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvStockReturnServiceTests.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvStockReturnPostingServiceTests.cs`

Reuse concepts from:

- `ErpWeb.Core/Sales/ISaCdnService.cs`
- `ErpWeb.Core/Sales/SaCdnService.cs`
- `ErpWeb.UI/Sales/Transactions/SaCdn.razor.cs`

Do not create a direct UI dependency from Inventory Razor component to the Sales page.

---

## 11.2 Extend stock-return DTOs

Add to line DTO/request:

```csharp
public string SourceInvNo { get; set; } = string.Empty;
public short SourceInvoiceLine { get; set; }
```

Naming may use `InvNo` / `InvoiceLine` instead if preferred, but the API must clearly mean **original source invoice and exact source invoice line**.

Map these to existing database columns:

```text
IvTrxBatchDetail.InvNo    <- SourceInvNo
IvTrxBatchDetail.SoLineNo <- SourceInvoiceLine
```

No schema migration is required.

When posting, existing history copy must preserve:

```text
IvTrxHistory.InvNo
IvTrxHistory.SoLineNo
```

These are the exact fields already consumed by `ResolveOriginalSaleCostAsync`.

---

## 11.3 Add source invoice lookup APIs

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

Add DTOs:

```csharp
IvStockReturnInvoiceLookupRow
IvStockReturnInvoiceLineLookupRow
```

Invoice lookup returns only:

- same CompanyCode
- same BranchCode
- POSTED invoices
- appropriate stock-controlled lines

Line lookup returns at minimum:

- InvNo
- invoice line no
- item code
- description
- sold/base qty as available
- UOM
- DO provenance if useful
- item stock-control information

Use tenant-safe queries.

---

## 11.4 Save-time validation

`IvStockReturnService.ValidateLineAsync` must verify:

1. `SourceInvNo` required.
2. `SourceInvoiceLine > 0`.
3. Source invoice exists in same company/branch.
4. Source invoice is POSTED.
5. Exact invoice line exists.
6. Invoice-line item matches return item.
7. Item is stock controlled.
8. Return quantity > 0.
9. Destination warehouse/location/status/lot rules remain valid.
10. User-supplied UnitPrice is not financial authority.

Persist:

```csharp
InvNo = source invoice
SoLineNo = source invoice line
```

Do not persist `PurchasePrice` as return valuation.

---

## 11.5 UI

Remove editable `Unit price`.

Add source controls:

```text
Source Invoice     [Search]
Source Line        [Search / select]
Item               auto-filled / read-only after source selection
Description        auto-filled
Return Qty
Destination WH
Destination Location
Lot / Expiry
Status
Reason
Remark
```

On source-line selection:

- auto-fill item code/description/UOM,
- clear incompatible destination/lot data if item changed,
- do not populate PurchasePrice.

Display help:

```text
Return inventory cost is restored from the original sale.
```

---

## 11.6 Posting authority

Do not rewrite the existing `ResolveOriginalSaleCostAsync` costing logic merely to support this UI.

It already:

- finds exact original outbound valuation facts,
- handles DO-linked vs direct invoice ownership,
- considers prior return allocations,
- rejects over-returned valuation quantity,
- restores original cost for WAC/FIFO,
- creates return cost allocation records,
- handles Standard-cost return variance logic.

The Stock Return fix must feed that engine the source identity it expects.

---

## 11.7 Tests

Update old tests that created arbitrary stock-return lines with only `UnitPrice`.

Seed an actual posted source invoice / outbound valuation.

Required tests:

1. save without source invoice -> reject.
2. save without source line -> reject.
3. wrong invoice line item -> reject.
4. cross-company/cross-branch invoice -> reject.
5. unposted invoice -> reject.
6. request UnitPrice = 9999 -> ignored.
7. direct invoice return -> original outbound cost restored.
8. DO-linked invoice return -> original DO valuation restored.
9. partial customer return -> proportional exact original valuation.
10. multiple partial returns -> remaining returnable valuation enforced.
11. over-return -> rejected by authoritative valuation path.
12. FIFO return -> correct original cost restored and return layer created correctly.
13. Standard return -> current Standard inventory receipt + existing standard return variance behavior preserved.
14. rollback -> exact valuation/allocation reversal.

---

# 12. Implementation workstream G — Stock Adjustment

## 12.1 Negative adjustment

### UI

When:

```text
AdjustQty < 0
```

hide/disable:

- Unit price input
- Cost Evidence
- Cost Override Reason

Show:

```text
Inventory cost is calculated automatically when posted.
```

### Service

For negative adjustment:

```text
CostEvidenceType = null
CostOverrideReason = null
```

Ignore incoming `line.UnitPrice`.

Do not fall back to PurchasePrice.

The financial valuation remains WAC/FIFO/Standard as already implemented.

---

## 12.2 Positive adjustment — normal system-cost path

Default UI state:

```text
Use system cost
```

No editable cost.

No evidence type.

No override reason.

Service behavior:

```text
adjustQty > 0
AND CostEvidenceType == null
```

must not treat incoming UnitPrice as authority.

Set detail UnitPrice to null/zero/reference only as required by compatibility, and allow `InventoryValuationService` to resolve current authoritative cost.

The existing valuation code already has special behavior for positive Stock Adjustment with no evidence:

```text
if an authoritative current cost state exists
 -> use current cost
else
 -> block with ValuationRequired
```

Preserve that behavior.

---

## 12.3 Positive adjustment — explicit override path

Only expose manual cost when user has:

```csharp
PermissionCodes.PriceOverride
```

UI:

```text
[ ] Override system cost
```

If unchecked:

- cost input hidden/read-only
- evidence hidden
- reason hidden

If checked:

show:

```text
Override Unit Cost
Cost Evidence
Approval Reason
```

Allowed evidence:

```text
MANUAL_APPROVED
OPENING_APPROVED
ZERO_COST_APPROVED
```

Server must still enforce permission even if UI is bypassed.

---

## 12.4 Evidence rules

Strengthen validation:

### `MANUAL_APPROVED`

```text
AdjustQty > 0
UnitPrice > 0
PRICE_OVERRIDE required
```

### `ZERO_COST_APPROVED`

```text
AdjustQty > 0
UnitPrice == 0
reason required
PRICE_OVERRIDE required
```

### `OPENING_APPROVED`

Recommended:

```text
AdjustQty > 0
UnitPrice > 0
reason required
PRICE_OVERRIDE required
```

If opening stock is intentionally zero-valued, use `ZERO_COST_APPROVED` rather than `OPENING_APPROVED` with zero cost.

This makes audit semantics unambiguous.

---

## 12.5 Remove PurchasePrice fallback

Current code includes:

```csharp
unitPrice = bal.UnitPrice ?? item.PurchasePrice ?? 0m;
```

Remove `item.PurchasePrice` from the valuation fallback.

For no-evidence positive adjustment:

- let the valuation engine use authoritative current `StockCostState`.
- if no authoritative state exists, posting must fail and require approved evidence.

Do not silently turn master purchase price into opening inventory cost.

---

## 12.6 Tests

Modify/add:

- `ErpWeb.Tests/Inventory/Transaction/IvStockAdjustmentPostingServiceTests.cs`

Required cases:

1. negative adjustment ignores request UnitPrice.
2. negative adjustment ignores evidence fields.
3. positive adjustment without evidence uses system current cost.
4. positive adjustment without authoritative current cost fails with valuation-required error.
5. user without `PRICE_OVERRIDE` cannot submit manual evidence.
6. MANUAL_APPROVED requires positive cost.
7. ZERO_COST_APPROVED requires exact zero and reason.
8. OPENING_APPROVED requires positive cost and reason.
9. PurchasePrice is different from authoritative cost and does not influence normal adjustment.
10. FIFO negative adjustment consumes layers.
11. Standard negative adjustment uses effective standard cost.
12. rollback reverses exact facts.

---

# 13. Implementation workstream H — posted document display

This is a **Should Implement**, but it must not delay the financial-control changes above.

## Problem

The current list/detail DTOs calculate document totals using:

```text
Qty x IvTrxBatchDetail.UnitPrice
```

That is not authoritative for system-cost transactions, especially FIFO.

## Rule

For NEW documents:

```text
Do not display a fake financial total.
```

Prefer:

```text
Cost calculated on posting
```

For POSTED documents:

derive actual value from `StockValuationFact`.

## Recommended implementation

Add a small read service:

`ErpWeb.Core/Inventory/IInventoryTransactionValuationReadService.cs`

`ErpWeb.Core/Inventory/InventoryTransactionValuationReadService.cs`

Responsibilities:

```text
Given:
CompanyCode
BranchCode
Inventory batch/source document
line

Return:
ActualBaseQty
ActualCostAmount
EffectiveUnitCost = ActualCostAmount / ActualBaseQty
CostMethod
ValuationStatus
```

This service is READ-ONLY.

It must query `StockValuationFact`; it must never calculate financial cost itself.

This can later be reused by MI, Scrap, VR, Transfer, Adjustment and costing inquiry.

If this display service is not implemented in the first commit, remove/hide misleading draft UnitPrice/Amount instead of leaving editable fake values.

---

# 14. Backward compatibility and data handling

## 14.1 Existing posted documents

Do not rewrite historical posted `IvTrxBatchDetail.UnitPrice`.

Historical monetary truth already lives in `StockValuationFact`.

Existing posted documents remain unchanged.

---

## 14.2 Existing NEW documents created before deployment

When editing a pre-change NEW document:

- ignore legacy manually entered UnitPrice for system-cost transactions,
- resolve source identity again,
- save using server-owned reference semantics,
- posting uses the valuation engine.

Do not grandfather a user-entered stock-out cost.

---

## 14.3 No database migration required for the main change

The repository already has:

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

Therefore the main hardening plan should be implemented without introducing a new monetary column.

Only add a migration if implementation discovers a proven missing index or constraint, and document that separately.

---

# 15. Security and server-side invariants

The UI is not a security boundary.

Every rule must also be enforced server-side.

## Mandatory invariants

1. MI user UnitPrice cannot alter valuation.
2. Scrap user UnitPrice cannot alter valuation.
3. Transfer user UnitPrice cannot alter valuation.
4. Vendor Return user UnitPrice cannot alter valuation.
5. Customer Return user UnitPrice cannot alter valuation.
6. Negative adjustment user UnitPrice cannot alter valuation.
7. Positive adjustment user UnitPrice is authoritative only with valid evidence and permission.
8. `PRICE_OVERRIDE` enforced server-side.
9. source invoice/PO/balance records are tenant-scoped.
10. costing method remains effective-dated as existing architecture requires.
11. posting and rollback remain atomic.
12. backdated cost-pool guards remain unchanged.
13. exact reversal behavior remains unchanged.

---

# 16. Do not change these proven components unnecessarily

Grok must **not** rewrite or simplify these areas unless a test proves a required defect:

- `InventoryValuationService.ValueMovingAveragePendingAsync`
- FIFO layer consumption algorithm
- Standard-cost strategy
- exact reversal mechanism
- StockPosting sealing
- cost-method cutover logic
- period-close logic
- `StockValuationFact` identity
- transfer locking/order
- backdated movement guards
- costing repair center
- production costing
- Goods Receipt cost calculation
- Purchase Cost Adjustment logic

This plan is an authority/UX hardening plan, not a costing-engine rewrite.

---

# 17. Required regression test matrix

Run the affected focused tests first, then the full Inventory transaction group.

## Existing test files to update/run

```text
ErpWeb.Tests/Inventory/Transaction/IvMiscIssuePostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvScrapPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockTransferPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvVendorReturnPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockReturnPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockReturnServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/IvStockAdjustmentPostingServiceTests.cs
ErpWeb.Tests/Inventory/Transaction/InventoryValuationServiceTests.cs
```

Add:

```text
ErpWeb.Tests/Inventory/Transaction/InventoryCostAuthorityPolicyTests.cs
```

if the shared policy helper is introduced.

---

# 18. Mandatory anti-tampering test pattern

For every system-cost transaction, include a test with deliberately absurd user input:

```csharp
UnitPrice = 999999m;
```

Then post.

Assert:

```text
StockValuationFact.UnitCost != 999999
StockValuationFact.CostAmount follows authoritative costing method
```

This is essential because simply making the UI read-only is insufficient.

Required for:

- MI
- Scrap
- Transfer
- Vendor Return
- Customer Return
- negative Adjustment

---

# 19. Cost-method acceptance scenarios

## 19.1 Moving average

Opening/current pool:

```text
Qty 100
Value RM350
Average RM3.50
```

Issue 10 through MI/Scrap/VR:

```text
OUT value = RM35.00
```

User-entered price must not change it.

Transfer 10:

```text
source OUT value = RM35
destination IN value = RM35
net pool value change = RM0
```

---

## 19.2 FIFO

Layers:

```text
5 @ RM3
10 @ RM4
```

Issue 8:

```text
5 @ RM3 = RM15
3 @ RM4 = RM12
total = RM27
effective = RM3.375
```

Any draft/request UnitPrice is ignored.

Transfer 8:

```text
source OUT total = RM27
destination IN total = RM27
```

---

## 19.3 Standard

Effective standard:

```text
RM3.80
```

Issue 10:

```text
inventory OUT = RM38
```

User value cannot override it.

Transfer:

```text
OUT RM38
IN RM38
```

---

## 19.4 Customer return

Original sale outbound:

```text
10 units
original cost amount RM35
```

Return 4 units.

The system must restore the correct share from the original outbound valuation facts, not:

- PurchasePrice
- current WAC
- arbitrary typed price

Prior return allocations must be considered.

---

# 20. UI acceptance criteria

## MI / Scrap / Transfer / Vendor Return

User must not see an editable inventory cost field.

User must be able to complete normal entry using only operational fields:

```text
item/source location/lot
qty
destination where relevant
reason/remark
source business reference where relevant
```

Cost message:

```text
Calculated automatically when posted
```

---

## Stock Return

User must select source invoice and exact source line.

Item is resolved from source line.

Editable inventory Unit Price is absent.

---

## Stock Adjustment

### Negative

No cost override UI.

### Positive normal

System-cost mode by default.

### Positive exception

Only authorized users see/use override controls.

Reason/evidence is mandatory according to evidence type.

---

# 21. Failure-message requirements

Use user-friendly errors.

Avoid:

```text
ValuationRequired
ReversalDependency
```

as the only UI message.

Examples:

### Customer Return missing source

```text
Select the original posted invoice and invoice line before saving this return.
```

### Positive adjustment with no system cost

```text
The system has no approved current cost for item ITEM001. Use an authorized cost override with evidence before posting this positive adjustment.
```

### Unauthorized override

```text
You are not authorized to override inventory cost.
```

### FIFO insufficiency

Retain existing meaningful FIFO / insufficient valuation quantity messages.

---

# 22. Implementation order

Grok should implement in this order.

## Phase 1 — server-side financial authority

1. Add `InventoryCostAuthorityPolicy`.
2. Harden MI service.
3. Harden Scrap service.
4. Harden Transfer service.
5. Harden Vendor Return service.
6. Harden Adjustment service.
7. Add/update anti-tampering tests.

Do this before UI work so API/service callers cannot bypass the rule.

---

## Phase 2 — UI cleanup

1. MI UI.
2. Scrap UI.
3. Transfer UI.
4. Vendor Return UI.
5. Adjustment conditional override UI.

Remove PurchasePrice defaults and editable inventory cost controls.

---

## Phase 3 — Stock Return source-link correction

1. Extend Stock Return DTOs.
2. Add invoice/invoice-line lookup methods.
3. Persist `InvNo` + `SoLineNo`.
4. Add validation.
5. Update UI.
6. Update stock-return tests with genuine source invoices/outbound valuation facts.
7. Verify `ResolveOriginalSaleCostAsync`.

---

## Phase 4 — posted valuation display

Implement read-only actual-cost display from `StockValuationFact` if desired in the same iteration.

This is lower risk after the financial authority is secured.

---

# 23. Definition of Done

Implementation is complete only when all of these are true:

- [ ] MI has no user-authoritative Unit Price.
- [ ] Scrap has no user-authoritative Unit Price.
- [ ] Stock Transfer has no user-authoritative Unit Price.
- [ ] Vendor Return has no user-authoritative inventory Unit Price.
- [ ] Customer Return restores original sale cost from exact source invoice/line.
- [ ] Negative Stock Adjustment cost is system controlled.
- [ ] Positive Stock Adjustment uses system cost by default.
- [ ] Positive manual/opening/zero-cost adjustment requires structured evidence.
- [ ] `PRICE_OVERRIDE` is enforced server-side.
- [ ] PurchasePrice is removed as inventory valuation fallback in affected flows.
- [ ] No new independent cost-calculation algorithm was introduced.
- [ ] WAC regression tests pass.
- [ ] FIFO regression tests pass.
- [ ] Standard-cost regression tests pass.
- [ ] exact rollback tests pass.
- [ ] transfer remains value-neutral.
- [ ] customer-return allocations remain exact.
- [ ] old posted history is not rewritten.
- [ ] existing NEW documents cannot preserve a manual outbound cost override.
- [ ] full affected Inventory transaction test suite passes.

---

# 24. Grok 4.7 implementation constraints

Grok must follow these constraints strictly:

1. **Read current files before editing.** Do not implement from this plan using guessed signatures.
2. Work only against the current `production` branch behavior.
3. Preserve tenant keys:
   - `CompanyCode`
   - `BranchCode`
   - existing location/lot/status identity.
4. Preserve cancellation tokens.
5. Preserve existing transaction boundaries.
6. Preserve existing access-right checks.
7. Preserve current NEW/POSTED/rollback lifecycle behavior.
8. Do not make posted documents editable.
9. Do not bypass `IvInventoryPostingService`.
10. Do not create a second cost ledger.
11. Do not use `PurchasePrice` as inventory valuation evidence.
12. Do not infer customer-return cost from current inventory.
13. Do not infer FIFO cost in UI.
14. Add tests before claiming completion.
15. If an existing test assumes user-entered outbound UnitPrice is authoritative, update that test to the new approved rule rather than preserving the old defect.

---

# 25. Final architectural outcome

After implementation:

```text
                 USER ENTERS PHYSICAL MOVEMENT
                           |
                           v
               Inventory Transaction Service
                           |
             validates stock/source/reference
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
                InventoryValuationService
                 /         |          \
                /          |           \
              WAC         FIFO       STANDARD
                \          |           /
                 \         |          /
                           v
                  StockValuationFact
                     AUTHORITATIVE
                           |
             +-------------+-------------+
             |                           |
             v                           v
       Inventory value               Cost reports
       / COGS / expense              / diagnostics
```

The user controls **what stock moved and why**.

The costing engine controls **what that stock movement is financially worth**.

The only intentional exception is an inbound positive adjustment with explicit, auditable, permission-controlled cost evidence.

---

# 26. Approval

**Architecture:** 10/10  
**Costing integrity:** 10/10  
**User-friendliness:** 10/10  
**Auditability:** 10/10  
**Backward compatibility:** 9.8/10 — legacy DTO fields retained intentionally while server authority changes  
**Implementation risk:** Controlled  
**Database migration:** Not required for the core plan  
**Ready for Grok 4.7 implementation:** **YES**

**APPROVED**
