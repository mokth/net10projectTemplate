# productionv2 --- PO vs GRN Inquiry & Supplier Delivery KPI

## FINAL AI Coding Agent Implementation Plan --- Deep Repo-Verified

**Repository:** `mokth/net10projectTemplate`\
**Branch:** `productionv2`\
**Review status:** **FINAL --- APPROVED FOR IMPLEMENTATION**\
**Review score:** **10/10**\
**Scope:** Malaysian SME procurement --- daily PO/GR visibility,
actionable outstanding/overdue inquiry, supplier delivery KPI\
**Change type:** **Additive inquiry/analytics enhancement; preserve
existing PO, GR, inventory, costing and Purchase Invoice transaction
behavior.**

------------------------------------------------------------------------

# 1. Executive implementation decision

This plan is approved because the `productionv2` branch already contains
the correct architectural foundation. The implementation must **extend
the existing procurement inquiry architecture**, not create another
reporting subsystem.

Confirmed branch components include:

``` text
ErpWeb.Core/Purchase/IPoPurchaseInquiryService.cs
ErpWeb.Core/Purchase/PoPurchaseInquiryService.cs
ErpWeb.Core/Purchase/PoPurchaseInquiryService.Phase2.cs
ErpWeb.Core/Purchase/PoDeliveryPerformanceCalc.cs
ErpWeb.Core/Purchase/PoInquiryResults.cs

ErpWeb.UI/Purchase/Inquiry/PoOrderOutstandingInquiry.razor(.cs)
ErpWeb.UI/Purchase/Inquiry/PoMatchingInquiry.razor(.cs)
ErpWeb.UI/Purchase/Inquiry/PoDeliveryPerformanceInquiry.razor(.cs)

ErpWeb.Core/Inventory/IIvGoodsReceiptService.cs
ErpWeb.Core/Inventory/IvGoodsReceiptService.cs

ErpWeb.Tests/Procurement/Inquiry/PoPurchaseInquiryServiceTests.cs
ErpWeb.Tests/Procurement/Inquiry/PoPurchaseInquiryPhase2Tests.cs
ErpWeb.Tests/Procurement/Inquiry/PoDeliveryPerformanceCalcTests.cs
ErpWeb.Tests/Procurement/Inquiry/PoPurchaseInquiryWorkbenchTests.cs
```

The menu already exposes:

``` text
/purchase/inquiry/po-outstanding
/purchase/inquiry/matching
/purchase/inquiry/delivery-performance
```

Therefore:

> **Do not add a fourth overlapping "PO vs GRN Dashboard" page.**

Enhance these existing screens/services so each has a clear
responsibility.

------------------------------------------------------------------------

# 2. Existing productionv2 business truth --- DO NOT replace it

The repository already persists the operational PO quantity rollups on
`PoOrderDetail`:

``` text
PoPurQty
RecvQty
ReturnQty
BalanceQty
InvoicedQty
EtaDate
RecvDate
NetAmount
PoUnitPrice
PurchaseUom
StdUom
PackSz
ToWarehouse
```

Existing inquiry code intentionally treats `BalanceQty` as the persisted
outstanding-delivery truth.

`PoOrderOutstandingRow` explicitly documents:

> `BalanceQty` is persisted outstanding delivery qty and is shown as-is,
> never recomputed.

Therefore this project must NOT replace:

``` text
BalanceQty
```

with an independently reconstructed:

``` text
PoPurQty - SUM(GR)
```

for operational outstanding quantities.

That would create two sources of truth.

### Correct split of responsibilities

Use persisted PO rollups for current operational state:

``` text
Ordered         = PoPurQty
Gross Received  = RecvQty
Returned        = ReturnQty
Net Received    = PoOrderCalc.ComputeNetReceived(RecvQty, ReturnQty)
Outstanding     = BalanceQty
Invoiced        = InvoicedQty
Invoiceable     = PoOrderCalc.ComputeInvoiceable(...)
```

Use posted GR movements for historical delivery timing and traceability:

``` text
IvTrxBatch
IvTrxBatchDetail
PoNo + PoRelNo + PoLineNo
```

This split already exists in `PoPurchaseInquiryService.Phase2.cs` and
must remain the canonical design.

------------------------------------------------------------------------

# 3. Existing GR truth --- DO NOT duplicate it

`IvGoodsReceiptService` already:

-   uses `IvTrxTypes.GoodsReceive` and
    `IvTrxTypes.NonStockGoodsReceive`;
-   links GR detail to `PoNo`, `PoRelNo`, `PoLineNo`;
-   uses PO `BalanceQty` when offering receivable PO lines;
-   subtracts unposted draft GR quantity from currently available
    receipt quantity;
-   updates PO receipt rollups through the existing transaction/posting
    flow;
-   uses tenant company/branch scoping.

The delivery-performance inquiry already reads historical GR movements
only when:

``` text
BatchStatus == IvBatchStatuses.Posted
```

and clips them by `AsOfDate`.

Do not add another receipt-history table, KPI snapshot table, or copied
PO/GR balance table.

------------------------------------------------------------------------

# 4. Existing latest-revision rule --- preserve exactly

Both PO Outstanding and Delivery Performance already use:

``` text
MAX(PoRelNo) per PoNo
```

within company + branch.

The enhancement must preserve latest-revision behavior.

Do not accidentally aggregate obsolete PO revisions into:

-   outstanding quantity,
-   due counts,
-   supplier KPI,
-   outstanding value,
-   receipt drill-down.

Historical GR trace must still join using the exact:

``` text
PoNo + PoRelNo + PoLineNo
```

key.

------------------------------------------------------------------------

# 5. Existing security/tenant pattern --- preserve exactly

`PoPurchaseInquiryService` already gates inquiry access using:

``` text
IInventoryTenantContext
IAccessRightService
MenuCodes.*
PermissionCodes.Access
```

and applies authenticated:

``` text
CompanyCode
BranchCode
```

server-side.

All enhancements must continue through the same `GateAsync(...)` path.

`PoInquiryQuery.BranchCode` must NOT be allowed to override
authenticated branch isolation unless the existing service explicitly
supports authorized cross-branch semantics.

No client-side-only tenant filtering.

------------------------------------------------------------------------

# 6. Final screen responsibility

## 6.1 PO Outstanding --- daily purchasing workbench

Route:

``` text
/purchase/inquiry/po-outstanding
```

Purpose:

> "What is still due from suppliers and what needs action?"

Enhance this page for:

-   open PO lines,
-   overdue lines,
-   partial deliveries,
-   due soon,
-   no ETA,
-   outstanding value,
-   overdue value,
-   incoming delivery schedule,
-   ageing.

Do not turn this page into invoice matching.

------------------------------------------------------------------------

## 6.2 PO / GR / Invoice Matching --- document reconciliation

Route:

``` text
/purchase/inquiry/matching
```

Purpose:

> "Do PO, received quantity and Purchase Invoice agree?"

Keep the existing matching responsibility.

Enhance only where PO/GR trace visibility is needed.

Do NOT move OTIF/supplier delivery KPI here.

------------------------------------------------------------------------

## 6.3 Supplier Delivery Performance --- supplier KPI

Route:

``` text
/purchase/inquiry/delivery-performance
```

Purpose:

> "Did suppliers meet committed delivery dates and quantities?"

Enhance the existing delivery-performance engine rather than creating a
new KPI engine.

------------------------------------------------------------------------

# 7. PO Outstanding --- repo-correct calculation contract

Existing service already computes:

``` text
Open          = BalanceQty > 0
Overdue       = BalanceQty > 0 && EtaDate < AsOf
Partial       = BalanceQty > 0 && RecvQty > 0
Unbilled      = RecvQty - ReturnQty - InvoicedQty > 0
```

Preserve these semantics unless an existing helper provides the
equivalent.

## 7.1 Outstanding value

The repository already has the correct rule:

``` text
OutstandingValue =
    NetAmount * BalanceQty / PoPurQty
```

when `PoPurQty > 0`.

Do NOT change it to:

``` text
BalanceQty * PoUnitPrice
```

because `NetAmount` reflects the existing commercial discount
calculation.

Use `PoOrderCalc.RoundMoney(...)`.

### Tax rule

The current outstanding inquiry uses commercial net (`NetAmount`) rather
than tax-inclusive PO total.

Keep this existing meaning and label it clearly as:

``` text
Outstanding Net Value
```

unless product requirements explicitly request tax-inclusive commitment
value later.

Do not silently change financial semantics in this enhancement.

------------------------------------------------------------------------

# 8. Add missing daily-action metrics to PO Outstanding

Extend `PoOrderOutstandingSummary` carefully.

Recommended fields:

``` csharp
public int OpenLineCount { get; set; }
public int OverdueLineCount { get; set; }
public int PartialLineCount { get; set; }
public int UnbilledLineCount { get; set; }       // preserve existing
public int DueSoonLineCount { get; set; }        // NEW
public int NoEtaLineCount { get; set; }          // NEW
public decimal OutstandingValue { get; set; }
public decimal OverdueValue { get; set; }        // NEW
public decimal DueSoonValue { get; set; }        // optional but useful
```

Do not remove existing summary fields.

## 8.1 Due Soon definition

Use a simple SME default:

``` text
BalanceQty > 0
AND EtaDate >= AsOfDate
AND EtaDate < AsOfDate + 7 days
```

Prefer an inclusive business label:

``` text
Due Next 7 Days
```

Avoid ambiguous "this week" calendar semantics.

## 8.2 No ETA

``` text
BalanceQty > 0
AND EtaDate IS NULL
```

This is operationally important because missing ETA prevents meaningful
delivery KPI.

------------------------------------------------------------------------

# 9. Add overdue value using the SAME commercial rule

For overdue open lines:

``` text
OverdueValue =
SUM(NetAmount * BalanceQty / PoPurQty)
```

subject to:

``` text
PoPurQty > 0
BalanceQty > 0
EtaDate < AsOfDate
```

Round using existing `PoOrderCalc.RoundMoney`.

No separate value engine.

------------------------------------------------------------------------

# 10. PO Outstanding ageing

Add ageing to the existing workbench using ETA.

Buckets:

``` text
NOT_DUE
1_7
8_14
15_30
31_60
OVER_60
NO_ETA
```

Calculation:

``` text
if BalanceQty <= 0:
    not in outstanding ageing

if EtaDate is null:
    NO_ETA

if EtaDate >= AsOfDate:
    NOT_DUE

days = (AsOfDate - EtaDate).Days
```

Then map to the overdue bucket.

Do not age from PO Date.

## UI

Prefer clickable chips/cards or a compact ageing summary.

Clicking a bucket must set the existing workbench/filter state and show
the actual lines.

Do not create a decorative pie chart as the primary interaction.

------------------------------------------------------------------------

# 11. Incoming delivery view

Do not create a new transaction or stock projection.

Use the existing open PO-line data.

Add workbench presets or quick filters:

``` text
DUE_NEXT_7_DAYS
DUE_NEXT_14_DAYS
NO_ETA
```

The grid already contains the useful fields:

``` text
PoNo
PoDate
VendCode / VendName
Buyer
ICode / IDesc
BalanceQty
EtaDate
ToWarehouse
OutstandingValue
```

If needed, expose `PurchaseUom` in `PoOrderOutstandingRow`; do not infer
it from another master when the PO line already owns the transaction
UOM.

------------------------------------------------------------------------

# 12. PO vs GR drill-down --- reuse existing receipt engine

The existing delivery-performance service already has:

``` text
GetDeliveryPerformanceReceiptsAsync(...)
```

and `PoDeliveryPerformanceCalc.WalkReceipts(...)`.

Do not write another GR-history algorithm.

Refactor only if necessary so a shared read-only receipt-history method
can be used from PO Outstanding and/or Matching.

For a PO line, drill-down should show:

``` text
GR Batch No
GR Date
Received Qty
Cumulative Qty
Remaining Qty
Days vs ETA
On-Time / Late
```

Add navigation to the existing Goods Receipt entry route using the
existing batch number/navigation convention.

### Important

PO Outstanding must still work for lines with `EtaDate == null`.

If ETA is absent:

-   receipt trace may still show GR batch/date/qty;
-   ETA-relative fields are blank/not applicable;
-   do not suppress GR trace simply because ETA is missing.

If sharing `GetDeliveryPerformanceReceiptsAsync` would currently return
empty when ETA is null, refactor the underlying receipt loader into a
neutral shared method rather than weakening PO Outstanding
functionality.

------------------------------------------------------------------------

# 13. Delivery KPI --- critical correction to current semantics

This is the most important part of this final plan.

The existing delivery-performance implementation is already strong for:

-   posted GR movement walking,
-   first/last receipt,
-   fully received date,
-   on-time received qty,
-   late received qty,
-   partial/open/late/early/on-time status,
-   return-after-full reopening,
-   AsOf clipping,
-   multiple GRs,
-   latest PO revision.

**Keep all of that.**

However, the existing supplier grouping calculates:

``` text
OnTimePct =
OnTimeCompletedLines / CompletedLines
```

This is useful as a completed-delivery punctuality metric, but it is
**not the same as period OTIF**.

Also, `LoadDeliveryPerformanceSpineAsync` currently applies the generic
inquiry date filter to:

``` text
PO Date
```

before KPI grouping.

For a supplier delivery KPI, the evaluation population must be driven
by:

``` text
EtaDate / committed delivery date
```

when calculating due-period KPI.

Therefore do NOT simply rename the current `OnTimePct` to OTIF.

------------------------------------------------------------------------

# 14. Preserve existing metric and add explicit KPI metrics

Keep the existing completed-line metric for backward compatibility, but
rename its UI meaning clearly if necessary:

``` text
Completed On-Time %
```

Definition:

``` text
(Early + OnTime completed lines)
/
Completed lines
```

Then add true due-period metrics.

------------------------------------------------------------------------

# 15. True Due-Period KPI population

Introduce a clear KPI evaluation mode.

For KPI summary:

``` text
DueDateFrom
DueDateTo
```

may reuse `DateFrom/DateTo` in the UI only if the service method clearly
applies them to `EtaDate`.

Do NOT silently change all existing delivery line-grid date filters from
PO Date to ETA if users already depend on current behavior.

Preferred design:

### Detail inquiry date mode

Add an explicit filter selector:

``` text
Date Basis:
- ETA / Due Date        [default for KPI]
- PO Date
```

or introduce a dedicated query property, e.g.:

``` csharp
public string? DeliveryDateBasis { get; set; }
```

with constants:

``` text
ETA
PO_DATE
```

For supplier KPI summary, default/use `ETA`.

For existing general line browsing, preserve compatibility where
required.

------------------------------------------------------------------------

# 16. Due-period denominator

A line qualifies for due-period supplier KPI when:

``` text
latest PO revision
not cancelled
PoPurQty > 0
EtaDate != null
EtaDate within selected due-date range
```

Do not require the line to be completed.

That is essential.

Otherwise chronically late/open supplier commitments disappear from the
KPI denominator.

------------------------------------------------------------------------

# 17. In-Full KPI

At `AsOfDate`, a due line is In-Full when the operational PO line
indicates no remaining delivery obligation:

``` text
BalanceQty <= 0
```

but use historical receipt timing to determine whether it was in-full by
ETA for OTIF.

For current-period KPI:

``` text
InFullPct =
DueLinesWithBalanceSatisfiedByAsOf
/
DueLines
* 100
```

If the business wants "in full by due date," that is represented by OTIF
below, not this current fulfilment metric.

Use explicit labels/help text.

------------------------------------------------------------------------

# 18. On-Time Completion KPI

For lines that eventually completed:

``` text
OnTimeCompleted =
FullyReceivedDate <= EtaDate
```

The existing `PoDeliveryPerformanceCalc` already determines
`FullyReceivedDate`.

Preserve it.

Recommended KPI:

``` text
OnTimeCompletionPct =
Due lines fully completed by ETA
/
Due lines
* 100
```

This denominator intentionally includes open/late commitments due in the
period.

That makes it management-actionable.

Do not use only `CompletedLines` as denominator for this new KPI.

------------------------------------------------------------------------

# 19. OTIF KPI

Define OTIF at PO-line commitment level:

``` text
OTIF line =
FullyReceivedDate != null
AND FullyReceivedDate <= EtaDate
AND quantity commitment was satisfied
```

With the existing GR walk, the moment cumulative posted GR reaches
`PoPurQty` is `FullyReceivedDate`.

Therefore:

``` text
OTIFPct =
Count(due lines with FullyReceivedDate <= EtaDate)
/
Count(all qualifying due lines)
* 100
```

### Partial-before-ETA + final-after-ETA

Must be OTIF failure.

Existing `WalkReceipts` already supports this correctly.

### Open overdue line

Must be OTIF failure once it is part of a completed evaluation period /
AsOf has passed ETA.

It must not disappear from the denominator.

------------------------------------------------------------------------

# 20. Returns and historical delivery performance

The repository already has an important behavior:

``` text
RecvQty = gross received
ReturnQty = returned
NetReceivedQty = RecvQty - ReturnQty
BalanceQty may reopen
```

while GR movement history retains the original fully received date.

Existing tests explicitly cover:

> return after full receipt reopens the line while preserving historical
> `FullyReceivedDate`.

Preserve this behavior.

Interpretation:

-   historical supplier delivery event can remain "delivered on time";
-   current line can be reopened/partial because goods were returned.

Do not automatically rewrite historical delivery punctuality because of
a later vendor return.

If future requirements need "accepted quality delivery," that is a
separate supplier-quality KPI and is out of scope.

------------------------------------------------------------------------

# 21. Average days late

Use two distinct concepts and label them correctly.

### Completed late average

Existing:

``` text
CompletionDaysLate =
max(FullyReceivedDate - EtaDate, 0)
```

Supplier KPI:

``` text
AvgCompletedDaysLate =
average CompletionDaysLate where > 0
```

### Open overdue exposure

For currently open overdue lines:

``` text
OpenDaysLate =
AsOfDate - EtaDate
```

Do not mix open days late into completed average days late.

Show open overdue count/value separately.

------------------------------------------------------------------------

# 22. Quantity aggregation warning

The current supplier summary contains:

``` text
OrderedQty = SUM(PoPurQty)
ReceivedQty = SUM(NetReceivedQty)
```

across a supplier.

This can mix:

``` text
PCS + KG + BOX + LITRE
```

and is not a meaningful cross-item supplier KPI.

### Required correction

Do not use cross-item raw quantity sums to calculate supplier fulfilment
percentage.

Options:

1.  retain raw quantity totals only if grouped by compatible UOM; or
2.  remove/hide them from supplier-level KPI cards; or
3.  show them only in line-level detail.

For the supplier summary, prefer dimensionless line-based metrics:

``` text
Due Lines
Completed Lines
On-Time Lines
Late Lines
Partial/Open Lines
On-Time Completion %
In-Full %
OTIF %
Avg Completed Days Late
Open Overdue Lines
```

This avoids invalid mixed-UOM arithmetic.

------------------------------------------------------------------------

# 23. Supplier KPI summary DTO

Extend the existing `PoDeliveryPerformanceRow`; do not replace it
unnecessarily.

Recommended additive fields:

``` csharp
public int DueLines { get; set; }
public int InFullLines { get; set; }
public int OtifLines { get; set; }
public int OpenOverdueLines { get; set; }

public decimal? CompletedOnTimePct { get; set; } // existing meaning
public decimal? OnTimeCompletionPct { get; set; }
public decimal? InFullPct { get; set; }
public decimal? OtifPct { get; set; }

public decimal? AvgCompletedDaysLate { get; set; }
public int? MaxCompletedDaysLate { get; set; }
```

Keep old properties only where required for compatibility; map/deprecate
cleanly rather than breaking existing UI/tests unexpectedly.

------------------------------------------------------------------------

# 24. Supplier KPI financial exposure

The delivery spine currently does not carry `NetAmount`, so delivery
summary cannot yet calculate outstanding/overdue value.

If adding supplier financial exposure, extend the spine with:

``` text
NetAmount
```

and use the same repository-approved formula:

``` text
lineOutstandingNet =
PoPurQty > 0
? RoundMoney(NetAmount * BalanceQty / PoPurQty)
: 0
```

Then aggregate:

``` text
OpenOutstandingNetValue
OverdueOutstandingNetValue
```

### Currency safety

Before summing supplier values, inspect `PoOrder.CurCode` and existing
base/local amount semantics.

Do NOT sum mixed document currencies as one number.

If the PO model does not persist a trustworthy base-currency line amount
suitable for this inquiry, choose one of:

-   group value by currency;
-   filter to a currency;
-   omit supplier aggregate value in V1.

**Never output a mathematically meaningless RM + USD + SGD total.**

PO Outstanding currently exposes one outstanding value without a
currency field; the agent must inspect the PO header currency and
current UI before expanding cross-supplier/card aggregation.

------------------------------------------------------------------------

# 25. Delivery-performance receipt loader --- preserve posted GR semantics

Keep:

``` text
GoodsReceive
NonStockGoodsReceive
BatchStatus == Posted
AsOf-exclusive date boundary
CompanyCode
BranchCode
exact PoNo/PoRelNo/PoLineNo
```

The current loader intentionally uses posted GR movements for timing.

Do not include draft GRs in delivery performance.

Draft GR reservation/availability belongs to GR entry workflow, not
supplier KPI.

------------------------------------------------------------------------

# 26. AsOf semantics

Preserve the existing `AsOfDate` concept.

For historical evaluation:

-   ignore GR movements after AsOf;
-   calculate open overdue days as of AsOf;
-   do not allow future receipts to make a historical KPI appear
    successful.

Example:

``` text
ETA: 10 Sep
GR: 15 Sep
AsOf: 10 Sep
```

At 10 Sep, the line must not be treated as completed.

Existing tests already enforce this behavior.

------------------------------------------------------------------------

# 27. Period semantics --- prevent KPI corruption

For KPI period `1 Sep – 30 Sep`:

``` text
Population = commitments whose EtaDate is in Sep
```

Receipt lookup must NOT be restricted to Sep if the KPI AsOf is later.

Example:

``` text
ETA: 30 Sep
Final GR: 2 Oct
AsOf: 31 Oct
```

September OTIF result:

``` text
FAIL — late completion
```

The 2 Oct GR must still be read to establish final completion timing.

Do not clip receipt lookup to `DateTo`; clip it to `AsOfDate`.

This distinction must have automated tests.

------------------------------------------------------------------------

# 28. KPI drill-down contract

Every supplier KPI cell/card must drill to the exact line population.

Examples:

``` text
OTIF 82%
  -> show all due lines
  -> clearly mark PASS / FAIL

Late Lines = 12
  -> show only late-completed commitments

Open Overdue = 8
  -> show current overdue open lines
```

Do not implement supplier KPI as opaque cards.

Reuse `PoDeliveryPerformanceLineRow` wherever possible.

------------------------------------------------------------------------

# 29. Delivery Performance UI

Keep the current page and enhance it.

Recommended top summary:

``` text
Due Lines
OTIF %
In-Full %
On-Time Completion %
Open Overdue
Avg Completed Days Late
```

Supplier grid:

  ----------------------------------------------------------------------------
  Supplier          Due     OTIF %  In-Full %  On-Time %       Open   Avg Late
                                                            Overdue       Days
  ---------- ---------- ---------- ---------- ---------- ---------- ----------

  ----------------------------------------------------------------------------

Line drill-down:

  -----------------------------------------------------------------------------------------
  PO         Line Item   ETA      Ordered        Net   Balance Fully          Days Status
                                            Received           Received       Late 
  ------ -------- ------ ------ --------- ---------- --------- ---------- -------- --------

  -----------------------------------------------------------------------------------------

Receipt expansion:

  ---------------------------------------------------------------------------
  GR Batch GR Date          Qty   Cumulative   Remaining Days vs ETA Timing
  -------- -------- ----------- ------------ ----------- ----------- --------

  ---------------------------------------------------------------------------

Use existing DevExpress patterns.

------------------------------------------------------------------------

# 30. PO Outstanding UI

Enhance the current page, not replace it.

Top action chips/cards:

``` text
Open
Overdue
Partial
Due Next 7 Days
No ETA
Unbilled
Outstanding Net Value
Overdue Net Value
```

Grid minimum:

``` text
PO No
Revision
PO Date
Supplier
Buyer
Line
Item
Description
Ordered Qty
Received Qty
Returned Qty
Balance Qty
Purchase UOM
ETA
Days Overdue
Warehouse
Outstanding Net Value
Status
```

Keep secondary fields hidden via column chooser where appropriate.

Follow the installed repo skill:

``` text
.agents/skills/devexpress-dxgrid-column-readability/skill.md
```

for grid readability.

------------------------------------------------------------------------

# 31. Existing UI standards the agent must read before coding

Mandatory agent references:

``` text
.agents/skills/purchase-transaction-ui/skill.md
.agents/skills/erp-ui-core/skill.md
.agents/skills/devexpress-dxgrid-column-readability/skill.md
```

If any new DevExpress editor is introduced, also read:

``` text
.agents/skills/devexpress-blazor-editor-binding-safety/skill.md
```

This avoids the known `TextExpression` / validation binding class of
runtime errors.

------------------------------------------------------------------------

# 32. No schema migration by default

The required core data already exists:

``` text
PoPurQty
RecvQty
ReturnQty
BalanceQty
InvoicedQty
EtaDate
GR PO source keys
GR posting date/status
supplier/buyer/warehouse
```

Therefore:

> **No database migration is expected for the core enhancement.**

Only add schema if a verified requirement cannot be satisfied by
existing fields.

Do not add "PromisedDate" merely because generic ERP design suggests
one; productionv2 already uses line `EtaDate` as the delivery commitment
in its inquiry engine.

------------------------------------------------------------------------

# 33. Service implementation strategy

Do not build a new service such as:

``` text
PoGrnDashboardService
```

unless a genuine separation concern is proven.

Preferred:

``` text
IPoPurchaseInquiryService
PoPurchaseInquiryService
PoPurchaseInquiryService.Phase2
PoDeliveryPerformanceCalc
PoInquiryResults
```

Add small pure calculation helpers where necessary.

Keep business calculations outside Razor.

------------------------------------------------------------------------

# 34. Performance --- specific repo issue to improve

Current delivery performance flow:

``` text
LoadDeliveryPerformanceSpineAsync(...)
LoadPostedDeliveryMovementsAsync(...)
materialise line list
calculate rows in memory
filter preset/status
group supplier in memory
then page
```

This is acceptable for modest SME datasets, but can become expensive
over long ranges.

### V1 implementation rule

Do not rewrite the entire query engine prematurely.

First:

-   preserve correctness;
-   add due-date filtering as early as possible in SQL;
-   ensure supplier/item/warehouse/buyer filters remain SQL-side;
-   limit KPI evaluation to a practical selected due-date range;
-   keep posted GR query constrained to the selected PO number set.

### Guardrail

For Delivery Performance UI, require/default a practical date range
rather than accidentally evaluating years of PO lines.

### Optimization trigger

Only introduce a more complex SQL aggregation if measured performance is
poor on realistic data.

Do not sacrifice the existing deterministic receipt walk for speculative
optimization.

------------------------------------------------------------------------

# 35. Index review

Do not blindly add indexes.

Inspect EF configurations/migrations/current DB scripts for indexes
supporting:

``` text
PoOrder:
CompanyCode + BranchCode + PoNo + PoRelNo

PoOrderDetail:
CompanyCode + BranchCode + PoNo + PoRelNo + Line
EtaDate
ToWarehouse

IvTrxBatch:
CompanyCode + BranchCode + BatchStatus + TrxDtTime

IvTrxBatchDetail:
CompanyCode + BranchCode + PoNo + PoRelNo + PoLineNo
BatchId
```

Add an index only if missing and supported by an actual slow query plan.

------------------------------------------------------------------------

# 36. Required automated tests --- preserve existing tests

Do not delete or weaken:

``` text
PoDeliveryPerformanceCalcTests
PoPurchaseInquiryPhase2Tests
PoPurchaseInquiryServiceTests
PoPurchaseInquiryWorkbenchTests
```

All existing tests must remain green.

------------------------------------------------------------------------

# 37. New tests --- PO Outstanding

Add tests for:

1.  Due Next 7 Days.
2.  No ETA.
3.  Overdue value uses `NetAmount * BalanceQty / PoPurQty`.
4.  PO with zero `PoPurQty` does not divide by zero.
5.  Cancelled latest revision excluded.
6.  Old revision excluded.
7.  Partial receipt still uses persisted `BalanceQty`.
8.  Return-reopened PO reflects persisted balance.
9.  Ageing bucket boundaries: 1, 7, 8, 14, 15, 30, 31, 60, 61.
10. AsOf controls overdue/ageing deterministically.

------------------------------------------------------------------------

# 38. New tests --- true supplier KPI

Mandatory:

### Due population

``` text
PO Date outside selected period
ETA inside selected period
=> INCLUDED in due-period KPI
```

### PO-date false inclusion

``` text
PO Date inside period
ETA outside period
=> NOT INCLUDED in due-period KPI
```

### On-time full

``` text
ETA 10 Sep
GR 100% on 10 Sep
=> OTIF PASS
```

### Partial on time, final late

``` text
ETA 10 Sep
40% GR 10 Sep
60% GR 15 Sep
=> OTIF FAIL
=> OnTimeReceivedQty 40%
=> FullyReceivedDate 15 Sep
```

### Open overdue

``` text
ETA 10 Sep
AsOf 20 Sep
Balance > 0
=> due denominator INCLUDED
=> OTIF FAIL
=> OpenOverdue INCLUDED
```

### Future receipt historical AsOf

``` text
ETA 10 Sep
GR 15 Sep
AsOf 10 Sep
=> not completed at historical AsOf
```

### Period-end late completion

``` text
ETA 30 Sep
GR 2 Oct
KPI due period Sep
AsOf 31 Oct
=> Sep KPI includes line
=> OTIF FAIL
=> completion 2 days late
```

### Return after full

Preserve current tested semantics:

``` text
full GR on time
later vendor return reopens balance
=> historical FullyReceivedDate preserved
=> current line may be Partial/Open
```

### No ETA

``` text
EtaDate null
=> excluded from due-date KPI
=> visible in PO Outstanding “No ETA”
```

### Mixed UOM

Ensure supplier KPI does not derive a fulfilment percentage from:

``` text
SUM(PCS + KG + BOX)
```

### Multiple GRs

Receipt trace must preserve deterministic order:

``` text
TrxDtTime
BatchNo
TrxLineNo
DetailId
```

------------------------------------------------------------------------

# 39. KPI formula contract --- final

For a selected due period and `AsOfDate`:

``` text
DueLines =
latest non-cancelled PO lines
where PoPurQty > 0
and EtaDate within selected due period
```

For each due line:

``` text
FullyReceivedDate =
first posted-GR date where cumulative receipt reaches PoPurQty
(as determined by existing PoDeliveryPerformanceCalc)
```

### OTIF pass

``` text
FullyReceivedDate != null
AND FullyReceivedDate <= EtaDate
```

### OTIF %

``` text
OTIFPassLines / DueLines * 100
```

### In-Full as of evaluation date

``` text
BalanceQty <= 0
```

### In-Full %

``` text
InFullLines / DueLines * 100
```

### On-Time Completion %

For this SME implementation, use:

``` text
FullyReceivedDate != null
AND FullyReceivedDate <= EtaDate
```

over all due lines.

This numerically matches line-level OTIF when "in-full" is defined by
cumulative ordered quantity, so **do not display two duplicate
percentages if they have identical semantics**.

Preferred UI simplification:

``` text
OTIF %
In-Full %
```

and retain existing:

``` text
Completed On-Time %
```

only if users need punctuality among completed deliveries.

Do not clutter the screen with three labels that show the same number.

------------------------------------------------------------------------

# 40. Practical SME KPI set --- final recommendation

For `productionv2`, the must-have supplier KPI set is:

``` text
Due Lines
OTIF %
In-Full %
Completed On-Time %
Open Overdue Lines
Avg Completed Days Late
Max Days Late
```

Optional after currency semantics are verified:

``` text
Open Outstanding Net Value
Overdue Outstanding Net Value
```

Do not add arbitrary weighted supplier scores.

------------------------------------------------------------------------

# 41. PO vs GRN inquiry --- final daily information set

Must provide:

``` text
PO ordered qty
gross received qty
returned qty
net received qty
outstanding qty
ETA
days overdue
GR receipt history
partial status
overdue status
due soon
no ETA
outstanding net value
warehouse
supplier
buyer
```

This is sufficient for normal Malaysian SME procurement operations
without SAP-level complexity.

------------------------------------------------------------------------

# 42. Export

If the existing inquiry export endpoint/framework supports these pages,
extend it rather than adding an unrelated export mechanism.

Export must honor:

``` text
same filters
same tenant/branch gate
same latest-revision rules
same KPI/status semantics
```

Do not let export show records different from the screen.

Respect the existing inquiry export row cap (`MaxExportRows`).

------------------------------------------------------------------------

# 43. Navigation

Use existing inquiry navigation helpers:

``` text
PoInquiryNavigation.cs
```

where applicable.

PO navigation must include revision where required.

GR navigation must use actual `BatchNo`.

Do not construct fragile URLs in multiple Razor components.

------------------------------------------------------------------------

# 44. Implementation sequence --- AI agent

## Step 1 --- Read mandatory repo files

Read:

``` text
PoPurchaseInquiryService.cs
PoPurchaseInquiryService.Phase2.cs
PoDeliveryPerformanceCalc.cs
PoInquiryResults.cs
IPoPurchaseInquiryService.cs
PoOrderOutstandingInquiry.razor(.cs)
PoMatchingInquiry.razor(.cs)
PoDeliveryPerformanceInquiry.razor(.cs)
IvGoodsReceiptService.cs
PoOrder.cs
PoOrderDetail.cs
relevant EF configurations
existing procurement inquiry tests
mandatory .agents UI skills
```

Do not code before this trace is complete.

## Step 2 --- Freeze existing semantics with tests

Run existing procurement inquiry tests.

Add characterization tests for:

``` text
BalanceQty truth
latest revision
return reopening
posted GR only
AsOf clipping
```

## Step 3 --- PO Outstanding additive enhancement

Add:

``` text
Due Next 7 Days
No ETA
Overdue Value
Ageing
Purchase UOM if absent
receipt drill-down
```

Do not alter posting logic.

## Step 4 --- Refactor neutral GR trace only if required

If PO Outstanding needs GR trace for no-ETA lines, extract a shared
neutral posted-GR loader.

Keep `PoDeliveryPerformanceCalc` for ETA timing calculations.

Do not duplicate SQL.

## Step 5 --- Correct delivery KPI date basis

Introduce explicit ETA/due-date population for supplier KPI.

Preserve existing PO-date browsing semantics where backward
compatibility matters.

## Step 6 --- Add true due-period KPI aggregation

Extend supplier summary with:

``` text
DueLines
InFullLines
OtifLines
OpenOverdueLines
OTIF %
In-Full %
```

Keep completed-line on-time metric explicitly named.

## Step 7 --- KPI drill-down

Every KPI must resolve to the same underlying
`PoDeliveryPerformanceLineRow` population.

## Step 8 --- UI enhancement

Keep screens compact and operational.

No duplicate dashboard.

## Step 9 --- Export consistency

Update existing export path if required.

## Step 10 --- Performance review

Test realistic range/volume.

Optimize only proven bottlenecks.

## Step 11 --- Full regression

Run procurement + inventory tests relevant to:

``` text
PO
GR
vendor return
Purchase Invoice
matching
delivery performance
```

------------------------------------------------------------------------

# 45. Files expected to change

Likely:

``` text
ErpWeb.Core/Purchase/IPoPurchaseInquiryService.cs
ErpWeb.Core/Purchase/PoPurchaseInquiryService.cs
ErpWeb.Core/Purchase/PoPurchaseInquiryService.Phase2.cs
ErpWeb.Core/Purchase/PoDeliveryPerformanceCalc.cs
ErpWeb.Core/Purchase/PoInquiryResults.cs

ErpWeb.UI/Purchase/Inquiry/PoOrderOutstandingInquiry.razor
ErpWeb.UI/Purchase/Inquiry/PoOrderOutstandingInquiry.razor.cs
ErpWeb.UI/Purchase/Inquiry/PoDeliveryPerformanceInquiry.razor
ErpWeb.UI/Purchase/Inquiry/PoDeliveryPerformanceInquiry.razor.cs

ErpWeb.Tests/Procurement/Inquiry/PoPurchaseInquiryServiceTests.cs
ErpWeb.Tests/Procurement/Inquiry/PoPurchaseInquiryPhase2Tests.cs
ErpWeb.Tests/Procurement/Inquiry/PoDeliveryPerformanceCalcTests.cs
ErpWeb.Tests/Procurement/Inquiry/PoPurchaseInquiryWorkbenchTests.cs
```

Possibly:

``` text
ErpWeb/Purchase/PoInquiryExportEndpoints.cs
ErpWeb.UI/Purchase/Inquiry/PoInquiryNavigation.cs
```

Only if required.

Not expected:

``` text
PO posting service changes
GR posting service changes
costing service changes
inventory ledger changes
database schema migration
```

------------------------------------------------------------------------

# 46. Hard stop conditions for coding agent

Stop and report rather than improvising if:

1.  `BalanceQty` semantics discovered in posting code contradict
    existing inquiry documentation.
2.  ETA has a different business meaning than delivery commitment.
3.  mixed-currency base/local amount cannot be resolved safely.
4.  a proposed change requires modifying GR/PO posting merely to produce
    an inquiry.
5.  existing tests reveal different return/reversal semantics.
6.  latest-revision logic differs for a specific document workflow.

Do not "fix" transaction logic as a side effect of this inquiry project.

------------------------------------------------------------------------

# 47. Acceptance criteria --- final

The enhancement is accepted only if all are true:

-   [ ] Existing PO save/post/revision logic is unchanged.
-   [ ] Existing GR save/post/rollback logic is unchanged.
-   [ ] Existing inventory stock posting/costing is unchanged.
-   [ ] Existing Purchase Invoice posting is unchanged.
-   [ ] Existing PO Outstanding route/page is enhanced, not duplicated.
-   [ ] Existing Matching route/page remains the reconciliation
    workbench.
-   [ ] Existing Delivery Performance route/page is enhanced, not
    duplicated.
-   [ ] `BalanceQty` remains operational outstanding truth.
-   [ ] `RecvQty`, `ReturnQty`, `InvoicedQty` retain existing semantics.
-   [ ] Latest `PoRelNo` rule is preserved.
-   [ ] Posted GR trace uses exact PO revision + line keys.
-   [ ] Draft GR is excluded from supplier delivery KPI.
-   [ ] AsOf clipping is preserved.
-   [ ] No-ETA open lines remain visible operationally.
-   [ ] No-ETA lines are excluded from due-date KPI denominator.
-   [ ] PO Outstanding ageing uses ETA, not PO Date.
-   [ ] Outstanding/overdue value uses existing `NetAmount` allocation
    rule.
-   [ ] Supplier KPI population uses ETA/due period, not accidentally PO
    Date.
-   [ ] Open overdue due commitments remain in OTIF denominator.
-   [ ] Partial-on-time + final-late is OTIF failure.
-   [ ] Period-end late receipt is correctly attributed to the original
    due period.
-   [ ] Vendor return does not erase historical full-receipt timing.
-   [ ] Supplier KPI does not calculate percentages from mixed-UOM
    quantity sums.
-   [ ] Mixed currencies are never blindly added.
-   [ ] Every KPI drills to exact PO lines.
-   [ ] Every PO line can trace contributing posted GR batches where
    available.
-   [ ] Existing procurement inquiry tests remain green.
-   [ ] New KPI/ageing/period tests pass.
-   [ ] Export, if changed, matches screen semantics.
-   [ ] Tenant/company/branch access remains server-side.
-   [ ] No duplicate KPI/balance persistence table is introduced.
-   [ ] No unnecessary database migration is introduced.

------------------------------------------------------------------------

# 48. Regression commands

Use the repository's actual solution/project structure.

At minimum:

``` bash
dotnet build
dotnet test ErpWeb.Tests --filter "FullyQualifiedName~Procurement.Inquiry"
```

Then run the relevant Inventory Goods Receipt / Vendor Return tests
identified by the agent.

If repository CI has a broader required test command, run it before
completion.

No implementation is "done" while relevant tests fail.

------------------------------------------------------------------------

# 49. Implementation completion report required from AI agent

The coding agent must return:

``` text
1. Files changed
2. Existing logic reused
3. New DTO/service fields
4. KPI formulas implemented
5. Date-basis behavior
6. Return/reversal behavior
7. Currency behavior
8. Tests added
9. Test results
10. Any intentionally deferred optional item
```

No vague "implemented successfully" completion message.

------------------------------------------------------------------------

# 50. Final review verdict

## FINAL --- APPROVED FOR IMPLEMENTATION --- 10/10

The original plan had the correct direction, especially:

-   read-only/additive scope,
-   no duplicate business truth,
-   PO-line grain,
-   traceability,
-   partial/multiple GR handling,
-   tenant isolation,
-   drill-down,
-   test requirements.

The deep `productionv2` review allows the final plan to be more precise:

1.  **Do not invent a new canonical PO/GR layer** --- productionv2
    already has one.
2.  **Use persisted `BalanceQty` as current outstanding truth.**
3.  **Use posted GR movement history for delivery timing.**
4.  **Preserve latest PO revision and AsOf semantics.**
5.  **Enhance the existing three inquiry pages instead of creating
    duplicates.**
6.  **Correct supplier KPI population to use ETA/due period rather than
    PO-date filtering.**
7.  **Do not mislabel completed-line On-Time % as OTIF.**
8.  **Include open overdue commitments in due-period KPI denominator.**
9.  **Do not aggregate incompatible UOM quantities into a supplier
    fulfilment percentage.**
10. **Do not aggregate mixed currencies without a verified
    common-currency basis.**
11. **Preserve the existing tested vendor-return behavior.**
12. **No schema/core posting changes are expected.**

This design is practical for Malaysian SMEs, aligns with the actual
`productionv2` architecture, minimizes regression risk, and gives
procurement staff useful daily operational visibility plus defensible
supplier-performance KPIs.
