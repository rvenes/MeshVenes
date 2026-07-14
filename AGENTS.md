# MeshVenes agent instructions

## Project and environment

- MeshVenes is a .NET 8 WinUI 3 application for Windows.
- The primary development environment is Windows 11. Use PowerShell and run commands from the repository root.
- Normal development, testing, packaging, and publishing for MeshVenes happen on Windows. The Mac and `SyncMacPC` are not part of the normal MeshVenes flow.
- If a task genuinely requires Mac work, cross-machine transfer, or Syncthing, follow the private development-environment instructions configured outside the repository. Private instructions must never be copied into this repository or committed.

## Communication and repository language

- Chat with the user in Norwegian, preferably Nynorsk.
- Use English for all repository content: source code, identifiers, comments, UI text, documentation, branch names, commit messages, and pull requests.

## Working style

- Implement the requested outcome completely while keeping the diff focused.
- Preserve unrelated user changes and never discard or overwrite them.
- Make safe, reasonable assumptions when details are minor. Ask only when a missing decision could materially change behavior, data, user experience, or release safety.
- Avoid unrelated cleanup and broad refactors unless they are necessary for correctness or clearly reduce risk.
- Add dependencies only when they materially improve the project. Prefer maintained Microsoft-supported libraries with clear licensing and explain any meaningful tradeoff.

## Validation

Run validation appropriate to the change before handing work back. For normal code changes, use the actual project paths:

```powershell
dotnet restore MeshVenes\MeshVenes.csproj -r win-x64 -p:Platform=x64
dotnet build MeshVenes\MeshVenes.csproj -c Release -p:Platform=x64 --no-restore
dotnet test MeshVenes.Tests\MeshVenes.Tests.csproj -c Release -p:Platform=x64
```

For release or packaging changes, also validate the self-contained publish:

```powershell
dotnet publish MeshVenes\MeshVenes.csproj -c Release -f net8.0-windows10.0.19041.0 -r win-x64 -p:Platform=x64 --self-contained true /p:PublishReadyToRun=false -o <temporaryPublishDirectory>
```

- A release ZIP must contain `MeshVenes.exe` at the ZIP root.
- If a relevant check cannot run, state exactly what was not run and why.

## Versioning

- Bump the application version when explicitly preparing a new distributable release, not for every pull request or push.
- Documentation, tests, CI/workflow changes, and internal refactors do not require a version bump unless they are part of a requested release.
- For a release, keep these properties in `MeshVenes/MeshVenes.csproj` aligned: `Version`, `AssemblyVersion`, `FileVersion`, and `AssemblyInformationalVersion`.
- Update `MeshVenes/Package.appxmanifest` only if packaged distribution is explicitly reintroduced.
- Use a patch bump by default unless the user requests another version.

## Git and pull requests

- Inspect the working tree before editing and include only task-related files in commits.
- Continue on the current pull-request branch while that pull request is open.
- For new work after a merge, update `main`, verify a clean working tree, and create a focused `codex/<name>` branch.
- When the user asks to publish changes, commit the validated task files, push the branch, and open or update a draft pull request. No fixed approval phrase is required.
- After a pull request is merged, synchronize local `main` before starting unrelated work.
- Do not force-push, rewrite shared history, hard-reset user work, or delete branches unless explicitly requested.

## Distribution and releases

- MeshVenes is distributed as an unsigned, self-contained Windows ZIP. Do not build or publish MSIX, MSIXBundle, or APPX packages unless the user explicitly changes this policy and provides a signing plan.
- GitHub Actions is CI only. It may build, test, and validate publishing, but it must not retain build artifacts or create releases.
- Create a release only when the user explicitly requests publication. Follow the private release safeguards configured outside the repository without copying them into the repository.

Release flow:

1. Bump the version and complete the build and tests locally on Windows.
2. Publish to a temporary directory outside the configured web-publishing directory.
3. ZIP the publish directory contents as `MeshVenes-<version>-win-x64.zip`, with `MeshVenes.exe` at the ZIP root.
4. Calculate SHA-256 and generate `version.json` with `version`, `url`, `sha256`, `sizeBytes`, `notes`, and `releaseUrl`.
5. Create or update the GitHub release with the verified ZIP using `gh`; use that release URL in `version.json`.
6. Copy the verified ZIP and `version.json` directly to the configured local MeshVenes web-publishing directory, then verify the copied size and SHA-256.
7. WinSCP uploads changes from that folder automatically. Confirm the public manifest and ZIP before reporting success.

- Do not use an intermediate release staging directory or retain release files as GitHub Actions artifacts.
- Publish only when the user explicitly asks for it.
- Keep Git tags as release history even when obsolete binary assets are removed.

## Self-update behavior

- The implementation lives in `MeshVenes/Services/UpdateService.cs`.
- The application reads `https://venes.org/meshvenes/version.json` with cache busting.
- The manifest points to `https://venes.org/meshvenes/MeshVenes-<version>-win-x64.zip` and includes its lowercase SHA-256 and exact byte size.
- Updates are downloaded, size-checked, hash-verified, extracted to staging, and applied after the app exits.
- Self-update is available only for unpackaged installations in writable folders. Packaged or read-only installations receive a download link instead.
- If the static manifest is unavailable, the GitHub releases API is a display-only fallback; it does not perform self-update.
- Tests may override the manifest URL through the `UpdateManifestUrlOverride` settings key.
