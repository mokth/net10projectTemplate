param(
    [string]$Server = '.\SQLEXPRESS',
    [switch]$NoBuild,
    [switch]$SameItem,
    [switch]$RepeatedAndOther,
    [switch]$TwoPiles,
    [switch]$MoreLines,
    [switch]$MixedLocations,
    [switch]$BothItems,
    [switch]$EditRace,
    [switch]$InjectFailure,
    [switch]$SalesReturn,
    [switch]$SalesReturnRolledBack,
    [switch]$SalesReturnFailure,
    [switch]$ReversedSale,
    [switch]$ReversedSaleEdit,
    [switch]$ReversedSaleBusy,
    [switch]$ReversedSaleSingle,
    [switch]$ReversedSaleGoods,
    [switch]$ReversedSaleCorrection,
    [switch]$ReversedSaleCycle,
    [switch]$ReversedSaleLate,
    [switch]$ReversedSaleLaterReceipt,
    [switch]$ReversedSaleLaterReceipts,
    [switch]$ReversedSaleMixedReceipts,
    [switch]$ReversedSaleLaterIssue,
    [switch]$ReversedSaleReceiptFirstIssue,
    [switch]$ReversedSaleMultipleIssues,
    [switch]$ReversedSaleReceiptFirstMultipleIssues
)

$ErrorActionPreference = 'Stop'
if ($SameItem -and $RepeatedAndOther) { throw 'Choose one receipt shape.' }
if ($TwoPiles -and ($SameItem -or $RepeatedAndOther)) { throw 'Two-pile tests use their own receipt shape.' }
if ($MixedLocations -and ($SameItem -or $RepeatedAndOther -or $TwoPiles -or $MoreLines)) { throw 'Mixed-location tests use their own receipt shape.' }
if ($BothItems -and (-not $MixedLocations -or $EditRace -or $InjectFailure)) { throw 'The both-item concurrency case requires only -MixedLocations.' }
if ($MoreLines -and -not $TwoPiles) { throw 'The multi-line case requires -TwoPiles.' }
if ($InjectFailure -and ($EditRace -or -not ($RepeatedAndOther -or $TwoPiles -or $MixedLocations))) { throw 'Choose a receipt shape for failure injection.' }
if (@($SalesReturn, $SalesReturnRolledBack, $SalesReturnFailure, $ReversedSale, $ReversedSaleEdit, $ReversedSaleBusy, $ReversedSaleSingle, $ReversedSaleGoods, $ReversedSaleCorrection, $ReversedSaleCycle, $ReversedSaleLate, $ReversedSaleLaterReceipt, $ReversedSaleLaterReceipts, $ReversedSaleMixedReceipts, $ReversedSaleLaterIssue, $ReversedSaleReceiptFirstIssue, $ReversedSaleMultipleIssues, $ReversedSaleReceiptFirstMultipleIssues).Where({ $_ }).Count -gt 1) { throw 'Choose one Sales case.' }
if (($SalesReturn -or $SalesReturnRolledBack -or $SalesReturnFailure -or $ReversedSale -or $ReversedSaleEdit -or $ReversedSaleBusy -or $ReversedSaleSingle -or $ReversedSaleGoods -or $ReversedSaleCorrection -or $ReversedSaleCycle -or $ReversedSaleLate -or $ReversedSaleLaterReceipt -or $ReversedSaleLaterReceipts -or $ReversedSaleMixedReceipts -or $ReversedSaleLaterIssue -or $ReversedSaleReceiptFirstIssue -or $ReversedSaleMultipleIssues -or $ReversedSaleReceiptFirstMultipleIssues) -and ($SameItem -or $RepeatedAndOther -or $TwoPiles -or $MoreLines -or $MixedLocations -or $BothItems -or $EditRace -or $InjectFailure)) { throw 'Choose the Sales case on its own.' }
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$scratch = 'Codex_ReplayNeutral_Test_' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
if ($scratch -cnotmatch '^Codex_ReplayNeutral_Test_[0-9a-f]{12}$') {
    throw 'Unexpected scratch database name.'
}

Push-Location -LiteralPath $repo
$created = $false
$scratchId = $null
$originalConnection = [Environment]::GetEnvironmentVariable('ERPWEB_MULTI_WAC_SQL_TEST_CONNECTION', 'Process')
try {
    $existing = & sqlcmd -S $Server -d master -E -b -h -1 -W -Q "SET NOCOUNT ON; SELECT COALESCE(CONVERT(varchar(12), DB_ID(N'$scratch')), 'NULL')"
    if ($LASTEXITCODE -ne 0 -or ($existing | Out-String).Trim() -ne 'NULL') {
        throw 'Could not verify that the scratch database name is unused.'
    }
    & sqlcmd -S $Server -d master -E -b -Q "CREATE DATABASE [$scratch]"
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the scratch database.' }
    $created = $true
    $scratchId = (& sqlcmd -S $Server -d master -E -b -h -1 -W -Q "SET NOCOUNT ON; SELECT CONVERT(varchar(12), DB_ID(N'$scratch'))" | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $scratchId -notmatch '^\d+$') {
        throw 'Could not verify the new scratch database.'
    }

    [Environment]::SetEnvironmentVariable('ERPWEB_MULTI_WAC_SQL_TEST_CONNECTION',
        "Server=$Server;Database=$scratch;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true", 'Process')
    $resultName = "multi-wac-sql-$scratch.trx"
    $resultDirectory = 'ErpWeb.Tests\TestResults\MultiWacScratch'
    $testName = if ($ReversedSaleReceiptFirstMultipleIssues) { 'SqlServer_receipt_first_rollback_reprices_canceled_sale_and_multiple_later_issues_atomically' }
        elseif ($ReversedSaleMultipleIssues) { 'SqlServer_late_receipt_reprices_canceled_sale_and_multiple_later_issues_atomically' }
        elseif ($ReversedSaleReceiptFirstIssue) { 'SqlServer_receipt_first_rollback_reprices_canceled_sale_and_later_issue_atomically' }
        elseif ($ReversedSaleLaterIssue) { 'SqlServer_late_receipt_reprices_canceled_sale_and_later_issue_atomically' }
        elseif ($ReversedSaleMixedReceipts) { 'SqlServer_late_receipt_before_mixed_later_receipts_repeats_atomically' }
        elseif ($ReversedSaleLaterReceipts) { 'SqlServer_late_receipt_before_multiple_later_receipts_repeats_atomically' }
        elseif ($ReversedSaleLaterReceipt) { 'SqlServer_late_receipt_before_later_receipt_repeats_atomically' }
        elseif ($ReversedSaleLate) { 'SqlServer_late_receipt_reprices_canceled_sale_and_repeats_atomically' }
        elseif ($ReversedSaleCycle) { 'SqlServer_receipt_repost_reverses_sale_correction_once_and_is_atomic' }
        elseif ($ReversedSaleCorrection) { 'SqlServer_receipt_rollback_reprices_reversed_sale_once_and_is_atomic' }
        elseif ($ReversedSaleGoods) { 'SqlServer_goods_receipt_with_neutral_reversed_sale_posts_and_rolls_back' }
        elseif ($ReversedSaleSingle) { 'SqlServer_singleline_receipt_with_reversed_sale_waits_safely' }
        elseif ($ReversedSaleBusy) { 'SqlServer_backdated_reversed_sale_does_not_wait_for_invoice_lock' }
        elseif ($ReversedSaleEdit) { 'SqlServer_backdated_reversed_sale_rejects_changed_invoice' }
        elseif ($ReversedSale) { 'SqlServer_backdated_receipt_with_reversed_sale_and_later_issue' }
        elseif ($SalesReturnFailure) { 'SqlServer_backdated_receipt_sales_return_is_atomic_on_failure' }
        elseif ($SalesReturnRolledBack) { 'SqlServer_backdated_receipt_after_rolled_back_return' }
        elseif ($SalesReturn) { 'SqlServer_backdated_receipt_reprices_two_sales_and_one_return' }
        elseif ($BothItems) { 'Concurrent_both_item_locations_receipt_posts_and_rolls_back_once' }
        elseif ($MixedLocations -and $InjectFailure) { 'Mixed_item_location_receipt_rolls_back_database_failures_atomically' }
        elseif ($MixedLocations -and $EditRace) { 'Mixed_item_location_receipt_rejects_draft_edit_after_planning' }
        elseif ($MixedLocations) { 'Concurrent_mixed_item_location_receipt_posts_and_rolls_back_once' }
        elseif ($MoreLines -and $InjectFailure) { 'Multi_line_multi_pile_receipt_rolls_back_database_failures_atomically' }
        elseif ($MoreLines -and $EditRace) { 'Multi_line_multi_pile_receipt_rejects_draft_edit_after_planning' }
        elseif ($MoreLines) { 'Concurrent_multi_line_multi_pile_receipt_posts_and_rolls_back_once' }
        elseif ($TwoPiles -and $InjectFailure) { 'Two_pile_receipt_rolls_back_database_failures_atomically' }
        elseif ($TwoPiles -and $EditRace) { 'Two_pile_receipt_rejects_draft_edit_after_planning' }
        elseif ($TwoPiles) { 'Concurrent_two_pile_receipt_posts_and_rolls_back_once' }
        elseif ($InjectFailure) { 'Repeated_and_other_items_roll_back_database_failures_atomically' }
        elseif ($EditRace -and $RepeatedAndOther) { 'Repeated_and_other_items_reject_draft_edit_after_planning' }
        elseif ($EditRace -and $SameItem) { 'Same_item_receipt_rejects_draft_edit_after_planning' }
        elseif ($EditRace) { 'Multi_item_receipt_rejects_draft_edit_after_planning' }
        elseif ($RepeatedAndOther) { 'Concurrent_repeated_and_other_items_post_and_rollback_apply_each_line_once' }
        elseif ($SameItem) { 'Concurrent_same_item_post_and_rollback_apply_each_line_once' }
        else { 'Concurrent_multi_item_post_and_rollback_apply_each_line_once' }
    $arguments = @('test', 'ErpWeb.Tests\ErpWeb.Tests.csproj', '--no-restore', '-m:1', '-clp:ErrorsOnly',
        '--filter', "FullyQualifiedName~$testName",
        '--logger', "trx;LogFileName=$resultName", '--results-directory', $resultDirectory)
    if ($NoBuild) { $arguments += '--no-build' }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'The isolated SQL Server test failed.' }
    [xml]$result = Get-Content -LiteralPath (Join-Path $resultDirectory $resultName)
    $counts = $result.TestRun.ResultSummary.Counters
    if ([int]$counts.passed -ne 1 -or [int]$counts.failed -ne 0 -or [int]$counts.notExecuted -ne 0) {
        throw 'The isolated SQL Server test did not pass exactly once.'
    }
}
finally {
    [Environment]::SetEnvironmentVariable('ERPWEB_MULTI_WAC_SQL_TEST_CONNECTION', $originalConnection, 'Process')
    if ($created) {
        if ($scratch -cnotmatch '^Codex_ReplayNeutral_Test_[0-9a-f]{12}$' -or $scratchId -notmatch '^\d+$') {
            throw "Scratch database identity could not be verified; cleanup was not attempted: $scratch"
        }
        $currentId = (& sqlcmd -S $Server -d master -E -b -h -1 -W -Q "SET NOCOUNT ON; SELECT COALESCE(CONVERT(varchar(12), DB_ID(N'$scratch')), 'NULL')" | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $currentId -ne $scratchId) {
            throw "Scratch database identity changed; cleanup was not attempted: $scratch"
        }
        & sqlcmd -S $Server -d master -E -b -Q "ALTER DATABASE [$scratch] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$scratch]"
        if ($LASTEXITCODE -ne 0) { throw "Could not remove scratch database $scratch." }
        $remaining = (& sqlcmd -S $Server -d master -E -b -h -1 -W -Q "SET NOCOUNT ON; SELECT COALESCE(CONVERT(varchar(12), DB_ID(N'$scratch')), 'NULL')" | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $remaining -ne 'NULL') { throw "Could not verify removal of $scratch." }
        Write-Output "Removed isolated scratch database $scratch."
    }
    Pop-Location
}
