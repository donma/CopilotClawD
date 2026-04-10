using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CopilotClawD.Core.Memory;

/// <summary>
/// 將跨 Session AI 摘要記憶以 JSON 檔案持久化至磁碟。
/// 每個 chatId 儲存最多 maxEntries 條摘要（FIFO，超過自動淘汰最舊的）。
/// 檔案路徑預設為 App 目錄下的 memory.json。
///
/// 寫入策略：記憶體快取即時更新，磁碟寫入以 debounce 方式批次處理（預設 5 秒內的變更合併為一次寫入）。
/// </summary>
public class JsonFileMemoryStore : IMemoryStore, IAsyncDisposable
{
    private readonly string _filePath;
    private readonly int _maxEntries;
    private readonly ILogger<JsonFileMemoryStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // 記憶體快取（啟動時從檔案載入）
    private ConcurrentDictionary<long, List<MemoryEntry>>? _cache;

    // 批次寫入：debounce timer
    private readonly TimeSpan _persistDelay = TimeSpan.FromSeconds(5);
    private Timer? _persistTimer;
    private volatile bool _dirty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JsonFileMemoryStore(string filePath, int maxEntries, ILogger<JsonFileMemoryStore> logger)
    {
        _filePath = filePath;
        _maxEntries = maxEntries > 0 ? maxEntries : 10;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetAsync(long chatId)
    {
        var cache = await EnsureCacheLoadedAsync();
        return cache.TryGetValue(chatId, out var entries) ? entries.AsReadOnly() : [];
    }

    public async Task AddAsync(long chatId, MemoryEntry entry)
    {
        var cache = await EnsureCacheLoadedAsync();

        var entries = cache.GetOrAdd(chatId, _ => []);

        await _lock.WaitAsync();
        try
        {
            entries.Add(entry);

            // FIFO 淘汰：超過上限移除最舊的
            while (entries.Count > _maxEntries)
            {
                entries.RemoveAt(0);
            }
        }
        finally
        {
            _lock.Release();
        }

        // 標記 dirty，排程批次寫入（非同步，不阻塞呼叫方）
        SchedulePersist();

        _logger.LogInformation("Chat {ChatId}: 新增記憶條目（{Trigger}），目前共 {Count} 條",
            chatId, entry.Trigger, entries.Count);
    }

    public async Task ClearAsync(long chatId)
    {
        var cache = await EnsureCacheLoadedAsync();
        if (cache.TryRemove(chatId, out _))
        {
            SchedulePersist();
            _logger.LogInformation("Chat {ChatId}: 已清除所有記憶", chatId);
        }
    }

    public async Task<int> CountAsync(long chatId)
    {
        var cache = await EnsureCacheLoadedAsync();
        return cache.TryGetValue(chatId, out var entries) ? entries.Count : 0;
    }

    /// <summary>
    /// 確保記憶體快取已從檔案載入。
    /// </summary>
    private async Task<ConcurrentDictionary<long, List<MemoryEntry>>> EnsureCacheLoadedAsync()
    {
        if (_cache is not null) return _cache;

        await _lock.WaitAsync();
        try
        {
            // Double-check
            if (_cache is not null) return _cache;

            if (File.Exists(_filePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_filePath);
                    var dict = JsonSerializer.Deserialize<Dictionary<long, List<MemoryEntry>>>(json, JsonOptions);
                    _cache = dict is not null
                        ? new ConcurrentDictionary<long, List<MemoryEntry>>(dict)
                        : new ConcurrentDictionary<long, List<MemoryEntry>>();

                    var totalEntries = _cache.Values.Sum(e => e.Count);
                    _logger.LogInformation("從 {FilePath} 載入 {ChatCount} 個 chat 的 {EntryCount} 條記憶",
                        _filePath, _cache.Count, totalEntries);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "無法載入記憶檔案 {FilePath}，將使用空白狀態", _filePath);
                    _cache = new ConcurrentDictionary<long, List<MemoryEntry>>();
                }
            }
            else
            {
                _cache = new ConcurrentDictionary<long, List<MemoryEntry>>();
                _logger.LogInformation("記憶檔案 {FilePath} 不存在，將在首次儲存時建立", _filePath);
            }

            return _cache;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 排程批次寫入。使用 debounce 策略：每次呼叫重設計時器，
    /// 只在最後一次變更後 _persistDelay 秒才實際寫入磁碟。
    /// </summary>
    private void SchedulePersist()
    {
        _dirty = true;
        _persistTimer?.Dispose();
        _persistTimer = new Timer(_ => _ = PersistIfDirtyAsync(), null, _persistDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 若有未寫入的變更，執行一次磁碟寫入。
    /// </summary>
    private async Task PersistIfDirtyAsync()
    {
        if (!_dirty || _cache is null) return;

        await _lock.WaitAsync();
        try
        {
            if (!_dirty) return;
            _dirty = false;

            // 確保目錄存在
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_cache, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json);

            _logger.LogDebug("記憶狀態已批次寫入 {FilePath}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "寫入記憶檔案 {FilePath} 失敗", _filePath);
            _dirty = true; // 寫入失敗，下次重試
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 關閉時確保所有未寫入的記憶都已持久化。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_persistTimer is not null)
        {
            await _persistTimer.DisposeAsync();
            _persistTimer = null;
        }

        // 確保 dirty 資料寫入
        await PersistIfDirtyAsync();
    }
}
