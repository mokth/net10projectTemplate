# QA handoff — Gate 2B (Cursor Grok 4.5 → later GPT-6)

**Status:** PHASE_2B_CODED, STOP at Gate 2B.  
**HEAD (untouched by this agent):** `717f348` (`prodcut defination enhance`).  
**Do not start Phase 2C / 3 / 4 / 5 in this checkout without a new explicit approval.**

## What 2B did

- `QaFgGrant` / `QaFgAllocation` (EF + `scripts/create-qa-fg-grant.sql`).
- Approve of `FG_STAGING_LOT` issues an OPEN grant for accepted qty. Rejected qty is not inventory.
- `IQaFgAuthorization` claims in the same FG POST transaction after eligibility / before ledger begin; restore on rollback reverses POSTED allocations and does **not** rewrite old source IDs. The new FG generation claims remaining grant against new source IDs.
- `FinalFgInspectionEnabled` is savable, **default OFF**. `PhysicalHoldEnabled` still rejected.
- When FinalFg is ON, PADJ cannot save/post `FG_STAGING` pools and search hides that source class. PROCESS_WIP PADJ is unchanged.
- WI / non-good / posted stock remain record-only. No logical holds (2C). No costing formula, FIFO/WAC/standard, or quantity algorithm changes.

## Flags

Live tenants stay OFF until a DBA runs the SQL scripts **and** an admin turns Incoming / FinalFg on. Catalogue `QaRequireInspectionOnReceipt` stays unused.

## Not proven

- Browser visual matrix (desktop/tablet/phone, light/dark).
- SQL Server concurrency (`UPDLOCK/HOLDLOCK` on `QaFgGrant`).
- Live-tenant script apply.

## Next (out of Grok scope)

Phase 2C logical holds, then 3–5. PhysicalHold remains rejected until Phase 4.
