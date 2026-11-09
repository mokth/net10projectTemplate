# Repository evidence and confidence boundary

Repository: `mokth/net10projectTemplate`, branch `productionv2`.
Checked during v1 skill authoring (2026-10-30) via GitHub connector:
- `ErpWeb.Core/StockLedger/StockPostingCoordinator.cs` blob `d55cf717421e42d0908f5761a35d4f5a40dbaf57`: normal and replay completion methods, branch lock and transaction coordinator.
- `ErpWeb.Core/Inventory/IvStockCountService.cs` blob `5b21fe833d81a6db2be7f196d0c56ab9d098145d`: conservative stock-count route and `OpenPeriodBackdatedReplayEnabled` configuration.
- `plans/productionv2_backdated_replay_impact_manifest.md` blob `0981b4645de5faa0e830a396c016c6a73a7355cb`: lengthy implementation progress log; last entry describes bounded WAC MR/GR→unconsumed Production IP recost and remaining limitations.
- `ErpWeb.Core/Inventory/IvMiscReceiptService.cs`, `ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs` were sampled for public entrypoint shape.

This is **not** a complete repository audit, executable test run, production database validation or current commit pin. Blob IDs above are evidence for specific files, not a branch-wide SHA. Do not treat the earlier conversational 80%-complete estimate as measured progress. Every agent must verify current HEAD, local changes, actual implementation, entity schema, permissions, and tests anew.

Useful source: https://github.com/mokth/net10projectTemplate/tree/productionv2
Manifest: https://github.com/mokth/net10projectTemplate/blob/productionv2/plans/productionv2_backdated_replay_impact_manifest.md
