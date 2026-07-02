using System;
using System.Collections.Generic;
using System.Linq;

namespace Magpie.Agent
{
    public sealed class ToolRegistry
    {
        private readonly Dictionary<string, ToolDescriptor> _tools;

        public ToolRegistry()
        {
            _tools = BuildDefaultTools().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        }

        public ToolDescriptor Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return _tools.TryGetValue(name, out var descriptor) ? descriptor : null;
        }

        public IReadOnlyList<ToolDescriptor> All()
        {
            return _tools.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<ToolDescriptor> BuildDefaultTools()
        {
            yield return Tool("create_ai_image", "AI image generation/editing", false, false, false,
                WorkflowIntent.AiImageGeneration);

            yield return Tool("ensure_gh_canvas", "Ensure Grasshopper canvas exists before mutation", true, false, false,
                WorkflowIntent.GrasshopperCreate, WorkflowIntent.VisualModeling, WorkflowIntent.SelfTraining);
            yield return Tool("get_gh_components", "Inspect current Grasshopper canvas topology, ids, errors, ports, and scripts", true, false, false,
                WorkflowIntent.GrasshopperCreate, WorkflowIntent.GrasshopperModify, WorkflowIntent.CSharpScriptCreate,
                WorkflowIntent.CSharpScriptFix, WorkflowIntent.VisualModeling, WorkflowIntent.SelfTraining, WorkflowIntent.GeneralChat);
            yield return Tool("check_gh_errors", "Check Grasshopper runtime errors and invalid outputs", true, false, false,
                WorkflowIntent.GrasshopperCreate, WorkflowIntent.GrasshopperModify, WorkflowIntent.CSharpScriptFix,
                WorkflowIntent.VisualModeling, WorkflowIntent.SelfTraining);
            yield return Tool("recompute_gh_canvas", "Recompute the active Grasshopper canvas after changes", false, true, false,
                WorkflowIntent.GrasshopperCreate, WorkflowIntent.GrasshopperModify, WorkflowIntent.CSharpScriptFix,
                WorkflowIntent.VisualModeling, WorkflowIntent.SelfTraining);

            yield return Tool("add_gh_component", "Add one Grasshopper component; prefer batch graph creation for new blocks", false, true, false,
                WorkflowIntent.GrasshopperCreate, WorkflowIntent.GrasshopperModify, WorkflowIntent.VisualModeling, WorkflowIntent.SelfTraining);
            yield return Tool("connect_gh_components", "Connect existing Grasshopper component ports", false, true, false,
                WorkflowIntent.GrasshopperCreate, WorkflowIntent.GrasshopperModify, WorkflowIntent.VisualModeling, WorkflowIntent.SelfTraining);
            yield return Tool("remove_gh_component", "Remove a Grasshopper component by public id", false, true, false,
                WorkflowIntent.GrasshopperModify, WorkflowIntent.SelfTraining);
            yield return Tool("remove_gh_connection", "Remove a Grasshopper connection", false, true, false,
                WorkflowIntent.GrasshopperModify, WorkflowIntent.SelfTraining);
            yield return Tool("set_gh_component_value", "Set slider/panel/value-list/helper component values", false, true, false,
                WorkflowIntent.GrasshopperCreate, WorkflowIntent.GrasshopperModify, WorkflowIntent.VisualModeling,
                WorkflowIntent.CSharpScriptCreate, WorkflowIntent.CSharpScriptFix, WorkflowIntent.SelfTraining);
            yield return Tool("create_component_graph", "Batch-create a local Grasshopper component graph with components and connections", false, true, false,
                WorkflowIntent.GrasshopperCreate, WorkflowIntent.GrasshopperModify, WorkflowIntent.VisualModeling, WorkflowIntent.SelfTraining);

            yield return Tool("create_csharp_script_component", "Create a C# Script component with ports, body, helper components, and optional group", false, true, false,
                WorkflowIntent.CSharpScriptCreate, WorkflowIntent.GrasshopperCreate, WorkflowIntent.VisualModeling, WorkflowIntent.SelfTraining);
            yield return Tool("edit_csharp_script_component", "Edit an existing C# Script component body", false, true, false,
                WorkflowIntent.CSharpScriptFix, WorkflowIntent.GrasshopperModify, WorkflowIntent.SelfTraining);
            yield return DeferredTool("create_script_component_graph", "Legacy script graph creation path", false, true, false,
                WorkflowIntent.GrasshopperCreate, WorkflowIntent.SelfTraining);
            yield return DeferredTool("gh_native_script_editor", "Fallback native script editor integration", false, true, false,
                WorkflowIntent.CSharpScriptFix, WorkflowIntent.GrasshopperModify);
            yield return DeferredTool("modify_gh_component_ports", "Fallback dynamic port repair tool", false, true, false,
                WorkflowIntent.CSharpScriptFix, WorkflowIntent.GrasshopperModify);

            yield return Tool("search_component_library", "Search local component library by name/category", true, false, false,
                WorkflowIntent.GrasshopperCreate, WorkflowIntent.GrasshopperModify, WorkflowIntent.VisualModeling);
            yield return Tool("search_gh_component_catalog", "Search Grasshopper component catalog when exact component identity is unknown", true, false, false,
                WorkflowIntent.GrasshopperCreate, WorkflowIntent.GrasshopperModify, WorkflowIntent.VisualModeling);
            yield return Tool("query_gh_components", "Query current GH components with focused criteria", true, false, false,
                WorkflowIntent.GrasshopperModify, WorkflowIntent.CSharpScriptFix);
            yield return Tool("get_component_context", "Read focused context for one component", true, false, false,
                WorkflowIntent.GrasshopperModify, WorkflowIntent.CSharpScriptFix);
            yield return Tool("read_component_script", "Read script/source from an existing component", true, false, false,
                WorkflowIntent.CSharpScriptFix, WorkflowIntent.GrasshopperModify);

            yield return Tool("read_skill_file", "Load one relevant skill body by file name", true, false, false,
                WorkflowIntent.SkillLookup, WorkflowIntent.GrasshopperCreate, WorkflowIntent.GrasshopperModify,
                WorkflowIntent.CSharpScriptCreate, WorkflowIntent.CSharpScriptFix, WorkflowIntent.VisualModeling,
                WorkflowIntent.SelfTraining);
            yield return DeferredTool("create_gh_skill", "Create a new skill markdown file; should be gated to skill authoring/self-training", false, false, true,
                WorkflowIntent.SkillAuthoring, WorkflowIntent.SelfTraining);

            yield return Tool("read_reference_json", "Read one saved reference JSON", true, false, false,
                WorkflowIntent.ReferenceLookup, WorkflowIntent.ReferenceImport, WorkflowIntent.GrasshopperCreate,
                WorkflowIntent.GrasshopperModify, WorkflowIntent.SelfTraining);
            yield return Tool("import_reference_gh", "Import a saved reference GH/GHX file into the active canvas", false, true, false,
                WorkflowIntent.ReferenceImport, WorkflowIntent.SelfTraining);
            yield return Tool("show_reference_options", "Ask user to choose among reference options", true, false, false,
                WorkflowIntent.ReferenceLookup, WorkflowIntent.ReferenceImport);

            yield return Tool("web_research", "Fetch/search local mirrored documentation", true, false, false,
                WorkflowIntent.WebResearch, WorkflowIntent.ApiDocLookup, WorkflowIntent.CSharpScriptCreate, WorkflowIntent.CSharpScriptFix);
            yield return Tool("show_plan_steps", "Render plan steps in Plan mode", true, false, false,
                WorkflowIntent.GeneralChat, WorkflowIntent.GrasshopperCreate, WorkflowIntent.GrasshopperModify);
        }

        private static ToolDescriptor Tool(string name, string useCase, bool readOnly, bool mutatesCanvas, bool writesFiles, params WorkflowIntent[] workflows)
        {
            var d = new ToolDescriptor
            {
                Name = name,
                Description = useCase,
                CanonicalUseCase = useCase,
                IsReadOnly = readOnly,
                MutatesCanvas = mutatesCanvas,
                WritesFiles = writesFiles,
                TokenCostRank = readOnly ? 1 : 2
            };
            d.IntendedWorkflows.AddRange(workflows);
            return d;
        }

        private static ToolDescriptor DeferredTool(string name, string useCase, bool readOnly, bool mutatesCanvas, bool writesFiles, params WorkflowIntent[] workflows)
        {
            var d = Tool(name, useCase, readOnly, mutatesCanvas, writesFiles, workflows);
            d.Lifecycle = ToolLifecycle.Deferred;
            return d;
        }
    }
}
