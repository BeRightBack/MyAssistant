using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MyAssistant.Main.Services;

public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly FontFamily MonoFont = new("Consolas, Cascadia Code, Fira Code, Courier New");
    private static readonly FontFamily SansFont = new("Segoe UI, Arial, Helvetica, sans-serif");

    private static readonly Brush CodeBg = new SolidColorBrush(Color.FromRgb(22, 27, 34));
    private static readonly Brush InlineCodeBg = new SolidColorBrush(Color.FromRgb(33, 38, 45));
    private static readonly Brush CodeBorder = new SolidColorBrush(Color.FromRgb(48, 54, 61));
    private static readonly Brush TextColor = new SolidColorBrush(Color.FromRgb(201, 209, 217));
    private static readonly Brush LinkColor = new SolidColorBrush(Color.FromRgb(88, 166, 255));
    private static readonly Brush HeadingColor = new SolidColorBrush(Color.FromRgb(230, 237, 243));
    private static readonly Brush BoldColor = new SolidColorBrush(Color.FromRgb(255, 255, 255));

    private static readonly Brush QuoteTextColor = new SolidColorBrush(Color.FromRgb(158, 167, 179));

    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            Background = Brushes.Transparent,
            Foreground = TextColor,
            FontFamily = SansFont,
            FontSize = 13,
            PagePadding = new Thickness(0),
            LineHeight = 20,
        };

        if (string.IsNullOrWhiteSpace(markdown))
        {
            doc.Blocks.Add(new Paragraph(new Run("") { Foreground = TextColor }));
            return doc;
        }

        var parsed = Markdown.Parse(markdown, Pipeline);
        foreach (var block in parsed)
        {
            var blockElement = RenderBlock(block);
            if (blockElement != null)
                doc.Blocks.Add(blockElement);
        }

        return doc;
    }

    private static System.Windows.Documents.Block? RenderBlock(Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                return RenderHeading(heading);
            case ParagraphBlock paragraph:
                return RenderParagraph(paragraph);
            case FencedCodeBlock fenced:
                return RenderFencedCode(fenced);
            case CodeBlock code:
                return RenderCodeBlock(code);
            case ListBlock list:
                return RenderList(list);
            case QuoteBlock quote:
                return RenderQuote(quote);
            case ThematicBreakBlock:
                return new Paragraph(new Run(new string('─', 50)) { Foreground = CodeBorder, FontSize = 10 });
            default:
                if (block is ContainerBlock container)
                    return RenderContainer(container);
                return null;
        }
    }

    private static System.Windows.Documents.Block RenderHeading(HeadingBlock heading)
    {
        var para = new Paragraph { Margin = new Thickness(0, 8, 0, 4) };
        double fontSize = heading.Level switch { 1 => 20, 2 => 17, 3 => 15, _ => 14 };
        var weight = heading.Level <= 2 ? FontWeights.Bold : FontWeights.SemiBold;

        if (heading.Inline != null)
            foreach (var inline in heading.Inline)
                RenderInline(para.Inlines, inline, fontSize, weight, HeadingColor);

        return para;
    }

    private static System.Windows.Documents.Block RenderParagraph(ParagraphBlock paragraph)
    {
        var para = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        if (paragraph.Inline != null)
            foreach (var inline in paragraph.Inline)
                RenderInline(para.Inlines, inline, 13, FontWeights.Normal, TextColor);
        return para;
    }

    private static System.Windows.Documents.Block RenderCodeBlock(CodeBlock codeBlock)
    {
        string code;
        if (codeBlock is FencedCodeBlock fenced)
            code = string.Join("\n", fenced.Lines);
        else
            code = string.Join("\n", codeBlock.Lines);

        return CreateCodeBlock(code.TrimEnd(), null);
    }

    private static System.Windows.Documents.Block RenderFencedCode(FencedCodeBlock fenced)
    {
        var code = string.Join("\n", fenced.Lines).TrimEnd();
        var lang = fenced.Info?.Trim().ToLowerInvariant();
        return CreateCodeBlock(code, lang);
    }

    private static System.Windows.Documents.Block CreateCodeBlock(string code, string? lang)
    {
        var border = new Border
        {
            Background = CodeBg,
            BorderBrush = CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 4, 0, 8),
        };

        var stack = new StackPanel();

        if (!string.IsNullOrEmpty(lang))
        {
            stack.Children.Add(new TextBlock
            {
                Text = lang,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(125, 133, 144)),
                FontFamily = SansFont,
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
            });
        }

        var codeText = new TextBlock
        {
            Text = code,
            FontFamily = MonoFont,
            FontSize = 12,
            Foreground = TextColor,
            TextWrapping = TextWrapping.NoWrap,
            LineHeight = 18,
        };

        var scroll = new ScrollViewer
        {
            Content = codeText,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 400,
        };

        stack.Children.Add(scroll);
        border.Child = stack;
        return new BlockUIContainer(border);
    }

    private static System.Windows.Documents.Block RenderList(ListBlock list)
    {
        var para = new Paragraph { Margin = new Thickness(16, 0, 0, 8) };
        int index = 1;
        foreach (var item in list)
        {
            if (item is ListItemBlock listItem)
            {
                string bullet = list.IsOrdered ? $"{index}. " : "• ";
                para.Inlines.Add(new Run(bullet) { Foreground = TextColor, FontWeight = FontWeights.Normal, FontSize = 13 });
                foreach (var sub in listItem)
                {
                    if (sub is ParagraphBlock p && p.Inline != null)
                        foreach (var inline in p.Inline)
                            RenderInline(para.Inlines, inline, 13, FontWeights.Normal, TextColor);
                }
                para.Inlines.Add(new LineBreak());
                index++;
            }
        }
        return para;
    }

    private static System.Windows.Documents.Block RenderQuote(QuoteBlock quote)
    {
        var para = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = QuoteTextColor,
            Padding = new Thickness(12, 0, 0, 0),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61)),
            BorderThickness = new Thickness(3, 0, 0, 0),
        };

        foreach (var block in quote)
        {
            if (block is ParagraphBlock p && p.Inline != null)
                foreach (var inline in p.Inline)
                    RenderInline(para.Inlines, inline, 13, FontWeights.Normal, QuoteTextColor);
        }
        return para;
    }

    private static System.Windows.Documents.Block RenderContainer(ContainerBlock container)
    {
        var para = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var block in container)
        {
            if (block is ParagraphBlock p && p.Inline != null)
                foreach (var inline in p.Inline)
                    RenderInline(para.Inlines, inline, 13, FontWeights.Normal, TextColor);
        }
        return para;
    }

    private static void RenderInline(InlineCollection inlines, Markdig.Syntax.Inlines.Inline inline, double fontSize, FontWeight weight, Brush color)
    {
        switch (inline)
        {
            case LiteralInline literal:
                inlines.Add(new Run(literal.Content.ToString()) { FontSize = fontSize, FontWeight = weight, Foreground = color });
                break;

            case CodeInline code:
                inlines.Add(new Run(code.Content)
                {
                    FontFamily = MonoFont,
                    FontSize = fontSize - 1,
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 210, 255)),
                    Background = InlineCodeBg,
                });
                break;

            case EmphasisInline emphasis:
                bool isBold = emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount >= 2;
                var emphasisWeight = isBold ? FontWeights.Bold : FontWeights.Normal;
                var emphasisColor = isBold ? BoldColor : color;
                foreach (var child in emphasis)
                    RenderInline(inlines, child, fontSize, emphasisWeight, emphasisColor);
                break;

            case LinkInline link:
                var linkText = link.FirstChild is LiteralInline lit ? lit.Content.ToString() : link.Url ?? "";
                var linkRun = new Run(linkText)
                {
                    FontSize = fontSize,
                    FontWeight = weight,
                    Foreground = LinkColor,
                    TextDecorations = TextDecorations.Underline,
                };
                inlines.Add(linkRun);
                break;

            case LineBreakInline:
                inlines.Add(new LineBreak());
                break;

            case ContainerInline container:
                foreach (var child in container)
                    RenderInline(inlines, child, fontSize, weight, color);
                break;

            default:
                var text = inline.ToString() ?? "";
                if (!string.IsNullOrEmpty(text))
                    inlines.Add(new Run(text) { FontSize = fontSize, FontWeight = weight, Foreground = color });
                break;
        }
    }
}
