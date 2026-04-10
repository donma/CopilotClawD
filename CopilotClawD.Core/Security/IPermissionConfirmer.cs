namespace CopilotClawD.Core.Security;

/// <summary>
/// 抽象介面：當權限等級為 Dangerous 時，向使用者確認是否允許執行。
/// 由各 IM Bot 模組（Telegram, Discord 等）實作。
/// </summary>
public interface IPermissionConfirmer
{
    /// <summary>
    /// 向指定 chatId 的使用者發送確認請求。
    /// </summary>
    /// <param name="chatId">目標聊天室 ID。</param>
    /// <param name="description">操作描述（顯示給使用者看的文字）。</param>
    /// <param name="timeout">等待使用者回應的逾時時間。</param>
    /// <returns>true = 使用者允許，false = 使用者拒絕或逾時。</returns>
    Task<bool> ConfirmAsync(long chatId, string description, TimeSpan timeout);
}
