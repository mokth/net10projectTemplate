# Plan: Inventory Period Close (Month End) + Stored Closing Balances

Status: **REV 6 — READY FOR APPROVAL.** REV 5 self-verification plus owner feedback on Sales/Procurement
scope: **D14** adds a document-date gate so stock-driving Sales/PO (and Inventory) creates/updates are
refused when the stock date falls in a closed period — not only at post/rollback time. Phase 0 item 7
(rollback semantics) remains **answered** in "Critical finding 2"; item 8 is the **Phase 1 → 2 exit gate**.
Phase 1 begins on owner approval — see **Approval** below.
Repo `c:\wincom\net10projects`. ERP is **still in development** — no data migration is in scope; the create
script is the primary artefact and is idempotent for the existing dev DB.

## TL;DR

Port the proven legacy `AdPara.CurrentMonth/Year` month-end contract (`docs/cdn-logic.md:138`,
`docs/erp_cyclecount-study.md:230,282`) as a first-class feature: an `IvPeriodCloseHdr` +
`IvPeriodCloseBal` pair, a **period guard** enforced (1) inside the posting engine on both POST and
ROLLBACK and (2) on **create/update** of stock-driving documents whose date would land in a closed
period (**D14**), plus two pages — an action page (close / reopen) and a read-only inquiry page over the
stored opening, movement and closing quantities per stock slice. This is **not** a full Sales/Procurement
freeze: QT / SO / PR / PO / Purchase Invoice and other non-stock paths stay open (**D10**). **No
system-of-record table is ever purged** — the legacy `IvTrxBatch` / `IvTrxBatchDetail` "clear the month"
step is explicitly rejected. The one deletion in this design is of the feature's **own derived snapshot
rows** (`IvPeriodCloseBal`), removed the moment their period is reopened so a superseded snapshot can
never be read as the current balance; the precise scope is in **The one deletion** below.

## Review disposition (this revision)

Review verdict: architecture approved; implementation un-gated pending owner sign-off (see **Approval**).
Every finding is resolved below.

| # | Review finding | Disposition |
|---|---|---|
| C1 | "Nine insertion points" contradicts the list, which has 10 (5 POST + 5 ROLLBACK) | **Fixed.** "Ten" everywhere (insight 5, Phase 1d, Relevant files); the anti-drift sweep now expects 10 |
| C2 | Rollback semantics unproven — a later-dated rollback could rewrite a closed period | **Answered and closed** from the code, not deferred: rollback **physically deletes** history and (in three of five paths) re-stamps `TransDate` with the ORIGINAL `batch.TrxDtTime`. See "Critical finding 2" and the item-7 table |
| C3 | `OpeningAdjustQty` formula undefined | **Fixed.** Exact solved formula, sign convention and rounding in "Formulas and invariants" |
| C4 | No current-balance reconciliation | **Fixed.** Added as **D11**, a Phase 0 proof (item 8) and a close-time conditional refusal |
| I5 | D3 zero-closing slices must be documented as "not a movement ledger" | **Fixed.** Folded into **D13** plus an explicit doc sentence and an inquiry-page operator note |
| I6 | Period-date rule not locked | **Fixed.** **D12** locks `Date(TrxDtTime)` membership and the four-way relationship between `PeriodFrom` / `PeriodTo` / `TrxDtTime` / `Today` |
| I7 | `CarryForwardOk` not defined | **Fixed.** Exact definition given, and explicitly distinguished from the ledger-replay check |
| I8 | Reopen destroys the prior snapshot | **Accepted for v1** with the consequence documented, and a future audit-history table recorded as an explicit follow-up |
| I9 | Concurrency must return a domain error, not a raw SQL exception | **Fixed.** Required by Phase 2e; the unique index stays as the last-resort net |
| A1 | Anti-drift test should resist a *new* method that bypasses the guard | **Fixed.** Phase 1g gains a source-scan guard alongside the behavioural sweep |
| P1 | Do not optimise away the full replay in v1 | **Accepted.** Verification item 8 reworded |

**Second pass (REV 3) — two refinements the first pass left incorrect:**

| # | Gap found on re-check | Disposition |
|---|---|---|
| C3b | `OpeningAdjustQty` was defined by an equation that referenced itself, so nothing anchored `ClosingQty` — the review's question "what is the source of `ActualBalanceAtClose`?" exposed a genuinely circular definition | **Fixed.** `AnchorClosing` (the pile de-trended to `PeriodTo`) is now the anchor, with an explicit two-case rule: solved on the **first** close, **forced to 0** afterwards |
| C4b | **D11 as literally worded would have blocked the first close**, because on the first close the pile and the ledger deliberately disagree — that difference *is* the baseline | **Fixed.** D11 is a refusal rule **from the second close onward**; on the first close the difference is recorded as `OpeningAdjustQty` and is not a failure |
| I5b | The "ending stock, not a movement ledger" wording was required in the doc but not on the page | **Fixed.** Phase 4b now requires the operator note in the markup |

**Third pass (REV 4) — five specification-level gaps:**

| # | Gap found | Disposition |
|---|---|---|
| S1 | `OpeningAdjustQty` still not unambiguous for an implementer | **Fixed.** A fixed 5-step evaluation order with the rounding point named at every step, plus a worked-example table including the negative case |
| S2 | D11 says "per slice" without defining *which* slices | **Fixed.** The reconciliation domain **Ω** is now defined as the union of both sides, and stated to be strictly larger than the stored rows |
| S3 | Rollback-after-close had no dedicated acceptance test | **Fixed.** Phase 1g gains a named acceptance test with the review's exact scenario and a **nothing-was-mutated** assertion |
| S4 | In / Out / `AdjustNetQty` classification unmapped | **Fixed.** New "Leg classification" subsection: column-driven rules, the two-slices-per-row transfer case, the overlap warning, and the NG exclusion |
| S5 | "Nothing is ever deleted" contradicts deleting snapshot lines on reopen | **Fixed.** New "The one deletion" subsection states exactly which five tables are never touched and which one is, and why |

### Review history

| Pass | Date | Result |
|---|---|---|
| 1 | 2026-09-25 | External review received; C1–C4 and findings I5–I9, A1, P1 incorporated → **REV 2** |
| 2 | 2026-09-25 | The same review re-checked against REV 2; two self-inflicted gaps found and fixed (C3b — the `OpeningAdjustQty` formula was circular; C4b — D11 as worded would have blocked the first close) → **REV 3** |
| 3 | 2026-09-25 | Third receipt of the identical review; verified every finding is closed and the review text preserved verbatim as **Appendix A** |
| 4 | 2026-09-25 | Specification-level feedback (5 gaps) — see the REV 4 table above; the feedback text is preserved verbatim as **Appendix B** |
| 5 | 2026-09-25 | Final self-verification (requested: "make it approvable"). Phase 0 item 7 answered from direct reads of all five rollback paths; corrected an over-broad `TransDate` claim in Critical finding 2; fixed the self-contradictory item-8 gate; added the **Approval** section → **REV 5** |
| 6 | 2026-09-25 | Owner asked whether Sales/Procurement block closed-month transactions; confirmed stronger rule: refuse **save/update** (not only post) on stock-driving docs → **D14** / **REV 6** |

**Fourth pass (REV 6) — Sales/Procurement document-date gate:**

| # | Gap found | Disposition |
|---|---|---|
| O1 | Post/rollback-only guard lets operators create DOs / invoices / CDNs dated in a closed month and only fail at post — too late and confusing | **Fixed.** **D14** refuses create/update when the document's stock-driving date is in a `CLOSED` period; same `IvPeriodCloseGuard.EnsureOpenAsync` helper; still not a full commercial freeze (D10) |

The review is kept in this file rather than only summarised, following the house convention for a plan of
record (`docs/sales-master-plan.md` §17 "review history (passes 2–7)"; `plans/Sales-master-v2-plan.md`
carries Review v1 / v2 / v4 records).

### Six load-bearing insights (each drives the design)

1. **The snapshot must be computed from `IvTrxHistory`, never from `IvBalLoc.StdQty`.** `StdQty` is the
   balance *now*; closing August on 25 September would otherwise freeze September's movements into
   August's closing figure.
2. **The close is a self-audit.** `OpeningQty_N` is always the **independent ledger replay**, and it is then
   compared against `ClosingQty_{N-1}` carried forward from the previous snapshot (`CarryForwardOk`). A
   mismatch means history inside a previously closed period was mutated after the close (i.e. exactly the
   "someone rolled back last month's MR" incident). A mismatch is **refused**.
3. **The first close establishes the opening baseline.** Legitimately, piles may hold quantity with no
   ledger (`UNEXPECTED_BALANCE`). The first close absorbs that into a visible `OpeningAdjustQty` plug per
   slice; every later period **must** have a zero plug. This is precisely the "opening-balance baseline"
   the Reconciliation page says it is waiting for.
4. **`UNEXPECTED_BALANCE` must NOT block a close.** It fires on a DB with no opening baseline, so
   blocking on it would make every future close impossible.
5. **There is no single choke point.** `IvInventoryPostingService.DispatchAsync` (line 54) is bypassed by
   every Sales/Purchase `*InTransactionAsync` path and by Stock Count's
   `PostStockAdjustmentInTransactionAsync`. The guard therefore goes into the **ten** methods listed in
   Phase 1d — **five POST and five ROLLBACK** — backed by an anti-drift behavioural sweep test.
6. **Rollback rewrites the ORIGINAL period, not the current one.** Every rollback path **physically
   deletes** the `IvTrxHistory` rows of the batch (`IvStockPostingRepository.cs:500-505` via
   `IvInventoryPostingService.cs:577, 798, 1159, 1764, 2260`); three of the five (MI, ADJ, TR) also re-stamp
   `IvBalLoc.TransDate` with the original `batch.TrxDtTime`, while MR leaves `TransDate` alone. A September
   rollback of an August batch therefore destroys August history either way. The guard must key on
   `batch.TrxDtTime` — never on the execution date or on `RollbackDate`. See "Critical finding 2".

## Decisions locked (owner-confirmed)

- **D1 Granularity:** Company + Branch (matches `IvBalLoc`'s tenant key; a company-wide close is the UI
  looping branches).
- **D2 Correction after close:** **reopen only** in v1. `PERMISSIONCODES.Close` and `PermissionCodes.Reopen`
  **already exist** as built-ins (`ErpWeb.Core/Menus/PermissionCodes.cs`) — **zero new permission
  constants**. Reversal documents (compensating batch dated today) are explicitly deferred.
- **D3 / D13 Snapshot scope:** one line per 7-part stock slice **with a non-zero closing quantity** only.
  A missing prior-period line therefore means "previous closing was 0". The consequence is deliberate and
  must be stated on the inquiry page and in the doc: a slice that opened at 100, took 50 in and issued 150
  out closes at 0 and **will not appear**. `IvPeriodCloseBal` therefore represents **ending stock, not a
  complete movement ledger**; the full movement picture stays in `IvTrxHistory`.
- **D4 Unposted drafts:** a `NEW` batch dated inside the period **blocks** the close and is listed in the
  error. Delete/cancel of such a batch stays permitted afterwards so nothing can ever be trapped.
  After a successful close, **D14** prevents creating new stock-driving drafts dated in that closed
  period; D4's delete path remains only as the escape hatch for any leftover `NEW` row.
- **D14 Document-date gate (stock-driving only) — owner-confirmed REV 6.** Refuse **create and update**
  (including rebuilds that write `TrxDtTime`) when the document's **stock-driving date** falls in a
  `CLOSED` period for that company+branch. Same authority as the posting guard:
  `IvPeriodCloseGuard.EnsureOpenAsync` keyed on the date component (D12), never on `Today` or UTC.
  Rationale: failing only at post lets operators build a pile of undeliverable documents; month-end
  should stop the bad date at the keyboard.

  | In scope (refuse save/update) | Stock-driving date |
  |---|---|
  | Inventory trx services that set `IvTrxBatch.TrxDtTime` (MR/GR/MI/TR/ADJ/SC/VR/SP batch create, Stock Count `CountDate`, etc.) | `TrxDtTime` / `CountDate` |
  | `SaDoService` | `DoDate` (already required equal to SP `batch.TrxDtTime` at post) |
  | `SaInvoiceService` when the invoice path creates/updates a stock-out batch | `InvDate` |
  | `SaCdnService` when creating/updating a stock-in batch | `DocDate` |
  | `PoCdnService` when creating/updating a stock-out batch | `DocDate` |

  | Out of scope (D10 — stay open) |
  |---|
  | Sales QT / SO / customer masters / price inquiry |
  | Purchase PR / PO / **Purchase Invoice** / supplier masters |
  | Any document that never writes an `IvTrxBatch` / never posts stock |
  | Delete/cancel of an existing `NEW` batch (D4) |
  | Reopen of the period (then the same dates become editable again) |

  Edit that **moves** a document's stock-driving date **into** a closed period is refused the same way.
  Edit that only changes non-date fields on a document whose date is already in a closed period is also
  refused (the row must not be mutated while that period stays `CLOSED`); the operator must reopen first.
  Post/rollback guards (Phase 1d) remain the second line of defence.

## Decisions recommended (please confirm or override)

- **D5 Period identified by dates, not by a document number.** `PeriodFrom`/`PeriodTo` (both `date`) are
  the key; the human handle is `2026-08 / HQ`. No `IRunningNumberService` key, so no multi-branch
  number-consumption oddity. An explicit `CloseNo` is optional later.
- **D6 Sequential and contiguous:** the new period's `PeriodFrom` must equal `MAX(PeriodTo) + 1 day` of the
  non-reopened closes; with no prior close, any `PeriodFrom` is allowed (the legacy "system start" case).
- **D7 `PeriodTo` may not be in the future** (`ICurrentDateService.Today`), so a partial month cannot be
  closed.
- **D8 Reopen reverses in strict order:** a period cannot be reopened while a later period is still
  `CLOSED`. Reopen requires a reason, stamps `ReopenCount`/`ReopenedBy`/`ReopenedOn`/`ReopenReason`, and
  **deletes the snapshot lines** (re-closing regenerates them on the same header row).
- **D9 Blocking reconciliation findings:** `MISMATCH`, `ORPHAN_HISTORY`, `DUPLICATE_SLICE`,
  `STOCK_COUNT_BATCH_NOT_POSTED`, `STOCK_COUNT_UNPOSTED_VARIANCE`, `STOCK_COUNT_BATCH_STILL_POSTED`.
  Advisory only: `UNEXPECTED_BALANCE`.
- **D10 Scope boundary:** this covers **stock-affecting** paths only — posting/rollback cores **and**
  the D14 document-date gate on stock-driving Sales/PO/Inventory saves. Non-stock commercial and
  financial documents (`PoInvoice` has no `IIvInventoryPostingService` reference at all; QT/SO/PR/PO
  likewise) are out of scope until the AP/GL phase adds `AccountingStatus`. A closed inventory month is
  **not** a full Sales/Procurement freeze.
- **D11 Closing reconciliation (conditional).** `LedgerClosing` (see "Formulas and invariants") must equal
  `IvBalLoc.StdQty` per slice, *when no movement exists after `PeriodTo`* (`PostLegs = 0`). Two bounds on
  this rule matter and both are load-bearing:
  - **It does not apply to the first close.** On the first close the pile and the ledger deliberately
    disagree — that difference *is* the opening baseline being established, and it is recorded as
    `OpeningAdjustQty`. Treating it as a failure would make the feature impossible to start.
  - **When movements after `PeriodTo` exist** (closing a back period) the comparison is invalid, because
    `LedgerClosing` legitimately excludes them. The header records `CurrentBalanceCheckApplies = 0` so the
    inquiry never implies a check that never ran.
  - **The comparison domain is Ω**, the union of both sides — not just the stored rows, which hold only
    non-zero closers (D13). Ω is defined in "The reconciliation domain (Ω)" above, along with the
    `CLOSING_WITHOUT_PILE` finding for a ledger slice that has no pile.
  A difference in the applicable case is a blocking refusal naming the slice, both values and the delta.
- **D12 Period membership.** A movement belongs to a period iff `Date(TrxDtTime)` falls in
  `[PeriodFrom, PeriodTo]`, inclusive. The **company-local date component is the only authority** — no UTC
  conversion anywhere in this feature. `PeriodFrom <= PeriodTo`, and `PeriodTo <= ICurrentDateService.Today`
  (D7).
- **D13 Zero-closing slices are excluded by design** and the table is a closing-balance snapshot, not a
  movement ledger (see D3). Full movement remains available from `IvTrxHistory`.

### The one deletion (resolving the wording conflict in S5)

"Nothing is ever deleted" is true of every system-of-record table and false of exactly one derived table.
Stated precisely, so the two statements can never be read as contradictory again:

| Table | Removed by this feature? | Why |
|---|---|---|
| `IvTrxBatch`, `IvTrxBatchDetail` | **Never** | Load-bearing: rollback reads them, `IsForceClosed` is the force-close tombstone, `RollbackCount` / `RollbackOperationId` are the audit trail |
| `IvTrxHistory` | **Never** | The historical source for the replay, Stock Card openings, reference-integrity guards and cost |
| `IvBalLoc` | **Never** | The live pile — rows are mutated in place and are not removed even at qty 0 |
| `IvPeriodCloseHdr` | **Never** | The close record; a reopen stamps it, it is never deleted |
| `IvPeriodCloseBal` | **Yes — on reopen only** | A **derived view** of the ledger and therefore re-derivable at will. Keeping superseded rows after a reopen would make them a competing authority for the closing figure, which is the exact failure this design exists to avoid (D8) |

The accurate claim is therefore: **no system of record is ever deleted; the only deletion is of this
feature's own derived snapshot, and only because a superseded snapshot must not be readable as current.**
Nothing outside `IvPeriodCloseBal` is affected by this feature at all.

## Formulas and invariants (binding)

All quantities are 4-dp decimals via `IvQty.Round` (`MidpointRounding.AwayFromZero`). Every equation is
per **7-part stock slice** (`IvStockSliceKey`, in its column order, with unused `LocCode` / `LotNo` /
`IStatus` as empty string — never null).

### Period membership (D12)

| `PeriodTo` | `TrxDtTime` | Period |
|---|---|---|
| 2026-08-31 | 2026-08-31 23:59:59 | August |
| 2026-08-31 | 2026-09-01 00:00:00 | September |
| 2026-08-31 | 2026-08-31 00:00:00 | August |

### Opening, movement and closing

```
-- Shared inputs (per 7-part slice; every quantity 4-dp via IvQty.Round)
OpeningQty    = Σ legs with Date(TrxDtTime) <  PeriodFrom  of (ToStdQty − FrStdQty)
InQty         = Σ in-legs  with Date(TrxDtTime) in period  of  ToStdQty
OutQty        = Σ out-legs with Date(TrxDtTime) in period  of  FrStdQty
AdjustNetQty  = Σ ADJ legs in period of (ToStdQty − FrStdQty)      -- SUBSET VIEW, not a term
LedgerClosing = OpeningQty + InQty − OutQty                        -- ledger-derived; no plug
PostLegs      = Σ legs with Date(TrxDtTime) >  PeriodTo  of (ToStdQty − FrStdQty)
AnchorClosing = IvQty.Round(IvBalLoc.StdQty) − IvQty.Round(PostLegs)  -- the pile, de-trended to PeriodTo

-- FIRST close for the company/branch: the baseline is established here.
-- Fixed evaluation order; each bracket is rounded once, then differenced:
--   1. a = IvQty.Round(IvBalLoc.StdQty)              -- the pile as it stands NOW
--   2. b = IvQty.Round(PostLegs)                     -- movements AFTER PeriodTo
--   3. AnchorClosing = IvQty.Round(a − b)            -- the pile AS AT PeriodTo
--   4. LedgerClosing = IvQty.Round(OpeningQty + InQty − OutQty)
--   5. OpeningAdjustQty = IvQty.Round(AnchorClosing − LedgerClosing)   -- SIGNED, stored as-is
ClosingQty       = AnchorClosing

-- EVERY LATER close: the baseline already exists, so nothing may be absorbed
OpeningAdjustQty = 0m                                              -- FORCED, never solved
ClosingQty       = LedgerClosing
-- and the close REFUSES unless IvQty.Round(AnchorClosing) == IvQty.Round(LedgerClosing)   (D11)

ClosingValue = IvQty.Round(ClosingQty × (pile.UnitPrice ?? item.PurchasePrice ?? 0m))
```

The two figures that the review asked to be made explicit:

- **`ActualBalanceAtClose` is `IvBalLoc.StdQty`, de-trended to `PeriodTo`** — that is `AnchorClosing`. The
  de-trending is what makes it usable: `StdQty` is the balance *now*, so movements posted after `PeriodTo`
  ("`PostLegs`") must be removed before it can describe the period's end. When `PostLegs = 0` — the normal
  month-end case — `AnchorClosing` is simply `IvQty.Round(IvBalLoc.StdQty)`.
- **`IndependentLedgerCalculatedBalanceAtClose` is `LedgerClosing`.** So the review's intended relationship
  holds exactly: `OpeningAdjustQty = AnchorClosing − LedgerClosing`, evaluated **only on the first close**.
  Afterwards it is forced to zero and the two must agree instead, which is precisely D11's refusal.

Sign and rounding, explicitly: `OpeningAdjustQty` is a **signed** `decimal(18,4)`; a negative value is legal
and is stored unclamped (it means the ledger over-states the pile). Both sides are rounded individually with
`IvQty.Round` **before** differencing, so a sub-4-dp drift can never manufacture a plug. The comparison is
**per stock slice**, never aggregated over an item or a warehouse.

- **`OpeningQty` is always the independent ledger replay**, never a copy of the prior snapshot. It is the
  stored authority and the replay is what makes the close a self-audit.
- **`AdjustNetQty` is a subset of `InQty − OutQty`.** Adding it to the closing equation would double-count.
  It exists only so the inquiry can show how much of the period's movement was adjustment.
- Storing the plug as a **visible column** — instead of silently forcing `ClosingQty` to the pile value — is
  what keeps the first close auditable: the baseline is a recorded number someone can inspect, not a silent
  reconciliation. `OpeningAdjustSlices` counts the non-zero plugs on the header.
- **`CarryForwardOk`** (per row) is `true` iff, after 4-dp normalisation, `OpeningQty_N` for this slice
  equals `ClosingQty_{N-1}` for the same slice in the immediately preceding closed period; a slice absent
  from the prior rows is treated as prior closing = 0 (D13); with no prior closed period it is `true` by
  definition. It is **not** the ledger-replay check — a replay mismatch is a separate finding.
- **`CurrentBalanceDelta`** (per row) = `IvQty.Round(LedgerClosing) − IvQty.Round(IvBalLoc.StdQty)`, and is
  `NULL` when `PostLegs ≠ 0` (the check is not applicable, D11). `0` is a pass; on the first close a non-zero
  value is **expected** and is explained by `OpeningAdjustQty`.

### Leg classification (In / Out / AdjustNetQty) — resolves S4

**Classification is column-driven, never type-driven**, because one `IvTrxHistory` row carries up to two legs
and a transfer's two legs belong to **different slices**:

| Leg | Column predicate | Contributes |
|---|---|---|
| Out-leg | `FromBalLocId IS NOT NULL` and `FrStdQty IS NOT NULL` | `OutQty += FrStdQty` for the slice of the pile `FromBalLocId` points at |
| In-leg | `ToBalLocId IS NOT NULL` and `ToStdQty IS NOT NULL` | `InQty += ToStdQty` for the slice of the pile `ToBalLocId` points at |
| `AdjustNetQty` | `TrxType = IvTrxTypes.StockAdjustment` (`"ADJ"`) | `+= ToStdQty − FrStdQty`, **signed** |

Three consequences an implementer must not miss:

1. **One row can contribute to two slices.** A stock transfer (`TR`) is a single history row with *both* legs
   populated — an Out-leg for the source slice and an In-leg for the destination slice. A row-by-row
   implementation that reads one slice per row gets every transfer wrong. The replay must be a **union of two
   leg projections**, exactly as D18 established for the reconciliation service.
2. **`AdjustNetQty` overlaps `InQty − OutQty`; it does not add to it.** ADJ legs are already counted as
   in-legs or out-legs, so `AdjustNetQty` is the same movements re-summed under a type filter and is a
   **disclosure** column. Adding it to the closing equation double-counts — the single most likely
   implementation error in this feature.
3. **Rows with no `BalLoc` legs contribute nothing and are skipped.** A non-stock goods receipt (`NG`)
   carries only `FrWarehouse` / `ToWarehouse` **strings** and no `BalLoc` foreign keys, so it never enters
   the replay — never resolve a slice from those string columns. A leg whose `BalLoc` id does not resolve is
   skipped too, and its existence is already a blocking precondition (`ORPHAN_HISTORY`, D9).

Cross-check by type: `MR` / `CR` / `GR` → In only. `MI` / `SC` / `VR` → Out only. `SP` (sales-out, posted via
the Sales path rather than `DispatchAsync`) → Out only. `TR` → Out on the source slice **and** In on the
destination slice. `ADJ` → In or Out depending on direction, plus `AdjustNetQty`. `NG` → nothing.

### The reconciliation domain (Ω) — resolves S2

D11 compares "per slice", so *which* slices are compared must be stated or the check is not a check. It is
the **union of both sides** over `(CompanyCode, BranchCode)`:

```
Ω = { slices of every IvBalLoc row }  ∪  { slices of every ledger leg with Date(TrxDtTime) ≤ PeriodTo }
```

- **Ω is strictly larger than the stored rows.** Rows are stored only for non-zero closers (D13), while the
  comparison must also cover piles that closed at **zero** (a zero pile against a ledger net of 50 is a real
  mismatch) and slices that appear only in the ledger. Storing and comparing are different domains on purpose.
- **A slice in Ω with no pile row** is compared against pile qty `0` and raises `CLOSING_WITHOUT_PILE`. In
  practice this cannot reach the frontier, because such a leg is already `ORPHAN_HISTORY` (D9) and blocks the
  close first — the rule exists so the invariant is *stated* rather than assumed.
- **A pile in Ω with no ledger legs at all** is the first-close baseline case: its delta is absorbed by
  `OpeningAdjustQty` on the first close and refused afterwards.
- **Excluded from Ω:** `IvBalLoc` rows with `StdQty = 0` **and** no ledger legs (nothing to verify), and any
  row for another company or branch.

### Worked examples — resolves S1

Baseline for every row: `StdQty = 100`, `PostLegs = 0` ⇒ `AnchorClosing = 100`.

| Case | `AnchorClosing` | `LedgerClosing` | `OpeningAdjustQty` | Refused? |
|---|---|---|---|---|
| First close — pile 100 with no ledger (the `UNEXPECTED_BALANCE` case) | 100 | 0 | **+100** | No — this *is* the baseline |
| First close — ledger over-states the pile (pile 100, ledger net 120) | 100 | 120 | **−20** | No — stored unclamped; negative is legal |
| Later close — pile and ledger agree | 100 | 100 | `0` (forced) | No |
| Later close — pile 95 vs ledger 100 | 95 | 100 | `0` (forced) | **Yes** — D11, delta −5 |
| Back-period close, `PostLegs = 30` | 70 | 70 | `0` (forced) | No — D11 not applicable; carry-forward + replay only |

## Critical finding 2 — rollback rewrites the ORIGINAL period (verified, not assumed)

The review's central risk is real and is now **verified from the code** rather than left to Phase 0. All
five rollback paths:

1. **Mutate the pile, and in three of the five paths re-stamp `TransDate` with the ORIGINAL date.** MI, ADJ
   and TR rollback route through `IncreaseBalLocQtyAsync` / `DecreaseBalLocQtyAsync`, which receive
   `batch.TrxDtTime` — the date the batch was *posted*, not the date the rollback runs
   (`IvInventoryPostingService.cs:1135, 1148, 1753, 2237, 2250`) — and the repository writes it straight
   into `IvBalLoc.TransDate` (`IvStockPostingRepository.cs:592-598` SQL Server, `:604` SQLite), which is
   load-bearing for FIFO ordering. MR rollback mutates `bal.StdQty` **in memory** and leaves `TransDate`
   alone; NG rollback touches no pile. See the item-7 table below — the earlier claim that *every* path
   re-stamps `TransDate` was over-broad and is corrected here.
2. **Physically delete the ledger.** `_posting.RemoveHistory(...)` → `db.IvTrxHistories.RemoveRange(rows)`
   (`IvStockPostingRepository.cs:500-505`), reached from `IvInventoryPostingService.cs:577, 798, 1159, 1764,
   2260`. The movement does not become a negative reversal — it ceases to exist.
3. **Stamp a different clock.** `batch.RollbackDate` is written from `DateTime.UtcNow` while
   `batch.TrxDtTime` is company-local, so the two are not merely different values but in different time
   zones. Any comparison between them must normalise deliberately.

**Phase 0 item 7 — ANSWERED (all five paths read directly, 2026-09-25).**

| Rollback path | Deletes history? | Mutates pile? | Re-stamps `TransDate`? |
|---|---|---|---|
| `RollBackInventoryMRCoreAsync:483` | Yes — `RemoveHistory` | Yes — `bal.StdQty −=` in memory | **No** |
| `RollBackNonStockGoodsReceiptCoreAsync:773` | Yes — `RemoveHistory` | No (non-stock) | No |
| `RollBackInventoryADJAsync:1051` | Yes — `RemoveHistory` | Yes — `Increase`/`DecreaseBalLocQtyAsync` | **Yes** (`batch.TrxDtTime`) |
| `RollBackInventoryMICoreAsync:1668` | Yes — `RemoveHistory` | Yes — `IncreaseBalLocQtyAsync` | **Yes** (`batch.TrxDtTime`) |
| `RollBackInventoryTRAsync:2134` | Yes — `RemoveHistory` | Yes — both source and destination piles | **Yes** (`batch.TrxDtTime`) |

Three conclusions, now proven rather than assumed:

1. **The guard key is correct.** Every history row is written with `TrxDtTime = batch.TrxDtTime` at post
   (`IvInventoryPostingService.cs:409, 740, 994, 1586, 2064`), and a batch has exactly one `TrxDtTime`, so
   the rows a rollback deletes always belong to the *batch's* period. The transfer "two legs could straddle
   a month" worry was unfounded — a batch's single date pins both legs to one period. Gating on
   `batch.TrxDtTime` therefore protects the affected historical period exactly.
2. **The `TransDate` claim is asymmetric and must not be relied on.** MI/ADJ/TR re-stamp it (FIFO impact);
   MR mutates `StdQty` in memory and leaves it; NG never touches a pile.
3. **Rollback physically deletes history in every path** — there is no compensating-leg mode anywhere. The
   `RollbackMode` (Unpost vs Reverse) seam remains a future enhancement, exactly as D2 states.

**Consequence for the guard (binding).** The guard is evaluated against **`batch.TrxDtTime`**, read
immediately after `LockBatchForUpdateAsync`, so it always protects the period the *movement* belongs to.
Using the execution date, `ICurrentDateService.Today`, or `batch.RollbackDate` would let a September
rollback delete August history — exactly the bypass the review describes. This also makes insight 2 the
*second* line of defence: even if a guard site were ever missed, the next close's carry-forward replay
detects the mutated period and refuses.

## Implementation steps

### Phase 0 — Verification spikes (blocks schema freeze)

Must complete before Phase 1's schema is written; each has a concrete answer, not an opinion.

1. **What populates `IvTrxBatch.TrxDtTime` at each of the 13 creation sites** (`IvMiscReceiptService:344`,
   `IvGoodsReceiptService:518`, `IvMiscIssueService:325`, `IvScrapService:326`, `IvStockAdjustmentService:290`,
   `IvStockCountService:1142`, `IvStockReturnService:305`, `IvStockTransferService:333`,
   `IvVendorReturnService:329`, `IvSpShipmentService:150`, `PoCdnService:1648,2001`, `SaCdnService:1550`).
   Confirm each is company-local (matching `ICurrentDateService`) and not UTC. `IvStockCountHdr.CountDate`
   is the confirmed back-datable case.
2. **`Menu.MenuCode` column width** — choose `INV_PERIOD_CLOSE` / `INV_PERIOD_CLOSE_INQ` only after
   confirming both fit.
3. **`IV_INQUIRY` child SortOrder 10 is free** (children currently 1–9); pick the next free SortOrder
   under `INVENTORY` for the close page by reading `ErpWeb/Menus/menus.xml`.
4. **Which UI folder holds `IvStockCount.razor`** — place the close page beside it.
5. **Confirm every `IvBalLoc.StdQty` mutation writes a matching `IvTrxHistory` leg** for the posted paths
   (the premise that makes the replay authoritative). Orphan/missing cases are the reconciliation service's
   own findings.
6. **Green baseline re-measure** before starting (documented baseline: 2221 total / 2206 passed / 15 failed
   with `Category!=SqlServer`).
7. **Rollback mutation analysis — five paths. ANSWERED** — see the table and conclusions in "Critical
   finding 2". Kept in this list only as the record that the review's required analysis was performed and
   where to find it, not as an open item.
8. **Prove current-balance reconciliation (D11) — the Phase 1 → 2 exit gate.** On a scratch DB built by
   `create-iv-period-close.sql`, demonstrate that `LedgerClosing` equals `IvBalLoc.StdQty` for every slice
   when `PostLegs = 0`, that `AnchorClosing` collapses to `IvQty.Round(IvBalLoc.StdQty)` in that case, and
   what the two figures do when `PostLegs ≠ 0` — the check must be **skipped**
   (`CurrentBalanceCheckApplies = 0`), never silently passed. Moved here from the schema-freeze gate because
   a proof needs the tables to exist.

> **Schema-freeze gate (corrected).** Item 7 is ANSWERED and no longer gates. Item 8 moves to the **Phase 1
> → 2 exit** because it cannot be proven without the tables. The gate that remains: no `CREATE`/`ALTER`
> script is applied until items 1–6 have a recorded answer — all six are one-line facts or a single command
> run, and none of them blocks the Phase 1 modelling.

### Phase 1 — Schema + the period guard (the part that "prevents")

*Parallel with nothing; everything else depends on 1a–1d.*

- **1a. Model.** `IvPeriodCloseHdr` (ID identity PK; CompanyCode/BranchCode nvarchar(5); PeriodFrom/PeriodTo
  `date`; Status CLOSED/REOPENED; ClosedBy/ClosedOn; ReopenCount; ReopenedBy/On/Reason; LineCount;
  SkippedZeroSlices; OpeningAdjustSlices; CarryForwardMismatchSlices; CurrentBalanceCheckApplies bit;
  CurrentBalanceMismatchSlices; TotalOpeningValue/TotalInValue/TotalOutValue/TotalClosingValue
  decimal(18,4); LastReopenLineCount/LastReopenClosingValue; UnpostedBatchCount; ReconcileFindingCount;
  Remark nvarchar(250); RowVersion; audit aliases `Created`/`UserID`/`Updated`/`UpdatedUID`; `CloseNo` NOT
  included per D5). `IvPeriodCloseBal` (ID identity PK; PeriodCloseId FK Restrict; CompanyCode/BranchCode;
  ICode nvarchar(30); WhCode nvarchar(20); LocCode nvarchar(10) default ''; LotNo nvarchar(50) default '';
  IStatus nvarchar(10) default ''; OpeningQty/OpeningAdjustQty/InQty/OutQty/AdjustNetQty/ClosingQty
  decimal(18,4); StdUom nvarchar(10); UnitPrice decimal(18,4); ClosingValue decimal(18,4); LegCount int;
  CarryForwardOk bit; CurrentBalanceDelta decimal(18,4) NULL; audit `Created`/`UserID`).
- **1b. EF config.** Mirror `IvStockCountHdrConfiguration.cs` / `IvStockCountLineConfiguration.cs`:
  explicit `HasKey(e => e.Id)` → column `ID` + `ValueGeneratedOnAdd()`; `RowVersion` `IsRowVersion()` on the
  header only; audit column aliases; `HasPrecision(18,4)` on every decimal. Unique
  `UQ_IvPeriodCloseHdr_Period (CompanyCode, BranchCode, PeriodFrom)`;
  `UQ_IvPeriodCloseBal_Slice (PeriodCloseId, ICode, WhCode, LocCode, LotNo, IStatus)`. The hot-path index is
  `IX_IvPeriodCloseHdr_Tenant_Status (CompanyCode, BranchCode, Status)` INCLUDE `PeriodTo`. Two DbSets in
  `AppDbContext` beside `IvTrxHistories` (line 46).
- **1c. Guard helper.** New `ErpWeb.Core/Inventory/IvPeriodCloseGuard.cs` — an **`internal static`** helper
  (no DI, no ctor change, so the 8 test files that construct `IvInventoryPostingService` are untouched;
  `InternalsVisibleTo("ErpWeb.Tests")` is already configured at `ErpWeb.Core.csproj:15`). Surface:
  `Task<DateTime?> ClosedThroughAsync(AppDbContext db, string company, string branch, CancellationToken)`
  and `Task<string?> EnsureOpenAsync(AppDbContext db, string company, string branch, DateTime trxDtTime,
  CancellationToken)`. Compares `trxDtTime.Date` against the `date` column **in C#** (no SQL translation
  risk). Refusal message mirrors the `IvStockCountLimits.MaxBackdateDays` precedent — names the date and
  the closed-through date. Only `Status = CLOSED` rows count.
- **1d. Ten insertion points** in `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`, each immediately
  after `LockBatchForUpdateAsync` so `batch.TrxDtTime` is in hand. **Post:** `PostInventoryMRCoreAsync`
  (234), `PostNonStockGoodsReceiptCoreAsync` (699), `PostInventoryADJCoreAsync` (846),
  `PostInventoryMICoreAsync` (1454), `PostInventoryTRAsync` (1792). **Rollback:**
  `RollBackInventoryMRCoreAsync` (483), `RollBackNonStockGoodsReceiptCoreAsync` (773),
  `RollBackInventoryADJAsync` (1051), `RollBackInventoryMICoreAsync` (1668), `RollBackInventoryTRAsync`
  (2134). Ten total: **five POST, five ROLLBACK**. `DispatchAsync` is deliberately **not** guarded so there
  is exactly one authority. `DeleteNewStockInBatchInTransactionAsync` (1380) is deliberately **not** guarded
  — it changes no stock and must stay available to clean up a stale draft inside a closed period (D4).
  Every one of the ten passes **`batch.TrxDtTime`** to `EnsureOpenAsync`; none passes
  `ICurrentDateService.Today` or `batch.RollbackDate` (see "Critical finding 2").
- **1e. Statuses/limits constant** — `IvPeriodCloseStatuses` (`Closed`, `Reopened`) next to
  `IvStockCountStatuses.cs`.
- **1f. Script** — `scripts/create-iv-period-close.sql`, idempotent `IF OBJECT_ID(...) IS NULL` guarded
  (the shape `create-iv-stock-count.sql` used successfully on both scratch and dev), plus the two indexes
  and a verification SELECT.
- **1g. Tests** — `ErpWeb.Tests/IvPeriodCloseGuardTests.cs`: refuses post in a closed period per path;
  refuses rollback; allows the open period; allows exactly `PeriodTo` (boundary); allows the day after;
  no header row ⇒ allowed; `REOPENED` row ⇒ allowed; tenant isolation; `Category` traits
  `Inventory` + `InventoryPeriodClose`.
  - **Dedicated acceptance test — rollback after close (S3).** The review's exact scenario, as its own named
    test rather than a sub-case: post an MR dated **2026-08-20**, close August, then on a simulated
    **2026-09-05** attempt the rollback. Assert in this order:
    (a) the operation is **refused**;
    (b) the message names the closed period for the **batch's** date (August), never the execution date;
    (c) **nothing was mutated** — `IvTrxBatch.BatchStatus` is still `POSTED`, `RollbackCount` is unchanged,
        the `IvTrxHistory` row still exists with the same `Id`, and `IvBalLoc.StdQty` is unchanged.
    (c) is the assertion that matters: it proves the guard fires **before** the first leg is restored, not
    between the restore and the history delete, so a partial rollback cannot pass the refusal check.
    Repeat for all five rollback paths, for the boundary dates (`TrxDtTime` = `PeriodFrom` 00:00:00 and
    `PeriodTo` 23:59:59), and add the mirror positive case — the same rollback **succeeds** once the period
    is reopened.
  - **Anti-drift, behavioural:** a sweep over every stock-affecting service in `ErpWeb.Core/Inventory/`
    plus `SaDoService` / `SaInvoiceService` / `SaCdnService` / `PoCdnService` asserting each refuses in a
    closed period, and asserting the count is **10** so a path silently dropping out of the sweep fails.
  - **Anti-drift, structural:** a source-scan guard that fails when any method in
    `IvInventoryPostingService.cs` mutates stock (`IncreaseBalLocQtyAsync` / `DecreaseBalLocQtyAsync` / an
    assignment to `bal.StdQty`) or calls `RemoveHistory` without the matching `EnsureOpenAsync` call. This
    is what stops a *future* posting method from bypassing the guard, which the behavioural sweep alone
    cannot catch.
- **1h. Document-date gates (D14).** Call the same `IvPeriodCloseGuard.EnsureOpenAsync` from the
  create/update (and batch-rebuild) paths of every stock-driving service listed under D14, keyed on that
  document's stock-driving date. Insertion is at the service layer **before** the first write of
  `TrxDtTime` / `DoDate` / `InvDate` / `DocDate` / `CountDate` into persistence — not only inside the
  posting cores. `ErpWeb.Core` is one assembly, so Sales/Purchase/Inventory services may call the
  `internal static` guard directly (no new DI surface). Refusal message mirrors the posting guard (names
  the date and the closed-through date). **Do not** add gates to QT/SO/PR/PO/`PoInvoice`. Tests (extend
  `IvPeriodCloseGuardTests` or a sibling `IvPeriodCloseDocumentGateTests`): one save-refusal case per
  in-scope service family (Inventory trx, SaDo, SaInvoice stock path, SaCdn stock path, PoCdn stock path)
  plus a positive control that QT/SO/PR/PO/`PoInvoice` still save when August is closed. Delete/cancel of
  `NEW` batches must still succeed (D4).

### Phase 2 — Close workflow + snapshot generation (*depends on 1a–1f and on the item-8 seeded D11 proof*)

- **2a. Movement replay.** Use `IvStockHistoryRepository.MovementsForBranch(db, company, branch)` captured
  in a **local variable** before projection (the documented unexpandable-helper trap), resolve each leg's
  slice **through the `BalLoc` it points at** (`FromBalLocId`/`ToBalLocId`) — the D18 rule, never string
  inference — and project **flat scalar columns** (`GROUP BY` over a slice holding entity references is
  not translatable; the Phase-2 lesson). OPENING and the two MOVEMENT totals come from two leg-filtered
  one-sided sums, never `CASE WHEN`. Never `Math.Abs` (no SQL translation on this provider).
  4 dp via `IvQty.Round`.
- **2b. Close transaction** (`IIvPeriodCloseService.CloseAsync`), one transaction: resolve scope via the
  existing `IvInquiryScopeResolver` (menu in a `KnownMenus` set) → require `CLOSE` → validate
  `PeriodTo <= today` → validate contiguity (D6) → collect blocking `NEW` batches (D4) → run the
  reconciliation precondition (D9) → replay `OpeningQty` from the ledger (never a copy of the prior
  snapshot) → aggregate `InQty` / `OutQty` / `AdjustNetQty` → aggregate `PostLegs` and derive
  `AnchorClosing` from the pile → decide the case: **first close** solves `OpeningAdjustQty` from
  `AnchorClosing − LedgerClosing` and sets `ClosingQty = AnchorClosing`; **any later close** forces
  `OpeningAdjustQty = 0` and sets `ClosingQty = LedgerClosing` → compute `ClosingValue` exactly as
  "Formulas and invariants" defines → evaluate `CarryForwardOk` per slice → evaluate `CurrentBalanceDelta`
  when D11 applies (recording `CurrentBalanceCheckApplies = 0` when `PostLegs ≠ 0`) → **refuse** on any
  carry-forward mismatch, any non-zero plug after the first close, or any non-zero `CurrentBalanceDelta` in
  the applicable case → write lines + header with the mismatch counters, `Status = CLOSED`, stamps.
  The refusals must be domain messages naming the offending slices (bounded list, with a total count), not
  a single generic failure — the operator has to know which slice to investigate.
- **2c. Reconciliation overload.** Add `ReconcileAsync(string menuCode, string? iCode, string? whCode,
  CancellationToken)` to `IIvInventoryReconciliationService` with its own `KnownMenus` set
  (`InventoryReconciliation` + `InventoryPeriodClose`), and keep the existing 3-arg signature delegating to
  it — the `IvStockSummaryService` / `IvStockCountService.Variance` own-menu precedent. This keeps the
  existing 7 reconciliation tests untouched.
- **2d. Reopen** (`ReopenAsync`): require `REOPEN` + a non-empty reason + `RowVersion`; refuse while a later
  period is `CLOSED`; stamp `LastReopenLineCount`/`LastReopenClosingValue` **before** deleting the lines so
  the shape of what was withdrawn survives.
  - **Documented audit consequence (review item 8).** The close → reopen → mutate → re-close cycle leaves
    the original per-slice snapshot unrecoverable: only the count and the total closing value of what was
    withdrawn survive. This is accepted for v1 because the period is a re-derivable view of
    `IvTrxHistory`, but it must be written into the doc and onto the reopen confirm dialog verbatim, and
    a full close/reopen history table is recorded as an explicit follow-up (Further considerations).
- **2e. Tests** — `ErpWeb.Tests/IvPeriodCloseServiceTests.cs`: sequential rule with the missing month named
  in the message; overlap refused; first close accepts any start; carry-forward equality across two closes;
  first close records the plug and the second refuses a non-zero plug; a `NEW` batch blocks and is listed;
  `MISMATCH` blocks but `UNEXPECTED_BALANCE` does not; reopen clears lines and re-close regenerates;
  reopen refused out of order; own-menu gating (holding `INV_PERIOD_CLOSE` must not read the inquiry and
  vice versa); tenant isolation. Plus the invariant tests the formula section implies: on a **first** close
  the identity `ClosingQty = OpeningQty + OpeningAdjustQty + InQty − OutQty` holds for every row, while on
  every **later** close `OpeningAdjustQty = 0` and `ClosingQty = OpeningQty + InQty − OutQty` with
  `AnchorClosing == LedgerClosing`; 4-dp
  rounding is applied on both sides of every comparison; a slice that closes at 0 is absent from the rows
  (D13) yet its `CarryForwardOk` on the *next* period is still evaluated as prior closing = 0; `D11`
  refuses on a fabricated pile/history divergence, and records `CurrentBalanceCheckApplies = 0` instead of
  passing when movements exist after `PeriodTo`. Plus the two first-close plug cases: a pile with **no**
  ledger produces a **positive** plug equal to its quantity, and the mirror case (ledger over-states the
  pile) produces a **negative** plug that is stored unclamped rather than clamped to zero — with a
  second close then refusing, proving the plug is a first-close-only instrument.
  `IvPeriodCloseSqlServerConcurrencyTests` (`SqlServer` trait) covers the duplicate-close race and asserts
  the loser receives a **domain message** ("Period 2026-08 for HQ has already been closed."), never a raw
  `SqlException` — the unique index remains the last-resort net, not the user-facing error path. Needs a
  manually stamped `RowVersion` (the SQLite `ValueGenerated.Never` trap).

### Phase 3 — Action page (*depends on 2b, 2d*)

- **3a.** `MenuCodes.InventoryPeriodClose = "INV_PERIOD_CLOSE"` with the house doc-comment; the
  `ErpWeb/Menus/menus.xml` row beside Stock Count (SortOrder from Phase 0); `scripts/init-inv-period-close-menu.sql`
  seeding `ACCESS` + `CLOSE` + `REOPEN` on `dbo.MenuPermission` (no `dbo.Permission` MERGE needed — all
  three are built-ins), idempotent, with the verification SELECT filter naming **exactly** the three
  permissions its PRINT claims.
- **3b.** `IvPeriodClose.razor(.cs)(.css)` beside `IvStockCount`: `iv-*` chrome, `MenuAuthorize`, the next
  closable period pre-filled from the last close, a preconditions panel (blocking `NEW` batches, blocking
  reconciliation findings, carry-forward status), a CLOSE button gated by `PermissionCodes.Close`, a
  confirm dialog, and a closed-periods grid with a REOPEN action gated by `PermissionCodes.Reopen`
  requiring a reason. Stamps via `ICurrentDateService`. **Never hardcode `DateTime.Today` in the service.**
- **3c.** `TestCategories.InventoryPeriodClose`; the behavioural assertions for the page's preconditions
  live in `IvPeriodCloseServiceTests`, since pages are not render-tested in this repo.

### Phase 4 — Stored-closing-balance inquiry page (*depends on 2b*)

- **4a.** `MenuCodes.InventoryPeriodCloseInq = "INV_PERIOD_CLOSE_INQ"`, a `menus.xml` row under
  `INV_INQUIRY` at **SortOrder 10** (verified free), and `scripts/init-inv-period-close-inq-menu.sql`
  granting `ACCESS` + `EXPORT` + `VIEW_PRICE` (reusing the idempotent `MERGE dbo.Permission` for
  `VIEW_PRICE` from `init-inv-stock-value-menu.sql`).
- **4b.** `ErpWeb.UI/Inventory/Inquiry/IvPeriodCloseInquiry.razor(.cs)(.css)`: period picker (closed
  periods only, company+branch scoped), a per-slice grid over `IvPeriodCloseBal` showing Slice, Opening,
  Opening adjust, In, Out, Adjust net, Closing, UOM, Closing value (omitted — never blanked — when
  `VIEW_PRICE` is absent, with the house "columns are hidden" banner), plus header chips/cards for the
  period totals, line count, `SkippedZeroSlices`, `OpeningAdjustSlices`, `UnpostedBatchCount`,
  `ReconcileFindingCount`, `ReopenCount`, and a **carry-forward status** badge driven by `CarryForwardOk`.
  The page must also carry the D13 operator note in the markup, not only in the doc — the wording the review
  asked for: *"Zero-closing slices are intentionally excluded. Historical movement remains available through
  Transaction Inquiry / Stock Card. This screen represents ending stock, not a complete movement ledger."*
  A second note appears when `CurrentBalanceCheckApplies = 0` (a back-period close), stating that the
  pile-reconciliation check did not run.
  Grid served by `IvListGridDataSource<T>` (or `IvStockSummaryGridDataSource` if the row count warrants
  server paging) with `UseBuiltInExport="false"` + the page's own export endpoint, and
  `..AuditColumns.For(n)` appended.
- **4c.** `ErpWeb/Inventory/IvPeriodCloseExportEndpoints.cs`, mapped in `ErpWeb/Program.cs` beside the other
  `MapIv*ExportEndpoints` calls, with `EXPORT` re-checked server-side.

### Phase 5 — Governance, detection and docs (*depends on 4b*)

- **5a.** Add detection finding codes to `IIvInventoryReconciliationService`: `ROLLBACK_AFTER_CLOSE`
  (a batch with `RollbackCount > 0` whose `RollbackDate` is after its period's close), `BACKDATED_POSTING`
  (a history leg whose `TrxDtTime` sits in a closed period but whose `Created` is later), and
  `CARRY_FORWARD_MISMATCH`. Non-blocking, so they never spawn a new class of dead-end.
- **5b.** `ErpWeb/docs/inventory-period-close.md` (house structure: headings, decision IDs D1–**D14**, a
  "Locked decisions" section, the formulas and invariants verbatim with their worked boundary examples, a
  "Implemented in ErpWeb" style status block, EF Core translation notes, and a Verification section). It
  must also state plainly that `IvPeriodCloseBal` is **ending stock, not a movement ledger** (D13), that
  a reopen discards the prior snapshot contents (item 8), and that Sales/Procurement are gated only on
  **stock-driving** dates (D14) — QT/SO/PR/PO/`PoInvoice` stay open (D10). Add a cross-reference paragraph to
  `ErpWeb/docs/inventory-inquiry-suite.md` (the Reconciliation section) recording that the first close
  supplies the opening-balance baseline.
- **5c.** Update `plans/POCNDN-plan.md` C12 and `plans/plan-procurementKpiFoundation.prompt.md:118`, which
  currently state that no period-close infrastructure exists — they are now stale.

## Relevant files

**New — model/config**
- `ErpWeb.Model/Entities/Inventory/IvPeriodCloseHdr.cs`, `.../IvPeriodCloseBal.cs`
- `ErpWeb.Model/Configurations/Inventory/IvPeriodCloseHdrConfiguration.cs`, `.../IvPeriodCloseBalConfiguration.cs`
- `ErpWeb.Model/Repositories/Inventory/IvPeriodCloseResults.cs` (line DTO + query + key types, keeping `IvStockSliceKey` as the slice authority)

**New — core**
- `ErpWeb.Core/Inventory/IvPeriodCloseStatuses.cs` (+ `IvPeriodCloseLimits`)
- `ErpWeb.Core/Inventory/IvPeriodCloseGuard.cs` (internal static — the enforcement)
- `ErpWeb.Core/Inventory/IIvPeriodCloseService.cs`, `IvPeriodCloseService.cs`,
  `IvPeriodCloseService.Snapshot.cs` (partial, mirroring `IvStockCountService.Variance.cs`)

**New — UI / host / scripts / tests / docs**
- `ErpWeb.UI/Inventory/<same folder as IvStockCount>/IvPeriodClose.razor(.cs)(.css)`
- `ErpWeb.UI/Inventory/Inquiry/IvPeriodCloseInquiry.razor(.cs)(.css)`
- `ErpWeb/Inventory/IvPeriodCloseExportEndpoints.cs`
- `scripts/create-iv-period-close.sql`, `scripts/init-inv-period-close-menu.sql`, `scripts/init-inv-period-close-inq-menu.sql`
- `ErpWeb.Tests/IvPeriodCloseGuardTests.cs`, `IvPeriodCloseServiceTests.cs`, `IvPeriodCloseSqlServerConcurrencyTests.cs`
- `ErpWeb/docs/inventory-period-close.md`

**Modified**
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs` — the **ten** guard call sites, five POST + five ROLLBACK (Phase 1d)
- Stock-driving document services (Phase 1h / D14) — create/update gates:
  `IvMiscReceiptService`, `IvGoodsReceiptService`, `IvMiscIssueService`, `IvScrapService`,
  `IvStockAdjustmentService`, `IvStockCountService`, `IvStockReturnService`, `IvStockTransferService`,
  `IvVendorReturnService`, `IvSpShipmentService` (batch create/`TrxDtTime` only),
  `SaDoService`, `SaInvoiceService`, `SaCdnService`, `PoCdnService`
- `ErpWeb.Core/Inventory/IvInventoryReconciliationService.cs` — `menuCode` overload + `KnownMenus` (Phase 2c)
- `ErpWeb.Model/Data/AppDbContext.cs` — two DbSets beside `IvTrxHistories`
- `ErpWeb.Core/CoreServiceCollectionExtensions.cs` — register `IIvPeriodCloseService` after `IIvStockSummaryService` (L159)
- `ErpWeb.Core/Menus/MenuCodes.cs` — two constants with house doc-comments
- `ErpWeb/Menus/menus.xml` — two rows (**required**, or `MenuSyncService` soft-disables them and `MenuAuthorize` redirects to `/unauthorized`)
- `ErpWeb.Tests/TestCategories.cs` — one constant
- `ErpWeb/Program.cs` — `MapIvPeriodCloseExportEndpoints()`
- `plans/POCNDN-plan.md` (C12), `plans/plan-procurementKpiFoundation.prompt.md` (L118)

## Verification

1. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category!=SqlServer"` — expect
   **2221 + new total / the same documented 15 failures / 0 skipped** (9 `SaCustServiceTests`,
   4 `PoSupplierServiceTests`, 2 library `InvoiceTypeCode`).
2. `dotnet test ... --filter "Category=InventoryPeriodClose"` and `--filter "Category=Inventory&Category!=SqlServer"`.
3. SQL Server race tests: set `ConnectionStrings__SqlServerTestConnection` (scratch DB whose name contains
   "test", e.g. `ERPWeb_PeriodCloseTest`) **and** `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` in a **separate
   terminal command** (a `;`-bearing value on the `dotnet test` line is silently truncated), then run
   `dotnet test ... --filter "Category=SqlServer"` alone and confirm **`Skipped: 0`** — that is the only
   proof the SQL tests actually executed.
4. `dotnet build ErpWeb.slnx --nologo -v:q` (mandatory after touching `ErpWeb.UI` — `dotnet test` does not
   compile `.razor`); use `dotnet build ErpWeb/ErpWeb.csproj -t:Compile` while the app is running.
5. `sqlcmd -E -d ERPWeb -i scripts/create-iv-period-close.sql` **twice** (second run a clean no-op), plus
   twice on scratch `ERPWeb_PeriodCloseDdlTest`. Confirm 2 tables / 4 indexes / 1 FK.
6. `sqlcmd -E -d ERPWeb -i scripts/init-inv-period-close-menu.sql` twice and the `-inq` sibling twice; the
   verification SELECT must list exactly the permissions each PRINT claims (3 and 3).
7. **Manual smoke sequence** (needs a browser): close a period → attempt a back-dated MI post (must be
   refused naming the closed date) → attempt **saving** a DO / sales invoice / PO CDN dated inside the
   closed period (must be refused — D14) → confirm a Sales Order / Purchase Order / Purchase Invoice with
   the same date still saves (D10) → attempt a rollback of a batch inside the period (refused) → open the
   inquiry page and read the stored opening/closing and the carry-forward badge → reopen with a reason →
   confirm the lines are gone and the DO save + MI post are now accepted → re-close and confirm the lines
   are regenerated.
8. **Performance acceptance:** time `CloseAsync` on a period with a realistic movement volume; record the
   numbers in the doc. The `< PeriodFrom` opening replay is the expensive half. **Do not weaken the v1
   correctness model for performance** — the full replay *is* the self-audit, and it stays. A later design
   may use the prior close as the normal opening source and run the full replay periodically as an audit
   operation, but that is a separate, separately-justified change.
9. A ROLE needs a `dbo.RoleMenuPermission` row (`IsAllowed`, **not** `IsActive`) before any user sees either
   menu — the deployment owner's step, deliberately not scripted.

## Scope boundaries (explicitly excluded)

- **No purge of any system of record.** `IvTrxBatch` / `IvTrxBatchDetail` / `IvTrxHistory` / `IvBalLoc` /
  `IvPeriodCloseHdr` are never deleted by this feature. The legacy purge existed for storage pressure in a
  Btrieve/SQL-6.5 era, and here those tables are load-bearing for rollback, reconciliation, Stock Card
  openings, reference-integrity guards and cost. The **single** deletion is of the feature's own derived
  `IvPeriodCloseBal` rows on reopen — see **The one deletion** above for the full table and rationale.
- **No reversal documents** in v1 (D2); the `RollbackMode` seam is *not* pre-built — it is a Phase-6 option.
- **No `CloseNo`** in v1 (D5).
- **No per-`TrxType` movement matrix** in v1 — `AdjustNetQty` is a subset view of `In`/`Out`, and a full
  breakdown is a later enhancement.
- **No full close/reopen history log table** — only `ReopenCount` + the last reopen stamps (the
  `IvTrxBatch`-style precedent). Consequence, accepted and documented: reopening discards the prior
  per-slice snapshot, keeping only `LastReopenLineCount` / `LastReopenClosingValue` as evidence of its
  shape. A future audit-history table is the recorded follow-up if full close/reopen provenance is ever
  required.
- **No GL posting, no valuation close, no non-stock document gating** — those arrive with the AP/GL phase.
- **No change to `IvStockCountLimits.MaxBackdateDays`**; the period guard is additional to it.
- **No caching of the closed-through date.** It is a single indexed `MAX()` per post; caching a lock state
  is a correctness risk, not an optimisation.

## Further considerations

1. **Where should the close page live** — under `INVENTORY` beside Stock Count (recommended, it is an
   action), or under a new `INVENTORY` → "Period End" group if more close-like screens are expected
   (GL period close, sales period close)? If the AP/GL phase is coming, a **parent menu now** avoids
   re-parenting later.
2. **Should the inquiry page be one page or two** — a single page with a "headers" grid and a "balances"
   child grid (recommended: one menu, one grant, matching how `IvStockCountVariance` fronts a summary plus
   rows), or separate `Period Close` and `Period Balances` menus?
3. **Should `UNEXPECTED_BALANCE` be *suppressed* once an opening baseline exists** (i.e. only reported for
   periods before the first close)? Recommended yes in a later phase, because after the first close the
   absence of history is expected and the finding becomes permanent noise on the Reconciliation page.
4. **Close/reopen provenance table (review item 8 follow-up).** If full close/reopen history is ever
   required, the shape is a third table `IvPeriodCloseLog` (PeriodCloseId, Action CLOSE/REOPEN, stamps,
   reason, LineCount, TotalClosingValue) written on every transition, plus retaining the superseded
   `IvPeriodCloseBal` rows keyed by a `CloseSequence` so the inquiry can show "as reported at close 1" and
   "as reported at close 2". Deliberately out of v1 — it is only worth its cost once someone actually needs
   to prove what a report said before a reopen.
5. **Should the operator be warned when a close cannot be balance-verified?** Closing a back period is
   legitimate (D6 only requires contiguity), but D11's pile comparison is then not applicable, so the close
   is verified by carry-forward and ledger replay alone. Recommended: a prominent, non-blocking banner on
   the confirm dialog and a `CurrentBalanceCheckApplies = 0` chip on the inquiry header, so nobody later
   assumes the strongest check ran when it did not.

---

## Approval

The architecture, schema (subject to Phase 0 items 2–3 choosing the final menu codes and SortOrder), guard
design (posting **and** D14 document-date), formulas, invariants and the decision set **D1–D14** are
complete and consistent. Nothing is left to the implementer's invention: every formula has a fixed
evaluation order and a worked example, every check has a domain and a failure message, and every path has
a test.

To approve, the owner signs off on three things:

1. **Decisions D5–D10** — currently marked "recommended, please confirm" — and accepts **D11–D14** as
   incorporated (D11–D13 from review; **D14** from owner confirmation that stock-driving Sales/PO saves
   must refuse closed-period dates).
2. **Phase 0 items 1–6** — five one-line facts and one command run; none blocks Phase 1 modelling.
3. **The gate, restated:** item 7 is ANSWERED ("Critical finding 2"); item 8 is the **Phase 1 → 2 exit**
   because it needs the tables to prove. **Phase 1 begins on owner approval; Phase 2 begins once the item-8
   seeded proof is recorded.**

Once those three are signed, the plan is ready for implementation and no further review pass is required.

---

## Appendix A — Review record (received 2026-09-25, verbatim)

Preserved as received. Heading levels are demoted by two so the review nests under this appendix; the text
is otherwise unaltered. The disposition of each finding is in **Review disposition** and **Review history**
above — this appendix is the record, not the resolution.

#### Review: Inventory Period Close Plan

##### Overall Assessment

**Architecture: 9/10**
**Implementation readiness: approximately 8/10**

The plan is strong and unusually detailed. It treats Inventory Period Close as a control mechanism rather
than merely a reporting snapshot.

The core architecture is sound:

- `IvPeriodCloseHdr` + `IvPeriodCloseBal`
- Posting/rollback period guard
- Historical replay from `IvTrxHistory`
- Carry-forward validation
- First-close opening baseline
- Reconciliation preconditions
- Close/reopen workflow
- Stored closing-balance inquiry
- Anti-drift tests
- SQL Server concurrency testing

The plan's strongest principle is that the period-close snapshot must not become a second source of truth:
`IvTrxHistory` remains the historical source, while `IvPeriodCloseBal` is a controlled period-end snapshot.

##### Critical Findings Before Implementation

###### 1. "Nine insertion points" is incorrect

The plan says there are nine guarded `*CoreAsync` / self-contained methods, but the actual list contains:

**POST — 5**

- `PostInventoryMRCoreAsync`
- `PostNonStockGoodsReceiptCoreAsync`
- `PostInventoryADJCoreAsync`
- `PostInventoryMICoreAsync`
- `PostInventoryTRAsync`

**ROLLBACK — 5**

- `RollBackInventoryMRCoreAsync`
- `RollBackNonStockGoodsReceiptCoreAsync`
- `RollBackInventoryADJAsync`
- `RollBackInventoryMICoreAsync`
- `RollBackInventoryTRAsync`

That is **10 insertion points**, not 9.

**Recommended change** — Update the plan to state: "Ten insertion points: five POST and five ROLLBACK
paths." The anti-drift sweep should also expect all 10 guarded paths.

###### 2. Rollback semantics must be verified before implementation

This is the most important technical risk.

The plan's guard compares `trxDtTime.Date` against the closed-through date. That is straightforward for a
normal posting (transaction date 2026-08-15, August closed ⇒ reject). But rollback can be different:

```
August 20     MR posted, Qty +100
September 5   User attempts to rollback the August 20 MR
```

The implementation must verify whether the rollback method uses the original batch transaction date or the
rollback execution date. If rollback is evaluated using September 5, the operation could potentially bypass
the August period guard while modifying August history.

**Required Phase 0 addition** — For every rollback method determine: which `IvTrxHistory` records are
changed/deleted; which `IvBalLoc.StdQty` records are changed; whether the original transaction date is
preserved; whether rollback of a later operation can affect a closed period; and whether rollback physically
deletes history or creates compensating history. The guard must protect the **affected historical period**,
not merely the date on which the rollback is executed.

###### 3. Define the exact `OpeningAdjustQty` formula

The plan correctly says the first close establishes the opening baseline and absorbs an unexpected balance
into `OpeningAdjustQty`. However, the exact formula is not explicit enough for an AI implementation agent.
A likely intended relationship is:

```
OpeningAdjustQty = ActualBalanceAtClose − IndependentLedgerCalculatedBalanceAtClose
```

But the plan should explicitly define: what is the source of `ActualBalanceAtClose`; is it
`IvBalLoc.StdQty`; how is rounding handled; is the comparison performed per stock slice; and what happens if
the calculated difference is negative. This should be locked before schema/code implementation.

###### 4. Add an explicit current-balance reconciliation

The plan validates `Previous Closing` vs `New Opening` and also independently replays historical movements.
A third invariant is recommended: `Calculated Closing Qty` vs `IvBalLoc.StdQty`.

```
Calculated Closing = 100, IvBalLoc.StdQty = 100  => OK
Calculated Closing = 100, IvBalLoc.StdQty =  95  => BLOCK CLOSE
```

**Recommended new decision — D11 — Closing balance must reconcile:** Before closing, calculated closing
quantity must equal the corresponding current `IvBalLoc.StdQty` for each stock slice, subject to the
documented 4-decimal rounding rules.

##### Important Findings

###### 5. Clarify D3: zero-closing slices

The plan intentionally stores only stock slices with non-zero closing quantity. This is reasonable for a
closing-balance snapshot, but it means the table is **not a complete period movement snapshot**:

```
Opening = 100, In = 50, Out = 150, Closing = 0
```

That slice will not exist in `IvPeriodCloseBal`. This should be explicitly documented: *"Zero-closing
slices are intentionally excluded from `IvPeriodCloseBal`. Historical movement remains available through
`IvTrxHistory`. The period-close inquiry represents ending stock, not a complete movement ledger."* This will
prevent future developers from interpreting `IvPeriodCloseBal` as a complete audit ledger.

###### 6. Lock the period-date rule

Make the invariant explicit: **Period membership = `Date(TrxDtTime)`.**

```
PeriodTo = 2026-08-31
  TrxDtTime = 2026-08-31 23:59:59  => August
  TrxDtTime = 2026-09-01 00:00:00  => September
```

Also explicitly define the relationship among `PeriodFrom`, `PeriodTo`, `TrxDtTime` and
`ICurrentDateService.Today`.

###### 7. Define `CarryForwardOk`

**Recommended definition:** `CarryForwardOk` is true when the opening quantity for every applicable slice
matches the previous closed period's closing quantity after the documented 4-dp normalization. This should
not be confused with the independent ledger replay check.

###### 8. Improve reopen auditability

Deleting the lines on reopen and keeping only `ReopenCount` / `ReopenedBy` / `ReopenedOn` / `ReopenReason` /
`LastReopenLineCount` / `LastReopenClosingValue` is acceptable for v1, but there is an audit consequence:

```
Close August  ->  Reopen  ->  Modify transactions  ->  Close August again
```

The original snapshot contents disappear. For v1 this can remain as designed, but document that a future
audit-history table may be required if full close/reopen history becomes a requirement. At minimum, consider
preserving the original close metadata if the existing audit model permits it.

###### 9. Concurrency handling should return a domain error

Expected behaviour: User A closes August ⇒ SUCCESS; User B closes August ⇒ controlled failure with a
domain-level message such as *"Period 2026-08 for HQ has already been closed."* rather than a raw SQL
duplicate-key exception. The database unique constraint should remain the final safety net.

##### Findings To Keep As-Is

- **D4 — NEW batches block close but remain deletable.** A good operational decision: it avoids the
dead-end where a draft cannot post and cannot be deleted, trapping the system.
- **Reconciliation blocking vs advisory findings.** Making `UNEXPECTED_BALANCE` advisory is consistent with
the first-close baseline design. Do not make it blocking unless the baseline model is changed.
- **Guard location.** Putting the guard into the posting/rollback core methods instead of only
  `DispatchAsync` is correct, because several transaction paths bypass `DispatchAsync`. The anti-drift
  behavioural sweep is particularly valuable.

##### Recommended Anti-Drift Test Enhancement

The anti-drift test should not only verify the known methods; it should make it difficult for future
stock-affecting methods to bypass the period guard. The intended invariant is: every stock-affecting
operation reaches a guarded posting/rollback path and therefore `IvPeriodCloseGuard`. This matters because a
future developer could add a new inventory posting method that directly changes `IvBalLoc` or `IvTrxHistory`
without passing through an existing guarded method.

##### Performance

The full historical replay should **not** be optimised away in v1 — it provides an important self-audit
(previous close vs historical ledger replay vs current balance). A later design could use the previous close
as the normal opening source and perform the full replay periodically as an audit operation. Do not weaken
the v1 correctness model merely for performance.

##### Recommended Revised Phase 0

Before schema freeze, expand Phase 0 to eight verification spikes: (1) `IvTrxBatch.TrxDtTime` creation
behaviour at all relevant creation sites; (2) `Menu.MenuCode` column width; (3) the appropriate `SortOrder`;
(4) the `IvStockCount` UI folder; (5) every `IvBalLoc.StdQty` mutation has a corresponding `IvTrxHistory`
leg; (6) re-measure the existing green baseline; (7) **rollback mutation analysis for all five rollback
paths**; (8) **prove current-balance reconciliation** between replayed calculated closing quantity and
`IvBalLoc.StdQty`. Do not freeze the schema until items 7 and 8 are answered.

##### Recommended Revised Decision List

Keep D1–D10, with these additions: **D11 — Closing balance reconciliation** (calculated closing quantity
must equal current `IvBalLoc.StdQty` for each applicable stock slice, after documented 4-dp normalization);
**D12 — Period membership** (a transaction belongs to a period based on the company-local date component of
its authoritative transaction datetime, `Date(TrxDtTime)`); **D13 — Zero-closing slices** (`IvPeriodCloseBal`
stores only non-zero ending stock slices; it is a closing-balance snapshot, not a complete movement ledger).

##### Final Recommendation

**Approve the overall architecture, but do not start Phase 1 implementation yet.** The four items to resolve
first are: (1) correct 9 → 10 guarded paths; (2) prove rollback cannot modify a closed period through a
later-dated rollback operation; (3) define the exact `OpeningAdjustQty` formula; (4) add and prove calculated
closing quantity vs `IvBalLoc.StdQty` reconciliation. Once those are answered, the plan is sufficiently
structured for an AI coding agent to implement in phases without having to invent important business rules.

##### Suggested instruction to the coding agent (as received)

> Review and revise the existing Inventory Period Close plan based on this review. Do not implement code
> yet. Resolve all Critical Findings first, especially rollback semantics, OpeningAdjustQty calculation,
> current-balance reconciliation, and the 10-vs-9 guarded-path discrepancy. Update the plan with explicit
> formulas, invariants, tests, and decision IDs. Then stop and wait for approval.

---

## Appendix B — Specification-level feedback (REV 4, verbatim)

Received 2026-09-25 as prose, preserved as written. Judged **not** architectural problems — five
specification-level gaps. Dispositions are in the REV 4 table under **Review disposition**.

> There are still a few **specification-level gaps**, not architectural problems:
>
> 1. Exact `OpeningAdjustQty` formula needs to be unambiguous.
> 2. `IvBalLoc.StdQty` reconciliation should explicitly define the union of slices.
> 3. Rollback-after-close needs a dedicated acceptance test.
> 4. `In` / `Out` / `AdjustNetQty` classification needs to be explicitly mapped.
> 5. The "nothing is ever deleted" wording conflicts with deleting snapshot lines on reopen.
>
> => my feedback on the plan, please update back u review into the same plan
