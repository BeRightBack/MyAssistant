using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyAssistant.Abstractions.Agent;
using MyAssistant.Abstractions.Services;
using MyAssistant.Core.Agent;
using MyAssistant.Core.Tools;
using MyAssistant.Providers;
using MyAssistant.Main.ViewModels;

namespace MyAssistant.Main;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private ServiceProvider? _serviceProvider;
    private static AppConfig? _config;
    private static SettingsRepository? _repo;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Info("=== Application starting ===");

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLog.Error("Unhandled domain exception", ex);
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            AppLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        DispatcherUnhandledException += (s, args) =>
        {
            AppLog.Error("Dispatcher unhandled exception", args.Exception);
            args.Handled = true;
        };

        Exit += (s, args) => AppLog.Info($"Application exiting (code {args.ApplicationExitCode})");

        try
        {
            // DB credentials are read from user-scope environment variables (MYCODER_DB_*)
            // by DbConfig — never hard-code them here or in appsettings.json.
            _repo = new SettingsRepository();
            await _repo.InitializeAsync();
            await _repo.InitializeDefaultRolesAsync();
            _config = await LoadConfigAsync();
            ApplyTheme(_config.UI?.Theme ?? "dark");
            ToolSandbox.WorkingDirectory = GetWorkspaceRoot();

            // Pre-fetch active role system prompt (DI factory is sync)
            var activeSystemPrompt = await GetSystemPromptAsync();

            var services = new ServiceCollection();

            // Semantic recall: reuse the Gemini provider API key for embeddings when present
            Func<string, CancellationToken, Task<float[]?>>? embedder = null;
            try
            {
                var gemini = await _repo.GetProviderAsync("gemini");
                if (!string.IsNullOrEmpty(gemini?.ApiKey))
                    embedder = new GeminiEmbeddingClient(gemini.ApiKey).EmbedAsync;
            }
            catch (Exception ex) { AppLog.Warn($"Semantic recall unavailable: {ex.Message}"); }

            services.AddSingleton<ISessionService>(_ => new MySqlSessionService(embedder));
            services.AddSingleton<ITool, ReadFileTool>();
            services.AddSingleton<ITool, WriteFileTool>();
            services.AddSingleton<ITool, EditFileTool>();
            services.AddSingleton<ITool, GlobTool>();
            services.AddSingleton<ITool, GrepTool>();
            services.AddSingleton<ITool, BashTool>();
            services.AddSingleton<ITool, WebFetchTool>();
            services.AddSingleton<ITool, WebSearchTool>();
            services.AddSingleton<ITool, ListDirectoryTool>();
            services.AddSingleton<ITool>(sp =>
            {
                Func<IReadOnlyList<ITool>> getParentTools = () => sp.GetServices<ITool>().Where(t => t.Name != "task_agent").ToList();
                return new TaskAgentTool((systemPrompt, subTools) =>
                {
                    var (pp, pm) = CreateProviderFromConfig();
                    return new ChatAgent(pp, pm, subTools, systemPrompt, 5);
                }, getParentTools, () => CreateProviderConfig().Config, () => CreateProviderConfig().ModelId);
            });
            services.AddSingleton<ITool>(sp => new StoreKnowledgeTool(() => sp.GetRequiredService<ISessionService>()));
            services.AddSingleton<ITool>(sp => new SearchKnowledgeTool(() => sp.GetRequiredService<ISessionService>()));
            services.AddSingleton<ITool>(sp => new ManageKnowledgeTool(() => sp.GetRequiredService<ISessionService>()));

            await _repo.InitializeDefaultToolsAsync(new List<string>
            {
                "read_file", "write_file", "edit_file", "glob", "grep",
                "bash", "web_fetch", "web_search", "list_directory", "task_agent",
                "store_knowledge", "search_knowledge", "manage_knowledge"
            });

            var toolStates = await _repo.GetToolStatesAsync();

            services.AddSingleton<IAgent>(provider =>
            {
                var allTools = provider.GetServices<ITool>().ToList();
                var tools = allTools.Where(t =>
                {
                    if (toolStates.TryGetValue(t.Name, out var enabled))
                        return enabled;
                    return true;
                }).ToList();
                var logger = provider.GetService<ILogger<ChatAgent>>();
                var (agentProvider, modelId) = CreateProviderFromConfig();
                var maxIter = _config?.Agent?.MaxIterations ?? 20;
                var maxCtx = _config?.Agent?.MaxContextTokens ?? ChatAgent.DefaultContextTokenLimit;
                return new ChatAgent(agentProvider, modelId, tools, activeSystemPrompt, maxIter, maxCtx, logger);
            });

            services.AddTransient<MainWindowViewModel>();
            services.AddTransient<MainWindow>();
            services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider, Services.AppLogLoggerProvider>();

            _serviceProvider = services.BuildServiceProvider();
            Services = _serviceProvider;

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
            AppLog.Info("Startup complete");
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup failed", ex);
            var logPath = Path.Combine(Path.GetTempPath(), "myassistant_crash.log");
            File.WriteAllText(logPath, ex.ToString());
            MessageBox.Show($"Startup error: {ex.Message}\n\nSee {logPath}", "MyAssistant Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    public static ProviderKind DetectProviderKind(string baseUrl, string? kindOverride = null)
    {
        if (!string.IsNullOrEmpty(kindOverride))
        {
            var k = kindOverride.ToLowerInvariant();
            if (k == "ollama") return ProviderKind.Ollama;
            if (k == "gemini") return ProviderKind.Gemini;
            if (k == "groq") return ProviderKind.Groq;
            if (k == "mistral") return ProviderKind.Mistral;
        }

        if (string.IsNullOrEmpty(baseUrl)) return ProviderKind.OpenAICompatible;
        var lower = baseUrl.ToLowerInvariant();
        if (lower.Contains("localhost:11434") || lower.Contains("127.0.0.1:11434") || lower.Contains("ollama.com"))
            return ProviderKind.Ollama;
        if (lower.Contains("generativelanguage.googleapis.com"))
            return ProviderKind.Gemini;
        if (lower.Contains("api.groq.com"))
            return ProviderKind.Groq;
        if (lower.Contains("api.mistral.ai"))
            return ProviderKind.Mistral;
        return ProviderKind.OpenAICompatible;
    }

    public static (IStreamingProvider Provider, string ModelId) CreateProviderFromConfig()
    {
        if (_config?.Providers != null && !string.IsNullOrEmpty(_config.ActiveProvider) &&
            _config.Providers.TryGetValue(_config.ActiveProvider, out var ps))
        {
            var kind = DetectProviderKind(ps.BaseUrl, ps.Kind);

            IStreamingProvider provider = kind == ProviderKind.Ollama
                ? new OllamaProvider(ps.BaseUrl, ps.ApiKey)
                : new OpenAICompatibleProvider(ps.BaseUrl, ps.ApiKey);

            return (provider, ps.DefaultModel);
        }

        return (new OllamaProvider(), "qwen3:1.7b");
    }

    public static (ProviderConfig Config, string ModelId) CreateProviderConfig()
    {
        if (_config?.Providers != null && !string.IsNullOrEmpty(_config.ActiveProvider) &&
            _config.Providers.TryGetValue(_config.ActiveProvider, out var ps))
        {
            var kind = DetectProviderKind(ps.BaseUrl, ps.Kind);
            return (new ProviderConfig(_config.ActiveProvider, ps.Name, ps.BaseUrl, ps.ApiKey, kind), ps.DefaultModel);
        }
        return (new ProviderConfig("ollama", "Ollama", "http://localhost:11434", null, ProviderKind.Ollama), "qwen3:1.7b");
    }

    private static async Task<AppConfig> LoadConfigAsync()
    {
        var config = new AppConfig();

        if (_repo != null)
        {
            var ids = await _repo.GetAllProviderIdsAsync();
            if (ids.Count > 0)
            {
                config.Providers = new Dictionary<string, ProviderSettings>();
                foreach (var id in ids)
                {
                    var ps = await _repo.GetProviderAsync(id);
                    if (ps != null) config.Providers[id] = ps;
                }
            }

            config.ActiveProvider = await _repo.GetSettingAsync("ActiveProvider", "ollama");

            config.Agent = new AgentSettings
            {
                SystemPrompt = await _repo.GetSettingAsync("SystemPrompt", ""),
                MaxIterations = int.TryParse(await _repo.GetSettingAsync("MaxIterations", "20"), out var mi) ? mi : 20,
                MaxContextMessages = int.TryParse(await _repo.GetSettingAsync("MaxContextMessages", "50"), out var mcm) ? mcm : 50,
                AutoScroll = await _repo.GetSettingAsync("AutoScroll", "true") == "true",
                ShowToolResults = await _repo.GetSettingAsync("ShowToolResults", "true") == "true",
                WorkspaceRoot = await _repo.GetSettingAsync("WorkspaceRoot", "")
            };

            config.UI = new UISettings
            {
                Theme = await _repo.GetSettingAsync("Theme", "dark"),
                FontFamily = await _repo.GetSettingAsync("FontFamily", "Consolas"),
                FontSize = double.TryParse(await _repo.GetSettingAsync("FontSize", "13"), out var fs) ? fs : 13
            };
        }

        if (config.Providers == null || config.Providers.Count == 0)
            await MigrateFromJsonAsync(config);

        return config;
    }

    private static async Task MigrateFromJsonAsync(AppConfig config)
    {
        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        if (!File.Exists(configPath)) return;

        try
        {
            var json = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath));
            if (json == null) return;

            config.Providers = json.Providers;
            config.ActiveProvider = json.ActiveProvider;
            config.Agent = json.Agent;
            config.UI = json.UI;

            await SaveConfigAsync(config);
        }
        catch (Exception ex) { AppLog.Warn($"Failed to migrate JSON config: {ex.Message}"); }
    }

    public static async Task SaveConfigAsync(AppConfig config)
    {
        _config = config;

        if (_repo == null) return;

        if (config.Providers != null)
        {
            foreach (var kvp in config.Providers)
                await _repo.SaveProviderAsync(kvp.Key, kvp.Value);
        }

        await _repo.SetSettingAsync("ActiveProvider", config.ActiveProvider ?? "ollama");

        if (config.Agent != null)
        {
            await _repo.SetSettingAsync("SystemPrompt", config.Agent.SystemPrompt ?? "");
            await _repo.SetSettingAsync("MaxIterations", config.Agent.MaxIterations.ToString());
            await _repo.SetSettingAsync("MaxContextMessages", config.Agent.MaxContextMessages.ToString());
            await _repo.SetSettingAsync("AutoScroll", config.Agent.AutoScroll.ToString().ToLowerInvariant());
            await _repo.SetSettingAsync("ShowToolResults", config.Agent.ShowToolResults.ToString().ToLowerInvariant());
            await _repo.SetSettingAsync("WorkspaceRoot", config.Agent.WorkspaceRoot ?? "");
        }

        if (config.UI != null)
        {
            await _repo.SetSettingAsync("Theme", config.UI.Theme ?? "dark");
            await _repo.SetSettingAsync("FontFamily", config.UI.FontFamily ?? "Consolas");
            await _repo.SetSettingAsync("FontSize", config.UI.FontSize.ToString());
        }

        ToolSandbox.WorkingDirectory = GetWorkspaceRoot();
    }

    public static string GetWorkspaceRoot()
    {
        var configured = _config?.Agent?.WorkspaceRoot;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return Path.GetFullPath(configured);

        var start = AppDomain.CurrentDomain.BaseDirectory;

        for (var dir = start; dir != null; dir = ParentOf(dir))
            try { if (Directory.GetFiles(dir, "*.sln").Length > 0 || Directory.GetFiles(dir, "*.slnx").Length > 0) return dir; } catch { }

        for (var dir = start; dir != null; dir = ParentOf(dir))
            try { if (Directory.GetFiles(dir, "*.csproj").Length > 0) return dir; } catch { }

        var docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MyAssistant");
        if (Directory.Exists(docs)) return docs;

        return Environment.CurrentDirectory;
    }

    private static string? ParentOf(string dir) =>
        Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));

    public static AppConfig GetConfig() => _config ?? new AppConfig();
    public static SettingsRepository GetRepo() => _repo!;

    public static void ApplyTheme(string theme)
    {
        try
        {
            var app = Current;
            if (app == null) return;
            var dicts = app.Resources.MergedDictionaries;
            dicts.Clear();
            var uri = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase)
                ? new Uri("pack://application:,,,/Themes/LightTheme.xaml")
                : new Uri("pack://application:,,,/Themes/DarkTheme.xaml");
            dicts.Add(new ResourceDictionary { Source = uri });
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to apply theme '{theme}'", ex);
        }
    }

    private static async Task<string> GetSystemPromptAsync()
    {
        var config = GetConfig();
        if (!string.IsNullOrEmpty(config.Agent?.SystemPrompt))
            return config.Agent.SystemPrompt;

        try
        {
            if (_repo != null)
            {
                var prompt = await _repo.GetActiveSystemPromptAsync("");
                if (!string.IsNullOrEmpty(prompt))
                    return prompt;
            }
        }
        catch { }

        return "You are MyAssistant, an AI assistant. You are direct, accurate, and helpful.";
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}

public class AppConfig
{
    public Dictionary<string, ProviderSettings>? Providers { get; set; }
    public string? ActiveProvider { get; set; }
    public AgentSettings? Agent { get; set; }
    public UISettings? UI { get; set; }
}

public class ProviderSettings
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public string DefaultModel { get; set; } = "";
    public string Kind { get; set; } = "OpenAICompatible";
    public double Temperature { get; set; } = 0.7;
    public double TopP { get; set; } = 0.95;
    public long MaxTokens { get; set; } = 8192;
}

public class AgentSettings
{
    public string SystemPrompt { get; set; } = "";
    public int MaxIterations { get; set; } = 20;
    public int MaxContextMessages { get; set; } = 50;
    public int MaxContextTokens { get; set; } = 131072;
    public bool AutoScroll { get; set; } = true;
    public bool ShowToolResults { get; set; } = true;
    public string WorkspaceRoot { get; set; } = "";
}

public class UISettings
{
    public string Theme { get; set; } = "dark";
    public string FontFamily { get; set; } = "Consolas";
    public double FontSize { get; set; } = 13;
}
