using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static readonly SolidColorBrush JsonBrushDefault = FreezeBrush(184, 184, 184);
        private static readonly SolidColorBrush JsonBrushPunct = FreezeBrush(132, 132, 132);
        private static readonly SolidColorBrush JsonBrushString = FreezeBrush(206, 145, 120);
        private static readonly SolidColorBrush JsonBrushNumber = FreezeBrush(181, 206, 168);
        private static readonly SolidColorBrush JsonBrushKeyword = FreezeBrush(86, 156, 214);
        private static readonly SolidColorBrush JsonBrushLineNum = FreezeBrush(90, 90, 90);
        private static readonly SolidColorBrush PlainCommentBrush = FreezeBrush(106, 153, 85);

        private static SolidColorBrush FreezeBrush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            if (br.CanFreeze) br.Freeze();
            return br;
        }

        private static int ComputeLineNumberGutterWidth(int lineCount)
        {
            lineCount = Math.Max(1, lineCount);
            return Math.Max(2, (int)Math.Floor(Math.Log10(lineCount)) + 1);
        }

        private static void AppendJsonLineNumberPrefix(InlineCollection inlines, int lineNumber1Based, int gutterChars)
        {
            string s = lineNumber1Based.ToString().PadLeft(gutterChars);
            inlines.Add(new Run(s + "  ") { Foreground = JsonBrushLineNum });
        }

        /// <param name="asPlainComment">整段按注释色显示（如空画布提示）。</param>
        private static void SetRichCodeViewContent(RichTextBox rtb, string text, bool asPlainComment = false)
        {
            if (rtb == null) return;
            string use = text ?? "";
            if (asPlainComment)
            {
                rtb.Document = BuildCommentOnlyDocument(use);
                ScheduleFlowDocumentPageWidth(rtb);
                return;
            }

            try
            {
                var tok = JToken.Parse(use);
                string formatted = tok.ToString(Formatting.Indented);
                rtb.Document = BuildJsonColoredDocument(formatted);
            }
            catch
            {
                rtb.Document = BuildPlainDocumentWithLineComments(use);
            }

            ScheduleFlowDocumentPageWidth(rtb);
        }

        /// <summary> RichTextBox 无 TextWrapping；用 FlowDocument.PageWidth 贴合视口以换行。 </summary>
        private static void ScheduleFlowDocumentPageWidth(RichTextBox rtb)
        {
            if (rtb == null) return;
            rtb.Dispatcher.BeginInvoke((Action)(() => SyncFlowDocumentPageWidthToViewport(rtb)), DispatcherPriority.Loaded);
        }

        private static void SyncFlowDocumentPageWidthToViewport(RichTextBox rtb)
        {
            if (rtb?.Document == null) return;
            double w = rtb.ViewportWidth;
            if (w <= 0) w = rtb.ActualWidth;
            if (w <= 32) return;

            double hPad = rtb.Padding.Left + rtb.Padding.Right + 24;
            rtb.Document.PageWidth = Math.Max(48, w - hPad);
        }

        private static FlowDocument CreateCodeFlowDocument()
        {
            return new FlowDocument {
                PagePadding = new Thickness(12, 12, 20, 16),
                Background = Brushes.Transparent,
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 12,
                TextAlignment = TextAlignment.Left
            };
        }

        private static FlowDocument BuildCommentOnlyDocument(string text)
        {
            var doc = CreateCodeFlowDocument();
            string useText = text ?? "";
            string[] lines = useText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            if (lines.Length == 0)
                lines = new[] { "" };

            var p = new Paragraph { Margin = new Thickness(0) };
            int gw = ComputeLineNumberGutterWidth(lines.Length);
            for (int li = 0; li < lines.Length; li++)
            {
                AppendJsonLineNumberPrefix(p.Inlines, li + 1, gw);
                p.Inlines.Add(new Run(lines[li]) { Foreground = PlainCommentBrush });
                if (li < lines.Length - 1)
                    p.Inlines.Add(new Run(Environment.NewLine) { Foreground = PlainCommentBrush });
            }

            doc.Blocks.Add(p);
            return doc;
        }

        private static FlowDocument BuildJsonColoredDocument(string text)
        {
            var doc = CreateCodeFlowDocument();
            var p = new Paragraph { Margin = new Thickness(0) };

            if (string.IsNullOrEmpty(text))
            {
                AppendJsonLineNumberPrefix(p.Inlines, 1, ComputeLineNumberGutterWidth(1));
                p.Inlines.Add(new Run("") { Foreground = JsonBrushDefault });
                doc.Blocks.Add(p);
                return doc;
            }

            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            int gw = ComputeLineNumberGutterWidth(lines.Length);

            for (int li = 0; li < lines.Length; li++)
            {
                AppendJsonLineNumberPrefix(p.Inlines, li + 1, gw);
                AppendJsonColoredInlines(p.Inlines, lines[li]);
                if (li < lines.Length - 1)
                    p.Inlines.Add(new Run(Environment.NewLine) { Foreground = JsonBrushDefault });
            }

            doc.Blocks.Add(p);
            return doc;
        }

        private static FlowDocument BuildPlainDocumentWithLineComments(string text)
        {
            var doc = CreateCodeFlowDocument();
            var p = new Paragraph { Margin = new Thickness(0) };
            p.Foreground = JsonBrushDefault;

            if (string.IsNullOrEmpty(text))
            {
                AppendJsonLineNumberPrefix(p.Inlines, 1, ComputeLineNumberGutterWidth(1));
                p.Inlines.Add(new Run("") { Foreground = JsonBrushDefault });
                doc.Blocks.Add(p);
                return doc;
            }

            var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            int gw = ComputeLineNumberGutterWidth(lines.Length);

            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];
                string trimmed = line.TrimStart();
                bool isComment = trimmed.StartsWith("//", StringComparison.Ordinal);

                AppendJsonLineNumberPrefix(p.Inlines, li + 1, gw);

                var run = new Run(line);
                run.Foreground = isComment ? PlainCommentBrush : JsonBrushDefault;
                p.Inlines.Add(run);
                if (li < lines.Length - 1)
                    p.Inlines.Add(new Run(Environment.NewLine) { Foreground = JsonBrushDefault });
            }

            doc.Blocks.Add(p);
            return doc;
        }

        private static void AppendJsonColoredInlines(InlineCollection inlines, string text)
        {
            int i = 0;
            int n = text == null ? 0 : text.Length;
            while (i < n)
            {
                char c = text[i];
                if (c == '\r')
                {
                    i++;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    int s = i;
                    while (i < n && char.IsWhiteSpace(text[i])) i++;
                    inlines.Add(new Run(text.Substring(s, i - s)) { Foreground = JsonBrushDefault });
                    continue;
                }

                if ("{}[],:".IndexOf(c) >= 0)
                {
                    inlines.Add(new Run(new string(c, 1)) { Foreground = JsonBrushPunct });
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    int start = i;
                    i++;
                    while (i < n)
                    {
                        if (text[i] == '\\' && i + 1 < n)
                        {
                            i += 2;
                            continue;
                        }

                        if (text[i] == '"')
                        {
                            i++;
                            break;
                        }

                        i++;
                    }

                    inlines.Add(new Run(text.Substring(start, i - start)) { Foreground = JsonBrushString });
                    continue;
                }

                if (c == '-' || char.IsDigit(c))
                {
                    int start = i;
                    if (c == '-') i++;
                    while (i < n && char.IsDigit(text[i])) i++;
                    if (i < n && text[i] == '.')
                    {
                        i++;
                        while (i < n && char.IsDigit(text[i])) i++;
                    }

                    if (i < n && (text[i] == 'e' || text[i] == 'E'))
                    {
                        i++;
                        if (i < n && (text[i] == '+' || text[i] == '-')) i++;
                        while (i < n && char.IsDigit(text[i])) i++;
                    }

                    inlines.Add(new Run(text.Substring(start, i - start)) { Foreground = JsonBrushNumber });
                    continue;
                }

                if (char.IsLetter(c))
                {
                    int start = i;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    string w = text.Substring(start, i - start);
                    Brush b = JsonBrushDefault;
                    if (string.Equals(w, "true", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(w, "false", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(w, "null", StringComparison.OrdinalIgnoreCase))
                        b = JsonBrushKeyword;
                    inlines.Add(new Run(w) { Foreground = b });
                    continue;
                }

                inlines.Add(new Run(new string(c, 1)) { Foreground = JsonBrushDefault });
                i++;
            }
        }
    }
}
