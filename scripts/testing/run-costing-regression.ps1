param(
    [switch]$NoBuild,
    [switch]$IncludeSqlServer,
    [string]$SqlServer = '.\SQLEXPRESS'
)

# This is a repeatable focused gate, not the full suite or production certification.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$patterns = @(
    'StockReplayPhysicalSimulatorTests', 'StockReplayWacSimulatorTests',
    'StockReplayFifoLayerAllocatorTests', 'StockReplayFifoReversedIssueProjectorTests',
    'StockReplayStandardSimulatorTests', 'StockReplayValuationGenerationResolverTests',
    'InventoryValuationServiceTests', 'IvGoodsReceiptServiceTests', 'IvMiscReceiptServiceTests',
    'IvStockTransferPostingServiceTests', 'IvPeriodCloseServiceTests', 'IvPeriodCloseGuardTests',
    'CostingDiagnosticCenterTests', 'QaPhysicalHoldTests', 'QaIncomingGrantTests',
    'PoInvoiceServiceTests', 'PoCdnServiceTests',
    'ProductionIssueEffectiveCostReaderTests', 'ProductionEffectiveCostResolverTests',
    'ProductionDependencyOwnerReaderTests', 'ProductionReplayDependencyPlannerTests',
    'ProductionPooledCostTraceMathTests', 'ProductionMaterialAllocationTests',
    'ProductionOutputConsumeVarianceTests', 'ProductionDailyOutputCoreTests',
    'Receipt_reposts_and_rolls_back_again_after_reversed_sale_correction',
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
    'Backdated_receipt_before_mixed_later_receipts_respects_qa_hold'
)
$required = @(
    'StockReplayWacSimulatorTests', 'StockReplayFifoLayerAllocatorTests',
    'StockReplayStandardSimulatorTests', 'InventoryValuationServiceTests',
    'IvPeriodCloseServiceTests', 'QaPhysicalHoldTests', 'PoInvoiceServiceTests',
    'ProductionEffectiveCostResolverTests',
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
    'Backdated_receipt_before_mixed_later_receipts_respects_qa_hold'
)
$filter = '(' + (($patterns | ForEach-Object { 'FullyQualifiedName~' + $_ }) -join '|') +
    ')&FullyQualifiedName!~SqlServer&Category!=SqlServer'
$resultName = 'costing-focused-' + [Guid]::NewGuid().ToString('N') + '.trx'
$resultDirectory = Join-Path $repo 'ErpWeb.Tests\TestResults\CostingRegression'
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
