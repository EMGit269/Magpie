using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Net;
using System.Net.Http;
using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;
using System.Windows.Documents;
using WpfPath = System.Windows.Shapes.Path;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Windows.Markup;
using System.Windows.Interop;
using Grasshopper.Kernel;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Script;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static Window _window;
        private const double DefaultWindowWidth = 410;
        private const double ChatPaneMinWidth = 386;
        private const double CodePaneMinWidth = 450;
        private const double CodeViewColumnWidth = 750;
        private const double HistorySidebarWidth = 320;
        private const double ChatContentMaxWidth = 900;
        private const double ChatContentCollapsedMaxWidth = 780;
        private const double ChatScrollbarGutter = 14;
        private const double ChatOuterInset = 20;
        private const double ChatMessageLeftInset = 14;
        private const double ChatMessageRightInset = 14;
        private static double _widthBeforeCodeView = double.NaN;
        private static StackPanel _chatPanel;
        private static ScrollViewer _chatScroll;
        private static FrameworkElement _chatToolbar;
        private static Border _toolbarDivider;
        private static TextBox _txtInput;
        private static Button _btnSend;
        private static Grid _contextMeterHost;
        private static WpfPath _contextRingProgress;
        private static System.Windows.Threading.DispatcherTimer _scrollHideTimer;
        private static readonly DependencyProperty SmoothVerticalOffsetProperty =
            DependencyProperty.RegisterAttached(
                "SmoothVerticalOffset",
                typeof(double),
                typeof(ChatWindow),
                new PropertyMetadata(0.0, OnSmoothVerticalOffsetChanged));

        private static Grid _settingsOverlay;
        private static Border _settingsPanel;
        private static TextBox _txtApiKey;
        private static ComboBox _comboProvider;
        private static ComboBox _comboVisionProvider;
        private static ComboBox _comboImageProvider;
        private static TextBox _txtApiBaseUrl;
        private static TextBox _txtModel;
        private static TextBox _txtProxyUrl;
        private static TextBox _txtVisionApiKey;
        private static TextBox _txtVisionApiBaseUrl;
        private static TextBox _txtVisionModel;
        private static TextBox _txtVisionProxyUrl;
        private static TextBox _txtImageApiKey;
        private static TextBox _txtImageApiBaseUrl;
        private static TextBox _txtImageModel;
        private static TextBox _txtImageProxyUrl;
        private static bool _isLoadingProviderSettings = false;
        private static System.Threading.CancellationTokenSource _cts;
        private static List<AttachmentItem> _pendingAttachments = new List<AttachmentItem>();
        private static WrapPanel _attachmentPreviewPanel;
        private static Button _btnClearImage;

        private static Border _codeViewBorder;
        private static RichTextBox _richCodeView;
        private static Border _codeCanvasIssuesHost;
        private static TextBox _txtCanvasIssues;
        private static Border _inputAreaBorder;
        private static Border _inputChromeBorder;
        private static TextBlock _emptyChatPrompt;
        private static Border _stickyUserMessageHost;
        private static Grid _stickyUserMessageStack;
        private static string _stickyUserMessageCurrentText;
        private static FrameworkElement _stickyUserMessageSource;
        private static Button _btnToggleViewMode;
        private static ColumnDefinition _historyCol;
        private static ColumnDefinition _chatCol;
        private static ColumnDefinition _splitterCol;
        private static ColumnDefinition _codeCol;
        private static GridSplitter _chatCodeSplitter;
        private static GH_Canvas _codeSurfaceHookedCanvas;
        private static GH_Document _codeSurfaceHookedDoc;
        private static System.Windows.Threading.DispatcherTimer _codeSurfaceDebounceTimer;
        private static bool _isCodeVisible = false;
        private static bool _isJsonMode = true;
        private static bool _isLibraryVisible = false;
        private static RowDefinition _libraryRow;
        private static FrameworkElement _libraryPanel;
        private static StackPanel _libraryContent;
        private static TextBlock _txtLibCount;
        private static RowDefinition _skillRow;
        private static StackPanel _skillContent;
        private static TextBlock _txtSkillCount;
        private static bool _isSkillVisible = false;
        private static Window _referenceLibraryWindow;

        private static List<ChatHistoryConversation> _chatHistory = new List<ChatHistoryConversation>();
        private static string _activeHistoryId;
        private static bool _isHistorySidebarVisible = false;
        private static bool _isHistoryRestoring = false;
        private static Border _historySidebar;
        private static StackPanel _historyListPanel;
        private static TextBlock _historyCountText;
        private static Button _btnToggleHistory;

        private static Button _btnModeDropdown;
        private static Button _btnAgentModeDropdown;
        private static Button _btnModeBattery;
        private static Button _btnModeCSharp;
        private static Button _btnModeMixed;
        private static Button _btnDisplayNormal;
        private static Button _btnDisplayLarge;
        private static Button _btnThemeDark;
        private static Button _btnThemeLight;
        private static Button _btnThemeSystem;
        private static MenuItem _menuAgentModeCreate;
        private static MenuItem _menuAgentModePlan;
        private static MenuItem _menuAgentModeSelfTrain;
        private static MenuItem _menuModeBattery;
        private static MenuItem _menuModeCSharp;
        private static MenuItem _menuModeMixed;

        private enum ImageInputRoute
        {
            None,
            ImageAttached
        }

        private static ImageInputRoute _activeImageInputRoute = ImageInputRoute.None;
        private static List<AttachmentItem> _currentTurnAttachments = new List<AttachmentItem>();

        private enum LayoutMode
        {
            Battery,
            Mixed,
            CSharpFirst
        }

        private enum AgentMode
        {
            Create,
            Plan,
            SelfTrain
        }

        private enum DisplayMode
        {
            Normal,
            Large
        }

        private enum ThemeMode
        {
            Dark,
            Light,
            System
        }

        private const string LayoutModeSettingKey = "MAGPIE_LayoutMode";
        private const string AgentModeSettingKey = "MAGPIE_AgentMode";
        private const string DisplayModeSettingKey = "MAGPIE_DisplayMode";
        private const string ThemeModeSettingKey = "MAGPIE_ThemeMode";
        private static LayoutMode _layoutMode = LayoutMode.Mixed;
        private static AgentMode _agentMode = AgentMode.Create;
        private static DisplayMode _displayMode = DisplayMode.Normal;
        private static ThemeMode _themeMode = ThemeMode.Light;

        private const string SYSTEM_PROMPT = @"你是 GH 参数化专家。

【建模逻辑】
1. 先对齐用户需求与约束，再落到具体步骤：数据流、关键电池；再动手改画布。
2. 命名：Number Slider 必须设 label；普通电池严禁改 label。
3. 最终回复用结构化 Markdown（短标题、列表、重点加粗）；代码/JSON/表达式/关键参数放在 ``` 代码块中，勿把大段技术内容堆在普通段落里。
4. 参考画布（reference）：先完成建模思路与 GH 逻辑规划，再查阅 skills/reference_index.md；仅当条目与**已确定方案**明显相关时才调用 read_reference_json 读 JSON 做对照或局部复用，勿「先读参考再空想」。
4a. Skills 很重要，且必须发生在建模规划前：首条系统消息会给出当前项目可用 skill 的 name/description/file；收到建模、修改、报错诊断或 C# Script 任务后，先根据用户任务、关键词和实现领域匹配相关专项 skill，并优先调用 `read_skill_file` 阅读正文，再制定建模方案、选择电池或修改画布。不要先凭记忆规划，再事后补读 skill；已有专项 skill 覆盖的工作流必须以 skill 正文为准，尤其是标注/出图、ClippingDrawing、Bake、可视化预览和参考画布复用。RhinoCommon API 查证 skill 是低频兜底，只在具体 API 不确定、API 编译错误、高风险 Rhino 文档操作或用户明确要求官方查证时读取。
4b. 本地文档查证：`web_research` 只读取本机镜像文档，不访问公网；当具体 RhinoCommon/Grasshopper API 签名不确定、用户要求官方文档核对，或遇到 CS0117、CS1061、CS1501 等 API 相关编译错误时，可自主调用 `web_research`。普通建模、已有 reference/skill 可复用、基础 C# 几何逻辑明确时不要查文档。API 查证不依赖宿主关键词硬触发：你在推理或修复过程中只要发现 RhinoCommon/Grasshopper 类型、方法、构造函数、重载或返回值不确定，就应自主查证。已知本地镜像可解析的官方 URL 时必须优先用 `mode=fetch` 直连。RhinoCommon/Grasshopper API 查证必须走逐层披露式 pipeline：先用 `mode=api_pipeline` 提交候选类型/方法和概念词，读取返回的 stages/candidate_docs/next_actions；从候选中选定具体官方类型页或方法页后，再用 `mode=fetch` 打开该 URL；只有 pipeline 无候选时才用 `mode=search` 扩展本地文档查询。不要把文档搜索结果当作画布事实或视觉事实。
5. 完成建模或修改后必须检查关键输出是否正确，不能以“没有报错”作为完成标准；目标电池可能仍输出 `Null`、空列表、空树或明显不符合预期的数据。
6. 检查时优先围绕目标结果相关的关键电池做验证：必要时先触发 recompute，再读取组件状态、预览信息或关键输出；如果仅靠现有信息无法确认，允许在目标输出端临时或直接连接 `Panel` 检查实际输出内容。
7. 若检查发现输出为 `Null`、空数据、类型不对、数据结构不对或结果与用户目标不一致，不能宣称完成；应继续定位并修正，再次验证后再结束。
8. Rhino 单位规则：所有几何尺寸、slider 数值、脚本参数默认都表示当前 Rhino 文档 `ModelUnitSystem` 的模型单位；如果任务涉及真实尺寸、距离、厚度、半径、高度、偏移或公差，必须先通过 `get_gh_components` 确认 `rhino_units`，不要假设单位是 mm、cm 或 m。若 `rhino_units.available=false`，应说明无法确认当前 Rhino 单位，并避免给出依赖单位的确定尺寸结论。

【多模态图片任务路由】
1. 你不是视觉模型，也看不到原图；当用户上传图片时，只能依据视觉预处理模型给出的图片分析、用户原始文字和当前上下文做判断。
2. 用户发图不等于要求建模或画图。先判断图片意图，再决定是否执行 Grasshopper 操作。意图类型包括：建模参考、修改建议、问题诊断、内容解释、素材输入、错误上传、不明确。
3. 只有当用户明确要求根据图片生成、建模、还原、复刻、设计或制作时，才进入建模/画图流程；否则按真实意图处理。
3a. 图片相关任务不由宿主按关键词硬路由；你需要根据用户原文、视觉预处理报告、当前上下文和可用工具自行判断下一步。
3b. 如果用户目标是理解、解释、识别或诊断图片，直接基于视觉信息回答，不要先查看或修改 Grasshopper 画布。
3c. 如果用户目标是 AI 创作图片、图生图或改图，调用 `create_ai_image`，不要擅自转成 GH 画布操作。
3d. 如果用户目标确实是根据图片做 GH/Rhino 建模、还原几何或修改当前模型，再使用 GH 工具；若意图不明确，先问一个简短澄清问题。
4. 如果图片是修改建议，围绕已有画布或上一轮结果定位要改的对象与变化点，不要无故重做整套方案。
5. 如果图片像截图、报错或界面异常，优先诊断问题与下一步操作，不要把截图当作设计参考。
6. 如果图片可能误发、与当前任务无关或意图不明确，先提出一个简短澄清问题，不要擅自建模。
7. 视觉预处理模型可结合受控画布上下文做定位，但不做最终决策，也不执行修改；你负责核实、规划和操作。不要声称自己直接看到了图片。
8. 视觉报告里的相关组件、输出或问题区域只是线索，不是最终事实；先核实再修改。
9. 文字与图片分析冲突时优先遵循用户文字；无法判断时先澄清。
10. 收到结构化视觉报告后，把“视觉事实”当高优先级参考；视觉报告只提供图片事实，不决定是否回答、出图或操作 Grasshopper。
11. 对“按参考图修改”“与图片保持一致”“看起来不对但未报错”这类任务，不要只依赖无报错和非 Null 数据；完成数据级检查后仍要说明剩余视觉偏差或不确定性。
12. 视觉报告与工具核实冲突时，以工具结果为准，并简短说明依据。
13. `capture_rhino_viewport` 不作为 AI tool 暴露；不要尝试调用截图工具。用户可通过画布界面手动捕获 Rhino/GH 截图。
14. 当用户要求检查模型形态、外观、轮廓、比例或整体效果时，优先做数据级验证：检查报错、Null、空数据、Panel 与关键输出；不要把截图路径、bbox 或预览计数等元数据当作视觉事实。
15. 当用户提供图像参考或用图片指出问题时，最终完成前仍应完成数据级检查；不要主动触发自动截图级视觉复核，也不要声称已由视觉模型核验，除非宿主明确返回了视觉分析结果。
16. 当用户目标是 AI 创作图片而不是 GH 建模时，调用 `create_ai_image`，不要先进入 VisualWorkflow，也不要擅自把图片要求转成 GH 画布操作。
16a. 当 `create_ai_image` 返回成功后，不要输出 Markdown 图片语法、模板变量、代码占位符或诸如 `${result.savedImages[0].path}` 之类的路径引用。图片展示由宿主界面负责；你只需用自然语言简短说明结果与可继续调整的方向。

【工具调用效率】
0. 画布对象对外默认使用短号 `01`、`02`、`03`… 作为 `id`；工具参数里优先使用这些短号，系统内部仍会映射到真实 GUID。若返回里同时出现 `guid`，那只是兼容与调试字段，不是首选引用方式。
1. 需要当前拓扑、连线或实例 id 时再 get_gh_components，避免无目的重复拉全图。
2. 新增一整块逻辑时，**优先**用 create_component_graph **一次**提交 components 与 connections，把该块内的放置与连线同时做完；尽量少用多轮「少量 add_gh_component ↔ 少量 connect_gh_components」交替，除非必须等上一轮返回的 id/端口才能定案。
3. 单独 add_gh_component 仅限少数必要情形（如占位定位、必须先看清画布再决定下一步）；能并入同一张局部图时仍应合并为一次 create_component_graph。
4. **脚本与 catalog（克制）**：get_gh_components 可读脚本在 **script_bodies**（可能截断）；内置 C#/VB Script 用 **gh_native_script_editor**（**read_source**＝与 script_bodies 同源反射读取，**set_source_commit**＝只替换首个可编辑块，勿整文件顶替模板）；**Rhino GhPython / Python 3 Script 等可执行源码在实例的 `Text` 属性，不是 `Description`，勿把代码写进 Description。** 其它用 **set_gh_component_value**（可加 **property**，优先 `Text`）；未执行可 **recompute_gh_canvas**。仅必要时 search_gh_component_catalog；日常用 get_gh_components、search_component_library、create_component_graph。
4a. C# Script 输出端口的真实代码变量可能是 `b/c/d...`；查画布或连线时优先看端口的 `semantic_label`、`display_name`、`description` 和 `csharp_variable`。连接 C# 输出时优先给 `connect_gh_components` 填 `from_port_label`，不要只凭 `b/c/d` 猜语义。
4b. C# Script 与其专属 Slider、Panel、Value List、Geometry Param、预览/调试 helper 应尽量放在同一个 Group。新建 C# 时优先在 `create_csharp_script_component` 的 `components` 内同时创建这些 helper，并填写 `group_name`；后续补加 helper 时用 `manage_gh_groups` 加入该 C# 所属组。
5. 每次调用 function 须在参数中填 **summary**（一句中文说明本次在做什么，勿写函数名或 API）；可选 **summary_detail**（卡片右侧短语）。**例外**：show_reference_options 仅需 options（5 个字符串数组），可不填 summary。
6. 优先批量、直接行动。
7. 不要把“我先检查一下画布”“我先读取当前状态”这类工具前过渡句写进 reasoning_content。只有在确实进行了建模判断、方案取舍、错误定位或结果核实时，才输出可见思考；若只是准备调工具，reasoning_content 留空。

【对用户表达】
对用户表达要直接说明改动内容、影响范围和需要确认的信息，避免暴露内部函数名或 API 名。";

        private static string BuildSystemPrompt()
        {
            string prompt = SYSTEM_PROMPT + BuildModePrompt(_layoutMode);
            prompt += GetModeSystemSkillPrompt();
            if (IsExecutionAgentMode() && _layoutMode == LayoutMode.CSharpFirst)
                prompt += BuildCSharpDedicatedToolPrompt();
            if (_agentMode == AgentMode.SelfTrain)
                prompt += BuildSelfTrainingPrompt();
            return prompt;
        }

        private static string GetModeSystemSkillPrompt()
        {
            string fileName = null;
            if (_layoutMode == LayoutMode.CSharpFirst)
                fileName = "system_csharp_mode.md";
            else if (_layoutMode == LayoutMode.Mixed)
                fileName = "system_mixed_mode.md";

            if (string.IsNullOrWhiteSpace(fileName))
                return "";

            try
            {
                string path = Path.Combine(GetSkillsDirectory(), fileName);
                if (!File.Exists(path))
                    return "";

                string raw = File.ReadAllText(path, Encoding.UTF8);
                string content = StripSkillFrontmatter(raw).Trim();
                return string.IsNullOrWhiteSpace(content) ? "" : "\n\n" + content;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Load mode system skill failed: " + ex.Message);
                return "";
            }
        }

        private static string StripSkillFrontmatter(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            string normalized = raw.Replace("\r\n", "\n");
            if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
                return raw;

            int end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end < 0)
                return raw;

            return normalized.Substring(end + 5).Replace("\n", Environment.NewLine);
        }

        private static string SanitizeAssistantDisplayContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return content ?? "";

            string sanitized = content.Replace("\r\n", "\n");

            sanitized = Regex.Replace(
                sanitized,
                @"^\s*!\[[^\]]*\]\(\s*\$\{[^)]*(savedImages|generated_images)[^)]*\}\s*\)\s*$",
                "",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            sanitized = Regex.Replace(
                sanitized,
                @"^\s*!\[[^\]]*\]\(\s*[^)]*(savedImages|generated_images)[^)]*\)\s*$",
                "",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            sanitized = Regex.Replace(
                sanitized,
                @"\$\{\s*result\.(savedImages|generated_images)\[[^\]]+\][^}]*\}",
                "",
                RegexOptions.IgnoreCase);

            sanitized = Regex.Replace(
                sanitized,
                @"(?m)^[ \t]*\n",
                "");

            sanitized = Regex.Replace(
                sanitized,
                @"\n{3,}",
                "\n\n");

            return sanitized.Trim();
        }

        private static string BuildCSharpDedicatedToolPrompt()
        {
            return @"

[C# Script dedicated tool rules]
1. In C# priority mode, new core modeling logic must use create_csharp_script_component, not create_script_component_graph.
2. Existing C# Script body edits must use edit_csharp_script_component, not gh_native_script_editor or set_gh_component_value.
3. The body field must contain only the RunScript method body. Do not include using statements, class declarations, the RunScript signature, or the default C# Script template.
4. The create tool first places a default C# Script component, waits for it to initialize, then applies the requested name, ports, and body.
5. Default C# outputs such as out/a are preserved. Requested outputs use their valid `outputs[].name` values as C# variables and port names; assign to those names in the body. Do not assign to a unless intentionally using the default output.
6. In C# priority mode, modify_gh_component_ports is not available for C# Script components. Change C# Script inputs and outputs through create_csharp_script_component or edit_csharp_script_component so ports, body, and aliases stay synchronized.
7. For real Rhino C# Script type hints, use known menu names such as bool, int, string, double, Point3d, Point3dList, Vector3d, Plane, Interval, Line, Circle, Arc, Curve, Polyline, Rectangle3d, Mesh, Surface, Brep, GeometryBase, TextDot, and TextEntity. Do not invent native Rhino type hint names.
8. ADD Agent conversion-only hints such as curve[], circle[], double[], and int[] are allowed only to drive defensive alias injection; they are not native Rhino port type hints.
9. Do not create unnecessary outputs. Prefer one or a few structured outputs; split into multiple script components only when the logic is genuinely clearer.
10. Do not declare local variables whose names collide with output variables currently in use.
11. Non-script helper components in this mode are limited to Params and Display categories for input, output, preview, and debugging.
12. When the user brings a new requirement or asks for an additive change, prefer adding one new C# Script component to extend the graph instead of rewriting an existing healthy C# Script component.
13. If an existing C# Script component has no bug and already satisfies its current responsibility, leave it unchanged unless modifying it is clearly necessary for correctness, shared interface changes, or a simpler overall graph boundary.
14. Starting with the second C# Script component in a solution, avoid dense inter-script wiring. If the new script has many ports, connect only the 1-2 ports that must receive upstream data and give all other ports safe defaults inside the script or via local helper inputs.
15. When creating a C# Script with its own sliders, panels, geometry params, value lists, preview, or debug helpers, put those helpers and the C# Script in one Grasshopper Group. Prefer create_csharp_script_component.components plus group_name; if helpers are added later, use manage_gh_groups to add them to the same group.";
        }

        private static string BuildCSharpTypedInputPrompt()
        {
            return @"

[C# typed input alias rules]
1. When create_csharp_script_component or edit_csharp_script_component inputs carry common type hints such as double/number, int/integer, bool, string, point3d, vector3d, curve, brep, mesh, or plane, the tool auto-injects strongly typed local aliases into the RunScript body.
2. Therefore, write the body as if those input names were already strongly typed. Do not spend extra tokens repeatedly converting object inputs unless you need custom validation beyond the tool's default aliasing.
3. If an input has no recognized type hint, treat it conservatively and add explicit handling only where necessary.
4. After writing a C# body, these tools automatically trigger a short delayed two-pass recompute. Do not routinely spend an extra tool call on recompute_gh_canvas unless the output still fails to update or verify.";
        }

        private static string BuildModePrompt(LayoutMode mode)
        {
            if (mode == LayoutMode.CSharpFirst)
            {
                return @"

[Current layout mode: C# Priority]
1. All core modeling logic must be implemented in one or more C# Script components. Keep the number of scripts small, but do not replace core logic with ordinary GH battery chains.
2. Params and Display components may be added directly as inputs, outputs, panels, previews, or diagnostics around C# Script.
3. Other non-script GH components are allowed only when there is a clear reason: `component_more_efficient` or `user_requested_component`.
4. Do not propose or build ordinary Grasshopper components for geometry, math, data-tree, list-processing, transform, curve, surface, brep, mesh, or similar core logic unless one of those two reasons truly applies.
5. Use `create_csharp_script_component` for new core logic. Do not use `create_component_graph` or `create_script_component_graph` as a substitute for the core implementation.
6. Existing C# Script logic must be edited with `edit_csharp_script_component`. Do not write C# source through generic value-setting tools.
7. The C# body must contain only the RunScript method body. Do not include using statements, class declarations, full templates, or a custom RunScript signature.
8. Requested C# outputs should use valid, explicit output names. Assign to those variables in the body.
9. Do not use `modify_gh_component_ports` for C# Script components. Normal C# interface changes must be made through C# script creation/editing tools.
10. Use Rhino C# Script menu type hint names for real port hints; list-like names such as curve[] or circle[] are ADD Agent conversion hints only, not native Rhino port hints.
11. If port changes temporarily desync the script signature, recompute and then fix the method body. Do not rewrite the full source template to work around it.
12. For each new user requirement, prefer extending the canvas with a new C# Script component that owns the new responsibility, instead of folding fresh logic into an existing healthy script.
13. Starting with the second C# Script component in a solution, avoid dense inter-script wiring. If a script has many ports, connect only the 1-2 upstream data ports that are necessary and set the rest with safe internal defaults or local helper inputs.
14. Treat an existing correct C# Script component as stable by default. Do not modify it unless required by bug fixing, shared interface changes, or a clearly better script boundary that reduces overall complexity.
15. When using `add_gh_component` or `create_component_graph` for non-Params/non-Display components in C# priority mode, choose `csharp_first_helper_reason`: `component_more_efficient` or `user_requested_component`, and explain it in `summary` or `csharp_first_helper_reason_detail`.
16. If the reason is not one of those two, do not call helper component tools; write or edit C# instead.";
            }

            if (mode == LayoutMode.Battery)
            {
                return @"

【当前排布模式：电池模式】
1. 优先使用原生 Grasshopper 电池完成建模逻辑，新增逻辑优先用 create_component_graph 批量创建电池与连线。
2. 本模式禁止新建 C# Script 电池；不要把新功能写成新的 C# 脚本组件。
3. 如果画布上已有 C# Script，仍可查看、读取、编辑或修复已有脚本；该限制只针对“新建 C# 电池”。
4. 只有在用户明确要求或现有画布已经依赖脚本时，才编辑已有脚本；常规建模尽量用电池网络表达。";
            }

            return @"

【当前排布模式：混合模式】
1. 在原生 GH 电池和 C# Script 之间平衡选择：简单、可视化、参数化的数据流优先用电池；复杂循环、几何算法、批量数据处理或重复逻辑可用 C# Script。
2. 新建一整块原生电池逻辑时优先用 create_component_graph；新建 C# 逻辑时使用 create_csharp_script_component。
3. 不要为了很小的参数、面板或基础数学操作创建 C# Script；也不要为了复杂算法硬堆大量电池。
4. 需要修改已有 C# Script 时使用专用编辑工具，保持现有模板和端口约定。";
        }

        private static List<object> BuildInitialSystemMessages()
        {
            return _contextPipeline.BuildInitialSystemMessages(new Magpie.Agent.ContextPipelineRequest
            {
                BasePromptProvider = BuildSystemPrompt,
                TypedPromptProvider = () => IsExecutionAgentMode() ? BuildCSharpTypedInputPrompt() : "",
                ContextPackProvider = BuildAgentContextPackPrompt,
                ContextLedgerProvider = BuildAgentContextLedgerPrompt,
                SkillSummaryProvider = GetSkillsSummary,
                MergeSkillsIntoBaseSystem = DeploymentOptions.MergeSkillsIntoSameSystemPromptAsLibraryIndex
            });
        }

        private static LayoutMode ReadLayoutModeSetting()
        {
            try
            {
                string raw = Grasshopper.Instances.Settings.GetValue(LayoutModeSettingKey, LayoutMode.Mixed.ToString());
                if (string.Equals(raw, "Normal", StringComparison.OrdinalIgnoreCase)) return LayoutMode.Mixed;
                if (string.Equals(raw, "PythonFirst", StringComparison.OrdinalIgnoreCase)) return LayoutMode.Mixed;
                if (Enum.TryParse(raw, true, out LayoutMode mode)) return mode;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Read layout mode failed: " + ex.Message);
            }
            return LayoutMode.Mixed;
        }

        private static AgentMode ReadAgentModeSetting()
        {
            try
            {
                string raw = Grasshopper.Instances.Settings.GetValue(AgentModeSettingKey, AgentMode.Create.ToString());
                if (Enum.TryParse(raw, true, out AgentMode mode)) return mode;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Read agent mode failed: " + ex.Message);
            }
            return AgentMode.Create;
        }

        private static DisplayMode ReadDisplayModeSetting()
        {
            try
            {
                string raw = Grasshopper.Instances.Settings.GetValue(DisplayModeSettingKey, DisplayMode.Normal.ToString());
                if (Enum.TryParse(raw, true, out DisplayMode mode)) return mode;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Read display mode failed: " + ex.Message);
            }
            return DisplayMode.Normal;
        }

        private static ThemeMode ReadThemeModeSetting()
        {
            try
            {
                string raw = Grasshopper.Instances.Settings.GetValue(ThemeModeSettingKey, ThemeMode.Light.ToString());
                if (Enum.TryParse(raw, true, out ThemeMode mode)) return mode;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Read theme mode failed: " + ex.Message);
            }
            return ThemeMode.Light;
        }

        private static void SaveLayoutModeSetting()
        {
            try { Grasshopper.Instances.Settings.SetValue(LayoutModeSettingKey, _layoutMode.ToString()); }
            catch (Exception ex) { AddGhLog.Warn("Save layout mode failed: " + ex.Message); }
        }

        private static void SaveAgentModeSetting()
        {
            try { Grasshopper.Instances.Settings.SetValue(AgentModeSettingKey, _agentMode.ToString()); }
            catch (Exception ex) { AddGhLog.Warn("Save agent mode failed: " + ex.Message); }
        }

        private static void SaveDisplayModeSetting()
        {
            try { Grasshopper.Instances.Settings.SetValue(DisplayModeSettingKey, _displayMode.ToString()); }
            catch (Exception ex) { AddGhLog.Warn("Save display mode failed: " + ex.Message); }
        }

        private static void SaveThemeModeSetting()
        {
            try { Grasshopper.Instances.Settings.SetValue(ThemeModeSettingKey, _themeMode.ToString()); }
            catch (Exception ex) { AddGhLog.Warn("Save theme mode failed: " + ex.Message); }
        }

        private static void ReplaceCurrentSystemPrompt()
        {
            if (_messages == null) return;
            int leading = ChatMessageHelpers.CountLeadingSystemMessages(_messages);
            for (int i = leading - 1; i >= 0; i--)
                _messages.RemoveAt(i);
            _messages.InsertRange(0, BuildInitialSystemMessages());
            RefreshContextMeter();
        }

        private static void SetLayoutMode(LayoutMode mode)
        {
            if (_isGenerating) return;
            _layoutMode = mode;
            SaveLayoutModeSetting();
            UpdateLayoutModeButtons();
            ReplaceCurrentSystemPrompt();
        }

        private static void SetAgentMode(AgentMode mode)
        {
            if (_isGenerating) return;
            _agentMode = mode;
            SaveAgentModeSetting();
            UpdateAgentModeButtons();
            ReplaceCurrentSystemPrompt();
        }

        private static void SetDisplayMode(DisplayMode mode)
        {
            _displayMode = mode;
            SaveDisplayModeSetting();
            ApplyDisplayMode();
            RefreshUI();
        }

        private static void SetThemeMode(ThemeMode mode)
        {
            _themeMode = mode;
            SaveThemeModeSetting();
            ApplyThemeMode();
            RefreshThemeAwareViews();
        }

        private static ThemeMode GetEffectiveThemeMode()
        {
            if (_themeMode != ThemeMode.System) return _themeMode;
            return IsSystemLightTheme() ? ThemeMode.Light : ThemeMode.Dark;
        }

        private static bool IsLightTheme()
        {
            return GetEffectiveThemeMode() == ThemeMode.Light;
        }

        private static bool IsSystemLightTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object value = key?.GetValue("AppsUseLightTheme");
                    if (value is int intValue) return intValue != 0;
                    if (value is byte byteValue) return byteValue != 0;
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("Read system theme failed: " + ex.Message);
            }
            return false;
        }

        private static Color ThemeColor(Color light, Color dark)
        {
            return IsLightTheme() ? light : dark;
        }

        private static SolidColorBrush ThemeBrush(Color light, Color dark)
        {
            return new SolidColorBrush(ThemeColor(light, dark));
        }

        private static FrameworkElement CreateChevronDownGlyph(Brush stroke = null)
        {
            return new WpfPath
            {
                Data = Geometry.Parse("M4,7 L8,11 L12,7"),
                Stroke = stroke ?? ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(160, 160, 160)),
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
                Width = 16,
                Height = 16,
                Stretch = Stretch.None,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static FrameworkElement CreateCloseGlyph(Brush stroke = null)
        {
            return new WpfPath
            {
                Data = Geometry.Parse("M5,5 L13,13 M13,5 L5,13"),
                Stroke = stroke ?? ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(160, 160, 160)),
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 18,
                Height = 18,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static FrameworkElement CreateSendGlyph(Brush fill = null)
        {
            return new WpfPath
            {
                Data = Geometry.Parse("M4,4 L14,9 L4,14 L6.5,9 Z"),
                Fill = fill ?? ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(0, 0, 0)),
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static object CreateDropdownContent(string label)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock
            {
                Text = label ?? "",
                Foreground = ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(160, 160, 160)),
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(CreateChevronDownGlyph());
            return panel;
        }

        private static void ApplySegmentedButtonState(Button button, bool selected)
        {
            if (button == null) return;
            Color bg = ThemeColor(
                selected ? Color.FromRgb(232, 238, 247) : Color.FromRgb(255, 255, 255),
                selected ? Color.FromRgb(43, 49, 58) : Color.FromRgb(34, 34, 34));
            Color fg = ThemeColor(
                selected ? Color.FromRgb(24, 36, 54) : Color.FromRgb(58, 64, 74),
                selected ? Color.FromRgb(229, 231, 235) : Color.FromRgb(205, 209, 216));
            Color border = ThemeColor(
                selected ? Color.FromRgb(156, 170, 190) : Color.FromRgb(214, 218, 225),
                selected ? Color.FromRgb(92, 98, 110) : Color.FromRgb(70, 70, 70));

            var fgBrush = new SolidColorBrush(fg);
            button.Background = new SolidColorBrush(bg);
            button.Foreground = fgBrush;
            button.SetValue(TextElement.ForegroundProperty, fgBrush);
            button.BorderBrush = new SolidColorBrush(border);
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private static void UpdateThemeButtons()
        {
            if (_btnThemeDark != null) _btnThemeDark.IsEnabled = !_isGenerating;
            if (_btnThemeLight != null) _btnThemeLight.IsEnabled = !_isGenerating;
            if (_btnThemeSystem != null) _btnThemeSystem.IsEnabled = !_isGenerating;
            ApplySegmentedButtonState(_btnThemeDark, _themeMode == ThemeMode.Dark);
            ApplySegmentedButtonState(_btnThemeLight, _themeMode == ThemeMode.Light);
            ApplySegmentedButtonState(_btnThemeSystem, _themeMode == ThemeMode.System);
        }

        private static void ApplyThemeMode()
        {
            if (_window == null) return;
            bool light = IsLightTheme();

            _window.Resources["ThemeWindowBackgroundBrush"] = ThemeBrush(Color.FromRgb(245, 247, 250), Color.FromRgb(20, 20, 20));
            _window.Resources["ThemeToolbarTextBrush"] = ThemeBrush(Color.FromRgb(34, 40, 49), Color.FromRgb(221, 225, 231));
            _window.Resources["ThemePrimaryTextBrush"] = ThemeBrush(Color.FromRgb(28, 32, 38), Color.FromRgb(243, 243, 243));
            _window.Resources["ThemeSecondaryTextBrush"] = ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(160, 160, 160));
            _window.Resources["ThemeBorderBrush"] = ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromRgb(58, 58, 58));
            _window.Resources["ThemePanelBrush"] = ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(30, 30, 30));
            _window.Resources["ThemeSurfaceBrush"] = ThemeBrush(Color.FromRgb(248, 249, 251), Color.FromRgb(36, 36, 36));
            _window.Resources["ThemeInputBrush"] = ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(42, 42, 42));
            _window.Resources["ThemeHoverBrush"] = ThemeBrush(Color.FromRgb(232, 236, 243), Color.FromRgb(26, 29, 34));
            _window.Resources["ThemePressedBrush"] = ThemeBrush(Color.FromRgb(220, 226, 235), Color.FromRgb(37, 42, 49));
            _window.Resources["ThemeSelectedSurfaceBrush"] = ThemeBrush(Color.FromRgb(238, 242, 247), Color.FromRgb(58, 58, 58));
            _window.Resources["ThemeScrollbarThumbBrush"] = ThemeBrush(Color.FromArgb(190, 92, 98, 110), Color.FromArgb(180, 255, 255, 255));
            _window.Resources["ThemeOverlayBrush"] = light
                ? new SolidColorBrush(Color.FromArgb(180, 245, 247, 250))
                : new SolidColorBrush(Color.FromArgb(165, 0, 0, 0));

            _window.Background = (Brush)_window.Resources["ThemeWindowBackgroundBrush"];
            ApplySystemTitleBarTheme();
            if (_settingsOverlay != null) _settingsOverlay.Background = (Brush)_window.Resources["ThemeOverlayBrush"];
            if (_settingsPanel != null)
            {
                _settingsPanel.Background = (Brush)_window.Resources["ThemePanelBrush"];
                _settingsPanel.BorderBrush = (Brush)_window.Resources["ThemeBorderBrush"];
            }
            if (_historySidebar != null)
            {
                _historySidebar.Background = (Brush)_window.Resources["ThemeSurfaceBrush"];
                _historySidebar.BorderBrush = (Brush)_window.Resources["ThemeBorderBrush"];
                if (_historySidebar.Effect is DropShadowEffect historyShadow)
                    historyShadow.Opacity = light ? 0.10 : 0.35;
                foreach (var text in FindVisualChildren<TextBlock>(_historySidebar))
                {
                    bool secondary = text.FontSize <= 11 || text == _historyCountText;
                    text.Foreground = secondary
                        ? (Brush)_window.Resources["ThemeSecondaryTextBrush"]
                        : (Brush)_window.Resources["ThemePrimaryTextBrush"];
                }
            }
            if (_libraryPanel != null)
            {
                if (_libraryPanel is Border libraryBorder)
                {
                    libraryBorder.Background = (Brush)_window.Resources["ThemeSurfaceBrush"];
                    libraryBorder.BorderBrush = (Brush)_window.Resources["ThemeBorderBrush"];
                }
                else if (_libraryPanel is Control libraryControl)
                {
                    libraryControl.Background = (Brush)_window.Resources["ThemeSurfaceBrush"];
                    libraryControl.BorderBrush = (Brush)_window.Resources["ThemeBorderBrush"];
                }
            }
            if (_codeViewBorder != null)
            {
                _codeViewBorder.Background = ThemeBrush(Color.FromRgb(250, 251, 253), Color.FromRgb(20, 20, 20));
                _codeViewBorder.BorderBrush = (Brush)_window.Resources["ThemeBorderBrush"];
            }
            if (_codeCanvasIssuesHost != null)
            {
                _codeCanvasIssuesHost.Background = (Brush)_window.Resources["ThemeSurfaceBrush"];
                _codeCanvasIssuesHost.BorderBrush = (Brush)_window.Resources["ThemeBorderBrush"];
            }
            if (_inputAreaBorder != null) _inputAreaBorder.Background = Brushes.Transparent;
            if (_inputChromeBorder != null)
            {
                _inputChromeBorder.Background = (Brush)_window.Resources["ThemeInputBrush"];
                _inputChromeBorder.BorderBrush = (Brush)_window.Resources["ThemeBorderBrush"];
                if (_inputChromeBorder.Effect is DropShadowEffect shadow)
                    shadow.Opacity = light ? 0 : 0.38;
            }
            if (_txtInput != null)
            {
                _txtInput.Foreground = (Brush)_window.Resources["ThemePrimaryTextBrush"];
                _txtInput.CaretBrush = (Brush)_window.Resources["ThemePrimaryTextBrush"];
            }
            if (_emptyChatPrompt != null) _emptyChatPrompt.Foreground = (Brush)_window.Resources["ThemePrimaryTextBrush"];
            ApplyThemeToSettingsPanelVisuals();
            UpdateLayoutModeButtons();
            UpdateDisplayModeButtons();
            UpdateThemeButtons();
            ApplySendButtonChrome(_isGenerating);
        }

        private static void ApplyThemeToSettingsPanelVisuals()
        {
            if (_settingsPanel == null || _window == null) return;
            foreach (var text in FindVisualChildren<TextBlock>(_settingsPanel))
            {
                if (HasVisualAncestor<ComboBox>(text))
                {
                    text.Foreground = (Brush)_window.Resources["ThemePrimaryTextBrush"];
                    continue;
                }
                bool secondary = text.FontSize <= 12 && text.FontWeight != FontWeights.SemiBold && text.FontWeight != FontWeights.Bold;
                text.Foreground = secondary
                    ? (Brush)_window.Resources["ThemeSecondaryTextBrush"]
                    : (Brush)_window.Resources["ThemePrimaryTextBrush"];
            }
            foreach (var textBox in FindVisualChildren<TextBox>(_settingsPanel))
            {
                textBox.Foreground = (Brush)_window.Resources["ThemePrimaryTextBrush"];
                textBox.CaretBrush = (Brush)_window.Resources["ThemePrimaryTextBrush"];
                textBox.Background = Brushes.Transparent;
            }
            foreach (var combo in FindVisualChildren<ComboBox>(_settingsPanel))
            {
                combo.Foreground = (Brush)_window.Resources["ThemePrimaryTextBrush"];
                combo.Background = (Brush)_window.Resources["ThemeInputBrush"];
                combo.BorderBrush = (Brush)_window.Resources["ThemeBorderBrush"];
            }
            foreach (var expander in FindVisualChildren<Expander>(_settingsPanel))
            {
                expander.Foreground = (Brush)_window.Resources["ThemePrimaryTextBrush"];
                expander.Background = Brushes.Transparent;
            }
            foreach (var button in FindVisualChildren<Button>(_settingsPanel))
            {
                if (button == _btnThemeDark || button == _btnThemeLight || button == _btnThemeSystem ||
                    button == _btnDisplayNormal || button == _btnDisplayLarge ||
                    button == _btnModeBattery || button == _btnModeMixed || button == _btnModeCSharp)
                    continue;
                button.Foreground = (Brush)_window.Resources["ThemePrimaryTextBrush"];
                if (button.Background is SolidColorBrush bg && bg.Color.A != 0)
                {
                    button.Background = (Brush)_window.Resources["ThemeSurfaceBrush"];
                    button.BorderBrush = (Brush)_window.Resources["ThemeBorderBrush"];
                }
            }
            foreach (var border in FindVisualChildren<Border>(_settingsPanel))
            {
                if (border == _settingsPanel || HasVisualAncestor<Button>(border) || HasVisualAncestor<ComboBox>(border))
                    continue;
                bool wrapsInput = FindVisualChildren<TextBox>(border).Any();
                if (wrapsInput)
                {
                    border.Background = (Brush)_window.Resources["ThemeInputBrush"];
                    border.BorderBrush = (Brush)_window.Resources["ThemeBorderBrush"];
                    border.BorderThickness = new Thickness(1);
                }
                else
                {
                    border.Background = Brushes.Transparent;
                    border.BorderThickness = new Thickness(0);
                }
            }
        }

        private static bool HasVisualAncestor<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject current = VisualTreeHelper.GetParent(child);
            while (current != null)
            {
                if (current is T) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private static void RefreshThemeAwareViews()
        {
            RefreshUI();
            if (_isHistorySidebarVisible) RefreshHistorySidebar();
            if (_isLibraryVisible) UpdateLibraryUI();
            if (_isSkillVisible) UpdateSkillLibraryUI();
        }

        private static double ChatBodyFontSize => _displayMode == DisplayMode.Large ? 18 : 14;
        private static double ChatBodyLineHeight => _displayMode == DisplayMode.Large ? 28 : 22;

        private static void ApplyDisplayMode()
        {
            if (_txtInput != null)
            {
                _txtInput.FontSize = _displayMode == DisplayMode.Large ? 18 : 14;
                _txtInput.MinHeight = _displayMode == DisplayMode.Large ? 58 : 44;
                _txtInput.MaxHeight = _displayMode == DisplayMode.Large ? 168 : 128;
            }

            if (_emptyChatPrompt != null)
                _emptyChatPrompt.FontSize = _displayMode == DisplayMode.Large ? 28 : 24;

            if (_window != null)
            {
                _window.Resources["ToolbarButtonSize"] = 42.0;
                _window.Resources["ToolbarIconSize"] = 21.0;
            }

            if (_btnSend != null)
            {
                _btnSend.Width = 28;
                _btnSend.Height = 28;
                _btnSend.FontSize = 13;
            }

            if (_contextMeterHost != null)
            {
                _contextMeterHost.Width = 21;
                _contextMeterHost.Height = 21;
            }

            UpdateDisplayModeButtons();
            UpdateChatBottomInset();
        }

        private static void UpdateDisplayModeButtons()
        {
            ApplySegmentedButtonState(_btnDisplayNormal, _displayMode == DisplayMode.Normal);
            ApplySegmentedButtonState(_btnDisplayLarge, _displayMode == DisplayMode.Large);
        }

        private static void ResetTransientConversationState()
        {
            _pendingAttachments.Clear();
            _queuedImmediateSendVisionSourceInputOverride = null;
            _queuedImmediateSendDisplayTextOverride = null;
            _pendingFinalVisualReview = false;
            _finalVisualReviewCompleted = false;
            _finalVisualReviewAttempted = false;
            _currentTurnHadToolExecution = false;
            _hasActiveVisionInputContext = false;
            _finalVisualReviewSourceInput = null;
            _finalVisualReviewSourceImages = new List<AttachmentItem>();
            _visualReviewPreviewComponentId = null;
            _visualReviewTargetSourceId = null;
            _visualReviewTargetOutputIndex = 0;
            ResetSelfTrainingTransientState();
        }

        private static void UpdateLayoutModeButtons()
        {
            string ModeLabel(LayoutMode mode)
            {
                switch (mode)
                {
                    case LayoutMode.Battery: return "电池模式";
                    case LayoutMode.CSharpFirst: return "C# 优先";
                    default: return "混合模式";
                }
            }

            if (_btnModeDropdown != null)
            {
                _btnModeDropdown.IsEnabled = !_isGenerating;
                _btnModeDropdown.Content = CreateDropdownContent(ModeLabel(_layoutMode));
                _btnModeDropdown.Foreground = ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(160, 160, 160));
            }

            if (_menuModeBattery != null) _menuModeBattery.Header = (_layoutMode == LayoutMode.Battery ? "✓ " : "   ") + "电池模式";
            if (_menuModeMixed != null) _menuModeMixed.Header = (_layoutMode == LayoutMode.Mixed ? "✓ " : "   ") + "混合模式";
            if (_menuModeCSharp != null) _menuModeCSharp.Header = (_layoutMode == LayoutMode.CSharpFirst ? "✓ " : "   ") + "C# 优先";

            if (_btnModeBattery != null) _btnModeBattery.IsEnabled = !_isGenerating;
            if (_btnModeMixed != null) _btnModeMixed.IsEnabled = !_isGenerating;
            if (_btnModeCSharp != null) _btnModeCSharp.IsEnabled = !_isGenerating;
            ApplySegmentedButtonState(_btnModeBattery, _layoutMode == LayoutMode.Battery);
            ApplySegmentedButtonState(_btnModeMixed, _layoutMode == LayoutMode.Mixed);
            ApplySegmentedButtonState(_btnModeCSharp, _layoutMode == LayoutMode.CSharpFirst);
        }

        private static void UpdateAgentModeButtons()
        {
            string label = _agentMode == AgentMode.Plan
                ? "Plan"
                : (_agentMode == AgentMode.SelfTrain ? "自训练" : "Create");

            if (_btnAgentModeDropdown != null)
            {
                _btnAgentModeDropdown.IsEnabled = !_isGenerating;
                _btnAgentModeDropdown.Content = CreateDropdownContent(label);
                _btnAgentModeDropdown.Foreground = ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(160, 160, 160));
            }

            if (_menuAgentModeCreate != null)
                _menuAgentModeCreate.Header = (_agentMode == AgentMode.Create ? "✓ " : "   ") + "Create";
            if (_menuAgentModePlan != null)
                _menuAgentModePlan.Header = (_agentMode == AgentMode.Plan ? "✓ " : "   ") + "Plan";
            if (_menuAgentModeSelfTrain != null)
                _menuAgentModeSelfTrain.Header = (_agentMode == AgentMode.SelfTrain ? "✓ " : "   ") + "自训练";
        }

        private static List<object> _messages = new List<object>();
        private static List<object> _displayMessages = new List<object>();
        private static string _cachedCanvasState = null;  // 画布状态缓存
        private static string _cachedRhinoUnitSignature = null;
        private static bool _canvasChanged = true;  // 画布是否改变标记
        private static Grasshopper.Kernel.GH_Document _publicIdBoundDocument = null;
        private static readonly Dictionary<Guid, string> _publicIdByGuid = new Dictionary<Guid, string>();
        private static readonly Dictionary<string, Guid> _guidByPublicId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        private static int _nextComponentPublicId = 1;
        private static int _nextGroupPublicId = 1;
        private static bool _pendingFinalVisualReview = false;
        private static bool _finalVisualReviewCompleted = false;
        private static bool _finalVisualReviewAttempted = false;
        private static bool _currentTurnHadToolExecution = false;
        private static bool _hasActiveVisionInputContext = false;
        private static string _finalVisualReviewSourceInput = null;
        private static List<AttachmentItem> _finalVisualReviewSourceImages = new List<AttachmentItem>();
        private static string _queuedImmediateSendVisionSourceInputOverride = null;
        private static string _queuedImmediateSendDisplayTextOverride = null;
        private static string _visualReviewPreviewComponentId = null;
        private static bool _chatContentWidthUpdatePending = false;
        private static double _lastAppliedChatContentWidth = -1;

        static ChatWindow()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Enable TLS 1.2 failed: " + ex.Message);
            }

            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                try
                {
                    _cts?.Cancel();
                    _cts?.Dispose();
                }
                catch (Exception ex)
                {
                    AddGhLog.Warn("ProcessExit CTS cleanup: " + ex.Message);
                }
                _cts = null;
            };
        }

        private static void EnforceChatHistoryLimit()
        {
            ChatMessageHelpers.TrimMessageHistory(_messages, DeploymentOptions.MaxPersistedChatMessages);
            RefreshContextMeter();
        }

        private static List<object> GetDisplayMessagesForUi()
        {
            return _displayMessages != null && _displayMessages.Count > 0
                ? _displayMessages
                : _messages;
        }

        private static JToken CloneMessageToken(object msg)
        {
            if (msg == null) return JValue.CreateNull();
            if (msg is JToken token) return token.DeepClone();
            return JToken.FromObject(msg);
        }

        private static void AddDisplayMessage(object msg)
        {
            if (_displayMessages == null)
                _displayMessages = new List<object>();
            _displayMessages.Add(CloneMessageToken(msg));
        }

        private static List<(string primary, string secondary)> ReadToolOperationSummaries(JArray summaries)
        {
            var result = new List<(string primary, string secondary)>();
            if (summaries == null) return result;

            foreach (var item in summaries.OfType<JObject>())
            {
                string primary = item["summary"]?.ToString() ?? item["primary"]?.ToString() ?? "";
                string secondary = item["summary_detail"]?.ToString() ?? item["secondary"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(primary))
                    result.Add((primary, secondary));
            }

            return result;
        }

        private static JArray BuildToolOperationSummaryArray(List<(string primary, string secondary, string undoId)> operationCards)
        {
            var arr = new JArray();
            if (operationCards == null) return arr;

            foreach (var item in operationCards)
            {
                if (string.IsNullOrWhiteSpace(item.primary)) continue;
                arr.Add(new JObject
                {
                    ["summary"] = item.primary,
                    ["summary_detail"] = item.secondary ?? ""
                });
            }

            return arr;
        }

        /// <summary>Rhino/GH 退出或聊天窗口关闭时取消进行中的请求并释放计时器等资源。</summary>
        public static void ShutdownPlugin()
        {
            try { _cts?.Cancel(); }
            catch (Exception ex) { AddGhLog.Warn("ShutdownPlugin cancel: " + ex.Message); }
            try { _cts?.Dispose(); }
            catch (Exception ex) { AddGhLog.Warn("ShutdownPlugin dispose CTS: " + ex.Message); }
            _cts = null;
            _isGenerating = false;

            try
            {
                _scrollHideTimer?.Stop();
                _scrollHideTimer = null;
            }
            catch (Exception ex) { AddGhLog.Warn("ShutdownPlugin timer: " + ex.Message); }

            TeardownGrasshopperCodeSurfaceHooks();
            DisposeCanvasWorkbench();
            StopHostBridgeRuntimeForExternalClients();

            _pendingAttachments.Clear();
            _thinkingBubble = null;
        }

        private static Border _thinkingBubble;
        private static bool _isGenerating = false;
        private static int _thinkingStatusStep = 0;
        private static readonly string[] ThinkingStatusVariants = new[]
        {
            "让我想想...",
            "努力中...",
            "我看看..."
        };

        private static void ApplyShimmerTextEffect(TextBlock text)
        {
            if (text == null) return;

            var transform = new TranslateTransform(-1.2, 0);
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                SpreadMethod = GradientSpreadMethod.Pad,
                RelativeTransform = transform
            };
            brush.GradientStops.Add(new GradientStop(ThemeColor(Color.FromRgb(112, 118, 130), Color.FromRgb(96, 96, 96)), 0.00));
            brush.GradientStops.Add(new GradientStop(ThemeColor(Color.FromRgb(112, 118, 130), Color.FromRgb(96, 96, 96)), 0.26));
            brush.GradientStops.Add(new GradientStop(ThemeColor(Color.FromRgb(160, 168, 180), Color.FromRgb(150, 150, 150)), 0.40));
            brush.GradientStops.Add(new GradientStop(ThemeColor(Color.FromRgb(200, 206, 216), Color.FromRgb(188, 188, 188)), 0.50));
            brush.GradientStops.Add(new GradientStop(ThemeColor(Color.FromRgb(160, 168, 180), Color.FromRgb(150, 150, 150)), 0.60));
            brush.GradientStops.Add(new GradientStop(ThemeColor(Color.FromRgb(112, 118, 130), Color.FromRgb(96, 96, 96)), 0.74));
            brush.GradientStops.Add(new GradientStop(ThemeColor(Color.FromRgb(112, 118, 130), Color.FromRgb(96, 96, 96)), 1.00));
            text.Foreground = brush;

            var shimmer = new DoubleAnimation
            {
                From = -1.35,
                To = 1.35,
                Duration = TimeSpan.FromSeconds(2.2),
                RepeatBehavior = RepeatBehavior.Forever
            };
            transform.BeginAnimation(TranslateTransform.XProperty, shimmer, HandoffBehavior.SnapshotAndReplace);
        }

        private static void ShowThinkingAnimation(string status = "思考中...")
        {
            status = NormalizeThinkingStatus(status);
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => {
                if (_thinkingBubble != null) {
                    var tb = _thinkingBubble.Child as TextBlock;
                    if (tb != null)
                    {
                        tb.Text = status;
                        ApplyShimmerTextEffect(tb);
                    }
                    return;
                }

                var text = new TextBlock {
                    Text = status,
                    FontSize = 12,
                    Margin = new Thickness(5, 0, 0, 18),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.Normal
                };
                ApplyShimmerTextEffect(text);

                _thinkingBubble = new Border { Child = text, Opacity = 1.0, Margin = new Thickness(0, 2, 0, 2) };
                _chatPanel.Children.Add(_thinkingBubble);
                _chatScroll.ScrollToEnd();
            }));
        }

        private static void HideThinkingAnimation()
        {
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => {
                if (_thinkingBubble != null) {
                    _chatPanel.Children.Remove(_thinkingBubble);
                    _thinkingBubble = null;
                }
            }));
        }

        private static string NormalizeThinkingStatus(string status)
        {
            string text = string.IsNullOrWhiteSpace(status) ? "思考中..." : status.Trim();
            if (!string.Equals(text, "思考中...", StringComparison.Ordinal))
                return text;

            if (_thinkingStatusStep++ == 0)
                return "思考中...";

            if (ThinkingStatusVariants.Length == 0)
                return "思考中...";

            return ThinkingStatusVariants[new Random(Guid.NewGuid().GetHashCode()).Next(ThinkingStatusVariants.Length)];
        }

        private static void InitializeFloatingScrollbars()
        {
            if (_window == null) return;

            _scrollHideTimer = new System.Windows.Threading.DispatcherTimer {
                Interval = TimeSpan.FromMilliseconds(700)
            };
            _scrollHideTimer.Tick += (s, e) => {
                _scrollHideTimer.Stop();
                HideFloatingScrollbars();
            };

            _window.Loaded += (s, e) => AttachFloatingScrollbarHandlers();
            _window.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((s, e) => {
                if (Math.Abs(e.VerticalChange) < 0.01 && Math.Abs(e.HorizontalChange) < 0.01) return;
                ShowFloatingScrollbars(e.OriginalSource as DependencyObject);
            }), true);
        }

        private static void AttachFloatingScrollbarHandlers()
        {
            if (_window == null) return;

            foreach (var viewer in FindVisualChildren<ScrollViewer>(_window)) {
                viewer.ScrollChanged -= ScrollViewer_ScrollChanged;
                viewer.ScrollChanged += ScrollViewer_ScrollChanged;
            }
        }

        private static void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (Math.Abs(e.VerticalChange) < 0.01 && Math.Abs(e.HorizontalChange) < 0.01) return;
            ShowFloatingScrollbars(sender as DependencyObject);
        }

        private static void OnSmoothVerticalOffsetChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
        {
            if (target is ScrollViewer viewer)
                viewer.ScrollToVerticalOffset((double)e.NewValue);
        }

        private static void SmoothScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!(sender is ScrollViewer viewer) || viewer.ScrollableHeight <= 0) return;

            e.Handled = true;

            double current = (double)viewer.GetValue(SmoothVerticalOffsetProperty);
            if (Math.Abs(current - viewer.VerticalOffset) > 1)
                current = viewer.VerticalOffset;

            double target = Math.Max(0, Math.Min(viewer.ScrollableHeight, current - e.Delta * 0.62));
            viewer.BeginAnimation(SmoothVerticalOffsetProperty, null);
            viewer.SetValue(SmoothVerticalOffsetProperty, current);

            var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(190))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            viewer.BeginAnimation(SmoothVerticalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
            ShowFloatingScrollbars(viewer);
        }

        private static void ShowFloatingScrollbars(DependencyObject scope)
        {
            if (_window == null) return;
            var root = scope ?? _window;

            foreach (var bar in FindVisualChildren<ScrollBar>(root)) {
                bar.Opacity = 0.55;
            }

            _scrollHideTimer?.Stop();
            _scrollHideTimer?.Start();
        }

        private static void HideFloatingScrollbars()
        {
            if (_window == null) return;

            foreach (var bar in FindVisualChildren<ScrollBar>(_window)) {
                if (bar.IsMouseOver || bar.IsMouseCaptureWithin) continue;
                bar.ClearValue(UIElement.OpacityProperty);
            }
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++) {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) yield return match;

                foreach (T descendant in FindVisualChildren<T>(child)) {
                    yield return descendant;
                }
            }
        }

        private static void ActivateMainWindow()
        {
            if (_window == null) return;

            if (_ballWindow != null && _ballWindow.IsVisible)
                _ballWindow.Hide();

            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;

            _window.Show();
            _window.Activate();
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

        private static void ApplySystemTitleBarTheme()
        {
            if (_window == null) return;

            try
            {
                IntPtr hwnd = new WindowInteropHelper(_window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int enabled = IsLightTheme() ? 0 : 1;
                int size = Marshal.SizeOf(typeof(int));
                int result = DwmSetWindowAttribute(hwnd, 20, ref enabled, size);
                if (result != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref enabled, size);
            }
            catch
            {
                // Older Windows builds can reject dark title bar attributes; the default title bar remains usable.
            }
        }

        private static ImageSource CreateTransparentWindowIcon()
        {
            byte[] pixels = new byte[4];
            BitmapSource icon = BitmapSource.Create(
                1,
                1,
                96,
                96,
                PixelFormats.Pbgra32,
                null,
                pixels,
                4);
            icon.Freeze();
            return icon;
        }

        private static void UpdateWindowMinWidthForVisiblePanes()
        {
            if (_window == null) return;

            double minWidth = DefaultWindowWidth;
            if (_isCodeVisible)
                minWidth = Math.Max(minWidth, ChatPaneMinWidth + CodePaneMinWidth + 40);

            _window.MinWidth = minWidth;
            if (_window.Width < minWidth)
                _window.Width = minWidth;
        }

        private static void UpdateChatContentWidth(bool animate = false)
        {
            double windowWidth = _window != null && _window.ActualWidth > 0 ? _window.ActualWidth : DefaultWindowWidth;
            double historyWidth = _isHistorySidebarVisible && !ShouldOverlayHistorySidebar() ? HistorySidebarWidth : 0;
            double fallbackWidth = Math.Max(ChatPaneMinWidth, windowWidth - historyWidth);
            double availableWidth = _chatCol != null && _chatCol.ActualWidth > 0
                ? _chatCol.ActualWidth
                : (_isCodeVisible ? Math.Max(ChatPaneMinWidth, fallbackWidth / 3.0) : fallbackWidth);
            double maxContentWidth = _isCodeVisible ? ChatContentMaxWidth : ChatContentCollapsedMaxWidth;
            double contentWidth = Math.Max(0, Math.Min(maxContentWidth, availableWidth - ChatOuterInset));
            if (Math.Abs(_lastAppliedChatContentWidth - contentWidth) >= 0.75)
            {
                _lastAppliedChatContentWidth = contentWidth;
                SetElementWidth(_chatScroll, contentWidth, animate);
                SetElementWidth(_inputAreaBorder, contentWidth, animate);
                SetElementWidth(_libraryPanel, contentWidth, animate);
                SetElementWidth(_stickyUserMessageHost, contentWidth, animate);
            }
            UpdateChatBottomInset();
            UpdateToolbarDividerVisibility();
            UpdateStickyUserMessage();
        }

        private static void UpdateChatBottomInset()
        {
            if (_chatPanel == null) return;

            double inputHeight = _inputAreaBorder != null && _inputAreaBorder.ActualHeight > 0
                ? _inputAreaBorder.ActualHeight
                : 170;
            double bottomInset = inputHeight + 14;
            _chatPanel.Margin = new Thickness(ChatMessageLeftInset, 8, ChatMessageRightInset, bottomInset);
        }

        private static bool IsVisibleChatEmpty()
        {
            if (_messages == null || _messages.Count == 0) return true;

            foreach (var msg in _messages)
            {
                string role = ChatMessageHelpers.TryGetRole(msg);
                if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static void UpdateEmptyChatLayout(bool? forceEmpty = null)
        {
            if (_inputAreaBorder == null) return;

            bool isEmpty = forceEmpty ?? IsVisibleChatEmpty();
            _inputAreaBorder.VerticalAlignment = isEmpty ? VerticalAlignment.Center : VerticalAlignment.Bottom;
            _inputAreaBorder.Padding = isEmpty
                ? new Thickness(ChatMessageLeftInset, 10, ChatMessageRightInset, 10)
                : new Thickness(ChatMessageLeftInset, 10, ChatMessageRightInset, 18);

            if (_emptyChatPrompt != null)
                _emptyChatPrompt.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;

            if (isEmpty && _stickyUserMessageHost != null)
            {
                _stickyUserMessageHost.Visibility = Visibility.Collapsed;
                _stickyUserMessageCurrentText = null;
                SetStickyUserMessageSource(null);
                _stickyUserMessageStack?.Children.Clear();
            }

            UpdateChatBottomInset();
        }

        private static void ScheduleChatContentWidthUpdate()
        {
            if (_window == null) return;
            if (_chatContentWidthUpdatePending) return;
            _chatContentWidthUpdatePending = true;
            _window.Dispatcher.BeginInvoke((Action)(() =>
            {
                _chatContentWidthUpdatePending = false;
                UpdateChatContentWidth();
            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        private static void SetElementWidth(FrameworkElement element, double width, bool animate = false)
        {
            if (element == null) return;
            double current = !double.IsNaN(element.Width) && element.Width > 0 ? element.Width : element.ActualWidth;
            if (current > 0 && Math.Abs(current - width) < 0.75)
                return;

            if (!animate || double.IsNaN(element.Width) || element.ActualWidth <= 0 || Math.Abs(element.ActualWidth - width) < 2)
            {
                element.BeginAnimation(FrameworkElement.WidthProperty, null);
                element.Width = width;
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var animation = new DoubleAnimation(element.ActualWidth, width, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = ease
            };
            element.BeginAnimation(FrameworkElement.WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private static string NormalizeStickyUserText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            text = Regex.Replace(text.Trim(), @"\s+", " ");
            const int maxLength = 140;
            return text.Length > maxLength ? text.Substring(0, maxLength).TrimEnd() + "..." : text;
        }

        private static void UpdateStickyUserMessage(double scrollDelta = 0)
        {
            if (_chatScroll == null || _chatPanel == null || _stickyUserMessageHost == null || _stickyUserMessageStack == null)
                return;

            if (_chatScroll.VerticalOffset <= 0.5)
            {
                HideStickyUserMessage(scrollDelta);
                return;
            }

            string activeText = null;
            FrameworkElement activeSource = null;
            const double activationY = 8;

            foreach (FrameworkElement child in _chatPanel.Children.OfType<FrameworkElement>())
            {
                string userText = child.Tag as string;
                if (string.IsNullOrWhiteSpace(userText)) continue;

                double top;
                try
                {
                    top = child.TransformToAncestor(_chatScroll).Transform(new Point(0, 0)).Y;
                }
                catch
                {
                    continue;
                }

                if (top <= activationY)
                {
                    double bottom = top + Math.Max(0, child.ActualHeight);
                    if (IsUserMessageOverStickyLineLimit(child) && bottom > activationY)
                    {
                        activeText = null;
                        activeSource = null;
                    }
                    else
                    {
                        activeText = userText;
                        activeSource = child;
                    }
                }
                else
                    break;
            }

            if (string.IsNullOrWhiteSpace(activeText))
            {
                HideStickyUserMessage(scrollDelta);
                return;
            }

            SetStickyUserMessageSource(activeSource);

            if (string.Equals(_stickyUserMessageCurrentText, activeText, StringComparison.Ordinal))
            {
                if (_stickyUserMessageStack.Children.Count > 0)
                {
                    _stickyUserMessageHost.Visibility = Visibility.Visible;
                    return;
                }

                _stickyUserMessageCurrentText = null;
            }

            TransitionStickyUserMessage(activeText, scrollDelta);
            _stickyUserMessageHost.Visibility = Visibility.Visible;
        }

        private static void SetStickyUserMessageSource(FrameworkElement source)
        {
            if (ReferenceEquals(_stickyUserMessageSource, source)) return;

            bool isFirstStickySource = _stickyUserMessageSource == null && source != null;

            if (_stickyUserMessageSource != null)
                AnimateElementOpacity(_stickyUserMessageSource, 1, 100);

            _stickyUserMessageSource = source;

            if (_stickyUserMessageSource != null)
            {
                if (isFirstStickySource)
                {
                    _stickyUserMessageSource.BeginAnimation(UIElement.OpacityProperty, null);
                    _stickyUserMessageSource.Opacity = 0;
                }
                else
                {
                    AnimateElementOpacity(_stickyUserMessageSource, 0, 100);
                }
            }
        }

        private static void AnimateElementOpacity(UIElement element, double opacity, int milliseconds)
        {
            if (element == null) return;

            element.BeginAnimation(UIElement.OpacityProperty, null);
            var animation = new DoubleAnimation(element.Opacity, opacity, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private static bool IsUserMessageOverStickyLineLimit(FrameworkElement source)
        {
            double contentHeight = GetPrimaryUserMessageTextHeight(source);
            if (contentHeight <= 0)
                contentHeight = Math.Max(0, (source?.ActualHeight ?? 0) - 16);

            return contentHeight > GetStickyUserMessageTextMaxHeight() + 2;
        }

        private static double GetPrimaryUserMessageTextHeight(DependencyObject source)
        {
            if (source == null) return 0;

            foreach (var richTextBox in FindVisualChildren<RichTextBox>(source))
            {
                if (richTextBox.ActualHeight > 0)
                    return richTextBox.ActualHeight;
            }

            return 0;
        }

        private static double GetStickyUserMessageTextMaxHeight()
        {
            return ChatBodyLineHeight * 4;
        }

        private static Border CreateStickyUserMessageCard(string text)
        {
            var content = BuildMarkdownPanel(text, false);
            content.MaxHeight = GetStickyUserMessageTextMaxHeight();
            content.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;

            var card = new Border
            {
                Padding = new Thickness(14, 8, 14, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(30, 30, 30)),
                BorderBrush = ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromRgb(52, 52, 52)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                ClipToBounds = true,
                Child = content,
                RenderTransform = new TranslateTransform()
            };

            return card;
        }

        private static void HideStickyUserMessage(double scrollDelta)
        {
            if (_stickyUserMessageHost == null || _stickyUserMessageStack == null)
                return;

            _stickyUserMessageCurrentText = null;
            RestoreStickyUserMessageSourceInstantly();
            foreach (var card in _stickyUserMessageStack.Children.OfType<FrameworkElement>())
            {
                card.BeginAnimation(UIElement.OpacityProperty, null);
                if (card.RenderTransform is TranslateTransform transform)
                    transform.BeginAnimation(TranslateTransform.YProperty, null);
            }
            _stickyUserMessageStack.Children.Clear();
            _stickyUserMessageHost.Visibility = Visibility.Collapsed;
        }

        private static void RestoreStickyUserMessageSourceInstantly()
        {
            if (_stickyUserMessageSource == null) return;

            _stickyUserMessageSource.BeginAnimation(UIElement.OpacityProperty, null);
            _stickyUserMessageSource.Opacity = 1;
            _stickyUserMessageSource = null;
        }

        private static void TransitionStickyUserMessage(string text, double scrollDelta)
        {
            if (_stickyUserMessageStack == null) return;

            var oldCards = _stickyUserMessageStack.Children.OfType<FrameworkElement>().ToList();
            var nextCard = CreateStickyUserMessageCard(text);
            nextCard.Opacity = oldCards.Count == 0 ? 1 : 0.88;
            _stickyUserMessageStack.Children.Add(nextCard);
            _stickyUserMessageCurrentText = text;

            double offset = Math.Max(18, oldCards.FirstOrDefault()?.ActualHeight ?? 34);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            double direction = scrollDelta < -0.01 ? -1 : 1;

            if (oldCards.Count > 0)
            {
                ((TranslateTransform)nextCard.RenderTransform).Y = offset * direction;

                foreach (var oldCard in oldCards)
                {
                    if (!(oldCard.RenderTransform is TranslateTransform oldTransform))
                        oldCard.RenderTransform = oldTransform = new TranslateTransform();

                    var oldSlide = new DoubleAnimation(0, -offset * direction, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
                    var oldFade = new DoubleAnimation(oldCard.Opacity, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
                    oldFade.Completed += (s, e) => _stickyUserMessageStack.Children.Remove(oldCard);
                    oldTransform.BeginAnimation(TranslateTransform.YProperty, oldSlide);
                    oldCard.BeginAnimation(UIElement.OpacityProperty, oldFade);
                }

                var nextSlide = new DoubleAnimation(offset * direction, 0, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease };
                var nextFade = new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
                ((TranslateTransform)nextCard.RenderTransform).BeginAnimation(TranslateTransform.YProperty, nextSlide);
                nextCard.BeginAnimation(UIElement.OpacityProperty, nextFade);
            }
        }

        private static void UpdateToolbarDividerVisibility()
        {
            if (_toolbarDivider == null) return;
            _toolbarDivider.Visibility = (_isHistorySidebarVisible || _isCodeVisible) ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void EnsureSlideTransform(FrameworkElement element)
        {
            if (element == null) return;
            if (!(element.RenderTransform is TranslateTransform))
                element.RenderTransform = new TranslateTransform();
        }

        private static void AnimatePanelIn(FrameworkElement element, double fromX)
        {
            if (element == null) return;
            EnsureSlideTransform(element);
            var transform = (TranslateTransform)element.RenderTransform;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            transform.X = fromX;
            element.Opacity = 0;
            element.Visibility = Visibility.Visible;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var slide = new DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
            transform.BeginAnimation(TranslateTransform.XProperty, slide);
            element.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private static void AnimatePanelOut(FrameworkElement element, double toX, Action completed)
        {
            if (element == null)
            {
                completed?.Invoke();
                return;
            }

            EnsureSlideTransform(element);
            var transform = (TranslateTransform)element.RenderTransform;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var slide = new DoubleAnimation(transform.X, toX, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
            var fade = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease };
            fade.Completed += (s, e) =>
            {
                transform.BeginAnimation(TranslateTransform.XProperty, null);
                element.BeginAnimation(UIElement.OpacityProperty, null);
                transform.X = 0;
                element.Opacity = 1;
                completed?.Invoke();
            };
            transform.BeginAnimation(TranslateTransform.XProperty, slide);
            element.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        public static void Show()
        {
            EnsureHostBridgeRuntimeForExternalClients();
            if (_window != null)
            {
                ActivateMainWindow();
                StartGrasshopperCodeSurfaceHooks();
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    SyncCodeIssuesStripHeightToInputArea();
                    ScheduleCodeSurfaceRefreshFromCanvas();
                    RefreshCanvasWorkbenchViewState();
                    NotifyCanvasConversationChanged(true);
                }));
                return;
            }

            string xaml = @"
<Window xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
        Title="""" Height=""760"" Width=""410""
        MinHeight=""520"" MinWidth=""410""
        ResizeMode=""CanResize""
        WindowStyle=""SingleBorderWindow"" Background=""{DynamicResource ThemeWindowBackgroundBrush}""
        Topmost=""True"" WindowStartupLocation=""CenterScreen"" x:Name=""MagpieWindow"">
    <Window.Resources>
        <sys:Double x:Key=""ToolbarButtonSize"" xmlns:sys=""clr-namespace:System;assembly=mscorlib"">42</sys:Double>
        <sys:Double x:Key=""ToolbarIconSize"" xmlns:sys=""clr-namespace:System;assembly=mscorlib"">21</sys:Double>
        <SolidColorBrush x:Key=""ThemeWindowBackgroundBrush"" Color=""#F5F7FA""/>
        <SolidColorBrush x:Key=""ThemeToolbarTextBrush"" Color=""#222831""/>
        <SolidColorBrush x:Key=""ThemePrimaryTextBrush"" Color=""#1C2026""/>
        <SolidColorBrush x:Key=""ThemeSecondaryTextBrush"" Color=""#5C626E""/>
        <SolidColorBrush x:Key=""ThemeBorderBrush"" Color=""#D6DAE1""/>
        <SolidColorBrush x:Key=""ThemePanelBrush"" Color=""#FFFFFF""/>
        <SolidColorBrush x:Key=""ThemeSurfaceBrush"" Color=""#F8F9FB""/>
        <SolidColorBrush x:Key=""ThemeInputBrush"" Color=""#FFFFFF""/>
        <SolidColorBrush x:Key=""ThemeHoverBrush"" Color=""#E8ECF3""/>
        <SolidColorBrush x:Key=""ThemePressedBrush"" Color=""#DCE2EB""/>
        <SolidColorBrush x:Key=""ThemeSelectedSurfaceBrush"" Color=""#EEF2F7""/>
        <SolidColorBrush x:Key=""ThemeScrollbarThumbBrush"" Color=""#BE5C626E""/>
        <SolidColorBrush x:Key=""ThemeOverlayBrush"" Color=""#B4F5F7FA""/>
        <Style TargetType=""ScrollBar"">
            <Setter Property=""Background"" Value=""Transparent""/>
            <Setter Property=""MinWidth"" Value=""0""/>
            <Setter Property=""MinHeight"" Value=""0""/>
            <Setter Property=""Opacity"" Value=""0""/>
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""ScrollBar"">
                        <Grid x:Name=""Bg"" Background=""Transparent"">
                            <Track x:Name=""PART_Track"" IsDirectionReversed=""true"">
                                <Track.DecreaseRepeatButton>
                                    <RepeatButton Command=""ScrollBar.PageUpCommand"" Opacity=""0""/>
                                </Track.DecreaseRepeatButton>
                                <Track.IncreaseRepeatButton>
                                    <RepeatButton Command=""ScrollBar.PageDownCommand"" Opacity=""0""/>
                                </Track.IncreaseRepeatButton>
                                <Track.Thumb>
                                    <Thumb MinWidth=""0"" MinHeight=""0"" Background=""Transparent"">
                                        <Thumb.Template>
                                            <ControlTemplate TargetType=""Thumb"">
                                                <Border Background=""{DynamicResource ThemeScrollbarThumbBrush}"" Width=""6"" HorizontalAlignment=""Right"" CornerRadius=""3"" Margin=""0,2""/>
                                            </ControlTemplate>
                                        </Thumb.Template>
                                    </Thumb>
                                </Track.Thumb>
                            </Track>
                        </Grid>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
            <Style.Triggers>
                <Trigger Property=""Orientation"" Value=""Vertical"">
                    <Setter Property=""Width"" Value=""12""/>
                </Trigger>
                <Trigger Property=""Orientation"" Value=""Horizontal"">
                    <Setter Property=""Height"" Value=""12""/>
                </Trigger>
                <Trigger Property=""IsMouseOver"" Value=""True"">
                    <Setter Property=""Opacity"" Value=""0.78""/>
                </Trigger>
                <Trigger Property=""IsMouseCaptureWithin"" Value=""True"">
                    <Setter Property=""Opacity"" Value=""0.78""/>
                </Trigger>
            </Style.Triggers>
        </Style>
        <Style TargetType=""ScrollViewer"">
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""ScrollViewer"">
                        <Grid>
                            <ScrollContentPresenter x:Name=""PART_ScrollContentPresenter"" CanContentScroll=""{TemplateBinding CanContentScroll}""/>
                            <ScrollBar x:Name=""PART_VerticalScrollBar"" HorizontalAlignment=""Right"" Maximum=""{TemplateBinding ScrollableHeight}"" ViewportSize=""{TemplateBinding ViewportHeight}"" Value=""{Binding VerticalOffset, Mode=OneWay, RelativeSource={RelativeSource TemplatedParent}}"" Visibility=""{TemplateBinding ComputedVerticalScrollBarVisibility}""/>
                            <ScrollBar x:Name=""PART_HorizontalScrollBar"" VerticalAlignment=""Bottom"" Orientation=""Horizontal"" Maximum=""{TemplateBinding ScrollableWidth}"" ViewportSize=""{TemplateBinding ViewportWidth}"" Value=""{Binding HorizontalOffset, Mode=OneWay, RelativeSource={RelativeSource TemplatedParent}}"" Visibility=""{TemplateBinding ComputedHorizontalScrollBarVisibility}""/>
                        </Grid>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        <Style TargetType=""Button"" x:Key=""IconButtonStyle"">
            <Setter Property=""Background"" Value=""Transparent""/>
            <Setter Property=""BorderThickness"" Value=""0""/>
            <Setter Property=""MinWidth"" Value=""28""/>
            <Setter Property=""Height"" Value=""28""/>
            <Setter Property=""Padding"" Value=""8,0""/>
            <Setter Property=""HorizontalContentAlignment"" Value=""Center""/>
            <Setter Property=""VerticalContentAlignment"" Value=""Center""/>
            <Setter Property=""VerticalAlignment"" Value=""Center""/>
            <Setter Property=""Cursor"" Value=""Hand""/>
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""Button"">
                        <Border x:Name=""Bd"" Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""14"" MinWidth=""{TemplateBinding MinWidth}"" Height=""{TemplateBinding Height}"" Padding=""{TemplateBinding Padding}"">
                            <ContentPresenter HorizontalAlignment=""{TemplateBinding HorizontalContentAlignment}"" VerticalAlignment=""{TemplateBinding VerticalContentAlignment}"" RecognizesAccessKey=""True""/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property=""IsMouseOver"" Value=""True"">
                                <Setter TargetName=""Bd"" Property=""Background"" Value=""#1AFFFFFF""/>
                            </Trigger>
                            <Trigger Property=""IsPressed"" Value=""True"">
                                <Setter TargetName=""Bd"" Property=""Background"" Value=""#26FFFFFF""/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        <Style TargetType=""Button"" x:Key=""ToolbarIconButtonStyle"">
            <Setter Property=""Width"" Value=""{DynamicResource ToolbarButtonSize}""/>
            <Setter Property=""Height"" Value=""{DynamicResource ToolbarButtonSize}""/>
            <Setter Property=""Background"" Value=""Transparent""/>
            <Setter Property=""Foreground"" Value=""{DynamicResource ThemeToolbarTextBrush}""/>
            <Setter Property=""BorderBrush"" Value=""Transparent""/>
            <Setter Property=""BorderThickness"" Value=""0""/>
            <Setter Property=""Padding"" Value=""0""/>
            <Setter Property=""Cursor"" Value=""Hand""/>
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""Button"">
                        <Border x:Name=""Bd"" Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""10"" SnapsToDevicePixels=""True"">
                            <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property=""IsMouseOver"" Value=""True"">
                                <Setter TargetName=""Bd"" Property=""Background"" Value=""{DynamicResource ThemeHoverBrush}""/>
                            </Trigger>
                            <Trigger Property=""IsPressed"" Value=""True"">
                                <Setter TargetName=""Bd"" Property=""Background"" Value=""{DynamicResource ThemePressedBrush}""/>
                                <Setter Property=""Foreground"" Value=""{DynamicResource ThemePrimaryTextBrush}""/>
                            </Trigger>
                            <Trigger Property=""IsEnabled"" Value=""False"">
                                <Setter Property=""Opacity"" Value=""0.72""/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        <Style TargetType=""ComboBox"" x:Key=""DarkComboBoxStyle"">
            <Setter Property=""Background"" Value=""{DynamicResource ThemeInputBrush}""/>
            <Setter Property=""Foreground"" Value=""{DynamicResource ThemePrimaryTextBrush}""/>
            <Setter Property=""BorderBrush"" Value=""{DynamicResource ThemeBorderBrush}""/>
            <Setter Property=""BorderThickness"" Value=""1""/>
            <Setter Property=""Padding"" Value=""10,6""/>
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""ComboBox"">
                        <Grid>
                            <ToggleButton x:Name=""ToggleButton"" Focusable=""False"" IsChecked=""{Binding Path=IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}"">
                                <ToggleButton.Template>
                                    <ControlTemplate TargetType=""ToggleButton"">
                                        <Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""1"" CornerRadius=""8"">
                                            <Grid>
                                                <TextBlock Margin=""10,0,30,0"" VerticalAlignment=""Center"" HorizontalAlignment=""Left"" Foreground=""{Binding Foreground, RelativeSource={RelativeSource AncestorType=ComboBox}}"" TextTrimming=""CharacterEllipsis"" Text=""{Binding Path=SelectedItem.Content, RelativeSource={RelativeSource AncestorType=ComboBox}}""/>
                                                <Path Data=""M4,6 L8,10 L12,6"" Stroke=""{DynamicResource ThemeSecondaryTextBrush}"" StrokeThickness=""1.6"" StrokeStartLineCap=""Round"" StrokeEndLineCap=""Round"" StrokeLineJoin=""Round"" Fill=""Transparent"" Width=""16"" Height=""16"" Stretch=""None"" HorizontalAlignment=""Right"" VerticalAlignment=""Center"" Margin=""0,0,10,0""/>
                                            </Grid>
                                        </Border>
                                    </ControlTemplate>
                                </ToggleButton.Template>
                            </ToggleButton>
                            <Popup x:Name=""PART_Popup"" IsOpen=""{TemplateBinding IsDropDownOpen}"" Placement=""Bottom"" AllowsTransparency=""True"" Focusable=""False"" PopupAnimation=""Fade"">
                                <Border Background=""{DynamicResource ThemePanelBrush}"" BorderBrush=""{DynamicResource ThemeBorderBrush}"" BorderThickness=""1"" CornerRadius=""8"" Margin=""0,4,0,0"">
                                    <ScrollViewer MaxHeight=""220"">
                                        <ItemsPresenter/>
                                    </ScrollViewer>
                                </Border>
                            </Popup>
                        </Grid>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        <Style TargetType=""ComboBoxItem"">
            <Setter Property=""Foreground"" Value=""{DynamicResource ThemePrimaryTextBrush}""/>
            <Setter Property=""Background"" Value=""{DynamicResource ThemePanelBrush}""/>
            <Setter Property=""Padding"" Value=""10,8""/>
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""ComboBoxItem"">
                        <Border x:Name=""Bd"" Background=""{TemplateBinding Background}"" CornerRadius=""6"" Padding=""{TemplateBinding Padding}"">
                            <ContentPresenter TextElement.Foreground=""{TemplateBinding Foreground}""/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property=""IsHighlighted"" Value=""True"">
                                <Setter TargetName=""Bd"" Property=""Background"" Value=""{DynamicResource ThemeHoverBrush}""/>
                            </Trigger>
                            <Trigger Property=""IsSelected"" Value=""True"">
                                <Setter TargetName=""Bd"" Property=""Background"" Value=""{DynamicResource ThemeSelectedSurfaceBrush}""/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style TargetType=""Expander"">
            <Setter Property=""Foreground"" Value=""#EEE""/>
            <Setter Property=""Background"" Value=""Transparent""/>
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""Expander"">
                        <Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"">
                            <DockPanel>
                                <ToggleButton x:Name=""HeaderSite"" DockPanel.Dock=""Top"" IsChecked=""{Binding IsExpanded, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}"" Content=""{TemplateBinding Header}"">
                                    <ToggleButton.Template>
                                        <ControlTemplate TargetType=""ToggleButton"">
                                            <Border Background=""Transparent"" Padding=""5"">
                                                <StackPanel Orientation=""Horizontal"">
                                                    <Path x:Name=""Icon"" Data=""M6,4 L10,8 L6,12"" Stroke=""{DynamicResource ThemeSecondaryTextBrush}"" StrokeThickness=""1.6"" StrokeStartLineCap=""Round"" StrokeEndLineCap=""Round"" StrokeLineJoin=""Round"" Fill=""Transparent"" Width=""15"" Height=""16"" Stretch=""None"" VerticalAlignment=""Center""/>
                                                    <ContentPresenter VerticalAlignment=""Center"" TextElement.Foreground=""{DynamicResource ThemePrimaryTextBrush}""/>
                                                </StackPanel>
                                            </Border>
                                            <ControlTemplate.Triggers>
                                                <Trigger Property=""IsChecked"" Value=""True"">
                                                    <Setter TargetName=""Icon"" Property=""Data"" Value=""M4,6 L8,10 L12,6""/>
                                                </Trigger>
                                                <Trigger Property=""IsMouseOver"" Value=""True"">
                                                    <Setter TargetName=""Icon"" Property=""Stroke"" Value=""{DynamicResource ThemePrimaryTextBrush}""/>
                                                </Trigger>
                                            </ControlTemplate.Triggers>
                                        </ControlTemplate>
                                    </ToggleButton.Template>
                                </ToggleButton>
                                <ContentPresenter x:Name=""ExpandSite"" Visibility=""Collapsed"" DockPanel.Dock=""Bottom""/>
                            </DockPanel>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property=""IsExpanded"" Value=""True"">
                                <Setter TargetName=""ExpandSite"" Property=""Visibility"" Value=""Visible""/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </Window.Resources>


    <Border Background=""{DynamicResource ThemeWindowBackgroundBrush}"" CornerRadius=""0"" Margin=""0"">
        <Grid> <!-- Root Wrapper -->
            <Grid x:Name=""MainLayout"">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width=""0"" x:Name=""HistoryCol""/>
                    <ColumnDefinition Width=""*"" MinWidth=""386"" x:Name=""ChatCol""/>
                    <ColumnDefinition Width=""0"" x:Name=""SplitterCol""/>
                    <ColumnDefinition Width=""0"" x:Name=""CodeCol""/>
                </Grid.ColumnDefinitions>
                <Grid.RowDefinitions>
                    <RowDefinition Height=""Auto""/>
                    <RowDefinition Height=""*""/>
                    <RowDefinition Height=""0""/>
                    <RowDefinition Height=""0"" x:Name=""LibraryRow""/>
                </Grid.RowDefinitions>

                <Grid Grid.Row=""0"" Grid.Column=""0"" Grid.ColumnSpan=""4"" x:Name=""ChatToolbar"" Panel.ZIndex=""12"" Margin=""10,6,10,6"" MinHeight=""52"">
                    <StackPanel Orientation=""Horizontal"" HorizontalAlignment=""Left"" VerticalAlignment=""Center"">
                        <Button x:Name=""BtnToggleHistory"" Style=""{StaticResource ToolbarIconButtonStyle}"" ToolTip=""对话历史"">
                            <Viewbox Width=""{DynamicResource ToolbarIconSize}"" Height=""{DynamicResource ToolbarIconSize}"">
                                <Grid Width=""20"" Height=""20"">
                                    <Path Data=""M4.5,4.5 L15.5,4.5 Q17,4.5 17,6 L17,14 Q17,15.5 15.5,15.5 L7.5,15.5 L4,18 L4,6 Q4,4.5 5.5,4.5"" Stroke=""{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"" StrokeThickness=""1.7"" StrokeStartLineCap=""Round"" StrokeEndLineCap=""Round"" StrokeLineJoin=""Round"" Fill=""Transparent""/>
                                    <Path Data=""M8,8.5 L13,8.5 M8,11.5 L12,11.5"" Stroke=""{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"" StrokeThickness=""1.7"" StrokeStartLineCap=""Round"" StrokeEndLineCap=""Round""/>
                                </Grid>
                            </Viewbox>
                        </Button>
                        <Button x:Name=""BtnNewChat"" Style=""{StaticResource ToolbarIconButtonStyle}"" ToolTip=""新对话"" Margin=""6,0,0,0"">
                            <Viewbox Width=""{DynamicResource ToolbarIconSize}"" Height=""{DynamicResource ToolbarIconSize}"">
                                <Grid Width=""20"" Height=""20"">
                                    <Path Data=""M10,4 L10,16 M4,10 L16,10"" Stroke=""{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"" StrokeThickness=""2"" StrokeStartLineCap=""Round"" StrokeEndLineCap=""Round""/>
                                </Grid>
                            </Viewbox>
                        </Button>
                        <TextBlock Text=""Magpie"" Foreground=""{DynamicResource ThemeToolbarTextBrush}"" FontSize=""17"" FontWeight=""SemiBold"" VerticalAlignment=""Center"" Margin=""12,0,0,0""/>
                    </StackPanel>
                    <StackPanel Orientation=""Horizontal"" HorizontalAlignment=""Right"">
                        <Button x:Name=""BtnToggleViewMode"" Content=""JSON"" Foreground=""#B8B8B8"" Background=""#242A32"" BorderThickness=""1"" BorderBrush=""#3A404A"" FontSize=""10"" Padding=""9,5"" Cursor=""Hand"" VerticalAlignment=""Center"" Margin=""0"" Visibility=""Collapsed"">
                            <Button.Template><ControlTemplate TargetType=""Button""><Border Background=""{TemplateBinding Background}"" CornerRadius=""6""><ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/></Border></ControlTemplate></Button.Template>
                        </Button>
                        <Button x:Name=""BtnSettings"" Style=""{StaticResource ToolbarIconButtonStyle}"" ToolTip=""配置"" Margin=""0,0,4,0"">
                            <Viewbox Width=""{DynamicResource ToolbarIconSize}"" Height=""{DynamicResource ToolbarIconSize}"">
                                <Grid Width=""20"" Height=""20"">
                                    <Path Data=""M10,3.5 L11.4,3.5 L11.9,5.4 C12.5,5.6 13.1,5.9 13.6,6.2 L15.4,5.4 L16.1,6.6 L14.8,8 C15,8.6 15.1,9.3 15.1,10 C15.1,10.7 15,11.4 14.8,12 L16.1,13.4 L15.4,14.6 L13.6,13.8 C13.1,14.1 12.5,14.4 11.9,14.6 L11.4,16.5 L8.6,16.5 L8.1,14.6 C7.5,14.4 6.9,14.1 6.4,13.8 L4.6,14.6 L3.9,13.4 L5.2,12 C5,11.4 4.9,10.7 4.9,10 C4.9,9.3 5,8.6 5.2,8 L3.9,6.6 L4.6,5.4 L6.4,6.2 C6.9,5.9 7.5,5.6 8.1,5.4 L8.6,3.5 L10,3.5 Z"" Stroke=""{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"" StrokeThickness=""1.45"" StrokeStartLineCap=""Round"" StrokeEndLineCap=""Round"" StrokeLineJoin=""Round"" Fill=""Transparent""/>
                                    <Ellipse Width=""4.6"" Height=""4.6"" Stroke=""{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"" StrokeThickness=""1.45"" Fill=""Transparent"" HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                                </Grid>
                            </Viewbox>
                        </Button>
                        <Button x:Name=""BtnToggleCode"" Style=""{StaticResource ToolbarIconButtonStyle}"" ToolTip=""切换代码视图"">
                            <Viewbox Width=""{DynamicResource ToolbarIconSize}"" Height=""{DynamicResource ToolbarIconSize}"">
                                <Grid Width=""20"" Height=""20"">
                                    <Path Data=""M3.5,6.5 L8,10 L3.5,13.5"" Stroke=""{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"" StrokeThickness=""1.8"" StrokeStartLineCap=""Round"" StrokeEndLineCap=""Round"" StrokeLineJoin=""Round"" Fill=""Transparent""/>
                                    <Path Data=""M16.5,6.5 L12,10 L16.5,13.5"" Stroke=""{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"" StrokeThickness=""1.8"" StrokeStartLineCap=""Round"" StrokeEndLineCap=""Round"" StrokeLineJoin=""Round"" Fill=""Transparent""/>
                                </Grid>
                            </Viewbox>
                        </Button>
                    </StackPanel>
                </Grid>
                <Border Grid.Row=""0"" Grid.Column=""0"" Grid.ColumnSpan=""4"" x:Name=""ToolbarDivider"" Panel.ZIndex=""11"" Height=""1"" VerticalAlignment=""Bottom"" Background=""{DynamicResource ThemeBorderBrush}"" Visibility=""Collapsed""/>

                <Border x:Name=""HistorySidebar"" Grid.Row=""1"" Grid.Column=""0"" Grid.RowSpan=""3"" Panel.ZIndex=""9"" HorizontalAlignment=""Stretch"" VerticalAlignment=""Stretch"" Visibility=""Collapsed"" Margin=""0"" Background=""{DynamicResource ThemeSurfaceBrush}"" BorderBrush=""{DynamicResource ThemeBorderBrush}"" BorderThickness=""0,0,1,1"" CornerRadius=""0"" ClipToBounds=""True"">
                    <Border.Effect>
                        <DropShadowEffect BlurRadius=""24"" ShadowDepth=""4"" Opacity=""0.35"" Color=""Black""/>
                    </Border.Effect>
                    <Grid Margin=""16,14,14,14"">
                        <Grid.RowDefinitions>
                            <RowDefinition Height=""Auto""/>
                            <RowDefinition Height=""Auto""/>
                            <RowDefinition Height=""*""/>
                        </Grid.RowDefinitions>
                        <Grid>
                            <TextBlock Text=""对话历史"" Foreground=""#EAEAEA"" FontSize=""15"" FontWeight=""SemiBold"" VerticalAlignment=""Center""/>
                            <TextBlock x:Name=""TxtHistoryCount"" Foreground=""#7C7C7C"" FontSize=""11"" Margin=""82,1,0,0"" VerticalAlignment=""Center""/>
                            <Button x:Name=""BtnCloseHistory"" Content=""✕"" HorizontalAlignment=""Right"" Background=""Transparent"" BorderThickness=""0"" Foreground=""#8E8E8E"" Cursor=""Hand"" FontSize=""11"" Width=""24"" Height=""24""/>
                        </Grid>
                        <TextBlock Grid.Row=""1"" Text=""本地保存，点击可恢复会话。"" Foreground=""#707070"" FontSize=""11"" Margin=""0,8,0,12""/>
                        <ScrollViewer Grid.Row=""2"" VerticalScrollBarVisibility=""Auto"" HorizontalScrollBarVisibility=""Disabled"">
                            <StackPanel x:Name=""HistoryListPanel""/>
                        </ScrollViewer>
                    </Grid>
                </Border>

                <ScrollViewer Grid.Row=""1"" Grid.Column=""1"" Grid.RowSpan=""2"" x:Name=""ChatScroll"" HorizontalAlignment=""Center"" Margin=""0,6,0,0"" VerticalScrollBarVisibility=""Auto"" PanningMode=""VerticalOnly"">
                    <StackPanel x:Name=""ChatPanel"" Margin=""14,8,14,0""/>
                </ScrollViewer>

                <Border x:Name=""StickyUserMessageHost"" Grid.Row=""1"" Grid.RowSpan=""2"" Grid.Column=""1"" Panel.ZIndex=""7"" HorizontalAlignment=""Center"" VerticalAlignment=""Top"" Margin=""0,6,0,0"" Padding=""14,8,14,0"" Background=""Transparent"" Visibility=""Collapsed"" IsHitTestVisible=""False"" ClipToBounds=""False"">
                    <Grid x:Name=""StickyUserMessageStack"" ClipToBounds=""False""/>
                </Border>

                <Border Grid.Row=""1"" Grid.RowSpan=""2"" Grid.Column=""1"" Panel.ZIndex=""8"" Background=""{x:Null}"" CornerRadius=""0"" Padding=""14,10,14,18"" x:Name=""InputAreaBorder"" HorizontalAlignment=""Center"" VerticalAlignment=""Bottom"">
                <StackPanel>
                    <TextBlock x:Name=""EmptyChatPrompt"" Text=""要用Magpie创造什么？"" Foreground=""#F3F3F3"" FontSize=""24"" FontWeight=""SemiBold"" TextAlignment=""Center"" HorizontalAlignment=""Center"" Margin=""0,0,0,24""/>

                        <Border x:Name=""InputChromeBorder"" Background=""{DynamicResource ThemeInputBrush}"" BorderBrush=""{DynamicResource ThemeBorderBrush}"" BorderThickness=""1"" CornerRadius=""24"" Padding=""18,14,18,14"" MinHeight=""118"" ClipToBounds=""True"">
                            <Border.Effect>
                                <DropShadowEffect BlurRadius=""28"" ShadowDepth=""0"" Opacity=""0"" Color=""Black""/>
                            </Border.Effect>
                            <Grid>
                                <Grid.RowDefinitions>
                                    <RowDefinition Height=""Auto""/>
                                    <RowDefinition Height=""*""/>
                                    <RowDefinition Height=""Auto""/>
                                </Grid.RowDefinitions>
                        <WrapPanel Grid.Row=""0"" x:Name=""AttachmentPreviewPanel"" Margin=""0,0,0,8"" Visibility=""Collapsed""/>
                        <Border Grid.Row=""1"" Background=""Transparent"" BorderThickness=""0"" Padding=""0"" Margin=""0"">
                            <TextBox x:Name=""TxtInput"" Background=""Transparent"" Foreground=""{DynamicResource ThemePrimaryTextBrush}"" BorderThickness=""0"" Padding=""0,0,0,8"" FontSize=""14"" AcceptsReturn=""True"" VerticalScrollBarVisibility=""Auto"" TextWrapping=""Wrap"" MinHeight=""44"" MaxHeight=""128"" CaretBrush=""{DynamicResource ThemePrimaryTextBrush}"" ToolTip=""可在此处输入；Ctrl+V 粘贴文件或截图即可加入附件""/>
                        </Border>
                        <Grid Grid.Row=""2"" Margin=""0,8,0,0"">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width=""Auto""/>
                                <ColumnDefinition Width=""Auto""/>
                                <ColumnDefinition Width=""Auto""/>
                                <ColumnDefinition Width=""Auto""/>
                                <ColumnDefinition Width=""*""/>
                                <ColumnDefinition Width=""Auto""/>
                                <ColumnDefinition Width=""Auto""/>
                            </Grid.ColumnDefinitions>

                            <Button x:Name=""BtnUploadImage"" Grid.Column=""0"" Style=""{StaticResource IconButtonStyle}"" Content=""+"" Foreground=""#A0A0A0"" Background=""Transparent"" BorderThickness=""0"" FontSize=""18"" FontWeight=""Medium"" Cursor=""Hand"" ToolTip=""添加附件或参考"" Margin=""0,0,6,0"">
                                <Button.ContextMenu>
                                    <ContextMenu Background=""#1E1E1E"" Foreground=""#E0E0E0"" BorderBrush=""#333"" BorderThickness=""1"" Padding=""4"">
                                        <ContextMenu.Template>
                                            <ControlTemplate TargetType=""ContextMenu"">
                                                <Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""4"" Padding=""{TemplateBinding Padding}"">
                                                    <ItemsPresenter/>
                                                </Border>
                                            </ControlTemplate>
                                        </ContextMenu.Template>
                                        <ContextMenu.Resources>
                                            <Style TargetType=""MenuItem"">
                                                <Setter Property=""Foreground"" Value=""#E0E0E0""/>
                                                <Setter Property=""Background"" Value=""Transparent""/>
                                                <Setter Property=""Padding"" Value=""12,8""/>
                                                <Setter Property=""Template"">
                                                    <Setter.Value>
                                                        <ControlTemplate TargetType=""MenuItem"">
                                                            <Border x:Name=""Bg"" Background=""{TemplateBinding Background}"" CornerRadius=""4"">
                                                                <ContentPresenter Content=""{TemplateBinding Header}"" Margin=""{TemplateBinding Padding}""/>
                                                            </Border>
                                                            <ControlTemplate.Triggers>
                                                                <Trigger Property=""IsHighlighted"" Value=""True"">
                                                                    <Setter TargetName=""Bg"" Property=""Background"" Value=""#333333""/>
                                                                </Trigger>
                                                            </ControlTemplate.Triggers>
                                                        </ControlTemplate>
                                                    </Setter.Value>
                                                </Setter>
                                            </Style>
                                        </ContextMenu.Resources>
                                        <MenuItem x:Name=""MenuUploadFile"" Header=""上传文件""/>
                                        <MenuItem x:Name=""MenuCreateReference"" Header=""创建参考""/>
                                    </ContextMenu>
                                </Button.ContextMenu>
                            </Button>
                            <Button x:Name=""BtnStop"" Grid.Column=""1"" Style=""{StaticResource IconButtonStyle}"" Visibility=""Collapsed"" Foreground=""{DynamicResource ThemePrimaryTextBrush}"" Background=""Transparent"" BorderThickness=""0"" Cursor=""Hand"" ToolTip=""停止按钮"" Margin=""0,0,10,0"" Width=""28"" Height=""28"" Padding=""0"">
                                <Grid Width=""28"" Height=""28"">
                                    <Border Width=""11"" Height=""11"" CornerRadius=""1"" Background=""{DynamicResource ThemePrimaryTextBrush}"" HorizontalAlignment=""Center"" VerticalAlignment=""Center"" SnapsToDevicePixels=""True""/>
                                </Grid>
                            </Button>
                            <Button x:Name=""BtnAgentModeDropdown"" Grid.Column=""2"" Style=""{StaticResource IconButtonStyle}"" Content=""Create ▾"" Foreground=""#A0A0A0"" Background=""Transparent"" BorderThickness=""0"" FontSize=""13"" Cursor=""Hand"" ToolTip=""执行模式"" Margin=""0,0,2,0"">
                                <Button.ContextMenu>
                                    <ContextMenu Background=""#1E1E1E"" Foreground=""#E0E0E0"" BorderBrush=""#333"" BorderThickness=""1"" Padding=""4"">
                                        <ContextMenu.Template>
                                            <ControlTemplate TargetType=""ContextMenu"">
                                                <Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""4"" Padding=""{TemplateBinding Padding}"">
                                                    <ItemsPresenter/>
                                                </Border>
                                            </ControlTemplate>
                                        </ContextMenu.Template>
                                        <ContextMenu.Resources>
                                            <Style TargetType=""MenuItem"">
                                                <Setter Property=""Foreground"" Value=""#E0E0E0""/>
                                                <Setter Property=""Background"" Value=""Transparent""/>
                                                <Setter Property=""Padding"" Value=""12,8""/>
                                                <Setter Property=""Template"">
                                                    <Setter.Value>
                                                        <ControlTemplate TargetType=""MenuItem"">
                                                            <Border x:Name=""Bg"" Background=""{TemplateBinding Background}"" CornerRadius=""4"">
                                                                <ContentPresenter Content=""{TemplateBinding Header}"" Margin=""{TemplateBinding Padding}""/>
                                                            </Border>
                                                            <ControlTemplate.Triggers>
                                                                <Trigger Property=""IsHighlighted"" Value=""True"">
                                                                    <Setter TargetName=""Bg"" Property=""Background"" Value=""#333333""/>
                                                                </Trigger>
                                                            </ControlTemplate.Triggers>
                                                        </ControlTemplate>
                                                    </Setter.Value>
                                                </Setter>
                                            </Style>
                                        </ContextMenu.Resources>
                                        <MenuItem x:Name=""MenuAgentModeCreate"" Header=""Create""/>
                                        <MenuItem x:Name=""MenuAgentModePlan"" Header=""Plan""/>
                                        <MenuItem x:Name=""MenuAgentModeSelfTrain"" Header=""自训练""/>
                                    </ContextMenu>
                                </Button.ContextMenu>
                            </Button>
                            <Grid x:Name=""ContextMeterHost"" Grid.Column=""5"" Width=""17"" Height=""17"" Margin=""0,0,10,0"" VerticalAlignment=""Center"" ToolTip=""上下文使用情况"">
                                <Ellipse Stroke=""#4A4A4A"" StrokeThickness=""1.3"" Fill=""Transparent""/>
                                <Path x:Name=""ContextRingProgress"" Stroke=""#D8D8D8"" StrokeThickness=""1.3"" StrokeStartLineCap=""Round"" StrokeEndLineCap=""Round"" Fill=""Transparent""/>
                            </Grid>

                            <Button x:Name=""BtnSend"" Grid.Column=""6"" Foreground=""#222831"" Background=""#E8ECF3"" BorderBrush=""#D6DAE1"" BorderThickness=""1"" FontSize=""11"" Margin=""0"" Width=""22"" Height=""22"" Cursor=""Hand"" VerticalAlignment=""Center"">
                                <Button.Template>
                                    <ControlTemplate TargetType=""Button"">
                                        <Border x:Name=""bg"" Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""11"">
                                        <ContentPresenter x:Name=""cp"" HorizontalAlignment=""Center"" VerticalAlignment=""Center"" Margin=""0""/>
                                        </Border>

                                    </ControlTemplate>
                                </Button.Template>
                            </Button>
                        </Grid>
                            </Grid>
                        </Border>
                    </StackPanel>
                </Border>

            <!-- 电池库扩展区 -->
                <Border Grid.Row=""3"" Grid.Column=""1"" Background=""{DynamicResource ThemeSurfaceBrush}"" BorderBrush=""{DynamicResource ThemeBorderBrush}"" BorderThickness=""0,1,0,0"" x:Name=""LibraryPanel"" CornerRadius=""0"" HorizontalAlignment=""Center"">
                    <Grid Margin=""15"">
                        <Grid.RowDefinitions>
                            <RowDefinition Height=""Auto""/>
                            <RowDefinition Height=""*"" />
                        </Grid.RowDefinitions>

                        <Grid Margin=""0,0,0,12"">
                            <StackPanel Orientation=""Horizontal"" VerticalAlignment=""Center"">
                                <TextBlock Text=""电池库"" Foreground=""#EEE"" FontSize=""15"" FontWeight=""Bold""/>
                                <TextBlock x:Name=""TxtLibCount"" Text="""" Foreground=""#555"" FontSize=""11"" Margin=""8,0,0,0"" VerticalAlignment=""Bottom""/>
                            </StackPanel>
                            <Button x:Name=""BtnRefreshLib"" Content=""同步"" HorizontalAlignment=""Right"" Foreground=""#A0A0A0"" Background=""Transparent"" BorderThickness=""0"" FontSize=""14"" Cursor=""Hand"" ToolTip=""重新同步电池库""/>
                        </Grid>

                        <ScrollViewer Grid.Row=""1"" VerticalScrollBarVisibility=""Auto"" Height=""350"">
                            <StackPanel x:Name=""LibraryContent"" />
                        </ScrollViewer>
                    </Grid>
                </Border>

                <Border Grid.Row=""1"" Grid.Column=""3"" Grid.RowSpan=""2"" x:Name=""CodeViewBorder"" Background=""{DynamicResource ThemeWindowBackgroundBrush}"" CornerRadius=""0"" BorderBrush=""{DynamicResource ThemeBorderBrush}"" BorderThickness=""1,0,0,0"">
                    <Grid ClipToBounds=""True"">
                        <Grid>
                            <Grid x:Name=""CanvasPane"" Background=""{DynamicResource ThemeWindowBackgroundBrush}"">
                                <Border Margin=""0"" Background=""#12161C"" BorderBrush=""Transparent"" BorderThickness=""0"" CornerRadius=""0"">
                                    <Grid x:Name=""CanvasSurfaceHost"">
                                        <Border x:Name=""CanvasStatusHost"" Background=""Transparent"" Padding=""22"">
                                            <StackPanel VerticalAlignment=""Center"" HorizontalAlignment=""Center"" Width=""280"">
                                                <TextBlock Text=""Infinite Canvas"" Foreground=""#ECEFF4"" FontSize=""15"" FontWeight=""SemiBold"" HorizontalAlignment=""Center"" Margin=""0,0,0,8""/>
                                                <TextBlock x:Name=""TxtCanvasStatus"" Text=""准备加载画布工作台..."" Foreground=""#AAB2BF"" FontSize=""12"" TextAlignment=""Center"" TextWrapping=""Wrap""/>
                                            </StackPanel>
                                        </Border>
                                    </Grid>
                                </Border>
                            </Grid>

                            <Grid x:Name=""InspectorPane"" Background=""{DynamicResource ThemeWindowBackgroundBrush}"" Visibility=""Collapsed"">
                                <Grid.RowDefinitions>
                                    <RowDefinition Height=""*""/>
                                    <RowDefinition Height=""Auto""/>
                                </Grid.RowDefinitions>
                                <Border Grid.Row=""0"" Margin=""0"" Background=""Transparent""><RichTextBox x:Name=""RichCodeView"" Background=""Transparent"" Foreground=""#B8B8B8"" BorderThickness=""0"" FontSize=""12"" FontFamily=""Consolas, Monaco, Courier New"" IsReadOnly=""True"" IsDocumentEnabled=""True"" VerticalScrollBarVisibility=""Auto"" HorizontalScrollBarVisibility=""Disabled"" CaretBrush=""#888"" Padding=""16,42,16,12""/></Border>
                                <Border Grid.Row=""1"" x:Name=""CodeCanvasIssuesHost"" Background=""{DynamicResource ThemeSurfaceBrush}"" CornerRadius=""0"" BorderBrush=""{DynamicResource ThemeBorderBrush}"" BorderThickness=""0,1,0,0"" MinHeight=""120""><DockPanel Margin=""15,10,15,12"" LastChildFill=""True""><TextBlock DockPanel.Dock=""Top"" Text=""画布诊断"" Foreground=""{DynamicResource ThemeSecondaryTextBrush}"" FontSize=""11"" FontWeight=""SemiBold"" Margin=""0,0,0,8""/><ScrollViewer VerticalScrollBarVisibility=""Auto"" HorizontalScrollBarVisibility=""Disabled""><TextBox x:Name=""TxtCanvasIssues"" IsReadOnly=""True"" TextWrapping=""Wrap"" AcceptsReturn=""True"" Background=""Transparent"" Foreground=""{DynamicResource ThemePrimaryTextBrush}"" BorderThickness=""0"" FontSize=""12"" Padding=""0"" CaretBrush=""{DynamicResource ThemePrimaryTextBrush}""/></ScrollViewer></DockPanel></Border>
                            </Grid>
                        </Grid>
                        <Button x:Name=""BtnCanvasPaneView"" Content=""Code"" HorizontalAlignment=""Left"" VerticalAlignment=""Top"" Margin=""14,12,0,0"" Padding=""10,5"" Foreground=""#E5E7EB"" Background=""#AA1B2027"" BorderBrush=""#3A404A"" BorderThickness=""1"" Cursor=""Hand"" Panel.ZIndex=""20"">
                            <Button.Template><ControlTemplate TargetType=""Button""><Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""8""><ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/></Border></ControlTemplate></Button.Template>
                        </Button>
                        <Button x:Name=""BtnInspectorPaneView"" Content=""Canvas"" HorizontalAlignment=""Left"" VerticalAlignment=""Top"" Margin=""14,12,0,0"" Padding=""10,5"" Foreground=""#E5E7EB"" Background=""#AA1B2027"" BorderBrush=""#3A404A"" BorderThickness=""1"" Cursor=""Hand"" Panel.ZIndex=""20"" Visibility=""Collapsed"">
                            <Button.Template><ControlTemplate TargetType=""Button""><Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""8""><ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/></Border></ControlTemplate></Button.Template>
                        </Button>
                        <Button x:Name=""BtnCanvasSync"" Content=""Sync"" HorizontalAlignment=""Right"" VerticalAlignment=""Top"" Foreground=""#D0D4DB"" Background=""#AA1B2027"" BorderBrush=""#3A404A"" BorderThickness=""1"" FontSize=""10"" Padding=""9,5"" Cursor=""Hand"" Margin=""0,12,14,0"" Panel.ZIndex=""20"">
                            <Button.Template><ControlTemplate TargetType=""Button""><Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""8""><ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/></Border></ControlTemplate></Button.Template>
                        </Button>
                    </Grid>
                </Border>
                <GridSplitter x:Name=""ChatCodeSplitter"" Grid.Row=""1"" Grid.Column=""2"" Grid.RowSpan=""2"" Width=""4"" HorizontalAlignment=""Stretch"" VerticalAlignment=""Stretch"" ResizeDirection=""Columns"" ResizeBehavior=""PreviousAndNext"" Background=""Transparent"" Cursor=""SizeWE"" Panel.ZIndex=""2"" Visibility=""Collapsed""/>
        </Grid> <!-- End MainLayout Grid -->

    <!-- 配置悬浮层 -->
            <Grid x:Name=""SettingsOverlay"" Grid.ColumnSpan=""4"" Panel.ZIndex=""999"" Margin=""0"" Background=""{DynamicResource ThemeOverlayBrush}"" Visibility=""Collapsed"">
            <Border x:Name=""SettingsPanel"" Background=""{DynamicResource ThemePanelBrush}"" BorderBrush=""{DynamicResource ThemeBorderBrush}"" BorderThickness=""1"" CornerRadius=""14"" MaxWidth=""450"" MaxHeight=""680"" HorizontalAlignment=""Right"" VerticalAlignment=""Top"" Margin=""12,68,18,12"" Padding=""18"">
                <Grid>
                    <Grid.RowDefinitions>
                        <RowDefinition Height=""Auto""/>
                        <RowDefinition Height=""*""/>
                        <RowDefinition Height=""Auto""/>
                    </Grid.RowDefinitions>

                    <StackPanel Grid.Row=""0"" Margin=""0,0,0,12"">
                        <TextBlock Text=""设置"" Foreground=""{DynamicResource ThemePrimaryTextBrush}"" FontSize=""18"" FontWeight=""SemiBold""/>
                        <TextBlock Text=""大模型接入设置、电池库与优先模式。"" Foreground=""{DynamicResource ThemeSecondaryTextBrush}"" FontSize=""11"" Margin=""0,6,0,0""/>
                    </StackPanel>

                    <ScrollViewer Grid.Row=""1"" VerticalScrollBarVisibility=""Auto"" HorizontalScrollBarVisibility=""Disabled"" Padding=""0,0,4,0"">
                        <StackPanel>
                            <Expander Header=""大模型接入设置"" IsExpanded=""True"" Foreground=""#ECECEC"" Background=""#242424"" Margin=""0,0,0,10"">
                                <StackPanel Margin=""0,8,0,0"">
                                    <Border Background=""#242424"" CornerRadius=""10"" Padding=""12"" Margin=""0,0,0,12"" BorderBrush=""#343434"" BorderThickness=""1"">
                                        <StackPanel>
                                            <TextBlock Text=""工作主模型"" Foreground=""#EAEAEA"" FontSize=""13"" FontWeight=""SemiBold"" Margin=""0,0,0,4""/>
                                            <TextBlock Text=""负责主对话、工具调用和 Grasshopper 执行规划。"" Foreground=""#8D96A5"" FontSize=""11"" Margin=""0,0,0,10"" TextWrapping=""Wrap""/>
                                            <ComboBox x:Name=""ComboProvider"" Height=""36"" Margin=""0,0,0,10"" Style=""{StaticResource DarkComboBoxStyle}""/>
                                                <TextBlock Text=""API Base URL"" Foreground=""#A0A0A0"" FontSize=""12"" Margin=""0,0,0,5""/>
                                                <Border Background=""#2A2A2A"" CornerRadius=""8"" Padding=""5"" Margin=""0,0,0,10"">
                                                    <TextBox x:Name=""TxtApiBaseUrl"" Background=""Transparent"" Foreground=""White"" BorderThickness=""0"" FontSize=""13"" Padding=""5"" CaretBrush=""White""/>
                                                </Border>

                                                <TextBlock Text=""API Key"" Foreground=""#A0A0A0"" FontSize=""12"" Margin=""0,0,0,5""/>
                                                <Border Background=""#2A2A2A"" CornerRadius=""8"" Padding=""5"" Margin=""0,0,0,10"">
                                                    <TextBox x:Name=""TxtApiKey"" Background=""Transparent"" Foreground=""White"" BorderThickness=""0"" FontSize=""13"" Padding=""5"" CaretBrush=""White""/>
                                                </Border>

                                                <TextBlock Text=""Model Name"" Foreground=""#A0A0A0"" FontSize=""12"" Margin=""0,0,0,5""/>
                                                <Border Background=""#2A2A2A"" CornerRadius=""8"" Padding=""5"" Margin=""0,0,0,10"">
                                                    <TextBox x:Name=""TxtModel"" Background=""Transparent"" Foreground=""White"" BorderThickness=""0"" FontSize=""13"" Padding=""5"" CaretBrush=""White""/>
                                                </Border>

                                                <Expander Header=""代理与网络"" IsExpanded=""False"" Foreground=""#D6D6D6"" Background=""#242424"">
                                                    <StackPanel Margin=""0,10,0,0"">
                                                        <TextBlock Text=""HTTPS Proxy (可选)"" Foreground=""#A0A0A0"" FontSize=""12"" Margin=""0,0,0,5""/>
                                                        <Border Background=""#2A2A2A"" CornerRadius=""8"" Padding=""5"" Margin=""0,0,0,4"">
                                                            <TextBox x:Name=""TxtProxyUrl"" Background=""Transparent"" Foreground=""White"" BorderThickness=""0"" FontSize=""13"" Padding=""5"" CaretBrush=""White"" ToolTip=""留空时自动尝试 MAGPIE_HTTPS_PROXY / HTTPS_PROXY / HTTP_PROXY 和系统代理""/>
                                                        </Border>
                                                        <TextBlock Text=""留空时自动尝试环境变量和系统代理"" Foreground=""#777777"" FontSize=""11"" Margin=""0,0,0,0""/>
                                                    </StackPanel>
                                                </Expander>
                                        </StackPanel>
                                    </Border>

                                    <Border Background=""#242424"" CornerRadius=""10"" Padding=""12"" Margin=""0,0,0,12"" BorderBrush=""#343434"" BorderThickness=""1"">
                                        <StackPanel>
                                            <TextBlock Text=""视觉模型"" Foreground=""#EAEAEA"" FontSize=""13"" FontWeight=""SemiBold"" Margin=""0,0,0,4""/>
                                            <TextBlock Text=""负责图片理解、视觉审查和图像驱动建模判断。"" Foreground=""#8D96A5"" FontSize=""11"" Margin=""0,0,0,10"" TextWrapping=""Wrap""/>
                                            <ComboBox x:Name=""ComboVisionProvider"" Height=""36"" Margin=""0,0,0,10"" Style=""{StaticResource DarkComboBoxStyle}""/>

                                            <TextBlock Text=""Vision API Base URL"" Foreground=""#A0A0A0"" FontSize=""12"" Margin=""0,0,0,5""/>
                                            <Border Background=""#2A2A2A"" CornerRadius=""8"" Padding=""5"" Margin=""0,0,0,10"">
                                                <TextBox x:Name=""TxtVisionApiBaseUrl"" Background=""Transparent"" Foreground=""White"" BorderThickness=""0"" FontSize=""13"" Padding=""5"" CaretBrush=""White""/>
                                            </Border>

                                            <TextBlock Text=""Vision API Key"" Foreground=""#A0A0A0"" FontSize=""12"" Margin=""0,0,0,5""/>
                                            <Border Background=""#2A2A2A"" CornerRadius=""8"" Padding=""5"" Margin=""0,0,0,10"">
                                                <TextBox x:Name=""TxtVisionApiKey"" Background=""Transparent"" Foreground=""White"" BorderThickness=""0"" FontSize=""13"" Padding=""5"" CaretBrush=""White""/>
                                            </Border>

                                            <TextBlock Text=""Vision Model Name"" Foreground=""#A0A0A0"" FontSize=""12"" Margin=""0,0,0,5""/>
                                            <Border Background=""#2A2A2A"" CornerRadius=""8"" Padding=""5"" Margin=""0,0,0,10"">
                                                <TextBox x:Name=""TxtVisionModel"" Background=""Transparent"" Foreground=""White"" BorderThickness=""0"" FontSize=""13"" Padding=""5"" CaretBrush=""White""/>
                                            </Border>

                                            <Expander Header=""视觉代理"" IsExpanded=""False"" Foreground=""#D6D6D6"" Background=""#242424"">
                                                <StackPanel Margin=""0,10,0,0"">
                                                    <TextBlock Text=""Vision HTTPS Proxy (可选)"" Foreground=""#A0A0A0"" FontSize=""12"" Margin=""0,0,0,5""/>
                                                    <Border Background=""#2A2A2A"" CornerRadius=""8"" Padding=""5"">
                                                        <TextBox x:Name=""TxtVisionProxyUrl"" Background=""Transparent"" Foreground=""White"" BorderThickness=""0"" FontSize=""13"" Padding=""5"" CaretBrush=""White""/>
                                                    </Border>
                                                </StackPanel>
                                            </Expander>
                                        </StackPanel>
                                    </Border>

                                    <Border Background=""#242424"" CornerRadius=""10"" Padding=""12"" BorderBrush=""#343434"" BorderThickness=""1"">
                                        <StackPanel>
                                            <TextBlock Text=""生图模型"" Foreground=""#EAEAEA"" FontSize=""13"" FontWeight=""SemiBold"" Margin=""0,0,0,4""/>
                                            <TextBlock Text=""负责画布 AI Image、文生图、图生图和改图工作流。"" Foreground=""#8D96A5"" FontSize=""11"" Margin=""0,0,0,10"" TextWrapping=""Wrap""/>
                                            <ComboBox x:Name=""ComboImageProvider"" Height=""36"" Margin=""0,0,0,10"" Style=""{StaticResource DarkComboBoxStyle}""/>

                                            <TextBlock Text=""Image API Base URL"" Foreground=""#A0A0A0"" FontSize=""12"" Margin=""0,0,0,5""/>
                                            <Border Background=""#2A2A2A"" CornerRadius=""8"" Padding=""5"" Margin=""0,0,0,10"">
                                                <TextBox x:Name=""TxtImageApiBaseUrl"" Background=""Transparent"" Foreground=""White"" BorderThickness=""0"" FontSize=""13"" Padding=""5"" CaretBrush=""White""/>
                                            </Border>

                                            <TextBlock Text=""Image API Key"" Foreground=""#A0A0A0"" FontSize=""12"" Margin=""0,0,0,5""/>
                                            <Border Background=""#2A2A2A"" CornerRadius=""8"" Padding=""5"" Margin=""0,0,0,10"">
                                                <TextBox x:Name=""TxtImageApiKey"" Background=""Transparent"" Foreground=""White"" BorderThickness=""0"" FontSize=""13"" Padding=""5"" CaretBrush=""White""/>
                                            </Border>

                                            <TextBlock Text=""Image Model Name"" Foreground=""#A0A0A0"" FontSize=""12"" Margin=""0,0,0,5""/>
                                            <Border Background=""#2A2A2A"" CornerRadius=""8"" Padding=""5"" Margin=""0,0,0,10"">
                                                <TextBox x:Name=""TxtImageModel"" Background=""Transparent"" Foreground=""White"" BorderThickness=""0"" FontSize=""13"" Padding=""5"" CaretBrush=""White""/>
                                            </Border>

                                            <Expander Header=""图片生成代理"" IsExpanded=""False"" Foreground=""#D6D6D6"" Background=""#242424"">
                                                <StackPanel Margin=""0,10,0,0"">
                                                    <TextBlock Text=""Image HTTPS Proxy (可选)"" Foreground=""#A0A0A0"" FontSize=""12"" Margin=""0,0,0,5""/>
                                                    <Border Background=""#2A2A2A"" CornerRadius=""8"" Padding=""5"">
                                                        <TextBox x:Name=""TxtImageProxyUrl"" Background=""Transparent"" Foreground=""White"" BorderThickness=""0"" FontSize=""13"" Padding=""5"" CaretBrush=""White""/>
                                                    </Border>
                                                </StackPanel>
                                            </Expander>
                                        </StackPanel>
                                    </Border>
                                </StackPanel>
                            </Expander>

                            <Expander Header=""电池库"" IsExpanded=""False"" Foreground=""#ECECEC"" Background=""#242424"" Margin=""0,0,0,10"">
                                <Border Background=""#242424"" CornerRadius=""10"" Padding=""12"" BorderBrush=""#343434"" BorderThickness=""1"">
                                    <StackPanel>
                                        <Button x:Name=""BtnToggleLibrary"" Content=""打开/收起电池库"" Background=""#2E2E2E"" Foreground=""#E8E8E8"" BorderBrush=""#444444"" BorderThickness=""1"" Height=""34"" FontSize=""12"" Cursor=""Hand"" Margin=""0,0,0,12"">
                                            <Button.Template>
                                                <ControlTemplate TargetType=""Button"">
                                                    <Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""9"">
                                                        <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                                                    </Border>
                                                </ControlTemplate>
                                            </Button.Template>
                                        </Button>
                                        <TextBlock Text=""电池库存储路径"" Foreground=""#A0A0A0"" FontSize=""12"" Margin=""0,0,0,5""/>
                                        <Grid Margin=""0,0,0,0"">
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width=""*""/>
                                                <ColumnDefinition Width=""10""/>
                                                <ColumnDefinition Width=""Auto""/>
                                            </Grid.ColumnDefinitions>
                                            <Border Grid.Column=""0"" Background=""#2A2A2A"" CornerRadius=""8"" Padding=""5"">
                                                <TextBox x:Name=""TxtLibraryPath"" Background=""Transparent"" Foreground=""White"" BorderThickness=""0"" FontSize=""13"" Padding=""5"" CaretBrush=""White"" IsReadOnly=""True""/>
                                            </Border>
                                            <Button x:Name=""BtnBrowseLibraryPath"" Grid.Column=""2"" Content=""浏览"" Background=""#444444"" Foreground=""White"" Width=""70"" Height=""32"" FontWeight=""SemiBold"">
                                                <Button.Template>
                                                    <ControlTemplate TargetType=""Button"">
                                                        <Border Background=""{TemplateBinding Background}"" CornerRadius=""8"">
                                                            <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                                                        </Border>
                                                    </ControlTemplate>
                                                </Button.Template>
                                            </Button>
                                        </Grid>
                                    </StackPanel>
                                </Border>
                            </Expander>

                            <Expander Header=""参考"" IsExpanded=""False"" Foreground=""#ECECEC"" Background=""#242424"" Margin=""0,0,0,10"">
                                <Border Background=""#242424"" CornerRadius=""10"" Padding=""12"" BorderBrush=""#343434"" BorderThickness=""1"">
                                    <StackPanel>
                                        <Button x:Name=""BtnMyReferences"" Content=""我的参考"" Background=""#2E2E2E"" Foreground=""#E8E8E8"" BorderBrush=""#444444"" BorderThickness=""1"" Height=""34"" FontSize=""12"" Cursor=""Hand"">
                                            <Button.Template>
                                                <ControlTemplate TargetType=""Button"">
                                                    <Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""9"">
                                                        <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                                                    </Border>
                                                </ControlTemplate>
                                            </Button.Template>
                                        </Button>
                                    </StackPanel>
                                </Border>
                            </Expander>

                            <Expander Header=""优先模式"" IsExpanded=""True"" Foreground=""#ECECEC"" Background=""#242424"" Margin=""0,0,0,4"">
                                <Border Background=""#242424"" CornerRadius=""10"" Padding=""12"" BorderBrush=""#343434"" BorderThickness=""1"">
                                    <Grid Background=""Transparent"">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width=""*""/>
                                            <ColumnDefinition Width=""*""/>
                                            <ColumnDefinition Width=""*""/>
                                        </Grid.ColumnDefinitions>
                                        <Button x:Name=""BtnModeBattery"" Grid.Column=""0"" Content=""电池模式"" Background=""#1E1E1E"" Foreground=""#A0A0A0"" BorderBrush=""#3A3A3A"" BorderThickness=""1"" Height=""34"" FontSize=""12"" Cursor=""Hand"">
                                            <Button.Template><ControlTemplate TargetType=""Button""><Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""9,0,0,9""><ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center"" TextElement.Foreground=""{TemplateBinding Foreground}""/></Border></ControlTemplate></Button.Template>
                                        </Button>
                                        <Button x:Name=""BtnModeMixed"" Grid.Column=""1"" Content=""混合模式"" Background=""#1E1E1E"" Foreground=""#A0A0A0"" BorderBrush=""#3A3A3A"" BorderThickness=""0,1,0,1"" Height=""34"" FontSize=""12"" Cursor=""Hand"">
                                            <Button.Template><ControlTemplate TargetType=""Button""><Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}""><ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center"" TextElement.Foreground=""{TemplateBinding Foreground}""/></Border></ControlTemplate></Button.Template>
                                        </Button>
                                        <Button x:Name=""BtnModeCSharp"" Grid.Column=""2"" Content=""C# 优先"" Background=""#1E1E1E"" Foreground=""#A0A0A0"" BorderBrush=""#3A3A3A"" BorderThickness=""1"" Height=""34"" FontSize=""12"" Cursor=""Hand"">
                                            <Button.Template><ControlTemplate TargetType=""Button""><Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""0,9,9,0""><ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center"" TextElement.Foreground=""{TemplateBinding Foreground}""/></Border></ControlTemplate></Button.Template>
                                        </Button>
                                    </Grid>
                                </Border>
                            </Expander>

                            <Expander Header=""界面显示"" IsExpanded=""True"" Foreground=""#ECECEC"" Background=""#242424"" Margin=""0,0,0,4"">
                                <Border Background=""#242424"" CornerRadius=""10"" Padding=""12"" BorderBrush=""#343434"" BorderThickness=""1"">
                                    <StackPanel>
                                        <TextBlock Text=""外观主题"" Foreground=""#A0A0A0"" FontSize=""12"" Margin=""0,0,0,8""/>
                                        <Grid Background=""Transparent"" Margin=""0,0,0,12"">
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width=""*""/>
                                                <ColumnDefinition Width=""*""/>
                                                <ColumnDefinition Width=""*""/>
                                            </Grid.ColumnDefinitions>
                                            <Button x:Name=""BtnThemeDark"" Grid.Column=""0"" Content=""暗色"" Background=""#1E1E1E"" Foreground=""#A0A0A0"" BorderBrush=""#3A3A3A"" BorderThickness=""1"" Height=""34"" FontSize=""12"" Cursor=""Hand"">
                                                <Button.Template><ControlTemplate TargetType=""Button""><Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""9,0,0,9""><ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center"" TextElement.Foreground=""{TemplateBinding Foreground}""/></Border></ControlTemplate></Button.Template>
                                            </Button>
                                            <Button x:Name=""BtnThemeLight"" Grid.Column=""1"" Content=""明色"" Background=""#1E1E1E"" Foreground=""#A0A0A0"" BorderBrush=""#3A3A3A"" BorderThickness=""0,1,0,1"" Height=""34"" FontSize=""12"" Cursor=""Hand"">
                                                <Button.Template><ControlTemplate TargetType=""Button""><Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}""><ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center"" TextElement.Foreground=""{TemplateBinding Foreground}""/></Border></ControlTemplate></Button.Template>
                                            </Button>
                                            <Button x:Name=""BtnThemeSystem"" Grid.Column=""2"" Content=""系统跟随"" Background=""#1E1E1E"" Foreground=""#A0A0A0"" BorderBrush=""#3A3A3A"" BorderThickness=""1"" Height=""34"" FontSize=""12"" Cursor=""Hand"">
                                                <Button.Template><ControlTemplate TargetType=""Button""><Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""0,9,9,0""><ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center"" TextElement.Foreground=""{TemplateBinding Foreground}""/></Border></ControlTemplate></Button.Template>
                                            </Button>
                                        </Grid>
                                        <TextBlock Text=""对话输入与输出字号"" Foreground=""#A0A0A0"" FontSize=""12"" Margin=""0,0,0,8""/>
                                        <Grid Background=""Transparent"">
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width=""*""/>
                                                <ColumnDefinition Width=""*""/>
                                            </Grid.ColumnDefinitions>
                                            <Button x:Name=""BtnDisplayNormal"" Grid.Column=""0"" Content=""正常显示"" Background=""#1E1E1E"" Foreground=""#A0A0A0"" BorderBrush=""#3A3A3A"" BorderThickness=""1"" Height=""34"" FontSize=""12"" Cursor=""Hand"">
                                                <Button.Template><ControlTemplate TargetType=""Button""><Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""9,0,0,9""><ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center"" TextElement.Foreground=""{TemplateBinding Foreground}""/></Border></ControlTemplate></Button.Template>
                                            </Button>
                                            <Button x:Name=""BtnDisplayLarge"" Grid.Column=""1"" Content=""放大显示"" Background=""#1E1E1E"" Foreground=""#A0A0A0"" BorderBrush=""#3A3A3A"" BorderThickness=""1"" Height=""34"" FontSize=""12"" Cursor=""Hand"">
                                                <Button.Template><ControlTemplate TargetType=""Button""><Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""0,9,9,0""><ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center"" TextElement.Foreground=""{TemplateBinding Foreground}""/></Border></ControlTemplate></Button.Template>
                                            </Button>
                                        </Grid>
                                    </StackPanel>
                                </Border>
                            </Expander>
                        </StackPanel>
                    </ScrollViewer>

                    <Grid Grid.Row=""2"" Margin=""0,14,0,0"">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width=""*""/>
                            <ColumnDefinition Width=""10""/>
                            <ColumnDefinition Width=""*""/>
                        </Grid.ColumnDefinitions>
                        <Button x:Name=""BtnCancelSettings"" Grid.Column=""0"" Content=""取消"" Background=""#333333"" Foreground=""White"" Height=""36"" FontWeight=""SemiBold"">
                            <Button.Template>
                                <ControlTemplate TargetType=""Button"">
                                    <Border Background=""{TemplateBinding Background}"" CornerRadius=""18"">
                                        <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                                    </Border>
                                </ControlTemplate>
                            </Button.Template>
                        </Button>
                        <Button x:Name=""BtnSaveSettings"" Grid.Column=""2"" Content=""保存并关闭"" Background=""White"" Foreground=""Black"" Height=""36"" FontWeight=""SemiBold"">
                            <Button.Template>
                                <ControlTemplate TargetType=""Button"">
                                    <Border Background=""{TemplateBinding Background}"" CornerRadius=""18"">
                                        <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                                    </Border>
                                </ControlTemplate>
                            </Button.Template>
                        </Button>
                    </Grid>
                </Grid>
            </Border>
            </Grid> <!-- End SettingsOverlay -->
        </Grid> <!-- End Root Wrapper -->
    </Border>
</Window>
";
            try
            {
                _window = (Window)XamlReader.Parse(xaml);
                _window.Title = string.Empty;
                _window.Icon = CreateTransparentWindowIcon();
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("界面加载失败: " + ex.Message);
                return;
            }

            _window.Closed += (s, e) =>
            {
                if (_ballWindow != null)
                {
                    _ballWindow.Close();
                    _ballWindow = null;
                }
                ShutdownPlugin();
                _window = null;
            };
            _window.SourceInitialized += (s, e) => ApplySystemTitleBarTheme();
            _window.SizeChanged += (s, e) => UpdateSettingsPanelBounds();
            InitializeFloatingScrollbars();

            _chatPanel = (StackPanel)_window.FindName("ChatPanel");
            _chatScroll = (ScrollViewer)_window.FindName("ChatScroll");
            if (_chatScroll != null)
            {
                _chatScroll.ScrollChanged += (s, e) => UpdateStickyUserMessage(e.VerticalChange);
                _chatScroll.PreviewMouseWheel += SmoothScrollViewer_PreviewMouseWheel;
                _chatScroll.SetValue(SmoothVerticalOffsetProperty, _chatScroll.VerticalOffset);
            }
            _chatToolbar = (FrameworkElement)_window.FindName("ChatToolbar");
            _toolbarDivider = (Border)_window.FindName("ToolbarDivider");
            _stickyUserMessageHost = (Border)_window.FindName("StickyUserMessageHost");
            _stickyUserMessageStack = (Grid)_window.FindName("StickyUserMessageStack");
            _txtInput = (TextBox)_window.FindName("TxtInput");
            _inputChromeBorder = (Border)_window.FindName("InputChromeBorder");
            _btnSend = (Button)_window.FindName("BtnSend");
            ApplySendButtonChrome(_isGenerating);
            _btnAgentModeDropdown = (Button)_window.FindName("BtnAgentModeDropdown");
            _btnModeDropdown = (Button)_window.FindName("BtnModeDropdown");
            _btnModeBattery = (Button)_window.FindName("BtnModeBattery");
            _btnModeCSharp = (Button)_window.FindName("BtnModeCSharp");
            _btnModeMixed = (Button)_window.FindName("BtnModeMixed");
            _btnDisplayNormal = (Button)_window.FindName("BtnDisplayNormal");
            _btnDisplayLarge = (Button)_window.FindName("BtnDisplayLarge");
            _btnThemeDark = (Button)_window.FindName("BtnThemeDark");
            _btnThemeLight = (Button)_window.FindName("BtnThemeLight");
            _btnThemeSystem = (Button)_window.FindName("BtnThemeSystem");
            _menuAgentModeCreate = (MenuItem)_window.FindName("MenuAgentModeCreate");
            _menuAgentModePlan = (MenuItem)_window.FindName("MenuAgentModePlan");
            _menuAgentModeSelfTrain = (MenuItem)_window.FindName("MenuAgentModeSelfTrain");
            _menuModeBattery = (MenuItem)_window.FindName("MenuModeBattery");
            _menuModeCSharp = (MenuItem)_window.FindName("MenuModeCSharp");
            _menuModeMixed = (MenuItem)_window.FindName("MenuModeMixed");
            _layoutMode = ReadLayoutModeSetting();
            _agentMode = ReadAgentModeSetting();
            _displayMode = ReadDisplayModeSetting();
            _themeMode = ReadThemeModeSetting();
            if (_btnAgentModeDropdown != null) {
                _btnAgentModeDropdown.Click += (s, e) => {
                    if (_btnAgentModeDropdown.ContextMenu != null) {
                        _btnAgentModeDropdown.ContextMenu.PlacementTarget = _btnAgentModeDropdown;
                        _btnAgentModeDropdown.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                        _btnAgentModeDropdown.ContextMenu.IsOpen = true;
                    }
                };
            }
            if (_btnModeDropdown != null) {
                _btnModeDropdown.Click += (s, e) => {
                    if (_btnModeDropdown.ContextMenu != null) {
                        _btnModeDropdown.ContextMenu.PlacementTarget = _btnModeDropdown;
                        _btnModeDropdown.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                        _btnModeDropdown.ContextMenu.IsOpen = true;
                    }
                };
            }
            if (_menuAgentModeCreate != null) _menuAgentModeCreate.Click += (s, e) => SetAgentMode(AgentMode.Create);
            if (_menuAgentModePlan != null) _menuAgentModePlan.Click += (s, e) => SetAgentMode(AgentMode.Plan);
            if (_menuAgentModeSelfTrain != null) _menuAgentModeSelfTrain.Click += (s, e) => SetAgentMode(AgentMode.SelfTrain);
            if (_btnModeBattery != null) _btnModeBattery.Click += (s, e) => SetLayoutMode(LayoutMode.Battery);
            if (_btnModeMixed != null) _btnModeMixed.Click += (s, e) => SetLayoutMode(LayoutMode.Mixed);
            if (_btnModeCSharp != null) _btnModeCSharp.Click += (s, e) => SetLayoutMode(LayoutMode.CSharpFirst);
            if (_btnDisplayNormal != null) _btnDisplayNormal.Click += (s, e) => SetDisplayMode(DisplayMode.Normal);
            if (_btnDisplayLarge != null) _btnDisplayLarge.Click += (s, e) => SetDisplayMode(DisplayMode.Large);
            if (_btnThemeDark != null) _btnThemeDark.Click += (s, e) => SetThemeMode(ThemeMode.Dark);
            if (_btnThemeLight != null) _btnThemeLight.Click += (s, e) => SetThemeMode(ThemeMode.Light);
            if (_btnThemeSystem != null) _btnThemeSystem.Click += (s, e) => SetThemeMode(ThemeMode.System);
            if (_menuModeBattery != null) _menuModeBattery.Click += (s, e) => SetLayoutMode(LayoutMode.Battery);
            if (_menuModeMixed != null) _menuModeMixed.Click += (s, e) => SetLayoutMode(LayoutMode.Mixed);
            if (_menuModeCSharp != null) _menuModeCSharp.Click += (s, e) => SetLayoutMode(LayoutMode.CSharpFirst);
            UpdateAgentModeButtons();
            UpdateLayoutModeButtons();
            ApplyDisplayMode();
            ApplyThemeMode();
            _historySidebar = (Border)_window.FindName("HistorySidebar");
            _historyListPanel = (StackPanel)_window.FindName("HistoryListPanel");
            _historyCountText = (TextBlock)_window.FindName("TxtHistoryCount");
            _btnToggleHistory = (Button)_window.FindName("BtnToggleHistory");
            if (false && _btnToggleHistory != null)
            {
                _btnToggleHistory.Width = 34;
                _btnToggleHistory.Height = 30;
                _btnToggleHistory.Padding = new Thickness(0);
                _btnToggleHistory.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
                    <ControlTemplate TargetType=""Button"" xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
                        <Border Background=""{TemplateBinding Background}"" CornerRadius=""6"" Padding=""4,3"">
                            <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                        </Border>
                    </ControlTemplate>");
                _btnToggleHistory.Content = new TextBlock
                {
                    Text = "↻",
                    Foreground = Brushes.White,
                    FontSize = 16,
                    FontFamily = new FontFamily("Segoe UI Symbol"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            _contextMeterHost = (Grid)_window.FindName("ContextMeterHost");
            _contextRingProgress = (WpfPath)_window.FindName("ContextRingProgress");

            var btnCloseHistory = (Button)_window.FindName("BtnCloseHistory");
            if (btnCloseHistory != null) btnCloseHistory.Content = CreateCloseGlyph();
            if (_btnToggleHistory != null) _btnToggleHistory.Click += (s, e) => ToggleHistorySidebar();
            if (btnCloseHistory != null) btnCloseHistory.Click += (s, e) => SetHistorySidebarVisible(false);
            LoadChatHistoryStore();
            RefreshHistorySidebar();

            _codeViewBorder = (Border)_window.FindName("CodeViewBorder");
            _codeCanvasIssuesHost = (Border)_window.FindName("CodeCanvasIssuesHost");
            _txtCanvasIssues = (TextBox)_window.FindName("TxtCanvasIssues");
            _richCodeView = (RichTextBox)_window.FindName("RichCodeView");
            if (_richCodeView != null)
                _richCodeView.SizeChanged += (s, ev) => SyncFlowDocumentPageWidthToViewport(_richCodeView);
            _btnToggleViewMode = (Button)_window.FindName("BtnToggleViewMode");
            InitializeCanvasWorkbenchBindings();
            MoveViewModeButtonIntoWorkbenchOverlay();
            _historyCol = (ColumnDefinition)_window.FindName("HistoryCol");
            _chatCol = (ColumnDefinition)_window.FindName("ChatCol");
            _splitterCol = (ColumnDefinition)_window.FindName("SplitterCol");
            _codeCol = (ColumnDefinition)_window.FindName("CodeCol");
            _chatCodeSplitter = (GridSplitter)_window.FindName("ChatCodeSplitter");
            if (_chatCodeSplitter != null)
            {
                _chatCodeSplitter.DragDelta += (s, ev) => ScheduleChatContentWidthUpdate();
                _chatCodeSplitter.DragCompleted += (s, ev) => ScheduleChatContentWidthUpdate();
            }
            _window.SizeChanged += (s, e) =>
            {
                ScheduleChatContentWidthUpdate();
                if (!_isHistorySidebarVisible) return;
                ApplyHistorySidebarLayout();
                UpdateWindowMinWidthForVisiblePanes();
            };
            var btnToggleCode = (Button)_window.FindName("BtnToggleCode");

            _inputAreaBorder = (Border)_window.FindName("InputAreaBorder");
            _emptyChatPrompt = (TextBlock)_window.FindName("EmptyChatPrompt");
            if (_inputAreaBorder != null)
                _inputAreaBorder.SizeChanged += (s, ev) =>
                {
                    SyncCodeIssuesStripHeightToInputArea();
                    UpdateChatBottomInset();
                };
            UpdateEmptyChatLayout();

            if (btnToggleCode != null) {
            btnToggleCode.Click += (s, e) => {
                _isCodeVisible = !_isCodeVisible;
                if (_isCodeVisible) {
                        if (_chatCol != null) _chatCol.MinWidth = ChatPaneMinWidth;
                        if (_splitterCol != null) _splitterCol.Width = new GridLength(4);
                        if (_codeCol != null) {
                            _codeCol.MinWidth = CodePaneMinWidth;
                            _codeCol.Width = new GridLength(2, GridUnitType.Star);
                        }
                    if (_btnToggleViewMode != null) _btnToggleViewMode.Visibility = Visibility.Visible;
                    UpdateToolbarDividerVisibility();
                    _widthBeforeCodeView = _window.ActualWidth > 0 ? _window.ActualWidth : _window.Width;
                    UpdateWindowMinWidthForVisiblePanes();
                    double desiredWidth = DefaultWindowWidth + CodeViewColumnWidth;
                    double maxWorkAreaWidth = SystemParameters.WorkArea.Width;
                    if (_window.Width < desiredWidth)
                        _window.Width = Math.Min(desiredWidth, Math.Max(_window.MinWidth, maxWorkAreaWidth));
                    if (_isHistorySidebarVisible) ApplyHistorySidebarLayout();
                    UpdateChatContentWidth();
                    ScheduleChatContentWidthUpdate();
                    AnimatePanelIn(_codeViewBorder, 24);
                    if (_inputAreaBorder != null) _inputAreaBorder.CornerRadius = new CornerRadius(0);
                    StartGrasshopperCodeSurfaceHooks();
                    SyncCodeIssuesStripHeightToInputArea();
                    UpdateCodeView();
                    RefreshCanvasWorkbenchViewState();
                } else {
                    if (_btnToggleViewMode != null) _btnToggleViewMode.Visibility = Visibility.Collapsed;
                    UpdateToolbarDividerVisibility();
                    AnimatePanelOut(_codeViewBorder, 24, () =>
                    {
                        if (_chatCol != null) {
                            _chatCol.MinWidth = ChatPaneMinWidth;
                            _chatCol.Width = new GridLength(1, GridUnitType.Star);
                        }
                        if (_codeCol != null) {
                            _codeCol.MinWidth = 0;
                            _codeCol.Width = new GridLength(0);
                        }
                        if (_splitterCol != null) _splitterCol.Width = new GridLength(0);
                        UpdateWindowMinWidthForVisiblePanes();
                        if (!double.IsNaN(_widthBeforeCodeView) && _widthBeforeCodeView >= _window.MinWidth)
                            _window.Width = _widthBeforeCodeView;
                        _widthBeforeCodeView = double.NaN;
                        if (_isHistorySidebarVisible) ApplyHistorySidebarLayout();
                        UpdateChatContentWidth();
                        ScheduleChatContentWidthUpdate();
                        if (_inputAreaBorder != null) _inputAreaBorder.CornerRadius = new CornerRadius(0);
                        UpdateToolbarDividerVisibility();
                    });
                }
            };
            }

            _window.Loaded += (s, ev) =>
            {
                UpdateChatContentWidth();
                UpdateChatBottomInset();
                StartGrasshopperCodeSurfaceHooks();
                SyncCodeIssuesStripHeightToInputArea();
                RefreshCanvasWorkbenchViewState();
            };

            if (_btnToggleViewMode != null) {
            _btnToggleViewMode.Click += (s, e) => {
                _isJsonMode = !_isJsonMode;
                _btnToggleViewMode.Content = _isJsonMode ? "JSON" : "RAW";
                UpdateCodeView();
            };
            }

            var btnSettings = (Button)_window.FindName("BtnSettings");
            _settingsOverlay = (Grid)_window.FindName("SettingsOverlay");
            _settingsPanel = (Border)_window.FindName("SettingsPanel");
            ApplyThemeMode();
            _txtApiKey = (TextBox)_window.FindName("TxtApiKey");
            _comboProvider = (ComboBox)_window.FindName("ComboProvider");
            _comboVisionProvider = (ComboBox)_window.FindName("ComboVisionProvider");
            _comboImageProvider = (ComboBox)_window.FindName("ComboImageProvider");
            _txtApiBaseUrl = (TextBox)_window.FindName("TxtApiBaseUrl");
            _txtModel = (TextBox)_window.FindName("TxtModel");
            _txtProxyUrl = (TextBox)_window.FindName("TxtProxyUrl");
            _txtVisionApiKey = (TextBox)_window.FindName("TxtVisionApiKey");
            _txtVisionApiBaseUrl = (TextBox)_window.FindName("TxtVisionApiBaseUrl");
            _txtVisionModel = (TextBox)_window.FindName("TxtVisionModel");
            _txtVisionProxyUrl = (TextBox)_window.FindName("TxtVisionProxyUrl");
            _txtImageApiKey = (TextBox)_window.FindName("TxtImageApiKey");
            _txtImageApiBaseUrl = (TextBox)_window.FindName("TxtImageApiBaseUrl");
            _txtImageModel = (TextBox)_window.FindName("TxtImageModel");
            _txtImageProxyUrl = (TextBox)_window.FindName("TxtImageProxyUrl");
            _attachmentPreviewPanel = (WrapPanel)_window.FindName("AttachmentPreviewPanel");
            var txtLibraryPath = (TextBox)_window.FindName("TxtLibraryPath");
            PopulateProviderCombo();

            if (_comboProvider != null) {
                _comboProvider.SelectionChanged += (s, e) => {
                    if (_isLoadingProviderSettings) return;
                    LoadProviderSettingsToUI(GetSelectedProviderId());
                };
            }

            if (_comboVisionProvider != null) {
                _comboVisionProvider.SelectionChanged += (s, e) => {
                    if (_isLoadingProviderSettings) return;
                    LoadVisionProviderSettingsToUI(GetSelectedVisionProviderId());
                };
            }

            if (_comboImageProvider != null) {
                _comboImageProvider.SelectionChanged += (s, e) => {
                    if (_isLoadingProviderSettings) return;
                    LoadImageProviderSettingsToUI(GetSelectedImageProviderId());
                };
            }

            if (btnSettings != null) {
            btnSettings.Click += (s, e) => {
                    string providerId = GetCurrentProviderId();
                    SelectProviderComboItem(providerId);
                    SelectVisionProviderComboItem(GetCurrentVisionProviderId());
                    SelectImageProviderComboItem(GetCurrentImageProviderId());
                    LoadProviderSettingsToUI(providerId);
                    LoadVisionProviderSettingsToUI(GetCurrentVisionProviderId());
                    LoadImageProviderSettingsToUI(GetCurrentImageProviderId());
                    if (txtLibraryPath != null) txtLibraryPath.Text = Grasshopper.Instances.Settings.GetValue("Library_Path", "");
                    SetSettingsOverlayVisible(true);
                };
            }

            var btnBrowseLibraryPath = (Button)_window.FindName("BtnBrowseLibraryPath");
            if (btnBrowseLibraryPath != null) {
            btnBrowseLibraryPath.Click += (s, e) => {
                var folderDialog = new System.Windows.Forms.FolderBrowserDialog {
                    Description = "选择电池库存储路径",
                    ShowNewFolderButton = true
                };
                if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
                        if (txtLibraryPath != null) txtLibraryPath.Text = folderDialog.SelectedPath;
                    }
                };
            }

            var btnSaveSettings = (Button)_window.FindName("BtnSaveSettings");
            if (btnSaveSettings != null) {
                btnSaveSettings.Click += (s, e) => {
                    SaveSelectedProviderSettings();
                    SaveSelectedVisionProviderSetting();
                    SaveSelectedImageProviderSettings();
                    if (txtLibraryPath != null) Grasshopper.Instances.Settings.SetValue("Library_Path", txtLibraryPath.Text);
                    SetSettingsOverlayVisible(false);
                };
            }

            var btnCancelSettings = (Button)_window.FindName("BtnCancelSettings");
            if (btnCancelSettings != null) {
                btnCancelSettings.Click += (s, e) => {
                    SetSettingsOverlayVisible(false);
                };
            }

            if (_btnSend != null) _btnSend.Click += BtnSend_Click;

            if (_txtInput != null) {
                _txtInput.AllowDrop = true;
                _txtInput.TextChanged += (s, e) => UpdateInputHeight();
                _txtInput.PreviewKeyDown += (s, e) => {
                    if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && TryConsumeClipboardImageAsAttachment())
                    {
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) {
                        e.Handled = true;
                        int caret = _txtInput.CaretIndex;
                        _txtInput.SelectedText = Environment.NewLine;
                        _txtInput.CaretIndex = caret + Environment.NewLine.Length;
                        UpdateInputHeight();
                    }
                    else if (e.Key == Key.Enter) {
                        e.Handled = true;
                        if (!_isGenerating) BtnSend_Click(null, null);
                    }
                };
                _txtInput.PreviewDragOver += TxtInput_OnPreviewDragOver;
                _txtInput.Drop += TxtInput_OnDrop;
                UpdateInputHeight();

                DataObject.AddPastingHandler(_txtInput, TxtInput_OnPasting);
            }
            if (_inputAreaBorder != null) {
                _inputAreaBorder.AllowDrop = true;
                _inputAreaBorder.PreviewDragOver += TxtInput_OnPreviewDragOver;
                _inputAreaBorder.Drop += TxtInput_OnDrop;
            }

            var btnUploadImage = (Button)_window.FindName("BtnUploadImage");
            var menuUploadFile = (MenuItem)_window.FindName("MenuUploadFile");
            _btnClearImage = (Button)_window.FindName("BtnClearImage");
            Action openAttachmentPicker = () => {
                var ofd = new Microsoft.Win32.OpenFileDialog {
                    Filter = "Supported Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.txt;*.md;*.json;*.csv;*.xml;*.ghx;*.pdf;*.docx;*.xlsx;*.pptx;*.doc;*.xls;*.ppt|All Files|*.*",
                    Multiselect = true
                };
                if (ofd.ShowDialog() == true) {
                    AddPendingAttachments(ofd.FileNames);
                }
            };

            if (btnUploadImage != null) {
                btnUploadImage.Click += (s, e) => {
                    if (btnUploadImage.ContextMenu != null) {
                        btnUploadImage.ContextMenu.PlacementTarget = btnUploadImage;
                        btnUploadImage.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                        btnUploadImage.ContextMenu.IsOpen = true;
                    } else {
                        openAttachmentPicker();
                    }
                };
            }

            if (menuUploadFile != null) {
                menuUploadFile.Click += (s, e) => openAttachmentPicker();
            }

            if (_btnClearImage != null) {
            _btnClearImage.Click += (s, e) => {
                _pendingAttachments.Clear();
                    RefreshAttachmentPreview();
                    if (_btnClearImage != null) _btnClearImage.Visibility = Visibility.Collapsed;
            };
            }

            var btnNewChat = (Button)_window.FindName("BtnNewChat");
            if (btnNewChat != null) {
            btnNewChat.Click += (s, e) => {
                _activeHistoryId = null;
                ResetTransientConversationState();
                ResetAgentContextLedger();
                _messages.Clear();
                _messages.AddRange(BuildInitialSystemMessages());
                _displayMessages?.Clear();
                    if (_chatPanel != null) _chatPanel.Children.Clear();
                    _cachedCanvasState = null;
                    _canvasChanged = true;
                if (_txtInput != null) _txtInput.Clear();
                RefreshAttachmentPreview();
                if (_btnClearImage != null) _btnClearImage.Visibility = Visibility.Collapsed;
                UpdateEmptyChatLayout(true);
                RefreshContextMeter();
                NotifyCanvasConversationChanged(true);
                if (_isHistorySidebarVisible) RefreshHistorySidebar();
            };
            }

            // 同步电池库按钮
            var btnSyncLibrary = (Button)_window.FindName("BtnSyncLibrary");
            if (btnSyncLibrary != null) {
                btnSyncLibrary.Click += (s, e) => SyncComponentLibrary();
            }

            // 取消设置按钮
            var btnCancelSettings2 = (Button)_window.FindName("BtnCancelSettings");
            if (btnCancelSettings2 != null) {
                btnCancelSettings2.Click += (s, e) => {
                    SetSettingsOverlayVisible(false);
                };
            }

            // 电池库逻辑
            _libraryRow = (RowDefinition)_window.FindName("LibraryRow");
            _libraryPanel = (FrameworkElement)_window.FindName("LibraryPanel");
            _libraryContent = (StackPanel)_window.FindName("LibraryContent");
            _txtLibCount = (TextBlock)_window.FindName("TxtLibCount");
            var btnToggleLibrary = (Button)_window.FindName("BtnToggleLibrary");
            var btnRefreshLib = (Button)_window.FindName("BtnRefreshLib");
            _skillRow = (RowDefinition)_window.FindName("SkillRow");
            _skillContent = (StackPanel)_window.FindName("SkillContent");
            _txtSkillCount = (TextBlock)_window.FindName("TxtSkillCount");
            var btnToggleSkill = (Button)_window.FindName("BtnToggleSkill");
            var btnRefreshSkill = (Button)_window.FindName("BtnRefreshSkill");
            var skillPanel = (Border)_window.FindName("SkillPanel");

            if (btnToggleSkill != null) {
                btnToggleSkill.Click += (s, e) => {
                    _isSkillVisible = !_isSkillVisible;
                    if (_skillRow != null) _skillRow.Height = _isSkillVisible ? new GridLength(400) : new GridLength(0);
                    if (skillPanel != null) skillPanel.Visibility = _isSkillVisible ? Visibility.Visible : Visibility.Collapsed;
                    if (_isSkillVisible) UpdateSkillLibraryUI();
                };
            }

            if (btnRefreshSkill != null) {
                btnRefreshSkill.Click += (s, e) => UpdateSkillLibraryUI();
            }

            if (btnToggleLibrary != null) {
                btnToggleLibrary.Click += (s, e) => {
                    _isLibraryVisible = !_isLibraryVisible;
                    _libraryRow.Height = _isLibraryVisible ? new GridLength(400) : new GridLength(0);
                    if (_isLibraryVisible) UpdateLibraryUI();
                };
            }

            if (btnRefreshLib != null) {
                btnRefreshLib.Click += (s, e) => SyncComponentLibrary();
            }

            var menuCreateReference = (MenuItem)_window.FindName("MenuCreateReference");
            if (menuCreateReference != null) {
                menuCreateReference.Click += (s, e) => {
                    if (!ShowReferenceOptionsTool.TryEnsureCanvasReadyForCreateReference()) return;
                    string prompt = "请对当前画布内容进行总结，生成五个简短的画布描述（围绕当前画布什么典型建模操作，比如某种 gh 电池使用、基于某种建模逻辑的曲线生成方法等等），描述以卡片形式供我选择。\n" +
                        "【顺序限定】须先调用 get_gh_components 与 check_gh_errors：若画布无法读取、没有电池，或检查中含 Error，则只用文字提醒用户处理，**禁止**调用 " + ShowReferenceOptionsTool.FunctionName + "；仅当画布有内容且无 Error 时，再调用 " + ShowReferenceOptionsTool.FunctionName + "，且 arguments 仅需 JSON 数组 options（恰好 5 个字符串，勿传单个长字符串）。\n" +
                        "用户选择后，程序会把画布 JSON 保存到项目 reference 文件夹，并更新 skills/reference_index.md。";
                    SendHiddenPromptAsync("保存当前画布为参考", prompt);
                };
            }

            var btnMyReferences = (Button)_window.FindName("BtnMyReferences");
            if (btnMyReferences != null) {
                btnMyReferences.Click += (s, e) => {
                    ShowReferenceLibraryUI();
                };
            }

            try {
                var helper = new System.Windows.Interop.WindowInteropHelper(_window);
                helper.Owner = Rhino.RhinoApp.MainWindowHandle();
            _window.Show();
            RefreshContextMeter();
            } catch (Exception ex) {
                System.Windows.Forms.MessageBox.Show("显示窗口时报错: " + ex.ToString());
            }
        }

        private static string GetTypeHint(IGH_Param param)
        {
            string baseType = "Any";
            try
            {
                string typeName = param.TypeName ?? "";
                if (typeName.Contains("Boolean") || typeName.Contains("Bool")) baseType = "Boolean";
                else if (typeName.Contains("Number") || typeName.Contains("Double") || typeName.Contains("Integer")) baseType = "Number";
                else if (typeName.Contains("Point")) baseType = "Point";
                else if (typeName.Contains("Vector")) baseType = "Vector";
                else if (typeName.Contains("Line")) baseType = "Line";
                else if (typeName.Contains("Curve")) baseType = "Curve";
                else if (typeName.Contains("Surface")) baseType = "Surface";
                else if (typeName.Contains("Brep")) baseType = "Brep";
                else if (typeName.Contains("Mesh")) baseType = "Mesh";
                else if (typeName.Contains("String") || typeName.Contains("Text")) baseType = "String";
            }
            catch (Exception ex) { AddGhLog.Debug("GetTypeHint TypeName: " + ex.Message); }

            try
            {
                if (param.Access == GH_ParamAccess.list) return baseType + "[]";
                if (param.Access == GH_ParamAccess.tree) return baseType + "[][]";
            }
            catch (Exception ex) { AddGhLog.Debug("GetTypeHint Access: " + ex.Message); }

            return baseType;
        }

        private static string NormalizeRequestedGhTypeHint(string raw)
        {
            string s = (raw ?? "").Trim();
            if (s.Length == 0) return "";

            while (s.EndsWith("[]", StringComparison.Ordinal))
                s = s.Substring(0, s.Length - 2).TrimEnd();

            switch (s.ToLowerInvariant())
            {
                case "bool":
                case "boolean":
                    return "Boolean";
                case "int":
                case "integer":
                    return "Integer";
                case "double":
                case "float":
                case "number":
                    return "Double";
                case "text":
                case "str":
                case "string":
                    return "String";
                case "point":
                case "point3d":
                    return "Point3d";
                case "vector":
                case "vector3d":
                    return "Vector3d";
                case "rect":
                case "rectangle":
                    return "Rectangle3d";
                default:
                    return s;
            }
        }

        private static Grasshopper.Kernel.Parameters.IGH_TypeHint TryCreateGhTypeHint(string raw)
        {
            string normalized = NormalizeRequestedGhTypeHint(raw);
            if (string.IsNullOrWhiteSpace(normalized)) return null;

            try
            {
                var found = Grasshopper.Kernel.Parameters.Hints.GH_TypeHintServer.FindHintByName(normalized);
                if (found != null) return found;
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("TryCreateGhTypeHint FindHintByName " + normalized + ": " + ex.Message);
            }

            string[] typeCandidates =
            {
                "Grasshopper.Kernel.Parameters.Hints.GH_" + normalized + "Hint",
                "Grasshopper.Kernel.Parameters.Hints.GH_" + normalized + "Hint_CS",
                "Grasshopper.Kernel.Parameters.Hints.GH_" + normalized
            };

            foreach (string fullName in typeCandidates)
            {
                try
                {
                    Type t = typeof(Grasshopper.Kernel.Parameters.IGH_TypeHint).Assembly.GetType(fullName, false);
                    if (t == null) continue;
                    if (!typeof(Grasshopper.Kernel.Parameters.IGH_TypeHint).IsAssignableFrom(t)) continue;
                    return Activator.CreateInstance(t) as Grasshopper.Kernel.Parameters.IGH_TypeHint;
                }
                catch (Exception ex)
                {
                    AddGhLog.Debug("TryCreateGhTypeHint ctor " + fullName + ": " + ex.Message);
                }
            }

            return null;
        }

        private static bool TryApplyRuntimeTypeHint(Grasshopper.Kernel.IGH_Param param, string rawTypeHint, List<string> warnings = null)
        {
            var hint = TryCreateGhTypeHint(rawTypeHint);
            if (hint == null) return false;

            Type paramType = param?.GetType();
            if (paramType == null) return false;

            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var prop in paramType.GetProperties(flags))
            {
                if (!prop.CanWrite) continue;
                if (prop.Name.IndexOf("TypeHint", StringComparison.OrdinalIgnoreCase) < 0 &&
                    !string.Equals(prop.Name, "Hint", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!prop.PropertyType.IsAssignableFrom(hint.GetType()) &&
                    !prop.PropertyType.IsAssignableFrom(typeof(Grasshopper.Kernel.Parameters.IGH_TypeHint)))
                    continue;
                try
                {
                    prop.SetValue(param, hint, null);
                    return true;
                }
                catch (Exception ex)
                {
                    AddGhLog.Debug("TryApplyRuntimeTypeHint prop " + prop.Name + ": " + ex.Message);
                }
            }

            foreach (var method in paramType.GetMethods(flags))
            {
                if (method.Name.IndexOf("TypeHint", StringComparison.OrdinalIgnoreCase) < 0 &&
                    !string.Equals(method.Name, "SetHint", StringComparison.OrdinalIgnoreCase))
                    continue;
                var args = method.GetParameters();
                if (args.Length != 1) continue;
                if (!args[0].ParameterType.IsAssignableFrom(hint.GetType()) &&
                    !args[0].ParameterType.IsAssignableFrom(typeof(Grasshopper.Kernel.Parameters.IGH_TypeHint)))
                    continue;
                try
                {
                    method.Invoke(param, new object[] { hint });
                    return true;
                }
                catch (Exception ex)
                {
                    AddGhLog.Debug("TryApplyRuntimeTypeHint method " + method.Name + ": " + ex.Message);
                }
            }

            for (Type t = paramType; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var field in t.GetFields(flags))
                {
                    if (field.Name.IndexOf("TypeHint", StringComparison.OrdinalIgnoreCase) < 0 &&
                        !string.Equals(field.Name, "m_typeHint", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!field.FieldType.IsAssignableFrom(hint.GetType()) &&
                        !field.FieldType.IsAssignableFrom(typeof(Grasshopper.Kernel.Parameters.IGH_TypeHint)))
                        continue;
                    try
                    {
                        field.SetValue(param, hint);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        AddGhLog.Debug("TryApplyRuntimeTypeHint field " + field.Name + ": " + ex.Message);
                    }
                }
            }

            warnings?.Add("Type hint '" + rawTypeHint + "' was recognized but could not be applied to runtime parameter type " + paramType.Name + "; C# Script ports may still expose object values, so the body writer should use generated aliases or explicit conversion.");
            return false;
        }

        private static void UpdateLibraryUI()
        {
            if (_libraryContent == null) return;

            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                _libraryContent.Children.Clear();

                var groups = Grasshopper.Instances.ComponentServer.ObjectProxies
                    .Where(p => !p.Obsolete)
                    .GroupBy(p => p.Desc.Category)
                    .OrderBy(g => g.Key)
                    .ToList();

                int total = groups.Sum(g => g.Count());
                if (_txtLibCount != null) _txtLibCount.Text = $"({total} 个)";

                foreach (var group in groups)
                {
                    var expander = new Expander
                    {
                        Header = $"{group.Key}  ({group.Count()})",
                        Foreground = ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(160, 160, 160)),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        BorderBrush = ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromRgb(40, 40, 40)),
                        Margin = new Thickness(0, 2, 0, 0),
                        IsExpanded = false
                    };

                    var wrap = new WrapPanel { Margin = new Thickness(4, 4, 4, 8) };
                    foreach (var p in group.OrderBy(x => x.Desc.Name))
                    {
                        var card = new Border
                        {
                            Background = ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(28, 28, 28)),
                            CornerRadius = new CornerRadius(6),
                            Width = 120,
                            Height = 58,
                            Margin = new Thickness(3),
                            BorderBrush = ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromRgb(50, 50, 50)),
                            BorderThickness = new Thickness(1),
                            Cursor = Cursors.Hand,
                            ToolTip = p.Desc.Description
                        };
                        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7) };
                        sp.Children.Add(new TextBlock
                        {
                            Text = p.Desc.Name,
                            Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                            FontSize = 11,
                            FontWeight = FontWeights.Bold,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        });
                        sp.Children.Add(new TextBlock
                        {
                            Text = p.Desc.NickName,
                            Foreground = ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(140, 140, 140)),
                            FontSize = 10
                        });
                        card.Child = sp;
                        wrap.Children.Add(card);
                    }
                    expander.Content = wrap;
                    _libraryContent.Children.Add(expander);
                }
            }));
        }

        private static void SyncComponentLibrary()
        {
            try
            {
                AppendSystemMessage("正在同步电池库...");

                string savePath = "";
                string customPath = Grasshopper.Instances.Settings.GetValue("Library_Path", "");

                // 如果用户设置了自定义路径，优先使用
                if (!string.IsNullOrEmpty(customPath) && System.IO.Directory.Exists(customPath))
                {
                    savePath = System.IO.Path.Combine(customPath, "grasshopper_library.json");
                }
                else
                {
                    // 使用默认路径逻辑
                    string skillsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Grasshopper", "Libraries", "skills");

                    // 如果AppData里没有，尝试在当前工作目录找（这应该是源代码所在位置）
                    if (!System.IO.Directory.Exists(skillsPath))
                    {
                        skillsPath = System.IO.Path.Combine(Environment.CurrentDirectory, "skills");
                    }
                    if (!System.IO.Directory.Exists(skillsPath))
                    {
                        skillsPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skills");
                    }
                    if (!System.IO.Directory.Exists(skillsPath))
                    {
                        // 如果还是找不到，尝试在工作目录创建
                        skillsPath = System.IO.Path.Combine(Environment.CurrentDirectory, "skills");
                        if (!System.IO.Directory.Exists(skillsPath))
                        {
                            System.IO.Directory.CreateDirectory(skillsPath);
                        }
                    }

                    // 在 skills 同级创建 reference 文件夹
                    string parentPath = System.IO.Path.GetDirectoryName(skillsPath);
                    string referencePath = System.IO.Path.Combine(parentPath, "reference");

                    if (!System.IO.Directory.Exists(referencePath))
                    {
                        System.IO.Directory.CreateDirectory(referencePath);
                    }

                    savePath = System.IO.Path.Combine(referencePath, "grasshopper_library.json");
                }

                // 按 Exposure 级别分类 - JSON
                System.Text.StringBuilder sbPrimary = new System.Text.StringBuilder();
                System.Text.StringBuilder sbSecondary = new System.Text.StringBuilder();
                System.Text.StringBuilder sbTertiary = new System.Text.StringBuilder();
                System.Text.StringBuilder sbHidden = new System.Text.StringBuilder();
                sbPrimary.AppendLine("[");
                sbSecondary.AppendLine("[");
                sbTertiary.AppendLine("[");
                sbHidden.AppendLine("[");
                bool firstPrimary = true, firstSecondary = true, firstTertiary = true, firstHidden = true;

                // 按 Exposure 级别分类 - CSV
                System.Text.StringBuilder sbCsvPrimary = new System.Text.StringBuilder();
                System.Text.StringBuilder sbCsvSecondary = new System.Text.StringBuilder();
                System.Text.StringBuilder sbCsvTertiary = new System.Text.StringBuilder();
                System.Text.StringBuilder sbCsvHidden = new System.Text.StringBuilder();

                // CSV表头
                string csvHeader = "name,nickname,description,category,subcategory,input_names,input_types,output_names,output_types";
                sbCsvPrimary.AppendLine(csvHeader);
                sbCsvSecondary.AppendLine(csvHeader);
                sbCsvTertiary.AppendLine(csvHeader);
                sbCsvHidden.AppendLine(csvHeader);

                int countPrimary = 0, countSecondary = 0, countTertiary = 0, countHidden = 0;

                foreach (var proxy in Grasshopper.Instances.ComponentServer.ObjectProxies)
                {
                    try
                    {
                        var comp = proxy.CreateInstance() as IGH_Component;
                        if (comp != null)
                        {
                            // 构建电池 JSON
                            string compJson = "";
                            compJson += "  {";
                            compJson += $"\"name\":\"{EscapeJsonString(comp.Name)}\",";
                            compJson += $"\"nickname\":\"{EscapeJsonString(comp.NickName)}\",";
                            compJson += $"\"description\":\"{EscapeJsonString(comp.Description)}\",";
                            compJson += $"\"category\":\"{EscapeJsonString(comp.Category)}\",";
                            compJson += $"\"subcategory\":\"{EscapeJsonString(comp.SubCategory)}\",";

                            // 输入端口
                            compJson += "\"inputs\":[";
                            List<string> inputNames = new List<string>();
                            List<string> inputTypes = new List<string>();
                            for (int i = 0; i < comp.Params.Input.Count; i++)
                            {
                                var param = comp.Params.Input[i];
                                if (i > 0) compJson += ",";
                                string typeHint = "Unknown";
                                try { typeHint = GetTypeHint(param); } catch (Exception ex) { AddGhLog.Debug("Sync lib input typeHint: " + ex.Message); }

                                string desc = "";
                                try { desc = param.Description ?? ""; } catch (Exception ex) { AddGhLog.Debug("Sync lib input desc: " + ex.Message); }

                                compJson += "{";
                                compJson += $"\"name\":\"{EscapeJsonString(param.Name)}\",";
                                compJson += $"\"description\":\"{EscapeJsonString(desc)}\",";
                                compJson += $"\"typeHint\":\"{EscapeJsonString(typeHint)}\"";
                                compJson += "}";

                                inputNames.Add(param.Name);
                                inputTypes.Add(typeHint);
                            }
                            compJson += "],";

                            // 输出端口
                            compJson += "\"outputs\":[";
                            List<string> outputNames = new List<string>();
                            List<string> outputTypes = new List<string>();
                            for (int i = 0; i < comp.Params.Output.Count; i++)
                            {
                                var param = comp.Params.Output[i];
                                if (i > 0) compJson += ",";
                                string typeHint = "Unknown";
                                try { typeHint = GetTypeHint(param); } catch (Exception ex) { AddGhLog.Debug("Sync lib output typeHint: " + ex.Message); }

                                string desc = "";
                                try { desc = param.Description ?? ""; } catch (Exception ex) { AddGhLog.Debug("Sync lib output desc: " + ex.Message); }

                                compJson += "{";
                                compJson += $"\"name\":\"{EscapeJsonString(param.Name)}\",";
                                compJson += $"\"description\":\"{EscapeJsonString(desc)}\",";
                                compJson += $"\"typeHint\":\"{EscapeJsonString(typeHint)}\"";
                                compJson += "}";

                                outputNames.Add(param.Name);
                                outputTypes.Add(typeHint);
                            }
                            compJson += "]";
                            compJson += "}";

                            // 构建 CSV 行
                            string csvLine =
                                $"{EscapeCsvString(comp.Name)}," +
                                $"{EscapeCsvString(comp.NickName)}," +
                                $"{EscapeCsvString(comp.Description)}," +
                                $"{EscapeCsvString(comp.Category)}," +
                                $"{EscapeCsvString(comp.SubCategory)}," +
                                $"{EscapeCsvString(string.Join("|", inputNames))}," +
                                $"{EscapeCsvString(string.Join("|", inputTypes))}," +
                                $"{EscapeCsvString(string.Join("|", outputNames))}," +
                                $"{EscapeCsvString(string.Join("|", outputTypes))}";

                            // 按 Exposure 级别分配
                            if (proxy.Exposure == GH_Exposure.primary)
                            {
                                if (!firstPrimary) sbPrimary.AppendLine(",");
                                firstPrimary = false;
                                sbPrimary.AppendLine(compJson);
                                sbCsvPrimary.AppendLine(csvLine);
                                countPrimary++;
                            }
                            else if (proxy.Exposure == GH_Exposure.secondary)
                            {
                                if (!firstSecondary) sbSecondary.AppendLine(",");
                                firstSecondary = false;
                                sbSecondary.AppendLine(compJson);
                                sbCsvSecondary.AppendLine(csvLine);
                                countSecondary++;
                            }
                            else if (proxy.Exposure == GH_Exposure.tertiary)
                            {
                                if (!firstTertiary) sbTertiary.AppendLine(",");
                                firstTertiary = false;
                                sbTertiary.AppendLine(compJson);
                                sbCsvTertiary.AppendLine(csvLine);
                                countTertiary++;
                            }
                            else
                            {
                                if (!firstHidden) sbHidden.AppendLine(",");
                                firstHidden = false;
                                sbHidden.AppendLine(compJson);
                                sbCsvHidden.AppendLine(csvLine);
                                countHidden++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AddGhLog.Warn("Library sync skipped one proxy: " + ex.Message);
                    }
                }

                sbPrimary.AppendLine("\n]");
                sbSecondary.AppendLine("\n]");
                sbTertiary.AppendLine("\n]");
                sbHidden.AppendLine("\n]");

                // 保存文件
                string directory = System.IO.Path.GetDirectoryName(savePath);

                // JSON文件
                string primaryPath = System.IO.Path.Combine(directory, "library_primary.json");
                string secondaryPath = System.IO.Path.Combine(directory, "library_secondary.json");
                string tertiaryPath = System.IO.Path.Combine(directory, "library_tertiary.json");
                string hiddenPath = System.IO.Path.Combine(directory, "library_hidden.json");
                System.IO.File.WriteAllText(primaryPath, sbPrimary.ToString(), System.Text.Encoding.UTF8);
                System.IO.File.WriteAllText(secondaryPath, sbSecondary.ToString(), System.Text.Encoding.UTF8);
                System.IO.File.WriteAllText(tertiaryPath, sbTertiary.ToString(), System.Text.Encoding.UTF8);
                System.IO.File.WriteAllText(hiddenPath, sbHidden.ToString(), System.Text.Encoding.UTF8);

                // CSV文件
                string csvPrimaryPath = System.IO.Path.Combine(directory, "library_primary.csv");
                string csvSecondaryPath = System.IO.Path.Combine(directory, "library_secondary.csv");
                string csvTertiaryPath = System.IO.Path.Combine(directory, "library_tertiary.csv");
                string csvHiddenPath = System.IO.Path.Combine(directory, "library_hidden.csv");
                System.IO.File.WriteAllText(csvPrimaryPath, sbCsvPrimary.ToString(), System.Text.Encoding.UTF8);
                System.IO.File.WriteAllText(csvSecondaryPath, sbCsvSecondary.ToString(), System.Text.Encoding.UTF8);
                System.IO.File.WriteAllText(csvTertiaryPath, sbCsvTertiary.ToString(), System.Text.Encoding.UTF8);
                System.IO.File.WriteAllText(csvHiddenPath, sbCsvHidden.ToString(), System.Text.Encoding.UTF8);

                AppendSystemMessage($"电池库同步完成！已保存到：{directory}");
                AppendSystemMessage($"分类：primary({countPrimary})、secondary({countSecondary})、tertiary({countTertiary})、hidden({countHidden})");
                AppendSystemMessage("已同时导出 JSON 和 CSV 两种格式，CSV 格式可降低 token 消耗。");
            }
            catch (Exception ex)
            {
                AddGhLog.Error("SyncComponentLibrary failed", ex);
                AppendQuietDiagnosticCard("电池库同步", "未完成：" + ex.Message);
            }
        }

        private static string EscapeJsonString(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private static string EscapeCsvString(string s)
        {
            if (s == null) return "";
            s = s.Replace("\"", "\"\"");
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r"))
            {
                s = "\"" + s + "\"";
            }
            return s;
        }

        private static void UpdateInputHeight()
        {
            if (_txtInput == null) return;

            _txtInput.UpdateLayout();
            int lineCount = Math.Max(1, _txtInput.LineCount);
            double lineHeight = Math.Max(20, _txtInput.FontSize * 1.45);
            double desiredHeight = 24 + (lineCount * lineHeight);
            _txtInput.Height = Math.Min(116, Math.Max(36, desiredHeight));
        }

        private static Border CreateStopSendGlyph(Brush fill = null)
        {
            var square = new Border {
                Width = 11,
                Height = 11,
                CornerRadius = new CornerRadius(1),
                Background = fill ?? ThemeBrush(Color.FromRgb(0, 0, 0), Color.FromRgb(255, 255, 255)),
                SnapsToDevicePixels = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetEdgeMode(square, EdgeMode.Aliased);
            return square;
        }

        private static void ApplySendButtonChrome(bool generating)
        {
            if (_btnSend == null) return;

            Brush background = generating
                ? ThemeBrush(Color.FromRgb(238, 241, 245), Color.FromRgb(54, 54, 54))
                : ThemeBrush(Color.FromRgb(232, 236, 243), Color.FromRgb(62, 62, 62));
            Brush border = generating
                ? ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromRgb(76, 76, 76))
                : ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromRgb(82, 82, 82));
            Brush icon = generating
                ? ThemeBrush(Color.FromRgb(0, 0, 0), Color.FromRgb(255, 255, 255))
                : ThemeBrush(Color.FromRgb(34, 40, 49), Color.FromRgb(245, 245, 245));

            _btnSend.Background = background;
            _btnSend.BorderBrush = border;
            _btnSend.BorderThickness = new Thickness(1);
            _btnSend.Content = generating ? (object)CreateStopSendGlyph(icon) : CreateSendGlyph(icon);

            var bg = _btnSend.Template.FindName("bg", _btnSend) as Border;
            if (bg != null)
            {
                bg.Background = background;
                bg.BorderBrush = border;
                bg.BorderThickness = new Thickness(1);
                bg.CornerRadius = new CornerRadius(11);
            }
            var cp = _btnSend.Template.FindName("cp", _btnSend) as ContentPresenter;
            if (cp != null) cp.Margin = new Thickness(0);
        }

        private static void ApplySendButtonGeneratingState()
        {
            UpdateAgentModeButtons();
            UpdateLayoutModeButtons();
            if (_btnSend == null) return;
            ApplySendButtonChrome(true);
        }

        private static void ApplySendButtonIdleState()
        {
            UpdateAgentModeButtons();
            UpdateLayoutModeButtons();
            if (_btnSend == null) return;
            ApplySendButtonChrome(false);
        }

        private static string BuildSimpleRollingSummaryBlock(IList<object> messages, int fromInclusive, int toExclusive, int maxChars)
        {
            if (messages == null || fromInclusive >= toExclusive) return "";

            var lines = new List<string>();
            for (int i = fromInclusive; i < toExclusive && i < messages.Count; i++)
            {
                string role = ChatMessageHelpers.TryGetRole(messages[i]) ?? "?";
                string content = ChatMessageHelpers.TryGetPlainTextContent(messages[i]) ?? "";
                content = content.Replace("\r", " ").Replace("\n", " ").Trim();
                if (content.Length > 220) content = content.Substring(0, 220) + "...";
                if (content.Length == 0) continue;
                lines.Add(role.ToUpperInvariant() + ": " + content);
            }

            string text = string.Join("\n", lines);
            if (text.Length > maxChars)
                text = text.Substring(0, maxChars) + "\n[...truncated]";
            return text;
        }

        private static bool TryApplyRollingSummaryInPlace()
        {
            if (_messages == null || _messages.Count == 0) return false;

            ChatMessageHelpers.GetTierBoundaries(_messages, out int tier0End, out int tier2Start, out bool hasTier1Summary);
            if (!ChatMessageHelpers.TryFindSummaryCutExclusive(_messages, tier2Start, DeploymentOptions.ContextVerbatimTailCount, out int cutExclusive))
                return false;

            string existingSummary = "";
            if (hasTier1Summary && tier0End < _messages.Count &&
                ChatMessageHelpers.IsRollingSummaryTier1Message(_messages[tier0End], out string body))
                existingSummary = body ?? "";

            int maxSummaryChars = Math.Max(1200, DeploymentOptions.Tier1SoftBudgetTokens * 3);
            string newBlock = BuildSimpleRollingSummaryBlock(_messages, tier2Start, cutExclusive, Math.Max(600, maxSummaryChars / 2));
            if (string.IsNullOrWhiteSpace(existingSummary) && string.IsNullOrWhiteSpace(newBlock))
                return false;

            string merged = string.IsNullOrWhiteSpace(existingSummary)
                ? newBlock
                : (string.IsNullOrWhiteSpace(newBlock) ? existingSummary : existingSummary + "\n" + newBlock);
            if (merged.Length > maxSummaryChars)
                merged = merged.Substring(0, maxSummaryChars) + "\n[...truncated]";

            for (int i = cutExclusive - 1; i >= tier2Start; i--)
                _messages.RemoveAt(i);

            var summaryMsg = new { role = "assistant", content = DeploymentOptions.RollingSummaryHeader + merged };
            if (hasTier1Summary && tier0End < _messages.Count)
                _messages[tier0End] = summaryMsg;
            else
                _messages.Insert(tier0End, summaryMsg);

            return true;
        }

        private static void ApplyMechanicalContextCompressionIfNeeded()
        {
            try
            {
                if (_messages == null || _messages.Count == 0) return;
                int projected = ChatMessageHelpers.EstimateProjectedMessageListTokens(_messages);
                var budget = Magpie.Agent.ContextBudget.Create(
                    DeploymentOptions.ContextBudgetTokens,
                    projected,
                    DeploymentOptions.ContextReservedOutputTokens,
                    DeploymentOptions.ContextCompressTriggerRatio);
                AddGhLog.Debug("Context budget before compression: " + budget.ToLogLine());
                if (!budget.ShouldCompact()) return;
                LogContextCompactionPlan(_messages, DeploymentOptions.ContextVerbatimTailCount);

                TryApplyRollingSummaryInPlace();
                projected = ChatMessageHelpers.EstimateProjectedMessageListTokens(_messages);
                budget = Magpie.Agent.ContextBudget.Create(
                    DeploymentOptions.ContextBudgetTokens,
                    projected,
                    DeploymentOptions.ContextReservedOutputTokens,
                    DeploymentOptions.ContextCompressTriggerRatio);
                if (!budget.ShouldCompact())
                {
                    ChatMessageHelpers.TrimMessageHistory(_messages, DeploymentOptions.MaxPersistedChatMessages);
                    return;
                }
                ChatMessageHelpers.ApplyMechanicalContextReductionInPlace(_messages);
                ChatMessageHelpers.TrimMessageHistory(_messages, DeploymentOptions.MaxPersistedChatMessages);
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("ApplyMechanicalContextCompressionIfNeeded: " + ex.Message);
            }
        }

        private static Geometry BuildContextArcGeometry(double ratio, double size, double strokeThickness)
        {
            ratio = Math.Max(0, Math.Min(1, ratio));
            if (ratio <= 0) return Geometry.Empty;

            double radius = Math.Max(0.1, (size - strokeThickness) / 2.0);
            Point center = new Point(size / 2.0, size / 2.0);
            Point start = new Point(center.X, center.Y - radius);

            if (ratio >= 0.999)
            {
                return new EllipseGeometry(center, radius, radius);
            }

            double angle = (Math.PI * 2.0 * ratio) - (Math.PI / 2.0);
            Point end = new Point(
                center.X + (radius * Math.Cos(angle)),
                center.Y + (radius * Math.Sin(angle)));

            var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = ratio >= 0.5
            });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

        private static void RefreshContextMeter()
        {
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                if (_contextMeterHost == null || _contextRingProgress == null) return;

                int projected = 0;
                double ratio = 0;
                try
                {
                    projected = ChatMessageHelpers.EstimateProjectedMessageListTokens(_messages);
                    ratio = DeploymentOptions.ContextBudgetTokens <= 0
                        ? 0
                        : Math.Max(0, Math.Min(1, (double)projected / DeploymentOptions.ContextBudgetTokens));
                }
                catch (Exception ex)
                {
                    AddGhLog.Debug("RefreshContextMeter estimate: " + ex.Message);
                }

                double size = _contextMeterHost.Width > 0 ? _contextMeterHost.Width : Math.Max(17, _contextMeterHost.ActualWidth);
                double stroke = _contextRingProgress.StrokeThickness > 0 ? _contextRingProgress.StrokeThickness : 1.3;
                _contextRingProgress.Data = BuildContextArcGeometry(ratio, size, stroke);

                Color color = ratio >= 0.9
                    ? Color.FromRgb(231, 76, 60)
                    : ratio >= 0.72
                        ? Color.FromRgb(230, 184, 92)
                        : Color.FromRgb(216, 216, 216);
                _contextRingProgress.Stroke = new SolidColorBrush(color);
                _contextRingProgress.Visibility = ratio <= 0.001 ? Visibility.Collapsed : Visibility.Visible;
                _contextMeterHost.ToolTip = $"上下文约 {Math.Round(ratio * 100)}% ({projected}/{DeploymentOptions.ContextBudgetTokens})";
            }));
        }

        private static async void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            if (_isGenerating) { _cts?.Cancel(); return; }
            string input = _txtInput.Text.Trim();
            var attachmentsToSend = _pendingAttachments.ToList();
            if (string.IsNullOrEmpty(input) && attachmentsToSend.Count == 0) return;
            string displayInput = string.IsNullOrWhiteSpace(_queuedImmediateSendDisplayTextOverride)
                ? input
                : _queuedImmediateSendDisplayTextOverride.Trim();
            _thinkingStatusStep = 0;
            bool hasImageAttachments = attachmentsToSend.Any(a => a.Kind == AttachmentKind.Image && !string.IsNullOrEmpty(a.Base64));
            _currentTurnAttachments = CloneAttachments(attachmentsToSend);
            _activeImageInputRoute = ResolveImageInputRoute(input, attachmentsToSend);
            ResetVisualWorkflowState(input, attachmentsToSend);
            ResetSelfTrainingState(input);
            string modelInput = BuildSelfTrainingModelInput(input);
            PrepareAgentWorkflowRoute(input, modelInput, attachmentsToSend);
            if (hasImageAttachments && !string.IsNullOrWhiteSpace(_queuedImmediateSendVisionSourceInputOverride))
                _finalVisualReviewSourceInput = _queuedImmediateSendVisionSourceInputOverride;
            _queuedImmediateSendVisionSourceInputOverride = null;
            _queuedImmediateSendDisplayTextOverride = null;

            _isGenerating = true;
            ApplySendButtonGeneratingState();
            _txtInput.Text = "";
            UpdateEmptyChatLayout(false);

            if (_messages.Count == 0) {
                _messages.AddRange(BuildInitialSystemMessages());
            }

            if (attachmentsToSend.Count > 0) {
                bool includeImagesInPrimaryMessage = ShouldIncludeImagesInPrimaryModelMessage(_activeImageInputRoute);
                string imageContextNote = includeImagesInPrimaryMessage
                    ? null
                    : BuildPrimaryModelImageContextNote(_activeImageInputRoute, attachmentsToSend);
                var contentArr = BuildUserMessageContent(modelInput, attachmentsToSend, includeImagesInPrimaryMessage, imageContextNote);
                var userMessage = new { role = "user", content = contentArr };
                _messages.Add(userMessage);
                AddDisplayMessage(userMessage);
                AppendUserMessageWithAttachments(displayInput, attachmentsToSend);
            } else {
                var userMessage = new { role = "user", content = modelInput };
                _messages.Add(userMessage);
                AddDisplayMessage(userMessage);
                AppendBubble(displayInput, true);
            }

            await _window.Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.Render);

            SyncActiveHistoryConversation(string.IsNullOrWhiteSpace(displayInput)
                ? (attachmentsToSend.FirstOrDefault()?.FileName ?? "附件对话")
                : displayInput);

            EnforceChatHistoryLimit();

            _pendingAttachments.Clear();
            RefreshAttachmentPreview();
            if (_btnClearImage != null) _btnClearImage.Visibility = Visibility.Collapsed;

            try { _cts?.Dispose(); } catch (Exception ex) { AddGhLog.Warn("Dispose prior CTS: " + ex.Message); }
            _cts = new System.Threading.CancellationTokenSource();

            try {
                ShowThinkingAnimation();
                if (!await PrepareImageDrivenExecutionContextAsync(modelInput, attachmentsToSend, _cts.Token))
                    return;

                string apiKey = GetProviderRuntimeSettings().ApiKey;
                await CallLLMAPI(apiKey, 0, _cts.Token);
            } catch (OperationCanceledException) {
                AppendSystemMessage("已停止生成。");
            } catch (Exception ex) {
                AddGhLog.Error("CallLLMAPI failed", ex);
                AppendQuietDiagnosticCard("对话请求",
                    BuildProviderDiagnostic(GetProviderRuntimeSettings(), "出现异常：" + ex.GetType().Name, ex.Message));
            } finally {
                HideThinkingAnimation();
                _isGenerating = false;
                ApplySendButtonIdleState();
                _activeImageInputRoute = ImageInputRoute.None;
                _currentTurnAttachments = new List<AttachmentItem>();
                try { _cts?.Dispose(); } catch (Exception ex) { AddGhLog.Warn("Dispose CTS after send: " + ex.Message); }
                _cts = null;
            }
        }

        private static void QueuePromptForImmediateSend(string prompt, List<AttachmentItem> carryoverAttachments = null, string visionSourceInputOverride = null, string displayTextOverride = null)
        {
            string text = (prompt ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text) || _txtInput == null || _isGenerating) return;
            if (carryoverAttachments != null && carryoverAttachments.Count > 0)
            {
                _pendingAttachments.Clear();
                _pendingAttachments.AddRange(CloneAttachments(carryoverAttachments));
                RefreshAttachmentPreview();
                if (_btnClearImage != null) _btnClearImage.Visibility = Visibility.Visible;
            }
            _queuedImmediateSendVisionSourceInputOverride = string.IsNullOrWhiteSpace(visionSourceInputOverride)
                ? null
                : visionSourceInputOverride.Trim();
            _queuedImmediateSendDisplayTextOverride = string.IsNullOrWhiteSpace(displayTextOverride)
                ? null
                : displayTextOverride.Trim();
            _txtInput.Text = text;
            BtnSend_Click(null, null);
        }

        private static void SetSettingsOverlayVisible(bool visible)
        {
            if (_settingsOverlay != null)
            {
                Panel.SetZIndex(_settingsOverlay, visible ? 999 : 20);
                if (visible)
                {
                    _settingsOverlay.Visibility = Visibility.Hidden;
                    UpdateSettingsPanelBounds();
                    ApplyThemeMode();
                    _settingsOverlay.ApplyTemplate();
                    _settingsPanel?.ApplyTemplate();
                    _settingsOverlay.UpdateLayout();
                    ApplyThemeMode();
                    _settingsOverlay.Visibility = Visibility.Visible;
                    _window?.Dispatcher.BeginInvoke((Action)(ApplyThemeMode), System.Windows.Threading.DispatcherPriority.Loaded);
                }
                else
                {
                    _settingsOverlay.Visibility = Visibility.Collapsed;
                }
            }

            if (_canvasWebView != null)
                _canvasWebView.Visibility = visible ? Visibility.Hidden : Visibility.Visible;

            // Keep the header available for dragging, but block actions that would mutate chat state.
            string[] headerActionNames = { "BtnNewChat", "BtnToggleCode", "BtnToggleHistory", "BtnSettings" };
            foreach (string name in headerActionNames)
            {
                if (_window?.FindName(name) is Button button) button.IsEnabled = !visible;
            }
        }

        private static void UpdateSettingsPanelBounds()
        {
            if (_settingsPanel == null || _window == null) return;

            double width = _settingsOverlay != null && _settingsOverlay.ActualWidth > 0
                ? _settingsOverlay.ActualWidth
                : (_window.ActualWidth > 0 ? _window.ActualWidth : _window.Width);
            double height = _settingsOverlay != null && _settingsOverlay.ActualHeight > 0
                ? _settingsOverlay.ActualHeight
                : (_window.ActualHeight > 0 ? _window.ActualHeight : _window.Height);

            double left = width < 430 ? 8 : 12;
            double right = width < 430 ? 8 : 18;
            double top = height < 620 ? 48 : 68;
            double bottom = height < 620 ? 10 : 14;
            double safetyInset = 10;

            double availableWidth = Math.Max(260, width - left - right - safetyInset);
            double availableHeight = Math.Max(260, height - top - bottom - safetyInset);

            _settingsPanel.Margin = new Thickness(left, top, right, bottom);
            _settingsPanel.Width = Math.Min(450, availableWidth);
            _settingsPanel.Height = Math.Min(680, availableHeight);
        }

        private static void MoveViewModeButtonIntoWorkbenchOverlay()
        {
            if (_btnToggleViewMode == null || _codeViewBorder == null) return;
            var target = _codeViewBorder.Child as Grid;
            if (target == null) return;

            var parentPanel = _btnToggleViewMode.Parent as Panel;
            if (parentPanel != null)
                parentPanel.Children.Remove(_btnToggleViewMode);

            _btnToggleViewMode.HorizontalAlignment = HorizontalAlignment.Right;
            _btnToggleViewMode.VerticalAlignment = VerticalAlignment.Top;
            _btnToggleViewMode.Margin = new Thickness(0, 12, 14, 0);
            _btnToggleViewMode.Padding = new Thickness(9, 5, 9, 5);
            Panel.SetZIndex(_btnToggleViewMode, 21);
            if (!target.Children.Contains(_btnToggleViewMode))
                target.Children.Add(_btnToggleViewMode);
        }

        private static ImageInputRoute ResolveImageInputRoute(string input, List<AttachmentItem> attachments)
        {
            bool hasImageAttachments = (attachments ?? new List<AttachmentItem>())
                .Any(a => a.Kind == AttachmentKind.Image && !string.IsNullOrEmpty(a.Base64));
            if (!hasImageAttachments)
                return ImageInputRoute.None;

            return ImageInputRoute.ImageAttached;
        }

        private static bool ShouldIncludeImagesInPrimaryModelMessage(ImageInputRoute route)
        {
            return GetProviderRuntimeSettings()?.Config?.SupportsVision ?? false;
        }

        private static string BuildPrimaryModelImageContextNote(ImageInputRoute route, List<AttachmentItem> attachments)
        {
            int imageCount = (attachments ?? new List<AttachmentItem>())
                .Count(a => a.Kind == AttachmentKind.Image && !string.IsNullOrEmpty(a.Base64));
            if (imageCount <= 0)
                return null;

            if (route == ImageInputRoute.ImageAttached)
                return $"当前轮已上传 {imageCount} 张图片附件。当前主模型链路不直接接收原图，请等待视觉预处理结果后自行判断是直接回答、调用 create_ai_image，还是在确有需要时操作 Grasshopper；不要先检查或修改 Grasshopper 画布。";

            return null;
        }

        private static string GetChatHistoryDirectory()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = System.IO.Path.Combine(root, "Magpie", "history");
            try { System.IO.Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        private static string GetChatHistoryFilePath()
        {
            return System.IO.Path.Combine(GetChatHistoryDirectory(), "conversations.json");
        }

        private static string NormalizeConversationTitle(string text)
        {
            string s = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (string.IsNullOrWhiteSpace(s)) return "新对话";
            if (s.Length > 28) s = s.Substring(0, 28) + "…";
            return s;
        }

        private static string GetConversationPreview(ChatHistoryConversation conv)
        {
            if (conv?.Messages == null || conv.Messages.Count == 0) return "空白对话";
            for (int i = conv.Messages.Count - 1; i >= 0; i--)
            {
                var msg = conv.Messages[i];
                string role = ChatMessageHelpers.TryGetRole(msg);
                if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                    continue;
                string toolSummary = TryGetToolOperationPreview(msg);
                if (!string.IsNullOrWhiteSpace(toolSummary))
                    return toolSummary.Length > 64 ? toolSummary.Substring(0, 64) + "…" : toolSummary;
                string content = ChatMessageHelpers.TryGetPlainTextContent(msg);
                if (string.IsNullOrWhiteSpace(content)) continue;
                content = content.Replace("\r", " ").Replace("\n", " ").Trim();
                return content.Length > 64 ? content.Substring(0, 64) + "…" : content;
            }
            return "空白对话";
        }

        private static string TryGetToolOperationPreview(object msg)
        {
            try
            {
                JObject jo = msg as JObject ?? (msg is JToken token ? token as JObject : null);
                if (jo == null && msg != null)
                    jo = JObject.FromObject(msg);
                var summaries = jo?["tool_operation_summaries"] as JArray;
                if (summaries == null || summaries.Count == 0)
                    return null;

                var parts = summaries
                    .OfType<JObject>()
                    .Select(item => item["summary"]?.ToString())
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Take(3)
                    .ToList();
                return parts.Count == 0 ? null : "工具：" + string.Join(" / ", parts);
            }
            catch
            {
                return null;
            }
        }

        private static string FormatConversationTime(DateTime utcTime)
        {
            try
            {
                return utcTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                return utcTime.ToString("yyyy-MM-dd HH:mm");
            }
        }

        private static ChatHistoryConversation FindHistoryConversation(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return _chatHistory.FirstOrDefault(c => string.Equals(c?.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private static ChatHistoryConversation GetOrCreateActiveHistoryConversation(string titleSeed = null)
        {
            var existing = FindHistoryConversation(_activeHistoryId);
            if (existing != null) return existing;

            var conv = new ChatHistoryConversation
            {
                Id = Guid.NewGuid().ToString("n"),
                Title = NormalizeConversationTitle(titleSeed),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Messages = new JArray()
            };
            _chatHistory.Insert(0, conv);
            _activeHistoryId = conv.Id;
            return conv;
        }

        private static void LoadChatHistoryStore()
        {
            _chatHistory = new List<ChatHistoryConversation>();
            try
            {
                string path = GetChatHistoryFilePath();
                if (!System.IO.File.Exists(path)) return;

                string json = System.IO.File.ReadAllText(path, Encoding.UTF8);
                JObject root = JObject.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                JArray items = root["conversations"] as JArray ?? new JArray();
                foreach (var token in items)
                {
                    var conv = token.ToObject<ChatHistoryConversation>();
                    if (conv == null) continue;
                    conv.Id = string.IsNullOrWhiteSpace(conv.Id) ? Guid.NewGuid().ToString("n") : conv.Id;
                    conv.Title = NormalizeConversationTitle(conv.Title);
                    conv.Messages = conv.Messages ?? new JArray();
                    _chatHistory.Add(conv);
                }

                _chatHistory = _chatHistory
                    .OrderByDescending(c => c.UpdatedAtUtc)
                    .ThenByDescending(c => c.CreatedAtUtc)
                    .ToList();
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("LoadChatHistoryStore failed: " + ex.Message);
            }
        }

        private static void SaveChatHistoryStore()
        {
            try
            {
                string path = GetChatHistoryFilePath();
                var root = new JObject
                {
                    ["conversations"] = JArray.FromObject(_chatHistory)
                };
                System.IO.File.WriteAllText(path, root.ToString(Formatting.Indented), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("SaveChatHistoryStore failed: " + ex.Message);
            }
        }

        private static void SyncActiveHistoryConversation(string titleSeed = null)
        {
            if (_isHistoryRestoring) return;
            if (_messages == null || _messages.Count == 0) return;

            var conv = GetOrCreateActiveHistoryConversation(titleSeed);
            if (conv == null) return;

            var sourceMessages = _displayMessages != null && _displayMessages.Count > 0
                ? _displayMessages
                : _messages;

            var payload = new JArray();
            foreach (var msg in sourceMessages)
            {
                string role = ChatMessageHelpers.TryGetRole(msg);
                if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)) continue;
                payload.Add(CloneMessageToken(msg));
            }

            conv.Messages = payload;
            if (string.IsNullOrWhiteSpace(conv.Title))
                conv.Title = NormalizeConversationTitle(titleSeed);
            if (string.Equals(conv.Title, "新对话", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(titleSeed))
                conv.Title = NormalizeConversationTitle(titleSeed);
            conv.UpdatedAtUtc = DateTime.UtcNow;

            _chatHistory = _chatHistory
                .OrderByDescending(c => c.UpdatedAtUtc)
                .ThenByDescending(c => c.CreatedAtUtc)
                .ToList();

            SaveChatHistoryStore();
            NotifyCanvasConversationChanged(false);
            if (_isHistorySidebarVisible) RefreshHistorySidebar();
        }

        private static void OpenHistoryConversation(string conversationId)
        {
            var conv = FindHistoryConversation(conversationId);
            if (conv == null) return;

            _isHistoryRestoring = true;
            try
            {
                _activeHistoryId = conv.Id;
                _messages = new List<object>(BuildInitialSystemMessages());
                _displayMessages = new List<object>();
                foreach (var token in conv.Messages ?? new JArray())
                {
                    JToken cloned = token.DeepClone();
                    if (cloned is JObject jo) _messages.Add(jo.DeepClone());
                    else _messages.Add(cloned);
                    _displayMessages.Add(cloned.DeepClone());
                }

                if (_chatPanel != null) _chatPanel.Children.Clear();
                RefreshUI();
                if (_txtInput != null) _txtInput.Text = "";
                RefreshContextMeter();
                UpdateHistorySidebarSelection();
                NotifyCanvasConversationChanged(true);
            }
            finally
            {
                _isHistoryRestoring = false;
            }
        }

        private static void DeleteHistoryConversation(string conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return;
            var removed = _chatHistory.FirstOrDefault(c => string.Equals(c.Id, conversationId, StringComparison.OrdinalIgnoreCase));
            if (removed == null) return;

            _chatHistory.Remove(removed);
            if (string.Equals(_activeHistoryId, conversationId, StringComparison.OrdinalIgnoreCase))
            {
                _activeHistoryId = null;
                if (_messages != null)
                {
                    _messages.Clear();
                    _messages.AddRange(BuildInitialSystemMessages());
                    _displayMessages?.Clear();
                    if (_chatPanel != null) _chatPanel.Children.Clear();
                    AppendSystemMessage("当前对话已删除，已切换到新会话。");
                    RefreshContextMeter();
                }
            }

            SaveChatHistoryStore();
            DeleteCanvasConversationState(conversationId);
            NotifyCanvasConversationChanged(true);
            RefreshHistorySidebar();
        }

        private static void UpdateHistorySidebarSelection()
        {
            if (_historyListPanel == null) return;
            foreach (var child in _historyListPanel.Children.OfType<Border>())
            {
                string id = child.Tag as string;
                bool active = !string.IsNullOrWhiteSpace(id)
                    && string.Equals(id, _activeHistoryId, StringComparison.OrdinalIgnoreCase);
                child.BorderBrush = Brushes.Transparent;
                child.BorderThickness = new Thickness(0);
                child.Background = active
                    ? ThemeBrush(Color.FromRgb(238, 242, 247), Color.FromRgb(26, 26, 26))
                    : ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(23, 23, 23));
            }
        }

        private static Button CreateHistoryActionButton(string text, bool danger = false)
        {
            object content = text;
            Thickness padding = new Thickness(10, 4, 10, 4);
            double width = double.NaN;
            double height = double.NaN;

            if (danger)
            {
                content = new WpfPath
                {
                    Data = Geometry.Parse("M6,7 L6.7,15.2 Q6.8,16.5 8.1,16.5 L11.9,16.5 Q13.2,16.5 13.3,15.2 L14,7 M5,7 L15,7 M8,7 L8.5,5 L11.5,5 L12,7 M9,9.2 L9,14 M11,9.2 L11,14"),
                    Stroke = ThemeBrush(Color.FromRgb(28, 32, 38), Color.FromRgb(230, 230, 230)),
                    StrokeThickness = 1.45,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Fill = Brushes.Transparent,
                    Width = 18,
                    Height = 18,
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                padding = new Thickness(0);
                width = 28;
                height = 28;
            }

            var button = new Button
            {
                Content = content,
                Foreground = danger ? ThemeBrush(Color.FromRgb(28, 32, 38), Color.FromRgb(230, 230, 230)) : ThemeBrush(Color.FromRgb(58, 64, 74), Color.FromRgb(208, 208, 208)),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = padding,
                Margin = new Thickness(0, 0, 0, 0),
                Cursor = Cursors.Hand,
                FontSize = 10.5,
                Width = width,
                Height = height,
                ToolTip = danger ? "删除" : null
            };
            button.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
                <ControlTemplate TargetType=""Button"" xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
                    <Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""6"">
                        <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center"" Margin=""{TemplateBinding Padding}""/>
                    </Border>
                </ControlTemplate>");
            return button;
        }

        private static void RefreshHistorySidebar()
        {
            if (_historySidebar == null || _historyListPanel == null) return;

            _historyListPanel.Children.Clear();
            if (_historyCountText != null)
                _historyCountText.Text = _chatHistory.Count.ToString() + " 条";

            if (_chatHistory.Count == 0)
            {
                _historyListPanel.Children.Add(new TextBlock
                {
                    Text = "暂无本地对话",
                    Foreground = ThemeBrush(Color.FromRgb(122, 128, 140), Color.FromRgb(110, 110, 110)),
                    FontSize = 12,
                    Margin = new Thickness(2, 10, 2, 0)
                });
                UpdateHistorySidebarSelection();
                return;
            }

            foreach (var conv in _chatHistory.OrderByDescending(c => c.UpdatedAtUtc).ThenByDescending(c => c.CreatedAtUtc))
            {
                var card = new Border
                {
                    Tag = conv.Id,
                    Background = ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(23, 23, 23)),
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(11),
                    Margin = new Thickness(0, 0, 0, 10),
                    Cursor = Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MaxWidth = 292
                };

                var cardGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel { Orientation = Orientation.Vertical };
                info.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(conv.Title) ? "新对话" : conv.Title,
                    Foreground = ThemeBrush(Color.FromRgb(28, 32, 38), Color.FromRgb(224, 224, 224)),
                    FontSize = 13,
                    FontWeight = FontWeights.Medium,
                    TextWrapping = TextWrapping.Wrap
                });
                info.Children.Add(new TextBlock
                {
                    Text = FormatConversationTime(conv.UpdatedAtUtc),
                    Foreground = new SolidColorBrush(Color.FromRgb(98, 98, 98)),
                    FontSize = 10,
                    Margin = new Thickness(0, 8, 0, 0)
                });
                info.Children.Add(new TextBlock
                {
                    Text = GetConversationPreview(conv),
                    Foreground = new SolidColorBrush(Color.FromRgb(126, 126, 126)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxHeight = 34,
                    Margin = new Thickness(0, 7, 0, 0)
                });

                Grid.SetColumn(info, 0);
                cardGrid.Children.Add(info);

                var actions = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };

                var deleteBtn = CreateHistoryActionButton("删除", true);
                deleteBtn.Visibility = Visibility.Hidden;
                deleteBtn.Click += (s, e) =>
                {
                    e.Handled = true;
                    DeleteHistoryConversation(conv.Id);
                };

                actions.Children.Add(deleteBtn);

                Grid.SetColumn(actions, 1);
                cardGrid.Children.Add(actions);

                card.Child = cardGrid;
                card.MouseEnter += (s, e) =>
                {
                    card.Background = ThemeBrush(Color.FromRgb(238, 242, 247), Color.FromRgb(32, 32, 32));
                    deleteBtn.Visibility = Visibility.Visible;
                };
                card.MouseLeave += (s, e) =>
                {
                    bool active = !string.IsNullOrWhiteSpace(conv.Id)
                        && string.Equals(conv.Id, _activeHistoryId, StringComparison.OrdinalIgnoreCase);
                    card.Background = active
                        ? ThemeBrush(Color.FromRgb(238, 242, 247), Color.FromRgb(26, 26, 26))
                        : ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(23, 23, 23));
                    deleteBtn.Visibility = Visibility.Hidden;
                };
                card.MouseLeftButtonUp += (s, e) =>
                {
                    if (e.Handled) return;
                    OpenHistoryConversation(conv.Id);
                };
                _historyListPanel.Children.Add(card);
            }

            UpdateHistorySidebarSelection();
        }

        private static void SetHistorySidebarVisible(bool visible)
        {
            if (_historySidebar == null) return;
            _isHistorySidebarVisible = visible;
            UpdateToolbarDividerVisibility();

            if (visible)
            {
                _historySidebar.Visibility = Visibility.Visible;
                ApplyHistorySidebarLayout();
                RefreshHistorySidebar();
                UpdateWindowMinWidthForVisiblePanes();
                AnimatePanelIn(_historySidebar, -18);
            }
            else
            {
                AnimatePanelOut(_historySidebar, -18, () =>
                {
                    _historySidebar.Visibility = Visibility.Collapsed;
                    if (_historyCol != null) _historyCol.Width = new GridLength(0);
                    Grid.SetColumnSpan(_historySidebar, 1);
                    _historySidebar.Width = double.NaN;
                    UpdateWindowMinWidthForVisiblePanes();
                    UpdateChatContentWidth();
                    UpdateToolbarDividerVisibility();
                });
            }
        }

        private static bool ShouldOverlayHistorySidebar()
        {
            double windowWidth = _window != null && _window.ActualWidth > 0
                ? _window.ActualWidth
                : (_window != null ? _window.Width : DefaultWindowWidth);
            double requiredWidth = (_isCodeVisible ? ChatPaneMinWidth + CodePaneMinWidth : DefaultWindowWidth) + HistorySidebarWidth;
            return windowWidth < requiredWidth;
        }

        private static void ApplyHistorySidebarLayout()
        {
            if (_historySidebar == null) return;

            bool overlay = ShouldOverlayHistorySidebar();
            _historySidebar.Height = double.NaN;
            _historySidebar.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetColumn(_historySidebar, 0);

            if (overlay)
            {
                if (_historyCol != null) _historyCol.Width = new GridLength(0);
                Grid.SetColumnSpan(_historySidebar, 3);
                _historySidebar.HorizontalAlignment = HorizontalAlignment.Left;
                _historySidebar.Width = Math.Min(HistorySidebarWidth, Math.Max(0, _window?.ActualWidth ?? HistorySidebarWidth));
            }
            else
            {
                if (_historyCol != null) _historyCol.Width = new GridLength(HistorySidebarWidth);
                Grid.SetColumnSpan(_historySidebar, 1);
                _historySidebar.HorizontalAlignment = HorizontalAlignment.Stretch;
                _historySidebar.Width = double.NaN;
            }
        }

        private static void ToggleHistorySidebar()
        {
            SetHistorySidebarVisible(!_isHistorySidebarVisible);
        }

        private static void TxtInput_OnPasting(object sender, DataObjectPastingEventArgs e)
        {
            try
            {
                if (TryConsumePasteAsAttachments(e))
                    e.Handled = true;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Paste into input: " + ex.Message);
            }
        }

        private static void TxtInput_OnPreviewDragOver(object sender, DragEventArgs e)
        {
            try
            {
                e.Effects = CanConsumeDragData(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("PreviewDragOver input: " + ex.Message);
            }
        }

        private static void TxtInput_OnDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (!TryConsumeDroppedDataAsAttachments(e.Data))
                    return;
                e.Handled = true;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Drop into input: " + ex.Message);
            }
        }

        /// <summary>
        /// WPF 粘贴时 <see cref="DataObjectPastingEventArgs.SourceDataObject"/> 有时与系统剪贴板不一致（例如资源管理器复制文件后 Ctrl+V），
        /// 故依次尝试事件源与 <see cref="Clipboard.GetDataObject"/>。
        /// </summary>
        private static IEnumerable<IDataObject> EnumeratePasteDataSources(DataObjectPastingEventArgs e)
        {
            if (e?.SourceDataObject != null)
                yield return e.SourceDataObject;
            IDataObject clip = null;
            try { clip = Clipboard.GetDataObject(); }
            catch (Exception ex) { AddGhLog.Debug("Clipboard.GetDataObject paste: " + ex.Message); yield break; }
            if (clip != null && !ReferenceEquals(clip, e?.SourceDataObject))
                yield return clip;
        }

        /// <summary>
        /// 将剪贴板中的文件路径或图片转为待发送附件；返回 true 时已 CancelCommand，不再往输入框插入文本。
        /// </summary>
        private static bool TryConsumePasteAsAttachments(DataObjectPastingEventArgs e)
        {
            foreach (IDataObject data in EnumeratePasteDataSources(e))
            {
                if (data == null) continue;

                if (data.GetDataPresent(DataFormats.FileDrop, true))
                {
                    var paths = data.GetData(DataFormats.FileDrop, true) as string[];
                    if (paths != null && paths.Length > 0)
                    {
                        string[] files = paths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        if (files.Length > 0)
                        {
                            e.CancelCommand();
                            AddPendingAttachments(files);
                            if (_btnClearImage != null) _btnClearImage.Visibility = Visibility.Visible;
                            return true;
                        }
                    }
                }
            }

            foreach (IDataObject data in EnumeratePasteDataSources(e))
            {
                if (data == null) continue;
                try
                {
                    if (TryConsumeImageDataObjectAsAttachment(data, "MAGPIE_paste_"))
                    {
                        e.CancelCommand();
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    AddGhLog.Debug("Paste PNG format scan: " + ex.Message);
                }
            }

            try
            {
                if (Clipboard.ContainsImage())
                {
                    BitmapSource img = Clipboard.GetImage();
                    if (img != null)
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(img));
                        string tmpPath = Path.Combine(Path.GetTempPath(), "MAGPIE_paste_" + DateTime.UtcNow.Ticks + "_" + Guid.NewGuid().ToString("n").Substring(0, 8) + ".png");
                        using (var fs = new FileStream(tmpPath, FileMode.CreateNew))
                            encoder.Save(fs);
                        e.CancelCommand();
                        AddPendingAttachments(new[] { tmpPath });
                        if (_btnClearImage != null) _btnClearImage.Visibility = Visibility.Visible;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Paste Clipboard.GetImage: " + ex.Message);
            }

            return false;
        }

        private static bool TryConsumeClipboardImageAsAttachment()
        {
            try
            {
                IDataObject data = null;
                try { data = Clipboard.GetDataObject(); }
                catch (Exception ex) { AddGhLog.Debug("Clipboard.GetDataObject shortcut paste: " + ex.Message); }

                if (data != null && TryConsumeImageDataObjectAsAttachment(data, "paste"))
                    return true;

                if (Clipboard.ContainsImage())
                {
                    BitmapSource img = Clipboard.GetImage();
                    if (img != null)
                    {
                        string tmpPath = SaveBitmapSourceToTempPng(img, "MAGPIE_paste_");
                        AddPendingAttachments(new[] { tmpPath });
                        if (_btnClearImage != null) _btnClearImage.Visibility = Visibility.Visible;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Shortcut paste image: " + ex.Message);
            }

            return false;
        }

        private static bool TryConsumeImageDataObjectAsAttachment(IDataObject data, string prefix)
        {
            if (data == null) return false;
            foreach (string fmt in data.GetFormats())
            {
                if (!IsClipboardImageFormat(fmt))
                    continue;

                object payload = data.GetData(fmt, false);
                string tmpPath = TrySaveClipboardImagePayload(payload, fmt, prefix);
                if (!string.IsNullOrWhiteSpace(tmpPath))
                {
                    AddPendingAttachments(new[] { tmpPath });
                    if (_btnClearImage != null) _btnClearImage.Visibility = Visibility.Visible;
                    return true;
                }
            }
            return false;
        }

        private static bool IsClipboardImageFormat(string fmt)
        {
            return string.Equals(fmt, "PNG", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fmt, "image/png", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fmt, "DeviceIndependentBitmap", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fmt, DataFormats.Dib, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fmt, DataFormats.Bitmap, StringComparison.OrdinalIgnoreCase);
        }

        private static string TrySaveClipboardImagePayload(object payload, string fmt, string prefix)
        {
            bool rawPng = string.Equals(fmt, "PNG", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fmt, "image/png", StringComparison.OrdinalIgnoreCase);

            if (rawPng && payload is MemoryStream ms)
            {
                byte[] bytes = ms.ToArray();
                if (bytes.Length > 16)
                {
                    string tmpPath = Path.Combine(Path.GetTempPath(), prefix + DateTime.UtcNow.Ticks + "_" + Guid.NewGuid().ToString("n").Substring(0, 8) + ".png");
                    File.WriteAllBytes(tmpPath, bytes);
                    return tmpPath;
                }
            }
            if (rawPng && payload is byte[] barr && barr.Length > 16)
            {
                string tmpPath = Path.Combine(Path.GetTempPath(), prefix + DateTime.UtcNow.Ticks + "_" + Guid.NewGuid().ToString("n").Substring(0, 8) + ".png");
                File.WriteAllBytes(tmpPath, barr);
                return tmpPath;
            }
            if (payload is BitmapSource source)
                return SaveBitmapSourceToTempPng(source, prefix);
            return null;
        }

        private static string SaveBitmapSourceToTempPng(BitmapSource source, string prefix)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            string tmpPath = Path.Combine(Path.GetTempPath(), prefix + DateTime.UtcNow.Ticks + "_" + Guid.NewGuid().ToString("n").Substring(0, 8) + ".png");
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew))
                encoder.Save(fs);
            return tmpPath;
        }

        private static bool CanConsumeDragData(IDataObject data)
        {
            if (data == null) return false;
            if (data.GetDataPresent(DataFormats.FileDrop, true)) return true;
            if (data.GetDataPresent(DataFormats.Bitmap, true)) return true;
            foreach (string fmt in data.GetFormats())
            {
                if (string.Equals(fmt, "PNG", StringComparison.OrdinalIgnoreCase) || string.Equals(fmt, "image/png", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool TryConsumeDroppedDataAsAttachments(IDataObject data)
        {
            if (data == null) return false;

            if (data.GetDataPresent(DataFormats.FileDrop, true))
            {
                var paths = data.GetData(DataFormats.FileDrop, true) as string[];
                if (paths != null && paths.Length > 0)
                {
                    string[] files = paths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (files.Length > 0)
                    {
                        AddPendingAttachments(files);
                        if (_btnClearImage != null) _btnClearImage.Visibility = Visibility.Visible;
                        return true;
                    }
                }
            }

            try
            {
                foreach (string fmt in data.GetFormats())
                {
                    if (!IsClipboardImageFormat(fmt))
                        continue;

                    object payload = data.GetData(fmt, false);
                    string tmpPath = TrySaveClipboardImagePayload(payload, fmt, "MAGPIE_drop_");
                    if (!string.IsNullOrWhiteSpace(tmpPath))
                    {
                        AddPendingAttachments(new[] { tmpPath });
                        if (_btnClearImage != null) _btnClearImage.Visibility = Visibility.Visible;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("Drop image format scan: " + ex.Message);
            }

            return false;
        }

        private static StackPanel BuildToolOperationCardsPanel(List<(string primary, string secondary)> entries)
        {
            var typed = entries?.Select(e => (e.primary, e.secondary, (string)null)).ToList();
            return BuildToolOperationCardsPanel(typed);
        }

        private static StackPanel BuildToolOperationCardsPanel(List<(string primary, string secondary, string undoId)> entries)
        {
            if (entries == null || entries.Count == 0) return null;
            var stack = new StackPanel {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            foreach (var tup in entries) {
                string primary = tup.primary ?? "";
                string secondary = tup.secondary ?? "";
                string undoId = tup.undoId;
                if (string.IsNullOrWhiteSpace(primary)) continue;

                var row = new Border {
                    Background = ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(22, 22, 22)),
                    BorderBrush = ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromRgb(48, 48, 48)),
                    BorderThickness = new Thickness(0.5),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 0, 4),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MinHeight = 22,
                    ToolTip = string.IsNullOrWhiteSpace(secondary) ? primary : (primary + " · " + secondary)
                };

                var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var primaryTb = new TextBlock {
                    Text = primary,
                    Foreground = ThemeBrush(Color.FromRgb(58, 64, 74), Color.FromRgb(148, 148, 148)),
                    FontSize = 11,
                    FontWeight = FontWeights.Normal,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Left
                };
                Grid.SetColumn(primaryTb, 0);

                var secondaryTb = new TextBlock {
                    Text = secondary,
                    Foreground = ThemeBrush(Color.FromRgb(122, 128, 140), Color.FromRgb(105, 105, 105)),
                    FontSize = 9.5,
                    FontWeight = FontWeights.Normal,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 120,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right,
                    Visibility = string.IsNullOrWhiteSpace(secondary) ? Visibility.Collapsed : Visibility.Visible
                };
                Grid.SetColumn(secondaryTb, 1);

                grid.Children.Add(primaryTb);
                grid.Children.Add(secondaryTb);
                row.Child = grid;
                stack.Children.Add(row);
            }

            return stack.Children.Count > 0 ? stack : null;
        }

        private static void InsertChatElementBeforeThinking(FrameworkElement element)
        {
            if (element == null || _chatPanel == null) return;
            if (_thinkingBubble != null) {
                _chatPanel.Children.Remove(_thinkingBubble);
                _chatPanel.Children.Add(element);
                _chatPanel.Children.Add(_thinkingBubble);
            } else {
                _chatPanel.Children.Add(element);
            }
            if (_chatScroll != null) _chatScroll.ScrollToEnd();
        }

        private static void AppendToolOperationCards(List<(string primary, string secondary)> entries)
        {
            StackPanel stack = BuildToolOperationCardsPanel(entries);
            if (stack == null) return;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => InsertChatElementBeforeThinking(stack)));
        }

        private static void AppendToolOperationCards(List<(string primary, string secondary, string undoId)> entries)
        {
            StackPanel stack = BuildToolOperationCardsPanel(entries);
            if (stack == null) return;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => InsertChatElementBeforeThinking(stack)));
        }


        private static async Task<ApiResponse> CallLLMAPI(string apiKey, int depth = 0, System.Threading.CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            const int MAX_DEPTH = 50;
            if (depth >= MAX_DEPTH)
            {
                AppendQuietDiagnosticCard("对话请求", "已达对话轮数安全上限（50 轮）。如需继续，请发送“继续”或选择步骤卡片继续。");
                return new ApiResponse {
                    Content = "已达对话轮数安全上限 (50轮)。如需继续，请发送“继续”或选择步骤卡片继续。"
                };
            }

            var providerSettings = GetProviderRuntimeSettings();
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = providerSettings.ApiKey;
            providerSettings.ApiKey = apiKey;
            if (string.IsNullOrWhiteSpace(providerSettings.ApiKey))
            {
                return ReturnProviderError(providerSettings, "LLM 配置错误",
                    $"请先配置 {providerSettings.Config.DisplayName} 的 API Key。");
            }

            ApplyMechanicalContextCompressionIfNeeded();
            RefreshContextMeter();
            var messagesToSend = ChatMessageHelpers.ProjectMessagesForSend(_messages);

            object[] toolDefinitions = BuildToolDefinitionsForCurrentMode();

                ShowThinkingAnimation("载入中...");
                DateTime startTime = DateTime.Now;

                HttpResponseMessage response;
                string usedEndpoint = null;
                string lastEndpointError = null;
                try
                {
                    response = null;
                    foreach (var endpoint in BuildEndpointCandidates(providerSettings.BaseUrl))
                    {
                        ct.ThrowIfCancellationRequested();
                        usedEndpoint = endpoint.Url;
                        JObject requestBody = BuildChatRequestBody(providerSettings, messagesToSend, toolDefinitions);
                        AddGhLog.Info("Trying LLM endpoint: " + endpoint.Url + ", model=" + providerSettings.ModelName);

                        response = await SendProviderRequestAsync(providerSettings, requestBody, endpoint.Url, ct);
                        if (response.IsSuccessStatusCode)
                            break;

                        string errPreview = "";
                        try { errPreview = await response.Content.ReadAsStringAsync(); }
                        catch (Exception readEx) { errPreview = "无法读取错误响应体：" + readEx.Message; }

                        lastEndpointError = "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "\n" + ClampDiagDetail(errPreview, 900);
                        AddGhLog.Warn("LLM endpoint failed: " + endpoint.Url + " | " + lastEndpointError.Replace("\r", " ").Replace("\n", " | "));

                        if (!ShouldTryNextEndpoint(response.StatusCode))
                        {
                            return ReturnProviderError(providerSettings, "LLM 连接错误",
                                "模型服务返回 HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase,
                                errPreview,
                                endpoint.Url);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return ReturnProviderError(providerSettings, "LLM 连接错误",
                        "请求未能发送到模型服务：" + ex.GetType().Name,
                        FormatExceptionChain(ex),
                        usedEndpoint);
                }

                ShowThinkingAnimation("思考中...");

                if (!response.IsSuccessStatusCode)
                {
                    return ReturnProviderError(providerSettings, "LLM 连接错误",
                        "模型服务返回 HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase,
                        lastEndpointError,
                        usedEndpoint);
                }

                // 使用流读取以支持即时取消
                string responseJson = "";
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var reader = new System.IO.StreamReader(stream))
                {
                    var task = reader.ReadToEndAsync();
                    while (!task.IsCompleted) {
                        if (ct.IsCancellationRequested) {
                            ct.ThrowIfCancellationRequested();
                        }
                        await Task.Delay(50, ct);
                    }
                    responseJson = task.Result;
                }
                double durationSeconds = (DateTime.Now - startTime).TotalSeconds;

                if (!TryParseAssistantMessageFromResponse(responseJson, out JObject messageNode, out string parseError))
                {
                    return ReturnProviderError(providerSettings, "LLM 响应错误",
                        "模型服务返回的内容不是可解析的 OpenAI 聊天响应：" + parseError,
                        responseJson,
                        usedEndpoint);
                }

                string fullContent = SanitizeAssistantDisplayContent(messageNode["content"]?.ToString() ?? "");
                messageNode["content"] = fullContent;
                string fullReasoning = messageNode["reasoning_content"]?.ToString() ?? "";
                var fullToolCalls = messageNode["tool_calls"] as JArray ?? new JArray();

                if (string.IsNullOrWhiteSpace(fullContent) && string.IsNullOrWhiteSpace(fullReasoning) && fullToolCalls.Count == 0)
                {
                    return ReturnProviderError(providerSettings, "LLM 响应错误",
                        "模型服务返回成功，但消息内容、思考内容和工具调用都为空。",
                        responseJson,
                        usedEndpoint);
                }

                if (ShouldRunFinalVisualReviewThisRound(fullToolCalls))
                {
                    var continuedResponse = await TryContinueWithFinalVisualReviewAsync(apiKey, depth, fullContent, ct);
                    if (continuedResponse != null)
                        return continuedResponse;
                }

                await _window.Dispatcher.InvokeAsync(() => {
                    if (ChatMessageHelpers.ShouldDisplayReasoningBubble(fullReasoning, fullContent, fullToolCalls))
                    {
                        string reasoningTitle = "已思考 " + Math.Round(durationSeconds, 1) + "s";
                        messageNode["_display_reasoning_title"] = reasoningTitle;
                        messageNode["_display_reasoning_icon"] = "💭";
                        AppendCollapsibleBubble(fullReasoning, reasoningTitle, "💭");
                    }
                    if (!string.IsNullOrEmpty(fullContent))
                    {
                        AppendBubble(fullContent, false, depth == 0);
                    }
                });

                _messages.Add(messageNode);
                if (_displayMessages == null)
                    _displayMessages = new List<object>();
                _displayMessages.Add(messageNode);
                EnforceChatHistoryLimit();

                int addComp = 0, delComp = 0, addConn = 0, delConn = 0, addCodeLines = 0, delCodeLines = 0;
                string latestStatsUndoId = null;
                bool turnMutatedGrasshopperCanvas = false;

                if (fullToolCalls.Count > 0)
                {
                    _currentTurnHadToolExecution = true;
                    ShowThinkingAnimation("工作中...");
                    var operationCards = new List<(string primary, string secondary, string undoId)>();

                    foreach (var toolCall in fullToolCalls)
                    {
                        ct.ThrowIfCancellationRequested();
                        string funcName = toolCall["function"]?["name"]?.ToString();
                        string argsJson = toolCall["function"]?["arguments"]?.ToString();
                        string callId = toolCall["id"]?.ToString();

                        JObject argsObj = ChatMessageHelpers.ParseToolArgumentsForExecution(funcName, argsJson, out string cardSum, out string cardDet);
                        int operationCardIndex = -1;
                        if (!string.IsNullOrWhiteSpace(cardSum))
                        {
                            operationCards.Add((cardSum, string.IsNullOrWhiteSpace(cardDet) ? "" : cardDet, null));
                            operationCardIndex = operationCards.Count - 1;
                        }

                        var dispatch = await ExecuteToolCallAsync(
                            funcName,
                            argsObj,
                            argsJson,
                            callId,
                            fullContent,
                            fullReasoning,
                            operationCards.Select(c => (c.primary, c.secondary)).ToList(),
                            ct);

                        if (dispatch.EndApiRoundAwaitingUser)
                        {
                            RecordAgentToolEvidence(funcName, dispatch.ToolResult ?? "");
                            if (operationCards.Count > 0)
                                messageNode["tool_operation_summaries"] = BuildToolOperationSummaryArray(operationCards);
                            SyncActiveHistoryConversation();
                            return dispatch.EarlyResponse ?? new ApiResponse { Content = fullContent, Reasoning = fullReasoning };
                        }

                        string toolResult = dispatch.ToolResult ?? "";
                        addComp += dispatch.AddComp;
                        delComp += dispatch.DelComp;
                        addConn += dispatch.AddConn;
                        delConn += dispatch.DelConn;
                        addCodeLines += dispatch.AddCodeLines;
                        delCodeLines += dispatch.DelCodeLines;
                        dispatch.UndoId = RegisterCanvasUndoRecord(funcName, callId, dispatch.UndoSnapshotPath, toolResult);
                        if (IsCanvasMutatingTool(funcName) && !toolResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                        {
                            turnMutatedGrasshopperCanvas = true;
                        }
                        if (!string.IsNullOrWhiteSpace(dispatch.UndoId))
                        {
                            latestStatsUndoId = dispatch.UndoId;
                        }

                        RecordAgentToolEvidence(funcName, toolResult);
                        var toolMessage = new { role = "tool", tool_call_id = callId, name = funcName, content = toolResult };
                        _messages.Add(toolMessage);
                        AddDisplayMessage(toolMessage);
                    }

                    EnforceChatHistoryLimit();

                    if (operationCards.Count > 0)
                        messageNode["tool_operation_summaries"] = BuildToolOperationSummaryArray(operationCards);

                    if (operationCards.Count > 0)
                        AppendToolOperationCards(operationCards);

                    if (addComp > 0 || delComp > 0 || addConn > 0 || delConn > 0 || addCodeLines > 0 || delCodeLines > 0) {
                        AppendColoredStatsMessage(addComp, delComp, addConn, delConn, addCodeLines, delCodeLines, latestStatsUndoId);
                    }

                    if (turnMutatedGrasshopperCanvas && IsSelfTrainingMode())
                    {
                        MarkSelfTrainingCanvasMutationForReview(operationCards);
                    }
                    else if (turnMutatedGrasshopperCanvas
                        && _agentMode == AgentMode.Create
                        && _finalVisualReviewSourceImages != null
                        && _finalVisualReviewSourceImages.Any(a => a.Kind == AttachmentKind.Image && !string.IsNullOrEmpty(a.Base64)))
                    {
                        _pendingFinalVisualReview = false;
                    }

                    SyncActiveHistoryConversation();
                    ct.ThrowIfCancellationRequested();
                    return await CallLLMAPI(apiKey, depth + 1, ct);
                }

                SyncActiveHistoryConversation();
                return new ApiResponse {
                    Content = fullContent,
                    Reasoning = fullReasoning
                };
        }

        private static Window _ballWindow;
        private static void MinimizeToBall()
        {
            if (_window == null) return;
            _window.Hide();

            if (_ballWindow != null) { _ballWindow.Show(); return; }

            _ballWindow = new Window {
                Width = 50, Height = 50,
                MinWidth = 50, MaxWidth = 50,
                MinHeight = 50, MaxHeight = 50,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                Cursor = Cursors.Hand,
                Left = _window.Left + _window.Width - 60,
                Top = _window.Top + 20
            };

            var border = new Border {
                Background = ThemeBrush(Color.FromRgb(24, 36, 54), Color.FromRgb(40, 40, 40)),
                CornerRadius = new CornerRadius(25),
                BorderThickness = new Thickness(0),
                Child = new TextBlock {
                Text = "✨",
                FontSize = 24,
                    Foreground = ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
                }
            };

            border.MouseLeftButtonDown += (s, e) => {
                if (e.LeftButton == MouseButtonState.Pressed) {
                    if (e.ClickCount >= 2) {
                        ActivateMainWindow();
                    } else {
                        _ballWindow.DragMove();
                    }
                }
            };

            _ballWindow.Content = border;
            _ballWindow.Show();
        }


        private static void UpdateSkillLibraryUI()
        {
            if (_skillContent == null) return;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => {
                _skillContent.Children.Clear();
                string skillsPath = GetSkillsDirectory();
                if (!System.IO.Directory.Exists(skillsPath)) return;

                var files = System.IO.Directory.GetFiles(skillsPath, "*.md");
                if (_txtSkillCount != null) _txtSkillCount.Text = $"({files.Length} 个)";

                var wrap = new WrapPanel { Margin = new Thickness(4, 4, 4, 8) };
                foreach (var file in files) {
                    string fileName = System.IO.Path.GetFileName(file);
                    if (fileName.Equals("index.md", StringComparison.OrdinalIgnoreCase)) continue;

                    string content = System.IO.File.ReadAllText(file, Encoding.UTF8);
                    var match = System.Text.RegularExpressions.Regex.Match(content, @"---\s*name:\s*(.*?)\s*description:\s*(.*?)\s*---", System.Text.RegularExpressions.RegexOptions.Singleline);

                    string name = fileName;
                    string desc = "";
                    if (match.Success) {
                        name = match.Groups[1].Value.Trim();
                        desc = match.Groups[2].Value.Trim();
                    }

                    var card = new Border {
                        Background = ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(28, 28, 28)),
                        CornerRadius = new CornerRadius(6),
                        Width = 160,
                        Height = 70,
                        Margin = new Thickness(3),
                        BorderBrush = ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromRgb(50, 50, 50)),
                        BorderThickness = new Thickness(1),
                        Cursor = Cursors.Hand,
                        ToolTip = desc
                    };

                    var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7) };
                    sp.Children.Add(new TextBlock { Text = name, Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)), FontSize = 12, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
                    sp.Children.Add(new TextBlock { Text = desc, Foreground = ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(140, 140, 140)), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 30, TextWrapping = TextWrapping.Wrap });
                    card.Child = sp;

                    card.MouseLeftButtonDown += (s, e) => {
                        if (_txtInput != null) {
                            _txtInput.Text = $"请参考技能：{name} ({fileName})";
                        }
                    };

                    wrap.Children.Add(card);
                }
                _skillContent.Children.Add(wrap);
            }));
        }

        private static string GetSkillsSummary()
        {
            try {
                string catalogSummary = BuildSkillCatalogSummary();
                if (catalogSummary != null)
                    return catalogSummary;

                string skillsPath = GetSkillsDirectory();
                if (!System.IO.Directory.Exists(skillsPath)) return "";

                var summaries = new List<string>();
                foreach (var file in System.IO.Directory.GetFiles(skillsPath, "*.md")) {
                    string fileName = System.IO.Path.GetFileName(file);
                    if (fileName.Equals("index.md", StringComparison.OrdinalIgnoreCase)) continue;

                    string content = System.IO.File.ReadAllText(file, Encoding.UTF8);
                    // 匹配 YAML Frontmatter: --- name: xxx description: xxx ---
                    var match = System.Text.RegularExpressions.Regex.Match(content, @"---\s*name:\s*(.*?)\s*description:\s*(.*?)\s*---", System.Text.RegularExpressions.RegexOptions.Singleline);

                    if (match.Success) {
                        string name = match.Groups[1].Value.Trim();
                        string desc = match.Groups[2].Value.Trim();
                        summaries.Add($"- [{name}]: {desc} (文件: {fileName})");
                    }
                }

                if (summaries.Count > 0) {
                    return "\n\n【当前项目可用技能库】:\n"
                        + string.Join("\n", summaries)
                        + "\n规则：当用户任务、报错关键词或实现领域与某个 skill 描述匹配时，优先调用 read_skill_file 阅读该文件正文，再规划或修改画布。Skills 是项目经验与官方参考的主要入口，不要只看摘要就凭记忆实现。";
                }
            } catch (Exception ex) {
                AddGhLog.Warn("GetSkillsSummary failed: " + ex.Message);
            }
            return "";
        }
    }
}


