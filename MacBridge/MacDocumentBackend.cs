using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Magpie.Host;
using Magpie.HostBridge;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Magpie.MacBridge
{
    // Every entry point is called on Rhino's UI thread by Bridge.OnUiThread.
    internal sealed class MacDocumentBackend : IGrasshopperHostBridgeBackend
    {
        internal static readonly string[] Tools = {
            "search_component_catalog", "get_canvas_summary", "query_components", "get_component_context", "check_gh_errors",
            "recompute_canvas", "connect_components", "remove_component", "set_component_value", "create_component_graph"
        };

        private static GH_Document Document => Instances.ActiveCanvas?.Document
            ?? throw new InvalidOperationException("Open a Grasshopper document before invoking canvas tools.");
        private static string Json(object value) => JsonConvert.SerializeObject(value);
        public string ResolveToolObjectId(string id) => GrasshopperDocumentHost.ResolveToolObjectId(id);
        private static IGH_DocumentObject Find(GH_Document doc, string id) =>
            GrasshopperDocumentHost.FindDocumentObjectByAnyId(doc, id)
            ?? throw new InvalidOperationException("Component not found: " + id);

        private static IList<IGH_Param> Ports(IGH_DocumentObject obj, bool output)
        {
            if (obj is IGH_Component component) return output ? component.Params.Output : component.Params.Input;
            if (obj is IGH_Param parameter) return new[] { parameter };
            return Array.Empty<IGH_Param>();
        }

        private static IGH_Param Port(IGH_DocumentObject obj, bool output, int index, string label = null)
        {
            var ports = Ports(obj, output);
            if (!string.IsNullOrWhiteSpace(label))
            {
                var matches = ports.Where(p => string.Equals(p.Name, label, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.NickName, label, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1) throw new InvalidOperationException("Port name is missing or ambiguous: " + label);
                return matches[0];
            }
            if (index < 0 || index >= ports.Count) throw new InvalidOperationException("Port index out of range: " + index);
            return ports[index];
        }

        private static IGH_DocumentObject Owner(IGH_Param parameter) => parameter.Attributes?.GetTopLevel.DocObject ?? parameter;
        private static bool IsScript(IGH_DocumentObject obj) => obj.GetType().FullName.IndexOf("script", StringComparison.OrdinalIgnoreCase) >= 0;
        private static bool HasErrors(IGH_DocumentObject obj) => obj is IGH_ActiveObject active
            && (active.RuntimeMessages(GH_RuntimeMessageLevel.Error).Count > 0 || active.RuntimeMessages(GH_RuntimeMessageLevel.Warning).Count > 0);
        private static bool HasConnections(IGH_DocumentObject obj) => Ports(obj, false).Any(p => p.SourceCount > 0)
            || Ports(obj, true).Any(p => p.Recipients.Count > 0);
        private static JArray Diagnostics(GH_Document doc)
        {
            var result = new JArray();
            foreach (var obj in doc.Objects.OfType<IGH_ActiveObject>())
                foreach (var level in new[] { GH_RuntimeMessageLevel.Error, GH_RuntimeMessageLevel.Warning })
                    foreach (var message in obj.RuntimeMessages(level))
                        result.Add(new JObject { ["id"] = obj.InstanceGuid.ToString(), ["name"] = obj.Name, ["level"] = level.ToString(), ["message"] = message });
            return result;
        }

        private static JObject DataPreview(Grasshopper.Kernel.Types.IGH_Goo goo)
        {
            if (goo == null) return new JObject { ["is_valid"] = false };
            var result = new JObject { ["type"] = goo.TypeName, ["is_valid"] = goo.IsValid };
            var value = goo.ScriptVariable();
            if (value is Rhino.Geometry.Circle circle)
            {
                result["shape"] = "circle";
                result["radius"] = circle.Radius;
                result["center"] = new JArray(circle.Center.X, circle.Center.Y, circle.Center.Z);
            }
            else if ((value as Rhino.Geometry.Surface ?? (value is Rhino.Geometry.Brep brep && brep.Surfaces.Count == 1 ? brep.Surfaces[0] : null))
                is Rhino.Geometry.Surface surface && surface.TryGetSphere(out var sphere))
            {
                result["shape"] = "sphere";
                result["radius"] = sphere.Radius;
                result["center"] = new JArray(sphere.Center.X, sphere.Center.Y, sphere.Center.Z);
            }
            else
            {
                var text = goo.ToString() ?? "";
                result["value"] = text.Length > 200 ? text.Substring(0, 200) : text;
            }
            return result;
        }

        private static JObject Describe(GH_Document doc, IGH_DocumentObject obj)
        {
            var connections = new JArray();
            var inputs = Ports(obj, false);
            for (var i = 0; i < inputs.Count; i++)
                foreach (var source in inputs[i].Sources)
                    connections.Add(new JObject { ["from_id"] = Owner(source).InstanceGuid.ToString(), ["from_port"] = source.Name, ["to_index"] = i });
            return new JObject
            {
                ["id"] = GrasshopperDocumentHost.GetPublicId(doc, obj), ["guid"] = obj.InstanceGuid.ToString(),
                ["component_guid"] = obj.ComponentGuid.ToString(), ["name"] = obj.Name, ["nickname"] = obj.NickName,
                ["category"] = obj.Category, ["type"] = obj.GetType().FullName, ["is_script"] = IsScript(obj),
                ["x"] = obj.Attributes?.Pivot.X ?? 0, ["y"] = obj.Attributes?.Pivot.Y ?? 0,
                ["inputs"] = new JArray(inputs.Select((p, i) => new JObject { ["index"] = i, ["name"] = p.Name, ["nickname"] = p.NickName, ["source_count"] = p.SourceCount })),
                ["outputs"] = new JArray(Ports(obj, true).Select((p, i) => new JObject { ["index"] = i, ["name"] = p.Name, ["nickname"] = p.NickName, ["recipient_count"] = p.Recipients.Count,
                    ["data_count"] = p.VolatileDataCount, ["preview"] = new JArray(p.VolatileData.AllData(true).Take(5).Select(DataPreview)) })),
                ["connections"] = connections, ["has_errors"] = HasErrors(obj)
            };
        }

        public string ExecuteGetCanvasSummary()
        {
            var doc = Document;
            return Json(new JObject
            {
                ["rhino_units"] = GrasshopperDocumentHost.BuildRhinoUnitsJson(), ["canvas_errors"] = Diagnostics(doc),
                ["components"] = new JArray(doc.Objects.Where(o => !(o is GH_Group)).Select(o => Describe(doc, o))),
                ["groups"] = new JArray(doc.Objects.OfType<GH_Group>().Select(g => new JObject { ["id"] = g.InstanceGuid.ToString(), ["name"] = g.NickName }))
            });
        }

        private static List<IGH_DocumentObject> Expand(GH_Document doc, IEnumerable<IGH_DocumentObject> seeds, int depth)
        {
            var found = seeds.ToDictionary(o => o.InstanceGuid);
            var frontier = found.Values.ToList();
            for (var i = 0; i < depth; i++)
            {
                var next = new List<IGH_DocumentObject>();
                foreach (var obj in frontier)
                    foreach (var neighbor in Ports(obj, false).SelectMany(p => p.Sources).Concat(Ports(obj, true).SelectMany(p => p.Recipients)).Select(Owner))
                        if (doc.FindObject(neighbor.InstanceGuid, false) != null && !found.ContainsKey(neighbor.InstanceGuid))
                        { found.Add(neighbor.InstanceGuid, neighbor); next.Add(neighbor); }
                frontier = next;
            }
            return found.Values.ToList();
        }

        public string ExecuteQueryComponents(string id, string nameContains, bool? hasErrors, bool? isScript, bool? hasConnections, string portNameContains, int maxResults, int neighborDepth)
        {
            var doc = Document;
            var matches = doc.Objects.Where(o => !(o is GH_Group))
                .Where(o => string.IsNullOrWhiteSpace(id) || GrasshopperDocumentHost.ObjectMatchesAnyId(doc, o, id))
                .Where(o => string.IsNullOrWhiteSpace(nameContains) || (o.Name + " " + o.NickName).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(o => !hasErrors.HasValue || HasErrors(o) == hasErrors.Value)
                .Where(o => !isScript.HasValue || IsScript(o) == isScript.Value)
                .Where(o => !hasConnections.HasValue || HasConnections(o) == hasConnections.Value)
                .Where(o => string.IsNullOrWhiteSpace(portNameContains) || Ports(o, false).Concat(Ports(o, true)).Any(p => (p.Name + " " + p.NickName).IndexOf(portNameContains, StringComparison.OrdinalIgnoreCase) >= 0))
                .Take(maxResults).ToList();
            return Json(new { count = matches.Count, hits = Expand(doc, matches, neighborDepth).Select(o => Describe(doc, o)) });
        }

        public string ExecuteGetComponentContext(string id, int depth, bool includeScriptBodies)
        {
            if (includeScriptBodies) throw new NotSupportedException("Script source inspection is not available on the Mac bridge yet.");
            var doc = Document;
            return Json(new { context_components = Expand(doc, new[] { Find(doc, id) }, depth).Select(o => Describe(doc, o)) });
        }
        public string ExecuteCheckGhErrors()
        {
            var diagnostics = Diagnostics(Document);
            return diagnostics.Count == 0 ? "一切正常。" : Json(diagnostics);
        }
        private static void Changed(GH_Document doc)
        {
            GrasshopperDocumentHost.CanvasChanged = true;
            doc.NewSolution(false);
            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
        }
        public string ExecuteRecomputeCanvas() { var doc = Document; Changed(doc); return Json(new { recomputed = true }); }

        public string ExecuteConnectComponents(string fromId, int fromIndex, string toId, int toIndex, string fromPortLabel, string toPortLabel)
        {
            var doc = Document;
            var source = Port(Find(doc, fromId), true, fromIndex, fromPortLabel);
            var target = Port(Find(doc, toId), false, toIndex, toPortLabel);
            if (Owner(source).InstanceGuid == Owner(target).InstanceGuid) throw new InvalidOperationException("Cannot connect a component to itself.");
            if (!target.Sources.Contains(source))
            {
                doc.UndoUtil.RecordWireEvent("Magpie connect components", target);
                target.AddSource(source);
                target.ExpireSolution(false);
                Changed(doc);
            }
            return Json(new { connected = true, from_id = fromId, to_id = toId });
        }
        public string ExecuteRemoveComponent(string id)
        {
            var doc = Document;
            var obj = Find(doc, id);
            doc.UndoUtil.RecordRemoveObjectEvent("Magpie remove component", obj);
            if (!doc.RemoveObject(obj, false)) throw new InvalidOperationException("Grasshopper did not remove the component.");
            Changed(doc);
            return Json(new { removed = true, guid = obj.InstanceGuid });
        }

        private static Action ValueChange(IGH_DocumentObject obj, string value, double? min = null, double? max = null, int? decimals = null)
        {
            if (obj is GH_NumberSlider slider)
            {
                var number = decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                var lower = min.HasValue ? (decimal)min.Value : slider.Slider.Minimum;
                var upper = max.HasValue ? (decimal)max.Value : slider.Slider.Maximum;
                if (lower > upper || number < lower || number > upper) throw new InvalidOperationException("Slider value must be within its minimum and maximum.");
                if (decimals.HasValue && (decimals < 0 || decimals > 10)) throw new InvalidOperationException("Slider decimal places must be between 0 and 10.");
                return () => { slider.Slider.Minimum = lower; slider.Slider.Maximum = upper; if (decimals.HasValue) slider.Slider.DecimalPlaces = decimals.Value; slider.SetSliderValue(number); };
            }
            if (min.HasValue || max.HasValue || decimals.HasValue) throw new InvalidOperationException("Range and decimals only apply to Number Slider.");
            if (obj is GH_Panel panel) return () => panel.UserText = value;
            if (obj is GH_BooleanToggle toggle) { var boolean = bool.Parse(value); return () => toggle.Value = boolean; }
            throw new NotSupportedException("Value changes support Number Slider, Panel and Boolean Toggle only.");
        }
        public string ExecuteSetComponentValue(string id, string value, double? min, double? max, int? decimals, string property, string graphMapperType)
        {
            if ((!string.IsNullOrWhiteSpace(property) && !property.Equals("value", StringComparison.OrdinalIgnoreCase)) || !string.IsNullOrWhiteSpace(graphMapperType))
                throw new NotSupportedException("The Mac bridge supports the value property only; Graph Mapper configuration is not supported yet.");
            if (value == null) throw new InvalidOperationException("A value is required.");
            var doc = Document;
            var obj = Find(doc, id);
            var apply = ValueChange(obj, value, min, max, decimals);
            obj.RecordUndoEvent("Magpie set component value");
            apply();
            if (obj is IGH_ActiveObject active) active.ExpireSolution(false);
            Changed(doc);
            return Json(new { updated = true, guid = obj.InstanceGuid, value });
        }

        public string SearchComponentCatalog(string query, int maxResults)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("Provide a component name in query.");
            if (maxResults < 1 || maxResults > 20) throw new InvalidOperationException("max_results must be between 1 and 20.");
            var search = query.Trim();
            // Use installed names, including common construction terminology.
            if (search.Equals("Construct Vector", StringComparison.OrdinalIgnoreCase)) search = "Vector XYZ";
            var boundary = new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(search), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var available = Instances.ComponentServer.ObjectProxies.Where(p => !p.Obsolete).ToList();
            var matches = available.Where(p => boundary.IsMatch(p.Desc.Name + " " + p.Desc.NickName)).ToList();
            if (matches.Count == 0)
                matches = available.Where(p => boundary.IsMatch(p.Desc.Description ?? "")).ToList();
            matches = matches.OrderBy(p => p.Desc.Name.Equals(search, StringComparison.OrdinalIgnoreCase) ? 0
                    : p.Desc.NickName.Equals(search, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(p => p.Desc.Category).ThenBy(p => p.Desc.Name).ToList();
            var items = new JArray();
            foreach (var proxy in matches.Take(maxResults))
            {
                var obj = proxy.CreateInstance();
                if (obj == null) continue;
                var component = obj as IGH_Component;
                items.Add(new JObject { ["name"] = proxy.Desc.Name, ["nickname"] = proxy.Desc.NickName,
                    ["component_guid"] = proxy.Guid.ToString(), ["category"] = proxy.Desc.Category,
                    ["subcategory"] = proxy.Desc.SubCategory, ["description"] = proxy.Desc.Description, ["is_component"] = component != null,
                    ["inputs"] = component == null ? new JArray() : new JArray(component.Params.Input.Select((p, i) =>
                        new JObject { ["index"] = i, ["name"] = p.Name, ["nickname"] = p.NickName,
                            ["type"] = p.TypeName, ["description"] = p.Description, ["access"] = p.Access.ToString(), ["optional"] = p.Optional })),
                    ["outputs"] = new JArray(Ports(obj, true).Select((p, i) =>
                        new JObject { ["index"] = i, ["name"] = p.Name, ["nickname"] = p.NickName })) });
            }
            return Json(new JObject { ["query"] = query, ["resolved_query"] = search, ["total"] = matches.Count, ["matches"] = items });
        }

        private static IGH_DocumentObject Instantiate(JObject spec)
        {
            var guidText = spec["component_guid"]?.ToString();
            IGH_DocumentObject obj;
            if (!string.IsNullOrWhiteSpace(guidText)) obj = Instances.ComponentServer.EmitObject(Guid.Parse(guidText));
            else
            {
                var name = spec["name"]?.ToString() ?? "";
                if (name.Equals("Number Slider", StringComparison.OrdinalIgnoreCase)) return new GH_NumberSlider();
                if (name.Equals("Panel", StringComparison.OrdinalIgnoreCase)) return new GH_Panel();
                if (name.Equals("Boolean Toggle", StringComparison.OrdinalIgnoreCase)) return new GH_BooleanToggle();
                var available = Instances.ComponentServer.ObjectProxies.Where(p => !p.Obsolete).ToList();
                var matches = available.Where(p => p.Desc.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0) matches = available.Where(p => p.Desc.NickName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count > 1)
                {
                    // Prefer the actual construction component over a geometry parameter with the same name.
                    var constructors = matches.Where(p => p.CreateInstance() is IGH_Component).ToList();
                    if (constructors.Count == 1) matches = constructors;
                }
                if (matches.Count != 1)
                    throw new InvalidOperationException("Component name is missing or ambiguous: " + name
                        + ". Call search_component_catalog and use an exact component_guid. Matches: "
                        + string.Join("; ", matches.Take(12).Select(p => p.Desc.Name + " [" + p.Desc.Category + "] " + p.Guid)));
                obj = matches[0].CreateInstance();
            }
            return obj ?? throw new InvalidOperationException("Component type could not be instantiated.");
        }

        public string ExecuteCreateComponentGraph(JArray components, JArray connections, string groupName, string csharpFirstHelperReason = null, string csharpFirstHelperReasonDetail = null)
        {
            var doc = Document;
            if (components == null || components.Count == 0 || components.Count > 100) throw new InvalidOperationException("Provide between 1 and 100 components.");
            var pending = new List<IGH_DocumentObject>();
            var aliases = new Dictionary<string, IGH_DocumentObject>(StringComparer.Ordinal);
            foreach (JObject spec in components)
            {
                var obj = Instantiate(spec);
                obj.CreateAttributes();
                obj.Attributes.Pivot = new PointF(spec["x"]?.Value<float>() ?? 0, spec["y"]?.Value<float>() ?? 0);
                var label = spec["label"]?.ToString();
                if (!string.IsNullOrEmpty(label)) obj.NickName = label;
                var alias = spec["alias"]?.ToString() ?? spec["alias_id"]?.ToString() ?? label ?? pending.Count.ToString(CultureInfo.InvariantCulture);
                if (!aliases.TryAdd(alias, obj)) throw new InvalidOperationException("Duplicate component alias: " + alias);
                if (spec["value"] != null)
                {
                    var lower = spec["min"]?.Value<double?>(); var upper = spec["max"]?.Value<double?>();
                    if (obj is GH_NumberSlider slider)
                    {
                        var initial = double.Parse(spec["value"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
                        lower ??= Math.Min((double)slider.Slider.Minimum, initial);
                        upper ??= Math.Max((double)slider.Slider.Maximum, initial);
                    }
                    ValueChange(obj, spec["value"].ToString(), lower, upper, spec["decimals"]?.Value<int?>())();
                }
                pending.Add(obj);
            }
            IGH_DocumentObject Resolve(JObject wire, string side)
            {
                var alias = wire[side + "_alias"]?.ToString();
                if (alias != null) return aliases.TryGetValue(alias, out var obj) ? obj : throw new InvalidOperationException("Unknown component alias: " + alias
                    + ". Aliases are local to this create_component_graph call. Use " + side + "_id for an existing instance; aliases in this batch: " + string.Join(", ", aliases.Keys));
                return Find(doc, wire[side + "_id"]?.ToString());
            }
            var wires = new List<(IGH_Param Source, IGH_Param Target)>();
            foreach (var entry in connections ?? new JArray())
            {
                var wire = (JObject)entry;
                var from = Resolve(wire, "from"); var to = Resolve(wire, "to");
                if (from.InstanceGuid == to.InstanceGuid) throw new InvalidOperationException("Cannot connect a component to itself.");
                if (!pending.Contains(to)) throw new InvalidOperationException("Graph connections must target a new component. Use connect_components to wire an existing target with undo support.");
                wires.Add((Port(from, true, wire["from_index"]?.Value<int>() ?? 0), Port(to, false, wire["to_index"]?.Value<int>() ?? 0)));
            }
            var added = new List<IGH_DocumentObject>();
            var attached = new List<(IGH_Param Source, IGH_Param Target)>();
            try
            {
                foreach (var obj in pending)
                {
                    if (!doc.AddObject(obj, false)) throw new InvalidOperationException("Grasshopper did not add the component.");
                    added.Add(obj);
                }
                if (!string.IsNullOrWhiteSpace(groupName))
                {
                    var group = new GH_Group { NickName = groupName };
                    group.CreateAttributes();
                    foreach (var obj in pending) group.AddObject(obj.InstanceGuid);
                    if (!doc.AddObject(group, false)) throw new InvalidOperationException("Grasshopper did not add the group.");
                    added.Add(group);
                }
                foreach (var wire in wires)
                    if (!wire.Target.Sources.Contains(wire.Source))
                    { wire.Target.AddSource(wire.Source); attached.Add(wire); wire.Target.ExpireSolution(false); }
                Changed(doc);
                doc.UndoUtil.RecordAddObjectEvent("Magpie create component graph", added);
                return Json(new { created_count = pending.Count, components = pending.Select(o => Describe(doc, o)), aliases = aliases.ToDictionary(p => p.Key, p => p.Value.InstanceGuid.ToString()) });
            }
            catch
            {
                foreach (var wire in attached) wire.Target.RemoveSource(wire.Source);
                foreach (var obj in added.AsEnumerable().Reverse()) doc.RemoveObject(obj, false);
                try { Changed(doc); } catch (Exception error) { AddGhLog.Warn("Graph rollback redraw failed: " + error.Message); }
                throw;
            }
        }

        public string ExecuteReadComponentScript(string id) => throw new NotSupportedException("Script source inspection is not available on the Mac bridge yet.");
        public string ExecuteCreateCSharpScript(string aliasId, string label, float x, float y, JArray inputs, JArray outputs, string body, JArray components, JArray connections, string groupName) => throw new NotSupportedException("Script editing is not available on the Mac bridge yet.");
        public string ExecuteEditCSharpScript(string id, string mode, string body) => throw new NotSupportedException("Script editing is not available on the Mac bridge yet.");
    }
}
