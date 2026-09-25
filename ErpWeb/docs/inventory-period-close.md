# Inventory Period Close (Month End) + Stored Closing Balances

Plan of record: `plans/plan-inventoryPeriodClose.prompt.md` (REV 6). Implemented in ErpWeb 2026-09-25.

## What it is

A month-end freeze for **stock**. Closing a period refuses any stock-driving document dated inside
that period (at save **and** at post/rollback), and records a per-slice snapshot of opening, movement
and closing quantities in `IvPeriodCloseBal`. Reopening (with a reason) withdraws that snapshot so the
period can be fixed and re-closed.

This is **not** a full Sales/Procurement freeze (D10): QT / SO / PR / PO / Purchase Invoice and every
other non-stock path stay open. Only stock-driving dates are gated (D14).

## Locked decisions (D1–D14)

- **D1** Granularity: Company + Branch (matches `IvBalLoc`'s tenant key).
- **D2** Correction after close: **reopen only** in v1. `CLOSE` / `REOPEN` are built-in permissions.
- **D3/D13** Snapshot scope: one row per 7-part slice **with a non-zero closing quantity**.
  `IvPeriodCloseBal` is **ending stock, not a movement ledger**; a slice that closes at 0 is absent,
  and the full movement picture stays in `IvTrxHistory`.
- **D4** Unposted drafts: a `NEW` batch dated inside the period blocks the close and is listed.
- **D5** Period identified by dates (`PeriodFrom`/`PeriodTo`), no document number, no `CloseNo`.
- **D6** Sequential and contiguous: `PeriodFrom` must equal `MAX(PeriodTo) + 1 day` of the CLOSED
  periods; with no prior close any start is allowed.
- **D7** `PeriodTo` may not be in the future (a partial month cannot be closed).
- **D8** Reopen reverses in strict order (a later closed period must be reopened first), requires a
  reason and stamps `ReopenCount`/`ReopenedBy`/`ReopenedOn`/`ReopenReason`, and **deletes the snapshot
  lines** (re-closing regenerates them on the same header row).
- **D9** Blocking reconciliation findings: `MISMATCH`, `ORPHAN_HISTORY`, `DUPLICATE_SLICE`,
  `STOCK_COUNT_BATCH_NOT_POSTED`, `STOCK_COUNT_UNPOSTED_VARIANCE`, `STOCK_COUNT_BATCH_STILL_POSTED`.
  Advisory only: `UNEXPECTED_BALANCE`.
- **D10** Scope boundary: stock-affecting paths only.
- **D11** Closing reconciliation: on a later close the pile (`AnchorClosing`) and the ledger
  (`LedgerClosing`) must agree when no movements exist after `PeriodTo`; the check is skipped (and
  `CurrentBalanceCheckApplies = 0`) otherwise, and never applies to the first close.
- **D12** Period membership: `Date(TrxDtTime)` in `[PeriodFrom, PeriodTo]`, inclusive, company-local.
- **D14** Document-date gate: refuse **create and update** of stock-driving documents whose date falls
  in a CLOSED period. In scope: the inventory batch creators (`TrxDtTime`), Stock Count (`CountDate`),
  `SaDoService` (`DoDate`), and the Sales/Purchase stock batch paths. Out of scope (D10): QT / SO / PR
  / PO / `PoInvoice` and any document that never writes an `IvTrxBatch`.

## Formulas and invariants (binding)

All quantities are 4 dp via `IvQty.Round` (`MidpointRounding.AwayFromZero`), per 7-part stock slice
(`IvStockSliceKey`: Company, Branch, ICode, WhCode, LocCode, LotNo, IStatus; unused parts empty string).

```
OpeningQty    = Σ legs with Date(TrxDtTime) <  PeriodFrom  of (ToStdQty − FrStdQty)
InQty         = Σ in-legs  with Date(TrxDtTime) in period  of  ToStdQty
OutQty        = Σ out-legs with Date(TrxDtTime) in period  of  FrStdQty
AdjustNetQty  = Σ ADJ legs in period of (ToStdQty − FrStdQty)      -- subset view, never added
LedgerClosing = OpeningQty + InQty − OutQty
PostLegs      = Σ legs with Date(TrxDtTime) >  PeriodTo  of (ToStdQty − FrStdQty)
AnchorClosing = IvQty.Round(IvBalLoc.StdQty) − IvQty.Round(PostLegs)
ClosingValue  = IvQty.Round(ClosingQty × (pile.UnitPrice ?? item.PurchasePrice ?? 0m))

-- FIRST close: OpeningAdjustQty = IvQty.Round(AnchorClosing − LedgerClosing); ClosingQty = AnchorClosing
-- LATER close: OpeningAdjustQty = 0 (FORCED); ClosingQty = LedgerClosing;
--              and the close REFUSES unless AnchorClosing == LedgerClosing (D11, when PostLegs = 0)
```

- **`CarryForwardOk`** per row: `OpeningQty_N == ClosingQty_{N-1}` of the immediately preceding closed
  period (absent prior row = 0). A mismatch refuses the close.
- **`CurrentBalanceDelta`** per row: `LedgerClosing − IvBalLoc.StdQty`, `NULL` when `PostLegs ≠ 0`.
- Leg classification is **column-driven** (D18): an in-leg is `ToBalLocId`/`ToStdQty` set, an out-leg
  is `FromBalLocId`/`FrStdQty` set. A transfer contributes one out-leg to the source slice and one
  in-leg to the destination slice. `AdjustNetQty` overlaps `InQty − OutQty` and is a disclosure column.
- The reconciliation domain **Ω** is the union of both sides (all piles ∪ all ledger legs with
  `Date(TrxDtTime) ≤ PeriodTo`), strictly larger than the stored rows.

### Worked boundary examples

| Case | `AnchorClosing` | `LedgerClosing` | `OpeningAdjustQty` | Refused? |
|---|---|---|---|---|
| First close, pile 100 with no ledger | 100 | 0 | **+100** | No (the baseline) |
| First close, ledger over-states (pile 100, net 120) | 100 | 120 | **−20** | No (stored unclamped) |
| Later close, agree | 100 | 100 | 0 (forced) | No |
| Later close, pile 95 vs ledger 100 | 95 | 100 | 0 (forced) | **Yes** (D11) |
| Back-period close, `PostLegs = 30` | 70 | 70 | 0 (forced) | No (D11 skipped) |

## Rollback rewrites the ORIGINAL period (Critical finding 2)

The guard keys on `batch.TrxDtTime` — never on the execution date, `ICurrentDateService.Today` or
`batch.RollbackDate`. A September rollback of an August batch destroys August history, so gating on
the batch's own date is what protects the affected period exactly.

## Implemented in ErpWeb

- **Model**: `IvPeriodCloseHdr` / `IvPeriodCloseBal` (+ configs, 2 DbSets in `AppDbContext`).
- **Core**: `IvPeriodCloseGuard` (internal static, the enforcement), `IvPeriodCloseStatuses` /
  `IvPeriodCloseLimits`, `IIvPeriodCloseService` / `IvPeriodCloseService` (+ `Snapshot` partial),
  the ten posting/rollback guard sites in `IvInventoryPostingService`, and the D14 document-date gates
  in the inventory trx services, Stock Count, `IvSpShipmentService` and `SaDoService`.
- **Reconciliation**: `IIvInventoryReconciliationService.ReconcileAsync(menuCode, …)` overload so the
  close workflow reuses the diagnostic under `INV_PERIOD_CLOSE`.
- **UI**: `IvPeriodClose` (`/inventory/period-close`) and `IvPeriodCloseInquiry`
  (`/inventory/period-close-inquiry`), menus `INV_PERIOD_CLOSE` / `INV_PERIOD_CLOSE_INQ`.
- **Scripts**: `create-iv-period-close.sql`, `init-inv-period-close-menu.sql`,
  `init-inv-period-close-inq-menu.sql` (all idempotent).
- **Tests**: `IvPeriodCloseGuardTests` (17) and `IvPeriodCloseServiceTests` (15), trait
  `Category=InventoryPeriodClose`.

## EF Core translation notes

The movement replay materialises `IvStockHistoryRepository.MovementsForBranch` and aggregates in
memory (the full replay **is** the self-audit and is not weakened for performance). No `Math.Abs`, no
`CASE WHEN`, no grouping over entity references — the shapes that are not translatable on this
provider. The guard compares the `date` column **in C#**, never in SQL.

## Verification

- `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=InventoryPeriodClose"` → 32 passed.
- `dotnet test … --filter "Category=Inventory&Category!=SqlServer"` → 520 passed, 0 failed.
- Fast suite baseline: **2253 total / 2238 passed / 15 failed / 0 skipped** — the 15 are the documented
  pre-existing WIP (9 `SaCustServiceTests`, 4 `PoSupplierServiceTests`, 2 library `InvoiceTypeCode`).
- `dotnet build ErpWeb.slnx` (0 errors) after touching `ErpWeb.UI`.
- SQL scripts applied with `sqlcmd -E -d ERPWeb -i scripts/…` (twice, idempotent).
- Manual smoke (needs a browser): close a period → back-dated post refused → DO save refused →
  reopen with reason → lines withdrawn → post/save accepted again.

## Known simplifications (deliberate)

- `SaCdn` / `PoCdn` save-time `DocDate` gates are **not** yet added: their stock batches (CR / VR)
  are created at POST through the guarded posting cores, so the post guard and the carry-forward replay
  are the second line. Recorded as a follow-up.
- A full close/reopen history table is a follow-up (only `ReopenCount` + `LastReopenLineCount` /
  `LastReopenClosingValue` survive a reopen).
