# Public build and acceptance status

This document is a public summary, not the original machine/account diagnostic log and not an authorization receipt for installer acceptance.

## Current follow-up status — 2026-10-07

This section supersedes the historical snapshots below for the current v5 build. The unsigned preview is published as [setup-preview-20261007-v5](https://github.com/yangjing6213-dev/GitHub-Backup/releases/tag/setup-preview-20261007-v5), with the installer and a separate checksum file.

- Installer artifact: `GitHubBackup-setup.exe`, 93,358,801 bytes, SHA-256 `40F0C458F3A97F9DEB97088FD11A51E695014EE467B2D88284F1E8092D1713DB`.
- Bundled app: self-contained Windows x64 executable, 117,487,776 bytes, SHA-256 `302B5F16C1FF14324BB32287A2C27ACD050B840DC04320454A7BDED4C8D30375`. Installer and app static verification: PASS. The installer is unsigned.
- Local replacement: the previous installed version was uninstalled with exit code 0; its install directory, start-menu shortcut, and uninstall registration were removed. The v5 installer exited with code 0 and installed four owned files; hashes, start-menu shortcut, uninstall registration, and committed receipt passed verification. Existing settings hash `1E1033DAE5A6AB2DB59800A35BB572E387C5D18FC07E2D4125796148D0A79F23` was unchanged.
- Source verification: full MSTest suite 1,199 passed, 0 failed, 31 skipped (1,230 total); Release x64 rebuild 0 warnings / 0 errors; single-file verifier PASS; `git diff --check` PASS after documentation updates.
- Iteration capabilities included in this package: new-folder restore verification, rate-limit pause/resume checkpoints, daily/weekly startup schedule, second local copy, explicit organization/collaborator scope, and optional Actions logs/artifacts with per-repository limits.
- Old package-only build directories were sent to the Windows Recycle Bin after file-count and scope checks; source, backups, settings, credentials, and the new v5 payload were not touched.
- Clean-PC use, installation on other computers/accounts, real-account scope differences, and restoring a backup: NOT_RUN. A successful local installation does not establish compatibility with every network, account, or repository.
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
