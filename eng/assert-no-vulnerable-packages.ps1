[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projects = @(Get-ChildItem -Path $repositoryRoot -Recurse -File -Filter '*.csproj' |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })

if ($projects.Count -eq 0) {
    throw 'No project files were found.'
}

$hasVulnerabilities = $false
foreach ($project in $projects) {
    $report = & dotnet package list --project $project.FullName `
        --vulnerable --include-transitive --format json --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Package vulnerability scan failed for $($project.FullName)."
    }

    if ($report -match '"vulnerabilities"\s*:\s*\[\s*\{') {
        Write-Host "Known vulnerable packages were found in $($project.FullName)." -ForegroundColor Red
        $report
        $hasVulnerabilities = $true
    }
}

if ($hasVulnerabilities) {
    throw 'Known vulnerable NuGet packages were found.'
}

Write-Host 'No known vulnerable NuGet packages were found.' -ForegroundColor Green
