param([switch]$GoodsReceipt, [switch]$Concurrent, [switch]$SameDay, [switch]$DisableReplay,
    [switch]$RollbackQuantity, [switch]$RollbackCost, [switch]$IssueDetail, [switch]$Damage,
    [switch]$CrossPile, [switch]$ReversedCost, [switch]$LowerCost, [switch]$Failure, [switch]$EarlierReceipt, [switch]$Edited, [switch]$NeutralEdited, [switch]$QuantityEdited, [switch]$QuantityRounding, [switch]$ReversedOutput, [switch]$PartialOutput, [switch]$FifoGuard, [switch]$Safe, [switch]$FifoNeutral, [switch]$Qa, [switch]$FifoProjection, [switch]$FifoSameTime, [switch]$FifoChangedCost, [switch]$WacEditRace)
$ErrorActionPreference = 'Stop'
if ($RollbackQuantity -and $RollbackCost) { throw 'Choose one rollback case.' }
$replayScratch = 'Codex_ReplayNeutral_Test_' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
if ($replayScratch -cnotmatch '^Codex_ReplayNeutral_Test_[0-9a-f]{12}$') {
    throw 'Unexpected scratch database name.'
}

$existing = & sqlcmd -S '.\SQLEXPRESS' -d master -E -b -h -1 -W -Q "SET NOCOUNT ON; SELECT COALESCE(CONVERT(varchar(12), DB_ID(N'$replayScratch')), 'NULL')"
if ($LASTEXITCODE -ne 0 -or ($existing | Out-String).Trim() -ne 'NULL') {
    throw 'Could not verify that the scratch database name is unused.'
}

$created = $false
$testExit = 1
try {
    & sqlcmd -S '.\SQLEXPRESS' -d master -E -b -Q "CREATE DATABASE [$replayScratch]"
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the scratch database.' }
    $created = $true
    $env:ConnectionStrings__SqlServerTestConnection = "Server=.\SQLEXPRESS;Database=$replayScratch;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
    Remove-Item Env:ERPWEB_REPLAY_ISOLATED_MR_TEST_CONNECTION, Env:ERPWEB_REPLAY_ISOLATED_GR_TEST_CONNECTION, Env:ERPWEB_REPLAY_ISOLATED_RACE_TEST_CONNECTION, Env:ERPWEB_REPLAY_ISOLATED_SAMEDAY_TEST_CONNECTION, Env:ERPWEB_REPLAY_ISOLATED_SAMEDAY_OFF_TEST_CONNECTION, Env:ERPWEB_REPLAY_ISOLATED_ROLLBACK_TEST_CONNECTION, Env:ERPWEB_REPLAY_ISOLATED_DETAIL_TEST_CONNECTION, Env:ERPWEB_REPLAY_ISOLATED_CROSS_PILE_TEST_CONNECTION, Env:ERPWEB_REPLAY_ISOLATED_REVERSED_COST_TEST_CONNECTION -ErrorAction SilentlyContinue
    if ($ReversedCost -or $EarlierReceipt -or $ReversedOutput -or $PartialOutput -or $FifoGuard -or $FifoNeutral -or $FifoProjection -or $FifoSameTime -or $FifoChangedCost -or $WacEditRace) {
        $env:ERPWEB_REPLAY_ISOLATED_REVERSED_COST_TEST_CONNECTION = $env:ConnectionStrings__SqlServerTestConnection
    }
    elseif ($CrossPile) {
        $env:ERPWEB_REPLAY_ISOLATED_CROSS_PILE_TEST_CONNECTION = $env:ConnectionStrings__SqlServerTestConnection
    }
    elseif ($IssueDetail) {
        $env:ERPWEB_REPLAY_ISOLATED_DETAIL_TEST_CONNECTION = $env:ConnectionStrings__SqlServerTestConnection
    }
    elseif ($RollbackQuantity -or $RollbackCost) {
        $env:ERPWEB_REPLAY_ISOLATED_ROLLBACK_TEST_CONNECTION = $env:ConnectionStrings__SqlServerTestConnection
    }
    elseif ($SameDay -and $DisableReplay) {
        $env:ERPWEB_REPLAY_ISOLATED_SAMEDAY_OFF_TEST_CONNECTION = $env:ConnectionStrings__SqlServerTestConnection
    }
    elseif ($SameDay) {
        $env:ERPWEB_REPLAY_ISOLATED_SAMEDAY_TEST_CONNECTION = $env:ConnectionStrings__SqlServerTestConnection
    }
    elseif ($Concurrent) {
        $env:ERPWEB_REPLAY_ISOLATED_RACE_TEST_CONNECTION = $env:ConnectionStrings__SqlServerTestConnection
    }
    elseif ($GoodsReceipt) {
        $env:ERPWEB_REPLAY_ISOLATED_GR_TEST_CONNECTION = $env:ConnectionStrings__SqlServerTestConnection
    }
    else {
        $env:ERPWEB_REPLAY_ISOLATED_MR_TEST_CONNECTION = $env:ConnectionStrings__SqlServerTestConnection
    }
    $env:ERPWEB_REQUIRE_SQLSERVER_TESTS = '1'
    Remove-Item Env:ConnectionStrings__DefaultConnection, Env:ERPWEB_PRODUCTION_STOCK_TEST_CONNECTION -ErrorAction SilentlyContinue
    if ($WacEditRace) {
        $testName = if ($GoodsReceipt) { 'Wac_reversed_issue_goods_receipt_serializes_edit_through_post_and_repost' } else { 'Wac_reversed_issue_receipt_serializes_edit_through_post_and_repost' }
        $resultName = if ($GoodsReceipt) { 'sqlserver-wac-reversed-edit-race-gr.trx' } else { 'sqlserver-wac-reversed-edit-race.trx' }
    }
    elseif ($FifoChangedCost) {
        if ($Edited -and $GoodsReceipt) {
            $testName = 'Fifo_changed_reversed_issue_goods_receipt_reposts_edited_quantity_and_price'
            $resultName = 'sqlserver-fifo-reversed-changed-edited-gr.trx'
        }
        elseif ($Edited -and !$GoodsReceipt) {
            $testName = 'Fifo_changed_reversed_issue_rejects_concurrent_draft_edit'
            $resultName = 'sqlserver-fifo-reversed-changed-edit-race.trx'
        }
        elseif ($Failure -and !$GoodsReceipt) {
            $testName = 'Fifo_changed_reversed_issue_failure_clears_session_and_retries'
            $resultName = 'sqlserver-fifo-reversed-changed-failure.trx'
        }
        elseif ($Concurrent -and !$GoodsReceipt) {
            $testName = 'Concurrent_fifo_changed_reversed_issue_receipt_posts_once'
            $resultName = 'sqlserver-fifo-reversed-changed-concurrent.trx'
        }
        elseif ($Qa -and $GoodsReceipt) {
            $testName = 'Fifo_changed_reversed_issue_goods_receipt_respects_qa_hold'
            $resultName = 'sqlserver-fifo-reversed-changed-qa-gr.trx'
        }
        elseif ($Damage -and !$GoodsReceipt) {
            $testName = 'Fifo_changed_reversed_issue_split_is_immutable'
            $resultName = 'sqlserver-fifo-reversed-changed-immutable.trx'
        }
        elseif ($GoodsReceipt) {
            $testName = 'Fifo_changed_reversed_issue_goods_receipt_posts_and_reposts'
            $resultName = 'sqlserver-fifo-reversed-changed-gr.trx'
        }
        else {
            $testName = 'Fifo_changed_reversed_issue_misc_receipt_posts_and_reposts'
            $resultName = 'sqlserver-fifo-reversed-changed-mr.trx'
        }
    }
    elseif ($FifoSameTime) {
        if ($GoodsReceipt -and $Qa) {
            $testName = 'Fifo_same_time_reversed_issue_goods_receipt_respects_qa_hold'
            $resultName = 'sqlserver-fifo-same-time-qa-gr.trx'
        }
        elseif ($GoodsReceipt -and $FifoGuard) {
            $testName = 'Fifo_same_time_reversed_issue_replays_consumed_goods_receipt'
            $resultName = 'sqlserver-fifo-same-time-block-gr.trx'
        }
        elseif ($FifoGuard) {
            $testName = 'Fifo_same_time_reversed_issue_replays_consumed_misc_receipt'
            $resultName = 'sqlserver-fifo-same-time-block-mr.trx'
        }
        elseif ($GoodsReceipt) {
            $testName = 'Fifo_same_time_reversed_issue_goods_receipt_posts_and_rolls_back'
            $resultName = 'sqlserver-fifo-same-time-gr.trx'
        }
        else {
            $testName = 'Fifo_same_time_reversed_issue_misc_receipt_posts_and_rolls_back'
            $resultName = 'sqlserver-fifo-same-time-mr.trx'
        }
    }
    elseif ($FifoProjection) {
        if ($GoodsReceipt) {
            $testName = 'Fifo_reversed_issue_projects_changed_goods_receipt_cost'
            $resultName = 'sqlserver-fifo-projection-gr.trx'
        }
        else {
            $testName = 'Fifo_reversed_issue_projects_changed_misc_receipt_cost'
            $resultName = 'sqlserver-fifo-projection-mr.trx'
        }
    }
    elseif ($FifoNeutral) {
        if ($QuantityRounding -and $GoodsReceipt) {
            $testName = 'Fifo_neutral_reversed_goods_receipt_accepts_exact_split_rounding'
            $resultName = 'sqlserver-fifo-neutral-rounding-gr.trx'
        }
        elseif ($Edited -and $GoodsReceipt) {
            $testName = 'Fifo_neutral_reversed_goods_receipt_reposts_quantity_and_price_edit'
            $resultName = 'sqlserver-fifo-neutral-edit-gr.trx'
        }
        elseif ($Failure -and !$GoodsReceipt) {
            $testName = 'Fifo_neutral_reversed_receipt_failure_clears_session'
            $resultName = 'sqlserver-fifo-neutral-failure.trx'
        }
        elseif ($Concurrent -and !$GoodsReceipt) {
            $testName = 'Concurrent_fifo_neutral_reversed_receipt_posts_once'
            $resultName = 'sqlserver-fifo-neutral-concurrent.trx'
        }
        elseif ($Qa -and $GoodsReceipt) {
            $testName = 'Fifo_neutral_reversed_goods_receipt_respects_qa_hold'
            $resultName = 'sqlserver-fifo-neutral-qa-gr.trx'
        }
        elseif (!$Failure -and !$Concurrent -and !$Qa -and $GoodsReceipt) {
            $testName = 'Fifo_neutral_reversed_goods_receipt_posts_and_rolls_back'
            $resultName = 'sqlserver-fifo-neutral-gr.trx'
        }
        elseif (!$Failure -and !$Concurrent -and !$Qa -and !$GoodsReceipt) {
            $testName = 'Fifo_neutral_reversed_misc_receipt_posts_and_rolls_back'
            $resultName = 'sqlserver-fifo-neutral-mr.trx'
        }
        else { throw 'This isolated neutral FIFO combination is not configured.' }
    }
    elseif ($FifoGuard) {
        if ($Safe -and !$GoodsReceipt) {
            $testName = 'Fifo_reversed_issue_allows_unconsumed_earlier_receipt_rollback'
            $resultName = 'sqlserver-fifo-reversed-guard-project-safe.trx'
        }
        elseif (!$Safe -and $GoodsReceipt) {
            $testName = 'Fifo_reversed_issue_replays_consumed_goods_receipt_rollback'
            $resultName = 'sqlserver-fifo-reversed-guard-project-gr.trx'
        }
        elseif (!$Safe -and !$GoodsReceipt) {
            $testName = 'Fifo_reversed_issue_replays_consumed_misc_receipt_rollback'
            $resultName = 'sqlserver-fifo-reversed-guard-project-mr.trx'
        }
        else { throw 'This isolated FIFO guard combination is not configured.' }
    }
    elseif ($PartialOutput) {
        if ($Failure -and !$GoodsReceipt -and !$LowerCost) {
            $testName = 'Partial_output_failure_rolls_back_and_clears_session'
            $resultName = 'sqlserver-partial-output-project-failure.trx'
        }
        elseif ($Concurrent -and !$GoodsReceipt -and !$LowerCost) {
            $testName = 'Concurrent_partial_output_receipt_posts_once'
            $resultName = 'sqlserver-partial-output-project-concurrent.trx'
        }
        elseif ($EarlierReceipt -and $Edited -and $GoodsReceipt -and !$LowerCost) {
            $testName = 'Earlier_goods_receipt_with_partial_reversed_output_reposts_price_edits'
            $resultName = 'sqlserver-partial-output-project-earlier-gr.trx'
        }
        elseif (!$EarlierReceipt -and !$GoodsReceipt -and !$LowerCost) {
            $testName = 'Partial_reversed_output_misc_receipt_posts_and_rolls_back'
            $resultName = 'sqlserver-partial-output-project-mr.trx'
        }
        else { throw 'This isolated partial-output combination is not configured.' }
    }
    elseif ($ReversedOutput) {
        if ($EarlierReceipt -and $Failure -and !$GoodsReceipt -and !$LowerCost) {
            $testName = 'Earlier_receipt_output_failure_rolls_back_and_clears_session'
            $resultName = 'sqlserver-earlier-output-failure.trx'
        }
        elseif ($EarlierReceipt -and $Concurrent -and !$GoodsReceipt -and !$LowerCost) {
            $testName = 'Concurrent_earlier_receipt_with_reversed_output_rolls_back_once'
            $resultName = 'sqlserver-earlier-output-concurrent.trx'
        }
        elseif ($EarlierReceipt -and $QuantityEdited -and $GoodsReceipt -and !$LowerCost) {
            $testName = 'Earlier_goods_receipt_with_reversed_output_reposts_quantity_edits'
            $resultName = 'sqlserver-earlier-output-quantity-gr.trx'
        }
        elseif ($EarlierReceipt -and $QuantityEdited -and !$GoodsReceipt -and !$LowerCost) {
            $testName = 'Earlier_misc_receipt_with_reversed_output_reposts_quantity_edits'
            $resultName = 'sqlserver-earlier-output-quantity-mr.trx'
        }
        elseif ($EarlierReceipt -and $Edited -and $GoodsReceipt -and !$LowerCost) {
            $testName = 'Earlier_goods_receipt_with_reversed_output_reposts_price_edits'
            $resultName = 'sqlserver-earlier-output-edited-gr.trx'
        }
        elseif ($EarlierReceipt -and !$GoodsReceipt -and !$LowerCost) {
            $testName = 'Earlier_misc_receipt_with_reversed_output_rolls_back_and_reposts'
            $resultName = 'sqlserver-earlier-output-mr.trx'
        }
        elseif ($EarlierReceipt) { throw 'This isolated earlier-output combination is not configured.' }
        elseif ($Failure -and !$GoodsReceipt -and !$LowerCost) {
            $testName = 'Reversed_issue_and_output_failure_rolls_back_and_clears_session'
            $resultName = 'sqlserver-reversed-output-failure.trx'
        }
        elseif ($Concurrent -and !$GoodsReceipt -and !$LowerCost) {
            $testName = 'Concurrent_reversed_issue_and_output_receipt_posts_once'
            $resultName = 'sqlserver-reversed-output-concurrent.trx'
        }
        elseif ($GoodsReceipt -and $LowerCost) {
            $testName = 'Reversed_issue_and_output_goods_receipt_posts_and_rolls_back'
            $resultName = 'sqlserver-reversed-output-gr.trx'
        }
        elseif (!$GoodsReceipt -and !$LowerCost) {
            $testName = 'Reversed_issue_and_output_misc_receipt_posts_and_rolls_back'
            $resultName = 'sqlserver-reversed-output-mr.trx'
        }
        else { throw 'This isolated reversed-output combination is not configured.' }
    }
    elseif ($EarlierReceipt) {
        if ($QuantityRounding -and $GoodsReceipt) {
            $testName = 'Earlier_goods_receipt_rejects_unreconciled_quantity_rounding'
            $resultName = 'sqlserver-earlier-receipt-quantity-rounding-gr.trx'
        }
        elseif ($QuantityEdited -and $GoodsReceipt) {
            $testName = 'Earlier_goods_receipt_reposts_changed_quantity_and_price'
            $resultName = 'sqlserver-earlier-receipt-quantity-edit-gr.trx'
        }
        elseif ($QuantityEdited -and !$GoodsReceipt) {
            $testName = 'Earlier_misc_receipt_reposts_changed_quantity_and_price'
            $resultName = 'sqlserver-earlier-receipt-quantity-edit-mr.trx'
        }
        elseif ($NeutralEdited -and $GoodsReceipt) {
            $testName = 'Earlier_goods_receipt_reposts_cost_neutral_price_then_changes_again'
            $resultName = 'sqlserver-earlier-receipt-neutral-edit-gr.trx'
        }
        elseif ($NeutralEdited -and !$GoodsReceipt) {
            $testName = 'Earlier_misc_receipt_reposts_cost_neutral_price_then_changes_again'
            $resultName = 'sqlserver-earlier-receipt-neutral-edit-mr.trx'
        }
        elseif ($Edited -and $Failure -and !$GoodsReceipt) {
            $testName = 'Earlier_receipt_edited_repost_failure_clears_session_and_can_retry'
            $resultName = 'sqlserver-earlier-receipt-edited-failure.trx'
        }
        elseif ($Edited -and $GoodsReceipt) {
            $testName = 'Earlier_goods_receipt_corrected_price_reposts_and_rolls_back'
            $resultName = 'sqlserver-earlier-receipt-edited-gr.trx'
        }
        elseif ($Edited -and !$GoodsReceipt) {
            $testName = 'Earlier_misc_receipt_corrected_price_reposts_and_rolls_back'
            $resultName = 'sqlserver-earlier-receipt-edited-mr.trx'
        }
        elseif ($Failure -and !$GoodsReceipt -and !$LowerCost) {
            $testName = 'Earlier_receipt_rollback_failure_clears_sql_session_and_can_retry'
            $resultName = 'sqlserver-earlier-receipt-failure-retry.trx'
        }
        elseif ($Concurrent -and !$GoodsReceipt -and !$LowerCost) {
            $testName = 'Concurrent_earlier_receipt_rollback_writes_only_one_reversal'
            $resultName = 'sqlserver-earlier-receipt-concurrent.trx'
        }
        elseif ($GoodsReceipt -and $LowerCost) {
            $testName = 'Earlier_goods_receipt_rolls_back_and_reposts_after_reversed_issue'
            $resultName = 'sqlserver-earlier-receipt-gr.trx'
        }
        elseif (!$GoodsReceipt -and !$LowerCost) {
            $testName = 'Earlier_misc_receipt_rolls_back_and_reposts_after_reversed_issue'
            $resultName = 'sqlserver-earlier-receipt-mr.trx'
        }
        else { throw 'This isolated earlier-receipt combination is not configured.' }
    }
    elseif ($ReversedCost) {
        if ($Failure -and !$GoodsReceipt -and !$LowerCost) {
            $testName = 'Reversed_issue_post_failure_clears_sql_session_and_can_retry'
            $resultName = 'sqlserver-reversed-issue-failure-retry.trx'
        }
        elseif ($Concurrent -and !$GoodsReceipt -and !$LowerCost) {
            $testName = 'Concurrent_reversed_issue_misc_receipt_posts_only_once'
            $resultName = 'sqlserver-reversed-issue-mr-concurrent.trx'
        }
        elseif ($GoodsReceipt -and $LowerCost) {
            $testName = 'Reversed_issue_goods_receipt_reprices_and_reposts_with_lower_cost'
            $resultName = 'sqlserver-reversed-issue-gr-lower.trx'
        }
        elseif (!$GoodsReceipt -and !$LowerCost) {
            $testName = 'Reversed_issue_misc_receipt_reprices_and_reposts_with_higher_cost'
            $resultName = 'sqlserver-reversed-issue-mr-higher.trx'
        }
        else { throw 'This isolated reversed-cost combination is not configured.' }
    }
    elseif ($CrossPile) {
        if ($GoodsReceipt -and !$DisableReplay) {
            $testName = 'Cross_pile_goods_receipt_rejects_reversed_issue_with_replay_enabled'
            $resultName = 'sqlserver-cross-pile-reversed-post-gr-enabled.trx'
        }
        elseif (!$GoodsReceipt -and $DisableReplay) {
            $testName = 'Cross_pile_misc_receipt_rejects_reversed_issue_with_replay_disabled'
            $resultName = 'sqlserver-cross-pile-reversed-post-mr-disabled.trx'
        }
        else { throw 'This isolated cross-pile combination is not configured.' }
    }
    elseif ($IssueDetail) {
        if ($Damage -and $GoodsReceipt) {
            $testName = 'Issue_detail_rejects_damaged_goods_receipt_cost_on_sql_server'
            $resultName = 'sqlserver-issue-detail-damaged-gr.trx'
        }
        elseif (!$Damage -and !$GoodsReceipt) {
            $testName = 'Issue_detail_shows_replayed_misc_receipt_cost_on_sql_server'
            $resultName = 'sqlserver-issue-detail-replayed-mr.trx'
        }
        else { throw 'This isolated issue-detail combination is not configured.' }
    }
    elseif ($RollbackQuantity -or $RollbackCost) {
        $kind = if ($GoodsReceipt) { 'Goods' } else { 'Misc' }
        $change = if ($RollbackQuantity) { 'quantity' } else { 'cost' }
        $mode = if ($DisableReplay) { 'disabled' } else { 'enabled' }
        $verb = if ($RollbackCost -and $GoodsReceipt -and !$DisableReplay) { 'replays' } else { 'rejects' }
        $testName = "${kind}_receipt_rollback_${verb}_reversed_issue_${change}_with_replay_${mode}"
        $resultName = "sqlserver-reversed-rollback-${kind}-${change}-${mode}.trx"
    }
    else {
        $testName = if ($SameDay -and $DisableReplay) { 'Same_day_issue_rejects_later_restored_stock_with_replay_disabled' } elseif ($SameDay) { 'Same_day_issue_rejects_later_restored_stock_with_replay_enabled' } elseif ($Concurrent) { 'Concurrent_cost_neutral_misc_receipt_posts_only_once_after_issue_reversal' } elseif ($GoodsReceipt) { 'Cost_neutral_backdated_goods_receipt_posts_and_rolls_back_after_issue_reversal' } else { 'Cost_neutral_backdated_misc_receipt_posts_and_rolls_back_after_issue_reversal' }
        $resultName = if ($SameDay -and $DisableReplay) { 'sqlserver-same-day-reversal-off.trx' } elseif ($SameDay) { 'sqlserver-same-day-reversal-on.trx' } elseif ($Concurrent) { 'sqlserver-wac-neutral-reversed-race.trx' } elseif ($GoodsReceipt) { 'sqlserver-wac-neutral-reversed-gr.trx' } else { 'sqlserver-wac-neutral-reversed-mr.trx' }
    }
    dotnet test ErpWeb.Tests\ErpWeb.Tests.csproj --no-build --no-restore -m:1 -clp:ErrorsOnly --filter "FullyQualifiedName~$testName" --logger "trx;LogFileName=$resultName" --results-directory .replay-test-results
    $testExit = $LASTEXITCODE
    if ($testExit -eq 0) {
        [xml]$result = Get-Content -LiteralPath (Join-Path '.replay-test-results' $resultName)
        if ([int]$result.TestRun.ResultSummary.Counters.passed -ne 1 -or [int]$result.TestRun.ResultSummary.Counters.failed -ne 0) {
            throw "The expected SQL test did not pass exactly once: $testName"
        }
    }
}
finally {
    if ($created) {
        & sqlcmd -S '.\SQLEXPRESS' -d master -E -b -Q "ALTER DATABASE [$replayScratch] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$replayScratch]"
        if ($LASTEXITCODE -ne 0) { throw "Could not remove scratch database $replayScratch." }
        $remaining = & sqlcmd -S '.\SQLEXPRESS' -d master -E -b -h -1 -W -Q "SET NOCOUNT ON; SELECT COALESCE(CONVERT(varchar(12), DB_ID(N'$replayScratch')), 'NULL')"
        if ($LASTEXITCODE -ne 0 -or ($remaining | Out-String).Trim() -ne 'NULL') {
            throw "Could not verify removal of scratch database $replayScratch."
        }
        Write-Output "Removed scratch database $replayScratch."
    }
}
exit $testExit
