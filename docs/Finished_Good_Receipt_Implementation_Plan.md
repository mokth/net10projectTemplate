Finished Good Receipt — Revised Implementation and Acceptance Plan

## 1. Summary and approval basis

Implement **Production → Finished Good Receipt** using Daily Production’s UI conventions. Each receipt transfers existing finished production stock into warehouse inventory while conserving quantity and exact value and preserving production genealogy.

**Target document:** `C:\wincom\net10projects\docs\Finished_Good_Receipt_Implementation_Plan.md`

**Review recommendation:** Approve this specification for implementation. Production deployment remains subject to the release gates below.

This replacement incorporates the original plan and resolves mixed-pool valuation, pooled dependencies, company-wide lot identity, document versioning, retry ordering and exact-value reporting.

Repository inspection confirms:

- Blazor baseline: `1af97876c807e38d429a24677e620798cb2bc8f2`.
- Production movements currently carry numeric costs but are stamped `UNVALUED`.
- The production stock writer updates quantities without maintaining balance value.
- Quantity contribution allocation infrastructure exists but requires integration.
- Inventory history reports currently reconstruct value from quantity and unit price.
- Inventory lots are unique by **company + item + lot**, across branches and warehouses.
- Inventory batch headers lack rowversion; the registered stock-freeze guard is a no-op.

The original plan reports nine passing tests and legacy baseline `1282097754401af2b36ba7a0ab46cc83574e6253`. Those results were not independently rerun during this review and do not establish release readiness.

## 2. Business rules and fixed defaults

- One Work Order per receipt; multiple source lots and destination lines.
- Quantity and UOM only; no independent weight.
- Carry verified material/WIP cost using pooled-average valuation. Labour, overhead, variance and GL integration are excluded.
- Lifecycle: **NEW → POSTED → REVERSED**. Correction creates a new linked draft; originals remain immutable.
- Drafts do not reserve stock. Posting validates availability again under locks.
- FG receipts affect warehouse-received quantities, not Work Order `GoodQty`, operation progress or completion.
- Transfer only the entered quantity. Do not clear remaining WIP automatically.
- Eligible stock must be available, stock-controlled final output from the final operation of the final FG route. Exclude intermediate handoffs, held/rejected stock and cancelled Work Orders.
- Completed or closed Work Orders may retain eligible stock.
- Source item and UOM are fixed. Users enter receipt quantity in source UOM; destination standard quantity is derived through frozen base-UOM conversion factors.
- Reject incompatible conversions, quantities rounding to zero and conversions that cannot conserve base quantity at supported precision.
- Destination stock status is `ACTIVE`.
- Destination lot defaults from the physical production lot and is editable in draft. Non-lot-controlled items use the existing empty-lot convention.
- Matching partial top-ups are permitted under the origin and expiry rules below.
- Posting uses the saved document effective date, defaults to the current business date/time and rejects future dates, closed periods and invalid chronology.
- Reversal uses the current business date/time in an open period, captured once on its first successful execution.
- Printing, labels, stock-count freeze management and automatic historical-cost migration remain deferred. The UI must not claim freeze enforcement.

## 3. Implementation changes

### Document model and service boundary

Reuse `IvTrxBatch` and `IvTrxBatchDetail` with `TrxType = FG`.

Add:

- A one-to-one FG header extension containing Work Order, SQL rowversion, document revision, posting/reversal references and corrected-document linkage.
- One FG source row per inventory detail, identified by surrogate IDs and referencing `ProductionBalLotId`, requested quantity and frozen source/destination UOM contracts.
- Immutable posting facts linking each line to its production movement, inventory history, posting generation, exact transferred value and destination identity.
- Immutable valuation provenance and pooled-dependency records, separate from quantity allocations.
- A one-to-one destination lot origin extension for FG-owned inventory lots.
- Nullable exact-value fields on inventory history: transferred total, valuation status and base quantity/UOM evidence. Existing non-FG history retains its current semantics.

Use restrictive foreign keys, tenant-scoped lookup indexes and unique constraints for document posting roles, request identities, reversal targets and posting facts. Quantities and transferred values use `decimal(18,4)`; conversion factors use `decimal(18,8)`. Follow `IvQty.Round`, which uses four decimals and midpoint rounding away from zero.

Register:

- Commands: `FG_RECEIPT_POST`, `FG_RECEIPT_ROLLBACK`.
- Document type: `FG_RECEIPT`.
- Production movements: existing `FG_RECEIPT_OUT` and new `FG_RECEIPT_REVERSAL`.
- FG terminal status: `REVERSED`.

Update movement constraints, direction helpers, reversal recognition, views, reconciliation and compatibility checks together.

Expose `IProductionFinishedGoodReceiptService` with paged receipt/source queries, document/readiness lookup, draft CRUD, post, rollback and correction-draft creation. Follow existing operation-result conventions.

All mutations derive tenant/user scope on the server. Client inputs cannot establish authoritative cost, valuation status or production origin.

### Versioning and request replay

Every draft mutation—including header-only, detail-only and source-only edits—must update the FG extension and increment its document revision in the same transaction. SQL rowversion therefore covers the entire document.

Update, delete, post and rollback require the expected rowversion. Generic inventory editing/posting/rollback paths must reject FG documents; only the FG service owns their lifecycle.

For post and rollback:

1. Authorize the caller and acquire the branch transaction lock.
2. Resolve the existing request by tenant, command and stable request ID.
3. For a sealed matching request, return the recorded outcome before evaluating current document state or rowversion.
4. Reject reuse of the request ID with different semantic input.
5. For a new request, validate rowversion, lifecycle and readiness.

The request fingerprint covers submitted document identity/version, command and rollback reason. Record the server-resolved posting snapshot separately. Generated timestamps and current master data must not change a retry’s fingerprint.

Store enough result information to return the original posting identity after a lost response. Replayed results remain subject to current access and cost-visibility permissions.

Unique posting-role and reversal-target constraints prevent different request IDs from performing the same operation twice.

### Cost provenance and mixed-pool readiness

Complete upstream readiness before enabling FG posting:

- Preserve missing prices as unknown through Issue to Production and Daily Production. Remove null-to-zero substitution from provenance decisions.
- Freeze cost, currency basis, price UOM, conversion factor and source evidence at posting.
- Use company base-currency value for transfer; perform no new FX conversion in FG.
- Verified zero requires explicit upstream evidence of a zero price. A defaulted zero is unknown.
- New inventory source postings feeding production must record whether their price was explicitly supplied or inherited from verified evidence. Preserve that evidence through transfers and IP.
- Existing numeric balance/history prices without trustworthy evidence remain unverified. Do not mark historical sealed movements verified retrospectively.
- Daily Production outputs are verified only when every consumed material/handoff contribution is verified. Missing provenance or unexplained zero-input output remains unvalued.
- Populate final-output dimensions explicitly, including FG staging, production location, status and physical lot identity.

Maintain a valuation-readiness projection per production pool:

- Adding verified output to a nonempty unvalued pool leaves the pool unvalued.
- FIFO exhaustion of an unknown contribution does not make the remaining pooled value verified.
- A new clean valuation generation begins only after both quantity and tracked value reach zero.
- Any residual value at zero quantity is a reconciliation failure.
- Reversal restores the original provenance; it cannot upgrade unknown cost to verified.

Persist FIFO quantity allocations and their reversals for production consumption, handoffs and FG. FIFO is genealogy only.

Separately persist pooled dependency edges from **every contributor in the current valuation generation** to each consuming movement, including contributors receiving no FIFO quantity allocation. Retain contributors through partial depletion. Connect consumed inputs to produced outputs so dependency checks can follow the chain through WIP.

Restoring stock through reversal restores its original dependency set and merges it with any existing pool dependency set. Historical edges remain immutable.

Block producer rollback whenever its quantity or pooled-value dependency chain reaches an active downstream production movement or FG receipt.

### Exact transfer and warehouse pricing

For each source balance, aggregate all requested base quantities before calculating value:

```text
Partial value = Round(remaining TotalCost × requested BaseQty / remaining BaseQty, 4)
Final depletion value = exact remaining TotalCost
```

Split the frozen value across its receipt lines proportionally in stable source-line ID order; assign the remainder to the final line.

Extend the production writer with explicit value-aware legs. It must update quantity, total cost, average cost and chronology once. Migrate participating upstream callers without double-applying their existing balance adjustments.

The FG inventory adapter must:

- Accept the frozen base quantity and exact transferred total.
- Append history and update destination quantity within the shared transaction.
- Store exact value independently of rounded unit prices.
- Aggregate multiple lines targeting the same destination slice before updating its pricing.
- Set the destination’s FG pricing snapshot from aggregate transferred value divided by aggregate destination standard quantity, using existing field precision/UOM conventions.
- Retain the destination’s prior pricing snapshot once per slice for reversal.

Update FG history, exports and reconciliation to read the stored exact total. Reverse history carries the same absolute total with the opposite direction. Never reconstruct FG totals from unit price or current purchase price.

Existing non-FG valuation formulas remain unchanged. Exact FG transfer value must not be presented as a new system-wide inventory valuation method.

### Destination lot origin, expiry and concurrency

For lot-controlled items, define production origin as:

**company + originating branch + Work Order ID + producing route-step ID + final operation ID + physical production lot**.

This permits top-ups from repeated output into the same physical production origin while rejecting unrelated Work Orders or production lots.

Store that identity in the FG lot extension. Keep receipt-specific references in posting facts; do not overwrite lot ownership on each top-up.

Rules:

- A new FG destination lot acquires its origin atomically.
- An existing lot is eligible only when its recorded FG origin matches.
- Existing lots with absent or conflicting origin evidence are blocked rather than silently adopted.
- Expiry is a date-only value. For an existing lot, blank input inherits its expiry; an explicit different value fails.
- All lines creating the same lot must agree on expiry.
- New expiry must satisfy existing item requirements and cannot precede the receipt date.
- Non-lot items create neither dummy lots nor lot-origin records; posting facts retain production genealogy.
- Reversal does not erase lot origin or expiry.

Lock lot identities at their actual company-wide key, **company + item + lot**, using indexed update/range locks, including missing-row creation. Acquire multiple keys in deterministic order. Retain the unique index as the final integrity constraint.

Branch stock locks alone are insufficient. Shared lot creation and metadata-writing paths must use the same company-wide locking protocol and must not overwrite FG-owned origin/expiry.

### Atomic posting, chronology and reversal

Use one `AppDbContext`, one SQL transaction and one shared `StockPostingContext`.

After authorization and replay handling:

1. Lock the FG document and production execution records.
2. Lock source pools/contributions, shared inventory identities and destination slices in deterministic order.
3. Validate version, state, branch, final-route eligibility, cost readiness, conversions, chronology, expiry and aggregate availability.
4. Freeze quantity allocations, pooled dependencies and exact transfer values.
5. Append warehouse receipt history and production `FG_RECEIPT_OUT`; update projections and posting facts.
6. Verify source/destination base quantity and exact value equality.
7. Mark POSTED, seal the posting and commit once.

Require a compatible active V2 ledger. No legacy fallback or inventory-only FG posting.

Normalize branch-first locking across participating IP, Daily Production, inventory and rollback paths before activation. Shared inventory rows must follow one consistent identity order across branches.

Period close already uses the branch stock lock; FG must validate its effective date under that same lock. Backdated FG movements cannot precede the latest affected source or destination stock event. Use posting sequence to distinguish same-time events.

Rollback must:

- Require permission, reason, expected version and a sealed original posting.
- Resolve replay before current-state validation.
- Use original posting facts and current reversal date, without recalculating costs.
- Reject insufficient destination quantity and later active movements on affected destination slices.
- Require later active FG outflows or other dependent outflows from the same source pool to be reversed first.
- Check for later pricing changes before restoring a destination pricing snapshot; never overwrite an unrelated newer price.
- Append inventory reversal, `FG_RECEIPT_REVERSAL`, allocation reversals and dependency reversal facts.
- Restore exact source quantity/value and original provenance.
- Retain original destination links and mark the document REVERSED.

An active movement means one without a completed linked reversal. Reverse-order checks use sealed posting identities/sequences, not dates alone.

Do not reuse generic inventory rollback behaviour that returns a document to NEW or clears links.

### UI, permissions and inquiries

Add **Finished Good Receipt** beside Daily Production:

- Menu: `PLN_FINISHED_GOOD_RECEIPT`.
- List: `/planning/finished-good-receipts`.
- Entry routes: `/planning/finished-good-receipts/new`, `/{id:int}/edit`, `/{id:int}/view`.
- Separate Razor markup, code-behind and scoped CSS.

Match Daily Production’s `PageBase`, `MenuAuthorize`, `iv-*` chrome, status chips, `CommonDataGridEx`, server paging, debounced search, filters, bounded mobile list, DevExpress forms and sticky Save Draft footer.

Entry flow:

**Select Work Order → select eligible lots → enter quantities/destinations → review readiness → save draft → post.**

Display source lot, item, Work Centre/process, availability, requested quantity/UOM, derived destination quantity/UOM, warehouse/bin/lot and expiry. Distinguish estimated draft costs from immutable posted costs.

Provide confirmation dialogs, rollback reason, submitting protection, unsaved-change handling and actionable readiness errors.

List actions: New, Post and Rollback. Row actions: View and eligible Edit. Draft deletion belongs on the entry command bar. Reversed documents offer Create Correction.

Correction requires ADD permission and creates a new identity with copied business inputs, empty posting facts and refreshed readiness. Require rollback completion before creating the correction.

Enforce `ACCESS`, `ADD`, `EDIT`, `DELETE`, `POST`, `ROLLBACK` and `VIEW_COST` server-side and in UI. Cost-restricted DTOs, exports and replay responses must omit amounts.

Extend production balances, stock cards and reconciliation with receipt/reversal links and derived net received/pending quantities. Never sum incompatible UOMs.

## 4. Validation and acceptance

| Area | Required scenarios |
|---|---|
| Lifecycle/versioning | Drafts change no stock; header-, detail- and source-only edits invalidate stale clients; posted/reversed documents cannot edit/delete; corrections get new identities. |
| Eligibility | Reject intermediate/held/rejected stock, cancelled orders, non-final routes, cross-tenant IDs, invalid destinations and future-dated stock; allow eligible stock from completed/closed orders. |
| Quantity/UOM | Receive 40 then 60 from 100; fractional conversions conserve base quantity; reject zero-after-rounding and incompatible conversion. |
| Average value | Five units costing RM50 plus five costing RM70: receiving six transfers RM72 and leaves RM48. |
| Rounding | Multiple lines/source and multiple sources/destination conserve exact totals; final depletion leaves zero quantity and value. |
| Provenance | Missing evidence blocks; verified zero works; new verified output cannot cleanse an unvalued pool; reversal restores original provenance. |
| Pooled dependencies | Producer rollback is blocked even when its FIFO allocation is zero or replenishment makes quantity sufficient; ordered downstream reversals permit rollback. |
| Lot ownership | Matching top-ups succeed; conflicting origin/expiry and unowned existing lots fail; non-lot items create no dummy lots. |
| Cross-branch concurrency | Competing creation/top-ups of the same company/item/lot cannot create duplicate lots or overwrite origin/expiry. |
| Idempotency | Lost-response retries of post/rollback return the original outcome despite changed rowversion, date or state; changed payload fails; different requests cannot duplicate execution. |
| Reversal | Original exact values restored despite master-price changes; later slice/source dependencies block; unrelated later pricing cannot be overwritten. |
| Atomicity | Fail after either stock leg, allocations, history or before sealing: no partial balances, facts, status or ownership survives. |
| Reporting | FG history, summaries, exports and reconciliation use exact totals; rounding and later master-price changes cannot alter posted values. |
| Security | Direct service calls enforce branch/actions; generic inventory paths reject FG mutation; unauthorized responses contain no costs. |
| Period/chronology | FG versus period close is serialized; closed-period posting fails; current-open-period reversal preserves original dates; same-time events obey sequence order. |
| UI | Desktop/mobile parity with Daily Production; permission states, errors, discard, correction, double-click and retry behaviour. |

Run real SQL Server concurrency tests in an isolated scratch database for source competition, duplicate requests, lot creation across branches, FG versus producer rollback, downstream warehouse movements and period closure.

Use `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`; every relevant SQL Server fixture must fail when required coverage is unavailable. Passing acceptance requires evidence that those tests actually ran.

Run relevant production, inventory, reporting and permission regression suites and build `ErpWeb.slnx`.

## 5. Delivery and release gates

1. Save this replacement to the target document when execution is enabled; preserve the original deep study.
2. Add schema, constants, constraints, permissions and rerunnable deployment/verification scripts.
3. Complete upstream provenance, mixed-pool readiness, allocations, dependency protection and consistent locking.
4. Implement FG draft/read services, atomic posting, reversal and correction.
5. Implement UI, inquiries, exact-value reporting and preflight diagnostics.
6. Execute automated checks and operator acceptance.

Keep FG posting disabled by default. Activate only when schema compatibility, upstream readiness, locking regression tests and reconciliation checks pass.

The preflight report must identify historical unvalued pools, incomplete provenance, missing dimensions, unresolved lot ownership and quantity/value mismatches. These exceptions remain blocked; they are not automatically converted.

Release acceptance requires:

- Exact production-out versus inventory-in quantity/value equality.
- No negative stock, stranded value or partial postings.
- Immutable originals, reversals and traceable correction documents.
- Verified retry, concurrency and failure recovery.
- Protected pooled dependencies and company-wide lot ownership.
- Exact-value agreement across posting and reporting.
- Daily Production UI consistency and successful operator acceptance.

Operational disablement stops new FG posting while preserving inquiries and authorized reversal. After live FG postings exist, do not roll back to an application version that cannot recognize their dependencies or history.

**Approval boundary:** This specification is ready for implementation approval. Release approval requires the recorded test and acceptance evidence above.