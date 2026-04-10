using System.Text.RegularExpressions;
using CopilotClawD.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotClawD.Core.Security;

/// <summary>
/// 工具執行權限的分級結果。
/// </summary>
public enum PermissionLevel
{
    /// <summary>安全：自動允許，不記錄。</summary>
    Safe,

    /// <summary>一般：自動允許，記錄日誌。</summary>
    Normal,

    /// <summary>危險：需要使用者透過 Telegram 確認。</summary>
    Dangerous,

    /// <summary>禁止：直接拒絕。</summary>
    Forbidden
}

/// <summary>
/// 權限評估結果。
/// </summary>
public record PermissionEvaluation(
    PermissionLevel Level,
    string Reason,
    string Detail);

/// <summary>
/// 根據設定檔中的 patterns 對工具呼叫進行分級評估。
/// </summary>
public class PermissionPolicy
{
    private readonly IOptionsMonitor<CopilotClawDConfig> _config;
    private readonly ILogger<PermissionPolicy> _logger;

    // 快取編譯後的 regex（在設定變更時重建）
    // 以 _cacheLock 保護，確保多 session 並行時不會 race
    private readonly object _cacheLock = new();
    private List<Regex>? _forbiddenRegexes;
    private List<Regex>? _dangerousRegexes;
    private List<Regex>? _safeRegexes;
    private string? _configHash;

    public PermissionPolicy(IOptionsMonitor<CopilotClawDConfig> config, ILogger<PermissionPolicy> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// 評估 Shell 指令的權限等級。
    /// </summary>
    public PermissionEvaluation EvaluateShellCommand(string command)
    {
        var security = _config.CurrentValue.Security;
        if (!security.Enabled)
            return new PermissionEvaluation(PermissionLevel.Safe, "安全檢查已停用", command);

        var (forbidden, dangerous, safe) = GetRegexCache(security);

        // 1. 檢查安全白名單（優先於 Dangerous）
        foreach (var regex in safe)
        {
            try
            {
                if (regex.IsMatch(command))
                {
                    _logger.LogDebug("Shell 指令符合安全白名單: {Command} (matched: {Pattern})", command, regex.ToString());
                    return new PermissionEvaluation(PermissionLevel.Normal, "安全白名單指令", command);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // 白名單逾時 → 繼續往下檢查（不影響安全性）
            }
        }

        // 2. 檢查禁止清單
        foreach (var regex in forbidden)
        {
            try
            {
                if (regex.IsMatch(command))
                {
                    _logger.LogWarning("Shell 指令被禁止: {Command} (matched: {Pattern})", command, regex.ToString());
                    return new PermissionEvaluation(PermissionLevel.Forbidden,
                        $"指令匹配禁止規則: {regex}", command);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Pattern 逾時 → 保守起見視為 Forbidden
                _logger.LogWarning("Forbidden regex 逾時: {Pattern} 對指令: {Command}，保守拒絕", regex, command);
                return new PermissionEvaluation(PermissionLevel.Forbidden, "規則比對逾時（保守拒絕）", command);
            }
        }

        // 3. 檢查危險清單
        foreach (var regex in dangerous)
        {
            try
            {
                if (regex.IsMatch(command))
                {
                    _logger.LogInformation("Shell 指令需確認: {Command} (matched: {Pattern})", command, regex.ToString());
                    return new PermissionEvaluation(PermissionLevel.Dangerous,
                        $"指令匹配危險規則: {regex}", command);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Dangerous pattern 逾時 → 保守起見要求確認
                _logger.LogWarning("Dangerous regex 逾時: {Pattern} 對指令: {Command}，要求使用者確認", regex, command);
                return new PermissionEvaluation(PermissionLevel.Dangerous, "規則比對逾時（需確認）", command);
            }
        }

        // 4. 預設為一般（允許 + 記錄）
        return new PermissionEvaluation(PermissionLevel.Normal, "一般指令", command);
    }

    /// <summary>
    /// 評估檔案讀取的權限等級。
    /// </summary>
    public PermissionEvaluation EvaluateRead(string path)
    {
        var security = _config.CurrentValue.Security;
        if (!security.Enabled)
            return new PermissionEvaluation(PermissionLevel.Safe, "安全檢查已停用", path);

        if (IsProtectedPath(path, security))
        {
            _logger.LogWarning("讀取被禁止: {Path}", path);
            return new PermissionEvaluation(PermissionLevel.Forbidden,
                "路徑匹配保護規則", path);
        }

        return new PermissionEvaluation(PermissionLevel.Safe, "讀取操作", path);
    }

    /// <summary>
    /// 評估檔案寫入的權限等級。
    /// </summary>
    public PermissionEvaluation EvaluateWrite(string path)
    {
        var security = _config.CurrentValue.Security;
        if (!security.Enabled)
            return new PermissionEvaluation(PermissionLevel.Safe, "安全檢查已停用", path);

        if (IsProtectedPath(path, security))
        {
            _logger.LogWarning("寫入被禁止: {Path}", path);
            return new PermissionEvaluation(PermissionLevel.Forbidden,
                "路徑匹配保護規則", path);
        }

        // 寫入預設為 Normal（允許 + 記錄）
        return new PermissionEvaluation(PermissionLevel.Normal, "寫入操作", path);
    }

    /// <summary>
    /// 評估 URL 存取的權限等級。
    /// </summary>
    public PermissionEvaluation EvaluateUrl(string url)
    {
        // file:// 完全禁止，可繞過 ProtectedPathPatterns
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return new PermissionEvaluation(PermissionLevel.Forbidden, "禁止本機檔案 URL 存取", url);

        // http:// 非加密連線列為危險（需使用者確認）
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return new PermissionEvaluation(PermissionLevel.Dangerous, "非加密 HTTP 連線", url);

        // https:// 及其他預設安全
        return new PermissionEvaluation(PermissionLevel.Safe, "URL 存取", url);
    }

    /// <summary>
    /// 評估 MCP 工具呼叫的權限等級。
    /// </summary>
    public PermissionEvaluation EvaluateMcp(string toolName, string? serverName)
    {
        // MCP 工具呼叫預設為 Normal（允許 + 記錄）
        return new PermissionEvaluation(PermissionLevel.Normal, "MCP 工具呼叫",
            $"{serverName}/{toolName}");
    }

    // ── Private Helpers ──────────────────────────────────────────

    private bool IsProtectedPath(string path, SecurityConfig security)
    {
        // 標準化路徑分隔符 + 統一小寫（避免大小寫混淆）
        var normalizedPath = path.Replace('\\', '/').ToLowerInvariant();

        foreach (var pattern in security.ProtectedPathPatterns)
        {
            if (MatchGlob(normalizedPath, pattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 簡易 glob 匹配：支援 * (單層) 和 ** (多層)。
    /// 輸入路徑預期已正規化（/ 分隔、小寫）。
    /// </summary>
    private static bool MatchGlob(string path, string pattern)
    {
        // 標準化 pattern：/ 分隔、小寫（與 path 保持一致）
        var normalizedPattern = pattern.Replace('\\', '/').ToLowerInvariant();

        // 將 glob pattern 轉換為 regex
        var regexPattern = "^" + Regex.Escape(normalizedPattern)
            .Replace(@"\*\*", "§DOUBLESTAR§")   // 暫存 **
            .Replace(@"\*", "[^/]*")              // * = 單層
            .Replace("§DOUBLESTAR§", ".*")        // ** = 多層
            + "$";

        // 路徑已小寫，不再需要 IgnoreCase
        return Regex.IsMatch(path, regexPattern);
    }

    /// <summary>
    /// 取得（或重建）編譯好的 regex 快取。
    /// 以 _cacheLock 保護，確保多 session 並行時不會 race。
    /// </summary>
    private (List<Regex> Forbidden, List<Regex> Dangerous, List<Regex> Safe) GetRegexCache(SecurityConfig security)
    {
        // 用 patterns 的 hash 判斷是否需要重建快取
        var hash = string.Join("|", security.ForbiddenCommandPatterns)
                 + "||"
                 + string.Join("|", security.DangerousCommandPatterns)
                 + "||"
                 + string.Join("|", security.SafeCommandPatterns);

        lock (_cacheLock)
        {
            if (_configHash == hash && _forbiddenRegexes != null && _dangerousRegexes != null && _safeRegexes != null)
                return (_forbiddenRegexes, _dangerousRegexes, _safeRegexes);

            _forbiddenRegexes = CompilePatterns(security.ForbiddenCommandPatterns, "Forbidden");
            _dangerousRegexes = CompilePatterns(security.DangerousCommandPatterns, "Dangerous");
            _safeRegexes      = CompilePatterns(security.SafeCommandPatterns, "Safe");
            _configHash = hash;

            _logger.LogInformation(
                "PermissionPolicy regex 快取已重建: {Forbidden} forbidden, {Dangerous} dangerous, {Safe} safe",
                _forbiddenRegexes.Count, _dangerousRegexes.Count, _safeRegexes.Count);

            return (_forbiddenRegexes, _dangerousRegexes, _safeRegexes);
        }
    }

    private List<Regex> CompilePatterns(List<string> patterns, string label)
    {
        var result = new List<Regex>();
        foreach (var pattern in patterns)
        {
            try
            {
                result.Add(new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(1)));
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning("無效的 {Label} regex pattern '{Pattern}': {Error}", label, pattern, ex.Message);
            }
        }
        return result;
    }
}
