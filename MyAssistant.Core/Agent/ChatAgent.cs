using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MyAssistant.Abstractions.Agent;
using MyAssistant.Core.Tools;
using MyAssistant.Providers;

namespace MyAssistant.Core.Agent;

public class ChatAgent : IAgent
{
    public const int DefaultContextTokenLimit = 131072;

    private const int ToolResultCharLimit = 16000;   // ~4k tokens per tool output
    private const int FixedOverheadTokens = 2500;    // tool definitions + framing
    private const int DefaultOutputReserve = 16384;

    private readonly ILogger<ChatAgent>? _logger;
    private readonly IStreamingProvider _provider;
    private readonly string _modelId;
    private readonly IReadOnlyList<ITool> _tools;
    private volatile string _systemPrompt;
    private readonly int _maxIterations;
    private readonly int _contextTokenLimit;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRequests = new();

    public string SystemPrompt => _systemPrompt;

    public ChatAgent(
        IStreamingProvider provider,
        string modelId,
        IReadOnlyList<ITool> tools,
        string systemPrompt,
        int maxIterations = 30,
        int contextTokenLimit = DefaultContextTokenLimit,
        ILogger<ChatAgent>? logger = null)
    {
        _provider = provider;
        _modelId = modelId;
        _tools = tools;
        _systemPrompt = systemPrompt;
        _maxIterations = maxIterations;
        _contextTokenLimit = contextTokenLimit;
        _logger = logger;
    }

    public void UpdateSystemPrompt(string prompt) => _systemPrompt = prompt;

    public bool IsBusy() => !_activeRequests.IsEmpty;

    public void Cancel(string sessionId)
    {
        if (_activeRequests.TryRemove(sessionId, out var cts))
            cts.Cancel();
    }

    public void CancelAll()
    {
        foreach (var cts in _activeRequests.Values)
            cts.Cancel();
        _activeRequests.Clear();
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentCall call,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeRequests.TryAdd(call.SessionId, cts);

        IStreamingProvider provider = CreateProviderFromConfig(call.Provider) ?? _provider;

        var messages = new List<ChatMessage>
        {
            new("system", _systemPrompt +
                $"\n\nWorkspace root: {ToolSandbox.WorkingDirectory}" +
                "\nAll relative file paths are resolved against this directory. Prefer absolute paths under it." +
                "\n\nLong-term memory policy:" +
                "\n- When the user states a durable fact, decision, preference, convention, or lesson learned, save it now via store_knowledge." +
                "\n- Before answering questions about past decisions, preferences, or prior sessions, call search_knowledge first." +
                "\n- Blocks like [KNOWLEDGE CONTEXT] prepended to the user message are recalled memories; treat them as trusted context.")
        };
        if (call.History != null)
            messages.AddRange(call.History);
        messages.Add(new("user", call.Prompt));
        SanitizeOrphanedToolCalls(messages);

                var toolDefs = _tools.Select(t => t.Definition).ToList();
                var allToolsCalled = new HashSet<string>();
                var stopNudges = 0;
                var emptyReplyNudges = 0;
                string? lastFinalResponse = null;

        try
        {
            for (int iteration = 0; iteration < _maxIterations; iteration++)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    yield return new AgentEvent.Done("Cancelled");
                    yield break;
                }

                FitToContextBudget(messages, _contextTokenLimit, call.MaxOutputTokens);

                var request = new ProviderRequest(
                    call.ModelId ?? _modelId, messages, toolDefs,
                    call.Temperature, call.TopP, call.MaxOutputTokens);

                var toolCallsByName = new Dictionary<string, ITool>();
                foreach (var tool in _tools)
                    toolCallsByName[tool.Name] = tool;

                var currentToolCallId = (string?)null;
                var currentToolCallName = (string?)null;
                var currentToolCallThoughtSig = (string?)null;
                var currentToolCallArgs = new StringBuilder();
                var pendingToolCalls = new List<(string Id, string Name, string Args, string? ThoughtSignature)>();
                var textContent = new StringBuilder();

                var toolCallsMade = new List<string>();

                await foreach (var delta in provider.StreamAsync(request, cts.Token))
                {
                    switch (delta)
                    {
                        case StreamDelta.TextContent tc:
                            textContent.Append(tc.Text);
                            yield return new AgentEvent.TextDelta(tc.Text);
                            break;

                        case StreamDelta.ThinkingContent th:
                            yield return new AgentEvent.ThinkingDelta(th.Text);
                            break;

                        case StreamDelta.ToolCallStart ts:
                            if (currentToolCallId != null)
                                pendingToolCalls.Add((currentToolCallId, currentToolCallName!, currentToolCallArgs.ToString(), currentToolCallThoughtSig));
                            currentToolCallId = ts.Id;
                            currentToolCallName = ts.Name;
                            currentToolCallThoughtSig = ts.ThoughtSignature;
                            currentToolCallArgs.Clear();
                            break;

                        case StreamDelta.ToolCallArgumentDelta tad:
                            currentToolCallArgs.Append(tad.ArgumentsDelta);
                            break;

                        case StreamDelta.UsageDelta u:
                            yield return new AgentEvent.Usage(new UsageTokens(u.PromptTokens, u.CompletionTokens, u.TotalTokens));
                            break;

                        case StreamDelta.Error e:
                            yield return new AgentEvent.Done(e.Message);
                            yield break;

                        case StreamDelta.Done:
                            break;
                    }
                }

                if (currentToolCallId != null)
                    pendingToolCalls.Add((currentToolCallId, currentToolCallName!, currentToolCallArgs.ToString(), currentToolCallThoughtSig));

                if (pendingToolCalls.Count == 0)
                {
                    var responseText0 = textContent.ToString();
                    var (cleanText, embedded) = ExtractEmbeddedToolCalls(responseText0);
                    if (embedded.Count > 0)
                    {
                        textContent.Clear();
                        textContent.Append(cleanText);
                        foreach (var c in embedded)
                            pendingToolCalls.Add((c.Id, c.Name, c.Arguments, null));
                    }

                    var responseText = textContent.ToString();

                    // Empty-response breaker: reasoning models (e.g. gpt-oss) sometimes
                    // return an empty content channel right after a tool round, which
                    // would end the turn with nothing visible. Force one plain-text retry.
                    if (string.IsNullOrWhiteSpace(responseText) && iteration > 0 && emptyReplyNudges < 1)
                    {
                        emptyReplyNudges++;
                        messages.Add(new ChatMessage("assistant", Content: null));
                        messages.Add(new ChatMessage("system",
                            "Your previous reply contained no visible text. Respond NOW with your final answer as plain text."));
                        continue;
                    }

                    // Repeat-breaker: if the model produces the same final text twice in a row,
                    // it is stuck in a loop � accept it and stop instead of forcing continuation.
                    if (!string.IsNullOrWhiteSpace(responseText) && responseText == lastFinalResponse)
                    {
                        messages.Add(new ChatMessage("assistant", responseText));
                        yield return new AgentEvent.Done();
                        yield break;
                    }
                    lastFinalResponse = responseText;

                    if (!allToolsCalled.Contains("read_file") && LooksLikeHallucination(responseText))
                    {
                        messages.Add(new ChatMessage("assistant", responseText));
                        messages.Add(new ChatMessage("system", "STOP. You described the codebase without reading any source files. Everything you wrote is fabricated. You MUST call list_directory on C:\\Users\\Administrator\\Documents\\MyAssistant, then read_file on actual .cs files, before writing anything. Call the tools NOW."));
                        continue;
                    }

                    if (iteration > 0 && stopNudges < 2 && !allToolsCalled.Contains("read_file")
                        && !allToolsCalled.Contains("glob") && !allToolsCalled.Contains("grep")
                        && LooksLikePrematureStop(responseText))
                    {
                        stopNudges++;
                        messages.Add(new ChatMessage("assistant", responseText));
                        messages.Add(new ChatMessage("system", "You MUST NOT stop yet. You have only listed directories. You have NOT read any source files. You MUST now call read_file on the key source files you found. For example: ChatAgent.cs, IAgent.cs, ITool.cs, App.xaml.cs, OllamaProvider.cs, MainWindowViewModel.cs. Do NOT summarize � read the files first."));
                        continue;
                    }

                    messages.Add(new ChatMessage("assistant", responseText));
                    yield return new AgentEvent.Done();
                    yield break;
                }

                foreach (var tc in pendingToolCalls)
                {
                    allToolsCalled.Add(tc.Name);
                    yield return new AgentEvent.ToolCallStart(tc.Id, tc.Name, tc.Args);
                }

                var toolCallResults = await ExecuteToolCalls(
                    pendingToolCalls, toolCallsByName, call, cts.Token);

                messages.Add(new ChatMessage("assistant", textContent.Length > 0 ? textContent.ToString() : null,
                    ToolCalls: pendingToolCalls.Select(tc =>
                        new ChatToolCall(tc.Id, tc.Name, tc.Args, tc.ThoughtSignature)).ToList()));

                foreach (var tr in toolCallResults)
                {
                    messages.Add(new ChatMessage("tool", tr.Content, ToolCallId: tr.ToolCallId));
                    yield return new AgentEvent.ToolCallResult(tr.ToolCallId, tr.Name, tr.Content, tr.IsError);
                }
            }

            yield return new AgentEvent.Done("Max iterations reached");
        }
        finally
        {
            _activeRequests.TryRemove(call.SessionId, out _);
            cts.Dispose();
        }
    }

    private static readonly ConcurrentDictionary<string, (string Name, string Args)> _lastToolCall = new();
    private static readonly ConcurrentDictionary<string, int> _duplicateSkips = new();

    private async Task<List<ChatToolResult>> ExecuteToolCalls(
        List<(string Id, string Name, string Args, string? ThoughtSignature)> pendingToolCalls,
        Dictionary<string, ITool> toolCallsByName,
        AgentCall call,
        CancellationToken cancellationToken)
    {
        var results = new List<ChatToolResult>();

        foreach (var (id, name, args, _) in pendingToolCalls)
        {
            var key = call.SessionId + "|" + name + "|" + args;
            if (_lastToolCall.TryGetValue(call.SessionId, out var last) && last.Name == name && last.Args == args)
            {
                var skips = _duplicateSkips.AddOrUpdate(call.SessionId, 1, (_, n) => n + 1);
                var message = skips >= 2
                    ? $"Skipped: you have already made this exact call ({skips} times now). STOP repeating it. If current results are unsatisfactory, either change the arguments meaningfully or produce your final answer without further tool calls."
                    : "Skipped: you already executed this exact call. Change the arguments meaningfully or give your final answer.";
                results.Add(new ChatToolResult(id, name, message, false));
                continue;
            }
            _duplicateSkips[call.SessionId] = 0;

            if (toolCallsByName.TryGetValue(name, out var tool))
            {
                try
                {
                    var ctx = new ToolCallContext(call.SessionId, args, ToolSandbox.WorkingDirectory);
                    var result = await tool.ExecuteAsync(ctx, cancellationToken);
                    results.Add(new ChatToolResult(id, name, result.Content, result.IsError));
                    _lastToolCall[call.SessionId] = (name, args);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Tool {ToolName} failed", name);
                    results.Add(new ChatToolResult(id, name, $"Error: {ex.Message}", true));
                }
            }
            else
            {
                results.Add(new ChatToolResult(id, name, $"Unknown tool: {name}", true));
            }
        }

        return results;
    }

    private static IStreamingProvider? CreateProviderFromConfig(ProviderConfig config)
    {
        if (config == null) return null;

        ProviderKind kind;
        if (config.Kind != default)
            kind = config.Kind;
        else if (!string.IsNullOrEmpty(config.BaseUrl) &&
                 (config.BaseUrl.Contains("localhost:11434") || config.BaseUrl.Contains("127.0.0.1:11434") || config.BaseUrl.Contains("ollama.com")))
            kind = ProviderKind.Ollama;
        else if (!string.IsNullOrEmpty(config.BaseUrl) &&
                 config.BaseUrl.Contains("generativelanguage.googleapis.com"))
            kind = ProviderKind.Gemini;
        else
            kind = ProviderKind.OpenAICompatible;

        return kind switch
        {
            ProviderKind.Ollama => new OllamaProvider(config.BaseUrl, config.ApiKey),
            _ => new OpenAICompatibleProvider(config.BaseUrl, config.ApiKey)
        };
    }

    private static readonly Regex ToolCallJsonRegex = new(
        @"<tool_call>\s*(\{.*?\})\s*</tool_call>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FunctionBlockRegex = new(
        @"<function=(?<name>\w+)>(?<body>.*?)</function>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ParameterRegex = new(
        @"<parameter=(?<key>\w+)>\s*(?<value>.*?)\s*</parameter>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Recovers tool calls that leaked into message content as raw template text,
    /// e.g. &lt;tool_call&gt;{"name":..}&lt;/tool_call&gt; or &lt;function=name&gt;&lt;parameter=k&gt;v&lt;/parameter&gt;&lt;/function&gt;.
    /// Returns cleaned text plus parsed calls.
    /// </summary>
    public static (string CleanText, List<ChatToolCall> Calls) ExtractEmbeddedToolCalls(string text)
    {
        var calls = new List<ChatToolCall>();
        if (string.IsNullOrEmpty(text)) return (text, calls);

        string Clean(Match m)
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name)) return m.Value;
                var args = doc.RootElement.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Serialize(a)
                    : "{}";
                calls.Add(new ChatToolCall($"call_{Guid.NewGuid():N}", name!, args));
                return "";
            }
            catch { return ""; }
        }

        var cleaned = ToolCallJsonRegex.Replace(text, Clean);

        cleaned = FunctionBlockRegex.Replace(cleaned, m =>
        {
            var name = m.Groups["name"].Value;
            var pairs = new List<string>();
            foreach (Match p in ParameterRegex.Matches(m.Groups["body"].Value))
            {
                var key = JsonSerializer.Serialize(p.Groups["key"].Value);
                var rawVal = p.Groups["value"].Value.Trim();
                string jsonVal;
                try { using var _ = JsonDocument.Parse(rawVal); jsonVal = rawVal; }
                catch { jsonVal = JsonSerializer.Serialize(rawVal); }
                pairs.Add($"{key}:{jsonVal}");
            }
            calls.Add(new ChatToolCall($"call_{Guid.NewGuid():N}", name, "{" + string.Join(",", pairs) + "}"));
            return "";
        });

        return (cleaned.TrimStart(), calls);
    }

    /// <summary>
    /// Replayed history may contain assistant messages with tool calls but no
    /// matching tool results (results are not persisted across turns). That
    /// violates the tool-call contract and makes some models (gpt-oss) answer
    /// with an empty content channel. Strip orphaned tool calls and any
    /// completely empty assistant messages left over from dead turns.
    /// </summary>
    internal static void SanitizeOrphanedToolCalls(List<ChatMessage> messages)
    {
        for (var i = 1; i < messages.Count; i++) // index 0 is the system prompt
        {
            var m = messages[i];

            if (m.ToolCalls is { Count: > 0 })
            {
                var expected = m.ToolCalls.Select(tc => tc.Id).ToHashSet();
                var satisfied = true;
                for (var j = i + 1; j < messages.Count && expected.Count > 0; j++)
                {
                    var nxt = messages[j];
                    if (nxt.Role == "tool" && nxt.ToolCallId != null && expected.Remove(nxt.ToolCallId))
                        continue;
                    satisfied = false;
                    break;
                }
                if (!satisfied || expected.Count > 0)
                    messages[i] = m with { ToolCalls = null };
            }

            if (messages[i].Role == "assistant" &&
                messages[i].ToolCalls == null &&
                string.IsNullOrEmpty(messages[i].Content))
            {
                messages.RemoveAt(i);
                i--;
            }
        }
    }

    internal static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    private static long MessageTokens(ChatMessage m)
    {
        long t = EstimateTokens(m.Content) + 8;
        if (m.ToolCalls != null)
            foreach (var tc in m.ToolCalls)
                t += EstimateTokens(tc.Arguments) + 12;
        return t;
    }

    /// <summary>
    /// Keeps the outgoing prompt inside the model's context window.
    /// 1. Oversized tool results are truncated in place.
    /// 2. Oldest message groups are dropped whole — an assistant message with
    ///    tool calls is never separated from its tool results (OpenAI-compatible
    ///    endpoints reject orphaned tool messages).
    /// The system prompt and the newest exchange are always preserved.
    /// </summary>
    internal static void FitToContextBudget(
        List<ChatMessage> messages, int contextTokenLimit, long? maxOutputTokens)
    {
        if (messages.Count <= 1) return;

        var outputReserve = (int)Math.Min(maxOutputTokens ?? DefaultOutputReserve, 1_000_000);
        var budget = contextTokenLimit - outputReserve - FixedOverheadTokens;
        if (budget < 4096) budget = 4096;

        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.Role == "tool" && m.Content is { Length: > ToolResultCharLimit })
            {
                messages[i] = m with
                {
                    Content = m.Content[..ToolResultCharLimit] +
                              $"\n[...truncated {m.Content.Length - ToolResultCharLimit} chars to fit context window...]"
                };
            }
        }

        long total = 0;
        foreach (var m in messages)
            total += MessageTokens(m);
        if (total <= budget) return;

        // Group atomic units starting after the system prompt (index 0).
        var starts = new List<int>();
        var ends = new List<int>();
        int i2 = 1;
        while (i2 < messages.Count)
        {
            int start = i2, end = start + 1;
            if (messages[start].ToolCalls is { Count: > 0 })
                while (end < messages.Count && messages[end].Role == "tool") end++;
            starts.Add(start);
            ends.Add(end);
            i2 = end;
        }

        int droppedUntil = -1; // exclusive; messages[1..droppedUntil) removed
        for (int g = 0; g < starts.Count - 1 && total > budget; g++) // newest group never dropped
        {
            for (int k = starts[g]; k < ends[g]; k++)
                total -= MessageTokens(messages[k]);
            droppedUntil = ends[g];
        }

        if (droppedUntil > 1)
        {
            int omitted = droppedUntil - 1;
            messages.RemoveRange(1, droppedUntil - 1);
            messages.Insert(1, new ChatMessage("system",
                $"[{omitted} earlier message(s) were omitted because the conversation exceeded the model context window.]"));
        }
    }

    internal static bool LooksLikePrematureStop(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;
        var lower = response.ToLowerInvariant();
        var stopIndicators = new[]
        {
            "here is", "here's the", "based on my", "i have explored",
            "the project structure", "project structure overview",
            "step 1", "step 2", "step 3",
            "architecture document", "technical architecture",
            "overview of", "summary of", "here is a summary",
            "below is", "above is", "the following"
        };
        var hasReadFiles = lower.Contains("read_file") || lower.Contains("read file")
            || lower.Contains("reading the source") || lower.Contains("after reading");

        int count = 0;
        foreach (var ind in stopIndicators)
            if (lower.Contains(ind)) count++;

        return count >= 2 && !hasReadFiles;
    }

    internal static bool LooksLikeHallucination(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;
        var lower = response.ToLowerInvariant();
        var indicators = new[]
        {
            "the code shows", "the file contains", "based on the source",
            "the implementation", "the class defines", "the method",
            "the interface", "this file", "the source code",
            "looking at the", "from the code", "as shown in",
            "the file has", "the project contains", "reading the",
            "the codebase", "the architecture", "the component",
            "let me call", "i'll start by", "i will start by",
            "i'll use the", "using the list_directory", "likely contains",
            "probably contains", "i should check", "i need to explore"
        };

        int indicatorCount = 0;
        foreach (var ind in indicators)
            if (lower.Contains(ind)) indicatorCount++;

        // At iteration 0 nothing has been read yet � describing code or
        // announcing tool plans without executing them is always fabrication.
        return indicatorCount >= 2;
    }
}
