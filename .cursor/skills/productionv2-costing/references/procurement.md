# Procurement valuation integrity

Trace PR→PO→GR→PI→CN/DN (including standalone and partial flows as actually supported). PR/PO commitment is not automatically a stock receipt or cost posting. Verify GR receipt owner, PO received quantities, receipt valuation, PI settlement, supplier return and purchase CN cost adjustment separately.

- Keep PO/GR quantities, inventory physical receipts and received/invoiced status consistent across partial receipts, split invoices and rollbacks.
- Verify original receipt price/currency/exchange rate, transaction/base amounts, tax/rounding, landed costs and cost authority; distinguish commercial invoice amount from inventory valuation.
- PI/CN retroactive price changes: split PPV or settlement between stock remaining and already consumed stock, with correct downstream COGS/WIP impact. FIFO layer authority and WAC/Standard treatment differ; never blanket-adjust `IvBalLoc` value.
- GR rollback with later PI, CN, issue, Sales or Production consumption must be fully supported by the active method and bounded route, or fail before mutations. No phantom PO quantities, orphan settlements or duplicate corrections.
- Validate vendor/document linkage, duplicate posting, permission, business date, closed period, and root lock again before mutation.

Tests: multi-GR PO, partial PI, CN/DN, FX, tax, price variance, fully/partially consumed GR, rollback both orders, repeated correction, concurrency, month close, failure injection.
