# Delivery Request (DR) — Usage, Inquiry & KPI Summary

## Purpose of DR

Delivery Request (DR) is the **fulfilment control document** between Sales demand and Stock / Procurement / Production execution.

Its main purpose is to answer:

> **What must be delivered, how much is required, when is it required, and what action is needed to fulfil it?**

Typical flow:

**Sales Order → Delivery Request → Stock / Procurement / Production → Work Order → Delivery Order → Invoice**

---

## Important Usage of DR

| Usage | Purpose |
|---|---|
| Sales demand confirmation | Record customer, item, quantity and required delivery date |
| Stock fulfilment | Check available stock and reserve what can be supplied |
| Production demand | Determine the remaining quantity that must be manufactured |
| Procurement signal | Identify raw-material shortages caused by the demand |
| Delivery planning | Track whether goods can be ready before the required date |
| SO → DR → WO traceability | Know which customer / SO caused each production requirement |
| Partial fulfilment | Split one SO requirement into different DR quantities or dates |
| Priority control | Identify urgent customer requirements |
| Project / customer reference | Keep fulfilment connected to customer or project commitments |
| Cross-department coordination | Give Sales, Store, Purchasing and Production one common demand reference |

### Core Fulfilment Logic

**DR Demand → Available Stock → Shortage → Purchase / Production → Ready Qty → Delivery**

---

## Important DR Inquiry

A useful DR Inquiry should allow filtering or searching by:

- DR No.
- Sales Order No.
- Customer
- Product / Item
- Warehouse
- Project
- Required Delivery Date
- Priority
- DR Status
- Work Order No.

### Recommended Inquiry Views

- Open DR
- Due Today
- Due This Week
- Overdue DR
- At-Risk DR
- Production Required
- Material Shortage
- Ready for Delivery
- Partially Fulfilled
- Completed DR
- DR by Customer
- DR by Product

### Suggested Inquiry Columns

| DR | Customer | Product | Required Qty | Stock Qty | WO Qty | Completed Qty | Ready Qty | Required Date | Status |
|---|---|---|---:|---:|---:|---:|---:|---|---|

---

## Important DR KPIs

| KPI | Meaning |
|---|---|
| Open DR Qty | Total customer demand still not fulfilled |
| DR Due Today / This Week | Immediate fulfilment workload |
| Overdue DR | Required date passed but demand is not ready/completed |
| On-Time Readiness % | Percentage of DRs ready on or before required date |
| At-Risk DR | Expected completion is later than required delivery date |
| Stock Fulfilment % | Percentage of DR demand fulfilled directly from stock |
| Production Requirement Qty | Quantity that still requires manufacturing |
| Production Completion % | Completed production quantity ÷ production required quantity |
| Material Shortage DR Count | Number of DRs blocked by raw-material shortage |
| Ready-for-Delivery Qty | Finished quantity available for Delivery Order |
| Average Fulfilment Lead Time | Time from DR creation to ready-for-delivery |
| Partial Fulfilment Count | Number of DRs not yet fully supplied |

---

## Recommended Management Dashboard KPIs

For an SME ERP, keep the dashboard simple and useful:

1. **Open DR**
2. **Due This Week**
3. **Overdue DR**
4. **At-Risk DR**
5. **Ready for Delivery**
6. **On-Time Readiness %**

These KPIs should answer one management question:

> **Can we fulfil what Sales promised to the customer, and if not, what is blocking it?**

---

## Recommended ERP Role of DR

DR should **not** be treated as another data-entry form only.

It should be the common fulfilment reference shared by:

**Sales → Stock → Procurement → Production → Delivery**

Important DR fields such as **Product, Quantity, Warehouse, Project, Priority and Required Date** should have meaningful downstream usage. If a field does not affect planning, fulfilment, filtering, prioritisation, reporting or workflow, it should not exist merely for display.
