using System.Text;
using System.Text.RegularExpressions;
using CopilotClawD.Core.Agents;
using CopilotClawD.Core.Configuration;
using CopilotClawD.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace CopilotClawD.Telegram.Handlers;

public class MessageHandler
{
    private readonly AgentService _agentService;
    private readonly SecretRedactor _secretRedactor;
    private readonly IOptionsMonitor<CopilotClawDConfig> _config;
    private readonly ILogger<MessageHandler> _logger;

    /// <summary>
    /// Streaming 更新的最小間隔（避免觸發 Telegram API rate limit）。
    /// Telegram 限制每秒約 30 則訊息，每則編輯至少間隔 ~1 秒較安全。
    /// </summary>
    private static readonly TimeSpan StreamingUpdateInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 等待計時器更新間隔（在尚未收到文字回覆時，每 N 秒更新佔位訊息）。
    /// </summary>
    private static readonly TimeSpan WaitingTimerInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Telegram 單則訊息字元上限（官方 4096，保留少許 buffer）。
    /// </summary>
    private const int TelegramMaxLength = 4000;

    public MessageHandler(AgentService agentService, SecretRedactor secretRedactor, IOptionsMonitor<CopilotClawDConfig> config, ILogger<MessageHandler> logger)
    {
        _agentService = agentService;
        _secretRedactor = secretRedactor;
        _config = config;
        _logger = logger;
    }

    public async Task HandleAsync(ITelegramBotClient botClient, Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var text = message.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(text))
            return;

        await StreamPromptAsync(botClient, chatId, text, replyToMessageId: message.MessageId, ct: ct);
    }

    /// <summary>
    /// 將 prompt 送給 AgentService，以 streaming 方式更新 Telegram 訊息。
    /// 可由 MessageHandler（使用者輸入）或 CommandHandler（指令觸發固定 prompt）共用。
    /// </summary>
    public async Task StreamPromptAsync(
        ITelegramBotClient botClient,
        long chatId,
        string prompt,
        string placeholderText = "thinking...",
        int? replyToMessageId = null,
        CancellationToken ct = default)
    {
        // 1. 先送出佔位訊息
        var placeholder = await botClient.SendMessage(
            chatId: chatId,
            text: placeholderText,
            replyParameters: replyToMessageId.HasValue
                ? new ReplyParameters { MessageId = replyToMessageId.Value }
                : null,
            cancellationToken: ct);

        // 2. 持續發送 "typing..." 狀態，直到收到第一個 AI 文字回覆
        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var typingTask = SendTypingLoopAsync(botClient, chatId, typingCts.Token);

        // 3. 等待計時器：在尚未收到文字回覆時，定期更新佔位訊息顯示已等待秒數
        using var waitingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toolTracker = new ToolProgressTracker();
        var waitingTask = SendWaitingTimerLoopAsync(
            botClient, chatId, placeholder.MessageId, placeholderText, toolTracker, waitingCts.Token);

        try
        {
            // 4. Streaming 收取 AI 回覆，定期編輯佔位訊息
            var buffer = new StringBuilder();
            var lastEditTime = DateTime.UtcNow;
            var lastEditedLength = 0;
            var firstDeltaReceived = false;
            var sessionResetNotice = string.Empty;
            var welcomeBackNotice = string.Empty;

            await foreach (var chunk in _agentService.ProcessMessageAsync(chatId, prompt, ct))
            {
                switch (chunk.Type)
                {
                    case StreamChunkType.Delta:
                        buffer.Append(chunk.Content);
                        // 收到第一個文字 → 停止 typing 指示器 + 等待計時器，並等它們真正結束
                        if (!firstDeltaReceived)
                        {
                            firstDeltaReceived = true;
                            await typingCts.CancelAsync();
                            await waitingCts.CancelAsync();
                            // 等 waitingTask 真正結束，確保不再有並發 edit
                            try { await waitingTask; } catch { /* ignore */ }
                            lastEditTime = DateTime.UtcNow;
                        }
                        break;

                    case StreamChunkType.ToolStart:
                        toolTracker.OnToolStart(chunk.Content);
                        break;

                    case StreamChunkType.ToolEnd:
                        toolTracker.OnToolEnd(chunk.Content);
                        break;

                    case StreamChunkType.SessionReset:
                        sessionResetNotice = chunk.Content;
                        break;

                    case StreamChunkType.WelcomeBack:
                        welcomeBackNotice = chunk.Content;
                        break;

                    case StreamChunkType.Error:
                        buffer.AppendLine();
                        buffer.Append($"Error: {chunk.Content}");
                        break;
                }

                // 依間隔更新訊息（只在有新文字、且 waitingTask 已結束後）
                if (chunk.Type == StreamChunkType.Delta && firstDeltaReceived)
                {
                    var elapsed = DateTime.UtcNow - lastEditTime;
                    if (elapsed >= StreamingUpdateInterval && buffer.Length > lastEditedLength)
                    {
                        await TryEditMessageAsync(botClient, chatId, placeholder.MessageId, buffer.ToString(), ct);
                        lastEditTime = DateTime.UtcNow;
                        lastEditedLength = buffer.Length;
                    }
                }
            }

            // 5. 若 Bot 重啟後第一次建立 session，先補發狀態提醒
            if (!string.IsNullOrEmpty(welcomeBackNotice))
            {
                try
                {
                    await botClient.SendMessage(
                        chatId: chatId,
                        text: welcomeBackNotice,
                        cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Chat {ChatId}: 發送 welcome back 通知失敗", chatId);
                }
            }

            // 6. 若 session 重建，先補發一條通知訊息
            if (!string.IsNullOrEmpty(sessionResetNotice))
            {
                try
                {
                    await botClient.SendMessage(
                        chatId: chatId,
                        text: $"⚠️ {sessionResetNotice}",
                        cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Chat {ChatId}: 發送 session reset 通知失敗", chatId);
                }
            }

            // 7. 最終更新：確保完整回覆已送出
            if (buffer.Length > 0)
            {
                // 先把 [SEND_FILE:] 標記從 buffer 移除，取得乾淨的文字 + 檔案清單
                var (cleanText, filePaths) = ExtractSendFileMarkers(buffer.ToString());

                // 有文字回覆：若尚未 edit 到最新，補一次最終 edit
                if (cleanText.Length > 0)
                {
                    // 注意：cleanText 比原始 buffer 短（去除了 SEND_FILE 標記），
                    // 所以不能用 lastEditedLength 比較，一律送出最終版
                    await SendOrSplitAsync(botClient, chatId, placeholder.MessageId, cleanText, ct);
                }
                else
                {
                    // 只有 SEND_FILE 標記，沒有其他文字
                    await TryEditMessageAsync(botClient, chatId, placeholder.MessageId,
                        "✅ 完成（AI 沒有產生文字回覆）", ct);
                }

                // 傳送所有檔案（[SEND_FILE:] marker 機制）
                foreach (var filePath in filePaths)
                {
                    await TrySendFileAsync(botClient, chatId, filePath, ct);
                }

                // Fallback：主動掃描 swap 目錄的 screenshot.jpg
                // 若 AI 沒有輸出 [SEND_FILE:] marker，仍可從固定路徑偵測並傳送
                await TrySendSwapScreenshotAsync(botClient, chatId, filePaths, ct);
            }
            else
            {
                // AI 沒有產出任何文字（例如：只執行工具就 idle）
                // 把佔位訊息改成提示，讓使用者知道 AI 已完成但沒有文字回覆
                await TryEditMessageAsync(botClient, chatId, placeholder.MessageId,
                    "✅ 完成（AI 沒有產生文字回覆）", ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Chat {ChatId}: 串流已取消", chatId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat {ChatId}: AI 回覆時發生錯誤", chatId);
            await TryEditMessageAsync(botClient, chatId, placeholder.MessageId,
                $"Error: {ex.Message}", ct);
        }
        finally
        {
            // 確保 typing loop 和 waiting timer 結束
            await typingCts.CancelAsync();
            await waitingCts.CancelAsync();
            try { await typingTask; } catch { /* ignore */ }
            try { await waitingTask; } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// 持續發送 "typing..." chat action，每 4 秒一次（Telegram 的 typing 狀態只維持 5 秒）。
    /// </summary>
    private static async Task SendTypingLoopAsync(ITelegramBotClient botClient, long chatId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await botClient.SendChatAction(chatId: chatId, action: ChatAction.Typing, cancellationToken: ct);
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常結束
        }
        catch
        {
            // 忽略 API 錯誤
        }
    }

    /// <summary>
    /// 在尚未收到 AI 文字回覆時，定期更新佔位訊息顯示等待秒數 + 工具進度。
    /// 例如：「thinking... (15s)」或「working... [2/3] read: src/Program.cs (20s)」
    /// </summary>
    private async Task SendWaitingTimerLoopAsync(
        ITelegramBotClient botClient, long chatId, int messageId,
        string basePlaceholder, ToolProgressTracker toolTracker, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(WaitingTimerInterval, ct);

                var elapsed = (int)(DateTime.UtcNow - startTime).TotalSeconds;
                var progress = toolTracker.GetProgressText();
                string statusText;
                if (string.IsNullOrEmpty(progress))
                {
                    statusText = elapsed >= 60
                        ? $"仍在執行中，請稍候... ({elapsed}s)"
                        : $"{basePlaceholder} ({elapsed}s)";
                }
                else
                {
                    statusText = $"working... {progress} ({elapsed}s)";
                }

                // 給 edit 加 8 秒 timeout：若 Telegram API hang 住，跳過這次更新繼續下一輪
                // 避免 TryEditMessageAsync 卡住導致整個 timer loop 停止更新
                using var editCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                editCts.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    await TryEditMessageAsync(botClient, chatId, messageId, statusText, editCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // edit timeout（8s），跳過此次更新，繼續下一輪計時
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常結束（收到第一個 delta 文字時取消）
        }
        catch
        {
            // 忽略 API 錯誤
        }
    }

    /// <summary>
    /// 安全地編輯訊息。先嘗試 MarkdownV2，失敗則 fallback 純文字。
    /// </summary>
    private async Task TryEditMessageAsync(
        ITelegramBotClient botClient, long chatId, int messageId, string text, CancellationToken ct)
    {
        // Phase 7: 機密防洩漏掃描
        text = _secretRedactor.Redact(text);

        var truncated = text.Length > TelegramMaxLength;
        if (truncated)
            text = text[..TelegramMaxLength];

        // 嘗試 MarkdownV2（含 429 retry）
        try
        {
            var mdText = ToMarkdownV2(text);
            if (truncated) mdText += "\n\n\\.\\.\\. \\(truncated\\)";
            await EditWithRetryAsync(botClient, chatId, messageId, mdText, ParseMode.MarkdownV2, ct);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("MarkdownV2 編輯失敗，嘗試純文字: {Error}", ex.Message);
        }

        // Fallback：純文字（含 429 retry）
        try
        {
            var plain = truncated ? text + "\n\n... (truncated)" : text;
            await EditWithRetryAsync(botClient, chatId, messageId, plain, ParseMode.Html, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 常見：訊息內容未變更時 Telegram 回 400，忽略
            _logger.LogDebug("純文字編輯亦失敗: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// 帶 429 Rate Limit retry 的 EditMessageText。
    /// Telegram 429 例外會帶 RetryAfter 秒數，等待後重試一次。
    /// </summary>
    private static async Task EditWithRetryAsync(
        ITelegramBotClient botClient, long chatId, int messageId,
        string text, ParseMode parseMode, CancellationToken ct)
    {
        try
        {
            await botClient.EditMessageText(
                chatId: chatId,
                messageId: messageId,
                text: text,
                parseMode: parseMode,
                cancellationToken: ct);
        }
        catch (ApiRequestException ex)
            when (ex.ErrorCode == 429 && ex.Parameters?.RetryAfter is { } retryAfter)
        {
            // 等待 Telegram 要求的秒數後重試一次
            await Task.Delay(TimeSpan.FromSeconds(retryAfter + 1), ct);
            await botClient.EditMessageText(
                chatId: chatId,
                messageId: messageId,
                text: text,
                parseMode: parseMode,
                cancellationToken: ct);
        }
    }

    /// <summary>
    /// 最終回覆：若長度超過 Telegram 上限則自動分拆成多則訊息。
    /// 第一則編輯佔位訊息，後續用 SendMessage 補發。先嘗試 MarkdownV2，失敗 fallback 純文字。
    /// </summary>
    private async Task SendOrSplitAsync(
        ITelegramBotClient botClient, long chatId, int placeholderMessageId, string text, CancellationToken ct)
    {
        // Phase 7: 機密防洩漏掃描
        text = _secretRedactor.Redact(text);

        var chunks = SplitIntoChunks(text, TelegramMaxLength);

        for (var i = 0; i < chunks.Count; i++)
        {
            if (i == 0)
            {
                // 第一段：編輯佔位訊息（TryEditMessageAsync 已有 MarkdownV2 fallback 邏輯）
                await TryEditMessageAsync(botClient, chatId, placeholderMessageId, chunks[i], ct);
            }
            else
            {
                // 後續段：補發新訊息，先試 MarkdownV2，失敗 fallback 純文字
                try
                {
                    await botClient.SendMessage(
                        chatId: chatId,
                        text: ToMarkdownV2(chunks[i]),
                        parseMode: ParseMode.MarkdownV2,
                        cancellationToken: ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug("SendMessage MarkdownV2 失敗，fallback 純文字: {Error}", ex.Message);
                    try
                    {
                        await botClient.SendMessage(
                            chatId: chatId,
                            text: chunks[i],
                            cancellationToken: ct);
                    }
                    catch (Exception ex2) when (ex2 is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex2, "Chat {ChatId}: SendMessage fallback 純文字亦失敗", chatId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 將一般 Markdown 文字轉換為 Telegram MarkdownV2 格式。
    /// 策略：保留常見 Markdown 結構（粗體、斜體、程式碼、code block、連結），
    /// 其餘特殊字元依 MarkdownV2 規則 escape。
    /// </summary>
    private static string ToMarkdownV2(string text)
    {
        // MarkdownV2 需要 escape 的字元（除了在格式標記內的）
        // 參考：https://core.telegram.org/bots/api#markdownv2-style
        // 在一般文字中需要 escape：_ * [ ] ( ) ~ ` > # + - = | { } . !
        var result = new StringBuilder(text.Length + 64);
        var i = 0;

        while (i < text.Length)
        {
            // Code block: ```lang\n...\n```
            if (i + 2 < text.Length && text[i] == '`' && text[i + 1] == '`' && text[i + 2] == '`')
            {
                var end = text.IndexOf("```", i + 3, StringComparison.Ordinal);
                if (end >= 0)
                {
                    // 找到配對的 ``` 結束
                    var block = text[(i + 3)..end];
                    // 移除語言標記（第一行若不含換行，視為語言）
                    var newlineIdx = block.IndexOf('\n');
                    string lang, code;
                    if (newlineIdx >= 0 && newlineIdx < 20 && !block[..newlineIdx].Contains(' '))
                    {
                        lang = block[..newlineIdx];
                        code = block[(newlineIdx + 1)..];
                    }
                    else
                    {
                        lang = string.Empty;
                        code = block;
                    }
                    // code block 內只需 escape ` 和 \
                    var escapedCode = code.Replace("\\", "\\\\").Replace("`", "\\`");
                    result.Append("```");
                    if (!string.IsNullOrEmpty(lang))
                        result.Append(EscapeV2(lang));
                    result.Append('\n');
                    result.Append(escapedCode);
                    result.Append("```");
                    i = end + 3;
                    continue;
                }
            }

            // Inline code: `...`
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end >= 0)
                {
                    var code = text[(i + 1)..end];
                    var escapedCode = code.Replace("\\", "\\\\").Replace("`", "\\`");
                    result.Append('`');
                    result.Append(escapedCode);
                    result.Append('`');
                    i = end + 1;
                    continue;
                }
            }

            // Bold: **...**
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end >= 0)
                {
                    result.Append('*');
                    result.Append(ToMarkdownV2(text[(i + 2)..end]));
                    result.Append('*');
                    i = end + 2;
                    continue;
                }
            }

            // Bold: *...*  (single asterisk)
            if (text[i] == '*')
            {
                var end = text.IndexOf('*', i + 1);
                if (end >= 0 && end > i + 1)
                {
                    result.Append('*');
                    result.Append(ToMarkdownV2(text[(i + 1)..end]));
                    result.Append('*');
                    i = end + 1;
                    continue;
                }
            }

            // Escape 所有 MarkdownV2 特殊字元
            result.Append(EscapeV2Char(text[i]));
            i++;
        }

        return result.ToString();
    }

    /// <summary>
    /// Escape 整個字串（用於 code block 語言標記等需要完整 escape 的情境）。
    /// </summary>
    private static string EscapeV2(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
            sb.Append(EscapeV2Char(c));
        return sb.ToString();
    }

    /// <summary>
    /// 將單一字元依 MarkdownV2 規則 escape。
    /// </summary>
    private static string EscapeV2Char(char c) => c switch
    {
        '\\' => "\\\\",
        '_' or '*' or '[' or ']' or '(' or ')' or '~' or '`' or '>' or
        '#' or '+' or '-' or '=' or '|' or '{' or '}' or '.' or '!' => $"\\{c}",
        _ => c.ToString()
    };

    /// <summary>
    /// 從 AI 回覆文字中擷取所有 [SEND_FILE: path] 標記，回傳去除標記後的乾淨文字與檔案路徑清單。
    /// </summary>
    private static (string CleanText, List<string> FilePaths) ExtractSendFileMarkers(string text)
    {
        var filePaths = new List<string>();
        // 匹配整行的 [SEND_FILE: C:\path\to\file.png]（允許前後空白）
        var pattern = new Regex(@"^\s*\[SEND_FILE:\s*(.+?)\s*\]\s*$", RegexOptions.Multiline);

        var cleanText = pattern.Replace(text, match =>
        {
            var path = match.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(path))
                filePaths.Add(path);
            return string.Empty; // 移除這行
        });

        // 清理多餘的連續空行（移除標記後可能留下空行）
        cleanText = Regex.Replace(cleanText, @"\n{3,}", "\n\n").Trim();

        return (cleanText, filePaths);
    }

    /// <summary>
    /// Fallback：主動掃描 swap 目錄的 screenshot.jpg，若存在且尚未透過 [SEND_FILE:] 傳送，
    /// 則自動傳送並刪除。
    /// </summary>
    private async Task TrySendSwapScreenshotAsync(
        ITelegramBotClient botClient, long chatId, List<string> alreadySentPaths, CancellationToken ct)
    {
        var swapDir = _config.CurrentValue.SwapDirectory;
        if (string.IsNullOrWhiteSpace(swapDir))
            return;

        var screenshotPath = Path.Combine(swapDir, "screenshot.jpg");

        // 若已由 [SEND_FILE:] 機制傳送過，跳過
        if (alreadySentPaths.Any(p => string.Equals(p, screenshotPath, StringComparison.OrdinalIgnoreCase)))
            return;

        if (!File.Exists(screenshotPath))
            return;

        _logger.LogInformation("Chat {ChatId}: 偵測到 swap 截圖，自動傳送: {Path}", chatId, screenshotPath);
        await TrySendFileAsync(botClient, chatId, screenshotPath, ct);
    }

    /// <summary>
    /// 嘗試傳送本機檔案到 Telegram。
    /// 圖片格式（png/jpg/jpeg/gif/webp）用 SendPhoto，其他用 SendDocument。
    /// </summary>
    private async Task TrySendFileAsync(
        ITelegramBotClient botClient, long chatId, string filePath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("Chat {ChatId}: AI 要求傳送的檔案不存在: {Path}", chatId, filePath);
                await botClient.SendMessage(
                    chatId: chatId,
                    text: $"⚠️ 檔案不存在：`{filePath}`",
                    parseMode: ParseMode.MarkdownV2,
                    cancellationToken: ct);
                return;
            }

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var isPhoto = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

            await using var stream = File.OpenRead(filePath);
            var fileName = Path.GetFileName(filePath);
            var inputFile = InputFile.FromStream(stream, fileName);

            if (isPhoto)
            {
                await botClient.SendPhoto(
                    chatId: chatId,
                    photo: inputFile,
                    cancellationToken: ct);
            }
            else
            {
                await botClient.SendDocument(
                    chatId: chatId,
                    document: inputFile,
                    cancellationToken: ct);
            }

            _logger.LogInformation("Chat {ChatId}: 已傳送檔案 {Path} ({Type})",
                chatId, filePath, isPhoto ? "photo" : "document");

            // 傳送成功後刪除暫存檔
            try
            {
                File.Delete(filePath);
                _logger.LogInformation("Chat {ChatId}: 已刪除暫存檔 {Path}", chatId, filePath);
            }
            catch (Exception delEx)
            {
                // 刪除失敗不影響主流程，記錄即可
                _logger.LogWarning(delEx, "Chat {ChatId}: 刪除暫存檔失敗: {Path}", chatId, filePath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Chat {ChatId}: 傳送檔案失敗: {Path}", chatId, filePath);
            try
            {
                await botClient.SendMessage(
                    chatId: chatId,
                    text: $"⚠️ 傳送檔案失敗：{ex.Message}",
                    cancellationToken: ct);
            }
            catch { /* 通知失敗也忽略 */ }
        }
    }

    /// <summary>
    /// 將長文字依段落（換行）切分，每段不超過 maxLength 字元。
    /// </summary>
    private static List<string> SplitIntoChunks(string text, int maxLength)
    {
        var result = new List<string>();
        if (text.Length <= maxLength)
        {
            result.Add(text);
            return result;
        }

        var lines = text.Split('\n');
        var current = new StringBuilder();

        foreach (var line in lines)
        {
            // 單行本身就超過上限（極端情況），硬切
            if (line.Length > maxLength)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString().TrimEnd());
                    current.Clear();
                }
                for (var i = 0; i < line.Length; i += maxLength)
                    result.Add(line.Substring(i, Math.Min(maxLength, line.Length - i)));
                continue;
            }

            // 加上這行會超過上限 → 先存目前的，開新段
            if (current.Length + line.Length + 1 > maxLength)
            {
                result.Add(current.ToString().TrimEnd());
                current.Clear();
            }

            current.AppendLine(line);
        }

        if (current.Length > 0)
            result.Add(current.ToString().TrimEnd());

        return result;
    }
}

/// <summary>
/// 追蹤工具執行進度，提供格式化的進度字串。
/// 例如：「[2/3] read: src/Program.cs」
/// Thread-safe：由 streaming loop（寫入）和 waiting timer loop（讀取）併行存取。
/// </summary>
internal class ToolProgressTracker
{
    private readonly object _lock = new();
    private readonly List<string> _completedTools = [];
    private string? _currentTool;
    private int _totalStarted;

    /// <summary>
    /// 記錄工具開始執行。content 格式為 "[Tool: shell]"。
    /// </summary>
    public void OnToolStart(string content)
    {
        // 從 "[Tool: shell]" 擷取工具名稱
        var toolName = content
            .Replace("[Tool: ", "").Replace("]", "").Trim();

        lock (_lock)
        {
            _totalStarted++;
            _currentTool = toolName;
        }
    }

    /// <summary>
    /// 記錄工具執行完成。content 格式為 "[Tool done]" 或 "[Tool failed]"。
    /// </summary>
    public void OnToolEnd(string content)
    {
        var status = content.Contains("failed") ? "failed" : "done";

        lock (_lock)
        {
            if (_currentTool is not null)
            {
                _completedTools.Add($"{_currentTool} ({status})");
                _currentTool = null;
            }
        }
    }

    /// <summary>
    /// 取得目前的進度文字。
    /// 無工具執行時回傳空字串。
    /// 有工具正在執行時回傳如 「[2/3] read: src/Program.cs」。
    /// </summary>
    public string GetProgressText()
    {
        lock (_lock)
        {
            if (_totalStarted == 0)
                return string.Empty;

            var completedCount = _completedTools.Count;

            // 有工具正在執行
            if (_currentTool is not null)
            {
                return $"[{completedCount + 1}/{_totalStarted}] {_currentTool}";
            }

            // 所有已啟動的工具都完成了（可能等待 AI 決定下一步）
            if (completedCount > 0)
            {
                var last = _completedTools[^1];
                return $"[{completedCount}/{_totalStarted}] {last}";
            }

            return string.Empty;
        }
    }
}
