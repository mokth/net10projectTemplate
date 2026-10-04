<#
.SYNOPSIS
  Run ErpWeb.Tests by ERP module / group namespace, excluding SqlServer by default.

.EXAMPLE
  .\ErpWeb.Tests\run-tests.ps1 Inventory
  .\ErpWeb.Tests\run-tests.ps1 Inventory Transaction
  .\ErpWeb.Tests\run-tests.ps1 Production Transaction -SqlServer
  .\ErpWeb.Tests\run-tests.ps1 -All
#>
[CmdletBinding(DefaultParameterSetName = 'Module')]
param(
    [Parameter(Position = 0, ParameterSetName = 'Module')]
    [string] $Module,

    [Parameter(Position = 1, ParameterSetName = 'Module')]
    [string] $Group,

    [Parameter(ParameterSetName = 'Module')]
    [switch] $SqlServer,

    [Parameter(ParameterSetName = 'All')]
    [switch] $All
)

$ErrorActionPreference = 'Stop'

$supportedModules = @(
    'Inventory', 'Sales', 'Procurement', 'Planning', 'Production', 'Admin', 'Other'
)
$supportedGroups = @('Master', 'Transaction', 'Inquiry')

$project = Join-Path $PSScriptRoot 'ErpWeb.Tests.csproj'

function Show-Usage {
    Write-Host @"
Usage:
  .\run-tests.ps1 <Module> [Group] [-SqlServer]
  .\run-tests.ps1 -All

Modules: $($supportedModules -join ', ')
Groups:  $($supportedGroups -join ', ')  (optional; not used with Other)

Default excludes Category=SqlServer. Pass -SqlServer to include those suites.
"@
}

if ($All) {
    Write-Host "Running full suite: $project"
    & dotnet test $project
    exit $LASTEXITCODE
}

if ([string]::IsNullOrWhiteSpace($Module)) {
    Show-Usage
    exit 1
}

$moduleMatch = $supportedModules | Where-Object { $_.Equals($Module, [StringComparison]::OrdinalIgnoreCase) }
if (-not $moduleMatch) {
    Write-Error "Unsupported module '$Module'."
    Show-Usage
    exit 1
}
$Module = $moduleMatch

$ns = "ErpWeb.Tests.$Module"
if (-not [string]::IsNullOrWhiteSpace($Group)) {
    if ($Module -eq 'Other') {
        Write-Error "Module 'Other' does not accept a Group argument."
        exit 1
    }
    $groupMatch = $supportedGroups | Where-Object { $_.Equals($Group, [StringComparison]::OrdinalIgnoreCase) }
    if (-not $groupMatch) {
        Write-Error "Unsupported group '$Group'."
        Show-Usage
        exit 1
    }
    $Group = $groupMatch
    $ns = "$ns.$Group"
}

$filter = "FullyQualifiedName~$ns"
if (-not $SqlServer) {
    $filter = "$filter&Category!=SqlServer"
}

Write-Host "dotnet test $project --filter `"$filter`""
& dotnet test $project --filter $filter
exit $LASTEXITCODE
