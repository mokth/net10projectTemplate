param(
    [switch]$NoBuild,
    [switch]$IncludeSqlServer,
    [string]$SqlServer = '.\SQLEXPRESS',
    [string]$ResultsDirectory = ''
)

# This is a repeatable focused gate, not the full suite or production certification.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$patterns = @(
    'StockLedgerInfrastructureTests', 'StockReplayPhysicalSimulatorTests', 'StockReplayWacSimulatorTests',
    'StockReplayFifoLayerAllocatorTests', 'StockReplayFifoReversedIssueProjectorTests',
    'StockReplayStandardSimulatorTests', 'StockReplayValuationGenerationResolverTests',
    'InventoryValuationServiceTests', 'IvGoodsReceiptServiceTests', 'IvMiscReceiptServiceTests',
    'IvStockTransferPostingServiceTests', 'IvPeriodCloseServiceTests', 'IvPeriodCloseGuardTests', 'V2PeriodCloseReplayReconciliationTests',
    'CostingDiagnosticCenterTests', 'QaPhysicalHoldTests', 'QaIncomingGrantTests',
    'QaFgGrantTests', 'QaLogicalHoldTests',
    'SubconPostBlockedTests', 'SubconModeTests', 'SubconLiveCustodyPostingTests',
    'Backdated_wac_send_out_before_later_issue_posts_rolls_back_and_reposts',
    'Backdated_wac_receive_back_before_later_issue_posts_rolls_back_and_reposts',
    'Backdated_wac_receive_back_to_new_destination_posts_rolls_back_and_reposts',
    'Backdated_subcon_replay_refuses_closed_period_or_cross_month_tail_atomically',
    'Backdated_subcon_receive_back_rollback_respects_quality_holds',
    'Backdated_subcon_send_out_rejects_historical_shortage_even_when_current_stock_is_sufficient',
    'Backdated_subcon_send_out_uses_historical_wac_when_later_receipt_changes_the_average',
    'Backdated_subcon_rollback_refuses_a_closed_original_period_without_mutation',
    'Backdated_subcon_rollback_rejects_changed_link_or_custody_allocation',
    'Backdated_subcon_receive_back_rollback_rejects_changed_custody_allocation',
    'PoInvoiceServiceTests', 'PoCdnServiceTests',
    'Backdated_purchase_invoice_reprices_direct_sale_and_rolls_back',
    'Backdated_purchase_invoice_before_sale_respects_disabled_replay',
    'Backdated_purchase_invoice_rejects_changed_direct_sale',
    'Backdated_purchase_invoice_reprices_sale_and_inventory_issue',
    'Backdated_purchase_invoice_reprices_sale_and_two_inventory_issues',
    'Backdated_purchase_invoice_replays_sale_and_ninety_nine_issues_at_boundary',
    'Purchase_invoice_before_sale_and_issue_reprices_both_on_rollback',
    'Backdated_purchase_invoice_before_sale_and_issue_respects_disabled_replay',
    'Backdated_purchase_invoice_rejects_damaged_sale_issue_cost',
    'Backdated_purchase_invoice_rejects_mixed_sale_issue_and_return_draft',
    'Backdated_purchase_invoice_reprices_sale_and_receipt',
    'Backdated_purchase_invoice_rejects_receipt_before_sale',
    'Backdated_purchase_invoice_reprices_sale_issue_and_receipt',
    'Backdated_purchase_invoice_rejects_damaged_sale_receipt',
    'Backdated_purchase_invoice_before_sale_receipt_respects_disabled_replay',
    'Backdated_purchase_invoice_reprices_sale_and_later_goods_receipt',
    'Backdated_purchase_invoice_reprices_sale_issue_and_later_goods_receipt',
    'Purchase_invoice_before_sale_and_goods_receipt_reprices_on_rollback',
    'Backdated_purchase_invoice_rejects_goods_receipt_before_sale',
    'Backdated_purchase_invoice_rejects_damaged_goods_receipt_after_sale',
    'Backdated_purchase_invoice_rejects_sale_with_return_draft',
    'Purchase_invoice_before_direct_sale_reprices_sale_on_rollback',
    'Purchase_invoice_before_canceled_sale_reprices_both_legs_on_rollback_and_repost',
    'Backdated_purchase_invoice_after_canceled_sale_',
    'Purchase_invoice_canceled_sale_replay_',
    'Purchase_invoice_canceled_sale_repost_rejects_damaged_audit',
    'Purchase_invoice_rollback_after_sale_cancellation_is_blocked_atomically',
    'Backdated_purchase_invoice_after_sale_cancellation_is_blocked_atomically',
    'Purchase_invoice_during_canceled_sale_is_blocked_atomically',
    'Backdated_purchase_invoice_with_damaged_canceled_sale_is_blocked_atomically',
    'Zero_variance_purchase_invoice_after_sale_cancellation_can_post_and_rollback',
    'Sales_return_after_backdated_purchase_invoice_uses_corrected_sale_cost',
    'Backdated_purchase_invoice_reprices_delivery_and_rolls_back',
    'Purchase_invoice_before_delivery_reprices_cost_on_rollback',
    'Backdated_purchase_invoice_reprices_partially_billed_delivery_and_rolls_back',
    'Backdated_purchase_invoice_reprices_two_partial_invoices_on_one_delivery_and_rolls_back',
    'Backdated_purchase_credit_note_reprices_partially_billed_delivery_and_rolls_back',
    'Backdated_purchase_invoice_rejects_damaged_delivery_billing',
    'Backdated_purchase_invoice_rejects_delivery_billing_changed_after_planning',
    'Backdated_purchase_invoice_rejects_damaged_delivery_line',
    'Backdated_purchase_invoice_delivery_replay_blocks_damaged_audit',
    'Backdated_purchase_invoice_before_delivery_respects_disabled_replay',
    'Backdated_purchase_invoice_rejects_return_draft_on_billed_delivery',
    'Sales_return_after_backdated_purchase_invoice_uses_corrected_delivery_cost',
    'ProductionIssueEffectiveCostReaderTests', 'ProductionEffectiveCostResolverTests',
    'ProductionDependencyOwnerReaderTests', 'ProductionReplayDependencyPlannerTests',
    'ProductionPooledCostTraceMathTests', 'ProductionMaterialAllocationTests',
    'ProductionOutputConsumeVarianceTests', 'ProductionDailyOutputCoreTests',
    'Receipt_reposts_and_rolls_back_again_after_reversed_sale_correction',
    'Receipt_and_reversed_sale_at_same_time_remain_blocked_atomically',
    'Receipt_first_rollback_reprices_canceled_sale_and_later_issue',
    'Receipt_first_rollback_reprices_canceled_sale_and_multiple_later_issues',
    'Receipt_first_rollback_reprices_canceled_sale_and_three_later_issues',
    'Receipt_first_rollback_reprices_canceled_sale_and_unequal_later_issues',
    'Receipt_first_rollback_with_multiple_issues_respects_qa_hold',
    'Receipt_first_rollback_with_multiple_issues_rejects_negative_stock',
    'Receipt_first_repost_with_multiple_issues_rejects_missing_audit_evidence',
    'Receipt_first_rollback_with_later_issue_respects_qa_hold',
    'Receipt_first_repost_with_later_issue_rejects_missing_audit_evidence',
    'Receipt_first_rollback_reprices_canceled_sale_and_mixed_receipts_and_issues',
    'Receipt_first_rollback_reprices_issues_around_a_later_receipt',
    'Receipt_first_rollback_with_mixed_receipts_and_issues_',
    'Receipt_repost_correction_blocks_close_when_undo_evidence_is_missing',
    'Receipt_repost_rejects_changed_quantity_or_cost',
    'Receipt_repost_rejects_damaged_receipt_reversal_history',
    'Receipt_repost_cycle_respects_active_qa_stock_hold',
    'Receipt_rollback_reprices_an_isolated_reversed_sale',
    'Receipt_reversed_sale_correction_blocks_close_when_evidence_is_invalid',
    'Backdated_singleline_wac_receipt_with_reversed_sale',
    'Backdated_multiline_wac_receipt_with_reversed_sale',
    'Backdated_goods_receipt_with_reversed_sale_requires_unchanged_sale_cost',
    'Backdated_wac_receipt_reprices_reversed_issue_and_rolls_back',
    'Backdated_wac_receipt_reprices_already_reversed_issue_without_output',
    'Backdated_wac_receipt_reprices_posted_sales_return_and_rolls_back',
    'Backdated_multiline_wac_receipt_reprices_posted_sales_return_and_rolls_back',
    'Backdated_multiline_wac_receipt_rolls_back_after_invoice',
    'Backdated_wac_receipt_reprices_do_and_both_can_roll_back',
    'Backdated_multiline_wac_receipt_rolls_back_after_do',
    'Backdated_wac_transfer_posts_rolls_back_and_reposts_before_later_transfer',
    'Wac_transfer_rollback_after_earlier_replay_restores_both_piles',
    'Backdated_wac_transfer_preserves_later_issue_or_rejects_shortage',
    'Wac_backdated_lot_issue_posts_and_rolls_back_before_later_issue',
    'Public_historical_WAC_count_posts_rolls_back_and_reposts_before_later_issue',
    'Replay_enabled_current_day_count_anchors_',
    'Replay_enabled_count_requires_coordinator_without_changing_stock',
    'Replay_count_post_and_rollback_respect_qa_stock_holds',
    'Open_month_issue_before_count_replays_and_rolls_back',
    'Open_month_receipt_before_count_replays_and_rolls_back',
    'Receipt_before_count_rollback_respects_quality_holds',
    'Receipt_before_count_reprices_changed_cost_and_restores_on_rollback',
    'Receipt_before_count_reprices_later_issue_on_post_rollback_and_repost',
    'Receipt_before_count_keeps_disabled_or_unsupported_later_tail_blocked',
    'Receipt_before_count_with_later_issue_rollback_respects_quality_hold',
    'Receipt_before_count_does_not_cross_or_strand_later_subcon_dispatch',
    'Issue_before_count_respects_quality_holds',
    'Issue_before_count_rejects_historical_shortage_even_when_current_stock_is_sufficient',
    'Issue_before_zero_count_rejects_insufficient_stock_or_an_unhandled_later_movement',
    'Wac_zero_physical_count_replays_earlier_issue_and_receipt_and_rolls_back',
    'Standard_movements_before_single_pile_count_post_rollback_and_repost',
    'Fifo_movements_before_single_pile_count_post_rollback_and_repost',
    'Public_backdated_WAC_count_replays_later_Inventory_transfer',
    'Public_backdated_WAC_count_replays_real_later_Inventory_MI_owner',
    'Public_backdated_WAC_count_preserves_real_later_Inventory_MR_value',
    'Initial_historical_WAC_count_reprices_a_later_issue_and_seals',
    'Backdated_receipt_reprices_a_canceled_sale_and_repeats_the_rollback_cycle',
    'Backdated_receipt_after_canceled_sale_',
    'Backdated_receipt_replays_canceled_sale_before_a_later_receipt',
    'Backdated_receipt_replays_canceled_sale_before_multiple_later_receipts',
    'Backdated_receipt_before_multiple_later_receipts_',
    'Backdated_receipt_reprices_canceled_sale_and_later_issue',
    'Backdated_receipt_reprices_canceled_sale_and_multiple_later_issues',
    'Backdated_receipt_reprices_canceled_sale_and_three_later_issues',
    'Backdated_receipt_reprices_canceled_sale_and_unequal_later_issues',
    'Backdated_receipt_reprices_canceled_sale_and_mixed_receipts_and_issues',
    'Backdated_receipt_before_mixed_receipts_and_issues_',
    'Backdated_receipt_before_canceled_sale_and_multiple_issues_',
    'Backdated_receipt_reprices_canceled_sale_and_issue_after_later_receipts',
    'Backdated_receipt_reprices_canceled_sale_and_issue_after_later_goods_receipt',
    'Backdated_receipt_before_canceled_sale_and_issue_',
    'Backdated_receipt_replays_canceled_sale_before_misc_and_goods_receipts',
    'Backdated_receipt_replays_canceled_sale_before_multiple_purchase_orders',
    'Backdated_receipt_before_later_goods_receipt_rejects_invoiced_purchase_order',
    'Backdated_receipt_before_mixed_later_receipts_respects_qa_hold',
    'Public_multi_pile_FIFO_and_Standard_count_posts_rolls_back_and_reposts',
    'Fifo_replay_with_active_finished_goods_does_not_report_stale_cost_trace',
    'Backdated_wac_receipt_reprices_active_finished_good_and_restores_it_on_removal',
    'Backdated_fifo_receipt_reprices_active_finished_good_and_restores_it_on_removal',
    'Backdated_wac_receipt_protects_cost_when_finished_goods_may_be_reversed',
    'Backdated_fifo_receipt_rejects_cost_change_after_finished_good_reversal',
    'Backdated_wac_and_standard_receipt_rollback_respects_qa_stock_holds',
    'Backdated_wac_corrected_finished_good_rollback_respects_qa_stock_holds',
    'Backdated_wac_and_standard_goods_receipt_respects_qa_gate_and_restores_allocation',
    'Wac_cost_neutral_reversed_issue_goods_receipt_respects_incoming_qa',
    'Backdated_wac_receipt_reprices_production_and_fg_while_preserving_active_qa_logical_wip_hold',
    'Backdated_wac_receipt_reprices_single_padj_out_and_rolls_back',
    'Backdated_wac_receipt_reprices_multiple_padj_out_and_rolls_back',
    'Backdated_wac_receipt_reprices_output_and_work_order_transfer_and_rolls_back',
    'Backdated_fifo_receipt_reprices_padj_out_and_rolls_back',
    'Backdated_fifo_receipt_reprices_output_and_work_order_transfer_and_rolls_back',
    'Backdated_wac_receipt_reprices_production_stock_return_and_rolls_back',
    'Backdated_fifo_receipt_reprices_production_stock_return_and_rolls_back',
    'Backdated_standard_receipt_preserves_production_stock_return_and_rolls_back',
    'Backdated_standard_receipt_preserves_output_and_work_order_transfer_and_rolls_back',
    'Backdated_receipt_through_finished_good_reprices_or_preserves_downstream_inventory_and_sales',
    'Backdated_receipt_through_finished_good_rejects_unsupported_fg_transfer',
    'Backdated_receipt_replays_production_stock_return_consumed_by_later_inventory_issue_and_rolls_back',
    'Backdated_fifo_receipt_rejects_production_stock_return_consumed_by_transfer',
    'Backdated_receipt_replays_multi_stage_wip_handoff_to_finished_good_and_rolls_back',
    'Backdated_receipt_rejects_multi_stage_wip_handoff_with_extra_second_stage_material',
    'Backdated_receipt_replays_work_order_transfer_to_downstream_output_and_finished_good_and_rolls_back',
    'Backdated_receipt_rejects_work_order_transfer_with_extra_target_consumption_material',
    'Backdated_standard_receipt_preserves_active_finished_good_and_rolls_back',
    'Backdated_standard_receipt_preserves_reversed_finished_good_and_rolls_back',
    'Backdated_wac_receipt_reprices_material_padj_in_and_out_authorities',
    'Backdated_fifo_receipt_reprices_material_padj_in_and_out_authorities',
    'Backdated_wac_receipt_reprices_output_wip_padj_with_manual_or_zero_authority',
    'Backdated_fifo_receipt_reprices_output_wip_padj_with_manual_authority',
    'Backdated_standard_receipt_preserves_output_wip_padj_with_standard_authority',
    'Standard_cost_backdated_purchase_invoice_posts_and_rolls_back_before_later_issue',
    'Future_draft_is_allowed_in_open_period_but_refused_on_post_and_closed_period_draft_is_refused',
    'Rollback_is_refused_when_original_transfer_period_is_closed_even_in_later_open_period',
    'Same_day_later_time_transfer_posts_under_date_only_business_date_guard',
    'Backdated_lot_transfer_creates_new_lot_and_pile_and_rolls_back_cleanly',
    'Backdated_transfer_creates_new_non_lot_destination_pile_rolls_back_and_reposts',
    'Backdated_new_destination_transfer_rejects_shortage_without_creating_pile',
    'Backdated_receipt_reprices_partially_billed_do_across_two_invoices_without_double_counting_cogs',
    'Backdated_wac_receipt_reprices_multiline_do_and_mixed_direct_linked_invoice',
    'Partially_billed_do_sales_return_attributes_cogs_reduction_only_to_returned_invoice',
    'Scrap_before_count_posts_rolls_back_and_reposts',
    'Vendor_return_before_count_updates_po_return_qty_and_restores_on_rollback',
    'Goods_receipt_before_count_reprices_changed_cost_updates_po_and_restores_on_rollback',
    'Public_backdated_FIFO_count_posts_rolls_back_and_reposts_with_bounded_tail',
    'Posted_return_rejects_legacy_rollback_after_either_ledger_is_restated'
)
$required = @(
    'StockLedgerInfrastructureTests', 'StockReplayWacSimulatorTests', 'StockReplayFifoLayerAllocatorTests',
    'StockReplayStandardSimulatorTests', 'InventoryValuationServiceTests',
    'Replay_enabled_current_day_count_anchors_and_reposts',
    'Replay_enabled_current_day_count_anchors_fifo_and_standard',
    'Replay_enabled_count_requires_coordinator_without_changing_stock',
    'Replay_count_post_and_rollback_respect_qa_stock_holds',
    'Open_month_issue_before_count_replays_and_rolls_back',
    'Open_month_receipt_before_count_replays_and_rolls_back',
    'Receipt_before_count_rollback_respects_quality_holds',
    'Receipt_before_count_reprices_changed_cost_and_restores_on_rollback',
    'Receipt_before_count_reprices_later_issue_on_post_rollback_and_repost',
    'Receipt_before_count_keeps_disabled_or_unsupported_later_tail_blocked',
    'Receipt_before_count_with_later_issue_rollback_respects_quality_hold',
    'Receipt_before_count_does_not_cross_or_strand_later_subcon_dispatch',
    'Issue_before_count_respects_quality_holds',
    'Issue_before_count_rejects_historical_shortage_even_when_current_stock_is_sufficient',
    'Issue_before_zero_count_rejects_insufficient_stock_or_an_unhandled_later_movement',
    'IvPeriodCloseServiceTests', 'QaPhysicalHoldTests', 'QaFgGrantTests', 'QaLogicalHoldTests', 'PoInvoiceServiceTests',
    'SubconPostBlockedTests', 'SubconModeTests', 'SubconLiveCustodyPostingTests',
    'Backdated_wac_send_out_before_later_issue_posts_rolls_back_and_reposts',
    'Backdated_wac_receive_back_before_later_issue_posts_rolls_back_and_reposts',
    'Backdated_wac_receive_back_to_new_destination_posts_rolls_back_and_reposts',
    'Backdated_subcon_replay_refuses_closed_period_or_cross_month_tail_atomically',
    'Backdated_subcon_receive_back_rollback_respects_quality_holds',
    'Backdated_subcon_send_out_rejects_historical_shortage_even_when_current_stock_is_sufficient',
    'Backdated_subcon_send_out_uses_historical_wac_when_later_receipt_changes_the_average',
    'Backdated_subcon_rollback_refuses_a_closed_original_period_without_mutation',
    'Backdated_subcon_rollback_rejects_changed_link_or_custody_allocation',
    'Backdated_subcon_receive_back_rollback_rejects_changed_custody_allocation',
    'Backdated_MR_targeting_subcon_custody_warehouse_is_blocked_even_with_replay_enabled',
    'Backdated_MR_and_StockCount_cannot_reprice_or_strand_later_subcon_custody_transfer',
    'Warehouse_and_location_delete_are_blocked_when_referenced_by_subcon_policy_or_map',
    'Sales_shipment_allocation_excludes_subcon_custody_warehouse',
    'Purchase_credit_note_refuses_unreplayed_later_issue',
    'Purchase_credit_note_replays_later_issue_and_repeats_rollback',
    'Purchase_credit_note_replays_multiple_later_issues_and_repeats_rollback',
    'Purchase_credit_note_and_later_issues_replay_after_a_canceled_issue',
    'Purchase_credit_note_and_issue_rollback_share_business_date',
    'Purchase_issue_rollback_replays_later_same_day_note',
    'Purchase_issue_rollback_replays_note_on_later_open_period_day',
    'Purchase_issue_rollback_rejects_unproven_later_note',
    'Purchase_credit_note_issue_rollback_rejects_damaged_earlier_note',
    'Purchase_credit_note_replay_rejects_unproven_evidence',
    'Purchase_credit_note_replay_preserves_qa_hold',
    'Fifo_backdated_purchase_note_replays_partial_and_full_later_issues_and_repeats_rollback',
    'Fifo_purchase_note_blocks_pre_cdn_consumption_and_unreplayed_later_issue',
    'Backdated_wac_misc_receipt_replays_after_value_neutral_purchase_invoice',
    'Backdated_wac_misc_receipt_replays_fully_capitalized_invoice_variance',
    'Backdated_wac_misc_receipt_replays_partial_purchase_invoice_variance',
    'Backdated_wac_misc_receipt_rejects_damaged_partial_purchase_variance',
    'Backdated_wac_misc_receipt_rejects_damaged_purchase_replay_generation',
    'Backdated_wac_lot_receipt_replays_partial_purchase_variance',
    'Backdated_wac_receipt_replays_fully_consumed_purchase_variance',
    'Backdated_purchase_invoice_with_later_issue_uses_historical_consumed_variance',
    'Backdated_consumed_invoice_rejects_change_after_planning',
    'Backdated_consumed_invoice_rejects_cost_policy_change_in_later_tail',
    'Backdated_consumed_invoice_rejects_receipt_with_inconsistent_time',
    'Backdated_consumed_invoice_rejects_damaged_receipt_cost',
    'Backdated_purchase_invoice_reprices_later_issue_and_rollback',
    'Backdated_purchase_invoice_issue_replay_rejects_damaged_audit',
    'Backdated_purchase_invoice_can_roll_back_after_later_issue_rollback',
    'Purchase_invoice_posted_before_later_issue_reprices_issue_on_rollback',
    'Backdated_purchase_invoice_reprices_fully_depleted_later_issue',
    'Backdated_purchase_invoice_reprices_two_later_issues_and_rollback',
    'Purchase_invoice_posted_before_two_issues_reprices_both_on_rollback',
    'Backdated_purchase_invoice_reprices_ten_later_issues_and_rollback',
    'Purchase_invoice_posted_before_ten_issues_reprices_all_on_rollback',
    'Backdated_purchase_invoice_ten_issue_replay_rejects_damaged_later_audit',
    'Backdated_purchase_invoice_replays_one_hundred_issues_and_rejects_more',
    'Backdated_purchase_invoice_reprices_issues_around_later_receipt',
    'Purchase_invoice_posted_before_mixed_tail_reprices_issues_on_rollback',
    'Backdated_purchase_invoice_rejects_damaged_later_receipt_value',
    'Backdated_purchase_invoice_can_roll_back_after_later_receipt_is_removed',
    'Backdated_purchase_invoice_reprices_issues_around_later_goods_receipt',
    'Purchase_invoice_before_goods_receipt_reprices_issues_on_rollback',
    'Backdated_purchase_invoice_can_roll_back_after_later_goods_receipt_is_removed',
    'Backdated_purchase_invoice_rejects_invoiced_later_goods_receipt',
    'Backdated_purchase_invoice_rejects_damaged_later_goods_receipt',
    'Backdated_purchase_invoice_rejects_changed_later_goods_purchase_evidence',
    'Backdated_purchase_invoice_replays_later_goods_on_same_order_line',
    'Backdated_purchase_invoice_rolls_back_after_same_order_goods_receipt',
    'Purchase_invoice_before_same_order_goods_receipt_reprices_issues_on_rollback',
    'Purchase_invoice_rejects_another_invoice_on_later_same_order_goods',
    'Backdated_purchase_invoice_before_later_goods_respects_disabled_replay',
    'Same_order_goods_rollback_rejects_damaged_earlier_invoice',
    'Backdated_purchase_invoice_reprices_direct_sale_and_rolls_back',
    'Backdated_purchase_invoice_before_sale_respects_disabled_replay',
    'Backdated_purchase_invoice_rejects_changed_direct_sale',
    'Backdated_purchase_invoice_reprices_sale_and_inventory_issue',
    'Backdated_purchase_invoice_reprices_sale_and_two_inventory_issues',
    'Backdated_purchase_invoice_replays_sale_and_ninety_nine_issues_at_boundary',
    'Purchase_invoice_before_sale_and_issue_reprices_both_on_rollback',
    'Backdated_purchase_invoice_before_sale_and_issue_respects_disabled_replay',
    'Backdated_purchase_invoice_rejects_damaged_sale_issue_cost',
    'Backdated_purchase_invoice_rejects_mixed_sale_issue_and_return_draft',
    'Backdated_purchase_invoice_reprices_sale_and_receipt',
    'Backdated_purchase_invoice_rejects_receipt_before_sale',
    'Backdated_purchase_invoice_reprices_sale_issue_and_receipt',
    'Backdated_purchase_invoice_rejects_damaged_sale_receipt',
    'Backdated_purchase_invoice_before_sale_receipt_respects_disabled_replay',
    'Backdated_purchase_invoice_reprices_sale_and_later_goods_receipt',
    'Backdated_purchase_invoice_reprices_sale_issue_and_later_goods_receipt',
    'Purchase_invoice_before_sale_and_goods_receipt_reprices_on_rollback',
    'Backdated_purchase_invoice_rejects_goods_receipt_before_sale',
    'Backdated_purchase_invoice_rejects_damaged_goods_receipt_after_sale',
    'Backdated_purchase_invoice_rejects_sale_with_return_draft',
    'Purchase_invoice_before_direct_sale_reprices_sale_on_rollback',
    'Purchase_invoice_before_canceled_sale_reprices_both_legs_on_rollback_and_repost',
    'Backdated_purchase_invoice_after_canceled_sale_posts_and_rolls_back',
    'Backdated_purchase_invoice_after_canceled_sale_rejects_unproven_evidence',
    'Backdated_purchase_invoice_after_canceled_sale_respects_qa_hold',
    'Backdated_purchase_invoice_after_canceled_sale_rejects_damaged_audit',
    'Purchase_invoice_canceled_sale_replay_respects_disabled_flag',
    'Purchase_invoice_canceled_sale_replay_rejects_changed_or_closed_evidence',
    'Purchase_invoice_canceled_sale_replay_rejects_later_receipt',
    'Purchase_invoice_canceled_sale_replay_preserves_qa_hold',
    'Purchase_invoice_canceled_sale_repost_rejects_damaged_audit',
    'Purchase_invoice_rollback_after_sale_cancellation_is_blocked_atomically',
    'Backdated_purchase_invoice_after_sale_cancellation_is_blocked_atomically',
    'Purchase_invoice_during_canceled_sale_is_blocked_atomically',
    'Backdated_purchase_invoice_with_damaged_canceled_sale_is_blocked_atomically',
    'Zero_variance_purchase_invoice_after_sale_cancellation_can_post_and_rollback',
    'Sales_return_after_backdated_purchase_invoice_uses_corrected_sale_cost',
    'Backdated_purchase_invoice_reprices_delivery_and_rolls_back',
    'Purchase_invoice_before_delivery_reprices_cost_on_rollback',
    'Backdated_purchase_invoice_reprices_partially_billed_delivery_and_rolls_back',
    'Backdated_purchase_invoice_reprices_two_partial_invoices_on_one_delivery_and_rolls_back',
    'Backdated_purchase_credit_note_reprices_partially_billed_delivery_and_rolls_back',
    'Backdated_purchase_invoice_rejects_damaged_delivery_billing',
    'Backdated_purchase_invoice_rejects_delivery_billing_changed_after_planning',
    'Backdated_purchase_invoice_rejects_damaged_delivery_line',
    'Backdated_purchase_invoice_delivery_replay_blocks_damaged_audit',
    'Backdated_purchase_invoice_before_delivery_respects_disabled_replay',
    'Backdated_purchase_invoice_rejects_return_draft_on_billed_delivery',
    'Sales_return_after_backdated_purchase_invoice_uses_corrected_delivery_cost',
    'Fully_consumed_wac_invoice_reverses_its_zero_value_anchor',
    'Backdated_wac_receipt_rejects_purchase_variance_without_original_stock_value',
    'Backdated_wac_misc_receipt_replays_issue_before_same_day_invoice_variance',
    'Backdated_wac_misc_receipt_rejects_damaged_invoice_variance_evidence',
    'Backdated_purchase_invoice_price_variance_rejects_later_active_issue',
    'Backdated_purchase_credit_note_preserves_later_issue_cost',
    'Purchase_variance_can_roll_back_before_backdated_receipt_after_issue_rollback',
    'Backdated_wac_misc_receipt_rejects_damaged_purchase_evidence',
    'Backdated_wac_misc_receipt_before_purchase_invoice_respects_qa_hold',
    'Neutral_purchase_invoice_can_roll_back_before_backdated_receipt',
    'ProductionEffectiveCostResolverTests',
    'Receipt_and_reversed_sale_at_same_time_remain_blocked_atomically',
    'Receipt_first_rollback_reprices_canceled_sale_and_later_issue',
    'Receipt_first_rollback_reprices_canceled_sale_and_multiple_later_issues',
    'Receipt_first_rollback_reprices_canceled_sale_and_three_later_issues',
    'Receipt_first_rollback_reprices_canceled_sale_and_unequal_later_issues',
    'Receipt_first_rollback_with_multiple_issues_respects_qa_hold',
    'Receipt_first_rollback_with_multiple_issues_rejects_negative_stock',
    'Receipt_first_repost_with_multiple_issues_rejects_missing_audit_evidence',
    'Receipt_first_rollback_with_later_issue_respects_qa_hold',
    'Receipt_first_repost_with_later_issue_rejects_missing_audit_evidence',
    'Receipt_first_rollback_reprices_canceled_sale_and_mixed_receipts_and_issues',
    'Receipt_first_rollback_reprices_issues_around_a_later_receipt',
    'Receipt_first_rollback_with_mixed_receipts_and_issues_rejects_damaged_receipt',
    'Receipt_first_rollback_with_mixed_receipts_and_issues_respects_qa_hold',
    'Backdated_receipt_replays_canceled_sale_before_multiple_later_receipts',
    'Backdated_receipt_before_multiple_later_receipts_respects_qa_hold',
    'Backdated_receipt_before_multiple_later_receipts_and_issues_rejects_invoiced_goods',
    'Backdated_receipt_reprices_canceled_sale_and_later_issue',
    'Backdated_receipt_reprices_canceled_sale_and_multiple_later_issues',
    'Backdated_receipt_reprices_canceled_sale_and_three_later_issues',
    'Backdated_receipt_reprices_canceled_sale_and_unequal_later_issues',
    'Backdated_receipt_reprices_canceled_sale_and_mixed_receipts_and_issues',
    'Backdated_receipt_before_mixed_receipts_and_issues_respects_qa_hold',
    'Backdated_receipt_before_mixed_receipts_and_issues_rejects_damaged_later_receipt',
    'Backdated_receipt_before_canceled_sale_and_multiple_issues_respects_qa_hold',
    'Backdated_receipt_before_canceled_sale_and_multiple_issues_rejects_missing_issue_evidence',
    'Backdated_receipt_before_canceled_sale_and_multiple_issues_rejects_foreign_last_issue_owner',
    'Backdated_receipt_before_canceled_sale_and_multiple_issues_blocks_close_for_missing_issue_evidence',
    'Backdated_receipt_reprices_canceled_sale_and_issue_after_later_receipts',
    'Backdated_receipt_reprices_canceled_sale_and_issue_after_later_goods_receipt',
    'Backdated_receipt_before_canceled_sale_and_issue_blocks_close_for_damaged_evidence',
    'Backdated_receipt_before_canceled_sale_and_issue_rejects_rollback_for_missing_issue_evidence',
    'Backdated_receipt_before_canceled_sale_and_issue_rejects_foreign_issue_owner',
    'Backdated_receipt_before_canceled_sale_and_issue_rejects_damaged_issue_source',
    'Backdated_receipt_before_canceled_sale_and_issue_rejects_overlapping_reversed_value',
    'Backdated_receipt_replays_canceled_sale_before_misc_and_goods_receipts',
    'Backdated_receipt_replays_canceled_sale_before_multiple_purchase_orders',
    'Backdated_receipt_before_later_goods_receipt_rejects_invoiced_purchase_order',
    'Backdated_receipt_before_mixed_later_receipts_respects_qa_hold',
    'Public_multi_pile_FIFO_and_Standard_count_posts_rolls_back_and_reposts',
    'Purchase_credit_note_replays_partly_consumed_and_fully_later_issued_stock',
    'Fifo_backdated_purchase_note_replays_partial_and_full_later_issues_and_repeats_rollback',
    'Fifo_purchase_note_blocks_pre_cdn_consumption_and_unreplayed_later_issue',
    'Purchase_issue_rollback_reprices_partly_consumed_later_note_and_note_rolls_back_active_generation',
    'Fifo_replay_with_active_finished_goods_does_not_report_stale_cost_trace',
    'Backdated_wac_receipt_reprices_active_finished_good_and_restores_it_on_removal',
    'Backdated_fifo_receipt_reprices_active_finished_good_and_restores_it_on_removal',
    'Backdated_wac_receipt_protects_cost_when_finished_goods_may_be_reversed',
    'Backdated_fifo_receipt_rejects_cost_change_after_finished_good_reversal',
    'Backdated_wac_and_standard_receipt_rollback_respects_qa_stock_holds',
    'Backdated_wac_corrected_finished_good_rollback_respects_qa_stock_holds',
    'Backdated_wac_and_standard_goods_receipt_respects_qa_gate_and_restores_allocation',
    'Wac_cost_neutral_reversed_issue_goods_receipt_respects_incoming_qa',
    'Backdated_wac_receipt_reprices_production_and_fg_while_preserving_active_qa_logical_wip_hold',
    'Backdated_wac_receipt_reprices_single_padj_out_and_rolls_back',
    'Backdated_wac_receipt_reprices_multiple_padj_out_and_rolls_back',
    'Backdated_wac_receipt_reprices_output_and_work_order_transfer_and_rolls_back',
    'Backdated_fifo_receipt_reprices_padj_out_and_rolls_back',
    'Backdated_fifo_receipt_reprices_output_and_work_order_transfer_and_rolls_back',
    'Backdated_wac_receipt_reprices_production_stock_return_and_rolls_back',
    'Backdated_fifo_receipt_reprices_production_stock_return_and_rolls_back',
    'Backdated_standard_receipt_preserves_production_stock_return_and_rolls_back',
    'Backdated_standard_receipt_preserves_output_and_work_order_transfer_and_rolls_back',
    'Backdated_receipt_through_finished_good_reprices_or_preserves_downstream_inventory_and_sales',
    'Backdated_receipt_through_finished_good_rejects_unsupported_fg_transfer',
    'Backdated_receipt_replays_production_stock_return_consumed_by_later_inventory_issue_and_rolls_back',
    'Backdated_fifo_receipt_rejects_production_stock_return_consumed_by_transfer',
    'Backdated_receipt_replays_multi_stage_wip_handoff_to_finished_good_and_rolls_back',
    'Backdated_receipt_rejects_multi_stage_wip_handoff_with_extra_second_stage_material',
    'Backdated_receipt_replays_work_order_transfer_to_downstream_output_and_finished_good_and_rolls_back',
    'Backdated_receipt_rejects_work_order_transfer_with_extra_target_consumption_material',
    'Backdated_standard_receipt_preserves_active_finished_good_and_rolls_back',
    'Backdated_standard_receipt_preserves_reversed_finished_good_and_rolls_back',
    'Backdated_wac_receipt_reprices_material_padj_in_and_out_authorities',
    'Backdated_fifo_receipt_reprices_material_padj_in_and_out_authorities',
    'Backdated_wac_receipt_reprices_output_wip_padj_with_manual_or_zero_authority',
    'Backdated_fifo_receipt_reprices_output_wip_padj_with_manual_authority',
    'Backdated_standard_receipt_preserves_output_wip_padj_with_standard_authority',
    'Standard_cost_backdated_purchase_invoice_posts_and_rolls_back_before_later_issue',
    'Future_draft_is_allowed_in_open_period_but_refused_on_post_and_closed_period_draft_is_refused',
    'Rollback_is_refused_when_original_transfer_period_is_closed_even_in_later_open_period',
    'Same_day_later_time_transfer_posts_under_date_only_business_date_guard',
    'Backdated_lot_transfer_creates_new_lot_and_pile_and_rolls_back_cleanly',
    'Backdated_transfer_creates_new_non_lot_destination_pile_rolls_back_and_reposts',
    'Backdated_new_destination_transfer_rejects_shortage_without_creating_pile',
    'Backdated_receipt_reprices_partially_billed_do_across_two_invoices_without_double_counting_cogs',
    'Backdated_wac_receipt_reprices_multiline_do_and_mixed_direct_linked_invoice',
    'Partially_billed_do_sales_return_attributes_cogs_reduction_only_to_returned_invoice',
    'Invoice_cogs_proof_pro_rates_partially_billed_linked_do_and_rejects_overbilled_do',
    'Scrap_before_count_posts_rolls_back_and_reposts',
    'Vendor_return_before_count_updates_po_return_qty_and_restores_on_rollback',
    'Goods_receipt_before_count_reprices_changed_cost_updates_po_and_restores_on_rollback',
    'Public_backdated_FIFO_count_posts_rolls_back_and_reposts_with_bounded_tail',
    'Posted_return_rejects_legacy_rollback_after_either_ledger_is_restated'
)
$filter = '(' + (($patterns | ForEach-Object { 'FullyQualifiedName~' + $_ }) -join '|') +
    ')&FullyQualifiedName!~SqlServer&Category!=SqlServer'
$resultName = 'costing-focused-' + [Guid]::NewGuid().ToString('N') + '.trx'
$resultDirectory = if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    Join-Path $repo 'ErpWeb.Tests\TestResults\CostingRegression'
} else {
    $ResultsDirectory
}
$environment = @{
    'ConnectionStrings__DefaultConnection' = 'Server=127.0.0.1,1;Database=Codex_Disabled_Test;Connect Timeout=1;TrustServerCertificate=True'
    'ConnectionStrings__SqlServerTestConnection' = $null
    'ERPWEB_MULTI_WAC_SQL_TEST_CONNECTION' = $null
    'TEST_DB_CONNECTIONSTRING' = $null
}
$original = @{}
foreach ($key in $environment.Keys) {
    $original[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    [Environment]::SetEnvironmentVariable($key, $environment[$key], 'Process')
}

Push-Location -LiteralPath $repo
try {
    $arguments = @('test', 'ErpWeb.Tests\ErpWeb.Tests.csproj', '--no-restore', '-m:1', '-clp:ErrorsOnly',
        '--filter', $filter, '--logger', "trx;LogFileName=$resultName", '--logger', 'console;verbosity=minimal',
        '--results-directory', $resultDirectory)
    if ($NoBuild) { $arguments += '--no-build' }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'The focused costing regression failed.' }
    $resultPath = Join-Path $resultDirectory $resultName
    [xml]$result = Get-Content -LiteralPath $resultPath
    $counts = $result.TestRun.ResultSummary.Counters
    if ([int]$counts.passed -le 0 -or [int]$counts.failed -ne 0 -or [int]$counts.notExecuted -ne 0) {
        throw 'The focused costing regression had no passing tests, failures, or skipped tests. Inspect the result file.'
    }
    $passed = @($result.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Passed' })
    foreach ($name in $required) {
        if (-not @($passed | Where-Object { $_.testName.Contains($name) }).Count) {
            throw "The required costing test group was missing or did not pass: $name"
        }
    }
    Write-Output "Focused costing regression passed $($counts.passed) tests. Result: $resultPath"

    if ($IncludeSqlServer) {
        $sqlRunner = Join-Path $PSScriptRoot 'run-multi-wac-sql-scratch.ps1'
        & $sqlRunner -Server $SqlServer -NoBuild -NeutralLaterPurchaseInvoice
        & $sqlRunner -Server $SqlServer -NoBuild -FullyCapitalizedPurchaseVariance
        & $sqlRunner -Server $SqlServer -NoBuild -PartialPurchaseVariance
        & $sqlRunner -Server $SqlServer -NoBuild -PartialPurchaseVarianceLot
        & $sqlRunner -Server $SqlServer -NoBuild -FullyConsumedPurchaseVariance
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedConsumedInvoice
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceIssueReplay
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceIssueFirst
        & $sqlRunner -Server $SqlServer -NoBuild -InvoiceBeforeIssue
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceTwoIssues
        & $sqlRunner -Server $SqlServer -NoBuild -InvoiceBeforeTwoIssues
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceTenIssues
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceTenConcurrent
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceIssueBoundary
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceMixedReceipt
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceMixedReceiptFirst
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceMixedGoods
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceMixedGoodsFirst
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceSameOrder
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceSameOrderGoodsFirst
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceDirectSale
        & $sqlRunner -Server $SqlServer -NoBuild -InvoiceBeforeDirectSale
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceSaleReturn
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceUnbilledDelivery
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceBilledDelivery
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceDeliveryReturn
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceDirectSaleIssue
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceDeliveryIssue
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceDirectSaleTwoIssues
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceSaleIssueBoundary
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceSaleIssueReceipt
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceDeliveryReceipt
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceSaleGoods
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceDeliveryGoods
        & $sqlRunner -Server $SqlServer -NoBuild -InvoiceBeforeSaleGoods
        & $sqlRunner -Server $SqlServer -NoBuild -InvoiceCanceledSaleCycle
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceCanceledSaleCycle
        & $sqlRunner -Server $SqlServer -NoBuild -InvoiceRollbackCanceledSale
        & $sqlRunner -Server $SqlServer -NoBuild -InvoicePostCanceledSale
        & $sqlRunner -Server $SqlServer -NoBuild -InvoiceDuringCanceledSale
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedInvoiceDepleted
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedPurchaseInvoice
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedPurchaseCreditNotePost
        & $sqlRunner -Server $SqlServer -NoBuild -BackdatedPurchaseCreditNoteRollback
        & $sqlRunner -Server $SqlServer -NoBuild -PurchaseCdnLaterIssuePost
        & $sqlRunner -Server $SqlServer -NoBuild -PurchaseCdnLaterIssueRollback
        & $sqlRunner -Server $SqlServer -NoBuild -PurchaseCdnIssueReplay
        & $sqlRunner -Server $SqlServer -NoBuild -PurchaseCdnMultiIssueReplay
        & $sqlRunner -Server $SqlServer -NoBuild -PurchaseCdnCanceledIssueReplay
        & $sqlRunner -Server $SqlServer -NoBuild -PurchaseCdnSameDayReplay
        & $sqlRunner -Server $SqlServer -NoBuild -PurchaseCdnLaterSameDayReplay
        & $sqlRunner -Server $SqlServer -NoBuild -PurchaseCdnLaterDayReplay
        & $sqlRunner -Server $SqlServer -NoBuild -PurchaseCdnConsumedGuard
        & $sqlRunner -Server $SqlServer -NoBuild -CurrentDayCountReplay
        & $sqlRunner -Server $SqlServer -NoBuild -IssueBeforeZeroCountReplay
        & $sqlRunner -Server $SqlServer -NoBuild -IssueBeforeVarianceCountReplay
        & $sqlRunner -Server $SqlServer -NoBuild -ReceiptBeforeVarianceCountReplay
        & $sqlRunner -Server $SqlServer -NoBuild -CostChangingReceiptBeforeCountReplay
        & $sqlRunner -Server $SqlServer -NoBuild -ReceiptBeforeCountThenIssueReplay
        & $sqlRunner -Server $SqlServer -NoBuild -GoodsReceiptBeforeCountThenIssueReplay
        & $sqlRunner -Server $SqlServer -NoBuild -SubconWacReplay
        & $sqlRunner -Server $SqlServer -NoBuild -PartlyConsumedNoteReplay
        & $sqlRunner -Server $SqlServer -NoBuild -PartlyConsumedNoteIssueRollback
        & $sqlRunner -Server $SqlServer -NoBuild -ReversedSaleLate
        & $sqlRunner -Server $SqlServer -NoBuild -ReversedSaleLaterReceipts
        & $sqlRunner -Server $SqlServer -NoBuild -ReversedSaleMixedReceipts
        & $sqlRunner -Server $SqlServer -NoBuild -ReversedSaleLaterIssue
        & $sqlRunner -Server $SqlServer -NoBuild -ReversedSaleReceiptFirstIssue
        & $sqlRunner -Server $SqlServer -NoBuild -ReversedSaleMultipleIssues
        & $sqlRunner -Server $SqlServer -NoBuild -ReversedSaleReceiptFirstMultipleIssues
        & $sqlRunner -Server $SqlServer -NoBuild -ReversedSaleMixedReceiptsAndIssues
        & $sqlRunner -Server $SqlServer -NoBuild -ReversedSaleReceiptFirstInterleaved
        & $sqlRunner -Server $SqlServer -NoBuild -ReversedSaleCycle
        & $sqlRunner -Server $SqlServer -NoBuild -ReversedSaleSameTime
        Write-Output 'The selected SQL Server replay checks passed in disposable databases.'
    }
    else {
        Write-Output 'SQL Server checks were not run. Use -IncludeSqlServer for the selected disposable-database checks.'
    }
    Write-Output 'This focused gate does not replace the full regression suite, complete transaction coverage, or release readiness review.'
}
finally {
    foreach ($key in $environment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $original[$key], 'Process')
    }
    Pop-Location
}
