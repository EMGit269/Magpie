using System;
using System.Collections.Generic;
using System.Linq;

namespace Magpie.Agent
{
    public sealed class ContextPackBuilder
    {
        public string Build(
            WorkflowRoute route,
            CanvasStateSummary canvasState,
            Func<string> skillSummaryProvider,
            Func<string> referenceSummaryProvider,
            int maxChars)
        {
            if (route == null) return "";
            var packs = new HashSet<string>(route.ContextPacks ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            if (packs.Count == 0) return "";

            var sections = new List<string>
            {
                route.RenderForPrompt()
            };

            if (packs.Contains("canvas-state") && canvasState != null)
                sections.Add(RenderCanvasState(canvasState));

            if (packs.Contains("skills-index"))
                sections.Add("## Skills Index Pack\nUse the available skill catalog summary already provided in system context. Read a matching skill body with read_skill_file before relying on specialized project knowledge.");

            if (packs.Contains("reference-index"))
                AddProviderSection(sections, referenceSummaryProvider);

            if (packs.Contains("api-doc-lookup"))
                sections.Add(RenderApiDocLookupGuidance());

            if (packs.Contains("web-research"))
                sections.Add(RenderWebResearchGuidance());

            if (packs.Contains("image-input"))
                sections.Add("## Image Input Pack\nUse image preprocessing and user text to infer intent. Do not mutate the Grasshopper canvas unless the user clearly requested modeling or editing.");

            if (packs.Contains("self-training"))
                sections.Add("## Self Training Pack\nSkill writes are allowed only for reusable workflow knowledge. Keep generated skills concise, scoped, and verifiable.");

            string text = string.Join(Environment.NewLine + Environment.NewLine, sections.Where(s => !string.IsNullOrWhiteSpace(s)));
            return Compact(text, maxChars <= 0 ? 8000 : maxChars);
        }

        private static void AddProviderSection(List<string> sections, Func<string> provider)
        {
            if (provider == null) return;
            string value = provider();
            if (!string.IsNullOrWhiteSpace(value))
                sections.Add(value.Trim());
        }

        private static string RenderCanvasState(CanvasStateSummary canvas)
        {
            var lines = new List<string>
            {
                "## Canvas State Pack",
                "- available: " + canvas.Available.ToString().ToLowerInvariant(),
                "- likely_empty: " + canvas.LikelyEmpty.ToString().ToLowerInvariant(),
                "- component_count: " + canvas.ComponentCount
            };
            if (!string.IsNullOrWhiteSpace(canvas.Summary))
                lines.Add("- summary: " + Compact(canvas.Summary, 240));
            return string.Join(Environment.NewLine, lines);
        }

        private static string RenderApiDocLookupGuidance()
        {
            return "## API Documentation Lookup Pack\n"
                + "- If RhinoCommon/Grasshopper type, method, constructor, overload, parameter, or return type is uncertain, call web_research with mode=api_pipeline.\n"
                + "- web_research reads local mirrored documentation only; it does not access the public internet.\n"
                + "- After api_pipeline returns candidates, fetch the selected mirrored official URL before relying on an API signature.\n"
                + "- Treat no candidate as name-mismatch evidence, not proof that an API does not exist.";
        }

        private static string RenderWebResearchGuidance()
        {
            return "## Web Research Pack\n"
                + "- web_research is a local documentation lookup tool, not a public internet search tool.\n"
                + "- Prefer mode=fetch when a mirrored official URL is known.\n"
                + "- Prefer focused documentation domains and small max_results.\n"
                + "- Do not use documentation search results as canvas-state evidence.";
        }

        private static string Compact(string value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string s = string.Join(" ", value.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            return s.Length <= maxChars ? s : s.Substring(0, Math.Max(0, maxChars - 3)) + "...";
        }
    }
}
