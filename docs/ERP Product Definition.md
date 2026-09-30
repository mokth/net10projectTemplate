# ERP Product Definition – Production Routing and Execution Rules

## 1. Purpose

The Product Definition describes **how a finished product is manufactured**.

It defines:

* Production Work Centers / production stages
* Work Center execution sequence
* Processes inside each Work Center
* Process execution sequence
* Raw material / component consumption
* Machine requirements
* Labour requirements
* Intermediate WIP output
* Final finished-goods output
* Dependencies between Work Centers
* Parallel and sequential production flow

The structure is:

```text
Product
 └─ Work Center
     ├─ Process
     │   ├─ BOM / Material
     │   ├─ Machine
     │   │   └─ Labour
     │   └─ Process timing / costing
     └─ Work Center Output
```

---

# 2. Core Production Rules

## 2.1 Work Center Sequence

Every Work Center has a `Sequence` / `Segment` number.

Example:

```text
WC1 Seg 1
WC2 Seg 2
WC3 Seg 3
```

Different sequence numbers mean the Work Centers execute **sequentially**.

```text
WC1
 ↓
WC2
 ↓
WC3
```

WC2 cannot start until WC1 is completed.

WC3 cannot start until WC2 is completed.

---

## 2.2 Parallel Work Centers

Work Centers having the **same sequence number** belong to the same production stage and may execute in parallel.

Example:

```text
WC1 Seg 1
WC2 Seg 1
WC3 Seg 2
```

Execution becomes:

```text
        ┌─ WC1 ─┐
Start ──┤       ├──> WC3
        └─ WC2 ─┘
```

WC1 and WC2 may start at the same time.

WC3 can start only when **all required Work Centers in Seg 1 are completed**.

Therefore:

```text
WC1 Finished
      +
WC2 Finished
      ↓
WC3 Can Start
```

This is effectively a **production synchronization / join point**.

---

# 3. Process Sequence Inside a Work Center

Each Work Center contains one or more Processes.

Processes also have their own sequence.

Example:

```text
Work Center WC1

Process A Seg 1
Process B Seg 2
Process C Seg 3
```

Normal execution is:

```text
Process A
   ↓
Process B
   ↓
Process C
```

By default, Processes inside the same Work Center **cannot execute in parallel**.

The next Process starts only after the previous Process is completed.

---

# 4. Final Process

A Process can be marked:

```text
FinalProcess = true
```

This means that completing this Process completes the current Work Center.

Example:

```text
Process A Seg 1
Process B Seg 2
Process C Seg 3 FinalProcess
```

When Process C is completed:

```text
Work Center = Completed
```

and the Work Center output quantity can be produced.

Normally the highest-sequence active Process should be the Final Process.

---

# 5. Work Center Output

Each Work Center has an Output Item.

The output can be either:

1. Intermediate WIP
2. Final Finished Goods

Example:

```text
WC1 → WIP001
WC2 → WIP002
WC3 → FG001
```

WIP outputs can later become BOM components of another Work Center.

Example:

```text
WC1 produces WIP001
WC2 produces WIP002

WC3 consumes:
  WIP001
  WIP002

WC3 produces:
  FG001
```

This allows the Product Definition to support **subassembly manufacturing**.

---

# 6. BOM Consumption

BOM materials should be attached to the Process where they are actually consumed.

Example:

```text
Process A
 ├─ ITEM001
 └─ ITEM002
```

This means ITEM001 and ITEM002 are issued/consumed during Process A.

Another Process may not consume any materials.

Example:

```text
Process B
 └─ No BOM
```

Therefore BOM is preferably **Process-level BOM**, rather than only Product-level BOM.

This provides better:

* Material planning
* Material issuing
* WIP tracking
* Production costing
* Material variance analysis
* Actual consumption tracking

---

# 7. Machine Definition

A Process may require one or more Machines.

Example:

```text
Process A
 └─ Machine A
```

Machine configuration may contain information such as:

```text
Setup Time
Cycle Time
Run Time
Capacity
Efficiency
Queue Time
Transfer Time
Downtime Allowance
```

These values can later be used for:

* Production scheduling
* Estimated completion time
* Machine loading
* Capacity planning
* Machine costing

---

# 8. Labour Definition

Labour is associated with the Process / Machine operation.

Example:

```text
Process A
 └─ Machine A
     └─ Labour 1
```

Labour may contain:

```text
Labour Type / Skill
Required Headcount
Setup Labour Time
Run Labour Time
Hourly Cost
Efficiency
```

This allows production costing to calculate:

```text
Material Cost
+
Machine Cost
+
Labour Cost
+
Overhead
=
Production Cost
```

---

# 9. Production Example A – Sequential Work Centers

Product:

```text
FG001
```

Definition:

```text
FG001

├─ WC1 Seg 1
│   Output: WIP001
│
│   ├─ Process A Seg 1
│   │   ├─ BOM ITEM001
│   │   ├─ BOM ITEM002
│   │   └─ Machine A
│   │       └─ Labour 1
│   │
│   └─ Process B Seg 2 [Final Process]
│       └─ Machine B
│           └─ Labour 2
│
├─ WC2 Seg 2
│   Output: WIP002
│
│   ├─ Process P1 Seg 1
│   │   ├─ BOM ITEM003
│   │   └─ Machine C
│   │       └─ Labour C1
│   │
│   └─ Process P2 Seg 2 [Final Process]
│       └─ Machine D
│           └─ Labour C2
│
└─ WC3 Seg 3
    Output: FG001

    └─ Process ASS Seg 1 [Assembly] [Final Process]
        ├─ BOM WIP001
        ├─ BOM WIP002
        └─ Machine D
            └─ Labour D1
```

Execution:

```text
WC1
 ↓
WIP001
 ↓
WC2
 ↓
WIP002
 ↓
WC3
 ↓
FG001
```

Rule:

```text
WC1 must finish before WC2 starts.

WC2 must finish before WC3 starts.
```

---

# 10. Production Example B – Parallel Work Centers

Product:

```text
FG001
```

Definition:

```text
WC1 Seg 1 → WIP001
WC2 Seg 1 → WIP002
WC3 Seg 2 → FG001
```

Execution:

```text
              ┌─ WC1 → WIP001 ─┐
Production ───┤                 ├──> WC3 → FG001
              └─ WC2 → WIP002 ─┘
```

WC1 and WC2 may run concurrently.

WC3 requires:

```text
WIP001
+
WIP002
```

Therefore WC3 cannot start until both WC1 and WC2 have completed the required quantities.

Business rule:

```text
All Work Centers belonging to the previous production segment
must satisfy their dependency requirements before the next
segment is released.
```

---

# 11. Production Example C – Simple Product

Product:

```text
FG002
```

Definition:

```text
WC1 Seg 1
Output: FG002

└─ Process A Seg 1 [Final Process]
    ├─ BOM ITEM001
    ├─ BOM ITEM002
    └─ Machine A
        └─ Labour 1
```

Execution:

```text
ITEM001
   +
ITEM002
   ↓
Process A
   ↓
FG002
```

This is the simplest manufacturing scenario:

```text
1 Product
1 Work Center
1 Process
Final output = Product itself
```

---

# 12. Production Example D – One Work Center with Multiple Processes

Product:

```text
FG002
```

Definition:

```text
WC1 Seg 1
Output: FG002

├─ Process A Seg 1
│   ├─ BOM ITEM001
│   ├─ BOM ITEM002
│   └─ Machine A
│       └─ Labour 1
│
├─ Process B Seg 2
│   └─ Machine B
│       └─ Labour 2
│
└─ Process C Seg 3 [Final Process]
    └─ Machine C
        └─ Labour 3
```

Execution:

```text
Process A
   ↓
Process B
   ↓
Process C
   ↓
FG002
```

Processes are executed sequentially.

Process B cannot start until Process A is completed.

Process C cannot start until Process B is completed.

Completion of Process C completes WC1 and produces FG002.

---

# 13. Recommended Execution Model

The production engine should treat the hierarchy as two levels of routing:

```text
LEVEL 1
Work Center Routing

LEVEL 2
Process Routing
```

Example:

```text
Product FG001

Production Stage 1
 ├─ WC1
 └─ WC2

Production Stage 2
 └─ WC3
```

Inside WC1:

```text
Process A
   ↓
Process B
```

Inside WC2:

```text
Process P1
   ↓
Process P2
```

Inside WC3:

```text
Assembly
```

Therefore the complete routing becomes:

```text
              ┌─ Process A → Process B → WIP001 ─┐
Stage 1 ──────┤                                   ├─── Stage 2
              └─ Process P1 → Process P2 → WIP002┘
                                                      ↓
                                                   Assembly
                                                      ↓
                                                    FG001
```

---

# 14. Important Enhancement: Separate Sequence From Dependency

For a simple ERP, `Sequence` is sufficient.

However, for a production system that will grow, it is recommended to distinguish:

```text
Sequence
```

from:

```text
Dependency
```

Sequence controls normal display / execution order.

Dependency defines what actually must finish before another operation can start.

Example:

```text
WC1 Sequence 10
WC2 Sequence 10
WC3 Sequence 20
```

with dependencies:

```text
WC3 depends on WC1
WC3 depends on WC2
```

This gives the system much greater future flexibility.

Future production flows could support:

```text
       WC1
      /   \
    WC2   WC3
      \   /
       WC4
```

instead of being limited to simple sequence numbers.

For Phase 1, sequence-based execution can remain the main design, while the database should preferably be designed so explicit dependencies can be added later.

---

# 15. Important Enhancement: Quantity Dependency

A Work Center should not necessarily need the previous Work Center to be **100% completed** before starting.

Future production may require transfer-batch processing.

Example:

```text
Production Order = 1,000 units

WC1 completes first 100 units.

WC2 may start processing those 100 units
while WC1 continues producing the remaining 900.
```

Therefore the system should eventually distinguish between:

```text
Full Batch Dependency
```

and:

```text
Transfer Batch Dependency
```

For the initial implementation, the rule can remain:

```text
Previous Work Center must be completed before next Work Center starts.
```

But the design should avoid preventing transfer-batch support later.

---

# 16. Recommended Product Definition Concepts

The Product Definition should therefore contain approximately:

```text
ProductDefinition
│
├─ WorkCenterRouting
│   ├─ WorkCenter
│   ├─ Sequence
│   ├─ OutputItem
│   ├─ OutputQty
│   ├─ Yield %
│   └─ Processes
│
├─ ProcessRouting
│   ├─ Process
│   ├─ Sequence
│   ├─ FinalProcess
│   ├─ Materials
│   ├─ Machines
│   ├─ Labour
│   └─ StandardTime
│
├─ BOM
│   ├─ Item
│   ├─ StandardQty
│   ├─ Scrap %
│   └─ IssueMethod
│
├─ MachineRequirement
│
└─ LabourRequirement
```

---

# 17. Key Business Rules

1. A Product must have at least one Work Center.

2. Every Work Center must contain at least one Process.

3. Work Centers with different sequence numbers run sequentially.

4. Work Centers with the same sequence number may run in parallel.

5. A later Work Center cannot start until its required predecessor Work Centers are completed.

6. Processes inside the same Work Center run sequentially by default.

7. A Process marked `FinalProcess` completes the Work Center.

8. Materials are consumed by the Process where they are required.

9. A Work Center may produce either a WIP item or the final Finished Goods item.

10. WIP produced by one Work Center may become a BOM component of another Work Center.

11. The final Work Center normally outputs the Product's Finished Goods Item Code.

12. Machine and Labour requirements belong to the Process operation.

13. Sequence should preferably support gaps such as:

```text
10
20
30
```

instead of:

```text
1
2
3
```

so operations can later be inserted without resequencing everything.

14. Only one Process should normally be marked as the Final Process for each Work Center.

15. The system should validate that every WIP consumed by a downstream Work Center can be produced by the Product Definition or supplied externally.

---

# 18. Overall Concept

The Product Definition can be summarized as:

```text
WHAT TO MAKE
    ↓
Product

WHERE TO MAKE IT
    ↓
Work Center

IN WHAT PRODUCTION STAGE
    ↓
Work Center Sequence

HOW TO MAKE IT
    ↓
Process

IN WHAT ORDER
    ↓
Process Sequence

WHAT MATERIAL IS NEEDED
    ↓
Process BOM

WHAT EQUIPMENT IS NEEDED
    ↓
Machine

WHO / WHAT LABOUR IS NEEDED
    ↓
Labour

WHAT DOES THIS STAGE PRODUCE
    ↓
WIP / Finished Goods Output
```

This Product Definition then becomes the **master blueprint** used later to generate:

```text
Production Order
      ↓
Work Center Jobs
      ↓
Process / Operation Jobs
      ↓
Material Requirement
      ↓
Machine Requirement
      ↓
Labour Requirement
      ↓
Scheduling
      ↓
WIP Movement
      ↓
Production Costing
      ↓
Finished Goods
```

# Additional Product Definition Production Cases

This document extends the Product Definition model with more possible manufacturing cases.

The current model supports:

```text
Product
 └─ Work Center
     └─ Process
         ├─ BOM
         ├─ Machine
         └─ Labour
```

Work Center sequence controls stage execution.

Process sequence controls execution inside the Work Center.

---

# Case E — Multiple Parallel Work Centers Join Into Final Assembly

## Scenario

Three different subassemblies are produced at the same time.

The final assembly waits until all three are completed.

## Definition

| Work Center | WC Seq | Output | Description         |
| ----------- | -----: | ------ | ------------------- |
| WC1         |     10 | WIP001 | Produce Component A |
| WC2         |     10 | WIP002 | Produce Component B |
| WC3         |     10 | WIP003 | Produce Component C |
| WC4         |     20 | FG001  | Final Assembly      |

## Structure

```text
             ┌─ WC1 → WIP001 ─┐
             │                 │
Start ───────┼─ WC2 → WIP002 ─┼──> WC4 → FG001
             │                 │
             └─ WC3 → WIP003 ─┘
```

## WC4 BOM

| BOM Item | Qty |
| -------- | --: |
| WIP001   |   1 |
| WIP002   |   1 |
| WIP003   |   1 |

## Rule

```text
WC4 can start only when:

WC1 = Completed
AND
WC2 = Completed
AND
WC3 = Completed
```

This is common for products made from several independent subassemblies.

---

# Case F — Parallel Work Centers Followed By Multiple Sequential Stages

## Scenario

Two subassemblies are produced in parallel.

They are assembled together.

The assembled product then goes through testing and packing.

## Work Centers

| Work Center | Seq | Output |
| ----------- | --: | ------ |
| WC1         |  10 | WIP001 |
| WC2         |  10 | WIP002 |
| WC3         |  20 | WIP003 |
| WC4         |  30 | WIP004 |
| WC5         |  40 | FG001  |

## Execution

```text
        ┌─ WC1 → WIP001 ─┐
Start ──┤                 ├──> WC3 → WIP003
        └─ WC2 → WIP002 ─┘
                             ↓
                           WC4
                           Test
                             ↓
                           WIP004
                             ↓
                           WC5
                           Pack
                             ↓
                           FG001
```

This case shows that the Product Definition can contain both:

```text
Parallel Production
+
Sequential Production
```

in the same routing.

---

# Case G — One Work Center With Many Processes and BOM Consumed At Different Processes

## Scenario

Raw materials are not all consumed at the first Process.

Different materials are introduced at different manufacturing stages.

## Definition

| Process | Seq | BOM              | Machine         | Final |
| ------- | --: | ---------------- | --------------- | ----- |
| Mixing  |  10 | ITEM001, ITEM002 | Mixer           | No    |
| Heating |  20 | ITEM003          | Heater          | No    |
| Cooling |  30 | None             | Cooling Unit    | No    |
| Packing |  40 | PACK001          | Packing Machine | Yes   |

## Execution

```text
ITEM001 + ITEM002
        ↓
      Mixing
        ↓
      Heating ← ITEM003
        ↓
      Cooling
        ↓
      Packing ← PACK001
        ↓
      FG001
```

## Important Rule

BOM consumption belongs to the Process where the material is actually used.

This improves:

* Material issuing
* Material traceability
* Process costing
* Variance analysis

---

# Case H — Work Center With No BOM

## Scenario

A Work Center performs only processing.

No additional material is consumed.

Examples:

* Cutting
* Grinding
* Polishing
* Heat treatment
* Inspection
* Testing

## Definition

| Work Center | Seq | Output |
| ----------- | --: | ------ |
| WC1         |  10 | WIP001 |
| WC2         |  20 | WIP002 |
| WC3         |  30 | FG001  |

WC2:

| Process        | BOM  | Machine | Labour   |
| -------------- | ---- | ------- | -------- |
| Heat Treatment | None | Furnace | Operator |

Execution:

```text
WC1
 ↓
WIP001
 ↓
WC2
No additional BOM
 ↓
WIP002
 ↓
WC3
 ↓
FG001
```

This must be considered valid.

A Process does not require BOM in order to be a valid Process.

---

# Case I — Manual Process With No Machine

## Scenario

A Process is completely manual.

Examples:

* Manual assembly
* Visual inspection
* Hand packing
* Label application

## Definition

| Process         | BOM     | Machine | Labour          |
| --------------- | ------- | ------- | --------------- |
| Manual Assembly | ITEM001 | None    | Assembly Worker |
| Inspection      | None    | None    | QC Inspector    |

## Rule

```text
Machine is optional.
Labour can exist without Machine.
```

The system must not force every Process to have a Machine.

---

# Case J — Automated Process With No Direct Labour

## Scenario

A machine operates automatically.

Labour is not required for every production cycle.

Examples:

* Automated CNC
* Automatic filling machine
* Robotic production line

## Definition

| Process           | Machine  | Labour |
| ----------------- | -------- | ------ |
| Automatic Filling | FILL-M01 | None   |

Possible setup labour may still exist separately.

## Rule

```text
Labour can be optional.
Machine can exist without Labour.
```

---

# Case K — Process Requires Multiple Machines

## Scenario

One Process requires more than one machine/resource.

Example:

```text
Injection Moulding
```

may require:

* Injection Machine
* Mould
* Chiller

## Definition

| Process   | Machine   | Type                |
| --------- | --------- | ------------------- |
| Injection | INJ-M01   | Main Machine        |
| Injection | MOULD-A01 | Tooling             |
| Injection | CHILLER01 | Supporting Resource |

Concept:

```text
Process Injection
 ├─ Injection Machine
 ├─ Mould
 └─ Chiller
```

Therefore:

```text
Process → Machine
```

should preferably be one-to-many.

---

# Case L — Process Requires Multiple Labour Types

## Scenario

One Process requires multiple labour resources.

Example:

```text
Assembly
```

requires:

* 2 operators
* 1 technician
* 1 QC person

## Definition

| Process  | Labour Type  | Headcount |
| -------- | ------------ | --------: |
| Assembly | Operator     |         2 |
| Assembly | Technician   |         1 |
| Assembly | QC Inspector |         1 |

Therefore:

```text
Process → Labour
```

should also be one-to-many.

---

# Case M — One Work Center Produces WIP Used Much Later

## Scenario

WIP produced at an earlier Work Center is not necessarily consumed by the immediately next Work Center.

## Work Centers

| WC  | Seq | Output |
| --- | --: | ------ |
| WC1 |  10 | WIP001 |
| WC2 |  20 | WIP002 |
| WC3 |  30 | WIP003 |
| WC4 |  40 | FG001  |

WC4 may consume:

```text
WIP001
WIP003
```

instead of only the immediate previous output.

## Execution

```text
WC1 → WIP001 ──────────────────┐
                               │
WC2 → WIP002 → WC3 → WIP003 ──┼──> WC4 → FG001
                               │
                               ┘
```

This demonstrates why BOM dependency and Work Center sequence should not be treated as exactly the same concept.

---

# Case N — Shared Raw Material Used By Different Parallel Work Centers

## Scenario

Two parallel Work Centers consume the same raw material.

## Definition

WC1:

| BOM     | Qty |
| ------- | --: |
| ITEM001 |   2 |

WC2:

| BOM     | Qty |
| ------- | --: |
| ITEM001 |   3 |

Total requirement for one Product Order:

```text
ITEM001 = 5
```

Structure:

```text
            ┌─ WC1 ← ITEM001 × 2
Start ──────┤
            └─ WC2 ← ITEM001 × 3
```

The MRP/material requirement engine must aggregate requirements without losing Process ownership.

---

# Case O — Scrap / Yield Loss

## Scenario

Not every unit entering a Process becomes good output.

Example:

```text
Input 100 KG
Expected Yield 95%
Output 95 KG
```

## Definition

| Process | Input Qty | Expected Yield |
| ------- | --------: | -------------: |
| Cutting |       100 |            95% |

Possible calculation:

```text
Expected Good Output
=
Input Qty × Yield %
```

Example:

```text
100 × 95%
=
95
```

This is important for real manufacturing.

Recommended fields:

| Field        | Example |
| ------------ | ------: |
| StandardQty  |     100 |
| ScrapPercent |      5% |
| YieldPercent |     95% |

---

# Case P — BOM Quantity Based On Production Batch

## Scenario

A raw material quantity is defined per batch instead of per individual FG.

Example:

```text
1 chemical bag
produces
100 FG
```

Instead of storing:

```text
0.01 bag per FG
```

you may store:

| BOM Item | Qty | For Output Qty |
| -------- | --: | -------------: |
| CHEM001  |   1 |            100 |

Equivalent:

```text
1 / 100 = 0.01 per FG
```

This fits your previous concept of supporting pack/batch size.

Recommended model:

```text
MaterialQty = 1
BaseOutputQty = 100
```

instead of forcing users to enter very small decimal quantities.

---

# Case Q — Fixed Setup Material Plus Variable Material

## Scenario

Some materials are required once per production batch.

Others depend on production quantity.

Example:

For production order of 1,000 units:

| Material          | Type            |   Qty |
| ----------------- | --------------- | ----: |
| Cleaning Chemical | Fixed Per Batch |     1 |
| Raw Material A    | Per Unit        | 1,000 |

Recommended BOM issue types:

```text
PerUnit
PerBatch
Fixed
```

Possible calculation:

```text
Required Qty =
PerUnit Qty × Production Qty
```

or:

```text
Required Qty =
Fixed Batch Qty
```

This becomes useful later for chemicals, setup consumables, labels, etc.

---

# Case R — Rework Process

## Scenario

A product fails QC and must return to an earlier Process.

Normal flow:

```text
Process A
 ↓
Process B
 ↓
QC
```

If QC fails:

```text
QC
 ↓ FAIL
Process B
 ↓
QC again
```

This does not need to be part of the basic Product Definition execution at first.

However, your design should not assume every Process can only execute once.

Recommended future Production Order design:

```text
OperationAttempt
Attempt 1
Attempt 2
Attempt 3
```

This allows rework history.

---

# Case S — Quality Inspection Between Processes

## Scenario

A QC Process exists before production can continue.

## Definition

| Process       | Seq | Type       | Final |
| ------------- | --: | ---------- | ----- |
| Machine       |  10 | Production | No    |
| QC Inspection |  20 | Inspection | No    |
| Packing       |  30 | Production | Yes   |

Execution:

```text
Machine
 ↓
QC
 ↓ PASS
Packing
 ↓
FG001
```

If QC fails:

```text
QC
 ↓ FAIL
Hold / Rework
```

Recommended Process Types:

```text
Production
Assembly
Inspection
Testing
Packing
Subcontract
Other
```

---

# Case T — External / Subcontract Process

## Scenario

One Process is performed by an external supplier.

Example:

```text
Cutting
 ↓
External Plating
 ↓
Assembly
```

Definition:

| Process  | Type        | Machine | Supplier   |
| -------- | ----------- | ------- | ---------- |
| Cutting  | Internal    | CUT-M01 | None       |
| Plating  | Subcontract | None    | Supplier A |
| Assembly | Internal    | ASS-M01 | None       |

Execution:

```text
Internal WC
 ↓
Send to Supplier
 ↓
Receive From Supplier
 ↓
Continue Production
```

This is useful if your Production module later integrates with Procurement/Subcontract PO.

---

# Case U — Same Work Center Used More Than Once

## Scenario

A physical Work Center is used twice in the production flow.

Example:

```text
WC-CUT
 ↓
WC-HEAT
 ↓
WC-CUT
```

The first and second usage are different routing steps.

Therefore the Product Definition should not assume:

```text
One WorkCenterCode
=
One occurrence in a Product
```

Recommended:

```text
ProductWorkCenter.Id
```

should identify the routing step.

Example:

| Route ID | WorkCenter | Seq |
| -------- | ---------- | --: |
| 101      | WC-CUT     |  10 |
| 102      | WC-HEAT    |  20 |
| 103      | WC-CUT     |  30 |

This is very important for database design.

---

# Case V — Same Process Used More Than Once

The same issue applies to Processes.

Example:

```text
Cleaning
 ↓
Painting
 ↓
Cleaning
```

Both Cleaning Processes may use the same master Process Code but represent different routing operations.

Therefore:

```text
ProcessMaster
```

and:

```text
ProductProcessRouting
```

should be separate concepts.

---

# Case W — Alternate Machine

## Scenario

A Process can run on several machines.

Example:

```text
Process Cutting
```

may use:

```text
CUT-M01
OR
CUT-M02
OR
CUT-M03
```

Definition:

| Process | Machine | Priority |
| ------- | ------- | -------: |
| Cutting | CUT-M01 |        1 |
| Cutting | CUT-M02 |        2 |
| Cutting | CUT-M03 |        3 |

This is different from Case K.

Case K:

```text
Machine A AND Machine B are required
```

Case W:

```text
Machine A OR Machine B can be used
```

Recommended future field:

```text
MachineRequirementType:
RequiredTogether
Alternative
```

---

# Case X — Alternate Material

## Scenario

A BOM item may have an approved substitute.

Example:

```text
ITEM001
```

can be replaced by:

```text
ITEM001-A
```

when ITEM001 has insufficient stock.

Definition:

| Primary Item | Alternative | Conversion |
| ------------ | ----------- | ---------: |
| ITEM001      | ITEM001-A   |        1:1 |

Production should still retain:

```text
Standard BOM
Actual Material Used
```

separately.

---

# Case Y — Optional BOM Component

## Scenario

Some Product options require additional materials.

Example:

```text
FG001 Standard
FG001 with Optional Label
```

Possible BOM:

| Item    | Required |
| ------- | -------- |
| ITEM001 | Yes      |
| ITEM002 | Yes      |
| LABEL01 | Optional |

This could later be controlled by:

```text
Product Variant
Customer Requirement
Sales Order Configuration
```

This is an advanced feature and does not need to be Phase 1.

---

# Case Z — Different Product Revision

## Scenario

The manufacturing definition changes over time.

Example:

```text
FG001 Revision A
FG001 Revision B
```

Revision A:

```text
ITEM001 + ITEM002
```

Revision B:

```text
ITEM001 + ITEM003
```

The Product Definition must retain historical versions.

Recommended:

| Product | Revision | Effective From | Status   |
| ------- | -------- | -------------- | -------- |
| FG001   | A        | 2026-01-01     | Obsolete |
| FG001   | B        | 2026-07-01     | Active   |

Production Orders created under Revision A must continue using Revision A even after Revision B becomes active.

---

# Case AA — Work Center Produces Multiple Outputs

## Scenario

One Process generates the main product plus a by-product.

Example:

```text
Raw Material
 ↓
Process
 ├─ Main Product
 └─ By-product
```

Example:

| Output      | Type       | Qty |
| ----------- | ---------- | --: |
| WIP001      | Main       | 100 |
| SCRAP-METAL | By-product |   5 |

Your current design assumes one main Work Center output.

That is fine for Phase 1.

But you may want a future:

```text
ProductWorkCenterOutput
```

table instead of only:

```text
OutputItemCode
```

on the Work Center row.

---

# Case AB — Co-Products

## Scenario

One manufacturing Process intentionally produces two valuable products.

Example:

```text
Process
 ├─ Product A
 └─ Product B
```

This is more complex than a normal by-product because production cost may need to be distributed between outputs.

This should probably be considered a future enhancement, not Phase 1.

---

# Case AC — Phantom / Non-Stock WIP

## Scenario

A logical WIP stage exists for routing/costing but is not physically stocked.

Example:

```text
WC1 → Logical WIP
 ↓
WC2
 ↓
FG001
```

The WIP may not require:

```text
Inventory Receipt
Inventory Issue
Warehouse Balance
```

Recommended future output type:

```text
StockedWIP
NonStockWIP
FinishedGoods
```

---

# Case AD — WIP Stored Before Next Stage

## Scenario

Unlike phantom WIP, the intermediate product is physically stored.

Example:

```text
WC1
 ↓
WIP001
 ↓
WIP Warehouse
 ↓
WC2
```

The production engine may need:

```text
WIP Receipt
WIP Transfer
WIP Issue
```

This distinction is important for Inventory integration.

---

# Case AE — Production Quantity Split Into Multiple Batches

## Scenario

Production Order:

```text
1,000 FG001
```

Machine capacity:

```text
250 units per batch
```

Production runs:

```text
Batch 1 = 250
Batch 2 = 250
Batch 3 = 250
Batch 4 = 250
```

The Product Definition provides standard production rules.

The Production Order execution determines actual batches.

Therefore batch execution should not be hard-coded into the Product Definition.

---

# Case AF — Partial Completion

## Scenario

Work Center requirement:

```text
1,000 units
```

Actual completed:

```text
600 units
```

The system should support:

```text
Required Qty = 1,000
Completed Qty = 600
Balance Qty = 400
```

Do not model Work Center status only as:

```text
Started
Completed
```

Recommended statuses:

```text
NotStarted
Released
InProgress
PartiallyCompleted
Completed
OnHold
Cancelled
```

---

# Case AG — Transfer Batch

## Scenario

Production Order is 1,000 units.

WC1 completes the first 100 units.

WC2 is allowed to start those 100 units while WC1 continues.

```text
WC1: 100 / 1000
        ↓
      WC2 starts 100
```

This is more advanced than your current rule:

```text
WC1 must finish completely before WC2 starts.
```

Recommended Phase 1:

```text
Full completion dependency
```

Recommended future support:

```text
TransferBatchQty
```

Example:

```text
TransferBatchQty = 100
```

---

# Case AH — Parallel Process Inside Same Work Center

Your current business rule is:

```text
Normally Processes inside the same Work Center
cannot run at the same time.
```

That is a good default.

However, a future exception may exist.

Example:

```text
WC1

Process A Seq 10
Process B Seq 10
Process C Seq 20
```

Possible execution:

```text
Process A ─┐
           ├──> Process C
Process B ─┘
```

For Phase 1, I recommend **not supporting this**.

Keep:

```text
Processes inside one Work Center = sequential only.
```

If parallel processing is required, define separate Work Centers instead.

This keeps the Production model much simpler.

---

# Case AI — Optional Process

## Scenario

Normally the product goes through:

```text
Process A
 ↓
Process B
 ↓
Process C
```

But Process B may only be required for certain orders.

Example:

```text
Special coating
```

Possible future field:

```text
IsOptional
```

or routing condition.

This is a future enhancement and should not complicate Phase 1.

---

# Case AJ — Outsourced Entire Work Center

Instead of one outsourced Process, the complete Work Center may be subcontracted.

Example:

```text
WC1 Internal Cutting
 ↓
WC2 External Painting
 ↓
WC3 Internal Assembly
```

WC2 may integrate with:

```text
Subcontract Purchase Order
Goods Sent
Goods Returned
Supplier Charges
```

---

# Case AK — Packaging As Final Work Center

Sometimes manufacturing is completed before packaging.

Example:

```text
WC1 Mixing → WIP001
WC2 Filling → WIP002
WC3 Packing → FG001
```

WC3 may consume:

```text
Bottle
Cap
Label
Carton
```

This means packaging material should be treated as BOM at the packing Process, not necessarily at the first Process.

---

# Case AL — QC Is Final Process Of Work Center

Example:

```text
WC1
 ├─ Process A
 ├─ Process B
 └─ QC Final Process
```

Output WIP001 should only be considered completed after QC passes.

Recommended interpretation:

```text
Final Process completed
AND
QC passed
=
Work Center completed
```

This is slightly different from merely marking the Process complete.

---

# Case AM — Final FG Produced Before Secondary Operation

Sometimes the main product is technically manufactured before a secondary operation such as:

* Printing
* Labelling
* Packing
* Sterilization

In ERP terms, it is usually safer to keep the item as WIP until the final required operation finishes.

Example:

```text
WC1 → WIP001
WC2 → FG001
```

rather than:

```text
WC1 → FG001
WC2 → FG001
```

This avoids receiving the same FG multiple times.

Recommended rule:

```text
Only the final production Work Center should normally output FG ProductCode.
```

---

# Case AN — Different WIP Quantity Conversion

## Scenario

The output quantity from one Work Center does not equal the input quantity of the next.

Example:

```text
WC1:
100 KG Raw Material
→
90 KG WIP001
```

WC2:

```text
90 KG WIP001
→
900 units FG001
```

This requires:

* UOM conversion
* Output quantity
* Yield
* BOM conversion

Your Work Center Output should therefore preferably include:

| Field             | Purpose         |
| ----------------- | --------------- |
| OutputItemCode    | Produced item   |
| OutputUOM         | Production UOM  |
| StandardOutputQty | Output quantity |
| YieldPercent      | Expected yield  |

---

# Case AO — Multiple UOM Production

Example:

Raw Material:

```text
KG
```

WIP:

```text
KG
```

Finished Goods:

```text
PCS
```

Example routing:

```text
100 KG ITEM001
 ↓
95 KG WIP001
 ↓
950 PCS FG001
```

This is very common and the Product Definition should not assume one UOM throughout the routing.

---

# Case AP — Machine Setup Time Once Per Batch

Example:

```text
Machine Setup = 30 minutes
Cycle Time = 2 seconds/unit
Production Qty = 1,000
```

Calculation:

```text
Run Time
=
1,000 × 2 seconds
=
2,000 seconds
```

Total:

```text
Setup Time
+
Run Time
```

Setup time should normally be charged once per production run/batch, not per unit.

---

# Case AQ — Machine Cycle Produces Multiple Units

Example:

```text
Injection mould
Cycle Time = 30 seconds
Output Per Cycle = 4 PCS
```

For 400 PCS:

```text
Required Cycles
=
400 / 4
=
100 cycles
```

Machine runtime:

```text
100 × 30 seconds
=
3,000 seconds
```

Recommended Machine Requirement fields:

```text
CycleTime
OutputPerCycle
SetupTime
```

This is particularly useful for your production completion-time calculation.

---

# Case AR — Labour Headcount Changes Process Duration

Example:

Standard operation:

```text
2 workers
60 minutes
```

If only 1 worker is available, actual duration may be longer.

For Phase 1, keep labour quantity mainly for costing and planning.

Avoid automatically changing Process time based on headcount unless the business formula is explicitly defined.

---

# Case AS — Product Without Intermediate WIP Item

Some simple routing may contain multiple Work Centers but the company does not want to create stock item codes for every intermediate stage.

Example:

```text
WC1 Cutting
 ↓
WC2 Assembly
 ↓
FG001
```

WC1 may have:

```text
OutputItemCode = NULL
```

and represent operation progress only.

However, this has implications for:

* WIP valuation
* Inventory tracking
* Material traceability

Recommended design decision:

```text
Output item should be required only when physical WIP inventory
needs to be tracked.
```

---

# Case AT — Product With Intermediate WIP That Can Also Be Sold

Example:

```text
WIP001
```

is produced for FG001 but can also be sold independently.

Then WIP001 is really a normal inventory/manufactured item.

Example:

```text
FG001 routing
WC1 → WIP001
WC2 → FG001
```

and:

```text
Customer can buy WIP001 directly.
```

This should work naturally if WIP001 is stored in the normal Item Master.

---

# Case AU — WIP Produced By Separate Product Definition

More complex example:

```text
WIP001
```

has its own Product Definition.

FG001 simply consumes WIP001.

Example:

```text
Product WIP001
  WC1 → WIP001

Product FG001
  WC2
    BOM WIP001
    BOM ITEM003
  → FG001
```

This differs from defining all Work Centers inside one FG001 routing.

Both models are valid.

Use separate Product Definitions when WIP001:

* Is manufactured independently
* Is stored
* Is shared by many Finished Goods
* Has independent planning
* Can be produced in separate Production Orders

---

# Case AV — Shared Subassembly Across Multiple Finished Goods

Example:

```text
WIP001
```

is used by:

```text
FG001
FG002
FG003
```

Recommended:

```text
WIP001 should have its own Product Definition.
```

Then:

```text
FG001 BOM → WIP001
FG002 BOM → WIP001
FG003 BOM → WIP001
```

This avoids duplicating the same WIP routing three times.

---

# Case AW — Production Alternative Routing

Example:

Normal route:

```text
WC1 → WC2 → WC3
```

Alternative route when Machine WC2 is unavailable:

```text
WC1 → WC4 → WC3
```

This is an advanced feature.

Future concept:

```text
Routing Version / Routing Alternative
```

Example:

| Route | Description          |
| ----- | -------------------- |
| R1    | Standard             |
| R2    | Backup Machine Route |

For Phase 1, one active routing per Product Revision is enough.

---

# Case AX — Product With No Machine And No Labour Cost

A Process may only represent:

```text
Waiting
Curing
Drying
Cooling
Aging
```

Example:

| Process | Machine | Labour | Duration |
| ------- | ------- | ------ | -------: |
| Curing  | None    | None   | 24 hours |

The Process is still important because it affects:

```text
Production completion date
Lead time
Scheduling
```

Therefore a valid Process may have:

```text
No BOM
No Machine
No Labour
But Duration > 0
```

---

# Case AY — Waiting / Queue Time Between Work Centers

Example:

```text
WC1
 ↓
Wait 4 hours
 ↓
WC2
```

Instead of creating a fake Work Center, you may later support:

```text
TransferTime
QueueTime
WaitTime
```

at Work Center or Process level.

---

# Case AZ — Multiple Final Products From Shared Early Stages

Example:

```text
WC1 Common Processing
 ↓
WIP001
 ├─ WC2 → FG001
 └─ WC3 → FG002
```

This is generally better modelled as:

```text
Product Definition WIP001
Product Definition FG001
Product Definition FG002
```

rather than one Product Definition containing two final products.

This keeps one Product Definition tied to one primary Product Code.

---

# Summary Table

| Case | Scenario                            | Recommended Phase                |
| ---- | ----------------------------------- | -------------------------------- |
| E    | 3 parallel WCs join final assembly  | Phase 1                          |
| F    | Parallel + sequential stages        | Phase 1                          |
| G    | BOM consumed at different Processes | Phase 1                          |
| H    | Process with no BOM                 | Phase 1                          |
| I    | Manual Process without Machine      | Phase 1                          |
| J    | Machine Process without Labour      | Phase 1                          |
| K    | Multiple required Machines          | Phase 1/2                        |
| L    | Multiple Labour types               | Phase 1                          |
| M    | WIP consumed much later             | Phase 1                          |
| N    | Shared raw material                 | Phase 1                          |
| O    | Scrap / Yield                       | Strongly Recommended             |
| P    | Batch-based BOM quantity            | Strongly Recommended             |
| Q    | Fixed + variable material           | Phase 2                          |
| R    | Rework                              | Phase 2                          |
| S    | QC Process                          | Strongly Recommended             |
| T    | Subcontract Process                 | Phase 2                          |
| U    | Same Work Center reused             | Must Support                     |
| V    | Same Process reused                 | Must Support                     |
| W    | Alternative Machine                 | Phase 2                          |
| X    | Substitute Material                 | Phase 2                          |
| Y    | Optional BOM                        | Future                           |
| Z    | Product Revision                    | Must Support                     |
| AA   | Main + By-product                   | Future                           |
| AB   | Co-products                         | Future                           |
| AC   | Non-stock WIP                       | Phase 2                          |
| AD   | Stocked WIP                         | Phase 1                          |
| AE   | Multiple production batches         | Production Order                 |
| AF   | Partial completion                  | Must Support                     |
| AG   | Transfer batch                      | Phase 2                          |
| AH   | Parallel Process inside same WC     | Avoid Phase 1                    |
| AI   | Optional Process                    | Future                           |
| AJ   | Outsourced Work Center              | Phase 2                          |
| AK   | Packaging final stage               | Phase 1                          |
| AL   | QC as final Process                 | Strongly Recommended             |
| AM   | FG only at final WC                 | Recommended Rule                 |
| AN   | Output quantity conversion          | Must Support                     |
| AO   | Multiple UOM                        | Must Support                     |
| AP   | Setup time per batch                | Must Support                     |
| AQ   | Multiple units per machine cycle    | Strongly Recommended             |
| AR   | Labour effect on duration           | Future                           |
| AS   | WC with no WIP item                 | Design Decision                  |
| AT   | WIP can also be sold                | Phase 1                          |
| AU   | WIP has own Product Definition      | Must Support                     |
| AV   | Shared subassembly                  | Must Support                     |
| AW   | Alternative routing                 | Future                           |
| AX   | Waiting/Curing Process              | Phase 1                          |
| AY   | Queue / transfer time               | Phase 2                          |
| AZ   | Shared stage produces different FGs | Use separate Product Definitions |

# Recommended Minimum Scope For Your ERP

Based on the current design, I would make sure the first production version supports these cases correctly:

```text
1. Single WC + Single Process
2. Single WC + Multiple Sequential Processes
3. Multiple Sequential Work Centers
4. Multiple Parallel Work Centers
5. Parallel Work Centers Joining Into Assembly
6. BOM at Process Level
7. Processes With No BOM
8. Processes With No Machine
9. Multiple Labour Requirements
10. WIP Output
11. WIP Consumption
12. Same Work Center Master Used Multiple Times
13. Same Process Master Used Multiple Times
14. Product Revision
15. Partial Completion
16. Scrap / Yield
17. Different UOM Between RM / WIP / FG
18. Setup Time + Cycle Time
19. Machine Output Per Cycle
20. Independent WIP Product Definition
21. Shared Subassembly
22. QC / Inspection Process
23. Packing As Final Production Stage
```

These cases give the Product Definition enough flexibility for a practical ERP production module without making the first implementation excessively complicated.

