# CODEX Rules (MeshVenes)

## Core principles

1. Prefer minimal diffs: change only what is required to satisfy the request.
   - If the request genuinely needs larger changes, do them, but keep them tightly scoped and explain the tradeoff.
2. English only inside the repo for: source code, identifiers, comments, UI text, commit messages, and documentation.
3. This repository is a **.NET 8 WinUI 3** project. Keep changes compatible with that stack unless explicitly upgraded with approval.
4. Ask questions when anything is unclear or requires a decision (UI/UX, behavior, naming, edge cases, data format, etc.).
5. Work safely: avoid destructive operations and do not overwrite or discard work unless it is clearly safe.

## Language Rules

1. Explanations and chat responses: Norwegian (Nynorsk preferred).
2. ALL source code, identifiers, comments, UI text, and commit messages: English only.
3. Never generate Norwegian text inside code or UI.

## Change scope

- Default: minimal, targeted edits.
- Allowed when necessary: larger refactors or multi-file changes **only** when they clearly reduce bugs, improve stability, or are required by the feature/fix.
- Avoid unrelated cleanups.

## Frameworks / libraries

- Default: do **not** introduce new frameworks, UI libraries, or architectural patterns.
- Exception: if a new dependency would make the app **materially better** (stability, performance, accessibility, maintainability, UX), it may be proposed.
  - Before adding it, ask for approval and provide:
    - What it solves and why current stack is insufficient
    - Cost/risks (size, complexity, licensing, maintenance)
    - Minimal integration plan
  - Prefer Microsoft-supported, well-maintained libraries with clear licensing.

## Build / validation (required)

After making changes, validate locally so the user does not need to discover basic failures manually:

1. Ensure the solution builds (no compilation errors).
2. If tests exist, run them.
3. If a specific packaging/build configuration is relevant, validate that path too.

Use standard .NET CLI commands from repo root (examples, adjust to the solution layout):

- dotnet --info
- dotnet restore
- dotnet build -c Debug
- dotnet test (only if tests exist)

If a build/test cannot be run in the current environment, state exactly what could not be run and why, and still keep changes minimal and safe.

## Version bump rule (required)

Before commit/push for changes that are intended to go to GitHub and trigger GitHub Actions artifacts/releases, bump the app version.

When the user asks for a version bump, or when preparing changes for GitHub push/release flow, update all relevant version sources so About and packaged builds show the same new version:

- `MeshVenes.csproj`: `Version`, `AssemblyVersion`, `FileVersion`, `AssemblyInformationalVersion`
- `MeshVenes/Package.appxmanifest`: `<Identity Version="...">`

Never bump only one location.
Default bump level: patch version, unless the user explicitly requests a different version.

## Git operations (automation)

Codex should handle the full Git workflow so the user can simply review a PR link and then pull/merge from GitHub.

### Safety rules

- Run git commands only when safe and unambiguous.
- Never use: force push, hard reset, rewriting history, deleting remote branches, rebasing shared branches, or mass file rewrites unless explicitly requested.
- Never delete anything if there is uncertainty.
- If the working tree is not clean, stop and ask what to do.

### Standard workflow (run from repo root)

1. Update main:

- git switch main
- git pull

2. Create a branch with the required Codex prefix:

- git switch -c codex/<name>

3. Implement changes + validate build/tests (see section above).

4. Commit:

- git add -A
- git commit -m "<English technical message>"

5. Push and provide a PR link:

- git push -u origin codex/<name>

Then provide the GitHub URL to open a PR (or use `gh pr create` only if GitHub CLI is installed and authenticated).

6. Do not delete the local branch automatically.
   - After the PR is merged, suggest cleanup commands, but do not run them unless asked:
     - git switch main
     - git pull
     - git branch -d codex/<name>

### Post-merge reset workflow (required)

When the user confirms the PR is merged and asks to prepare for new work:

1. `git switch main`
2. `git pull`
3. Verify clean state (`git status`)
4. Create a fresh feature branch for the next task only when the user is ready to start coding:
   - `git switch -c codex/<next-name>`

When the user confirms the PR is already merged on GitHub, Codex should perform steps 1-3 automatically before doing any further repo work, as long as the working tree is clean or the state can be advanced safely without discarding changes.

## Manual approval before commit & push (mandatory)

After implementing changes and successfully validating the build/tests:

1. STOP before running:
   - git add
   - git commit
   - git push

2. Provide a concise summary including:
   - Files changed
   - What was implemented
   - Build/test status
   - Any assumptions or UI decisions

3. Clearly instruct the user how to proceed by printing exactly:

To continue, reply with:
Approved – proceed with commit and push

4. Only after the user replies exactly:

Approved – proceed with commit and push

Then execute the standard Git workflow:

- git switch main
- git pull
- git switch -c codex/<name>
- git add -A
- git commit -m "<English technical message>"
- git push -u origin codex/<name>

Then provide the GitHub PR link.

Never push automatically without explicit approval.

## Branch hygiene (required)

- If continuing work for an open PR, stay on that PR branch and keep working there until the user asks to reset after merge.
- If starting a new task and the previous PR is already merged, switch to `main`, pull, verify a clean working tree, then create a fresh `codex/<name>` branch before editing.
- Do not keep coding on an outdated branch if the intended work should land through a different active PR branch.
- During post-merge cleanup, never discard uncommitted work. If cleanup would overwrite local changes, stop and preserve them safely before advancing branches.
- Do not delete merged local branches automatically during cleanup. Only delete them if the user explicitly asks.

## App self-update (venes.org/meshvenes)

The app updates itself from a static feed hosted at `https://venes.org/meshvenes/`. The implementation lives in `MeshVenes/Services/UpdateService.cs`.

### Feed layout on venes.org

Two files are uploaded manually to `venes.org/meshvenes/`:

- `version.json` — manifest describing the latest version:
  - `version` (e.g. `"1.4.8"`), `url` (absolute URL to the zip), `sha256` (lowercase hex of the zip), `sizeBytes`, `notes`, `releaseUrl`
- `MeshVenes-<version>-win-x64.zip` — full zip of the self-contained win-x64 publish output (must contain `MeshVenes.exe` at the zip root)

### How the check works

1. On startup (and on demand from the About page) the app fetches `version.json` with a cache-busting query string.
2. If the manifest version is greater than the running version, the user is prompted to update. If the manifest is unreachable, the app falls back to the GitHub releases API for display-only information (no self-update).
3. On accept, the app downloads the zip to `%LOCALAPPDATA%` under the app data `Updates` folder, verifies the sha256, extracts to a staging folder, writes an `apply-update.cmd` script, and starts it. The script waits for the app process to exit, robocopies the staged files over the install folder, and restarts the app. The app does NOT exit automatically: the UI tells the user the update is downloaded and offers "Restart now" (closes the main window so the script can apply the update); otherwise the update is applied the next time the app is closed.
4. Self-update only works for unpackaged installs in a writable folder (`CanSelfUpdate()`); MSIX/read-only installs only get a download link.
5. For testing, the manifest URL can be overridden via the `UpdateManifestUrlOverride` settings key.

### Building the upload package locally

1. Bump the version (see version bump rule) and build/publish:
   `dotnet publish MeshVenes/MeshVenes.csproj -c Release -f net8.0-windows10.0.19041.0 -r win-x64 -p:Platform=x64 --self-contained true -o <publishDir>`
2. Zip the publish folder contents (not the folder itself) as `MeshVenes-<version>-win-x64.zip`.
3. Generate `version.json` with the new version, URL `https://venes.org/meshvenes/<zipName>`, sha256, and size.
4. Place both files in `H:\Koding\venes-upload` for the user to upload to `venes.org/meshvenes/`.

The GitHub Actions workflow (`.github/workflows/build-release.yml`) produces the same two files as a `venes-upload` artifact when a GitHub release is created (version taken from the release tag).

## Environment assumptions

- Windows 11
- Repository path: `H:\Koding\MeshVenes`
- Git is executed from Windows PowerShell in the repo root.
- Do not use WSL paths like `/mnt/h/...`.
- Do not use `git -C <path>`.
