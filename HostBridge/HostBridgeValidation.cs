using System;
using Newtonsoft.Json.Linq;

namespace Magpie.HostBridge
{
    internal static class HostBridgeValidation
    {
        internal static void ValidateFirstWaveToolArgs(string tool, JObject args)
        {
            switch ((tool ?? "").Trim())
            {
                case "get_canvas_summary":
                case "check_gh_errors":
                    EnsureNoRequiredFields(args);
                    return;

                case "query_components":
                    ValidateQueryComponents(args);
                    return;

                case "get_component_context":
                    ValidateGetComponentContext(args);
                    return;

                case "create_component_graph":
                    ValidateCreateComponentGraph(args);
                    return;
            }
        }

        private static void EnsureNoRequiredFields(JObject args)
        {
            if (args == null) return;
        }

        private static void ValidateQueryComponents(JObject args)
        {
            if (args == null) return;

            EnsureType(args, "id", JTokenType.String);
            EnsureType(args, "name_contains", JTokenType.String);
            EnsureType(args, "has_errors", JTokenType.Boolean);
            EnsureType(args, "is_script", JTokenType.Boolean);
            EnsureType(args, "has_connections", JTokenType.Boolean);
            EnsureType(args, "port_name_contains", JTokenType.String);
            EnsureIntegerRange(args, "max_results", 1, 50);
            EnsureIntegerRange(args, "neighbor_depth", 0, 2);
        }

        private static void ValidateGetComponentContext(JObject args)
        {
            if (args == null)
                throw new InvalidOperationException("Arguments are required.");

            EnsureRequiredString(args, "id");
            EnsureIntegerRange(args, "depth", 0, 4);
            EnsureType(args, "include_script_bodies", JTokenType.Boolean);
        }

        private static void ValidateCreateComponentGraph(JObject args)
        {
            if (args == null)
                throw new InvalidOperationException("Arguments are required.");

            var components = args["components"];
            if (components == null || components.Type != JTokenType.Array)
                throw new InvalidOperationException("Field 'components' is required and must be an array.");

            EnsureType(args, "connections", JTokenType.Array);
            EnsureType(args, "group_name", JTokenType.String);

            foreach (var item in (JArray)components)
            {
                if (!(item is JObject component))
                    throw new InvalidOperationException("Every component entry must be an object.");

                EnsureRequiredString(component, "name", allowEmpty: true, allowMissing: true);
                EnsureRequiredNumber(component, "x");
                EnsureRequiredNumber(component, "y");
                EnsureType(component, "component_guid", JTokenType.String);
                EnsureType(component, "label", JTokenType.String);
                EnsureType(component, "value", JTokenType.String);
            }

            var connections = args["connections"] as JArray;
            if (connections == null) return;

            foreach (var item in connections)
            {
                if (!(item is JObject connection))
                    throw new InvalidOperationException("Every connection entry must be an object.");

                EnsureType(connection, "from_alias", JTokenType.String);
                EnsureType(connection, "to_alias", JTokenType.String);
                EnsureType(connection, "from_id", JTokenType.String);
                EnsureType(connection, "to_id", JTokenType.String);
                EnsureIntegerRange(connection, "from_index", 0, 128);
                EnsureIntegerRange(connection, "to_index", 0, 128);
            }
        }

        private static void EnsureRequiredString(JObject obj, string field, bool allowEmpty = false, bool allowMissing = false)
        {
            var token = obj?[field];
            if (token == null)
            {
                if (allowMissing) return;
                throw new InvalidOperationException("Field '" + field + "' is required.");
            }

            if (token.Type != JTokenType.String)
                throw new InvalidOperationException("Field '" + field + "' must be a string.");

            if (!allowEmpty && string.IsNullOrWhiteSpace(token.ToString()))
                throw new InvalidOperationException("Field '" + field + "' cannot be empty.");
        }

        private static void EnsureRequiredNumber(JObject obj, string field)
        {
            var token = obj?[field];
            if (token == null)
                throw new InvalidOperationException("Field '" + field + "' is required.");

            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw new InvalidOperationException("Field '" + field + "' must be numeric.");
        }

        private static void EnsureType(JObject obj, string field, JTokenType expected)
        {
            var token = obj?[field];
            if (token == null) return;
            if (token.Type != expected)
                throw new InvalidOperationException("Field '" + field + "' must be of type " + expected.ToString().ToLowerInvariant() + ".");
        }

        private static void EnsureIntegerRange(JObject obj, string field, int min, int max)
        {
            var token = obj?[field];
            if (token == null) return;
            if (token.Type != JTokenType.Integer)
                throw new InvalidOperationException("Field '" + field + "' must be an integer.");

            int value = token.Value<int>();
            if (value < min || value > max)
                throw new InvalidOperationException("Field '" + field + "' must be between " + min + " and " + max + ".");
        }
    }
}
