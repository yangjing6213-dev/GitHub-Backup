![GitHub Backup cover: Keep a local copy of your AI projects](docs/assets/cover-en.png)

# GitHub Backup — Keep a Local Copy of Your AI Projects

[简体中文](README.md) | [English](README.en.md)

[![Source license: MIT](https://img.shields.io/badge/Source%20license-MIT-yellow.svg?style=for-the-badge)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg?style=for-the-badge&logo=.net&logoColor=white)](src/GitHubBackup.App/GitHubBackup.App.csproj)
[![C#](https://img.shields.io/badge/C%23-%23239120.svg?style=for-the-badge&logo=csharp&logoColor=white)](src/GitHubBackup.App/GitHubBackup.App.csproj)
[![Windows x64](https://img.shields.io/badge/Windows-x64-0078D6.svg?style=for-the-badge&logo=windows&logoColor=white)](src/GitHubBackup.App/GitHubBackup.App.csproj)

> **Your AI-built project should not exist only behind one GitHub login.** While you can still access your account, save the repositories and project records you can reach to your computer. A local copy can reduce the risk of losing access if your account is suspended or unavailable.
>
> [See available downloads](https://github.com/yangjing6213-dev/GitHub-Backup/releases) · [Start a backup](#start-a-backup) · [Read before using](#read-before-using)
>
> **Back up in advance:** this app cannot unlock an account or retrieve files that were never backed up after access is lost.

## What does it do?

GitHub Backup is a Windows desktop app. Sign in with your GitHub account, and it saves repositories and supported project records that you can access to your computer. It does not need an AI model and is not a downloader for arbitrary GitHub links.

By default, it backs up repositories you own. You can explicitly include organization or collaborator repositories when you have access. The desktop app keeps backups locally; it does not upload them back to GitHub.

## What can it save?

| Content | What that means |
| --- | --- |
| Project code and version history | Includes Git LFS objects when a repository uses them. |
| Project records | Supported data includes Wikis, repository details, issues, pull requests, comments, labels, milestones, and release information. |
| Release attachments | Downloaded in “Full backup”; “Daily backup” updates code and records. |
| Actions records | Optional workflow logs and build artifacts, subject to GitHub retention and a per-repository size limit. |
| Backup results | Each run has a summary and log. On GitHub rate limits, the app can pause and save progress to continue later. |

After a backup, you can verify and restore it into a new local folder, copy a verified second copy to another local location, or set a daily or weekly schedule. A schedule is checked when the app opens; it does not run in the background while the app is closed.

## Start a backup

1. Open the [project Releases page](https://github.com/yangjing6213-dev/GitHub-Backup/releases), read the version notes, and download the matching Windows x64 installer and SHA-256 checksum. Older test packages may still be listed; GitHub’s `Source code` archive is not a runnable app.
2. Run the installer and open the app. The current v5 installer is unsigned, so Windows may show an “Unknown publisher” warning. Check the file source and checksum; do not turn off system protection. Follow the release notes for installation and removal.
3. Enter your own GitHub username, choose a backup folder on a local fixed drive, and follow the app’s checks for Git, GitHub CLI, and Git LFS. If a tool is missing, the app shows available install options or official instructions.
4. Read the sign-in notice and sign in through your browser. Choose “Daily backup” or “Full backup,” start the run, then review its result and log. A folder appearing on disk does not by itself prove the backup completed successfully.

You need a Windows x64 computer, your own GitHub account, network access, and enough local disk space. Installation has only been checked on the development PC running Windows 11 25H2 x64; compatibility with other Windows versions is unverified. The app includes its .NET runtime. A backup covers multiple repositories in the selected account scope, not just one project.

## Read before using

- **This is a preview.** The v5 installer passed file checks and was installed on the development PC after uninstalling the older version. Source checks also passed. A clean-computer install, use on another computer, real-account permission differences, and a complete backup restore have not been verified. Do not use a preview as the only copy of important files.
- **Published installers may lag behind the source.** This README describes the project in the current repository. Before downloading, use the selected Release’s notes, asset name, and SHA-256 checksum to confirm which features that installer includes.
- **Sign-in may affect other GitHub CLI tools for the same Windows user.** Browser sign-in uses GitHub CLI’s shared credential store. Keep this in mind if other tools use a different GitHub account.
- **A local backup may contain private information.** Code, commit history, and project records are saved as-is and are not encrypted. Protect your computer and backup drive; do not publicly share raw backups or unchecked diagnostic files.
- **This is not a full export of GitHub.** Repositories you cannot access or have not selected are not backed up. Submodules are not fetched recursively. Actions records must be enabled and are limited by retention and storage size. Restore creates a new local working folder; it does not write back to GitHub.
- **A second local copy is still stored where you choose.** If both copies are on the same computer or drive, a device or drive failure could affect both.

## Common questions

**Can it retrieve a project after I lose access to my account?** No. It can only organize data that you could access and backed up before access was lost.

**Will a scheduled backup run while my computer is shut down?** No. Daily or weekly schedules are checked when you open the app; this is not an always-on Windows background service.

**Does the app make paid AI calls?** No paid AI model is used. You still need your own network, GitHub account, and local storage.

**What license does the project use?** Original source code is under the [MIT License](LICENSE). This does not replace third-party component licenses or grant rights to third-party trademarks or the author image.

## Project details

- [Installer verification record](docs/verification/setup-installer.md)
- [Build instructions for developers](publish/README.md)
- [Full license](LICENSE)

## About the author

![About the author: Enhe and contact information](docs/assets/about-author.png)

Enhe — Product Designer · One-person company practitioner · AI Builder

Building a one-person company with AI.

- GitHub: [yangjing6213-dev](https://github.com/yangjing6213-dev)
- X / Twitter: [Amenenhe_ai](https://x.com/Amenenhe_ai)
- Website: [Enhe ENHE AI](https://www.enhe-tech.com.cn/)
- WeChat: Hu-Amen
- Email: [amen.enhe@gmail.com](mailto:amen.enhe@gmail.com)
