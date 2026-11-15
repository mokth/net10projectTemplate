# PRODUCTIONV2 — UNIFIED SUBCONTRACTING MODULE
## v10 — STRICT REAL-WORLD MALAYSIAN SME DESIGN & IMPLEMENTATION REVIEW
### BOM-to-FG assembly • same-item processing • external WIP • material returns • 100% existing costing authority • current DevExpress UI standards

**DOCUMENT STATUS: CORRECTED IMPLEMENTATION PLAN — NOT APPROVED FOR POSTING OR PRODUCTION GO-LIVE.** The only unconditional next execution is **G0: read-only repo/database feasibility and proof**. Existing *draft-only* repair and visual prototyping may be separately authorized after current local checkout verification. Do not call this 10/10, implementation-ready for a new costing mutation, or live-ready. **No new module-wide enable flag may be set until each enabled scenario has passed its own gates.**

**Remote repository examined:** `mokth/net10projectTemplate`, branch `productionv2`, commit **`37be8d3c3f6d273a850c7149c1f13f6cfc057624`** (2026-11-13 01:45:27 UTC). Reviewed current remote source at this ref and repository `.agents/skills` standards. **Could not inspect local working tree/uncommitted files, a live SQL Server tenant or running Blazor screens, and did not execute build/tests/UAT**. Any claim of working code requires evidence from the agent's CURRENT local tree and SQL Server. Remote audit is a design basis, **not** an operational certification.

**Supersedes:** v9's normative architecture/phase text and v8's speculative integration pathways. **Retains all 35 v9 scenario families and v8 critical/high implementation defects** as requirements. v10's corrections are definitive; v8/v9 are historical reference only. The implementation agent must use this single document rather than merge conflicting old instructions.

---

# 0. Executive decision and real-world acceptance

The existing Subcon draft shell is **not useful as a manufacturing transaction** until a business can complete its whole journey: create job → prepare material/WO source → dispatch → optionally confirm vendor receipt → partial goods/FG receipt → actual component consumption → leftover return or controlled scrap/loss → accepted/held/rejected outcome → approved settlement → job reconciliation/close → inquiry → authorized reversal. **A BOM-based job converting components into another output item is a manufacturing transaction, not a transfer or simple miscellaneous receipt.**

**One Subcontracting workbench, one maintainable workflow, distinct existing Inventory/Production/Procurement/QA authorities.** The workbench is an **orchestrator**, not a second inventory/production/costing engine. Do not make users choose costing method or remember internal line identifiers. Normal SMEs may run Inventory only; a Work Order and Planning permission are *not* mandatory for inventory-only assembly. The same screens adapt to external Work Order operations for production customers. A company buying ready-made FG without supplying any owned inputs should ordinarily use normal PO → GRN → Invoice, with optional Subcon job reference, rather than creating a duplicate FG receipt.

## 0.1 Three unambiguous kinds of proof

| Label in plan | Meaning | Allowed claim |
|---|---|---|
| **SOURCE-PROVEN** | Actual method/entity/readers found in pinned current repo | That component exists, **not** that Subcon can use it safely |
| **REQUIRED DESIGN** | Behavior necessary to support the workflow; implementation path not yet proven | Build a feasibility proof; **BLOCK posting** until approved |
| **BUSINESS EXAMPLE** | Representative Malaysian SME processing/assembly pattern | Design requirement; not a customer-specific fact or tax ruling |

**Priority:** (1) physical quantity/custody, (2) financial cost valuation, (3) user-friendly daily operation, (4) breadth of scenario coverage. No UI simplification may weaken the first two. Conversely, backend perfection does not excuse an unusable daily screen.

# 1. Source inspection — exact current behavior and known blockers

| Component/source | Verified remote behavior | Binding implication |
|---|---|---|
| `ErpWeb.Core/Subcon/ISubconInventorySourceAdapter.cs` | Both registered adapter implementations always reject posting/rollback | **NO live Send Out/Receive Back/WIP**. Preserve fail-closed. No false posting status. |
| `ErpWeb.Core/CoreServiceCollectionExtensions.cs` | DI maps both Subcon source adapters to BLOCKED implementations | No existing Subcon-to-ledger integration; do **not** replace registration before gate |
| `ErpWeb.Core/Subcon/SubconApplicationService.cs` | Draft dispatch/receipt only; rejects Production and lot-controlled sources; receipt Post calls dispatch-shaped Inventory adapter | Implement dedicated commands for dispatch, receipt, material use, leftover return and reversals; all remain blocked until posted authority proven |
| `SubconApplicationService.SaveReceiptDraftAsync` | Per-line `SumAsync` over saved allocations can double-count own edit, miss duplicates within request and race concurrently | Repair GroupBy, self-exclusion, lock, version/identity before extending draft; tests on SQL Server |
| `SubconApplicationService.SaveDispatchDraftAsync` | Replaces all linked lines on edit; warehouse and UOM come from free text/default; PO ref free text | Stable FK and source identity needed; no destructively replacing allocated lines; no free-form lot/source at Post |
| `ErpWeb.UI/Subcon/{Workbench,SendOut,ReceiveBack,Inquiry,Settings}` | Workbench shows drafts as open/outstanding; repeated Add-Line popup; Receive asks manual dispatch/line numbers | Redesign UX and truthful posted KPIs; reuse existing theme and pickers; shipping workbench **not** business-ready |
| `ErpWeb.Model/Entities/Subcon/*`, `Configurations/Subcon/*` | Only draft policy/dispatch/receipt, no job/BOM/material-consumption/FG/cost source link | Add minimal versioned business-evidence entities only after schema G0; **not** a shadow stock/cost ledger |
| `ErpWeb.Core/Planning/{BomExplosionService,IBomExplosionService,PrBomCalc}` | BOM modes, ACTIVE revisions, nested Make/Phantom, `RequiredQty`; public `ExplodeAsync` checks `PlanningProductDef.Access` | Reuse formula and explosion with a **Subcon-scoped read-only adapter**; neither bypass Planning authorization nor force WO for Inventory-only |
| `ErpWeb.Model/Entities/Planning/{PrBomHdr,PrDefBOM,PrBomOperation}` | Version/definition, BaseQty, alternate groups, supply source, operation key; `SUBCONTRACT` is duration-based process kind | Freeze exact selected materials/revision at first physical post; process kind alone is not a vendor dispatch feature |
| `ProductionWorkOrderService` | Builds/copies Work Order BOM/route snapshots | Production source must use actual released WO snapshot; never explode current master BOM for an existing WO |
| `ProductionOutputService.Posting.cs` | Current Daily Production rejects `YieldPercent != 100`; Production cost consumption/pooling is specialized | Do not claim arbitrary cutting yield/multi-output or external WIP is supported. Requires explicit proof/extension |
| `ProductionWorkOrderTransferService` | Full-balance cross-WO transfer | **Cannot** represent partial offsite supplier custody |
| `ProductionStockReturnService` | Production RM return tied to original issued material and valuation | Use only where exact issue identity and dependency apply; not a generic inventory supplier-leftover return |
| `IvStockTransferService` | `PostAsync(TR,batches)` dispatches to public inventory poster; lot-controlled destination must be a new different lot | No calling TR then separate Subcon commit; lot round-trip to existing original lot unsupported under existing rule |
| `IvInventoryPostingService` | Public multi-batch posting iterates and can return partial success across batches | Multi-line Subcon action must be **one atomic root**, not aggregate public batch results |
| `StockPostingCoordinator`, `InventoryValuationService`, `StockCostMethodResolver`, FIFO/WAC/Standard strategies | Existing stock posting and cost policy owners | No second costing system; feasibility of new conversion **unproven**, use approved coordinator/writers only |
| `ProductionStockWriter`, `ProductionPoolValuationService`, `ProductionCostReadiness` | Authoritative Production movement, pool valuation evidence and readiness; exact value reconciled | Offsite WIP requires method/eligibility proof; do not edit balances or attach phantom WIP |
| `PurchaseCostAdjustmentPostingService`, `StockValueAdjustmentWriter` | Existing procurement value-only posting path | Not automatically valid for component→FG conversion or subcontract fee; **zero-qty value adjustment cannot create FG qty** |
| `PoInvoiceService` | Certain PI lines validate net received GR and reject invoicing before physical GR | Fee PO/service acceptance requires exact compliant procurement contract, not fake GRN |
| `QaReadinessMessages`, `QaPolicyService` | Multiple physical/logical QA gates still frozen/record-only | QA **case** is not warehouse/WIP isolation. If QA required, post must BLOCK unless valid physical hold authorizes it |
| `IvStockMaster`, `IvBalLoc`, `IvWarehouse` | SKU color/size/lot/expiry controls; `IvWarehouse.WarehouseType` exists | Material identity and stock status are first-class, supplier warehouse type alone cannot prove all downstream source restrictions |
| `ErpWeb.Tests/Subcon/*` | Draft/blocked behavior tested primarily on in-memory SQLite | No Subcon SQL Server end-to-end physical/cost regression proof; **not ready** |

**Current source reference files to inspect on every gate:** `ErpWeb.Core/Inventory/IIvInventoryPostingService.cs`, `IvStockTransferService.cs`, `IvInventoryPostingService.*`, `ErpWeb.Core/StockLedger/StockPostingCoordinator.cs`, `InventoryValuationService.cs`, `Costing/*`, `StockValueAdjustmentWriter.cs`, `ErpWeb.Core/Production/Production{MaterialIssue,Output,FinishedGoodReceipt,StockReturn,WorkOrderTransfer,PoolValuation}*`, `ProductionStockWriter.cs`, `ErpWeb.Core/Planning/{BomExplosionService,PrBomCalc}`, `ErpWeb.Core/Purchase/{PoOrderService,PoInvoiceService,PurchaseReceiptCostSettlementService,PurchaseCostAdjustmentPostingService}`, `ErpWeb.Core/Quality/*`, `ErpWeb.Core/Subcon/*`.

# 2. Defects in v9 corrected by v10 (not merely reworded)

| ID / severity | Defect, omission or unsafe assumption in v9 | Mandatory correction / blocking gate |
|---|---|---|
| **C01 Critical** | “Source mode = one per job” does not cover WO-linked WIP **plus extra inventory materials** at vendor | One **output transaction owner**, but **each input movement/leg** has one authoritative source owner. Mix only via explicitly supported cross-owner atomic contract; else separate truthful transactions and block combined conversion |
| **C02 Critical** | Suggesting existing FIFO/WAC/Standard strategy automatically supports Inventory-only multi-component FG | No generic Inventory-only conversion owner proven. Define an approved **new transaction contract inside existing coordinator/valuation authority** if necessary, with exact input→output cost and reversible facts; never create Subcon-specific costing engine |
| **C03 Critical** | “Send 100, receive 100 same item” is assumed value-neutral in all date/layer conditions | Prove by FIFO layer lineage, WAC replay chronology, Standard valuation/variance, lot and later downstream usage. No invented receipt price/new layer; refuse if lacking legal return path |
| **C04 Critical** | Supplier stock separation described abstractly, not by actual consumers | Gate inventory **all-mutators/all-eligibility** matrix and WIP consumer matrix before source flag on; block Sale, MI, count, reservation, production source, QA misuse by server authority, not page filters |
| **C05 Critical** | Completion can appear correct when **service fee capitalization is missing** | Tenant-level approved fee policy required before financial go-live: existing supported capitalization OR approved expense treatment OR proven approved accrual/true-up. An unpriced/unvalued FG is NOT a valid workaround |
| **C06 Critical** | Multiple physical/financial effects across job could be independently posted and still shown “one task” | One atomic posting root **per business action**, including material use + FG create + exact ledger link; multi-day job is multiple immutable roots. If one requested combined action cannot be atomic, split into explicit operations with truthful states; never pretend one all-or-none result |
| **C07 Critical** | Possible conversion of component quantities without valuation lineage and subsequent reversal | Input cost layer/lot, output generation, partial qty, WAC effective time, Std variances, scrap loss and later Sales COGS must reconcile and rollback fail-closed |
| **H01 High** | No explicit supplier acknowledgement vs sender dispatch time | Optional pickup/acknowledgement/variance evidence; authoritative custody transfer occurs at a clearly defined posted timestamp; unsigned delivery does not override posted ledger silently |
| **H02 High** | BOM quantity freeze “on job creation” can block useful corrections while still draft | Job draft can recalculate/refresh with explicit user confirmation; **freeze source version, alternates, component snapshot at first physical post**, never mutate posted-snapshot, revise residual obligation through a versioned amendment |
| **H03 High** | BOM explosion may yield planning/issue shapes that are not a valid supplier kit | Verify purchased/MAKE/PHANTOM, operation-owned materials, alternatives, scrap%, supplier-provided inputs, multiple definitions; use appropriate mode and explicit assembly source validator; don't blindly take all explosion nodes as Send lines |
| **H04 High** | Material scrap vs FG reject vs rework and abnormal loss can be double counted | Separate material loss/disposition from rejected *output* and from material included in completed output. Finance chooses normal vs abnormal treatment; no dual posting |
| **H05 High** | Insufficient explicit job-level allocation of stock when same SKU/lot sits at one vendor for multiple jobs | Physical `IvBalLoc` stays sole quantity authority; track **immutable job-to-source-event allocation/consumption references** and derived reservations; do not reuse shared vendor stock for 2 jobs without serialized allocation and unique event identity |
| **H06 High** | Partial FG output and supplier material return can race; v9 only states tests in general | Lock by supplier slice AND job material allocations under same branch lock; aggregate all proposed qty, prevent competing receipt/return/transfer/repair events; test exact race/timeout/duplicate |
| **H07 High** | Direct purchased-to-vendor material lacks title/custody and existing PO/GRN checks | Distinguish commercial title transfer, GRN-to-eligible supplier WH, who receives/invoices, tax and inventory method. NEVER create owned stock from external supplier delivery confirmation alone |
| **H08 High** | Production WIP transfer and Inventory material supply can both consume the same component valuation | Explicit WO issued materials vs separate supplier-stock components; no double `MaterialIssue`, output costs claimed once; check WO route/operation and pooled cost authority before output |
| **H09 High** | Post/rollback matrix insufficient for post-sale consumption, late PI fee, period-close and backdated replay | Verify reversible graph/generation by event type, including return-after-FG, Sales DO vs invoice stock owner, QA, Stock Count, PO invoice/credit, open-period replay support; prohibit orphan corrections |
| **H10 High** | “35 scenarios” read as full P0 implementation rather than valid market design catalogue | Separate **release-certified baseline**, gated extensions and unsupported variants; avoid 35 handlers and long operator menus; no claim of support without passed test for that scenario |
| **H11 High** | Inventory-only BOM source uses Planning Product Definition services that may be inaccessible | Explicit read-only Subcon BOM permission and internal scoped loader; *do not* remove Planning gate or create second BOM master. Optional ad-hoc job kit; reusable BOM maintenance uses existing Product Definition authoring under explicit rights |
| **H12 High** | Supplier statements, shipping documents and special local compliance could be forgotten | Printable supplier dispatch/packing/BOM/return documents, goods acknowledgement, per-supplier monthly movement/custody reconciliation; optional customs regime metadata with regulatory verification |
| **H13 High** | Overly simplistic “minimal clicks” conflicts with stock-lot and approvals | Auto-fill **only verified** defaults; inline quantity grid plus existing lot/warehouse picker for exceptions. Count exception handling and safety errors in UAT, not just click count |
| **H14 High** | v9's quantities could imply yield allocation by proportional FG count | `PrBomCalc` is **planned** BOM estimate only; actual inputs/lot, scrap/offcuts, accepted/held/rejected outputs are distinct facts; no inferred cost from target BOM quantity |
| **M01 Medium** | No explicit ERP tenant capability/profile matrix | Company-level policy per scenario, lot, QA requirement, inventory-only vs WO, branch; no single “enable everything” checkbox |
| **M02 Medium** | Job status can hide outstanding vendor stock if “complete” set manually | Compute eligibility from immutable posted facts, output obligation and bill/QA obligations; status change never writes down stock |
| **M03 Medium** | G0 “two architecture options” could reward two unsafe proposals | Reject both if unproven. A single source-grounded permitted design with explicit blockers is preferred over invented alternatives |
| **M04 Medium** | No clear tenant migration for phase-1 drafts and historical quantities | Idempotent additive SQL plus read-only preflight and archival/migration for existing draft IDs; never silently mark old drafts POSTED |
| **M05 Medium** | Specialty Customs/SST/e-Invoice treated as generic tax rates/assumptions | Use existing PO/PI tax/e-Invoice authority; regulated LMW/GPB approval/statement tracking only as optional audited configuration; no new tax engine |

**Review decision:** v9 provides a useful business catalogue but is **not safe to approve for transactional implementation**. v10 is the corrected roadmap; G0 must still prove the missing core integration.

---

# 3. Malaysian SME job taxonomy (simple choices for operators)

**Normal launch actions (four buttons):** `Assemble Product`, `Send for Processing`, `Receive Goods`, `Return Materials`. Secondary actions inside a selected job: `Send More`, `Report Damage`, `Rework`, `Close Job`, `Print`, `History`. **No separate modules or 35 choices.** A simple user can skip unnecessary sections. Complex options are shown only when relevant and permitted.

| Job intent | Business example | Physical input/output rule | Baseline / restriction |
|---|---|---|---|
| **Same-item processing** | polish, paint, plate, wash, repair item A | physical custody of item A outbound and same item/eligible lot inbound; no implied manufactured new SKU | P0 for Inventory-only; lot-gated |
| **Assembly from BOM/components** | fan, harness, LED lamp, furniture, packaging set | one output FG with multiple actual owned inputs; leftovers and scrap accounted separately | **Primary P0 business requirement**, gated inventory conversion |
| **Transform one item into another** | RAW-BRACKET → COATED-BRACKET | consume original, create explicitly different SKU with exact value, not stock transfer | same protected conversion as assembly |
| **Repacking/cutting/kitting** | bulk → packs; roll → pieces | material quantities may differ radically; yield/by-product/offcuts must be explicitly modeled | simple one-output only if conversion proven; variable/multi-output additional gate |
| **External WO process** | partially manufactured WIP sent for powder coating | exact released WO/operation/WIP identity, source and next-stage claims | Production-only P0, separate WIP capability gate |
| **Supplier buys everything** | finished product supplied complete | normal purchase FG flow, optionally link Subcon job as tracking record | **Do not post a second FG receipt** |
| **Service-only** | calibration/installation without company-owned materials | normal service PO/AP, no physical stock leg | no fake stock |
| **Customer-owned / consigned / intercompany** | processing third-party goods; company B manufactures for A | separate title and custody contract, no owned inventory value until appropriate | P2 until independently audited |

**Ownership is NOT the same as custody.** Company-owned stock held at supplier still belongs in company stock valuation, but cannot be picked by ordinary internal stock source commands. Supplier-owned materials used in a job are **not** booked into company-owned inventory simply to match BOM. Output ownership, invoicing and tax treatment have their own contractual evidence. Avoid asserting universal accounting rules for all customers; record the signed-off policy.

## 3.1 Mandatory release-candidate flows

**FLOW A (Inventory, same-SKU):** New Job → supplier + item + source lot/WH + qty → Save → Post Send Out → print dispatch note → supplier acknowledgement (optional/configured) → choose supplier/job → see Sent / Already Back / Outstanding → Receive Now → permitted QA → Post → supplier holding 0/remaining → optionally Rollback in reverse dependency order. FIFO/WAC/Standard trace proves no duplicate costs.

**FLOW B (Inventory, BOM Assembly — PRIMARY):** New Job → supplier + output SKU + 50 UNIT → select active Product Definition revision (or job-only manual kit) → snapshot + recommended material quantities → select eligible stock source slices → send 110 A and 165 B → partial completion **45 FG** with **actual** use 90 A / 135 B → separately approved scrap 4 A / 5 B → supplier returns unused 16 A / 25 B → finish job state **5 FG obligation remains**, vendor stock 0. If output accounting/valuation incomplete, **must not Post FG**; UI may save draft only. If further 5 units required, amend/send more materials, don't auto-complete.

**FLOW C (Production external operation):** Select released WO and approved `SUBCONTRACT` operation → show exact prior WIP lot and authorized available qty → send partial WIP and optionally separately supplied material with explicit non-duplicating source link → hold offsite WIP from next internal operations and FG → return partial treated WIP with valid QA/stage and financial lineage → finish internal production/FG through existing authorities → rollback only before active dependent operations.

**FLOW D (Supplier supplies all):** Optional job tracking → normal Supplier PO/GRN (and PI) for owned FG → attach supplier delivery/result to job → job inquiry reflects **referenced** procurement facts, no Subcon FG stock receipt or WIP value duplicate.

**FLOW E (Mixed-owned kit):** Own case sent through existing ledger; supplier screw priced in approved service/finished input arrangement; receipt uses actual company component costs + approved supplier expense/capitalizable value ONLY when financial policy and existing owner accept. Never fabricate issue/receipt of supplier screw in `IvBalLoc`.

## 3.2 Exact example — quantities, no manufactured assumptions

For FG-BOX qty **50**, frozen BOM (for **50**) needs **100 A + 150 B**. Business sends **110 A + 165 B**. First output **45 good FG**; reported actual input **90 A + 135 B**; separately recorded **4 A + 5 B** as approved scrap; return **16 A + 25 B** unused. Per-item reconciliation:

```text
A: 110 posted to supplier -90 consumed -4 approved scrap -16 posted returned = 0 at supplier
B: 165 posted to supplier -135 consumed -5 approved scrap -25 posted returned = 0 at supplier
FG: 50 ordered -45 physically received = 5 still expected (unless approved amendment).
```

This example's financial input value at hypothetical RM5/A and RM2/B is RM880 sent, RM720 actual consumed, RM30 scrap and RM130 returned. If separately-approved capitalizable service fee is RM180, one *illustrative* approved policy could recognize RM900 finished stock (720+180), RM30 scrap expense and RM130 remaining raw material value, subject to actual source-layer cost, method and policy. **Those figures are not system costing equations or universal policy**; FIFO/WAC/Std original valuation facts govern. A permitted policy may treat normal production loss differently. **Do not count both scrap expense and absorbed scrap cost**. Actual output consumption is not calculated from BOM plan or job target.

## 3.3 Full scenario catalogue — classification, NOT promised out-of-box support

**Status at reviewed HEAD:** none of the 35 physical Subcon scenarios are implemented live. Their classification is a roadmap, not proven code. **Core release requires A/B plus the same-SKU route; other scenarios only when separately validated.**

| ID | Real-world scenario | Example / required outcome | Profile / priority |
|---|---|---|---|
| SC01 | Same item, same SKU after process | Steel bracket polished, item remains BRACKET-A; original qty/lot/cost preserved | S / P0 |
| SC02 | Same physical item, **different processed SKU** | RAW-BRACKET to COATED-BRACKET; conversion source and output must reconcile | A / P0 |
| SC03 | **BOM assembly with company-supplied materials** | Issue bolts, casings, PCBs; receive completed fan/LED light/box-build | A / **P0** |
| SC04 | **Unused BOM components returned** | Receive 80 FG; supplier returns 25 unused switches and 30 casings | A / **P0** |
| SC05 | Partial assembly and staged FG deliveries | Send for 100; receive 30 + 50 + 20 over several dates | A / **P0** |
| SC06 | Extra materials sent later | Supplier reports shortage/damage; add supplier material delivery linked to same job | A / P0 |
| SC07 | Scrap, damage, shortages, overconsumption | 3 boards damaged, 2 missing; approved disposition; never silently consume | A / **P0** |
| SC08 | BOM substitutes and alternates | PCB-B replaced by approved compatible PCB-C; preserve version/approval/lot | A / P1 |
| SC09 | Mixed ownership of materials | Company supplies casing, supplier buys screws/labels; no phantom company stock | A+C / P0 |
| SC10 | Supplier supplies **all** materials | Vendor supplies completed product; link PO/FG GRN; don't pretend company sent BOM | C / P0 |
| SC11 | Direct purchased material sent to subcontractor | Vendor A ships components to assembly vendor B; company's GRN owns stock at B if title passed | A+C / P1 |
| SC12 | Vendor holds company stock for future jobs | Reuse leftover screws in next job with controlled allocation/transfer | X / P1 |
| SC13 | Multi-warehouse / lot-controlled inputs | BOM components from two WHs and multiple batches; exact lot genealogy | A / P0 |
| SC14 | Supplier delivers good, reject and rework | 90 accepted, 5 held, 5 rejected; rework/reship without duplicate finished goods | A+P / P0 |
| SC15 | Goods never physically leave until subcontractor collects | Draft/packing vs **actual custody transfer** distinguished; optional pickup confirmation | S+A / P1 |
| SC16 | External process on WO WIP | Machine shop -> powder coating supplier -> internal assembly; partial operation handoff | P / P0 for Production users |
| SC17 | Multiple subcontractors in sequence | Vendor A cuts -> Vendor B polishes -> own warehouse; inter-vendor custody with proof | X+P / P1 |
| SC18 | Partial WO outsourcing / multiple WO aggregation | Several WOs at one vendor; per-WO quantity and cost trace; no blended undocumented WIP | P+X / P1 |
| SC19 | Repacking / bundle / kitting | 1 bulk case into 10 retail packs, gift set from five SKUs | A / P0 |
| SC20 | Cutting/slitting, variable yield | 100m roll -> 96 cut pieces + offcuts; UOM/output/scrap accounting | A+X / P1 |
| SC21 | Multiple FG or by-products from one BOM | Main FG plus usable offcuts; approved value allocation and recovery | X / P2 |
| SC22 | Customer-owned materials processed by subcontractor | Customer supplies fabric/parts; quantity custody but **not** company inventory valuation | C / P2 |
| SC23 | Supplier-owned consignment or subcontractor's own stock | No company ownership until title transfer; never include in IvBalLoc merely for visibility | C / P1 |
| SC24 | Home-based/individual piecework | Garment sewing, embroidery, packaging with partial collections and wages/service charges | S+A+C / P1 |
| SC25 | Electronics SMT / harness / box-build | Multiple input lots, traceability, QA and serialized FG where repository supports it | A / P1 |
| SC26 | Food/cosmetic co-packing (where allowed) | Batch/expiry, provided recipe/materials, rejects, third-party packaging and traceability | A+X / P1 |
| SC27 | Return/rework of customer's rejected FG | Repair under original SO/DO/RMA lineage; avoid counting as newly manufactured FG | S+P / P1 |
| SC28 | Supplier processing fee per good unit / input qty / flat fee | Service PO + invoice partial; cost basis approved; no auto capitalization shortcut | C / P0 |
| SC29 | Post-completion invoice / credit note / fee variance | Late invoice, rate changes, supplier debit/credit; restatement or expense by approved policy | C / P0 |
| SC30 | Subcon output delivered direct to customer | Only if custody, ownership, goods issue and sales shipment owner are all proven; no double DO/FG | X / P2 |
| SC31 | Service-only work with no materials/stock | Calibration/certification/installation: use service PO/AP; optional job tracking, no fake inventory | C / P1 |
| SC32 | Intercompany related factories | Marketing Co sends parts to factory company B, returns product; **cross-company ownership** is separate, not a normal within-tenant transfer | X / P2 |
| SC33 | Supplier exchange / replacement material or extra material | Vendor swaps defective part with own material; approval, exact ownership/value record | A+C / P1 |
| SC34 | Product BOM updated while job open | Freeze old version at job creation; amendment/revision for remaining only, no silent change | A / P0 |
| SC35 | Outsource prototype / one-off assembly | Job-specific component list with no persistent Product Definition required | A / P0 |

**Industry relevance:** Malaysian contract manufacturers explicitly describe mixed in-house, purchased and consigned components; metal coating/plating/finishing; electrical/electronic assembly; and PCB/harness/box-build. These are *domain examples*, not evidence that your current ERP supports those paths. See sources listed in Appendix B.



**Risk-tier classification rule:** P0/P1/P2 in this catalogue denotes business value only; risky P0 flows are blocked until valuation/quantity approval. Production-only scenarios may remain unavailable to inventory-only tenants, and vice versa. No route is released based solely on its name or industry example.

# 4. Architecture: one module, existing owners, per-leg source authority

```text
Subcon Workbench (DevExpress; ONE screen family)
   ├── Jobs: processing / BOM assembly / WO external operation / procurement-linked
   ├── Drafts: planned BOM, required input, vendor and chosen output
   ├── POST business events: Dispatch / Receive+Consume+Output / Return / Scrap / Reversal
   └── Read-only inquiry, print, supplier statements, audit
                  │ validated application commands
                  ▼
     Subcon Application/Orchestrator (business-specific rules only)
          │       │          │            │
   BOM reader   Inventory   Production   Procurement/QA references
     (scoped)   adapter      adapter       (authorized owners)
          │       │          │            │
          └───────┴──────────┴────────────┘
                  │ exactly ONE authorized posting root per physical event
                  ▼
       Existing StockPostingCoordinator + branch lock + period/date guards
          │            │             │
   Inventory ledger  Production   Costing/valuation authority
   actual stock legs WIP writer     IInventoryValuationService
       IvBalLoc      pool facts      FIFO / WAC / Standard
          └────────────┴─────────────┘
                    sealed immutable facts + reversal generations
                  ▼
        Sales, Production, PI/CN, stock count and QA eligibility checks
```

**Precisely one posting owner per physical leg, NOT necessarily a single source type per *job*.** A WO subcontract job can have WIP and extra inventory material lines. But mixing those legs in one final output requires a proven combined posting/valuation root. Do not combine by sequential, separately committed services. If combined atomicity cannot be proved, prohibit that job variant until approved; do not create a new costing or balance engine to conceal complexity.

**Recommended new Subcon interfaces are *descriptive*, not already implemented:**

- `ISubconJobService`: CRUD, revision/approval, job draft/search, immutable BOM snapshot; no ledger mutation.
- `ISubconBomReadService`: scoped `Subcon.Workbench.Access` + `Subcon.BOM.View`, read active Product Definition/revision without `PlanningProductDef.Access` bypass; use existing BOM math/explosion, no global BOM copy. Design existing authorization before creating a new `PermissionCodes` without registry support.
- `ISubconPostingOrchestrator`: dispatch / receipt-conversion / materials return / scrap / rollback command contract, stable GUID IDs and fingerprint. **No public Post implementation until approved source adapter**.
- `ISubconInventoryMovementAdapter`, `ISubconProductionMovementAdapter`: thin transaction adapters invoking existing authoritative writers and validation under one posting context; no cost calculation.
- `ISubconSupplierStockReader`: read-only projection from active posted physical slices plus unique job/source-event allocations, not writable stock counts.
- `ISubconReconciliationReader`, `ISubconStatementReader`, `ISubconPrintDataSource`: server-paged read-only reports with authorized data.

**Interface design must preserve existing current Subcon routes where possible**; avoid breaking 2026-11-13 draft records. Rename no existing service/code simply to match this proposal if an additive method fits. Do not invent public methods inside current `IvInventoryPostingService` or `ProductionStockWriter` without the protected review gate.

# 5. Data model (provisional; G0 must verify exact EF/SQL schema and migrate safely)

**Existing Subcon draft records:** `SubconPolicy`, `SubconDispatch`, `SubconDispatchLine`, `SubconReceipt`, `SubconReceiptLine`; retain existing IDs, document numbers and draft audit. **Don't drop/recreate old rows.**

| Proposed additive entity | Minimum authoritative fields | Why it exists / boundary |
|---|---|---|
| `SubconJob` | Uid, trusted Company/Branch, DocNo, supplier, site, job intent, output SKU, output qty+UOM, ownership policy, source profile, status/revision/version, created/modified/rowversion | Long-running business obligation, **not** stock balance |
| `SubconJobSourceSnapshot` | Job, source type `INVENTORY/WO/PO_REFERENCE`, BOM hdr/version/definition, selected alternative group, frozen date, WorkOrder/operation/route/lot IDs if applicable, source hash | Freeze exact input contract at **first physical Post**, with versioned amendment thereafter |
| `SubconJobMaterial` | Job+line, material SKU, BaseUOM, planned qty, supply owner (`COMPANY/SUPPLIER/OTHER`), issue method, chosen alternative, source BOM line/operation, optional scrap allowance, status, rowversion | Planned inputs (not availability or consumed stock) |
| `SubconJobOutput` | Job+line, SKU+color/size, requested qty/UOM, expected delivery, status/QA policy | Planned FG outcome separate from physical receipt |
| `SubconDispatch` extensions | Job UID, posted status/revision, location/source reference, business date, delivery/ack metadata, request ID | Dispatch transaction header with posting generation |
| `SubconDispatchLine` extensions | JobMaterial ID, stock item source slice/lot/expiry/UOM/factor, source balances, posted facts, optional WO lot/operation ID | Exact physical source identity and allocated qty, not `StdQty` cache |
| `SubconReceipt` extensions | Job UID, output line, acceptance/QA status, receipt action revision, request ID | Physical received output including same-item and produced FG |
| `SubconInputUseLine` | Receipt/conversion event, JobMaterial, posted vendor stock identity, actual use qty/base, value evidence IDs | Input consumption *allocation reference* and actuals |
| `SubconMaterialDisposition` | Job/line, action (RETURN, NORMAL_LOSS, ABNORMAL_LOSS, SCRAP, REWORK etc), source physical/lot, qty, reason and approval | Disposition request, no independent financial arithmetic |
| `SubconPostingLink` | action type, job/event, source module/authoritative posting id, source doc revision, stable request GUID, fingerprint, original posting, reversal posting and lineage | Exact idempotency and audit linking; unique `(company,branch,action,request)` and per event/revision identity |
| `SubconSupplierAssignment` | tenant/branch, approved supplier+site, mapped external warehouse/location, live window, status and options | Suppliers and stock custodians differ; mapping and isolation server-gated |
| `SubconComplianceRef` (conditional) | regulatory profile, Customs approval/period, declaration references, signed statement links, transport/ack attachments | Only for regulated users; not generic tax decision engine |
| `SubconServiceLink` (conditional) | PO revision/line, accepted service qty, PI/CN line, currency/tax, source invoice, allocation authority | Commercial evidence; not directly posted cost |

**Do not create:** `SubconCostLayer`, `SubconWAC`, Subcon FIFO layer manager, parallel `SubconWarehouseBalance`, independent `SubconWipBalance`, or any financial value table that becomes a second posting source. A reconciliation view/projection is fine if every number is derived from sealed existing physical/cost events. Never store a writable “current vendor qty” without a proved authoritative projection and reconciliation contract.

**SQL Server minimum safeguards:** unique company+branch+doc number, strict FK from physical allocation→exact job source and posting link, CHECK `Qty > 0` and `BaseQty > 0` for event legs, UOM evidence, type discriminator, `rowversion`, filtered unique active generations, audit stamps, company/branch isolation, allowed status transitions in services, deterministic lock ordering, nullable only for legitimately pending draft facts; validate FK/cross-tenant enforcement in service. Additive numbered schema upgrades with preflight, per-tenant success log and rollback DDL plan; no automatic startup schema migration. Historical draft migration must be reversible in a database backup/staging rehearsal. Do not assume SQL CHECK constraints can cover multirow totals; enforce those under a branch lock and through DB constraints/indexes where possible.

**Draft stability defect**: `SaveDispatchDraftAsync` replacing all detail lines can sever references to draft receipts. Preserve line IDs, or block referenced line edit and allow controlled replacement after dependent draft disposal; show human-friendly message. `SaveReceiptDraftAsync` must group duplicate allocation lines, exclude current receipt previous allocations on edit, enforce rounded sum, and serialize conflict under SQL Server; never trust UI totals alone.

# 6. Non-negotiable quantitative and valuation invariants

## 6.1 Per-slice physical conservation (same effective time)

For each `(company, branch, ownership, item SKU/variant, warehouse, location, lot, status, UOM/base identity, active posting generation)`:

```text
Vendor-held company stock NOW
  = Verified posted opening/vendor inbound
  + company stock dispatch-in
  + owned direct GRN into vendor site (when title transferred and posted)
  + validated transfer-in from other vendor/job
  - actual material use CONSUMED into output (posted conversion)
  - approved separately posted loss/scrap/writeoff
  - posted unused stock RETURN to internal warehouse
  - validated transfer-out to another site/job
  +/- exact active reversal generations
  >= 0.

Job allocation/vendor-held claim <= actual source vendor physical slice qty
Sum of allocations across jobs for the same source lot <= eligible vendor slice qty
Receipts + accepted holds + final rejections + outstanding output obligations
  reconcile to verified physical outputs and approved job change (no duplicates).
```

**Do not sum FG units, screws, meters of fabric or component stock into a single quantity KPI.** Inventory quantity is by item/base UOM/lot, not by independent Subcon summary. Job planned output, received FG, supplier-held component stock, consumed inputs, rejected output and QA accepted stock are **different facts**. For any “at supplier” number show whether it is physical stock, planned job quantity or received-not-accepted goods.

**Multi-job supplier stock:** If same 100 A/lot is pooled at one supplier for jobs J1/J2, Subcon needs posted, unique, lock-serialized **job allocations to existing source event**; item warehouse balance remains the only stock ledger. Source identity must disallow hidden borrowing across jobs. If reliable allocation across pooled jobs is unavailable, require dedicated vendor custody slice/job isolation or block cross-job pooling. No editable job-level `OnHand` surrogate.

**WO/WIP:** `Onsite eligible WIP + Offsite company-owned WIP = active owned WIP` by exact WO/pool/route. Same physical unit cannot be treated as both available for next internal operation and held at vendor. Additional inventory components given to the supplier must not be included in WIP value until actual authorized issuance/consumption; no duplicate material issue.

**BOM estimate**: `PrBomCalc.RequiredQty(targetQty, baseQty, StdQty, ScrapPercent)` is planning quantity. `ScrapPercent` is **planned allowance**, not already-booked wastage. Actual output may consume more or less and must be explicitly entered or evidence-backed. Do not hide supplier-reported losses as “BOM variance.”

**UOM/variant:** `IvStockMaster` has `Size`, `Color`, `LotControl`, `ExpiryControl`, `StdUom`; `IvBalLoc` has physical stock slice identity/status/UOM. Use verified `IvItemUomConversion` and `IvQty` methods only; preserve base/display qty per line, deterministic decimal rounding and leftover rounding residual. Don't silently allow 0.0001 of indivisible `PCS` because database supports decimal(18,4). Lot and expiry genealogy follows source identity; serial/barcode extensions only if current repository actually supports them and approved for that item.

## 6.2 Inventory same-item dispatch and return — not assumed safe

- Post via **one** approved Inventory stock transfer/transaction root that also records the Subcon posting link and document status in the *same* SQL transaction/branch lock. `IvStockTransferService.PostAsync` currently does **not** expose a proven caller-shared in-transaction API; do not call public Post and then save Subcon separately.
- Validate before and *inside* source lock: active stock, sufficient eligible unreserved qty per exact WH/lot, valid vendor mapped destination, not in closed/future date, authorized role, lot exists and chronology permits.
- Lot-controlled TR requires different/new destination lot under current source; returning to original existing lot is NOT a proven legal stock-transfer operation. Do not change current global lot rule or mint random lots as workaround; approved Subcon-specific return contract with original lot genealogy needed for lot pilot.
- WAC values can change chronologically; FIFO split/cost-layer lineage and Standard valuation/variance must remain with existing authoritative valuation facts. A physical transfer is not financial manufacturing or a vendor purchase.
- Rollback checks exact origin, linked receipts, later sale/issue, active generation, current/future stock, count/period close, replay support. Reverse only legal most recent root; do not delete ledger history.

## 6.3 BOM assembly/conversion — mandatory valuation gate before FG POST

**Physical transformation A + B → FG cannot be implemented as `MI` then unrelated `MR`**, regardless of separate batch success; it would book expense/stock with no proved product input-cost link. Nor does `StockValueAdjustmentWriter` create FG physical quantity. Required exact one-root posting contract:

```text
Subcon conversion action (one stable GUID and document generation)
    1. Lock job, source vendor stock slices, BOM snapshot, last posting lineage, period
    2. Recheck actual used A/B by lot/base UOM; availability >= use; supplier job allocation
    3. Resolve source financial cost/valuations via EXISTING costing authority
    4. Record actual component OUT, same authoritative inventory physical writer
    5. Record new FG IN (eligible/held status), same authoritative stock posting root
    6. Link source OUT valuation facts to destination FG IN valuation evidence,
       including approved normal scrap costs, source lot splitting and Standard variance
    7. Update job/receipt/posted allocations/audit in SAME transaction; seal once
    8. On failure abort entire operation; no source use, no FG, no cost/value orphan
```

**Current `IInventoryValuationService` does not establish this generic conversion contract by its mere existence.** G0 must identify whether a complete existing physical writer and a value-generation path exist; if not, design a *minimal, separately approved* new posting command/owner **inside existing V2 ledger**, reusing strategy/valuation facts and replay graph. All financial and quantity algorithms in protected existing owners remain unchanged **unless user explicitly authorizes exact diff**. New source-specific mapping/allocation evidence is acceptable where needed; it must not become a second cost engine. Fail if output valuation lacks verified input cost.

**FIFO:** exact consumed source receipt layers/splits, remaining layer qty and values, FG source allocation and reversed cost, no reuse or duplicated receipt layer. **WAC:** effective business time, sale/receipt order and replay/closed-period guards, zero-qty residue. **Standard:** effective-dated approved cost, input/output difference and variance authority. Never fake `0` cost or use price list/current purchase price.

**Scrap and QA distinction:** If a component is consumed as part of FG production, it cannot simultaneously be scrapped as a separate loss unless that is explicitly another physical quantity. Rejected FG physically received is still stock/custody (may be held) and has different disposition from unused raw components. Cost of normal loss vs abnormal loss needs signed-off accounting policy; test no double deduction from value or on-hand. Rework receipt is NOT an additional good FG receipt on the same original output.

## 6.4 Production source — cost pool authority

Work Order mode must use the **frozen released WO route/operation/material snapshot**, not current `PrBomHdr` or a fresh ad-hoc inventory kit. The process type `SUBCONTRACT` indicates duration semantics in current code but does not create stock custody. Existing `ProductionWorkOrderTransferService` is full-WIP inter-WO and cannot implement partial supplier dispatch. Need verified approved partial offsite movement and eligibility controls. Use `ProductionStockWriter` and `ProductionPoolValuationService` with valid movement semantics and `ProductionCostReadiness`; do not manually modify `ProductionBalLot.Qty`, `BaseQty`, `TotalCost`, `AverageUnitCost`. Verify next-operation eligibility, Daily Production, FG receipt, WO return, QA, corrective replay and rollback. If a service would create a WIP receipt with zero cost while FG exists, **NO-GO**.

## 6.5 Supplier processing fee and commercial settlement (separate financial gate)

- **Commercial ownership:** linked approved PO service/charge line (or permitted service acceptance document), supplier invoice and CN/DN by existing `PoOrderService`/`PoInvoiceService` policies, currency/tax/e-Invoice rules and permissions. v9 does not prove a service PO invoice-without-GRN workflow; current `PoInvoiceService` contains received-qty checks. Do not fake GRN or set receipt qty without approved service-acceptance path.
- **Financial policy must be explicit per company/job type:** (a) cost capitalized into specifically traceable FG/WIP with valid existing StockValueAdjustment/Production conversion authority; (b) approved expensed fee when correct accounting policy; (c) approved accrual with controlled later true-up. Approval of policy does **not** by itself prove current code implements it.
- Capitalization before output, after partial output, after sale, after closed period and after supplier credit are **different financial graphs**. Trace which on-hand and consumed/COGS values change; do not stuff full invoice into first partial FG or silently revalue sold goods. 
- `PurchaseCostAdjustmentPostingService`/`StockValueAdjustmentWriter` are **candidate existing value adjustment patterns**, not universal Subcon cost posting. `ProductionConversionCostFacts` cannot simply be written from a supplier invoice without valid WO/output authority.
- **Full financial go-live** requires approved treatment of expected processing fees and periods; where neither capitalization nor compliant expense treatment is possible, block the job type.

## 6.6 Immutable posting/rollback protocol

**Business actions and status:** Job draft → Active → Partial/Completed/Closed; individual Send Out/Receive/Material Use/Return/Scrap transactions `NEW → POSTED → permitted ROLLBACK (active reversal facts)`; `CANCELLED` only unused draft, not posted document. Do not change job status directly to conceal open inventory or liability. Reopen after reversal must preserve historical audit and source revision.

**For every action:** trusted company+branch, role and source-module permission, input readiness, stable `RequestId` + fingerprint, document/line revision, exact active source posting, deterministic lock order, double-submit prevention, period/future business dates, Stock Count freeze, per-event partial reconciliation, valuation & physical fact consistency. **Rollback dependency checks**: later vendor transfer, FG conversion, QA hold, customer DO/Invoice shipment, rework/scrap, PO invoice cost allocation, issued WIP, month close; if unsafe, refuse with exact guidance. A successful timeout replay must return prior result without a second stock effect. Partial multi-line failures must rollback all event legs and statuses.

---

# 7. UX implementation contract — CURRENT theme and live UI skill files are binding

## 7.1 Mandatory skill-read sequence (read actual HEAD, not just filename)

1. `.agents/skills/erp-transaction-ui-standard/skill.md` (router).
2. `.agents/skills/erp-ui-core/skill.md` (shared shell, theme, accessibility, grid, responsive).
3. **Both** `.agents/skills/inventory-transaction-ui/skill.md` and `.agents/skills/production-transaction-ui/skill.md` because Subcon spans Inventory-like lots and Production-like workspaces. When PO/PI linked, read `.agents/skills/purchase-transaction-ui/skill.md`; for a Sales consumer change, `.agents/skills/sales-transaction-ui/skill.md`.
4. `.agents/skills/erp-large-data-smart-lookup-standard/skill.md`, `.agents/skills/erp-inline-search-standard/skill.md`, `.agents/skills/erp-grid-row-actions-left/skill.md`, `.agents/skills/erp-entry-footer-standard/skill.md`, `.agents/skills/devexpress-dxgrid-column-readability/skill.md`.
5. `.agents/skills/productionv2-costing/SKILL.md` **plus** `references/stock-balance.md`, `inventory.md`, `production.md`, `procurement.md`, `replay-rollback.md`, `testing.md`, `repository-evidence.md`, plus `sales.md` when Sales source/consumers touched.
6. For actual DevExpress controls, read local `.agents/skills/devexpress-blazor-grid`, `devexpress-blazor-combobox` skills (if applicable), but **do not replace established `SmartLookupInput` or pickers with a giant preload ComboBox**.

**Inspection references:** `ErpWeb.UI/Inventory/Transactions/{IvStockTransfer,IvMiscReceipt,IvMiscIssue}*.razor[.cs/.css]`; `ErpWeb.UI/Planning/WorkOrders/{PrMaterialIssueEntry,PrDailyProductionEntry,PrFinishedGoodReceiptEntry,PrWorkOrderEntry}*.razor[.cs/.css]`; `ErpWeb.UI/Planning/Components/PrMaterialIssueBomDialog.razor`; `ErpWeb.UI/Purchase/Transactions/{PoOrder,PoInvoice}*.razor[.cs/.css]`; `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.*`; `ErpWeb.UI/{Inventory/Lookups/IvStockMasterPicker,Inventory/Lookups/IvBalLocPicker,Purchase/Lookups/PoSupplierPicker}.razor`; `ErpWeb/wwwroot/css/{site,inventory-chrome}.css`; current `ErpWeb.UI/Subcon/*`.

## 7.2 One workbench, no fifth visual language

```text
┌──────────────────────── Subcontracting ────────────────────────────────┐
│  [ Assemble Product ]  [ Send for Processing ]                         │
│  [ Receive Goods ]     [ Return Materials ]                  [Help ?]  │
│                                                                         │
│  Drafts 8    Supplier stock 4 jobs   Due soon 3   Awaiting receipt 2   │
│  Search [Supplier / Job / Product] 🔍      Filters [Status] [Mode]       │
│                                                                         │
│  Actions | Job       Supplier      Work          Output   Due   Status  │
│  View    | SC-101    ABC Assembly  50 FAN-01     45/50   20/11 Partial │
│  View    | SC-102    XZ Plating    BRACKET       90/100  24/11 Partial │
│                                                                         │
│   Click job → summary / sent materials / returned / used / FG / history │
└─────────────────────────────────────────────────────────────────────────┘
```

- Base `iv-page`, `iv-hero`, `iv-card`, DevExpress Fluent/theme tokens; isolated `.razor.css` only. **No new palette**. Desktop row action fixed **left**, `CommonDataGridEx` with persistent unique `GridKey`; compact mobile list; server paging/filtering and debounce. Do not display joined “total items” for mixed UOM.
- `Draft` and **posted supplier stock** KPIs are separate, labeled. A supplier job may show outstanding planned **FG qty** and vendor-held *material* quantities in separate blocks; never call both “Still out”. All totals from authoritative posted records, not first-page rows.
- A single job can have multiple posted Send Outs, Receipts, Material Returns, approved Loss events and invoice links; users see clear chronology and remaining work without navigating to other modules for each line.

## 7.3 Normal flow screens (operator hides technical complexity)

**Assemble Product — normal entry**

```text
Supplier [ABC Assembly          🔍 ]   Due [date]
Product  [FAN-01               🔍 ]   Build Qty [100 PCS]
BOM      [STANDARD v3 ▼]  [View materials]  [Refresh while DRAFT]
Materials (auto-populated, one grid)
Item    Name         Needed  At WH  Send Now  UOM   Source / Status
MOTOR   Motor        100     120    100       PCS   MAIN/BIN1 ✓
BLADE   Fan blade    300     400    300       PCS   MAIN/BIN2 ✓
SCREW   Screw        400     1000   420       PCS   MAIN/BIN3 ✓ (extra)
[Auto select eligible stock] [Edit selected lot] [Print BOM/packing]
                                   [Cancel] [Save Draft]
[Post Send Out]  ← separate permitted business action, not Save
```

- Reuse `PoSupplierPicker`, `IvStockMasterPicker`, `IvBalLocPicker` and server-paged lookup; do **not** type warehouse/location/lot IDs from memory.
- **Hybrid entry rule:** display 20+ BOM lines in a **compact inline grid with editable Send Now Qty**, but open existing verified stock-balance picker / line popup for complex warehouse, source-lot split, UOM exceptions, alternates and material ownership. Do not make every row a popup; do not add dozens of always-visible dense input columns. Show shortage and invalid source inline, explain on hover/expanded details. Normal path uses one submit for all lines; Post validates again under lock.
- Output SKU and BOM details selected from **current `PrBomHdr`/`PrDefBOM`** only where permitted; default active definition if unambiguous, otherwise require explicit choice. A manual one-off kit must be captured in **job-only snapshot**, not a second global BOM master. For repeat jobs, direct users to existing Product Definition authoring with proper rights rather than copying records into Subcon.
- **Draft BOM revision refresh** must show exactly what quantities/alternatives change and require confirmation; once physically posted, no silent BOM switch. Additional material shipments use job's frozen requirements and documented amendments.
- Save Draft is neutral; **Post Send Out** is separate, permission gated, and cannot be shown enabled while adapter blocked. Print dispatch note after successful actual posting, draft packing preview watermarked `DRAFT — NOT DISPATCHED`.

**Receive Goods/FG**

```text
Supplier [ABC Assembly      🔍]  Job [SC-101  • FAN-01  • due 20/11 🔍]
Product FAN-01    Planned 100     Already physically received 45
Receive now [30]      Good [28]  Held [2]  Rejected [0]   [QA details]
Materials used NOW (suggested from frozen BOM; actuals confirmed)
MOTOR  Suggested 30  Actual [30]  Supplier-held 55  SKU/lot [selected]
BLADE  Suggested 90  Actual [90]  Supplier-held 165 SKU/lot [selected]
[Advanced: substitutions, losses, partial, notes]
                                             [Cancel] [Save Draft]
[Post Receipt + Verified Conversion]  ← all or nothing under one root
```

- Select **supplier → open jobs** (not a typed send-out doc + integer line); show only **posted eligible** supplier jobs in actual receiving choice. All quantities/mixed ownership and “materials at supplier” derived from active stock events and job allocation; drafts cannot look physically outstanding.
- On same-item job, hide BOM/material use entirely: `Sent/Received/Outstanding/Receive Now`, optional QA. On assembly job, suggested input consumption is based on **receipt qty** and frozen BOM; user confirms *actual*, reason/rights for exception. Do not auto-consume on draft save.
- Sum of Good+Held+Rejected+Other must equal **physically received output**, only if statuses represent disjoint physical goods; do not count held goods twice when accepted later. If QA physical gates are frozen, fail close for QA-required receipt; no fake “accepted” stock. Where QA is not required, output stock status still derives from proven posting.
- Optional **Return Leftovers** within same workbench, but recorded as a *distinct posted event*. Do not promise atomic combined FG + return unless one common root is actually proven. In the UI show a clear boundary: “Received FG posted. Return unused materials next.”

**Return Materials**: choose Supplier → Job → remaining posted supplier-held company stock by SKU/lot (positive only) → edit Return Now → choose verified receiving slice → Save Draft → Post Material Return → confirmation with remaining at supplier; allow before/after FG as long as sufficient unallocated material remains. Avoid retyping original numbers. A material used for FG cannot be simultaneously returned.

**Inventory same-item:** choose Supplier → source stock/qty → Save → Post Send → choose Job → `Receive Qty`, optional QA → Post; no BOM distractions. **Supplier-supplied finished goods:** provide `Open related PO/GRN` and show source receipt truth; do not create an alternate FG Post button.

## 7.4 UI standards acceptance, measurable not cosmetic

- Standard `New/Edit/View` behavior, `Save/Cancel` sticky bottom-right, `Close/Edit` for view, discard-unsaved confirmation, separate safe `Post/Rollback` command bar with confirmation and permission/status; no hidden effect on Save.
- Meaningful compact field widths and proper alignment; no duplicate SearchBox when toolbar search exists; `erp-inline-search` pattern, icon-only magnifier, `CommonDataGridEx` rows. Use `GridColumnSize` semantics, user width/layout persistence and left-fixed actions; no global wrapping.
- Company/supplier/product/warehouse/lot/WO/PO are lookup fields. Large-code lookup uses `SmartLookupInput` server-side resolution, not a huge client ComboBox. Keep keyboard Tab/Enter predictable; Enter must not accidentally Post.
- Clear messages, **not technical exceptions**: “Supplier has only 40 PCS of A remaining; you entered 50.” / “Cannot undo this send-out: 30 PCS were already used in FG SC-123.” Unknown exceptions logged with support reference.
- Roles: normal Storekeeper sees quantities; Finance/Supervisor sees processing fees under authorized `ViewCost`. No leaking cost into printed packing note or unauthorised search/inquiry/export.
- Device QA: Chrome/Edge desktop 1366px and tablet widths; responsive form/grid; no clipped captions/no global horizontal page overflow, no command offscreen. Test 20-line BOM entry and per-lot split, supplier receipt, return and error recovery.
- Prototype UAT: **5 unfamiliar nontechnical SME users × 5 role-appropriate tasks = 25**. Target ≥23/25 completed without verbal coaching, all 5/5 on quantity/permission safety tasks, no typed internal document/line numbers, and measure actual elapsed time, clicks, error rate and edits. Avoid an arbitrary “only three clicks” claim when user must verify stock/lot. Repeat after fixes. **Do not label UX PASS from static source review.**

**Mandatory per-page `UI STANDARD EVIDENCE` form** in Gate G2:

```text
Target page/route + current page evidence (razor/cs/css):
Archetype: shared list / Inventory batch entry / Production workflow / procurement link
Live canonical file references and chosen classes:
Grid key/actions left-fixed/column sizes/search/filter/persistence:
Header layout/label widths/mobile behavior/dirty-check/footer:
Supplier/item/warehouse-lot/BOM/WO/PO smart lookup components:
Source qty, UOM, stock status, display cost permission:
State machine and server+client permissions for each action:
Post and rollback command availability and blocked reasons:
Intentional deviations and screenshots of light/dark/tablet:
5-user UAT evidence including errors/number of decisions:
```

---

# 8. Malaysia SME tax, customs and documentation — carefully scoped

**Business evidence:** Malaysia DOSM classifies manufacturing service on materials owned by others as processing, assembly, packaging, labeling and testing; MySST recognizes subcontract manufacturing as a distinct situation. Malaysian manufacturers outsource finishing and assembly. **These sources establish the scenario's relevance, not that all 35 scenarios are common to every SME.** Use customer interviews/UAT to prioritize optional variants.

**Normal SME (default):** do not implement a new tax/e-invoice engine. Use existing supplier PO/PI tax codes, exemption evidence, currency and e-Invoice integration. A transfer of company-owned goods to a processor is not automatically the same as a sale to the processor. Legal/tax treatment of imported, subcontracted, registered vs nonregistered and resold goods varies; confirm with customer's accountant rather than assume a rate. Don't generate an ordinary sales DO/invoice to record a custody-only dispatch.

**Licensed manufacturing warehouse / special customs regimes (opt-in only):** official Customs **Lampiran G1** conditions describe subcontract approval reference, special stock book/movement evidence, separate secure storage, supplier stock statement reconciliation, unusable/waste return and monthly report for covered holders. These are **conditional regime requirements**, not automatic rules for every Malaysian company. Company-level `CustomsProfile=NONE/REGULATED`, approved site/approval expiry and attachment requirements; no hardcoded 50%/30% restrictions or declaration deadlines for generic tenants without regulatory review. Export a reconciled read-only report, let authorized compliance user attach approval and external references. Verify current legal rules with Customs/accountant before implementing enforcement. No hardcoded SST/GST historical rate.

**Business documents:** print supplier processing instruction, BOM/packing list, posted company-owned material dispatch note, goods receipt acknowledgement, unused-material return note, scrap/damage report and supplier statement. Every page needs source doc, supplier/site, item/lot, UOM, transaction business date, printed status, signed/received quantities, audit. Draft print clearly labeled. Suppress costs unless the user has appropriate permission; optionally multilingual instructions (English/Malay/Chinese) through existing localization pattern, not custom UI framework.

# 9. Source availability / downstream impact matrix — G0 must fill EXACT call sites

For **every** current effective stock reader, assert supplier-external or offsite WIP cannot be consumed internally without explicit authorized transfer/return. A page-level filter or warehouse type alone is insufficient.

| Current process to inspect | Required blocker, identity, evidence | Test before flag enabled |
|---|---|---|
| Sales DO/picking and Invoice-direct shipment, SO reservation | Source cannot be supplier custody; no duplicate Sales stock owner; cost lineage intact | Supplier WH stock excluded from pick and direct invoice, even if UI bypassed |
| Inventory MI, Stock Return, Scrap, Adjustment | Ordinary users cannot remove/consume supplier stock except controlled authorized event | Direct service call with vendor WH and `ACTIVE` stock rejects; approved Subcon scrap works |
| Inventory TR | No ordinary transfer out from mapped supplier unless allowed return/chain | No shortcut to bypass job allocation; lot return legal or blocked |
| Stock Count and month-end freeze | Offsite owned stock counted/confirmed separately, normal in-house count cannot silently adjust | Vendor count vs book reconciliation; closed period blocks mutations |
| Purchase GRN/return | Direct GRN to supplier only with title and approved WH; no double receipt | Supplier A→Subcon B owned inbound, correct fee invoice distinction |
| Production material issue | Offsite vendor RM not available for new WOs unless transfer/authorized consume | `AvailableMaterialQty` never sees uncontrolled supplier WH |
| Production Daily Output/WO handoff/FG | Offsite WIP cannot be processed by local operation until returned | No premature FG, correct WO material execution facts |
| QA logical/physical hold | Record-only QA not accepted as physical stock block | QA mandatory blocks receipt; hold/claim changes eligibility once authorized |
| Backdated replay/cost layers/period close | Source/all downstream are in active valuation graph; no invented historical stock | FIFO/WAC/Std chronology, closed periods, reverse dependencies |
| Procurement PI/CN and processing fees | Fee recognized once per accepted service/period; stage capitalization/expense sign-off | Invoice-before-accept and credit-after-sale tested; no duplicate payment charge |
| Reporting/audit | One posted source for quantity/value and immutable service links | Supplier statement agrees stock ledger at cut-off and by SKU/lot/job |

**G0 evidence requirement:** for each matrix row, list **actual code method/path and calls** plus a failing regression test as it existed before correction, and a passing test after correction when authorized. Do not mark “covered” by anecdotal observation. Map warehouse-specific exclusions across **all** Inventory source readers; inventory may have non-Sales access routes. Check stock availability in SQL, not merely the picker.

# 10. Phases, models, hard approval boundaries

**Use the G0–G11 names from v9 for continuity, but with v10 conditions**. Gates operate per **release profile** (non-lot same SKU; lot same SKU; inventory BOM single-output; Production external WIP; regulated company, etc.). A later gate passing for one scenario does not permit other unsupported scenarios. DeepSeek **Pro** is recommended for domain/posted transactions; **Flash** for UI-only under frozen data contracts; independent review before critical migration/protected code.

| Gate | Scope and exact deliverables | Owners | Approval condition |
|---|---|---|---|
| **G0 — current local forensic evidence (ONLY unconditional GO)** | checkout HEAD/status/worktree, diff, 35-case matrix, exact BOM mode/revision/permission, source eligibility/stock count/QA/WO readers, WAC/FIFO/Std, contract conversion and fee approval feasibility; draft bugs; tenant schema and actual UI patterns | Pro | **READ-ONLY**. Mark every path PROVEN / EXTENSION REQUIRED / BLOCKED / NOT TESTED. Two proposed approaches only if both truly viable. Human approves next gate. |
| **G1 — repair draft shell** | duplicate/self/edit/concurrency allocation, FK-preserving lines, precise UOM/stock master eligibility, PO references, receipt contract, messages, revisions and tenant migration; all adapters BLOCKED | Pro | No physical or cost changes; tests incl SQLite and SQL Server concurrency for allocation; diff of protected folders empty |
| **G2 — UX prototype + job/BOM snapshot (draft)** | single themed workbench; job intent, output, supplier, safe BOM read-only adapter, multi-line qty grid/lot selection, one-off kit, draft send/receive/return, first-print preview and help; disabled Post truthful | Flash(UI), Pro(service) | Current UI skill evidence; 5-user/5-task UAT measured; no fake Posted/outstanding KPI; data model additive and migration-safe |
| **G3 — non-lot SAME-SKU dispatch & return** | approved transaction-sharing Inventory seam and site isolation, source cost lineage, one-root atomic posting and reversal | Pro | **Protected owner change request separately approved;** actual SQL Server FIFO/WAC/Std and cross-consumer tests. Lot support remains blocked |
| **G4 — material kit send and leftover return** | multiple items/lots/WH, supplier custody allocation, extra delivery, return and supplier statement; **no consumption or new FG yet** | Pro | Every posted event one root; supplier stock/allocation balances; all consumers excluded; SQL Server history/valuation tests |
| **G5 — Inventory BOM/CONVERSION → finished SKU (CORE)** | actual component consumption, single-output FG, partial receipt, scrap/QA, leftover ties, protected Inventory-only valuation inside existing ledger | Pro | **Separate core valuation API approval**; method-by-method FIFO/WAC/Std trace, 45/50 example, full end-to-end `A+B→FG` incl rollback and downstream Sales; NO live FG if absent |
| **G6 — WO external WIP + optional extra materials** | approved WIP partial external custody, source-specific WO operation, offsite block, WO material issue integration, processed WIP receipt, downstream/rollback | Pro | Separate Production core approval, exact pool value conservation and operation eligibility tests incl mixed-source material |
| **G7 — Commercial, QA, reports and controls** | approved service PO/PI acceptance and CN; QA physical hold if configured; supplier statement, print docs, aging, optional Customs profile; export | Pro + Flash | Commercial/QA capability-specific approval; tax/e-invoice follows current owners; no fabricated GRN or QA gate |
| **G8 — Fee costing feasibility** | Source-grounded accounting options (expense, capitalize, accrued/true-up), FIFO/WAC/Standard, late PI after FG sold, partials, COGS, closed period; exact file impact | Pro | **READ-ONLY**, finance/controller signoff and explicit extension proposal |
| **G9 — Conditional processing fee capitalization** | approved already-proven existing value adjustment or minimal reviewed extension; invoice/credit reversal evidence | Pro | Separate explicit protected-costing approval, full SQL Server + financial tests or BLOCKED |
| **G10 — Extended scenarios** | lot return if not G3, multi-output, cut/offcuts, different ownership, direct GRN, multi-vendor chain, rework, intercompany; separate capability flags | Pro+Flash | Specific scenario proof, not automatic release |
| **G11 — release, migration, UAT** | whole-system regression, cutover/open vendor stock, support training, multi-tenant scripts, rollback runbook, permissions, UI screenshots/light/dark and all 35-case coverage status | Pro+Flash+SME testers | **GO only for explicitly enumerated scenarios with PASS/NOT RUN shown**, no global module 10/10 claim |

**Important ordering for real implementation:** G8 **financial policy feasibility** must be completed **before** G5 live capitalization-dependent FG, even if its label is numerically later. G7/QA readiness likewise must be checked for any customer that requires QA **before** first FG Post. G0 must create dependency-based sequencing for the targeted pilot; numbers are a planning organization, **not unconditional go-live sequence**. G3 can pilot same SKU but the broader Subcon business goal is **not complete** without G4+G5 with cost authority.

**G0 must produce a yes/no capability map**, not 35 working modes. Example:

| Capability | Code state at pinned HEAD | Required first proof |
|---|---|---|
| Inventory non-lot same-SKU dispatch/return | BLOCKED | atomic TR+Subcon and supplier exclusion |
| Inventory lot same-SKU | BLOCKED | return-to-existing-lot legal route and source cost |
| Inventory BOM job draft/preview | NOT IMPLEMENTED | scoped Product Definition read + snapshot |
| Inventory kit material dispatch/return | BLOCKED | multi-item one-root posting and vendor allocation |
| Inventory BOM new SKU / partial FG | BLOCKED | approved original cost → FG valuation contract |
| Production external partial WIP | BLOCKED | signed-off WIP movement/eligibility contract |
| Supplier materials mixed ownership | BLOCKED | title/source/valuation model |
| PO/PI service settlement | PARTIAL procurement patterns only | approved service acceptance without fake GRN |
| QA hold/release | record-only/frozen at reviewed code | actual physical stock gating and source match |
| Service fee capitalization | BLOCKED | exact existing valuation graph and accounting signoff |

# 11. Mandatory test suites — mark PASS/FAIL/SKIP/NOT RUN, no self-certification

**A. Foundation:** draft save/edit/deletes, receipt duplicates same request, edits excluding self, 2 concurrent receipt drafts, new status transitions, stable IDs, tenant/branch isolation, privilege changes, data recovery if user cancels, BOM variant roles, reference PO status, version and supplier site.

**B. BOM snapshot:** 1-level + multi-level (MAKE/PHANTOM), ACTIVE nondefault/multiple definitions, optional manual kit, operation-owned materials, alternate group, source `PURCHASED/EXTERNAL_SUPPLY/INTERNAL_ROUTE_WIP/SEPARATE_PRODUCT_DEFINITION`, 1/100 base Qty, 2.5% scrap planning vs actual consumption, supplier stock availability, UOM factor, color/size, lot expiry, revision changes, type disabled.

**C. Inventory and value:** on-hand 0/below/just enough, 20 lines with one short, multiple lot allocations same SKU, repeat request ID, identity from wrong supplier, same-SKU return, lot-existing return blocked until safe, FIFO per layer + active generation, WAC temporal replay and zero qty, Standard effective value/variance, posting-on-closed-date, future draft vs Post, stock count, no negative vendor stock, no stranded cost, seller-stock inaccessible to Sales & MI.

**D. Conversion/FG (primary):** 110/165 A/B sent, 45 FG accepted, 90/135 consumed, 4/5 scrap, 16/25 returned, vendor 0, 5 FG output pending; source value 880 = consumed 720 + disposed 30 + returned 130 (illustrative only), approved fee 180 accounted exactly once; partial 30 then 15 outputs, later invoice and CN; no FG without value; rejected/held FG not sellable; output cannot exceed obligation without approved overrun.

**E. WIP:** 1,000 stage units→300 offsite→200 returned→100 remains; extra RM supply separately tracked, correct WO operation stage, no next-operation/FG use before return; work order cancellation, release and rollback, pooled valuation Verified and original cost lineage; late material issue and double consumption attempts.

**F. Procurement/QA/Compliance:** supplier fee per received unit/fixed fee, invoice prior to service acceptance blocked or separately approved route, partial invoice/credit, supplier supplies all → only one PO/GRN receipt, QA required frozen → blocked, QA eligible hold/reject/scrap, approved LMW shipment doc and monthly movement report, ordinary SME without Customs fields, tax & e-invoice existing service behavior.

**G. Dependency/adversarial SQL Server:** two users sending last 5 units, two users consuming same vendor lot, return-vs-FG race, 2 jobs share same lot, alternate/WO revision changes mid-Post, network timeout after SQL commit, time-of-check/time-of-use, rollback after Sales DO/Invoice, after QA hold, PO invoice, Stock Count, closed month/backdated WAC/FIFO. Verify **one** sealed root each and exact persisted value/qty facts, not just result message.

**H. Full existing ERP regression:** Inventory GRN/MI/MR/TR/SA/Scrap/Count/returns, Sales DO & Invoice posting/rollback and FIFO/COGS, Procurement PO/PI/CN, Production IP/WI/Daily/FG/WIP/WO transfer/stock return, QA, month-end, replay for WAC/FIFO/Standard. Run available tests with explicit filter/exit and SQL Server provider; SQLite is insufficient for locks/concurrency/replay.

**I. UI/operator acceptance:** five unfamiliar non-developers, five essential tasks (same-SKU send/return, 20-line BOM kit, partial FG+actual usage, leftover return, prevented over-consumption/rollback); desktop + tablet screenshots with real DevExpress theme and dark mode; measure task completion, error explanation, no typed internal document-line IDs, keyboard tab order, input widths, too many clicks, large dataset lookup performance. **No invented UI pass results**.

**J. Deployment/operations:** read-only tenant preflight, idempotent SQL upgrade, no startup migrations, `Subcon` feature flags default OFF per mode/scenario, existing drafts and numbering preserved, version ledger, backup/restore drill, UAT dedicated tenant, dry-run supplier reconciliations, cutover of physical vendor-held stock only with verified opening valuation and approved opening posting root, support-facing runbook and exception audit.

**Evidence template per gate:**

```text
CURRENT local git HEAD, branch, worktree path, git status, diff SHA:
G0 exact source code/line method and screenshots/skills:
Scenario/job/mode:
Before snapshot: IvBalLoc per slice, StockValuationFact, CostLayer, WIP pool, supplier allocation:
Actions and SQL Server DB provider/test fixture:
After snapshot: per-slice physical, financial, WIP and downstream effects:
Posting IDs/request ID/revision/status and reversal graph:
Concurrent/dependency/future/month-end result:
Actual commands + exit codes + tests (PASS/FAIL/SKIP/NOT RUN):
UI 5x5 operator metrics/light-dark-tablet evidence:
Protected owner changes and explicit human approval reference:
Decision: PROVEN / PARTIAL / BLOCKED / NOT VERIFIED, GO/NO-GO per scenario:
```

# 12. NO-CHANGE list and explicit approval protocol

**NEVER without separate exact-file approval:** algorithms/semantics under `IvInventoryPostingService.*`, `StockLedger/Costing/*`, FIFO/CostLayer, WAC replay, Standard cost/variance, `StockPostingCoordinator`, `IvBalLoc` mutation, `ProductionStockWriter`, WIP pool valuation/production balancing, Sales DO/Invoice stock owners, Procurement GR/PI settlement, QA gates, closed periods, future date policy, stock counts, security/permissions, audit and cross-module consumption. These may be **inspected** in G0; a new adapter is not permission to bypass them. Minimal approved transaction seam in the existing V2 ledger is permissible **only after** diff-level impact review and real SQL Server regression.

**MUST NOT** create a new Subcon costing strategy, cost layer, independent warehouse balance, independent WIP cost balance, duplicate Product Definition/BOM master, new tax/e-Invoice system, parallel Purchase or Sales transaction. Reuse existing `StockPostingCoordinator`, valuation strategies, `ProductionPoolValuationService`, `PoOrderService`/`PoInvoiceService` and established UI skill components. No cross-branch data leak or permission shortcut. Never silently repurpose `QCHOLD` or stock status to reserve external stock.

**When a required capability is missing:** write a **Stop/Approval Request** with (1) exact current source/method and failing scenario, (2) two alternatives only if both truly feasible, (3) minimal required protected interfaces/files, (4) quantity, FIFO/WAC/Standard and WIP implications, (5) multi-slice concurrency and reversal design, (6) tests that must pass, (7) what stays BLOCKED if rejected. Do not silently implement protected changes. Core amendments require user signoff and a new gate.

# 13. G0 agent handoff prompt — READ ONLY, do not assume local repo

> Inspect the **CURRENT LOCAL** checkout of `mokth/net10projectTemplate` on the actual `productionv2` branch, including `git status`, worktrees, unstaged/staged changes and commit SHA. Use this v10 as authoritative. Verify all paths against actual code; audit the existing `Subcon`, Inventory, Production, BOM, Procurement, Sales, QA and StockLedger full flow. Read the exact UI and costing `.agents/skills` list in §7. Perform **G0 READ-ONLY ONLY** and deliver: (a) source/consumer/permission/effective-date matrix with actual code lines and constraints; (b) **Inventory-only BOM A+B→new FG** and unused return feasibility with FIFO/WAC/Std and exact ledger root; (c) same-SKU lot return and vendor isolation feasibility; (d) Production partial offsite WIP + optional RM with total value lineage; (e) compliant existing PO service acceptance/PI fee policy with conditional Malaysian SST/e-Invoice; (f) prioritized 35-case capability matrix; (g) current UI baseline and 5-user task prototype; (h) minimal candidate G1/G2 file edits, risks and test plan. Report **GO/NO-GO per capability** and exact protected code needs. Do not create DB migrations, change adapter registration, implement Post, modify costing/quantity/StockLedger/QA, or claim UAT/tests ran. STOP for independent review.

**Further phase tool assignment:** DeepSeek Pro: G0/G1/back-end/G3–G9; DeepSeek Flash: G2 and view-only G7 UI with approved contract; GPT-6 High independent verification for protected changes. **Do not proceed because a plan claims 10/10.**

# 14. External Malaysian scenario evidence and verification notes

All below are **public contextual evidence**, not evidence that the ERP implements any feature. Regulatory descriptions are context only and must be revalidated for relevant customer registration, manufacturing scheme, date and legal advice before enforcement:

1. **DOSM, survey explanatory notes**, manufacturing services on materials owned by others: processing, assembly, labelling, packaging, testing — https://www.dosm.gov.my/v1/uploads/files/2_Censuses%20%26%20Surveys/External%20Sector/ITS/ITS%20Q4%202016/Explanatory_Notes_for_ITS_Survey.pdf (historical official taxonomy, not current tax instruction).
2. **Royal Malaysian Customs MySST FAQ**: manufacture includes conversion of materials/assembly — https://mysst.customs.gov.my/faq-sales-tax/
3. **Royal Malaysian Customs MySST registration**: recognizes subcontract manufacturing under SST registration rules — https://mysst.customs.gov.my/registering-business/
4. **Royal Malaysian Customs Lampiran G1**, regulated subcontract/farm-out conditions (special stock book, site, signed statement and approved movement) — https://www.customs.gov.my/images/04-fasilitasi/LAMPIRAN/Lampiran%20G1.pdf . **Do not apply conditional customs restrictions to all SMEs**.
5. **Federation of Malaysian Manufacturing** membership description includes businesses outsourcing production and manufacturing service providers — https://www3.fmm.org.my/Join

**Prepared from remote source inspection and public contextual research on 2026-11-13. No local source changes, production database writes, tests or GUI/UAT were performed in this review.**
