param(
    [string]$Server = '.\SQLEXPRESS',
    [switch]$NoBuild,
    [switch]$SameItem,
    [switch]$RepeatedAndOther,
    [switch]$TwoPiles,
    [switch]$MoreLines,
    [switch]$EditRace,
    [switch]$InjectFailure
)

$ErrorActionPreference = 'Stop'
if ($SameItem -and $RepeatedAndOther) { throw 'Choose one receipt shape.' }
if ($TwoPiles -and ($SameItem -or $RepeatedAndOther)) { throw 'Two-pile tests use their own receipt shape.' }
if ($MoreLines -and -not $TwoPiles) { throw 'The multi-line case requires -TwoPiles.' }
if ($InjectFailure -and ($EditRace -or -not ($RepeatedAndOther -or $TwoPiles))) { throw 'Choose a receipt shape for failure injection.' }
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
    $testName = if ($MoreLines -and $InjectFailure) { 'Multi_line_multi_pile_receipt_rolls_back_database_failures_atomically' }
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
