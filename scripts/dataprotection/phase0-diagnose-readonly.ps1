#Requires -Version 5.1
<#
.SYNOPSIS
  Read-only Phase 0 evidence collection for ErpWeb Data Protection key rings (operator-run on IIS host).

.DESCRIPTION
  Collects IIS site/app/pool metadata, lists key filenames and SHA-256 hashes, searches for a target key GUID
  by filename only, and dumps NTFS ACLs. Never reads or prints key XML contents, cookies, or secrets.

.PARAMETER SiteName
  IIS site name (optional; lists all sites if omitted).

.PARAMETER AppPath
  IIS application path under the site, e.g. /erpweb

.PARAMETER KeyRingPaths
  Additional directories to scan for key-*.xml (content-root rings, backups). Repeatable.

.PARAMETER SearchKeyId
  Key GUID to search for in filenames (default: production incident key).

.PARAMETER OutputFile
  Optional path to write the evidence report (UTF-8). Console output is also emitted.

.EXAMPLE
  .\phase0-diagnose-readonly.ps1 -SiteName "Default Web Site" -AppPath "/erpweb" `
    -KeyRingPaths "C:\inetpub\wwwroot\erpweb\DataProtection-Keys" `
    -OutputFile ".\phase0-evidence.txt"
#>
[CmdletBinding()]
param(
    [string] $SiteName,
    [string] $AppPath = "/erpweb",
    [string[]] $KeyRingPaths = @(),
    [Guid] $SearchKeyId = '855d60ca-6640-46b7-9648-59c673a6042e',
    [string] $OutputFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Evidence {
    param([string] $Line)
    Write-Host $Line
    if ($script:Writer) { $script:Writer.WriteLine($Line) }
}

function Get-KeyFileMetadata {
    param([string] $Directory)
    if (-not (Test-Path -LiteralPath $Directory)) {
        return @()
    }
    Get-ChildItem -LiteralPath $Directory -Filter 'key-*.xml' -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            [PSCustomObject]@{
                Directory = $Directory
                FileName  = $_.Name
                Length    = $_.Length
                Sha256    = $hash
                KeyId     = if ($_.BaseName -match '^key-(.+)$') { $Matches[1] } else { $null }
            }
        }
}

if ($OutputFile) {
    $dir = Split-Path -Parent $OutputFile
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $script:Writer = [System.IO.StreamWriter]::new($OutputFile, $false, [System.Text.UTF8Encoding]::new($false))
}

try {
    Write-Evidence "=== ErpWeb Data Protection Phase 0 (read-only) ==="
    Write-Evidence "Timestamp (UTC): $([DateTime]::UtcNow.ToString('o'))"
    Write-Evidence "Computer: $env:COMPUTERNAME"
    Write-Evidence "SearchKeyId: $SearchKeyId"
    Write-Evidence ""

    Import-Module WebAdministration -ErrorAction Stop

    $apps = @()
    if ($SiteName) {
        $apps = Get-WebApplication -Site $SiteName -ErrorAction SilentlyContinue
        if ($AppPath) {
            $apps = $apps | Where-Object { $_.Path -eq $AppPath }
        }
    } else {
        Get-Website | ForEach-Object {
            $site = $_.Name
            Get-WebApplication -Site $site | ForEach-Object {
                $_ | Add-Member -NotePropertyName SiteName -NotePropertyValue $site -Force
                $_
            }
        } | ForEach-Object { $apps += $_ }
    }

    Write-Evidence "--- IIS applications ---"
    foreach ($app in $apps) {
        $site = if ($app.SiteName) { $app.SiteName } else { $SiteName }
        $pool = $app.applicationPool
        $physical = $app.PhysicalPath
        if ($physical -and $physical -match '%\{(.+)\}%') {
            $physical = [Environment]::GetEnvironmentVariable($Matches[1], 'Machine')
            if (-not $physical) { $physical = [Environment]::GetEnvironmentVariable($Matches[1], 'Process') }
        }
        Write-Evidence "Site: $site | AppPath: $($app.Path) | Pool: $pool | PhysicalPath: $physical"

        try {
            $poolConfig = Get-Item "IIS:\AppPools\$pool" -ErrorAction SilentlyContinue
            if ($poolConfig) {
                Write-Evidence "  Pool identity: $($poolConfig.processModel.identityType) / $($poolConfig.processModel.userName)"
                Write-Evidence "  Load User Profile: $($poolConfig.processModel.loadUserProfile)"
                Write-Evidence "  Max processes: $($poolConfig.processModel.maxProcesses)"
            }
        } catch {
            Write-Evidence "  (Could not read app pool settings: $($_.Exception.Message))"
        }

        if ($physical -and (Test-Path -LiteralPath $physical)) {
            $contentKeys = Join-Path $physical 'DataProtection-Keys'
            if ($KeyRingPaths -notcontains $contentKeys) {
                $KeyRingPaths += $contentKeys
            }
        }
    }

    Write-Evidence ""
    Write-Evidence "--- Key ring directories (metadata only) ---"
    $allKeys = @()
    $seenDirs = @{}
    foreach ($path in ($KeyRingPaths | Select-Object -Unique)) {
        if (-not $path) { continue }
        $canonical = try { [System.IO.Path]::GetFullPath($path) } catch { $path }
        if ($seenDirs[$canonical]) { continue }
        $seenDirs[$canonical] = $true

        Write-Evidence "Directory: $canonical"
        if (-not (Test-Path -LiteralPath $canonical)) {
            Write-Evidence "  (missing)"
            continue
        }

        Write-Evidence "  ACL (icacls):"
        $icacls = icacls $canonical 2>&1
        foreach ($line in $icacls) { Write-Evidence "    $line" }

        $meta = Get-KeyFileMetadata -Directory $canonical
        Write-Evidence "  Key file count: $($meta.Count)"
        foreach ($m in $meta) {
            Write-Evidence "    $($m.FileName) | len=$($m.Length) | sha256=$($m.Sha256)"
            $allKeys += $m
        }
    }

    Write-Evidence ""
    Write-Evidence "--- SearchKeyId in filenames ---"
    $matches = $allKeys | Where-Object { $_.KeyId -eq $SearchKeyId.Guid.ToString() }
    if ($matches) {
        Write-Evidence "FOUND_RECOVERABLE_KEY: filename match for $SearchKeyId in:"
        foreach ($m in $matches) { Write-Evidence "  $($m.Directory)\$($m.FileName)" }
    } else {
        Write-Evidence "No filename match for $SearchKeyId in scanned directories (does not prove key is unrecoverable elsewhere)."
    }

    Write-Evidence ""
    Write-Evidence "--- Operator classification (fill in) ---"
    Write-Evidence "Diagnosis: [ FOUND_RECOVERABLE_KEY | KEY_NOT_FOUND | MULTI_INSTANCE_RING_MISMATCH | ACL/IDENTITY_PROBLEM | UNKNOWN ]"
    Write-Evidence "Backup location: ____________________"
    Write-Evidence "Approved durable KeysPath: ____________________"
    Write-Evidence "Rollback: restore DataProtection__KeysPath to previous value; preserve old key folder copy."
}
finally {
    if ($script:Writer) {
        $script:Writer.Dispose()
        $script:Writer = $null
    }
}
