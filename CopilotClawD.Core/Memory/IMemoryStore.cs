namespace CopilotClawD.Core.Memory;

/// <summary>
/// 跨 Session 的 AI 摘要記憶持久化。
/// 每個 chatId 可儲存多條摘要，新 Session 建立時注入 SystemPrompt。
/// </summary>
public interface IMemoryStore
{
    /// <summary>
    /// 取得指定 chatId 的所有記憶條目（按時間排序，最舊在前）。
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> GetAsync(long chatId);

    /// <summary>
    /// 新增一條記憶。若超過 maxEntries 上限，自動移除最舊的條目。
    /// </summary>
    Task AddAsync(long chatId, MemoryEntry entry);

    /// <summary>
    /// 清除指定 chatId 的所有記憶。
    /// </summary>
    Task ClearAsync(long chatId);

    /// <summary>
    /// 取得指定 chatId 的記憶條目數量。
    /// </summary>
    Task<int> CountAsync(long chatId);
}

/// <summary>
/// 單條跨 Session 記憶（AI 摘要）。
/// </summary>
public class MemoryEntry
{
    /// <summary>
    /// 摘要產生時間。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 觸發原因：SessionSwitch（切換專案/模型）、SessionClear（/clear）、
    /// SessionCorrupted（session 損壞）、GracefulShutdown（正常關閉）。
    /// </summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>
    /// 結束時的專案代號（若有）。
    /// </summary>
    public string? ProjectKey { get; set; }

    /// <summary>
    /// 結束時使用的模型。
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// AI 產生的對話摘要。
    /// </summary>
    public string Summary { get; set; } = string.Empty;
}
