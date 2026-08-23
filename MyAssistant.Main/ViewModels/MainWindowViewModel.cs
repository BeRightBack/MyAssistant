using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using MyAssistant.Abstractions.Agent;
using MyAssistant.Abstractions.Services;
using MyAssistant.Core.Memory;
using MyAssistant.Main.Models;

namespace MyAssistant.Main.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IAgent _agent;
    private readonly ISessionService _sessions;
    private readonly IReadOnlyList<ITool> _tools;
    private string _inputText = "";
    private bool _isProcessing = false;
    private string _sessionId = "";
    private string _statusText = "Ready";
    private string _sessionTitle = "New Chat";
    private string _providerStatus = "Connected";
    private string _modelInfo = "";
    private string _tokenUsage = "";
    private string _statusBarText = "Ready";
    private string _statusBarCenter = "";
    private string _toolCount = "";
    private string _sessionCount = "";
    private string _apiKeyStatusText = "Checking...";
    private Color _apiKeyStatusColor = Colors.Gray;
    private string _activeProviderName = "";
    private double _totalCost;

    // Pricing per 1M tokens (input/output) — free-tier / local = 0
    private static readonly Dictionary<string, (double Input, double Output)> _pricing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qwen3:8b"] = (0, 0),
        ["qwen3:4b"] = (0, 0),
        ["qwen3:1.7b"] = (0, 0),
        ["qwen2.5-coder:3b"] = (0, 0),
        ["gemini-3.5-flash"] = (0.075, 0.30),
        ["qwen/qwen3.6-27b"] = (0.0, 0.0),
        ["openai/gpt-oss-120b"] = (0.0, 0.0),
        ["allam-2-7b"] = (0.0, 0.0),
    };
    private string _errorMessage = "";
    private bool _hasError = false;
    private string _selectedModel = "";
    private string _searchText = "";
    private CancellationTokenSource? _currentCts;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();
    public ObservableCollection<SessionViewModel> Sessions { get; } = new();
    public ObservableCollection<SessionViewModel> FilteredSessions { get; } = new();
    public ObservableCollection<string> ProviderNames { get; } = new();
    public ObservableCollection<string> AvailableModels { get; } = new();
    public event Action? ScrollToBottomRequested;
    public event Action? ProviderChanged;
    public event Action<string>? ThemeChanged;

    public string InputText
    {
        get => _inputText;
        set
        {
            SetProperty(ref _inputText, value);
            OnPropertyChanged(nameof(CanSend));
            SendCommand?.RaiseCanExecuteChanged();
            if (ShowWelcome && !string.IsNullOrWhiteSpace(value))
                ShowWelcome = false;
        }
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set { SetProperty(ref _isProcessing, value); OnPropertyChanged(nameof(IsNotProcessing)); OnPropertyChanged(nameof(CanSend)); SendCommand?.RaiseCanExecuteChanged(); }
    }

    public bool IsNotProcessing => !IsProcessing;
    public bool CanSend => !IsProcessing && !string.IsNullOrWhiteSpace(InputText);

    public bool IsNotEmpty => Messages.Count > 0;
    public bool ShowWelcome { get => _showWelcome; set { SetProperty(ref _showWelcome, value); } }
    private bool _showWelcome = true;

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string SessionTitle { get => _sessionTitle; set => SetProperty(ref _sessionTitle, value); }
    public string ProviderStatus { get => _providerStatus; set => SetProperty(ref _providerStatus, value); }
    public string ModelInfo { get => _modelInfo; set => SetProperty(ref _modelInfo, value); }
    public string TokenUsage { get => _tokenUsage; set => SetProperty(ref _tokenUsage, value); }
    public string StatusBarText { get => _statusBarText; set => SetProperty(ref _statusBarText, value); }
    public string StatusBarCenter { get => _statusBarCenter; set => SetProperty(ref _statusBarCenter, value); }
    public string ToolCount { get => _toolCount; set => SetProperty(ref _toolCount, value); }
    public string SessionCount { get => _sessionCount; set => SetProperty(ref _sessionCount, value); }
    public string ApiKeyStatusText { get => _apiKeyStatusText; set => SetProperty(ref _apiKeyStatusText, value); }
    public Color ApiKeyStatusColor { get => _apiKeyStatusColor; set { _apiKeyStatusColor = value; OnPropertyChanged(); } }
    public string ActiveProviderName { get => _activeProviderName; set { SetProperty(ref _activeProviderName, value); } }
    public string ErrorMessage { get => _errorMessage; set { SetProperty(ref _errorMessage, value); OnPropertyChanged(); } }
    public bool HasError { get => _hasError; set { _hasError = value; OnPropertyChanged(); } }
    public string SelectedModel { get => _selectedModel; set { SetProperty(ref _selectedModel, value); } }
    public string SearchText { get => _searchText; set { SetProperty(ref _searchText, value); FilterSessions(); } }
    public Color ConnectionStatusColor => HasError ? Colors.OrangeRed : Colors.LimeGreen;

    public AsyncRelayCommand SendCommand { get; }
    public AsyncRelayCommand NewSessionCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearErrorCommand { get; }

    public MainWindowViewModel() : this(
        App.Services.GetRequiredService<IAgent>(),
        App.Services.GetRequiredService<ISessionService>(),
        App.Services.GetServices<ITool>().ToList()) { }

    public MainWindowViewModel(IAgent agent, ISessionService sessions, IReadOnlyList<ITool> tools)
    {
        _agent = agent;
        _sessions = sessions;
        _tools = tools;
        SendCommand = new AsyncRelayCommand(SendMessageAsync, () => CanSend);
        NewSessionCommand = new AsyncRelayCommand(NewSessionAsync);
        CancelCommand = new RelayCommand(CancelCurrent, () => IsProcessing);
        ClearErrorCommand = new RelayCommand(() => { HasError = false; ErrorMessage = ""; });
        _ = InitializeAsync();
    }

    public void RefreshProviderInfo()
    {
        var config = App.GetConfig();
        var activeKey = config.ActiveProvider ?? "ollama";
        var ps = config.Providers?.GetValueOrDefault(activeKey);

        ProviderNames.Clear();
        if (config.Providers != null)
        {
            foreach (var kvp in config.Providers)
                ProviderNames.Add(kvp.Key);
        }

        ActiveProviderName = activeKey;

        var providerName = ps?.Name ?? "Ollama";
        var modelId = ps?.DefaultModel ?? "unknown";
        ProviderStatus = providerName;
        ModelInfo = $"{providerName} / {modelId}";

        var hasApiKey = !string.IsNullOrWhiteSpace(ps?.ApiKey);
        var isLocal = ps?.BaseUrl?.Contains("localhost") == true || ps?.BaseUrl?.Contains("127.0.0.1") == true;

        if (isLocal || hasApiKey)
        {
            ApiKeyStatusText = isLocal ? "Local — no key needed" : $"API key set — {providerName}";
            ApiKeyStatusColor = Color.FromRgb(0x3F, 0xB9, 0x50);
        }
        else
        {
            ApiKeyStatusText = "No API key — click to configure";
            ApiKeyStatusColor = Color.FromRgb(0xFF, 0xA6, 0x57);
        }

        StatusBarText = $"Connected to {providerName} — {_tools.Count} tools available";
        ToolCount = $"{_tools.Count} tools";
        SessionCount = $"{Sessions.Count} sessions";

        _ = RefreshModelsAsync();
    }

    public async Task RefreshModelsAsync()
    {
        var config = App.GetConfig();
        var activeKey = config.ActiveProvider ?? "ollama";
        var ps = config.Providers?.GetValueOrDefault(activeKey);
        if (ps == null) return;

        var baseUrl = ps.BaseUrl?.TrimEnd('/') ?? "";
        var currentModel = ps.DefaultModel ?? "";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MyAssistant/1.0");
            var kind = App.DetectProviderKind(baseUrl, ps.Kind);
            List<string> models = new();

            if (kind == MyAssistant.Abstractions.Agent.ProviderKind.Ollama)
            {
                var response = await http.GetFromJsonAsync<OllamaModelsResponse>($"{baseUrl}/api/tags");
                models = response?.Models?.Select(m => m.Name).ToList() ?? new();
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
                if (!string.IsNullOrEmpty(ps.ApiKey))
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ps.ApiKey);
                var resp = await http.SendAsync(request);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                    if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        models = data.EnumerateArray().Where(m => m.TryGetProperty("id", out _)).Select(m => { var id = m.GetProperty("id").GetString() ?? ""; return id.StartsWith("models/") ? id["models/".Length..] : id; }).Where(id => !string.IsNullOrEmpty(id) && IsCloudChatModel(id, kind)).ToList();
                }
            }

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                AvailableModels.Clear();
                foreach (var m in models) AvailableModels.Add(m);
                if (!string.IsNullOrEmpty(currentModel))
                {
                    SelectedModel = models.Contains(currentModel) ? currentModel : currentModel;
                    if (!models.Contains(currentModel) && models.Count > 0) { AvailableModels.Insert(0, currentModel); SelectedModel = currentModel; }
                }
                else if (models.Count > 0) SelectedModel = models.First();
            });
        }
        catch
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                AvailableModels.Clear();
                if (!string.IsNullOrEmpty(currentModel)) { AvailableModels.Add(currentModel); SelectedModel = currentModel; }
            });
        }
    }

    public void RefreshModels() { _ = RefreshModelsAsync(); }

    private static bool IsCloudChatModel(string id, MyAssistant.Abstractions.Agent.ProviderKind kind)
    {
        var lower = id.ToLowerInvariant();

        var excludePatterns = new[]
        {
            "embed", "tts", "stt", "whisper", "image", "video", "lyria", "veo",
            "robotics", "antigravity", "deep-research", "computer-use", "aqa",
            "guard", "realtime", "moderation", "native-audio", "nvidia-",
            "paraformer", "breezelm", "telechat", "ffmpeg"
        };
        foreach (var p in excludePatterns)
            if (lower.Contains(p)) return false;

        if (kind == MyAssistant.Abstractions.Agent.ProviderKind.Gemini)
        {
            if (lower.StartsWith("gemini-2.5-flash") || lower.StartsWith("gemini-2.5-pro") ||
                lower.StartsWith("gemini-3.5-flash") || lower.StartsWith("gemini-3.1-flash-lite") ||
                lower.StartsWith("gemini-flash-") || lower.StartsWith("gemini-pro-") ||
                lower.StartsWith("gemma-"))
                return true;
            return false;
        }

        if (kind == MyAssistant.Abstractions.Agent.ProviderKind.Groq)
        {
            if (lower.StartsWith("qwen") || lower.StartsWith("meta-llama/llama") ||
                lower.StartsWith("openai/gpt-oss") || lower.StartsWith("allam") ||
                lower.StartsWith("gemma") || lower.StartsWith("mixtral") ||
                lower.StartsWith("deepseek") || lower.StartsWith("phi-"))
                return true;
            return false;
        }

        if (kind == MyAssistant.Abstractions.Agent.ProviderKind.Mistral)
        {
            if (lower.StartsWith("mistral") || lower.StartsWith("open-mistral") ||
                lower.StartsWith("codestral") || lower.StartsWith("pixtral"))
                return true;
            return false;
        }

        return true;
    }

    public void OnModelChanged(string model)
    {
        if (string.IsNullOrEmpty(model)) return;
        var config = App.GetConfig();
        var activeKey = config.ActiveProvider ?? "ollama";
        if (config.Providers?.TryGetValue(activeKey, out var ps) == true)
        {
            ps.DefaultModel = model;
            _ = App.SaveConfigAsync(config);
            ModelInfo = $"{ps.Name} / {model}";
        }
    }

    public void OnThemeChanged(string theme)
    {
        var config = App.GetConfig();
        if (config.UI == null) config.UI = new UISettings();
        config.UI.Theme = theme;
        _ = App.SaveConfigAsync(config);
        App.ApplyTheme(theme);
        ThemeChanged?.Invoke(theme);
    }

    private async Task InitializeAsync()
    {
        try
        {
            var sessions = await _sessions.ListAsync();
            foreach (var s in sessions)
            {
                var timeAgo = GetTimeAgo(s.UpdatedAt);
                Sessions.Add(new SessionViewModel { Title = s.Title, Id = s.Id, IsSelected = false, TimeAgo = timeAgo });
            }

            FilterSessions();

            if (sessions.Length > 0)
            {
                var last = sessions[0];
                _sessionId = last.Id;
                if (Sessions.Count > 0) Sessions[0].IsSelected = true;
                SessionTitle = last.Title;

                var messages = await _sessions.GetMessagesAsync(_sessionId);
                foreach (var msg in messages)
                {
                    Messages.Add(new ChatMessageViewModel
                    {
                        Role = msg.Role,
                        RoleName = msg.Role == "user" ? "You" : "MyAssistant",
                        Content = msg.Content ?? "",
                        Timestamp = DateTime.Now,
                        Usage = ""
                    });
                }
            }
            else
            {
                var session = await _sessions.CreateAsync("New Chat");
                _sessionId = session.Id;
                Sessions.Add(new SessionViewModel { Title = "New Chat", Id = session.Id, IsSelected = true, TimeAgo = "now" });
                FilterSessions();
            }
        }
        catch
        {
            var session = await _sessions.CreateAsync("New Chat");
            _sessionId = session.Id;
            Sessions.Add(new SessionViewModel { Title = "New Chat", Id = session.Id, IsSelected = true, TimeAgo = "now" });
            FilterSessions();
        }
        RefreshProviderInfo();
        SessionCount = $"{Sessions.Count} sessions";
    }

    private static string GetTimeAgo(DateTime dt)
    {
        var span = DateTime.UtcNow - dt;
        if (span.TotalMinutes < 1) return "now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return dt.ToString("MMM dd");
    }

    public void OnProviderSwitched()
    {
        ProviderChanged?.Invoke();
        RefreshProviderInfo();
    }

    private async Task<bool> KnowledgeEntryExistsAsync(string category, string content)
    {
        try
        {
            var existing = await _sessions.ListKnowledgeAsync(_sessionId, category);
            return existing.Any(e => e.Content.Trim().Equals(content.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsProcessing) return;

        var userText = InputText.Trim();
        InputText = "";
        IsProcessing = true;
        HasError = false;
        ErrorMessage = "";
        StatusText = "Thinking...";
        StatusBarText = "Processing...";
        _currentCts = new CancellationTokenSource(TimeSpan.FromSeconds(3600));

        Messages.Add(new ChatMessageViewModel { Role = "User", RoleName = "You", Content = userText, Timestamp = DateTime.Now });
        OnPropertyChanged(nameof(IsNotEmpty));

        try { await _sessions.AddMessageAsync(_sessionId, new ChatMessage("user", userText)); }
        catch (Exception ex) { AppLog.Warn($"Failed to save user message: {ex.Message}"); }

        var assistantMsg = new ChatMessageViewModel { Role = "Assistant", RoleName = "MyAssistant", Content = "", Timestamp = DateTime.Now };
        Messages.Add(assistantMsg);

        var collectedToolCalls = new List<ChatToolCall>();
        long sessionPromptTokens = 0, sessionCompletionTokens = 0;
        var currentSession = Sessions.FirstOrDefault(s => s.Id == _sessionId);
        if (currentSession != null && Sessions.IndexOf(currentSession) > 0)
            Sessions.Move(Sessions.IndexOf(currentSession), 0);

        try
        {
            var config = App.GetConfig();
            var (providerConfig, modelId) = App.CreateProviderConfig();
            var ps = config.Providers?.GetValueOrDefault(config.ActiveProvider ?? "") ?? new ProviderSettings();

            StatusText = $"Connecting to {providerConfig.BaseUrl} with model {modelId}...";

            // Auto-store middleware: detect "remember that..." patterns
            var promptToSend = userText;
            try
            {
                if (MemoryTriggerParser.TryParse(userText, out var triggerCategory, out var triggerContent))
                {
                    if (!await KnowledgeEntryExistsAsync(triggerCategory, triggerContent))
                    {
                        await _sessions.StoreKnowledgeAsync(_sessionId, triggerCategory, triggerContent);
                        StatusText = triggerCategory == "global"
                            ? "Global knowledge stored automatically."
                            : "Knowledge stored automatically.";
                    }
                }
            }
            catch (Exception ex) { AppLog.Warn($"Auto-store knowledge failed: {ex.Message}"); }

            // Search-first middleware: auto-inject knowledge context
            try
            {
                var knowledgeResults = await _sessions.SearchKnowledgeAsync(_sessionId, userText, 5);
                var globalResults = await _sessions.SearchKnowledgeAsync(_sessionId, userText, 5, "global");
                var contextParts = new StringBuilder();

                var allResults = knowledgeResults.Concat(globalResults).DistinctBy(e => e.Id).ToList();
                if (allResults.Count > 0)
                {
                    contextParts.AppendLine("[KNOWLEDGE CONTEXT — retrieved automatically from your memory]");
                    foreach (var entry in allResults)
                        contextParts.AppendLine($"[{entry.Category}] {entry.Content}");
                    contextParts.AppendLine("[END KNOWLEDGE CONTEXT]");
                }

                // Also search chat history for relevant past messages
                try
                {
                    var historyMsgs = await _sessions.GetMessagesAsync(_sessionId);
                    var keywords = userText.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var relevantHistory = new List<string>();
                    foreach (var msg in historyMsgs)
                    {
                        if (string.IsNullOrEmpty(msg.Content) || msg.Role == "system") continue;
                        if (msg.Role == "user" && msg.Content == userText) continue;
                        var lower = msg.Content.ToLower();
                        if (keywords.Any(k => k.Length > 3 && lower.Contains(k)))
                            relevantHistory.Add($"[{msg.Role}]: {msg.Content}");
                    }
                    if (relevantHistory.Count > 0)
                    {
                        contextParts.AppendLine("[CHAT HISTORY — relevant past messages]");
                        foreach (var h in relevantHistory.TakeLast(6))
                            contextParts.AppendLine(h);
                        contextParts.AppendLine("[END CHAT HISTORY]");
                    }
                }
                catch { }

                if (contextParts.Length > 0)
                    promptToSend = contextParts.ToString() + "\n" + userText;
            }
            catch (Exception ex) { AppLog.Warn($"Knowledge/search failed: {ex.Message}"); }

            // File mention detection middleware
            try
            {
                var filePattern = new System.Text.RegularExpressions.Regex(@"[\w\\\/\.\-]+\.\w{1,5}\b");
                var matches = filePattern.Matches(userText);
                var fileContext = new StringBuilder();
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    var filePath = m.Value;
                    if (!System.IO.Path.IsPathRooted(filePath))
                        filePath = System.IO.Path.Combine(App.GetWorkspaceRoot(), filePath);
                    if (System.IO.File.Exists(filePath))
                    {
                        try
                        {
                            var content = await System.IO.File.ReadAllTextAsync(filePath);
                            if (content.Length > 8000) content = content.Substring(0, 8000) + "\n... [truncated]";
                            fileContext.AppendLine($"[FILE: {filePath}]\n{content}\n[END FILE]");
                        }
                        catch { }
                    }
                }
                if (fileContext.Length > 0)
                    promptToSend = fileContext + "\n" + promptToSend;
            }
            catch (Exception ex) { AppLog.Warn($"File mention detection failed: {ex.Message}"); }

            // Error paste detection middleware
            try
            {
                var hasError = userText.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                               userText.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
                               System.Text.RegularExpressions.Regex.IsMatch(userText, @"at .+ in .+:line \d+");
                if (hasError)
                {
                    var errorResults = await _sessions.SearchKnowledgeAsync(_sessionId, userText, 3, "insight");
                    if (errorResults.Length > 0)
                    {
                        var errCtx = new StringBuilder();
                        errCtx.AppendLine("[RELATED PAST ERRORS — from your knowledge]");
                        foreach (var entry in errorResults)
                            errCtx.AppendLine($"[{entry.Category}] {entry.Content}");
                        errCtx.AppendLine("[END RELATED PAST ERRORS]\n");
                        promptToSend = errCtx + promptToSend;
                    }
                }
            }
            catch (Exception ex) { AppLog.Warn($"Error paste detection failed: {ex.Message}"); }

            // Git change context middleware
            try
            {
                var gitDir = App.GetWorkspaceRoot();
                if (System.IO.Directory.Exists(System.IO.Path.Combine(gitDir, ".git")))
                {
                    var gitCtx = new StringBuilder();
                    var log = await RunProcessAsync("git", $"--git-dir=\"{gitDir}\" log --oneline -5");
                    if (!string.IsNullOrWhiteSpace(log))
                        gitCtx.AppendLine($"[RECENT GIT CHANGES]\n{log}");
                    var diffStat = await RunProcessAsync("git", $"--git-dir=\"{gitDir}\" diff --stat HEAD");
                    if (!string.IsNullOrWhiteSpace(diffStat))
                        gitCtx.AppendLine($"[CURRENT CHANGES]\n{diffStat}");
                    if (gitCtx.Length > 0)
                    {
                        gitCtx.AppendLine("[END GIT CONTEXT]\n");
                        promptToSend = gitCtx + promptToSend;
                    }
                }
            }
            catch (Exception ex) { AppLog.Warn($"Git context failed: {ex.Message}"); }

            // Project structure injection middleware (first message per session)
            try
            {
                if (Messages.Count <= 2)
                {
                    var workDir = App.GetWorkspaceRoot();
                    var tree = await RunProcessAsync("cmd", $"/c tree \"{workDir}\" /F /A 2>nul | findstr /V /C:\".git\" /C:\"node_modules\" /C:\"bin\" /C:\"obj\" /C:\".vs\"");
                    if (!string.IsNullOrWhiteSpace(tree) && tree.Length < 6000)
                    {
                        promptToSend = $"[PROJECT STRUCTURE]\n{tree}\n[END PROJECT STRUCTURE]\n\n" + promptToSend;
                    }
                }
            }
            catch (Exception ex) { AppLog.Warn($"Project structure injection failed: {ex.Message}"); }

            var call = new AgentCall(
                SessionId: _sessionId,
                Prompt: promptToSend,
                Provider: providerConfig,
                ModelId: modelId,
                Attachments: ImmutableArray<Attachment>.Empty,
                MaxOutputTokens: ps.MaxTokens,
                Temperature: ps.Temperature,
                TopP: ps.TopP,
                History: await LoadHistoryAsync(config.Agent?.MaxContextMessages ?? 50));

            AppLog.Info($"Turn started (session {_sessionId[..8]}, history {call.History?.Count ?? 0} msgs)");

            var contentBuilder = new StringBuilder();
            var thinkingBuilder = new StringBuilder();
            var firstContent = true;

            await foreach (var evt in _agent.RunAsync(call, _currentCts.Token))
            {
                switch (evt)
                {
                    case AgentEvent.TextDelta td:
                        contentBuilder.Append(td.Delta);
                        assistantMsg.Content = contentBuilder.ToString();
                        StatusText = "Streaming...";
                        ScrollToBottomRequested?.Invoke();
                        if (firstContent && contentBuilder.Length > 0)
                        {
                            var preview = contentBuilder.ToString().Split('\n')[0];
                            if (preview.Length > 40) preview = preview[..40] + "...";
                            if (currentSession != null) currentSession.Title = preview;
                            SessionTitle = preview;
                            firstContent = false;
                            try { await _sessions.SaveAsync(new Session(_sessionId, preview, DateTime.MinValue, DateTime.MinValue, 0, 0)); }
                            catch (Exception ex) { AppLog.Warn($"Failed to save session title: {ex.Message}"); }
                        }
                        break;

                    case AgentEvent.ThinkingDelta th:
                        thinkingBuilder.Append(th.Delta);
                        assistantMsg.Thinking = thinkingBuilder.ToString();
                        StatusText = "Thinking...";
                        ScrollToBottomRequested?.Invoke();
                        break;

                    case AgentEvent.ToolCallStart tc:
                        collectedToolCalls.Add(new ChatToolCall(tc.Id, tc.Name, tc.ArgumentsJson ?? ""));
                        assistantMsg.ToolCalls.Add(new ToolCallViewModel
                        {
                            Id = tc.Id, Name = tc.Name, ArgsPreview = tc.ArgumentsJson?.Length > 120
                                ? tc.ArgumentsJson[..120] + "..." : tc.ArgumentsJson ?? "",
                            StatusText = "Running...", StatusColor = Colors.DodgerBlue, Icon = "◉"
                        });
                        StatusText = $"Running: {tc.Name}";
                        StatusBarText = $"Tool: {tc.Name}";
                        ScrollToBottomRequested?.Invoke();
                        break;

                    case AgentEvent.ToolCallResult tcr:
                        var existing = assistantMsg.ToolCalls.FirstOrDefault(t => t.Id == tcr.Id);
                        if (existing != null)
                        {
                            existing.StatusText = tcr.IsError ? "Failed" : "Done";
                            existing.StatusColor = tcr.IsError ? Colors.OrangeRed : Colors.LimeGreen;
                            existing.Icon = tcr.IsError ? "✕" : "✓";
                            existing.ArgsPreview = tcr.Content.Length > 200 ? tcr.Content[..200] + "..." : tcr.Content;
                        }
                        StatusBarText = tcr.IsError ? $"Error: {tcr.Name}" : $"Done: {tcr.Name}";
                        ScrollToBottomRequested?.Invoke();
                        break;

                    case AgentEvent.Usage u:
                        TokenUsage = $"{u.Tokens.PromptTokens} in / {u.Tokens.CompletionTokens} out";
                        sessionPromptTokens += u.Tokens.PromptTokens;
                        sessionCompletionTokens += u.Tokens.CompletionTokens;
                        _totalCost += ComputeCost(u.Tokens.PromptTokens, u.Tokens.CompletionTokens);
                        StatusBarCenter = $"Tokens: {sessionPromptTokens + sessionCompletionTokens} total" + (_totalCost > 0 ? $" — ${_totalCost:F4}" : "");
                        break;

                    case AgentEvent.Done d:
                        if (d.Error != null && string.IsNullOrEmpty(contentBuilder.ToString()))
                        { assistantMsg.Content = $"Error: {d.Error}"; ErrorMessage = d.Error; HasError = true; }
                        else if (d.Error != null)
                        { assistantMsg.Content = contentBuilder.ToString() + $"\n\nError: {d.Error}"; ErrorMessage = d.Error; HasError = true; }
                        break;
                }
            }

            StatusBarText = "Ready";
            StatusText = "Ready";
            AppLog.Info("Turn finished");

            try
            {
                var finalContent = contentBuilder.ToString();
                if (!string.IsNullOrEmpty(finalContent) || collectedToolCalls.Count > 0)
                    await _sessions.AddMessageAsync(_sessionId, new ChatMessage("assistant", finalContent, collectedToolCalls.Count > 0 ? collectedToolCalls : null));

                if (sessionPromptTokens > 0 || sessionCompletionTokens > 0)
                {
                    var existing = await _sessions.GetAsync(_sessionId);
                    if (existing != null)
                        await _sessions.SaveAsync(existing with { PromptTokens = sessionPromptTokens, CompletionTokens = sessionCompletionTokens });
                }
            }
            catch (Exception ex) { AppLog.Warn($"Failed to save assistant message: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            var errorMsg = ex.InnerException?.Message ?? ex.Message;
            var config2 = App.GetConfig();
            var ps2 = config2.Providers?.GetValueOrDefault(config2.ActiveProvider ?? "");
            var diagInfo = $"[{ps2?.Kind ?? "?"}] {ps2?.BaseUrl ?? "?"} model={ps2?.DefaultModel ?? "?"} | {errorMsg}";
            assistantMsg.Content = $"Error: {diagInfo}";
            ErrorMessage = diagInfo;
            HasError = true;
            StatusText = "Error";
            StatusBarText = $"Error: {errorMsg}";
        }
        finally
        {
            IsProcessing = false;
            _currentCts?.Dispose();
            _currentCts = null;
        }
    }

    private async Task<IReadOnlyList<ChatMessage>?> LoadHistoryAsync(int maxMessages)
    {
        try
        {
            var msgs = await _sessions.GetMessagesAsync(_sessionId);
            // Last message is the prompt we just saved — exclude it.
            var recent = msgs.SkipLast(1);
            if (maxMessages > 0)
                recent = recent.TakeLast(maxMessages);

            // Total character budget keeps prompt+output inside num_ctx.
            const int TotalBudgetChars = 24000;
            var picked = new List<ChatMessage>();
            int used = 0;
            foreach (var m in recent.Reverse())
            {
                if (string.IsNullOrWhiteSpace(m.Content) || m.Role == "system") continue;
                var content = m.Content.Length > 4000 ? m.Content[..4000] + "\n... (truncated)" : m.Content;
                if (used + content.Length > TotalBudgetChars) break;
                used += content.Length;
                picked.Add(new ChatMessage(m.Role.ToLowerInvariant(), content));
            }
            picked.Reverse();
            return picked.Count > 0 ? picked : null;
        }
        catch (Exception ex) { AppLog.Warn($"Failed to load history: {ex.Message}"); return null; }
    }

    public async Task EditAndResendAsync(ChatMessageViewModel messageToEdit, string newContent)
    {
        if (IsProcessing) return;

        int msgIndex = Messages.IndexOf(messageToEdit);
        if (msgIndex < 0) return;

        var msgsToRemove = Messages.Skip(msgIndex).ToList();
        foreach (var m in msgsToRemove) Messages.Remove(m);

        try { await _sessions.DeleteMessagesFromAsync(_sessionId, msgIndex); }
        catch (Exception ex) { AppLog.Warn($"Failed to delete messages: {ex.Message}"); }

        InputText = newContent;
        OnPropertyChanged(nameof(IsNotEmpty));
        await SendMessageAsync();
    }

    public async Task RegenerateAsync(ChatMessageViewModel assistantMessage)
    {
        if (IsProcessing) return;

        int msgIndex = Messages.IndexOf(assistantMessage);
        if (msgIndex < 0) return;

        var msgsToRemove = Messages.Skip(msgIndex).ToList();
        foreach (var m in msgsToRemove) Messages.Remove(m);

        try { await _sessions.DeleteMessagesFromAsync(_sessionId, msgIndex); }
        catch (Exception ex) { AppLog.Warn($"Failed to delete messages: {ex.Message}"); }

        var lastUserMsg = Messages.LastOrDefault(m => m.Role == "User");
        if (lastUserMsg == null) return;

        InputText = lastUserMsg.Content;
        Messages.Remove(lastUserMsg);
        OnPropertyChanged(nameof(IsNotEmpty));
        await SendMessageAsync();
    }

    private async Task NewSessionAsync()
    {
        Messages.Clear();
        var session = await _sessions.CreateAsync("New Chat");
        _sessionId = session.Id;
        foreach (var s in Sessions) s.IsSelected = false;
        Sessions.Insert(0, new SessionViewModel { Title = "New Chat", Id = session.Id, IsSelected = true, TimeAgo = "now" });
        FilterSessions();
        SessionTitle = "New Chat";
        StatusText = "Ready";
        StatusBarText = "New session";
        SessionCount = $"{Sessions.Count} sessions";
        OnPropertyChanged(nameof(IsNotEmpty));
    }

    private void CancelCurrent()
    {
        _currentCts?.Cancel();
        StatusText = "Cancelled";
        StatusBarText = "Cancelled";
    }

    public async void SwitchSession(string sessionId)
    {
        if (sessionId == _sessionId) return;
        _currentCts?.Cancel();
        IsProcessing = false;

        _sessionId = sessionId;
        foreach (var s in Sessions) s.IsSelected = (s.Id == sessionId);

        var selectedSession = Sessions.FirstOrDefault(s => s.Id == sessionId);
        SessionTitle = selectedSession?.Title ?? "Chat";

        Messages.Clear();
        OnPropertyChanged(nameof(IsNotEmpty));

        try
        {
            var messages = await _sessions.GetMessagesAsync(sessionId);
            foreach (var msg in messages)
            {
                var vm = new ChatMessageViewModel
                {
                    Role = msg.Role,
                    RoleName = msg.Role == "user" ? "You" : "MyAssistant",
                    Content = msg.Content ?? "",
                    Timestamp = DateTime.Now,
                    Usage = ""
                };

                if (msg.ToolCalls != null)
                {
                    foreach (var tc in msg.ToolCalls)
                    {
                        vm.ToolCalls.Add(new ToolCallViewModel
                        {
                            Id = tc.Id, Name = tc.Name,
                            ArgsPreview = tc.Arguments.Length > 120 ? tc.Arguments[..120] + "..." : tc.Arguments,
                            StatusText = "Completed", StatusColor = Colors.LimeGreen, Icon = "✓"
                        });
                    }
                }

                Messages.Add(vm);
            }
        }
        catch (Exception ex) { AppLog.Warn($"Failed to load session messages: {ex.Message}"); }

        StatusBarText = $"Switched to: {selectedSession?.Title ?? "Chat"}";

        try
        {
            var sessionData = await _sessions.GetAsync(sessionId);
            if (sessionData != null && (sessionData.PromptTokens > 0 || sessionData.CompletionTokens > 0))
            {
                _totalCost = ComputeCost(sessionData.PromptTokens, sessionData.CompletionTokens);
                StatusBarCenter = $"Tokens: {sessionData.PromptTokens + sessionData.CompletionTokens} total" + (_totalCost > 0 ? $" — ${_totalCost:F4}" : "");
            }
            else
            {
                _totalCost = 0;
                StatusBarCenter = "";
            }
        }
        catch { StatusBarCenter = ""; }
    }

    public async Task RenameSession(string sessionId, string newName)
    {
        var session = Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null) return;
        session.Title = newName;
        await _sessions.SaveAsync(new Session(sessionId, newName, DateTime.MinValue, DateTime.MinValue, 0, 0));
        if (sessionId == _sessionId) SessionTitle = newName;
        FilterSessions();
    }

    public async void DeleteSession(string sessionId)
    {
        var session = Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null) return;
        await _sessions.DeleteAsync(sessionId);
        Sessions.Remove(session);
        FilterSessions();
        SessionCount = $"{Sessions.Count} sessions";

        if (sessionId == _sessionId)
        {
            if (Sessions.Count > 0) SwitchSession(Sessions[0].Id);
            else await NewSessionAsync();
        }
    }

    public void FilterSessions()
    {
        FilteredSessions.Clear();
        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? Sessions.ToList()
            : Sessions.Where(s => s.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var s in filtered) FilteredSessions.Add(s);
    }

    public string ExportChatAsMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {SessionTitle}");
        sb.AppendLine();
        foreach (var msg in Messages)
        {
            sb.AppendLine($"## {msg.RoleName} ({msg.Timestamp:HH:mm})");
            sb.AppendLine();
            sb.AppendLine(msg.Content);
            if (msg.ToolCalls.Count > 0)
            {
                sb.AppendLine();
                foreach (var tc in msg.ToolCalls)
                    sb.AppendLine($"- **Tool**: {tc.Name} — {tc.StatusText}");
            }
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(n); return true;
    }

    private double ComputeCost(long promptTokens, long completionTokens)
    {
        var config = App.GetConfig();
        var activeKey = config.ActiveProvider ?? "ollama";
        var ps = config.Providers?.GetValueOrDefault(activeKey);
        var modelId = ps?.DefaultModel ?? SelectedModel;
        if (string.IsNullOrEmpty(modelId)) return 0;
        if (_pricing.TryGetValue(modelId, out var price))
            return (promptTokens * price.Input + completionTokens * price.Output) / 1_000_000.0;
        return 0;
    }

    private static async Task<string> RunProcessAsync(string fileName, string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return "";
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return output.Trim();
        }
        catch { return ""; }
    }
}

public class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) { _execute = execute; _canExecute = canExecute; }
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public async void Execute(object? parameter) => await _execute();
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
