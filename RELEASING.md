# Release Checklist

This checklist is for a future public release. An official release must be
signed and accompanied by hashes; an unsigned local publish is only a test
artifact.

## Prepare

1. Update the application version in `AngelBossKey.Next.App.csproj` and the
   portable output folder in `Properties\PublishProfiles\Portable.pubxml`.
2. Run the complete gate and review the results:

   ```powershell
   .\eng\quality.ps1 -Coverage
   .\eng\assert-no-vulnerable-packages.ps1
   Get-ChildItem -Recurse -Filter *.csproj | ForEach-Object {
     dotnet package list --project $_.FullName --deprecated --include-transitive
   }
   ```

3. Verify that no credentials, recovery journals, logs, `bin`, `obj`, or
   `dist` files are staged for source control.
4. Confirm that `LICENSE` contains the MIT License and that the copyright year
   and holder remain accurate for the release.

## Build and sign

```powershell
dotnet publish .\AngelBossKey.Next.App\AngelBossKey.Next.App.csproj -p:PublishProfile=Portable
```

Use the organization-approved code-signing certificate and timestamp service to
sign every shipped `.exe` and `.dll`. Verify each signature with
`Get-AuthenticodeSignature`; no item should report a status other than `Valid`.

## Package and verify

Create a zip from the signed publish directory and generate a SHA-256 checksum:

```powershell
Get-FileHash .\dist\AngelBossKey.Next-<version>-win-x64.zip -Algorithm SHA256
```

Publish the checksum next to the release asset. Confirm the published archive
extracts, starts without elevation, registers no service, and stores settings
only under `%LocalAppData%\AngelBossKey.Next`.

## Publish notes

Document the exact version, supported Windows versions, SDK/runtime version,
known compatibility limits for independent desktops, recovery behavior, and
security-impacting changes. Do not attach diagnostic logs, recovery files, or
screenshots containing user window content.
