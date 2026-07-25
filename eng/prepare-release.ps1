[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [ValidateSet('Candidate', 'Preview')]
    [string]$Channel = 'Candidate',
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

function Get-ChangelogVersionSection {
    param(
        [Parameter(Mandatory)][string]$Changelog,
        [Parameter(Mandatory)][string]$ReleaseVersion
    )

    $escapedVersion = [regex]::Escape($ReleaseVersion)
    $match = [regex]::Match(
        $Changelog,
        "(?ms)^## \[$escapedVersion\]\s*(.*?)(?=^## \[|\z)"
    )
    if (-not $match.Success) {
        throw "CHANGELOG.md does not contain a section for version '$ReleaseVersion'."
    }

    $section = $match.Groups[1].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($section)) {
        throw "CHANGELOG.md section '$ReleaseVersion' is empty."
    }

    return $section
}

function Get-UnreleasedChangelogSection {
    param([Parameter(Mandatory)][string]$Changelog)

    $match = [regex]::Match(
        $Changelog,
        '(?ms)^## \[Unreleased\]\s*(.*?)(?=^## \[|\z)'
    )
    $section = $match.Groups[1].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($section)) {
        return '- No unreleased changelog entries were found.'
    }

    return $section
}

function Assert-ArchiveContainsFile {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$ExpectedFileName
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq $ExpectedFileName } |
            Select-Object -First 1
        if ($null -eq $entry) {
            throw "Release archive does not contain $ExpectedFileName."
        }
    }
    finally {
        $archive.Dispose()
    }
}

[xml]$versionProps = Get-Content -LiteralPath $versionFile
$sourceVersion = $versionProps.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($Channel -eq 'Preview' -and $Version -notmatch '^\d+\.\d+\.\d+-preview\.\d+$') {
    throw "Preview version '$Version' must match <major>.<minor>.<patch>-preview.<number>."
}
if ($Version -ne $sourceVersion) {
    throw "Requested version '$Version' does not match Directory.Build.props version '$sourceVersion'."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts\release-v$Version"
}

if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Release output directory already exists: $OutputDirectory"
}

$commit = ([string](& git -C $repositoryRoot rev-parse HEAD)).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
    throw 'Could not determine the source commit for release notes.'
}

$publishDirectory = Join-Path $OutputDirectory 'portable'
if ($Channel -eq 'Candidate') {
    $archiveName = "AngelBossKey.Next-v$Version-win-x64-UNSIGNED.zip"
    $releaseWarning = @"
> Prepared from commit ``$commit``. This artifact is unsigned and must not be
> published as an official release.
"@
    $releaseVerification = @"
- Sign every `.exe` and `.dll` in the portable folder with the approved
  certificate and timestamp service.
- Create the final zip after signing and calculate a new SHA-256 checksum.
- Run the [release validation guide](https://github.com/yigenhuobah/AngelBossKey.Next/blob/$commit/docs/release-validation.md)
  on an isolated Windows environment before publishing.
"@
}
else {
    $archiveName = "AngelBossKey.Next-v$Version-win-x64.zip"
    $releaseWarning = @"
> **Unsigned Preview.** This build is not code signed. Windows SmartScreen may
> show a warning. Download only from the official GitHub Releases page and
> verify the attached SHA-256 checksum before starting it.
"@
    $releaseVerification = @"
- Download only from the official GitHub Releases page.
- Verify the attached SHA-256 checksum before extracting or starting the app.
- Run the [release validation guide](https://github.com/yigenhuobah/AngelBossKey.Next/blob/$commit/docs/release-validation.md)
  on a Windows device before reporting a compatibility result.
- Submit only redacted results through the [compatibility report form](https://github.com/yigenhuobah/AngelBossKey.Next/issues/new?template=compatibility_report.yml);
  do not include window titles, executable paths, launch arguments, recovery
  files, raw logs, or screenshots containing user content.
"@
}
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
    Assert-ArchiveContainsFile -ArchivePath $archivePath -ExpectedFileName 'AngelBossKey.Next.exe'

    $checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumLine = "$checksum *$archiveName"
    $checksumLine | Set-Content -LiteralPath $checksumPath -Encoding ascii
    if ((Get-Content -LiteralPath $checksumPath -Raw).Trim() -ne $checksumLine) {
        throw "Release checksum file does not match $archiveName."
    }

    $changelog = Get-Content -LiteralPath $changelogFile -Raw
    if ($Channel -eq 'Preview') {
        $highlights = Get-ChangelogVersionSection -Changelog $changelog -ReleaseVersion $Version
    }
    else {
        $highlights = Get-UnreleasedChangelogSection -Changelog $changelog
    }

@"
# AngelBossKey Next v$Version

$releaseWarning

## Highlights

$highlights

## Compatibility

- Windows 10 22H2 x64 and Windows 11 x64.
- Self-contained .NET 10.0.10 runtime.

## Security and privacy

- Review [SECURITY.md](https://github.com/yigenhuobah/AngelBossKey.Next/blob/$commit/SECURITY.md) for reporting guidance.
- Do not attach diagnostic logs, recovery data, or screenshots containing user
  window content to the release.

## Release verification

$releaseVerification
"@ | Set-Content -LiteralPath $notesPath -Encoding utf8

    Write-Host "$Channel release package prepared: $archivePath" -ForegroundColor Yellow
    Write-Host "$Channel checksum: $checksumPath" -ForegroundColor Yellow
    Write-Host "Release notes draft: $notesPath" -ForegroundColor Yellow
}
finally {
    Pop-Location
}
