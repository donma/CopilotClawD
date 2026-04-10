using CopilotClawD.Core.Configuration;
using Microsoft.Extensions.Options;

namespace CopilotClawD.Core.Security;

/// <summary>
/// 掃描文字並將已知的 Token / Secret 字串替換為 [REDACTED]。
/// 用於在將 AI 回覆送到 Telegram 之前防止機密洩漏。
/// 快取已排序的 secrets list，並在設定變更時透過 IOptionsMonitor 自動失效，
/// 避免在每個 streaming chunk 上重複分配 List。
/// </summary>
public class SecretRedactor : IDisposable
{
    private readonly IOptionsMonitor<CopilotClawDConfig> _config;
    private readonly IDisposable? _changeToken;

    // 快取（volatile 確保跨執行緒可見性；寫入側以 lock 保護）
    private readonly object _cacheLock = new();
    private volatile List<string>? _cachedSecrets;

    public SecretRedactor(IOptionsMonitor<CopilotClawDConfig> config)
    {
        _config = config;

        // 設定變更時清除快取，下次呼叫 Redact() 時重建
        _changeToken = config.OnChange(_ =>
        {
            lock (_cacheLock)
                _cachedSecrets = null;
        });
    }

    /// <summary>
    /// 掃描並遮蔽文字中的已知機密。
    /// </summary>
    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var config = _config.CurrentValue;

        if (!config.Security.RedactSecrets)
            return text;

        var secrets = GetCachedSecrets(config);
        if (secrets.Count == 0)
            return text;

        var result = text;
        foreach (var secret in secrets)
        {
            if (secret.Length >= 4 && result.Contains(secret, StringComparison.Ordinal))
            {
                result = result.Replace(secret, "[REDACTED]");
            }
        }

        return result;
    }

    public void Dispose() => _changeToken?.Dispose();

    // ── Private Helpers ──────────────────────────────────────────

    private List<string> GetCachedSecrets(CopilotClawDConfig config)
    {
        // Fast path: cache hit (volatile read)
        if (_cachedSecrets is { } cached)
            return cached;

        lock (_cacheLock)
        {
            // Double-check inside lock
            if (_cachedSecrets is not null)
                return _cachedSecrets;

            var secrets = new List<string>();

            if (!string.IsNullOrWhiteSpace(config.TelegramBotToken))
                secrets.Add(config.TelegramBotToken);

            if (!string.IsNullOrWhiteSpace(config.RegistrationPasscode))
                secrets.Add(config.RegistrationPasscode);

            foreach (var extra in config.Security.AdditionalSecrets)
            {
                if (!string.IsNullOrWhiteSpace(extra))
                    secrets.Add(extra);
            }

            // Sort longest-first to avoid short-string shadowing long-string replacements
            secrets.Sort((a, b) => b.Length.CompareTo(a.Length));

            _cachedSecrets = secrets;
            return secrets;
        }
    }
}
