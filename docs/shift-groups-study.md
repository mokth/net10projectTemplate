# Production Shift / Shift Group / Machine Calendar — Study & Blazor Port Guide

Source ERP (WebForms + DevExpress): `c:\wincom\ERPV55\ERP_5.5\ProductionPlan\ProdPlan\Master\`

| Screen | Files | Table(s) |
|--------|-------|----------|
| Work Shift | `WorkShift.aspx` / `.cs`, list `WorkShiftView.aspx` | `PrShift` |
| Shift Group | `ShiftGroup.aspx` / `.cs`, list `WorkGroupView.aspx` | `PrShiftGroup` |
| Machine Shift Calendar | `MacShiftCal.aspx` / `.cs` | `PrShiftCalendar`, `PrHoliday` |

Supporting BL:

- `ERPClasses\BL\PrShiftGroupBL.cs` — planning-time shift slots
- `ERPClasses\BL\PrShiftCalendarBL.cs` — load/save calendar
- `ERPClasses\Classes\CAdapter.cs` — `SetPrShift`, `SetPrShiftGroup`, `SetPrShiftCalendar`

---

## 1. Domain model

```text
PrShift (Work Shift)                    ← WorkShift entry
   └─ selected into ─┐
                     ├─► PrShiftGroup (Shift Group = 1..N shifts)  ← ShiftGroup entry
                     │        └─ color / DefaultGrp
                     ▼
              PrShiftCalendar (per Machine + Year + Date)          ← MacShiftCal
                     Date_Cd = 'W' (work) | 'O' (off)
                     ShfGrp_Cd when work
              PrHoliday (forces Off on those dates)
```

| Table | Role | Key |
|-------|------|-----|
| `PrShift` | Single shift definition (times + breaks) | `Shift_Cd` |
| `PrShiftGroup` | Group **repeated per shift** (not normalized header+detail) | `(ShfGrp_Cd, Shift_Cd)` |
| `PrShiftCalendar` | Machine annual calendar day | `Dt` + `MachineCode` |
| `PrHoliday` | Holiday / off-day list | `UID` |

**Dependency order for porting:** Work Shift → Shift Group → Holiday → Machine Calendar → port `GetShitGroupInfo` for WO planning.

---

## 2. Work Shift (`WorkShift`)

### 2.1 Navigation & modes

- List: `WorkShiftView.aspx` → NEW / EDIT / VIEW / DELETE
- Entry: `WorkShift.aspx`
  - New: no query string
  - Edit: `?ID={Shift_Cd}&Type=EDIT`
  - View: `?ID={Shift_Cd}&Type=VIEW`
- Cancel → `WorkShiftView.aspx`

### 2.2 Entry UI design

Round panel **"WORK SHIFT"**, 2-column form.

**Header (required)**

| Field | Control | Validation |
|-------|---------|------------|
| SHIFT CODE | text | required (`vgSave`) |
| DESCRIPTION | text | required |
| START TIME | HH spin (0–23) + MM spin (0–59) | both required |
| END TIME | HH + MM | both required |
| OT START | HH + MM | **ClientVisible=false**; still saved as `00:00` if empty |

**Breaks 1–5 (optional)**

```text
BREAK TIME n : [HH] [MM]     TO : [HH] [MM]
```

Empty → treated as `00:00` on save/calc (means “no break”).

**Footer**

| Field | Behavior |
|-------|----------|
| TOTAL WORKING | HH + MM, **read-only**, required before save |
| GET TOTAL | callback recalculates |
| Hint | “Hour is using 24hour format” |
| SAVE / CANCEL | callback save / navigate to list |

Hidden fields: `UserID`, `CompCode`, `BranchCode`, `LocCode`.

**Modes**

| Query | UI |
|-------|-----|
| *(none)* = New | Code editable |
| `Type=EDIT` | Code locked (`ClientEnabled=false`), bind + `getTotal()` |
| `Type=VIEW` | All inputs + SAVE/CANCEL/GET TOTAL disabled/hidden |

Client JS: validate `vgSave` → `callPanel.PerformCallback('SAVE:')` or `'GETTOTAL:'`.

### 2.3 Time encoding

All times stored as **DateTime** (calendar date + time-of-day). UI only edits HH/MM.

**Bind rule:** if `HH:mm == 00:00` for OT or any break → show blank; Start/End always filled. After bind → auto `getTotal()`.

### 2.4 `getTotal()` — net working minutes

```text
startMin = HH*60 + MM
endMin   = HH*60 + MM
if endMin < startMin → endMin += 1440          // overnight shift

for each break i=1..5:
  fromMin / toMin  (empty → 0)
  breakMins += (toMin - fromMin)

totalMin = (endMin - startMin) - sum(breakMins)
txtTotal1 = totalMin / 60
txtTotal2 = totalMin % 60
```

Notes:

- Overnight (+1440) applies only to Start→End on this page, **not** to break windows.
- Unused breaks at `00:00–00:00` add 0.

### 2.5 Save flow

```text
1. InputValidation on Shift_Cd
2. Lookup PrShift by code
   - not found → NewRow (Create)
   - found + code still editable → error "Item(s) Exist!"  (duplicate on New)
   - found + code locked (Edit) → update that row
3. Shift_Cd = UPPER(code), Shift_Des = desc
4. Parse every HH/MM into DateTime fields (empty → 00:00)
   Validate HH≤24, MM≤59
5. Audit:
   New  → UserID, Created
   Edit → UpdatedUID, Updated
   Always → CompCode, BranchCode, LocCode
6. totaltime = float( "HH.MM" )   // e.g. 8h30 → 8.30  NOT 8.5
7. CAdapter.SetPrShift → da.Update
8. Success → alert "Data Saved!", clear form (does NOT auto-return to list)
```

### 2.6 `totaltime` storage (critical)

```csharp
double tttime = Convert(txtTotal1.Value + "." + txtTotal2.Value);
drShift["totaltime"] = tttime;
```

| Display | Stored float | Real minutes |
|---------|--------------|--------------|
| 8:30 | `8.30` | 510 |
| 8:05 | `8.05` | 485 |
| 10:00 | `10.00` | 600 |

ShiftGroup converts later: integer part = hours, `(frac * 100)` = minutes. **Keep this encoding if sharing the same DB.**

### 2.7 Field → DB map (`PrShift`)

| UI | Column | Type |
|----|--------|------|
| Shift Code | `Shift_Cd` | nvarchar(10), PK, UPPER |
| Description | `Shift_Des` | nvarchar(30) |
| Start | `Start_Tm` | datetime |
| End | `End_Tm` | datetime |
| Break n From/To | `Break_Tm{n}From/To` | datetime × 10 |
| OT (hidden) | `OT_StartTime` | datetime |
| Total HH.MM | `totaltime` | float (HH.MM encoding) |
| — | `Override_MRP_Plan` | exists on table/adapter; **not set by this form** |
| Session | `UserID`, `UpdatedUID`, `Created`, `Updated`, `CompCode`, `BranchCode`, `LocCode` | |

### 2.8 CRUD summary

| Op | Where | How |
|----|-------|-----|
| **Create** | Entry (no query) | Insert if code free |
| **Read** | Edit/View bind | `Select * from PrShift` then filter |
| **Update** | Entry `Type=EDIT` | Same adapter Update |
| **Delete** | `WorkShiftView` only | LINQ delete by `Shift_Cd` |

No delete on entry form. List delete does **not** check usage in `PrShiftGroup` / calendar (orphan risk).

### 2.9 Quirks

1. Hour validation code allows `> 24` while spin MaxValue is 23 — Blazor: clamp 0–23 / 0–59.
2. Breaks don’t get overnight +1440 here; overnight only on Start→End.
3. Successful save clears form but stays on page; Cancel returns to list.
4. `Override_MRP_Plan` unused by this form.

---

## 3. Shift Group (`ShiftGroup`)

### 3.1 Navigation & modes

- List: `WorkGroupView.aspx` → NEW / EDIT / VIEW / DELETE  
  - Key: `ShfGrp_Cd`  
  - Source: `vprd_ShiftGroups`  
  - Delete removes **all** `PrShiftGroup` rows for that `ShfGrp_Cd`
- Entry: `ShiftGroup.aspx?ID={ShfGrp_Cd}&Type=EDIT|VIEW`
- Cancel → `WorkGroupView.aspx`

### 3.2 Entry UI design

Two sections:

**WORK SHIFT GROUP**

| Field | Notes |
|-------|-------|
| SHIFT GROUP | code text + **Default Shift Group** checkbox |
| DESCRIPTION | required |
| GET TOTAL | HH + MM (spin) + button |
| COLOR | color picker → hex `ShiftColor` |

**WORK SHIFT LISTING**

- Read-only grid of all `PrShift` rows
- Checkbox multi-select = group members
- Columns: Shift_Cd, Des, Start, End, TotalTime
- Start/End display `HH:mm`

### 3.3 Business rules

| Rule | Behavior |
|------|----------|
| Max 2 shifts | Client: if selection > 2, unselect latest |
| Overlap | On 2nd select, callback `CALCULATETIME` — if ranges overlap (incl. overnight), unselect + error |
| Total ≤ 24h | Sum of selected `totaltime` (as minutes) ≥ 1440 → block |
| ≥1 shift required | Else “No Shift Code Selected!” |
| One default group | If this group is Default, clear `DefaultGrp` on all other groups |
| Only 1 row in whole table | Force `DefaultGrp = true` |
| Composite rows | One DB row per `(ShfGrp_Cd, Shift_Cd)`; header fields (`Des`, `Color`, `Default`) **copied onto every row** |

### 3.4 Total / overlap helpers

**`getTotal(saveTotal)`**

- For each selected shift: optionally recalculate from Start/End − breaks (legacy path), but displayed total uses sum of `PrShift.totaltime` via `ConvertDecimalToTime` → minutes.
- If minutes ≥ 1440 → error; else set HH/MM; if `saveTotal` → call `Save(minutes)`.

**`ConvertDecimalToTime(decimal)`** — decode HH.MM float:

```text
hours = floor(value)
minutesPart = (value % 1) * 100
totalMinutes = hours * 60 + minutesPart
```

**`calculateTimeOver`** — interval overlap including wrap-to-next-day when `end < start`.

### 3.5 Save CRUD (`Save`)

```text
1. Validate code + at least one selected shift
2. Load all PrShiftGroup
3. For each selected Shift_Cd:
     if (ShfGrp_Cd, Shift_Cd) missing → INSERT row
     else → UPDATE row
     set Des, TotalTime(= that shift's TotalMin, not group sum),
         Color, Default, Comp/Branch/Loc, audit
4. DELETE rows for this ShfGrp_Cd whose Shift_Cd is no longer selected
5. If Default ticked → unset Default on other groups
6. SqlDataAdapter Update via CAdapter.SetPrShiftGroup
7. Success → "Item(s) saved!" → return to parent list
```

**Load edit:** bind header from any row of that group; select grid rows by each member `Shift_Cd`.

### 3.6 Field → DB map (`PrShiftGroup`)

| Concept | Column | Notes |
|---------|--------|-------|
| Group code | `ShfGrp_Cd` | nvarchar(10), UPPER on save |
| Description | `ShfGrp_Des` | duplicated on every member row |
| Member shift | `Shift_Cd` | nvarchar(10); composite with group |
| Per-shift minutes | `TotalTime` | int — **that shift’s** minutes |
| Default flag | `DefaultGrp` | bit; only one group should be true |
| Calendar color | `ShiftColor` | hex e.g. `#RRGGBB` |
| Audit / org | `UserID`, `UpdatedUID`, `Created`, `Updated`, `CompCode`, `BranchCode`, `LocCode` | |

PK for adapter delete/update: `(ShfGrp_Cd, Shift_Cd)`.

### 3.7 Runtime consumer (`PrShiftGroupBL`)

Used heavily by Work Order / planning:

- `GetMainGroup()` — shifts where `DefaultGrp = 1`, join `PrShift`, order by `Start_Tm`
- `GetGroupByName(grp)` — same join filtered by group code
- `GetShitGroupInfo(date, grpname)` — builds:
  - `ShiftGroupInfo`: total working/break minutes per day, shift count, `TotalMinPerShift = 1440 / count`
  - Per shift: net minutes, break minutes, **work TimeSlots** split around up to 5 breaks
  - Handles overnight: if End < Start, add 1 day to end and cascading breaks

Port this BL carefully for Blazor WO scheduling.

---

## 4. Machine Shift Calendar (`MacShiftCal`)

### 4.1 Entry UI design

**Top bar:** Year, Machine (source), Copy To (machine or **ALL MACHINE**), Copy button

**Main:** Year calendar (3×4 months) colored by status

**Right panel:**

- Set Day as → Work Day / Off Day
- Shift Group combo (code / desc / color) + color swatch
- Weekly off days + weekly force-work days checkboxes
- Shift Groups legend grid
- Holiday CRUD grid (+ Calendarific import by Malaysian state)

**Actions:** UPDATE CALENDAR (preview) → SAVE (persist)

### 4.2 Calendar cell meaning

| `Date_Cd` | Meaning | Color |
|-----------|---------|-------|
| `O` | Off | Red |
| `W` | Work | `PrShiftGroup.ShiftColor` for that day’s `ShfGrp_Cd` |

Click day (when “Set Day as” on) → callback `SETDATE:yyyy-MM-dd:WORK|OFF` mutates in-memory `Session["MacCalendarDT"]`.

### 4.3 Generate / Preview (`GetDates`)

Requires Year + Machine + a selected shift group (and a default group in DB).

For each day of selected year + machine:

```text
if weekday in Off list       → Date_Cd='O', ShfGrp_Cd=''
else if weekday in Work list → Date_Cd='W', ShfGrp_Cd=selected combo group
else                         → Date_Cd='W', ShfGrp_Cd=DefaultGrp (PrShiftGroupBL.GetMainGroup)
then SetHolidays → any PrHoliday date forced to 'O'
```

- New days: insert rows into in-memory DataTable  
- Existing days: update off/work-day checkbox matches; other weekdays left alone on regenerate  
- DayOfWeek values: Sun=0 … Sat=6 (matches checkbox Values)

### 4.4 Save / Copy

- **Save:** re-apply holidays, then `PrShiftCalendarBL.Save(dt, year, machine)` via adapter upsert of session DataTable (does **not** wipe year first).
- **Copy:** for target machine(s): DELETE year rows for target, then `INSERT … SELECT` from source. Skip source==target. “ALL MACHINE” loops all `PrMachine`.

Also persists weekly off/work preferences via `PlanningUtils.SetWeeklyOffday` / `SetWeeklyWorkDay`.

### 4.5 Holiday

- Grid CRUD on `PrHoliday` filtered by selected year
- Optional: API Calendarific (`country=my`) → filter by state name or ALL → multi-select save into `PrHoliday`

### 4.6 Field → DB map (`PrShiftCalendar`)

| Column | Meaning |
|--------|---------|
| `Dt` | Calendar date |
| `Date_Cd` | `'W'` or `'O'` |
| `ShfGrp_Cd` | Shift group when work; empty when off |
| `MachineCode` | Machine |
| Audit / org | `Created`, `Updated`, `UserID`, `CompCode`, `BranchCode`, `LocCode` |

Shift group combo SQL (distinct groups):

```sql
SELECT ShfGrp_Cd, ShfGrp_Des, MAX(ShiftColor) AS ShiftColor
FROM dbo.PrShiftGroup
GROUP BY ShfGrp_Cd, ShfGrp_Des, DefaultGrp
```

---

## 5. How the three screens connect

1. **Work Shift** defines times/breaks/`totaltime`.
2. **Shift Group** picks 1–2 shifts, stores denormalized group rows + color + default; uses `totaltime` for display/24h check and Start/End for overlap.
3. **MacShiftCal** paints each machine-day as Work(with group color) or Off; holidays force Off; copy replicates calendar across machines.
4. **Planning** (`PrShiftGroupBL.GetShitGroupInfo`) joins group→shift and builds work slots for WO capacity.

---

## 6. Blazor Server port blueprint

### 6.1 Suggested routes

```text
/master/work-shifts              → list
/master/work-shifts/new
/master/work-shifts/{id}         → edit / view

/master/shift-groups             → list
/master/shift-groups/new
/master/shift-groups/{id}

/master/machine-shift-cal        → calendar workspace
```

### 6.2 Services (pure C#, unit-testable)

1. **`ShiftTimeCalculator`**
   - `GetNetMinutes(start, end, breaks[])` with overnight +1440
   - `EncodeTotalTime(hh, mm)` → HH.MM float
   - `DecodeTotalTime(double)` → minutes

2. **`ShiftOverlapChecker`** — port `calculateTimeOver`

3. **`WorkShiftService`** — list / get / save / delete + duplicate-code check

4. **`ShiftGroupService`**
   - List (distinct by `ShfGrp_Cd` or view `vprd_ShiftGroups`)
   - Get by id (header + selected shift codes)
   - Save: upsert selected, delete removed, enforce single Default
   - Validate: 1–2 shifts, no overlap, total &lt; 1440

5. **`MachineCalendarService`**
   - Load year/machine → day cells
   - Preview/Generate from weekly rules + holidays + default group
   - SetDay, Save, CopyTo

6. Port **`PrShiftGroupBL.GetShitGroupInfo`** for planning consumers

### 6.3 Work Shift Blazor UI sketch

```text
WORK SHIFT
┌─────────────────────┬─────────────────────┐
│ Shift Code*         │ Description*        │
│ Start HH* MM*       │ End HH* MM*         │
│ (OT optional/hidden)│                     │
├─────────────────────┴─────────────────────┤
│ Break 1 From HH MM  │ To HH MM            │
│ Break 2 … Break 5   │                     │
│ Total Working HH MM [Get Total] (readonly)│
│ * 24-hour format                          │
└───────────────────────────────────────────┘
[Save] [Cancel → list]
```

Suggested model:

```csharp
public class WorkShiftEditModel
{
    public string ShiftCd { get; set; }
    public string ShiftDes { get; set; }
    public TimeOnly? Start { get; set; }
    public TimeOnly? End { get; set; }
    public TimeOnly? OtStart { get; set; }
    public (TimeOnly? From, TimeOnly? To)[] Breaks { get; set; } // length 5
    public int TotalHours { get; set; }       // computed
    public int TotalMinutesPart { get; set; } // computed 0-59
}
```

On Save: compute total first (same as current SAVE callback), then persist.

### 6.4 Shift Group Blazor UI sketch

```text
[Header]
  Code* | Default checkbox
  Description*
  Total HH/MM [Get Total]
  Color

[Shift picker grid — max 2]
  ☐ Code | Des | Start | End | Total

[Save] [Cancel → list]
```

On selection change: enforce max 2 → overlap check → optionally auto Get Total.  
On Save: transaction upsert/delete members + clear other defaults.

### 6.5 Machine Calendar Blazor UI sketch

Keep the same mental model: **edit draft year in memory → Preview applies rules → Save writes DB**. Copy is a separate confirm command.

WebForms → Blazor mapping:

| WebForms | Blazor |
|----------|--------|
| ListView + `?Type=EDIT` | List page + EditForm route; `EditContext` |
| CallbackPanel SAVE/GETTOTAL | Service methods + `StateHasChanged` |
| Grid checkbox select | Checkbox list / grid selection |
| Calendar DayCellPrepared | Custom month grid; bind background to color |
| Session DataTable | Scoped/circuit draft state until Save |

### 6.6 EF Core entity notes

```csharp
// PrShift — PK Shift_Cd
// PrShiftGroup — composite PK (ShfGrpCd, ShiftCd); denormalized header fields on each row
// PrShiftCalendar — (Dt, MachineCode); DateCd 'W'|'O'
// PrHoliday — UID, DateOff, Description, Year
```

---

## 7. Quirks checklist (preserve or fix deliberately)

1. **`PrShiftGroup` is not 1 header + child table** — header fields duplicated per shift row; calendar/combo use `GROUP BY ShfGrp_Cd`.
2. **`PrShift.totaltime` float = HH.MM**, not fractional hours — keep converters if sharing classic ERP DB.
3. Shift Group UI allows **max 2 shifts**; planning BL supports N.
4. Group save writes **per-shift minutes** into each row’s `TotalTime`, not the group sum.
5. Calendar **Save** does not wipe year first (adapter update); **Copy** does delete-then-insert.
6. Work Shift delete from list does not check Shift Group / calendar references.
7. Hour validation inconsistencies (code vs MaxValue) — normalize in Blazor.

---

## 8. Source file index

```text
ProductionPlan/ProdPlan/Master/
  WorkShift.aspx / WorkShift.aspx.cs
  WorkShiftView.aspx / WorkShiftView.aspx.cs
  ShiftGroup.aspx / ShiftGroup.aspx.cs
  WorkGroupView.aspx / WorkGroupView.aspx.cs
  MacShiftCal.aspx / MacShiftCal.aspx.cs

ERPClasses/BL/
  PrShiftGroupBL.cs
  PrShiftCalendarBL.cs
  PrCalendarBL.cs          (holidays helpers)

ERPClasses/Classes/
  CAdapter.cs              SetPrShift / SetPrShiftGroup / SetPrShiftCalendar
```

---

*Document generated from study of ERP 5.5 ProductionPlan master screens for Blazor Server port into `net10projects`.*

---

## 9. Blazor port status (Rev 3)

Implemented under `ErpWeb.Core/Planning` + UI masters:

- Work Shift / Shift Group / Company Calendar / Machine Calendar masters
- `IPrShiftGroupPlanningService`: `ShiftGroupDefinition` (date-less) vs `ShiftGroupInfo` (DateOnly-anchored WorkSlots)
- Exact 1440 circular coverage allowed; Off invariant `DateCd=O => ShfGrpCd=null`; lazy default resolve on machine generate

**Future WO scheduler hand-off:**

1. Load `PrShiftCalendar` for machine + date → if `DateCd == "O"` → no capacity
2. Else call `GetShiftGroupInfoAsync(DateOnly, ShfGrpCd)` → walk `WorkSlots` / `NetMinutes`
3. Do **not** use `NominalDaySegmentMinutes` (1440/count) as production capacity
