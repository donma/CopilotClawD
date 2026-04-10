# CopilotClawD

透過 Telegram 操控你本機程式碼的 AI Agent，由 GitHub Copilot SDK 驅動。

傳一條訊息給 Bot，它就能讀寫你的檔案、執行 Shell 指令、操作 Git、搜尋網路——全部在你的機器上本地執行。

[English README](README.en.md)

---

## 展示

![](https://github.com/donma/BlogResource/blob/main/2569/260410125857.jpg?raw=true)

主要執行畫面與啟動展示。

---

## 功能特色

- **Streaming 回覆** — AI 回覆逐字即時顯示，不用等到完整回應才看到
- **多專案切換** — 每個 Telegram 對話可獨立切換工作目錄（project），AI 的 working directory 隨之改變
- **多模型切換** — 支援 Copilot 所有可用模型（gpt-4o、gpt-5、claude-sonnet 等），含費率倍率顯示
- **Session 持久化** — 重啟 Bot 後可自動恢復前次對話 session，不需重新說明 context
- **跨 Session 記憶** — Session 結束時 AI 自動產生對話摘要，下次啟動會注入為背景記憶
- **安全分級攔截** — Shell 指令依危險程度分為 Forbidden / Dangerous / Normal，危險操作需 Telegram 按鈕確認
- **機密防洩漏** — AI 回覆中的 Token、密碼等敏感字串自動遮蔽
- **MCP Server 支援** — 可連接任意 MCP server（local stdio 或 remote HTTP/SSE），擴充 AI 可用工具
- **自我更新** — `/update` 指令觸發重新編譯並自動重啟為新版本，不需登入主機
- **自助註冊** — 可設定密碼讓授權使用者自助加入白名單，不需手動改設定檔
- **多行 Debounce** — 3 秒內連續發送的訊息會合併成一則再送給 AI

---

## 系統需求

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [GitHub Copilot CLI](https://docs.github.com/en/copilot/github-copilot-in-the-cli) (`winget install GitHub.Copilot`)
- GitHub 帳號（需有 Copilot 訂閱）
- Telegram Bot Token（從 [@BotFather](https://t.me/BotFather) 取得）

---

## 快速開始

### 1. 取得 Telegram Bot Token

向 [@BotFather](https://t.me/BotFather) 傳送 `/newbot`，依指示建立 Bot 並取得 Token。

### 2. 設定自助註冊密碼

將 `RegistrationPasscode` 設成你要使用的密碼，例如 `PASSCODE_YOUR_SET`。之後，把這組密碼傳給 Bot，Bot 會自動把你加入 `AllowedUserIds` 白名單。

### 3. 設定 GitHub 驗證（或準備 GitHub Token）

使用 device 驗證登入 GitHub CLI：

```powershell
gh auth login --web
```

執行後會顯示一組一次性代碼，並自動開啟瀏覽器。在瀏覽器中登入 GitHub 並輸入代碼即完成驗證。

### 4. 建立設定檔

複製範本並填入你的資訊：

```powershell
copy CopilotClawD\appsettings.secret.example.json CopilotClawD\appsettings.secret.json
```

編輯 `appsettings.secret.json`（最少必填欄位）：

`RegistrationPasscode` 是自助註冊密碼。留空時會關閉自助註冊；若設定了密碼，尚未在 `AllowedUserIds` 內的使用者可以直接把這組密碼傳給 Bot，Bot 會自動把該使用者加入白名單並寫回設定檔。

```json
{
  "CopilotClawD": {
    "TelegramBotToken": "你的 Bot Token",
    "AllowedUserIds": [你的 User ID],
    "RegistrationPasscode": "<PASSCODE_YOUR_SET>",
    "Projects": {
      "myproject": {
        "Path": "C:\\Users\\you\\Source\\MyProject",
        "Description": "我的專案"
      }
    }
  }
}
```

### 5. 編譯並執行

```powershell
dotnet run --project CopilotClawD\CopilotClawD.csproj
```

或先編譯再直接執行 exe：

```powershell
dotnet build CopilotClawD\CopilotClawD.csproj -c Release
.\CopilotClawD\bin\Release\net10.0\CopilotClawD.exe
```

啟動成功後，前往 Telegram 傳訊息給你的 Bot 即可開始使用。

---

## 設定檔參考

所有設定集中在 `appsettings.secret.json`（不納入版控）。`appsettings.json` 只放不含機密的公用設定。

| 欄位 | 必填 | 說明 |
|------|------|------|
| `TelegramBotToken` | ✅ | 從 @BotFather 取得的 Bot Token |
| `AllowedUserIds` | ✅ | 允許使用 Bot 的 Telegram User ID 清單 |
| `AdminUserIds` | | 可執行 `/update` 等管理指令的 User ID（預設為 AllowedUserIds 第一位）|
| `RegistrationPasscode` | | 自助註冊密碼（留空則關閉自助註冊）|
| `DefaultModel` | | 預設 AI 模型（例如 `gpt-4o`、`gpt-5-mini`）|
| `SwapDirectory` | | Bot 暫存檔（截圖等）的目錄路徑 |
| `Projects` | | 專案清單，key 為代號，value 含 `Path` 和 `Description` |
| `McpServers` | | MCP server 設定（見下方說明）|
| `Security.Enabled` | | 是否啟用安全攔截（預設 `true`）|
| `Security.ConfirmationTimeoutSeconds` | | 危險操作確認逾時秒數（預設 60）|
| `Security.RedactSecrets` | | 是否遮蔽 AI 回覆中的機密（預設 `true`）|
| `Security.ForbiddenCommandPatterns` | | 直接禁止的 Shell 指令 regex（預設含格式化磁碟、挖礦等）|
| `Security.DangerousCommandPatterns` | | 需使用者確認的 Shell 指令 regex（預設含 rm -rf、git push --force 等）|
| `Security.SafeCommandPatterns` | | 白名單：符合者略過危險攔截（例如允許對 swap 目錄寫入）|
| `Security.ProtectedPathPatterns` | | 禁止 AI 讀寫的路徑 glob（預設含 .env、SSH key、Windows 系統目錄）|
| `Security.AdditionalSecrets` | | 額外需遮蔽的機密字串清單 |
| `Memory.Enabled` | | 是否啟用跨 Session 記憶（預設 `true`）|
| `Memory.MaxEntries` | | 每個 chat 保留的記憶條目上限（預設 10）|
| `Memory.SessionIdleTimeoutHours` | | Session idle 後自動產摘要並銷毀的小時數（0 = 停用，預設 2）|

### 自助註冊（RegistrationPasscode）

`RegistrationPasscode` 是讓新使用者**自助加入白名單**的機制，不需要管理員手動編輯設定檔。

**運作方式：**

1. 管理員在 `appsettings.secret.json` 設定一組密碼，例如：
   ```json
   "RegistrationPasscode": "my-secret-code"
   ```
2. 將密碼透過其他管道（私訊、群組等）告知想要加入的使用者。
3. 使用者對 Bot 傳送任何訊息時，若尚未在 `AllowedUserIds` 白名單內，Bot 會嘗試將訊息內容當作密碼比對。
4. 密碼正確 → Bot 回覆「註冊成功」，並自動將該使用者的 ID 寫入 `appsettings.secret.json` 的 `AllowedUserIds`，**立即生效，無需重啟**。
5. 密碼錯誤 → Bot **靜默忽略**（不回覆任何訊息，避免暴露密碼機制）。

**注意：**
- 留空（預設）則關閉自助註冊，所有未列在 `AllowedUserIds` 的使用者一律被拒絕。
- 密碼本身也會被 `SecretRedactor` 遮蔽，不會出現在 AI 回覆或日誌中。

### MCP Server 設定範例

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

## Telegram 指令

| 指令 | 說明 |
|------|------|
| `/start` | 顯示歡迎訊息與目前狀態 |
| `/help` | 顯示所有可用指令 |
| `/projects` | 列出所有專案，點按鈕切換 |
| `/use <name>` | 切換至指定專案（支援不區分大小寫）|
| `/model` | 顯示可用模型，點按鈕切換 |
| `/model <name>` | 切換至指定模型 |
| `/status` | 顯示目前 session 狀態（專案、模型、Session ID、uptime）|
| `/memory` | 顯示所有跨 Session 記憶條目 |
| `/memory clear` | 清除所有跨 Session 記憶 |
| `/clear` | 銷毀目前 session，清除對話歷史 |
| `/mcp` | 列出已設定的 MCP server 及連線狀態 |
| `/news <關鍵字>` | 搜尋最新新聞並以繁中摘要回覆 |
| `/update` | 重新編譯並自動重啟（僅管理員）|

---

## 架構概覽

```
Telegram App
    │  (HTTPS polling)
    ▼
TelegramBotService          ← BackgroundService，接收 updates
    │
    ├── CommandHandler       ← 處理 / 開頭的指令
    └── MessageHandler       ← 處理一般對話（含 3 秒 debounce 合併）
            │
            ▼
        AgentService         ← Session 管理、記憶注入、權限攔截
            │
            ▼
        CopilotClient        ← GitHub Copilot SDK（JSON-RPC over stdio）
            │
            ▼
        Copilot CLI          ← 本機執行的 copilot process
            │
            ▼
        Copilot API          ← GitHub 雲端 AI（streaming）
```

**每個 Telegram chat 對應一個獨立的 `CopilotSession`**，session 的 working directory 對應到所選專案的路徑。

---

## 專案結構

```
DClaw/
├── CopilotClawD/                    # 主程式（入口點）
│   ├── Program.cs                   # Host 建置、服務註冊、啟動流程
│   ├── Splash.cs                    # 啟動動畫（ASCII Art）
│   ├── appsettings.json             # 公用設定（Log level 等）
│   ├── appsettings.secret.json      # 機密設定（不納入版控）
│   └── appsettings.secret.example.json  # 設定範本
│
├── CopilotClawD.Core/               # 核心邏輯（無 UI 相依）
│   ├── Agents/
│   │   ├── AgentService.cs          # Session 管理、訊息路由、記憶注入
│   │   └── SystemPrompts.cs         # System prompt 組裝（專案資訊、記憶、工具說明）
│   ├── Configuration/
│   │   └── CopilotClawDConfig.cs    # 所有設定的 POCO 模型
│   ├── Memory/
│   │   ├── ISessionStore.cs / JsonFileSessionStore.cs   # Session 持久化
│   │   └── IMemoryStore.cs / JsonFileMemoryStore.cs     # 跨 Session 記憶持久化
│   ├── Registration/
│   │   └── RegistrationService.cs   # 自助註冊（密碼驗證、白名單寫入）
│   ├── Security/
│   │   ├── PermissionPolicy.cs      # 指令/路徑/MCP 危險程度評估
│   │   ├── SecretRedactor.cs        # AI 回覆機密遮蔽
│   │   └── IPermissionConfirmer.cs  # 危險操作確認介面
│   └── SelfUpdateService.cs         # 自我編譯重啟
│
├── CopilotClawD.Telegram/           # Telegram Bot 實作
│   ├── TelegramBotService.cs        # BackgroundService（polling、debounce）
│   ├── ServiceCollectionExtensions.cs  # DI 擴充方法
│   ├── TelegramPermissionConfirmer.cs  # Inline Keyboard 危險操作確認
│   ├── MarkdownV2Helper.cs          # MarkdownV2 跳脫工具
│   └── Handlers/
│       ├── CommandHandler.cs        # 所有 /指令 處理
│       └── MessageHandler.cs        # 一般對話 streaming 處理
│
└── CopilotClawD.slnx               # 方案檔
```

---

## 安全模型

所有 AI 發起的 Shell 指令、檔案讀寫、MCP 工具呼叫都經過 `PermissionPolicy` 評估，分為四個等級：

| 等級 | 行為 | 範例 |
|------|------|------|
| **Safe** | 直接允許 | 一般讀取操作 |
| **Normal** | 直接允許 | 一般寫入、git status 等 |
| **Dangerous** | 傳送 Telegram 確認按鈕，等待使用者允許或拒絕（逾時自動拒絕）| `rm -rf`、`git push --force` |
| **Forbidden** | 直接拒絕，不詢問 | 磁碟格式化、系統關機、挖礦程式 |

所有規則皆可在 `appsettings.secret.json` 的 `Security` 區段自訂。

`appsettings.secret.example.json` 已內建一組**建議的預設安全規則**，複製後請務必根據自己的使用情境審視並調整。

### Forbidden（直接禁止）

下列行為會被直接拒絕，不要求確認：

| 規則 | 涵蓋範圍 |
|------|---------|
| `format [a-z]:` | Windows 磁碟格式化 |
| `mkfs` / `fdisk` / `diskpart` / `diskutil eraseDisk` | Linux/macOS 磁碟操作 |
| `shutdown` / `reboot` / `init [06]` / `systemctl poweroff\|reboot\|halt` | 系統關機/重啟 |
| `xmrig` / `minergate` / `cpuminer` 等 | 已知挖礦程式 |
| `curl ... \| sh` / `wget ... \| sh` | 下載後直接執行（常見惡意腳本手法） |
| `Invoke-Expression` | PowerShell 任意程式碼執行 |
| `reg delete` / `Registry::` | Windows 登錄檔刪除 |
| `rm ... /` / `Remove-Item [A-Z]:\` | 刪除根目錄 |

### Dangerous（需確認）

下列操作會傳送 Telegram 確認按鈕，需你手動按「允許」才會執行：

| 規則 | 涵蓋範圍 |
|------|---------|
| `rm -r` / `rm -f` / `rmdir /s` / `del /s` / `rd /s` / `Remove-Item -Recurse` | 遞迴刪除 |
| `git push --force` / `git push -f` | 強制推送 |
| `git reset --hard` / `git clean -fdx` / `git checkout -- .` | 破壞性 git 操作 |
| `git branch -d\|-D` | 刪除分支 |
| `chmod` / `chown` / `icacls` / `takeown` | 修改檔案權限/擁有者 |
| `npm install -g` / `pip install` / `brew install` / `apt install` / `choco install` / `winget install` | 全域套件安裝 |
| `kill -9` / `taskkill` / `Stop-Process` | 強制終止 process |
| `sc stop\|delete\|config` / `systemctl stop\|disable` / `launchctl unload` | 系統服務操作 |
| `setx` / `$env:` / `export PATH` | 修改環境變數 |
| `Set-Content` / `Add-Content` / `Out-File` / `New-Item` / `Copy-Item` / `Move-Item` / `Rename-Item` / `mv` / `cp` / `tee` | PowerShell/Shell 寫入與搬移 |

> **注意：** `DangerousCommandPatterns` 的範圍相當廣，包含許多開發日常會用到的操作（如 `mv`、`cp`、`New-Item`）。建議依據自己的工作流程，將常用的安全操作加入 `SafeCommandPatterns` 白名單以減少確認次數。

### ProtectedPathPatterns（禁止 AI 讀寫的路徑）

下列路徑 AI 無法讀取或寫入：

| 路徑 | 說明 |
|------|------|
| `**/appsettings.secret.json` | Bot 自身機密設定 |
| `**/*.env` / `**/.env` / `**/.env.*` | 環境變數檔 |
| `**/credentials.json` / `**/credentials.yaml` / `**/secrets.json` / `**/secrets.yaml` | 常見 credential 檔 |
| `**/*_rsa` / `**/*_ed25519` / `**/id_rsa` / `**/id_ed25519` / `**/.ssh/config` | SSH 金鑰 |
| `**/.git-credentials` / `**/.netrc` / `**/token.json` | Git/HTTP 驗證憑證 |
| `C:/Windows/**` / `C:/Program Files/**` / `C:/Program Files (x86)/**` | Windows 系統目錄 |
| `/etc/passwd` / `/etc/shadow` / `/etc/sudoers` / `/etc/ssh/**` | Linux 系統帳號與 SSH |
| `/System/**` / `/System32/**` / `/Library/**` | macOS/Windows 系統目錄 |

### 建議的調整方向

在複製 `appsettings.secret.example.json` 後，建議考慮以下調整：

- **加入 `SafeCommandPatterns`**：將你確定安全的指令（例如 `dotnet run`、`npm run dev`）加入白名單，讓 AI 不需每次都確認
- **縮小 `DangerousCommandPatterns`**：移除你日常必用但不危險的規則（例如在測試環境下允許 `pip install`）
- **擴充 `ProtectedPathPatterns`**：加入你的專案中其他含機密的檔案路徑
- **`AdditionalSecrets`**：填入任何不想出現在 AI 回覆中的機密字串（API key 值等）

---

## 執行畫面

| | |
|:---:|:---:|
| ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-55-03.jpg?raw=true) | ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-55-00.jpg?raw=true) |
| ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-55-02.jpg?raw=true) | ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-54-58.jpg?raw=true) |
| ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-55-05.jpg?raw=true) | ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_12-55-07.jpg?raw=true) |
| ![](https://github.com/donma/BlogResource/blob/main/2569/photo_2026-04-10_13-06-26.jpg?raw=true) | |

---

## 注意事項

- **只能執行一個 instance**：同一個 Bot Token 同時只能有一個 polling 連線，多開會出現 Telegram 409 錯誤。啟動前請確認沒有殘存 process：
  ```powershell
  Stop-Process -Name CopilotClawD -Force -ErrorAction SilentlyContinue
  ```
- **`appsettings.secret.json` 不納入版控**：該檔案含有 Bot Token 等機密，請勿 commit。
- **Bot 在你的機器上執行**：AI 有能力讀寫你電腦上的檔案並執行 Shell 指令，請妥善設定 `AllowedUserIds` 只允許你自己使用。

---

## 授權

MIT License — 詳見 [LICENSE](../LICENSE)

本專案以 MIT 授權釋出，你可以自由使用、修改、散布，包含用於商業用途，惟須保留原始著作權聲明。

---

## 免責聲明

本專案為個人工具，按「現狀（AS IS）」提供，不附帶任何明示或暗示的保證。

**使用前請了解以下風險：**

- **本機執行風險**：Bot 運行於你的本機，AI 可讀寫檔案並執行 Shell 指令。設定不當可能導致非預期的檔案修改或系統操作。
- **AI 行為不可預測**：語言模型可能產生不正確、有誤導性或非預期的輸出，所有 AI 建議的操作皆應由使用者自行判斷。
- **安全規則非完整防護**：內建的 `ForbiddenCommandPatterns` 與 `DangerousCommandPatterns` 僅為輔助，無法保證攔截所有潛在危險操作，使用者應根據自身環境審慎調整。
- **第三方服務依賴**：本專案依賴 GitHub Copilot、Telegram Bot API 等第三方服務，其可用性、政策變更或費用由各服務方決定，與本專案無關。
- **機密資訊保護**：請勿將 `appsettings.secret.json` 提交至版控或分享給他人。`SecretRedactor` 提供盡力而為的遮蔽，無法保證在所有情況下完全防止機密洩漏。

作者不對因使用本專案所造成的任何直接或間接損失負責，包含但不限於資料遺失、系統損壞或服務中斷。
