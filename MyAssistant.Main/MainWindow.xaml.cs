using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Extensions.DependencyInjection;
using MyAssistant.Main.ViewModels;
using MyAssistant.Main.Models;

namespace MyAssistant.Main;

public partial class MainWindow : Window
{
    private ScrollViewer? _chatScrollViewer;

    public MainWindow() : this(App.Services.GetRequiredService<MainWindowViewModel>()) { }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.ScrollToBottomRequested += OnScrollToBottom;
        viewModel.ThemeChanged += OnThemeChanged;
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += (s, e) => AppLog.Info("MainWindow closing requested");
        Closed += (s, e) => AppLog.Info("MainWindow closed");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _chatScrollViewer = FindName("ChatScrollViewer") as ScrollViewer;
        CreateGearIcon();
        if (DataContext is MainWindowViewModel vm)
        {
            vm.RefreshProviderInfo();
            if (ProviderSelector != null)
            {
                ProviderSelector.ItemsSource = vm.ProviderNames;
                ProviderSelector.SelectedItem = vm.ActiveProviderName;
            }
            if (ModelSelector != null)
            {
                ModelSelector.ItemsSource = vm.AvailableModels;
                ModelSelector.SelectedItem = vm.SelectedModel;
            }
            var config = App.GetConfig();
            if (ThemeLabel != null)
                ThemeLabel.Text = string.Equals(config.UI?.Theme, "light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        }
        InputTextBox?.Focus();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        InputTextBox?.Focus();
    }

    private void OnScrollToBottom()
    {
        if (_chatScrollViewer != null)
            _chatScrollViewer.ScrollToEnd();
    }

    private void OnThemeChanged(string theme)
    {
        Dispatcher.Invoke(() =>
        {
            if (ThemeLabel != null)
                ThemeLabel.Text = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        });
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (e.Key == Key.Escape)
        {
            if (DataContext is MainWindowViewModel vm && vm.ShowWelcome)
            {
                vm.ShowWelcome = false;
                e.Handled = true;
            }
        }
        else if (ctrl && e.Key == Key.N)
        {
            e.Handled = true;
            if (DataContext is MainWindowViewModel vm)
                vm.NewSessionCommand.Execute(null);
        }
        else if (ctrl && e.Key == Key.W)
        {
            e.Handled = true;
            if (DataContext is MainWindowViewModel vm && !string.IsNullOrEmpty(vm.Sessions.FirstOrDefault(s => s.IsSelected)?.Id))
            {
                var sel = vm.Sessions.FirstOrDefault(s => s.IsSelected);
                if (sel != null)
                {
                    var result = MessageBox.Show("Delete this chat?", "Confirm Delete",
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                        vm.DeleteSession(sel.Id);
                }
            }
        }
        else if (ctrl && e.Key == Key.F)
        {
            e.Handled = true;
            InputTextBox?.Focus();
            if (DataContext is MainWindowViewModel vm)
                vm.SearchText = vm.SearchText;
        }
    }

    private void NewSession_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.NewSessionCommand.Execute(null);
    }

    private void SessionItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SessionViewModel session && DataContext is MainWindowViewModel vm)
            vm.SwitchSession(session.Id);
    }

    private void DeleteSession_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is string sessionId && DataContext is MainWindowViewModel vm)
        {
            var result = MessageBox.Show("Delete this chat?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
                vm.DeleteSession(sessionId);
        }
    }

    private void CopyMessage_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is string text)
            Clipboard.SetText(text);
    }

    private void ToggleThinking_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessageViewModel msg)
            msg.IsThinkingExpanded = !msg.IsThinkingExpanded;
    }

    private void EditMessage_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessageViewModel msg)
        {
            msg.EditText = msg.Content;
            msg.IsEditing = true;
        }
    }

    private void AcceptEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessageViewModel msg && DataContext is MainWindowViewModel vm)
        {
            msg.IsEditing = false;
            if (!string.IsNullOrWhiteSpace(msg.EditText))
                _ = vm.EditAndResendAsync(msg, msg.EditText);
        }
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessageViewModel msg)
        {
            msg.IsEditing = false;
            msg.EditText = "";
        }
    }

    private void EditTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ChatMessageViewModel msg && DataContext is MainWindowViewModel vm)
        {
            if (e.Key == Key.Escape)
            {
                msg.IsEditing = false;
                msg.EditText = "";
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                msg.IsEditing = false;
                if (!string.IsNullOrWhiteSpace(msg.EditText))
                    _ = vm.EditAndResendAsync(msg, msg.EditText);
                e.Handled = true;
            }
        }
    }

    private void EditTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.Focus();
    }

    private void RegenerateMessage_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessageViewModel msg && DataContext is MainWindowViewModel vm)
            _ = vm.RegenerateAsync(msg);
    }

    private void RenameSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string sessionId && DataContext is MainWindowViewModel vm)
        {
            var session = vm.Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (session == null) return;

            var dialog = new Window
            {
                Title = "Rename Session",
                Width = 350, Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(Color.FromRgb(13, 17, 23)),
                ResizeMode = ResizeMode.NoResize
            };

            var stack = new StackPanel { Margin = new Thickness(16) };
            var label = new TextBlock
            {
                Text = "New name:",
                Foreground = new SolidColorBrush(Color.FromRgb(158, 167, 179)),
                FontSize = 12, Margin = new Thickness(0, 0, 0, 8)
            };
            var textBox = new TextBox
            {
                Text = session.Title, FontSize = 13, Padding = new Thickness(6, 4, 6, 4),
                Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
                Foreground = new SolidColorBrush(Color.FromRgb(201, 209, 217)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(33, 38, 45)),
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12)
            };
            var okBtn = new Button
            {
                Content = "Rename", Width = 80, Height = 28, HorizontalAlignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Color.FromRgb(35, 134, 54)),
                Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand
            };
            okBtn.Click += async (s, ev) =>
            {
                var newName = textBox.Text.Trim();
                if (!string.IsNullOrEmpty(newName))
                {
                    await vm.RenameSession(sessionId, newName);
                    // The dialog may have been closed while the rename was awaited
                    if (dialog.IsLoaded)
                        dialog.DialogResult = true;
                }
            };
            textBox.KeyDown += (s, ev) =>
            {
                if (ev.Key == Key.Enter) okBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            };

            stack.Children.Add(label);
            stack.Children.Add(textBox);
            stack.Children.Add(okBtn);
            dialog.Content = stack;
            dialog.ShowDialog();
        }
    }

    private void DeleteSessionMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string sessionId && DataContext is MainWindowViewModel vm)
        {
            var result = MessageBox.Show("Delete this chat?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
                vm.DeleteSession(sessionId);
        }
    }

    private void Send_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.CanSend)
            vm.SendCommand.Execute(null);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow { Owner = this };
        if (win.ShowDialog() == true)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.RefreshProviderInfo();
        }
    }

    private void StatusBarSettings_Click(object sender, MouseButtonEventArgs e) => Settings_Click(sender, e);

    private void ClearError_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ClearErrorCommand.Execute(null);
    }

    private void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && ModelSelector?.SelectedItem is string model)
            vm.OnModelChanged(model);
    }

    private void RefreshModels_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.RefreshModels();
    }

    private void ProviderSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && ProviderSelector?.SelectedItem is string selected)
        {
            if (selected != vm.ActiveProviderName)
            {
                var config = App.GetConfig();
                config.ActiveProvider = selected;
                _ = App.SaveConfigAsync(config);
                vm.OnProviderSwitched();
            }
        }
    }

    private void ExportChat_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (DataContext is MainWindowViewModel vm)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*",
                DefaultExt = ".md",
                FileName = $"{vm.SessionTitle.ReplaceInvalidChars("_")}.md"
            };
            if (dialog.ShowDialog() == true)
            {
                var md = vm.ExportChatAsMarkdown();
                File.WriteAllText(dialog.FileName, md);
                MessageBox.Show($"Exported to {dialog.FileName}", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private void ThemeToggle_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (DataContext is MainWindowViewModel vm)
        {
            var config = App.GetConfig();
            var current = config.UI?.Theme ?? "dark";
            var newTheme = string.Equals(current, "dark", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
            vm.OnThemeChanged(newTheme);
        }
    }

    private void CreateGearIcon()
    {
        var canvas = new Canvas { Width = 20, Height = 20 };

        int teeth = 8;
        double outerR = 9, innerR = 6.5, cx = 10, cy = 10;
        var gearBrush = new SolidColorBrush(Color.FromRgb(158, 167, 179));

        var gearPath = new System.Windows.Shapes.Path { Fill = gearBrush };
        var fig = new PathFigure { StartPoint = new Point(cx + innerR, cy), IsClosed = true };

        for (int i = 0; i < teeth; i++)
        {
            double a0 = i * 2 * Math.PI / teeth;
            double a1 = a0 + 0.15 * 2 * Math.PI / teeth;
            double a2 = a0 + 0.45 * 2 * Math.PI / teeth;
            double a3 = a0 + 0.55 * 2 * Math.PI / teeth;
            double a4 = a0 + 0.85 * 2 * Math.PI / teeth;

            fig.Segments.Add(new LineSegment(new Point(cx + innerR * Math.Cos(a1), cy + innerR * Math.Sin(a1)), true));
            fig.Segments.Add(new LineSegment(new Point(cx + outerR * Math.Cos(a2), cy + outerR * Math.Sin(a2)), true));
            fig.Segments.Add(new LineSegment(new Point(cx + outerR * Math.Cos(a3), cy + outerR * Math.Sin(a3)), true));
            fig.Segments.Add(new LineSegment(new Point(cx + innerR * Math.Cos(a4), cy + innerR * Math.Sin(a4)), true));
        }

        var pg = new PathGeometry();
        pg.Figures.Add(fig);
        gearPath.Data = pg;
        Canvas.SetLeft(gearPath, 0);
        Canvas.SetTop(gearPath, 0);
        canvas.Children.Add(gearPath);

        var hole = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromRgb(13, 17, 23)) };
        Canvas.SetLeft(hole, 7.5);
        Canvas.SetTop(hole, 7.5);
        canvas.Children.Add(hole);

        SettingsButton.Content = canvas;
    }
}

public static class StringExtensions
{
    public static string ReplaceInvalidChars(this string s, string replacement)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            s = s.Replace(c, replacement[0]);
        return s;
    }
}
