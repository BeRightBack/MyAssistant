using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Documents;
using MyAssistant.Main.Services;

namespace MyAssistant.Main.Services;

public class MarkdownToFlowDocumentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string text && !string.IsNullOrEmpty(text))
            return MarkdownRenderer.Render(text);
        return MarkdownRenderer.Render(value?.ToString() ?? "");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
