using System.Collections.Concurrent;
using CopilotClawD.Core.Security;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;

namespace CopilotClawD.Telegram;

/// <summary>
/// Telegram 實作：透過 Inline Keyboard 按鈕讓使用者確認危險操作。
/// 每個確認請求產生一個唯一 ID，對應一個 TaskCompletionSource&lt;bool&gt;。
/// 使用者按下「允許」或「拒絕」後，CallbackQuery handler 會 set result。
/// </summary>
public class TelegramPermissionConfirmer : IPermissionConfirmer
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<TelegramPermissionConfirmer> _logger;

    /// <summary>
    /// 待確認的請求：Key = callbackData prefix (e.g. "perm:abc123"), Value = TCS。
    /// </summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();

    public TelegramPermissionConfirmer(
        ITelegramBotClient botClient,
        ILogger<TelegramPermissionConfirmer> logger)
    {
        _botClient = botClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(long chatId, string description, TimeSpan timeout)
    {
        var requestId = Guid.NewGuid().ToString("N")[..8]; // 簡短 ID
        var approveData = $"perm:{requestId}:yes";
        var denyData = $"perm:{requestId}:no";

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        try
        {
            // 發送帶 Inline Keyboard 的確認訊息
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ 允許", approveData),
                    InlineKeyboardButton.WithCallbackData("❌ 拒絕", denyData),
                }
            });

            await _botClient.SendMessage(
                chatId: chatId,
                text: description,
                replyMarkup: keyboard);

            // 提示使用者 Bot 正在等待確認（避免看起來像卡死）
            try
            {
                await _botClient.SendMessage(
                    chatId: chatId,
                    text: "⏳ 等待您確認後繼續執行...");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Chat {ChatId}: 發送等待提示失敗（非致命）", chatId);
            }

            // 等待使用者回應，或逾時
            using var cts = new CancellationTokenSource(timeout);
            var timedOut = false;
            cts.Token.Register(() =>
            {
                timedOut = true;
                tcs.TrySetResult(false);
            });

            var result = await tcs.Task;

            // 若是超時（而非使用者主動拒絕），補發通知
            if (timedOut)
            {
                _logger.LogInformation("Chat {ChatId}: 權限確認逾時，操作已自動拒絕", chatId);
                try
                {
                    await _botClient.SendMessage(
                        chatId: chatId,
                        text: "⏱️ 確認逾時，操作已自動拒絕。");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Chat {ChatId}: 發送逾時通知失敗", chatId);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "確認請求失敗: {RequestId}", requestId);
            return false; // 發生錯誤 = 拒絕
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// 由 TelegramBotService 的 CallbackQuery handler 呼叫。
    /// 解析 callback data 並完成對應的 TCS。
    /// </summary>
    /// <param name="callbackData">格式: "perm:{requestId}:yes" 或 "perm:{requestId}:no"</param>
    /// <returns>true 表示此 callback 已被處理，false 表示不屬於 permission 系統。</returns>
    public bool TryHandleCallback(string callbackData)
    {
        if (!callbackData.StartsWith("perm:"))
            return false;

        var parts = callbackData.Split(':');
        if (parts.Length != 3)
            return false;

        var requestId = parts[1];
        var approved = parts[2] == "yes";

        if (_pending.TryGetValue(requestId, out var tcs))
        {
            tcs.TrySetResult(approved);
            _logger.LogInformation("權限確認回應: {RequestId} = {Result}",
                requestId, approved ? "允許" : "拒絕");
            return true;
        }

        _logger.LogWarning("收到過期或未知的權限確認: {CallbackData}", callbackData);
        return true; // 仍然回傳 true 表示格式正確（只是已過期）
    }
}
