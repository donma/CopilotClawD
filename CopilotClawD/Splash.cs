namespace CopilotClawD;

/// <summary>
/// Console 啟動動畫 — 炫炮的 CopilotClawD ASCII Art + 逐字打印 + 色彩效果。
/// </summary>
public static class Splash
{
    // CopilotClawD ASCII Art — 兩行 banner
    private static readonly string[] ClawArt =
    [
        @"  ██████╗ ██████╗ ██████╗ ██╗██╗      ██████╗ ████████╗",
        @" ██╔════╝██╔═══██╗██╔══██╗██║██║     ██╔═══██╗╚══██╔══╝",
        @" ██║     ██║   ██║██████╔╝██║██║     ██║   ██║   ██║   ",
        @" ██║     ██║   ██║██╔═══╝ ██║██║     ██║   ██║   ██║   ",
        @" ╚██████╗╚██████╔╝██║     ██║███████╗╚██████╔╝   ██║   ",
        @"  ╚═════╝ ╚═════╝ ╚═╝     ╚═╝╚══════╝ ╚═════╝    ╚═╝   ",
        @"",
        @"  ██████╗██╗      █████╗ ██╗    ██╗██████╗ ",
        @" ██╔════╝██║     ██╔══██╗██║    ██║██╔══██╗",
        @" ██║     ██║     ███████║██║ █╗ ██║██║  ██║",
        @" ██║     ██║     ██╔══██║██║███╗██║██║  ██║",
        @" ╚██████╗███████╗██║  ██║╚███╔███╔╝██████╔╝",
        @"  ╚═════╝╚══════╝╚═╝  ╚═╝ ╚══╝╚══╝ ╚═════╝ D",
    ];

    private static readonly string[] ClawIcon =
    [
        @"              ,---.",
        @"             / ,-, \",
        @"            / / | \ \",
        @"           | |  |  | |",
        @"           | |  |  | |",
        @"            \ \ | / /",
        @"             `.   .'",
        @"               | |",
        @"               | |",
        @"           ~~~~   ~~~~",
    ];

    private static readonly string Tagline = "  Your Local AI Agent  //  Grab Your Code  //  Powered by Copilot SDK";

    /// <summary>
    /// 播放完整的啟動動畫。
    /// </summary>
    public static async Task PlayAsync()
    {
        // 若無真正的 console（例如從 IDE redirect 或無視窗環境啟動），直接跳過動畫
        if (!IsConsoleAvailable())
            return;

        var originalFg = Console.ForegroundColor;
        var originalBg = Console.BackgroundColor;

        try
        {
            Console.Clear();
            Console.CursorVisible = false;

            // Phase 1: 爪子圖示逐行淡入
            await AnimateClawIconAsync();

            // Phase 2: CopilotClawD ASCII Art 逐行掃描
            await AnimateLogoAsync();

            // Phase 3: Tagline 打字機效果
            await TypewriterAsync(Tagline, ConsoleColor.DarkGray);
            Console.WriteLine();

            // Phase 4: 分隔線動畫
            await AnimateDividerAsync();
            Console.WriteLine();

            // Phase 5: 啟動資訊
            await PrintStartupInfoAsync();

            Console.CursorVisible = true;
        }
        catch
        {
            // Console 動畫失敗不應影響程式啟動
            try { Console.CursorVisible = true; } catch { /* 無 console 視窗時忽略 */ }
            try { Console.ForegroundColor = originalFg; } catch { /* 同上 */ }
            try { Console.BackgroundColor = originalBg; } catch { /* 同上 */ }
        }
    }

    private static async Task AnimateClawIconAsync()
    {
        var colors = new[] { ConsoleColor.DarkCyan, ConsoleColor.Cyan, ConsoleColor.White };

        foreach (var line in ClawIcon)
        {
            var colorIndex = Array.IndexOf(ClawIcon, line) % colors.Length;
            Console.ForegroundColor = colors[colorIndex];
            Console.WriteLine(line);
            await Task.Delay(40);
        }

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine();
        await Task.Delay(100);
    }

    private static async Task AnimateLogoAsync()
    {
        // 從暗到亮的色彩漸變
        var gradient = new[]
        {
            ConsoleColor.DarkBlue,
            ConsoleColor.DarkCyan,
            ConsoleColor.Cyan,
            ConsoleColor.White,
            ConsoleColor.Yellow,
            ConsoleColor.White,
            ConsoleColor.Gray,       // 空行
            ConsoleColor.DarkCyan,
            ConsoleColor.Cyan,
            ConsoleColor.White,
            ConsoleColor.Yellow,
            ConsoleColor.White,
            ConsoleColor.Cyan,       // 最後一行含 "D"
        };

        for (var i = 0; i < ClawArt.Length; i++)
        {
            var line = ClawArt[i];
            var color = gradient[i % gradient.Length];
            Console.ForegroundColor = color;

            // 逐字掃描效果
            foreach (var ch in line)
            {
                Console.Write(ch);
                if (ch != ' ')
                    await Task.Delay(1); // 非空白字元略有延遲
            }
            Console.WriteLine();
        }

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine();
        await Task.Delay(150);
    }

    private static async Task TypewriterAsync(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;

        foreach (var ch in text)
        {
            Console.Write(ch);
            await Task.Delay(ch == ' ' ? 10 : 20);
        }
        Console.WriteLine();
    }

    private static async Task AnimateDividerAsync()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        var width = Math.Min(Console.WindowWidth - 1, 70);

        for (var i = 0; i < width; i++)
        {
            Console.Write(i % 2 == 0 ? '═' : '─');
            if (i % 4 == 0)
                await Task.Delay(5);
        }
        Console.WriteLine();
    }

    private static async Task PrintStartupInfoAsync()
    {
        var items = new (string Label, string Value, ConsoleColor ValueColor)[]
        {
            ("  Runtime", $".NET {Environment.Version}", ConsoleColor.Green),
            ("  PID", $"{Environment.ProcessId}", ConsoleColor.Yellow),
            ("  Time", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}", ConsoleColor.Cyan),
            ("  OS", $"{Environment.OSVersion}", ConsoleColor.Magenta),
        };

        foreach (var (label, value, valueColor) in items)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"{label}: ");
            Console.ForegroundColor = valueColor;
            Console.WriteLine(value);
            await Task.Delay(60);
        }

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine();
    }

    /// <summary>
    /// 輸出帶顏色的狀態訊息（啟動後各模組載入時用）。
    /// </summary>
    public static void Status(string component, string message, ConsoleColor color = ConsoleColor.Cyan)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  [{DateTime.Now:HH:mm:ss}] ");
        Console.ForegroundColor = color;
        Console.Write(component);
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($" {message}");
    }

    /// <summary>
    /// 輸出成功訊息。
    /// </summary>
    public static void Success(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  ✓ ");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(message);
    }

    /// <summary>
    /// 輸出錯誤訊息。
    /// </summary>
    public static void Error(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("  ✗ ");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(message);
    }

    /// <summary>
    /// 偵測是否有真正的 console 視窗（避免在無 console 環境下呼叫 Console API 拋 IOException）。
    /// </summary>
    private static bool IsConsoleAvailable()
    {
        try
        {
            // WindowWidth 在無 console 時會拋 IOException
            _ = Console.WindowWidth;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
