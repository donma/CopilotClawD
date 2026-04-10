# CopilotClawD

An AI agent that lets you control local code projects through Telegram, powered by the GitHub Copilot SDK.

Send a message to the bot and it can read and write files, execute shell commands, operate Git, search the web, and run entirely on your machine.

[中文版 README](README.md)

---

## Showcase

![](https://github.com/donma/BlogResource/blob/main/2569/260410125857.jpg?raw=true)

Main execution view and startup showcase.

---

## Features

- **Streaming replies** - AI output appears token by token in real time
- **Multi-project switching** - each Telegram chat can switch to a different working directory (project)
- **Multi-model switching** - supports all available Copilot models (gpt-4o, gpt-5, claude-sonnet, etc.) with rate multiplier display
- **Session persistence** - restore previous conversations after restarting the bot without re-explaining context
- **Cross-session memory** - when a session ends, the AI generates a summary that will be injected as background memory next time
- **Security layering** - shell commands are classified into Forbidden / Dangerous / Normal, and dangerous operations require Telegram button confirmation
- **Secret redaction** - sensitive strings such as tokens and passwords are automatically masked in AI output
- **MCP server support** - connect to any MCP server (local stdio or remote HTTP/SSE) to extend available tools
- **Self-update** - the `/update` command rebuilds and restarts the bot automatically without logging into the host machine
- **Passcode-based self-registration** - authorized users can join the whitelist themselves without editing the config manually
- **Multi-line debounce** - messages sent within 3 seconds are merged before being sent to the AI

---

## System Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [GitHub Copilot CLI](https://docs.github.com/en/copilot/github-copilot-in-the-cli) (`winget install GitHub.Copilot`)
- A GitHub account with Copilot access
- A Telegram Bot Token from [@BotFather](https://t.me/BotFather)

---

## Quick Start

### 1. Get a Telegram Bot Token

Send `/newbot` to [@BotFather](https://t.me/BotFather) and follow the prompts to create a bot and obtain the token.

### 2. Get your Telegram User ID

Send any message to [@userinfobot](https://t.me/userinfobot) and it will reply with your numeric Telegram User ID.

### 3. Set up GitHub authentication

Use device-based login for GitHub CLI:

```powershell
gh auth login --web
```

This will display a one-time code and open a browser. Log in to GitHub in the browser and enter the code to complete authentication.

### 4. Create the configuration file

Copy the template and fill in your own values:

```powershell
copy CopilotClawD\appsettings.secret.example.json CopilotClawD\appsettings.secret.json
```

Edit `appsettings.secret.json` (minimum required fields):

```json
{
  "CopilotClawD": {
    "TelegramBotToken": "your bot token",
    "AllowedUserIds": [your user id],
    "Projects": {
      "myproject": {
        "Path": "C:\\Users\\you\\Source\\MyProject",
        "Description": "My project"
      }
    }
  }
}
```

### 5. Build and run

```powershell
dotnet run --project CopilotClawD\CopilotClawD.csproj
```

Or build first and run the executable directly:

```powershell
dotnet build CopilotClawD\CopilotClawD.csproj -c Release
.\CopilotClawD\bin\Release\net10.0\CopilotClawD.exe
```

After startup succeeds, send a message to your bot in Telegram to begin using it.

---

## Configuration Reference

All settings are centralized in `appsettings.secret.json` and are not committed to source control. `appsettings.json` only contains non-sensitive shared settings.

| Field | Required | Description |
|------|------|------|
| `TelegramBotToken` | Yes | Bot token from @BotFather |
| `AllowedUserIds` | Yes | List of Telegram user IDs allowed to use the bot |
| `AdminUserIds` | No | User IDs allowed to run admin commands such as `/update` (defaults to the first `AllowedUserIds` entry) |
| `RegistrationPasscode` | No | Self-registration passcode (leave empty to disable self-registration) |
| `DefaultModel` | No | Default AI model (for example `gpt-4o`, `gpt-5-mini`) |
| `SwapDirectory` | No | Directory for bot temporary files such as screenshots |
| `Projects` | No | Project list; key is an alias and value contains `Path` and `Description` |
| `McpServers` | No | MCP server configuration (see example below) |
| `Security.Enabled` | No | Enables security filtering (defaults to `true`) |
| `Security.ConfirmationTimeoutSeconds` | No | Timeout in seconds for dangerous-operation confirmation (defaults to 60) |
| `Security.RedactSecrets` | No | Redacts secrets from AI output (defaults to `true`) |
| `Security.ForbiddenCommandPatterns` | No | Regex patterns for shell commands that are directly forbidden (defaults include disk formatting and mining, etc.) |
| `Security.DangerousCommandPatterns` | No | Regex patterns that require user confirmation (defaults include `rm -rf`, `git push --force`, etc.) |
| `Security.SafeCommandPatterns` | No | Whitelist patterns that bypass dangerous-command checks (for example allowing writes to the swap directory) |
| `Security.ProtectedPathPatterns` | No | Glob patterns for paths AI is not allowed to read or write (defaults include `.env`, SSH keys, Windows system directories) |
| `Security.AdditionalSecrets` | No | Additional secret strings to mask |
| `Memory.Enabled` | No | Enables cross-session memory (defaults to `true`) |
| `Memory.MaxEntries` | No | Maximum memory entries kept per chat (defaults to 10) |
| `Memory.SessionIdleTimeoutHours` | No | Hours before an idle session is summarized and destroyed (0 = disabled, defaults to 2) |

### Self-Registration (`RegistrationPasscode`)

`RegistrationPasscode` enables new users to **self-join the whitelist** without requiring the administrator to edit the config file manually.

**How it works:**

1. The administrator sets a passcode in `appsettings.secret.json`, for example:
   ```json
   "RegistrationPasscode": "my-secret-code"
   ```
2. The passcode is shared with the user through another channel such as a private message or group chat.
3. When a user sends any message to the bot and is not yet in `AllowedUserIds`, the bot treats that message as a passcode attempt.
4. If the passcode is correct, the bot replies with a registration success message and automatically writes the user ID into `AllowedUserIds` inside `appsettings.secret.json`. The change takes effect immediately without a restart.
5. If the passcode is wrong, the bot ignores it silently to avoid exposing the mechanism.

**Notes:**

- Leaving it empty (the default) disables self-registration, and any user not in `AllowedUserIds` will be rejected.
- The passcode itself is also redacted by `SecretRedactor` and will not appear in AI output or logs.

### MCP Server Example

```json
"McpServers": {
  "github": {
    "Type": "http",
    "Url": "https://api.githubcopilot.com/mcp/",
    "Headers": {
      "Authorization": "Bearer YOUR_GITHUB_TOKEN"
    },
    "Tools": ["*"]
  },
  "local-tool": {
    "Type": "local",
    "Command": "npx",
    "Args": ["-y", "@some/mcp-server"],
    "Tools": ["*"]
  }
}
```

---

## Telegram Commands

| Command | Description |
|------|------|
| `/start` | Show the welcome message and current status |
| `/help` | Show all available commands |
| `/projects` | List all projects with buttons for switching |
| `/use <name>` | Switch to a specific project (case-insensitive) |
| `/model` | Show available models with buttons for switching |
| `/model <name>` | Switch to a specific model |
| `/status` | Show the current session status (project, model, session ID, uptime) |
| `/memory` | Show all cross-session memory entries |
| `/memory clear` | Clear all cross-session memory |
| `/clear` | Destroy the current session and clear conversation history |
| `/mcp` | List configured MCP servers and their connection status |
| `/news <keyword>` | Search the latest news and return a Traditional Chinese summary |
| `/update` | Rebuild and restart automatically (admins only) |

---

## Architecture Overview

```
Telegram App
    │  (HTTPS polling)
    ▼
TelegramBotService          ← BackgroundService, receives updates
    │
    ├── CommandHandler       ← Handles commands starting with /
    └── MessageHandler       ← Handles normal chat messages (with 3-second debounce)
            │
            ▼
        AgentService         ← Session management, memory injection, permission checks
            │
            ▼
        CopilotClient        ← GitHub Copilot SDK (JSON-RPC over stdio)
            │
            ▼
        Copilot CLI          ← Local copilot process
            │
            ▼
        Copilot API          ← GitHub cloud AI (streaming)
```

Each Telegram chat corresponds to its own `CopilotSession`, and the session working directory maps to the selected project's path.

---

## Project Structure

```
DClaw/
├── CopilotClawD/                    # Main app (entry point)
│   ├── Program.cs                   # Host setup, service registration, startup flow
│   ├── Splash.cs                    # Startup animation (ASCII art)
│   ├── appsettings.json             # Shared settings (log level, etc.)
│   ├── appsettings.secret.json      # Secret settings (not committed)
│   └── appsettings.secret.example.json  # Template
│
├── CopilotClawD.Core/               # Core logic (no UI dependency)
│   ├── Agents/
│   │   ├── AgentService.cs          # Session management, message routing, memory injection
│   │   └── SystemPrompts.cs         # System prompt composition (project info, memory, tool guidance)
│   ├── Configuration/
│   │   └── CopilotClawDConfig.cs    # POCO model for all settings
│   ├── Memory/
│   │   ├── ISessionStore.cs / JsonFileSessionStore.cs   # Session persistence
│   │   └── IMemoryStore.cs / JsonFileMemoryStore.cs     # Cross-session memory persistence
│   ├── Registration/
│   │   └── RegistrationService.cs   # Self-registration (passcode verification, whitelist writing)
│   ├── Security/
│   │   ├── PermissionPolicy.cs      # Evaluates command/path/MCP risk level
│   │   ├── SecretRedactor.cs        # Redacts secrets from AI output
│   │   └── IPermissionConfirmer.cs  # Confirmation interface for dangerous actions
│   └── SelfUpdateService.cs         # Self-build and restart
│
├── CopilotClawD.Telegram/           # Telegram bot implementation
│   ├── TelegramBotService.cs        # BackgroundService (polling, debounce)
│   ├── ServiceCollectionExtensions.cs  # DI extension methods
│   ├── TelegramPermissionConfirmer.cs  # Inline keyboard confirmation for dangerous actions
│   ├── MarkdownV2Helper.cs          # MarkdownV2 escaping helper
│   └── Handlers/
│       ├── CommandHandler.cs        # Handles all / commands
│       └── MessageHandler.cs        # Normal conversation streaming handler
│
└── CopilotClawD.slnx               # Solution file
```

---

## Security Model

All AI-initiated shell commands, file reads/writes, and MCP tool calls are evaluated by `PermissionPolicy` and split into four levels:

| Level | Behavior | Example |
|------|------|------|
| **Safe** | Allowed immediately | Normal read operations |
| **Normal** | Allowed immediately | Normal writes, `git status`, etc. |
| **Dangerous** | Sends Telegram confirmation buttons and waits for allow/reject (timeout auto-rejects) | `rm -rf`, `git push --force` |
| **Forbidden** | Rejected immediately without asking | Disk formatting, shutdown commands, mining tools |

All rules can be customized in the `Security` section of `appsettings.secret.json`.

`appsettings.secret.example.json` already includes a recommended default security policy. Please review it carefully and adjust it for your own workflow.

### Forbidden

The following actions are rejected immediately without asking for confirmation:

| Rule | Scope |
|------|---------|
| `format [a-z]:` | Windows disk formatting |
| `mkfs` / `fdisk` / `diskpart` / `diskutil eraseDisk` | Linux/macOS disk operations |
| `shutdown` / `reboot` / `init [06]` / `systemctl poweroff|reboot|halt` | System shutdown/restart |
| `xmrig` / `minergate` / `cpuminer` and similar | Known mining software |
| `curl ... | sh` / `wget ... | sh` | Download-then-execute scripts |
| `Invoke-Expression` | Arbitrary PowerShell code execution |
| `reg delete` / `Registry::` | Windows registry deletion |
| `rm ... /` / `Remove-Item [A-Z]:\` | Deleting a root drive |

### Dangerous

The following actions show Telegram confirmation buttons and require manual approval:

| Rule | Scope |
|------|---------|
| `rm -r` / `rm -f` / `rmdir /s` / `del /s` / `rd /s` / `Remove-Item -Recurse` | Recursive deletion |
| `git push --force` / `git push -f` | Force push |
| `git reset --hard` / `git clean -fdx` / `git checkout -- .` | Destructive Git operations |
| `git branch -d|-D` | Delete branch |
| `chmod` / `chown` / `icacls` / `takeown` | Permission and ownership changes |
| `npm install -g` / `pip install` / `brew install` / `apt install` / `choco install` / `winget install` | Global package installation |
| `kill -9` / `taskkill` / `Stop-Process` | Force killing processes |
| `sc stop|delete|config` / `systemctl stop|disable` / `launchctl unload` | Service operations |
| `setx` / `$env:` / `export PATH` | Environment variable changes |
| `Set-Content` / `Add-Content` / `Out-File` / `New-Item` / `Copy-Item` / `Move-Item` / `Rename-Item` / `mv` / `cp` / `tee` | Write and move operations |

> **Note:** `DangerousCommandPatterns` is intentionally broad and includes many common development actions such as `mv`, `cp`, and `New-Item`. Add trusted commands to `SafeCommandPatterns` to reduce confirmation prompts.

### Protected Paths

The following paths cannot be read or written by AI:

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

After copying `appsettings.secret.example.json`, consider the following:

- Add trusted commands to `SafeCommandPatterns`
- Narrow `DangerousCommandPatterns` to fit your workflow
- Extend `ProtectedPathPatterns` with any project-specific secret files
- Put API keys or other sensitive strings into `AdditionalSecrets`

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
- The bot runs on your own machine, so AI can read and write files and execute shell commands. Limit `AllowedUserIds` to yourself or other trusted users.

---

## License

MIT License - see [LICENSE](../LICENSE)

This project is released under the MIT License. You are free to use, modify, and distribute it, including for commercial purposes, as long as the original copyright notice is retained.

---

## Disclaimer

This project is provided by the author "as is", without any express or implied warranty.

**Please understand the following risks before using it:**

- **Local execution risk**: the bot runs on your machine, and AI can read/write files and execute shell commands. Misconfiguration may lead to unexpected file changes or system operations.
- **AI behavior is not perfectly predictable**: language models may produce incorrect, misleading, or unexpected output, and you should always judge AI-suggested actions yourself.
- **Security rules are not complete protection**: the built-in `ForbiddenCommandPatterns` and `DangerousCommandPatterns` are only safety layers and cannot guarantee that every dangerous action will be blocked. Adjust them carefully for your own environment.
- **Third-party service dependency**: this project depends on GitHub Copilot, Telegram Bot API, and other third-party services. Availability, policy changes, and pricing are controlled by those providers, not by this project.
- **Secret protection is best-effort**: please do not commit or share `appsettings.secret.json`. `SecretRedactor` helps reduce exposure, but it cannot guarantee perfect secrecy in every case.

The author is not responsible for any direct or indirect damage caused by using this project, including but not limited to data loss, system damage, or service interruption.
