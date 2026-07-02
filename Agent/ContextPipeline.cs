using System;
using System.Collections.Generic;

namespace Magpie.Agent
{
    public sealed class ContextPipelineRequest
    {
        public Func<string> BasePromptProvider { get; set; }
        public Func<string> TypedPromptProvider { get; set; }
        public Func<string> ContextPackProvider { get; set; }
        public Func<string> ContextLedgerProvider { get; set; }
        public Func<string> SkillSummaryProvider { get; set; }
        public bool MergeSkillsIntoBaseSystem { get; set; }
    }

    public sealed class ContextPipeline
    {
        public List<object> BuildInitialSystemMessages(ContextPipelineRequest request)
        {
            var list = new List<object>();
            if (request == null)
            {
                list.Add(new { role = "system", content = "" });
                return list;
            }

            string basePrompt = Safe(request.BasePromptProvider)
                + Safe(request.TypedPromptProvider)
                + Safe(request.ContextPackProvider)
                + Safe(request.ContextLedgerProvider);
            string skills = Safe(request.SkillSummaryProvider);

            if (string.IsNullOrWhiteSpace(skills) || request.MergeSkillsIntoBaseSystem)
            {
                list.Add(new { role = "system", content = basePrompt + skills });
                return list;
            }

            list.Add(new { role = "system", content = basePrompt });
            list.Add(new { role = "system", content = skills.TrimStart() });
            return list;
        }

        private static string Safe(Func<string> provider)
        {
            if (provider == null) return "";
            return provider() ?? "";
        }
    }
}
