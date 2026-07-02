using System;
using System.Collections.Generic;
using System.Linq;

namespace Magpie.Agent
{
    public sealed class WorkflowRoute
    {
        public WorkflowIntent Intent { get; set; }
        public double Confidence { get; set; }
        public string Reason { get; set; }
        public List<string> RequiredTools { get; private set; }
        public List<string> OptionalTools { get; private set; }
        public List<string> ContextPacks { get; private set; }
        public bool RequiresVisualReview { get; set; }
        public bool AllowsCanvasMutation { get; set; }
        public bool AllowsSkillWrite { get; set; }
        public bool ShouldAskClarifyingQuestion { get; set; }

        public WorkflowRoute()
        {
            RequiredTools = new List<string>();
            OptionalTools = new List<string>();
            ContextPacks = new List<string>();
            Reason = "";
        }

        public static WorkflowRoute Create(WorkflowIntent intent, double confidence, string reason)
        {
            return new WorkflowRoute
            {
                Intent = intent,
                Confidence = Math.Max(0, Math.Min(1, confidence)),
                Reason = reason ?? ""
            };
        }

        public static WorkflowRoute Fallback()
        {
            var route = Create(WorkflowIntent.GeneralChat, 0.25, "No explicit workflow route has been established.");
            route.ContextPacks.Add("general");
            return route;
        }

        public string ToLogLine()
        {
            return string.Format(
                "intent={0}, confidence={1:0.00}, canvas_mutation={2}, skill_write={3}, visual_review={4}, reason={5}",
                Intent,
                Confidence,
                AllowsCanvasMutation,
                AllowsSkillWrite,
                RequiresVisualReview,
                Compact(Reason, 180));
        }

        public string RenderForPrompt()
        {
            var lines = new List<string>
            {
                "## Current Workflow Route",
                "- intent: " + Intent,
                "- confidence: " + Confidence.ToString("0.00"),
                "- reason: " + Compact(Reason, 240),
                "- allows_canvas_mutation: " + AllowsCanvasMutation.ToString().ToLowerInvariant(),
                "- allows_skill_write: " + AllowsSkillWrite.ToString().ToLowerInvariant(),
                "- requires_visual_review: " + RequiresVisualReview.ToString().ToLowerInvariant()
            };

            if (RequiredTools.Count > 0)
                lines.Add("- required_tools: " + string.Join(", ", RequiredTools.Distinct()));
            if (OptionalTools.Count > 0)
                lines.Add("- optional_tools: " + string.Join(", ", OptionalTools.Distinct()));
            if (ContextPacks.Count > 0)
                lines.Add("- context_packs: " + string.Join(", ", ContextPacks.Distinct()));
            if (ShouldAskClarifyingQuestion)
                lines.Add("- next_action: ask one concise clarifying question before mutating the canvas");

            return string.Join(Environment.NewLine, lines);
        }

        private static string Compact(string value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var s = string.Join(" ", value.Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return s.Length <= maxChars ? s : s.Substring(0, Math.Max(0, maxChars - 3)) + "...";
        }
    }
}
