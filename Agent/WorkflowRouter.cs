using System;

namespace Magpie.Agent
{
    public sealed class WorkflowRouter
    {
        private readonly WorkflowSignalExtractor _signalExtractor;

        public WorkflowRouter()
            : this(new WorkflowSignalExtractor())
        {
        }

        public WorkflowRouter(WorkflowSignalExtractor signalExtractor)
        {
            _signalExtractor = signalExtractor ?? new WorkflowSignalExtractor();
        }

        public WorkflowRoute Route(AgentTurnContext context)
        {
            if (context == null) return WorkflowRoute.Fallback();

            var signals = _signalExtractor.Extract(context);

            if (IsSelfTraining(context))
                return SelfTrainingRoute();

            if (signals.HasImageAttachments)
                return RouteImageTurn(context, signals);

            double apiDocScore = signals.ApiDocLookupScore();
            if (apiDocScore >= 0.62)
                return ApiDocLookupRoute(apiDocScore, signals);

            if (signals.UserAskedWebResearch)
                return WebResearchRoute();

            if (signals.UserAskedReferenceImport)
                return ReferenceImportRoute();

            if (signals.UserAskedReferenceLookup)
                return ReferenceLookupRoute();

            if (signals.UserAskedSkillLookup)
                return SkillLookupRoute();

            if (signals.HasCSharpSignal || signals.HasCSharpCodeBlock || signals.HasCompileError)
            {
                if (signals.UserAskedCreate)
                    return CSharpCreateRoute(signals);
                return CSharpFixRoute(signals);
            }

            if (signals.UserAskedModify)
                return GrasshopperModifyRoute(context);

            if (signals.UserAskedCreate || signals.UserAskedGrasshopper)
                return GrasshopperCreateRoute(context);

            return GeneralRoute(context);
        }

        private static WorkflowRoute RouteImageTurn(AgentTurnContext context, WorkflowSignals signals)
        {
            if (signals.UserAskedImageGeneration)
            {
                var route = WorkflowRoute.Create(WorkflowIntent.AiImageGeneration, 0.92, "Image attachment with AI image generation/editing intent.");
                route.RequiredTools.Add("create_ai_image");
                route.ContextPacks.Add("image-input");
                return route;
            }

            if (signals.UserAskedVisualModeling)
            {
                var route = WorkflowRoute.Create(WorkflowIntent.VisualModeling, 0.86, "Image attachment with Grasshopper/Rhino modeling intent.");
                route.AllowsCanvasMutation = true;
                route.RequiresVisualReview = true;
                route.RequiredTools.Add("ensure_gh_canvas");
                route.RequiredTools.Add("get_gh_components");
                route.OptionalTools.Add("create_component_graph");
                route.OptionalTools.Add("create_csharp_script_component");
                route.ContextPacks.Add("image-input");
                route.ContextPacks.Add("canvas-state");
                return route;
            }

            bool unclear = string.IsNullOrWhiteSpace(context.UserText);
            var visual = WorkflowRoute.Create(
                WorkflowIntent.VisualUnderstanding,
                unclear ? 0.55 : 0.76,
                unclear
                    ? "Image attachment without a clear text goal; ask for intent before mutating canvas."
                    : "Image attachment appears to need explanation, diagnosis, or discussion rather than canvas mutation.");
            visual.ContextPacks.Add("image-input");
            visual.ShouldAskClarifyingQuestion = unclear;
            return visual;
        }

        private static WorkflowRoute SelfTrainingRoute()
        {
            var route = WorkflowRoute.Create(WorkflowIntent.SelfTraining, 0.95, "Current agent mode is self-training.");
            route.AllowsCanvasMutation = true;
            route.AllowsSkillWrite = true;
            route.RequiresVisualReview = true;
            route.RequiredTools.Add("ensure_gh_canvas");
            route.RequiredTools.Add("get_gh_components");
            route.OptionalTools.Add("create_component_graph");
            route.OptionalTools.Add("create_csharp_script_component");
            route.OptionalTools.Add("edit_csharp_script_component");
            route.OptionalTools.Add("create_gh_skill");
            route.ContextPacks.Add("self-training");
            route.ContextPacks.Add("skills-index");
            return route;
        }

        private static WorkflowRoute ApiDocLookupRoute(double score, WorkflowSignals signals)
        {
            var route = WorkflowRoute.Create(
                WorkflowIntent.ApiDocLookup,
                score,
                "Structured API documentation lookup recommended from signals: " + signals.ExplainApiDocSignals() + ".");
            route.RequiredTools.Add("web_research");
            route.OptionalTools.Add("read_skill_file");
            route.OptionalTools.Add("create_csharp_script_component");
            route.OptionalTools.Add("edit_csharp_script_component");
            route.OptionalTools.Add("get_gh_components");
            route.OptionalTools.Add("recompute_gh_canvas");
            route.ContextPacks.Add("api-doc-lookup");
            route.ContextPacks.Add("web-research");
            route.ContextPacks.Add("skills-index");
            return route;
        }

        private static WorkflowRoute WebResearchRoute()
        {
            var route = WorkflowRoute.Create(WorkflowIntent.WebResearch, 0.82, "User requested local documentation lookup or mirrored URL verification.");
            route.RequiredTools.Add("web_research");
            route.ContextPacks.Add("web-research");
            return route;
        }

        private static WorkflowRoute ReferenceImportRoute()
        {
            var route = WorkflowRoute.Create(WorkflowIntent.ReferenceImport, 0.82, "User requested importing or reusing a saved reference canvas.");
            route.AllowsCanvasMutation = true;
            route.RequiredTools.Add("read_reference_json");
            route.RequiredTools.Add("import_reference_gh");
            route.ContextPacks.Add("reference-index");
            route.ContextPacks.Add("canvas-state");
            return route;
        }

        private static WorkflowRoute ReferenceLookupRoute()
        {
            var route = WorkflowRoute.Create(WorkflowIntent.ReferenceLookup, 0.74, "User mentioned references; inspect relevant reference entries before importing.");
            route.RequiredTools.Add("read_reference_json");
            route.OptionalTools.Add("show_reference_options");
            route.ContextPacks.Add("reference-index");
            return route;
        }

        private static WorkflowRoute SkillLookupRoute()
        {
            var route = WorkflowRoute.Create(WorkflowIntent.SkillLookup, 0.70, "User mentioned skills or reusable experience.");
            route.RequiredTools.Add("read_skill_file");
            route.ContextPacks.Add("skills-index");
            return route;
        }

        private static WorkflowRoute CSharpCreateRoute(WorkflowSignals signals)
        {
            var route = WorkflowRoute.Create(WorkflowIntent.CSharpScriptCreate, 0.78, "User request likely needs a new C# Script component.");
            route.AllowsCanvasMutation = true;
            route.RequiredTools.Add("create_csharp_script_component");
            route.OptionalTools.Add("get_gh_components");
            route.OptionalTools.Add("recompute_gh_canvas");
            AddApiLookupAffordance(route, signals);
            route.ContextPacks.Add("canvas-state");
            route.ContextPacks.Add("skills-index");
            return route;
        }

        private static WorkflowRoute CSharpFixRoute(WorkflowSignals signals)
        {
            var route = WorkflowRoute.Create(WorkflowIntent.CSharpScriptFix, 0.78, "User request mentions C# Script, errors, or script repair.");
            route.AllowsCanvasMutation = true;
            route.RequiredTools.Add("get_gh_components");
            route.RequiredTools.Add("edit_csharp_script_component");
            route.OptionalTools.Add("recompute_gh_canvas");
            AddApiLookupAffordance(route, signals);
            route.ContextPacks.Add("canvas-state");
            route.ContextPacks.Add("skills-index");
            return route;
        }

        private static void AddApiLookupAffordance(WorkflowRoute route, WorkflowSignals signals)
        {
            if (route == null || signals == null) return;
            if (signals.ApiDocLookupScore() < 0.35) return;
            route.OptionalTools.Add("web_research");
            route.ContextPacks.Add("api-doc-lookup");
            route.Reason += " API lookup is available if the agent cannot verify a RhinoCommon/Grasshopper signature from local context.";
        }

        private static WorkflowRoute GrasshopperModifyRoute(AgentTurnContext context)
        {
            var route = WorkflowRoute.Create(WorkflowIntent.GrasshopperModify, 0.72, "User request appears to modify an existing Grasshopper canvas.");
            route.AllowsCanvasMutation = true;
            route.RequiredTools.Add("get_gh_components");
            route.OptionalTools.Add("create_component_graph");
            route.OptionalTools.Add("set_gh_component_value");
            route.OptionalTools.Add("connect_gh_components");
            route.OptionalTools.Add("remove_gh_component");
            route.ContextPacks.Add("canvas-state");
            route.ContextPacks.Add("skills-index");
            if (!context.CanvasAvailable || context.CanvasLikelyEmpty)
                route.Reason += " Canvas may be empty; verify before assuming existing components.";
            return route;
        }

        private static WorkflowRoute GrasshopperCreateRoute(AgentTurnContext context)
        {
            var route = WorkflowRoute.Create(WorkflowIntent.GrasshopperCreate, 0.70, "User request appears to create a Grasshopper definition.");
            route.AllowsCanvasMutation = true;
            route.RequiredTools.Add("ensure_gh_canvas");
            route.OptionalTools.Add("create_component_graph");
            route.OptionalTools.Add("create_csharp_script_component");
            route.OptionalTools.Add("get_gh_components");
            route.ContextPacks.Add("canvas-state");
            route.ContextPacks.Add("skills-index");
            return route;
        }

        private static WorkflowRoute GeneralRoute(AgentTurnContext context)
        {
            var route = WorkflowRoute.Create(WorkflowIntent.GeneralChat, 0.45, "No high-confidence specialized workflow matched.");
            route.ContextPacks.Add("general");
            if (context.CanvasAvailable && !context.CanvasLikelyEmpty)
                route.OptionalTools.Add("get_gh_components");
            route.OptionalTools.Add("read_skill_file");
            return route;
        }

        private static bool IsSelfTraining(AgentTurnContext context)
        {
            return string.Equals(context.AgentMode, "SelfTrain", StringComparison.OrdinalIgnoreCase);
        }
    }
}
