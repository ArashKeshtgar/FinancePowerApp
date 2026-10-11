<#
.SYNOPSIS
    Brings the FinancePowerApp database up to date: runs each numbered script once, in order, and records it.
.DESCRIPTION
    Works on the existing Power Apps database (005 then finds everything already there and changes nothing) and on
    a new one. Scripts already run are listed in dbo.ErpMigration and skipped.
.EXAMPLE
    ./deploy.ps1                                 # the local FinancePowerApp database
    ./deploy.ps1 -Database FinancePowerApp_Test -Recreate
    ./deploy.ps1 -Server myserver.database.windows.net -Database FinancePowerApp -NoDemo
#>
param(
    [string]$Server = '.',
    [string]$Database = 'FinancePowerApp',
    [switch]$Recreate,
    [switch]$NoDemo
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

function Invoke-Sql([string]$db, [string]$file, [string]$query) {
    $sqlArgs = @('-S', $Server, '-E', '-C', '-d', $db, '-b', '-I', '-f', '65001')
    if ($file) { $sqlArgs += @('-i', $file) } else { $sqlArgs += @('-Q', $query) }
    & sqlcmd @sqlArgs
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed ($db): $file$query" }
}

if ($Recreate) {
    Write-Host "Dropping $Database..."
    Invoke-Sql 'master' $null "IF DB_ID(N'$Database') IS NOT NULL BEGIN ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database]; END"
}
Invoke-Sql 'master' $null "IF DB_ID(N'$Database') IS NULL CREATE DATABASE [$Database]"
Invoke-Sql $Database $null "IF OBJECT_ID(N'dbo.ErpMigration') IS NULL CREATE TABLE dbo.ErpMigration (Script nvarchar(200) NOT NULL PRIMARY KEY, AppliedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME())"

$applied = & sqlcmd -S $Server -E -C -d $Database -h -1 -W -Q "SET NOCOUNT ON; SELECT Script FROM dbo.ErpMigration"
$applied = @($applied | Where-Object { $_ } | ForEach-Object { $_.Trim() })

$scripts = Get-ChildItem $here -Filter '*.sql' | Where-Object { $_.Name -match '^\d{3}_' } | Sort-Object Name
if ($NoDemo) { $scripts = $scripts | Where-Object Name -notlike '*_seed_demo.sql' }

foreach ($s in $scripts) {
    if ($applied -contains $s.Name) { continue }
    Write-Host "Running $($s.Name)..."
    Invoke-Sql $Database $s.FullName $null
    Invoke-Sql $Database $null "INSERT dbo.ErpMigration (Script) VALUES (N'$($s.Name)')"
}
Write-Host "$Database is up to date." -ForegroundColor Green
