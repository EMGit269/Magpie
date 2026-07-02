using System.Collections.Generic;

namespace Magpie.Agent
{
    public sealed class ToolDescriptor
    {
        public string Name { get; set; }
        public ToolLifecycle Lifecycle { get; set; }
        public string Description { get; set; }
        public string Capability { get; set; }
        public string CanonicalUseCase { get; set; }
        public string Replacement { get; set; }
        public bool IsReadOnly { get; set; }
        public bool MutatesCanvas { get; set; }
        public bool WritesFiles { get; set; }
        public int TokenCostRank { get; set; }
        public List<WorkflowIntent> IntendedWorkflows { get; private set; }

        public ToolDescriptor()
        {
            Name = "";
            Description = "";
            Capability = "";
            CanonicalUseCase = "";
            Replacement = "";
            Lifecycle = ToolLifecycle.Active;
            IntendedWorkflows = new List<WorkflowIntent>();
        }
    }
}
