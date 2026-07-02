using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static class ShowPlanStepsTool
        {
            public const string FunctionName = "show_plan_steps";
            private const string ToolResultShown = "已展示实施计划卡片，等待用户点击执行按钮继续。";
            private const string ToolResultMissingPlan = "Error: arguments must satisfy the show_plan_steps schema and include schema_version, plan_title, steps, and execute_prompt; each step must include at least step_id, title, and detail.";

            private sealed class PlanStep
            {
                public string StepId { get; set; }
                public string Title { get; set; }
                public string Detail { get; set; }
                public string ExecutePrompt { get; set; }
            }

            private sealed class PlanPayload
            {
                public string SchemaVersion { get; set; }
                public string PlanTitle { get; set; }
                public string ExecutePrompt { get; set; }
                public string ExecuteLabel { get; set; }
                public List<PlanStep> Steps { get; set; } = new List<PlanStep>();
            }

            public static object GetApiToolDefinition() => new
            {
                type = "function",
                function = new
                {
                    name = FunctionName,
                    description = "Plan-mode UI tool. Render one large implementation-plan card in the chat area with a step list and one bottom execute button. Required top-level fields: schema_version, plan_title, steps, execute_prompt. Optional top-level field: execute_label. Each step must include step_id, title, and detail. For backward compatibility, a step-level execute_prompt may still be supplied, but the UI only renders one shared execute button.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            schema_version = new { type = "string", description = "Schema version, for example v1." },
                            plan_title = new { type = "string", description = "Overall plan title." },
                            execute_prompt = new { type = "string", description = "Required. Full Chinese execution instruction to send immediately after switching to Create mode." },
                            execute_label = new { type = "string", description = "Optional label for the shared execute button. Defaults to 执行计划." },
                            steps = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        step_id = new { type = "string", description = "Required stable step id, such as step_1." },
                                        title = new { type = "string", description = "Short step title." },
                                        detail = new { type = "string", description = "What this step does and the expected result, in one or two sentences." },
                                        execute_prompt = new { type = "string", description = "Backward compatibility only. If the top-level execute_prompt is missing, the tool may synthesize one from these step prompts." }
                                    },
                                    required = new[] { "step_id", "title", "detail" }
                                }
                            }
                        },
                        required = new[] { "schema_version", "plan_title", "steps", "execute_prompt" }
                    }
                }
            };

            public static (string toolResult, bool endApiRoundAwaitingUser) Run(
                JObject argsObj,
                string argsJson,
                List<(string primary, string secondary)> operationSummaries)
            {
                PlanPayload payload = NormalizePayload(ParsePayload(argsObj));
                if (payload == null || payload.Steps.Count == 0)
                {
                    string hint = argsJson != null && argsJson.Length > 240 ? argsJson.Substring(0, 240) + "..." : (argsJson ?? "");
                    string detail = "未能展示实施计划卡片：arguments 必须符合 show_plan_steps schema，至少包含 schema_version、plan_title、steps、execute_prompt；且每步都要提供 step_id、title、detail。";
                    if (!string.IsNullOrEmpty(hint))
                        detail += "\n参数片段：\n" + hint;
                    AppendQuietDiagnosticCard("实施计划", detail);
                    return (ToolResultMissingPlan, false);
                }

                try
                {
                    Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                    {
                        try
                        {
                            if (_thinkingBubble != null) _chatPanel.Children.Remove(_thinkingBubble);

                            StackPanel op = BuildToolOperationCardsPanel(operationSummaries);
                            if (op != null) _chatPanel.Children.Add(op);

                            FrameworkElement shell = BuildPlanShell(payload);
                            if (shell == null)
                                throw new InvalidOperationException("BuildPlanShell returned null.");
                            _chatPanel.Children.Add(shell);

                            if (_thinkingBubble != null) _chatPanel.Children.Add(_thinkingBubble);
                            if (_chatScroll != null) _chatScroll.ScrollToEnd();
                        }
                        catch (Exception ex)
                        {
                            AddGhLog.Error("show_plan_steps render failed", ex);
                            AppendQuietDiagnosticCard("实施计划", "卡片渲染失败：" + ex.Message);
                        }
                    }));
                }
                catch (Exception ex)
                {
                    AddGhLog.Error("show_plan_steps dispatch failed", ex);
                    AppendQuietDiagnosticCard("实施计划", "卡片派发失败：" + ex.Message);
                    return ("Error: show_plan_steps dispatch failed: " + ex.Message, false);
                }

                return (ToolResultShown, true);
            }

            private static PlanPayload ParsePayload(JObject argsObj)
            {
                var payload = new PlanPayload
                {
                    SchemaVersion = (argsObj?["schema_version"]?.ToString() ?? "").Trim(),
                    PlanTitle = (argsObj?["plan_title"]?.ToString() ?? "").Trim(),
                    ExecutePrompt = (argsObj?["execute_prompt"]?.ToString() ?? "").Trim(),
                    ExecuteLabel = (argsObj?["execute_label"]?.ToString() ?? "").Trim()
                };

                var arr = argsObj?["steps"] as JArray;
                if (arr == null) return payload;

                foreach (var token in arr.OfType<JObject>())
                {
                    payload.Steps.Add(new PlanStep
                    {
                        StepId = (token["step_id"]?.ToString() ?? "").Trim(),
                        Title = (token["title"]?.ToString() ?? "").Trim(),
                        Detail = (token["detail"]?.ToString() ?? "").Trim(),
                        ExecutePrompt = (token["execute_prompt"]?.ToString() ?? "").Trim()
                    });
                }

                return payload;
            }

            private static PlanPayload NormalizePayload(PlanPayload raw)
            {
                if (raw == null) return null;
                if (string.IsNullOrWhiteSpace(raw.SchemaVersion) || string.IsNullOrWhiteSpace(raw.PlanTitle))
                    return null;

                raw.Steps = (raw.Steps ?? new List<PlanStep>())
                    .Where(s => s != null
                        && !string.IsNullOrWhiteSpace(s.StepId)
                        && !string.IsNullOrWhiteSpace(s.Title)
                        && !string.IsNullOrWhiteSpace(s.Detail))
                    .Take(7)
                    .ToList();

                if (raw.Steps.Count == 0) return null;
                if (string.IsNullOrWhiteSpace(raw.ExecuteLabel)) raw.ExecuteLabel = "执行计划";
                if (string.IsNullOrWhiteSpace(raw.ExecutePrompt))
                    raw.ExecutePrompt = BuildFallbackExecutePrompt(raw);

                return string.IsNullOrWhiteSpace(raw.ExecutePrompt) ? null : raw;
            }

            private static string BuildFallbackExecutePrompt(PlanPayload payload)
            {
                if (payload == null || payload.Steps == null || payload.Steps.Count == 0) return null;

                bool hasPerStepPrompts = payload.Steps.Any(s => !string.IsNullOrWhiteSpace(s.ExecutePrompt));
                if (hasPerStepPrompts)
                {
                    var sbLegacy = new StringBuilder();
                    sbLegacy.AppendLine("请切换到 Create 模式，并按以下实施计划从上到下连续执行，不要停在单步：");
                    sbLegacy.AppendLine(payload.PlanTitle);
                    sbLegacy.AppendLine();
                    int i = 1;
                    foreach (var step in payload.Steps)
                    {
                        sbLegacy.Append(i++).Append(". ").AppendLine(step.Title);
                        if (!string.IsNullOrWhiteSpace(step.ExecutePrompt))
                            sbLegacy.AppendLine(step.ExecutePrompt.Trim());
                        else
                            sbLegacy.AppendLine(step.Detail.Trim());
                        sbLegacy.AppendLine();
                    }
                    return sbLegacy.ToString().Trim();
                }

                var sb = new StringBuilder();
                sb.AppendLine("请切换到 Create 模式，并按以下实施计划完整执行：");
                sb.AppendLine(payload.PlanTitle);
                sb.AppendLine();
                sb.AppendLine("实施步骤：");
                for (int i = 0; i < payload.Steps.Count; i++)
                {
                    var step = payload.Steps[i];
                    sb.Append(i + 1).Append(". ").Append(step.Title.Trim()).Append("：").AppendLine(step.Detail.Trim());
                }
                return sb.ToString().Trim();
            }

            private static Border BuildPlanShell(PlanPayload payload)
            {
                var rootPanel = new StackPanel
                {
                    Margin = new Thickness(0, 0, 0, 12),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                rootPanel.Children.Add(new TextBlock
                {
                    Text = payload.PlanTitle,
                    Foreground = new SolidColorBrush(Color.FromRgb(236, 236, 236)),
                    FontSize = 13.5,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 6),
                    TextWrapping = TextWrapping.Wrap
                });

                rootPanel.Children.Add(new TextBlock
                {
                    Text = "实施步骤",
                    Foreground = ThemeBrush(Color.FromRgb(28, 32, 38), Color.FromRgb(222, 222, 222)),
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 8)
                });

                for (int i = 0; i < payload.Steps.Count; i++)
                {
                    rootPanel.Children.Add(BuildStepRow(i + 1, payload.Steps[i]));
                }

                rootPanel.Children.Add(new TextBlock
                {
                    Text = "点击后会切换到 Create 模式，并按以上步骤连续执行。",
                    Foreground = ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(150, 150, 150)),
                    FontSize = 10.5,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });

                var executeButton = new Button
                {
                    Content = payload.ExecuteLabel,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MinWidth = 98,
                    Margin = new Thickness(0, 10, 0, 0),
                    Padding = new Thickness(12, 6, 12, 6),
                    Background = ThemeBrush(Color.FromRgb(24, 36, 54), Color.FromRgb(238, 238, 238)),
                    Foreground = ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(22, 22, 22)),
                    BorderBrush = ThemeBrush(Color.FromRgb(24, 36, 54), Color.FromRgb(210, 210, 210)),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold
                };
                executeButton.Click += (s, e) => ExecutePlan(payload.ExecutePrompt);
                rootPanel.Children.Add(executeButton);

                return new Border
                {
                    Background = ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(24, 24, 24)),
                    BorderBrush = ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromRgb(56, 56, 56)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 0, 0, 8),
                    Child = rootPanel
                };
            }

            private static Border BuildStepRow(int index, PlanStep step)
            {
                var body = new StackPanel();

                body.Children.Add(new TextBlock
                {
                    Text = index + ". " + (step.Title ?? "").Trim(),
                    Foreground = ThemeBrush(Color.FromRgb(28, 32, 38), Color.FromRgb(236, 236, 236)),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });

                if (!string.IsNullOrWhiteSpace(step.StepId))
                {
                    body.Children.Add(new TextBlock
                    {
                        Text = step.StepId.Trim(),
                        Foreground = ThemeBrush(Color.FromRgb(122, 128, 140), Color.FromRgb(112, 112, 112)),
                        FontSize = 9.5,
                        Margin = new Thickness(0, 2, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    });
                }

                body.Children.Add(new TextBlock
                {
                    Text = (step.Detail ?? "").Trim(),
                    Foreground = ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(168, 168, 168)),
                    FontSize = 11,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });

                return new Border
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(10, 8, 10, 8),
                    Background = ThemeBrush(Color.FromRgb(248, 249, 251), Color.FromRgb(32, 32, 32)),
                    BorderBrush = ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromRgb(70, 70, 70)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Child = body
                };
            }

            private static void ExecutePlan(string executePrompt)
            {
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    if (_isGenerating) return;
                    var carryoverImages = CloneAttachments(_finalVisualReviewSourceImages);
                    string originalVisionSourceInput = _finalVisualReviewSourceInput;
                    SetAgentMode(AgentMode.Create);
                    QueuePromptForImmediateSend(
                        executePrompt,
                        carryoverImages,
                        originalVisionSourceInput,
                        "执行已选方案");
                }));
            }
        }
    }
}
