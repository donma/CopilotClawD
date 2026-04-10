using CopilotClawD.Core.Configuration;
using CopilotClawD.Core.Memory;

namespace CopilotClawD.Core.Agents;

/// <summary>
/// 管理 AI Agent 的系統提示詞。
/// Copilot SDK 已有內建的 coding agent system prompt，
/// 我們只需 Append 額外的 CopilotClawD-specific 指令與專案 context。
/// </summary>
public static class SystemPrompts
{
    /// <summary>
    /// CopilotClawD 專屬附加指令（會 append 到 Copilot SDK 的內建 system prompt 之後）。
    /// </summary>
    private const string DClawBasePrompt = """
        You are also serving as "CopilotClawD", a Telegram Bot AI agent on the user's local Windows machine.
        The user is interacting with you through Telegram on their phone.

        Additional guidelines for CopilotClawD context:
        - Be concise — the user reads your replies on a phone screen
        - Use Markdown formatting for code blocks and emphasis
        - Default language for conversation: 繁體中文 (Traditional Chinese), but respond in whatever language the user uses
        - When showing code, always specify the language in fenced code blocks
        - When performing file operations, always confirm what you did (e.g., "已讀取 src/Program.cs", "已寫入 test.cs")
        - For destructive operations (overwriting files, git commit), briefly explain what you're about to do
        - IMPORTANT: When displaying Windows file paths (e.g. D:\AI_PROJECTS\SPACreator), always wrap them in backticks (`D:\AI_PROJECTS\SPACreator`). Never output bare Windows paths in plain text — the backslashes will be misinterpreted by Telegram's Markdown parser and disappear.
        """;

    /// <summary>
    /// FILE SENDING PROTOCOL 區段（動態注入 swapDirectory 路徑）。
    /// </summary>
    private static string BuildFileSendingProtocol(string swapDirectory)
    {
        var screenshotPath = Path.Combine(swapDirectory, "screenshot.jpg");
        return $"""
        - IMPORTANT — FILE SENDING PROTOCOL: The CopilotClawD bot can forward local files to the user on Telegram.
          When you take a screenshot or produce any image/file that the user should receive, follow these steps:
          1. Save the file to this exact path: {screenshotPath}
             (This is the designated swap directory. Always use this fixed filename for screenshots.)
          2. After saving, output the following marker on its own line in your text reply:
             [SEND_FILE: {screenshotPath}]
             The bot monitors this marker AND actively scans the swap directory after your reply —
             so the file will be delivered even if the marker is missing. But always output it anyway.
          Rules:
          * Save to the exact path above — do NOT use $env:TEMP, desktop, or any other location.
          * Place the marker on its own line, NOT inside a code block or backticks.
          * Supported image formats (sent as photo): png, jpg, jpeg, gif, webp. Others sent as file attachment.
          * The bot deletes the file after sending. You do not need to clean up.
        """;
    }

    /// <summary>
    /// 組裝 Copilot SDK SystemMessage 的附加內容。
    /// 使用 SystemMessageMode.Append 模式，加在 Copilot 內建 prompt 之後。
    /// </summary>
    /// <param name="currentProject">目前 active project（null = 無專案）。</param>
    /// <param name="memories">跨 Session 的 AI 摘要記憶（null 或空 = 無歷史記憶）。</param>
    /// <param name="currentProjectKey">目前專案的 key（用於記憶排序）。</param>
    /// <param name="swapDirectory">暫存目錄路徑（注入到 FILE SENDING PROTOCOL）。留空則不注入截圖指引。</param>
    public static string Build(
        ProjectConfig? currentProject = null,
        IReadOnlyList<MemoryEntry>? memories = null,
        string? currentProjectKey = null,
        string? swapDirectory = null)
    {
        var parts = new List<string> { DClawBasePrompt };

        // FILE SENDING PROTOCOL（含 swap 路徑）
        if (!string.IsNullOrWhiteSpace(swapDirectory))
        {
            parts.Add(BuildFileSendingProtocol(swapDirectory));
        }

        // 專案 context
        if (currentProject is not null)
        {
            parts.Add($"""

                Current project context:
                - Path: {currentProject.Path}
                - Description: {currentProject.Description}

                When the user asks about code, assume they are referring to this project unless stated otherwise.
                All file operations and shell commands should default to this project's directory.
                """);
        }

        // 跨 Session 記憶注入（依專案相關性排序：同專案優先，再按時間倒序）
        if (memories is { Count: > 0 })
        {
            IReadOnlyList<MemoryEntry> sorted = memories;
            if (!string.IsNullOrEmpty(currentProjectKey))
            {
                sorted = memories
                    .OrderByDescending(m => string.Equals(m.ProjectKey, currentProjectKey, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(m => m.CreatedAt)
                    .ToList();
            }

            parts.Add(BuildMemorySection(sorted));
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// 組裝跨 Session 記憶區段，注入 SystemPrompt。
    /// </summary>
    private static string BuildMemorySection(IReadOnlyList<MemoryEntry> memories)
    {
        var lines = new List<string>
        {
            "",
            "Previous session memory (summaries from past conversations with this user):",
            "Use this context to maintain continuity — reference past decisions, preferences, and progress when relevant.",
            ""
        };

        for (var i = 0; i < memories.Count; i++)
        {
            var m = memories[i];
            var timeAgo = FormatTimeAgo(m.CreatedAt);
            var projectInfo = !string.IsNullOrEmpty(m.ProjectKey) ? $" | Project: {m.ProjectKey}" : "";
            var modelInfo = !string.IsNullOrEmpty(m.Model) ? $" | Model: {m.Model}" : "";

            lines.Add($"[Memory {i + 1}] ({timeAgo}{projectInfo}{modelInfo})");
            lines.Add(m.Summary);
            lines.Add("");
        }

        lines.Add("End of previous session memory.");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 將時間差格式化為人類可讀的字串（e.g., "2 hours ago", "3 days ago"）。
    /// </summary>
    private static string FormatTimeAgo(DateTimeOffset timestamp)
    {
        var diff = DateTimeOffset.UtcNow - timestamp;

        return diff.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)diff.TotalMinutes} min ago",
            < 1440 => $"{(int)diff.TotalHours} hours ago",
            < 43200 => $"{(int)diff.TotalDays} days ago",
            _ => timestamp.ToString("yyyy-MM-dd")
        };
    }
}
