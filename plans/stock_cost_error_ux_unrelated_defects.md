# Unrelated pre-existing issues — report only, not fixed

The plan requires any pre-existing defect found while working to be listed separately rather than fixed
inside an error-presentation change. Everything below was observed on this tree; **none of it was
changed by this work stream**.

---

## 1. Pre-existing test failures (27, identical at baseline `371725c`)

| Group | Count | Representative test |
|---|---|---|
| BOM structure tree | 7 | `PrBomStructureTreeTests.Structure_phantom_recurses` |
| Product definition validation | 2 | `PrProductDefServiceTests.Circular_bom_rejected_on_save` |
| Product snapshot loader | 2 | `ProductDefinitionSnapshotLoaderTests.No_active_revision_reports_not_found` |
| BOM explosion | 1 | `BomExplosionServiceTests.Make_without_bom_is_hard_error` |
| Nested phantom snapshot | 1 | `ProductionWorkOrderServiceTests.Nested_phantom_material_keeps_its_own_bom_revision_and_line_snapshots` |
| Customer master / GL rules | 9 | `SaCustServiceTests.PaymentFieldChange_RequiresGlCode` |
| Self-billed e-Invoice UBL roots | 2 | `SaEInvoiceSelfBillAndTinTests.Self_billed_types_use_the_note_and_invoice_ubl_roots` |
| Purchase inquiry workbench / KPI | 3 | `PoPurchaseInquiryWorkbenchTests.PoSummary_And_Presets_Overdue_Partial_Unbilled_Value` |

**Evidence they predate this work:** a throw-away worktree at `371725c` produced 27 failures with an
identical name list (`Compare-Object` → no differences).

**Why not fixed:** they are BOM/master-data/UML-transport defects. Fixing them would change business
or document logic, which this change stream explicitly forbids.

**Suggested owners:** Planning master-data (BOM/snapshot), Sales master-data (customer GL rules),
Purchase inquiry (KPI window), Sales e-Invoice (UBL roots).

---

## 2. Data-integrity: stock balances without a valid stock date

`IvStockDateRules.NullStockDateMessage()` exists because `IvBalLoc` rows can be found with no stock
date; such a balance is unusable until repaired.

- **Symptom for the operator:** the action is refused with "This stock balance has no valid stock date…"
  (message only; no repair path).
- **Root cause:** outside this work stream — a balance created by a path that does not stamp the date.
- **Not fixed:** would require a data audit + a guard at the creating path (business change).
- **This stream:** only removed the internal `balLocId` from the operator text.

---

## 3. Swallowed diagnostics in the affected-document reader

`IvFailureEvidenceReader.TryReadAsync` swallows every non-cancellation exception at `Debug` level so a
secondary lookup failure can never mask the original posting refusal.

- **Trade-off:** deliberate (an error explanation must not create a second error), but a persistent
  enrichment failure is nearly invisible in production logs.
- **Suggested follow-up:** count/emit a metric or log at `Warning` with a sampled rate.
- **Not changed:** logging level in an enrichment path is a diagnostic policy decision.

---

## 4. Raw `ex.Message` in out-of-scope screens (presentation debt)

These can show library text, though not a stack trace or SQL:

| Screen area | Paths |
|---|---|
| Sales e-Invoice transport | `SaCdnList`, `SaInvoiceList` |
| Purchase self-billed e-Invoice | `PoSb*List` |
| Inventory entry popups | `IvStockTransfer`, `IvStockReturn`, `IvMiscReceipt` (lot/lookup actions) |
| Production entry save/delete/lookup | `PrProduction*Entry`, `PrWorkOrderTransferEntry`, `PrFinishedGoodReceiptEntry` (cost trace) |
| Work-order scheduling | `PrMachineSchedulePlanner` (7 sites) |

They were registered as out of scope (Register E-14 and the "popup is not a posting path" rule) because
none is a stock/cost **posting** refusal. A future stream could apply the same banner to them.

---

## 5. Unowned changes sitting in the same working tree

Not defects, but they must not be attributed to this work stream (see implementation report §2.1):

| Path | Note |
|---|---|
| `ErpWeb.Core/Production/ProductionPostingInvariant.cs` | Compares reversal cost against effective replay cost — **behaviour change** |
| `ErpWeb.Core/Production/ProductionOutputService.Rollback.cs` | **Behaviour change (mixed with ours):** reversal cost writes now use the effective replay cost, plus reconciliation guards. Only its `ProductionOperatorErrorLog` catches belong to this stream |
| `ErpWeb.Core/Production/ProductionIssueEffectiveCostReader.cs` | Stricter identity/valuation guards on the same feature |
| `IvInventoryPostingService.{Wac,StandardMr,WacProduction,StandardTransfer,StandardProduction}Replay.cs` | Backdated replay plumbing |
| `ProductionIssueEffectiveCostReader.cs`, `ProductionReplayEffectiveCostReader.cs`, `ProductionOutputIssueCostCorrectionWriter.cs` | Replay cost correction feature |
| `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor.cs` | Column min-width edit |
| `plans/productionv2_backdated_replay_impact_manifest.md`, `plans/productionv2_replay_architecture_assessment.md` | Same effort's documentation |

---

## 6. Compiler warnings worth a cleanup pass

- `CS0108` — `IvLotGenealogyInquiry.ErrorMessage` hides `PageBase.ErrorMessage`. Harmless today; if the
  base member's behaviour changes, this page will silently stop using it.
- 115 warnings in total on a clean Release build; all nullable/analyzer/`CS0108` categories, none new
  from this stream.

---

## 7. Known limitation accepted by the product owner

`VALUATION_REQUIRED` wording keeps the "cost could not be verified" phrasing (customer decision recorded
at the Phase 1 gate) rather than promising that stock exists. If a future UAT shows operators reading it
as a stock shortage, revisit the wording only — not the classification.
