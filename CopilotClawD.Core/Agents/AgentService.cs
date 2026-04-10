using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CopilotClawD.Core.Configuration;
using CopilotClawD.Core.Memory;
using CopilotClawD.Core.Security;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotClawD.Core.Agents;

/// <summary>
/// AI 對話核心編排服務 — 基於 GitHub Copilot SDK。
/// 每個 Telegram chatId 對應一個 CopilotSession + 獨立的 active project / model 設定。
/// Session 狀態透過 ISessionStore 持久化，重啟後可恢復。
/// </summary>
public class AgentService : IAsyncDisposable
{
    private readonly CopilotClient _copilotClient;
    private readonly IOptions<CopilotClawDConfig> _config;
    private readonly ILogger<AgentService> _logger;
    private readonly ISessionStore _sessionStore;
    private readonly IMemoryStore _memoryStore;
    private readonly PermissionPolicy _permissionPolicy;
    private readonly IPermissionConfirmer? _permissionConfirmer;

    // 記錄 Bot 啟動時間（用於 /status uptime 計算）
    private static readonly DateTimeOffset BotStartedAt = DateTimeOffset.UtcNow;

    // 每個 chatId 對應一個 session（含 session lock 防止並行呼叫同一 session）
    private readonly ConcurrentDictionary<long, ChatSession> _sessions = new();

    // 每個 chatId 的 active project key（null = 使用第一個專案）
    private readonly ConcurrentDictionary<long, string> _activeProjects = new();

    // 每個 chatId 的 model override（null = 使用 DefaultModel）
    private readonly ConcurrentDictionary<long, string> _modelOverrides = new();

    // 是否已從 store 載入過設定
    private bool _storeLoaded;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    // 本次程序生命週期內曾建立過 session 的 chatId 集合。
    // 只有第一次建立 session 才顯示 WelcomeBack 提醒；之後的 session 重建（切模型、idle timeout 等）不再提醒。
    private readonly HashSet<long> _seenChatIds = [];

    // Idle timeout background loop
    private readonly CancellationTokenSource _idleCts = new();

    public AgentService(
        CopilotClient copilotClient,
        IOptions<CopilotClawDConfig> config,
        ILogger<AgentService> logger,
        ISessionStore sessionStore,
        IMemoryStore memoryStore,
        PermissionPolicy permissionPolicy,
        IPermissionConfirmer? permissionConfirmer = null)
    {
        _copilotClient = copilotClient;
        _config = config;
        _logger = logger;
        _sessionStore = sessionStore;
        _memoryStore = memoryStore;
        _permissionPolicy = permissionPolicy;
        _permissionConfirmer = permissionConfirmer;

        // 啟動 idle timeout 背景迴圈
        _ = Task.Run(() => IdleTimeoutLoopAsync(_idleCts.Token));
    }

    /// <summary>
    /// 處理使用者訊息並以 streaming 方式回傳 AI 回覆片段。
    /// </summary>
    public async IAsyncEnumerable<StreamChunk> ProcessMessageAsync(
        long chatId,
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureStoreLoadedAsync();
        var chatSession = await GetOrCreateSessionAsync(chatId, ct);

        // 若本次 session 是 resume 失敗後重建，通知呼叫方（讓 UI 告知使用者）
        if (chatSession.IsResumeFailed)
        {
            chatSession.IsResumeFailed = false; // 重置，避免下次重複通知
            yield return new StreamChunk(StreamChunkType.SessionReset, "對話記憶已重置（前次 session 無法恢復）");
        }

        // Bot 重啟後第一次建立 session，通知使用者目前的專案/模型狀態
        if (chatSession.WelcomeBackMessage is { } welcomeMsg)
        {
            chatSession.WelcomeBackMessage = null; // 重置，避免下次重複通知
            yield return new StreamChunk(StreamChunkType.WelcomeBack, welcomeMsg);
        }

        // 防止同一 chat 並行呼叫（Copilot session 不支援並行 SendAsync）
        await chatSession.Lock.WaitAsync(ct);
        try
        {
            // 建立一個 Channel 來橋接事件回呼 → IAsyncEnumerable
            var channel = Channel.CreateUnbounded<StreamChunk>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sessionCorrupted = false;

            // 訂閱 session 事件
            using var subscription = chatSession.Session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        var text = delta.Data.DeltaContent;
                        if (!string.IsNullOrEmpty(text))
                        {
                            channel.Writer.TryWrite(new StreamChunk(StreamChunkType.Delta, text));
                        }
                        break;

                    case ToolExecutionStartEvent toolStart:
                        var toolInfo = $"[Tool: {toolStart.Data.ToolName}]";
                        channel.Writer.TryWrite(new StreamChunk(StreamChunkType.ToolStart, toolInfo));
                        break;

                    case ToolExecutionCompleteEvent toolComplete:
                        var status = toolComplete.Data.Success ? "done" : "failed";
                        channel.Writer.TryWrite(new StreamChunk(StreamChunkType.ToolEnd, $"[Tool {status}]"));
                        break;

                    case SessionErrorEvent error:
                        // 偵測 session 腐敗：tool_use/tool_result 斷裂
                        if (IsSessionCorruptedError(error.Data.Message))
                        {
                            sessionCorrupted = true;
                            _logger.LogWarning("Chat {ChatId}: 偵測到 session 異常（{Error}），將重建 session",
                                chatId, error.Data.Message);
                        }
                        else
                        {
                            channel.Writer.TryWrite(new StreamChunk(StreamChunkType.Error, error.Data.Message));
                        }
                        done.TrySetResult();
                        channel.Writer.TryComplete();
                        break;

                    case SessionIdleEvent:
                        done.TrySetResult();
                        channel.Writer.TryComplete();
                        break;
                }
            });

            // 發送訊息
            // SendAsync 可能直接 throw（例如 "Session not found"），需要在這裡捕捉
            _logger.LogInformation("Chat {ChatId}: 發送訊息至 Copilot，長度 {Length}", chatId, userMessage.Length);
            try
            {
                await chatSession.Session.SendAsync(new MessageOptions { Prompt = userMessage });
            }
            catch (Exception ex) when (IsSessionCorruptedError(ex.Message))
            {
                // SendAsync 直接丟出 session 失效例外（繞過 SessionErrorEvent）
                _logger.LogWarning(ex, "Chat {ChatId}: SendAsync 偵測到 session 失效，將重建並重試", chatId);
                sessionCorrupted = true;
                // 關閉 channel，避免 ReadAllAsync 卡住
                channel.Writer.TryComplete();
                done.TrySetResult();
            }

            // 從 Channel 讀取並 yield（真正的 streaming）
            await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
            {
                yield return chunk;
            }

            // 等待 session idle（通常 channel 已完成，這只是確保）
            await done.Task;

            // 若 session 腐敗（事件或 SendAsync 例外），銷毀並重建 session，再重試一次
            if (sessionCorrupted)
            {
                await DestroyChatSessionAsync(chatId);
                chatSession = await GetOrCreateSessionAsync(chatId, ct);

                // 重試：用新 session 重新發送
                await foreach (var chunk in RetryWithNewSessionAsync(chatSession, chatId, userMessage, ct))
                {
                    yield return chunk;
                }
            }

            // 更新 store 的最後活動時間 + session 內的 LastActiveAt
            chatSession.LastActiveAt = DateTimeOffset.UtcNow;
            await UpdateLastActiveAsync(chatId);

            _logger.LogInformation("Chat {ChatId}: AI 回覆完成", chatId);
        }
        finally
        {
            chatSession.Lock.Release();
        }
    }

    /// <summary>
    /// 使用新建的 session 重新發送訊息（retry 用）。
    /// </summary>
    private async IAsyncEnumerable<StreamChunk> RetryWithNewSessionAsync(
        ChatSession chatSession, long chatId, string userMessage,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<StreamChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = chatSession.Session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    var text = delta.Data.DeltaContent;
                    if (!string.IsNullOrEmpty(text))
                        channel.Writer.TryWrite(new StreamChunk(StreamChunkType.Delta, text));
                    break;

                case ToolExecutionStartEvent toolStart:
                    channel.Writer.TryWrite(new StreamChunk(StreamChunkType.ToolStart, $"[Tool: {toolStart.Data.ToolName}]"));
                    break;

                case ToolExecutionCompleteEvent toolComplete:
                    var status = toolComplete.Data.Success ? "done" : "failed";
                    channel.Writer.TryWrite(new StreamChunk(StreamChunkType.ToolEnd, $"[Tool {status}]"));
                    break;

                case SessionErrorEvent error:
                    channel.Writer.TryWrite(new StreamChunk(StreamChunkType.Error, error.Data.Message));
                    done.TrySetResult();
                    channel.Writer.TryComplete();
                    break;

                case SessionIdleEvent:
                    done.TrySetResult();
                    channel.Writer.TryComplete();
                    break;
            }
        });

        _logger.LogInformation("Chat {ChatId}: 使用新 session 重試，長度 {Length}", chatId, userMessage.Length);
        try
        {
            await chatSession.Session.SendAsync(new MessageOptions { Prompt = userMessage });
        }
        catch (Exception ex)
        {
            // retry 的新 session 也失敗，直接 emit Error chunk 讓使用者知道
            _logger.LogError(ex, "Chat {ChatId}: Retry SendAsync 亦失敗", chatId);
            channel.Writer.TryWrite(new StreamChunk(StreamChunkType.Error, ex.Message));
            channel.Writer.TryComplete();
            done.TrySetResult();
        }

        await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
        {
            yield return chunk;
        }

        await done.Task;
    }

    /// <summary>
    /// 判斷錯誤訊息是否為 session 腐敗（例如 tool_use/tool_result 配對斷裂）。
    /// 這種情況通常發生在 Bot 被強制關閉，導致正在執行工具的 session 狀態不一致。
    /// </summary>
    private static bool IsSessionCorruptedError(string errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return false;

        // Copilot CLI: session 過期或找不到
        if (errorMessage.Contains("Session not found", StringComparison.OrdinalIgnoreCase))
            return true;

        // Anthropic/Claude: tool_use ids were found without tool_result blocks
        if (errorMessage.Contains("tool_use", StringComparison.OrdinalIgnoreCase) &&
            errorMessage.Contains("tool_result", StringComparison.OrdinalIgnoreCase))
            return true;

        // OpenAI: tool calls / tool results 相關錯誤
        if (errorMessage.Contains("tool_call", StringComparison.OrdinalIgnoreCase) &&
            errorMessage.Contains("tool", StringComparison.OrdinalIgnoreCase) &&
            errorMessage.Contains("missing", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    // ─── 專案管理 ───────────────────────────────────────────────

    /// <summary>
    /// 取得所有已設定的專案。
    /// </summary>
    public IReadOnlyDictionary<string, ProjectConfig> GetAllProjects()
        => _config.Value.Projects;

    /// <summary>
    /// 取得指定 chat 的 active project（Key + Config）。
    /// 若未選擇，回傳第一個專案。若無專案設定，回傳 null。
    /// </summary>
    public async Task<(string Key, ProjectConfig Config)?> GetActiveProjectAsync(long chatId)
    {
        await EnsureStoreLoadedAsync();

        var projects = _config.Value.Projects;
        if (projects.Count == 0) return null;

        if (_activeProjects.TryGetValue(chatId, out var key) && projects.TryGetValue(key, out var config))
            return (key, config);

        // 預設：第一個專案
        var first = projects.First();
        return (first.Key, first.Value);
    }

    /// <summary>
    /// 切換 active project。會銷毀現有 session，下次對話以新專案 context 建立 session。
    /// </summary>
    /// <returns>切換後的專案 config，若專案名稱不存在則回傳 null。</returns>
    public async Task<ProjectConfig?> SwitchProjectAsync(long chatId, string projectKey)
    {
        var projects = _config.Value.Projects;

        // 嘗試不區分大小寫的 key 匹配
        var match = projects.Keys.FirstOrDefault(k =>
            string.Equals(k, projectKey, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            return null;

        _activeProjects[chatId] = match;

        // 銷毀現有 session（會先產生摘要記憶），讓下次對話建立新 session（帶新專案 context）
        await DestroyWithMemoryAsync(chatId, "SessionSwitch");

        // 更新 store（保留 sessionId 為空，下次 GetOrCreateSession 會建新的）
        await SaveStateAsync(chatId, sessionId: null);

        _logger.LogInformation("Chat {ChatId}: 切換專案至 '{ProjectKey}'", chatId, match);
        return projects[match];
    }

    // ─── 模型管理 ───────────────────────────────────────────────

    /// <summary>
    /// 取得指定 chat 目前使用的模型名稱。
    /// </summary>
    public string GetCurrentModel(long chatId)
    {
        if (_modelOverrides.TryGetValue(chatId, out var model))
            return model;
        return _config.Value.DefaultModel;
    }

    /// <summary>
    /// 切換模型。會銷毀現有 session，下次對話以新模型建立 session。
    /// </summary>
    public async Task SwitchModelAsync(long chatId, string model)
    {
        _modelOverrides[chatId] = model;
        await DestroyWithMemoryAsync(chatId, "SessionSwitch");

        // 更新 store（sessionId 清空，下次建新 session）
        await SaveStateAsync(chatId, sessionId: null);

        _logger.LogInformation("Chat {ChatId}: 切換模型至 '{Model}'", chatId, model);
    }

    /// <summary>
    /// 列出所有可用模型（含計費倍率與能力資訊）。
    /// </summary>
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync()
    {
        try
        {
            var models = await _copilotClient.ListModelsAsync();
            if (models is null) return [new ModelInfo(_config.Value.DefaultModel)];

            return models.Select(m => new ModelInfo(
                m.Id ?? "unknown",
                m.Billing?.Multiplier ?? 1.0,
                m.Capabilities?.Supports?.ReasoningEffort ?? false
            )).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "無法列出模型，使用預設值");
            return [new ModelInfo(_config.Value.DefaultModel)];
        }
    }

    // ─── Session 管理 ───────────────────────────────────────────

    /// <summary>
    /// 取得 /status 指令所需的 session 診斷資訊。
    /// </summary>
    public SessionStatusInfo GetSessionInfo(long chatId)
    {
        _sessions.TryGetValue(chatId, out var chatSession);
        var sessionId = chatSession?.Session.SessionId;
        var createdAt = chatSession?.CreatedAt;
        return new SessionStatusInfo(sessionId, createdAt, BotStartedAt);
    }

    /// <summary>
    /// 清除指定 chat 的 session + 重設專案/模型選擇。
    /// /clear 會先產生摘要記憶再銷毀，但不清除記憶庫（記憶跨 session 保留）。
    /// </summary>
    public async Task ClearSessionAsync(long chatId)
    {
        // 先產生摘要記憶再銷毀 session
        await DestroyWithMemoryAsync(chatId, "SessionClear");

        _activeProjects.TryRemove(chatId, out _);
        _modelOverrides.TryRemove(chatId, out _);

        // 從 store 刪除（完全清除持久化狀態，但記憶庫保留）
        await _sessionStore.DeleteAsync(chatId);
    }

    /// <summary>
    /// 清除指定 chat 的所有跨 Session 記憶。
    /// </summary>
    public async Task ClearMemoryAsync(long chatId)
    {
        await _memoryStore.ClearAsync(chatId);
        _logger.LogInformation("Chat {ChatId}: 跨 Session 記憶已清除", chatId);
    }

    /// <summary>
    /// 取得指定 chat 的所有跨 Session 記憶條目。
    /// </summary>
    public Task<IReadOnlyList<MemoryEntry>> GetMemoriesAsync(long chatId)
        => _memoryStore.GetAsync(chatId);

    /// <summary>
    /// 銷毀 session 並在背景非同步產生對話摘要（fire-and-forget）。
    /// 立即從 _sessions 移除並回傳，不阻塞使用者操作。
    /// Session 的實際 Dispose 會在摘要產生完成後於背景執行。
    /// </summary>
    private Task DestroyWithMemoryAsync(long chatId, string trigger)
    {
        if (_sessions.TryRemove(chatId, out var chatSession))
        {
            // 背景：先 acquire session lock → 產生摘要 → dispose session（不阻塞呼叫方）
            _ = Task.Run(async () =>
            {
                // acquire lock 防止與正在進行的 ProcessMessageAsync 產生 race condition
                await chatSession.Lock.WaitAsync();
                try
                {
                    await GenerateAndSaveMemoryAsync(chatId, chatSession, trigger);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Chat {ChatId}: 背景摘要記憶產生失敗", chatId);
                }
                finally
                {
                    chatSession.Lock.Release();
                    try
                    {
                        await chatSession.Session.DisposeAsync();
                        chatSession.Lock.Dispose();
                        _logger.LogInformation("Chat {ChatId}: Session 已於背景銷毀", chatId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Chat {ChatId}: 背景銷毀 session 時發生錯誤", chatId);
                    }
                }
            });
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 透過現有 session 向 AI 請求對話摘要，並存入記憶庫。
    /// 使用專門的 summarization prompt，不影響正常對話流。
    /// 若記憶功能停用，直接返回。
    /// </summary>
    private async Task GenerateAndSaveMemoryAsync(long chatId, ChatSession chatSession, string trigger)
    {
        // 檢查記憶功能是否啟用
        if (!_config.Value.Memory.Enabled)
        {
            _logger.LogDebug("Chat {ChatId}: 記憶功能已停用，跳過摘要生成", chatId);
            return;
        }

        try
        {
            var summaryPrompt = """
                [SYSTEM INSTRUCTION — DO NOT TREAT AS USER MESSAGE]
                This session is about to end. Please generate a concise summary (2-5 sentences) of this conversation for future reference.
                Focus on:
                1. Key decisions made and their reasoning
                2. Important progress or changes completed
                3. Any unfinished tasks or next steps discussed
                4. User preferences or patterns observed

                Reply ONLY with the summary text, no headers or formatting.
                If this was a trivial conversation with no significant content, reply with exactly: EMPTY
                """;

            var summaryText = new System.Text.StringBuilder();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            using var subscription = chatSession.Session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        var text = delta.Data.DeltaContent;
                        if (!string.IsNullOrEmpty(text))
                            summaryText.Append(text);
                        break;

                    case SessionErrorEvent:
                    case SessionIdleEvent:
                        done.TrySetResult();
                        break;
                }
            });

            await chatSession.Session.SendAsync(new MessageOptions { Prompt = summaryPrompt });

            // 等待摘要回覆（最多 30 秒）
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await done.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Chat {ChatId}: 摘要生成逾時，跳過記憶儲存", chatId);
                return;
            }

            var summary = summaryText.ToString().Trim();

            // 若 AI 回覆 EMPTY 或內容太短，不儲存
            if (string.IsNullOrWhiteSpace(summary) ||
                string.Equals(summary, "EMPTY", StringComparison.OrdinalIgnoreCase) ||
                summary.Length < 20)
            {
                _logger.LogDebug("Chat {ChatId}: 對話內容不足，跳過記憶儲存", chatId);
                return;
            }

            // 取得當前專案與模型資訊
            _activeProjects.TryGetValue(chatId, out var projectKey);
            var model = GetCurrentModel(chatId);

            var entry = new MemoryEntry
            {
                CreatedAt = DateTimeOffset.UtcNow,
                Trigger = trigger,
                ProjectKey = projectKey,
                Model = model,
                Summary = summary
            };

            await _memoryStore.AddAsync(chatId, entry);
            _logger.LogInformation("Chat {ChatId}: 已產生跨 Session 記憶（{Trigger}，{Length} 字元）",
                chatId, trigger, summary.Length);
        }
        catch (Exception ex)
        {
            // 摘要失敗不應阻止 session 銷毀
            _logger.LogWarning(ex, "Chat {ChatId}: 產生摘要記憶失敗，繼續銷毀 session", chatId);
        }
    }

    private async Task DestroyChatSessionAsync(long chatId)
    {
        if (_sessions.TryRemove(chatId, out var chatSession))
        {
            // DisposeAsync 會保留 session 資料在磁碟（Copilot SDK 行為）
            await chatSession.Session.DisposeAsync();
            chatSession.Lock.Dispose();
            _logger.LogInformation("Chat {ChatId}: Session 已銷毀", chatId);
        }
    }

    private async Task<ChatSession> GetOrCreateSessionAsync(long chatId, CancellationToken ct)
    {
        if (_sessions.TryGetValue(chatId, out var existing))
            return existing;

        var activeProject = await GetActiveProjectAsync(chatId);
        var model = GetCurrentModel(chatId);
        var workingDirectory = activeProject?.Config.Path;

        // 載入跨 Session 記憶（用於注入 SystemPrompt）
        var memories = await _memoryStore.GetAsync(chatId);
        var swapDir = _config.Value.SwapDirectory;
        var systemPrompt = SystemPrompts.Build(activeProject?.Config, memories, activeProject?.Key, swapDir);

        // 嘗試從 store 恢復 session
        var storedState = await _sessionStore.GetAsync(chatId);
        CopilotSession? session = null;

        if (!string.IsNullOrEmpty(storedState?.SessionId))
        {
            try
            {
                _logger.LogInformation(
                    "Chat {ChatId}: 嘗試恢復 Session {SessionId}",
                    chatId, storedState.SessionId);

                session = await _copilotClient.ResumeSessionAsync(
                    storedState.SessionId,
                    new ResumeSessionConfig
                    {
                        Model = model,
                        Streaming = true,
                        WorkingDirectory = workingDirectory,
                        SystemMessage = new SystemMessageConfig
                        {
                            Mode = SystemMessageMode.Append,
                            Content = systemPrompt
                        },
                        McpServers = BuildMcpServers(),
                        OnPermissionRequest = (req, inv) => HandlePermissionRequestAsync(chatId, req, inv)
                    });

                _logger.LogInformation(
                    "Chat {ChatId}: 成功恢復 Session {SessionId}",
                    chatId, session.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Chat {ChatId}: 恢復 Session {SessionId} 失敗，將建立新 session",
                    chatId, storedState.SessionId);
                session = null;
            }
        }

        // 恢復失敗或無儲存狀態 → 建立新 session
        var isResumeFailed = false;
        if (session is null)
        {
            isResumeFailed = !string.IsNullOrEmpty(storedState?.SessionId); // 有 sessionId 但 resume 失敗
            session = await _copilotClient.CreateSessionAsync(new SessionConfig
            {
                Model = model,
                Streaming = true,
                WorkingDirectory = workingDirectory,
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = systemPrompt
                },
                McpServers = BuildMcpServers(),
                OnPermissionRequest = (req, inv) => HandlePermissionRequestAsync(chatId, req, inv)
            });

            var memoryCount = memories.Count;
            _logger.LogInformation(
                "Chat {ChatId}: 建立新 Copilot Session {SessionId}, Model={Model}, Project={Project}, CWD={CWD}, Memories={MemoryCount}",
                chatId, session.SessionId, model, activeProject?.Key ?? "(none)", workingDirectory ?? "(none)", memoryCount);
        }

        // 儲存 session ID 到 store
        await SaveStateAsync(chatId, session.SessionId);

        // 組裝重啟歡迎提醒：只有本次程序生命週期內第一次為此 chatId 建立 session 時才通知。
        // 後續因切模型、idle timeout、截圖後 session 重建等情況，不再重複通知。
        string? welcomeBack = null;
        if (_seenChatIds.Add(chatId))
        {
            welcomeBack = await BuildWelcomeBackMessageAsync(chatId, model, activeProject);
        }

        var chatSession = new ChatSession(session, isResumeFailed, welcomeBack);
        _sessions[chatId] = chatSession;

        return chatSession;
    }

    // ─── 歡迎提醒 ───────────────────────────────────────────────

    /// <summary>
    /// 組裝 Bot 重啟後第一次建立 session 的歡迎提醒訊息。
    /// 格式範例：「ℹ️ Bot 已重啟。目前狀態：專案 myproject | 模型 gpt-4o [1x]」
    /// </summary>
    private async Task<string> BuildWelcomeBackMessageAsync(long chatId, string model, (string Key, ProjectConfig Config)? activeProject)
    {
        // 查詢倍率（best-effort，失敗就省略）
        var rateTag = string.Empty;
        try
        {
            var models = await ListModelsAsync();
            var info = models.FirstOrDefault(m =>
                string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
            if (info is not null)
            {
                var multiplier = info.Multiplier == 1.0 ? "1x" : $"{info.Multiplier:0.##}x";
                rateTag = info.SupportsReasoning
                    ? $" [reasoning, {multiplier}]"
                    : $" [{multiplier}]";
            }
        }
        catch { /* 查詢失敗不影響主流程 */ }

        var projectPart = activeProject is not null
            ? $"專案 {activeProject.Value.Key}"
            : "無專案";

        return $"ℹ️ Bot 已重啟。目前狀態：{projectPart} | 模型 {model}{rateTag}";
    }

    // ─── MCP 輔助方法 ───────────────────────────────────────────

    /// <summary>
    /// 將 CopilotClawDConfig.McpServers 轉換為 SDK 接受的 Dictionary&lt;string, object&gt;。
    /// local → McpLocalServerConfig；http/sse → McpRemoteServerConfig。
    /// 若無 MCP 設定，回傳 null（SDK 不啟動任何 MCP server）。
    /// </summary>
    private Dictionary<string, object>? BuildMcpServers()
    {
        var cfg = _config.Value.McpServers;
        if (cfg is null || cfg.Count == 0) return null;

        var result = new Dictionary<string, object>();
        foreach (var (name, server) in cfg)
        {
            object sdkConfig = server.Type is "http" or "sse"
                ? new McpRemoteServerConfig
                {
                    Type = server.Type,
                    Url = server.Url ?? string.Empty,
                    Headers = server.Headers,
                    Timeout = server.Timeout,
                    Tools = server.Tools
                }
                : new McpLocalServerConfig
                {
                    Command = server.Command ?? string.Empty,
                    Args = server.Args,
                    Env = server.Env,
                    Cwd = server.Cwd,
                    Timeout = server.Timeout,
                    Tools = server.Tools
                };

            result[name] = sdkConfig;
        }

        _logger.LogDebug("MCP servers 已設定：{Names}", string.Join(", ", result.Keys));
        return result;
    }

    /// <summary>
    /// 取得目前 session 已連接的 MCP server 清單（用於 /mcp 指令）。
    /// 若 chatId 無 active session，回傳 null。
    /// </summary>
    public async Task<IReadOnlyList<GitHub.Copilot.SDK.Rpc.Server>?> GetMcpServersAsync(long chatId)
    {
        if (!_sessions.TryGetValue(chatId, out var chatSession))
            return null;

        try
        {
            var result = await chatSession.Session.Rpc.Mcp.ListAsync();
            return result?.Servers?.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: 無法取得 MCP server 清單", chatId);
            return null;
        }
    }

    // ─── Store 輔助方法 ─────────────────────────────────────────

    /// <summary>
    /// 從 store 載入所有 chat 的 activeProject 和 modelOverride 到記憶體。
    /// 只執行一次。
    /// </summary>
    private async Task EnsureStoreLoadedAsync()
    {
        if (_storeLoaded) return;

        await _loadLock.WaitAsync();
        try
        {
            if (_storeLoaded) return;

            var all = await _sessionStore.GetAllAsync();
            foreach (var (chatId, state) in all)
            {
                if (!string.IsNullOrEmpty(state.ActiveProjectKey))
                    _activeProjects.TryAdd(chatId, state.ActiveProjectKey);

                if (!string.IsNullOrEmpty(state.ModelOverride))
                    _modelOverrides.TryAdd(chatId, state.ModelOverride);
            }

            _logger.LogInformation("從 SessionStore 載入 {Count} 筆 chat 設定", all.Count);
            _storeLoaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// 儲存目前 chat 的完整狀態到 store。
    /// </summary>
    private async Task SaveStateAsync(long chatId, string? sessionId)
    {
        _activeProjects.TryGetValue(chatId, out var projectKey);
        _modelOverrides.TryGetValue(chatId, out var modelOverride);

        // 如果 sessionId 為 null，嘗試保留現有的 sessionId（例如切換專案時暫時沒有 session）
        var existingState = await _sessionStore.GetAsync(chatId);

        var state = new ChatSessionState
        {
            SessionId = sessionId ?? string.Empty,
            ActiveProjectKey = projectKey,
            ModelOverride = modelOverride,
            CreatedAt = existingState?.CreatedAt ?? DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow
        };

        await _sessionStore.SaveAsync(chatId, state);
    }

    /// <summary>
    /// 更新 store 中的最後活動時間。
    /// </summary>
    private async Task UpdateLastActiveAsync(long chatId)
    {
        var state = await _sessionStore.GetAsync(chatId);
        if (state is not null)
        {
            state.LastActiveAt = DateTimeOffset.UtcNow;
            await _sessionStore.SaveAsync(chatId, state);
        }
    }

    /// <summary>
    /// 權限處理（Phase 7）：依據 PermissionPolicy 評估結果決定允許/拒絕/詢問使用者。
    /// chatId 透過 GetOrCreateSessionAsync 中的 closure 注入。
    /// </summary>
    private async Task<PermissionRequestResult> HandlePermissionRequestAsync(
        long chatId, PermissionRequest request, PermissionInvocation invocation)
    {
        // 1. 取得操作描述 + 進行權限評估
        var (toolInfo, evaluation) = request switch
        {
            PermissionRequestShell shell => (
                $"shell: {shell.FullCommandText}",
                _permissionPolicy.EvaluateShellCommand(shell.FullCommandText)),

            PermissionRequestWrite write => (
                $"write: {write.FileName}",
                _permissionPolicy.EvaluateWrite(write.FileName)),

            PermissionRequestRead read => (
                $"read: {read.Path}",
                _permissionPolicy.EvaluateRead(read.Path)),

            PermissionRequestMcp mcp => (
                $"mcp: {mcp.ToolName}",
                _permissionPolicy.EvaluateMcp(mcp.ToolName, mcp.ServerName)),

            PermissionRequestUrl url => (
                $"url: {url.Url}",
                _permissionPolicy.EvaluateUrl(url.Url)),

            PermissionRequestCustomTool ct => (
                $"custom: {ct.ToolName}",
                new PermissionEvaluation(PermissionLevel.Normal, "自訂工具", ct.ToolName)),

            _ => (
                request.Kind,
                new PermissionEvaluation(PermissionLevel.Normal, "未知類型", request.Kind))
        };

        _logger.LogInformation("Permission request: Kind={Kind}, Detail={Detail}, Level={Level}",
            request.Kind, toolInfo, evaluation.Level);

        // 2. 依據權限等級處理
        return evaluation.Level switch
        {
            PermissionLevel.Safe => new PermissionRequestResult
            {
                Kind = PermissionRequestResultKind.Approved
            },

            PermissionLevel.Normal => new PermissionRequestResult
            {
                Kind = PermissionRequestResultKind.Approved
            },

            PermissionLevel.Dangerous => await HandleDangerousAsync(chatId, toolInfo, evaluation),

            PermissionLevel.Forbidden => HandleForbidden(toolInfo, evaluation),

            _ => new PermissionRequestResult
            {
                Kind = PermissionRequestResultKind.Approved
            }
        };
    }

    /// <summary>
    /// 處理危險操作：透過 IPermissionConfirmer 詢問使用者，無確認器時預設拒絕。
    /// </summary>
    private async Task<PermissionRequestResult> HandleDangerousAsync(
        long chatId, string toolInfo, PermissionEvaluation evaluation)
    {
        if (_permissionConfirmer is null)
        {
            _logger.LogWarning("危險操作被拒絕（無確認器）: {Detail}", toolInfo);
            return new PermissionRequestResult
            {
                Kind = PermissionRequestResultKind.DeniedCouldNotRequestFromUser
            };
        }

        var timeout = TimeSpan.FromSeconds(_config.Value.Security.ConfirmationTimeoutSeconds);
        var description = $"⚠️ 危險操作\n\n{toolInfo}\n\n原因: {evaluation.Reason}";

        _logger.LogInformation("Chat {ChatId}: 等待使用者確認危險操作: {Detail}", chatId, toolInfo);

        var approved = await _permissionConfirmer.ConfirmAsync(chatId, description, timeout);

        _logger.LogInformation("Chat {ChatId}: 使用者{Result}危險操作: {Detail}",
            chatId, approved ? "允許" : "拒絕", toolInfo);

        return new PermissionRequestResult
        {
            Kind = approved ? PermissionRequestResultKind.Approved : PermissionRequestResultKind.DeniedInteractivelyByUser
        };
    }

    /// <summary>
    /// 處理禁止操作：直接拒絕。
    /// </summary>
    private PermissionRequestResult HandleForbidden(string toolInfo, PermissionEvaluation evaluation)
    {
        _logger.LogWarning("禁止操作被攔截: {Detail} — {Reason}", toolInfo, evaluation.Reason);
        return new PermissionRequestResult
        {
            Kind = PermissionRequestResultKind.DeniedByRules
        };
    }

    /// <summary>
    /// 背景迴圈：每 30 分鐘掃描所有 session，若超過 idle timeout 就自動銷毀並產生摘要。
    /// </summary>
    private async Task IdleTimeoutLoopAsync(CancellationToken ct)
    {
        var checkInterval = TimeSpan.FromMinutes(30);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(checkInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var timeoutHours = _config.Value.Memory.SessionIdleTimeoutHours;
            if (timeoutHours <= 0) continue;

            var timeout = TimeSpan.FromHours(timeoutHours);
            var now = DateTimeOffset.UtcNow;

            foreach (var (chatId, chatSession) in _sessions)
            {
                if (now - chatSession.LastActiveAt >= timeout)
                {
                    _logger.LogInformation(
                        "Chat {ChatId}: Session idle timeout（超過 {Hours} 小時），自動產摘要並銷毀",
                        chatId, timeoutHours);
                    await DestroyWithMemoryAsync(chatId, "IdleTimeout");
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 停止 idle timeout 背景迴圈
        await _idleCts.CancelAsync();
        _idleCts.Dispose();

        foreach (var (chatId, chatSession) in _sessions)
        {
            try
            {
                // 正常關閉時也嘗試產生摘要記憶
                await GenerateAndSaveMemoryAsync(chatId, chatSession, "GracefulShutdown");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Chat {ChatId}: 關閉時產生摘要記憶失敗", chatId);
            }

            try
            {
                // DisposeAsync 保留 session 在磁碟，方便下次恢復
                await chatSession.Session.DisposeAsync();
                chatSession.Lock.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "銷毀 Chat {ChatId} session 時發生錯誤", chatId);
            }
        }
        _sessions.Clear();
    }

    /// <summary>
    /// 內部類別：包裝 CopilotSession + SemaphoreSlim（防止並行呼叫）。
    /// IsResumedFailed 為 true 表示本次是 resume 失敗後重建的新 session，
    /// 可用於通知使用者對話記憶已重置。
    /// </summary>
    private sealed class ChatSession(CopilotSession session, bool isResumeFailed = false, string? welcomeBackMessage = null)
    {
        public CopilotSession Session { get; } = session;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// 最後一次收到使用者訊息的時間（用於 idle timeout 判斷）。
        /// </summary>
        public DateTimeOffset LastActiveAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// 本次 session 是否為 resume 失敗後重建。使用後應重置為 false，避免重複通知。
        /// </summary>
        public bool IsResumeFailed { get; set; } = isResumeFailed;

        /// <summary>
        /// Bot 重啟後第一次建立 session 時的歡迎提醒訊息（專案/模型狀態）。
        /// 使用後應清為 null，避免重複通知。
        /// </summary>
        public string? WelcomeBackMessage { get; set; } = welcomeBackMessage;
    }
}

/// <summary>
/// Streaming 回覆的片段類型。
/// </summary>
public enum StreamChunkType
{
    Delta,        // AI 回覆文字片段
    ToolStart,    // 工具開始執行
    ToolEnd,      // 工具執行完成
    Error,        // 錯誤
    SessionReset, // Session resume 失敗，已重建新 session
    SendFile,     // AI 要求傳送本機檔案給使用者（Content = 絕對路徑）
    WelcomeBack   // Bot 重啟後第一次收到訊息，通知目前的專案/模型狀態（Content = 提醒訊息）
}

/// <summary>
/// Streaming 回覆的單一片段。
/// </summary>
public record StreamChunk(StreamChunkType Type, string Content);

/// <summary>
/// /status 指令所需的 session 診斷資訊。
/// </summary>
public record SessionStatusInfo(
    string? SessionId,
    DateTimeOffset? CreatedAt,
    DateTimeOffset BotStartedAt);

/// <summary>
/// 可用模型資訊（含計費倍率與能力）。
/// </summary>
/// <param name="Id">模型識別碼（例如 "gpt-4o"）。</param>
/// <param name="Multiplier">計費倍率（相對於基準費率）。1.0 = 1x premium request。</param>
/// <param name="SupportsReasoning">是否支援 reasoning effort 設定。</param>
public record ModelInfo(
    string Id,
    double Multiplier = 1.0,
    bool SupportsReasoning = false);
