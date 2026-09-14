# Purchase Credit / Debit Note — Logic Specification

> Normative reference for the `PoCdn` module. Controls are numbered **C1–C48** and are the
> signed-off decisions from the plan review rounds. The implementation plan is
> `plans/POCNDN-plan.md`; this document records what the shipped behaviour *is*.

---

## 1. What this module is — and what it is not

The purchase side has **three** distinct correction mechanisms. Confusing them is the single
biggest source of defects, so the boundary is stated first (C7).

| Mechanism | Entity | What it changes | Touches stock? |
|---|---|---|---|
| **Purchase Credit/Debit Note** | `PoCdn` | Money owed to the supplier | Only when `ReturnStock` is set |
| **Quantity correction** | `PoInvoice` with `Type = CN` | Received quantity against a PO/GR | Yes |
| **Vendor Return** | `IvTrxBatch` (`VR`) | Physical goods leaving the warehouse | Yes |

`PoCdn` is the **financial** document. A price adjustment is a `PoCdn` and nothing else. A
goods return is a `PoCdn` **only** when it also carries a money implication; the physical
movement is still performed by the existing vendor-return machinery.

---

## 2. Document shape

### Header — `dbo.PoCdn`

- Key: `(CompanyCode, BranchCode, DocNo)`
- `Type` ∈ `{CN, DN}` — credit note / debit note.
- `Status` ∈ `NEW` → `POSTED`, plus `CANCELLED`. **`POSTED` is an operational state, not a
  lock on physical correction** (C1).
- `ReasonCode` — drives ceilings and stock behaviour. See §4.
- `VrBatchNo` — populated only when the note carries a physical return.
- `RowVersion` — optimistic concurrency, **not** immutability (C21).

### Line — `dbo.PoCdnDetail`

- Key: `(CompanyCode, BranchCode, DocNo, Line)`
- `InvLineNo` — the referenced invoice line (C24).
- `IsStockReturn` — whether this line drives a physical return (C43).
- `FromBalLocId` — source location for a stock return.
- `PoNo` / `PoRelNo` / `PoLineNo` — PO traceability for physical returns.

---

## 3. Lifecycle

```
SaveNewAsync ──► NEW ──► PostAsync ──► POSTED
                  │                      │
                  │                      └──► RollbackAsync ──► (back to NEW)
                  └──► DeleteAsync (NEW only)
```

- **Create** — a draft reserves money against the referenced invoice immediately (C3).
- **Update** — `NEW` documents only (C21). A `POSTED` document must be rolled back first.
- **Delete** — `NEW` documents only; releases the reservation.
- **Post** — for a value-only note, marks `POSTED` with no vendor return. For a stock-returning
  note, posts the physical movement and the financial document together, or neither.
- **Rollback** — reverses a `POSTED` note. Releases reservations; reverses any VR batch.

---

## 4. Reason codes (C9, C19, C36, C38)

Reason codes are **not** GL keys (C33). They are a policy selector: each reason declares whether
it is capped by quantity, capped by value, requires a PO, requires an invoice, and whether it is
allowed to move stock.

### Credit note reasons

| Reason | Cap | Stock-capable | Requires PO | Requires invoice | Needs `INTERNAL_ADJUSTMENT` |
|---|---|---|---|---|---|
| `RETURN` | Quantity | Yes | Yes | Yes | No |
| `DAMAGED` | Quantity | Yes | Yes | Yes | No |
| `SHORT_SUPPLY` | Quantity | Yes | Yes | Yes | No |
| `PRICE_ADJUSTMENT` | Value | No | No | Yes | No |
| `OVERBILL` | Value | No | No | Yes | No |
| `REBATE` | None | No | No | No | No |
| `TAX_ADJUSTMENT` | None | No | No | No | No |
| `OTHER` | None | No | No | No | No |
| `INTERNAL_ADJUSTMENT` | None | No | No | No | **Yes** |

### Debit note reasons

All debit-note reasons are uncapped and do not require an invoice.

`INTERNAL_ADJUSTMENT` is the escape hatch for corrections with no supplier document behind them.
It is gated behind a dedicated permission (see §9) because it can move money with no external
evidence.

---

## 5. Ceilings and reservations

Three independent ceilings apply to a credit note. **All must hold.**

### 5.1 Source-line quantity ceiling

For quantity-capped reasons, the note may not credit more than the invoice line still holds.
Consumption by other draft and posted notes is subtracted (C34).

### 5.2 Source-line value ceiling

For value-capped reasons, the credited net value may not exceed the invoice line's remaining net
value, again net of other notes (C34, C46).

> **Check ordering is deliberate.** The source-line ceilings are evaluated *before* the
> document-level reservation. The line message ("Line 1: source-line value 600.00 exceeds …")
> is strictly more actionable than the document message ("total 1272 exceeds invoice remaining
> 1060"), so the specific cause is reported first.

### 5.3 Document reservation

A credit note against a posted invoice may never push the total credited above the invoice total:

```
invoice total ≥ posted notes + draft notes + this note
```

The invoice total is **tax-inclusive**, so the note totals compared against it are tax-inclusive
too. Comparing a net note against a gross invoice silently over-credits by the tax amount.

Reservation arithmetic lives in `PoCdnCalc` and is pure — `EvaluateRemaining`,
`CreditReservationAmount`, `EvaluateSourceLineQuantity`, `EvaluateSourceLineValue`.

---

## 6. Concurrency (C10, C41)

### C41 — invoice-line reservation race

Two users crediting the *same* invoice line at the same instant would each read
"remaining = 10" and both save, silently over-crediting the invoice. This is prevented by
taking a lock on the **invoice row** for the duration of the check-and-insert:

```
PoCdnLockOrder.AcquireAsync → invoice, then PoCdn
```

Lock ordering is fixed (invoice before note) so two concurrent saves cannot deadlock. The loser
observes the winner's reservation and is refused.

### C10 — physical return race

Stock-returning notes are additionally bounded by what was actually received on the PO line
(`RecvQty − ReturnQty`), re-checked under the PO lock in
`IvInventoryPostingService.ApplyVendorReturnPoQtyAsync`.

Both races are exercised in `ErpWeb.Tests/PoCdnSqlServerConcurrencyTests.cs`, which **self-skips
unless** `ConnectionStrings:SqlServerTestConnection` is set **and** names a database containing
"test". SQLite cannot validate `UPDLOCK`/`HOLDLOCK`, so these controls are not covered by the SQLite
suite.

> **Why a dedicated connection key.** The other `*SqlServerConcurrencyTests` in this repo read
> `DefaultConnection`, which points at the live `ERPWeb` database, and they seed and delete rows. The
> PoCdn tests deliberately do not do that: a stray `dotnet test` must never run a destructive
> concurrency race against production data. Set the key to a scratch database to run them:
>
> ```
> $env:ConnectionStrings__SqlServerTestConnection = "Server=.\SQLEXPRESS;Database=ERPWeb_PoCdnTest;Trusted_Connection=True;TrustServerCertificate=True"
> ```

### What the SQL Server tests established

Run on 2026-09-14 against a scratch database:

| Control | Result |
|---|---|
| **C41** invoice-line reservation | Two concurrent 10-unit notes on a 10-unit line → exactly one wins |
| **C41** follow-up | The survivor consumed exactly its own quantity; the remainder is available |
| **C10** stock-return post race | Both drafts save (save is a soft warning), exactly one **post** succeeds |
| **C2** duplicate supplier document | The filtered unique index rejects the loser |

> **C10 nuance worth preserving.** The save-time physical ceiling is deliberately **not**
> authoritative — the plan specifies it as a soft warning, with the real revalidation inside the
> posting transaction under the PO lock. A test that asserts the race at *save* time is asserting the
> opposite of the design.

---

## 7. Currency (C17, C18, C26)

`PoCdnService.ResolveCurrRateCoreAsync` **fails closed**:

- Home currency (`MYR`) with no `SaCurrRate` row → rate `1`.
- Foreign currency → requires an active `SaCurrRate` window covering `DocDate`, and rejects a
  rate of exactly `1`.
- Anything unresolvable → the call fails and **carries the reason**.

This is deliberately *stricter* than `PoInvoiceService.ResolveCurrRateAsync`, which silently
returns `1m`. A zero or `1m` foreign rate would misstate the note by the full exchange
difference, so a missing rate must block the save rather than default.

> **C18 is enforced.** When `InvNo` is supplied the header `Currency` **must** equal the referenced
> invoice's currency. A blank request currency inherits silently; a *conflicting* one is
> **rejected** (`Currency USD does not match the currency MYR of invoice PINV01.`).
> The earlier behaviour silently overwrote the request currency, which discarded the operator's
> stated intent without telling them. Regression tests:
> `A_credit_note_rejects_a_currency_that_differs_from_the_invoice` and
> `A_credit_note_with_a_blank_currency_inherits_the_invoice_currency`.
>
> Note the ordering: the C18 mismatch is reported in preference to a downstream rate failure, so
> the operator sees the specific fault rather than "no rate for USD".

---

## 8. Validation behaviour

Two rules govern how failures are surfaced, both learned from defects found by tests:

1. **Per-line errors win.** After collecting per-line failures, validation returns *before*
   any document-level aggregate is evaluated. Otherwise a document check overwrites
   `errors["Lines"]` and the operator is told the wrong thing — e.g. "total must be greater
   than zero" when the real fault was one bad line.
2. **The specific message is the contract.** `ErrorKind` is `Validation` and `ErrorMessage`
   carries the concrete cause. A generic "Validation failed." is a bug.

Check order in `PrepareAsync`, and why:

| Order | Check | Rationale |
|---|---|---|
| 1 | Context / permission / type | Cheapest, and fails closed |
| 2 | Per-line contract (C30/C38/C46) | Specific, field-addressable errors |
| 3 | Source-line quantity & value ceilings | Most specific monetary cause |
| 4 | Document reservation | Coarse, only meaningful once lines are valid |
| 5 | Supplier-document duplicate | Needs a coherent document |
| 6 | Vendor blocked policy | Warning at post, block at save (C15) |

`UpdateAsync` checks **C21 immutability before the RowVersion presence guard**. A caller editing
a `POSTED` document must be told it is immutable, not that "another user changed it".

---

## 9. Traceability and integrity

- **C24** — `InvLineNo` binds a note line to a specific invoice line. A requested line that does
  not exist is reported as such; the *found* line, not the requested number, is what the
  traceability checks compare against.
- **C25** — `PoInvoiceService.RollbackOneAsync` refuses to roll back an invoice that a `PoCdn`
  references. This is a *second* guard alongside the pre-existing quantity-correction check;
  both exist because the two correction mechanisms are independent.
- **C31** — `IvVendorReturnService` protects VR batches owned by a `PoCdn`. `UpdateAsync`,
  `DeleteAsync` and `CancelAsync` refuse to touch them, and `PostAsync`/`RollbackAsync` carry
  read-only pre-checks. Owned batches are identified by the `PCN/` reference prefix.
- **C23** — duplicate supplier documents are prevented by a filtered unique index on
  `(CompanyCode, BranchCode, VendorCode, Type, SupplierDocNo)` where `SupplierDocNo IS NOT NULL`.
  Document numbers are normalised before comparison.
- **C42** — a fingerprint identifies the VR batch a note would produce, so a partially-completed
  post is detectable rather than re-executed.
- **C17/C47** — failure and rollback emit `PoCdnPostFailed` / `PoCdnRollbackFailed` through
  Serilog. There is no field-level audit trail in this codebase; `IvTrxHistory` covers inventory
  only.

---

## 10. Permissions and menu

Permission `INTERNAL_ADJUSTMENT` gates the `INTERNAL_ADJUSTMENT` reason code (C43). Menu codes
`PO_CN`, `PO_DN`, `PO_CN_RESERVATIONS` sit under `PO_TRANSACTIONS`.

Numbering modules are `PCN` (credit) and `PDN` (debit) — deliberately **not** `CN`/`DN`, which
belong to the sales module.

---

## 11. Test coverage

| Suite | Count | What it proves |
|---|---|---|
| `PoCdnCalcTests` | 44 | Pure arithmetic: reason metadata, ceilings, tax contract, snapshots |
| `PoCdnServiceTests` | 36 | Lifecycle, permissions, currency, validation ordering |
| `PoCdnSqlServerConcurrencyTests` | 3 | C10 / C41 races — **requires SQL Server** |

The SQLite suites prove arithmetic, not serialization. The concurrency file is the only place
the two locks are genuinely exercised.

---

## 12. UI surface

| Route | Screen |
|---|---|
| `/purchase/credit-notes`, `/purchase/debit-notes` | `PoCdnList` — batch POST / ROLLBACK / DELETE, filters |
| `/purchase/{credit\|debit}-notes/{new\|edit\|view}[/{docNo}]` | `PoCdn` — entry / edit / view |
| `/purchase/cn-reservations` | `PoCdnReservations` — diagnostic report |

The posted purchase invoice screen also offers **Create Purchase Credit Note**, which navigates to
`/purchase/credit-notes/new?invNo=…` so the operator does not re-key the reference.

### Authoring a stock-return line

A stock-return line needs two things beyond a normal line, and the screen drives both:

1. **Source invoice line** — chosen through the *Source line* picker, which lists the invoice's
   lines with `RemainingStdQty` / `RemainingAmount` already net of every other draft and posted
   note (C34). Selecting one sets C24 traceability (`InvLineNo`) **and** the PO link
   (`PoNo`/`PoRelNo`/`PoLineNo`), which the billing line alone cannot supply.
2. **Balance location** — chosen through `IvBalLocPicker`, the same on-hand pile picker every other
   inventory screen uses, so the authoritative stock identity (C35) is resolved identically here.
   The chosen pile's warehouse, location, lot, status and on-hand quantity are carried onto the
   line, and the screen refuses a quantity above what the pile actually holds.

The service re-validates all of this on save; the screen's checks exist to catch the mistake early,
not to replace the server's.

> **Deliberate restriction:** the *Source line* picker is the only way to set `InvLineNo` — the field
> is read-only in the popup. Authoring an invoice line reference by hand would invite a reference the
> C24 traceability check cannot corroborate.
