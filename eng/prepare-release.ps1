[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $repositoryRoot 'Directory.Build.props'
$changelogFile = Join-Path $repositoryRoot 'CHANGELOG.md'
$appProject = Join-Path $repositoryRoot 'AngelBossKey.Next.App\AngelBossKey.Next.App.csproj'

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

[xml]$versionProps = Get-Content -LiteralPath $versionFile
$sourceVersion = $versionProps.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($Version -ne $sourceVersion) {
    throw "Requested version '$Version' does not match Directory.Build.props version '$sourceVersion'."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts\release-v$Version"
}

if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Release output directory already exists: $OutputDirectory"
}

$publishDirectory = Join-Path $OutputDirectory 'portable'
$archiveName = "AngelBossKey.Next-v$Version-win-x64-UNSIGNED.zip"
$archivePath = Join-Path $OutputDirectory $archiveName
$checksumPath = "$archivePath.sha256"
$notesPath = Join-Path $OutputDirectory 'release-notes.md'
$deprecationPath = Join-Path $OutputDirectory 'deprecated-packages.txt'

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Push-Location $repositoryRoot
try {
    & (Join-Path $PSScriptRoot 'quality.ps1') -Coverage
    & (Join-Path $PSScriptRoot 'assert-no-vulnerable-packages.ps1')

    $projects = @(Get-ChildItem -Path $repositoryRoot -Recurse -File -Filter '*.csproj' |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    $deprecatedReports = foreach ($project in $projects) {
        "# $($project.FullName)"
        & dotnet package list --project $project.FullName `
            --deprecated --include-transitive --format json --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "Deprecated package check failed for $($project.FullName)."
        }
        ''
    }
    Set-Content -LiteralPath $deprecationPath -Value $deprecatedReports -Encoding utf8

    Invoke-DotNet @(
        'publish', $appProject, '--configuration', 'Release',
        '-p:PublishProfile=Portable', "-p:PublishDir=$publishDirectory"
    )

    $executable = Join-Path $publishDirectory 'AngelBossKey.Next.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Portable publish did not produce $executable."
    }

    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath
    $checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$checksum *$archiveName" | Set-Content -LiteralPath $checksumPath -Encoding ascii

    $commit = ([string](& git rev-parse HEAD)).Trim()
    $changelog = Get-Content -LiteralPath $changelogFile -Raw
    $unreleased = [regex]::Match(
        $changelog,
        '(?ms)^## \[Unreleased\]\s*(.*?)(?=^## \[|\z)'
    ).Groups[1].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($unreleased)) {
        $unreleased = '- No unreleased changelog entries were found.'
    }

    @"
# AngelBossKey Next v$Version

> Prepared from commit ``$commit``. This artifact is unsigned and must not be
> published as an official release.

## Highlights

$unreleased

## Compatibility

- Windows 10 22H2 x64 and Windows 11 x64.
- Self-contained .NET 10.0.10 runtime.

## Security and privacy

- Review [SECURITY.md](https://github.com/yigenhuobah/AngelBossKey.Next/blob/$commit/SECURITY.md) for reporting guidance.
- Do not attach diagnostic logs, recovery data, or screenshots containing user
  window content to the release.

## Release verification

- Sign every `.exe` and `.dll` in the portable folder with the approved
  certificate and timestamp service.
- Create the final zip after signing and calculate a new SHA-256 checksum.
- Run the [release validation guide](https://github.com/yigenhuobah/AngelBossKey.Next/blob/$commit/docs/release-validation.md)
  on an isolated Windows environment before publishing.
"@ | Set-Content -LiteralPath $notesPath -Encoding utf8

    Write-Host "Unsigned release candidate prepared: $archivePath" -ForegroundColor Yellow
    Write-Host "Candidate checksum: $checksumPath" -ForegroundColor Yellow
    Write-Host "Release notes draft: $notesPath" -ForegroundColor Yellow
}
finally {
    Pop-Location
}
