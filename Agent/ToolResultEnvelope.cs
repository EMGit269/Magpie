using System;

namespace Magpie.Agent
{
    public sealed class ToolResultEnvelope
    {
        public string ToolName { get; set; }
        public bool Success { get; set; }
        public string Summary { get; set; }
        public string ArtifactPath { get; set; }
        public string ResultKind { get; set; }
        public int RawCharCount { get; set; }
        public DateTime TimestampUtc { get; set; }

        public static ToolResultEnvelope Empty(string toolName)
        {
            return new ToolResultEnvelope
            {
                ToolName = toolName ?? "",
                Success = false,
                Summary = "",
                ArtifactPath = "",
                ResultKind = "empty",
                RawCharCount = 0,
                TimestampUtc = DateTime.UtcNow
            };
        }
    }
}
