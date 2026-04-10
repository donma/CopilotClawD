using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CopilotClawD.Core;

/// <summary>
/// 自我更新服務：編譯新版本 → 啟動新 Process → 舊 Process 自行結束。
/// 避免用 process name kill（否則新舊都會被殺）。
/// </summary>
public class SelfUpdateService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SelfUpdateService> _logger;

    // 方案根目錄：從執行檔往上找 .slnx 或固定 3 層（bin/Debug/net10.0 → DClaw → root）
    private static readonly string SolutionDir = ResolveSolutionDir();

    // 自動偵測當前 Configuration（從執行路徑判斷 Debug / Release）
    private static readonly string CurrentConfiguration = DetectConfiguration();

    // Build 時使用另一個 Configuration，避免檔案鎖定（自己正在跑的 DLL 無法被覆寫）
    private static readonly string BuildConfiguration =
        CurrentConfiguration.Equals("Debug", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    public SelfUpdateService(IHostApplicationLifetime lifetime, ILogger<SelfUpdateService> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>
    /// 執行「編譯 → 啟動新 Process → 舊 Process 自行停止」流程。
    /// 回傳 null 代表成功（舊 Process 即將停止），否則回傳錯誤訊息。
    /// 注意：此方法啟動後不可被外部取消（傳入的 ct 僅用於初始狀態回報）。
    /// </summary>
    public async Task<string?> UpdateAndRestartAsync(
        Func<string, Task> onStatus,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Self-update: solution dir = {Dir}, current = {Current}, build target = {Build}",
            SolutionDir, CurrentConfiguration, BuildConfiguration);

        // 使用獨立的 CancellationToken，避免 Telegram polling 取消導致 build 中斷
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var buildCt = timeoutCts.Token;

        // ── 1. 編譯（用另一個 Configuration 避免檔案鎖定） ────────
        await onStatus($"🔨 編譯中（{BuildConfiguration}）...");

        var buildOutput = new System.Text.StringBuilder();
        var buildResult = await RunProcessAsync(
            "dotnet", $"build \"{SolutionDir}\" -c {BuildConfiguration} --nologo -v minimal",
            output => buildOutput.AppendLine(output),
            buildCt);

        if (buildResult != 0)
        {
            _logger.LogError("Build failed (exit {Code}):\n{Output}", buildResult, buildOutput);
            var tail = GetTail(buildOutput.ToString(), 20);
            return TruncateForTelegram($"❌ 編譯失敗（exit {buildResult}）：\n\n{tail}");
        }

        await onStatus("✅ 編譯完成，啟動新版本...");

        // ── 2. 找到新版執行檔 ─────────────────────────────────────
        var exePath = Path.Combine(SolutionDir, "CopilotClawD", "bin", BuildConfiguration, "net10.0", "CopilotClawD.exe");
        if (!File.Exists(exePath))
        {
            return $"❌ 找不到執行檔：{exePath}";
        }

        // ── 3. 啟動新 Process（透過 cmd /c start 完全脫離 Job Object） ─
        // 直接用 Process.Start(exePath, UseShellExecute=true) 在 Windows Job Object
        // 環境（終端機、Task Scheduler）下，子 Process 仍屬同一 Job，
        // 父 Process 結束時子 Process 也會被系統殺掉。
        // 改用 `cmd /c start "" "..."` 可讓新 Process 完全獨立。
        _logger.LogInformation("Self-update: launching {Exe}", exePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c start \"\" \"{exePath}\"",
            WorkingDirectory = Path.GetDirectoryName(exePath),
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process.Start(startInfo);

        // ── 4. 舊 Process 自行結束（不 kill by name） ────────────
        _logger.LogInformation("Self-update: new process started, stopping old instance (PID {Pid})",
            Environment.ProcessId);

        await onStatus("🔄 新版本已啟動，關閉舊版本中...");

        // 給 Telegram 訊息一點時間送出
        await Task.Delay(1500, CancellationToken.None);

        _lifetime.StopApplication();
        return null;
    }

    // ── helpers ──────────────────────────────────────────────────

    private static async Task<int> RunProcessAsync(
        string fileName, string arguments,
        Action<string> onOutput,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) onOutput(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    private static string ResolveSolutionDir()
    {
        // 從 AppContext.BaseDirectory 往上找，直到找到 .slnx 檔案
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.slnx").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }

        // 後備：假設執行在 bin/Debug/net10.0 → 往上 4 層
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    /// <summary>
    /// 從當前執行路徑偵測 Configuration（Debug / Release）。
    /// 路徑格式：.../bin/{Configuration}/net10.0/DClaw.exe
    /// </summary>
    private static string DetectConfiguration()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // baseDir 類似 D:\...\DClaw\bin\Debug\net10.0
        var parts = baseDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 從尾端往回找 "bin"，bin 的下一個就是 Configuration
        for (int i = parts.Length - 1; i >= 1; i--)
        {
            if (parts[i - 1].Equals("bin", StringComparison.OrdinalIgnoreCase))
                return parts[i]; // "Debug" or "Release"
        }

        // 後備
        return "Release";
    }

    private static string GetTail(string text, int lines)
    {
        // Windows build output uses \r\n; split on both to avoid trailing \r garbage
        var all = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }

    /// <summary>
    /// 截斷訊息以符合 Telegram 4096 字元限制。
    /// </summary>
    private static string TruncateForTelegram(string text, int maxLength = 4000)
    {
        if (text.Length <= maxLength)
            return text;
        return text[..(maxLength - 20)] + "\n...(已截斷)```";
    }
}
