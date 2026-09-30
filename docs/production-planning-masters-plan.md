# Production Planning Masters — Implementation Plan (Rev 4.1 executed)

This document is the working master plan for ErpWeb Planning masters, filled during **Phase 0** (schema/docs) and kept current as services ship.

## Status

| Gate | Status |
|------|--------|
| Phase 0 schema/docs/menus/`PrProcess` | **Done** (local SQLEXPRESS had no Planning tables — matrices from EF + CAdapter; apply `scripts/create-planning-masters.sql`) |
| Phase 0.5 legacy behavior | See [production-planning-masters-legacy-behavior.md](production-planning-masters-legacy-behavior.md) |
| Phase 1–7 implementation | Shipped in ErpWeb.Core / ErpWeb.UI / scripts |

## Live SQL note

Queried `.\SQLEXPRESS` / `ERPWeb`: **0 Planning master tables** present. Production server `WINCOM-server4` unreachable from this workstation.  

**Authoritative interim matrix source:** EF configurations under `ErpWeb.Model/Configurations/Planning` + legacy `CAdapter.SetPr*`. Re-run Phase 0 matrix against production before cutover and update uniqueness/concurrency/sentinel columns if they differ.

## Key / tenant / rekey matrix (signed interim)

| Entity | Physical/EF key | Business uniqueness | Tenant | Key editable? | Concurrency | Uniqueness enforcement |
|--------|-----------------|---------------------|--------|---------------|-------------|------------------------|
| PrWorkCentre | Wrk_Ctr_Cd | Wrk_Ctr_Cd per CompCode | CompCode | No | Updated compare | App (+ optional UX index) |
| PrProcess | (Process_Cd, Work_Centre) | Same per CompCode | CompCode | ProcessCd No | Updated | App |
| PrMachine | (Machine_Cd, Process_Cd) | Machine_Cd per CompCode | CompCode | Process via Relocated only | Updated | App + filtered UX index in create script |
| PrShift | Shift_Cd | per CompCode | CompCode | No | Updated | App |
| PrShiftGroup | (ShfGrp_Cd, Shift_Cd) | one DefaultGrp per CompCode | CompCode | Group code No | Transaction clear+set | App (serializable recommended if no filtered unique index) |
| PrOperator | Code | per CompanyCode | CompanyCode | No | Updated | App |
| PrWorkPefix | Prefix | per CompCode | CompCode | No | Updated | App — **EF updates correct table** |
| PrMacSeq | (Machine_Cd, Process_Cd) | SeqNo per process (soft) | CompCode | — | Updated | App |
| PrPreventive | UID identity | Machine+DownDt duplicate rejected | (no Comp on entity) | — | UID | Identity |
| PrCalendar | Dt | Dt (+ CompCode filter) | CompCode | — | Year SHA-256 fingerprint | App |
| PrShiftCalendar | (Dt, MachineCode) | — | CompCode | — | Year SHA-256 fingerprint | App |
| PrHoliday | UID | DateOff | n/a | — | — | Identity |

## Break persistence Case

**Interim decision: Case A preferred** (nullable DateTime break columns in create script).  

Load path still treats legacy **00:00/00:00 sentinel as unused** for compatibility (`treatMidnightAsUnused: true`). Exact midnight breaks are supported when DB stores non-sentinel distinct times / nulls. If production is Case C (non-null sentinel only), document restriction before claiming midnight break support.

## Delete vs Relocation matrix (signed interim)

| Related | Permanent Machine Delete | Machine Relocation (same MachineCd) |
|---------|--------------------------|-------------------------------------|
| PrMacSeq | Cascade | Re-key (delete old + insert new process) |
| PrPreventive | **Block** | Preserve (keyed by MachineCd) |
| PrShiftCalendar | **Block** | Preserve (keyed by MachineCode) |
| PrDefMachine | **Block** | **Block** (`RELOCATION_BLOCKED`) |
| WO history | Block | Do not rewrite |

## Import FK order (provisional → routing tables)

Delete: PrDefMachine → PrDefProcess → PrDefWCenter → PrDefMas (per ICode, tenant-scoped).  
Insert: reverse.  
BOM sheet: preview-validated only; multilevel BOM via Product Definition UI (`PrBomHdr`/`PrDefBOM`).

## Menus / permission seed

- `ErpWeb/Menus/menus.xml` — all PLN_* master routes  
- `ErpWeb.Core/Menus/MenuCodes.cs` — constants  
- `scripts/init-planning-masters-menu.sql` — MenuPermission ACCESS/ADD/EDIT/DELETE  
- Startup `MenuSyncService` syncs XML → dbo.Menu  

## Architecture delivered

`IDbContextFactory` services, Tenant Boundary, ACCESS/ADD/EDIT/DELETE, `PlanningServiceResult` + error codes + UI targets, Relocated UoW with mid-transaction `SaveChanges` flush, ShiftTimeCalculator, ShiftGroupValidator `[start,end)`, calendar SHA-256 fingerprint, ClosedXML import.

## Scripts

- `scripts/create-planning-masters.sql`  
- `scripts/init-planning-masters-menu.sql`  

## Phase 7 cutover / UAT checklist

1. **Schema** — Apply `create-planning-masters.sql` on target DB (or confirm production tables match Phase 0 matrices). Re-verify uniqueness/concurrency/sentinel columns against live SQL before go-live.  
2. **Menus** — Deploy `menus.xml`; start app once for `MenuSyncService`; run `init-planning-masters-menu.sql`; grant RoleMenuPermission (optional block in script).  
3. **Permissions smoke** — Login as non-admin: ACCESS-only user cannot Save; ADD/EDIT/DELETE gates on each PLN_* screen.  
4. **Hierarchy** — Create WC→Process→Machine; Relocate machine Process with flush; delete blocked by Preventive/ShiftCalendar/PrDefMachine.  
5. **Shifts** — Daytime + overnight + midnight break; ShiftGroup adjacency OK / 1-min overlap fail / total &lt; 1440; sole group auto-default.  
6. **Calendars** — Generate year + holiday overlay; fingerprint conflict reject; GenerateMissing vs RegenerateAll confirm; machine copy preview/overwrite; Calendarific optional (`Calendarific:ApiKey`).  
7. **Support** — Prefix UPDATE hits `PrWorkPefix`; Preventive range-add duplicate reject; MacSeq FK + SeqNo uniqueness.  
8. **Import** — Workbook `Meta!A1 = ImportPrdDefV2`; formula reject; missing master FK reject; commit all-or-nothing; Labour sheet preview-only v1.  
9. **WO wiring** — Process Preview builds operations from `PrDefProcess`/`PrDefMachine`, enriched from WC/Process/Machine masters; save persists ops+resources.  
10. **Perf** — Load company calendar year + 50 machines GenerateMissing; hierarchy save with &lt;100 pending nodes.  
