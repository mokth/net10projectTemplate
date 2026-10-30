#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Provisions a durable Data Protection key directory for ErpWeb (operator-controlled).

.PARAMETER Path
  Absolute path outside all publish/deployment directories.

.PARAMETER AppPoolName
  IIS application pool name (not the site name).

.PARAMETER Confirm
  Must be supplied to execute changes.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $Path,

    [Parameter(Mandatory = $true)]
    [string] $AppPoolName,

    [switch] $ConfirmProvision
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $ConfirmProvision) {
    throw "Refusing to run without -ConfirmProvision. Review Path and AppPoolName first."
}

if (-not [System.IO.Path]::IsPathRooted($Path)) {
    throw "Path must be absolute."
}

$fullPath = [System.IO.Path]::GetFullPath($Path)
$poolPrincipal = "IIS AppPool\$AppPoolName"

if ($PSCmdlet.ShouldProcess($fullPath, "Create directory and grant Modify to $poolPrincipal")) {
    if (-not (Test-Path -LiteralPath $fullPath)) {
        New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
        Write-Host "Created: $fullPath"
    } else {
        Write-Host "Directory already exists: $fullPath"
    }

    Write-Host "Current ACL:"
    icacls $fullPath

    icacls $fullPath /grant "${poolPrincipal}:(OI)(CI)M" | Out-Null
    Write-Host "Granted Modify to $poolPrincipal"

    Write-Host "Updated ACL:"
    icacls $fullPath
}

Write-Host ""
Write-Host "Next: verify access under the worker identity, configure DataProtection__KeysPath on IIS, then migrate keys (phase3)."
