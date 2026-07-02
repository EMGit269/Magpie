using System.Collections.Generic;

namespace Magpie.Agent
{
    public sealed class SkillIndex
    {
        public int Version { get; set; }
        public List<SkillIndexEntry> Skills { get; set; }

        public SkillIndex()
        {
            Version = 1;
            Skills = new List<SkillIndexEntry>();
        }
    }

    public sealed class SkillIndexEntry
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public string FileName { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public List<string> Tags { get; set; }
        public List<WorkflowIntent> Workflows { get; set; }
        public string Quality { get; set; }
        public bool Verified { get; set; }
        public string LastVerifiedAt { get; set; }
        public int TokenEstimate { get; set; }

        public SkillIndexEntry()
        {
            Id = "";
            Path = "";
            FileName = "";
            Title = "";
            Description = "";
            Tags = new List<string>();
            Workflows = new List<WorkflowIntent>();
            Quality = "experimental";
            LastVerifiedAt = "";
        }
    }
}
