namespace CopilotClawD.Core.Memory;

/// <summary>
/// 儲存每個 Telegram chatId 對應的 Copilot session 資訊，
/// 讓 Bot 重啟後可以恢復 session。
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// 取得指定 chatId 的 session 狀態。若不存在回傳 null。
    /// </summary>
    Task<ChatSessionState?> GetAsync(long chatId);

    /// <summary>
    /// 儲存或更新指定 chatId 的 session 狀態。
    /// </summary>
    Task SaveAsync(long chatId, ChatSessionState state);

    /// <summary>
    /// 刪除指定 chatId 的 session 狀態。
    /// </summary>
    Task DeleteAsync(long chatId);

    /// <summary>
    /// 取得所有已儲存的 session 狀態。
    /// </summary>
    Task<IReadOnlyDictionary<long, ChatSessionState>> GetAllAsync();
}

/// <summary>
/// 每個 chatId 對應的持久化 session 狀態。
/// </summary>
public class ChatSessionState
{
    /// <summary>
    /// Copilot SDK 的 session ID，用於 ResumeSessionAsync。
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// 該 chat 的 active project key（null = 使用預設專案）。
    /// </summary>
    public string? ActiveProjectKey { get; set; }

    /// <summary>
    /// 該 chat 的 model override（null = 使用 DefaultModel）。
    /// </summary>
    public string? ModelOverride { get; set; }

    /// <summary>
    /// Session 建立時間。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 最後活動時間。
    /// </summary>
    public DateTimeOffset LastActiveAt { get; set; } = DateTimeOffset.UtcNow;
}
