using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyAssistant.Main;

public static class TextBoxBehavior
{
    public static readonly DependencyProperty EnterKeyCommandProperty =
        DependencyProperty.RegisterAttached("EnterKeyCommand", typeof(ICommand), typeof(TextBoxBehavior),
            new PropertyMetadata(null, OnEnterKeyCommandChanged));

    public static readonly DependencyProperty ShiftEnterAddsNewLineProperty =
        DependencyProperty.RegisterAttached("ShiftEnterAddsNewLine", typeof(bool), typeof(TextBoxBehavior),
            new PropertyMetadata(true));

    public static ICommand GetEnterKeyCommand(DependencyObject obj) => (ICommand)obj.GetValue(EnterKeyCommandProperty);
    public static void SetEnterKeyCommand(DependencyObject obj, ICommand value) => obj.SetValue(EnterKeyCommandProperty, value);
    public static bool GetShiftEnterAddsNewLine(DependencyObject obj) => (bool)obj.GetValue(ShiftEnterAddsNewLineProperty);
    public static void SetShiftEnterAddsNewLine(DependencyObject obj, bool value) => obj.SetValue(ShiftEnterAddsNewLineProperty, value);

    private static void OnEnterKeyCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBox textBox)
        {
            textBox.PreviewKeyDown -= TextBox_PreviewKeyDown;
            if (e.NewValue != null)
                textBox.PreviewKeyDown += TextBox_PreviewKeyDown;
        }
    }

    private static void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox textBox && e.Key == Key.Enter)
        {
            var shiftEnterAddsNewLine = GetShiftEnterAddsNewLine(textBox);
            var command = GetEnterKeyCommand(textBox);
            if (!shiftEnterAddsNewLine || (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                if (command?.CanExecute(textBox.Text) == true)
                    command.Execute(textBox.Text);
            }
        }
    }
}
