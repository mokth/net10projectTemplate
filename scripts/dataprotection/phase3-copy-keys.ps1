#Requires -Version 5.1
<#
.SYNOPSIS
  Copies all Data Protection key XML files to a durable directory (never deletes source).

.PARAMETER SourceDirectories
  One or more existing key-ring directories. If multiple sources contain conflicting filenames with different hashes, the script aborts.

.PARAMETER DestinationDirectory
  Operator-provisioned durable target (must exist).

.PARAMETER Confirm
  Must be supplied to copy files.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string[]] $SourceDirectories,

    [Parameter(Mandatory = $true)]
    [string] $DestinationDirectory,

    [switch] $ConfirmCopy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $ConfirmCopy) {
    throw "Refusing to run without -ConfirmCopy."
}

if (-not (Test-Path -LiteralPath $DestinationDirectory)) {
    throw "Destination directory does not exist: $DestinationDirectory"
}

$destFull = [System.IO.Path]::GetFullPath($DestinationDirectory)
$fileMap = @{}

foreach ($source in $SourceDirectories) {
    if (-not (Test-Path -LiteralPath $source)) {
        Write-Warning "Skipping missing source: $source"
        continue
    }

    Get-ChildItem -LiteralPath $source -Filter 'key-*.xml' -File | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        $name = $_.Name
        if ($fileMap.ContainsKey($name)) {
            if ($fileMap[$name].Hash -ne $hash) {
                throw "Conflicting key file '$name' in multiple sources with different SHA-256. Reconcile manually; do not blind-merge."
            }
            return
        }

        $fileMap[$name] = [PSCustomObject]@{
            Source = $_.FullName
            Hash   = $hash
        }
    }
}

Write-Host "Files to copy: $($fileMap.Count)"
foreach ($entry in $fileMap.GetEnumerator()) {
    $target = Join-Path $destFull $entry.Key
    Write-Host "  $($entry.Key) sha256=$($entry.Value.Hash)"
    if ($PSCmdlet.ShouldProcess($target, "Copy key file")) {
        Copy-Item -LiteralPath $entry.Value.Source -Destination $target -Force
        $destHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($destHash -ne $entry.Value.Hash) {
            throw "Hash mismatch after copy for $($entry.Key)"
        }
    }
}

Write-Host "Copy complete. Source directories were not modified or deleted."
