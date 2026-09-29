# Releasing MeshVenes

This is the project-specific operator guide for building, verifying, releasing, and staging MeshVenes. It does not itself authorize a commit, push, pull request, tag, GitHub Release, staging change, or public publication. Each operation requires matching explicit scope.

The active global venes.org instructions own full-tree preview, destructive-change review, public publication, and public verification. This guide owns the MeshVenes build products and the checks required before local web staging.

## Release boundaries

Treat these as separate operations:

1. Restore, build, and test.
2. Prepare and verify an unsigned release artifact.
3. Commit and push release source changes, if explicitly requested.
4. Create and push a tag, if explicitly requested.
5. Create or update a GitHub Release, if explicitly requested.
6. Stage approved files under `H:\Koding\Venes.org\meshvenes`, if explicitly requested.
7. Publish the staged venes.org tree, if explicitly requested, using the active global publication procedure.

Completing or authorizing one operation does not authorize any later operation. In particular, a web-publication request does not authorize Git operations, and a Git request does not authorize staging or web publication.

## Distribution and version invariants

- MeshVenes is distributed as an unsigned, unpackaged, self-contained Windows x64 ZIP.
- The ZIP must not contain MSIX, MSIXBundle, APPX, APPXBundle, or AppInstaller artifacts. Packaged distribution requires an explicit policy change plus a signing and packaging plan.
- Bump the application version only when explicitly preparing a new distributable release. Documentation, tests, CI changes, and internal refactors do not require a bump unless they are part of that release.
- Keep `Version`, `AssemblyVersion`, `FileVersion`, and `AssemblyInformationalVersion` aligned in `MeshVenes/MeshVenes.csproj`.
- `app.manifest` is a compatibility manifest, not the distributable release version. The project must continue to set `WindowsPackageType=None` for this flow.
- Use a patch bump by default unless another version is requested.
- GitHub Actions is validation-only. It must not retain build artifacts or create a release.

## Restore, build, and test

Run from the repository root on Windows with the intended release source and a reviewed working tree:

```powershell
dotnet restore MeshVenes\MeshVenes.csproj -r win-x64 -p:Platform=x64
dotnet build MeshVenes\MeshVenes.csproj -c Release -p:Platform=x64 --no-restore
dotnet test MeshVenes.Tests\MeshVenes.Tests.csproj -c Release -p:Platform=x64
```

Do not continue if a required check fails. If a check cannot run, report exactly which check was omitted and why.

## Create and verify the Windows release artifact

1. Publish to a new temporary directory outside the repository and outside `H:\Koding\Venes.org`:

   ```powershell
   dotnet publish MeshVenes\MeshVenes.csproj -c Release -f net10.0-windows10.0.19041.0 -r win-x64 -p:Platform=x64 --self-contained true /p:PublishReadyToRun=false -o <temporaryPublishDirectory>
   ```

2. Confirm that `MeshVenes.exe` and `MeshVenes.pri` exist at the publish root. `MeshVenes.pri` is required for unpackaged WinUI startup.
3. Recursively reject files with the extensions `.msix`, `.msixbundle`, `.appx`, `.appxbundle`, or `.appinstaller`.
4. Smoke-test a copy of the finished publish directory. Keep the artifact source untouched: startup can create WebView2 profile data and an installation lock file.
5. ZIP the contents of the publish directory, not the directory itself, as `MeshVenes-<version>-win-x64.zip`. Confirm again that `MeshVenes.exe` and `MeshVenes.pri` are at the ZIP root.
6. Calculate the final ZIP's lowercase SHA-256 and exact byte size. These values are immutable release metadata; if the ZIP changes, calculate them again.

Do not describe the executable or ZIP as Authenticode-signed. Release metadata has a separate signature, described below. Do not mix files from different publish runs. Use `-p:DebugType=None -p:DebugSymbols=false` for the release publish to avoid shipping source-path debug information.

## Git and GitHub Release

Git operations are optional, separate actions. Perform only the actions explicitly requested, after the source diff and release checks are complete. A GitHub Release normally requires the intended release commit and matching `v<version>` tag to be available on GitHub; stop if that prerequisite has not been authorized or completed.

For a new explicitly authorized release, create the annotated tag from the verified release commit, push that tag, and create the release with the verified ZIP:

```powershell
git tag -a "v<version>" -m "MeshVenes <version>"
git push origin "v<version>"
gh release create "v<version>" "<verifiedZipPath>" --title "MeshVenes <version>" --notes "<approvedReleaseNotes>"
```

If the matching release already exists and an update was explicitly requested, inspect it first. Replace its ZIP only with the verified intended artifact and update approved metadata as needed:

```powershell
gh release view "v<version>" --json tagName,name,isDraft,isPrerelease,url,assets
gh release upload "v<version>" "<verifiedZipPath>" --clobber
```

After either path, query the release again and confirm the tag, public release URL, asset name, exact byte size, and SHA-256 digest. Keep Git tags as release history even if obsolete binary assets are removed later.

## Generate `version.json`

Generate the final manifest only after the actual GitHub Release URL is known. It must contain:

```json
{
  "version": "<version>",
  "url": "https://venes.org/meshvenes/MeshVenes-<version>-win-x64.zip",
  "sha256": "<lowercase-sha256>",
  "sizeBytes": 123456,
  "notes": "<approved release notes>",
  "releaseUrl": "https://github.com/rvenes/MeshVenes/releases/tag/v<version>"
}
```

Confirm that `version` matches all four project version properties, `url` names the verified ZIP, `sha256` and `sizeBytes` match its final bytes, and `releaseUrl` is the URL returned for the matching GitHub Release. Do not stage a provisional manifest.

## Project-specific pre-staging verification

Before changing local web staging:

1. Confirm the release ZIP came from the verified fresh publish and has the approved name.
2. Recalculate its exact byte size and lowercase SHA-256 and compare both with the final manifest.
3. Inspect the ZIP root for `MeshVenes.exe` and `MeshVenes.pri` and reject packaged artifacts anywhere in the archive.
4. Confirm the GitHub Release has the matching tag and verified asset.
5. Check the ZIP and manifest for credentials, private paths, private instructions, logs, profiles, and other non-release data.
6. Review the existing `H:\Koding\Venes.org\meshvenes` tree. Preserve website files and historical ZIPs unless a separate retention change was explicitly requested.

## Local web staging

Staging is not public publication. Only when staging is explicitly requested, copy the verified new ZIP and final `version.json` to:

```text
H:\Koding\Venes.org\meshvenes
```

Do not publish directly from a build directory. After copying, compare the staged ZIP's size and SHA-256 with the source and manifest, and byte-compare the staged manifest with its approved source. Do not remove unrelated or historical files.

## Public publication and verification

Public publication is a separate action. When it is explicitly requested, follow the active global venes.org procedure for the full-tree preview, deletion-risk review, publication, and verification. Do not use WinSCP, FTP, SFTP, SCP, automatic sync, or a repository-local alternative.

After a successful global publication, verify the public `version.json` and ZIP: the manifest must equal the staged manifest, the ZIP must have the expected size and SHA-256, and both URLs must respond successfully. Do not claim public publication based only on local staging or a preview.

The completion report must distinguish restore/build/test, artifact creation, Git commit/push, tag, GitHub Release, local staging, public publication, and public verification.

## PowerShell installer delivery

The installer source is `installers/install-windows.ps1`. Test it on Windows
PowerShell 5.1 and PowerShell 7 before delivery:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File installers\Test-Installer.ps1
pwsh.exe -NoProfile -File installers\Test-Installer.ps1
```

The offline test suite covers archive integrity, invalid paths, redirects through
filesystem junctions, shortcut paths, repeat installation, previous-version
selection and activation failure. Also test the real HTTPS feed, a fresh install,
a repeated install and application startup. Close MeshVenes before installation.

When installer staging is explicitly authorized, copy the reviewed script to
`H:\Koding\Venes.org\meshvenes\install-windows.ps1` and byte-compare the files.
Update the website instructions in the same authorized staging operation. An
installer-only change does not require a new application version or ZIP. When
application fixes accompany it, the normal fresh release requirements still apply.
Staging/publication authorization and the global web publication procedure remain
mandatory. Do not advertise the public command as available until the public script
has been fetched successfully and compared with the approved source.

The installer and new updater require `version.json.sig`: a 384-byte RSA-3072,
SHA-256, PKCS#1 v1.5 signature over the exact raw `version.json` bytes. Verify the
signature against `installers/release-public-key.xml`. Never reformat a manifest
after signing. Copy and byte-compare the signature alongside the manifest, ZIP and
installer during an authorized staging operation. Publish these together. A
temporary manifest/signature mismatch fails closed; GitHub remains display-only.

## Signing key management

Use PowerShell 7.4 or later for the operator scripts. `scripts/Release-Signing.ps1
-Initialize` creates a dedicated RSA-3072 key in `%LOCALAPPDATA%\MeshVenesReleaseSigning`
with an ACL restricted to the current Windows user and SYSTEM. Initialization
refuses to replace an existing directory. The signing key is DPAPI-protected for
the current Windows account and must never enter source control or web staging.

Initialization also writes an encrypted portable `recovery-key.pk8` and a separate
`recovery-password.txt` in that private directory. Move the encrypted recovery copy
to offline storage and store its password separately in a password manager. Do
this before reinstalling Windows; the DPAPI file alone is not portable. Retaining
both recovery files together is not an adequate offline backup arrangement.

After preparing and verifying the final manifest:

```powershell
pwsh -NoProfile -File scripts\Release-Signing.ps1 -ManifestPath <finalManifestPath>
```

After reinstalling Windows, restore the same key with
`scripts/Restore-ReleaseSigningKey.ps1 -RecoveryKeyPath <encryptedBackupPath>`.
It prompts for a masked recovery password, refuses to overwrite an existing key
directory and stores the restored key under the new account's DPAPI protection.
Compare the restored `public-key.xml` with the repository key before signing.
Never paste the password or private key into chat, command history or release logs.

For planned key rotation, first distribute a bridge application and installer that
trust both the old and new public keys while still signing the feed with the old
key. Only switch feed signatures after that bridge is available. The current
implementation pins one key; this bridge needs a reviewed code change. If the old
key is lost, restore its encrypted backup. If compromised, stop signing and provide
a verified out-of-band replacement installer; an attacker-controlled old key cannot
securely authorize its own replacement. Do not silently bypass signature checks.

Migration: old clients through 1.5.1 ignore the detached signature and rely on HTTPS
for their first upgrade. The new installer fails against the legacy unsigned feed.
Do not announce it until signed metadata is publicly available and verified.

Self-update now retains its unique staging directory, a per-file backup and
`apply-result.txt` under `%LOCALAPPDATA%\MeshVenes\Updates`. Failed copy operations
attempt to restore original files and do not automatically launch a possibly broken
application. Keep these files for diagnosis/recovery. Installer rollback changes
the selected program folder only; it does not roll back user data or undo an
in-place self-update inside that folder.

After an interrupted update, close all MeshVenes processes, retain the entire update
directory, and run the reviewed recovery script with explicit paths:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File installers\Repair-Update.ps1 -UpdateDirectory <retainedUpdateDirectory> -InstallDirectory <affectedProgramDirectory>
```

It verifies every retained original-file hash before restoring anything, refuses
links and destination mismatches, and keeps the backups. Use only the journal from
the affected installation; never substitute a downloaded or unrelated recovery plan.
