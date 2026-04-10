namespace CopilotClawD.Telegram;

/// <summary>
/// Telegram MarkdownV2 escape 工具。
/// 集中管理，避免各 handler 各自維護一份。
/// </summary>
internal static class MarkdownV2Helper
{
    /// <summary>
    /// 跳脫 MarkdownV2 特殊字元，讓任意字串可安全嵌入 MarkdownV2 訊息的純文字段落。
    /// 規則：先 escape backslash，再 escape 其餘 18 個特殊字元。
    /// </summary>
    public static string Escape(string text)
    {
        text = text.Replace("\\", "\\\\");
        var specialChars = new[] { '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };
        foreach (var ch in specialChars)
            text = text.Replace(ch.ToString(), $"\\{ch}");
        return text;
    }
}
