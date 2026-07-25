# Release Checklist

This checklist is for a future public release. An official release must be
signed and accompanied by hashes; an unsigned local publish is only a test
artifact.

## Prepare

1. Update the single `Version` value in `Directory.Build.props`. The
   application metadata and portable output folder derive from it.
2. Use the candidate-preparation step below without bypassing it. It runs the
   complete quality gate with coverage, rejects known vulnerable packages, and
   writes a deprecated-package report for review.

3. Verify that no credentials, recovery journals, logs, `bin`, `obj`, or
   `dist` files are staged for source control.
4. Confirm that `LICENSE` contains the MIT License and that the copyright year
   and holder remain accurate for the release.

## Prepare an unsigned candidate

```powershell
.\eng\prepare-release.ps1 -Version 0.9.0
```

The script creates an unsigned portable zip, an unsigned-candidate SHA-256
file, a deprecated-package report, and a release-notes draft. The candidate
checksum is only for the unsigned artifact and must never be published as the
official checksum.

The same preparation can be started manually from GitHub Actions. It validates
the requested version against `Directory.Build.props`, then uploads the same
unsigned material as a 14-day Artifact. It does not create a tag or a release.

## Sign, package, and verify

1. Download and extract the unsigned candidate, then sign every shipped `.exe`
   and `.dll` with the organization-approved certificate and timestamp service.
2. Verify every signature with `Get-AuthenticodeSignature`; no item should
   report a status other than `Valid`.
3. Create a new zip from the signed portable directory, then write its
   SHA-256 checksum next to it:

```powershell
$archive = '.\dist\AngelBossKey.Next-<version>-win-x64.zip'
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$([System.IO.Path]::GetFileName($archive))" |
  Set-Content "$archive.sha256" -Encoding ascii
```

4. Run `docs\release-validation.md` on an isolated VM or dedicated test
   machine. Confirm the signed archive extracts, starts without elevation,
   registers no service, and stores settings only under
   `%LocalAppData%\AngelBossKey.Next`.

## Publish notes

Document the exact version, supported Windows versions, SDK/runtime version,
known compatibility limits for independent desktops, recovery behavior, and
security-impacting changes. Do not attach diagnostic logs, recovery files, or
screenshots containing user window content.
