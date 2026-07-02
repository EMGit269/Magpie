using System.Collections.Generic;
using System.Linq;

namespace Magpie.Agent
{
    public sealed class CompactionPlan
    {
        public List<int> PinnedIndices { get; private set; }
        public List<int> SummarizeIndices { get; private set; }

        public CompactionPlan()
        {
            PinnedIndices = new List<int>();
            SummarizeIndices = new List<int>();
        }

        public string ToLogLine()
        {
            return "pinned=" + PinnedIndices.Count
                + " [" + string.Join(",", PinnedIndices.Take(20)) + "]"
                + ", summarize=" + SummarizeIndices.Count
                + " [" + string.Join(",", SummarizeIndices.Take(20)) + "]";
        }
    }
}
