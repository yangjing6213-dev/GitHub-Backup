# Public build and acceptance status

This document is a public summary, not the original machine/account diagnostic log and not an authorization receipt for installer acceptance.

## Current follow-up status — 2026-10-25

This section supersedes the historical snapshots below for the current v5 preview package. The installer and separate checksum asset on [setup-preview-20261007-v5](https://github.com/yangjing6213-dev/GitHub-Backup/releases/tag/setup-preview-20261007-v5) were refreshed from source commit `9e1ec01962b7ebbf64ee82f97403336be40aa502`; the existing release tag was left in place.

- Installer artifact: `GitHubBackup-setup.exe`, 93,366,754 bytes, SHA-256 `3A4B6D662F6C727CEF10F1B7F5336A159ADF26CFA0EC0AAEB432D5E07872EE24`.
- Bundled app: self-contained Windows x64 executable, 117,491,872 bytes, SHA-256 `534C2104B771867208967BE1D52B6B35AF00AF0EB42EE12CFE004A89DF56F2D3`. App and installer static verification: PASS. The installer is unsigned.
- Local replacement: old uninstaller exit 0; new installer exit 0; four owned files and two shortcuts verified. The installed receipt names source commit `9e1ec01962b7ebbf64ee82f97403336be40aa502`. Settings, GitHub CLI configuration, and backup-directory metadata were unchanged.
- Source verification: full MSTest suite 1,202 passed, 0 failed, 31 skipped (1,233 total); Release x64 publish 0 warnings / 0 errors; single-file verifier PASS; setup preflight and static/installed-file verification PASS. NSIS 3.12 completed with 13 warnings about unused alternate install/uninstall routines and variables.
- Public asset metadata: GitHub reports the installer size and SHA-256 above, matching the local file. A direct download from this environment was not verified because the GitHub download service timed out.
- The installed app was left closed for the user's manual test. Actual GitHub-account backup and restore, clean-PC use, and installation on other computers/accounts: NOT_RUN.
- Compatibility note: a schema-2 older install is not upgraded in place; its existing `Uninstall.exe` must be run first. If a current uninstall registration is missing, uninstall can proceed after validating the files, but a repair/reinstall through that missing registration remains fail-closed.

## Historical snapshot — 2026-10-03 (superseded above)

## Main application

- Windows x64 .NET 10 desktop application, self-contained single EXE.
- Latest Release publish after the dependency-window change: PASS for single-file creation with `DebugType=None` and `DebugSymbols=false`; NuGet reported that vulnerability metadata could not be fetched.
- Artifact: `GitHubBackup.exe`, 116742304 bytes.
- SHA256: `BBAF02F1E1AB8FC8B05FC4FFBE55390F0D7AC0A3C9DEFE3ECB9890F485C791A5`.
- Read-only file verifier: one regular EXE, AMD64 PE32+ Windows GUI, unsigned, seven known synthetic/legacy marker checks PASS.
- Application assembly embedded portable debug metadata: absent in the reviewed public candidate.
- Additional bounded byte-pattern check: no GitHub-token-like strings, private-key headers or developer-profile path matches. These are limited checks, not a security guarantee.
- Startup/exit, UI, clean-machine use, real login/backup and signing: NOT_RUN.
- Dependency-dialog clarification: satisfied Git/GitHub CLI/Git LFS versions are labelled "no installation needed" and no longer show consent/install controls. Missing or unsupported WinGet now displays why the install buttons are unavailable and links Microsoft's official App Installer page. The checkbox's role and the separate Install click are explained. Release build completed; UI interaction was not launched or runtime-verified in this task.

## Installer

- Installer lifecycle: NOT_IMPLEMENTED. `publish/Setup/GitHubBackup.nsi` retains `!error "SETUP_LIFECYCLE_NOT_IMPLEMENTED"` before output generation.
- No final setup.exe is supplied or claimed as verified.
- Fresh-install journal contract: known RED check `Fresh_install_journal_is_created_and_verified_before_payload_copy`; missing integration is not hidden by removing the test.
- Earlier 44-check packaging subset success is not a claim that the current full suite passes.
- No test-account execution, installation, uninstallation or runtime test was performed while preparing this public snapshot.

## Public snapshot boundary

The reviewed public snapshot contains source, tests, build scripts, current user documentation and third-party notices. It excludes local backup data, downloaded build tools, runtime logs, private diagnostics, repeated old-version archive folders and the original local Git history. The original workspace and its history remain unchanged.

The public copy uses the current user's profile to locate the PowerShell test tool rather than hardcoding a developer-profile path. The main application source includes the dependency-dialog clarification described above. Tests are NOT_RUN for these changes.

No root source-code LICENSE has been selected. Third-party license texts in `publish/Setup/NOTICE.txt` do not grant a license to the project's source.
