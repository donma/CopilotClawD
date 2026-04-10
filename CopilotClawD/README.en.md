# CopilotClawD

CopilotClawD is a local Windows Telegram Bot AI agent powered by the GitHub Copilot SDK. It lets you control local code projects from Telegram while keeping execution on your own machine.

Send a message to the bot and it can read and write files, run shell commands, operate Git, search the web, and use MCP tools locally.

[中文版 README](README.md)

---

## Screenshots

| | |
|:---:|:---:|
| ![](https://github.com/donma/BlogResource/blob/main/2569/260410125857.jpg?raw=true) | ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-55-03.jpg?raw=true) |
| ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-55-00.jpg?raw=true) | ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-55-02.jpg?raw=true) |
| ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-54-58.jpg?raw=true) | ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-55-05.jpg?raw=true) |
| ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-55-07.jpg?raw=true) | ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_13-06-26.jpg?raw=true) |

---

## Features

- Streaming replies displayed in real time
- Multi-project switching per Telegram chat
- Multi-model switching
- Session persistence across restarts
- Cross-session memory summaries
- Security layering for shell commands and file access
- Secret redaction in outputs
- MCP server support
- Self-update workflow
- Passcode-based self-registration
- Message debounce for consecutive bursts

---

## System Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [GitHub Copilot CLI](https://docs.github.com/en/copilot/github-copilot-in-the-cli) (`winget install GitHub.Copilot`)
- A GitHub account with Copilot access
- A Telegram Bot Token from [@BotFather](https://t.me/BotFather)

---

## Quick Start

### 1. Get a Telegram Bot Token

Send `/newbot` to [@BotFather](https://t.me/BotFather) and follow the prompts to create your bot.

### 2. Get your Telegram User ID

Send any message to [@userinfobot](https://t.me/userinfobot) to get your numeric Telegram User ID.

### 3. Sign in to GitHub

Use device-based authentication:

```powershell
gh auth login --web
```

### 4. Create the secret config file

Copy the example file:

```powershell
copy CopilotClawD\appsettings.secret.example.json CopilotClawD\appsettings.secret.json
```

Then edit `appsettings.secret.json` and fill in at least:

- `TelegramBotToken`
- `AllowedUserIds`
- `Projects`

### 5. Place the secret file next to the executable

`appsettings.secret.json` must be placed in the same directory as `CopilotClawD.exe`.

### 6. Run the app

Run the executable from its own directory, or use `dotnet run` from the `CopilotClawD` project folder during development.

---

## Self-Registration

`RegistrationPasscode` enables self-registration for new users without manually editing the white-list.

### How it works

1. Set a passcode in `appsettings.secret.json`.
2. Share the passcode with a user through a secure channel.
3. If the user is not on `AllowedUserIds`, the bot treats their message as a passcode attempt.
4. If the passcode is correct, the user is added to `AllowedUserIds` and written back to `appsettings.secret.json` immediately.
5. If the passcode is wrong, the bot ignores it silently to avoid exposing the mechanism.

### Notes

- Leave `RegistrationPasscode` empty to disable self-registration.
- The passcode is also treated as a secret and redacted from outputs where applicable.

---

## Configuration Reference

### `appsettings.secret.json`

```json
{
  "CopilotClawD": {
    "TelegramBotToken": "<from @BotFather>",
    "AllowedUserIds": [123456789],
    "AdminUserIds": [123456789],
    "RegistrationPasscode": "",
    "DefaultModel": "gpt-5-mini",
    "SwapDirectory": "D:\\AI_PROJECTS\\CopilotClawD\\swap",
    "Projects": {
      "myproject": {
        "Path": "D:\\Workspace\\MyProject",
        "Description": "My project"
      }
    },
    "Security": {
      "Enabled": true,
      "ConfirmationTimeoutSeconds": 60,
      "RedactSecrets": true,
      "ForbiddenCommandPatterns": [],
      "DangerousCommandPatterns": [],
      "SafeCommandPatterns": [],
      "ProtectedPathPatterns": [],
      "AdditionalSecrets": []
    },
    "Memory": {
      "Enabled": true,
      "MaxEntries": 10,
      "SessionIdleTimeoutHours": 2
    },
    "McpServers": {
      "github": {
        "Type": "http",
        "Url": "https://api.githubcopilot.com/mcp/",
        "Headers": {
          "Authorization": "Bearer YOUR_GITHUB_TOKEN"
        },
        "Tools": ["*"]
      }
    }
  }
}
```

### Field Notes

| Field | Required | Description |
|------|------|------|
| `TelegramBotToken` | Yes | Bot token from @BotFather |
| `AllowedUserIds` | Yes | Telegram user IDs allowed to use the bot |
| `AdminUserIds` | No | Users allowed to run admin commands like `/update` |
| `RegistrationPasscode` | No | Passcode for self-registration; empty disables it |
| `DefaultModel` | No | Default AI model |
| `SwapDirectory` | No | Temporary folder for bot files such as screenshots |
| `Projects` | Yes | Project list, keyed by alias |
| `McpServers` | No | MCP server definitions |
| `Security.Enabled` | No | Enables security filtering |
| `Security.ConfirmationTimeoutSeconds` | No | Timeout for dangerous-operation confirmation |
| `Security.RedactSecrets` | No | Redacts secrets from AI output |
| `Security.ForbiddenCommandPatterns` | No | Regex patterns for directly forbidden shell commands |
| `Security.DangerousCommandPatterns` | No | Regex patterns that require user confirmation |
| `Security.SafeCommandPatterns` | No | Whitelist patterns that bypass danger checks |
| `Security.ProtectedPathPatterns` | No | Glob patterns for paths AI cannot read/write |
| `Security.AdditionalSecrets` | No | Extra secret strings to redact |
| `Memory.Enabled` | No | Enables cross-session memory |
| `Memory.MaxEntries` | No | Max memory entries per chat |
| `Memory.SessionIdleTimeoutHours` | No | Auto-summary timeout for idle sessions |

---

## Security Model

All AI-initiated shell commands, file operations, and MCP tool calls are evaluated by `PermissionPolicy` and classified into four levels:

| Level | Behavior | Example |
|------|------|------|
| **Safe** | Allowed immediately | Normal read operations |
| **Normal** | Allowed immediately | Normal writes, `git status` |
| **Dangerous** | Telegram confirmation required | `rm -rf`, `git push --force` |
| **Forbidden** | Rejected immediately | Disk formatting, shutdown commands, mining software |

All rules can be customized in the `Security` section of `appsettings.secret.json`.

`appsettings.secret.example.json` already includes most of the recommended security rules. Please review them carefully and adjust them for your own environment before running the bot.

### Forbidden Rules

These actions are rejected without asking for confirmation:

| Rule | Scope |
|------|------|
| `format [a-z]:` | Windows disk formatting |
| `mkfs` / `fdisk` / `diskpart` / `diskutil eraseDisk` | Linux/macOS disk operations |
| `shutdown` / `reboot` / `init [06]` / `systemctl poweroff|reboot|halt` | System shutdown/restart |
| `xmrig` / `minergate` / `cpuminer` etc. | Known mining tools |
| `curl ... | sh` / `wget ... | sh` | Download-and-execute scripts |
| `Invoke-Expression` | PowerShell arbitrary code execution |
| `reg delete` / `Registry::` | Windows registry deletion |
| `rm ... /` / `Remove-Item [A-Z]:\` | Root-level deletion |

### Dangerous Rules

These actions trigger Telegram confirmation buttons and require manual approval:

| Rule | Scope |
|------|------|
| `rm -r` / `rm -f` / `rmdir /s` / `del /s` / `rd /s` / `Remove-Item -Recurse` | Recursive deletion |
| `git push --force` / `git push -f` | Force push |
| `git reset --hard` / `git clean -fdx` / `git checkout -- .` | Destructive Git operations |
| `git branch -d|-D` | Delete branch |
| `chmod` / `chown` / `icacls` / `takeown` | Permission and ownership changes |
| `npm install -g` / `pip install` / `brew install` / `apt install` / `choco install` / `winget install` | Global package installs |
| `kill -9` / `taskkill` / `Stop-Process` | Forcefully kill processes |
| `sc stop|delete|config` / `systemctl stop|disable` / `launchctl unload` | Service operations |
| `setx` / `$env:` / `export PATH` | Environment variable changes |
| `Set-Content` / `Add-Content` / `Out-File` / `New-Item` / `Copy-Item` / `Move-Item` / `Rename-Item` / `mv` / `cp` / `tee` | Write and move operations |

> **Note:** The `DangerousCommandPatterns` list is intentionally broad. Common development commands such as `mv`, `cp`, and `New-Item` may require confirmation unless you whitelist them in `SafeCommandPatterns`.

### Protected Paths

These paths cannot be read or written by AI:

| Path | Description |
|------|------|
| `**/appsettings.secret.json` | Bot secret config |
| `**/*.env` / `**/.env` / `**/.env.*` | Environment files |
| `**/credentials.json` / `**/credentials.yaml` / `**/secrets.json` / `**/secrets.yaml` | Common credential files |
| `**/*_rsa` / `**/*_ed25519` / `**/id_rsa` / `**/id_ed25519` / `**/.ssh/config` | SSH keys |
| `**/.git-credentials` / `**/.netrc` / `**/token.json` | Git/HTTP credentials |
| `C:/Windows/**` / `C:/Program Files/**` / `C:/Program Files (x86)/**` | Windows system directories |
| `/etc/passwd` / `/etc/shadow` / `/etc/sudoers` / `/etc/ssh/**` | Linux system accounts and SSH |
| `/System/**` / `/System32/**` / `/Library/**` | macOS/Windows system directories |

### Recommended Adjustments

After copying `appsettings.secret.example.json`, consider:

- Adding trusted commands to `SafeCommandPatterns`
- Narrowing `DangerousCommandPatterns` for your workflow
- Extending `ProtectedPathPatterns` for any project-specific secrets
- Filling `AdditionalSecrets` with API keys or other sensitive strings you want redacted

---

## Runtime Screenshots

| | |
|:---:|:---:|
| ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-55-03.jpg?raw=true) | ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-55-00.jpg?raw=true) |
| ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-55-02.jpg?raw=true) | ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-54-58.jpg?raw=true) |
| ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-55-05.jpg?raw=true) | ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-55-07.jpg?raw=true) |
| ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_13-06-26.jpg?raw=true) | |

---

## Notes

- Only one instance can run at a time. Multiple polling connections with the same bot token will trigger Telegram 409 conflicts.
- `appsettings.secret.json` is not committed to source control.
- The bot runs on your machine, so AI can read/write files and execute shell commands. Limit `AllowedUserIds` to trusted users only.

---

## License

MIT License. See [LICENSE](../LICENSE)

You may use, modify, and distribute this project, including for commercial purposes, as long as the original copyright notice is retained.

---

## Disclaimer

This project is provided as-is without any express or implied warranty.

Before using it, understand the risks:

- Local execution risk: the bot can read/write files and run shell commands on your machine.
- AI behavior is not perfectly predictable: review every AI-generated action yourself.
- Security rules are not complete protection: the built-in patterns are a safety layer, not a guarantee.
- Third-party services are involved: GitHub Copilot, Telegram Bot API, and others may change availability or policies.
- Secret protection is best-effort: `SecretRedactor` reduces exposure, but it cannot guarantee perfect prevention.

The author is not responsible for any direct or indirect damage, including data loss, system damage, or service interruption.
