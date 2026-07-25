# Release Checklist

There are three distinct release channels. `Candidate` is an internal,
unsigned Artifact and must never be published. An unsigned `Preview` is a
time-limited public prerelease with a ZIP and matching SHA-256 file. A future
stable release must be signed and accompanied by hashes.

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

## Publish an unsigned Preview

Only use this channel for versions matching `<major>.<minor>.<patch>-preview.<number>`.
The Preview package is intentionally unsigned; it is not a substitute for the
signed stable-release process below.

1. Add a non-empty exact version section to `CHANGELOG.md`, update the central
   `Version` in `Directory.Build.props`, and merge those changes into `main`.
2. From the **Publish unsigned Preview** workflow, run `workflow_dispatch` with
   `ref=main` and the exact Preview version. This is a dry run: it runs the
   quality gate, vulnerability check, portable publish, ZIP/SHA-256 checks, and
   uploads an Artifact, but it does not create a Release. Tag resolution and
   tag-to-commit validation happen only after a real tag push.
3. After the dry run succeeds, create and push the matching protected tag, for
   example `v0.9.0-preview.1`, pointing at the current `main` commit.
4. The tag workflow verifies the exact version and that the tag commit is in
   `main`, creates a Draft prerelease, uploads the ZIP, SHA-256, and
   deprecated-package report, then makes it public only when every upload
   succeeds. If it fails after the Draft is created, keep that Draft private
   and rerun the workflow to refresh it.
5. The release notes must retain the unsigned-preview warning: SmartScreen may
   appear; users should download only from official GitHub Releases and verify
   the attached SHA-256 before extracting or running the app.

Do not use GitHub-hosted runners as a desktop GUI certification environment.
Real tray, Explorer, DPI, multi-display, audio, and independent-desktop checks
belong on a maintainer or volunteer Windows device; ask contributors to use the
redacted compatibility-report form rather than upload private window data.

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
