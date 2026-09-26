# Procurement Inquiry Gaps

## Phase 1 deliverables (this work)

- New: `IPoPurchaseInquiryService`, pages under `Purchase/Inquiry/`, export endpoints, `PO_INQUIRY` menus
- New: `SearchSuppliersAsync` on supplier lookup
- No new tables/views required for Phase 1 (EF queries)

## Blocked / deferred (not Phase 1)

| Gap | Blocks |
|-----|--------|
| AP ledger / payments | Supplier outstanding, aging, statement, payment performance, unpaid invoice |
| `PoInvoice.DueDate` | Overdue / aging-by-due |
| Change-history / `PoDocEvent` | Field-level audit, approval lead time |
| Formal approval workflow | Approval inquiry beyond stored timestamps |
| Promise history / OTIF | Delivery performance rigor |

## Model notes

- No dedicated Purchase Receipt entity — use Inventory GR
- Two CN concepts: `POInvoice` Type=CN vs `PoCdn` — keep separate
- Self-billed has no PO/GR lineage
