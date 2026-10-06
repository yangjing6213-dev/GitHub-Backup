# Public build and acceptance status

This document is a public summary, not the original machine/account diagnostic log and not an authorization receipt for installer acceptance.

## Current follow-up status — 2026-10-07

This section supersedes the historical snapshots below for the current local v4 build. The package has been installed on the development PC; public release status is separate and must be checked on GitHub.

- Installer artifact: `GitHubBackup-setup.exe`, 93,293,246 bytes, SHA-256 `937DDD6E89C1A72CD1C36766591B3AFEF2297DD316DC439DEE69F3050B1C751F`.
- Bundled app: self-contained Windows x64 executable, SHA-256 `99571B5DD8C42CDE84475E3001D440895F43925E981A96652DD077908939BD08`. Installer and app static verification: PASS. The installer is unsigned.
- Local replacement: old version uninstall and v4 install exited successfully; installed files, shortcuts, uninstall registration and receipt passed verification. Existing settings hash was unchanged.
- Local backup: the user confirmed that a normal backup completed successfully with the installed app. This record relies on that confirmation; this documentation update did not inspect backup contents, credentials, or account data.
- Related focused tests: 269 passed, 0 failed, 0 skipped. The broader test suite was not rerun for this package update.
- Clean-PC use, installation on other computers/accounts, and restoring a backup: NOT_RUN. A successful backup on one PC does not establish compatibility with every network or repository.
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
