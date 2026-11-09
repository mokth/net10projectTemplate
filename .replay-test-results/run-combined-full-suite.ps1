$ErrorActionPreference = 'Stop'
$runDirectory = 'C:\wincom\net10projects\.replay-productionv2-20261104'
Set-Location -LiteralPath $runDirectory
Get-ChildItem Env: | Where-Object {
    $_.Name -like 'ConnectionStrings__*' -or $_.Name -like 'ERPWEB_*_CONNECTION' -or
    $_.Name -eq 'ERPWEB_REQUIRE_SQLSERVER_TESTS'
} | Remove-Item -ErrorAction SilentlyContinue
$suiteExit = -1
try {
    & dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --no-build --no-restore `
        --filter 'Category!=SqlServer&FullyQualifiedName!~SqlServer' `
        --logger 'trx;LogFileName=productionv2-replay-full-combined.trx' `
        --results-directory .replay-test-results --verbosity quiet -clp:ErrorsOnly `
        *> '.replay-test-results/productionv2-replay-full-combined.log'
    $suiteExit = $LASTEXITCODE
}
catch {
    $_ | Out-File -Append -LiteralPath '.replay-test-results/productionv2-replay-full-combined.log'
}
finally {
    Set-Content -LiteralPath '.replay-test-results/productionv2-replay-full-combined.exit' -Value $suiteExit
}
