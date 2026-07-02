using Newtonsoft.Json.Linq;

namespace Magpie.HostBridge
{
    internal sealed class HostToolSpec
    {
        internal string Name { get; set; }
        internal bool ReadOnly { get; set; }
        internal string Description { get; set; }
        internal JArray InputSchema { get; set; }
        internal bool ReturnsStructuredResult { get; set; }
    }
}
