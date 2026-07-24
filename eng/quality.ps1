[CmdletBinding()]
param(
    [switch]$Fix,
    [switch]$SkipRestore,
    [switch]$Coverage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot 'AngelBossKey.Next.slnx'

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    if (-not $SkipRestore) {
        Invoke-DotNet @('restore', $solution)
    }

    $whitespaceArguments = @('format', $solution, 'whitespace', '--no-restore')
    $usingArguments = @(
        'format', $solution, 'style', '--no-restore',
        '--severity', 'info', '--diagnostics', 'IDE0005'
    )
    if (-not $Fix) {
        $whitespaceArguments += '--verify-no-changes'
        $usingArguments += '--verify-no-changes'
    }
    Invoke-DotNet $whitespaceArguments
    Invoke-DotNet $usingArguments

    Invoke-DotNet @('build', $solution, '-c', 'Release', '--no-restore')
    if ($Coverage) {
        $coverageRoot = Join-Path $repositoryRoot (
            'artifacts\coverage-' + [guid]::NewGuid().ToString('N')
        )
        Invoke-DotNet @(
            'test', $solution, '-c', 'Release', '--no-build', '--no-restore',
            '--collect:XPlat Code Coverage', '--results-directory', $coverageRoot
        )

        $report = Get-ChildItem $coverageRoot -Recurse -Filter 'coverage.cobertura.xml' |
            Select-Object -First 1
        if ($null -eq $report) {
            throw 'The coverage collector did not produce a Cobertura report.'
        }

        [xml]$coverageReport = Get-Content -LiteralPath $report.FullName
        $lineRate = [double]$coverageReport.coverage.'line-rate'
        $branchRate = [double]$coverageReport.coverage.'branch-rate'
        Write-Host (
            'Coverage: lines {0:P2}; branches {1:P2}; report {2}' -f
            $lineRate, $branchRate, $report.FullName
        )
    }
    else {
        Invoke-DotNet @('test', $solution, '-c', 'Release', '--no-build', '--no-restore')
    }

    Write-Host 'Quality checks passed.' -ForegroundColor Green
}
finally {
    Pop-Location
}
