GitHub Backup Tool for D drive
Version: 4.0.0-one-click-isolated-ssh443

Recommended workflow:

1. Run 07-RESTORE-CODEX-GIT-SETTINGS.cmd once.
   This removes only the legacy backup-specific global URL rewrite and the
   legacy HTTP/1.1 override, then refreshes GitHub CLI credentials.

2. Run 05-ONE-CLICK-FULL-BACKUP.cmd for a complete backup.
   Enter the SSH private key passphrase once when prompted.

3. Later, run 06-ONE-CLICK-DAILY-UPDATE.cmd for routine updates.
   Release assets are skipped in daily mode.

4. Run 08-CHECK-LAST-BACKUP.cmd to review the latest summary.

How the one-click mode works:
- Starts a temporary SSH agent.
- Loads C:\Users\<you>\.ssh\enhe-ai-github-ed25519.
- Uses SSH over port 443 only inside the backup process.
- Does not change global Git settings.
- Stops the temporary agent and clears temporary settings even if backup fails.
- Backups remain under D:\GitHub-Backups.

A passphrase entry is still required because the private key is encrypted.
The tool never stores the passphrase.
