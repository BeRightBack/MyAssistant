using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using MyAssistant.Abstractions.Agent;
using MyAssistant.Main.Models;

namespace MyAssistant.Main;

public partial class SettingsWindow : Window
{
    private AppConfig _config;
    private List<string> _availableModels = new();
    private List<ToolViewModel> _toolViewModels = new();

    public SettingsWindow()
    {
        InitializeComponent();
        _config = App.GetConfig();
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        await LoadValuesAsync();
        await LoadToolsAsync();
        await LoadRolesAsync();
        LoadDbSettings();
    }

    private async Task LoadValuesAsync()
    {
        var activeProvider = _config.ActiveProvider ?? "ollama";
        ActiveProviderCombo.Items.Clear();
        if (_config.Providers != null)
        {
            foreach (var kvp in _config.Providers)
                ActiveProviderCombo.Items.Add(kvp.Key);
            ActiveProviderCombo.SelectedItem = activeProvider;
            ActiveProviderCombo.SelectionChanged += (s, e) => LoadProviderSettings();
            LoadProviderSettings();
        }
    }

    private async Task LoadToolsAsync()
    {
        var toolDescriptions = new Dictionary<string, string>
        {
            ["read_file"] = "Read file contents with line numbers",
            ["write_file"] = "Create or overwrite files",
            ["edit_file"] = "Find and replace text in files",
            ["glob"] = "Search files by pattern",
            ["grep"] = "Search file contents with regex",
            ["bash"] = "Execute shell commands",
            ["web_fetch"] = "Fetch and read web pages",
            ["web_search"] = "Search the web for information",
            ["list_directory"] = "List files in a directory",
            ["task_agent"] = "Spawn a sub-agent for parallel tasks",
            ["store_knowledge"] = "Store important information for future reference",
            ["search_knowledge"] = "Search stored knowledge for relevant context",
            ["manage_knowledge"] = "List or delete stored knowledge entries"
        };
        var toolStates = await App.GetRepo().GetToolStatesAsync();
        _toolViewModels.Clear();
        foreach (var kvp in toolDescriptions)
        {
            var enabled = toolStates.TryGetValue(kvp.Key, out var e) ? e : true;
            _toolViewModels.Add(new ToolViewModel { Name = kvp.Key, Description = kvp.Value, Enabled = enabled });
        }
        ToolsList.ItemsSource = _toolViewModels;
    }

    private void LoadProviderSettings()
    {
        if (ActiveProviderCombo.SelectedItem is not string selected) return;
        if (_config.Providers == null || !_config.Providers.TryGetValue(selected, out var ps)) return;
        ProviderName.Text = ps.Name;
        BaseUrl.Text = ps.BaseUrl;
        ApiKey.Text = ps.ApiKey ?? "";
        DefaultModel.Text = ps.DefaultModel;
        Temperature.Text = ps.Temperature.ToString("F2");
        TopP.Text = ps.TopP.ToString("F2");
        MaxTokens.Text = ps.MaxTokens.ToString();
        var kind = App.DetectProviderKind(ps.BaseUrl, ps.Kind);
        DetectedKind.Text = $"Detected: {kind}";
        ProviderKindLabel.Text = kind.ToString();
        MaxIterations.Text = (_config.Agent?.MaxIterations ?? 20).ToString();
        MaxContextMessages.Text = (_config.Agent?.MaxContextMessages ?? 50).ToString();
        WorkspaceRootBox.Text = _config.Agent?.WorkspaceRoot ?? "";
        FontSizeBox.Text = (_config.UI?.FontSize ?? 13).ToString("F0");
        FontFamilyBox.Text = _config.UI?.FontFamily ?? "Consolas";
        _ = FetchModelsAsync(ps.BaseUrl, ps.DefaultModel);
    }

    private void BaseUrl_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var kind = App.DetectProviderKind(BaseUrl.Text, null);
        DetectedKind.Text = $"Detected: {kind}";
        ProviderKindLabel.Text = kind.ToString();
    }

    private async Task FetchModelsAsync(string baseUrl, string currentModel)
    {
        ConnectionStatus.Text = "Fetching models...";
        ConnectionStatus.Foreground = System.Windows.Media.Brushes.Gray;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MyAssistant/1.0");
            var kind = App.DetectProviderKind(baseUrl ?? "", ProviderKindLabel?.Text);
            if (kind == MyAssistant.Abstractions.Agent.ProviderKind.Ollama)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/tags");
                    if (!string.IsNullOrEmpty(ApiKey.Text))
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey.Text);
                    var resp = await http.SendAsync(request);
                    if (resp.IsSuccessStatusCode)
                    {
                        var tags = await resp.Content.ReadFromJsonAsync<OllamaModelsResponse>();
                        _availableModels = tags?.Models?.Select(m => m.Name).ToList() ?? new();
                    }
                }
                catch
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");
                    if (!string.IsNullOrEmpty(ApiKey.Text))
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey.Text);
                    var resp = await http.SendAsync(request);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                        if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                            _availableModels = data.EnumerateArray().Where(m => m.TryGetProperty("id", out _)).Select(m => m.GetProperty("id").GetString() ?? "").Where(id => !string.IsNullOrEmpty(id)).ToList();
                    }
                }
            }
            else if (kind == MyAssistant.Abstractions.Agent.ProviderKind.Gemini)
            {
                // Gemini OpenAI-compatible endpoint
                var modelsUrl = "https://generativelanguage.googleapis.com/v1beta/openai/models";
                using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                if (!string.IsNullOrEmpty(ApiKey.Text))
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey.Text);
                var resp = await http.SendAsync(request);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                    if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        _availableModels = data.EnumerateArray().Where(m => m.TryGetProperty("id", out _)).Select(m => m.GetProperty("id").GetString() ?? "").Where(id => !string.IsNullOrEmpty(id)).ToList();
                }
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");
                if (!string.IsNullOrEmpty(ApiKey.Text))
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey.Text);
                var resp = await http.SendAsync(request);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                    if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        _availableModels = data.EnumerateArray().Where(m => m.TryGetProperty("id", out _)).Select(m => m.GetProperty("id").GetString() ?? "").Where(id => !string.IsNullOrEmpty(id)).ToList();
                }
                else
                {
                    var errBody = await resp.Content.ReadAsStringAsync();
                    ConnectionStatus.Text = $"Models endpoint returned {(int)resp.StatusCode}: {errBody[..Math.Min(200, errBody.Length)]}";
                    ConnectionStatus.Foreground = System.Windows.Media.Brushes.Orange;
                }
            }
            ModelCombo.Items.Clear();
            foreach (var m in _availableModels) ModelCombo.Items.Add(m);
            if (!string.IsNullOrEmpty(currentModel))
            {
                ModelCombo.SelectedItem = currentModel;
                if (ModelCombo.SelectedIndex < 0 && _availableModels.Count > 0) ModelCombo.Items.Add($"(custom) {currentModel}");
            }
            else if (_availableModels.Count > 0) ModelCombo.SelectedItem = _availableModels[0];
            ConnectionStatus.Text = _availableModels.Count > 0 ? $"Found {_availableModels.Count} models" : "No models found";
            ConnectionStatus.Foreground = _availableModels.Count > 0 ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Orange;
        }
        catch (Exception ex)
        {
            ConnectionStatus.Text = $"Could not fetch models: {ex.Message}";
            ConnectionStatus.Foreground = System.Windows.Media.Brushes.Orange;
            ModelCombo.Items.Clear();
            if (!string.IsNullOrEmpty(currentModel)) ModelCombo.Items.Add(currentModel);
        }
    }

    private void ModelCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ModelCombo.SelectedItem is string selected) DefaultModel.Text = selected;
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await FetchModelsAsync(BaseUrl.Text, DefaultModel.Text);

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var baseUrl = BaseUrl.Text?.TrimEnd('/');
        var model = DefaultModel.Text;
        var apiKey = ApiKey.Text;
        var testBtn = (System.Windows.Controls.Button)sender;
        var originalContent = testBtn.Content;
        testBtn.Content = "Testing..."; testBtn.IsEnabled = false;
        ConnectionStatus.Text = "Testing connection...";
        ConnectionStatus.Foreground = System.Windows.Media.Brushes.Gray;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MyAssistant/1.0");
            var kind = App.DetectProviderKind(baseUrl ?? "", null);
            if (kind == MyAssistant.Abstractions.Agent.ProviderKind.Ollama)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/tags");
                if (!string.IsNullOrEmpty(apiKey))
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                var response = await http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var tags = await response.Content.ReadFromJsonAsync<OllamaModelsResponse>();
                    var models = tags?.Models?.Select(m => m.Name).ToList() ?? [];
                    ConnectionStatus.Text = models.Count > 0 ? $"Connected! Models: {string.Join(", ", models.Take(5))}" : "Connected but no models. Pull one first: ollama pull llama3.2:3b";
                    ConnectionStatus.Foreground = models.Count > 0 ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Orange;
                }
                else { ConnectionStatus.Text = $"Failed: {(int)response.StatusCode}"; ConnectionStatus.Foreground = System.Windows.Media.Brushes.OrangeRed; }
            }
            else if (kind == MyAssistant.Abstractions.Agent.ProviderKind.Gemini)
            {
                // Test Gemini via OpenAI-compatible models endpoint
                var modelsUrl = "https://generativelanguage.googleapis.com/v1beta/openai/models";
                using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                if (!string.IsNullOrEmpty(apiKey))
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                var resp = await http.SendAsync(request);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                    var count = json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data.GetArrayLength() : 0;
                    ConnectionStatus.Text = count > 0 ? $"Connected! {count} models available" : "Connected but no models found";
                    ConnectionStatus.Foreground = count > 0 ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Orange;
                }
                else { var body = await resp.Content.ReadAsStringAsync(); ConnectionStatus.Text = $"Failed: {(int)resp.StatusCode} - {body[..Math.Min(200, body.Length)]}"; ConnectionStatus.Foreground = System.Windows.Media.Brushes.OrangeRed; }
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
                request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { model, messages = new[] { new { role = "user", content = "Say hi" } }, max_tokens = 5 }), System.Text.Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                var resp = await http.SendAsync(request);
                if (resp.IsSuccessStatusCode) { ConnectionStatus.Text = "Connected successfully!"; ConnectionStatus.Foreground = System.Windows.Media.Brushes.LightGreen; }
                else { var body = await resp.Content.ReadAsStringAsync(); ConnectionStatus.Text = $"Failed: {(int)resp.StatusCode} - {body[..Math.Min(200, body.Length)]}"; ConnectionStatus.Foreground = System.Windows.Media.Brushes.OrangeRed; }
            }
        }
        catch (Exception ex) { ConnectionStatus.Text = $"Error: {ex.Message}"; ConnectionStatus.Foreground = System.Windows.Media.Brushes.OrangeRed; }
        finally { testBtn.Content = originalContent; testBtn.IsEnabled = true; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveProviderCombo.SelectedItem is string selected)
            _config.ActiveProvider = selected;
        if (_config.Providers == null) _config.Providers = new Dictionary<string, ProviderSettings>();
        if (!string.IsNullOrEmpty(ActiveProviderCombo.SelectedItem as string))
        {
            var key = (string)ActiveProviderCombo.SelectedItem;
            var ps = _config.Providers.GetValueOrDefault(key) ?? new ProviderSettings();
            ps.Name = ProviderName.Text;
            ps.BaseUrl = BaseUrl.Text;
            ps.ApiKey = ApiKey.Text;
            ps.DefaultModel = DefaultModel.Text;
            ps.Kind = ProviderKindLabel.Text;
            ps.Temperature = double.TryParse(Temperature.Text, out var t) ? t : 0.7;
            ps.TopP = double.TryParse(TopP.Text, out var tp) ? tp : 0.95;
            ps.MaxTokens = long.TryParse(MaxTokens.Text, out var mt) ? mt : 8192;
            _config.Providers[key] = ps;
        }
        _config.Agent = new AgentSettings
        {
            MaxIterations = int.TryParse(MaxIterations.Text, out var mi) ? mi : 20,
            MaxContextMessages = int.TryParse(MaxContextMessages.Text, out var mcm) ? mcm : 50,
            AutoScroll = true,
            ShowToolResults = true,
            WorkspaceRoot = string.IsNullOrWhiteSpace(WorkspaceRootBox.Text) ? "" : WorkspaceRootBox.Text.Trim()
        };
        _config.UI = new UISettings
        {
            Theme = _config.UI?.Theme ?? "dark",
            FontFamily = string.IsNullOrWhiteSpace(FontFamilyBox.Text) ? "Consolas" : FontFamilyBox.Text,
            FontSize = double.TryParse(FontSizeBox.Text, out var fs) ? fs : 13
        };
        await App.SaveConfigAsync(_config);
        foreach (var tool in _toolViewModels)
            await App.GetRepo().SetToolEnabledAsync(tool.Name, tool.Enabled);
        SaveDbSettings();
        DialogResult = true;
        Close();
    }

    private void SaveDbSettings()
    {
        try
        {
            var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var host = string.IsNullOrWhiteSpace(DbHost.Text) ? "localhost" : DbHost.Text.Trim();
            var port = int.TryParse(DbPort.Text, out var p) ? p : 3306;
            var name = string.IsNullOrWhiteSpace(DbName.Text) ? "mycoderdb" : DbName.Text.Trim();
            var user = DbUser.Text?.Trim() ?? "";
            var pass = DbPassword.Password ?? "";
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                Database = new { Host = host, Port = port, Name = name, User = user, Password = pass, SslMode = "None", AllowPublicKeyRetrieval = true }
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(path, json);
            DbConfig.InvalidateCache();
        }
        catch { }
    }

    private List<RoleEntry> _roles = new();

    private async Task LoadRolesAsync()
    {
        var repo = new SettingsRepository();
        _roles = await repo.GetAllRolesAsync();
        RoleCombo.Items.Clear();
        foreach (var role in _roles)
            RoleCombo.Items.Add(role.Name);
        var active = _roles.FirstOrDefault(r => r.IsActive);
        if (active != null)
            RoleCombo.SelectedItem = active.Name;
        RoleCombo.SelectionChanged += RoleCombo_SelectionChanged;
        LoadSelectedRole();
    }

    private void LoadSelectedRole()
    {
        var selected = RoleCombo.SelectedItem?.ToString();
        var role = _roles.FirstOrDefault(r => r.Name == selected);
        if (role == null) return;
        RoleName.Text = role.Name;
        RoleDescription.Text = role.Description;
        RoleSystemPrompt.Text = role.SystemPrompt;
    }

    private void RoleCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        LoadSelectedRole();
    }

    private async void SaveRole_Click(object sender, RoutedEventArgs e)
    {
        var selected = RoleCombo.SelectedItem?.ToString();
        var role = _roles.FirstOrDefault(r => r.Name == selected);
        if (role == null) return;
        role.Name = RoleName.Text;
        role.Description = RoleDescription.Text;
        role.SystemPrompt = RoleSystemPrompt.Text;
        var repo = new SettingsRepository();
        await repo.SaveRoleAsync(role);
        await repo.SetActiveRoleAsync(role.Id);
        // Reload system prompt in the active agent
        try
        {
            var agent = App.Services?.GetService<IAgent>();
            if (agent != null)
            {
                var newPrompt = await repo.GetActiveSystemPromptAsync("");
                agent.UpdateSystemPrompt(newPrompt);
            }
        }
        catch { }
        RoleStatus.Text = "Role saved and activated.";
    }

    private async void DeleteRole_Click(object sender, RoutedEventArgs e)
    {
        var selected = RoleCombo.SelectedItem?.ToString();
        var role = _roles.FirstOrDefault(r => r.Name == selected);
        if (role == null) return;
        var repo = new SettingsRepository();
        await repo.DeleteRoleAsync(role.Id);
        RoleStatus.Text = "Role deleted.";
        await LoadRolesAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void BrowseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select workspace root"
        };
        if (dialog.ShowDialog() == true)
            WorkspaceRootBox.Text = dialog.FolderName;
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Add Provider",
            Width = 400,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = System.Windows.Media.Brushes.Black
        };
        var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Provider name (e.g. openrouter):",
            Foreground = System.Windows.Media.Brushes.LightGray,
            Margin = new System.Windows.Thickness(0, 0, 0, 8)
        });
        var textBox = new System.Windows.Controls.TextBox
        {
            Text = "openrouter",
            Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF0D1117")),
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF30363D")),
            Padding = new System.Windows.Thickness(6, 4, 6, 4),
            Margin = new System.Windows.Thickness(0, 0, 0, 12)
        };
        stack.Children.Add(textBox);
        var btnPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        btnPanel.HorizontalAlignment = HorizontalAlignment.Right;
        var okBtn = new System.Windows.Controls.Button
        {
            Content = "OK",
            Width = 80,
            Height = 28,
            Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF238636")),
            Foreground = System.Windows.Media.Brushes.White,
            Margin = new System.Windows.Thickness(0, 0, 8, 0)
        };
        okBtn.Click += (s, ev) => { dialog.DialogResult = true; };
        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Width = 80,
            Height = 28,
            Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF21262D")),
            Foreground = System.Windows.Media.Brushes.LightGray
        };
        cancelBtn.Click += (s, ev) => { dialog.DialogResult = false; };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        stack.Children.Add(btnPanel);
        dialog.Content = stack;
        textBox.Focus();

        if (dialog.ShowDialog() != true) return;
        var name = textBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        var key = name.ToLowerInvariant().Replace(" ", "-");
        if (_config.Providers == null) _config.Providers = new Dictionary<string, ProviderSettings>();
        if (_config.Providers.ContainsKey(key))
        {
            MessageBox.Show("A provider with that name already exists.", "Add Provider");
            return;
        }
        _config.Providers[key] = new ProviderSettings
        {
            Name = name,
            BaseUrl = "",
            DefaultModel = "",
            Kind = "OpenAICompatible",
            Temperature = 0.7,
            TopP = 0.95,
            MaxTokens = 8192
        };
        ActiveProviderCombo.Items.Add(key);
        ActiveProviderCombo.SelectedItem = key;
    }

    private void DeleteProvider_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveProviderCombo.SelectedItem is not string selected) return;
        if (_config.Providers == null || !_config.Providers.ContainsKey(selected)) return;
        if (MessageBox.Show($"Delete provider '{selected}'?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _config.Providers.Remove(selected);
        ActiveProviderCombo.Items.Remove(selected);
        if (ActiveProviderCombo.Items.Count > 0)
            ActiveProviderCombo.SelectedIndex = 0;
    }

    private void LoadDbSettings()
    {
        try
        {
            var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            if (!System.IO.File.Exists(path)) return;
            var json = System.IO.File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Database", out var db))
            {
                if (db.TryGetProperty("Host", out var h)) DbHost.Text = h.GetString() ?? "";
                if (db.TryGetProperty("Port", out var p)) DbPort.Text = p.GetInt32().ToString();
                if (db.TryGetProperty("Name", out var n)) DbName.Text = n.GetString() ?? "";
                if (db.TryGetProperty("User", out var u)) DbUser.Text = u.GetString() ?? "";
                if (db.TryGetProperty("Password", out var pw)) DbPassword.Password = pw.GetString() ?? "";
            }
        }
        catch { }
    }

    private async void TestDbConnection_Click(object sender, RoutedEventArgs e)
    {
        var testBtn = (System.Windows.Controls.Button)sender;
        var originalContent = testBtn.Content;
        testBtn.Content = "Testing..."; testBtn.IsEnabled = false;
        DbConnectionStatus.Text = "Testing connection...";
        DbConnectionStatus.Foreground = System.Windows.Media.Brushes.Gray;
        try
        {
            var host = string.IsNullOrWhiteSpace(DbHost.Text) ? "localhost" : DbHost.Text.Trim();
            var port = string.IsNullOrWhiteSpace(DbPort.Text) ? "3306" : DbPort.Text.Trim();
            var name = string.IsNullOrWhiteSpace(DbName.Text) ? "mycoderdb" : DbName.Text.Trim();
            var user = DbUser.Text?.Trim() ?? "";
            var pass = DbPassword.Password ?? "";
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                DbConnectionStatus.Text = "Username and password are required";
                DbConnectionStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
                return;
            }
            var connStr = $"Server={host};Port={port};Database={name};User={user};Password={pass};SslMode=None;AllowPublicKeyRetrieval=true;ConnectionTimeout=5;";
            using var conn = new MySqlConnector.MySqlConnection(connStr);
            await conn.OpenAsync();
            DbConnectionStatus.Text = "Connected successfully!";
            DbConnectionStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        catch (Exception ex)
        {
            DbConnectionStatus.Text = $"Failed: {ex.Message}";
            DbConnectionStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
        }
        finally { testBtn.Content = originalContent; testBtn.IsEnabled = true; }
    }
}

public class ToolViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private bool _enabled;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get => _enabled; set { _enabled = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Enabled))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
