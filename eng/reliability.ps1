[CmdletBinding()]
param(
    [ValidateRange(1, 4096)]
    [int]$Seeds = 256,

    [ValidateRange(1, 100000)]
    [int]$Steps = 500,

    [ValidateRange(1, 10000)]
    [int]$WindowCycles = 1000,

    [int]$BaseSeed = 4301281,

    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repositoryRoot 'AngelBossKey.Next.Tests\AngelBossKey.Next.Tests.csproj'
$resultDirectory = Join-Path $repositoryRoot (
    'artifacts\reliability\' +
    (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' +
    [guid]::NewGuid().ToString('N').Substring(0, 8)
)
$environment = @{
    ANGEL_RELIABILITY_SEEDS = $Seeds.ToString([Globalization.CultureInfo]::InvariantCulture)
    ANGEL_RELIABILITY_STEPS = $Steps.ToString([Globalization.CultureInfo]::InvariantCulture)
    ANGEL_RELIABILITY_BASE_SEED = $BaseSeed.ToString([Globalization.CultureInfo]::InvariantCulture)
    ANGEL_WINDOW_VISIBILITY_CYCLES = $WindowCycles.ToString([Globalization.CultureInfo]::InvariantCulture)
    ANGEL_RELIABILITY_OUTPUT = $resultDirectory
}
$previousEnvironment = @{}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
@{
    Seeds = $Seeds
    Steps = $Steps
    WindowCycles = $WindowCycles
    BaseSeed = $BaseSeed
    StartedAtUtc = (Get-Date).ToUniversalTime().ToString('O')
} |
    ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $resultDirectory 'run.json') -Encoding utf8
foreach ($name in $environment.Keys) {
    $existing = Get-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
    $previousEnvironment[$name] = if ($null -eq $existing) { $null } else { $existing.Value }
    Set-Item -LiteralPath "Env:$name" -Value $environment[$name]
}

Push-Location $repositoryRoot
try {
    if (-not $SkipRestore) {
        Invoke-DotNet @('restore', $testProject)
    }

    Invoke-DotNet @(
        'test', $testProject,
        '--configuration', 'Release',
        '--no-restore',
        '--filter', 'Category=Reliability',
        '--logger', 'trx;LogFileName=reliability.trx',
        '--results-directory', $resultDirectory
    )

    Write-Host (
        'Reliability checks passed: seeds={0}; steps={1}; window-cycles={2}; results={3}' -f
        $Seeds, $Steps, $WindowCycles, $resultDirectory
    ) -ForegroundColor Green
}
finally {
    Pop-Location
    foreach ($name in $previousEnvironment.Keys) {
        if ($null -eq $previousEnvironment[$name]) {
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }
        else {
            Set-Item -LiteralPath "Env:$name" -Value $previousEnvironment[$name]
        }
    }
}
