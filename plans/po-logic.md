# Purchase Order — CRUD, PR Link, GR (GRN), Close / Lock

Sources (standard ERP, not customer flavours):

- List: `ERP/Purchasing/POForms/POView.aspx.cs`
- Entry: `ERP/Purchasing/POForms/PO.aspx.cs`, `ERP/Purchasing/POForms/PO.aspx`
- Email / PDF attach: `ERP/Purchasing/POForms/SendPOAttach.aspx.cs` (not CRUD)
- Helper: `ERPCommonUI/Purchasing/POForms/POHelper.cs`
- PR picker: `ERPCommonUI/Purchasing/POForms/PRListing.ascx.cs`
- Status / balance: `ERPClasses/BL/POOrderBL.cs`
- GR post / rollback: `ERPCommonUI/Helper/GRNHelper.cs`
- GR entry / PO pull: `ERP/Inventory/GRN/GRN.aspx.cs`
- GR PO listing: `ERP/Inventory/GRN/GRNPOListing.ascx.cs`

Screen ID: `300.2.2`. List key: `PONo;PORelNo`. List datasource: `vgridPO`.

Use this when porting Purchase Order to ERP Blazor. PO is a **procurement document**: PR consume → PO → GRN receive → later Purchase Invoice / costing. `POStat` is the only lock. There is no separate lock table.

`SendPOAttach` is email/PDF only. It is not part of CRUD.

This document records **what the current project actually does**. Recommendations for Blazor are in section 10. Open questions are in section 11.

---

## 1. Core tables and identity

**Composite key:** `PONo` + `PORelNo` (revision). Listing key is `PONo;PORelNo`.

| Table | Role |
|---|---|
| `POOrder` | Header |
| `PODetail` | Lines (`Line`, `PRNo`, `PRLineNo`, `POPurQty`, `POQty`, `RecvQty`, `BalanceQty`, tax/discount, warehouse, CJ) |
| `POPR` / `POPRDtl` | PR header / lines. Link field is `PONo` (null = not converted) |
| `POAttachFile` | Attachments (`DocID` + `RevNo`) |
| `IvBalance` | `Qty_Allocated` / `Qty_AllocatedWt` (default warehouse) on PO save |
| `POCJ` / `POCJDetail` | Contract/job qty (`CJDBalQty`) |
| `AdSmNum` / `AdSmNumDate` | Numbering |
| `AdPara` | `SupplierPrice`, `POPriceDecimal`, `POControlIcode`, `UseWeight`, `PurchaseTaxDec`, `PurOrderApprAmt` |
| Web.config `POApproval` | Turns on `PENDING` / `CHECKED` workflow |

**Qty identity on a line:**

- `POPurQty` = order qty in **purchase UOM**
- `POQty` = std qty (`PurQty × PackSize`)
- `RecvQty` = received purchase qty (updated by GRN post)
- `BalanceQty` = remaining purchase qty
- New / PR-pull: `BalanceQty = POPurQty`, `RecvQty = 0`
- After GR: `RecvQty += ToPurQty`, `BalanceQty = POPurQty - RecvQty` (tolerance from `POItemByVendor.Tolerance`)

---

## 2. Status machine (this is the lock)

`POStat` is the **only lock**.

```
OPEN ────────────────────────────────────────── pessimistic edit lock
  │
  ├─ save (no GR yet)
  │     POApproval=TRUE  → PENDING
  │     POApproval=FALSE → NEW
  │
PENDING ── CheckBy approve ──► CHECKED (if amount > AdPara.PurOrderApprAmt)
  │                              │
  └──────── approve ─────────────┴── ApprovedBy ──► NEW
                                          │
NEW ── GR posted (any remaining) ──► RECEIVED
  │                                    │
  └── all BalanceQty=0 (non-SERVICE) ──┴──► CLOSED   (auto, from GR)

FORCE CLOSE (listing) ── toggle if BalanceQty > 0 ──► CLOSED
CLOSED (with remaining qty) ── same button ──► NEW or RECEIVED

CANCELLED  (listing; no qty reversal)
```

| Status | Meaning | Edit | Delete | GR pick | Cancel | Force close |
|---|---|---|---|---|---|---|
| `OPEN` | Someone is in the entry screen | No (stuck until Refresh/Cancel) | No | Yes (not excluded) | No | No |
| `PENDING` | Waiting 1st approver (`CheckBy`) | Yes **only if** `POApproval=TRUE` | Yes **only if** approval on | No | Yes | Yes (if bal > 0) |
| `CHECKED` | Waiting 2nd approver (`ApprovedBy`) | No | No | No | Yes | Yes (if bal > 0) |
| `NEW` | Released / not received | Yes if approval **off** | Yes if approval **off** | Yes | Yes | Yes (if bal > 0) |
| `RECEIVED` | Partial GR | Yes | No | Yes | Yes | Yes (if bal > 0) |
| `CLOSED` | Fully received **or** force-closed | No (`CheckPOStat` blocks save) | No | **No** | No | Toggle **reopen** if bal > 0 |
| `CANCELLED` | Voided | No | No | No | No | No |

Listing JS mentions `POSTED`. Server never sets `POSTED`. Do not invent that status in Blazor if sharing this DB.

---

## 3. PO List (`POView.aspx.cs`)

Default entry page: `PO.aspx`.

Buttons: NEW, DELETE, PRINT, REFRESH, CLOSED (Force Closed), CANCELLED, REVISE, ATTACHMENT, COPY, SENT EMAIL TO APPROVER, APPROVE (if `POApproval`), COPY TO SO (company `GH` / `GM` only).

Grid columns: Date, PO No, Rev, PR No, Vendor Code, Vendor Name, Amount, Buyer, Status, Print Counter, Sent (`AttachSent` / `POType`), ETA Date, Created User/Date, Updated User/Date, Currency. Hidden by default: Project, refs, SI remark, quotation, ship via, contact, email, website, reg no, payment term, buying term, tax group.

Datasource: `vgridPO`. If `AdUserGroupDefault` exists for the user’s group, they see all POs; otherwise only `UserID = current user`.

Rights:

| Action | Right |
|---|---|
| New / Copy / Revise / Copy to SO | `New` |
| Edit | `Edit` |
| View | `Access` |
| Delete | `Delete` |
| Cancel / Close / Approve | `Post` |
| Print | `Print` |

### 3.1 New

Opens `PO.aspx?Type=NEW&ID=`.

### 3.2 Edit

- Approval on: status must be `PENDING` or `RECEIVED`.
- Approval off: status must be `NEW` or `RECEIVED`.
- URL: `PO.aspx?ID={PONo}&Type=Edit&PORelNO={rev}&Comp=&Branch=`.

Entry then immediately writes `POStat = OPEN` (`POHelper.UpdateItemStatus`).

### 3.3 View

Opens entry in a new window: `Type=View`. Add / delete / save disabled.

### 3.4 Copy

Any existing PO. Opens `Type=COPY`. New `PONo=AUTO`, `PORelNo=1`, `RecvQty/ReturnQty=0`, `BalanceQty=POPurQty`, **`PRNo` cleared**, date=now, buyer=current user. Attachments get new `DocKey`.

### 3.5 Revise

Only `NEW` (and not `OPEN` by someone else). Opens `Type=Revise`. New revision = old `PORelNo + 1`, date=today. **Old revision is set `CANCELLED`** on save (`updateStatus`). Details are cloned onto the new rev.

### 3.6 Delete

Listing gate:

- Approval on: status must be `PENDING`.
- Approval off: status must be `NEW`.

Then `POHelper.Delete`:

- **Only deletes header when `POStat='NEW'`** — listing and helper disagree when approval is on.
- Marks `POOrder` deleted.
- Clears `POPRDtl.PONo` for linked PR lines.
- Returns CJ qty (`UpdateCJNo`).
- Writes audit log.

`PODetail` rows are **not** marked `Deleted` in `POHelper.Delete`. Either SQL cascade exists, or details can be orphaned. Confirm on the live DB before porting.

`POPR.PONo` (header) is **not** cleared. After delete, leftover PR lines can stay hidden from the PO picker (`vLook_POPR.PONo == "NONE"`).

### 3.7 Cancel (`OnCancel`)

Rights: `Post`. Block `CLOSED`, `CANCELLED`, `OPEN`. Set `POStat = CANCELLED`, `UpdatedUID` / `Updated`.

**No** `RecvQty` / `BalanceQty` change, **no** PR unlink, **no** `IvBalance` reverse. Listing does **not** check whether GR already exists.

### 3.8 Force Close / Reopen (`OnClosed`) — Task 260804093215

Rights: `Post`. This is a **toggle**, not a one-way lock.

1. Block `CANCELLED` and `OPEN`.
2. **Must have remaining `BalanceQty > 0`** (`POOrderBL.IsPOhaveBalanceQty`). If all lines are fully received, fail: *“Unable to Reverse Close PO. No available quantity remains for this PO.”*
3. If already `CLOSED` → reopen: `POOrderBL.PONewStatus` → `RECEIVED` if any `RecvQty > 0`, else `NEW`.
4. Else → set `CLOSED`.
5. **Does not zero `BalanceQty`.** It only hides the PO from GR listing (`POStat != CLOSED`). Outstanding qty stays on the line.

Success message is always “PO Closed.” even when reopening. Error text always says “Reverse Close” even when trying to close.

Force close = **stop further GR**. It is not “complete and write off remaining.”

### 3.9 Refresh (`OnRefreshItem`)

Only when status is `OPEN` (stuck edit lock). Recalculate from lines:

- Sum `BalanceQty == 0` → `CLOSED`
- Else any line `POPurQty > BalanceQty` → `RECEIVED`
- Else if still `OPEN`: `PENDING` (approval on) or `NEW` (approval off)

### 3.10 Approve (`OnApprove`)

Rights: `Post`. One PO at a time.

- `PENDING` and current user = `CheckBy`:
  - Amount `>` `AdPara.PurOrderApprAmt` (0 treated as unlimited) → `CHECKED`, email 2nd approver.
  - Else → `NEW`.
- `CHECKED` and current user = `ApprovedBy` → `NEW`.
- Else fail.

Approvers on the header come from `HRDept` (`CheckBy`, `ApprovedBy`, `AuthorisedBy`) when department changes on the entry screen.

### 3.11 Sent email to approver

All selected must be `PENDING`, same `ApprovedBy`, email found in `POAuthorised`.

### 3.12 Print

Rights: `Print`. `POHelper.PrintChecking` increments `PrintCounter`. If user `AdUser.POReprint = true` **and** already printed → **block reprint** (name is the opposite of the rule). Pending-print block is commented out.

Report “PURCHASE ORDER” attachment path opens `SendPOAttach.aspx` (Crystal/DevExpress PDF, SMTP). After send, unused field `POOrder.POType = '1'` is used as sent flag.

### 3.13 Copy to SO

Company `GH` / `GM` only. `POHelper.StartCreateSO`.

---

## 4. PO Entry (`PO.aspx` / `PO.aspx.cs`)

Query `Type`: `NEW` | `Edit` | `View` | `COPY` | `Revise`. Optional `RawMat` from production outstanding raw-mat inquiry.

Session working set (per `hdSessionID`):

- `PODETAIL` — `PODetail` rows
- `PRDELKEY` — PR lines to unlink on save
- `PO_ATTACHEDFILE` — attachments
- `POITEMDTL` — grid bind copy

### 4.1 Create (`Type=NEW`)

Header defaults: `PONo=AUTO`, `PORelNo=1`, `POStat=OPEN` (UI only, not yet in DB), `Buyer=UserID`, `Currency=MYR`, date=today.

User adds lines (manual / PR / outstanding raw mat). Save requires at least one line (`CheckRecordAlreadyAdd`).

Number: prefix `PO` + vendor/user prefix → `CCommon.GenerateAutoNumberWithDateEx` + increment `AdSmNumDate`. If generated number already exists, fail.

After save (if not already `RECEIVED`/`CLOSED` from qty check): `PENDING` or `NEW`.

Concurrency wrapper: `TrxPostingHelper` (`SAVEPO`). Then one SQL transaction writes:

1. `POOrder`
2. `PODetail`
3. `AdSmNum` / `AdSmNumDate`
4. `IvBalance`
5. `POCJ` / `POCJDetail`
6. `POPR` / `POPRDtl`
7. `POAttachFile`

### 4.2 Line add rules (must keep)

Client (`OnAddItemClick`): item code not blank (`PR000006`), purchase qty ≠ 0, pack size ≠ 0, std qty ≠ 0.

Server (`AddRow`):

- One tax mode only: all inclusive **or** all exclusive (`PR000002`).
- One material type only: `OneTime` true = indirect, false = stock. Cannot mix. First line locks the checkbox.
- Direct item: purchase UOM must exist on vendor item (`vLook_PODirectItem_POCtrl` or `vLook_PODirectItem_NoPOCtrrl`). `IType=NSI` can skip UOM check.
- `MinQty` from `POItemByVendor` (same vendor + item + PurUOM).
- `MOQ` from `POPurItem`.
- If vendor item `CJItem=true`, `CJNo` required; save cannot exceed `CJDBalQty`.
- Price source: `AdPara.SupplierPrice` `0` = item master; `2`/`3` = vendor price book (`GetItemByDirectMaterialPrice`).
- Amount: `POPurQty × unit price`, then discount (`JOIN` two-level % or amount via `SalesDicountHelper.CalculateDiscount`), then SST/GST from `SaTaxGroup` using `AdPara.PurchaseTaxDec`.
- Std qty: `PurQty × PackSize` (pack 0 treated as 1). Catch weight: `StdQty × DefCatchWt` if `AdPara.UseWeight`.
- `SupplierPrice=2` and default vendor item has `LeadTime` → ETA = PO date + lead days.
- Default warehouse: user default (`AdUserDefault` page `PO`) else item `DefWarehouse`.
- Whole-document tax rounding: `TableRounding()`.

### 4.3 Line edit after GR (`ShowSelections`)

If `RecvQty > 0`:

- Alert: “The Item is received.”
- Purchase qty and item code locked.
- Only **adjustment qty** can raise order/balance:
  - `POPurQty = PurQty + AdjustmentQty`
  - `BalanceQty = BalanceQty + AdjustmentQty`
  - `RecvQty` kept.

If `PRNo` not empty: item code and UOM locked.

If header status is `RECEIVED`: PO date disabled.

### 4.4 Line delete (`DeleteItem`)

Blocked if `RecvQty > 0`. PR key stored in `prdellist` so save can clear `POPRDtl.PONo`. On brand-new `AUTO` PO, line numbers are resequenced.

### 4.5 Save status (`CheckPOStatus` + header write)

For each current line: stamp `PONo`; if `POPurQty > BalanceQty` → header `RECEIVED`; sum `BalanceQty == 0` → `CLOSED`.

Then if status is not `RECEIVED`/`CLOSED`:

- `POApproval=TRUE` → `PENDING`
- else → `NEW`

`CheckPOStat()` (edit only): if DB header is already `CLOSED` or missing, save is rejected (“updated by other”).

`UpdateQtyOnAllocated`: delta of `POQty` / `WtQty` applied to `IvBalance.Qty_Allocated` at item default warehouse.

`UpdateQtyOnCJ`: CJ items adjust `POCJ.CJDBalQty`; cannot go below available.

### 4.6 Cancel on the entry form (not listing Cancel)

If `Type=Edit` and DB status is `OPEN`, restore from **DB qty**: `CLOSED` / `RECEIVED` / `PENDING` or `NEW`. Then clear session and return to listing.

---

## 5. PR → PO link

### 5.1 What the picker shows

`PRListing.ascx.cs`:

- Header: `vLook_POPR` where `PRStat == APPROVED` and `PONo == "NONE"`. Optional vendor filter = current PO vendor.
- Lines: `POPRDtl` where `PONo is null or ''`, same PR, optional vendor.

PR itself is approved on `POPRView` / `PRApproveView`: `NEW` → `APPROVED` (or `NOTAPPROVE` / `CANCELLED`).

### 5.2 Pull (`AddRowPOPRDtl`)

1. User picks PR, then selected lines (`ADDPRITEMLINES`).
2. Only unused lines (`PONO is null`). If none: “PRNo Not Found!”
3. Cannot mix direct / indirect with existing PO lines.
4. Same inclusive/exclusive tax rule as manual add.
5. Copies: item, qty, UOM, price, tax, warehouse, ETA, purpose → remarks, requester, payment term, currency.
6. PO line stores `PRNo` + `PRLineNo`. `BalanceQty = PurchaseQty`. `RecvQty = 0`. Discount = 0.
7. Header project from `POPR.ProjID`; vendor from first PR line `VendorCd`; remark from `POPR.Remarks`; vendor `POPrefix` if set.

`ADDPRITEM` (all lines) exists in UI but the ALL button is `ClientVisible=false`. Production path is selected lines.

### 5.3 On PO save (`UpdatePR`)

- `POPR.PONo` and matching `POPRDtl.PONo` = this PO number (stamp, not qty consume).
- Deleted PO lines in `prdellist`: `POPRDtl.PONo` set back to null.

### 5.4 Limitations in this code (do not silently copy)

- Header `POPR.PONo` is set when **any** line is used → whole PR drops out of the picker (`PONo == "NONE"`). Leftover lines are stranded unless you query `POPRDtl` directly.
- No PR remaining-qty field. Convert is **all-or-nothing per line**. Partial qty on one PR line is not supported.
- Same PR line cannot sit on two POs (`CheckPR`).
- After PO delete, `POPR.PONo` is not cleared (see 3.6).

---

## 6. PO → GR (this project calls it GRN)

GR is **`IvTrxBatch` + `IvBatchDtl`**, `TrxType` `GR` (stock) or `NG` (non-stock). Link on the GR line: `PO_No`, `PO_Rel_No`, `PO_Line_No`.

### 6.1 Which POs GR can pick (`GRNPOListing`)

- Normal GR: not `CLOSED`, `CANCELLED`, `CHECKED`, `PENDING`; `OneTime=false` (stock PO).
- Indirect GR: same, `OneTime=true`.
- Return-to-vendor (`IsReceivedPOOnly`, e.g. `RtnToVendor.aspx`): also excludes `NEW` — only already-received / open-for-return.

`OPEN` is **not** excluded, so a PO being edited can still be received. That is a race.

### 6.2 Pull PO into GR (`AddPOITems`)

- `FrPurQty` = current `BalanceQty`
- `ToPurQty` = user `ToRecvQty` (skip if `<= 0`)
- `ToStdQty` = `ToRecvQty × PackSz`
- Warehouse from PO item session `ToWh` (not always `PODetail.ToWarehouse`)
- Over-receive allowed only within vendor **tolerance %** of `POPurQty` (`isValidRecQty`, error `IN000019`)
- Std qty on PO line must be `> 0`

### 6.3 On GR post (`GRNHelper.UpdateReceivedQtyInPOrderFG` + `IsPOCanbeClosed`)

- Must use **latest** `PORelNo` (`GetPOMaxRevNo`). Old revision cannot post.
- `RecvQty += ToPurQty`, `BalanceQty = POPurQty - RecvQty`
- If `BalanceQty < -tolerance` → fail: “Receive Qty cannot more then Po Order Quantity!”
- If `BalanceQty <= 0` → set `RecvDate` = GR transaction date
- Then header: every non-`SERVICE` line `BalanceQty <= 0` → `CLOSED`; else `RECEIVED`
- SERVICE lines are ignored for close

### 6.4 On GR unpost (`RollbackReceivedQtyInPOrderFG` + `IsPOCanbeRollback`)

Qty rolled back; header forced to `RECEIVED` (even if `RecvQty` is now 0). A separate SQL (`IsPOCanBeNew`) may set `NEW` if `SUM(RecvQty)=0` (note: that SQL does not filter `PORelNo` on the sum). Rollback math is messy — do not copy it as-is.

---

## 7. Approval, print, email (related, not CRUD)

- Config: `appSettings["POApproval"]`.
- Approvers from `HRDept` when department changes.
- Amount gate: `AdPara.PurOrderApprAmt` (0 = unlimited → skip `CHECKED`).
- Email-to-approver: all selected `PENDING`, same `ApprovedBy`, email in `POAuthorised`.
- Print counter / reprint: see 3.12.
- `SendPOAttach`: Crystal `rptPO` / `rptPOAtt`, or DevExpress dynamic report; SMTP; WhatsApp URL was disabled (task 250718053008); sent flag = `POType='1'`.

---

## 8. End-to-end flow

```
PR (NEW → APPROVED)
    → PO picker (APPROVED + unused / PONo NONE)
    → PO lines (PRNo / PRLineNo)
    → Save → NEW or PENDING → approve → NEW
    → Print / email vendor
    → GRN pick PO (not CLOSED / CANCELLED / PENDING / CHECKED)
    → Post GR → RecvQty / BalanceQty
         all received → CLOSED
         partial     → RECEIVED (still editable)
    → Force Close leftover → CLOSED (qty remains, GR blocked)
    → Reopen Close if BalanceQty > 0 → NEW or RECEIVED
    → later Purchase Invoice / costing (separate module, reads PO)
```

Optional create paths (same save):

- Manual item add (vendor item / item master)
- Outstanding raw material (`Session["DATAFORGENERATEPO"]` from `RawMatCalInq`)
- Copy existing PO (PR link stripped)
- Revise (old rev cancelled)

---

## 9. Side settings that change behaviour

| Setting | Effect |
|---|---|
| `appSettings["POApproval"]` | Save → `PENDING`; edit/delete gates change; Approve button shown |
| `AdPara.SupplierPrice` | `0` item master; `2`/`3` vendor price book; `2` also drives ETA from lead time |
| `AdPara.POPriceDecimal` | Unit price decimals (default 6) |
| `AdPara.POControlIcode` | Restrict item lookup to controlled vendor items |
| `AdPara.UseWeight` | Catch-weight fields |
| `AdPara.PurchaseTaxDec` | Tax rounding decimals |
| `AdPara.PurOrderApprAmt` | 1st vs 2nd approver amount gate |
| `AdUser.CurrView` | Hide prices / footer totals if false |
| `AdUserDefault` page `PO` | Default prefix and warehouse |
| `AdUser.POReprint` | If true **and** printed once → block reprint |
| `AdUserGroupDefault` | See all POs vs own only |
| Vendor `POPrefix` | Numbering prefix when vendor selected |
| `POItemByVendor.CJItem` / `Tolerance` / `MinQty` / `LeadTime` | CJ, GR over-receive, min qty, ETA |

---

## 10. Malaysian ERP practice vs this code — Blazor recommendation

Copy the **business meaning**, not the WebForms session / `DataTable` / `PerformCallback` style.

Standard MY procurement: **PR → PO → GRN → PI** (3-way match), SST tax group, approval before vendor send, force-close outstanding, revision, numbering by company/branch/period.

### Keep

1. Explicit status enum + one transition service. No writing `POStat` from the page.
2. Line remaining qty: `Balance = Ordered − Received − Returned` (this code almost ignores `ReturnQty` on GR post; include it).
3. PR line consume with **remaining qty**, not a single `PONo` stamp. One PR line → many POs.
4. Force close = status lock **plus** optional write-off of remaining (user choice). Today it only locks status.
5. Cancel = no stock movement. If any GR exists, cancel must be blocked (this listing does **not** check GR — fix that).
6. SST: one tax mode per document; tax from `SaTaxGroup`; store taxable, tax, inclusive flag. Ready for e-Invoice later (TIN, classification) even if you do not send from PO.
7. Numbering by company/branch/period (already here).
8. Approval: `PENDING → [CHECKED] → NEW` before vendor send / GR. GR must never pick `PENDING`/`CHECKED` (already true).
9. Revision: old rev cancelled, GR only on latest rev (already true).
10. Server-side transaction: header + lines + PR consume + numbering.

Do **not** put `IvBalance` allocation on PO save unless purchasing still uses “qty allocated” the same way — in MY practice allocation usually belongs to SO, not PO.

### Do not copy as-is

| Current | Why | Blazor |
|---|---|---|
| `OPEN` as DB status | Stuck documents; GR can still post | Optimistic concurrency (`RowVersion`) + short-lived “editing” if needed |
| Session `DataTable` as the document | Lost on timeout | Domain entity in memory / EF, save once |
| Stamp `POPR.PONo` | Kills leftover PR lines | `PRLine.RemainingQty`, `POLine.SourcePRLineId` |
| Delete helper vs listing (NEW vs PENDING) | Split brain | One rule |
| Force-close message always “Closed” / “Reverse Close” | Wrong UX | Two actions: Close / Reopen |
| `CheckPOStat` only looks at `CLOSED` | Weak concurrency | Version token |
| GR rollback header always `RECEIVED` | Wrong after full unpost | Recalc from remaining |
| POHelper delete may leave `PODetail` | Data integrity | Cascade delete in one unit of work |
| `POReprint=true` blocks reprint | Confusing | `AllowReprint` means allow |
| SQL string concat | Injection / quoting bugs | Parameterized / EF |

### Suggested Blazor services (one write path)

```
PoAppService
  Create / Update / Delete / Copy / Revise
  SubmitForApproval / Approve / Reject
  Cancel / ForceClose / Reopen
  RefreshStuckOpen   // only if you keep OPEN

PoLineCalculator   // qty, tax, discount, pack
PrConversionService
GrnPostingService  // already exists conceptually in GRNHelper — share it
PoStatusPolicy     // allowed transitions, no if/else on pages
```

If Blazor **shares this SQL DB**, keep the same status strings: `PENDING`, `CHECKED`, `NEW`, `RECEIVED`, `CLOSED`, `CANCELLED`. Do not invent `POSTED`.

---

## 11. Open questions (do not assume)

1. **Blazor shares this SQL DB** or is a new schema? If shared, status strings and `PONo+PORelNo` must stay.
2. **Is `POApproval` on** for the target company? It changes edit/delete/save status.
3. **Must you keep:** CJ, vendor allocation %, copy-to-SO (GH/GM only), raw-mat generate, catch weight, `OneTime` indirect PO, revise, print-counter?
4. **Force close:** keep “status only, qty remains” or also write off remaining (MY common: close + zero outstanding, with reason)?
5. **Partial PR qty** — do buyers split one PR line across several POs? Current code cannot.
6. **After PO delete**, should leftover PR lines become pickable again? Today header `POPR.PONo` likely stays set.
7. **Which flavour is the source of truth?** This document is standard `ERP/Purchasing/POForms`, not IEP / NWest / Phletora / SMC.
8. **Cancel after GR** — block (recommended) or keep current status-only cancel?
9. **Is there a DB cascade** from `POOrder` to `PODetail` on delete?

---

## 12. Key methods (quick index)

| Method | File | What it does |
|---|---|---|
| `OnNewItem` / `OnEditItem` / `OnDeleteItem` | `POView.aspx.cs` | List CRUD gates |
| `OnCancel` / `OnClosed` / `OnRefreshItem` | `POView.aspx.cs` | Cancel, force close toggle, recover `OPEN` |
| `OnApprove` / `OnSentApproval` | `POView.aspx.cs` | Two-step approval |
| `Save` / `SavePO` | `PO.aspx.cs` | Number, status, PR stamp, CJ, allocation, transaction |
| `CheckPOStatus` / `CheckPOStat` | `PO.aspx.cs` | Qty → RECEIVED/CLOSED; concurrency vs CLOSED |
| `AddRow` / `AddRowPOPRDtl` / `DeleteItem` | `PO.aspx.cs` | Line add / PR pull / line delete |
| `UpdatePR` | `PO.aspx.cs` | Stamp / unstamp `POPR` + `POPRDtl.PONo` |
| `Cancel` (entry) | `PO.aspx.cs` | Restore status from qty when leaving edit |
| `UpdateItemStatus` / `Delete` | `POHelper.cs` | Set `OPEN`; delete NEW + unlink PR + CJ |
| `IsPOhaveBalanceQty` / `PONewStatus` | `POOrderBL.cs` | Close toggle helpers |
| `UpdateReceivedQtyInPOrderFG` / `IsPOCanbeClosed` | `GRNHelper.cs` | GR post qty + header status |
| `RollbackReceivedQtyInPOrderFG` | `GRNHelper.cs` | GR unpost qty |
