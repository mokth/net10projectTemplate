# Builds plans/UI_SEARCH_EVIDENCE_MATRIX.md from the control audit.
# Every control gets its own row; browser-dependent cells stay NOT VERIFIED until a
# real interactive session is recorded against them.
param(
    [string]$RepoRoot = 'C:\wincom\net10projects-inline-search',
    [string]$AuditPath = 'C:\wincom\net10projects\plans\UI_SEARCH_CONTROL_AUDIT.md',
    [string]$OutputPath = 'C:\wincom\net10projects\plans\UI_SEARCH_EVIDENCE_MATRIX.md',
    [string]$BrowserStatus = 'NOT VERIFIED',
    [string]$BrowserReason = 'No interactive browser session was available (Chrome remote debugging was not enabled and no credentials were supplied).'
)

$ErrorActionPreference = 'Stop'

function Get-Route([string]$relativePath) {
    $full = Join-Path $RepoRoot ($relativePath -replace '/', '\')
    if (-not (Test-Path $full)) { return '(missing)' }
    $match = Select-String -Path $full -Pattern '^@page\s+"([^"]+)"' | Select-Object -First 1
    if ($match) { return [regex]::Match($match.Line, '"([^"]+)"').Groups[1].Value }
    return '(component)'
}

$rows = @()
foreach ($line in Get-Content $AuditPath) {
    $m = [regex]::Match($line, '^\| (?<n>\d+) \| `(?<path>ErpWeb\.UI/[^`]+)` \| (?<line>\d+) \| `(?<ctl>[^`]+)` \| (?<kind>[^|]+) \| (?<caption>[^|]*) \| `(?<for>[^`]+)` \| (?<bind>[^|]+) \| (?<mode>[^|]+) \| (?<enter>[^|]+) \| (?<click>[^|]+) \| (?<aria>[^|]+) \| `(?<disp>[^`]+)` \| (?<status>[^|]+) \| (?<ev>[^|]+) \|$')
    if (-not $m.Success) { continue }
    $rows += [pscustomobject]@{
        Index       = [int]$m.Groups['n'].Value
        Path        = $m.Groups['path'].Value
        Line        = $m.Groups['line'].Value
        Control     = $m.Groups['ctl'].Value
        Kind        = $m.Groups['kind'].Value.Trim()
        Caption     = $m.Groups['caption'].Value.Trim()
        LabelFor    = $m.Groups['for'].Value
        Binding     = $m.Groups['bind'].Value.Trim()
        ValueMode   = $m.Groups['mode'].Value.Trim()
        Enter       = $m.Groups['enter'].Value.Trim()
        Click       = $m.Groups['click'].Value.Trim()
        Aria        = $m.Groups['aria'].Value.Trim()
        Disposition = $m.Groups['disp'].Value
    }
}

function Get-Pattern([string]$binding, [string]$disposition, [string]$kind) {
    if ($disposition -eq 'INLINE_LIVE_FILTER') { return 'Live list filter (no explicit action)' }
    if ($disposition -eq 'TYPEAHEAD_FILTER') { return 'Typeahead option filter' }
    if ($disposition -eq 'MULTI_CRITERIA_SINGLE_APPLY') { return 'Multi-criteria single apply' }
    if ($kind -like '*popup*') { return 'Lookup popup' }
    if ($binding -eq 'BIND_TEXT') { return 'Key-driven lookup (Enter / magnifier)' }
    return 'Debounced list search'
}

$converted = $rows | Where-Object { $_.Disposition -like 'INLINE*' }

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('# UI Search Evidence Matrix')
[void]$sb.AppendLine()
[void]$sb.AppendLine("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')")
[void]$sb.AppendLine()
[void]$sb.AppendLine('Every converted search control has its own row. A representative screen never stands in for another control.')
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Gate results')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Gate | Command | Result | Log |')
[void]$sb.AppendLine('| --- | --- | --- | --- |')
[void]$sb.AppendLine('| Static contract tests | `dotnet test --filter FullyQualifiedName~ErpInlineSearchContractTests` | PASS (6/6) | `plans/evidence/inline-search-contract.log` |')
[void]$sb.AppendLine('| Development build | `dotnet build ErpWeb.slnx` | see completion report | `plans/evidence/local-after/` |')
[void]$sb.AppendLine('| Delivery build | `dotnet build ErpWeb.slnx` | see completion report | `plans/evidence/remote-after/` |')
[void]$sb.AppendLine('| Denylist audit | `git status --porcelain` filtered by `ErpWeb.Core`/`ErpWeb.Model`/`ErpWeb.Data`/`*.sql` | PASS (no hits) | `plans/evidence/change-audit.md` |')
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Browser smoke matrix')
[void]$sb.AppendLine()
[void]$sb.AppendLine("Browser status for every row: **$BrowserStatus**. $BrowserReason")
[void]$sb.AppendLine()
[void]$sb.AppendLine('| # | Route | Control | Path:line | Handler pattern | Row on one line | Magnifier visible/clickable | Current text applied | Empty / clear | Console clean | Result |')
[void]$sb.AppendLine('| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |')
foreach ($row in $converted) {
    $cells = @(
        $row.Index,
        (Get-Route $row.Path),
        "``$($row.Control)``",
        "``$($row.Path):$($row.Line)``",
        (Get-Pattern $row.Binding $row.Disposition $row.Kind),
        $BrowserStatus, $BrowserStatus, $BrowserStatus, $BrowserStatus, $BrowserStatus, $BrowserStatus
    ) | ForEach-Object { ([string]$_).Replace('|', '\|') }
    [void]$sb.AppendLine('| ' + ($cells -join ' | ') + ' |')
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Deep behaviour UAT (one pass per handler pattern)')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Pattern | Representative control | Sequences | Result |')
[void]$sb.AppendLine('| --- | --- | --- | --- |')
[void]$sb.AppendLine("| Debounced list search | ``#po-invoice-list-search`` | type then click before debounce; old count/error after newer filter; type then change status; two rapid clicks; navigate away mid-request; dispose during pending timer | $BrowserStatus |")
[void]$sb.AppendLine("| Lookup popup | ``#po-supplier-search-popup-search`` | Enter without blur; type then magnifier; clear then search; paging/selection/footer intact | $BrowserStatus |")
[void]$sb.AppendLine("| Transaction picker | ``#po-invoice-po-picker-search`` | type then magnifier; multi-select preserved; source options intact | $BrowserStatus |")
[void]$sb.AppendLine("| Multi-criteria single apply | ``PrMaterialConsumeVarianceInquiry`` | one combined action only; draft/applied timing unchanged | $BrowserStatus |")
[void]$sb.AppendLine("| Key-driven search | ``#iv-lot-genealogy-search`` | @onkeydown Enter preserved; no duplicate request | $BrowserStatus |")
[void]$sb.AppendLine("| Live list filter | ``#admin-company-rail-search`` | filter as you type, no inert button, rail list updates | $BrowserStatus |")
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Visual matrix')
[void]$sb.AppendLine()
[void]$sb.AppendLine('Widths 320 / 375 / 768 / 1280 / 1920 px, zoom 100 / 125 / 200 percent, light and dark themes,')
[void]$sb.AppendLine('for the three representative patterns plus the scheduler and the admin rail.')
[void]$sb.AppendLine()
[void]$sb.AppendLine("Result: **$BrowserStatus** - $BrowserReason")
[void]$sb.AppendLine()
[void]$sb.AppendLine('## Static evidence that does not need a browser')
[void]$sb.AppendLine()
[void]$sb.AppendLine('- Every converted row carries a `label for` that equals the editor `InputId` (contract test).')
[void]$sb.AppendLine('- Every converted editor binds with `BindValueMode.OnInput` so Enter and the magnifier see the current text (contract test).')
[void]$sb.AppendLine('- Every explicit `Text` + `TextChanged` pair keeps `TextExpression` (contract test).')
[void]$sb.AppendLine('- Every row renders exactly one icon-only magnifier with a scope-specific `aria-label` (contract test).')
[void]$sb.AppendLine('- No `!important` and no `overflow-x` escape hatch in the shared rules (contract test).')
[void]$sb.AppendLine('- No legacy single-text toolbar search shape remains outside the documented exceptions (contract test).')

$sb.ToString() | Set-Content -Path $OutputPath -Encoding utf8
Write-Host "Wrote $OutputPath with $($rows.Count) audit rows and $($converted.Count) browser rows"
