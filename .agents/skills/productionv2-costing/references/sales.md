# Sales stock and COGS integrity

Trace SO(optional)→DO(optional)→Invoice, direct Invoice without DO, Sales Return and CN/DN, including partial and multi-line flows. Confirm actual repo-supported combinations before assuming any route.

- Only the document that owns the physical shipment posts stock out. A linked Invoice cannot issue again; a direct Invoice can own stock only under existing rules.
- COGS must equal active outbound valuation, including WAC repricing and FIFO splits; gross-profit inquiry must not combine a corrected sale with stale return allocations.
- Stock-return CN must restore correct physical quantity and cost tied to the outbound source/active lineage. Financial-only CN/DN must not touch inventory; customer selling price and stock cost are distinct.
- Preserve DO/Invoice application, partial billing, multiple lines, multiple returns, posted/reversed notes, return layer consumption and foreign-currency evidence. If any shape lacks certified replay support, reject the historical root atomically.
- Reconcile gross sales, returns, COGS, gross profit and stock movements after receipt backdating, Sales rollback and repeated repost.

Tests: DO+Invoice, direct Invoice, no SO, partial DO billing, multi-line, single/multiple CN, FIFO split return, consumed return layer, changed return value, reversal ordering, future shipment, closed period, concurrent shipment, negative stock.
