using System;
using Newtonsoft.Json.Linq;

namespace Magpie.HostBridge
{
    internal sealed class GrasshopperHostToolExecutor
    {
        private readonly IGrasshopperHostBridgeBackend _backend;

        public GrasshopperHostToolExecutor(IGrasshopperHostBridgeBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public JObject BuildManifestPayload()
        {
            return new JObject
            {
                ["protocol"] = "addgh-host-bridge-v1",
                ["generated_at_utc"] = DateTime.UtcNow.ToString("o"),
                ["tools"] = BuildHostToolManifest()
            };
        }

        public JObject ExecuteRequest(JObject request)
        {
            string requestId = request?["request_id"]?.ToString();
            string tool = request?["tool"]?.ToString();
            JObject args = request?["args"] as JObject ?? new JObject();

            try
            {
                if (string.IsNullOrWhiteSpace(tool))
                    return BuildError(requestId, tool, "Missing tool name.");

                if (IsFirstWaveFormalTool(tool))
                    HostBridgeValidation.ValidateFirstWaveToolArgs(tool, args);

                string result = ExecuteTool(tool, args);
                return BuildSuccess(requestId, tool, result);
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Host bridge tool failed: " + (tool ?? "?") + " | " + ex.Message);
                return BuildError(requestId, tool, ex.Message);
            }
        }

        private JArray BuildHostToolManifest()
        {
            return new JArray
            {
                BuildHostToolDescriptor("get_canvas_summary", true, "Return current Grasshopper canvas summary and runtime state."),
                BuildHostToolDescriptor("query_components", true, "Query focused Grasshopper components by filters."),
                BuildHostToolDescriptor("get_component_context", true, "Return focused context around one component."),
                BuildHostToolDescriptor("read_component_script", true, "Read source/body from an existing script component."),
                BuildHostToolDescriptor("check_gh_errors", true, "Return current Grasshopper runtime errors and warnings."),
                BuildHostToolDescriptor("recompute_canvas", false, "Recompute the active Grasshopper document."),
                BuildHostToolDescriptor("connect_components", false, "Connect two existing Grasshopper component ports."),
                BuildHostToolDescriptor("remove_component", false, "Remove one existing Grasshopper component."),
                BuildHostToolDescriptor("set_component_value", false, "Set value/configuration on an existing helper component."),
                BuildHostToolDescriptor("create_component_graph", false, "Create a batch of Grasshopper components and connections."),
                BuildHostToolDescriptor("create_csharp_script", false, "Create a C# Script component with ports/body/helpers."),
                BuildHostToolDescriptor("edit_csharp_script", false, "Edit an existing C# Script component body.")
            };
        }

        private JObject BuildHostToolDescriptor(string name, bool readOnly, string description)
        {
            var spec = GetFormalHostToolSpec(name, readOnly, description);
            return new JObject
            {
                ["name"] = spec.Name,
                ["read_only"] = spec.ReadOnly,
                ["description"] = spec.Description,
                ["input_schema"] = spec.InputSchema ?? new JArray(),
                ["returns_structured_result"] = spec.ReturnsStructuredResult
            };
        }

        private HostToolSpec GetFormalHostToolSpec(string name, bool readOnly, string description)
        {
            var spec = new HostToolSpec
            {
                Name = name,
                ReadOnly = readOnly,
                Description = description,
                InputSchema = new JArray(),
                ReturnsStructuredResult = true
            };

            switch (name)
            {
                case "get_canvas_summary":
                case "check_gh_errors":
                    break;

                case "query_components":
                    spec.InputSchema = new JArray
                    {
                        Field("id", "string", false, "Optional exact public id or GUID."),
                        Field("name_contains", "string", false, "Optional substring filter."),
                        Field("has_errors", "boolean", false, "Optional runtime error filter."),
                        Field("is_script", "boolean", false, "Optional script component filter."),
                        Field("has_connections", "boolean", false, "Optional connection filter."),
                        Field("port_name_contains", "string", false, "Optional port name substring filter."),
                        Field("max_results", "integer", false, "Maximum matches to return."),
                        Field("neighbor_depth", "integer", false, "Neighbor traversal depth.")
                    };
                    break;

                case "get_component_context":
                    spec.InputSchema = new JArray
                    {
                        Field("id", "string", true, "Target public id or GUID."),
                        Field("depth", "integer", false, "Neighbor traversal depth."),
                        Field("include_script_bodies", "boolean", false, "Whether to include script bodies.")
                    };
                    break;

                case "create_component_graph":
                    spec.InputSchema = new JArray
                    {
                        Field("components", "array", true, "Batch components to create."),
                        Field("connections", "array", false, "Batch connections to create."),
                        Field("group_name", "string", false, "Optional group name.")
                    };
                    break;
            }

            return spec;
        }

        private static JObject Field(string name, string type, bool required, string description)
        {
            return new JObject
            {
                ["name"] = name,
                ["type"] = type,
                ["required"] = required,
                ["description"] = description
            };
        }

        private string ExecuteTool(string tool, JObject args)
        {
            switch ((tool ?? "").Trim())
            {
                case "get_canvas_summary":
                    return _backend.ExecuteGetCanvasSummary();
                case "query_components":
                    return _backend.ExecuteQueryComponents(
                        args["id"]?.ToString(),
                        args["name_contains"]?.ToString(),
                        args["has_errors"]?.Value<bool?>(),
                        args["is_script"]?.Value<bool?>(),
                        args["has_connections"]?.Value<bool?>(),
                        args["port_name_contains"]?.ToString(),
                        args["max_results"]?.Value<int?>() ?? 20,
                        args["neighbor_depth"]?.Value<int?>() ?? 0);
                case "get_component_context":
                    return _backend.ExecuteGetComponentContext(
                        _backend.ResolveToolObjectId(args["id"]?.ToString()),
                        args["depth"]?.Value<int?>() ?? 1,
                        args["include_script_bodies"]?.Value<bool?>() ?? false);
                case "read_component_script":
                    return _backend.ExecuteReadComponentScript(_backend.ResolveToolObjectId(args["id"]?.ToString()));
                case "check_gh_errors":
                    return _backend.ExecuteCheckGhErrors();
                case "recompute_canvas":
                    return _backend.ExecuteRecomputeCanvas();
                case "connect_components":
                    return _backend.ExecuteConnectComponents(
                        _backend.ResolveToolObjectId(args["from_id"]?.ToString()),
                        args["from_index"]?.Value<int?>() ?? 0,
                        _backend.ResolveToolObjectId(args["to_id"]?.ToString()),
                        args["to_index"]?.Value<int?>() ?? 0,
                        args["from_port_label"]?.ToString(),
                        args["to_port_label"]?.ToString());
                case "remove_component":
                    return _backend.ExecuteRemoveComponent(_backend.ResolveToolObjectId(args["id"]?.ToString()));
                case "set_component_value":
                    return _backend.ExecuteSetComponentValue(
                        _backend.ResolveToolObjectId(args["id"]?.ToString()),
                        args["value"]?.ToString(),
                        args["min"]?.Value<double?>(),
                        args["max"]?.Value<double?>(),
                        args["decimals"]?.Value<int?>(),
                        args["property"]?.ToString(),
                        args["graph_mapper_type"]?.ToString());
                case "create_component_graph":
                    return _backend.ExecuteCreateComponentGraph(
                        args["components"] as JArray ?? new JArray(),
                        args["connections"] as JArray ?? new JArray(),
                        args["group_name"]?.ToString());
                case "create_csharp_script":
                    return _backend.ExecuteCreateCSharpScript(
                        args["alias_id"]?.ToString(),
                        args["name"]?.ToString() ?? args["label"]?.ToString(),
                        args["x"]?.Value<float?>() ?? 0f,
                        args["y"]?.Value<float?>() ?? 0f,
                        args["inputs"] as JArray ?? new JArray(),
                        args["outputs"] as JArray ?? new JArray(),
                        args["body"]?.ToString(),
                        args["components"] as JArray ?? new JArray(),
                        args["connections"] as JArray ?? new JArray(),
                        args["group_name"]?.ToString());
                case "edit_csharp_script":
                    return _backend.ExecuteEditCSharpScript(
                        _backend.ResolveToolObjectId(args["id"]?.ToString()),
                        args["mode"]?.ToString() ?? "set_body",
                        args["body"]?.ToString());
                default:
                    throw new InvalidOperationException("Unsupported host bridge tool: " + tool);
            }
        }

        private JObject BuildSuccess(string requestId, string tool, string rawResult)
        {
            bool firstWaveFormalTool = IsFirstWaveFormalTool(tool);
            if (LooksLikeToolError(rawResult))
                return BuildError(requestId, tool, rawResult);

            JToken resultToken = firstWaveFormalTool
                ? NormalizeFirstWaveResult(tool, rawResult)
                : TryParseHostBridgeResult(rawResult);
            return new JObject
            {
                ["request_id"] = requestId ?? "",
                ["tool"] = tool ?? "",
                ["status"] = "ok",
                ["summary"] = "Tool executed successfully.",
                ["result"] = resultToken,
                ["error"] = null,
                ["structured"] = firstWaveFormalTool,
                ["version"] = firstWaveFormalTool ? "host-tool-v1" : "legacy-host-tool"
            };
        }

        private JObject BuildError(string requestId, string tool, string error)
        {
            return new JObject
            {
                ["request_id"] = requestId ?? "",
                ["tool"] = tool ?? "",
                ["status"] = "error",
                ["summary"] = "Tool execution failed.",
                ["result"] = null,
                ["error"] = error ?? "Unknown error.",
                ["structured"] = IsFirstWaveFormalTool(tool),
                ["version"] = IsFirstWaveFormalTool(tool) ? "host-tool-v1" : "legacy-host-tool"
            };
        }

        private static bool IsFirstWaveFormalTool(string tool)
        {
            switch ((tool ?? "").Trim())
            {
                case "get_canvas_summary":
                case "query_components":
                case "get_component_context":
                case "create_component_graph":
                case "check_gh_errors":
                    return true;
                default:
                    return false;
            }
        }

        private static bool LooksLikeToolError(string rawResult)
        {
            return !string.IsNullOrWhiteSpace(rawResult)
                && rawResult.TrimStart().StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
        }

        private static JToken NormalizeFirstWaveResult(string tool, string rawResult)
        {
            switch ((tool ?? "").Trim())
            {
                case "get_canvas_summary":
                    return NormalizeCanvasSummaryResult(rawResult);
                case "query_components":
                    return NormalizeQueryComponentsResult(rawResult);
                case "get_component_context":
                    return NormalizeComponentContextResult(rawResult);
                case "create_component_graph":
                    return NormalizeCreateComponentGraphResult(rawResult);
                case "check_gh_errors":
                    return NormalizeCheckErrorsResult(rawResult);
                default:
                    return TryParseHostBridgeResult(rawResult);
            }
        }

        private static JToken NormalizeCanvasSummaryResult(string rawResult)
        {
            var parsed = TryParseHostBridgeResult(rawResult) as JObject;
            if (parsed == null)
            {
                return new JObject
                {
                    ["kind"] = "canvas_summary",
                    ["raw_text"] = rawResult ?? ""
                };
            }

            var result = new JObject
            {
                ["kind"] = "canvas_summary",
                ["rhino_units"] = parsed["rhino_units"],
                ["canvas_errors"] = parsed["canvas_errors"] ?? new JArray(),
                ["components"] = parsed["components"] ?? new JArray(),
                ["groups"] = parsed["groups"] ?? new JArray()
            };
            if (parsed["timestamp"] != null)
                result["timestamp"] = parsed["timestamp"];
            result["component_count"] = ((JArray)result["components"]).Count;
            result["group_count"] = ((JArray)result["groups"]).Count;
            result["error_count"] = ((JArray)result["canvas_errors"]).Count;
            return result;
        }

        private static JToken NormalizeQueryComponentsResult(string rawResult)
        {
            var parsed = TryParseHostBridgeResult(rawResult) as JObject;
            if (parsed == null)
            {
                return new JObject
                {
                    ["kind"] = "query_components_result",
                    ["raw_text"] = rawResult ?? ""
                };
            }

            var hits = parsed["hits"] as JArray ?? parsed["items"] as JArray ?? new JArray();
            return new JObject
            {
                ["kind"] = "query_components_result",
                ["match_count"] = parsed["count"]?.Value<int?>() ?? hits.Count,
                ["hits"] = hits
            };
        }

        private static JToken NormalizeComponentContextResult(string rawResult)
        {
            var parsed = TryParseHostBridgeResult(rawResult) as JObject;
            if (parsed == null)
            {
                return new JObject
                {
                    ["kind"] = "component_context_result",
                    ["raw_text"] = rawResult ?? ""
                };
            }

            var components = parsed["context_components"] as JArray ?? new JArray();
            return new JObject
            {
                ["kind"] = "component_context_result",
                ["context_count"] = components.Count,
                ["context_components"] = components
            };
        }

        private static JToken NormalizeCreateComponentGraphResult(string rawResult)
        {
            var parsed = TryParseHostBridgeResult(rawResult);
            if (parsed is JObject obj)
            {
                obj["kind"] = "create_component_graph_result";
                return obj;
            }

            return new JObject
            {
                ["kind"] = "create_component_graph_result",
                ["message"] = rawResult ?? ""
            };
        }

        private static JToken NormalizeCheckErrorsResult(string rawResult)
        {
            string text = (rawResult ?? "").Trim();
            bool clean = string.IsNullOrWhiteSpace(text)
                || string.Equals(text, "一切正常。", StringComparison.Ordinal)
                || string.Equals(text, "一切正常", StringComparison.Ordinal);

            return new JObject
            {
                ["kind"] = "check_gh_errors_result",
                ["is_clean"] = clean,
                ["message"] = string.IsNullOrWhiteSpace(text) ? "一切正常。" : text
            };
        }

        private static JToken TryParseHostBridgeResult(string rawResult)
        {
            if (string.IsNullOrWhiteSpace(rawResult))
                return JValue.CreateString(string.Empty);

            string text = rawResult.Trim();
            if ((text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal))
                || (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal)))
            {
                try
                {
                    return JToken.Parse(text);
                }
                catch
                {
                }
            }

            return JValue.CreateString(rawResult);
        }
    }
}
