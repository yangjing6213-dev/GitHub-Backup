# Public build and acceptance status

This document is a public summary, not the original machine/account diagnostic log and not an authorization receipt for installer acceptance.

## Current follow-up status — 2026-10-04

This section supersedes the historical snapshot below for the current local build. It does not claim that the package has been installed in a real user profile or released publicly.

- Installer artifact: `GitHubBackup-setup.exe`, 92,445,219 bytes, SHA-256 `B8AFB5F8B5C047E0A743476C699EBC6DEA09B3FAD8B86C933F408BAB7469454F`.
- Bundled app: one self-contained Windows x64 executable, 116,877,472 bytes, SHA-256 `6110E971830F1A4D0863EB9953FBF3A4884886152EDDC9FBD495845036134564`; file and installer static verification: PASS. The installer is unsigned.
- The installer implementation is designed to create Desktop and Start Menu shortcuts and a per-user Windows uninstall registration. Actual display in Windows' Installed apps list has not been checked in a real profile.
- Browser sign-in now attempts to open the fixed GitHub device-login page after the one-time code is issued; the user still completes sign-in manually. If opening fails, the app shows a manual fallback. The code is not put in the URL or persisted by this flow. The main window no longer gives its live log the initial focus.
- Focused tests for the affected authentication, UI and setup areas: 117 passed, 0 failed, 0 skipped. A broader UI interaction test class previously had 8 environment-limited failures while creating restricted ACL fixtures; that broader result is not a pass.
- Actual install/uninstall, Windows uninstall-list display, browser launch, real sign-in, backup, clean-machine use and restore: NOT_RUN. No real credentials, registry entries, account, or backup data were accessed by these checks. These build checks do not constitute real-user acceptance.
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
