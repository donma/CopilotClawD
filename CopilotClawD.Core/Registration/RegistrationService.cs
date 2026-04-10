using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotClawD.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotClawD.Core.Registration;

/// <summary>
/// 處理使用者自助註冊：驗證密碼、加入白名單、反寫 appsettings.secret.json。
/// </summary>
public class RegistrationService
{
    private readonly IOptionsMonitor<CopilotClawDConfig> _config;
    private readonly ILogger<RegistrationService> _logger;
    private readonly string _secretFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RegistrationService(
        IOptionsMonitor<CopilotClawDConfig> config,
        ILogger<RegistrationService> logger,
        string secretFilePath)
    {
        _config = config;
        _logger = logger;
        _secretFilePath = secretFilePath;
    }

    /// <summary>
    /// 自助註冊是否啟用（RegistrationPasscode 非空）。
    /// </summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(_config.CurrentValue.RegistrationPasscode);

    /// <summary>
    /// 檢查 userId 是否已在白名單中。
    /// </summary>
    public bool IsAllowed(long userId)
    {
        return _config.CurrentValue.AllowedUserIds.Contains(userId);
    }

    /// <summary>
    /// 嘗試用密碼註冊。成功回傳 true 並將 userId 寫入設定檔。
    /// </summary>
    public async Task<RegistrationResult> TryRegisterAsync(long userId, string username, string passcode)
    {
        if (!IsEnabled)
            return RegistrationResult.Disabled;

        // 已經在白名單
        if (IsAllowed(userId))
            return RegistrationResult.AlreadyRegistered;

        // 密碼比對
        if (!string.Equals(passcode.Trim(), _config.CurrentValue.RegistrationPasscode, StringComparison.Ordinal))
            return RegistrationResult.WrongPasscode;

        // 加入記憶體白名單
        _config.CurrentValue.AllowedUserIds.Add(userId);

        // 反寫 appsettings.secret.json
        await PersistAllowedUserIdsAsync(userId, username);

        _logger.LogInformation(
            "使用者 {Username}({UserId}) 註冊成功，已加入白名單並寫入 {FilePath}",
            username, userId, _secretFilePath);

        return RegistrationResult.Success;
    }

    private async Task PersistAllowedUserIdsAsync(long newUserId, string username)
    {
        await _lock.WaitAsync();
        try
        {
            JsonNode? root;

            // 讀取現有檔案，若不存在則建立新的
            if (File.Exists(_secretFilePath))
            {
                var json = await File.ReadAllTextAsync(_secretFilePath);
                root = JsonNode.Parse(json) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            // 確保 CopilotClawD 節點存在
            var dclaw = root["CopilotClawD"]?.AsObject();
            if (dclaw is null)
            {
                dclaw = new JsonObject();
                root["CopilotClawD"] = dclaw;
            }

            // 讀取現有的 AllowedUserIds，或建立新的
            var existingIds = new HashSet<long>();
            if (dclaw["AllowedUserIds"] is JsonArray existingArray)
            {
                foreach (var item in existingArray)
                {
                    if (item?.GetValue<long>() is long id)
                        existingIds.Add(id);
                }
            }

            // 加入新 userId
            existingIds.Add(newUserId);

            // 替換陣列
            var newArray = new JsonArray();
            foreach (var id in existingIds.Order())
            {
                newArray.Add(id);
            }
            dclaw["AllowedUserIds"] = newArray;

            // 寫入
            var dir = Path.GetDirectoryName(_secretFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(_secretFilePath, root.ToJsonString(options));

            _logger.LogInformation("已將 UserId {UserId} ({Username}) 寫入 {FilePath}",
                newUserId, username, _secretFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "寫入 {FilePath} 失敗", _secretFilePath);
        }
        finally
        {
            _lock.Release();
        }
    }
}

public enum RegistrationResult
{
    Success,
    AlreadyRegistered,
    WrongPasscode,
    Disabled
}
