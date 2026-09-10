---
name: v8 review updates
overview: "Write the consolidated v8 spec (no architecture change): fold both reviews plus the two final clarifications, tests 8.8–8.9, explicit post-lock re-read wording, and a precise Section 15 checklist."
todos:
  - id: locked-decisions
    content: "Update Section 2: dest cost (incl. cost-mismatch still allowed), multi-source piles, full slice compare, post-lock identity, FindOrCreate, rollback integrity"
    status: completed
  - id: business-posting
    content: "Clarify §5/§6/§7: costing, slice compare, authoritative post-lock re-read + StdUom, concurrent dest, rollback integrity + atomic fail"
    status: completed
  - id: tests-guardrails
    content: Add tests 8.1–8.9, agent instructions 14–18, typo fix, explicit Section 15 verification checklist
    status: completed
isProject: false
---

# Consolidate v8 implementation spec

Architecture stays as written. Only [ErpWeb/docs/stock_transfer_implementation_plan_v8.md](ErpWeb/docs/stock_transfer_implementation_plan_v8.md) is updated (status line: review comments folded in; implementation-ready). No code, no v9, no v7 changes.

This pass is the **consolidated implementation specification**. Do not produce another review/update document.

## Locked decision: multiple source piles

Add to Section 2: **a document may contain many different `FromBalLocId` values.** Each pile is aggregated independently. Example: A→X, A→Y, B→Z is valid. Do not require one source pile per document.

## Section edits

**Section 2 locked table**
- Cost V1: replace the one-liner with the dest cost rules below, including the cost-mismatch rule.
- Add rows: multiple source piles; same-slice compare uses full `IvStockSliceKey`; post-lock source identity + `StdUom`; dest create via existing `FindOrCreateBalLocAsync`; rollback integrity fail = no mutation.

**Section 3.3** — fix `reconcilation` → `reconciliation`.

**Section 3.5** — after dest find-or-create: concurrent posts targeting the same new slice must reuse `FindOrCreateBalLocAsync` (lock-then-insert, unique-violation re-read). Never insert `IvBalLoc` from TR code. Constraint `UQ_IvBalLoc_StockSlice` remains authoritative.

**Section 5.2** — same-source/dest reject must compare the full key, not WH+Loc only:

```text
CompanyCode + BranchCode + ICode + WhCode + LocCode + LotNo + IStatus
```

Same WH/Loc with different `IStatus` is a **different** slice (allowed).

**New 5.3a / expand 5.3** — multiple source piles + independent aggregation. Keep the 1→3 dest example.

**New costing subsection (5.x)** — Destination costing V1:

- Dest missing → create with source `Cost` / `UnitPrice`
- Dest exists, `StdQty = 0` → set from source
- Dest exists, `StdQty > 0` → **do not overwrite**
- **If dest `StdQty > 0` and source `Cost` / `UnitPrice` differs from dest `Cost` / `UnitPrice`, the transfer is still allowed.** Do not treat the difference as a validation failure. Existing dest cost remains unchanged.
- History always stores source `Cost` / `UnitPrice` for the transferred qty
- No MAV / warehouse blend

Replace the vague line in Section 6 (“if new or empty”).

**Section 5.3 UI qty** — explicit: UI available qty is advisory only; posting must lock and re-read `IvBalLoc.StdQty` inside the transaction. Never use client/UI qty as authority.

**Section 7 PostInventoryTRAsync step 10** — after locking `FromBalLocId`, **re-read the authoritative `IvBalLoc` row inside the transaction**. Compare identity and `StdUom` against staged source information. Do **not** treat the previously loaded entity as authoritative.

```text
CompanyCode, BranchCode, ICode, WhCode, LocCode, LotNo, IStatus, StdUom
```

Mismatch → fail whole transaction.

**Section 7 RollBackInventoryTRAsync** — before any qty change, require (and clone MI line-pairing):

```text
History.BatchNo == BatchNo
History.TrxType == TR
History.TrxLineNo == Detail.TrxLineNo
History.FromBalLocId == Detail.FromBalLocId
History.ToBalLocId == Detail.ToBalLocId
History.FrStdQty == Detail.FrStdQty
History.ToStdQty == Detail.ToStdQty
```

Also: 1:1 line pairing, no duplicate `TrxLineNo`, tenant match (same pattern as `ValidateMiHistoryIntegrity`). On failure: rollback DB tx, **no** stock change, **no** history delete, batch stays `POSTED`. Partial dest qty: fail atomically; **no** source restore.

**Section 11** — add test groups 8.1–8.9:

- 8.1 Multiple source piles
- 8.2 Same complete stock slice
- 8.3 Destination cost (including: dest with qty and different cost still posts; dest cost unchanged)
- 8.4 Concurrent destination creation
- 8.5 Source identity after lock
- 8.6 Rollback integrity
- 8.7 Partial destination movement
- **8.8 Full-batch atomicity:** 3-line document, line 3 invalid → no source/dest qty change, no committed dest pile, no history, batch stays `NEW`, entire tx rolls back (proves later-line failure cannot commit earlier lines)
- **8.9 Concurrent overlapping transfer:** A and B both Source X → Dest Z. Source cannot go negative; no lost update; no duplicate dest `IvBalLoc`; winner has correct qty; loser fails/waits per existing locks; no partial history

**Section 13** — add instructions 14–18 (advisory UI qty; don’t overwrite non-empty dest cost **and don’t reject cost mismatch**; full slice after lock via re-read; reuse FindOrCreate; rollback integrity first).

**Section 15** — use explicit verification statements:

- [x] Destination cost overwrite rule defined
- [x] Cost mismatch on non-empty dest does not block transfer
- [x] Multiple source piles explicitly supported
- [x] Full IvStockSliceKey comparison defined
- [x] Source identity + StdUom revalidated after lock (authoritative re-read)
- [x] Destination creation uses existing FindOrCreateBalLocAsync
- [x] Rollback validates history integrity before mutation
- [x] Rollback failure is atomic
- [x] Full-batch posting atomicity tested
- [x] Concurrent overlapping transfer tested

Do not change implementation phase order (TR-1 → TR-4, stop for review).
