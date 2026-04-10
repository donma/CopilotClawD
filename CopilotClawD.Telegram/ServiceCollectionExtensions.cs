using CopilotClawD.Core.Configuration;
using CopilotClawD.Core.Registration;
using CopilotClawD.Core.Security;
using CopilotClawD.Telegram.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace CopilotClawD.Telegram;

/// <summary>
/// DClaw.Telegram 的 DI 擴充方法。
/// 在主程式中呼叫 builder.Services.AddCopilotClawDTelegram() 即可註冊 Telegram Bot 相關服務。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 註冊 Telegram Bot Client、Handlers、BackgroundService。
    /// 前提：CopilotClawDConfig 已透過 Options 綁定。
    /// </summary>
    public static IServiceCollection AddCopilotClawDTelegram(this IServiceCollection services)
    {
        // Telegram Bot Client
        services.AddSingleton<ITelegramBotClient>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<CopilotClawDConfig>>().Value;
            if (string.IsNullOrWhiteSpace(config.TelegramBotToken))
                throw new InvalidOperationException(
                    "TelegramBotToken 未設定，請在 appsettings.secret.json 中填入 Bot Token。" +
                    "參考 appsettings.secret.example.json 範本。");

            return new TelegramBotClient(config.TelegramBotToken);
        });

        // Handlers
        services.AddSingleton<CommandHandler>();
        services.AddSingleton<MessageHandler>();

        // Permission Confirmer（Telegram Inline Keyboard 確認危險操作）
        services.AddSingleton<TelegramPermissionConfirmer>();
        services.AddSingleton<IPermissionConfirmer>(sp => sp.GetRequiredService<TelegramPermissionConfirmer>());

        // Registration Service（白名單自助註冊，反寫 appsettings.secret.json）
        services.AddSingleton<RegistrationService>(sp =>
        {
            var config = sp.GetRequiredService<IOptionsMonitor<CopilotClawDConfig>>();
            var logger = sp.GetRequiredService<ILogger<RegistrationService>>();
            var secretFilePath = Path.Combine(AppContext.BaseDirectory, "appsettings.secret.json");
            return new RegistrationService(config, logger, secretFilePath);
        });

        // Bot BackgroundService
        services.AddHostedService<TelegramBotService>();

        return services;
    }
}
