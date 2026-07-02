using System;
using System.Collections.Generic;
using System.Linq;

namespace Magpie.Agent
{
    public sealed class ToolSurfacePolicy
    {
        private readonly ToolRegistry _registry;

        public ToolSurfacePolicy(ToolRegistry registry)
        {
            _registry = registry ?? new ToolRegistry();
        }

        public object[] FilterForRoute(IEnumerable<object> toolDefinitions, WorkflowRoute route, Func<object, string> getToolName)
        {
            if (toolDefinitions == null) return null;
            if (route == null) return toolDefinitions.ToArray();
            if (getToolName == null) return toolDefinitions.ToArray();

            var routeTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in route.RequiredTools ?? new List<string>()) routeTools.Add(name);
            foreach (var name in route.OptionalTools ?? new List<string>()) routeTools.Add(name);

            return toolDefinitions
                .Where(tool =>
                {
                    string name = getToolName(tool);
                    if (string.IsNullOrWhiteSpace(name)) return true;

                    var descriptor = _registry.Find(name);
                    if (descriptor == null)
                    {
                        // Unknown tools stay visible until the registry is complete.
                        return true;
                    }

                    if (descriptor.Lifecycle == ToolLifecycle.Removed
                        || descriptor.Lifecycle == ToolLifecycle.HiddenCompatibility
                        || descriptor.Lifecycle == ToolLifecycle.Deprecated)
                        return false;

                    if (routeTools.Contains(name)) return true;

                    if (descriptor.Lifecycle == ToolLifecycle.Deferred)
                        return false;

                    if (descriptor.IntendedWorkflows == null || descriptor.IntendedWorkflows.Count == 0)
                        return true;

                    return descriptor.IntendedWorkflows.Contains(route.Intent);
                })
                .ToArray();
        }
    }
}
