using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MyAssistant.Abstractions.Agent;

namespace MyAssistant.Main.Models;

public class ChatMessageViewModel : INotifyPropertyChanged
{
    private string _role = "";
    private string _roleName = "";
    private string _content = "";
    private string _thinking = "";
    private DateTime _timestamp = DateTime.Now;
    private string? _usage;
    private bool _isEditing;
    private string _editText = "";
    private bool _isThinkingExpanded;

    public string Role { get => _role; set { _role = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsUser)); OnPropertyChanged(nameof(IsAssistant)); } }
    public string RoleName { get => _roleName; set { _roleName = value; OnPropertyChanged(); } }
    public string Content { get => _content; set { _content = value; OnPropertyChanged(); } }
    public string Thinking { get => _thinking; set { _thinking = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasThinking)); } }
    public bool HasThinking => !string.IsNullOrWhiteSpace(_thinking);
    public bool IsThinkingExpanded { get => _isThinkingExpanded; set { _isThinkingExpanded = value; OnPropertyChanged(); } }
    public DateTime Timestamp { get => _timestamp; set { _timestamp = value; OnPropertyChanged(); } }
    public ObservableCollection<ToolCallViewModel> ToolCalls { get; } = new();
    public string? Usage { get => _usage; set { _usage = value; OnPropertyChanged(); } }
    public bool IsEditing { get => _isEditing; set { _isEditing = value; OnPropertyChanged(); } }
    public string EditText { get => _editText; set { _editText = value; OnPropertyChanged(); } }
    public bool IsUser => Role == "user" || Role == "User";
    public bool IsAssistant => Role == "assistant" || Role == "Assistant";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class ToolCallViewModel : INotifyPropertyChanged
{
    private string _id = "";
    private string _name = "";
    private string _statusText = "Pending";
    private string _argsPreview = "";
    private Color _statusColor = Colors.Gray;
    private string _icon = "○";

    public string Id { get => _id; set { _id = value; OnPropertyChanged(); } }
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }
    public string ArgsPreview { get => _argsPreview; set { _argsPreview = value; OnPropertyChanged(); } }
    public Color StatusColor { get => _statusColor; set { _statusColor = value; OnPropertyChanged(); } }
    public string Icon { get => _icon; set { _icon = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class SessionViewModel : INotifyPropertyChanged
{
    private string _id = "";
    private string _title = "";
    private bool _isSelected;
    private string _timeAgo = "";

    public string Id { get => _id; set { _id = value; OnPropertyChanged(); } }
    public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }
    public string TimeAgo { get => _timeAgo; set { _timeAgo = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;
    public RelayCommand(Action execute, Func<bool>? canExecute = null) { _execute = execute; _canExecute = canExecute; }
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
