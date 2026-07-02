using System;
using System.Collections.Generic;
using System.Linq;
using Magpie.Agent;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static readonly WorkflowRouter _workflowRouter = new WorkflowRouter();
        private static readonly ToolSurfacePolicy _toolSurfacePolicy = new ToolSurfacePolicy(new ToolRegistry());
        private static readonly ContextLedger _contextLedger = new ContextLedger();
        private static readonly ContextCompactionPlanner _contextCompactionPlanner = new ContextCompactionPlanner();
        private static readonly ContextPackBuilder _contextPackBuilder = new ContextPackBuilder();
        private static readonly ContextPipeline _contextPipeline = new ContextPipeline();
        private static readonly ToolSurfaceBuilder _toolSurfaceBuilder = new ToolSurfaceBuilder();
        private static WorkflowRoute _currentWorkflowRoute = WorkflowRoute.Fallback();
        private static CanvasStateSummary _currentAgentCanvasState = null;

        private static void PrepareAgentWorkflowRoute(string userInput, string modelInput, List<AttachmentItem> attachmentsToSend)
        {
            if (!DeploymentOptions.UseWorkflowRouter)
            {
                _currentWorkflowRoute = WorkflowRoute.Fallback();
                _contextLedger.RecordRoute(_currentWorkflowRoute);
                return;
            }

            try
            {
                var turn = BuildAgentTurnContext(userInput, modelInput, attachmentsToSend);
                var route = _workflowRouter.Route(turn) ?? WorkflowRoute.Fallback();
                _currentWorkflowRoute = route;
                _contextLedger.RecordRoute(route);
                AddGhLog.Debug("Workflow route: " + route.ToLogLine());
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("PrepareAgentWorkflowRoute failed: " + ex.Message);
                _currentWorkflowRoute = WorkflowRoute.Fallback();
                _contextLedger.RecordRoute(_currentWorkflowRoute);
            }
        }

        private static AgentTurnContext BuildAgentTurnContext(string userInput, string modelInput, List<AttachmentItem> attachmentsToSend)
        {
            var canvas = CaptureAgentCanvasStateSummary();
            _currentAgentCanvasState = canvas;
            var attachments = attachmentsToSend ?? new List<AttachmentItem>();

            return new AgentTurnContext
            {
                UserText = string.IsNullOrWhiteSpace(modelInput) ? (userInput ?? "") : modelInput,
                LayoutMode = _layoutMode.ToString(),
                AgentMode = _agentMode.ToString(),
                HasAttachments = attachments.Count > 0,
                HasImageAttachments = attachments.Any(a => a.Kind == AttachmentKind.Image && !string.IsNullOrEmpty(a.Base64)),
                CanvasAvailable = canvas.Available,
                CanvasLikelyEmpty = canvas.LikelyEmpty
            };
        }

        private static CanvasStateSummary CaptureAgentCanvasStateSummary()
        {
            var summary = new CanvasStateSummary();
            try
            {
                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                summary.Available = doc != null;
                summary.ComponentCount = doc?.Objects?.Count ?? 0;
                summary.LikelyEmpty = !summary.Available || summary.ComponentCount == 0;
                summary.Summary = summary.Available
                    ? "Active Grasshopper document with " + summary.ComponentCount + " object(s)."
                    : "No active Grasshopper document.";
                _contextLedger.CanvasState = summary;
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("CaptureAgentCanvasStateSummary failed: " + ex.Message);
                summary.Available = false;
                summary.LikelyEmpty = true;
                summary.Summary = "Canvas state unavailable: " + ex.Message;
            }
            return summary;
        }

        private static string BuildAgentContextLedgerPrompt()
        {
            if (!DeploymentOptions.UseContextLedgerPrompt) return "";
            try
            {
                string rendered = _contextLedger.RenderForPrompt(DeploymentOptions.ContextLedgerPromptMaxChars);
                return string.IsNullOrWhiteSpace(rendered) ? "" : "\n\n" + rendered.Trim();
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("BuildAgentContextLedgerPrompt failed: " + ex.Message);
                return "";
            }
        }

        private static string BuildAgentContextPackPrompt()
        {
            if (!DeploymentOptions.UseContextPackPrompt) return "";
            try
            {
                string rendered = _contextPackBuilder.Build(
                    _currentWorkflowRoute,
                    _currentAgentCanvasState,
                    BuildSkillCatalogSummary,
                    BuildReferenceCatalogSummary,
                    DeploymentOptions.ContextPackPromptMaxChars);
                return string.IsNullOrWhiteSpace(rendered) ? "" : "\n\n" + rendered.Trim();
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("BuildAgentContextPackPrompt failed: " + ex.Message);
                return "";
            }
        }

        private static void ResetAgentContextLedger()
        {
            try
            {
                _contextLedger.ResetForNewConversation();
                _currentWorkflowRoute = WorkflowRoute.Fallback();
                _currentAgentCanvasState = null;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("ResetAgentContextLedger failed: " + ex.Message);
            }
        }

        private static object[] ApplyWorkflowToolSurfacePolicy(object[] toolDefinitions)
        {
            if (!DeploymentOptions.UseToolSurfacePolicy) return toolDefinitions;
            try
            {
                var filtered = _toolSurfacePolicy.FilterForRoute(toolDefinitions, _currentWorkflowRoute, GetToolDefinitionName);
                AddGhLog.Debug("Tool surface policy: "
                    + (toolDefinitions?.Length ?? 0)
                    + " -> "
                    + (filtered?.Length ?? 0)
                    + " for "
                    + (_currentWorkflowRoute?.Intent.ToString() ?? "unknown"));
                return filtered;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("ApplyWorkflowToolSurfacePolicy failed: " + ex.Message);
                return toolDefinitions;
            }
        }

        private static object[] BuildAgentToolSurface(object[] toolDefinitions)
        {
            try
            {
                return _toolSurfaceBuilder.Build(new ToolSurfaceRequest
                {
                    ToolDefinitions = toolDefinitions,
                    UseModeFilters = true,
                    LayoutMode = _layoutMode.ToString(),
                    AgentMode = _agentMode.ToString(),
                    ShowPlanStepsToolName = ShowPlanStepsTool.FunctionName,
                    ShowReferenceOptionsToolName = ShowReferenceOptionsTool.FunctionName,
                    GetToolName = GetToolDefinitionName,
                    WorkflowFilter = ApplyWorkflowToolSurfacePolicy,
                    UseWorkflowFilter = DeploymentOptions.UseToolSurfacePolicy,
                    Route = _currentWorkflowRoute,
                    LogDebug = msg => AddGhLog.Debug(msg)
                }
                .AddPreFilter(FilterToolsForVisionContext));
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("BuildAgentToolSurface failed: " + ex.Message);
                return toolDefinitions;
            }
        }

        private static void RecordAgentToolEvidence(string toolName, string toolResult)
        {
            try
            {
                var envelope = ToolResultCompactor.BuildEnvelope(toolName, toolResult);
                _contextLedger.RecordToolResult(
                    envelope.ToolName,
                    envelope.Success,
                    envelope.Summary,
                    envelope.ArtifactPath);
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("RecordAgentToolEvidence failed: " + ex.Message);
            }
        }

        private static void LogContextCompactionPlan(IList<object> messages, int keepRecent)
        {
            try
            {
                var plan = _contextCompactionPlanner.Plan(messages, keepRecent);
                AddGhLog.Debug("Context compaction plan: " + plan.ToLogLine());
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("LogContextCompactionPlan failed: " + ex.Message);
            }
        }
    }
}
