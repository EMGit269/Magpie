using System;
using System.Collections.Generic;

namespace Magpie.Agent
{
    public sealed class AgentTurnContext
    {
        public string UserText { get; set; }
        public string LayoutMode { get; set; }
        public string AgentMode { get; set; }
        public bool HasAttachments { get; set; }
        public bool HasImageAttachments { get; set; }
        public bool CanvasAvailable { get; set; }
        public bool CanvasLikelyEmpty { get; set; }
        public string LastToolName { get; set; }
        public bool LastToolFailed { get; set; }
        public List<string> RecentlyLoadedSkills { get; private set; }
        public List<string> RecentReferenceFiles { get; private set; }

        public AgentTurnContext()
        {
            UserText = "";
            LayoutMode = "";
            AgentMode = "";
            LastToolName = "";
            RecentlyLoadedSkills = new List<string>();
            RecentReferenceFiles = new List<string>();
        }
    }
}
