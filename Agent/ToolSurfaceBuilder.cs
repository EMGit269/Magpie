using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Magpie.Agent
{
    public sealed class ToolSurfaceRequest
    {
        public object[] ToolDefinitions { get; set; }
        public List<Func<object[], object[]>> PreFilters { get; private set; }
        public Func<object[], object[]> WorkflowFilter { get; set; }
        public bool UseWorkflowFilter { get; set; }
        public bool UseModeFilters { get; set; }
        public string LayoutMode { get; set; }
        public string AgentMode { get; set; }
        public string ShowPlanStepsToolName { get; set; }
        public string ShowReferenceOptionsToolName { get; set; }
        public Func<object, string> GetToolName { get; set; }
        public WorkflowRoute Route { get; set; }
        public Action<string> LogDebug { get; set; }

        public ToolSurfaceRequest()
        {
            PreFilters = new List<Func<object[], object[]>>();
        }

        public ToolSurfaceRequest AddPreFilter(Func<object[], object[]> filter)
        {
            if (filter != null)
                PreFilters.Add(filter);
            return this;
        }
    }

    public sealed class ToolSurfaceBuilder
    {
        public object[] Build(ToolSurfaceRequest request)
        {
            if (request == null) return null;
            object[] current = request.ToolDefinitions;
            if (current == null) return null;

            if (request.UseModeFilters)
                current = ApplyModeFilters(current, request);

            foreach (var filter in request.PreFilters ?? new List<Func<object[], object[]>>())
            {
                if (filter == null) continue;
                current = filter(current) ?? current;
            }

            int beforeWorkflow = current.Length;
            if (request.UseWorkflowFilter && request.WorkflowFilter != null)
                current = request.WorkflowFilter(current) ?? current;

            request.LogDebug?.Invoke(
                "Tool surface built: "
                + (request.ToolDefinitions?.Length ?? 0)
                + " -> "
                + beforeWorkflow
                + " -> "
                + (current?.Length ?? 0)
                + " for "
                + (request.Route?.Intent.ToString() ?? "unknown"));

            return current?.ToArray();
        }

        private static object[] ApplyModeFilters(object[] toolDefinitions, ToolSurfaceRequest request)
        {
            var afterLayout = FilterForLayoutMode(toolDefinitions, request);
            return FilterForAgentMode(afterLayout, request);
        }

        private static object[] FilterForLayoutMode(object[] toolDefinitions, ToolSurfaceRequest request)
        {
            if (toolDefinitions == null) return toolDefinitions;

            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "capture_rhino_viewport",
                "prepare_visual_review_preview"
            };

            if (IsMode(request.AgentMode, "SelfTrain"))
                blocked.Add("create_gh_skill");

            if (IsMode(request.LayoutMode, "Battery"))
            {
                blocked.Add("create_csharp_script_component");
            }
            else if (IsMode(request.LayoutMode, "CSharpFirst"))
            {
                blocked.Add("create_script_component_graph");
                blocked.Add("gh_native_script_editor");
                blocked.Add("modify_gh_component_ports");
                if (IsMode(request.AgentMode, "Create"))
                {
                    blocked.Add("read_reference_json");
                    blocked.Add("create_gh_skill");
                    if (!string.IsNullOrWhiteSpace(request.ShowReferenceOptionsToolName))
                        blocked.Add(request.ShowReferenceOptionsToolName);
                }
            }

            return toolDefinitions
                .Where(t => !blocked.Contains(GetName(request, t) ?? ""))
                .Select(t => RestrictToolForCSharpFirstMode(t, request))
                .ToArray();
        }

        private static object[] FilterForAgentMode(object[] toolDefinitions, ToolSurfaceRequest request)
        {
            if (toolDefinitions == null) return toolDefinitions;
            string planTool = request.ShowPlanStepsToolName ?? "";

            if (!IsMode(request.AgentMode, "Plan"))
            {
                return toolDefinitions
                    .Where(t => !string.Equals(GetName(request, t), planTool, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "get_gh_components",
                "check_gh_errors",
                "search_component_library",
                "search_gh_component_catalog",
                "query_gh_components",
                "get_component_context",
                "read_component_script",
                "read_skill_file",
                "read_reference_json",
                "import_reference_gh",
                "web_research"
            };
            if (!string.IsNullOrWhiteSpace(planTool))
                allowed.Add(planTool);

            return toolDefinitions
                .Where(t => allowed.Contains(GetName(request, t) ?? ""))
                .ToArray();
        }

        private static object RestrictToolForCSharpFirstMode(object toolDefinition, ToolSurfaceRequest request)
        {
            string name = GetName(request, toolDefinition);
            if (!IsMode(request.LayoutMode, "CSharpFirst"))
                return toolDefinition;

            if (string.Equals(name, "add_gh_component", StringComparison.OrdinalIgnoreCase))
            {
                JObject jo = JObject.FromObject(toolDefinition);
                var fn = jo["function"] as JObject;
                if (fn != null)
                    fn["description"] = "C# priority mode controlled helper tool. Params and Display components may be added directly as inputs, outputs, panels, previews, or diagnostics around C# Script. Other Grasshopper components are allowed only when explicitly justified with csharp_first_helper_reason. Do not replace core C# modeling logic with ordinary GH chains.";
                return AddCSharpFirstHelperReasonFields(jo);
            }

            if (string.Equals(name, "create_component_graph", StringComparison.OrdinalIgnoreCase))
            {
                JObject jo = JObject.FromObject(toolDefinition);
                var fn = jo["function"] as JObject;
                if (fn != null)
                    fn["description"] = "C# priority mode controlled batch helper tool. Params and Display components may be created directly around C# Script. Any other GH component in the batch requires csharp_first_helper_reason. Keep non-script graph logic small and justified; put main modeling logic in create_csharp_script_component.";
                return AddCSharpFirstHelperReasonFields(jo);
            }

            if (string.Equals(name, "set_gh_component_value", StringComparison.OrdinalIgnoreCase))
            {
                JObject jo = JObject.FromObject(toolDefinition);
                var fn = jo["function"] as JObject;
                if (fn != null)
                    fn["description"] = "C# priority helper value tool. Use only for non-script helper values such as Slider or Panel. Do not use it to edit C# Script source; use edit_csharp_script_component for C# body edits.";
                return jo;
            }

            if (string.Equals(name, "modify_gh_component_ports", StringComparison.OrdinalIgnoreCase))
            {
                JObject jo = JObject.FromObject(toolDefinition);
                var fn = jo["function"] as JObject;
                if (fn != null)
                    fn["description"] = "C# priority fallback repair tool for dynamic ports. Do not use this as the normal way to change C# Script inputs or outputs; prefer create_csharp_script_component for new scripts and edit_csharp_script_component for existing script logic. Use only when a C# Script or other variable-parameter component is visibly out of sync and a direct port repair is required.";
                return jo;
            }

            return toolDefinition;
        }

        private static JObject AddCSharpFirstHelperReasonFields(JObject toolDefinition)
        {
            var parameters = toolDefinition["function"]?["parameters"] as JObject;
            var properties = parameters?["properties"] as JObject;
            if (properties == null) return toolDefinition;

            properties["csharp_first_helper_reason"] = new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray("component_more_efficient", "user_requested_component"),
                ["description"] = "Required in C# priority mode only when adding non-Params/non-Display GH components. Choose exactly one reason: component_more_efficient, or user_requested_component."
            };
            properties["csharp_first_helper_reason_detail"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Optional short Chinese explanation of why this non-script component/graph is justified."
            };

            return toolDefinition;
        }

        private static string GetName(ToolSurfaceRequest request, object tool)
        {
            return request?.GetToolName == null ? null : request.GetToolName(tool);
        }

        private static bool IsMode(string value, string expected)
        {
            return string.Equals(value ?? "", expected, StringComparison.OrdinalIgnoreCase);
        }
    }
}
