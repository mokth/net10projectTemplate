# Production Planning Masters — Legacy Behavior Verification (Phase 0.5)

Tags: **Confirmed** | **Intentional enhancement** | **Known defect (do not port)** | **Unverified**

## Hierarchy keys / tenant

| Finding | Tag |
|---------|-----|
| Work Centre PK `Wrk_Ctr_Cd`; Process composite `(Process_Cd, Work_Centre)`; Machine composite `(Machine_Cd, Process_Cd)` per CAdapter/EF | Confirmed |
| Legacy UI did not consistently filter by CompCode on `Select *` loads | Confirmed |
| New system uniqueness + queries scoped by CompCode | Intentional enhancement |
| Legacy tree enforced global unique Process_Cd; flat grid used composite | Confirmed (inconsistency) |
| New system uses composite Process key | Intentional enhancement |
| MachineCd unique across table in legacy insert rule | Confirmed |
| MachineCd unique per CompCode in new system | Intentional enhancement |
| ProcessCd immutable on Machine update; Relocated UoW for process change | Intentional enhancement |

## Delete / relocation

| Finding | Tag |
|---------|-----|
| Flat WC delete had no FK guard (could orphan) | Known defect |
| Tree cascade deleted WC→Process→Machine immediately | Confirmed |
| New: cascade only owned rows; Preventive + ShiftCalendar block Machine delete | Intentional enhancement |
| Relocation: MacSeq re-keyed; Preventive/Calendar preserved; PrDefMachine blocks | Intentional enhancement (signed interim) |
| WO history rewrite on relocate | Unverified (blocked if tied) |

## Shift / breaks

| Finding | Tag |
|---------|-----|
| Overnight: end &lt; start ⇒ +1440 minutes | Confirmed |
| Up to 5 break pairs; net = span − breaks | Confirmed |
| `totaltime` float = hours + minutes/100 | Confirmed |
| Legacy stored breaks as DateTime; empty often 00:00 | Confirmed |
| Domain nullable TimeOnly; 00:00 is valid time | Intentional enhancement |
| Load maps 00:00–00:00 sentinel → unused for compat | Intentional enhancement |
| Exact midnight break round-trip on Case C DB | Unverified (Case A preferred) |

## Shift group

| Finding | Tag |
|---------|-----|
| Junction rows ShfGrp_Cd + Shift_Cd | Confirmed |
| Total &lt; 1440; overlap rejected | Confirmed |
| DefaultGrp clear others on save | Confirmed |
| Circular `[start,end)` adjacency allowed | Intentional enhancement (explicit) |
| Concurrent default race | Unverified in legacy; new uses single transaction |

## Calendars

| Finding | Tag |
|---------|-----|
| Company Date_Cd W/O; machine calendar + ShfGrp_Cd | Confirmed |
| SaveWithMachine recreate could overwrite | Confirmed |
| New default GenerateMissingOnly; RegenerateAll confirm | Intentional enhancement |
| Year fingerprint concurrency | Intentional enhancement |
| Calendarific MY holidays | Confirmed optional; state-specific Unverified |

## Preventive / Prefix / Import

| Finding | Tag |
|---------|-----|
| Preventive UPDATE by MachineCd only | Known defect |
| New UPDATE by UID; range-add duplicate = error | Intentional enhancement |
| Prefix UPDATE targeted PrWorkCentre | Known defect |
| New EF maps to PrWorkPefix | Intentional enhancement |
| Import ACE OLEDB + session key casing bugs | Known defect |
| New ClosedXML; literals-only numerics; validate then one transaction | Intentional enhancement |
| Import BOM into multilevel PrDefBOM | Not in scope (Product Def UI) |

## Description casing

| Finding | Tag |
|---------|-----|
| Legacy often `.ToUpper()` on descriptions | Confirmed |
| New: uppercase codes only; trim descriptions | Intentional enhancement |

## Gate

Phase 0.5 interim sign-off for Phase 1+: **Accepted** with production SQL re-verify required before cutover for uniqueness indexes, break nullability, and calendar CompCode PK shape.
