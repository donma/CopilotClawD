namespace CopilotClawD.Core.Configuration;

public class CopilotClawDConfig
{
    public const string SectionName = "CopilotClawD";

    public string TelegramBotToken { get; set; } = string.Empty;

    /// <summary>
    /// 預設 AI 模型。例如 "gpt-4o", "gpt-5", "claude-sonnet-4.5"。
    /// </summary>
    public string DefaultModel { get; set; } = "gpt-4o";

    /// <summary>
    /// 白名單：只允許這些 Telegram User ID 使用 Bot。
    /// 可透過 RegistrationPasscode 動態新增。
    /// </summary>
    public List<long> AllowedUserIds { get; set; } = [];

    /// <summary>
    /// 管理員：允許執行 /update 等高危指令的 Telegram User ID。
    /// 若未設定，則 AllowedUserIds 中的第一個 ID 視為管理員。
    /// </summary>
    public List<long> AdminUserIds { get; set; } = [];

    /// <summary>
    /// 實際生效的管理員 ID 清單。
    /// 若 AdminUserIds 有設定，優先使用；否則 fallback 到 AllowedUserIds 的第一個 ID。
    /// </summary>
    public IReadOnlyList<long> EffectiveAdminIds =>
        AdminUserIds.Count > 0
            ? AdminUserIds
            : AllowedUserIds.Count > 0 ? [AllowedUserIds[0]] : [];

    /// <summary>
    /// 註冊密碼：未授權的使用者輸入此密碼即可自動加入白名單。
    /// 留空表示關閉自助註冊功能。
    /// </summary>
    public string RegistrationPasscode { get; set; } = string.Empty;

    /// <summary>
    /// 專案清單：Key = 專案代號，Value = 路徑 + 描述。
    /// </summary>
    public Dictionary<string, ProjectConfig> Projects { get; set; } = [];

    /// <summary>
    /// Copilot CLI 路徑（選填）。若未設定，使用 PATH 中的 copilot 或 SDK 預設值。
    /// </summary>
    public string? CopilotCliPath { get; set; }

    /// <summary>
    /// 安全設定：權限分級、危險指令攔截、保護路徑等。
    /// </summary>
    public SecurityConfig Security { get; set; } = new();

    /// <summary>
    /// 跨 Session 記憶設定。
    /// </summary>
    public MemoryConfig Memory { get; set; } = new();

    /// <summary>
    /// 暫存目錄：Bot 用來放截圖等暫存檔的資料夾路徑。
    /// 留空時預設為執行目錄下的 swap 子資料夾。
    /// </summary>
    public string SwapDirectory { get; set; } = string.Empty;

    /// <summary>
    /// MCP Server 清單。Key = server 名稱，Value = server 設定。
    /// 每個 session 建立時會自動連接這些 MCP server。
    /// </summary>
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = [];
}

public class ProjectConfig
{
    public string Path { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// 安全加固設定（Phase 7）。所有清單皆可在 appsettings.secret.json 中自訂。
/// </summary>
public class SecurityConfig
{
    /// <summary>
    /// 是否啟用權限分級攔截。false = 全部自動允許（舊行為）。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 危險操作確認的逾時秒數（超過即自動拒絕）。
    /// </summary>
    public int ConfirmationTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// 是否啟用 Token/Secret 防洩漏掃描。
    /// </summary>
    public bool RedactSecrets { get; set; } = true;

    /// <summary>
    /// 直接禁止的 Shell 指令 regex patterns（跨平台常見危險指令）。
    /// 匹配到 = 直接拒絕，不詢問使用者。
    /// </summary>
    public List<string> ForbiddenCommandPatterns { get; set; } =
    [
        // ── 磁碟格式化 / 分割區操作 ──
        @"(?i)\bformat\s+[a-z]:",                          // Windows: format C:
        @"(?i)\bmkfs\b",                                    // Linux: mkfs.*
        @"(?i)\bfdisk\b",                                   // Linux: fdisk
        @"(?i)\bdiskpart\b",                                // Windows: diskpart
        @"(?i)\bdiskutil\s+eraseDisk\b",                    // macOS: diskutil eraseDisk

        // ── 系統關機 / 重啟 ──
        @"(?i)\bshutdown\b",                                // Windows/Linux/macOS
        @"(?i)\breboot\b",                                  // Linux
        @"(?i)\binit\s+[06]\b",                             // Linux: init 0, init 6
        @"(?i)\bsystemctl\s+(poweroff|reboot|halt)\b",      // Linux systemd

        // ── 挖礦 / 惡意軟體 ──
        @"(?i)\b(xmrig|minergate|cpuminer|cgminer|bfgminer)\b",
        @"(?i)\bcurl\b.*\|\s*(ba)?sh\b",                    // curl ... | sh (遠端腳本執行)
        @"(?i)\bwget\b.*\|\s*(ba)?sh\b",                    // wget ... | sh
        @"(?i)\bInvoke-Expression\b",                       // PowerShell: iex

        // ── 登錄檔 / 系統設定破壞 ──
        @"(?i)\breg\s+delete\b",                            // Windows: reg delete
        @"(?i)\bRegistry\s*::",                             // PowerShell registry
        @"(?i)\bdscl\b",                                    // macOS: Directory Service

        // ── 全系統遞迴刪除（根目錄） ──
        @"(?i)\brm\s+.*\s+/\s*$",                          // rm ... /
        @"(?i)\bRemove-Item\s+.*[A-Z]:\\\s*$",             // PowerShell: Remove-Item C:\
    ];

    /// <summary>
    /// 需要使用者確認的 Shell 指令 regex patterns（危險但合理的操作）。
    /// 匹配到 = 發 Telegram 按鈕問使用者。
    /// </summary>
    public List<string> DangerousCommandPatterns { get; set; } =
    [
        // ── 刪除操作 ──
        @"(?i)\brm\s+-[rR]",                               // rm -r, rm -rf
        @"(?i)\brm\s+.*-[fF]",                             // rm -f, rm -rf
        @"(?i)\brmdir\s+/[sS]",                            // Windows: rmdir /s
        @"(?i)\bdel\s+/[sSfFqQ]",                          // Windows: del /s /f /q
        @"(?i)\bRemove-Item\s+.*-Recurse",                 // PowerShell
        @"(?i)\brd\s+/[sS]",                               // Windows: rd /s

        // ── Git 危險操作 ──
        @"(?i)\bgit\s+push\s+.*--force",                   // git push --force
        @"(?i)\bgit\s+push\s+-f\b",                        // git push -f
        @"(?i)\bgit\s+reset\s+--hard",                     // git reset --hard
        @"(?i)\bgit\s+clean\s+-[fdx]",                     // git clean -fd, -fx
        @"(?i)\bgit\s+checkout\s+--\s+\.",                  // git checkout -- . (discard all)
        @"(?i)\bgit\s+branch\s+-[dD]\b",                   // git branch -d/-D

        // ── 權限 / 擁有者變更 ──
        // chmod/chown 只在有 -R（遞迴）、777、setuid(4xxx) 或根路徑時才視為危險
        @"(?i)\bchmod\s+.*-[rR]\b",                         // chmod -R (遞迴)
        @"(?i)\bchmod\s+[0-7]*7[0-7][0-7]\b",              // chmod 777 / 775 / ...（world-writable）
        @"(?i)\bchmod\s+[0-7]*(4|2|6)[0-7]{3}\b",          // chmod 4755 / 2755（setuid/setgid）
        @"(?i)\bchmod\s+\S+\s+/(\s|$)",                    // chmod <mode> /（根目錄）
        @"(?i)\bchown\s+.*-[rR]\b",                         // chown -R (遞迴)
        @"(?i)\bchown\s+\S+\s+/(\s|$)",                    // chown <owner> /（根目錄）
        @"(?i)\bicacls\b",                                  // Windows
        @"(?i)\btakeown\b",                                 // Windows

        // ── 套件管理（安裝未知套件） ──
        @"(?i)\bnpm\s+install\s+-g\b",                     // npm install -g (全域安裝)
        @"(?i)\bpip\s+install\b",                          // pip install
        @"(?i)\bbrew\s+install\b",                         // macOS: brew install
        @"(?i)\bapt(-get)?\s+install\b",                   // Linux: apt install
        @"(?i)\byum\s+install\b",                          // Linux: yum install
        @"(?i)\bchoco\s+install\b",                        // Windows: chocolatey
        @"(?i)\bwinget\s+install\b",                       // Windows: winget

        // ── 服務 / 程序管理 ──
        @"(?i)\bkill\s+-9\b",                              // kill -9
        @"(?i)\btaskkill\b",                               // Windows: taskkill
        @"(?i)\bStop-Process\b",                           // PowerShell
        @"(?i)\bsc\s+(stop|delete|config)\b",              // Windows: sc stop/delete
        @"(?i)\bsystemctl\s+(stop|disable|mask)\b",        // Linux: systemctl stop/disable
        @"(?i)\blaunchctl\s+(unload|remove)\b",            // macOS

        // ── 環境變數 / PATH 修改 ──
        @"(?i)\bsetx\b",                                   // Windows: setx (永久環境變數)
        @"(?i)\b\$env:\w+\s*=",                            // PowerShell: $env:VAR = ... (賦值才危險，讀取 $env:TEMP 不攔截)
        @"(?i)\bexport\s+PATH\b",                         // Linux/macOS: export PATH=
    ];

    /// <summary>
    /// 禁止讀寫的路徑 patterns（glob 風格，case-insensitive）。
    /// 匹配到 = 直接拒絕讀取或寫入。
    /// </summary>
    public List<string> ProtectedPathPatterns { get; set; } =
    [
        // ── 機密檔案 ──
        "**/appsettings.secret.json",
        "**/*.env",
        "**/.env",
        "**/.env.*",
        "**/credentials.json",
        "**/credentials.yaml",
        "**/secrets.json",
        "**/secrets.yaml",
        "**/*_rsa",
        "**/*_ed25519",
        "**/id_rsa",
        "**/id_ed25519",
        "**/.ssh/config",
        "**/.git-credentials",
        "**/.netrc",
        "**/token.json",

        // ── Windows 系統目錄 ──
        "C:/Windows/**",
        "C:/Program Files/**",
        "C:/Program Files (x86)/**",

        // ── Linux/macOS 系統目錄 ──
        "/etc/passwd",
        "/etc/shadow",
        "/etc/sudoers",
        "/etc/ssh/**",
        "/System/**",
        "/Library/**",
    ];

    /// <summary>
    /// 額外需要掃描並遮蔽的機密字串（除了 TelegramBotToken 外）。
    /// </summary>
    public List<string> AdditionalSecrets { get; set; } = [];

    /// <summary>
    /// 安全白名單：符合這些 patterns 的 Shell 指令直接視為 Normal，
    /// 不會被 DangerousCommandPatterns 攔截。
    /// 用途：例如允許 AI 對 SwapDirectory 執行寫入指令而不需要每次確認。
    /// </summary>
    public List<string> SafeCommandPatterns { get; set; } = [];
}

/// <summary>
/// 跨 Session AI 摘要記憶設定（Phase 8）。
/// </summary>
public class MemoryConfig
{
    /// <summary>
    /// 是否啟用跨 Session 記憶功能。false = 不產生摘要、不注入記憶。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 每個 chatId 最多保留的記憶條目數量（FIFO 淘汰）。
    /// </summary>
    public int MaxEntries { get; set; } = 10;

    /// <summary>
    /// Session idle timeout（小時）。超過此時間沒有對話，自動產摘要並銷毀 session，
    /// 避免 Copilot server 端 session 失效但 bot 仍持有舊 session ID。
    /// 0 = 停用 idle timeout。
    /// </summary>
    public int SessionIdleTimeoutHours { get; set; } = 2;
}

/// <summary>
/// MCP Server 設定（Phase 6）。
/// Type = "local"（stdio）或 "http" / "sse"（遠端）。
/// </summary>
public class McpServerConfig
{
    /// <summary>
    /// Server 類型：「local」（stdio 子程序）、「http」、「sse」。預設 local。
    /// </summary>
    public string Type { get; set; } = "local";

    // ── local only ──────────────────────────────────────────────

    /// <summary>
    /// 執行 MCP server 的命令（local 模式）。例如 "npx" 或 "node"。
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    /// 傳給命令的參數（local 模式）。
    /// </summary>
    public List<string> Args { get; set; } = [];

    /// <summary>
    /// 設定給子程序的環境變數（local 模式）。
    /// </summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>
    /// 子程序的工作目錄（local 模式）。留空 = 繼承 Bot 的工作目錄。
    /// </summary>
    public string? Cwd { get; set; }

    // ── remote only ─────────────────────────────────────────────

    /// <summary>
    /// 遠端 MCP server 的 URL（http / sse 模式）。
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// 傳給遠端 server 的 HTTP headers（http / sse 模式）。
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    // ── both ────────────────────────────────────────────────────

    /// <summary>
    /// 工具呼叫逾時（毫秒）。留空 = 使用 SDK 預設值。
    /// </summary>
    public int? Timeout { get; set; }

    /// <summary>
    /// 允許的工具清單。["*"] = 允許所有工具；[] = 不允許任何工具；["tool1"] = 白名單。
    /// </summary>
    public List<string> Tools { get; set; } = ["*"];
}
