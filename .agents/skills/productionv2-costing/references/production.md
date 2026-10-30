# Production RM/WIP/FG cost and quantity integrity

Trace WO release and schedule prerequisites only where they affect actual postings. Then trace Inventory material issue (IP) → material-in pool → Daily Output/WIP → inter-operation transfer → Finished Goods receipt, plus Production Return, Stock Adjustment, WO Transfer and rollback of every stage.

- RM issue must have a uniquely attributable inventory cost and Production contribution. A Production stock return can return to Inventory only when permitted by actual original Inventory issue lineage; never fabricate original cost or return an unissued adjustment as RM.
- Reconcile WIP per WO/process/material/pool and any relevant lot/operation: opening + inputs − consumption ± transfers/adjustments = ending, for **both quantity and value**. Account for scrap, rework, partial output and shared-pool allocation as supported.
- FG valuation must reconcile allocated RM, labor, machine, electricity/overhead and other approved contributors under actual current policies. Avoid duplicate absorption, orphan contributions, and treating a draft/scheduled quantity as posted production.
- Check original and active Production dependency/allocation edges and foreign-root restatements. A corrected RM issue must propagate to every already-consumed WIP/output/FG/downstream sale or be blocked atomically.
- A successful isolated IP recost test does NOT certify consumed WIP, multiple IPs, mixed Sales/Procurement, FIFO/Standard Production or full FG replay. Respect the current conservative blockers.
- Rollback after downstream usage must be dependency-aware, preserve original history, reverse both sides of WO transfers and restore cost pools without negative or residual balances.

Tests: IP→output→FG→sale; partial daily output; split operations; shared WIP; return/adjustment; WO transfer; multiple IP; later receipt repricing; FIFO/Standard; rollback each stage and order; closed period; concurrent posting; injected failure. Compare stock, WIP, FG cost and GP before/after.
