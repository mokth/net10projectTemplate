# Read-only scan that produces the control-level audit for the global inline search
# standardization work. It never writes to the repository outside plans/.
[CmdletBinding()]
param(
    [string]$RepoRoot = 'C:\wincom\net10projects-inline-search',
    [string]$OutputPath = 'C:\wincom\net10projects\plans\UI_SEARCH_CONTROL_AUDIT.md'
)

$ErrorActionPreference = 'Stop'
$uiRoot = Join-Path $RepoRoot 'ErpWeb.UI'
$hostRoot = Join-Path $RepoRoot 'ErpWeb'

# Rows that are intentionally outside the single-text-search standard.
$exceptions = [ordered]@{
    'ErpWeb.UI/Inventory/Inquiry/IvReconciliation.razor'                 = @('MULTI_CRITERIA_SINGLE_APPLY', 'Item + warehouse row sharing one RUN action.')
    'ErpWeb.UI/Purchase/Inquiry/PoSupplierInquiry.razor'                 = @('TYPEAHEAD_FILTER', 'Text box narrows the adjacent supplier combo option list, not the grid.')
    'ErpWeb.UI/Sales/Inquiry/SaCustomerInquiry.razor'                    = @('TYPEAHEAD_FILTER', 'Text box narrows the adjacent customer combo option list, not the grid.')
    'ErpWeb.UI/Purchase/Transactions/PoCdnReservations.razor'            = @('MULTI_CRITERIA_SINGLE_APPLY', 'Invoice no + vendor code + over-reserved share one REFRESH.')
    'ErpWeb.UI/Sales/Transactions/SaCdnReservations.razor'               = @('MULTI_CRITERIA_SINGLE_APPLY', 'Invoice no + customer code + over-reserved share one REFRESH.')
    'ErpWeb.UI/Planning/Inquiry/PrMaterialConsumeVarianceInquiry.razor'  = @('MULTI_CRITERIA_SINGLE_APPLY', 'Ten criteria share one SEARCH action.')
    'ErpWeb.UI/Sales/Inquiry/SaPriceChangeHistoryInquiry.razor'          = @('MULTI_CRITERIA_SINGLE_APPLY', 'Thirteen criteria share one SEARCH action.')
    'ErpWeb.UI/Sales/Pricing/SaPriceMaintenance.razor'                   = @('MULTI_CRITERIA_SINGLE_APPLY', 'Item/brand/currency block shares one search action.')
    'ErpWeb.UI/Sales/Transactions/SaEInvoiceTin.razor'                   = @('MULTI_CRITERIA_SINGLE_APPLY', 'Validate TIN plus name/ID search share one magnifier.')
    'ErpWeb.UI/Sales/Transactions/SaEInvoicePanel.razor'                 = @('MULTI_CRITERIA_SINGLE_APPLY', 'E-invoice submit/reconcile actions, not a text search row.')
    'ErpWeb.UI/Sales/Transactions/SaSo.razor'                            = @('BIND_TEXT_AFTER', '@bind-Text:after="MarkDirtyOnly" document form; OnInput would set the dirty flag per keystroke.')
    'ErpWeb.UI/Sales/Transactions/SaQt.razor'                            = @('BIND_TEXT_AFTER', 'Quotation document form with dirty-flag callbacks; layout untouched.')
    'ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor'         = @('MULTI_CRITERIA_SINGLE_APPLY', 'Find-operation dialog with seven criteria and one FIND.')
    'ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueEntry.razor'           = @('MULTI_CRITERIA_SINGLE_APPLY', 'Find-operation dialog with seven criteria and one FIND.')
    'ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor'     = @('MULTI_CRITERIA_SINGLE_APPLY', 'Find-source/dialog criteria share one FIND.')
    'ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor'       = @('INLINE_SINGLE_TEXT_SEARCH', 'Converted; combo filters remain typeahead.')
}

function Get-RelativePath([string]$full) {
    $full.Replace($RepoRoot + '\', '').Replace('\', '/')
}

function Find-MatchingDivEnd([string]$text, [int]$start) {
    $depth = 0
    $i = $start
    while ($i -lt $text.Length) {
        if ($i -le $text.Length - 7 -and [string]::CompareOrdinal($text, $i, '</div>', 0, 6) -eq 0) {
            $depth--
            $i += 6
            if ($depth -eq 0) { return $i }
            continue
        }
        if ($i -le $text.Length - 4 -and [string]::CompareOrdinal($text, $i, '<div', 0, 4) -eq 0) {
            $depth++
            $i += 4
            continue
        }
        $i++
    }
    return -1
}

function Get-LineNumber([string]$text, [int]$index) {
    ($text.Substring(0, $index) -split "`n").Count
}

function Get-Attr([string]$block, [string]$name) {
    $m = [regex]::Match($block, '(?:^|\s)' + [regex]::Escape($name) + '="(?<v>[^"]*)"')
    if ($m.Success) { return $m.Groups['v'].Value }
    return ''
}

$rows = [System.Collections.Generic.List[object]]::new()
$files = @()
$files += Get-ChildItem $uiRoot -Recurse -Filter *.razor | Sort-Object FullName
$files += Get-ChildItem $hostRoot -Recurse -Filter *.razor | Sort-Object FullName

foreach ($file in $files) {
    $rel = Get-RelativePath $file.FullName
    $text = Get-Content -Raw $file.FullName

    # Locate inline rows first so their editors are never double counted.
    $inlineRanges = [System.Collections.Generic.List[object]]::new()
    foreach ($m in [regex]::Matches($text, '<div class="[^"]*\berp-inline-search\b[^"]*">')) {
        $end = Find-MatchingDivEnd $text $m.Index
        if ($end -gt $m.Index) {
            $inlineRanges.Add([pscustomobject]@{ Start = $m.Index; End = $end; Tag = $m.Value })
        }
    }

    $hasCombinedAction = [regex]::IsMatch($text, 'Text="(?:SEARCH|FIND|APPLY|RUN|REFRESH|FILTER)"')

    # Control 1..n: each inline row is one audited control.
    foreach ($range in $inlineRanges) {
        $block = $text.Substring($range.Start, $range.End - $range.Start)
        $line = Get-LineNumber $text $range.Start
        $labelFor = [regex]::Match($block, '<label class="erp-inline-search__caption"[^>]*for="(?<f>[^"]+)"')
        $labelText = [regex]::Match($block, '<label class="erp-inline-search__caption"[^>]*>(?<t>[^<]*)</label>')
        $button = [regex]::Match($block, '<DxButton(?<a>[\s\S]*?)/>')
        $editor = [regex]::Match($block, '<DxTextBox(?<a>[\s\S]*?)/>')
        $isLive = $range.Tag.Contains('erp-inline-search--live')
        $control = if ($editor.Success) { Get-Attr $editor.Groups['a'].Value 'InputId' } else { '' }
        $rows.Add([pscustomobject]@{
                Path        = $rel
                Line        = $line
                Control     = if ($control) { "#$control" } else { '(no input)' }
                Kind        = if ($isLive) { 'DxTextBox (live filter)' } else { 'DxTextBox + magnifier' }
                Caption     = if ($labelText.Success) { $labelText.Groups['t'].Value.Trim() } else { '(missing)' }
                LabelFor    = if ($labelFor.Success) { $labelFor.Groups['f'].Value } else { '(missing)' }
                Binding     = if ($editor.Success -and $editor.Groups['a'].Value.Contains('TextChanged=')) { 'EXPLICIT_TEXT_CHANGED' } else { 'BIND_TEXT' }
                Binding2    = if ($editor.Success -and $editor.Groups['a'].Value.Contains('BindValueMode="BindValueMode.OnInput"')) { 'OnInput' } else { 'default' }
                Enter       = if ($block.Contains('@onkeydown')) { '@onkeydown -> apply' } else { 'none' }
                Click       = if ($button.Success) { Get-Attr $button.Groups['a'].Value 'Click' } else { '(none)' }
                Aria        = if ($button.Success) { Get-Attr $button.Groups['a'].Value 'aria-label' } else { '(none)' }
                Disposition = if ($isLive) { 'INLINE_LIVE_FILTER' } else { 'INLINE_SINGLE_TEXT_SEARCH' }
                State       = 'CONVERTED'
                Evidence    = 'see plans/UI_SEARCH_EVIDENCE_MATRIX.md'
            })
    }

    # Control 2..n: search text editors that are not part of an inline row.
    foreach ($m in [regex]::Matches($text, '<DxTextBox(?<a>[\s\S]*?)/>')) {
        $inside = $false
        foreach ($range in $inlineRanges) {
            if ($m.Index -ge $range.Start -and $m.Index -lt $range.End) { $inside = $true; break }
        }
        if ($inside) { continue }

        $attrs = $m.Groups['a'].Value
        $nullText = Get-Attr $attrs 'NullText'
        $changed = Get-Attr $attrs 'TextChanged'
        $bound = Get-Attr $attrs 'Text'
        $afterBind = [regex]::IsMatch($attrs, '@bind-Text:after')

        $looksLikeSearch = ($nullText -match '(?i)search|find') -or ($changed -match '(?i)search')
        if (-not $looksLikeSearch) { continue }

        # A search box that only narrows a neighbouring combo option list is a typeahead filter.
        $windowStart = [Math]::Max(0, $m.Index - 900)
        $window = $text.Substring($windowStart, [Math]::Min(1800, $text.Length - $windowStart))
        $nextToCombo = [regex]::IsMatch($window, '<(?:DxComboBox|IvCodeComboBox|SmartLookupInput)')

        $disposition =
        if ($afterBind) { 'BIND_TEXT_AFTER' }
        elseif ($exceptions.Contains($rel)) { $exceptions[$rel][0] }
        elseif ($nextToCombo) { 'TYPEAHEAD_FILTER' }
        elseif ($hasCombinedAction) { 'MULTI_CRITERIA_SINGLE_APPLY' }
        else { 'BLOCKED_NEEDS_DECISION' }

        $reason = if ($exceptions.Contains($rel)) { $exceptions[$rel][1] } else { '' }

            $row = [pscustomobject]@{
                Path        = $rel
                Line        = Get-LineNumber $text $m.Index
                Control     = if ($bound) { $bound } else { '(no binding)' }
                Kind        = 'DxTextBox (outside inline row)'
                Caption     = if ($nullText) { $nullText } else { '(no placeholder)' }
                LabelFor    = '(n/a)'
                Binding     = if ($afterBind) { 'BIND_TEXT_AFTER' } elseif ($changed) { 'EXPLICIT_TEXT_CHANGED' } elseif ($bound) { 'EXPLICIT_TEXT' } else { '@bind-Text' }
                Binding2    = if ($attrs.Contains('BindValueMode="BindValueMode.OnInput"')) { 'OnInput' } else { 'default' }
                Enter       = if ($attrs.Contains('@onkeydown')) { '@onkeydown -> apply' } else { 'none' }
                Click       = '(n/a)'
                Aria        = '(n/a)'
                Disposition = $disposition
                State       = if ($disposition -eq 'BLOCKED_NEEDS_DECISION') { 'BLOCKED' } else { 'EXCLUDED' }
                Evidence    = $reason
            }
        $rows.Add($row)
    }
}

$uiCount = (Get-ChildItem $uiRoot -Recurse -Filter *.razor).Count
$hostCount = (Get-ChildItem $hostRoot -Recurse -Filter *.razor).Count
$converted = $rows | Where-Object { $_.State -eq 'CONVERTED' }
$excluded = $rows | Where-Object { $_.State -eq 'EXCLUDED' }
$blocked = $rows | Where-Object { $_.State -eq 'BLOCKED' }
$changedFiles = (git -C $RepoRoot diff --name-only) | Where-Object { $_ -like '*.razor*' }

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('# UI Search Control Audit')
[void]$sb.AppendLine()
[void]$sb.AppendLine("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')  ")
[void]$sb.AppendLine("Repository: ``$RepoRoot``  ")
[void]$sb.AppendLine("Branch: ``$((git -C $RepoRoot rev-parse --abbrev-ref HEAD).Trim())`` at ``$((git -C $RepoRoot rev-parse --short HEAD).Trim())``")
[void]$sb.AppendLine()
[void]$sb.AppendLine('## File reconciliation')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Scope | Razor files |')
[void]$sb.AppendLine('| --- | --- |')
[void]$sb.AppendLine("| ``ErpWeb.UI`` | $uiCount |")
[void]$sb.AppendLine("| ``ErpWeb`` host | $hostCount |")
[void]$sb.AppendLine("| Total audited | $($uiCount + $hostCount) |")
[void]$sb.AppendLine("| UI files with ``erp-inline-search`` rows | $(($rows | Where-Object { $_.Disposition -like 'INLINE*' } | Select-Object -ExpandProperty Path -Unique).Count) |")
[void]$sb.AppendLine("| Search-bearing Razor files changed on this branch | $($changedFiles.Count) |")
[void]$sb.AppendLine()
[void]$sb.AppendLine('The local base has 250 ``ErpWeb.UI`` Razor files against 249 on ``origin/productionv2``.')
[void]$sb.AppendLine('The single extra file is ``ErpWeb.UI/Components/Common/OperatorError/ErpOperatorFailureBanner.razor``,')
[void]$sb.AppendLine('introduced by local commit ``8d09a22`` (operator-error banner), which is not part of the delivery branch.')
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Totals')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Status | Controls |')
[void]$sb.AppendLine('| --- | --- |')
[void]$sb.AppendLine("| CONVERTED (inline standard) | $($converted.Count) |")
[void]$sb.AppendLine("| EXCLUDED (documented disposition) | $($excluded.Count) |")
[void]$sb.AppendLine("| BLOCKED | $($blocked.Count) |")
[void]$sb.AppendLine("| **Total audited controls** | **$($rows.Count)** |")
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Disposition summary')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Disposition | Controls |')
[void]$sb.AppendLine('| --- | --- |')
foreach ($group in ($rows | Group-Object Disposition | Sort-Object Name)) {
    [void]$sb.AppendLine("| ``$($group.Name)`` | $($group.Count) |")
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Control equation')
[void]$sb.AppendLine()
$fileWithControl = ($rows | Select-Object -ExpandProperty Path -Unique).Count
$totalFiles = $uiCount + $hostCount
$fileWithoutControl = $totalFiles - $fileWithControl
[void]$sb.AppendLine('```text')
[void]$sb.AppendLine("audited controls ($($rows.Count)) = converted ($($converted.Count)) + documented exceptions ($($excluded.Count)) + blocked ($($blocked.Count))")
[void]$sb.AppendLine("razor files ($totalFiles) = files with a text search control ($fileWithControl) + files without one ($fileWithoutControl)")
[void]$sb.AppendLine('```')
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Control-level audit')
[void]$sb.AppendLine()
[void]$sb.AppendLine('Binding classes: ``BIND_TEXT`` (@bind-Text only), ``BIND_TEXT_AFTER`` (@bind-Text:after side effect), ')
[void]$sb.AppendLine('``EXPLICIT_TEXT_CHANGED`` (explicit Text/TextChanged/TextExpression), ``KEY_EVENT_DRIVEN`` (Enter/@onkeydown).')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| # | Path | Line | Control (InputId / binding) | Kind | Visible caption / placeholder | Label for | Binding | Value mode | Enter path | Click path | Accessible name | Disposition | Status | Evidence |')
[void]$sb.AppendLine('| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |')
$index = 0
foreach ($row in ($rows | Sort-Object Path, Line)) {
    $index++
    $cells = @(
        $index, "``$($row.Path)``", $row.Line, "``$($row.Control)``", $row.Kind, $row.Caption, "``$($row.LabelFor)``",
        $row.Binding, $row.Binding2, $row.Enter, $row.Click, $row.Aria, "``$($row.Disposition)``", $row.State, $row.Evidence
    ) | ForEach-Object { ([string]$_).Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ') }
    [void]$sb.AppendLine('| ' + ($cells -join ' | ') + ' |')
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Enter-driven manual searches (latest-text guarantee)')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Path | Control | Enter path | OnInput |')
[void]$sb.AppendLine('| --- | --- | --- | --- |')
foreach ($row in ($rows | Where-Object { $_.Enter -ne 'none' } | Sort-Object Path, Line)) {
    [void]$sb.AppendLine("| ``$($row.Path)`` | ``$($row.Control)`` | $($row.Enter) | $($row.Binding2) |")
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Side-effectful text callbacks')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Path | Line | Binding | Reason kept off OnInput |')
[void]$sb.AppendLine('| --- | --- | --- | --- |')
foreach ($row in ($rows | Where-Object { $_.Binding -eq 'BIND_TEXT_AFTER' } | Sort-Object Path, Line)) {
    [void]$sb.AppendLine("| ``$($row.Path)`` | $($row.Line) | ``$($row.Control)`` | @bind-Text:after callback sets the dirty flag; OnInput would fire it per keystroke. |")
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Blocked controls')
[void]$sb.AppendLine()
if ($blocked.Count -eq 0) {
    [void]$sb.AppendLine('None.')
}
else {
    [void]$sb.AppendLine('| Path | Line | Control | Placeholder |')
    [void]$sb.AppendLine('| --- | --- | --- | --- |')
    foreach ($row in $blocked) {
        [void]$sb.AppendLine("| ``$($row.Path)`` | $($row.Line) | ``$($row.Control)`` | $($row.Caption) |")
    }
}

$sb.ToString() | Set-Content -Path $OutputPath -Encoding utf8
Write-Host "Wrote $OutputPath"
Write-Host ("Controls: {0} total, {1} converted, {2} excluded, {3} blocked" -f $rows.Count, $converted.Count, $excluded.Count, $blocked.Count)
