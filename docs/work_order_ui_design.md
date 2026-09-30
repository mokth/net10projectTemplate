# Work Order UI Design Specification

> Extracted from the supplied Work Order screenshots. This document focuses on the visible UI structure, controls, tabs, grid columns, sample values, and apparent interaction states so it can be reused as a reference for rebuilding the page in a modern ERP/Blazor application.

## 1. Page Overview

**Page title:** `WORK ORDER`

The screen is divided into four main areas:

1. **Work Order Header / General Information**
2. **Processing Action Bar**
3. **Detail Tabs**
   - Center
   - Process
   - BOM
   - Machine
   - Labour
4. **Page-Level Actions**
   - Save
   - Cancel

---

## 2. Work Order Header

The header uses a two-column layout.

### 2.1 Left Column

| Field | Required | Control Type | Sample Value | Notes |
|---|---:|---|---|---|
| Transaction Date | Yes | Date picker | `28/07/2026` | Calendar/drop-down button on the right |
| Work Order | Yes | Text box | `VF26070002` | Followed by a small type/select control showing `WO` and an information icon |
| Delivery Req No | No | Lookup/text box | blank | Lookup/search icon and information icon are shown |
| Std Batch Size | Yes | Numeric input | `1` | Paired with Std UOM |
| Std UOM | No | Text/display field | `BAG` | Shown to the right of Std Batch Size |
| Delivery Qty | Yes | Numeric input | `0` |  |
| Desire Qty | No | Numeric input | `5000` |  |
| Desire Consignment Qty | No | Numeric input | `0` |  |
| Schedule Qty | No | Numeric input | `5000` | Small utility/calculation button at the right |

### 2.2 Right Column

| Field | Required | Control Type | Sample Value | Notes |
|---|---:|---|---|---|
| Mode | No | Read-only/display field | `EDIT` | Blue text |
| Status | No | Read-only/display field | `IN PROGRESS` | Blue text |
| Product Def Code | Yes | Lookup/text box | `1VF-WCVF002-01P(BH)` | Lookup icon and information icon |
| Description | No | Multiline/read-only field | `CB2H PP LUNCH BOX-BH (WITH HOLE-BAG)` | Two-line-capable text area |
| Start Date | Yes | Date/time picker | `28/07/2026 08:00` | Calendar button; paired with checkbox **Start From Start Date** |
| Completed Date | Yes | Date/time picker | `28/07/2026 09:23` | Calendar button |
| Remark | No | Multiline text area | blank | Paired with checkbox **Remark From Product Definition** |

### 2.3 Header Checkboxes

- [x] **Start From Start Date**
- [ ] **Remark From Product Definition**

---

## 3. Processing Action Bar

Placed immediately below the header.

| Action | Visible State | Purpose / Interpretation |
|---|---|---|
| Process | Disabled | Initial processing/generation action; disabled in the captured state |
| Re Process | Enabled | Rebuild/recalculate process details |
| Normalize Quantity | Enabled | Normalize or recalculate work-order quantities |

---

## 4. Detail Navigation Tabs

A horizontal tab strip is used across all detail views.

| Tab | Purpose |
|---|---|
| Center | Work-center routing / work-center output |
| Process | Processes belonging to each work center |
| BOM | Materials required by process/work center |
| Machine | Machine assignment and timing |
| Labour | Labour assignment and labour cost |

Active tabs use a blue gradient/highlight. Inactive tabs use a light grey/white background.

---

# 5. CENTER Tab

## 5.1 Grid Layout

The Center tab is the high-level production routing view.

| Column | Sample Value | Notes |
|---|---|---|
| Add / Action | `+` / delete icon | Add row at header; delete action per row |
| Work Center | `WC1` | Work-center code |
| Seq No | `10` | Execution sequence |
| Center Product | `1VF-WCVF002-01P(BH)` | Output/product of the work center |
| Description | `CB2H PP LUNCH BOX BH (WITH HOLE BAG)` | Product description |
| Class | `VF` | Product/work-center class |
| Pack Size | `1` | Pack/output size |
| Std UOM | `BAG` | Standard unit of measure |
| Schedule Qty | `5000` | Planned quantity |
| Start Date | `28/07/2026 08:00` | Planned/actual start |
| Complete Date | `28/07/2026 09:23` | Planned/actual completion |

### 5.2 Center Tab Sample Row

| Work Center | Seq No | Center Product | Description | Class | Pack Size | Std UOM | Schedule Qty | Start Date | Complete Date |
|---|---:|---|---|---|---:|---|---:|---|---|
| WC1 | 10 | 1VF-WCVF002-01P(BH) | CB2H PP LUNCH BOX BH (WITH HOLE BAG) | VF | 1 | BAG | 5000 | 28/07/2026 08:00 | 28/07/2026 09:23 |

### 5.3 Center Grid Footer Actions

- **Preview Changes**
- **Save Changes**
- **Cancel Changes**

---

# 6. PROCESS Tab

## 6.1 Grid Layout

| Column | Sample Value | Notes |
|---|---|---|
| Add / Action | `+` / delete icon | Add/delete process row |
| Work Center | `WC1` | Parent work center |
| Center Product | `1VF-WCVF002-01P(BH)` | Work-center output product |
| Process Code | `PROCESS1` | Process identifier |
| Seq No | `10` | Process execution sequence |
| Setup Lost Qty | `0` | Expected setup loss/scrap quantity |
| Operation Lost | `0` | Expected operating loss/scrap |
| Final Process | Checked | Marks process as the final process for the route/work center |
| Start Date | `28/07/2026 08:00` | Process start date/time |
| End Date | `28/07/2026 09:23` | Process end date/time |
| Remark | blank | Free-text remark |

## 6.2 Process Tab Sample Row

| Work Center | Center Product | Process Code | Seq No | Setup Lost Qty | Operation Lost | Final Process | Start Date | End Date | Remark |
|---|---|---|---:|---:|---:|---|---|---|---|
| WC1 | 1VF-WCVF002-01P(BH) | PROCESS1 | 10 | 0 | 0 | Yes | 28/07/2026 08:00 | 28/07/2026 09:23 | |

## 6.3 Process Grid Footer Actions

- **Preview Changes**
- **Save Changes**
- **Cancel Changes**

---

# 7. BOM Tab

## 7.1 Grid Layout

| Column | Sample Value | Notes |
|---|---|---|
| Add / Action | `+` / delete icon | Add/delete BOM row |
| Work Center | `WC1` | Work center consuming the material |
| Center Product | `1VF-WCVF002-01P(BH)` | Work-center output product |
| Process Code | `PROCESS1` | Process consuming the material |
| BOM Default | Checked | Indicates default BOM material |
| WIP Default | Unchecked | Indicates default WIP/intermediate material |
| Raw Item Code | `2CS-00001` | Raw material/item code |
| Item Desc | `PP-CLEAR SHEET (B) M13.1 ROLL DIA MAX-10 0.30MM X 890MM` | Material description |
| Std Qty | `5,000.0000` | Standard required quantity |
| UOM | `KG` | Material unit of measure |
| Tolerance (%) | `0` | Allowed material variance |
| Warehouse | `WH-B` | Source warehouse |

## 7.2 BOM Tab Sample Row

| Work Center | Center Product | Process Code | BOM Default | WIP Default | Raw Item Code | Item Desc | Std Qty | UOM | Tolerance (%) | Warehouse |
|---|---|---|---|---|---|---|---:|---|---:|---|
| WC1 | 1VF-WCVF002-01P(BH) | PROCESS1 | Yes | No | 2CS-00001 | PP-CLEAR SHEET (B) M13.1 ROLL DIA MAX-10 0.30MM X 890MM | 5,000.0000 | KG | 0 | WH-B |

## 7.3 BOM Grid Footer Actions

- **Preview Changes**
- **Save Changes**
- **Cancel Changes**

---

# 8. MACHINE Tab

## 8.1 Grid Layout

| Column | Sample Value | Notes |
|---|---|---|
| Add / Action | `+` / delete icon | Add/delete machine row |
| Work Center | `WC1` | Parent work center |
| Center Product | `1VF-WCVF002-01P(BH)` | Work-center output product |
| Process Code | `PROCESS1` | Parent process |
| Default | Checked | Default machine for the process |
| Mac Code | `MC01` | Machine code |
| Mac Name | `MACHINE01` | Machine name |
| Cycle Time (Min) | `83` | Processing/cycle time in minutes |
| Conversion Time | `0` | Additional conversion/change time |
| Startup Time | `0` | Machine startup/setup time |
| Queue Time | `0` | Waiting/queue time |
| SeqNo | `10` | Machine sequence number |
| Start Date | `28/07/2026 08:00` | Machine start date/time |
| End Date | `28/07/2026 09:23` | Machine end date/time |

## 8.2 Machine Tab Sample Row

| Work Center | Center Product | Process Code | Default | Mac Code | Mac Name | Cycle Time (Min) | Conversion Time | Startup Time | Queue Time | SeqNo | Start Date | End Date |
|---|---|---|---|---|---|---:|---:|---:|---:|---:|---|---|
| WC1 | 1VF-WCVF002-01P(BH) | PROCESS1 | Yes | MC01 | MACHINE01 | 83 | 0 | 0 | 0 | 10 | 28/07/2026 08:00 | 28/07/2026 09:23 |

## 8.3 Machine Grid Footer Actions

- **Preview Changes**
- **Save Changes**
- **Cancel Changes**

---

# 9. LABOUR Tab

## 9.1 Grid Layout

| Column | Notes |
|---|---|
| Add / Action | `+` button for a new labour row |
| Work Center | Parent work center |
| Center Product | Work-center output product |
| Process Code | Parent process |
| Mac Code | Related machine code |
| Labour Code | Labour/employee/resource code |
| Labour Cost | Labour cost/rate |

### 9.2 Empty State

The captured screen contains no labour records and displays:

> `NO DATA TO DISPLAY`

## 9.3 Labour Grid Footer Actions

- **Preview Changes**
- **Save Changes**
- **Cancel Changes**

---

# 10. Page-Level Actions

At the bottom of the Work Order page:

| Button | Purpose |
|---|---|
| Save | Save the complete Work Order |
| Cancel | Cancel/exit the current changes |

---

# 11. Visible UI Behaviour / Interaction Model

The screenshots suggest the following interaction model:

1. The **header** holds the work-order identity, product definition, quantities, status, and planned dates.
2. **Process / Re Process / Normalize Quantity** are high-level commands affecting generated routing/detail data.
3. The five tabs represent a hierarchy:

```text
Work Order
└── Center
    └── Process
        ├── BOM
        ├── Machine
        └── Labour
```

4. Each detail tab uses an editable grid with:
   - Add button in the first header cell.
   - Delete button per existing row.
   - Inline editable/read-only cells.
   - `Preview Changes`, `Save Changes`, and `Cancel Changes` at grid level.
5. The overall page then provides a final `Save` / `Cancel` action.
6. Date/time values are shown using Malaysian-style `dd/MM/yyyy HH:mm` formatting.
7. Required fields are marked with an asterisk `*`.
8. Lookup-enabled fields use small search/lookup icons and, in some places, an adjacent information icon.

---

# 12. Suggested Logical Data Relationship

Based strictly on the UI structure, the page can be represented as:

```text
WorkOrderHeader
├── WorkOrderCenters[]
│   └── WorkOrderProcesses[]
│       ├── WorkOrderBOMs[]
│       ├── WorkOrderMachines[]
│       └── WorkOrderLabours[]
```

Possible parent keys shown by the UI:

```text
WorkOrder
  -> WorkCenter
      -> CenterProduct
          -> Process
              -> BOM
              -> Machine
                  -> Labour
```

> Note: Labour includes `Mac Code` in the grid, so labour may optionally be tied to a specific machine under the process rather than only to the process itself.

---

# 13. Compact Wireframe

```text
+----------------------------------------------------------------------------------+
| WORK ORDER                                                                       |
+--------------------------------------+-------------------------------------------+
| Transaction Date * [date]            | Mode              [EDIT]                  |
| Work Order *       [code] [WO]       | Status            [IN PROGRESS]           |
| Delivery Req No    [lookup]          | Product Def Code* [lookup]                |
| Std Batch Size *   [1]   UOM [BAG]   | Description       [....................]   |
| Delivery Qty *     [0]   Desire[5000]| Start Date *      [datetime] [x Start...] |
| Desire Consign Qty [0]   Sched [5000]| Completed Date *  [datetime]              |
|                                      | Remark            [....................]   |
|                                      | [ ] Remark From Product Definition         |
+----------------------------------------------------------------------------------+
| [Process] [Re Process] [Normalize Quantity]                                      |
+----------------------------------------------------------------------------------+
| [CENTER] [PROCESS] [BOM] [MACHINE] [LABOUR]                                     |
+----------------------------------------------------------------------------------+
| Editable Detail Grid                                                             |
| ...                                                                              |
|                                              [Preview] [Save Changes] [Cancel]    |
+----------------------------------------------------------------------------------+
| [Save] [Cancel]                                                                  |
+----------------------------------------------------------------------------------+
```

---

# 14. UI Style Observed

- Traditional ERP/web-form visual style.
- Pale blue/grey form background.
- Cyan/blue gradient used for active tabs and grid headers.
- Compact spacing intended for desktop/high-density data entry.
- Blue hyperlink-style values for selectable codes.
- Green `+` icon for add actions.
- Red `X` icon for delete actions.
- Grey/white rounded buttons for change management.
- Grid-centric editing rather than card/dialog-centric editing.

---

# 15. Rebuild Notes for a Modern Blazor ERP

For a modern implementation, preserve the same information hierarchy even if the visual design is upgraded:

```text
Header / General
    ↓
Routing Centers
    ↓
Processes per Center
    ↓
Materials + Machines + Labour per Process
```

The most important behaviours to retain are:

- Parent-child filtering between tabs.
- Sequence control for Center, Process, and Machine.
- Final Process flag.
- Default BOM / WIP indicators.
- Default machine indicator.
- Production timing fields.
- Material warehouse and tolerance.
- Quantity normalization/reprocessing.
- Separate detail-level changes from final Work Order save.

