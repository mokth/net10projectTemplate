# Stock Balance Control — mandatory cross-module reference (v1.1)

## Purpose and authority
This reference governs physical on-hand, reserved/available, in-transit and production WIP quantity integrity alongside costing. **Never infer the exact table names, sign conventions, negative-stock policy, reservation semantics or UOM precision from this file**: inspect the current `productionv2` implementation and its tests first. In particular, inspect `IvBalLoc`, inventory histories, the V2 physical timeline, `CostLayer`, Production pool movements, shipment ownership and stock-count lineage. Existing explicit business rules are authoritative until an approved change.

## Mandatory quantity identities
For each **trusted company + branch + stock item + warehouse + location + lot + stock status + base UOM** (and any additional keys in the current schema):

1. `EndingOnHand = OpeningOnHand + PostedReceipts - PostedIssues + PostedAdjustments + TransferIn - TransferOut + SupportedReversals`, at a defined business-effective cut-off. Never mix posting time with effective business time.
2. At item/company/branch level, an internal transfer is **net zero**; its source decrement and destination increment are one atomic operation. Check cross-warehouse and cross-location movements and distinct lot/status slices separately.
3. `AvailableToPromise = OnHand - ActiveReservations - OtherCurrentPolicyHolds` only if the repository actually implements that policy. Reservations are commitments, **not physical issues**. Drafts must not silently reduce physical stock.
4. `QuantityInBaseUOM = EnteredQuantity × EffectiveApprovedConversion` using the repository's direction, precision and rounding rules. Never mix entered UOM with `StdQty`; forbid unapproved conversion changes that silently revalue or move stock.
5. For FIFO, reconcile physical on-hand against the active eligible layer balances at the same cut-off and key granularity **where the repository's layer design supports that equivalence**. Account explicitly for transfers, returns, adjustments, reversal generations and legitimate value-only facts. Value-only adjustments change **zero physical quantity**.
6. For Production, separately reconcile Inventory RM decrement, Production material-in quantity, WIP input/consumption, output, scrap, return, adjustment, transfer OUT/IN and FG Inventory receipt. Never equate unlike units (e.g., RM kg versus FG pieces) or assert 1:1 quantities where BOM/yield/conversion applies. Verify both material and finished-product UOM, scrap/yield and operation-specific rules.
7. Stock count is an **absolute observation at its effective date**, not an unconditional live-balance overwrite. Preserve the documented count end-of-day chronology, historical tail, active generations, and all later transactions. Zero-variance counts still need their correct audit/lineage treatment when supported.

## Posting and rollback control points
- **New/Edit:** preserve draft status, identity, precision, UOM and validation; allow future drafts only where current rules permit. Never reserve, consume or value physical stock by saving a draft unless the repository explicitly defines a separate reservation action.
- **Post:** verify access, tenant/branch, period, business date, stock control eligibility, slice identity, sufficient eligible physical/available stock according to current policy, current reservations, UOM conversion, duplicate posting, dependency status and cost authority. Revalidate stock and dependent records **under the same authoritative SQL transaction and lock**; no check-then-write race.
- **Partial flows:** handle partially received PO/GR, partially delivered SO/DO, partially billed DO, partial return, split FIFO consumption, multi-line/multi-warehouse documents, partial Production material issue and WIP consumption. Quantities must be bounded by the eligible remaining amount at the right document line and business date. Do not assume a document has only one line.
- **Rollback/Reversal:** verify downstream stock/cost/production dependencies, active generation, original owner, closed periods and later movements; reverse exact active physical and financial effects, once, in one root. If replay is unsupported, fail before any mutation. Preserve history, allocations and original source identities.
- **Failure/Retry:** duplicate request, cancellation, timeout, stale snapshot, serialization failure, invalid UOM, blocked period, negative slice and missing cost authority must leave no partial inventory, production, procurement, sales, reservation or valuation mutation. Retry only a verified safe idempotent transaction, never blindly repeat an uncertain commit.

## Transaction-by-transaction checks
| Route | Physical control | Cross-check / forbidden mistake |
|---|---|---|
| Inventory Misc Receipt / GRN | Increase only eligible receiving slices on POST | Match receipt history and valuation; PO received quantity only once |
| Inventory Misc Issue / Scrap / Vendor Return | Decrease exact source slice on POST | Sufficient as-of quantity, cost consumption, no duplicate deduction |
| Transfer | Decrease source + increase destination atomically | Net item zero; correct lots, statuses, UOM and FIFO cost ownership |
| Stock Adjustment | Signed delta on intended slice | Correct sign, business date, value, permissions, reversal |
| Stock Count | Absolute observed quantity at count cut-off | Later transactions retained; historical reset/lineage and count-to-zero |
| Production Material Issue | RM Inventory out + Production material-in | Match posted source and production link; no double material consumption |
| Production Daily Output | Consume eligible WIP / record output per actual business rule | Check yield, scrap, partial operations and pool cost; no invented 1:1 conversion |
| Production Return | Return only eligible previously issued RM to Inventory | Source issue quantity/cost remaining and exact lineage |
| Production Adjustment / WO Transfer | Correct WIP pool/operation movements | OUT/IN symmetry where applicable; never create inventory stock accidentally |
| Production FG Receipt | Eligible FG Inventory in + matching WIP reduction | Correct FG UOM, quantity, completed operations and capitalized cost |
| Sales DO / Direct Invoice | One shipment owner issues stock exactly once | Linked invoice does not issue again; partial billing and multi-DO cases |
| Sales stock-return CN | Receive only verified returned quantity | Tie to original shipped line and active cost allocation; financial-only CN no stock |
| Purchase Invoice / CN/DN | Generally commercial/cost settlement unless actual route owns stock | Never invent physical movement; distinguish return from price adjustment |

## Adversarial test matrix (required for impacted routes)
- Zero stock, exact stock, one unit short, negative attempt, multiple slices, multiple lots, blocked/quarantine status, fractional quantity, UOM conversion and precision boundary.
- Two concurrent posts competing for last eligible stock; concurrent post versus rollback; repeated identical POST; timeout after DB commit; stale rowversion/watermark.
- Earlier-dated post followed by later issue, transfer, Sales shipment, Production issue, WIP consumption, FG receipt, and returns; both rollback orders; changed FIFO split and changed active valuation generation.
- Same-day event ordering, future-dated draft, future POST refusal, closed period, month-end snapshot and count at end-of-day.
- Multi-line partial transactions, partial returns, two warehouses, mixed stock-controlled/non-stock-controlled items, mixed cost methods if supported.
- Reconcile opening + posted movement to ending by slice; sum across slices to item; match physical V2 active timeline to legacy balances where the repository promises consistency; match FIFO remaining layer quantities and WIP balances independently; assert **no state mutation on rejected requests**.
- SQL Server integration for lock ordering, isolation, unique constraints, pooled SQL session cleanup and concurrent writers. SQLite-only success is not certification.

## Required evidence before sign-off
Produce a table of **every affected public Post/Rollback route** with: transaction owner, physical movement key, source and destination, quantity/UOM, active history/generation, cost impact, downstream consumers, period/future guards, permissions, lock/transaction boundary, tests and unsupported cases. Include exact before/after stock slices, WIP and value totals from reproducible tests. Mark unsupported historical replay as **BLOCKED**, not completed. No release if any unexplained quantity, FIFO layer, WIP or value mismatch remains.
