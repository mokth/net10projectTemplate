# Inventory costing and stock integrity

Inspect actual `ErpWeb.Core/Inventory`, `ErpWeb.Core/StockLedger`, `ErpWeb.Model` entities/mappings and `ErpWeb.Tests` for current source names. Trace MR, MI, GR, Scrap, Vendor Return, Transfer, Adjustment, Stock Count: create/edit/post/reverse/repost, including multi-line and partial failure.

## Mandatory checks
- Scope every stock slice by all real identity dimensions, including tenant, branch, item, warehouse, location, lot and stock status; convert UOM with authoritative precision and unit basis.
- Reconcile `IvBalLoc` and legacy batch/detail/history with active V2 physical and valuation evidence; do not assume legacy history alone represents active corrections.
- WAC: simulate event-order moving average, exact signed quantities/values, value-only events, zero quantity/value and decimal tolerances. FIFO: verify `CostLayer` receipt/consumption lineage, exact split and remaining balances, reversals, transfer legs, and returned layers. Standard: effective-dated cost and variance posting.
- Stock Count: distinguish observed absolute count from a delta; historical as-of count versus live count; canonical end-of-day anchor; zero-variance anchors; original count generation, corrected generations, frozen basis and approval evidence. Never substitute current live balance for historical authority.
- Transfers: outgoing/incoming same quantity and supported valuation; rollback restores both slices; negative-stock checks at effective event time, not only today.
- Backdated changes must reprice every affected later active event or fail atomically; do not bypass chronology restrictions until all affected financial and downstream writers are certified.

## Acceptance examples
Receipt→issue→rollback; FIFO multi-layer issue and return; transfer across warehouses; same-day ordering; cost-only adjustment; zero-stock WAC; count with later movements; receipt backdated ahead of issue; repeated post/rollback; concurrent posts; closed period; future-dated draft/post. Compare all active facts, layer balances, item cost state and as-of report before/after, not just API success.
