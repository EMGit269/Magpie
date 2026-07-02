using System;
using System.Collections.Generic;
using System.Linq;

namespace Magpie.Agent
{
    public sealed class WorkflowSignals
    {
        public bool HasImageAttachments { get; set; }
        public bool HasCSharpSignal { get; set; }
        public bool HasCSharpCodeBlock { get; set; }
        public bool HasCompileError { get; set; }
        public bool HasApiMemberPattern { get; set; }
        public bool HasRhinoCommonSymbol { get; set; }
        public bool HasGrasshopperSymbol { get; set; }
        public bool MentionsSignatureOrOverload { get; set; }
        public bool UserAskedExternalVerification { get; set; }
        public bool UserAskedWebResearch { get; set; }
        public bool UserAskedReferenceImport { get; set; }
        public bool UserAskedReferenceLookup { get; set; }
        public bool UserAskedSkillLookup { get; set; }
        public bool UserAskedCreate { get; set; }
        public bool UserAskedModify { get; set; }
        public bool UserAskedImageGeneration { get; set; }
        public bool UserAskedVisualModeling { get; set; }
        public bool UserAskedGrasshopper { get; set; }

        public double ApiDocLookupScore()
        {
            double score = 0;
            if (HasApiMemberPattern) score += 0.30;
            if (HasRhinoCommonSymbol) score += 0.24;
            if (HasGrasshopperSymbol) score += 0.18;
            if (HasCompileError) score += 0.22;
            if (MentionsSignatureOrOverload) score += 0.16;
            if (HasCSharpSignal || HasCSharpCodeBlock) score += 0.10;
            if (UserAskedExternalVerification) score += 0.12;
            return Math.Min(1.0, score);
        }

        public string ExplainApiDocSignals()
        {
            var parts = new List<string>();
            Add(parts, HasApiMemberPattern, "api-member-pattern");
            Add(parts, HasRhinoCommonSymbol, "rhinocommon-symbol");
            Add(parts, HasGrasshopperSymbol, "grasshopper-symbol");
            Add(parts, HasCompileError, "compile-error");
            Add(parts, MentionsSignatureOrOverload, "signature-or-overload");
            Add(parts, HasCSharpSignal || HasCSharpCodeBlock, "csharp-context");
            Add(parts, UserAskedExternalVerification, "verification-request");
            return parts.Count == 0 ? "no-api-doc-signals" : string.Join(", ", parts.Distinct());
        }

        private static void Add(List<string> parts, bool condition, string name)
        {
            if (condition) parts.Add(name);
        }
    }
}
