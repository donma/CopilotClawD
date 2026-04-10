using System.Text;
using CopilotClawD.Core;
using CopilotClawD.Core.Agents;
using CopilotClawD.Core.Configuration;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace CopilotClawD.Telegram.Handlers;

public class CommandHandler
{
    private readonly AgentService _agentService;
    private readonly IOptions<CopilotClawDConfig> _config;
    private readonly MessageHandler _messageHandler;
    private readonly SelfUpdateService _selfUpdate;

    public CommandHandler(
        AgentService agentService,
        IOptions<CopilotClawDConfig> config,
        MessageHandler messageHandler,
        SelfUpdateService selfUpdate)
    {
        _agentService = agentService;
        _config = config;
        _messageHandler = messageHandler;
        _selfUpdate = selfUpdate;
    }

    public async Task HandleAsync(ITelegramBotClient botClient, Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var text = message.Text ?? string.Empty;

        // 取出指令（去掉 @BotName 後綴）與參數
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].Split('@')[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1].Trim() : null;

        await (command switch
        {
            "/start"    => HandleStartAsync(botClient, chatId, ct),
            "/help"     => HandleHelpAsync(botClient, chatId, ct),
            "/model"    => HandleModelAsync(botClient, chatId, argument, ct),
            "/clear"    => HandleClearAsync(botClient, chatId, ct),
            "/projects" => HandleProjectsAsync(botClient, chatId, ct),
            "/use"      => HandleUseAsync(botClient, chatId, argument, ct),
            "/memory"   => HandleMemoryAsync(botClient, chatId, argument, ct),
            "/news"     => HandleNewsAsync(botClient, chatId, argument, ct),
            "/status"   => HandleStatusAsync(botClient, chatId, ct),
            "/mcp"      => HandleMcpAsync(botClient, chatId, ct),
            "/update"   => HandleUpdateAsync(botClient, chatId, message.From?.Id ?? 0, ct),
            _           => HandleUnknownAsync(botClient, chatId, command, ct)
        });
    }

    private async Task HandleStartAsync(ITelegramBotClient botClient, long chatId, CancellationToken ct)
    {
        var active = await _agentService.GetActiveProjectAsync(chatId);
        var projectLine = active is not null
            ? $"目前專案：`{EscapeMarkdownV2(active.Value.Key)}`"
            : "尚未選擇專案";

        var welcome = $"*CopilotClawD* \\- 你的本機 AI Agent \\(Copilot SDK\\)\n"
            + "\n"
            + "透過 Telegram 與 AI 互動，直接操作你本機的程式碼專案\\.\n"
            + "由 GitHub Copilot SDK 驅動，內建檔案讀寫、Shell 執行、Git 操作等能力\\.\n"
            + "\n"
            + $"{projectLine}\n"
            + "\n"
            + "直接傳送文字即可與 AI 對話\\.\n"
            + "輸入 /help 查看所有可用指令\\.";

        await botClient.SendMessage(
            chatId: chatId,
            text: welcome,
            parseMode: ParseMode.MarkdownV2,
            cancellationToken: ct);
    }

    private static Task HandleHelpAsync(ITelegramBotClient botClient, long chatId, CancellationToken ct)
    {
        var help = "*指令列表：*\n"
            + "\n"
            + "/help \\- 顯示此說明\n"
            + "/projects \\- 列出並切換專案\n"
            + "/model \\- 顯示並切換 AI 模型\n"
            + "/status \\- 目前 session 狀態\n"
            + "/mcp \\- 列出已連接的 MCP server\n"
            + "/memory \\- 查看跨 Session 記憶\n"
            + "/memory clear \\- 清除所有記憶\n"
            + "/clear \\- 清除對話（銷毀 Session）\n"
            + "/news `<關鍵字>` \\- 搜尋最新新聞並翻譯成繁中\n"
            + "/update \\- 重新編譯並重啟 Bot\n"
            + "\n"
            + "直接傳送文字即可與 AI 對話\\.";

        return botClient.SendMessage(
            chatId: chatId,
            text: help,
            parseMode: ParseMode.MarkdownV2,
            cancellationToken: ct);
    }

    // ─── /projects ──────────────────────────────────────────────

    private async Task HandleProjectsAsync(ITelegramBotClient botClient, long chatId, CancellationToken ct)
    {
        var projects = _agentService.GetAllProjects();
        if (projects.Count == 0)
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: "尚未設定任何專案。請在 appsettings.json 的 CopilotClawD.Projects 中加入專案。",
                cancellationToken: ct);
            return;
        }

        var active = await _agentService.GetActiveProjectAsync(chatId);

        // 每個專案一個按鈕（active 的加 ✓ 標記）
        var buttons = projects.Select(kv =>
        {
            var isActive = active is not null && active.Value.Key == kv.Key;
            var label = isActive ? $"✓ {kv.Key}" : kv.Key;
            return new[] { InlineKeyboardButton.WithCallbackData(label, $"proj:{kv.Key}") };
        }).ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("*專案列表*");
        sb.AppendLine();
        foreach (var (key, config) in projects)
        {
            var isActive = active is not null && active.Value.Key == key;
            var marker = isActive ? " _\\(active\\)_" : "";
            sb.AppendLine($"`{EscapeMarkdownV2(key)}`{marker}");
            sb.AppendLine(EscapeMarkdownV2(config.Description));
            sb.AppendLine();
        }
        sb.Append("按下按鈕切換專案：");

        await botClient.SendMessage(
            chatId: chatId,
            text: sb.ToString(),
            parseMode: ParseMode.MarkdownV2,
            replyMarkup: new InlineKeyboardMarkup(buttons),
            cancellationToken: ct);
    }

    // ─── /use <name> ────────────────────────────────────────────

    private async Task HandleUseAsync(ITelegramBotClient botClient, long chatId, string? projectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
        {
            // 無參數：顯示目前 active project
            var active = await _agentService.GetActiveProjectAsync(chatId);
            if (active is not null)
            {
                await botClient.SendMessage(
                    chatId: chatId,
                    text: $"目前專案：`{EscapeMarkdownV2(active.Value.Key)}` \\- {EscapeMarkdownV2(active.Value.Config.Description)}\n\n使用 `/use <name>` 切換，`/projects` 列出所有專案",
                    parseMode: ParseMode.MarkdownV2,
                    cancellationToken: ct);
            }
            else
            {
                await botClient.SendMessage(
                    chatId: chatId,
                    text: "尚未設定任何專案。",
                    cancellationToken: ct);
            }
            return;
        }

        var result = await _agentService.SwitchProjectAsync(chatId, projectKey);
        if (result is not null)
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: $"已切換至專案：`{EscapeMarkdownV2(projectKey)}`\n{EscapeMarkdownV2(result.Description)}\n`{EscapeMarkdownV2(result.Path)}`\n\nSession 已重建，對話歷史已清除。",
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: ct);
        }
        else
        {
            var projects = _agentService.GetAllProjects();
            var available = string.Join(", ", projects.Keys.Select(k => $"`{EscapeMarkdownV2(k)}`"));
            await botClient.SendMessage(
                chatId: chatId,
                text: $"找不到專案 `{EscapeMarkdownV2(projectKey)}`。\n\n可用專案：{available}",
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: ct);
        }
    }

    // ─── /model [name] ──────────────────────────────────────────

    private async Task HandleModelAsync(ITelegramBotClient botClient, long chatId, string? modelName, CancellationToken ct)
    {
        var available = await _agentService.ListModelsAsync();
        var current = _agentService.GetCurrentModel(chatId);

        if (string.IsNullOrWhiteSpace(modelName))
        {
            // 無參數：顯示目前模型 + inline keyboard 選單
            var buttons = available.Select(m =>
            {
                var isActive = string.Equals(m.Id, current, StringComparison.OrdinalIgnoreCase);
                var rateTag = FormatRateTag(m);
                var label = isActive ? $"✓ {m.Id} {rateTag}" : $"{m.Id} {rateTag}";
                return new[] { InlineKeyboardButton.WithCallbackData(label, $"model:{m.Id}") };
            }).ToArray();

            await botClient.SendMessage(
                chatId: chatId,
                text: $"目前使用的模型：`{EscapeMarkdownV2(current)}`\n\n按下按鈕切換模型：",
                parseMode: ParseMode.MarkdownV2,
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: ct);
            return;
        }

        // 驗證模型名稱（不區分大小寫）
        var matchedInfo = available.FirstOrDefault(m =>
            string.Equals(m.Id, modelName, StringComparison.OrdinalIgnoreCase));

        if (matchedInfo is null)
        {
            var list = string.Join("\n", available.Select(m =>
                $"  • `{EscapeMarkdownV2(m.Id)}` {EscapeMarkdownV2(FormatRateTag(m))}"));
            await botClient.SendMessage(
                chatId: chatId,
                text: $"未知模型：`{EscapeMarkdownV2(modelName)}`\n\n*可用模型：*\n{list}",
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: ct);
            return;
        }

        // 切換模型（使用實際的 matched 名稱，保留大小寫）
        await _agentService.SwitchModelAsync(chatId, matchedInfo.Id);
        await botClient.SendMessage(
            chatId: chatId,
            text: $"已切換模型至：`{EscapeMarkdownV2(matchedInfo.Id)}` {EscapeMarkdownV2(FormatRateTag(matchedInfo))}\n\nSession 已重建，對話歷史已清除。",
            parseMode: ParseMode.MarkdownV2,
            cancellationToken: ct);
    }

    /// <summary>
    /// 將 ModelInfo 格式化為費率標籤，例如 "[1x]"、"[2x]"、"[reasoning, 2x]"。
    /// </summary>
    internal static string FormatRateTag(ModelInfo m)
    {
        var multiplier = m.Multiplier == 1.0 ? "1x" : $"{m.Multiplier:0.##}x";
        return m.SupportsReasoning
            ? $"[reasoning, {multiplier}]"
            : $"[{multiplier}]";
    }

    // ─── /clear ─────────────────────────────────────────────────

    private async Task HandleClearAsync(ITelegramBotClient botClient, long chatId, CancellationToken ct)
    {
        await _agentService.ClearSessionAsync(chatId);

        await botClient.SendMessage(
            chatId: chatId,
            text: "Session 已銷毀，對話歷史已清除。專案與模型設定已重設為預設值。\n（跨 Session 記憶已保留，使用 /memory clear 可清除）",
            cancellationToken: ct);
    }

    // ─── /memory [clear] ────────────────────────────────────────

    private async Task HandleMemoryAsync(ITelegramBotClient botClient, long chatId, string? argument, CancellationToken ct)
    {
        // /memory clear — 清除所有記憶
        if (string.Equals(argument, "clear", StringComparison.OrdinalIgnoreCase))
        {
            await _agentService.ClearMemoryAsync(chatId);
            await botClient.SendMessage(
                chatId: chatId,
                text: "已清除所有跨 Session 記憶。",
                cancellationToken: ct);
            return;
        }

        // /memory — 顯示所有記憶條目
        var memories = await _agentService.GetMemoriesAsync(chatId);
        if (memories.Count == 0)
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: "目前沒有跨 Session 記憶。\n\n記憶會在 Session 切換、模型切換或 Bot 關閉時自動產生。",
                cancellationToken: ct);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"*跨 Session 記憶（共 {memories.Count} 條）：*");
        sb.AppendLine();

        for (var i = 0; i < memories.Count; i++)
        {
            var m = memories[i];
            var time = m.CreatedAt.ToLocalTime().ToString("MM/dd HH:mm");
            var projectInfo = !string.IsNullOrEmpty(m.ProjectKey) ? $" \\| {EscapeMarkdownV2(m.ProjectKey)}" : "";
            sb.AppendLine($"{i + 1}\\. \\[{EscapeMarkdownV2(time)}{projectInfo}\\] _{EscapeMarkdownV2(m.Trigger)}_");
            sb.AppendLine(EscapeMarkdownV2(m.Summary.Length > 500 ? m.Summary[..500] + "..." : m.Summary));
            sb.AppendLine();
        }

        sb.AppendLine("使用 `/memory clear` 清除所有記憶");

        await botClient.SendMessage(
            chatId: chatId,
            text: sb.ToString(),
            parseMode: ParseMode.MarkdownV2,
            cancellationToken: ct);
    }

    // ─── /news <keyword> ────────────────────────────────────────

    private Task HandleNewsAsync(ITelegramBotClient botClient, long chatId, string? keyword, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return botClient.SendMessage(
                chatId: chatId,
                text: "請提供關鍵字，例如：`/news AI`",
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: ct);
        }

var prompt = $"""
Please search for the latest news about "{keyword}" (within the last 24-48 hours) using web search or shell tools.

Compile the results in the following format:

📰 **Latest News on {keyword}**

1. **Headline**
One-sentence summary.
[Read more](https://...)

2. **Headline**
...

List up to 10 most recent items, sorted by publication time.
Each item should include only the headline, a one-sentence summary, and a clickable link to the original article in Markdown format: [Read more](url).
Do not include items without a verifiable link to the original source.
At the end, add: "These are the latest news about '{keyword}' as of today."
""";

        return _messageHandler.StreamPromptAsync(
            botClient, chatId,
            prompt: prompt,
            placeholderText: $"搜尋「{keyword}」新聞中...",
            ct: ct);
    }

    // ─── /update ────────────────────────────────────────────────

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, long chatId, long userId, CancellationToken ct)
    {
        // 權限檢查：只有 EffectiveAdminIds 可以執行
        var admins = _config.Value.EffectiveAdminIds;

        if (!admins.Contains(userId))
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: "⛔ 你沒有權限執行此指令。",
                cancellationToken: ct);
            return;
        }

        var statusMsg = await botClient.SendMessage(
            chatId: chatId,
            text: "🔄 開始自我更新...",
            cancellationToken: ct);

        var error = await _selfUpdate.UpdateAndRestartAsync(
            async status =>
            {
                try
                {
                    await botClient.EditMessageText(
                        chatId: chatId,
                        messageId: statusMsg.MessageId,
                        text: status,
                        cancellationToken: ct);
                }
                catch { /* ignore edit failures */ }
            },
            ct);

        if (error is not null)
        {
            await botClient.EditMessageText(
                chatId: chatId,
                messageId: statusMsg.MessageId,
                text: EscapeMarkdownV2(error),
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: ct);
        }
    }

    // ─── /mcp ───────────────────────────────────────────────────

    private async Task HandleMcpAsync(ITelegramBotClient botClient, long chatId, CancellationToken ct)
    {
        var configured = _config.Value.McpServers;

        // 若設定檔無任何 MCP server，直接告知
        if (configured.Count == 0)
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: "未設定任何 MCP server\\.\n\n在 `appsettings\\.json` 的 `CopilotClawD.McpServers` 加入 server 設定\\.",
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: ct);
            return;
        }

        // 嘗試從 active session 取得即時狀態
        var liveServers = await _agentService.GetMcpServersAsync(chatId);

        var sb = new StringBuilder();
        sb.AppendLine($"*MCP Servers \\({EscapeMarkdownV2(configured.Count.ToString())}\\)*");
        sb.AppendLine();

        foreach (var (name, serverCfg) in configured)
        {
            // 查找對應的 live 狀態
            var live = liveServers?.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

            var statusIcon = live?.Status switch
            {
                GitHub.Copilot.SDK.Rpc.ServerStatus.Connected      => "✅",
                GitHub.Copilot.SDK.Rpc.ServerStatus.Failed         => "❌",
                GitHub.Copilot.SDK.Rpc.ServerStatus.Pending        => "⏳",
                GitHub.Copilot.SDK.Rpc.ServerStatus.Disabled       => "⏸",
                GitHub.Copilot.SDK.Rpc.ServerStatus.NotConfigured  => "⚠️",
                null                                                => "○",  // 無 session，未知
                _                                                   => "?"
            };

            var statusText = live?.Status switch
            {
                GitHub.Copilot.SDK.Rpc.ServerStatus.Connected     => "Connected",
                GitHub.Copilot.SDK.Rpc.ServerStatus.Failed        => "Failed",
                GitHub.Copilot.SDK.Rpc.ServerStatus.Pending       => "Pending",
                GitHub.Copilot.SDK.Rpc.ServerStatus.Disabled      => "Disabled",
                GitHub.Copilot.SDK.Rpc.ServerStatus.NotConfigured => "Not configured",
                null                                               => "no session",
                _                                                  => live?.Status.ToString() ?? "unknown"
            };

            sb.AppendLine($"{statusIcon} *{EscapeMarkdownV2(name)}* \\({EscapeMarkdownV2(serverCfg.Type)}\\)");

            // 顯示 command 或 url
            if (serverCfg.Type is "http" or "sse" && serverCfg.Url is not null)
                sb.AppendLine($"  `{EscapeMarkdownV2(serverCfg.Url)}`");
            else if (serverCfg.Command is not null)
            {
                var cmd = serverCfg.Command;
                if (serverCfg.Args.Count > 0)
                    cmd += " " + string.Join(" ", serverCfg.Args);
                sb.AppendLine($"  `{EscapeMarkdownV2(cmd)}`");
            }

            // 狀態 + 錯誤
            sb.AppendLine($"  狀態：{EscapeMarkdownV2(statusText)}");
            if (live?.Error is { Length: > 0 } err)
                sb.AppendLine($"  錯誤：`{EscapeMarkdownV2(err)}`");

            // Tools 設定
            var tools = serverCfg.Tools;
            if (tools.Count == 1 && tools[0] == "*")
                sb.AppendLine("  工具：全部");
            else if (tools.Count == 0)
                sb.AppendLine("  工具：無（已停用）");
            else
                sb.AppendLine($"  工具：{EscapeMarkdownV2(string.Join(", ", tools))}");

            sb.AppendLine();
        }

        if (liveServers is null)
            sb.AppendLine("_\\* 尚無 active session，狀態為推估值_");

        await botClient.SendMessage(
            chatId: chatId,
            text: sb.ToString().TrimEnd(),
            parseMode: ParseMode.MarkdownV2,
            cancellationToken: ct);
    }

    // ─── /status ────────────────────────────────────────────────

    private async Task HandleStatusAsync(ITelegramBotClient botClient, long chatId, CancellationToken ct)
    {
        var active = await _agentService.GetActiveProjectAsync(chatId);
        var model = _agentService.GetCurrentModel(chatId);
        var memories = await _agentService.GetMemoriesAsync(chatId);
        var sessionInfo = _agentService.GetSessionInfo(chatId);

        var sb = new StringBuilder();
        sb.AppendLine("*Session 狀態*");
        sb.AppendLine();
        sb.AppendLine($"專案：`{EscapeMarkdownV2(active?.Key ?? "(未選擇)")}`");
        sb.AppendLine($"模型：`{EscapeMarkdownV2(model)}`");
        sb.AppendLine($"記憶：{memories.Count} 條");
        sb.AppendLine($"Session ID：`{EscapeMarkdownV2(sessionInfo.SessionId ?? "(無)")}`");
        sb.AppendLine($"Session 建立時間：{EscapeMarkdownV2(sessionInfo.CreatedAt?.ToLocalTime().ToString("MM/dd HH:mm:ss") ?? "(無)")}");
        sb.AppendLine($"Bot uptime：{EscapeMarkdownV2(FormatUptime(sessionInfo.BotStartedAt))}");

        await botClient.SendMessage(
            chatId: chatId,
            text: sb.ToString(),
            parseMode: ParseMode.MarkdownV2,
            cancellationToken: ct);
    }

    private static string FormatUptime(DateTimeOffset startedAt)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        if (elapsed.TotalDays >= 1)
            return $"{(int)elapsed.TotalDays}d {elapsed.Hours}h {elapsed.Minutes}m";
        if (elapsed.TotalHours >= 1)
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
        return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
    }

    // ─── Unknown ────────────────────────────────────────────────

    private static Task HandleUnknownAsync(ITelegramBotClient botClient, long chatId, string command, CancellationToken ct)
    {
        return botClient.SendMessage(
            chatId: chatId,
            text: $"未知指令：`{EscapeMarkdownV2(command)}`，輸入 /help 查看可用指令。",
            parseMode: ParseMode.MarkdownV2,
            cancellationToken: ct);
    }

    /// <summary>
    /// 跳脫 MarkdownV2 特殊字元。
    /// </summary>
    private static string EscapeMarkdownV2(string text) => MarkdownV2Helper.Escape(text);
}
