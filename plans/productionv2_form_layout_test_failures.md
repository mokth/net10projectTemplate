# Phase 1 full-suite failures

Baseline: `61a4a66099061ef07157af15f8020cb257077420` on `productionv2`.

**Full working-tree suite:** 3,276 passed; 110 failed; 0 skipped; 3,386 total (5m 37s).

**Focused UI contracts:** 22 passed, 0 failed. **Clean-baseline sample:** all four selected failures reproduced. Other failures have not been individually rerun on the clean baseline. No failing service code was changed by this task.

After implementing the five cross-archetype canaries, the form-layout contract set passed (5/5) and the popup-footer contract passed (1/1). Those focused runs used loopback access because the restricted VSTest host could not connect to its local console socket. The full suite was not rerun after the visual-only markup changes; the 110 baseline failures remain the broad-rollout gate.

| Test class | Failures |
| --- | ---: |
| `Inventory.Master.IvStockMasterServiceTests` | 2 |
| `Inventory.Transaction.IvInventoryPostingServiceTests` | 1 |
| `Planning.Master.BomExplosionServiceTests` | 1 |
| `Planning.Master.PrBomStructureTreeTests` | 7 |
| `Planning.Master.PrProductDefServiceTests` | 2 |
| `Planning.Master.ProductDefinitionSnapshotLoaderTests` | 2 |
| `Planning.Transaction.ProductionWorkOrderServiceTests` | 11 |
| `Planning.Transaction.WorkOrderQuantityCalculatorTests` | 9 |
| `Planning.Transaction.WorkOrderReadinessValidatorTests` | 3 |
| `Procurement.Inquiry.PoProcurementKpiServiceTests` | 1 |
| `Procurement.Inquiry.PoPurchaseInquiryWorkbenchTests` | 2 |
| `Procurement.Master.PoSupplierServiceTests` | 4 |
| `Production.Transaction.ProductionAbsorbedCostCalculatorTests` | 1 |
| `Production.Transaction.ProductionCostGateTests` | 5 |
| `Production.Transaction.ProductionMaterialAllocationTests` | 5 |
| `Production.Transaction.ProductionMaterialIssueSequenceOverlapTests` | 3 |
| `Production.Transaction.ProductionPooledCostTraceMathTests` | 1 |
| `Production.Transaction.ProductionProcessHandoffServiceTests` | 1 |
| `Sales.Master.SaCustServiceTests` | 9 |
| `Sales.Transaction.SaDoServiceTests` | 2 |
| `Sales.Transaction.SaDocApplicationTests` | 22 |
| `Sales.Transaction.SaEInvoiceSelfBillAndTinTests` | 2 |
| `Sales.Transaction.SaSoServiceTests` | 14 |

## Failing test names

- `ErpWeb.Tests.Production.Transaction.ProductionAbsorbedCostCalculatorTests.Calculates_only_frozen_contributors_for_good_quantity`
- `ErpWeb.Tests.Procurement.Master.PoSupplierServiceTests.GlCode_LegacyNull_PhoneChange_Succeeds`
- `ErpWeb.Tests.Procurement.Master.PoSupplierServiceTests.GlCode_Existing_FinancialChange_Succeeds`
- `ErpWeb.Tests.Sales.Master.SaCustServiceTests.AppInvoice_On_CopiesGeneralToInvoice`
- `ErpWeb.Tests.Sales.Master.SaCustServiceTests.Salesman_LegacyValue_ToleratedOnUnrelatedEdit`
- `ErpWeb.Tests.Procurement.Master.PoSupplierServiceTests.GlCode_LegacyNull_PaymentChange_Rejected`
- `ErpWeb.Tests.Sales.Master.SaCustServiceTests.Update_PreservesLeftoverBranchCode`
- `ErpWeb.Tests.Procurement.Master.PoSupplierServiceTests.AddressReplace_AssignsLine_AndDiscardsBlank`
- `ErpWeb.Tests.Production.Transaction.ProductionMaterialIssueSequenceOverlapTests.Later_stage_active_issue_blocks_predecessor_stage_rollback`
- `ErpWeb.Tests.Production.Transaction.ProductionMaterialIssueSequenceOverlapTests.Active_next_process_issue_blocks_predecessor_rollback_to_zero_and_clears_after_issue_rollback`
- `ErpWeb.Tests.Planning.Transaction.WorkOrderReadinessValidatorTests.Machine_options_on_a_fixed_duration_process_are_a_warning_not_an_error`
- `ErpWeb.Tests.Planning.Transaction.WorkOrderReadinessValidatorTests.Terminal_output_must_match_the_work_order_item`
- `ErpWeb.Tests.Planning.Transaction.WorkOrderReadinessValidatorTests.A_complete_snapshot_is_ready`
- `ErpWeb.Tests.Planning.Master.PrBomStructureTreeTests.Structure_circular_marks_node_and_continues`
- `ErpWeb.Tests.Production.Transaction.ProductionMaterialIssueSequenceOverlapTests.Next_process_material_issue_is_blocked_until_predecessor_posts_good`
- `ErpWeb.Tests.Planning.Master.PrBomStructureTreeTests.Structure_missing_make_and_phantom_bom_status`
- `ErpWeb.Tests.Procurement.Inquiry.PoPurchaseInquiryWorkbenchTests.DueSoon_SummaryAndRowsShareInclusiveSevenDayWindowAndLatestRevision`
- `ErpWeb.Tests.Planning.Master.PrBomStructureTreeTests.Structure_phantom_recurses`
- `ErpWeb.Tests.Procurement.Inquiry.PoPurchaseInquiryWorkbenchTests.PoSummary_And_Presets_Overdue_Partial_Unbilled_Value`
- `ErpWeb.Tests.Planning.Master.PrBomStructureTreeTests.Structure_same_component_under_different_parents_has_distinct_keys`
- `ErpWeb.Tests.Planning.Master.PrBomStructureTreeTests.Structure_multi_level_make_make_buy`
- `ErpWeb.Tests.Planning.Transaction.ProductionWorkOrderServiceTests.Save_normalizes_quantity_and_rejects_unimplemented_source_types`
- `ErpWeb.Tests.Planning.Master.PrBomStructureTreeTests.Structure_max_depth_marks_node`
- `ErpWeb.Tests.Production.Transaction.ProductionPooledCostTraceMathTests.Largest_atom_receives_the_deterministic_money_residual`
- `ErpWeb.Tests.Planning.Master.PrBomStructureTreeTests.Structure_draft_root_and_draft_child_visible`
- `ErpWeb.Tests.Sales.Master.SaCustServiceTests.PhoneOnly_EmptyGlCode_OnEdit_Succeeds`
- `ErpWeb.Tests.Planning.Transaction.ProductionWorkOrderServiceTests.Work_order_snapshot_blocks_product_definition_delete`
- `ErpWeb.Tests.Sales.Master.SaCustServiceTests.PaymentFieldChange_RequiresGlCode`
- `ErpWeb.Tests.Sales.Master.SaCustServiceTests.AppInvoice_Off_GeneralChange_LeavesInvoiceUnchanged`
- `ErpWeb.Tests.Sales.Master.SaCustServiceTests.Save_ReplacesChildren_TenantScoped`
- `ErpWeb.Tests.Sales.Master.SaCustServiceTests.SubGroup_LegacyValue_ToleratedOnUnrelatedEdit`
- `ErpWeb.Tests.Sales.Master.SaCustServiceTests.ConcurrencyFail_NoChildOrHeaderPersist`
- `ErpWeb.Tests.Inventory.Transaction.IvInventoryPostingServiceTests.Lot_controlled_creates_and_reuses_lot`
- `ErpWeb.Tests.Planning.Master.ProductDefinitionSnapshotLoaderTests.Boundary_is_half_open_inclusive_from_exclusive_to`
- `ErpWeb.Tests.Planning.Master.PrProductDefServiceTests.Circular_bom_rejected_on_save`
- `ErpWeb.Tests.Planning.Master.PrProductDefServiceTests.Overlapping_active_versions_rejected`
- `ErpWeb.Tests.Inventory.Master.IvStockMasterServiceTests.ExistingRemove_ClearsPathAndCleansOld`
- `ErpWeb.Tests.Planning.Master.ProductDefinitionSnapshotLoaderTests.No_active_revision_reports_not_found`
- `ErpWeb.Tests.Inventory.Master.IvStockMasterServiceTests.ExistingReplacement_UpdatesPathAndCleansOldAfterSave`
- `ErpWeb.Tests.Sales.Transaction.SaDoServiceTests.CLOSED_DO_cannot_be_deleted`
- `ErpWeb.Tests.Planning.Transaction.ProductionWorkOrderServiceTests.Release_detects_snapshot_tampering_even_when_header_token_is_current`
- `ErpWeb.Tests.Planning.Transaction.ProductionWorkOrderServiceTests.Stale_row_version_is_rejected`
- `ErpWeb.Tests.Sales.Transaction.SaDoServiceTests.Delete_posted_DO_fails`
- `ErpWeb.Tests.Planning.Transaction.ProductionWorkOrderServiceTests.Save_reload_and_master_change_preserve_snapshot`
- `ErpWeb.Tests.Production.Transaction.ProductionMaterialAllocationTests.Rollback_is_blocked_when_a_later_movement_depends_on_the_issue`
- `ErpWeb.Tests.Planning.Master.BomExplosionServiceTests.Make_without_bom_is_hard_error`
- `ErpWeb.Tests.Procurement.Inquiry.PoProcurementKpiServiceTests.PriceMovement_IsUnavailable_WhenLatestSamplesUseDifferentPurchaseUom`
- `ErpWeb.Tests.Production.Transaction.ProductionMaterialAllocationTests.Issue_can_reverse_after_its_consume_has_been_reversed`
- `ErpWeb.Tests.Sales.Transaction.SaSoServiceTests.DO_rollback_after_full_consume_reopens_SO_to_NEW_and_clears_consumed_qty`
- `ErpWeb.Tests.Production.Transaction.ProductionProcessHandoffServiceTests.Uom_contract_conversion_blank_fallback_and_mismatches`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.SO_INV_and_DO_INV_caps_and_cross_path`
- `ErpWeb.Tests.Production.Transaction.ProductionMaterialAllocationTests.Post_creates_inventory_and_production_facts_and_replay_is_idempotent`
- `ErpWeb.Tests.Production.Transaction.ProductionMaterialAllocationTests.Reconciliation_reports_open_drafts_then_unused_issued_material`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.Mixed_invoice_LinkDo_plus_plain_does_not_double_deduct`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.Authz_denies_post_without_permission`
- `ErpWeb.Tests.Production.Transaction.ProductionMaterialAllocationTests.Rollback_then_edit_qty_and_repost_preserves_historical_batch_details`
- `ErpWeb.Tests.Sales.Transaction.SaSoServiceTests.Invoice_LinkDo_posts_DO_INV_and_rejects_mix_with_SO_INV`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.Mixed_DO_SO_and_standalone_lines_invoice_updates_SO_only_for_bound`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.Reconciliation_reports_no_findings_at_every_lifecycle_step`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.Combined_SO_INV_and_DO_INV_cap_and_multi_DO_same_SO`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.Allocation_reconciliation_seeded_has_no_findings`
- `ErpWeb.Tests.Sales.Transaction.SaSoServiceTests.GetRemainingLines_returns_balance_only_and_closed_SO_returns_empty`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.SO_DO_exact_and_over_qty_and_split`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.Doc_flow_query_walks_SO_DO_INV_and_CN`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.ForceClose_one_of_two_dos_does_not_write_off_the_other`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.Pure_LinkDo_invoice_AddShipment_creates_no_batch`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.Mixed_DO_partial_billing_across_two_invoices`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.Billable_identity_holds_at_every_step`
- `ErpWeb.Tests.Sales.Transaction.SaSoServiceTests.ForceClose_sets_FORCE_CLOSED_without_changing_shipped_qty`
- `ErpWeb.Tests.Planning.Transaction.WorkOrderQuantityCalculatorTests.Missing_conversion_is_blocking_and_reported`
- `ErpWeb.Tests.Planning.Transaction.WorkOrderQuantityCalculatorTests.Machine_cycles_and_parallel_slots_both_round_up`
- `ErpWeb.Tests.Planning.Transaction.WorkOrderQuantityCalculatorTests.Material_requirement_applies_scrap_and_never_tolerance`
- `ErpWeb.Tests.Planning.Transaction.WorkOrderQuantityCalculatorTests.Machine_cycle_slots_round_up_when_capacity_does_not_divide`
- `ErpWeb.Tests.Planning.Transaction.WorkOrderQuantityCalculatorTests.Material_requirement_converts_the_operation_output_into_the_bom_denominator_uom`
- `ErpWeb.Tests.Planning.Transaction.WorkOrderQuantityCalculatorTests.Machine_absorbed_cost_uses_cycle_count_and_excludes_queue`
- `ErpWeb.Tests.Planning.Transaction.WorkOrderQuantityCalculatorTests.Parallel_processes_each_receive_the_full_route_quantity`
- `ErpWeb.Tests.Planning.Transaction.WorkOrderQuantityCalculatorTests.Labour_amount_uses_the_consuming_operation_output_and_ignores_non_contributing_rows`
- `ErpWeb.Tests.Planning.Transaction.WorkOrderQuantityCalculatorTests.Zero_output_per_cycle_is_blocking`
- `ErpWeb.Tests.Sales.Transaction.SaSoServiceTests.Over_consume_second_DO_save_fails_ALLOC_OVER`
- `ErpWeb.Tests.Sales.Transaction.SaEInvoiceSelfBillAndTinTests.Self_billed_types_use_the_note_and_invoice_ubl_roots(code: "12", expectedNamespace: "urn:oasis:names:specification:ubl:schema:xsd:Credi"···)`
- `ErpWeb.Tests.Sales.Transaction.SaEInvoiceSelfBillAndTinTests.Self_billed_types_use_the_note_and_invoice_ubl_roots(code: "13", expectedNamespace: "urn:oasis:names:specification:ubl:schema:xsd:Debit"···)`
- `ErpWeb.Tests.Planning.Transaction.ProductionWorkOrderServiceTests.Release_requires_approve_permission_server_side`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.ForceClose_full_do_writes_off_full_qty`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.ForceClose_is_irreversible`
- `ErpWeb.Tests.Planning.Transaction.ProductionWorkOrderServiceTests.Draft_cancel_requires_reason_and_blocks_release`
- `ErpWeb.Tests.Sales.Transaction.SaSoServiceTests.ForceClosed_SO_blocks_DO_rollback_and_DO_stays_POSTED`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.Soft_reserve_force_closed_DO_and_rollback_and_tenant_exclude`
- `ErpWeb.Tests.Planning.Transaction.ProductionWorkOrderServiceTests.Nested_phantom_material_keeps_its_own_bom_revision_and_line_snapshots`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.Soft_reserve_DO_edit_exclude_and_NEW_to_POSTED`
- `ErpWeb.Tests.Sales.Transaction.SaSoServiceTests.Fully_consumed_SO_rollback_second_DO_reopens_SHIPPED_with_balance`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.Invoice_rollback_restores_projections_and_statuses`
- `ErpWeb.Tests.Sales.Transaction.SaSoServiceTests.Update_OrderQty_reduced_to_delivered_stays_SHIPPED_until_billed`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.ForceClose_partially_billed_do_writes_off_remainder`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.WrittenOffQty_reconciles_to_force_closed_do_contributions`
- `ErpWeb.Tests.Sales.Transaction.SaSoServiceTests.Duplicate_SO_lines_on_one_DO_consume_and_rollback_full_aggregate`
- `ErpWeb.Tests.Sales.Transaction.SaSoServiceTests.SO_RowVersion_advances_on_consume_and_old_token_update_fails`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.ForceClose_fully_billed_do_writes_off_nothing`
- `ErpWeb.Tests.Sales.Transaction.SaSoServiceTests.ForceClose_already_CLOSED_SO_fails`
- `ErpWeb.Tests.Sales.Transaction.SaDocApplicationTests.Soft_reserve_multi_detail_same_SO_line_and_draft_invoice_blocks_DO`
- `ErpWeb.Tests.Sales.Transaction.SaSoServiceTests.Update_SHIPPED_cannot_change_customer`
- `ErpWeb.Tests.Planning.Transaction.ProductionWorkOrderServiceTests.Branch_scope_cannot_read_another_branch_work_order`
- `ErpWeb.Tests.Sales.Transaction.SaSoServiceTests.DO_post_consumes_SO_and_sets_detail_consumed_qty`
- `ErpWeb.Tests.Planning.Transaction.ProductionWorkOrderServiceTests.Release_detects_case_only_snapshot_text_tampering`
- `ErpWeb.Tests.Planning.Transaction.ProductionWorkOrderServiceTests.Release_freezes_snapshot_and_has_no_inventory_side_effect`
- `ErpWeb.Tests.Sales.Transaction.SaSoServiceTests.Update_OrderQty_increased_above_shipped_stays_SHIPPED`
- `ErpWeb.Tests.Production.Transaction.ProductionCostGateTests.Ip_success_creates_sealed_verified_issue_lineage`
- `ErpWeb.Tests.Production.Transaction.ProductionCostGateTests.Daily_rejects_missing_unvalued_and_mismatched_pools_and_zero_cost_basis`
- `ErpWeb.Tests.Production.Transaction.ProductionCostGateTests.Ip_sealed_replay_from_new_draft_is_reconciliation_failure`
- `ErpWeb.Tests.Production.Transaction.ProductionCostGateTests.Ip_invariant_failure_rolls_back_the_transaction`
- `ErpWeb.Tests.Production.Transaction.ProductionCostGateTests.Ip_rejects_missing_negative_and_blank_cost_evidence_and_allows_evidenced_zero`

Full assertion messages and stacks are preserved in `artifacts/form-layout/phase1-tests.log`. The clean-baseline sample is in `artifacts/form-layout/baseline-tests.log`.
