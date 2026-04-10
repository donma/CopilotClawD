# CopilotClawD

透過 Telegram 操控你本機程式碼的 AI Agent，由 GitHub Copilot SDK 驅動。

傳一條訊息給 Bot，它就能讀寫你的檔案、執行 Shell 指令、操作 Git、搜尋網路——全部在你的機器上本地執行。

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

### 2. 取得你的 Telegram User ID

向 [@userinfobot](https://t.me/userinfobot) 傳送任意訊息，它會回覆你的 User ID（數字）。

### 3. 設定 GitHub CLI 登入（或準備 GitHub Token）

```powershell
gh auth login
```

或在設定檔中填入 GitHub Personal Access Token（需有 Copilot 權限）。

### 4. 建立設定檔

複製範本並填入你的資訊：

```powershell
copy CopilotClawD\appsettings.secret.example.json CopilotClawD\appsettings.secret.json
```

編輯 `appsettings.secret.json`（最少必填欄位）：

```json
{
  "CopilotClawD": {
    "TelegramBotToken": "你的 Bot Token",
    "AllowedUserIds": [你的 User ID],
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
| `GitHubToken` | | GitHub PAT（留空則使用 `gh` CLI 登入的帳號）|
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

---

## 注意事項

- **只能執行一個 instance**：同一個 Bot Token 同時只能有一個 polling 連線，多開會出現 Telegram 409 錯誤。啟動前請確認沒有殘存 process：
  ```powershell
  Stop-Process -Name CopilotClawD -Force -ErrorAction SilentlyContinue
  ```
- **`appsettings.secret.json` 不納入版控**：該檔案含有 Bot Token 等機密，請勿 commit。
- **Bot 在你的機器上執行**：AI 有能力讀寫你電腦上的檔案並執行 Shell 指令，請妥善設定 `AllowedUserIds` 只允許你自己使用。
