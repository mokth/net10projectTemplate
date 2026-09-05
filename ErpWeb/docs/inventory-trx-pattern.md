# Inventory stock-in / stock-out document pattern

This note is the clone guide for new inventory transaction screens (Customer Return, Vendor Return, Scrap, and so on).

**MI** and **MR** are the two standard implementations. Do not invent a new UI or posting architecture. Pick a family, clone the matching screens and services, then change only the named deltas.

Related notes:

- [inventory-posting-explain.md](inventory-posting-explain.md) — how MI posting moves stock
- [how_lotno_generated.md](how_lotno_generated.md) — lot number on receipt entry
- [inventory-stock-lot.md](inventory-stock-lot.md) — lot vs on-hand vs history
- [ivlot_explain.md](ivlot_explain.md) — `IvLot` columns

---

## How to instruct the agent

Every new trx type task should say four things. If a delta is not listed, keep the MI/MR behaviour.

1. **Family:** Stock IN or Stock OUT.
2. **Clone from:** the file list for that family (below).
3. **Identity:** `TrxType`, menu code, routes, CSS prefix, page titles.
4. **Deltas only:** extra header fields, extra line rules, cancel yes/no, posting differences.

Do **not** ask the agent to extract a shared base class or generic document framework unless this spec says so.

Paste this shape into the chat (fill the blanks):

```text
Follow ErpWeb/docs/inventory-trx-pattern.md.

Add {Name} (stock {IN|OUT}).

Family: Stock {IN|OUT}. Clone from {MR|MI}, not the other family.

Read the clone file list in that doc first, then copy those files.

Identity:
- TrxType: IvTrxTypes.{ExistingConstant} ("{CODE}") — reuse if already defined
- Menu: {INV_...}
- Routes: /inventory/{slug}
- CSS prefix: {xx}-
- Titles: {human name}

Same as the family:
- CRUD, list filters, new/edit/view, post, rollback
- Line model of that family (To* for IN, FromBalLocId for OUT)
- Shared RunningNumberKeys.IvBatch
- Posting: reuse the family posting method; only change TrxType check + menu permission unless this spec says the movement rules differ

Different from the family (only this):
- {header field / line rule / cancel yes-no}

Do not:
- refactor MI/MR into a shared base
- invent a new posting engine
- mix IN line fields with OUT line fields
- change lot generation unless this spec says so
```

`@` the four razor files plus the two Core services of the family in the same message.

---

## Two families

| Family | Canonical screens | Stock movement | Line identity | Typical new types |
| --- | --- | --- | --- | --- |
| **Stock IN** | Miscellaneous Receipt (MR) | Increase on-hand | Destination: `ToWarehouse` / `ToLocation` / `ToLotNo` | Customer Return (`CR`), Goods Receive (`GR`), Finished Goods (`FG`) |
| **Stock OUT** | Miscellaneous Issue (MI) | Decrease on-hand | Source pile: `FromBalLocId` | Vendor Return (`VR`), Sales Out (`SP`), Issue to Production (`IP`), Scrap |

Transfer (`TR`) is a **third** family (from **and** to). Spec: [stock_transfer_implementation_plan_v8.md](stock_transfer_implementation_plan_v8.md). Clone **MI chrome** (list, post/rollback/cancel) and add MR-style destination warehouse/location on each line. Do not dispatch TR into MI/MR posting methods. Do not implement v7 (`InventoryDocument` / `StockLedger`).

Codes already live in `ErpWeb.Core/Inventory/IvTrxConstants.cs` (`IvTrxTypes`). Reuse them. Do not invent a second code for the same document.

---

## Two layers — do not collapse them

| Layer | Job | Lives in |
| --- | --- | --- |
| **Document service** | Draft CRUD, search, delete, optional cancel, then call posting with a trx type | `IIvMiscReceiptService` / `IIvMiscIssueService` (and the clone) |
| **Posting service** | Lock batch, move `IvBalLoc`, write `IvTrxHistory`, mark POSTED or roll back | `IIvInventoryPostingService` |

The new page talks to its own document service. That service calls:

```csharp
_posting.PostAsync(IvTrxTypes.{Type}, batchNos, cancellationToken);
_posting.RollbackAsync(IvTrxTypes.{Type}, batchNos, cancellationToken);
```

It must not embed balance SQL.

If the new type’s stock movement is the same as the family, **dispatch into the existing family method**. Change only:

- `TrxType` check on the locked batch
- menu code used for `PermissionCodes.Post` / `Rollback`
- user-facing error text (“receipt” vs “vendor return”)

Copy `PostInventoryMRAsync` / `PostInventoryMIAsync` only when the movement rules actually differ (for example scrap must land as `DAMAGED`, or a return cannot go negative in a new way).

Today `DispatchAsync` only handles `MR` and `MI`. A new type **must** add a branch there, or posting will return `Posting is not implemented for transaction type '…'`.

---

## What is already shared — do not duplicate

| Piece | Where |
| --- | --- |
| Document header / lines | `IvTrxBatch`, `IvTrxBatchDetail` |
| On-hand pile | `IvBalLoc` |
| Posted ledger | `IvTrxHistory` |
| Lot identity | `IvLot` (created on **post**, not on save) |
| Batch number | `RunningNumberKeys.IvBatch` (`IV_BATCH`) — one sequence for all inventory docs |
| Lookups | `IIvInventoryLookupService` |
| Repositories | `IIvStockTransactionRepository`, `IIvStockPostingRepository`, `IIvStockCommonRepository` |
| Statuses | `IvBatchStatuses`: `NEW`, `POSTED`, `CANCELLED` |
| Qty rounding | `IvQty.Round` (4 dp) |
| Max post/rollback selection | `IvPostingLimits.MaxPostSelection` (10) |
| Receipt lot popup helper | `ErpWeb.UI/Inventory/InventoryLotEntryState.cs` (stock IN only) |

---

## Clone file list

### Stock IN — copy from MR

| Role | Path |
| --- | --- |
| List UI | `ErpWeb.UI/Inventory/Transactions/IvMiscReceiptList.razor` |
| List code | `ErpWeb.UI/Inventory/Transactions/IvMiscReceiptList.razor.cs` |
| List CSS | `ErpWeb.UI/Inventory/Transactions/IvMiscReceiptList.razor.css` |
| Document UI | `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor` |
| Document code | `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor.cs` |
| Document CSS | `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor.css` |
| DTOs + interface | `ErpWeb.Core/Inventory/IIvMiscReceiptService.cs` |
| Document service | `ErpWeb.Core/Inventory/IvMiscReceiptService.cs` |
| Posting | `ErpWeb.Core/Inventory/IvInventoryPostingService.cs` (`DispatchAsync` + `PostInventoryMRAsync` / `RollBackInventoryMRAsync`) |
| Tests | `ErpWeb.Tests/IvMiscReceiptServiceTests.cs` |

MR document service methods: Peek, lookups, Search, Get, SaveNew, Update, Delete, Post, Rollback. **No** `CancelAsync`.

### Stock OUT — copy from MI

| Role | Path |
| --- | --- |
| List UI | `ErpWeb.UI/Inventory/Transactions/IvMiscIssueList.razor` |
| List code | `ErpWeb.UI/Inventory/Transactions/IvMiscIssueList.razor.cs` |
| List CSS | `ErpWeb.UI/Inventory/Transactions/IvMiscIssueList.razor.css` |
| Document UI | `ErpWeb.UI/Inventory/Transactions/IvMiscIssue.razor` |
| Document code | `ErpWeb.UI/Inventory/Transactions/IvMiscIssue.razor.cs` |
| Document CSS | `ErpWeb.UI/Inventory/Transactions/IvMiscIssue.razor.css` |
| DTOs + interface | `ErpWeb.Core/Inventory/IIvMiscIssueService.cs` |
| Document service | `ErpWeb.Core/Inventory/IvMiscIssueService.cs` |
| Posting | `ErpWeb.Core/Inventory/IvInventoryPostingService.cs` (`DispatchAsync` + `PostInventoryMIAsync` / `RollBackInventoryMIAsync`) |
| Tests | `ErpWeb.Tests/IvMiscIssuePostingServiceTests.cs` |

MI also has `CancelAsync` (NEW → `CANCELLED`). Include cancel on the clone only if the task says so.

### Always touch for a new type (both families)

| Role | Path |
| --- | --- |
| Menu constant | `ErpWeb.Core/Menus/MenuCodes.cs` |
| Menu XML | `ErpWeb/Menus/menus.xml` (under `INVENTORY`) |
| DI | `ErpWeb.Core/CoreServiceCollectionExtensions.cs` |
| Trx type | `ErpWeb.Core/Inventory/IvTrxConstants.cs` — reuse existing constant |

Permissions already exist: `ACCESS`, `ADD`, `EDIT`, `DELETE`, `POST`, `ROLLBACK`, and `CANCEL` if used. See `ErpWeb.Core/Menus/PermissionCodes.cs`.

---

## Delta checklist (must change; do not copy blindly)

| Item | Example — Vendor Return (OUT, clone MI) |
| --- | --- |
| `IvTrxTypes` | `VendorReturn` = `"VR"` (already defined) |
| `MenuCodes` | e.g. `InventoryVendorReturn` = `"INV_VENDOR_RETURN"` |
| `menus.xml` | Name, `Route`, `SortOrder` |
| Routes | `/inventory/vendor-return`, `/inventory/vendor-return/{new\|edit\|view}/{BatchNo}` |
| Page titles, chips, toasts, empty text | “Vendor return”, not “miscellaneous issue” |
| CSS prefix | `vr-` not `mi-` / `mr-` |
| `GridKey` | `inv-vendor-return-list` |
| Service / DTO names | `IvVendorReturnService`, keep `FromBalLocId` |
| `MenuAuthorize` + every `CanAsync` | new menu code |
| Posting `DispatchAsync` | `VR` → family stock-out method + VR menu |
| Extra header fields | only if the spec lists them |
| Extra line rules | only if the spec lists them |
| Cancel | MI has it; MR does not. Spec must say yes or no. |

Rename CSS classes, routes, and user-visible strings. Keep layout, modes (`new` / `edit` / `view`), list filters, confirm dialogs, and dirty-check behaviour.

---

## Line model — do not mix

**Stock IN (MR family)**

- Line writes destination warehouse / location / lot.
- Posting finds or creates the `IvBalLoc` slice and **adds** qty.
- Lot-controlled items use `InventoryLotEntryState` on the add/edit popup.
- `IvLot` is created at **post**, not at save.

**Stock OUT (MI family)**

- Line points at an existing pile: `FromBalLocId`.
- Posting **subtracts** from that pile. It does not pick “any stock of this item”.
- UI available qty is informational. Server validates on save and again on post.
- Do not generate a new lot on issue.

---

## UI behaviour to keep

List page:

- Server-side grid, search debounce, status + date filters
- Buttons gated by permission: Add, Edit, Delete, Post, Rollback (and Cancel if specified)
- Confirm dialog before Delete / Post / Rollback / Cancel
- Post and rollback limited to `IvPostingLimits.MaxPostSelection`
- Open: Add → `/…/new`, row → `/…/view/{BatchNo}`, Edit → `/…/edit/{BatchNo}`

Document page:

- Modes: `new`, `edit`, `view`
- Header: trx date, status (read-only), ref no (`AUTO` if empty), remark
- Lines in a grid; add/edit via popup
- Save only when there is at least one line
- After save: navigate to view mode
- View of a `NEW` document can switch to edit if the user has Edit
- Posted / cancelled documents are read-only

---

## Do not

- Refactor MI/MR into a shared base while adding a new type
- Put posting SQL in the document service or the Blazor page
- Use `FromBalLocId` on a stock-in document, or `ToWarehouse` as the stock identity on a stock-out document
- Allocate a new running-number key (keep `IV_BATCH`)
- Add a new `IvTrxTypes` value when `CR` / `VR` / `GR` / … already exists
- Create `IvLot` or change `IvBalLoc` on draft save
- Skip tests: clone the family test file and assert the new `TrxType`, menu, and the listed deltas

---

## When to extract shared UI later

Clone first. Extract only after **three or more** documents of the same family exist and the chrome is still identical:

- shared list chrome (search, filters, post/rollback confirm)
- shared document chrome (hero, toast, new/edit/view)
- posting keyed by family (`StockIn` vs `StockOut`) instead of copied methods

Until then, clone + delta is the rule.
