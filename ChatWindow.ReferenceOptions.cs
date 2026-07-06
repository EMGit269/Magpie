using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        /// <summary> 「创建参考」：show_reference_options 工具定义、options 解析、聊天区选卡（无 summary 要求）。 </summary>
        private static class ShowReferenceOptionsTool
        {
            public const string FunctionName = "show_reference_options";

            public const string ToolResultShown = "已展示 5 条可选描述，等待用户选择或填写自定义后保存。";
            public const string ToolResultMissingOptions =
                "Error: arguments 中缺少有效的 options。必须提供 JSON 数组 options，恰好 5 个非空字符串；勿用单一大字符串代替数组，勿把 5 条只写在对话正文。";

            public static object GetApiToolDefinition() => new
            {
                type = "function",
                function = new
                {
                    name = FunctionName,
                    description =
                        "创建参考用：在聊天区展示 5 条可点击的描述，用户点选或自定义后保存画布 JSON。arguments **只需要** `options`（字符串数组，length=5）。\n" +
                        "【调用前限定】必须先确认画布可用：先 get_gh_components 确认能拿到画布且 components 非空；再 check_gh_errors，若报告中含 Error（或画布无法读取），**禁止**调用本工具，应直接提醒用户排错或补全后再试；仅当画布有内容且无 Error 时再调用本工具生成 options。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            options = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = "必填：5 个非空字符串，各为一句简短中文，概括一种可保存的画布侧重点。"
                            }
                        },
                        required = new[] { "options" }
                    }
                }
            };

            /// <summary> 菜单「创建参考」：先通过再发起对话，避免无画布/空画布/有 Error 时仍去生成卡片。 </summary>
            public static bool TryEnsureCanvasReadyForCreateReference()
            {
                if (!IsCanvasSuitableForReferenceSnapshot(out string why))
                {
                    AppendQuietDiagnosticCard("创建参考", why);
                    return false;
                }
                return true;
            }

            /// <summary> 画布可保存为参考：有打开文档、至少一个电池、且无 GH Error 级报错。 </summary>
            private static bool IsCanvasSuitableForReferenceSnapshot(out string reason)
            {
                reason = null;
                string json = Magpie.Host.GrasshopperDocumentHost.ExecuteGetCanvasSummary();
                if (string.IsNullOrWhiteSpace(json))
                {
                    reason = "无法读取 Grasshopper 画布，请打开文档后再创建参考。";
                    return false;
                }
                if (json.StartsWith("Error:", StringComparison.Ordinal))
                {
                    reason = json.StartsWith("Error: 没有打开的画布", StringComparison.Ordinal)
                        ? "当前没有可用的 Grasshopper 画布，请先打开或新建文档后再创建参考。"
                        : json;
                    return false;
                }
                try
                {
                    var jo = JObject.Parse(json);
                    int n = (jo["components"] as JArray)?.Count ?? 0;
                    if (n == 0)
                    {
                        reason = "当前画布上没有电池，内容不完整，请先布置好再创建参考。";
                        return false;
                    }
                }
                catch
                {
                    reason = "画布数据解析失败，请确认 Grasshopper 文档正常后再试。";
                    return false;
                }

                string check = Magpie.Host.GrasshopperDocumentHost.ExecuteCheckGhErrors() ?? "";
                if (check.StartsWith("Error:", StringComparison.Ordinal))
                {
                    reason = check;
                    return false;
                }
                if (check.IndexOf("Error(", StringComparison.Ordinal) >= 0)
                {
                    reason = "画布上仍有错误（Error），请先修复后再创建参考。\n\n" + check.Trim();
                    return false;
                }

                return true;
            }

            public static (string toolResult, bool endApiRoundAwaitingUser) Run(
                JObject argsObj,
                string argsJson,
                List<(string primary, string secondary)> operationSummaries)
            {
                if (!IsCanvasSuitableForReferenceSnapshot(out string canvasWhy))
                {
                    AppendQuietDiagnosticCard("创建参考", canvasWhy);
                    return ("Error: 当前不满足创建参考条件：" + canvasWhy, false);
                }

                List<string> labels = NormalizeOptionLabels(CollectOptionLabels(argsObj));
                if (labels.Count == 5)
                {
                    PresentSummaryStripAndPickers(operationSummaries, labels);
                    return (ToolResultShown, true);
                }

                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                    InsertChatElementBeforeThinking(BuildPickerShell(labels))));

                string hint = argsJson != null && argsJson.Length > 240 ? argsJson.Substring(0, 240) + "…" : (argsJson ?? "");
                string detail = "未能展示选项：options 须为长度 5 的非空字符串数组。已回传错误供模型重试。";
                if (!string.IsNullOrEmpty(hint))
                    detail += "\n参数片段：\n" + hint;
                AppendQuietDiagnosticCard("创建参考", detail);

                return (ToolResultMissingOptions, false);
            }

            private static readonly string[] OptionPropertyNames =
            {
                "options", "Options", "descriptions", "choices", "items", "labels", "candidates"
            };

            private static List<string> CollectOptionLabels(JObject argsObj)
            {
                if (argsObj == null) return new List<string>();
                foreach (string key in OptionPropertyNames)
                {
                    var list = ParseOptionsToken(argsObj[key]);
                    if (list.Count > 0) return list;
                }
                return new List<string>();
            }

            private static List<string> NormalizeOptionLabels(List<string> raw)
            {
                if (raw == null) return new List<string>();
                var cleaned = raw
                    .Select(s => (s ?? "").Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                if (cleaned.Count == 5) return cleaned;
                if (cleaned.Count > 5) return cleaned.Take(5).ToList();
                return cleaned;
            }

            private static List<string> ParseOptionsToken(JToken tok)
            {
                var list = new List<string>();
                if (tok == null || tok.Type == JTokenType.Null) return list;
                if (tok.Type == JTokenType.String)
                {
                    string s = tok.ToString().Trim();
                    if (s.StartsWith("[", StringComparison.Ordinal))
                    {
                        try
                        {
                            foreach (var x in JArray.Parse(s))
                                list.Add(x.ToString());
                            return list;
                        }
                        catch (Exception ex)
                        {
                            AddGhLog.Debug("ParseOptionsToken JArray.Parse fallback: " + ex.Message);
                        }
                    }
                    if (!string.IsNullOrEmpty(s) && (s.Contains("\n") || s.Contains("\r")))
                    {
                        var lines = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(l => l.Trim().TrimStart('•', '-', ' ', '　'))
                            .Where(l => l.Length > 0)
                            .ToList();
                        if (lines.Count >= 2) return lines;
                    }
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                    return list;
                }
                if (tok is JArray ja)
                {
                    foreach (var x in ja)
                    {
                        if (x == null || x.Type == JTokenType.Null) continue;
                        if (x.Type == JTokenType.String)
                        {
                            list.Add(x.ToString());
                            continue;
                        }
                        if (x is JObject jo)
                        {
                            foreach (var key in new[] { "text", "title", "label", "description", "value", "name", "content" })
                            {
                                var v = jo[key];
                                if (v == null || v.Type == JTokenType.Null) continue;
                                string t = v.ToString().Trim();
                                if (t.Length > 0)
                                {
                                    list.Add(t);
                                    break;
                                }
                            }
                            continue;
                        }
                        list.Add(x.ToString());
                    }
                    return list;
                }
                if (tok.Type == JTokenType.Object)
                {
                    foreach (var p in ((JObject)tok).Properties()
                                 .OrderBy(pr => int.TryParse(pr.Name, out int k) ? k : int.MaxValue))
                        list.Add(p.Value.ToString());
                }
                return list;
            }

            private static void PresentSummaryStripAndPickers(
                List<(string primary, string secondary)> summaries,
                IReadOnlyList<string> optionLabels)
            {
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    if (_thinkingBubble != null) _chatPanel.Children.Remove(_thinkingBubble);

                    StackPanel op = BuildToolOperationCardsPanel(summaries);
                    if (op != null) _chatPanel.Children.Add(op);

                    _chatPanel.Children.Add(BuildPickerShell(optionLabels));

                    if (_thinkingBubble != null) _chatPanel.Children.Add(_thinkingBubble);
                    if (_chatScroll != null) _chatScroll.ScrollToEnd();
                }));
            }

            private static SolidColorBrush Bg => ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(28, 28, 28));
            private static SolidColorBrush Bd => ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromRgb(56, 56, 56));
            private static SolidColorBrush Row => ThemeBrush(Color.FromRgb(248, 249, 251), Color.FromRgb(34, 34, 34));
            private static SolidColorBrush RowHi => ThemeBrush(Color.FromRgb(238, 242, 247), Color.FromRgb(42, 42, 42));
            private static SolidColorBrush Sub => ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(150, 150, 150));
            private static SolidColorBrush Txt => ThemeBrush(Color.FromRgb(28, 32, 38), Color.FromRgb(224, 224, 224));

            private static Border BuildPickerShell(IReadOnlyList<string> options)
            {
                var root = new StackPanel
                {
                    Margin = new Thickness(0, 0, 0, 12),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                root.Children.Add(new TextBlock
                {
                    Text = "点选一项保存，或在下方填写其它说明。",
                    Foreground = Sub,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 8),
                    TextWrapping = TextWrapping.Wrap
                });

                var list = new StackPanel();
                int n = options?.Count ?? 0;
                for (int i = 0; i < n; i++)
                {
                    string text = options[i] ?? "";
                    if (text.Length > 200) text = text.Substring(0, 197) + "…";
                    list.Children.Add(MakeRow(i + 1, text, root));
                }

                if (n == 0)
                {
                    list.Children.Add(new TextBlock
                    {
                        Text = "未收到选项，可直接在下方填写后保存。",
                        Foreground = Sub,
                        FontSize = 11,
                        Margin = new Thickness(0, 0, 0, 6)
                    });
                }

                var box = new TextBox
                {
                    MinHeight = 36,
                    MaxLength = 400,
                    Margin = new Thickness(0, 10, 0, 6),
                    Background = ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(22, 22, 22)),
                    Foreground = Txt,
                    BorderBrush = Bd,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 8, 6),
                    CaretBrush = ThemeBrush(Color.FromRgb(28, 32, 38), Color.FromRgb(255, 255, 255)),
                    FontSize = 12
                };

                var ok = new Border
                {
                    Background = ThemeBrush(Color.FromRgb(238, 242, 247), Color.FromRgb(58, 58, 58)),
                    BorderBrush = Bd,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(14, 6, 14, 6),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = "保存自定义", Foreground = Txt, FontSize = 12 }
                };

                void saveCustom()
                {
                    string t = box.Text.Trim();
                    if (string.IsNullOrEmpty(t)) return;
                    SaveReference(t);
                    AppendBubble("已选择: " + t, true);
                    root.IsEnabled = false;
                }

                ok.MouseLeftButtonDown += (s, e) => saveCustom();
                box.KeyDown += (s, e) => { if (e.Key == Key.Enter) saveCustom(); };

                root.Children.Add(list);
                root.Children.Add(box);
                root.Children.Add(ok);

                return new Border
                {
                    Background = Bg,
                    BorderBrush = Bd,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 10, 10, 10),
                    MaxWidth = 480,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Child = root
                };
            }

            private static Border MakeRow(int index, string body, StackPanel rootShell)
            {
                var idx = new TextBlock
                {
                    Text = index + ".",
                    Foreground = Sub,
                    FontSize = 12,
                    Width = 22,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                var line = new TextBlock
                {
                    Text = body,
                    Foreground = Txt,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18
                };

                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(idx, 0);
                Grid.SetColumn(line, 1);

                g.Children.Add(idx);
                g.Children.Add(line);

                var row = new Border
                {
                    Background = Row,
                    BorderBrush = Bd,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 8, 8, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Cursor = Cursors.Hand,
                    Child = g
                };

                string cap = body;
                row.MouseEnter += (s, e) =>
                {
                    if (!rootShell.IsEnabled) return;
                    row.Background = RowHi;
                };
                row.MouseLeave += (s, e) =>
                {
                    if (!rootShell.IsEnabled) return;
                    row.Background = Row;
                };
                row.MouseLeftButtonDown += (s, e) =>
                {
                    if (!rootShell.IsEnabled) return;
                    SaveReference(cap);
                    AppendBubble("已选择: " + cap, true);
                    rootShell.IsEnabled = false;
                    e.Handled = true;
                };

                return row;
            }
        }
    }
}
