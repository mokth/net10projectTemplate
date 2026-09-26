# Procurement Inquiry Assessment

Repository is the source of truth. Chat notes are guidance only.

## 1. Executive Summary

Purchase today has Masters + Transactions only — no Inquiry module. Phase 1 adds a Sales-Inquiry-mirrored suite of seven read-only screens. AP outstanding/aging/statement remain blocked (no AP ledger). Goods receipt lives in Inventory (`IvTrxBatch` GR), not a Purchase receipt entity.

## 2. Existing Architecture

- Entities: `ErpWeb.Model/Entities/Purchase/` (26)
- Services: `ErpWeb.Core/Purchase/`
- UI: `ErpWeb.UI/Purchase/Masters|Transactions` plus **Phase 1 Inquiry** under `ErpWeb.UI/Purchase/Inquiry/`
- Reuse template: `SaInquiryPageBase` → `PoInquiryPageBase` + `IPoPurchaseInquiryService` + CSV export

Document flow: PR → PO → GR (IvTrxBatch) → INV → PoCdn / qty-CN; Self-billed (`PoSb*`) is independent.

## 3. Verified Rules (Step 0.5)

### A. PR → PO consumption

| Item | Verified result |
|------|-----------------|
| Source methods | `PoOrderService.LivePoConsumedForPrAsync`, `BuildPrRemainingLinesAsync` |
| Formula | `Remaining = PurchaseQty − Sum(PoOrderDetail.PoPurQty)` where `PrNo`/`PrLineNo` match |
| Cancelled PO | `Order.Status != CANCELLED` required; cancelled headers excluded |
| One PR → many PO | **Yes** — sum of all matching live PO lines |
| One PO line → many PR | **No** — single `PrNo`/`PrLineNo` on `PoOrderDetail` |
| Revisions | Consumption sums **all** non-cancelled `PoRelNo` rows (helper does not filter to max) |
| Business qty | `PurchaseQty` (purchase UOM); no unit conversion in inquiry |

### B. PO revision authority

| Item | Verified result |
|------|-----------------|
| Authority | `PoOrderRepository.SearchLatestPagedAsync` (used by `PoOrderService.SearchAsync` → `PoOrderList`) |
| Rule | Latest revision only = `MAX(PoRelNo)` per `PoNo` via max-rel join |
| Rollups | Per-revision on detail rows (`BalanceQty`, `RecvQty`, `InvoicedQty`) |
| Old revisions | Queryable via `ListRevisionsAsync`; not shown on operational list |
| Outstanding display | **Latest revision only**, lines with persisted `BalanceQty > 0`, exclude `Status == CANCELLED` |

### C. Persisted vs derived

| Value | Kind |
|-------|------|
| `BalanceQty`, `RecvQty`, `InvoicedQty`, `TotAmnt` | Persisted — display as stored |
| PR `ConsumedQty` / `RemainingQty` | Derived — Step 0.5A only |
| Invoiceable | Derived via `PoOrderCalc.ComputeInvoiceable` if shown |

### D. Self-billed latest submission

Reuse `SaSalesInquiryService.GetEInvoiceStatusAsync`: highest `EInvDocSubmission.Id` for `(CompanyId, DocumentType, DocumentNo)` + `EInvoiceStatuses.Normalize`. Doc types SBI/SBC/SBD.

### E. Supplier transaction amounts / signs

| Item | Locked rule |
|------|-------------|
| PR amount | Same as PR list: sum line nets + taxes (`PoPrCalc.SumTotals` / list Total) |
| PO amount | Same as PO list: `PoOrderCalc.SumTotals` of detail `(NetAmount, TaxAmount)` → Total |
| Invoice | `PoInvoice.TotAmnt` |
| PoCdn | Stored positive; Tab B net = INV + DN − CN (Sales convention) |
| Qty-CN in Tab B | **Omit** (`POInvoice.Type=CN` excluded from history) |
| Self-billed | Tab A **include**; Tab B **exclude** |

### F. Document relationship evidence

| Relation | Fields | Evidence |
|----------|--------|----------|
| PR→PO | `PoOrderDetail.PrNo`, `PrLineNo` | `PoOrderService` PR pick / consumption |
| PO→GR | `IvTrxBatchDetail.PoNo/PoRelNo/PoLineNo`, `IvTrxBatch.TrxType` in (`GR`,`NG`) | `IvGoodsReceiptService` |
| PO→INV | `PoInvoiceDetail.PoNo/PoRelNo/PoLineNo` | `PoInvoiceService` posting |
| INV→CDN | `PoCdn.InvNo` | `PoCdn` entity / service |
| INV→QtyCN | `PoInvoice.Type=CN` + `InvNo` → INV | `PoInvoice` model comments |

## 4. Phase 1 Screens (shipped)

| Screen | Route | MenuCode |
|--------|-------|----------|
| PO Outstanding | `/purchase/inquiry/po-outstanding` | `PO_ORDER_OUTSTANDING` |
| PR Status / Outstanding | `/purchase/inquiry/pr-status` | `PO_PR_STATUS` |
| Supplier Transactions | `/purchase/inquiry/supplier` | `PO_SUPP_TRX` |
| Purchase Invoices | `/purchase/inquiry/invoices` | `PO_INV_INQUIRY` |
| Credit / Debit Notes | `/purchase/inquiry/credit-debit-notes` | `PO_CDN_INQUIRY` |
| Document Relationships | `/purchase/inquiry/doc-relationships` | `PO_DOC_REL` |
| Self-billed e-Invoice | `/purchase/inquiry/einvoice` | `PO_SB_EINV_INQUIRY` |

Seed ACCESS via `scripts/init-po-inquiry-menu.sql` (plus `menus.xml` triad).

## 5. Later phases (not this delivery)

AP payables, GR suite, analysis/dashboards, change-history, OTIF KPIs.

## 6. Risks

Multi-revision qty if wrong join; CDN vs qty-CN confusion; performance on union queries — keep server-side paging.
