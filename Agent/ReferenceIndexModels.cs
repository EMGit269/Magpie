using System.Collections.Generic;

namespace Magpie.Agent
{
    public sealed class ReferenceIndex
    {
        public int Version { get; set; }
        public List<ReferenceIndexEntry> References { get; set; }

        public ReferenceIndex()
        {
            Version = 1;
            References = new List<ReferenceIndexEntry>();
        }
    }

    public sealed class ReferenceIndexEntry
    {
        public string Id { get; set; }
        public string FileName { get; set; }
        public string Description { get; set; }
        public string SkillFileName { get; set; }
        public string ImportHint { get; set; }
        public bool JsonExists { get; set; }
        public bool GhExists { get; set; }
        public int TokenEstimate { get; set; }

        public ReferenceIndexEntry()
        {
            Id = "";
            FileName = "";
            Description = "";
            SkillFileName = "";
            ImportHint = "";
        }
    }
}
