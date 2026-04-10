using CopilotClawD.Core.Agents;
using CopilotClawD.Core.Configuration;
using CopilotClawD.Core.Registration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace CopilotClawD.Telegram;

public class TelegramBotService : BackgroundService
{
    private readonly ITelegramBotClient _botClient;
    private readonly IOptionsMonitor<CopilotClawDConfig> _config;
    private readonly ILogger<TelegramBotService> _logger;
    private readonly Handlers.CommandHandler _commandHandler;
    private readonly Handlers.MessageHandler _messageHandler;
    private readonly RegistrationService _registrationService;
    private readonly TelegramPermissionConfirmer _permissionConfirmer;
    private readonly AgentService _agentService;

    /// <summary>
    /// Debounce 狀態（每個 chatId 獨立）。
    /// </summary>
    private sealed class DebounceEntry
    {
        public List<string> Lines { get; } = [];
        public int? FirstMessageId { get; set; }
        public CancellationTokenSource Cts { get; set; } = new();
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, DebounceEntry>
        _debounceMap = new();

    /// <summary>
    /// 多行訊息 debounce 等待時間（3 秒）。
    /// </summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(3);

    public TelegramBotService(
        ITelegramBotClient botClient,
        IOptionsMonitor<CopilotClawDConfig> config,
        ILogger<TelegramBotService> logger,
        Handlers.CommandHandler commandHandler,
        Handlers.MessageHandler messageHandler,
        RegistrationService registrationService,
        TelegramPermissionConfirmer permissionConfirmer,
        AgentService agentService)
    {
        _botClient = botClient;
        _config = config;
        _logger = logger;
        _commandHandler = commandHandler;
        _messageHandler = messageHandler;
        _registrationService = registrationService;
        _permissionConfirmer = permissionConfirmer;
        _agentService = agentService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await _botClient.GetMe(stoppingToken);
        _logger.LogInformation("CopilotClawD Bot started: @{Username} (ID: {Id})", me.Username, me.Id);
        _logger.LogInformation("Allowed user IDs: {UserIds}",
            string.Join(", ", _config.CurrentValue.AllowedUserIds));
        _logger.LogInformation("Registration: {Status}",
            _registrationService.IsEnabled ? "ENABLED (passcode set)" : "DISABLED");

        // 丟棄 Bot 離線期間積壓的所有 pending updates，
        // 避免重啟後重新執行使用者之前送出的指令/訊息。
        await DropPendingUpdatesAsync(stoppingToken);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
        };

        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken
        );

        // 等待取消信號
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        _logger.LogInformation("CopilotClawD Bot stopped.");
    }

    private Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        // Fire-and-forget：讓 polling loop 立刻繼續接收下一個 update。
        // 若不這樣做，當 streaming 長時間佔用 handler，使用者按下 Inline Keyboard 按鈕的
        // CallbackQuery 會在 Telegram server 積壓，無法被即時收到（導致權限確認按鈕無反應）。
        _ = Task.Run(() => HandleUpdateCoreErrorSafeAsync(botClient, update, ct), ct);
        return Task.CompletedTask;
    }

    private async Task HandleUpdateCoreErrorSafeAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        try
        {
            await HandleUpdateCoreAsync(botClient, update, ct);
        }
        catch (OperationCanceledException)
        {
            // 正常取消，不需處理
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleUpdateAsync 發生未處理例外");

            // 嘗試通知使用者
            var chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id;
            if (chatId.HasValue)
            {
                try
                {
                    await botClient.SendMessage(
                        chatId: chatId.Value,
                        text: "處理訊息時發生錯誤，請稍後再試。",
                        cancellationToken: ct);
                }
                catch
                {
                    // 通知失敗也忽略，避免無限循環
                }
            }
        }
    }

    private async Task HandleUpdateCoreAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        // ── CallbackQuery（Inline Keyboard 回應，例如權限確認按鈕）──
        if (update.CallbackQuery is { } callbackQuery)
        {
            await HandleCallbackQueryAsync(botClient, callbackQuery, ct);
            return;
        }

        // ── Message ──
        if (update.Message is not { } message) return;
        if (message.Text is not { } text)
        {
            // 非文字訊息（圖片、貼圖、檔案、語音等）：給予友善提示
            if (_registrationService.IsAllowed(message.From?.Id ?? 0))
            {
                await botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: "目前只支援文字訊息。",
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: ct);
            }
            return;
        }

        var chatId = message.Chat.Id;
        var userId = message.From?.Id ?? 0;
        var username = message.From?.Username ?? message.From?.FirstName ?? "Unknown";

        // 白名單驗證
        if (!_registrationService.IsAllowed(userId))
        {
            // 嘗試自助註冊
            await HandleUnauthorizedAsync(botClient, message, userId, username, text, ct);
            return;
        }

        _logger.LogInformation("[{Username}({UserId})] {Text}", username, userId, text);

        // 分派：指令 vs 一般訊息（一般訊息走 debounce 合併）
        if (text.StartsWith('/'))
        {
            await _commandHandler.HandleAsync(botClient, message, ct);
        }
        else
        {
            await EnqueueDebounceAsync(botClient, chatId, message.MessageId, text, ct);
        }
    }

    /// <summary>
    /// 處理 Inline Keyboard 的 CallbackQuery（目前用於權限確認按鈕、專案/模型選擇）。
    /// </summary>
    private async Task HandleCallbackQueryAsync(
        ITelegramBotClient botClient, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var data = callbackQuery.Data;
        if (string.IsNullOrEmpty(data)) return;

        var userId = callbackQuery.From.Id;
        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;

        // 白名單驗證（只有授權使用者可以回應按鈕）
        if (!_registrationService.IsAllowed(userId))
        {
            _logger.LogWarning("Unauthorized CallbackQuery from {UserId}", userId);
            await botClient.AnswerCallbackQuery(callbackQuery.Id, text: "未授權", cancellationToken: ct);
            return;
        }

        // ── 專案切換 ────────────────────────────────────────────
        if (data.StartsWith("proj:"))
        {
            var projectKey = data["proj:".Length..];
            await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            var result = await _agentService.SwitchProjectAsync(chatId, projectKey);
            if (result is not null)
            {
                // 更新選單訊息（移除按鈕）
                if (callbackQuery.Message is { } msg)
                {
                    try
                    {
                        var switchText =
                            $"已切換至專案：`{EscapeMarkdownV2(projectKey)}`\n" +
                            $"{EscapeMarkdownV2(result.Description)}\n" +
                            $"`{EscapeMarkdownV2(result.Path)}`\n\n" +
                            $"Session 已重建，對話歷史已清除。";
                        await botClient.EditMessageText(
                            chatId: msg.Chat.Id,
                            messageId: msg.MessageId,
                            text: switchText,
                            parseMode: ParseMode.MarkdownV2,
                            cancellationToken: ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "更新專案選單訊息失敗");
                    }
                }
            }
            else
            {
                await botClient.SendMessage(chatId: chatId, text: $"找不到專案：{projectKey}", cancellationToken: ct);
            }
            return;
        }

        // ── 模型切換 ────────────────────────────────────────────
        if (data.StartsWith("model:"))
        {
            var model = data["model:".Length..];
            await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            await _agentService.SwitchModelAsync(chatId, model);

            // 查詢費率標籤（best-effort，失敗就省略）
            var rateTag = string.Empty;
            try
            {
                var models = await _agentService.ListModelsAsync();
                var info = models.FirstOrDefault(m =>
                    string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
                if (info is not null)
                    rateTag = " " + Handlers.CommandHandler.FormatRateTag(info);
            }
            catch { /* 查詢失敗不影響切換確認 */ }

            if (callbackQuery.Message is { } msg)
            {
                try
                {
                    await botClient.EditMessageText(
                        chatId: msg.Chat.Id,
                        messageId: msg.MessageId,
                        text: $"已切換模型至：`{EscapeMarkdownV2(model)}`{EscapeMarkdownV2(rateTag)}\nSession 已重建，對話歷史已清除。",
                        parseMode: ParseMode.MarkdownV2,
                        cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "更新模型選單訊息失敗");
                }
            }
            return;
        }

        // ── 權限確認 ─────────────────────────────────────────────
        if (data.StartsWith("perm:"))
        {
            var approved = data.EndsWith(":yes");
            var answerText = approved ? "✅ 已允許" : "❌ 已拒絕";
            _permissionConfirmer.TryHandleCallback(data);

            // AnswerCallbackQuery 有 10 秒限制，過時會拋 400，直接忽略
            try
            {
                await botClient.AnswerCallbackQuery(callbackQuery.Id, text: answerText, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "AnswerCallbackQuery 失敗（可能已過期）");
            }

            // 更新按鈕訊息，移除按鈕（避免重複點擊）
            if (callbackQuery.Message is { } msg)
            {
                try
                {
                    var originalText = msg.Text ?? data;
                    var resultLabel = approved ? "\n\n✅ 使用者已允許執行" : "\n\n❌ 使用者已拒絕執行";
                    await botClient.EditMessageText(
                        chatId: msg.Chat.Id,
                        messageId: msg.MessageId,
                        text: originalText + resultLabel,
                        cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    // 編輯失敗（例如訊息過舊），忽略但記錄
                    _logger.LogDebug(ex, "編輯權限確認訊息失敗（可能訊息已過舊或內容未變更）");
                }
            }
            return;
        }

        // 未知的 callback data
        _logger.LogDebug("未處理的 CallbackQuery data: {Data}", data);
        await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
    }

    /// <summary>
    /// F10 Debounce：將訊息加入 per-chat buffer，3 秒後若無新訊息則合併送出。
    /// 若同一 chatId 在 3 秒內發送多則訊息，會合併成一個（以換行分隔）再送給 AI。
    /// </summary>
    private async Task EnqueueDebounceAsync(
        ITelegramBotClient botClient, long chatId, int messageId, string text, CancellationToken ct)
    {
        var entry = _debounceMap.GetOrAdd(chatId, _ => new DebounceEntry());

        CancellationTokenSource oldCts;
        CancellationTokenSource newCts;

        lock (entry)
        {
            entry.Lines.Add(text);
            if (!entry.FirstMessageId.HasValue)
                entry.FirstMessageId = messageId;

            // 取消舊的 timer，建新的
            oldCts = entry.Cts;
            newCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            entry.Cts = newCts;
        }

        try { oldCts.Cancel(); } catch { /* ignore */ }
        oldCts.Dispose();

        // 等待 debounce delay（若被新訊息取消，直接返回）
        try
        {
            await Task.Delay(DebounceDelay, newCts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // 新訊息已接管
        }

        // 時間到，取出並清空 buffer
        string combinedText;
        int firstMsgId;

        lock (entry)
        {
            if (entry.Lines.Count == 0) return;
            combinedText = string.Join("\n", entry.Lines);
            firstMsgId = entry.FirstMessageId ?? messageId;
            entry.Lines.Clear();
            entry.FirstMessageId = null;
        }

        _debounceMap.TryRemove(chatId, out _);

        var lineCount = combinedText.Split('\n').Length;
        _logger.LogInformation("Chat {ChatId}: Debounce flush，合併 {Lines} 行訊息", chatId, lineCount);

        await _messageHandler.StreamPromptAsync(
            botClient,
            chatId,
            combinedText,
            replyToMessageId: firstMsgId,
            ct: ct);
    }

    private async Task HandleUnauthorizedAsync(
        ITelegramBotClient botClient, Message message,
        long userId, string username, string text, CancellationToken ct)
    {
        // 如果自助註冊未啟用，直接拒絕
        if (!_registrationService.IsEnabled)
        {
            _logger.LogWarning("Unauthorized access from {Username}({UserId}), registration disabled",
                username, userId);
            return;
        }

        // 嘗試用訊息內容當作密碼
        var result = await _registrationService.TryRegisterAsync(userId, username, text);

        switch (result)
        {
            case RegistrationResult.Success:
                _logger.LogInformation("User {Username}({UserId}) registered successfully", username, userId);
                await botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: $"✓ 註冊成功！歡迎 @{username}\n你的 User ID: {userId}\n\n輸入 /help 查看可用指令。",
                    cancellationToken: ct);
                break;

            case RegistrationResult.AlreadyRegistered:
                // 不應走到這裡，但以防萬一
                break;

            case RegistrationResult.WrongPasscode:
                _logger.LogWarning("Failed registration attempt from {Username}({UserId})", username, userId);
                // 不給任何提示，避免暴露密碼機制
                break;

            case RegistrationResult.Disabled:
                break;
        }
    }

    /// <summary>
    /// 清空 Bot 離線期間 Telegram 伺服器上積壓的 pending updates。
    /// 做法：用 offset = -1 呼叫 GetUpdates，Telegram 會回傳最後一筆 update；
    /// 再用 offset = lastUpdateId + 1 確認（ACK），讓後續 polling 從新訊息開始。
    /// </summary>
    private async Task DropPendingUpdatesAsync(CancellationToken ct)
    {
        try
        {
            // offset = -1 只取最後一筆，不會觸發大量 API 呼叫
            var updates = await _botClient.GetUpdates(offset: -1, timeout: 0, cancellationToken: ct);
            if (updates.Length > 0)
            {
                var lastId = updates[^1].Id;
                // ACK：確認到 lastId，後續 polling 從 lastId + 1 開始
                await _botClient.GetUpdates(offset: lastId + 1, timeout: 0, cancellationToken: ct);
                _logger.LogInformation("已丟棄 pending updates（最後 update ID：{LastId}）", lastId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清空 pending updates 失敗，繼續啟動");
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        _logger.LogError(exception, "Telegram Bot error from {Source}", source);
        return Task.CompletedTask;
    }

    private static string EscapeMarkdownV2(string text) => MarkdownV2Helper.Escape(text);
}
