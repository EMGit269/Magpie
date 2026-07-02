using System;
using System.Collections.Generic;
using System.Linq;

namespace Magpie.Agent
{
    public sealed class ContextLedger
    {
        private const int MaxToolEvidence = 40;
        private const int MaxLoadedSkills = 20;
        private const int MaxReferences = 20;

        public WorkflowRoute CurrentRoute { get; private set; }
        public List<ToolEvidence> ToolEvidence { get; private set; }
        public List<LoadedSkillEvidence> LoadedSkills { get; private set; }
        public List<ReferenceEvidence> References { get; private set; }
        public List<DecisionEvidence> Decisions { get; private set; }
        public CanvasStateSummary CanvasState { get; set; }

        public ContextLedger()
        {
            CurrentRoute = WorkflowRoute.Fallback();
            ToolEvidence = new List<ToolEvidence>();
            LoadedSkills = new List<LoadedSkillEvidence>();
            References = new List<ReferenceEvidence>();
            Decisions = new List<DecisionEvidence>();
        }

        public void ResetForNewConversation()
        {
            CurrentRoute = WorkflowRoute.Fallback();
            ToolEvidence.Clear();
            LoadedSkills.Clear();
            References.Clear();
            Decisions.Clear();
            CanvasState = null;
        }

        public void RecordRoute(WorkflowRoute route)
        {
            if (route != null)
                CurrentRoute = route;
        }

        public void RecordToolResult(string toolName, bool success, string summary, string artifactPath = null)
        {
            ToolEvidence.Add(new ToolEvidence
            {
                ToolName = toolName ?? "",
                Success = success,
                Summary = Compact(summary, 320),
                ArtifactPath = artifactPath ?? "",
                TimestampUtc = DateTime.UtcNow
            });
            Trim(ToolEvidence, MaxToolEvidence);
        }

        public void RecordLoadedSkill(string id, string path, string reason)
        {
            LoadedSkills.Add(new LoadedSkillEvidence
            {
                Id = id ?? "",
                Path = path ?? "",
                Reason = Compact(reason, 240),
                TimestampUtc = DateTime.UtcNow
            });
            Trim(LoadedSkills, MaxLoadedSkills);
        }

        public void RecordReference(string id, string path, string summary)
        {
            References.Add(new ReferenceEvidence
            {
                Id = id ?? "",
                Path = path ?? "",
                Summary = Compact(summary, 240),
                TimestampUtc = DateTime.UtcNow
            });
            Trim(References, MaxReferences);
        }

        public void RecordDecision(string decision, string reason)
        {
            Decisions.Add(new DecisionEvidence
            {
                Decision = Compact(decision, 240),
                Reason = Compact(reason, 320),
                TimestampUtc = DateTime.UtcNow
            });
            Trim(Decisions, 30);
        }

        public string RenderForPrompt(int maxChars)
        {
            var lines = new List<string> { "## Agent Context Ledger" };

            if (CurrentRoute != null)
                lines.Add(CurrentRoute.RenderForPrompt());

            if (CanvasState != null)
            {
                lines.Add("## Canvas State");
                lines.Add("- available: " + CanvasState.Available.ToString().ToLowerInvariant());
                lines.Add("- likely_empty: " + CanvasState.LikelyEmpty.ToString().ToLowerInvariant());
                lines.Add("- component_count: " + CanvasState.ComponentCount);
                if (!string.IsNullOrWhiteSpace(CanvasState.Summary))
                    lines.Add("- summary: " + Compact(CanvasState.Summary, 240));
            }

            AppendRecent(lines, "Recent Tool Evidence", ToolEvidence.Select(e =>
                "- " + e.ToolName + ": " + (e.Success ? "success" : "failed") + "; " + Compact(e.Summary, 220)), 6);

            AppendRecent(lines, "Loaded Skills", LoadedSkills.Select(s =>
                "- " + s.Id + " (" + s.Path + "): " + Compact(s.Reason, 180)), 5);

            AppendRecent(lines, "Reference Evidence", References.Select(r =>
                "- " + r.Id + " (" + r.Path + "): " + Compact(r.Summary, 180)), 5);

            AppendRecent(lines, "Decisions", Decisions.Select(d =>
                "- " + Compact(d.Decision, 160) + ": " + Compact(d.Reason, 180)), 5);

            var text = string.Join(Environment.NewLine, lines);
            return Compact(text, maxChars <= 0 ? 4000 : maxChars);
        }

        private static void AppendRecent(List<string> lines, string title, IEnumerable<string> entries, int limit)
        {
            var recent = entries == null ? new List<string>() : entries.Reverse().Take(limit).Reverse().ToList();
            if (recent.Count == 0) return;
            lines.Add("## " + title);
            lines.AddRange(recent);
        }

        private static void Trim<T>(List<T> list, int max)
        {
            if (list == null || max <= 0) return;
            while (list.Count > max)
                list.RemoveAt(0);
        }

        private static string Compact(string value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var s = string.Join(" ", value.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            return s.Length <= maxChars ? s : s.Substring(0, Math.Max(0, maxChars - 3)) + "...";
        }
    }

    public sealed class ToolEvidence
    {
        public string ToolName { get; set; }
        public bool Success { get; set; }
        public string Summary { get; set; }
        public string ArtifactPath { get; set; }
        public DateTime TimestampUtc { get; set; }
    }

    public sealed class LoadedSkillEvidence
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public string Reason { get; set; }
        public DateTime TimestampUtc { get; set; }
    }

    public sealed class ReferenceEvidence
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public string Summary { get; set; }
        public DateTime TimestampUtc { get; set; }
    }

    public sealed class DecisionEvidence
    {
        public string Decision { get; set; }
        public string Reason { get; set; }
        public DateTime TimestampUtc { get; set; }
    }

    public sealed class CanvasStateSummary
    {
        public bool Available { get; set; }
        public bool LikelyEmpty { get; set; }
        public int ComponentCount { get; set; }
        public string Summary { get; set; }
    }
}
