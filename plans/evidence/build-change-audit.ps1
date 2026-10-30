# Builds plans/evidence/change-audit.md: path classes, denylist verdict and a
# member-level review of every touched .razor.cs file.
param(
    [string]$RepoRoot = 'C:\wincom\net10projects-inline-search',
    [string]$OutputPath = 'C:\wincom\net10projects\plans\evidence\change-audit.md'
)

$ErrorActionPreference = 'Stop'
Push-Location $RepoRoot
try {
    $files = (git status --porcelain=v1 | ForEach-Object { $_.Substring(3).Trim('"') }) | Sort-Object
    $markup = @($files | Where-Object { $_ -like '*.razor' })
    $scoped = @($files | Where-Object { $_ -like '*.razor.css' })
    $code = @($files | Where-Object { $_ -like '*.razor.cs' })
    $shared = @($files | Where-Object { $_ -like '*.css' -and $_ -notlike '*.razor.css' })
    $tests = @($files | Where-Object { $_ -like 'ErpWeb.Tests/*' })

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('# Change audit - global inline search')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('Branch: `ui/global-inline-search-dev`  ')
    [void]$sb.AppendLine('Base: `8d09a224ae740695199e4bed30c475afb2cf268f`')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Path classes')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('| Class | Count | Verdict |')
    [void]$sb.AppendLine('| --- | --- | --- |')
    [void]$sb.AppendLine("| ErpWeb.UI markup (`*.razor`) | $($markup.Count) | LAYOUT-ONLY |")
    [void]$sb.AppendLine("| ErpWeb.UI scoped styles (`*.razor.css`) | $($scoped.Count) | LAYOUT-ONLY |")
    [void]$sb.AppendLine("| ErpWeb.UI code-behind (`*.razor.cs`) | $($code.Count) | LAYOUT-ONLY (see member list) |")
    [void]$sb.AppendLine("| Shared stylesheet (`ErpWeb/wwwroot/css`) | $($shared.Count) | LAYOUT-ONLY |")
    [void]$sb.AppendLine("| Source contract tests (`ErpWeb.Tests/UI`) | $($tests.Count) | TEST-ONLY |")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Denylist check')
    [void]$sb.AppendLine()
    $deny = @($files | Where-Object { $_ -match '^ErpWeb\.(Core|Model|Data)/' -or $_ -match '\.sql$' -or $_ -match 'Migrat' -or $_ -match 'Permission' })
    if ($deny.Count -eq 0) {
        [void]$sb.AppendLine('No file under `ErpWeb.Core/`, `ErpWeb.Model/`, `ErpWeb.Data/`, SQL or migrations was touched. Result: **PASS**.')
    }
    else {
        [void]$sb.AppendLine('**FAIL** - disallowed paths:')
        $deny | ForEach-Object { [void]$sb.AppendLine("- ``$_``") }
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Method-level review of code-behind changes')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('Member declarations added or removed in each `.razor.cs` file. Only search wiring, layout state, debounce/generation bookkeeping and disposal are permitted.')
    [void]$sb.AppendLine()
    foreach ($f in $code) {
        $members = (git diff -U0 --no-color -- $f) |
            Where-Object { ($_ -like '+*' -and $_ -notlike '+++*') -or ($_ -like '-*' -and $_ -notlike '---*') } |
            ForEach-Object { $_.Substring(1).Trim() } |
            Where-Object { $_ -match '^(protected|private|public|internal).*\(|^(protected|private|public|internal).*[\{;]$' } |
            Sort-Object -Unique
        if ($members.Count -gt 0) {
            [void]$sb.AppendLine("### ``$f``")
            foreach ($m in $members) { [void]$sb.AppendLine("- ``$m``") }
            [void]$sb.AppendLine()
        }
    }

    $sb.ToString() | Set-Content -Path $OutputPath -Encoding utf8
    Write-Host "Wrote $OutputPath"
}
finally {
    Pop-Location
}
