# GitHub Backup

[简体中文](README.md) | [English](README.en.md)

A local backup app for AI creators who keep their own projects on GitHub. It helps save repository code, version history, and supported project records to your computer, then review the backup results in one place.

As AI makes it easier to create more projects, it can become difficult to tell whether a complete local copy exists. This tool brings the backup steps into a Windows app where you can choose a destination, check prerequisites, and review each run.

**This is an unsigned Windows preview, not a production release.** The v4 installer passed static checks and was installed on the development PC, where the user confirmed a normal backup completed. Clean-PC use and backup restoration have not been verified. Check the [GitHub Releases page](https://github.com/yangjing6213-dev/GitHub-Backup/releases) for versions, notes, and checksums that are actually available; the older preview ZIP does not include the v4 fixes. Do not rely on a preview as the only copy of important files.

Contents: [About the project](#1-what-is-this-project) · [Example output](#2-what-does-a-backup-look-like) · [Who it is for](#3-who-is-it-for) · [Quick start](#6-installation-and-quick-start) · [Daily use](#7-how-to-use-it) · [Cautions and FAQ](#10-cautions-and-faq) · [Version status](#11-version-status) · [About the author](#13-about-the-author)

## 1. What is this project?

This is a local backup app under development. It is not a Skill to install in an AI assistant or a collection of prompts. The repository also keeps earlier command-line scripts for reference; ordinary users should start with this desktop-app guide.

You enter your own GitHub account and a local destination, sign in through a browser, and approve the requested backup access. After checking the environment, the app reads repositories owned by that account and saves code, version history, and supported project records locally.

It is not a downloader for arbitrary GitHub links, and it does not automatically upload backups back to GitHub.

## 2. What does a backup look like?

There is no public screenshot of a real interface or completed backup yet. The following is an **output layout example based on the code**, not a backup performed for this README.

For example, if you choose `D:\GitHub-Backups`, a run is organized by account and repository:

```text
D:\GitHub-Backups\<your-account>\
├─ mirrors\<repository>.git\             Repository code and history
├─ wikis\<repository>.wiki.git\          Existing Wiki content
├─ metadata\<repository>\
│  ├─ repository.json                     Repository details
│  ├─ issues.pages.json                   Issue records
│  ├─ pull-requests.pages.json             Pull request records
│  └─ releases.pages.json                 Release information
├─ manifests\summary-<run-id>.json         Run summary
└─ logs\backup-<run-id>.log                Run log
```

The `.git` folder is a repository mirror that preserves version history; it is not an ordinary project folder to open by double-clicking. `JSON` files are structured text, and logs help explain which steps succeeded or failed.

The “Full backup” mode also saves supported release assets under `releases\<repository>\`. After trying your own account, use “Open backup folder” and “View latest log” to inspect the actual output.

## 3. Who is it for?

### A good fit

- AI creators who have built several projects and want local copies of repositories they own.
- People who manage code, issues, Wiki pages, and release files and want them organized together.
- People who prefer checking backup status in a Chinese-language window instead of running several scripts by hand.

### Not a good fit

- People who only want to paste another person's repository URL and download that one project: the desktop app works with repositories owned by the signed-in account.
- People who need a validated installer, unattended scheduled backups, or one-click restoration to a remote repository: these are not delivered capabilities yet.
- People using macOS, Linux, Windows on ARM64, or a network drive as the destination: the current build targets Windows x64 and a local fixed disk.

You do not need programming skills, but you do need to download a file, choose a folder, and sign in to your own GitHub account in a browser. The current preview has additional tool requirements described below.

## 4. What will it save?

Based on the current code, backup output includes:

- Repository mirrors and version history. If a repository uses Git LFS, its large-file objects are saved too. Git LFS is an add-on for large files stored by GitHub projects.
- Wiki mirrors when a repository has a Wiki.
- Repository details, issues, pull requests, comments, labels, milestones, release information, workflow records, and other supported records as `JSON` files.
- Release assets and an asset index in Full backup mode.
- A repository manifest, run summary, and log for each run.

The default backup root is `D:\GitHub-Backups`; you can choose another eligible folder in the app. Actual files are organized below it by account. Avoid editing mirrors and indexes by hand because that may interfere with later updates.

App settings and diagnostic files are stored separately under `%LOCALAPPDATA%\GitHubBackupTool\`, not entirely on the backup drive. Those files are created only after the app runs.

## 5. What problem does it help solve?

Manual backups often require separate steps for repositories, release files, and result checks. The desktop app brings them together: check tools and the destination, choose Daily or Full mode, and review the outcome.

Daily mode skips release assets and is intended for recurring updates. Full mode includes release assets. Network settings are limited to the current backup process or a temporary configuration so that a run does not leave a global Git setting behind.

You still need to sign in, approve necessary actions, have a working network and enough disk space, and check the summary. The project does not support claims that every item is guaranteed to succeed or that it saves a particular amount of time.

## 6. Installation and quick start

### What you need

Required:

- A 64-bit Windows PC (x64), network access to GitHub, and your own GitHub account.
- A backup folder on a local fixed disk; NTFS is recommended. The disk root, network drives, removable drives, and directory links are not accepted.
- Enough disk space for your repositories and temporary files; reserving just 1 GB does not guarantee that it will be enough.
- Git, GitHub CLI (command name `gh`), and Git LFS. These are companion tools for backup and sign-in, not AI models.

The current app checks for Git `2.55.0.windows.3`, GitHub CLI `2.100.0`, and Git LFS `3.7.1` or later. These are requirements set by this project, not necessarily the official minimums for those tools.

The app has a “Check and install dependencies” entry. Software that already meets the required version does not need reinstalling. Installing missing or outdated tools through the app requires winget version `1.29.290` or later. If winget is unavailable, install “App Installer” from the [official Microsoft Store page](https://apps.microsoft.com/detail/9nblggh4nns1), or use the official links shown in the app.

The app bundles the .NET runtime, so ordinary users do not need to install the .NET SDK just to start it. Running on a clean computer has not yet been validated. A network proxy is optional; do not configure one if the connection already works.

The local code does not require a paid AI model. Access to GitHub, network service, and storage depends on your own circumstances. This page does not grant a promise of free or commercial-use rights.

### Installer download and safety checks

Use the [GitHub Releases page](https://github.com/yangjing6213-dev/GitHub-Backup/releases) to find installers with clear release notes and checksums. The page may also retain an older preview ZIP, which does not include the latest installer fixes. Treat a file as the fixed installer only when its release notes identify `GitHubBackup-setup.exe` and provide the matching SHA-256 value. If you cannot find that file, do not mistake the older ZIP for the latest version or construct a download URL.

The installer supports Windows 11 25H2 x64 (build 26200). Run it as a normal user, not as administrator. It installs to the fixed location `%LOCALAPPDATA%\Programs\GitHubBackupTool`, creates Desktop and Start Menu shortcuts, and registers an uninstall entry in the current user's Installed apps list. Uninstall removes only verified app-owned files and shortcuts; it preserves backups, settings, logs, and credentials.

If the installer reports an older version, follow its instructions to open the install folder and run `Uninstall.exe`, then rerun the new installer. Do not delete installation files manually. The installer is unsigned, so Windows may display an “Unknown publisher” warning. Verify the file's source; do not turn off Windows security protections.

### Previously released preview ZIP (does not include these fixes)

1. Read the current [preview release notes](https://github.com/yangjing6213-dev/GitHub-Backup/releases/tag/build-preview-20261001), then download the [Windows x64 preview package (ZIP)](https://github.com/yangjing6213-dev/GitHub-Backup/releases/download/build-preview-20261001/GitHubBackup-win-x64-preview-20261001-02.zip) and its [checksum file](https://github.com/yangjing6213-dev/GitHub-Backup/releases/download/build-preview-20261001/SHA256SUMS-preview-20261001-02.txt). This is a development preview, not a production release.
2. Extract the ZIP into a new folder. It should contain `GitHubBackup.exe` and the license notice `NOTICE.txt`; only the first file is the app. Keep the notice. GitHub's automatically generated `Source code` archives contain source code, not a runnable app.
3. Double-click `GitHubBackup.exe`. The app should show a Chinese-language backup window. It is unsigned, so Windows may warn that its publisher is unverified. Do not turn off security software or dismiss the warning blindly. Verify the download source first; if you cannot, stop.

This older ZIP does not include the newly fixed installer; its app still runs directly after extraction. The window flow above was checked against the interface and code, but **has not been validated through real-world use**.

### Minimal first run

1. In the main window, replace the default author account `yangjing6213-dev` with **your own GitHub username**. Do not enter your email or a repository URL.
2. Click “Check and install dependencies”. **Software that meets the minimum version will show “No installation needed”; checking the consent box does not start an installation.** Only when a tool is missing or too old should you agree and click the enabled “Install” button beside it. In-app installation requires winget. If the window says winget is unavailable, install “App Installer” from the [official Microsoft Store page](https://apps.microsoft.com/detail/9nblggh4nns1), then check again. Click “Recheck environment” afterward.
3. Choose a backup folder such as `D:\GitHub-Backups`. If you do not have a D: drive, choose a new folder on another local fixed disk. Click “Validate and save folder”; read the prompt before allowing it to create or repair folder permissions.
4. Read the notices about saving original data and credential use. After agreeing, click “Sign in with browser”; the app will try to open GitHub's official device sign-in page. If it does not open, visit `https://github.com/login/device` and enter the one-time code shown in the app. Then recheck the environment. This uses the GitHub credential store shared by the current Windows user and may affect the account used by other `gh` tools.
5. Start with “Daily backup”. Once the environment check passes and “Start backup” is enabled, click it. **The app processes repositories owned by this account, not just one sample project.**
6. When it finishes, click “Open backup folder” and “View latest log”. Check the run summary instead of assuming success just because a folder exists.

If a check fails, address the item shown in the app. Do not lower security settings just to enable a button.

### Build from source (for contributors)

Building requires the .NET 10 SDK. Run the following commands in PowerShell from the project root. These are developer build commands, not installation steps for ordinary users. `artifacts\publish\preview` should be a new output folder with no old files.

```powershell
dotnet build src/GitHubBackup.App/GitHubBackup.App.csproj --configuration Release --runtime win-x64 --no-restore -t:Rebuild -p:DebugType=None -p:DebugSymbols=false
dotnet publish src/GitHubBackup.App/GitHubBackup.App.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --no-build -p:DebugType=None -p:DebugSymbols=false --output artifacts/publish/preview
```

The current desktop app is an internal development preview. File metadata shows `1.0.0.0`; this is the executable's file version, not a validated stable `v1.0.0` release. The root [VERSION.txt](VERSION.txt) value `4.0.0-one-click-isolated-ssh443` applies only to earlier scripts.

The v4 installer and bundled Windows x64 app passed static file verification. The old version was removed, v4 was installed on the development PC, and the user confirmed a normal backup completed. The related focused tests passed 269/269. This does not verify use on a clean PC or restoration of a backup; see the [public verification record](docs/verification/setup-installer.md).

The latest setup preview is unsigned. Check its matching [GitHub Releases entry](https://github.com/yangjing6213-dev/GitHub-Backup/releases) for the available file and SHA-256 value. Real-profile installation and uninstall acceptance remain outstanding. Next, evaluate restore and legacy Git-configuration cleanup tools; planned features are not current capabilities. See the [public verification record](docs/verification/setup-installer.md).

## 7. How to use it

For a routine update, open the app, confirm the account and folder, complete the environment check, choose “Daily backup”, and start. Review the result when it finishes:

- `PASS`: the run summary reports success; still check that the repositories you need were in scope.
- `PARTIAL`: only part completed; review the summary and log for missing items.
- `FAIL`: the run did not pass; address the cause before trying again.
- `CANCELLED`: the run was canceled and must not be treated as a complete backup.

Choose “Full backup” when you need release assets. Revalidate a changed destination. Do not manually move, delete, or edit files in the target while a backup is running.

The app offers cancellation and a “Retry cleanup” action. If cleanup is incomplete, follow the prompt before starting another run. Closing the window does not mean every step finished successfully.

To report a problem, you can use “Preview and export diagnostics”. Review the export first and decide yourself whether to share it. Do not upload the raw backup, credentials, or unchecked logs.

## 8. How the process works

Enter your account and destination → check tools, space, and folder → approve necessary actions and sign in → choose a backup mode → the app reads repositories and records → review the run summary and log.

You decide whether to approve actions that affect your computer or account, such as signing in, installing tools, or creating/repairing a folder. The app handles reading repositories, organizing the output, and recording results. You still need to confirm that the result contains what you intended to preserve.

## 9. Project layout

```text
GitHub-Backup/
├─ README.md                            Chinese user guide
├─ README.en.md                         This English user guide
├─ VERSION.txt                          Earlier-script version, not the desktop release number
├─ src/GitHubBackup.App/                 Desktop app source
├─ tests/GitHubBackup.App.Tests/         Automated code checks, not user backups
├─ publish/
│  ├─ README.md                         Single-file build and inspection notes
│  ├─ Verify-SingleExe.ps1               Read-only check of a generated app file
│  └─ Setup/                            Installer build scripts, safety rules, and license notices
├─ docs/verification/setup-installer.md  Public development and acceptance status
├─ Backup-GitHubAccount.ps1              Earlier backup script
├─ Invoke-OneClickGitHubBackup.ps1       Earlier one-click entry script
└─ GitHubBackupTool.sln                  Entry point for opening the project in developer tools
```

The repository root also keeps numbered `.cmd` files and restore scripts from earlier workflows. Some depend on the author's SSH configuration; restore and cleanup scripts can change a remote repository or global settings. **Do not treat the old instruction “run 07 first” as a required installation step for everyone.**

The source tree is not the backup destination. Actual backups, app settings, and build output are created only when the app runs or a build is performed. They are not published with the source. Ordinary users do not need to edit core source files.

## 10. Cautions and FAQ

### Does the backup modify GitHub repositories?

The desktop backup flow reads GitHub content and does not upload a restore. Separate legacy restore scripts in the repository can push a mirror to a remote and are not part of the ordinary backup flow; assess overwrite risks separately before using them.

### What about private repositories and sensitive information?

The app reads repositories owned by the signed-in account that the account can access. The raw backup is not redacted: secrets already present in code, commit history, or project records may be copied as-is. Do not sync the backup folder to a public location.

After explicit consent, the app reads the credential for the specified account from the Windows credential store and uses it in memory for read-only requests; it does not separately save, display, log, or export that credential. Browser sign-in itself is written to the Windows shared credential store by GitHub CLI, so this does **not** mean that credentials are never stored during the sign-in process.

Logs are redacted and the backup folder has access restrictions. This is not encryption and does not guarantee that a computer administrator cannot access the files. Review diagnostic exports and anything you share manually.

### Why can I still not start after the checks?

Companion tools may be missing or below the project's required versions, the signed-in account may not match, the folder may not qualify, or disk space may be low. Follow the specific message in the app and check again. If you do not have a D: drive, choose another folder yourself; the app will not silently change the destination.

### I checked a dependency box, but the “Install” button is still disabled. Why?

Checking the box only means that you agree to the download and license terms; you must also click the adjacent Install button. If the detected version already meets the minimum, “No installation needed” is normal. If a tool is missing or outdated, the app shows whether winget is available. If it is not, install “App Installer” from the Microsoft Store as instructed by the app, then check again. Do not disable security protections to make a disabled button work.

### What if the network connection fails?

For timeouts, refused connections, and resets, the app retries according to its policy and may try the Windows system proxy when applicable. Network settings are limited to the current backup and do not change global Git settings. This cannot resolve every issue; follow the prompt or wait for account-permission, certificate, or GitHub rate-limit errors.

### Can these backups replace all GitHub data?

No. Workflow records are not the same as every Actions run log or build artifact, and submodules are not fetched recursively. The desktop app does not yet provide a completed restore flow, scheduled backups, or arbitrary repository filtering. A `PASS` result does not prove that every GitHub feature has a local copy.

### Can I install, use this commercially, or redistribute it?

Original project source code is licensed under the [MIT License](LICENSE). You may copy, modify, distribute, and commercially use that source, provided you retain the copyright and license notices. This license covers only original project source code; third-party components retain their own licenses, and it does not grant rights to third-party trademarks or the author-introduction image. The v4 preview installer is unsigned. It was installed on the development PC, where the user confirmed a normal backup completed; clean-PC use and restore have not been verified. Check the [GitHub Releases page](https://github.com/yangjing6213-dev/GitHub-Backup/releases) for versions and checksums that are actually available.

Third-party runtime and installer-build-tool licenses are listed in [NOTICE.txt](publish/Setup/NOTICE.txt). That notice is not a license for this project's source. Do not remove license notices distributed with the app or disable security protections to dismiss warnings.

## 11. Version status

The desktop app is an internal development preview. File metadata shows `1.0.0.0`; that is the executable's file version, not a validated stable `v1.0.0` release. The root [VERSION.txt](VERSION.txt) value `4.0.0-one-click-isolated-ssh443` applies only to earlier scripts.

Completed with build evidence: the v4 Windows x64 installer was built and passed static file verification. The old version was uninstalled, v4 was installed on the development PC, and the user confirmed a normal backup completed. For eligible network interruptions, the app retries and may try the Windows system proxy. The related test set passed 269 tests.

Not yet verified: use on a clean PC, installation on other computers or accounts, and whether a backup can be fully restored. The preview installer is unsigned; a successful backup on one PC does not prove that every account, network, or repository will work. Check the [GitHub Releases page](https://github.com/yangjing6213-dev/GitHub-Backup/releases) for an actually published download and SHA-256.

The [releases page](https://github.com/yangjing6213-dev/GitHub-Backup/releases) is the download source; choose only a version whose notes and SHA-256 match the installer fixes you need. Restore and legacy Git-configuration cleanup tools remain future work, not current capabilities. See the [installer verification record](docs/verification/setup-installer.md) for evidence and unverified areas.

## 12. Related projects

None.

## 13. About the author

Enhe (恩禾) | Product Designer · Solo-company Practitioner · AI Builder

Building a one-person company with AI.

- GitHub: [yangjing6213-dev](https://github.com/yangjing6213-dev)
- X / Twitter: [Amenenhe_ai](https://x.com/Amenenhe_ai)
- Website: [ENHE AI](https://www.enhe-tech.com.cn/)
- WeChat: Hu-Amen
- Email: [amen.enhe@gmail.com](mailto:amen.enhe@gmail.com)

## 14. Keep exploring

After creating AI projects, preserving and organizing them is also part of a personal work system. I hope this tool can gradually make it clearer where projects live, which records have been kept, and which backups still need attention.

If you use AI to create content, organize a knowledge base, build workflows, or turn an idea into a product, visit my website to learn more about my AI creation and one-person-company practice:

[Visit ENHE AI](https://www.enhe-tech.com.cn/)
