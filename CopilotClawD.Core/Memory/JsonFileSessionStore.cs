using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CopilotClawD.Core.Memory;

/// <summary>
/// 將 session 狀態以 JSON 檔案持久化至磁碟。
/// 檔案路徑預設為 App 目錄下的 sessions.json。
/// 使用 SemaphoreSlim 防止並行寫入損毀檔案。
/// </summary>
public class JsonFileSessionStore : ISessionStore
{
    private readonly string _filePath;
    private readonly ILogger<JsonFileSessionStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // 記憶體快取（啟動時從檔案載入）
    private ConcurrentDictionary<long, ChatSessionState>? _cache;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JsonFileSessionStore(string filePath, ILogger<JsonFileSessionStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public async Task<ChatSessionState?> GetAsync(long chatId)
    {
        var cache = await EnsureCacheLoadedAsync();
        return cache.TryGetValue(chatId, out var state) ? state : null;
    }

    public async Task SaveAsync(long chatId, ChatSessionState state)
    {
        var cache = await EnsureCacheLoadedAsync();
        cache[chatId] = state;
        await PersistAsync();
    }

    public async Task DeleteAsync(long chatId)
    {
        var cache = await EnsureCacheLoadedAsync();
        if (cache.TryRemove(chatId, out _))
        {
            await PersistAsync();
        }
    }

    public async Task<IReadOnlyDictionary<long, ChatSessionState>> GetAllAsync()
    {
        var cache = await EnsureCacheLoadedAsync();
        return cache;
    }

    /// <summary>
    /// 確保記憶體快取已從檔案載入。
    /// </summary>
    private async Task<ConcurrentDictionary<long, ChatSessionState>> EnsureCacheLoadedAsync()
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
                    var dict = JsonSerializer.Deserialize<Dictionary<long, ChatSessionState>>(json, JsonOptions);
                    _cache = dict is not null
                        ? new ConcurrentDictionary<long, ChatSessionState>(dict)
                        : new ConcurrentDictionary<long, ChatSessionState>();

                    _logger.LogInformation("從 {FilePath} 載入 {Count} 筆 session 狀態",
                        _filePath, _cache.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "無法載入 session 檔案 {FilePath}，將使用空白狀態", _filePath);
                    _cache = new ConcurrentDictionary<long, ChatSessionState>();
                }
            }
            else
            {
                _cache = new ConcurrentDictionary<long, ChatSessionState>();
                _logger.LogInformation("Session 檔案 {FilePath} 不存在，將在首次儲存時建立", _filePath);
            }

            return _cache;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 將記憶體快取寫入磁碟。
    /// </summary>
    private async Task PersistAsync()
    {
        await _lock.WaitAsync();
        try
        {
            // 確保目錄存在
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_cache, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json);

            _logger.LogDebug("Session 狀態已寫入 {FilePath}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "寫入 session 檔案 {FilePath} 失敗", _filePath);
        }
        finally
        {
            _lock.Release();
        }
    }
}
