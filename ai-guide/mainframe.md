# CopilotClawD — Architecture Mainframe

## 1. 專案概述

**CopilotClawD** 是一個跑在本機 Windows 上的 Telegram Bot AI Agent，透過 **GitHub Copilot SDK** 整合 GitHub Copilot（支援 GPT-4o、GPT-5、Claude 等模型），讓你可以在 Telegram 對話中操控本機多個程式碼專案。

### 核心理念

- 在 Telegram 上與 AI 互動，讓 AI 直接讀寫、搜尋你本機的程式碼
- 透過設定檔管理多個專案，隨時切換 working directory context
- **由 GitHub Copilot SDK 提供內建工具**：Shell 執行、檔案讀寫、Git 操作、URL 抓取、MCP 整合
- 所有對話由 Copilot SDK 管理（infinite session + context compaction），無需自行持久化
- 權限控制透過 `OnPermissionRequest` callback 處理，四級分類（Safe / Normal / Dangerous / Forbidden）

### 命名由來

CopilotClawD = GitHub Copilot + Claw（爪）+ D(onma)，就是一個簡易版本我自己夠用的OpenClaw概念產品。

### 架構演進

- Phase 1-3 最初使用 `Microsoft.Extensions.AI` + `Microsoft.Extensions.AI.OpenAI` + 自寫工具，但是放棄了發現過於肥大且架構複雜
- **Pivot**：改用 `GitHub.Copilot.SDK` 取代自寫工具 + AI Client，大幅簡化架構，讓 Copilot SDK 處理所有工具呼叫

---

## 2. 技術棧

| 層級 | 技術選擇 | NuGet 套件 |
|------|---------|------------|
| **運行時** | .NET 10 Console (Generic Host) | `Microsoft.Extensions.Hosting` |
| **Telegram** | Telegram.Bot SDK | `Telegram.Bot` |
| **AI 核心 + 工具** | GitHub Copilot SDK | `GitHub.Copilot.SDK` |
| **MCP** | Copilot SDK 內建 MCP 支援 | （含在 Copilot SDK 中） |
| **對話記憶** | Copilot SDK Infinite Session + 跨 Session AI 摘要記憶 | （含在 SDK 中 + CopilotClawD Memory Store） |
| **設定** | JSON 設定檔 | `Microsoft.Extensions.Configuration.Json` |
| **日誌** | Microsoft.Extensions.Logging | （含在 Hosting 中） |

### 關於 GitHub Copilot SDK

`GitHub.Copilot.SDK` 是 GitHub 官方的 .NET SDK，用於程式化控制 Copilot CLI：

- 透過 JSON-RPC (stdio) 與 Copilot CLI 子程序通訊
- **內建工具**：`shell`（Shell 執行）、`read`（檔案讀取）、`write`（檔案寫入）、`url`（URL 抓取）、`mcp`（MCP 工具呼叫）、`memory`（記憶管理）
- 支援 streaming、infinite session、session persistence、BYOK
- 透過 `OnPermissionRequest` callback 控制工具執行權限
- 支援多模型（gpt-4o、gpt-5、claude-sonnet 等）

**先決條件**：需要 GitHub Copilot 訂閱 + Copilot CLI 已安裝（SDK 會自動偵測）。

```csharp
// 初始化範例
await using var client = new CopilotClient(new CopilotClientOptions
{
    AutoStart = true,
    UseStdio = true,
    // 使用已登入的 GitHub CLI 使用者（gh auth login --web）
});

await using var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = "gpt-4o",
    Streaming = true,
    OnPermissionRequest = (req, inv) => HandlePermissionAsync(chatId, req, inv)
});
```

---

## 3. 專案結構

### 目錄配置

```
Root/                                         ← 方案根目錄
├── CopilotClawD.slnx                          ← 方案檔
├── ai-guide/
│   └── mainframe.md                           ← 本文件（AI agent 架構參考）
│
├── CopilotClawD/                              ← 主程式（Console App，唯一啟動入口）
│   ├── CopilotClawD.csproj
│   ├── Program.cs                             # Generic Host + DI + Splash + 啟動 CopilotClient
│   ├── Splash.cs                              # Console 啟動動畫（ASCII Art + 色彩 + 打字機效果）
│   ├── appsettings.json                       # 非敏感設定（Logging）
│   ├── appsettings.secret.json                # 敏感設定（Token、白名單）— gitignored，需放在 exe 同層
│   └── appsettings.secret.example.json        # 設定檔範本
│
├── CopilotClawD.Core/                         ← 核心邏輯（Class Library）
│   ├── CopilotClawD.Core.csproj
│   ├── Configuration/
│   │   └── CopilotClawDConfig.cs              # 整體設定 POCO（含 SecurityConfig、MemoryConfig）
│   ├── Agents/
│   │   ├── AgentService.cs                    # Copilot Session 管理 + 權限評估 + Session 持久化 + 跨 Session 記憶
│   │   └── SystemPrompts.cs                   # CopilotClawD 附加系統提示詞（Append 模式，含記憶注入）
│   ├── Memory/
│   │   ├── ISessionStore.cs                   # Session 持久化介面 + ChatSessionState 資料模型
│   │   ├── JsonFileSessionStore.cs            # JSON 檔案實作（sessions.json）
│   │   ├── IMemoryStore.cs                    # 跨 Session 記憶介面 + MemoryEntry 資料模型
│   │   └── JsonFileMemoryStore.cs             # JSON 檔案實作（memory.json，FIFO 淘汰）
│   ├── Security/
│   │   ├── PermissionPolicy.cs                # 工具呼叫權限分級引擎（Safe/Normal/Dangerous/Forbidden）
│   │   ├── IPermissionConfirmer.cs            # 危險操作確認介面（由 IM Bot 實作）
│   │   └── SecretRedactor.cs                  # Token/Secret 防洩漏掃描
│   ├── Registration/
│   │   └── RegistrationService.cs             # 自助註冊：驗證 passcode + 寫入 AllowedUserIds
│   └── SelfUpdateService.cs                   # 自我更新：重新編譯 + 啟動新 Process
│
└── CopilotClawD.Telegram/                     ← Telegram Bot 模組（Class Library）
    ├── CopilotClawD.Telegram.csproj
    ├── ServiceCollectionExtensions.cs         # AddCopilotClawDTelegram() 擴充方法
    ├── TelegramBotService.cs                  # BackgroundService：Bot polling + 白名單 + CallbackQuery
    ├── TelegramPermissionConfirmer.cs         # Inline Keyboard 確認危險操作
    ├── MarkdownV2Helper.cs                    # Telegram MarkdownV2 跳脫工具
    └── Handlers/
        ├── CommandHandler.cs                  # /start /help /projects /use /model /memory /clear /news /update
        └── MessageHandler.cs                  # 文字 → AgentService.ProcessMessageAsync → Streaming 回覆 + Secret 掃描
```

### 專案相依關係

```
CopilotClawD（主程式）→  CopilotClawD.Core
                       →  CopilotClawD.Telegram

CopilotClawD.Telegram  →  CopilotClawD.Core

CopilotClawD.Core      →  （無內部相依，依賴 GitHub.Copilot.SDK）
```

### 架構設計理念

- **CopilotClawD（主程式）** 是唯一的啟動入口（Console App），負責組合所有模組
- **各 IM Bot（Telegram、Discord、LINE...）** 是獨立的 Class Library，透過 `AddCopilotClawDXxx()` 擴充方法註冊
- 未來新增 IM Bot 只需：建立新 Class Library → 實作 `AddCopilotClawDXxx()` → 在 `Program.cs` 加一行
- 設定檔統一放在 exe 同層目錄，所有模組共用 `CopilotClawDConfig`

---

## 4. 核心模組設計

### 4.1 統一啟動入口（CopilotClawD/Program.cs）

`CopilotClawD` 是唯一的 Console App 啟動點，負責：

1. 播放 Splash 動畫
2. 確認 `appsettings.secret.json` 存在（從 `AppContext.BaseDirectory` 讀取，找不到時印錯誤並 `Exit(1)`）
3. 建立 Generic Host + 載入設定
4. 註冊 Core 服務（CopilotClient、ISessionStore、IMemoryStore、AgentService、PermissionPolicy、SecretRedactor、SelfUpdateService）
5. 註冊 IM Bot 模組（`builder.Services.AddCopilotClawDTelegram()`）
6. 啟動 CopilotClient（連接 Copilot CLI）+ 啟動 Host

```csharp
// CopilotClawD/Program.cs 結構
await Splash.PlayAsync();

var builder = Host.CreateApplicationBuilder(args);

// 從 exe 同層讀取，找不到時明確報錯
var secretFilePath = Path.Combine(AppContext.BaseDirectory, "appsettings.secret.json");
if (!File.Exists(secretFilePath)) { Splash.Error(...); Environment.Exit(1); }
builder.Configuration.AddJsonFile(secretFilePath, optional: false, reloadOnChange: true);

builder.Services.Configure<CopilotClawDConfig>(...);

// Core Services
builder.Services.AddSingleton<CopilotClient>(...);
builder.Services.AddSingleton<ISessionStore, JsonFileSessionStore>(...);
builder.Services.AddSingleton<IMemoryStore, JsonFileMemoryStore>(...);
builder.Services.AddSingleton<AgentService>();
builder.Services.AddSingleton<PermissionPolicy>();
builder.Services.AddSingleton<SecretRedactor>();
builder.Services.AddSingleton<SelfUpdateService>();

// IM Bots — 未來擴充只需加一行
builder.Services.AddCopilotClawDTelegram();
// builder.Services.AddCopilotClawDDiscord();

var app = builder.Build();
await copilotClient.StartAsync();
await app.RunAsync();
```

### 4.2 Splash（CopilotClawD/Splash.cs）

啟動時在 Console 顯示 ASCII Art 動畫，包含：

- **ClawIcon**：爪子 ASCII 圖示，逐行色彩漸變淡入
- **ClawArt**：CopilotClawD 大字 ASCII Art，逐字掃描效果
- **Tagline**：打字機效果逐字顯示
- **StartupInfo**：Runtime / PID / Time / OS 資訊

靜態方法：

| 方法 | 輸出格式 | 用途 |
|------|---------|------|
| `Splash.Status(label, msg, color)` | `[label] msg` | 各模組載入進度 |
| `Splash.Success(msg)` | `  ✓ msg` | 載入成功 |
| `Splash.Error(msg)` | `  ✗ msg` | 錯誤訊息 |

無真正 console 視窗時（`IsConsoleAvailable()` 偵測），自動跳過動畫，避免 `IOException`。

### 4.3 CopilotClawDConfig（Core/Configuration/CopilotClawDConfig.cs）

所有設定的 POCO 模型，對應 `appsettings.secret.json` 的 `CopilotClawD` 節點：

```csharp
public class CopilotClawDConfig
{
    public const string SectionName = "CopilotClawD";

    public string TelegramBotToken { get; set; }        // 必填
    public List<long> AllowedUserIds { get; set; }      // 必填；RegistrationService 可動態新增
    public List<long> AdminUserIds { get; set; }        // 管理員（預設為 AllowedUserIds 第一位）
    public string RegistrationPasscode { get; set; }    // 留空則關閉自助註冊
    public string DefaultModel { get; set; }
    public string? CopilotCliPath { get; set; }         // null = SDK 自動尋找
    public string SwapDirectory { get; set; }           // 暫存檔目錄（截圖等）
    public Dictionary<string, ProjectConfig> Projects { get; set; }
    public Dictionary<string, McpServerConfig> McpServers { get; set; }
    public SecurityConfig Security { get; set; }
    public MemoryConfig Memory { get; set; }
}

public class ProjectConfig
{
    public string Path { get; set; }
    public string Description { get; set; }
}

public class SecurityConfig
{
    public bool Enabled { get; set; } = true;
    public int ConfirmationTimeoutSeconds { get; set; } = 60;
    public bool RedactSecrets { get; set; } = true;
    public List<string> ForbiddenCommandPatterns { get; set; }
    public List<string> DangerousCommandPatterns { get; set; }
    public List<string> SafeCommandPatterns { get; set; }
    public List<string> ProtectedPathPatterns { get; set; }
    public List<string> AdditionalSecrets { get; set; }
}

public class MemoryConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxEntries { get; set; } = 10;
    public int SessionIdleTimeoutHours { get; set; } = 2;
}
```

### 4.4 AgentService（Core/Agents/AgentService.cs）

每個 Telegram chatId 對應一個 `CopilotSession`，使用 `ConcurrentDictionary` 管理：

```csharp
public class AgentService : IAsyncDisposable
{
    // chatId → (CopilotSession, SemaphoreSlim 防並行)
    private readonly ConcurrentDictionary<long, ChatSession> _sessions = new();

    public async IAsyncEnumerable<StreamChunk> ProcessMessageAsync(long chatId, string message, ...)
    {
        var session = await GetOrCreateSessionAsync(chatId);

        // Channel<StreamChunk> 橋接事件回呼 → IAsyncEnumerable
        var channel = Channel.CreateUnbounded<StreamChunk>();

        session.On(evt => {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    channel.Writer.TryWrite(new StreamChunk(Delta, delta.Data.DeltaContent));
                    break;
                case ToolExecutionStartEvent tool:
                    channel.Writer.TryWrite(new StreamChunk(ToolStart, tool.ToolName));
                    break;
                case SessionIdleEvent:
                    channel.Writer.TryComplete();
                    break;
            }
        });

        await session.SendAsync(new MessageOptions { Prompt = message });

        await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
            yield return chunk;
    }
}
```

#### GetOrCreateSessionAsync 流程

```
收到 chatId 訊息
  ├─ 記憶體有 session → 直接使用
  ├─ 記憶體無 session + ISessionStore 有 sessionId
  │   ├─ ResumeSessionAsync(storedSessionId) 嘗試恢復
  │   └─ 恢復失敗 → 建立新 session
  └─ 無 store 記錄 → CreateSessionAsync

建立/恢復 session 時：
  ├─ IMemoryStore.GetAsync(chatId) → 載入歷史記憶
  ├─ SystemPrompts.Build(project, memories) → 注入 SystemMessage（Append 模式）
  ├─ SessionConfig.WorkingDirectory = 當前專案路徑
  ├─ SessionConfig.Model = ModelOverride ?? DefaultModel
  └─ OnPermissionRequest = (req, inv) => HandlePermissionRequestAsync(chatId, req, inv)
```

### 4.5 Permission Handling（Core/Security/PermissionPolicy.cs）

`OnPermissionRequest` callback 在每次工具執行前觸發，透過 `PermissionPolicy` 進行四級評估：

| 等級 | 處理方式 | 範例 |
|------|---------|------|
| **Safe** | 自動允許，不記錄 | `read`（非保護路徑）、`url` |
| **Normal** | 自動允許 + 記錄日誌 | `write`（非保護路徑）、`shell`（一般指令）、`mcp` |
| **Dangerous** | Telegram Inline Keyboard 確認 | `shell`（rm -rf、git push --force、chmod、pip install...） |
| **Forbidden** | 直接拒絕 | `shell`（format、shutdown、挖礦）、`read/write`（appsettings.secret.json、.env、SSH keys） |

支援的 `PermissionRequest` 子類型：

| 類型 | 關鍵屬性 |
|------|---------|
| `PermissionRequestShell` | `FullCommandText`、`Intention`、`Warning` |
| `PermissionRequestWrite` | `FileName`、`Diff`、`NewFileContents` |
| `PermissionRequestRead` | `Path`、`Intention` |
| `PermissionRequestUrl` | `Url`、`Intention` |
| `PermissionRequestMcp` | `ToolName`、`ServerName`、`Args` |
| `PermissionRequestCustomTool` | `ToolName`、`Args` |

#### Telegram Inline Keyboard 確認流程（TelegramPermissionConfirmer）

```
AI 呼叫危險工具 → PermissionPolicy 判定 Dangerous
  ├─ TelegramPermissionConfirmer.ConfirmAsync(chatId, description, timeout)
  │   ├─ 發送 InlineKeyboardMarkup：
  │   │   「⚠️ 危險操作
  │   │    shell: rm -rf /tmp/old-build
  │   │    [✅ 允許]  [❌ 拒絕]」
  │   ├─ TaskCompletionSource<bool> 等待使用者按按鈕
  │   ├─ CancellationTokenSource 實作 timeout（預設 60 秒）
  │   └─ TelegramBotService.HandleCallbackQueryAsync → TryHandleCallback(callbackData)
  ├─ 使用者按「允許」→ Approved → 工具執行
  ├─ 使用者按「拒絕」→ DeniedInteractivelyByUser → AI 收到拒絕訊息
  └─ Timeout → 自動拒絕
```

#### Secret 防洩漏（SecretRedactor）

`SecretRedactor` 在 `MessageHandler` 的所有輸出路徑（`TryEditMessageAsync`、`SendOrSplitAsync`）掃描並替換已知機密（TelegramBotToken、RegistrationPasscode、AdditionalSecrets）為 `[REDACTED]`。由長到短排序替換，避免部分匹配問題。

### 4.6 Streaming 回覆（Telegram/Handlers/MessageHandler.cs）

```
使用者傳訊息 → 3 秒 Debounce（合併連續訊息）→ MessageHandler
  ├─ 白名單檢查：不在白名單 → 嘗試當作 RegistrationPasscode
  ├─ 送出 "thinking..." 佔位訊息
  ├─ await foreach (chunk in AgentService.ProcessMessageAsync(...))
  │   ├─ Delta → 累積到 buffer，每 ~1.5 秒 EditMessage 更新（避免 rate limit）
  │   ├─ ToolStart → 若無文字回覆，顯示 "working... [Tool: xxx]"
  │   └─ Error → 顯示錯誤訊息
  ├─ SecretRedactor 掃描最終回覆
  └─ 最終 EditMessage 送出完整回覆（超過 4096 字元時自動切割）
```

### 4.7 自助註冊（Core/Registration/RegistrationService.cs）

讓新使用者無需管理員手動改設定檔，透過 Passcode 自助加入白名單：

1. 管理員在設定檔設定 `RegistrationPasscode`（留空則關閉）
2. 未在白名單的使用者傳訊息時，Bot 將訊息當作 passcode 比對
3. 比對正確 → 加入記憶體白名單 + 寫回 `appsettings.secret.json` 的 `AllowedUserIds`，**即時生效不需重啟**
4. 比對錯誤 → **靜默忽略**，不給任何回應（避免暴露機制）

```csharp
public enum RegistrationResult
{
    Success,           // passcode 正確，已加入白名單
    AlreadyRegistered, // 已在白名單
    WrongPasscode,     // 密碼錯誤（靜默）
    Disabled           // RegistrationPasscode 未設定（靜默）
}
```

### 4.8 多專案管理

所有個人化設定集中於 `appsettings.secret.json`（gitignored，放在 exe 同層目錄）。`appsettings.json` 只保留與環境無關的 `Logging` 設定。

每個 Telegram chat 獨立維護：
- `ActiveProjectKey`：當前使用的專案（key in `Projects`）
- `ModelOverride`：覆蓋預設模型

切換專案或模型時，先產生 AI 摘要記憶（`DestroyWithMemoryAsync`），再銷毀舊 session，下次對話建立新 session（新 SystemPrompt + WorkingDirectory）。

### 4.9 Telegram 指令表

| 指令 | 功能 | 備註 |
|------|------|------|
| `/start` | 歡迎訊息 + 使用說明 | 未授權使用者也會顯示 |
| `/help` | 同 `/start` | |
| `/projects` | 列出所有專案（含 active 標記） | |
| `/use <name>` | 切換到指定專案（case-insensitive） | 觸發記憶生成 + 銷毀舊 session |
| `/model` | 顯示目前 AI 模型及可用模型清單 | 無參數 |
| `/model <name>` | 切換 AI 模型 | 觸發記憶生成 + 銷毀舊 session |
| `/memory` | 顯示所有跨 Session 記憶條目 | 含時間、專案、觸發原因 |
| `/memory clear` | 清除所有跨 Session 記憶 | |
| `/clear` | 銷毀 Session + 清除專案/模型選擇 | 記憶**保留**，需 `/memory clear` 清除 |
| `/news <keyword>` | 搜尋最新新聞並翻譯成繁中 | 透過 AI + shell/url 工具 |
| `/update` | 重新編譯並重啟 Bot | 管理員限定（AdminUserIds） |
| 直接打字 | 送入 AI Agent 處理 | 3 秒 Debounce 合併連續訊息 |

### 4.10 Session 持久化（Core/Memory/ISessionStore）

Bot 重啟後可恢復每個 chat 的 session 狀態：

```
ISessionStore 介面
  ├─ GetAsync(chatId) → ChatSessionState?
  ├─ SaveAsync(chatId, state)
  ├─ DeleteAsync(chatId)
  └─ GetAllAsync() → IReadOnlyDictionary<long, ChatSessionState>

ChatSessionState
  ├─ SessionId          ← Copilot SDK session ID（用於 ResumeSessionAsync）
  ├─ ActiveProjectKey   ← 該 chat 的 active project
  ├─ ModelOverride      ← 該 chat 的 model override
  ├─ CreatedAt
  └─ LastActiveAt

JsonFileSessionStore（實作）
  └─ 持久化至 {AppContext.BaseDirectory}/sessions.json
  └─ 記憶體快取 + SemaphoreSlim 防並行寫入
```

**AgentService Session 生命週期：**

| 時機 | 動作 |
|------|------|
| 啟動時 | `EnsureStoreLoadedAsync()`：從 store 還原所有 chat 的 ActiveProjectKey / ModelOverride |
| 收到訊息 | `GetOrCreateSessionAsync`：嘗試 Resume → 失敗才 Create |
| AI 回覆完成 | 更新 `LastActiveAt` |
| `/use` / `/model` | `DestroyWithMemoryAsync` → 產生摘要 → 銷毀 session → 更新 store |
| `/clear` | `DestroyWithMemoryAsync` → 從 store 完全刪除（記憶庫保留） |
| 正常關閉 | `DisposeAsync` → 對所有活躍 session 產生摘要記憶 |

### 4.11 跨 Session AI 摘要記憶（Core/Memory/IMemoryStore）

解決 Session 切換後 AI 遺忘所有先前對話 context 的問題。Session 銷毀前，AI 自動產生對話摘要，下次建立 Session 時注入 SystemPrompt。

#### 記憶資料模型

```csharp
public class MemoryEntry
{
    public DateTimeOffset CreatedAt { get; set; }
    public string Trigger { get; set; }      // SessionSwitch | SessionClear | GracefulShutdown
    public string? ProjectKey { get; set; }  // 結束時的專案
    public string? Model { get; set; }       // 結束時的模型
    public string Summary { get; set; }      // AI 產生的摘要（2-5 句話）
}
```

#### 完整流程

```
Session 即將銷毀（/clear、/use、/model、正常關閉）
  └─ DestroyWithMemoryAsync(chatId, trigger)
      ├─ MemoryConfig.Enabled == false → 直接銷毀，跳過
      └─ GenerateAndSaveMemoryAsync(chatId, session, trigger)
          ├─ 發送 summarization prompt 給現有 session
          │   「請用 2-5 句話摘要此對話。若無實質內容回覆 EMPTY」
          ├─ 訂閱 AssistantMessageDeltaEvent 收集回覆（30 秒 timeout）
          ├─ 過濾：回覆為 "EMPTY" 或 < 20 字元 → 不儲存
          └─ IMemoryStore.AddAsync(chatId, MemoryEntry)
              └─ FIFO 淘汰：超過 MaxEntries 時移除最舊條目

新 Session 建立時（下次收到訊息）
  └─ GetOrCreateSessionAsync(chatId)
      ├─ IMemoryStore.GetAsync(chatId) → 載入歷史記憶
      └─ SystemPrompts.Build(project, memories)
          └─ 每條記憶格式化為：
             「[Memory N] (2 hours ago | Project: my-project | Model: gpt-5-mini)
               摘要內容...」
```

#### 記憶觸發時機

| 觸發事件 | Trigger 值 | 記憶是否保留 |
|----------|-----------|------------|
| `/clear` | `SessionClear` | 保留（需 `/memory clear` 清除） |
| `/use <project>` | `SessionSwitch` | 保留 |
| `/model <name>` | `SessionSwitch` | 保留 |
| Bot 正常關閉 | `GracefulShutdown` | 保留 |
| Session 損壞 | （不觸發） | — |

#### 持久化

- `JsonFileMemoryStore`：JSON 檔案（`{AppContext.BaseDirectory}/memory.json`）
- Per-chatId `List<MemoryEntry>`，SemaphoreSlim 防並行寫入
- FIFO 淘汰：超過 `MaxEntries`（預設 10）時移除最舊條目

### 4.12 SystemPrompts（Core/Agents/SystemPrompts.cs）

Copilot SDK 已有完整的 coding agent system prompt，CopilotClawD 只需 **Append** 額外指令：

```csharp
new SystemMessageConfig
{
    Mode = SystemMessageMode.Append,  // 不覆蓋 Copilot 內建 prompt
    Content = SystemPrompts.Build(currentProject, memories)
}
```

附加內容包含：

1. CopilotClawD 身份說明（Telegram Bot）
2. 回覆格式指引（簡潔、繁中預設、Markdown）
3. 目前專案 context（路徑、描述）
4. **跨 Session 記憶**：歷史對話摘要，含時間、專案、模型資訊

### 4.13 MCP Server 支援

透過設定檔即可連接外部 MCP Server：

```json
"McpServers": {
  "github": {
    "Type": "http",
    "Url": "https://api.githubcopilot.com/mcp/",
    "Headers": { "Authorization": "Bearer <token>" },
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

`SessionConfig.McpServers` 在建立/恢復 session 時傳入，由 Copilot SDK 管理連線。

### 4.14 SelfUpdateService（Core/SelfUpdateService.cs）

`/update` 指令（管理員限定）觸發：

1. `dotnet build` 重新編譯目前專案
2. 啟動新版本 Process（從 `AppContext.BaseDirectory` 的新 exe 啟動）
3. 舊 Process 延遲後自行 `Environment.Exit(0)`

---

## 5. 設定檔參考

### appsettings.secret.json 完整範例

```json
{
  "CopilotClawD": {
    "TelegramBotToken": "<從 @BotFather 取得>",
    "AllowedUserIds": [123456789],
    "AdminUserIds": [123456789],
    "RegistrationPasscode": "",
    "DefaultModel": "gpt-5-mini",
    "SwapDirectory": "C:\\Users\\you\\CopilotClawD\\swap",
    "Projects": {
      "myproject": {
        "Path": "C:\\Users\\you\\Source\\MyProject",
        "Description": "我的專案"
      }
    },
    "McpServers": {},
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
    }
  }
}
```

---

## 6. 安全性設計

### 6.1 存取控制層

| 層次 | 機制 | 說明 |
|------|------|------|
| **Telegram 層** | User ID 白名單 | 只有 `AllowedUserIds` 可互動；非白名單靜默忽略 |
| **自助註冊** | Passcode 比對 | 管理員設定密碼，使用者自助加入白名單 |
| **工具執行層** | PermissionPolicy | 四級分類：Safe/Normal/Dangerous/Forbidden |
| **路徑保護** | glob 匹配 | 禁止 AI 讀寫 secret 檔案、SSH keys、系統目錄 |
| **輸出層** | SecretRedactor | AI 回覆中的 Token/Secret 自動替換為 `[REDACTED]` |

### 6.2 PermissionPolicy 評估邏輯

```
PermissionRequestShell → EvaluateShellCommand(command)
  ├─ 符合 ForbiddenCommandPatterns → Forbidden
  ├─ 符合 SafeCommandPatterns（白名單）→ Safe（優先級高於 Dangerous）
  ├─ 符合 DangerousCommandPatterns → Dangerous
  └─ 其他 → Normal

PermissionRequestRead/Write → EvaluateRead/Write(path)
  ├─ 符合 ProtectedPathPatterns → Forbidden
  └─ 其他 → Safe（Read）/ Normal（Write）

PermissionRequestUrl → Safe
PermissionRequestMcp → Normal
```

### 6.3 機密資訊保護

- `appsettings.secret.json` 列於 `.gitignore`
- `appsettings.secret.example.json` 作為公開範本（不含真實值）
- `ProtectedPathPatterns` 預設包含：`**/appsettings.secret.json`、`**/.env`、`**/id_rsa`、`**/id_ed25519`、`**/.ssh/config`、`C:/Windows/**` 等
- `SecretRedactor` 由長到短排序替換，避免部分字串匹配覆蓋問題

---

## 7. 實作階段

### Phase 1 — 基礎骨架 ✅

- [x] 建立 `CopilotClawD.Core`、`CopilotClawD.Telegram` 兩個專案
- [x] `CopilotClawD.Telegram` 使用 Generic Host + `BackgroundService`
- [x] 載入 `appsettings.json` + `appsettings.secret.json`
- [x] 實作 `TelegramBotService`：啟動 Bot polling
- [x] 實作基本 echo：收到訊息回傳
- [x] 實作 Telegram User ID 白名單驗證

### Phase 2 — AI 對話整合 ✅（已重構為 Copilot SDK）

- [x] ~~`Microsoft.Extensions.AI` + `Microsoft.Extensions.AI.OpenAI`~~ → 改用 `GitHub.Copilot.SDK`
- [x] 實作 `AgentService`（使用 CopilotSession）
- [x] 實作 `MessageHandler`：streaming 回覆（Channel 橋接事件）
- [x] 實作 `/model` 指令
- [x] 實作 `SystemPrompts`（Append 模式）

### Phase 3 — Tool Calling ✅（由 Copilot SDK 內建）

- [x] ~~自寫 FileSystemTools、ShellTools、GitTools~~ → **Copilot SDK 內建 tools 取代**
- [x] 實作 `OnPermissionRequest` 權限處理
- [x] ToolExecutionStart/Complete 事件通知顯示在 Telegram

### Phase 4 — 多專案管理 ✅

- [x] 每個 chat 獨立的 active project 選擇（`ConcurrentDictionary<long, string>`）
- [x] `/projects` 指令：列出所有專案（含 `(active)` 標記）
- [x] `/use <name>` 指令：切換目前專案（case-insensitive）
- [x] `/model <name>` 指令：切換模型

### Phase 5 — Session 持久化 ✅

- [x] `ISessionStore` 介面 + `JsonFileSessionStore` 實作（`sessions.json`）
- [x] `ChatSessionState`：SessionId、ActiveProjectKey、ModelOverride、CreatedAt、LastActiveAt
- [x] `GetOrCreateSessionAsync`：先嘗試 `ResumeSessionAsync` → 失敗才 `CreateSessionAsync`
- [x] 切換專案/模型時更新 store
- [x] `/clear` 從 store 完全刪除
- [x] 啟動時 `EnsureStoreLoadedAsync()` 還原所有 chat 的設定

### Phase 6 — MCP 支援 ✅（Copilot SDK 內建）

- [x] `McpServerConfig` POCO 類別（Type、Url/Command、Args、Headers、Tools）
- [x] `CopilotClawDConfig.McpServers` 設定區段
- [x] 建立/恢復 session 時傳入 `SessionConfig.McpServers`
- [x] `OnPermissionRequest` 針對 `PermissionRequestMcp` 記錄 ToolName、ServerName

### Phase 7 — 安全加固 ✅

- [x] `PermissionPolicy`：四級分類（Safe/Normal/Dangerous/Forbidden）+ compiled regex 快取
- [x] `SecurityConfig`：ForbiddenCommandPatterns、DangerousCommandPatterns、SafeCommandPatterns、ProtectedPathPatterns
- [x] `TelegramPermissionConfirmer`：Inline Keyboard 確認 + TaskCompletionSource + timeout
- [x] `TelegramBotService`：支援 `UpdateType.CallbackQuery`
- [x] `SecretRedactor`：Token/Secret 防洩漏掃描
- [x] `SelfUpdateService`：`/update` 重新編譯並重啟
- [x] 修復 `SelfUpdateService` 模糊參考

### Phase 8 — 跨 Session AI 摘要記憶 ✅

- [x] `IMemoryStore` 介面 + `MemoryEntry` 資料模型
- [x] `JsonFileMemoryStore`：`memory.json`，FIFO 淘汰，SemaphoreSlim
- [x] `SystemPrompts.Build()` 新增 memories 參數，`BuildMemorySection()` 格式化
- [x] `DestroyWithMemoryAsync`：先產生摘要再銷毀 session
- [x] `GenerateAndSaveMemoryAsync`：30 秒 timeout，過濾 EMPTY / < 20 字元
- [x] 在 `/clear`、`/use`、`/model`、`DisposeAsync` 中觸發記憶生成
- [x] `/memory` 指令：顯示 / 清除記憶
- [x] `MemoryConfig`：Enabled、MaxEntries、SessionIdleTimeoutHours

### Phase 9 — 自助註冊 ✅

- [x] `RegistrationService`：passcode 比對 + 反寫 `appsettings.secret.json`
- [x] `CopilotClawDConfig.RegistrationPasscode`
- [x] 比對錯誤靜默忽略（不暴露機制）
- [x] `IOptionsMonitor<CopilotClawDConfig>` 支援 hot-reload（設定變更不需重啟）
- [x] `SecretRedactor` 遮蔽 RegistrationPasscode

### Phase 10 — 啟動健壯性 ✅

- [x] `appsettings.secret.json` 改從 `AppContext.BaseDirectory` 讀取（固定到 exe 同層目錄）
- [x] 找不到設定檔時明確報錯（`Splash.Error` + 提示路徑 + `Environment.Exit(1)`）
- [x] `optional: false`：設定檔存在但格式錯誤時也會明確拋錯
- [x] `Splash.cs`：`IsConsoleAvailable()` 偵測，無 console 環境自動跳過動畫（避免 `IOException`）

---

## 8. 未來擴展方向

- **多 IM Bot**：新增 Discord / LINE / Slack Bot 模組，各自實作 `AddCopilotClawDXxx()` 擴充方法
- **Custom Tools**：透過 `SessionConfig.Tools` 或 `IDClawTool` 介面註冊自定義 C# 工具
- **Telegram UX 改善**：Inline Keyboard 選專案/模型、進度條、更好的 Markdown 渲染
- **多媒體支援**：接收/處理圖片（截圖分析）、檔案附件（上傳 code file）
- **Web UI**：除 IM Bot 外增加 Web 介面
- **記憶增強**：語意搜尋記憶、記憶分類（偏好/決策/進度）、相似條目自動合併
- **自動化排程**：定時任務（code review、跑測試、git pull 並通知結果）
- **RAG**：為大型專案建立程式碼向量索引
- **監控儀表板**：操作歷史記錄、每日摘要通知、錯誤自動回報
