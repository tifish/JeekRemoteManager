using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Markdown.Avalonia;
using Markdown.Avalonia.Parsers;
using Markdown.Avalonia.Plugins;

namespace JeekRemoteManager.Views;

/// <summary>A Markdown viewer whose code parsers create selectable code controls directly.
/// No layout callback or visual-tree replacement is required after rendering.</summary>
public sealed class SelectableMarkdownView : MarkdownScrollViewer
{
    public SelectableMarkdownView()
    {
        var plugins = new MdAvPlugins();
        plugins.Plugins.Add(new SelectableCodeBlockPlugin());
        Plugins = plugins;
        SelectionEnabled = true;
    }

    private sealed class SelectableCodeBlockPlugin : IMdAvPlugin
    {
        public void Setup(SetupInfo info)
        {
            info.Register(new FencedCodeBlockOverride());
            info.Register(new IndentedCodeBlockOverride());
        }
    }

    private sealed class FencedCodeBlockOverride : IBlockOverride
    {
        public string ParserName => "CodeBlocksWithLangEvaluator";

        public IEnumerable<Control>? Convert(string text, Match firstMatch, ParseStatus status,
            IMarkdownEngine engine, out int parseTextBegin, out int parseTextEnd)
        {
            parseTextBegin = firstMatch.Index;
            var marker = firstMatch.Groups[1].Value;
            if (marker.Length < 3)
            {
                parseTextEnd = -1;
                return null;
            }

            var markerPattern = Regex.Escape(marker[0].ToString()) + $"{{{marker.Length},}}";
            var codeStart = firstMatch.Index + firstMatch.Length;
            var closingFencePattern = new Regex(
                $@"\r?\n[ ]*{markerPattern}[ ]*(?:\r?\n|\z)",
                RegexOptions.None,
                TimeSpan.FromSeconds(1));
            var closingFence = closingFencePattern.Match(text, codeStart);
            if (!closingFence.Success)
            {
                parseTextEnd = -1;
                return null;
            }

            var code = text.Substring(codeStart, closingFence.Index - codeStart);
            parseTextEnd = closingFence.Index + closingFence.Length;
            return [CreateCodeBlock(code)];
        }
    }

    private sealed class IndentedCodeBlockOverride : IBlockOverride
    {
        public string ParserName => "CodeBlocksWithoutLangEvaluator";

        public IEnumerable<Control> Convert(string text, Match firstMatch, ParseStatus status,
            IMarkdownEngine engine, out int parseTextBegin, out int parseTextEnd)
        {
            parseTextBegin = firstMatch.Index;
            parseTextEnd = firstMatch.Index + firstMatch.Length;
            var code = string.Join("\n", firstMatch.Groups[1].Value
                .Trim('\r', '\n')
                .Split('\n')
                .Select(line =>
                {
                    var value = line.TrimEnd('\r');
                    return value.StartsWith("    ", StringComparison.Ordinal) ? value[4..] : value;
                }));
            return [CreateCodeBlock(code)];
        }
    }

    private static Border CreateCodeBlock(string code)
    {
        var selectable = new SelectableTextBlock
        {
            Name = "SelectableCodeBlock",
            Text = code,
            TextWrapping = TextWrapping.NoWrap,
        };
        selectable.Classes.Add("CodeBlock");

        // AllowAutoHide=false makes Fluent put the bar in its own grid row instead of
        // overlaying content. With Auto visibility, short blocks still take no extra
        // height; long lines grow by the scrollbar without covering the last text row.
        var scroll = new ScrollViewer
        {
            Content = selectable,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AllowAutoHide = false,
        };
        scroll.Classes.Add("CodeBlock");

        var border = new Border { Child = scroll };
        border.Classes.Add("CodeBlock");
        return border;
    }
}
