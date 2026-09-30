# Company Calendar & Machine Calendar — Study Guide for Blazor Port

**Source (legacy WebForms):** `c:\wincom\ERPV55\ERP_5.5\ProductionPlan\ProdPlan\Master\`  
**Primary files:**
- `CompanyCalEx.aspx` / `CompanyCalEx.aspx.cs`
- `MacShiftCal.aspx` / `MacShiftCal.aspx.cs`
- BL: `ERPClasses\BL\PrCalendarBL.cs`, `PrShiftCalendarBL.cs`, `PrShiftGroupBL.cs`
- Adapters: `ERPClasses\Classes\CAdapter.cs` (`SetPrCalendar`, `SetPrShiftCalendar`)
- Scheduling consumers: `WorkOrder.aspx.cs`, `CalendarHelper.cs` / `WorkCalendar.cs`, `PlanHelper.cs`

**Purpose of this doc:** explain how company/machine calendars work, how CRUD flows, why they matter for Work Order scheduling, and a concrete Blazor Server port checklist (DTOs, services, APIs, UI, parity tests).

---

## 1. Role in production scheduling

Company Calendar is the **company-wide work/off day master**. Saving it does not only write `PrCalendar` — it **fans out the same W/O pattern into every machine’s `PrShiftCalendar`**.

Classic Work Order scheduling walks **`PrShiftCalendar` per machine** day-by-day (`Date_Cd = 'W'` only). If a machine/year is missing, scheduling errors with:

- *Machine X not yet define in Calendar*
- *The Date dd-MM-yyyy is not yet defined in Calendar for Machine X*

```
┌─────────────────────┐     SAVE TO ALL MACHINES      ┌──────────────────────────┐
│  Company Calendar   │ ─────────────────────────────► │  PrShiftCalendar         │
│  PrCalendar (W/O)   │   + default ShfGrp_Cd          │  (per Machine × Date)    │
│  PrHoliday (named)  │                                └────────────┬─────────────┘
└─────────────────────┘                                             │
                                                                    ▼
                                                      Work Order / MRP scheduling
                                                      (only Date_Cd='W' counts)
```

| Layer | Table | Edited by | Used by |
|--------|--------|-----------|---------|
| Company calendar | `PrCalendar` | CompanyCalEx | AI scheduler, PlanHelper off-days, template for machines |
| Holidays | `PrHoliday` | CompanyCalEx + MacShiftCal (API import) | Applied onto calendar rows at Preview/Save; AI off-days |
| Machine calendar | `PrShiftCalendar` | CompanyCalEx (bulk) + MacShiftCal (per machine) | **Classic WO scheduling** |
| Default shift | `PrShiftGroup` (`DefaultGrp=1`) + `PrShift` | Shift master screens | Assigned on company→machine fan-out |

`Date_Cd` values:
- **`W`** = Work day (schedulable)
- **`O`** = Off day (skipped)

---

## 2. Database models

### 2.1 `PrCalendar`

| Column | Type | Notes |
|--------|------|--------|
| `Dt` | DateTime NOT NULL | Logical key by calendar date |
| `Date_Cd` | NVarChar(1) | `W` or `O` |
| `Created` / `Updated` | DateTime | |
| `UserID`, `CompCode`, `BranchCode`, `LocCode` | NVarChar(10) | Tenant/audit |

Upsert key in adapter: same year/month/day as original `Dt`.

### 2.2 `PrHoliday`

| Column | Type | Notes |
|--------|------|--------|
| `UID` | Int IDENTITY PK | |
| `DateOff` | DateTime | Off date |
| `Description` | NVarChar(50) | e.g. holiday name |
| `Year` | Int | Denormalized year |

CRUD is independent of `PrCalendar` until Preview/Save forces those dates to `O`.

### 2.3 `PrShiftCalendar`

| Column | Type | Notes |
|--------|------|--------|
| `Dt` | DateTime NOT NULL | |
| `Date_Cd` | NVarChar(1) | `W` / `O` |
| `ShfGrp_Cd` | NVarChar(10) | Shift group for that day |
| `MachineCode` | NVarChar(10) NOT NULL | |
| `Created` / `Updated`, audit | | |

Update key: date + `MachineCode` (shift group not required in WHERE for update).

### 2.4 Prerequisite

At least one `PrShiftGroup` row with `DefaultGrp = 1` (joined to `PrShift` for times). Company save throws: *No default shift group found.*

---

## 3. Company Calendar (`CompanyCalEx`) — UI & workflow

### 3.1 Screen controls

| Control | Purpose |
|---------|---------|
| `cmbYear` | Year (Now−3 … Now+6). Change → `LOAD` |
| Calendar (`Columns=3`, `Rows=4`) | Full-year view; red = off |
| `chkSetAction` + `rdbActions` | Click cell → Work or Off (session only) |
| `chkDays` | Weekly offs (Mon=1 … Sat=6, Sun=0). Default Sat+Sun |
| `gridHoliday` | CRUD `PrHoliday` for selected year |
| **UPDATE CALENDER** | Regenerate in-memory year |
| **SAVE TO ALL MACHINES** | Persist `PrCalendar` + recreate all `PrShiftCalendar` |

Hidden (legacy): `chkUpdMacCal` / `chkCrMacCal` — Save always uses recreate path.

### 3.2 Client callback map

| Client action | Callback | Server method |
|---------------|----------|---------------|
| Year change | `callPanel` `LOAD` | `LoadCal()` |
| Preview | `callPanel` `PREVIEW` | `Preview()` → `GetDates` |
| Cell paint | `callBack` `SETDATE:date:OFF\|WORK` | `SetDate` |
| Save | `callBack` `SAVE` | `Save()` → `PrCalendarBL.SaveWithMachine(..., true)` |

### 3.3 Session keys

| Key | Content |
|-----|---------|
| `CalendarDTCY` | Working `DataTable` of `PrCalendar` for year |
| `HolidayDT` | Holidays for year |
| `CalYearCY` | Selected year int |

In Blazor: replace Session with scoped page state / edit model (do not rely on ASP.NET Session).

---

## 4. Company Calendar — CRUD logic (port this)

### 4.1 Read — `LOAD`

```
GetHoliday(year)  → SELECT * FROM PrHoliday WHERE year(DateOff)=@year
GetCalendar(year) → SELECT * FROM PrCalendar WHERE year(Dt)=@year
```

Bind calendar UI from rows where `Date_Cd='O'` (red).

### 4.2 Holiday CRUD

Direct SQL (legacy `SqlDataSource`):

```sql
INSERT INTO PrHoliday (DateOff, Description, Year) VALUES (...)
UPDATE PrHoliday SET DateOff=..., Description=..., Year=... WHERE UID=...
DELETE FROM PrHoliday WHERE UID=...
SELECT * FROM PrHoliday WHERE year(DateOff)=@year
```

Holidays do **not** update `PrCalendar` until Preview or Save.

### 4.3 Preview / Generate — `GetDates(year)` + `SetHolidays`

Algorithm (must match for parity):

1. Load existing `PrCalendar` for year (may be empty schema/rows).
2. Build string of selected weekday offs from `chkDays` values (`DayOfWeek` ints: Sun=0 … Sat=6), joined with `;`.
3. For each day Jan 1–Dec 31:
   - If no row: insert `Dt`, `Date_Cd` = `O` if weekday in offs else `W`, set `Created`.
   - If row exists: overwrite `Date_Cd` from weekday pattern, set `Updated`.
4. `SetHolidays`: for each `PrHoliday` that year, set matching calendar row `Date_Cd = 'O'`.
5. Keep result in memory only (Preview does not write DB).

Default weekly offs (`PlanningUtils.SetWeeklyOffday`): if nothing selected, select **Sat + Sun**.

### 4.4 Cell toggle — `SetDate`

Find row by exact date → set `Date_Cd` to `O` or `W`. Memory only until Save.

### 4.5 Save — `Save()` + `PrCalendarBL.SaveWithMachine`

**Guards:**
1. If year already exists among `SELECT DISTINCT year(dt) FROM PrShiftCalendar` → error: *There is already CALENDAR set for this year.*  
   (Guard uses **machine calendar years**, not `PrCalendar`.)
2. Calendar DataTable / year must be present.
3. Re-apply `SetHolidays` before persist.

**Always:**

```csharp
PrCalendarBL.SaveWithMachine(dtCalendar, year, reCreateMacCal: true, out errmsg);
```

**Transaction (`SaveWithMachine`):**

1. `PopulateShiftMac(dt, year, sqlTrans)` with recreate:
   - Machines: `SELECT DISTINCT Machine_Cd FROM PrMachine`
   - Default group: `PrShiftGroupBL.GetMainGroup()` → first `ShfGrp_Cd`
   - Per machine: `DELETE FROM PrShiftCalendar WHERE Year(Dt)=@year AND MachineCode=@mac`
   - Per company calendar day: INSERT shift row copying `Dt`, `Date_Cd`, default `ShfGrp_Cd`, `MachineCode`, audit fields
2. Upsert `PrCalendar` (Insert/Update via adapter)
3. Insert all new `PrShiftCalendar` rows
4. Commit / rollback

**Not on this screen:** delete year; per-machine exceptions (use Machine Calendar).

### 4.6 Alternate BL methods (legacy, unused by current UI)

| Method | Behavior |
|--------|----------|
| `Save(...)` | Company table only |
| `SaveWithMachine(..., false)` | Update existing machine rows’ `Date_Cd` / insert missing (no wipe) |
| `SaveWithMachine(..., true)` | Wipe year per machine + full insert (**current UI**) |

---

## 5. Machine Calendar (`MacShiftCal`) — related screen

Port after Company Calendar. Overrides / copies per machine.

### 5.1 Differences from Company Calendar

| | CompanyCalEx | MacShiftCal |
|--|--------------|-------------|
| Target table | `PrCalendar` + all `PrShiftCalendar` | One machine’s `PrShiftCalendar` |
| Load | Year only | Year + Machine |
| Shift group | Forced to default on fan-out | Per-day `ShfGrp_Cd` via combo when setting Work |
| Weekly pattern | Off days only | Off days **and** Work days (assigns selected shift group) |
| Save | Block if year exists in any machine cal | Upsert that machine’s year |
| Copy | N/A | Copy year from machine A → B or ALL |
| Holiday import | Manual grid | Calendarific API by MY state + bulk insert |

### 5.2 MacShiftCal generate rules (`GetDates`)

For each day:

1. Weekday in **off** list → `Date_Cd=O`, `ShfGrp_Cd=""`
2. Else weekday in **work** list → `Date_Cd=W`, `ShfGrp_Cd=selected cmbShiftGrp`
3. Else (new row) → `Date_Cd=W`, `ShfGrp_Cd=default group`; (existing row) → leave pattern alone, only touch `Updated`
4. Then force holidays to `O`

### 5.3 Copy calendar

For target machine(s):

1. Ensure source has rows for year
2. `DELETE` target year for that machine
3. `INSERT … SELECT` from source, replacing `MachineCode`

Skip when source == target. `ALL MACHINE` loops all `PrMachine`.

### 5.4 Calendarific holiday import (Mac only)

- Config: `calendarific_APIKey`
- URL: `https://calendarific.com/api/v2/holidays?api_key=…&country=my&year={year}`
- Filter by state name or `ALL`
- User selects rows → insert into `PrHoliday`
- Still need Preview/Save on calendars to paint those dates as off

---

## 6. How Work Order scheduling consumes calendars

### 6.1 Classic WO (`WorkOrder.aspx.cs`)

Per machine / cycle-time back-schedule loop:

1. `SELECT * FROM PrShiftCalendar WHERE MachineCode=@mac` — empty → hard error.
2. Candidate day must exist in that table.
3. Only days with `Date_Cd='W'` consume working minutes.
4. Then apply shift group minutes / preventive downtime (`PrPreventive`).

**Implication for Blazor:** WO scheduling service must read `PrShiftCalendar`, not only `PrCalendar`. Company save must successfully fan out.

### 6.2 AI / newer scheduler (`CalendarHelper` → `WorkCalendar`)

`InitCalendar()`:

1. `GetWorkDays()` — distinct weekdays from future `PrCalendar` where `Date_Cd='W'` (fallback Mon–Sat)
2. `GetOffDays()` — future `PrCalendar` `O` ∪ `PrHoliday`
3. Load default shift group, shifts, maintenance

`WorkCalendar.BuildSegments` skips days not in `WorkingDays` or in `Holidays`.

### 6.3 PlanHelper

- `GetCompOffDays()` → `PrCalendar` where `Date_Cd=="O"`
- `GetOffDays(machine)` → `PrShiftCalendar` offs for machine
- Used to nudge start dates past off days

---

## 7. Blazor Server port — recommended architecture

### 7.1 Projects / layers

```
Erp.Domain / Entities
  PrCalendar, PrHoliday, PrShiftCalendar, PrMachine, PrShiftGroup, PrShift

Erp.Application / Production / Calendar
  ICompanyCalendarService
  IHolidayService
  IMachineCalendarService
  DTOs + validators

Erp.Infrastructure
  EF Core / Dapper repositories matching tables above

Erp.Blazor (Server)
  Pages/Production/Master/CompanyCalendar.razor
  Pages/Production/Master/MachineShiftCalendar.razor
```

### 7.2 DTOs

```csharp
public enum DateCode : byte { Work = 0 /* W */, Off = 1 /* O */ }

public sealed class CalendarDayDto
{
    public DateOnly Date { get; set; }
    public DateCode Code { get; set; }
    public string? ShiftGroupCode { get; set; } // machine calendar only
}

public sealed class HolidayDto
{
    public int? Uid { get; set; }
    public DateOnly DateOff { get; set; }
    public string Description { get; set; } = "";
    public int Year => DateOff.Year;
}

public sealed class CompanyCalendarPreviewRequest
{
    public int Year { get; set; }
    public IReadOnlyList<DayOfWeek> WeeklyOffDays { get; set; } = Array.Empty<DayOfWeek>();
    /// <summary>Optional cell overrides after weekly pattern + holidays.</summary>
    public IReadOnlyDictionary<DateOnly, DateCode>? DayOverrides { get; set; }
}

public sealed class CompanyCalendarSaveRequest : CompanyCalendarPreviewRequest
{
    /// <summary>Parity default: true (wipe + recreate all machine years).</summary>
    public bool RecreateMachineCalendars { get; set; } = true;
}

public sealed class CompanyCalendarYearState
{
    public int Year { get; set; }
    public IReadOnlyList<CalendarDayDto> Days { get; set; } = Array.Empty<CalendarDayDto>();
    public IReadOnlyList<HolidayDto> Holidays { get; set; } = Array.Empty<HolidayDto>();
    public bool MachineYearAlreadyExists { get; set; }
}
```

Persist `Date_Cd` as `"W"` / `"O"` in DB; map in repository.

### 7.3 Service interfaces

```csharp
public interface IHolidayService
{
    Task<IReadOnlyList<HolidayDto>> GetByYearAsync(int year, CancellationToken ct = default);
    Task<HolidayDto> UpsertAsync(HolidayDto dto, CancellationToken ct = default);
    Task DeleteAsync(int uid, CancellationToken ct = default);
}

public interface ICompanyCalendarService
{
    Task<CompanyCalendarYearState> LoadAsync(int year, CancellationToken ct = default);
    /// <summary>In-memory generate: weekly offs + holidays + overrides. No DB write.</summary>
    Task<CompanyCalendarYearState> PreviewAsync(CompanyCalendarPreviewRequest req, CancellationToken ct = default);
    Task SaveAsync(CompanyCalendarSaveRequest req, CancellationToken ct = default);
    Task<bool> MachineYearExistsAsync(int year, CancellationToken ct = default);
}

public interface IMachineCalendarService
{
    Task<CompanyCalendarYearState> LoadAsync(string machineCode, int year, CancellationToken ct = default);
    Task<CompanyCalendarYearState> PreviewAsync(/* machine + offs + work days + shift group */, CancellationToken ct = default);
    Task SaveAsync(string machineCode, int year, IReadOnlyList<CalendarDayDto> days, CancellationToken ct = default);
    Task CopyYearAsync(string fromMachine, string toMachineOrAll, int year, CancellationToken ct = default);
}
```

### 7.4 Core algorithm (shared, unit-testable)

```csharp
public static class CalendarGenerator
{
    public static List<CalendarDayDto> GenerateCompanyYear(
        int year,
        IEnumerable<DayOfWeek> weeklyOffs,
        IEnumerable<DateOnly> holidayDates,
        IReadOnlyDictionary<DateOnly, DateCode>? overrides = null)
    {
        var offSet = weeklyOffs.ToHashSet();
        var holidaySet = holidayDates.ToHashSet();
        var days = new List<CalendarDayDto>();
        for (var d = new DateOnly(year, 1, 1); d.Year == year; d = d.AddDays(1))
        {
            var code = offSet.Contains(d.DayOfWeek) || holidaySet.Contains(d)
                ? DateCode.Off : DateCode.Work;
            if (overrides != null && overrides.TryGetValue(d, out var o))
                code = o;
            // Holidays always win to Off if applying after overrides (legacy: holidays after weekly, cell after that)
            days.Add(new CalendarDayDto { Date = d, Code = code });
        }
        return days;
    }
}
```

**Parity note on override order (legacy):**

1. Weekly pattern (Preview)
2. Holidays force `O` (Preview/Save)
3. Cell clicks mutate after Preview (session)

On Save, holidays are applied again (`SetHolidays`) — so a cell set to Work on a holiday date may be forced back to Off unless you change behavior intentionally.

Recommend documenting product choice:

- **Parity:** holiday always wins on Save.
- **Better UX:** warn if override conflicts with holiday.

### 7.5 Save transaction (EF Core sketch)

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);

// Upsert PrCalendar for year (merge by Date)
foreach (var day in days) { /* insert or update Date_Cd */ }

if (recreateMachineCalendars)
{
    var machines = await db.PrMachines.Select(m => m.Machine_Cd).Distinct().ToListAsync(ct);
    var defaultGrp = await GetDefaultShiftGroupCodeAsync(ct)
        ?? throw new InvalidOperationException("No default shift group found.");

    foreach (var mac in machines)
    {
        await db.PrShiftCalendars
            .Where(x => x.Dt.Year == year && x.MachineCode == mac)
            .ExecuteDeleteAsync(ct);

        db.PrShiftCalendars.AddRange(days.Select(d => new PrShiftCalendar
        {
            Dt = d.Date.ToDateTime(TimeOnly.MinValue),
            Date_Cd = d.Code == DateCode.Off ? "O" : "W",
            ShfGrp_Cd = defaultGrp,
            MachineCode = mac,
            Created = DateTime.Now,
            // UserID / CompCode / BranchCode / LocCode from session
        }));
    }
}

await db.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
```

### 7.6 Blazor UI checklist — Company Calendar page

- [ ] Year dropdown (Now−3 … Now+6)
- [ ] Full-year calendar grid (12 months); off = red
- [ ] Weekly off checkboxes (default Sat+Sun)
- [ ] Holiday grid: add / edit / delete (`PrHoliday`)
- [ ] Optional “set day as Work/Off” paint mode
- [ ] Preview button (no save)
- [ ] Save button with confirm: *Generate and save / push to all machines*
- [ ] Disable Save if `MachineYearAlreadyExists` (parity) **or** offer explicit “recreate year” admin action
- [ ] Loading indicator during save (can be slow: machines × 365)
- [ ] Error toasts: no year, no default shift group, DB errors
- [ ] Auth / CompCode / BranchCode / LocCode from current user context

### 7.7 Blazor UI checklist — Machine Calendar page (phase 2)

- [ ] Year + Machine selectors
- [ ] Shift group combo + color legend
- [ ] Off-day + Work-day weekly checkboxes
- [ ] Preview / Save for one machine
- [ ] Copy to machine / ALL
- [ ] Optional Calendarific import (feature-flag + API key)

### 7.8 Optional minimal APIs (if separating UI)

```
GET  /api/prod/holidays?year=2026
POST /api/prod/holidays
PUT  /api/prod/holidays/{uid}
DELETE /api/prod/holidays/{uid}

GET  /api/prod/company-calendar/{year}
POST /api/prod/company-calendar/{year}/preview
POST /api/prod/company-calendar/{year}/save

GET  /api/prod/machine-calendar/{machine}/{year}
POST /api/prod/machine-calendar/{machine}/{year}/preview
POST /api/prod/machine-calendar/{machine}/{year}/save
POST /api/prod/machine-calendar/copy
```

For Blazor Server, calling application services directly from the page is fine; APIs help if mobile/other clients share logic.

---

## 8. Parity test plan

### 8.1 Unit tests — `CalendarGenerator`

| # | Case | Expect |
|---|------|--------|
| U1 | Year 2026, offs = Sat+Sun, no holidays | All Sat/Sun = O; others = W; 365 rows |
| U2 | Leap year 2024 | 366 rows; Feb 29 present |
| U3 | Holiday on a weekday | That date = O |
| U4 | Holiday on Saturday | Still O |
| U5 | Override Work on Sat then holidays applied (parity Save) | Holiday wins → O |
| U6 | Empty offs → apply default Sat+Sun | Same as U1 |

### 8.2 Integration — Holiday CRUD

| # | Case | Expect |
|---|------|--------|
| H1 | Insert holiday | Row in `PrHoliday`; calendar not yet changed |
| H2 | Preview after H1 | Matching `PrCalendar` day (in preview model) = O |
| H3 | Delete holiday + Preview | Day follows weekly pattern again |

### 8.3 Integration — Company Save

| # | Case | Expect |
|---|------|--------|
| S1 | Fresh year, N machines | `PrCalendar` = 365/366; `PrShiftCalendar` = N × days; all `ShfGrp_Cd` = default |
| S2 | Save same year again (parity) | Rejected: year already in `PrShiftCalendar` |
| S3 | No `DefaultGrp=1` | Error message |
| S4 | Machine with zero prior rows | Created after company save |
| S5 | Spot-check Date_Cd | Random Sat = O; midweek non-holiday = W on both tables |

### 8.4 Scheduling smoke

| # | Case | Expect |
|---|------|--------|
| W1 | WO schedule before calendar | Error: machine not in calendar |
| W2 | After company save | Schedule progresses; skips `O` days |
| W3 | Mac override: set weekday to O for one machine | Only that machine skips the day |
| W4 | Copy machine A→B | B year matches A |

### 8.5 Manual UI checklist

1. Open Company Calendar → select next empty year → set offs Sat/Sun → add one holiday → Preview (red days match) → Save.
2. Open Machine Calendar → same year/machine → verify colors/shift group.
3. Run one WO calculate start/complete; confirm dates skip offs/holidays.

---

## 9. Implementation order (recommended)

1. **Entities + repos** for `PrHoliday`, `PrCalendar`, `PrShiftCalendar`, `PrMachine`, `PrShiftGroup`
2. **`CalendarGenerator` + unit tests**
3. **`IHolidayService` + Blazor holiday grid**
4. **`ICompanyCalendarService` Preview + UI**
5. **`SaveWithMachine` transaction + Save UI**
6. **Wire WO scheduling to `PrShiftCalendar`** (verify existing Blazor scheduler)
7. **`IMachineCalendarService`** (override + copy)
8. **Optional:** Calendarific import, soft “recreate year” admin

---

## 10. Behavioral quirks — decide consciously

| Quirk | Legacy behavior | Port recommendation |
|-------|-----------------|---------------------|
| Re-save year | Blocked if year exists in `PrShiftCalendar` | Keep for parity; add admin “Force recreate” later |
| Save always recreates machines | `reCreateMacCal=true` | Same default; optional update-only later |
| Holiday alone | Not painted until Preview/Save | Same |
| Default weekly off | Sat + Sun | Same |
| Weekday ints | `DayOfWeek` (Sun=0) | Use `DayOfWeek` enum in C# |
| Cell vs holiday | Save re-applies holidays | Document; holiday wins |
| SQL string concat | Widespread in legacy | Use parameters / EF |
| Session `DataTable` | Edit buffer | Scoped Blazor state / DTOs |
| Date filter culture | WO uses `dd/MM/yyyy` / `dd-MM-yyyy` in `DataTable.Select` | Prefer `DateOnly` / invariant compares in new code |

---

## 11. Source file index

| Concern | Path |
|---------|------|
| Company UI | `ProductionPlan\ProdPlan\Master\CompanyCalEx.aspx(.cs)` |
| Machine UI | `ProductionPlan\ProdPlan\Master\MacShiftCal.aspx(.cs)` |
| Company BL | `ERPClasses\BL\PrCalendarBL.cs` |
| Machine BL | `ERPClasses\BL\PrShiftCalendarBL.cs` |
| Default shift | `ERPClasses\BL\PrShiftGroupBL.cs` → `GetMainGroup()` |
| Adapters | `ERPClasses\Classes\CAdapter.cs` → `SetPrCalendar`, `SetPrShiftCalendar` |
| Holiday adapter (prod) | `ProductionPlan\BL\CAdapter.cs` → `SetPrHoliday` |
| Weekly defaults | `ProductionPlan\Helper\PlanningUtils.cs` |
| WO scheduling | `ProductionPlan\ProdPlan\WorkOrder.aspx.cs` (~3088+) |
| AI calendar | `ProductionPlan\ProdPlan\AIMacCalculate\CalendarHelper.cs`, `WorkCalendar.cs` |
| Off-day helpers | `ProductionPlan\Helper\PlanHelper.cs` → `GetCompOffDays`, `GetOffDays` |

---

## 12. One-page summary for implementers

1. Maintain **`PrHoliday`** (named offs) and generate a full-year **`PrCalendar`** (`W`/`O`) from weekly offs + holidays + optional cell overrides.
2. On company **Save**, upsert `PrCalendar` and **delete+rebuild** that year’s **`PrShiftCalendar` for every machine** with the **default shift group**.
3. Work Order scheduling must use **`PrShiftCalendar.Date_Cd == 'W'`** per machine.
4. Machine Calendar is for **exceptions and copy**; Company Calendar is the bulk bootstrap.
5. Port logic into a pure `CalendarGenerator` + transactional `CompanyCalendarService`; Blazor UI is a thin editor over Preview/Save.

---

*Generated from legacy ERP 5.5 ProductionPlan study for net10 Blazor Server ERP port.*
