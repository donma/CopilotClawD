using CopilotClawD;
using CopilotClawD.Core;
using CopilotClawD.Core.Agents;
using CopilotClawD.Core.Configuration;
using CopilotClawD.Core.Memory;
using CopilotClawD.Core.Security;
using CopilotClawD.Telegram;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// ─── Splash Screen ──────────────────────────────────────────────
await Splash.PlayAsync();
Splash.Status("CopilotClawD", "Initializing...", ConsoleColor.Yellow);

// ─── Host Builder ───────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);

// 載入 appsettings.secret.json（從 exe 同層目錄讀取）
var secretFilePath = Path.Combine(AppContext.BaseDirectory, "appsettings.secret.json");
if (!File.Exists(secretFilePath))
{
    Splash.Error($"找不到設定檔：{secretFilePath}");
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"  請將 appsettings.secret.json 放到以下目錄：");
    Console.WriteLine($"  {AppContext.BaseDirectory}");
    Console.WriteLine($"  可參考 appsettings.secret.example.json 範本。");
    Console.ResetColor();
    Environment.Exit(1);
}

builder.Configuration.AddJsonFile(secretFilePath, optional: false, reloadOnChange: true);

// 設定檔繫結
builder.Services.Configure<CopilotClawDConfig>(
    builder.Configuration.GetSection(CopilotClawDConfig.SectionName));

Splash.Success("Configuration loaded");

// ─── Core Services ──────────────────────────────────────────────

// GitHub Copilot SDK — CopilotClient
builder.Services.AddSingleton<CopilotClient>(sp =>
{
    var config = sp.GetRequiredService<IOptions<CopilotClawDConfig>>().Value;
    var logger = sp.GetRequiredService<ILogger<CopilotClient>>();

    var options = new CopilotClientOptions
    {
        Logger = logger,
        AutoStart = true,
        UseStdio = true
    };

    if (!string.IsNullOrWhiteSpace(config.CopilotCliPath))
        options.CliPath = config.CopilotCliPath;

    return new CopilotClient(options);
});

// Session Store（持久化 chatId → sessionId + 專案/模型設定）
builder.Services.AddSingleton<ISessionStore>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<JsonFileSessionStore>>();
    var filePath = Path.Combine(AppContext.BaseDirectory, "sessions.json");
    return new JsonFileSessionStore(filePath, logger);
});

// Memory Store（跨 Session AI 摘要記憶持久化 — Phase 8）
builder.Services.AddSingleton<IMemoryStore>(sp =>
{
    var config = sp.GetRequiredService<IOptions<CopilotClawDConfig>>().Value;
    var logger = sp.GetRequiredService<ILogger<JsonFileMemoryStore>>();
    var filePath = Path.Combine(AppContext.BaseDirectory, "memory.json");
    return new JsonFileMemoryStore(filePath, config.Memory.MaxEntries, logger);
});

// Agent Service（Copilot session 管理核心）
builder.Services.AddSingleton<AgentService>();

// Security（Phase 7）— 權限分級 + 機密防洩漏
builder.Services.AddSingleton<PermissionPolicy>();
builder.Services.AddSingleton<SecretRedactor>();

// Self-update Service（編譯新版本 → 啟動新 Process → 舊 Process 自行停止）
builder.Services.AddSingleton<CopilotClawD.Core.SelfUpdateService>();

Splash.Success("Core services registered");

// ─── IM Bot Modules ─────────────────────────────────────────────

// Telegram Bot
builder.Services.AddCopilotClawDTelegram();
Splash.Success("Telegram module registered");

// 未來擴充點：
// builder.Services.AddCopilotClawDDiscord();
// builder.Services.AddCopilotClawDLine();

// ─── Build & Start ──────────────────────────────────────────────

var app = builder.Build();

// 啟動 CopilotClient（連接 Copilot CLI）
Splash.Status("Copilot", "Starting Copilot CLI...", ConsoleColor.Cyan);
var copilotClient = app.Services.GetRequiredService<CopilotClient>();
await copilotClient.StartAsync();
Splash.Success("Copilot CLI connected");

// 顯示啟動完成
Console.WriteLine();
Splash.Status("CopilotClawD", "All systems online. Waiting for messages...", ConsoleColor.Green);
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine("  Press Ctrl+C to stop.");
Console.ForegroundColor = ConsoleColor.Gray;
Console.WriteLine();

// ─── Run ────────────────────────────────────────────────────────

await app.RunAsync();

// ─── Graceful Shutdown ──────────────────────────────────────────

Splash.Status("CopilotClawD", "Shutting down...", ConsoleColor.Yellow);

// 確保跨 Session 記憶的 dirty 資料已寫入磁碟
var memoryStore = app.Services.GetRequiredService<IMemoryStore>();
if (memoryStore is IAsyncDisposable disposableStore)
    await disposableStore.DisposeAsync();

await copilotClient.StopAsync();
Splash.Success("Goodbye!");
